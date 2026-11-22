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
    IServiceScopeFactory scopes, IOptions<WorkerOptions> options, ILogger<SchedulerService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var poll = TimeSpan.FromSeconds(options.Value.PollIntervalSeconds);
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await DispatchDueAsync(stop);
                await RecoverExpiredAsync(stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                log.LogError(ex, "Scheduler iteration failed; will retry");
            }

            try { await Task.Delay(poll, stop); } catch (OperationCanceledException) { break; }
        }
    }

    internal async Task DispatchDueAsync(CancellationToken ct)
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

        foreach (var s in due)
        {
            defs.TryGetValue(s.JobKey, out var def);
            var runnable = def is { Enabled: true, IsAvailable: true };
            var decision = ScheduleCalculator.Evaluate(
                s.NextDueUtc, TimeSpan.FromSeconds(s.IntervalSeconds), now,
                runnable ? def!.MissedRunPolicy : MissedRunPolicy.Skip);

            s.NextDueUtc = decision.NextDueUtc;
            if (!runnable)
            {
                log.LogDebug("Schedule for {Key} advanced without enqueue (disabled/unavailable)", s.JobKey);
                continue;
            }

            foreach (var scheduled in decision.RunTimes)
            {
                db.Runs.Add(new JobRun
                {
                    Id = Guid.NewGuid(), JobKey = s.JobKey, Trigger = TriggerSource.Schedule,
                    Status = RunStatus.Queued, ScheduledUtc = scheduled, QueuedUtc = now, NotBeforeUtc = now,
                    MaxAttempts = def!.MaxAttempts, CorrelationId = Guid.NewGuid().ToString("N"),
                });
            }
            if (decision.RunTimes.Count > 0) s.LastDispatchedUtc = now;
            if (decision.Missed > 0)
                log.LogWarning("Schedule {Key}: {Missed} missed occurrence(s); policy {Policy} enqueued {Count}",
                    s.JobKey, decision.Missed, def!.MissedRunPolicy, decision.RunTimes.Count);
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Running runs with an expired lease belong to a dead/stalled worker: retry or mark Abandoned.</summary>
    internal async Task RecoverExpiredAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MonitoringDbContext>();
        var now = DateTime.UtcNow;

        var expired = await db.Runs.AsNoTracking()
            .Where(r => r.Status == RunStatus.Running && r.LeaseExpiresUtc < now)
            .OrderBy(r => r.LeaseExpiresUtc)
            .Select(r => new { r.Id, r.Attempt, r.MaxAttempts, r.JobKey })
            .Take(100).ToListAsync(ct);

        foreach (var r in expired)
        {
            var retry = RetryPolicy.CanRetry(r.Attempt, r.MaxAttempts);
            var status = retry ? RunStatus.Queued : RunStatus.Abandoned;
            const string error = "Worker lease expired; run recovered";

            // Compare-and-set: only one recoverer wins, and only if the lease is still expired.
            var rows = await db.Runs
                .Where(x => x.Id == r.Id && x.Status == RunStatus.Running && x.LeaseExpiresUtc < now)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, status)
                    .SetProperty(x => x.WorkerId, (string?)null)
                    .SetProperty(x => x.LeaseExpiresUtc, (DateTime?)null)
                    .SetProperty(x => x.NotBeforeUtc, now)
                    .SetProperty(x => x.EndedUtc, retry ? (DateTime?)null : now)
                    .SetProperty(x => x.Error, error), ct);
            if (rows == 0) continue;

            await db.Attempts.Where(a => a.RunId == r.Id && a.Attempt == r.Attempt && a.Status == RunStatus.Running)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.Status, RunStatus.Abandoned)
                    .SetProperty(a => a.EndedUtc, now)
                    .SetProperty(a => a.Error, error), ct);

            log.LogWarning("Recovered run {RunId} of {Key}: {Outcome}", r.Id, r.JobKey, status);
        }
    }
}
