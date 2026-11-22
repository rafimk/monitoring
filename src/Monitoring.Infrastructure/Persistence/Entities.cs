using Monitoring.Contracts;

namespace Monitoring.Infrastructure.Persistence;

public class JobDefinition
{
    public string Key { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public int TimeoutSeconds { get; set; } = 300;
    public int MaxAttempts { get; set; } = 3;
    public int RetryBaseDelaySeconds { get; set; } = 10;
    public ConcurrencyPolicy ConcurrencyPolicy { get; set; } = ConcurrencyPolicy.SkipIfRunning;
    public MissedRunPolicy MissedRunPolicy { get; set; } = MissedRunPolicy.CoalesceOne;
    public string TimeZoneId { get; set; } = "UTC";
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public class JobSchedule
{
    public Guid Id { get; set; }
    public string JobKey { get; set; } = "";
    public int IntervalSeconds { get; set; }
    public DateTime NextDueUtc { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTime? LastDispatchedUtc { get; set; }
}

public class JobRun
{
    public Guid Id { get; set; }
    public string JobKey { get; set; } = "";
    public TriggerSource Trigger { get; set; }
    public RunStatus Status { get; set; }
    public DateTime ScheduledUtc { get; set; }
    public DateTime QueuedUtc { get; set; }
    public DateTime NotBeforeUtc { get; set; }
    public DateTime? StartedUtc { get; set; }
    public DateTime? EndedUtc { get; set; }
    public long? DurationMs { get; set; }
    /// <summary>
    /// Sequence number of the most recently started attempt (0 while the first attempt is still queued).
    /// Strictly increasing and never decremented, so it always matches <see cref="RunAttempt.Attempt"/>.
    /// It also acts as the fencing token for every write made on behalf of a running attempt.
    /// </summary>
    public int Attempt { get; set; }
    /// <summary>
    /// Attempts that count toward <see cref="MaxAttempts"/>. A graceful-shutdown interruption is recorded in the
    /// attempt history but not counted, so it never exhausts the retry budget.
    /// </summary>
    public int CountedAttempts { get; set; }
    public int MaxAttempts { get; set; } = 1;
    public string? WorkerId { get; set; }
    public DateTime? LeaseExpiresUtc { get; set; }
    public string CorrelationId { get; set; } = "";
    public string? RequestedBy { get; set; }
    public string? Error { get; set; }
}

public class RunAttempt
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public int Attempt { get; set; }
    public RunStatus Status { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime? EndedUtc { get; set; }
    public string WorkerId { get; set; } = "";
    public string? Error { get; set; }
}

public class WorkerHeartbeat
{
    public string WorkerId { get; set; } = "";
    public string Host { get; set; } = "";
    public DateTime StartedUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public int RunningCount { get; set; }
}

/// <summary>A job key that a worker instance can execute (its registered implementations).</summary>
public class WorkerJob
{
    public string WorkerId { get; set; } = "";
    public string JobKey { get; set; } = "";
}

public class AuditEntry
{
    public long Id { get; set; }
    public DateTime Utc { get; set; }
    public string Actor { get; set; } = "";
    public string Action { get; set; } = "";
    public string Target { get; set; } = "";
    public string? Details { get; set; }
}

public enum ChangeKind { Run = 1, Job = 2, Workers = 3 }

/// <summary>
/// Transactional outbox / cross-process change feed. The worker and the API write a row in the same transaction as the
/// state change it describes; the API's dispatcher reads pending rows and pushes small notifications to dashboard
/// clients. Rows carry only "what changed" (never the record itself): clients refetch from the normal APIs.
/// </summary>
public class ChangeEvent
{
    public long Id { get; set; }
    public ChangeKind Kind { get; set; }
    /// <summary>Job the change belongs to (null for worker-wide changes).</summary>
    public string? JobKey { get; set; }
    public Guid? RunId { get; set; }
    /// <summary>Short machine-readable cause, e.g. queued, started, retry, succeeded, recovered, enabled.</summary>
    public string Reason { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public DateTime? DispatchedUtc { get; set; }
}

/// <summary>
/// Bookkeeping for scheduled maintenance (one row per task, e.g. "retention"). It lets a restarted or newly elected worker
/// know whether the last scheduled run already happened, so it neither repeats nor silently skips the work.
/// </summary>
public class MaintenanceState
{
    public string Task { get; set; } = "";
    /// <summary>When this row was first created: the baseline for "was a run missed?" before any run has succeeded.</summary>
    public DateTime InitializedUtc { get; set; }
    public DateTime? LastSuccessUtc { get; set; }
    public long? LastDurationMs { get; set; }
    /// <summary>Counts deleted by the last successful run, e.g. "JobRuns=12, RunAttempts=30".</summary>
    public string? LastSummary { get; set; }
}
