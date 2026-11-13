// Module: @role: 非エンジニア向けかんたん手順ビルダー・記録・AI変換・承認・実行・復旧を統合するViewModel（コア状態・プロパティ）
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using AiMacroSystem.Application;
using AiMacroSystem.Domain;
using AiMacroSystem.Infrastructure;

namespace AiMacroSystem.Presentation;

public sealed partial class MainViewModel : INotifyPropertyChanged
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
    private readonly LiveCustomAppDriver? _liveCustomAppDriver;
    private readonly RecentOperationLogStore? _logStore;

    public Func<IReadOnlyList<AppTargetPromptRequest>, Task<IReadOnlyDictionary<string, WindowTargetSelection>?>>? ConfirmExecutionTargetsAsync { get; set; }

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
        ILiveOperationLogCollector? liveLogCollector = null,
        LiveCustomAppDriver? liveCustomAppDriver = null,
        RecentOperationLogStore? logStore = null)
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
        _liveCustomAppDriver = liveCustomAppDriver;
        _logStore = logStore;
        if (_liveBrowserDriver is not null)
        {
            _openSystemBrowserOnNavigate = _liveBrowserDriver.LaunchSystemBrowserOnNavigate;
        }
    }

    public RecordingService RecordingService => _recordingService;
    public LiveChromeBrowserDriver? LiveBrowserDriver => _liveBrowserDriver;
    public string RecentLogDirectoryPath => _logStore?.LogDirectoryPath ??
        RecentOperationLogStore.ResolveDefaultLogDirectory();
    public string LatestTextLogPath => _logStore?.LatestLogFilePath ??
        Path.Combine(RecentLogDirectoryPath, RecentOperationLogStore.LatestLogFileName);

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

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
