// Module: @role: 単一ワークフロー排他制御・状態機械駆動・アクション境界での停止/キャンセル・Unknown自動再試行禁止
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AiMacroSystem.Domain;
using AiMacroSystem.Infrastructure;

namespace AiMacroSystem.Application;

public sealed partial class ExecutionEngine
{
    private readonly SqliteRepository _repository;
    private readonly PolicyEngine _policyEngine;
    private readonly ApprovalService _approvalService;
    private readonly RecoveryService _recoveryService;
    private readonly IReadOnlyDictionary<string, IActionAdapter> _adapters;

    private readonly object _lockSync = new();
    private string? _activeExecutionId;
    private volatile bool _pauseRequested;
    private volatile bool _cancelRequested;
    private TaskCompletionSource<bool>? _resumeSignal;

    public event Func<string, string, Task>? ActionNodeCompletedAsync;

    public ExecutionEngine(
        SqliteRepository repository,
        PolicyEngine policyEngine,
        ApprovalService approvalService,
        RecoveryService recoveryService,
        IReadOnlyDictionary<string, IActionAdapter> adapters)
    {
        _repository = repository;
        _policyEngine = policyEngine;
        _approvalService = approvalService;
        _recoveryService = recoveryService;
        _adapters = adapters;
    }

    public void RequestPause(string executionId)
    {
        lock (_lockSync)
        {
            if (!string.Equals(_activeExecutionId, executionId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Execution '{executionId}' is not currently active.");
            }
            if (!_pauseRequested)
            {
                _pauseRequested = true;
                _resumeSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _repository.AppendExecutionEvent(executionId, null, "PauseRequested", "User requested cooperative pause at next action boundary.");
            }
        }
    }

    public void RequestResume(string executionId)
    {
        lock (_lockSync)
        {
            if (!string.Equals(_activeExecutionId, executionId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Execution '{executionId}' is not currently active.");
            }
            _pauseRequested = false;
            _resumeSignal?.TrySetResult(true);
        }
    }

    public void RequestCancel(string executionId)
    {
        lock (_lockSync)
        {
            if (!string.Equals(_activeExecutionId, executionId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Execution '{executionId}' is not currently active.");
            }
            _cancelRequested = true;
            _pauseRequested = false;
            _resumeSignal?.TrySetResult(false);
            _repository.AppendExecutionEvent(executionId, null, "CancelRequested", "User requested cooperative cancellation at next action boundary.");
        }
    }

    public async Task<ExecutionRecord> ExecuteWorkflowVersionAsync(
        string versionId,
        string? approvalId,
        CancellationToken ct = default)
    {
        // 1. 異常終了後の復旧未完了チェックと単一ワークフロー排他制御（仕様12.3 / 12.4 / 18.7 / 18.8）
        lock (_lockSync)
        {
            if (_recoveryService.HasUnresolvedRecovery())
            {
                throw new InvalidOperationException("New workflow execution is prohibited until startup recovery verification and user decision are completed.");
            }
            if (_activeExecutionId is not null)
            {
                throw new InvalidOperationException($"Another workflow execution ('{_activeExecutionId}') is already active or paused. Concurrent execution is prohibited.");
            }
            _pauseRequested = false;
            _cancelRequested = false;
            _activeExecutionId = "RESERVING";
        }

        string executionId = $"exec-{Guid.NewGuid():N}";
        try
        {
            var versionRecord = _repository.GetVersionById(versionId)
                ?? throw new InvalidOperationException($"Workflow version '{versionId}' does not exist.");

            var (syntaxResult, definition) = new IrValidator().ValidateJson(versionRecord.IrJson);
            if (!syntaxResult.IsValid || definition is null)
            {
                throw new InvalidOperationException($"Stored Workflow IR failed validation: {syntaxResult.Issues.FirstOrDefault()?.Message}");
            }

            var (_, plan) = _policyEngine.BuildAndEvaluatePlan(versionId, definition);
            string planHash = plan?.PlanHash ?? "INVALID_PLAN";
            ApprovalRecord? approval = approvalId is not null ? _repository.GetApprovalById(approvalId) : null;

            _repository.CreateExecutionAtomic(executionId, versionId, planHash, ExecutionStatus.Pending, "Execution accepted in Pending state.", approval?.ApprovalId);
            lock (_lockSync)
            {
                _activeExecutionId = executionId;
            }

            // Pending -> Validating
            TransitionExecution(executionId, ExecutionStatus.Pending, ExecutionStatus.Validating, "Validating IR, ExecutionPlan, and Policy.");

            if (plan is null)
            {
                TransitionExecution(executionId, ExecutionStatus.Validating, ExecutionStatus.Failed, "Execution plan validation failed.", ended: true);
                return _repository.GetExecutionById(executionId)!;
            }

            // Validating -> AwaitingApproval
            TransitionExecution(executionId, ExecutionStatus.Validating, ExecutionStatus.AwaitingApproval, "Checking approval validity for version and planHash.");

            var (isApproved, approvalReason) = _approvalService.VerifyApprovalForPlan(plan, approval);
            if (!isApproved)
            {
                TransitionExecution(executionId, ExecutionStatus.AwaitingApproval, ExecutionStatus.Failed, $"Approval check failed: {approvalReason}", ended: true);
                return _repository.GetExecutionById(executionId)!;
            }

            // AwaitingApproval -> Ready -> Running
            TransitionExecution(executionId, ExecutionStatus.AwaitingApproval, ExecutionStatus.Ready, "Validation and approval verified.");
            TransitionExecution(executionId, ExecutionStatus.Ready, ExecutionStatus.Running, "Starting sequential execution.");

            var runtimeOutputs = new Dictionary<string, IReadOnlyDictionary<string, object?>>(StringComparer.Ordinal);
            var outcomeFlags = new ExecutionOutcomeFlags();

            await ExecuteNodeRecursiveAsync(executionId, definition.Root!, definition.Policy, plan, runtimeOutputs, outcomeFlags, ct);

            if ((_cancelRequested || ct.IsCancellationRequested) && !outcomeFlags.CancelConfirmedLogged)
            {
                outcomeFlags.WasCancelled = true;
                outcomeFlags.CancelConfirmedLogged = true;
                _repository.AppendExecutionEvent(executionId, null, "CancelConfirmed", "Cooperative cancellation confirmed after action completion.");
            }

            ExecutionStatus currentExecStatus = _repository.GetExecutionById(executionId)!.Status;
            ExecutionStatus finalStatus = ExecutionStateMachine.ResolveCompetingTerminal(
                cancelRequested: _cancelRequested || outcomeFlags.WasCancelled,
                hasUnknownAction: outcomeFlags.HasUnknown,
                hasFailedAction: outcomeFlags.HasFailed);

            TransitionExecution(executionId, currentExecStatus, finalStatus, $"Execution finished with status '{finalStatus}'.", ended: true);

            // Unknown終端となった場合は新規実行をブロックするためRecoveryRecordを登録
            if (finalStatus == ExecutionStatus.Unknown)
            {
                _repository.CreateRecoveryRecord(executionId, RecoveryStatus.Pending, "Execution ended in Unknown state due to uncertain external side effect.");
            }

            return _repository.GetExecutionById(executionId)!;
        }
        finally
        {
            lock (_lockSync)
            {
                _activeExecutionId = null;
                _pauseRequested = false;
                _cancelRequested = false;
                _resumeSignal = null;
            }
        }
    }

    // 正式版を直接変更しない一時編集のExecutionPlanを再検証・承認確認して実行する（仕様12）
    public async Task<ExecutionRecord> ExecuteTemporaryPlanAsync(
        ExecutionPlan temporaryPlan,
        string? approvalId,
        CancellationToken ct = default)
    {
        lock (_lockSync)
        {
            if (_recoveryService.HasUnresolvedRecovery())
            {
                throw new InvalidOperationException("New workflow execution is prohibited until startup recovery verification and user decision are completed.");
            }
            if (_activeExecutionId is not null)
            {
                throw new InvalidOperationException($"Another workflow execution ('{_activeExecutionId}') is already active or paused. Concurrent execution is prohibited.");
            }
            _pauseRequested = false;
            _cancelRequested = false;
            _activeExecutionId = "RESERVING";
        }

        string executionId = $"exec-{Guid.NewGuid():N}";
        try
        {
            var (reval, rebuiltPlan) = _policyEngine.BuildAndEvaluatePlan(temporaryPlan.VersionId, temporaryPlan.Definition);
            string planHash = rebuiltPlan?.PlanHash ?? temporaryPlan.PlanHash;
            ApprovalRecord? approval = approvalId is not null ? _repository.GetApprovalById(approvalId) : null;

            _repository.CreateExecutionAtomic(executionId, temporaryPlan.VersionId, planHash, ExecutionStatus.Pending, "Temporary edit execution accepted in Pending state.", approval?.ApprovalId);
            lock (_lockSync)
            {
                _activeExecutionId = executionId;
            }

            TransitionExecution(executionId, ExecutionStatus.Pending, ExecutionStatus.Validating, "Re-validating temporary edit ExecutionPlan and Policy.");
            if (!reval.IsValid || rebuiltPlan is null || !string.Equals(rebuiltPlan.PlanHash, temporaryPlan.PlanHash, StringComparison.Ordinal))
            {
                TransitionExecution(executionId, ExecutionStatus.Validating, ExecutionStatus.Failed, "Temporary edit execution plan validation failed.", ended: true);
                return _repository.GetExecutionById(executionId)!;
            }

            TransitionExecution(executionId, ExecutionStatus.Validating, ExecutionStatus.AwaitingApproval, "Checking approval validity for temporary edit planHash.");
            var (isApproved, approvalReason) = _approvalService.VerifyApprovalForPlan(rebuiltPlan, approval);
            if (!isApproved)
            {
                TransitionExecution(executionId, ExecutionStatus.AwaitingApproval, ExecutionStatus.Failed, $"Approval check failed: {approvalReason}", ended: true);
                return _repository.GetExecutionById(executionId)!;
            }

            TransitionExecution(executionId, ExecutionStatus.AwaitingApproval, ExecutionStatus.Ready, "Temporary edit validation and approval verified.");
            TransitionExecution(executionId, ExecutionStatus.Ready, ExecutionStatus.Running, "Starting sequential execution of temporary edit plan.");

            var runtimeOutputs = new Dictionary<string, IReadOnlyDictionary<string, object?>>(StringComparer.Ordinal);
            var outcomeFlags = new ExecutionOutcomeFlags();

            await ExecuteNodeRecursiveAsync(executionId, rebuiltPlan.Definition.Root!, rebuiltPlan.Definition.Policy, rebuiltPlan, runtimeOutputs, outcomeFlags, ct);

            if ((_cancelRequested || ct.IsCancellationRequested) && !outcomeFlags.CancelConfirmedLogged)
            {
                outcomeFlags.WasCancelled = true;
                outcomeFlags.CancelConfirmedLogged = true;
                _repository.AppendExecutionEvent(executionId, null, "CancelConfirmed", "Cooperative cancellation confirmed after action completion.");
            }

            ExecutionStatus currentExecStatus = _repository.GetExecutionById(executionId)!.Status;
            ExecutionStatus finalStatus = ExecutionStateMachine.ResolveCompetingTerminal(
                cancelRequested: _cancelRequested || outcomeFlags.WasCancelled,
                hasUnknownAction: outcomeFlags.HasUnknown,
                hasFailedAction: outcomeFlags.HasFailed);

            TransitionExecution(executionId, currentExecStatus, finalStatus, $"Execution finished with status '{finalStatus}'.", ended: true);
            if (finalStatus == ExecutionStatus.Unknown)
            {
                _repository.CreateRecoveryRecord(executionId, RecoveryStatus.Pending, "Execution ended in Unknown state due to uncertain external side effect.");
            }

            return _repository.GetExecutionById(executionId)!;
        }
        finally
        {
            lock (_lockSync)
            {
                _activeExecutionId = null;
                _pauseRequested = false;
                _cancelRequested = false;
                _resumeSignal = null;
            }
        }
    }

    private void TransitionExecution(string executionId, ExecutionStatus from, ExecutionStatus to, string details, bool ended = false)
    {
        ExecutionStateMachine.EnsureValidTransition(from, to);
        _repository.UpdateExecutionStateAtomic(
            executionId,
            to,
            $"ExecutionState:{from}->{to}",
            details,
            ended ? DateTimeOffset.UtcNow : null);
    }

    private sealed class ExecutionOutcomeFlags
    {
        public bool HasFailed { get; set; }
        public bool HasUnknown { get; set; }
        public bool WasCancelled { get; set; }
        public bool CancelConfirmedLogged { get; set; }

        public bool ShouldStop => HasFailed || HasUnknown || WasCancelled;
    }
}
