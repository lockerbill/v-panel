using System.Text;
using VPanel.Core.Configuration;
using VPanel.Core.Domain;
using VPanel.Core.Utilities;

namespace VPanel.Infrastructure.Docker;

public sealed record RenderedInstance(
    string ProjectName,
    string Compose,
    string Env,
    string? Override,
    string? ProxyAlias,
    IReadOnlyDictionary<string, string> EnvValues,
    ComposeAnalysis Analysis,
    string ConfigHash)
{
    public const string ComposeFile = AppInstance.DefaultComposeFile;
    public const string EnvFileName = ".env";
    public const string OverrideFile = "docker-compose.vpanel.yml";
}

/// <summary>Pure function: instance + settings → files written to the install folder.</summary>
public static class ComposeRenderer
{
    public static string ProxyAlias(AppInstance instance, string service) =>
        $"{instance.ProjectName}-{Slug.From(service)}";

    public static Dictionary<string, string> ManagedVariables(AppInstance instance, PanelSettings settings)
    {
        var url = instance.PublicUrl?.Trim() ?? "";
        var vars = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["COMPOSE_PROJECT_NAME"] = instance.ProjectName,
            ["VPANEL_APP"] = instance.App?.Slug ?? "",
            ["VPANEL_INSTANCE"] = instance.Slug,
            ["VPANEL_INSTANCE_NAME"] = instance.Name,
            ["VPANEL_PUBLIC_URL"] = UrlHelper.TryParse(url, out var u) ? u.ToString().TrimEnd('/') : url,
            ["VPANEL_HOST"] = UrlHelper.HostOf(url) ?? "",
            ["VPANEL_SHARED_NETWORK"] = settings.SharedNetwork,
            ["VPANEL_INSTALL_PATH"] = instance.InstallPath,
            ["VPANEL_CADDY_DIR"] = settings.CaddyDir,
        };
        if (instance.EffectiveProxyService is { Length: > 0 } svc) vars["VPANEL_PROXY_ALIAS"] = ProxyAlias(instance, svc);
        return vars;
    }

    public static RenderedInstance Render(AppInstance instance, PanelSettings settings)
    {
        ArgumentNullException.ThrowIfNull(instance.App, "instance.App must be loaded");
        var managed = ManagedVariables(instance, settings);
        var merged = EnvFile.Parse(instance.App.EnvTemplate).MergeWith(EnvFile.Parse(instance.EnvOverrides));
        var envText = merged.Render(managed);

        var values = new Dictionary<string, string>(merged.ToDictionary(), StringComparer.Ordinal);
        foreach (var (k, v) in managed) values[k] = v;
        // resolve ${VAR} references inside env values for offline analysis
        foreach (var k in values.Keys.ToList()) values[k] = ComposeAnalyzer.Interpolate(values[k], values);

        var compose = instance.EffectiveCompose;
        var analysis = ComposeAnalyzer.Analyze(compose, values);

        string? overrideYaml = null, alias = null;
        var proxySvc = instance.EffectiveProxyService;
        var service = analysis.Services.FirstOrDefault(s => s.Name == proxySvc);
        if (service is not null && service.NetworkMode is null)
        {
            var joined = analysis.JoinedNetwork(service, settings.SharedNetwork);
            if (joined is { Aliases.Count: > 0 })
            {
                // Already reachable on the shared network under its own alias (typical for hand-written stacks):
                // route to that alias and leave the compose model untouched, so containers are not recreated.
                alias = joined.Aliases[0];
            }
            else
            {
                alias = ProxyAlias(instance, service.Name);
                overrideYaml = BuildOverride(service, alias, settings.SharedNetwork, joined?.Key);
            }
        }

        string?[] parts = [compose, envText, overrideYaml, instance.PublicUrl, proxySvc, instance.EffectiveProxyPort?.ToString(), instance.InstallPath];
        // Options added later only take part when set, so hashes of existing instances stay the same.
        if (instance.TlsInternal) parts = [.. parts, "tls:internal"];
        if (!string.IsNullOrWhiteSpace(instance.ProxyDirectives)) parts = [.. parts, "directives:" + instance.ProxyDirectives];
        if (!string.IsNullOrWhiteSpace(instance.ProxyOptions)) parts = [.. parts, "proxy-options:" + instance.ProxyOptions];
        if (instance.BuildMode != BuildMode.Build) parts = [.. parts, $"build:{instance.BuildMode}"];
        if (instance.ComposeFileName is { Length: > 0 } file && file != AppInstance.DefaultComposeFile) parts = [.. parts, $"file:{file}"];
        var hash = Hashing.Sha256(parts);
        return new RenderedInstance(instance.ProjectName, compose, envText, overrideYaml, alias, values, analysis, hash);
    }

    /// <summary>
    /// Attaches the proxied service to the shared network with a unique alias, so the shared Caddy
    /// container can reach it as <c>alias:port</c>. Keeps the implicit default network when the
    /// service does not declare networks.
    /// </summary>
    /// <param name="existingKey">
    /// Key under which the compose file already joins the shared network: the alias is added to that entry
    /// (a second key for the same docker network would attach the container twice, which docker refuses).
    /// </param>
    public static string BuildOverride(ComposeServiceInfo service, string alias, string sharedNetwork, string? existingKey = null)
    {
        var sb = new StringBuilder();
        sb.Append("# Generated by v-panel. Do not edit: overwritten on every deploy.\n");
        sb.Append("services:\n");
        sb.Append("  ").Append(Q(service.Name)).Append(":\n");
        sb.Append("    networks:\n");
        if (existingKey is not null)
        {
            sb.Append("      ").Append(Q(existingKey)).Append(":\n");
            sb.Append("        aliases:\n");
            sb.Append("          - ").Append(Q(alias)).Append('\n');
            return sb.ToString();
        }
        if (!service.HasExplicitNetworks) sb.Append("      default: {}\n");
        sb.Append("      vpanel_shared:\n");
        sb.Append("        aliases:\n");
        sb.Append("          - ").Append(Q(alias)).Append('\n');
        sb.Append("networks:\n");
        sb.Append("  vpanel_shared:\n");
        sb.Append("    external: true\n");
        sb.Append("    name: ").Append(Q(sharedNetwork)).Append('\n');
        return sb.ToString();
    }

    private static string Q(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
