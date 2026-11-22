using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Monitoring.Infrastructure.Persistence;

namespace Monitoring.Worker.Services;

/// <summary>Publishes worker liveness so the API/dashboard can show stale or offline workers.</summary>
public sealed class HeartbeatService(
    IServiceScopeFactory scopes, IOptions<WorkerOptions> options, WorkerIdentity identity,
    RunnerState state, ILogger<HeartbeatService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.HeartbeatSeconds));
        do
        {
            try { await BeatAsync(stop); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogWarning(ex, "Heartbeat failed"); }
        }
        while (await WaitAsync(timer, stop));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer t, CancellationToken ct)
    {
        try { return await t.WaitForNextTickAsync(ct); } catch (OperationCanceledException) { return false; }
    }

    private async Task BeatAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MonitoringDbContext>();
        var now = DateTime.UtcNow;
        var running = state.Running;

        var rows = await db.Workers.Where(w => w.WorkerId == identity.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.LastSeenUtc, now).SetProperty(w => w.RunningCount, running), ct);
        if (rows == 0)
        {
            db.Workers.Add(new WorkerHeartbeat
            {
                WorkerId = identity.Id, Host = Environment.MachineName,
                StartedUtc = identity.StartedUtc, LastSeenUtc = now, RunningCount = running,
            });
            await db.SaveChangesAsync(ct);
        }
    }
}
