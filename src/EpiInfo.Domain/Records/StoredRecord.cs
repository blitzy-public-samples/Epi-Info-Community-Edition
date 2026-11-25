using EpiInfo.Domain.Values;

namespace EpiInfo.Domain.Records;

/// <summary>
/// One record as the store read it: the base-table row of a form together with its page rows. It travels only
/// inside the server, between the store, the record service and the validator, and is never sent to a client.
/// Responses carry a <c>RecordDto</c> built from it, which adds the record's intrinsic position and count.
/// </summary>
/// <remarks>
/// <para>
/// <b>Immutability.</b> The values are exposed only as <see cref="IReadOnlyDictionary{TKey, TValue}"/>, and a
/// producer hands over a map it no longer changes. Anything that needs a different set of values, such as the
/// <c>PUT</c> overlay of submitted values on stored ones that the required gate checks, builds a new map instead.
/// </para>
/// <para>
/// <b>Equality.</b> The synthesized record equality compares <see cref="Values"/> by reference, so two reads of
/// the same row are not equal. Compare the maps entry by entry.
/// </para>
/// <para>
/// <b>Redaction.</b> <see cref="Values"/> holds field data, which must never reach a log line or an error body.
/// Log calls name a record by <see cref="GlobalRecordId"/> or <see cref="UniqueKey"/> only. The synthesized
/// <see cref="object.ToString"/> prints the map through the map's own <see cref="object.ToString"/>, which for the
/// framework dictionaries is the type name and never an entry.
/// </para>
/// </remarks>
/// <param name="GlobalRecordId">
/// The base-table <c>GlobalRecordId</c> (<c>nvarchar(255)</c>, not null): the record's identity in every API
/// route and the key its page rows are joined on. Records the application inserts get a new GUID string; seeded
/// rows keep the text they hold.
/// </param>
/// <param name="UniqueKey">
/// The base-table <c>UniqueKey</c>, its <c>int</c> identity primary key. Records are ordered by it, and the
/// position of the record with key k is the number of selected-set rows whose <c>UniqueKey</c> is at most k.
/// </param>
/// <param name="RecStatus">
/// The base-table <c>RecStatus</c>, a <c>smallint</c> widened to <see cref="int"/> and kept exactly as read:
/// <see cref="RecordQueryRules.ActiveRecStatus"/> (1) for an active record and
/// <see cref="RecordQueryRules.DeletedRecStatus"/> (0) for a soft-deleted one. Any other value is preserved, not
/// mapped: such a row is outside the active set, and its intrinsic coordinates are taken over every row
/// (<see cref="RecordQueryRules.IntrinsicIncludesDeleted"/>).
/// </param>
/// <param name="Values">
/// One entry per data field of the form, keyed by the canonical <c>metaFields.Name</c> and inserted in data-field
/// order (page <c>Position</c>, then <c>TabIndex</c>). Producers build it with
/// <see cref="RecordQueryRules.CreateValuesMap"/>, so a lookup ignores letter case. A <c>NULL</c> column, or a page
/// with no row for this <see cref="GlobalRecordId"/>, gives <see cref="FieldValue.Null"/> for each of its fields,
/// as the legacy per-page load (<c>CollectedDataProvider.LoadRecordIntoView</c>) nulls every field of a page
/// that has no row. Label, group and unsupported fields have no entry, because they are never stored or written.
/// </param>
public sealed record StoredRecord(
    string GlobalRecordId,
    int UniqueKey,
    int RecStatus,
    IReadOnlyDictionary<string, FieldValue> Values)
{
    /// <summary>
    /// Whether the record is in the active set: <see cref="RecStatus"/> is exactly
    /// <see cref="RecordQueryRules.ActiveRecStatus"/>. This is not the deleted test: a <c>PUT</c> is refused with
    /// 409 only for <see cref="RecordQueryRules.DeletedRecStatus"/>.
    /// </summary>
    public bool IsActive => RecStatus == RecordQueryRules.ActiveRecStatus;
}
