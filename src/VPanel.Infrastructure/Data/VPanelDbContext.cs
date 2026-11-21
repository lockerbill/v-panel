using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using VPanel.Core.Abstractions;
using VPanel.Core.Domain;

namespace VPanel.Infrastructure.Data;

public class VPanelDbContext(DbContextOptions<VPanelDbContext> options, ISecretProtector protector) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<AppDefinition> Apps => Set<AppDefinition>();
    public DbSet<AppVolume> AppVolumes => Set<AppVolume>();
    public DbSet<AppInstance> Instances => Set<AppInstance>();
    public DbSet<Deployment> Deployments => Set<Deployment>();
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        var secret = new ValueConverter<string, string>(v => protector.Protect(v), v => protector.Unprotect(v));
        var secretNullable = new ValueConverter<string?, string?>(
            v => v == null ? null : protector.Protect(v), v => v == null ? null : protector.Unprotect(v));
        // SQLite cannot order by DateTimeOffset; store as unix ms.
        var dto = new ValueConverter<DateTimeOffset, long>(v => v.ToUnixTimeMilliseconds(), v => DateTimeOffset.FromUnixTimeMilliseconds(v));
        var dtoNullable = new ValueConverter<DateTimeOffset?, long?>(
            v => v == null ? null : v.Value.ToUnixTimeMilliseconds(), v => v == null ? null : DateTimeOffset.FromUnixTimeMilliseconds(v.Value));

        b.Entity<User>(e =>
        {
            e.HasIndex(x => x.Username).IsUnique();
            e.Property(x => x.Username).HasMaxLength(64).UseCollation("NOCASE");
        });

        b.Entity<AppDefinition>(e =>
        {
            e.ToTable("Apps");
            e.HasIndex(x => x.Slug).IsUnique();
            e.Property(x => x.Name).HasMaxLength(128);
            e.Property(x => x.Slug).HasMaxLength(64);
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.EnvTemplate).HasConversion(secret);
            e.HasMany(x => x.Instances).WithOne(x => x.App).HasForeignKey(x => x.AppId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Volumes).WithOne(x => x.App).HasForeignKey(x => x.AppId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<AppVolume>(e =>
        {
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(x => new { x.AppId, x.Name }).IsUnique();
        });

        b.Entity<AppInstance>(e =>
        {
            e.ToTable("Instances");
            e.HasIndex(x => x.Slug).IsUnique();
            e.Property(x => x.Name).HasMaxLength(128);
            e.Property(x => x.Slug).HasMaxLength(64);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.EnvOverrides).HasConversion(secret);
            e.Property(x => x.ServerId).HasMaxLength(64);
            e.HasMany(x => x.Deployments).WithOne(x => x.Instance).HasForeignKey(x => x.InstanceId).OnDelete(DeleteBehavior.Cascade);
            e.Ignore(x => x.ProjectName);
            e.Ignore(x => x.EffectiveProxyService);
            e.Ignore(x => x.EffectiveProxyPort);
            e.Ignore(x => x.EffectiveCompose);
        });

        b.Entity<Deployment>(e =>
        {
            e.Property(x => x.Action).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.EnvSnapshot).HasConversion(secretNullable);
            e.Property(x => x.StartedAt).HasConversion(dtoNullable);
            e.Property(x => x.FinishedAt).HasConversion(dtoNullable);
            e.HasIndex(x => new { x.InstanceId, x.CreatedAt });
            e.Ignore(x => x.Duration);
        });

        b.Entity<ApiToken>(e =>
        {
            e.HasIndex(x => x.Name).IsUnique();
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.Property(x => x.Name).HasMaxLength(64).UseCollation("NOCASE");
            e.Property(x => x.TokenHash).HasMaxLength(64);
            e.Property(x => x.Prefix).HasMaxLength(16);
            e.Property(x => x.CreatedBy).HasMaxLength(128);
        });

        b.Entity<Setting>(e => e.HasKey(x => x.Key));

        b.Entity<AuditLog>(e =>
        {
            e.Property(x => x.At).HasConversion(dto);
            e.HasIndex(x => x.At);
        });

        // Apply unix-ms storage to all DateTimeOffset properties.
        foreach (var entity in b.Model.GetEntityTypes())
        foreach (var prop in entity.GetProperties())
        {
            if (prop.GetValueConverter() is not null) continue;
            if (prop.ClrType == typeof(DateTimeOffset)) prop.SetValueConverter(dto);
            else if (prop.ClrType == typeof(DateTimeOffset?)) prop.SetValueConverter(dtoNullable);
        }
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        Touch();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        Touch();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void Touch()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in ChangeTracker.Entries<Entity>())
        {
            if (entry.State == EntityState.Added) entry.Entity.CreatedAt = entry.Entity.UpdatedAt = now;
            else if (entry.State == EntityState.Modified) entry.Entity.UpdatedAt = now;
        }
    }
}
