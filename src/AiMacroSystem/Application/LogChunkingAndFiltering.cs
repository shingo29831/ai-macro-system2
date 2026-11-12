// Module: @role: 操作ログの推定トークン算出・ノイズ除去・重要度スコアリングおよびチャンク分割エンジン
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using AiMacroSystem.Domain;

namespace AiMacroSystem.Application;

public static class TokenEstimator
{
    private const int RawEventBaseOverheadTokens = 24;
    private const int CandidateBaseOverheadTokens = 28;
    private const int LocatorBaseOverheadTokens = 10;

    public static int EstimateTokens(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        int asciiCount = 0;
        int nonAsciiCount = 0;
        foreach (char c in text)
        {
            if (c <= 0x7F)
            {
                asciiCount++;
            }
            else
            {
                nonAsciiCount++;
            }
        }

        // マルチバイト文字は1文字あたり約0.7トークン、ASCII文字は約4文字で1トークンとして算出
        return (int)Math.Ceiling((asciiCount / 4.0) + (nonAsciiCount * 0.7));
    }

    public static int EstimateTokens(SemanticLocator? locator)
    {
        if (locator is null)
        {
            return 2;
        }

        int tokens = LocatorBaseOverheadTokens
            + EstimateTokens(locator.Role)
            + EstimateTokens(locator.AccessibleName)
            + EstimateTokens(locator.Label)
            + EstimateTokens(locator.StableId)
            + EstimateTokens(locator.CssSelector);

        if (locator.CoordinateX.HasValue || locator.CoordinateY.HasValue)
        {
            tokens += 4;
        }

        return tokens;
    }

    public static int EstimateTokens(RawRecordedEvent? ev)
    {
        if (ev is null)
        {
            return 0;
        }

        return RawEventBaseOverheadTokens
            + EstimateTokens(ev.EventId)
            + EstimateTokens(ev.ApplicationKind)
            + EstimateTokens(ev.EventKind)
            + EstimateTokens(ev.TargetRole)
            + EstimateTokens(ev.TargetName)
            + EstimateTokens(ev.TargetLabel)
            + EstimateTokens(ev.TargetStableId)
            + EstimateTokens(ev.UrlOrWorkbookPath)
            + EstimateTokens(ev.SheetName)
            + EstimateTokens(ev.RangeAddress)
            + EstimateTokens(ev.CapturedValue)
            + EstimateTokens(ev.LogSource)
            + EstimateTokens(ev.CssSelector)
            + EstimateTokens(ev.WindowOrProcessName);
    }

    public static int EstimateTokens(NormalizedActionCandidate? candidate)
    {
        if (candidate is null)
        {
            return 0;
        }

        int rawIdTokens = candidate.SourceRawEventIds?.Sum(EstimateTokens) ?? 0;
        return CandidateBaseOverheadTokens
            + EstimateTokens(candidate.CandidateId)
            + EstimateTokens(candidate.SuggestedActionType)
            + rawIdTokens
            + EstimateTokens(candidate.Locator)
            + EstimateTokens(candidate.TargetUrlOrWorkbook)
            + EstimateTokens(candidate.SheetName)
            + EstimateTokens(candidate.RangeAddress)
            + EstimateTokens(candidate.SanitizedValue)
            + EstimateTokens(candidate.AmbiguityReason);
    }

    public static int EstimateTotalTokens(IEnumerable<RawRecordedEvent>? events) =>
        events?.Sum(EstimateTokens) ?? 0;

    public static int EstimateTotalTokens(IEnumerable<NormalizedActionCandidate>? candidates) =>
        candidates?.Sum(EstimateTokens) ?? 0;
}

public static class LogFilterService
{
    private static readonly HashSet<string> _nonInteractiveRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "window", "pane", "client", "document", "body", "background", "desktop", "whitespace", "empty", "none", "group"
    };

    private static readonly string[] _confirmButtonKeywords =
    {
        "送信", "確定", "実行", "保存", "登録", "検索", "ログイン", "完了", "承認", "次へ",
        "submit", "confirm", "save", "search", "login", "apply", "execute", "ok", "send"
    };

    public static (IReadOnlyList<RawRecordedEvent> CleanedEvents, int FilteredNoiseCount) FilterNoiseRawEvents(
        IReadOnlyList<RawRecordedEvent> rawEvents,
        TimeSpan? debounceWindow = null)
    {
        if (rawEvents.Count == 0)
        {
            return (Array.Empty<RawRecordedEvent>(), 0);
        }

        TimeSpan window = debounceWindow ?? TimeSpan.FromSeconds(5);
        var ordered = rawEvents
            .Select((ev, idx) => (Event: ev, Index: idx))
            .OrderBy(x => x.Event.Timestamp)
            .ThenBy(x => x.Index)
            .Select(x => x.Event)
            .ToList();

        var cleaned = new List<RawRecordedEvent>(ordered.Count);
        int filteredCount = 0;
        string? lastNavigatedUrl = null;

        for (int i = 0; i < ordered.Count; i++)
        {
            var current = ordered[i];

            if (MultiLogExtractionEngine.IsNoiseOrContextOnlyEvent(current))
            {
                // Copy系やFocus系はコンテキスト補完に使われるため保持しつつ、純粋ノイズイベントは除外カウントする
                if (IsPureTelemetryOrMovementNoise(current.EventKind))
                {
                    filteredCount++;
                    continue;
                }
                cleaned.Add(current);
                continue;
            }

            // 1. 意味のない空振りクリック（非対話背景ロール、または識別子・座標ともに欠落した後続操作直前の空クリック）を間引く
            if (IsClickKind(current.EventKind) && IsMeaninglessStrayClick(current, ordered, i, window))
            {
                filteredCount++;
                continue;
            }

            // 2. 連続する微小な入力（同一ターゲットへの直後の確定入力で上書きされる途中打鍵や空入力）を間引く
            if (IsInputKind(current.EventKind) && IsSupersededMicroInput(current, ordered, i, window))
            {
                filteredCount++;
                continue;
            }

            // 3. 重複コンテキスト遷移（操作を挟まない同一URLへの連続ナビゲーション）を間引く
            if (IsNavigateKind(current.EventKind))
            {
                string normUrl = NormalizeUrlKey(current.UrlOrWorkbookPath);
                if (!string.IsNullOrEmpty(normUrl) &&
                    string.Equals(lastNavigatedUrl, normUrl, StringComparison.OrdinalIgnoreCase))
                {
                    filteredCount++;
                    continue;
                }
                lastNavigatedUrl = normUrl;
            }
            else if (!MultiLogExtractionEngine.IsNoiseOrContextOnlyEvent(current))
            {
                lastNavigatedUrl = null;
            }

            cleaned.Add(current);
        }

        return (cleaned, filteredCount);
    }

    public static ScoredActionCandidate ScoreCandidate(NormalizedActionCandidate candidate, int originalIndex = 0)
    {
        int tokens = TokenEstimator.EstimateTokens(candidate);
        string action = candidate.SuggestedActionType ?? string.Empty;

        // 曖昧な候補は仕様6.3/7.2/18.2により勝手に除外せず最優先で保持して検証器へ渡す
        if (candidate.IsAmbiguous)
        {
            return new ScoredActionCandidate(candidate, originalIndex, IntentPriorityKind.Essential, 1000.0, tokens, true);
        }

        IntentPriorityKind priority = IntentPriorityKind.Medium;
        double score = 50.0;
        bool isEssential = false;

        if (string.Equals(action, IrConstants.ActionChromeSubmit, StringComparison.OrdinalIgnoreCase))
        {
            priority = IntentPriorityKind.Essential;
            score = 100.0;
            isEssential = true;
        }
        else if (string.Equals(action, IrConstants.ActionExcelWriteRange, StringComparison.OrdinalIgnoreCase))
        {
            priority = IntentPriorityKind.Essential;
            score = !string.IsNullOrWhiteSpace(candidate.SanitizedValue) ? 96.0 : 82.0;
            isEssential = true;
        }
        else if (string.Equals(action, IrConstants.ActionChromeNavigate, StringComparison.OrdinalIgnoreCase))
        {
            priority = IntentPriorityKind.Essential;
            score = !string.IsNullOrWhiteSpace(candidate.TargetUrlOrWorkbook) ? 95.0 : 75.0;
            isEssential = true;
        }
        else if (string.Equals(action, IrConstants.ActionChromeFill, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(action, IrConstants.ActionCustomAppFill, StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(candidate.SanitizedValue))
            {
                priority = IntentPriorityKind.Essential;
                score = 92.0;
                isEssential = true;
            }
            else
            {
                priority = IntentPriorityKind.Low;
                score = 35.0;
            }
        }
        else if (string.Equals(action, IrConstants.ActionExcelReadRange, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(action, IrConstants.ActionCustomAppReadText, StringComparison.OrdinalIgnoreCase))
        {
            priority = IntentPriorityKind.High;
            score = 86.0;
        }
        else if (string.Equals(action, IrConstants.ActionChromeClick, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(action, IrConstants.ActionCustomAppClick, StringComparison.OrdinalIgnoreCase))
        {
            if (IsConfirmOrCommitButton(candidate.Locator))
            {
                priority = IntentPriorityKind.Essential;
                score = 94.0;
                isEssential = true;
            }
            else if (candidate.Locator is not null && candidate.Locator.HasSemanticTarget())
            {
                priority = IntentPriorityKind.High;
                score = 78.0;
            }
            else
            {
                priority = IntentPriorityKind.Low;
                score = 40.0;
            }
        }

        if (candidate.Locator is not null && candidate.Locator.HasSemanticTarget())
        {
            score += 4.0;
        }

        return new ScoredActionCandidate(candidate, originalIndex, priority, score, tokens, isEssential);
    }

    public static IReadOnlyList<NormalizedActionCandidate> FilterCandidatesByTokenBudget(
        IReadOnlyList<NormalizedActionCandidate> candidates,
        int maxTokenBudget,
        out int filteredOutCount,
        IntentPriorityKind minimumPriority = IntentPriorityKind.Low)
    {
        filteredOutCount = 0;
        if (candidates.Count == 0)
        {
            return Array.Empty<NormalizedActionCandidate>();
        }

        int effectiveBudget = Math.Max(200, maxTokenBudget);
        var deduplicated = DeduplicateRedundantCandidates(candidates);
        int dedupRemoved = candidates.Count - deduplicated.Count;

        var scored = deduplicated
            .Select((c, idx) => ScoreCandidate(c, idx))
            .Where(s => s.Candidate.IsAmbiguous || s.PriorityKind >= minimumPriority)
            .ToList();

        int priorityRemoved = deduplicated.Count - scored.Count;
        int totalTokens = scored.Sum(s => s.EstimatedTokens);

        if (totalTokens <= effectiveBudget)
        {
            filteredOutCount = dedupRemoved + priorityRemoved;
            return scored.OrderBy(s => s.OriginalIndex).Select(s => s.Candidate).ToList();
        }

        // トークン上限超過時は重要度スコア降順で選抜し、最後に元の時系列順序へ復元する
        var prioritized = scored
            .OrderByDescending(s => s.Candidate.IsAmbiguous)
            .ThenByDescending(s => s.IsEssentialIntent)
            .ThenByDescending(s => s.PriorityKind)
            .ThenByDescending(s => s.ImportanceScore)
            .ThenBy(s => s.OriginalIndex)
            .ToList();

        var selected = new List<ScoredActionCandidate>();
        int accumulatedTokens = 0;

        foreach (var item in prioritized)
        {
            if (selected.Count == 0 || accumulatedTokens + item.EstimatedTokens <= effectiveBudget || item.Candidate.IsAmbiguous)
            {
                selected.Add(item);
                accumulatedTokens += item.EstimatedTokens;
            }
        }

        filteredOutCount = candidates.Count - selected.Count;
        return selected
            .OrderBy(s => s.OriginalIndex)
            .Select(s => s.Candidate)
            .ToList();
    }

    private static List<NormalizedActionCandidate> DeduplicateRedundantCandidates(
        IReadOnlyList<NormalizedActionCandidate> candidates)
    {
        var result = new List<NormalizedActionCandidate>(candidates.Count);
        for (int i = 0; i < candidates.Count; i++)
        {
            var current = candidates[i];
            if (result.Count > 0)
            {
                var prev = result[^1];
                // 連続する同一URLへのNavigate候補を1つに圧縮
                if (string.Equals(prev.SuggestedActionType, IrConstants.ActionChromeNavigate, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(current.SuggestedActionType, IrConstants.ActionChromeNavigate, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(NormalizeUrlKey(prev.TargetUrlOrWorkbook), NormalizeUrlKey(current.TargetUrlOrWorkbook), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }
            result.Add(current);
        }
        return result;
    }

    private static bool IsConfirmOrCommitButton(SemanticLocator? locator)
    {
        if (locator is null)
        {
            return false;
        }

        bool isButtonRole = string.Equals(locator.Role, "button", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(locator.Role, "submit", StringComparison.OrdinalIgnoreCase);

        string combined = $"{locator.AccessibleName} {locator.Label} {locator.StableId}";
        bool hasConfirmWord = _confirmButtonKeywords.Any(k => combined.Contains(k, StringComparison.OrdinalIgnoreCase));

        return (isButtonRole && (!string.IsNullOrWhiteSpace(locator.AccessibleName) || !string.IsNullOrWhiteSpace(locator.Label)))
               || hasConfirmWord;
    }

    private static bool IsPureTelemetryOrMovementNoise(string? eventKind)
    {
        if (string.IsNullOrWhiteSpace(eventKind))
        {
            return true;
        }
        string k = eventKind.Trim();
        return k.Equals("MouseMove", StringComparison.OrdinalIgnoreCase) ||
               k.Equals("Hover", StringComparison.OrdinalIgnoreCase) ||
               k.Equals("MouseOver", StringComparison.OrdinalIgnoreCase) ||
               k.Equals("MouseEnter", StringComparison.OrdinalIgnoreCase) ||
               k.Equals("MouseLeave", StringComparison.OrdinalIgnoreCase) ||
               k.Equals("Scroll", StringComparison.OrdinalIgnoreCase) ||
               k.Equals("Heartbeat", StringComparison.OrdinalIgnoreCase) ||
               k.Equals("TelemetryPing", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsClickKind(string? eventKind)
    {
        if (string.IsNullOrWhiteSpace(eventKind))
        {
            return false;
        }
        string k = eventKind.Trim();
        return k.Equals("Click", StringComparison.OrdinalIgnoreCase) ||
               k.Equals("LeftClick", StringComparison.OrdinalIgnoreCase) ||
               k.Equals("MouseClick", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInputKind(string? eventKind)
    {
        if (string.IsNullOrWhiteSpace(eventKind))
        {
            return false;
        }
        string k = eventKind.Trim();
        return k.Equals("Input", StringComparison.OrdinalIgnoreCase) ||
               k.Equals("Type", StringComparison.OrdinalIgnoreCase) ||
               k.Equals("KeyPress", StringComparison.OrdinalIgnoreCase) ||
               k.Equals("Fill", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNavigateKind(string? eventKind)
    {
        if (string.IsNullOrWhiteSpace(eventKind))
        {
            return false;
        }
        string k = eventKind.Trim();
        return k.Equals("Navigate", StringComparison.OrdinalIgnoreCase) ||
               k.Equals("OpenUrl", StringComparison.OrdinalIgnoreCase) ||
               k.Equals("PageLoad", StringComparison.OrdinalIgnoreCase) ||
               k.Equals("GoTo", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMeaninglessStrayClick(
        RawRecordedEvent current,
        List<RawRecordedEvent> ordered,
        int index,
        TimeSpan window)
    {
        bool hasSemantic = MultiLogExtractionEngine.HasAnySemanticIdentity(current);
        if (!string.IsNullOrWhiteSpace(current.TargetRole) &&
            _nonInteractiveRoles.Contains(current.TargetRole.Trim()) &&
            !hasSemantic)
        {
            return true;
        }

        if (hasSemantic)
        {
            return false;
        }

        // 意味的識別子も座標も持たず、直後に同一アプリで意味的操作が存在する場合は空振りクリックと判定
        if (!current.CoordinateX.HasValue && !current.CoordinateY.HasValue && index + 1 < ordered.Count)
        {
            var next = ordered[index + 1];
            if ((next.Timestamp - current.Timestamp).Duration() <= window &&
                MultiLogExtractionEngine.HasAnySemanticIdentity(next))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSupersededMicroInput(
        RawRecordedEvent current,
        List<RawRecordedEvent> ordered,
        int index,
        TimeSpan window)
    {
        string currentVal = current.CapturedValue ?? string.Empty;
        if (index + 1 >= ordered.Count)
        {
            return false;
        }

        var next = ordered[index + 1];
        if (!IsInputKind(next.EventKind) || (next.Timestamp - current.Timestamp).Duration() > window)
        {
            return false;
        }

        bool sameTarget = string.Equals(current.TargetStableId, next.TargetStableId, StringComparison.OrdinalIgnoreCase) &&
                          string.Equals(current.TargetName, next.TargetName, StringComparison.OrdinalIgnoreCase) &&
                          string.Equals(current.TargetLabel, next.TargetLabel, StringComparison.OrdinalIgnoreCase);

        if (!sameTarget)
        {
            return false;
        }

        string nextVal = next.CapturedValue ?? string.Empty;
        return string.IsNullOrEmpty(currentVal) ||
               (nextVal.Length > currentVal.Length && nextVal.StartsWith(currentVal, StringComparison.Ordinal));
    }

    private static string NormalizeUrlKey(string? rawUrl) =>
        string.IsNullOrWhiteSpace(rawUrl) ? string.Empty : rawUrl.Trim().TrimEnd('/');
}

public static class LogChunker
{
    public static IReadOnlyList<LogChunk> BuildChunks(
        IReadOnlyList<RawRecordedEvent> rawEvents,
        IReadOnlyList<NormalizedActionCandidate> candidates,
        LogChunkingOptions? options = null,
        int totalFilteredOutEvents = 0)
    {
        var opts = options ?? new LogChunkingOptions();
        int maxTokens = Math.Max(200, opts.MaxTokensPerChunk);

        var rawById = rawEvents
            .Where(e => !string.IsNullOrWhiteSpace(e.EventId))
            .GroupBy(e => e.EventId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        if (candidates.Count == 0)
        {
            if (rawEvents.Count == 0)
            {
                return Array.Empty<LogChunk>();
            }

            var orderedRaw = rawEvents.OrderBy(e => e.Timestamp).ToList();
            return new List<LogChunk>
            {
                new(
                    ChunkId: "chunk-0001",
                    SequenceIndex: 0,
                    EstimatedTokens: TokenEstimator.EstimateTotalTokens(orderedRaw),
                    AppCategory: InferAppCategoryFromRawEvent(orderedRaw[0]),
                    StartTime: orderedRaw[0].Timestamp,
                    EndTime: orderedRaw[^1].Timestamp,
                    RawEventIds: orderedRaw.Select(e => e.EventId).ToList(),
                    CandidateIds: Array.Empty<string>(),
                    Candidates: Array.Empty<NormalizedActionCandidate>(),
                    FilteredOutEventCount: totalFilteredOutEvents)
            };
        }

        var chunks = new List<LogChunk>();
        var currentCandidates = new List<NormalizedActionCandidate>();
        var currentRawIds = new List<string>();
        int currentTokens = 0;
        string currentApp = string.Empty;
        DateTimeOffset chunkStart = DateTimeOffset.UtcNow;
        DateTimeOffset chunkEnd = chunkStart;

        for (int i = 0; i < candidates.Count; i++)
        {
            var cand = candidates[i];
            int candTokens = TokenEstimator.EstimateTokens(cand);
            string candApp = ResolveCandidateAppCategory(cand, rawById);
            var (candStart, candEnd) = ResolveCandidateTimeRange(cand, rawById, chunkEnd);

            bool shouldSplit = false;
            if (currentCandidates.Count > 0)
            {
                bool exceedsTokenLimit = currentTokens + candTokens > maxTokens;
                bool crossesAppBoundary = opts.SplitOnApplicationBoundary &&
                                          !string.Equals(currentApp, candApp, StringComparison.OrdinalIgnoreCase);
                bool exceedsIdleWindow = (candStart - chunkEnd).Duration() > opts.IdleBoundaryWindow;

                shouldSplit = exceedsTokenLimit || crossesAppBoundary || exceedsIdleWindow;
            }

            if (shouldSplit)
            {
                int seqIdx = chunks.Count;
                chunks.Add(new LogChunk(
                    ChunkId: $"chunk-{seqIdx + 1:D4}",
                    SequenceIndex: seqIdx,
                    EstimatedTokens: currentTokens,
                    AppCategory: currentApp,
                    StartTime: chunkStart,
                    EndTime: chunkEnd,
                    RawEventIds: currentRawIds.Distinct(StringComparer.Ordinal).ToList(),
                    CandidateIds: currentCandidates.Select(c => c.CandidateId).ToList(),
                    Candidates: currentCandidates.ToList(),
                    FilteredOutEventCount: seqIdx == 0 ? totalFilteredOutEvents : 0));

                currentCandidates.Clear();
                currentRawIds.Clear();
                currentTokens = 0;
            }

            if (currentCandidates.Count == 0)
            {
                currentApp = candApp;
                chunkStart = candStart;
                chunkEnd = candEnd;
            }
            else
            {
                if (candStart < chunkStart) chunkStart = candStart;
                if (candEnd > chunkEnd) chunkEnd = candEnd;
                if (!string.Equals(currentApp, candApp, StringComparison.OrdinalIgnoreCase))
                {
                    currentApp = "Mixed";
                }
            }

            currentCandidates.Add(cand);
            if (cand.SourceRawEventIds is { Count: > 0 })
            {
                currentRawIds.AddRange(cand.SourceRawEventIds);
            }
            currentTokens += candTokens;
        }

        if (currentCandidates.Count > 0)
        {
            int seqIdx = chunks.Count;
            chunks.Add(new LogChunk(
                ChunkId: $"chunk-{seqIdx + 1:D4}",
                SequenceIndex: seqIdx,
                EstimatedTokens: currentTokens,
                AppCategory: currentApp,
                StartTime: chunkStart,
                EndTime: chunkEnd,
                RawEventIds: currentRawIds.Distinct(StringComparer.Ordinal).ToList(),
                CandidateIds: currentCandidates.Select(c => c.CandidateId).ToList(),
                Candidates: currentCandidates.ToList(),
                FilteredOutEventCount: seqIdx == 0 ? totalFilteredOutEvents : 0));
        }

        return chunks;
    }

    private static string ResolveCandidateAppCategory(
        NormalizedActionCandidate candidate,
        IReadOnlyDictionary<string, RawRecordedEvent> rawById)
    {
        if (!string.IsNullOrWhiteSpace(candidate.SuggestedActionType))
        {
            int dot = candidate.SuggestedActionType.IndexOf('.');
            if (dot > 0)
            {
                return candidate.SuggestedActionType[..dot];
            }
        }

        if (candidate.SourceRawEventIds is { Count: > 0 })
        {
            foreach (string rawId in candidate.SourceRawEventIds)
            {
                if (rawById.TryGetValue(rawId, out var rawEv))
                {
                    return InferAppCategoryFromRawEvent(rawEv);
                }
            }
        }

        return "CustomApp";
    }

    private static string InferAppCategoryFromRawEvent(RawRecordedEvent ev)
    {
        string kind = ev.ApplicationKind ?? string.Empty;
        if (kind.Contains("Chrome", StringComparison.OrdinalIgnoreCase) ||
            kind.Contains("Browser", StringComparison.OrdinalIgnoreCase) ||
            kind.Contains("Web", StringComparison.OrdinalIgnoreCase))
        {
            return "Chrome";
        }
        if (kind.Contains("Excel", StringComparison.OrdinalIgnoreCase))
        {
            return "Excel";
        }
        return "CustomApp";
    }

    private static (DateTimeOffset Start, DateTimeOffset End) ResolveCandidateTimeRange(
        NormalizedActionCandidate candidate,
        IReadOnlyDictionary<string, RawRecordedEvent> rawById,
        DateTimeOffset fallbackTime)
    {
        DateTimeOffset? minTs = null;
        DateTimeOffset? maxTs = null;

        if (candidate.SourceRawEventIds is { Count: > 0 })
        {
            foreach (string id in candidate.SourceRawEventIds)
            {
                if (rawById.TryGetValue(id, out var ev))
                {
                    if (!minTs.HasValue || ev.Timestamp < minTs.Value) minTs = ev.Timestamp;
                    if (!maxTs.HasValue || ev.Timestamp > maxTs.Value) maxTs = ev.Timestamp;
                }
            }
        }

        var start = minTs ?? fallbackTime;
        var end = maxTs ?? start;
        return (start, end);
    }
}

public static class AiAuditMetadataFormatter
{
    private static readonly Regex _metaRegex = new(
        @"\[Chunk=(?<chunk>[^,\]]+),\s*Tokens=(?<tokens>\d+),\s*FilteredOut=(?<filtered>\d+)\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string FormatStageSummaryWithMetadata(
        string baseStageSummary,
        string? chunkId,
        int estimatedTokens,
        int filteredOutEventCount)
    {
        string safeChunk = string.IsNullOrWhiteSpace(chunkId) ? "chunk-all" : chunkId;
        return $"{baseStageSummary} [Chunk={safeChunk}, Tokens={Math.Max(0, estimatedTokens)}, FilteredOut={Math.Max(0, filteredOutEventCount)}]";
    }

    public static (string? ChunkId, int EstimatedTokens, int FilteredOutEventCount) ParseMetadata(string? stageSummary)
    {
        if (string.IsNullOrWhiteSpace(stageSummary))
        {
            return (null, 0, 0);
        }

        var match = _metaRegex.Match(stageSummary);
        if (!match.Success)
        {
            return (null, 0, 0);
        }

        string chunkId = match.Groups["chunk"].Value;
        int.TryParse(match.Groups["tokens"].Value, out int tokens);
        int.TryParse(match.Groups["filtered"].Value, out int filtered);
        return (chunkId, tokens, filtered);
    }

    public static string? TryGetChunkId(this AiAuditRecord record) =>
        ParseMetadata(record.ValidationStageSummary).ChunkId;

    public static int TryGetEstimatedTokens(this AiAuditRecord record) =>
        ParseMetadata(record.ValidationStageSummary).EstimatedTokens;

    public static int TryGetFilteredOutEventCount(this AiAuditRecord record) =>
        ParseMetadata(record.ValidationStageSummary).FilteredOutEventCount;
}
