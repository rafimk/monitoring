using System.ComponentModel.DataAnnotations;
using Monitoring.Contracts;

namespace Monitoring.Api;

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

public sealed record LastRunDto(Guid Id, RunStatus Status, DateTime? StartedUtc, DateTime? EndedUtc, long? DurationMs);

public sealed record JobSummaryDto(
    string Key, string DisplayName, string Description, bool Enabled, bool IsAvailable,
    int? IntervalSeconds, DateTime? NextDueUtc, bool Overdue, int RunningCount, LastRunDto? LastRun);

public sealed record JobDetailDto(
    string Key, string DisplayName, string Description, bool Enabled, bool IsAvailable,
    int? IntervalSeconds, DateTime? NextDueUtc, bool Overdue,
    int TimeoutSeconds, int MaxAttempts, int RetryBaseDelaySeconds,
    ConcurrencyPolicy ConcurrencyPolicy, MissedRunPolicy MissedRunPolicy, string TimeZoneId,
    int RunningCount, LastRunDto? LastRun, DateTime? LastSuccessUtc);

public sealed record RunDto(
    Guid Id, string JobKey, TriggerSource Trigger, RunStatus Status, DateTime ScheduledUtc, DateTime QueuedUtc,
    DateTime? StartedUtc, DateTime? EndedUtc, long? DurationMs, int Attempt, int MaxAttempts,
    string? WorkerId, string CorrelationId, string? RequestedBy, string? Error);

public sealed record AttemptDto(int Attempt, RunStatus Status, DateTime StartedUtc, DateTime? EndedUtc, string WorkerId, string? Error);

public sealed record RunDetailDto(RunDto Run, IReadOnlyList<AttemptDto> Attempts);

public sealed record WorkerDto(string WorkerId, string Host, DateTime StartedUtc, DateTime LastSeenUtc, int RunningCount, int AgeSeconds, string State);

public sealed record TrendPointDto(DateTime HourUtc, int Succeeded, int Failed);

public sealed record SummaryDto(
    int TotalJobs, int EnabledJobs, int UnavailableJobs, int Running, int Queued, int Overdue,
    int Succeeded24h, int Failed24h, double? SuccessRate24h,
    int WorkersOnline, int WorkersStale, IReadOnlyList<TrendPointDto> Trend);

public sealed class UpdateJobRequest
{
    [Range(10, 86400 * 7)] public int? IntervalSeconds { get; set; }
    [Range(1, 86400)] public int? TimeoutSeconds { get; set; }
    [Range(1, 20)] public int? MaxAttempts { get; set; }
    [Range(1, 3600)] public int? RetryBaseDelaySeconds { get; set; }
    public ConcurrencyPolicy? ConcurrencyPolicy { get; set; }
    public MissedRunPolicy? MissedRunPolicy { get; set; }
}

public sealed class DevTokenRequest
{
    [Required, StringLength(100)] public string Username { get; set; } = "";
    [Required] public string Role { get; set; } = Roles.Viewer;
}
