using EpiInfo.Domain.Values;

namespace EpiInfo.Domain.Records;

/// <summary>
/// One row of the record grid: an item of the <c>items</c> array that <c>GET /api/forms/{viewId}/records</c>
/// returns, listed in <c>UniqueKey</c> order over the set the caller selected.
/// </summary>
/// <remarks>
/// <para>
/// A summary carries the record's identity, its status and its grid columns, and nothing else. It has no
/// <c>position</c> or <c>count</c>: the grid opens a row by <see cref="GlobalRecordId"/>, and the navigator then
/// shows "[n] of N" over the set its deleted toggle selects. n is the <see cref="NavigationPosition.Position"/> of
/// <c>GET /api/forms/{viewId}/records/{globalRecordId}/position</c> and N is the count from
/// <c>GET /api/forms/{viewId}/records/count</c>, both with the toggle's <c>includeDeleted</c>. Neither is taken from
/// the opened record's <see cref="RecordDto.Position"/> or <see cref="RecordDto.Count"/>, which are intrinsic to its
/// <c>RecStatus</c>.
/// </para>
/// <para>
/// This is a wire type. Each public property is a member of the response body and of the committed OpenAPI
/// document, so the type declares no other; the API's serializer settings write the names in camelCase and each
/// value as a plain scalar.
/// </para>
/// <para>
/// <see cref="Values"/> is field data, which the redaction policy keeps out of logs and error bodies. Never pass it,
/// or any entry of it, to a logger or an error message.
/// </para>
/// </remarks>
/// <param name="GlobalRecordId">
/// The record's <c>GlobalRecordId</c>, the GUID string that keys its base row and every page row. The grid opens
/// the record by it, through <c>GET /api/forms/{viewId}/records/{globalRecordId}</c>.
/// </param>
/// <param name="UniqueKey">
/// The record's base-table <c>UniqueKey</c>, the identity value by which the grid and every record set are
/// ordered, ascending.
/// </param>
/// <param name="RecStatus">
/// The record's <c>RecStatus</c>: <see cref="RecordQueryRules.ActiveRecStatus"/> for an active record, or
/// <see cref="RecordQueryRules.DeletedRecStatus"/> after a soft delete. The list holds deleted rows only when the
/// caller sets <c>includeDeleted=true</c>, and this value lets the grid mark them as deleted while its
/// "Show deleted" toggle is on.
/// </param>
/// <param name="Values">
/// The grid columns only: the first <see cref="RecordQueryRules.GridColumnCount"/> data fields whose type is
/// <c>text</c>, <c>number</c>, <c>date</c> or <c>legalValues</c>, in page <c>Position</c> order, then
/// <c>TabIndex</c> order, as
/// <see cref="RecordQueryRules.GetGridColumns(EpiInfo.Domain.Metadata.FormMetadata)"/> selects them. The map is
/// keyed by canonical <c>metaFields.Name</c> in that column order and holds <see cref="FieldValue.Null"/> for an
/// empty value. Seeded <c>CaseReport</c> gives <c>PhoneNumber, LastName, FirstName, Address, City, State</c>, and
/// seeded <c>FoodHistory</c> gives <c>CaseID, DateofInterview, FirstName, LastName, Sex, DOB</c>. Producers build
/// it with <see cref="RecordQueryRules.ProjectValues"/>, so its key order and case-insensitive lookup match every
/// other <c>values</c> map.
/// </param>
public sealed record RecordSummary(
    string GlobalRecordId,
    int UniqueKey,
    int RecStatus,
    IReadOnlyDictionary<string, FieldValue> Values);
