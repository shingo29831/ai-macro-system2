// Module: @role: 仕様書第18.6〜18.10/18.12章（状態機械・排他制御・障害復旧・機密保護・不変バージョン管理・追跡性）受け入れテスト
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using AiMacroSystem.Application;
using AiMacroSystem.Domain;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AiMacroSystem.Tests;

public sealed partial class SpecificationAcceptanceTests
{
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
        var rawEv = recorder.RecordEvent("Chrome", "Navigate", null, null, null, null, null, "https://example.com/trace", null, null, null);
        recorder.StopRecording();

        var candidates = normalizer.Normalize(new[] { rawEv });
        var actionNode = new ActionNode
        {
            NodeId = "trace-nav",
            ActionType = IrConstants.ActionChromeNavigate,
            SourceCandidateIds = new List<string> { candidates[0].CandidateId },
            Args = JsonSerializer.SerializeToElement(new ChromeNavigateArgs { Url = "https://example.com/trace" })
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
}
