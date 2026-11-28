namespace EpiInfo.Domain.Records;

/// <summary>
/// The coerced inputs of the records list, <c>GET /api/forms/{viewId}/records</c>: the grid filter <c>q</c>, the
/// deleted toggle <c>includeDeleted</c> (default <see langword="false"/>), and the paging parameters <c>page</c>
/// (default <see cref="RecordQueryRules.DefaultPage"/>) and <c>pageSize</c> (default
/// <see cref="RecordQueryRules.DefaultPageSize"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Construction.</b> The API's request-parameter coercion parses the query string and checks the paging bounds
/// with <see cref="RecordQueryRules.ValidatePaging"/> before it constructs this record: a <c>page</c> below 1, or a
/// <c>pageSize</c> outside 1 to 200, is a 400 keyed by the parameter's name and never reaches this type. The record
/// itself does not repeat that check; <see cref="Offset"/> throws when it meets bounds that were not validated.
/// </para>
/// <para>
/// <b>Semantics.</b> The rows come from the selected set that
/// <see cref="RecordQueryRules.RequiredRecStatus"/> gives for <see cref="IncludeDeleted"/>: <c>RecStatus = 1</c>
/// by default, every row when <see cref="IncludeDeleted"/> is <see langword="true"/>. They are ordered by
/// <c>UniqueKey</c> ascending, filtered by <see cref="Filter"/>, then paged at <see cref="Offset"/> for
/// <see cref="PageSize"/> rows. The list's <c>total</c> counts the filtered set, so a valid page past the end
/// returns no items with the correct total. This type only carries the input: the data layer renders the filter
/// as a case-insensitive "contains" over the grid columns, independent of the database collation, with the
/// pattern characters of <c>q</c> escaped and the text passed as a parameter.
/// </para>
/// <para>
/// <b>Not a wire type.</b> Endpoints bind the four query parameters as strings and build this record from them;
/// they never bind it with <c>[AsParameters]</c>. The OpenAPI parameter schemas come from the API's parameter
/// schema transformer, so the computed <see cref="Filter"/> and <see cref="Offset"/> never reach JSON or OpenAPI.
/// </para>
/// <para>
/// <b>Redaction.</b> <see cref="Q"/> is a query-string value, which must never be logged or placed in an error
/// body. <see cref="ToString"/> therefore returns the type name only.
/// </para>
/// </remarks>
/// <param name="Q">
/// The grid filter exactly as the caller sent it, or <see langword="null"/> when <c>q</c> was absent. Callers read
/// <see cref="Filter"/>, which treats a blank value as no filter.
/// </param>
/// <param name="IncludeDeleted">
/// Whether every row is selected; <see langword="false"/> selects the active set only.
/// </param>
/// <param name="Page">The 1-based page number, already checked by <see cref="RecordQueryRules.ValidatePaging"/>.</param>
/// <param name="PageSize">
/// The number of records per page, from 1 to 200, already checked by <see cref="RecordQueryRules.ValidatePaging"/>.
/// </param>
public sealed record RecordListQuery(string? Q, bool IncludeDeleted, int Page, int PageSize)
{
    /// <summary>
    /// The query a request with no parameters makes: no filter, the active set, page
    /// <see cref="RecordQueryRules.DefaultPage"/> of <see cref="RecordQueryRules.DefaultPageSize"/> records.
    /// </summary>
    public static RecordListQuery Default { get; } =
        new(null, false, RecordQueryRules.DefaultPage, RecordQueryRules.DefaultPageSize);

    /// <summary>
    /// The filter text to apply, or <see langword="null"/> for no filter. An absent, empty or whitespace-only
    /// <see cref="Q"/> means no filter; any other value is passed unchanged, surrounding spaces included, because
    /// <c>q</c> is not trimmed.
    /// </summary>
    public string? Filter => string.IsNullOrWhiteSpace(Q) ? null : Q;

    /// <summary>
    /// The 0-based offset of the page's first row in <c>UniqueKey</c> order, <c>(Page - 1) * PageSize</c>, from
    /// <see cref="RecordQueryRules.ComputeOffset"/>. It is 64-bit so the largest admitted page, 2,147,483,647,
    /// cannot wrap; the data layer binds it as a <c>bigint</c> offset.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="Page"/> or <see cref="PageSize"/> is outside its admitted range, because the bounds were not
    /// validated before construction.
    /// </exception>
    public long Offset => RecordQueryRules.ComputeOffset(Page, PageSize);

    /// <summary>
    /// Returns the type name only. The compiler-generated record <c>ToString</c> would print <see cref="Q"/>, a
    /// query-string value the redaction policy keeps out of logs and error bodies, and would evaluate
    /// <see cref="Offset"/>, which throws for unvalidated bounds.
    /// </summary>
    /// <returns><c>RecordListQuery</c>.</returns>
    public override string ToString() => nameof(RecordListQuery);
}
