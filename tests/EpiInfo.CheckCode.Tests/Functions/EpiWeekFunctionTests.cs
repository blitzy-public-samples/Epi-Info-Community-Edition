using EpiInfo.CheckCode.Functions;
using EpiInfo.Domain.Values;

namespace EpiInfo.CheckCode.Tests.Functions;

/// <summary>
/// Tests of the <c>EPIWEEK</c> port. Expected weeks were cross-checked with
/// <c>testdata/tools/epi_functions.py</c> <c>epiweek()</c>, which takes the first day already reduced by 1.
/// </summary>
public sealed class EpiWeekFunctionTests
{
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

    // The first-day argument is 1-based and reduced by 1, as legacy does: 1 is Sunday, 2 is Monday.
    [Theory]
    [InlineData(2012, 12, 30, 1, 1)]
    [InlineData(2012, 12, 30, 2, 52)]
    [InlineData(2011, 1, 1, 2, 1)]
    [InlineData(2015, 1, 3, 2, 1)]
    public void FirstDayArgumentShiftsTheWeekStart(int year, int month, int day, int firstDay, int expectedWeek)
    {
        var result = EpiWeekFunction.Invoke([Date(year, month, day), FieldValue.FromNumber(firstDay)]);

        AssertWeek(result, expectedWeek);
    }

    [Fact]
    public void FirstDayOneEqualsTheDefault()
    {
        var withFirstDay = EpiWeekFunction.Invoke([Date(2012, 12, 30), FieldValue.FromNumber(1)]);
        var withoutFirstDay = EpiWeekFunction.Invoke([Date(2012, 12, 30)]);

        Assert.Equal(withoutFirstDay, withFirstDay);
    }

    // Legacy throws from new DateTime(0, 1, 1) on a missing date; the port fails instead. The evaluator
    // turns that failure into an error diagnostic and skips the statement, which Evaluation/EvaluatorTests
    // and DiagnosticsTests cover.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingDateFailsWithoutThrowing(bool withFirstDay)
    {
        IReadOnlyList<FieldValue> arguments = withFirstDay
            ? [FieldValue.Null, FieldValue.FromNumber(1)]
            : [FieldValue.Null];
        FunctionResult result = default;

        var exception = Record.Exception(() => result = EpiWeekFunction.Invoke(arguments));

        Assert.Null(exception);
        Assert.False(result.IsSuccess);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    private static FieldValue Date(int year, int month, int day) => FieldValue.FromDate(new DateOnly(year, month, day));

    private static void AssertWeek(FunctionResult result, int expected)
    {
        Assert.True(result.IsSuccess);
        Assert.Null(result.Error);
        Assert.Equal(FieldValueKind.Number, result.Value.Kind);
        Assert.True(result.Value.TryGetNumber(out var week));
        Assert.Equal((double)expected, week);
    }
}
