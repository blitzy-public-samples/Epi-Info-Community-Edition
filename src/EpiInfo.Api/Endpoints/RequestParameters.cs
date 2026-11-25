using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using EpiInfo.Api.Errors;
using EpiInfo.Domain.Records;

namespace EpiInfo.Api.Endpoints;

/// <summary>
/// The single owner of route and query coercion. Endpoints bind <c>position</c> and <c>q</c> as strings, and
/// <c>page</c>, <c>pageSize</c> and <c>includeDeleted</c> as the strings of every occurrence sent, and hand them
/// here, so framework binding never produces an undocumented status: a value that does not coerce is a field-level
/// 400 keyed by the parameter's wire name.
/// </summary>
/// <remarks>
/// <para>
/// <b>Absent values.</b> <c>page</c>, <c>pageSize</c> and <c>includeDeleted</c> arrive as their occurrences in the
/// order sent: none for a key that was not sent, and the empty string for an occurrence sent without a value, such as
/// <c>?page=</c> or a bare <c>?page</c>. Only a parameter with no occurrence, an empty or <see langword="null"/> list,
/// is absent and takes its default. A parameter sent once is that one value, and any text, the empty string and
/// whitespace included, must coerce. A parameter sent more than once has no single value and is a 400 under its key
/// with the same fixed message as a value that does not coerce, whatever each occurrence holds, so an empty
/// occurrence is never dropped in favour of another. A <see langword="null"/> occurrence is invalid. <c>position</c>
/// is a route segment and has no default.
/// </para>
/// <para>
/// <b>Integers</b> are invariant 32-bit values written as one complete token: an optional single leading <c>+</c> or
/// <c>-</c> followed by one or more ASCII digits <c>0</c> to <c>9</c>, and nothing else. Surrounding whitespace,
/// embedded or trailing NUL characters, non-ASCII digits and signs, thousands separators, decimals, exponents and hex
/// are rejected before the range conversion, and a token outside <see cref="int"/> is a 400.
/// </para>
/// <para>
/// <b>Filter length.</b> A non-blank <c>q</c> must pass <see cref="RecordQueryRules.IsRepresentableFilter"/>, so its
/// escaped and wrapped contains pattern fits SQL Server's <c>LIKE</c> pattern limit; a longer one is a 400 on
/// <c>q</c> with <see cref="RecordQueryRules.FilterTooLongMessage"/>.
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
    /// <summary>The query parameter name of the records-list filter, the key of its 400 <c>errors</c> entry.</summary>
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
    /// <param name="raw">
    /// The query occurrences as bound, in the order sent: none, or <see langword="null"/>, when the key was not sent;
    /// the empty string for an occurrence sent without a value.
    /// </param>
    /// <param name="includeDeleted">
    /// The flag: <see langword="false"/> when <paramref name="raw"/> has no occurrence; for exactly one occurrence,
    /// <c>true</c> or <c>false</c> matched ignoring case; <see langword="false"/> on failure, which includes an empty
    /// occurrence and a key sent more than once.
    /// </param>
    /// <param name="problem">
    /// <see langword="null"/> on success; otherwise a field-level 400 with <c>errors["includeDeleted"]</c>.
    /// </param>
    /// <returns>Whether <paramref name="raw"/> coerced.</returns>
    public static bool TryParseIncludeDeleted(
        string[]? raw,
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
    /// The query occurrences of <c>includeDeleted</c> in the order sent, as <see cref="TryParseIncludeDeleted"/>
    /// coerces them.
    /// </param>
    /// <param name="parsedPosition">The parsed position; 0 on failure.</param>
    /// <param name="parsedIncludeDeleted">The parsed flag; <see langword="false"/> on failure.</param>
    /// <param name="problem">
    /// <see langword="null"/> on success; otherwise one field-level 400 with an entry per bad parameter.
    /// </param>
    /// <returns>Whether both parameters coerced.</returns>
    public static bool TryParsePositionRequest(
        string? position,
        string[]? includeDeleted,
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
    /// <see cref="RecordQueryRules.ValidatePaging"/> and the filter length with
    /// <see cref="RecordQueryRules.IsRepresentableFilter"/>.
    /// </summary>
    /// <param name="q">
    /// The grid filter; absent, empty or whitespace means no filter, and any other value is kept unchanged, without
    /// trimming. Its contains pattern must fit <see cref="RecordQueryRules.MaxLikePatternLength"/>.
    /// </param>
    /// <param name="includeDeleted">
    /// The query occurrences of <c>includeDeleted</c> in the order sent, as <see cref="TryParseIncludeDeleted"/>
    /// coerces them.
    /// </param>
    /// <param name="page">
    /// The occurrences of the page number in the order sent; none, or <see langword="null"/>, a key not sent, means
    /// <see cref="RecordQueryRules.DefaultPage"/>. Exactly one occurrence, the empty string included, must be an
    /// integer of at least <see cref="RecordQueryRules.MinPage"/>, and more than one is a 400.
    /// </param>
    /// <param name="pageSize">
    /// The occurrences of the page size in the order sent; none, or <see langword="null"/>, a key not sent, means
    /// <see cref="RecordQueryRules.DefaultPageSize"/>. Exactly one occurrence, the empty string included, must be an
    /// integer from <see cref="RecordQueryRules.MinPageSize"/> to <see cref="RecordQueryRules.MaxPageSize"/>, and
    /// more than one is a 400.
    /// </param>
    /// <param name="query">The coerced query on success; otherwise <see langword="null"/>.</param>
    /// <param name="problem">
    /// <see langword="null"/> on success; otherwise one field-level 400 with an entry per bad parameter, each
    /// <c>page</c> or <c>pageSize</c> entry carrying either <see cref="IntegerExpectedMessage"/> or its range message,
    /// and a <c>q</c> entry carrying <see cref="RecordQueryRules.FilterTooLongMessage"/>.
    /// </param>
    /// <returns>Whether every parameter coerced and the paging and filter bounds hold.</returns>
    public static bool TryParseListQuery(
        string? q,
        string[]? includeDeleted,
        string[]? page,
        string[]? pageSize,
        [NotNullWhen(true)] out RecordListQuery? query,
        [NotNullWhen(false)] out IResult? problem)
    {
        Dictionary<string, string[]>? errors = null;

        // Blank stays no filter; any other q is kept untrimmed, so its own length and characters are what count.
        var filter = string.IsNullOrWhiteSpace(q) ? null : q;
        if (filter is not null && !RecordQueryRules.IsRepresentableFilter(filter))
        {
            AddError(Query, RecordQueryRules.FilterTooLongMessage, ref errors);
        }

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
            filter,
            parsedIncludeDeleted,
            parsedPage,
            parsedPageSize);
        return true;
    }

    private static void CoerceIncludeDeleted(
        string[]? occurrences,
        out bool includeDeleted,
        ref Dictionary<string, string[]>? errors)
    {
        includeDeleted = false;
        if (!TryGetSingleOccurrence(occurrences, out var raw))
        {
            AddError(IncludeDeleted, BooleanExpectedMessage, ref errors);
            return;
        }

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

    private static bool TryParseOptionalInteger(string[]? occurrences, int defaultValue, out int value)
    {
        if (!TryGetSingleOccurrence(occurrences, out var raw))
        {
            value = 0;
            return false;
        }

        if (IsAbsent(raw))
        {
            value = defaultValue;
            return true;
        }

        return TryParseInteger(raw, out value);
    }

    private static bool TryParseInteger([NotNullWhen(true)] string? raw, out int value)
    {
        // int.TryParse alone also admits trailing NUL characters, so the token is checked first and the parse only
        // converts it and checks the 32-bit range.
        if (!IsIntegerToken(raw))
        {
            value = 0;
            return false;
        }

        return int.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }

    // An optional single leading '+' or '-' followed by one or more ASCII digits, and nothing else.
    private static bool IsIntegerToken([NotNullWhen(true)] string? raw)
    {
        if (raw is null)
        {
            return false;
        }

        var digits = raw.AsSpan();
        if (!digits.IsEmpty && digits[0] is '+' or '-')
        {
            digits = digits[1..];
        }

        return !digits.IsEmpty && !digits.ContainsAnyExceptInRange('0', '9');
    }

    // Resolves the occurrences of one query key as bound: none is absent, so raw is null; exactly one non-null
    // occurrence is raw, the empty string included. A key sent more than once, or a null occurrence, has no single
    // value and fails, so no occurrence is ever chosen over or dropped in favour of another.
    private static bool TryGetSingleOccurrence(string[]? occurrences, out string? raw)
    {
        switch (occurrences)
        {
            case null or []:
                raw = null;
                return true;

            case [{ } single]:
                raw = single;
                return true;

            default:
                raw = null;
                return false;
        }
    }

    // Only a key that was not sent is absent; the empty string is a value that was sent.
    private static bool IsAbsent([NotNullWhen(false)] string? raw) => raw is null;

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
