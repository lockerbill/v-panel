using VPanel.Core.Configuration;

namespace VPanel.Infrastructure.Import;

/// <summary>Derives v-panel settings (Caddy container and folder, shared network, ingress) from an existing infra stack.</summary>
public static class InfraDetector
{
    public static InfraDetection Detect(ScannedStack infra, IReadOnlyList<ScannedStack> allStacks)
    {
        var warnings = new List<string>();
        var caddy = infra.Services.First(StackScanner.IsCaddy);
        var caddyContainer = infra.Containers.FirstOrDefault(c => c.Service == caddy.Name);

        // Container name: the running one, else the compose default (<project>-<service>-1) or container_name.
        var containerName = caddyContainer?.Name ?? caddy.ContainerName ?? $"{infra.ProjectName}-{caddy.Name}-1";
        if (caddyContainer is null) warnings.Add($"Caddy is not running; its container is assumed to be named '{containerName}'.");

        // Configuration folder: the bind mount on /etc/caddy (folder), or on the Caddyfile itself.
        var mounts = caddy.Mounts.Count > 0 ? caddy.Mounts : caddyContainer?.Mounts ?? [];
        string? caddyDir = null, dirInContainer = null;
        var singleFile = false;
        var dirMount = mounts.FirstOrDefault(m => m.Type == "bind" && m.Target.TrimEnd('/') == "/etc/caddy");
        if (dirMount is not null)
        {
            caddyDir = dirMount.Source.TrimEnd('/');
            dirInContainer = "/etc/caddy";
        }
        else if (mounts.FirstOrDefault(m => m.Type == "bind" && m.Target.EndsWith("/Caddyfile", StringComparison.Ordinal)) is { } fileMount)
        {
            singleFile = true;
            caddyDir = Path.GetDirectoryName(fileMount.Source);
            dirInContainer = Path.GetDirectoryName(fileMount.Target);
            warnings.Add($"Caddy mounts only {fileMount.Source}; v-panel's site files would be invisible to Caddy. " +
                         $"Mount the folder instead (e.g. '{caddyDir}:{dirInContainer}:ro') and redeploy the infra stack.");
            if (Path.GetFileName(fileMount.Source) != "Caddyfile")
                warnings.Add($"The mounted Caddyfile is named {Path.GetFileName(fileMount.Source)}; v-panel expects 'Caddyfile' in the Caddy folder.");
        }
        else
        {
            warnings.Add("No bind mount of /etc/caddy found on the Caddy service: set the Caddy folder in Settings by hand.");
        }

        // Shared network: the one Caddy is on; with several, the one most other stacks join.
        var caddyNetworks = caddy.Networks.Select(n => n.Name).Distinct().ToList();
        if (caddyNetworks.Count == 0 && caddyContainer is not null) caddyNetworks = caddyContainer.Networks.Keys.ToList();
        string? shared = caddyNetworks.Count switch
        {
            0 => null,
            1 => caddyNetworks[0],
            _ => caddyNetworks.OrderByDescending(n => allStacks.Count(s => s != infra && s.Services.Any(x => x.On(n) is not null)))
                .ThenBy(n => n, StringComparer.Ordinal).First(),
        };
        if (shared is null) warnings.Add("Caddy joins no named network; apps cannot be reached through it.");
        else if (caddyNetworks.Count > 1) warnings.Add($"Caddy is on several networks ({string.Join(", ", caddyNetworks)}); '{shared}' is used as shared network.");

        // Ingress: cloudflared anywhere and Caddy without public ports = tunnel.
        var published = caddy.PublishedPorts.Concat(caddyContainer?.PublishedContainerPorts ?? []).ToList();
        var hasPublicPorts = published.Contains(80) || published.Contains(443)
                             || (caddyContainer?.PublishedContainerPorts.Any(p => p is 80 or 443) ?? false);
        var hasTunnel = allStacks.SelectMany(s => s.Services).Any(s => s.ImageRepository is "cloudflare/cloudflared" || s.ImageBase == "cloudflared");
        var ingress = hasTunnel && !hasPublicPorts ? IngressMode.CloudflareTunnel : IngressMode.Direct;

        var pg = infra.Services.FirstOrDefault(s => s.ImageBase is "postgres" or "postgis");
        var pgNet = pg?.Networks.FirstOrDefault();

        return new InfraDetection
        {
            ProjectName = infra.ProjectName,
            CaddyService = caddy.Name,
            CaddyContainer = containerName,
            CaddyRunning = caddyContainer?.State == "running",
            CaddyDir = caddyDir,
            CaddyConfigDirInContainer = dirInContainer,
            CaddySingleFileMount = singleFile,
            SharedNetwork = shared,
            Ingress = ingress,
            PostgresService = pg?.Name,
            PostgresNetwork = pgNet?.Name,
            PostgresAliases = pgNet?.Aliases ?? [],
            Warnings = warnings,
        };
    }
}
