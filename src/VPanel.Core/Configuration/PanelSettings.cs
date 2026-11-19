namespace VPanel.Core.Configuration;

/// <summary>Static configuration bound from appsettings ("VPanel" section).</summary>
public class VPanelOptions
{
    public const string Section = "VPanel";

    /// <summary>Directory for the SQLite database and data-protection keys.</summary>
    public string DataDir { get; set; } = "/var/lib/vpanel";

    /// <summary>Path to the docker CLI.</summary>
    public string DockerBinary { get; set; } = "docker";

    /// <summary>Defaults for runtime settings (editable in the UI, stored in DB).</summary>
    public PanelSettings Defaults { get; set; } = new();

    /// <summary>Seed the shared infrastructure app (Caddy + Postgres) on first start.</summary>
    public bool SeedInfrastructure { get; set; } = true;

    /// <summary>Seed an example app template (whoami) on first start.</summary>
    public bool SeedExamples { get; set; } = true;

    /// <summary>Max deployment jobs running concurrently (jobs for the same instance are always serialized).</summary>
    public int MaxConcurrentDeployments { get; set; } = 2;

    /// <summary>Interval for syncing instance status from Docker.</summary>
    public TimeSpan StatusSyncInterval { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>Runtime settings (defaults from config, overridable from the Settings page).</summary>
public record PanelSettings
{
    /// <summary>Root folder; every instance InstallPath must live below it.</summary>
    public string AppsRoot { get; init; } = "/opt/vpanel/apps";

    /// <summary>External docker network shared by Caddy, Postgres and app services.</summary>
    public string SharedNetwork { get; init; } = "vpanel-shared";

    public bool CaddyEnabled { get; init; } = true;

    /// <summary>
    /// How public traffic reaches Caddy. <see cref="IngressMode.Direct"/>: Caddy publishes 80/443 and obtains certificates.
    /// <see cref="IngressMode.CloudflareTunnel"/>: cloudflared forwards plain HTTP to Caddy; TLS ends at Cloudflare.
    /// </summary>
    public IngressMode Ingress { get; init; } = IngressMode.Direct;

    /// <summary>Name of the Caddy container (used for docker exec validate/reload).</summary>
    public string CaddyContainer { get; init; } = "caddy";

    /// <summary>Host folder mounted into the Caddy container at <see cref="CaddyConfigDirInContainer"/>.</summary>
    public string CaddyDir { get; init; } = "/opt/vpanel/caddy";

    public string CaddyConfigDirInContainer { get; init; } = "/etc/caddy";

    /// <summary>Optional public URL for v-panel itself (adds a Caddy route).</summary>
    public string? PanelPublicUrl { get; init; }

    /// <summary>Upstream used for the v-panel route, as seen from the Caddy container.</summary>
    public string PanelUpstream { get; init; } = "host.docker.internal:5080";

    public string CaddySitesDir => Path.Combine(CaddyDir, "sites");
    public string CaddyMainFile => Path.Combine(CaddyDir, "Caddyfile");
    public string CaddyMainFileInContainer => CaddyConfigDirInContainer.TrimEnd('/') + "/Caddyfile";
}

/// <summary>How public traffic reaches the shared Caddy container.</summary>
public enum IngressMode
{
    /// <summary>Caddy publishes ports 80/443 on the host and manages HTTPS certificates itself.</summary>
    Direct = 0,

    /// <summary>cloudflared (on the shared network) forwards to http://caddy:80; Caddy serves plain HTTP, publishes no ports.</summary>
    CloudflareTunnel = 1,
}
