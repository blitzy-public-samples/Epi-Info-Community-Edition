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
/// a projection failure rolls the save back. A <c>Success</c> from <c>InsertAsync</c> or <c>UpdateAsync</c>, or
/// <c>Deleted</c> from <c>SoftDeleteAsync</c>, is the store reporting the commit. Call <see cref="MarkWritten"/> as
/// soon as that call returns, before any post-commit step: the post-commit log line, serialisation and delivery of
/// the DTO already built.
/// </description></item>
/// <item><description>
/// Call <see cref="MarkNotWritten"/> as soon as <c>InsertAsync</c> returns <c>Forbidden</c>, <c>UpdateAsync</c>
/// returns any defined status other than <c>Success</c>, or <c>SoftDeleteAsync</c> returns <c>NotFound</c>,
/// <c>AlreadyDeleted</c> or <c>Forbidden</c>: the store reports that it wrote nothing, so a failure while that
/// response is written is <c>notWritten</c>, not <c>unknown</c>, whatever outcome a later store exception reports.
/// </description></item>
/// <item><description>
/// A <c>DELETE</c> of an already-deleted record wrote nothing, so it is marked not written, never written.
/// </description></item>
/// </list>
/// <para>
/// The feature is in one of four states: unset (no store write call issued), started (<see cref="WriteStarted"/>
/// alone), and the two definitive results the store reported, <see cref="Written"/> and <see cref="NotWritten"/>,
/// which are never both true. Semantics the exception handler relies on; the handler implements this precedence,
/// this type only records the facts:
/// </para>
/// <list type="number">
/// <item><description><see cref="Written"/> is true: <c>written</c>.</description></item>
/// <item><description>Otherwise <see cref="NotWritten"/> is true: <c>notWritten</c>.</description></item>
/// <item><description>
/// Otherwise a missing feature, or <see cref="WriteStarted"/> false: <c>notWritten</c>, whatever outcome a store
/// exception reports, because no store write call was issued.
/// </description></item>
/// <item><description>
/// Otherwise, for a started write still outstanding, the store exception's <c>Outcome</c>, when the failure is a store
/// exception.
/// </description></item>
/// <item><description>Otherwise: <c>unknown</c>.</description></item>
/// </list>
/// </remarks>
public sealed class WriteOutcomeFeature
{
    /// <summary>
    /// True from <see cref="MarkWriteStarted"/> until the store reports a result that wrote nothing; a reported commit
    /// keeps it true, because <see cref="Written"/> implies it. While this is false nothing can have been written: no
    /// store write call was issued, for example because the failure came from the metadata read, or the store reported
    /// that its call wrote nothing.
    /// </summary>
    public bool WriteStarted { get; private set; }

    /// <summary>
    /// True once the store has reported a commit. It never reverts to false, so a later failure in the same request
    /// is reported as <c>written</c>.
    /// </summary>
    public bool Written { get; private set; }

    /// <summary>
    /// True once the store call returned a definitive result that wrote nothing, until the next
    /// <see cref="MarkWriteStarted"/>. Never true together with <see cref="Written"/>. While it is true the request is
    /// <c>notWritten</c>, whatever outcome a later store exception reports, because the store has already said what
    /// its write did.
    /// </summary>
    public bool NotWritten { get; private set; }

    /// <summary>
    /// Records that a store write call is about to be issued. It sets <see cref="WriteStarted"/> and clears
    /// <see cref="NotWritten"/>, because a new write call makes an earlier no-write result no longer the latest fact;
    /// it never clears <see cref="Written"/>. Calling it again has no further effect.
    /// </summary>
    public void MarkWriteStarted()
    {
        WriteStarted = true;
        NotWritten = false;
    }

    /// <summary>
    /// Records that the store reported a commit; this also sets <see cref="WriteStarted"/> and clears
    /// <see cref="NotWritten"/>. Calling it again has no further effect.
    /// </summary>
    public void MarkWritten()
    {
        WriteStarted = true;
        Written = true;
        NotWritten = false;
    }

    /// <summary>
    /// Records that the store call returned a definitive result that wrote nothing: it rolled back or sent no data
    /// statement. Unless <see cref="Written"/> is true, this sets <see cref="NotWritten"/> and clears
    /// <see cref="WriteStarted"/>; once <see cref="Written"/> is true it changes nothing, because a commit never
    /// reverts and <see cref="Written"/> always implies <see cref="WriteStarted"/>. Calling it again has no further
    /// effect.
    /// </summary>
    public void MarkNotWritten()
    {
        if (!Written)
        {
            WriteStarted = false;
            NotWritten = true;
        }
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
