using VPanel.Core.Abstractions;
using VPanel.Core.Configuration;
using VPanel.Core.Domain;
using VPanel.Infrastructure.Deployments;
using VPanel.Infrastructure.Import;
using VPanel.Infrastructure.Services;
using VPanel.Tests.Engine;
using VPanel.Tests.Services;

namespace VPanel.Tests.Import;

public sealed class StackImportTests
{
    private static async Task<(ServiceTestHost Host, ArmServer Server)> SetupAsync()
    {
        var host = await ServiceTestHost.CreateAsync(root => new Dictionary<string, string?>
        {
            ["VPanel:AdoptedRoots:0"] = Path.Combine(root, "srv"),
            ["VPanel:AdoptedRoots:1"] = "/etc", // refused
        });
        var server = new ArmServer(host.Root);
        server.Configure(host.Runner);
        return (host, server);
    }

    [Fact]
    public async Task Scan_detects_shared_infra_and_app_routes()
    {
        var (host, server) = await SetupAsync();
        await using var _ = host;
        var scan = await host.Get<StackScanner>().ScanAsync();

        Assert.True(scan.DockerReachable);
        Assert.Contains(scan.Warnings, w => w.Contains("/etc") && w.Contains("system folder"));
        var infra = Assert.IsType<InfraDetection>(scan.Infra);
        Assert.Equal("infra", infra.ProjectName);
        Assert.Equal("infra-caddy-1", infra.CaddyContainer);
        Assert.Equal(server.CaddyDir, infra.CaddyDir);
        Assert.Equal("/etc/caddy", infra.CaddyConfigDirInContainer);
        Assert.Equal("proxy", infra.SharedNetwork);
        Assert.Equal(IngressMode.Direct, infra.Ingress);
        Assert.Equal(["infra-postgres"], infra.PostgresAliases);
        Assert.Equal(StackKind.Infra, scan.Find("infra")!.Kind);

        var n8n = scan.Find("n8n-arm")!;
        Assert.True(n8n.CanImport);
        Assert.Equal(StackKind.App, n8n.Kind);
        Assert.Equal(InstanceStatus.Running, n8n.RuntimeStatus);
        Assert.Equal("n8n", n8n.ProxyService);
        Assert.Equal(5678, n8n.ProxyPort);
        Assert.Equal("https://internal.n8n-arm.arm-app-u01", n8n.PublicUrl);
        Assert.False(n8n.TlsInternal); // TLS comes from "import common", which is carried over
        Assert.Equal("tls internal (from snippet 'common')", n8n.TlsNote);
        Assert.StartsWith("import common\n", n8n.ProxyDirectives);
        Assert.Contains("header {\n\tX-Content-Type-Options \"nosniff\"", n8n.ProxyDirectives);
        Assert.Contains("# n8n uses WebSockets", n8n.ProxyDirectives);
        Assert.Null(n8n.ProxyOptions);
        Assert.True(n8n.ManualSite!.CanTakeOver);
        Assert.Equal(BuildMode.Build, n8n.SuggestedBuildMode);

        var cxml = scan.Find("cxml-dev-server")!;
        Assert.Equal("app", cxml.ProxyService);
        Assert.Equal(8080, cxml.ProxyPort);
        Assert.Equal("https://internal.cxml.arm-app-u01", cxml.PublicUrl);
        Assert.Equal("health_uri /health\nhealth_interval 15s\nhealth_timeout 5s\n", cxml.ProxyOptions);
        Assert.Contains("-Server", cxml.ProxyDirectives);
        Assert.Contains("# Deliberately NO X-Frame-Options", cxml.ProxyDirectives);
        Assert.Equal(BuildMode.PullFirst, cxml.SuggestedBuildMode);
        Assert.Equal(server.CxmlRepo, cxml.SuggestedRoot); // folder + build context ../..

        // nothing was written or run besides read-only docker calls
        Assert.DoesNotContain(host.Runner.Calls, c => c.Arguments.Contains("up") || c.Arguments.Contains("exec"));
    }

    [Fact]
    public async Task Apps_wait_for_the_infra_import()
    {
        var (host, _) = await SetupAsync();
        await using var __ = host;
        var r = await host.Get<StackImportService>().ImportAsync(new ImportRequest { ProjectName = "n8n-arm" }, "t");
        Assert.Contains(r.Errors, e => e.Contains("shared infrastructure ('infra') first"));
    }

    [Fact]
    public async Task Imports_infra_then_app_in_place_and_takes_over_the_caddy_site_on_first_deploy()
    {
        var (host, server) = await SetupAsync();
        await using var _ = host;
        var importer = host.Get<StackImportService>();
        var settings = host.Get<SettingsService>();

        // 1. infra: replaces the seeded template, adopts settings, Caddyfile already imports sites/*.caddy
        var infra = await importer.ImportAsync(new ImportRequest { ProjectName = "infra" }, "admin");
        Assert.True(infra.Success, string.Join("; ", infra.Errors));
        var s = settings.Get();
        Assert.Equal("proxy", s.SharedNetwork);
        Assert.Equal("infra-caddy-1", s.CaddyContainer);
        Assert.Equal(server.CaddyDir, s.CaddyDir);
        Assert.Equal(IngressMode.Direct, s.Ingress);
        Assert.Equal(ArmServer.MainCaddyfile, await File.ReadAllTextAsync(Path.Combine(server.CaddyDir, "Caddyfile")));
        var apps = await host.Get<AppService>().ListAsync();
        Assert.DoesNotContain(apps, a => a.App.Slug == "shared-infra");
        var infraInstance = infra.Value!.Instance;
        Assert.True(infraInstance.App!.IsSystem);
        Assert.Equal("infra", infraInstance.ProjectName);
        Assert.Equal(server.Infra, infraInstance.InstallPath);
        Assert.Contains("# Shared infra stack", infraInstance.App.ComposeTemplate); // verbatim, comments kept

        // 2. app
        var r = await importer.ImportAsync(new ImportRequest { ProjectName = "n8n-arm" }, "admin");
        Assert.True(r.Success, string.Join("; ", r.Errors));
        var inst = r.Value!.Instance;
        Assert.Equal("n8n-arm", inst.ProjectName);
        Assert.Equal(server.N8n, inst.InstallPath);
        Assert.Equal("docker-compose.yml", inst.ComposeFileName);
        Assert.False(inst.TlsInternal);
        Assert.StartsWith("import common\n", inst.ProxyDirectives);
        Assert.True(inst.IsAwaitingFirstDeploy);
        Assert.Equal(InstanceStatus.Running, inst.Status);
        Assert.Equal("https://internal.n8n-arm.arm-app-u01", inst.PublicUrl);
        Assert.Equal(Path.Combine(server.CaddyDir, "sites", "n8n-arm.caddy"), inst.Import!.PendingTakeover!.File);
        Assert.True(File.Exists(Path.Combine(inst.Import.BackupDir!, "docker-compose.yml")));
        Assert.True(File.Exists(Path.Combine(inst.Import.BackupDir!, ".env")));
        Assert.Null(ProxyRouteService.RouteFor(inst)); // hand-written route stays until the first deploy
        Assert.True(host.Get<InstanceService>().HasUndeployedChanges(inst));

        // already managed now
        var again = await importer.ImportAsync(new ImportRequest { ProjectName = "n8n-arm" }, "admin");
        Assert.Contains(again.Errors, e => e.Contains("already managed"));

        // 3. preview: the existing alias is reused, so nothing would be recreated
        host.Runner.Before(a => a.Contains("--hash"), new ProcessResult(0, "n8n hash-n8n\n", ""))
            .Before(a => a is ["ps", "--all", "--filter", ..] && a.Any(x => x.Contains("config-hash")), new ProcessResult(0, "n8n\thash-n8n\tFalse\n", ""));
        var preview = await importer.PreviewDeployAsync(inst.Id);
        Assert.True(preview.Success, preview.Error);
        Assert.False(preview.RecreatesAnything);

        // 4. first deploy: same project and compose file, no override, generated site replaces the hand-written one
        var job = await host.Get<DeploymentService>().EnqueueAsync(inst.Id, DeploymentAction.Deploy, "admin");
        await host.Get<DeploymentWorker>().RunJobAsync(job.Value!.Id, default);
        var deployment = (await host.Get<DeploymentService>().GetAsync(job.Value.Id))!;
        Assert.True(deployment.Status == DeploymentStatus.Succeeded, deployment.ErrorMessage + "\n" + deployment.Log);

        var up = host.Runner.Calls.Last(c => FakeProcessRunner.IsCompose(c.Arguments, "up"));
        Assert.Equal("n8n-arm", ArmServer.ProjectOf(up.Arguments));
        Assert.Contains(Path.Combine(server.N8n, "docker-compose.yml"), up.Arguments);
        Assert.DoesNotContain(up.Arguments, a => a.EndsWith("docker-compose.vpanel.yml"));
        Assert.False(File.Exists(Path.Combine(server.N8n, "docker-compose.vpanel.yml")));
        Assert.Contains("N8N_ENCRYPTION_KEY=k", await File.ReadAllTextAsync(Path.Combine(server.N8n, ".env")));
        Assert.Contains("COMPOSE_PROJECT_NAME=n8n-arm", await File.ReadAllTextAsync(Path.Combine(server.N8n, ".env")));

        var site = await File.ReadAllTextAsync(Path.Combine(server.CaddyDir, "sites", "vp-internal.n8n-arm.arm-app-u01.caddy"));
        Assert.Contains("internal.n8n-arm.arm-app-u01 {", site);
        Assert.DoesNotContain("tls internal", site); // provided by "import common"
        Assert.Contains("\timport common\n", site);
        Assert.Contains("\theader {\n\t\tX-Content-Type-Options \"nosniff\"\n\t\tReferrer-Policy \"strict-origin-when-cross-origin\"\n\t\t-Server\n\t}\n", site);
        Assert.Contains("\treverse_proxy n8n-arm:5678\n}", site);
        Assert.False(File.Exists(Path.Combine(server.CaddyDir, "sites", "n8n-arm.caddy")));
        Assert.Single(Directory.GetFiles(Path.Combine(server.CaddyDir, ".vpanel-backup"), "n8n-arm.caddy", SearchOption.AllDirectories));
        Assert.True(File.Exists(Path.Combine(server.CaddyDir, "sites", "cxml.caddy"))); // untouched

        var after = (await host.Get<InstanceService>().GetAsync(inst.Id))!;
        Assert.False(after.IsAwaitingFirstDeploy);
        Assert.Null(after.Import!.PendingTakeover);
        Assert.Equal("n8n-arm", after.DeployedProxyAlias);
        Assert.Equal("n8n-arm:5678", ProxyRouteService.RouteFor(after)!.Upstream);
        Assert.False(host.Get<InstanceService>().HasUndeployedChanges(after));
    }

    [Fact]
    public async Task Pull_first_stack_keeps_published_images()
    {
        var (host, server) = await SetupAsync();
        await using var __ = host;
        var importer = host.Get<StackImportService>();
        Assert.True((await importer.ImportAsync(new ImportRequest { ProjectName = "infra" }, "t")).Success);
        var r = await importer.ImportAsync(new ImportRequest { ProjectName = "cxml-dev-server" }, "t");
        Assert.True(r.Success, string.Join("; ", r.Errors));
        Assert.Equal(BuildMode.PullFirst, r.Value!.Instance.BuildMode);
        Assert.Equal("https://internal.cxml.arm-app-u01", r.Value.Instance.PublicUrl);

        var job = await host.Get<DeploymentService>().EnqueueAsync(r.Value.Instance.Id, DeploymentAction.Deploy, "t");
        await host.Get<DeploymentWorker>().RunJobAsync(job.Value!.Id, default);
        var pull = host.Runner.Calls.Last(c => FakeProcessRunner.IsCompose(c.Arguments, "pull"));
        Assert.Contains("--ignore-pull-failures", pull.Arguments);
        Assert.DoesNotContain("--ignore-buildable", pull.Arguments);
        var up = host.Runner.Calls.Last(c => FakeProcessRunner.IsCompose(c.Arguments, "up"));
        Assert.DoesNotContain("--build", up.Arguments);

        // cxml's site is taken over too, with its health checks and headers
        var site = await File.ReadAllTextAsync(Path.Combine(server.CaddyDir, "sites", "vp-internal.cxml.arm-app-u01.caddy"));
        Assert.Contains("\treverse_proxy cxml-app:8080 {\n\t\thealth_uri /health\n\t\thealth_interval 15s\n\t\thealth_timeout 5s\n\t}\n", site);
        Assert.Contains("\t\tStrict-Transport-Security \"max-age=31536000\"", site);
        Assert.False(File.Exists(Path.Combine(server.CaddyDir, "sites", "cxml.caddy")));
    }

    [Fact]
    public async Task Public_url_served_by_a_site_with_routing_is_refused()
    {
        var (host, server) = await SetupAsync();
        await using var __ = host;
        await File.WriteAllTextAsync(Path.Combine(server.CaddyDir, "sites", "cxml.caddy"),
            "internal.cxml.arm-app-u01 {\n\timport common\n\thandle_path /api/* {\n\t\treverse_proxy api:9000\n\t}\n\treverse_proxy cxml-app:8080\n}\n");
        var importer = host.Get<StackImportService>();
        Assert.True((await importer.ImportAsync(new ImportRequest { ProjectName = "infra" }, "t")).Success);
        var r = await importer.ImportAsync(new ImportRequest { ProjectName = "cxml-dev-server", PublicUrl = "https://internal.cxml.arm-app-u01" }, "t");
        Assert.Contains(r.Errors, e => e.Contains("cannot take over"));
    }

    [Fact]
    public async Task Forgetting_an_imported_stack_before_deploy_leaves_it_alone()
    {
        var (host, server) = await SetupAsync();
        await using var _ = host;
        var importer = host.Get<StackImportService>();
        Assert.True((await importer.ImportAsync(new ImportRequest { ProjectName = "infra" }, "t")).Success);
        var r = await importer.ImportAsync(new ImportRequest { ProjectName = "n8n-arm" }, "t");
        var calls = host.Runner.Calls.Count;
        Assert.True((await host.Get<InstanceService>().DeleteAsync(r.Value!.Instance.Id, "t")).Success);
        Assert.Equal(calls, host.Runner.Calls.Count);
        Assert.DoesNotContain((await host.Get<AppService>().ListAsync()), a => a.App.Slug == "n8n-arm");
        Assert.True(File.Exists(Path.Combine(server.CaddyDir, "sites", "n8n-arm.caddy")));
        Assert.NotNull((await host.Get<StackScanner>().ScanAsync()).Find("n8n-arm")); // can be imported again
    }

    [Fact]
    public async Task Stacks_outside_the_adopted_roots_cannot_be_imported()
    {
        await using var host = await ServiceTestHost.CreateAsync();
        new ArmServer(host.Root).Configure(host.Runner);
        var scan = await host.Get<StackScanner>().ScanAsync();
        var n8n = scan.Find("n8n-arm")!;
        Assert.False(n8n.InAllowedRoot);
        Assert.False(n8n.CanImport);
        Assert.Contains(scan.Warnings, w => w.Contains("--adopt"));
        var r = await host.Get<StackImportService>().ImportAsync(new ImportRequest { ProjectName = "infra" }, "t");
        Assert.Contains(r.Errors, e => e.Contains("AdoptedRoots"));
    }
}
