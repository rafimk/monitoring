using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Monitoring.Contracts;

namespace Monitoring.IntegrationTests;

[Collection("api")]
public class ApiTests(ApiFixture fx)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    // ---- authentication / authorization matrix ----

    [Theory]
    [InlineData("/api/v1/jobs")]
    [InlineData("/api/v1/runs")]
    [InlineData("/api/v1/summary")]
    [InlineData("/api/v1/workers")]
    public async Task Anonymous_IsRejected(string url)
    {
        var c = await fx.ClientAsync(null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync(url)).StatusCode);
    }

    [Theory]
    [InlineData("viewer")]
    [InlineData("operator")]
    [InlineData("administrator")]
    public async Task AllRoles_CanRead(string role)
    {
        var c = await fx.ClientAsync(role);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/jobs")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/summary")).StatusCode);
    }

    [Fact]
    public async Task Viewer_CannotRunDisableOrEdit()
    {
        var c = await fx.ClientAsync("viewer");
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsync($"/api/v1/jobs/{ApiFixture.JobKey}/run", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsync($"/api/v1/jobs/{ApiFixture.JobKey}/disable", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PutAsJsonAsync($"/api/v1/jobs/{ApiFixture.JobKey}", new { timeoutSeconds = 5 })).StatusCode);
    }

    [Fact]
    public async Task Operator_CannotEdit_ButAdministratorCan()
    {
        var op = await fx.ClientAsync("operator");
        Assert.Equal(HttpStatusCode.Forbidden, (await op.PutAsJsonAsync($"/api/v1/jobs/{ApiFixture.JobKey}", new { timeoutSeconds = 99 })).StatusCode);

        var admin = await fx.ClientAsync("administrator");
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/api/v1/jobs/{ApiFixture.JobKey}", new { timeoutSeconds = 99 })).StatusCode);
        var detail = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/jobs/{ApiFixture.JobKey}", Json);
        Assert.Equal(99, detail.GetProperty("timeoutSeconds").GetInt32());
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/api/v1/jobs/{ApiFixture.JobKey}", new { timeoutSeconds = 60 })).StatusCode);
    }

    [Fact]
    public async Task Audit_IsAdministratorOnly()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await (await fx.ClientAsync("operator")).GetAsync("/api/v1/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await (await fx.ClientAsync("administrator")).GetAsync("/api/v1/audit")).StatusCode);
    }

    // ---- commands ----

    [Fact]
    public async Task RunNow_CreatesDurableManualRun_AndRejectsDuplicate_AndIsAudited()
    {
        var c = await fx.ClientAsync("operator");
        var first = await c.PostAsync($"/api/v1/jobs/{ApiFixture.JobKey}/run", null);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var runId = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("runId").GetGuid();

        var run = await fx.QueryDbAsync(db => db.Runs.AsNoTracking().FirstAsync(r => r.Id == runId));
        Assert.Equal(TriggerSource.Manual, run.Trigger);
        Assert.Equal(RunStatus.Queued, run.Status);
        Assert.Equal("operator-user", run.RequestedBy);

        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsync($"/api/v1/jobs/{ApiFixture.JobKey}/run", null)).StatusCode);
        Assert.True(await fx.QueryDbAsync(db => db.Audit.AnyAsync(a => a.Action == "job.run-now" && a.Actor == "operator-user")));

        // clean up so other tests see a stable queue
        await fx.QueryDbAsync(async db => await db.Runs.Where(r => r.Id == runId).ExecuteDeleteAsync());
    }

    [Fact]
    public async Task RunNow_OnUnavailableJob_IsConflict()
    {
        var c = await fx.ClientAsync("operator");
        var r = await c.PostAsync("/api/v1/jobs/test.missing/run", null);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("application/problem+json", r.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task DisableThenEnable_TogglesState()
    {
        var c = await fx.ClientAsync("operator");
        Assert.Equal(HttpStatusCode.NoContent, (await c.PostAsync($"/api/v1/jobs/{ApiFixture.JobKey}/disable", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsync($"/api/v1/jobs/{ApiFixture.JobKey}/run", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await c.PostAsync($"/api/v1/jobs/{ApiFixture.JobKey}/enable", null)).StatusCode);
    }

    [Fact]
    public async Task UnknownJob_Returns404Problem()
    {
        var c = await fx.ClientAsync("viewer");
        var r = await c.GetAsync("/api/v1/jobs/nope.nope");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Equal("application/problem+json", r.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("{\"intervalSeconds\":1}")]
    [InlineData("{\"maxAttempts\":0}")]
    [InlineData("{\"timeoutSeconds\":-5}")]
    public async Task InvalidUpdate_Returns400(string body)
    {
        var c = await fx.ClientAsync("administrator");
        var r = await c.PutAsync($"/api/v1/jobs/{ApiFixture.JobKey}", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    // ---- queries ----

    [Fact]
    public async Task Runs_Pagination_And_Total()
    {
        var c = await fx.ClientAsync("viewer");
        var p1 = await c.GetFromJsonAsync<JsonElement>("/api/v1/runs?jobKey=test.job&page=1&pageSize=10", Json);
        var p3 = await c.GetFromJsonAsync<JsonElement>("/api/v1/runs?jobKey=test.job&page=3&pageSize=10", Json);
        var p4 = await c.GetFromJsonAsync<JsonElement>("/api/v1/runs?jobKey=test.job&page=4&pageSize=10", Json);
        Assert.Equal(30, p1.GetProperty("total").GetInt32());
        Assert.Equal(10, p1.GetProperty("items").GetArrayLength());
        Assert.Equal(10, p3.GetProperty("items").GetArrayLength());
        Assert.Equal(0, p4.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Runs_FilterByStatus_And_Trigger()
    {
        var c = await fx.ClientAsync("viewer");
        var failed = await c.GetFromJsonAsync<JsonElement>("/api/v1/runs?status=Failed&pageSize=100", Json);
        Assert.Equal(6, failed.GetProperty("total").GetInt32());
        Assert.All(failed.GetProperty("items").EnumerateArray(), i => Assert.Equal("Failed", i.GetProperty("status").GetString()));

        var manual = await c.GetFromJsonAsync<JsonElement>("/api/v1/runs?trigger=Manual", Json);
        Assert.Equal(0, manual.GetProperty("total").GetInt32());
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=500")]
    public async Task Runs_InvalidPaging_Returns400(string query)
    {
        var c = await fx.ClientAsync("viewer");
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync($"/api/v1/runs?{query}")).StatusCode);
    }

    [Fact]
    public async Task RunDetail_IncludesAttemptsList()
    {
        var c = await fx.ClientAsync("viewer");
        var list = await c.GetFromJsonAsync<JsonElement>("/api/v1/runs?pageSize=1", Json);
        var id = list.GetProperty("items")[0].GetProperty("id").GetGuid();
        var detail = await c.GetFromJsonAsync<JsonElement>($"/api/v1/runs/{id}", Json);
        Assert.Equal(id, detail.GetProperty("run").GetProperty("id").GetGuid());
        Assert.True(detail.TryGetProperty("attempts", out _));
    }

    [Fact]
    public async Task Summary_ReportsCountsAndRate()
    {
        var c = await fx.ClientAsync("viewer");
        var s = await c.GetFromJsonAsync<JsonElement>("/api/v1/summary", Json);
        Assert.Equal(2, s.GetProperty("totalJobs").GetInt32());
        Assert.Equal(1, s.GetProperty("unavailableJobs").GetInt32());
        Assert.Equal(24, s.GetProperty("trend").GetArrayLength());
    }

    // ---- operational ----

    [Fact]
    public async Task Health_Endpoints_AreAnonymous_AndHealthy()
    {
        var c = await fx.ClientAsync(null);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/health/ready")).StatusCode);
    }

    [Fact]
    public async Task DevToken_RejectsUnknownRole()
    {
        var c = await fx.ClientAsync(null);
        var r = await c.PostAsJsonAsync("/api/v1/auth/dev-token", new { username = "x", role = "root" });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }
}
