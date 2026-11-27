using System.Text;
using System.Text.RegularExpressions;
using VPanel.Core.Utilities;

namespace VPanel.Infrastructure.Proxy;

/// <summary>A top-level block of a Caddyfile (site, global options or snippet).</summary>
/// <param name="Addresses">Site addresses; empty for the global options block.</param>
/// <param name="Start">Offset of the first character of the header line.</param>
/// <param name="End">Offset just after the closing brace line (including its newline).</param>
/// <param name="Upstream">Single <c>reverse_proxy</c> upstream (host:port) when the block is simple.</param>
/// <param name="IsSimple">
/// One address and nothing but one <c>reverse_proxy host:port</c>, optionally <c>tls internal</c> and <c>encode</c>:
/// v-panel can generate an equivalent block and take the site over.
/// </param>
/// <param name="Reason">Why the block is not simple.</param>
public sealed record CaddySiteBlock(
    string File,
    IReadOnlyList<string> Addresses,
    int Start,
    int End,
    int Line,
    bool IsSnippet,
    string? Upstream,
    bool TlsInternal,
    bool IsSimple,
    string? Reason)
{
    public bool IsGlobalOptions => Addresses.Count == 0 && !IsSnippet;
    public bool IsSite => Addresses.Count > 0 && !IsSnippet;
    public string Address => string.Join(", ", Addresses);
}

/// <summary>A parsed Caddyfile: its top-level blocks and top-level directives (e.g. <c>import</c> lines).</summary>
public sealed record CaddyFileModel(IReadOnlyList<CaddySiteBlock> Blocks, IReadOnlyList<string> TopLevelLines)
{
    public IEnumerable<CaddySiteBlock> Sites => Blocks.Where(b => b.IsSite);

    public IEnumerable<string> ImportPatterns => TopLevelLines
        .Select(l => l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        .Where(t => t.Length >= 2 && t[0] == "import")
        .Select(t => t[1]);
}

/// <summary>
/// Small, lenient Caddyfile reader: finds top-level blocks with Caddy's brace rules (a brace only opens/closes a block when
/// it is a token of its own, so placeholders like <c>{remote_host}</c> are ignored) and recognises simple reverse-proxy sites.
/// It never rewrites anything except removing a whole site block.
/// </summary>
public static partial class CaddySiteParser
{
    [GeneratedRegex(@"^[A-Za-z0-9._\-\[\]:]+:\d{1,5}$")]
    private static partial Regex UpstreamRegex();

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9.\-]*[A-Za-z0-9])?$")]
    private static partial Regex HostRegex();

    public static CaddyFileModel Parse(string text, string file = "")
    {
        text = text.Replace("\r\n", "\n");
        var masked = Mask(text);
        var blocks = new List<CaddySiteBlock>();
        var topLevel = new List<string>();
        var pos = 0;
        while (pos < masked.Length)
        {
            var open = NextBrace(masked, pos, '{');
            if (open < 0)
            {
                AddLines(topLevel, masked[pos..]);
                break;
            }

            // Header = the line holding "{" (plus previous lines ending with a comma: multi-line address lists).
            var headerStart = LineStart(masked, open);
            while (headerStart > pos)
            {
                var prevEnd = headerStart - 1;
                var prevStart = LineStart(masked, prevEnd);
                if (!masked[prevStart..prevEnd].TrimEnd().EndsWith(',')) break;
                headerStart = prevStart;
            }
            AddLines(topLevel, masked[pos..headerStart]);
            var header = masked[headerStart..open].Trim();

            var close = MatchingClose(masked, open);
            if (close < 0)
            {
                // Unbalanced file: report what we have, never guess further.
                blocks.Add(new CaddySiteBlock(file, Addresses(header), headerStart, masked.Length, LineOf(text, headerStart),
                    header.StartsWith('('), null, false, false, "unbalanced braces"));
                break;
            }
            var end = masked.IndexOf('\n', close);
            end = end < 0 ? masked.Length : end + 1;
            var body = masked[(open + 1)..close];
            blocks.Add(Analyze(file, header, body, headerStart, end, LineOf(text, headerStart)));
            pos = end;
        }
        return new CaddyFileModel(blocks, topLevel);
    }

    /// <summary>
    /// Removes the site block whose only address is <paramref name="address"/>. Returns null when no such block exists.
    /// <paramref name="isEmpty"/> tells whether anything but comments and blank lines remains.
    /// </summary>
    public static string? RemoveSite(string text, string address, out bool isEmpty)
    {
        text = text.Replace("\r\n", "\n");
        isEmpty = false;
        var block = Parse(text).Sites.FirstOrDefault(b => b.Addresses.Count == 1 && SameAddress(b.Addresses[0], address));
        if (block is null) return null;
        var result = text[..block.Start] + text[block.End..];
        isEmpty = Mask(result).Trim().Length == 0;
        return result;
    }

    public static bool SameAddress(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Public URL for a site address: <c>http://</c> prefix or port 80 means http, anything else https.</summary>
    public static string? PublicUrl(string address)
    {
        var a = address.Trim();
        var scheme = "https";
        if (a.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) { scheme = "http"; a = a[7..]; }
        else if (a.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) a = a[8..];
        var slash = a.IndexOf('/');
        var path = "";
        if (slash >= 0) { path = a[slash..].TrimEnd('*').TrimEnd('/'); a = a[..slash]; }
        var host = a;
        int? port = null;
        var colon = a.LastIndexOf(':');
        if (colon > 0 && int.TryParse(a[(colon + 1)..], out var p)) { host = a[..colon]; port = p; }
        if (!HostRegex().IsMatch(host)) return null;
        if (port == 80 && scheme == "https") scheme = "http";
        var defaultPort = scheme == "http" ? 80 : 443;
        var url = $"{scheme}://{host.ToLowerInvariant()}{(port is { } x && x != defaultPort ? $":{x}" : "")}{path}";
        return UrlHelper.TryParse(url, out _) ? url : null;
    }

    private static CaddySiteBlock Analyze(string file, string header, string body, int start, int end, int line)
    {
        if (header.Length == 0) return new(file, [], start, end, line, false, null, false, false, "global options");
        if (header.StartsWith('(')) return new(file, [], start, end, line, true, null, false, false, "snippet");
        var addresses = Addresses(header);
        string? upstream = null, reason = null;
        var tlsInternal = false;
        var proxies = 0;
        if (NextBrace(body, 0, '{') >= 0) reason = "contains nested blocks";
        foreach (var raw in body.Split('\n'))
        {
            var tokens = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) continue;
            switch (tokens[0])
            {
                case "reverse_proxy" when tokens.Length == 2:
                    proxies++;
                    var u = tokens[1].StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? tokens[1][7..] : tokens[1];
                    if (UpstreamRegex().IsMatch(u)) upstream = u;
                    else reason ??= $"unsupported upstream '{tokens[1]}'";
                    break;
                case "reverse_proxy":
                    proxies++;
                    reason ??= "reverse_proxy with several upstreams or options";
                    break;
                case "tls" when tokens is [_, "internal"]:
                    tlsInternal = true;
                    break;
                case "encode":
                    break;
                default:
                    reason ??= $"directive '{tokens[0]}'";
                    break;
            }
        }
        if (proxies != 1) reason ??= proxies == 0 ? "no reverse_proxy" : "several reverse_proxy directives";
        if (addresses.Count != 1) reason ??= "several addresses";
        else if (PublicUrl(addresses[0]) is null) reason ??= "address is not a single host name";
        var simple = reason is null && upstream is not null;
        return new(file, addresses, start, end, line, false, upstream, tlsInternal, simple, reason);
    }

    private static List<string> Addresses(string header) =>
        header.Split([' ', '\t', '\n', ','], StringSplitOptions.RemoveEmptyEntries).ToList();

    private static void AddLines(List<string> into, string text)
    {
        foreach (var l in text.Split('\n'))
            if (l.Trim() is { Length: > 0 } t) into.Add(t);
    }

    /// <summary>Same text with comments blanked out (same length, so offsets stay valid).</summary>
    internal static string Mask(string text)
    {
        var sb = new StringBuilder(text);
        var inQuote = false;
        for (var i = 0; i < sb.Length; i++)
        {
            var c = sb[i];
            if (c == '\n') { inQuote = false; continue; }
            if (c == '"' && (i == 0 || sb[i - 1] != '\\')) { inQuote = !inQuote; continue; }
            if (c == '#' && !inQuote && (i == 0 || char.IsWhiteSpace(sb[i - 1])))
                while (i < sb.Length && sb[i] != '\n') sb[i++] = ' ';
            if (i < sb.Length && sb[i] == '\n') inQuote = false;
        }
        return sb.ToString();
    }

    private static bool IsToken(string s, int i) =>
        (i == 0 || char.IsWhiteSpace(s[i - 1])) && (i + 1 >= s.Length || char.IsWhiteSpace(s[i + 1]));

    private static int NextBrace(string s, int from, char brace)
    {
        for (var i = from; i < s.Length; i++)
            if (s[i] == brace && IsToken(s, i)) return i;
        return -1;
    }

    private static int MatchingClose(string s, int open)
    {
        var depth = 0;
        for (var i = open; i < s.Length; i++)
        {
            if (!IsToken(s, i)) continue;
            if (s[i] == '{') depth++;
            else if (s[i] == '}' && --depth == 0) return i;
        }
        return -1;
    }

    private static int LineStart(string s, int i)
    {
        var nl = i <= 0 ? -1 : s.LastIndexOf('\n', i - 1);
        return nl + 1;
    }

    private static int LineOf(string s, int offset) => s.AsSpan(0, Math.Min(offset, s.Length)).Count('\n') + 1;
}
