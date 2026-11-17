using VPanel.Infrastructure.Docker;

namespace VPanel.Tests.Engine;

public class ComposeRendererTests
{
    [Fact]
    public void Renders_env_with_managed_variables_and_overrides()
    {
        var r = ComposeRenderer.Render(TestData.Instance(), TestData.Settings());
        Assert.Equal("vp-shop-01", r.ProjectName);
        Assert.Contains("COMPOSE_PROJECT_NAME=vp-shop-01\n", r.Env);
        Assert.Contains("VPANEL_HOST=shop-01.example.com\n", r.Env);
        Assert.Contains("VPANEL_PROXY_ALIAS=vp-shop-01-web\n", r.Env);
        Assert.Contains("GREETING=hi\n", r.Env);
        Assert.DoesNotContain("GREETING=hello", r.Env);
        Assert.Equal("shop_shop-01", r.EnvValues["DB_NAME"]);
    }

    [Fact]
    public void Override_attaches_proxy_service_to_shared_network_keeping_default()
    {
        var r = ComposeRenderer.Render(TestData.Instance(), TestData.Settings());
        Assert.NotNull(r.Override);
        Assert.Contains("\"web\":", r.Override);
        Assert.Contains("default: {}", r.Override);
        Assert.Contains("- \"vp-shop-01-web\"", r.Override);
        Assert.Contains("name: \"vpanel-shared\"", r.Override);
    }

    [Fact]
    public void Override_keeps_explicit_networks()
    {
        var inst = TestData.Instance();
        inst.ComposeOverride = "services:\n  web:\n    image: nginx\n    networks: [front]\nnetworks:\n  front: {}\n";
        var r = ComposeRenderer.Render(inst, TestData.Settings());
        Assert.DoesNotContain("default: {}", r.Override);
    }

    [Fact]
    public void No_override_when_proxy_service_missing()
    {
        var inst = TestData.Instance();
        inst.ProxyService = "api";
        Assert.Null(ComposeRenderer.Render(inst, TestData.Settings()).Override);
    }

    [Fact]
    public void Hash_changes_with_configuration()
    {
        var inst = TestData.Instance();
        var h1 = ComposeRenderer.Render(inst, TestData.Settings()).ConfigHash;
        Assert.Equal(h1, ComposeRenderer.Render(inst, TestData.Settings()).ConfigHash);
        inst.EnvOverrides = "GREETING=other\n";
        Assert.NotEqual(h1, ComposeRenderer.Render(inst, TestData.Settings()).ConfigHash);
        inst.EnvOverrides = "GREETING=hi\n";
        inst.App!.ComposeTemplate += "# change\n";
        Assert.NotEqual(h1, ComposeRenderer.Render(inst, TestData.Settings()).ConfigHash);
    }

    [Fact]
    public void Parses_resolved_compose_json()
    {
        const string json = """
            {"name":"vp-x","networks":{"default":{"name":"vp-x_default"},"shared":{"name":"vpanel-shared","external":true}},
             "services":{"web":{"image":"nginx","volumes":[{"type":"volume","source":"data","target":"/d"},{"type":"bind","source":"/opt/apps/x/conf","target":"/c"}]},
                         "api":{"build":{"context":"."}}},
             "volumes":{"data":{"name":"vp-x_data"},"ext":{"name":"files","external":true}}}
            """;
        var m = ResolvedCompose.Parse(json);
        Assert.Equal(["vpanel-shared"], m.ExternalNetworks);
        Assert.Equal(["files"], m.ExternalVolumes);
        Assert.Equal(["vp-x_data"], m.NamedVolumes);
        Assert.Equal(["/opt/apps/x/conf"], m.BindSources);
        Assert.True(m.HasBuild);
    }

    public static TheoryData<string, VPanel.Core.Domain.InstanceStatus> SummaryCases => new()
    {
        { "running,running", VPanel.Core.Domain.InstanceStatus.Running },
        { "running,exited:0", VPanel.Core.Domain.InstanceStatus.Running },
        { "running,exited:1", VPanel.Core.Domain.InstanceStatus.Partial },
        { "exited:0,exited:0", VPanel.Core.Domain.InstanceStatus.Stopped },
        { "exited:143", VPanel.Core.Domain.InstanceStatus.Stopped },
        { "exited:1", VPanel.Core.Domain.InstanceStatus.Failed },
        { "restarting", VPanel.Core.Domain.InstanceStatus.Failed },
        { "", VPanel.Core.Domain.InstanceStatus.Stopped },
    };

    [Theory]
    [MemberData(nameof(SummaryCases))]
    public void Summarizes_container_states(string spec, VPanel.Core.Domain.InstanceStatus expected)
    {
        var states = spec.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Split(':')).Select(p => (p[0], p.Length > 1 ? int.Parse(p[1]) : (int?)null)).ToList();
        Assert.Equal(expected, DockerComposeHandler.Summarize(states));
    }

    [Theory]
    [InlineData("Exited (0) 3 minutes ago", 0)]
    [InlineData("Exited (137) 1 second ago", 137)]
    [InlineData("Up 5 minutes", null)]
    public void Exit_code_from_status(string status, int? expected) => Assert.Equal(expected, DockerComposeHandler.ExitCodeFromStatus(status));
}
