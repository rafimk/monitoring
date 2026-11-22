namespace Monitoring.Worker;

/// <summary>
/// Nightly data-retention settings (<c>Worker:Retention</c>). Each data kind has its own policy; none is deleted under
/// another kind's policy. Queued/Running runs and undispatched change-feed events are never deleted, whatever their age.
/// </summary>
public sealed class RetentionOptions
{
    public const int MaxDays = 36500;

    /// <summary>Master switch for the nightly cleanup. Manual execution of the stored procedure is always possible.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Terminal job runs (and their attempts) whose EndedUtc is older than this are deleted.</summary>
    public int JobHistoryRetentionDays { get; set; } = 30;

    /// <summary>Audit entries older than this are deleted. Longer than job history by default: it records who changed what.</summary>
    public int AuditRetentionDays { get; set; } = 365;

    /// <summary>Change-feed events that were already dispatched and are older than this are deleted. Pending events are never deleted.</summary>
    public int ChangeFeedRetentionDays { get; set; } = 7;

    /// <summary>Rows deleted per transaction (per table). Small enough to keep locks short and log growth bounded.</summary>
    public int BatchSize { get; set; } = 1000;

    /// <summary>Pause between batches so cleanup yields I/O to normal work.</summary>
    public int BatchPauseMilliseconds { get; set; } = 50;

    /// <summary>Local wall-clock time of the nightly run, "HH:mm" (24 h).</summary>
    public string RunAt { get; set; } = "02:00";

    /// <summary>
    /// Time zone for <see cref="RunAt"/>: an IANA id (e.g. "Europe/Berlin") or a Windows id; "UTC" by default.
    /// A local time skipped by a DST change runs at the first valid instant after the gap; a repeated local time runs on
    /// its first occurrence. A UTC schedule has no DST at all.
    /// </summary>
    public string TimeZoneId { get; set; } = "UTC";

    public TimeSpan RunAtTime => TimeSpan.ParseExact(RunAt, @"hh\:mm", System.Globalization.CultureInfo.InvariantCulture);
}
