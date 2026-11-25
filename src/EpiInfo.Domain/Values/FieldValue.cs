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

    /// <summary>
    /// A finite number, held by number fields: an exact signed 64-bit integer when the number is a whole number
    /// within that range, otherwise its floor when that lies within the range, and always its nearest double.
    /// </summary>
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
/// Each factory fills only the slots of its own kind and leaves every other slot at its default, so the
/// synthesized record equality is exact and values of different kinds are never equal. A number fills three
/// slots: its nearest double always; its exact signed 64-bit integer when the number is exactly a whole number
/// within that range; and otherwise its floor, the greatest integer not above its exact value, when that floor
/// lies within the range. Two numbers are therefore equal only when all three slots agree, so
/// <c>FromNumber(2)</c> equals <c>FromInteger(2)</c>, a number never equals the option index of the same
/// value, a non-integral number whose nearest double is whole never equals that whole number, and two numbers
/// that hold no exact integer are equal only when their nearest doubles and their floors agree, so
/// <c>2.0000000000000001</c> and <c>1.9999999999999999</c>, which share the nearest double 2, differ.
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

    // Has a value exactly when Kind is Number and the number is a whole number within the signed 64-bit
    // range; _number then holds its nearest double.
    private readonly long? _integer;

    // Has a value exactly when Kind is Number, the number holds no exact 64-bit integer, and the greatest integer
    // not above its exact value lies within the signed 64-bit range. It keeps the integer interval the exact number
    // lies in, which its nearest double can lose: 9007199254740992.5 has the nearest double 9007199254740992.
    private readonly long? _floor;

    private FieldValue(
        FieldValueKind kind,
        string? text = null,
        double number = 0d,
        bool boolean = false,
        DateOnly date = default,
        int index = 0,
        long? integer = null,
        long? floor = null)
    {
        Kind = kind;
        _text = text;
        _number = number;
        _boolean = boolean;
        _date = date;
        _index = index;
        _integer = integer;
        _floor = floor;
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

    /// <summary>
    /// Creates a number value from a double, taken as exact. Negative zero is stored as zero. A whole number
    /// within the signed 64-bit range also keeps that exact integer, so <c>FromNumber(2)</c> equals
    /// <c>FromInteger(2)</c> and <see cref="TryGetInteger"/> returns it; a non-integral number keeps its floor,
    /// which <see cref="TryGetFloor"/> returns.
    /// </summary>
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

        if (Math.Truncate(value) != value)
        {
            // Every double of magnitude 2^52 or more is whole, so a non-integral one has an exact floor well
            // within the signed 64-bit range.
            return new FieldValue(FieldValueKind.Number, number: value, floor: (long)Math.Floor(value));
        }

        // -2^63 and 2^63 are exact doubles, so this range holds exactly the whole doubles a long can represent,
        // and the cast cannot overflow. A whole double beyond it has no floor within the range.
        long? integer = value >= -9223372036854775808d && value < 9223372036854775808d
            ? (long)value
            : null;
        return new FieldValue(FieldValueKind.Number, number: value, integer: integer);
    }

    /// <summary>
    /// Creates a number value holding an exact signed 64-bit integer, such as a JSON integer above 2^53 or a
    /// value read from a <c>bigint</c> column. <see cref="TryGetInteger"/> returns it unchanged; its double,
    /// from <see cref="TryGetNumber"/>, is the nearest one, which may be rounded beyond 2^53.
    /// </summary>
    /// <param name="value">The integer.</param>
    public static FieldValue FromInteger(long value) => new(FieldValueKind.Number, number: value, integer: value);

    /// <summary>
    /// Creates a number value for a finite number whose exact value is not a whole number within the signed
    /// 64-bit range: a non-integral number such as <c>2.0000000000000001</c>, or an integer beyond that range
    /// such as <c>9223372036854775808</c>. Its nearest double and its exact floor are kept, and
    /// <see cref="TryGetInteger"/> returns <see langword="false"/> even when that double is whole, so rounding
    /// cannot turn the number into an integer or an option index. Negative zero is stored as zero.
    /// </summary>
    /// <remarks>
    /// When <paramref name="nearest"/> is not whole, the floor is fully determined by it: no integer lies between
    /// a number and its non-integral nearest double, because every integer of that magnitude is itself a double.
    /// A different floor is therefore a caller error and is rejected. When <paramref name="nearest"/> is whole,
    /// as for <c>2.0000000000000001</c>, <c>1.9999999999999999</c> or <c>1e-400</c>, only the caller knows the
    /// exact number, and its floor is kept as given.
    /// </remarks>
    /// <param name="nearest">The double nearest to the number.</param>
    /// <param name="floor">
    /// The greatest integer not above the number's exact value, or <see langword="null"/> when that integer lies
    /// beyond the signed 64-bit range.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="nearest"/> is NaN or infinite.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="nearest"/> is not whole and <paramref name="floor"/> is not its floor.
    /// </exception>
    internal static FieldValue FromNonInt64Number(double nearest, long? floor)
    {
        if (!double.IsFinite(nearest))
        {
            throw new ArgumentOutOfRangeException(nameof(nearest), "A number field value must be finite.");
        }

        // A non-integral double has magnitude below 2^52, so its floor converts to long exactly.
        if (Math.Truncate(nearest) != nearest && floor != (long)Math.Floor(nearest))
        {
            throw new ArgumentException(
                "The floor of a number whose nearest double is not whole must be the floor of that double.",
                nameof(floor));
        }

        // A number nearer to zero than the smallest double, such as -1e-400, rounds to -0; it is stored as +0,
        // and its floor, -1, keeps its sign.
        if (nearest == 0d)
        {
            nearest = 0d;
        }

        return new FieldValue(FieldValueKind.Number, number: nearest, floor: floor);
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
    /// The one calendar-date form of a date value: year, month and day, such as <c>2011-01-15</c>, with no
    /// time of day, always read and written with the invariant culture. Date values take this form on the
    /// wire, in search criteria and in validation-bound text, and <c>FieldValueCoercion.TryParseIsoDate</c>
    /// parses exactly this form.
    /// </summary>
    public const string IsoDateFormat = "yyyy-MM-dd";

    /// <summary>Formats a calendar date in the <see cref="IsoDateFormat"/> form with the invariant culture.</summary>
    /// <param name="value">The calendar date.</param>
    /// <returns>The date as <c>yyyy-MM-dd</c>, such as <c>2011-01-15</c>, whatever the current culture.</returns>
    public static string FormatIsoDate(DateOnly value) => value.ToString(IsoDateFormat, CultureInfo.InvariantCulture);

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

    /// <summary>
    /// Gets the nearest double to the number when this is a number value. It is exact for a <c>float</c>
    /// column, but may be rounded for an integer beyond 2^53 or a decimal a double cannot hold, so an option
    /// index decides with <see cref="TryGetInteger"/> instead, and an integer column with
    /// <see cref="TryGetInteger"/> and <see cref="TryGetFloor"/>.
    /// </summary>
    /// <param name="value">The nearest double, or 0 when this is not a number value.</param>
    /// <returns>Whether this is a number value.</returns>
    public bool TryGetNumber(out double value)
    {
        value = Kind == FieldValueKind.Number ? _number : default;
        return Kind == FieldValueKind.Number;
    }

    /// <summary>
    /// Gets the exact integer when this is a number value that is exactly a whole number within the signed
    /// 64-bit range. Integer columns (<c>tinyint</c>, <c>smallint</c>, <c>int</c>, <c>bigint</c>) and option
    /// indexes must decide with this, never with the double, which can round a number into a whole one.
    /// </summary>
    /// <param name="value">The exact integer, or 0 when this is not such a number.</param>
    /// <returns>
    /// Whether this is a number value holding an exact signed 64-bit integer; <see langword="false"/> for a
    /// non-integral number such as <c>2.0000000000000001</c>, an integer beyond that range, and every other kind.
    /// </returns>
    public bool TryGetInteger(out long value)
    {
        if (Kind == FieldValueKind.Number && _integer is { } integer)
        {
            value = integer;
            return true;
        }

        value = 0L;
        return false;
    }

    /// <summary>
    /// Gets the floor, the greatest integer not above the number's exact value, when this is a number value that
    /// holds no exact 64-bit integer and whose floor lies within the signed 64-bit range. It places the exact
    /// number strictly inside the integer interval from the floor to the floor plus 1, which its nearest double
    /// may not: <c>9007199254740992.5</c> has the floor <c>9007199254740992</c> and the same nearest double, and
    /// <c>1.9999999999999999</c> has the floor 1 and the nearest double 2. An integer column compares with
    /// <see cref="TryGetInteger"/>, then with this, and for a number beyond the signed 64-bit range, where both
    /// return <see langword="false"/>, with the sign of <see cref="TryGetNumber"/>.
    /// </summary>
    /// <param name="value">The floor, or 0 when this is not such a number.</param>
    /// <returns>
    /// Whether this is a number value holding no exact 64-bit integer whose floor lies within the signed 64-bit
    /// range; <see langword="false"/> for an exact integer such as <c>FromInteger(2)</c> or <c>FromNumber(2)</c>,
    /// a number whose floor lies beyond the range such as <c>9223372036854775808</c> or
    /// <c>-9223372036854775808.5</c>, and every other kind.
    /// </returns>
    public bool TryGetFloor(out long value)
    {
        if (Kind == FieldValueKind.Number && _floor is { } floor)
        {
            value = floor;
            return true;
        }

        value = 0L;
        return false;
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

    /// <summary>
    /// Returns the nearest double to the number, which <see cref="TryGetNumber"/> describes; the exact integer
    /// comes from <see cref="TryGetInteger"/>.
    /// </summary>
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
    /// stored, a number as its exact integer (<c>9007199254740993</c>) when it holds one and otherwise its
    /// double in round-trip form (<c>37.5</c>), <c>true</c> or <c>false</c>, a date as
    /// <c>yyyy-MM-dd</c>, and an index as an integer.
    /// </summary>
    /// <returns>The formatted value, or <see langword="null"/> for the null value.</returns>
    public string? ToInvariantString() => Kind switch
    {
        FieldValueKind.Text => _text,
        FieldValueKind.Number => _integer is { } integer
            ? integer.ToString(CultureInfo.InvariantCulture)
            : _number.ToString("R", CultureInfo.InvariantCulture),
        FieldValueKind.Boolean => _boolean ? "true" : "false",
        FieldValueKind.Date => FormatIsoDate(_date),
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
