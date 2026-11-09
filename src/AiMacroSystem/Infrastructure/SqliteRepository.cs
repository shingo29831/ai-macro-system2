// Module: @role: SQLiteによるハイブリッド永続化・状態更新と履歴追記の同一トランザクション保証
using System;
using System.Collections.Generic;
using System.Text.Json;
using AiMacroSystem.Application;
using AiMacroSystem.Domain;
using Microsoft.Data.Sqlite;

namespace AiMacroSystem.Infrastructure;

public sealed partial class SqliteRepository
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
                ApprovalId TEXT NULL,
                Status TEXT NOT NULL,
                StartedAt TEXT NOT NULL,
                EndedAt TEXT NULL,
                FOREIGN KEY (VersionId) REFERENCES WorkflowVersions(VersionId),
                FOREIGN KEY (ApprovalId) REFERENCES Approvals(ApprovalId)
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

            CREATE TABLE IF NOT EXISTS RecordedRawEvents (
                EventId TEXT PRIMARY KEY,
                ApplicationKind TEXT NOT NULL,
                EventKind TEXT NOT NULL,
                TargetRole TEXT NULL,
                TargetName TEXT NULL,
                TargetLabel TEXT NULL,
                TargetStableId TEXT NULL,
                UrlOrWorkbookPath TEXT NULL,
                SheetName TEXT NULL,
                RangeAddress TEXT NULL,
                CapturedValue TEXT NULL,
                IsSensitive INTEGER NOT NULL,
                Timestamp TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS NormalizedActionCandidates (
                CandidateId TEXT PRIMARY KEY,
                SuggestedActionType TEXT NOT NULL,
                SourceRawEventIdsJson TEXT NOT NULL,
                LocatorJson TEXT NULL,
                TargetUrlOrWorkbook TEXT NULL,
                SheetName TEXT NULL,
                RangeAddress TEXT NULL,
                SanitizedValue TEXT NULL,
                IsSensitive INTEGER NOT NULL,
                IsAmbiguous INTEGER NOT NULL,
                AmbiguityReason TEXT NULL
            );

            CREATE TRIGGER IF NOT EXISTS trg_WorkflowVersions_Immutable
            BEFORE UPDATE ON WorkflowVersions
            BEGIN
                SELECT RAISE(ABORT, 'Formal WorkflowVersions are immutable.');
            END;

            CREATE TRIGGER IF NOT EXISTS trg_WorkflowVersions_NoDelete
            BEFORE DELETE ON WorkflowVersions
            BEGIN
                SELECT RAISE(ABORT, 'Formal WorkflowVersions cannot be deleted.');
            END;

            CREATE TRIGGER IF NOT EXISTS trg_ExecutionEvents_AppendOnly_NoUpdate
            BEFORE UPDATE ON ExecutionEvents
            BEGIN
                SELECT RAISE(ABORT, 'ExecutionEvents are append-only and cannot be updated.');
            END;

            CREATE TRIGGER IF NOT EXISTS trg_ExecutionEvents_AppendOnly_NoDelete
            BEFORE DELETE ON ExecutionEvents
            BEGIN
                SELECT RAISE(ABORT, 'ExecutionEvents are append-only and cannot be deleted.');
            END;

            CREATE TRIGGER IF NOT EXISTS trg_AiAuditLogs_AppendOnly_NoUpdate
            BEFORE UPDATE ON AiAuditLogs
            BEGIN
                SELECT RAISE(ABORT, 'AiAuditLogs are append-only and cannot be updated.');
            END;

            CREATE TRIGGER IF NOT EXISTS trg_AiAuditLogs_AppendOnly_NoDelete
            BEFORE DELETE ON AiAuditLogs
            BEGIN
                SELECT RAISE(ABORT, 'AiAuditLogs are append-only and cannot be deleted.');
            END;
            """;
        cmd.ExecuteNonQuery();

        EnsureApprovalIdColumnExists(conn);
        EnsureRawEventsMultiLogColumnsExist(conn);
    }

    private static void EnsureRawEventsMultiLogColumnsExist(SqliteConnection conn)
    {
        var existingCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var checkCmd = conn.CreateCommand())
        {
            checkCmd.CommandText = "PRAGMA table_info(RecordedRawEvents);";
            using var reader = checkCmd.ExecuteReader();
            while (reader.Read())
            {
                existingCols.Add(reader.GetString(1));
            }
        }

        (string Name, string SqlType)[] requiredColumns =
        {
            ("LogSource", "TEXT NOT NULL DEFAULT 'PrimaryHook'"),
            ("CssSelector", "TEXT NULL"),
            ("CoordinateX", "INTEGER NULL"),
            ("CoordinateY", "INTEGER NULL"),
            ("SourceConfidence", "REAL NOT NULL DEFAULT 0.85"),
            ("WindowOrProcessName", "TEXT NULL")
        };

        foreach (var (colName, sqlType) in requiredColumns)
        {
            if (!existingCols.Contains(colName))
            {
                using var alterCmd = conn.CreateCommand();
                alterCmd.CommandText = $"ALTER TABLE RecordedRawEvents ADD COLUMN {colName} {sqlType};";
                alterCmd.ExecuteNonQuery();
            }
        }
    }

    private static void EnsureApprovalIdColumnExists(SqliteConnection conn)
    {
        using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = "PRAGMA table_info(Executions);";
        bool hasApprovalId = false;
        using (var reader = checkCmd.ExecuteReader())
        {
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), "ApprovalId", StringComparison.OrdinalIgnoreCase))
                {
                    hasApprovalId = true;
                    break;
                }
            }
        }
        if (!hasApprovalId)
        {
            using var alterCmd = conn.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE Executions ADD COLUMN ApprovalId TEXT NULL;";
            alterCmd.ExecuteNonQuery();
        }
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
}
