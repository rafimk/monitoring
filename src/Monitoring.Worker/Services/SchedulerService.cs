using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Monitoring.Application.Scheduling;
using Monitoring.Contracts;
using Monitoring.Infrastructure.Persistence;

namespace Monitoring.Worker.Services;

/// <summary>
/// Polls persisted schedules, atomically claims due ones and advances NextDueUtc in the same
/// transaction (UPDLOCK+READPAST so concurrent workers skip rows another worker holds), and
/// recovers runs whose lease expired.
/// </summary>
public sealed class SchedulerService(
    IServiceScopeFactory scopes, RunStore store, IOptions<WorkerOptions> options, ILogger<SchedulerService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var poll = TimeSpan.FromSeconds(options.Value.PollIntervalSeconds);
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await DispatchDueAsync(stop);
                var recovered = await store.RecoverExpiredAsync(DateTime.UtcNow, stop);
                if (recovered > 0) log.LogWarning("Recovered {Count} run(s) with expired leases", recovered);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                log.LogError(ex, "Scheduler iteration failed; will retry");
            }

            try { await Task.Delay(poll, stop); } catch (OperationCanceledException) { break; }
        }
    }

    public async Task DispatchDueAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MonitoringDbContext>();
        var now = DateTime.UtcNow;
        var batch = options.Value.SchedulerBatchSize;

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var due = await db.Schedules
            .FromSqlInterpolated($@"SELECT TOP ({batch}) * FROM JobSchedules WITH (UPDLOCK, READPAST, ROWLOCK)
                                    WHERE Enabled = 1 AND NextDueUtc <= {now} ORDER BY NextDueUtc")
            .ToListAsync(ct);
        if (due.Count == 0) { await tx.CommitAsync(ct); return; }

        var keys = due.Select(d => d.JobKey).Distinct().ToList();
        var defs = await db.Definitions.Where(d => keys.Contains(d.Key)).ToDictionaryAsync(d => d.Key, ct);
        // A job is only dispatched while at least one live worker can execute it (otherwise occurrences would pile up
        // as queued runs that nobody can claim). Any worker, whatever it implements, makes this decision.
        var live = (await WorkerCapacity.LiveJobKeys(db, now.AddSeconds(-options.Value.LeaseSeconds)).ToListAsync(ct)).ToHashSet();

        foreach (var s in due)
        {
            defs.TryGetValue(s.JobKey, out var def);
            var runnable = def is { Enabled: true } && live.Contains(s.JobKey);
            var decision = ScheduleCalculator.Evaluate(
                s.NextDueUtc, TimeSpan.FromSeconds(s.IntervalSeconds), now,
                runnable ? def!.MissedRunPolicy : MissedRunPolicy.Skip);

            s.NextDueUtc = decision.NextDueUtc;
            if (!runnable)
            {
                log.LogDebug("Schedule for {Key} advanced without enqueue (disabled or no live worker implements it)", s.JobKey);
                continue;
            }

            foreach (var scheduled in decision.RunTimes)
            {
                var run = new JobRun
                {
                    Id = Guid.NewGuid(), JobKey = s.JobKey, Trigger = TriggerSource.Schedule,
                    Status = RunStatus.Queued, ScheduledUtc = scheduled, QueuedUtc = now, NotBeforeUtc = now,
                    MaxAttempts = def!.MaxAttempts, CorrelationId = Guid.NewGuid().ToString("N"),
                };
                db.Runs.Add(run);
                ChangeFeed.Run(db, run.Id, run.JobKey, "queued"); // same transaction as the occurrence itself
            }
            if (decision.RunTimes.Count > 0) s.LastDispatchedUtc = now;
            if (decision.RunTimes.Count > 0 || decision.Missed > 0) ChangeFeed.Job(db, s.JobKey, "scheduled"); // next-due moved
            if (decision.Missed > 0)
                log.LogWarning("Schedule {Key}: {Missed} missed occurrence(s); policy {Policy} enqueued {Count}",
                    s.JobKey, decision.Missed, def!.MissedRunPolicy, decision.RunTimes.Count);
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}
