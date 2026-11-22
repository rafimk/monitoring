using System.IdentityModel.Tokens.Jwt;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Monitoring.Api.Realtime;

namespace Monitoring.IntegrationTests.Realtime;

/// <summary>Stands in for the Keycloak realm: one signing key, one issuer, one audience. No network involved.</summary>
public static class FakeKeycloak
{
    public const string Authority = "https://keycloak.test/realms/monitoring";
    public const string Audience = "monitoring-api";
    public const string ClientId = "monitoring-api";

    public static readonly RsaSecurityKey SigningKey = new(RSA.Create(2048)) { KeyId = "test-key" };
    public static readonly RsaSecurityKey OtherKey = new(RSA.Create(2048)) { KeyId = "test-key" }; // same kid, different key

    public static OpenIdConnectConfiguration Configuration()
    {
        var c = new OpenIdConnectConfiguration { Issuer = Authority };
        c.SigningKeys.Add(SigningKey);
        return c;
    }

    /// <summary>Issues a Keycloak-shaped access token. Every deviation is a parameter so tests can break exactly one thing.</summary>
    public static string Token(
        string[]? realmRoles = null, string[]? clientRoles = null, string user = "alice",
        string issuer = Authority, string? audience = Audience, SecurityKey? key = null,
        TimeSpan? lifetime = null, string clientIdForRoles = ClientId)
    {
        var now = DateTime.UtcNow;
        var life = lifetime ?? TimeSpan.FromMinutes(5);
        var claims = new List<Claim> { new("preferred_username", user), new("sub", Guid.NewGuid().ToString()) };
        var payload = new JwtPayload(issuer, audience, claims,
            notBefore: life < TimeSpan.Zero ? now + life - TimeSpan.FromMinutes(5) : now.AddSeconds(-5),
            expires: now + life);
        if (realmRoles is not null) payload["realm_access"] = new Dictionary<string, object> { ["roles"] = realmRoles };
        if (clientRoles is not null)
            payload["resource_access"] = new Dictionary<string, object> { [clientIdForRoles] = new Dictionary<string, object> { ["roles"] = clientRoles } };
        var creds = new SigningCredentials(key ?? SigningKey, SecurityAlgorithms.RsaSha256);
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(new JwtHeader(creds), payload));
    }

    public static string Viewer(string user = "viewer-user") => Token(["monitoring-viewer"], user: user);
    public static string Operator(string user = "operator-user") => Token(["monitoring-operator"], user: user);
    public static string Admin(string user = "admin-user") => Token(["monitoring-administrator"], user: user);
}

/// <summary>
/// The real API configured the way production is (Production environment, Keycloak, no dev tokens) but validating against
/// <see cref="FakeKeycloak"/>'s in-memory realm configuration. Each instance points at the connection string it is given.
/// </summary>
public sealed class KeycloakApiFactory(string connectionString, Dictionary<string, string?>? settings = null, bool dispatcher = false)
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.UseSetting("ConnectionStrings:Monitoring", connectionString);
        builder.UseSetting("Keycloak:Authority", FakeKeycloak.Authority);
        builder.UseSetting("Keycloak:Audience", FakeKeycloak.Audience);
        builder.UseSetting("Api:AllowedOrigins:0", "http://localhost:5173");
        builder.UseSetting("Api:ChangeFeedEnabled", dispatcher ? "true" : "false");
        builder.UseSetting("Api:ChangeFeedPollMilliseconds", "100");
        builder.UseSetting("Api:WorkerStaleSeconds", "5");
        builder.UseSetting("Api:WorkerWatchSeconds", "1");
        foreach (var (k, v) in settings ?? []) builder.UseSetting(k, v);

        builder.ConfigureTestServices(s =>
            s.Configure<JwtBearerOptions>(AuthenticationSetupNames.Keycloak, o => o.Configuration = FakeKeycloak.Configuration()));
    }

    public async Task<HttpClient> ClientAsync(string? token)
    {
        var c = CreateClient();
        if (token is not null) c.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return await Task.FromResult(c);
    }

    public ChangeFeedDispatcher Dispatcher => Services.GetRequiredService<ChangeFeedDispatcher>();

    /// <summary>A SignalR client wired to the in-memory test server (WebSockets, token in the query string as a browser would).</summary>
    public HubConnection Hub(string? token, HttpTransportType transport = HttpTransportType.WebSockets, string? origin = null,
        Func<Task<string?>>? tokenProvider = null)
    {
        var server = Server;
        return new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, HubPaths.Monitoring), o =>
            {
                o.Transports = transport;
                o.HttpMessageHandlerFactory = _ => server.CreateHandler();
                o.AccessTokenProvider = tokenProvider ?? (() => Task.FromResult(token));
                if (origin is not null) o.Headers["Origin"] = origin;
                o.WebSocketFactory = async (ctx, ct) =>
                {
                    var ws = server.CreateWebSocketClient();
                    if (origin is not null) ws.ConfigureRequest = r => r.Headers["Origin"] = origin;
                    // A browser cannot set headers on a WebSocket, so it passes the token as access_token (what the hub must accept).
                    var t = await (tokenProvider ?? (() => Task.FromResult(token)))();
                    var uri = t is null ? ctx.Uri : new UriBuilder(ctx.Uri) { Query = (ctx.Uri.Query.TrimStart('?') + "&access_token=" + Uri.EscapeDataString(t)).TrimStart('&') }.Uri;
                    return await ws.ConnectAsync(uri, ct);
                };
            })
            .Build();
    }
}

public static class AuthenticationSetupNames
{
    public const string Keycloak = Monitoring.Api.Auth.AuthenticationSetup.KeycloakScheme;
}

/// <summary>Collects hub notifications so tests can await specific ones instead of sleeping.</summary>
public sealed class HubRecorder
{
    public List<RunChangedMessage> Runs { get; } = [];
    public List<JobChangedMessage> Jobs { get; } = [];
    public List<WorkersChangedMessage> Workers { get; } = [];
    public int Summaries;
    private readonly object _gate = new();

    public HubRecorder(HubConnection c)
    {
        c.On<RunChangedMessage>("RunChanged", m => { lock (_gate) Runs.Add(m); });
        c.On<JobChangedMessage>("JobChanged", m => { lock (_gate) Jobs.Add(m); });
        c.On<WorkersChangedMessage>("WorkersChanged", m => { lock (_gate) Workers.Add(m); });
        c.On("SummaryChanged", () => Interlocked.Increment(ref Summaries));
    }

    public List<RunChangedMessage> RunSnapshot() { lock (_gate) return [.. Runs]; }
    public List<JobChangedMessage> JobSnapshot() { lock (_gate) return [.. Jobs]; }
    public List<WorkersChangedMessage> WorkerSnapshot() { lock (_gate) return [.. Workers]; }

    public static async Task EventuallyAsync(Func<bool> condition, string because, int timeoutSeconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out after {timeoutSeconds}s waiting for: {because}");
            await Task.Delay(50);
        }
    }
}
