using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using EpiInfo.Api.Errors;
using EpiInfo.Api.Hosting;
using EpiInfo.Domain.Records;
using EpiInfo.Domain.Search;
using EpiInfo.Domain.Values;

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
/// The endpoint reads the whole body as one JSON value through JsonRequestBody, under the JSON options of JsonSetup,
/// before it reads this type. Content that is not valid JSON is the request-level 400 "malformed JSON"; an array, a
/// string, a number or a boolean is the request-level 400 "request body must be a JSON object"; and the JSON literal
/// <c>null</c> reads as no request, which RecordService answers, once it has found the form, with the request-level
/// 400 "request body is required". An object that names a member more than once, ignoring letter case, is the
/// request-level 400 "request body names a member more than once". <see cref="SearchRequestShape"/> then checks the
/// JSON kind of every member, so a member of the wrong kind is a field-level 400 under its name instead of a
/// deserialisation failure. FieldValueJsonConverter reads each criterion <c>value</c>.
/// <c>RespectRequiredConstructorParameters</c> keeps its default <see langword="false"/>, so a missing
/// <c>criteria</c>, <c>includeDeleted</c>, <c>excludeGlobalRecordId</c> or criterion <c>field</c> binds as
/// <see langword="null"/> instead of failing deserialisation, and a missing <c>criteria</c> reaches its documented
/// 400. A criterion's <c>value</c> must be present, while <c>"value": null</c> binds as the blank criterion:
/// SearchRequestShape reports a criterion without it, and JsonSetup marks the member required, so this type never
/// binds one.
/// ScalarSchemaTransformer publishes <c>criteria[].value</c> as the <c>FieldValue</c> scalar union
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
/// <see cref="EpiInfo.Domain.Values.FieldValueKind.Boolean"/>; kind
/// <see cref="EpiInfo.Domain.Values.FieldValueKind.Null"/> always means the caller sent <c>"value": null</c>. An
/// object or array <c>value</c>, a number that is not finite, a criterion that omits <c>value</c>, a <c>field</c>
/// that is not a string, an item that is neither an object nor <c>null</c>, and a <c>criteria</c> that is not an
/// array are field-level 400s under <c>errors["criteria"]</c>, which <see cref="SearchRequestShape"/> reports before
/// the body is read as this type: by the item's index in an array of at most six items, and as one fixed message for
/// a longer array, however many of its items have a problem.
/// </para>
/// <para>
/// The interpretation per field type, trimming, the six-field limit, the bound of 32,767 UTF-16 code units on each
/// string <c>value</c>, the search-wide bounds of 6,554 alternatives other than equality and <c>IS NULL</c> and of
/// 32,767 code units of wildcard pattern text, and the rejection of a field named twice and of an unknown or
/// unsearchable field all belong to SearchCriteriaParser, each answered with a 400. A longer value is one fixed
/// message under its field's canonical name. A longer array whose items all pass the member check reaches the parser,
/// which answers it with one fixed message under <c>criteria</c> before it reads any entry, as it answers a search
/// past a search-wide bound. A missing or <c>null</c> <c>criteria</c>, or an empty array, is the request-level 400
/// "criteria must not be empty", which RecordService raises; neither the member check nor deserialisation rejects it.
/// The serializer materialises the list as a <see cref="List{T}"/>. It leaves <see cref="SearchCriterion.Field"/>
/// <see langword="null"/> for a criterion that omits <c>field</c>, because it does not enforce required constructor
/// parameters, and for <c>"field": null</c>, because it does not enforce nullable annotations; the parser rejects a
/// null name as unknown.
/// </para>
/// </param>
/// <param name="IncludeDeleted">
/// The record set searched. <see langword="null"/> or absent means <see langword="false"/>: the active set
/// <c>RecStatus = 1</c>. <see langword="true"/> selects every row, deleted records included. The selected set bounds
/// the matches and numbers each result's <c>position</c>, its <c>ROW_NUMBER()</c> by <c>UniqueKey</c> over the selected
/// set before the criteria apply, so it equals the record's navigation position in that set. Only a JSON boolean or
/// <c>null</c> is accepted: the string <c>"true"</c>, a number or any other kind is the field-level 400 under
/// <c>errors["includeDeleted"]</c>, which <see cref="SearchRequestShape"/> reports.
/// </param>
/// <param name="ExcludeGlobalRecordId">
/// The <c>GlobalRecordId</c> of the record open in the editor, which the result must not list. It is removed on the
/// server before <c>total</c> and the 200-item cap are computed. Standard Enter Find keeps a matching open record;
/// legacy dropped it after the query only on the linked-record path. <see langword="null"/> or absent excludes
/// nothing. The id is neither parsed nor validated as a GUID: it is only bound as a SQL parameter, so an id that
/// matches no record excludes nothing. Only a JSON string or <c>null</c> is accepted; any other kind is the
/// field-level 400 under <c>errors["excludeGlobalRecordId"]</c>, which <see cref="SearchRequestShape"/> reports.
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

/// <summary>
/// The member check of the Find search body. It judges the JSON kind of each member of the body object before the
/// object is read as a <see cref="SearchRequest"/>, so a well-formed body whose members have the wrong kind gets a 400
/// that names the member, never "malformed JSON".
/// </summary>
/// <remarks>
/// <para>
/// <c>RecordEndpoints</c> passes <see cref="Check"/> to <c>JsonRequestBody.ReadObjectAsync</c>, which runs it once the
/// body is a JSON object whose member names are decoded and each named once, and before RecordService runs, so a body
/// problem is answered before the form is looked up. Members are matched by the wire names of the request types,
/// ignoring letter case as binding does; any other member is skipped, as binding skips it.
/// </para>
/// <para>The rules, each answered with a fixed message:</para>
/// <list type="bullet">
/// <item><description>
/// <c>includeDeleted</c> is absent, <c>null</c>, <c>true</c> or <c>false</c>; anything else is
/// <see cref="IncludeDeletedMessage"/> under <c>errors["includeDeleted"]</c>.
/// </description></item>
/// <item><description>
/// <c>excludeGlobalRecordId</c> is absent, <c>null</c> or a string; anything else is
/// <see cref="ExcludeGlobalRecordIdMessage"/> under <c>errors["excludeGlobalRecordId"]</c>.
/// </description></item>
/// <item><description>
/// <c>criteria</c> is an array, or absent or <c>null</c>, which RecordService answers with the request-level 400
/// "criteria must not be empty"; anything else is <see cref="CriteriaMessage"/> under <c>errors["criteria"]</c>.
/// </description></item>
/// <item><description>
/// Each criterion, numbered from 0 as <c>criteria[i]</c>: <c>null</c> is left to SearchCriteriaParser, which names
/// it; any other value that is not an object is "criteria[i] must be an object with field and value."; and an object
/// that names a member more than once is the request-level 400 of
/// <see cref="ApiProblems.RequestBodyRepeatsMember"/>, returned at once. In an object, <c>field</c> is absent,
/// <c>null</c> or a string, the first two left to the parser as an unknown field, else "criteria[i] field must be a
/// string."; <c>value</c> is present, else "criteria[i] needs a value, which may be null."; it is a string, number,
/// boolean or <c>null</c>, else "criteria[i] value must be a string, number, boolean or null."; and a number is finite,
/// parsed from its literal exactly as FieldValueJsonConverter parses it, else "criteria[i] value must be a finite
/// number.".
/// </description></item>
/// <item><description>
/// A <c>criteria</c> array of more than <see cref="RecordQueryRules.MaxSearchFields"/> items has every item checked
/// by the rule above, and a repeated member is still the request-level 400 returned at once. When any item has a
/// problem, the one <see cref="OverLongCriteriaMessage"/> stands under <c>errors["criteria"]</c> in place of the
/// per-item messages. An array that long whose items all pass is left to SearchCriteriaParser, which applies the
/// field limit.
/// </description></item>
/// </list>
/// <para>
/// Every criterion message is under <c>errors["criteria"]</c>, in criterion order, because the check runs before the
/// form's metadata is read: a number that is not finite is reported by its criterion's index rather than under the
/// field's canonical name. Every error of the body is collected into one field-level 400. It holds at most two
/// messages per item of a <c>criteria</c> array of at most <see cref="RecordQueryRules.MaxSearchFields"/> items, or
/// one for a longer array, and at most one each for <c>includeDeleted</c> and <c>excludeGlobalRecordId</c>, so its
/// size has a fixed bound whatever the size of the body. Messages are fixed text and never quote a submitted value or
/// name. Strings are not decoded here, so a string that cannot be decoded still fails reading the request, which is
/// "malformed JSON". The class holds no mutable state and is safe to call from any thread.
/// </para>
/// </remarks>
internal static class SearchRequestShape
{
    /// <summary>
    /// The message under <c>errors["includeDeleted"]</c> for a value that is not a boolean or <c>null</c>.
    /// </summary>
    internal const string IncludeDeletedMessage = "includeDeleted must be true, false or null.";

    /// <summary>
    /// The message under <c>errors["excludeGlobalRecordId"]</c> for a value that is not a string or <c>null</c>.
    /// </summary>
    internal const string ExcludeGlobalRecordIdMessage = "excludeGlobalRecordId must be a string or null.";

    /// <summary>The message under <c>errors["criteria"]</c> for a <c>criteria</c> that is not an array.</summary>
    internal const string CriteriaMessage = "criteria must be an array of {field, value} objects.";

    /// <summary>
    /// The one message under <c>errors["criteria"]</c> for a <c>criteria</c> array of more than
    /// <see cref="RecordQueryRules.MaxSearchFields"/> items of which at least one has a problem, in place of the
    /// per-item messages, so the 400 does not grow with the array.
    /// </summary>
    internal static readonly string OverLongCriteriaMessage = string.Create(
        CultureInfo.InvariantCulture,
        $"criteria must be an array of at most {RecordQueryRules.MaxSearchFields} {{field, value}} objects.");

    // What follows "criteria[i] " in the message of each criterion problem.
    private const string CriterionNotObjectProblem = "must be an object with field and value.";

    private const string FieldNotStringProblem = "field must be a string.";

    private const string ValueMissingProblem = "needs a value, which may be null.";

    private const string ValueNotScalarProblem = "value must be a string, number, boolean or null.";

    private const string ValueNotFiniteProblem = "value must be a finite number.";

    // The wire names JsonSetup's camelCase naming policy gives the members, so the check and binding agree on them.
    private static readonly string CriteriaName =
        JsonNamingPolicy.CamelCase.ConvertName(nameof(SearchRequest.Criteria));

    private static readonly string IncludeDeletedName =
        JsonNamingPolicy.CamelCase.ConvertName(nameof(SearchRequest.IncludeDeleted));

    private static readonly string ExcludeGlobalRecordIdName =
        JsonNamingPolicy.CamelCase.ConvertName(nameof(SearchRequest.ExcludeGlobalRecordId));

    private static readonly string FieldName = JsonNamingPolicy.CamelCase.ConvertName(nameof(SearchCriterion.Field));

    private static readonly string ValueName = JsonNamingPolicy.CamelCase.ConvertName(nameof(SearchCriterion.Value));

    /// <summary>Checks the JSON kind of each member of a search body object.</summary>
    /// <param name="body">The body, a JSON object.</param>
    /// <returns>
    /// <see langword="null"/> when every member can be read as a <see cref="SearchRequest"/>; otherwise the
    /// field-level 400 holding every error the remarks list, or the request-level 400 of a criterion object that names
    /// a member more than once.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="body"/> is not a JSON object.</exception>
    /// <exception cref="BadHttpRequestException">
    /// With status 400 and a <see cref="JsonException"/> as its inner exception, which the exception handler answers
    /// with "malformed JSON", when a member name cannot be decoded.
    /// </exception>
    internal static IResult? Check(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("A search body is a JSON object.", nameof(body));
        }

        var errors = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var member in body.EnumerateObject())
        {
            var name = JsonRequestBody.MemberName(member);
            var value = member.Value;
            if (Names(name, IncludeDeletedName))
            {
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null))
                {
                    AddError(errors, IncludeDeletedName, IncludeDeletedMessage);
                }
            }
            else if (Names(name, ExcludeGlobalRecordIdName))
            {
                if (value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                {
                    AddError(errors, ExcludeGlobalRecordIdName, ExcludeGlobalRecordIdMessage);
                }
            }
            else if (Names(name, CriteriaName) && CheckCriteria(value, errors) is { } problem)
            {
                return problem;
            }
        }

        if (errors.Count == 0)
        {
            return null;
        }

        var frozen = new Dictionary<string, string[]>(errors.Count, StringComparer.Ordinal);
        foreach (var (key, messages) in errors)
        {
            frozen.Add(key, [.. messages]);
        }

        return ApiProblems.ValidationFailed(frozen);
    }

    // Adds the errors of the criteria member to errors, or returns the request-level 400 of a criterion object that
    // names a member more than once. Every item is checked whatever the array's length, but past
    // RecordQueryRules.MaxSearchFields items only whether one has a problem is kept, so the errors stay bounded.
    private static IResult? CheckCriteria(JsonElement criteria, Dictionary<string, List<string>> errors)
    {
        if (criteria.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (criteria.ValueKind != JsonValueKind.Array)
        {
            AddError(errors, SearchCriteriaParser.CriteriaKey, CriteriaMessage);
            return null;
        }

        var itemized = criteria.GetArrayLength() <= RecordQueryRules.MaxSearchFields;
        var misshapen = false;

        // The problems of the current item; an object has at most two, since each of its members is named once.
        var problems = new List<string>(2);
        var index = 0;
        foreach (var criterion in criteria.EnumerateArray())
        {
            problems.Clear();
            switch (criterion.ValueKind)
            {
                // SearchCriteriaParser reports a null criterion by its index.
                case JsonValueKind.Null:
                    break;

                case JsonValueKind.Object:
                    if (JsonRequestBody.NamesAMemberMoreThanOnce(criterion))
                    {
                        return ApiProblems.RequestBodyRepeatsMember();
                    }

                    CheckCriterion(criterion, problems);
                    break;

                default:
                    problems.Add(CriterionNotObjectProblem);
                    break;
            }

            if (itemized)
            {
                foreach (var problem in problems)
                {
                    AddCriterionError(errors, index, problem);
                }
            }
            else
            {
                misshapen |= problems.Count > 0;
            }

            index++;
        }

        if (misshapen)
        {
            AddError(errors, SearchCriteriaParser.CriteriaKey, OverLongCriteriaMessage);
        }

        return null;
    }

    // Adds the problems of one criterion object to problems, in member order, each the text after "criteria[i] ".
    private static void CheckCriterion(JsonElement criterion, List<string> problems)
    {
        var hasValue = false;
        foreach (var member in criterion.EnumerateObject())
        {
            var name = JsonRequestBody.MemberName(member);
            var value = member.Value;
            if (Names(name, FieldName))
            {
                // A null or absent field reaches the parser, which rejects it as an unknown field.
                if (value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                {
                    problems.Add(FieldNotStringProblem);
                }
            }
            else if (Names(name, ValueName))
            {
                hasValue = true;
                if (value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    problems.Add(ValueNotScalarProblem);
                }
                else if (value.ValueKind == JsonValueKind.Number
                         && !FieldValueCoercion.TryParseNumber(JsonMarshal.GetRawUtf8Value(value), out _))
                {
                    // The literal parse FieldValueJsonConverter applies, so a number it would refuse, such as 1e400,
                    // is reported here instead of failing deserialisation.
                    problems.Add(ValueNotFiniteProblem);
                }
            }
        }

        if (!hasValue)
        {
            problems.Add(ValueMissingProblem);
        }
    }

    // Binding matches member names ignoring case under the web defaults, so the check does too.
    private static bool Names(string name, string wireName) =>
        string.Equals(name, wireName, StringComparison.OrdinalIgnoreCase);

    // Adds "criteria[i] " and problem under the criteria key, the index written with the invariant culture.
    private static void AddCriterionError(Dictionary<string, List<string>> errors, int index, string problem) =>
        AddError(
            errors,
            SearchCriteriaParser.CriteriaKey,
            string.Create(CultureInfo.InvariantCulture, $"criteria[{index}] {problem}"));

    private static void AddError(Dictionary<string, List<string>> errors, string key, string message)
    {
        if (!errors.TryGetValue(key, out var messages))
        {
            messages = [];
            errors.Add(key, messages);
        }

        messages.Add(message);
    }
}
