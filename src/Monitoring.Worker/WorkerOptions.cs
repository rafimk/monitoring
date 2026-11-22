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

    /// <summary>
    /// The application this worker deployment serves (required). Dashboard nodes are grouped by it. It is distinct from
    /// <see cref="WorkerId"/> (stable node name) and from the per-process instance id. The default is a local-development
    /// placeholder: set it per deployment (for example "Billing").
    /// </summary>
    public string ApplicationName { get; set; } = DefaultApplicationName;
    public const string DefaultApplicationName = "monitoring-local";

    [Range(1, 300)] public int PollIntervalSeconds { get; set; } = 2;
    [Range(5, 3600)] public int LeaseSeconds { get; set; } = 60;
    [Range(1, 600)] public int HeartbeatSeconds { get; set; } = 15;
    [Range(1, 256)] public int MaxParallelism { get; set; } = 4;
    [Range(1, 1000)] public int SchedulerBatchSize { get; set; } = 50;
    [Range(1, 600)] public int ShutdownGraceSeconds { get; set; } = 30;

    /// <summary>
    /// Active/standby election. <see cref="WorkerLeaderMode.ActiveStandby"/> (default): only the node holding one
    /// SQL Server application lock schedules and executes; others wait as healthy standbys.
    /// <see cref="WorkerLeaderMode.Disabled"/>: every node is active (the claim/fencing protections still prevent duplicates).
    /// </summary>
    public WorkerLeaderMode LeaderMode { get; set; } = WorkerLeaderMode.ActiveStandby;

    /// <summary>Name of the application lock (database-scoped, so it only applies to the Monitoring database). Same on every node.</summary>
    public string LeaderLockResource { get; set; } = "monitoring-worker-leader";

    /// <summary>How often a standby (or a node that lost leadership) tries to acquire the lock.</summary>
    [Range(1, 300)] public int StandbyRetrySeconds { get; set; } = 5;

    /// <summary>How often the active node proves it still owns the lock. Must be at most half of <see cref="LeaseSeconds"/>.</summary>
    [Range(1, 300)] public int LeaderCheckSeconds { get; set; } = 5;

    /// <summary>Nightly data retention (<c>Worker:Retention</c>).</summary>
    public RetentionOptions Retention { get; set; } = new();

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
        if (string.IsNullOrWhiteSpace(o.ApplicationName) || !NamePattern().IsMatch(o.ApplicationName.Trim()))
            return ValidateOptionsResult.Fail("Worker:ApplicationName is required: letters, digits, '.', '_' and '-' (max 80 characters).");
        if (!Enum.IsDefined(o.LeaderMode))
            return ValidateOptionsResult.Fail("Worker:LeaderMode must be ActiveStandby or Disabled.");
        if (string.IsNullOrWhiteSpace(o.LeaderLockResource) || o.LeaderLockResource.Length > 255)
            return ValidateOptionsResult.Fail("Worker:LeaderLockResource must be 1-255 characters (it names the SQL Server application lock).");
        // The active node must be able to prove it still owns the lock well inside the run lease; otherwise a node that
        // lost the lock could keep renewing leases for most of a lease period before noticing.
        if (o.LeaderMode == WorkerLeaderMode.ActiveStandby && o.LeaderCheckSeconds * 2 > o.LeaseSeconds)
            return ValidateOptionsResult.Fail("Worker:LeaderCheckSeconds must be at most half of Worker:LeaseSeconds.");
        var retention = ValidateRetention(o.Retention);
        if (retention is not null) return ValidateOptionsResult.Fail(retention);
        return ValidateOptionsResult.Success;
    }

    private static string? ValidateRetention(RetentionOptions? r)
    {
        if (r is null) return "Worker:Retention is missing.";
        static bool Days(int d) => d is >= 1 and <= RetentionOptions.MaxDays;
        if (!Days(r.JobHistoryRetentionDays)) return $"Worker:Retention:JobHistoryRetentionDays must be between 1 and {RetentionOptions.MaxDays}.";
        if (!Days(r.AuditRetentionDays)) return $"Worker:Retention:AuditRetentionDays must be between 1 and {RetentionOptions.MaxDays}.";
        if (!Days(r.ChangeFeedRetentionDays)) return $"Worker:Retention:ChangeFeedRetentionDays must be between 1 and {RetentionOptions.MaxDays}.";
        if (r.BatchSize is < 1 or > 10000) return "Worker:Retention:BatchSize must be between 1 and 10000.";
        if (r.BatchPauseMilliseconds is < 0 or > 60000) return "Worker:Retention:BatchPauseMilliseconds must be between 0 and 60000.";
        if (!TimeSpan.TryParseExact(r.RunAt, @"hh\:mm", System.Globalization.CultureInfo.InvariantCulture, out var t) || t >= TimeSpan.FromDays(1))
            return "Worker:Retention:RunAt must be a 24-hour time such as \"02:00\".";
        try { TimeZoneInfo.FindSystemTimeZoneById(r.TimeZoneId ?? ""); }
        catch (Exception) { return $"Worker:Retention:TimeZoneId '{r.TimeZoneId}' is not a known time zone (use an IANA id such as \"Europe/Berlin\" or \"UTC\")."; }
        return null;
    }
}

/// <summary>Unique identity of this worker process instance.</summary>
public sealed class WorkerIdentity(IOptions<WorkerOptions> options)
{
    /// <summary>"{name}-{random}" — unique per process even when the configured name is shared.</summary>
    public string Id { get; } = Build(options.Value.WorkerId);
    public DateTime StartedUtc { get; } = DateTime.UtcNow;
    /// <summary>Application this node belongs to (Worker:ApplicationName).</summary>
    public string ApplicationName { get; } = options.Value.ApplicationName.Trim();
    /// <summary>Stable node name: the configured Worker:WorkerId, else the machine name. Same across restarts.</summary>
    public string Name { get; } = StableName(options.Value.WorkerId);

    private static string StableName(string? configured)
    {
        var name = string.IsNullOrWhiteSpace(configured) ? Environment.MachineName : configured.Trim();
        return name.Length > 80 ? name[..80] : name;
    }

    private static string Build(string? configured)
    {
        var name = string.IsNullOrWhiteSpace(configured) ? $"{Environment.MachineName}-{Environment.ProcessId}" : configured.Trim();
        if (name.Length > 80) name = name[..80];
        return $"{name}-{Guid.NewGuid().ToString("N")[..8]}";
    }
}

public enum WorkerLeaderMode
{
    /// <summary>Only the node holding the SQL Server application lock schedules and executes jobs; others are standbys.</summary>
    ActiveStandby = 0,
    /// <summary>No election: every node is active. Duplicate protection then rests only on the per-run claim and fencing.</summary>
    Disabled = 1,
}
