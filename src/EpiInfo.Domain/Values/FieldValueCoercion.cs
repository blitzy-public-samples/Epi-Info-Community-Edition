using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace EpiInfo.Domain.Values;

/// <summary>
/// The machine-readable reason a coercion failed. It carries no value text: callers build the user-facing
/// message, and the redaction policy keeps the raw value out of every message and log line.
/// </summary>
public enum FieldValueCoercionError
{
    /// <summary>The coercion succeeded.</summary>
    None = 0,

    /// <summary>
    /// The raw value has the wrong CLR type for the target kind, for example a string or a boolean for a
    /// number, a number for a boolean, or anything other than a string for text.
    /// </summary>
    WrongType,

    /// <summary>A number or option index is NaN or infinite.</summary>
    NotFinite,

    /// <summary>A date string is not a valid calendar date in the exact form <c>yyyy-MM-dd</c>.</summary>
    InvalidDate,

    /// <summary>An option index has a fractional part, such as <c>2.5</c>.</summary>
    NotIntegral,

    /// <summary>An option index is below 0.</summary>
    Negative,

    /// <summary>An option index is above <see cref="int.MaxValue"/>.</summary>
    OutOfRange,

    /// <summary>
    /// The target kind cannot hold a value: it is <see cref="FieldValueKind.Null"/> with a non-null raw value,
    /// or it is not a defined <see cref="FieldValueKind"/>.
    /// </summary>
    UnsupportedTarget,
}

/// <summary>
/// The outcome of a coercion: the value when it succeeded, or <see cref="FieldValue.Null"/> and the reason
/// when it failed.
/// </summary>
/// <param name="Value">The coerced value; <see cref="FieldValue.Null"/> when the coercion failed.</param>
/// <param name="Error">The failure reason, or <see cref="FieldValueCoercionError.None"/> on success.</param>
public readonly record struct FieldValueCoercionResult(FieldValue Value, FieldValueCoercionError Error)
{
    /// <summary>Whether the coercion succeeded.</summary>
    public bool Succeeded => Error == FieldValueCoercionError.None;

    /// <summary>Creates a successful result holding <paramref name="value"/>.</summary>
    /// <param name="value">The coerced value.</param>
    /// <returns>The successful result.</returns>
    public static FieldValueCoercionResult Success(FieldValue value) => new(value, FieldValueCoercionError.None);

    /// <summary>
    /// Creates a failed result whose value is <see cref="FieldValue.Null"/>. A failure must name a reason, so
    /// <see cref="FieldValueCoercionError.None"/> is reported as
    /// <see cref="FieldValueCoercionError.UnsupportedTarget"/> rather than as a success.
    /// </summary>
    /// <param name="error">The failure reason.</param>
    /// <returns>The failed result.</returns>
    public static FieldValueCoercionResult Failure(FieldValueCoercionError error) => new(
        FieldValue.Null,
        error == FieldValueCoercionError.None ? FieldValueCoercionError.UnsupportedTarget : error);
}

/// <summary>
/// Culture-independent conversion of raw scalars and metadata or search strings into field values. Every
/// member is pure, and bad input is reported through the return value, never by an exception.
/// </summary>
/// <remarks>
/// Only the shape of a value is checked here. Storage ranges, lengths, bounds, the option count, emptiness and
/// required fields are the validator's rules, and trimming of search input is the search parser's.
/// </remarks>
public static class FieldValueCoercion
{
    private const string MetadataDateFormat = "MM-dd-yyyy";

    /// <summary>
    /// Coerces a raw scalar into a <see cref="FieldValue"/> of the <paramref name="target"/> kind.
    /// </summary>
    /// <param name="raw">
    /// <see langword="null"/>, a <see cref="string"/>, a <see cref="bool"/>, a numeric primitive
    /// (<see cref="double"/>, <see cref="float"/>, <see cref="decimal"/>, <see cref="int"/>, <see cref="long"/>,
    /// <see cref="short"/>, <see cref="byte"/>, <see cref="sbyte"/>, <see cref="uint"/>, <see cref="ulong"/> or
    /// <see cref="ushort"/>), or a <see cref="DateOnly"/> for a date target. A boxed <see cref="FieldValue"/> is
    /// coerced by the <see cref="Coerce(FieldValue, FieldValueKind, bool)"/> overload.
    /// </param>
    /// <param name="target">The kind of value the field holds.</param>
    /// <param name="normalizeEmptyText">
    /// Whether an empty string for a text target becomes <see cref="FieldValue.Null"/>. Only the empty string
    /// is normalised; whitespace is kept as entered.
    /// </param>
    /// <returns>
    /// <see cref="FieldValue.Null"/> for a <see langword="null"/> raw value whatever the target; otherwise the
    /// value of the target kind, or the reason the raw value cannot be one:
    /// <list type="bullet">
    /// <item><description>text needs a string;</description></item>
    /// <item><description>
    /// a number needs a finite numeric primitive, kept exact: an integer primitive, or a whole decimal, within the
    /// signed 64-bit range keeps its exact integer, and any other value keeps its nearest double without being
    /// rounded into a whole number;
    /// </description></item>
    /// <item><description>a boolean needs a <see cref="bool"/>;</description></item>
    /// <item><description>a date needs a <c>yyyy-MM-dd</c> string or a <see cref="DateOnly"/>;</description></item>
    /// <item><description>an index needs an integral numeric primitive from 0 to <see cref="int.MaxValue"/>.</description></item>
    /// </list>
    /// </returns>
    public static FieldValueCoercionResult Coerce(object? raw, FieldValueKind target, bool normalizeEmptyText = false)
    {
        if (raw is null)
        {
            return FieldValueCoercionResult.Success(FieldValue.Null);
        }

        if (raw is FieldValue value)
        {
            return Coerce(value, target, normalizeEmptyText);
        }

        return target switch
        {
            FieldValueKind.Text => CoerceText(raw, normalizeEmptyText),
            FieldValueKind.Number => CoerceNumber(raw),
            FieldValueKind.Boolean => raw is bool boolean
                ? FieldValueCoercionResult.Success(FieldValue.FromBoolean(boolean))
                : FieldValueCoercionResult.Failure(FieldValueCoercionError.WrongType),
            FieldValueKind.Date => CoerceDate(raw),
            FieldValueKind.Index => CoerceIndex(raw),

            // FieldValueKind.Null and any undefined kind cannot hold a non-null value.
            _ => FieldValueCoercionResult.Failure(FieldValueCoercionError.UnsupportedTarget),
        };
    }

    /// <summary>
    /// Coerces a value already read from the wire, such as one the JSON converter produced field-type blind,
    /// into a <see cref="FieldValue"/> of the <paramref name="target"/> kind, keeping a number exact.
    /// </summary>
    /// <param name="raw">
    /// The value as read: <see cref="FieldValue.Null"/>, text, a number, a boolean, a date or an option index.
    /// </param>
    /// <param name="target">The kind of value the field holds.</param>
    /// <param name="normalizeEmptyText">
    /// Whether empty text for a text target becomes <see cref="FieldValue.Null"/>. Only the empty string is
    /// normalised; whitespace is kept as entered.
    /// </param>
    /// <returns>
    /// For a number: the number unchanged for a number target; for an index target, an index when the number
    /// holds an exact integer (<see cref="FieldValue.TryGetInteger"/>) from 0 to <see cref="int.MaxValue"/>,
    /// otherwise <see cref="FieldValueCoercionError.NotIntegral"/>, <see cref="FieldValueCoercionError.Negative"/>
    /// or <see cref="FieldValueCoercionError.OutOfRange"/>, so <c>2.0000000000000001</c> is never index 2;
    /// <see cref="FieldValueCoercionError.WrongType"/> for a text, boolean or date target; and
    /// <see cref="FieldValueCoercionError.UnsupportedTarget"/> for a <see cref="FieldValueKind.Null"/> or undefined
    /// target. Any other value is coerced as its CLR primitive by the
    /// <see cref="Coerce(object, FieldValueKind, bool)"/> overload.
    /// </returns>
    public static FieldValueCoercionResult Coerce(FieldValue raw, FieldValueKind target, bool normalizeEmptyText = false)
    {
        if (raw.Kind == FieldValueKind.Number)
        {
            return target switch
            {
                FieldValueKind.Number => FieldValueCoercionResult.Success(raw),
                FieldValueKind.Index => IndexFromNumber(raw),
                FieldValueKind.Text or FieldValueKind.Boolean or FieldValueKind.Date =>
                    FieldValueCoercionResult.Failure(FieldValueCoercionError.WrongType),

                // FieldValueKind.Null and any undefined kind cannot hold a non-null value.
                _ => FieldValueCoercionResult.Failure(FieldValueCoercionError.UnsupportedTarget),
            };
        }

        // Every other kind holds exactly its CLR primitive, so the primitive rules apply unchanged.
        return raw.Kind switch
        {
            FieldValueKind.Null => Coerce((object?)null, target, normalizeEmptyText),
            FieldValueKind.Text => Coerce(raw.AsText(), target, normalizeEmptyText),
            FieldValueKind.Boolean => Coerce(raw.AsBoolean(), target, normalizeEmptyText),
            FieldValueKind.Date => Coerce(raw.AsDate(), target, normalizeEmptyText),
            FieldValueKind.Index => Coerce(raw.AsIndex(), target, normalizeEmptyText),

            // The factories create no other kind; a value of an undefined kind fits no target.
            _ => FieldValueCoercionResult.Failure(FieldValueCoercionError.WrongType),
        };
    }

    /// <summary>
    /// Parses a date in exactly the form <c>yyyy-MM-dd</c> with the invariant culture, without trimming.
    /// The storage range of the column is not checked here.
    /// </summary>
    /// <param name="text">The text, such as <c>2011-01-15</c>.</param>
    /// <param name="value">The date, or <c>default(DateOnly)</c> when parsing fails.</param>
    /// <returns>
    /// Whether <paramref name="text"/> is a valid calendar date in that form; <see langword="false"/> for
    /// <see langword="null"/>, empty or whitespace input, surrounding whitespace, a time of day or another
    /// format.
    /// </returns>
    public static bool TryParseIsoDate([NotNullWhen(true)] string? text, out DateOnly value) =>
        DateOnly.TryParseExact(text, FieldValue.IsoDateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);

    /// <summary>
    /// Parses a metadata date bound, stored as <c>MM-dd-yyyy</c> (for example <c>01-15-2011</c>), after
    /// trimming surrounding whitespace.
    /// </summary>
    /// <param name="text">The stored bound text.</param>
    /// <param name="value">The date, or <c>default(DateOnly)</c> when parsing fails.</param>
    /// <returns>
    /// Whether the trimmed text is a valid calendar date in that form; <see langword="false"/> for
    /// <see langword="null"/>, empty or whitespace input, keywords such as <c>SYSTEMDATE</c>, and any other
    /// format, ISO dates included.
    /// </returns>
    public static bool TryParseMetadataDate([NotNullWhen(true)] string? text, out DateOnly value)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            value = default;
            return false;
        }

        return DateOnly.TryParseExact(
            text.Trim(),
            MetadataDateFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out value);
    }

    /// <summary>
    /// Parses a finite number with the invariant culture: surrounding whitespace, a leading sign, a decimal
    /// point and an exponent are allowed, and thousands separators are not. Negative zero becomes zero.
    /// The result is the nearest double; <see cref="TryParseNumber(string, out FieldValue)"/> keeps a whole
    /// number exact and never rounds a fraction into one.
    /// </summary>
    /// <param name="text">The text, such as <c>120</c> or <c>37.5</c>.</param>
    /// <param name="value">The number, or 0 when parsing fails.</param>
    /// <returns>
    /// Whether <paramref name="text"/> is a finite number; <see langword="false"/> for <see langword="null"/>,
    /// empty or whitespace input, <c>NaN</c>, infinities, values that overflow to infinity, and text such as
    /// <c>1,000</c> or <c>37,5</c>.
    /// </returns>
    public static bool TryParseInvariantNumber([NotNullWhen(true)] string? text, out double value)
    {
        // The invariant culture accepts "NaN", "Infinity" and "∞", and overflow yields an infinity, so a
        // successful parse alone does not make a field value.
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            || !double.IsFinite(value))
        {
            value = 0d;
            return false;
        }

        // -0 == 0, so this replaces negative zero with positive zero, as FieldValue.FromNumber stores it.
        if (value == 0d)
        {
            value = 0d;
        }

        return true;
    }

    /// <summary>
    /// Parses a finite number exactly, with the lexical rules of
    /// <see cref="TryParseInvariantNumber(string, out double)"/>: the invariant culture, surrounding whitespace, a
    /// leading sign, a decimal point and an exponent, and no thousands separators. A whole number within the
    /// signed 64-bit range, such as <c>9007199254740993</c>, <c>2.0</c> or <c>2e3</c>, keeps its exact integer;
    /// any other number keeps its nearest double without being rounded into a whole number, so
    /// <c>2.0000000000000001</c> is not the integer 2. Negative zero becomes zero.
    /// </summary>
    /// <param name="text">Search or metadata text, such as <c>120</c> or <c>37.5</c>.</param>
    /// <param name="value">The number, or <see cref="FieldValue.Null"/> when parsing fails.</param>
    /// <returns>
    /// Whether <paramref name="text"/> is a finite number; <see langword="false"/> for <see langword="null"/>,
    /// empty or whitespace input, <c>NaN</c>, infinities, values that overflow to infinity, and text such as
    /// <c>1,000</c> or <c>37,5</c>.
    /// </returns>
    public static bool TryParseNumber([NotNullWhen(true)] string? text, out FieldValue value)
    {
        if (long.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var integer))
        {
            value = FieldValue.FromInteger(integer);
            return true;
        }

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var nearest))
        {
            return TryFromNonInt64Number(nearest, out value);
        }

        value = FieldValue.Null;
        return false;
    }

    /// <summary>
    /// Parses a finite number from UTF-8 text exactly, with the rules of
    /// <see cref="TryParseNumber(string, out FieldValue)"/>. It serves JSON number literals, which the JSON reader
    /// has already checked against the JSON number grammar, so <c>9007199254740993</c> keeps its exact integer
    /// and <c>1e400</c> is refused rather than read as infinity.
    /// </summary>
    /// <param name="utf8Text">The UTF-8 text, such as a JSON number literal.</param>
    /// <param name="value">The number, or <see cref="FieldValue.Null"/> when parsing fails.</param>
    /// <returns>
    /// Whether <paramref name="utf8Text"/> is a finite number; <see langword="false"/> for empty input, text that
    /// is not a number, and values that overflow to infinity.
    /// </returns>
    public static bool TryParseNumber(ReadOnlySpan<byte> utf8Text, out FieldValue value)
    {
        if (long.TryParse(utf8Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var integer))
        {
            value = FieldValue.FromInteger(integer);
            return true;
        }

        if (double.TryParse(utf8Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var nearest))
        {
            return TryFromNonInt64Number(nearest, out value);
        }

        value = FieldValue.Null;
        return false;
    }

    /// <summary>
    /// Parses a 32-bit integer with the invariant culture: surrounding whitespace and a leading sign are
    /// allowed, and a decimal point, an exponent or thousands separators are not.
    /// </summary>
    /// <param name="text">The text, such as <c>2</c> or <c> -1 </c>.</param>
    /// <param name="value">The integer, or 0 when parsing fails.</param>
    /// <returns>
    /// Whether <paramref name="text"/> is an integer within the range of <see cref="int"/>;
    /// <see langword="false"/> for <see langword="null"/>, empty or whitespace input, <c>2.0</c> and
    /// <c>2147483648</c>.
    /// </returns>
    public static bool TryParseInvariantInteger([NotNullWhen(true)] string? text, out int value) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    /// <summary>
    /// Whether a stored metadata bound is unset. Bounds are stored as <c>NULL</c> or as an empty string when
    /// unset, and both surface as <see langword="null"/> in the field definition.
    /// </summary>
    /// <param name="text">The stored bound text.</param>
    /// <returns>
    /// <see langword="true"/> for <see langword="null"/>, empty or whitespace text; otherwise
    /// <see langword="false"/>.
    /// </returns>
    public static bool IsUnsetBound([NotNullWhen(false)] string? text) => string.IsNullOrWhiteSpace(text);

    private static FieldValueCoercionResult CoerceText(object raw, bool normalizeEmptyText)
    {
        if (raw is not string text)
        {
            return FieldValueCoercionResult.Failure(FieldValueCoercionError.WrongType);
        }

        // Legacy data entry writes NULL for an empty string, so a save may ask for the same normalisation.
        // Whitespace is data and stays as entered.
        return normalizeEmptyText && text.Length == 0
            ? FieldValueCoercionResult.Success(FieldValue.Null)
            : FieldValueCoercionResult.Success(FieldValue.FromText(text));
    }

    // Integer primitives become exact integers, never doubles, so a bigint beyond 2^53 keeps its value; only a
    // value that no signed 64-bit integer holds keeps its nearest double, and then never as a whole number.
    private static FieldValueCoercionResult CoerceNumber(object raw) => raw switch
    {
        double value => NumberFromDouble(value),
        float value => NumberFromDouble(value),
        int value => FieldValueCoercionResult.Success(FieldValue.FromInteger(value)),
        long value => FieldValueCoercionResult.Success(FieldValue.FromInteger(value)),
        short value => FieldValueCoercionResult.Success(FieldValue.FromInteger(value)),
        sbyte value => FieldValueCoercionResult.Success(FieldValue.FromInteger(value)),
        byte value => FieldValueCoercionResult.Success(FieldValue.FromInteger(value)),
        ushort value => FieldValueCoercionResult.Success(FieldValue.FromInteger(value)),
        uint value => FieldValueCoercionResult.Success(FieldValue.FromInteger(value)),
        ulong value => FieldValueCoercionResult.Success(value <= long.MaxValue
            ? FieldValue.FromInteger((long)value)
            : FieldValue.FromNonInt64Number(value)),
        decimal value => NumberFromDecimal(value),
        _ => FieldValueCoercionResult.Failure(FieldValueCoercionError.WrongType),
    };

    // A double or float is exact as given, so FromNumber keeps a whole one as its exact integer.
    private static FieldValueCoercionResult NumberFromDouble(double value) => double.IsFinite(value)
        ? FieldValueCoercionResult.Success(FieldValue.FromNumber(value))
        : FieldValueCoercionResult.Failure(FieldValueCoercionError.NotFinite);

    // A decimal can hold a fraction a double rounds away, such as 2.0000000000000001, so it is tested for a whole
    // value in decimal arithmetic. Every decimal lies within the range of double, so the conversion cannot overflow.
    private static FieldValueCoercionResult NumberFromDecimal(decimal value) =>
        FieldValueCoercionResult.Success(
            decimal.Truncate(value) == value && value >= long.MinValue && value <= long.MaxValue
                ? FieldValue.FromInteger((long)value)
                : FieldValue.FromNonInt64Number((double)value));

    // long.TryParse refuses every number that is not exactly a whole number within the signed 64-bit range, so a
    // number reaching here keeps only its nearest double. The invariant culture accepts "NaN", "Infinity" and "∞",
    // and overflow yields an infinity, so a successful double parse alone does not make a field value.
    private static bool TryFromNonInt64Number(double nearest, out FieldValue value)
    {
        if (!double.IsFinite(nearest))
        {
            value = FieldValue.Null;
            return false;
        }

        value = FieldValue.FromNonInt64Number(nearest);
        return true;
    }

    private static FieldValueCoercionResult CoerceDate(object raw) => raw switch
    {
        string text => TryParseIsoDate(text, out var date)
            ? FieldValueCoercionResult.Success(FieldValue.FromDate(date))
            : FieldValueCoercionResult.Failure(FieldValueCoercionError.InvalidDate),
        DateOnly date => FieldValueCoercionResult.Success(FieldValue.FromDate(date)),
        _ => FieldValueCoercionResult.Failure(FieldValueCoercionError.WrongType),
    };

    // Integer primitives are range-checked in their own type (or as long/ulong), never through double,
    // so a large long or ulong cannot lose precision and slip past the bounds.
    private static FieldValueCoercionResult CoerceIndex(object raw) => raw switch
    {
        int value => IndexFromSigned(value),
        long value => IndexFromSigned(value),
        short value => IndexFromSigned(value),
        sbyte value => IndexFromSigned(value),
        byte value => IndexFromUnsigned(value),
        ushort value => IndexFromUnsigned(value),
        uint value => IndexFromUnsigned(value),
        ulong value => IndexFromUnsigned(value),
        double value => IndexFromDouble(value),
        float value => IndexFromDouble(value),
        decimal value => IndexFromDecimal(value),
        _ => FieldValueCoercionResult.Failure(FieldValueCoercionError.WrongType),
    };

    private static FieldValueCoercionResult IndexFromSigned(long value)
    {
        if (value < 0)
        {
            return FieldValueCoercionResult.Failure(FieldValueCoercionError.Negative);
        }

        return value > int.MaxValue
            ? FieldValueCoercionResult.Failure(FieldValueCoercionError.OutOfRange)
            : FieldValueCoercionResult.Success(FieldValue.FromIndex((int)value));
    }

    private static FieldValueCoercionResult IndexFromUnsigned(ulong value) => value > int.MaxValue
        ? FieldValueCoercionResult.Failure(FieldValueCoercionError.OutOfRange)
        : FieldValueCoercionResult.Success(FieldValue.FromIndex((int)value));

    private static FieldValueCoercionResult IndexFromDouble(double value)
    {
        if (!double.IsFinite(value))
        {
            return FieldValueCoercionResult.Failure(FieldValueCoercionError.NotFinite);
        }

        if (Math.Truncate(value) != value)
        {
            return FieldValueCoercionResult.Failure(FieldValueCoercionError.NotIntegral);
        }

        if (value < 0d)
        {
            return FieldValueCoercionResult.Failure(FieldValueCoercionError.Negative);
        }

        // Every integral double from 0 to int.MaxValue converts exactly; -0 is not below 0 and becomes index 0.
        return value > int.MaxValue
            ? FieldValueCoercionResult.Failure(FieldValueCoercionError.OutOfRange)
            : FieldValueCoercionResult.Success(FieldValue.FromIndex((int)value));
    }

    private static FieldValueCoercionResult IndexFromDecimal(decimal value)
    {
        if (decimal.Truncate(value) != value)
        {
            return FieldValueCoercionResult.Failure(FieldValueCoercionError.NotIntegral);
        }

        if (value < 0m)
        {
            return FieldValueCoercionResult.Failure(FieldValueCoercionError.Negative);
        }

        return value > int.MaxValue
            ? FieldValueCoercionResult.Failure(FieldValueCoercionError.OutOfRange)
            : FieldValueCoercionResult.Success(FieldValue.FromIndex((int)value));
    }

    // A number value decides on its exact integer, never on its double. One without an exact integer is either
    // non-integral or an integer beyond the signed 64-bit range. Rounding is monotonic and -2^63 and 2^63 are exact
    // doubles, so an integer beyond that range has a nearest double at or past one of them, and a nearest double
    // strictly between them can only stand for a non-integral number.
    private static FieldValueCoercionResult IndexFromNumber(FieldValue number)
    {
        if (number.TryGetInteger(out var integer))
        {
            return IndexFromSigned(integer);
        }

        var nearest = number.AsNumber();
        if (nearest > -9223372036854775808d && nearest < 9223372036854775808d)
        {
            return FieldValueCoercionResult.Failure(FieldValueCoercionError.NotIntegral);
        }

        return FieldValueCoercionResult.Failure(
            nearest < 0d ? FieldValueCoercionError.Negative : FieldValueCoercionError.OutOfRange);
    }
}
