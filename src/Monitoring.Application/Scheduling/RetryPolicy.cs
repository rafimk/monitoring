namespace Monitoring.Application.Scheduling;

public static class RetryPolicy
{
    /// <summary>Delay before the next attempt after <paramref name="failedAttempt"/> (1-based) failed. Exponential, capped.</summary>
    public static TimeSpan Delay(int failedAttempt, int baseDelaySeconds, int maxDelaySeconds = 3600)
    {
        if (failedAttempt < 1) throw new ArgumentOutOfRangeException(nameof(failedAttempt));
        var exp = Math.Min(failedAttempt - 1, 20);
        var seconds = Math.Min((double)baseDelaySeconds * Math.Pow(2, exp), maxDelaySeconds);
        return TimeSpan.FromSeconds(seconds);
    }

    public static bool CanRetry(int attemptsUsed, int maxAttempts) => attemptsUsed < maxAttempts;
}
