using System.Diagnostics.CodeAnalysis;
using EpiInfo.CheckCode.Syntax;
using EpiInfo.Domain.Values;

namespace EpiInfo.CheckCode.Functions;

/// <summary>
/// The outcome of one check-code function call: a value, or the reason the call cannot produce one.
/// </summary>
/// <remarks>
/// <para>
/// <c>Ok(FieldValue.Null)</c> is a successful call whose value is null. <c>YEARS</c> with a missing date
/// returns it, so an <c>ASSIGN</c> of that call assigns <see langword="null"/> (scenario <c>EC-AGE-05</c>).
/// </para>
/// <para>
/// A failed result means the statement holding the call cannot run. The evaluator turns
/// <see cref="Error"/> into an <c>error</c> diagnostic and skips that statement; the effects of earlier
/// statements stand.
/// </para>
/// <para>
/// <see cref="Value"/> may be computed from field data, which the redaction policy keeps out of logs and
/// error bodies. <see cref="Error"/> is client diagnostic text: the evaluator puts it into the reason of the
/// statement's <c>error</c> diagnostic, which the field-event response returns. It is not a log argument. The
/// diagnostic is logged through a fixed message template with structured fields only, so neither
/// <see cref="Error"/>, <see cref="Value"/> nor this result's <see cref="object.ToString"/> is passed to a logger.
/// </para>
/// </remarks>
/// <param name="Value">The returned value; <see cref="FieldValue.Null"/> for a null result or a failure.</param>
/// <param name="Error">
/// The failure message, or <see langword="null"/> when the call succeeded.
/// </param>
public readonly record struct FunctionResult(FieldValue Value, string? Error)
{
    /// <summary>Whether the call produced a value, possibly <see cref="FieldValue.Null"/>.</summary>
    public bool IsSuccess => Error is null;

    /// <summary>Creates a successful result.</summary>
    /// <param name="value">The value, which may be <see cref="FieldValue.Null"/>.</param>
    /// <returns>A result whose <see cref="IsSuccess"/> is <see langword="true"/>.</returns>
    public static FunctionResult Ok(FieldValue value) => new(value, null);

    /// <summary>Creates a failed result, whose value is <see cref="FieldValue.Null"/>.</summary>
    /// <remarks>
    /// The message becomes part of a diagnostic returned to the client, so it names the function and the
    /// problem only and never contains an argument value, for example <c>"YEARS requires exactly 2 arguments"</c>
    /// or <c>"EPIWEEK requires a date"</c>. It is never itself written to the log.
    /// </remarks>
    /// <param name="message">A fixed message naming the function and the problem.</param>
    /// <returns>A result whose <see cref="IsSuccess"/> is <see langword="false"/>.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="message"/> is <see langword="null"/>, empty or white space. This is a programming
    /// error, never a consequence of input.
    /// </exception>
    public static FunctionResult Fail(string message)
    {
        // A plain ArgumentException for null as well, so callers can expect one exception type.
        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("A function failure message must not be null or blank.", nameof(message));
        }

        return new FunctionResult(FieldValue.Null, message);
    }
}

/// <summary>
/// A check-code function. It never throws for any argument value and reports every failure through
/// <see cref="FunctionResult.Fail"/>.
/// </summary>
/// <param name="arguments">
/// The arguments, already evaluated, in source order. A missing value is <see cref="FieldValue.Null"/>.
/// The function checks the argument count itself.
/// </param>
/// <returns>The call's outcome.</returns>
public delegate FunctionResult CheckCodeFunction(IReadOnlyList<FieldValue> arguments);

/// <summary>
/// The implementations of the check-code functions the interpreter executes, <c>YEARS</c> and <c>EPIWEEK</c>,
/// looked up by name.
/// </summary>
/// <remarks>
/// <para>
/// The names are the syntax-level <see cref="FunctionNames"/> vocabulary, which the parser reads as well: the
/// registry maps each of them to its implementation. Names match case-insensitively and exactly, as check-code
/// keywords do: <c>years</c> and <c>EpiWeek</c> resolve, while <c>YEAR</c>, <c>EPIWEEKS</c> and a name with
/// surrounding spaces do not. Every other function is unsupported; the parser reports a call to one as a
/// <c>warning</c> diagnostic.
/// </para>
/// <para>
/// The registry does not check the argument count. Each function enforces its own arity in its
/// <c>Invoke</c> method (<c>YEARS</c> exactly 2, <c>EPIWEEK</c> 1 or 2) and returns a failed result
/// otherwise, so a direct call applies the same rule as a call resolved here.
/// </para>
/// <para>
/// The map is built once during type initialisation and never changes, and no state is kept between
/// calls, so the registry is safe to use from any number of threads.
/// </para>
/// </remarks>
public static class FunctionRegistry
{
    // Add, not the indexer, so a duplicate name fails type initialisation instead of replacing an entry.
    private static readonly Dictionary<string, CheckCodeFunction> Functions = new(StringComparer.OrdinalIgnoreCase)
    {
        { YearsFunction.Name, YearsFunction.Invoke },
        { EpiWeekFunction.Name, EpiWeekFunction.Invoke },
    };

    /// <summary>
    /// The canonical names of the supported functions, <see cref="FunctionNames.Supported"/>: <c>YEARS</c>, then
    /// <c>EPIWEEK</c>.
    /// </summary>
    public static IReadOnlyCollection<string> SupportedNames => FunctionNames.Supported;

    /// <summary>Looks up a supported function by name.</summary>
    /// <param name="name">The function name as written in the program. <see langword="null"/> matches nothing.</param>
    /// <param name="function">The function when found; otherwise <see langword="null"/>.</param>
    /// <returns>Whether <paramref name="name"/> names a supported function.</returns>
    public static bool TryGet(string? name, [NotNullWhen(true)] out CheckCodeFunction? function)
    {
        if (name is null)
        {
            function = null;
            return false;
        }

        return Functions.TryGetValue(name, out function);
    }

    /// <summary>Whether a name is a supported function.</summary>
    /// <param name="name">The function name as written in the program. <see langword="null"/> matches nothing.</param>
    /// <returns>Whether <paramref name="name"/> names a supported function.</returns>
    public static bool IsSupported(string? name) => TryGet(name, out _);
}
