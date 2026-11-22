using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Monitoring.Application.Jobs;
using Monitoring.Contracts;
using Monitoring.Infrastructure.Persistence;

namespace Monitoring.Worker.Services;

/// <summary>
/// Runs once before the loops: optional migrations (dev), syncs registered implementations into
/// JobDefinitions (flagging missing ones as unavailable), and optionally seeds sample schedules.
/// </summary>
public sealed class StartupInitializer(
    IServiceScopeFactory scopes, JobRegistry registry, IOptions<WorkerOptions> options, ILogger<StartupInitializer> log)
    : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MonitoringDbContext>();

        if (options.Value.ApplyMigrationsOnStartup)
        {
            log.LogInformation("Applying migrations (Worker:ApplyMigrationsOnStartup=true)");
            await db.Database.MigrateAsync(ct);
        }

        var now = DateTime.UtcNow;
        var existing = await db.Definitions.ToDictionaryAsync(d => d.Key, ct);

        foreach (var reg in registry.All)
        {
            if (existing.TryGetValue(reg.Key, out var def))
            {
                def.IsAvailable = true;
                def.DisplayName = reg.DisplayName;
                if (!string.IsNullOrEmpty(reg.Description)) def.Description = reg.Description;
                def.UpdatedUtc = now;
            }
            else
            {
                db.Definitions.Add(new JobDefinition
                {
                    Key = reg.Key, DisplayName = reg.DisplayName, Description = reg.Description,
                    CreatedUtc = now, UpdatedUtc = now,
                    TimeoutSeconds = reg.Key == "sample.slow" ? 90 : 60,
                });
            }
        }

        foreach (var def in existing.Values.Where(d => !registry.TryGet(d.Key, out _) && d.IsAvailable))
        {
            def.IsAvailable = false; // implementation removed: show as unavailable, never crash
            def.UpdatedUtc = now;
            log.LogWarning("Job {Key} has no registered implementation; marked unavailable", def.Key);
        }

        await db.SaveChangesAsync(ct);

        if (options.Value.SeedSampleData) await SeedAsync(db, now, ct);
    }

    private static async Task SeedAsync(MonitoringDbContext db, DateTime now, CancellationToken ct)
    {
        var seeds = new (string Key, int Interval)[] { ("sample.job", 60), ("sample.slow", 120), ("sample.flaky", 300) };
        foreach (var (key, interval) in seeds)
        {
            if (!await db.Definitions.AnyAsync(d => d.Key == key, ct)) continue;
            if (await db.Schedules.AnyAsync(s => s.JobKey == key, ct)) continue;
            db.Schedules.Add(new JobSchedule
            {
                Id = Guid.NewGuid(), JobKey = key, IntervalSeconds = interval,
                NextDueUtc = now.AddSeconds(5), Enabled = true,
            });
        }
        await db.SaveChangesAsync(ct);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
