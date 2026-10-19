namespace AI.MacroSystem.Infrastructure.Persistence.Repositories;

using AI.MacroSystem.Domain.Models;
using Microsoft.Data.Sqlite;

public interface ISystemLockRepository
{
    Task<bool> TryAcquireExecutionLockAsync(string executionId, SqliteTransaction? transaction = null, CancellationToken cancellationToken = default);
    Task ReleaseExecutionLockAsync(string executionId, bool isRecoveryPending, SqliteTransaction? transaction = null, CancellationToken cancellationToken = default);
    Task ClearRecoveryPendingAsync(SqliteTransaction? transaction = null, CancellationToken cancellationToken = default);
    Task<SystemLockStatus> GetSystemLockStatusAsync(SqliteTransaction? transaction = null, CancellationToken cancellationToken = default);
}
