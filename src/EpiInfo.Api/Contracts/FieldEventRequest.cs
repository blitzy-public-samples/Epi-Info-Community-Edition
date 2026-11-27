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
/// Every member is a raw <see cref="JsonElement"/>, so once the body is a well-formed JSON object, binding accepts
/// each member in any JSON kind and leaves every member check to <c>FieldEventService</c>. A <c>string</c> member
/// would turn a JSON number into a binder error with no <c>errors["event"]</c> entry, and an enum member would let
/// enum converters accept integers and other spellings. Presence and nullability are separate. A missing member
/// binds as <see langword="null"/> because the JSON options of <see cref="EpiInfo.Api.Json.JsonSetup"/> leave
/// <c>RespectRequiredConstructorParameters</c> off; on, it would fail binding on the missing member. An explicit
/// <c>null</c> binds as <see langword="null"/> either way, and <c>RespectNullableAnnotations</c> does not reject it,
/// because that option applies to non-nullable reference members and <c>JsonElement?</c> is a nullable value type.
/// <c>ScalarSchemaTransformer</c> publishes the wire schema of each member, so no <see cref="JsonElement"/> shape
/// appears in the OpenAPI document.
/// </para>
/// <para>
/// The endpoint reads the whole body as one JSON value through <c>JsonRequestBody</c> before it reads this type, and
/// answers a body that is not a JSON object itself. Content that is not valid JSON, such as truncated JSON or
/// content after the value, is a <c>BadHttpRequestException</c> that <c>ApiExceptionHandler</c> maps to the
/// request-level 400 "malformed JSON". A valid value that is a JSON array, string, number or boolean is the
/// request-level 400 "request body must be a JSON object". An object that names a member more than once, exactly or
/// differing only by letter case, such as two <c>event</c> or <c>fieldName</c> members, is the request-level 400
/// "request body names a member more than once", so no occurrence is bound in place of another. A body that is the
/// JSON literal <c>null</c> reads as no request and gets the request-level 400 "request body is required", which
/// <c>JsonContentTypeMiddleware</c> gives a missing or empty body before the endpoint runs.
/// </para>
/// <para>
/// <c>FieldEventService</c> answers a bound request with one of two 400 shapes. A missing, <c>null</c>, wrongly typed
/// or unrecognised <c>event</c> or <c>fieldName</c> is field-level, with <c>errors["event"]</c> or
/// <c>errors["fieldName"]</c>, and so is a <c>values</c> key or value that <c>ValuesReader</c> rejects. A
/// <c>values</c> that is missing, <c>null</c> or not a JSON object is request-level instead: its <c>detail</c> is
/// <see cref="EpiInfo.Domain.Validation.ValidationMessages.ValuesMustBeObject"/> and it carries no <c>errors</c> map.
/// That request-level 400 comes only when <c>event</c> and <c>fieldName</c> are both valid; when either fails, the
/// response is the field-level 400 with their entries and any per-key <c>values</c> errors.
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
/// because the editor must be able to evaluate stored values that break current rules. A missing, <c>null</c> or
/// non-object <c>values</c> is the request-level 400 described above; a key or value that breaks either rule is
/// field-level.
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
