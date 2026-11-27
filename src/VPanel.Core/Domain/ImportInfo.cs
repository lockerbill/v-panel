using System.Text.Json;

namespace VPanel.Core.Domain;

/// <summary>A hand-written Caddy site block that v-panel removes when it starts serving the same address.</summary>
public sealed record ManualSiteRef(string File, string Address);

/// <summary>Where an imported stack came from (stored as JSON on <see cref="AppInstance.ImportInfoJson"/>).</summary>
public sealed record ImportInfo
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public DateTimeOffset ImportedAt { get; init; }
    public string? ImportedBy { get; init; }

    /// <summary>Compose files the stack was started with (absolute host paths).</summary>
    public IReadOnlyList<string> OriginalComposeFiles { get; init; } = [];

    /// <summary>Env file the stack was started with, if any.</summary>
    public string? OriginalEnvFile { get; init; }

    /// <summary>Copy of the original files taken before v-panel wrote anything.</summary>
    public string? BackupDir { get; init; }

    /// <summary>Hand-written Caddy site removed on the first successful deploy (v-panel then serves the address).</summary>
    public ManualSiteRef? PendingTakeover { get; init; }

    public static ImportInfo? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<ImportInfo>(json, Json); }
        catch (JsonException) { return null; }
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);
}
