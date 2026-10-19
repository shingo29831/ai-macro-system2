// @role: 期待する現在状態と実データが不一致となった並行性競合を表すドメイン例外
namespace AI.MacroSystem.Domain.Exceptions;

public sealed class ConcurrencyConflictException : InvalidOperationException
{
    public string EntityType { get; }
    public string EntityId { get; }
    public string ExpectedState { get; }

    public ConcurrencyConflictException(string entityType, string entityId, string expectedState, string? message = null)
        : base(message ?? $"Concurrency conflict for {entityType} '{entityId}': expected state was '{expectedState}'.")
    {
        EntityType = entityType;
        EntityId = entityId;
        ExpectedState = expectedState;
    }
}
