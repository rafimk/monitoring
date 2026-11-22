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

    /// <summary>Set false to run the API without the live-update dispatcher (clients then rely on polling).</summary>
    public bool ChangeFeedEnabled { get; set; } = true;
    [Range(50, 60000)] public int ChangeFeedPollMilliseconds { get; set; } = 1000;
    [Range(1, 5000)] public int ChangeFeedBatchSize { get; set; } = 500;
    /// <summary>More distinct run changes than this in one poll are sent as a single "bulk" notification.</summary>
    [Range(1, 1000)] public int MaxRunNotificationsPerBatch { get; set; } = 50;
    /// <summary>How often the dispatcher compares the set of live workers to detect workers appearing or dying.</summary>
    [Range(1, 600)] public int WorkerWatchSeconds { get; set; } = 5;
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
