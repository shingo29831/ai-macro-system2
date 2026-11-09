// Module: @role: MainViewModelのワークフロー実行・一時停止・キャンセル・対象ウィンドウ割り当て・復旧処理を担当するPartialクラス
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AiMacroSystem.Application;
using AiMacroSystem.Domain;
using AiMacroSystem.Infrastructure;

namespace AiMacroSystem.Presentation;

public sealed partial class MainViewModel
{
    public async Task ExecuteSelectedMacroFromListAsync()
    {
        if (_currentSavedVersionId is null && EasySteps.Count > 0)
        {
            SaveCurrentWorkflowAsVersion();
        }

        var target = SelectedMacroInList ?? WorkflowVersions.FirstOrDefault();
        if (target is null)
        {
            StatusBanner = "⚠️ 実行するマクロを一覧から選択してください";
            return;
        }
        LoadSelectedVersionIntoEditor(target);
        ApproveCurrentWorkflow();
        await ExecuteApprovedMacroAsync();
    }

    public async Task ExecuteOneClickAllInOneAsync()
    {
        try
        {
            if (_currentSavedVersionId is null)
            {
                SaveCurrentWorkflowAsVersion();
            }
            if (_currentApprovalId is null)
            {
                ApproveCurrentWorkflow();
            }
            await ExecuteApprovedMacroAsync();
        }
        catch (Exception ex)
        {
            StatusBanner = $"❌ 実行ブロック: {ex.Message}";
        }
    }

    public async Task ExecuteTemporaryEditWithoutCommittingVersionAsync()
    {
        ValidateCurrentEditorIr();
        if (CurrentValidationIssues.Count > 0 || _currentEvaluatedPlan is null)
        {
            StatusBanner = "❌ 一時編集内容に検証エラーがあるため実行できません。";
            return;
        }

        string baseVerId = _currentSavedVersionId ?? WorkflowVersions.FirstOrDefault()?.VersionId ?? string.Empty;
        if (string.IsNullOrWhiteSpace(baseVerId))
        {
            SaveCurrentWorkflowAsVersion();
            baseVerId = _currentSavedVersionId!;
        }

        var (_, def) = new IrValidator().ValidateJson(EditorIrJson);
        if (def is null) return;

        var (val, tempPlan) = _policyEngine.BuildAndEvaluatePlan(baseVerId, def);
        if (!val.IsValid || tempPlan is null) return;

        var highRiskNodes = ApproveHighRiskChecked
            ? tempPlan.PlannedActions.Where(a => a.RequiresIndividualApproval).Select(a => a.NodeId).ToList()
            : new List<string>();

        var tempApproval = _repository.SaveApproval(
            baseVerId,
            tempPlan.PlanHash,
            string.IsNullOrWhiteSpace(ApproverNameInput) ? "ユーザー本人" : ApproverNameInput.Trim(),
            ApprovalDecision.Approved,
            highRiskNodes);

        var execRecord = await _executionEngine.ExecuteTemporaryPlanAsync(tempPlan, tempApproval.ApprovalId);
        RefreshExecutionMonitor(execRecord.ExecutionId);
        RefreshAllListsFromDatabase();
        StatusBanner = $"✅ 正式版を変更せず一時編集プラン({tempPlan.PlanHash[..8]})を再検証・承認して実行しました: {execRecord.Status}";
    }

    public async Task ExecuteApprovedMacroAsync()
    {
        if (_currentSavedVersionId is null || _currentApprovalId is null)
        {
            StatusBanner = "⚠️ 実行する前に「💾 保存」と「✅ 承認」を行ってください（または「▶ ワンクリック実行」を押してください）。";
            return;
        }

        async Task OnStepCompleted(string execId, string completedNodeId)
        {
            _activeRunningExecutionId = execId;
            RunningProgressText = $"実行中: {WorkflowNameInput} ({completedNodeId} 完了)";
            await Task.Delay(350);
        }

        var promptRequests = BuildAppTargetPromptRequests(includeThumbnails: ConfirmExecutionTargetsAsync is not null);
        if (promptRequests.Count > 0 && ConfirmExecutionTargetsAsync is not null)
        {
            var selectedTargets = await ConfirmExecutionTargetsAsync(promptRequests);
            if (selectedTargets is null)
            {
                StatusBanner = "⏹ 実行対象ウィンドウの選択がキャンセルされたため、マクロ実行を中止しました。";
                return;
            }
            ApplyWindowTargetSelections(selectedTargets);
        }
        else
        {
            _liveBrowserDriver?.ConfigureTargetSelection(null);
            _liveCustomAppDriver?.ConfigureTargetSelections(null);
        }

        _executionEngine.ActionNodeCompletedAsync += OnStepCompleted;
        try
        {
            IsExecutingMacro = true;
            RunningProgressText = $"実行中: {WorkflowNameInput} (全 {EasySteps.Count} ブロック)";
            StatusBanner = "🚀 マクロを実行中です...";
            if (_liveBrowserDriver?.EnableOsAutomation == true)
            {
                await Task.Delay(180);
            }
            var execRecord = await _executionEngine.ExecuteWorkflowVersionAsync(_currentSavedVersionId, _currentApprovalId);
            _activeRunningExecutionId = null;

            RefreshExecutionMonitor(execRecord.ExecutionId);
            RefreshAllListsFromDatabase();

            StatusBanner = execRecord.Status switch
            {
                ExecutionStatus.Succeeded => $"🎉 マクロが正常に完了しました！（Excel出力先: {StepExcelPathInput}）",
                ExecutionStatus.Cancelled => "⏹ マクロの実行を安全にキャンセルしました。",
                ExecutionStatus.Unknown => "⚠️ 外部操作の成否が不明(Unknown)となったため安全停止しました。「⑤ 実行履歴・トラブル復旧」で確認してください。",
                _ => $"❌ マクロが停止しました（状態: {execRecord.Status}）。詳細メッセージをモニターで確認してください。"
            };
        }
        catch (Exception ex)
        {
            StatusBanner = $"⛔ 安全ロックにより実行が拒否されました: {ex.Message}";
        }
        finally
        {
            _executionEngine.ActionNodeCompletedAsync -= OnStepCompleted;
            IsExecutingMacro = false;
        }
    }

    public void PauseActiveMacro()
    {
        var active = _repository.GetNonTerminalExecutions().FirstOrDefault();
        string? targetId = _activeRunningExecutionId ?? active?.ExecutionId;
        if (targetId is null)
        {
            StatusBanner = "ℹ️ 現在実行中のマクロはありません。";
            return;
        }
        try
        {
            _executionEngine.RequestPause(targetId);
            StatusBanner = "⏸ 一時停止を要求しました。現在のステップが終わり次第、安全に一時停止します。";
        }
        catch (Exception ex)
        {
            StatusBanner = $"⚠️ 一時停止エラー: {ex.Message}";
        }
    }

    public void ResumeActiveMacro()
    {
        var active = _repository.GetNonTerminalExecutions().FirstOrDefault();
        string? targetId = _activeRunningExecutionId ?? active?.ExecutionId;
        if (targetId is null) return;
        try
        {
            _executionEngine.RequestResume(targetId);
            StatusBanner = "▶ マクロの実行を再開しました。";
        }
        catch (Exception ex)
        {
            StatusBanner = $"⚠️ 再開エラー: {ex.Message}";
        }
    }

    public void CancelActiveMacro()
    {
        var active = _repository.GetNonTerminalExecutions().FirstOrDefault();
        string? targetId = _activeRunningExecutionId ?? active?.ExecutionId;
        if (targetId is null) return;
        try
        {
            _executionEngine.RequestCancel(targetId);
            StatusBanner = "⏹ キャンセルを要求しました。安全に停止します。";
        }
        catch (Exception ex)
        {
            StatusBanner = $"⚠️ キャンセルエラー: {ex.Message}";
        }
    }

    public IReadOnlyList<AppTargetPromptRequest> BuildAppTargetPromptRequests(bool includeThumbnails = true)
    {
        var requests = new List<AppTargetPromptRequest>();
        var actionNodes = new List<WorkflowNode>();

        try
        {
            var def = JsonSerializer.Deserialize<WorkflowDefinition>(EditorIrJson);
            if (def?.Root is not null)
            {
                StepBlockFormatter.CollectActionNodesRecursive(def.Root, actionNodes);
            }
        }
        catch
        {
        }

        if (actionNodes.Count == 0 && EasySteps.Count > 0)
        {
            actionNodes.AddRange(EasySteps.Select(s => s.UnderlyingNode));
        }

        int chromeCount = actionNodes.Count(n => n.ActionType is not null && n.ActionType.StartsWith("Chrome.", StringComparison.Ordinal));
        if (chromeCount > 0)
        {
            string? firstUrl = actionNodes
                .Where(n => n.ActionType == IrConstants.ActionChromeNavigate && n.Args.HasValue)
                .Select(n => n.Args!.Value.Deserialize<ChromeNavigateArgs>()?.Url)
                .FirstOrDefault(u => !string.IsNullOrWhiteSpace(u));

            var browserWindows = WindowThumbnailCaptureService.GetOpenWindowsForTarget("Chrome", null, includeThumbnails);
            requests.Add(new AppTargetPromptRequest
            {
                TargetKey = "Browser",
                AppCategory = "Chrome",
                ApplicationName = "Chrome",
                DisplayTitle = "ブラウザ操作 (Chrome / Edge / 内蔵スマートブラウザ)",
                ActionCount = chromeCount,
                InitialUrlOrPath = firstUrl,
                AvailableWindows = browserWindows
            });
        }

        int excelCount = actionNodes.Count(n => n.ActionType is not null && n.ActionType.StartsWith("Excel.", StringComparison.Ordinal));
        if (excelCount > 0)
        {
            var excelWindows = WindowThumbnailCaptureService.GetOpenWindowsForTarget("Excel", null, includeThumbnails);
            requests.Add(new AppTargetPromptRequest
            {
                TargetKey = "Excel",
                AppCategory = "Excel",
                ApplicationName = "Excel",
                DisplayTitle = "Excel ブック操作",
                ActionCount = excelCount,
                InitialUrlOrPath = StepExcelPathInput,
                AvailableWindows = excelWindows
            });
        }

        var customGroups = actionNodes
            .Where(n => n.ActionType is not null && n.ActionType.StartsWith("CustomApp.", StringComparison.Ordinal))
            .GroupBy(n =>
            {
                if (!n.Args.HasValue) return "独自アプリ";
                return n.ActionType switch
                {
                    IrConstants.ActionCustomAppClick => n.Args.Value.Deserialize<CustomAppClickArgs>()?.ApplicationName ?? "独自アプリ",
                    IrConstants.ActionCustomAppFill => n.Args.Value.Deserialize<CustomAppFillArgs>()?.ApplicationName ?? "独自アプリ",
                    IrConstants.ActionCustomAppReadText => n.Args.Value.Deserialize<CustomAppReadTextArgs>()?.ApplicationName ?? "独自アプリ",
                    _ => "独自アプリ"
                };
            }, StringComparer.OrdinalIgnoreCase);

        foreach (var grp in customGroups)
        {
            string appName = string.IsNullOrWhiteSpace(grp.Key) ? "独自アプリ" : grp.Key;
            var customWindows = WindowThumbnailCaptureService.GetOpenWindowsForTarget("CustomApp", appName, includeThumbnails);
            requests.Add(new AppTargetPromptRequest
            {
                TargetKey = $"CustomApp:{appName}",
                AppCategory = "CustomApp",
                ApplicationName = appName,
                DisplayTitle = $"独自アプリ「{appName}」",
                ActionCount = grp.Count(),
                AvailableWindows = customWindows
            });
        }

        return requests;
    }

    public void ApplyWindowTargetSelections(IReadOnlyDictionary<string, WindowTargetSelection> selections)
    {
        if (selections.TryGetValue("Browser", out var browserSel))
        {
            _liveBrowserDriver?.ConfigureTargetSelection(browserSel);
        }
        else
        {
            _liveBrowserDriver?.ConfigureTargetSelection(null);
        }

        _liveCustomAppDriver?.ConfigureTargetSelections(selections);
    }

    public void RefreshExecutionMonitor(string executionId)
    {
        CurrentRunActionStatuses.Clear();
        foreach (var act in _repository.GetActionExecutions(executionId))
        {
            CurrentRunActionStatuses.Add(act);
        }

        ExecutionHistoryEvents.Clear();
        foreach (var ev in _repository.GetExecutionEvents(executionId))
        {
            ExecutionHistoryEvents.Add(ev);
        }
    }

    public async Task SimulateAbnormalTerminationForDemoAsync()
    {
        if (_currentSavedVersionId is null)
        {
            SaveCurrentWorkflowAsVersion();
        }
        if (_currentSavedVersionId is null || _currentEvaluatedPlan is null) return;

        string crashedId = $"exec-crash-{DateTimeOffset.UtcNow:HHmmss}";
        _repository.CreateExecutionAtomic(crashedId, _currentSavedVersionId, _currentEvaluatedPlan.PlanHash, ExecutionStatus.Running, "異常終了（停電・強制終了）のシミュレーション");
        _repository.CreateActionExecutionAtomic($"act-{Guid.NewGuid():N}", crashedId, "step-1", ActionExecutionStatus.Running);

        await InitializeAndCheckRecoveryAsync();
    }

    public void ResolveFirstPendingRecovery(RecoveryStatus status, string note)
    {
        var first = PendingRecoveries.FirstOrDefault();
        if (first is null)
        {
            StatusBanner = "ℹ️ 現在復旧待ちの項目はありません。";
            return;
        }
        _recoveryService.ResolveRecovery(first.RecoveryId, status, note);
        RefreshAllListsFromDatabase();
        StatusBanner = "✅ トラブル復旧の確認が完了し、安全ロックが解除されました！新しいマクロを実行できます。";
    }

    public void RefreshAllListsFromDatabase()
    {
        WorkflowVersions.Clear();
        foreach (var v in _repository.GetAllLatestVersions()) WorkflowVersions.Add(v);

        WorkflowDrafts.Clear();
        foreach (var d in _repository.GetAllDrafts()) WorkflowDrafts.Add(d);

        AllExecutions.Clear();
        foreach (var e in _repository.GetAllExecutions()) AllExecutions.Add(e);

        PendingRecoveries.Clear();
        foreach (var r in _repository.GetPendingRecoveries()) PendingRecoveries.Add(r);

        RecoveryHistory.Clear();
        foreach (var rh in _repository.GetAllRecoveries()) RecoveryHistory.Add(rh);

        AiAuditLogs.Clear();
        foreach (var a in _repository.GetAiAuditLogs()) AiAuditLogs.Add(a);

        if (AllExecutions.Count > 0 && ExecutionHistoryEvents.Count == 0)
        {
            RefreshExecutionMonitor(AllExecutions[0].ExecutionId);
        }
    }
}
