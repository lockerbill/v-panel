using VPanel.Core.Configuration;
using VPanel.Core.Domain;
using VPanel.Infrastructure.Deployments;
using VPanel.Infrastructure.Services;

namespace VPanel.Tests.Services;

public class InstanceLifecycleTests
{
    private static async Task<(ServiceTestHost Host, AppDefinition App)> SetupAsync()
    {
        var host = await ServiceTestHost.CreateAsync();
        var apps = await host.Get<AppService>().ListAsync();
        return (host, apps.Single(a => a.App.Slug == "whoami").App);
    }

    [Fact]
    public async Task Seeds_infrastructure_and_example()
    {
        await using var host = await ServiceTestHost.CreateAsync();
        var apps = await host.Get<AppService>().ListAsync();
        Assert.Contains(apps, a => a.App is { Slug: "shared-infra", IsSystem: true } && a.InstanceCount == 1);
        Assert.Contains(apps, a => a.App.Slug == "whoami");
    }

    [Fact]
    public async Task Fresh_install_uses_cloudflare_tunnel_and_restart_keeps_saved_mode()
    {
        await using var host = await ServiceTestHost.CreateAsync();
        var settings = host.Get<SettingsService>();
        Assert.Equal(IngressMode.CloudflareTunnel, settings.Get().Ingress);

        await settings.SaveAsync(settings.Get() with { Ingress = IngressMode.Direct });
        await VPanel.Infrastructure.Seed.VPanelInitializer.InitializeAsync(host.Services);
        Assert.Equal(IngressMode.Direct, settings.Get().Ingress);
    }

    [Fact]
    public void Settings_saved_before_the_ingress_option_existed_stay_direct()
    {
        var old = System.Text.Json.JsonSerializer.Deserialize<PanelSettings>("{\"AppsRoot\":\"/opt/vpanel/apps\",\"CaddyEnabled\":true}")!;
        Assert.Equal(IngressMode.Direct, old.Ingress);
    }

    [Fact]
    public async Task Env_is_encrypted_at_rest()
    {
        var (host, app) = await SetupAsync();
        await using var _ = host;
        await using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(host.Root, "data", "vpanel.db")}");
        await conn.OpenAsync();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT EnvTemplate FROM Apps WHERE Slug = 'whoami'";
        var raw = (string)(await cmd.ExecuteScalarAsync())!;
        Assert.StartsWith("dp1:", raw);
        Assert.Contains("GREETING=hello", (await host.Get<AppService>().GetAsync(app.Id))!.EnvTemplate);
    }

    [Fact]
    public async Task Instance_validation()
    {
        var (host, app) = await SetupAsync();
        await using var _ = host;
        var svc = host.Get<InstanceService>();

        var draft = await svc.NewDraftAsync(app.Id);
        Assert.Equal("whoami-01", draft.Slug);
        Assert.Equal(Path.Combine(host.AppsRoot, "whoami-01"), draft.InstallPath);
        draft.PublicUrl = "https://w1.example.com";
        Assert.True((await svc.SaveAsync(draft, "t")).Success);

        var outside = await svc.NewDraftAsync(app.Id);
        outside.InstallPath = "/etc/evil";
        Assert.Contains((await svc.SaveAsync(outside, "t")).Errors, e => e.Contains("inside the apps root"));

        var clash = await svc.NewDraftAsync(app.Id);
        clash.PublicUrl = "w1.example.com"; // same as https://w1.example.com
        Assert.Contains((await svc.SaveAsync(clash, "t")).Errors, e => e.Contains("already used"));

        var dupSlug = await svc.NewDraftAsync(app.Id);
        dupSlug.Slug = "whoami-01";
        dupSlug.InstallPath = Path.Combine(host.AppsRoot, "x");
        Assert.Contains((await svc.SaveAsync(dupSlug, "t")).Errors, e => e.Contains("slug"));
    }

    [Fact]
    public async Task Deploy_job_updates_state_routes_and_change_detection()
    {
        var (host, app) = await SetupAsync();
        await using var _ = host;
        var instances = host.Get<InstanceService>();
        var deployments = host.Get<DeploymentService>();
        var worker = host.Get<DeploymentWorker>();

        var draft = await instances.NewDraftAsync(app.Id);
        draft.PublicUrl = "https://w1.example.com";
        var inst = (await instances.SaveAsync(draft, "tester")).Value!;

        var job = await deployments.EnqueueAsync(inst.Id, DeploymentAction.Deploy, "tester");
        Assert.True(job.Success);
        Assert.False((await deployments.EnqueueAsync(inst.Id, DeploymentAction.Deploy, "tester")).Success, "second job is rejected while one is queued");

        await worker.RunJobAsync(job.Value!.Id, default);

        var d = await deployments.GetAsync(job.Value.Id);
        Assert.Equal(DeploymentStatus.Succeeded, d!.Status);
        Assert.Contains("Done in", d.Log);
        Assert.NotNull(d.ComposeSnapshot);

        var deployed = await instances.GetAsync(inst.Id);
        Assert.Equal(InstanceStatus.Running, deployed!.Status);
        Assert.NotNull(deployed.DeployedConfigHash);
        Assert.False(instances.HasUndeployedChanges(deployed));

        // fresh installs default to Cloudflare Tunnel ingress: plain HTTP site, original scheme forwarded
        var site = Path.Combine(host.Root, "caddy", "sites", "vp-http-w1.example.com.caddy");
        Assert.True(File.Exists(site));
        var siteContent = await File.ReadAllTextAsync(site);
        Assert.Contains("http://w1.example.com {", siteContent);
        Assert.Contains("reverse_proxy vp-whoami-01-web:80 {\n\t\theader_up X-Forwarded-Proto https\n\t}", siteContent);

        deployed.EnvOverrides = "GREETING=changed\n";
        await instances.SaveAsync(deployed, "tester");
        Assert.True(instances.HasUndeployedChanges((await instances.GetAsync(inst.Id))!));

        Assert.False((await instances.DeleteAsync(inst.Id, "tester")).Success, "deployed instance cannot be deleted directly");

        var remove = await deployments.EnqueueAsync(inst.Id, DeploymentAction.Remove, "tester");
        await worker.RunJobAsync(remove.Value!.Id, default);
        Assert.Equal(InstanceStatus.Removed, (await instances.GetAsync(inst.Id))!.Status);
        Assert.False(File.Exists(site), "route removed with the instance");
        Assert.True((await instances.DeleteAsync(inst.Id, "tester")).Success);
    }

    [Fact]
    public async Task Failed_proxy_sync_fails_deployment_of_routed_instance()
    {
        var (host, app) = await SetupAsync();
        await using var _ = host;
        host.Runner.On(a => a.Contains("validate"), new VPanel.Core.Abstractions.ProcessResult(1, "", "caddy not running"));
        var instances = host.Get<InstanceService>();
        var draft = await instances.NewDraftAsync(app.Id);
        draft.PublicUrl = "https://w1.example.com";
        var inst = (await instances.SaveAsync(draft, "t")).Value!;
        var job = await host.Get<DeploymentService>().EnqueueAsync(inst.Id, DeploymentAction.Deploy, "t");
        await host.Get<DeploymentWorker>().RunJobAsync(job.Value!.Id, default);

        var d = await host.Get<DeploymentService>().GetAsync(job.Value.Id);
        Assert.Equal(DeploymentStatus.Failed, d!.Status);
        Assert.Contains("proxy configuration failed", d.ErrorMessage);
        Assert.Equal(InstanceStatus.Running, (await instances.GetAsync(inst.Id))!.Status);
    }

    [Fact]
    public async Task Users_and_lockout()
    {
        await using var host = await ServiceTestHost.CreateAsync();
        var users = host.Get<UserService>();
        Assert.False(await users.AnyUsersAsync());
        Assert.NotNull((await users.CreateAsync("admin", "short")).Error);
        Assert.NotNull((await users.CreateAsync("admin", "longenough")).User);
        Assert.Equal(LoginOutcome.Success, (await users.VerifyAsync("ADMIN", "longenough", "ip")).Outcome);
        for (var i = 0; i < 5; i++) Assert.Equal(LoginOutcome.InvalidCredentials, (await users.VerifyAsync("admin", "wrong", "ip2")).Outcome);
        Assert.Equal(LoginOutcome.LockedOut, (await users.VerifyAsync("admin", "longenough", "ip2")).Outcome);
    }
}
