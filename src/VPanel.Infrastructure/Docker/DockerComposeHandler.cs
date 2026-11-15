using System.Text.Json;
using Microsoft.Extensions.Logging;
using VPanel.Core.Abstractions;
using VPanel.Core.Configuration;
using VPanel.Core.Domain;
using VPanel.Core.Utilities;

namespace VPanel.Infrastructure.Docker;

/// <summary>Deploys and operates instances defined by a docker compose file.</summary>
public sealed class DockerComposeHandler(DockerCli docker, ILogger<DockerComposeHandler> logger) : IAppTypeHandler
{
    private static readonly TimeSpan PullTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan UpTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMinutes(2);

    public AppType Type => AppType.DockerCompose;

    public ValidationReport Validate(string compose, string env)
    {
        var values = EnvFile.Parse(env).ToDictionary();
        var analysis = ComposeAnalyzer.Analyze(compose, values);
        return new ValidationReport(analysis.Issues);
    }

    public string ComputeConfigHash(AppInstance instance, PanelSettings settings) => ComposeRenderer.Render(instance, settings).ConfigHash;

    public async Task<OperationResult> ExecuteAsync(DeploymentAction action, InstanceOperationContext ctx, RemoveOptions? removeOptions, CancellationToken ct)
    {
        var instance = ctx.Instance;
        var dir = PathGuard.ResolveInside(ctx.Settings.AppsRoot, instance.InstallPath);
        if (dir is null)
            return OperationResult.Fail($"Install path '{instance.InstallPath}' must be inside apps root '{ctx.Settings.AppsRoot}'.");

        return action switch
        {
            DeploymentAction.Deploy => await DeployAsync(ctx, dir, ct),
            DeploymentAction.Start => await SimpleAsync(ctx, dir, InstanceStatus.Running, ct, "start"),
            DeploymentAction.Stop => await SimpleAsync(ctx, dir, InstanceStatus.Stopped, ct, "stop"),
            DeploymentAction.Restart => await SimpleAsync(ctx, dir, InstanceStatus.Running, ct, "restart"),
            DeploymentAction.Down => await SimpleAsync(ctx, dir, InstanceStatus.Stopped, ct, "down", "--remove-orphans"),
            DeploymentAction.Remove => await RemoveAsync(ctx, dir, removeOptions ?? new RemoveOptions(), ct),
            _ => OperationResult.Fail($"Unsupported action {action}"),
        };
    }

    private async Task<OperationResult> DeployAsync(InstanceOperationContext ctx, string dir, CancellationToken ct)
    {
        var log = ctx.Log;
        var instance = ctx.Instance;

        log.Info($"Rendering configuration for '{instance.Name}' (project {instance.ProjectName})");
        var rendered = ComposeRenderer.Render(instance, ctx.Settings);
        foreach (var w in rendered.Analysis.Issues.Where(i => i.IsWarning)) log.Warn(w.ToString());
        if (!rendered.Analysis.IsValid)
        {
            foreach (var e in rendered.Analysis.Issues.Where(i => !i.IsWarning)) log.Error(e.ToString());
            return OperationResult.Fail("Compose validation failed.");
        }
        if (instance.EffectiveProxyService is { Length: > 0 } ps && rendered.Override is null)
            log.Warn($"Proxy service '{ps}' not found in compose (or uses network_mode); it will not be attached to the shared network.");

        // 1. write files
        log.Info($"Writing files to {dir}");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, RenderedInstance.ComposeFile), rendered.Compose, ct);
        var envPath = Path.Combine(dir, RenderedInstance.EnvFileName);
        await File.WriteAllTextAsync(envPath, rendered.Env, ct);
        TrySetOwnerOnly(envPath);
        var overridePath = Path.Combine(dir, RenderedInstance.OverrideFile);
        if (rendered.Override is not null) await File.WriteAllTextAsync(overridePath, rendered.Override, ct);
        else if (File.Exists(overridePath)) File.Delete(overridePath);
        var withOverride = rendered.Override is not null;

        // 2. shared network (needed by override and commonly referenced as external)
        if (!await docker.EnsureNetworkAsync(ctx.Settings.SharedNetwork, log, ct))
            return OperationResult.Fail("Could not create shared network.") with { ConfigHash = rendered.ConfigHash };

        // 3. validate with docker + resolve model
        log.Info("Validating with `docker compose config`");
        var config = await docker.RunAsync(DockerCli.ComposeArgs(instance.ProjectName, dir, withOverride, "config", "--format", "json"),
            workingDir: dir, timeout: ShortTimeout, ct: ct);
        if (!config.Success)
        {
            foreach (var line in config.StdErr.Split('\n', StringSplitOptions.RemoveEmptyEntries)) log.Error(line);
            return Fail("docker compose config failed.", rendered);
        }

        // 4. external resources + bind folders
        var model = ResolvedCompose.Parse(config.StdOut);
        foreach (var net in model.ExternalNetworks)
            if (!await docker.EnsureNetworkAsync(net, log, ct)) return Fail($"Could not create network '{net}'.", rendered);
        var manualVolumes = instance.App?.Volumes.Where(v => v.Kind == VolumeKind.External).Select(v => v.Name) ?? [];
        foreach (var vol in model.ExternalVolumes.Union(manualVolumes))
            if (!await docker.EnsureVolumeAsync(vol, log, ct)) return Fail($"Could not create volume '{vol}'.", rendered);
        foreach (var bind in model.BindSources)
        {
            if (Directory.Exists(bind) || File.Exists(bind)) continue;
            if (PathGuard.IsStrictlyInside(dir, bind) && string.IsNullOrEmpty(Path.GetExtension(bind)))
            {
                log.Info($"Creating bind folder {bind}");
                Directory.CreateDirectory(bind);
            }
        }
        foreach (var vol in model.NamedVolumes) log.Info($"Volume '{vol}' will be created by docker compose if missing");

        // 5. pull + up
        log.Info("Pulling images");
        var pull = await docker.RunAsync(DockerCli.ComposeArgs(instance.ProjectName, dir, withOverride, "pull", "--ignore-buildable"),
            log.Output, dir, PullTimeout, ct);
        if (!pull.Success) log.Warn("Image pull reported errors; continuing with locally available images.");

        log.Info("Starting containers (docker compose up -d)");
        var upArgs = new List<string> { "up", "-d", "--remove-orphans" };
        if (model.HasBuild) upArgs.Add("--build");
        var up = await docker.RunAsync(DockerCli.ComposeArgs(instance.ProjectName, dir, withOverride, [.. upArgs]), log.Output, dir, UpTimeout, ct);
        if (!up.Success) return Fail($"docker compose up failed (exit {up.ExitCode}).", rendered);

        var status = await QueryStatusAsync(instance.ProjectName, dir, withOverride, ct);
        log.Info($"Instance status: {status}");
        return new OperationResult(true, status, null, rendered.ConfigHash, rendered.Compose, rendered.Env);
    }

    private static OperationResult Fail(string error, RenderedInstance r) =>
        new(false, InstanceStatus.Failed, error, r.ConfigHash, r.Compose, r.Env);

    private async Task<OperationResult> SimpleAsync(InstanceOperationContext ctx, string dir, InstanceStatus okStatus, CancellationToken ct, params string[] command)
    {
        if (!File.Exists(Path.Combine(dir, RenderedInstance.ComposeFile)))
            return OperationResult.Fail("Instance has not been deployed yet (no compose file in install path).", ctx.Instance.Status);
        var withOverride = File.Exists(Path.Combine(dir, RenderedInstance.OverrideFile));
        ctx.Log.Info($"docker compose {string.Join(' ', command)}");
        var r = await docker.RunAsync(DockerCli.ComposeArgs(ctx.Instance.ProjectName, dir, withOverride, command), ctx.Log.Output, dir, UpTimeout, ct);
        if (!r.Success) return OperationResult.Fail($"docker compose {command[0]} failed (exit {r.ExitCode}).", ctx.Instance.Status);
        // After an explicit stop/down the exit codes (e.g. 2 or 143 on SIGTERM) are not failures.
        var status = okStatus == InstanceStatus.Stopped ? InstanceStatus.Stopped : await QueryStatusAsync(ctx.Instance.ProjectName, dir, withOverride, ct);
        return new OperationResult(true, status);
    }

    private async Task<OperationResult> RemoveAsync(InstanceOperationContext ctx, string dir, RemoveOptions opts, CancellationToken ct)
    {
        var log = ctx.Log;
        if (File.Exists(Path.Combine(dir, RenderedInstance.ComposeFile)))
        {
            var withOverride = File.Exists(Path.Combine(dir, RenderedInstance.OverrideFile));
            var args = new List<string> { "down", "--remove-orphans" };
            if (opts.DeleteVolumes) args.Add("--volumes");
            log.Info($"docker compose {string.Join(' ', args)}");
            var r = await docker.RunAsync(DockerCli.ComposeArgs(ctx.Instance.ProjectName, dir, withOverride, [.. args]), log.Output, dir, UpTimeout, ct);
            if (!r.Success) return OperationResult.Fail($"docker compose down failed (exit {r.ExitCode}).", ctx.Instance.Status);
        }
        else
        {
            log.Info("No compose file found; nothing to stop.");
        }

        if (opts.DeleteFiles && Directory.Exists(dir))
        {
            log.Info($"Deleting folder {dir}");
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception ex) { log.Warn($"Could not delete folder: {ex.Message}"); }
        }
        return new OperationResult(true, InstanceStatus.Removed);
    }

    private async Task<InstanceStatus> QueryStatusAsync(string project, string dir, bool withOverride, CancellationToken ct)
    {
        var r = await docker.RunAsync(DockerCli.ComposeArgs(project, dir, withOverride, "ps", "--all", "--format", "json"), workingDir: dir, timeout: ShortTimeout, ct: ct);
        if (!r.Success) return InstanceStatus.Failed;
        return Summarize(ParsePs(r.StdOut).Select(c => (c.State, c.ExitCode)).ToList());
    }

    /// <summary>
    /// Aggregates container states. Containers that exited with code 0 are treated as completed one-shot
    /// jobs (migrations, db-init...) and do not make a running instance "partial".
    /// </summary>
    internal static InstanceStatus Summarize(IReadOnlyList<(string State, int? ExitCode)> containers)
    {
        var relevant = containers.Where(c => !(IsExited(c.State) && c.ExitCode == 0)).ToList();
        if (relevant.Count == 0) return InstanceStatus.Stopped;
        var running = relevant.Count(c => c.State.Equals("running", StringComparison.OrdinalIgnoreCase));
        if (running == relevant.Count) return InstanceStatus.Running;
        if (running == 0)
            return relevant.Any(c => c.State.Equals("restarting", StringComparison.OrdinalIgnoreCase) || (IsExited(c.State) && c.ExitCode is not (null or 0 or 137 or 143)))
                ? InstanceStatus.Failed
                : InstanceStatus.Stopped;
        return InstanceStatus.Partial;
    }

    private static bool IsExited(string state) => state.Equals("exited", StringComparison.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<ContainerInfo>> GetContainersAsync(AppInstance instance, PanelSettings settings, CancellationToken ct)
    {
        var r = await docker.RunAsync(["ps", "--all", "--filter", $"label=com.docker.compose.project={instance.ProjectName}", "--format", "json", "--no-trunc"],
            timeout: ShortTimeout, ct: ct);
        if (!r.Success) return [];
        var list = new List<ContainerInfo>();
        foreach (var line in r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var e = doc.RootElement;
                var labels = Str(e, "Labels");
                var service = labels.Split(',').Select(l => l.Split('=', 2)).FirstOrDefault(p => p[0] == "com.docker.compose.service")?.ElementAtOrDefault(1) ?? "";
                list.Add(new ContainerInfo(Str(e, "Names"), service, Str(e, "State"), Str(e, "Status"), Str(e, "Image"), Str(e, "Ports")));
            }
            catch (JsonException ex) { logger.LogDebug(ex, "Unparseable docker ps line"); }
        }
        return list.OrderBy(c => c.Service).ThenBy(c => c.Name).ToList();
    }

    public async Task StreamLogsAsync(AppInstance instance, PanelSettings settings, int tail, bool follow, Action<string> onLine, CancellationToken ct)
    {
        var dir = PathGuard.ResolveInside(settings.AppsRoot, instance.InstallPath);
        if (dir is null || !File.Exists(Path.Combine(dir, RenderedInstance.ComposeFile)))
        {
            onLine("(instance has not been deployed)");
            return;
        }
        var withOverride = File.Exists(Path.Combine(dir, RenderedInstance.OverrideFile));
        var args = DockerCli.ComposeArgs(instance.ProjectName, dir, withOverride, "logs", "--no-color", "--timestamps", "--tail", tail.ToString());
        if (follow) args.Add("--follow");
        try
        {
            await docker.RunAsync(args, onLine, dir, follow ? null : ShortTimeout, ct);
        }
        catch (OperationCanceledException) { /* stopped following */ }
    }

    public async Task<IReadOnlyDictionary<string, RuntimeState>> GetRuntimeStatesAsync(CancellationToken ct)
    {
        var r = await docker.RunAsync(["ps", "--all", "--filter", "label=com.docker.compose.project", "--format", "{{.Label \"com.docker.compose.project\"}}\t{{.State}}\t{{.Status}}"],
            timeout: ShortTimeout, ct: ct);
        if (!r.Success) throw new InvalidOperationException("docker ps failed: " + r.StdErr.Trim());
        return r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split('\t'))
            .Where(p => p.Length >= 2 && p[0].StartsWith("vp-", StringComparison.Ordinal))
            .GroupBy(p => p[0], p => (State: p[1], ExitCode: p.Length > 2 ? ExitCodeFromStatus(p[2]) : null))
            .ToDictionary(g => g.Key, g =>
            {
                var states = g.ToList();
                var detail = string.Join(", ", states.GroupBy(s => s.State).Select(s => $"{s.Count()} {s.Key}"));
                return new RuntimeState(g.Key, Summarize(states), detail);
            });
    }

    internal static int? ExitCodeFromStatus(string status)
    {
        var m = System.Text.RegularExpressions.Regex.Match(status, @"Exited \((\d+)\)");
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }

    internal static List<(string Name, string State, int? ExitCode)> ParsePs(string output)
    {
        var result = new List<(string, string, int?)>();
        var text = output.Trim();
        if (text.Length == 0) return result;
        IEnumerable<JsonElement> items;
        if (text.StartsWith('['))
        {
            using var doc = JsonDocument.Parse(text);
            items = doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
        }
        else
        {
            items = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => { using var d = JsonDocument.Parse(l); return d.RootElement.Clone(); }).ToList();
        }
        foreach (var e in items)
            result.Add((Str(e, "Name"), Str(e, "State"), e.TryGetProperty("ExitCode", out var x) && x.ValueKind == JsonValueKind.Number ? x.GetInt32() : null));
        return result;
    }

    private static string Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString() : "";

    private static void TrySetOwnerOnly(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { /* best effort */ }
    }
}

/// <summary>Subset of the `docker compose config --format json` model used by the engine.</summary>
internal sealed record ResolvedCompose(
    IReadOnlyList<string> ExternalNetworks,
    IReadOnlyList<string> ExternalVolumes,
    IReadOnlyList<string> NamedVolumes,
    IReadOnlyList<string> BindSources,
    bool HasBuild)
{
    public static ResolvedCompose Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var extNets = new List<string>();
        var extVols = new List<string>();
        var named = new List<string>();
        var binds = new List<string>();
        var hasBuild = false;

        if (root.TryGetProperty("networks", out var nets) && nets.ValueKind == JsonValueKind.Object)
            foreach (var n in nets.EnumerateObject())
                if (IsExternal(n.Value)) extNets.Add(NameOf(n));

        if (root.TryGetProperty("volumes", out var vols) && vols.ValueKind == JsonValueKind.Object)
            foreach (var v in vols.EnumerateObject())
                (IsExternal(v.Value) ? extVols : named).Add(NameOf(v));

        if (root.TryGetProperty("services", out var svcs) && svcs.ValueKind == JsonValueKind.Object)
            foreach (var s in svcs.EnumerateObject())
            {
                if (s.Value.TryGetProperty("build", out _)) hasBuild = true;
                if (!s.Value.TryGetProperty("volumes", out var sv) || sv.ValueKind != JsonValueKind.Array) continue;
                foreach (var m in sv.EnumerateArray())
                    if (m.TryGetProperty("type", out var t) && t.GetString() == "bind" && m.TryGetProperty("source", out var src) && src.GetString() is { } p)
                        binds.Add(p);
            }
        return new(extNets.Distinct().ToList(), extVols.Distinct().ToList(), named.Distinct().ToList(), binds.Distinct().ToList(), hasBuild);
    }

    private static bool IsExternal(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty("external", out var x) && x.ValueKind == JsonValueKind.True;

    private static string NameOf(JsonProperty p) =>
        p.Value.ValueKind == JsonValueKind.Object && p.Value.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } s ? s : p.Name;
}
