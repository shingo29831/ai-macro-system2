namespace AI.MacroSystem.Infrastructure.Persistence.Repositories;

using AI.MacroSystem.Domain.Enums;
using AI.MacroSystem.Domain.Models;
using Microsoft.Data.Sqlite;

public interface IExecutionRepository
{
    Task CreateExecutionAsync(ExecutionRecord execution, SqliteTransaction? transaction = null, CancellationToken cancellationToken = default);
    Task<ExecutionRecord?> GetExecutionAsync(string executionId, SqliteTransaction? transaction = null, CancellationToken cancellationToken = default);

    Task UpdateExecutionStatusAndAppendEventAsync(
        string executionId,
        ExecutionStatus newStatus,
        DateTime? completedAtUtc,
        string? errorMessage,
        ExecutionEventRecord evt,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default);

    Task AddActionExecutionAsync(ActionExecutionRecord actionExecution, SqliteTransaction? transaction = null, CancellationToken cancellationToken = default);
    Task UpdateActionExecutionAsync(ActionExecutionRecord actionExecution, SqliteTransaction? transaction = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ActionExecutionRecord>> GetActionExecutionsAsync(string executionId, SqliteTransaction? transaction = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ExecutionEventRecord>> GetExecutionEventsAsync(string executionId, SqliteTransaction? transaction = null, CancellationToken cancellationToken = default);

    Task AddRecoveryRecordAsync(RecoveryRecord recovery, SqliteTransaction? transaction = null, CancellationToken cancellationToken = default);
    Task<RecoveryRecord?> GetRecoveryRecordAsync(string recoveryId, SqliteTransaction? transaction = null, CancellationToken cancellationToken = default);
}
