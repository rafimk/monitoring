using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Monitoring.Application.Jobs;
using Monitoring.Application.Scheduling;
using Monitoring.Contracts;
using Monitoring.Infrastructure.Persistence;

namespace Monitoring.Worker.Services;

/// <summary>An attempt this worker has claimed and now owns. <see cref="Attempt"/> is the fencing token.</summary>
public sealed record ClaimedRun(
    Guid Id, string JobKey, TriggerSource Trigger, DateTime ScheduledUtc, string CorrelationId,
    int Attempt, int CountedAttempts, int MaxAttempts, DateTime AttemptStartedUtc, DateTime LeaseExpiresUtc,
    int TimeoutSeconds, int RetryBaseDelaySeconds);

public enum Completion
{
    /// <summary>The run reached a terminal status.</summary>
    Final,
    /// <summary>The attempt failed; the run is re-queued (counted attempt, backoff applies).</summary>
    Retry,
    /// <summary>The worker is shutting down; the run is re-queued and this attempt is not counted against MaxAttempts.</summary>
    Interrupted,
}

/// <summary>
/// All run-state transitions. Every write made on behalf of a running attempt is a compare-and-set on
/// (RunId, WorkerId, Attempt, Status = Running), so a worker that lost ownership - even to a later attempt of the
/// <i>same</i> worker - can never renew, finish or overwrite the run.
/// </summary>
public class RunStore(
    IServiceScopeFactory scopes, JobRegistry registry, IOptions<WorkerOptions> options, WorkerIdentity identity)
{
    public const string LeaseExpiredMessage = "Worker lease expired; run recovered";
    public const string ShutdownMessage = "Worker shutting down";

    /// <summary>
    /// Claims up to <paramref name="slots"/> queued runs this worker can execute. Claim decisions are serialised
    /// cluster-wide with an application lock so per-job concurrency policies hold across workers.
    /// </summary>
    public async Task<List<ClaimedRun>> ClaimAsync(int slots, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MonitoringDbContext>();
        var now = DateTime.UtcNow;
        var lease = now.AddSeconds(options.Value.LeaseSeconds);
        var mine = registry.All.Select(r => r.Key).ToList(); // never claim a run this worker has no implementation for
        if (mine.Count == 0) return [];

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync(
            "DECLARE @r int; EXEC @r = sp_getapplock @Resource = 'monitoring-run-claim', @LockMode = 'Exclusive', " +
            "@LockOwner = 'Transaction', @LockTimeout = 10000; IF @r < 0 THROW 50001, 'Could not acquire run-claim lock', 1;", ct);

        var candidates = await db.Runs
            .Where(r => r.Status == RunStatus.Queued && r.NotBeforeUtc <= now && mine.Contains(r.JobKey))
            .OrderBy(r => r.QueuedUtc).ThenBy(r => r.Id).Take(Math.Max(50, slots * 4)).ToListAsync(ct);
        if (candidates.Count == 0) { await tx.CommitAsync(ct); return []; }

        var keys = candidates.Select(c => c.JobKey).Distinct().ToList();
        var defs = await db.Definitions.Where(d => keys.Contains(d.Key)).ToDictionaryAsync(d => d.Key, ct);
        var active = (await db.Runs.Where(r => r.Status == RunStatus.Running && keys.Contains(r.JobKey))
                .GroupBy(r => r.JobKey).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct))
            .ToDictionary(x => x.Key, x => x.N);

        var claimed = new List<ClaimedRun>();
        foreach (var run in candidates)
        {
            if (claimed.Count >= slots) break;
            if (!defs.TryGetValue(run.JobKey, out var def)) continue;

            if (!def.Enabled && run.Trigger == TriggerSource.Schedule)
            {
                Finish(run, RunStatus.Cancelled, now, "Skipped: job disabled");
                continue;
            }

            active.TryGetValue(run.JobKey, out var running);
            if (def.ConcurrencyPolicy != ConcurrencyPolicy.AllowParallel && running > 0)
            {
                if (def.ConcurrencyPolicy == ConcurrencyPolicy.SkipIfRunning && run.Trigger == TriggerSource.Schedule)
                    Finish(run, RunStatus.Cancelled, now, "Skipped: previous run still active (overlap policy)");
                continue; // otherwise wait in the queue
            }

            run.Status = RunStatus.Running;
            run.WorkerId = identity.Id;
            run.StartedUtc ??= now;
            run.Attempt += 1;
            run.CountedAttempts += 1;
            run.LeaseExpiresUtc = lease;
            run.Error = null;
            active[run.JobKey] = running + 1;
            db.Attempts.Add(new RunAttempt
            {
                Id = Guid.NewGuid(), RunId = run.Id, Attempt = run.Attempt,
                Status = RunStatus.Running, StartedUtc = now, WorkerId = identity.Id,
            });
            claimed.Add(new ClaimedRun(run.Id, run.JobKey, run.Trigger, run.ScheduledUtc, run.CorrelationId,
                run.Attempt, run.CountedAttempts, run.MaxAttempts, now, lease, def.TimeoutSeconds, def.RetryBaseDelaySeconds));
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return claimed;
    }

    private static void Finish(JobRun run, RunStatus status, DateTime now, string error)
    {
        run.Status = status; run.EndedUtc = now; run.Error = error;
        run.DurationMs = run.StartedUtc is { } s ? (long)(now - s).TotalMilliseconds : 0;
    }

    /// <summary>Extends the lease. Returns false when this attempt no longer owns the run. Virtual so tests can inject store outages.</summary>
    public virtual async Task<bool> RenewLeaseAsync(ClaimedRun run, DateTime newExpiryUtc, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MonitoringDbContext>();
        var wid = identity.Id;
        var rows = await db.Runs
            .Where(r => r.Id == run.Id && r.WorkerId == wid && r.Attempt == run.Attempt && r.Status == RunStatus.Running)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.LeaseExpiresUtc, newExpiryUtc), ct);
        return rows > 0;
    }

    /// <summary>
    /// Records the outcome of an owned attempt in one transaction (run row + attempt row). Returns false - and writes
    /// nothing - when ownership was lost, which is how a stale worker is prevented from persisting a final result.
    /// </summary>
    public async Task<bool> CompleteAsync(
        ClaimedRun run, Completion kind, RunStatus attemptStatus, string? error, DateTime notBeforeUtc)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MonitoringDbContext>();
        var now = DateTime.UtcNow;
        var wid = identity.Id;

        var requeue = kind != Completion.Final;
        var newStatus = requeue ? RunStatus.Queued : attemptStatus;
        var counted = kind == Completion.Interrupted ? run.CountedAttempts - 1 : run.CountedAttempts;
        DateTime? ended = requeue ? null : now;
        long? duration = requeue ? null : (long)(now - run.AttemptStartedUtc).TotalMilliseconds;
        var runError = kind == Completion.Interrupted ? null : error;
        var workerAfter = requeue ? null : wid;
        var notBefore = kind == Completion.Interrupted ? now : notBeforeUtc;

        await using var tx = await db.Database.BeginTransactionAsync(CancellationToken.None);
        var rows = await db.Runs
            .Where(r => r.Id == run.Id && r.WorkerId == wid && r.Attempt == run.Attempt && r.Status == RunStatus.Running)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, newStatus)
                .SetProperty(r => r.EndedUtc, ended)
                .SetProperty(r => r.DurationMs, duration)
                .SetProperty(r => r.NotBeforeUtc, notBefore)
                .SetProperty(r => r.CountedAttempts, counted)
                .SetProperty(r => r.WorkerId, workerAfter)
                .SetProperty(r => r.LeaseExpiresUtc, (DateTime?)null)
                .SetProperty(r => r.Error, runError), CancellationToken.None);
        if (rows == 0) { await tx.RollbackAsync(CancellationToken.None); return false; }

        // The interrupted/failed attempt keeps its own history row under its own number; it is never deleted or reused.
        await db.Attempts.Where(a => a.RunId == run.Id && a.Attempt == run.Attempt && a.Status == RunStatus.Running)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Status, attemptStatus)
                .SetProperty(a => a.EndedUtc, now)
                .SetProperty(a => a.Error, error), CancellationToken.None);
        await tx.CommitAsync(CancellationToken.None);
        return true;
    }

    /// <summary>
    /// Running runs whose lease expired belong to a dead or stalled worker: re-queue (counted attempt stays used,
    /// because the job may have executed) or mark Abandoned when the retry budget is spent. Returns runs recovered.
    /// </summary>
    public async Task<int> RecoverExpiredAsync(DateTime nowUtc, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MonitoringDbContext>();

        var expired = await db.Runs.AsNoTracking()
            .Where(r => r.Status == RunStatus.Running && r.LeaseExpiresUtc < nowUtc)
            .OrderBy(r => r.LeaseExpiresUtc)
            .Select(r => new { r.Id, r.Attempt, r.CountedAttempts, r.MaxAttempts })
            .Take(100).ToListAsync(ct);

        var recovered = 0;
        foreach (var r in expired)
        {
            var retry = RetryPolicy.CanRetry(r.CountedAttempts, r.MaxAttempts);
            var status = retry ? RunStatus.Queued : RunStatus.Abandoned;

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            // Compare-and-set on the attempt number: only one recoverer wins, and only for the attempt that expired.
            var rows = await db.Runs
                .Where(x => x.Id == r.Id && x.Attempt == r.Attempt && x.Status == RunStatus.Running && x.LeaseExpiresUtc < nowUtc)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, status)
                    .SetProperty(x => x.WorkerId, (string?)null)
                    .SetProperty(x => x.LeaseExpiresUtc, (DateTime?)null)
                    .SetProperty(x => x.NotBeforeUtc, nowUtc)
                    .SetProperty(x => x.EndedUtc, retry ? (DateTime?)null : nowUtc)
                    .SetProperty(x => x.Error, LeaseExpiredMessage), ct);
            if (rows == 0) { await tx.RollbackAsync(ct); continue; }

            await db.Attempts.Where(a => a.RunId == r.Id && a.Attempt == r.Attempt && a.Status == RunStatus.Running)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.Status, RunStatus.Abandoned)
                    .SetProperty(a => a.EndedUtc, nowUtc)
                    .SetProperty(a => a.Error, LeaseExpiredMessage), ct);
            await tx.CommitAsync(ct);
            recovered++;
        }
        return recovered;
    }
}
