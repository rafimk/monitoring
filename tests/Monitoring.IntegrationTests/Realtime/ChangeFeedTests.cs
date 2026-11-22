using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Monitoring.Contracts;
using Monitoring.Infrastructure.Persistence;
using Monitoring.IntegrationTests.Worker;
using Monitoring.Worker.Services;

namespace Monitoring.IntegrationTests.Realtime;

/// <summary>
/// Worker and API are separate processes that only share the database. These tests run a real worker and a real API host
/// (with the dispatcher driven one poll at a time, so nothing depends on sleeps) and check that changes travel through
/// the ChangeEvents table to connected hub clients.
/// </summary>
[Collection("worker-sql")]
public class ChangeFeedTests : WorkerTestBase
{
    private readonly List<IAsyncDisposable> _toDispose = [];

    private KeycloakApiFactory Api(Dictionary<string, string?>? settings = null)
    {
        var f = new KeycloakApiFactory(ConnectionString, settings, dispatcher: false); // manual dispatch
        _toDispose.Add(f);
        return f;
    }

    private async Task<(HubConnection Hub, HubRecorder Rec)> ConnectAsync(KeycloakApiFactory f)
    {
        await f.Dispatcher.EnsureLeadershipAsync(CancellationToken.None); // the hub only serves the dispatcher instance
        var hub = f.Hub(FakeKeycloak.Viewer());
        var rec = new HubRecorder(hub);
        _toDispose.Add(hub);
        await hub.StartAsync();
        return (hub, rec);
    }

    public new async Task DisposeAsync()
    {
        foreach (var d in _toDispose) await d.DisposeAsync();
        await base.DisposeAsync();
    }

    private async Task<List<ChangeEvent>> FeedAsync()
    {
        await using var db = NewDb();
        return await db.ChangeEvents.AsNoTracking().OrderBy(e => e.Id).ToListAsync();
    }

    private async Task<Guid> ScheduleAndAwaitRunAsync(string key, int dueInSeconds = 1)
    {
        await using (var db = NewDb())
        {
            db.Schedules.Add(new JobSchedule { Id = Guid.NewGuid(), JobKey = key, IntervalSeconds = 3600, NextDueUtc = DateTime.UtcNow.AddSeconds(dueInSeconds) });
            await db.SaveChangesAsync();
        }
        Guid id = default;
        await EventuallyAsync(async () =>
        {
            await using var db = NewDb();
            var r = await db.Runs.AsNoTracking().Where(x => x.JobKey == key && x.Status == RunStatus.Succeeded).FirstOrDefaultAsync();
            id = r?.Id ?? default;
            return r is not null;
        }, "the scheduled run to succeed");
        return id;
    }

    // ---- worker -> database -> hub ----

    [Fact]
    public async Task WorkerChanges_ReachConnectedClients_ThroughTheSharedDatabase_Coalesced()
    {
        const string key = "f.job";
        await DefineJobAsync(key);
        Scripts.Set(key, (_, _) => Task.FromResult(JobResult.Ok()));
        var api = Api();
        var (_, rec) = await ConnectAsync(api);
        await api.Dispatcher.DispatchOnceAsync(CancellationToken.None); // leader; drains the registration event
        rec.Workers.Clear();

        await StartWorkerAsync([key]);
        var runId = await ScheduleAndAwaitRunAsync(key);

        // The worker wrote queued/started/succeeded for the run in the same transactions as the state changes.
        var feed = await FeedAsync();
        Assert.Equal(new[] { "queued", "started", "succeeded" }, feed.Where(e => e.RunId == runId).Select(e => e.Reason));

        await api.Dispatcher.DispatchOnceAsync(CancellationToken.None);
        await HubRecorder.EventuallyAsync(() => rec.RunSnapshot().Any(m => m.RunId == runId), "a RunChanged for the run");

        var forRun = rec.RunSnapshot().Where(m => m.RunId == runId).ToList();
        Assert.Single(forRun);                         // three feed rows -> one message
        Assert.Equal("succeeded", forRun[0].Reason);   // the latest state wins
        Assert.Equal(key, forRun[0].JobKey);
        Assert.Contains(rec.JobSnapshot(), m => m.JobKey == key && m.Reason == "scheduled");
        Assert.Contains(rec.WorkerSnapshot(), _ => true);                     // the worker registered
        Assert.True(rec.Summaries >= 1);

        // Rows are marked dispatched after the broadcast.
        Assert.All(await FeedAsync(), e => Assert.NotNull(e.DispatchedUtc));
    }

    [Fact]
    public async Task RetryAndLeaseRecovery_AreAnnounced()
    {
        const string key = "f.retry";
        await DefineJobAsync(key, maxAttempts: 2, retryBaseSeconds: 1);
        Scripts.Set(key, (ctx, _) => Task.FromResult(ctx.Attempt < 2 ? JobResult.Fail("boom") : JobResult.Ok()));
        await StartWorkerAsync([key]);
        var retried = await EnqueueManualAsync(key);
        await WaitForStatusAsync(retried, RunStatus.Succeeded);
        Assert.Equal(new[] { "started", "retry", "started", "succeeded" },
            (await FeedAsync()).Where(e => e.RunId == retried).Select(e => e.Reason));

        const string key2 = "f.crash";
        await DefineJobAsync(key2);
        Scripts.Set(key2, (_, _) => Task.FromResult(JobResult.Ok()));
        var crashed = await InsertCrashedRunAsync(key2, 3, 1, TimeSpan.FromSeconds(30));
        await StartWorkerAsync([key2]);
        await WaitForStatusAsync(crashed, RunStatus.Succeeded);
        Assert.Contains("recovered", (await FeedAsync()).Where(e => e.RunId == crashed).Select(e => e.Reason));
    }

    [Fact]
    public async Task ApiCommands_WriteFeedRowsWithTheStateChange()
    {
        const string key = "f.cmd";
        await DefineJobAsync(key);
        Scripts.Set(key, (_, _) => Task.FromResult(JobResult.Ok()));
        await StartWorkerAsync([key]); // a live worker makes run-now legal
        var api = Api();
        var op = await api.ClientAsync(FakeKeycloak.Operator());
        var admin = await api.ClientAsync(FakeKeycloak.Admin());
        var (_, rec) = await ConnectAsync(api);
        await api.Dispatcher.DispatchOnceAsync(CancellationToken.None);
        rec.Jobs.Clear(); rec.Runs.Clear();

        Assert.True((await op.PostAsync($"/api/v1/jobs/{key}/disable", null)).IsSuccessStatusCode);
        Assert.True((await op.PostAsync($"/api/v1/jobs/{key}/enable", null)).IsSuccessStatusCode);
        Assert.True((await admin.PutAsJsonAsync($"/api/v1/jobs/{key}", new { timeoutSeconds = 45 })).IsSuccessStatusCode);
        var run = await op.PostAsync($"/api/v1/jobs/{key}/run", null);
        Assert.True(run.IsSuccessStatusCode);

        await api.Dispatcher.DispatchOnceAsync(CancellationToken.None);
        await HubRecorder.EventuallyAsync(() => rec.RunSnapshot().Count > 0 && rec.JobSnapshot().Count > 0, "run and job notifications");
        Assert.Contains(rec.RunSnapshot(), m => m.JobKey == key);
        Assert.Equal("updated", rec.JobSnapshot().Single(m => m.JobKey == key).Reason); // coalesced: latest of enabled/disabled/updated
    }

    // ---- restart / replay ----

    [Fact]
    public async Task NotificationsResume_AfterTheApiWasStopped_WhileTheWorkerKeptRunning()
    {
        const string key = "f.resume";
        await DefineJobAsync(key);
        Scripts.Set(key, (_, _) => Task.FromResult(JobResult.Ok()));

        // 1) API up, then stopped.
        var first = Api();
        _ = first.Server;
        await first.Dispatcher.DispatchOnceAsync(CancellationToken.None);
        await first.DisposeAsync();

        // 2) Worker alone keeps scheduling and executing (no API process exists).
        await StartWorkerAsync([key]);
        var runId = await ScheduleAndAwaitRunAsync(key);
        var pending = (await FeedAsync()).Where(e => e.DispatchedUtc is null && e.RunId == runId).ToList();
        Assert.NotEmpty(pending); // events are waiting in the database

        // 3) API restarted: a client that connects receives what it missed, from the durable feed.
        var second = Api();
        var (_, rec) = await ConnectAsync(second);
        await second.Dispatcher.DispatchOnceAsync(CancellationToken.None);
        await HubRecorder.EventuallyAsync(() => rec.RunSnapshot().Any(m => m.RunId == runId && m.Reason == "succeeded"), "replayed run notification");
        Assert.All(await FeedAsync(), e => Assert.NotNull(e.DispatchedUtc));
    }

    [Fact]
    public async Task ReplayedAndDuplicateFeedRows_AreHarmless()
    {
        const string key = "f.dupe";
        await DefineJobAsync(key);
        var runId = await EnqueueManualAsync(key);
        var api = Api();
        var (_, rec) = await ConnectAsync(api);

        await using (var db = NewDb())
        {
            // The same logical change written three times (e.g. a retried writer).
            for (var i = 0; i < 3; i++) ChangeFeed.Run(db, runId, key, "queued");
            await db.SaveChangesAsync();
        }
        await api.Dispatcher.DispatchOnceAsync(CancellationToken.None);
        await HubRecorder.EventuallyAsync(() => rec.RunSnapshot().Count >= 1, "first notification");
        await Task.Delay(300);
        Assert.Single(rec.RunSnapshot()); // duplicates in one batch collapse

        // Simulate a crash between "broadcast" and "mark dispatched": the rows become pending again.
        await using (var db = NewDb())
            await db.ChangeEvents.ExecuteUpdateAsync(s => s.SetProperty(e => e.DispatchedUtc, (DateTime?)null));
        await api.Dispatcher.DispatchOnceAsync(CancellationToken.None);
        await HubRecorder.EventuallyAsync(() => rec.RunSnapshot().Count >= 2, "replayed notification");

        // A replay is just another "refetch" hint: the identical message, and no change to the run itself.
        Assert.All(rec.RunSnapshot(), m => { Assert.Equal(runId, m.RunId); Assert.Equal("queued", m.Reason); });
        Assert.Equal(RunStatus.Queued, (await GetRunAsync(runId)).Status);
        Assert.Equal(0, (await api.Dispatcher.DispatchOnceAsync(CancellationToken.None)));       // nothing left, nothing re-sent
    }

    [Fact]
    public async Task AFloodOfRunChanges_IsSentAsOneBulkNotification()
    {
        const string key = "f.flood";
        await DefineJobAsync(key);
        var api = Api(new() { ["Api:MaxRunNotificationsPerBatch"] = "10" });
        var (_, rec) = await ConnectAsync(api);
        await api.Dispatcher.DispatchOnceAsync(CancellationToken.None);
        rec.Runs.Clear();

        await using (var db = NewDb())
        {
            for (var i = 0; i < 60; i++) ChangeFeed.Run(db, Guid.NewGuid(), key, "queued");
            await db.SaveChangesAsync();
        }
        await api.Dispatcher.DispatchOnceAsync(CancellationToken.None);
        await HubRecorder.EventuallyAsync(() => rec.RunSnapshot().Count > 0, "bulk notification");
        await Task.Delay(300);
        var msgs = rec.RunSnapshot();
        Assert.Single(msgs);
        Assert.Null(msgs[0].RunId);
        Assert.Equal("bulk", msgs[0].Reason);
    }

    // ---- heartbeats, availability, cleanup, atomicity, single instance ----

    [Fact]
    public async Task PlainHeartbeats_DoNotWriteFeedRows()
    {
        const string key = "f.hb";
        await DefineJobAsync(key);
        await StartWorkerAsync([key], FastOptions(lease: 6, heartbeat: 1));
        var before = (await FeedAsync()).Count; // registration only
        Assert.True(before >= 1);

        await Task.Delay(TimeSpan.FromSeconds(4)); // several heartbeats
        Assert.Equal(before, (await FeedAsync()).Count);
    }

    [Fact]
    public async Task WorkerGoingOffline_IsDetectedAndAnnounced_WithoutAnyWrite()
    {
        const string key = "f.offline";
        await DefineJobAsync(key);
        var worker = await StartWorkerAsync([key], FastOptions(lease: 6, heartbeat: 1));
        var api = Api();
        var (_, rec) = await ConnectAsync(api);
        await api.Dispatcher.DispatchOnceAsync(CancellationToken.None); // baseline: worker is live
        rec.Workers.Clear(); rec.Jobs.Clear();

        await worker.StopAsync(); // heartbeats stop; nothing is written to the database
        await HubRecorder.EventuallyAsync(() =>
        {
            api.Dispatcher.DispatchOnceAsync(CancellationToken.None).GetAwaiter().GetResult();
            return rec.WorkerSnapshot().Count > 0;
        }, "WorkersChanged after the heartbeat went stale", timeoutSeconds: 20);
        Assert.Contains(rec.JobSnapshot(), m => m.JobKey is null && m.Reason == "availability"); // job availability changed too
    }

    [Fact]
    public async Task TheDispatcher_NeverDeletesFeedRows_OnlyNightlyRetentionDoes()
    {
        await using (var db = NewDb())
        {
            var now = DateTime.UtcNow;
            db.ChangeEvents.AddRange(
                new ChangeEvent { Kind = ChangeKind.Job, JobKey = "old.dispatched", Reason = "x", CreatedUtc = now.AddDays(-30), DispatchedUtc = now.AddDays(-30) },
                new ChangeEvent { Kind = ChangeKind.Job, JobKey = "recent.dispatched", Reason = "x", CreatedUtc = now, DispatchedUtc = now });
            await db.SaveChangesAsync();
        }
        var api = Api();
        _ = api.Server;
        await api.Dispatcher.DispatchOnceAsync(CancellationToken.None);

        var left = (await FeedAsync()).Select(e => e.JobKey).ToList();
        Assert.Contains("old.dispatched", left);
        Assert.Contains("recent.dispatched", left);
    }

    [Fact]
    public async Task APendingRow_IsDispatched_EvenWhenItIsAncient_AndNothingDeletesItFirst()
    {
        await using (var db = NewDb())
        {
            db.ChangeEvents.Add(new ChangeEvent { Kind = ChangeKind.Job, JobKey = "ancient.pending", Reason = "x", CreatedUtc = DateTime.UtcNow.AddDays(-400) });
            await db.SaveChangesAsync();
        }
        await DefineJobAsync("f.keep");
        await StartWorkerAsync(["f.keep"]);   // the worker's loops must not purge the feed
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.Contains(await FeedAsync(), e => e.JobKey == "ancient.pending" && e.DispatchedUtc == null);

        var api = Api();
        _ = api.Server;
        await api.Dispatcher.DispatchOnceAsync(CancellationToken.None);
        Assert.Contains(await FeedAsync(), e => e.JobKey == "ancient.pending" && e.DispatchedUtc != null);
    }

    [Fact]
    public async Task FeedRow_IsCommittedWithTheStateChange_NotWrittenWhenTheChangeIsRejected()
    {
        const string key = "f.atomic";
        await DefineJobAsync(key);
        var runId = await EnqueueManualAsync(key);
        var (store, _) = CreateBareStore("atomic", key);

        var claimed = (await store.ClaimAsync(1, CancellationToken.None)).Single();
        Assert.Equal(new[] { "started" }, (await FeedAsync()).Where(e => e.RunId == runId).Select(e => e.Reason));

        // A stale attempt's completion is rejected and its transaction rolled back: no phantom "succeeded" event.
        var stale = claimed with { Attempt = claimed.Attempt + 1 };
        Assert.False(await store.CompleteAsync(stale, Completion.Final, RunStatus.Succeeded, null, DateTime.UtcNow));
        Assert.Equal(new[] { "started" }, (await FeedAsync()).Where(e => e.RunId == runId).Select(e => e.Reason));

        Assert.True(await store.CompleteAsync(claimed, Completion.Final, RunStatus.Succeeded, null, DateTime.UtcNow));
        Assert.Equal(new[] { "started", "succeeded" }, (await FeedAsync()).Where(e => e.RunId == runId).Select(e => e.Reason));
    }

    [Fact]
    public async Task OnlyOneApiInstanceDispatches_TheOtherRefusesHubConnections_UntilTakeover()
    {
        const string key = "f.leader";
        await DefineJobAsync(key);
        var a = Api();
        var b = Api();
        _ = a.Server; _ = b.Server;

        await a.Dispatcher.DispatchOnceAsync(CancellationToken.None); // A takes the lock
        await b.Dispatcher.DispatchOnceAsync(CancellationToken.None);  // B stays standby

        await using (var db = NewDb())
        {
            ChangeFeed.Job(db, key, "enabled");
            await db.SaveChangesAsync();
        }
        Assert.Equal(0, await b.Dispatcher.DispatchOnceAsync(CancellationToken.None));
        Assert.Contains(await FeedAsync(), e => e.DispatchedUtc is null); // B did not consume anything

        // A client on the standby instance is turned away (it falls back to polling).
        var standbyClient = b.Hub(FakeKeycloak.Viewer());
        var closed = new TaskCompletionSource();
        standbyClient.Closed += _ => { closed.TrySetResult(); return Task.CompletedTask; };
        try { await standbyClient.StartAsync(); } catch { closed.TrySetResult(); }
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await standbyClient.DisposeAsync();

        // When A goes away, B takes over and delivers the pending change.
        await a.DisposeAsync();
        await EventuallyAsync(() => b.Dispatcher.EnsureLeadershipAsync(CancellationToken.None), "B to acquire the released lock", 20);
        var (_, rec) = await ConnectAsync(b);
        await HubRecorder.EventuallyAsync(() =>
        {
            b.Dispatcher.DispatchOnceAsync(CancellationToken.None).GetAwaiter().GetResult();
            return rec.JobSnapshot().Any(m => m.JobKey == key);
        }, "standby instance to take over", timeoutSeconds: 20);
    }
}
