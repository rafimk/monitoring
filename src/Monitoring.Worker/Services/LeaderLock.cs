using System.Data;
using Microsoft.Data.SqlClient;

namespace Monitoring.Worker.Services;

/// <summary>
/// The cluster-wide "active worker" lock: one exclusive SQL Server application lock with @LockOwner = 'Session',
/// held on its own physical connection for the whole active tenure.
///
/// Why this shape:
///  * Session-owned (not Transaction-owned): the lock must outlive any transaction.
///  * A dedicated, non-pooled connection: a pooled connection would keep the session (and the lock) alive after we
///    "close" it, and EF's pooled connections are shared with other work. Connection resiliency (ConnectRetryCount) is
///    disabled so a broken session can never be silently replaced by a new one that has no lock.
///  * Database-scoped: the lock lives in the Monitoring database the connection string points to.
///  * When the process or the session dies, SQL Server releases the lock by itself: the standby takes over.
/// </summary>
public sealed class LeaderLock : IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly string _resource;
    private readonly SemaphoreSlim _gate = new(1, 1); // one connection: never two commands at once
    private SqlConnection? _connection;

    public LeaderLock(string connectionString, string resource, string applicationName = "Monitoring.Worker.Leader")
    {
        _resource = resource;
        var b = new SqlConnectionStringBuilder(connectionString)
        {
            Pooling = false,        // never return a connection that holds the lock to a pool
            ConnectRetryCount = 0,  // never silently reconnect to a new session
            ApplicationName = applicationName,
        };
        _connectionString = b.ConnectionString;
    }

    /// <summary>SQL Server session id (@@SPID) holding the lock; null while not held. Useful in logs and diagnostics.</summary>
    public int? SessionId { get; private set; }
    public bool IsHeld => SessionId is not null;

    /// <summary>
    /// Tries to take the lock without waiting. Returns false on ordinary contention (another node is active);
    /// throws on connection or SQL errors so the caller can back off.
    /// </summary>
    public async Task<bool> TryAcquireAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await CloseAsync();
            var connection = new SqlConnection(_connectionString);
            try
            {
                await connection.OpenAsync(ct);
                await using var cmd = connection.CreateCommand();
                cmd.CommandText =
                    "DECLARE @r int; EXEC @r = sp_getapplock @Resource = @res, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0; " +
                    "SELECT @r, @@SPID;";
                cmd.Parameters.AddWithValue("@res", _resource);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                await reader.ReadAsync(ct);
                var rc = reader.GetInt32(0);
                var spid = Convert.ToInt32(reader.GetValue(1));
                await reader.CloseAsync();

                if (rc >= 0) // 0 = granted, 1 = granted after waiting
                {
                    _connection = connection;
                    SessionId = spid;
                    return true;
                }
                if (rc == -999) throw new InvalidOperationException("sp_getapplock rejected the request (parameter validation error)");
                await connection.DisposeAsync(); // -1 timeout (held elsewhere), -2 cancelled, -3 deadlock victim
                return false;
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Proves the lock is still held by the same SQL session. Any failure (broken connection, timeout, a different
    /// session id, the lock not being ours) returns false: ownership that cannot be proven is treated as lost.
    /// </summary>
    public async Task<(bool Held, string? Reason)> VerifyAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (_connection is null || SessionId is null) return (false, "lock is not held");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await _gate.WaitAsync(cts.Token);
            try
            {
                if (_connection.State != ConnectionState.Open) return (false, $"lock connection is {_connection.State}");
                await using var cmd = _connection.CreateCommand();
                cmd.CommandText = "SELECT @@SPID, APPLOCK_MODE('public', @res, 'Session')";
                cmd.Parameters.AddWithValue("@res", _resource);
                cmd.CommandTimeout = Math.Max(1, (int)Math.Ceiling(timeout.TotalSeconds));
                await using var reader = await cmd.ExecuteReaderAsync(cts.Token);
                await reader.ReadAsync(cts.Token);
                var spid = Convert.ToInt32(reader.GetValue(0));
                var mode = reader.GetString(1);
                if (spid != SessionId) return (false, $"connection is now session {spid}, expected {SessionId}");
                return mode == "Exclusive" ? (true, null) : (false, $"application lock mode is {mode}");
            }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return (false, ex.GetType().Name + ": " + ex.Message); }
    }

    /// <summary>Explicitly releases the lock (so a standby can take over at once) and closes the connection.</summary>
    public async Task ReleaseAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_connection is { State: ConnectionState.Open } c && SessionId is not null)
            {
                try
                {
                    await using var cmd = c.CreateCommand();
                    cmd.CommandText = "EXEC sp_releaseapplock @Resource = @res, @LockOwner = 'Session'";
                    cmd.Parameters.AddWithValue("@res", _resource);
                    cmd.CommandTimeout = 5;
                    await cmd.ExecuteNonQueryAsync();
                }
                catch { /* connection already gone: the lock went with the session */ }
            }
            await CloseAsync();
        }
        finally { _gate.Release(); }
    }

    private async Task CloseAsync()
    {
        SessionId = null;
        var c = _connection;
        _connection = null;
        if (c is null) return;
        try { await c.DisposeAsync(); } catch { /* already gone */ }
    }

    public async ValueTask DisposeAsync() => await ReleaseAsync();
}
