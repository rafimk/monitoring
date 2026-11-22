using Microsoft.Extensions.Options;
using Monitoring.Worker;

namespace Monitoring.UnitTests;

public class WorkerIdentityTests
{
    private static WorkerIdentity Identity(string? configured) => new(Options.Create(new WorkerOptions { WorkerId = configured }));

    [Fact]
    public void TwoInstancesWithTheSameConfiguredName_GetDistinctIdentities()
    {
        var a = Identity("billing-worker");
        var b = Identity("billing-worker");

        Assert.NotEqual(a.Id, b.Id); // heartbeat rows and lease ownership are keyed by Id, so they cannot collide
        Assert.StartsWith("billing-worker-", a.Id);
        Assert.StartsWith("billing-worker-", b.Id);
    }

    [Fact]
    public void WithoutAConfiguredName_UsesHostAndProcessPlusSuffix()
    {
        var id = Identity(null).Id;
        Assert.Contains(Environment.MachineName, id);
        Assert.NotEqual(Identity(null).Id, id);
    }

    [Fact]
    public void LongNames_AreTruncatedSoTheIdFitsTheColumn() =>
        Assert.True(Identity(new string('x', 200)).Id.Length <= 100);

    [Theory]
    [InlineData(null, 60, 15, true)]
    [InlineData("worker-1", 60, 15, true)]
    [InlineData("bad name!", 60, 15, false)]
    [InlineData("a/b", 60, 15, false)]
    [InlineData("ok", 20, 15, false)]   // lease must be >= 2x heartbeat
    [InlineData("ok", 30, 15, true)]
    public void Validator_EnforcesNameAndLeaseRules(string? name, int lease, int heartbeat, bool valid)
    {
        var result = new WorkerOptionsValidator().Validate(null,
            new WorkerOptions { WorkerId = name, LeaseSeconds = lease, HeartbeatSeconds = heartbeat });
        Assert.Equal(valid, result.Succeeded);
    }

    [Fact]
    public void SafetyMargin_IsAFractionOfTheLease_AndLeavesRoomForAHeartbeat()
    {
        var o = new WorkerOptions { LeaseSeconds = 60, HeartbeatSeconds = 15 };
        Assert.Equal(TimeSpan.FromSeconds(15), o.LeaseSafetyMargin);
        // Worst-case gap between renewals (one heartbeat) plus the margin must still fit inside the lease.
        Assert.True(TimeSpan.FromSeconds(o.HeartbeatSeconds) + o.LeaseSafetyMargin < TimeSpan.FromSeconds(o.LeaseSeconds));
    }
}
