using Microsoft.Extensions.Options;
using Monitoring.Application.Jobs;
using Monitoring.Infrastructure;
using Monitoring.Worker;
using Monitoring.Worker.Jobs;
using Monitoring.Worker.Services;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);

// Run as a Windows Service / systemd unit when hosted that way; plain console otherwise.
builder.Services.AddWindowsService(o => o.ServiceName = "Monitoring.Worker");
builder.Services.AddSystemd();

builder.Services.AddSerilog((sp, cfg) => cfg.ReadFrom.Configuration(builder.Configuration).Enrich.FromLogContext());

builder.Services.AddOptions<WorkerOptions>()
    .Bind(builder.Configuration.GetSection(WorkerOptions.Section))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<WorkerOptions>, WorkerOptionsValidator>();

builder.Services.AddMonitoringPersistence(builder.Configuration);

// Consuming applications register their job implementations here.
builder.Services.AddJobRegistry();
builder.Services.AddJob<SampleJob>("sample.job", "Sample job", "Runs every minute; idempotent demo work.");
builder.Services.AddJob<SampleSlowJob>("sample.slow", "Sample slow job", "Takes ~30s on a 2 minute schedule.");
builder.Services.AddJob<SampleFlakyJob>("sample.flaky", "Sample flaky job", "Fails once then succeeds, to show retry.");

builder.Services.AddSingleton<WorkerIdentity>();
builder.Services.AddSingleton<RunnerState>();
builder.Services.AddHostedService<StartupInitializer>(); // must be registered first: runs before the loops
builder.Services.AddHostedService<HeartbeatService>();
builder.Services.AddHostedService<SchedulerService>();
builder.Services.AddHostedService<RunnerService>();

builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(45));

await builder.Build().RunAsync();
