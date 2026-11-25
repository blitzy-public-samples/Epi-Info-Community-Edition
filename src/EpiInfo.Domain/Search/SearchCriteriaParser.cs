using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using EpiInfo.Domain.Forms;
using EpiInfo.Domain.Metadata;
using EpiInfo.Domain.Records;
using EpiInfo.Domain.Values;

namespace EpiInfo.Domain.Search;

/// <summary>
/// The outcome of <see cref="SearchCriteriaParser.Parse"/>: the parsed conditions, or the 400 the record service
/// answers with. Exactly one of three states holds: success with at least one condition, a request-level failure
/// (<see cref="RequestError"/>, a plain ProblemDetails <c>detail</c> with no <c>errors</c> map), or a field-level
/// failure (<see cref="FieldErrors"/>, the <c>errors</c> map of an <c>HttpValidationProblemDetails</c>).
/// </summary>
/// <remarks>
/// Instances are immutable, so one can be read from any thread. No message carries a submitted value, as the
/// redaction policy requires of every error body.
/// </remarks>
public sealed class SearchParseResult
{
    private static readonly IReadOnlyDictionary<string, string[]> NoErrors =
        new ReadOnlyDictionary<string, string[]>(new Dictionary<string, string[]>(StringComparer.Ordinal));

    private SearchParseResult(
        IReadOnlyList<SearchCondition> conditions,
        string? requestError,
        IReadOnlyDictionary<string, string[]> fieldErrors)
    {
        Conditions = conditions;
        RequestError = requestError;
        FieldErrors = fieldErrors;
    }

    /// <summary>Whether the criteria parsed, so <see cref="Conditions"/> holds the search.</summary>
    public bool IsSuccess => RequestError is null && FieldErrors.Count == 0;

    /// <summary>
    /// One condition per searched field, in the order the criteria named them, each holding its alternatives in
    /// the order they were written; empty on failure.
    /// </summary>
    public IReadOnlyList<SearchCondition> Conditions { get; }

    /// <summary>
    /// The <c>detail</c> of a request-level 400, such as <see cref="SearchCriteriaParser.EmptyCriteriaMessage"/>;
    /// <see langword="null"/> otherwise.
    /// </summary>
    public string? RequestError { get; }

    /// <summary>
    /// The <c>errors</c> map of a field-level 400, keyed ordinally by the canonical field name, or by
    /// <see cref="SearchCriteriaParser.CriteriaKey"/> for errors about the criteria list itself; empty otherwise.
    /// </summary>
    public IReadOnlyDictionary<string, string[]> FieldErrors { get; }

    /// <summary>Creates a successful result.</summary>
    /// <param name="conditions">The parsed conditions; at least one, none null.</param>
    /// <returns>The successful result, holding its own copy of <paramref name="conditions"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="conditions"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="conditions"/> is empty or holds a null entry.</exception>
    public static SearchParseResult Success(IReadOnlyList<SearchCondition> conditions)
    {
        ArgumentNullException.ThrowIfNull(conditions);

        // A search without a condition is the request-level failure, never a search of every record.
        SearchCondition[] copy = [.. conditions];
        if (copy.Length == 0)
        {
            throw new ArgumentException("A successful search needs at least one condition.", nameof(conditions));
        }

        if (Array.Exists(copy, condition => condition is null))
        {
            throw new ArgumentException("A search condition must not be null.", nameof(conditions));
        }

        return new SearchParseResult(Array.AsReadOnly(copy), null, NoErrors);
    }

    /// <summary>Creates a request-level failure, answered as a 400 ProblemDetails with no <c>errors</c> map.</summary>
    /// <param name="detail">The fixed problem text, which must carry no submitted value.</param>
    /// <returns>The failed result.</returns>
    /// <exception cref="ArgumentException"><paramref name="detail"/> is null, empty or whitespace.</exception>
    public static SearchParseResult RequestFailure(string detail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        return new SearchParseResult([], detail, NoErrors);
    }

    /// <summary>Creates a field-level failure, answered as a 400 with an <c>errors</c> map.</summary>
    /// <param name="errors">
    /// The messages by key: at least one key, each with at least one non-blank message that carries no submitted
    /// value.
    /// </param>
    /// <returns>The failed result, holding its own ordinal copy of <paramref name="errors"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="errors"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="errors"/> is empty, or a key holds no message or a blank one.
    /// </exception>
    public static SearchParseResult FieldFailure(IReadOnlyDictionary<string, string[]> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);

        var copy = new Dictionary<string, string[]>(errors.Count, StringComparer.Ordinal);
        foreach (var (key, messages) in errors)
        {
            if (messages is not { Length: > 0 } || Array.Exists(messages, string.IsNullOrWhiteSpace))
            {
                throw new ArgumentException("Each error key needs at least one non-blank message.", nameof(errors));
            }

            copy.Add(key, [.. messages]);
        }

        if (copy.Count == 0)
        {
            throw new ArgumentException("A field-level failure needs at least one error.", nameof(errors));
        }

        return new SearchParseResult([], null, new ReadOnlyDictionary<string, string[]>(copy));
    }
}

/// <summary>
/// The one owner of search criteria. It turns the <c>criteria</c> of <c>POST /api/forms/{viewId}/records/search</c>
/// into <see cref="SearchCondition"/>s with the semantics of the legacy Find dialog, which win over the request's
/// "contains" wording for this endpoint. The record service calls it, and the data layer's search builder renders
/// its output as parameterised SQL.
/// </summary>
/// <remarks>
/// <para>
/// <b>Kept from legacy</b> (<c>Epi.Windows.Enter/Dialogs/FindRecordDialog.cs</c>, lines 103-120, 293-374, 404-646
/// and 708-771; <c>Epi.Core/Data/Services/CollectedDataProvider.cs</c>, lines 1973-2249): the searchable fields
/// (data fields plus the system fields <c>GlobalRecordId</c> and <c>RecStatus</c>); at most
/// <see cref="RecordQueryRules.MaxSearchFields"/> fields; the trimmed value split on exactly <c>" or "</c>,
/// <c>" OR "</c>, <c>" Or "</c> or <c>" oR "</c> into alternatives that are not trimmed again; the leading
/// <c>&lt;=</c>, <c>&gt;=</c>, <c>&lt;&gt;</c>, <c>&gt;</c> or <c>&lt;</c>, tested in that order; the per-type use of
/// that prefix; the wildcard and 255-character multiline comparisons; and the Phone Number path without wildcards.
/// Alternatives of one field combine with OR and conditions with AND.
/// </para>
/// <para>
/// <b>Departures from legacy</b>, each recorded in the "Search semantics" section of <c>docs/parity-notes.md</c>:
/// <list type="bullet">
/// <item><description>Every value is bound as a parameter, so single quotes stay in wildcard patterns, where legacy
/// removed them from the literal it inlined.</description></item>
/// <item><description>Dates are accepted only as <c>YYYY-MM-DD</c> within the <c>datetime</c> range, where legacy
/// parsed them with the current culture.</description></item>
/// <item><description>A blank option criterion is a clean 400, where legacy failed with a null
/// dereference.</description></item>
/// <item><description>A checkbox also accepts a JSON boolean, beside the legacy <c>Yes</c>, <c>No</c>, <c>(+)</c>
/// and <c>(-)</c> strings.</description></item>
/// <item><description>Unsupported fields, Yes/No included, are not searchable, because they are not data fields of
/// the form.</description></item>
/// <item><description>A field named twice is a 400, because the legacy dialog cannot select a field
/// twice.</description></item>
/// <item><description><c>GlobalRecordId</c> and <c>RecStatus</c> are base-table conditions, where the legacy join
/// on their unbound page failed before any result.</description></item>
/// </list>
/// </para>
/// <para>
/// The selected set, <c>includeDeleted</c>, <c>excludeGlobalRecordId</c>, the <c>UniqueKey</c> order and the
/// 200-item cap are not applied here: the record service, the record query rules and the search builder own them.
/// </para>
/// <para>
/// Every comparison is ordinal and every number or date is read with the invariant culture, through the field-value
/// coercion helpers. No error message carries a submitted value or the caller's raw field name, as the redaction
/// policy requires. The class holds no mutable state, so it is safe to call from any thread.
/// </para>
/// </remarks>
public static class SearchCriteriaParser
{
    /// <summary>The <c>errors</c> key of every error about the criteria list rather than one field's value.</summary>
    public const string CriteriaKey = "criteria";

    /// <summary>The canonical name of the base table's <c>GlobalRecordId</c> system field.</summary>
    public const string GlobalRecordIdFieldName = "GlobalRecordId";

    /// <summary>The canonical name of the base table's <c>RecStatus</c> system field.</summary>
    public const string RecStatusFieldName = "RecStatus";

    /// <summary>The request-level 400 <c>detail</c> for a missing, null or empty <c>criteria</c>.</summary>
    public const string EmptyCriteriaMessage = "`criteria` must not be empty";

    // The legacy dialog's alternative separators, matched ordinally by string.Split.
    private static readonly string[] OrSeparators = [" or ", " OR ", " Or ", " oR "];

    // Two-character prefixes come first, so "<=" is never read as "<" followed by "=".
    private static readonly (string Text, SearchOperator Operator)[] Prefixes =
    [
        ("<=", SearchOperator.LessOrEqual),
        (">=", SearchOperator.GreaterOrEqual),
        ("<>", SearchOperator.NotEqual),
        (">", SearchOperator.Greater),
        ("<", SearchOperator.Less),
    ];

    private static readonly char[] WildcardCharacters = ['%', '*', '?'];

    private static readonly string FieldLimitMessage = string.Create(
        CultureInfo.InvariantCulture,
        $"At most {RecordQueryRules.MaxSearchFields} fields can be searched at once.");

    // Legacy's default representations of a checked and an unchecked box; the new application has no legacy
    // configuration file that could change them.
    private const string YesText = "Yes";
    private const string NoText = "No";
    private const string CheckedLiteral = "(+)";
    private const string UncheckedLiteral = "(-)";

    private const string AlternativesHint = "with alternatives separated by \" or \"";
    private const string PrefixHint = "each optionally led by <=, >=, <>, > or <";

    private enum CriterionKind
    {
        ComparedText,
        PatternText,
        Number,
        Option,
        Date,
        Checkbox,
        RecStatus,
    }

    private enum ValueError
    {
        None,
        Blank,
        Invalid,
    }

    private enum TextReading
    {
        Alternatives,
        Blank,
        NotText,
    }

    /// <summary>
    /// Parses the criteria of one search against the data fields of the searched form.
    /// </summary>
    /// <param name="dataFields">
    /// The form's data fields, from <see cref="FormDefinitionFactory.GetDataFields"/>. Labels, groups, unsupported
    /// fields and system rows are absent from it, so they are never searchable.
    /// </param>
    /// <param name="criteria">The criteria as received, in request order; any entry may be malformed.</param>
    /// <returns>
    /// <list type="bullet">
    /// <item><description>A request-level failure with <see cref="EmptyCriteriaMessage"/> when
    /// <paramref name="criteria"/> is null or empty.</description></item>
    /// <item><description>A field-level failure holding every error found in one pass: under
    /// <see cref="CriteriaKey"/>, one message per null entry, unknown or unsearchable name and repeated field, and
    /// one for exceeding <see cref="RecordQueryRules.MaxSearchFields"/> distinct fields, whose excess criteria are not
    /// parsed; and under each field's canonical name, the error in its value.</description></item>
    /// <item><description>Otherwise a success with one condition per criterion, in criteria order.</description></item>
    /// </list>
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="dataFields"/> is null. Bad input never throws: it is reported in the result.
    /// </exception>
    public static SearchParseResult Parse(DataFieldSet dataFields, IReadOnlyList<SearchCriterion>? criteria)
    {
        ArgumentNullException.ThrowIfNull(dataFields);

        if (criteria is null || criteria.Count == 0)
        {
            return SearchParseResult.RequestFailure(EmptyCriteriaMessage);
        }

        var errors = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var conditions = new List<SearchCondition>(Math.Min(criteria.Count, RecordQueryRules.MaxSearchFields));
        var namedFields = new HashSet<string>(StringComparer.Ordinal);
        var limitReported = false;

        for (var index = 0; index < criteria.Count; index++)
        {
            // The wire reader can yield a null entry for a JSON null in the array, whatever the annotation says.
            if (criteria[index] is not { } criterion)
            {
                AddError(errors, CriteriaKey, CriteriaMessage(index, "must name a field and a value."));
                continue;
            }

            if (!TryResolve(dataFields, criterion.Field, out var field))
            {
                AddError(errors, CriteriaKey, CriteriaMessage(index, "does not name a searchable field of this form."));
                continue;
            }

            if (!namedFields.Add(field.Name))
            {
                AddError(errors, CriteriaKey, $"{field.Name} is named more than once.");
                continue;
            }

            if (namedFields.Count > RecordQueryRules.MaxSearchFields)
            {
                if (!limitReported)
                {
                    AddError(errors, CriteriaKey, FieldLimitMessage);
                    limitReported = true;
                }

                continue;
            }

            var error = ParseValue(field, criterion.Value, out var alternatives);
            switch (error)
            {
                case ValueError.None:
                    conditions.Add(new SearchCondition(field.Name, field.Target, field.FieldType, alternatives));
                    break;
                case ValueError.Blank:
                    AddError(errors, field.Name, $"{field.Name} needs a search value.");
                    break;
                default:
                    AddError(errors, field.Name, InvalidValueMessage(field));
                    break;
            }
        }

        if (errors.Count > 0)
        {
            return SearchParseResult.FieldFailure(
                errors.ToDictionary(entry => entry.Key, entry => entry.Value.ToArray(), StringComparer.Ordinal));
        }

        return SearchParseResult.Success(conditions);
    }


    // Resolves a criterion's name to a searchable column: a data field first, then the two system fields. The
    // defensive branch rejects a data field of a kind that holds no value, which the data-field set never contains.
    private static bool TryResolve(DataFieldSet dataFields, string? name, out ResolvedField field)
    {
        field = default;
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        if (dataFields.TryGet(name, out var dataField))
        {
            // Phone Number maps to the text type, so it is told apart by its metadata type name first.
            CriterionKind? kind = FieldTypeMapper.IsPhoneNumber(dataField.Field)
                ? CriterionKind.ComparedText
                : FieldTypeMapper.IsTextFamily(dataField.FieldType)
                    ? CriterionKind.PatternText
                    : dataField.FieldType switch
                    {
                        FieldType.Number => CriterionKind.Number,
                        FieldType.Option => CriterionKind.Option,
                        FieldType.Date => CriterionKind.Date,
                        FieldType.Checkbox => CriterionKind.Checkbox,
                        _ => null,
                    };

            if (kind is not { } dataKind)
            {
                return false;
            }

            field = new ResolvedField(
                dataField.Name, SearchTarget.PageField, dataField.FieldType, dataField.Column, dataKind);
            return true;
        }

        if (string.Equals(name, GlobalRecordIdFieldName, StringComparison.OrdinalIgnoreCase))
        {
            field = new ResolvedField(
                GlobalRecordIdFieldName, SearchTarget.BaseGlobalRecordId, null, null, CriterionKind.ComparedText);
            return true;
        }

        if (string.Equals(name, RecStatusFieldName, StringComparison.OrdinalIgnoreCase))
        {
            field = new ResolvedField(
                RecStatusFieldName, SearchTarget.BaseRecStatus, null, null, CriterionKind.RecStatus);
            return true;
        }

        return false;
    }

    private static ValueError ParseValue(ResolvedField field, FieldValue value, out SearchAlternative[] alternatives) =>
        field.Kind switch
        {
            CriterionKind.ComparedText => ParseComparedText(value, out alternatives),
            CriterionKind.PatternText => ParsePatternText(
                value, field.FieldType == FieldType.Multiline, out alternatives),
            CriterionKind.Number => ParseNumber(value, out alternatives),
            CriterionKind.Option => ParseOption(value, out alternatives),
            CriterionKind.Date => ParseDate(value, field.Column, out alternatives),
            CriterionKind.Checkbox => ParseCheckbox(value, out alternatives),
            CriterionKind.RecStatus => ParseRecStatus(value, out alternatives),
            _ => throw new UnreachableException("Every criterion kind has a value parser."),
        };

    // Phone Number and GlobalRecordId: the prefix is the operator on the rest, and a bare prefix matches a column
    // holding no value. There is no wildcard path, so '*', '%' and '?' compare as themselves.
    private static ValueError ParseComparedText(FieldValue value, out SearchAlternative[] alternatives)
    {
        alternatives = [];
        var reading = ReadAlternatives(value, out var texts);
        if (reading != TextReading.Alternatives)
        {
            return reading == TextReading.Blank ? ValueError.Blank : ValueError.Invalid;
        }

        var parsed = new SearchAlternative[texts.Length];
        for (var i = 0; i < texts.Length; i++)
        {
            var (comparison, rest) = SplitPrefix(texts[i]);
            parsed[i] = rest.Length == 0
                ? SearchAlternative.Null
                : new SearchAlternative(comparison, FieldValue.FromText(rest));
        }

        alternatives = parsed;
        return ValueError.None;
    }

    // Text, multiline and legal values other than Phone Number: the prefix is stripped and ignored. The wildcard test
    // precedes the multiline test, as in legacy, so a multiline pattern is a pattern match over the whole column.
    private static ValueError ParsePatternText(FieldValue value, bool multiline, out SearchAlternative[] alternatives)
    {
        alternatives = [];
        var reading = ReadAlternatives(value, out var texts);
        if (reading != TextReading.Alternatives)
        {
            return reading == TextReading.Blank ? ValueError.Blank : ValueError.Invalid;
        }

        var parsed = new SearchAlternative[texts.Length];
        for (var i = 0; i < texts.Length; i++)
        {
            var (_, rest) = SplitPrefix(texts[i]);
            if (rest.Length == 0)
            {
                parsed[i] = SearchAlternative.Null;
            }
            else if (rest.IndexOfAny(WildcardCharacters) >= 0)
            {
                // Only '*' is translated. '?', '_', '[...]' and single quotes stay as written; the pattern is a
                // parameter, so a quote cannot end it.
                parsed[i] = new SearchAlternative(SearchOperator.Like, FieldValue.FromText(rest.Replace('*', '%')));
            }
            else
            {
                parsed[i] = new SearchAlternative(
                    multiline ? SearchOperator.PrefixEqual255 : SearchOperator.Equal, FieldValue.FromText(rest));
            }
        }

        alternatives = parsed;
        return ValueError.None;
    }

    // Number: blank matches a column holding no value; otherwise each alternative is the prefix operator on an
    // invariant number, so a bare prefix, which leaves no number, is invalid.
    private static ValueError ParseNumber(FieldValue value, out SearchAlternative[] alternatives)
    {
        alternatives = [];
        if (value.Kind == FieldValueKind.Number)
        {
            alternatives = [new SearchAlternative(SearchOperator.Equal, value)];
            return ValueError.None;
        }

        var reading = ReadAlternatives(value, out var texts);
        if (reading == TextReading.Blank)
        {
            alternatives = [SearchAlternative.Null];
            return ValueError.None;
        }

        if (reading == TextReading.NotText)
        {
            return ValueError.Invalid;
        }

        var parsed = new SearchAlternative[texts.Length];
        for (var i = 0; i < texts.Length; i++)
        {
            var (comparison, rest) = SplitPrefix(texts[i]);

            // The exact parse keeps a whole number exact and never rounds a fraction into one.
            if (!FieldValueCoercion.TryParseNumber(rest, out var number))
            {
                return ValueError.Invalid;
            }

            parsed[i] = new SearchAlternative(comparison, number);
        }

        alternatives = parsed;
        return ValueError.None;
    }

    // Option: an integer index of 0 or more, compared for equality with any prefix ignored. No option-count check is
    // made: an index with no option matches nothing. Blank is rejected (a target departure from legacy's failure).
    private static ValueError ParseOption(FieldValue value, out SearchAlternative[] alternatives)
    {
        alternatives = [];
        if (value.Kind == FieldValueKind.Number)
        {
            var index = FieldValueCoercion.Coerce(value, FieldValueKind.Index);
            if (!index.Succeeded)
            {
                return ValueError.Invalid;
            }

            alternatives = [new SearchAlternative(SearchOperator.Equal, index.Value)];
            return ValueError.None;
        }

        var reading = ReadAlternatives(value, out var texts);
        if (reading != TextReading.Alternatives)
        {
            return reading == TextReading.Blank ? ValueError.Blank : ValueError.Invalid;
        }

        var parsed = new SearchAlternative[texts.Length];
        for (var i = 0; i < texts.Length; i++)
        {
            var (_, rest) = SplitPrefix(texts[i]);
            if (!FieldValueCoercion.TryParseInvariantInteger(rest, out var optionIndex) || optionIndex < 0)
            {
                return ValueError.Invalid;
            }

            parsed[i] = new SearchAlternative(SearchOperator.Equal, FieldValue.FromIndex(optionIndex));
        }

        alternatives = parsed;
        return ValueError.None;
    }


    // Date: blank matches a column holding no value; otherwise each alternative is the prefix operator on an ISO
    // date the field's datetime column can hold, so a bare prefix or another format is invalid (a target departure
    // from legacy's current-culture parse).
    private static ValueError ParseDate(FieldValue value, ColumnMetadata? column, out SearchAlternative[] alternatives)
    {
        alternatives = [];
        var reading = ReadAlternatives(value, out var texts);
        if (reading == TextReading.Blank)
        {
            alternatives = [SearchAlternative.Null];
            return ValueError.None;
        }

        if (reading == TextReading.NotText || column is null)
        {
            return ValueError.Invalid;
        }

        var parsed = new SearchAlternative[texts.Length];
        for (var i = 0; i < texts.Length; i++)
        {
            var (comparison, rest) = SplitPrefix(texts[i]);
            if (!FieldValueCoercion.TryParseIsoDate(rest, out var day))
            {
                return ValueError.Invalid;
            }

            // The mapper owns the datetime range, 1753-01-01 to 9999-12-31.
            var date = FieldValue.FromDate(day);
            if (!FieldTypeMapper.IsRepresentable(column, date))
            {
                return ValueError.Invalid;
            }

            parsed[i] = new SearchAlternative(comparison, date);
        }

        alternatives = parsed;
        return ValueError.None;
    }

    // Checkbox: each alternative is compared as written, prefix included and case-sensitive, with the legacy
    // representations; a JSON boolean is also accepted (a target extension for API callers).
    private static ValueError ParseCheckbox(FieldValue value, out SearchAlternative[] alternatives)
    {
        alternatives = [];
        if (value.TryGetBoolean(out var isChecked))
        {
            alternatives = [new SearchAlternative(SearchOperator.Equal, FieldValue.FromBoolean(isChecked))];
            return ValueError.None;
        }

        var reading = ReadAlternatives(value, out var texts);
        if (reading != TextReading.Alternatives)
        {
            return reading == TextReading.Blank ? ValueError.Blank : ValueError.Invalid;
        }

        var parsed = new SearchAlternative[texts.Length];
        for (var i = 0; i < texts.Length; i++)
        {
            bool? state = texts[i] switch
            {
                YesText or CheckedLiteral => true,
                NoText or UncheckedLiteral => false,
                _ => null,
            };

            if (state is not { } checkedState)
            {
                return ValueError.Invalid;
            }

            parsed[i] = new SearchAlternative(SearchOperator.Equal, FieldValue.FromBoolean(checkedState));
        }

        alternatives = parsed;
        return ValueError.None;
    }

    // RecStatus: an integral status compared for equality with any prefix ignored. It narrows the caller's selected
    // set and never replaces that set's predicate.
    private static ValueError ParseRecStatus(FieldValue value, out SearchAlternative[] alternatives)
    {
        alternatives = [];
        if (value.Kind == FieldValueKind.Number)
        {
            if (!value.TryGetInteger(out var status) || status < int.MinValue || status > int.MaxValue)
            {
                return ValueError.Invalid;
            }

            alternatives = [new SearchAlternative(SearchOperator.Equal, FieldValue.FromNumber(status))];
            return ValueError.None;
        }

        var reading = ReadAlternatives(value, out var texts);
        if (reading != TextReading.Alternatives)
        {
            return reading == TextReading.Blank ? ValueError.Blank : ValueError.Invalid;
        }

        var parsed = new SearchAlternative[texts.Length];
        for (var i = 0; i < texts.Length; i++)
        {
            var (_, rest) = SplitPrefix(texts[i]);
            if (!FieldValueCoercion.TryParseInvariantInteger(rest, out var parsedStatus))
            {
                return ValueError.Invalid;
            }

            parsed[i] = new SearchAlternative(SearchOperator.Equal, FieldValue.FromNumber(parsedStatus));
        }

        alternatives = parsed;
        return ValueError.None;
    }

    // Reads a value as the legacy dialog reads a control's text: trimmed, then split into its alternatives, which are
    // not trimmed again. JSON null, text that trims to nothing, and text that leaves no alternative are blank.
    private static TextReading ReadAlternatives(FieldValue value, out string[] alternatives)
    {
        alternatives = [];
        if (value.IsNull)
        {
            return TextReading.Blank;
        }

        if (!value.TryGetText(out var text))
        {
            return TextReading.NotText;
        }

        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return TextReading.Blank;
        }

        alternatives = trimmed.Split(OrSeparators, StringSplitOptions.RemoveEmptyEntries);
        return alternatives.Length == 0 ? TextReading.Blank : TextReading.Alternatives;
    }

    // The legacy prefix rule: the first prefix the alternative starts with, ordinally, is the operator and is
    // stripped; with none, the operator is Equal and nothing is stripped.
    private static (SearchOperator Operator, string Remainder) SplitPrefix(string alternative)
    {
        foreach (var (text, comparison) in Prefixes)
        {
            if (alternative.StartsWith(text, StringComparison.Ordinal))
            {
                return (comparison, alternative[text.Length..]);
            }
        }

        return (SearchOperator.Equal, alternative);
    }

    // Fixed text naming the canonical field and the expected syntax; never the submitted value.
    private static string InvalidValueMessage(ResolvedField field) => field.Kind switch
    {
        CriterionKind.ComparedText or CriterionKind.PatternText => $"{field.Name} must be searched with text.",
        CriterionKind.Number =>
            $"{field.Name} must be searched with numbers, {PrefixHint}, {AlternativesHint}.",
        CriterionKind.Option =>
            $"{field.Name} must be searched with option indexes, whole numbers of 0 or more, {AlternativesHint}.",
        CriterionKind.Date =>
            $"{field.Name} must be searched with dates in the form YYYY-MM-DD from 1753-01-01 to 9999-12-31, " +
            $"{PrefixHint}, {AlternativesHint}.",
        CriterionKind.Checkbox =>
            $"{field.Name} must be searched with Yes, No, (+) or (-), {AlternativesHint}, or with true or false.",
        CriterionKind.RecStatus => $"{field.Name} must be searched with whole numbers, {AlternativesHint}.",
        _ => throw new UnreachableException("Every criterion kind has an error message."),
    };

    // Names the criterion by its 0-based position only, because the caller's raw name is never echoed.
    private static string CriteriaMessage(int index, string problem) =>
        string.Create(CultureInfo.InvariantCulture, $"criteria[{index}] {problem}");

    private static void AddError(Dictionary<string, List<string>> errors, string key, string message)
    {
        if (!errors.TryGetValue(key, out var messages))
        {
            messages = [];
            errors.Add(key, messages);
        }

        messages.Add(message);
    }

    // A criterion name resolved to its searchable column: the canonical name, the target, the data field's type and
    // column (null for both system fields), and the value rules that apply.
    private readonly record struct ResolvedField(
        string Name,
        SearchTarget Target,
        FieldType? FieldType,
        ColumnMetadata? Column,
        CriterionKind Kind);
}
