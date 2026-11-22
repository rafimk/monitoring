using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Monitoring.Worker;

public sealed class WorkerOptions
{
    public const string Section = "Worker";

    /// <summary>
    /// Optional human-readable name (shown in the dashboard). It is only a prefix: every process instance appends a
    /// random suffix, so two workers configured with the same name still have distinct identities and can never
    /// overwrite each other's heartbeat or hold each other's run leases.
    /// </summary>
    public string? WorkerId { get; set; }

    [Range(1, 300)] public int PollIntervalSeconds { get; set; } = 2;
    [Range(5, 3600)] public int LeaseSeconds { get; set; } = 60;
    [Range(1, 600)] public int HeartbeatSeconds { get; set; } = 15;
    [Range(1, 256)] public int MaxParallelism { get; set; } = 4;
    [Range(1, 1000)] public int SchedulerBatchSize { get; set; } = 50;
    [Range(1, 600)] public int ShutdownGraceSeconds { get; set; } = 30;

    /// <summary>Development convenience only. In production apply migrations as a controlled deployment step.</summary>
    public bool ApplyMigrationsOnStartup { get; set; }
    /// <summary>Development only: create the sample schedules.</summary>
    public bool SeedSampleData { get; set; }

    /// <summary>A job is cancelled this long before its lease would expire if the lease cannot be renewed.</summary>
    public TimeSpan LeaseSafetyMargin => TimeSpan.FromSeconds(Math.Max(0.5, LeaseSeconds / 4.0));
    /// <summary>Pause between renewal retries after a transient failure.</summary>
    public TimeSpan RenewalRetryDelay => TimeSpan.FromMilliseconds(500);
}

public sealed partial class WorkerOptionsValidator : IValidateOptions<WorkerOptions>
{
    [GeneratedRegex("^[A-Za-z0-9._-]{1,80}$")]
    private static partial Regex NamePattern();

    public ValidateOptionsResult Validate(string? name, WorkerOptions o)
    {
        // The lease must outlive at least two heartbeats, otherwise healthy runs would be recovered as abandoned.
        if (o.LeaseSeconds < o.HeartbeatSeconds * 2)
            return ValidateOptionsResult.Fail("Worker:LeaseSeconds must be at least twice Worker:HeartbeatSeconds.");
        if (!string.IsNullOrWhiteSpace(o.WorkerId) && !NamePattern().IsMatch(o.WorkerId))
            return ValidateOptionsResult.Fail("Worker:WorkerId may only contain letters, digits, '.', '_' and '-' (max 80 characters).");
        return ValidateOptionsResult.Success;
    }
}

/// <summary>Unique identity of this worker process instance.</summary>
public sealed class WorkerIdentity(IOptions<WorkerOptions> options)
{
    /// <summary>"{name}-{random}" — unique per process even when the configured name is shared.</summary>
    public string Id { get; } = Build(options.Value.WorkerId);
    public DateTime StartedUtc { get; } = DateTime.UtcNow;

    private static string Build(string? configured)
    {
        var name = string.IsNullOrWhiteSpace(configured) ? $"{Environment.MachineName}-{Environment.ProcessId}" : configured.Trim();
        if (name.Length > 80) name = name[..80];
        return $"{name}-{Guid.NewGuid().ToString("N")[..8]}";
    }
}
