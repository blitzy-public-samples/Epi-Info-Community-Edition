namespace EpiInfo.Domain.Storage;

/// <summary>
/// What is known about the stored data after a record write (insert, update or soft delete) fails. A failed
/// <c>POST</c>, <c>PUT</c> or <c>DELETE</c> on records reports it as the <c>writeOutcome</c> member of its 500 or 503
/// ProblemDetails body. The client resends a <c>POST</c> or <c>PUT</c> automatically only for
/// <see cref="NotWritten"/>; every other value, and a missing or unrecognised one, is unsafe to replay.
/// </summary>
/// <remarks>
/// <para>Who sets it and who reads it:</para>
/// <list type="bullet">
/// <item><description>
/// The <c>EpiInfo.Data</c> store and its <c>SqlErrorClassifier</c> set it as the <c>Outcome</c> of the
/// <c>DataStoreUnavailableException</c> or <c>DataStoreWriteException</c> raised for a failed write.
/// </description></item>
/// <item><description>
/// <c>RecordService</c> records <see cref="Written"/> through the API's <c>WriteOutcomeFeature</c> once the store
/// reports a commit.
/// </description></item>
/// <item><description>
/// <c>ApiExceptionHandler</c> reads it. For a write endpoint the handler takes the feature first, then the
/// exception's <c>Outcome</c>, and writes the string from <see cref="WriteOutcomeExtensions.ToWireValue"/>.
/// </description></item>
/// </list>
/// <para>
/// <see cref="Unknown"/> is deliberately the default value (0), so <c>default(WriteOutcome)</c> or a field nobody set
/// never reads as safe to resend.
/// </para>
/// </remarks>
public enum WriteOutcome
{
    /// <summary>
    /// DML was sent, and neither a commit nor a completed rollback can be confirmed: for example the commit call
    /// failed or did not return, or the rollback itself failed. Unsafe to resend automatically. This is the default
    /// value, so an outcome nobody set is never mistaken for <see cref="NotWritten"/>.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// Nothing was written. Either no DML was sent, including a failure before the store was reached such as the
    /// metadata read; or the rollback completed without error; or the server reported the transaction aborted
    /// (deadlock victim, error 1205). The only outcome that is safe to resend automatically.
    /// </summary>
    NotWritten = 1,

    /// <summary>
    /// The commit succeeded and a later step failed, such as the post-commit log line or response serialisation.
    /// Unsafe to resend, because the data is already stored.
    /// </summary>
    Written = 2,
}

/// <summary>
/// The wire spellings of <see cref="WriteOutcome"/>: the single source of the strings the API places in the
/// <c>writeOutcome</c> ProblemDetails member and lists in the <c>WriteProblemDetails</c> schema.
/// </summary>
public static class WriteOutcomeExtensions
{
    /// <summary>The wire spelling of <see cref="WriteOutcome.Unknown"/>.</summary>
    public const string WireUnknown = "unknown";

    /// <summary>The wire spelling of <see cref="WriteOutcome.NotWritten"/>.</summary>
    public const string WireNotWritten = "notWritten";

    /// <summary>The wire spelling of <see cref="WriteOutcome.Written"/>.</summary>
    public const string WireWritten = "written";

    /// <summary>Returns the <c>writeOutcome</c> wire spelling of <paramref name="outcome"/>.</summary>
    /// <param name="outcome">A defined <see cref="WriteOutcome"/> value.</param>
    /// <returns><see cref="WireUnknown"/>, <see cref="WireNotWritten"/> or <see cref="WireWritten"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="outcome"/> is not a defined value. The message names the parameter only.
    /// </exception>
    public static string ToWireValue(this WriteOutcome outcome) => outcome switch
    {
        WriteOutcome.Unknown => WireUnknown,
        WriteOutcome.NotWritten => WireNotWritten,
        WriteOutcome.Written => WireWritten,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };
}
