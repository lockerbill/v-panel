using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VPanel.Core.Abstractions;
using VPanel.Core.Configuration;
using VPanel.Core.Domain;
using VPanel.Infrastructure.Docker;

namespace VPanel.Tests.Engine;

public sealed class DockerComposeHandlerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vpanel-h-").FullName;
    private readonly FakeProcessRunner _runner = new();

    private DockerComposeHandler Handler() =>
        new(new DockerCli(_runner, Options.Create(new VPanelOptions())), NullLogger<DockerComposeHandler>.Instance);

    private const string ConfigJson = """
        {"services":{"web":{"image":"nginx","volumes":[{"type":"bind","source":"__ROOT__/shop-01/uploads","target":"/u"}]}},
         "volumes":{"data":{"name":"vp-shop-01_data"},"files":{"name":"files","external":true}},
         "networks":{"vpanel_shared":{"name":"vpanel-shared","external":true}}}
        """;

    [Fact]
    public async Task Deploy_writes_files_and_runs_compose_in_order()
    {
        _runner
            .On(a => a is ["network", "inspect", ..], new ProcessResult(0, "vpanel-shared", ""))
            .On(a => a is ["volume", "inspect", ..], new ProcessResult(1, "", "no such volume"))
            .On(a => FakeProcessRunner.IsCompose(a, "config"), new ProcessResult(0, ConfigJson.Replace("__ROOT__", _root), ""))
            .On(a => FakeProcessRunner.IsCompose(a, "ps"), new ProcessResult(0, "{\"Name\":\"vp-shop-01-web-1\",\"State\":\"running\"}\n", ""));

        var instance = TestData.Instance(appsRoot: _root);
        var result = await Handler().ExecuteAsync(DeploymentAction.Deploy,
            new InstanceOperationContext(instance, TestData.Settings(_root), NullDeploymentLog.Instance), null, default);

        Assert.True(result.Success, result.Error);
        Assert.Equal(InstanceStatus.Running, result.NewStatus);
        Assert.Equal(["config", "pull", "up", "ps"], _runner.ComposeCommands);
        Assert.Contains(_runner.Calls, c => c.Arguments is ["volume", "create", "files"]);

        var dir = Path.Combine(_root, "shop-01");
        Assert.True(File.Exists(Path.Combine(dir, "docker-compose.yml")));
        Assert.Contains("GREETING=hi", await File.ReadAllTextAsync(Path.Combine(dir, ".env")));
        Assert.True(File.Exists(Path.Combine(dir, "docker-compose.vpanel.yml")));
        Assert.True(Directory.Exists(Path.Combine(dir, "uploads")), "bind folder inside install path is created");
        var up = _runner.Calls.Single(c => FakeProcessRunner.IsCompose(c.Arguments, "up"));
        Assert.Contains("vp-shop-01", up.Arguments);
        Assert.Contains("--remove-orphans", up.Arguments);
    }

    [Fact]
    public async Task Config_failure_stops_before_up()
    {
        _runner.On(a => FakeProcessRunner.IsCompose(a, "config"), new ProcessResult(1, "", "services.web.image must be a string"));
        var result = await Handler().ExecuteAsync(DeploymentAction.Deploy,
            new InstanceOperationContext(TestData.Instance(appsRoot: _root), TestData.Settings(_root), NullDeploymentLog.Instance), null, default);
        Assert.False(result.Success);
        Assert.Equal(InstanceStatus.Failed, result.NewStatus);
        Assert.DoesNotContain("up", _runner.ComposeCommands);
    }

    [Fact]
    public async Task Invalid_compose_fails_without_running_docker()
    {
        var inst = TestData.Instance(appsRoot: _root);
        inst.App!.ComposeTemplate = "services: [";
        var result = await Handler().ExecuteAsync(DeploymentAction.Deploy,
            new InstanceOperationContext(inst, TestData.Settings(_root), NullDeploymentLog.Instance), null, default);
        Assert.False(result.Success);
        Assert.Empty(_runner.Calls);
    }

    [Fact]
    public async Task Install_path_outside_root_is_rejected()
    {
        var inst = TestData.Instance(appsRoot: _root);
        inst.InstallPath = "/etc";
        var result = await Handler().ExecuteAsync(DeploymentAction.Deploy,
            new InstanceOperationContext(inst, TestData.Settings(_root), NullDeploymentLog.Instance), null, default);
        Assert.False(result.Success);
        Assert.Contains("must be inside", result.Error);
        Assert.Empty(_runner.Calls);
    }

    [Fact]
    public async Task Remove_with_volumes_and_files()
    {
        var inst = TestData.Instance(appsRoot: _root);
        var dir = Path.Combine(_root, "shop-01");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "docker-compose.yml"), "services: {}");
        var result = await Handler().ExecuteAsync(DeploymentAction.Remove,
            new InstanceOperationContext(inst, TestData.Settings(_root), NullDeploymentLog.Instance), new RemoveOptions(true, true), default);
        Assert.True(result.Success);
        Assert.Equal(InstanceStatus.Removed, result.NewStatus);
        var down = _runner.Calls.Single(c => FakeProcessRunner.IsCompose(c.Arguments, "down"));
        Assert.Contains("--volumes", down.Arguments);
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public async Task Stop_reports_stopped_even_with_nonzero_exit_codes()
    {
        var inst = TestData.Instance(appsRoot: _root);
        Directory.CreateDirectory(Path.Combine(_root, "shop-01"));
        await File.WriteAllTextAsync(Path.Combine(_root, "shop-01", "docker-compose.yml"), "services: {}");
        _runner.On(a => FakeProcessRunner.IsCompose(a, "ps"), new ProcessResult(0, "{\"Name\":\"c\",\"State\":\"exited\",\"ExitCode\":2}", ""));
        var result = await Handler().ExecuteAsync(DeploymentAction.Stop,
            new InstanceOperationContext(inst, TestData.Settings(_root), NullDeploymentLog.Instance), null, default);
        Assert.True(result.Success);
        Assert.Equal(InstanceStatus.Stopped, result.NewStatus);
        Assert.Equal(["stop"], _runner.ComposeCommands);
    }

    [Fact]
    public async Task Runtime_states_are_grouped_by_project()
    {
        _runner.On(a => a is ["ps", ..], new ProcessResult(0, "vp-a\trunning\tUp 1 hour\nvp-a\texited\tExited (1) 2 minutes ago\nvp-b\trunning\tUp\nvp-b\texited\tExited (0) 1 hour ago\nother\trunning\tUp\n", ""));
        var states = await Handler().GetRuntimeStatesAsync(default);
        Assert.Equal(InstanceStatus.Partial, states["vp-a"].Status);
        Assert.Equal(InstanceStatus.Running, states["vp-b"].Status);
        // imported stacks keep their own project names, so every project is reported
        Assert.Equal(InstanceStatus.Running, states["other"].Status);
    }

    public void Dispose() => Directory.Delete(_root, true);
}
