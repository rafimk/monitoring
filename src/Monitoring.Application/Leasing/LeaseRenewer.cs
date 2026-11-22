namespace Monitoring.Application.Leasing;

public enum LeaseLossReason
{
    /// <summary>The store reported that this worker/attempt no longer owns the run (it was recovered or re-claimed).</summary>
    OwnershipLost,
    /// <summary>Renewal kept failing until the last moment at which the job could still be stopped before its lease expires.</summary>
    RenewalFailed,
}

/// <summary>
/// Keeps a run's lease alive while a job executes. On a transient failure it retries quickly, but only while the
/// currently persisted lease is still valid <i>with a safety margin</i>: once <c>expiry - margin</c> passes it gives up so
/// the caller can cancel the job <b>before</b> another worker is allowed to recover the run. It never extends optimistically:
/// the local expiry only moves forward after the store confirmed a renewal.
/// </summary>
public sealed class LeaseRenewer(
    TimeSpan leaseDuration, TimeSpan interval, TimeSpan retryDelay, TimeSpan safetyMargin, TimeProvider time)
{
    /// <summary>
    /// Runs until cancelled (returns <c>null</c>) or until the lease is lost (returns the reason).
    /// </summary>
    /// <param name="initialExpiryUtc">Lease expiry that was persisted when the run was claimed.</param>
    /// <param name="renew">Persists a new expiry. Returns false when ownership is gone; throws on transient errors.</param>
    /// <param name="onTransientError">Called for each failed renewal attempt (for logging).</param>
    public async Task<LeaseLossReason?> RunAsync(
        DateTime initialExpiryUtc,
        Func<DateTime, CancellationToken, Task<bool>> renew,
        Action<Exception>? onTransientError,
        CancellationToken ct)
    {
        var expiry = initialExpiryUtc;
        try
        {
            while (true)
            {
                await Task.Delay(interval, time, ct);

                while (true)
                {
                    var now = time.GetUtcNow().UtcDateTime;
                    var remaining = (expiry - safetyMargin) - now;
                    if (remaining <= TimeSpan.Zero) return LeaseLossReason.RenewalFailed;

                    var proposed = now + leaseDuration;
                    try
                    {
                        // A hung store call must not outlive the point where the job still can be stopped safely.
                        using var deadlineCts = new CancellationTokenSource(remaining, time);
                        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct, deadlineCts.Token);
                        if (!await renew(proposed, attemptCts.Token)) return LeaseLossReason.OwnershipLost;
                        expiry = proposed;
                        break;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
                    catch (Exception ex)
                    {
                        onTransientError?.Invoke(ex);
                        var left = (expiry - safetyMargin) - time.GetUtcNow().UtcDateTime;
                        if (left <= TimeSpan.Zero) return LeaseLossReason.RenewalFailed;
                        await Task.Delay(retryDelay < left ? retryDelay : left, time, ct);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
    }
}
