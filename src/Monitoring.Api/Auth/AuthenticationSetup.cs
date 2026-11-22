using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Monitoring.Api.Realtime;

namespace Monitoring.Api.Auth;

public static class AuthenticationSetup
{
    public const string PolicyScheme = "Monitoring";
    public const string KeycloakScheme = "Keycloak";
    public const string DevScheme = "DevBearer";

    /// <summary>Fails fast and clearly at startup instead of running with an unauthenticated or locally-signed production API.</summary>
    public static void Validate(ApiOptions api, KeycloakOptions kc, bool isDevelopment)
    {
        var devTokens = api.EnableDevTokens;
        if (devTokens && !isDevelopment)
            throw new InvalidOperationException("Api:EnableDevTokens may only be enabled in the Development environment.");
        if (devTokens && (string.IsNullOrWhiteSpace(api.JwtKey) || api.JwtKey.Length < 32))
            throw new InvalidOperationException("Api:JwtKey (at least 32 characters) is required when Api:EnableDevTokens is on. Use user-secrets.");

        if (!kc.IsConfigured)
        {
            if (!isDevelopment || !devTokens)
                throw new InvalidOperationException(
                    "Keycloak:Authority and Keycloak:Audience must be configured. " +
                    "(Only a Development environment with Api:EnableDevTokens=true may run without Keycloak.)");
            return;
        }
        if (!Uri.TryCreate(kc.Authority, UriKind.Absolute, out var authority))
            throw new InvalidOperationException("Keycloak:Authority must be an absolute URL such as https://keycloak.example.com/realms/monitoring.");
        if (kc.RequireHttpsMetadata && authority.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Keycloak:Authority must use https (set Keycloak:RequireHttpsMetadata=false only for local development).");
        if (!kc.RequireHttpsMetadata && !isDevelopment)
            throw new InvalidOperationException("Keycloak:RequireHttpsMetadata=false is only allowed in the Development environment.");
        if (string.IsNullOrWhiteSpace(kc.Audience))
            throw new InvalidOperationException("Keycloak:Audience is required: tokens must be issued for this API (audience mapper).");
        if (kc.RoleSource == KeycloakRoleSource.Client && string.IsNullOrWhiteSpace(kc.ClientId))
            throw new InvalidOperationException("Keycloak:ClientId is required when Keycloak:RoleSource is Client.");
    }

    public static IServiceCollection AddMonitoringAuthentication(this IServiceCollection services, ApiOptions api, KeycloakOptions kc)
    {
        var auth = services.AddAuthentication(PolicyScheme)
            .AddPolicyScheme(PolicyScheme, PolicyScheme, o =>
            {
                // Pick the validation scheme from the (unvalidated) issuer; the chosen scheme then does the real validation.
                o.ForwardDefaultSelector = ctx =>
                {
                    var token = ExtractToken(ctx.Request);
                    if (api.EnableDevTokens && token is not null && IssuerOf(token) == api.JwtIssuer) return DevScheme;
                    return kc.IsConfigured ? KeycloakScheme : DevScheme;
                };
            });

        if (kc.IsConfigured)
        {
            auth.AddJwtBearer(KeycloakScheme, o =>
            {
                o.Authority = kc.Authority;
                if (!string.IsNullOrWhiteSpace(kc.MetadataAddress)) o.MetadataAddress = kc.MetadataAddress;
                o.RequireHttpsMetadata = kc.RequireHttpsMetadata;
                o.MapInboundClaims = false;
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true, ValidIssuer = kc.Authority!.TrimEnd('/'),
                    ValidateAudience = true, ValidAudience = kc.Audience,
                    ValidateIssuerSigningKey = true,
                    ValidateLifetime = true, RequireExpirationTime = true,
                    ClockSkew = TimeSpan.FromSeconds(kc.ClockSkewSeconds),
                    NameClaimType = "preferred_username", RoleClaimType = "role",
                };
                o.Events = new JwtBearerEvents
                {
                    OnMessageReceived = AcceptHubQueryToken,
                    OnTokenValidated = ctx =>
                    {
                        var identity = (ClaimsIdentity)ctx.Principal!.Identity!;
                        // Only mapped roles become "role" claims; a user with none is rejected by every policy (403).
                        foreach (var role in KeycloakRoleMapper.Map(ctx.Principal.Claims, kc))
                            identity.AddClaim(new Claim("role", role));
                        if (identity.FindFirst("preferred_username") is null)
                        {
                            var name = identity.FindFirst("name")?.Value ?? identity.FindFirst("email")?.Value ?? identity.FindFirst("sub")?.Value;
                            if (name is not null) identity.AddClaim(new Claim("preferred_username", name));
                        }
                        return Task.CompletedTask;
                    },
                };
            });
        }

        if (api.EnableDevTokens)
        {
            auth.AddJwtBearer(DevScheme, o =>
            {
                o.MapInboundClaims = false;
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true, ValidIssuer = api.JwtIssuer,
                    ValidateAudience = true, ValidAudience = api.JwtAudience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(api.JwtKey)),
                    ValidateLifetime = true, ClockSkew = TimeSpan.FromSeconds(30),
                    RoleClaimType = "role", NameClaimType = "name",
                };
                o.Events = new JwtBearerEvents { OnMessageReceived = AcceptHubQueryToken };
            });
        }
        return services;
    }

    /// <summary>
    /// Browsers cannot set an Authorization header on a WebSocket, so SignalR sends access_token in the query string.
    /// It is honoured for the exact hub path only; on any other URL the query token is ignored (the request stays anonymous).
    /// </summary>
    private static Task AcceptHubQueryToken(MessageReceivedContext ctx)
    {
        if (ctx.Request.Path.Equals(HubPaths.Monitoring, StringComparison.OrdinalIgnoreCase)
            && ctx.Request.Query.TryGetValue("access_token", out var t) && !string.IsNullOrEmpty(t))
            ctx.Token = t;
        return Task.CompletedTask;
    }

    private static string? ExtractToken(HttpRequest req)
    {
        var header = req.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return header["Bearer ".Length..].Trim();
        if (req.Path.Equals(HubPaths.Monitoring, StringComparison.OrdinalIgnoreCase) && req.Query.TryGetValue("access_token", out var t))
            return t.ToString();
        return null;
    }

    private static string? IssuerOf(string token)
    {
        try
        {
            var h = new JwtSecurityTokenHandler();
            return h.CanReadToken(token) ? h.ReadJwtToken(token).Issuer : null;
        }
        catch { return null; }
    }
}
