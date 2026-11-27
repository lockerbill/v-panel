using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VPanel.Core.Abstractions;
using VPanel.Core.Configuration;
using VPanel.Infrastructure.Docker;
using VPanel.Infrastructure.Proxy;

namespace VPanel.Tests.Engine;

public sealed class CaddyFileProviderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("vpanel-caddy-").FullName;
    private readonly FakeProcessRunner _runner = new();
    private PanelSettings Settings => TestData.Settings(caddyDir: _dir);

    private CaddyFileProvider Provider() => new(new DockerCli(_runner, Options.Create(new VPanelOptions())), NullLogger<CaddyFileProvider>.Instance);

    [Fact]
    public async Task Writes_sites_validates_and_reloads()
    {
        var r = await Provider().ApplyAsync([new ProxyRoute("vp-a", "https://a.io", "vp-a-web:80")], Settings, NullDeploymentLog.Instance, default);
        Assert.True(r.Success, r.Error);
        Assert.True(File.Exists(Path.Combine(_dir, "Caddyfile")));
        Assert.True(File.Exists(Path.Combine(_dir, "sites", "vp-a.io.caddy")));
        Assert.Equal(["validate", "reload"], _runner.Calls.Select(c => c.Arguments[3]));
        Assert.All(_runner.Calls, c => Assert.Equal(["exec", "caddy", "caddy"], c.Arguments.Take(3)));
    }

    [Fact]
    public async Task Removes_stale_sites_and_skips_when_unchanged()
    {
        var p = Provider();
        await p.ApplyAsync([new ProxyRoute("a", "https://a.io", "a:80"), new ProxyRoute("b", "https://b.io", "b:80")], Settings, NullDeploymentLog.Instance, default);
        await p.ApplyAsync([new ProxyRoute("a", "https://a.io", "a:80")], Settings, NullDeploymentLog.Instance, default);
        Assert.False(File.Exists(Path.Combine(_dir, "sites", "vp-b.io.caddy")));
        var calls = _runner.Calls.Count;
        var r = await p.ApplyAsync([new ProxyRoute("a", "https://a.io", "a:80")], Settings, NullDeploymentLog.Instance, default);
        Assert.True(r.Success);
        Assert.Equal(calls, _runner.Calls.Count);
    }

    [Fact]
    public async Task Rolls_back_when_validation_fails()
    {
        var p = Provider();
        await p.ApplyAsync([new ProxyRoute("a", "https://a.io", "a:80")], Settings, NullDeploymentLog.Instance, default);
        var before = await File.ReadAllTextAsync(Path.Combine(_dir, "sites", "vp-a.io.caddy"));

        _runner.On(a => a.Contains("validate"), new ProcessResult(1, "", "Error: adapting config: bad"));
        var r = await p.ApplyAsync([new ProxyRoute("a", "https://a.io", "other:81"), new ProxyRoute("c", "https://c.io", "c:80")],
            Settings, NullDeploymentLog.Instance, default);

        Assert.False(r.Success);
        Assert.Contains("validation failed", r.Error);
        Assert.Equal(before, await File.ReadAllTextAsync(Path.Combine(_dir, "sites", "vp-a.io.caddy")));
        Assert.False(File.Exists(Path.Combine(_dir, "sites", "vp-c.io.caddy")));
    }

    [Fact]
    public async Task Does_not_touch_user_files()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "sites"));
        await File.WriteAllTextAsync(Path.Combine(_dir, "sites", "my-site.caddy"), "mine");
        await Provider().ApplyAsync([], Settings, NullDeploymentLog.Instance, default);
        Assert.Equal("mine", await File.ReadAllTextAsync(Path.Combine(_dir, "sites", "my-site.caddy")));
    }

    [Fact]
    public async Task Switching_to_tunnel_mode_replaces_site_files()
    {
        var p = Provider();
        await p.ApplyAsync([new ProxyRoute("a", "https://a.io", "a:80")], Settings, NullDeploymentLog.Instance, default);
        var r = await p.ApplyAsync([new ProxyRoute("a", "https://a.io", "a:80")], Settings with { Ingress = IngressMode.CloudflareTunnel },
            NullDeploymentLog.Instance, default);
        Assert.True(r.Success, r.Error);
        Assert.False(File.Exists(Path.Combine(_dir, "sites", "vp-a.io.caddy")));
        Assert.Contains("http://a.io {", await File.ReadAllTextAsync(Path.Combine(_dir, "sites", "vp-http-a.io.caddy")));
    }

    [Fact]
    public async Task Take_over_removes_the_hand_written_site_in_the_same_reload()
    {
        var sites = Path.Combine(_dir, "sites");
        Directory.CreateDirectory(sites);
        var manual = Path.Combine(sites, "apps.caddy");
        await File.WriteAllTextAsync(manual, "a.io {\n\treverse_proxy a:80\n}\n\nb.io {\n\treverse_proxy b:80\n}\n");

        var r = await Provider().ApplyAsync([new ProxyRoute("a", "https://a.io", "a:80")], Settings, NullDeploymentLog.Instance, default,
            [new ManualSiteRemoval(manual, "a.io")]);

        Assert.True(r.Success, r.Error);
        var left = await File.ReadAllTextAsync(manual);
        Assert.DoesNotContain("a.io {", left);
        Assert.Contains("b.io {", left);
        Assert.Single(Directory.GetFiles(Path.Combine(_dir, ".vpanel-backup"), "apps.caddy", SearchOption.AllDirectories));
        Assert.Equal(["validate", "reload"], _runner.Calls.Select(c => c.Arguments[3]));
    }

    [Fact]
    public async Task Failed_take_over_restores_the_hand_written_file()
    {
        var sites = Path.Combine(_dir, "sites");
        Directory.CreateDirectory(sites);
        var manual = Path.Combine(sites, "a.caddy");
        const string original = "# mine\na.io {\n\treverse_proxy a:80\n}\n";
        await File.WriteAllTextAsync(manual, original);
        _runner.On(a => a.Contains("reload"), new ProcessResult(1, "", "reload failed"));

        var r = await Provider().ApplyAsync([new ProxyRoute("a", "https://a.io", "a:80")], Settings, NullDeploymentLog.Instance, default,
            [new ManualSiteRemoval(manual, "a.io")]);

        Assert.False(r.Success);
        Assert.Equal(original, await File.ReadAllTextAsync(manual));
        Assert.False(File.Exists(Path.Combine(sites, "vp-a.io.caddy")));
    }

    [Fact]
    public async Task Take_over_never_touches_files_outside_the_caddy_folder()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"vpanel-outside-{Guid.NewGuid():N}.caddy");
        await File.WriteAllTextAsync(outside, "a.io {\n\treverse_proxy a:80\n}\n");
        try
        {
            await Provider().ApplyAsync([new ProxyRoute("a", "https://a.io", "a:80")], Settings, NullDeploymentLog.Instance, default,
                [new ManualSiteRemoval(outside, "a.io")]);
            Assert.Contains("a.io {", await File.ReadAllTextAsync(outside));
        }
        finally { File.Delete(outside); }
    }

    [Fact]
    public async Task Adds_an_import_line_to_an_existing_caddyfile_only_when_needed()
    {
        var main = Path.Combine(_dir, "Caddyfile");
        await File.WriteAllTextAsync(main, "{\n\temail a@b.c\n}\n\nsite.io {\n\treverse_proxy s:80\n}\n");
        var r = await Provider().EnsureIncludedAsync(Settings, NullDeploymentLog.Instance, default);
        Assert.True(r.Success, r.Error);
        var text = await File.ReadAllTextAsync(main);
        Assert.Contains("import /etc/caddy/sites/vp-*.caddy", text);
        Assert.Contains("site.io {", text);
        Assert.Equal(["validate", "reload"], _runner.Calls.Select(c => c.Arguments[3]));

        var calls = _runner.Calls.Count;
        Assert.True((await Provider().EnsureIncludedAsync(Settings, NullDeploymentLog.Instance, default)).Success);
        Assert.Equal(calls, _runner.Calls.Count);
    }

    [Theory]
    [InlineData("import sites/*.caddy", true)]
    [InlineData("import sites/*", true)]
    [InlineData("import /etc/caddy/sites/vp-*.caddy", true)]
    [InlineData("import ./sites/*.caddy", true)]
    [InlineData("import sites/cxml.caddy", false)]
    [InlineData("import /other/sites/*.caddy", false)]
    [InlineData("# import sites/*.caddy", false)]
    public void Detects_imports_of_managed_sites(string line, bool expected) =>
        Assert.Equal(expected, CaddyFileProvider.ImportsManagedSites($"{{\n}}\n{line}\n", Settings));

    public void Dispose() => Directory.Delete(_dir, true);
}
