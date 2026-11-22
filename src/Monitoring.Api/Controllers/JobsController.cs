using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Monitoring.Contracts;
using Monitoring.Infrastructure.Persistence;

namespace Monitoring.Api.Controllers;

[ApiController]
[Route("api/v1/jobs")]
[Authorize(Policy = Policies.Viewer)]
public class JobsController(MonitoringDbContext db) : ControllerBase
{
    private const int OverdueGraceSeconds = 30;

    [HttpGet]
    public async Task<IReadOnlyList<JobSummaryDto>> List(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var defs = await db.Definitions.AsNoTracking().OrderBy(d => d.Key).ToListAsync(ct);
        var schedules = await db.Schedules.AsNoTracking().ToDictionaryAsync(s => s.JobKey, ct);
        var running = await db.Runs.AsNoTracking().Where(r => r.Status == RunStatus.Running)
            .GroupBy(r => r.JobKey).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var last = await LastRunsAsync(ct);

        return defs.Select(d =>
        {
            schedules.TryGetValue(d.Key, out var s);
            last.TryGetValue(d.Key, out var lr);
            return new JobSummaryDto(d.Key, d.DisplayName, d.Description, d.Enabled, d.IsAvailable,
                s?.IntervalSeconds, s?.NextDueUtc, IsOverdue(d, s, now),
                running.GetValueOrDefault(d.Key), lr);
        }).ToList();
    }

    [HttpGet("{key}")]
    public async Task<ActionResult<JobDetailDto>> Get(string key, CancellationToken ct)
    {
        var d = await db.Definitions.AsNoTracking().FirstOrDefaultAsync(x => x.Key == key, ct);
        if (d is null) return NotFoundProblem(key);
        var s = await db.Schedules.AsNoTracking().FirstOrDefaultAsync(x => x.JobKey == key, ct);
        var running = await db.Runs.CountAsync(r => r.JobKey == key && r.Status == RunStatus.Running, ct);
        var last = (await LastRunsAsync(ct, key)).GetValueOrDefault(key);
        var lastSuccess = await db.Runs.Where(r => r.JobKey == key && r.Status == RunStatus.Succeeded)
            .MaxAsync(r => (DateTime?)r.EndedUtc, ct);

        return new JobDetailDto(d.Key, d.DisplayName, d.Description, d.Enabled, d.IsAvailable,
            s?.IntervalSeconds, s?.NextDueUtc, IsOverdue(d, s, DateTime.UtcNow),
            d.TimeoutSeconds, d.MaxAttempts, d.RetryBaseDelaySeconds, d.ConcurrencyPolicy, d.MissedRunPolicy, d.TimeZoneId,
            running, last, lastSuccess);
    }

    /// <summary>Enqueues a durable manual run; the worker picks it up (the API never executes jobs).</summary>
    [HttpPost("{key}/run")]
    [Authorize(Policy = Policies.Operator)]
    public async Task<IActionResult> RunNow(string key, CancellationToken ct)
    {
        var d = await db.Definitions.FirstOrDefaultAsync(x => x.Key == key, ct);
        if (d is null) return NotFoundProblem(key);
        if (!d.IsAvailable) return ConflictProblem("Job has no registered implementation on any worker.");
        if (!d.Enabled) return ConflictProblem("Job is disabled. Enable it before running.");
        if (await db.Runs.AnyAsync(r => r.JobKey == key && r.Trigger == TriggerSource.Manual && r.Status == RunStatus.Queued, ct))
            return ConflictProblem("A manual run for this job is already queued.");

        var now = DateTime.UtcNow;
        var run = new JobRun
        {
            Id = Guid.NewGuid(), JobKey = key, Trigger = TriggerSource.Manual, Status = RunStatus.Queued,
            ScheduledUtc = now, QueuedUtc = now, NotBeforeUtc = now, MaxAttempts = d.MaxAttempts,
            CorrelationId = HttpContext.TraceIdentifier.Replace(":", "").ToLowerInvariant(),
            RequestedBy = Actor,
        };
        db.Runs.Add(run);
        Audit("job.run-now", key, null);
        await db.SaveChangesAsync(ct);
        return AcceptedAtAction(nameof(RunsController.Get), "Runs", new { id = run.Id }, new { runId = run.Id });
    }

    [HttpPost("{key}/enable")]
    [Authorize(Policy = Policies.Operator)]
    public Task<IActionResult> Enable(string key, CancellationToken ct) => SetEnabled(key, true, ct);

    [HttpPost("{key}/disable")]
    [Authorize(Policy = Policies.Operator)]
    public Task<IActionResult> Disable(string key, CancellationToken ct) => SetEnabled(key, false, ct);

    private async Task<IActionResult> SetEnabled(string key, bool enabled, CancellationToken ct)
    {
        var d = await db.Definitions.FirstOrDefaultAsync(x => x.Key == key, ct);
        if (d is null) return NotFoundProblem(key);
        if (d.Enabled != enabled)
        {
            d.Enabled = enabled;
            d.UpdatedUtc = DateTime.UtcNow;
            Audit(enabled ? "job.enable" : "job.disable", key, null);
            await db.SaveChangesAsync(ct);
        }
        return NoContent();
    }

    [HttpPut("{key}")]
    [Authorize(Policy = Policies.Administrator)]
    public async Task<IActionResult> Update(string key, UpdateJobRequest req, CancellationToken ct)
    {
        var d = await db.Definitions.FirstOrDefaultAsync(x => x.Key == key, ct);
        if (d is null) return NotFoundProblem(key);

        if (req.TimeoutSeconds is { } t) d.TimeoutSeconds = t;
        if (req.MaxAttempts is { } m) d.MaxAttempts = m;
        if (req.RetryBaseDelaySeconds is { } r) d.RetryBaseDelaySeconds = r;
        if (req.ConcurrencyPolicy is { } c) d.ConcurrencyPolicy = c;
        if (req.MissedRunPolicy is { } p) d.MissedRunPolicy = p;
        d.UpdatedUtc = DateTime.UtcNow;

        if (req.IntervalSeconds is { } i)
        {
            var s = await db.Schedules.FirstOrDefaultAsync(x => x.JobKey == key, ct);
            if (s is null)
                db.Schedules.Add(new JobSchedule { Id = Guid.NewGuid(), JobKey = key, IntervalSeconds = i, NextDueUtc = DateTime.UtcNow.AddSeconds(i), Enabled = true });
            else if (s.IntervalSeconds != i)
            {
                s.IntervalSeconds = i;
                s.NextDueUtc = DateTime.UtcNow.AddSeconds(i); // restart the cadence from the change
            }
        }

        Audit("job.update", key, JsonSerializer.Serialize(req));
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private async Task<Dictionary<string, LastRunDto>> LastRunsAsync(CancellationToken ct, string? key = null)
    {
        var q = db.Runs.AsNoTracking().Where(r => key == null || r.JobKey == key);
        var rows = await q.Where(r => r.QueuedUtc == db.Runs.Where(x => x.JobKey == r.JobKey).Max(x => x.QueuedUtc))
            .Select(r => new { r.JobKey, Dto = new LastRunDto(r.Id, r.Status, r.StartedUtc, r.EndedUtc, r.DurationMs) })
            .ToListAsync(ct);
        return rows.GroupBy(x => x.JobKey).ToDictionary(g => g.Key, g => g.First().Dto);
    }

    private static bool IsOverdue(JobDefinition d, JobSchedule? s, DateTime now) =>
        d.Enabled && d.IsAvailable && s is { Enabled: true } && s.NextDueUtc < now.AddSeconds(-OverdueGraceSeconds);

    private string Actor => User.Identity?.Name ?? "unknown";

    private void Audit(string action, string target, string? details) =>
        db.Audit.Add(new AuditEntry { Utc = DateTime.UtcNow, Actor = Actor, Action = action, Target = target, Details = details });

    private ObjectResult NotFoundProblem(string key) => Problem($"Job '{key}' was not found.", statusCode: 404, title: "Not found");
    private ObjectResult ConflictProblem(string detail) => Problem(detail, statusCode: 409, title: "Conflict");
}
