using EpiInfo.Domain.Values;

namespace EpiInfo.CheckCode.Functions;

/// <summary>
/// <c>YEARS(first, second)</c>: the whole years from the first date to the second, negative when the first
/// date is later.
/// </summary>
/// <remarks>
/// <para>
/// An exact port of the legacy function. The span between the dates is turned into a
/// <see cref="DateTime"/> from its ticks, and the result is that date's year minus 1. When either argument
/// is not a date, including <see cref="FieldValue.Null"/> and text, the result is a successful null; text
/// is never parsed into a date.
/// </para>
/// <para>
/// The tick-based count is kept on purpose, although it is wrong across leap days: a birth date of
/// 2000-02-29 and an interview on 2011-02-28 give 11, not 10. That is the legacy defect of scenario
/// <c>EC-AGE-03</c>, listed under "Legacy defects kept" in <c>docs/parity-notes.md</c>.
/// </para>
/// </remarks>
public static class YearsFunction
{
    /// <summary>The function name, matched case-insensitively.</summary>
    public const string Name = "YEARS";

    /// <summary>Computes <c>YEARS</c>.</summary>
    /// <param name="arguments">Exactly two evaluated arguments.</param>
    /// <returns>
    /// The year count as a number; a successful null when either argument is not a date; a failure when
    /// the argument count is not 2.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="arguments"/> is <see langword="null"/>.</exception>
    public static FunctionResult Invoke(IReadOnlyList<FieldValue> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (arguments.Count != 2)
        {
            return FunctionResult.Fail("YEARS requires exactly 2 arguments");
        }

        if (!arguments[0].TryGetDate(out var firstDate) || !arguments[1].TryGetDate(out var secondDate))
        {
            return FunctionResult.Ok(FieldValue.Null);
        }

        // Date values carry no time of day, so both sides are compared at midnight.
        var first = firstDate.ToDateTime(TimeOnly.MinValue);
        var second = secondDate.ToDateTime(TimeOnly.MinValue);

        // Integer arithmetic before the conversion to double, so a reversed span under a year is 0, not -0.
        int years = first > second
            ? -(new DateTime((first - second).Ticks).Year - 1)
            : new DateTime((second - first).Ticks).Year - 1;

        return FunctionResult.Ok(FieldValue.FromNumber(years));
    }
}
