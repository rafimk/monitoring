using Monitoring.Contracts;

namespace Monitoring.Application.Scheduling;

/// <param name="RunTimes">Scheduled occurrence times (UTC, ascending) that should be enqueued now.</param>
/// <param name="NextDueUtc">The next due time to persist (always strictly after <c>now</c>).</param>
/// <param name="Missed">Occurrences that were overdue beyond the first.</param>
public sealed record ScheduleDecision(IReadOnlyList<DateTime> RunTimes, DateTime NextDueUtc, int Missed);

/// <summary>
/// Pure fixed-interval schedule math. The next due time is anchored to the previous due time
/// (no drift) and is never in the past after evaluation.
/// </summary>
public static class ScheduleCalculator
{
    public const int DefaultCatchUpCap = 5;

    public static ScheduleDecision Evaluate(
        DateTime nextDueUtc,
        TimeSpan interval,
        DateTime nowUtc,
        MissedRunPolicy policy,
        int catchUpCap = DefaultCatchUpCap)
    {
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval), "Interval must be positive.");
        if (catchUpCap < 1) throw new ArgumentOutOfRangeException(nameof(catchUpCap));

        if (nowUtc < nextDueUtc)
            return new ScheduleDecision(Array.Empty<DateTime>(), nextDueUtc, 0);

        // Number of occurrences with time <= now, starting at nextDueUtc.
        long count = (nowUtc - nextDueUtc).Ticks / interval.Ticks + 1;
        var last = nextDueUtc + TimeSpan.FromTicks(interval.Ticks * (count - 1));
        var next = last + interval;
        var missed = (int)Math.Min(count - 1, int.MaxValue);

        IReadOnlyList<DateTime> times = policy switch
        {
            MissedRunPolicy.Skip => count == 1 ? new[] { last } : Array.Empty<DateTime>(),
            MissedRunPolicy.CoalesceOne => new[] { last },
            MissedRunPolicy.CatchUpCapped => CatchUp(last, interval, (int)Math.Min(count, catchUpCap)),
            _ => throw new ArgumentOutOfRangeException(nameof(policy))
        };

        return new ScheduleDecision(times, next, missed);
    }

    private static DateTime[] CatchUp(DateTime last, TimeSpan interval, int n)
    {
        var result = new DateTime[n];
        for (var i = 0; i < n; i++)
            result[i] = last - TimeSpan.FromTicks(interval.Ticks * (n - 1 - i));
        return result;
    }
}
