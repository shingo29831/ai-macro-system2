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

    private string _statusBanner = "準備完了：左上の「① かんたん作成・編集」または「💡 お試しサンプル読込」から始められます。";
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
        LiveChromeBrowserDriver? liveBrowserDriver = null)
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
    }

    public ObservableCollection<WorkflowVersionRecord> WorkflowVersions { get; } = new();
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
    public ObservableCollection<AiAuditRecord> AiAuditLogs { get; } = new();

    public IReadOnlyList<KeyValuePair<string, string>> AvailableActionKinds { get; } = new[]
    {
        new KeyValuePair<string, string>(IrConstants.ActionChromeNavigate, "🌐 Webページを開く (Chrome)"),
        new KeyValuePair<string, string>(IrConstants.ActionChromeFill, "✏️ Webの入力欄に文字を入れる (Chrome)"),
        new KeyValuePair<string, string>(IrConstants.ActionChromeClick, "👆 Webのボタンをクリックする (Chrome)"),
        new KeyValuePair<string, string>(IrConstants.ActionChromeSubmit, "📤 Webのフォームを送信する (Chrome)"),
        new KeyValuePair<string, string>(IrConstants.ActionExcelReadRange, "📖 Excelのセル範囲を読み取る (Excel)"),
        new KeyValuePair<string, string>(IrConstants.ActionExcelWriteRange, "📝 Excelのセル範囲に書き込む (Excel)")
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
        set { _editorIrJson = value; OnPropertyChanged(); }
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

    public async Task InitializeAndCheckRecoveryAsync()
    {
        var recoveries = await _recoveryService.RunStartupRecoveryAsync();
        RefreshAllListsFromDatabase();

        if (EasySteps.Count == 0 && string.IsNullOrWhiteSpace(EditorIrJson))
        {
            LoadSampleMacro();
        }

        StatusBanner = PendingRecoveries.Count > 0
            ? $"⚠️ 【確認が必要です】前回途中で終了したマクロが {PendingRecoveries.Count} 件あります。「⑤ 実行履歴・トラブル復旧」タブで解決するまで新しい実行は安全のためロックされています。"
            : "🟢 準備完了：サンプルマクロが読み込まれました。「▶ マクロをワンクリック実行（保存＋承認＋実行）」ですぐに試せます！";
    }

    public void LoadSampleMacro()
    {
        EasySteps.Clear();
        WorkflowIdInput = $"wf-sample-{DateTimeOffset.UtcNow:HHmmss}";
        WorkflowNameInput = "Webフォーム入力とExcel集計の自動化マクロ";

        AddEasyStepInternal(
            IrConstants.ActionChromeNavigate,
            url: "https://example.com/portal",
            elementName: "",
            fillVal: "",
            isSens: false,
            excelPath: StepExcelPathInput,
            sheet: "Sheet1",
            range: "A1",
            excelText: "");

        AddEasyStepInternal(
            IrConstants.ActionChromeFill,
            url: "",
            elementName: "担当者名",
            fillVal: "山田 太郎",
            isSens: false,
            excelPath: StepExcelPathInput,
            sheet: "Sheet1",
            range: "A1",
            excelText: "");

        AddEasyStepInternal(
            IrConstants.ActionChromeClick,
            url: "",
            elementName: "照会する",
            fillVal: "",
            isSens: false,
            excelPath: StepExcelPathInput,
            sheet: "Sheet1",
            range: "A1",
            excelText: "");

        AddEasyStepInternal(
            IrConstants.ActionExcelWriteRange,
            url: "",
            elementName: "",
            fillVal: "",
            isSens: false,
            excelPath: StepExcelPathInput,
            sheet: "Sheet1",
            range: "A1:B2",
            excelText: "担当者,結果\n山田 太郎,正常完了");

        RebuildIrJsonFromEasySteps();
        StatusBanner = "💡 お試しサンプルマクロ（4ステップ）を読み込みました。手順の追加や「▶ ワンクリック実行」が可能です。";
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
        int num = EasySteps.Count + 1;
        string nodeId = $"step-{num}";
        WorkflowNode node;
        string title;
        string detail;

        switch (actionKind)
        {
            case IrConstants.ActionChromeNavigate:
                title = "🌐 Webページを開く";
                detail = $"移動先URL: {url}";
                node = new WorkflowNode
                {
                    NodeId = nodeId,
                    Type = IrConstants.NodeTypeAction,
                    ActionType = IrConstants.ActionChromeNavigate,
                    Args = JsonSerializer.SerializeToElement(new ChromeNavigateArgs { Url = url.Trim() })
                };
                break;

            case IrConstants.ActionChromeFill:
                title = "✏️ Webの入力欄に文字を入れる";
                detail = $"入力欄「{elementName}」へ「{(isSens ? "******** (保護)" : fillVal)}」を入力";
                node = new WorkflowNode
                {
                    NodeId = nodeId,
                    Type = IrConstants.NodeTypeAction,
                    ActionType = IrConstants.ActionChromeFill,
                    Args = JsonSerializer.SerializeToElement(new ChromeFillArgs
                    {
                        Locator = new SemanticLocator { Role = "textbox", AccessibleName = elementName.Trim(), Label = elementName.Trim() },
                        Value = isSens ? SensitiveDataProtector.RedactedPlaceholder : fillVal,
                        IsSensitive = isSens
                    })
                };
                break;

            case IrConstants.ActionChromeClick:
                title = "👆 Webのボタンをクリックする";
                detail = $"ボタン名: 「{elementName}」";
                node = new WorkflowNode
                {
                    NodeId = nodeId,
                    Type = IrConstants.NodeTypeAction,
                    ActionType = IrConstants.ActionChromeClick,
                    Args = JsonSerializer.SerializeToElement(new ChromeClickArgs
                    {
                        Locator = new SemanticLocator { Role = "button", AccessibleName = elementName.Trim(), Label = elementName.Trim() }
                    })
                };
                break;

            case IrConstants.ActionChromeSubmit:
                title = "📤 Webのフォームを送信する（外部送信）";
                detail = $"送信ボタン「{elementName}」 / 送信先: {url}";
                node = new WorkflowNode
                {
                    NodeId = nodeId,
                    Type = IrConstants.NodeTypeAction,
                    ActionType = IrConstants.ActionChromeSubmit,
                    Args = JsonSerializer.SerializeToElement(new ChromeSubmitArgs
                    {
                        Locator = new SemanticLocator { Role = "button", AccessibleName = elementName.Trim(), Label = elementName.Trim() },
                        DestinationUrl = url.Trim(),
                        ExpectedSuccessSelector = "#submit-success"
                    })
                };
                break;

            case IrConstants.ActionExcelReadRange:
                title = "📖 Excelのセル範囲を読み取る";
                detail = $"ファイル: {Path.GetFileName(excelPath)} / シート: {sheet} / 範囲: {range}";
                node = new WorkflowNode
                {
                    NodeId = nodeId,
                    Type = IrConstants.NodeTypeAction,
                    ActionType = IrConstants.ActionExcelReadRange,
                    Args = JsonSerializer.SerializeToElement(new ExcelReadRangeArgs
                    {
                        WorkbookPath = excelPath.Trim(),
                        SheetName = sheet.Trim(),
                        RangeAddress = range.Trim().ToUpperInvariant()
                    })
                };
                break;

            default:
                title = "📝 Excelのセル範囲に書き込む";
                detail = $"ファイル: {Path.GetFileName(excelPath)} / シート: {sheet} / 範囲: {range}";
                var rows = ParseCsvMatrix(excelText);
                node = new WorkflowNode
                {
                    NodeId = nodeId,
                    Type = IrConstants.NodeTypeAction,
                    ActionType = IrConstants.ActionExcelWriteRange,
                    Args = JsonSerializer.SerializeToElement(new ExcelWriteRangeArgs
                    {
                        WorkbookPath = excelPath.Trim(),
                        SheetName = sheet.Trim(),
                        RangeAddress = range.Trim().ToUpperInvariant(),
                        Values = rows
                    })
                };
                break;
        }

        EasySteps.Add(new EasyStepItem
        {
            StepNumber = num,
            NodeId = nodeId,
            ActionType = actionKind,
            FriendlyTitle = title,
            FriendlyDetail = detail,
            UnderlyingNode = node
        });
    }

    private void RebuildIrJsonFromEasySteps()
    {
        var origins = new List<OriginRule>();
        var children = new List<WorkflowNode>();

        int num = 1;
        foreach (var item in EasySteps)
        {
            item.StepNumber = num;
            item.UnderlyingNode.NodeId = $"step-{num}";
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

        var def = new WorkflowDefinition
        {
            SchemaVersion = IrConstants.CurrentSchemaVersion,
            WorkflowId = string.IsNullOrWhiteSpace(WorkflowIdInput) ? "wf-custom" : WorkflowIdInput.Trim(),
            Name = string.IsNullOrWhiteSpace(WorkflowNameInput) ? "マイマクロ" : WorkflowNameInput.Trim(),
            Version = 1,
            Policy = new PolicyDeclaration
            {
                AllowedOrigins = origins,
                AllowCoordinateClick = false,
                AllowExternalAi = AllowExternalAiPolicy
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
            ? $"✅ 安全チェック合格（問題 0 件）：全 {CurrentPlannedActions.Count} ステップの安全性が確認されました。"
            : $"⚠️ 安全チェックで {CurrentValidationIssues.Count} 件の修正ポイントが見つかりました。「③ 安全チェック・承認」タブを確認してください。";
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

    public async Task ExecuteApprovedMacroAsync()
    {
        if (_currentSavedVersionId is null || _currentApprovalId is null)
        {
            StatusBanner = "⚠️ 実行する前に「💾 保存」と「✅ 承認」を行ってください（または「▶ ワンクリック実行」を押してください）。";
            return;
        }

        try
        {
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

    // --- 操作記録 ＆ AI自動生成 ---
    public void StartEasyRecording()
    {
        _recordingService.StartRecording();
        RecordedEvents.Clear();
        StatusBanner = "🔴 操作記録を開始しました！下の「記録シミュレーター」ボタンを押して操作を記録してください。";
    }

    public void StopEasyRecording()
    {
        _recordingService.StopRecording();
        StatusBanner = $"⏹ 操作記録を停止しました（記録イベント数: {RecordedEvents.Count} 件）。「✨ 記録からマクロを自動作成」を押してください。";
    }

    public void RecordSimulatedWebOpen()
    {
        if (!_recordingService.IsRecording) _recordingService.StartRecording();
        var ev = _recordingService.RecordEvent("Chrome", "Navigate", null, null, null, null, null, StepUrlInput, null, null, null);
        RecordedEvents.Add(ev);
        StatusBanner = $"🔴 Webページ移動（{ev.UrlOrWorkbookPath}）を記録しました。";
    }

    public void RecordSimulatedWebInput(bool asPassword)
    {
        if (!_recordingService.IsRecording) _recordingService.StartRecording();
        var ev = _recordingService.RecordEvent(
            "Chrome",
            "Input",
            asPassword ? "password" : "text",
            "textbox",
            asPassword ? "Password" : StepElementNameInput,
            asPassword ? "パスワード" : StepElementNameInput,
            null,
            StepUrlInput,
            null,
            null,
            asPassword ? "SecretPass123!" : StepFillValueInput);
        RecordedEvents.Add(ev);
        StatusBanner = asPassword
            ? "🔒 パスワード入力を検知したため、値を自動的に [REDACTED] にマスキング保護して記録しました。"
            : $"🔴 文字入力（{ev.TargetLabel}: {ev.CapturedValue}）を記録しました。";
    }

    public void RecordSimulatedExcelWrite()
    {
        if (!_recordingService.IsRecording) _recordingService.StartRecording();
        var ev = _recordingService.RecordEvent(
            "Excel", "Write", null, null, null, null, null,
            StepExcelPathInput, StepExcelSheetInput, StepExcelRangeInput, StepExcelWriteTextInput);
        RecordedEvents.Add(ev);
        StatusBanner = $"🔴 Excel書き込み（{ev.SheetName}!{ev.RangeAddress}）を記録しました。";
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
        if (_aiOrchestrator is not null)
        {
            var policy = new PolicyDeclaration
            {
                AllowedOrigins = new List<OriginRule> { new() { Scheme = "https", Host = "example.com", Port = 443 } },
                AllowExternalAi = AllowExternalAiPolicy
            };
            var outcome = await _aiOrchestrator.GenerateAndValidateIrAsync(candidates, policy, AllowExternalAiPolicy, default);
            RefreshAllListsFromDatabase();

            if (outcome.IsAccepted && outcome.RawJson is not null)
            {
                EditorIrJson = outcome.RawJson;
                ValidateCurrentEditorIr();
                StatusBanner = "✨ AIが記録から安全なマクロを作成し、8段階すべての安全検証に合格しました！";
                return;
            }
            StatusBanner = $"⚠️ AI生成結果が安全基準でブロックされました: {outcome.ErrorReason}";
        }
    }

    public void LoadSelectedVersionIntoEditor(WorkflowVersionRecord version)
    {
        EditorIrJson = version.IrJson;
        WorkflowIdInput = version.WorkflowId;
        WorkflowNameInput = version.Name;
        _currentSavedVersionId = version.VersionId;
        _currentApprovalId = null;
        ValidateCurrentEditorIr();
        StatusBanner = $"📂 保存済みマクロ「{version.Name} (v{version.VersionNumber})」を読み込みました。";
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

        AllExecutions.Clear();
        foreach (var e in _repository.GetAllExecutions()) AllExecutions.Add(e);

        PendingRecoveries.Clear();
        foreach (var r in _repository.GetPendingRecoveries()) PendingRecoveries.Add(r);

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
