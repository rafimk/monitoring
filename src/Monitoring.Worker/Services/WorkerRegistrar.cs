using Microsoft.EntityFrameworkCore;
using Monitoring.Application.Jobs;
using Monitoring.Infrastructure.Persistence;

namespace Monitoring.Worker.Services;

/// <summary>
/// Publishes this worker's identity and the job keys it can execute. Availability is derived from these rows (live
/// workers only), so a worker that registers a different set of jobs never makes another worker's jobs "unavailable".
/// </summary>
public sealed class WorkerRegistrar(WorkerIdentity identity, JobRegistry registry)
{
    public async Task RegisterAsync(MonitoringDbContext db, int runningCount, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var id = identity.Id;

        var announce = false; // a worker appearing or changing capabilities is dashboard-visible; plain heartbeats are not
        var updated = await db.Workers.Where(w => w.WorkerId == id)
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.LastSeenUtc, now).SetProperty(w => w.RunningCount, runningCount).SetProperty(w => w.StoppedUtc, (DateTime?)null), ct);
        if (updated == 0)
        {
            db.Workers.Add(new WorkerHeartbeat
            {
                WorkerId = id, ApplicationName = identity.ApplicationName, WorkerName = identity.Name, Host = Environment.MachineName,
                StartedUtc = identity.StartedUtc, LastSeenUtc = now, RunningCount = runningCount,
            });
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            announce = true;
        }

        var wanted = registry.All.Select(r => r.Key).ToHashSet();
        var existing = await db.WorkerJobs.AsNoTracking().Where(w => w.WorkerId == id).Select(w => w.JobKey).ToListAsync(ct);
        foreach (var gone in existing.Where(k => !wanted.Contains(k)))
            await db.WorkerJobs.Where(w => w.WorkerId == id && w.JobKey == gone).ExecuteDeleteAsync(ct);
        foreach (var added in wanted.Where(k => !existing.Contains(k)))
            db.WorkerJobs.Add(new WorkerJob { WorkerId = id, JobKey = added });
        if (announce || existing.Count != wanted.Count || wanted.Any(k => !existing.Contains(k))) ChangeFeed.Workers(db, "registered");
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Marks this instance as no longer serving (graceful stop, or leadership lost) so the dashboard and availability
    /// logic see it as offline immediately instead of after the heartbeat goes stale. Regaining leadership clears it.
    /// </summary>
    public async Task MarkStoppedAsync(MonitoringDbContext db, CancellationToken ct)
    {
        var id = identity.Id;
        var rows = await db.Workers.Where(w => w.WorkerId == id && w.StoppedUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.StoppedUtc, DateTime.UtcNow).SetProperty(w => w.RunningCount, 0), ct);
        if (rows > 0)
        {
            ChangeFeed.Workers(db, "stopped");
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>Called by the heartbeat. Returns true when the heartbeat row existed (the common case).</summary>
    public async Task<bool> BeatAsync(MonitoringDbContext db, int runningCount, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var id = identity.Id;
        var rows = await db.Workers.Where(w => w.WorkerId == id)
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.LastSeenUtc, now).SetProperty(w => w.RunningCount, runningCount).SetProperty(w => w.StoppedUtc, (DateTime?)null), ct);
        if (rows > 0) return true;
        await RegisterAsync(db, runningCount, ct); // row was purged: restore identity and capabilities
        return false;
    }
}
