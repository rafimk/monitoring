namespace Monitoring.Api.Realtime;

public static class HubPaths
{
    public const string Monitoring = "/hubs/monitoring";
}

/// <summary>
/// Notifications say WHAT changed, never carry the record: the dashboard refetches from the normal (authorized) REST
/// endpoints. A message is therefore safe to duplicate, drop or reorder.
/// </summary>
public interface IMonitoringClient
{
    /// <summary>A run changed. RunId/JobKey are null when many runs changed at once (coalesced flood protection).</summary>
    Task RunChanged(RunChangedMessage message);
    /// <summary>A job definition or its schedule changed (JobKey null = potentially all jobs, e.g. availability).</summary>
    Task JobChanged(JobChangedMessage message);
    /// <summary>A worker appeared, stopped or changed capabilities.</summary>
    Task WorkersChanged(WorkersChangedMessage message);
    /// <summary>Dashboard summary numbers may have changed.</summary>
    Task SummaryChanged();
}

public sealed record RunChangedMessage(Guid? RunId, string? JobKey, string Reason);
public sealed record JobChangedMessage(string? JobKey, string Reason);
public sealed record WorkersChangedMessage(string Reason);
