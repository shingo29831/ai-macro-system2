namespace AI.MacroSystem.Infrastructure.Persistence.Repositories;

using System.Globalization;
using AI.MacroSystem.Domain.Models;
using Microsoft.Data.Sqlite;

public sealed class SystemLockRepository : ISystemLockRepository
{
    private const string LockKey = "GlobalExecutionLock";
    private readonly ISqliteConnectionFactory _connectionFactory;

    public SystemLockRepository(ISqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<bool> TryAcquireExecutionLockAsync(string executionId, SqliteTransaction? transaction = null, CancellationToken cancellationToken = default)
    {
        var conn = transaction?.Connection ?? await _connectionFactory.CreateOpenConnectionAsync(cancellationToken);
        await using var _ = transaction is null ? conn : null;

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        // 未実行中かつ復旧待ちフラグが立っていない場合のみアトミックにロックを取得
        cmd.CommandText = @"
            UPDATE SystemLocks
            SET CurrentExecutionId = @ExecutionId,
                AcquiredAtUtc = @NowUtc
            WHERE LockKey = @LockKey
              AND CurrentExecutionId IS NULL
              AND IsRecoveryPending = 0;";
        cmd.Parameters.AddWithValue("@ExecutionId", executionId);
        cmd.Parameters.AddWithValue("@NowUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@LockKey", LockKey);

        var rows = await cmd.ExecuteNonQueryAsync(cancellationToken);
        return rows > 0;
    }

    public async Task ReleaseExecutionLockAsync(string executionId, bool isRecoveryPending, SqliteTransaction? transaction = null, CancellationToken cancellationToken = default)
    {
        var conn = transaction?.Connection ?? await _connectionFactory.CreateOpenConnectionAsync(cancellationToken);
        await using var _ = transaction is null ? conn : null;

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = @"
            UPDATE SystemLocks
            SET CurrentExecutionId = NULL,
                AcquiredAtUtc = NULL,
                IsRecoveryPending = @IsRecoveryPending
            WHERE LockKey = @LockKey AND CurrentExecutionId = @ExecutionId;";
        cmd.Parameters.AddWithValue("@IsRecoveryPending", isRecoveryPending ? 1 : 0);
        cmd.Parameters.AddWithValue("@LockKey", LockKey);
        cmd.Parameters.AddWithValue("@ExecutionId", executionId);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ClearRecoveryPendingAsync(SqliteTransaction? transaction = null, CancellationToken cancellationToken = default)
    {
        var conn = transaction?.Connection ?? await _connectionFactory.CreateOpenConnectionAsync(cancellationToken);
        await using var _ = transaction is null ? conn : null;

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "UPDATE SystemLocks SET IsRecoveryPending = 0 WHERE LockKey = @LockKey;";
        cmd.Parameters.AddWithValue("@LockKey", LockKey);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<SystemLockStatus> GetSystemLockStatusAsync(SqliteTransaction? transaction = null, CancellationToken cancellationToken = default)
    {
        var conn = transaction?.Connection ?? await _connectionFactory.CreateOpenConnectionAsync(cancellationToken);
        await using var _ = transaction is null ? conn : null;

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT LockKey, CurrentExecutionId, IsRecoveryPending, AcquiredAtUtc FROM SystemLocks WHERE LockKey = @LockKey;";
        cmd.Parameters.AddWithValue("@LockKey", LockKey);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("System lock row not initialized.");
        }

        return new SystemLockStatus(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetInt32(2) == 1,
            reader.IsDBNull(3) ? null : DateTime.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }
}
