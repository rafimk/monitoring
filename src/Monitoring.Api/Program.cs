using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Monitoring.Api;
using Monitoring.Infrastructure;
using Monitoring.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<ApiOptions>()
    .Bind(builder.Configuration.GetSection(ApiOptions.Section))
    .ValidateDataAnnotations()
    .ValidateOnStart();
var api = builder.Configuration.GetSection(ApiOptions.Section).Get<ApiOptions>() ?? new ApiOptions();

if (string.IsNullOrWhiteSpace(api.JwtKey) || api.JwtKey.Length < 32)
    throw new InvalidOperationException("Api:JwtKey must be configured with at least 32 characters (use user-secrets or environment variables).");
if (api.EnableDevTokens && !builder.Environment.IsDevelopment())
    throw new InvalidOperationException("Api:EnableDevTokens may only be enabled in the Development environment.");

builder.Services.AddMonitoringPersistence(builder.Configuration);

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false; // keep short claim names ("role", "name") exactly as issued
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true, ValidIssuer = api.JwtIssuer,
            ValidateAudience = true, ValidAudience = api.JwtAudience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(api.JwtKey)),
            ValidateLifetime = true, ClockSkew = TimeSpan.FromSeconds(30),
            RoleClaimType = "role", NameClaimType = "name",
        };
    });
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
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health/live", new() { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new() { Predicate = r => r.Tags.Contains("ready") });

app.Run();

public partial class Program; // for WebApplicationFactory in integration tests
