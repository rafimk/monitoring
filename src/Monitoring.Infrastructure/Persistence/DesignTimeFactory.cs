using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Monitoring.Infrastructure.Persistence;

/// <summary>Used by `dotnet ef` only. Override with the ConnectionStrings__Monitoring env var.</summary>
public class DesignTimeFactory : IDesignTimeDbContextFactory<MonitoringDbContext>
{
    public MonitoringDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("ConnectionStrings__Monitoring")
                 ?? "Server=localhost,14333;Database=Monitoring;User Id=sa;Password=Dev_Passw0rd!;TrustServerCertificate=True";
        var options = new DbContextOptionsBuilder<MonitoringDbContext>().UseSqlServer(cs).Options;
        return new MonitoringDbContext(options);
    }
}
