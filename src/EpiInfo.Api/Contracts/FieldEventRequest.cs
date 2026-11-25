using System.Text.Json;

namespace EpiInfo.Api.Contracts;

/// <summary>
/// Request body of <c>POST /api/forms/{viewId}/checkcode/field-event</c>: one check-code event the browser raised
/// on one field, with the editor's current record values. The request is stateless: it carries no session, record
/// generation or capture sequence, and nothing is persisted when it is evaluated.
/// </summary>
/// <remarks>
/// <para>
/// Wire shape:
/// <c>{"event":"FieldAfter","fieldName":"DOB","values":{"DOB":"1990-05-01","DateofInterview":"2011-02-01"}}</c>.
/// </para>
/// <para>
/// Every member is a raw <see cref="JsonElement"/>, and a missing member binds as <see langword="null"/>, so binding
/// never rejects a body and every member is validated by FieldEventService with its own <c>errors</c> entry. A
/// <c>string</c> member would turn a JSON number into a binder error with no <c>errors["event"]</c> entry, and an
/// enum member would let enum converters accept integers and other spellings. The binder therefore depends on the
/// JSON options of JsonSetup leaving <c>RespectRequiredConstructorParameters</c> and
/// <c>RespectNullableAnnotations</c> off. ScalarSchemaTransformer publishes the wire schema of each member, so no
/// <see cref="JsonElement"/> shape appears in the OpenAPI document.
/// </para>
/// <para>
/// Callers supply only an event name, a field name and values. The body has no member for save stamps,
/// <c>RecStatus</c>, <c>UniqueKey</c>, <c>GlobalRecordId</c> or program text: the API reads the program from
/// <c>metaViews.CheckCode</c> itself.
/// </para>
/// </remarks>
/// <param name="Event">
/// The event raised, valid only as a JSON string that
/// <see cref="EpiInfo.Domain.Events.FieldEventKindNames.TryParseExact"/> accepts: exactly <c>FieldBefore</c>,
/// <c>FieldAfter</c> or <c>FieldClick</c>, compared ordinally and case-sensitively. They select the field's
/// <c>Before</c>, <c>After</c> and <c>Click</c> block. A missing value, <c>null</c>, a number, a boolean, an object or
/// any other spelling, <c>fieldafter</c> included, is a 400 with <c>errors["event"]</c>.
/// </param>
/// <param name="FieldName">
/// The field the event was raised on, valid only as a JSON string naming a field of the form, matched
/// case-insensitively to its canonical <c>metaFields.Name</c>. A missing value, a non-string or an unknown name is a
/// 400 with <c>errors["fieldName"]</c>. A known field whose program has no matching block gets 200 with the empty
/// envelope <c>{"enabled":[],"disabled":[],"cleared":[],"assigned":{},"dialogs":[],"diagnostics":[]}</c>.
/// </param>
/// <param name="Values">
/// The editor's current record values, a JSON object keyed by field name that ValuesReader converts into field
/// values in document order. Only two rules apply: every key is a data field of the form, read-only fields included,
/// and every value has its wire type (an ISO <c>YYYY-MM-DD</c> date, a finite number, a boolean, a string or
/// <c>null</c>). Read-only, range, length, mask, legal-value, required and empty-record rules are not applied,
/// because the editor must be able to evaluate stored values that break current rules. FieldEventService answers a
/// missing or non-object <c>values</c>.
/// </param>
public sealed record FieldEventRequest(JsonElement? Event, JsonElement? FieldName, JsonElement? Values)
{
    /// <summary>Returns the type name only.</summary>
    /// <returns><c>FieldEventRequest</c>.</returns>
    /// <remarks>
    /// Replaces the synthesised record <c>ToString</c>, which would print the field values, so a request logged by
    /// mistake can never carry field data into a log.
    /// </remarks>
    public override string ToString() => nameof(FieldEventRequest);
}
