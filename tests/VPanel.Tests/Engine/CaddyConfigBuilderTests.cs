using VPanel.Core.Abstractions;
using VPanel.Infrastructure.Proxy;

namespace VPanel.Tests.Engine;

public class CaddyConfigBuilderTests
{
    [Fact]
    public void One_file_per_host_with_reverse_proxy()
    {
        var files = CaddyConfigBuilder.Build([new ProxyRoute("vp-a", "https://a.example.com", "vp-a-web:8080")]);
        var (name, content) = Assert.Single(files);
        Assert.Equal("vp-a.example.com.caddy", name);
        Assert.Contains("a.example.com {\n\treverse_proxy vp-a-web:8080\n}", content);
    }

    [Fact]
    public void Routes_on_same_host_are_merged_by_path()
    {
        var files = CaddyConfigBuilder.Build([
            new ProxyRoute("vp-root", "https://x.io", "root:80"),
            new ProxyRoute("vp-p1", "https://x.io/p1", "p1:80"),
        ]);
        var content = Assert.Single(files).Value;
        Assert.Contains("handle_path /p1/* {\n\t\treverse_proxy p1:80", content);
        Assert.Contains("handle {\n\t\treverse_proxy root:80", content);
        Assert.True(content.IndexOf("/p1", StringComparison.Ordinal) < content.IndexOf("handle {", StringComparison.Ordinal));
    }

    [Fact]
    public void Http_scheme_and_port_are_kept_in_address()
    {
        var files = CaddyConfigBuilder.Build([new ProxyRoute("r", "http://app.localhost:8080", "u:80")]);
        Assert.Contains("http://app.localhost:8080 {", files.Single().Value);
        Assert.Equal("vp-http-app.localhost-8080.caddy", files.Single().Key);
    }

    [Theory]
    [InlineData("bad upstream")]
    [InlineData("host")]
    [InlineData("a;b:80")]
    public void Invalid_upstreams_are_skipped(string upstream) =>
        Assert.Empty(CaddyConfigBuilder.Build([new ProxyRoute("r", "https://a.io", upstream)]));
}
