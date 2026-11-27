using VPanel.Core.Configuration;

namespace VPanel.Core.Abstractions;

/// <summary>A public URL routed to an upstream (host:port reachable from the proxy).</summary>
/// <param name="TlsInternal">Serve the site with the proxy's internal CA instead of a public certificate.</param>
/// <param name="Directives">Extra site directives (proxy syntax), placed before the generated reverse proxy.</param>
/// <param name="ProxyOptions">Options inside the generated reverse proxy block.</param>
public sealed record ProxyRoute(string Id, string PublicUrl, string Upstream, bool TlsInternal = false, string? Directives = null,
    string? ProxyOptions = null);

/// <summary>A hand-written site (one address in one config file) removed in the same apply that starts serving it.</summary>
public sealed record ManualSiteRemoval(string File, string Address);

public sealed record ProxyApplyResult(bool Success, string? Error = null, IReadOnlyList<string>? FilesWritten = null);

/// <summary>
/// Reverse proxy integration. The provider receives the complete desired set of routes and
/// converges the proxy configuration to it (idempotent). v1: Caddy via config snippets + reload.
/// </summary>
public interface IReverseProxyProvider
{
    string Name { get; }

    /// <summary>Creates base configuration (e.g. main Caddyfile) if missing.</summary>
    Task EnsureBootstrapAsync(PanelSettings settings, CancellationToken ct);

    /// <param name="removals">
    /// Hand-written sites taken over by v-panel: removed atomically with the new configuration (restored if it fails).
    /// </param>
    Task<ProxyApplyResult> ApplyAsync(IReadOnlyList<ProxyRoute> routes, PanelSettings settings, IDeploymentLog log, CancellationToken ct,
        IReadOnlyList<ManualSiteRemoval>? removals = null);

    /// <summary>
    /// Makes sure an existing (hand-written) main configuration loads the files v-panel manages, e.g. by adding an import line.
    /// Used when adopting a proxy installed before v-panel.
    /// </summary>
    Task<ProxyApplyResult> EnsureIncludedAsync(PanelSettings settings, IDeploymentLog log, CancellationToken ct);

    Task<(bool Ok, string Message)> CheckAsync(PanelSettings settings, CancellationToken ct);
}
