// Module: @role: 制御フロー構造（Sequence/If/ForEach/While）およびアクション実行制御・境界ハンドリング
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AiMacroSystem.Domain;

namespace AiMacroSystem.Application;

public sealed partial class ExecutionEngine
{
    private async Task ExecuteNodeRecursiveAsync(
        string executionId,
        WorkflowNode node,
        PolicyDeclaration policy,
        ExecutionPlan approvedPlan,
        Dictionary<string, IReadOnlyDictionary<string, object?>> runtimeOutputs,
        ExecutionOutcomeFlags flags,
        CancellationToken ct)
    {
        if (flags.ShouldStop)
        {
            return;
        }

        await HandleActionBoundaryPauseOrCancelAsync(executionId, flags, ct);
        if (flags.ShouldStop)
        {
            return;
        }

        switch (node.Type)
        {
            case IrConstants.NodeTypeSequence:
                foreach (var child in node.Children!)
                {
                    await ExecuteNodeRecursiveAsync(executionId, child, policy, approvedPlan, runtimeOutputs, flags, ct);
                    if (flags.ShouldStop) break;
                }
                break;

            case IrConstants.NodeTypeIf:
                bool condTrue = EvaluateCondition(node.Condition!, runtimeOutputs);
                var chosenBranch = condTrue ? node.ThenChildren! : (node.ElseChildren ?? new List<WorkflowNode>());
                var skippedBranch = condTrue ? (node.ElseChildren ?? new List<WorkflowNode>()) : node.ThenChildren!;

                foreach (var skippedNode in skippedBranch)
                {
                    MarkSubtreeSkipped(executionId, skippedNode);
                }

                foreach (var child in chosenBranch)
                {
                    await ExecuteNodeRecursiveAsync(executionId, child, policy, approvedPlan, runtimeOutputs, flags, ct);
                    if (flags.ShouldStop) break;
                }
                break;

            case IrConstants.NodeTypeForEach:
                var items = ResolveArrayReference(node.CollectionRef!, runtimeOutputs);
                if (items.Count == 0)
                {
                    foreach (var skippedChild in node.BodyChildren!)
                    {
                        MarkSubtreeSkipped(executionId, skippedChild);
                    }
                    break;
                }
                foreach (string item in items)
                {
                    runtimeOutputs[node.NodeId] = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        [node.ItemVariableName!] = item
                    };
                    foreach (var child in node.BodyChildren!)
                    {
                        await ExecuteNodeRecursiveAsync(executionId, child, policy, approvedPlan, runtimeOutputs, flags, ct);
                        if (flags.ShouldStop) break;
                    }
                    if (flags.ShouldStop) break;
                }
                runtimeOutputs.Remove(node.NodeId);
                break;

            case IrConstants.NodeTypeWhile:
                int iterations = 0;
                int maxIter = node.MaxIterations ?? 100;
                if (!EvaluateCondition(node.Condition!, runtimeOutputs))
                {
                    foreach (var skippedChild in node.BodyChildren!)
                    {
                        MarkSubtreeSkipped(executionId, skippedChild);
                    }
                    break;
                }
                while (EvaluateCondition(node.Condition!, runtimeOutputs))
                {
                    if (++iterations > maxIter)
                    {
                        flags.HasFailed = true;
                        _repository.AppendExecutionEvent(executionId, null, "WhileMaxIterationsExceeded", $"Node '{node.NodeId}' exceeded maxIterations ({maxIter}).");
                        break;
                    }
                    foreach (var child in node.BodyChildren!)
                    {
                        await ExecuteNodeRecursiveAsync(executionId, child, policy, approvedPlan, runtimeOutputs, flags, ct);
                        if (flags.ShouldStop) break;
                    }
                    if (flags.ShouldStop) break;
                }
                break;

            case IrConstants.NodeTypeAction:
                await ExecuteSingleActionNodeAsync(executionId, node, policy, approvedPlan, runtimeOutputs, flags, ct);
                break;
        }
    }

    private async Task ExecuteSingleActionNodeAsync(
        string executionId,
        WorkflowNode node,
        PolicyDeclaration policy,
        ExecutionPlan approvedPlan,
        Dictionary<string, IReadOnlyDictionary<string, object?>> runtimeOutputs,
        ExecutionOutcomeFlags flags,
        CancellationToken ct)
    {
        string actionExecutionId = $"actexec-{Guid.NewGuid():N}";
        _repository.CreateActionExecutionAtomic(actionExecutionId, executionId, node.NodeId, ActionExecutionStatus.Pending);

        if (_cancelRequested || ct.IsCancellationRequested)
        {
            ActionStateMachine.EnsureValidTransition(ActionExecutionStatus.Pending, ActionExecutionStatus.Cancelled);
            _repository.UpdateActionStateAtomic(executionId, actionExecutionId, ActionExecutionStatus.Cancelled, 0, FailureClassification.CancelledByUser, "Cancelled before action start.", "ActionCancelled");
            flags.WasCancelled = true;
            return;
        }

        // 実行直前に対象・引数・許可範囲・副作用リスクが承認済み計画と一致するか再検証する（仕様2.6 / 8）
        var (runtimePlanOk, runtimeMismatchReason) = _policyEngine.VerifyRuntimeNodeMatchesApprovedPlan(node, policy, approvedPlan);
        if (!runtimePlanOk)
        {
            ActionStateMachine.EnsureValidTransition(ActionExecutionStatus.Pending, ActionExecutionStatus.Running);
            _repository.UpdateActionStateAtomic(executionId, actionExecutionId, ActionExecutionStatus.Running, 1, FailureClassification.None, "Starting runtime verification.", "ActionStarted");
            ActionStateMachine.EnsureValidTransition(ActionExecutionStatus.Running, ActionExecutionStatus.Failed);
            _repository.UpdateActionStateAtomic(executionId, actionExecutionId, ActionExecutionStatus.Failed, 1, FailureClassification.PolicyViolation, runtimeMismatchReason, "ActionFailedPlanMismatch");
            flags.HasFailed = true;
            return;
        }

        var contract = ActionCatalog.All[node.ActionType!];
        if (!_adapters.TryGetValue(node.ActionType!, out var adapter))
        {
            throw new InvalidOperationException($"No adapter registered for actionType '{node.ActionType}'.");
        }

        var currentActionStatus = ActionExecutionStatus.Pending;
        int attempt = 0;

        while (true)
        {
            attempt++;
            ActionStateMachine.EnsureValidTransition(currentActionStatus, ActionExecutionStatus.Running);
            currentActionStatus = ActionExecutionStatus.Running;
            _repository.UpdateActionStateAtomic(executionId, actionExecutionId, currentActionStatus, attempt, FailureClassification.None, $"Executing attempt {attempt}.", "ActionRunning");

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(contract.Timeout);

            AdapterExecutionResult result;
            try
            {
                result = await adapter.ExecuteAsync(node, policy, runtimeOutputs, timeoutCts.Token);
            }
            catch (OperationCanceledException) when (_cancelRequested || ct.IsCancellationRequested)
            {
                // 実行中の中断で副作用がある操作は成否不明(Unknown)を優先する（仕様11）
                if (contract.DeclaredSideEffects == SideEffectKind.Read)
                {
                    ActionStateMachine.EnsureValidTransition(currentActionStatus, ActionExecutionStatus.Cancelled);
                    _repository.UpdateActionStateAtomic(executionId, actionExecutionId, ActionExecutionStatus.Cancelled, attempt, FailureClassification.CancelledByUser, "Read-only action cancelled safely.", "ActionCancelled");
                    flags.WasCancelled = true;
                }
                else
                {
                    ActionStateMachine.EnsureValidTransition(currentActionStatus, ActionExecutionStatus.Unknown);
                    _repository.UpdateActionStateAtomic(executionId, actionExecutionId, ActionExecutionStatus.Unknown, attempt, FailureClassification.ExternalOutcomeUnknown, "Side-effecting action interrupted by cancellation; external outcome is unknown.", "ActionOutcomeUnknown");
                    flags.HasUnknown = true;
                }
                break;
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
                // timeout超過時は読み取り専用なら一時エラー、副作用操作ならUnknownとする（仕様5）
                result = contract.DeclaredSideEffects == SideEffectKind.Read
                    ? AdapterExecutionResult.Failed(FailureClassification.TransientError, $"Action timed out after {contract.Timeout.TotalSeconds}s.", sideEffectCommitted: false)
                    : AdapterExecutionResult.Unknown($"Action timed out after {contract.Timeout.TotalSeconds}s; side-effect outcome is unknown.");
            }

            if (result.Status == ActionExecutionStatus.Succeeded)
            {
                // 出力型が契約の宣言型と適合するか実行時検証（仕様4.5 要件7）
                if (!VerifyRuntimeOutputsMatchContract(contract, result.Outputs))
                {
                    ActionStateMachine.EnsureValidTransition(currentActionStatus, ActionExecutionStatus.Failed);
                    _repository.UpdateActionStateAtomic(executionId, actionExecutionId, ActionExecutionStatus.Failed, attempt, FailureClassification.PostconditionMismatch, "Action output did not conform to declared contract output types.", "ActionOutputContractMismatch");
                    flags.HasFailed = true;
                    break;
                }

                ActionStateMachine.EnsureValidTransition(currentActionStatus, ActionExecutionStatus.Succeeded);
                _repository.UpdateActionStateAtomic(executionId, actionExecutionId, ActionExecutionStatus.Succeeded, attempt, FailureClassification.None, result.Message, "ActionSucceeded");
                runtimeOutputs[node.NodeId] = result.Outputs;
                break;
            }

            if (result.Status == ActionExecutionStatus.Unknown)
            {
                // Unknownの場合は自動再試行を絶対に行わず即時停止（仕様5.3 / 11.3 / 12.1）
                ActionStateMachine.EnsureValidTransition(currentActionStatus, ActionExecutionStatus.Unknown);
                _repository.UpdateActionStateAtomic(executionId, actionExecutionId, ActionExecutionStatus.Unknown, attempt, FailureClassification.ExternalOutcomeUnknown, result.Message, "ActionOutcomeUnknown");
                flags.HasUnknown = true;
                break;
            }

            // Failed の場合：再試行条件（契約許可・エラー分類・外部副作用未確定・上限回数内・キャンセル未要求）を検証（仕様12.1）
            bool canRetry = contract.AllowRetry
                && contract.RetryableClassifications.Contains(result.Classification)
                && !result.SideEffectCommitted
                && attempt <= contract.MaxRetries
                && !_cancelRequested
                && !ct.IsCancellationRequested;

            if (canRetry)
            {
                ActionStateMachine.EnsureValidTransition(currentActionStatus, ActionExecutionStatus.Retrying);
                currentActionStatus = ActionExecutionStatus.Retrying;
                _repository.UpdateActionStateAtomic(executionId, actionExecutionId, currentActionStatus, attempt, result.Classification, $"Retrying after transient failure: {result.Message}", "ActionRetrying");
                continue;
            }

            ActionStateMachine.EnsureValidTransition(currentActionStatus, ActionExecutionStatus.Failed);
            _repository.UpdateActionStateAtomic(executionId, actionExecutionId, ActionExecutionStatus.Failed, attempt, result.Classification, result.Message, "ActionFailed");
            flags.HasFailed = true;
            break;
        }

        if (ActionNodeCompletedAsync is not null)
        {
            await ActionNodeCompletedAsync.Invoke(executionId, node.NodeId);
        }

        // 停止要求受付後、実行中アクションが完了したことを独立イベントとして記録する（仕様11）
        if (_pauseRequested || _cancelRequested || ct.IsCancellationRequested)
        {
            _repository.AppendExecutionEvent(
                executionId,
                actionExecutionId,
                "RunningActionCompletedAfterStopRequest",
                $"Running action '{node.NodeId}' completed safely before applying cooperative stop.");
        }
    }

    private async Task HandleActionBoundaryPauseOrCancelAsync(string executionId, ExecutionOutcomeFlags flags, CancellationToken ct)
    {
        if (_cancelRequested || ct.IsCancellationRequested)
        {
            flags.WasCancelled = true;
            flags.CancelConfirmedLogged = true;
            _repository.AppendExecutionEvent(executionId, null, "CancelConfirmed", "Cooperative cancellation confirmed at action boundary.");
            return;
        }

        TaskCompletionSource<bool>? waitTcs = null;
        lock (_lockSync)
        {
            if (_pauseRequested)
            {
                waitTcs = _resumeSignal;
            }
        }

        if (waitTcs is not null)
        {
            TransitionExecution(executionId, ExecutionStatus.Running, ExecutionStatus.Paused, "Execution paused at action boundary.");
            _repository.AppendExecutionEvent(executionId, null, "PauseConfirmed", "Cooperative pause confirmed at action boundary.");
            bool resumed = await waitTcs.Task;
            if (!resumed || _cancelRequested || ct.IsCancellationRequested)
            {
                flags.WasCancelled = true;
                flags.CancelConfirmedLogged = true;
                _repository.AppendExecutionEvent(executionId, null, "CancelConfirmed", "Cooperative cancellation confirmed while paused.");
                return;
            }
            TransitionExecution(executionId, ExecutionStatus.Paused, ExecutionStatus.Running, "Execution resumed from Paused state.");
        }
    }

    private void MarkSubtreeSkipped(string executionId, WorkflowNode node)
    {
        if (node.Type == IrConstants.NodeTypeAction)
        {
            string actId = $"actexec-{Guid.NewGuid():N}";
            _repository.CreateActionExecutionAtomic(actId, executionId, node.NodeId, ActionExecutionStatus.Pending);
            ActionStateMachine.EnsureValidTransition(ActionExecutionStatus.Pending, ActionExecutionStatus.Skipped);
            _repository.UpdateActionStateAtomic(executionId, actId, ActionExecutionStatus.Skipped, 0, FailureClassification.None, "Skipped due to branch condition.", "ActionSkipped");
        }
        if (node.Children is not null) foreach (var c in node.Children) MarkSubtreeSkipped(executionId, c);
        if (node.ThenChildren is not null) foreach (var c in node.ThenChildren) MarkSubtreeSkipped(executionId, c);
        if (node.ElseChildren is not null) foreach (var c in node.ElseChildren) MarkSubtreeSkipped(executionId, c);
        if (node.BodyChildren is not null) foreach (var c in node.BodyChildren) MarkSubtreeSkipped(executionId, c);
    }

    private static bool VerifyRuntimeOutputsMatchContract(ActionContract contract, IReadOnlyDictionary<string, object?> outputs)
    {
        foreach (var kv in contract.OutputTypes)
        {
            if (!outputs.TryGetValue(kv.Key, out object? val) || val is null)
            {
                return false;
            }
            bool typeOk = kv.Value switch
            {
                IrConstants.TypeString => val is string,
                IrConstants.TypeNumber => val is int or long or double or decimal,
                IrConstants.TypeBoolean => val is bool,
                IrConstants.TypeArrayString => val is IEnumerable<string>,
                IrConstants.TypeTable2D => val is List<List<string>>,
                _ => false
            };
            if (!typeOk)
            {
                return false;
            }
        }
        return true;
    }

    private static bool EvaluateCondition(
        ConditionExpression condition,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> runtimeOutputs)
    {
        string leftVal = string.Empty;
        if (condition.LeftRef is not null &&
            runtimeOutputs.TryGetValue(condition.LeftRef.SourceNodeId, out var outputs) &&
            outputs.TryGetValue(condition.LeftRef.OutputName, out object? raw))
        {
            leftVal = raw?.ToString() ?? string.Empty;
        }

        return condition.Operator switch
        {
            "Equals" => string.Equals(leftVal, condition.RightLiteral ?? string.Empty, StringComparison.Ordinal),
            "NotEquals" => !string.Equals(leftVal, condition.RightLiteral ?? string.Empty, StringComparison.Ordinal),
            "NotEmpty" => !string.IsNullOrEmpty(leftVal),
            _ => false
        };
    }

    private static IReadOnlyList<string> ResolveArrayReference(
        DataReference reference,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> runtimeOutputs)
    {
        if (runtimeOutputs.TryGetValue(reference.SourceNodeId, out var outputs) &&
            outputs.TryGetValue(reference.OutputName, out object? raw) &&
            raw is IEnumerable<string> enumerable)
        {
            return enumerable.ToList();
        }
        return Array.Empty<string>();
    }
}
