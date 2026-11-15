using VPanel.Core.Utilities;

namespace VPanel.Tests.Core;

public class SlugAndUrlTests
{
    [Theory]
    [InlineData("Procurement 01", "procurement-01")]
    [InlineData("Quản lý Đơn hàng", "quan-ly-don-hang")]
    [InlineData("  --Hello__World--  ", "hello-world")]
    public void Slug_from_text(string input, string expected) => Assert.Equal(expected, Slug.From(input));

    [Theory]
    [InlineData("a", true)]
    [InlineData("app-01", true)]
    [InlineData("-app", false)]
    [InlineData("App", false)]
    [InlineData("app_01", false)]
    [InlineData("", false)]
    public void Slug_validation(string slug, bool valid) => Assert.Equal(valid, Slug.IsValid(slug));

    [Theory]
    [InlineData("example.com", "https", "example.com", "")]
    [InlineData("http://a.example.com/app/", "http", "a.example.com", "/app")]
    [InlineData("https://x.io:8443/p/q", "https", "x.io", "/p/q")]
    public void Url_parsing(string text, string scheme, string host, string path)
    {
        Assert.True(UrlHelper.TryParse(text, out var uri));
        Assert.Equal(scheme, uri.Scheme);
        Assert.Equal(host, uri.Host);
        Assert.Equal(path, UrlHelper.PathPrefix(uri));
    }

    [Theory]
    [InlineData("ftp://x")]
    [InlineData("not a url")]
    [InlineData("")]
    public void Url_rejects_invalid(string text) => Assert.False(UrlHelper.TryParse(text, out _));
}
