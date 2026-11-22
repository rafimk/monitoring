using System.Data;
using System.Data.Common;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Monitoring.Infrastructure.Persistence;

namespace Monitoring.Worker.Services;

/// <summary>Proves, right now, that this node is still the active worker (see <see cref="WorkerCoordinator"/>).</summary>
public delegate Task<bool> LeadershipCheck(CancellationToken ct);

public enum RetentionOutcome
{
    /// <summary>Cleanup finished and the success time was persisted.</summary>
    Completed,
    /// <summary>An error occurred (or no progress could be made); it will be retried with backoff.</summary>
    Failed,
    /// <summary>Another node holds the cleanup lock; nothing was done here.</summary>
    Skipped,
    /// <summary>Leadership could not be proven; the service stops (a standby never runs cleanup).</summary>
    LeadershipLost,
}

/// <summary>
/// Nightly data retention. Runs inside the active worker's tenure only (a standby does not even create this service),
/// calls the versioned stored procedure <c>dbo.usp_Monitoring_ApplyRetention_v1</c> one batch at a time, and re-proves
/// leadership immediately before every batch. Its own SQL application lock (<see cref="LockResource"/>) is a second
/// safeguard against two nodes ever cleaning at once. Failures are logged and retried with backoff; they never affect
/// job execution, because this service is isolated from the scheduler and runner loops.
///
/// "Done for the night" is persisted in <c>MaintenanceState</c>, so a restart near the scheduled time does not repeat the
/// work, and a worker that was down at the scheduled time performs exactly one catch-up run when it returns.
/// </summary>
public sealed class RetentionService(
    IServiceScopeFactory scopes, IOptions<WorkerOptions> options, Tenure tenure, LeadershipCheck leadership,
    TimeProvider clock, ILogger<RetentionService> log) : BackgroundService
{
    public const string LockResource = "monitoring-retention";
    public const string TaskName = "retention";
    public const string Procedure = "dbo.usp_Monitoring_ApplyRetention_v1";

    private static readonly TimeSpan MaxIdleWait = TimeSpan.FromMinutes(15); // re-evaluate regularly: clock changes, DST, config
    private int _failures;
    private DateTime? _announcedNext;

    public RetentionOutcome? LastOutcome { get; private set; }
    /// <summary>Counts deleted by the most recent completed run.</summary>
    public IReadOnlyDictionary<string, int> LastDeleted { get; private set; } = new Dictionary<string, int>();

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        await Task.Yield();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop, tenure.Lost);
        var ct = linked.Token;
        while (!ct.IsCancellationRequested)
        {
            TimeSpan? delay;
            try { delay = await TickAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                log.LogError(ex, "Retention scheduling failed; will retry (job execution is not affected)");
                delay = Backoff(++_failures);
            }
            if (delay is null) break; // leadership lost
            try { await Task.Delay(delay.Value, clock, ct); } catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>One scheduling decision: run now if a scheduled run is outstanding, otherwise say how long to wait. Null = stop.</summary>
    public async Task<TimeSpan?> TickAsync(CancellationToken ct)
    {
        var o = options.Value.Retention;
        if (!o.Enabled) return MaxIdleWait;

        var zone = TimeZoneInfo.FindSystemTimeZoneById(o.TimeZoneId);
        var at = o.RunAtTime;
        var now = clock.GetUtcNow().UtcDateTime;

        var state = await EnsureStateAsync(now, ct);
        var lastDue = RetentionSchedule.LatestAtOrBefore(now, at, zone);
        var baseline = state.LastSuccessUtc ?? state.InitializedUtc;

        if (baseline >= lastDue)
        {
            var next = RetentionSchedule.NextAfter(now, at, zone);
            if (_announcedNext != next)
            {
                _announcedNext = next;
                log.LogInformation("Next retention run: {Next:u} ({Local} {Zone})", next,
                    TimeZoneInfo.ConvertTimeFromUtc(next, zone).ToString("yyyy-MM-dd HH:mm"), zone.Id);
            }
            var wait = next - now;
            return wait > MaxIdleWait ? MaxIdleWait : wait < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : wait;
        }

        if (now - lastDue > TimeSpan.FromMinutes(5))
            log.LogWarning("Retention run scheduled for {Due:u} did not happen (worker down, not active, or failing); running it now", lastDue);

        switch (await RunOnceAsync(ct))
        {
            case RetentionOutcome.Completed: _failures = 0; return TimeSpan.FromSeconds(1);
            case RetentionOutcome.Skipped: return TimeSpan.FromMinutes(5);
            case RetentionOutcome.LeadershipLost: return null;
            default: return Backoff(++_failures);
        }
    }

    /// <summary>Runs the whole cleanup now (all batches). Public so it can be driven directly (tests, diagnostics).</summary>
    public async Task<RetentionOutcome> RunOnceAsync(CancellationToken ct)
    {
        var outcome = await RunCoreAsync(ct);
        LastOutcome = outcome;
        return outcome;
    }

    private async Task<RetentionOutcome> RunCoreAsync(CancellationToken stop)
    {
        var o = options.Value.Retention;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop, tenure.Lost);
        var ct = linked.Token;
        var totals = new Dictionary<string, int>();
        var watch = Stopwatch.StartNew();
        var batches = 0;
        var startedUtc = clock.GetUtcNow().UtcDateTime;

        try
        {
            if (!await IsLeaderAsync(ct)) return LostLeadership();

            string cs;
            using (var scope = scopes.CreateScope())
                cs = scope.ServiceProvider.GetRequiredService<MonitoringDbContext>().Database.GetConnectionString()
                     ?? throw new InvalidOperationException("Monitoring connection string is not configured");

            await using var gate = new LeaderLock(cs, LockResource, "Monitoring.Worker.Retention");
            if (!await gate.TryAcquireAsync(ct))
            {
                log.LogInformation("Retention skipped: another node is running it (application lock '{Lock}' is held)", LockResource);
                return RetentionOutcome.Skipped;
            }

            log.LogInformation(
                "Retention started: job history > {Jobs}d, audit > {Audit}d, dispatched change feed > {Feed}d, batch size {Batch}",
                o.JobHistoryRetentionDays, o.AuditRetentionDays, o.ChangeFeedRetentionDays, o.BatchSize);

            while (true)
            {
                // Immediately before every batch: still the active worker, still holding the cleanup lock.
                if (!await IsLeaderAsync(ct)) return LostLeadership();
                var (held, reason) = await gate.VerifyAsync(TimeSpan.FromSeconds(5), ct);
                if (!held) { log.LogError("Retention stopped: cleanup lock no longer held ({Reason})", reason); return RetentionOutcome.Failed; }

                var batch = await ExecuteBatchAsync(o, startedUtc, ct);
                batches++;
                var deletedNow = 0;
                foreach (var row in batch.Rows)
                {
                    totals[row.Entity] = totals.GetValueOrDefault(row.Entity) + row.Deleted;
                    deletedNow += row.Deleted;
                }
                if (!batch.Rows.Any(r => r.HasMore)) break;
                if (deletedNow == 0)
                {
                    log.LogError("Retention stopped: rows remain but a batch deleted nothing (locked by other work?); will retry");
                    return RetentionOutcome.Failed;
                }
                if (o.BatchPauseMilliseconds > 0) await Task.Delay(o.BatchPauseMilliseconds, ct);
            }

            watch.Stop();
            await SaveSuccessAsync(clock.GetUtcNow().UtcDateTime, watch.ElapsedMilliseconds, totals, ct);
            LastDeleted = totals;
            log.LogInformation("Retention completed in {Duration:0.0}s over {Batches} batch(es): {Counts}",
                watch.Elapsed.TotalSeconds, batches, Describe(totals));
            return RetentionOutcome.Completed;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) when (tenure.IsLost) { return LostLeadership(); }
        catch (Exception ex)
        {
            log.LogError(ex, "Retention failed after {Duration:0.0}s ({Counts}); the cleanup is idempotent and will be retried",
                watch.Elapsed.TotalSeconds, Describe(totals));
            return RetentionOutcome.Failed;
        }
    }

    private RetentionOutcome LostLeadership()
    {
        log.LogWarning("Retention stopped: this node can no longer prove it is the active worker. A standby never runs cleanup");
        return RetentionOutcome.LeadershipLost;
    }

    private async Task<bool> IsLeaderAsync(CancellationToken ct) => !tenure.IsLost && await leadership(ct);

    private sealed record BatchRow(string Entity, int Deleted, bool HasMore);
    private sealed record BatchResult(List<BatchRow> Rows);

    private async Task<BatchResult> ExecuteBatchAsync(RetentionOptions o, DateTime nowUtc, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MonitoringDbContext>();
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = Procedure;
        cmd.CommandType = CommandType.StoredProcedure;
        cmd.CommandTimeout = 300;
        Add(cmd, "@JobHistoryRetentionDays", o.JobHistoryRetentionDays);
        Add(cmd, "@AuditRetentionDays", o.AuditRetentionDays);
        Add(cmd, "@ChangeFeedRetentionDays", o.ChangeFeedRetentionDays);
        Add(cmd, "@BatchSize", o.BatchSize);
        Add(cmd, "@NowUtc", nowUtc); // fixed for the whole run, so every batch uses the same cutoffs
        Add(cmd, "@MaxBatches", 1); // one batch per table per call, so leadership is re-proven between batches

        var rows = new List<BatchRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add(new BatchRow(reader.GetString(0), reader.GetInt32(1), Convert.ToInt32(reader.GetValue(2)) == 1));
        return new BatchResult(rows);
    }

    private static void Add(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    private async Task<MaintenanceState> EnsureStateAsync(DateTime now, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MonitoringDbContext>();
        var state = await db.Maintenance.AsNoTracking().FirstOrDefaultAsync(m => m.Task == TaskName, ct);
        if (state is not null) return state;
        try
        {
            // First start ever: remember "now" as the baseline, so a fresh install does not delete anything before the
            // first scheduled night.
            state = new MaintenanceState { Task = TaskName, InitializedUtc = now };
            db.Maintenance.Add(state);
            await db.SaveChangesAsync(ct);
            return state;
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return await db.Maintenance.AsNoTracking().FirstAsync(m => m.Task == TaskName, ct);
        }
    }

    private async Task SaveSuccessAsync(DateTime now, long durationMs, Dictionary<string, int> totals, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MonitoringDbContext>();
        var summary = Describe(totals);
        var updated = await db.Maintenance.Where(m => m.Task == TaskName).ExecuteUpdateAsync(s => s
            .SetProperty(m => m.LastSuccessUtc, now).SetProperty(m => m.LastDurationMs, durationMs).SetProperty(m => m.LastSummary, summary), ct);
        if (updated == 0)
        {
            db.Maintenance.Add(new MaintenanceState
            { Task = TaskName, InitializedUtc = now, LastSuccessUtc = now, LastDurationMs = durationMs, LastSummary = summary });
            await db.SaveChangesAsync(ct);
        }
    }

    private static string Describe(Dictionary<string, int> totals) =>
        totals.Count == 0 ? "nothing deleted" : string.Join(", ", totals.Select(t => $"{t.Key}={t.Value}"));

    /// <summary>30 s, 1 min, 2 min ... capped at 15 min, +-20% jitter: a persistent failure never becomes a rapid loop.</summary>
    private static TimeSpan Backoff(int failures)
    {
        var seconds = Math.Min(900, 30 * Math.Pow(2, Math.Min(failures - 1, 5)));
        return TimeSpan.FromSeconds(seconds * (0.8 + Random.Shared.NextDouble() * 0.4));
    }
}
