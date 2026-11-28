namespace EpiInfo.Domain.Records;

/// <summary>
/// Where one record stands in the set the navigator's deleted toggle selects: the body of
/// <c>GET /api/forms/{viewId}/records/{globalRecordId}/position</c>, the "[n] of N" the navigator shows.
/// </summary>
/// <remarks>
/// <para>
/// A record's own <c>position</c> and <c>count</c> on the Record DTO are intrinsic to its <c>RecStatus</c>, so an
/// active record keeps the same coordinates whatever the toggle says. This type carries the coordinates over the
/// caller's selected set instead, which is why it is separate from the Record DTO.
/// </para>
/// <para>
/// Both values are computed from the base table alone, never through a page table, so a base row that lacks a
/// page row is still placed.
/// </para>
/// </remarks>
/// <param name="Position">
/// The record's 1-based position in the selected set: the number of selected-set rows whose <c>UniqueKey</c> is
/// at most the record's own, ordered by <c>UniqueKey</c> ascending. This is the legacy record-number formula, a
/// count of rows with <c>UniqueKey &lt;= k</c> (<c>Epi.Enter.CheckCodeEngine/RunTimeView.cs</c>, lines 673-694),
/// restricted to the selected set. <see langword="null"/> when the record exists but is outside the selected
/// set, for example a record with <c>RecStatus = 0</c> queried without <c>includeDeleted</c>.
/// </param>
/// <param name="Count">
/// The number of rows in the selected set: the active rows (<c>RecStatus = 1</c>) by default, or every row when
/// <c>includeDeleted</c> is <see langword="true"/>. <c>RecordQueryRules</c> owns that predicate.
/// </param>
public sealed record NavigationPosition(int? Position, int Count);
