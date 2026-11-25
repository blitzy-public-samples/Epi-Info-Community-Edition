namespace EpiInfo.Api.Errors;

/// <summary>
/// Per-request record of how far a record write got. The exception handler reads it to report the
/// <c>writeOutcome</c> of a failed <c>POST</c>, <c>PUT</c> or <c>DELETE</c> on records.
/// </summary>
/// <remarks>
/// <para>
/// One instance belongs to one request and lives in <see cref="HttpContext.Features"/>. It is never shared across
/// requests and never registered for dependency injection.
/// </para>
/// <para>Calling contract for <c>RecordService</c>:</para>
/// <list type="bullet">
/// <item><description>
/// Call <see cref="GetOrCreate"/> before any store write. The endpoint may pass the feature in, or the service may
/// obtain it from the <see cref="HttpContext"/> it receives.
/// </description></item>
/// <item><description>
/// Call <see cref="MarkWriteStarted"/> immediately before <c>IDataStore.InsertAsync</c>, <c>UpdateAsync</c> or
/// <c>SoftDeleteAsync</c>, after the metadata read and the phase-1 validation.
/// </description></item>
/// <item><description>
/// The store reads the response snapshot and builds the response DTO inside the transaction, before it commits, so
/// a projection failure rolls the save back. The store call returning successfully is the store reporting the
/// commit. Call <see cref="MarkWritten"/> as soon as that call returns, before any post-commit step: the post-commit
/// log line, serialisation and delivery of the DTO already built.
/// </description></item>
/// <item><description>
/// A <c>DELETE</c> of an already-deleted record writes nothing and need not be marked written. Marking it would be
/// harmless, because that request ends in 204 with no error body.
/// </description></item>
/// </list>
/// <para>
/// Semantics the exception handler relies on; the handler implements this precedence, this type only records the
/// facts:
/// </para>
/// <list type="number">
/// <item><description><see cref="Written"/> is true: <c>written</c>.</description></item>
/// <item><description>Otherwise the store exception's <c>Outcome</c>, when the failure is a store exception.</description></item>
/// <item><description>Otherwise a missing feature, or <see cref="WriteStarted"/> false: <c>notWritten</c>.</description></item>
/// <item><description>Otherwise: <c>unknown</c>.</description></item>
/// </list>
/// </remarks>
public sealed class WriteOutcomeFeature
{
    /// <summary>
    /// True once a store write call is about to be issued. A failure while this is false happened before any data
    /// could have been written, for example during the metadata read.
    /// </summary>
    public bool WriteStarted { get; private set; }

    /// <summary>
    /// True once the store has reported a commit. It never reverts to false, so a later failure in the same request
    /// is reported as <c>written</c>.
    /// </summary>
    public bool Written { get; private set; }

    /// <summary>Records that a store write call is about to be issued. Calling it again has no further effect.</summary>
    public void MarkWriteStarted() => WriteStarted = true;

    /// <summary>
    /// Records that the store reported a commit; this also sets <see cref="WriteStarted"/>. Calling it again has no
    /// further effect.
    /// </summary>
    public void MarkWritten()
    {
        WriteStarted = true;
        Written = true;
    }

    /// <summary>Returns the request's feature, or <see langword="null"/> when no write path has created one.</summary>
    /// <param name="context">The current request.</param>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public static WriteOutcomeFeature? Get(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Features.Get<WriteOutcomeFeature>();
    }

    /// <summary>
    /// Returns the request's feature, first storing a new one in <see cref="HttpContext.Features"/> when none exists.
    /// </summary>
    /// <param name="context">The current request.</param>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public static WriteOutcomeFeature GetOrCreate(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var feature = context.Features.Get<WriteOutcomeFeature>();
        if (feature is null)
        {
            feature = new WriteOutcomeFeature();
            context.Features.Set(feature);
        }

        return feature;
    }
}

/// <summary>
/// Endpoint metadata marking a record write: attached with <c>WithMetadata</c> to exactly
/// <c>POST /api/forms/{viewId}/records</c>, <c>PUT /api/forms/{viewId}/records/{globalRecordId}</c> and
/// <c>DELETE /api/forms/{viewId}/records/{globalRecordId}</c>, and to no other endpoint (not search, not
/// field-event).
/// </summary>
/// <remarks>
/// The exception handler adds the <c>writeOutcome</c> member to 500 and 503 bodies only when the matched endpoint
/// carries this metadata, because <c>WriteProblemDetails</c> is the documented schema of those responses alone.
/// </remarks>
public sealed class WriteEndpointMetadata
{
    /// <summary>Marks <c>POST /api/forms/{viewId}/records</c>.</summary>
    public static WriteEndpointMetadata Insert { get; } = new("record.insert");

    /// <summary>Marks <c>PUT /api/forms/{viewId}/records/{globalRecordId}</c>.</summary>
    public static WriteEndpointMetadata Update { get; } = new("record.update");

    /// <summary>Marks <c>DELETE /api/forms/{viewId}/records/{globalRecordId}</c>.</summary>
    public static WriteEndpointMetadata SoftDelete { get; } = new("record.softDelete");

    /// <summary>Creates a marker for the named write operation.</summary>
    /// <param name="operation">A fixed operation name; it is used only as a log argument.</param>
    /// <exception cref="ArgumentNullException"><paramref name="operation"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="operation"/> is empty or whitespace.</exception>
    public WriteEndpointMetadata(string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        Operation = operation;
    }

    /// <summary>The fixed operation name, used only as a log argument; never a request path or value.</summary>
    public string Operation { get; }
}
