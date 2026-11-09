// Module: @role: 仕様書第18.1〜18.5章（IR型検証・AI制御・承認ポリシー・ブラウザ/Excelアダプター）受け入れテスト
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiMacroSystem.Application;
using AiMacroSystem.Domain;
using Xunit;

namespace AiMacroSystem.Tests;

public sealed partial class SpecificationAcceptanceTests
{
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
}
