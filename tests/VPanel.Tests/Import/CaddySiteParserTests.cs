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
        Assert.True(sites["app.internal"].IsSimple);
        Assert.True(sites["app.internal"].TlsInternal);
        Assert.Equal("app:8080", sites["app.internal"].Upstream);
        Assert.Equal(13, sites["app.internal"].Line);
        Assert.True(sites["http://plain.example.com"].IsSimple);
        Assert.Equal("plain:80", sites["http://plain.example.com"].Upstream);
        Assert.False(sites["complex.example.com"].IsSimple);
        Assert.Contains("nested", sites["complex.example.com"].Reason);
        Assert.False(sites["a.example.com, b.example.com"].IsSimple);
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
