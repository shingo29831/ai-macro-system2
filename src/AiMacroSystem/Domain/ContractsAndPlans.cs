// Module: @role: アクション契約・実行計画・永続化エンティティ・検証結果のデータ契約定義
using System;
using System.Collections.Generic;

namespace AiMacroSystem.Domain;

public sealed class ActionContract
{
    public required string ActionType { get; init; }
    public required SideEffectKind DeclaredSideEffects { get; init; }
    public required RiskLevel BaseRiskLevel { get; init; }
    public required IReadOnlyDictionary<string, string> OutputTypes { get; init; }
    public required TimeSpan Timeout { get; init; }
    public required bool AllowRetry { get; init; }
    public required int MaxRetries { get; init; }
    public required IReadOnlySet<FailureClassification> RetryableClassifications { get; init; }
    public required bool RequiresPostconditionVerification { get; init; }
}

public sealed class PlannedActionItem
{
    public required string NodeId { get; init; }
    public required string ActionType { get; init; }
    public required string TargetSummary { get; init; }
    public required SideEffectKind SideEffects { get; init; }
    public required RiskLevel EvaluatedRisk { get; init; }
    public required bool RequiresIndividualApproval { get; init; }
}

public sealed class ExecutionPlan
{
    public required string VersionId { get; init; }
    public required string WorkflowId { get; init; }
    public required int VersionNumber { get; init; }
    public required string PlanHash { get; init; }
    public required WorkflowDefinition Definition { get; init; }
    public required IReadOnlyList<PlannedActionItem> PlannedActions { get; init; }
    public required RiskLevel MaxRiskLevel { get; init; }
}

public sealed class ValidationIssue
{
    public required ValidationStage Stage { get; init; }
    public required string Code { get; init; }
    public required string Message { get; init; }
    public string? NodeId { get; init; }
    public string? SuggestedFix { get; init; }
}

public sealed class ValidationResult
{
    public bool IsValid => Issues.Count == 0;
    public List<ValidationIssue> Issues { get; } = new();

    public void AddIssue(ValidationStage stage, string code, string message, string? nodeId = null, string? suggestedFix = null)
    {
        Issues.Add(new ValidationIssue
        {
            Stage = stage,
            Code = code,
            Message = message,
            NodeId = nodeId,
            SuggestedFix = suggestedFix
        });
    }
}

public sealed record WorkflowVersionRecord(
    string VersionId,
    string WorkflowId,
    string Name,
    int VersionNumber,
    string IrJson,
    string SchemaVersion,
    DateTimeOffset CreatedAt);

public sealed record WorkflowDraftRecord(
    string DraftId,
    string WorkflowId,
    string? BaseVersionId,
    string DraftJson,
    DateTimeOffset UpdatedAt);

public sealed record ApprovalRecord(
    string ApprovalId,
    string VersionId,
    string PlanHash,
    string Approver,
    ApprovalDecision Decision,
    IReadOnlyList<string> ApprovedHighRiskNodeIds,
    DateTimeOffset CreatedAt);

public sealed record ExecutionRecord(
    string ExecutionId,
    string VersionId,
    string PlanHash,
    ExecutionStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt);

public sealed record ActionExecutionRecord(
    string ActionExecutionId,
    string ExecutionId,
    string NodeId,
    ActionExecutionStatus Status,
    int AttemptCount,
    FailureClassification LastFailureClassification,
    string LastMessage);

public sealed record ExecutionEventRecord(
    string EventId,
    string ExecutionId,
    string? ActionExecutionId,
    string EventType,
    DateTimeOffset Timestamp,
    string DetailsJson);

public sealed record RecoveryRecord(
    string RecoveryId,
    string ExecutionId,
    RecoveryStatus RecoveryStatus,
    string DecisionNotes,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ResolvedAt);

public sealed record AiAuditRecord(
    string AuditId,
    string ProviderName,
    string ModelId,
    string ValidationStageSummary,
    bool IsAccepted,
    string ErrorClassification,
    int RepairAttemptCount,
    bool ExternalSendApproved,
    DateTimeOffset Timestamp);
