using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace EpiInfo.Domain.Values;

/// <summary>
/// The kind of value a <see cref="FieldValue"/> holds.
/// </summary>
public enum FieldValueKind
{
    /// <summary>No value. Zero, so <c>default(FieldValue)</c> is the null value.</summary>
    Null = 0,

    /// <summary>A string, held by text, multiline and legal-values fields.</summary>
    Text = 1,

    /// <summary>A finite double, held by number fields.</summary>
    Number = 2,

    /// <summary>A boolean, held by checkbox fields.</summary>
    Boolean = 3,

    /// <summary>A calendar date with no time of day, held by date fields.</summary>
    Date = 4,

    /// <summary>A 0-based option index, held by option fields.</summary>
    Index = 5,
}

/// <summary>
/// One typed field value inside the server. It is never serialised as such: the API writes it as a plain
/// JSON scalar and the data layer converts it to a SQL parameter.
/// </summary>
/// <remarks>
/// Each factory fills only the slot of its own kind and leaves every other slot at its default, so the
/// synthesized record equality is exact and values of different kinds are never equal.
/// The value text is field data, which the redaction policy keeps out of logs and error bodies:
/// never pass a <see cref="FieldValue"/>, its <see cref="ToString"/> or its
/// <see cref="ToInvariantString"/> to a logger or an error message.
/// </remarks>
public readonly record struct FieldValue
{
    private readonly string? _text;
    private readonly double _number;
    private readonly bool _boolean;
    private readonly DateOnly _date;
    private readonly int _index;

    private FieldValue(
        FieldValueKind kind,
        string? text = null,
        double number = 0d,
        bool boolean = false,
        DateOnly date = default,
        int index = 0)
    {
        Kind = kind;
        _text = text;
        _number = number;
        _boolean = boolean;
        _date = date;
        _index = index;
    }

    /// <summary>The null value, equal to <c>default(FieldValue)</c>.</summary>
    public static FieldValue Null => default;

    /// <summary>The kind of value held.</summary>
    public FieldValueKind Kind { get; }

    /// <summary>Whether this is the null value.</summary>
    public bool IsNull => Kind == FieldValueKind.Null;

    /// <summary>Creates a text value. An empty string stays text; it is not turned into <see cref="Null"/>.</summary>
    /// <param name="value">The text.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    public static FieldValue FromText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new FieldValue(FieldValueKind.Text, text: value);
    }

    /// <summary>Creates a number value. Negative zero is stored as zero.</summary>
    /// <param name="value">A finite number.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is NaN or infinite.</exception>
    public static FieldValue FromNumber(double value)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "A number field value must be finite.");
        }

        // -0 == 0 is true, so this replaces -0 with +0 and keeps equality, hashing and formatting stable.
        if (value == 0d)
        {
            value = 0d;
        }

        return new FieldValue(FieldValueKind.Number, number: value);
    }

    /// <summary>Creates a boolean value.</summary>
    /// <param name="value">The boolean.</param>
    public static FieldValue FromBoolean(bool value) => new(FieldValueKind.Boolean, boolean: value);

    /// <summary>
    /// Creates a date value. The storage range of the column is checked by the validator and the search
    /// parser, not here.
    /// </summary>
    /// <param name="value">The calendar date.</param>
    public static FieldValue FromDate(DateOnly value) => new(FieldValueKind.Date, date: value);

    /// <summary>
    /// Creates a 0-based option index. The upper bound, the option count, is checked by the caller.
    /// </summary>
    /// <param name="value">The index, 0 or greater.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is negative.</exception>
    public static FieldValue FromIndex(int value)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "An option index must not be negative.");
        }

        return new FieldValue(FieldValueKind.Index, index: value);
    }

    /// <summary>Gets the text when this is a text value.</summary>
    /// <param name="value">The text, or <see langword="null"/> when this is not a text value.</param>
    /// <returns>Whether this is a text value.</returns>
    public bool TryGetText([NotNullWhen(true)] out string? value)
    {
        value = Kind == FieldValueKind.Text ? _text : null;
        return value is not null;
    }

    /// <summary>Gets the number when this is a number value.</summary>
    /// <param name="value">The number, or 0 when this is not a number value.</param>
    /// <returns>Whether this is a number value.</returns>
    public bool TryGetNumber(out double value)
    {
        value = Kind == FieldValueKind.Number ? _number : default;
        return Kind == FieldValueKind.Number;
    }

    /// <summary>Gets the boolean when this is a boolean value.</summary>
    /// <param name="value">The boolean, or <see langword="false"/> when this is not a boolean value.</param>
    /// <returns>Whether this is a boolean value.</returns>
    public bool TryGetBoolean(out bool value)
    {
        value = Kind == FieldValueKind.Boolean && _boolean;
        return Kind == FieldValueKind.Boolean;
    }

    /// <summary>Gets the date when this is a date value.</summary>
    /// <param name="value">The date, or <c>default(DateOnly)</c> when this is not a date value.</param>
    /// <returns>Whether this is a date value.</returns>
    public bool TryGetDate(out DateOnly value)
    {
        value = Kind == FieldValueKind.Date ? _date : default;
        return Kind == FieldValueKind.Date;
    }

    /// <summary>Gets the option index when this is an index value.</summary>
    /// <param name="value">The index, or 0 when this is not an index value.</param>
    /// <returns>Whether this is an index value.</returns>
    public bool TryGetIndex(out int value)
    {
        value = Kind == FieldValueKind.Index ? _index : default;
        return Kind == FieldValueKind.Index;
    }

    /// <summary>Returns the text.</summary>
    /// <exception cref="InvalidOperationException">This is not a text value.</exception>
    public string AsText() => TryGetText(out var value) ? value : throw KindMismatch(FieldValueKind.Text);

    /// <summary>Returns the number.</summary>
    /// <exception cref="InvalidOperationException">This is not a number value.</exception>
    public double AsNumber() => TryGetNumber(out var value) ? value : throw KindMismatch(FieldValueKind.Number);

    /// <summary>Returns the boolean.</summary>
    /// <exception cref="InvalidOperationException">This is not a boolean value.</exception>
    public bool AsBoolean() => TryGetBoolean(out var value) ? value : throw KindMismatch(FieldValueKind.Boolean);

    /// <summary>Returns the date.</summary>
    /// <exception cref="InvalidOperationException">This is not a date value.</exception>
    public DateOnly AsDate() => TryGetDate(out var value) ? value : throw KindMismatch(FieldValueKind.Date);

    /// <summary>Returns the option index.</summary>
    /// <exception cref="InvalidOperationException">This is not an index value.</exception>
    public int AsIndex() => TryGetIndex(out var value) ? value : throw KindMismatch(FieldValueKind.Index);

    /// <summary>
    /// Formats the value with the invariant culture: <see langword="null"/> for the null value, text as
    /// stored, a number in round-trip form (<c>37.5</c>), <c>true</c> or <c>false</c>, a date as
    /// <c>yyyy-MM-dd</c>, and an index as an integer.
    /// </summary>
    /// <returns>The formatted value, or <see langword="null"/> for the null value.</returns>
    public string? ToInvariantString() => Kind switch
    {
        FieldValueKind.Text => _text,
        FieldValueKind.Number => _number.ToString("R", CultureInfo.InvariantCulture),
        FieldValueKind.Boolean => _boolean ? "true" : "false",
        FieldValueKind.Date => _date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        FieldValueKind.Index => _index.ToString(CultureInfo.InvariantCulture),

        // Only Null remains: the private constructor is reached solely through the factories above.
        _ => null,
    };

    /// <summary>
    /// A culture-independent debugging form: <c>Null</c>, or the kind and the invariant value, such as
    /// <c>Number:37.5</c>. It contains field data, so it must never reach a log or an error message.
    /// </summary>
    /// <returns>The debugging form.</returns>
    public override string ToString() => IsNull ? "Null" : $"{Kind}:{ToInvariantString()}";

    private InvalidOperationException KindMismatch(FieldValueKind expected) =>
        new($"Expected a field value of kind {expected}, but its kind is {Kind}.");
}
