using VPanel.Core.Configuration;
using VPanel.Core.Domain;

namespace VPanel.Core.Abstractions;

public sealed record ValidationIssue(string Message, int? Line = null, int? Column = null, bool IsWarning = false)
{
    public override string ToString() => Line is null ? Message : $"Line {Line}: {Message}";
}

public sealed record ValidationReport(IReadOnlyList<ValidationIssue> Issues)
{
    public static readonly ValidationReport Ok = new([]);
    public bool IsValid => Issues.All(i => i.IsWarning);
    public IEnumerable<ValidationIssue> Errors => Issues.Where(i => !i.IsWarning);
    public IEnumerable<ValidationIssue> Warnings => Issues.Where(i => i.IsWarning);
}

/// <summary>Everything a handler needs to run one operation for one instance.</summary>
public sealed record InstanceOperationContext(
    AppInstance Instance,
    PanelSettings Settings,
    IDeploymentLog Log,
    string? PanelBaseUrl = null);

public sealed record OperationResult(bool Success, InstanceStatus NewStatus, string? Error = null, string? ConfigHash = null,
    string? RenderedCompose = null, string? RenderedEnv = null, string? ProxyAlias = null)
{
    public static OperationResult Fail(string error, InstanceStatus status = InstanceStatus.Failed) => new(false, status, error);
}

public sealed record ContainerInfo(string Name, string Service, string State, string Status, string Image, string Ports);

public sealed record RuntimeState(string ProjectName, InstanceStatus Status, string Detail);

/// <summary>Option bag for destructive operations.</summary>
public sealed record RemoveOptions(bool DeleteVolumes = false, bool DeleteFiles = false);

/// <summary>
/// Engine for one <see cref="AppType"/>. v1 ships the docker compose handler; new kinds of apps
/// (Dockerfile build, static site, systemd service...) are added by implementing this interface and
/// registering it as a keyed service.
/// </summary>
public interface IAppTypeHandler
{
    AppType Type { get; }

    /// <summary>Quick, offline validation used by the editor (no side effects).</summary>
    ValidationReport Validate(string compose, string env);

    /// <summary>Hash of the fully rendered configuration, used to detect undeployed changes.</summary>
    string ComputeConfigHash(AppInstance instance, PanelSettings settings);

    Task<OperationResult> ExecuteAsync(DeploymentAction action, InstanceOperationContext context, RemoveOptions? removeOptions, CancellationToken ct);

    Task<IReadOnlyList<ContainerInfo>> GetContainersAsync(AppInstance instance, PanelSettings settings, CancellationToken ct);

    /// <summary>Streams container logs; completes when cancelled (follow) or when output ends.</summary>
    Task StreamLogsAsync(AppInstance instance, PanelSettings settings, int tail, bool follow, Action<string> onLine, CancellationToken ct);

    /// <summary>Runtime status for all projects managed by this handler, keyed by project name.</summary>
    Task<IReadOnlyDictionary<string, RuntimeState>> GetRuntimeStatesAsync(CancellationToken ct);
}
