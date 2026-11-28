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
/// <b>Invariant.</b> The one public constructor checks the item limits and keeps <see cref="Truncated"/> derived from
/// <see cref="Total"/>, and no member can be assigned afterwards, a <c>with</c> expression included, so every
/// instance holds at most <see cref="RecordQueryRules.SearchCap"/> items, never more items than <see cref="Total"/>
/// counts, and a <see cref="Truncated"/> that is <c>Total &gt; 200</c>. The serializer reads a response back through
/// that constructor, so a deserialized body passes the same checks and its <c>truncated</c> member is derived again,
/// never read.
/// </para>
/// </remarks>
public sealed record SearchResponse
{
    /// <summary>
    /// Builds a response from one capped page of matches and the uncapped match count. <see cref="Truncated"/> is
    /// derived from <paramref name="total"/> with <see cref="RecordQueryRules.IsTruncated"/>, never supplied.
    /// </summary>
    /// <param name="items">
    /// The matches to return, already ordered by <c>UniqueKey</c> and capped at
    /// <see cref="RecordQueryRules.SearchCap"/>. The response keeps a read-only copy, so a later change to the
    /// caller's list cannot break the limits checked here.
    /// </param>
    /// <param name="total">The number of matches in the selected set after the exclusion, before the cap.</param>
    /// <exception cref="ArgumentNullException"><paramref name="items"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="total"/> is negative, or <paramref name="items"/> holds more than
    /// <see cref="RecordQueryRules.SearchCap"/> results or more results than <paramref name="total"/> counts.
    /// </exception>
    public SearchResponse(IReadOnlyList<SearchResult> items, int total)
    {
        ArgumentNullException.ThrowIfNull(items);

        // Fixed messages only: the redaction policy keeps submitted and computed values out of exception text.
        if (total < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(total), "A search total must not be negative.");
        }

        // The limits are checked on the list's count before any item is read, so a list already outside them is
        // rejected without being copied, however large it is.
        ThrowIfOutsideTheLimits(items.Count);

        // They are checked again on the copy the response keeps, because the caller's list can change, or report a
        // wrong count, between the count and the copy. The copy reads at most one item past the cap, which is enough
        // to tell that the list holds too many, so no list can make it read without bound.
        SearchResult[] snapshot = [.. items.Take(RecordQueryRules.SearchCap + 1)];
        ThrowIfOutsideTheLimits(snapshot.Length);

        Items = Array.AsReadOnly(snapshot);
        Total = total;

        void ThrowIfOutsideTheLimits(int itemCount)
        {
            if (itemCount > RecordQueryRules.SearchCap)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(items),
                    $"A search response holds at most {RecordQueryRules.SearchCap} items.");
            }

            if (itemCount > total)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(items),
                    "A search response cannot hold more items than its total counts.");
            }
        }
    }

    /// <summary>
    /// The matching results in <c>UniqueKey</c> ascending order: the first <see cref="RecordQueryRules.SearchCap"/>
    /// (200) matches at most, each carrying its position over the selected set and its grid-column values. Empty, never
    /// <see langword="null"/>, when nothing matches.
    /// </summary>
    public IReadOnlyList<SearchResult> Items { get; }

    /// <summary>
    /// The number of matches in the selected set after the <c>excludeGlobalRecordId</c> exclusion, counted before the
    /// cap, so it can exceed the number of <see cref="Items"/>.
    /// </summary>
    public int Total { get; }

    /// <summary>
    /// Whether <see cref="Total"/> exceeds <see cref="RecordQueryRules.SearchCap"/>, that is <c>Total &gt; 200</c>, so
    /// <see cref="Items"/> holds fewer results than were found. Always derived from <see cref="Total"/>.
    /// </summary>
    public bool Truncated => RecordQueryRules.IsTruncated(Total);

    /// <summary>
    /// Builds a response exactly as the constructor does: from one capped page of matches and the uncapped match
    /// count, deriving <see cref="Truncated"/> with <see cref="RecordQueryRules.IsTruncated"/>.
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
    public static SearchResponse Create(IReadOnlyList<SearchResult> items, int total) => new(items, total);
}
