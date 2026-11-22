using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Monitoring.Contracts;

namespace Monitoring.Application.Jobs;

/// <summary>Immutable lookup of job implementations registered in the host via DI.</summary>
public sealed class JobRegistry
{
    private static readonly Regex KeyPattern = new("^[a-z0-9][a-z0-9._-]{1,99}$", RegexOptions.Compiled);
    private readonly Dictionary<string, JobRegistration> _byKey;

    public JobRegistry(IEnumerable<JobRegistration> registrations)
    {
        _byKey = new Dictionary<string, JobRegistration>(StringComparer.Ordinal);
        foreach (var r in registrations)
        {
            if (!KeyPattern.IsMatch(r.Key))
                throw new InvalidOperationException($"Job key '{r.Key}' is invalid. Use 2-100 chars of a-z, 0-9, '.', '_' or '-'.");
            if (!typeof(IJob).IsAssignableFrom(r.ImplementationType))
                throw new InvalidOperationException($"Job '{r.Key}' implementation does not implement IJob.");
            if (!_byKey.TryAdd(r.Key, r))
                throw new InvalidOperationException($"Duplicate job key '{r.Key}'.");
        }
    }

    public IReadOnlyCollection<JobRegistration> All => _byKey.Values;
    public bool TryGet(string key, out JobRegistration registration) => _byKey.TryGetValue(key, out registration!);
}

public static class JobRegistrationExtensions
{
    public static IServiceCollection AddJobRegistry(this IServiceCollection services)
    {
        services.AddSingleton<JobRegistry>();
        return services;
    }

    /// <summary>Registers a job implementation under a stable key. Implementations are resolved per run in a new scope.</summary>
    public static IServiceCollection AddJob<TJob>(this IServiceCollection services, string key, string displayName, string description = "")
        where TJob : class, IJob
    {
        services.AddScoped<TJob>();
        services.AddSingleton(new JobRegistration(key, displayName, description, typeof(TJob)));
        return services;
    }
}
