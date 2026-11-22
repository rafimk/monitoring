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
        builder.UseSetting("Api:WorkerStaleSeconds", "3600"); // seeded heartbeats must not go stale mid-run
    }

    public async Task InitializeAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MonitoringDbContext>();
        await db.Database.EnsureDeletedAsync();
        await CreateDatabaseAsync();   // CREATE DATABASE can be slow while many throwaway worker-test databases are being created in parallel
        await db.Database.MigrateAsync();

        var now = DateTime.UtcNow;
        db.Definitions.Add(new JobDefinition { Key = JobKey, DisplayName = "Test job", CreatedUtc = now, UpdatedUtc = now });
        // "test.missing" is defined but no worker implements it.
        db.Definitions.Add(new JobDefinition { Key = "test.missing", DisplayName = "Missing impl", CreatedUtc = now, UpdatedUtc = now });
        db.Workers.Add(new WorkerHeartbeat { WorkerId = "w1", Host = "test", StartedUtc = now, LastSeenUtc = now });
        db.WorkerJobs.Add(new WorkerJob { WorkerId = "w1", JobKey = JobKey });
        db.Schedules.Add(new JobSchedule { Id = Guid.NewGuid(), JobKey = JobKey, IntervalSeconds = 60, NextDueUtc = now.AddMinutes(1) });
        for (var i = 0; i < 30; i++)
        {
            db.Runs.Add(new JobRun
            {
                Id = Guid.NewGuid(), JobKey = JobKey, Trigger = TriggerSource.Schedule,
                Status = i % 5 == 0 ? RunStatus.Failed : RunStatus.Succeeded,
                ScheduledUtc = now.AddMinutes(-i - 10), QueuedUtc = now.AddMinutes(-i - 10),
                NotBeforeUtc = now.AddMinutes(-i - 10), StartedUtc = now.AddMinutes(-i - 10),
                EndedUtc = now.AddMinutes(-i - 10).AddSeconds(2), DurationMs = 2000, Attempt = 1, CountedAttempts = 1, MaxAttempts = 3,
                WorkerId = "w1", CorrelationId = Guid.NewGuid().ToString("N"),
                Error = i % 5 == 0 ? "Boom" : null,
            });
        }
        await db.SaveChangesAsync();
    }

    private static async Task CreateDatabaseAsync()
    {
        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(ConnectionString);
        var name = builder.InitialCatalog;
        builder.InitialCatalog = "master";
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandTimeout = 300;
        cmd.CommandText = $"IF DB_ID(N'{name.Replace("'", "''")}') IS NULL CREATE DATABASE [{name.Replace("]", "]]")}]";
        await cmd.ExecuteNonQueryAsync();
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
