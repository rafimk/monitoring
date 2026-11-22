using Monitoring.Application;
using Monitoring.Application.Jobs;
using Monitoring.Application.Scheduling;
using Monitoring.Contracts;

namespace Monitoring.UnitTests;

public class ScheduleCalculatorTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Min = TimeSpan.FromMinutes(1);

    [Fact]
    public void NotDue_ReturnsNothingAndKeepsNextDue()
    {
        var d = ScheduleCalculator.Evaluate(T0, Min, T0.AddSeconds(-1), MissedRunPolicy.CoalesceOne);
        Assert.Empty(d.RunTimes);
        Assert.Equal(T0, d.NextDueUtc);
    }

    [Theory]
    [InlineData(MissedRunPolicy.Skip)]
    [InlineData(MissedRunPolicy.CoalesceOne)]
    [InlineData(MissedRunPolicy.CatchUpCapped)]
    public void OnTime_EnqueuesExactlyOne_AndAnchorsNextDue(MissedRunPolicy policy)
    {
        var d = ScheduleCalculator.Evaluate(T0, Min, T0.AddSeconds(3), policy);
        Assert.Equal(new[] { T0 }, d.RunTimes);
        Assert.Equal(T0 + Min, d.NextDueUtc); // anchored: no drift from the 3s poll lag
        Assert.Equal(0, d.Missed);
    }

    [Fact]
    public void Skip_DropsMissedOccurrences_ButStillAdvances()
    {
        var now = T0.AddMinutes(10).AddSeconds(5);
        var d = ScheduleCalculator.Evaluate(T0, Min, now, MissedRunPolicy.Skip);
        Assert.Empty(d.RunTimes);
        Assert.Equal(T0.AddMinutes(11), d.NextDueUtc);
        Assert.Equal(10, d.Missed);
    }

    [Fact]
    public void Coalesce_EnqueuesOnlyTheLatestOccurrence()
    {
        var now = T0.AddMinutes(10).AddSeconds(5);
        var d = ScheduleCalculator.Evaluate(T0, Min, now, MissedRunPolicy.CoalesceOne);
        Assert.Equal(new[] { T0.AddMinutes(10) }, d.RunTimes);
        Assert.Equal(T0.AddMinutes(11), d.NextDueUtc);
    }

    [Fact]
    public void CatchUp_IsCapped_AndAscending()
    {
        var now = T0.AddMinutes(10).AddSeconds(5);
        var d = ScheduleCalculator.Evaluate(T0, Min, now, MissedRunPolicy.CatchUpCapped, catchUpCap: 3);
        Assert.Equal(new[] { T0.AddMinutes(8), T0.AddMinutes(9), T0.AddMinutes(10) }, d.RunTimes);
        Assert.Equal(T0.AddMinutes(11), d.NextDueUtc);
    }

    [Fact]
    public void CatchUp_BelowCap_EnqueuesAllMissed()
    {
        var d = ScheduleCalculator.Evaluate(T0, Min, T0.AddMinutes(2).AddSeconds(1), MissedRunPolicy.CatchUpCapped, 5);
        Assert.Equal(3, d.RunTimes.Count);
    }

    [Theory]
    [InlineData(MissedRunPolicy.Skip)]
    [InlineData(MissedRunPolicy.CoalesceOne)]
    [InlineData(MissedRunPolicy.CatchUpCapped)]
    public void NextDue_IsAlwaysInTheFuture_EvenAfterLongOutage(MissedRunPolicy policy)
    {
        var now = T0.AddDays(30).AddSeconds(17);
        var d = ScheduleCalculator.Evaluate(T0, TimeSpan.FromMinutes(2), now, policy);
        Assert.True(d.NextDueUtc > now);
        Assert.True(d.NextDueUtc - now <= TimeSpan.FromMinutes(2));
    }

    [Fact]
    public void RejectsNonPositiveInterval() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ScheduleCalculator.Evaluate(T0, TimeSpan.Zero, T0, MissedRunPolicy.Skip));
}

public class RetryPolicyTests
{
    [Theory]
    [InlineData(1, 10, 10)]
    [InlineData(2, 10, 20)]
    [InlineData(3, 10, 40)]
    public void Delay_IsExponential(int attempt, int baseSec, int expectedSec) =>
        Assert.Equal(TimeSpan.FromSeconds(expectedSec), RetryPolicy.Delay(attempt, baseSec));

    [Fact]
    public void Delay_IsCapped() =>
        Assert.Equal(TimeSpan.FromSeconds(3600), RetryPolicy.Delay(50, 10));

    [Theory]
    [InlineData(1, 3, true)]
    [InlineData(2, 3, true)]
    [InlineData(3, 3, false)]
    public void CanRetry_RespectsMaxAttempts(int used, int max, bool expected) =>
        Assert.Equal(expected, RetryPolicy.CanRetry(used, max));
}

public class ErrorSanitizerTests
{
    [Fact]
    public void RedactsSecrets() =>
        Assert.DoesNotContain("hunter2", ErrorSanitizer.Sanitize("login failed; Password=hunter2; token: abc123"));

    [Fact]
    public void TruncatesLongText() =>
        Assert.True(ErrorSanitizer.Sanitize(new string('x', 5000)).Length <= 501);

    [Fact]
    public void StripsNewlines() =>
        Assert.DoesNotContain('\n', ErrorSanitizer.Sanitize("a\nb"));
}

public class JobRegistryTests
{
    private sealed class Dummy : IJob
    {
        public Task<JobResult> ExecuteAsync(JobExecutionContext c, CancellationToken t) => Task.FromResult(JobResult.Ok());
    }

    [Fact]
    public void RejectsDuplicateKeys() =>
        Assert.Throws<InvalidOperationException>(() => new JobRegistry(new[]
        {
            new JobRegistration("a.job", "A", "", typeof(Dummy)),
            new JobRegistration("a.job", "B", "", typeof(Dummy)),
        }));

    [Theory]
    [InlineData("Bad Key")]
    [InlineData("x")]
    [InlineData("UPPER")]
    public void RejectsInvalidKeys(string key) =>
        Assert.Throws<InvalidOperationException>(() => new JobRegistry(new[] { new JobRegistration(key, "A", "", typeof(Dummy)) }));

    [Fact]
    public void RejectsNonJobTypes() =>
        Assert.Throws<InvalidOperationException>(() => new JobRegistry(new[] { new JobRegistration("ok.job", "A", "", typeof(string)) }));

    [Fact]
    public void ResolvesRegisteredKey()
    {
        var r = new JobRegistry(new[] { new JobRegistration("ok.job", "A", "", typeof(Dummy)) });
        Assert.True(r.TryGet("ok.job", out _));
        Assert.False(r.TryGet("missing.job", out _));
    }
}
