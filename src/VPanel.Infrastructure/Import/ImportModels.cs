using VPanel.Core.Configuration;
using VPanel.Core.Domain;
using VPanel.Infrastructure.Proxy;

namespace VPanel.Infrastructure.Import;

/// <summary>A mount of a container or compose service (bind or volume).</summary>
public sealed record MountRef(string Type, string Source, string Target);

/// <summary>Runtime view of one compose-managed container (from <c>docker inspect</c>).</summary>
public sealed record ContainerSnapshot(
    string Id,
    string Name,
    string Project,
    string Service,
    string State,
    int? ExitCode,
    string? Image,
    string? WorkingDir,
    IReadOnlyList<string> ConfigFiles,
    IReadOnlyList<string> EnvironmentFiles,
    string? ConfigHash,
    bool OneOff,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Networks,
    IReadOnlyList<MountRef> Mounts,
    IReadOnlyList<int> PublishedContainerPorts);

/// <summary>A service of a scanned stack joining a docker network (resolved docker name + aliases).</summary>
public sealed record ServiceNetworkRef(string Key, string Name, IReadOnlyList<string> Aliases);

/// <summary>Service of a scanned stack, from the resolved compose model (or from running containers as a fallback).</summary>
public sealed record StackService(
    string Name,
    string? Image,
    bool HasBuild,
    string? BuildContext,
    string? ContainerName,
    string? NetworkMode,
    IReadOnlyList<ServiceNetworkRef> Networks,
    IReadOnlyList<int> ContainerPorts,
    IReadOnlyList<int> PublishedPorts,
    IReadOnlyList<MountRef> Mounts)
{
    /// <summary>Image name without registry, path and tag (e.g. "caddy" for docker.io/library/caddy:2).</summary>
    public string? ImageBase
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Image)) return null;
            var name = Image.Split('@')[0];
            var slash = name.LastIndexOf('/');
            if (slash >= 0) name = name[(slash + 1)..];
            var colon = name.IndexOf(':');
            return (colon >= 0 ? name[..colon] : name).ToLowerInvariant();
        }
    }

    /// <summary>Image repository without tag (e.g. "cloudflare/cloudflared").</summary>
    public string? ImageRepository
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Image)) return null;
            var name = Image.Split('@')[0];
            var lastSlash = name.LastIndexOf('/');
            var colon = name.IndexOf(':', lastSlash + 1);
            return (colon >= 0 ? name[..colon] : name).ToLowerInvariant();
        }
    }

    public ServiceNetworkRef? On(string network) => Networks.FirstOrDefault(n => n.Name == network);
}

public enum StackKind
{
    App = 0,

    /// <summary>Shared infrastructure: the stack running the reverse proxy (Caddy).</summary>
    Infra = 1,
}

/// <summary>An existing compose stack found on the server and not managed by v-panel yet.</summary>
public sealed record ScannedStack
{
    public required string ProjectName { get; init; }
    public string? WorkingDir { get; init; }
    public IReadOnlyList<string> ConfigFiles { get; init; } = [];
    public string? EnvFile { get; init; }
    public StackKind Kind { get; init; }
    public IReadOnlyList<StackService> Services { get; init; } = [];
    public IReadOnlyList<ContainerSnapshot> Containers { get; init; } = [];

    /// <summary>Status summarised from the containers (Stopped when there are none).</summary>
    public InstanceStatus RuntimeStatus { get; init; }

    /// <summary>The compose files can be read from v-panel (in container mode: their folder is mounted).</summary>
    public bool Accessible { get; init; }

    /// <summary>Install path accepted by v-panel (inside the apps root or an adopted root).</summary>
    public bool InAllowedRoot { get; init; }

    /// <summary>Folder to mount / add to AdoptedRoots so v-panel can manage the stack (covers its folder and build contexts).</summary>
    public string? SuggestedRoot { get; init; }

    public string? ConfigError { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];

    // Suggestions for the import form
    public string SuggestedSlug { get; init; } = "";
    public string? ProxyService { get; init; }
    public int? ProxyPort { get; init; }
    public string? PublicUrl { get; init; }
    public bool TlsInternal { get; init; }
    public BuildMode SuggestedBuildMode { get; init; }

    /// <summary>Hand-written Caddy site routing to this stack (taken over on the first deploy when simple).</summary>
    public CaddySiteBlock? ManualSite { get; init; }

    public bool CanImport => Accessible && InAllowedRoot && WorkingDir is not null && ConfigFiles.Count > 0;
}

/// <summary>Settings derived from an existing shared-infrastructure stack.</summary>
public sealed record InfraDetection
{
    public required string ProjectName { get; init; }
    public string? CaddyService { get; init; }
    public string? CaddyContainer { get; init; }
    public bool CaddyRunning { get; init; }

    /// <summary>Host folder mounted as the Caddy configuration folder.</summary>
    public string? CaddyDir { get; init; }
    public string? CaddyConfigDirInContainer { get; init; }

    /// <summary>Only the Caddyfile itself is mounted (not its folder): v-panel's site files would not be visible to Caddy.</summary>
    public bool CaddySingleFileMount { get; init; }
    public string? SharedNetwork { get; init; }
    public IngressMode Ingress { get; init; }
    public string? PostgresService { get; init; }
    public string? PostgresNetwork { get; init; }
    public IReadOnlyList<string> PostgresAliases { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Current settings with the detected values applied.</summary>
    public PanelSettings ApplyTo(PanelSettings s) => s with
    {
        SharedNetwork = SharedNetwork ?? s.SharedNetwork,
        CaddyEnabled = CaddyService is not null || s.CaddyEnabled,
        CaddyContainer = CaddyContainer ?? s.CaddyContainer,
        CaddyDir = CaddyDir ?? s.CaddyDir,
        CaddyConfigDirInContainer = CaddyConfigDirInContainer ?? s.CaddyConfigDirInContainer,
        Ingress = CaddyService is not null ? Ingress : s.Ingress,
    };
}

public sealed record ScanResult(
    bool DockerReachable,
    string? Error,
    IReadOnlyList<ScannedStack> Stacks,
    InfraDetection? Infra,
    bool InfraManaged,
    IReadOnlyList<CaddySiteBlock> ManualSites,
    IReadOnlyList<string> ManagedProjects,
    IReadOnlyList<string> Warnings)
{
    public ScannedStack? Find(string project) => Stacks.FirstOrDefault(s => s.ProjectName == project);
}

/// <summary>What to import and how (defaults come from the scan).</summary>
public sealed record ImportRequest
{
    public required string ProjectName { get; init; }
    public string? AppName { get; init; }
    public string? Slug { get; init; }
    public string? ProxyService { get; init; }
    public int? ProxyPort { get; init; }
    public string? PublicUrl { get; init; }
    public bool? TlsInternal { get; init; }
    public BuildMode? BuildMode { get; init; }

    /// <summary>Replace the hand-written Caddy site for the public URL by a generated one on the first deploy.</summary>
    public bool TakeOverSite { get; init; } = true;
}

public sealed record ImportOutcome(AppInstance Instance, IReadOnlyList<string> Notes);

/// <summary>Effect of the first v-panel deploy on one service (compose config hash of the running container vs. the rendered one).</summary>
public sealed record ServiceChange(string Service, string? CurrentHash, string? NextHash)
{
    public string Action => CurrentHash is null ? "create" : NextHash is null ? "remove" : CurrentHash == NextHash ? "keep" : "recreate";
}

public sealed record DeployPreview(bool Success, string? Error, IReadOnlyList<ServiceChange> Services)
{
    public bool RecreatesAnything => Services.Any(s => s.Action != "keep");
}
