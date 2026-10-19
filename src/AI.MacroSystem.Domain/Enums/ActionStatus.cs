// @role: アクション実行単位のライフサイクル状態を定義する列挙型
namespace AI.MacroSystem.Domain.Enums;

public enum ActionStatus
{
    Pending,
    Running,
    Retrying,
    Succeeded,
    Failed,
    Skipped,
    Cancelled,
    Unknown,
}
