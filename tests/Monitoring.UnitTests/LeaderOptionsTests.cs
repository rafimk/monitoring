using System.ComponentModel.DataAnnotations;
using Monitoring.Worker;

namespace Monitoring.UnitTests;

public class LeaderOptionsTests
{
    private static bool Valid(WorkerOptions o) => new WorkerOptionsValidator().Validate(null, o).Succeeded;

    [Fact]
    public void Defaults_EnableActiveStandby_WithAFixedResourceName()
    {
        var o = new WorkerOptions();
        Assert.Equal(WorkerLeaderMode.ActiveStandby, o.LeaderMode);
        Assert.Equal("monitoring-worker-leader", o.LeaderLockResource);
        Assert.Equal(5, o.StandbyRetrySeconds);
        Assert.Equal(5, o.LeaderCheckSeconds);
        Assert.True(Valid(o));
    }

    [Theory]
    [InlineData(60, 30, true)]   // proof at least twice per lease
    [InlineData(60, 31, false)]
    [InlineData(10, 5, true)]
    [InlineData(10, 6, false)]
    public void LeaderCheck_MustFitInsideTheLease_WhenElectionIsOn(int lease, int check, bool valid)
    {
        var o = new WorkerOptions { LeaseSeconds = lease, HeartbeatSeconds = 2, LeaderCheckSeconds = check };
        Assert.Equal(valid, Valid(o));
    }

    [Fact]
    public void LeaderCheck_IsNotConstrained_WhenElectionIsDisabled()
    {
        Assert.True(Valid(new WorkerOptions { LeaseSeconds = 10, HeartbeatSeconds = 2, LeaderCheckSeconds = 200, LeaderMode = WorkerLeaderMode.Disabled }));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("monitoring-worker-leader", true)]
    public void LockResource_MustBeNamed(string resource, bool valid) =>
        Assert.Equal(valid, Valid(new WorkerOptions { LeaderLockResource = resource }));

    [Fact]
    public void LockResource_IsLimitedToWhatSqlServerAccepts()
    {
        Assert.True(Valid(new WorkerOptions { LeaderLockResource = new string('x', 255) }));
        Assert.False(Valid(new WorkerOptions { LeaderLockResource = new string('x', 256) }));
    }

    [Fact]
    public void UndefinedMode_IsRejected() => Assert.False(Valid(new WorkerOptions { LeaderMode = (WorkerLeaderMode)42 }));

    [Theory]
    [InlineData(nameof(WorkerOptions.StandbyRetrySeconds), 0)]
    [InlineData(nameof(WorkerOptions.StandbyRetrySeconds), 301)]
    [InlineData(nameof(WorkerOptions.LeaderCheckSeconds), 0)]
    [InlineData(nameof(WorkerOptions.LeaderCheckSeconds), 301)]
    public void Periods_AreRangeChecked(string property, int value)
    {
        var o = new WorkerOptions();
        typeof(WorkerOptions).GetProperty(property)!.SetValue(o, value);
        Assert.False(Validator.TryValidateObject(o, new ValidationContext(o), [], validateAllProperties: true));
    }
}
