using System.ComponentModel.DataAnnotations;

namespace Monitoring.Api;

public sealed class ApiOptions
{
    public const string Section = "Api";

    public string JwtKey { get; set; } = "";
    [Required] public string JwtIssuer { get; set; } = "monitoring";
    [Required] public string JwtAudience { get; set; } = "monitoring-web";
    public string[] AllowedOrigins { get; set; } = [];
    /// <summary>Seconds without a heartbeat before a worker is shown as stale.</summary>
    [Range(5, 3600)] public int WorkerStaleSeconds { get; set; } = 45;
    /// <summary>Development only: exposes POST /api/v1/auth/dev-token to mint tokens without an identity provider.</summary>
    public bool EnableDevTokens { get; set; }
}

public static class Roles
{
    public const string Viewer = "viewer";
    public const string Operator = "operator";
    public const string Administrator = "administrator";
    public static readonly string[] All = [Viewer, Operator, Administrator];
}

public static class Policies
{
    public const string Viewer = "Viewer";
    public const string Operator = "Operator";
    public const string Administrator = "Administrator";
}
