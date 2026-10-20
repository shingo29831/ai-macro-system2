// Module: @role: 第16章の全10画面機能（一覧・記録・編集・検証・レビュー・承認・実行・履歴・復旧・設定）を統合するWPF ViewModel
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using AiMacroSystem.Application;
using AiMacroSystem.Domain;
using AiMacroSystem.Infrastructure;

namespace AiMacroSystem.Presentation;

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

    private string _statusBanner = "Ready";
    private string _editorIrJson = string.Empty;
    private string _localAiEndpoint = "http://localhost:11434";
    private string _localAiModel = "llama3.1:8b";
    private bool _allowExternalAiPolicy;

    public MainViewModel(
        SqliteRepository repository,
        WorkflowManagementService workflowService,
        RecordingService recordingService,
        NormalizationService normalizationService,
        PolicyEngine policyEngine,
        ApprovalService approvalService,
        ExecutionEngine executionEngine,
        RecoveryService recoveryService)
    {
        _repository = repository;
        _workflowService = workflowService;
        _recordingService = recordingService;
        _normalizationService = normalizationService;
        _policyEngine = policyEngine;
        _approvalService = approvalService;
        _executionEngine = executionEngine;
        _recoveryService = recoveryService;
    }

    public ObservableCollection<WorkflowVersionRecord> WorkflowVersions { get; } = new();
    public ObservableCollection<RawRecordedEvent> RecordedEvents { get; } = new();
    public ObservableCollection<ValidationIssue> CurrentValidationIssues { get; } = new();
    public ObservableCollection<PlannedActionItem> CurrentPlannedActions { get; } = new();
    public ObservableCollection<ExecutionEventRecord> ExecutionHistoryEvents { get; } = new();
    public ObservableCollection<RecoveryRecord> PendingRecoveries { get; } = new();

    public string StatusBanner
    {
        get => _statusBanner;
        set { _statusBanner = value; OnPropertyChanged(); }
    }

    public string EditorIrJson
    {
        get => _editorIrJson;
        set { _editorIrJson = value; OnPropertyChanged(); }
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

    public async Task InitializeAndCheckRecoveryAsync()
    {
        var recoveries = await _recoveryService.RunStartupRecoveryAsync();
        PendingRecoveries.Clear();
        foreach (var rec in recoveries)
        {
            PendingRecoveries.Add(rec);
        }

        StatusBanner = PendingRecoveries.Count > 0
            ? $"[RECOVERY REQUIRED] {PendingRecoveries.Count} unfinished execution(s) detected. New runs are blocked until resolved."
            : "System Initialized. All integrity checks passed.";
    }

    public void ValidateCurrentEditorIr()
    {
        CurrentValidationIssues.Clear();
        CurrentPlannedActions.Clear();

        var validator = new IrValidator();
        var (res, def) = validator.ValidateJson(EditorIrJson);
        foreach (var issue in res.Issues)
        {
            CurrentValidationIssues.Add(issue);
        }

        if (res.IsValid && def is not null)
        {
            var (planVal, plan) = _policyEngine.BuildAndEvaluatePlan("preview-ver", def);
            foreach (var issue in planVal.Issues)
            {
                CurrentValidationIssues.Add(issue);
            }
            if (plan is not null)
            {
                foreach (var act in plan.PlannedActions)
                {
                    CurrentPlannedActions.Add(act);
                }
            }
        }

        StatusBanner = CurrentValidationIssues.Count == 0
            ? "Validation Succeeded: 0 issues."
            : $"Validation Blocked: {CurrentValidationIssues.Count} concrete issue(s) found.";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
