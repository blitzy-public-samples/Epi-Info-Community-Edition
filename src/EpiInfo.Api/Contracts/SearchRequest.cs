using EpiInfo.Domain.Records;

namespace EpiInfo.Api.Contracts;

/// <summary>
/// Request body of <c>POST /api/forms/{viewId}/records/search</c>, the Find record search. On the wire it is
/// <c>{"criteria":[{"field":"LastName","value":"Sm*"}],"includeDeleted":false,</c>
/// <c>"excludeGlobalRecordId":"&lt;id&gt;"}</c>.
/// </summary>
/// <remarks>
/// <para>
/// The type only carries what the caller sent. It holds no normalisation or validation, and no binding or schema
/// attribute. The legacy Find semantics are applied by SearchCriteriaParser alone; the selected set, the
/// exclusion, the <c>UniqueKey</c> order and the 200-item cap are applied by RecordService and the search SQL builder.
/// </para>
/// <para>
/// The body carries criteria and set selection only. It has no member for save stamps, <c>RecStatus</c>,
/// <c>UniqueKey</c> or any other system column, and the serializer skips unknown top-level members.
/// <c>GlobalRecordId</c> and <c>RecStatus</c> can appear only as the <c>field</c> of a criterion, where the parser
/// treats them as the searchable system fields. No member carries SQL text: every criterion value and the excluded id
/// reach SQL Server only as parameters.
/// </para>
/// <para>
/// Binding relies on the JSON options of JsonSetup. FieldValueJsonConverter reads each criterion <c>value</c>, and
/// <c>RespectRequiredConstructorParameters</c> and <c>RespectNullableAnnotations</c> keep their default
/// <see langword="false"/>, so a missing member binds as <see langword="null"/> instead of failing binding, and a
/// missing <c>criteria</c> reaches its documented 400. ScalarSchemaTransformer publishes <c>criteria[].value</c> as the
/// <c>FieldValue</c> scalar union
/// <c>string | number | boolean | null</c>, so no member of the <see cref="EpiInfo.Domain.Values.FieldValue"/> struct
/// appears in the OpenAPI document.
/// </para>
/// </remarks>
/// <param name="Criteria">
/// <para>
/// One <see cref="SearchCriterion"/> per searched field, at most six, combined with AND. A string <c>value</c> may use
/// the legacy Find syntax: alternatives separated by exactly <c>" or "</c>, <c>" OR "</c>, <c>" Or "</c> or
/// <c>" oR "</c>, combined with OR; a leading <c>&lt;=</c>, <c>&gt;=</c>, <c>&lt;&gt;</c>, <c>&gt;</c> or <c>&lt;</c>;
/// and the wildcards <c>*</c> and <c>%</c>. Each <c>value</c> is the raw JSON scalar, read as a field value of kind
/// <see cref="EpiInfo.Domain.Values.FieldValueKind.Null"/>, <see cref="EpiInfo.Domain.Values.FieldValueKind.Text"/>,
/// <see cref="EpiInfo.Domain.Values.FieldValueKind.Number"/> or
/// <see cref="EpiInfo.Domain.Values.FieldValueKind.Boolean"/>; an object or array <c>value</c> fails binding with a
/// 400.
/// </para>
/// <para>
/// The interpretation per field type, trimming, the six-field limit, the rejection of a field named twice and of an
/// unknown or unsearchable field all belong to SearchCriteriaParser, each answered with a 400. A missing or
/// <c>null</c> <c>criteria</c>, or an empty array, is the request-level 400 "<c>criteria</c> must not be empty", which
/// RecordService raises; binding never rejects it. The serializer materialises the list as a
/// <see cref="List{T}"/>, and leaves <see cref="SearchCriterion.Field"/> <see langword="null"/> for a criterion that
/// omits <c>field</c>, because it does not enforce nullable annotations; the parser rejects that name as unknown.
/// </para>
/// </param>
/// <param name="IncludeDeleted">
/// The record set searched. <see langword="null"/> or absent means <see langword="false"/>: the active set
/// <c>RecStatus = 1</c>. <see langword="true"/> selects every row, deleted records included. The selected set bounds
/// the matches and numbers each result's <c>position</c>, its <c>ROW_NUMBER()</c> by <c>UniqueKey</c> over the selected
/// set before the criteria apply, so it equals the record's navigation position in that set. Only a JSON boolean binds:
/// the string <c>"true"</c>, a number or an object fails binding, and ApiExceptionHandler answers with a request-level
/// 400.
/// </param>
/// <param name="ExcludeGlobalRecordId">
/// The <c>GlobalRecordId</c> of the record open in the editor, which the result must not list. It is removed on the
/// server before <c>total</c> and the 200-item cap are computed, where legacy dropped it from the result after the
/// query. <see langword="null"/> or absent excludes nothing. The id is neither parsed nor validated as a GUID: it is
/// only bound as a SQL parameter, so an id that matches no record excludes nothing.
/// </param>
public sealed record SearchRequest(
    IReadOnlyList<SearchCriterion>? Criteria,
    // The null defaults change no binding, since a missing member binds as null either way. They mark both members
    // optional in the generated OpenAPI schema, which otherwise lists every positional parameter as required.
    bool? IncludeDeleted = null,
    string? ExcludeGlobalRecordId = null)
{
    /// <summary>
    /// Returns the type name only. The compiler-generated record <c>ToString</c> would print the criteria, whose values
    /// are field data that must never reach a log line or an error body.
    /// </summary>
    /// <returns><c>SearchRequest</c>.</returns>
    public override string ToString() => nameof(SearchRequest);
}
