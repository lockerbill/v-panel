namespace VPanel.Core.Domain;

/// <summary>Kind of application. Each value maps to an <see cref="Abstractions.IAppTypeHandler"/>.</summary>
public enum AppType
{
    DockerCompose = 0,
}

public enum InstanceStatus
{
    Draft = 0,
    Deploying = 1,
    Running = 2,
    Stopped = 3,
    Failed = 4,
    Removed = 5,
    Partial = 6,
}

public enum DeploymentAction
{
    Deploy = 0,
    Start = 1,
    Stop = 2,
    Restart = 3,
    Down = 4,
    Remove = 5,
}

public enum DeploymentStatus
{
    Queued = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    Cancelled = 4,
}

public enum VolumeKind
{
    Named = 0,
    Bind = 1,
    External = 2,
}

public enum UserRole
{
    Admin = 0,
}

/// <summary>How a deploy obtains images.</summary>
public enum BuildMode
{
    /// <summary>Pull images of services without <c>build:</c>, then <c>up --build</c> (always rebuilds buildable services).</summary>
    Build = 0,

    /// <summary>
    /// Pull every image (pull failures ignored), then <c>up</c> without <c>--build</c>: services with both <c>image:</c> and
    /// <c>build:</c> use the published image, and only images that are still missing are built.
    /// </summary>
    PullFirst = 1,
}
