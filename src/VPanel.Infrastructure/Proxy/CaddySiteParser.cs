using System.Text;
using System.Text.RegularExpressions;
using VPanel.Core.Utilities;

namespace VPanel.Infrastructure.Proxy;

/// <summary>A top-level block of a Caddyfile (site, global options or snippet).</summary>
/// <param name="Addresses">Site addresses; empty for the global options block.</param>
/// <param name="Start">Offset of the first character of the header line.</param>
/// <param name="End">Offset just after the closing brace line (including its newline).</param>
/// <param name="Upstream">The single <c>reverse_proxy</c> upstream (host:port), when there is one.</param>
/// <param name="TlsInternal">The block itself contains <c>tls internal</c>.</param>
/// <param name="CanTakeOver">
/// One host address and exactly one <c>reverse_proxy host:port</c> without matcher or extra upstreams, and no routing
/// directives (handle, route, redir, matchers...): v-panel can generate an equivalent block, carrying the other directives
/// (<see cref="Directives"/>) and the <c>reverse_proxy</c> options (<see cref="ProxyOptions"/>) over verbatim.
/// </param>
/// <param name="Reason">Why the block cannot be taken over.</param>
/// <param name="Directives">Site directives other than <c>reverse_proxy</c>, original text (comments kept), dedented.</param>
/// <param name="ProxyOptions">Body of the <c>reverse_proxy { ... }</c> block, original text, dedented.</param>
/// <param name="Imports">Snippets imported directly in the site (<c>import name</c>).</param>
public sealed record CaddySiteBlock(
    string File,
    IReadOnlyList<string> Addresses,
    int Start,
    int End,
    int Line,
    bool IsSnippet,
    string? Upstream,
    bool TlsInternal,
    bool CanTakeOver,
    string? Reason,
    string? Directives = null,
    string? ProxyOptions = null,
    IReadOnlyList<string>? Imports = null)
{
    /// <summary>Snippet name for snippet blocks (<c>(common)</c> → common).</summary>
    public string? SnippetName { get; init; }

    /// <summary>For snippets: the snippet contains <c>tls internal</c>.</summary>
    public bool SnippetHasTlsInternal { get; init; }

    /// <summary>Name of an imported snippet that provides <c>tls internal</c> (filled in once all files are read).</summary>
    public string? TlsViaSnippet { get; init; }
    public bool IsGlobalOptions => Addresses.Count == 0 && !IsSnippet;
    public bool IsSite => Addresses.Count > 0 && !IsSnippet;
    public string Address => string.Join(", ", Addresses);
}

/// <summary>A parsed Caddyfile: its top-level blocks and top-level directives (e.g. <c>import</c> lines).</summary>
public sealed record CaddyFileModel(IReadOnlyList<CaddySiteBlock> Blocks, IReadOnlyList<string> TopLevelLines)
{
    public IEnumerable<CaddySiteBlock> Sites => Blocks.Where(b => b.IsSite);
    public IEnumerable<CaddySiteBlock> Snippets => Blocks.Where(b => b.IsSnippet);

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
            blocks.Add(Analyze(file, header, text, masked, open, close, headerStart, end, LineOf(text, headerStart)));
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

    /// <summary>Directives that route requests: a site using them is more than "one upstream" and stays hand-written.</summary>
    public static readonly string[] RoutingDirectives =
        ["handle", "handle_path", "handle_errors", "route", "redir", "rewrite", "uri", "respond", "file_server", "php_fastcgi",
         "try_files", "map", "invoke", "abort", "error", "copy_response", "forward_auth", "templates", "vars", "method"];

    /// <summary>One top-level directive of a block body: its name, the masked first line and the original text (comments before it included).</summary>
    private sealed record Segment(string Name, string[] Tokens, string Text, int BodyOpen, int BodyClose, string LeadingComments);

    private static CaddySiteBlock Analyze(string file, string header, string text, string masked, int open, int close, int start, int end, int line)
    {
        if (header.Length == 0) return new(file, [], start, end, line, false, null, false, false, "global options");
        var segments = Segments(text, masked, open + 1, close);
        if (header.StartsWith('('))
            return new(file, [], start, end, line, true, null, false, false, "snippet")
            {
                SnippetName = header.Trim('(', ')', ' '),
                SnippetHasTlsInternal = segments.Any(g => g.Tokens is ["tls", "internal", ..]),
            };

        var addresses = Addresses(header);
        string? upstream = null, reason = null, options = null;
        var tlsInternal = false;
        var proxies = 0;
        var directives = new StringBuilder();
        var proxyComments = "";
        var imports = new List<string>();
        foreach (var g in segments)
        {
            if (g.Name == "reverse_proxy")
            {
                proxies++;
                proxyComments = g.LeadingComments;
                var args = g.Tokens.Skip(1).Where(t => t != "{").ToList();
                if (args.Count == 1 && !IsMatcher(args[0]))
                {
                    var u = args[0].StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? args[0][7..] : args[0];
                    if (UpstreamRegex().IsMatch(u)) upstream = u;
                    else reason ??= $"unsupported upstream '{args[0]}'";
                }
                else reason ??= args.Count == 0 ? "reverse_proxy without inline upstream" : "reverse_proxy with a matcher or several upstreams";
                if (g.BodyOpen >= 0)
                {
                    options = Dedent(text[(g.BodyOpen + 1)..g.BodyClose]);
                    foreach (var o in Segments(text, masked, g.BodyOpen + 1, g.BodyClose))
                        if (o.Name == "to") reason ??= "reverse_proxy with 'to' (several upstreams)";
                }
                continue;
            }
            if (g.Name.StartsWith('@')) reason ??= $"named matcher '{g.Name}'";
            else if (RoutingDirectives.Contains(g.Name)) reason ??= $"routing directive '{g.Name}'";
            if (g.Tokens is ["tls", "internal", ..]) tlsInternal = true;
            if (g.Tokens is ["import", var snippet, ..]) imports.Add(snippet);
            directives.Append(g.Text);
        }
        // Comments written just above reverse_proxy stay right above it (end of the directives).
        directives.Append(proxyComments);

        if (proxies != 1) reason ??= proxies == 0 ? "no reverse_proxy" : "several reverse_proxy directives";
        if (addresses.Count != 1) reason ??= "several addresses";
        else if (PublicUrl(addresses[0]) is null) reason ??= "address is not a single host name";
        var canTakeOver = reason is null && upstream is not null;
        return new(file, addresses, start, end, line, false, upstream, tlsInternal, canTakeOver, reason,
            NullIfBlank(Dedent(directives.ToString())), NullIfBlank(options), imports);
    }

    /// <summary>
    /// Problems with text a user puts into a generated site block: unbalanced braces, and for site directives anything that
    /// would route requests (the route itself is generated); for reverse_proxy options, extra upstreams.
    /// </summary>
    public static IReadOnlyList<string> ValidateDirectives(string? text, bool proxyOptions)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return errors;
        var what = proxyOptions ? "reverse_proxy options" : "Caddy site directives";
        var wrapped = "x.invalid {\n" + text.Replace("\r\n", "\n") + "\n}\n";
        var masked = Mask(wrapped);
        var open = NextBrace(masked, 0, '{');
        var close = MatchingClose(masked, open);
        if (close < 0 || masked[(close + 1)..].Trim().Length > 0 || NextBrace(masked, close + 1, '}') >= 0)
        {
            errors.Add($"{what}: braces are not balanced.");
            return errors;
        }
        foreach (var g in Segments(wrapped, masked, open + 1, close))
        {
            if (proxyOptions)
            {
                if (g.Name == "to") errors.Add($"{what}: 'to' is not allowed (the upstream comes from the proxy service and port).");
                continue;
            }
            if (g.Name == "reverse_proxy") errors.Add($"{what}: reverse_proxy is generated from the proxy service and port; put its options in 'reverse_proxy options'.");
            else if (g.Name.StartsWith('@')) errors.Add($"{what}: named matchers ({g.Name}) are not supported in generated sites.");
            else if (RoutingDirectives.Contains(g.Name)) errors.Add($"{what}: routing directive '{g.Name}' is not supported in generated sites.");
        }
        return errors;
    }

    /// <summary>True when the directives already decide TLS (a tls directive, or an import that may contain one).</summary>
    public static bool HandlesTls(string? directives)
    {
        if (string.IsNullOrWhiteSpace(directives)) return false;
        var wrapped = "x {\n" + directives + "\n}\n";
        var masked = Mask(wrapped);
        var open = NextBrace(masked, 0, '{');
        var close = MatchingClose(masked, open);
        if (close < 0) return false;
        return Segments(wrapped, masked, open + 1, close).Any(g => g.Name is "tls" or "import");
    }

    private static bool IsMatcher(string token) => token.StartsWith('@') || token.StartsWith('/') || token == "*";

    /// <summary>Splits a block body into its top-level directives (a directive with a block runs to its closing brace).</summary>
    private static List<Segment> Segments(string text, string masked, int from, int to)
    {
        var list = new List<Segment>();
        var pending = new StringBuilder();
        var pos = from;
        // the remainder of the line holding "{" belongs to nothing
        var firstNl = masked.IndexOf('\n', pos);
        pos = firstNl < 0 || firstNl >= to ? to : firstNl + 1;
        while (pos < to)
        {
            var lineEnd = masked.IndexOf('\n', pos);
            if (lineEnd < 0 || lineEnd > to) lineEnd = to;
            var maskedLine = masked[pos..lineEnd];
            if (maskedLine.Trim().Length == 0)
            {
                pending.Append(text[pos..Math.Min(lineEnd + 1, to)]);
                pos = lineEnd + 1;
                continue;
            }
            var bodyOpen = NextBrace(masked[..lineEnd], pos, '{');
            int bodyClose = -1, segEnd = lineEnd;
            if (bodyOpen >= 0)
            {
                bodyClose = MatchingClose(masked, bodyOpen);
                if (bodyClose < 0 || bodyClose > to) bodyClose = to;
                var nl = masked.IndexOf('\n', bodyClose);
                segEnd = nl < 0 || nl > to ? to : nl;
            }
            var tokens = (bodyOpen >= 0 ? masked[pos..bodyOpen] : maskedLine).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var segText = text[pos..Math.Min(segEnd + 1, to)];
            if (!segText.EndsWith('\n')) segText += "\n";
            var comments = pending.ToString();
            list.Add(new Segment(tokens.FirstOrDefault() ?? "", tokens, comments + segText, bodyOpen, bodyClose, comments));
            pending.Clear();
            pos = segEnd + 1;
        }
        if (pending.Length > 0 && list.Count > 0)
        {
            var last = list[^1];
            list[^1] = last with { Text = last.Text + pending };
        }
        else if (pending.Length > 0) list.Add(new Segment("", [], pending.ToString(), -1, -1, ""));
        return list;
    }

    /// <summary>Removes the indentation common to all non-blank lines and surrounding blank lines.</summary>
    internal static string Dedent(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        while (lines.Count > 0 && lines[0].Trim().Length == 0) lines.RemoveAt(0);
        while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
        if (lines.Count == 0) return "";
        var indent = lines.Where(l => l.Trim().Length > 0).Min(l => l.Length - l.TrimStart(' ', '\t').Length);
        return string.Join('\n', lines.Select(l => l.Trim().Length == 0 ? "" : l[indent..].TrimEnd())) + "\n";
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

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
