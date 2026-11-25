using System.Data;
using System.Data.Common;
using Dapper;
using EpiInfo.Domain.Storage;
using Microsoft.Data.SqlClient;

namespace EpiInfo.Data.Sql;

/// <summary>
/// Opens the SQL Server connections of the data layer from <see cref="DatabaseOptions"/>, and reports every failure to
/// open one as the store's own exceptions, so no SqlClient exception leaves the data layer from here.
/// </summary>
/// <remarks>
/// <para>
/// Each call opens a new connection that the caller owns and disposes (<c>await using</c>); SqlClient pools the
/// physical connections. Nothing is retried, cached or logged here.
/// </para>
/// <para>
/// The connection string carries the database password, so it never appears in an exception this class raises: the
/// exceptions carry their fixed messages, the caller's fixed operation name and, at most, a SQL error number. The
/// original failure is attached only as the inner exception, which the API never logs or returns.
/// </para>
/// <para>The instance holds only immutable options and is safe to share across threads.</para>
/// </remarks>
/// <param name="options">The connection string and command timeout.</param>
internal sealed class SqlConnectionFactory(DatabaseOptions options)
{
    private readonly DatabaseOptions options = options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>
    /// The timeout, in whole seconds, every command must run with: <c>Database:CommandTimeoutSeconds</c>, 30 by default.
    /// Callers pass it to <see cref="SqlExecution"/>.
    /// </summary>
    internal int CommandTimeoutSeconds => options.CommandTimeoutSeconds;

    /// <summary>Opens a new connection to the configured database.</summary>
    /// <param name="operation">
    /// The fixed name of the store operation that needs the connection, such as <c>InsertAsync</c>; never request input.
    /// </param>
    /// <param name="cancellationToken">Cancels the open when the client aborts the request.</param>
    /// <returns>An open connection, owned and disposed by the caller.</returns>
    /// <exception cref="ArgumentException"><paramref name="operation"/> is null, empty or whitespace.</exception>
    /// <exception cref="DataStoreUnavailableException">
    /// No connection string is configured, detected before any connection object exists; or opening failed in a way
    /// <see cref="SqlErrorClassifier"/> classifies as unavailable (unreachable server, failed login, database that
    /// cannot be opened, timeout, pool exhaustion). Its outcome is <see cref="WriteOutcome.NotWritten"/>.
    /// </exception>
    /// <exception cref="DataStoreWriteException">
    /// The connection string is malformed, or opening failed for any other reason. Its outcome is
    /// <see cref="WriteOutcome.NotWritten"/>.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> cancelled the open.</exception>
    internal async Task<SqlConnection> OpenAsync(string operation, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        if (!options.HasConnectionString)
        {
            throw new DataStoreUnavailableException(operation, WriteOutcome.NotWritten);
        }

        SqlConnection connection;
        try
        {
            connection = new SqlConnection(options.ConnectionString);
        }
        catch (ArgumentException exception)
        {
            // SqlClient parses the string here and rejects a malformed one; that is a configuration fault (500), not an
            // unavailable server.
            throw new DataStoreWriteException(operation, WriteOutcome.NotWritten, sqlErrorNumber: null, exception);
        }

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await connection.DisposeAsync().ConfigureAwait(false);

            // Opening is the one place where SqlClient's pool-timeout InvalidOperationException means unavailable.
            SqlFailure failure = SqlErrorClassifier.Classify(exception, cancellationToken, duringOpen: true);

            // A cancellation SqlClient raised as OperationCanceledException propagates unchanged. Any other failure
            // classified as cancelled becomes the classifier's OperationCanceledException, so no SqlException leaves
            // the data layer from here.
            if (failure.Kind == SqlFailureKind.Cancelled && exception is OperationCanceledException)
            {
                throw;
            }

            throw SqlErrorClassifier.ToDataStoreException(failure, operation, WriteOutcome.NotWritten, exception);
        }

        return connection;
    }
}

/// <summary>
/// The single path through which <c>EpiInfo.Data</c> sends a <see cref="SqlStatement"/> to SQL Server. Every command
/// runs with an explicit timeout, binds every value as a typed input parameter, and is reported to the
/// <see cref="ISqlCommandObserver"/> before it is sent and once after it completes or fails.
/// </summary>
/// <remarks>
/// <para>
/// Every method takes an open connection, the transaction the command joins (or <see langword="null"/>), the fixed
/// operation name, the statement, the timeout from <see cref="SqlConnectionFactory.CommandTimeoutSeconds"/>, the
/// observer and the caller's cancellation token.
/// </para>
/// <para>
/// A command failure is reported to the observer and rethrown unchanged: classifying it, and deciding what a failed
/// write means for the stored data, belong to the caller. An observer that throws never changes a command's result or
/// error; its exception is discarded.
/// </para>
/// <para>
/// The statement text is sent exactly as given. Values travel only in parameters, each bound with the
/// <see cref="DbType"/> and size the statement carries.
/// </para>
/// </remarks>
internal static class SqlExecution
{
    private const string ConnectionNotOpenMessage = "A SQL command runs only on an open connection.";
    private const string ForeignTransactionMessage =
        "The transaction is not an active transaction of the connection the command runs on.";

    /// <summary>Runs a statement that returns no rows, such as an <c>INSERT</c> or <c>UPDATE</c>.</summary>
    /// <param name="connection">An open connection.</param>
    /// <param name="transaction">The transaction the command joins, or <see langword="null"/>.</param>
    /// <param name="operation">The fixed operation name reported to the observer.</param>
    /// <param name="statement">The statement to run.</param>
    /// <param name="commandTimeoutSeconds">The command timeout in whole seconds; at least 1.</param>
    /// <param name="observer">The command observer.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns>The number of rows affected.</returns>
    /// <exception cref="InvalidOperationException">
    /// The connection is not open, or the transaction is not an active transaction of it.
    /// </exception>
    internal static Task<int> ExecuteAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        string operation,
        SqlStatement statement,
        int commandTimeoutSeconds,
        ISqlCommandObserver observer,
        CancellationToken cancellationToken) =>
        RunObservedAsync(
            connection,
            transaction,
            operation,
            statement,
            commandTimeoutSeconds,
            observer,
            static (open, command, _) => open.ExecuteAsync(command),
            cancellationToken);

    /// <summary>Runs a statement and returns the first column of its first row.</summary>
    /// <typeparam name="T">The type the value is converted to.</typeparam>
    /// <param name="connection">An open connection.</param>
    /// <param name="transaction">The transaction the command joins, or <see langword="null"/>.</param>
    /// <param name="operation">The fixed operation name reported to the observer.</param>
    /// <param name="statement">The statement to run.</param>
    /// <param name="commandTimeoutSeconds">The command timeout in whole seconds; at least 1.</param>
    /// <param name="observer">The command observer.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns>The value, or the default of <typeparamref name="T"/> when there is no row or the value is NULL.</returns>
    /// <exception cref="InvalidOperationException">
    /// The connection is not open, or the transaction is not an active transaction of it.
    /// </exception>
    internal static Task<T?> ExecuteScalarAsync<T>(
        SqlConnection connection,
        SqlTransaction? transaction,
        string operation,
        SqlStatement statement,
        int commandTimeoutSeconds,
        ISqlCommandObserver observer,
        CancellationToken cancellationToken) =>
        RunObservedAsync(
            connection,
            transaction,
            operation,
            statement,
            commandTimeoutSeconds,
            observer,
            static (open, command, _) => open.ExecuteScalarAsync<T>(command),
            cancellationToken);

    /// <summary>Runs a query and maps every row of its first result set to <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">The row type.</typeparam>
    /// <param name="connection">An open connection.</param>
    /// <param name="transaction">The transaction the command joins, or <see langword="null"/>.</param>
    /// <param name="operation">The fixed operation name reported to the observer.</param>
    /// <param name="statement">The statement to run.</param>
    /// <param name="commandTimeoutSeconds">The command timeout in whole seconds; at least 1.</param>
    /// <param name="observer">The command observer.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns>The rows, fully read before the observer hears that the command completed.</returns>
    /// <exception cref="InvalidOperationException">
    /// The connection is not open, or the transaction is not an active transaction of it.
    /// </exception>
    internal static Task<IReadOnlyList<T>> QueryAsync<T>(
        SqlConnection connection,
        SqlTransaction? transaction,
        string operation,
        SqlStatement statement,
        int commandTimeoutSeconds,
        ISqlCommandObserver observer,
        CancellationToken cancellationToken) =>
        RunObservedAsync(
            connection,
            transaction,
            operation,
            statement,
            commandTimeoutSeconds,
            observer,
            static async (open, command, _) =>
            {
                IEnumerable<T> rows = await open.QueryAsync<T>(command).ConfigureAwait(false);
                return (IReadOnlyList<T>)rows.AsList().AsReadOnly();
            },
            cancellationToken);

    /// <summary>
    /// Runs a statement and hands its data reader to <paramref name="read"/>, for multiple result sets or typed row
    /// reads. Reading and disposing the reader happen inside the observed command, so a failure while fetching rows,
    /// such as a timeout mid-read, is reported to the observer as the command's failure.
    /// </summary>
    /// <typeparam name="T">The result <paramref name="read"/> builds.</typeparam>
    /// <param name="connection">An open connection.</param>
    /// <param name="transaction">The transaction the command joins, or <see langword="null"/>.</param>
    /// <param name="operation">The fixed operation name reported to the observer.</param>
    /// <param name="statement">The statement to run.</param>
    /// <param name="commandTimeoutSeconds">The command timeout in whole seconds; at least 1.</param>
    /// <param name="observer">The command observer.</param>
    /// <param name="cancellationToken">Cancels the command; also passed to <paramref name="read"/>.</param>
    /// <param name="read">
    /// Reads the rows. The reader is valid only until the returned task completes; it is disposed afterwards.
    /// </param>
    /// <returns>The result of <paramref name="read"/>.</returns>
    /// <exception cref="InvalidOperationException">
    /// The connection is not open, or the transaction is not an active transaction of it.
    /// </exception>
    internal static Task<T> ReadAsync<T>(
        SqlConnection connection,
        SqlTransaction? transaction,
        string operation,
        SqlStatement statement,
        int commandTimeoutSeconds,
        ISqlCommandObserver observer,
        CancellationToken cancellationToken,
        Func<DbDataReader, CancellationToken, Task<T>> read)
    {
        ArgumentNullException.ThrowIfNull(read);

        return RunObservedAsync(
            connection,
            transaction,
            operation,
            statement,
            commandTimeoutSeconds,
            observer,
            async (open, command, token) =>
            {
                DbDataReader reader = await open.ExecuteReaderAsync(command).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    return await read(reader, token).ConfigureAwait(false);
                }
            },
            cancellationToken);
    }

    // The one place a command is sent: argument and state guards, then OnExecuting, the command (with any reading it
    // does), and exactly one OnExecuted with the outcome. A guard failure sends nothing, so it is not observed.
    private static async Task<TResult> RunObservedAsync<TResult>(
        SqlConnection connection,
        SqlTransaction? transaction,
        string operation,
        SqlStatement statement,
        int commandTimeoutSeconds,
        ISqlCommandObserver observer,
        Func<SqlConnection, CommandDefinition, CancellationToken, Task<TResult>> run,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(statement);
        // SqlClient reads a timeout of 0 as "wait indefinitely", which would defeat the configured limit.
        ArgumentOutOfRangeException.ThrowIfLessThan(commandTimeoutSeconds, 1);
        ArgumentNullException.ThrowIfNull(observer);

        if (connection.State != ConnectionState.Open)
        {
            throw new InvalidOperationException(ConnectionNotOpenMessage);
        }

        // A committed or rolled-back SqlTransaction reports no connection, so this also rejects a finished one.
        if (transaction is not null && !ReferenceEquals(transaction.Connection, connection))
        {
            throw new InvalidOperationException(ForeignTransactionMessage);
        }

        var command = new CommandDefinition(
            commandText: statement.Text,
            parameters: new StatementParameters(statement.Parameters),
            transaction: transaction,
            commandTimeout: commandTimeoutSeconds,
            commandType: CommandType.Text,
            flags: CommandFlags.Buffered,
            cancellationToken: cancellationToken);
        SqlCommandInfo info = Describe(operation, statement, connection, transaction);

        NotifyExecuting(observer, info);
        TResult result;
        try
        {
            result = await run(connection, command, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            NotifyExecuted(observer, info, (exception as SqlException)?.Number, exception);
            throw;
        }

        NotifyExecuted(observer, info, sqlErrorNumber: null, error: null);
        return result;
    }

    private static SqlCommandInfo Describe(
        string operation,
        SqlStatement statement,
        SqlConnection connection,
        SqlTransaction? transaction)
    {
        var parameters = new SqlCommandParameterInfo[statement.Parameters.Count];
        for (int index = 0; index < parameters.Length; index++)
        {
            SqlParameterValue parameter = statement.Parameters[index];
            parameters[index] = new SqlCommandParameterInfo(
                parameter.Name,
                parameter.DbType,
                parameter.Value is DBNull ? null : parameter.Value);
        }

        return new SqlCommandInfo(
            operation,
            statement.Text,
            Array.AsReadOnly(parameters),
            connection.ClientConnectionId,
            InTransaction: transaction is not null);
    }

    private static void NotifyExecuting(ISqlCommandObserver observer, SqlCommandInfo info)
    {
        try
        {
            observer.OnExecuting(info);
        }
        catch (Exception)
        {
            // Discarded by design: an observer is a test seam and must never cancel, fail or replace a command. This
            // file logs nothing, because the command text and parameter values must not reach a log.
        }
    }

    private static void NotifyExecuted(ISqlCommandObserver observer, SqlCommandInfo info, int? sqlErrorNumber, Exception? error)
    {
        try
        {
            observer.OnExecuted(info, sqlErrorNumber, error);
        }
        catch (Exception)
        {
            // Discarded by design, as in NotifyExecuting: the command's own result or error stands.
        }
    }

    // Binds the statement's parameters exactly as given: input direction, the explicit DbType and, when set, the size.
    // Dapper's DynamicParameters is deliberately not used: it rewrites "{=name}" literal tokens in the command text,
    // inlining a numeric value even inside a quoted identifier, so a catalogue name holding such a token would put a
    // value into SQL text. Through this binder Dapper never touches the text.
    private sealed class StatementParameters(IReadOnlyList<SqlParameterValue> values) : SqlMapper.IDynamicParameters
    {
        public void AddParameters(IDbCommand command, SqlMapper.Identity identity)
        {
            foreach (SqlParameterValue value in values)
            {
                // DbType is set before Value, so SqlClient binds the declared type and never infers one from the value.
                var parameter = new SqlParameter
                {
                    ParameterName = value.Name,
                    DbType = value.DbType,
                    Direction = ParameterDirection.Input,
                    Value = value.Value,
                };

                if (value.Size is int size)
                {
                    parameter.Size = size;
                }

                command.Parameters.Add(parameter);
            }
        }
    }
}
