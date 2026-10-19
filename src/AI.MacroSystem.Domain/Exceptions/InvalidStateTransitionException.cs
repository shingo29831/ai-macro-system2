// @role: 状態機械において不正な状態遷移が試みられたことを表すドメイン例外
namespace AI.MacroSystem.Domain.Exceptions;

public sealed class InvalidStateTransitionException : InvalidOperationException
{
    public string EntityType { get; }
    public string CurrentState { get; }
    public string AttemptedTargetState { get; }

    public InvalidStateTransitionException(string entityType, string currentState, string attemptedTargetState, string? message = null)
        : base(message ?? $"Invalid state transition for {entityType}: cannot transition from '{currentState}' to '{attemptedTargetState}'.")
    {
        EntityType = entityType;
        CurrentState = currentState;
        AttemptedTargetState = attemptedTargetState;
    }
}
