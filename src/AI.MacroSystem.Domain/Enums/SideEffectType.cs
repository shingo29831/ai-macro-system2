namespace AI.MacroSystem.Domain.Enums;

/// <summary>
/// ワークフローおよび操作の副作用分類を定義します。
/// </summary>
public enum SideEffectType
{
    Read,
    LocalMutation,
    ExternalSend,
    Destructive,
}
