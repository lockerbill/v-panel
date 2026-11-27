using Microsoft.Extensions.Logging;
using VPanel.Core.Abstractions;
using VPanel.Core.Configuration;
using VPanel.Infrastructure.Docker;

namespace VPanel.Infrastructure.Proxy;

/// <summary>
/// Caddy integration: writes one snippet per site to the sites folder (mounted into the shared Caddy
/// container), validates with `caddy validate` and applies with `caddy reload`. Changes are rolled back
/// if validation or reload fails. Config survives Caddy restarts because it lives in files.
/// </summary>
public sealed class CaddyFileProvider(DockerCli docker, ILogger<CaddyFileProvider> logger) : IReverseProxyProvider
{
    private static readonly SemaphoreSlim Lock = new(1, 1);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    public string Name => "Caddy";

    public async Task EnsureBootstrapAsync(PanelSettings settings, CancellationToken ct)
    {
        Directory.CreateDirectory(settings.CaddySitesDir);
        if (!File.Exists(settings.CaddyMainFile))
        {
            logger.LogInformation("Creating main Caddyfile at {Path}", settings.CaddyMainFile);
            await File.WriteAllTextAsync(settings.CaddyMainFile, CaddyConfigBuilder.MainCaddyfile(settings.CaddyConfigDirInContainer), ct);
        }
        var placeholder = Path.Combine(settings.CaddySitesDir, "_placeholder.caddy");
        if (!File.Exists(placeholder))
            await File.WriteAllTextAsync(placeholder, "# keeps the import glob non-empty\n", ct);
    }

    public async Task<ProxyApplyResult> ApplyAsync(IReadOnlyList<ProxyRoute> routes, PanelSettings settings, IDeploymentLog log, CancellationToken ct,
        IReadOnlyList<ManualSiteRemoval>? removals = null)
    {
        await Lock.WaitAsync(ct);
        try
        {
            await EnsureBootstrapAsync(settings, ct);
            var desired = CaddyConfigBuilder.Build(routes, settings.Ingress);
            var dir = settings.CaddySitesDir;

            var existing = Directory.EnumerateFiles(dir, CaddyConfigBuilder.ManagedPrefix + "*" + CaddyConfigBuilder.Extension)
                .ToDictionary(f => Path.GetFileName(f), File.ReadAllText, StringComparer.Ordinal);

            // Hand-written sites taken over: computed before anything is written, applied together with the managed files.
            var manualEdits = PlanRemovals(removals, settings, log);

            var changed = desired.Any(d => !existing.TryGetValue(d.Key, out var old) || old != d.Value)
                          || existing.Keys.Any(k => !desired.ContainsKey(k))
                          || manualEdits.Count > 0;
            if (!changed)
            {
                log.Info($"Caddy: {desired.Count} site(s) already up to date");
                return new ProxyApplyResult(true, null, []);
            }

            foreach (var (file, content) in desired) await File.WriteAllTextAsync(Path.Combine(dir, file), content, ct);
            foreach (var stale in existing.Keys.Where(k => !desired.ContainsKey(k))) File.Delete(Path.Combine(dir, stale));
            log.Info($"Caddy: wrote {desired.Count} site file(s) to {dir}");
            ApplyRemovals(manualEdits, settings, log);

            var validate = await CaddyAsync(settings, ct, "validate", "--config", settings.CaddyMainFileInContainer, "--adapter", "caddyfile");
            if (!validate.Success)
            {
                Rollback(dir, existing, desired.Keys);
                RestoreManual(manualEdits);
                var msg = $"Caddy validation failed: {LastLines(validate.Combined)}";
                log.Error(msg);
                return new ProxyApplyResult(false, msg);
            }

            var reload = await CaddyAsync(settings, ct, "reload", "--config", settings.CaddyMainFileInContainer, "--adapter", "caddyfile");
            if (!reload.Success)
            {
                Rollback(dir, existing, desired.Keys);
                RestoreManual(manualEdits);
                var msg = $"Caddy reload failed: {LastLines(reload.Combined)}";
                log.Error(msg);
                return new ProxyApplyResult(false, msg);
            }
            log.Info("Caddy: configuration reloaded");
            return new ProxyApplyResult(true, null, desired.Keys.ToList());
        }
        finally
        {
            Lock.Release();
        }
    }

    public async Task<ProxyApplyResult> EnsureIncludedAsync(PanelSettings settings, IDeploymentLog log, CancellationToken ct)
    {
        await Lock.WaitAsync(ct);
        try
        {
            await EnsureBootstrapAsync(settings, ct);
            var main = settings.CaddyMainFile;
            var original = await File.ReadAllTextAsync(main, ct);
            if (ImportsManagedSites(original, settings))
            {
                log.Info($"Caddy: {main} already loads the sites folder");
                return new ProxyApplyResult(true);
            }

            var backup = Backup(settings, main);
            var line = $"import {settings.CaddyConfigDirInContainer.TrimEnd('/')}/sites/{CaddyConfigBuilder.ManagedPrefix}*{CaddyConfigBuilder.Extension}";
            var updated = original.TrimEnd('\n') + "\n\n# Added by v-panel: load the site files it manages (vp-*.caddy).\n" + line + "\n";
            await File.WriteAllTextAsync(main, updated, ct);
            log.Info($"Caddy: added '{line}' to {main} (backup: {backup})");

            foreach (var step in new[] { "validate", "reload" })
            {
                var r = await CaddyAsync(settings, ct, step, "--config", settings.CaddyMainFileInContainer, "--adapter", "caddyfile");
                if (r.Success) continue;
                await File.WriteAllTextAsync(main, original, ct);
                var msg = $"Caddy {step} failed after adding the import line (restored): {LastLines(r.Combined)}";
                log.Error(msg);
                return new ProxyApplyResult(false, msg);
            }
            return new ProxyApplyResult(true, null, [main]);
        }
        finally
        {
            Lock.Release();
        }
    }

    /// <summary>True when a top-level <c>import</c> of the main Caddyfile matches the managed vp-*.caddy files.</summary>
    internal static bool ImportsManagedSites(string caddyfile, PanelSettings settings)
    {
        var containerDir = settings.CaddyConfigDirInContainer.TrimEnd('/');
        var sitesDir = containerDir + "/sites";
        foreach (var pattern in CaddySiteParser.Parse(caddyfile).ImportPatterns)
        {
            var full = pattern.StartsWith('/') ? pattern : containerDir + "/" + pattern.TrimStart('.', '/');
            var slash = full.LastIndexOf('/');
            if (slash < 0 || full[..slash].TrimEnd('/') != sitesDir) continue;
            var glob = "^" + System.Text.RegularExpressions.Regex.Escape(full[(slash + 1)..]).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            if (System.Text.RegularExpressions.Regex.IsMatch("vp-example.com.caddy", glob)) return true;
        }
        return false;
    }

    private sealed record ManualEdit(string File, string Original, string? Updated);

    /// <summary>Works out the new content of each hand-written file (null: the file only held that site and is moved away).</summary>
    private List<ManualEdit> PlanRemovals(IReadOnlyList<ManualSiteRemoval>? removals, PanelSettings settings, IDeploymentLog log)
    {
        var edits = new Dictionary<string, ManualEdit>(StringComparer.Ordinal);
        foreach (var r in removals ?? [])
        {
            var file = Core.Utilities.PathGuard.ResolveInside(settings.CaddyDir, r.File);
            if (file is null || Path.GetFileName(file).StartsWith(CaddyConfigBuilder.ManagedPrefix, StringComparison.Ordinal))
            {
                log.Warn($"Caddy: not touching '{r.File}' (outside {settings.CaddyDir} or managed by v-panel)");
                continue;
            }
            if (!File.Exists(file))
            {
                log.Warn($"Caddy: '{r.File}' no longer exists; nothing to take over");
                continue;
            }
            var original = edits.TryGetValue(file, out var prev) ? prev.Original : File.ReadAllText(file);
            var current = prev is null ? original : prev.Updated ?? "";
            var updated = CaddySiteParser.RemoveSite(current, r.Address, out var empty);
            if (updated is null)
            {
                log.Warn($"Caddy: site '{r.Address}' not found in {file}; nothing to take over");
                continue;
            }
            edits[file] = new ManualEdit(file, original, empty ? null : updated);
        }
        return edits.Values.ToList();
    }

    private void ApplyRemovals(List<ManualEdit> edits, PanelSettings settings, IDeploymentLog log)
    {
        foreach (var e in edits)
        {
            var backup = Backup(settings, e.File);
            if (e.Updated is null)
            {
                File.Delete(e.File);
                log.Info($"Caddy: moved {e.File} to {backup} (its only site is now generated by v-panel)");
            }
            else
            {
                File.WriteAllText(e.File, e.Updated);
                log.Info($"Caddy: removed the taken-over site from {e.File} (backup: {backup})");
            }
        }
    }

    private void RestoreManual(List<ManualEdit> edits)
    {
        foreach (var e in edits)
        {
            try { File.WriteAllText(e.File, e.Original); }
            catch (Exception ex) { logger.LogError(ex, "Could not restore {File}", e.File); }
        }
    }

    /// <summary>
    /// Copies a hand-written file to &lt;CaddyDir&gt;/.vpanel-backup/&lt;timestamp&gt;/ (outside the sites folder, so Caddy never
    /// imports the copy) and returns the copy's path.
    /// </summary>
    private static string Backup(PanelSettings settings, string file)
    {
        var relative = Path.GetRelativePath(settings.CaddyDir, file);
        var target = Path.Combine(settings.CaddyDir, ".vpanel-backup", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"), relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(file, target, overwrite: true);
        return target;
    }

    public async Task<(bool Ok, string Message)> CheckAsync(PanelSettings settings, CancellationToken ct)
    {
        var r = await CaddyAsync(settings, ct, "version");
        return r.Success
            ? (true, $"Caddy container '{settings.CaddyContainer}' reachable: {r.StdOut.Trim()}")
            : (false, $"Cannot exec into Caddy container '{settings.CaddyContainer}': {LastLines(r.Combined)}");
    }

    private Task<Core.Abstractions.ProcessResult> CaddyAsync(PanelSettings s, CancellationToken ct, params string[] args) =>
        docker.RunAsync(["exec", s.CaddyContainer, "caddy", .. args], timeout: Timeout, ct: ct);

    private void Rollback(string dir, IReadOnlyDictionary<string, string> previous, IEnumerable<string> written)
    {
        logger.LogWarning("Rolling back Caddy site files");
        foreach (var f in written) if (!previous.ContainsKey(f)) TryDelete(Path.Combine(dir, f));
        foreach (var (f, content) in previous) File.WriteAllText(Path.Combine(dir, f), content);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* ignore */ }
    }

    private static string LastLines(string text, int n = 6) =>
        string.Join(" | ", text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(n));
}
