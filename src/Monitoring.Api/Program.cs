using Microsoft.AspNetCore.Builder;
using Monitoring.Api;
using Monitoring.Api.Auth;
using Monitoring.Api.Realtime;
using Monitoring.Infrastructure;
using Monitoring.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<ApiOptions>()
    .Bind(builder.Configuration.GetSection(ApiOptions.Section))
    .ValidateDataAnnotations()
    .ValidateOnStart();
var api = builder.Configuration.GetSection(ApiOptions.Section).Get<ApiOptions>() ?? new ApiOptions();

builder.Services.AddOptions<KeycloakOptions>()
    .Bind(builder.Configuration.GetSection(KeycloakOptions.Section))
    .ValidateDataAnnotations()
    .ValidateOnStart();
var keycloak = builder.Configuration.GetSection(KeycloakOptions.Section).Get<KeycloakOptions>() ?? new KeycloakOptions();
// Production must use Keycloak; the locally signed dev token is Development-only and must be explicitly enabled.
AuthenticationSetup.Validate(api, keycloak, builder.Environment.IsDevelopment());

builder.Services.AddMonitoringPersistence(builder.Configuration);
builder.Services.AddMonitoringAuthentication(api, keycloak);

builder.Services.AddSignalR(o => o.EnableDetailedErrors = false);
builder.Services.AddSingleton<DispatcherLeadership>();
builder.Services.AddSingleton<ChangeFeedDispatcher>(); // resolvable so tests can drive single polls deterministically
if (api.ChangeFeedEnabled) builder.Services.AddHostedService(sp => sp.GetRequiredService<ChangeFeedDispatcher>());
builder.Services.AddAuthorization(o =>
{
    o.AddPolicy(Policies.Viewer, p => p.RequireRole(Roles.Viewer, Roles.Operator, Roles.Administrator));
    o.AddPolicy(Policies.Operator, p => p.RequireRole(Roles.Operator, Roles.Administrator));
    o.AddPolicy(Policies.Administrator, p => p.RequireRole(Roles.Administrator));
});

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
{
    if (api.AllowedOrigins.Length > 0)
        p.WithOrigins(api.AllowedOrigins).AllowAnyHeader().AllowAnyMethod();
}));

builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHealthChecks().AddDbContextCheck<MonitoringDbContext>("database", tags: ["ready"]);

var app = builder.Build();

// Structured error responses; never leak exception details (they may contain secrets).
app.UseExceptionHandler(e => e.Run(async ctx =>
{
    ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
    await Results.Problem(title: "An unexpected error occurred.", statusCode: 500,
        extensions: new Dictionary<string, object?> { ["traceId"] = ctx.TraceIdentifier }).ExecuteAsync(ctx);
}));
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
// Browser WebSocket upgrades are only accepted from the configured dashboard origins.
var webSockets = new WebSocketOptions();
foreach (var origin in api.AllowedOrigins) webSockets.AllowedOrigins.Add(origin);
app.UseWebSockets(webSockets);
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHub<MonitoringHub>(HubPaths.Monitoring, o =>
{
    o.CloseOnAuthenticationExpiration = true; // a connection ends when its token expires; the client reconnects with a fresh one
}).RequireAuthorization(Policies.Viewer);
app.MapHealthChecks("/health/live", new() { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new() { Predicate = r => r.Tags.Contains("ready") });

app.Run();

public partial class Program; // for WebApplicationFactory in integration tests
