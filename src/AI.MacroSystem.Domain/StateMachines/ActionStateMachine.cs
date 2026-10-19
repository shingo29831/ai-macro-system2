// @role: アクション実行状態の遷移ルール検証および監査イベント生成を行うドメイン状態機械
namespace AI.MacroSystem.Domain.StateMachines;

using AI.MacroSystem.Domain.Enums;
using AI.MacroSystem.Domain.Exceptions;

public sealed record ActionTransitionRule(
    ActionStatus From,
    ActionStatus To,
    ActionTrigger Trigger,
    string AuditEventType,
    string GuardDescription);

public sealed record ActionTransitionResult(
    ActionStatus CurrentStatus,
    ActionStatus NewStatus,
    ActionTrigger Trigger,
    string AuditEventType);

public static class ActionStateMachine
{
    public static readonly IReadOnlyList<ActionTransitionRule> Rules = new List<ActionTransitionRule>
    {
        new(ActionStatus.Pending, ActionStatus.Running, ActionTrigger.Start, "ActionStarted", "アクション試行の開始"),
        new(ActionStatus.Pending, ActionStatus.Skipped, ActionTrigger.Skip, "ActionSkipped", "条件分岐等によるスキップ判定"),
        new(ActionStatus.Pending, ActionStatus.Cancelled, ActionTrigger.Cancel, "ActionCancelled", "親ワークフロー中止等によるキャンセル"),

        new(ActionStatus.Running, ActionStatus.Succeeded, ActionTrigger.CompleteSuccess, "ActionSucceeded", "アクション試行の正常完了"),
        new(ActionStatus.Running, ActionStatus.Retrying, ActionTrigger.ScheduleRetry, "ActionRetryScheduled", "失敗検知かつ副作用安全・再試行上限未満"),
        new(ActionStatus.Running, ActionStatus.Failed, ActionTrigger.Fail, "ActionFailed", "失敗確定（上限到達または非再試行エラー）"),
        new(ActionStatus.Running, ActionStatus.Unknown, ActionTrigger.FaultUnknown, "ActionFaultedUnknown", "副作用成否不明状態の確定"),
        new(ActionStatus.Running, ActionStatus.Cancelled, ActionTrigger.Cancel, "ActionCancelled", "アクション実行中の中断要求"),

        new(ActionStatus.Retrying, ActionStatus.Running, ActionTrigger.StartRetry, "ActionRetrying", "待機インターバル完了後の再試行開始"),
        new(ActionStatus.Retrying, ActionStatus.Failed, ActionTrigger.Fail, "ActionFailed", "再試行待機中の致命的障害または期限到達"),
        new(ActionStatus.Retrying, ActionStatus.Cancelled, ActionTrigger.Cancel, "ActionCancelled", "再試行待機中のキャンセル"),
    };

    public static bool CanTransition(ActionStatus current, ActionStatus target)
    {
        if (current == target) return false;
        return Rules.Any(r => r.From == current && r.To == target);
    }

    public static bool CanTransition(ActionStatus current, ActionTrigger trigger)
    {
        return Rules.Any(r => r.From == current && r.Trigger == trigger);
    }

    public static ActionTransitionResult Transition(ActionStatus current, ActionTrigger trigger)
    {
        var rule = Rules.FirstOrDefault(r => r.From == current && r.Trigger == trigger);
        if (rule is null)
        {
            throw new InvalidStateTransitionException(
                "ActionExecution",
                current.ToString(),
                $"[Trigger: {trigger}]",
                $"Action state machine cannot trigger '{trigger}' from state '{current}'.");
        }

        return new ActionTransitionResult(current, rule.To, trigger, rule.AuditEventType);
    }

    public static ActionTransitionResult Transition(ActionStatus current, ActionStatus target)
    {
        var rule = Rules.FirstOrDefault(r => r.From == current && r.To == target);
        if (rule is null)
        {
            throw new InvalidStateTransitionException(
                "ActionExecution",
                current.ToString(),
                target.ToString());
        }

        return new ActionTransitionResult(current, target, rule.Trigger, rule.AuditEventType);
    }
}
