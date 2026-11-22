using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Monitoring.Api;
using Monitoring.Api.Controllers;
using Monitoring.Contracts;
using Monitoring.Infrastructure.Persistence;
using Monitoring.Worker;
using Monitoring.Worker.Services;

namespace Monitoring.IntegrationTests.Worker;

/// <summary>
/// Worker nodes grouped by application: persistence of the application name, the migration backfill, and the
/// /workers/applications query (registered vs running services, online/stale/offline, restarts, duplicate registrations,
/// active/standby), against a throwaway SQL Server database.
/// </summary>
public class WorkerApplicationsTests : WorkerTestBase
{
    private const int Stale = 45;

    private MonitoringController Controller(MonitoringDbContext db) =>
        new(db, Options.Create(new ApiOptions { WorkerStaleSeconds = Stale }));

    private async Task<WorkerApplicationsDto> GetAsync()
    {
        await using var db = NewDb();
        return await Controller(db).WorkerApplications(CancellationToken.None);
    }

    private async Task AddWorkerAsync(string id, string app, string name, int ageSeconds, params string[] services)
    {
        await using var db = NewDb();
        var now = DateTime.UtcNow;
        db.Workers.Add(new WorkerHeartbeat
        {
            WorkerId = id, ApplicationName = app, WorkerName = name, Host = "h", StartedUtc = now.AddHours(-3), LastSeenUtc = now.AddSeconds(-ageSeconds),
        });
        await db.SaveChangesAsync();
        foreach (var s in services) db.WorkerJobs.Add(new WorkerJob { WorkerId = id, JobKey = s });
        await db.SaveChangesAsync();
    }

    private async Task DefineAsync(params string[] keys)
    {
        foreach (var k in keys) await DefineJobAsync(k);
    }

    private async Task AddRunningAsync(string workerId, string jobKey, int count = 1, RunStatus status = RunStatus.Running)
    {
        await using var db = NewDb();
        var now = DateTime.UtcNow;
        for (var i = 0; i < count; i++)
            db.Runs.Add(new JobRun
            {
                Id = Guid.NewGuid(), JobKey = jobKey, Trigger = TriggerSource.Manual, Status = status, ScheduledUtc = now, QueuedUtc = now, NotBeforeUtc = now,
                StartedUtc = now, Attempt = 1, CountedAttempts = 1, MaxAttempts = 1, WorkerId = workerId, LeaseExpiresUtc = now.AddMinutes(1),
                CorrelationId = Guid.NewGuid().ToString("N"),
            });
        await db.SaveChangesAsync();
    }

    // ------------------------------------------------------------------ migration / backfill

    [Fact]
    public async Task Migration_BackfillsExistingWorkerRows_AndKeepsTheirCapabilities()
    {
        var name = "Monitoring_M_" + Guid.NewGuid().ToString("N")[..12];
        var cs = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = name }.ConnectionString;
        DbContextOptions<MonitoringDbContext> Opts() => new DbContextOptionsBuilder<MonitoringDbContext>().UseSqlServer(cs).Options;
        try
        {
            await using (var db = new MonitoringDbContext(Opts()))
            {
                // The schema as it was before application names existed.
                await db.GetService<IMigrator>().MigrateAsync("RetentionProcedure");
                await db.Database.ExecuteSqlRawAsync(
                    "INSERT INTO dbo.WorkerHeartbeats (WorkerId, Host, StartedUtc, LastSeenUtc, RunningCount) VALUES " +
                    "(N'billing-node-1a2b3c4d', N'h', SYSUTCDATETIME(), SYSUTCDATETIME(), 0), (N'short', N'h', SYSUTCDATETIME(), SYSUTCDATETIME(), 0)");
                await db.Database.ExecuteSqlRawAsync(
                    "INSERT INTO dbo.WorkerJobs (WorkerId, JobKey) VALUES (N'billing-node-1a2b3c4d', N'bill.invoice')");
            }

            await using (var db = new MonitoringDbContext(Opts()))
            {
                await db.Database.MigrateAsync();
                var rows = await db.Workers.AsNoTracking().OrderBy(w => w.WorkerId).ToListAsync();
                Assert.All(rows, w => Assert.Equal("Unassigned", w.ApplicationName));
                Assert.Equal("billing-node", rows.Single(w => w.WorkerId == "billing-node-1a2b3c4d").WorkerName);   // suffix stripped
                Assert.Equal("short", rows.Single(w => w.WorkerId == "short").WorkerName);                           // too short to carry a suffix
                Assert.All(rows, w => Assert.Null(w.StoppedUtc));
                Assert.Equal(1, await db.WorkerJobs.CountAsync());                                                   // capabilities untouched
            }
        }
        finally
        {
            await using var db = new MonitoringDbContext(Opts());
            await db.Database.EnsureDeletedAsync();
            SqlConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task BackfilledRows_AppearInTheUnassignedGroup_AsHistory()
    {
        await AddWorkerAsync("old-node-aaaaaaaa", "Unassigned", "old-node", ageSeconds: 3600);
        var result = await GetAsync();
        var group = Assert.Single(result.Applications);
        Assert.Equal("Unassigned", group.ApplicationName);
        Assert.Equal(0, group.OnlineNodes);
        Assert.Single(group.PreviousInstances);
    }

    // ------------------------------------------------------------------ identity is persisted by the real registrar

    [Fact]
    public async Task Worker_PersistsApplicationName_StableName_AndAUniqueInstanceId()
    {
        var o = FastOptions("billing-node");
        o.ApplicationName = "Billing";
        await StartWorkerAsync([], o);

        await using var db = NewDb();
        var w = await db.Workers.AsNoTracking().SingleAsync();
        Assert.Equal("Billing", w.ApplicationName);
        Assert.Equal("billing-node", w.WorkerName);
        Assert.StartsWith("billing-node-", w.WorkerId);
        Assert.NotEqual(w.WorkerName, w.WorkerId);
        Assert.NotEqual(w.ApplicationName, w.WorkerId);
    }

    [Fact]
    public async Task WithoutAConfiguredWorkerName_TheStableNameIsTheMachineName_NotTheProcessId()
    {
        var o = FastOptions();
        o.ApplicationName = "Billing";
        await StartWorkerAsync([], o);
        await using var db = NewDb();
        Assert.Equal(Environment.MachineName, (await db.Workers.AsNoTracking().SingleAsync()).WorkerName);
    }

    [Fact]
    public async Task Restart_CreatesANewInstance_ThatKeepsTheGroupingWhileTheOldOneStaysInItsOriginalApplication()
    {
        var o1 = FastOptions("billing-node"); o1.ApplicationName = "Billing";
        var first = await StartWorkerAsync([], o1);
        var firstId = first.Identity.Id;
        await first.StopAsync();
        await using (var db = NewDb()) await db.Workers.Where(w => w.WorkerId == firstId).ExecuteUpdateAsync(s => s.SetProperty(w => w.StoppedUtc, DateTime.UtcNow));

        var o2 = FastOptions("billing-node"); o2.ApplicationName = "Billing";
        await StartWorkerAsync([], o2);

        var result = await GetAsync();
        var billing = Assert.Single(result.Applications);
        Assert.Equal(1, billing.OnlineNodes);
        Assert.Single(billing.Nodes);
        Assert.NotEqual(firstId, billing.Nodes[0].WorkerId);
        Assert.Equal("billing-node", billing.Nodes[0].WorkerName);
        var previous = Assert.Single(billing.PreviousInstances);
        Assert.Equal(firstId, previous.WorkerId);
        Assert.Equal("offline", previous.State);
    }

    // ------------------------------------------------------------------ grouping and counts

    [Fact]
    public async Task TwoApplications_AreGroupedSeparately_EvenWithTheSameConfiguredWorkerName()
    {
        await DefineAsync("bill.a", "rep.a");
        await AddWorkerAsync("node-aaaa1111", "Billing", "node", 2, "bill.a");
        await AddWorkerAsync("node-bbbb2222", "Reports", "node", 2, "rep.a");

        var result = await GetAsync();

        Assert.Equal(["Billing", "Reports"], result.Applications.Select(a => a.ApplicationName));
        var billing = result.Applications[0];
        var reports = result.Applications[1];
        Assert.Equal("node-aaaa1111", Assert.Single(billing.Nodes).WorkerId);
        Assert.Equal("node-bbbb2222", Assert.Single(reports.Nodes).WorkerId);
        Assert.Equal(["bill.a"], billing.Nodes[0].Services.Select(s => s.JobKey));
        Assert.Equal(["rep.a"], reports.Nodes[0].Services.Select(s => s.JobKey));
    }

    [Fact]
    public async Task RegisteredAndRunning_AreSeparate_AndOnlyDistinctServicesAreCounted()
    {
        await DefineAsync("a", "b", "c", "d", "e");
        await AddWorkerAsync("n-11111111", "App", "n", 1, "a", "b", "c", "d", "e");
        await AddRunningAsync("n-11111111", "a");

        var app = Assert.Single((await GetAsync()).Applications);
        var node = Assert.Single(app.Nodes);

        Assert.Equal(5, node.RegisteredServiceCount);
        Assert.Equal(1, node.RunningServices);
        Assert.Equal(1, node.RunningRuns);
        Assert.Equal(5, app.RegisteredServices);
        Assert.Equal(1, app.RunningServices);
        Assert.Equal(1, node.Services.Single(s => s.JobKey == "a").RunningCount);
        Assert.All(node.Services.Where(s => s.JobKey != "a"), s => Assert.Equal(0, s.RunningCount));
    }

    [Fact]
    public async Task HistoricalAndQueuedRuns_AreNotServicesAndNotRunning()
    {
        await DefineAsync("a", "b");
        await AddWorkerAsync("n-11111111", "App", "n", 1, "a", "b");
        foreach (var status in new[] { RunStatus.Succeeded, RunStatus.Failed, RunStatus.Queued, RunStatus.Abandoned })
            await AddRunningAsync("n-11111111", "a", count: 3, status);

        var node = Assert.Single(Assert.Single((await GetAsync()).Applications).Nodes);

        Assert.Equal(2, node.RegisteredServiceCount);   // two services, however many runs exist
        Assert.Equal(0, node.RunningServices);
        Assert.Equal(0, node.RunningRuns);
    }

    [Fact]
    public async Task ParallelRunsOfOneService_CountAsOneRunningService_WithTheRunCountPerService()
    {
        await DefineAsync("a", "b");
        await AddWorkerAsync("n-11111111", "App", "n", 1, "a", "b");
        await AddRunningAsync("n-11111111", "a", count: 2);

        var app = Assert.Single((await GetAsync()).Applications);
        var node = Assert.Single(app.Nodes);
        Assert.Equal(1, node.RunningServices);
        Assert.Equal(2, node.RunningRuns);
        Assert.Equal(2, node.Services.Single(s => s.JobKey == "a").RunningCount);
        Assert.Equal(1, app.RunningServices);
    }

    [Fact]
    public async Task RunningWork_IsAttributedToTheNodeThatExecutesIt()
    {
        await DefineAsync("a");
        await AddWorkerAsync("n1-11111111", "App", "n1", 1, "a");
        await AddWorkerAsync("n2-22222222", "App", "n2", 1, "a");
        await AddRunningAsync("n2-22222222", "a");

        var app = Assert.Single((await GetAsync()).Applications);
        Assert.Equal(0, app.Nodes.Single(n => n.WorkerName == "n1").RunningServices);
        Assert.Equal(1, app.Nodes.Single(n => n.WorkerName == "n2").RunningServices);
    }

    [Fact]
    public async Task DuplicateServiceOnTwoNodes_IsOneDistinctService_WhileEachNodeShowsItsOwnCount()
    {
        await DefineAsync("a", "b", "c");
        await AddWorkerAsync("n1-11111111", "App", "n1", 1, "a", "b");
        await AddWorkerAsync("n2-22222222", "App", "n2", 1, "a", "b", "c");

        var app = Assert.Single((await GetAsync()).Applications);

        Assert.Equal(2, app.OnlineNodes);
        Assert.Equal(3, app.RegisteredServices);   // a, b, c: not 5
        Assert.Equal(2, app.Nodes.Single(n => n.WorkerName == "n1").RegisteredServiceCount);
        Assert.Equal(3, app.Nodes.Single(n => n.WorkerName == "n2").RegisteredServiceCount);
    }

    [Fact]
    public async Task ServiceNamesAndEnabledStateComeFromTheDefinitions()
    {
        await DefineAsync("a", "b");
        await using (var db = NewDb()) await db.Definitions.Where(d => d.Key == "b").ExecuteUpdateAsync(s => s.SetProperty(d => d.Enabled, false).SetProperty(d => d.DisplayName, "Bee"));
        await AddWorkerAsync("n-11111111", "App", "n", 1, "a", "b");

        var node = Assert.Single(Assert.Single((await GetAsync()).Applications).Nodes);

        var b = node.Services.Single(s => s.JobKey == "b");
        Assert.Equal("Bee", b.DisplayName);
        Assert.False(b.Enabled);
        Assert.True(node.Services.Single(s => s.JobKey == "a").Enabled);
    }

    // ------------------------------------------------------------------ stale / offline

    [Fact]
    public async Task StaleNode_IsListedButItsCapabilitiesAreNotCountedAsAvailable()
    {
        await DefineAsync("a", "b");
        await AddWorkerAsync("ok-11111111", "App", "ok", 1, "a");
        await AddWorkerAsync("stale-2222222", "App", "stale", Stale + 30, "a", "b");

        var app = Assert.Single((await GetAsync()).Applications);

        var stale = app.Nodes.Single(n => n.WorkerName == "stale");
        Assert.Equal("stale", stale.State);
        Assert.Empty(stale.Services);
        Assert.Equal(1, app.OnlineNodes);
        Assert.Equal(1, app.RegisteredServices);   // "b" is only registered by the stale node
    }

    [Fact]
    public async Task OfflineNode_IsHistorical_WithNoServices_AndNeverCountsAsOnline()
    {
        await DefineAsync("a");
        await AddWorkerAsync("gone-11111111", "App", "gone", Stale * 4 + 60, "a");

        var app = Assert.Single((await GetAsync()).Applications);

        Assert.Equal(0, app.OnlineNodes);
        Assert.Equal(0, app.RegisteredServices);
        Assert.Empty(app.Nodes);
        Assert.Single(app.PreviousInstances);
    }

    [Fact]
    public async Task ApplicationWithNoOnlineNodes_SortsAfterApplicationsThatAreServing()
    {
        await DefineAsync("a");
        await AddWorkerAsync("a-11111111", "Alpha", "a", Stale * 4 + 60, "a");
        await AddWorkerAsync("z-11111111", "Zulu", "z", 1, "a");

        Assert.Equal(["Zulu", "Alpha"], (await GetAsync()).Applications.Select(a => a.ApplicationName));
    }

    [Fact]
    public async Task ARunningRowOfAnOfflineNode_IsNotReportedAsCurrentlyExecuting()
    {
        await DefineAsync("a");
        await AddWorkerAsync("gone-11111111", "App", "gone", Stale * 4 + 60, "a");
        await AddRunningAsync("gone-11111111", "a");   // the run is still marked Running until its lease is recovered

        var app = Assert.Single((await GetAsync()).Applications);
        Assert.Equal(0, app.RunningServices);
    }

    [Fact]
    public async Task AStoppedInstance_IsOfflineAtOnce_EvenWithAFreshHeartbeat()
    {
        await DefineAsync("a");
        await AddWorkerAsync("old-11111111", "App", "n", 1, "a");
        await AddWorkerAsync("new-22222222", "App", "n", 1, "a");
        await using (var db = NewDb()) await db.Workers.Where(w => w.WorkerId == "old-11111111").ExecuteUpdateAsync(s => s.SetProperty(w => w.StoppedUtc, DateTime.UtcNow));

        var app = Assert.Single((await GetAsync()).Applications);

        Assert.Equal("new-22222222", Assert.Single(app.Nodes).WorkerId);
        Assert.Equal("old-11111111", Assert.Single(app.PreviousInstances).WorkerId);
        await using var check = NewDb();
        Assert.Equal(["a"], await WorkerCapacity.LiveJobKeys(check, DateTime.UtcNow.AddSeconds(-Stale)).ToListAsync());   // availability still has the new node
        await check.Workers.Where(w => w.WorkerId == "new-22222222").ExecuteUpdateAsync(s => s.SetProperty(w => w.StoppedUtc, DateTime.UtcNow));
        Assert.Empty(await WorkerCapacity.LiveJobKeys(check, DateTime.UtcNow.AddSeconds(-Stale)).ToListAsync());             // a stopped worker is not capacity
    }

    // ------------------------------------------------------------------ efficiency

    private sealed class CommandCounter : DbCommandInterceptor
    {
        public int Commands;
        public override InterceptionResult<System.Data.Common.DbDataReader> ReaderExecuting(
            System.Data.Common.DbCommand command, CommandEventData eventData, InterceptionResult<System.Data.Common.DbDataReader> result)
        { Interlocked.Increment(ref Commands); return result; }
        public override ValueTask<InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, CommandEventData eventData, InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken ct = default)
        { Interlocked.Increment(ref Commands); return ValueTask.FromResult(result); }
    }

    [Fact]
    public async Task TheQuery_UsesAFixedNumberOfCommands_NotOnePerWorker()
    {
        var keys = Enumerable.Range(0, 6).Select(i => $"svc.{i}").ToArray();
        await DefineAsync(keys);
        for (var i = 0; i < 25; i++) await AddWorkerAsync($"node{i}-{i:D8}", $"App{i % 5}", $"node{i}", 1, keys);
        await AddRunningAsync("node3-00000003", "svc.1");

        var counter = new CommandCounter();
        await using var db = new MonitoringDbContext(new DbContextOptionsBuilder<MonitoringDbContext>().UseSqlServer(ConnectionString).AddInterceptors(counter).Options);
        var result = await Controller(db).WorkerApplications(CancellationToken.None);

        Assert.Equal(5, result.Applications.Count);
        Assert.Equal(25, result.Applications.Sum(a => a.OnlineNodes));
        Assert.True(counter.Commands <= 4, $"expected at most 4 commands, saw {counter.Commands}");
    }

    // ------------------------------------------------------------------ active / standby

    [Fact]
    public async Task ActiveStandby_OnlyTheActiveNodeAppears_AndAFailoverMovesTheCardToTheNewInstance()
    {
        var oa = LeaderOptions("node-a"); oa.ApplicationName = "Billing";
        var ob = LeaderOptions("node-b"); ob.ApplicationName = "Billing";
        var a = await StartNodeAsync([], oa);
        await EventuallyAsync(() => Task.FromResult(a.Coordinator.Role == WorkerRole.Active), "A active");
        var b = await StartNodeAsync([], ob);
        await EventuallyAsync(() => Task.FromResult(b.Coordinator.Role == WorkerRole.Standby), "B standby");

        var beforeFailover = Assert.Single((await GetAsync()).Applications);
        Assert.Equal(1, beforeFailover.OnlineNodes);                          // the standby registers nothing and is not shown
        Assert.Equal("node-a", Assert.Single(beforeFailover.Nodes).WorkerName);

        await a.StopAsync();
        await EventuallyAsync(() => Task.FromResult(b.Coordinator.Role == WorkerRole.Active), "B takes over");
        await EventuallyAsync(async () =>
        {
            var g = Assert.Single((await GetAsync()).Applications);
            return g.OnlineNodes == 1 && g.Nodes.Single().WorkerName == "node-b";
        }, "the dashboard moves to node B immediately (no 45 s window with two 'online' nodes)", timeoutSeconds: 15);

        var after = Assert.Single((await GetAsync()).Applications);
        Assert.Equal(a.Identity.Id, Assert.Single(after.PreviousInstances).WorkerId);
    }

    [Fact]
    public async Task TheHeartbeatOfALiveInstance_ClearsAStoppedMarker()
    {
        var o = LeaderOptions("node-a"); o.ApplicationName = "Billing";
        var node = await StartNodeAsync([], o);
        await EventuallyAsync(() => Task.FromResult(node.Coordinator.Role == WorkerRole.Active), "active");
        await using (var db = NewDb()) await db.Workers.ExecuteUpdateAsync(s => s.SetProperty(w => w.StoppedUtc, DateTime.UtcNow));   // as if marked stopped

        // The next heartbeat of a live instance clears the marker.
        await EventuallyAsync(async () => Assert.Single((await GetAsync()).Applications).OnlineNodes == 1, "the heartbeat clears StoppedUtc", timeoutSeconds: 20);
    }
}
