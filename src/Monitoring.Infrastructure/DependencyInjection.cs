using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Monitoring.Infrastructure.Persistence;

namespace Monitoring.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringName = "Monitoring";

    public static IServiceCollection AddMonitoringPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        var cs = configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException(
                $"Connection string 'ConnectionStrings:{ConnectionStringName}' is not configured. " +
                "Set it via appsettings, user-secrets or the ConnectionStrings__Monitoring environment variable.");

        // No EnableRetryOnFailure: the scheduler/runner use explicit transactions, which the retrying
        // execution strategy rejects. Their loops retry whole iterations instead.
        services.AddDbContext<MonitoringDbContext>(o => o.UseSqlServer(cs, sql =>
            sql.MigrationsAssembly(typeof(MonitoringDbContext).Assembly.FullName)));
        return services;
    }
}
