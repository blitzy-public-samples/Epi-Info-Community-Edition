using System.Globalization;
using EpiInfo.CheckCode.Functions;
using EpiInfo.Domain.Events;
using EpiInfo.Domain.Metadata;
using EpiInfo.Domain.Values;

namespace EpiInfo.CheckCode.Tests.Functions;

/// <summary>
/// Tests of the <c>EPIWEEK</c> port. Expected weeks were cross-checked with
/// <c>testdata/tools/epi_functions.py</c> <c>epiweek()</c>, which takes the first day already reduced by 1.
/// </summary>
/// <remarks>
/// Arguments of other kinds are written as <c>kind:text</c> and built by <see cref="Argument"/>, so each theory
/// row stays a primitive and reports as its own test case.
/// </remarks>
public sealed class EpiWeekFunctionTests
{
    // Synthetic: the seeded program passes only the Date field OnsetDate, so text arguments need their own form.
    private static readonly FormMetadata WeekForm = new(
        ViewId: 99,
        Name: "Synthetic",
        BaseTableName: "Synthetic",
        Pages: [new PageMetadata(1, "Page 1", 0, "Synthetic1")],
        Fields:
        [
            Field(1, "OnsetDate", 7, "Date", 1, "datetime", 8),
            Field(2, "OnsetWeek", 5, "Number", 2, "float", 8, readOnly: true),
            Field(3, "LastName", 1, "Text", 3, "nvarchar", 8000),
        ]);

    [Theory]
    [InlineData(2011, 1, 1, 52)] // EC-WEEK-01: belongs to MMWR year 2010
    [InlineData(2011, 1, 2, 1)] // EC-WEEK-02
    [InlineData(2011, 5, 4, 18)] // EC-WEEK-03
    [InlineData(2012, 12, 29, 52)] // EC-WEEK-04
    [InlineData(2012, 12, 30, 1)] // EC-WEEK-05: week 1 of MMWR year 2013
    [InlineData(2011, 6, 15, 24)]
    [InlineData(2015, 1, 3, 53)] // MMWR year 2014 has 53 weeks
    [InlineData(2015, 1, 4, 1)]
    public void DateAloneGivesTheMmwrWeekStartingSunday(int year, int month, int day, int expectedWeek)
    {
        var result = EpiWeekFunction.Invoke([Date(year, month, day)]);

        AssertWeek(result, expectedWeek);
    }

    // Text holding a year-first numeric date ('-' or '/'), or a day, a full Title-case month name other than May and a
    // year in one of three orders, optionally after a full Title-case weekday name, optionally with a time of day
    // after 'T', a comma or white space, reads as legacy's lenient DateTime.TryParse read it in every
    // Gregorian-calendar culture: the time of day included, a fraction of any length rounded to ticks, an upper-case
    // AM or PM, a year of more than four digits.
    // For 2011-12-31T10:00 the next MMWR start, 2012-01-01, is 14 hours later, which is 0 whole days, so legacy's "more
    // than 0 days" test takes that start; the elapsed -14 hours are 0 whole days, because TimeSpan.Days truncates, so
    // the week is 1. epi_functions.py floors the negative span to -1 day and gives 0 there; the port follows legacy's
    // truncation.
    [Theory]
    [InlineData("2011-01-01", 52)] // EC-WEEK-01 as text
    [InlineData("2011-1-2", 1)]
    [InlineData(" 2011-05-04 ", 18)]
    [InlineData("2012-12-30T00:00:00", 1)]
    [InlineData("2011-05-04 10:30", 18)]
    [InlineData("2011-05-04 1:05:09.5", 18)]
    [InlineData("2011-05-04T10:30:15.1234567", 18)]
    [InlineData("2011-12-31", 52)]
    [InlineData("2011-12-31T10:00", 1)]
    [InlineData("2011-12-31T10:00:00.12345678", 1)] // eight fraction digits, rounded to ticks
    [InlineData("2011/01/02", 1)]
    [InlineData("2011-01-02 10:00 AM", 1)]
    [InlineData("2011-05-04 10 PM", 18)]
    [InlineData("2011-05-04,10:00", 18)]
    [InlineData("2011-05-04\t10:00:00,5", 18)]
    [InlineData("02011-01-02", 1)]
    [InlineData("2011-05-04 T 10:00", 18)]
    [InlineData("2011-05-04\u00A010:00", 18)] // no-break space before the time
    [InlineData("January 2, 2011", 1)]
    [InlineData("2 January 2011", 1)]
    [InlineData("January 2,2011", 1)]
    [InlineData("2011 December 31", 52)]
    [InlineData("Saturday, December 31, 2011", 52)]
    [InlineData("Sunday,January 2,2011 10:00 AM", 1)]
    [InlineData("February 28, 2011 13:00", 9)]
    public void UnambiguousDateTextGivesTheWeekOfThatDateAndTime(string text, int expectedWeek)
    {
        var result = EpiWeekFunction.Invoke([FieldValue.FromText(text)]);

        AssertWeek(result, expectedWeek);
    }

    // Legacy returns null for a present value its DateTime.TryParse cannot read, before it reads the first day, and
    // the port does the same for text of the shapes above it cannot read (2011-02-30, a one-digit hour after 'T', a
    // weekday that is not the date's). The port reads only the spellings above, so other text gives null too,
    // including text whose legacy reading depended on the culture (01/02/2011, '.' as the date separator, May, an
    // abbreviated or lower-case month name, a lower-case pm), the clock (1.5, 1/5, January 2 and January 2 10:00 AM
    // take the current year or date) or the server time zone (a Z or offset suffix); docs/parity-notes.md records
    // this under "Parity gaps".
    [Theory]
    [InlineData("text:not a date")]
    [InlineData("text:")]
    [InlineData("text:01/02/2011")]
    [InlineData("text:2011-01-01T10:00:00Z")]
    [InlineData("text:2011-02-30")]
    [InlineData("text:1.5")]
    [InlineData("text:2011-01-01T1:05")]
    [InlineData("text:2011-01-01T10:00:00.")]
    [InlineData("text:2011.01.02")]
    [InlineData("text:May 5, 2011")] // 5 November under bas and bas-CM, 5 October under dua and dua-CM
    [InlineData("text:Jan 2, 2011")] // 2 June under st, st-LS and st-ZA
    [InlineData("text:january 2, 2011")] // not a date under en-US-POSIX
    [InlineData("text:January 2")]
    [InlineData("text:January 2 10:00 AM")]
    [InlineData("text:Monday, January 2, 2011")] // 2011-01-02 is a Sunday, so no culture reads it
    [InlineData("text:2011-01-02 10:00 pm")]
    [InlineData("text:2011-01-01T10:00:00+02:00")]
    [InlineData("text:1/5")]
    [InlineData("number:5")]
    [InlineData("number:1.5")]
    [InlineData("boolean:true")]
    [InlineData("index:0")]
    public void PresentValueThatIsNotADateGivesNull(string date)
    {
        AssertNullWeek(EpiWeekFunction.Invoke([Argument(date)]));

        // Legacy returned before reading the first day, so a missing first day does not fail this call.
        AssertNullWeek(EpiWeekFunction.Invoke([Argument(date), FieldValue.Null]));
    }

    // A text value can be long, so the date-text test runs without backtracking: a date followed by a million spaces
    // and a character that ends the match is rejected in linear time and gives a successful null.
    [Fact]
    public void LongTextThatIsNotADateGivesNull()
    {
        var text = "2011-01-01" + new string(' ', 1_000_000) + "x";

        AssertNullWeek(EpiWeekFunction.Invoke([FieldValue.FromText(text)]));
    }

    // The first-day argument is 1-based and reduced by 1, as legacy does: 1 is Sunday, 2 is Monday. Legacy read it
    // with int.TryParse of the value's text, so only an exact integer, an option index or integer text moves the
    // start; any other value keeps the default first day, Sunday, which gives week 52 for 2011-01-01.
    [Theory]
    [InlineData(2012, 12, 30, "number:1", 1)]
    [InlineData(2012, 12, 30, "number:2", 52)]
    [InlineData(2011, 1, 1, "number:2", 1)]
    [InlineData(2015, 1, 3, "number:2", 1)]
    [InlineData(2011, 1, 1, "number:3", 1)] // Tuesday first day: legacy starts MMWR 2011 on Friday 2010-12-31
    [InlineData(2011, 5, 4, "number:2", 18)] // Monday first day: legacy starts MMWR 2011 on Saturday 2011-01-01
    [InlineData(2011, 1, 1, "integer:2", 1)]
    [InlineData(2011, 1, 1, "index:2", 1)]
    [InlineData(2011, 1, 1, "text:2", 1)]
    [InlineData(2011, 1, 1, "text:abc", 52)]
    [InlineData(2011, 1, 1, "number:2.5", 52)]
    [InlineData(2011, 1, 1, "exact:2.0000000000000001", 52)] // non-integral, although its nearest double is 2
    [InlineData(2011, 1, 1, "number:3e9", 52)] // an integer beyond the 32-bit range
    [InlineData(2011, 1, 1, "boolean:true", 52)]
    [InlineData(2011, 1, 1, "date:2011-01-02", 52)]
    public void FirstDayArgumentShiftsTheWeekStart(int year, int month, int day, string firstDay, int expectedWeek)
    {
        var result = EpiWeekFunction.Invoke([Date(year, month, day), Argument(firstDay)]);

        AssertWeek(result, expectedWeek);
    }

    [Fact]
    public void FirstDayOneEqualsTheDefault()
    {
        var withFirstDay = EpiWeekFunction.Invoke([Date(2012, 12, 30), FieldValue.FromNumber(1)]);
        var withoutFirstDay = EpiWeekFunction.Invoke([Date(2012, 12, 30)]);

        Assert.Equal(withoutFirstDay, withFirstDay);
    }

    // Legacy throws from new DateTime(0, 1, 1) on a missing date, from ToString() on a missing first day, and from
    // the neighbouring-year arithmetic for a date in year 1 or 9999; the port fails instead. Calls outside the
    // EPIWEEK(date[, firstDay]) signature fail the same way. The evaluator turns a failure into an error
    // diagnostic and skips the statement, which Evaluation/EvaluatorTests and DiagnosticsTests cover. The reason
    // becomes client diagnostic text, so it names the function and the problem but never an argument: each row
    // expects the whole fixed text, which is what proves that no argument value is disclosed.
    [Theory]
    [InlineData("EPIWEEK requires a date", "null:")]
    [InlineData("EPIWEEK requires a date", "null:", "number:1")]
    [InlineData("EPIWEEK first-day argument is missing", "date:2011-01-01", "null:")]
    [InlineData("EPIWEEK date is out of range", "date:0001-01-01")]
    [InlineData("EPIWEEK date is out of range", "date:0001-12-31")]
    [InlineData("EPIWEEK date is out of range", "date:9999-01-01")]
    [InlineData("EPIWEEK date is out of range", "date:9999-12-31")]
    [InlineData("EPIWEEK date is out of range", "text:9999-12-31")]
    [InlineData("EPIWEEK requires 1 or 2 arguments")]
    [InlineData("EPIWEEK requires 1 or 2 arguments", "date:2011-01-01", "number:1", "number:1")]
    public void InvalidCallFailsWithoutThrowing(string expectedMessage, params string[] arguments)
    {
        FieldValue[] values = [.. arguments.Select(Argument)];
        FunctionResult result = default;

        var exception = Record.Exception(() => result = EpiWeekFunction.Invoke(values));

        Assert.Null(exception);
        Assert.False(result.IsSuccess);
        Assert.Equal(expectedMessage, result.Error);
        Assert.Equal(FieldValue.Null, result.Value);
    }

    // Parsing and evaluation deliver both arguments: a Date field with a number literal as the first day, a Text
    // field, and string literals. epi_functions.py epiweek(datetime(2012, 12, 30), 1) is 52.
    [Theory]
    [InlineData("OnsetDate", "date:2012-12-30", "EPIWEEK(OnsetDate, 2)", 52)]
    [InlineData("OnsetDate", "date:2012-12-30", "EPIWEEK(OnsetDate)", 1)]
    [InlineData("LastName", "text:2011-01-02", "EPIWEEK(LastName)", 1)]
    [InlineData("LastName", "text:not a date", "EPIWEEK(LastName)", null)]
    [InlineData("LastName", "text:Smith", "EPIWEEK(\"2011-01-01\", \"2\")", 1)]
    public void EngineDeliversTheArgumentsToEpiWeek(string field, string value, string call, int? expectedWeek)
    {
        var program = $"""
            Field {field}
                After
                    ASSIGN OnsetWeek = {call}
                End-After
            End-Field
            """;
        var values = new Dictionary<string, FieldValue> { [field] = Argument(value) };

        var response = ((ICheckCodeEngine)new CheckCodeEngine()).Run(
            program,
            WeekForm,
            FieldEventKind.FieldAfter,
            field,
            values);

        Assert.Empty(response.Diagnostics);
        var assigned = Assert.Single(response.Assigned);
        Assert.Equal("OnsetWeek", assigned.Key);
        Assert.Equal(expectedWeek is { } week ? FieldValue.FromNumber(week) : FieldValue.Null, assigned.Value);
    }

    private static FieldValue Date(int year, int month, int day) => FieldValue.FromDate(new DateOnly(year, month, day));

    // Builds an argument from "kind:text". A number is a double, as a float column holds it; an exact number is
    // parsed as a request number is, so a non-integral number stays non-integral; a date is yyyy-MM-dd; null takes
    // no text.
    private static FieldValue Argument(string spec)
    {
        var separator = spec.IndexOf(':', StringComparison.Ordinal);
        if (separator < 0)
        {
            throw new ArgumentException("An argument is written as kind:text.", nameof(spec));
        }

        var text = spec[(separator + 1)..];
        return spec[..separator] switch
        {
            "text" => FieldValue.FromText(text),
            "number" => FieldValue.FromNumber(double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture)),
            "exact" => FieldValueCoercion.TryParseNumber(text, out var exact)
                ? exact
                : throw new ArgumentException("An exact number argument must be a finite number.", nameof(spec)),
            "integer" => FieldValue.FromInteger(long.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture)),
            "boolean" => FieldValue.FromBoolean(bool.Parse(text)),
            "date" => FieldValue.FromDate(
                DateOnly.ParseExact(text, FieldValue.IsoDateFormat, CultureInfo.InvariantCulture)),
            "index" => FieldValue.FromIndex(int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture)),
            "null" when text.Length == 0 => FieldValue.Null,
            _ => throw new ArgumentOutOfRangeException(nameof(spec), spec, "Unknown argument kind."),
        };
    }

    // Column metadata comes only from FieldTypeMapper.DescribeColumn, the one owner of the column-family rules.
    private static FieldMetadata Field(
        int id,
        string name,
        int typeId,
        string typeName,
        decimal tab,
        string sqlType,
        int bytes,
        bool readOnly = false)
    {
        return new FieldMetadata
        {
            FieldId = id,
            Name = name,
            FieldTypeId = typeId,
            FieldTypeName = typeName,
            PageId = 1,
            TabIndex = tab,
            IsReadOnly = readOnly,
            Column = FieldTypeMapper.DescribeColumn(name, sqlType, bytes),
        };
    }

    private static void AssertWeek(FunctionResult result, int expected)
    {
        Assert.True(result.IsSuccess);
        Assert.Null(result.Error);
        Assert.Equal(FieldValueKind.Number, result.Value.Kind);
        Assert.True(result.Value.TryGetNumber(out var week));
        Assert.Equal((double)expected, week);
    }

    private static void AssertNullWeek(FunctionResult result)
    {
        Assert.True(result.IsSuccess);
        Assert.Null(result.Error);
        Assert.Equal(FieldValueKind.Null, result.Value.Kind);
    }
}
