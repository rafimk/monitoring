using System.Security.Claims;
using System.Text.Json;

namespace Monitoring.Api.Auth;

/// <summary>
/// Turns Keycloak role claims into the API's three roles. This is the single, explicit translation point: nothing the
/// browser sends or parses is trusted, and any Keycloak role that is not mapped here is ignored.
/// </summary>
public static class KeycloakRoleMapper
{
    public static IReadOnlyList<string> Map(IEnumerable<Claim> claims, KeycloakOptions o)
    {
        var granted = ReadRoles(claims, o).ToHashSet(StringComparer.Ordinal);
        var result = new List<string>();
        if (granted.Contains(o.ViewerRole)) result.Add(Roles.Viewer);
        if (granted.Contains(o.OperatorRole)) result.Add(Roles.Operator);
        if (granted.Contains(o.AdministratorRole)) result.Add(Roles.Administrator);
        return result;
    }

    private static IEnumerable<string> ReadRoles(IEnumerable<Claim> claims, KeycloakOptions o)
    {
        var (claimType, clientId) = o.RoleSource == KeycloakRoleSource.Client
            ? ("resource_access", o.ClientId)
            : ("realm_access", null);

        foreach (var claim in claims.Where(c => c.Type == claimType))
        {
            JsonElement root;
            try { root = JsonDocument.Parse(claim.Value).RootElement; }
            catch (JsonException) { continue; } // a malformed claim grants nothing

            if (clientId is not null)
            {
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(clientId, out root)) continue;
            }
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("roles", out var roles) || roles.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var r in roles.EnumerateArray())
                if (r.ValueKind == JsonValueKind.String && r.GetString() is { } s) yield return s;
        }
    }
}
