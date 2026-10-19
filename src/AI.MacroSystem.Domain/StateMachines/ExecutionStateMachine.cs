// @role: ワークフロー実行状態の遷移ルール検証および監査イベント生成を行うドメイン状態機械
namespace AI.MacroSystem.Domain.StateMachines;

using AI.MacroSystem.Domain.Enums;
using AI.MacroSystem.Domain.Exceptions;

public sealed record ExecutionTransitionRule(
    ExecutionStatus From,
    ExecutionStatus To,
    ExecutionTrigger Trigger,
    string AuditEventType,
    string GuardDescription);

public sealed record ExecutionTransitionResult(
    ExecutionStatus CurrentStatus,
    ExecutionStatus NewStatus,
    ExecutionTrigger Trigger,
    string AuditEventType);

public static class ExecutionStateMachine
{
    public static readonly IReadOnlyList<ExecutionTransitionRule> Rules = new List<ExecutionTransitionRule>
    {
        new(ExecutionStatus.Pending, ExecutionStatus.Validating, ExecutionTrigger.StartValidation, "ExecutionValidationStarted", "構文・スキーマ・リスク検証の開始"),
        new(ExecutionStatus.Pending, ExecutionStatus.Cancelled, ExecutionTrigger.Cancel, "ExecutionCancelled", "実行開始前キャンセル要求"),

        new(ExecutionStatus.Validating, ExecutionStatus.Ready, ExecutionTrigger.ValidationPassedAutoApprove, "ExecutionValidationPassed", "検証合格かつ承認不要または事前承認済み"),
        new(ExecutionStatus.Validating, ExecutionStatus.AwaitingApproval, ExecutionTrigger.ValidationPassedRequireApproval, "ExecutionApprovalRequested", "検証合格かつ高リスク等のため承認待ち"),
        new(ExecutionStatus.Validating, ExecutionStatus.Failed, ExecutionTrigger.ValidationFailed, "ExecutionValidationFailed", "検証エラー検知"),
        new(ExecutionStatus.Validating, ExecutionStatus.Cancelled, ExecutionTrigger.Cancel, "ExecutionCancelled", "検証中キャンセル要求"),

        new(ExecutionStatus.AwaitingApproval, ExecutionStatus.Ready, ExecutionTrigger.Approve, "ExecutionApproved", "明示的承認の受領"),
        new(ExecutionStatus.AwaitingApproval, ExecutionStatus.Failed, ExecutionTrigger.RejectApproval, "ExecutionApprovalRejected", "承認拒絶または却下"),
        new(ExecutionStatus.AwaitingApproval, ExecutionStatus.Cancelled, ExecutionTrigger.Cancel, "ExecutionCancelled", "承認待ち中キャンセル要求"),

        new(ExecutionStatus.Ready, ExecutionStatus.Running, ExecutionTrigger.StartExecution, "ExecutionStarted", "単一実行排他ロック取得完了と実行開始"),
        new(ExecutionStatus.Ready, ExecutionStatus.Cancelled, ExecutionTrigger.Cancel, "ExecutionCancelled", "実行直前キャンセル要求"),

        new(ExecutionStatus.Running, ExecutionStatus.Paused, ExecutionTrigger.Pause, "ExecutionPaused", "一時停止要求の受領"),
        new(ExecutionStatus.Running, ExecutionStatus.Succeeded, ExecutionTrigger.CompleteSuccess, "ExecutionSucceeded", "全ノード正常終了"),
        new(ExecutionStatus.Running, ExecutionStatus.Failed, ExecutionTrigger.FailExecution, "ExecutionFailed", "致命的障害または回復不能エラー"),
        new(ExecutionStatus.Running, ExecutionStatus.Unknown, ExecutionTrigger.FaultUnknown, "ExecutionFaultedUnknown", "副作用成否不明またはクラッシュ"),
        new(ExecutionStatus.Running, ExecutionStatus.Cancelled, ExecutionTrigger.Cancel, "ExecutionCancelled", "実行中中断要求"),

        new(ExecutionStatus.Paused, ExecutionStatus.Running, ExecutionTrigger.Resume, "ExecutionResumed", "一時停止からの再開"),
        new(ExecutionStatus.Paused, ExecutionStatus.Cancelled, ExecutionTrigger.Cancel, "ExecutionCancelled", "一時停止中キャンセル要求"),
    };

    public static bool CanTransition(ExecutionStatus current, ExecutionStatus target)
    {
        if (current == target) return false;
        return Rules.Any(r => r.From == current && r.To == target);
    }

    public static bool CanTransition(ExecutionStatus current, ExecutionTrigger trigger)
    {
        return Rules.Any(r => r.From == current && r.Trigger == trigger);
    }

    public static ExecutionTransitionResult Transition(ExecutionStatus current, ExecutionTrigger trigger)
    {
        var rule = Rules.FirstOrDefault(r => r.From == current && r.Trigger == trigger);
        if (rule is null)
        {
            throw new InvalidStateTransitionException(
                "Execution",
                current.ToString(),
                $"[Trigger: {trigger}]",
                $"Execution state machine cannot trigger '{trigger}' from state '{current}'.");
        }

        return new ExecutionTransitionResult(current, rule.To, trigger, rule.AuditEventType);
    }

    public static ExecutionTransitionResult Transition(ExecutionStatus current, ExecutionStatus target)
    {
        var rule = Rules.FirstOrDefault(r => r.From == current && r.To == target);
        if (rule is null)
        {
            throw new InvalidStateTransitionException(
                "Execution",
                current.ToString(),
                target.ToString());
        }

        return new ExecutionTransitionResult(current, target, rule.Trigger, rule.AuditEventType);
    }
}
