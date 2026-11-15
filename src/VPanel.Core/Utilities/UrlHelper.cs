namespace VPanel.Core.Utilities;

public static class UrlHelper
{
    /// <summary>Parses a free-text public URL; scheme defaults to https.</summary>
    public static bool TryParse(string? text, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim();
        if (!t.Contains("://", StringComparison.Ordinal)) t = "https://" + t;
        if (!Uri.TryCreate(t, UriKind.Absolute, out var u)) return false;
        if (u.Scheme is not ("http" or "https")) return false;
        if (string.IsNullOrEmpty(u.Host)) return false;
        uri = u;
        return true;
    }

    public static string? HostOf(string? text) => TryParse(text, out var u) ? u.Host : null;

    /// <summary>Path prefix without trailing slash ("" for root).</summary>
    public static string PathPrefix(Uri uri) => uri.AbsolutePath.TrimEnd('/');
}
