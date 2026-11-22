using Microsoft.Extensions.Options;
using Monitoring.Infrastructure.Persistence;

namespace Monitoring.Worker.Services;

/// <summary>Publishes worker liveness so the API/dashboard can show stale or offline workers.</summary>
public sealed class HeartbeatService(
    IServiceScopeFactory scopes, IOptions<WorkerOptions> options, WorkerRegistrar registrar,
    RunnerState state, ILogger<HeartbeatService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.HeartbeatSeconds));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MonitoringDbContext>();
                await registrar.BeatAsync(db, state.Running, stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogWarning(ex, "Heartbeat failed"); }
        }
        while (await WaitAsync(timer, stop));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer t, CancellationToken ct)
    {
        try { return await t.WaitForNextTickAsync(ct); } catch (OperationCanceledException) { return false; }
    }
}
