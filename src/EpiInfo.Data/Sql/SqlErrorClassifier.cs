using System.Collections.Frozen;
using EpiInfo.Domain.Storage;
using Microsoft.Data.SqlClient;

namespace EpiInfo.Data.Sql;

/// <summary>
/// The class a failed store operation falls into, which decides how the API reports it.
/// </summary>
public enum SqlFailureKind
{
    /// <summary>
    /// The caller's cancellation token was cancelled, normally because the client aborted the request. This is
    /// not a database failure: the API writes no response for it.
    /// </summary>
    Cancelled,

    /// <summary>
    /// SQL Server could not be reached or used: a connectivity, login, timeout or deadlock failure, or connection
    /// pool exhaustion. Raised as <see cref="DataStoreUnavailableException"/> and reported as 503.
    /// </summary>
    Unavailable,

    /// <summary>
    /// Any other failure. Raised as <see cref="DataStoreWriteException"/> and reported as 500.
    /// </summary>
    Unexpected,
}

/// <summary>
/// The classification of one failure, as <see cref="SqlErrorClassifier.Classify"/> returns it.
/// </summary>
/// <param name="Kind">The class of the failure.</param>
/// <param name="SqlErrorNumber">
/// The SQL Server error number when the failure is a <see cref="SqlException"/>: for an unavailable failure, the
/// number that decided it; otherwise the exception's primary number. <see langword="null"/> when the
/// failure is not a <see cref="SqlException"/> or carries no error.
/// </param>
public readonly record struct SqlFailure(SqlFailureKind Kind, int? SqlErrorNumber);

/// <summary>
/// How far a write transaction had progressed when it failed, which bounds what is known about the stored data.
/// </summary>
public enum WriteStage
{
    /// <summary>No <c>INSERT</c> or <c>UPDATE</c> has been sent, including a failure while opening or locking.</summary>
    BeforeDml,

    /// <summary>At least one <c>INSERT</c> or <c>UPDATE</c> was sent, and <c>Commit</c> has not been called.</summary>
    DmlSent,

    /// <summary><c>Commit</c> was called and threw or did not return, so the server may or may not have committed.</summary>
    Committing,
}

/// <summary>
/// The single owner of failure classification for the SQL Server store: which failures make the store unavailable
/// (503), which are unexpected (500), which are a client cancellation, and what a failed write reports as its
/// <see cref="WriteOutcome"/>.
/// </summary>
/// <remarks>
/// <para>
/// The classifier only inspects. It retries nothing, logs nothing, and reads no exception message except the
/// SqlClient pool-timeout test in <see cref="Classify"/>. The exceptions it builds carry fixed messages, the caller's
/// fixed operation name and the SQL error number, never SQL text, a parameter value, a connection string or a
/// database message; the original failure is attached only as the inner exception.
/// </para>
/// <para>
/// A <see cref="DataStoreUnavailableException"/> or <see cref="DataStoreWriteException"/> that a store method has
/// already raised, such as the unavailable failure for an absent connection string, is rethrown unchanged by its
/// callers rather than classified again: classification applies to the underlying failure only.
/// </para>
/// </remarks>
public static class SqlErrorClassifier
{
    /// <summary>
    /// The <see cref="Exception.Data"/> key under which <c>SqlRecordRepository</c> stores the type name
    /// (<see cref="string"/>) of a rollback that failed, on the exception it raises for the original failure.
    /// </summary>
    /// <remarks>
    /// The rollback exception's message is never stored, because it may hold SQL text. With
    /// <see cref="RollbackSqlErrorNumberKey"/>, this lets <c>ApiExceptionHandler</c> log the rollback failure while
    /// the original failure keeps deciding the status and the classification.
    /// </remarks>
    public const string RollbackExceptionTypeKey = "EpiInfo.Data.RollbackExceptionType";

    /// <summary>
    /// The <see cref="Exception.Data"/> key under which <c>SqlRecordRepository</c> stores the SQL error number
    /// (<see cref="int"/>) of a rollback that failed with a <see cref="SqlException"/>; absent for any other rollback
    /// failure.
    /// </summary>
    /// <remarks>
    /// Stored beside <see cref="RollbackExceptionTypeKey"/> on the exception raised for the original failure, never
    /// in place of it, and never with the rollback exception's message.
    /// </remarks>
    public const string RollbackSqlErrorNumberKey = "EpiInfo.Data.RollbackSqlErrorNumber";

    /// <summary>The fixed message of the cancellation exception <see cref="ToDataStoreException"/> builds.</summary>
    internal const string CancelledMessage = "The data store operation was cancelled.";

    // SqlClient's pool-exhaustion InvalidOperationException reads "Timeout expired. The timeout period elapsed prior
    // to obtaining a connection from the pool. ..."; SqlClient raises it with no error number to test instead.
    private const string PoolTimeoutMessageFragment = "obtaining a connection from the pool";

    // The server rolls back a deadlock victim's transaction itself, so nothing it sent is kept.
    private const int DeadlockVictimErrorNumber = 1205;

    // The error number and minimum severity class with which SqlClient reports its own transport failures.
    private const int ClientTransportErrorNumber = 0;
    private const byte FatalErrorClass = 20;

    /// <summary>
    /// The SQL Server error numbers that make the store unavailable (503): 53, -1, 2, 40, 64, 121, 233, 10053, 10054,
    /// 10060, 10061 and 11001 (the connection cannot be made, or it broke); 18456 (login failed); 4060 (cannot open
    /// the database); -2 (timeout); 1205 (deadlock victim).
    /// </summary>
    public static IReadOnlySet<int> UnavailableErrorNumbers { get; } = new[]
    {
        53, -1, 2, 40, 64, 121, 233, 10053, 10054, 10060, 10061, 11001,
        18456,
        4060,
        -2,
        DeadlockVictimErrorNumber,
    }.ToFrozenSet();

    /// <summary>Classifies a failure raised by a store operation. Never throws.</summary>
    /// <param name="exception">The failure caught by the store.</param>
    /// <param name="cancellationToken">The token the operation ran under.</param>
    /// <param name="duringOpen">
    /// <see langword="true"/> when the failure came from opening the connection, the only place SqlClient's
    /// pool-exhaustion <see cref="InvalidOperationException"/> means the store is unavailable.
    /// </param>
    /// <returns>
    /// <list type="number">
    /// <item><description>
    /// <see cref="SqlFailureKind.Cancelled"/> whenever <paramref name="cancellationToken"/> is cancelled, whatever
    /// the exception, with the <see cref="SqlException"/> number when there is one.
    /// </description></item>
    /// <item><description>
    /// <see cref="SqlFailureKind.Unavailable"/> for a <see cref="SqlException"/> whose
    /// <see cref="SqlException.Number"/>, or failing that one of whose <see cref="SqlException.Errors"/>, is in
    /// <see cref="UnavailableErrorNumbers"/>, with that number. A <see cref="System.ComponentModel.Win32Exception"/>
    /// counts only inside such a <see cref="SqlException"/>, never on its own.
    /// </description></item>
    /// <item><description>
    /// <see cref="SqlFailureKind.Unavailable"/>, with number 0, for a <see cref="SqlException"/> whose primary error is
    /// number 0 with severity class 20 or above: the form in which SqlClient's managed network layer, used on Linux
    /// and macOS, reports the same connection failures that Windows reports under the listed numbers.
    /// </description></item>
    /// <item><description>
    /// <see cref="SqlFailureKind.Unavailable"/>, with no number, for SqlClient's pool-timeout
    /// <see cref="InvalidOperationException"/> when <paramref name="duringOpen"/> is <see langword="true"/>.
    /// </description></item>
    /// <item><description>
    /// <see cref="SqlFailureKind.Unexpected"/> for everything else: any other <see cref="SqlException"/> (with its
    /// primary number), a malformed connection string's <see cref="ArgumentException"/>, any other
    /// <see cref="InvalidOperationException"/>, an <see cref="OperationCanceledException"/> under a token that was
    /// not cancelled, and any non-SQL exception.
    /// </description></item>
    /// </list>
    /// </returns>
    public static SqlFailure Classify(Exception exception, CancellationToken cancellationToken, bool duringOpen = false)
    {
        var sqlException = exception as SqlException;

        if (cancellationToken.IsCancellationRequested)
        {
            return new SqlFailure(SqlFailureKind.Cancelled, sqlException is null ? null : PrimaryNumber(sqlException));
        }

        if (sqlException is not null)
        {
            return ClassifySqlException(sqlException);
        }

        if (duringOpen
            && exception is InvalidOperationException
            && exception.Message.Contains(PoolTimeoutMessageFragment, StringComparison.OrdinalIgnoreCase))
        {
            return new SqlFailure(SqlFailureKind.Unavailable, null);
        }

        return new SqlFailure(SqlFailureKind.Unexpected, null);
    }

    /// <summary>
    /// Classifies a single SQL Server error number: <see cref="SqlFailureKind.Unavailable"/> when it is in
    /// <see cref="UnavailableErrorNumbers"/>, otherwise <see cref="SqlFailureKind.Unexpected"/>, carrying the number
    /// either way.
    /// </summary>
    /// <param name="number">A SQL Server error number.</param>
    /// <returns>The classification of <paramref name="number"/>.</returns>
    internal static SqlFailure ClassifyNumber(int number) =>
        new(UnavailableErrorNumbers.Contains(number) ? SqlFailureKind.Unavailable : SqlFailureKind.Unexpected, number);

    /// <summary>
    /// Decides the <see cref="WriteOutcome"/> a failed write reports, from how far the transaction had progressed.
    /// </summary>
    /// <param name="stage">How far the write had progressed when it failed.</param>
    /// <param name="rollbackCompleted">
    /// <see langword="true"/> when <c>Rollback</c> was attempted after the failure and completed without error.
    /// </param>
    /// <param name="failure">The classification of the original failure.</param>
    /// <returns>
    /// <see cref="WriteOutcome.NotWritten"/> before any DML, or after DML when the rollback completed or the server
    /// aborted the transaction as a deadlock victim (1205); otherwise <see cref="WriteOutcome.Unknown"/>.
    /// <see cref="WriteOutcome.Written"/> is never returned: <c>RecordService</c> records it once the store reports
    /// a commit.
    /// </returns>
    public static WriteOutcome ResolveWriteOutcome(WriteStage stage, bool rollbackCompleted, SqlFailure failure) =>
        stage switch
        {
            WriteStage.BeforeDml => WriteOutcome.NotWritten,
            WriteStage.DmlSent when rollbackCompleted || failure.SqlErrorNumber == DeadlockVictimErrorNumber =>
                WriteOutcome.NotWritten,
            WriteStage.DmlSent => WriteOutcome.Unknown,
            WriteStage.Committing => WriteOutcome.Unknown,
            // An undefined stage proves nothing about the data, so it reports the one outcome that is never resent.
            _ => WriteOutcome.Unknown,
        };

    /// <summary>
    /// Builds the exception a store raises for a classified failure. The expected use is
    /// <c>throw SqlErrorClassifier.ToDataStoreException(...)</c>.
    /// </summary>
    /// <param name="failure">The classification of <paramref name="inner"/>.</param>
    /// <param name="operation">
    /// A fixed operation name chosen by the calling code, such as <c>InsertRecord</c>; never request input.
    /// </param>
    /// <param name="outcome">
    /// What is known about the stored data: <see cref="WriteOutcome.NotWritten"/> for reads, otherwise the result of
    /// <see cref="ResolveWriteOutcome"/>.
    /// </param>
    /// <param name="inner">The original failure, attached for diagnostics only.</param>
    /// <returns>
    /// A <see cref="DataStoreUnavailableException"/> for <see cref="SqlFailureKind.Unavailable"/>, a
    /// <see cref="DataStoreWriteException"/> for <see cref="SqlFailureKind.Unexpected"/>, and an
    /// <see cref="OperationCanceledException"/> with a fixed message for <see cref="SqlFailureKind.Cancelled"/>. For a
    /// cancellation the caller may instead rethrow the original exception.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="operation"/> is <see langword="null"/>, empty or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="outcome"/> is not a defined value.</exception>
    public static Exception ToDataStoreException(SqlFailure failure, string operation, WriteOutcome outcome, Exception inner) =>
        failure.Kind switch
        {
            SqlFailureKind.Unavailable =>
                new DataStoreUnavailableException(operation, outcome, failure.SqlErrorNumber, inner),
            SqlFailureKind.Cancelled => new OperationCanceledException(CancelledMessage, inner),
            _ => new DataStoreWriteException(operation, outcome, failure.SqlErrorNumber, inner),
        };

    private static SqlFailure ClassifySqlException(SqlException exception)
    {
        int? primary = PrimaryNumber(exception);
        if (primary is int number && ClassifyNumber(number) is { Kind: SqlFailureKind.Unavailable } byNumber)
        {
            return byNumber;
        }

        // SqlClient reports the first error as Number; a listed number further down the collection still decides.
        foreach (SqlError error in exception.Errors)
        {
            if (ClassifyNumber(error.Number) is { Kind: SqlFailureKind.Unavailable } byError)
            {
                return byError;
            }
        }

        if (IsClientTransportFailure(exception))
        {
            return new SqlFailure(SqlFailureKind.Unavailable, primary);
        }

        return new SqlFailure(SqlFailureKind.Unexpected, primary);
    }

    // SqlException.Number reads the first entry of Errors; guarding the count keeps Classify from throwing on an
    // instance that carries no error.
    private static int? PrimaryNumber(SqlException exception) =>
        exception.Errors.Count > 0 ? exception.Number : null;

    // SqlClient's managed network layer, the one it uses on Linux and macOS, reports a connection that cannot be made
    // or that broke (for example a refused connection, an unreachable server or an unresolvable host) as error number
    // 0 with the fatal class 20, and carries the SNI or socket code that Windows reports as Number (40, 53, 10061,
    // 11001, ...) only in its message text, which is not read. The primary error alone is tested: SQL Server itself
    // never raises number 0, and a session the server ended (596, class 21) adds a trailing number-0 error that must
    // not turn its failure into unavailability.
    private static bool IsClientTransportFailure(SqlException exception) =>
        exception.Errors.Count > 0
        && exception.Number == ClientTransportErrorNumber
        && exception.Class >= FatalErrorClass;
}
