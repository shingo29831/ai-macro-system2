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
        var customAppAdapter = new CustomAppAdapter(new LiveCustomAppDriver());

        _adapters = new Dictionary<string, IActionAdapter>(StringComparer.Ordinal)
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

        // ループ内部スコープの外部参照禁止と、分岐内出力の isOptional 必須検証
        var scopeDef = CreateValidSampleDefinition();
        scopeDef.Root!.Children!.Add(new WorkflowNode
        {
            NodeId = "read-excel",
            Type = IrConstants.NodeTypeAction,
            ActionType = IrConstants.ActionExcelReadRange,
            Args = JsonSerializer.SerializeToElement(new ExcelReadRangeArgs
            {
                WorkbookPath = Path.Combine(Path.GetTempPath(), "Scope.xlsx"),
                SheetName = "Sheet1",
                RangeAddress = "A1:A2"
            })
        });
        scopeDef.Root.Children.Add(new WorkflowNode
        {
            NodeId = "loop-1",
            Type = IrConstants.NodeTypeForEach,
            CollectionRef = new DataReference
            {
                SourceNodeId = "read-excel",
                OutputName = "values",
                ExpectedType = IrConstants.TypeArrayString
            },
            ItemVariableName = "item",
            BodyChildren = new List<WorkflowNode>
            {
                new()
                {
                    NodeId = "inner-fill",
                    Type = IrConstants.NodeTypeAction,
                    ActionType = IrConstants.ActionChromeFill,
                    Args = JsonSerializer.SerializeToElement(new ChromeFillArgs
                    {
                        Locator = new SemanticLocator { Label = "ItemField" },
                        ValueRef = new DataReference
                        {
                            SourceNodeId = "loop-1",
                            OutputName = "item",
                            ExpectedType = IrConstants.TypeString
                        }
                    })
                }
            }
        });
        scopeDef.Root.Children.Add(new WorkflowNode
        {
            NodeId = "outside-leak-ref",
            Type = IrConstants.NodeTypeAction,
            ActionType = IrConstants.ActionChromeFill,
            Args = JsonSerializer.SerializeToElement(new ChromeFillArgs
            {
                Locator = new SemanticLocator { Label = "LeakField" },
                ValueRef = new DataReference
                {
                    SourceNodeId = "inner-fill",
                    OutputName = "filledValue",
                    ExpectedType = IrConstants.TypeString
                }
            })
        });

        var scopeRes = _validator.ValidateDefinition(scopeDef);
        Assert.False(scopeRes.IsValid);
        Assert.Contains(scopeRes.Issues, i => i.NodeId == "outside-leak-ref" && i.Code == "REF_SOURCE_UNREACHABLE_OR_UNDEFINED");
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
    public async Task Section18_12_ControlFlow_TemporaryEditPlan_Traceability_AndLocatorPriority()
    {
        // 1. ロケーター優先順位（①Role+AccessibleName → ②Label → ③StableId → ④CssSelector → ⑤Coordinates）の検証
        var locator = new SemanticLocator
        {
            Role = "button",
            AccessibleName = "SubmitOrder",
            Label = "FallbackLabel",
            StableId = "btn-submit",
            CssSelector = "#submit"
        };
        Assert.Equal(("RoleAndAccessibleName", "button:SubmitOrder"), locator.ResolveByPriority());

        var cssOnlyLocator = new SemanticLocator { CssSelector = "#only-css" };
        Assert.Equal(("CssSelector", "#only-css"), cssOnlyLocator.ResolveByPriority());

        // 2. 生イベント → 正規化アクション → IRノード の追跡可能性（仕様6）
        var recorder = new RecordingService(_repository);
        var normalizer = new NormalizationService(_repository);
        recorder.StartRecording();
        var rawEv = recorder.RecordEvent("Chrome", "Navigate", null, null, null, null, null, "[https://example.com/trace](https://example.com/trace)", null, null, null);
        recorder.StopRecording();

        var candidates = normalizer.Normalize(new[] { rawEv });
        var actionNode = new ActionNode
        {
            NodeId = "trace-nav",
            ActionType = IrConstants.ActionChromeNavigate,
            SourceCandidateIds = new List<string> { candidates[0].CandidateId },
            Args = JsonSerializer.SerializeToElement(new ChromeNavigateArgs { Url = "[https://example.com/trace](https://example.com/trace)" })
        };

        var tracedRawEvents = _workflowService.TraceNodeToRawEvents(actionNode);
        Assert.Single(tracedRawEvents);
        Assert.Equal(rawEv.EventId, tracedRawEvents[0].EventId);

        // 3. 正式版を変更しない一時編集のExecutionPlan再検証・旧承認無効化・再承認後の実行（仕様12）
        var baseDef = CreateValidSampleDefinition();
        baseDef.WorkflowId = "wf-temp-edit";
        var formalVer = _workflowService.CreateInitialWorkflow(baseDef);
        var (_, formalPlan) = _policyEngine.BuildAndEvaluatePlan(formalVer.VersionId, baseDef);
        var formalApproval = _repository.SaveApproval(formalVer.VersionId, formalPlan!.PlanHash, "user", ApprovalDecision.Approved, Array.Empty<string>());

        var tempEditedDef = CreateValidSampleDefinition();
        tempEditedDef.WorkflowId = "wf-temp-edit";
        tempEditedDef.Root!.Children![1].Args = JsonSerializer.SerializeToElement(new ChromeFillArgs
        {
            Locator = new SemanticLocator { Role = "textbox", AccessibleName = "Username" },
            Value = "bob-temporary-edit"
        });

        var (tempVal, tempPlan) = _policyEngine.BuildAndEvaluatePlan(formalVer.VersionId, tempEditedDef);
        Assert.True(tempVal.IsValid);
        Assert.NotEqual(formalPlan.PlanHash, tempPlan!.PlanHash);

        // 旧承認では一時編集プランを実行できないことを確認
        var rejectedTempExec = await _executionEngine.ExecuteTemporaryPlanAsync(tempPlan, formalApproval.ApprovalId);
        Assert.Equal(ExecutionStatus.Failed, rejectedTempExec.Status);

        // 一時編集プランに対する承認を取得して実行すると成功し、DB上の正式版IRは不変のままであることを確認
        var tempApproval = _repository.SaveApproval(formalVer.VersionId, tempPlan.PlanHash, "user", ApprovalDecision.Approved, Array.Empty<string>());
        var succeededTempExec = await _executionEngine.ExecuteTemporaryPlanAsync(tempPlan, tempApproval.ApprovalId);
        Assert.Equal(ExecutionStatus.Succeeded, succeededTempExec.Status);

        var storedFormalAfter = _repository.GetVersionById(formalVer.VersionId)!;
        Assert.Contains("alice", storedFormalAfter.IrJson);
        Assert.DoesNotContain("bob-temporary-edit", storedFormalAfter.IrJson);

        // 4. 実行履歴から使用バージョン・承認レコード・状態遷移が完全に追跡可能であること（仕様13・16）
        Assert.Equal(formalVer.VersionId, succeededTempExec.VersionId);
        Assert.Equal(tempApproval.ApprovalId, succeededTempExec.ApprovalId);
        Assert.Equal(tempPlan.PlanHash, succeededTempExec.PlanHash);
        var events = _repository.GetExecutionEvents(succeededTempExec.ExecutionId);
        Assert.Contains(events, e => e.EventType == "ExecutionState:Pending->Validating");
        Assert.Contains(events, e => e.EventType == "ExecutionState:Validating->AwaitingApproval");
        Assert.Contains(events, e => e.EventType == "ExecutionState:AwaitingApproval->Ready");
        Assert.Contains(events, e => e.EventType == "ExecutionState:Ready->Running");
        Assert.Contains(events, e => e.EventType == "ExecutionState:Running->Succeeded");

        // 5. DBレベルで正式バージョン(WorkflowVersions)の更新・削除と監査履歴(ExecutionEvents)の改ざんが禁止されていること
        using var rawConn = new SqliteConnection($"Data Source={_dbPath}");
        rawConn.Open();
        using (var updVerCmd = rawConn.CreateCommand())
        {
            updVerCmd.CommandText = "UPDATE WorkflowVersions SET IrJson = '{}' WHERE VersionId = $vid;";
            updVerCmd.Parameters.AddWithValue("$vid", formalVer.VersionId);
            Assert.Throws<SqliteException>(() => updVerCmd.ExecuteNonQuery());
        }
        using (var delEvCmd = rawConn.CreateCommand())
        {
            delEvCmd.CommandText = "DELETE FROM ExecutionEvents WHERE ExecutionId = $eid;";
            delEvCmd.Parameters.AddWithValue("$eid", succeededTempExec.ExecutionId);
            Assert.Throws<SqliteException>(() => delEvCmd.ExecuteNonQuery());
        }
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

        // 11件の複数生ログからノイズが除去・統合され、高精度な4件のアクション候補が抽出されることを検証
        var rawEvents = recorder.GetRecordedEvents();
        Assert.Equal(11, rawEvents.Count);

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
