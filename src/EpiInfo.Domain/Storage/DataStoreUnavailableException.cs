namespace EpiInfo.Domain.Storage;

/// <summary>
/// Raised by a data store when SQL Server is unavailable: the one way a store reports that condition, so the API
/// never handles a SQL client type. <c>ApiExceptionHandler</c> maps it to a 503 ProblemDetails response, and the API
/// never retries the operation.
/// </summary>
/// <remarks>
/// <para>
/// The <c>EpiInfo.Data</c> store raises it for two kinds of failure only. The first is a failure its
/// <c>SqlErrorClassifier</c> classifies as unavailable:
/// </para>
/// <list type="bullet">
/// <item><description>
/// a SQL exception, raised while opening the connection or during a command, whose error number is one of 53, -1, 2,
/// 40, 64, 121, 233, 10053, 10054, 10060, 10061 or 11001 (the connection cannot be made, or it broke), 18456 (login
/// failed), 4060 (cannot open the database), -2 (command timeout) or 1205 (deadlock victim);
/// </description></item>
/// <item><description>
/// a SQL exception that carries none of those numbers, whose primary error is number 0 with severity class 20 or
/// above and has a message ending in the "(provider: …, error: … - …)" text that SqlClient's network interface layer
/// appends to every error it raises, and which carries transport evidence: a socket, I/O or timeout cause or, without
/// one, a connection-failure SNI error code in that text, such as 40 "could not open a connection", 2 "connection
/// terminated" or 11 "connection timeout". That is the form in which SqlClient's managed network layer, used on Linux
/// and macOS, reports the connection failures that Windows reports under the numbers above, such as a refused
/// connection, an unreachable server or an unresolvable host. <see cref="SqlErrorNumber"/> is then 0. A malformed
/// connection string's code 25 and the other configuration codes, and a generic fatal number-0 error without the
/// network layer's text, such as a server-side severe error, remain a 500;
/// </description></item>
/// <item><description>SqlClient connection-pool exhaustion while opening a connection.</description></item>
/// </list>
/// <para>
/// The second is a condition the store detects itself, with no exception for the classifier to classify, so
/// <see cref="SqlErrorNumber"/> is <see langword="null"/> and <see cref="Outcome"/> is
/// <see cref="WriteOutcome.NotWritten"/>:
/// </para>
/// <list type="bullet">
/// <item><description>
/// an absent or blank <c>ConnectionStrings:EpiInfo</c>, detected before any connection is attempted;
/// </description></item>
/// <item><description>
/// an open the outage gate ends early, once an open has failed with one of the SQL exceptions above or past its
/// deadline (next item), meaning SQL Server could not be reached or used: every other open then waiting for a
/// connection is released at once, rather than waiting out a connect timeout of its own, and while that outage is
/// recorded every open but the single probe is refused before any connection is attempted. Nothing was sent. The
/// probe that opens, or that fails in a way showing the server answered, clears the outage;
/// </description></item>
/// <item><description>
/// an open, normal or probe, that has no connection when its deadline passes: the connection string's connect
/// timeout plus a one-second grace, none for a connect timeout of 0. SqlClient cannot stop an attempt it has begun,
/// so without the deadline an open waiting behind an attempt begun for a request that has since ended would answer
/// only after about two connect timeouts. Such an open is evidence that the server did not answer, so it records the
/// outage as the SQL exceptions above do. Nothing was sent. The grace keeps SqlClient's own pool-exhaustion failure,
/// due at the connect timeout, ahead of the deadline;
/// </description></item>
/// <item><description>
/// the refusal of the per-form write lock that a write requests through <c>sp_getapplock</c> inside its transaction,
/// before its first DML statement: <c>sp_getapplock</c> returns -1, the lock request timed out, or -3, the request
/// was chosen as a deadlock victim. These are <c>sp_getapplock</c> return codes, not SQL Server error numbers, so the
/// -1 here is not the connection error number -1 above, and the refusal raises no SQL exception. A command timeout
/// while the lock request waits is a SQL exception numbered -2, which the classifier classifies as the first kind.
/// </description></item>
/// </list>
/// <para>
/// Any other failure surfaces differently: the API reports it as a 500, except a cancellation caused by the client
/// aborting the request, which writes no response.
/// </para>
/// <para>How the store chooses <see cref="Outcome"/>:</para>
/// <list type="bullet">
/// <item><description>
/// <see cref="WriteOutcome.NotWritten"/> for every read, and for any write that failed before its first DML
/// statement was sent;
/// </description></item>
/// <item><description>
/// <see cref="WriteOutcome.NotWritten"/> for a deadlock (1205), because the server aborted the transaction, and for
/// a failure after which the rollback completed without error;
/// </description></item>
/// <item><description>
/// <see cref="WriteOutcome.Unknown"/> when DML was sent and neither a commit nor a completed rollback can be
/// confirmed, for example because the commit call failed or the rollback itself failed.
/// </description></item>
/// </list>
/// <para>
/// On <c>POST</c>, <c>PUT</c> and <c>DELETE</c> of records the handler reports the <c>writeOutcome</c> member from the
/// request's <c>WriteOutcomeFeature</c> first, which holds <see cref="WriteOutcome.Written"/> once the store has
/// reported a commit and <see cref="WriteOutcome.NotWritten"/> once it has returned a result that wrote nothing or
/// while no store write call was issued. Only once a store write call was issued, and while it is outstanding, does
/// the handler report <see cref="Outcome"/>.
/// </para>
/// <para>
/// Redaction: <see cref="Exception.Message"/> is always <see cref="FixedMessage"/>, because no constructor takes a
/// message, so <see cref="Exception.Message"/> never carries SQL text, a parameter or field value, a connection string
/// or a database exception's message. That guarantee covers the message alone: the inner exception, attached for
/// diagnostics, may hold any of these, and so may <see cref="Exception.ToString"/>, which includes it. The handler logs
/// only the exception type, <see cref="SqlErrorNumber"/>, <see cref="Operation"/> and the trace id; it never logs
/// <see cref="Exception.Message"/> or any inner message. Never pass this exception to a logger as its exception
/// argument, because the logger records <see cref="Exception.ToString"/>.
/// </para>
/// </remarks>
public sealed class DataStoreUnavailableException : Exception
{
    /// <summary>The message of every instance: fixed text that names no server, database, value or statement.</summary>
    public const string FixedMessage = "The data store is unavailable.";

    /// <summary>Initialises the exception for one failed store operation.</summary>
    /// <param name="operation">
    /// A fixed identifier of the store operation that failed, chosen by the store and never derived from data; by
    /// convention the <c>IDataStore</c> method name, for example <c>nameof(IDataStore.UpdateAsync)</c>.
    /// </param>
    /// <param name="outcome">What is known about the stored data, chosen by the rules in the remarks.</param>
    /// <param name="sqlErrorNumber">
    /// The SQL Server error number when the cause was a SQL exception; <see langword="null"/> otherwise, as for a
    /// missing connection string, pool exhaustion or a refused form write lock, whose <c>sp_getapplock</c> return code
    /// (-1 timed out, -3 deadlock victim) is not an error number and is never passed here.
    /// </param>
    /// <param name="innerException">The underlying failure, attached for diagnostics only; may be <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="operation"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="operation"/> is empty or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="outcome"/> is not a defined <see cref="WriteOutcome"/> value. The message names the parameter only.
    /// </exception>
    public DataStoreUnavailableException(
        string operation,
        WriteOutcome outcome,
        int? sqlErrorNumber = null,
        Exception? innerException = null)
        : base(FixedMessage, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        Operation = operation;
        Outcome = outcome;
        SqlErrorNumber = sqlErrorNumber;
    }

    /// <summary>
    /// The fixed identifier of the store operation that failed, by convention the <c>IDataStore</c> method name. It
    /// never holds data, so it is safe to log.
    /// </summary>
    public string Operation { get; }

    /// <summary>What is known about the stored data when the failure happened.</summary>
    public WriteOutcome Outcome { get; }

    /// <summary>
    /// The SQL Server error number of the underlying SQL exception, or <see langword="null"/> when the cause was not a
    /// SQL exception: a missing connection string, pool exhaustion, an open the outage gate released or refused, an
    /// open whose deadline passed, or a form write lock that <c>sp_getapplock</c> refused with its return code -1
    /// (timed out) or -3 (deadlock victim), which is never stored here.
    /// </summary>
    public int? SqlErrorNumber { get; }
}
