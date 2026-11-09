// Module: @role: MainViewModelの手順ブロック編集操作・選択・並べ替え・UIバインディング更新を担当するPartialクラス
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
}
