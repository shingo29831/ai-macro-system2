// @role: アクション実行状態遷移を駆動するドメイントリガーの列挙型
namespace AI.MacroSystem.Domain.StateMachines;

public enum ActionTrigger
{
    Start,
    Skip,
    CompleteSuccess,
    ScheduleRetry,
    StartRetry,
    Fail,
    FaultUnknown,
    Cancel,
}
