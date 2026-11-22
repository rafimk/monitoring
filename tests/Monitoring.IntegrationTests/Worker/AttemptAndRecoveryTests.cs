using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Monitoring.Contracts;
using Monitoring.Worker.Services;

namespace Monitoring.IntegrationTests.Worker;

/// <summary>Attempt numbering across shutdowns/retries, and lease expiry/recovery/ownership. Real SQL Server, real worker classes.</summary>
[Collection("worker-sql")]
public class AttemptAndRecoveryTests : WorkerTestBase
{
    // ---------------- shutdown ----------------

    [Fact]
    public async Task GracefulShutdown_RequeuesRun_KeepsInterruptedAttemptInHistory_AndRunSucceedsAfterRestart()
    {
        const string key = "t.shutdown";
        await DefineJobAsync(key, maxAttempts: 1); // a single-attempt budget: interruptions must not consume it
        const int interruptions = 2;
        Scripts.Set(key, async (ctx, ct) =>
        {
            if (ctx.Attempt <= interruptions) await Task.Delay(Timeout.Infinite, ct); // runs until the host stops
            return JobResult.Ok();
        });
        var runId = await EnqueueManualAsync(key);

        for (var n = 1; n <= interruptions; n++)
        {
            var worker = await StartWorkerAsync([key]);
            await WaitForRunningAttemptAsync(runId, n);
            await worker.StopAsync();

            var requeued = await GetRunAsync(runId);
            Assert.Equal(RunStatus.Queued, requeued.Status);
            Assert.Equal(n, requeued.Attempt);            // the sequence number is never decremented ...
            Assert.Equal(0, requeued.CountedAttempts);    // ... but the interruption is not charged to the retry budget
            Assert.Null(requeued.WorkerId);
            Assert.Null(requeued.LeaseExpiresUtc);
            Assert.Null(requeued.Error);
        }

        await StartWorkerAsync([key]); // restart: this used to collide with the unique (RunId, Attempt) index
        await WaitForStatusAsync(runId, RunStatus.Succeeded);

        var run = await GetRunAsync(runId);
        Assert.Equal(3, run.Attempt);
        Assert.Equal(1, run.CountedAttempts);

        var attempts = await GetAttemptsAsync(runId);
        Assert.Equal(new[] { 1, 2, 3 }, attempts.Select(a => a.Attempt));
        Assert.Equal(new[] { RunStatus.Cancelled, RunStatus.Cancelled, RunStatus.Succeeded }, attempts.Select(a => a.Status));
        Assert.All(attempts.Take(2), a => Assert.Equal(RunStore.ShutdownMessage, a.Error)); // accurate record of what happened
        Assert.All(attempts, a => Assert.NotNull(a.EndedUtc));
    }

    // ---------------- retry ----------------

    [Fact]
    public async Task Retry_AttemptNumbersAreSequential_BackoffDoubles_AndSecretsAreRedacted()
    {
        const string key = "t.retry";
        await DefineJobAsync(key, maxAttempts: 3, retryBaseSeconds: 1);
        Scripts.Set(key, (ctx, _) => Task.FromResult(ctx.Attempt < 3 ? JobResult.Fail("boom password=hunter2") : JobResult.Ok()));
        await StartWorkerAsync([key]);
        var runId = await EnqueueManualAsync(key);

        await WaitForStatusAsync(runId, RunStatus.Succeeded);

        var run = await GetRunAsync(runId);
        Assert.Equal(3, run.Attempt);
        Assert.Equal(3, run.CountedAttempts);
        var a = await GetAttemptsAsync(runId);
        Assert.Equal(new[] { RunStatus.Failed, RunStatus.Failed, RunStatus.Succeeded }, a.Select(x => x.Status));
        Assert.Contains("***", a[0].Error);
        Assert.DoesNotContain("hunter2", a[0].Error);
        Assert.True(a[1].StartedUtc - a[0].EndedUtc >= TimeSpan.FromSeconds(0.9), "first retry waits the base delay (1s)");
        Assert.True(a[2].StartedUtc - a[1].EndedUtc >= TimeSpan.FromSeconds(1.9), "second retry waits double (2s)");
    }

    [Fact]
    public async Task Retry_WhenBudgetIsSpent_RunEndsFailed_WithExactlyMaxAttempts()
    {
        const string key = "t.exhaust";
        await DefineJobAsync(key, maxAttempts: 2, retryBaseSeconds: 1);
        var executions = 0;
        Scripts.Set(key, (_, _) => { Interlocked.Increment(ref executions); return Task.FromResult(JobResult.Fail("always fails")); });
        await StartWorkerAsync([key]);
        var runId = await EnqueueManualAsync(key);

        await WaitForStatusAsync(runId, RunStatus.Failed);
        await Task.Delay(TimeSpan.FromSeconds(3)); // no further attempts may appear

        var run = await GetRunAsync(runId);
        Assert.Equal(2, run.Attempt);
        Assert.Equal(2, run.CountedAttempts);
        Assert.NotNull(run.EndedUtc);
        Assert.Equal(2, executions);
        Assert.Equal(new[] { RunStatus.Failed, RunStatus.Failed }, (await GetAttemptsAsync(runId)).Select(a => a.Status));
    }

    [Fact]
    public async Task Interruption_DoesNotShortenTheRetryBudget_OfALaterFailure()
    {
        const string key = "t.mixed";
        await DefineJobAsync(key, maxAttempts: 2, retryBaseSeconds: 1);
        // attempt 1: interrupted by shutdown; attempts 2 and 3: fail. Budget is 2 *counted* attempts.
        Scripts.Set(key, async (ctx, ct) =>
        {
            if (ctx.Attempt == 1) await Task.Delay(Timeout.Infinite, ct);
            return JobResult.Fail("nope");
        });
        var runId = await EnqueueManualAsync(key);
        var first = await StartWorkerAsync([key]);
        await WaitForRunningAttemptAsync(runId, 1);
        await first.StopAsync();

        await StartWorkerAsync([key]);
        await WaitForStatusAsync(runId, RunStatus.Failed);

        var run = await GetRunAsync(runId);
        Assert.Equal(3, run.Attempt);           // three attempts were started ...
        Assert.Equal(2, run.CountedAttempts);   // ... but only two are charged to MaxAttempts = 2
        Assert.Equal(new[] { RunStatus.Cancelled, RunStatus.Failed, RunStatus.Failed },
            (await GetAttemptsAsync(runId)).Select(a => a.Status));
    }

    // ---------------- lease expiry / recovery ----------------

    [Fact]
    public async Task ExpiredLease_OfACrashedWorker_IsAbandonedAndRetriedAsTheNextAttemptNumber()
    {
        const string key = "t.crash";
        await DefineJobAsync(key, maxAttempts: 3);
        Scripts.Set(key, (_, _) => Task.FromResult(JobResult.Ok()));
        var runId = await InsertCrashedRunAsync(key, maxAttempts: 3, attemptsAlreadyUsed: 1, leaseExpiredAgo: TimeSpan.FromSeconds(30));

        await StartWorkerAsync([key]);
        await WaitForStatusAsync(runId, RunStatus.Succeeded);

        var run = await GetRunAsync(runId);
        Assert.Equal(2, run.Attempt);
        Assert.Equal(2, run.CountedAttempts); // the crashed attempt may have executed, so it stays charged
        var attempts = await GetAttemptsAsync(runId);
        Assert.Equal(new[] { RunStatus.Abandoned, RunStatus.Succeeded }, attempts.Select(a => a.Status));
        Assert.Equal(RunStore.LeaseExpiredMessage, attempts[0].Error);
        Assert.Equal("crashed-worker-1", attempts[0].WorkerId); // history still names the worker that died
    }

    [Fact]
    public async Task ExpiredLease_WithSpentRetryBudget_EndsAbandoned_AndIsNeverExecutedAgain()
    {
        const string key = "t.crash.final";
        await DefineJobAsync(key, maxAttempts: 1);
        var executions = 0;
        Scripts.Set(key, (_, _) => { Interlocked.Increment(ref executions); return Task.FromResult(JobResult.Ok()); });
        var runId = await InsertCrashedRunAsync(key, maxAttempts: 1, attemptsAlreadyUsed: 1, leaseExpiredAgo: TimeSpan.FromSeconds(30));

        await StartWorkerAsync([key]);
        await WaitForStatusAsync(runId, RunStatus.Abandoned);
        await Task.Delay(TimeSpan.FromSeconds(3));

        var run = await GetRunAsync(runId);
        Assert.Equal(RunStatus.Abandoned, run.Status);
        Assert.NotNull(run.EndedUtc);
        Assert.Equal(0, executions);
        Assert.Single(await GetAttemptsAsync(runId));
    }

    [Fact]
    public async Task UnexpiredLease_IsNotRecovered()
    {
        const string key = "t.alive";
        await DefineJobAsync(key);
        Scripts.Set(key, (_, _) => Task.FromResult(JobResult.Ok()));
        // Owned by another worker, lease still valid for a minute: a healthy worker elsewhere must be left alone.
        var runId = await InsertCrashedRunAsync(key, 3, 1, leaseExpiredAgo: TimeSpan.FromSeconds(-60));

        await StartWorkerAsync([key]);
        await Task.Delay(TimeSpan.FromSeconds(3));

        var run = await GetRunAsync(runId);
        Assert.Equal(RunStatus.Running, run.Status);
        Assert.Equal("crashed-worker-1", run.WorkerId);
    }

    // ---------------- ownership / fencing ----------------

    [Fact]
    public async Task StaleAttempt_CannotRenewOrComplete_EvenWhenTheSameWorkerOwnsALaterAttempt()
    {
        const string key = "t.fence";
        await DefineJobAsync(key);
        var runId = await EnqueueManualAsync(key);
        var (store, identity) = CreateBareStore("fence", key);

        var attempt1 = (await store.ClaimAsync(1, CancellationToken.None)).Single();
        Assert.Equal(1, attempt1.Attempt);

        // Attempt 1's lease lapses and the run is recovered, then the SAME worker claims it again as attempt 2.
        await using (var db = NewDb())
            await db.Runs.Where(r => r.Id == runId).ExecuteUpdateAsync(s => s.SetProperty(r => r.LeaseExpiresUtc, DateTime.UtcNow.AddMinutes(-1)));
        Assert.Equal(1, await store.RecoverExpiredAsync(DateTime.UtcNow, CancellationToken.None));
        var attempt2 = (await store.ClaimAsync(1, CancellationToken.None)).Single();
        Assert.Equal(2, attempt2.Attempt);

        // The zombie attempt 1 is fenced out of every write.
        Assert.False(await store.RenewLeaseAsync(attempt1, DateTime.UtcNow.AddMinutes(5), CancellationToken.None));
        Assert.False(await store.CompleteAsync(attempt1, Completion.Final, RunStatus.Succeeded, null, DateTime.UtcNow));
        var mid = await GetRunAsync(runId);
        Assert.Equal(RunStatus.Running, mid.Status);
        Assert.Equal(2, mid.Attempt);
        Assert.Equal(identity.Id, mid.WorkerId);

        // A different worker cannot finish it either.
        var (other, _) = CreateBareStore("intruder", key);
        Assert.False(await other.CompleteAsync(attempt2, Completion.Final, RunStatus.Succeeded, null, DateTime.UtcNow));

        // The rightful owner can.
        Assert.True(await store.CompleteAsync(attempt2, Completion.Final, RunStatus.Succeeded, null, DateTime.UtcNow));
        var done = await GetRunAsync(runId);
        Assert.Equal(RunStatus.Succeeded, done.Status);
        Assert.Equal(new[] { RunStatus.Abandoned, RunStatus.Succeeded }, (await GetAttemptsAsync(runId)).Select(a => a.Status));
    }

    [Fact]
    public async Task OwnershipLoss_IsDetectedByRenewal_JobIsCancelled_AndItsLateResultIsNotPersisted()
    {
        const string key = "t.lost";
        await DefineJobAsync(key);
        var cancellationSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Scripts.Set(key, async (ctx, ct) =>
        {
            if (ctx.Attempt > 1) return JobResult.Ok();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { cancellationSeen.TrySetResult(); }
            return JobResult.Ok("late result from a worker that no longer owns the run"); // a job that ignores cancellation
        });
        await StartWorkerAsync([key], FastOptions(lease: 6, heartbeat: 1));
        var runId = await EnqueueManualAsync(key);
        await WaitForRunningAttemptAsync(runId, 1);

        // Someone else recovers the run (exactly what RecoverExpiredAsync does), in one transaction.
        await using (var db = NewDb())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE JobRuns SET Status = 'Queued', WorkerId = NULL, LeaseExpiresUtc = NULL WHERE Id = {runId} AND Status = 'Running'");
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE RunAttempts SET Status = 'Abandoned', EndedUtc = SYSUTCDATETIME() WHERE RunId = {runId} AND Attempt = 1");
            await tx.CommitAsync();
        }

        await cancellationSeen.Task.WaitAsync(TimeSpan.FromSeconds(15)); // the next renewal tick finds the ownership gone
        await WaitForStatusAsync(runId, RunStatus.Succeeded);

        var run = await GetRunAsync(runId);
        Assert.Equal(2, run.Attempt); // completed by the legitimate attempt 2, not overwritten by the stale attempt 1
        var attempts = await GetAttemptsAsync(runId);
        Assert.Equal(new[] { RunStatus.Abandoned, RunStatus.Succeeded }, attempts.Select(a => a.Status));
    }

    [Fact]
    public async Task RenewalOutage_CancelsTheJobBeforeTheLeaseExpires_AndTheStaleResultIsNeverPersisted()
    {
        const string key = "t.renewfail";
        await DefineJobAsync(key);
        FaultyRunStore? faulty = null;
        DateTime? cancelledAt = null;
        var cancellationSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Scripts.Set(key, async (ctx, ct) =>
        {
            if (ctx.Attempt > 1) return JobResult.Ok();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { cancelledAt = DateTime.UtcNow; cancellationSeen.TrySetResult(); }
            return JobResult.Ok("late"); // misbehaving job returns success after being cancelled
        });

        // lease 4s, heartbeat 1s -> safety margin 1s: the job must be stopped no later than (last confirmed expiry - 1s).
        await StartWorkerAsync([key], FastOptions(lease: 4, heartbeat: 1),
            sp => faulty = new FaultyRunStore(sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<Monitoring.Application.Jobs.JobRegistry>(),
                sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Monitoring.Worker.WorkerOptions>>(), sp.GetRequiredService<Monitoring.Worker.WorkerIdentity>()));
        var runId = await EnqueueManualAsync(key);
        await WaitForRunningAttemptAsync(runId, 1);
        await EventuallyAsync(() => Task.FromResult(faulty!.LastConfirmedExpiry != default), "at least one successful lease renewal");

        faulty!.FailRenewals = true; // the database "goes away" for lease renewals only

        await cancellationSeen.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var confirmed = faulty.LastConfirmedExpiry;
        Assert.True(cancelledAt < confirmed, $"job was cancelled at {cancelledAt:O}, after the lease could already be recovered ({confirmed:O})");

        // Right after cancellation the run is still owned by attempt 1 and the job's "late" success was NOT written.
        var during = await GetRunAsync(runId);
        Assert.Equal(RunStatus.Running, during.Status);
        Assert.Equal(1, during.Attempt);

        await WaitForStatusAsync(runId, RunStatus.Succeeded, timeoutSeconds: 40); // lease expires -> recovered -> attempt 2 runs
        var run = await GetRunAsync(runId);
        Assert.Equal(2, run.Attempt);
        Assert.Equal(new[] { RunStatus.Abandoned, RunStatus.Succeeded }, (await GetAttemptsAsync(runId)).Select(a => a.Status));
    }

    private sealed class FaultyRunStore(
        IServiceScopeFactory scopes, Monitoring.Application.Jobs.JobRegistry registry,
        Microsoft.Extensions.Options.IOptions<Monitoring.Worker.WorkerOptions> options, Monitoring.Worker.WorkerIdentity identity)
        : RunStore(scopes, registry, options, identity)
    {
        public volatile bool FailRenewals;
        public DateTime LastConfirmedExpiry;

        public override async Task<bool> RenewLeaseAsync(ClaimedRun run, DateTime newExpiryUtc, CancellationToken ct)
        {
            if (FailRenewals) throw new TimeoutException("simulated database outage");
            var ok = await base.RenewLeaseAsync(run, newExpiryUtc, ct);
            if (ok) LastConfirmedExpiry = newExpiryUtc;
            return ok;
        }
    }
}
