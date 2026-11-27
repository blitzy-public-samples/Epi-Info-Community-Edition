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
/// SqlClient's pool blocking period is disabled (<see cref="PoolBlockingPeriod.NeverBlock"/>) unless the connection
/// string sets <c>Pool Blocking Period</c> (or <c>PoolBlockingPeriod</c>) itself, in which case that setting is kept:
/// <see cref="EffectiveConnectionString"/> puts the default ahead of the configured string, whose own setting SqlClient
/// reads last and keeps. With blocking enabled, the SqlClient default for a server outside Azure SQL, one failed open
/// makes the pool answer every open for the next 5 to 60 seconds with that cached failure, without contacting the
/// server, so the API would go on reporting the database unavailable after SQL Server is back. With it disabled, each
/// open after a failure contacts the server again, so the next request after SQL Server returns succeeds without a
/// restart. That is not a retry: each call still opens once, and the failure of that open is what it reports. The cost
/// is that, while the server stays unreachable, each open takes as long as SqlClient's own attempt, up to the
/// connection string's connect timeout, before it fails; a connection string that prefers the cached failure sets
/// <c>Pool Blocking Period</c> itself.
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
    // The default every connection string with a setting of its own opens behind. It goes first, never last: SqlClient
    // keeps the last value it reads for a keyword, under either of its spellings, so a Pool Blocking Period or
    // PoolBlockingPeriod the configured string sets wins over it.
    private const string NeverBlockDefault = "Pool Blocking Period=NeverBlock;";

    private readonly DatabaseOptions options = options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>
    /// The timeout, in whole seconds, every command must run with: <c>Database:CommandTimeoutSeconds</c>, 30 by default.
    /// Callers pass it to <see cref="SqlExecution"/>.
    /// </summary>
    internal int CommandTimeoutSeconds => options.CommandTimeoutSeconds;

    /// <summary>Opens a new connection to the configured database.</summary>
    /// <remarks>
    /// <para>
    /// The connection opens with <see cref="EffectiveConnectionString"/>: the configured string, behind a default that
    /// disables the pool blocking period unless the string sets it. SqlClient reads and validates that string when the
    /// connection is created, on each call and never at construction, exactly as it reads the configured string alone,
    /// so the default changes neither which strings are accepted nor any other setting. After a failed open, the next
    /// call therefore contacts the server again instead of receiving the pool's cached failure, and succeeds as soon as
    /// SQL Server answers.
    /// </para>
    /// <para>
    /// A connection that fails to open is released here, only after its failure is classified
    /// (<see cref="OpenFailureAsync"/>). An error from that release is discarded, so the exception raised is always the
    /// one for the open failure.
    /// </para>
    /// </remarks>
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
            // Not null: HasConnectionString, checked above, is false for a null string.
            connection = new SqlConnection(EffectiveConnectionString(options.ConnectionString!));
        }
        catch (Exception exception) when (IsConnectionStringFault(exception))
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
            Exception failure = await OpenFailureAsync(connection, operation, exception, cancellationToken)
                .ConfigureAwait(false);
            if (ReferenceEquals(failure, exception))
            {
                throw;
            }

            throw failure;
        }

        return connection;
    }

    /// <summary>
    /// The connection string every connection opens with: <c>Pool Blocking Period=NeverBlock;</c> followed by
    /// <paramref name="configured"/>, verbatim. SqlClient reads a connection string's settings in order and keeps the
    /// last value of each keyword, under either of its spellings, so a <c>Pool Blocking Period</c> or
    /// <c>PoolBlockingPeriod</c> the configured string sets is the one that applies, and the default applies only when
    /// it sets none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The string is not parsed here, so the default changes neither which strings are accepted nor how any setting
    /// is read: <see cref="SqlConnection"/> parses and validates the result as it would the configured string alone.
    /// The default's closing <c>;</c> returns SqlClient's parser to the state it starts in, so the configured string is
    /// read from that state, and SqlClient validates only the value it keeps for each keyword. A configured string
    /// SqlClient rejects, an invalid <c>Pool Blocking Period</c> included, is still rejected; one it accepts, with an
    /// earlier duplicate holding an invalid value or with leading or trailing separators, is still accepted with the
    /// same settings.
    /// </para>
    /// <para>
    /// A configured string of nothing but <c>;</c> separators, white space and NUL terminators holds no setting, and
    /// is returned unchanged. SqlClient refuses to open such a string without contacting any server, or rejects it
    /// when a <c>;</c> follows a NUL; behind the default, the default would be its one setting, and the open would
    /// contact the default local server instead.
    /// </para>
    /// <para>
    /// Internal, rather than private, so that <c>SqlErrorClassifierTests</c> can check the string and clear the pool
    /// it keys.
    /// </para>
    /// </remarks>
    /// <param name="configured">
    /// The configured connection string, not null. It carries the database password, so neither it nor the result is
    /// ever put in a message.
    /// </param>
    /// <returns>
    /// The effective connection string; <paramref name="configured"/> itself when it holds no setting.
    /// </returns>
    internal static string EffectiveConnectionString(string configured) =>
        HoldsNoSetting(configured) ? configured : NeverBlockDefault + configured;

    // SqlClient's parser skips ';' and white space between settings, and after a NUL accepts only NULs and white
    // space, so any other character starts a setting or makes SqlClient reject the string.
    private static bool HoldsNoSetting(string configured) =>
        configured.All(static character => character is ';' or '\0' || char.IsWhiteSpace(character));

    // Every way SqlConnection rejects a connection string before any connection exists. It raises an
    // ArgumentException for a syntax error, an unknown keyword, a value that does not convert, is out of range or is
    // too long, and for settings that contradict each other, such as a Min Pool Size above Max Pool Size; and a
    // NotSupportedException for a keyword this platform does not support, such as Network Library.
    private static bool IsConnectionStringFault(Exception exception) =>
        exception is ArgumentException or NotSupportedException;

    /// <summary>
    /// Classifies a failure to open <paramref name="connection"/>, then releases the connection, and returns the
    /// exception <see cref="OpenAsync"/> raises for it.
    /// </summary>
    /// <remarks>
    /// The order is the point: the failure is classified, and its exception built, before the release. A release that
    /// throws therefore cannot replace the open failure or leave it unclassified, which would let a write's caller
    /// report <see cref="WriteOutcome.Unknown"/> for a save that provably sent nothing. A release error is discarded:
    /// nothing can have been sent on a connection that never opened, and the open failure is what is reported.
    /// </remarks>
    /// <param name="connection">The connection that failed to open; released here.</param>
    /// <param name="operation">The fixed name of the store operation that needed the connection.</param>
    /// <param name="exception">The failure the open raised.</param>
    /// <param name="cancellationToken">The token the open ran under.</param>
    /// <returns>
    /// <paramref name="exception"/> itself, to be rethrown unchanged, when it is an
    /// <see cref="OperationCanceledException"/> the classifier recognises as the client's cancellation. Otherwise the
    /// <see cref="SqlErrorClassifier.ToDataStoreException"/> result for its classification, with
    /// <paramref name="exception"/> as its inner exception: a <see cref="DataStoreUnavailableException"/> or
    /// <see cref="DataStoreWriteException"/> with outcome <see cref="WriteOutcome.NotWritten"/>, or, for any other
    /// failure classified as cancelled, an <see cref="OperationCanceledException"/>, so no SqlException leaves the data
    /// layer from here.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="connection"/> or <paramref name="exception"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="operation"/> is null, empty or whitespace.</exception>
    internal static async Task<Exception> OpenFailureAsync(
        IAsyncDisposable connection,
        string operation,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(exception);

        // Opening is the one place where SqlClient's pool-timeout InvalidOperationException means unavailable.
        SqlFailure failure = SqlErrorClassifier.Classify(exception, cancellationToken, duringOpen: true);
        Exception result = failure.Kind == SqlFailureKind.Cancelled && exception is OperationCanceledException
            ? exception
            : SqlErrorClassifier.ToDataStoreException(failure, operation, WriteOutcome.NotWritten, exception);

        try
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Discarded by design: the open failure, already classified, is what is reported, and a release error must
            // not replace it. Nothing was sent on this connection, so NotWritten still holds.
        }

        return result;
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
/// observer and the caller's cancellation token. <see cref="ExecuteAsync"/> and <see cref="ExecuteScalarAsync{T}"/>
/// also take an optional dispatch callback, which tells a caller that the command passed every local check and is
/// being sent, as distinct from one those checks refused.
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
    /// <param name="onDispatch">
    /// Called once, after the guards, immediately before the command is sent; never when a guard rejects it.
    /// <see langword="null"/> for no callback.
    /// </param>
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
        CancellationToken cancellationToken,
        Action? onDispatch = null) =>
        RunObservedAsync(
            connection,
            transaction,
            operation,
            statement,
            commandTimeoutSeconds,
            observer,
            static (open, command, _) => open.ExecuteAsync(command),
            onDispatch,
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
    /// <param name="onDispatch">
    /// Called once, after the guards, immediately before the command is sent; never when a guard rejects it.
    /// <see langword="null"/> for no callback.
    /// </param>
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
        CancellationToken cancellationToken,
        Action? onDispatch = null) =>
        RunObservedAsync(
            connection,
            transaction,
            operation,
            statement,
            commandTimeoutSeconds,
            observer,
            static (open, command, _) => open.ExecuteScalarAsync<T>(command),
            onDispatch,
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
            onDispatch: null,
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
            onDispatch: null,
            cancellationToken);
    }

    // The one place a command is sent: argument and state guards, then onDispatch, OnExecuting, the command (with any
    // reading it does), and exactly one OnExecuted with the outcome. A guard failure sends nothing, so it is neither
    // dispatched nor observed. onDispatch runs when every local step that can refuse the command has passed, before
    // the observer hears of it, so a callback that throws also sends nothing and leaves nothing half-observed.
    private static async Task<TResult> RunObservedAsync<TResult>(
        SqlConnection connection,
        SqlTransaction? transaction,
        string operation,
        SqlStatement statement,
        int commandTimeoutSeconds,
        ISqlCommandObserver observer,
        Func<SqlConnection, CommandDefinition, CancellationToken, Task<TResult>> run,
        Action? onDispatch,
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

        onDispatch?.Invoke();
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
