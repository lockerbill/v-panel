using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VPanel.Core.Abstractions;
using VPanel.Core.Configuration;
using VPanel.Core.Domain;
using VPanel.Infrastructure.Data;
using VPanel.Infrastructure.Services;

namespace VPanel.Infrastructure.Deployments;

/// <summary>
/// Executes queued deployment jobs. Jobs of the same instance are serialized; different instances run
/// concurrently up to <see cref="VPanelOptions.MaxConcurrentDeployments"/>.
/// </summary>
public sealed class DeploymentWorker(
    DeploymentQueue queue,
    DeploymentLogBus bus,
    IDbContextFactory<VPanelDbContext> dbFactory,
    AppTypeRegistry registry,
    SettingsService settingsService,
    ProxyRouteService proxyRoutes,
    IOptions<VPanelOptions> options,
    TimeProvider time,
    ILogger<DeploymentWorker> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _instanceLocks = new();
    private readonly ConcurrentDictionary<int, byte> _activeInstances = new();

    /// <summary>Instances with a job currently executing (status sync skips them).</summary>
    public IReadOnlyCollection<int> ActiveInstanceIds => _activeInstances.Keys.ToList();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverAsync(stoppingToken);
        using var slots = new SemaphoreSlim(Math.Max(1, options.Value.MaxConcurrentDeployments));
        var running = new List<Task>();
        await foreach (var id in queue.ReadAllAsync(stoppingToken))
        {
            await slots.WaitAsync(stoppingToken);
            running.RemoveAll(t => t.IsCompleted);
            running.Add(Task.Run(async () =>
            {
                try { await RunJobAsync(id, stoppingToken); }
                finally { slots.Release(); }
            }, CancellationToken.None));
        }
        await Task.WhenAll(running);
    }

    /// <summary>Jobs interrupted by a restart are marked failed; queued jobs are re-enqueued.</summary>
    private async Task RecoverAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var stale = await db.Deployments.Include(d => d.Instance)
            .Where(d => d.Status == DeploymentStatus.Running || d.Status == DeploymentStatus.Queued).OrderBy(d => d.Id).ToListAsync(ct);
        foreach (var d in stale)
        {
            if (d.Status == DeploymentStatus.Running)
            {
                d.Status = DeploymentStatus.Failed;
                d.FinishedAt = time.GetUtcNow();
                d.ErrorMessage = "Interrupted: v-panel restarted while the job was running.";
                if (d.Instance is { Status: InstanceStatus.Deploying } inst) { inst.Status = InstanceStatus.Failed; inst.StatusDetail = d.ErrorMessage; }
            }
        }
        await db.SaveChangesAsync(ct);
        foreach (var d in stale.Where(d => d.Status == DeploymentStatus.Queued)) await queue.EnqueueAsync(d.Id, ct);
    }

    internal async Task RunJobAsync(int deploymentId, CancellationToken ct)
    {
        int instanceId;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var d = await db.Deployments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == deploymentId, ct);
            if (d is null || d.Status != DeploymentStatus.Queued) return;
            instanceId = d.InstanceId;
        }

        var gate = _instanceLocks.GetOrAdd(instanceId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        _activeInstances[instanceId] = 0;
        var log = bus.Start(deploymentId);
        try
        {
            await ExecuteCoreAsync(deploymentId, log, ct);
        }
        finally
        {
            _activeInstances.TryRemove(instanceId, out _);
            gate.Release();
            bus.Complete(deploymentId);
        }
    }

    private async Task ExecuteCoreAsync(int deploymentId, LiveDeploymentLog log, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var deployment = await db.Deployments.Include(d => d.Instance).ThenInclude(i => i!.App).ThenInclude(a => a!.Volumes)
            .FirstAsync(d => d.Id == deploymentId, ct);
        var instance = deployment.Instance!;
        deployment.Status = DeploymentStatus.Running;
        deployment.StartedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        bus.NotifyChanged(deploymentId);

        OperationResult result;
        var settings = settingsService.Get();
        log.Info($"{deployment.Action} '{instance.Name}' (app {instance.App!.Name}) requested by {deployment.TriggeredBy ?? "system"}");
        try
        {
            var handler = registry.Get(instance.App.Type);
            var removeOptions = deployment.Parameters is null ? null : JsonSerializer.Deserialize<RemoveOptions>(deployment.Parameters);
            result = await handler.ExecuteAsync(deployment.Action, new InstanceOperationContext(instance, settings, log), removeOptions, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            result = OperationResult.Fail("Cancelled: v-panel is shutting down.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Deployment {Id} crashed", deploymentId);
            log.Error(ex.Message);
            result = OperationResult.Fail($"Unexpected error: {ex.Message}");
        }

        // Persist instance state before syncing routes (route table is computed from DB).
        instance.Status = result.NewStatus;
        instance.StatusDetail = result.Error;
        if (deployment.Action == DeploymentAction.Deploy && result.Success)
        {
            instance.DeployedConfigHash = result.ConfigHash;
            instance.LastDeployedAt = time.GetUtcNow();
        }
        if (deployment.Action == DeploymentAction.Remove && result.Success) instance.DeployedConfigHash = null;
        await db.SaveChangesAsync(CancellationToken.None);

        string? proxyError = null;
        if (result.Success && deployment.Action is DeploymentAction.Deploy or DeploymentAction.Remove)
        {
            try
            {
                var sync = await proxyRoutes.SyncAsync(settings, log, ct);
                if (!sync.Success) proxyError = sync.Error;
            }
            catch (Exception ex)
            {
                proxyError = ex.Message;
                log.Error($"Route sync failed: {ex.Message}");
            }
            var hasRoute = ProxyRouteService.RouteFor(instance) is not null;
            if (proxyError is not null && !hasRoute)
            {
                log.Warn("Route sync failed, but this instance has no public route; deployment is still considered successful.");
                proxyError = null;
            }
        }

        var success = result.Success && proxyError is null;
        deployment.Status = success ? DeploymentStatus.Succeeded : DeploymentStatus.Failed;
        deployment.ErrorMessage = result.Error ?? (proxyError is null ? null : $"Containers are up, but proxy configuration failed: {proxyError}");
        if (proxyError is not null) instance.StatusDetail = deployment.ErrorMessage;
        deployment.ConfigHash = result.ConfigHash;
        deployment.ComposeSnapshot = result.RenderedCompose;
        deployment.EnvSnapshot = result.RenderedEnv;
        deployment.FinishedAt = time.GetUtcNow();
        if (success) log.Info($"Done in {(deployment.FinishedAt - deployment.StartedAt)?.TotalSeconds:0.0}s");
        else log.Error(deployment.ErrorMessage ?? "Failed");
        deployment.Log = log.Text;
        await db.SaveChangesAsync(CancellationToken.None);
    }
}
