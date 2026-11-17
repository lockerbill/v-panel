using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using VPanel.Core.Abstractions;

namespace VPanel.Infrastructure.Data;

/// <summary>Used by `dotnet ef` tooling only.</summary>
internal sealed class DesignTimeFactory : IDesignTimeDbContextFactory<VPanelDbContext>
{
    public VPanelDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<VPanelDbContext>().UseSqlite("Data Source=design.db").Options;
        return new VPanelDbContext(options, PassthroughSecretProtector.Instance);
    }
}
