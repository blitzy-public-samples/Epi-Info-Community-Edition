using EpiInfo.CheckCode.Functions;
using EpiInfo.Domain.Values;

namespace EpiInfo.CheckCode.Tests.Functions;

/// <summary>
/// The <c>YEARS</c> port of legacy <c>Rule_Years.cs</c> and its lookup through <see cref="FunctionRegistry"/>.
/// Every expected count was cross-checked with <c>years()</c> in <c>testdata/tools/epi_functions.py</c>.
/// </summary>
public sealed class YearsFunctionTests
{
    [Theory]
    [InlineData(1986, 1, 1, 2011, 5, 11, 25)] // EC-AGE-01
    [InlineData(2001, 9, 1, 2011, 6, 15, 9)] // EC-AGE-02: 9.79 elapsed years give the completed 9, not a rounded 10
    [InlineData(2000, 2, 29, 2011, 2, 28, 11)] // EC-AGE-03: kept legacy defect; a birthday-based age is 10
    [InlineData(2011, 6, 15, 2011, 6, 15, 0)] // EC-AGE-04
    [InlineData(2011, 5, 11, 1986, 1, 1, -25)] // the first date is later, so the count is negated
    [InlineData(2000, 2, 29, 2010, 2, 28, 10)]
    [InlineData(2000, 1, 1, 2011, 1, 1, 11)]
    public void TwoDatesGiveTheLegacyYearCount(
        int firstYear, int firstMonth, int firstDay, int secondYear, int secondMonth, int secondDay, int expected)
    {
        var result = YearsFunction.Invoke([Date(firstYear, firstMonth, firstDay), Date(secondYear, secondMonth, secondDay)]);

        AssertNumber(expected, result);
    }

    [Fact]
    public void LeapDayBirthDateKeepsLegacyResultOfEleven()
    {
        // A kept legacy defect, recorded under "Legacy defects kept" in docs/parity-notes.md: the 4,017-day
        // span equals calendar years 1 to 11, so the tick-based count gives 11 before the eleventh birthday.
        // Changing YEARS to a birthday-based age (10) breaks parity with stored legacy ages and must fail here.
        var result = YearsFunction.Invoke([Date(2000, 2, 29), Date(2011, 2, 28)]);

        AssertNumber(11, result);
    }

    [Fact]
    public void MissingSecondDateGivesNull()
    {
        // EC-AGE-05: a missing interview date assigns null.
        AssertSuccessfulNull(YearsFunction.Invoke([Date(2011, 1, 20), FieldValue.Null]));
    }

    [Fact]
    public void MissingFirstDateGivesNull()
    {
        AssertSuccessfulNull(YearsFunction.Invoke([FieldValue.Null, Date(2011, 5, 11)]));
    }

    [Fact]
    public void TextArgumentGivesNullAndIsNotParsedAsADate()
    {
        // Legacy returns null unless both arguments are already dates, even when the text looks like one.
        AssertSuccessfulNull(YearsFunction.Invoke([FieldValue.FromText("2000-01-01"), Date(2011, 1, 1)]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void WrongArgumentCountFailsWithoutThrowing(int argumentCount)
    {
        FieldValue[] available = [Date(2000, 1, 1), Date(2011, 1, 1), Date(2012, 1, 1)];
        IReadOnlyList<FieldValue> arguments = available[..argumentCount];

        FunctionResult result = default;
        var exception = Record.Exception(() => result = YearsFunction.Invoke(arguments));

        Assert.Null(exception);
        Assert.False(result.IsSuccess);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Theory]
    [InlineData("YEARS")]
    [InlineData("years")]
    [InlineData("EpiWeek")]
    [InlineData("EPIWEEK")]
    public void SupportedFunctionNamesMatchCaseInsensitively(string name)
    {
        Assert.True(FunctionRegistry.IsSupported(name));
    }

    [Theory]
    [InlineData("DAYS")]
    [InlineData("SUBSTRING")]
    [InlineData("")]
    public void OtherFunctionNamesAreNotSupported(string name)
    {
        Assert.False(FunctionRegistry.IsSupported(name));
    }

    [Fact]
    public void RegistryResolvesYearsToTheYearsFunction()
    {
        Assert.Equal("YEARS", YearsFunction.Name);
        Assert.True(FunctionRegistry.TryGet("years", out var function));
        Assert.NotNull(function);

        IReadOnlyList<FieldValue> arguments = [Date(1986, 1, 1), Date(2011, 5, 11)];

        Assert.Equal(YearsFunction.Invoke(arguments), function(arguments));
    }

    [Fact]
    public void RegistryDoesNotResolveAnUnsupportedFunction()
    {
        Assert.False(FunctionRegistry.TryGet("DAYS", out _));
    }

    private static FieldValue Date(int year, int month, int day) => FieldValue.FromDate(new DateOnly(year, month, day));

    private static void AssertNumber(int expected, FunctionResult result)
    {
        Assert.True(result.IsSuccess);
        Assert.Null(result.Error);
        Assert.Equal(FieldValueKind.Number, result.Value.Kind);
        Assert.True(result.Value.TryGetNumber(out var number));
        Assert.Equal((double)expected, number);
    }

    private static void AssertSuccessfulNull(FunctionResult result)
    {
        Assert.True(result.IsSuccess);
        Assert.Null(result.Error);
        Assert.Equal(FieldValueKind.Null, result.Value.Kind);
    }
}
