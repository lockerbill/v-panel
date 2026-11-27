using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using VPanel.Core.Abstractions;
using VPanel.Core.Domain;
using VPanel.Infrastructure;
using VPanel.Infrastructure.Seed;
using VPanel.Tests.Engine;

namespace VPanel.Tests.Services;

/// <summary>Real service graph (SQLite in a temp folder) with docker replaced by a fake process runner.</summary>
internal sealed class ServiceTestHost : IAsyncDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("vpanel-svc-").FullName;
    public FakeProcessRunner Runner { get; } = new();
    public ServiceProvider Services { get; }

    public string AppsRoot => Path.Combine(Root, "apps");

    private ServiceTestHost(Func<string, IDictionary<string, string?>>? extraConfig)
    {
        var values = new Dictionary<string, string?>
        {
            ["VPanel:DataDir"] = Path.Combine(Root, "data"),
            ["VPanel:Defaults:AppsRoot"] = AppsRoot,
            ["VPanel:Defaults:CaddyDir"] = Path.Combine(Root, "caddy"),
        };
        if (extraConfig is not null)
            foreach (var (k, v) in extraConfig(Root)) values[k] = v;
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddVPanelInfrastructure(config, Root);
        services.RemoveAll<IProcessRunner>();
        services.AddSingleton<IProcessRunner>(Runner);
        Services = services.BuildServiceProvider();

        Runner
            .On(a => FakeProcessRunner.IsCompose(a, "config"), new ProcessResult(0, "{\"services\":{}}", ""))
            .On(a => FakeProcessRunner.IsCompose(a, "ps"), new ProcessResult(0, "{\"Name\":\"c1\",\"State\":\"running\"}", ""));
    }

    /// <param name="extraConfig">Additional configuration values, given the host's temp root folder.</param>
    public static async Task<ServiceTestHost> CreateAsync(Func<string, IDictionary<string, string?>>? extraConfig = null)
    {
        var host = new ServiceTestHost(extraConfig);
        await VPanelInitializer.InitializeAsync(host.Services);
        return host;
    }

    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Root, true); } catch { /* best effort */ }
    }
}
