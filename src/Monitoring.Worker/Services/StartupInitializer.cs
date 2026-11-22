using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Monitoring.Application.Jobs;
using Monitoring.Infrastructure.Persistence;

namespace Monitoring.Worker.Services;

/// <summary>
/// Runs once before the loops: optional migrations (dev), upserts the definitions of the jobs THIS worker implements,
/// registers the worker and its capabilities, and optionally seeds sample schedules.
/// It never marks other jobs unavailable: another worker may implement them. Availability is derived from live workers.
/// </summary>
public sealed class StartupInitializer(
    IServiceScopeFactory scopes, JobRegistry registry, WorkerRegistrar registrar, IOptions<WorkerOptions> options,
    ILogger<StartupInitializer> log) : IHostedService
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
        await UpsertDefinitionsAsync(db, now, ct);

        // Housekeeping: instances that have been gone for a week are removed (capabilities cascade).
        await db.Workers.Where(w => w.LastSeenUtc < now.AddDays(-7)).ExecuteDeleteAsync(ct);

        await registrar.RegisterAsync(db, 0, ct);
        log.LogInformation("Worker registered; implements {Count} job(s): {Keys}",
            registry.All.Count, string.Join(", ", registry.All.Select(r => r.Key)));

        if (options.Value.SeedSampleData) await SeedAsync(db, now, ct);
    }

    private async Task UpsertDefinitionsAsync(MonitoringDbContext db, DateTime now, CancellationToken ct)
    {
        // Two workers may start together and both try to insert the same new definition; the loser retries as an update.
        for (var attempt = 1; ; attempt++)
        {
            db.ChangeTracker.Clear();
            var keys = registry.All.Select(r => r.Key).ToList();
            var existing = await db.Definitions.Where(d => keys.Contains(d.Key)).ToDictionaryAsync(d => d.Key, ct);

            foreach (var reg in registry.All)
            {
                if (existing.TryGetValue(reg.Key, out var def))
                {
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

            try { await db.SaveChangesAsync(ct); return; }
            catch (DbUpdateException) when (attempt < 3) { /* concurrent insert by another worker: re-read and update */ }
        }
    }

    private static async Task SeedAsync(MonitoringDbContext db, DateTime now, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
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
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { /* another worker seeded concurrently */ }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
