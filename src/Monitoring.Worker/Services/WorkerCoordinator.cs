using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Monitoring.Infrastructure.Persistence;

namespace Monitoring.Worker.Services;

public enum WorkerRole
{
    /// <summary>Not started yet, or between tenures.</summary>
    Starting,
    /// <summary>Healthy but passive: another node holds the leader lock. Nothing is registered, scheduled or executed.</summary>
    Standby,
    /// <summary>Holds the leader lock; initialised, heartbeating, scheduling and executing.</summary>
    Active,
    Stopping,
}

/// <summary>One leadership period. <see cref="Lost"/> fires when ownership could not be proven (fail closed).</summary>
public sealed class Tenure
{
    private readonly CancellationTokenSource _lost = new();
    public CancellationToken Lost => _lost.Token;
    public bool IsLost => _lost.IsCancellationRequested;
    public void MarkLost() => _lost.Cancel();
}

/// <summary>
/// Decides when this node is the active worker. In <see cref="WorkerLeaderMode.ActiveStandby"/> mode it competes for one
/// SQL Server session application lock (see <see cref="LeaderLock"/>): the winner runs the initializer, heartbeat,
/// scheduler and runner for as long as it can prove it still owns the lock; everyone else retries quietly as a standby.
/// A new set of loop instances is created for every tenure, so a node can lose and regain leadership.
///
/// This is an availability mechanism, not an exactly-once guarantee: the per-run claim and attempt fencing in
/// <see cref="RunStore"/> stay in force as the second layer, and a job that ignores cancellation can still be running
/// when a successor starts the next attempt.
/// </summary>
public sealed class WorkerCoordinator(
    IServiceProvider services, IOptions<WorkerOptions> options, WorkerIdentity identity, RunnerState state,
    ILogger<WorkerCoordinator> log) : BackgroundService
{
    private volatile WorkerRole _role = WorkerRole.Starting;
    private int _activations;
    private int? _sessionId;

    public WorkerRole Role => _role;
    /// <summary>How many times this process has become active (diagnostics and tests).</summary>
    public int Activations => Volatile.Read(ref _activations);
    /// <summary>SQL session id holding the leader lock right now (diagnostics and tests).</summary>
    public int? LeaderSessionId => _sessionId;

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        // Never block the host's StartAsync (Windows Service start must complete promptly): leave synchronously now.
        await Task.Yield();

        var o = options.Value;
        if (o.LeaderMode == WorkerLeaderMode.Disabled)
        {
            log.LogInformation("Leader election is disabled (Worker:LeaderMode=Disabled): this node is always active");
            await RunTenureAsync(null, stop);
            return;
        }

        var retry = TimeSpan.FromSeconds(o.StandbyRetrySeconds);
        var failures = 0;            // consecutive failed acquisitions or short/abnormal tenures: drives the backoff
        var announcedStandby = false;
        var lastError = "";

        while (!stop.IsCancellationRequested)
        {
            LeaderLock? lease = null;
            var delay = retry;
            try
            {
                var cs = ConnectionString();
                lease = new LeaderLock(cs, o.LeaderLockResource);
                if (!await lease.TryAcquireAsync(stop))
                {
                    // Ordinary contention: not an error, nothing to log above Debug after the first announcement.
                    _role = WorkerRole.Standby;
                    if (!announcedStandby)
                    {
                        log.LogInformation("Worker {WorkerId} is STANDBY: another node holds the leader lock '{Resource}'. Retrying every {Seconds}s",
                            identity.Id, o.LeaderLockResource, o.StandbyRetrySeconds);
                        announcedStandby = true;
                    }
                    else log.LogDebug("Still standby; leader lock '{Resource}' is held elsewhere", o.LeaderLockResource);
                    failures = 0;
                }
                else
                {
                    announcedStandby = false;
                    _sessionId = lease.SessionId;
                    var began = DateTime.UtcNow;
                    var clean = await RunTenureAsync(lease, stop);
                    // A tenure that ends abnormally (lost ownership, failed initialisation) must not turn into a tight
                    // loop: back off, longer when it keeps happening, and reset only after a stable period.
                    var stable = DateTime.UtcNow - began > TimeSpan.FromSeconds(Math.Max(30, o.LeaseSeconds * 2));
                    failures = clean || stable ? 0 : failures + 1;
                    if (!clean) delay = Backoff(retry, Math.Max(1, failures));
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                if (ex is SqlException { Number: 4060 } && o.ApplyMigrationsOnStartup && await TryCreateDatabaseAsync(stop))
                {
                    delay = TimeSpan.FromMilliseconds(200); // development bootstrap: the database now exists, retry at once
                    continue;
                }
                // Database outage or connection error: stay a healthy standby and back off instead of spinning.
                _role = WorkerRole.Standby;
                failures++;
                delay = Backoff(retry, failures);
                var message = ex is SqlException sql ? $"SQL error {sql.Number}: {sql.Message}" : ex.Message;
                if (message != lastError || failures % 12 == 0)
                    log.LogWarning("Cannot reach the database to contend for the leader lock ({Message}); standby, next try in {Delay:0}s", message, delay.TotalSeconds);
                lastError = message;
            }
            finally
            {
                _sessionId = null;
                if (lease is not null) await lease.DisposeAsync();
            }

            if (stop.IsCancellationRequested) break;
            try { await Task.Delay(delay, stop); } catch (OperationCanceledException) { break; }
        }

        _role = WorkerRole.Stopping;
        log.LogInformation("Worker {WorkerId} stopped (leader lock released if held)", identity.Id);
    }

    /// <summary>Runs one active tenure. Returns true when it ended because the host is stopping.</summary>
    private async Task<bool> RunTenureAsync(LeaderLock? lease, CancellationToken stop)
    {
        var o = options.Value;
        var tenure = new Tenure();
        state.Running = 0;
        BackgroundService? heartbeat = null, scheduler = null, runner = null, retention = null;
        var proofTimeout = TimeSpan.FromSeconds(Math.Max(2, o.LeaderCheckSeconds));
        // Same proof the tenure loop uses; the nightly cleanup calls it immediately before every batch.
        LeadershipCheck proof = async ct => lease is null || (await VerifySafelyAsync(lease, proofTimeout, ct)).Item1;
        var started = new List<BackgroundService>();
        var clean = true;

        try
        {
            try
            {
                // 1) Initialise as the active worker (migrations in dev, definitions, registration, seed).
                await ActivatorUtilities.CreateInstance<StartupInitializer>(services).StartAsync(stop);

                // 2) Start the loops. They are created per tenure because a BackgroundService cannot be restarted.
                heartbeat = ActivatorUtilities.CreateInstance<HeartbeatService>(services);
                scheduler = ActivatorUtilities.CreateInstance<SchedulerService>(services);
                runner = ActivatorUtilities.CreateInstance<RunnerService>(services, tenure);
                // Created only here, i.e. only on the active node: a standby never runs retention. Isolated from the other
                // loops, so a cleanup failure cannot affect job execution.
                retention = ActivatorUtilities.CreateInstance<RetentionService>(services, tenure, proof);
                foreach (var loop in new[] { heartbeat, scheduler, runner, retention })
                {
                    await loop.StartAsync(CancellationToken.None);
                    started.Add(loop);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return true; }
            catch (Exception ex)
            {
                clean = false;
                log.LogError(ex, "Worker {WorkerId} won the leader lock but could not initialise; releasing it so another node can try", identity.Id);
                return false;
            }

            Interlocked.Increment(ref _activations);
            _role = WorkerRole.Active;
            log.LogInformation("Worker {WorkerId} is ACTIVE{Session}", identity.Id,
                lease is null ? " (leader election disabled)" : $" (leader lock '{o.LeaderLockResource}' held by SQL session {lease.SessionId})");

            if (lease is null)
            {
                try { await Task.Delay(Timeout.Infinite, stop); } catch (OperationCanceledException) { }
                return true;
            }

            // 3) Prove ownership periodically for the whole tenure; anything short of proof is a loss (fail closed).
            var check = TimeSpan.FromSeconds(o.LeaderCheckSeconds);
            while (true)
            {
                try { await Task.Delay(check, stop); } catch (OperationCanceledException) { return true; }
                var (held, reason) = await VerifySafelyAsync(lease, proofTimeout, stop);
                if (stop.IsCancellationRequested) return true;
                if (held) continue;

                clean = false;
                tenure.MarkLost(); // cancels running jobs, stops lease renewal, makes results get discarded
                log.LogError("Worker {WorkerId} can no longer prove it holds the leader lock ({Reason}). Stopping all work and returning to standby; " +
                             "running attempts are not written back and will be recovered by the next active node after their lease expires",
                    identity.Id, reason);
                return false;
            }
        }
        finally
        {
            _role = clean ? WorkerRole.Stopping : WorkerRole.Standby;
            await StopLoopsAsync(started, lost: tenure.IsLost);
            // The lock is released by the caller only after everything above has stopped, so a successor never starts
            // while this node's graceful shutdown (re-queueing of running jobs) is still in progress.
        }
    }

    private async Task<(bool, string?)> VerifySafelyAsync(LeaderLock lease, TimeSpan timeout, CancellationToken stop)
    {
        try { return await lease.VerifyAsync(timeout, stop); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { return (true, null); }
    }

    private async Task StopLoopsAsync(List<BackgroundService> started, bool lost)
    {
        if (started.Count == 0) return;
        try
        {
            if (lost)
            {
                // Immediate: no ordering, no waiting for jobs.
                await Task.WhenAll(started.Select(l => l.StopAsync(CancellationToken.None)));
            }
            else
            {
                // Graceful: runner first (it waits up to ShutdownGraceSeconds and re-queues interrupted runs), then the rest.
                foreach (var loop in Enumerable.Reverse(started)) await loop.StopAsync(CancellationToken.None);
            }
        }
        catch (Exception ex) { log.LogWarning(ex, "Error while stopping the worker loops"); }
        state.Running = 0;
    }

    private string ConnectionString()
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MonitoringDbContext>();
        return db.Database.GetConnectionString() ?? throw new InvalidOperationException("Monitoring connection string is not configured");
    }

    /// <summary>Development bootstrap only (ApplyMigrationsOnStartup): the database does not exist yet, so nobody can lock in it.</summary>
    private async Task<bool> TryCreateDatabaseAsync(CancellationToken ct)
    {
        try
        {
            log.LogInformation("Monitoring database does not exist yet; creating it (Worker:ApplyMigrationsOnStartup=true)");
            using var scope = services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<MonitoringDbContext>().Database.MigrateAsync(ct);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not create the database");
            return false;
        }
    }

    /// <summary>Exponential backoff with a 60 s ceiling and +-20% jitter so two nodes do not retry in lock step.</summary>
    private static TimeSpan Backoff(TimeSpan baseDelay, int failures)
    {
        var seconds = Math.Min(60, baseDelay.TotalSeconds * Math.Pow(2, Math.Min(failures - 1, 6)));
        seconds = Math.Max(seconds, baseDelay.TotalSeconds);
        return TimeSpan.FromSeconds(seconds * (0.8 + Random.Shared.NextDouble() * 0.4));
    }
}
