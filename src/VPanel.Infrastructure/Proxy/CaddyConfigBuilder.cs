using System.Text;
using System.Text.RegularExpressions;
using VPanel.Core.Abstractions;
using VPanel.Core.Configuration;
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
    /// <param name="routes">Desired routes.</param>
    /// <param name="mode">
    /// In <see cref="IngressMode.CloudflareTunnel"/> mode every site is served over plain HTTP (cloudflared terminates TLS at
    /// Cloudflare and forwards to http://caddy:80), and upstreams of https routes are told the original scheme.
    /// </param>
    public static IReadOnlyDictionary<string, string> Build(IEnumerable<ProxyRoute> routes, IngressMode mode = IngressMode.Direct)
    {
        var sites = new Dictionary<string, List<SiteEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var route in routes)
        {
            if (!UrlHelper.TryParse(route.PublicUrl, out var uri)) continue;
            if (!IsValidUpstream(route.Upstream)) continue;
            var address = SiteAddress(uri, mode);
            if (!sites.TryGetValue(address, out var list)) sites[address] = list = [];
            var forwardHttps = mode == IngressMode.CloudflareTunnel && uri.Scheme == "https";
            list.Add(new SiteEntry(UrlHelper.PathPrefix(uri), route, forwardHttps));
        }

        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (address, entries) in sites.OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            // Internal CA (hosts without public DNS/certificates). Plain-HTTP sites (tunnel mode, http://) have no TLS at all.
            var tlsInternal = !address.StartsWith("http://", StringComparison.Ordinal) && entries.Any(e => e.Route.TlsInternal);
            files[FileName(address)] = BuildSite(address, entries, tlsInternal);
        }
        return files;
    }

    public static string SiteAddress(Uri uri, IngressMode mode = IngressMode.Direct)
    {
        var host = uri.IdnHost.ToLowerInvariant();
        if (mode == IngressMode.CloudflareTunnel)
        {
            // cloudflared always talks plain HTTP to caddy:80; the public port (443) belongs to Cloudflare's edge.
            var httpPort = uri.Scheme == "http" && !uri.IsDefaultPort ? $":{uri.Port}" : "";
            return $"http://{host}{httpPort}";
        }
        var port = uri.IsDefaultPort ? "" : $":{uri.Port}";
        return uri.Scheme == "http" ? $"http://{host}{port}" : $"{host}{port}";
    }

    public static string FileName(string address)
    {
        var name = UnsafeFileChars().Replace(address.ToLowerInvariant().Replace("http://", "http-").Replace(':', '-'), "-").Trim('-');
        return ManagedPrefix + name + Extension;
    }

    private sealed record SiteEntry(string Prefix, ProxyRoute Route, bool ForwardHttps);

    private static string BuildSite(string address, List<SiteEntry> entries, bool tlsInternal)
    {
        var sb = new StringBuilder();
        sb.Append("# Managed by v-panel. Do not edit: regenerated on every deploy.\n");
        foreach (var e in entries) sb.Append("# route ").Append(e.Route.Id).Append(" -> ").Append(e.Route.Upstream).Append('\n');
        sb.Append(address).Append(" {\n");
        if (tlsInternal) sb.Append("\ttls internal\n");

        // longest prefix first; a duplicate prefix keeps the first route
        var ordered = entries.GroupBy(e => e.Prefix).Select(g => g.First()).OrderByDescending(e => e.Prefix.Length).ToList();
        if (ordered.Count == 1 && ordered[0].Prefix.Length == 0)
        {
            AppendReverseProxy(sb, "\t", ordered[0]);
        }
        else
        {
            foreach (var e in ordered)
            {
                if (e.Prefix.Length == 0)
                {
                    sb.Append("\thandle {\n");
                    AppendReverseProxy(sb, "\t\t", e);
                    sb.Append("\t}\n");
                    continue;
                }
                sb.Append("\tredir ").Append(e.Prefix).Append(' ').Append(e.Prefix).Append("/ 308\n");
                sb.Append("\thandle_path ").Append(e.Prefix).Append("/* {\n");
                AppendReverseProxy(sb, "\t\t", e);
                sb.Append("\t}\n");
            }
        }
        sb.Append("}\n");
        return sb.ToString();
    }

    private static void AppendReverseProxy(StringBuilder sb, string indent, SiteEntry e)
    {
        sb.Append(indent).Append("reverse_proxy ").Append(e.Route.Upstream);
        if (!e.ForwardHttps)
        {
            sb.Append('\n');
            return;
        }
        // Caddy sees plain HTTP from cloudflared and would otherwise send "X-Forwarded-Proto: http": apps that
        // enforce HTTPS would redirect forever. The visitor used https at Cloudflare's edge.
        sb.Append(" {\n");
        sb.Append(indent).Append("\theader_up X-Forwarded-Proto https\n");
        sb.Append(indent).Append("}\n");
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
