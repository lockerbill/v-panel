using Microsoft.EntityFrameworkCore;
using VPanel.Core.Domain;
using VPanel.Core.Utilities;
using VPanel.Infrastructure.Data;

namespace VPanel.Infrastructure.Services;

public sealed record AppSummary(AppDefinition App, int InstanceCount, int RunningCount);

public sealed class AppService(IDbContextFactory<VPanelDbContext> dbFactory, AuditService audit)
{
    public async Task<List<AppSummary>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var apps = await db.Apps.AsNoTracking().Include(a => a.Instances).ToListAsync(ct);
        return apps
            .OrderBy(a => a.IsSystem ? 0 : 1).ThenBy(a => a.Name)
            .Select(a => new AppSummary(a,
                a.Instances.Count(i => i.Status != InstanceStatus.Removed),
                a.Instances.Count(i => i.Status == InstanceStatus.Running)))
            .ToList();
    }

    public async Task<AppDefinition?> GetAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Apps.AsNoTracking().Include(a => a.Volumes).Include(a => a.Instances)
            .FirstOrDefaultAsync(a => a.Id == id, ct);
    }

    /// <summary>Saves app metadata (draft). Compose content is stored even if invalid; it is validated at deploy.</summary>
    public async Task<Result<AppDefinition>> SaveAsync(AppDefinition input, string? user, CancellationToken ct = default)
    {
        var errors = new List<string>();
        input.Name = input.Name.Trim();
        if (string.IsNullOrWhiteSpace(input.Name)) errors.Add("Name is required.");
        input.Slug = string.IsNullOrWhiteSpace(input.Slug) ? Slug.From(input.Name) : input.Slug.Trim();
        if (!Slug.IsValid(input.Slug)) errors.Add("Slug may only contain lowercase letters, digits and dashes (max 48).");
        if (input.DefaultProxyPort is < 1 or > 65535) errors.Add("Proxy port must be between 1 and 65535.");
        var volumeNames = input.Volumes.Select(v => v.Name.Trim()).ToList();
        if (volumeNames.Any(string.IsNullOrEmpty)) errors.Add("Volume name is required.");
        if (volumeNames.Distinct().Count() != volumeNames.Count) errors.Add("Volume names must be unique.");
        if (errors.Count > 0) return Result<AppDefinition>.Fail(errors);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.Apps.AnyAsync(a => a.Slug == input.Slug && a.Id != input.Id, ct))
            return Result<AppDefinition>.Fail($"Another app already uses slug '{input.Slug}'.");

        AppDefinition entity;
        if (input.Id == 0)
        {
            entity = new AppDefinition();
            db.Apps.Add(entity);
        }
        else
        {
            entity = await db.Apps.Include(a => a.Volumes).FirstOrDefaultAsync(a => a.Id == input.Id, ct)
                     ?? throw new KeyNotFoundException($"App {input.Id} not found");
        }

        entity.Name = input.Name;
        entity.Slug = input.Slug;
        entity.Description = input.Description?.Trim();
        entity.Type = input.Type;
        entity.ComposeTemplate = Normalize(input.ComposeTemplate);
        entity.EnvTemplate = Normalize(input.EnvTemplate);
        entity.DefaultProxyService = string.IsNullOrWhiteSpace(input.DefaultProxyService) ? null : input.DefaultProxyService.Trim();
        entity.DefaultProxyPort = input.DefaultProxyPort;
        entity.IsSystem = input.IsSystem;

        // replace manual volumes
        db.AppVolumes.RemoveRange(entity.Volumes.Where(v => input.Volumes.All(i => i.Id != v.Id || i.Id == 0)));
        foreach (var v in input.Volumes)
        {
            var existing = v.Id == 0 ? null : entity.Volumes.FirstOrDefault(e => e.Id == v.Id);
            if (existing is null) entity.Volumes.Add(new AppVolume { Name = v.Name.Trim(), Kind = v.Kind, Description = v.Description });
            else { existing.Name = v.Name.Trim(); existing.Kind = v.Kind; existing.Description = v.Description; }
        }

        await db.SaveChangesAsync(ct);
        await audit.LogAsync(user, input.Id == 0 ? "app.create" : "app.update", entity.Slug, ct: ct);
        return Result<AppDefinition>.Ok(entity);
    }

    public async Task<Result<bool>> DeleteAsync(int id, string? user, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var app = await db.Apps.Include(a => a.Instances).FirstOrDefaultAsync(a => a.Id == id, ct);
        if (app is null) return Result<bool>.Fail("App not found.");
        if (app.Instances.Count > 0) return Result<bool>.Fail("Delete all instances of this app first.");
        db.Apps.Remove(app);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(user, "app.delete", app.Slug, ct: ct);
        return Result<bool>.Ok(true);
    }

    internal static string Normalize(string? text) => (text ?? "").Replace("\r\n", "\n");
}
