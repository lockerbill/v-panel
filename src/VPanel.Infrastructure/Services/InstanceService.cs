using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using VPanel.Core.Configuration;
using VPanel.Core.Domain;
using VPanel.Core.Utilities;
using VPanel.Infrastructure.Data;

namespace VPanel.Infrastructure.Services;

public sealed record InstanceSummary(AppInstance Instance, bool HasUndeployedChanges);

public sealed class InstanceService(IDbContextFactory<VPanelDbContext> dbFactory, SettingsService settings, AppTypeRegistry registry, AuditService audit,
    IOptions<VPanelOptions> options)
{
    public async Task<List<InstanceSummary>> ListAsync(int? appId = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var q = db.Instances.AsNoTracking().Include(i => i.App).AsQueryable();
        if (appId is not null) q = q.Where(i => i.AppId == appId);
        var list = await q.ToListAsync(ct);
        return list.OrderBy(i => i.App!.IsSystem ? 0 : 1).ThenBy(i => i.App!.Name).ThenBy(i => i.Name)
            .Select(i => new InstanceSummary(i, HasUndeployedChanges(i))).ToList();
    }

    public async Task<AppInstance?> GetAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Instances.AsNoTracking().Include(i => i.App).FirstOrDefaultAsync(i => i.Id == id, ct);
    }

    public bool HasUndeployedChanges(AppInstance instance)
    {
        if (instance.DeployedConfigHash is null || instance.App is null) return false;
        if (instance.Status is InstanceStatus.Removed or InstanceStatus.Draft) return false;
        try { return registry.Get(instance.App.Type).ComputeConfigHash(instance, settings.Get()) != instance.DeployedConfigHash; }
        catch { return true; }
    }

    /// <summary>New, unsaved instance with suggested name / slug / folder.</summary>
    public async Task<AppInstance> NewDraftAsync(int appId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var app = await db.Apps.AsNoTracking().FirstAsync(a => a.Id == appId, ct);
        var taken = await db.Instances.Select(i => i.Slug).ToListAsync(ct);
        string slug;
        if (app.IsSystem && !taken.Contains(app.Slug)) slug = app.Slug;
        else
        {
            var n = 1;
            do slug = $"{app.Slug}-{n++:00}"; while (taken.Contains(slug));
        }
        return new AppInstance
        {
            AppId = app.Id,
            App = app,
            Name = slug,
            Slug = slug,
            InstallPath = Path.Combine(settings.Get().AppsRoot, slug),
        };
    }

    public async Task<Result<AppInstance>> SaveAsync(AppInstance input, string? user, CancellationToken ct = default)
    {
        var s = settings.Get();
        var errors = new List<string>();
        input.Name = input.Name.Trim();
        if (string.IsNullOrWhiteSpace(input.Name)) errors.Add("Instance name is required.");
        input.Slug = string.IsNullOrWhiteSpace(input.Slug) ? Slug.From(input.Name) : input.Slug.Trim();
        if (!Slug.IsValid(input.Slug)) errors.Add("Slug may only contain lowercase letters, digits and dashes (max 48).");
        if (string.IsNullOrWhiteSpace(input.InstallPath)) input.InstallPath = Path.Combine(s.AppsRoot, input.Slug);
        var resolved = PathGuard.ResolveInsideAny(s.AllowedRoots, input.InstallPath.Trim());
        if (resolved is null)
            errors.Add(s.AdoptedRoots.Count == 0
                ? $"Install folder must be inside the apps root ({s.AppsRoot})."
                : $"Install folder must be inside the apps root ({s.AppsRoot}) or an adopted root ({string.Join(", ", s.AdoptedRoots)}).");
        else input.InstallPath = resolved;
        var dataDir = options.Value.DataDir.TrimEnd('/');
        if (resolved is not null && (resolved == dataDir || PathGuard.IsStrictlyInside(resolved, dataDir) || PathGuard.IsStrictlyInside(dataDir, resolved)))
            errors.Add("Install folder must not overlap the v-panel data folder.");
        if (input.ComposeProjectName is { Length: > 0 } project && !Slug.IsValidProjectName(project))
            errors.Add("Compose project name may only contain lowercase letters, digits, '-' and '_'.");
        if (input.ComposeFileName is { Length: > 0 } file && (file.Contains('/') || file.Contains('\\') || file.StartsWith('.')))
            errors.Add("Compose file name must be a plain file name.");
        errors.AddRange(Proxy.CaddySiteParser.ValidateDirectives(input.ProxyDirectives, proxyOptions: false));
        errors.AddRange(Proxy.CaddySiteParser.ValidateDirectives(input.ProxyOptions, proxyOptions: true));
        if (!string.IsNullOrWhiteSpace(input.PublicUrl) && !UrlHelper.TryParse(input.PublicUrl, out _))
            errors.Add("Public URL is not a valid http(s) URL.");
        if (input.ProxyPort is < 1 or > 65535) errors.Add("Proxy port must be between 1 and 65535.");
        if (errors.Count > 0) return Result<AppInstance>.Fail(errors);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var others = await db.Instances.AsNoTracking().Where(i => i.Id != input.Id && i.Status != InstanceStatus.Removed).ToListAsync(ct);
        if (await db.Instances.AnyAsync(i => i.Id != input.Id && i.Slug == input.Slug, ct))
            errors.Add($"Another instance already uses slug '{input.Slug}'.");
        if (others.Any(o => o.ProjectName == input.ProjectName))
            errors.Add($"Another instance already uses compose project '{input.ProjectName}'.");
        if (others.Any(o => string.Equals(o.InstallPath.TrimEnd('/'), input.InstallPath.TrimEnd('/'), StringComparison.Ordinal)
                            || PathGuard.IsStrictlyInside(o.InstallPath, input.InstallPath) || PathGuard.IsStrictlyInside(input.InstallPath, o.InstallPath)))
            errors.Add("Install folder overlaps with another instance.");
        if (!string.IsNullOrWhiteSpace(input.PublicUrl) && UrlHelper.TryParse(input.PublicUrl, out var mine))
        {
            var key = UrlKey(mine);
            var clash = others.FirstOrDefault(o => UrlHelper.TryParse(o.PublicUrl, out var u) && UrlKey(u) == key);
            if (clash is not null) errors.Add($"Public URL is already used by instance '{clash.Name}'.");
        }
        if (errors.Count > 0) return Result<AppInstance>.Fail(errors);

        AppInstance entity;
        if (input.Id == 0)
        {
            entity = new AppInstance { AppId = input.AppId, Status = InstanceStatus.Draft };
            db.Instances.Add(entity);
        }
        else
        {
            entity = await db.Instances.FirstOrDefaultAsync(i => i.Id == input.Id, ct) ?? throw new KeyNotFoundException();
            var deployed = entity.DeployedConfigHash is not null && entity.Status != InstanceStatus.Removed;
            if (deployed && entity.ProjectName != input.ProjectName)
                return Result<AppInstance>.Fail("The compose project name cannot change after deploy (slug of v-panel instances). Remove the instance first.");
        }
        entity.ComposeProjectName = string.IsNullOrWhiteSpace(input.ComposeProjectName) ? null : input.ComposeProjectName.Trim();
        entity.ComposeFileName = string.IsNullOrWhiteSpace(input.ComposeFileName) ? null : input.ComposeFileName.Trim();
        entity.BuildMode = input.BuildMode;
        entity.TlsInternal = input.TlsInternal;
        entity.ProxyDirectives = string.IsNullOrWhiteSpace(input.ProxyDirectives) ? null : AppService.Normalize(input.ProxyDirectives).TrimEnd() + "\n";
        entity.ProxyOptions = string.IsNullOrWhiteSpace(input.ProxyOptions) ? null : AppService.Normalize(input.ProxyOptions).TrimEnd() + "\n";
        entity.Name = input.Name;
        entity.Slug = input.Slug;
        entity.Description = input.Description?.Trim();
        entity.PublicUrl = string.IsNullOrWhiteSpace(input.PublicUrl) ? null : input.PublicUrl.Trim();
        entity.InstallPath = input.InstallPath;
        entity.EnvOverrides = AppService.Normalize(input.EnvOverrides);
        entity.ComposeOverride = string.IsNullOrWhiteSpace(input.ComposeOverride) ? null : AppService.Normalize(input.ComposeOverride);
        entity.ProxyService = string.IsNullOrWhiteSpace(input.ProxyService) ? null : input.ProxyService.Trim();
        entity.ProxyPort = input.ProxyPort;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(user, input.Id == 0 ? "instance.create" : "instance.update", entity.Slug, ct: ct);
        return Result<AppInstance>.Ok(entity);
    }

    public async Task<Result<bool>> DeleteAsync(int id, string? user, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var inst = await db.Instances.FirstOrDefaultAsync(i => i.Id == id, ct);
        if (inst is null) return Result<bool>.Fail("Instance not found.");
        // An imported stack never deployed by v-panel can be forgotten: v-panel has changed nothing on it yet.
        if (inst.Status is not (InstanceStatus.Draft or InstanceStatus.Removed) && inst.DeployedConfigHash is not null && !inst.IsAwaitingFirstDeploy)
            return Result<bool>.Fail("Instance is deployed. Run 'Remove' first to stop its containers.");
        if (await db.Deployments.AnyAsync(d => d.InstanceId == id && (d.Status == DeploymentStatus.Queued || d.Status == DeploymentStatus.Running), ct))
            return Result<bool>.Fail("A deployment job is in progress.");
        db.Instances.Remove(inst);
        // The template created by an import belongs to that stack only: forgetting the stack removes it too.
        if (inst.IsImported && !await db.Instances.AnyAsync(i => i.AppId == inst.AppId && i.Id != inst.Id, ct))
            db.Apps.Remove(await db.Apps.FirstAsync(a => a.Id == inst.AppId, ct));
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(user, "instance.delete", inst.Slug, ct: ct);
        return Result<bool>.Ok(true);
    }

    private static string UrlKey(Uri u) => $"{u.Scheme}://{u.IdnHost.ToLowerInvariant()}:{u.Port}{UrlHelper.PathPrefix(u).ToLowerInvariant()}";
}
