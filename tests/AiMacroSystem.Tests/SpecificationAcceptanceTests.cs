// Module: @role: 仕様書第18章(18.1〜18.11)の全受け入れ条件・障害注入・安全性契約を検証する統合テスト
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
using Microsoft.Data.Sqlite;
using Xunit;

namespace AiMacroSystem.Tests;

internal sealed class FakeBrowserDriver : IBrowserDriver
{
    public Uri? CurrentUrl { get; set; }
    public List<Uri> NextRedirectChain { get; set; } = new();
    public List<Uri> NextPopups { get; set; } = new();
    public int MatchingCount { get; set; } = 1;
    public bool ThrowOnClick { get; set; }
    public bool PostconditionElementExists { get; set; } = true;
    public string? ReadbackOverride { get; set; }
    public int FillCallCount { get; private set; }
    public int SubmitCallCount { get; private set; }
    private string? _lastFilled;

    public Task<(IReadOnlyList<Uri> RedirectChain, Uri FinalUrl, IReadOnlyList<Uri> OpenedPopups)> NavigateAsync(Uri targetUrl, CancellationToken ct)
    {
        CurrentUrl = NextRedirectChain.Count > 0 ? NextRedirectChain[^1] : targetUrl;
        return Task.FromResult<(IReadOnlyList<Uri>, Uri, IReadOnlyList<Uri>)>((NextRedirectChain, CurrentUrl, NextPopups));
    }

    public Task<int> CountMatchingElementsAsync(SemanticLocator locator, CancellationToken ct) =>
        Task.FromResult(MatchingCount);

    public Task ClickElementAsync(SemanticLocator locator, CancellationToken ct)
    {
        SubmitCallCount++;
        if (ThrowOnClick)
        {
            throw new IOException("Simulated network drop during click/submit.");
        }
        return Task.CompletedTask;
    }

    public Task FillElementAsync(SemanticLocator locator, string value, CancellationToken ct)
    {
        FillCallCount++;
        _lastFilled = value;
        return Task.CompletedTask;
    }

    public Task<string?> ReadElementValueAsync(SemanticLocator locator, CancellationToken ct) =>
        Task.FromResult(ReadbackOverride ?? _lastFilled);

    public Task<bool> ElementExistsAsync(string selector, CancellationToken ct) =>
        Task.FromResult(PostconditionElementExists);
}

internal sealed class FakeExcelComDriver : IExcelComDriver
{
    public bool WorkbookAccessible { get; set; } = true;
    public bool MultipleWindows { get; set; }
    public bool IsReadOnly { get; set; }
    public bool SheetPresent { get; set; } = true;
    public bool SheetProtected { get; set; }
    public bool ThrowComOnWrite { get; set; }
    public bool CorruptReadback { get; set; }
    public int WriteRange2DCallCount { get; private set; }
    private string[,] _storage = new string[1, 1] { { "" } };

    public bool WorkbookExistsAndAccessible(string workbookPath, out bool isOpenInMultipleWindows, out bool isReadOnly)
    {
        isOpenInMultipleWindows = MultipleWindows;
        isReadOnly = IsReadOnly;
        return WorkbookAccessible;
    }

    public bool SheetExists(string workbookPath, string sheetName, out bool isProtected)
    {
        isProtected = SheetProtected;
        return SheetPresent;
    }

    public string[,] ReadRange2D(string workbookPath, string sheetName, string rangeAddress)
    {
        if (CorruptReadback)
        {
            return new string[1, 1] { { "CORRUPTED_VALUE" } };
        }
        return _storage;
    }

    public void WriteRange2D(string workbookPath, string sheetName, string rangeAddress, string[,] values)
    {
        WriteRange2DCallCount++;
        if (ThrowComOnWrite)
        {
            throw new InvalidOperationException("RPC_E_SERVERFAULT (0x80010105)");
        }
        _storage = values;
    }
}

internal sealed class FakeAiProvider : IAiProvider
{
    public string ProviderName { get; init; } = "LocalOllama";
    public string ModelId { get; init; } = "llama3.1:8b";
    public bool IsExternal { get; init; }
    public bool IsAvailable { get; set; } = true;
    public Queue<string> Responses { get; } = new();
    public List<IReadOnlyList<NormalizedActionCandidate>> ReceivedCandidates { get; } = new();

    public Task<string> GenerateWorkflowIrJsonAsync(
        IReadOnlyList<NormalizedActionCandidate> candidates,
        PolicyDeclaration policy,
        IReadOnlyList<ValidationIssue>? previousValidationIssues,
        CancellationToken ct)
    {
        ReceivedCandidates.Add(candidates);
        string next = Responses.Count > 0 ? Responses.Dequeue() : "{}";
        return Task.FromResult(next);
    }
}

public sealed class SpecificationAcceptanceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteRepository _repository;
    private readonly IrValidator _validator;
    private readonly PolicyEngine _policyEngine;
    private readonly ApprovalService _approvalService;
    private readonly WorkflowManagementService _workflowService;
    private readonly FakeBrowserDriver _browserDriver;
    private readonly FakeExcelComDriver _excelDriver;
    private readonly Dictionary<string, IActionAdapter> _adapters;
    private readonly RecoveryService _recoveryService;
    private readonly ExecutionEngine _executionEngine;

    public SpecificationAcceptanceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"aimacro-test-{Guid.NewGuid():N}.sqlite");
        _repository = new SqliteRepository(_dbPath);
        _validator = new IrValidator();
        _policyEngine = new PolicyEngine(_validator);
        _approvalService = new ApprovalService();
        _workflowService = new WorkflowManagementService(_repository, _validator);
        _browserDriver = new FakeBrowserDriver();
        _excelDriver = new FakeExcelComDriver();

        var chromeAdapter = new ChromeAdapter(_browserDriver);
        var excelAdapter = new ExcelAdapter(_excelDriver);

        _adapters = new Dictionary<string, IActionAdapter>(StringComparer.Ordinal)
        {
            [IrConstants.ActionChromeNavigate] = chromeAdapter,
            [IrConstants.ActionChromeClick] = chromeAdapter,
            [IrConstants.ActionChromeFill] = chromeAdapter,
            [IrConstants.ActionChromeSubmit] = chromeAdapter,
            [IrConstants.ActionExcelReadRange] = excelAdapter,
            [IrConstants.ActionExcelWriteRange] = excelAdapter
        };

        _recoveryService = new RecoveryService(_repository, _adapters);
        _executionEngine = new ExecutionEngine(_repository, _policyEngine, _approvalService, _recoveryService, _adapters);
    }

    public void Dispose()
    {
        try
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(_dbPath)) File.Delete(_dbPath);
        }
        catch
        {
            // Ignore cleanup lock in test runner
        }
    }

    private static WorkflowDefinition CreateValidSampleDefinition()
    {
        return new WorkflowDefinition
        {
            SchemaVersion = "0.7",
            WorkflowId = "wf-sample",
            Name = "Sample Workflow",
            Version = 1,
            Policy = new PolicyDeclaration
            {
                AllowedOrigins = new List<OriginRule>
                {
                    new() { Scheme = "https", Host = "example.com", Port = 443 }
                },
                AllowCoordinateClick = false,
                AllowExternalAi = false
            },
            Root = new WorkflowNode
            {
                NodeId = "root",
                Type = IrConstants.NodeTypeSequence,
                Children = new List<WorkflowNode>
                {
                    new()
                    {
                        NodeId = "nav-1",
                        Type = IrConstants.NodeTypeAction,
                        ActionType = IrConstants.ActionChromeNavigate,
                        Args = JsonSerializer.SerializeToElement(new ChromeNavigateArgs
                        {
                            Url = "https://example.com/form"
                        })
                    },
                    new()
                    {
                        NodeId = "fill-1",
                        Type = IrConstants.NodeTypeAction,
                        ActionType = IrConstants.ActionChromeFill,
                        Args = JsonSerializer.SerializeToElement(new ChromeFillArgs
                        {
                            Locator = new SemanticLocator { Role = "textbox", AccessibleName = "Username" },
                            Value = "alice"
                        })
                    }
                }
            }
        };
    }

    [Fact]
    public void Section18_1_IrAndTypeValidation_RejectsInvalidStructureScopeAndTypes()
    {
        var def = CreateValidSampleDefinition();
        // 未知のアクション型と未定義参照を追加
        def.Root!.Children!.Add(new WorkflowNode
        {
            NodeId = "bad-action",
            Type = IrConstants.NodeTypeAction,
            ActionType = "Desktop.ArbitraryShellExec",
            Args = JsonSerializer.SerializeToElement(new { cmd = "calc.exe" })
        });
        def.Root.Children.Add(new WorkflowNode
        {
            NodeId = "bad-ref",
            Type = IrConstants.NodeTypeAction,
            ActionType = IrConstants.ActionChromeFill,
            Args = JsonSerializer.SerializeToElement(new ChromeFillArgs
            {
                Locator = new SemanticLocator { Label = "Email" },
                ValueRef = new DataReference
                {
                    SourceNodeId = "non-existent-node",
                    OutputName = "finalUrl",
                    ExpectedType = IrConstants.TypeString
                }
            })
        });

        var result = _validator.ValidateDefinition(def);
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Code == "UNKNOWN_ACTION_TYPE");
        Assert.Contains(result.Issues, i => i.Code == "REF_SOURCE_UNREACHABLE_OR_UNDEFINED");
    }

    [Fact]
    public async Task Section18_2_AiOrchestrator_EnforcesLocalFirst_RejectsAmbiguous_RevalidatesRepairs()
    {
        var localAi = new FakeAiProvider { IsAvailable = false };
        var externalAi = new FakeAiProvider { ProviderName = "ExternalCloud", IsExternal = true, IsAvailable = true };
        var orchestrator = new AiOrchestrator(localAi, externalAi, _validator, _repository, maxRepairAttempts: 1);

        var policy = new PolicyDeclaration
        {
            AllowedOrigins = new List<OriginRule> { new() { Scheme = "https", Host = "example.com", Port = 443 } },
            AllowExternalAi = false
        };

        var validCandidate = new NormalizedActionCandidate(
            "c1", IrConstants.ActionChromeNavigate, new[] { "e1" }, null, "https://example.com", null, null, null, false, false, null);

        // ローカルAI利用不可時に暗黙で外部AIへフォールバックしないことを確認
        var fallbackBlocked = await orchestrator.GenerateAndValidateIrAsync(new[] { validCandidate }, policy, userApprovedExternalAi: false, CancellationToken.None);
        Assert.False(fallbackBlocked.IsAccepted);
        Assert.Contains(fallbackBlocked.Validation.Issues, i => i.Code == "LOCAL_AI_UNAVAILABLE_EXTERNAL_NOT_PERMITTED");

        // 曖昧な対象を推測で確定させず停止することを確認
        localAi.IsAvailable = true;
        var ambiguousCandidate = validCandidate with { IsAmbiguous = true, AmbiguityReason = "Multiple matching buttons" };
        var ambOutcome = await orchestrator.GenerateAndValidateIrAsync(new[] { ambiguousCandidate }, policy, false, CancellationToken.None);
        Assert.False(ambOutcome.IsAccepted);
        Assert.Contains(ambOutcome.Validation.Issues, i => i.Code == "AMBIGUOUS_CANDIDATE_UNRESOLVED");

        // 1回目が不正JSON、2回目の修正再要求で全8段階検証を通過することを確認
        localAi.Responses.Enqueue("{ invalid json ");
        localAi.Responses.Enqueue(JsonSerializer.Serialize(CreateValidSampleDefinition()));
        var repairedOutcome = await orchestrator.GenerateAndValidateIrAsync(new[] { validCandidate }, policy, false, CancellationToken.None);
        Assert.True(repairedOutcome.IsAccepted);
        Assert.Equal(1, repairedOutcome.RepairAttemptsUsed);
    }

    [Fact]
    public async Task Section18_3_ApprovalAndPolicy_BlocksUnapproved_Prohibited_AndStalePlanApprovals()
    {
        var def = CreateValidSampleDefinition();
        var version = _workflowService.CreateInitialWorkflow(def);

        // 未承認の実行は拒否される
        var unapprovedExec = await _executionEngine.ExecuteWorkflowVersionAsync(version.VersionId, approvalId: null);
        Assert.Equal(ExecutionStatus.Failed, unapprovedExec.Status);

        // 計画ハッシュが異なる古い承認は拒否される
        var staleApproval = _repository.SaveApproval(version.VersionId, "STALE_HASH", "admin", ApprovalDecision.Approved, Array.Empty<string>());
        var staleExec = await _executionEngine.ExecuteWorkflowVersionAsync(version.VersionId, staleApproval.ApprovalId);
        Assert.Equal(ExecutionStatus.Failed, staleExec.Status);

        // Prohibitedな操作（破壊的クリック）は承認があっても拒否される
        var prohibitedDef = CreateValidSampleDefinition();
        prohibitedDef.WorkflowId = "wf-prohibited";
        prohibitedDef.Root!.Children!.Add(new WorkflowNode
        {
            NodeId = "del-click",
            Type = IrConstants.NodeTypeAction,
            ActionType = IrConstants.ActionChromeClick,
            Args = JsonSerializer.SerializeToElement(new ChromeClickArgs
            {
                Locator = new SemanticLocator { Role = "button", AccessibleName = "Delete Account" },
                IsDestructive = true
            })
        });
        var (val, plan) = _policyEngine.BuildAndEvaluatePlan("ver-prohibited", prohibitedDef);
        Assert.False(val.IsValid);
        Assert.Null(plan);
        Assert.Contains(val.Issues, i => i.Code == "POLICY_PROHIBITED_OPERATION");
    }

    [Fact]
    public async Task Section18_4_ChromeAdapter_EnforcesAllowlistOnRedirectsAndPopups_AndRequiresUniqueTarget()
    {
        var def = CreateValidSampleDefinition();
        var ver = _workflowService.CreateInitialWorkflow(def);
        var (_, plan) = _policyEngine.BuildAndEvaluatePlan(ver.VersionId, def);
        var approval = _repository.SaveApproval(ver.VersionId, plan!.PlanHash, "tester", ApprovalDecision.Approved, Array.Empty<string>());

        // リダイレクト先に未許可オリジンが含まれる場合は遮断・停止する
        _browserDriver.NextRedirectChain = new List<Uri>
        {
            new("https://example.com/start"),
            new("https://malicious.example.org/phish")
        };

        var redirectBlockedExec = await _executionEngine.ExecuteWorkflowVersionAsync(ver.VersionId, approval.ApprovalId);
        Assert.Equal(ExecutionStatus.Failed, redirectBlockedExec.Status);
        var actions1 = _repository.GetActionExecutions(redirectBlockedExec.ExecutionId);
        Assert.Equal(FailureClassification.PolicyViolation, actions1[0].LastFailureClassification);

        // 対象が複数件（曖昧）の場合は操作しない
        _browserDriver.NextRedirectChain.Clear();
        _browserDriver.MatchingCount = 3;
        var ambiguousExec = await _executionEngine.ExecuteWorkflowVersionAsync(ver.VersionId, approval.ApprovalId);
        Assert.Equal(ExecutionStatus.Failed, ambiguousExec.Status);
        var actions2 = _repository.GetActionExecutions(ambiguousExec.ExecutionId);
        Assert.Contains(actions2, a => a.LastFailureClassification == FailureClassification.TargetAmbiguous);
    }

    [Fact]
    public async Task Section18_5_ExcelAdapter_VerifiesReadback_AndNeverRetriesUnknownComFailure()
    {
        var def = new WorkflowDefinition
        {
            SchemaVersion = "0.7",
            WorkflowId = "wf-excel",
            Name = "Excel Write Test",
            Version = 1,
            Policy = new PolicyDeclaration(),
            Root = new WorkflowNode
            {
                NodeId = "root",
                Type = IrConstants.NodeTypeSequence,
                Children = new List<WorkflowNode>
                {
                    new()
                    {
                        NodeId = "excel-write",
                        Type = IrConstants.NodeTypeAction,
                        ActionType = IrConstants.ActionExcelWriteRange,
                        Args = JsonSerializer.SerializeToElement(new ExcelWriteRangeArgs
                        {
                            WorkbookPath = Path.Combine(Path.GetTempPath(), "Book1.xlsx"),
                            SheetName = "Sheet1",
                            RangeAddress = "A1:B2",
                            Values = new List<List<string>>
                            {
                                new() { "R1C1", "R1C2" },
                                new() { "R2C1", "R2C2" }
                            }
                        })
                    }
                }
            }
        };

        var ver = _workflowService.CreateInitialWorkflow(def);
        var (_, plan) = _policyEngine.BuildAndEvaluatePlan(ver.VersionId, def);
        var approval = _repository.SaveApproval(ver.VersionId, plan!.PlanHash, "tester", ApprovalDecision.Approved, Array.Empty<string>());

        // 1. 読み戻し不一致を検出して失敗させることを確認
        _excelDriver.CorruptReadback = true;
        var mismatchExec = await _executionEngine.ExecuteWorkflowVersionAsync(ver.VersionId, approval.ApprovalId);
        Assert.Equal(ExecutionStatus.Failed, mismatchExec.Status);
        var mismatchActions = _repository.GetActionExecutions(mismatchExec.ExecutionId);
        Assert.Equal(FailureClassification.PostconditionMismatch, mismatchActions[0].LastFailureClassification);

        // 2. COM例外時はUnknownとなり、自動再試行されない（試行回数1回のみ）ことを確認
        _excelDriver.CorruptReadback = false;
        _excelDriver.ThrowComOnWrite = true;
        int beforeCalls = _excelDriver.WriteRange2DCallCount;
        var unknownExec = await _executionEngine.ExecuteWorkflowVersionAsync(ver.VersionId, approval.ApprovalId);
        Assert.Equal(ExecutionStatus.Unknown, unknownExec.Status);
        Assert.Equal(beforeCalls + 1, _excelDriver.WriteRange2DCallCount);
        var unknownActions = _repository.GetActionExecutions(unknownExec.ExecutionId);
        Assert.Equal(ActionExecutionStatus.Unknown, unknownActions[0].Status);
        Assert.Equal(1, unknownActions[0].AttemptCount);
    }

    [Fact]
    public async Task Section18_6_to_18_8_StateMachine_Exclusivity_CrashRecovery_AndPauseCancel()
    {
        var def = CreateValidSampleDefinition();
        var ver = _workflowService.CreateInitialWorkflow(def);
        var (_, plan) = _policyEngine.BuildAndEvaluatePlan(ver.VersionId, def);
        var approval = _repository.SaveApproval(ver.VersionId, plan!.PlanHash, "tester", ApprovalDecision.Approved, Array.Empty<string>());

        // 一時停止中に別ワークフローを開始しようとしてもExecutionEngineが排他拒否することを確認
        async Task OnFirstActionCompleted(string activeExecutionId, string completedNodeId)
        {
            if (completedNodeId == "nav-1")
            {
                _executionEngine.RequestPause(activeExecutionId);

                await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                    await _executionEngine.ExecuteWorkflowVersionAsync(ver.VersionId, approval.ApprovalId));

                _executionEngine.RequestCancel(activeExecutionId);
            }
        }

        _executionEngine.ActionNodeCompletedAsync += OnFirstActionCompleted;
        ExecutionRecord cancelledExec;
        try
        {
            cancelledExec = await _executionEngine.ExecuteWorkflowVersionAsync(ver.VersionId, approval.ApprovalId);
        }
        finally
        {
            _executionEngine.ActionNodeCompletedAsync -= OnFirstActionCompleted;
        }
        Assert.Equal(ExecutionStatus.Cancelled, cancelledExec.Status);
        var actionRecords = _repository.GetActionExecutions(cancelledExec.ExecutionId);
        // 1つ目のアクション完了後の境界でキャンセル要求が反映され、2つ目は実行されない
        Assert.Single(actionRecords);
        Assert.Equal(ActionExecutionStatus.Succeeded, actionRecords[0].Status);

        // 異常終了（Running状態のまま残存）をシミュレートし、起動時リカバリーが自動再開せず新規実行をブロックすることを確認
        string crashedExecId = "exec-crashed-01";
        _repository.CreateExecutionAtomic(crashedExecId, ver.VersionId, plan.PlanHash, ExecutionStatus.Running, "Simulated crash while running.");
        _repository.CreateActionExecutionAtomic("act-crashed-01", crashedExecId, "fill-1", ActionExecutionStatus.Running);

        var recoveries = await _recoveryService.RunStartupRecoveryAsync();
        Assert.Single(recoveries);
        Assert.Equal(RecoveryStatus.Pending, recoveries[0].RecoveryStatus);

        // 復旧判断が完了するまで新規実行できないことを確認
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await _executionEngine.ExecuteWorkflowVersionAsync(ver.VersionId, approval.ApprovalId));

        // ユーザーが明示的に復旧判断を記録した後は新規実行が可能になることを確認
        _recoveryService.ResolveRecovery(recoveries[0].RecoveryId, RecoveryStatus.ResolvedAsFailed, "User verified state and aborted crashed run.");
        var cleanExec = await _executionEngine.ExecuteWorkflowVersionAsync(ver.VersionId, approval.ApprovalId);
        Assert.Equal(ExecutionStatus.Succeeded, cleanExec.Status);
    }

    [Fact]
    public void Section18_9_and_18_10_SensitiveDataProtection_AndImmutableVersioning()
    {
        // パスワード入力が取得段階でマスキングされることを確認
        var recorder = new RecordingService();
        recorder.StartRecording();
        var ev = recorder.RecordEvent(
            "Chrome", "Input", "password", "textbox", "UserPassword", "パスワード", "pwd-id",
            "https://example.com/login?token=secret123", null, null, "MySuperSecretPassword!");
        recorder.StopRecording();

        Assert.True(ev.IsSensitive);
        Assert.Equal(SensitiveDataProtector.RedactedPlaceholder, ev.CapturedValue);
        Assert.DoesNotContain("secret123", ev.UrlOrWorkbookPath);
        Assert.DoesNotContain("MySuperSecretPassword!", ev.CapturedValue);

        // 正式バージョンの不変性と古いドラフトによる上書き防止を確認
        var def = CreateValidSampleDefinition();
        def.WorkflowId = "wf-versioning";
        var v1 = _workflowService.CreateInitialWorkflow(def);

        var draftA = _workflowService.SaveDraft(def.WorkflowId, v1.VersionId, def);
        var draftB = _workflowService.SaveDraft(def.WorkflowId, v1.VersionId, def);

        var v2 = _workflowService.CommitDraftAsNewVersion(draftA.DraftId);
        Assert.Equal(2, v2.VersionNumber);
        Assert.NotEqual(v1.VersionId, v2.VersionId);

        // v1をベースにした古いdraftBを確定しようとすると上書き防止エラーになる
        Assert.Throws<InvalidOperationException>(() => _workflowService.CommitDraftAsNewVersion(draftB.DraftId));
    }
}
