using System.Text.RegularExpressions;
using VPanel.Core.Abstractions;
using VPanel.Core.Domain;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace VPanel.Infrastructure.Docker;

public sealed record ComposeVolumeInfo(string Name, VolumeKind Kind, string? Source, string? Service, string? Target);

/// <summary>A network a service joins (key as written under the service), with its aliases on it.</summary>
public sealed record ComposeServiceNetwork(string Key, IReadOnlyList<string> Aliases);

public sealed record ComposeServiceInfo(string Name, string? Image, bool HasBuild, bool HasExplicitNetworks, string? NetworkMode,
    IReadOnlyList<string> Ports, IReadOnlyList<string> ExposedPorts, IReadOnlyList<ComposeServiceNetwork> Networks);

public sealed record ComposeAnalysis(
    IReadOnlyList<ComposeServiceInfo> Services,
    IReadOnlyList<ComposeVolumeInfo> Volumes,
    IReadOnlyList<string> ExternalNetworks,
    IReadOnlyList<ValidationIssue> Issues,
    IReadOnlyDictionary<string, string>? NetworkNames = null)
{
    public bool IsValid => Issues.All(i => i.IsWarning);

    /// <summary>
    /// Docker name of a top-level network key: its <c>name:</c> (interpolated), or the key itself for an external network.
    /// Null for project-scoped networks without a fixed name (docker names them &lt;project&gt;_&lt;key&gt;).
    /// </summary>
    public string? NetworkName(string key) => NetworkNames is not null && NetworkNames.TryGetValue(key, out var n) ? n : null;

    /// <summary>The network entry through which <paramref name="service"/> joins the docker network named <paramref name="networkName"/>.</summary>
    public ComposeServiceNetwork? JoinedNetwork(ComposeServiceInfo service, string networkName) =>
        service.Networks.FirstOrDefault(n => NetworkName(n.Key) == networkName);
}

/// <summary>
/// Offline, best-effort analysis of a compose file with YamlDotNet (used by the editor and for
/// generating the v-panel override). Authoritative validation is done by `docker compose config` at deploy.
/// </summary>
public static partial class ComposeAnalyzer
{
    [GeneratedRegex(@"\$\{([A-Za-z_][A-Za-z0-9_]*)(?:(:?[-?])([^}]*))?\}|\$([A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex VarRegex();

    public static string Interpolate(string value, IReadOnlyDictionary<string, string> env) =>
        VarRegex().Replace(value.Replace("$$", "\u0000"), m =>
        {
            var name = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[4].Value;
            var has = env.TryGetValue(name, out var v);
            if (m.Groups[2].Success && m.Groups[2].Value.Contains('-'))
            {
                var colon = m.Groups[2].Value.StartsWith(':');
                if (!has || (colon && string.IsNullOrEmpty(v))) return m.Groups[3].Value;
            }
            return v ?? "";
        }).Replace("\u0000", "$");

    public static ComposeAnalysis Analyze(string? compose, IReadOnlyDictionary<string, string>? env = null)
    {
        env ??= new Dictionary<string, string>();
        var issues = new List<ValidationIssue>();
        var services = new List<ComposeServiceInfo>();
        var volumes = new List<ComposeVolumeInfo>();
        var externalNetworks = new List<string>();

        if (string.IsNullOrWhiteSpace(compose))
            return new([], [], [], [new ValidationIssue("Compose file is empty.")]);

        var yaml = new YamlStream();
        try
        {
            yaml.Load(new StringReader(compose));
        }
        catch (YamlException ex)
        {
            return new([], [], [], [new ValidationIssue(ex.InnerException?.Message ?? ex.Message, (int)ex.Start.Line, (int)ex.Start.Column)]);
        }

        if (yaml.Documents.Count == 0 || yaml.Documents[0].RootNode is not YamlMappingNode root)
            return new([], [], [], [new ValidationIssue("Compose file must be a YAML mapping.")]);

        var topVolumes = Map(root, "volumes");
        var declaredVolumes = new Dictionary<string, (bool External, string? Name)>();
        if (topVolumes is not null)
        {
            foreach (var (k, v) in topVolumes.Children)
            {
                var name = ((YamlScalarNode)k).Value!;
                var vm = v as YamlMappingNode;
                var external = vm is not null && IsTrue(vm, "external");
                var realName = vm is not null && Scalar(vm, "name") is { } n ? Interpolate(n, env) : null;
                declaredVolumes[name] = (external, realName);
            }
        }

        var svcNode = Map(root, "services");
        if (svcNode is null || svcNode.Children.Count == 0)
        {
            issues.Add(new ValidationIssue("No services defined (missing 'services:' section)."));
        }
        else
        {
            foreach (var (k, v) in svcNode.Children)
            {
                var name = ((YamlScalarNode)k).Value!;
                if (v is not YamlMappingNode svc)
                {
                    issues.Add(new ValidationIssue($"Service '{name}' must be a mapping.", (int)k.Start.Line));
                    continue;
                }
                var image = Scalar(svc, "image");
                var hasBuild = svc.Children.ContainsKey(new YamlScalarNode("build"));
                if (image is null && !hasBuild)
                    issues.Add(new ValidationIssue($"Service '{name}' needs 'image' or 'build'.", (int)k.Start.Line));
                var networkMode = Scalar(svc, "network_mode");
                var hasNetworks = svc.Children.ContainsKey(new YamlScalarNode("networks"));
                var ports = Seq(svc, "ports").Select(p => p is YamlScalarNode s ? s.Value ?? "" : DescribePort(p)).ToList();
                var expose = Seq(svc, "expose").OfType<YamlScalarNode>().Select(s => s.Value ?? "").ToList();
                services.Add(new ComposeServiceInfo(name, image, hasBuild, hasNetworks, networkMode, ports, expose, ServiceNetworks(svc, env)));

                foreach (var vol in Seq(svc, "volumes"))
                {
                    string? source = null, target = null, type = null;
                    if (vol is YamlScalarNode s && s.Value is { } spec)
                    {
                        var parts = Interpolate(spec, env).Split(':');
                        if (parts.Length >= 2) { source = parts[0]; target = parts[1]; }
                        else target = parts[0];
                    }
                    else if (vol is YamlMappingNode m)
                    {
                        type = Scalar(m, "type");
                        source = Scalar(m, "source") is { } src ? Interpolate(src, env) : null;
                        target = Scalar(m, "target");
                    }
                    if (string.IsNullOrEmpty(source)) continue; // anonymous volume
                    var isBind = type == "bind" || (type is null && (source.StartsWith('.') || source.StartsWith('/') || source.StartsWith('~')));
                    if (type is "tmpfs" or "npipe") continue;
                    if (isBind)
                    {
                        volumes.Add(new ComposeVolumeInfo(source, VolumeKind.Bind, source, name, target));
                    }
                    else if (declaredVolumes.TryGetValue(source, out var decl))
                    {
                        volumes.Add(new ComposeVolumeInfo(decl.Name ?? source, decl.External ? VolumeKind.External : VolumeKind.Named, source, name, target));
                    }
                    else
                    {
                        issues.Add(new ValidationIssue($"Service '{name}' uses volume '{source}' which is not declared under top-level 'volumes:'.", (int)vol.Start.Line));
                    }
                }
            }
        }

        // Declared but unused volumes are still listed
        foreach (var (name, decl) in declaredVolumes)
            if (volumes.All(v => v.Source != name))
                volumes.Add(new ComposeVolumeInfo(decl.Name ?? name, decl.External ? VolumeKind.External : VolumeKind.Named, name, null, null));

        var topNetworks = Map(root, "networks");
        var networkNames = new Dictionary<string, string>(StringComparer.Ordinal);
        if (topNetworks is not null)
            foreach (var (k, v) in topNetworks.Children)
            {
                var key = ((YamlScalarNode)k).Value!;
                var nm = v as YamlMappingNode;
                var external = nm is not null && IsTrue(nm, "external");
                var explicitName = nm is not null && Scalar(nm, "name") is { } n ? Interpolate(n, env) : null;
                if (explicitName is not null || external) networkNames[key] = explicitName ?? key;
                if (external) externalNetworks.Add(explicitName ?? key);
            }

        if (root.Children.ContainsKey(new YamlScalarNode("version")))
            issues.Add(new ValidationIssue("Top-level 'version' is obsolete and ignored by docker compose.", IsWarning: true));

        return new(services, volumes, externalNetworks, issues, networkNames);
    }

    /// <summary>Service <c>networks:</c> in list form (<c>[a, b]</c>) or map form (<c>a: { aliases: [...] }</c>).</summary>
    private static List<ComposeServiceNetwork> ServiceNetworks(YamlMappingNode svc, IReadOnlyDictionary<string, string> env)
    {
        var list = new List<ComposeServiceNetwork>();
        if (!svc.Children.TryGetValue(new YamlScalarNode("networks"), out var node)) return list;
        if (node is YamlSequenceNode seq)
        {
            foreach (var item in seq.Children.OfType<YamlScalarNode>())
                if (item.Value is { Length: > 0 } key) list.Add(new ComposeServiceNetwork(key, []));
        }
        else if (node is YamlMappingNode map)
        {
            foreach (var (k, v) in map.Children)
            {
                if (((YamlScalarNode)k).Value is not { Length: > 0 } key) continue;
                var aliases = v is YamlMappingNode m
                    ? Seq(m, "aliases").OfType<YamlScalarNode>().Select(a => Interpolate(a.Value ?? "", env)).Where(a => a.Length > 0).ToList()
                    : [];
                list.Add(new ComposeServiceNetwork(key, aliases));
            }
        }
        return list;
    }

    private static string DescribePort(YamlNode node) =>
        node is YamlMappingNode m ? $"{Scalar(m, "published")}:{Scalar(m, "target")}" : node.ToString();

    private static YamlMappingNode? Map(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out var v) ? v as YamlMappingNode : null;

    private static IEnumerable<YamlNode> Seq(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out var v) && v is YamlSequenceNode s ? s.Children : [];

    private static string? Scalar(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out var v) && v is YamlScalarNode s ? s.Value : null;

    private static bool IsTrue(YamlMappingNode node, string key)
    {
        if (!node.Children.TryGetValue(new YamlScalarNode(key), out var v)) return false;
        if (v is YamlScalarNode s) return string.Equals(s.Value, "true", StringComparison.OrdinalIgnoreCase);
        return v is YamlMappingNode; // legacy `external: { name: x }`
    }
}
