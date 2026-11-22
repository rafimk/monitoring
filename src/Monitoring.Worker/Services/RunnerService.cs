using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Monitoring.Application;
using Monitoring.Application.Jobs;
using Monitoring.Application.Scheduling;
using Monitoring.Contracts;
using Monitoring.Infrastructure.Persistence;

namespace Monitoring.Worker.Services;

/// <summary>
/// Claims queued runs (serialised with an app lock so per-job concurrency rules are race-free across
/// workers), executes them in a DI scope with timeout/cancellation, renews leases and records outcomes.
/// </summary>
public sealed class RunnerService(
    IServiceScopeFactory scopes, JobRegistry registry, IOptions<WorkerOptions> options,
    WorkerIdentity identity, RunnerState state, ILogger<RunnerService> log) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, Task> _inflight = new();

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var o = options.Value;
        log.LogInformation("Runner started as worker {WorkerId} (parallelism {Max})", identity.Id, o.MaxParallelism);

        while (!stop.IsCancellationRequested)
        {
            try
            {
                var free = o.MaxParallelism - _inflight.Count;
                if (free > 0)
                {
                    foreach (var run in await ClaimAsync(free, stop))
                    {
                        var task = Task.Run(() => ExecuteRunAsync(run, stop), CancellationToken.None);
                        _inflight[run.Id] = task;
                        _ = task.ContinueWith(_ => { _inflight.TryRemove(run.Id, out Task? _); state.Running = _inflight.Count; },
                            TaskScheduler.Default);
                    }
                    state.Running = _inflight.Count;
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogError(ex, "Run claim failed; will retry"); }

            try { await Task.Delay(TimeSpan.FromSeconds(o.PollIntervalSeconds), stop); } catch (OperationCanceledException) { break; }
        }

        // Graceful shutdown: running jobs observe the cancelled token and are re-queued without consuming an attempt.
        var grace = Task.WhenAll(_inflight.Values);
        await Task.WhenAny(grace, Task.Delay(TimeSpan.FromSeconds(o.ShutdownGraceSeconds), CancellationToken.None));
    }

    private async Task<List<JobRun>> ClaimAsync(int slots, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MonitoringDbContext>();
        var now = DateTime.UtcNow;
        var lease = now.AddSeconds(options.Value.LeaseSeconds);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Serialise claim decisions cluster-wide; released automatically on commit/rollback.
        await db.Database.ExecuteSqlRawAsync(
            "DECLARE @r int; EXEC @r = sp_getapplock @Resource = 'monitoring-run-claim', @LockMode = 'Exclusive', " +
            "@LockOwner = 'Transaction', @LockTimeout = 10000; IF @r < 0 THROW 50001, 'Could not acquire run-claim lock', 1;", ct);

        var candidates = await db.Runs
            .Where(r => r.Status == RunStatus.Queued && r.NotBeforeUtc <= now)
            .OrderBy(r => r.QueuedUtc).Take(slots * 4).ToListAsync(ct);
        if (candidates.Count == 0) { await tx.CommitAsync(ct); return []; }

        var keys = candidates.Select(c => c.JobKey).Distinct().ToList();
        var defs = await db.Definitions.Where(d => keys.Contains(d.Key)).ToDictionaryAsync(d => d.Key, ct);
        var active = (await db.Runs.Where(r => r.Status == RunStatus.Running && keys.Contains(r.JobKey))
                .GroupBy(r => r.JobKey).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct))
            .ToDictionary(x => x.Key, x => x.N);

        var claimed = new List<JobRun>();
        foreach (var run in candidates)
        {
            if (claimed.Count >= slots) break;
            if (!defs.TryGetValue(run.JobKey, out var def) || !def.IsAvailable) continue; // stays queued; shown as unavailable

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
            run.LeaseExpiresUtc = lease;
            active[run.JobKey] = running + 1;
            db.Attempts.Add(new RunAttempt
            {
                Id = Guid.NewGuid(), RunId = run.Id, Attempt = run.Attempt,
                Status = RunStatus.Running, StartedUtc = now, WorkerId = identity.Id,
            });
            claimed.Add(run);
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

    private async Task ExecuteRunAsync(JobRun run, CancellationToken stop)
    {
        var o = options.Value;
        using var scope = scopes.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<MonitoringDbContext>();

        var def = await db.Definitions.AsNoTracking().FirstAsync(d => d.Key == run.JobKey, CancellationToken.None);
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(stop);
        using var renewCts = new CancellationTokenSource();
        var leaseLost = false;
        var timedOut = false;
        runCts.CancelAfter(TimeSpan.FromSeconds(def.TimeoutSeconds));

        var renew = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(o.HeartbeatSeconds));
            try
            {
                while (await timer.WaitForNextTickAsync(renewCts.Token))
                {
                    using var s = scopes.CreateScope();
                    var rdb = s.ServiceProvider.GetRequiredService<MonitoringDbContext>();
                    var until = DateTime.UtcNow.AddSeconds(o.LeaseSeconds);
                    var rows = await rdb.Runs
                        .Where(r => r.Id == run.Id && r.WorkerId == identity.Id && r.Status == RunStatus.Running)
                        .ExecuteUpdateAsync(u => u.SetProperty(r => r.LeaseExpiresUtc, until), CancellationToken.None);
                    if (rows == 0) { leaseLost = true; runCts.Cancel(); return; }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { log.LogWarning(ex, "Lease renewal failed for run {RunId}", run.Id); }
        });

        var status = RunStatus.Succeeded;
        string? error = null;
        try
        {
            if (!registry.TryGet(run.JobKey, out var reg)) throw new InvalidOperationException("No registered implementation");
            var job = (IJob)sp.GetRequiredService(reg.ImplementationType);
            var ctx = new JobExecutionContext(run.Id, run.JobKey, run.Attempt, run.Trigger, run.CorrelationId, run.ScheduledUtc);
            using (log.BeginScope(new Dictionary<string, object> { ["RunId"] = run.Id, ["JobKey"] = run.JobKey, ["CorrelationId"] = run.CorrelationId }))
            {
                var result = await job.ExecuteAsync(ctx, runCts.Token);
                if (!result.Success) { status = RunStatus.Failed; error = ErrorSanitizer.Sanitize(result.Message ?? "Job reported failure"); }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            status = RunStatus.Cancelled; error = "Worker shutting down";
        }
        catch (OperationCanceledException)
        {
            timedOut = !leaseLost;
            status = leaseLost ? RunStatus.Cancelled : RunStatus.TimedOut;
            error = leaseLost ? "Lease lost" : $"Timed out after {def.TimeoutSeconds}s";
        }
        catch (Exception ex)
        {
            status = RunStatus.Failed; error = ErrorSanitizer.Sanitize(ex);
        }
        finally
        {
            renewCts.Cancel();
            try { await renew; } catch { /* already logged */ }
        }

        if (leaseLost) { log.LogWarning("Run {RunId} lost its lease; result discarded", run.Id); return; }
        await CompleteAsync(db, run, def, status, error, timedOut);
    }

    private async Task CompleteAsync(MonitoringDbContext db, JobRun run, JobDefinition def, RunStatus status, string? error, bool timedOut)
    {
        var now = DateTime.UtcNow;
        var shuttingDown = status == RunStatus.Cancelled && error == "Worker shutting down";
        var failedLike = status is RunStatus.Failed or RunStatus.TimedOut;
        var retry = failedLike && RetryPolicy.CanRetry(run.Attempt, run.MaxAttempts);
        var requeue = retry || shuttingDown;

        var newStatus = requeue ? RunStatus.Queued : status;
        var notBefore = retry ? now + RetryPolicy.Delay(run.Attempt, def.RetryBaseDelaySeconds) : now;
        var attemptAfter = shuttingDown ? run.Attempt - 1 : run.Attempt; // a shutdown does not consume an attempt
        DateTime? ended = requeue ? null : now;
        long? duration = requeue ? null : (long)(now - (run.StartedUtc ?? now)).TotalMilliseconds;
        var attemptError = error;
        var runError = requeue ? (shuttingDown ? null : error) : error; // keep last error visible while waiting to retry

        var rows = await db.Runs
            .Where(r => r.Id == run.Id && r.WorkerId == identity.Id && r.Status == RunStatus.Running)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, newStatus)
                .SetProperty(r => r.EndedUtc, ended)
                .SetProperty(r => r.DurationMs, duration)
                .SetProperty(r => r.NotBeforeUtc, notBefore)
                .SetProperty(r => r.Attempt, attemptAfter)
                .SetProperty(r => r.WorkerId, requeue ? null : identity.Id)
                .SetProperty(r => r.LeaseExpiresUtc, (DateTime?)null)
                .SetProperty(r => r.Error, runError), CancellationToken.None);

        if (rows == 0) { log.LogWarning("Run {RunId} completion ignored: no longer owned by this worker", run.Id); return; }

        await db.Attempts.Where(a => a.RunId == run.Id && a.Attempt == run.Attempt)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Status, status)
                .SetProperty(a => a.EndedUtc, now)
                .SetProperty(a => a.Error, attemptError), CancellationToken.None);

        log.LogInformation("Run {RunId} of {Key} attempt {Attempt} => {Status}{Retry}", run.Id, run.JobKey, run.Attempt, status,
            retry ? $" (retry at {notBefore:O})" : "");
    }
}

/// <summary>Shared counter so the heartbeat can report current load.</summary>
public sealed class RunnerState { public volatile int Running; }
