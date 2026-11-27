using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using VPanel.Core.Abstractions;
using VPanel.Core.Configuration;
using VPanel.Core.Domain;
using VPanel.Core.Utilities;
using VPanel.Infrastructure.Data;
using VPanel.Infrastructure.Docker;
using VPanel.Infrastructure.Proxy;
using VPanel.Infrastructure.Seed;
using VPanel.Infrastructure.Services;

namespace VPanel.Infrastructure.Import;

/// <summary>
/// Brings a scanned stack under v-panel management without touching its containers: the compose project name, folder,
/// volumes and networks stay as they are. v-panel writes its files only on the first deploy (after the original files were
/// backed up), and takes over the hand-written Caddy site in that same deploy.
/// </summary>
public sealed class StackImportService(
    StackScanner scanner,
    IDbContextFactory<VPanelDbContext> dbFactory,
    SettingsService settingsService,
    InstanceService instances,
    IReverseProxyProvider proxy,
    DockerCli docker,
    AuditService audit,
    IOptions<VPanelOptions> options,
    TimeProvider time)
{
    public const string BackupFolder = ".vpanel-backup";

    public async Task<Result<ImportOutcome>> ImportAsync(ImportRequest request, string? user, CancellationToken ct = default)
    {
        var scan = await scanner.ScanAsync(ct);
        if (!scan.DockerReachable) return Result<ImportOutcome>.Fail($"Docker is not reachable: {scan.Error}");
        var stack = scan.Find(request.ProjectName);
        if (stack is null)
            return Result<ImportOutcome>.Fail(scan.ManagedProjects.Contains(request.ProjectName)
                ? $"Compose project '{request.ProjectName}' is already managed by v-panel."
                : $"No unmanaged compose project named '{request.ProjectName}' was found.");
        if (stack.WorkingDir is null || stack.ConfigFiles.Count == 0)
            return Result<ImportOutcome>.Fail("The stack's folder and compose files are unknown (no container labels, no compose file found).");
        if (!stack.Accessible)
            return Result<ImportOutcome>.Fail($"{stack.WorkingDir} is not visible to v-panel. Mount {stack.SuggestedRoot ?? stack.WorkingDir} into the v-panel container (installer --adopt) and add it to VPanel:AdoptedRoots.");
        if (!stack.InAllowedRoot)
            return Result<ImportOutcome>.Fail($"{stack.WorkingDir} is outside the apps root and the adopted roots. Add {stack.SuggestedRoot ?? stack.WorkingDir} to VPanel:AdoptedRoots (installer --adopt).");

        var isInfra = stack.Kind == StackKind.Infra;
        var settings = settingsService.Get();
        if (!isInfra && scan.Infra is { SharedNetwork: { } infraNet } && infraNet != settings.SharedNetwork)
            return Result<ImportOutcome>.Fail($"Import the shared infrastructure ('{scan.Infra.ProjectName}') first: it sets the shared network to '{infraNet}'.");

        var notes = new List<string>();
        var compose = await ReadComposeAsync(stack, notes, ct);
        if (compose.Error is not null) return Result<ImportOutcome>.Fail(compose.Error);
        var env = stack.EnvFile is not null && File.Exists(stack.EnvFile) ? StripManaged(await File.ReadAllTextAsync(stack.EnvFile, ct)) : "";

        // Proxy route: defaults from the scan, overridable. A public URL already served by a hand-written site needs the take over.
        var publicUrl = request.PublicUrl ?? (isInfra ? null : stack.PublicUrl);
        publicUrl = string.IsNullOrWhiteSpace(publicUrl) ? null : publicUrl.Trim();
        ManualSiteRef? takeover = null;
        CaddySiteBlock? takenOver = null;
        if (publicUrl is not null)
        {
            if (!UrlHelper.TryParse(publicUrl, out var uri)) return Result<ImportOutcome>.Fail("Public URL is not a valid http(s) URL.");
            var address = CaddyConfigBuilder.SiteAddress(uri, settings.Ingress);
            var clash = scan.ManualSites.FirstOrDefault(s => s.Addresses.Any(a => SameSite(a, uri)));
            if (clash is not null)
            {
                if (!clash.CanTakeOver)
                    return Result<ImportOutcome>.Fail($"{publicUrl} is served by a hand-written Caddy site ({clash.File}:{clash.Line}) that v-panel cannot take over ({clash.Reason}). Leave the public URL empty to keep that site hand-written.");
                if (!request.TakeOverSite)
                    return Result<ImportOutcome>.Fail($"{publicUrl} is already served by {clash.File}:{clash.Line}. Enable 'take over' or leave the public URL empty.");
                takeover = new ManualSiteRef(clash.File, clash.Addresses[0]);
                takenOver = clash;
                notes.Add($"On the first deploy, the Caddy site '{clash.Addresses[0]}' in {clash.File} is replaced by a generated one ({address})" +
                          (clash.Directives is null && clash.ProxyOptions is null ? "." : ", keeping its other directives and reverse_proxy options."));
            }
        }

        var slug = string.IsNullOrWhiteSpace(request.Slug) ? stack.SuggestedSlug : request.Slug.Trim();
        if (!Core.Utilities.Slug.IsValid(slug)) return Result<ImportOutcome>.Fail("Slug may only contain lowercase letters, digits and dashes (max 48).");

        // Backups before anything else is written to the stack's folder.
        string backupDir;
        try { backupDir = BackupOriginals(stack); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result<ImportOutcome>.Fail($"Cannot write a backup into {stack.WorkingDir}: {ex.Message}");
        }
        notes.Add($"Original files copied to {backupDir}.");

        // Infra first: settings, seeded template, Caddy import line.
        if (isInfra && scan.Infra is { } infra)
        {
            await RemoveSeededInfraAsync(notes, ct);
            settings = infra.ApplyTo(settingsService.Get());
            await settingsService.SaveAsync(settings, ct);
            notes.Add($"Settings updated: shared network '{settings.SharedNetwork}', Caddy container '{settings.CaddyContainer}', " +
                      $"Caddy folder {settings.CaddyDir}, ingress {settings.Ingress}.");
            notes.AddRange(infra.Warnings);
            if (infra.CaddyDir is not null && !infra.CaddySingleFileMount)
            {
                var log = new CollectingLog();
                var r = await proxy.EnsureIncludedAsync(settings, log, ct);
                notes.AddRange(log.Lines);
                if (!r.Success) notes.Add("The Caddyfile could not be prepared automatically; add an `import` of sites/vp-*.caddy by hand.");
            }
        }

        // App template + instance (validated by InstanceService like any other instance).
        AppDefinition app;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var appSlug = slug;
            for (var n = 2; await db.Apps.AnyAsync(a => a.Slug == appSlug, ct); n++) appSlug = $"{slug}-{n}";
            app = new AppDefinition
            {
                Name = string.IsNullOrWhiteSpace(request.AppName) ? (isInfra ? $"Shared infrastructure ({stack.ProjectName})" : stack.ProjectName) : request.AppName.Trim(),
                Slug = appSlug,
                Description = $"Imported from {stack.WorkingDir} on {time.GetUtcNow():yyyy-MM-dd}.",
                Type = AppType.DockerCompose,
                ComposeTemplate = AppService.Normalize(compose.Text!),
                EnvTemplate = AppService.Normalize(env),
                DefaultProxyService = isInfra ? null : request.ProxyService ?? stack.ProxyService,
                DefaultProxyPort = isInfra ? null : request.ProxyPort ?? stack.ProxyPort,
                IsSystem = isInfra,
            };
            db.Apps.Add(app);
            await db.SaveChangesAsync(ct);
        }

        var saved = await instances.SaveAsync(new AppInstance
        {
            AppId = app.Id,
            App = app,
            Name = stack.ProjectName,
            Slug = slug,
            Description = isInfra ? "Imported shared infrastructure." : null,
            PublicUrl = publicUrl,
            InstallPath = stack.WorkingDir,
            ComposeProjectName = stack.ProjectName,
            ComposeFileName = compose.FileName,
            BuildMode = request.BuildMode ?? stack.SuggestedBuildMode,
            TlsInternal = request.TlsInternal ?? (takenOver is not null ? takenOver.TlsInternal && !CaddySiteParser.HandlesTls(takenOver.Directives) : stack.TlsInternal),
            ProxyDirectives = request.ProxyDirectives ?? takenOver?.Directives,
            ProxyOptions = request.ProxyOptions ?? takenOver?.ProxyOptions,
        }, user, ct);
        if (!saved.Success)
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            db.Apps.Remove(await db.Apps.FirstAsync(a => a.Id == app.Id, ct));
            await db.SaveChangesAsync(ct);
            return Result<ImportOutcome>.Fail(saved.Errors);
        }

        // .env must exist for compose commands run before the first deploy (start/stop/logs pass --env-file .env).
        var envPath = Path.Combine(stack.WorkingDir, RenderedInstance.EnvFileName);
        if (!File.Exists(envPath))
        {
            await File.WriteAllTextAsync(envPath, env, ct);
            notes.Add($"Created {envPath} (copy of the stack's environment) so compose commands find it.");
        }

        AppInstance instance;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            instance = await db.Instances.Include(i => i.App).FirstAsync(i => i.Id == saved.Value!.Id, ct);
            instance.Status = stack.RuntimeStatus;
            instance.StatusDetail = "Imported; not deployed by v-panel yet";
            instance.DeployedConfigHash = AppInstance.ImportedHash;
            instance.ImportInfoJson = new ImportInfo
            {
                ImportedAt = time.GetUtcNow(),
                ImportedBy = user,
                OriginalComposeFiles = stack.ConfigFiles,
                OriginalEnvFile = stack.EnvFile,
                BackupDir = backupDir,
                PendingTakeover = takeover,
            }.ToJson();
            await db.SaveChangesAsync(ct);
        }

        if (!isInfra && instance.App?.DefaultProxyService is null && publicUrl is not null)
            notes.Add("No proxy service is set: the public URL will not be routed until you choose one.");
        notes.Add("Deploy once from v-panel to bring the stack fully under management (preview first: it lists services that would be recreated).");
        await audit.LogAsync(user, "instance.import", instance.Slug, $"{stack.ProjectName} from {stack.WorkingDir}", ct);
        return Result<ImportOutcome>.Ok(new ImportOutcome(instance, notes));
    }

    /// <summary>
    /// Which services the next deploy would recreate: compares <c>docker compose config --hash</c> of the files v-panel would write
    /// with the config-hash label of the running containers. Nothing is written to the install folder.
    /// </summary>
    public async Task<DeployPreview> PreviewDeployAsync(int instanceId, CancellationToken ct = default)
    {
        var instance = await instances.GetAsync(instanceId, ct);
        if (instance?.App is null) return new DeployPreview(false, "Instance not found.", []);
        var settings = settingsService.Get();
        var dir = PathGuard.ResolveInsideAny(settings.AllowedRoots, instance.InstallPath);
        if (dir is null) return new DeployPreview(false, "Install path is outside the allowed roots.", []);

        var rendered = ComposeRenderer.Render(instance, settings);
        var tmp = Path.Combine(options.Value.DataDir, "tmp", "preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var composePath = Path.Combine(tmp, instance.EffectiveComposeFile);
            await File.WriteAllTextAsync(composePath, rendered.Compose, ct);
            var envPath = Path.Combine(tmp, RenderedInstance.EnvFileName);
            await File.WriteAllTextAsync(envPath, rendered.Env, ct);
            var args = new List<string> { "compose", "--ansi", "never", "-p", instance.ProjectName, "--project-directory", dir, "-f", composePath };
            if (rendered.Override is not null)
            {
                var overridePath = Path.Combine(tmp, RenderedInstance.OverrideFile);
                await File.WriteAllTextAsync(overridePath, rendered.Override, ct);
                args.AddRange(["-f", overridePath]);
            }
            args.AddRange(["--env-file", envPath, "config", "--hash", "*"]);
            var r = await docker.RunAsync(args, workingDir: dir, timeout: TimeSpan.FromMinutes(2), ct: ct);
            if (!r.Success) return new DeployPreview(false, r.StdErr.Trim(), []);
            var next = ParseHashes(r.StdOut);

            var ps = await docker.RunAsync(["ps", "--all", "--filter", $"label=com.docker.compose.project={instance.ProjectName}",
                "--format", "{{.Label \"com.docker.compose.service\"}}\t{{.Label \"com.docker.compose.config-hash\"}}\t{{.Label \"com.docker.compose.oneoff\"}}"],
                timeout: TimeSpan.FromMinutes(1), ct: ct);
            var current = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in ps.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var p = line.Split('\t');
                if (p.Length >= 2 && p[0].Length > 0 && (p.Length < 3 || p[2] != "True")) current.TryAdd(p[0], p[1]);
            }
            var changes = next.Keys.Union(current.Keys).Order(StringComparer.Ordinal)
                .Select(s => new ServiceChange(s, current.GetValueOrDefault(s), next.GetValueOrDefault(s))).ToList();
            return new DeployPreview(true, null, changes);
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { /* best effort */ }
        }
    }

    internal static Dictionary<string, string> ParseHashes(string output)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var p = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (p.Length == 2) d[p[0]] = p[1];
        }
        return d;
    }

    private sealed record ComposeSource(string? Text, string? FileName, string? Error);

    /// <summary>One compose file in the stack folder is imported verbatim (comments kept); several are merged by docker compose.</summary>
    private async Task<ComposeSource> ReadComposeAsync(ScannedStack stack, List<string> notes, CancellationToken ct)
    {
        var dir = stack.WorkingDir!;
        if (stack.ConfigFiles.Count == 1 && Path.GetDirectoryName(stack.ConfigFiles[0])?.TrimEnd('/') == dir.TrimEnd('/'))
            return new ComposeSource(await File.ReadAllTextAsync(stack.ConfigFiles[0], ct), Path.GetFileName(stack.ConfigFiles[0]), null);

        var args = new List<string> { "compose", "--ansi", "never", "-p", stack.ProjectName, "--project-directory", dir };
        foreach (var f in stack.ConfigFiles) args.AddRange(["-f", f]);
        args.AddRange(["config", "--no-interpolate", "--no-path-resolution"]);
        var r = await docker.RunAsync(args, workingDir: dir, timeout: TimeSpan.FromMinutes(2), ct: ct);
        if (!r.Success) return new ComposeSource(null, null, "Could not merge the compose files: " + r.StdErr.Trim());
        const string merged = "docker-compose.vpanel-merged.yml";
        notes.Add($"Compose files {string.Join(", ", stack.ConfigFiles.Select(Path.GetFileName))} were merged; v-panel will deploy {merged} (the originals are left as they are).");
        return new ComposeSource("# Merged by v-panel from: " + string.Join(", ", stack.ConfigFiles) + "\n" + r.StdOut, merged, null);
    }

    private string BackupOriginals(ScannedStack stack)
    {
        var dir = Path.Combine(stack.WorkingDir!, BackupFolder, time.GetUtcNow().ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(dir);
        foreach (var f in stack.ConfigFiles.Append(stack.EnvFile).OfType<string>().Where(File.Exists).Distinct())
            File.Copy(f, Path.Combine(dir, Path.GetFileName(f)), overwrite: true);
        return dir;
    }

    /// <summary>The shared-infra template seeded on first start is replaced by the imported stack when it was never deployed.</summary>
    private async Task RemoveSeededInfraAsync(List<string> notes, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var seeded = await db.Apps.Include(a => a.Instances).FirstOrDefaultAsync(a => a.Slug == BuiltInTemplates.InfraSlug && a.IsSystem, ct);
        if (seeded is null || seeded.Instances.Any(i => i.DeployedConfigHash is not null)) return;
        var ids = seeded.Instances.Select(i => i.Id).ToList();
        db.Deployments.RemoveRange(db.Deployments.Where(d => ids.Contains(d.InstanceId)));
        db.Instances.RemoveRange(seeded.Instances);
        db.Apps.Remove(seeded);
        await db.SaveChangesAsync(ct);
        notes.Add("Removed the built-in 'shared-infra' template (never deployed): the imported stack replaces it.");
    }

    private static bool SameSite(string address, Uri uri)
    {
        var url = CaddySiteParser.PublicUrl(address);
        // Host only: in tunnel mode the hand-written site may be http://host while the instance URL is https://host.
        return url is not null && UrlHelper.TryParse(url, out var other)
               && string.Equals(other.IdnHost, uri.IdnHost, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Drops a managed block left by an earlier v-panel render, so it is not imported as user content.</summary>
    internal static string StripManaged(string env)
    {
        var start = env.IndexOf(EnvFile.ManagedHeader, StringComparison.Ordinal);
        var end = env.IndexOf(EnvFile.ManagedFooter, StringComparison.Ordinal);
        if (start < 0 || end < start) return env;
        var after = env.IndexOf('\n', end);
        return env[..start] + (after < 0 ? "" : env[(after + 1)..]);
    }

    private sealed class CollectingLog : IDeploymentLog
    {
        public List<string> Lines { get; } = [];
        public void Info(string message) => Lines.Add(message);
        public void Warn(string message) => Lines.Add(message);
        public void Error(string message) => Lines.Add(message);
        public void Output(string line) { }
    }
}
