using System.ComponentModel.DataAnnotations;

namespace Monitoring.Worker;

public sealed class WorkerOptions
{
    public const string Section = "Worker";

    /// <summary>Optional stable id; defaults to host-pid-random so each process instance is distinct.</summary>
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
}

public sealed class WorkerOptionsValidator : Microsoft.Extensions.Options.IValidateOptions<WorkerOptions>
{
    public Microsoft.Extensions.Options.ValidateOptionsResult Validate(string? name, WorkerOptions o)
    {
        // Lease must outlive at least two heartbeats or healthy runs would be recovered as abandoned.
        if (o.LeaseSeconds < o.HeartbeatSeconds * 2)
            return Microsoft.Extensions.Options.ValidateOptionsResult.Fail(
                "Worker:LeaseSeconds must be at least twice Worker:HeartbeatSeconds.");
        return Microsoft.Extensions.Options.ValidateOptionsResult.Success;
    }
}

/// <summary>Unique identity of this worker process instance.</summary>
public sealed class WorkerIdentity(Microsoft.Extensions.Options.IOptions<WorkerOptions> options)
{
    public string Id { get; } = !string.IsNullOrWhiteSpace(options.Value.WorkerId)
        ? options.Value.WorkerId!
        : $"{Environment.MachineName}-{Environment.ProcessId}-{Guid.NewGuid().ToString("N")[..6]}";
    public DateTime StartedUtc { get; } = DateTime.UtcNow;
}
