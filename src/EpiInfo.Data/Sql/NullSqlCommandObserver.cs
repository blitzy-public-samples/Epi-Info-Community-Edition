namespace EpiInfo.Data.Sql;

/// <summary>
/// The production <see cref="ISqlCommandObserver"/>: it records and logs nothing, so command text,
/// parameter values and field values never reach a log or any other sink.
/// </summary>
/// <remarks>
/// The class holds no state and every member is a no-op, so one instance can serve every request on any
/// thread. Use <see cref="Instance"/> or let dependency injection construct it; the two are equivalent.
/// </remarks>
public sealed class NullSqlCommandObserver : ISqlCommandObserver
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NullSqlCommandObserver"/> class.
    /// </summary>
    public NullSqlCommandObserver()
    {
    }

    /// <summary>
    /// Gets a shared instance.
    /// </summary>
    public static NullSqlCommandObserver Instance { get; } = new();

    /// <inheritdoc />
    /// <remarks>Does nothing.</remarks>
    public void OnExecuting(SqlCommandInfo command)
    {
    }

    /// <inheritdoc />
    /// <remarks>Does nothing; <paramref name="error"/> is left for the caller to rethrow.</remarks>
    public void OnExecuted(SqlCommandInfo command, int? sqlErrorNumber, Exception? error)
    {
    }
}
