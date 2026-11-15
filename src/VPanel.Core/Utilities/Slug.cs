using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace VPanel.Core.Utilities;

public static partial class Slug
{
    public const int MaxLength = 48;

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,46}[a-z0-9])?$")]
    private static partial Regex ValidRegex();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex InvalidCharsRegex();

    public static bool IsValid(string? slug) => slug is not null && ValidRegex().IsMatch(slug);

    /// <summary>Generates a DNS-safe slug: lowercase ascii, digits and dashes.</summary>
    public static string From(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var normalized = text.Trim().ToLowerInvariant().Replace('đ', 'd').Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in normalized)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        var slug = InvalidCharsRegex().Replace(sb.ToString(), "-").Trim('-');
        if (slug.Length > MaxLength) slug = slug[..MaxLength].Trim('-');
        return slug;
    }
}
