"""Module: @role: 正式バージョンの不変管理・ドラフト競合防止・異常終了後の起動時状態照合と復旧制御"""
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiMacroSystem.Domain;
using AiMacroSystem.Infrastructure;

namespace AiMacroSystem.Application;

public sealed class WorkflowManagementService
{
    private readonly SqliteRepository _repository;
    private readonly IrValidator _validator;

    public WorkflowManagementService(SqliteRepository repository, IrValidator validator)
    {
        _repository = repository;
        _validator = validator;
    }

    public WorkflowVersionRecord CreateInitialWorkflow(WorkflowDefinition definition)
    {
        var validation = _validator.ValidateDefinition(definition);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException($"Cannot save invalid Workflow IR: {validation.Issues[0].Message}");
        }

        definition.Version = 1;
        string irJson = JsonSerializer.Serialize(definition);
        return _repository.CreateWorkflowAndInitialVersion(definition.WorkflowId, definition.Name, irJson, definition.SchemaVersion);
    }

    public WorkflowDraftRecord SaveDraft(string workflowId, string? baseVersionId, WorkflowDefinition draftDefinition)
    {
        var latest = _repository.GetLatestVersion(workflowId);
        if (latest is not null && !string.Equals(latest.VersionId, baseVersionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Stale draft detected: draft baseVersionId '{baseVersionId}' does not match latest committed version '{latest.VersionId}'. Overwrite prevented.");
        }

        string draftJson = JsonSerializer.Serialize(draftDefinition);
        return _repository.SaveOrUpdateDraft(workflowId, baseVersionId, draftJson);
    }

    public WorkflowVersionRecord CommitDraftAsNewVersion(string draftId)
    {
        var draft = _repository.GetDraftById(draftId)
            ?? throw new InvalidOperationException($"Draft '{draftId}' was not found.");

        var latest = _repository.GetLatestVersion(draft.WorkflowId);
        // 古いドラフトによる新しい正式バージョンの意図しない上書きを防止（仕様13.3 / 18.10）
        if (latest is not null && !string.Equals(latest.VersionId, draft.BaseVersionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Cannot commit stale draft '{draftId}' based on version '{draft.BaseVersionId}' because newer version '{latest.VersionId}' already exists.");
        }

        var (validation, definition) = _validator.ValidateJson(draft.DraftJson);
        if (!validation.IsValid || definition is null)
        {
            throw new InvalidOperationException($"Draft validation failed: {validation.Issues.FirstOrDefault()?.Message}");
        }

        int nextVersionNumber = (latest?.VersionNumber ?? 0) + 1;
        definition.Version = nextVersionNumber;
        string canonicalJson = JsonSerializer.Serialize(definition);

        return _repository.CommitNewVersionFromDraft(draft.DraftId, draft.WorkflowId, definition.Name, nextVersionNumber, canonicalJson, definition.SchemaVersion);
    }
}

public sealed class RecoveryService
{
    private readonly SqliteRepository _repository;
    private readonly IReadOnlyDictionary<string, IActionAdapter> _adapters;

    public RecoveryService(SqliteRepository repository, IReadOnlyDictionary<string, IActionAdapter> adapters)
    {
        _repository = repository;
        _adapters = adapters;
    }

    public bool HasUnresolvedRecovery() =>
        _repository.GetPendingRecoveries().Count > 0 || _repository.GetNonTerminalExecutions().Count > 0;

    // 起動時に未終端実行を検出し、自動再開せず外部状態を照合して復旧対象に登録する（仕様12.2 / 12.3）
    public async Task<IReadOnlyList<RecoveryRecord>> RunStartupRecoveryAsync(CancellationToken ct = default)
    {
        _repository.VerifyDatabaseIntegrity();

        var nonTerminalExecutions = _repository.GetNonTerminalExecutions();
        var createdRecoveries = new List<RecoveryRecord>();

        foreach (var exec in nonTerminalExecutions)
        {
            var version = _repository.GetVersionById(exec.VersionId);
            WorkflowDefinition? definition = version is not null
                ? JsonSerializer.Deserialize<WorkflowDefinition>(version.IrJson)
                : null;

            var nodeMap = new Dictionary<string, WorkflowNode>(StringComparer.Ordinal);
            if (definition?.Root is not null)
            {
                IndexNodes(definition.Root, nodeMap);
            }

            var actions = _repository.GetActionExecutions(exec.ExecutionId);
            bool anyUnknown = false;

            foreach (var act in actions)
            {
                if (ActionStateMachine.IsTerminal(act.Status))
                {
                    if (act.Status == ActionExecutionStatus.Unknown)
                    {
                        anyUnknown = true;
                    }
                    continue;
                }

                // 実行中だったアクションについて外部状態を照合し、照合不能なら未実行と断定せずUnknownとする
                ExternalStateProbeResult probe = ExternalStateProbeResult.OutcomeUnknown;
                if (nodeMap.TryGetValue(act.NodeId, out var node) &&
                    node.ActionType is not null &&
                    _adapters.TryGetValue(node.ActionType, out var adapter))
                {
                    probe = await adapter.ProbeExternalStateAsync(node, ct);
                }

                if (probe == ExternalStateProbeResult.ConfirmedCompleted)
                {
                    _repository.UpdateActionStateAtomic(
                        exec.ExecutionId,
                        act.ActionExecutionId,
                        ActionExecutionStatus.Succeeded,
                        act.AttemptCount,
                        FailureClassification.None,
                        "Startup recovery probe confirmed external operation completed.",
                        "RecoveryActionConfirmedCompleted");
                }
                else if (probe == ExternalStateProbeResult.ConfirmedNotExecuted)
                {
                    _repository.UpdateActionStateAtomic(
                        exec.ExecutionId,
                        act.ActionExecutionId,
                        ActionExecutionStatus.Cancelled,
                        act.AttemptCount,
                        FailureClassification.CancelledByUser,
                        "Startup recovery probe confirmed external operation had not started.",
                        "RecoveryActionConfirmedNotExecuted");
                }
                else
                {
                    anyUnknown = true;
                    _repository.UpdateActionStateAtomic(
                        exec.ExecutionId,
                        act.ActionExecutionId,
                        ActionExecutionStatus.Unknown,
                        act.AttemptCount,
                        FailureClassification.ExternalOutcomeUnknown,
                        "Abnormal termination detected during action execution; external outcome is unknown.",
                        "RecoveryActionMarkedUnknown");
                }
            }

            ExecutionStatus targetExecStatus = anyUnknown ? ExecutionStatus.Unknown : ExecutionStatus.Failed;
            if (exec.Status != targetExecStatus)
            {
                _repository.UpdateExecutionStateAtomic(
                    exec.ExecutionId,
                    targetExecStatus,
                    "RecoveryIsolatedAbnormalExecution",
                    $"Execution isolated by startup recovery (previous status: {exec.Status}). Automatic resume is prohibited.",
                    endedAt: DateTimeOffset.UtcNow);
            }

            var existingRecovery = _repository.GetPendingRecoveries()
                .FirstOrDefault(r => string.Equals(r.ExecutionId, exec.ExecutionId, StringComparison.Ordinal));

            if (existingRecovery is null)
            {
                var rec = _repository.CreateRecoveryRecord(
                    exec.ExecutionId,
                    RecoveryStatus.Pending,
                    $"Isolated after abnormal termination. External state unknown={anyUnknown}. Requires explicit user recovery decision.");
                createdRecoveries.Add(rec);
            }
            else
            {
                createdRecoveries.Add(existingRecovery);
            }
        }

        return createdRecoveries;
    }

    public RecoveryRecord ResolveRecovery(string recoveryId, RecoveryStatus resolution, string userDecisionNotes)
    {
        if (resolution == RecoveryStatus.Pending)
        {
            throw new ArgumentException("Resolution status cannot be Pending.", nameof(resolution));
        }

        return _repository.ResolveRecoveryRecordAtomic(recoveryId, resolution, userDecisionNotes);
    }

    private static void IndexNodes(WorkflowNode node, Dictionary<string, WorkflowNode> map)
    {
        map[node.NodeId] = node;
        if (node.Children is not null)
        {
            foreach (var c in node.Children) IndexNodes(c, map);
        }
        if (node.ThenChildren is not null)
        {
            foreach (var c in node.ThenChildren) IndexNodes(c, map);
        }
        if (node.ElseChildren is not null)
        {
            foreach (var c in node.ElseChildren) IndexNodes(c, map);
        }
        if (node.BodyChildren is not null)
        {
            foreach (var c in node.BodyChildren) IndexNodes(c, map);
        }
    }
}
