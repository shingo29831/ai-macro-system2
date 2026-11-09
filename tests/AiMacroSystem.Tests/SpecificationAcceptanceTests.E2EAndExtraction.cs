// Module: @role: 仕様書第18.11/18.13章（E2E自動化・複数ログ抽出・アプリ横断マクロ編集）受け入れテスト
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiMacroSystem.Application;
using AiMacroSystem.Domain;
using AiMacroSystem.Infrastructure;
using Xunit;

namespace AiMacroSystem.Tests;

public sealed partial class SpecificationAcceptanceTests
{
    [Fact]
    public async Task Section18_11_EndToEnd_EasyMacroBuilder_Recording_AiGeneration_AndLiveExecution()
    {
        var liveBrowser = new LiveChromeBrowserDriver();
        var liveExcel = new LiveExcelDriver();
        var localAi = new LocalBuiltInAiProvider();

        var chromeAdapter = new ChromeAdapter(liveBrowser);
        var excelAdapter = new ExcelAdapter(liveExcel);
        var customAppAdapter = new CustomAppAdapter(new LiveCustomAppDriver());
        var liveAdapters = new Dictionary<string, IActionAdapter>(StringComparer.Ordinal)
        {
            [IrConstants.ActionChromeNavigate] = chromeAdapter,
            [IrConstants.ActionChromeClick] = chromeAdapter,
            [IrConstants.ActionChromeFill] = chromeAdapter,
            [IrConstants.ActionChromeSubmit] = chromeAdapter,
            [IrConstants.ActionExcelReadRange] = excelAdapter,
            [IrConstants.ActionExcelWriteRange] = excelAdapter,
            [IrConstants.ActionCustomAppClick] = customAppAdapter,
            [IrConstants.ActionCustomAppFill] = customAppAdapter,
            [IrConstants.ActionCustomAppReadText] = customAppAdapter
        };

        var liveRecovery = new RecoveryService(_repository, liveAdapters);
        var liveEngine = new ExecutionEngine(_repository, _policyEngine, _approvalService, liveRecovery, liveAdapters);
        var recordingService = new RecordingService();
        var normalizationService = new NormalizationService();
        var aiOrchestrator = new AiOrchestrator(localAi, null, _validator, _repository);

        var vm = new AiMacroSystem.Presentation.MainViewModel(
            _repository,
            _workflowService,
            recordingService,
            normalizationService,
            _policyEngine,
            _approvalService,
            liveEngine,
            liveRecovery,
            aiOrchestrator,
            liveBrowser);

        vm.StepExcelPathInput = Path.Combine(Path.GetTempPath(), $"LiveMacro-{Guid.NewGuid():N}.xlsx");
        await vm.InitializeAndCheckRecoveryAsync();

        // サンプルマクロが自動構成され、ワンクリックで保存・承認・実行まで完了することを確認
        Assert.Equal(4, vm.EasySteps.Count);
        await vm.ExecuteOneClickAllInOneAsync();

        Assert.NotEmpty(vm.AllExecutions);
        Assert.Equal(ExecutionStatus.Succeeded, vm.AllExecutions[0].Status);
        Assert.Equal(4, vm.CurrentRunActionStatuses.Count);
        Assert.All(vm.CurrentRunActionStatuses, a => Assert.Equal(ActionExecutionStatus.Succeeded, a.Status));

        // 記録からAI変換で新しいマクロを生成して実行できることを確認
        vm.StartEasyRecording();
        vm.RecordSimulatedWebOpen();
        vm.RecordSimulatedWebInput(asPassword: true);
        vm.RecordSimulatedExcelWrite();
        vm.StopEasyRecording();

        await vm.GenerateMacroFromRecordingViaAiAsync();
        Assert.Empty(vm.CurrentValidationIssues);

        await vm.ExecuteOneClickAllInOneAsync();
        Assert.Equal(ExecutionStatus.Succeeded, vm.AllExecutions[0].Status);
    }

    [Fact]
    public async Task Section18_13_MultiLogExtraction_CrossAppMacroGeneration_AndBlockEditing()
    {
        var recorder = new RecordingService(_repository);
        var normalizer = new NormalizationService(_repository);
        var localAi = new LocalBuiltInAiProvider();
        var aiOrchestrator = new AiOrchestrator(localAi, null, _validator, _repository);

        string excelPath = Path.Combine(Path.GetTempPath(), $"MultiLogCrossApp-{Guid.NewGuid():N}.xlsx");
        var t0 = DateTimeOffset.UtcNow;

        recorder.StartRecording();

        // 1. ブラウザ操作: マウス移動ノイズ + OS座標フック + UIAutomation + BrowserDom の複数ログ
        recorder.RecordEvent("Chrome", "MouseMove", null, null, null, null, null,
            "https://example.com/orders", null, null, null,
            logSource: LogSourceConstants.OsInputHook, coordinateX: 100, coordinateY: 100,
            sourceConfidence: 0.1, timestampOverride: t0);
        recorder.RecordEvent("Chrome", "Navigate", null, null, null, null, null,
            "https://example.com/orders", null, null, null,
            logSource: LogSourceConstants.BrowserDom, sourceConfidence: 0.99, timestampOverride: t0.AddMilliseconds(20));

        // 2. ブラウザ文字入力: 1文字ずつの打鍵ノイズログ(OsInputHook) + UIAutomation + BrowserDom確定ログ
        var in1 = recorder.RecordEvent("Chrome", "Input", "text", null, null, null, null,
            null, null, null, "A",
            logSource: LogSourceConstants.OsInputHook, coordinateX: 250, coordinateY: 180,
            sourceConfidence: 0.25, timestampOverride: t0.AddSeconds(1));
        var in2 = recorder.RecordEvent("Chrome", "Input", "text", "textbox", "顧客コード", null, null,
            null, null, null, "ABC-",
            logSource: LogSourceConstants.UiAutomation, coordinateX: 250, coordinateY: 180,
            sourceConfidence: 0.85, timestampOverride: t0.AddSeconds(1).AddMilliseconds(150));
        var in3 = recorder.RecordEvent("Chrome", "Input", "text", null, null, "顧客コード", "cust-code",
            "https://example.com/orders", null, null, "ABC-999",
            logSource: LogSourceConstants.BrowserDom, cssSelector: "#cust-code",
            sourceConfidence: 0.99, timestampOverride: t0.AddSeconds(1).AddMilliseconds(300));

        // 3. Excel操作: SheetActivateノイズ + 段階的編集ログ + ExcelCom最終確定ログ
        recorder.RecordEvent("Excel", "SheetActivate", null, null, null, null, null,
            excelPath, "Orders", "A1:B1", null,
            logSource: LogSourceConstants.ExcelCom, sourceConfidence: 0.8, timestampOverride: t0.AddSeconds(3));
        recorder.RecordEvent("Excel", "Write", null, null, null, null, null,
            null, null, "A1:B1", "ABC-999,仮登録",
            logSource: LogSourceConstants.UiAutomation, sourceConfidence: 0.6, timestampOverride: t0.AddSeconds(3).AddMilliseconds(100));
        recorder.RecordEvent("Excel", "Write", null, null, null, null, null,
            excelPath, "Orders", "A1:B1", "ABC-999,正式受注",
            logSource: LogSourceConstants.ExcelCom, sourceConfidence: 0.99, timestampOverride: t0.AddSeconds(3).AddMilliseconds(250));

        // 4. 独自アプリ操作: ウィンドウフォーカス + OS座標クリック + UIAutomation + CustomAppHook
        recorder.RecordEvent("CustomApp", "WindowFocus", null, null, null, null, null,
            null, null, null, null,
            logSource: LogSourceConstants.WindowManager, windowOrProcessName: "社内受注管理システム",
            sourceConfidence: 0.7, timestampOverride: t0.AddSeconds(5));
        recorder.RecordEvent("CustomApp", "Input", null, null, null, null, null,
            null, null, null, "ABC-",
            logSource: LogSourceConstants.OsInputHook, coordinateX: 400, coordinateY: 260,
            sourceConfidence: 0.2, timestampOverride: t0.AddSeconds(5).AddMilliseconds(80));
        recorder.RecordEvent("CustomApp", "Input", "text", "textbox", "受注コード入力", null, null,
            null, null, null, "ABC-999",
            logSource: LogSourceConstants.UiAutomation, coordinateX: 400, coordinateY: 260,
            sourceConfidence: 0.90, windowOrProcessName: "社内受注管理システム", timestampOverride: t0.AddSeconds(5).AddMilliseconds(160));
        recorder.RecordEvent("CustomApp", "Input", "text", null, null, "受注コード", "txt-order-code",
            "社内受注管理システム", null, null, "ABC-999",
            logSource: LogSourceConstants.CustomAppHook,
            sourceConfidence: 0.99, windowOrProcessName: "社内受注管理システム", timestampOverride: t0.AddSeconds(5).AddMilliseconds(220));

        recorder.StopRecording();

        // 12件の複数生ログからノイズが除去・統合され、高精度な4件のアクション候補が抽出されることを検証
        var rawEvents = recorder.GetRecordedEvents();
        Assert.Equal(12, rawEvents.Count);

        var candidates = normalizer.Normalize(rawEvents);
        Assert.Equal(4, candidates.Count);
        Assert.All(candidates, c => Assert.False(c.IsAmbiguous));

        // 2番目の候補（Chrome.Fill）は3つのログ（OsInputHook, UiAutomation, BrowserDom）が統合され、
        // 最終確定値 "ABC-999" と最高優先度ロケーター（Role+AccessibleName, Label, StableId, CssSelector）をすべて保持する
        var fillCand = candidates[1];
        Assert.Equal(IrConstants.ActionChromeFill, fillCand.SuggestedActionType);
        Assert.Equal("ABC-999", fillCand.SanitizedValue);
        Assert.Equal(3, fillCand.SourceRawEventIds.Count);
        Assert.Contains(in1.EventId, fillCand.SourceRawEventIds);
        Assert.Contains(in2.EventId, fillCand.SourceRawEventIds);
        Assert.Contains(in3.EventId, fillCand.SourceRawEventIds);
        Assert.Equal(("RoleAndAccessibleName", "textbox:顧客コード"), fillCand.Locator!.ResolveByPriority());
        Assert.Equal("cust-code", fillCand.Locator.StableId);
        Assert.Equal("#cust-code", fillCand.Locator.CssSelector);

        // 3番目の候補（Excel.WriteRange）はコンテキスト補完と上書き統合により最終値 "ABC-999,正式受注" になる
        var excelCand = candidates[2];
        Assert.Equal(IrConstants.ActionExcelWriteRange, excelCand.SuggestedActionType);
        Assert.Equal(excelPath, excelCand.TargetUrlOrWorkbook);
        Assert.Equal("Orders", excelCand.SheetName);
        Assert.Equal("A1:B1", excelCand.RangeAddress);
        Assert.Equal("ABC-999,正式受注", excelCand.SanitizedValue);

        // 4番目の候補（CustomApp.Fill）は独自アプリの複数ログから抽出される
        var customCand = candidates[3];
        Assert.Equal(IrConstants.ActionCustomAppFill, customCand.SuggestedActionType);
        Assert.Equal("社内受注管理システム", customCand.TargetUrlOrWorkbook);
        Assert.Equal("ABC-999", customCand.SanitizedValue);
        Assert.Equal(("RoleAndAccessibleName", "textbox:受注コード入力"), customCand.Locator!.ResolveByPriority());

        // 抽出した候補からマクロを生成し、作成済みマクロをブロック編集で操作・実行できることを検証
        var vm = new AiMacroSystem.Presentation.MainViewModel(
            _repository,
            _workflowService,
            recorder,
            normalizer,
            _policyEngine,
            _approvalService,
            _executionEngine,
            _recoveryService,
            aiOrchestrator);

        // アプリ別ウィンドウ選択リクエスト生成（Chrome / Excel / CustomApp の3種が検出されること）と選択バインドを検証
        vm.EditorIrJson = (await localAi.GenerateWorkflowIrJsonAsync(candidates, new PolicyDeclaration(), null, CancellationToken.None));
        var targetRequests = vm.BuildAppTargetPromptRequests(includeThumbnails: false);
        Assert.Equal(3, targetRequests.Count);
        Assert.Contains(targetRequests, r => r.AppCategory == "Chrome" && r.ActionCount == 2);
        Assert.Contains(targetRequests, r => r.AppCategory == "Excel" && r.ActionCount == 1);
        Assert.Contains(targetRequests, r => r.AppCategory == "CustomApp" && r.ApplicationName == "社内受注管理システム");

        // ブラウザ検索タイトルからの日本語検索ワード抽出ヘルパーを検証
        string inferredGoogleUrl = BrowserUiAutomationHelper.InferBrowserUrlOrSearchEngine("名古屋 カフェ - Google 検索 - Google Chrome", out string? extractedQuery);
        Assert.Equal("https://www.google.com", inferredGoogleUrl);
        Assert.Equal("名古屋 カフェ", extractedQuery);

        // 記録開始で IsRecordingMacro が true になり、記録終了で自動的に複数ログからマクロが生成され IsRecordingMacro が false に戻ることを検証
        vm.StartEasyRecording();
        Assert.True(vm.IsRecordingMacro);
        recorder.RecordMultiSourceBatch(rawEvents);
        await vm.StopEasyRecordingAndGenerateAsync();
        Assert.False(vm.IsRecordingMacro);
        Assert.True(vm.IsEditingMode);
        Assert.Empty(vm.CurrentValidationIssues);
        Assert.Equal(4, vm.EasySteps.Count);

        // 各ビジュアルブロックに操作対象アプリ・ロケーション・操作値・統合元ログ情報がマクロ化されていることを検証
        Assert.Contains("Chrome", vm.EasySteps[1].TargetAppBadge);
        Assert.Contains("顧客コード", vm.EasySteps[1].TargetLocationSummary);
        Assert.Contains("ABC-999", vm.EasySteps[1].OperationValueSummary);
        Assert.Contains("BrowserDom", vm.EasySteps[1].SourceLogSummary);

        Assert.Contains("社内受注管理システム", vm.EasySteps[3].TargetAppBadge);
        Assert.Contains("ABC-999", vm.EasySteps[3].OperationValueSummary);

        // 作成済みマクロの既存ビジュアルブロックを選択して操作内容を編集し、記録済み座標も維持されることを検証
        vm.SelectStepForEditing(vm.EasySteps[1]);
        Assert.Equal(vm.EasySteps[1], vm.SelectedStepForEdit);
        vm.StepFillValueInput = "ABC-1000-EDITED";
        vm.UpdateSelectedStepFromBuilderUi();
        Assert.Contains("ABC-1000-EDITED", vm.EasySteps[1].OperationValueSummary);
        var updatedFillArgs = vm.EasySteps[1].UnderlyingNode.Args!.Value.Deserialize<ChromeFillArgs>()!;
        Assert.Equal(250, updatedFillArgs.Locator!.CoordinateX);
        Assert.Equal(180, updatedFillArgs.Locator.CoordinateY);

        // 作成済みマクロに対するブロック追加・上下移動
        vm.SelectedActionKind = IrConstants.ActionCustomAppClick;
        vm.StepCustomAppNameInput = "社内受注管理システム";
        vm.StepElementNameInput = "登録確定";
        vm.AddStepFromBuilderUi();
        Assert.Equal(5, vm.EasySteps.Count);

        var addedStep = vm.EasySteps[4];
        vm.MoveStepUp(addedStep);
        Assert.Equal(IrConstants.ActionCustomAppClick, vm.EasySteps[3].ActionType);
        vm.MoveStepDown(addedStep);
        Assert.Equal(IrConstants.ActionCustomAppClick, vm.EasySteps[4].ActionType);

        await vm.ExecuteOneClickAllInOneAsync();
        Assert.NotEmpty(vm.AllExecutions);
        Assert.Equal(ExecutionStatus.Succeeded, vm.AllExecutions[0].Status);

        // 5. アプリ種別自動判定("Auto")およびブラウザからExcelへのコピー＆ペースト横断転記とマクロ保存の検証
        vm.StartEasyRecording();
        var tCross = DateTimeOffset.UtcNow.AddSeconds(10);
        var copyEv = recorder.RecordEvent(
            "Auto", "Copy", null, "table", "売上集計表", "売上集計表", "tbl-sales",
            "https://example.com/sales", null, null, "品目,金額\nノートPC,150000",
            logSource: LogSourceConstants.BrowserDom, cssSelector: "#tbl-sales",
            sourceConfidence: 0.98, windowOrProcessName: "売上管理 - Google Chrome", timestampOverride: tCross);
        recorder.RecordEvent(
            "Auto", "WindowActivate", null, null, null, null, null,
            excelPath, "Orders", "A1:B2", null,
            logSource: LogSourceConstants.WindowManager,
            sourceConfidence: 0.85, windowOrProcessName: "Orders.xlsx - Excel", timestampOverride: tCross.AddMilliseconds(300));
        var pasteHookEv = recorder.RecordEvent(
            "Auto", "Paste", null, null, null, null, null,
            null, null, null, null,
            logSource: LogSourceConstants.OsInputHook, coordinateX: 220, coordinateY: 160,
            sourceConfidence: 0.4, windowOrProcessName: "Orders.xlsx - Excel", timestampOverride: tCross.AddMilliseconds(450));
        var writeComEv = recorder.RecordEvent(
            "Auto", "Write", null, null, null, null, null,
            excelPath, "Orders", "A1:B2", null,
            logSource: LogSourceConstants.ExcelCom, coordinateX: 220, coordinateY: 160,
            sourceConfidence: 0.99, windowOrProcessName: "Orders.xlsx - Excel", timestampOverride: tCross.AddMilliseconds(500));

        await vm.StopEasyRecordingAndGenerateAsync();
        Assert.Single(vm.EasySteps);
        Assert.Equal(IrConstants.ActionExcelWriteRange, vm.EasySteps[0].ActionType);
        Assert.Contains("ノートPC, 150000", vm.EasySteps[0].OperationValueSummary);
        Assert.Contains(copyEv.EventId, vm.EasySteps[0].UnderlyingNode.SourceCandidateIds!
            .SelectMany(cid => _repository.GetNormalizedCandidates().Where(c => c.CandidateId == cid).SelectMany(c => c.SourceRawEventIds)));
        Assert.Contains(pasteHookEv.EventId, vm.EasySteps[0].UnderlyingNode.SourceCandidateIds!
            .SelectMany(cid => _repository.GetNormalizedCandidates().Where(c => c.CandidateId == cid).SelectMany(c => c.SourceRawEventIds)));
        Assert.Contains(writeComEv.EventId, vm.EasySteps[0].UnderlyingNode.SourceCandidateIds!
            .SelectMany(cid => _repository.GetNormalizedCandidates().Where(c => c.CandidateId == cid).SelectMany(c => c.SourceRawEventIds)));

        // 記録終了後にマクロ名を設定して「マクロを保存」を実行し、正式バージョンとして保存・選択されることを検証
        vm.WorkflowIdInput = "wf-cross-app-saved";
        vm.WorkflowNameInput = "ブラウザからExcelへの売上転記マクロ";
        vm.SaveCurrentWorkflowAsVersion();
        Assert.NotNull(vm.SelectedMacroInList);
        Assert.Equal("wf-cross-app-saved", vm.SelectedMacroInList!.WorkflowId);
        Assert.Equal("ブラウザからExcelへの売上転記マクロ", vm.SelectedMacroInList.Name);
    }
}
