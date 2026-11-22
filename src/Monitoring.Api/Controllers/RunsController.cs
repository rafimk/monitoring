using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Monitoring.Contracts;
using Monitoring.Infrastructure.Persistence;

namespace Monitoring.Api.Controllers;

[ApiController]
[Route("api/v1/runs")]
[Authorize(Policy = Policies.Viewer)]
public class RunsController(MonitoringDbContext db) : ControllerBase
{
    private const int MaxPageSize = 200;

    /// <summary>Server-side filtered, paginated run history (newest first).</summary>
    [HttpGet]
    public async Task<ActionResult<PagedResult<RunDto>>> List(
        [FromQuery] string? jobKey, [FromQuery] RunStatus? status, [FromQuery] TriggerSource? trigger,
        [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
    {
        if (page < 1) return BadRequestProblem("page must be >= 1.");
        if (pageSize is < 1 or > MaxPageSize) return BadRequestProblem($"pageSize must be between 1 and {MaxPageSize}.");
        if (fromUtc is not null && toUtc is not null && fromUtc > toUtc) return BadRequestProblem("fromUtc must not be after toUtc.");

        var q = db.Runs.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(jobKey)) q = q.Where(r => r.JobKey == jobKey);
        if (status is { } s) q = q.Where(r => r.Status == s);
        if (trigger is { } t) q = q.Where(r => r.Trigger == t);
        if (fromUtc is { } f) q = q.Where(r => r.QueuedUtc >= f.ToUniversalTime());
        if (toUtc is { } to) q = q.Where(r => r.QueuedUtc <= to.ToUniversalTime());

        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(r => r.QueuedUtc).ThenByDescending(r => r.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(r => ToDto(r)).ToListAsync(ct);
        return new PagedResult<RunDto>(items, total, page, pageSize);
    }

    [HttpGet("{id:guid}", Name = "GetRun")]
    public async Task<ActionResult<RunDetailDto>> Get(Guid id, CancellationToken ct)
    {
        var run = await db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (run is null) return Problem($"Run '{id}' was not found.", statusCode: 404, title: "Not found");
        var attempts = await db.Attempts.AsNoTracking().Where(a => a.RunId == id).OrderBy(a => a.Attempt)
            .Select(a => new AttemptDto(a.Attempt, a.Status, a.StartedUtc, a.EndedUtc, a.WorkerId, a.Error)).ToListAsync(ct);
        return new RunDetailDto(ToDto(run), attempts);
    }

    private static RunDto ToDto(JobRun r) => new(r.Id, r.JobKey, r.Trigger, r.Status, r.ScheduledUtc, r.QueuedUtc,
        r.StartedUtc, r.EndedUtc, r.DurationMs, r.Attempt, r.MaxAttempts, r.WorkerId, r.CorrelationId, r.RequestedBy, r.Error);

    private ObjectResult BadRequestProblem(string detail) => Problem(detail, statusCode: 400, title: "Invalid request");
}
