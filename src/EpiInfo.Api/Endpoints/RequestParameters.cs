using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using EpiInfo.Api.Errors;
using EpiInfo.Domain.Records;

namespace EpiInfo.Api.Endpoints;

/// <summary>
/// The single owner of route and query coercion. Endpoints bind <c>position</c>, <c>page</c>, <c>pageSize</c>,
/// <c>includeDeleted</c> and <c>q</c> as strings and hand them here, so framework binding never produces an
/// undocumented status: a value that does not coerce is a field-level 400 keyed by the parameter's wire name.
/// </summary>
/// <remarks>
/// <para>
/// <b>Absent values.</b> For <c>page</c>, <c>pageSize</c> and <c>includeDeleted</c>, <see langword="null"/> and the
/// empty string both mean absent, because a bare <c>?page=</c> carries no value and string binding may present it
/// either way; an absent value takes its default. Any other text, whitespace included, is present and must coerce.
/// <c>position</c> is a route segment and has no default.
/// </para>
/// <para>
/// <b>Integers</b> are invariant 32-bit values with an optional leading sign and nothing else: no surrounding
/// whitespace, thousands separators, decimals, exponents or hex. A value outside <see cref="int"/> is a 400.
/// </para>
/// <para>
/// <b>One 400 per request.</b> Every bad parameter of a request is reported in one
/// <see cref="HttpValidationProblemDetails"/>, one <c>errors</c> key per parameter, built by
/// <see cref="ApiProblems"/>. Messages are fixed texts that never echo the submitted value, as the redaction policy
/// requires.
/// </para>
/// <para>
/// <c>viewId</c> is not coerced here: its <c>int</c> route constraint makes a non-integer form id match no route,
/// which status-code pages turn into the 404 ProblemDetails. Every member is a pure function with no shared state.
/// </para>
/// </remarks>
public static class RequestParameters
{
    /// <summary>The query parameter name of the records-list filter.</summary>
    public const string Query = "q";

    /// <summary>The query parameter name of the deleted toggle, the key of its 400 <c>errors</c> entry.</summary>
    public const string IncludeDeleted = "includeDeleted";

    /// <summary>The query parameter name of the page number, the key of its 400 <c>errors</c> entry.</summary>
    public const string Page = RecordQueryRules.PageParameter;

    /// <summary>The query parameter name of the page size, the key of its 400 <c>errors</c> entry.</summary>
    public const string PageSize = RecordQueryRules.PageSizeParameter;

    /// <summary>The route parameter name of a 1-based position, the key of its 400 <c>errors</c> entry.</summary>
    public const string Position = "position";

    /// <summary>The fixed message for a non-integer <c>page</c>, <c>pageSize</c> or <c>position</c>.</summary>
    public const string IntegerExpectedMessage = "The value must be an integer.";

    /// <summary>The fixed message for an <c>includeDeleted</c> that is neither <c>true</c> nor <c>false</c>.</summary>
    public const string BooleanExpectedMessage = "The value must be true or false.";

    private const string TrueText = "true";

    private const string FalseText = "false";

    /// <summary>
    /// Coerces <c>includeDeleted</c>, on every endpoint that takes it, <c>count</c> included.
    /// </summary>
    /// <param name="raw">The query value as bound, or <see langword="null"/> when absent.</param>
    /// <param name="includeDeleted">
    /// The flag: <see langword="false"/> when <paramref name="raw"/> is absent, otherwise <c>true</c> or <c>false</c>
    /// matched ignoring case; <see langword="false"/> on failure.
    /// </param>
    /// <param name="problem">
    /// <see langword="null"/> on success; otherwise a field-level 400 with <c>errors["includeDeleted"]</c>.
    /// </param>
    /// <returns>Whether <paramref name="raw"/> coerced.</returns>
    public static bool TryParseIncludeDeleted(
        string? raw,
        out bool includeDeleted,
        [NotNullWhen(false)] out IResult? problem)
    {
        Dictionary<string, string[]>? errors = null;
        CoerceIncludeDeleted(raw, out includeDeleted, ref errors);
        return Succeeded(errors, out problem);
    }

    /// <summary>
    /// Coerces a route <c>position</c>. Any 32-bit integer succeeds whatever its value: <c>0</c>, a negative value
    /// and a value above <c>count</c> are integers whose 404 the record service decides.
    /// </summary>
    /// <param name="raw">The route value as bound.</param>
    /// <param name="position">The parsed position; 0 on failure.</param>
    /// <param name="problem">
    /// <see langword="null"/> on success; otherwise a field-level 400 with <c>errors["position"]</c>, for an absent
    /// value as for any other text that is not an integer.
    /// </param>
    /// <returns>Whether <paramref name="raw"/> coerced.</returns>
    public static bool TryParsePosition(string? raw, out int position, [NotNullWhen(false)] out IResult? problem)
    {
        Dictionary<string, string[]>? errors = null;
        CoercePosition(raw, out position, ref errors);
        return Succeeded(errors, out problem);
    }

    /// <summary>
    /// Coerces the inputs of <c>GET .../records/at/{position}</c>, reporting both parameters in one 400 when both
    /// are bad.
    /// </summary>
    /// <param name="position">The route value of <c>position</c>, as <see cref="TryParsePosition"/> coerces it.</param>
    /// <param name="includeDeleted">
    /// The query value of <c>includeDeleted</c>, as <see cref="TryParseIncludeDeleted"/> coerces it.
    /// </param>
    /// <param name="parsedPosition">The parsed position; 0 on failure.</param>
    /// <param name="parsedIncludeDeleted">The parsed flag; <see langword="false"/> on failure.</param>
    /// <param name="problem">
    /// <see langword="null"/> on success; otherwise one field-level 400 with an entry per bad parameter.
    /// </param>
    /// <returns>Whether both parameters coerced.</returns>
    public static bool TryParsePositionRequest(
        string? position,
        string? includeDeleted,
        out int parsedPosition,
        out bool parsedIncludeDeleted,
        [NotNullWhen(false)] out IResult? problem)
    {
        Dictionary<string, string[]>? errors = null;
        CoercePosition(position, out parsedPosition, ref errors);
        CoerceIncludeDeleted(includeDeleted, out parsedIncludeDeleted, ref errors);

        if (!Succeeded(errors, out problem))
        {
            parsedPosition = 0;
            parsedIncludeDeleted = false;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Coerces the inputs of the records list, <c>GET .../records</c>, and checks the paging bounds with
    /// <see cref="RecordQueryRules.ValidatePaging"/>.
    /// </summary>
    /// <param name="q">
    /// The grid filter; absent, empty or whitespace means no filter, and any other value is kept unchanged, without
    /// trimming.
    /// </param>
    /// <param name="includeDeleted">
    /// The query value of <c>includeDeleted</c>, as <see cref="TryParseIncludeDeleted"/> coerces it.
    /// </param>
    /// <param name="page">
    /// The page number; absent means <see cref="RecordQueryRules.DefaultPage"/>. It must be an integer of at least
    /// <see cref="RecordQueryRules.MinPage"/>.
    /// </param>
    /// <param name="pageSize">
    /// The page size; absent means <see cref="RecordQueryRules.DefaultPageSize"/>. It must be an integer from
    /// <see cref="RecordQueryRules.MinPageSize"/> to <see cref="RecordQueryRules.MaxPageSize"/>.
    /// </param>
    /// <param name="query">The coerced query on success; otherwise <see langword="null"/>.</param>
    /// <param name="problem">
    /// <see langword="null"/> on success; otherwise one field-level 400 with an entry per bad parameter, each
    /// <c>page</c> or <c>pageSize</c> entry carrying either <see cref="IntegerExpectedMessage"/> or its range message.
    /// </param>
    /// <returns>Whether every parameter coerced and the paging bounds hold.</returns>
    public static bool TryParseListQuery(
        string? q,
        string? includeDeleted,
        string? page,
        string? pageSize,
        [NotNullWhen(true)] out RecordListQuery? query,
        [NotNullWhen(false)] out IResult? problem)
    {
        Dictionary<string, string[]>? errors = null;
        CoerceIncludeDeleted(includeDeleted, out var parsedIncludeDeleted, ref errors);

        var pageParsed = TryParseOptionalInteger(page, RecordQueryRules.DefaultPage, out var parsedPage);
        var pageSizeParsed =
            TryParseOptionalInteger(pageSize, RecordQueryRules.DefaultPageSize, out var parsedPageSize);

        // An unparsed parameter is checked as its default, which is always in range, so the one ValidatePaging call
        // reports range errors only for the parameters that parsed.
        var rangeErrors = RecordQueryRules.ValidatePaging(
            pageParsed ? parsedPage : RecordQueryRules.DefaultPage,
            pageSizeParsed ? parsedPageSize : RecordQueryRules.DefaultPageSize);
        AddPagingError(Page, pageParsed, rangeErrors, ref errors);
        AddPagingError(PageSize, pageSizeParsed, rangeErrors, ref errors);

        if (!Succeeded(errors, out problem))
        {
            query = null;
            return false;
        }

        query = new RecordListQuery(
            string.IsNullOrWhiteSpace(q) ? null : q,
            parsedIncludeDeleted,
            parsedPage,
            parsedPageSize);
        return true;
    }

    private static void CoerceIncludeDeleted(
        string? raw,
        out bool includeDeleted,
        ref Dictionary<string, string[]>? errors)
    {
        includeDeleted = false;
        if (IsAbsent(raw) || string.Equals(raw, FalseText, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Compared exactly rather than with bool.TryParse, which would also admit surrounding whitespace.
        if (string.Equals(raw, TrueText, StringComparison.OrdinalIgnoreCase))
        {
            includeDeleted = true;
            return;
        }

        AddError(IncludeDeleted, BooleanExpectedMessage, ref errors);
    }

    private static void CoercePosition(string? raw, out int position, ref Dictionary<string, string[]>? errors)
    {
        if (!TryParseInteger(raw, out position))
        {
            position = 0;
            AddError(Position, IntegerExpectedMessage, ref errors);
        }
    }

    private static void AddPagingError(
        string parameter,
        bool parsed,
        IReadOnlyList<PagingError> rangeErrors,
        ref Dictionary<string, string[]>? errors)
    {
        if (!parsed)
        {
            AddError(parameter, IntegerExpectedMessage, ref errors);
            return;
        }

        foreach (var error in rangeErrors)
        {
            if (string.Equals(error.Parameter, parameter, StringComparison.Ordinal))
            {
                AddError(parameter, error.Message, ref errors);
            }
        }
    }

    private static bool TryParseOptionalInteger(string? raw, int defaultValue, out int value)
    {
        if (IsAbsent(raw))
        {
            value = defaultValue;
            return true;
        }

        return TryParseInteger(raw, out value);
    }

    private static bool TryParseInteger([NotNullWhen(true)] string? raw, out int value) =>
        int.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);

    private static bool IsAbsent([NotNullWhen(false)] string? raw) => string.IsNullOrEmpty(raw);

    private static void AddError(string parameter, string message, ref Dictionary<string, string[]>? errors)
    {
        errors ??= new Dictionary<string, string[]>(StringComparer.Ordinal);
        errors[parameter] = errors.TryGetValue(parameter, out var existing) ? [.. existing, message] : [message];
    }

    private static bool Succeeded(Dictionary<string, string[]>? errors, [NotNullWhen(false)] out IResult? problem)
    {
        if (errors is null)
        {
            problem = null;
            return true;
        }

        problem = ApiProblems.ValidationFailed(errors);
        return false;
    }
}
