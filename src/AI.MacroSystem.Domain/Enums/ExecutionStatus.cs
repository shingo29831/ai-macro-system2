namespace AI.MacroSystem.Domain.Enums;

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
    Unknown,
}
