using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Monitoring.Infrastructure.Persistence;

namespace Monitoring.Api.Realtime;

/// <summary>
/// Reads the shared change feed (written transactionally by the worker and the API) and pushes small notifications to
/// dashboard clients. Delivery is at-least-once: rows are marked dispatched only after the broadcast, so after a crash or
/// an API restart pending rows are simply broadcast again. That is harmless because every notification only tells the
/// client to refetch. It also works while no API process existed: the worker never talks to the API.
/// </summary>
public sealed class ChangeFeedDispatcher(
    IServiceScopeFactory scopes, IHubContext<MonitoringHub, IMonitoringClient> hub, DispatcherLeadership leadership,
    IOptions<ApiOptions> options, ILogger<ChangeFeedDispatcher> log, TimeProvider? timeProvider = null) : BackgroundService
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private HashSet<string>? _liveWorkers;
    private DateTime _nextWorkerCheckUtc = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var o = options.Value;
        var wasLeader = false;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var processed = await DispatchOnceAsync(stop);
                if (leadership.IsLeader && !wasLeader) log.LogInformation("This API instance is now the change-feed dispatcher");
                wasLeader = leadership.IsLeader;
                if (processed >= o.ChangeFeedBatchSize) continue; // backlog: keep draining without sleeping
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogError(ex, "Change-feed dispatch failed; will retry"); }

            try { await Task.Delay(TimeSpan.FromMilliseconds(o.ChangeFeedPollMilliseconds), _time, stop); }
            catch (OperationCanceledException) { break; }
        }
        await leadership.ReleaseAsync();
    }

    /// <summary>Takes (or confirms) the dispatcher lock without consuming any feed rows.</summary>
    public async Task<bool> EnsureLeadershipAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MonitoringDbContext>();
        return await leadership.EnsureAsync(db.Database.GetConnectionString()!, ct);
    }

    /// <summary>One poll. Returns how many feed rows were consumed (0 when not the leader or nothing is pending).</summary>
    public async Task<int> DispatchOnceAsync(CancellationToken ct)
    {
        var o = options.Value;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MonitoringDbContext>();

        if (!await leadership.EnsureAsync(db.Database.GetConnectionString()!, ct)) return 0;

        var now = _time.GetUtcNow().UtcDateTime;
        var pending = await db.ChangeEvents.AsNoTracking()
            .Where(e => e.DispatchedUtc == null).OrderBy(e => e.Id).Take(o.ChangeFeedBatchSize).ToListAsync(ct);

        var workersChanged = await DetectWorkerChangeAsync(db, now, ct);

        if (pending.Count > 0 || workersChanged)
        {
            await BroadcastAsync(pending, workersChanged, o.MaxRunNotificationsPerBatch, ct);
            // Mark only after the broadcast: a failure above leaves the rows pending and they are retried.
            if (pending.Count > 0)
            {
                var ids = pending.Select(p => p.Id).ToList();
                await db.ChangeEvents.Where(e => ids.Contains(e.Id) && e.DispatchedUtc == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(e => e.DispatchedUtc, now), ct);
            }
        }

        return pending.Count;
    }

    private async Task BroadcastAsync(List<ChangeEvent> events, bool workersChanged, int maxRuns, CancellationToken ct)
    {
        var clients = hub.Clients.All;

        // Coalesce: many events for the same run/job become one message; a flood becomes a single generic one.
        var runs = events.Where(e => e.Kind == ChangeKind.Run && e.RunId != null)
            .GroupBy(e => e.RunId!.Value).Select(g => g.Last()).ToList();
        if (runs.Count > maxRuns)
            await clients.RunChanged(new RunChangedMessage(null, null, "bulk"));
        else
            foreach (var r in runs) await clients.RunChanged(new RunChangedMessage(r.RunId, r.JobKey, r.Reason));

        foreach (var j in events.Where(e => e.Kind == ChangeKind.Job).GroupBy(e => e.JobKey).Select(g => g.Last()))
            await clients.JobChanged(new JobChangedMessage(j.JobKey, j.Reason));

        if (workersChanged || events.Any(e => e.Kind == ChangeKind.Workers))
        {
            var reason = events.LastOrDefault(e => e.Kind == ChangeKind.Workers)?.Reason ?? "availability";
            await clients.WorkersChanged(new WorkersChangedMessage(reason));
            await clients.JobChanged(new JobChangedMessage(null, "availability")); // job availability derives from live workers
        }
        await clients.SummaryChanged();
    }

    /// <summary>
    /// Workers dying produce no write, so liveness changes are detected by diffing the live set (rate-limited). This is
    /// also how plain heartbeats stay silent: only appearing/disappearing workers reach the dashboard.
    /// </summary>
    private async Task<bool> DetectWorkerChangeAsync(MonitoringDbContext db, DateTime now, CancellationToken ct)
    {
        var o = options.Value;
        if (now < _nextWorkerCheckUtc) return false;
        _nextWorkerCheckUtc = now.AddSeconds(o.WorkerWatchSeconds);

        var live = (await db.Workers.AsNoTracking().Where(w => w.LastSeenUtc >= now.AddSeconds(-o.WorkerStaleSeconds) && w.StoppedUtc == null)
            .Select(w => w.WorkerId).ToListAsync(ct)).ToHashSet();
        var changed = _liveWorkers is not null && !_liveWorkers.SetEquals(live);
        _liveWorkers = live; // the first observation after start is a baseline, not a change
        return changed;
    }
}
