using System.Data;

namespace EpiInfo.Data.Sql;

/// <summary>
/// One parameter bound to an observed SQL command.
/// </summary>
/// <param name="Name">The parameter name as bound, for example <c>@p0</c>.</param>
/// <param name="DbType">The explicit <see cref="System.Data.DbType"/> the parameter is bound with.</param>
/// <param name="Value">
/// The value bound to the parameter. A database null (<see cref="DBNull.Value"/>) is reported as
/// <see langword="null"/>, so an observer never sees <see cref="DBNull"/>.
/// </param>
public sealed record SqlCommandParameterInfo(string Name, DbType DbType, object? Value);

/// <summary>
/// One SQL command that <c>EpiInfo.Data</c> sends to SQL Server, as an <see cref="ISqlCommandObserver"/>
/// receives it.
/// </summary>
/// <remarks>
/// The same instance is passed to <see cref="ISqlCommandObserver.OnExecuting"/> and
/// <see cref="ISqlCommandObserver.OnExecuted"/> for one execution. Records compare by value and two
/// identical commands can be in flight at once, so an observer that pairs the two calls keys on the
/// instance (reference equality), not on the record's value.
/// </remarks>
/// <param name="Operation">
/// The fixed operation name chosen by the calling code, such as <c>InsertRecord</c> or
/// <c>ReadMetadata</c>. It is never derived from request input.
/// </param>
/// <param name="CommandText">
/// The exact SQL text sent. Every value travels in <paramref name="Parameters"/>, never in this text.
/// </param>
/// <param name="Parameters">The parameters bound to the command, in binding order.</param>
/// <param name="ClientConnectionId">
/// <c>SqlConnection.ClientConnectionId</c> of the connection the command runs on. Commands that share a
/// connection, such as the statements of one save transaction, share this id.
/// </param>
/// <param name="InTransaction">
/// <see langword="true"/> when the command runs with a <c>SqlTransaction</c>; otherwise
/// <see langword="false"/>.
/// </param>
public sealed record SqlCommandInfo(
    string Operation,
    string CommandText,
    IReadOnlyList<SqlCommandParameterInfo> Parameters,
    Guid ClientConnectionId,
    bool InTransaction);

/// <summary>
/// Observes every SQL command <c>EpiInfo.Data</c> sends, metadata and catalogue reads included, with its
/// text, parameters and outcome.
/// </summary>
/// <remarks>
/// <para>
/// This is a test seam: tests substitute a recorder to assert on the commands an operation issued. The
/// production implementation, <c>NullSqlCommandObserver</c>, records and logs nothing, so command text and
/// parameter values never reach a log.
/// </para>
/// <para>
/// Implementations must not throw and must not change control flow: an observer cannot cancel, retry or
/// replace a command, its result or its error. One instance serves every request, so calls can arrive
/// concurrently from several threads.
/// </para>
/// </remarks>
public interface ISqlCommandObserver
{
    /// <summary>
    /// Called immediately before <paramref name="command"/> is sent to SQL Server.
    /// </summary>
    /// <param name="command">The command about to run.</param>
    void OnExecuting(SqlCommandInfo command);

    /// <summary>
    /// Called once after <paramref name="command"/> completes or fails. For a command whose rows are read
    /// through a data reader, the call follows the reading of those rows, so a failure while fetching them
    /// is reported here too.
    /// </summary>
    /// <param name="command">The instance passed to <see cref="OnExecuting"/> for this execution.</param>
    /// <param name="sqlErrorNumber">
    /// <c>SqlException.Number</c> when the failure is a <c>SqlException</c>, for example -2 for a command
    /// timeout; otherwise <see langword="null"/>, including on success.
    /// </param>
    /// <param name="error">
    /// The exception that ended the command, or <see langword="null"/> on success. Observing it does not
    /// handle it: the caller rethrows it unchanged.
    /// </param>
    void OnExecuted(SqlCommandInfo command, int? sqlErrorNumber, Exception? error);
}
