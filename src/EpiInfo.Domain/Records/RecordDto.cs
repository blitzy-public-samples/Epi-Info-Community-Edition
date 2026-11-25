using EpiInfo.Domain.Values;

namespace EpiInfo.Domain.Records;

/// <summary>
/// One record as the API returns it: the body of <c>GET /api/forms/{viewId}/records/at/{position}</c>, of
/// <c>GET /api/forms/{viewId}/records/{globalRecordId}</c> (deleted records included), of the 201 a <c>POST</c>
/// creates and of the 200 a <c>PUT</c> returns.
/// </summary>
/// <remarks>
/// <para>
/// <b>Wire type.</b> Each public property is a member of the response body and of the committed OpenAPI document,
/// so the type declares no other public instance member; the static <see cref="From"/> factory is not serialised.
/// The API's serializer settings write the names in camelCase, and its field-value converter writes each entry of
/// <see cref="Values"/> as a plain scalar.
/// </para>
/// <para>
/// <b>Intrinsic coordinates.</b> <see cref="Position"/> and <see cref="Count"/> belong to the record, not to the
/// request: they are taken over the record's intrinsic set, which its <see cref="RecStatus"/> chooses, so an active
/// record is numbered within the active set and a deleted one within every row, whatever <c>includeDeleted</c> the
/// caller sent. The navigator's "[n] of N" is taken over the set its deleted toggle selects: N is the count from
/// <c>GET /api/forms/{viewId}/records/count</c> with the toggle's <c>includeDeleted</c>, and n is the requested
/// position after <c>at/{n}</c>; after an open by id, a save of a new or an existing record or a toggle change, it
/// is the <see cref="NavigationPosition.Position"/> from
/// <c>GET /api/forms/{viewId}/records/{globalRecordId}/position</c> with the same <c>includeDeleted</c>. Neither
/// pair is derived from the other, and equal numbers in the two pairs do not mean the two sets are the same.
/// </para>
/// <para>
/// <b>Equality.</b> The synthesized record equality compares <see cref="Values"/> by reference, so two DTOs built
/// from separate reads of the same row are not equal. Compare the maps entry by entry.
/// </para>
/// <para>
/// <b>Redaction.</b> <see cref="Values"/> is field data, which must never reach a log line or an error body. Log
/// calls name a record by <see cref="GlobalRecordId"/> or <see cref="UniqueKey"/> only. The synthesized
/// <see cref="object.ToString"/> prints the map's type name rather than its entries; even so, never pass a DTO or
/// its values to a logger or an error message.
/// </para>
/// </remarks>
/// <param name="GlobalRecordId">
/// The base-table <c>GlobalRecordId</c>: the record's identity in every API route and the key its page rows are
/// joined on. A record the application creates gets a new GUID string, which the <c>Location</c> header of the 201
/// also carries.
/// </param>
/// <param name="UniqueKey">
/// The base-table <c>UniqueKey</c>, the <c>int</c> identity value by which every record set is ordered, ascending.
/// </param>
/// <param name="RecStatus">
/// The base-table <c>RecStatus</c>, exactly as stored: <see cref="RecordQueryRules.ActiveRecStatus"/> (1) for an
/// active record and <see cref="RecordQueryRules.DeletedRecStatus"/> (0) after a soft delete. It decides the set
/// <see cref="Position"/> and <see cref="Count"/> are taken over.
/// </param>
/// <param name="Position">
/// The record's 1-based intrinsic position: the number of rows of its intrinsic set whose <c>UniqueKey</c> is at
/// most the record's own, the legacy record-number formula, a count of rows with <c>UniqueKey &lt;= k</c>
/// (<c>RunTimeView.cs</c>, lines 673-694), restricted to that set. The intrinsic set is the active set
/// (<c>RecStatus = 1</c>) when <see cref="RecStatus"/> is 1, and every row, deleted records included, when it is 0
/// or any other value; callers choose it with <see cref="RecordQueryRules.IntrinsicIncludesDeleted"/>. The value is
/// the same in every response that returns the record, whatever the caller's <c>includeDeleted</c>.
/// </param>
/// <param name="Count">
/// The number of rows in the record's intrinsic set, the same set <see cref="Position"/> is counted over: the active
/// rows for an active record, every row for any other. Like <see cref="Position"/>, it does not follow the
/// caller's <c>includeDeleted</c>.
/// </param>
/// <param name="Values">
/// One entry per data field of the form, keyed by the canonical <c>metaFields.Name</c> and kept in data-field
/// order (page <c>Position</c>, then <c>TabIndex</c>), as the map from
/// <see cref="RecordQueryRules.CreateValuesMap"/> holds it. Labels, groups and unsupported fields have no entry. An
/// empty value, from a <c>NULL</c> column or from a page with no row for the record, is
/// <see cref="FieldValue.Null"/>. Each
/// entry reaches the client as the scalar its field type sets: a string for text, multiline and legal-values
/// fields, a number for number fields, a boolean for checkboxes, a <c>YYYY-MM-DD</c> string for date fields and
/// the 0-based option index for option fields.
/// </param>
public sealed record RecordDto(
    string GlobalRecordId,
    int UniqueKey,
    int RecStatus,
    int Position,
    int Count,
    IReadOnlyDictionary<string, FieldValue> Values)
{
    /// <summary>
    /// Builds the DTO of a stored record together with its intrinsic coordinates.
    /// </summary>
    /// <remarks>
    /// The values map is taken over as it is, not copied: a <see cref="StoredRecord"/> producer hands over a map it
    /// no longer changes, so the DTO keeps its data-field order and case-insensitive lookup.
    /// </remarks>
    /// <param name="record">The record as the store read it.</param>
    /// <param name="position">
    /// The record's position over the set <see cref="RecordQueryRules.IntrinsicIncludesDeleted"/> selects for its
    /// <c>RecStatus</c>.
    /// </param>
    /// <param name="count">The number of rows in that same set.</param>
    /// <returns>
    /// A DTO carrying the record's <c>GlobalRecordId</c>, <c>UniqueKey</c>, <c>RecStatus</c> and values, with
    /// <paramref name="position"/> and <paramref name="count"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="record"/> is <see langword="null"/>.</exception>
    public static RecordDto From(StoredRecord record, int position, int count)
    {
        ArgumentNullException.ThrowIfNull(record);

        return new RecordDto(
            record.GlobalRecordId,
            record.UniqueKey,
            record.RecStatus,
            position,
            count,
            record.Values);
    }
}
