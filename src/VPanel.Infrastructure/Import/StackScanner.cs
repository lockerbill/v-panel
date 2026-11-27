using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VPanel.Core.Configuration;
using VPanel.Core.Domain;
using VPanel.Core.Utilities;
using VPanel.Infrastructure.Data;
using VPanel.Infrastructure.Docker;
using VPanel.Infrastructure.Proxy;
using VPanel.Infrastructure.Services;

namespace VPanel.Infrastructure.Import;

/// <summary>
/// Read-only discovery of compose stacks installed before v-panel: running/stopped projects from Docker labels, stacks that are
/// down from compose files under the adopted roots, the shared infrastructure (Caddy) and hand-written Caddy sites.
/// Nothing is written and no container is touched.
/// </summary>
public sealed class StackScanner(
    DockerCli docker,
    SettingsService settingsService,
    IDbContextFactory<VPanelDbContext> dbFactory,
    IOptions<VPanelOptions> options,
    ILogger<StackScanner> logger)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);
    private static readonly string[] ComposeFileNames = ["compose.yaml", "compose.yml", "docker-compose.yaml", "docker-compose.yml"];

    /// <summary>Project names never offered for import (v-panel itself).</summary>
    public static readonly string[] ReservedProjects = ["vpanel"];

    public async Task<ScanResult> ScanAsync(CancellationToken ct = default)
    {
        var settings = settingsService.Get();
        var warnings = new List<string>();

        List<ContainerSnapshot> containers;
        try
        {
            containers = await InspectContainersAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ScanResult(false, ex.Message, [], null, false, [], [], []);
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var instances = await db.Instances.AsNoTracking().Include(i => i.App).Where(i => i.Status != InstanceStatus.Removed).ToListAsync(ct);
        // Drafts never deployed own no containers yet: they do not hide a stack (a clash is reported when importing).
        var managed = instances.Where(i => i.DeployedConfigHash is not null).Select(i => i.ProjectName).ToHashSet(StringComparer.Ordinal);

        // Projects: compose labels first, then compose ls (covers projects whose containers are all gone but known to compose).
        var projects = new Dictionary<string, (string? WorkingDir, List<string> Files, List<string> EnvFiles)>(StringComparer.Ordinal);
        foreach (var g in containers.Where(c => !c.OneOff).GroupBy(c => c.Project))
        {
            var first = g.First();
            projects[g.Key] = (first.WorkingDir, first.ConfigFiles.ToList(), first.EnvironmentFiles.ToList());
        }
        foreach (var (name, files) in await ComposeLsAsync(ct))
            if (!projects.ContainsKey(name) && files.Count > 0)
                projects[name] = (Path.GetDirectoryName(files[0]), files, []);

        // Stacks that are down: compose files under the adopted roots.
        var knownDirs = projects.Values.Select(p => p.WorkingDir).OfType<string>().ToHashSet(StringComparer.Ordinal);
        foreach (var (dir, file) in FindComposeFilesOnDisk(settings))
        {
            if (knownDirs.Contains(dir) || instances.Any(i => SamePath(i.InstallPath, dir))) continue;
            var name = ProjectNameFromFile(file) ?? NormalizeProjectName(Path.GetFileName(dir));
            if (string.IsNullOrEmpty(name) || projects.ContainsKey(name)) continue;
            projects[name] = (dir, [file], []);
            knownDirs.Add(dir);
        }

        var self = SelfContainerProject(containers);
        var stacks = new List<ScannedStack>();
        foreach (var (name, info) in projects.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (ReservedProjects.Contains(name) || name == self || managed.Contains(name)) continue;
            stacks.Add(await AnalyzeAsync(name, info.WorkingDir, info.Files, info.EnvFiles, containers.Where(c => c.Project == name && !c.OneOff).ToList(), settings, ct));
        }

        // Shared infrastructure: the stack running Caddy (an already managed one counts too).
        var infraStack = stacks.FirstOrDefault(s => s.Services.Any(IsCaddy));
        InfraDetection? infra = null;
        var infraManaged = false;
        if (infraStack is not null)
        {
            infra = InfraDetector.Detect(infraStack, stacks);
            stacks[stacks.IndexOf(infraStack)] = infraStack with { Kind = StackKind.Infra };
            if (stacks.Count(s => s.Services.Any(IsCaddy)) > 1)
                warnings.Add("Several stacks run Caddy; only the first one (" + infraStack.ProjectName + ") is treated as shared infrastructure.");
        }
        else
        {
            infraManaged = instances.Any(i => i.App?.IsSystem == true && i.DeployedConfigHash is not null);
        }

        // Hand-written Caddy sites (from the detected infra, or the configured Caddy folder).
        var caddySettings = infra?.ApplyTo(settings) ?? settings;
        var sites = ReadManualSites(caddySettings, warnings);
        var sharedNetwork = caddySettings.SharedNetwork;
        stacks = stacks.Select(s => s.Kind == StackKind.App ? Suggest(s, sites, stacks, sharedNetwork) : s).ToList();

        if (settings.AdoptedRoots.Count == 0 && stacks.Any(s => !s.InAllowedRoot))
            warnings.Add("No adopted roots are configured: stacks outside the apps root cannot be imported. Re-run the installer with --adopt (or set VPanel:AdoptedRoots).");
        foreach (var rejected in options.Value.RejectedAdoptedRoots) warnings.Add($"Adopted root refused: {rejected}");

        return new ScanResult(true, null, stacks, infra, infraManaged, sites, managed.OrderBy(m => m).ToList(), warnings);
    }

    internal static bool IsCaddy(StackService s) => s.ImageBase == "caddy";

    // ---- analysis -------------------------------------------------------------------------------------------------

    private async Task<ScannedStack> AnalyzeAsync(string project, string? workingDir, List<string> files, List<string> envFiles,
        List<ContainerSnapshot> containers, PanelSettings settings, CancellationToken ct)
    {
        var warnings = new List<string>();
        var accessible = workingDir is not null && Directory.Exists(workingDir) && files.Count > 0 && files.All(File.Exists);
        var envFile = envFiles.FirstOrDefault() ?? (workingDir is null ? null : Path.Combine(workingDir, ".env"));
        if (envFile is not null && !File.Exists(envFile) && accessible) envFile = null;

        IReadOnlyList<StackService> services = [];
        string? configError = null;
        if (accessible)
        {
            var args = new List<string> { "compose", "--ansi", "never", "-p", project, "--project-directory", workingDir! };
            foreach (var f in files) args.AddRange(["-f", f]);
            if (envFile is not null) args.AddRange(["--env-file", envFile]);
            args.AddRange(["config", "--format", "json"]);
            var r = await docker.RunAsync(args, workingDir: workingDir, timeout: Timeout, ct: ct);
            if (r.Success)
            {
                try { services = ParseComposeModel(r.StdOut); }
                catch (JsonException ex) { configError = "Unreadable `docker compose config` output: " + ex.Message; }
            }
            else configError = LastLine(r.StdErr);
        }
        if (services.Count == 0 && containers.Count > 0) services = ServicesFromContainers(containers);

        if (files.Count > 1) warnings.Add($"Started with {files.Count} compose files; they will be merged into one file for v-panel.");
        if (envFiles.Count > 1) warnings.Add("Started with several env files; only the first one is imported.");
        else if (envFiles.Count == 1 && workingDir is not null && !SamePath(envFiles[0], Path.Combine(workingDir, ".env")))
            warnings.Add($"Uses env file {envFiles[0]}; its content becomes the .env v-panel writes in {workingDir}.");
        if (!accessible && workingDir is not null)
            warnings.Add($"{workingDir} is not visible to v-panel. Mount it (installer --adopt) to import this stack.");

        var neededPaths = new List<string>();
        if (workingDir is not null) neededPaths.Add(workingDir);
        neededPaths.AddRange(services.Select(s => s.BuildContext).OfType<string>().Where(p => p.StartsWith('/')));
        var suggestedRoot = CommonAncestor(neededPaths);
        var inAllowed = workingDir is not null && PathGuard.ResolveInsideAny(settings.AllowedRoots, workingDir) is not null;
        if (accessible && !inAllowed)
            warnings.Add($"{workingDir} is outside the apps root and the adopted roots; add {suggestedRoot} to VPanel:AdoptedRoots.");
        foreach (var ctx in services.Select(s => s.BuildContext).OfType<string>().Where(p => p.StartsWith('/')))
            if (!Directory.Exists(ctx))
                warnings.Add($"Build context {ctx} is not visible to v-panel: rebuilding this stack needs it mounted.");

        var status = containers.Count == 0
            ? InstanceStatus.Stopped
            : DockerComposeHandler.Summarize(containers.Select(c => (c.State, c.ExitCode)).ToList());

        return new ScannedStack
        {
            ProjectName = project,
            WorkingDir = workingDir,
            ConfigFiles = files,
            EnvFile = envFile,
            Services = services,
            Containers = containers,
            RuntimeStatus = status,
            Accessible = accessible,
            InAllowedRoot = inAllowed,
            SuggestedRoot = suggestedRoot,
            ConfigError = configError,
            Warnings = warnings,
            SuggestedSlug = Core.Utilities.Slug.From(project),
            SuggestedBuildMode = services.Any(s => s.HasBuild && s.Image is not null) ? BuildMode.PullFirst : BuildMode.Build,
        };
    }

    /// <summary>Proxy service, port and public URL: from the hand-written Caddy site pointing at the stack, else from the shared network.</summary>
    internal static ScannedStack Suggest(ScannedStack stack, IReadOnlyList<CaddySiteBlock> sites, IReadOnlyList<ScannedStack> all, string sharedNetwork)
    {
        foreach (var site in sites.Where(s => s.Upstream is not null))
        {
            var (host, port) = SplitUpstream(site.Upstream!);
            var service = MatchUpstream(stack, host, sharedNetwork);
            if (service is null) continue;
            var warnings = stack.Warnings.ToList();
            if (!site.IsSimple)
                warnings.Add($"Caddy site '{site.Address}' ({site.File}:{site.Line}) is not a plain reverse proxy ({site.Reason}); it stays hand-written.");
            return stack with
            {
                ProxyService = service.Name,
                ProxyPort = port,
                PublicUrl = site.IsSimple ? CaddySiteParser.PublicUrl(site.Addresses[0]) : null,
                TlsInternal = site.TlsInternal,
                ManualSite = site,
                Warnings = warnings,
            };
        }
        var onShared = stack.Services.FirstOrDefault(s => s.On(sharedNetwork) is not null);
        if (onShared is null) return stack;
        return stack with
        {
            ProxyService = onShared.Name,
            ProxyPort = onShared.ContainerPorts.Count == 1 ? onShared.ContainerPorts[0] : null,
        };
    }

    /// <summary>Service of <paramref name="stack"/> that a Caddy upstream host designates on the shared network.</summary>
    internal static StackService? MatchUpstream(ScannedStack stack, string host, string sharedNetwork)
    {
        foreach (var s in stack.Services)
        {
            var net = s.On(sharedNetwork);
            if (net is not null && net.Aliases.Contains(host, StringComparer.OrdinalIgnoreCase)) return s;
        }
        foreach (var s in stack.Services)
        {
            if (s.ContainerName is { } cn && string.Equals(cn, host, StringComparison.OrdinalIgnoreCase)) return s;
            if (string.Equals($"{stack.ProjectName}-{s.Name}-1", host, StringComparison.OrdinalIgnoreCase)) return s;
            if (stack.Containers.Any(c => c.Service == s.Name && (string.Equals(c.Name, host, StringComparison.OrdinalIgnoreCase)
                                                                  || c.Networks.Values.Any(a => a.Contains(host, StringComparer.OrdinalIgnoreCase)))))
                return s;
        }
        return null;
    }

    internal static (string Host, int? Port) SplitUpstream(string upstream)
    {
        var colon = upstream.LastIndexOf(':');
        return colon > 0 && int.TryParse(upstream[(colon + 1)..], out var p) ? (upstream[..colon], p) : (upstream, null);
    }

    // ---- caddy ----------------------------------------------------------------------------------------------------

    /// <summary>Sites of the main Caddyfile and of the files it imports from the Caddy folder (v-panel's vp-* files excluded).</summary>
    internal static List<CaddySiteBlock> ReadManualSites(PanelSettings s, List<string> warnings)
    {
        var sites = new List<CaddySiteBlock>();
        if (!File.Exists(s.CaddyMainFile)) return sites;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>([s.CaddyMainFile]);
        while (queue.Count > 0)
        {
            var file = queue.Dequeue();
            if (!seen.Add(file)) continue;
            string text;
            try { text = File.ReadAllText(file); }
            catch (Exception ex) { warnings.Add($"Cannot read {file}: {ex.Message}"); continue; }
            var model = CaddySiteParser.Parse(text, file);
            sites.AddRange(model.Sites);
            foreach (var pattern in model.ImportPatterns)
                foreach (var f in ResolveImport(pattern, file, s))
                    queue.Enqueue(f);
        }
        return sites;
    }

    /// <summary>Host files matched by an import pattern written for the Caddy container (only inside the Caddy folder).</summary>
    internal static IEnumerable<string> ResolveImport(string pattern, string fromFile, PanelSettings s)
    {
        var containerDir = s.CaddyConfigDirInContainer.TrimEnd('/');
        string hostPath;
        if (pattern.StartsWith(containerDir + "/", StringComparison.Ordinal)) hostPath = Path.Combine(s.CaddyDir, pattern[(containerDir.Length + 1)..]);
        else if (!pattern.StartsWith('/')) hostPath = Path.Combine(Path.GetDirectoryName(fromFile)!, pattern);
        else yield break;
        var dir = Path.GetDirectoryName(hostPath)!;
        var glob = Path.GetFileName(hostPath);
        if (!Directory.Exists(dir) || PathGuard.ResolveInside(s.CaddyDir, Path.Combine(dir, "x")) is null) yield break;
        string[] files;
        try { files = glob.IndexOfAny(['*', '?']) >= 0 ? Directory.GetFiles(dir, glob) : File.Exists(hostPath) ? [hostPath] : []; }
        catch (IOException) { yield break; }
        foreach (var f in files.Order(StringComparer.Ordinal))
            if (!Path.GetFileName(f).StartsWith(CaddyConfigBuilder.ManagedPrefix, StringComparison.Ordinal))
                yield return f;
    }

    // ---- docker ---------------------------------------------------------------------------------------------------

    private async Task<List<ContainerSnapshot>> InspectContainersAsync(CancellationToken ct)
    {
        var ids = await docker.RunAsync(["ps", "--all", "--quiet", "--no-trunc", "--filter", "label=com.docker.compose.project"], timeout: Timeout, ct: ct);
        if (!ids.Success) throw new InvalidOperationException("docker ps failed: " + LastLine(ids.StdErr));
        var list = ids.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (list.Count == 0) return [];
        var inspect = await docker.RunAsync(["inspect", .. list], timeout: Timeout, ct: ct);
        if (!inspect.Success) throw new InvalidOperationException("docker inspect failed: " + LastLine(inspect.StdErr));
        return ParseInspect(inspect.StdOut);
    }

    private async Task<List<(string Name, List<string> Files)>> ComposeLsAsync(CancellationToken ct)
    {
        var r = await docker.RunAsync(["compose", "ls", "--all", "--format", "json"], timeout: Timeout, ct: ct);
        if (!r.Success || string.IsNullOrWhiteSpace(r.StdOut)) return [];
        try
        {
            using var doc = JsonDocument.Parse(r.StdOut);
            return doc.RootElement.EnumerateArray()
                .Select(e => (Str(e, "Name") ?? "", (Str(e, "ConfigFiles") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()))
                .Where(p => p.Item1.Length > 0).ToList();
        }
        catch (JsonException ex)
        {
            logger.LogDebug(ex, "Unparseable compose ls output");
            return [];
        }
    }

    /// <summary>Project of the container v-panel itself runs in (container hostname = short container id).</summary>
    private static string? SelfContainerProject(IEnumerable<ContainerSnapshot> containers)
    {
        var host = Environment.MachineName;
        if (host.Length < 12) return null;
        return containers.FirstOrDefault(c => c.Id.StartsWith(host, StringComparison.OrdinalIgnoreCase))?.Project;
    }

    internal static List<ContainerSnapshot> ParseInspect(string json)
    {
        var result = new List<ContainerSnapshot>();
        using var doc = JsonDocument.Parse(json);
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var labels = new Dictionary<string, string>(StringComparer.Ordinal);
            if (e.TryGetProperty("Config", out var cfg) && cfg.TryGetProperty("Labels", out var l) && l.ValueKind == JsonValueKind.Object)
                foreach (var p in l.EnumerateObject()) labels[p.Name] = p.Value.GetString() ?? "";
            if (!labels.TryGetValue("com.docker.compose.project", out var project)) continue;

            var networks = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            if (e.TryGetProperty("NetworkSettings", out var ns) && ns.TryGetProperty("Networks", out var nets) && nets.ValueKind == JsonValueKind.Object)
                foreach (var n in nets.EnumerateObject())
                    networks[n.Name] = Strings(n.Value, "Aliases").Concat(Strings(n.Value, "DNSNames")).Distinct().ToList();

            var mounts = new List<MountRef>();
            if (e.TryGetProperty("Mounts", out var ms) && ms.ValueKind == JsonValueKind.Array)
                foreach (var m in ms.EnumerateArray())
                    mounts.Add(new MountRef(Str(m, "Type") ?? "", Str(m, "Source") ?? Str(m, "Name") ?? "", Str(m, "Destination") ?? ""));

            var published = new List<int>();
            if (e.TryGetProperty("HostConfig", out var hc) && hc.TryGetProperty("PortBindings", out var pb) && pb.ValueKind == JsonValueKind.Object)
                foreach (var p in pb.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.Array && p.Value.GetArrayLength() > 0 && int.TryParse(p.Name.Split('/')[0], out var port))
                        published.Add(port);

            string state = "";
            int? exit = null;
            if (e.TryGetProperty("State", out var st) && st.ValueKind == JsonValueKind.Object)
            {
                state = Str(st, "Status") ?? "";
                if (st.TryGetProperty("ExitCode", out var x) && x.ValueKind == JsonValueKind.Number) exit = x.GetInt32();
            }

            result.Add(new ContainerSnapshot(
                Str(e, "Id") ?? "",
                (Str(e, "Name") ?? "").TrimStart('/'),
                project,
                labels.GetValueOrDefault("com.docker.compose.service", ""),
                state,
                exit,
                cfg.ValueKind == JsonValueKind.Object ? Str(cfg, "Image") : null,
                labels.GetValueOrDefault("com.docker.compose.project.working_dir"),
                SplitList(labels.GetValueOrDefault("com.docker.compose.project.config_files")),
                SplitList(labels.GetValueOrDefault("com.docker.compose.project.environment_file")),
                labels.GetValueOrDefault("com.docker.compose.config-hash"),
                labels.GetValueOrDefault("com.docker.compose.oneoff") == "True",
                networks,
                mounts,
                published.Distinct().ToList()));
        }
        return result;
    }

    /// <summary>Services from the resolved model of <c>docker compose config --format json</c>.</summary>
    internal static List<StackService> ParseComposeModel(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var networkNames = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("networks", out var nets) && nets.ValueKind == JsonValueKind.Object)
            foreach (var n in nets.EnumerateObject())
                networkNames[n.Name] = n.Value.ValueKind == JsonValueKind.Object && Str(n.Value, "name") is { Length: > 0 } nm ? nm : n.Name;

        var list = new List<StackService>();
        if (!root.TryGetProperty("services", out var svcs) || svcs.ValueKind != JsonValueKind.Object) return list;
        foreach (var s in svcs.EnumerateObject())
        {
            var v = s.Value;
            string? buildContext = null;
            var hasBuild = v.TryGetProperty("build", out var b);
            if (hasBuild) buildContext = b.ValueKind == JsonValueKind.String ? b.GetString() : Str(b, "context");

            var networks = new List<ServiceNetworkRef>();
            if (v.TryGetProperty("networks", out var sn) && sn.ValueKind == JsonValueKind.Object)
                foreach (var n in sn.EnumerateObject())
                    networks.Add(new ServiceNetworkRef(n.Name, networkNames.GetValueOrDefault(n.Name, n.Name),
                        n.Value.ValueKind == JsonValueKind.Object ? Strings(n.Value, "aliases").ToList() : []));

            var ports = new List<int>();
            var published = new List<int>();
            if (v.TryGetProperty("ports", out var ps) && ps.ValueKind == JsonValueKind.Array)
                foreach (var p in ps.EnumerateArray())
                {
                    if (p.TryGetProperty("target", out var t) && t.ValueKind == JsonValueKind.Number) ports.Add(t.GetInt32());
                    if (Str(p, "published") is { Length: > 0 } pub && int.TryParse(pub, out var pp)) published.Add(pp);
                }
            foreach (var x in Strings(v, "expose"))
                if (int.TryParse(x.Split('/')[0], out var ex)) ports.Add(ex);

            var mounts = new List<MountRef>();
            if (v.TryGetProperty("volumes", out var vs) && vs.ValueKind == JsonValueKind.Array)
                foreach (var m in vs.EnumerateArray())
                    mounts.Add(new MountRef(Str(m, "type") ?? "", Str(m, "source") ?? "", Str(m, "target") ?? ""));

            list.Add(new StackService(s.Name, Str(v, "image"), hasBuild, buildContext, Str(v, "container_name"), Str(v, "network_mode"),
                networks, ports.Distinct().ToList(), published.Distinct().ToList(), mounts));
        }
        return list;
    }

    private static List<StackService> ServicesFromContainers(IEnumerable<ContainerSnapshot> containers) =>
        containers.GroupBy(c => c.Service).Select(g =>
        {
            var c = g.First();
            return new StackService(g.Key, c.Image, false, null, c.Name, null,
                c.Networks.Select(n => new ServiceNetworkRef(n.Key, n.Key, n.Value)).ToList(), [], c.PublishedContainerPorts, c.Mounts);
        }).ToList();

    // ---- disk -----------------------------------------------------------------------------------------------------

    /// <summary>Compose files under the adopted roots (up to 3 levels deep), skipping hidden and v-panel folders.</summary>
    private static IEnumerable<(string Dir, string File)> FindComposeFilesOnDisk(PanelSettings s)
    {
        var results = new List<(string, string)>();
        foreach (var root in s.AdoptedRoots)
            Walk(root, 0);
        return results;

        void Walk(string dir, int depth)
        {
            if (!Directory.Exists(dir) || SamePath(dir, s.AppsRoot)) return;
            var file = ComposeFileNames.Select(n => Path.Combine(dir, n)).FirstOrDefault(File.Exists);
            if (file is not null)
            {
                results.Add((dir, file));
                return;
            }
            if (depth >= 3) return;
            string[] children;
            try { children = Directory.GetDirectories(dir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
            foreach (var child in children.Order(StringComparer.Ordinal))
            {
                var name = Path.GetFileName(child);
                if (name.StartsWith('.') || name is "node_modules" or "data" or "backups") continue;
                Walk(child, depth + 1);
            }
        }
    }

    /// <summary>Top-level <c>name:</c> of a compose file, if it is a plain value.</summary>
    internal static string? ProjectNameFromFile(string file)
    {
        try
        {
            foreach (var line in File.ReadLines(file))
            {
                if (!line.StartsWith("name:", StringComparison.Ordinal)) continue;
                var v = line[5..].Split('#')[0].Trim().Trim('"', '\'');
                return v.Contains('$') || v.Length == 0 ? null : NormalizeProjectName(v);
            }
        }
        catch (IOException) { }
        return null;
    }

    /// <summary>Same normalisation as docker compose for a folder name used as project name.</summary>
    internal static string NormalizeProjectName(string name)
    {
        var chars = name.ToLowerInvariant().Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').ToArray();
        return new string(chars).TrimStart('-', '_');
    }

    internal static string? CommonAncestor(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return null;
        var split = paths.Select(p => Path.GetFullPath(p).TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries)).ToList();
        var common = new List<string>();
        for (var i = 0; i < split.Min(p => p.Length); i++)
        {
            var part = split[0][i];
            if (split.All(p => p[i] == part)) common.Add(part);
            else break;
        }
        return common.Count == 0 ? null : "/" + string.Join('/', common);
    }

    private static bool SamePath(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.Ordinal);

    private static List<string> SplitList(string? value) =>
        string.IsNullOrWhiteSpace(value) ? [] : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static string? Str(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v)
            ? v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Null => null, _ => v.ToString() }
            : null;

    private static IEnumerable<string> Strings(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.ToString()).Where(x => x.Length > 0)
            : [];

    private static string LastLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "unknown error";
}
