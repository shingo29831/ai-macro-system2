namespace AI.MacroSystem.Infrastructure.Persistence;

public enum SqliteErrorCategory
{
    None,
    ForeignKeyViolation,
    UniqueConstraintViolation,
    TriggerAbort,
    BusyOrLocked,
    Other,
}
