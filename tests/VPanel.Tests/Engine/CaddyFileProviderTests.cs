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

    public void Dispose() => Directory.Delete(_dir, true);
}
