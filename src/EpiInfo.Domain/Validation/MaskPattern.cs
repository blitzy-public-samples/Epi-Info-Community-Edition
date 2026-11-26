using System.Diagnostics.CodeAnalysis;
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
    /// A text-family field with a digit mask such as the seeded <c>###-###-####</c>: a pattern with at least one
    /// <c>#</c> and no letter, where every <c>#</c> is an ASCII digit and every other character, a digit such as
    /// the <c>2</c> of <c>##-2</c> included, is a literal.
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
/// a text-family <c>#</c> mask (seeded <c>###-###-####</c>), a pattern with at least one <c>#</c> and no
/// letter, needs exactly the mask's length, an ASCII digit at each <c>#</c> and the literal, a digit included,
/// at every other position, so <c>555-014-8636</c> is accepted and <c>5550148636</c>, <c>555-014-863</c> and
/// the seeded <c>(555) 0148-6367</c> are rejected;
/// </description></item>
/// <item><description>
/// a number mask (seeded <c>###</c>, <c>###.#</c>) allows at most as many integer digits as <c>#</c> before
/// the point and as many decimal digits as <c>#</c> after it, with a leading sign, so <c>120</c>,
/// <c>0</c> and <c>37.5</c> pass while <c>1000</c> and <c>37.25</c> do not. A number is counted as its column
/// stores it: an exact integer on an integer column (<c>tinyint</c>, <c>smallint</c>, <c>int</c>,
/// <c>bigint</c>) by its own digits, so <c>999999999999999999</c> has 18, and any other number by the plain
/// form of its double, which is what a <c>float</c> column stores, so the same number there is
/// <c>1E+18</c> and has 19;
/// </description></item>
/// <item><description>
/// a date pattern and any other pattern are not checked.
/// </description></item>
/// </list>
/// <para>
/// The rule is checked on submitted values only, so stored historical values that break it stay readable.
/// The client mirror in <c>web/src/validation/clientValidation.ts</c> repeats the same rule for feedback.
/// </para>
/// <para>
/// The checking members, <see cref="Classify"/>, <see cref="Parse"/>, <see cref="IsSatisfiedBy"/>,
/// <see cref="MatchesTextMask"/>, both <c>MatchesNumberMask</c> overloads and every member of
/// <see cref="ParsedMask"/>, return only a classification, a boolean or the parsed pattern metadata, and never
/// echo a value, so field data cannot reach a validation message or a log through them.
/// <see cref="FormatPlain"/> is the exception: it returns the submitted number itself as text, and exists only
/// so the number's digits can be counted. Its result is field data, which the redaction policy keeps out of
/// every log, validation message and error body.
/// </para>
/// <para>
/// Every member is a pure function of its arguments and is safe to call from any thread. A
/// <see cref="ParsedMask"/> is immutable, so one instance can serve every validation of its field on any
/// thread.
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
    /// at least one <c>#</c> and no letter, its other characters, digits included, being literals;
    /// <see cref="MaskPatternKind.NumberMask"/> for a number field whose pattern is exactly one or more <c>#</c>,
    /// optionally followed by a point and one or more <c>#</c>; and <see cref="MaskPatternKind.Unchecked"/> for
    /// every other pattern on a text-family, number or date field.
    /// </returns>
    public static MaskPatternKind Classify(string? pattern, FieldType fieldType) => Interpret(pattern, fieldType).Kind;

    /// <summary>
    /// Interprets a field's metadata pattern once, for its field type and column, so every later check of a
    /// submitted value reuses the classification and digit budget instead of parsing the pattern again. The record
    /// validator parses each field's pattern when it is built.
    /// </summary>
    /// <param name="pattern">The <c>metaFields.Pattern</c> expression as stored; it is not trimmed.</param>
    /// <param name="fieldType">The field's mapped type.</param>
    /// <param name="columnFamily">
    /// The family of the field's page-table column, which decides how a number is counted: by its exact integer
    /// on a <see cref="ColumnFamily.Integer"/> column, and otherwise by its double, which is what a
    /// <see cref="ColumnFamily.Float"/> column stores. It does not affect text or date fields.
    /// </param>
    /// <returns>
    /// The parsed mask, whose <see cref="ParsedMask.Kind"/> is what <see cref="Classify"/> returns for the same
    /// pattern and field type.
    /// </returns>
    public static ParsedMask Parse(string? pattern, FieldType fieldType, ColumnFamily columnFamily)
    {
        var (kind, maxIntegerDigits, maxDecimalDigits) = Interpret(pattern, fieldType);
        return new ParsedMask(
            kind,
            pattern,
            maxIntegerDigits,
            maxDecimalDigits,
            countsExactInteger: columnFamily == ColumnFamily.Integer);
    }

    /// <summary>
    /// Whether a submitted value satisfies the field's pattern. The pattern is parsed for this one call; a caller
    /// that checks many values of one field parses it once with <see cref="Parse"/> and calls
    /// <see cref="ParsedMask.IsSatisfiedBy"/>, which decides the same way.
    /// </summary>
    /// <param name="pattern">The <c>metaFields.Pattern</c> expression as stored.</param>
    /// <param name="fieldType">The field's mapped type.</param>
    /// <param name="columnFamily">
    /// The family of the field's page-table column, which decides how a number is counted: by its exact integer
    /// on a <see cref="ColumnFamily.Integer"/> column, and otherwise by its double, which is what a
    /// <see cref="ColumnFamily.Float"/> column stores. It is ignored for text and date fields.
    /// </param>
    /// <param name="value">The normalised submitted value.</param>
    /// <returns>
    /// <see langword="true"/> when the value is null, when the pattern classifies as
    /// <see cref="MaskPatternKind.None"/> or <see cref="MaskPatternKind.Unchecked"/>, or when the value is not
    /// of the kind the mask checks, since type checking belongs to the record validator; otherwise the result
    /// of <see cref="MatchesTextMask"/> for a text value under a text digit mask, and, under a number mask, of
    /// <see cref="MatchesNumberMask(string, long)"/> for an exact integer on an integer column or of
    /// <see cref="MatchesNumberMask(string, double)"/> for any other number.
    /// </returns>
    public static bool IsSatisfiedBy(
        string? pattern,
        FieldType fieldType,
        ColumnFamily columnFamily,
        FieldValue value) =>
        Parse(pattern, fieldType, columnFamily).IsSatisfiedBy(value);

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
    /// Whether a number fits a number mask, counted as a <c>float</c> column stores it: its plain invariant form
    /// (<see cref="FormatPlain"/>), after one leading sign, has at most as many integer digits as the mask has
    /// <c>#</c> before the point, and at most as many decimal digits as it has <c>#</c> after the point (none
    /// when the mask has no point).
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
        var (maxIntegerDigits, maxDecimalDigits) = RequireNumberMask(mask);
        return FitsDigitBudget(number, maxIntegerDigits, maxDecimalDigits);
    }

    /// <summary>
    /// Whether an exact integer fits a number mask, counted as an integer column stores it: its invariant digits,
    /// after one leading minus sign, are at most as many as the mask has <c>#</c> before the point, and it has
    /// no decimal digits. Every signed 64-bit value is counted exactly, <see cref="long.MinValue"/> included,
    /// with no rounding through a double.
    /// </summary>
    /// <param name="mask">
    /// A number mask: one or more <c>#</c>, optionally followed by a point and one or more <c>#</c>.
    /// </param>
    /// <param name="integer">The submitted integer.</param>
    /// <returns>Whether the integer fits the mask.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mask"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="mask"/> is not a number mask.</exception>
    public static bool MatchesNumberMask(string mask, long integer)
    {
        var (maxIntegerDigits, maxDecimalDigits) = RequireNumberMask(mask);
        return FitsDigitBudget(integer, maxIntegerDigits, maxDecimalDigits);
    }

    /// <summary>
    /// Formats a finite number in invariant positional notation: the shortest round-trip digits, with no
    /// exponent and no group separators, and negative zero written as <c>0</c>. For example <c>120</c> gives
    /// <c>120</c>, <c>37.5</c> gives <c>37.5</c>, <c>1E+21</c> gives <c>1000000000000000000000</c> and
    /// <c>1.5E-07</c> gives <c>0.00000015</c>.
    /// </summary>
    /// <remarks>
    /// This exists to count a number's digits. Unlike the checking members it returns the number itself, so for a
    /// submitted value the result is field data: never pass it to a logger, a validation message or an error body.
    /// </remarks>
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

    /// <summary>
    /// Whether a finite number fits a digit budget, counted by its plain form, which is what a <c>float</c>
    /// column stores. NaN and the infinities fit no budget.
    /// </summary>
    internal static bool FitsDigitBudget(double number, int maxIntegerDigits, int maxDecimalDigits) =>
        double.IsFinite(number) && PlainFormFitsDigitBudget(FormatPlain(number), maxIntegerDigits, maxDecimalDigits);

    /// <summary>
    /// Whether an exact integer fits a digit budget, counted by its own invariant digits, which is what an integer
    /// column stores. Formatting rather than negating keeps <see cref="long.MinValue"/> exact.
    /// </summary>
    internal static bool FitsDigitBudget(long integer, int maxIntegerDigits, int maxDecimalDigits) =>
        PlainFormFitsDigitBudget(integer.ToString(CultureInfo.InvariantCulture), maxIntegerDigits, maxDecimalDigits);

    /// <summary>
    /// The one digit-budget rule: after one leading sign, the digits before the point are at most
    /// <paramref name="maxIntegerDigits"/> and the digits after it at most <paramref name="maxDecimalDigits"/>.
    /// </summary>
    private static bool PlainFormFitsDigitBudget(string plain, int maxIntegerDigits, int maxDecimalDigits)
    {
        var unsigned = plain.Length > 0 && plain[0] is '-' or '+' ? plain.AsSpan(1) : plain.AsSpan();
        var point = unsigned.IndexOf(DecimalPoint);
        var integerDigits = point < 0 ? unsigned.Length : point;
        var decimalDigits = point < 0 ? 0 : unsigned.Length - point - 1;
        return integerDigits <= maxIntegerDigits && decimalDigits <= maxDecimalDigits;
    }

    /// <summary>
    /// The digit budget of a mask a caller passes as a number mask. A mask of another shape has no defined digit
    /// budget; counting its <c>#</c> would silently reject or accept values, so a caller that skipped
    /// <see cref="Classify"/> fails loudly instead.
    /// </summary>
    private static (int MaxIntegerDigits, int MaxDecimalDigits) RequireNumberMask(string mask)
    {
        ArgumentNullException.ThrowIfNull(mask);

        if (!TryParseNumberMask(mask, out var maxIntegerDigits, out var maxDecimalDigits))
        {
            throw new ArgumentException(
                "The mask is not a number mask of '#' digits with an optional point.",
                nameof(mask));
        }

        return (maxIntegerDigits, maxDecimalDigits);
    }

    /// <summary>
    /// The one interpretation of a pattern behind <see cref="Classify"/> and <see cref="Parse"/>: its kind and,
    /// for a number mask, its digit budget, which is 0 and 0 for every other kind.
    /// </summary>
    private static (MaskPatternKind Kind, int MaxIntegerDigits, int MaxDecimalDigits) Interpret(
        string? pattern,
        FieldType fieldType)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return (MaskPatternKind.None, 0, 0);
        }

        if (FieldTypeMapper.IsTextFamily(fieldType))
        {
            return (IsTextDigitMask(pattern) ? MaskPatternKind.TextDigitMask : MaskPatternKind.Unchecked, 0, 0);
        }

        if (fieldType == FieldType.Number)
        {
            return TryParseNumberMask(pattern, out var maxIntegerDigits, out var maxDecimalDigits)
                ? (MaskPatternKind.NumberMask, maxIntegerDigits, maxDecimalDigits)
                : (MaskPatternKind.Unchecked, 0, 0);
        }

        return (fieldType == FieldType.Date ? MaskPatternKind.Unchecked : MaskPatternKind.None, 0, 0);
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
            else if (char.IsLetter(c))
            {
                // A letter marks another kind of pattern, such as a date expression; a digit is a literal.
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

/// <summary>
/// A field's <c>metaFields.Pattern</c> interpreted once for the field's type and column, as
/// <see cref="MaskPattern.Parse"/> produces it, so the save-time check of each submitted value reads the stored
/// classification and digit budget without parsing the pattern again.
/// </summary>
/// <remarks>
/// An instance is immutable, so one can be shared by every validation of its field on any thread. It holds
/// metadata only, never a submitted value, and each member returns a classification, the stored pattern, a
/// digit budget or a boolean.
/// </remarks>
public sealed class ParsedMask
{
    // Set when the field's column is in the integer family, which stores a number as its exact integer.
    private readonly bool _countsExactInteger;

    internal ParsedMask(
        MaskPatternKind kind,
        string? pattern,
        int maxIntegerDigits,
        int maxDecimalDigits,
        bool countsExactInteger)
    {
        Kind = kind;
        Pattern = pattern;
        MaxIntegerDigits = maxIntegerDigits;
        MaxDecimalDigits = maxDecimalDigits;
        _countsExactInteger = countsExactInteger;
    }

    /// <summary>How the server treats the pattern, as <see cref="MaskPattern.Classify"/> classifies it.</summary>
    public MaskPatternKind Kind { get; }

    /// <summary>
    /// The <c>metaFields.Pattern</c> expression as stored, untrimmed, or <see langword="null"/> when the field has
    /// none. It is metadata, not field data.
    /// </summary>
    public string? Pattern { get; }

    /// <summary>
    /// Whether the server checks submitted values against the pattern: <see langword="true"/> exactly for a
    /// <see cref="MaskPatternKind.TextDigitMask"/> or a <see cref="MaskPatternKind.NumberMask"/>, which always
    /// has a pattern.
    /// </summary>
    [MemberNotNullWhen(true, nameof(Pattern))]
    public bool IsChecked =>
        Pattern is not null && Kind is MaskPatternKind.TextDigitMask or MaskPatternKind.NumberMask;

    /// <summary>
    /// The most integer digits a number mask allows, its count of <c>#</c> before the point; 0 for every other
    /// kind.
    /// </summary>
    public int MaxIntegerDigits { get; }

    /// <summary>
    /// The most decimal digits a number mask allows, its count of <c>#</c> after the point, which is 0 when it
    /// has no point; 0 for every other kind.
    /// </summary>
    public int MaxDecimalDigits { get; }

    /// <summary>
    /// Whether a submitted value satisfies the pattern, decided exactly as
    /// <see cref="MaskPattern.IsSatisfiedBy"/> decides it for the same pattern, field type and column.
    /// </summary>
    /// <param name="value">The normalised submitted value.</param>
    /// <returns>
    /// <see langword="true"/> when the value is null, when the pattern is not checked, or when the value is not of
    /// the kind the mask checks; otherwise whether a text value fits the text digit mask, or whether a number fits
    /// the number mask's digit budget, counted by its exact integer on an integer column and by its double
    /// otherwise.
    /// </returns>
    public bool IsSatisfiedBy(FieldValue value)
    {
        if (value.IsNull || !IsChecked)
        {
            return true;
        }

        if (Kind == MaskPatternKind.TextDigitMask)
        {
            return !value.TryGetText(out var text) || MaskPattern.MatchesTextMask(Pattern, text);
        }

        // An integer column stores the exact integer, so its own digits are counted: the double nearest to a large
        // integer can have one digit more, as 999999999999999999 becomes 1E+18. Every other number is counted as
        // the double a float column stores.
        if (_countsExactInteger && value.TryGetInteger(out var integer))
        {
            return MaskPattern.FitsDigitBudget(integer, MaxIntegerDigits, MaxDecimalDigits);
        }

        return !value.TryGetNumber(out var number)
            || MaskPattern.FitsDigitBudget(number, MaxIntegerDigits, MaxDecimalDigits);
    }
}
