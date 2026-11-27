using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VPanel.Core.Abstractions;
using VPanel.Core.Configuration;
using VPanel.Core.Domain;
using VPanel.Core.Utilities;
using VPanel.Infrastructure.Docker;
using VPanel.Infrastructure.Proxy;
using VPanel.Tests.Engine;

namespace VPanel.Tests.Import;

public sealed class AdoptedStackEngineTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vpanel-adopt-").FullName;
    private readonly FakeProcessRunner _runner = new();

    private static AppInstance Imported(string compose, string project, string service, int port, string dir) => new()
    {
        Id = 7,
        App = new AppDefinition { Slug = project, ComposeTemplate = compose, DefaultProxyService = service, DefaultProxyPort = port },
        Name = project,
        Slug = project,
        ComposeProjectName = project,
        PublicUrl = "https://x.internal",
        InstallPath = dir,
    };

    private static PanelSettings Shared(string network = "proxy", params string[] adopted) =>
        TestData.Settings() with { SharedNetwork = network, AdoptedRoots = adopted };

    [Fact]
    public void Existing_alias_on_the_shared_network_is_reused_without_override()
    {
        var r = ComposeRenderer.Render(Imported(ArmServer.N8nCompose, "n8n-arm", "n8n", 5678, "/srv/n8n-arm"), Shared());
        Assert.Null(r.Override);
        Assert.Equal("n8n-arm", r.ProxyAlias);
        Assert.Equal("n8n-arm", r.ProjectName);

        var cxml = ComposeRenderer.Render(Imported(ArmServer.CxmlCompose, "cxml-dev-server", "app", 8080, "/srv/cxml"), Shared());
        Assert.Null(cxml.Override);
        Assert.Equal("cxml-app", cxml.ProxyAlias);
    }

    [Fact]
    public void Alias_is_added_to_the_existing_network_entry()
    {
        const string compose = "services:\n  web:\n    image: nginx\n    networks: [front, proxy]\nnetworks:\n  front: {}\n  proxy:\n    external: true\n";
        var r = ComposeRenderer.Render(Imported(compose, "shop", "web", 80, "/srv/shop"), Shared());
        Assert.Equal("shop-web", r.ProxyAlias);
        Assert.Contains("\"proxy\":\n        aliases:\n          - \"shop-web\"", r.Override);
        Assert.DoesNotContain("vpanel_shared", r.Override);
    }

    [Fact]
    public void Default_instances_keep_their_hash_and_override()
    {
        var instance = TestData.Instance();
        var r = ComposeRenderer.Render(instance, TestData.Settings());
        Assert.Contains("vpanel_shared", r.Override);
        Assert.Equal("vp-shop-01-web", r.ProxyAlias);
        instance.TlsInternal = true;
        Assert.NotEqual(r.ConfigHash, ComposeRenderer.Render(instance, TestData.Settings()).ConfigHash);
    }

    [Fact]
    public void Adopted_roots_extend_the_allowed_install_paths()
    {
        var adopted = Path.Combine(_root, "srv");
        var stack = Path.Combine(adopted, "n8n");
        Directory.CreateDirectory(stack);
        Assert.Equal(stack, PathGuard.ResolveInsideAny([Path.Combine(_root, "apps"), adopted], stack));
        Assert.Null(PathGuard.ResolveInsideAny([Path.Combine(_root, "apps"), adopted], "/etc/passwd"));
        Assert.Null(PathGuard.ResolveInsideAny([adopted], adopted)); // the root itself is not an install folder

        Assert.True(PathGuard.IsAcceptableAdoptedRoot("/opt/infra/", out var normalized, out _));
        Assert.Equal("/opt/infra", normalized);
        foreach (var bad in new[] { "/", "/etc", "/etc/nginx", "/var/lib", "/var/lib/docker/volumes", "relative/path", "/usr/local" })
            Assert.False(PathGuard.IsAcceptableAdoptedRoot(bad, out _, out _), bad);
    }

    [Fact]
    public async Task Deploy_uses_the_stack_compose_file_and_pull_first()
    {
        var adopted = Path.Combine(_root, "srv");
        var dir = Path.Combine(adopted, "app");
        Directory.CreateDirectory(dir);
        _runner.On(a => FakeProcessRunner.IsCompose(a, "config"), new ProcessResult(0, "{\"services\":{\"web\":{\"build\":{\"context\":\".\"}}}}", ""))
            .On(a => FakeProcessRunner.IsCompose(a, "ps"), new ProcessResult(0, "{\"Name\":\"app-web-1\",\"State\":\"running\"}", ""));
        var instance = Imported("services:\n  web:\n    image: x/web\n    build: .\n", "app", "web", 80, dir);
        instance.ComposeFileName = "compose.yaml";
        instance.BuildMode = BuildMode.PullFirst;
        var handler = new DockerComposeHandler(new DockerCli(_runner, Options.Create(new VPanelOptions())), NullLogger<DockerComposeHandler>.Instance);

        var result = await handler.ExecuteAsync(DeploymentAction.Deploy,
            new InstanceOperationContext(instance, Shared("vpanel-shared", adopted), NullDeploymentLog.Instance), null, default);

        Assert.True(result.Success, result.Error);
        Assert.True(File.Exists(Path.Combine(dir, "compose.yaml")));
        Assert.False(File.Exists(Path.Combine(dir, "docker-compose.yml")));
        var up = _runner.Calls.Single(c => FakeProcessRunner.IsCompose(c.Arguments, "up"));
        Assert.Contains(Path.Combine(dir, "compose.yaml"), up.Arguments);
        Assert.Contains("app", up.Arguments);
        Assert.DoesNotContain("--build", up.Arguments);
        Assert.Contains("--ignore-pull-failures", _runner.Calls.Single(c => FakeProcessRunner.IsCompose(c.Arguments, "pull")).Arguments);
        Assert.Equal("app-web", result.ProxyAlias);

        var outside = await handler.ExecuteAsync(DeploymentAction.Deploy,
            new InstanceOperationContext(instance, Shared(), NullDeploymentLog.Instance), null, default);
        Assert.False(outside.Success);
    }

    [Fact]
    public void Generated_site_uses_the_internal_ca_when_asked()
    {
        var files = CaddyConfigBuilder.Build([new ProxyRoute("a", "https://a.internal", "a:80", TlsInternal: true)]);
        Assert.Contains("a.internal {\n\ttls internal\n\treverse_proxy a:80", files["vp-a.internal.caddy"]);
        var tunnel = CaddyConfigBuilder.Build([new ProxyRoute("a", "https://a.internal", "a:80", TlsInternal: true)], IngressMode.CloudflareTunnel);
        Assert.DoesNotContain("tls internal", tunnel.Values.Single());
    }

    [Fact]
    public void Generated_site_carries_directives_and_proxy_options()
    {
        var route = new ProxyRoute("cxml", "https://internal.cxml.arm-app-u01", "cxml-app:8080", TlsInternal: true,
            Directives: "import common\n\nheader {\n\t-Server\n}\n", ProxyOptions: "health_uri /health\n");
        var site = CaddyConfigBuilder.Build([route])["vp-internal.cxml.arm-app-u01.caddy"];
        Assert.Contains("internal.cxml.arm-app-u01 {\n\timport common\n\n\theader {\n\t\t-Server\n\t}\n\n\treverse_proxy cxml-app:8080 {\n\t\thealth_uri /health\n\t}\n}\n", site);
        Assert.DoesNotContain("tls internal", site); // the imported snippet decides TLS

        var tunnel = CaddyConfigBuilder.Build([route with { TlsInternal = false }], IngressMode.CloudflareTunnel).Values.Single();
        Assert.Contains("reverse_proxy cxml-app:8080 {\n\t\thealth_uri /health\n\t\theader_up X-Forwarded-Proto https\n\t}", tunnel);
    }

    [Fact]
    public void Directives_change_the_config_hash_only_when_set()
    {
        var instance = TestData.Instance();
        var before = ComposeRenderer.Render(instance, TestData.Settings()).ConfigHash;
        instance.ProxyDirectives = "";
        Assert.Equal(before, ComposeRenderer.Render(instance, TestData.Settings()).ConfigHash);
        instance.ProxyDirectives = "import common\n";
        Assert.NotEqual(before, ComposeRenderer.Render(instance, TestData.Settings()).ConfigHash);
    }

    public void Dispose() => Directory.Delete(_root, true);
}
