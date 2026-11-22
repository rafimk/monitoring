using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monitoring.Application.Jobs;
using Monitoring.Infrastructure.Persistence;
using Monitoring.Worker;
using Monitoring.Worker.Services;

namespace Monitoring.IntegrationTests.Worker;

public sealed record LogEntry(LogLevel Level, string Category, string Message);

public sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<LogEntry> Entries { get; } = new();
    public ILogger CreateLogger(string categoryName) => new Capture(this, categoryName);
    public void Dispose() { }

    private sealed class Capture(CapturingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> fmt) =>
            owner.Entries.Enqueue(new LogEntry(level, category, fmt(state, ex) + (ex is null ? "" : " :: " + ex.GetType().Name)));
    }
}

/// <summary>
/// A whole worker host as production wires it: only the <see cref="WorkerCoordinator"/> is hosted, and it creates the
/// initializer, heartbeat, scheduler and runner when (and only while) this node is the active one.
/// </summary>
public sealed class TestNode : IAsyncDisposable
{
    private readonly ServiceProvider _sp;
    private bool _stopped;

    public WorkerCoordinator Coordinator { get; }
    public WorkerIdentity Identity { get; }
    public CapturingLoggerProvider Logs { get; }
    /// <summary>How long Coordinator.StartAsync took: a Windows Service start must not wait for leadership.</summary>
    public TimeSpan StartDuration { get; private set; }

    private TestNode(ServiceProvider sp, CapturingLoggerProvider logs)
    {
        _sp = sp; Logs = logs;
        Coordinator = sp.GetRequiredService<WorkerCoordinator>();
        Identity = sp.GetRequiredService<WorkerIdentity>();
    }

    public static async Task<TestNode> StartAsync(string cs, JobScripts scripts, string[] jobKeys, WorkerOptions options)
    {
        var logs = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Debug).AddProvider(logs));
        services.AddSingleton(Options.Create(options));
        services.AddDbContext<MonitoringDbContext>(o => o.UseSqlServer(cs), ServiceLifetime.Scoped);
        services.AddJobRegistry();
        foreach (var key in jobKeys) services.AddJob<ScriptedJob>(key, key, "test job");
        services.AddSingleton(scripts);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<WorkerIdentity>();
        services.AddSingleton<RunnerState>();
        services.AddSingleton<WorkerRegistrar>();
        services.AddSingleton<RunStore>();
        services.AddSingleton<WorkerCoordinator>();

        var node = new TestNode(services.BuildServiceProvider(), logs);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await node.Coordinator.StartAsync(CancellationToken.None);
        node.StartDuration = sw.Elapsed;
        return node;
    }

    /// <summary>Graceful stop, exactly like the host stopping the service.</summary>
    public async Task StopAsync()
    {
        if (_stopped) return;
        _stopped = true;
        await Coordinator.StopAsync(CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await _sp.DisposeAsync();
    }
}
