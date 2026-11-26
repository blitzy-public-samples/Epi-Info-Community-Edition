namespace EpiInfo.Domain.Storage;

/// <summary>
/// The exception through which a store reports an unexpected failure that is not an availability failure, so the
/// API never receives a SQL client exception. <c>ApiExceptionHandler</c> maps it to a 500 ProblemDetails whose
/// <c>detail</c> is a fixed generic sentence plus the <c>traceId</c>, in every environment. On <c>POST</c>,
/// <c>PUT</c> and <c>DELETE</c> of records the handler adds <c>writeOutcome</c>: the <c>WriteOutcomeFeature</c> value
/// first, then <see cref="Outcome"/>, which applies only once a store write call was issued and while it is
/// outstanding.
/// </summary>
/// <remarks>
/// <para>
/// Its availability counterpart is <c>DataStoreUnavailableException</c>, which maps to 503. The two classes share one
/// shape, so the handler and the tests treat them alike. A store-specific failure reaches the API only as one of the
/// two, or as an <see cref="OperationCanceledException"/> when the client aborts the request.
/// </para>
/// <para>When a store raises it:</para>
/// <list type="bullet">
/// <item><description>
/// For every SQL failure that <c>SqlErrorClassifier</c> does not classify as unavailable, on reads and writes alike.
/// A read always uses <see cref="WriteOutcome.NotWritten"/>.
/// </description></item>
/// <item><description>
/// Inside a write, for any other exception raised after the transaction began, with the outcome the store can prove.
/// </description></item>
/// </list>
/// <para>
/// These are the "Unexpected" (500) class of the error classification. Examples: a constraint or conversion error; a
/// permission error (229, 230) while permission awareness is not delivered; the <see cref="ArgumentException"/> of a
/// malformed connection string; an <see cref="InvalidOperationException"/> that is not SqlClient pool exhaustion.
/// </para>
/// <para>The outcome follows what the store can prove about the data:</para>
/// <list type="bullet">
/// <item><description>
/// <see cref="WriteOutcome.NotWritten"/>: the failure came before any DML was sent, or <c>Rollback</c> completed
/// without error.
/// </description></item>
/// <item><description>
/// <see cref="WriteOutcome.Unknown"/>: DML was sent and neither a commit nor a completed rollback can be confirmed,
/// including a rollback that itself failed. The store logs the rollback error, and it never replaces the original
/// failure's classification.
/// </description></item>
/// <item><description><see cref="WriteOutcome.Written"/>: the commit succeeded and a later step failed.</description></item>
/// </list>
/// <para>
/// Redaction: <see cref="Exception.Message"/> is always <see cref="FixedMessage"/>, because no constructor takes a
/// message. The inner exception may be attached for diagnostics, but the handler logs only the exception type,
/// <see cref="SqlErrorNumber"/>, <see cref="Operation"/> and the <c>traceId</c>, never <see cref="Exception.Message"/>
/// or an inner exception's message, which may hold SQL text or values.
/// </para>
/// <para>
/// Not marked serializable and given no serialization constructor: that pattern is obsolete since .NET 8 (SYSLIB0051).
/// </para>
/// </remarks>
public sealed class DataStoreWriteException : Exception
{
    /// <summary>The fixed message of every instance; it never carries data.</summary>
    public const string FixedMessage = "The data store operation failed.";

    /// <summary>Initialises the exception for one failed store operation.</summary>
    /// <param name="operation">
    /// A fixed operation identifier chosen by the store, never data. By convention it is the <c>IDataStore</c> method
    /// name, for example <c>nameof(IDataStore.InsertAsync)</c>.
    /// </param>
    /// <param name="outcome">What is known about the stored data; a defined <see cref="WriteOutcome"/> value.</param>
    /// <param name="sqlErrorNumber">
    /// The SQL Server error number when the cause was a SQL exception; otherwise <see langword="null"/>.
    /// </param>
    /// <param name="innerException">The underlying failure, kept for diagnostics only.</param>
    /// <exception cref="ArgumentNullException"><paramref name="operation"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="operation"/> is empty or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="outcome"/> is not a defined value. The message names the parameter only.
    /// </exception>
    public DataStoreWriteException(
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
    /// The fixed identifier of the failed store operation, by convention the <c>IDataStore</c> method name; never data.
    /// </summary>
    public string Operation { get; }

    /// <summary>What is known about the stored data after the failure.</summary>
    public WriteOutcome Outcome { get; }

    /// <summary>
    /// The SQL Server error number when the cause was a SQL exception; otherwise <see langword="null"/>.
    /// </summary>
    public int? SqlErrorNumber { get; }
}
