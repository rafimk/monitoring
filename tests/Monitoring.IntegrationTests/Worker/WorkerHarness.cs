using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Monitoring.Application.Jobs;
using Monitoring.Contracts;
using Monitoring.Infrastructure.Persistence;
using Monitoring.Worker;
using Monitoring.Worker.Services;

namespace Monitoring.IntegrationTests.Worker;

/// <summary>
/// Base class for worker tests. xUnit creates one instance per test, so every test gets its own throwaway SQL Server
/// database (migrated from the real migrations) and is dropped afterwards: tests never see each other's rows.
/// Uses the Docker SQL Server from `docker compose up -d`; override with MONITORING_TEST_SERVER_CS (Database is replaced).
/// </summary>
public abstract class WorkerTestBase : IAsyncLifetime
{
    private readonly List<TestWorker> _workers = [];
    private readonly List<TestNode> _nodes = [];
    private readonly string _dbName = "Monitoring_W_" + Guid.NewGuid().ToString("N")[..12];

    protected string ConnectionString { get; }
    protected JobScripts Scripts { get; } = new();

    protected WorkerTestBase()
    {
        var baseCs = Environment.GetEnvironmentVariable("MONITORING_TEST_SERVER_CS")
                     ?? "Server=localhost,14333;User Id=sa;Password=Dev_Passw0rd!;TrustServerCertificate=True";
        ConnectionString = new SqlConnectionStringBuilder(baseCs) { InitialCatalog = _dbName }.ConnectionString;
    }

    public async Task InitializeAsync()
    {
        await using var db = NewDb();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        foreach (var n in _nodes) await n.DisposeAsync();
        foreach (var w in _workers) await w.DisposeAsync();
        await using (var db = NewDb()) await db.Database.EnsureDeletedAsync();
        SqlConnection.ClearAllPools();
    }

    protected MonitoringDbContext NewDb() =>
        new(new DbContextOptionsBuilder<MonitoringDbContext>().UseSqlServer(ConnectionString).Options);

    protected static WorkerOptions FastOptions(string? name = null, int lease = 6, int heartbeat = 2) => new()
    {
        WorkerId = name, PollIntervalSeconds = 1, LeaseSeconds = lease, HeartbeatSeconds = heartbeat,
        MaxParallelism = 3, SchedulerBatchSize = 50, ShutdownGraceSeconds = 10,
    };

    protected async Task<TestWorker> StartWorkerAsync(
        string[] jobKeys, WorkerOptions? options = null, Func<IServiceProvider, RunStore>? runStore = null)
    {
        var w = await TestWorker.StartAsync(ConnectionString, Scripts, jobKeys, options ?? FastOptions(), runStore);
        _workers.Add(w);
        return w;
    }

    /// <summary>Starts a full worker host with the real <see cref="WorkerCoordinator"/> (leader election per options).</summary>
    protected async Task<TestNode> StartNodeAsync(string[] jobKeys, WorkerOptions? options = null, string? connectionString = null)
    {
        var n = await TestNode.StartAsync(connectionString ?? ConnectionString, Scripts, jobKeys, options ?? LeaderOptions());
        _nodes.Add(n);
        return n;
    }

    /// <summary>Fast timings with leader election on (the production default), so tests finish in seconds.</summary>
    protected static WorkerOptions LeaderOptions(string? name = null, int standbyRetry = 1, int lease = 6, int heartbeat = 2) =>
        new()
        {
            WorkerId = name, PollIntervalSeconds = 1, LeaseSeconds = lease, HeartbeatSeconds = heartbeat, MaxParallelism = 3,
            SchedulerBatchSize = 50, ShutdownGraceSeconds = 10, LeaderMode = WorkerLeaderMode.ActiveStandby,
            StandbyRetrySeconds = standbyRetry, LeaderCheckSeconds = 1,
        };

    // ---- data helpers ----

    protected async Task DefineJobAsync(
        string key, int maxAttempts = 3, int retryBaseSeconds = 1, int timeoutSeconds = 60,
        ConcurrencyPolicy concurrency = ConcurrencyPolicy.SkipIfRunning)
    {
        await using var db = NewDb();
        var now = DateTime.UtcNow;
        db.Definitions.Add(new JobDefinition
        {
            Key = key, DisplayName = key, CreatedUtc = now, UpdatedUtc = now, MaxAttempts = maxAttempts,
            RetryBaseDelaySeconds = retryBaseSeconds, TimeoutSeconds = timeoutSeconds, ConcurrencyPolicy = concurrency,
        });
        await db.SaveChangesAsync();
    }

    protected async Task<Guid> EnqueueManualAsync(string key)
    {
        await using var db = NewDb();
        var def = await db.Definitions.AsNoTracking().FirstAsync(d => d.Key == key);
        var now = DateTime.UtcNow;
        var run = new JobRun
        {
            Id = Guid.NewGuid(), JobKey = key, Trigger = TriggerSource.Manual, Status = RunStatus.Queued,
            ScheduledUtc = now, QueuedUtc = now, NotBeforeUtc = now, MaxAttempts = def.MaxAttempts,
            CorrelationId = Guid.NewGuid().ToString("N"),
        };
        db.Runs.Add(run);
        await db.SaveChangesAsync();
        return run.Id;
    }

    /// <summary>A run that a worker claimed and then died on: Running, owned by a worker that never heartbeats again.</summary>
    protected async Task<Guid> InsertCrashedRunAsync(string key, int maxAttempts, int attemptsAlreadyUsed, TimeSpan leaseExpiredAgo)
    {
        await using var db = NewDb();
        var now = DateTime.UtcNow;
        var run = new JobRun
        {
            Id = Guid.NewGuid(), JobKey = key, Trigger = TriggerSource.Manual, Status = RunStatus.Running,
            ScheduledUtc = now.AddMinutes(-2), QueuedUtc = now.AddMinutes(-2), NotBeforeUtc = now.AddMinutes(-2),
            StartedUtc = now.AddMinutes(-1), Attempt = attemptsAlreadyUsed, CountedAttempts = attemptsAlreadyUsed,
            MaxAttempts = maxAttempts, WorkerId = "crashed-worker-1", LeaseExpiresUtc = now - leaseExpiredAgo,
            CorrelationId = Guid.NewGuid().ToString("N"),
        };
        db.Runs.Add(run);
        db.Attempts.Add(new RunAttempt
        {
            Id = Guid.NewGuid(), RunId = run.Id, Attempt = attemptsAlreadyUsed, Status = RunStatus.Running,
            StartedUtc = now.AddMinutes(-1), WorkerId = "crashed-worker-1",
        });
        await db.SaveChangesAsync();
        return run.Id;
    }

    /// <summary>A store with no background loops, to drive claim/renew/complete/recover step by step.</summary>
    protected (RunStore Store, WorkerIdentity Identity) CreateBareStore(string name, params string[] jobKeys)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(FastOptions(name)));
        services.AddDbContext<MonitoringDbContext>(o => o.UseSqlServer(ConnectionString));
        services.AddJobRegistry();
        foreach (var k in jobKeys) services.AddJob<ScriptedJob>(k, k, "test job");
        services.AddSingleton(Scripts);
        services.AddSingleton<WorkerIdentity>();
        services.AddSingleton<RunStore>();
        var sp = services.BuildServiceProvider();
        return (sp.GetRequiredService<RunStore>(), sp.GetRequiredService<WorkerIdentity>());
    }

    protected async Task<JobRun> GetRunAsync(Guid id)
    {
        await using var db = NewDb();
        return await db.Runs.AsNoTracking().FirstAsync(r => r.Id == id);
    }

    protected async Task<List<RunAttempt>> GetAttemptsAsync(Guid runId)
    {
        await using var db = NewDb();
        return await db.Attempts.AsNoTracking().Where(a => a.RunId == runId).OrderBy(a => a.Attempt).ToListAsync();
    }

    protected static async Task EventuallyAsync(Func<Task<bool>> condition, string because, int timeoutSeconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out after {timeoutSeconds}s waiting for: {because}");
            await Task.Delay(100);
        }
    }

    protected Task WaitForStatusAsync(Guid runId, RunStatus status, int timeoutSeconds = 30) =>
        EventuallyAsync(async () => (await GetRunAsync(runId)).Status == status, $"run {runId} to be {status}", timeoutSeconds);

    protected Task WaitForRunningAttemptAsync(Guid runId, int attempt) =>
        EventuallyAsync(async () => { var r = await GetRunAsync(runId); return r.Status == RunStatus.Running && r.Attempt == attempt; },
            $"run {runId} to be Running on attempt {attempt}");
}

/// <summary>Per-test behaviour for each job key, so one generic job class can play any role.</summary>
public sealed class JobScripts
{
    public delegate Task<JobResult> Script(JobExecutionContext ctx, CancellationToken ct);
    private readonly ConcurrentDictionary<string, Script> _scripts = new();
    public void Set(string key, Script script) => _scripts[key] = script;
    public Script Get(string key) => _scripts[key];
}

public sealed class ScriptedJob(JobScripts scripts) : IJob
{
    public Task<JobResult> ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken) =>
        scripts.Get(context.JobKey)(context, cancellationToken);
}

/// <summary>
/// A complete worker (initializer, heartbeat, scheduler, runner) running in-process against the test database,
/// built from the real production classes with fast timings.
/// </summary>
public sealed class TestWorker : IAsyncDisposable
{
    private readonly ServiceProvider _sp;
    private readonly List<BackgroundService> _loops;

    public WorkerIdentity Identity { get; }
    public RunStore Store { get; }
    public SchedulerService Scheduler { get; }

    private TestWorker(ServiceProvider sp, List<BackgroundService> loops)
    {
        _sp = sp; _loops = loops;
        Identity = sp.GetRequiredService<WorkerIdentity>();
        Store = sp.GetRequiredService<RunStore>();
        Scheduler = sp.GetRequiredService<SchedulerService>();
    }

    public static async Task<TestWorker> StartAsync(
        string cs, JobScripts scripts, string[] jobKeys, WorkerOptions options, Func<IServiceProvider, RunStore>? runStore)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(options));
        services.AddDbContext<MonitoringDbContext>(o => o.UseSqlServer(cs), ServiceLifetime.Scoped);
        services.AddJobRegistry();
        foreach (var key in jobKeys) services.AddJob<ScriptedJob>(key, key, "test job");
        services.AddSingleton(scripts);
        services.AddSingleton<WorkerIdentity>();
        services.AddSingleton<RunnerState>();
        services.AddSingleton<WorkerRegistrar>();
        if (runStore is null) services.AddSingleton<RunStore>(); else services.AddSingleton(runStore);
        services.AddSingleton<StartupInitializer>();
        services.AddSingleton<HeartbeatService>();
        services.AddSingleton<SchedulerService>();
        services.AddSingleton<RunnerService>();

        var sp = services.BuildServiceProvider();
        await sp.GetRequiredService<StartupInitializer>().StartAsync(CancellationToken.None);

        var loops = new List<BackgroundService>
        {
            sp.GetRequiredService<HeartbeatService>(), sp.GetRequiredService<SchedulerService>(), sp.GetRequiredService<RunnerService>(),
        };
        foreach (var l in loops) await l.StartAsync(CancellationToken.None);
        return new TestWorker(sp, loops);
    }

    /// <summary>Graceful shutdown, exactly like the host stopping the service: running jobs are cancelled and re-queued.</summary>
    public async Task StopAsync()
    {
        if (_stopped) return;
        _stopped = true;
        // Runner first (it waits for running jobs to record their outcome), then the other loops.
        foreach (var l in Enumerable.Reverse(_loops)) await l.StopAsync(CancellationToken.None);
    }
    private bool _stopped;

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await _sp.DisposeAsync();
    }
}
