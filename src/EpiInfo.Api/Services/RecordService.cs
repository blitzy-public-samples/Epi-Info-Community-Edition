using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using EpiInfo.Api.Contracts;
using EpiInfo.Api.Errors;
using EpiInfo.Api.Json;
using EpiInfo.Domain.Metadata;
using EpiInfo.Domain.Records;
using EpiInfo.Domain.Search;
using EpiInfo.Domain.Storage;
using EpiInfo.Domain.Validation;
using EpiInfo.Domain.Values;

namespace EpiInfo.Api.Services;

/// <summary>
/// The body of <c>GET /api/forms/{viewId}/records/count</c>: <c>{"count": n}</c>, the number of records in the
/// selected set.
/// </summary>
/// <param name="Count">The selected-set count; 0 for a form with no records in that set.</param>
public sealed record RecordCountResponse(int Count);

/// <summary>
/// Serves record navigation, positions, the records list, Find and the three record writes. It takes over the
/// workflow the legacy Enter module kept in <c>GuiMediator</c> and <c>FindRecordDialog</c>, as a stateless
/// application service: navigation state lives in the client, and each call answers one positional, identity, list,
/// search or write request.
/// </summary>
/// <remarks>
/// <para>
/// <b>Rule owners.</b> The service applies no rule of its own. The record-set predicate and positions belong to
/// <see cref="RecordQueryRules"/>, validation to <see cref="RecordValidator"/>, the legacy Find semantics to
/// <see cref="SearchCriteriaParser"/>, form loading to <see cref="FormService.LoadFormAsync"/> and the permission rule
/// to <see cref="FormPermissions"/>. Every error body comes from <see cref="ApiProblems"/>. Data is reached only
/// through <see cref="IDataStore"/>, and no store call is retried.
/// </para>
/// <para>
/// <b>Reads never write.</b> Every read issues only <c>SELECT</c>s through the store and runs no check code: the
/// service has no check-code engine, and the legacy re-save after opening a record is not carried, so loading a record
/// changes nothing (scenario <c>DATA-01</c>). A Record DTO always carries its intrinsic <c>position</c> and
/// <c>count</c>, over the active set for an active record and over every row for a deleted one, never the caller's
/// selected-set coordinates; the navigator takes those from <see cref="GetNavigationPositionAsync"/>. The store
/// returns them with the row from one consistent read, and the service passes that DTO through unchanged.
/// </para>
/// <para>
/// <b>Writes.</b> The input checks run outside any transaction, then the store runs the write in one transaction.
/// <see cref="WriteOutcomeFeature.MarkWriteStarted"/> is recorded immediately before the store write,
/// <see cref="WriteOutcomeFeature.MarkWritten"/> as soon as the store reports a commit, and
/// <see cref="WriteOutcomeFeature.MarkNotWritten"/> as soon as it returns a result that wrote nothing, so the exception
/// handler can state the <c>writeOutcome</c> of a failure. After the commit the service makes no store call: it
/// writes one log line, whose own failure cannot change the response, and returns the DTO the store built inside the
/// transaction. Store failures and client aborts are never caught here; they propagate to the exception handler.
/// </para>
/// <para>
/// <b>Permissions.</b> Each write reads the login's permissions afresh. A store that reports none
/// (<see langword="null"/>) means no check; otherwise a denied write is a 403 before any transaction, and the service
/// hands what it read to the store, which reads the permissions again inside the write's transaction, before its first
/// data statement, and refuses a write they no longer allow, which the service answers with the same 403 and nothing
/// written. The service's own read therefore only turns that check on and never decides a write the store runs.
/// </para>
/// <para>
/// <b>Redaction.</b> Log lines use one fixed template whose arguments are the form, the view id, the record id, the
/// operation and the outcome. Field values, search criteria, the list filter and request bodies are never logged.
/// </para>
/// <para>The type keeps no mutable state; the API registers it as a scoped service.</para>
/// </remarks>
/// <param name="store">The persistence port every read and write goes through.</param>
/// <param name="forms">Loads a form's metadata with its definition for validating writes.</param>
/// <param name="stamps">Supplies the save stamp of each write.</param>
/// <param name="logger">Receives one line per committed or unchanged write.</param>
/// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
public sealed partial class RecordService(
    IDataStore store,
    FormService forms,
    SaveStampProvider stamps,
    ILogger<RecordService> logger)
{
    private const string InsertOperation = "insert";
    private const string UpdateOperation = "update";
    private const string SoftDeleteOperation = "softDelete";

    private const string CommittedOutcome = "committed";
    private const string UnchangedOutcome = "unchanged";

    private readonly IDataStore store = store ?? throw new ArgumentNullException(nameof(store));

    private readonly FormService forms = forms ?? throw new ArgumentNullException(nameof(forms));

    private readonly SaveStampProvider stamps = stamps ?? throw new ArgumentNullException(nameof(stamps));

    private readonly ILogger<RecordService> logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>Reads one page of the records list for <c>GET /api/forms/{viewId}/records</c>.</summary>
    /// <param name="viewId">The form's <c>metaViews.ViewId</c> from the route.</param>
    /// <param name="query">
    /// The coerced filter, deleted toggle and paging, whose paging bounds and filter length the endpoint has checked.
    /// </param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>
    /// 200 with the <see cref="RecordPage"/>, whose items are empty for a valid page past the end while its total stays
    /// correct; or 404 for an unknown form.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="query"/> is <see langword="null"/>.</exception>
    public async Task<IResult> ListAsync(int viewId, RecordListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var form = await store.GetFormMetadataAsync(viewId, cancellationToken).ConfigureAwait(false);
        if (form is null)
        {
            return ApiProblems.FormNotFound();
        }

        var page = await store.ListAsync(form, query, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(page);
    }

    /// <summary>Counts the selected set for <c>GET /api/forms/{viewId}/records/count</c>.</summary>
    /// <param name="viewId">The form's <c>metaViews.ViewId</c> from the route.</param>
    /// <param name="includeDeleted">
    /// <see langword="false"/> for the active set (<c>RecStatus = 1</c>); <see langword="true"/> for every row.
    /// </param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>200 with the <see cref="RecordCountResponse"/>, or 404 for an unknown form.</returns>
    public async Task<IResult> CountAsync(int viewId, bool includeDeleted, CancellationToken cancellationToken)
    {
        var form = await store.GetFormMetadataAsync(viewId, cancellationToken).ConfigureAwait(false);
        if (form is null)
        {
            return ApiProblems.FormNotFound();
        }

        var count = await store.CountAsync(form, includeDeleted, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(new RecordCountResponse(count));
    }

    /// <summary>
    /// Opens the record at a 1-based position of the selected set, in <c>UniqueKey</c> order, for
    /// <c>GET /api/forms/{viewId}/records/at/{position}</c>.
    /// </summary>
    /// <param name="viewId">The form's <c>metaViews.ViewId</c> from the route.</param>
    /// <param name="position">The coerced 1-based position; any integer.</param>
    /// <param name="includeDeleted">Whether the selected set holds every row rather than the active set.</param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>
    /// 200 with the <see cref="RecordDto"/> and its intrinsic coordinates; 404 for an unknown form; or 404 for a
    /// position outside 1 to the selected-set count, which covers <c>at/0</c> and every position on an empty set.
    /// </returns>
    public async Task<IResult> GetAtAsync(
        int viewId,
        int position,
        bool includeDeleted,
        CancellationToken cancellationToken)
    {
        var form = await store.GetFormMetadataAsync(viewId, cancellationToken).ConfigureAwait(false);
        if (form is null)
        {
            return ApiProblems.FormNotFound();
        }

        // The rules check only the lower bound; the store answers null above the selected-set count, which is the
        // upper-bound 404 without a separate count read.
        if (!RecordQueryRules.IsValidPosition(position))
        {
            return ApiProblems.PositionNotFound();
        }

        var record = await store.GetAtPositionAsync(form, position, includeDeleted, cancellationToken)
            .ConfigureAwait(false);
        if (record is null)
        {
            return ApiProblems.PositionNotFound();
        }

        return TypedResults.Ok(record);
    }

    /// <summary>
    /// Opens one record by its <c>GlobalRecordId</c> for <c>GET /api/forms/{viewId}/records/{globalRecordId}</c>.
    /// Deleted records are returned, marked by their <c>recStatus</c> (scenario <c>DATA-02</c>).
    /// </summary>
    /// <param name="viewId">The form's <c>metaViews.ViewId</c> from the route.</param>
    /// <param name="globalRecordId">The record's <c>GlobalRecordId</c> from the route.</param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>
    /// 200 with the <see cref="RecordDto"/> and its intrinsic coordinates; or 404 for an unknown form or an id that is
    /// not in the base table.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="globalRecordId"/> is <see langword="null"/>.</exception>
    public async Task<IResult> GetByIdAsync(int viewId, string globalRecordId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(globalRecordId);

        var form = await store.GetFormMetadataAsync(viewId, cancellationToken).ConfigureAwait(false);
        if (form is null)
        {
            return ApiProblems.FormNotFound();
        }

        var record = await store.GetByIdAsync(form, globalRecordId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return ApiProblems.RecordNotFound();
        }

        return TypedResults.Ok(record);
    }

    /// <summary>
    /// Places a record in the caller's selected set, for the navigator's "[n] of N", for
    /// <c>GET /api/forms/{viewId}/records/{globalRecordId}/position</c>.
    /// </summary>
    /// <remarks>
    /// The store reads only the base table, so a record missing a page row is still placed. The coordinates are never
    /// derived from search or from the Record DTO, whose own coordinates are intrinsic.
    /// </remarks>
    /// <param name="viewId">The form's <c>metaViews.ViewId</c> from the route.</param>
    /// <param name="globalRecordId">The record's <c>GlobalRecordId</c> from the route.</param>
    /// <param name="includeDeleted">Whether the selected set holds every row rather than the active set.</param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>
    /// 200 with the <see cref="NavigationPosition"/>, whose <c>position</c> is <see langword="null"/> when the record
    /// is outside the selected set; or 404 for an unknown form or an id that is not in the base table.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="globalRecordId"/> is <see langword="null"/>.</exception>
    public async Task<IResult> GetNavigationPositionAsync(
        int viewId,
        string globalRecordId,
        bool includeDeleted,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(globalRecordId);

        var form = await store.GetFormMetadataAsync(viewId, cancellationToken).ConfigureAwait(false);
        if (form is null)
        {
            return ApiProblems.FormNotFound();
        }

        var placement = await store.GetSetPositionAsync(form, globalRecordId, includeDeleted, cancellationToken)
            .ConfigureAwait(false);
        if (placement is null)
        {
            return ApiProblems.RecordNotFound();
        }

        return TypedResults.Ok(placement);
    }

    /// <summary>
    /// Finds records with the legacy Find semantics for <c>POST /api/forms/{viewId}/records/search</c>.
    /// </summary>
    /// <remarks>
    /// <see cref="SearchCriteriaParser"/> owns the criteria rules: trimming, the <c>" or "</c> alternatives, comparison
    /// prefixes, wildcards, the six-field limit and the rejection of a field named twice. A wildcard pattern of any
    /// length reaches the store, which evaluates one longer than SQL Server's <c>LIKE</c> pattern limit window by
    /// window; only a pattern holding a <c>[...]</c> set longer than <see cref="RecordQueryRules.MaxLikePatternLength"/>
    /// code units that is unclosed or that no cut keeping its members divides, which no SQL Server construct can
    /// evaluate, is a 400, after the form's metadata is read and before any Find statement is sent. Any number of
    /// alternatives reaches the store, which binds a large set in packed form. The store applies the selected set,
    /// removes the open record before counting and capping, orders by <c>UniqueKey</c> and caps the items at
    /// <see cref="RecordQueryRules.SearchCap"/>.
    /// </remarks>
    /// <param name="viewId">The form's <c>metaViews.ViewId</c> from the route.</param>
    /// <param name="request">
    /// The bound body, or <see langword="null"/> when the body is the JSON literal <c>null</c>.
    /// </param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>
    /// 200 with the <see cref="SearchResponse"/>, whose items are empty when nothing matches; 404 for an unknown form;
    /// a request-level 400 for a missing body or empty criteria; or a field-level 400 for criteria that do not parse.
    /// </returns>
    public async Task<IResult> SearchAsync(int viewId, SearchRequest? request, CancellationToken cancellationToken)
    {
        var form = await store.GetFormMetadataAsync(viewId, cancellationToken).ConfigureAwait(false);
        if (form is null)
        {
            return ApiProblems.FormNotFound();
        }

        if (request is null)
        {
            return ApiProblems.RequestBodyRequired();
        }

        var parsed = SearchCriteriaParser.Parse(FormDefinitionFactory.GetDataFields(form), request.Criteria);
        if (!parsed.IsSuccess)
        {
            return ApiProblems.FromSearch(parsed);
        }

        var exclude = string.IsNullOrWhiteSpace(request.ExcludeGlobalRecordId) ? null : request.ExcludeGlobalRecordId;
        var includeDeleted = request.IncludeDeleted ?? false;

        var response = await store.SearchAsync(form, parsed.Conditions, includeDeleted, exclude, cancellationToken)
            .ConfigureAwait(false);
        return TypedResults.Ok(response);
    }

    /// <summary>Creates a record for <c>POST /api/forms/{viewId}/records</c>.</summary>
    /// <remarks>
    /// <para>Before any transaction, in this order:</para>
    /// <list type="number">
    /// <item><description>an unknown form is a 404;</description></item>
    /// <item><description>
    /// a JSON <c>null</c> body or a <c>values</c> that is not an object is a request-level 400;
    /// </description></item>
    /// <item><description>
    /// the input checks of <see cref="RecordValidator.ValidateInput"/> give a field-level 400;
    /// </description></item>
    /// <item><description>
    /// <see cref="RecordValidator.CheckNewRecord"/> gives the request-level 400 for a record whose values are all
    /// <c>null</c>, absent fields counting as <c>null</c>, and a field-level 400 listing every missing required field.
    /// An explicit <c>false</c> is a value, so a body of unchecked checkboxes creates a record;
    /// </description></item>
    /// <item><description>a denied insert permission is a 403.</description></item>
    /// </list>
    /// <para>
    /// The store then inserts the base row and every page row and builds the response inside one transaction. Once
    /// that transaction holds the form's insert lock, and before its base row is inserted, the store reads the
    /// permissions again inside it and refuses, with nothing written, an insert the login may no longer apply: INSERT
    /// denied on the base table or a page table. That refusal is the same 403. The service's earlier read only turns
    /// that check on.
    /// </para>
    /// </remarks>
    /// <param name="httpContext">The current request, which carries the <see cref="WriteOutcomeFeature"/>.</param>
    /// <param name="viewId">The form's <c>metaViews.ViewId</c> from the route.</param>
    /// <param name="request">
    /// The bound body, or <see langword="null"/> when the body is the JSON literal <c>null</c>.
    /// </param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>
    /// 201 with the new <see cref="RecordDto"/> and <c>Location: /api/forms/{viewId}/records/{globalRecordId}</c>, or
    /// the 400, 403 or 404 described in the remarks, the 403 before the store or by its in-transaction re-check.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="httpContext"/> is <see langword="null"/>.</exception>
    public async Task<IResult> CreateAsync(
        HttpContext httpContext,
        int viewId,
        RecordWriteRequest? request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var loaded = await forms.LoadFormAsync(viewId, cancellationToken).ConfigureAwait(false);
        if (loaded is null)
        {
            return ApiProblems.FormNotFound();
        }

        if (!TryValidateInput(loaded, request, out var validator, out var values, out var problem))
        {
            return problem;
        }

        var check = validator.CheckNewRecord(values);
        if (!check.IsValid)
        {
            return ApiProblems.FromValidation(check);
        }

        var form = loaded.Metadata;
        var permissions = await store.GetPermissionsAsync(form, cancellationToken).ConfigureAwait(false);
        if (!IsAllowed(permissions, form, ForbiddenAction.Insert))
        {
            return ApiProblems.Forbidden(ForbiddenAction.Insert);
        }

        var stamp = stamps.Create();
        var outcome = WriteOutcomeFeature.GetOrCreate(httpContext);
        outcome.MarkWriteStarted();

        var result = await store.InsertAsync(form, check.Values, stamp, permissions, cancellationToken)
            .ConfigureAwait(false);

        switch (result.Status)
        {
            case RecordWriteStatus.Success when result.IsSuccess:
                // The store reported the commit; nothing after this line reads the database.
                outcome.MarkWritten();
                LogWrite(form, InsertOperation, result.Record.GlobalRecordId, CommittedOutcome);

                var location = string.Create(
                    CultureInfo.InvariantCulture,
                    $"/api/forms/{viewId}/records/{Uri.EscapeDataString(result.Record.GlobalRecordId)}");
                return TypedResults.Created(location, result.Record);

            // The store's own permission read refused the insert and wrote nothing, so a failure while this response
            // is written must report notWritten, not unknown.
            case RecordWriteStatus.Forbidden:
                outcome.MarkNotWritten();
                return ApiProblems.Forbidden(ForbiddenAction.Insert);

            default:
                throw new InvalidOperationException("The store returned an insert result outside its contract.");
        }
    }

    /// <summary>Updates a record as a delta for <c>PUT /api/forms/{viewId}/records/{globalRecordId}</c>.</summary>
    /// <remarks>
    /// <para>
    /// The form lookup, body checks and input checks run as for <see cref="CreateAsync"/>, then a denied update
    /// permission is a 403. Only the submitted fields are written; <c>{"values": {}}</c> is valid and updates only the
    /// last-save stamp.
    /// </para>
    /// <para>
    /// The required gate runs inside the store's transaction, after the locked read of the base row and every page
    /// row, on the stored values overlaid with the submitted ones, so two concurrent saves can never both pass it on
    /// stale state. A failed gate rolls back with nothing written. After it, the store reads the permissions again
    /// inside that transaction and refuses, with nothing written, an update the login may no longer apply: UPDATE
    /// denied on the base table or a page table, or INSERT denied on the table of a page whose row is missing. The
    /// service's earlier read only turns that check on.
    /// </para>
    /// <para>
    /// The store calls <see cref="SaveStampProvider.Create"/> only once those checks decide that the update writes, so
    /// the 404, 409, 400 and 403 answers never need a stamp. A stamp that cannot be made, because the process has no OS
    /// user name, fails only an update that would write, as the store's failure with nothing written: a 500 whose
    /// <c>writeOutcome</c> is <c>notWritten</c>.
    /// </para>
    /// </remarks>
    /// <param name="httpContext">The current request, which carries the <see cref="WriteOutcomeFeature"/>.</param>
    /// <param name="viewId">The form's <c>metaViews.ViewId</c> from the route.</param>
    /// <param name="globalRecordId">The record's <c>GlobalRecordId</c> from the route.</param>
    /// <param name="request">
    /// The bound body, or <see langword="null"/> when the body is the JSON literal <c>null</c>.
    /// </param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>
    /// 200 with the updated <see cref="RecordDto"/>; 400 for invalid input or a failed required gate; 403 for a denied
    /// permission, before the store or by its in-transaction re-check, including a missing page row the login may not
    /// insert; 404 for an unknown form or record; or 409 for a deleted record.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="httpContext"/> or <paramref name="globalRecordId"/> is <see langword="null"/>.
    /// </exception>
    public async Task<IResult> UpdateAsync(
        HttpContext httpContext,
        int viewId,
        string globalRecordId,
        RecordWriteRequest? request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(globalRecordId);

        var loaded = await forms.LoadFormAsync(viewId, cancellationToken).ConfigureAwait(false);
        if (loaded is null)
        {
            return ApiProblems.FormNotFound();
        }

        if (!TryValidateInput(loaded, request, out var validator, out var submitted, out var problem))
        {
            return problem;
        }

        var form = loaded.Metadata;
        var permissions = await store.GetPermissionsAsync(form, cancellationToken).ConfigureAwait(false);
        if (!IsAllowed(permissions, form, ForbiddenAction.Update))
        {
            return ApiProblems.Forbidden(ForbiddenAction.Update);
        }

        // The store creates the stamp only once its locked checks decide the update writes, so an answer that writes
        // nothing needs no stamp, and a stamp it cannot create is the store's failure with nothing written.
        var outcome = WriteOutcomeFeature.GetOrCreate(httpContext);
        outcome.MarkWriteStarted();

        var result = await store.UpdateAsync(
                form,
                globalRecordId,
                submitted,
                stamps.Create,
                stored => validator.CheckRequired(stored, submitted),
                permissions,
                cancellationToken)
            .ConfigureAwait(false);

        switch (result.Status)
        {
            case RecordWriteStatus.Success when result.IsSuccess:
                // The store reported the commit; nothing after this line reads the database.
                outcome.MarkWritten();
                LogWrite(form, UpdateOperation, result.Record.GlobalRecordId, CommittedOutcome);
                return TypedResults.Ok(result.Record);

            // Each result below means the store wrote nothing, so a failure while its response is written must report
            // notWritten, not unknown.
            case RecordWriteStatus.NotFound:
                outcome.MarkNotWritten();
                return ApiProblems.RecordNotFound();

            case RecordWriteStatus.Deleted:
                outcome.MarkNotWritten();
                return ApiProblems.RecordDeleted();

            case RecordWriteStatus.ValidationFailed when result.Validation is { } validation:
                outcome.MarkNotWritten();
                return ApiProblems.FromValidation(validation);

            case RecordWriteStatus.Forbidden:
                outcome.MarkNotWritten();
                return ApiProblems.Forbidden(ForbiddenAction.Update);

            default:
                throw new InvalidOperationException("The store returned an update result outside its contract.");
        }
    }

    /// <summary>
    /// Soft-deletes a record for <c>DELETE /api/forms/{viewId}/records/{globalRecordId}</c>: the base row's
    /// <c>RecStatus</c> becomes 0 with the last-save stamp. Page rows are not touched, nothing cascades and no save
    /// precedes it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The log line that follows a record the store deleted or found already deleted names it by the
    /// <c>GlobalRecordId</c> the store's locked read returned, as stored. It therefore carries the spelling the
    /// record's insert and update lines carry, whatever letter case the route used. The store's refusal carries no id,
    /// and no such line follows it.
    /// </para>
    /// <para>
    /// After a denied delete permission is a 403, the store's locked read decides the 404 and the already-deleted
    /// 204. For an active record, the store then reads the base table's permissions again inside its transaction and
    /// refuses, with nothing written, a delete the login may no longer apply: UPDATE denied on the base table. That
    /// refusal is the same 403. The service's earlier read only turns that check on.
    /// </para>
    /// <para>
    /// The store calls <see cref="SaveStampProvider.Create"/> only when its locked read finds an active record and its
    /// permission check passes, so the 404, the already-deleted 204 and the 403 never need a stamp. A stamp that
    /// cannot be made, because the process has no OS user name, fails only the delete of an active record, as the
    /// store's failure with nothing written: a 500 whose <c>writeOutcome</c> is <c>notWritten</c>.
    /// </para>
    /// </remarks>
    /// <param name="httpContext">The current request, which carries the <see cref="WriteOutcomeFeature"/>.</param>
    /// <param name="viewId">The form's <c>metaViews.ViewId</c> from the route.</param>
    /// <param name="globalRecordId">The record's <c>GlobalRecordId</c> from the route.</param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>
    /// 204 when the record was deleted, and also when it was already deleted, in which case nothing is written, stamps
    /// included, so a repeated <c>DELETE</c> is safe; 403 for a denied permission, before the store or by its
    /// in-transaction re-check; or 404 for an unknown form or record.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="httpContext"/> or <paramref name="globalRecordId"/> is <see langword="null"/>.
    /// </exception>
    public async Task<IResult> DeleteAsync(
        HttpContext httpContext,
        int viewId,
        string globalRecordId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(globalRecordId);

        var form = await store.GetFormMetadataAsync(viewId, cancellationToken).ConfigureAwait(false);
        if (form is null)
        {
            return ApiProblems.FormNotFound();
        }

        var permissions = await store.GetPermissionsAsync(form, cancellationToken).ConfigureAwait(false);
        if (!IsAllowed(permissions, form, ForbiddenAction.Delete))
        {
            return ApiProblems.Forbidden(ForbiddenAction.Delete);
        }

        // The store makes the stamp only for an active record its permission check lets through, the one case that
        // writes, so a 404, the already-deleted 204 and the store's 403 need none.
        var outcome = WriteOutcomeFeature.GetOrCreate(httpContext);
        outcome.MarkWriteStarted();

        var result = await store.SoftDeleteAsync(form, globalRecordId, stamps.Create, permissions, cancellationToken)
            .ConfigureAwait(false);

        // Each log line names the record by the stored id the store returned, never by the route's spelling.
        switch (result.Status)
        {
            case SoftDeleteStatus.Deleted when result.IsFound:
                // The store reported the commit; nothing after this line reads the database.
                outcome.MarkWritten();
                LogWrite(form, SoftDeleteOperation, result.GlobalRecordId, CommittedOutcome);
                return TypedResults.NoContent();

            // Each result below means the store wrote nothing, so a failure while the response is written must report
            // notWritten, not unknown; an already-deleted record is never marked written.
            case SoftDeleteStatus.AlreadyDeleted when result.IsFound:
                outcome.MarkNotWritten();
                LogWrite(form, SoftDeleteOperation, result.GlobalRecordId, UnchangedOutcome);
                return TypedResults.NoContent();

            case SoftDeleteStatus.NotFound:
                outcome.MarkNotWritten();
                return ApiProblems.RecordNotFound();

            case SoftDeleteStatus.Forbidden:
                outcome.MarkNotWritten();
                return ApiProblems.Forbidden(ForbiddenAction.Delete);

            default:
                throw new InvalidOperationException("The store returned a delete result outside its contract.");
        }
    }

    /// <summary>
    /// Runs the input checks a <c>POST</c> and a <c>PUT</c> share, before any transaction: the body, the shape of
    /// <c>values</c>, and <see cref="RecordValidator.ValidateInput"/>.
    /// </summary>
    /// <param name="loaded">The form loaded for this request.</param>
    /// <param name="request">The bound body, or <see langword="null"/> for a JSON <c>null</c> body.</param>
    /// <param name="validator">The validator for the form, for the effective-record checks that follow.</param>
    /// <param name="values">The submitted values under their canonical names, normalised.</param>
    /// <param name="problem">The 400 to return when a check fails.</param>
    /// <returns>Whether every check passed.</returns>
    private static bool TryValidateInput(
        LoadedForm loaded,
        RecordWriteRequest? request,
        [NotNullWhen(true)] out RecordValidator? validator,
        [NotNullWhen(true)] out IReadOnlyDictionary<string, FieldValue>? values,
        [NotNullWhen(false)] out IResult? problem)
    {
        validator = null;
        values = null;

        if (request is null)
        {
            problem = ApiProblems.RequestBodyRequired();
            return false;
        }

        var raw = ValuesReader.ReadRaw(request.Values);
        if (raw.RequestError is { } requestError)
        {
            // A fixed ValuesReader text: "values must be an object", or "malformed JSON" for a name or string that
            // cannot be decoded, which must not be reported as a shape problem.
            problem = ApiProblems.RequestLevel(requestError);
            return false;
        }

        if (!raw.IsSuccess)
        {
            problem = ApiProblems.ValidationFailed(raw.FieldErrorsFor(loaded.Metadata));
            return false;
        }

        var formValidator = new RecordValidator(loaded.Metadata, loaded.Definition);
        var input = formValidator.ValidateInput(raw.Values);
        if (!input.IsValid)
        {
            problem = ApiProblems.FromValidation(input);
            return false;
        }

        validator = formValidator;
        values = input.Values;
        problem = null;
        return true;
    }

    // The one place the service's own read meets the permission rule, whose derivation FormPermissions owns; the store
    // applies the same rule again inside each write's transaction. A store that reports no permissions means no check,
    // so a SQL permission failure then surfaces from the write itself.
    private static bool IsAllowed(FormPermissions? permissions, FormMetadata form, ForbiddenAction action) =>
        permissions is null || action switch
        {
            ForbiddenAction.Insert => permissions.CanInsert(form),
            ForbiddenAction.Update => permissions.CanUpdate(form),
            ForbiddenAction.Delete => permissions.CanDelete(form),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };

    private void LogWrite(FormMetadata form, string operation, string globalRecordId, string outcome)
    {
        try
        {
            LogRecordWrite(logger, operation, form.Name, form.ViewId, globalRecordId, outcome);
        }
        catch (Exception)
        {
            // Deliberately swallowed: the response is already decided, for a committed write or a record left
            // unchanged, and a failing log provider must never turn it into an error, which the client would read as
            // a save that may not have happened.
        }
    }

    [LoggerMessage(
        EventId = 1,
        EventName = "RecordWrite",
        Level = LogLevel.Information,
        Message = "Record {Operation} for form {Form} (view {ViewId}) record {GlobalRecordId}: {Outcome}")]
    private static partial void LogRecordWrite(
        ILogger logger,
        string operation,
        string form,
        int viewId,
        string globalRecordId,
        string outcome);
}
