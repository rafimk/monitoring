using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Monitoring.Application;
using Monitoring.Application.Jobs;
using Monitoring.Application.Leasing;
using Monitoring.Application.Scheduling;
using Monitoring.Contracts;

namespace Monitoring.Worker.Services;

/// <summary>
/// Claims queued runs, executes them in a DI scope with timeout/cancellation, keeps their leases alive and records
/// outcomes. Execution is at-least-once: if a worker dies, or loses its lease, the run is recovered and may execute
/// again, so job code must be idempotent.
/// </summary>
public sealed class RunnerService(
    IServiceScopeFactory scopes, JobRegistry registry, IOptions<WorkerOptions> options, RunStore store,
    WorkerIdentity identity, RunnerState state, ILogger<RunnerService> log, TimeProvider? timeProvider = null, Tenure? tenure = null) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, Task> _inflight = new();
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var o = options.Value;
        log.LogInformation("Runner started as worker {WorkerId} (parallelism {Max})", identity.Id, o.MaxParallelism);

        while (!stop.IsCancellationRequested && tenure?.IsLost != true)
        {
            try
            {
                var free = o.MaxParallelism - _inflight.Count;
                if (free > 0)
                {
                    foreach (var run in await store.ClaimAsync(free, stop))
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

        // Graceful shutdown: running jobs observe the cancelled token and are re-queued (the interrupted attempt stays
        // in the history but does not count against MaxAttempts).
        // After lost leadership nothing is waited for: results are discarded and the leases simply expire.
        if (tenure?.IsLost == true) return;
        var grace = Task.WhenAll(_inflight.Values);
        await Task.WhenAny(grace, Task.Delay(TimeSpan.FromSeconds(o.ShutdownGraceSeconds), CancellationToken.None));
    }

    private async Task ExecuteRunAsync(ClaimedRun run, CancellationToken stop)
    {
        try { await ExecuteCoreAsync(run, stop); }
        catch (Exception ex)
        {
            // Typically the database was unreachable when recording the outcome. The lease will expire and the run
            // will be recovered (at-least-once), so this must never crash the worker.
            log.LogError(ex, "Run {RunId} attempt {Attempt} could not record its outcome; lease expiry will recover it", run.Id, run.Attempt);
        }
    }

    private async Task ExecuteCoreAsync(ClaimedRun run, CancellationToken stop)
    {
        var o = options.Value;
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(stop, tenure?.Lost ?? CancellationToken.None);
        using var renewCts = CancellationTokenSource.CreateLinkedTokenSource(tenure?.Lost ?? CancellationToken.None); // lost leadership: stop renewing so the lease expires
        runCts.CancelAfter(TimeSpan.FromSeconds(run.TimeoutSeconds));

        // Lease renewal runs beside the job. If ownership cannot be kept, cancel the job before the lease can expire.
        var renewer = new LeaseRenewer(
            TimeSpan.FromSeconds(o.LeaseSeconds), TimeSpan.FromSeconds(o.HeartbeatSeconds),
            o.RenewalRetryDelay, o.LeaseSafetyMargin, _time);
        var renewTask = Task.Run(async () =>
        {
            var loss = await renewer.RunAsync(run.LeaseExpiresUtc,
                (expiry, ct) => store.RenewLeaseAsync(run, expiry, ct),
                ex => log.LogWarning(ex, "Lease renewal failed for run {RunId} attempt {Attempt}; retrying", run.Id, run.Attempt),
                renewCts.Token);
            if (loss is not null) runCts.Cancel();
            return loss;
        });

        if (tenure?.IsLost == true) { renewCts.Cancel(); await renewTask; return; } // claimed just before leadership was lost

        var status = RunStatus.Succeeded;
        string? error = null;
        try
        {
            if (!registry.TryGet(run.JobKey, out var reg)) throw new InvalidOperationException("No registered implementation");
            using var scope = scopes.CreateScope();
            var job = (IJob)scope.ServiceProvider.GetRequiredService(reg.ImplementationType);
            var ctx = new JobExecutionContext(run.Id, run.JobKey, run.Attempt, run.Trigger, run.CorrelationId, run.ScheduledUtc);
            using (log.BeginScope(new Dictionary<string, object> { ["RunId"] = run.Id, ["JobKey"] = run.JobKey, ["CorrelationId"] = run.CorrelationId }))
            {
                var result = await job.ExecuteAsync(ctx, runCts.Token);
                if (!result.Success) { status = RunStatus.Failed; error = ErrorSanitizer.Sanitize(result.Message ?? "Job reported failure"); }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            status = RunStatus.Cancelled; error = RunStore.ShutdownMessage;
        }
        catch (OperationCanceledException) when (runCts.IsCancellationRequested)
        {
            // Our own token fired but the host is not stopping: either the timeout, or a lost lease (decided below).
            status = RunStatus.TimedOut; error = $"Timed out after {run.TimeoutSeconds}s";
        }
        catch (Exception ex)
        {
            status = RunStatus.Failed; error = ErrorSanitizer.Sanitize(ex);
        }
        finally
        {
            renewCts.Cancel();
        }

        // Leadership lost (fail closed): whatever the job did, do not write its outcome. The attempt stays Running
        // until its lease expires; the successor then recovers it under the normal at-least-once policy.
        if (tenure?.IsLost == true)
        {
            await renewTask;
            log.LogWarning("Run {RunId} attempt {Attempt} ended after this node lost the worker leadership; its result is discarded", run.Id, run.Attempt);
            return;
        }

        var lossReason = await renewTask;
        if (lossReason is not null)
        {
            // Ownership is gone (or could not be proven in time). Whatever the job returned, it must not be persisted:
            // the run will be/has been recovered and executed again. At-least-once, never exactly-once.
            log.LogWarning("Run {RunId} attempt {Attempt} lost its lease ({Reason}); its result is discarded", run.Id, run.Attempt, lossReason);
            return;
        }

        await RecordAsync(run, status, error);
    }

    private async Task RecordAsync(ClaimedRun run, RunStatus status, string? error)
    {
        var interrupted = status == RunStatus.Cancelled && error == RunStore.ShutdownMessage;
        var retry = (status is RunStatus.Failed or RunStatus.TimedOut) && RetryPolicy.CanRetry(run.CountedAttempts, run.MaxAttempts);
        var kind = interrupted ? Completion.Interrupted : retry ? Completion.Retry : Completion.Final;
        var notBefore = retry
            ? DateTime.UtcNow + RetryPolicy.Delay(run.CountedAttempts, run.RetryBaseDelaySeconds)
            : DateTime.UtcNow;

        var owned = await store.CompleteAsync(run, kind, status, error, notBefore);
        if (!owned)
        {
            log.LogWarning("Run {RunId} attempt {Attempt} completion ignored: no longer owned by this worker", run.Id, run.Attempt);
            return;
        }
        log.LogInformation("Run {RunId} of {Key} attempt {Attempt} => {Status}{Next}", run.Id, run.JobKey, run.Attempt, status,
            kind == Completion.Retry ? $" (retry at {notBefore:O})" : kind == Completion.Interrupted ? " (re-queued)" : "");
    }
}

/// <summary>Shared counter so the heartbeat can report current load.</summary>
public sealed class RunnerState { public volatile int Running; }
