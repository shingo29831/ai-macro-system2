"""Module: @role: 実行状態・アクション状態・副作用・リスク・検証分類等のドメイン列挙型定義"""
using System;

namespace AiMacroSystem.Domain;

public enum ExecutionStatus
{
    Pending,
    Validating,
    AwaitingApproval,
    Ready,
    Running,
    Paused,
    Succeeded,
    Failed,
    Cancelled,
    Unknown
}

public enum ActionExecutionStatus
{
    Pending,
    Running,
    Retrying,
    Succeeded,
    Failed,
    Skipped,
    Cancelled,
    Unknown
}

[Flags]
public enum SideEffectKind
{
    None = 0,
    Read = 1,
    LocalMutation = 2,
    ExternalSend = 4,
    Destructive = 8,
    Unknown = 16
}

public enum RiskLevel
{
    Low = 0,
    Medium = 1,
    High = 2,
    Prohibited = 3
}

public enum ApprovalDecision
{
    Approved,
    Rejected
}

public enum FailureClassification
{
    None,
    ValidationFailure,
    PolicyViolation,
    TargetNotFound,
    TargetAmbiguous,
    PreconditionFailed,
    PostconditionMismatch,
    TransientError,
    PermanentError,
    CancelledByUser,
    ExternalOutcomeUnknown
}

public enum RecoveryStatus
{
    Pending,
    ResolvedAsFailed,
    ResolvedAsCancelled,
    ResolvedAsCompleted,
    ApprovedForFreshRun
}

public enum ExternalStateProbeResult
{
    ConfirmedCompleted,
    ConfirmedNotExecuted,
    OutcomeUnknown
}

public enum ValidationStage
{
    JsonSyntax = 1,
    JsonSchema = 2,
    ArgumentTypesAndRanges = 3,
    DataReferencesAndScope = 4,
    ControlFlowStructure = 5,
    PreAndPostConditions = 6,
    TargetAndAllowedScope = 7,
    SideEffectsRiskAndPolicy = 8
}
