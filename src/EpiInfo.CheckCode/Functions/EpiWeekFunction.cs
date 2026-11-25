using System.Globalization;
using EpiInfo.Domain.Values;

namespace EpiInfo.CheckCode.Functions;

/// <summary>
/// <c>EPIWEEK(date[, firstDay])</c>: the MMWR epidemiological week of a date.
/// </summary>
/// <remarks>
/// <para>
/// An exact port of the legacy function. Week 1 is the first week with at least four days in the new
/// year. The start of the MMWR year is taken from the year before, the year itself or the year after,
/// and the week is the elapsed days from that start divided by 7, plus 1. The optional first-day
/// argument is 1-based (1 = Sunday) and is reduced by 1, as in legacy.
/// </para>
/// <para>
/// Departures from legacy, each where legacy threw or depended on the server culture:
/// a missing date fails with "EPIWEEK requires a date" where legacy threw; a value that is not a date,
/// text included, fails the same way, because legacy's culture-dependent text parsing is not ported and
/// date literals are outside the executed subset; a missing first-day argument fails where legacy threw;
/// a text first day is parsed with the invariant culture; and a date whose MMWR computation leaves the
/// supported date range fails with "EPIWEEK date is out of range" where legacy threw.
/// </para>
/// </remarks>
public static class EpiWeekFunction
{
    /// <summary>The function name, matched case-insensitively.</summary>
    public const string Name = "EPIWEEK";

    /// <summary>Computes <c>EPIWEEK</c>.</summary>
    /// <param name="arguments">One or two evaluated arguments: the date, then the optional first day.</param>
    /// <returns>The week number as a number, or a failure that names the problem.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="arguments"/> is <see langword="null"/>.</exception>
    public static FunctionResult Invoke(IReadOnlyList<FieldValue> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (arguments.Count is < 1 or > 2)
        {
            return FunctionResult.Fail("EPIWEEK requires 1 or 2 arguments");
        }

        if (!arguments[0].TryGetDate(out var date))
        {
            return FunctionResult.Fail("EPIWEEK requires a date");
        }

        var firstDay = 0;
        if (arguments.Count == 2)
        {
            var firstDayArgument = arguments[1];
            if (firstDayArgument.IsNull)
            {
                return FunctionResult.Fail("EPIWEEK first-day argument is missing");
            }

            if (TryReadFirstDay(firstDayArgument, out var parsed))
            {
                firstDay = unchecked(parsed - 1);
            }
        }

        try
        {
            var day = date.ToDateTime(TimeOnly.MinValue);
            var start = GetMmwrStart(day, firstDay);

            // Integer division truncates toward zero, as legacy's (int)(Days / 7) does.
            var week = (day - start).Days / 7 + 1;
            return FunctionResult.Ok(FieldValue.FromNumber(week));
        }
        catch (ArgumentOutOfRangeException)
        {
            // Legacy threw here too: a neighbouring year outside 1..9999, or a start before 0001-01-01.
            return FunctionResult.Fail("EPIWEEK date is out of range");
        }
    }

    /// <summary>
    /// Reads the 1-based first day the way legacy's <c>int.TryParse</c> of the value's text did; a value
    /// that does not read as an integer keeps the default.
    /// </summary>
    private static bool TryReadFirstDay(FieldValue argument, out int value)
    {
        value = 0;
        switch (argument.Kind)
        {
            case FieldValueKind.Number:
                var number = argument.AsNumber();
                if (number == Math.Floor(number) && number >= int.MinValue && number <= int.MaxValue)
                {
                    value = (int)number;
                    return true;
                }

                return false;

            case FieldValueKind.Index:
                value = argument.AsIndex();
                return true;

            case FieldValueKind.Text:
                return int.TryParse(argument.AsText(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

            default:
                // Booleans and dates never parsed as an integer in legacy.
                return false;
        }
    }

    private static DateTime GetMmwrStart(int year, int firstDay)
    {
        var januaryFirst = new DateTime(year, 1, 1);
        var dayOfWeek = (int)januaryFirst.DayOfWeek;

        return dayOfWeek <= firstDay + 3
            ? januaryFirst.AddDays(firstDay - dayOfWeek)
            : januaryFirst.AddDays(7 - firstDay - dayOfWeek);
    }

    private static DateTime GetMmwrStart(DateTime day, int firstDay)
    {
        var previous = GetMmwrStart(day.Year - 1, firstDay);
        var current = GetMmwrStart(day.Year, firstDay);
        var next = GetMmwrStart(day.Year + 1, firstDay);

        if ((current - day).Days > 0)
        {
            return previous;
        }

        return (next - day).Days > 0 ? current : next;
    }
}
