using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Monitoring.Contracts;
using Monitoring.Infrastructure.Persistence;

namespace Monitoring.IntegrationTests;

/// <summary>
/// Boots the real API against a dedicated SQL Server database (Monitoring_Test) on the local Docker instance.
/// Override with the MONITORING_TEST_CS environment variable. Start it with `docker compose up -d`.
/// </summary>
public sealed class ApiFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string JobKey = "test.job";
    public static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MONITORING_TEST_CS")
        ?? "Server=localhost,14333;Database=Monitoring_Test;User Id=sa;Password=Dev_Passw0rd!;TrustServerCertificate=True";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development"); // enables the dev token endpoint
        builder.UseSetting("ConnectionStrings:Monitoring", ConnectionString);
    }

    public async Task InitializeAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MonitoringDbContext>();
        await db.Database.EnsureDeletedAsync();
        await db.Database.MigrateAsync();

        var now = DateTime.UtcNow;
        db.Definitions.Add(new JobDefinition { Key = JobKey, DisplayName = "Test job", CreatedUtc = now, UpdatedUtc = now });
        db.Definitions.Add(new JobDefinition { Key = "test.missing", DisplayName = "Missing impl", IsAvailable = false, CreatedUtc = now, UpdatedUtc = now });
        db.Schedules.Add(new JobSchedule { Id = Guid.NewGuid(), JobKey = JobKey, IntervalSeconds = 60, NextDueUtc = now.AddMinutes(1) });
        for (var i = 0; i < 30; i++)
        {
            db.Runs.Add(new JobRun
            {
                Id = Guid.NewGuid(), JobKey = JobKey, Trigger = TriggerSource.Schedule,
                Status = i % 5 == 0 ? RunStatus.Failed : RunStatus.Succeeded,
                ScheduledUtc = now.AddMinutes(-i - 10), QueuedUtc = now.AddMinutes(-i - 10),
                NotBeforeUtc = now.AddMinutes(-i - 10), StartedUtc = now.AddMinutes(-i - 10),
                EndedUtc = now.AddMinutes(-i - 10).AddSeconds(2), DurationMs = 2000, Attempt = 1, MaxAttempts = 3,
                WorkerId = "w1", CorrelationId = Guid.NewGuid().ToString("N"),
                Error = i % 5 == 0 ? "Boom" : null,
            });
        }
        await db.SaveChangesAsync();
    }

    Task IAsyncLifetime.DisposeAsync() => Task.CompletedTask;

    public async Task<HttpClient> ClientAsync(string? role)
    {
        var client = CreateClient();
        if (role is null) return client;
        var resp = await client.PostAsJsonAsync("/api/v1/auth/dev-token", new { username = $"{role}-user", role });
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<TokenBody>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body!.Token);
        return client;
    }

    public async Task<T> QueryDbAsync<T>(Func<MonitoringDbContext, Task<T>> query)
    {
        using var scope = Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<MonitoringDbContext>());
    }

    private sealed record TokenBody(string Token);
}

[CollectionDefinition("api")]
public class ApiCollection : ICollectionFixture<ApiFixture>;
