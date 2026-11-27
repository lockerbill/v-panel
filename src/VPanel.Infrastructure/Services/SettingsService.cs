using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using VPanel.Core.Configuration;
using VPanel.Core.Domain;
using VPanel.Infrastructure.Data;

namespace VPanel.Infrastructure.Services;

/// <summary>Runtime settings: config defaults overridden by values saved from the Settings page.</summary>
public sealed class SettingsService(IDbContextFactory<VPanelDbContext> dbFactory, IOptions<VPanelOptions> options)
{
    private const string Key = "panel";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private PanelSettings? _cache;

    public PanelSettings Defaults => options.Value.Defaults with { AdoptedRoots = options.Value.AdoptedRoots };

    public PanelSettings Get()
    {
        if (_cache is not null) return _cache;
        using var db = dbFactory.CreateDbContext();
        var row = db.Settings.AsNoTracking().FirstOrDefault(s => s.Key == Key);
        PanelSettings settings = Defaults;
        if (row is not null)
        {
            try { settings = JsonSerializer.Deserialize<PanelSettings>(row.Value, Json) ?? Defaults; }
            catch (JsonException) { settings = Defaults; }
        }
        return _cache = settings with { AdoptedRoots = options.Value.AdoptedRoots };
    }

    public async Task SaveAsync(PanelSettings settings, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.Settings.FirstOrDefaultAsync(s => s.Key == Key, ct);
        var json = JsonSerializer.Serialize(settings, Json);
        if (row is null) db.Settings.Add(new Setting { Key = Key, Value = json });
        else { row.Value = json; row.UpdatedAt = DateTimeOffset.UtcNow; }
        await db.SaveChangesAsync(ct);
        _cache = settings with { AdoptedRoots = options.Value.AdoptedRoots };
    }

    public IReadOnlyList<string> Validate(PanelSettings s)
    {
        var errors = new List<string>();
        if (!Path.IsPathRooted(s.AppsRoot)) errors.Add("Apps root must be an absolute path.");
        if (!Path.IsPathRooted(s.CaddyDir)) errors.Add("Caddy folder must be an absolute path.");
        if (string.IsNullOrWhiteSpace(s.SharedNetwork) || !System.Text.RegularExpressions.Regex.IsMatch(s.SharedNetwork, "^[a-zA-Z0-9][a-zA-Z0-9_.-]*$"))
            errors.Add("Shared network name is invalid.");
        if (s.CaddyEnabled && string.IsNullOrWhiteSpace(s.CaddyContainer)) errors.Add("Caddy container name is required.");
        if (!string.IsNullOrWhiteSpace(s.PanelPublicUrl) && !Core.Utilities.UrlHelper.TryParse(s.PanelPublicUrl, out _))
            errors.Add("Panel public URL is invalid.");
        return errors;
    }
}
