using System.ComponentModel;
using EpiInfo.CheckCode.Syntax;
using EpiInfo.Domain.Values;

namespace EpiInfo.CheckCode.Evaluation;

/// <summary>
/// The value rules of the check-code subset: emptiness, the <c>=</c> operator and the value of a literal, ported
/// from the legacy interpreter's emptiness test, comparison rule and literal rule.
/// </summary>
/// <remarks>
/// <para>
/// Every member is a pure function: no state, no I/O and no dependency on the current culture. Text is trimmed
/// with <see cref="char.IsWhiteSpace(char)"/>, compared with <see cref="StringComparison.OrdinalIgnoreCase"/> and
/// parsed as a number with the invariant culture, so a program gives the same answer on every server.
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
    /// The check-code <c>=</c> operator, the only comparison in the subset. It never throws.
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
    /// One boolean and the other neither boolean nor empty: the result is the boolean itself, because the legacy
    /// rule compares the boolean with "the other side is not empty", which is true here.
    /// </description></item>
    /// <item><description>
    /// Text against a number or an option index: the trimmed text, parsed with the invariant culture, compared as
    /// a double, so <c>" 25 "</c> equals 25. Text that is not a number falls through to rule 8.
    /// </description></item>
    /// <item><description>
    /// Both text: compared after trimming, ignoring case, so <c>" Yes "</c> equals <c>"yes"</c>.
    /// </description></item>
    /// <item><description>Both dates: equal when they are the same calendar date.</description></item>
    /// <item><description>
    /// Any other pair, such as a date against text: the invariant string forms of both sides
    /// (<see cref="FieldValue.ToInvariantString"/>: dates as <c>yyyy-MM-dd</c>, numbers in round-trip form, text
    /// as stored), trimmed and compared ignoring case, so text <c>"2011-01-15"</c> equals the date 2011-01-15.
    /// </description></item>
    /// </list>
    /// <para>
    /// Two departures from the legacy rule keep the result independent of the server and the side a value is on.
    /// Legacy compared text with the current culture, ignoring case; this compares ordinally, ignoring case. Legacy
    /// handled a boolean on the right as rule 5 does, but converted text on the right of a boolean on the left with
    /// <c>ConvertStringToBoolean</c>, which failed with an exception for text other than a yes/no spelling and
    /// stopped the program; rule 5 applies to either side instead and never throws.
    /// </para>
    /// <para>
    /// Numbers compare as their nearest doubles, as legacy converts both sides to <see cref="double"/>, so two
    /// integers beyond 2^53 that round to the same double are equal.
    /// </para>
    /// </remarks>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns>Whether the operands are equal under the rules above.</returns>
    public static bool AreEqual(FieldValue left, FieldValue right)
    {
        var leftEmpty = IsEmpty(left);
        var rightEmpty = IsEmpty(right);
        if (leftEmpty || rightEmpty)
        {
            return leftEmpty && rightEmpty;
        }

        var leftIsNumeric = TryGetNumeric(left, out var leftNumber);
        var rightIsNumeric = TryGetNumeric(right, out var rightNumber);
        if (leftIsNumeric && rightIsNumeric)
        {
            return leftNumber == rightNumber;
        }

        var leftIsBoolean = left.TryGetBoolean(out var leftBoolean);
        var rightIsBoolean = right.TryGetBoolean(out var rightBoolean);
        if (leftIsBoolean && rightIsBoolean)
        {
            return leftBoolean == rightBoolean;
        }

        // The other side is known to be non-empty, so "boolean = (other side is not empty)" is the boolean itself.
        if (leftIsBoolean)
        {
            return leftBoolean;
        }

        if (rightIsBoolean)
        {
            return rightBoolean;
        }

        // TryGetText leaves the text null for every other kind, so a non-null text marks a text value.
        left.TryGetText(out var leftText);
        right.TryGetText(out var rightText);
        if (leftText is not null && rightIsNumeric && TryParseTrimmedNumber(leftText, out var parsedLeft))
        {
            return parsedLeft == rightNumber;
        }

        if (rightText is not null && leftIsNumeric && TryParseTrimmedNumber(rightText, out var parsedRight))
        {
            return leftNumber == parsedRight;
        }

        if (leftText is not null && rightText is not null)
        {
            return TrimmedTextEquals(leftText, rightText);
        }

        if (left.TryGetDate(out var leftDate) && right.TryGetDate(out var rightDate))
        {
            return leftDate == rightDate;
        }

        // Neither side is empty, so neither is Null and both invariant forms are non-null.
        return TrimmedTextEquals(left.ToInvariantString() ?? string.Empty, right.ToInvariantString() ?? string.Empty);
    }

    /// <summary>
    /// The value a literal evaluates to: <c>(.)</c> is <see cref="FieldValue.Null"/>, <c>(-)</c> is
    /// <see langword="false"/>, a number is that number and a string is its text.
    /// </summary>
    /// <remarks>
    /// A string literal's text arrives with its quotes already removed and is kept as written, untrimmed; a
    /// string literal without text is the empty text. A numeric literal without a finite number is
    /// <see cref="FieldValue.Null"/>, as a legacy literal that failed to parse had no value.
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
                ? FieldValue.FromNumber(number)
                : FieldValue.Null,
            LiteralKind.String => FieldValue.FromText(literal.Text ?? string.Empty),

            // Not ArgumentOutOfRangeException: the evaluator reports that exception from a statement as a
            // date-arithmetic diagnostic, while an undeclared kind is a defect that must surface as one.
            _ => throw new InvalidEnumArgumentException(nameof(literal), (int)literal.Kind, typeof(LiteralKind)),
        };
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

    // Trim removes every Unicode white-space character, such as a non-breaking space, which the invariant number
    // styles alone would reject.
    private static bool TryParseTrimmedNumber(string text, out double number) =>
        FieldValueCoercion.TryParseInvariantNumber(text.Trim(), out number);

    private static bool TrimmedTextEquals(string left, string right) =>
        left.AsSpan().Trim().Equals(right.AsSpan().Trim(), StringComparison.OrdinalIgnoreCase);
}
