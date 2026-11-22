using Monitoring.Worker;
using Monitoring.Worker.Services;

namespace Monitoring.UnitTests;

public class RetentionScheduleTests
{
    private static readonly TimeSpan TwoAm = TimeSpan.FromHours(2);
    private static DateTime Utc(int y, int mo, int d, int h = 0, int mi = 0) => new(y, mo, d, h, mi, 0, DateTimeKind.Utc);
    private static TimeZoneInfo Berlin => TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");

    [Fact]
    public void Utc_NextRun_IsTheFollowingTwoAm()
    {
        Assert.Equal(Utc(2026, 6, 16, 2), RetentionSchedule.NextAfter(Utc(2026, 6, 15, 12), TwoAm, TimeZoneInfo.Utc));
        Assert.Equal(Utc(2026, 6, 15, 2), RetentionSchedule.NextAfter(Utc(2026, 6, 15, 1, 59), TwoAm, TimeZoneInfo.Utc));
    }

    [Fact]
    public void NextRun_IsStrictlyAfter_SoARunExactlyOnTheMinuteIsNotRepeated()
    {
        Assert.Equal(Utc(2026, 6, 16, 2), RetentionSchedule.NextAfter(Utc(2026, 6, 15, 2), TwoAm, TimeZoneInfo.Utc));
        Assert.Equal(Utc(2026, 6, 15, 2), RetentionSchedule.LatestAtOrBefore(Utc(2026, 6, 15, 2), TwoAm, TimeZoneInfo.Utc));
    }

    [Fact]
    public void LatestAtOrBefore_ReturnsThePreviousNight_BeforeTodaysRunTime()
    {
        Assert.Equal(Utc(2026, 6, 14, 2), RetentionSchedule.LatestAtOrBefore(Utc(2026, 6, 15, 1, 59), TwoAm, TimeZoneInfo.Utc));
        Assert.Equal(Utc(2026, 6, 15, 2), RetentionSchedule.LatestAtOrBefore(Utc(2026, 6, 15, 2, 1), TwoAm, TimeZoneInfo.Utc));
    }

    [Fact]
    public void LocalTimeZone_FollowsWallClock_AcrossStandardAndSummerTime()
    {
        // Winter (UTC+1): 02:00 local = 01:00Z. Summer (UTC+2): 02:00 local = 00:00Z.
        Assert.Equal(Utc(2026, 1, 15, 1), RetentionSchedule.NextAfter(Utc(2026, 1, 14, 12), TwoAm, Berlin));
        Assert.Equal(Utc(2026, 7, 15, 0), RetentionSchedule.NextAfter(Utc(2026, 7, 14, 12), TwoAm, Berlin));
    }

    [Fact]
    public void SpringForward_SkippedLocalTime_RunsAtTheFirstValidInstantAfterTheGap()
    {
        // 2027-03-28: Berlin jumps 02:00 -> 03:00, so 02:30 does not exist. Expect 03:00 local (= 01:00Z), once.
        var half = TimeSpan.FromHours(2.5);
        var next = RetentionSchedule.NextAfter(Utc(2027, 3, 27, 12), half, Berlin);
        Assert.Equal(Utc(2027, 3, 28, 1), next);
        // The following day is back to the normal 02:30 local (= 00:30Z, summer time).
        Assert.Equal(Utc(2027, 3, 29, 0, 30), RetentionSchedule.NextAfter(next, half, Berlin));
    }

    [Fact]
    public void FallBack_RepeatedLocalTime_RunsOnceOnTheFirstOccurrence()
    {
        // 2026-10-25: Berlin goes 03:00 -> 02:00, so 02:30 happens twice (00:30Z and 01:30Z). Expect the first.
        var half = TimeSpan.FromHours(2.5);
        var first = RetentionSchedule.NextAfter(Utc(2026, 10, 24, 12), half, Berlin);
        Assert.Equal(Utc(2026, 10, 25, 0, 30), first);
        // After it ran, the second occurrence must not trigger another run: next is the following day.
        var after = RetentionSchedule.NextAfter(first, half, Berlin);
        Assert.Equal(Utc(2026, 10, 26, 1, 30), after);
        Assert.Equal(first, RetentionSchedule.LatestAtOrBefore(Utc(2026, 10, 25, 1, 45), half, Berlin));
    }

    [Fact]
    public void ConsecutiveOccurrences_AreNeverEqual_AndAlwaysIncrease()
    {
        var at = TimeSpan.FromHours(2.5);
        var t = Utc(2026, 1, 1);
        var previous = RetentionSchedule.NextAfter(t, at, Berlin);
        for (var i = 0; i < 800; i++)
        {
            var next = RetentionSchedule.NextAfter(previous, at, Berlin);
            Assert.True(next > previous, $"{next:u} must be after {previous:u}");
            Assert.InRange((next - previous).TotalHours, 22, 26);
            previous = next;
        }
    }
}

public class RetentionOptionsTests
{
    private static string? Check(Action<RetentionOptions> change)
    {
        var o = new WorkerOptions();
        change(o.Retention);
        var result = new WorkerOptionsValidator().Validate(null, o);
        return result.Succeeded ? null : result.FailureMessage;
    }

    [Fact]
    public void Defaults_AreValid_AndMatchTheDocumentedPolicies()
    {
        var r = new WorkerOptions().Retention;
        Assert.True(r.Enabled);
        Assert.Equal(30, r.JobHistoryRetentionDays);
        Assert.Equal(365, r.AuditRetentionDays);
        Assert.Equal(7, r.ChangeFeedRetentionDays);
        Assert.Equal("02:00", r.RunAt);
        Assert.Equal("UTC", r.TimeZoneId);
        Assert.Null(Check(_ => { }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(36501)]
    public void EachRetentionPolicy_IsRangeChecked(int days)
    {
        Assert.Contains("JobHistoryRetentionDays", Check(r => r.JobHistoryRetentionDays = days));
        Assert.Contains("AuditRetentionDays", Check(r => r.AuditRetentionDays = days));
        Assert.Contains("ChangeFeedRetentionDays", Check(r => r.ChangeFeedRetentionDays = days));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(36500)]
    public void RetentionBounds_AreInclusive(int days) =>
        Assert.Null(Check(r => { r.JobHistoryRetentionDays = days; r.AuditRetentionDays = days; r.ChangeFeedRetentionDays = days; }));

    [Theory]
    [InlineData(0)]
    [InlineData(10001)]
    public void BatchSize_IsRangeChecked(int size) => Assert.Contains("BatchSize", Check(r => r.BatchSize = size));

    [Theory]
    [InlineData("")]
    [InlineData("25:00")]
    [InlineData("2am")]
    [InlineData("24:00")]
    public void RunAt_MustBeATwentyFourHourTime(string value) => Assert.Contains("RunAt", Check(r => r.RunAt = value));

    [Theory]
    [InlineData("00:00")]
    [InlineData("23:59")]
    public void RunAt_AcceptsTheWholeDay(string value) => Assert.Null(Check(r => r.RunAt = value));

    [Fact]
    public void TimeZone_MustExist()
    {
        Assert.Contains("TimeZoneId", Check(r => r.TimeZoneId = "Mars/Olympus"));
        Assert.Null(Check(r => r.TimeZoneId = "Europe/Berlin"));
    }
}
