using System.Collections.Frozen;
using System.Globalization;
using System.Net.Sockets;
using System.Resources;
using EpiInfo.Domain.Storage;
using Microsoft.Data.SqlClient;

namespace EpiInfo.Data.Sql;

/// <summary>
/// The class a failed store operation falls into, which decides how the API reports it.
/// </summary>
public enum SqlFailureKind
{
    /// <summary>
    /// The failure was caused by the client aborting the request: the caller's cancellation token was cancelled and
    /// the failure is the cancellation itself, an <see cref="OperationCanceledException"/> or SqlClient's
    /// acknowledgement that it cancelled the running command. This is not a database failure: the API writes no
    /// response for it. A failure that only coincides with a cancelled token keeps its own class.
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
/// The classifier only inspects: it retries nothing and logs nothing. It compares exception messages only to
/// recognise three texts SqlClient itself generates: the pool-timeout message, the attention-acknowledgement
/// ("operation cancelled") error, and the SQL Network Interfaces scaffold <c>(provider: ..., error: ... - ...)</c>
/// that marks an error SqlClient's network interface layer raised, from which it reads only the integer SNI error
/// code. It never copies, returns or logs message text otherwise. The exceptions it
/// builds carry fixed messages, the caller's fixed operation name and the SQL error number, never SQL text, a
/// parameter value, a connection string or a database message; the original failure is attached only as the inner
/// exception.
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

    // SqlClient raises pool exhaustion as an InvalidOperationException with no error number, whose message is its
    // resource PooledOpenTimeoutResource in the current UI culture: "Timeout expired.  The timeout period elapsed
    // prior to obtaining a connection from the pool. ..." in English. IsPoolTimeout matches this English fragment only
    // when SqlClient's resources yield no text for that resource, so a localised message is recognised whenever
    // SqlClient ships its text.
    private const string PoolTimeoutMessageFragment = "obtaining a connection from the pool";

    // The SqlClient resources holding the texts the classifier recognises (see the remarks above): the pool-timeout
    // message, and the "operation cancelled" error SqlClient adds when the server acknowledges a cancelled command.
    private const string SqlClientResourceBaseName = "Microsoft.Data.SqlClient.Resources.Strings";
    private const string PooledOpenTimeoutResource = "ADP_PooledOpenTimeout";
    private const string OperationCancelledResource = "SQL_OperationCancelled";

    // SqlClient's own string resources. A ResourceManager loads and caches each culture's set on first use.
    private static readonly ResourceManager SqlClientStrings =
        new(SqlClientResourceBaseName, typeof(SqlException).Assembly);

    // The error number SqlClient gives each error of its attention acknowledgement; SQL Server never raises number 0.
    private const int AttentionAcknowledgementErrorNumber = 0;

    // The server rolls back a deadlock victim's transaction itself, so nothing it sent is kept.
    private const int DeadlockVictimErrorNumber = 1205;

    // The error number and minimum severity class SqlClient gives every error its network layer raises. SqlClient
    // also gives them to fatal errors of its own that are not transport failures, so the pair alone never decides:
    // IsClientTransportFailure also requires the network layer's text and transport evidence.
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
    /// <param name="cancellationToken">
    /// The token the operation ran under. Its cancellation decides <see cref="SqlFailureKind.Cancelled"/> only together
    /// with evidence that the failure is the cancellation itself; on its own it changes no classification.
    /// </param>
    /// <param name="duringOpen">
    /// <see langword="true"/> when the failure came from opening the connection, the only place SqlClient's
    /// pool-exhaustion <see cref="InvalidOperationException"/> means the store is unavailable.
    /// </param>
    /// <returns>
    /// <list type="number">
    /// <item><description>
    /// <see cref="SqlFailureKind.Cancelled"/> when <paramref name="cancellationToken"/> is cancelled and the failure
    /// is the cancellation itself: an <see cref="OperationCanceledException"/>, <see cref="TaskCanceledException"/>
    /// included, which SqlClient raises for a token cancelled before a command or while opening; or SqlClient's
    /// attention acknowledgement for a command it cancelled, a <see cref="SqlException"/> whose errors all have number
    /// 0 and a severity class below 20, at least one of them carrying SqlClient's own "operation cancelled" text in
    /// the current UI culture or the invariant culture. The number is the <see cref="SqlException"/>'s primary number,
    /// 0 for the acknowledgement, and absent for an <see cref="OperationCanceledException"/>. Any other failure under a
    /// cancelled token, such as a timeout (-2), a permission error or a server error raised alongside the
    /// acknowledgement, keeps the class the items below give it.
    /// </description></item>
    /// <item><description>
    /// <see cref="SqlFailureKind.Unavailable"/> for a <see cref="SqlException"/> whose
    /// <see cref="SqlException.Number"/>, or failing that one of whose <see cref="SqlException.Errors"/>, is in
    /// <see cref="UnavailableErrorNumbers"/>, with that number. A <see cref="System.ComponentModel.Win32Exception"/>
    /// counts only inside such a <see cref="SqlException"/>, never on its own.
    /// </description></item>
    /// <item><description>
    /// <see cref="SqlFailureKind.Unavailable"/>, with number 0, for a <see cref="SqlException"/> whose primary error is
    /// number 0 with severity class 20 or above, has a message ending in the "(provider: …, error: … - …)" text that
    /// SqlClient's network interface layer appends to every error it raises, and carries transport evidence: a
    /// socket, I/O or timeout cause (<see cref="SocketException"/>, <see cref="IOException"/> or
    /// <see cref="TimeoutException"/>) in its <see cref="Exception.InnerException"/> chain or, when it has no inner
    /// exception, a connection-failure SNI error code in that text, such as 40 "could not open a connection", 2
    /// "connection terminated" or 11 "connection timeout". This is the form in which the managed network layer, used
    /// on Linux and macOS, reports the connection failures that Windows reports under the listed numbers. A malformed
    /// connection string's code 25 and the other configuration codes, and a generic fatal number-0 error without the
    /// network layer's text, such as a server-side severe error, stay <see cref="SqlFailureKind.Unexpected"/> (500).
    /// </description></item>
    /// <item><description>
    /// <see cref="SqlFailureKind.Unavailable"/>, with no number, for SqlClient's pool-timeout
    /// <see cref="InvalidOperationException"/> when <paramref name="duringOpen"/> is <see langword="true"/>: its
    /// message equals SqlClient's own pool-timeout text (resource <c>ADP_PooledOpenTimeout</c>) in the current UI
    /// culture, the one SqlClient formats it in, or in the invariant culture. Only when SqlClient yields neither text
    /// does a message containing the English fragment "obtaining a connection from the pool" qualify instead.
    /// SqlClient's non-pooled open timeout is not pool exhaustion.
    /// </description></item>
    /// <item><description>
    /// <see cref="SqlFailureKind.Unexpected"/> for everything else, whether or not
    /// <paramref name="cancellationToken"/> is cancelled: any other <see cref="SqlException"/> (with its primary
    /// number), including SqlClient's attention acknowledgement under a token that was not cancelled; a malformed
    /// connection string's <see cref="ArgumentException"/>; any other <see cref="InvalidOperationException"/>, and the
    /// pool-timeout one outside opening; an <see cref="OperationCanceledException"/> under a token that was not
    /// cancelled; and any non-SQL exception.
    /// </description></item>
    /// </list>
    /// </returns>
    public static SqlFailure Classify(Exception exception, CancellationToken cancellationToken, bool duringOpen = false)
    {
        var sqlException = exception as SqlException;

        if (cancellationToken.IsCancellationRequested && IsCancellation(exception, sqlException))
        {
            return new SqlFailure(SqlFailureKind.Cancelled, sqlException is null ? null : PrimaryNumber(sqlException));
        }

        if (sqlException is not null)
        {
            return ClassifySqlException(sqlException);
        }

        if (duringOpen && IsPoolTimeout(exception))
        {
            return new SqlFailure(SqlFailureKind.Unavailable, null);
        }

        return new SqlFailure(SqlFailureKind.Unexpected, null);
    }

    // The evidence that a failure under a cancelled token is the cancellation itself rather than a failure that only
    // coincides with it. SqlClient raises an OperationCanceledException (TaskCanceledException included) for a token
    // cancelled before a command or while opening; that exception's own token is not the caller's, so its type is the
    // evidence. A command cancelled while running ends in SqlClient's attention acknowledgement instead.
    private static bool IsCancellation(Exception exception, SqlException? sqlException) =>
        exception is OperationCanceledException
        || (sqlException is not null && IsAttentionAcknowledgement(sqlException));

    // When the server acknowledges the attention SqlClient sends for a cancelled command (a cancelled token or
    // SqlCommand.Cancel), SqlClient 5.2.3 raises a SqlException whose errors are all number 0 with class 11: a severe
    // error, then its SQL_OperationCancelled text "Operation cancelled by user." in the current UI culture. It adds
    // that text only on the acknowledgement, never for a command timeout, which is the single error -2. Any error
    // the server raised itself (a nonzero number) or a fatal class means the failure is not the abort alone, so it
    // keeps its own classification.
    private static bool IsAttentionAcknowledgement(SqlException exception)
    {
        if (exception.Errors.Count == 0)
        {
            return false;
        }

        bool acknowledged = false;
        foreach (SqlError error in exception.Errors)
        {
            if (error.Number != AttentionAcknowledgementErrorNumber || error.Class >= FatalErrorClass)
            {
                return false;
            }

            acknowledged |= IsSqlClientText(error.Message, OperationCancelledResource, out _);
        }

        return acknowledged;
    }

    // SqlClient's pool-exhaustion InvalidOperationException: its message is SqlClient's PooledOpenTimeoutResource text,
    // or, only when SqlClient yields no text for that resource, contains the English PoolTimeoutMessageFragment.
    private static bool IsPoolTimeout(Exception exception)
    {
        if (exception is not InvalidOperationException)
        {
            return false;
        }

        string message = exception.Message;
        bool matches = IsSqlClientText(message, PooledOpenTimeoutResource, out bool textAvailable);
        return textAvailable
            ? matches
            : message.Contains(PoolTimeoutMessageFragment, StringComparison.OrdinalIgnoreCase);
    }

    // True when message is, by ordinal equality, SqlClient's text for resourceName in the current UI culture, the one
    // SqlClient formats its messages in, or in the invariant culture. textAvailable is false when neither lookup
    // yields a text, so a caller can tell a resource that a future SqlClient renamed from a message that differs.
    private static bool IsSqlClientText(string? message, string resourceName, out bool textAvailable)
    {
        string? current = SqlClientText(resourceName, CultureInfo.CurrentUICulture);
        string? invariant = SqlClientText(resourceName, CultureInfo.InvariantCulture);
        textAvailable = current is not null || invariant is not null;

        return message is not null
            && (string.Equals(message, current, StringComparison.Ordinal)
                || string.Equals(message, invariant, StringComparison.Ordinal));
    }

    // SqlClient's text for resourceName in culture, falling back as ResourceManager does to the neutral resources,
    // or null when SqlClient provides none. The three exceptions caught are the ones ResourceManager.GetString raises
    // for missing or unusable resources; Classify must never throw.
    private static string? SqlClientText(string resourceName, CultureInfo culture)
    {
        try
        {
            return SqlClientStrings.GetString(resourceName, culture);
        }
        catch (Exception exception) when (exception
            is MissingManifestResourceException
            or MissingSatelliteAssemblyException
            or InvalidOperationException)
        {
            return null;
        }
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

    // SqlClient 5.2.3's SqlException.Number returns 0 when Errors is empty, which would be indistinguishable from
    // SqlClient's own error number 0; the guard reports an exception that carries no error as null instead.
    private static int? PrimaryNumber(SqlException exception) =>
        exception.Errors.Count > 0 ? exception.Number : null;

    // SqlClient's managed network layer, the one it uses on Linux and macOS, reports a connection that cannot be made,
    // that broke or that timed out (a refused connection, an unreachable server, an unresolvable host, a connection
    // reset or closed by the peer) as error number 0 with the fatal class 20, and carries the SNI or socket code that
    // Windows reports as Number (40, 53, 10061, 11001, ...) only in its message. In SqlClient 5.2.3,
    // TdsParser.ProcessSNIError builds every error of that layer with that number and class and the message
    // "{context} (provider: {provider}, error: {code} - {text})", where code is the layer's SNI error code. The layer
    // reports configuration problems the same way, such as a malformed connection string (code 25), and TdsParser
    // raises number 0 with class 20 for failures outside the layer: the DONE_SRVERROR severe error ("A severe error
    // occurred on the current command. ..."), UserInstanceFailure and the bare PreloginError message. So number and
    // class alone never decide; both of these must be present too:
    // - the primary error's message ends with the ProcessSNIError scaffold, the mark of an error the network layer
    //   raised. Its literal parts are the same in every culture SqlClient ships, and no SqlClient resource string
    //   contains "(provider: ";
    // - transport evidence. An InnerException decides alone, because the layer attaches the exception it caught when
    //   it has one: only a socket, I/O or timeout cause in its chain counts (HasTransportCause). Without one, the
    //   scaffold's SNI code must be a connection-failure code (TransportNetworkInterfaceCodes), such as 40 "could not
    //   open a connection", 2 "connection terminated" or 11 "connection timeout".
    // A malformed connection string's code 25 and the other configuration codes, and a generic fatal number-0 error
    // without the network layer's text, stay Unexpected (500). The primary error alone is tested: SQL Server itself
    // never raises number 0, and a session the server killed gives the errors [596 class 21, 0 class 20], whose
    // trailing number-0 error must not turn it into unavailability.
    private static bool IsClientTransportFailure(SqlException exception)
    {
        if (exception.Errors.Count == 0)
        {
            return false;
        }

        SqlError primary = exception.Errors[0];
        if (primary.Number != ClientTransportErrorNumber
            || primary.Class < FatalErrorClass
            || !TryReadNetworkInterfaceErrorCode(primary.Message, out int networkInterfaceCode))
        {
            return false;
        }

        return exception.InnerException is { } cause
            ? HasTransportCause(cause)
            : TransportNetworkInterfaceCodes.Contains(networkInterfaceCode);
    }

    // The SNI error codes with which SqlClient 5.2.3's managed network layer (constants in SNICommon) reports, without
    // an exception, a connection that could not be made, broke or timed out:
    // - 0: a read returned no data because the peer closed the connection (SNITcpHandle, SNINpHandle), reported with
    //   the provider's own text, such as "Success";
    // - 2, ConnTerminatedError: "Connection was terminated" (SNIPacket);
    // - 11, ConnTimeoutError: "Timeout error" (SNITcpHandle, SNIMarsHandle);
    // - 19, ConnNotUsableError: "Physical connection is not usable" (SNIMarsConnection);
    // - 40, ConnOpenFailedError: "Could not open a connection to SQL Server" (SNITcpHandle, SNINpHandle).
    // Every other code raised without an exception names a configuration problem, not an unavailable server: 5 and 8
    // (invalid parameter, protocol not supported), 25 InvalidConnStringError ("Connection string is not valid"), 26
    // ErrorLocatingServerInstance without the socket cause of an instance lookup (SNIProxy, for a LocalDB name the
    // managed layer cannot resolve), 47-49 (MultiSubnetFailover misuse) and 50-57 (LocalDB).
    private static readonly FrozenSet<int> TransportNetworkInterfaceCodes = new[] { 0, 2, 11, 19, 40 }.ToFrozenSet();

    // How many links of an InnerException chain HasTransportCause follows. SqlClient's transport causes sit at most
    // two links deep (an IOException wrapping a SocketException); the bound keeps any longer chain from being walked.
    private const int MaxCauseDepth = 8;

    // True when the chain holds a socket, I/O or timeout cause: a SocketException, an IOException or a
    // TimeoutException, the exceptions the managed network layer catches when a connection cannot be made, breaks or
    // times out (a named pipe it cannot open within the connect timeout raises a TimeoutException). Any other cause,
    // such as the AuthenticationException of a failed TLS handshake or an ObjectDisposedException, is not one.
    private static bool HasTransportCause(Exception cause)
    {
        Exception? current = cause;
        for (int depth = 0; current is not null && depth < MaxCauseDepth; depth++)
        {
            if (current is SocketException or IOException or TimeoutException)
            {
                return true;
            }

            current = current.InnerException;
        }

        return false;
    }

    // The literal parts of the scaffold "{0} (provider: {1}, error: {2} - {3})" that TdsParser.ProcessSNIError formats.
    private const string ProviderMarker = " (provider: ";
    private const string ErrorMarker = ", error: ";
    private const string CodeSeparator = " - ";

    // True when message ends with the scaffold: its last " (provider: ", then ", error: ", one or more ASCII digits
    // with an optional leading '-', then " - ", and a closing ")" as the final character. code is then the SNI error
    // code those digits give, and 0 when the method returns false. The test is ordinal and cannot throw, and no part
    // of the message leaves it except that integer.
    private static bool TryReadNetworkInterfaceErrorCode(string? message, out int code)
    {
        code = 0;
        if (string.IsNullOrEmpty(message) || message[^1] != ')')
        {
            return false;
        }

        int provider = message.LastIndexOf(ProviderMarker, StringComparison.Ordinal);
        if (provider < 0)
        {
            return false;
        }

        int error = message.IndexOf(ErrorMarker, provider + ProviderMarker.Length, StringComparison.Ordinal);
        if (error < 0)
        {
            return false;
        }

        int codeStart = error + ErrorMarker.Length;
        int position = codeStart;
        if (position < message.Length && message[position] == '-')
        {
            position++;
        }

        int digitsStart = position;
        while (position < message.Length && char.IsAsciiDigit(message[position]))
        {
            position++;
        }

        // TryParse rejects digits that overflow int, leaving code 0 and returning false.
        return position > digitsStart
            && message.AsSpan(position).StartsWith(CodeSeparator, StringComparison.Ordinal)
            && int.TryParse(
                message.AsSpan(codeStart, position - codeStart),
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out code);
    }
}
