namespace AI.MacroSystem.IntegrationTests.Persistence;

using AI.MacroSystem.Infrastructure.Persistence;
using FluentAssertions;
using Xunit;

public class MigrationTests
{
    [Fact]
    public async Task MigrateAsync_ExecutesSuccessfullyAndIsIdempotent()
    {
        var connectionString = $"Data Source=migration_test_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        var factory = new SqliteConnectionFactory(connectionString);
        var migrator = new DatabaseMigrator(factory);

        // インメモリキャッシュ共有を維持するため接続を開いて保持
        await using var keepAlive = await factory.CreateOpenConnectionAsync();

        // 1回目の実行
        await migrator.MigrateAsync();

        // 2回目の実行 (冪等性・整合性の確認)
        var act = async () => await migrator.MigrateAsync();
        await act.Should().NotThrowAsync();

        await using var cmd = keepAlive.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM __SchemaMigrations;";
        var count = Convert.ToInt64(await cmd.ExecuteScalarAsync());
        count.Should().Be(1);
    }
}
