using VPanel.Infrastructure.Proxy;

namespace VPanel.Tests.Import;

public sealed class CaddySiteParserTests
{
    private const string Caddyfile = """
        {
        	email ops@example.com # global options
        	local_certs
        }

        (common) {
        	encode gzip
        }

        import sites/*.caddy

        # simple, internal CA
        app.internal {
        	tls internal
        	reverse_proxy app:8080
        }

        http://plain.example.com {
        	reverse_proxy http://plain:80
        }

        complex.example.com {
        	header X-Real-IP {remote_host}
        	reverse_proxy {
        		to a:1 b:2
        	}
        }

        a.example.com, b.example.com {
        	reverse_proxy multi:80
        }
        """;

    [Fact]
    public void Finds_blocks_imports_and_simple_sites()
    {
        var model = CaddySiteParser.Parse(Caddyfile, "Caddyfile");
        Assert.Contains(model.Blocks, b => b.IsGlobalOptions);
        Assert.Contains(model.Blocks, b => b.IsSnippet);
        Assert.Equal(["sites/*.caddy"], model.ImportPatterns);

        var sites = model.Sites.ToDictionary(s => s.Address);
        Assert.Equal(4, sites.Count);
        Assert.True(sites["app.internal"].CanTakeOver);
        Assert.True(sites["app.internal"].TlsInternal);
        Assert.Equal("app:8080", sites["app.internal"].Upstream);
        Assert.Equal(13, sites["app.internal"].Line);
        Assert.True(sites["http://plain.example.com"].CanTakeOver);
        Assert.Equal("plain:80", sites["http://plain.example.com"].Upstream);
        Assert.False(sites["complex.example.com"].CanTakeOver);
        Assert.Contains("reverse_proxy", sites["complex.example.com"].Reason);
        Assert.False(sites["a.example.com, b.example.com"].CanTakeOver);
    }

    [Fact]
    public void Removes_one_site_and_keeps_the_rest()
    {
        var result = CaddySiteParser.RemoveSite(Caddyfile, "app.internal", out var empty);
        Assert.NotNull(result);
        Assert.False(empty);
        Assert.DoesNotContain("reverse_proxy app:8080", result);
        Assert.Contains("http://plain.example.com {", result);
        Assert.Contains("{remote_host}", result);
        Assert.Equal(3, CaddySiteParser.Parse(result).Sites.Count());

        Assert.Null(CaddySiteParser.RemoveSite(Caddyfile, "missing.example.com", out _));
        var only = CaddySiteParser.RemoveSite("# n8n\nn8n.local {\n\treverse_proxy n8n:5678\n}\n", "N8N.local", out var nothingLeft);
        Assert.Equal("# n8n\n", only);
        Assert.True(nothingLeft);
    }

    [Fact]
    public void Real_sites_are_taken_over_with_their_directives_and_options()
    {
        var n8n = CaddySiteParser.Parse(ArmServer.N8nSite, "n8n-arm.caddy").Sites.Single();
        Assert.True(n8n.CanTakeOver, n8n.Reason);
        Assert.Equal("n8n-arm:5678", n8n.Upstream);
        Assert.Equal(["common"], n8n.Imports);
        Assert.Null(n8n.ProxyOptions);
        Assert.Equal("""
            import common

            header {
            	X-Content-Type-Options "nosniff"
            	Referrer-Policy "strict-origin-when-cross-origin"
            	-Server
            }

            # n8n uses WebSockets for the editor (push); reverse_proxy passes them through.

            """.Replace("\r\n", "\n"), n8n.Directives);

        var cxml = CaddySiteParser.Parse(ArmServer.CxmlSite, "cxml.caddy").Sites.Single();
        Assert.True(cxml.CanTakeOver, cxml.Reason);
        Assert.Equal("cxml-app:8080", cxml.Upstream);
        Assert.Equal("health_uri /health\nhealth_interval 15s\nhealth_timeout 5s\n", cxml.ProxyOptions);
        Assert.Equal("""
            import common

            header {
            	Strict-Transport-Security "max-age=31536000"
            	X-Content-Type-Options "nosniff"
            	Referrer-Policy "strict-origin-when-cross-origin"
            	# Deliberately NO X-Frame-Options / CSP: PunchOut runs in iframes, Blazor needs inline scripts + WebSockets.
            	-Server
            }

            # Blazor (SignalR over WebSockets) and plain HTTP both pass through reverse_proxy unchanged.
            # The app trusts X-Forwarded-For/-Proto, not X-Forwarded-Host.

            """.Replace("\r\n", "\n"), cxml.Directives);

        var main = CaddySiteParser.Parse(ArmServer.MainCaddyfile);
        var common = main.Snippets.Single();
        Assert.Equal("common", common.SnippetName);
        Assert.True(common.SnippetHasTlsInternal);
        Assert.Equal(["sites/*.caddy"], main.ImportPatterns);
        Assert.Empty(main.Sites);
    }

    [Theory]
    [InlineData("a.io {\n\thandle_path /api/* {\n\t\treverse_proxy api:1\n\t}\n\treverse_proxy web:80\n}\n", "routing directive 'handle_path'")]
    [InlineData("a.io {\n\t@ws header Upgrade websocket\n\treverse_proxy web:80\n}\n", "named matcher")]
    [InlineData("a.io {\n\treverse_proxy /api/* api:1\n}\n", "matcher")]
    [InlineData("a.io {\n\treverse_proxy web:80 {\n\t\tto web2:80\n\t}\n}\n", "'to'")]
    [InlineData("a.io {\n\treverse_proxy a:1\n\treverse_proxy b:2\n}\n", "several reverse_proxy")]
    public void Sites_that_route_cannot_be_taken_over(string caddyfile, string reason)
    {
        var site = CaddySiteParser.Parse(caddyfile).Sites.Single();
        Assert.False(site.CanTakeOver);
        Assert.Contains(reason, site.Reason);
    }

    [Fact]
    public void Validates_directives_typed_by_users()
    {
        Assert.Empty(CaddySiteParser.ValidateDirectives("import common\nheader {\n\t-Server\n}\n", proxyOptions: false));
        Assert.Contains("balanced", CaddySiteParser.ValidateDirectives("header {\n\t-Server\n", false).Single());
        Assert.Contains("balanced", CaddySiteParser.ValidateDirectives("}\nevil.io {\n\trespond hi\n", false).Single());
        Assert.Contains("reverse_proxy", CaddySiteParser.ValidateDirectives("reverse_proxy x:1", false).Single());
        Assert.Contains("redir", CaddySiteParser.ValidateDirectives("redir /a /b", false).Single());
        Assert.Empty(CaddySiteParser.ValidateDirectives("health_uri /health\nlb_try_duration 5s", proxyOptions: true));
        Assert.Contains("'to'", CaddySiteParser.ValidateDirectives("to a:1 b:2", proxyOptions: true).Single());
        Assert.True(CaddySiteParser.HandlesTls("import common"));
        Assert.True(CaddySiteParser.HandlesTls("tls internal"));
        Assert.False(CaddySiteParser.HandlesTls("header {\n\t-Server\n}"));
    }

    [Theory]
    [InlineData("app.internal", "https://app.internal")]
    [InlineData("https://app.example.com", "https://app.example.com")]
    [InlineData("http://app.example.com", "http://app.example.com")]
    [InlineData("app.example.com:80", "http://app.example.com")]
    [InlineData("app.example.com:8443", "https://app.example.com:8443")]
    [InlineData("*.example.com", null)]
    [InlineData(":80", null)]
    public void Public_url_from_address(string address, string? expected) =>
        Assert.Equal(expected, CaddySiteParser.PublicUrl(address));
}
