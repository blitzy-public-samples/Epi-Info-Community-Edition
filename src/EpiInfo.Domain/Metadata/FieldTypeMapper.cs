using System.Collections.ObjectModel;
using System.Text;
using EpiInfo.Domain.Forms;
using EpiInfo.Domain.Values;

namespace EpiInfo.Domain.Metadata;

/// <summary>
/// The one owner of field-type mapping and column-family compatibility: it turns a <c>metaFieldTypes</c> name and
/// the page-table column a field owns into the <see cref="FieldType"/> the API exposes, and decides which values a
/// column can store exactly. The form-definition factory, the record validator, the search parser, the record query
/// rules and the metadata reader all call it rather than repeating these rules.
/// </summary>
/// <remarks>
/// <para>
/// The discovered column wins: a field whose column the application cannot convert exactly is mapped
/// <see cref="FieldType.Unsupported"/>, rendered as a placeholder naming its metadata type, and never written.
/// </para>
/// <para>
/// Every member is a pure function of its arguments, with no state, so the class is safe to call from any thread.
/// Names are compared ordinally after invariant lower-casing, so the result never depends on the current culture.
/// </para>
/// </remarks>
public static class FieldTypeMapper
{
    // The range of the SQL Server datetime type, the only date column the application admits.
    private static readonly DateOnly MinDatetime = new(1753, 1, 1);
    private static readonly DateOnly MaxDatetime = new(9999, 12, 31);

    /// <summary>
    /// Classifies a <c>sys.types</c> name into the family of types the application converts exactly.
    /// </summary>
    /// <param name="sqlTypeName">
    /// The <c>sys.types.name</c> of a column, in any case and with surrounding whitespace allowed.
    /// </param>
    /// <returns>
    /// <see cref="ColumnFamily.Character"/> for <c>nvarchar</c>, <c>nchar</c> and <c>ntext</c>;
    /// <see cref="ColumnFamily.Integer"/> for <c>tinyint</c>, <c>smallint</c>, <c>int</c> and <c>bigint</c>;
    /// <see cref="ColumnFamily.Float"/> for <c>float</c>; <see cref="ColumnFamily.DateTime"/> for <c>datetime</c>;
    /// <see cref="ColumnFamily.Bit"/> for <c>bit</c>; and <see cref="ColumnFamily.Other"/> for every other name,
    /// a blank name and <see langword="null"/>.
    /// </returns>
    public static ColumnFamily ClassifyColumn(string? sqlTypeName) => NormalizeSqlTypeName(sqlTypeName) switch
    {
        "nvarchar" or "nchar" or "ntext" => ColumnFamily.Character,
        "tinyint" or "smallint" or "int" or "bigint" => ColumnFamily.Integer,
        "float" => ColumnFamily.Float,
        "datetime" => ColumnFamily.DateTime,
        "bit" => ColumnFamily.Bit,
        _ => ColumnFamily.Other,
    };

    /// <summary>
    /// Describes a page-table column from its catalogue values, so the metadata reader never repeats the family or
    /// length rules.
    /// </summary>
    /// <param name="name">The column name exactly as <c>sys.columns.name</c> returns it.</param>
    /// <param name="sqlTypeName">The <c>sys.types.name</c> of the column, in any case.</param>
    /// <param name="maxLengthBytes">
    /// <c>sys.columns.max_length</c>: the size in bytes, or <c>-1</c> for a <c>(max)</c> type.
    /// </param>
    /// <returns>
    /// The column with its trimmed, lower-case type name and its family. <see cref="ColumnMetadata.MaxCharacters"/>
    /// is <c>maxLengthBytes / 2</c> for <c>nvarchar(n)</c> and <c>nchar(n)</c>, the UTF-16 code-unit length SQL
    /// Server enforces (<c>8000</c> bytes is <c>nvarchar(4000)</c>), and <see langword="null"/> for
    /// <c>nvarchar(max)</c>, <c>ntext</c> and every non-character type.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="sqlTypeName"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The type is <c>nvarchar</c> or <c>nchar</c> and <paramref name="maxLengthBytes"/> is neither positive nor
    /// <c>-1</c>, a size the catalogue never reports for these types.
    /// </exception>
    public static ColumnMetadata DescribeColumn(string name, string sqlTypeName, int maxLengthBytes)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(sqlTypeName);

        var typeName = NormalizeSqlTypeName(sqlTypeName);
        int? maxCharacters = null;
        if (typeName is "nvarchar" or "nchar")
        {
            // A size of 0 or below -1 has no meaning for these types. Reading it as unbounded would let a value
            // through that SQL Server then rejects, so the impossible size fails here instead.
            if (maxLengthBytes == 0 || maxLengthBytes < -1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxLengthBytes),
                    maxLengthBytes,
                    "A Unicode character column must report a positive size in bytes, or -1 for (max).");
            }

            maxCharacters = maxLengthBytes > 0 ? maxLengthBytes / 2 : null;
        }

        return new ColumnMetadata(name, typeName, maxCharacters, ClassifyColumn(typeName));
    }

    /// <summary>
    /// Normalises a <c>metaFieldTypes.Name</c> for matching: invariant lower case with every whitespace character
    /// removed, so <c>Legal Values</c> becomes <c>legalvalues</c> and <c>Label/Title</c> becomes
    /// <c>label/title</c>.
    /// </summary>
    /// <param name="name">The stored type name, or <see langword="null"/>.</param>
    /// <returns>The normalised name; the empty string for <see langword="null"/>.</returns>
    public static string NormalizeTypeName(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(name.Length);
        foreach (var character in name)
        {
            if (!char.IsWhiteSpace(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Maps a field's metadata row to the field type the API exposes.
    /// </summary>
    /// <param name="field">The <c>metaFields</c> row with its type name and the page-table column it owns.</param>
    /// <returns>
    /// For a supported field, its type, <see langword="false"/> and <see langword="null"/>. For an unsupported one,
    /// <see cref="FieldType.Unsupported"/>, <see langword="true"/> and the stored <c>metaFieldTypes.Name</c>
    /// exactly as read, for example <c>Yes/No</c> or <c>Command Button</c>.
    /// </returns>
    /// <remarks>
    /// The rules apply in order:
    /// <list type="number">
    /// <item><description>An encrypted field is unsupported.</description></item>
    /// <item><description>
    /// The supported seeded names map to their types: <c>Label/Title</c>, <c>Group</c>, <c>Text</c>,
    /// <c>Multiline</c>, <c>Number</c>, <c>Date</c>, <c>Checkbox</c>, <c>Option</c> and <c>Legal Values</c>.
    /// </description></item>
    /// <item><description>
    /// Grid, image, mirror, GUID, command button, relate, <c>Date/Time</c>, <c>Time</c> and any GPS or geocode type
    /// are always unsupported.
    /// </description></item>
    /// <item><description>
    /// Any other name is <see cref="FieldType.Text"/> on a character column and unsupported otherwise: the seeded
    /// <c>Phone Number</c> and <c>Comment Legal</c> are text, while <c>Yes/No</c> on <c>tinyint</c> is unsupported,
    /// because text written into an integer column would fail or corrupt the data.
    /// </description></item>
    /// <item><description>
    /// A data type must own a column of a compatible family: character for text, multiline and legal values;
    /// integer or float for number; <c>datetime</c> for date; <c>bit</c> for checkbox; and <c>tinyint</c>,
    /// <c>smallint</c> or <c>int</c> for option. Labels and groups need no column.
    /// </description></item>
    /// </list>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="field"/> is null.</exception>
    public static (FieldType FieldType, bool Unsupported, string? UnsupportedType) Map(FieldMetadata field)
    {
        ArgumentNullException.ThrowIfNull(field);

        if (field.IsEncrypted)
        {
            return UnsupportedResult(field);
        }

        var typeName = NormalizeTypeName(field.FieldTypeName);
        FieldType candidate;
        if (SupportedTypeByName(typeName) is { } named)
        {
            candidate = named;
        }
        else if (IsAlwaysUnsupported(typeName))
        {
            return UnsupportedResult(field);
        }
        else if (field.Column?.Family == ColumnFamily.Character)
        {
            candidate = FieldType.Text;
        }
        else
        {
            return UnsupportedResult(field);
        }

        return IsAdmitted(candidate, field.Column) ? (candidate, false, null) : UnsupportedResult(field);
    }

    /// <summary>
    /// The kind of value a field of the given type holds in a record's <c>values</c>.
    /// </summary>
    /// <param name="type">The field type.</param>
    /// <returns>
    /// <see cref="FieldValueKind.Text"/> for text, multiline and legal values; <see cref="FieldValueKind.Number"/>
    /// for number; <see cref="FieldValueKind.Date"/> for date; <see cref="FieldValueKind.Boolean"/> for checkbox;
    /// <see cref="FieldValueKind.Index"/> for option; and <see langword="null"/> for labels, groups and unsupported
    /// fields, which hold no value.
    /// </returns>
    public static FieldValueKind? ValueKind(FieldType type) => type switch
    {
        FieldType.Text or FieldType.Multiline or FieldType.LegalValues => FieldValueKind.Text,
        FieldType.Number => FieldValueKind.Number,
        FieldType.Date => FieldValueKind.Date,
        FieldType.Checkbox => FieldValueKind.Boolean,
        FieldType.Option => FieldValueKind.Index,
        _ => null,
    };

    /// <summary>
    /// Whether the type holds free or coded text, the family to which empty-string normalisation and pattern search
    /// apply.
    /// </summary>
    /// <param name="type">The field type.</param>
    /// <returns><see langword="true"/> for text, multiline and legal values.</returns>
    public static bool IsTextFamily(FieldType type) =>
        type is FieldType.Text or FieldType.Multiline or FieldType.LegalValues;

    /// <summary>
    /// Whether a field of the type holds a value in its page-table column, that is, whether it is a data field.
    /// </summary>
    /// <param name="type">The field type.</param>
    /// <returns>
    /// <see langword="true"/> for every type except labels, groups and unsupported fields.
    /// </returns>
    public static bool IsDataFieldType(FieldType type) => type is FieldType.Text
        or FieldType.Multiline
        or FieldType.Number
        or FieldType.Date
        or FieldType.Checkbox
        or FieldType.Option
        or FieldType.LegalValues;

    /// <summary>
    /// Whether the field's metadata type is <c>Phone Number</c>, which search compares as a plain string with no
    /// pattern path, as legacy Find does.
    /// </summary>
    /// <param name="field">The field's metadata row.</param>
    /// <returns><see langword="true"/> when the normalised type name is <c>phonenumber</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="field"/> is null.</exception>
    public static bool IsPhoneNumber(FieldMetadata field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return string.Equals(NormalizeTypeName(field.FieldTypeName), "phonenumber", StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether the column can store the value exactly, so an out-of-domain input is rejected as a validation error
    /// and never reaches SQL Server as a conversion failure.
    /// </summary>
    /// <param name="column">The page-table column the value would be written to.</param>
    /// <param name="value">The value.</param>
    /// <returns>
    /// <see langword="true"/> for the null value, and otherwise when the value has the kind the column family
    /// stores and lies within the column's range:
    /// <list type="bullet">
    /// <item><description>
    /// character: text of at most <see cref="ColumnMetadata.MaxCharacters"/> UTF-16 code units, the way SQL Server
    /// counts <c>nvarchar(n)</c>, or of any length when that is <see langword="null"/>;
    /// </description></item>
    /// <item><description>
    /// integer: an exact whole number or an option index within the range of <c>tinyint</c> (0 to 255),
    /// <c>smallint</c>, <c>int</c> or <c>bigint</c>;
    /// </description></item>
    /// <item><description>float: any number;</description></item>
    /// <item><description><c>datetime</c>: a date from 1753-01-01 to 9999-12-31;</description></item>
    /// <item><description><c>bit</c>: a boolean.</description></item>
    /// </list>
    /// A column of any other family stores nothing.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="column"/> is null.</exception>
    public static bool IsRepresentable(ColumnMetadata column, FieldValue value)
    {
        ArgumentNullException.ThrowIfNull(column);

        if (value.IsNull)
        {
            return true;
        }

        return column.Family switch
        {
            ColumnFamily.Character => value.TryGetText(out var text)
                && (column.MaxCharacters is not { } maxCharacters || text.Length <= maxCharacters),
            ColumnFamily.Integer => TryGetWholeNumber(value, out var whole)
                && IntegerRange(column.SqlTypeName) is { } range
                && whole >= range.Min
                && whole <= range.Max,
            ColumnFamily.Float => value.Kind == FieldValueKind.Number,
            ColumnFamily.DateTime => value.TryGetDate(out var date) && date >= MinDatetime && date <= MaxDatetime,
            ColumnFamily.Bit => value.Kind == FieldValueKind.Boolean,
            _ => false,
        };
    }

    /// <summary>
    /// Parses the items of an Option field from <c>metaFields.List</c>, as legacy <c>OptionField</c> reads them:
    /// the text before the first <c>||</c> is split on commas, keeping every item as stored, untrimmed and empty
    /// items included.
    /// </summary>
    /// <param name="list">The stored <c>metaFields.List</c>, or <see langword="null"/>.</param>
    /// <returns>
    /// One option per item in list order, whose value is the item's 0-based index, the value legacy stores in the
    /// field's integer column, and whose label is the item text. <c>Yes,No,Unknown||12,34</c> gives
    /// <c>Yes</c>, <c>No</c> and <c>Unknown</c> at indexes 0, 1 and 2. The list is empty when there is no item
    /// text.
    /// </returns>
    public static IReadOnlyList<FieldOption> ParseOptionItems(string? list)
    {
        var itemText = list ?? string.Empty;
        var separator = itemText.IndexOf("||", StringComparison.Ordinal);
        if (separator >= 0)
        {
            itemText = itemText[..separator];
        }

        // Legacy splits even empty text and shows one blank radio button. With no item text there is nothing to
        // choose, so no option is offered, whether the list is missing, empty or starts with "||".
        if (itemText.Length == 0)
        {
            return ReadOnlyCollection<FieldOption>.Empty;
        }

        var items = itemText.Split(',');
        var options = new FieldOption[items.Length];
        for (var index = 0; index < items.Length; index++)
        {
            options[index] = new FieldOption(FieldValue.FromIndex(index), items[index]);
        }

        return Array.AsReadOnly(options);
    }

    // The placeholder names the type as stored, never its normalised form.
    private static (FieldType FieldType, bool Unsupported, string? UnsupportedType) UnsupportedResult(
        FieldMetadata field) => (FieldType.Unsupported, true, field.FieldTypeName);

    private static FieldType? SupportedTypeByName(string normalizedTypeName) => normalizedTypeName switch
    {
        "label/title" => FieldType.Label,
        "group" => FieldType.Group,
        "text" => FieldType.Text,
        "multiline" => FieldType.Multiline,
        "number" => FieldType.Number,
        "date" => FieldType.Date,
        "checkbox" => FieldType.Checkbox,
        "option" => FieldType.Option,
        "legalvalues" => FieldType.LegalValues,
        _ => null,
    };

    private static bool IsAlwaysUnsupported(string normalizedTypeName) =>
        normalizedTypeName is "grid"
            or "image"
            or "mirror"
            or "guid"
            or "commandbutton"
            or "relate"
            or "date/time"
            or "time"
        || normalizedTypeName.Contains("gps", StringComparison.Ordinal)
        || normalizedTypeName.Contains("geocode", StringComparison.Ordinal);

    // Column-family compatibility: a data type is admitted only on a column it converts exactly.
    private static bool IsAdmitted(FieldType type, ColumnMetadata? column) => type switch
    {
        FieldType.Label or FieldType.Group => true,
        FieldType.Text or FieldType.Multiline or FieldType.LegalValues => column?.Family == ColumnFamily.Character,
        FieldType.Number => column?.Family is ColumnFamily.Integer or ColumnFamily.Float,
        FieldType.Date => column?.Family == ColumnFamily.DateTime,
        FieldType.Checkbox => column?.Family == ColumnFamily.Bit,

        // Legacy stores the 0-based index in a smallint column. The admitted option columns stop at int, so a
        // bigint column leaves the field unsupported.
        FieldType.Option => column is { Family: ColumnFamily.Integer }
            && NormalizeSqlTypeName(column.SqlTypeName) is "tinyint" or "smallint" or "int",
        _ => false,
    };

    // Integer columns decide on the exact integer, never on the nearest double, as FieldValue requires: a number
    // such as 2.0000000000000001, or an integer above 2^53 whose double is rounded, must not pass as whole or
    // land on a neighbouring value. A number holding no exact signed 64-bit integer fits no integer column.
    private static bool TryGetWholeNumber(FieldValue value, out long whole)
    {
        if (value.TryGetIndex(out var index))
        {
            whole = index;
            return true;
        }

        return value.TryGetInteger(out whole);
    }

    private static (long Min, long Max)? IntegerRange(string sqlTypeName) => NormalizeSqlTypeName(sqlTypeName) switch
    {
        "tinyint" => (byte.MinValue, byte.MaxValue),
        "smallint" => (short.MinValue, short.MaxValue),
        "int" => (int.MinValue, int.MaxValue),
        "bigint" => (long.MinValue, long.MaxValue),
        _ => null,
    };

    private static string NormalizeSqlTypeName(string? sqlTypeName) =>
        sqlTypeName?.Trim().ToLowerInvariant() ?? string.Empty;
}
