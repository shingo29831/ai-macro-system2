namespace AI.MacroSystem.Domain.Models;

using AI.MacroSystem.Domain.Enums;

public sealed record ExecutionRecord(
    string ExecutionId,
    string WorkflowId,
    int Version,
    ExecutionStatus Status,
    string TriggeredBy,
    DateTime? StartedAtUtc,
    DateTime? CompletedAtUtc,
    string? ErrorMessage,
    DateTime CreatedAtUtc);

public sealed record ActionExecutionRecord(
    string ActionExecutionId,
    string ExecutionId,
    string NodeId,
    int ExecutionOrder,
    int IterationIndex,
    string ActionType,
    ActionStatus Status,
    string ParametersJson,
    string? ResultJson,
    string? ErrorMessage,
    ActionErrorCategory ErrorCategory,
    DateTime StartedAtUtc,
    DateTime? CompletedAtUtc);

public sealed record ExecutionEventRecord(
    string EventId,
    string ExecutionId,
    int SequenceNumber,
    string EventType,
    string PayloadJson,
    DateTime TimestampUtc);

public sealed record RecoveryRecord(
    string RecoveryId,
    string ExecutionId,
    string Reason,
    RecoveryStatus RecoveryStatus,
    string? ConfirmedBy,
    DateTime? ConfirmedAtUtc,
    string? DetailsJson,
    DateTime CreatedAtUtc);

public sealed record SystemLockStatus(
    string LockKey,
    string? CurrentExecutionId,
    bool IsRecoveryPending,
    DateTime? AcquiredAtUtc);
