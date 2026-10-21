// Module: @role: 実行全体およびアクション単位の状態遷移表と遷移規則の強制
using System;
using System.Collections.Generic;

namespace AiMacroSystem.Domain;

public static class ExecutionStateMachine
{
    private static readonly HashSet<(ExecutionStatus From, ExecutionStatus To)> _allowedTransitions = new()
    {
        (ExecutionStatus.Pending, ExecutionStatus.Validating),
        (ExecutionStatus.Pending, ExecutionStatus.Cancelled),
        (ExecutionStatus.Pending, ExecutionStatus.Failed),

        (ExecutionStatus.Validating, ExecutionStatus.AwaitingApproval),
        (ExecutionStatus.Validating, ExecutionStatus.Ready),
        (ExecutionStatus.Validating, ExecutionStatus.Failed),
        (ExecutionStatus.Validating, ExecutionStatus.Cancelled),

        (ExecutionStatus.AwaitingApproval, ExecutionStatus.Ready),
        (ExecutionStatus.AwaitingApproval, ExecutionStatus.Failed),
        (ExecutionStatus.AwaitingApproval, ExecutionStatus.Cancelled),

        (ExecutionStatus.Ready, ExecutionStatus.Running),
        (ExecutionStatus.Ready, ExecutionStatus.Cancelled),
        (ExecutionStatus.Ready, ExecutionStatus.Failed),

        (ExecutionStatus.Running, ExecutionStatus.Paused),
        (ExecutionStatus.Running, ExecutionStatus.Succeeded),
        (ExecutionStatus.Running, ExecutionStatus.Failed),
        (ExecutionStatus.Running, ExecutionStatus.Cancelled),
        (ExecutionStatus.Running, ExecutionStatus.Unknown),

        (ExecutionStatus.Paused, ExecutionStatus.Running),
        (ExecutionStatus.Paused, ExecutionStatus.Cancelled),
        (ExecutionStatus.Paused, ExecutionStatus.Failed),
        (ExecutionStatus.Paused, ExecutionStatus.Unknown)
    };

    public static bool CanTransition(ExecutionStatus from, ExecutionStatus to) =>
        _allowedTransitions.Contains((from, to));

    public static void EnsureValidTransition(ExecutionStatus from, ExecutionStatus to)
    {
        if (!CanTransition(from, to))
        {
            throw new InvalidOperationException($"Invalid execution state transition from '{from}' to '{to}'.");
        }
    }

    public static bool IsTerminal(ExecutionStatus status) =>
        status is ExecutionStatus.Succeeded
            or ExecutionStatus.Failed
            or ExecutionStatus.Cancelled
            or ExecutionStatus.Unknown;

    public static bool HoldsExclusiveLock(ExecutionStatus status) =>
        status is ExecutionStatus.Pending
            or ExecutionStatus.Validating
            or ExecutionStatus.AwaitingApproval
            or ExecutionStatus.Ready
            or ExecutionStatus.Running
            or ExecutionStatus.Paused
            or ExecutionStatus.Unknown;

    // キャンセル要求と外部副作用のUnknownが競合した場合は仕様11.5に従いUnknownを優先する
    public static ExecutionStatus ResolveCompetingTerminal(bool cancelRequested, bool hasUnknownAction, bool hasFailedAction)
    {
        if (hasUnknownAction)
        {
            return ExecutionStatus.Unknown;
        }
        if (hasFailedAction)
        {
            return ExecutionStatus.Failed;
        }
        if (cancelRequested)
        {
            return ExecutionStatus.Cancelled;
        }
        return ExecutionStatus.Succeeded;
    }
}

public static class ActionStateMachine
{
    private static readonly HashSet<(ActionExecutionStatus From, ActionExecutionStatus To)> _allowedTransitions = new()
    {
        (ActionExecutionStatus.Pending, ActionExecutionStatus.Running),
        (ActionExecutionStatus.Pending, ActionExecutionStatus.Skipped),
        (ActionExecutionStatus.Pending, ActionExecutionStatus.Cancelled),

        (ActionExecutionStatus.Running, ActionExecutionStatus.Succeeded),
        (ActionExecutionStatus.Running, ActionExecutionStatus.Failed),
        (ActionExecutionStatus.Running, ActionExecutionStatus.Retrying),
        (ActionExecutionStatus.Running, ActionExecutionStatus.Cancelled),
        (ActionExecutionStatus.Running, ActionExecutionStatus.Unknown),

        (ActionExecutionStatus.Retrying, ActionExecutionStatus.Running),
        (ActionExecutionStatus.Retrying, ActionExecutionStatus.Failed),
        (ActionExecutionStatus.Retrying, ActionExecutionStatus.Cancelled),
        (ActionExecutionStatus.Retrying, ActionExecutionStatus.Unknown)
    };

    public static bool CanTransition(ActionExecutionStatus from, ActionExecutionStatus to) =>
        _allowedTransitions.Contains((from, to));

    public static void EnsureValidTransition(ActionExecutionStatus from, ActionExecutionStatus to)
    {
        // Unknown状態からの自動再試行(Retrying/Running)は仕様により厳禁
        if (from == ActionExecutionStatus.Unknown && (to == ActionExecutionStatus.Retrying || to == ActionExecutionStatus.Running))
        {
            throw new InvalidOperationException("Automatic retry or re-execution from Unknown action state is strictly prohibited.");
        }

        if (!CanTransition(from, to))
        {
            throw new InvalidOperationException($"Invalid action state transition from '{from}' to '{to}'.");
        }
    }

    public static bool IsTerminal(ActionExecutionStatus status) =>
        status is ActionExecutionStatus.Succeeded
            or ActionExecutionStatus.Failed
            or ActionExecutionStatus.Skipped
            or ActionExecutionStatus.Cancelled
            or ActionExecutionStatus.Unknown;
}
