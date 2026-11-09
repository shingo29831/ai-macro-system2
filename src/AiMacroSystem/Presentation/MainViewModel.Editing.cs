// Module: @role: MainViewModelの手順ブロック編集・IR JSON相互変換・保存および承認フローを担当するPartialクラス
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AiMacroSystem.Application;
using AiMacroSystem.Domain;
using AiMacroSystem.Infrastructure;

namespace AiMacroSystem.Presentation;

public sealed partial class MainViewModel
{
    public void RemoveSpecificStep(EasyStepItem? item)
    {
        if (item is not null && EasySteps.Remove(item))
        {
            RebuildIrJsonFromEasySteps();
            StatusBanner = $"ブロック「{item.FriendlyTitle}」を削除しました";
        }
    }

    public void MoveStepUp(EasyStepItem? item)
    {
        if (item is null) return;
        int idx = EasySteps.IndexOf(item);
        if (idx > 0)
        {
            EasySteps.Move(idx, idx - 1);
            RebuildIrJsonFromEasySteps();
        }
    }

    public void MoveStepDown(EasyStepItem? item)
    {
        if (item is null) return;
        int idx = EasySteps.IndexOf(item);
        if (idx >= 0 && idx < EasySteps.Count - 1)
        {
            EasySteps.Move(idx, idx + 1);
            RebuildIrJsonFromEasySteps();
        }
    }

    public void LoadSampleMacro()
    {
        EasySteps.Clear();
        string sampleId = $"wf-sample-{DateTimeOffset.UtcNow:HHmmss}";
        string sampleName = "Webフォーム入力とExcel集計の自動化マクロ";

        _recordingService.StartRecording();
        RecordedEvents.Clear();
        RecordCrossAppBrowserToExcelLogs();
        _recordingService.StopRecording();

        GenerateMacroFromRecordingSynchronously();
        WorkflowIdInput = sampleId;
        WorkflowNameInput = sampleName;
        RebuildIrJsonFromEasySteps();
        StatusBanner = "複数アプリの操作ログ（ブラウザ→Excelデータ移動）からマクロを自動生成しました";
    }

    private void GenerateMacroFromRecordingSynchronously()
    {
        var rawList = _recordingService.GetRecordedEvents();
        var candidates = _normalizationService.Normalize(rawList);
        var policy = new PolicyDeclaration
        {
            AllowedOrigins = new List<OriginRule> { new() { Scheme = "https", Host = "example.com", Port = 443 } },
            AllowExternalAi = AllowExternalAiPolicy
        };
        var localGenerator = new LocalBuiltInAiProvider();
        string irJson = localGenerator.GenerateWorkflowIrJsonAsync(candidates, policy, null, default).GetAwaiter().GetResult();
        EditorIrJson = irJson;
        RebuildEasyStepsFromIrJson(irJson);
        ValidateCurrentEditorIr();
    }

    public void SelectStepForEditing(EasyStepItem? item)
    {
        foreach (var step in EasySteps)
        {
            step.IsSelectedForEdit = ReferenceEquals(step, item);
        }
        SelectedStepForEdit = item;

        if (item is null)
        {
            VisualBlocksUpdated?.Invoke();
            return;
        }

        SelectedActionKind = item.ActionType;
        if (item.UnderlyingNode.Args.HasValue)
        {
            JsonElement args = item.UnderlyingNode.Args.Value;
            switch (item.ActionType)
            {
                case IrConstants.ActionChromeNavigate:
                {
                    var a = args.Deserialize<ChromeNavigateArgs>();
                    if (a is not null) StepUrlInput = a.Url;
                    break;
                }
                case IrConstants.ActionChromeFill:
                {
                    var a = args.Deserialize<ChromeFillArgs>();
                    if (a is not null)
                    {
                        StepElementNameInput = a.Locator?.Label ?? a.Locator?.AccessibleName ?? string.Empty;
                        StepIsPasswordInput = a.IsSensitive;
                        if (!a.IsSensitive && a.Value is not null) StepFillValueInput = a.Value;
                    }
                    break;
                }
                case IrConstants.ActionChromeClick:
                {
                    var a = args.Deserialize<ChromeClickArgs>();
                    if (a is not null)
                    {
                        StepElementNameInput = a.Locator?.AccessibleName ?? a.Locator?.Label ?? string.Empty;
                    }
                    break;
                }
                case IrConstants.ActionChromeSubmit:
                {
                    var a = args.Deserialize<ChromeSubmitArgs>();
                    if (a is not null)
                    {
                        StepUrlInput = a.DestinationUrl;
                        StepElementNameInput = a.Locator?.AccessibleName ?? a.Locator?.Label ?? string.Empty;
                    }
                    break;
                }
                case IrConstants.ActionExcelReadRange:
                {
                    var a = args.Deserialize<ExcelReadRangeArgs>();
                    if (a is not null)
                    {
                        StepExcelPathInput = a.WorkbookPath;
                        StepExcelSheetInput = a.SheetName;
                        StepExcelRangeInput = a.RangeAddress;
                    }
                    break;
                }
                case IrConstants.ActionExcelWriteRange:
                {
                    var a = args.Deserialize<ExcelWriteRangeArgs>();
                    if (a is not null)
                    {
                        StepExcelPathInput = a.WorkbookPath;
                        StepExcelSheetInput = a.SheetName;
                        StepExcelRangeInput = a.RangeAddress;
                        if (a.Values is { Count: > 0 })
                        {
                            StepExcelWriteTextInput = string.Join("\n", a.Values.Select(r => string.Join(",", r)));
                        }
                    }
                    break;
                }
                case IrConstants.ActionCustomAppClick:
                {
                    var a = args.Deserialize<CustomAppClickArgs>();
                    if (a is not null)
                    {
                        StepCustomAppNameInput = a.ApplicationName;
                        StepElementNameInput = a.Locator?.AccessibleName ?? a.Locator?.Label ?? string.Empty;
                    }
                    break;
                }
                case IrConstants.ActionCustomAppFill:
                {
                    var a = args.Deserialize<CustomAppFillArgs>();
                    if (a is not null)
                    {
                        StepCustomAppNameInput = a.ApplicationName;
                        StepElementNameInput = a.Locator?.Label ?? a.Locator?.AccessibleName ?? string.Empty;
                        StepIsPasswordInput = a.IsSensitive;
                        if (!a.IsSensitive && a.Value is not null) StepFillValueInput = a.Value;
                    }
                    break;
                }
                case IrConstants.ActionCustomAppReadText:
                {
                    var a = args.Deserialize<CustomAppReadTextArgs>();
                    if (a is not null)
                    {
                        StepCustomAppNameInput = a.ApplicationName;
                        StepElementNameInput = a.Locator?.Label ?? a.Locator?.AccessibleName ?? string.Empty;
                    }
                    break;
                }
            }
        }

        VisualBlocksUpdated?.Invoke();
        StatusBanner = $"✏ ブロック #{item.StepNumber}「{item.FriendlyTitle}」を選択しました。中央パネルで内容を編集できます。";
    }

    public void UpdateSelectedStepFromBuilderUi()
    {
        if (SelectedStepForEdit is null)
        {
            AddStepFromBuilderUi();
            return;
        }

        int index = EasySteps.IndexOf(SelectedStepForEdit);
        if (index < 0)
        {
            AddStepFromBuilderUi();
            return;
        }

        var preservedCandidateIds = SelectedStepForEdit.UnderlyingNode.SourceCandidateIds;
        var preservedLocator = StepBlockFormatter.ExtractLocatorFromNode(SelectedStepForEdit.UnderlyingNode);
        var updatedItem = CreateStepItemFromInputs(
            index + 1,
            SelectedActionKind,
            StepUrlInput,
            StepElementNameInput,
            StepFillValueInput,
            StepIsPasswordInput,
            StepExcelPathInput,
            StepExcelSheetInput,
            StepExcelRangeInput,
            StepExcelWriteTextInput,
            preservedCandidateIds,
            preservedLocator);

        updatedItem.IsSelectedForEdit = true;
        EasySteps[index] = updatedItem;
        SelectedStepForEdit = updatedItem;

        RebuildIrJsonFromEasySteps();
        VisualBlocksUpdated?.Invoke();
        StatusBanner = $"✅ ブロック #{updatedItem.StepNumber}「{updatedItem.FriendlyTitle}」の編集内容を反映しました。";
    }

    public void AddStepFromBuilderUi()
    {
        AddEasyStepInternal(
            SelectedActionKind,
            StepUrlInput,
            StepElementNameInput,
            StepFillValueInput,
            StepIsPasswordInput,
            StepExcelPathInput,
            StepExcelSheetInput,
            StepExcelRangeInput,
            StepExcelWriteTextInput);

        RebuildIrJsonFromEasySteps();
        VisualBlocksUpdated?.Invoke();
        StatusBanner = $"➕ 手順ステップ {EasySteps.Count} を追加し、安全チェックを更新しました。";
    }

    public void RemoveLastEasyStep()
    {
        if (EasySteps.Count > 0)
        {
            EasySteps.RemoveAt(EasySteps.Count - 1);
            RebuildIrJsonFromEasySteps();
            StatusBanner = "↩ 最後のステップを削除しました。";
        }
    }

    public void ClearEasySteps()
    {
        EasySteps.Clear();
        EditorIrJson = string.Empty;
        CurrentValidationIssues.Clear();
        FriendlyValidationIssues.Clear();
        CurrentPlannedActions.Clear();
        FriendlyPlanItems.Clear();
        _currentSavedVersionId = null;
        _currentApprovalId = null;
        StatusBanner = "🗑 手順をすべてクリアしました。";
    }

    private void AddEasyStepInternal(
        string actionKind,
        string url,
        string elementName,
        string fillVal,
        bool isSens,
        string excelPath,
        string sheet,
        string range,
        string excelText)
    {
        var item = CreateStepItemFromInputs(
            EasySteps.Count + 1,
            actionKind,
            url,
            elementName,
            fillVal,
            isSens,
            excelPath,
            sheet,
            range,
            excelText,
            null);
        EasySteps.Add(item);
    }

    private EasyStepItem CreateStepItemFromInputs(
        int stepNumber,
        string actionKind,
        string url,
        string elementName,
        string fillVal,
        bool isSens,
        string excelPath,
        string sheet,
        string range,
        string excelText,
        List<string>? sourceCandidateIds,
        SemanticLocator? preservedLocator = null)
    {
        string customAppName = string.IsNullOrWhiteSpace(StepCustomAppNameInput) ? "基幹業務システム" : StepCustomAppNameInput.Trim();
        string safeElem = string.IsNullOrWhiteSpace(elementName) ? "対象要素" : elementName.Trim();
        string safeUrl = string.IsNullOrWhiteSpace(url) ? "https://example.com/portal" : url.Trim();
        string safeRange = string.IsNullOrWhiteSpace(range) ? "A1" : range.Trim().Replace("$", "").ToUpperInvariant();
        string nodeId = $"step-{stepNumber}";
        WorkflowNode node;

        switch (actionKind)
        {
            case IrConstants.ActionChromeNavigate:
                node = new WorkflowNode
                {
                    NodeId = nodeId,
                    Type = IrConstants.NodeTypeAction,
                    ActionType = IrConstants.ActionChromeNavigate,
                    SourceCandidateIds = sourceCandidateIds,
                    Args = JsonSerializer.SerializeToElement(new ChromeNavigateArgs { Url = safeUrl })
                };
                break;

            case IrConstants.ActionChromeFill:
                node = new WorkflowNode
                {
                    NodeId = nodeId,
                    Type = IrConstants.NodeTypeAction,
                    ActionType = IrConstants.ActionChromeFill,
                    SourceCandidateIds = sourceCandidateIds,
                    Args = JsonSerializer.SerializeToElement(new ChromeFillArgs
                    {
                        Locator = StepBlockFormatter.MergeLocatorWithPreservedMetadata("textbox", safeElem, preservedLocator),
                        Value = isSens ? SensitiveDataProtector.RedactedPlaceholder : fillVal,
                        IsSensitive = isSens
                    })
                };
                break;

            case IrConstants.ActionChromeClick:
                node = new WorkflowNode
                {
                    NodeId = nodeId,
                    Type = IrConstants.NodeTypeAction,
                    ActionType = IrConstants.ActionChromeClick,
                    SourceCandidateIds = sourceCandidateIds,
                    Args = JsonSerializer.SerializeToElement(new ChromeClickArgs
                    {
                        Locator = StepBlockFormatter.MergeLocatorWithPreservedMetadata("button", safeElem, preservedLocator)
                    })
                };
                break;

            case IrConstants.ActionChromeSubmit:
                node = new WorkflowNode
                {
                    NodeId = nodeId,
                    Type = IrConstants.NodeTypeAction,
                    ActionType = IrConstants.ActionChromeSubmit,
                    SourceCandidateIds = sourceCandidateIds,
                    Args = JsonSerializer.SerializeToElement(new ChromeSubmitArgs
                    {
                        Locator = StepBlockFormatter.MergeLocatorWithPreservedMetadata("button", safeElem, preservedLocator),
                        DestinationUrl = safeUrl,
                        ExpectedSuccessSelector = "#submit-success"
                    })
                };
                break;

            case IrConstants.ActionExcelReadRange:
                node = new WorkflowNode
                {
                    NodeId = nodeId,
                    Type = IrConstants.NodeTypeAction,
                    ActionType = IrConstants.ActionExcelReadRange,
                    SourceCandidateIds = sourceCandidateIds,
                    Args = JsonSerializer.SerializeToElement(new ExcelReadRangeArgs
                    {
                        WorkbookPath = excelPath.Trim(),
                        SheetName = sheet.Trim(),
                        RangeAddress = safeRange
                    })
                };
                break;

            case IrConstants.ActionCustomAppClick:
                node = new WorkflowNode
                {
                    NodeId = nodeId,
                    Type = IrConstants.NodeTypeAction,
                    ActionType = IrConstants.ActionCustomAppClick,
                    SourceCandidateIds = sourceCandidateIds,
                    Args = JsonSerializer.SerializeToElement(new CustomAppClickArgs
                    {
                        ApplicationName = customAppName,
                        Locator = StepBlockFormatter.MergeLocatorWithPreservedMetadata("button", safeElem, preservedLocator)
                    })
                };
                break;

            case IrConstants.ActionCustomAppFill:
                node = new WorkflowNode
                {
                    NodeId = nodeId,
                    Type = IrConstants.NodeTypeAction,
                    ActionType = IrConstants.ActionCustomAppFill,
                    SourceCandidateIds = sourceCandidateIds,
                    Args = JsonSerializer.SerializeToElement(new CustomAppFillArgs
                    {
                        ApplicationName = customAppName,
                        Locator = StepBlockFormatter.MergeLocatorWithPreservedMetadata("textbox", safeElem, preservedLocator),
                        Value = isSens ? SensitiveDataProtector.RedactedPlaceholder : fillVal,
                        IsSensitive = isSens
                    })
                };
                break;

            case IrConstants.ActionCustomAppReadText:
                node = new WorkflowNode
                {
                    NodeId = nodeId,
                    Type = IrConstants.NodeTypeAction,
                    ActionType = IrConstants.ActionCustomAppReadText,
                    SourceCandidateIds = sourceCandidateIds,
                    Args = JsonSerializer.SerializeToElement(new CustomAppReadTextArgs
                    {
                        ApplicationName = customAppName,
                        Locator = StepBlockFormatter.MergeLocatorWithPreservedMetadata("text", safeElem, preservedLocator)
                    })
                };
                break;

            default:
            {
                var rows = StepBlockFormatter.AdjustMatrixToRange(StepBlockFormatter.ParseCsvMatrix(excelText), safeRange);
                node = new WorkflowNode
                {
                    NodeId = nodeId,
                    Type = IrConstants.NodeTypeAction,
                    ActionType = IrConstants.ActionExcelWriteRange,
                    SourceCandidateIds = sourceCandidateIds,
                    Args = JsonSerializer.SerializeToElement(new ExcelWriteRangeArgs
                    {
                        WorkbookPath = excelPath.Trim(),
                        SheetName = sheet.Trim(),
                        RangeAddress = safeRange,
                        Values = rows
                    })
                };
                break;
            }
        }

        return BuildEasyStepItemFromNode(node, stepNumber, false);
    }

    private void RebuildIrJsonFromEasySteps()
    {
        var origins = new List<OriginRule>();
        var children = new List<WorkflowNode>();

        int num = 1;
        int total = EasySteps.Count;
        foreach (var item in EasySteps)
        {
            item.StepNumber = num;
            item.UnderlyingNode.NodeId = $"step-{num}";
            item.HasNextBlock = num < total;
            num++;
            children.Add(item.UnderlyingNode);

            if (item.UnderlyingNode.Args.HasValue)
            {
                if (item.ActionType == IrConstants.ActionChromeNavigate)
                {
                    var nav = item.UnderlyingNode.Args.Value.Deserialize<ChromeNavigateArgs>();
                    StepBlockFormatter.TryRegisterOrigin(nav?.Url, origins);
                }
                else if (item.ActionType == IrConstants.ActionChromeSubmit)
                {
                    var sub = item.UnderlyingNode.Args.Value.Deserialize<ChromeSubmitArgs>();
                    StepBlockFormatter.TryRegisterOrigin(sub?.DestinationUrl, origins);
                }
            }
        }

        if (_liveBrowserDriver?.CurrentUrl is not null)
        {
            StepBlockFormatter.TryRegisterOrigin(_liveBrowserDriver.CurrentUrl.ToString(), origins);
        }

        if (origins.Count == 0)
        {
            origins.Add(new OriginRule { Scheme = "https", Host = "example.com", Port = 443 });
            origins.Add(new OriginRule { Scheme = "https", Host = "www.google.com", Port = 443 });
        }

        if (!string.IsNullOrWhiteSpace(AllowedOriginHostInput) &&
            !origins.Any(o => string.Equals(o.Host, AllowedOriginHostInput.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            origins.Add(new OriginRule { Scheme = "https", Host = AllowedOriginHostInput.Trim(), Port = 443 });
        }

        var excelDirs = new List<string>();
        if (!string.IsNullOrWhiteSpace(AllowedExcelDirectoryInput))
        {
            excelDirs.Add(AllowedExcelDirectoryInput.Trim());
        }

        var def = new WorkflowDefinition
        {
            SchemaVersion = IrConstants.CurrentSchemaVersion,
            WorkflowId = string.IsNullOrWhiteSpace(WorkflowIdInput) ? "wf-custom" : WorkflowIdInput.Trim(),
            Name = string.IsNullOrWhiteSpace(WorkflowNameInput) ? "マイマクロ" : WorkflowNameInput.Trim(),
            Version = 1,
            Policy = new PolicyDeclaration
            {
                AllowedOrigins = origins,
                AllowCoordinateClick = AllowCoordinateClickPolicy,
                AllowExternalAi = AllowExternalAiPolicy,
                AllowedExcelDirectories = excelDirs
            },
            Root = new WorkflowNode
            {
                NodeId = "root",
                Type = IrConstants.NodeTypeSequence,
                Children = children
            }
        };

        EditorIrJson = JsonSerializer.Serialize(def, new JsonSerializerOptions { WriteIndented = true });
        _currentSavedVersionId = null;
        _currentApprovalId = null;
        CurrentApprovalStatusText = "手順が変更されたため、実行前に再承認が必要です。";
        ValidateCurrentEditorIr();
    }

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

    private void RebuildEasyStepsFromIrJson(string irJson)
    {
        try
        {
            var def = JsonSerializer.Deserialize<WorkflowDefinition>(irJson);
            if (def?.Root is null) return;

            var actionNodes = new List<WorkflowNode>();
            StepBlockFormatter.CollectActionNodesRecursive(def.Root, actionNodes);
            if (actionNodes.Count == 0) return;

            EasySteps.Clear();
            SelectedStepForEdit = null;
            int total = actionNodes.Count;
            for (int i = 0; i < total; i++)
            {
                var node = actionNodes[i];
                var stepItem = BuildEasyStepItemFromNode(node, i + 1, i + 1 < total);
                EasySteps.Add(stepItem);
            }
            VisualBlocksUpdated?.Invoke();
        }
        catch
        {
        }
    }

    private EasyStepItem BuildEasyStepItemFromNode(WorkflowNode node, int stepNumber, bool hasNextBlock)
    {
        string actionType = node.ActionType ?? IrConstants.ActionChromeClick;
        var (title, detail) = StepBlockFormatter.DescribeActionNode(node);
        var (icon, category, colorHex) = StepBlockFormatter.GetBlockVisualMeta(actionType);
        var (appBadge, targetLoc, opValue) = StepBlockFormatter.ExtractStructuredBlockInfo(node);

        string sourceLogSummary = "ブロック編集により構成";
        var tracedEvents = _workflowService.TraceNodeToRawEvents(node);
        if (tracedEvents.Count > 0)
        {
            var distinctSources = tracedEvents
                .Select(e => e.LogSource)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            sourceLogSummary = $"抽出元ログ ({tracedEvents.Count}件統合): {string.Join(" + ", distinctSources)}";
        }
        else if (node.SourceCandidateIds is { Count: > 0 })
        {
            var memEvents = _recordingService.GetRecordedEvents();
            if (memEvents.Count > 0)
            {
                var distinctSources = memEvents
                    .Select(e => e.LogSource)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                sourceLogSummary = $"抽出元ログ: {string.Join(" + ", distinctSources)}";
            }
        }

        return new EasyStepItem
        {
            StepNumber = stepNumber,
            NodeId = string.IsNullOrWhiteSpace(node.NodeId) ? $"step-{stepNumber}" : node.NodeId,
            ActionType = actionType,
            FriendlyTitle = title,
            FriendlyDetail = detail,
            UnderlyingNode = node,
            BlockIcon = icon,
            BlockCategory = category,
            AccentColorHex = colorHex,
            TargetAppBadge = appBadge,
            TargetLocationSummary = targetLoc,
            OperationValueSummary = opValue,
            SourceLogSummary = sourceLogSummary,
            HasNextBlock = hasNextBlock
        };
    }
}
