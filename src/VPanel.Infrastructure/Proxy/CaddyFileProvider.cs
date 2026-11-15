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

    public async Task<ProxyApplyResult> ApplyAsync(IReadOnlyList<ProxyRoute> routes, PanelSettings settings, IDeploymentLog log, CancellationToken ct)
    {
        await Lock.WaitAsync(ct);
        try
        {
            await EnsureBootstrapAsync(settings, ct);
            var desired = CaddyConfigBuilder.Build(routes);
            var dir = settings.CaddySitesDir;

            var existing = Directory.EnumerateFiles(dir, CaddyConfigBuilder.ManagedPrefix + "*" + CaddyConfigBuilder.Extension)
                .ToDictionary(Path.GetFileName, File.ReadAllText, StringComparer.Ordinal)!;

            var changed = desired.Any(d => !existing.TryGetValue(d.Key, out var old) || old != d.Value)
                          || existing.Keys.Any(k => !desired.ContainsKey(k!));
            if (!changed)
            {
                log.Info($"Caddy: {desired.Count} site(s) already up to date");
                return new ProxyApplyResult(true, null, []);
            }

            foreach (var (file, content) in desired) await File.WriteAllTextAsync(Path.Combine(dir, file), content, ct);
            foreach (var stale in existing.Keys.Where(k => !desired.ContainsKey(k!))) File.Delete(Path.Combine(dir, stale!));
            log.Info($"Caddy: wrote {desired.Count} site file(s) to {dir}");

            var validate = await CaddyAsync(settings, ct, "validate", "--config", settings.CaddyMainFileInContainer, "--adapter", "caddyfile");
            if (!validate.Success)
            {
                Rollback(dir, existing, desired.Keys);
                var msg = $"Caddy validation failed: {LastLines(validate.Combined)}";
                log.Error(msg);
                return new ProxyApplyResult(false, msg);
            }

            var reload = await CaddyAsync(settings, ct, "reload", "--config", settings.CaddyMainFileInContainer, "--adapter", "caddyfile");
            if (!reload.Success)
            {
                Rollback(dir, existing, desired.Keys);
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
