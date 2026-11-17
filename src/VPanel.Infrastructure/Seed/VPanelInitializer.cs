using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VPanel.Core.Abstractions;
using VPanel.Core.Configuration;
using VPanel.Core.Domain;
using VPanel.Infrastructure.Data;
using VPanel.Infrastructure.Services;

namespace VPanel.Infrastructure.Seed;

public static class VPanelInitializer
{
    /// <summary>Creates folders, migrates the database and seeds templates. Called once at startup.</summary>
    public static async Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var options = services.GetRequiredService<IOptions<VPanelOptions>>().Value;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("VPanel.Init");
        Directory.CreateDirectory(options.DataDir);

        var factory = services.GetRequiredService<IDbContextFactory<VPanelDbContext>>();
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            await db.Database.MigrateAsync(ct);
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);

            if (!await db.Apps.AnyAsync(ct))
            {
                if (options.SeedInfrastructure) await SeedInfraAsync(db, options.Defaults, logger, ct);
                if (options.SeedExamples) SeedExamples(db, logger);
                await db.SaveChangesAsync(ct);
            }
        }

        var settings = services.GetRequiredService<SettingsService>().Get();
        TryCreate(settings.AppsRoot, logger);
        if (settings.CaddyEnabled)
        {
            try { await services.GetRequiredService<IReverseProxyProvider>().EnsureBootstrapAsync(settings, ct); }
            catch (Exception ex) { logger.LogWarning("Could not prepare Caddy folder {Dir}: {Message}", settings.CaddyDir, ex.Message); }
        }
    }

    private static Task SeedInfraAsync(VPanelDbContext db, PanelSettings settings, ILogger logger, CancellationToken ct)
    {
        logger.LogInformation("Seeding shared infrastructure template");
        var password = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var app = new AppDefinition
        {
            Name = "Shared infrastructure",
            Slug = BuiltInTemplates.InfraSlug,
            Description = "Caddy reverse proxy and PostgreSQL shared by all apps on this server.",
            Type = AppType.DockerCompose,
            ComposeTemplate = BuiltInTemplates.InfraCompose,
            EnvTemplate = BuiltInTemplates.InfraEnv(password),
            IsSystem = true,
        };
        app.Instances.Add(new AppInstance
        {
            Name = BuiltInTemplates.InfraSlug,
            Slug = BuiltInTemplates.InfraSlug,
            Description = "Deploy this first: it starts Caddy and PostgreSQL.",
            InstallPath = Path.Combine(settings.AppsRoot, BuiltInTemplates.InfraSlug),
            Status = InstanceStatus.Draft,
        });
        db.Apps.Add(app);
        return Task.CompletedTask;
    }

    private static void SeedExamples(VPanelDbContext db, ILogger logger)
    {
        logger.LogInformation("Seeding example app template");
        db.Apps.Add(new AppDefinition
        {
            Name = "Whoami (example)",
            Slug = "whoami",
            Description = "Example template. Create instances whoami-01, whoami-02... with different public URLs.",
            Type = AppType.DockerCompose,
            ComposeTemplate = BuiltInTemplates.WhoamiCompose,
            EnvTemplate = BuiltInTemplates.WhoamiEnv,
            DefaultProxyService = "web",
            DefaultProxyPort = 80,
        });
    }

    private static void TryCreate(string dir, ILogger logger)
    {
        try { Directory.CreateDirectory(dir); }
        catch (Exception ex) { logger.LogWarning("Could not create {Dir}: {Message}", dir, ex.Message); }
    }
}
