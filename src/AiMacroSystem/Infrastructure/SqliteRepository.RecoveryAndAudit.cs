// Module: @role: 異常終了後の復旧レコード・AI生成監査ログ・生操作イベント・正規化候補の永続化
using System;
using System.Collections.Generic;
using System.Text.Json;
using AiMacroSystem.Application;
using AiMacroSystem.Domain;
using Microsoft.Data.Sqlite;

namespace AiMacroSystem.Infrastructure;

public sealed partial class SqliteRepository
{
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

    public IReadOnlyList<RecoveryRecord> GetAllRecoveries()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT RecoveryId, ExecutionId, RecoveryStatus, Decision, CreatedAt, ResolvedAt
            FROM RecoveryRecords
            ORDER BY CreatedAt DESC;
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

    public void InsertAiAuditLog(
        AiAuditRecord record,
        string? chunkId = null,
        int estimatedTokens = 0,
        int filteredOutEventCount = 0)
    {
        using var conn = OpenConnection();
        EnsureLogChunkAndAuditSchema(conn);

        var parsed = AiAuditMetadataFormatter.ParseMetadata(record.ValidationStageSummary);
        string? resolvedChunkId = chunkId ?? parsed.ChunkId;
        int resolvedTokens = estimatedTokens > 0 ? estimatedTokens : parsed.EstimatedTokens;
        int resolvedFiltered = filteredOutEventCount > 0 ? filteredOutEventCount : parsed.FilteredOutEventCount;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO AiAuditLogs (
                AuditId, ProviderName, ModelId, ValidationStageSummary, IsAccepted,
                ErrorClassification, RepairAttemptCount, ExternalSendApproved, Timestamp,
                ChunkId, EstimatedTokens, FilteredOutEventCount
            )
            VALUES ($id, $p, $m, $s, $acc, $err, $rep, $ext, $ts, $cid, $tok, $flt);
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
        cmd.Parameters.AddWithValue("$cid", (object?)resolvedChunkId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tok", resolvedTokens);
        cmd.Parameters.AddWithValue("$flt", resolvedFiltered);
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

    public void InsertRawRecordedEvent(RawRecordedEvent ev)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO RecordedRawEvents (
                EventId, ApplicationKind, EventKind, TargetRole, TargetName, TargetLabel, TargetStableId,
                UrlOrWorkbookPath, SheetName, RangeAddress, CapturedValue, IsSensitive, Timestamp,
                LogSource, CssSelector, CoordinateX, CoordinateY, SourceConfidence, WindowOrProcessName
            ) VALUES (
                $id, $app, $kind, $role, $name, $label, $sid, $url, $sheet, $range, $val, $sens, $ts,
                $lsrc, $css, $cx, $cy, $conf, $win
            );
            """;
        cmd.Parameters.AddWithValue("$id", ev.EventId);
        cmd.Parameters.AddWithValue("$app", ev.ApplicationKind);
        cmd.Parameters.AddWithValue("$kind", ev.EventKind);
        cmd.Parameters.AddWithValue("$role", (object?)ev.TargetRole ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$name", (object?)ev.TargetName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$label", (object?)ev.TargetLabel ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sid", (object?)ev.TargetStableId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$url", (object?)SensitiveDataProtector.SanitizeText(ev.UrlOrWorkbookPath) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sheet", (object?)ev.SheetName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$range", (object?)ev.RangeAddress ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$val", (object?)(ev.IsSensitive ? SensitiveDataProtector.RedactedPlaceholder : SensitiveDataProtector.SanitizeText(ev.CapturedValue)) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sens", ev.IsSensitive ? 1 : 0);
        cmd.Parameters.AddWithValue("$ts", ev.Timestamp.ToString("O"));
        cmd.Parameters.AddWithValue("$lsrc", string.IsNullOrWhiteSpace(ev.LogSource) ? LogSourceConstants.PrimaryHook : ev.LogSource);
        cmd.Parameters.AddWithValue("$css", (object?)ev.CssSelector ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cx", ev.CoordinateX.HasValue ? ev.CoordinateX.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$cy", ev.CoordinateY.HasValue ? ev.CoordinateY.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$conf", ev.SourceConfidence);
        cmd.Parameters.AddWithValue("$win", (object?)ev.WindowOrProcessName ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void InsertNormalizedCandidate(NormalizedActionCandidate candidate)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO NormalizedActionCandidates (
                CandidateId, SuggestedActionType, SourceRawEventIdsJson, LocatorJson,
                TargetUrlOrWorkbook, SheetName, RangeAddress, SanitizedValue,
                IsSensitive, IsAmbiguous, AmbiguityReason
            ) VALUES (
                $id, $act, $rawIds, $loc, $target, $sheet, $range, $val, $sens, $amb, $reas
            );
            """;
        cmd.Parameters.AddWithValue("$id", candidate.CandidateId);
        cmd.Parameters.AddWithValue("$act", candidate.SuggestedActionType);
        cmd.Parameters.AddWithValue("$rawIds", JsonSerializer.Serialize(candidate.SourceRawEventIds));
        cmd.Parameters.AddWithValue("$loc", candidate.Locator is not null ? JsonSerializer.Serialize(candidate.Locator) : DBNull.Value);
        cmd.Parameters.AddWithValue("$target", (object?)SensitiveDataProtector.SanitizeText(candidate.TargetUrlOrWorkbook) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sheet", (object?)candidate.SheetName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$range", (object?)candidate.RangeAddress ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$val", (object?)(candidate.IsSensitive ? SensitiveDataProtector.RedactedPlaceholder : SensitiveDataProtector.SanitizeText(candidate.SanitizedValue)) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sens", candidate.IsSensitive ? 1 : 0);
        cmd.Parameters.AddWithValue("$amb", candidate.IsAmbiguous ? 1 : 0);
        cmd.Parameters.AddWithValue("$reas", (object?)candidate.AmbiguityReason ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<RawRecordedEvent> GetRawRecordedEvents()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT EventId, ApplicationKind, EventKind, TargetRole, TargetName, TargetLabel, TargetStableId,
                   UrlOrWorkbookPath, SheetName, RangeAddress, CapturedValue, IsSensitive, Timestamp,
                   LogSource, CssSelector, CoordinateX, CoordinateY, SourceConfidence, WindowOrProcessName
            FROM RecordedRawEvents
            ORDER BY Timestamp ASC;
            """;
        using var reader = cmd.ExecuteReader();
        var list = new List<RawRecordedEvent>();
        while (reader.Read())
        {
            list.Add(new RawRecordedEvent(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.GetInt32(11) == 1,
                DateTimeOffset.Parse(reader.GetString(12)),
                reader.IsDBNull(13) ? LogSourceConstants.PrimaryHook : reader.GetString(13),
                reader.IsDBNull(14) ? null : reader.GetString(14),
                reader.IsDBNull(15) ? null : reader.GetInt32(15),
                reader.IsDBNull(16) ? null : reader.GetInt32(16),
                reader.IsDBNull(17) ? 0.85 : reader.GetDouble(17),
                reader.IsDBNull(18) ? null : reader.GetString(18)));
        }
        return list;
    }

    public IReadOnlyList<NormalizedActionCandidate> GetNormalizedCandidates()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT CandidateId, SuggestedActionType, SourceRawEventIdsJson, LocatorJson,
                   TargetUrlOrWorkbook, SheetName, RangeAddress, SanitizedValue,
                   IsSensitive, IsAmbiguous, AmbiguityReason
            FROM NormalizedActionCandidates;
            """;
        using var reader = cmd.ExecuteReader();
        var list = new List<NormalizedActionCandidate>();
        while (reader.Read())
        {
            var rawIds = JsonSerializer.Deserialize<List<string>>(reader.GetString(2)) ?? new List<string>();
            SemanticLocator? locator = reader.IsDBNull(3)
                ? null
                : JsonSerializer.Deserialize<SemanticLocator>(reader.GetString(3));

            list.Add(new NormalizedActionCandidate(
                reader.GetString(0),
                reader.GetString(1),
                rawIds,
                locator,
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.GetInt32(8) == 1,
                reader.GetInt32(9) == 1,
                reader.IsDBNull(10) ? null : reader.GetString(10)));
        }
        return list;
    }

    public void SaveLogChunk(LogChunk chunk)
    {
        using var conn = OpenConnection();
        EnsureLogChunkAndAuditSchema(conn);
        UpsertLogChunkInternal(conn, null, chunk);
    }

    public void SaveLogChunks(IEnumerable<LogChunk> chunks, bool replaceExisting = true)
    {
        using var conn = OpenConnection();
        EnsureLogChunkAndAuditSchema(conn);
        using var tx = conn.BeginTransaction();

        if (replaceExisting)
        {
            using var clearCmd = conn.CreateCommand();
            clearCmd.Transaction = tx;
            clearCmd.CommandText = "DELETE FROM RecordedLogChunks;";
            clearCmd.ExecuteNonQuery();
        }

        foreach (var chunk in chunks)
        {
            UpsertLogChunkInternal(conn, tx, chunk);
        }

        tx.Commit();
    }

    public IReadOnlyList<LogChunk> GetLogChunks()
    {
        using var conn = OpenConnection();
        EnsureLogChunkAndAuditSchema(conn);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT ChunkId, SequenceIndex, EstimatedTokens, AppCategory, StartTime, EndTime,
                   RawEventIdsJson, CandidateIdsJson, CandidatesJson, FilteredOutEventCount
            FROM RecordedLogChunks
            ORDER BY SequenceIndex ASC;
            """;
        using var reader = cmd.ExecuteReader();
        var list = new List<LogChunk>();
        while (reader.Read())
        {
            list.Add(ReadLogChunkRow(reader));
        }
        return list;
    }

    public LogChunk? GetLogChunkById(string chunkId)
    {
        using var conn = OpenConnection();
        EnsureLogChunkAndAuditSchema(conn);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT ChunkId, SequenceIndex, EstimatedTokens, AppCategory, StartTime, EndTime,
                   RawEventIdsJson, CandidateIdsJson, CandidatesJson, FilteredOutEventCount
            FROM RecordedLogChunks
            WHERE ChunkId = $cid;
            """;
        cmd.Parameters.AddWithValue("$cid", chunkId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadLogChunkRow(reader) : null;
    }

    private static void UpsertLogChunkInternal(SqliteConnection conn, SqliteTransaction? tx, LogChunk chunk)
    {
        var sanitizedCandidates = chunk.Candidates.Select(c => c with
        {
            TargetUrlOrWorkbook = SensitiveDataProtector.SanitizeText(c.TargetUrlOrWorkbook),
            SanitizedValue = c.IsSensitive
                ? SensitiveDataProtector.RedactedPlaceholder
                : SensitiveDataProtector.SanitizeText(c.SanitizedValue)
        }).ToList();

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO RecordedLogChunks (
                ChunkId, SequenceIndex, EstimatedTokens, AppCategory, StartTime, EndTime,
                RawEventIdsJson, CandidateIdsJson, CandidatesJson, FilteredOutEventCount
            ) VALUES (
                $cid, $seq, $tok, $app, $st, $et, $rawIds, $candIds, $cands, $flt
            );
            """;
        cmd.Parameters.AddWithValue("$cid", chunk.ChunkId);
        cmd.Parameters.AddWithValue("$seq", chunk.SequenceIndex);
        cmd.Parameters.AddWithValue("$tok", chunk.EstimatedTokens);
        cmd.Parameters.AddWithValue("$app", chunk.AppCategory);
        cmd.Parameters.AddWithValue("$st", chunk.StartTime.ToString("O"));
        cmd.Parameters.AddWithValue("$et", chunk.EndTime.ToString("O"));
        cmd.Parameters.AddWithValue("$rawIds", JsonSerializer.Serialize(chunk.RawEventIds));
        cmd.Parameters.AddWithValue("$candIds", JsonSerializer.Serialize(chunk.CandidateIds));
        cmd.Parameters.AddWithValue("$cands", JsonSerializer.Serialize(sanitizedCandidates));
        cmd.Parameters.AddWithValue("$flt", chunk.FilteredOutEventCount);
        cmd.ExecuteNonQuery();
    }

    private static LogChunk ReadLogChunkRow(SqliteDataReader reader)
    {
        var rawIds = JsonSerializer.Deserialize<List<string>>(reader.GetString(6)) ?? new List<string>();
        var candIds = JsonSerializer.Deserialize<List<string>>(reader.GetString(7)) ?? new List<string>();
        var candidates = JsonSerializer.Deserialize<List<NormalizedActionCandidate>>(reader.GetString(8))
                         ?? new List<NormalizedActionCandidate>();

        return new LogChunk(
            ChunkId: reader.GetString(0),
            SequenceIndex: reader.GetInt32(1),
            EstimatedTokens: reader.GetInt32(2),
            AppCategory: reader.GetString(3),
            StartTime: DateTimeOffset.Parse(reader.GetString(4)),
            EndTime: DateTimeOffset.Parse(reader.GetString(5)),
            RawEventIds: rawIds,
            CandidateIds: candIds,
            Candidates: candidates,
            FilteredOutEventCount: reader.IsDBNull(9) ? 0 : reader.GetInt32(9));
    }

    private static void EnsureLogChunkAndAuditSchema(SqliteConnection conn)
    {
        using (var tableCmd = conn.CreateCommand())
        {
            tableCmd.CommandText = """
                CREATE TABLE IF NOT EXISTS RecordedLogChunks (
                    ChunkId TEXT PRIMARY KEY,
                    SequenceIndex INTEGER NOT NULL,
                    EstimatedTokens INTEGER NOT NULL,
                    AppCategory TEXT NOT NULL,
                    StartTime TEXT NOT NULL,
                    EndTime TEXT NOT NULL,
                    RawEventIdsJson TEXT NOT NULL,
                    CandidateIdsJson TEXT NOT NULL,
                    CandidatesJson TEXT NOT NULL,
                    FilteredOutEventCount INTEGER NOT NULL DEFAULT 0
                );
                """;
            tableCmd.ExecuteNonQuery();
        }

        var existingCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var pragmaCmd = conn.CreateCommand())
        {
            pragmaCmd.CommandText = "PRAGMA table_info(AiAuditLogs);";
            using var reader = pragmaCmd.ExecuteReader();
            while (reader.Read())
            {
                existingCols.Add(reader.GetString(1));
            }
        }

        if (!existingCols.Contains("ChunkId"))
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE AiAuditLogs ADD COLUMN ChunkId TEXT;";
            alter.ExecuteNonQuery();
        }
        if (!existingCols.Contains("EstimatedTokens"))
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE AiAuditLogs ADD COLUMN EstimatedTokens INTEGER NOT NULL DEFAULT 0;";
            alter.ExecuteNonQuery();
        }
        if (!existingCols.Contains("FilteredOutEventCount"))
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE AiAuditLogs ADD COLUMN FilteredOutEventCount INTEGER NOT NULL DEFAULT 0;";
            alter.ExecuteNonQuery();
        }
    }

    public void PruneOldOperationLogs(int maxKeepRawEvents = 500, int maxKeepCandidates = 200)
    {
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();

        using (var pruneRawCmd = conn.CreateCommand())
        {
            pruneRawCmd.Transaction = tx;
            pruneRawCmd.CommandText = """
                DELETE FROM RecordedRawEvents
                WHERE EventId NOT IN (
                    SELECT EventId FROM RecordedRawEvents
                    ORDER BY Timestamp DESC
                    LIMIT $limit
                );
                """;
            pruneRawCmd.Parameters.AddWithValue("$limit", Math.Max(1, maxKeepRawEvents));
            pruneRawCmd.ExecuteNonQuery();
        }

        using (var pruneCandCmd = conn.CreateCommand())
        {
            pruneCandCmd.Transaction = tx;
            pruneCandCmd.CommandText = """
                DELETE FROM NormalizedActionCandidates
                WHERE rowid NOT IN (
                    SELECT rowid FROM NormalizedActionCandidates
                    ORDER BY rowid DESC
                    LIMIT $limit
                );
                """;
            pruneCandCmd.Parameters.AddWithValue("$limit", Math.Max(1, maxKeepCandidates));
            pruneCandCmd.ExecuteNonQuery();
        }

        tx.Commit();
    }
}
