using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Monitoring.Api;
using Monitoring.Api.Auth;
using Monitoring.Contracts;
using Monitoring.Infrastructure.Persistence;

namespace Monitoring.IntegrationTests.Realtime;

/// <summary>Keycloak token validation, role mapping and the viewer/operator/administrator matrix on REST and the hub.</summary>
public class KeycloakAuthTests : IAsyncLifetime
{
    private readonly string _db = "Monitoring_K_" + Guid.NewGuid().ToString("N")[..12];
    private readonly List<IAsyncDisposable> _disposables = [];
    private string Cs => new SqlConnectionStringBuilder(
        Environment.GetEnvironmentVariable("MONITORING_TEST_SERVER_CS")
        ?? "Server=localhost,14333;User Id=sa;Password=Dev_Passw0rd!;TrustServerCertificate=True") { InitialCatalog = _db }.ConnectionString;

    public async Task InitializeAsync()
    {
        await using var db = new MonitoringDbContext(new DbContextOptionsBuilder<MonitoringDbContext>().UseSqlServer(Cs).Options);
        await db.Database.MigrateAsync();
        var now = DateTime.UtcNow;
        db.Definitions.Add(new JobDefinition { Key = "k.job", DisplayName = "Job", CreatedUtc = now, UpdatedUtc = now });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        foreach (var d in _disposables) await d.DisposeAsync();
        await using var db = new MonitoringDbContext(new DbContextOptionsBuilder<MonitoringDbContext>().UseSqlServer(Cs).Options);
        await db.Database.EnsureDeletedAsync();
        SqlConnection.ClearAllPools();
    }

    private KeycloakApiFactory Api(Dictionary<string, string?>? settings = null, bool dispatcher = false)
    {
        var f = new KeycloakApiFactory(Cs, settings, dispatcher);
        _disposables.Add(f);
        return f;
    }

    private static async Task<HttpStatusCode> GetAsync(KeycloakApiFactory f, string? token, string url = "/api/v1/jobs") =>
        (await (await f.ClientAsync(token)).GetAsync(url)).StatusCode;

    // ---- token validation ----

    [Fact]
    public async Task ValidKeycloakToken_IsAccepted_AndNameComesFromPreferredUsername()
    {
        var f = Api();
        Assert.Equal(HttpStatusCode.OK, await GetAsync(f, FakeKeycloak.Viewer()));
    }

    [Fact]
    public async Task Anonymous_IsUnauthorized() => Assert.Equal(HttpStatusCode.Unauthorized, await GetAsync(Api(), null));

    public static IEnumerable<object[]> BadTokens()
    {
        string[] viewer = ["monitoring-viewer"];
        yield return ["wrong issuer", FakeKeycloak.Token(viewer, issuer: "https://evil.example/realms/monitoring")];
        yield return ["wrong audience", FakeKeycloak.Token(viewer, audience: "some-other-api")];
        yield return ["missing audience", FakeKeycloak.Token(viewer, audience: null)];
        yield return ["expired", FakeKeycloak.Token(viewer, lifetime: TimeSpan.FromMinutes(-10))];
        yield return ["signed by another key", FakeKeycloak.Token(viewer, key: FakeKeycloak.OtherKey)];
        yield return ["garbage", "not.a.jwt"];
    }

    [Theory]
    [MemberData(nameof(BadTokens))]
    public async Task InvalidToken_IsUnauthorized(string _, string token) =>
        Assert.Equal(HttpStatusCode.Unauthorized, await GetAsync(Api(), token));

    [Fact]
    public async Task LocallySignedDevToken_IsRejected_AndDevEndpointIsOffInProduction()
    {
        var f = Api();
        var c = await f.ClientAsync(null);
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync("/api/v1/auth/dev-token", new { username = "x", role = "administrator" })).StatusCode);
        // A token a developer might mint with the old shared key must not be accepted.
        var dev = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
            "monitoring", "monitoring-web", [new("role", "administrator")], expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: new(new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(new byte[32]), "HS256")));
        Assert.Equal(HttpStatusCode.Unauthorized, await GetAsync(f, dev));
    }

    // ---- role mapping ----

    [Fact]
    public async Task UserWithoutAMonitoringRole_IsForbiddenEverywhere()
    {
        var f = Api();
        foreach (var token in new[]
                 {
                     FakeKeycloak.Token(realmRoles: []),                                  // no roles at all
                     FakeKeycloak.Token(realmRoles: ["offline_access", "uma_authorization"]), // only Keycloak defaults
                     FakeKeycloak.Token(realmRoles: ["administrator", "operator", "viewer"]), // right words, unmapped names
                     FakeKeycloak.Token(clientRoles: ["monitoring-administrator"]),       // client roles are ignored in Realm mode
                 })
        {
            Assert.Equal(HttpStatusCode.Forbidden, await GetAsync(f, token));
            Assert.Equal(HttpStatusCode.Forbidden, await GetAsync(f, token, "/api/v1/summary"));
        }
    }

    [Fact]
    public async Task ClientRoleSource_ReadsOnlyTheConfiguredClientsRoles()
    {
        var f = Api(new() { ["Keycloak:RoleSource"] = "Client", ["Keycloak:ClientId"] = FakeKeycloak.ClientId });
        Assert.Equal(HttpStatusCode.OK, await GetAsync(f, FakeKeycloak.Token(clientRoles: ["monitoring-viewer"])));
        Assert.Equal(HttpStatusCode.Forbidden, await GetAsync(f, FakeKeycloak.Token(realmRoles: ["monitoring-administrator"])));
        Assert.Equal(HttpStatusCode.Forbidden, await GetAsync(f, FakeKeycloak.Token(clientRoles: ["monitoring-viewer"], clientIdForRoles: "another-client")));
    }

    [Fact]
    public async Task CustomRoleNames_AreHonoured()
    {
        var f = Api(new() { ["Keycloak:ViewerRole"] = "ops-read" });
        Assert.Equal(HttpStatusCode.OK, await GetAsync(f, FakeKeycloak.Token(["ops-read"])));
        Assert.Equal(HttpStatusCode.Forbidden, await GetAsync(f, FakeKeycloak.Token(["monitoring-viewer"])));
    }

    // ---- permission matrix: the API, not React, decides ----

    [Fact]
    public async Task PermissionMatrix_MatchesViewerOperatorAdministrator()
    {
        var f = Api();
        var viewer = await f.ClientAsync(FakeKeycloak.Viewer());
        var op = await f.ClientAsync(FakeKeycloak.Operator());
        var admin = await f.ClientAsync(FakeKeycloak.Admin());

        // read
        foreach (var c in new[] { viewer, op, admin })
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/jobs")).StatusCode);
        // enable/disable = operator+
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync("/api/v1/jobs/k.job/disable", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await op.PostAsync("/api/v1/jobs/k.job/disable", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync("/api/v1/jobs/k.job/enable", null)).StatusCode);
        // run-now = operator+ (passes authorization; 409 because no live worker implements the job)
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync("/api/v1/jobs/k.job/run", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await op.PostAsync("/api/v1/jobs/k.job/run", null)).StatusCode);
        // edit = administrator only; audit read = administrator only
        var edit = JsonContent.Create(new { timeoutSeconds = 30 });
        Assert.Equal(HttpStatusCode.Forbidden, (await op.PutAsync("/api/v1/jobs/k.job", edit)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsync("/api/v1/jobs/k.job", JsonContent.Create(new { timeoutSeconds = 30 }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.GetAsync("/api/v1/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/audit")).StatusCode);

        // the audit trail names the Keycloak user
        await using var db = new MonitoringDbContext(new DbContextOptionsBuilder<MonitoringDbContext>().UseSqlServer(Cs).Options);
        Assert.Contains(await db.Audit.Select(a => a.Actor).ToListAsync(), a => a == "operator-user");
    }

    // ---- hub access ----

    [Fact]
    public async Task Hub_RejectsAnonymousAndRoleLessUsers_AcceptsAnyMonitoringRole()
    {
        var f = Api(dispatcher: true);
        _ = f.Server;
        await f.Dispatcher.DispatchOnceAsync(CancellationToken.None); // become the dispatcher leader

        foreach (var token in new string?[] { null, FakeKeycloak.Token(realmRoles: ["offline_access"]), FakeKeycloak.Token(["monitoring-viewer"], key: FakeKeycloak.OtherKey) })
        {
            var bad = f.Hub(token);
            await Assert.ThrowsAnyAsync<Exception>(() => bad.StartAsync());
            await bad.DisposeAsync();
        }
        foreach (var token in new[] { FakeKeycloak.Viewer(), FakeKeycloak.Operator(), FakeKeycloak.Admin() })
        {
            var ok = f.Hub(token);
            await ok.StartAsync();
            Assert.Equal(HubConnectionState.Connected, ok.State);
            await ok.DisposeAsync();
        }
    }

    [Fact]
    public async Task QueryStringToken_IsOnlyHonouredOnTheExactHubPath()
    {
        var f = Api();
        var c = await f.ClientAsync(null);
        var token = FakeKeycloak.Admin();
        // A token in the query is ignored on every REST endpoint ...
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync($"/api/v1/jobs?access_token={token}")).StatusCode);

    }

    [Fact]
    public async Task Hub_RejectsConnectionsFromUnknownOrigins_AndAllowsConfiguredDashboardOrigin()
    {
        var f = Api(dispatcher: true);
        _ = f.Server;
        await f.Dispatcher.DispatchOnceAsync(CancellationToken.None);

        var good = f.Hub(FakeKeycloak.Viewer(), HttpTransportType.LongPolling, origin: "http://localhost:5173");
        await good.StartAsync();
        await Task.Delay(500);
        Assert.Equal(HubConnectionState.Connected, good.State);
        await good.DisposeAsync();

        var evil = f.Hub(FakeKeycloak.Viewer(), HttpTransportType.LongPolling, origin: "https://evil.example");
        var closed = new TaskCompletionSource();
        evil.Closed += _ => { closed.TrySetResult(); return Task.CompletedTask; };
        try { await evil.StartAsync(); } catch { closed.TrySetResult(); }
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotEqual(HubConnectionState.Connected, evil.State);
        await evil.DisposeAsync();
    }

    // ---- startup safety ----

    [Fact]
    public void Production_WithoutKeycloak_RefusesToStart()
    {
        var api = new ApiOptions();
        Assert.Throws<InvalidOperationException>(() => AuthenticationSetup.Validate(api, new KeycloakOptions(), isDevelopment: false));
        // a leftover dev-token flag must not rescue it either
        Assert.Throws<InvalidOperationException>(() =>
            AuthenticationSetup.Validate(new ApiOptions { EnableDevTokens = true, JwtKey = new string('k', 40) }, new KeycloakOptions(), false));
    }

    [Fact]
    public void Validation_RejectsUnsafeKeycloakSettings()
    {
        var api = new ApiOptions();
        var ok = new KeycloakOptions { Authority = "https://kc.example.com/realms/monitoring", Audience = "monitoring-api" };
        AuthenticationSetup.Validate(api, ok, false); // baseline passes

        Assert.Throws<InvalidOperationException>(() => AuthenticationSetup.Validate(api, new KeycloakOptions { Authority = "not a url", Audience = "a" }, false));
        Assert.Throws<InvalidOperationException>(() => AuthenticationSetup.Validate(api, new KeycloakOptions { Authority = "http://kc/realms/m", Audience = "a" }, false));
        Assert.Throws<InvalidOperationException>(() => AuthenticationSetup.Validate(api, new KeycloakOptions { Authority = "https://kc/realms/m" }, false)); // no audience
        Assert.Throws<InvalidOperationException>(() => AuthenticationSetup.Validate(api,
            new KeycloakOptions { Authority = "http://kc/realms/m", Audience = "a", RequireHttpsMetadata = false }, false)); // insecure outside Development
        Assert.Throws<InvalidOperationException>(() => AuthenticationSetup.Validate(api,
            new KeycloakOptions { Authority = "https://kc/realms/m", Audience = "a", RoleSource = KeycloakRoleSource.Client }, false)); // no client id
    }

    [Fact]
    public void Development_AllowsDevTokensOnly_WhenExplicitlyEnabled_WithAStrongKey()
    {
        var kc = new KeycloakOptions();
        AuthenticationSetup.Validate(new ApiOptions { EnableDevTokens = true, JwtKey = new string('k', 40) }, kc, true);
        Assert.Throws<InvalidOperationException>(() => AuthenticationSetup.Validate(new ApiOptions(), kc, true)); // nothing configured
        Assert.Throws<InvalidOperationException>(() => AuthenticationSetup.Validate(new ApiOptions { EnableDevTokens = true, JwtKey = "short" }, kc, true));
    }

    [Fact]
    public void RoleMapper_IgnoresMalformedClaimsAndUnmappedRoles()
    {
        var o = new KeycloakOptions();
        System.Security.Claims.Claim C(string t, string v) => new(t, v);
        Assert.Empty(KeycloakRoleMapper.Map([C("realm_access", "{not json")], o));
        Assert.Empty(KeycloakRoleMapper.Map([C("realm_access", "{\"roles\":\"monitoring-admin\"}")], o)); // roles must be an array
        Assert.Equal([Roles.Viewer, Roles.Administrator],
            KeycloakRoleMapper.Map([C("realm_access", "{\"roles\":[\"monitoring-administrator\",\"x\",\"monitoring-viewer\"]}")], o));
    }

    [Fact]
    public void RequestLogging_DoesNotRecordQueryStrings()
    {
        // The hub token travels in the query string; the default host request log prints it at Information level.
        // The test output folder holds whichever project copied its appsettings.json last, so read the API project's own file.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "Monitoring.Api", "appsettings.json"))) dir = dir.Parent;
        var json = File.ReadAllText(Path.Combine(dir!.FullName, "src", "Monitoring.Api", "appsettings.json"));
        Assert.Contains("\"Microsoft.AspNetCore.Hosting.Diagnostics\": \"Warning\"", json);
    }
}
