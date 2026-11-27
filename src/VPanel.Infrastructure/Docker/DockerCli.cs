using Microsoft.Extensions.Options;
using VPanel.Core.Abstractions;
using VPanel.Core.Configuration;

namespace VPanel.Infrastructure.Docker;

/// <summary>Thin wrapper building docker CLI invocations.</summary>
public sealed class DockerCli(IProcessRunner runner, IOptions<VPanelOptions> options)
{
    private string Binary => options.Value.DockerBinary;

    public Task<ProcessResult> RunAsync(IEnumerable<string> args, Action<string>? onOutput = null, string? workingDir = null,
        TimeSpan? timeout = null, CancellationToken ct = default) =>
        runner.RunAsync(new ProcessSpec(Binary, args.ToList()) { WorkingDirectory = workingDir, Timeout = timeout }, onOutput, ct);

    public static List<string> ComposeArgs(string project, string dir, bool withOverride, params string[] tail) =>
        ComposeArgs(project, dir, RenderedInstance.ComposeFile, withOverride, tail);

    /// <param name="composeFile">Compose file name inside <paramref name="dir"/> (imported stacks keep their own, e.g. compose.yaml).</param>
    public static List<string> ComposeArgs(string project, string dir, string composeFile, bool withOverride, params string[] tail)
    {
        var args = new List<string> { "compose", "--ansi", "never", "--progress", "plain", "-p", project,
            "--project-directory", dir, "-f", Path.Combine(dir, composeFile) };
        if (withOverride) args.AddRange(["-f", Path.Combine(dir, RenderedInstance.OverrideFile)]);
        args.AddRange(["--env-file", Path.Combine(dir, RenderedInstance.EnvFileName)]);
        args.AddRange(tail);
        return args;
    }

    public async Task<bool> EnsureNetworkAsync(string name, IDeploymentLog log, CancellationToken ct)
    {
        if ((await RunAsync(["network", "inspect", name, "--format", "{{.Name}}"], ct: ct)).Success) return true;
        log.Info($"Creating docker network '{name}'");
        var r = await RunAsync(["network", "create", name], log.Output, ct: ct);
        if (!r.Success) log.Error($"Failed to create network '{name}': {r.StdErr.Trim()}");
        return r.Success;
    }

    public async Task<bool> EnsureVolumeAsync(string name, IDeploymentLog log, CancellationToken ct)
    {
        if ((await RunAsync(["volume", "inspect", name, "--format", "{{.Name}}"], ct: ct)).Success) return true;
        log.Info($"Creating docker volume '{name}'");
        var r = await RunAsync(["volume", "create", name], log.Output, ct: ct);
        if (!r.Success) log.Error($"Failed to create volume '{name}': {r.StdErr.Trim()}");
        return r.Success;
    }
}
