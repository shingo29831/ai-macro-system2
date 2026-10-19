// @role: ワークフロー実行状態遷移を駆動するドメイントリガーの列挙型
namespace AI.MacroSystem.Domain.StateMachines;

public enum ExecutionTrigger
{
    StartValidation,
    ValidationPassedAutoApprove,
    ValidationPassedRequireApproval,
    ValidationFailed,
    Approve,
    RejectApproval,
    StartExecution,
    Pause,
    Resume,
    CompleteSuccess,
    FailExecution,
    FaultUnknown,
    Cancel,
}
