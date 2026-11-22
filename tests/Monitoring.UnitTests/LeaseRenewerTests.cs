using Microsoft.Extensions.Time.Testing;
using Monitoring.Application.Leasing;

namespace Monitoring.UnitTests;

/// <summary>Deterministic: time only moves when the test advances the fake clock.</summary>
public class LeaseRenewerTests
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan Retry = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan Margin = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(250);
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static (FakeTimeProvider time, LeaseRenewer renewer, DateTime initialExpiry) Arrange()
    {
        var time = new FakeTimeProvider(T0);
        return (time, new LeaseRenewer(Lease, Interval, Retry, Margin, time), T0.UtcDateTime + Lease);
    }

    /// <summary>Advances the fake clock in small steps until the task completes.</summary>
    private static async Task<T> Pump<T>(FakeTimeProvider time, Task<T> task, int maxSteps = 400)
    {
        for (var i = 0; i < maxSteps && !task.IsCompleted; i++)
        {
            time.Advance(Step);
            await Task.Delay(1);
        }
        return await task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static async Task PumpFor(FakeTimeProvider time, TimeSpan duration)
    {
        for (var elapsed = TimeSpan.Zero; elapsed < duration; elapsed += Step) { time.Advance(Step); await Task.Delay(1); }
    }

    [Fact]
    public async Task RenewsEveryInterval_AndProposesAFullLeaseFromNow()
    {
        var (time, renewer, expiry) = Arrange();
        var proposals = new List<DateTime>();
        using var cts = new CancellationTokenSource();

        var run = renewer.RunAsync(expiry, (e, _) => { proposals.Add(e); return Task.FromResult(true); }, null, cts.Token);
        await PumpFor(time, TimeSpan.FromSeconds(10));
        cts.Cancel();

        Assert.Null(await Pump(time, run));
        Assert.True(proposals.Count >= 3, $"expected >= 3 renewals in 10s, got {proposals.Count}");
        Assert.All(proposals, p => Assert.True(p >= T0.UtcDateTime + Interval + Lease - TimeSpan.FromSeconds(0.5)));
    }

    [Fact]
    public async Task TransientFailures_AreRetriedWhileLeaseIsStillSafe_AndDoNotLoseOwnership()
    {
        var (time, renewer, expiry) = Arrange();
        var calls = 0;
        var errors = 0;
        using var cts = new CancellationTokenSource();

        // First tick (t=3s): two failures, then success. 3s + ~1s of retries is well before expiry(10s) - margin(2s).
        var run = renewer.RunAsync(expiry, (_, _) =>
        {
            calls++;
            if (calls <= 2) throw new InvalidOperationException("db blip");
            return Task.FromResult(true);
        }, _ => errors++, cts.Token);

        await PumpFor(time, TimeSpan.FromSeconds(5));
        Assert.False(run.IsCompleted, "a recovered transient failure must not stop the job");
        Assert.Equal(2, errors);
        Assert.True(calls >= 3);

        cts.Cancel();
        Assert.Null(await Pump(time, run));
    }

    [Fact]
    public async Task PersistentFailure_CancelsBeforeTheLeaseCanExpire()
    {
        var (time, renewer, expiry) = Arrange();
        var attempts = 0;

        var run = renewer.RunAsync(expiry, (_, _) => { attempts++; throw new TimeoutException("db down"); }, null, CancellationToken.None);
        var reason = await Pump(time, run);

        Assert.Equal(LeaseLossReason.RenewalFailed, reason);
        Assert.True(attempts > 1, "should have retried more than once before giving up");
        // The decision to stop the job is made no later than (expiry - margin) plus one clock step: strictly before expiry.
        var decidedAt = time.GetUtcNow().UtcDateTime;
        Assert.True(decidedAt <= expiry - Margin + Step, $"gave up at {decidedAt:O}, too close to expiry {expiry:O}");
        Assert.True(decidedAt < expiry);
    }

    [Fact]
    public async Task HungRenewalCall_IsAbandonedAtTheDeadline()
    {
        var (time, renewer, expiry) = Arrange();

        // A store call that never returns unless cancelled (e.g. database hang).
        var run = renewer.RunAsync(expiry, async (_, ct) => { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return true; }, null, CancellationToken.None);
        var reason = await Pump(time, run);

        Assert.Equal(LeaseLossReason.RenewalFailed, reason);
        Assert.True(time.GetUtcNow().UtcDateTime < expiry, "must stop the job while the persisted lease is still valid");
    }

    [Fact]
    public async Task OwnershipLost_IsReportedImmediatelyWithoutRetries()
    {
        var (time, renewer, expiry) = Arrange();
        var calls = 0;

        var run = renewer.RunAsync(expiry, (_, _) => { calls++; return Task.FromResult(false); }, null, CancellationToken.None);
        var reason = await Pump(time, run);

        Assert.Equal(LeaseLossReason.OwnershipLost, reason);
        Assert.Equal(1, calls);
        Assert.True(time.GetUtcNow() - T0 <= Interval + Step * 2);
    }

    [Fact]
    public async Task DeadlineMovesForwardOnlyAfterAConfirmedRenewal()
    {
        var (time, renewer, expiry) = Arrange();
        var calls = 0;
        DateTime confirmed = default;

        // First tick (t=3s) succeeds and confirms a lease until ~13s. Every later renewal fails.
        var run = renewer.RunAsync(expiry, (e, _) =>
        {
            if (++calls == 1) { confirmed = e; return Task.FromResult(true); }
            throw new InvalidOperationException("db down");
        }, null, CancellationToken.None);

        var reason = await Pump(time, run);
        Assert.Equal(LeaseLossReason.RenewalFailed, reason);
        var decidedAt = time.GetUtcNow().UtcDateTime;
        Assert.True(decidedAt > expiry - Margin, "should have benefited from the confirmed renewal");
        Assert.True(decidedAt <= confirmed - Margin + Step, $"gave up at {decidedAt:O} but confirmed lease only runs to {confirmed:O}");
    }

    [Fact]
    public async Task Cancellation_StopsCleanlyWithoutReportingLoss()
    {
        var (time, renewer, expiry) = Arrange();
        using var cts = new CancellationTokenSource();
        var run = renewer.RunAsync(expiry, (_, _) => Task.FromResult(true), null, cts.Token);

        await PumpFor(time, TimeSpan.FromSeconds(1));
        cts.Cancel();
        Assert.Null(await Pump(time, run));
    }
}
