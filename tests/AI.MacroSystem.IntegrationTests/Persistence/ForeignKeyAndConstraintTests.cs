namespace AI.MacroSystem.IntegrationTests.Persistence;

using AI.MacroSystem.Domain.Enums;
using AI.MacroSystem.Domain.Models;
using AI.MacroSystem.Infrastructure.Persistence;
using AI.MacroSystem.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

public class ForeignKeyAndConstraintTests
{
    [Fact]
    public async Task ForeignKeyViolation_IsEnforcedAndClassifiedCorrectly()
    {
        var connectionString = $"Data Source=fk_test_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        var factory = new SqliteConnectionFactory(connectionString);
        var migrator = new DatabaseMigrator(factory);
        await using var keepAlive = await factory.CreateOpenConnectionAsync();
        await migrator.MigrateAsync();

        var repo = new WorkflowRepository(factory);

        // 存在しないWorkflowIdでVersionを追加
        var invalidVersion = new WorkflowVersionRecord("non-existent-wf", 1, "{}", RiskLevel.Low, DateTime.UtcNow, "tester");
        var act = async () => await repo.AddVersionAsync(invalidVersion);

        var ex = await act.Should().ThrowAsync<SqliteException>();
        SqliteExceptionClassifier.Classify(ex.Which).Should().Be(SqliteErrorCategory.ForeignKeyViolation);
    }

    [Fact]
    public async Task ActionExecutions_SupportsDistinctInstancesForSameNodeInLoops()
    {
        var connectionString = $"Data Source=action_exec_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        var factory = new SqliteConnectionFactory(connectionString);
        var migrator = new DatabaseMigrator(factory);
        await using var keepAlive = await factory.CreateOpenConnectionAsync();
        await migrator.MigrateAsync();

        var wfRepo = new WorkflowRepository(factory);
        var execRepo = new ExecutionRepository(factory);

        await wfRepo.AddWorkflowAsync(new WorkflowRecord("wf-loop", "Loop Test", "", DateTime.UtcNow, DateTime.UtcNow));
        await wfRepo.AddVersionAsync(new WorkflowVersionRecord("wf-loop", 1, "{}", RiskLevel.Low, DateTime.UtcNow, "tester"));

        var exec = new ExecutionRecord("exec-1", "wf-loop", 1, ExecutionStatus.Running, "user", DateTime.UtcNow, null, null, DateTime.UtcNow);
        await execRepo.CreateExecutionAsync(exec);

        // 同一NodeIdがループによって複数回実行されるシナリオ (反復0回目と1回目)
        var action1 = new ActionExecutionRecord(
            "act-exec-1", "exec-1", "loop_node_a", 1, 0, "Delay", ExecutionStatus.Succeeded, "{}", null, null, DateTime.UtcNow, DateTime.UtcNow);
        var action2 = new ActionExecutionRecord(
            "act-exec-2", "exec-1", "loop_node_a", 2, 1, "Delay", ExecutionStatus.Succeeded, "{}", null, null, DateTime.UtcNow, DateTime.UtcNow);

        await execRepo.AddActionExecutionAsync(action1);
        await execRepo.AddActionExecutionAsync(action2);

        var list = await execRepo.GetActionExecutionsAsync("exec-1");
        list.Should().HaveCount(2);
        list[0].ActionExecutionId.Should().Be("act-exec-1");
        list[0].IterationIndex.Should().Be(0);
        list[1].ActionExecutionId.Should().Be("act-exec-2");
        list[1].IterationIndex.Should().Be(1);

        // 同一ExecutionOrderで重複登録しようとすると一意制約違反
        var duplicateOrderAction = new ActionExecutionRecord(
            "act-exec-dup", "exec-1", "other_node", 1, 0, "Log", ExecutionStatus.Running, "{}", null, null, DateTime.UtcNow, null);
        var act = async () => await execRepo.AddActionExecutionAsync(duplicateOrderAction);
        var ex = await act.Should().ThrowAsync<SqliteException>();
        SqliteExceptionClassifier.Classify(ex.Which).Should().Be(SqliteErrorCategory.UniqueConstraintViolation);
    }
}
