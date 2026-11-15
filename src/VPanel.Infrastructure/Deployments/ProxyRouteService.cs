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
    public static ProxyRoute? RouteFor(AppInstance i)
    {
        if (i.Status is InstanceStatus.Draft or InstanceStatus.Removed || i.DeployedConfigHash is null) return null;
        if (string.IsNullOrWhiteSpace(i.PublicUrl) || !UrlHelper.TryParse(i.PublicUrl, out _)) return null;
        if (i.EffectiveProxyService is not { Length: > 0 } svc || i.EffectiveProxyPort is not { } port) return null;
        return new ProxyRoute(i.ProjectName, i.PublicUrl, $"{ComposeRenderer.ProxyAlias(i, svc)}:{port}");
    }

    public async Task<IReadOnlyList<ProxyRoute>> DesiredRoutesAsync(PanelSettings settings, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var instances = await db.Instances.AsNoTracking().Include(i => i.App).ToListAsync(ct);
        var routes = instances.Select(RouteFor).OfType<ProxyRoute>().ToList();
        if (!string.IsNullOrWhiteSpace(settings.PanelPublicUrl))
            routes.Insert(0, new ProxyRoute("vpanel", settings.PanelPublicUrl, settings.PanelUpstream));
        return routes;
    }

    public async Task<ProxyApplyResult> SyncAsync(PanelSettings settings, IDeploymentLog log, CancellationToken ct)
    {
        if (!settings.CaddyEnabled)
        {
            log.Info("Reverse proxy integration is disabled in settings; skipping route sync.");
            return new ProxyApplyResult(true);
        }
        var routes = await DesiredRoutesAsync(settings, ct);
        log.Info($"Syncing {routes.Count} route(s) to {proxy.Name}");
        return await proxy.ApplyAsync(routes, settings, log, ct);
    }
}
