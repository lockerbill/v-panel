using VPanel.Core.Abstractions;
using VPanel.Core.Domain;
using VPanel.Infrastructure.Docker;

namespace VPanel.Web.Mcp;

// Shapes returned by the MCP tools. They are plain records (never EF entities) so the JSON stays small and stable.

public sealed record AppListItem(int Id, string Slug, string Name, string? Description, bool IsSystem, int Instances, int Running,
    string? DefaultProxyService, int? DefaultProxyPort);

public sealed record AppInstanceRef(int Id, string Slug, string Name, string Status, string? PublicUrl);

public sealed record AppVolumeDto(string Name, string Kind, string? Description);

public sealed record AppDetail(int Id, string Slug, string Name, string? Description, string Type, bool IsSystem,
    string? DefaultProxyService, int? DefaultProxyPort, string Compose, string Env,
    IReadOnlyList<AppVolumeDto> Volumes, IReadOnlyList<AppInstanceRef> Instances)
{
    public static AppDetail From(AppDefinition a) => new(a.Id, a.Slug, a.Name, a.Description, a.Type.ToString(), a.IsSystem,
        a.DefaultProxyService, a.DefaultProxyPort, a.ComposeTemplate, a.EnvTemplate,
        a.Volumes.Select(v => new AppVolumeDto(v.Name, v.Kind.ToString(), v.Description)).ToList(),
        a.Instances.OrderBy(i => i.Name).Select(i => new AppInstanceRef(i.Id, i.Slug, i.Name, i.Status.ToString(), i.PublicUrl)).ToList());
}

public sealed record SaveAppResult(AppDetail App, ComposeReport Validation);

public sealed record ServiceDto(string Name, string? Image, bool HasBuild, IReadOnlyList<string> PublishedPorts, IReadOnlyList<string> Expose,
    string? NetworkMode);

public sealed record VolumeDto(string Name, string Kind, string? Service, string? Target);

public sealed record ComposeReport(bool Valid, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings, IReadOnlyList<ServiceDto> Services,
    IReadOnlyList<VolumeDto> Volumes, IReadOnlyList<string> ExternalNetworks)
{
    public static ComposeReport From(ComposeAnalysis analysis, IEnumerable<ValidationIssue>? extraWarnings = null)
    {
        var warnings = analysis.Issues.Where(i => i.IsWarning).Select(i => i.ToString()).ToList();
        if (extraWarnings is not null) warnings.AddRange(extraWarnings.Select(i => i.ToString()));
        return new ComposeReport(analysis.IsValid,
            analysis.Issues.Where(i => !i.IsWarning).Select(i => i.ToString()).ToList(),
            warnings,
            analysis.Services.Select(s => new ServiceDto(s.Name, s.Image, s.HasBuild, s.Ports, s.ExposedPorts, s.NetworkMode)).ToList(),
            analysis.Volumes.Select(v => new VolumeDto(v.Name, v.Kind.ToString(), v.Service, v.Target)).ToList(),
            analysis.ExternalNetworks);
    }
}

public sealed record InstanceListItem(int Id, string Slug, string Name, string App, string Status, string? StatusDetail, string? PublicUrl,
    bool HasUndeployedChanges, DateTimeOffset? LastDeployedAt);

public sealed record ContainerDto(string Name, string Service, string State, string Status, string Image, string Ports);

public sealed record DeploymentListItem(int Id, string Instance, string Action, string Status, string? TriggeredBy, DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, string? Error)
{
    public static DeploymentListItem From(Deployment d) => new(d.Id, d.Instance?.Slug ?? d.InstanceId.ToString(), d.Action.ToString(),
        d.Status.ToString(), d.TriggeredBy, d.CreatedAt, d.StartedAt, d.FinishedAt, d.ErrorMessage);
}

public sealed record InstanceDetail(int Id, string Slug, string Name, string? Description, string App, string AppSlug, string Status,
    string? StatusDetail, string? PublicUrl, string InstallPath, string ProjectName, string? ProxyService, int? ProxyPort,
    string? EffectiveProxyService, int? EffectiveProxyPort, string? ProxyAlias, string EnvOverrides, string? ComposeOverride,
    bool HasUndeployedChanges, DateTimeOffset? LastDeployedAt, IReadOnlyList<ContainerDto>? Containers,
    IReadOnlyList<DeploymentListItem> RecentDeployments, string BuildMode, bool TlsInternal, bool Imported, bool AwaitingFirstDeploy,
    string? PendingCaddyTakeover, string? ProxyDirectives, string? ProxyOptions);

public sealed record ScannedServiceDto(string Name, string? Image, bool HasBuild, IReadOnlyList<string> Networks, IReadOnlyList<int> Ports);

public sealed record ScannedStackDto(string Project, string Kind, string? Folder, IReadOnlyList<string> ComposeFiles, string Status,
    bool CanImport, bool Accessible, bool InAllowedRoot, string? SuggestedRoot, IReadOnlyList<ScannedServiceDto> Services,
    string SuggestedSlug, string? ProxyService, int? ProxyPort, string? PublicUrl, bool TlsInternal, string SuggestedBuildMode,
    string? CaddySite, bool CaddySiteCanBeTakenOver, string? ProxyDirectives, string? ProxyOptions, string? TlsNote, string? ConfigError,
    IReadOnlyList<string> Warnings);

public sealed record InfraDto(string Project, string? SharedNetwork, string? CaddyContainer, bool CaddyRunning, string? CaddyDir,
    string? CaddyConfigDirInContainer, string Ingress, string? Postgres, IReadOnlyList<string> Warnings);

public sealed record CaddySiteDto(string Address, string? Upstream, string File, int Line, bool TlsInternal, bool CanBeTakenOver, string? Reason,
    string? Directives, string? ProxyOptions);

public sealed record ScanDto(bool DockerReachable, string? Error, InfraDto? Infra, bool InfraManaged, bool ImportInfraFirst,
    IReadOnlyList<ScannedStackDto> Stacks, IReadOnlyList<CaddySiteDto> CaddySites, IReadOnlyList<string> ManagedProjects,
    IReadOnlyList<string> AdoptedRoots, IReadOnlyList<string> Warnings);

public sealed record ImportResultDto(InstanceDetail Instance, IReadOnlyList<string> Notes);

public sealed record ServiceChangeDto(string Service, string Action);

public sealed record DeployPreviewDto(bool Success, string? Error, bool RecreatesAnything, IReadOnlyList<ServiceChangeDto> Services);

public sealed record DeploymentDetail(int Id, string Instance, string Action, string Status, bool Finished, string? Error, string? TriggeredBy,
    DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, string? InstanceStatus, string? PublicUrl,
    string LogTail, string? Hint);

public sealed record ManagedVariableDto(string Name, string Description);

public sealed record SharedServiceDto(string Host, int Port, string Description);

public sealed record PanelInfo(string Version, string AppsRoot, string SharedNetwork, string Ingress, bool CaddyEnabled, string? PanelPublicUrl,
    IReadOnlyList<ManagedVariableDto> ManagedVariables, IReadOnlyList<SharedServiceDto> SharedServices,
    IReadOnlyList<InstanceListItem> SystemInstances, IReadOnlyList<string> Notes);
