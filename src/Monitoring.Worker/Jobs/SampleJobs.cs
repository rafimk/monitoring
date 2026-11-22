using Monitoring.Contracts;

namespace Monitoring.Worker.Jobs;

/// <summary>Idempotent: it only logs, so a repeat after a crash is harmless.</summary>
public sealed class SampleJob(ILogger<SampleJob> logger) : IJob
{
    public async Task<JobResult> ExecuteAsync(JobExecutionContext context, CancellationToken ct)
    {
        logger.LogInformation("SampleJob run {RunId} attempt {Attempt} ({Trigger}) correlation {Correlation}",
            context.RunId, context.Attempt, context.Trigger, context.CorrelationId);
        await Task.Delay(TimeSpan.FromSeconds(2), ct);
        return JobResult.Ok("Sample work done");
    }
}

/// <summary>Runs longer than 30s so overlap/concurrency behaviour can be observed (2 min schedule).</summary>
public sealed class SampleSlowJob(ILogger<SampleSlowJob> logger) : IJob
{
    public async Task<JobResult> ExecuteAsync(JobExecutionContext context, CancellationToken ct)
    {
        logger.LogInformation("SampleSlowJob run {RunId} started", context.RunId);
        await Task.Delay(TimeSpan.FromSeconds(30), ct);
        return JobResult.Ok();
    }
}

/// <summary>Fails on its first attempt to demonstrate retry with backoff; succeeds on the second.</summary>
public sealed class SampleFlakyJob : IJob
{
    public Task<JobResult> ExecuteAsync(JobExecutionContext context, CancellationToken ct)
    {
        if (context.Attempt < 2)
            throw new InvalidOperationException("Simulated transient failure (password=hunter2 must be redacted)");
        return Task.FromResult(JobResult.Ok("Recovered on retry"));
    }
}
