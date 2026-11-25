namespace EpiInfo.Domain.Records;

/// <summary>
/// The Find response: the body of the 200 that <c>POST /api/forms/{viewId}/records/search</c> returns. A criterion
/// that matches nothing is still a 200, with <see cref="Items"/> empty, <see cref="Total"/> 0 and
/// <see cref="Truncated"/> <see langword="false"/>, never a 404.
/// </summary>
/// <remarks>
/// <para>
/// <b>Set and exclusion.</b> The matches are taken from the caller's selected set, the predicate
/// <see cref="RecordQueryRules.RequiredRecStatus"/> gives, with the record named by <c>excludeGlobalRecordId</c>
/// (the record open in the editor) removed on the server before <see cref="Total"/> is counted and before the cap
/// is applied. Legacy Find filtered <c>RecStatus &gt; 0</c> and dropped the open record from the result table only
/// after the query had run (<c>Epi.Core/Data/Services/CollectedDataProvider.cs</c>, lines 2249 and 2256-2266).
/// </para>
/// <para>
/// <b>Order and cap.</b> Legacy returned its matches in no stated order and with no cap. Here the items are ordered
/// by <c>UniqueKey</c> ascending and at most <see cref="RecordQueryRules.SearchCap"/> of them are returned; both
/// departures are recorded in the parity notes' "Search semantics" section.
/// </para>
/// <para>
/// Build every instance through <see cref="Create"/>, which derives <see cref="Truncated"/> from
/// <see cref="Total"/>, so the two can never disagree.
/// </para>
/// </remarks>
/// <param name="Items">
/// The matching results in <c>UniqueKey</c> ascending order: the first <see cref="RecordQueryRules.SearchCap"/>
/// (200) matches at most, each carrying its position over the selected set and its grid-column values. Empty, never
/// <see langword="null"/>, when nothing matches.
/// </param>
/// <param name="Total">
/// The number of matches in the selected set after the <c>excludeGlobalRecordId</c> exclusion, counted before the
/// cap, so it can exceed the number of <see cref="Items"/>.
/// </param>
/// <param name="Truncated">
/// Whether <see cref="Total"/> exceeds <see cref="RecordQueryRules.SearchCap"/>, that is <c>Total &gt; 200</c>, so
/// <see cref="Items"/> holds fewer results than were found.
/// </param>
public sealed record SearchResponse(IReadOnlyList<SearchResult> Items, int Total, bool Truncated)
{
    /// <summary>
    /// Builds a response from one capped page of matches and the uncapped match count, deriving
    /// <see cref="Truncated"/> with <see cref="RecordQueryRules.IsTruncated"/>. Every producer of a search response
    /// calls this factory rather than the constructor.
    /// </summary>
    /// <param name="items">
    /// The matches to return, already ordered by <c>UniqueKey</c> and capped at
    /// <see cref="RecordQueryRules.SearchCap"/>.
    /// </param>
    /// <param name="total">The number of matches in the selected set after the exclusion, before the cap.</param>
    /// <returns>A response whose <see cref="Truncated"/> is <c><paramref name="total"/> &gt; 200</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="items"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="total"/> is negative, or <paramref name="items"/> holds more than
    /// <see cref="RecordQueryRules.SearchCap"/> results or more results than <paramref name="total"/> counts.
    /// </exception>
    public static SearchResponse Create(IReadOnlyList<SearchResult> items, int total)
    {
        ArgumentNullException.ThrowIfNull(items);

        // Fixed messages only: the redaction policy keeps submitted and computed values out of exception text.
        if (total < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(total), "A search total must not be negative.");
        }

        if (items.Count > RecordQueryRules.SearchCap)
        {
            throw new ArgumentOutOfRangeException(
                nameof(items),
                $"A search response holds at most {RecordQueryRules.SearchCap} items.");
        }

        if (items.Count > total)
        {
            throw new ArgumentOutOfRangeException(
                nameof(items),
                "A search response cannot hold more items than its total counts.");
        }

        return new SearchResponse(items, total, RecordQueryRules.IsTruncated(total));
    }
}
