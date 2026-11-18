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
}
