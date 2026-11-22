using Microsoft.EntityFrameworkCore;

namespace Monitoring.Infrastructure.Persistence;

/// <summary>Writes and purges <see cref="ChangeEvent"/> rows (the shared change feed between worker and API).</summary>
public static class ChangeFeed
{
    /// <summary>
    /// Stages a change event on the context. Call it before the SaveChanges / transaction commit that persists the state
    /// change itself, so the state and its notification commit (or roll back) together.
    /// </summary>
    public static void Record(MonitoringDbContext db, ChangeKind kind, string? jobKey, Guid? runId, string reason) =>
        db.ChangeEvents.Add(new ChangeEvent
        {
            Kind = kind, JobKey = jobKey, RunId = runId, Reason = reason, CreatedUtc = DateTime.UtcNow,
        });

    public static void Run(MonitoringDbContext db, Guid runId, string jobKey, string reason) => Record(db, ChangeKind.Run, jobKey, runId, reason);
    public static void Job(MonitoringDbContext db, string jobKey, string reason) => Record(db, ChangeKind.Job, jobKey, null, reason);
    public static void Workers(MonitoringDbContext db, string reason) => Record(db, ChangeKind.Workers, null, null, reason);
}
