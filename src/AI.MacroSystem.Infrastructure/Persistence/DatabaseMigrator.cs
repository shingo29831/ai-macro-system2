namespace AI.MacroSystem.Infrastructure.Persistence;

using System.Globalization;
using System.Reflection;
using Microsoft.Data.Sqlite;

public sealed class DatabaseMigrator
{
    private readonly ISqliteConnectionFactory _connectionFactory;

    public DatabaseMigrator(ISqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            await using (var initCmd = connection.CreateCommand())
            {
                initCmd.Transaction = transaction;
                initCmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS __SchemaMigrations (
                        Version INTEGER PRIMARY KEY,
                        Description TEXT NOT NULL,
                        AppliedAtUtc TEXT NOT NULL
                    );";
                await initCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            var migrations = GetMigrationScripts();
            foreach (var migration in migrations)
            {
                bool isApplied;
                await using (var checkCmd = connection.CreateCommand())
                {
                    checkCmd.Transaction = transaction;
                    checkCmd.CommandText = "SELECT COUNT(1) FROM __SchemaMigrations WHERE Version = @Version;";
                    checkCmd.Parameters.AddWithValue("@Version", migration.Version);
                    var count = Convert.ToInt64(await checkCmd.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
                    isApplied = count > 0;
                }

                if (!isApplied)
                {
                    await using (var scriptCmd = connection.CreateCommand())
                    {
                        scriptCmd.Transaction = transaction;
                        scriptCmd.CommandText = migration.SqlScript;
                        await scriptCmd.ExecuteNonQueryAsync(cancellationToken);
                    }

                    await using (var recordCmd = connection.CreateCommand())
                    {
                        recordCmd.Transaction = transaction;
                        recordCmd.CommandText = @"
                            INSERT INTO __SchemaMigrations (Version, Description, AppliedAtUtc)
                            VALUES (@Version, @Description, @AppliedAtUtc);";
                        recordCmd.Parameters.AddWithValue("@Version", migration.Version);
                        recordCmd.Parameters.AddWithValue("@Description", migration.Description);
                        recordCmd.Parameters.AddWithValue("@AppliedAtUtc", DateTime.UtcNow.ToString("o"));
                        await recordCmd.ExecuteNonQueryAsync(cancellationToken);
                    }
                }
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static List<(int Version, string Description, string SqlScript)> GetMigrationScripts()
    {
        var assembly = typeof(DatabaseMigrator).Assembly;
        var resourceNames = assembly.GetManifestResourceNames()
            .Where(n => n.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n)
            .ToList();

        var list = new List<(int Version, string Description, string SqlScript)>();

        foreach (var resourceName in resourceNames)
        {
            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Could not load migration resource: {resourceName}");
            using var reader = new StreamReader(stream);
            var sql = reader.ReadToEnd();

            var fileName = resourceName.Split('.').Reverse().Skip(1).First();
            var parts = fileName.Split('_', 2);
            var version = int.Parse(parts[0], CultureInfo.InvariantCulture);
            var desc = parts.Length > 1 ? parts[1] : fileName;

            list.Add((version, desc, sql));
        }

        return list;
    }
}
