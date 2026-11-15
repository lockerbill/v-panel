using Microsoft.EntityFrameworkCore;
using VPanel.Core.Domain;
using VPanel.Infrastructure.Data;

namespace VPanel.Infrastructure.Services;

public sealed class AuditService(IDbContextFactory<VPanelDbContext> dbFactory)
{
    public async Task LogAsync(string? user, string action, string? target = null, string? detail = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.AuditLogs.Add(new AuditLog { User = user, Action = action, Target = target, Detail = detail });
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<AuditLog>> RecentAsync(int take = 50, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.AuditLogs.AsNoTracking().OrderByDescending(a => a.At).Take(take).ToListAsync(ct);
    }
}
