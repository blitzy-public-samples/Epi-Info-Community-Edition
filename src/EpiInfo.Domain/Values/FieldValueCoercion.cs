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
    private const string IsoDateFormat = "yyyy-MM-dd";
    private const string MetadataDateFormat = "MM-dd-yyyy";

    /// <summary>
    /// Coerces a raw scalar into a <see cref="FieldValue"/> of the <paramref name="target"/> kind.
    /// </summary>
    /// <param name="raw">
    /// <see langword="null"/>, a <see cref="string"/>, a <see cref="bool"/>, a numeric primitive
    /// (<see cref="double"/>, <see cref="float"/>, <see cref="decimal"/>, <see cref="int"/>, <see cref="long"/>,
    /// <see cref="short"/>, <see cref="byte"/>, <see cref="sbyte"/>, <see cref="uint"/>, <see cref="ulong"/> or
    /// <see cref="ushort"/>), or a <see cref="DateOnly"/> for a date target.
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
    /// <item><description>a number needs a finite numeric primitive;</description></item>
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
        DateOnly.TryParseExact(text, IsoDateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);

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

    private static FieldValueCoercionResult CoerceNumber(object raw)
    {
        if (!TryGetDouble(raw, out var number))
        {
            return FieldValueCoercionResult.Failure(FieldValueCoercionError.WrongType);
        }

        return double.IsFinite(number)
            ? FieldValueCoercionResult.Success(FieldValue.FromNumber(number))
            : FieldValueCoercionResult.Failure(FieldValueCoercionError.NotFinite);
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

    private static bool TryGetDouble(object raw, out double value)
    {
        switch (raw)
        {
            case double number:
                value = number;
                return true;
            case float number:
                value = number;
                return true;
            case decimal number:
                // Every decimal lies within the range of double, so this explicit conversion cannot overflow.
                value = (double)number;
                return true;
            case int number:
                value = number;
                return true;
            case long number:
                value = number;
                return true;
            case short number:
                value = number;
                return true;
            case byte number:
                value = number;
                return true;
            case sbyte number:
                value = number;
                return true;
            case uint number:
                value = number;
                return true;
            case ulong number:
                value = number;
                return true;
            case ushort number:
                value = number;
                return true;
            default:
                value = 0d;
                return false;
        }
    }
}
