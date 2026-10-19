namespace AI.MacroSystem.Infrastructure.Persistence.Repositories;

using System.Globalization;
using AI.MacroSystem.Domain.Enums;
using AI.MacroSystem.Domain.Models;
using Microsoft.Data.Sqlite;

public sealed class WorkflowRepository : IWorkflowRepository
{
    private readonly ISqliteConnectionFactory _connectionFactory;

    public WorkflowRepository(ISqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task AddWorkflowAsync(WorkflowRecord workflow, SqliteTransaction? transaction = null, CancellationToken cancellationToken = default)
    {
        var conn = transaction?.Connection ?? await _connectionFactory.CreateOpenConnectionAsync(cancellationToken);
        await using var _ = transaction is null ? conn : null;

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = @"
            INSERT INTO Workflows (WorkflowId, Name, Description, CreatedAtUtc, UpdatedAtUtc)
            VALUES (@WorkflowId, @Name, @Description, @CreatedAtUtc, @UpdatedAtUtc);";
        cmd.Parameters.AddWithValue("@WorkflowId", workflow.WorkflowId);
        cmd.Parameters.AddWithValue("@Name", workflow.Name);
        cmd.Parameters.AddWithValue("@Description", workflow.Description);
        cmd.Parameters.AddWithValue("@CreatedAtUtc", workflow.CreatedAtUtc.ToString("o", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@UpdatedAtUtc", workflow.UpdatedAtUtc.ToString("o", CultureInfo.InvariantCulture));

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<WorkflowRecord?> GetWorkflowAsync(string workflowId, SqliteTransaction? transaction = null, CancellationToken cancellationToken = default)
    {
        var conn = transaction?.Connection ?? await _connectionFactory.CreateOpenConnectionAsync(cancellationToken);
        await using var _ = transaction is null ? conn : null;

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT WorkflowId, Name, Description, CreatedAtUtc, UpdatedAtUtc FROM Workflows WHERE WorkflowId = @WorkflowId;";
        cmd.Parameters.AddWithValue("@WorkflowId", workflowId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        return new WorkflowRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            DateTime.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            DateTime.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }

    public async Task<IReadOnlyList<WorkflowRecord>> GetAllWorkflowsAsync(SqliteTransaction? transaction = null, CancellationToken cancellationToken = default)
    {
        var conn = transaction?.Connection ?? await _connectionFactory.CreateOpenConnectionAsync(cancellationToken);
        await using var _ = transaction is null ? conn : null;

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT WorkflowId, Name, Description, CreatedAtUtc, UpdatedAtUtc FROM Workflows ORDER BY CreatedAtUtc DESC;";

        var list = new List<WorkflowRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new WorkflowRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                DateTime.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                DateTime.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
        }

        return list;
    }

    public async Task AddVersionAsync(WorkflowVersionRecord version, SqliteTransaction? transaction = null, CancellationToken cancellationToken = default)
    {
        var conn = transaction?.Connection ?? await _connectionFactory.CreateOpenConnectionAsync(cancellationToken);
        await using var _ = transaction is null ? conn : null;

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = @"
            INSERT INTO WorkflowVersions (WorkflowId, Version, WorkflowJson, RiskLevel, CreatedAtUtc, CreatedBy)
            VALUES (@WorkflowId, @Version, @WorkflowJson, @RiskLevel, @CreatedAtUtc, @CreatedBy);";
        cmd.Parameters.AddWithValue("@WorkflowId", version.WorkflowId);
        cmd.Parameters.AddWithValue("@Version", version.Version);
        cmd.Parameters.AddWithValue("@WorkflowJson", version.WorkflowJson);
        cmd.Parameters.AddWithValue("@RiskLevel", version.RiskLevel.ToString());
        cmd.Parameters.AddWithValue("@CreatedAtUtc", version.CreatedAtUtc.ToString("o", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@CreatedBy", version.CreatedBy);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<WorkflowVersionRecord?> GetVersionAsync(string workflowId, int version, SqliteTransaction? transaction = null, CancellationToken cancellationToken = default)
    {
        var conn = transaction?.Connection ?? await _connectionFactory.CreateOpenConnectionAsync(cancellationToken);
        await using var _ = transaction is null ? conn : null;

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = @"
            SELECT WorkflowId, Version, WorkflowJson, RiskLevel, CreatedAtUtc, CreatedBy
            FROM WorkflowVersions
            WHERE WorkflowId = @WorkflowId AND Version = @Version;";
        cmd.Parameters.AddWithValue("@WorkflowId", workflowId);
        cmd.Parameters.AddWithValue("@Version", version);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        return new WorkflowVersionRecord(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.GetString(2),
            Enum.Parse<RiskLevel>(reader.GetString(3)),
            DateTime.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            reader.GetString(5));
    }

    public async Task<IReadOnlyList<WorkflowVersionRecord>> GetVersionsAsync(string workflowId, SqliteTransaction? transaction = null, CancellationToken cancellationToken = default)
    {
        var conn = transaction?.Connection ?? await _connectionFactory.CreateOpenConnectionAsync(cancellationToken);
        await using var _ = transaction is null ? conn : null;

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = @"
            SELECT WorkflowId, Version, WorkflowJson, RiskLevel, CreatedAtUtc, CreatedBy
            FROM WorkflowVersions
            WHERE WorkflowId = @WorkflowId
            ORDER BY Version ASC;";
        cmd.Parameters.AddWithValue("@WorkflowId", workflowId);

        var list = new List<WorkflowVersionRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new WorkflowVersionRecord(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetString(2),
                Enum.Parse<RiskLevel>(reader.GetString(3)),
                DateTime.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.GetString(5)));
        }

        return list;
    }

    public async Task SaveDraftAsync(WorkflowDraftRecord draft, SqliteTransaction? transaction = null, CancellationToken cancellationToken = default)
    {
        var conn = transaction?.Connection ?? await _connectionFactory.CreateOpenConnectionAsync(cancellationToken);
        await using var _ = transaction is null ? conn : null;

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = @"
            INSERT INTO WorkflowDrafts (WorkflowId, DraftVersion, WorkflowJson, UpdatedAtUtc)
            VALUES (@WorkflowId, @DraftVersion, @WorkflowJson, @UpdatedAtUtc)
            ON CONFLICT (WorkflowId, DraftVersion)
            DO UPDATE SET WorkflowJson = excluded.WorkflowJson, UpdatedAtUtc = excluded.UpdatedAtUtc;";
        cmd.Parameters.AddWithValue("@WorkflowId", draft.WorkflowId);
        cmd.Parameters.AddWithValue("@DraftVersion", draft.DraftVersion);
        cmd.Parameters.AddWithValue("@WorkflowJson", draft.WorkflowJson);
        cmd.Parameters.AddWithValue("@UpdatedAtUtc", draft.UpdatedAtUtc.ToString("o", CultureInfo.InvariantCulture));

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<WorkflowDraftRecord?> GetDraftAsync(string workflowId, int draftVersion, SqliteTransaction? transaction = null, CancellationToken cancellationToken = default)
    {
        var conn = transaction?.Connection ?? await _connectionFactory.CreateOpenConnectionAsync(cancellationToken);
        await using var _ = transaction is null ? conn : null;

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT WorkflowId, DraftVersion, WorkflowJson, UpdatedAtUtc FROM WorkflowDrafts WHERE WorkflowId = @WorkflowId AND DraftVersion = @DraftVersion;";
        cmd.Parameters.AddWithValue("@WorkflowId", workflowId);
        cmd.Parameters.AddWithValue("@DraftVersion", draftVersion);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        return new WorkflowDraftRecord(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.GetString(2),
            DateTime.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }

    public async Task DeleteDraftAsync(string workflowId, int draftVersion, SqliteTransaction? transaction = null, CancellationToken cancellationToken = default)
    {
        var conn = transaction?.Connection ?? await _connectionFactory.CreateOpenConnectionAsync(cancellationToken);
        await using var _ = transaction is null ? conn : null;

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "DELETE FROM WorkflowDrafts WHERE WorkflowId = @WorkflowId AND DraftVersion = @DraftVersion;";
        cmd.Parameters.AddWithValue("@WorkflowId", workflowId);
        cmd.Parameters.AddWithValue("@DraftVersion", draftVersion);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AddApprovalAsync(ApprovalRecord approval, SqliteTransaction? transaction = null, CancellationToken cancellationToken = default)
    {
        var conn = transaction?.Connection ?? await _connectionFactory.CreateOpenConnectionAsync(cancellationToken);
        await using var _ = transaction is null ? conn : null;

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = @"
            INSERT INTO Approvals (ApprovalId, WorkflowId, Version, Status, ApprovedBy, ApprovedAtUtc, Notes, CreatedAtUtc)
            VALUES (@ApprovalId, @WorkflowId, @Version, @Status, @ApprovedBy, @ApprovedAtUtc, @Notes, @CreatedAtUtc);";
        cmd.Parameters.AddWithValue("@ApprovalId", approval.ApprovalId);
        cmd.Parameters.AddWithValue("@WorkflowId", approval.WorkflowId);
        cmd.Parameters.AddWithValue("@Version", approval.Version);
        cmd.Parameters.AddWithValue("@Status", approval.Status.ToString());
        cmd.Parameters.AddWithValue("@ApprovedBy", (object?)approval.ApprovedBy ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ApprovedAtUtc", approval.ApprovedAtUtc.HasValue ? approval.ApprovedAtUtc.Value.ToString("o", CultureInfo.InvariantCulture) : DBNull.Value);
        cmd.Parameters.AddWithValue("@Notes", (object?)approval.Notes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@CreatedAtUtc", approval.CreatedAtUtc.ToString("o", CultureInfo.InvariantCulture));

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ApprovalRecord?> GetApprovalAsync(string approvalId, SqliteTransaction? transaction = null, CancellationToken cancellationToken = default)
    {
        var conn = transaction?.Connection ?? await _connectionFactory.CreateOpenConnectionAsync(cancellationToken);
        await using var _ = transaction is null ? conn : null;

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT ApprovalId, WorkflowId, Version, Status, ApprovedBy, ApprovedAtUtc, Notes, CreatedAtUtc FROM Approvals WHERE ApprovalId = @ApprovalId;";
        cmd.Parameters.AddWithValue("@ApprovalId", approvalId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        return new ApprovalRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt32(2),
            Enum.Parse<ApprovalStatus>(reader.GetString(3)),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : DateTime.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            DateTime.Parse(reader.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }
}
