using EpiInfo.Domain.Values;

namespace EpiInfo.Domain.Records;

/// <summary>
/// One Find result: an item of the <c>items</c> array in the body of
/// <c>POST /api/forms/{viewId}/records/search</c>. The Find dialog lists each result with its position and opens
/// it by <see cref="GlobalRecordId"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Order and cap.</b> The producer returns results ordered by <c>UniqueKey</c> ascending and keeps at most
/// <see cref="RecordQueryRules.SearchCap"/> of them; the response's <c>total</c> still counts every match and its
/// <c>truncated</c> flag is <see cref="RecordQueryRules.IsTruncated"/>. Legacy Find returned its matches in no
/// stated order and with no cap (<c>CollectedDataProvider.cs</c>, lines 1890-2380); the departure is recorded in
/// the parity notes' "Search semantics" section.
/// </para>
/// <para>
/// <b>Not a Record.</b> A result carries the grid columns only, like a record summary. Its <see cref="Position"/>
/// is taken over the caller's selected set, the one the search's <c>includeDeleted</c> chooses. The Record DTO's
/// own <c>position</c> and <c>count</c> are taken over the record's intrinsic set, the one its <c>RecStatus</c>
/// chooses, whatever <c>includeDeleted</c> the search sent. Each is computed over its own set, never from the other.
/// </para>
/// <para>
/// <see cref="Values"/> is field data, which the redaction policy keeps out of logs and error bodies. The
/// synthesized <see cref="ToString"/> prints the map's type name rather than its entries; even so, never pass a
/// result or its values to a logger or an error message.
/// </para>
/// </remarks>
/// <param name="GlobalRecordId">
/// The record's <c>GlobalRecordId</c>, the identity the client opens the result by.
/// </param>
/// <param name="UniqueKey">The record's <c>UniqueKey</c>, the key results are ordered by.</param>
/// <param name="RecStatus">
/// The record's <c>RecStatus</c>: <see cref="RecordQueryRules.ActiveRecStatus"/> for an active record, or
/// <see cref="RecordQueryRules.DeletedRecStatus"/> for a deleted one, which a search returns only with
/// <c>includeDeleted=true</c>.
/// </param>
/// <param name="Position">
/// The record's 1-based <c>ROW_NUMBER()</c> over the selected set, ordered by <c>UniqueKey</c>. The selected set
/// is the active rows, <c>RecStatus = 1</c>, by default, or every row with <c>includeDeleted=true</c>, the predicate
/// <see cref="RecordQueryRules.RequiredRecStatus"/> gives. The number is taken before the criteria filter and
/// before the <c>excludeGlobalRecordId</c> exclusion, so it equals the navigation position: the legacy
/// record-number formula, the count of rows with <c>UniqueKey &lt;= k</c> (<c>RunTimeView.cs</c>, lines 673-694),
/// restricted to the selected set. Opening <c>at/{Position}</c> with the same <c>includeDeleted</c> therefore
/// returns this record. It is not the Record DTO's intrinsic position.
/// </param>
/// <param name="Values">
/// The record's grid-column values only, keyed by canonical field name in grid order and compared ordinally, as a
/// record summary carries them: the projection <see cref="RecordQueryRules.ProjectValues"/> builds over the form's
/// grid columns. A grid column with no stored value is <see cref="FieldValue.Null"/>.
/// </param>
public sealed record SearchResult(
    string GlobalRecordId,
    int UniqueKey,
    int RecStatus,
    int Position,
    IReadOnlyDictionary<string, FieldValue> Values);
