using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monitoring.Contracts;
using Monitoring.Infrastructure.Persistence;
using Monitoring.Worker;
using Monitoring.Worker.Services;

namespace Monitoring.IntegrationTests.Worker;

/// <summary>
/// Nightly retention against a real, throwaway SQL Server database: the stored procedure (scope, boundaries, batching,
/// idempotence), the service (schedule, persistence, failure, leadership, locking) and a two-node leader/standby run.
/// </summary>
public class RetentionTests : WorkerTestBase
{
    private static readonly DateTime Now = new(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly RunStatus[] Terminal =
        [RunStatus.Succeeded, RunStatus.Failed, RunStatus.Cancelled, RunStatus.TimedOut, RunStatus.Abandoned];

    // ---------------------------------------------------------------- data helpers

    private async Task<Guid> AddRunAsync(RunStatus status, DateTime? ended, int attempts = 2, DateTime? scheduled = null, DateTime? queued = null)
    {
        await using var db = NewDb();
        var s = scheduled ?? ended ?? Now.AddDays(-1);
        var run = new JobRun
        {
            Id = Guid.NewGuid(), JobKey = "ret.job", Trigger = TriggerSource.Manual, Status = status,
            ScheduledUtc = s, QueuedUtc = queued ?? s, NotBeforeUtc = s, StartedUtc = ended?.AddSeconds(-5), EndedUtc = ended,
            Attempt = attempts, CountedAttempts = attempts, MaxAttempts = 3, CorrelationId = Guid.NewGuid().ToString("N"),
        };
        db.Runs.Add(run);
        for (var i = 1; i <= attempts; i++)
            db.Attempts.Add(new RunAttempt
            { Id = Guid.NewGuid(), RunId = run.Id, Attempt = i, Status = i == attempts ? status : RunStatus.Failed, StartedUtc = s, WorkerId = "w" });
        await db.SaveChangesAsync();
        return run.Id;
    }

    private async Task AddAuditAsync(DateTime utc, int count = 1)
    {
        await using var db = NewDb();
        for (var i = 0; i < count; i++) db.Audit.Add(new AuditEntry { Utc = utc, Actor = "a", Action = "x", Target = "t" });
        await db.SaveChangesAsync();
    }

    private async Task AddFeedAsync(DateTime created, DateTime? dispatched, int count = 1)
    {
        await using var db = NewDb();
        for (var i = 0; i < count; i++)
            db.ChangeEvents.Add(new ChangeEvent { Kind = ChangeKind.Run, JobKey = "ret.job", Reason = "queued", CreatedUtc = created, DispatchedUtc = dispatched });
        await db.SaveChangesAsync();
    }

    private async Task<int> RowsAsync(string table, string where = "1=1")
    {
        await using var c = new SqlConnection(ConnectionString);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM dbo.{table} WHERE {where}";
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task ExecAsync(string sql)
    {
        await using var c = new SqlConnection(ConnectionString);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private sealed record Counts(Dictionary<string, int> Deleted, Dictionary<string, bool> More)
    {
        public int this[string entity] => Deleted[entity];
    }

    /// <summary>Calls the stored procedure exactly like an operator would.</summary>
    private async Task<Counts> RunProcAsync(int jobDays = 30, int auditDays = 365, int feedDays = 7, int batch = 1000, int? maxBatches = null, DateTime? now = null)
    {
        await using var c = new SqlConnection(ConnectionString);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "dbo.usp_Monitoring_ApplyRetention_v1";
        cmd.CommandType = System.Data.CommandType.StoredProcedure;
        cmd.Parameters.AddWithValue("@JobHistoryRetentionDays", jobDays);
        cmd.Parameters.AddWithValue("@AuditRetentionDays", auditDays);
        cmd.Parameters.AddWithValue("@ChangeFeedRetentionDays", feedDays);
        cmd.Parameters.AddWithValue("@BatchSize", batch);
        cmd.Parameters.AddWithValue("@MaxBatches", (object?)maxBatches ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@NowUtc", now ?? Now);
        var deleted = new Dictionary<string, int>();
        var more = new Dictionary<string, bool>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) { deleted[r.GetString(0)] = r.GetInt32(1); more[r.GetString(0)] = Convert.ToInt32(r.GetValue(2)) == 1; }
        return new Counts(deleted, more);
    }

    // ---------------------------------------------------------------- stored procedure: job history

    [Fact]
    public async Task Procedure_IsCreatedByTheMigration()
    {
        Assert.Equal(1, await RowsAsync("sysobjects", "name = 'usp_Monitoring_ApplyRetention_v1' AND xtype = 'P'"));
    }

    [Fact]
    public async Task JobHistory_RemovesOldTerminalRunsAndTheirAttempts_KeepsNewerOnes()
    {
        foreach (var s in Terminal) await AddRunAsync(s, Now.AddDays(-31));   // old: one per terminal status
        foreach (var s in Terminal) await AddRunAsync(s, Now.AddDays(-29));   // recent

        var r = await RunProcAsync();

        Assert.Equal(5, r["JobRuns"]);
        Assert.Equal(10, r["RunAttempts"]);
        Assert.Equal(5, await RowsAsync("JobRuns"));
        Assert.Equal(10, await RowsAsync("RunAttempts"));
        Assert.Equal(0, await RowsAsync("JobRuns", $"EndedUtc < '{Now.AddDays(-30):yyyy-MM-dd HH:mm:ss}'"));
        Assert.Equal(0, await RowsAsync("RunAttempts", "RunId NOT IN (SELECT Id FROM dbo.JobRuns)"));
    }

    [Fact]
    public async Task JobHistory_UsesEndedUtc_NotScheduledOrQueued()
    {
        // Scheduled/queued long ago, but only just finished: must stay.
        var recentEnd = await AddRunAsync(RunStatus.Succeeded, Now.AddDays(-1), scheduled: Now.AddDays(-90), queued: Now.AddDays(-90));
        // Scheduled/queued yesterday, but ended 45 days ago (clock skew / backfilled data): must go.
        var oldEnd = await AddRunAsync(RunStatus.Failed, Now.AddDays(-45), scheduled: Now.AddDays(-1), queued: Now.AddDays(-1));

        await RunProcAsync();

        await using var db = NewDb();
        Assert.True(await db.Runs.AnyAsync(x => x.Id == recentEnd));
        Assert.False(await db.Runs.AnyAsync(x => x.Id == oldEnd));
    }

    [Fact]
    public async Task JobHistory_NeverDeletesQueuedOrRunningRuns_EvenWhenAncient()
    {
        var queued = await AddRunAsync(RunStatus.Queued, null, attempts: 0, scheduled: Now.AddDays(-400));
        var running = await AddRunAsync(RunStatus.Running, null, attempts: 1, scheduled: Now.AddDays(-400));
        // A pathological row: active status but an old EndedUtc is still not a terminal run.
        var runningOddEnd = await AddRunAsync(RunStatus.Running, Now.AddDays(-400), attempts: 1);
        var queuedOddEnd = await AddRunAsync(RunStatus.Queued, Now.AddDays(-400), attempts: 0);
        // A terminal status without an end time cannot be aged, so it stays.
        var noEnd = await AddRunAsync(RunStatus.Failed, null, attempts: 1, scheduled: Now.AddDays(-400));

        var r = await RunProcAsync(jobDays: 1);

        Assert.Equal(0, r["JobRuns"]);
        Assert.Equal(0, r["RunAttempts"]);
        await using var db = NewDb();
        foreach (var id in new[] { queued, running, runningOddEnd, queuedOddEnd, noEnd })
            Assert.True(await db.Runs.AnyAsync(x => x.Id == id), $"run {id} must remain");
        Assert.Equal(3, await db.Attempts.CountAsync()); // running(1) + runningOddEnd(1) + noEnd(1)
    }

    [Fact]
    public async Task JobHistory_CutoffBoundary_IsStrict()
    {
        var cutoff = Now.AddDays(-30);
        var atCutoff = await AddRunAsync(RunStatus.Succeeded, cutoff);
        var justAfter = await AddRunAsync(RunStatus.Succeeded, cutoff.AddSeconds(1));
        var justBefore = await AddRunAsync(RunStatus.Succeeded, cutoff.AddSeconds(-1));

        await RunProcAsync();

        await using var db = NewDb();
        Assert.True(await db.Runs.AnyAsync(x => x.Id == atCutoff), "exactly at the cutoff is retained");
        Assert.True(await db.Runs.AnyAsync(x => x.Id == justAfter));
        Assert.False(await db.Runs.AnyAsync(x => x.Id == justBefore), "one second older than the cutoff is deleted");
    }

    [Fact]
    public async Task JobHistory_Batching_DeletesInBoundedSlices_AndReportsRemainingWork()
    {
        for (var i = 0; i < 25; i++) await AddRunAsync(RunStatus.Succeeded, Now.AddDays(-40 - i), attempts: 1);

        var first = await RunProcAsync(batch: 10, maxBatches: 1);
        Assert.Equal(10, first["JobRuns"]);
        Assert.True(first.More["JobRuns"]);
        Assert.Equal(15, await RowsAsync("JobRuns"));

        var second = await RunProcAsync(batch: 10, maxBatches: 1);
        Assert.Equal(10, second["JobRuns"]);
        Assert.True(second.More["JobRuns"]);

        var third = await RunProcAsync(batch: 10, maxBatches: 1);
        Assert.Equal(5, third["JobRuns"]);
        Assert.False(third.More["JobRuns"]);
        Assert.Equal(0, await RowsAsync("JobRuns"));
        Assert.Equal(0, await RowsAsync("RunAttempts"));
    }

    [Fact]
    public async Task Batching_WithoutALimit_DrainsEverythingInSmallTransactions()
    {
        for (var i = 0; i < 37; i++) await AddRunAsync(RunStatus.Succeeded, Now.AddDays(-40), attempts: 1);
        await AddAuditAsync(Now.AddDays(-500), 23);
        await AddFeedAsync(Now.AddDays(-20), Now.AddDays(-20), 19);

        var r = await RunProcAsync(batch: 5, maxBatches: null);

        Assert.Equal(37, r["JobRuns"]);
        Assert.Equal(37, r["RunAttempts"]);
        Assert.Equal(23, r["AuditEntries"]);
        Assert.Equal(19, r["ChangeEvents"]);
        Assert.All(r.More.Values, more => Assert.False(more));
    }

    [Fact]
    public async Task RepeatedCalls_AreSafe_AndDeleteNothingTheSecondTime()
    {
        await AddRunAsync(RunStatus.Succeeded, Now.AddDays(-40));
        await AddAuditAsync(Now.AddDays(-500));
        await AddFeedAsync(Now.AddDays(-20), Now.AddDays(-20));

        var first = await RunProcAsync();
        var second = await RunProcAsync();
        var third = await RunProcAsync();

        Assert.Equal(1, first["JobRuns"]);
        foreach (var again in new[] { second, third })
            Assert.All(again.Deleted.Values, n => Assert.Equal(0, n));
    }

    [Fact]
    public async Task Procedure_RejectsUnsafeParameters_AndDeletesNothing()
    {
        await AddRunAsync(RunStatus.Succeeded, Now.AddDays(-400));
        await Assert.ThrowsAsync<SqlException>(() => RunProcAsync(jobDays: 0));      // would delete everything
        await Assert.ThrowsAsync<SqlException>(() => RunProcAsync(auditDays: -5));
        await Assert.ThrowsAsync<SqlException>(() => RunProcAsync(feedDays: 0));
        await Assert.ThrowsAsync<SqlException>(() => RunProcAsync(batch: 0));
        await Assert.ThrowsAsync<SqlException>(() => RunProcAsync(batch: 10001));
        await Assert.ThrowsAsync<SqlException>(() => RunProcAsync(maxBatches: 0));
        Assert.Equal(1, await RowsAsync("JobRuns"));
    }

    // ---------------------------------------------------------------- stored procedure: independent policies

    [Fact]
    public async Task AuditPolicy_IsIndependentOfJobHistory()
    {
        await AddAuditAsync(Now.AddDays(-400));           // older than 365: deleted
        await AddAuditAsync(Now.AddDays(-100));           // older than the 30-day job policy, newer than audit: kept
        await AddAuditAsync(Now.AddDays(-365));           // exactly at the cutoff: kept
        await AddAuditAsync(Now.AddDays(-365).AddSeconds(-1)); // just past it: deleted

        var r = await RunProcAsync(jobDays: 30, auditDays: 365);

        Assert.Equal(2, r["AuditEntries"]);
        Assert.Equal(2, await RowsAsync("AuditEntries"));
        Assert.Equal(0, await RowsAsync("AuditEntries", $"Utc < '{Now.AddDays(-365):yyyy-MM-dd HH:mm:ss}'"));
    }

    [Fact]
    public async Task AuditPolicy_CanBeShorterOrLongerThanJobHistory_WithoutAffectingRuns()
    {
        await AddAuditAsync(Now.AddDays(-5));
        var run = await AddRunAsync(RunStatus.Succeeded, Now.AddDays(-5));

        var r = await RunProcAsync(jobDays: 30, auditDays: 2);

        Assert.Equal(1, r["AuditEntries"]);
        Assert.Equal(0, r["JobRuns"]);
        await using var db = NewDb();
        Assert.True(await db.Runs.AnyAsync(x => x.Id == run));
    }

    [Fact]
    public async Task ChangeFeedPolicy_DeletesOnlyDispatchedRows_PendingRowsAreNeverTouched()
    {
        await AddFeedAsync(Now.AddDays(-30), Now.AddDays(-30), 3);   // dispatched long ago: deleted
        await AddFeedAsync(Now.AddDays(-30), Now.AddDays(-2), 2);    // dispatched recently (old row): kept
        await AddFeedAsync(Now.AddDays(-30), null, 4);               // never dispatched, a month old: kept
        await AddFeedAsync(Now.AddDays(-3000), null, 1);             // never dispatched, ancient: kept
        await AddFeedAsync(Now.AddMinutes(-1), null, 2);             // fresh pending: kept

        var r = await RunProcAsync(feedDays: 7);

        Assert.Equal(3, r["ChangeEvents"]);
        Assert.Equal(9, await RowsAsync("ChangeEvents"));
        Assert.Equal(7, await RowsAsync("ChangeEvents", "DispatchedUtc IS NULL"));
        // And the policy value alone must not matter for pending rows: shrink it to the minimum.
        var minimal = await RunProcAsync(feedDays: 1);
        Assert.Equal(2, minimal["ChangeEvents"]);
        Assert.Equal(7, await RowsAsync("ChangeEvents", "DispatchedUtc IS NULL"));
    }

    [Fact]
    public async Task ChangeFeedPolicy_BoundaryIsStrict_AndUsesDispatchTime()
    {
        var cutoff = Now.AddDays(-7);
        await AddFeedAsync(Now.AddDays(-1), cutoff, 1);                    // created recently, dispatched exactly at cutoff: kept
        await AddFeedAsync(Now.AddDays(-1), cutoff.AddSeconds(-1), 1);     // created recently, dispatched before cutoff: deleted
        await AddFeedAsync(Now.AddDays(-60), cutoff.AddSeconds(1), 1);     // created long ago, dispatched after cutoff: kept

        var r = await RunProcAsync();

        Assert.Equal(1, r["ChangeEvents"]);
        Assert.Equal(2, await RowsAsync("ChangeEvents"));
    }

    [Fact]
    public async Task Cleanup_NeverTouchesSchedulesDefinitionsWorkersOrCapabilities()
    {
        await DefineJobAsync("ret.job");
        await using (var db = NewDb())
        {
            db.Schedules.Add(new JobSchedule { Id = Guid.NewGuid(), JobKey = "ret.job", IntervalSeconds = 60, NextDueUtc = Now.AddDays(-900), Enabled = true });
            db.Workers.Add(new WorkerHeartbeat { WorkerId = "old-worker", Host = "h", StartedUtc = Now.AddDays(-900), LastSeenUtc = Now.AddDays(-900) });
            db.WorkerJobs.Add(new WorkerJob { WorkerId = "old-worker", JobKey = "ret.job" });
            await db.SaveChangesAsync();
        }
        await ExecAsync("UPDATE dbo.JobDefinitions SET CreatedUtc='2020-01-01', UpdatedUtc='2020-01-01'");
        await AddRunAsync(RunStatus.Succeeded, Now.AddDays(-900));

        var r = await RunProcAsync(jobDays: 1, auditDays: 1, feedDays: 1);

        Assert.Equal(1, r["JobRuns"]);
        Assert.Equal(1, await RowsAsync("JobDefinitions"));
        Assert.Equal(1, await RowsAsync("JobSchedules"));
        Assert.Equal(1, await RowsAsync("WorkerHeartbeats"));
        Assert.Equal(1, await RowsAsync("WorkerJobs"));
    }

    // ---------------------------------------------------------------- service

    private sealed class FakeClock(DateTime utc) : TimeProvider
    {
        public DateTime Utc { get; set; } = utc;
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(Utc, DateTimeKind.Utc));
    }

    private sealed class Rig : IAsyncDisposable
    {
        public required ServiceProvider Provider { get; init; }
        public required RetentionService Service { get; init; }
        public required Tenure Tenure { get; init; }
        public required CapturingLoggerProvider Logs { get; init; }
        public required FakeClock Clock { get; init; }
        public int LeadershipChecks;
        public ValueTask DisposeAsync() => Provider.DisposeAsync();
    }

    private Rig NewRig(Action<RetentionOptions>? configure = null, Func<int, bool>? leader = null, DateTime? now = null)
    {
        var retention = new RetentionOptions { BatchPauseMilliseconds = 0, BatchSize = 1000, RunAt = "02:00", TimeZoneId = "UTC" };
        configure?.Invoke(retention);
        var logs = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Debug).AddProvider(logs));
        services.AddDbContext<MonitoringDbContext>(o => o.UseSqlServer(ConnectionString), ServiceLifetime.Scoped);
        var sp = services.BuildServiceProvider();
        var tenure = new Tenure();
        var clock = new FakeClock(now ?? Now);
        var rig = new Rig { Provider = sp, Tenure = tenure, Logs = logs, Clock = clock, Service = null! };
        var checks = 0;
        LeadershipCheck proof = _ => Task.FromResult(leader?.Invoke(Interlocked.Increment(ref checks)) ?? true);
        var service = new RetentionService(
            sp.GetRequiredService<IServiceScopeFactory>(), Options.Create(new WorkerOptions { Retention = retention }),
            tenure, proof, clock, sp.GetRequiredService<ILogger<RetentionService>>());
        return new Rig { Provider = sp, Tenure = tenure, Logs = logs, Clock = clock, Service = service };
    }

    private async Task SeedStateAsync(DateTime initialized, DateTime? lastSuccess)
    {
        await using var db = NewDb();
        db.Maintenance.Add(new MaintenanceState { Task = RetentionService.TaskName, InitializedUtc = initialized, LastSuccessUtc = lastSuccess });
        await db.SaveChangesAsync();
    }

    private async Task<MaintenanceState?> StateAsync()
    {
        await using var db = NewDb();
        return await db.Maintenance.AsNoTracking().FirstOrDefaultAsync(m => m.Task == RetentionService.TaskName);
    }

    [Fact]
    public async Task Service_FreshInstall_DoesNotDeleteBeforeTheFirstScheduledNight()
    {
        await AddRunAsync(RunStatus.Succeeded, Now.AddDays(-90));
        await using var rig = NewRig();

        var wait = await rig.Service.TickAsync(CancellationToken.None);

        Assert.NotNull(wait);
        Assert.Null(rig.Service.LastOutcome);
        Assert.Equal(1, await RowsAsync("JobRuns"));
        Assert.NotNull(await StateAsync()); // baseline recorded
    }

    [Fact]
    public async Task Service_RunsWhenAScheduledNightIsOutstanding_PersistsSuccess_AndReportsCounts()
    {
        await SeedStateAsync(Now.AddDays(-5), Now.AddDays(-1.5));   // last success before today's 02:00 occurrence
        await AddRunAsync(RunStatus.Succeeded, Now.AddDays(-40));
        await AddRunAsync(RunStatus.Succeeded, Now.AddDays(-2));
        await AddAuditAsync(Now.AddDays(-400));
        await AddFeedAsync(Now.AddDays(-9), Now.AddDays(-9));
        await using var rig = NewRig();

        var wait = await rig.Service.TickAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(1), wait);
        Assert.Equal(RetentionOutcome.Completed, rig.Service.LastOutcome);
        Assert.Equal(1, rig.Service.LastDeleted["JobRuns"]);
        Assert.Equal(1, await RowsAsync("JobRuns"));
        Assert.Equal(0, await RowsAsync("AuditEntries"));
        Assert.Equal(0, await RowsAsync("ChangeEvents"));
        var state = await StateAsync();
        Assert.Equal(Now, state!.LastSuccessUtc);
        Assert.Contains("JobRuns=1", state.LastSummary);
        Assert.Contains(rig.Logs.Entries, e => e.Message.StartsWith("Retention started"));
        Assert.Contains(rig.Logs.Entries, e => e.Message.StartsWith("Retention completed") && e.Message.Contains("JobRuns=1"));
    }

    [Fact]
    public async Task Service_RestartNearTheScheduledTime_DoesNotRepeatTheWork()
    {
        await SeedStateAsync(Now.AddDays(-5), Now.AddDays(-1.5));
        await AddRunAsync(RunStatus.Succeeded, Now.AddDays(-40));
        await using (var first = NewRig()) await first.Service.TickAsync(CancellationToken.None);
        Assert.Equal(0, await RowsAsync("JobRuns"));

        // New process, minutes later, same night: more old data has appeared, but tonight's run is already done.
        await AddRunAsync(RunStatus.Succeeded, Now.AddDays(-40));
        await using var restarted = NewRig(now: Now.AddMinutes(7));
        var wait = await restarted.Service.TickAsync(CancellationToken.None);

        Assert.Null(restarted.Service.LastOutcome);
        Assert.Equal(1, await RowsAsync("JobRuns"));
        Assert.True(wait > TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Service_WorkerDownOvernight_RunsExactlyOneCatchUp_ThenWaitsForTheNextNight()
    {
        await SeedStateAsync(Now.AddDays(-10), Now.AddDays(-4));    // three scheduled nights missed
        await AddRunAsync(RunStatus.Succeeded, Now.AddDays(-40));
        await using var rig = NewRig();

        await rig.Service.TickAsync(CancellationToken.None);
        Assert.Equal(RetentionOutcome.Completed, rig.Service.LastOutcome);
        Assert.Contains(rig.Logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("did not happen"));

        await AddRunAsync(RunStatus.Succeeded, Now.AddDays(-40));
        await rig.Service.TickAsync(CancellationToken.None);   // same day again: nothing outstanding
        Assert.Equal(1, await RowsAsync("JobRuns"));

        rig.Clock.Utc = Now.AddDays(1);                       // next night's 02:00 has passed
        await rig.Service.TickAsync(CancellationToken.None);
        Assert.Equal(0, await RowsAsync("JobRuns"));
    }

    [Fact]
    public async Task Service_FailureIsContained_RetriedWithBackoff_AndRecovers()
    {
        await SeedStateAsync(Now.AddDays(-5), Now.AddDays(-2));
        var run = await AddRunAsync(RunStatus.Succeeded, Now.AddDays(-40));
        await ExecAsync("EXEC sp_rename 'dbo.usp_Monitoring_ApplyRetention_v1', 'usp_Hidden'");
        await using var rig = NewRig();

        var wait = await rig.Service.TickAsync(CancellationToken.None);   // must not throw

        Assert.Equal(RetentionOutcome.Failed, rig.Service.LastOutcome);
        Assert.True(wait >= TimeSpan.FromSeconds(20), $"retry delay {wait} must not be a rapid loop");
        Assert.Contains(rig.Logs.Entries, e => e.Level == LogLevel.Error && e.Message.StartsWith("Retention failed"));
        Assert.Equal(1, await RowsAsync("JobRuns"));
        Assert.Equal(Now.AddDays(-2), (await StateAsync())!.LastSuccessUtc);   // failure is not recorded as success

        var second = await rig.Service.TickAsync(CancellationToken.None);
        Assert.True(second > wait * 0.9, "backoff grows after repeated failures");

        await ExecAsync("EXEC sp_rename 'dbo.usp_Hidden', 'usp_Monitoring_ApplyRetention_v1'");
        await rig.Service.TickAsync(CancellationToken.None);
        Assert.Equal(RetentionOutcome.Completed, rig.Service.LastOutcome);
        Assert.Equal(0, await RowsAsync("JobRuns"));
    }

    [Fact]
    public async Task Service_WhenLeadershipCannotBeProven_DoesNothingAndStops()
    {
        await SeedStateAsync(Now.AddDays(-5), Now.AddDays(-2));
        await AddRunAsync(RunStatus.Succeeded, Now.AddDays(-40));
        await using var rig = NewRig(leader: _ => false);

        var wait = await rig.Service.TickAsync(CancellationToken.None);

        Assert.Null(wait);   // stop signal
        Assert.Equal(RetentionOutcome.LeadershipLost, rig.Service.LastOutcome);
        Assert.Equal(1, await RowsAsync("JobRuns"));
        Assert.Equal(Now.AddDays(-2), (await StateAsync())!.LastSuccessUtc);
    }

    [Fact]
    public async Task Service_LeadershipLostMidCleanup_StopsAtTheNextBatchBoundary()
    {
        await SeedStateAsync(Now.AddDays(-5), Now.AddDays(-2));
        for (var i = 0; i < 50; i++) await AddRunAsync(RunStatus.Succeeded, Now.AddDays(-40), attempts: 1);
        // Check 1 = before starting, 2 and 3 = before the first two batches, 4 = lost.
        await using var rig = NewRig(o => o.BatchSize = 10, leader: n => n <= 3);

        var wait = await rig.Service.TickAsync(CancellationToken.None);

        Assert.Null(wait);
        var remaining = await RowsAsync("JobRuns");
        Assert.InRange(remaining, 1, 49);
        Assert.Equal(0, await RowsAsync("RunAttempts", "RunId NOT IN (SELECT Id FROM dbo.JobRuns)"));   // no half-deleted runs
        Assert.Equal(Now.AddDays(-2), (await StateAsync())!.LastSuccessUtc);
    }

    [Fact]
    public async Task Service_TenureLostMidCleanup_CancelsTheRun()
    {
        await SeedStateAsync(Now.AddDays(-5), Now.AddDays(-2));
        await AddRunAsync(RunStatus.Succeeded, Now.AddDays(-40));
        await using var rig = NewRig();
        rig.Tenure.MarkLost();

        var outcome = await rig.Service.RunOnceAsync(CancellationToken.None);

        Assert.Equal(RetentionOutcome.LeadershipLost, outcome);
        Assert.Equal(1, await RowsAsync("JobRuns"));
    }

    [Fact]
    public async Task Service_CleanupLockHeldElsewhere_SkipsWithoutDeleting()
    {
        await SeedStateAsync(Now.AddDays(-5), Now.AddDays(-2));
        await AddRunAsync(RunStatus.Succeeded, Now.AddDays(-40));
        await using var other = new LeaderLock(ConnectionString, RetentionService.LockResource, "other-node");
        Assert.True(await other.TryAcquireAsync(CancellationToken.None));
        await using var rig = NewRig();

        var wait = await rig.Service.TickAsync(CancellationToken.None);

        Assert.Equal(RetentionOutcome.Skipped, rig.Service.LastOutcome);
        Assert.NotNull(wait);
        Assert.Equal(1, await RowsAsync("JobRuns"));
        Assert.Equal(Now.AddDays(-2), (await StateAsync())!.LastSuccessUtc);

        await other.ReleaseAsync();
        await rig.Service.TickAsync(CancellationToken.None);
        Assert.Equal(RetentionOutcome.Completed, rig.Service.LastOutcome);
        Assert.Equal(0, await RowsAsync("JobRuns"));
    }

    [Fact]
    public async Task Service_Disabled_NeverRuns()
    {
        await SeedStateAsync(Now.AddDays(-5), Now.AddDays(-2));
        await AddRunAsync(RunStatus.Succeeded, Now.AddDays(-40));
        await using var rig = NewRig(o => o.Enabled = false);

        await rig.Service.TickAsync(CancellationToken.None);

        Assert.Null(rig.Service.LastOutcome);
        Assert.Equal(1, await RowsAsync("JobRuns"));
    }

    [Fact]
    public async Task Service_PoliciesAreAppliedFromOptions_Independently()
    {
        await SeedStateAsync(Now.AddDays(-5), Now.AddDays(-2));
        await AddRunAsync(RunStatus.Succeeded, Now.AddDays(-10));        // job: 5 days -> deleted
        await AddAuditAsync(Now.AddDays(-10));                           // audit: 60 days -> kept
        await AddFeedAsync(Now.AddDays(-10), Now.AddDays(-10));          // feed: 20 days -> kept
        await AddFeedAsync(Now.AddDays(-100), null);                     // pending: always kept
        await using var rig = NewRig(o => { o.JobHistoryRetentionDays = 5; o.AuditRetentionDays = 60; o.ChangeFeedRetentionDays = 20; });

        await rig.Service.TickAsync(CancellationToken.None);

        Assert.Equal(0, await RowsAsync("JobRuns"));
        Assert.Equal(1, await RowsAsync("AuditEntries"));
        Assert.Equal(2, await RowsAsync("ChangeEvents"));
    }

    // ---------------------------------------------------------------- two nodes, leader / standby

    [Fact]
    public async Task TwoNodes_OnlyTheActiveLeaderCleans_AStandbyNeverDoes_AndTakeoverDoesNotRepeat()
    {
        var now = DateTime.UtcNow;
        await SeedStateAsync(now.AddDays(-5), now.AddDays(-3));          // a scheduled night is outstanding
        await using (var db = NewDb())
        {
            for (var i = 0; i < 20; i++) await AddRunAsync(RunStatus.Succeeded, now.AddDays(-40), attempts: 1);
        }

        var a = await StartNodeAsync([], LeaderOptions("node-a"));
        await EventuallyAsync(() => Task.FromResult(a.Coordinator.Role == WorkerRole.Active), "node A becomes active");
        var b = await StartNodeAsync([], LeaderOptions("node-b"));
        await EventuallyAsync(() => Task.FromResult(b.Coordinator.Role == WorkerRole.Standby), "node B is standby");

        await EventuallyAsync(async () => await RowsAsync("JobRuns") == 0, "the leader removes the old runs");
        await EventuallyAsync(async () => (await StateAsync())!.LastSuccessUtc > now.AddMinutes(-1), "success is persisted");

        Assert.DoesNotContain(b.Logs.Entries, e => e.Category.Contains("RetentionService"));
        Assert.Contains(a.Logs.Entries, e => e.Message.StartsWith("Retention completed"));
        var persisted = (await StateAsync())!.LastSuccessUtc;

        // Fail over: the standby becomes active, but tonight's cleanup is already recorded.
        await AddRunAsync(RunStatus.Succeeded, now.AddDays(-40), attempts: 1);
        await a.StopAsync();
        await EventuallyAsync(() => Task.FromResult(b.Coordinator.Role == WorkerRole.Active), "node B takes over");
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.Equal(1, await RowsAsync("JobRuns"));
        Assert.Equal(persisted, (await StateAsync())!.LastSuccessUtc);
        Assert.DoesNotContain(b.Logs.Entries, e => e.Message.StartsWith("Retention started"));
    }

    [Fact]
    public async Task RetentionFailure_DoesNotStopJobExecution()
    {
        var key = "ret.exec";
        await DefineJobAsync(key);
        Scripts.Set(key, (_, _) => Task.FromResult(JobResult.Ok()));
        var now = DateTime.UtcNow;
        await SeedStateAsync(now.AddDays(-5), now.AddDays(-3));
        await ExecAsync("EXEC sp_rename 'dbo.usp_Monitoring_ApplyRetention_v1', 'usp_Hidden'");

        var node = await StartNodeAsync([key], LeaderOptions("node-a"));
        await EventuallyAsync(() => Task.FromResult(node.Logs.Entries.Any(e => e.Message.StartsWith("Retention failed"))), "retention fails");
        var run = await EnqueueManualAsync(key);

        await WaitForStatusAsync(run, RunStatus.Succeeded);
        Assert.Equal(WorkerRole.Active, node.Coordinator.Role);
    }
}
