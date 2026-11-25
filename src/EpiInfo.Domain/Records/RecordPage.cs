namespace EpiInfo.Domain.Records;

/// <summary>
/// One page of the record grid: the body of <c>GET /api/forms/{viewId}/records</c>, which the grid's search box,
/// "Show deleted" toggle and paging controls drive.
/// </summary>
/// <remarks>
/// <para>
/// <b>Set, filter and order.</b> The rows come from the set the request selects with <c>includeDeleted</c>, through
/// the predicate <see cref="RecordQueryRules.RequiredRecStatus"/> gives: <c>RecStatus = 1</c> by default, every row
/// with <c>includeDeleted=true</c>. A non-blank <c>q</c> then keeps the rows in which any grid column contains it,
/// ignoring case. What remains is ordered by <c>UniqueKey</c> ascending and paged. Legacy had no records list; its
/// nearest counterpart, the record count (<c>CollectedDataProvider.cs</c>, <c>GetRecordCount</c>), counted every row
/// of the view's table with no status filter.
/// </para>
/// <para>
/// <b>Past the end.</b> A valid page beyond the last row is not an error: the response is 200 with
/// <see cref="Items"/> empty and <see cref="Total"/> still counting the filtered set. That holds for the largest
/// admitted page, 2,147,483,647, whose offset <see cref="RecordQueryRules.ComputeOffset"/> computes in 64 bits so it
/// cannot wrap, and for a form with no records, which gives <see cref="Items"/> empty and <see cref="Total"/> 0.
/// </para>
/// <para>
/// <b>Wire type.</b> Each public property is a member of the response body and of the committed OpenAPI document,
/// so the type declares no other; the API's serializer settings write the names in camelCase.
/// </para>
/// <para>
/// <b>Redaction.</b> <see cref="Items"/> carries field data, which the redaction policy keeps out of logs and error
/// bodies. The synthesized <see cref="ToString"/> prints the list's type name rather than its entries; even so,
/// never pass a page or its items to a logger or an error message.
/// </para>
/// </remarks>
/// <param name="Items">
/// The page's record summaries: at most <see cref="PageSize"/> of them, in <c>UniqueKey</c> ascending order,
/// starting at the 0-based offset <see cref="RecordQueryRules.ComputeOffset"/> gives for <see cref="Page"/> and
/// <see cref="PageSize"/>. Each carries the form's grid columns only. Never <see langword="null"/>: a page past the
/// end, <c>page=2147483647</c> and every page of an empty form included, is an empty list.
/// </param>
/// <param name="Total">
/// The number of rows in the selected set after the <c>q</c> filter, across every page, not only this one. It does
/// not depend on <see cref="Page"/> or <see cref="PageSize"/>, so the grid can show the full count and the number
/// of pages whichever page it holds.
/// </param>
/// <param name="Page">
/// The 1-based page number the request asked for, as coerced from the query string:
/// <see cref="RecordQueryRules.DefaultPage"/> when <c>page</c> was absent.
/// </param>
/// <param name="PageSize">
/// The page size the request asked for, from <see cref="RecordQueryRules.MinPageSize"/> to
/// <see cref="RecordQueryRules.MaxPageSize"/>, as coerced from the query string:
/// <see cref="RecordQueryRules.DefaultPageSize"/> when <c>pageSize</c> was absent.
/// </param>
public sealed record RecordPage(IReadOnlyList<RecordSummary> Items, int Total, int Page, int PageSize);
