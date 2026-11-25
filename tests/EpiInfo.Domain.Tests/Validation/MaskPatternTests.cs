using System.Globalization;
using EpiInfo.Domain.Forms;
using EpiInfo.Domain.Metadata;
using EpiInfo.Domain.Validation;
using EpiInfo.Domain.Values;

namespace EpiInfo.Domain.Tests.Validation;

/// <summary>
/// Unit tests of <see cref="MaskPattern"/>, the server's save-time check of <c>metaFields.Pattern</c>, over the
/// seeded <c>metaPatterns</c> expressions.
/// </summary>
/// <remarks>
/// These tests pin the target pattern rule, which is deliberately stronger than legacy Epi Info: they are not
/// legacy parity. Legacy only loaded the pattern into a WinForms <c>MaskedTextBox</c> entry mask
/// (<c>Epi.Windows.Enter/PresentationLogic/ControlFactory.cs</c>, lines 379-393), where <c>#</c> is an optional
/// digit, space or sign position, and never checked the pattern on save. It therefore accepted partial entries
/// and values such as the seeded <c>(555) 0148-6367</c>, which these tests expect the server to reject. The
/// difference is recorded in <c>docs/parity-notes.md</c>.
/// </remarks>
public sealed class MaskPatternTests
{
    private const string PhoneMask = "###-###-####";
    private const string ShortPhoneMask = "###-####";
    private const string TemperatureMask = "###.#";
    private const string DateMask = "MM-DD-YYYY";

    // 18 and 19 integer positions: 999999999999999999 has 18 digits, and long.MaxValue and 1E+18 have 19.
    private const string EighteenDigitMask = "##################";
    private const string NineteenDigitMask = "###################";

    [Theory]
    [InlineData(PhoneMask, FieldType.Text)]
    [InlineData(ShortPhoneMask, FieldType.Text)]
    [InlineData(PhoneMask, FieldType.Multiline)]
    [InlineData(ShortPhoneMask, FieldType.LegalValues)]
    public void Classify_DigitMaskOnTextFamilyField_IsTextDigitMask(string pattern, FieldType fieldType)
    {
        Assert.Equal(MaskPatternKind.TextDigitMask, MaskPattern.Classify(pattern, fieldType));
        Assert.Equal(MaskPatternKind.TextDigitMask, MaskPattern.Parse(pattern, fieldType, ColumnFamily.Character).Kind);
    }

    /// <summary>Every seeded number pattern of <c>metaPatterns</c>, and the seeded <c>FeverTemp</c> pattern.</summary>
    [Theory]
    [InlineData("#")]
    [InlineData("##")]
    [InlineData("###")]
    [InlineData("####")]
    [InlineData("##.##")]
    [InlineData("##.###")]
    [InlineData(TemperatureMask)]
    public void Classify_NumberMaskOnNumberField_IsNumberMask(string pattern)
    {
        Assert.Equal(MaskPatternKind.NumberMask, MaskPattern.Classify(pattern, FieldType.Number));
        Assert.Equal(MaskPatternKind.NumberMask, MaskPattern.Parse(pattern, FieldType.Number, ColumnFamily.Float).Kind);
        Assert.Equal(MaskPatternKind.NumberMask, MaskPattern.Parse(pattern, FieldType.Number, ColumnFamily.Integer).Kind);
    }

    /// <summary>
    /// A date pattern is a display and entry format for the client only, even when written as a <c>#</c> mask,
    /// and any pattern of a shape the rule does not recognise gets no server check.
    /// </summary>
    [Theory]
    [InlineData(DateMask, FieldType.Date)]
    [InlineData("YYYY-MM-DD", FieldType.Date)]
    [InlineData("##-##-####", FieldType.Date)]
    [InlineData("AAA", FieldType.Text)]
    [InlineData(DateMask, FieldType.Text)]
    [InlineData("---", FieldType.Text)]
    [InlineData("AAA", FieldType.Number)]
    [InlineData(PhoneMask, FieldType.Number)]
    [InlineData("##.", FieldType.Number)]
    [InlineData(".##", FieldType.Number)]
    [InlineData("##.#.#", FieldType.Number)]
    public void Classify_OtherPattern_IsUnchecked(string pattern, FieldType fieldType)
    {
        Assert.Equal(MaskPatternKind.Unchecked, MaskPattern.Classify(pattern, fieldType));
        Assert.Equal(MaskPatternKind.Unchecked, MaskPattern.Parse(pattern, fieldType, ColumnFamily.Float).Kind);
    }

    /// <summary>
    /// A null, empty or whitespace-only pattern is no pattern, and patterns never apply to checkbox, option,
    /// label, group or unsupported fields.
    /// </summary>
    [Theory]
    [InlineData(null, FieldType.Text)]
    [InlineData(null, FieldType.Number)]
    [InlineData("", FieldType.Text)]
    [InlineData("", FieldType.Date)]
    [InlineData("   ", FieldType.Text)]
    [InlineData(" \t", FieldType.Number)]
    [InlineData("###", FieldType.Checkbox)]
    [InlineData("###", FieldType.Option)]
    [InlineData("###", FieldType.Label)]
    [InlineData("###", FieldType.Group)]
    [InlineData(PhoneMask, FieldType.Unsupported)]
    public void Classify_BlankPatternOrFieldTypeWithoutPatterns_IsNone(string? pattern, FieldType fieldType)
    {
        Assert.Equal(MaskPatternKind.None, MaskPattern.Classify(pattern, fieldType));
        Assert.Equal(MaskPatternKind.None, MaskPattern.Parse(pattern, fieldType, ColumnFamily.Float).Kind);
    }

    /// <summary>A number mask keeps its digit budget: the count of <c>#</c> before the point and after it.</summary>
    [Theory]
    [InlineData("###", 3, 0)]
    [InlineData(TemperatureMask, 3, 1)]
    [InlineData("##.##", 2, 2)]
    public void Parse_NumberMask_IsCheckedWithItsDigitBudget(string pattern, int maxIntegerDigits, int maxDecimalDigits)
    {
        ParsedMask mask = MaskPattern.Parse(pattern, FieldType.Number, ColumnFamily.Float);

        Assert.Equal(MaskPatternKind.NumberMask, mask.Kind);
        Assert.True(mask.IsChecked);
        Assert.Equal(pattern, mask.Pattern);
        Assert.Equal(maxIntegerDigits, mask.MaxIntegerDigits);
        Assert.Equal(maxDecimalDigits, mask.MaxDecimalDigits);
    }

    [Fact]
    public void Parse_TextDigitMask_IsCheckedWithNoDigitBudget()
    {
        ParsedMask mask = MaskPattern.Parse(PhoneMask, FieldType.Text, ColumnFamily.Character);

        Assert.Equal(MaskPatternKind.TextDigitMask, mask.Kind);
        Assert.True(mask.IsChecked);
        Assert.Equal(PhoneMask, mask.Pattern);
        Assert.Equal(0, mask.MaxIntegerDigits);
        Assert.Equal(0, mask.MaxDecimalDigits);
    }

    /// <summary>A date pattern, an unrecognised pattern and a blank one get no server check.</summary>
    [Theory]
    [InlineData(DateMask, FieldType.Date, ColumnFamily.DateTime, MaskPatternKind.Unchecked)]
    [InlineData("AAA", FieldType.Text, ColumnFamily.Character, MaskPatternKind.Unchecked)]
    [InlineData("", FieldType.Text, ColumnFamily.Character, MaskPatternKind.None)]
    [InlineData(null, FieldType.Number, ColumnFamily.Float, MaskPatternKind.None)]
    public void Parse_PatternWithoutServerCheck_IsNotChecked(
        string? pattern,
        FieldType fieldType,
        ColumnFamily columnFamily,
        MaskPatternKind expectedKind)
    {
        ParsedMask mask = MaskPattern.Parse(pattern, fieldType, columnFamily);

        Assert.Equal(expectedKind, mask.Kind);
        Assert.False(mask.IsChecked);
        Assert.Equal(pattern, mask.Pattern);
        Assert.Equal(0, mask.MaxIntegerDigits);
        Assert.Equal(0, mask.MaxDecimalDigits);
    }

    /// <summary>
    /// The record validator parses a field's mask once and checks every submitted value with it; the parsed mask
    /// decides as the static check does.
    /// </summary>
    [Fact]
    public void ParsedMask_ReusedForSeveralValues_DecidesAsTheStaticCheck()
    {
        ParsedMask temperature = MaskPattern.Parse(TemperatureMask, FieldType.Number, ColumnFamily.Float);
        ParsedMask phone = MaskPattern.Parse(PhoneMask, FieldType.Text, ColumnFamily.Character);

        Assert.True(temperature.IsSatisfiedBy(FieldValue.FromNumber(37.5d)));
        Assert.False(temperature.IsSatisfiedBy(FieldValue.FromNumber(37.25d)));
        Assert.True(temperature.IsSatisfiedBy(FieldValue.Null));
        Assert.True(phone.IsSatisfiedBy(FieldValue.FromText("555-014-8636")));
        Assert.False(phone.IsSatisfiedBy(FieldValue.FromText("(555) 0148-6367")));
    }

    [Theory]
    [InlineData(PhoneMask, "555-014-8636")]
    [InlineData(ShortPhoneMask, "014-8636")]
    public void TextMask_ValueFittingMaskExactly_IsAccepted(string mask, string text)
    {
        Assert.True(MaskPattern.MatchesTextMask(mask, text));
        Assert.True(MaskPattern.IsSatisfiedBy(mask, FieldType.Text, ColumnFamily.Character, FieldValue.FromText(text)));
    }

    [Theory]
    [InlineData(PhoneMask, "5550148636")] // the literals are missing
    [InlineData(PhoneMask, "555-014-863")] // too short
    [InlineData(PhoneMask, "(555) 0148-6367")] // the shape of the seeded historical phone values
    [InlineData(PhoneMask, "555-014-86367")] // too long
    [InlineData(PhoneMask, "555a014-8636")] // a letter in place of a literal
    [InlineData(PhoneMask, "55a-014-8636")] // a letter at a '#'
    [InlineData(PhoneMask, "555-014- 636")] // a space at a '#', which the legacy entry mask allowed
    [InlineData(PhoneMask, "555-014-+636")] // a sign at a '#', which the legacy entry mask allowed
    [InlineData(PhoneMask, "555.014.8636")] // the wrong literal
    [InlineData(PhoneMask, " 555-014-8636")] // nothing is trimmed
    [InlineData(PhoneMask, "")]
    [InlineData(ShortPhoneMask, "0148636")]
    [InlineData(ShortPhoneMask, "014-863")]
    public void TextMask_ValueNotFittingMask_IsRejected(string mask, string text)
    {
        Assert.False(MaskPattern.MatchesTextMask(mask, text));
        Assert.False(MaskPattern.IsSatisfiedBy(mask, FieldType.Text, ColumnFamily.Character, FieldValue.FromText(text)));
    }

    /// <summary>Arabic-Indic and full-width digits at every <c>#</c> position, with ASCII hyphens.</summary>
    [Theory]
    [InlineData("\u0665\u0665\u0665-\u0660\u0661\u0664-\u0668\u0666\u0663\u0666")]
    [InlineData("\uFF15\uFF15\uFF15-\uFF10\uFF11\uFF14-\uFF18\uFF16\uFF13\uFF16")]
    public void TextMask_NonAsciiDigitsAtDigitPositions_AreRejected(string text)
    {
        // Each character is a Unicode decimal digit, so a char.IsDigit check alone would accept the value.
        Assert.All(text.Where(c => c != '-'), c => Assert.True(char.IsDigit(c)));

        Assert.False(MaskPattern.MatchesTextMask(PhoneMask, text));
        Assert.False(
            MaskPattern.IsSatisfiedBy(PhoneMask, FieldType.Text, ColumnFamily.Character, FieldValue.FromText(text)));
    }

    [Fact]
    public void MatchesTextMask_NullArgument_Throws()
    {
        Assert.Throws<ArgumentNullException>("mask", () => MaskPattern.MatchesTextMask(null!, "555-014-8636"));
        Assert.Throws<ArgumentNullException>("text", () => MaskPattern.MatchesTextMask(PhoneMask, null!));
    }

    /// <summary>A leading sign is not a digit and does not count against the integer digits.</summary>
    [Theory]
    [InlineData("###", 120d)]
    [InlineData("###", 0d)]
    [InlineData("###", 5d)]
    [InlineData("###", 999d)]
    [InlineData("###", -120d)]
    [InlineData(TemperatureMask, 37.5d)]
    [InlineData(TemperatureMask, 37d)]
    [InlineData(TemperatureMask, 110d)]
    [InlineData(TemperatureMask, 0d)]
    [InlineData(TemperatureMask, -37.5d)]
    [InlineData("##.###", 12.345d)]
    [InlineData("##.##", 12.34d)]
    [InlineData("##.##", 5d)]
    public void NumberMask_NumberWithinDigitBudget_IsAccepted(string mask, double number)
    {
        Assert.True(MaskPattern.MatchesNumberMask(mask, number));
        Assert.True(MaskPattern.IsSatisfiedBy(mask, FieldType.Number, ColumnFamily.Float, FieldValue.FromNumber(number)));
    }

    [Theory]
    [InlineData("###", 1000d)] // too many integer digits
    [InlineData("###", -1000d)] // too many integer digits after the sign
    [InlineData("###", 12.5d)] // the mask has no decimal positions
    [InlineData(TemperatureMask, 37.25d)] // too many decimal digits
    [InlineData(TemperatureMask, 1000.1d)] // too many integer digits
    [InlineData("#", 10d)]
    [InlineData("##.##", 123d)] // too many integer digits
    [InlineData("##.##", 1.234d)] // too many decimal digits
    public void NumberMask_NumberBeyondDigitBudget_IsRejected(string mask, double number)
    {
        Assert.False(MaskPattern.MatchesNumberMask(mask, number));
        Assert.False(MaskPattern.IsSatisfiedBy(mask, FieldType.Number, ColumnFamily.Float, FieldValue.FromNumber(number)));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void MatchesNumberMask_NonFiniteNumber_IsRejected(double number)
    {
        Assert.False(MaskPattern.MatchesNumberMask("###", number));
    }

    /// <summary>A mask of another shape has no digit budget, so a caller that skipped classification fails loudly.</summary>
    [Theory]
    [InlineData(PhoneMask)]
    [InlineData(DateMask)]
    [InlineData("##.")]
    [InlineData("")]
    public void MatchesNumberMask_MaskOfAnotherShape_Throws(string mask)
    {
        Assert.Throws<ArgumentException>("mask", () => MaskPattern.MatchesNumberMask(mask, 1d));
    }

    [Fact]
    public void MatchesNumberMask_NullMask_Throws()
    {
        Assert.Throws<ArgumentNullException>("mask", () => MaskPattern.MatchesNumberMask(null!, 1d));
    }

    [Fact]
    public void MatchesNumberMask_IntegerWithNullOrOtherMask_Throws()
    {
        Assert.Throws<ArgumentNullException>("mask", () => MaskPattern.MatchesNumberMask(null!, 1L));
        Assert.Throws<ArgumentException>("mask", () => MaskPattern.MatchesNumberMask(PhoneMask, 1L));
    }

    /// <summary>
    /// An integer column stores the exact integer, so its own digits are counted. Through its double the valid
    /// 18-digit <c>999999999999999999</c> would be <c>1E+18</c>, 19 digits, and fail an 18-position mask.
    /// </summary>
    [Theory]
    [InlineData(EighteenDigitMask, 999999999999999999L)]
    [InlineData(EighteenDigitMask, -999999999999999999L)]
    [InlineData(NineteenDigitMask, long.MaxValue)]
    [InlineData(NineteenDigitMask, long.MinValue)]
    [InlineData(TemperatureMask, 110L)]
    public void NumberMask_ExactIntegerWithinDigitBudgetOnIntegerColumn_IsAccepted(string mask, long integer)
    {
        Assert.True(MaskPattern.MatchesNumberMask(mask, integer));
        Assert.True(
            MaskPattern.IsSatisfiedBy(mask, FieldType.Number, ColumnFamily.Integer, FieldValue.FromInteger(integer)));
    }

    [Theory]
    [InlineData(EighteenDigitMask, 1000000000000000000L)]
    [InlineData(EighteenDigitMask, long.MaxValue)]
    [InlineData(EighteenDigitMask, long.MinValue)]
    [InlineData("###", 1000L)]
    [InlineData("###", -1000L)]
    public void NumberMask_ExactIntegerBeyondDigitBudgetOnIntegerColumn_IsRejected(string mask, long integer)
    {
        Assert.False(MaskPattern.MatchesNumberMask(mask, integer));
        Assert.False(
            MaskPattern.IsSatisfiedBy(mask, FieldType.Number, ColumnFamily.Integer, FieldValue.FromInteger(integer)));
    }

    /// <summary>
    /// A float column stores the double nearest to the number, so that double is counted: the same
    /// <c>999999999999999999</c> is stored there as <c>1E+18</c>, which has 19 integer digits.
    /// </summary>
    [Fact]
    public void NumberMask_LargeIntegerOnFloatColumn_CountsTheStoredDouble()
    {
        FieldValue value = FieldValue.FromInteger(999999999999999999L);

        Assert.True(value.TryGetNumber(out double stored));
        Assert.Equal("1000000000000000000", MaskPattern.FormatPlain(stored));
        Assert.False(MaskPattern.IsSatisfiedBy(EighteenDigitMask, FieldType.Number, ColumnFamily.Float, value));
        Assert.True(MaskPattern.IsSatisfiedBy(NineteenDigitMask, FieldType.Number, ColumnFamily.Float, value));
    }

    /// <summary>
    /// A number with no exact integer, such as a fraction, is counted by its double on any column; on an integer
    /// column the storage range check rejects it before the pattern is checked.
    /// </summary>
    [Fact]
    public void NumberMask_NonIntegralNumberOnIntegerColumn_CountsItsDouble()
    {
        ParsedMask temperature = MaskPattern.Parse(TemperatureMask, FieldType.Number, ColumnFamily.Integer);
        ParsedMask whole = MaskPattern.Parse("###", FieldType.Number, ColumnFamily.Integer);

        Assert.True(temperature.IsSatisfiedBy(FieldValue.FromNumber(37.5d)));
        Assert.False(whole.IsSatisfiedBy(FieldValue.FromNumber(12.5d)));
    }

    /// <summary>The storage range of a <c>datetime</c> column and a date inside it, under the seeded date pattern.</summary>
    [Theory]
    [InlineData(2011, 1, 15)]
    [InlineData(1753, 1, 1)]
    [InlineData(9999, 12, 31)]
    public void IsSatisfiedBy_DatePattern_IsNotChecked(int year, int month, int day)
    {
        FieldValue value = FieldValue.FromDate(new DateOnly(year, month, day));

        Assert.True(MaskPattern.IsSatisfiedBy(DateMask, FieldType.Date, ColumnFamily.DateTime, value));
    }

    [Fact]
    public void IsSatisfiedBy_UnrecognisedPattern_IsNotChecked()
    {
        Assert.True(
            MaskPattern.IsSatisfiedBy(
                "AAA",
                FieldType.Text,
                ColumnFamily.Character,
                FieldValue.FromText("anything at all")));
    }

    [Fact]
    public void IsSatisfiedBy_BlankPattern_AcceptsAnyValue()
    {
        Assert.True(MaskPattern.IsSatisfiedBy(null, FieldType.Text, ColumnFamily.Character, FieldValue.FromText("x")));
        Assert.True(MaskPattern.IsSatisfiedBy("", FieldType.Number, ColumnFamily.Float, FieldValue.FromNumber(123456d)));
        Assert.True(MaskPattern.IsSatisfiedBy("   ", FieldType.Text, ColumnFamily.Character, FieldValue.FromText("x")));
    }

    /// <summary>Clearing a field is legitimate; whether it may be empty is the separate required rule.</summary>
    [Theory]
    [InlineData(PhoneMask, FieldType.Text, ColumnFamily.Character)]
    [InlineData(TemperatureMask, FieldType.Number, ColumnFamily.Float)]
    [InlineData("###", FieldType.Number, ColumnFamily.Integer)]
    public void IsSatisfiedBy_NullValue_HasNothingToCheck(string pattern, FieldType fieldType, ColumnFamily columnFamily)
    {
        Assert.True(MaskPattern.IsSatisfiedBy(pattern, fieldType, columnFamily, FieldValue.Null));
    }

    /// <summary>A value of the wrong JSON type is the record validator's type error, not a pattern failure.</summary>
    [Fact]
    public void IsSatisfiedBy_ValueOfAnotherKind_LeavesTheTypeCheckToTheValidator()
    {
        Assert.True(MaskPattern.IsSatisfiedBy("###", FieldType.Number, ColumnFamily.Float, FieldValue.FromText("1000")));
        Assert.True(
            MaskPattern.IsSatisfiedBy(
                PhoneMask,
                FieldType.Text,
                ColumnFamily.Character,
                FieldValue.FromNumber(5550148636d)));
    }

    [Fact]
    public void FormatPlain_LargeNumber_HasNoExponent()
    {
        string plain = MaskPattern.FormatPlain(1e21);

        Assert.DoesNotContain("E", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("e", plain, StringComparison.Ordinal);
        Assert.Equal("1000000000000000000000", plain);
    }

    [Theory]
    [InlineData(0.0001d, "0.0001")]
    [InlineData(1.5e-7, "0.00000015")]
    [InlineData(37.5d, "37.5")]
    [InlineData(-120d, "-120")]
    [InlineData(0d, "0")]
    public void FormatPlain_FiniteNumber_IsInvariantPositionalNotation(double number, string expected)
    {
        Assert.Equal(expected, MaskPattern.FormatPlain(number));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void FormatPlain_NonFiniteNumber_Throws(double number)
    {
        Assert.Throws<ArgumentOutOfRangeException>("number", () => MaskPattern.FormatPlain(number));
    }

    /// <summary>
    /// A culture with a comma decimal separator changes neither the plain form nor the digit count. Culture is
    /// per async flow, so setting it here does not leak into tests running in parallel.
    /// </summary>
    [Fact]
    public void FormatPlainAndNumberMask_UnderCommaDecimalCulture_StayInvariant()
    {
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo german = new("de-DE");
            CultureInfo.CurrentCulture = german;
            CultureInfo.CurrentUICulture = german;

            // Culture-sensitive formatting does change under this culture, so the assertions below are meaningful.
            Assert.Equal("37,5", 37.5d.ToString(CultureInfo.CurrentCulture));

            Assert.Equal("37.5", MaskPattern.FormatPlain(37.5d));
            Assert.True(MaskPattern.MatchesNumberMask(TemperatureMask, 37.5d));
            Assert.False(MaskPattern.MatchesNumberMask(TemperatureMask, 37.25d));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }
}
