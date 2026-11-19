using VPanel.Core.Abstractions;
using VPanel.Core.Configuration;
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

    [Fact]
    public void Tunnel_mode_serves_plain_http_and_forwards_https_scheme()
    {
        var files = CaddyConfigBuilder.Build([new ProxyRoute("vp-a", "https://a.example.com", "vp-a-web:8080")], IngressMode.CloudflareTunnel);
        var (name, content) = Assert.Single(files);
        Assert.Equal("vp-http-a.example.com.caddy", name);
        Assert.Contains("http://a.example.com {\n\treverse_proxy vp-a-web:8080 {\n\t\theader_up X-Forwarded-Proto https\n\t}\n}", content);
    }

    [Fact]
    public void Tunnel_mode_forwards_scheme_inside_path_handlers()
    {
        var content = CaddyConfigBuilder.Build([
            new ProxyRoute("vp-root", "https://x.io", "root:80"),
            new ProxyRoute("vp-p1", "https://x.io/p1", "p1:80"),
        ], IngressMode.CloudflareTunnel).Single().Value;
        Assert.StartsWith("http://x.io {", content.Split('\n').First(l => !l.StartsWith('#')));
        Assert.Contains("handle_path /p1/* {\n\t\treverse_proxy p1:80 {\n\t\t\theader_up X-Forwarded-Proto https\n\t\t}\n\t}", content);
        Assert.Contains("handle {\n\t\treverse_proxy root:80 {\n\t\t\theader_up X-Forwarded-Proto https\n\t\t}\n\t}", content);
    }

    [Fact]
    public void Tunnel_mode_http_url_gets_no_scheme_override_and_https_port_is_dropped()
    {
        var http = CaddyConfigBuilder.Build([new ProxyRoute("r", "http://a.io", "u:80")], IngressMode.CloudflareTunnel).Single().Value;
        Assert.Contains("http://a.io {\n\treverse_proxy u:80\n}", http);
        Assert.DoesNotContain("header_up", http);

        var https = CaddyConfigBuilder.Build([new ProxyRoute("r", "https://a.io:8443", "u:80")], IngressMode.CloudflareTunnel);
        Assert.Equal("vp-http-a.io.caddy", https.Single().Key);
    }

    [Fact]
    public void Direct_mode_never_overrides_forwarded_scheme() =>
        Assert.DoesNotContain("header_up", CaddyConfigBuilder.Build([new ProxyRoute("r", "https://a.io", "u:80")]).Single().Value);
}
