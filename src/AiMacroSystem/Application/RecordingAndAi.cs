// Module: @role: 操作記録・機密情報除外・意味的正規化・ローカル優先AIオーケストレーター・AI監査
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AiMacroSystem.Domain;
using AiMacroSystem.Infrastructure;

namespace AiMacroSystem.Application;

public static class SensitiveDataProtector
{
    private static readonly Regex _secretQueryRegex = new(
        @"([?&](?:password|passwd|pwd|token|access_token|api_key|secret|auth)=)([^&#\s]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex _bearerRegex = new(
        @"(Bearer\s+)[A-Za-z0-9\-._~+/]+=*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public const string RedactedPlaceholder = "[REDACTED]";

    public static bool IsSensitiveField(string? fieldType, string? fieldName, string? label)
    {
        if (string.Equals(fieldType, "password", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        string combined = $"{fieldName} {label}";
        return combined.Contains("password", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("token", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("パスワード", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("暗証番号", StringComparison.OrdinalIgnoreCase);
    }

    public static string SanitizeText(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }
        string masked = _secretQueryRegex.Replace(input, $"$1{RedactedPlaceholder}");
        masked = _bearerRegex.Replace(masked, $"$1{RedactedPlaceholder}");
        return masked;
    }
}

public sealed record RawRecordedEvent(
    string EventId,
    string ApplicationKind,
    string EventKind,
    string? TargetRole,
    string? TargetName,
    string? TargetLabel,
    string? TargetStableId,
    string? UrlOrWorkbookPath,
    string? SheetName,
    string? RangeAddress,
    string? CapturedValue,
    bool IsSensitive,
    DateTimeOffset Timestamp,
    string LogSource = LogSourceConstants.PrimaryHook,
    string? CssSelector = null,
    int? CoordinateX = null,
    int? CoordinateY = null,
    double SourceConfidence = 0.85,
    string? WindowOrProcessName = null);

public sealed record NormalizedActionCandidate(
    string CandidateId,
    string SuggestedActionType,
    IReadOnlyList<string> SourceRawEventIds,
    SemanticLocator? Locator,
    string? TargetUrlOrWorkbook,
    string? SheetName,
    string? RangeAddress,
    string? SanitizedValue,
    bool IsSensitive,
    bool IsAmbiguous,
    string? AmbiguityReason);

public interface ILiveOperationLogCollector
{
    void Start(RecordingService recordingService, Action<RawRecordedEvent>? onEventCaptured = null);
    void StopAndFlush();
}

public sealed class RecordingService
{
    private readonly SqliteRepository? _repository;
    private readonly RecentOperationLogStore? _logStore;
    private readonly List<RawRecordedEvent> _events = new();
    private DateTimeOffset _monotonicClock = DateTimeOffset.UtcNow;
    public bool IsRecording { get; private set; }

    public RecordingService(SqliteRepository? repository = null, RecentOperationLogStore? logStore = null)
    {
        _repository = repository;
        _logStore = logStore;
    }

    public void StartRecording()
    {
        _events.Clear();
        _monotonicClock = DateTimeOffset.UtcNow;
        IsRecording = true;
        _logStore?.BeginSession();
    }

    public void StopRecording()
    {
        IsRecording = false;
    }

    // 連続した操作グループの記録時にタイムスタンプの逆転・交錯を防ぐため論理クロックを進める
    public DateTimeOffset AdvanceLogicalClock(TimeSpan step)
    {
        var now = DateTimeOffset.UtcNow;
        if (now > _monotonicClock)
        {
            _monotonicClock = now;
        }
        _monotonicClock = _monotonicClock.Add(step);
        return _monotonicClock;
    }

    private DateTimeOffset ResolveMonotonicTimestamp(DateTimeOffset? overrideTimestamp)
    {
        if (overrideTimestamp.HasValue)
        {
            if (overrideTimestamp.Value > _monotonicClock)
            {
                _monotonicClock = overrideTimestamp.Value;
            }
            return overrideTimestamp.Value;
        }

        var now = DateTimeOffset.UtcNow;
        if (now <= _monotonicClock)
        {
            _monotonicClock = _monotonicClock.AddMilliseconds(5);
            return _monotonicClock;
        }

        _monotonicClock = now;
        return now;
    }

    public RawRecordedEvent RecordEvent(
        string applicationKind,
        string eventKind,
        string? fieldType,
        string? targetRole,
        string? targetName,
        string? targetLabel,
        string? targetStableId,
        string? urlOrWorkbookPath,
        string? sheetName,
        string? rangeAddress,
        string? rawInputValue,
        string logSource = LogSourceConstants.PrimaryHook,
        string? cssSelector = null,
        int? coordinateX = null,
        int? coordinateY = null,
        double sourceConfidence = 0.85,
        string? windowOrProcessName = null,
        DateTimeOffset? timestampOverride = null)
    {
        bool sensitive = SensitiveDataProtector.IsSensitiveField(fieldType, targetName, targetLabel);
        // パスワードや認証情報は取得段階で生値を破棄し保存しない（仕様6.1 / 15.1）
        string? safeValue = sensitive
            ? SensitiveDataProtector.RedactedPlaceholder
            : SensitiveDataProtector.SanitizeText(rawInputValue);

        string? safeUrlOrPath = SensitiveDataProtector.SanitizeText(urlOrWorkbookPath);

        var resolvedTimestamp = ResolveMonotonicTimestamp(timestampOverride);

        var ev = new RawRecordedEvent(
            EventId: $"evt-{Guid.NewGuid():N}",
            ApplicationKind: applicationKind,
            EventKind: eventKind,
            TargetRole: targetRole,
            TargetName: targetName,
            TargetLabel: targetLabel,
            TargetStableId: targetStableId,
            UrlOrWorkbookPath: safeUrlOrPath,
            SheetName: sheetName,
            RangeAddress: rangeAddress,
            CapturedValue: safeValue,
            IsSensitive: sensitive,
            Timestamp: resolvedTimestamp,
            LogSource: string.IsNullOrWhiteSpace(logSource) ? LogSourceConstants.PrimaryHook : logSource,
            CssSelector: cssSelector,
            CoordinateX: coordinateX,
            CoordinateY: coordinateY,
            SourceConfidence: sourceConfidence,
            WindowOrProcessName: windowOrProcessName);

        if (IsRecording)
        {
            _events.Add(ev);
            _repository?.InsertRawRecordedEvent(ev);
            _logStore?.AppendRawEvent(ev);
        }
        return ev;
    }

    public IReadOnlyList<RawRecordedEvent> RecordMultiSourceBatch(IEnumerable<RawRecordedEvent> batchEvents)
    {
        var recorded = new List<RawRecordedEvent>();
        foreach (var raw in batchEvents)
        {
            bool sensitive = raw.IsSensitive || SensitiveDataProtector.IsSensitiveField(null, raw.TargetName, raw.TargetLabel);
            var safeEv = raw with
            {
                EventId = string.IsNullOrWhiteSpace(raw.EventId) ? $"evt-{Guid.NewGuid():N}" : raw.EventId,
                UrlOrWorkbookPath = SensitiveDataProtector.SanitizeText(raw.UrlOrWorkbookPath),
                CapturedValue = sensitive
                    ? SensitiveDataProtector.RedactedPlaceholder
                    : SensitiveDataProtector.SanitizeText(raw.CapturedValue),
                IsSensitive = sensitive
            };

            if (IsRecording)
            {
                _events.Add(safeEv);
                _repository?.InsertRawRecordedEvent(safeEv);
                _logStore?.AppendRawEvent(safeEv);
            }
            recorded.Add(safeEv);
        }
        return recorded;
    }

    public IReadOnlyList<RawRecordedEvent> GetRecordedEvents() => _events.ToList();
}

public sealed class NormalizationService
{
    private readonly SqliteRepository? _repository;
    private readonly MultiLogExtractionEngine _extractionEngine;
    private readonly LogChunkingOptions _chunkingOptions;
    private readonly RecentOperationLogStore? _logStore;

    public IReadOnlyList<LogChunk> LastGeneratedChunks { get; private set; } = Array.Empty<LogChunk>();
    public LogFilterSummary? LastFilterSummary { get; private set; }

    public NormalizationService(
        SqliteRepository? repository = null,
        MultiLogExtractionEngine? extractionEngine = null,
        LogChunkingOptions? chunkingOptions = null,
        RecentOperationLogStore? logStore = null)
    {
        _repository = repository;
        _extractionEngine = extractionEngine ?? new MultiLogExtractionEngine();
        _chunkingOptions = chunkingOptions ?? new LogChunkingOptions();
        _logStore = logStore;
    }

    public IReadOnlyList<NormalizedActionCandidate> Normalize(IReadOnlyList<RawRecordedEvent> rawEvents)
    {
        var (candidates, _) = NormalizeAndChunk(rawEvents, _chunkingOptions);
        return candidates;
    }

    public (IReadOnlyList<NormalizedActionCandidate> Candidates, IReadOnlyList<LogChunk> Chunks) NormalizeAndChunk(
        IReadOnlyList<RawRecordedEvent> rawEvents,
        LogChunkingOptions? options = null)
    {
        var effectiveOptions = options ?? _chunkingOptions;
        var recentRawEvents = _logStore is not null
            ? _logStore.FilterRecentEvents(rawEvents)
            : rawEvents;

        var (candidates, summary) = _extractionEngine.ExtractCandidatesWithSummary(recentRawEvents);
        LastFilterSummary = summary;

        foreach (var candidate in candidates)
        {
            _repository?.InsertNormalizedCandidate(candidate);
        }

        var chunks = LogChunker.BuildChunks(
            recentRawEvents,
            candidates,
            effectiveOptions,
            summary.FilteredOutEventCount);
        LastGeneratedChunks = chunks;

        if (chunks.Count > 0)
        {
            _repository?.SaveLogChunks(chunks);
        }

        _logStore?.WriteSessionSummary(recentRawEvents, candidates);
        return (candidates, chunks);
    }
}

public interface IAiProvider
{
    string ProviderName { get; }
    string ModelId { get; }
    bool IsExternal { get; }
    bool IsAvailable { get; }

    Task<string> GenerateWorkflowIrJsonAsync(
        IReadOnlyList<NormalizedActionCandidate> candidates,
        PolicyDeclaration policy,
        IReadOnlyList<ValidationIssue>? previousValidationIssues,
        CancellationToken ct);
}

public sealed class AiGenerationOutcome
{
    public required bool IsAccepted { get; init; }
    public WorkflowDefinition? Definition { get; init; }
    public string? RawJson { get; init; }
    public required ValidationResult Validation { get; init; }
    public required string ErrorReason { get; init; }
    public required int RepairAttemptsUsed { get; init; }
    public int TotalChunksCount { get; init; } = 1;
    public int EstimatedTokens { get; init; }
    public int FilteredOutEventCount { get; init; }
    public string? FailedChunkId { get; init; }
    public bool UsedFallbackGeneration { get; init; }
    public IReadOnlyList<string> ProcessedChunkIds { get; init; } = Array.Empty<string>();
}

public sealed class AiOrchestrator
{
    private readonly IAiProvider _localProvider;
    private readonly IAiProvider? _externalProvider;
    private readonly IrValidator _validator;
    private readonly SqliteRepository _repository;
    private readonly int _maxRepairAttempts;
    private readonly LogChunkingOptions _chunkingOptions;
    private readonly RecentOperationLogStore? _logStore;

    public AiOrchestrator(
        IAiProvider localProvider,
        IAiProvider? externalProvider,
        IrValidator validator,
        SqliteRepository repository,
        int maxRepairAttempts = 2,
        LogChunkingOptions? chunkingOptions = null,
        RecentOperationLogStore? logStore = null)
    {
        _localProvider = localProvider;
        _externalProvider = externalProvider;
        _validator = validator;
        _repository = repository;
        _maxRepairAttempts = maxRepairAttempts;
        _chunkingOptions = chunkingOptions ?? new LogChunkingOptions();
        _logStore = logStore;
    }

    public async Task<AiGenerationOutcome> GenerateAndValidateIrAsync(
        IReadOnlyList<NormalizedActionCandidate> candidates,
        PolicyDeclaration policy,
        bool userApprovedExternalAi,
        CancellationToken ct,
        IReadOnlyList<RawRecordedEvent>? sessionRawEvents = null)
    {
        var scopedRawEvents = ResolveRecentSessionRawEvents(candidates, sessionRawEvents);
        var outcome = await GenerateAndValidateIrCoreAsync(
            candidates,
            policy,
            userApprovedExternalAi,
            scopedRawEvents,
            ct);
        _logStore?.WriteSessionSummary(sessionRawEvents ?? scopedRawEvents, candidates, outcome);
        return outcome;
    }

    private IReadOnlyList<RawRecordedEvent> ResolveRecentSessionRawEvents(
        IReadOnlyList<NormalizedActionCandidate> candidates,
        IReadOnlyList<RawRecordedEvent>? sessionRawEvents)
    {
        if (sessionRawEvents is not null)
        {
            return _logStore is not null
                ? _logStore.FilterRecentEvents(sessionRawEvents)
                : sessionRawEvents;
        }

        // セッションイベント未指定時でも、全履歴混入によるトークン浪費を防ぐため現候補が参照するイベントのみ抽出する
        var allStored = _repository.GetRawRecordedEvents();
        var referencedIds = candidates
            .SelectMany(c => c.SourceRawEventIds)
            .ToHashSet(StringComparer.Ordinal);

        var matched = referencedIds.Count > 0
            ? allStored.Where(e => referencedIds.Contains(e.EventId)).ToList()
            : allStored.ToList();

        return _logStore is not null
            ? _logStore.FilterRecentEvents(matched)
            : matched;
    }

    private async Task<AiGenerationOutcome> GenerateAndValidateIrCoreAsync(
        IReadOnlyList<NormalizedActionCandidate> candidates,
        PolicyDeclaration policy,
        bool userApprovedExternalAi,
        IReadOnlyList<RawRecordedEvent> rawEvents,
        CancellationToken ct)
    {
        // 曖昧な候補が残っている場合はAIが勝手に推測して確定することを禁止（仕様6.3 / 7.2 / 18.2）
        var ambiguous = candidates.FirstOrDefault(c => c.IsAmbiguous);
        if (ambiguous is not null)
        {
            var ambValidation = new ValidationResult();
            ambValidation.AddIssue(
                ValidationStage.TargetAndAllowedScope,
                "AMBIGUOUS_CANDIDATE_UNRESOLVED",
                $"Candidate '{ambiguous.CandidateId}' is ambiguous ({ambiguous.AmbiguityReason}). User disambiguation is required before AI IR generation.");

            RecordAudit(_localProvider.ProviderName, _localProvider.ModelId, "PreCheck:AmbiguousCandidate", false, "AMBIGUOUS_TARGET", 0, false);
            return new AiGenerationOutcome
            {
                IsAccepted = false,
                Validation = ambValidation,
                ErrorReason = ambValidation.Issues[0].Message,
                RepairAttemptsUsed = 0
            };
        }

        // ローカルAI優先。利用不可でも暗黙に外部AIへフォールバックしない（仕様7.1 / 18.2）
        IAiProvider activeProvider;
        bool externalApproved = false;
        if (_localProvider.IsAvailable)
        {
            activeProvider = _localProvider;
        }
        else
        {
            if (!policy.AllowExternalAi || !userApprovedExternalAi || _externalProvider is null || !_externalProvider.IsAvailable)
            {
                var noAiValidation = new ValidationResult();
                noAiValidation.AddIssue(
                    ValidationStage.SideEffectsRiskAndPolicy,
                    "LOCAL_AI_UNAVAILABLE_EXTERNAL_NOT_PERMITTED",
                    "Local AI is unavailable, and implicit fallback to external AI is prohibited without policy permission and explicit user approval.");

                RecordAudit(_localProvider.ProviderName, _localProvider.ModelId, "ProviderSelection", false, "AI_PROVIDER_UNAVAILABLE", 0, false);
                return new AiGenerationOutcome
                {
                    IsAccepted = false,
                    Validation = noAiValidation,
                    ErrorReason = noAiValidation.Issues[0].Message,
                    RepairAttemptsUsed = 0
                };
            }
            activeProvider = _externalProvider;
            externalApproved = true;
        }

        // 外部AI送信時は機密データを再検査・除外する（仕様7.1 / 15.1）
        var safeCandidates = activeProvider.IsExternal
            ? SanitizeCandidatesForExternalAi(candidates)
            : candidates;

        int usedRawCount = safeCandidates.SelectMany(c => c.SourceRawEventIds).Distinct(StringComparer.Ordinal).Count();
        int baseFilteredOutCount = rawEvents.Count > usedRawCount ? rawEvents.Count - usedRawCount : 0;

        var chunks = LogChunker.BuildChunks(rawEvents, safeCandidates, _chunkingOptions, baseFilteredOutCount);
        if (chunks.Count > 0)
        {
            _repository.SaveLogChunks(chunks);
        }

        int totalTokens = TokenEstimator.EstimateTotalTokens(safeCandidates);
        string primaryChunkLabel = chunks.Count == 1 ? chunks[0].ChunkId : $"batch-{chunks.Count}chunks";
        var chunkIds = chunks.Select(c => c.ChunkId).ToList();

        // 長大ログ（上限トークン超過）の場合は最初から重要度フィルタリングまたはチャンク段階生成を適用する
        if (totalTokens > _chunkingOptions.MaxTokensPerChunk)
        {
            var fallbackOutcome = await TryFallbackGenerationAsync(
                activeProvider,
                safeCandidates,
                chunks,
                policy,
                externalApproved,
                baseFilteredOutCount,
                ct);
            if (fallbackOutcome is not null)
            {
                return fallbackOutcome;
            }
        }

        IReadOnlyList<ValidationIssue>? previousIssues = null;
        ValidationResult lastValidation = new();
        string lastJson = string.Empty;

        for (int attempt = 0; attempt <= _maxRepairAttempts; attempt++)
        {
            lastJson = await activeProvider.GenerateWorkflowIrJsonAsync(safeCandidates, policy, previousIssues, ct);

            // 修正再要求後も最初から全8段階を検証する（仕様7.3 / 18.2）
            var (validation, definition) = _validator.ValidateJson(lastJson);
            lastValidation = validation;

            if (validation.IsValid && definition is not null)
            {
                RecordAudit(
                    activeProvider.ProviderName,
                    activeProvider.ModelId,
                    "AllStagesPassed",
                    true,
                    "NONE",
                    attempt,
                    externalApproved,
                    primaryChunkLabel,
                    totalTokens,
                    baseFilteredOutCount);
                return new AiGenerationOutcome
                {
                    IsAccepted = true,
                    Definition = definition,
                    RawJson = lastJson,
                    Validation = validation,
                    ErrorReason = string.Empty,
                    RepairAttemptsUsed = attempt,
                    TotalChunksCount = Math.Max(1, chunks.Count),
                    EstimatedTokens = totalTokens,
                    FilteredOutEventCount = baseFilteredOutCount,
                    ProcessedChunkIds = chunkIds
                };
            }

            previousIssues = validation.Issues;
        }

        // 一括生成が全試行失敗した場合、重要度フィルタリングまたはチャンク単位の段階的生成へフォールバックする
        var postFailureFallback = await TryFallbackGenerationAsync(
            activeProvider,
            safeCandidates,
            chunks,
            policy,
            externalApproved,
            baseFilteredOutCount,
            ct);
        if (postFailureFallback is not null)
        {
            return postFailureFallback;
        }

        string firstErrorCode = lastValidation.Issues.FirstOrDefault()?.Code ?? "VALIDATION_FAILED";
        string stageSummary = string.Join(",", lastValidation.Issues.Select(i => i.Stage.ToString()).Distinct());
        RecordAudit(
            activeProvider.ProviderName,
            activeProvider.ModelId,
            stageSummary,
            false,
            firstErrorCode,
            _maxRepairAttempts,
            externalApproved,
            primaryChunkLabel,
            totalTokens,
            baseFilteredOutCount);

        return new AiGenerationOutcome
        {
            IsAccepted = false,
            RawJson = lastJson,
            Validation = lastValidation,
            ErrorReason = string.Join("; ", lastValidation.Issues.Select(i => $"[{i.Code}] {i.Message}")),
            RepairAttemptsUsed = _maxRepairAttempts,
            TotalChunksCount = Math.Max(1, chunks.Count),
            EstimatedTokens = totalTokens,
            FilteredOutEventCount = baseFilteredOutCount,
            FailedChunkId = chunks.FirstOrDefault()?.ChunkId,
            ProcessedChunkIds = chunkIds
        };
    }

    public async Task<AiGenerationOutcome> RetryChunkGenerationAsync(
        string chunkId,
        PolicyDeclaration policy,
        bool userApprovedExternalAi,
        CancellationToken ct = default)
    {
        var chunk = _repository.GetLogChunkById(chunkId);
        if (chunk is null)
        {
            var notFoundVal = new ValidationResult();
            notFoundVal.AddIssue(
                ValidationStage.TargetAndAllowedScope,
                "CHUNK_NOT_FOUND",
                $"LogChunk '{chunkId}' was not found in repository.");
            return new AiGenerationOutcome
            {
                IsAccepted = false,
                Validation = notFoundVal,
                ErrorReason = notFoundVal.Issues[0].Message,
                RepairAttemptsUsed = 0,
                FailedChunkId = chunkId
            };
        }

        return await GenerateAndValidateIrAsync(chunk.Candidates, policy, userApprovedExternalAi, ct);
    }

    private async Task<AiGenerationOutcome?> TryFallbackGenerationAsync(
        IAiProvider activeProvider,
        IReadOnlyList<NormalizedActionCandidate> safeCandidates,
        IReadOnlyList<LogChunk> chunks,
        PolicyDeclaration policy,
        bool externalApproved,
        int baseFilteredOutCount,
        CancellationToken ct)
    {
        try
        {
            // 1. 必須意図・高優先度フィルタリングによるフォールバック
            var essentialFiltered = LogFilterService.FilterCandidatesByTokenBudget(
                safeCandidates,
                _chunkingOptions.MaxTokensPerChunk,
                out int extraFilteredCount,
                IntentPriorityKind.High);

            if (essentialFiltered.Count > 0 && essentialFiltered.Count < safeCandidates.Count)
            {
                int filteredTokens = TokenEstimator.EstimateTotalTokens(essentialFiltered);
                int totalFiltered = baseFilteredOutCount + extraFilteredCount;
                string json = await activeProvider.GenerateWorkflowIrJsonAsync(essentialFiltered, policy, null, ct);
                var (val, def) = _validator.ValidateJson(json);
                if (val.IsValid && def is not null)
                {
                    RecordAudit(
                        activeProvider.ProviderName,
                        activeProvider.ModelId,
                        "AllStagesPassed:FilteredFallback",
                        true,
                        "NONE",
                        0,
                        externalApproved,
                        "fallback-filtered",
                        filteredTokens,
                        totalFiltered);
                    return new AiGenerationOutcome
                    {
                        IsAccepted = true,
                        Definition = def,
                        RawJson = json,
                        Validation = val,
                        ErrorReason = string.Empty,
                        RepairAttemptsUsed = _maxRepairAttempts,
                        TotalChunksCount = Math.Max(1, chunks.Count),
                        EstimatedTokens = filteredTokens,
                        FilteredOutEventCount = totalFiltered,
                        UsedFallbackGeneration = true,
                        ProcessedChunkIds = chunks.Select(c => c.ChunkId).ToList()
                    };
                }
            }

            // 2. チャンク単位での段階的AI生成と原因特定監査ログ記録
            if (chunks.Count > 1)
            {
                var mergedNodes = new List<WorkflowNode>();
                var mergedOrigins = new List<OriginRule>(policy.AllowedOrigins);
                var processedChunkIds = new List<string>();
                int totalChunkTokens = 0;
                int totalFilteredEvents = baseFilteredOutCount;
                int stepCounter = 1;

                foreach (var chunk in chunks)
                {
                    var chunkCandidates = LogFilterService.FilterCandidatesByTokenBudget(
                        chunk.Candidates,
                        _chunkingOptions.MaxTokensPerChunk,
                        out int chunkFilteredOut);

                    int chunkTokens = TokenEstimator.EstimateTotalTokens(chunkCandidates);
                    totalChunkTokens += chunkTokens;
                    totalFilteredEvents += chunkFilteredOut;

                    IReadOnlyList<ValidationIssue>? chunkPrevIssues = null;
                    ValidationResult chunkVal = new();
                    WorkflowDefinition? chunkDef = null;

                    for (int attempt = 0; attempt <= _maxRepairAttempts; attempt++)
                    {
                        string chunkJson = await activeProvider.GenerateWorkflowIrJsonAsync(
                            chunkCandidates,
                            policy,
                            chunkPrevIssues,
                            ct);
                        (chunkVal, chunkDef) = _validator.ValidateJson(chunkJson);
                        if (chunkVal.IsValid && chunkDef is not null)
                        {
                            RecordAudit(
                                activeProvider.ProviderName,
                                activeProvider.ModelId,
                                "AllStagesPassed:ChunkStaged",
                                true,
                                "NONE",
                                attempt,
                                externalApproved,
                                chunk.ChunkId,
                                chunkTokens,
                                chunk.FilteredOutEventCount + chunkFilteredOut);
                            break;
                        }
                        chunkPrevIssues = chunkVal.Issues;
                    }

                    if (!chunkVal.IsValid || chunkDef is null)
                    {
                        string errCode = chunkVal.Issues.FirstOrDefault()?.Code ?? "CHUNK_VALIDATION_FAILED";
                        string stages = string.Join(",", chunkVal.Issues.Select(i => i.Stage.ToString()).Distinct());
                        RecordAudit(
                            activeProvider.ProviderName,
                            activeProvider.ModelId,
                            stages,
                            false,
                            errCode,
                            _maxRepairAttempts,
                            externalApproved,
                            chunk.ChunkId,
                            chunkTokens,
                            chunk.FilteredOutEventCount + chunkFilteredOut);

                        return new AiGenerationOutcome
                        {
                            IsAccepted = false,
                            Validation = chunkVal,
                            ErrorReason = $"Chunk '{chunk.ChunkId}' failed validation: " +
                                          string.Join("; ", chunkVal.Issues.Select(i => $"[{i.Code}] {i.Message}")),
                            RepairAttemptsUsed = _maxRepairAttempts,
                            TotalChunksCount = chunks.Count,
                            EstimatedTokens = totalChunkTokens,
                            FilteredOutEventCount = totalFilteredEvents,
                            FailedChunkId = chunk.ChunkId,
                            UsedFallbackGeneration = true,
                            ProcessedChunkIds = processedChunkIds
                        };
                    }

                    processedChunkIds.Add(chunk.ChunkId);
                    foreach (var origin in chunkDef.Policy.AllowedOrigins)
                    {
                        if (!mergedOrigins.Any(o =>
                                string.Equals(o.Scheme, origin.Scheme, StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(o.Host, origin.Host, StringComparison.OrdinalIgnoreCase) &&
                                o.Port == origin.Port))
                        {
                            mergedOrigins.Add(origin);
                        }
                    }

                    if (chunkDef.Root?.Children is { Count: > 0 })
                    {
                        foreach (var child in chunkDef.Root.Children)
                        {
                            child.NodeId = $"step-{stepCounter++}";
                            mergedNodes.Add(child);
                        }
                    }
                }

                if (mergedNodes.Count > 0)
                {
                    var mergedWorkflow = new WorkflowDefinition
                    {
                        SchemaVersion = IrConstants.CurrentSchemaVersion,
                        WorkflowId = $"wf-ai-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}",
                        Name = "記録から自動作成したマクロ（チャンク統合）",
                        Version = 1,
                        Policy = new PolicyDeclaration
                        {
                            AllowedOrigins = mergedOrigins,
                            AllowCoordinateClick = policy.AllowCoordinateClick,
                            AllowExternalAi = policy.AllowExternalAi,
                            AllowedExcelDirectories = policy.AllowedExcelDirectories
                        },
                        Root = new WorkflowNode
                        {
                            NodeId = "root",
                            Type = IrConstants.NodeTypeSequence,
                            Children = mergedNodes
                        }
                    };

                    string mergedJson = JsonSerializer.Serialize(
                        mergedWorkflow,
                        new JsonSerializerOptions { WriteIndented = true });
                    var (mergedValidation, validatedDef) = _validator.ValidateJson(mergedJson);
                    if (mergedValidation.IsValid && validatedDef is not null)
                    {
                        return new AiGenerationOutcome
                        {
                            IsAccepted = true,
                            Definition = validatedDef,
                            RawJson = mergedJson,
                            Validation = mergedValidation,
                            ErrorReason = string.Empty,
                            RepairAttemptsUsed = 0,
                            TotalChunksCount = chunks.Count,
                            EstimatedTokens = totalChunkTokens,
                            FilteredOutEventCount = totalFilteredEvents,
                            UsedFallbackGeneration = true,
                            ProcessedChunkIds = processedChunkIds
                        };
                    }
                }
            }
        }
        catch (InvalidOperationException)
        {
            // モックプロバイダー等で追加レスポンスが未定義の場合は元の検証結果へ委譲する
        }

        return null;
    }

    private static IReadOnlyList<NormalizedActionCandidate> SanitizeCandidatesForExternalAi(
        IReadOnlyList<NormalizedActionCandidate> source)
    {
        return source.Select(c => c with
        {
            SanitizedValue = c.IsSensitive
                ? SensitiveDataProtector.RedactedPlaceholder
                : SensitiveDataProtector.SanitizeText(c.SanitizedValue),
            TargetUrlOrWorkbook = SensitiveDataProtector.SanitizeText(c.TargetUrlOrWorkbook)
        }).ToList();
    }

    private void RecordAudit(
        string providerName,
        string modelId,
        string stageSummary,
        bool accepted,
        string errorClassification,
        int repairCount,
        bool externalApproved,
        string? chunkId = null,
        int estimatedTokens = 0,
        int filteredOutEventCount = 0)
    {
        string formattedStageSummary = AiAuditMetadataFormatter.FormatStageSummaryWithMetadata(
            stageSummary,
            chunkId,
            estimatedTokens,
            filteredOutEventCount);

        _repository.InsertAiAuditLog(
            new AiAuditRecord(
                AuditId: $"aiaudit-{Guid.NewGuid():N}",
                ProviderName: providerName,
                ModelId: modelId,
                ValidationStageSummary: formattedStageSummary,
                IsAccepted: accepted,
                ErrorClassification: errorClassification,
                RepairAttemptCount: repairCount,
                ExternalSendApproved: externalApproved,
                Timestamp: DateTimeOffset.UtcNow),
            chunkId,
            estimatedTokens,
            filteredOutEventCount);
    }
}
