namespace VPanel.Core.Domain;

public abstract class Entity
{
    public int Id { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class User : Entity
{
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public UserRole Role { get; set; } = UserRole.Admin;
    public DateTimeOffset? LastLoginAt { get; set; }
}

/// <summary>
/// An application template: compose file + default environment. Deployed through one or more
/// <see cref="AppInstance"/>s (e.g. procurement → procurement-01, procurement-02).
/// </summary>
public class AppDefinition : Entity
{
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public string? Description { get; set; }
    public AppType Type { get; set; } = AppType.DockerCompose;

    /// <summary>docker-compose.yml content shared by all instances (unless overridden).</summary>
    public string ComposeTemplate { get; set; } = "";

    /// <summary>Default .env content (encrypted at rest).</summary>
    public string EnvTemplate { get; set; } = "";

    /// <summary>Default compose service that receives HTTP traffic from the reverse proxy.</summary>
    public string? DefaultProxyService { get; set; }
    public int? DefaultProxyPort { get; set; }

    /// <summary>Infrastructure app (shared Caddy/Postgres...). Shown separately and protected from accidental deletion.</summary>
    public bool IsSystem { get; set; }

    public List<AppVolume> Volumes { get; set; } = [];
    public List<AppInstance> Instances { get; set; } = [];
}

/// <summary>Volume declared manually as app metadata (in addition to the ones parsed from compose).</summary>
public class AppVolume : Entity
{
    public int AppId { get; set; }
    public AppDefinition? App { get; set; }
    public string Name { get; set; } = "";
    public VolumeKind Kind { get; set; } = VolumeKind.External;
    public string? Description { get; set; }
}

public class AppInstance : Entity
{
    public const string LocalServer = "local";

    public int AppId { get; set; }
    public AppDefinition? App { get; set; }

    public string Name { get; set; } = "";

    /// <summary>Unique, DNS-safe identifier. Compose project name is <c>vp-{Slug}</c>.</summary>
    public string Slug { get; set; } = "";
    public string? Description { get; set; }

    /// <summary>Public URL (free text), e.g. https://procurement-01.example.com. Used for reverse proxy routing.</summary>
    public string? PublicUrl { get; set; }

    /// <summary>Folder on the server holding docker-compose.yml and .env. Must be under AppsRoot.</summary>
    public string InstallPath { get; set; } = "";

    /// <summary>.env lines that override the app template (encrypted at rest).</summary>
    public string EnvOverrides { get; set; } = "";

    /// <summary>Optional full compose override; null means use the app template.</summary>
    public string? ComposeOverride { get; set; }

    /// <summary>Service/port receiving HTTP traffic. Null falls back to app defaults.</summary>
    public string? ProxyService { get; set; }
    public int? ProxyPort { get; set; }

    public InstanceStatus Status { get; set; } = InstanceStatus.Draft;
    public string? StatusDetail { get; set; }

    /// <summary>Hash of the rendered configuration at last successful deploy.</summary>
    public string? DeployedConfigHash { get; set; }
    public DateTimeOffset? LastDeployedAt { get; set; }

    /// <summary>Target server; only "local" in v1 (reserved for multi-server support).</summary>
    public string ServerId { get; set; } = LocalServer;

    public List<Deployment> Deployments { get; set; } = [];

    public string ProjectName => $"vp-{Slug}";
    public string? EffectiveProxyService => string.IsNullOrWhiteSpace(ProxyService) ? App?.DefaultProxyService : ProxyService;
    public int? EffectiveProxyPort => ProxyPort ?? App?.DefaultProxyPort;
    public string EffectiveCompose => ComposeOverride ?? App?.ComposeTemplate ?? "";
}

public class Deployment : Entity
{
    public int InstanceId { get; set; }
    public AppInstance? Instance { get; set; }
    public DeploymentAction Action { get; set; }
    public DeploymentStatus Status { get; set; } = DeploymentStatus.Queued;
    public string? TriggeredBy { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string Log { get; set; } = "";
    public string? ErrorMessage { get; set; }

    /// <summary>Action parameters as JSON (e.g. remove options).</summary>
    public string? Parameters { get; set; }

    /// <summary>Snapshot of rendered files at deploy time (basis for future rollback).</summary>
    public string? ComposeSnapshot { get; set; }
    public string? EnvSnapshot { get; set; }
    public string? ConfigHash { get; set; }

    public TimeSpan? Duration => StartedAt is { } s && FinishedAt is { } f ? f - s : null;
}

/// <summary>
/// Bearer token for machine access (MCP endpoint). Only a SHA-256 hash of the secret is stored; the secret is shown once at creation.
/// </summary>
public class ApiToken : Entity
{
    public string Name { get; set; } = "";
    public string TokenHash { get; set; } = "";

    /// <summary>First characters of the secret, to recognise a token without revealing it.</summary>
    public string Prefix { get; set; } = "";
    public string? CreatedBy { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }

    public bool IsExpired(DateTimeOffset now) => ExpiresAt is { } e && e <= now;
}

public class Setting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class AuditLog
{
    public long Id { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public string? User { get; set; }
    public string Action { get; set; } = "";
    public string? Target { get; set; }
    public string? Detail { get; set; }
}
