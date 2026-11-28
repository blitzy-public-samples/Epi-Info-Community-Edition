using System.Globalization;
using System.Text.RegularExpressions;
using EpiInfo.CheckCode.Syntax;
using EpiInfo.Domain.Values;

namespace EpiInfo.CheckCode.Functions;

/// <summary>
/// <c>EPIWEEK(date[, firstDay])</c>: the MMWR epidemiological week of a date.
/// </summary>
/// <remarks>
/// <para>
/// An exact port of the legacy function. The start of the MMWR year is taken from the year before, the
/// year itself or the year after, and the week is the elapsed days from that start divided by 7, plus 1.
/// For a date value, or date text without a time of day (midnight), with the default first day, the argument
/// omitted or 1, this is the MMWR week: weeks start on Sunday, and week 1 is the first week with at least four
/// days in the new year. With a time of day, legacy's arithmetic, kept unchanged, counts whole days by
/// truncation, so the hours of the day before the next MMWR start, less than a whole day before it, fall in
/// week 1 of the next MMWR year: <c>2011-12-31T10:00</c> gives 1, while <c>2011-12-31</c> gives 52.
/// </para>
/// <para>
/// The optional first-day argument is 1-based (1 = Sunday) and is reduced by 1, as in legacy. Another
/// first day keeps legacy's offset arithmetic unchanged: with the reduced first day and the weekday of
/// 1 January both counted from Sunday = 0, the start is 1 January moved by (first day - weekday) days
/// when the weekday is at most first day + 3, and by (7 - first day - weekday) days otherwise. That start
/// need not fall on the named weekday nor keep the four-day rule: <c>EPIWEEK</c> of 2011-01-01 with first
/// day 2 takes Saturday 2011-01-01 as the start of week 1. The first day counts only when it reads as an
/// integer: an exact integer number, an option index, or integer text read with the invariant culture.
/// Any other value, a non-integral number whose nearest double is whole included, keeps the default.
/// </para>
/// <para>
/// The date argument is a date value, or text in one of two families. A year-first numeric date is a year of
/// four or more digits, a month and a day, separated by '-' or '/', as in <c>2011-01-15</c> or <c>2011/1/15</c>.
/// A named-month date is a day, a full Title-case month name other than May and a year of four or more digits, as
/// day, month and year (<c>2 January 2011</c>), month, day and year (<c>January 2, 2011</c>) or year, month and day
/// (<c>2011 January 2</c>), with white space between the parts, or a comma and optional white space before a year
/// that comes last, optionally after a full Title-case weekday name and a comma or white space, as in
/// <c>Sunday, January 2, 2011</c>. Either may be followed, after 'T', a comma or white space, by a time of day:
/// H:mm with optional seconds, an optional '.' or ',' fraction of any length and an optional upper-case AM or PM,
/// or an hour with AM or PM, as in <c>2011/1/15 9:30 PM</c>, <c>2011-01-15T10:30:15.12345678</c> or
/// <c>January 15, 2011 21:30</c>; surrounding white space is allowed. Such text is read with legacy's own lenient
/// <c>DateTime.TryParse</c> in the invariant culture, in the Gregorian calendar with its time of day kept, which is
/// how legacy read it under every Gregorian-calendar culture; text of either shape the parser rejects, such as
/// <c>2011-02-30</c>, <c>February 30, 2011</c> or a weekday that is not the date's, is not a date. Legacy read any
/// present value with the current culture's <c>DateTime.TryParse</c> and returned null, before reading the first
/// day, when that failed. Here every other present value gives that successful null, other text included. Legacy's
/// reading of other text depends on the culture: May (another month under bas, bas-CM, dua and dua-CM), abbreviated
/// month and weekday names (<c>Jan</c> is June under st, st-LS and st-ZA), other casings of the names and a
/// lower-case am or pm (not read under en-US-POSIX), '.' as the date separator, and a numeric date that is not
/// year-first. It also depends on the clock (text without a year, such as <c>1/5</c> or <c>January 2</c>, takes the
/// current year or date), on the server time zone (a <c>Z</c> or offset suffix is converted to local time) and on
/// the calendar (a culture whose calendar is not Gregorian reads even <c>2011-01-01</c> as another date). A small
/// remainder outside both families that legacy reads alike everywhere gives null by the bound of these shapes: a
/// period right after the seconds followed by white space, white space before a comma, a month name run into the
/// year (<c>2 January2011</c>), and a year and a month without a day, alone or followed after a comma, white space
/// or a dash by more, such as <c>2011, 10</c> or <c>2011 - 10</c>, read as 1 October 2011. The gap is recorded in
/// docs/parity-notes.md under "Parity gaps".
/// </para>
/// <para>
/// Departures from legacy. Where legacy threw, the call fails instead: a missing date fails with
/// "EPIWEEK requires a date"; a missing first-day argument fails with "EPIWEEK first-day argument is
/// missing"; and a date whose MMWR computation leaves the supported date range fails with "EPIWEEK date
/// is out of range". A call with no argument, where legacy also threw, or with more than two, where legacy
/// ignored the arguments after the second, fails with "EPIWEEK requires 1 or 2 arguments", following the
/// <c>EPIWEEK(date[, firstDay])</c> signature. Integer text as the first day is read with the invariant
/// culture where legacy used the current one.
/// </para>
/// </remarks>
public static class EpiWeekFunction
{
    /// <summary>The function name, <see cref="FunctionNames.EpiWeek"/>, matched case-insensitively.</summary>
    public const string Name = FunctionNames.EpiWeek;

    // Full Title-case month names, read as the same month in every Gregorian-calendar culture. May is left out because
    // bas, bas-CM, dua and dua-CM read it as another month, abbreviations because st, st-LS and st-ZA read Jan as June,
    // and other casings because en-US-POSIX matches month names case-sensitively.
    private const string MonthName =
        "(?:January|February|March|April|June|July|August|September|October|November|December)";

    // Full Title-case weekday names. A weekday that is not the date's fails the parse, in legacy and here alike.
    private const string WeekdayName = "(?:Sunday|Monday|Tuesday|Wednesday|Thursday|Friday|Saturday)";

    // An optional time of day after 'T', a comma or white space: H:mm with optional :ss and an optional '.' or ','
    // fraction of any length, with an optional upper-case AM or PM, or an hour with AM or PM.
    private const string TimeOfDay =
        @"(?:(?:\s*T\s*|\s*,\s*|\s+)"
        + @"(?:[0-9]{1,2}:[0-9]{1,2}(?::[0-9]{1,2}(?:[.,][0-9]+)?)?(?:\s*(?:AM|PM))?|[0-9]{1,2}\s*(?:AM|PM)))?";

    // The shape of date text whose invariant-culture reading by legacy's lenient DateTime.TryParse is its reading in
    // every culture whose calendar is Gregorian, followed by an optional time of day. A year-first numeric date: a
    // year of four or more digits, a month and a day, with '-' or '/' between them. Or a named-month date, after an
    // optional weekday and a comma or white space: day, month and year, month, day and year, or year, month and day,
    // with white space between the parts, or a comma and optional white space before a year that comes last. Every
    // spelling holds a year of four or more digits and a day, so no reading takes either from the clock. Text values
    // can be long, so the expression runs without backtracking, which keeps matching linear in the length of any text.
    private static readonly Regex UnambiguousDateText = new(
        @"^\s*(?:[0-9]{4,}[-/][0-9]{1,2}[-/][0-9]{1,2}"
        + "|(?:" + WeekdayName + @"(?:,\s*|\s+))?"
        + @"(?:[0-9]{1,2}\s+" + MonthName + @"(?:,\s*|\s+)[0-9]{4,}"
        + "|" + MonthName + @"\s+[0-9]{1,2}(?:,\s*|\s+)[0-9]{4,}"
        + @"|[0-9]{4,}\s+" + MonthName + @"\s+[0-9]{1,2}))"
        + TimeOfDay + @"\s*$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

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

        if (arguments[0].IsNull)
        {
            return FunctionResult.Fail("EPIWEEK requires a date");
        }

        // Legacy returns null for a present value it cannot read as a date, before it reads the first day.
        if (!TryReadDate(arguments[0], out var day))
        {
            return FunctionResult.Ok(FieldValue.Null);
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
    /// Reads the date argument. A date value is that day at midnight. Text that has the shape of
    /// <see cref="UnambiguousDateText"/> is read with legacy's own lenient <c>DateTime.TryParse</c> in the invariant
    /// culture and keeps its time of day as legacy did; text of that shape the parser rejects, such as 2011-02-30 or
    /// February 30, 2011, does not read as a date. Text of any other shape, and every other kind, does not read as a
    /// date.
    /// </summary>
    private static bool TryReadDate(FieldValue argument, out DateTime value)
    {
        switch (argument.Kind)
        {
            case FieldValueKind.Date:
                value = argument.AsDate().ToDateTime(TimeOnly.MinValue);
                return true;

            case FieldValueKind.Text:
                var text = argument.AsText();
                if (UnambiguousDateText.IsMatch(text))
                {
                    return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
                }

                value = default;
                return false;

            default:
                // Legacy's reading of a number's, boolean's or option index's text found no date in any culture
                // (5, True), or a culture- and clock-dependent one (1.5 as a day of the current year).
                value = default;
                return false;
        }
    }

    /// <summary>
    /// Reads the 1-based first day the way legacy's <c>int.TryParse</c> of the value's text did: an exact
    /// 32-bit integer number, an option index, or integer text read with the invariant culture. Any other value,
    /// a non-integral number included, does not read as an integer and keeps the default.
    /// </summary>
    private static bool TryReadFirstDay(FieldValue argument, out int value)
    {
        value = 0;
        switch (argument.Kind)
        {
            case FieldValueKind.Number:
                // The exact integer, never the double, which can round a non-integral number into a whole one.
                if (argument.TryGetInteger(out var integer) && integer is >= int.MinValue and <= int.MaxValue)
                {
                    value = (int)integer;
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
