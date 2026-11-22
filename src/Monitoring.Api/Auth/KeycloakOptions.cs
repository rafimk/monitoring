using System.ComponentModel.DataAnnotations;

namespace Monitoring.Api.Auth;

/// <summary>Where the API learns who the caller is. Production tokens are issued by Keycloak; nothing here is a secret.</summary>
public sealed class KeycloakOptions
{
    public const string Section = "Keycloak";

    /// <summary>Realm URL, e.g. https://keycloak.example.com/realms/monitoring. It is the expected token issuer (iss).</summary>
    public string? Authority { get; set; }

    /// <summary>Only when the metadata cannot be fetched from <see cref="Authority"/> (internal hostname behind a proxy).</summary>
    public string? MetadataAddress { get; set; }

    /// <summary>The value the API requires in the token "aud" claim (added by an audience mapper in Keycloak).</summary>
    public string? Audience { get; set; }

    /// <summary>Disable only for a local http Keycloak. Production must keep this true.</summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>"Realm" reads realm_access.roles; "Client" reads resource_access.{ClientId}.roles.</summary>
    public KeycloakRoleSource RoleSource { get; set; } = KeycloakRoleSource.Realm;

    /// <summary>Required when RoleSource is Client: the client whose roles carry the monitoring permissions.</summary>
    public string? ClientId { get; set; }

    /// <summary>Keycloak role names that grant each monitoring role. A user holding none of them is rejected.</summary>
    public string ViewerRole { get; set; } = "monitoring-viewer";
    public string OperatorRole { get; set; } = "monitoring-operator";
    public string AdministratorRole { get; set; } = "monitoring-administrator";

    [Range(0, 300)] public int ClockSkewSeconds { get; set; } = 30;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Authority);
}

public enum KeycloakRoleSource { Realm, Client }
