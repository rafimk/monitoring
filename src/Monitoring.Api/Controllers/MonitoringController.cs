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

    [HttpGet("summary")]
    public async Task<SummaryDto> Summary(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var since = now.AddHours(-24);

        var defs = await db.Definitions.AsNoTracking().Select(d => new { d.Enabled, d.IsAvailable }).ToListAsync(ct);
        var running = await db.Runs.CountAsync(r => r.Status == RunStatus.Running, ct);
        var queued = await db.Runs.CountAsync(r => r.Status == RunStatus.Queued, ct);
        var overdueCutoff = now.AddSeconds(-30);
        var overdue = await db.Schedules.CountAsync(s => s.Enabled && s.NextDueUtc < overdueCutoff
            && db.Definitions.Any(d => d.Key == s.JobKey && d.Enabled && d.IsAvailable), ct);

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

        return new SummaryDto(defs.Count, defs.Count(d => d.Enabled), defs.Count(d => !d.IsAvailable), running, queued, overdue,
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
        var state = age <= stale ? "online" : age <= stale * 4 ? "stale" : "offline";
        return new WorkerDto(w.WorkerId, w.Host, w.StartedUtc, w.LastSeenUtc, w.RunningCount, age, state);
    }
}
