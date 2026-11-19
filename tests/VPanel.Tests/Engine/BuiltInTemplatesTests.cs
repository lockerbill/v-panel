using VPanel.Infrastructure.Seed;

namespace VPanel.Tests.Engine;

public class BuiltInTemplatesTests
{
    [Fact]
    public void Shared_postgres_is_version_18_with_its_data_layout()
    {
        Assert.Contains("image: postgres:18", BuiltInTemplates.InfraCompose);
        // PG18 images keep data in /var/lib/postgresql/18/docker and refuse to start with a volume on .../data.
        Assert.Contains("- postgres_data:/var/lib/postgresql\n", BuiltInTemplates.InfraCompose.ReplaceLineEndings("\n"));
        Assert.DoesNotContain("/var/lib/postgresql/data", BuiltInTemplates.InfraCompose);
    }

    [Fact]
    public void Shared_infra_runs_cloudflared_and_publishes_no_web_ports()
    {
        var compose = BuiltInTemplates.InfraCompose.ReplaceLineEndings("\n");
        Assert.Contains("image: cloudflare/cloudflared", compose);
        Assert.Contains("TUNNEL_TOKEN: ${CLOUDFLARE_TUNNEL_TOKEN}", compose);
        var active = compose.Split('\n').Where(l => !l.TrimStart().StartsWith('#')).ToList();
        Assert.DoesNotContain(active, l => l.Contains("ports:") || l.Contains("\"80:80\"") || l.Contains("\"443:443"));
        Assert.Contains("CLOUDFLARE_TUNNEL_TOKEN=", BuiltInTemplates.InfraEnv("pw"));
    }

    [Fact]
    public void Shared_infra_template_is_valid_yaml_and_matches_sample()
    {
        var model = new YamlDotNet.Serialization.DeserializerBuilder().Build().Deserialize<Dictionary<string, object>>(BuiltInTemplates.InfraCompose);
        var services = Assert.IsAssignableFrom<IDictionary<object, object>>(model["services"]);
        Assert.Equal(["cloudflared", "caddy", "postgres"], services.Keys.Cast<string>());

        var sample = Path.Combine(RepoRoot(), "samples", "infra", "docker-compose.yml");
        Assert.Equal(File.ReadAllText(sample).ReplaceLineEndings("\n"), BuiltInTemplates.InfraCompose.ReplaceLineEndings("\n"));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "VPanel.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
