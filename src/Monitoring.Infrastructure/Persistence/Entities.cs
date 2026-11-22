using Monitoring.Contracts;

namespace Monitoring.Infrastructure.Persistence;

public class JobDefinition
{
    public string Key { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>False when no registered implementation exists in the worker host.</summary>
    public bool IsAvailable { get; set; } = true;
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
    /// <summary>Attempts started so far (0 while the first attempt is still queued).</summary>
    public int Attempt { get; set; }
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

public class AuditEntry
{
    public long Id { get; set; }
    public DateTime Utc { get; set; }
    public string Actor { get; set; } = "";
    public string Action { get; set; } = "";
    public string Target { get; set; } = "";
    public string? Details { get; set; }
}
