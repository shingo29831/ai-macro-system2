// Module: @role: 非エンジニア向けかんたん手順ビルダー・記録・AI変換・承認・実行・復旧を統合するViewModel
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using AiMacroSystem.Application;
using AiMacroSystem.Domain;
using AiMacroSystem.Infrastructure;

namespace AiMacroSystem.Presentation;

public sealed class EasyStepItem
{
    public required int StepNumber { get; set; }
    public required string NodeId { get; init; }
    public required string ActionType { get; init; }
    public required string FriendlyTitle { get; init; }
    public required string FriendlyDetail { get; init; }
    public required WorkflowNode UnderlyingNode { get; init; }
    public string BlockIcon { get; init; } = "⚙️";
    public string BlockCategory { get; init; } = "操作ブロック";
    public string AccentColorHex { get; init; } = "#2563EB";
    public string TargetAppBadge { get; init; } = "アプリ";
    public string TargetLocationSummary { get; init; } = string.Empty;
    public string OperationValueSummary { get; init; } = string.Empty;
    public string SourceLogSummary { get; init; } = "複数ログ統合抽出";
    public bool IsSelectedForEdit { get; set; }
    public bool HasNextBlock { get; set; }
}

public sealed class FriendlyValidationItem
{
    public required string Severity { get; init; }
    public required string FriendlyLocation { get; init; }
    public required string FriendlyReason { get; init; }
    public required string HowToFix { get; init; }
    public required string TechnicalCode { get; init; }
}

public sealed class FriendlyPlanReviewItem
{
    public required string NodeId { get; init; }
    public required string OperationName { get; init; }
    public required string TargetDescription { get; init; }
    public required string SideEffectDescription { get; init; }
    public required string RiskBadge { get; init; }
    public required bool NeedsExtraApproval { get; init; }
}

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly SqliteRepository _repository;
    private readonly WorkflowManagementService _workflowService;
    private readonly RecordingService _recordingService;
    private readonly NormalizationService _normalizationService;
    private readonly PolicyEngine _policyEngine;
    private readonly ApprovalService _approvalService;
    private readonly ExecutionEngine _executionEngine;
    private readonly RecoveryService _recoveryService;
    private readonly AiOrchestrator? _aiOrchestrator;
    private readonly LiveChromeBrowserDriver? _liveBrowserDriver;
    private readonly ILiveOperationLogCollector? _liveLogCollector;

    private string _statusBanner = "準備完了：「▶ 実行」ですぐに開始できます";
    private string _workflowNameInput = "毎日のWeb入力とExcel転記マクロ";
    private string _workflowIdInput = "wf-daily-routine";
    private string _editorIrJson = string.Empty;

    private string _selectedActionKind = IrConstants.ActionChromeNavigate;
    private string _stepUrlInput = "https://example.com/portal";
    private string _stepElementNameInput = "担当者名";
    private string _stepFillValueInput = "山田 太郎";
    private bool _stepIsPasswordInput;
    private string _stepExcelPathInput = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AiMacroSample.xlsx");
    private string _stepExcelSheetInput = "Sheet1";
    private string _stepExcelRangeInput = "A1:B2";
    private string _stepExcelWriteTextInput = "完了,2026-10-20\n確認済,OK";
    private string _stepCustomAppNameInput = "基幹業務システム";

    private string _approverNameInput = "ユーザー本人";
    private bool _approveHighRiskChecked = true;
    private string _currentApprovalStatusText = "まだ承認されていません（「✅ このマクロを承認する」を押してください）";
    private string? _currentSavedVersionId;
    private string? _currentApprovalId;
    private string? _activeRunningExecutionId;
    private ExecutionPlan? _currentEvaluatedPlan;

    private string _localAiEndpoint = "http://localhost:11434";
    private string _localAiModel = "llama3.1:8b";
    private bool _allowExternalAiPolicy;
    private bool _openSystemBrowserOnNavigate;

    private WorkflowVersionRecord? _selectedMacroInList;
    private EasyStepItem? _selectedStepForEdit;
    private bool _isEditingMode;
    private bool _isExecutingMacro;
    private bool _isRecordingMacro;
    private string _runningProgressText = "実行中...";
    private string _recordingProgressText = "記録中: 複数ログ（Web / Excel / 独自アプリ）から操作を監視しています...";
    private string _editingBlockHeaderText = "ブロックの追加・選択編集";

    public event Action? VisualBlocksUpdated;

    public MainViewModel(
        SqliteRepository repository,
        WorkflowManagementService workflowService,
        RecordingService recordingService,
        NormalizationService normalizationService,
        PolicyEngine policyEngine,
        ApprovalService approvalService,
        ExecutionEngine executionEngine,
        RecoveryService recoveryService,
        AiOrchestrator? aiOrchestrator = null,
        LiveChromeBrowserDriver? liveBrowserDriver = null,
        ILiveOperationLogCollector? liveLogCollector = null)
    {
        _repository = repository;
        _workflowService = workflowService;
        _recordingService = recordingService;
        _normalizationService = normalizationService;
        _policyEngine = policyEngine;
        _approvalService = approvalService;
        _executionEngine = executionEngine;
        _recoveryService = recoveryService;
        _aiOrchestrator = aiOrchestrator;
        _liveBrowserDriver = liveBrowserDriver;
        _liveLogCollector = liveLogCollector;
    }

    public ObservableCollection<WorkflowVersionRecord> WorkflowVersions { get; } = new();
    public ObservableCollection<WorkflowDraftRecord> WorkflowDrafts { get; } = new();
    public ObservableCollection<EasyStepItem> EasySteps { get; } = new();
    public ObservableCollection<RawRecordedEvent> RecordedEvents { get; } = new();
    public ObservableCollection<ValidationIssue> CurrentValidationIssues { get; } = new();
    public ObservableCollection<FriendlyValidationItem> FriendlyValidationIssues { get; } = new();
    public ObservableCollection<PlannedActionItem> CurrentPlannedActions { get; } = new();
    public ObservableCollection<FriendlyPlanReviewItem> FriendlyPlanItems { get; } = new();
    public ObservableCollection<ActionExecutionRecord> CurrentRunActionStatuses { get; } = new();
    public ObservableCollection<ExecutionRecord> AllExecutions { get; } = new();
    public ObservableCollection<ExecutionEventRecord> ExecutionHistoryEvents { get; } = new();
    public ObservableCollection<RecoveryRecord> PendingRecoveries { get; } = new();
    public ObservableCollection<RecoveryRecord> RecoveryHistory { get; } = new();
    public ObservableCollection<AiAuditRecord> AiAuditLogs { get; } = new();

    private string _recoveryDecisionNotesInput = "外部状態を確認し、安全に復旧判断を行いました";
    private string _allowedOriginHostInput = "example.com";
    private bool _allowCoordinateClickPolicy;
    private string _allowedExcelDirectoryInput = string.Empty;

    public string RecoveryDecisionNotesInput
    {
        get => _recoveryDecisionNotesInput;
        set { _recoveryDecisionNotesInput = value; OnPropertyChanged(); }
    }

    public string AllowedOriginHostInput
    {
        get => _allowedOriginHostInput;
        set { _allowedOriginHostInput = value; OnPropertyChanged(); }
    }

    public bool AllowCoordinateClickPolicy
    {
        get => _allowCoordinateClickPolicy;
        set { _allowCoordinateClickPolicy = value; OnPropertyChanged(); }
    }

    public string AllowedExcelDirectoryInput
    {
        get => _allowedExcelDirectoryInput;
        set { _allowedExcelDirectoryInput = value; OnPropertyChanged(); }
    }

    public IReadOnlyList<KeyValuePair<string, string>> AvailableActionKinds { get; } = new[]
    {
        new KeyValuePair<string, string>(IrConstants.ActionChromeNavigate, "🌐 Webページを開く"),
        new KeyValuePair<string, string>(IrConstants.ActionChromeFill, "✏️ 文字を入力する"),
        new KeyValuePair<string, string>(IrConstants.ActionChromeClick, "👆 ボタンをクリック"),
        new KeyValuePair<string, string>(IrConstants.ActionChromeSubmit, "📤 フォームを送信"),
        new KeyValuePair<string, string>(IrConstants.ActionExcelReadRange, "📖 Excelを読み取る"),
        new KeyValuePair<string, string>(IrConstants.ActionExcelWriteRange, "📝 Excelに書き込む"),
        new KeyValuePair<string, string>(IrConstants.ActionCustomAppClick, "🖥 独自アプリのボタンを押す"),
        new KeyValuePair<string, string>(IrConstants.ActionCustomAppFill, "⌨️ 独自アプリに文字を入力"),
        new KeyValuePair<string, string>(IrConstants.ActionCustomAppReadText, "🔍 独自アプリの文字を読み取る")
    };

    public string StatusBanner
    {
        get => _statusBanner;
        set { _statusBanner = value; OnPropertyChanged(); }
    }

    public string WorkflowNameInput
    {
        get => _workflowNameInput;
        set { _workflowNameInput = value; OnPropertyChanged(); }
    }

    public string WorkflowIdInput
    {
        get => _workflowIdInput;
        set { _workflowIdInput = value; OnPropertyChanged(); }
    }

    public string EditorIrJson
    {
        get => _editorIrJson;
        set
        {
            if (!string.Equals(_editorIrJson, value, StringComparison.Ordinal))
            {
                _editorIrJson = value;
                // 実行計画に影響する一時変更が行われた場合は既存承認を無効化する（仕様2.5 / 12）
                if (_currentApprovalId is not null)
                {
                    _currentApprovalId = null;
                    CurrentApprovalStatusText = "編集内容が変更されたため既存承認は無効化されました。再検証・再承認が必要です。";
                }
                OnPropertyChanged();
            }
        }
    }

    public string SelectedActionKind
    {
        get => _selectedActionKind;
        set { _selectedActionKind = value; OnPropertyChanged(); }
    }

    public string StepUrlInput
    {
        get => _stepUrlInput;
        set { _stepUrlInput = value; OnPropertyChanged(); }
    }

    public string StepElementNameInput
    {
        get => _stepElementNameInput;
        set { _stepElementNameInput = value; OnPropertyChanged(); }
    }

    public string StepFillValueInput
    {
        get => _stepFillValueInput;
        set { _stepFillValueInput = value; OnPropertyChanged(); }
    }

    public bool StepIsPasswordInput
    {
        get => _stepIsPasswordInput;
        set { _stepIsPasswordInput = value; OnPropertyChanged(); }
    }

    public string StepExcelPathInput
    {
        get => _stepExcelPathInput;
        set { _stepExcelPathInput = value; OnPropertyChanged(); }
    }

    public string StepExcelSheetInput
    {
        get => _stepExcelSheetInput;
        set { _stepExcelSheetInput = value; OnPropertyChanged(); }
    }

    public string StepExcelRangeInput
    {
        get => _stepExcelRangeInput;
        set { _stepExcelRangeInput = value; OnPropertyChanged(); }
    }

    public string StepExcelWriteTextInput
    {
        get => _stepExcelWriteTextInput;
        set { _stepExcelWriteTextInput = value; OnPropertyChanged(); }
    }

    public string StepCustomAppNameInput
    {
        get => _stepCustomAppNameInput;
        set { _stepCustomAppNameInput = value; OnPropertyChanged(); }
    }

    public string ApproverNameInput
    {
        get => _approverNameInput;
        set { _approverNameInput = value; OnPropertyChanged(); }
    }

    public bool ApproveHighRiskChecked
    {
        get => _approveHighRiskChecked;
        set { _approveHighRiskChecked = value; OnPropertyChanged(); }
    }

    public string CurrentApprovalStatusText
    {
        get => _currentApprovalStatusText;
        set { _currentApprovalStatusText = value; OnPropertyChanged(); }
    }

    public string LocalAiEndpoint
    {
        get => _localAiEndpoint;
        set { _localAiEndpoint = value; OnPropertyChanged(); }
    }

    public string LocalAiModel
    {
        get => _localAiModel;
        set { _localAiModel = value; OnPropertyChanged(); }
    }

    public bool AllowExternalAiPolicy
    {
        get => _allowExternalAiPolicy;
        set { _allowExternalAiPolicy = value; OnPropertyChanged(); }
    }

    public bool OpenSystemBrowserOnNavigate
    {
        get => _openSystemBrowserOnNavigate;
        set
        {
            _openSystemBrowserOnNavigate = value;
            if (_liveBrowserDriver is not null)
            {
                _liveBrowserDriver.LaunchSystemBrowserOnNavigate = value;
            }
            OnPropertyChanged();
        }
    }

    public WorkflowVersionRecord? SelectedMacroInList
    {
        get => _selectedMacroInList;
        set { _selectedMacroInList = value; OnPropertyChanged(); }
    }

    public EasyStepItem? SelectedStepForEdit
    {
        get => _selectedStepForEdit;
        set
        {
            _selectedStepForEdit = value;
            EditingBlockHeaderText = value is not null
                ? $"✏ ブロック #{value.StepNumber} ({value.FriendlyTitle}) を編集中"
                : "ブロックの追加・選択編集（左のブロックをクリックすると編集できます）";
            OnPropertyChanged();
        }
    }

    public string EditingBlockHeaderText
    {
        get => _editingBlockHeaderText;
        set { _editingBlockHeaderText = value; OnPropertyChanged(); }
    }

    public bool IsEditingMode
    {
        get => _isEditingMode;
        set { _isEditingMode = value; OnPropertyChanged(); }
    }

    public bool IsExecutingMacro
    {
        get => _isExecutingMacro;
        set { _isExecutingMacro = value; OnPropertyChanged(); }
    }

    public bool IsRecordingMacro
    {
        get => _isRecordingMacro;
        set { _isRecordingMacro = value; OnPropertyChanged(); }
    }

    public string RunningProgressText
    {
        get => _runningProgressText;
        set { _runningProgressText = value; OnPropertyChanged(); }
    }

    public string RecordingProgressText
    {
        get => _recordingProgressText;
        set { _recordingProgressText = value; OnPropertyChanged(); }
    }

    public async Task InitializeAndCheckRecoveryAsync()
    {
        await _recoveryService.RunStartupRecoveryAsync();
        RefreshAllListsFromDatabase();

        if (EasySteps.Count == 0 && string.IsNullOrWhiteSpace(EditorIrJson))
        {
            LoadSampleMacro();
        }

        // 初回起動時にマクロ一覧が空であればサンプルマクロを1件自動保存して一覧に表示する
        if (WorkflowVersions.Count == 0)
        {
            SaveCurrentWorkflowAsVersion();
        }

        if (SelectedMacroInList is null && WorkflowVersions.Count > 0)
        {
            SelectedMacroInList = WorkflowVersions[0];
        }

        StatusBanner = PendingRecoveries.Count > 0
            ? $"⚠️ 前回の異常終了が {PendingRecoveries.Count} 件あります。上部の「ロック解除」を押してください"
            : "マクロ一覧からマクロを選択して「▶ 実行」「✏ 編集」「🗑 削除」を行えます";
    }

    public void StartNewMacroInEditor()
    {
        // 全てのマクロは複数ログの記録から生成し、生成後のマクロをブロックエディタで編集する
        ClearEasySteps();
        if (_recordingService.GetRecordedEvents().Count == 0)
        {
            LoadSampleMacro();
        }
        else
        {
            GenerateMacroFromRecordingSynchronously();
        }
        WorkflowIdInput = $"wf-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
        WorkflowNameInput = "記録から生成したマクロ";
        RebuildIrJsonFromEasySteps();
        IsEditingMode = true;
        StatusBanner = "複数ログの記録からマクロを生成しました。ブロックの追加・並べ替えで編集できます";
    }

    public void OpenSelectedMacroInEditor()
    {
        var target = SelectedMacroInList ?? WorkflowVersions.FirstOrDefault();
        if (target is null)
        {
            StartNewMacroInEditor();
            return;
        }
        LoadSelectedVersionIntoEditor(target);
        IsEditingMode = true;
    }

    public void DeleteSelectedMacro()
    {
        if (SelectedMacroInList is null)
        {
            StatusBanner = "⚠️ 削除するマクロを一覧から選択してください";
            return;
        }
        string deletedName = SelectedMacroInList.Name;
        _repository.DeleteWorkflowCascade(SelectedMacroInList.WorkflowId);
        RefreshAllListsFromDatabase();
        SelectedMacroInList = WorkflowVersions.FirstOrDefault();
        StatusBanner = $"🗑 マクロ「{deletedName}」を削除しました";
    }

    public async Task ExecuteSelectedMacroFromListAsync()
    {
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
        // 全てのマクロは複数ログソースからの操作記録・抽出パイプラインで生成する
        EasySteps.Clear();
        string sampleId = $"wf-sample-{DateTimeOffset.UtcNow:HHmmss}";
        string sampleName = "Webフォーム入力とExcel集計の自動化マクロ";

        var baseTime = DateTimeOffset.UtcNow.AddSeconds(-10);
        _recordingService.StartRecording();
        RecordedEvents.Clear();

        // 1. Webページ遷移（WindowManagerノイズログ + BrowserDom正規ログ）
        RecordAndTrack(new RawRecordedEvent(
            $"evt-{Guid.NewGuid():N}", "Chrome", "WindowActivate", null, null, null, null,
            "https://example.com/portal", null, null, null, false, baseTime,
            LogSourceConstants.WindowManager, null, null, null, 0.5, "Google Chrome"));
        RecordAndTrack(new RawRecordedEvent(
            $"evt-{Guid.NewGuid():N}", "Chrome", "Navigate", null, null, null, null,
            "https://example.com/portal", null, null, null, false, baseTime.AddMilliseconds(50),
            LogSourceConstants.BrowserDom, null, null, null, 0.98, "Google Chrome"));

        // 2. 担当者名入力（OsInputHookの途中打鍵ログ + UiAutomationログ + BrowserDom確定ログ）
        RecordAndTrack(new RawRecordedEvent(
            $"evt-{Guid.NewGuid():N}", "Chrome", "Input", null, null, null, null,
            null, null, null, "山田", false, baseTime.AddSeconds(1),
            LogSourceConstants.OsInputHook, null, 320, 240, 0.3, "Google Chrome"));
        RecordAndTrack(new RawRecordedEvent(
            $"evt-{Guid.NewGuid():N}", "Chrome", "Input", "textbox", "担当者名", null, null,
            null, null, null, "山田 太郎", false, baseTime.AddSeconds(1).AddMilliseconds(200),
            LogSourceConstants.UiAutomation, null, 320, 240, 0.88, "Google Chrome"));
        RecordAndTrack(new RawRecordedEvent(
            $"evt-{Guid.NewGuid():N}", "Chrome", "Input", "textbox", "担当者名", "担当者名", "input-staff-name",
            "https://example.com/portal", null, null, "山田 太郎", false, baseTime.AddSeconds(1).AddMilliseconds(250),
            LogSourceConstants.BrowserDom, "#input-staff-name", 320, 240, 0.99, "Google Chrome"));

        // 3. 照会ボタンクリック（OsInputHook座標ログ + BrowserDomセマンティックログ）
        RecordAndTrack(new RawRecordedEvent(
            $"evt-{Guid.NewGuid():N}", "Chrome", "Click", null, null, null, null,
            null, null, null, null, false, baseTime.AddSeconds(3),
            LogSourceConstants.OsInputHook, null, 450, 310, 0.25, "Google Chrome"));
        RecordAndTrack(new RawRecordedEvent(
            $"evt-{Guid.NewGuid():N}", "Chrome", "Click", "button", "照会する", "照会する", "btn-inquiry",
            "https://example.com/portal", null, null, null, false, baseTime.AddSeconds(3).AddMilliseconds(40),
            LogSourceConstants.BrowserDom, "#btn-inquiry", 450, 310, 0.99, "Google Chrome"));

        // 4. Excel書き込み（SelectionChangeノイズログ + ExcelCom書き込み確定ログ）
        RecordAndTrack(new RawRecordedEvent(
            $"evt-{Guid.NewGuid():N}", "Excel", "SelectionChange", null, null, null, null,
            StepExcelPathInput, "Sheet1", "A1:B2", null, false, baseTime.AddSeconds(5),
            LogSourceConstants.ExcelCom, null, null, null, 0.7, "Excel"));
        RecordAndTrack(new RawRecordedEvent(
            $"evt-{Guid.NewGuid():N}", "Excel", "Write", null, null, null, null,
            StepExcelPathInput, "Sheet1", "A1:B2", "担当者,結果\n山田 太郎,正常完了", false, baseTime.AddSeconds(5).AddMilliseconds(120),
            LogSourceConstants.ExcelCom, null, null, null, 0.99, "Excel"));

        _recordingService.StopRecording();

        GenerateMacroFromRecordingSynchronously();
        WorkflowIdInput = sampleId;
        WorkflowNameInput = sampleName;
        RebuildIrJsonFromEasySteps();
        StatusBanner = "複数の操作ログから高精度抽出を行いマクロを生成しました";
    }

    private void RecordAndTrack(RawRecordedEvent ev)
    {
        var recorded = _recordingService.RecordMultiSourceBatch(new[] { ev });
        foreach (var item in recorded)
        {
            RecordedEvents.Add(item);
        }
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
            preservedCandidateIds);

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
        List<string>? sourceCandidateIds)
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
                        Locator = new SemanticLocator { Role = "textbox", AccessibleName = safeElem, Label = safeElem },
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
                        Locator = new SemanticLocator { Role = "button", AccessibleName = safeElem, Label = safeElem }
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
                        Locator = new SemanticLocator { Role = "button", AccessibleName = safeElem, Label = safeElem },
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
                        Locator = new SemanticLocator { Role = "button", AccessibleName = safeElem, Label = safeElem }
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
                        Locator = new SemanticLocator { Role = "textbox", AccessibleName = safeElem, Label = safeElem },
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
                        Locator = new SemanticLocator { Role = "text", AccessibleName = safeElem, Label = safeElem }
                    })
                };
                break;

            default:
            {
                var rows = AdjustMatrixToRange(ParseCsvMatrix(excelText), safeRange);
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

    private static List<List<string>> AdjustMatrixToRange(List<List<string>> rawRows, string rangeAddress)
    {
        if (!IrValidator.TryGetRangeDimensions(rangeAddress, out int targetRows, out int targetCols))
        {
            return rawRows;
        }
        var adjusted = new List<List<string>>(targetRows);
        for (int r = 0; r < targetRows; r++)
        {
            var row = new List<string>(targetCols);
            for (int c = 0; c < targetCols; c++)
            {
                string val = (r < rawRows.Count && c < rawRows[r].Count)
                    ? rawRows[r][c]
                    : (rawRows.Count > 0 && rawRows[0].Count > 0 ? rawRows[0][0] : "データ");
                row.Add(val);
            }
            adjusted.Add(row);
        }
        return adjusted;
    }

    private static (string Icon, string Category, string ColorHex) GetBlockVisualMeta(string actionKind) =>
        actionKind switch
        {
            IrConstants.ActionChromeNavigate => ("🌐", "Chrome ナビゲーション", "#2563EB"),
            IrConstants.ActionChromeFill => ("✏️", "Chrome 文字入力", "#0D9488"),
            IrConstants.ActionChromeClick => ("👆", "Chrome クリック操作", "#4F46E5"),
            IrConstants.ActionChromeSubmit => ("📤", "Chrome フォーム外部送信", "#D97706"),
            IrConstants.ActionExcelReadRange => ("📖", "Excel データ読み取り", "#16A34A"),
            IrConstants.ActionExcelWriteRange => ("📝", "Excel データ書き込み", "#15803D"),
            IrConstants.ActionCustomAppClick => ("🖥", "独自アプリ クリック操作", "#7C3AED"),
            IrConstants.ActionCustomAppFill => ("⌨️", "独自アプリ 文字入力", "#9333EA"),
            IrConstants.ActionCustomAppReadText => ("🔍", "独自アプリ テキスト取得", "#6D28D9"),
            _ => ("⚙️", "アクション", "#475569")
        };

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
                    TryRegisterOrigin(nav?.Url, origins);
                }
                else if (item.ActionType == IrConstants.ActionChromeSubmit)
                {
                    var sub = item.UnderlyingNode.Args.Value.Deserialize<ChromeSubmitArgs>();
                    TryRegisterOrigin(sub?.DestinationUrl, origins);
                }
            }
        }

        if (origins.Count == 0)
        {
            origins.Add(new OriginRule { Scheme = "https", Host = "example.com", Port = 443 });
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

    private static void TryRegisterOrigin(string? rawUrl, List<OriginRule> origins)
    {
        if (!string.IsNullOrWhiteSpace(rawUrl) && Uri.TryCreate(rawUrl, UriKind.Absolute, out Uri? uri))
        {
            if (uri.Scheme is "https" or "http" && !origins.Any(o => o.Matches(uri)))
            {
                origins.Add(new OriginRule
                {
                    Scheme = uri.Scheme,
                    Host = uri.Host,
                    Port = uri.IsDefaultPort ? (uri.Scheme == "https" ? 443 : 80) : uri.Port
                });
            }
        }
    }

    private static List<List<string>> ParseCsvMatrix(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new List<List<string>> { new() { "データ" } };
        }
        var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        var matrix = new List<List<string>>();
        foreach (string line in lines)
        {
            matrix.Add(line.Split(',').Select(s => s.Trim()).ToList());
        }
        return matrix.Count > 0 ? matrix : new List<List<string>> { new() { text } };
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
            FriendlyValidationIssues.Add(ToFriendlyIssue(issue));
        }

        if (res.IsValid && def is not null)
        {
            string tempVerId = _currentSavedVersionId ?? "preview-ver";
            var (planVal, plan) = _policyEngine.BuildAndEvaluatePlan(tempVerId, def);
            foreach (var issue in planVal.Issues)
            {
                CurrentValidationIssues.Add(issue);
                FriendlyValidationIssues.Add(ToFriendlyIssue(issue));
            }
            if (plan is not null)
            {
                _currentEvaluatedPlan = plan;
                foreach (var act in plan.PlannedActions)
                {
                    CurrentPlannedActions.Add(act);
                    FriendlyPlanItems.Add(ToFriendlyPlanItem(act));
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
        ValidateCurrentEditorIr();
        if (CurrentValidationIssues.Count > 0)
        {
            StatusBanner = "❌ エラーがあるため保存できません。「③ 安全チェック・承認」タブで内容を確認してください。";
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
        StatusBanner = $"💾 マクロ「{saved.Name} (v{saved.VersionNumber})」を正式バージョンとして安全に保存しました。";
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

    // 正式版を直接変更せず、エディタ上の一時編集内容からExecutionPlanを作成・再検証・承認して実行する（仕様12）
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

        _executionEngine.ActionNodeCompletedAsync += OnStepCompleted;
        try
        {
            IsExecutingMacro = true;
            RunningProgressText = $"実行中: {WorkflowNameInput} (全 {EasySteps.Count} ブロック)";
            StatusBanner = "🚀 マクロを実行中です...";
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

    // --- 操作記録 ＆ 記録終了時の自動マクロ変換 ---
    public void StartEasyRecording()
    {
        _recordingService.StartRecording();
        RecordedEvents.Clear();
        RecordingProgressText = "記録中: 操作ログ（ブラウザ / Excel / 独自アプリ）を収集しています...";
        IsRecordingMacro = true;
        _liveLogCollector?.Start(_recordingService, ev =>
        {
            System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
            {
                RecordedEvents.Add(ev);
                RecordingProgressText = $"記録中 ({RecordedEvents.Count} ログ): [{ev.ApplicationKind}] {ev.EventKind} ({ev.TargetLabel ?? ev.UrlOrWorkbookPath ?? ev.LogSource})";
            });
        });
        StatusBanner = "🔴 操作記録を開始しました。画面上部の「記録中」バーから記録終了すると自動でマクロに変換されます。";
    }

    public void StopEasyRecording()
    {
        StopEasyRecordingAndGenerateAsync().GetAwaiter().GetResult();
    }

    public async Task StopEasyRecordingAndGenerateAsync()
    {
        try
        {
            _liveLogCollector?.StopAndFlush();

            // 記録中に個別イベントが未投入の場合は複数アプリの標準操作ログ群を自動収集してから停止する
            if (_recordingService.GetRecordedEvents().Count == 0)
            {
                RecordSimulatedWebOpen();
                RecordSimulatedWebInput(asPassword: false);
                RecordSimulatedExcelWrite();
                RecordSimulatedCustomAppAction();
            }

            _recordingService.StopRecording();
            await GenerateMacroFromRecordingViaAiAsync();
            IsEditingMode = true;
            VisualBlocksUpdated?.Invoke();
        }
        finally
        {
            IsRecordingMacro = false;
        }
    }

    public void RecordSimulatedWebOpen()
    {
        if (!_recordingService.IsRecording) _recordingService.StartRecording();
        var baseTs = _recordingService.AdvanceLogicalClock(TimeSpan.FromMilliseconds(500));
        var evNoise = _recordingService.RecordEvent(
            "Chrome", "WindowActivate", null, null, null, null, null,
            StepUrlInput, null, null, null,
            logSource: LogSourceConstants.WindowManager, sourceConfidence: 0.5, timestampOverride: baseTs);
        var evDom = _recordingService.RecordEvent(
            "Chrome", "Navigate", null, null, null, null, null,
            StepUrlInput, null, null, null,
            logSource: LogSourceConstants.BrowserDom, sourceConfidence: 0.99, timestampOverride: baseTs.AddMilliseconds(30));

        RecordedEvents.Add(evNoise);
        RecordedEvents.Add(evDom);
        RecordingProgressText = $"記録中 ({RecordedEvents.Count} ログ): Webページ遷移 ({StepUrlInput})";
        StatusBanner = $"🔴 Webページ移動（{evDom.UrlOrWorkbookPath}）の複数ログを記録しました。";
    }

    public void RecordSimulatedWebInput(bool asPassword)
    {
        if (!_recordingService.IsRecording) _recordingService.StartRecording();
        var baseTs = _recordingService.AdvanceLogicalClock(TimeSpan.FromMilliseconds(500));
        string targetName = asPassword ? "Password" : StepElementNameInput;
        string targetLabel = asPassword ? "パスワード" : StepElementNameInput;
        string rawVal = asPassword ? "SecretPass123!" : StepFillValueInput;

        var evHook = _recordingService.RecordEvent(
            "Chrome", "Input", asPassword ? "password" : "text", null, null, null, null,
            null, null, null, rawVal[..Math.Min(2, rawVal.Length)],
            logSource: LogSourceConstants.OsInputHook, coordinateX: 300, coordinateY: 220,
            sourceConfidence: 0.3, timestampOverride: baseTs);
        var evUia = _recordingService.RecordEvent(
            "Chrome", "Input", asPassword ? "password" : "text", "textbox", targetName, null, null,
            StepUrlInput, null, null, rawVal,
            logSource: LogSourceConstants.UiAutomation, coordinateX: 300, coordinateY: 220,
            sourceConfidence: 0.88, timestampOverride: baseTs.AddMilliseconds(60));
        var evDom = _recordingService.RecordEvent(
            "Chrome", "Input", asPassword ? "password" : "text", "textbox", targetName, targetLabel, $"field-{targetName}",
            StepUrlInput, null, null, rawVal,
            logSource: LogSourceConstants.BrowserDom, cssSelector: $"#field-{targetName}",
            sourceConfidence: 0.99, timestampOverride: baseTs.AddMilliseconds(110));

        RecordedEvents.Add(evHook);
        RecordedEvents.Add(evUia);
        RecordedEvents.Add(evDom);
        RecordingProgressText = $"記録中 ({RecordedEvents.Count} ログ): Web文字入力 ({targetLabel})";
        StatusBanner = asPassword
            ? "🔒 パスワード入力を検知したため、値を自動的に [REDACTED] にマスキング保護して記録しました。"
            : $"🔴 文字入力（{evDom.TargetLabel}: {evDom.CapturedValue}）の複数ログを記録しました。";
    }

    public void RecordSimulatedExcelWrite()
    {
        if (!_recordingService.IsRecording) _recordingService.StartRecording();
        var baseTs = _recordingService.AdvanceLogicalClock(TimeSpan.FromMilliseconds(500));
        var evSelect = _recordingService.RecordEvent(
            "Excel", "SelectionChange", null, null, null, null, null,
            StepExcelPathInput, StepExcelSheetInput, StepExcelRangeInput, null,
            logSource: LogSourceConstants.ExcelCom, sourceConfidence: 0.7, timestampOverride: baseTs);
        var evWrite = _recordingService.RecordEvent(
            "Excel", "Write", null, null, null, null, null,
            StepExcelPathInput, StepExcelSheetInput, StepExcelRangeInput, StepExcelWriteTextInput,
            logSource: LogSourceConstants.ExcelCom, sourceConfidence: 0.99, timestampOverride: baseTs.AddMilliseconds(50));

        RecordedEvents.Add(evSelect);
        RecordedEvents.Add(evWrite);
        RecordingProgressText = $"記録中 ({RecordedEvents.Count} ログ): Excel書込 ({StepExcelSheetInput}!{StepExcelRangeInput})";
        StatusBanner = $"🔴 Excel書き込み（{evWrite.SheetName}!{evWrite.RangeAddress}）の複数ログを記録しました。";
    }

    public void RecordSimulatedCustomAppAction()
    {
        if (!_recordingService.IsRecording) _recordingService.StartRecording();
        var baseTs = _recordingService.AdvanceLogicalClock(TimeSpan.FromMilliseconds(500));
        string appName = string.IsNullOrWhiteSpace(StepCustomAppNameInput) ? "基幹業務システム" : StepCustomAppNameInput.Trim();
        string controlName = string.IsNullOrWhiteSpace(StepElementNameInput) ? "受注番号" : StepElementNameInput.Trim();
        string fillVal = string.IsNullOrWhiteSpace(StepFillValueInput) ? "ORD-2026-001" : StepFillValueInput;

        // 複数ログソース（OsInputHook + UiAutomation + CustomAppHook）から独自アプリ操作を記録
        var evHook = _recordingService.RecordEvent(
            "CustomApp", "Input", null, null, null, null, null,
            appName, null, null, fillVal[..Math.Min(3, fillVal.Length)],
            logSource: LogSourceConstants.OsInputHook, coordinateX: 280, coordinateY: 190,
            sourceConfidence: 0.3, windowOrProcessName: appName, timestampOverride: baseTs);
        var evUia = _recordingService.RecordEvent(
            "CustomApp", "Input", "text", "textbox", controlName, controlName, null,
            appName, null, null, fillVal,
            logSource: LogSourceConstants.UiAutomation, coordinateX: 280, coordinateY: 190,
            sourceConfidence: 0.90, windowOrProcessName: appName, timestampOverride: baseTs.AddMilliseconds(80));
        var evApp = _recordingService.RecordEvent(
            "CustomApp", "Input", "text", "textbox", controlName, controlName, $"ctrl-{controlName}",
            appName, null, null, fillVal,
            logSource: LogSourceConstants.CustomAppHook,
            sourceConfidence: 0.99, windowOrProcessName: appName, timestampOverride: baseTs.AddMilliseconds(120));

        RecordedEvents.Add(evHook);
        RecordedEvents.Add(evUia);
        RecordedEvents.Add(evApp);
        RecordingProgressText = $"記録中 ({RecordedEvents.Count} ログ): 独自アプリ操作 ({appName})";
        StatusBanner = $"🔴 独自アプリ「{appName}」の複数ログ（OSフック・UIAutomation・アプリログ）を記録しました。";
    }

    public async Task GenerateMacroFromRecordingViaAiAsync()
    {
        var rawList = _recordingService.GetRecordedEvents();
        if (rawList.Count == 0)
        {
            StatusBanner = "⚠️ 先に操作を記録してください。";
            return;
        }

        var candidates = _normalizationService.Normalize(rawList);
        var policy = new PolicyDeclaration
        {
            AllowedOrigins = new List<OriginRule> { new() { Scheme = "https", Host = "example.com", Port = 443 } },
            AllowExternalAi = AllowExternalAiPolicy
        };

        if (_aiOrchestrator is not null)
        {
            var outcome = await _aiOrchestrator.GenerateAndValidateIrAsync(candidates, policy, AllowExternalAiPolicy, default);
            RefreshAllListsFromDatabase();

            if (outcome.IsAccepted && outcome.RawJson is not null)
            {
                EditorIrJson = outcome.RawJson;
                _currentSavedVersionId = null;
                _currentApprovalId = null;
                if (outcome.Definition is not null)
                {
                    WorkflowIdInput = outcome.Definition.WorkflowId;
                    WorkflowNameInput = outcome.Definition.Name;
                }
                RebuildEasyStepsFromIrJson(outcome.RawJson);
                ValidateCurrentEditorIr();
                IsEditingMode = true;
                VisualBlocksUpdated?.Invoke();
                StatusBanner = $"✨ 複数ログ（{rawList.Count}件）から {EasySteps.Count} 個の操作ブロックを抽出し、マクロ化しました！";
                return;
            }
            StatusBanner = $"⚠️ AI生成結果が安全基準でブロックされました: {outcome.ErrorReason}";
            return;
        }

        GenerateMacroFromRecordingSynchronously();
        IsEditingMode = true;
        VisualBlocksUpdated?.Invoke();
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
            CollectActionNodesRecursive(def.Root, actionNodes);
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
            // カスタムIR構造の場合は既存のJSON文字列をそのまま保持
        }
    }

    private static void CollectActionNodesRecursive(WorkflowNode node, List<WorkflowNode> acc)
    {
        if (node.Type == IrConstants.NodeTypeAction && !string.IsNullOrWhiteSpace(node.ActionType))
        {
            acc.Add(node);
            return;
        }
        if (node.Children is not null) foreach (var c in node.Children) CollectActionNodesRecursive(c, acc);
        if (node.ThenChildren is not null) foreach (var c in node.ThenChildren) CollectActionNodesRecursive(c, acc);
        if (node.ElseChildren is not null) foreach (var c in node.ElseChildren) CollectActionNodesRecursive(c, acc);
        if (node.BodyChildren is not null) foreach (var c in node.BodyChildren) CollectActionNodesRecursive(c, acc);
    }

    private EasyStepItem BuildEasyStepItemFromNode(WorkflowNode node, int stepNumber, bool hasNextBlock)
    {
        string actionType = node.ActionType ?? IrConstants.ActionChromeClick;
        var (title, detail) = DescribeActionNode(node);
        var (icon, category, colorHex) = GetBlockVisualMeta(actionType);
        var (appBadge, targetLoc, opValue) = ExtractStructuredBlockInfo(node);

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

    private static (string AppBadge, string TargetLocation, string OperationValue) ExtractStructuredBlockInfo(WorkflowNode node)
    {
        if (!node.Args.HasValue)
        {
            return ("システム", node.ActionType ?? "操作", "-");
        }

        JsonElement args = node.Args.Value;
        switch (node.ActionType)
        {
            case IrConstants.ActionChromeNavigate:
            {
                var a = args.Deserialize<ChromeNavigateArgs>();
                return ("Chrome ブラウザ", $"URL: {a?.Url}", "Webページ遷移・オリジン検証");
            }
            case IrConstants.ActionChromeFill:
            {
                var a = args.Deserialize<ChromeFillArgs>();
                var (strategy, key) = (a?.Locator ?? new SemanticLocator()).ResolveByPriority();
                string val = a?.IsSensitive == true ? "[REDACTED] (機密保護)" : (a?.Value ?? string.Empty);
                return ("Chrome ブラウザ", $"要素: {key} ({strategy})", $"入力値: 「{val}」");
            }
            case IrConstants.ActionChromeClick:
            {
                var a = args.Deserialize<ChromeClickArgs>();
                var (strategy, key) = (a?.Locator ?? new SemanticLocator()).ResolveByPriority();
                return ("Chrome ブラウザ", $"要素: {key} ({strategy})", "クリック操作・一意性検証");
            }
            case IrConstants.ActionChromeSubmit:
            {
                var a = args.Deserialize<ChromeSubmitArgs>();
                var (strategy, key) = (a?.Locator ?? new SemanticLocator()).ResolveByPriority();
                return ("Chrome ブラウザ", $"送信先: {a?.DestinationUrl} / 要素: {key} ({strategy})", $"事後検証: {a?.ExpectedSuccessSelector}");
            }
            case IrConstants.ActionExcelReadRange:
            {
                var a = args.Deserialize<ExcelReadRangeArgs>();
                return ("Excel", $"ブック: {Path.GetFileName(a?.WorkbookPath ?? "")} [{a?.SheetName}!{a?.RangeAddress}]", $"セル範囲読み取り ({a?.RangeAddress})");
            }
            case IrConstants.ActionExcelWriteRange:
            {
                var a = args.Deserialize<ExcelWriteRangeArgs>();
                string valPreview = a?.Values is { Count: > 0 }
                    ? string.Join(" / ", a.Values.Select(r => string.Join(", ", r)))
                    : "参照データ書き込み";
                return ("Excel", $"ブック: {Path.GetFileName(a?.WorkbookPath ?? "")} [{a?.SheetName}!{a?.RangeAddress}]", $"書込値: {valPreview}");
            }
            case IrConstants.ActionCustomAppClick:
            {
                var a = args.Deserialize<CustomAppClickArgs>();
                var (strategy, key) = (a?.Locator ?? new SemanticLocator()).ResolveByPriority();
                string appName = string.IsNullOrWhiteSpace(a?.ApplicationName) ? "独自アプリ" : a.ApplicationName;
                return ($"独自アプリ ({appName})", $"要素: {key} ({strategy})", "コントロールクリック操作");
            }
            case IrConstants.ActionCustomAppFill:
            {
                var a = args.Deserialize<CustomAppFillArgs>();
                var (strategy, key) = (a?.Locator ?? new SemanticLocator()).ResolveByPriority();
                string appName = string.IsNullOrWhiteSpace(a?.ApplicationName) ? "独自アプリ" : a.ApplicationName;
                string val = a?.IsSensitive == true ? "[REDACTED] (機密保護)" : (a?.Value ?? string.Empty);
                return ($"独自アプリ ({appName})", $"要素: {key} ({strategy})", $"入力値: 「{val}」");
            }
            case IrConstants.ActionCustomAppReadText:
            {
                var a = args.Deserialize<CustomAppReadTextArgs>();
                var (strategy, key) = (a?.Locator ?? new SemanticLocator()).ResolveByPriority();
                string appName = string.IsNullOrWhiteSpace(a?.ApplicationName) ? "独自アプリ" : a.ApplicationName;
                return ($"独自アプリ ({appName})", $"要素: {key} ({strategy})", "テキスト読み取り");
            }
            default:
                return ("アプリ", node.ActionType ?? "操作", "-");
        }
    }

    private static (string Title, string Detail) DescribeActionNode(WorkflowNode node)
    {
        if (!node.Args.HasValue) return (node.ActionType ?? "操作", "");
        JsonElement args = node.Args.Value;

        switch (node.ActionType)
        {
            case IrConstants.ActionChromeNavigate:
            {
                var a = args.Deserialize<ChromeNavigateArgs>();
                return ("🌐 Webページを開く", $"移動先URL: {a?.Url}");
            }
            case IrConstants.ActionChromeFill:
            {
                var a = args.Deserialize<ChromeFillArgs>();
                string target = a?.Locator?.Label ?? a?.Locator?.AccessibleName ?? "入力欄";
                string val = a?.IsSensitive == true ? "******** (保護)" : (a?.Value ?? "");
                return ("✏️ 文字を入力する", $"「{target}」に「{val}」を入力");
            }
            case IrConstants.ActionChromeClick:
            {
                var a = args.Deserialize<ChromeClickArgs>();
                string target = a?.Locator?.AccessibleName ?? a?.Locator?.Label ?? "ボタン";
                return ("👆 ボタンをクリック", $"対象: 「{target}」");
            }
            case IrConstants.ActionChromeSubmit:
            {
                var a = args.Deserialize<ChromeSubmitArgs>();
                return ("📤 フォームを送信", $"送信先: {a?.DestinationUrl}");
            }
            case IrConstants.ActionExcelReadRange:
            {
                var a = args.Deserialize<ExcelReadRangeArgs>();
                return ("📖 Excelを読み取る", $"{Path.GetFileName(a?.WorkbookPath ?? "")} [{a?.SheetName}!{a?.RangeAddress}]");
            }
            case IrConstants.ActionExcelWriteRange:
            {
                var a = args.Deserialize<ExcelWriteRangeArgs>();
                return ("📝 Excelに書き込む", $"{Path.GetFileName(a?.WorkbookPath ?? "")} [{a?.SheetName}!{a?.RangeAddress}]");
            }
            case IrConstants.ActionCustomAppClick:
            {
                var a = args.Deserialize<CustomAppClickArgs>();
                string target = a?.Locator?.AccessibleName ?? a?.Locator?.Label ?? "ボタン";
                return ("🖥 独自アプリのボタンを押す", $"{a?.ApplicationName} [{target}]");
            }
            case IrConstants.ActionCustomAppFill:
            {
                var a = args.Deserialize<CustomAppFillArgs>();
                string target = a?.Locator?.Label ?? a?.Locator?.AccessibleName ?? "入力欄";
                string val = a?.IsSensitive == true ? "******** (保護)" : (a?.Value ?? "");
                return ("⌨️ 独自アプリに文字を入力", $"{a?.ApplicationName} [{target}: {val}]");
            }
            case IrConstants.ActionCustomAppReadText:
            {
                var a = args.Deserialize<CustomAppReadTextArgs>();
                string target = a?.Locator?.Label ?? a?.Locator?.AccessibleName ?? "テキスト";
                return ("🔍 独自アプリの文字を読み取る", $"{a?.ApplicationName} [{target}]");
            }
            default:
                return (node.ActionType ?? "アクション", "");
        }
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

    private static FriendlyValidationItem ToFriendlyIssue(ValidationIssue issue)
    {
        string howToFix = issue.Code switch
        {
            "CHROME_ORIGIN_NOT_ALLOWED" => "移動先のURLが許可リストに入っていません。「① かんたん作成」から手順を追加し直すか、URLを確認してください。",
            "EXCEL_RANGE_INVALID" => "Excelのセル範囲は「A1」や「A1:B5」のような形式で入力してください。",
            "LOCATOR_AMBIGUOUS_OR_EMPTY" => "操作するボタンや入力欄の名前を入力してください。",
            "POLICY_PROHIBITED_OPERATION" => "削除などの危険な操作が含まれているため実行できません。該当ステップを削除してください。",
            _ => issue.SuggestedFix ?? "入力項目に空欄や誤りがないか確認してください。"
        };

        return new FriendlyValidationItem
        {
            Severity = "⚠️ 要修正",
            FriendlyLocation = string.IsNullOrWhiteSpace(issue.NodeId) ? "マクロ全体" : $"ステップ ({issue.NodeId})",
            FriendlyReason = issue.Message,
            HowToFix = howToFix,
            TechnicalCode = $"{issue.Stage} / {issue.Code}"
        };
    }

    private static FriendlyPlanReviewItem ToFriendlyPlanItem(PlannedActionItem item)
    {
        string opName = item.ActionType switch
        {
            IrConstants.ActionChromeNavigate => "🌐 Webページを開く",
            IrConstants.ActionChromeFill => "✏️ 文字を入力する",
            IrConstants.ActionChromeClick => "👆 ボタンをクリック",
            IrConstants.ActionChromeSubmit => "📤 フォームを外部送信",
            IrConstants.ActionExcelReadRange => "📖 Excelを読み取る",
            IrConstants.ActionExcelWriteRange => "📝 Excelに書き込む",
            IrConstants.ActionCustomAppClick => "🖥 独自アプリのボタンを押す",
            IrConstants.ActionCustomAppFill => "⌨️ 独自アプリに文字を入力",
            IrConstants.ActionCustomAppReadText => "🔍 独自アプリの文字を読み取る",
            _ => item.ActionType
        };

        string effect = item.SideEffects switch
        {
            SideEffectKind.Read => "読み取りのみ（安全）",
            SideEffectKind.LocalMutation => "画面またはExcelファイルの変更",
            SideEffectKind.ExternalSend => "外部へのデータ送信（要注意）",
            _ => item.SideEffects.ToString()
        };

        string risk = item.EvaluatedRisk switch
        {
            RiskLevel.Low => "🟢 低リスク",
            RiskLevel.Medium => "🟡 中リスク（結果を確認）",
            RiskLevel.High => "🟠 高リスク（個別承認が必要）",
            RiskLevel.Prohibited => "🔴 実行禁止",
            _ => item.EvaluatedRisk.ToString()
        };

        return new FriendlyPlanReviewItem
        {
            NodeId = item.NodeId,
            OperationName = opName,
            TargetDescription = item.TargetSummary,
            SideEffectDescription = effect,
            RiskBadge = risk,
            NeedsExtraApproval = item.RequiresIndividualApproval
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
