namespace AI.MacroSystem.IntegrationTests.Persistence;

using AI.MacroSystem.Domain.Enums;
using AI.MacroSystem.Domain.Models;
using AI.MacroSystem.Infrastructure.Persistence;
using AI.MacroSystem.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Xunit;

public class ExecutionRepositoryTests
{
    [Fact]
    public async Task TransitionExecutionStatus_ExecutesAtomicallyWithCasCondition()
    {
        var connectionString = $"Data Source=tx_test_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        var factory = new SqliteConnectionFactory(connectionString);
        var migrator = new DatabaseMigrator(factory);
        await using var keepAlive = await factory.CreateOpenConnectionAsync();
        await migrator.MigrateAsync();

        var wfRepo = new WorkflowRepository(factory);
        var execRepo = new ExecutionRepository(factory);

        await wfRepo.AddWorkflowAsync(new WorkflowRecord("wf-tx", "Tx Test", "", DateTime.UtcNow, DateTime.UtcNow));
        await wfRepo.AddVersionAsync(new WorkflowVersionRecord("wf-tx", 1, "{}", RiskLevel.Low, DateTime.UtcNow, "tester"));

        var exec = new ExecutionRecord("exec-tx-1", "wf-tx", 1, ExecutionStatus.Running, "user", DateTime.UtcNow, null, null, DateTime.UtcNow);
        await execRepo.CreateExecutionAsync(exec);

        var evt = new ExecutionEventRecord("evt-1", "exec-tx-1", 1, "ExecutionFailed", "{\"reason\":\"Operation failed\"}", DateTime.UtcNow);
        await execRepo.TransitionExecutionStatusAsync(
            "exec-tx-1",
            expectedCurrentStatus: ExecutionStatus.Running,
            newStatus: ExecutionStatus.Failed,
            completedAtUtc: DateTime.UtcNow,
            errorMessage: "Operation failed",
            evt: evt);

        var updated = await execRepo.GetExecutionAsync("exec-tx-1");
        updated.Should().NotBeNull();
        updated!.Status.Should().Be(ExecutionStatus.Failed);
        updated.ErrorMessage.Should().Be("Operation failed");

        var events = await execRepo.GetExecutionEventsAsync("exec-tx-1");
        events.Should().HaveCount(1);
        events[0].EventId.Should().Be("evt-1");
        events[0].EventType.Should().Be("ExecutionFailed");
    }

    [Fact]
    public async Task TransitionExecutionStatus_WhenExpectedStatusMismatches_ThrowsAndRollsBack()
    {
        var connectionString = $"Data Source=cas_test_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        var factory = new SqliteConnectionFactory(connectionString);
        var migrator = new DatabaseMigrator(factory);
        await using var keepAlive = await factory.CreateOpenConnectionAsync();
        await migrator.MigrateAsync();

        var wfRepo = new WorkflowRepository(factory);
        var execRepo = new ExecutionRepository(factory);

        await wfRepo.AddWorkflowAsync(new WorkflowRecord("wf-cas", "CAS Test", "", DateTime.UtcNow, DateTime.UtcNow));
        await wfRepo.AddVersionAsync(new WorkflowVersionRecord("wf-cas", 1, "{}", RiskLevel.Low, DateTime.UtcNow, "tester"));

        var exec = new ExecutionRecord("exec-cas-1", "wf-cas", 1, ExecutionStatus.Pending, "user", null, null, null, DateTime.UtcNow);
        await execRepo.CreateExecutionAsync(exec);

        var evt = new ExecutionEventRecord("evt-cas-1", "exec-cas-1", 1, "ExecutionStarted", "{}", DateTime.UtcNow);

        // 期待状態が Running だが実際は Pending のため競合例外
        var act = () => execRepo.TransitionExecutionStatusAsync(
            "exec-cas-1",
            expectedCurrentStatus: ExecutionStatus.Running,
            newStatus: ExecutionStatus.Succeeded,
            completedAtUtc: DateTime.UtcNow,
            errorMessage: null,
            evt: evt);

        await act.Should().ThrowAsync<AI.MacroSystem.Domain.Exceptions.ConcurrencyConflictException>();

        // 状態が未変更かつイベント未追加であることを確認
        var untouched = await execRepo.GetExecutionAsync("exec-cas-1");
        untouched!.Status.Should().Be(ExecutionStatus.Pending);

        var events = await execRepo.GetExecutionEventsAsync("exec-cas-1");
        events.Should().BeEmpty();
    }

    [Fact]
    public async Task TransitionActionExecutionStatus_FunctionsIndependentlyAndAtomically()
    {
        var connectionString = $"Data Source=act_test_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        var factory = new SqliteConnectionFactory(connectionString);
        var migrator = new DatabaseMigrator(factory);
        await using var keepAlive = await factory.CreateOpenConnectionAsync();
        await migrator.MigrateAsync();

        var wfRepo = new WorkflowRepository(factory);
        var execRepo = new ExecutionRepository(factory);

        await wfRepo.AddWorkflowAsync(new WorkflowRecord("wf-act", "Act Test", "", DateTime.UtcNow, DateTime.UtcNow));
        await wfRepo.AddVersionAsync(new WorkflowVersionRecord("wf-act", 1, "{}", RiskLevel.Low, DateTime.UtcNow, "tester"));

        var exec = new ExecutionRecord("exec-act-1", "wf-act", 1, ExecutionStatus.Running, "user", DateTime.UtcNow, null, null, DateTime.UtcNow);
        await execRepo.CreateExecutionAsync(exec);

        var action = new ActionExecutionRecord(
            "act-1",
            "exec-act-1",
            "node-1",
            1,
            0,
            "browser.navigate",
            ActionStatus.Running,
            "{\"url\":\"https://example.com\"}",
            null,
            null,
            ActionErrorCategory.None,
            DateTime.UtcNow,
            null);
        await execRepo.AddActionExecutionAsync(action);

        // 1. アクション状態をCAS更新＋イベント追加
        var evt = new ExecutionEventRecord("evt-act-1", "exec-act-1", 1, "ActionSucceeded", "{\"nodeId\":\"node-1\"}", DateTime.UtcNow);
        await execRepo.TransitionActionExecutionStatusAsync(
            "act-1",
            expectedCurrentStatus: ActionStatus.Running,
            newStatus: ActionStatus.Succeeded,
            resultJson: "{\"status\":200}",
            errorMessage: null,
            errorCategory: ActionErrorCategory.None,
            completedAtUtc: DateTime.UtcNow,
            evt: evt);

        var updatedAction = await execRepo.GetActionExecutionAsync("act-1");
        updatedAction.Should().NotBeNull();
        updatedAction!.Status.Should().Be(ActionStatus.Succeeded);
        updatedAction.ResultJson.Should().Be("{\"status\":200}");
        updatedAction.ErrorCategory.Should().Be(ActionErrorCategory.None);

        // 2. 実行レコード側は独立して Running のまま
        var execRecord = await execRepo.GetExecutionAsync("exec-act-1");
        execRecord!.Status.Should().Be(ExecutionStatus.Running);

        // 3. 不一致状態での更新は競合例外
        var actConflict = () => execRepo.TransitionActionExecutionStatusAsync(
            "act-1",
            expectedCurrentStatus: ActionStatus.Running,
            newStatus: ActionStatus.Failed,
            resultJson: null,
            errorMessage: "conflict",
            errorCategory: ActionErrorCategory.SystemOrInternal,
            completedAtUtc: DateTime.UtcNow,
            evt: new ExecutionEventRecord("evt-conflict", "exec-act-1", 2, "ActionFailed", "{}", DateTime.UtcNow));

        await actConflict.Should().ThrowAsync<AI.MacroSystem.Domain.Exceptions.ConcurrencyConflictException>();
    }

    [Fact]
    public async Task SystemLock_AcquireAndReleaseAndRecoveryBlocking_FunctionsAccurately()
    {
        var connectionString = $"Data Source=lock_test_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        var factory = new SqliteConnectionFactory(connectionString);
        var migrator = new DatabaseMigrator(factory);
        await using var keepAlive = await factory.CreateOpenConnectionAsync();
        await migrator.MigrateAsync();

        var wfRepo = new WorkflowRepository(factory);
        var execRepo = new ExecutionRepository(factory);
        var lockRepo = new SystemLockRepository(factory);

        await wfRepo.AddWorkflowAsync(new WorkflowRecord("wf-lock", "Lock Test", "", DateTime.UtcNow, DateTime.UtcNow));
        await wfRepo.AddVersionAsync(new WorkflowVersionRecord("wf-lock", 1, "{}", RiskLevel.Low, DateTime.UtcNow, "tester"));

        var exec1 = new ExecutionRecord("exec-1", "wf-lock", 1, ExecutionStatus.Running, "user", DateTime.UtcNow, null, null, DateTime.UtcNow);
        var exec2 = new ExecutionRecord("exec-2", "wf-lock", 1, ExecutionStatus.Pending, "user", DateTime.UtcNow, null, null, DateTime.UtcNow);
        await execRepo.CreateExecutionAsync(exec1);
        await execRepo.CreateExecutionAsync(exec2);

        // 1. exec1が排他ロック取得
        var acquired1 = await lockRepo.TryAcquireExecutionLockAsync("exec-1");
        acquired1.Should().BeTrue();

        // 2. 同時実行のexec2はロック取得に失敗
        var acquired2 = await lockRepo.TryAcquireExecutionLockAsync("exec-2");
        acquired2.Should().BeFalse();

        // 3. exec1が異常終了し復旧待ち (isRecoveryPending: true) でロック解放
        await lockRepo.ReleaseExecutionLockAsync("exec-1", isRecoveryPending: true);

        // 4. 復旧待ちのため新規のexec2はロック取得不可
        var acquired3 = await lockRepo.TryAcquireExecutionLockAsync("exec-2");
        acquired3.Should().BeFalse();

        // 5. 復旧確認が完了
        await lockRepo.ClearRecoveryPendingAsync();

        // 6. 復旧クリア後はexec2がロック取得可能
        var acquired4 = await lockRepo.TryAcquireExecutionLockAsync("exec-2");
        acquired4.Should().BeTrue();
    }
}
