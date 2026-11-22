using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Monitoring.Contracts;
using Monitoring.Infrastructure.Persistence;


namespace Monitoring.Api.Controllers;

[ApiController]
[Route("api/v1")]
[Authorize(Policy = Policies.Viewer)]
public class MonitoringController(MonitoringDbContext db, IOptions<ApiOptions> options) : ControllerBase
{
    [HttpGet("workers")]
    public async Task<IReadOnlyList<WorkerDto>> Workers(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var rows = await db.Workers.AsNoTracking().OrderByDescending(w => w.LastSeenUtc).Take(100).ToListAsync(ct);
        return rows.Select(w => ToDto(w, now)).ToList();
    }

    /// <summary>
    /// Worker nodes grouped by application, with each online node's registered services (WorkerJobs) and what is running
    /// right now. Four queries in total, whatever the number of workers.
    /// </summary>
    [HttpGet("workers/applications")]
    public async Task<WorkerApplicationsDto> WorkerApplications(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var rows = await db.Workers.AsNoTracking().Where(w => w.LastSeenUtc >= now.AddHours(-24))
            .OrderByDescending(w => w.LastSeenUtc).Take(300).ToListAsync(ct);
        var dtos = rows.Select(w => (Row: w, Dto: ToDto(w, now))).ToList();
        var onlineIds = dtos.Where(d => d.Dto.State == "online").Select(d => d.Row.WorkerId).ToList();

        var caps = onlineIds.Count == 0 ? [] : await db.WorkerJobs.AsNoTracking().Where(c => onlineIds.Contains(c.WorkerId))
            .Select(c => new { c.WorkerId, c.JobKey }).ToListAsync(ct);
        var running = onlineIds.Count == 0 ? [] : await db.Runs.AsNoTracking()
            .Where(r => r.Status == RunStatus.Running && r.WorkerId != null && onlineIds.Contains(r.WorkerId))
            .GroupBy(r => new { WorkerId = r.WorkerId!, r.JobKey }).Select(g => new { g.Key.WorkerId, g.Key.JobKey, Count = g.Count() }).ToListAsync(ct);
        var keys = caps.Select(c => c.JobKey).Concat(running.Select(r => r.JobKey)).Distinct().ToList();
        var defs = keys.Count == 0 ? new Dictionary<string, (string Name, bool Enabled)>()
            : (await db.Definitions.AsNoTracking().Where(d => keys.Contains(d.Key)).Select(d => new { d.Key, d.DisplayName, d.Enabled }).ToListAsync(ct))
                .ToDictionary(d => d.Key, d => (Name: d.DisplayName, d.Enabled));

        var capsByWorker = caps.ToLookup(c => c.WorkerId, c => c.JobKey);
        var runningByWorker = running.ToLookup(r => r.WorkerId);

        WorkerNodeDto Node(WorkerHeartbeat w, WorkerDto dto)
        {
            var counts = runningByWorker[w.WorkerId].ToDictionary(r => r.JobKey, r => r.Count);
            var serviceKeys = capsByWorker[w.WorkerId].Union(counts.Keys).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
            var services = serviceKeys.Select(k => new NodeServiceDto(
                k, defs.TryGetValue(k, out var d) ? d.Name : k, defs.TryGetValue(k, out var e) && e.Enabled, counts.GetValueOrDefault(k))).ToList();
            return new WorkerNodeDto(w.WorkerId, w.WorkerName, w.Host, dto.State, w.StartedUtc, w.LastSeenUtc, dto.AgeSeconds,
                services.Count, services.Count(s => s.RunningCount > 0), services.Sum(s => s.RunningCount), services);
        }

        var groups = dtos.GroupBy(d => d.Row.ApplicationName, StringComparer.Ordinal).Select(g =>
        {
            var nodes = g.Where(d => d.Dto.State != "offline").OrderBy(d => d.Row.WorkerName).ThenBy(d => d.Row.StartedUtc)
                .Select(d => Node(d.Row, d.Dto)).ToList();
            var online = nodes.Where(n => n.State == "online").ToList();
            var previous = g.Where(d => d.Dto.State == "offline").OrderByDescending(d => d.Row.LastSeenUtc)
                .Select(d => new PreviousInstanceDto(d.Row.WorkerId, d.Row.WorkerName, d.Row.Host, d.Dto.State, d.Row.LastSeenUtc, d.Dto.AgeSeconds)).ToList();
            return new ApplicationGroupDto(g.Key, online.Count,
                online.SelectMany(n => n.Services).Select(s => s.JobKey).Distinct().Count(),
                online.SelectMany(n => n.Services).Where(s => s.RunningCount > 0).Select(s => s.JobKey).Distinct().Count(),
                nodes, previous);
        }).OrderByDescending(a => a.OnlineNodes > 0).ThenBy(a => a.ApplicationName, StringComparer.OrdinalIgnoreCase).ToList();

        return new WorkerApplicationsDto(now, groups);
    }

    [HttpGet("summary")]
    public async Task<SummaryDto> Summary(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var since = now.AddHours(-24);

        var live = (await WorkerCapacity.LiveJobKeys(db, now.AddSeconds(-options.Value.WorkerStaleSeconds)).ToListAsync(ct)).ToHashSet();
        var defs = await db.Definitions.AsNoTracking().Select(d => new { d.Key, d.Enabled }).ToListAsync(ct);
        var running = await db.Runs.CountAsync(r => r.Status == RunStatus.Running, ct);
        var queued = await db.Runs.CountAsync(r => r.Status == RunStatus.Queued, ct);
        var overdueCutoff = now.AddSeconds(-30);
        var overdueKeys = await db.Schedules.Where(s => s.Enabled && s.NextDueUtc < overdueCutoff
            && db.Definitions.Any(d => d.Key == s.JobKey && d.Enabled)).Select(s => s.JobKey).ToListAsync(ct);
        var overdue = overdueKeys.Count(live.Contains);

        // Finished runs in the last 24h, projected to the two columns needed for the hourly trend.
        var finished = await db.Runs.AsNoTracking()
            .Where(r => r.EndedUtc >= since && (r.Status == RunStatus.Succeeded || r.Status == RunStatus.Failed
                        || r.Status == RunStatus.TimedOut || r.Status == RunStatus.Abandoned))
            .Select(r => new { r.EndedUtc, r.Status }).ToListAsync(ct);
        var ok = finished.Count(f => f.Status == RunStatus.Succeeded);
        var bad = finished.Count - ok;

        var hours = Enumerable.Range(0, 24).Select(i => new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc).AddHours(-23 + i));
        var trend = hours.Select(h =>
        {
            var inHour = finished.Where(f => f.EndedUtc >= h && f.EndedUtc < h.AddHours(1)).ToList();
            var s = inHour.Count(f => f.Status == RunStatus.Succeeded);
            return new TrendPointDto(h, s, inHour.Count - s);
        }).ToList();

        var workers = (await db.Workers.AsNoTracking().Where(w => w.LastSeenUtc >= now.AddHours(-1)).ToListAsync(ct))
            .Select(w => ToDto(w, now)).ToList();

        return new SummaryDto(defs.Count, defs.Count(d => d.Enabled), defs.Count(d => !live.Contains(d.Key)), running, queued, overdue,
            ok, bad, finished.Count == 0 ? null : Math.Round(100.0 * ok / finished.Count, 1),
            workers.Count(w => w.State == "online"), workers.Count(w => w.State != "online"), trend);
    }

    [HttpGet("audit")]
    [Authorize(Policy = Policies.Administrator)]
    public async Task<IActionResult> Audit([FromQuery] int take = 50, CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 200);
        var rows = await db.Audit.AsNoTracking().OrderByDescending(a => a.Id).Take(take).ToListAsync(ct);
        return Ok(rows);
    }

    private WorkerDto ToDto(WorkerHeartbeat w, DateTime now)
    {
        var age = (int)Math.Max(0, (now - w.LastSeenUtc).TotalSeconds);
        var stale = options.Value.WorkerStaleSeconds;
        var state = w.StoppedUtc != null ? "offline" : age <= stale ? "online" : age <= stale * 4 ? "stale" : "offline";
        return new WorkerDto(w.WorkerId, w.Host, w.StartedUtc, w.LastSeenUtc, w.RunningCount, age, state, w.ApplicationName, w.WorkerName);
    }
}
