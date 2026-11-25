using System.ComponentModel;
using System.Globalization;
using EpiInfo.CheckCode.Syntax;
using EpiInfo.Domain.Values;

namespace EpiInfo.CheckCode.Evaluation;

/// <summary>
/// The value rules of the check-code subset: emptiness, the <c>=</c> operator and the value of a literal, ported
/// from the legacy interpreter's emptiness test, comparison rule and literal rule.
/// </summary>
/// <remarks>
/// <para>
/// Every member is a pure function: no state, no I/O and no dependency on the current culture. Two texts are
/// trimmed with <see cref="char.IsWhiteSpace(char)"/>, and text is compared linguistically with the invariant
/// culture, ignoring case, where legacy used the desktop's current culture; numeric text is parsed with legacy's
/// number styles in the invariant culture. A program therefore gives the same answer on every server. The cases
/// where the desktop culture could answer differently are recorded in <c>docs/parity-notes.md</c>, "Parity gaps".
/// </para>
/// <para>
/// The seeded programs depend on these cases:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <c>Sex = "1"</c> on <c>CaseReport</c>, where <c>Sex</c> is a text field: text <c>"1"</c> is equal, so the
/// <c>THEN</c> branch runs (<c>LY-01</c>); text <c>"2"</c> is not (<c>LY-02</c>, <c>LY-03</c>); and a null
/// <c>Sex</c> is not, because exactly one side is empty, so the <c>ELSE</c> branch runs (<c>LY-04</c>).
/// </description></item>
/// <item><description>
/// <c>FeverTemp = (.)</c> on <c>FoodHistory</c>: the number <c>101.2</c> against the missing value is not equal
/// (<c>EC-FEVER-01</c>, <c>EC-FEVER-02</c>), and a null <c>FeverTemp</c> is (<c>EC-FEVER-03</c>).
/// </description></item>
/// <item><description>
/// <c>Fever = (-)</c>: an unchecked checkbox (<see langword="false"/>) is equal, a checked one is not, and a null
/// checkbox is not, because exactly one side is empty.
/// </description></item>
/// </list>
/// </remarks>
public static class ValueComparer
{
    // The number styles of double.TryParse(string), which legacy comparison uses.
    private const NumberStyles LegacyNumberStyles = NumberStyles.Float | NumberStyles.AllowThousands;

    /// <summary>
    /// Whether a value is empty: <see cref="FieldValue.Null"/>, or text of length 0.
    /// </summary>
    /// <remarks>
    /// The legacy test treats a value as empty when it is null or its string form is the empty string. Text of
    /// spaces is therefore not empty, and neither is any boolean (<see langword="false"/> included), number
    /// (0 included), date or option index.
    /// </remarks>
    /// <param name="value">The value to test.</param>
    /// <returns>Whether <paramref name="value"/> is empty.</returns>
    public static bool IsEmpty(FieldValue value) =>
        value.IsNull || (value.TryGetText(out var text) && text.Length == 0);

    /// <summary>
    /// The check-code <c>=</c> operator, the only comparison in the subset, following legacy
    /// <c>Rule_CompareExp</c>. It never throws: it reports a pair legacy cannot compare by returning
    /// <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// <para>The first rule that applies decides:</para>
    /// <list type="number">
    /// <item><description>Both sides empty (<see cref="IsEmpty"/>): equal, so <c>"" = (.)</c> holds.</description></item>
    /// <item><description>Exactly one side empty: not equal, so <c>" " = (.)</c> does not hold.</description></item>
    /// <item><description>
    /// Both numeric, a number or an option index: compared as doubles, so the number 25 equals the index 25.
    /// </description></item>
    /// <item><description>Both boolean: equal when the booleans are equal.</description></item>
    /// <item><description>
    /// A boolean on the right, the left neither boolean nor empty: the result is the right boolean itself, because
    /// legacy compares it with "the left side is not empty", which holds here. So <c>"No" = (-)</c> and
    /// <c>5 = (-)</c> do not hold, while text against a checked checkbox does, whatever the text says.
    /// </description></item>
    /// <item><description>
    /// A boolean on the left, the right neither boolean nor empty: legacy converts the right side with
    /// <c>ConvertStringToBoolean</c>, matching its invariant upper case, untrimmed. <c>(+)</c>, <c>YES</c>,
    /// <c>Y</c>, <c>TRUE</c> and <c>T</c> are <see langword="true"/>; <c>(-)</c>, <c>NO</c>, <c>N</c>,
    /// <c>FALSE</c> and <c>F</c> are <see langword="false"/>; the two booleans are then compared, so
    /// <c>Fever = "No"</c> holds for an unchecked checkbox and not for a checked one. Any other right side (other
    /// text, untrimmed text such as <c>" No "</c>, a number, a date or an option index) has no conversion, and
    /// legacy fails with an exception. This method then returns <see langword="false"/>, and the evaluator reports
    /// a type-mismatch runtime error for the enclosing statement.
    /// </description></item>
    /// <item><description>
    /// Text against a number or an option index: the text, as it stands, parsed as legacy's
    /// <c>double.TryParse(string)</c> does, with <see cref="NumberStyles.Float"/> and
    /// <see cref="NumberStyles.AllowThousands"/>, in the invariant culture, and compared as a double. So
    /// <c>" 25 "</c> equals 25 and <c>"1,000"</c> equals 1000. Text that is not a finite number falls through to
    /// rule 10.
    /// </description></item>
    /// <item><description>
    /// Both text: compared after trimming, linguistically with the invariant culture and ignoring case, so
    /// <c>" Yes "</c> equals <c>"yes"</c> and a precomposed <c>"é"</c> equals <c>"e"</c> followed by a combining
    /// acute accent.
    /// </description></item>
    /// <item><description>Both dates: equal when they are the same calendar date.</description></item>
    /// <item><description>
    /// Any other pair, such as a date against text or text that is not a number against a number: the invariant
    /// string forms of both sides (<see cref="FieldValue.ToInvariantString"/>: dates as <c>yyyy-MM-dd</c>, numbers
    /// in round-trip form, text as stored), untrimmed, as legacy compares the string forms of two different types,
    /// and otherwise compared as in rule 8. So text <c>"2011-01-15"</c> equals the date 2011-01-15, while
    /// <c>"25"</c> followed by a non-breaking space, which rule 7 does not read as a number, does not equal 25.
    /// </description></item>
    /// </list>
    /// <para>
    /// Numbers compare as their nearest doubles, as legacy converts both sides to <see cref="double"/>, so two
    /// integers beyond 2^53 that round to the same double are equal.
    /// </para>
    /// <para>
    /// Legacy compared text and parsed numbers with the desktop's current culture, and wrote a date in rule 10 in
    /// that culture's format. The invariant culture agrees with it on case, trimming, canonically equivalent forms,
    /// ignorable characters, and numbers written with <c>","</c> grouping and a <c>"."</c> decimal point, as en-US
    /// writes them. The cases it cannot reproduce, the date's format among them, and the runtime error that replaces
    /// legacy's exception in rule 6, are recorded in <c>docs/parity-notes.md</c>, "Parity gaps".
    /// </para>
    /// </remarks>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <param name="equal">
    /// Whether the operands are equal under the rules above; <see langword="false"/> when the method returns
    /// <see langword="false"/>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> for every pair legacy can compare; <see langword="false"/> only for a boolean on the
    /// left against a value that is not a yes/no spelling, which legacy cannot convert.
    /// </returns>
    public static bool TryAreEqual(FieldValue left, FieldValue right, out bool equal)
    {
        var leftEmpty = IsEmpty(left);
        var rightEmpty = IsEmpty(right);
        if (leftEmpty || rightEmpty)
        {
            equal = leftEmpty && rightEmpty;
            return true;
        }

        var leftIsNumeric = TryGetNumeric(left, out var leftNumber);
        var rightIsNumeric = TryGetNumeric(right, out var rightNumber);
        if (leftIsNumeric && rightIsNumeric)
        {
            equal = leftNumber == rightNumber;
            return true;
        }

        var leftIsBoolean = left.TryGetBoolean(out var leftBoolean);
        var rightIsBoolean = right.TryGetBoolean(out var rightBoolean);
        if (leftIsBoolean && rightIsBoolean)
        {
            equal = leftBoolean == rightBoolean;
            return true;
        }

        // Rule_CompareExp.cs:119-121: a boolean on the right is compared with "the left side is not empty", which is
        // known to hold here, so the result is the right boolean itself, whatever the left side holds.
        if (rightIsBoolean)
        {
            equal = rightBoolean;
            return true;
        }

        // TryGetText leaves the text null for every other kind, so a non-null text marks a text value.
        left.TryGetText(out var leftText);
        right.TryGetText(out var rightText);

        // Rule_CompareExp.cs:182-185: a boolean on the left is compared with the right side converted by
        // ConvertStringToBoolean. Only text can be a yes/no spelling; the string form of a number, date or option
        // index never is, so legacy's unboxing cast of the null conversion fails for those as for other text.
        if (leftIsBoolean)
        {
            if (rightText is not null && TryConvertToBoolean(rightText, out var rightAsBoolean))
            {
                equal = leftBoolean == rightAsBoolean;
                return true;
            }

            equal = false;
            return false;
        }

        if (leftText is not null && rightIsNumeric && TryParseLegacyNumber(leftText, out var parsedLeft))
        {
            equal = parsedLeft == rightNumber;
            return true;
        }

        if (rightText is not null && leftIsNumeric && TryParseLegacyNumber(rightText, out var parsedRight))
        {
            equal = leftNumber == parsedRight;
            return true;
        }

        if (leftText is not null && rightText is not null)
        {
            equal = TrimmedTextEquals(leftText, rightText);
            return true;
        }

        if (left.TryGetDate(out var leftDate) && right.TryGetDate(out var rightDate))
        {
            equal = leftDate == rightDate;
            return true;
        }

        // Rule_CompareExp.cs:191-217: any other pair is compared by the string forms of both sides, untrimmed, so text
        // that failed the number parse, such as "25" followed by a non-breaking space, is not trimmed into a number.
        // Neither side is empty, so neither is Null and both invariant forms are non-null.
        equal = TextEquals(left.ToInvariantString() ?? string.Empty, right.ToInvariantString() ?? string.Empty);
        return true;
    }

    /// <summary>
    /// The value a literal evaluates to: <c>(.)</c> is <see cref="FieldValue.Null"/>, <c>(-)</c> is
    /// <see langword="false"/>, a number is its exact value and a string is its text.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A string literal's text arrives with its quotes already removed and is kept as written, untrimmed; a
    /// string literal without text is the empty text. A numeric literal without a finite number is
    /// <see cref="FieldValue.Null"/>, as a legacy literal that failed to parse had no value.
    /// </para>
    /// <para>
    /// A numeric literal parsed from source carries its spelling in <see cref="LiteralNode.Text"/>, read with
    /// <see cref="FieldValueCoercion.TryParseNumber(string, out FieldValue)"/>, the rule a number in
    /// <c>values</c> follows, so <c>ASSIGN</c> types the literal as the same text sent as a value is typed:
    /// <c>2.0</c> and <c>9007199254740993</c> keep their exact integers, and <c>2.0000000000000001</c> is not a
    /// whole number, so it is never option index 2. Its nearest double, which <see cref="TryAreEqual"/> compares, is
    /// unchanged: <c>2.0000000000000001 = 2</c> still holds. A literal without a spelling, or whose spelling is
    /// not that of its <see cref="LiteralNode.Number"/>, is that double taken as exact
    /// (<see cref="FieldValue.FromNumber(double)"/>).
    /// </para>
    /// </remarks>
    /// <param name="literal">The literal node.</param>
    /// <returns>The literal's value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="literal"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidEnumArgumentException">
    /// <paramref name="literal"/> has a <see cref="LiteralKind"/> outside the declared members, which the parser
    /// never produces.
    /// </exception>
    public static FieldValue LiteralToValue(LiteralNode literal)
    {
        ArgumentNullException.ThrowIfNull(literal);

        return literal.Kind switch
        {
            LiteralKind.Missing => FieldValue.Null,
            LiteralKind.Unchecked => FieldValue.FromBoolean(false),
            LiteralKind.Number => literal.Number is { } number && double.IsFinite(number)
                ? NumberLiteralValue(literal.Text, number)
                : FieldValue.Null,
            LiteralKind.String => FieldValue.FromText(literal.Text ?? string.Empty),

            // Not ArgumentOutOfRangeException: the evaluator reports that exception from a statement as a
            // date-arithmetic diagnostic, while an undeclared kind is a defect that must surface as one.
            _ => throw new InvalidEnumArgumentException(nameof(literal), (int)literal.Kind, typeof(LiteralKind)),
        };

        // The spelling decides only exactness: a node built through its constructor or a `with` expression can
        // pair a number with unrelated text, and then the number stands.
        static FieldValue NumberLiteralValue(string? spelling, double number) =>
            FieldValueCoercion.TryParseNumber(spelling, out FieldValue exact)
                && exact.TryGetNumber(out double nearest)
                && nearest == number
                    ? exact
                    : FieldValue.FromNumber(number);
    }

    // Numbers and option indexes are the numeric kinds; an index widens exactly to a double.
    private static bool TryGetNumeric(FieldValue value, out double number)
    {
        if (value.TryGetNumber(out number))
        {
            return true;
        }

        if (value.TryGetIndex(out var index))
        {
            number = index;
            return true;
        }

        number = 0d;
        return false;
    }

    // EnterRule.cs:64-89 (ConvertStringToBoolean): the spellings legacy reads as a boolean, matched on the invariant
    // upper case of the text as it stands, untrimmed, so " No " is not a spelling.
    private static bool TryConvertToBoolean(string text, out bool value)
    {
        switch (text.ToUpperInvariant())
        {
            case "(+)" or "YES" or "Y" or "TRUE" or "T":
                value = true;
                return true;

            case "(-)" or "NO" or "N" or "FALSE" or "F":
                value = false;
                return true;

            default:
                value = false;
                return false;
        }
    }

    // Rule_CompareExp.cs:123, 150: legacy parses the text as it stands with double.TryParse(string), whose styles
    // allow thousands separators and only the ASCII white space of NumberStyles.Float, so " 1,000.5 " is 1000.5 and
    // "25" followed by a non-breaking space is not a number. The invariant culture reads "," as the group and "." as
    // the decimal separator on every server. A result that is not finite is rejected, as .NET Framework rejected an
    // overflow; such text could never equal the finite number or index on the other side.
    private static bool TryParseLegacyNumber(string text, out double number)
    {
        if (double.TryParse(text, LegacyNumberStyles, CultureInfo.InvariantCulture, out number)
            && double.IsFinite(number))
        {
            return true;
        }

        number = 0d;
        return false;
    }

    // Rule_CompareExp.cs:226: legacy trims two texts before it compares them.
    private static bool TrimmedTextEquals(string left, string right) =>
        TextEquals(left.AsSpan().Trim(), right.AsSpan().Trim());

    // Rule_CompareExp.cs:195, 226: legacy compares text with StringComparer.CurrentCultureIgnoreCase. The invariant
    // culture keeps that linguistic comparison, so canonically equivalent forms and ignorable characters still compare
    // equal, without depending on the server's culture.
    private static bool TextEquals(ReadOnlySpan<char> left, ReadOnlySpan<char> right) =>
        CultureInfo.InvariantCulture.CompareInfo.Compare(left, right, CompareOptions.IgnoreCase) == 0;
}
