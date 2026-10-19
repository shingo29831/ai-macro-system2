namespace AI.MacroSystem.IntegrationTests.Persistence;

using AI.MacroSystem.Domain.Enums;
using AI.MacroSystem.Domain.Models;
using AI.MacroSystem.Infrastructure.Persistence;
using AI.MacroSystem.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

public class WorkflowVersionImmutabilityTests
{
    [Fact]
    public async Task WorkflowVersions_AreProtectedByDatabaseTriggersAgainstUpdatesAndDeletions()
    {
        var connectionString = $"Data Source=immutability_test_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        var factory = new SqliteConnectionFactory(connectionString);
        var migrator = new DatabaseMigrator(factory);
        await using var keepAlive = await factory.CreateOpenConnectionAsync();
        await migrator.MigrateAsync();

        var repo = new WorkflowRepository(factory);

        var workflow = new WorkflowRecord("wf-imm-1", "Immutable Test", "Desc", DateTime.UtcNow, DateTime.UtcNow);
        await repo.AddWorkflowAsync(workflow);

        var version = new WorkflowVersionRecord("wf-imm-1", 1, "{}", RiskLevel.Low, DateTime.UtcNow, "tester");
        await repo.AddVersionAsync(version);

        // 1. 直接SQLでのUPDATE試行がトリガーにより拒否されることを検証
        await using var updateCmd = keepAlive.CreateCommand();
        updateCmd.CommandText = "UPDATE WorkflowVersions SET WorkflowJson = '{\"altered\":true}' WHERE WorkflowId = 'wf-imm-1' AND Version = 1;";
        var updateAct = async () => await updateCmd.ExecuteNonQueryAsync();
        var exUpdate = await updateAct.Should().ThrowAsync<SqliteException>();
        SqliteExceptionClassifier.Classify(exUpdate.Which).Should().Be(SqliteErrorCategory.TriggerAbort);

        // 2. 直接SQLでのDELETE試行がトリガーにより拒否されることを検証
        await using var deleteCmd = keepAlive.CreateCommand();
        deleteCmd.CommandText = "DELETE FROM WorkflowVersions WHERE WorkflowId = 'wf-imm-1' AND Version = 1;";
        var deleteAct = async () => await deleteCmd.ExecuteNonQueryAsync();
        var exDelete = await deleteAct.Should().ThrowAsync<SqliteException>();
        SqliteExceptionClassifier.Classify(exDelete.Which).Should().Be(SqliteErrorCategory.TriggerAbort);

        // 内容が不変であることを再確認
        var fetched = await repo.GetVersionAsync("wf-imm-1", 1);
        fetched.Should().NotBeNull();
        fetched!.WorkflowJson.Should().Be("{}");
    }
}
