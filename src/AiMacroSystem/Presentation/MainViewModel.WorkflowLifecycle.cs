// Module: @role: ワークフローのIR静的検証・ドラフト保存・正式バージョンコミット・承認・却下フローを担当するPartialクラス
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AiMacroSystem.Application;
using AiMacroSystem.Domain;

namespace AiMacroSystem.Presentation;

public sealed partial class MainViewModel
{
    public void ValidateCurrentEditorIr()
    {
        CurrentValidationIssues.Clear();
        FriendlyValidationIssues.Clear();
        CurrentPlannedActions.Clear();
        FriendlyPlanItems.Clear();
        _currentEvaluatedPlan = null;

        var validator = new IrValidator();
        var (res, def) = validator.ValidateJson(EditorIrJson);
        foreach (var issue in res.Issues)
        {
            CurrentValidationIssues.Add(issue);
            FriendlyValidationIssues.Add(StepBlockFormatter.ToFriendlyIssue(issue));
        }

        if (res.IsValid && def is not null)
        {
            string tempVerId = _currentSavedVersionId ?? "preview-ver";
            var (planVal, plan) = _policyEngine.BuildAndEvaluatePlan(tempVerId, def);
            foreach (var issue in planVal.Issues)
            {
                CurrentValidationIssues.Add(issue);
                FriendlyValidationIssues.Add(StepBlockFormatter.ToFriendlyIssue(issue));
            }
            if (plan is not null)
            {
                _currentEvaluatedPlan = plan;
                foreach (var act in plan.PlannedActions)
                {
                    CurrentPlannedActions.Add(act);
                    FriendlyPlanItems.Add(StepBlockFormatter.ToFriendlyPlanItem(act));
                }
            }
        }

        StatusBanner = CurrentValidationIssues.Count == 0
            ? $"✅ 安全チェックOK（{CurrentPlannedActions.Count} ステップ）"
            : $"⚠️ 要確認が {CurrentValidationIssues.Count} 件あります（「確認・結果」タブを参照）";
    }

    public void SaveCurrentAsDraft()
    {
        var (_, def) = new IrValidator().ValidateJson(EditorIrJson);
        if (def is null)
        {
            StatusBanner = "❌ JSON構文エラーのためドラフト保存できません。";
            return;
        }

        var existingLatest = _repository.GetLatestVersion(def.WorkflowId);
        if (existingLatest is null)
        {
            var initial = _workflowService.CreateInitialWorkflow(def);
            _currentSavedVersionId = initial.VersionId;
        }

        var latestAfter = _repository.GetLatestVersion(def.WorkflowId);
        var draft = _workflowService.SaveDraft(def.WorkflowId, latestAfter?.VersionId, def);
        RefreshAllListsFromDatabase();
        StatusBanner = $"📝 ドラフト「{draft.DraftId}」を正式版とは分離して保存しました。";
    }

    public void RejectCurrentWorkflow()
    {
        if (_currentSavedVersionId is null)
        {
            SaveCurrentWorkflowAsVersion();
        }
        if (_currentSavedVersionId is null || _currentEvaluatedPlan is null)
        {
            return;
        }

        var rejected = _repository.SaveApproval(
            _currentSavedVersionId,
            _currentEvaluatedPlan.PlanHash,
            string.IsNullOrWhiteSpace(ApproverNameInput) ? "ユーザー本人" : ApproverNameInput.Trim(),
            ApprovalDecision.Rejected,
            Array.Empty<string>());

        _currentApprovalId = rejected.ApprovalId;
        CurrentApprovalStatusText = $"🚫 却下済み（審査者: {rejected.Approver} / 時刻: {rejected.CreatedAt.ToLocalTime():HH:mm:ss}）";
        StatusBanner = "🚫 この実行計画を却下しました。再承認されるまで実行できません。";
    }

    public void SaveCurrentWorkflowAsVersion()
    {
        SyncMetadataIntoEditorIrJson();
        ValidateCurrentEditorIr();
        if (CurrentValidationIssues.Count > 0)
        {
            StatusBanner = "❌ エラーがあるため保存できません。「④ 検証・実行前レビュー・承認」タブで内容を確認してください。";
            return;
        }

        var (_, def) = new IrValidator().ValidateJson(EditorIrJson);
        if (def is null) return;

        var existingLatest = _repository.GetLatestVersion(def.WorkflowId);
        WorkflowVersionRecord saved;
        if (existingLatest is null)
        {
            saved = _workflowService.CreateInitialWorkflow(def);
        }
        else
        {
            var draft = _workflowService.SaveDraft(def.WorkflowId, existingLatest.VersionId, def);
            saved = _workflowService.CommitDraftAsNewVersion(draft.DraftId);
        }

        _currentSavedVersionId = saved.VersionId;
        var (_, plan) = _policyEngine.BuildAndEvaluatePlan(saved.VersionId, def);
        _currentEvaluatedPlan = plan;
        _currentApprovalId = null;
        CurrentApprovalStatusText = $"バージョン v{saved.VersionNumber} を保存しました。続けて「✅ 承認する」を押してください。";

        RefreshAllListsFromDatabase();
        SelectedMacroInList = WorkflowVersions.FirstOrDefault(v => string.Equals(v.VersionId, saved.VersionId, StringComparison.Ordinal))
            ?? WorkflowVersions.FirstOrDefault();
        StatusBanner = $"💾 マクロ「{saved.Name} (v{saved.VersionNumber})」を保存しました！";
    }

    private void SyncMetadataIntoEditorIrJson()
    {
        if (string.IsNullOrWhiteSpace(EditorIrJson))
        {
            if (EasySteps.Count > 0)
            {
                RebuildIrJsonFromEasySteps();
            }
            return;
        }

        try
        {
            var def = JsonSerializer.Deserialize<WorkflowDefinition>(EditorIrJson);
            if (def is null) return;

            bool changed = false;
            if (!string.IsNullOrWhiteSpace(WorkflowIdInput) && !string.Equals(def.WorkflowId, WorkflowIdInput.Trim(), StringComparison.Ordinal))
            {
                def.WorkflowId = WorkflowIdInput.Trim();
                changed = true;
            }
            if (!string.IsNullOrWhiteSpace(WorkflowNameInput) && !string.Equals(def.Name, WorkflowNameInput.Trim(), StringComparison.Ordinal))
            {
                def.Name = WorkflowNameInput.Trim();
                changed = true;
            }
            if (changed)
            {
                EditorIrJson = JsonSerializer.Serialize(def, new JsonSerializerOptions { WriteIndented = true });
            }
        }
        catch
        {
        }
    }

    public void ApproveCurrentWorkflow()
    {
        if (_currentSavedVersionId is null)
        {
            SaveCurrentWorkflowAsVersion();
        }
        if (_currentSavedVersionId is null || _currentEvaluatedPlan is null)
        {
            return;
        }

        var highRiskNodes = ApproveHighRiskChecked
            ? _currentEvaluatedPlan.PlannedActions.Where(a => a.RequiresIndividualApproval).Select(a => a.NodeId).ToList()
            : new List<string>();

        var approval = _repository.SaveApproval(
            _currentSavedVersionId,
            _currentEvaluatedPlan.PlanHash,
            string.IsNullOrWhiteSpace(ApproverNameInput) ? "ユーザー本人" : ApproverNameInput.Trim(),
            ApprovalDecision.Approved,
            highRiskNodes);

        _currentApprovalId = approval.ApprovalId;
        CurrentApprovalStatusText = $"✅ 承認済み（承認者: {approval.Approver} / 時刻: {approval.CreatedAt.ToLocalTime():HH:mm:ss}）";
        StatusBanner = "✅ マクロの承認が完了しました！「▶ マクロを実行する」ボタンで自動実行できます。";
    }

    public void LoadSelectedVersionIntoEditor(WorkflowVersionRecord version)
    {
        EditorIrJson = version.IrJson;
        WorkflowIdInput = version.WorkflowId;
        WorkflowNameInput = version.Name;
        _currentSavedVersionId = version.VersionId;
        _currentApprovalId = null;
        RebuildEasyStepsFromIrJson(version.IrJson);
        ValidateCurrentEditorIr();
        StatusBanner = $"📂 マクロ「{version.Name} (v{version.VersionNumber})」を読み込みました";
    }
}
