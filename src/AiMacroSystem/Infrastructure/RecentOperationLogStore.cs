// Module: @role: 最近の操作ログ・正規化結果・AI監査結果を専用ディレクトリのテキストファイルへ記録し古いログを自動削除するストア
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AiMacroSystem.Application;

namespace AiMacroSystem.Infrastructure;

public sealed class RecentOperationLogOptions
{
    public int MaxRetainedSessionFiles { get; init; } = 3;
    public TimeSpan MaxFileAge { get; init; } = TimeSpan.FromDays(1);
    public int MaxEventsPerTextFile { get; init; } = 200;
    public TimeSpan RecentEventWindow { get; init; } = TimeSpan.FromMinutes(30);
}

public sealed class RecentOperationLogStore
{
    public const string LatestLogFileName = "latest-operation-log.txt";
    private const string SessionFilePrefix = "session-";
    private const string SessionFilePattern = "session-*.txt";

    private readonly object _sync = new();
    private readonly RecentOperationLogOptions _options;
    private readonly List<RawRecordedEvent> _currentSessionEvents = new();
    private string _currentSessionId;
    private string _currentSessionFilePath;

    public string LogDirectoryPath { get; }
    public string LatestLogFilePath { get; }

    public RecentOperationLogStore(string? logDirectoryPath = null, RecentOperationLogOptions? options = null)
    {
        _options = options ?? new RecentOperationLogOptions();
        LogDirectoryPath = string.IsNullOrWhiteSpace(logDirectoryPath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AiMacroSystem",
                "logs")
            : Path.GetFullPath(logDirectoryPath);

        Directory.CreateDirectory(LogDirectoryPath);
        LatestLogFilePath = Path.Combine(LogDirectoryPath, LatestLogFileName);
        _currentSessionId = $"session-{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}";
        _currentSessionFilePath = Path.Combine(LogDirectoryPath, $"{_currentSessionId}.txt");
    }

    public void BeginSession()
    {
        lock (_sync)
        {
            Directory.CreateDirectory(LogDirectoryPath);
            _currentSessionEvents.Clear();
            _currentSessionId = $"session-{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}";
            _currentSessionFilePath = Path.Combine(LogDirectoryPath, $"{_currentSessionId}.txt");

            string header = BuildHeaderText(0, 0);
            File.WriteAllText(LatestLogFilePath, header, Encoding.UTF8);
            File.WriteAllText(_currentSessionFilePath, header, Encoding.UTF8);
            PruneOldLogFilesLocked();
        }
    }

    public void AppendRawEvent(RawRecordedEvent ev)
    {
        if (ev is null)
        {
            return;
        }

        lock (_sync)
        {
            Directory.CreateDirectory(LogDirectoryPath);
            _currentSessionEvents.Add(ev);

            // ファイル肥大化によるトークン浪費を防ぐため、セッション内でも直近上限件数のみ保持する
            int maxEvents = Math.Max(1, _options.MaxEventsPerTextFile);
            if (_currentSessionEvents.Count > maxEvents)
            {
                _currentSessionEvents.RemoveRange(0, _currentSessionEvents.Count - maxEvents);
                string trimmedContent = BuildSessionText(_currentSessionEvents, Array.Empty<NormalizedActionCandidate>(), null, 0);
                File.WriteAllText(LatestLogFilePath, trimmedContent, Encoding.UTF8);
                File.WriteAllText(_currentSessionFilePath, trimmedContent, Encoding.UTF8);
                return;
            }

            string line = FormatRawEventLine(ev) + Environment.NewLine;
            File.AppendAllText(LatestLogFilePath, line, Encoding.UTF8);
            File.AppendAllText(_currentSessionFilePath, line, Encoding.UTF8);
        }
    }

    public IReadOnlyList<RawRecordedEvent> FilterRecentEvents(IReadOnlyList<RawRecordedEvent> rawEvents)
    {
        if (rawEvents is null || rawEvents.Count == 0)
        {
            return Array.Empty<RawRecordedEvent>();
        }

        var ordered = rawEvents
            .OrderBy(e => e.Timestamp)
            .ToList();

        // 最新イベント時刻を基準に時間窓内のイベントのみ抽出し、時計ずれ耐性と古いログ除外を両立する
        DateTimeOffset latestTs = ordered[^1].Timestamp;
        DateTimeOffset cutoff = latestTs - _options.RecentEventWindow;
        int maxEvents = Math.Max(1, _options.MaxEventsPerTextFile);

        var recent = ordered
            .Where(e => e.Timestamp >= cutoff)
            .ToList();

        if (recent.Count > maxEvents)
        {
            recent = recent.Skip(recent.Count - maxEvents).ToList();
        }

        return recent;
    }

    public void WriteSessionSummary(
        IReadOnlyList<RawRecordedEvent> rawEvents,
        IReadOnlyList<NormalizedActionCandidate> candidates,
        AiGenerationOutcome? outcome = null)
    {
        lock (_sync)
        {
            Directory.CreateDirectory(LogDirectoryPath);
            var sourceEvents = rawEvents ?? Array.Empty<RawRecordedEvent>();
            var filteredEvents = FilterRecentEvents(sourceEvents);
            int trimmedCount = Math.Max(0, sourceEvents.Count - filteredEvents.Count);

            string content = BuildSessionText(
                filteredEvents,
                candidates ?? Array.Empty<NormalizedActionCandidate>(),
                outcome,
                trimmedCount);

            File.WriteAllText(LatestLogFilePath, content, Encoding.UTF8);
            File.WriteAllText(_currentSessionFilePath, content, Encoding.UTF8);
            PruneOldLogFilesLocked();
        }
    }

    public int PruneOldLogFiles()
    {
        lock (_sync)
        {
            return PruneOldLogFilesLocked();
        }
    }

    private int PruneOldLogFilesLocked()
    {
        if (!Directory.Exists(LogDirectoryPath))
        {
            return 0;
        }

        int deletedCount = 0;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        int keepCount = Math.Max(1, _options.MaxRetainedSessionFiles);

        var sessionFiles = new DirectoryInfo(LogDirectoryPath)
            .GetFiles(SessionFilePattern, SearchOption.TopDirectoryOnly)
            .Where(f => f.Name.StartsWith(SessionFilePrefix, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ThenByDescending(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        for (int i = 0; i < sessionFiles.Count; i++)
        {
            var file = sessionFiles[i];
            bool exceedsRetainLimit = i >= keepCount;
            bool isExpired = (now - file.LastWriteTimeUtc) > _options.MaxFileAge;
            bool isCurrentFile = string.Equals(file.FullName, _currentSessionFilePath, StringComparison.OrdinalIgnoreCase);

            if ((exceedsRetainLimit || isExpired) && !isCurrentFile)
            {
                try
                {
                    file.Delete();
                    deletedCount++;
                }
                catch (IOException)
                {
                    // 外部ビューアで開かれているファイルは次回ローテーション時に再試行する
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        return deletedCount;
    }

    private string BuildHeaderText(int eventCount, int trimmedOldCount)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# AiMacroSystem Recent Operation Log");
        sb.AppendLine($"SessionId: {_currentSessionId}");
        sb.AppendLine($"UpdatedAt: {DateTimeOffset.Now:O}");
        sb.AppendLine($"RecentEvents: {eventCount} (TrimmedOld: {trimmedOldCount})");
        sb.AppendLine();
        sb.AppendLine("## RawEvents");
        return sb.ToString();
    }

    private string BuildSessionText(
        IReadOnlyList<RawRecordedEvent> recentEvents,
        IReadOnlyList<NormalizedActionCandidate> candidates,
        AiGenerationOutcome? outcome,
        int trimmedOldCount)
    {
        var sb = new StringBuilder();
        sb.Append(BuildHeaderText(recentEvents.Count, trimmedOldCount));

        foreach (var ev in recentEvents)
        {
            sb.AppendLine(FormatRawEventLine(ev));
        }

        if (candidates.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"## NormalizedCandidates ({candidates.Count})");
            foreach (var cand in candidates)
            {
                sb.AppendLine(FormatCandidateLine(cand));
            }
        }

        if (outcome is not null)
        {
            sb.AppendLine();
            sb.AppendLine("## AiGeneration");
            sb.AppendLine($"Status: {(outcome.IsAccepted ? "ACCEPTED" : "BLOCKED")}");
            sb.AppendLine($"Chunks: {outcome.TotalChunksCount} | EstimatedTokens: {outcome.EstimatedTokens} | FilteredOut: {outcome.FilteredOutEventCount}");
            if (!string.IsNullOrWhiteSpace(outcome.FailedChunkId))
            {
                sb.AppendLine($"FailedChunkId: {outcome.FailedChunkId}");
            }
            if (!string.IsNullOrWhiteSpace(outcome.ErrorReason))
            {
                sb.AppendLine($"Reason: {NormalizeInline(outcome.ErrorReason)}");
            }
            if (outcome.Validation.Issues.Count > 0)
            {
                sb.AppendLine("ValidationIssues:");
                foreach (var issue in outcome.Validation.Issues)
                {
                    sb.AppendLine($"- [{issue.Stage}/{issue.Code}] {NormalizeInline(issue.Message)}");
                }
            }
        }

        return sb.ToString();
    }

    private static string FormatRawEventLine(RawRecordedEvent ev)
    {
        var parts = new List<string>
        {
            ev.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff"),
            ev.ApplicationKind,
            ev.EventKind
        };

        if (!string.IsNullOrWhiteSpace(ev.WindowOrProcessName))
        {
            parts.Add($"win={NormalizeInline(ev.WindowOrProcessName)}");
        }
        if (!string.IsNullOrWhiteSpace(ev.UrlOrWorkbookPath))
        {
            parts.Add($"target={NormalizeInline(SensitiveDataProtector.SanitizeText(ev.UrlOrWorkbookPath))}");
        }
        if (!string.IsNullOrWhiteSpace(ev.SheetName))
        {
            parts.Add($"sheet={NormalizeInline(ev.SheetName)}");
        }
        if (!string.IsNullOrWhiteSpace(ev.RangeAddress))
        {
            parts.Add($"range={NormalizeInline(ev.RangeAddress)}");
        }
        if (!string.IsNullOrWhiteSpace(ev.TargetLabel) || !string.IsNullOrWhiteSpace(ev.TargetName))
        {
            string elem = !string.IsNullOrWhiteSpace(ev.TargetLabel) ? ev.TargetLabel : ev.TargetName!;
            parts.Add($"elem={NormalizeInline(elem)}");
        }
        if (!string.IsNullOrWhiteSpace(ev.TargetRole))
        {
            parts.Add($"role={NormalizeInline(ev.TargetRole)}");
        }
        if (ev.IsSensitive)
        {
            parts.Add($"val={SensitiveDataProtector.RedactedPlaceholder}");
        }
        else if (!string.IsNullOrWhiteSpace(ev.CapturedValue))
        {
            parts.Add($"val={NormalizeInline(SensitiveDataProtector.SanitizeText(ev.CapturedValue))}");
        }
        if (ev.CoordinateX.HasValue && ev.CoordinateY.HasValue)
        {
            parts.Add($"xy=({ev.CoordinateX},{ev.CoordinateY})");
        }
        if (!string.IsNullOrWhiteSpace(ev.LogSource))
        {
            parts.Add($"src={ev.LogSource}");
        }

        return string.Join(" | ", parts);
    }

    private static string FormatCandidateLine(NormalizedActionCandidate cand)
    {
        var parts = new List<string>
        {
            $"- [{cand.CandidateId}] {cand.SuggestedActionType}"
        };

        string? target = SensitiveDataProtector.SanitizeText(cand.TargetUrlOrWorkbook);
        if (!string.IsNullOrWhiteSpace(target))
        {
            string fullTarget = !string.IsNullOrWhiteSpace(cand.SheetName) || !string.IsNullOrWhiteSpace(cand.RangeAddress)
                ? $"{target}!{cand.SheetName}!{cand.RangeAddress}"
                : target;
            parts.Add($"target={NormalizeInline(fullTarget)}");
        }

        if (cand.Locator is not null)
        {
            var (_, resolved) = cand.Locator.ResolveByPriority();
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                parts.Add($"loc={NormalizeInline(resolved)}");
            }
        }

        if (cand.IsSensitive)
        {
            parts.Add($"val={SensitiveDataProtector.RedactedPlaceholder}");
        }
        else if (!string.IsNullOrWhiteSpace(cand.SanitizedValue))
        {
            parts.Add($"val={NormalizeInline(SensitiveDataProtector.SanitizeText(cand.SanitizedValue))}");
        }

        if (cand.IsAmbiguous)
        {
            parts.Add($"AMBIGUOUS={NormalizeInline(cand.AmbiguityReason ?? "unknown")}");
        }

        return string.Join(" | ", parts);
    }

    private static string NormalizeInline(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Replace("\r\n", "\\n").Replace("\r", "\\n").Replace("\n", "\\n").Trim();
    }
}
