using System.Text;
using System.Text.RegularExpressions;
using VPanel.Core.Abstractions;
using VPanel.Core.Utilities;

namespace VPanel.Infrastructure.Proxy;

/// <summary>Builds Caddyfile site snippets from routes. Routes sharing a host are merged into one site block.</summary>
public static partial class CaddyConfigBuilder
{
    public const string ManagedPrefix = "vp-";
    public const string Extension = ".caddy";

    [GeneratedRegex("[^a-z0-9.-]+")]
    private static partial Regex UnsafeFileChars();

    [GeneratedRegex(@"^[A-Za-z0-9._\-\[\]:]+:\d{1,5}$")]
    private static partial Regex UpstreamRegex();

    public static bool IsValidUpstream(string upstream) => UpstreamRegex().IsMatch(upstream);

    /// <summary>Returns file name → content for all managed site files.</summary>
    public static IReadOnlyDictionary<string, string> Build(IEnumerable<ProxyRoute> routes)
    {
        var sites = new Dictionary<string, List<(string Prefix, ProxyRoute Route)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var route in routes)
        {
            if (!UrlHelper.TryParse(route.PublicUrl, out var uri)) continue;
            if (!IsValidUpstream(route.Upstream)) continue;
            var address = SiteAddress(uri);
            if (!sites.TryGetValue(address, out var list)) sites[address] = list = [];
            list.Add((UrlHelper.PathPrefix(uri), route));
        }

        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (address, entries) in sites.OrderBy(s => s.Key, StringComparer.Ordinal))
            files[FileName(address)] = BuildSite(address, entries);
        return files;
    }

    public static string SiteAddress(Uri uri)
    {
        var host = uri.IdnHost.ToLowerInvariant();
        var port = uri.IsDefaultPort ? "" : $":{uri.Port}";
        return uri.Scheme == "http" ? $"http://{host}{port}" : $"{host}{port}";
    }

    public static string FileName(string address)
    {
        var name = UnsafeFileChars().Replace(address.ToLowerInvariant().Replace("http://", "http-").Replace(':', '-'), "-").Trim('-');
        return ManagedPrefix + name + Extension;
    }

    private static string BuildSite(string address, List<(string Prefix, ProxyRoute Route)> entries)
    {
        var sb = new StringBuilder();
        sb.Append("# Managed by v-panel. Do not edit: regenerated on every deploy.\n");
        foreach (var e in entries) sb.Append("# route ").Append(e.Route.Id).Append(" -> ").Append(e.Route.Upstream).Append('\n');
        sb.Append(address).Append(" {\n");

        // longest prefix first; a duplicate prefix keeps the first route
        var ordered = entries.GroupBy(e => e.Prefix).Select(g => g.First()).OrderByDescending(e => e.Prefix.Length).ToList();
        if (ordered.Count == 1 && ordered[0].Prefix.Length == 0)
        {
            sb.Append("\treverse_proxy ").Append(ordered[0].Route.Upstream).Append('\n');
        }
        else
        {
            foreach (var (prefix, route) in ordered)
            {
                if (prefix.Length == 0)
                {
                    sb.Append("\thandle {\n\t\treverse_proxy ").Append(route.Upstream).Append("\n\t}\n");
                    continue;
                }
                sb.Append("\tredir ").Append(prefix).Append(' ').Append(prefix).Append("/ 308\n");
                sb.Append("\thandle_path ").Append(prefix).Append("/* {\n");
                sb.Append("\t\treverse_proxy ").Append(route.Upstream).Append('\n');
                sb.Append("\t}\n");
            }
        }
        sb.Append("}\n");
        return sb.ToString();
    }

    public static string MainCaddyfile(string configDirInContainer) =>
        $$"""
        # Main Caddyfile, created by v-panel.
        # v-panel writes one file per site into ./sites (files named vp-*.caddy are managed and regenerated).
        # You can add your own sites below or as other files in ./sites.
        {
        	# email admin@example.com
        }

        import {{configDirInContainer.TrimEnd('/')}}/sites/*.caddy

        """;
}
