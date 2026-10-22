// Module: @role: 操作記録・機密情報除外・意味的正規化・ローカル優先AIオーケストレーター・AI監査
using System;
using System.Collections.Generic;
using System.Linq;
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
    private readonly List<RawRecordedEvent> _events = new();
    private DateTimeOffset _monotonicClock = DateTimeOffset.UtcNow;
    public bool IsRecording { get; private set; }

    public RecordingService(SqliteRepository? repository = null)
    {
        _repository = repository;
    }

    public void StartRecording()
    {
        _events.Clear();
        _monotonicClock = DateTimeOffset.UtcNow;
        IsRecording = true;
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
            }
            recorded.Add(safeEv);
        }
        return recorded;
    }

    public IReadOnlyList<RawRecordedEvent> GetRecordedEvents() => _events.AsReadOnly();
}

public sealed class NormalizationService
{
    private readonly SqliteRepository? _repository;
    private readonly MultiLogExtractionEngine _extractionEngine;

    public NormalizationService(SqliteRepository? repository = null, MultiLogExtractionEngine? extractionEngine = null)
    {
        _repository = repository;
        _extractionEngine = extractionEngine ?? new MultiLogExtractionEngine();
    }

    public IReadOnlyList<NormalizedActionCandidate> Normalize(IReadOnlyList<RawRecordedEvent> rawEvents)
    {
        var candidates = _extractionEngine.ExtractCandidates(rawEvents);
        foreach (var candidate in candidates)
        {
            _repository?.InsertNormalizedCandidate(candidate);
        }
        return candidates;
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
}

public sealed class AiOrchestrator
{
    private readonly IAiProvider _localProvider;
    private readonly IAiProvider? _externalProvider;
    private readonly IrValidator _validator;
    private readonly SqliteRepository _repository;
    private readonly int _maxRepairAttempts;

    public AiOrchestrator(
        IAiProvider localProvider,
        IAiProvider? externalProvider,
        IrValidator validator,
        SqliteRepository repository,
        int maxRepairAttempts = 2)
    {
        _localProvider = localProvider;
        _externalProvider = externalProvider;
        _validator = validator;
        _repository = repository;
        _maxRepairAttempts = maxRepairAttempts;
    }

    public async Task<AiGenerationOutcome> GenerateAndValidateIrAsync(
        IReadOnlyList<NormalizedActionCandidate> candidates,
        PolicyDeclaration policy,
        bool userApprovedExternalAi,
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
                RecordAudit(activeProvider.ProviderName, activeProvider.ModelId, "AllStagesPassed", true, "NONE", attempt, externalApproved);
                return new AiGenerationOutcome
                {
                    IsAccepted = true,
                    Definition = definition,
                    RawJson = lastJson,
                    Validation = validation,
                    ErrorReason = string.Empty,
                    RepairAttemptsUsed = attempt
                };
            }

            previousIssues = validation.Issues;
        }

        string firstErrorCode = lastValidation.Issues.FirstOrDefault()?.Code ?? "VALIDATION_FAILED";
        string stageSummary = string.Join(",", lastValidation.Issues.Select(i => i.Stage.ToString()).Distinct());
        RecordAudit(activeProvider.ProviderName, activeProvider.ModelId, stageSummary, false, firstErrorCode, _maxRepairAttempts, externalApproved);

        return new AiGenerationOutcome
        {
            IsAccepted = false,
            RawJson = lastJson,
            Validation = lastValidation,
            ErrorReason = string.Join("; ", lastValidation.Issues.Select(i => $"[{i.Code}] {i.Message}")),
            RepairAttemptsUsed = _maxRepairAttempts
        };
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
        bool externalApproved)
    {
        _repository.InsertAiAuditLog(new AiAuditRecord(
            AuditId: $"aiaudit-{Guid.NewGuid():N}",
            ProviderName: providerName,
            ModelId: modelId,
            ValidationStageSummary: stageSummary,
            IsAccepted: accepted,
            ErrorClassification: errorClassification,
            RepairAttemptCount: repairCount,
            ExternalSendApproved: externalApproved,
            Timestamp: DateTimeOffset.UtcNow));
    }
}
