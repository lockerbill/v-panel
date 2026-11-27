using Microsoft.EntityFrameworkCore;
using VPanel.Core.Abstractions;
using VPanel.Core.Configuration;
using VPanel.Core.Domain;
using VPanel.Core.Utilities;
using VPanel.Infrastructure.Data;
using VPanel.Infrastructure.Docker;

namespace VPanel.Infrastructure.Deployments;

/// <summary>Computes the desired route table from the database and pushes it to the reverse proxy.</summary>
public sealed class ProxyRouteService(IDbContextFactory<VPanelDbContext> dbFactory, IReverseProxyProvider proxy)
{
    /// <param name="includePendingTakeover">
    /// Include an imported instance whose hand-written site has not been taken over yet. Only the sync that removes that site
    /// may do so: any other sync would serve the address twice and Caddy would refuse the whole configuration.
    /// </param>
    public static ProxyRoute? RouteFor(AppInstance i, bool includePendingTakeover = false)
    {
        if (i.Status is InstanceStatus.Draft or InstanceStatus.Removed || i.DeployedConfigHash is null) return null;
        // Imported stacks keep their hand-written route until v-panel deploys them (and attaches the proxy alias) once.
        if (i.IsAwaitingFirstDeploy) return null;
        if (!includePendingTakeover && i.Import?.PendingTakeover is not null) return null;
        if (string.IsNullOrWhiteSpace(i.PublicUrl) || !UrlHelper.TryParse(i.PublicUrl, out _)) return null;
        if (i.EffectiveProxyService is not { Length: > 0 } svc || i.EffectiveProxyPort is not { } port) return null;
        var alias = string.IsNullOrWhiteSpace(i.DeployedProxyAlias) ? ComposeRenderer.ProxyAlias(i, svc) : i.DeployedProxyAlias;
        return new ProxyRoute(i.ProjectName, i.PublicUrl, $"{alias}:{port}", i.TlsInternal, i.ProxyDirectives, i.ProxyOptions);
    }

    public async Task<IReadOnlyList<ProxyRoute>> DesiredRoutesAsync(PanelSettings settings, CancellationToken ct, int? takeoverInstanceId = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var instances = await db.Instances.AsNoTracking().Include(i => i.App).ToListAsync(ct);
        var routes = instances.Select(i => RouteFor(i, i.Id == takeoverInstanceId)).OfType<ProxyRoute>().ToList();
        if (!string.IsNullOrWhiteSpace(settings.PanelPublicUrl))
            routes.Insert(0, new ProxyRoute("vpanel", settings.PanelPublicUrl, settings.PanelUpstream));
        return routes;
    }

    /// <param name="removals">Hand-written sites removed in the same apply (take over by <paramref name="takeoverInstanceId"/>).</param>
    public async Task<ProxyApplyResult> SyncAsync(PanelSettings settings, IDeploymentLog log, CancellationToken ct,
        IReadOnlyList<ManualSiteRemoval>? removals = null, int? takeoverInstanceId = null)
    {
        if (!settings.CaddyEnabled)
        {
            log.Info("Reverse proxy integration is disabled in settings; skipping route sync.");
            return new ProxyApplyResult(true);
        }
        var routes = await DesiredRoutesAsync(settings, ct, removals is { Count: > 0 } ? takeoverInstanceId : null);
        log.Info($"Syncing {routes.Count} route(s) to {proxy.Name}");
        if (removals is { Count: > 0 })
            foreach (var r in removals) log.Info($"Taking over hand-written site '{r.Address}' from {r.File}");
        return await proxy.ApplyAsync(routes, settings, log, ct, removals);
    }
}
