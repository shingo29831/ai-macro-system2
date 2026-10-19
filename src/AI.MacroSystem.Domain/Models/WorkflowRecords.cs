namespace AI.MacroSystem.Domain.Models;

using AI.MacroSystem.Domain.Enums;

public sealed record WorkflowRecord(
    string WorkflowId,
    string Name,
    string Description,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record WorkflowVersionRecord(
    string WorkflowId,
    int Version,
    string WorkflowJson,
    RiskLevel RiskLevel,
    DateTime CreatedAtUtc,
    string CreatedBy);

public sealed record WorkflowDraftRecord(
    string WorkflowId,
    int DraftVersion,
    string WorkflowJson,
    DateTime UpdatedAtUtc);

public sealed record ApprovalRecord(
    string ApprovalId,
    string WorkflowId,
    int Version,
    ApprovalStatus Status,
    string? ApprovedBy,
    DateTime? ApprovedAtUtc,
    string? Notes,
    DateTime CreatedAtUtc);
