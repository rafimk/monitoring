using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace Monitoring.Api.Realtime;

/// <summary>Server-to-client only. Every authenticated user with at least the viewer role may listen.</summary>
[Authorize(Policy = Policies.Viewer)]
public sealed class MonitoringHub(DispatcherLeadership leadership, IOptions<ApiOptions> options, ILogger<MonitoringHub> log)
    : Hub<IMonitoringClient>
{
    public override Task OnConnectedAsync()
    {
        var http = Context.GetHttpContext();
        var origin = http?.Request.Headers.Origin.ToString();
        var allowed = options.Value.AllowedOrigins;
        // Browsers always send Origin; reject any that is not a configured dashboard origin. Non-browser clients send none
        // and are still bound by the token.
        if (!string.IsNullOrEmpty(origin) && !allowed.Contains(origin, StringComparer.OrdinalIgnoreCase))
        {
            log.LogWarning("Hub connection rejected: origin {Origin} is not an allowed dashboard origin", origin);
            Context.Abort();
            return Task.CompletedTask;
        }
        // Only the instance holding the dispatcher lock receives change events. A second API instance would silently
        // stay stale, so it refuses hub connections and clients fall back to polling.
        if (!leadership.IsLeader)
        {
            log.LogWarning("Hub connection rejected: this API instance is not the change-feed dispatcher (single instance supported)");
            Context.Abort();
            return Task.CompletedTask;
        }
        return base.OnConnectedAsync();
    }
}
