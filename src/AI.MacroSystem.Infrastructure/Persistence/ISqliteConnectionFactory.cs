namespace AI.MacroSystem.Infrastructure.Persistence;

using Microsoft.Data.Sqlite;

public interface ISqliteConnectionFactory
{
    SqliteConnection CreateConnection();
    Task<SqliteConnection> CreateOpenConnectionAsync(CancellationToken cancellationToken = default);
}
