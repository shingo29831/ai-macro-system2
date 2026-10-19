namespace AI.MacroSystem.Infrastructure.Persistence.Repositories;

using System.Globalization;
using AI.MacroSystem.Domain.Enums;
using AI.MacroSystem.Domain.Exceptions;
using AI.MacroSystem.Domain.Models;
using Microsoft.Data.Sqlite;

public sealed class ExecutionRepository : IExecutionRepository
{
    private readonly ISqliteConnectionFactory _connectionFactory;

    public ExecutionRepository(ISqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task CreateExecutionAsync(ExecutionRecord execution, SqliteTransaction? transaction = null, CancellationToken cancellationToken = default)
    {
        var conn = transaction?.Connection ?? await _connectionFactory.CreateOpenConnectionAsync(cancellationToken);
        await using var _ = transaction is null ? conn : null;

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = @"
            INSERT INTO Executions (ExecutionId, WorkflowId, Version, Status, TriggeredBy, StartedAtUtc, CompletedAtUtc, ErrorMessage, CreatedAtUtc)
            VALUES (@ExecutionId, @WorkflowId, @Version, @Status, @TriggeredBy, @StartedAtUtc, @CompletedAtUtc, @ErrorMessage, @CreatedAtUtc);";
        cmd.Parameters.AddWithValue("@ExecutionId", execution.ExecutionId);
        cmd.Parameters.AddWithValue("@WorkflowId", execution.WorkflowId);
        cmd.Parameters.AddWithValue("@Version", execution.Version);
        cmd.Parameters.AddWithValue("@Status", execution.Status.ToString());
        cmd.Parameters.AddWithValue("@TriggeredBy", execution.TriggeredBy);
        cmd.Parameters.AddWithValue("@StartedAtUtc", execution.StartedAtUtc.HasValue ? execution.StartedAtUtc.Value.ToString("o", CultureInfo.InvariantCulture) : DBNull.Value);
        cmd.Parameters.AddWithValue("@CompletedAtUtc", execution.CompletedAtUtc.HasValue ? execution.CompletedAtUtc.Value.ToString("o", CultureInfo.InvariantCulture) : DBNull.Value);
        cmd.Parameters.AddWithValue("@ErrorMessage", (object?)execution.ErrorMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@CreatedAtUtc", execution.CreatedAtUtc.ToString("o", CultureInfo.InvariantCulture));

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ExecutionRecord?> GetExecutionAsync(string executionId, SqliteTransaction? transaction = null, CancellationToken cancellationToken = default)
    {
        var conn = transaction?.Connection ?? await _connectionFactory.CreateOpenConnectionAsync(cancellationToken);
        await using var _ = transaction is null ? conn : null;

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = @"
            SELECT ExecutionId, WorkflowId, Version, Status, TriggeredBy, StartedAtUtc, CompletedAtUtc, ErrorMessage, CreatedAtUtc
            FROM Executions
            WHERE ExecutionId = @ExecutionId;";
        cmd.Parameters.AddWithValue("@ExecutionId", executionId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        return new ExecutionRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt32(2),
            Enum.Parse<ExecutionStatus>(reader.GetString(3)),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : DateTime.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            reader.IsDBNull(6) ? null : DateTime.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            DateTime.Parse(reader.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }

    public async Task TransitionExecutionStatusAsync(
        string executionId,
        ExecutionStatus expectedCurrentStatus,
        ExecutionStatus newStatus,
        DateTime? completedAtUtc,
        string? errorMessage,
        ExecutionEventRecord evt,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        var conn = transaction?.Connection ?? await _connectionFactory.CreateOpenConnectionAsync(cancellationToken);
        var ownsConnection = transaction is null;
        var localTransaction = ownsConnection ? (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken) : transaction;

        try
        {
            int rowsUpdated;
            await using (var updateCmd = conn.CreateCommand())
            {
                updateCmd.Transaction = localTransaction;
                updateCmd.CommandText = @"
                    UPDATE Executions
                    SET Status = @Status,
                        CompletedAtUtc = @CompletedAtUtc,
                        ErrorMessage = @ErrorMessage
                    WHERE ExecutionId = @ExecutionId AND Status = @ExpectedStatus;";
                updateCmd.Parameters.AddWithValue("@Status", newStatus.ToString());
                updateCmd.Parameters.AddWithValue("@CompletedAtUtc", completedAtUtc.HasValue ? completedAtUtc.Value.ToString("o", CultureInfo.InvariantCulture) : DBNull.Value);
                updateCmd.Parameters.AddWithValue("@ErrorMessage", (object?)errorMessage ?? DBNull.Value);
                updateCmd.Parameters.AddWithValue("@ExecutionId", executionId);
                updateCmd.Parameters.AddWithValue("@ExpectedStatus", expectedCurrentStatus.ToString());
                rowsUpdated = await updateCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            if (rowsUpdated == 0)
            {
                throw new ConcurrencyConflictException("Execution", executionId, expectedCurrentStatus.ToString());
            }

            await using (var eventCmd = conn.CreateCommand())
            {
                eventCmd.Transaction = localTransaction;
                eventCmd.CommandText = @"
                    INSERT INTO ExecutionEvents (EventId, ExecutionId, SequenceNumber, EventType, PayloadJson, TimestampUtc)
                    VALUES (@EventId, @ExecutionId, @SequenceNumber, @EventType, @PayloadJson, @TimestampUtc);";
                eventCmd.Parameters.AddWithValue("@EventId", evt.EventId);
                eventCmd.Parameters.AddWithValue("@ExecutionId", evt.ExecutionId);
                eventCmd.Parameters.AddWithValue("@SequenceNumber", evt.SequenceNumber);
                eventCmd.Parameters.AddWithValue("@EventType", evt.EventType);
                eventCmd.Parameters.AddWithValue("@PayloadJson", evt.PayloadJson);
                eventCmd.Parameters.AddWithValue("@TimestampUtc", evt.TimestampUtc.ToString("o", CultureInfo.InvariantCulture));
                await eventCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            if (ownsConnection && localTransaction != null)
            {
                await localTransaction.CommitAsync(cancellationToken);
            }
        }
        catch
        {
            if (ownsConnection && localTransaction != null)
            {
                await localTransaction.RollbackAsync(cancellationToken);
            }
            throw;
        }
        finally
        {
            if (ownsConnection)
            {
                if (localTransaction != null) await localTransaction.DisposeAsync();
                await conn.DisposeAsync();
            }
        }
    }

    public async Task AddActionExecutionAsync(ActionExecutionRecord actionExecution, SqliteTransaction? transaction = null, CancellationToken cancellationToken = default)
    {
        var conn = transaction?.Connection ?? await _connectionFactory.CreateOpenConnectionAsync(cancellationToken);
        await using var _ = transaction is null ? conn : null;

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = @"
            INSERT INTO ActionExecutions (ActionExecutionId, ExecutionId, NodeId, ExecutionOrder, IterationIndex, ActionType, Status, ParametersJson, ResultJson, ErrorMessage, ErrorCategory, StartedAtUtc, CompletedAtUtc)
            VALUES (@ActionExecutionId, @ExecutionId, @NodeId, @ExecutionOrder, @IterationIndex, @ActionType, @Status, @ParametersJson, @ResultJson, @ErrorMessage, @ErrorCategory, @StartedAtUtc, @CompletedAtUtc);";
        cmd.Parameters.AddWithValue("@ActionExecutionId", actionExecution.ActionExecutionId);
        cmd.Parameters.AddWithValue("@ExecutionId", actionExecution.ExecutionId);
        cmd.Parameters.AddWithValue("@NodeId", actionExecution.NodeId);
        cmd.Parameters.AddWithValue("@ExecutionOrder", actionExecution.ExecutionOrder);
        cmd.Parameters.AddWithValue("@IterationIndex", actionExecution.IterationIndex);
        cmd.Parameters.AddWithValue("@ActionType", actionExecution.ActionType);
        cmd.Parameters.AddWithValue("@Status", actionExecution.Status.ToString());
        cmd.Parameters.AddWithValue("@ParametersJson", actionExecution.ParametersJson);
        cmd.Parameters.AddWithValue("@ResultJson", (object?)actionExecution.ResultJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ErrorMessage", (object?)actionExecution.ErrorMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ErrorCategory", actionExecution.ErrorCategory.ToString());
        cmd.Parameters.AddWithValue("@StartedAtUtc", actionExecution.StartedAtUtc.ToString("o", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@CompletedAtUtc", actionExecution.CompletedAtUtc.HasValue ? actionExecution.CompletedAtUtc.Value.ToString("o", CultureInfo.InvariantCulture) : DBNull.Value);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateActionExecutionAsync(ActionExecutionRecord actionExecution, SqliteTransaction? transaction = null, CancellationToken cancellationToken = default)
    {
        var conn = transaction?.Connection ?? await _connectionFactory.CreateOpenConnectionAsync(cancellationToken);
        await using var _ = transaction is null ? conn : null;

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = @"
            UPDATE ActionExecutions
            SET Status = @Status,
                ResultJson = @ResultJson,
                ErrorMessage = @ErrorMessage,
                ErrorCategory = @ErrorCategory,
                CompletedAtUtc = @CompletedAtUtc
            WHERE ActionExecutionId = @ActionExecutionId;";
        cmd.Parameters.AddWithValue("@Status", actionExecution.Status.ToString());
        cmd.Parameters.AddWithValue("@ResultJson", (object?)actionExecution.ResultJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ErrorMessage", (object?)actionExecution.ErrorMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ErrorCategory", actionExecution.ErrorCategory.ToString());
        cmd.Parameters.AddWithValue("@CompletedAtUtc", actionExecution.CompletedAtUtc.HasValue ? actionExecution.CompletedAtUtc.Value.ToString("o", CultureInfo.InvariantCulture) : DBNull.Value);
        cmd.Parameters.AddWithValue("@ActionExecutionId", actionExecution.ActionExecutionId);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task TransitionActionExecutionStatusAsync(
        string actionExecutionId,
        ActionStatus expectedCurrentStatus,
        ActionStatus newStatus,
        string? resultJson,
        string? errorMessage,
        ActionErrorCategory errorCategory,
        DateTime? completedAtUtc,
        ExecutionEventRecord evt,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        var conn = transaction?.Connection ?? await _connectionFactory.CreateOpenConnectionAsync(cancellationToken);
        var ownsConnection = transaction is null;
        var localTransaction = ownsConnection ? (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken) : transaction;

        try
        {
            int rowsUpdated;
            await using (var updateCmd = conn.CreateCommand())
            {
                updateCmd.Transaction = localTransaction;
                updateCmd.CommandText = @"
                    UPDATE ActionExecutions
                    SET Status = @Status,
                        ResultJson = @ResultJson,
                        ErrorMessage = @ErrorMessage,
                        ErrorCategory = @ErrorCategory,
                        CompletedAtUtc = @CompletedAtUtc
                    WHERE ActionExecutionId = @ActionExecutionId AND Status = @ExpectedStatus;";
                updateCmd.Parameters.AddWithValue("@Status", newStatus.ToString());
                updateCmd.Parameters.AddWithValue("@ResultJson", (object?)resultJson ?? DBNull.Value);
                updateCmd.Parameters.AddWithValue("@ErrorMessage", (object?)errorMessage ?? DBNull.Value);
                updateCmd.Parameters.AddWithValue("@ErrorCategory", errorCategory.ToString());
                updateCmd.Parameters.AddWithValue("@CompletedAtUtc", completedAtUtc.HasValue ? completedAtUtc.Value.ToString("o", CultureInfo.InvariantCulture) : DBNull.Value);
                updateCmd.Parameters.AddWithValue("@ActionExecutionId", actionExecutionId);
                updateCmd.Parameters.AddWithValue("@ExpectedStatus", expectedCurrentStatus.ToString());
                rowsUpdated = await updateCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            if (rowsUpdated == 0)
            {
                throw new ConcurrencyConflictException("ActionExecution", actionExecutionId, expectedCurrentStatus.ToString());
            }

            await using (var eventCmd = conn.CreateCommand())
            {
                eventCmd.Transaction = localTransaction;
                eventCmd.CommandText = @"
                    INSERT INTO ExecutionEvents (EventId, ExecutionId, SequenceNumber, EventType, PayloadJson, TimestampUtc)
                    VALUES (@EventId, @ExecutionId, @SequenceNumber, @EventType, @PayloadJson, @TimestampUtc);";
                eventCmd.Parameters.AddWithValue("@EventId", evt.EventId);
                eventCmd.Parameters.AddWithValue("@ExecutionId", evt.ExecutionId);
                eventCmd.Parameters.AddWithValue("@SequenceNumber", evt.SequenceNumber);
                eventCmd.Parameters.AddWithValue("@EventType", evt.EventType);
                eventCmd.Parameters.AddWithValue("@PayloadJson", evt.PayloadJson);
                eventCmd.Parameters.AddWithValue("@TimestampUtc", evt.TimestampUtc.ToString("o", CultureInfo.InvariantCulture));
                await eventCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            if (ownsConnection && localTransaction != null)
            {
                await localTransaction.CommitAsync(cancellationToken);
            }
        }
        catch
        {
            if (ownsConnection && localTransaction != null)
            {
                await localTransaction.RollbackAsync(cancellationToken);
            }
            throw;
        }
        finally
        {
            if (ownsConnection)
            {
                if (localTransaction != null) await localTransaction.DisposeAsync();
                await conn.DisposeAsync();
            }
        }
    }

    public async Task<ActionExecutionRecord?> GetActionExecutionAsync(string actionExecutionId, SqliteTransaction? transaction = null, CancellationToken cancellationToken = default)
    {
        var conn = transaction?.Connection ?? await _connectionFactory.CreateOpenConnectionAsync(cancellationToken);
        await using var _ = transaction is null ? conn : null;

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = @"
            SELECT ActionExecutionId, ExecutionId, NodeId, ExecutionOrder, IterationIndex, ActionType, Status, ParametersJson, ResultJson, ErrorMessage, ErrorCategory, StartedAtUtc, CompletedAtUtc
            FROM ActionExecutions
            WHERE ActionExecutionId = @ActionExecutionId;";
        cmd.Parameters.AddWithValue("@ActionExecutionId", actionExecutionId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        return new ActionExecutionRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt32(3),
            reader.GetInt32(4),
            reader.GetString(5),
            Enum.Parse<ActionStatus>(reader.GetString(6)),
            reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            Enum.Parse<ActionErrorCategory>(reader.GetString(10)),
            DateTime.Parse(reader.GetString(11), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            reader.IsDBNull(12) ? null : DateTime.Parse(reader.GetString(12), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }

    public async Task<IReadOnlyList<ActionExecutionRecord>> GetActionExecutionsAsync(string executionId, SqliteTransaction? transaction = null, CancellationToken cancellationToken = default)
    {
        var conn = transaction?.Connection ?? await _connectionFactory.CreateOpenConnectionAsync(cancellationToken);
        await using var _ = transaction is null ? conn : null;

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = @"
            SELECT ActionExecutionId, ExecutionId, NodeId, ExecutionOrder, IterationIndex, ActionType, Status, ParametersJson, ResultJson, ErrorMessage, ErrorCategory, StartedAtUtc, CompletedAtUtc
            FROM ActionExecutions
            WHERE ExecutionId = @ExecutionId
            ORDER BY ExecutionOrder ASC;";
        cmd.Parameters.AddWithValue("@ExecutionId", executionId);

        var list = new List<ActionExecutionRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new ActionExecutionRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetString(5),
                Enum.Parse<ActionStatus>(reader.GetString(6)),
                reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                Enum.Parse<ActionErrorCategory>(reader.GetString(10)),
                DateTime.Parse(reader.GetString(11), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.IsDBNull(12) ? null : DateTime.Parse(reader.GetString(12), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
        }

        return list;
    }

    public async Task<IReadOnlyList<ExecutionEventRecord>> GetExecutionEventsAsync(string executionId, SqliteTransaction? transaction = null, CancellationToken cancellationToken = default)
    {
        var conn = transaction?.Connection ?? await _connectionFactory.CreateOpenConnectionAsync(cancellationToken);
        await using var _ = transaction is null ? conn : null;

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = @"
            SELECT EventId, ExecutionId, SequenceNumber, EventType, PayloadJson, TimestampUtc
            FROM ExecutionEvents
            WHERE ExecutionId = @ExecutionId
            ORDER BY SequenceNumber ASC;";
        cmd.Parameters.AddWithValue("@ExecutionId", executionId);

        var list = new List<ExecutionEventRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new ExecutionEventRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetString(3),
                reader.GetString(4),
                DateTime.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
        }

        return list;
    }

    public async Task AddRecoveryRecordAsync(RecoveryRecord recovery, SqliteTransaction? transaction = null, CancellationToken cancellationToken = default)
    {
        var conn = transaction?.Connection ?? await _connectionFactory.CreateOpenConnectionAsync(cancellationToken);
        await using var _ = transaction is null ? conn : null;

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = @"
            INSERT INTO RecoveryRecords (RecoveryId, ExecutionId, Reason, RecoveryStatus, ConfirmedBy, ConfirmedAtUtc, DetailsJson, CreatedAtUtc)
            VALUES (@RecoveryId, @ExecutionId, @Reason, @RecoveryStatus, @ConfirmedBy, @ConfirmedAtUtc, @DetailsJson, @CreatedAtUtc);";
        cmd.Parameters.AddWithValue("@RecoveryId", recovery.RecoveryId);
        cmd.Parameters.AddWithValue("@ExecutionId", recovery.ExecutionId);
        cmd.Parameters.AddWithValue("@Reason", recovery.Reason);
        cmd.Parameters.AddWithValue("@RecoveryStatus", recovery.RecoveryStatus.ToString());
        cmd.Parameters.AddWithValue("@ConfirmedBy", (object?)recovery.ConfirmedBy ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ConfirmedAtUtc", recovery.ConfirmedAtUtc.HasValue ? recovery.ConfirmedAtUtc.Value.ToString("o", CultureInfo.InvariantCulture) : DBNull.Value);
        cmd.Parameters.AddWithValue("@DetailsJson", (object?)recovery.DetailsJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@CreatedAtUtc", recovery.CreatedAtUtc.ToString("o", CultureInfo.InvariantCulture));

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<RecoveryRecord?> GetRecoveryRecordAsync(string recoveryId, SqliteTransaction? transaction = null, CancellationToken cancellationToken = default)
    {
        var conn = transaction?.Connection ?? await _connectionFactory.CreateOpenConnectionAsync(cancellationToken);
        await using var _ = transaction is null ? conn : null;

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = @"
            SELECT RecoveryId, ExecutionId, Reason, RecoveryStatus, ConfirmedBy, ConfirmedAtUtc, DetailsJson, CreatedAtUtc
            FROM RecoveryRecords
            WHERE RecoveryId = @RecoveryId;";
        cmd.Parameters.AddWithValue("@RecoveryId", recoveryId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        return new RecoveryRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            Enum.Parse<RecoveryStatus>(reader.GetString(3)),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : DateTime.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            DateTime.Parse(reader.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }
}
