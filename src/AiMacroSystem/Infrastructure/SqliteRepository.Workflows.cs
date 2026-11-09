// Module: @role: ワークフロー定義・不変バージョン・ドラフト・実行計画承認レコードの永続化
using System;
using System.Collections.Generic;
using System.Text.Json;
using AiMacroSystem.Domain;
using Microsoft.Data.Sqlite;

namespace AiMacroSystem.Infrastructure;

public sealed partial class SqliteRepository
{
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

    public IReadOnlyList<WorkflowDraftRecord> GetAllDrafts()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DraftId, WorkflowId, BaseVersionId, DraftJson, UpdatedAt FROM WorkflowDrafts ORDER BY UpdatedAt DESC;";
        using var reader = cmd.ExecuteReader();
        var list = new List<WorkflowDraftRecord>();
        while (reader.Read())
        {
            list.Add(new WorkflowDraftRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4))));
        }
        return list;
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

    public void DeleteWorkflowCascade(string workflowId)
    {
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();

        // 管理上の完全削除時のみ一時的に不変トリガーを解除し、削除後に再設定する
        string[] preStatements =
        {
            "DROP TRIGGER IF EXISTS trg_ExecutionEvents_AppendOnly_NoDelete;",
            "DROP TRIGGER IF EXISTS trg_WorkflowVersions_NoDelete;"
        };
        foreach (string sql in preStatements)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

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

        using (var restoreCmd = conn.CreateCommand())
        {
            restoreCmd.Transaction = tx;
            restoreCmd.CommandText = """
                CREATE TRIGGER IF NOT EXISTS trg_WorkflowVersions_NoDelete
                BEFORE DELETE ON WorkflowVersions
                BEGIN
                    SELECT RAISE(ABORT, 'Formal WorkflowVersions cannot be deleted.');
                END;

                CREATE TRIGGER IF NOT EXISTS trg_ExecutionEvents_AppendOnly_NoDelete
                BEFORE DELETE ON ExecutionEvents
                BEGIN
                    SELECT RAISE(ABORT, 'ExecutionEvents are append-only and cannot be deleted.');
                END;
                """;
            restoreCmd.ExecuteNonQuery();
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
}
