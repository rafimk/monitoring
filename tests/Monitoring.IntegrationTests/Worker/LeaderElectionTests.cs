using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Monitoring.Contracts;
using Monitoring.Infrastructure.Persistence;
using Monitoring.Worker;
using Monitoring.Worker.Services;

namespace Monitoring.IntegrationTests.Worker;

/// <summary>
/// Active/standby behaviour against real SQL Server: real coordinators, real session application locks, real KILLs.
/// Timings are short (1 s checks, 6 s leases) but every wait is condition-based.
/// </summary>
[Collection("worker-sql")]
public class LeaderElectionTests : WorkerTestBase
{
    private const string Resource = "monitoring-worker-leader";

    private static Task<bool> Is(TestNode n, WorkerRole role) => Task.FromResult(n.Coordinator.Role == role);

    private Task WaitForRoleAsync(TestNode n, WorkerRole role, string who, int seconds = 30) =>
        EventuallyAsync(() => Is(n, role), $"{who} to be {role}", seconds);

    private async Task AddScheduleAsync(string key, int intervalSeconds)
    {
        await using var db = NewDb();
        db.Schedules.Add(new JobSchedule { Id = Guid.NewGuid(), JobKey = key, IntervalSeconds = intervalSeconds, NextDueUtc = DateTime.UtcNow.AddSeconds(1) });
        await db.SaveChangesAsync();
    }

    private async Task<int> CountAsync(string key, RunStatus status)
    {
        await using var db = NewDb();
        return await db.Runs.CountAsync(r => r.JobKey == key && r.Status == status);
    }

    private (TestNode Active, TestNode Standby) Split(TestNode a, TestNode b) =>
        a.Coordinator.Role == WorkerRole.Active ? (a, b) : (b, a);

    private async Task<(TestNode Active, TestNode Standby)> WaitForExactlyOneActiveAsync(TestNode a, TestNode b)
    {
        await EventuallyAsync(() => Task.FromResult(
            (a.Coordinator.Role == WorkerRole.Active) ^ (b.Coordinator.Role == WorkerRole.Active)
            && (a.Coordinator.Role == WorkerRole.Standby || b.Coordinator.Role == WorkerRole.Standby)),
            "exactly one node Active and the other Standby");
        return Split(a, b);
    }

    private async Task<LeaderLock> HoldLeaderLockElsewhereAsync()
    {
        var holder = new LeaderLock(ConnectionString, Resource, "test-foreign-holder");
        Assert.True(await holder.TryAcquireAsync(CancellationToken.None), "the test could not take the lock first");
        return holder;
    }

    // ---- election ----

    [Fact]
    public async Task TwoNodes_ExactlyOneBecomesActive_AndOnlyItRegistersSchedulesAndExecutes()
    {
        const string key = "t.leader-one";
        await DefineJobAsync(key);
        var executions = new ConcurrentDictionary<Guid, int>();
        Scripts.Set(key, (ctx, _) => { executions.AddOrUpdate(ctx.RunId, 1, (_, n) => n + 1); return Task.FromResult(JobResult.Ok()); });
        await AddScheduleAsync(key, 2);

        var a = await StartNodeAsync([key], LeaderOptions("node-a"));
        var b = await StartNodeAsync([key], LeaderOptions("node-b"));
        Assert.True(a.StartDuration < TimeSpan.FromSeconds(3) && b.StartDuration < TimeSpan.FromSeconds(3),
            "StartAsync must return promptly (a Windows Service start must not wait for leadership)");

        var (active, standby) = await WaitForExactlyOneActiveAsync(a, b);
        await EventuallyAsync(async () => await CountAsync(key, RunStatus.Succeeded) >= 3, "three scheduled runs to succeed");

        await using var db = NewDb();
        var runs = await db.Runs.AsNoTracking().Where(r => r.JobKey == key).ToListAsync();
        Assert.All(runs, r => Assert.True(r.WorkerId is null || r.WorkerId == active.Identity.Id, $"run executed by {r.WorkerId}"));
        Assert.All(executions, kv => Assert.Equal(1, kv.Value));
        Assert.Equal(runs.Count, runs.Select(r => r.ScheduledUtc).Distinct().Count());

        // The standby registered nothing: no heartbeat row, no advertised capabilities, never initialised.
        Assert.Equal([active.Identity.Id], await db.Workers.Select(w => w.WorkerId).ToListAsync());
        Assert.Equal([active.Identity.Id], await db.WorkerJobs.Select(w => w.WorkerId).Distinct().ToListAsync());
        Assert.Equal(WorkerRole.Standby, standby.Coordinator.Role);
        Assert.Equal(0, standby.Coordinator.Activations);
        Assert.Equal(1, active.Coordinator.Activations);
    }

    [Fact]
    public async Task StoppingTheLeader_StandbyTakesOver_NoDuplicateOccurrences_AndJobsStayRunnable()
    {
        const string key = "t.leader-failover";
        await DefineJobAsync(key);
        var executions = new ConcurrentDictionary<Guid, int>();
        Scripts.Set(key, (ctx, _) => { executions.AddOrUpdate(ctx.RunId, 1, (_, n) => n + 1); return Task.FromResult(JobResult.Ok()); });
        await AddScheduleAsync(key, 2);

        var a = await StartNodeAsync([key], LeaderOptions("node-a"));
        var b = await StartNodeAsync([key], LeaderOptions("node-b"));
        var (first, second) = await WaitForExactlyOneActiveAsync(a, b);
        await EventuallyAsync(async () => await CountAsync(key, RunStatus.Succeeded) >= 2, "two runs by the first leader");

        await first.StopAsync();                       // graceful: runner stops, then the lock is released
        await WaitForRoleAsync(second, WorkerRole.Active, "the standby", seconds: 15);
        var takeover = DateTime.UtcNow;

        // A manual run for a job the new leader implements must execute shortly after takeover.
        var manual = await EnqueueManualAsync(key);
        await WaitForStatusAsync(manual, RunStatus.Succeeded, timeoutSeconds: 15);
        Assert.True(DateTime.UtcNow - takeover < TimeSpan.FromSeconds(15));
        Assert.Equal(second.Identity.Id, (await GetRunAsync(manual)).WorkerId);

        var before = await CountAsync(key, RunStatus.Succeeded);
        await EventuallyAsync(async () => await CountAsync(key, RunStatus.Succeeded) >= before + 2, "scheduled runs to continue under the new leader");

        await using var db = NewDb();
        var scheduled = await db.Runs.AsNoTracking().Where(r => r.JobKey == key && r.Trigger == TriggerSource.Schedule).ToListAsync();
        Assert.Equal(scheduled.Count, scheduled.Select(r => r.ScheduledUtc).Distinct().Count()); // no occurrence twice
        Assert.All(executions, kv => Assert.Equal(1, kv.Value));
        Assert.All(scheduled.Where(r => r.StartedUtc > takeover.AddSeconds(1)), r => Assert.Equal(second.Identity.Id, r.WorkerId));
        Assert.Equal(1, second.Coordinator.Activations);
    }

    [Fact]
    public async Task LossOfTheDedicatedSqlSession_StopsTheOldLeader_DiscardsItsResult_AndTheStandbyTakesOver()
    {
        const string key = "t.leader-killed";
        await DefineJobAsync(key, maxAttempts: 3, retryBaseSeconds: 1);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sawCancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        Scripts.Set(key, async (ctx, ct) =>
        {
            if (Interlocked.Increment(ref calls) > 1) return JobResult.Ok();   // the successor's attempt
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { sawCancellation.TrySetResult(); }
            return JobResult.Ok();   // a "successful" result that must NOT be persisted after leadership was lost
        });

        // A waits much longer than B between attempts, so B reliably wins the vacated lock.
        var a = await StartNodeAsync([key], LeaderOptions("node-a", standbyRetry: 10));
        await WaitForRoleAsync(a, WorkerRole.Active, "node A");
        var b = await StartNodeAsync([key], LeaderOptions("node-b", standbyRetry: 1));
        await WaitForRoleAsync(b, WorkerRole.Standby, "node B");

        var runId = await EnqueueManualAsync(key);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var spid = a.Coordinator.LeaderSessionId ?? throw new InvalidOperationException("leader has no session");
        await using (var admin = NewDb()) await admin.Database.ExecuteSqlRawAsync($"KILL {spid}");

        await sawCancellation.Task.WaitAsync(TimeSpan.FromSeconds(15));     // the old leader cancelled its running job
        await WaitForRoleAsync(a, WorkerRole.Standby, "old leader to stand down", seconds: 15);
        Assert.Contains(a.Logs.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("can no longer prove"));

        await WaitForRoleAsync(b, WorkerRole.Active, "node B to take over", seconds: 20);
        // The lease expires (6 s), B recovers the attempt and runs a second one. The old attempt is never recorded as Succeeded.
        await WaitForStatusAsync(runId, RunStatus.Succeeded, timeoutSeconds: 40);

        var run = await GetRunAsync(runId);
        var attempts = await GetAttemptsAsync(runId);
        Assert.Equal(b.Identity.Id, run.WorkerId);
        Assert.Equal(2, attempts.Count);
        Assert.Equal(a.Identity.Id, attempts[0].WorkerId);
        Assert.Equal(RunStatus.Abandoned, attempts[0].Status);              // stale outcome rejected / never written
        Assert.Equal(RunStatus.Succeeded, attempts[1].Status);
        Assert.Equal(1, b.Coordinator.Activations);
    }

    [Fact]
    public async Task RestartedFormerLeader_ComesBackAsStandby_WithoutDisturbingTheNewLeader()
    {
        const string key = "t.leader-restart";
        await DefineJobAsync(key);
        Scripts.Set(key, (_, _) => Task.FromResult(JobResult.Ok()));
        var a = await StartNodeAsync([key], LeaderOptions("node-a"));
        await WaitForRoleAsync(a, WorkerRole.Active, "node A");
        var b = await StartNodeAsync([key], LeaderOptions("node-b"));
        await WaitForRoleAsync(b, WorkerRole.Standby, "node B");

        await a.StopAsync();
        await WaitForRoleAsync(b, WorkerRole.Active, "node B after A stopped", seconds: 15);

        var a2 = await StartNodeAsync([key], LeaderOptions("node-a"));   // same configured name, new process
        await WaitForRoleAsync(a2, WorkerRole.Standby, "restarted node A");
        await Task.Delay(TimeSpan.FromSeconds(5));                          // several retry cycles

        Assert.Equal(WorkerRole.Active, b.Coordinator.Role);
        Assert.Equal(1, b.Coordinator.Activations);
        Assert.Equal(WorkerRole.Standby, a2.Coordinator.Role);
        Assert.Equal(0, a2.Coordinator.Activations);
        await using var db = NewDb();
        Assert.DoesNotContain(a2.Identity.Id, await db.Workers.Select(w => w.WorkerId).ToListAsync());
        Assert.Contains(b.Identity.Id, await db.Workers.Select(w => w.WorkerId).ToListAsync());
    }

    [Fact]
    public async Task GracefulStop_ReleasesTheLockImmediately()
    {
        const string key = "t.leader-release";
        await DefineJobAsync(key);
        Scripts.Set(key, (_, _) => Task.FromResult(JobResult.Ok()));
        var a = await StartNodeAsync([key], LeaderOptions("node-a"));
        await WaitForRoleAsync(a, WorkerRole.Active, "node A");
        await using (var probe = new LeaderLock(ConnectionString, Resource, "test-probe"))
            Assert.False(await probe.TryAcquireAsync(CancellationToken.None), "the leader must hold the lock while active");

        await a.StopAsync();

        await using var after = new LeaderLock(ConnectionString, Resource, "test-probe");
        Assert.True(await after.TryAcquireAsync(CancellationToken.None), "the lock must be free right after a graceful stop");
    }

    // ---- standby behaviour ----

    [Fact]
    public async Task OrdinaryLockContention_IsAQuietStandby_NotAnError_AndTheNodeActivatesWhenTheLockIsFree()
    {
        const string key = "t.leader-contention";
        await DefineJobAsync(key);
        Scripts.Set(key, (_, _) => Task.FromResult(JobResult.Ok()));
        await using var holder = await HoldLeaderLockElsewhereAsync();

        var node = await StartNodeAsync([key], LeaderOptions("node-a"));
        await WaitForRoleAsync(node, WorkerRole.Standby, "node");
        await Task.Delay(TimeSpan.FromSeconds(5));                          // ~5 retries against a held lock

        Assert.DoesNotContain(node.Logs.Entries, e => e.Level >= LogLevel.Warning);
        Assert.Single(node.Logs.Entries, e => e.Message.Contains("is STANDBY"));
        await using (var db = NewDb())
        {
            Assert.Empty(await db.Workers.ToListAsync());                   // advertises nothing
            Assert.Empty(await db.WorkerJobs.ToListAsync());
        }

        await holder.ReleaseAsync();
        await WaitForRoleAsync(node, WorkerRole.Active, "node after the lock became free", seconds: 15);
    }

    [Fact]
    public async Task StandbyDoesNotInitialiseSchedulePollOrExecute_WhileAnotherNodeHoldsTheLock()
    {
        const string key = "t.leader-passive";
        await DefineJobAsync(key);
        var calls = 0;
        Scripts.Set(key, (_, _) => { Interlocked.Increment(ref calls); return Task.FromResult(JobResult.Ok()); });
        await AddScheduleAsync(key, 1);
        var manual = await EnqueueManualAsync(key);
        await using var holder = await HoldLeaderLockElsewhereAsync();

        var node = await StartNodeAsync([key], LeaderOptions("node-a"));
        await WaitForRoleAsync(node, WorkerRole.Standby, "node");
        await Task.Delay(TimeSpan.FromSeconds(5));

        Assert.Equal(0, calls);
        Assert.Equal(RunStatus.Queued, (await GetRunAsync(manual)).Status);   // not claimed
        await using var db = NewDb();
        Assert.Equal(1, await db.Runs.CountAsync());                          // nothing scheduled
        Assert.Empty(await db.Workers.ToListAsync());
    }

    [Fact]
    public async Task SqlOutage_KeepsTheNodeAHealthyStandby_WithBackoffAndNoCrash()
    {
        const string key = "t.leader-outage";
        Scripts.Set(key, (_, _) => Task.FromResult(JobResult.Ok()));
        // Nothing listens on this port: every connection attempt fails, like a database that is down.
        var down = "Server=127.0.0.1,1;Database=Monitoring;User Id=sa;Password=x;TrustServerCertificate=True;Connect Timeout=2";

        var node = await StartNodeAsync([key], LeaderOptions("node-a"), down);
        Assert.True(node.StartDuration < TimeSpan.FromSeconds(3), "an unreachable database must not block service start");
        await Task.Delay(TimeSpan.FromSeconds(8));

        Assert.Equal(WorkerRole.Standby, node.Coordinator.Role);
        Assert.Equal(0, node.Coordinator.Activations);
        var warnings = node.Logs.Entries.Count(e => e.Level == LogLevel.Warning && e.Category.EndsWith(nameof(WorkerCoordinator)));
        Assert.InRange(warnings, 1, 3);                                       // announced, not logged on every retry
        Assert.DoesNotContain(node.Logs.Entries, e => e.Level >= LogLevel.Error);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await node.StopAsync();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), "stopping during an outage must be prompt");
    }

    [Fact]
    public async Task StoppingWhileWaitingToRetry_CancelsPromptly_EvenWithALongRetryInterval()
    {
        const string key = "t.leader-cancel";
        Scripts.Set(key, (_, _) => Task.FromResult(JobResult.Ok()));
        await using var holder = await HoldLeaderLockElsewhereAsync();
        var node = await StartNodeAsync([key], LeaderOptions("node-a", standbyRetry: 120));
        await WaitForRoleAsync(node, WorkerRole.Standby, "node");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await node.StopAsync();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"stop took {sw.Elapsed}");
        Assert.Equal(WorkerRole.Stopping, node.Coordinator.Role);
    }

    [Fact]
    public async Task LeaderModeDisabled_EveryNodeIsActive_AsBefore()
    {
        const string key = "t.leader-disabled";
        await DefineJobAsync(key);
        Scripts.Set(key, (_, _) => Task.FromResult(JobResult.Ok()));
        var o1 = LeaderOptions("node-a"); o1.LeaderMode = WorkerLeaderMode.Disabled;
        var o2 = LeaderOptions("node-b"); o2.LeaderMode = WorkerLeaderMode.Disabled;
        await using var holder = await HoldLeaderLockElsewhereAsync();        // would block a node if election were on

        var a = await StartNodeAsync([key], o1);
        var b = await StartNodeAsync([key], o2);
        await WaitForRoleAsync(a, WorkerRole.Active, "node A");
        await WaitForRoleAsync(b, WorkerRole.Active, "node B");
        await using var db = NewDb();
        await EventuallyAsync(async () => await db.Workers.CountAsync() == 2, "both nodes to register");
    }
}
