namespace Monitoring.Contracts;

public enum RunStatus { Queued, Running, Succeeded, Failed, Cancelled, TimedOut, Abandoned }

public enum TriggerSource { Schedule, Manual, Api }

public enum MissedRunPolicy { Skip, CoalesceOne, CatchUpCapped }

public enum ConcurrencyPolicy { SkipIfRunning, QueueIfRunning, AllowParallel }

/// <summary>Context passed to each job execution.</summary>
public sealed record JobExecutionContext(
    Guid RunId,
    string JobKey,
    int Attempt,
    TriggerSource Trigger,
    string CorrelationId,
    DateTime ScheduledUtc);

public sealed record JobResult(bool Success, string? Message = null)
{
    public static JobResult Ok(string? message = null) => new(true, message);
    public static JobResult Fail(string message) => new(false, message);
}

/// <summary>Implementations must be idempotent: execution is at-least-once.</summary>
public interface IJob
{
    Task<JobResult> ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken);
}

/// <summary>Registers a job implementation under a stable key.</summary>
public sealed record JobRegistration(string Key, string DisplayName, string Description, Type ImplementationType);
