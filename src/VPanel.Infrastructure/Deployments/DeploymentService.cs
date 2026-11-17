using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VPanel.Core.Abstractions;
using VPanel.Core.Domain;
using VPanel.Infrastructure.Data;
using VPanel.Infrastructure.Services;

namespace VPanel.Infrastructure.Deployments;

public sealed class DeploymentService(IDbContextFactory<VPanelDbContext> dbFactory, DeploymentQueue queue, DeploymentLogBus bus, AuditService audit)
{
    public async Task<Result<Deployment>> EnqueueAsync(int instanceId, DeploymentAction action, string? user, RemoveOptions? removeOptions = null,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var instance = await db.Instances.Include(i => i.App).FirstOrDefaultAsync(i => i.Id == instanceId, ct);
        if (instance is null) return Result<Deployment>.Fail("Instance not found.");
        if (await db.Deployments.AnyAsync(d => d.InstanceId == instanceId && (d.Status == DeploymentStatus.Queued || d.Status == DeploymentStatus.Running), ct))
            return Result<Deployment>.Fail("Another job for this instance is already queued or running.");
        if (action != DeploymentAction.Deploy && action != DeploymentAction.Remove && instance.DeployedConfigHash is null)
            return Result<Deployment>.Fail("Instance has never been deployed.");

        var deployment = new Deployment
        {
            InstanceId = instanceId,
            Action = action,
            Status = DeploymentStatus.Queued,
            TriggeredBy = user,
            Parameters = removeOptions is null ? null : JsonSerializer.Serialize(removeOptions),
        };
        db.Deployments.Add(deployment);
        if (action == DeploymentAction.Deploy) instance.Status = InstanceStatus.Deploying;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(user, $"instance.{action.ToString().ToLowerInvariant()}", instance.Slug, ct: ct);
        await queue.EnqueueAsync(deployment.Id, ct);
        bus.NotifyChanged(deployment.Id);
        return Result<Deployment>.Ok(deployment);
    }

    public async Task<List<Deployment>> ListAsync(int? instanceId = null, int take = 100, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var q = db.Deployments.AsNoTracking().Include(d => d.Instance).ThenInclude(i => i!.App).AsQueryable();
        if (instanceId is not null) q = q.Where(d => d.InstanceId == instanceId);
        var list = await q.OrderByDescending(d => d.Id).Take(take)
            .Select(d => new Deployment
            {
                Id = d.Id, InstanceId = d.InstanceId, Instance = d.Instance, Action = d.Action, Status = d.Status,
                TriggeredBy = d.TriggeredBy, StartedAt = d.StartedAt, FinishedAt = d.FinishedAt, CreatedAt = d.CreatedAt,
                ErrorMessage = d.ErrorMessage,
            })
            .ToListAsync(ct);
        return list;
    }

    public async Task<Deployment?> GetAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Deployments.AsNoTracking().Include(d => d.Instance).ThenInclude(i => i!.App).FirstOrDefaultAsync(d => d.Id == id, ct);
    }
}
