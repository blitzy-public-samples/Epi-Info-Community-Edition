using System.Text.Json;

namespace EpiInfo.Api.Contracts;

/// <summary>
/// Request body of <c>POST /api/forms/{viewId}/records</c> (create) and
/// <c>PUT /api/forms/{viewId}/records/{globalRecordId}</c> (update). On the wire it is
/// <c>{ "values": { "&lt;fieldName&gt;": string | number | boolean | null, ... } }</c>.
/// </summary>
/// <remarks>
/// <para>
/// The body carries field values only. Save stamps, <c>RecStatus</c>, <c>UniqueKey</c>, <c>GlobalRecordId</c> and
/// every other system column have no member here, and the serializer skips unknown top-level members, so a caller
/// cannot set any of them. Inside <c>values</c>, a system-column name is an unknown field and a read-only field is
/// not writable; the record validator rejects both with a 400, not this type.
/// </para>
/// <para>
/// The type holds no validation and no binding or schema attributes. <c>Endpoints/RecordEndpoints.cs</c> decides how
/// the body binds, and <c>OpenApi/ScalarSchemaTransformer.cs</c> documents <c>values</c> as an object whose
/// <c>additionalProperties</c> is <c>FieldValue</c>, so no <see cref="JsonElement"/> shape reaches the OpenAPI
/// document.
/// </para>
/// <para>
/// A missing <c>values</c> binds as <see langword="null"/> because the serializer option
/// <c>RespectRequiredConstructorParameters</c> keeps its default <see langword="false"/>. Turned on, it would fail
/// binding on a missing <c>values</c> instead of letting the documented 400 <c>detail</c> answer it. That option
/// concerns missing parameters only: an explicit <c>null</c> binds as <see langword="null"/> either way. Nor does
/// <c>RespectNullableAnnotations</c> reject it, because that option applies to non-nullable reference members and
/// <c>JsonElement?</c> is a nullable value type.
/// </para>
/// <para>
/// A missing body, or an empty one sent as <c>application/json</c>, never reaches this type:
/// <c>JsonContentTypeMiddleware</c> answers it with the request-level 400 "request body is required", and a body
/// that is the JSON literal <c>null</c> binds no request and gets the same 400. Malformed or truncated JSON, and a
/// body that is a JSON array, string, number or boolean rather than an object, fail binding with a
/// <c>BadHttpRequestException</c>, which <c>ApiExceptionHandler</c> maps to the request-level 400 "malformed JSON".
/// </para>
/// </remarks>
/// <param name="Values">
/// <para>
/// The submitted field values keyed by field name, kept as the raw JSON the caller sent. A typed nullable dictionary
/// would also bind a missing or <c>null</c> <c>values</c> as <see langword="null"/> and keep keys that differ only by
/// letter case, but binding would fail on a non-object <c>values</c> and on a member value that is an array or an
/// object, so those requests would get "malformed JSON" instead of their documented 400, and a key repeated exactly
/// would collapse into its last occurrence. The raw element lets every malformed shape reach the service and its
/// documented response, and keeps every property occurrence, exact repeats included.
/// </para>
/// <list type="bullet">
/// <item><description>
/// A missing <c>values</c> and an explicit <c>null</c> are both <see langword="null"/>, never an element whose
/// <see cref="JsonElement.ValueKind"/> is <see cref="JsonValueKind.Null"/>, and an array, string, number or boolean
/// is an element of that kind. <c>RecordService</c> answers every one of them alike, with the request-level 400
/// whose <c>detail</c> is <see cref="EpiInfo.Domain.Validation.ValidationMessages.ValuesMustBeObject"/> and which
/// carries no <c>errors</c> map.
/// </description></item>
/// <item><description>
/// Keys that differ only by letter case, such as <c>lastName</c> and <c>LastName</c>, both survive, as does a key
/// repeated exactly. <c>ValuesReader</c> reads every property occurrence in document order, and the record validator
/// rejects a field named more than once, exactly or differing only by case, with a field-level 400 keyed by its
/// canonical name and carrying <see cref="EpiInfo.Domain.Validation.ValidationMessages.DuplicateField"/>.
/// </description></item>
/// <item><description>
/// A field absent from the object is left unchanged by <c>PUT</c> and stored as <c>null</c> by <c>POST</c>. An empty
/// string on a text-family field is normalised to <c>null</c> by the validator, not here.
/// </description></item>
/// </list>
/// <para>
/// An element produced by <see cref="JsonSerializer"/> owns a copy of its data, so it stays valid after the request
/// body stream has been read and needs no <see cref="JsonElement.Clone"/>.
/// </para>
/// </param>
public sealed record RecordWriteRequest(JsonElement? Values)
{
    /// <summary>
    /// Returns the type name only. The compiler-generated record <c>ToString</c> would print the submitted values, and
    /// a field value must never reach a log line or an error body.
    /// </summary>
    public override string ToString() => nameof(RecordWriteRequest);
}
