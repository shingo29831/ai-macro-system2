namespace AI.MacroSystem.Infrastructure.Persistence;

using Microsoft.Data.Sqlite;

public static class SqliteExceptionClassifier
{
    private const int SqliteConstraint = 19;
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;
    private const int SqliteConstraintForeignKey = 787;
    private const int SqliteConstraintUnique = 2067;
    private const int SqliteConstraintPrimaryKey = 1555;
    private const int SqliteConstraintTrigger = 1811;

    public static SqliteErrorCategory Classify(SqliteException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);

        if (ex.SqliteErrorCode is SqliteBusy or SqliteLocked)
        {
            return SqliteErrorCategory.BusyOrLocked;
        }

        if (ex.SqliteExtendedErrorCode == SqliteConstraintForeignKey ||
            ex.Message.Contains("FOREIGN KEY constraint failed", StringComparison.OrdinalIgnoreCase))
        {
            return SqliteErrorCategory.ForeignKeyViolation;
        }

        if (ex.SqliteExtendedErrorCode is SqliteConstraintUnique or SqliteConstraintPrimaryKey ||
            ex.Message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase))
        {
            return SqliteErrorCategory.UniqueConstraintViolation;
        }

        if (ex.SqliteExtendedErrorCode == SqliteConstraintTrigger ||
            ex.Message.Contains("immutable", StringComparison.OrdinalIgnoreCase))
        {
            return SqliteErrorCategory.TriggerAbort;
        }

        if (ex.SqliteErrorCode == SqliteConstraint)
        {
            return SqliteErrorCategory.Other;
        }

        return SqliteErrorCategory.Other;
    }
}
