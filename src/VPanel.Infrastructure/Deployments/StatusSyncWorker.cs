using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VPanel.Core.Configuration;
using VPanel.Core.Domain;
using VPanel.Infrastructure.Data;
using VPanel.Infrastructure.Services;

namespace VPanel.Infrastructure.Deployments;

/// <summary>Health of the docker daemon as last observed by <see cref="StatusSyncWorker"/>.</summary>
public sealed class DockerHealth
{
    public bool? Reachable { get; private set; }
    public string? Message { get; private set; }
    public DateTimeOffset? CheckedAt { get; private set; }
    public event Action? Changed;

    public void Report(bool ok, string? message)
    {
        (Reachable, Message, CheckedAt) = (ok, message, DateTimeOffset.UtcNow);
        Changed?.Invoke();
    }
}

/// <summary>Periodically reconciles instance status with what Docker actually runs.</summary>
public sealed class StatusSyncWorker(
    IDbContextFactory<VPanelDbContext> dbFactory,
    AppTypeRegistry registry,
    DeploymentWorker deploymentWorker,
    DockerHealth health,
    DeploymentLogBus bus,
    IOptions<VPanelOptions> options,
    ILogger<StatusSyncWorker> logger) : BackgroundService
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.StatusSyncInterval);
        do
        {
            try { await SyncOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Status sync failed"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task SyncOnceAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            foreach (var type in registry.SupportedTypes)
            {
                IReadOnlyDictionary<string, Core.Abstractions.RuntimeState> states;
                try
                {
                    states = await registry.Get(type).GetRuntimeStatesAsync(ct);
                    health.Report(true, null);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    health.Report(false, ex.Message);
                    return;
                }

                await using var db = await dbFactory.CreateDbContextAsync(ct);
                var active = deploymentWorker.ActiveInstanceIds;
                var instances = await db.Instances.Include(i => i.App)
                    .Where(i => i.App!.Type == type && i.DeployedConfigHash != null
                                && i.Status != InstanceStatus.Draft && i.Status != InstanceStatus.Removed && i.Status != InstanceStatus.Deploying)
                    .ToListAsync(ct);
                var changed = false;
                foreach (var inst in instances.Where(i => !active.Contains(i.Id)))
                {
                    var (status, detail) = states.TryGetValue(inst.ProjectName, out var s)
                        ? (s.Status, s.Detail)
                        : (InstanceStatus.Stopped, "no containers");
                    if (inst.Status == InstanceStatus.Failed && status == InstanceStatus.Stopped) continue; // keep failure visible
                    if (inst.Status == InstanceStatus.Stopped && status == InstanceStatus.Failed && !detail.Contains("restarting")) continue; // stopped on purpose
                    if (inst.Status != status)
                    {
                        inst.Status = status;
                        inst.StatusDetail = detail;
                        changed = true;
                    }
                }
                if (changed)
                {
                    await db.SaveChangesAsync(ct);
                    bus.NotifyChanged(0);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
