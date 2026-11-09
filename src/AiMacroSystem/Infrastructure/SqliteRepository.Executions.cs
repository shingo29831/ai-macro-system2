// Module: @role: ワークフロー実行状態・アクション実行状態・追記専用イベント履歴の同一トランザクション保証
using System;
using System.Collections.Generic;
using System.Text.Json;
using AiMacroSystem.Application;
using AiMacroSystem.Domain;
using Microsoft.Data.Sqlite;

namespace AiMacroSystem.Infrastructure;

public sealed partial class SqliteRepository
{
    public IReadOnlyList<ExecutionRecord> GetAllExecutions()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT ExecutionId, VersionId, PlanHash, Status, StartedAt, EndedAt, ApprovalId
            FROM Executions
            ORDER BY StartedAt DESC;
            """;
        using var reader = cmd.ExecuteReader();
        var list = new List<ExecutionRecord>();
        while (reader.Read())
        {
            list.Add(new ExecutionRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                Enum.Parse<ExecutionStatus>(reader.GetString(3)),
                DateTimeOffset.Parse(reader.GetString(4)),
                reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5)),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }
        return list;
    }

    // 状態変更と履歴追記は必ず同一トランザクションで実行する（仕様11.3 / 14.3 / 14.4）
    public void CreateExecutionAtomic(
        string executionId,
        string versionId,
        string planHash,
        ExecutionStatus initialStatus,
        string details,
        string? approvalId = null)
    {
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();
        var now = DateTimeOffset.UtcNow;

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO Executions (ExecutionId, VersionId, PlanHash, ApprovalId, Status, StartedAt, EndedAt)
                VALUES ($eid, $vid, $ph, $aid, $st, $sat, NULL);
                """;
            cmd.Parameters.AddWithValue("$eid", executionId);
            cmd.Parameters.AddWithValue("$vid", versionId);
            cmd.Parameters.AddWithValue("$ph", planHash);
            cmd.Parameters.AddWithValue("$aid", (object?)approvalId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$st", initialStatus.ToString());
            cmd.Parameters.AddWithValue("$sat", now.ToString("O"));
            cmd.ExecuteNonQuery();
        }

        InsertEventInternal(conn, tx, executionId, null, $"ExecutionCreated:{initialStatus}", details, now);
        tx.Commit();
    }

    public void UpdateExecutionStateAtomic(
        string executionId,
        ExecutionStatus newStatus,
        string eventType,
        string details,
        DateTimeOffset? endedAt = null)
    {
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();
        var now = DateTimeOffset.UtcNow;

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE Executions
                SET Status = $st, EndedAt = COALESCE($eat, EndedAt)
                WHERE ExecutionId = $eid;
                """;
            cmd.Parameters.AddWithValue("$st", newStatus.ToString());
            cmd.Parameters.AddWithValue("$eat", endedAt.HasValue ? endedAt.Value.ToString("O") : DBNull.Value);
            cmd.Parameters.AddWithValue("$eid", executionId);
            cmd.ExecuteNonQuery();
        }

        InsertEventInternal(conn, tx, executionId, null, eventType, details, now);
        tx.Commit();
    }

    public void CreateActionExecutionAtomic(
        string actionExecutionId,
        string executionId,
        string nodeId,
        ActionExecutionStatus initialStatus)
    {
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();
        var now = DateTimeOffset.UtcNow;

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO ActionExecutions (ActionExecutionId, ExecutionId, NodeId, Status, AttemptCount, LastFailureClassification, LastMessage)
                VALUES ($aeid, $eid, $nid, $st, 0, $fc, '');
                """;
            cmd.Parameters.AddWithValue("$aeid", actionExecutionId);
            cmd.Parameters.AddWithValue("$eid", executionId);
            cmd.Parameters.AddWithValue("$nid", nodeId);
            cmd.Parameters.AddWithValue("$st", initialStatus.ToString());
            cmd.Parameters.AddWithValue("$fc", FailureClassification.None.ToString());
            cmd.ExecuteNonQuery();
        }

        InsertEventInternal(conn, tx, executionId, actionExecutionId, $"ActionCreated:{initialStatus}", $"Node={nodeId}", now);
        tx.Commit();
    }

    public void UpdateActionStateAtomic(
        string executionId,
        string actionExecutionId,
        ActionExecutionStatus newStatus,
        int attemptCount,
        FailureClassification classification,
        string message,
        string eventType)
    {
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();
        var now = DateTimeOffset.UtcNow;
        string safeMessage = SensitiveDataProtector.SanitizeText(message);

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE ActionExecutions
                SET Status = $st, AttemptCount = $att, LastFailureClassification = $fc, LastMessage = $msg
                WHERE ActionExecutionId = $aeid;
                """;
            cmd.Parameters.AddWithValue("$st", newStatus.ToString());
            cmd.Parameters.AddWithValue("$att", attemptCount);
            cmd.Parameters.AddWithValue("$fc", classification.ToString());
            cmd.Parameters.AddWithValue("$msg", safeMessage);
            cmd.Parameters.AddWithValue("$aeid", actionExecutionId);
            cmd.ExecuteNonQuery();
        }

        InsertEventInternal(conn, tx, executionId, actionExecutionId, eventType, $"Status={newStatus};Attempt={attemptCount};Class={classification};Msg={safeMessage}", now);
        tx.Commit();
    }

    public void AppendExecutionEvent(string executionId, string? actionExecutionId, string eventType, string details)
    {
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();
        InsertEventInternal(conn, tx, executionId, actionExecutionId, eventType, details, DateTimeOffset.UtcNow);
        tx.Commit();
    }

    private static void InsertEventInternal(
        SqliteConnection conn,
        SqliteTransaction tx,
        string executionId,
        string? actionExecutionId,
        string eventType,
        string details,
        DateTimeOffset timestamp)
    {
        string safeDetails = SensitiveDataProtector.SanitizeText(details);
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO ExecutionEvents (EventId, ExecutionId, ActionExecutionId, EventType, Timestamp, DetailsJson)
            VALUES ($evid, $eid, $aeid, $et, $ts, $det);
            """;
        cmd.Parameters.AddWithValue("$evid", $"ev-{Guid.NewGuid():N}");
        cmd.Parameters.AddWithValue("$eid", executionId);
        cmd.Parameters.AddWithValue("$aeid", (object?)actionExecutionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$et", eventType);
        cmd.Parameters.AddWithValue("$ts", timestamp.ToString("O"));
        cmd.Parameters.AddWithValue("$det", JsonSerializer.Serialize(new { message = safeDetails }));
        cmd.ExecuteNonQuery();
    }

    public ExecutionRecord? GetExecutionById(string executionId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT ExecutionId, VersionId, PlanHash, Status, StartedAt, EndedAt, ApprovalId FROM Executions WHERE ExecutionId = $eid;";
        cmd.Parameters.AddWithValue("$eid", executionId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        return new ExecutionRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            Enum.Parse<ExecutionStatus>(reader.GetString(3)),
            DateTimeOffset.Parse(reader.GetString(4)),
            reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5)),
            reader.IsDBNull(6) ? null : reader.GetString(6));
    }

    public IReadOnlyList<ExecutionRecord> GetNonTerminalExecutions()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT ExecutionId, VersionId, PlanHash, Status, StartedAt, EndedAt, ApprovalId
            FROM Executions
            WHERE Status NOT IN ('Succeeded', 'Failed', 'Cancelled');
            """;
        using var reader = cmd.ExecuteReader();
        var list = new List<ExecutionRecord>();
        while (reader.Read())
        {
            list.Add(new ExecutionRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                Enum.Parse<ExecutionStatus>(reader.GetString(3)),
                DateTimeOffset.Parse(reader.GetString(4)),
                reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5)),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }
        return list;
    }

    public IReadOnlyList<ActionExecutionRecord> GetActionExecutions(string executionId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT ActionExecutionId, ExecutionId, NodeId, Status, AttemptCount, LastFailureClassification, LastMessage
            FROM ActionExecutions
            WHERE ExecutionId = $eid;
            """;
        cmd.Parameters.AddWithValue("$eid", executionId);
        using var reader = cmd.ExecuteReader();
        var list = new List<ActionExecutionRecord>();
        while (reader.Read())
        {
            list.Add(new ActionExecutionRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                Enum.Parse<ActionExecutionStatus>(reader.GetString(3)),
                reader.GetInt32(4),
                Enum.Parse<FailureClassification>(reader.GetString(5)),
                reader.GetString(6)));
        }
        return list;
    }

    public IReadOnlyList<ExecutionEventRecord> GetExecutionEvents(string executionId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT EventId, ExecutionId, ActionExecutionId, EventType, Timestamp, DetailsJson
            FROM ExecutionEvents
            WHERE ExecutionId = $eid
            ORDER BY Timestamp ASC;
            """;
        cmd.Parameters.AddWithValue("$eid", executionId);
        using var reader = cmd.ExecuteReader();
        var list = new List<ExecutionEventRecord>();
        while (reader.Read())
        {
            list.Add(new ExecutionEventRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4)),
                reader.GetString(5)));
        }
        return list;
    }
}
