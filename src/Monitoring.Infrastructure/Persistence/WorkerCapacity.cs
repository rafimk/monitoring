namespace Monitoring.Infrastructure.Persistence;

/// <summary>
/// Job availability is not stored on the job definition: it is derived from which live workers advertise an
/// implementation. Workers with different registrations can therefore coexist, and a job only counts as
/// unavailable when no live worker can run it.
/// </summary>
public static class WorkerCapacity
{
    /// <summary>Distinct job keys that at least one worker seen since <paramref name="liveSinceUtc"/> can execute.</summary>
    public static IQueryable<string> LiveJobKeys(MonitoringDbContext db, DateTime liveSinceUtc) =>
        (from wj in db.WorkerJobs
         join w in db.Workers on wj.WorkerId equals w.WorkerId
         where w.LastSeenUtc >= liveSinceUtc && w.StoppedUtc == null
         select wj.JobKey).Distinct();
}
