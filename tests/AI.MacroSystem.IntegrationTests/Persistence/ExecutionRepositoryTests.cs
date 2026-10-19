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
    public async Task UpdateExecutionStatusAndAppendEvent_ExecutesAtomically()
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

        var evt = new ExecutionEventRecord("evt-1", "exec-tx-1", 1, "StateChanged", "{\"to\":\"Failed\"}", DateTime.UtcNow);
        await execRepo.UpdateExecutionStatusAndAppendEventAsync(
            "exec-tx-1",
            ExecutionStatus.Failed,
            DateTime.UtcNow,
            "Operation failed",
            evt);

        var updated = await execRepo.GetExecutionAsync("exec-tx-1");
        updated.Should().NotBeNull();
        updated!.Status.Should().Be(ExecutionStatus.Failed);
        updated.ErrorMessage.Should().Be("Operation failed");

        var events = await execRepo.GetExecutionEventsAsync("exec-tx-1");
        events.Should().HaveCount(1);
        events[0].EventId.Should().Be("evt-1");
        events[0].EventType.Should().Be("StateChanged");
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
