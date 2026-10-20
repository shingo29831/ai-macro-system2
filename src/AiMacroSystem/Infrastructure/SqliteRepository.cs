// Module: @role: SQLiteによるハイブリッド永続化・状態更新と履歴追記の同一トランザクション保証
using System;
using System.Collections.Generic;
using System.Text.Json;
using AiMacroSystem.Application;
using AiMacroSystem.Domain;
using Microsoft.Data.Sqlite;

namespace AiMacroSystem.Infrastructure;

public sealed class SqliteRepository
{
    private readonly string _connectionString;

    public SqliteRepository(string databasePath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            ForeignKeys = true,
            Mode = SqliteOpenMode.ReadWriteCreate
        };
        _connectionString = builder.ToString();
        InitializeSchema();
    }

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    public void InitializeSchema()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA foreign_keys = ON;

            CREATE TABLE IF NOT EXISTS Workflows (
                WorkflowId TEXT PRIMARY KEY,
                Name TEXT NOT NULL,
                CreatedAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS WorkflowVersions (
                VersionId TEXT PRIMARY KEY,
                WorkflowId TEXT NOT NULL,
                VersionNumber INTEGER NOT NULL,
                IrJson TEXT NOT NULL,
                SchemaVersion TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                FOREIGN KEY (WorkflowId) REFERENCES Workflows(WorkflowId),
                UNIQUE (WorkflowId, VersionNumber)
            );

            CREATE TABLE IF NOT EXISTS WorkflowDrafts (
                DraftId TEXT PRIMARY KEY,
                WorkflowId TEXT NOT NULL,
                BaseVersionId TEXT NULL,
                DraftJson TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                FOREIGN KEY (WorkflowId) REFERENCES Workflows(WorkflowId)
            );

            CREATE TABLE IF NOT EXISTS Approvals (
                ApprovalId TEXT PRIMARY KEY,
                VersionId TEXT NOT NULL,
                PlanHash TEXT NOT NULL,
                Approver TEXT NOT NULL,
                Decision TEXT NOT NULL,
                ApprovedNodesJson TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                FOREIGN KEY (VersionId) REFERENCES WorkflowVersions(VersionId)
            );

            CREATE TABLE IF NOT EXISTS Executions (
                ExecutionId TEXT PRIMARY KEY,
                VersionId TEXT NOT NULL,
                PlanHash TEXT NOT NULL,
                Status TEXT NOT NULL,
                StartedAt TEXT NOT NULL,
                EndedAt TEXT NULL,
                FOREIGN KEY (VersionId) REFERENCES WorkflowVersions(VersionId)
            );

            CREATE TABLE IF NOT EXISTS ActionExecutions (
                ActionExecutionId TEXT PRIMARY KEY,
                ExecutionId TEXT NOT NULL,
                NodeId TEXT NOT NULL,
                Status TEXT NOT NULL,
                AttemptCount INTEGER NOT NULL,
                LastFailureClassification TEXT NOT NULL,
                LastMessage TEXT NOT NULL,
                FOREIGN KEY (ExecutionId) REFERENCES Executions(ExecutionId)
            );

            CREATE TABLE IF NOT EXISTS ExecutionEvents (
                EventId TEXT PRIMARY KEY,
                ExecutionId TEXT NOT NULL,
                ActionExecutionId TEXT NULL,
                EventType TEXT NOT NULL,
                Timestamp TEXT NOT NULL,
                DetailsJson TEXT NOT NULL,
                FOREIGN KEY (ExecutionId) REFERENCES Executions(ExecutionId)
            );

            CREATE TABLE IF NOT EXISTS RecoveryRecords (
                RecoveryId TEXT PRIMARY KEY,
                ExecutionId TEXT NOT NULL,
                RecoveryStatus TEXT NOT NULL,
                Decision TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                ResolvedAt TEXT NULL,
                FOREIGN KEY (ExecutionId) REFERENCES Executions(ExecutionId)
            );

            CREATE TABLE IF NOT EXISTS AiAuditLogs (
                AuditId TEXT PRIMARY KEY,
                ProviderName TEXT NOT NULL,
                ModelId TEXT NOT NULL,
                ValidationStageSummary TEXT NOT NULL,
                IsAccepted INTEGER NOT NULL,
                ErrorClassification TEXT NOT NULL,
                RepairAttemptCount INTEGER NOT NULL,
                ExternalSendApproved INTEGER NOT NULL,
                Timestamp TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public void VerifyDatabaseIntegrity()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA integrity_check;";
        string? res = cmd.ExecuteScalar()?.ToString();
        if (!string.Equals(res, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"SQLite integrity check failed: {res}");
        }
    }

    public WorkflowVersionRecord CreateWorkflowAndInitialVersion(string workflowId, string name, string irJson, string schemaVersion)
    {
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();

        var now = DateTimeOffset.UtcNow;
        string versionId = $"ver-{Guid.NewGuid():N}";

        using (var cmdWf = conn.CreateCommand())
        {
            cmdWf.Transaction = tx;
            cmdWf.CommandText = "INSERT INTO Workflows (WorkflowId, Name, CreatedAt) VALUES ($wid, $name, $cat);";
            cmdWf.Parameters.AddWithValue("$wid", workflowId);
            cmdWf.Parameters.AddWithValue("$name", name);
            cmdWf.Parameters.AddWithValue("$cat", now.ToString("O"));
            cmdWf.ExecuteNonQuery();
        }

        using (var cmdVer = conn.CreateCommand())
        {
            cmdVer.Transaction = tx;
            cmdVer.CommandText = """
                INSERT INTO WorkflowVersions (VersionId, WorkflowId, VersionNumber, IrJson, SchemaVersion, CreatedAt)
                VALUES ($vid, $wid, 1, $json, $sver, $cat);
                """;
            cmdVer.Parameters.AddWithValue("$vid", versionId);
            cmdVer.Parameters.AddWithValue("$wid", workflowId);
            cmdVer.Parameters.AddWithValue("$json", irJson);
            cmdVer.Parameters.AddWithValue("$sver", schemaVersion);
            cmdVer.Parameters.AddWithValue("$cat", now.ToString("O"));
            cmdVer.ExecuteNonQuery();
        }

        tx.Commit();
        return new WorkflowVersionRecord(versionId, workflowId, name, 1, irJson, schemaVersion, now);
    }

    public WorkflowDraftRecord SaveOrUpdateDraft(string workflowId, string? baseVersionId, string draftJson)
    {
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();
        var now = DateTimeOffset.UtcNow;
        string draftId = $"draft-{Guid.NewGuid():N}";

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO WorkflowDrafts (DraftId, WorkflowId, BaseVersionId, DraftJson, UpdatedAt)
                VALUES ($did, $wid, $bvid, $json, $uat);
                """;
            cmd.Parameters.AddWithValue("$did", draftId);
            cmd.Parameters.AddWithValue("$wid", workflowId);
            cmd.Parameters.AddWithValue("$bvid", (object?)baseVersionId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$json", draftJson);
            cmd.Parameters.AddWithValue("$uat", now.ToString("O"));
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
        return new WorkflowDraftRecord(draftId, workflowId, baseVersionId, draftJson, now);
    }

    public WorkflowDraftRecord? GetDraftById(string draftId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DraftId, WorkflowId, BaseVersionId, DraftJson, UpdatedAt FROM WorkflowDrafts WHERE DraftId = $did;";
        cmd.Parameters.AddWithValue("$did", draftId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        return new WorkflowDraftRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetString(3),
            DateTimeOffset.Parse(reader.GetString(4)));
    }

    public WorkflowVersionRecord CommitNewVersionFromDraft(
        string draftId,
        string workflowId,
        string name,
        int versionNumber,
        string irJson,
        string schemaVersion)
    {
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();
        var now = DateTimeOffset.UtcNow;
        string versionId = $"ver-{Guid.NewGuid():N}";

        using (var cmdVer = conn.CreateCommand())
        {
            cmdVer.Transaction = tx;
            cmdVer.CommandText = """
                INSERT INTO WorkflowVersions (VersionId, WorkflowId, VersionNumber, IrJson, SchemaVersion, CreatedAt)
                VALUES ($vid, $wid, $vnum, $json, $sver, $cat);
                """;
            cmdVer.Parameters.AddWithValue("$vid", versionId);
            cmdVer.Parameters.AddWithValue("$wid", workflowId);
            cmdVer.Parameters.AddWithValue("$vnum", versionNumber);
            cmdVer.Parameters.AddWithValue("$json", irJson);
            cmdVer.Parameters.AddWithValue("$sver", schemaVersion);
            cmdVer.Parameters.AddWithValue("$cat", now.ToString("O"));
            cmdVer.ExecuteNonQuery();
        }

        using (var cmdDelDraft = conn.CreateCommand())
        {
            cmdDelDraft.Transaction = tx;
            cmdDelDraft.CommandText = "DELETE FROM WorkflowDrafts WHERE DraftId = $did;";
            cmdDelDraft.Parameters.AddWithValue("$did", draftId);
            cmdDelDraft.ExecuteNonQuery();
        }

        tx.Commit();
        return new WorkflowVersionRecord(versionId, workflowId, name, versionNumber, irJson, schemaVersion, now);
    }

    public WorkflowVersionRecord? GetVersionById(string versionId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT v.VersionId, v.WorkflowId, w.Name, v.VersionNumber, v.IrJson, v.SchemaVersion, v.CreatedAt
            FROM WorkflowVersions v
            JOIN Workflows w ON w.WorkflowId = v.WorkflowId
            WHERE v.VersionId = $vid;
            """;
        cmd.Parameters.AddWithValue("$vid", versionId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        return new WorkflowVersionRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt32(3),
            reader.GetString(4),
            reader.GetString(5),
            DateTimeOffset.Parse(reader.GetString(6)));
    }

    public IReadOnlyList<WorkflowVersionRecord> GetAllLatestVersions()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT v.VersionId, v.WorkflowId, w.Name, v.VersionNumber, v.IrJson, v.SchemaVersion, v.CreatedAt
            FROM WorkflowVersions v
            JOIN Workflows w ON w.WorkflowId = v.WorkflowId
            WHERE v.VersionNumber = (
                SELECT MAX(v2.VersionNumber)
                FROM WorkflowVersions v2
                WHERE v2.WorkflowId = v.WorkflowId
            )
            ORDER BY v.CreatedAt DESC;
            """;
        using var reader = cmd.ExecuteReader();
        var list = new List<WorkflowVersionRecord>();
        while (reader.Read())
        {
            list.Add(new WorkflowVersionRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetString(4),
                reader.GetString(5),
                DateTimeOffset.Parse(reader.GetString(6))));
        }
        return list;
    }

    public IReadOnlyList<ExecutionRecord> GetAllExecutions()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT ExecutionId, VersionId, PlanHash, Status, StartedAt, EndedAt
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
                reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5))));
        }
        return list;
    }

    public void DeleteWorkflowCascade(string workflowId)
    {
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();

        string[] statements =
        {
            "DELETE FROM ExecutionEvents WHERE ExecutionId IN (SELECT ExecutionId FROM Executions WHERE VersionId IN (SELECT VersionId FROM WorkflowVersions WHERE WorkflowId = $wid));",
            "DELETE FROM ActionExecutions WHERE ExecutionId IN (SELECT ExecutionId FROM Executions WHERE VersionId IN (SELECT VersionId FROM WorkflowVersions WHERE WorkflowId = $wid));",
            "DELETE FROM RecoveryRecords WHERE ExecutionId IN (SELECT ExecutionId FROM Executions WHERE VersionId IN (SELECT VersionId FROM WorkflowVersions WHERE WorkflowId = $wid));",
            "DELETE FROM Executions WHERE VersionId IN (SELECT VersionId FROM WorkflowVersions WHERE WorkflowId = $wid);",
            "DELETE FROM Approvals WHERE VersionId IN (SELECT VersionId FROM WorkflowVersions WHERE WorkflowId = $wid);",
            "DELETE FROM WorkflowDrafts WHERE WorkflowId = $wid;",
            "DELETE FROM WorkflowVersions WHERE WorkflowId = $wid;",
            "DELETE FROM Workflows WHERE WorkflowId = $wid;"
        };

        foreach (string sql in statements)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$wid", workflowId);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public WorkflowVersionRecord? GetLatestVersion(string workflowId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT v.VersionId, v.WorkflowId, w.Name, v.VersionNumber, v.IrJson, v.SchemaVersion, v.CreatedAt
            FROM WorkflowVersions v
            JOIN Workflows w ON w.WorkflowId = v.WorkflowId
            WHERE v.WorkflowId = $wid
            ORDER BY v.VersionNumber DESC
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$wid", workflowId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        return new WorkflowVersionRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt32(3),
            reader.GetString(4),
            reader.GetString(5),
            DateTimeOffset.Parse(reader.GetString(6)));
    }

    public ApprovalRecord SaveApproval(
        string versionId,
        string planHash,
        string approver,
        ApprovalDecision decision,
        IReadOnlyList<string> approvedHighRiskNodeIds)
    {
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();
        var now = DateTimeOffset.UtcNow;
        string approvalId = $"appr-{Guid.NewGuid():N}";
        string nodesJson = JsonSerializer.Serialize(approvedHighRiskNodeIds);

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO Approvals (ApprovalId, VersionId, PlanHash, Approver, Decision, ApprovedNodesJson, CreatedAt)
                VALUES ($aid, $vid, $ph, $app, $dec, $nodes, $cat);
                """;
            cmd.Parameters.AddWithValue("$aid", approvalId);
            cmd.Parameters.AddWithValue("$vid", versionId);
            cmd.Parameters.AddWithValue("$ph", planHash);
            cmd.Parameters.AddWithValue("$app", approver);
            cmd.Parameters.AddWithValue("$dec", decision.ToString());
            cmd.Parameters.AddWithValue("$nodes", nodesJson);
            cmd.Parameters.AddWithValue("$cat", now.ToString("O"));
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
        return new ApprovalRecord(approvalId, versionId, planHash, approver, decision, approvedHighRiskNodeIds, now);
    }

    public ApprovalRecord? GetApprovalById(string approvalId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT ApprovalId, VersionId, PlanHash, Approver, Decision, ApprovedNodesJson, CreatedAt FROM Approvals WHERE ApprovalId = $aid;";
        cmd.Parameters.AddWithValue("$aid", approvalId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        var nodes = JsonSerializer.Deserialize<List<string>>(reader.GetString(5)) ?? new List<string>();
        return new ApprovalRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            Enum.Parse<ApprovalDecision>(reader.GetString(4)),
            nodes,
            DateTimeOffset.Parse(reader.GetString(6)));
    }

    // 状態変更と履歴追記は必ず同一トランザクションで実行する（仕様11.3 / 14.3 / 14.4）
    public void CreateExecutionAtomic(string executionId, string versionId, string planHash, ExecutionStatus initialStatus, string details)
    {
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();
        var now = DateTimeOffset.UtcNow;

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO Executions (ExecutionId, VersionId, PlanHash, Status, StartedAt, EndedAt)
                VALUES ($eid, $vid, $ph, $st, $sat, NULL);
                """;
            cmd.Parameters.AddWithValue("$eid", executionId);
            cmd.Parameters.AddWithValue("$vid", versionId);
            cmd.Parameters.AddWithValue("$ph", planHash);
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
        cmd.CommandText = "SELECT ExecutionId, VersionId, PlanHash, Status, StartedAt, EndedAt FROM Executions WHERE ExecutionId = $eid;";
        cmd.Parameters.AddWithValue("$eid", executionId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        return new ExecutionRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            Enum.Parse<ExecutionStatus>(reader.GetString(3)),
            DateTimeOffset.Parse(reader.GetString(4)),
            reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5)));
    }

    public IReadOnlyList<ExecutionRecord> GetNonTerminalExecutions()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT ExecutionId, VersionId, PlanHash, Status, StartedAt, EndedAt
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
                reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5))));
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

    public RecoveryRecord CreateRecoveryRecord(string executionId, RecoveryStatus status, string decisionNotes)
    {
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();
        var now = DateTimeOffset.UtcNow;
        string recId = $"rec-{Guid.NewGuid():N}";

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO RecoveryRecords (RecoveryId, ExecutionId, RecoveryStatus, Decision, CreatedAt, ResolvedAt)
                VALUES ($rid, $eid, $st, $dec, $cat, NULL);
                """;
            cmd.Parameters.AddWithValue("$rid", recId);
            cmd.Parameters.AddWithValue("$eid", executionId);
            cmd.Parameters.AddWithValue("$st", status.ToString());
            cmd.Parameters.AddWithValue("$dec", decisionNotes);
            cmd.Parameters.AddWithValue("$cat", now.ToString("O"));
            cmd.ExecuteNonQuery();
        }

        InsertEventInternal(conn, tx, executionId, null, "RecoveryRecordCreated", decisionNotes, now);
        tx.Commit();
        return new RecoveryRecord(recId, executionId, status, decisionNotes, now, null);
    }

    public RecoveryRecord ResolveRecoveryRecordAtomic(string recoveryId, RecoveryStatus resolution, string decisionNotes)
    {
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();
        var now = DateTimeOffset.UtcNow;

        string executionId;
        DateTimeOffset createdAt;
        using (var cmdGet = conn.CreateCommand())
        {
            cmdGet.Transaction = tx;
            cmdGet.CommandText = "SELECT ExecutionId, CreatedAt FROM RecoveryRecords WHERE RecoveryId = $rid;";
            cmdGet.Parameters.AddWithValue("$rid", recoveryId);
            using var reader = cmdGet.ExecuteReader();
            if (!reader.Read())
            {
                throw new InvalidOperationException($"Recovery record '{recoveryId}' not found.");
            }
            executionId = reader.GetString(0);
            createdAt = DateTimeOffset.Parse(reader.GetString(1));
        }

        using (var cmdUpd = conn.CreateCommand())
        {
            cmdUpd.Transaction = tx;
            cmdUpd.CommandText = """
                UPDATE RecoveryRecords
                SET RecoveryStatus = $st, Decision = $dec, ResolvedAt = $rat
                WHERE RecoveryId = $rid;
                """;
            cmdUpd.Parameters.AddWithValue("$st", resolution.ToString());
            cmdUpd.Parameters.AddWithValue("$dec", decisionNotes);
            cmdUpd.Parameters.AddWithValue("$rat", now.ToString("O"));
            cmdUpd.Parameters.AddWithValue("$rid", recoveryId);
            cmdUpd.ExecuteNonQuery();
        }

        ExecutionStatus resolvedExecStatus = resolution switch
        {
            RecoveryStatus.ResolvedAsCompleted => ExecutionStatus.Succeeded,
            RecoveryStatus.ResolvedAsCancelled => ExecutionStatus.Cancelled,
            _ => ExecutionStatus.Failed
        };

        using (var cmdExec = conn.CreateCommand())
        {
            cmdExec.Transaction = tx;
            cmdExec.CommandText = "UPDATE Executions SET Status = $est, EndedAt = COALESCE(EndedAt, $now) WHERE ExecutionId = $eid;";
            cmdExec.Parameters.AddWithValue("$est", resolvedExecStatus.ToString());
            cmdExec.Parameters.AddWithValue("$now", now.ToString("O"));
            cmdExec.Parameters.AddWithValue("$eid", executionId);
            cmdExec.ExecuteNonQuery();
        }

        InsertEventInternal(conn, tx, executionId, null, $"RecoveryResolved:{resolution}", decisionNotes, now);
        tx.Commit();

        return new RecoveryRecord(recoveryId, executionId, resolution, decisionNotes, createdAt, now);
    }

    public IReadOnlyList<RecoveryRecord> GetPendingRecoveries()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT RecoveryId, ExecutionId, RecoveryStatus, Decision, CreatedAt, ResolvedAt
            FROM RecoveryRecords
            WHERE RecoveryStatus = 'Pending';
            """;
        using var reader = cmd.ExecuteReader();
        var list = new List<RecoveryRecord>();
        while (reader.Read())
        {
            list.Add(new RecoveryRecord(
                reader.GetString(0),
                reader.GetString(1),
                Enum.Parse<RecoveryStatus>(reader.GetString(2)),
                reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4)),
                reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5))));
        }
        return list;
    }

    public void InsertAiAuditLog(AiAuditRecord record)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO AiAuditLogs (AuditId, ProviderName, ModelId, ValidationStageSummary, IsAccepted, ErrorClassification, RepairAttemptCount, ExternalSendApproved, Timestamp)
            VALUES ($id, $p, $m, $s, $acc, $err, $rep, $ext, $ts);
            """;
        cmd.Parameters.AddWithValue("$id", record.AuditId);
        cmd.Parameters.AddWithValue("$p", record.ProviderName);
        cmd.Parameters.AddWithValue("$m", record.ModelId);
        cmd.Parameters.AddWithValue("$s", record.ValidationStageSummary);
        cmd.Parameters.AddWithValue("$acc", record.IsAccepted ? 1 : 0);
        cmd.Parameters.AddWithValue("$err", record.ErrorClassification);
        cmd.Parameters.AddWithValue("$rep", record.RepairAttemptCount);
        cmd.Parameters.AddWithValue("$ext", record.ExternalSendApproved ? 1 : 0);
        cmd.Parameters.AddWithValue("$ts", record.Timestamp.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<AiAuditRecord> GetAiAuditLogs()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT AuditId, ProviderName, ModelId, ValidationStageSummary, IsAccepted, ErrorClassification, RepairAttemptCount, ExternalSendApproved, Timestamp
            FROM AiAuditLogs
            ORDER BY Timestamp DESC;
            """;
        using var reader = cmd.ExecuteReader();
        var list = new List<AiAuditRecord>();
        while (reader.Read())
        {
            list.Add(new AiAuditRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4) == 1,
                reader.GetString(5),
                reader.GetInt32(6),
                reader.GetInt32(7) == 1,
                DateTimeOffset.Parse(reader.GetString(8))));
        }
        return list;
    }
}
