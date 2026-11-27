using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using VPanel.Core.Abstractions;
using VPanel.Core.Configuration;
using VPanel.Infrastructure.Data;
using VPanel.Infrastructure.Deployments;
using VPanel.Infrastructure.Docker;
using VPanel.Infrastructure.Proxy;
using VPanel.Infrastructure.Security;
using VPanel.Infrastructure.Services;

namespace VPanel.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddVPanelInfrastructure(this IServiceCollection services, IConfiguration configuration, string contentRoot)
    {
        var options = new VPanelOptions();
        configuration.GetSection(VPanelOptions.Section).Bind(options);
        options.DataDir = Absolute(options.DataDir, contentRoot);
        options.Defaults = options.Defaults with
        {
            AppsRoot = Absolute(options.Defaults.AppsRoot, contentRoot),
            CaddyDir = Absolute(options.Defaults.CaddyDir, contentRoot),
        };
        options.AdoptedRoots = NormalizeAdoptedRoots(options);
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(options));

        services.AddDataProtection()
            .SetApplicationName("vpanel")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(options.DataDir, "keys")));

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
        services.AddDbContextFactory<VPanelDbContext>(o =>
            o.UseSqlite($"Data Source={Path.Combine(options.DataDir, "vpanel.db")}"));

        // Engines (extension points)
        services.AddSingleton<IProcessRunner, LocalProcessRunner>();
        services.AddSingleton<DockerCli>();
        services.AddSingleton<IAppTypeHandler, DockerComposeHandler>();
        services.AddSingleton<AppTypeRegistry>();
        services.AddSingleton<IReverseProxyProvider, CaddyFileProvider>();

        // Application services (stateless, use DbContext factory)
        services.AddSingleton<SettingsService>();
        services.AddSingleton<AuditService>();
        services.AddSingleton<UserService>();
        services.AddSingleton<ApiTokenService>();
        services.AddSingleton<AppService>();
        services.AddSingleton<InstanceService>();
        services.AddSingleton<DeploymentService>();
        services.AddSingleton<ProxyRouteService>();
        services.AddSingleton<Import.StackScanner>();
        services.AddSingleton<Import.StackImportService>();

        // Background processing
        services.AddSingleton<DeploymentQueue>();
        services.AddSingleton<DeploymentLogBus>();
        services.AddSingleton<DockerHealth>();
        services.AddSingleton<DeploymentWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<DeploymentWorker>());
        services.AddSingleton<StatusSyncWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<StatusSyncWorker>());
        return services;
    }

    private static string[] NormalizeAdoptedRoots(VPanelOptions options)
    {
        var accepted = new List<string>();
        foreach (var raw in options.AdoptedRoots)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (!Core.Utilities.PathGuard.IsAcceptableAdoptedRoot(raw.Trim(), out var root, out var reason))
                options.RejectedAdoptedRoots.Add($"{raw}: {reason}");
            else if (root == options.DataDir || Core.Utilities.PathGuard.IsStrictlyInside(options.DataDir, root))
                options.RejectedAdoptedRoots.Add($"{raw}: inside the v-panel data folder");
            else if (!accepted.Contains(root))
                accepted.Add(root);
        }
        return [.. accepted];
    }

    private static string Absolute(string path, string root) =>
        Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
}
