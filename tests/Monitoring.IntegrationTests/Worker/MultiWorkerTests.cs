using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Monitoring.Contracts;
using Monitoring.Infrastructure.Persistence;

namespace Monitoring.IntegrationTests.Worker;

/// <summary>Two (or more) real worker instances sharing one database.</summary>
[Collection("worker-sql")]
public class MultiWorkerTests : WorkerTestBase
{
    [Fact]
    public async Task TwoWorkers_ExecuteEveryQueuedRunExactlyOnce_AndBothDoWork()
    {
        const string key = "t.fanout";
        await DefineJobAsync(key, concurrency: ConcurrencyPolicy.AllowParallel);
        var executions = new ConcurrentDictionary<Guid, int>();
        Scripts.Set(key, async (ctx, ct) =>
        {
            executions.AddOrUpdate(ctx.RunId, 1, (_, n) => n + 1);
            await Task.Delay(150, ct);
            return JobResult.Ok();
        });
        var a = await StartWorkerAsync([key]);
        var b = await StartWorkerAsync([key]);

        var ids = new List<Guid>();
        for (var i = 0; i < 24; i++) ids.Add(await EnqueueManualAsync(key));
        await EventuallyAsync(async () =>
        {
            await using var db = NewDb();
            return await db.Runs.CountAsync(r => r.Status == RunStatus.Succeeded) == ids.Count;
        }, "all 24 runs to succeed", timeoutSeconds: 45);

        Assert.Equal(24, executions.Count);
        Assert.All(executions, kv => Assert.Equal(1, kv.Value)); // no run was executed twice
        await using var check = NewDb();
        var runs = await check.Runs.AsNoTracking().ToListAsync();
        Assert.All(runs, r => { Assert.Equal(1, r.Attempt); Assert.Equal(1, r.CountedAttempts); });
        Assert.Equal(new[] { a.Identity.Id, b.Identity.Id }.Order(), runs.Select(r => r.WorkerId!).Distinct().Order());
    }

    [Fact]
    public async Task TwoWorkers_ScheduledJob_NeverDispatchTheSameOccurrenceTwice()
    {
        const string key = "t.sched";
        await DefineJobAsync(key);
        var executions = new ConcurrentDictionary<Guid, int>();
        Scripts.Set(key, (ctx, _) => { executions.AddOrUpdate(ctx.RunId, 1, (_, n) => n + 1); return Task.FromResult(JobResult.Ok()); });
        var a = await StartWorkerAsync([key]);
        var b = await StartWorkerAsync([key]);
        await using (var db = NewDb())
        {
            db.Schedules.Add(new JobSchedule { Id = Guid.NewGuid(), JobKey = key, IntervalSeconds = 2, NextDueUtc = DateTime.UtcNow.AddSeconds(1) });
            await db.SaveChangesAsync();
        }

        await Task.Delay(TimeSpan.FromSeconds(10));
        await a.StopAsync();
        await b.StopAsync();

        await using var check = NewDb();
        var runs = await check.Runs.AsNoTracking().Where(r => r.JobKey == key).OrderBy(r => r.ScheduledUtc).ToListAsync();
        Assert.True(runs.Count >= 3, $"expected several occurrences in 10s, got {runs.Count}");
        Assert.Equal(runs.Count, runs.Select(r => r.ScheduledUtc).Distinct().Count()); // one run per occurrence
        Assert.All(runs.Zip(runs.Skip(1)), p => Assert.Equal(0, (p.Second.ScheduledUtc - p.First.ScheduledUtc).TotalSeconds % 2)); // anchored cadence
        Assert.All(executions, kv => Assert.Equal(1, kv.Value));
        Assert.All(runs, r => Assert.Contains(r.Status, new[] { RunStatus.Succeeded, RunStatus.Queued }));
    }

    [Fact]
    public async Task JobLongerThanItsInterval_NeverOverlaps_AcrossTwoWorkers_AndSkippedOccurrencesAreRecorded()
    {
        const string key = "t.long";
        await DefineJobAsync(key, maxAttempts: 1, concurrency: ConcurrencyPolicy.SkipIfRunning);
        var active = 0;
        var maxActive = 0;
        Scripts.Set(key, async (_, ct) =>
        {
            var now = Interlocked.Increment(ref active);
            InterlockedMax(ref maxActive, now);
            try { await Task.Delay(TimeSpan.FromSeconds(3), ct); } // interval is 2s: every run outlasts its successor's due time
            finally { Interlocked.Decrement(ref active); }
            return JobResult.Ok();
        });
        var a = await StartWorkerAsync([key]);
        var b = await StartWorkerAsync([key]);
        await using (var db = NewDb())
        {
            db.Schedules.Add(new JobSchedule { Id = Guid.NewGuid(), JobKey = key, IntervalSeconds = 2, NextDueUtc = DateTime.UtcNow.AddSeconds(1) });
            await db.SaveChangesAsync();
        }

        await Task.Delay(TimeSpan.FromSeconds(14));
        await a.StopAsync();
        await b.StopAsync();

        Assert.Equal(1, maxActive); // never two executions at once, even with two workers
        await using var check = NewDb();
        var runs = await check.Runs.AsNoTracking().Where(r => r.JobKey == key).ToListAsync();
        var succeeded = runs.Count(r => r.Status == RunStatus.Succeeded);
        var skipped = runs.Where(r => r.Status == RunStatus.Cancelled).ToList();
        Assert.True(succeeded >= 2, $"expected at least 2 completed runs, got {succeeded}");
        Assert.True(skipped.Count >= 2, $"expected overlapping occurrences to be skipped, got {skipped.Count}");
        Assert.All(skipped, r => Assert.Contains("previous run still active", r.Error));
        Assert.DoesNotContain(runs, r => r.Status is RunStatus.Failed or RunStatus.TimedOut);
        Assert.Equal(runs.Count, runs.Select(r => r.ScheduledUtc).Distinct().Count()); // every occurrence accounted for exactly once
    }

    [Fact]
    public async Task SameConfiguredWorkerId_StillGivesEachInstanceItsOwnHeartbeatAndOwnership()
    {
        const string key = "t.dup";
        await DefineJobAsync(key, concurrency: ConcurrencyPolicy.AllowParallel);
        Scripts.Set(key, (_, _) => Task.FromResult(JobResult.Ok()));
        var a = await StartWorkerAsync([key], FastOptions("same-name", heartbeat: 1));
        var b = await StartWorkerAsync([key], FastOptions("same-name", heartbeat: 1));

        Assert.NotEqual(a.Identity.Id, b.Identity.Id);
        await using (var db = NewDb())
        {
            var rows = await db.Workers.AsNoTracking().ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.All(rows, r => Assert.StartsWith("same-name-", r.WorkerId));
            Assert.Equal(2, await db.WorkerJobs.CountAsync(w => w.JobKey == key));
        }

        // Each heartbeat advances only its own row.
        var before = await HeartbeatsAsync();
        await EventuallyAsync(async () =>
        {
            var now = await HeartbeatsAsync();
            return now[a.Identity.Id] > before[a.Identity.Id] && now[b.Identity.Id] > before[b.Identity.Id];
        }, "both instances to heartbeat independently");

        // Runs are owned by the instance that claimed them, never by the shared name.
        var runIds = new List<Guid>();
        for (var i = 0; i < 6; i++) runIds.Add(await EnqueueManualAsync(key));
        await EventuallyAsync(async () => { await using var db = NewDb(); return await db.Runs.CountAsync(r => r.Status == RunStatus.Succeeded) == 6; }, "runs to finish");
        await using var check = NewDb();
        Assert.All(await check.Runs.AsNoTracking().ToListAsync(), r => Assert.Contains(r.WorkerId, new[] { a.Identity.Id, b.Identity.Id }));
    }

    private async Task<Dictionary<string, DateTime>> HeartbeatsAsync()
    {
        await using var db = NewDb();
        return await db.Workers.AsNoTracking().ToDictionaryAsync(w => w.WorkerId, w => w.LastSeenUtc);
    }

    [Fact]
    public async Task WorkersWithDifferentRegistrations_CoexistAndEachOnlyRunsItsOwnJobs()
    {
        await DefineJobAsync("t.only-a");
        await DefineJobAsync("t.only-b");
        await DefineJobAsync("t.nobody");
        foreach (var k in new[] { "t.only-a", "t.only-b", "t.nobody" }) Scripts.Set(k, (_, _) => Task.FromResult(JobResult.Ok()));
        var a = await StartWorkerAsync(["t.only-a"]);
        var b = await StartWorkerAsync(["t.only-b"]); // starting B must not mark A's job unavailable (or vice versa)

        var ra = await EnqueueManualAsync("t.only-a");
        var rb = await EnqueueManualAsync("t.only-b");
        var rn = await EnqueueManualAsync("t.nobody");
        await WaitForStatusAsync(ra, RunStatus.Succeeded);
        await WaitForStatusAsync(rb, RunStatus.Succeeded);
        await Task.Delay(TimeSpan.FromSeconds(3));

        Assert.Equal(a.Identity.Id, (await GetRunAsync(ra)).WorkerId); // never claimed by the worker that cannot run it
        Assert.Equal(b.Identity.Id, (await GetRunAsync(rb)).WorkerId);
        var orphan = await GetRunAsync(rn);                            // waits for a capable worker instead of failing
        Assert.Equal(RunStatus.Queued, orphan.Status);
        Assert.Equal(0, orphan.Attempt);

        await using var db = NewDb();
        var live = await WorkerCapacity.LiveJobKeys(db, DateTime.UtcNow.AddMinutes(-1)).ToListAsync();
        Assert.Equal(new[] { "t.only-a", "t.only-b" }, live.Order());
        Assert.Equal(3, await db.Definitions.CountAsync()); // definitions are kept for every known job
    }

    [Fact]
    public async Task Scheduler_DoesNotEnqueueOccurrences_ForJobsNoLiveWorkerCanRun()
    {
        await DefineJobAsync("t.real");
        await DefineJobAsync("t.ghost");
        Scripts.Set("t.real", (_, _) => Task.FromResult(JobResult.Ok()));
        var w = await StartWorkerAsync(["t.real"]);
        await using (var db = NewDb())
        {
            db.Schedules.Add(new JobSchedule { Id = Guid.NewGuid(), JobKey = "t.ghost", IntervalSeconds = 60, NextDueUtc = DateTime.UtcNow.AddSeconds(-5) });
            await db.SaveChangesAsync();
        }

        // The worker's own scheduler loop may be holding the row (UPDLOCK/READPAST makes a concurrent call skip it), so
        // either dispatcher may be the one that advances the schedule: wait for the outcome instead of assuming who ran.
        await w.Scheduler.DispatchDueAsync(CancellationToken.None);
        await EventuallyAsync(async () =>
        {
            await using var poll = NewDb();
            return (await poll.Schedules.AsNoTracking().SingleAsync(s => s.JobKey == "t.ghost")).NextDueUtc > DateTime.UtcNow;
        }, "the schedule cadence to advance past now");

        await using var check = NewDb();
        Assert.False(await check.Runs.AnyAsync(r => r.JobKey == "t.ghost")); // no backlog for an unrunnable job
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)))
            if (Interlocked.CompareExchange(ref target, value, current) == current) return;
    }
}
