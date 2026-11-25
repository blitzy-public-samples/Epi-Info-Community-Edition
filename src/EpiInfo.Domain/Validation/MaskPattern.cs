using System.Globalization;
using System.Text;
using EpiInfo.Domain.Forms;
using EpiInfo.Domain.Metadata;
using EpiInfo.Domain.Values;

namespace EpiInfo.Domain.Validation;

/// <summary>
/// How the server treats a field's <c>metaFields.Pattern</c> when it validates a submitted value.
/// </summary>
public enum MaskPatternKind
{
    /// <summary>
    /// No pattern applies: the pattern is null, empty or whitespace, or the field type is one patterns never
    /// apply to (checkbox, option, label, group, unsupported).
    /// </summary>
    None,

    /// <summary>
    /// A text-family field with a digit mask such as the seeded <c>###-###-####</c>: every <c>#</c> is an
    /// ASCII digit and every other character is a literal.
    /// </summary>
    TextDigitMask,

    /// <summary>
    /// A number field with a number mask such as the seeded <c>###</c> or <c>###.#</c>, which bounds the
    /// count of integer and decimal digits.
    /// </summary>
    NumberMask,

    /// <summary>
    /// Any other non-blank pattern, including every pattern on a date field (<c>MM-DD-YYYY</c> and the
    /// other date expressions are display and entry formats for the client only). The server does not
    /// check it.
    /// </summary>
    Unchecked,
}

/// <summary>
/// The server's save-time pattern rule for <c>metaFields.Pattern</c>, applied by the record validator to
/// each submitted value.
/// </summary>
/// <remarks>
/// <para>
/// This is a deliberate target rule, recorded in <c>docs/parity-notes.md</c>, and not legacy parity. Legacy
/// Epi Info loads the pattern, through the <c>metaPatterns</c> expression-to-mask table, into a WinForms
/// <c>MaskedTextBox</c> mask, where <c>#</c> is an optional digit, space or sign position. That mask is an
/// entry aid only: legacy accepted partial entries and never checked the pattern on save. The target is
/// stronger on purpose:
/// </para>
/// <list type="bullet">
/// <item><description>
/// a text-family <c>#</c> mask (seeded <c>###-###-####</c>) needs exactly the mask's length, an ASCII digit
/// at each <c>#</c> and the literal at every other position, so <c>555-014-8636</c> is accepted and
/// <c>5550148636</c>, <c>555-014-863</c> and the seeded <c>(555) 0148-6367</c> are rejected;
/// </description></item>
/// <item><description>
/// a number mask (seeded <c>###</c>, <c>###.#</c>) allows at most as many integer digits as <c>#</c> before
/// the point and as many decimal digits as <c>#</c> after it, with a leading sign, so <c>120</c>,
/// <c>0</c> and <c>37.5</c> pass while <c>1000</c> and <c>37.25</c> do not;
/// </description></item>
/// <item><description>
/// a date pattern and any other pattern are not checked.
/// </description></item>
/// </list>
/// <para>
/// The rule is checked on submitted values only, so stored historical values that break it stay readable.
/// The client mirror in <c>web/src/validation/clientValidation.ts</c> repeats the same rule for feedback.
/// Every member returns a classification or a boolean and never echoes a value, so field data cannot reach
/// a validation message or a log through this class. Every member is a pure function of its arguments and is
/// safe to call from any thread.
/// </para>
/// </remarks>
public static class MaskPattern
{
    private const char DigitPosition = '#';
    private const char DecimalPoint = '.';

    /// <summary>
    /// Classifies a field's metadata pattern for server-side checking.
    /// </summary>
    /// <param name="pattern">The <c>metaFields.Pattern</c> expression as stored; it is not trimmed.</param>
    /// <param name="fieldType">The field's mapped type.</param>
    /// <returns>
    /// <see cref="MaskPatternKind.None"/> for a null, empty or whitespace pattern, and for field types patterns
    /// never apply to; <see cref="MaskPatternKind.TextDigitMask"/> for a text-family field whose pattern holds
    /// at least one <c>#</c> and no other letter or digit; <see cref="MaskPatternKind.NumberMask"/> for a number
    /// field whose pattern is exactly one or more <c>#</c>, optionally followed by a point and one or more
    /// <c>#</c>; and <see cref="MaskPatternKind.Unchecked"/> for every other pattern on a text-family, number or
    /// date field.
    /// </returns>
    public static MaskPatternKind Classify(string? pattern, FieldType fieldType)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return MaskPatternKind.None;
        }

        if (FieldTypeMapper.IsTextFamily(fieldType))
        {
            return IsTextDigitMask(pattern) ? MaskPatternKind.TextDigitMask : MaskPatternKind.Unchecked;
        }

        return fieldType switch
        {
            FieldType.Number => TryParseNumberMask(pattern, out _, out _)
                ? MaskPatternKind.NumberMask
                : MaskPatternKind.Unchecked,
            FieldType.Date => MaskPatternKind.Unchecked,
            _ => MaskPatternKind.None,
        };
    }

    /// <summary>
    /// Whether a submitted value satisfies the field's pattern.
    /// </summary>
    /// <param name="pattern">The <c>metaFields.Pattern</c> expression as stored.</param>
    /// <param name="fieldType">The field's mapped type.</param>
    /// <param name="value">The normalised submitted value.</param>
    /// <returns>
    /// <see langword="true"/> when the value is null, when the pattern classifies as
    /// <see cref="MaskPatternKind.None"/> or <see cref="MaskPatternKind.Unchecked"/>, or when the value is not
    /// of the kind the mask checks, since type checking belongs to the record validator; otherwise the result
    /// of <see cref="MatchesTextMask"/> for a text value under a text digit mask, or of
    /// <see cref="MatchesNumberMask"/> for a number value under a number mask.
    /// </returns>
    public static bool IsSatisfiedBy(string? pattern, FieldType fieldType, FieldValue value)
    {
        if (value.IsNull || string.IsNullOrWhiteSpace(pattern))
        {
            return true;
        }

        return Classify(pattern, fieldType) switch
        {
            MaskPatternKind.TextDigitMask => !value.TryGetText(out var text) || MatchesTextMask(pattern, text),
            MaskPatternKind.NumberMask => !value.TryGetNumber(out var number) || MatchesNumberMask(pattern, number),
            _ => true,
        };
    }

    /// <summary>
    /// Whether text fits a digit mask exactly: the same length as the mask, an ASCII digit <c>0</c>-<c>9</c>
    /// wherever the mask holds <c>#</c>, and the mask's own character, compared ordinally, at every other
    /// position. Nothing is trimmed, and digits outside ASCII, such as Arabic-Indic or full-width digits, are
    /// rejected.
    /// </summary>
    /// <param name="mask">The mask, such as <c>###-###-####</c>.</param>
    /// <param name="text">The submitted text.</param>
    /// <returns>Whether the text fits the mask.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mask"/> or <paramref name="text"/> is null.</exception>
    public static bool MatchesTextMask(string mask, string text)
    {
        ArgumentNullException.ThrowIfNull(mask);
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length != mask.Length)
        {
            return false;
        }

        for (var i = 0; i < mask.Length; i++)
        {
            var matches = mask[i] == DigitPosition ? char.IsAsciiDigit(text[i]) : text[i] == mask[i];
            if (!matches)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether a number fits a number mask: its plain invariant form (<see cref="FormatPlain"/>), after one
    /// leading sign, has at most as many integer digits as the mask has <c>#</c> before the point, and at most
    /// as many decimal digits as it has <c>#</c> after the point (none when the mask has no point).
    /// </summary>
    /// <param name="mask">
    /// A number mask: one or more <c>#</c>, optionally followed by a point and one or more <c>#</c>.
    /// </param>
    /// <param name="number">The submitted number.</param>
    /// <returns>Whether the number fits the mask; <see langword="false"/> for NaN or an infinity.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mask"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="mask"/> is not a number mask.</exception>
    public static bool MatchesNumberMask(string mask, double number)
    {
        ArgumentNullException.ThrowIfNull(mask);

        // A mask of another shape has no defined digit budget; counting its '#' would silently reject or
        // accept values, so a caller that skipped Classify fails loudly instead.
        if (!TryParseNumberMask(mask, out var maxIntegerDigits, out var maxDecimalDigits))
        {
            throw new ArgumentException(
                "The mask is not a number mask of '#' digits with an optional point.",
                nameof(mask));
        }

        if (!double.IsFinite(number))
        {
            return false;
        }

        var plain = FormatPlain(number);
        var unsigned = plain.Length > 0 && plain[0] is '-' or '+' ? plain[1..] : plain;
        var point = unsigned.IndexOf(DecimalPoint);
        var integerDigits = point < 0 ? unsigned.Length : point;
        var decimalDigits = point < 0 ? 0 : unsigned.Length - point - 1;
        return integerDigits <= maxIntegerDigits && decimalDigits <= maxDecimalDigits;
    }

    /// <summary>
    /// Formats a finite number in invariant positional notation: the shortest round-trip digits, with no
    /// exponent and no group separators, and negative zero written as <c>0</c>. For example <c>120</c> gives
    /// <c>120</c>, <c>37.5</c> gives <c>37.5</c>, <c>1E+21</c> gives <c>1000000000000000000000</c> and
    /// <c>1.5E-07</c> gives <c>0.00000015</c>.
    /// </summary>
    /// <param name="number">A finite number.</param>
    /// <returns>The number in plain positional notation, whatever the current culture.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="number"/> is NaN or an infinity.</exception>
    public static string FormatPlain(double number)
    {
        if (!double.IsFinite(number))
        {
            throw new ArgumentOutOfRangeException(nameof(number), "Only a finite number has a positional form.");
        }

        // -0 == 0, so this also turns negative zero into "0".
        if (number == 0d)
        {
            return "0";
        }

        var roundTrip = number.ToString("R", CultureInfo.InvariantCulture);
        var exponentAt = roundTrip.IndexOfAny(['E', 'e']);
        return exponentAt < 0 ? roundTrip : ExpandExponent(roundTrip, exponentAt);
    }

    /// <summary>
    /// Rewrites a round-trip form such as <c>-1.2345E+20</c> positionally by moving the decimal point over the
    /// mantissa digits as text, so no digit is added or lost, as converting through <c>decimal</c> or a
    /// fixed-precision format could do.
    /// </summary>
    private static string ExpandExponent(string roundTrip, int exponentAt)
    {
        var exponent = int.Parse(
            roundTrip.AsSpan(exponentAt + 1),
            NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture);
        var mantissa = roundTrip.AsSpan(0, exponentAt);
        var negative = mantissa.Length > 0 && mantissa[0] == '-';
        if (negative)
        {
            mantissa = mantissa[1..];
        }

        var pointInMantissa = mantissa.IndexOf(DecimalPoint);
        var digits = pointInMantissa < 0
            ? mantissa.ToString()
            : string.Concat(mantissa[..pointInMantissa], mantissa[(pointInMantissa + 1)..]);
        var integerDigitCount = (pointInMantissa < 0 ? mantissa.Length : pointInMantissa) + exponent;

        var plain = new StringBuilder(digits.Length + Math.Abs(integerDigitCount) + 3);
        if (negative)
        {
            plain.Append('-');
        }

        if (integerDigitCount <= 0)
        {
            plain.Append("0.").Append('0', -integerDigitCount).Append(digits);
        }
        else if (integerDigitCount >= digits.Length)
        {
            plain.Append(digits).Append('0', integerDigitCount - digits.Length);
        }
        else
        {
            plain.Append(digits, 0, integerDigitCount)
                .Append(DecimalPoint)
                .Append(digits, integerDigitCount, digits.Length - integerDigitCount);
        }

        return plain.ToString();
    }

    private static bool IsTextDigitMask(string pattern)
    {
        var hasDigitPosition = false;
        foreach (var c in pattern)
        {
            if (c == DigitPosition)
            {
                hasDigitPosition = true;
            }
            else if (char.IsLetterOrDigit(c))
            {
                return false;
            }
        }

        return hasDigitPosition;
    }

    /// <summary>
    /// Parses the number-mask shape <c>#+(\.#+)?</c> over the whole string. A loop rather than a regular
    /// expression keeps the match exact: <c>$</c> would also match before a trailing line feed.
    /// </summary>
    private static bool TryParseNumberMask(string mask, out int maxIntegerDigits, out int maxDecimalDigits)
    {
        maxIntegerDigits = 0;
        maxDecimalDigits = 0;
        var point = mask.IndexOf(DecimalPoint);
        var integerPart = point < 0 ? mask.AsSpan() : mask.AsSpan(0, point);
        var decimalPart = point < 0 ? ReadOnlySpan<char>.Empty : mask.AsSpan(point + 1);

        if (integerPart.IsEmpty || integerPart.ContainsAnyExcept(DigitPosition))
        {
            return false;
        }

        if (point >= 0 && (decimalPart.IsEmpty || decimalPart.ContainsAnyExcept(DigitPosition)))
        {
            return false;
        }

        maxIntegerDigits = integerPart.Length;
        maxDecimalDigits = decimalPart.Length;
        return true;
    }
}
