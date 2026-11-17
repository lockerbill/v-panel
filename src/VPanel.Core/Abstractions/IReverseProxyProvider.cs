using VPanel.Core.Configuration;

namespace VPanel.Core.Abstractions;

/// <summary>A public URL routed to an upstream (host:port reachable from the proxy).</summary>
public sealed record ProxyRoute(string Id, string PublicUrl, string Upstream);

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

    Task<ProxyApplyResult> ApplyAsync(IReadOnlyList<ProxyRoute> routes, PanelSettings settings, IDeploymentLog log, CancellationToken ct);

    Task<(bool Ok, string Message)> CheckAsync(PanelSettings settings, CancellationToken ct);
}
