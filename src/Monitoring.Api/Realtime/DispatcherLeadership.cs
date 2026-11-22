using System.Data;
using Microsoft.Data.SqlClient;

namespace Monitoring.Api.Realtime;

/// <summary>
/// SignalR connections live in one process, and this release has no backplane, so exactly one API instance may
/// dispatch change events. The instance that holds this SQL application lock is the leader; the lock is tied to a
/// dedicated session and disappears when that process (or its connection) dies, letting a standby instance take over.
/// </summary>
public sealed class DispatcherLeadership : IAsyncDisposable
{
    public const string LockResource = "monitoring-api-change-dispatcher";
    private SqlConnection? _connection;
    private readonly SemaphoreSlim _gate = new(1, 1); // the lock session is one connection: never two commands at once

    public bool IsLeader { get; private set; }

    /// <summary>Acquires the lock if free, or verifies the held one. Never blocks waiting for another instance.</summary>
    public async Task<bool> EnsureAsync(string connectionString, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return await EnsureCoreAsync(connectionString, ct); }
        finally { _gate.Release(); }
    }

    private async Task<bool> EnsureCoreAsync(string connectionString, CancellationToken ct)
    {
        if (IsLeader && _connection is { State: ConnectionState.Open })
        {
            try
            {
                await using var check = _connection.CreateCommand();
                check.CommandText = "SELECT APPLOCK_MODE('public', @r, 'Session')";
                check.Parameters.AddWithValue("@r", LockResource);
                if ((string?)await check.ExecuteScalarAsync(ct) == "Exclusive") return true;
            }
            catch (SqlException) { /* connection lost: fall through and try to re-acquire */ }
            await ReleaseCoreAsync();
        }
        IsLeader = false;

        try
        {
            // Pooling off: a pooled session would keep the session-scoped lock alive after we "close" it.
            _connection = new SqlConnection(new SqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
            await _connection.OpenAsync(ct);
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DECLARE @r int; EXEC @r = sp_getapplock @Resource = @res, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0; SELECT @r;";
            cmd.Parameters.AddWithValue("@res", LockResource);
            var rc = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
            IsLeader = rc >= 0; // 0 = granted, 1 = granted after waiting; negative = held by someone else / error
            if (!IsLeader) await ReleaseCoreAsync();
        }
        catch (SqlException) { await ReleaseCoreAsync(); }
        return IsLeader;
    }

    public async Task ReleaseAsync()
    {
        await _gate.WaitAsync();
        try { await ReleaseCoreAsync(); }
        finally { _gate.Release(); }
    }

    private async Task ReleaseCoreAsync()
    {
        IsLeader = false;
        if (_connection is null) return;
        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = "EXEC sp_releaseapplock @Resource = @res, @LockOwner = 'Session'";
            cmd.Parameters.AddWithValue("@res", LockResource);
            await cmd.ExecuteNonQueryAsync();
        }
        catch { /* connection already gone: the lock went with it */ }
        try { await _connection.DisposeAsync(); } catch { /* already gone */ }
        _connection = null;
    }

    public ValueTask DisposeAsync() => new(ReleaseAsync());
}
