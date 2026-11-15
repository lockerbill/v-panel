using VPanel.Core.Domain;
using VPanel.Infrastructure.Docker;

namespace VPanel.Tests.Engine;

public class ComposeAnalyzerTests
{
    private const string Compose = """
        services:
          web:
            image: nginx
            volumes:
              - data:/data
              - ./conf:/etc/conf:ro
              - type: bind
                source: ${LOGS_DIR:-./logs}
                target: /logs
              - shared:/shared
            networks: [front]
          worker:
            build: .
            network_mode: host
        volumes:
          data:
          shared:
            external: true
            name: ${SHARED_VOLUME}
        networks:
          front: {}
          pg:
            external: true
            name: vpanel-shared
        """;

    [Fact]
    public void Detects_services_volumes_and_external_resources()
    {
        var a = ComposeAnalyzer.Analyze(Compose, new Dictionary<string, string> { ["SHARED_VOLUME"] = "files" });
        Assert.True(a.IsValid, string.Join(";", a.Issues));
        Assert.Equal(["web", "worker"], a.Services.Select(s => s.Name));
        Assert.True(a.Services[0].HasExplicitNetworks);
        Assert.Equal("host", a.Services[1].NetworkMode);
        Assert.True(a.Services[1].HasBuild);

        Assert.Contains(a.Volumes, v => v is { Name: "data", Kind: VolumeKind.Named, Service: "web" });
        Assert.Contains(a.Volumes, v => v is { Name: "./conf", Kind: VolumeKind.Bind });
        Assert.Contains(a.Volumes, v => v is { Name: "./logs", Kind: VolumeKind.Bind, Target: "/logs" });
        Assert.Contains(a.Volumes, v => v is { Name: "files", Kind: VolumeKind.External });
        Assert.Equal(["vpanel-shared"], a.ExternalNetworks);
    }

    [Fact]
    public void Reports_yaml_errors_with_line()
    {
        var a = ComposeAnalyzer.Analyze("services:\n  web:\n    image: nginx\n   bad: [\n");
        Assert.False(a.IsValid);
        Assert.NotNull(a.Issues[0].Line);
    }

    [Fact]
    public void Reports_undeclared_volume_and_missing_image()
    {
        var a = ComposeAnalyzer.Analyze("services:\n  web:\n    volumes:\n      - nope:/x\n");
        Assert.Contains(a.Issues, i => i.Message.Contains("needs 'image' or 'build'"));
        Assert.Contains(a.Issues, i => i.Message.Contains("'nope'"));
    }

    [Fact]
    public void Version_key_is_only_a_warning()
    {
        var a = ComposeAnalyzer.Analyze("version: '3'\nservices:\n  web:\n    image: nginx\n");
        Assert.True(a.IsValid);
        Assert.Single(a.Issues, i => i.IsWarning);
    }

    [Theory]
    [InlineData("${A}", "1")]
    [InlineData("${MISSING:-def}", "def")]
    [InlineData("${EMPTY:-def}", "def")]
    [InlineData("${EMPTY-def}", "")]
    [InlineData("pre-$A-post", "pre-1-post")]
    [InlineData("$$A", "$A")]
    public void Interpolation(string input, string expected) =>
        Assert.Equal(expected, ComposeAnalyzer.Interpolate(input, new Dictionary<string, string> { ["A"] = "1", ["EMPTY"] = "" }));
}
