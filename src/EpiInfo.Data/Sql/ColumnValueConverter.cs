using System.Data;
using System.Globalization;
using EpiInfo.Data.Metadata;
using EpiInfo.Domain.Forms;
using EpiInfo.Domain.Metadata;
using EpiInfo.Domain.Search;
using EpiInfo.Domain.Values;

namespace EpiInfo.Data.Sql;

/// <summary>
/// One parameter of a <see cref="SqlStatement"/>: a positional name, an explicit <see cref="System.Data.DbType"/>, the
/// value, and the character size of a string. It has the shape of legacy <c>QueryParameter</c> (name, type, value and
/// size) and is bound as an input parameter with exactly this type and size, never with an inferred one.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ColumnValueConverter"/> builds every instance from the column type the metadata reader discovered, using
/// the types SqlClient maps one-to-one onto the admitted SQL Server types: <c>String</c> is <c>nvarchar</c>,
/// <c>Double</c> <c>float</c>, <c>Byte</c> <c>tinyint</c>, <c>Int16</c> <c>smallint</c>, <c>Int32</c> <c>int</c>,
/// <c>Int64</c> <c>bigint</c>, <c>Boolean</c> <c>bit</c> and <c>DateTime</c> <c>datetime</c>. <c>Decimal</c>, bound
/// as <c>decimal</c> with the precision and scale of its value, is used only for a number search criterion on an
/// integer column that holds no exact 64-bit integer, and is never written to a column.
/// </para>
/// <para>
/// The value is field data, which the redaction policy keeps out of logs and error bodies, so <see cref="ToString"/>
/// leaves it out. Never pass <see cref="Value"/> to a logger or an exception message.
/// </para>
/// </remarks>
/// <param name="Name">The positional name, such as <c>@p0</c>, never derived from a field name or a value.</param>
/// <param name="DbType">The type the parameter is bound with.</param>
/// <param name="Value">The value; <see cref="DBNull.Value"/> for a database null, never <see langword="null"/>.</param>
/// <param name="Size">
/// For a string, its size in characters: n for an <c>nvarchar(n)</c> or <c>nchar(n)</c> column, <c>-1</c> for
/// <c>nvarchar(max)</c>, <c>ntext</c> and values bound without a column. <see langword="null"/> for every other type.
/// </param>
internal readonly record struct SqlParameterValue(string Name, DbType DbType, object Value, int? Size)
{
    /// <summary>The positional name, such as <c>@p0</c>.</summary>
    public string Name { get; } = string.IsNullOrWhiteSpace(Name)
        ? throw new ArgumentException("A SQL parameter must have a non-blank name.", nameof(Name))
        : Name;

    /// <summary>The value; <see cref="DBNull.Value"/> for a database null, never <see langword="null"/>.</summary>
    public object Value { get; } = Value
        ?? throw new ArgumentNullException(nameof(Value), "A SQL parameter value is DBNull.Value, never null.");

    /// <summary>The name and type only, so the value never reaches a diagnostic string.</summary>
    /// <returns>For example <c>@p0 String(255)</c> or <c>@p1 Int16</c>.</returns>
    public override string ToString() => Size is { } size
        ? string.Create(CultureInfo.InvariantCulture, $"{Name} {DbType}({size})")
        : string.Create(CultureInfo.InvariantCulture, $"{Name} {DbType}");
}

/// <summary>
/// One SQL statement on its way to SQL Server: fixed text and the parameters it binds. It is the only form in which the
/// data layer hands SQL to <c>SqlExecution</c>.
/// </summary>
/// <remarks>
/// <para>
/// The builders name parameters positionally (<c>@p0</c>, <c>@p1</c>, ...), place identifiers in
/// <see cref="Text"/> only through <c>SqlIdentifier.Quote</c> of a reader-built schema object, and never place a value
/// in it: every value travels in <see cref="Parameters"/>.
/// </para>
/// <para>The instance is immutable and safe to share across threads.</para>
/// </remarks>
/// <param name="Text">The statement text.</param>
/// <param name="Parameters">The parameters in binding order, each name used once.</param>
internal sealed record SqlStatement(string Text, IReadOnlyList<SqlParameterValue> Parameters)
{
    /// <summary>The statement text, holding identifiers and parameter names but no value.</summary>
    public string Text { get; } = string.IsNullOrWhiteSpace(Text)
        ? throw new ArgumentException("A SQL statement must have non-blank text.", nameof(Text))
        : Text;

    /// <summary>
    /// The parameters in binding order. The list is a read-only copy, so a builder that keeps adding to the list it
    /// passed cannot change the statement afterwards.
    /// </summary>
    public IReadOnlyList<SqlParameterValue> Parameters { get; } = CopyParameters(Parameters);

    // A repeated name would let the binder keep only one of the values silently, and default(SqlParameterValue)
    // bypasses the struct's checks, so both are rejected here. T-SQL resolves variable names without regard to case.
    private static IReadOnlyList<SqlParameterValue> CopyParameters(IReadOnlyList<SqlParameterValue> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters, nameof(Parameters));

        var copy = parameters.ToArray();
        var names = new HashSet<string>(copy.Length, StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in copy)
        {
            if (string.IsNullOrWhiteSpace(parameter.Name) || parameter.Value is null)
            {
                throw new ArgumentException(
                    "Every parameter of a SQL statement must have a name and a value.", nameof(Parameters));
            }

            if (!names.Add(parameter.Name))
            {
                throw new ArgumentException(
                    "A SQL statement cannot bind two parameters with the same name.", nameof(Parameters));
            }
        }

        return Array.AsReadOnly(copy);
    }
}

/// <summary>
/// The SQL-side owner of value conversion: it turns a field value into a typed parameter for the column that stores
/// it, a stored column value back into a field value, a stored system-column value or a computed integer back into
/// the value the port carries, a system-column value or a search criterion into a typed parameter, and binds the
/// scalars the builders need. The wire side is <c>FieldValueJsonConverter</c> in the API.
/// </summary>
/// <remarks>
/// <para>
/// Every parameter takes its <see cref="DbType"/> and size from the column type the metadata reader discovered, so
/// SqlClient binds the SQL Server type of the column: <c>nvarchar</c>, <c>nchar</c> and <c>ntext</c> as
/// <see cref="DbType.String"/>, <c>tinyint</c> as <see cref="DbType.Byte"/>, <c>smallint</c> as
/// <see cref="DbType.Int16"/>, <c>int</c> as <see cref="DbType.Int32"/>, <c>bigint</c> as <see cref="DbType.Int64"/>,
/// <c>float</c> as <see cref="DbType.Double"/>, <c>datetime</c> as <see cref="DbType.DateTime"/> and <c>bit</c> as
/// <see cref="DbType.Boolean"/>. These are the types legacy <c>SqlDatabase.CovertToNativeDbType</c> maps onto the same
/// native types. Any other column type is refused. Search criteria, which no column stores, are bound by
/// <see cref="ToSearchParameter"/>, which describes their types, <see cref="DbType.Decimal"/> among them.
/// </para>
/// <para>
/// A value the column cannot hold exactly is refused with an <see cref="ArgumentException"/> before it is bound, by
/// the representability rule <see cref="FieldTypeMapper.IsRepresentable"/> owns. The record validator has already
/// rejected such input with a 400, so reaching that exception is a programming error; the check exists so that
/// SqlClient never truncates a string to the parameter size or rounds a number into an integer column.
/// </para>
/// <para>
/// Text is bound and read as it stands: nothing here trims or pads it. A fixed-width <c>nchar(n)</c> column admits
/// text of up to n UTF-16 code units, for a data field and a system column alike, and binds it as given; SQL Server
/// pads shorter text with spaces to n, as it does for legacy's writes. A stored value is returned as stored, the
/// padding included, so short text written to an <c>nchar(n)</c> column reads back padded.
/// </para>
/// <para>
/// Every exception message is fixed text that names no value, field, column or SQL text, as the redaction policy
/// requires. Nothing here formats a value into SQL text. Every member is a pure function of its arguments, safe to
/// call from any thread.
/// </para>
/// </remarks>
internal static class ColumnValueConverter
{
    /// <summary>The parameter size SqlClient reads as <c>nvarchar(max)</c>.</summary>
    internal const int MaxSize = -1;

    private const string NotDataFieldMessage = "The field type holds no stored value.";
    private const string ColumnNotAdmittedMessage = "The column type does not admit values of the field type.";
    private const string UnsupportedColumnTypeMessage = "The column type is not one the data layer converts exactly.";
    private const string KindMismatchMessage = "The value kind does not match the field type.";
    private const string NotRepresentableMessage = "The value cannot be stored exactly in the column type.";
    private const string SystemValueTypeMessage = "The value's runtime type does not match the column type.";
    private const string UnexpectedStoredValueMessage =
        "A stored value does not have a type its column and field admit.";
    private const string SearchNullMessage = "An IS NULL alternative binds no parameter.";
    private const string SearchTargetMessage = "The search target and field type do not form a searchable column.";
    private const string SearchKindMismatchMessage = "The criterion value does not fit the searched column.";
    private const string NotIntegerColumnMessage = "The column type is not an integer type the data layer converts.";
    private const string NotCharacterColumnMessage = "The column type is not a character type the data layer converts.";
    private const string ComputedIntegerTypeMessage = "A computed integer is declared as Int32 or Int64 only.";
    private const string MissingSystemValueMessage = "A system column or computed integer was read with no value.";
    private const string UnexpectedSystemValueMessage =
        "A system column or computed integer was not read as its native type.";
    private const string SystemValueRangeMessage = "A system column or computed integer lies outside the int range.";

    // The largest magnitude up to which every integer is an exact double, so an integral system value bound to a
    // float column is never rounded.
    private const long MaxExactDoubleInteger = 1L << 53;

    // The SQL types search criteria are bound as. Describing them as columns lets the one representability rule decide
    // the datetime range and the int range of criterion values too.
    private static readonly ColumnMetadata DateTimeCriterionType =
        FieldTypeMapper.DescribeColumn(nameof(DateTimeCriterionType), "datetime", 8);

    private static readonly ColumnMetadata Int32CriterionType =
        FieldTypeMapper.DescribeColumn(nameof(Int32CriterionType), "int", 4);

    /// <summary>
    /// Binds a data field's value for writing to the page-table column that stores it.
    /// </summary>
    /// <param name="name">The positional parameter name, such as <c>@p3</c>.</param>
    /// <param name="column">The field's page-table column, as the metadata reader discovered it.</param>
    /// <param name="fieldType">The field's type, as <see cref="FieldTypeMapper.Map"/> decided it.</param>
    /// <param name="value">The value; <see cref="FieldValue.Null"/> writes SQL <c>NULL</c>.</param>
    /// <returns>
    /// A parameter typed by the column: the null value is <see cref="DBNull.Value"/>; text is bound unchanged as
    /// <see cref="DbType.String"/> sized to the column, <c>-1</c> for <c>nvarchar(max)</c> and <c>ntext</c>, and on an
    /// <c>nchar(n)</c> column shorter text too, which SQL Server stores padded with spaces to n; a number as the
    /// column's integer type or <see cref="DbType.Double"/>; a date as <see cref="DbType.DateTime"/> at 00:00:00 with
    /// an unspecified kind, because a date field is date-only; a checkbox as <see cref="DbType.Boolean"/>; and an
    /// option as its 0-based index in the column's integer type.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="column"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> is blank; the field type is a label, group or unsupported; the column type is not one
    /// the field type admits; the value's kind is not the field type's; or the column cannot hold the value exactly,
    /// text longer than the n code units of an <c>nvarchar(n)</c> or <c>nchar(n)</c> column included.
    /// </exception>
    internal static SqlParameterValue ToParameter(
        string name,
        ColumnMetadata column,
        FieldType fieldType,
        FieldValue value)
    {
        var binding = FieldBinding(column, fieldType);
        if (value.IsNull)
        {
            return new SqlParameterValue(name, binding.DbType, DBNull.Value, binding.Size);
        }

        if (value.Kind != FieldTypeMapper.ValueKind(fieldType))
        {
            throw new ArgumentException(KindMismatchMessage, nameof(value));
        }

        if (!FieldTypeMapper.IsRepresentable(column, value))
        {
            throw new ArgumentException(NotRepresentableMessage, nameof(value));
        }

        return new SqlParameterValue(name, binding.DbType, ToClrValue(binding.DbType, value), binding.Size);
    }

    /// <summary>
    /// Binds a value for a base-table system column (<c>GlobalRecordId</c>, <c>RecStatus</c>, <c>UniqueKey</c> and the
    /// four save-stamp columns), typed by the column the metadata reader discovered rather than by its name.
    /// </summary>
    /// <param name="name">The positional parameter name.</param>
    /// <param name="column">The reader-built system column, for example the seeded <c>RecStatus smallint</c>.</param>
    /// <param name="value">
    /// The value, or <see langword="null"/> or <see cref="DBNull.Value"/> for SQL <c>NULL</c>: a <see cref="string"/>
    /// for a character column, an integral CLR number for an integer column, a <see cref="double"/>, a
    /// <see cref="float"/> or an integer within ±2^53 for <c>float</c>, a <see cref="DateTime"/> for <c>datetime</c>,
    /// kept with its time of day, and a <see cref="bool"/> for <c>bit</c>.
    /// </param>
    /// <returns>
    /// A parameter whose type and size come from the column, with an integral value converted to exactly the column's
    /// integer type, for example <c>RecStatus</c> 1 as <c>(short)1</c> and <c>GlobalRecordId</c> as a
    /// <see cref="DbType.String"/> of size 255.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="column"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> is blank; the column type is not one the data layer converts; the value's runtime type
    /// does not suit the column; or the value is out of the column's range or longer than its size. Text shorter than
    /// an <c>nchar(n)</c> column, such as a logon name, is not refused: it is bound as given and SQL Server pads it.
    /// </exception>
    internal static SqlParameterValue ToSystemParameter(string name, ColumnSchema column, object? value)
    {
        ArgumentNullException.ThrowIfNull(column);

        var metadata = column.Metadata;
        if (BindingFor(metadata) is not { } binding)
        {
            throw new ArgumentException(UnsupportedColumnTypeMessage, nameof(column));
        }

        if (value is null or DBNull)
        {
            return new SqlParameterValue(name, binding.DbType, DBNull.Value, binding.Size);
        }

        var bound = ToSystemClrValue(binding.DbType, metadata, value);
        return new SqlParameterValue(name, binding.DbType, bound, binding.Size);
    }

    /// <summary>Binds a 32-bit integer that is not a column value, such as the search cap or a page size.</summary>
    /// <param name="name">The positional parameter name.</param>
    /// <param name="value">The integer.</param>
    /// <returns>An <see cref="DbType.Int32"/> parameter.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is blank.</exception>
    internal static SqlParameterValue Int32(string name, int value) => new(name, DbType.Int32, value, null);

    /// <summary>
    /// Binds a 64-bit integer that is not a column value, such as an <c>OFFSET</c> computed as
    /// <c>(page - 1) * pageSize</c> or <c>position - 1</c>, which must not wrap at 32 bits.
    /// </summary>
    /// <param name="name">The positional parameter name.</param>
    /// <param name="value">The integer.</param>
    /// <returns>An <see cref="DbType.Int64"/> parameter, bound as <c>bigint</c>.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is blank.</exception>
    internal static SqlParameterValue Int64(string name, long value) => new(name, DbType.Int64, value, null);

    /// <summary>
    /// Binds text that is not tied to one column, such as the escaped grid <c>q</c> pattern or a
    /// <c>globalRecordId</c> route value, at full length so nothing is truncated to a column size.
    /// </summary>
    /// <param name="name">The positional parameter name.</param>
    /// <param name="value">The text, bound unchanged.</param>
    /// <returns>A <see cref="DbType.String"/> parameter of size <c>-1</c>, bound as <c>nvarchar(max)</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is blank.</exception>
    internal static SqlParameterValue Text(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new SqlParameterValue(name, DbType.String, value, MaxSize);
    }

    /// <summary>
    /// Reads a stored page-table value into a data field's value, exactly as stored.
    /// </summary>
    /// <param name="column">The field's page-table column, as the metadata reader discovered it.</param>
    /// <param name="fieldType">The field's type, as <see cref="FieldTypeMapper.Map"/> decided it.</param>
    /// <param name="dbValue">
    /// The value the data reader returned for the column, uncoerced: a non-null value has the runtime type SqlClient
    /// returns for the column's native type, <see cref="string"/> for <c>nvarchar</c>, <c>nchar</c> and <c>ntext</c>,
    /// <see cref="byte"/> for <c>tinyint</c>, <see cref="short"/> for <c>smallint</c>, <see cref="int"/> for
    /// <c>int</c>, <see cref="long"/> for <c>bigint</c>, <see cref="double"/> for <c>float</c>,
    /// <see cref="DateTime"/> for <c>datetime</c> and <see cref="bool"/> for <c>bit</c>.
    /// </param>
    /// <returns>
    /// <see cref="FieldValue.Null"/> for <see langword="null"/> and <see cref="DBNull"/>; otherwise text unchanged,
    /// the empty string included, since reads never normalise, and the text of an <c>nchar(n)</c> column as stored,
    /// with the spaces SQL Server padded any shorter write with, never trimmed; a number, the stored double of a
    /// <c>float</c> column or the exact integer of an integer column, a <c>bigint</c> beyond 2^53 included; a date
    /// with any time of day discarded, because a date field is date-only; a boolean; or an option index.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="column"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// The field type is a label, group or unsupported, which is never read into <c>values</c>, or the column type is
    /// not one the field type admits, whatever the value, the null value included.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The stored value's runtime type is not the one SqlClient returns for the column's native type, such as a
    /// <see cref="double"/> from a <c>bigint</c> column, an integer from a <c>float</c> column or an integer of
    /// another width; or it holds a value no field value can hold, such as a negative option index or a non-finite
    /// float.
    /// </exception>
    internal static FieldValue FromDbValue(ColumnMetadata column, FieldType fieldType, object? dbValue)
    {
        var binding = FieldBinding(column, fieldType);
        if (dbValue is null or DBNull)
        {
            return FieldValue.Null;
        }

        switch (fieldType)
        {
            case FieldType.Text or FieldType.Multiline or FieldType.LegalValues:
                if (dbValue is string text)
                {
                    return FieldValue.FromText(text);
                }

                break;

            // A number is read only as the type its column returns: a bigint projected as a double may already have
            // been rounded, since beyond 2^53 not every integer is a double, and an integer for a float column comes
            // from a projection that converted the stored double, so neither is the stored value.
            case FieldType.Number:
                if (binding.DbType == DbType.Double)
                {
                    if (dbValue is double number && double.IsFinite(number))
                    {
                        return FieldValue.FromNumber(number);
                    }
                }
                else if (TryReadNativeInteger(binding.DbType, dbValue, out var whole))
                {
                    // FromInteger keeps a bigint beyond 2^53 exact, where a double would round it.
                    return FieldValue.FromInteger(whole);
                }

                break;

            case FieldType.Date:
                if (dbValue is DateTime time)
                {
                    return FieldValue.FromDate(DateOnly.FromDateTime(time));
                }

                break;

            case FieldType.Checkbox:
                if (dbValue is bool flag)
                {
                    return FieldValue.FromBoolean(flag);
                }

                break;

            // The admitted option columns are tinyint, smallint and int, each read only as its own integer type.
            case FieldType.Option:
                if (TryReadNativeInteger(binding.DbType, dbValue, out var index) && index is >= 0 and <= int.MaxValue)
                {
                    return FieldValue.FromIndex((int)index);
                }

                break;
        }

        throw new InvalidOperationException(UnexpectedStoredValueMessage);
    }

    /// <summary>
    /// Reads a stored base-table system integer, <c>UniqueKey</c> or <c>RecStatus</c>, as the <see cref="int"/> the
    /// port carries, typed by the column the metadata reader discovered rather than by its name.
    /// </summary>
    /// <param name="column">
    /// The reader-built column the value was read from, for example the seeded <c>UniqueKey int</c> or
    /// <c>RecStatus smallint</c>. Its type must be one the data layer binds as an integer: <c>tinyint</c>,
    /// <c>smallint</c>, <c>int</c> or <c>bigint</c>.
    /// </param>
    /// <param name="dbValue">
    /// The value the data reader, or an <c>OUTPUT</c> clause read as a scalar, returned for the column, uncoerced: a
    /// stored value has the runtime type SqlClient returns for the column's native type, <see cref="byte"/> for
    /// <c>tinyint</c>, <see cref="short"/> for <c>smallint</c>, <see cref="int"/> for <c>int</c> and
    /// <see cref="long"/> for <c>bigint</c>.
    /// </param>
    /// <returns>The stored integer, widened exactly and narrowed to <see cref="int"/> without wrapping.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="column"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// The column type is not an integer type the data layer converts, whatever the value.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The value is <see langword="null"/> or <see cref="DBNull"/>; its runtime type is not the one SqlClient returns
    /// for the column's native type, such as an <see cref="int"/> from a <c>smallint</c> column, a
    /// <see cref="long"/> from an <c>int</c> column, a <see cref="double"/> or a <see cref="string"/>; or it lies
    /// outside the <see cref="int"/> range.
    /// </exception>
    internal static int FromSystemInteger(ColumnSchema column, object? dbValue)
    {
        ArgumentNullException.ThrowIfNull(column);

        if (BindingFor(column.Metadata) is not
            { DbType: DbType.Byte or DbType.Int16 or DbType.Int32 or DbType.Int64 } binding)
        {
            throw new ArgumentException(NotIntegerColumnMessage, nameof(column));
        }

        return ReadNativeInt32(binding.DbType, dbValue);
    }

    /// <summary>
    /// Reads a stored base-table system text, <c>GlobalRecordId</c>, exactly as stored, typed by the column the
    /// metadata reader discovered rather than by its name.
    /// </summary>
    /// <param name="column">
    /// The reader-built column the value was read from, for example the seeded <c>GlobalRecordId nvarchar(255)</c>. Its
    /// type must be one the data layer binds as text: <c>nvarchar(n)</c>, <c>nchar(n)</c>, <c>nvarchar(max)</c> or
    /// <c>ntext</c>.
    /// </param>
    /// <param name="dbValue">
    /// The value the data reader returned for the column, uncoerced: a <see cref="string"/>.
    /// </param>
    /// <returns>
    /// The stored text unchanged, trailing spaces included, so every later statement binds the spelling the row holds.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="column"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// The column type is not a character type the data layer converts, whatever the value.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The value is <see langword="null"/> or <see cref="DBNull"/>, or it is not a <see cref="string"/>.
    /// </exception>
    internal static string FromSystemText(ColumnSchema column, object? dbValue)
    {
        ArgumentNullException.ThrowIfNull(column);

        if (BindingFor(column.Metadata) is not { DbType: DbType.String })
        {
            throw new ArgumentException(NotCharacterColumnMessage, nameof(column));
        }

        return dbValue switch
        {
            null or DBNull => throw new InvalidOperationException(MissingSystemValueMessage),
            string text => text,
            _ => throw new InvalidOperationException(UnexpectedSystemValueMessage),
        };
    }

    /// <summary>
    /// Reads an integer a statement computes rather than stores, such as a <c>COUNT_BIG</c>, a <c>ROW_NUMBER</c> or a
    /// fixed <c>CASE</c> flag, as the <see cref="int"/> the port carries.
    /// </summary>
    /// <param name="nativeType">
    /// The native type the builder declares for the computed column: <see cref="DbType.Int64"/> for <c>bigint</c>
    /// or <see cref="DbType.Int32"/> for <c>int</c>.
    /// </param>
    /// <param name="dbValue">
    /// The value the data reader or the scalar read returned, uncoerced: a <see cref="long"/> for
    /// <see cref="DbType.Int64"/> and an <see cref="int"/> for <see cref="DbType.Int32"/>.
    /// </param>
    /// <returns>The computed integer, widened exactly and narrowed to <see cref="int"/> without wrapping.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="nativeType"/> is neither <see cref="DbType.Int32"/> nor <see cref="DbType.Int64"/>, whatever the
    /// value.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The value is <see langword="null"/> or <see cref="DBNull"/>; its runtime type is not the one SqlClient returns
    /// for the declared type, such as an <see cref="int"/> for <see cref="DbType.Int64"/>; or it lies outside the
    /// <see cref="int"/> range.
    /// </exception>
    internal static int FromComputedInteger(DbType nativeType, object? dbValue)
    {
        if (nativeType is not (DbType.Int32 or DbType.Int64))
        {
            throw new ArgumentException(ComputedIntegerTypeMessage, nameof(nativeType));
        }

        return ReadNativeInt32(nativeType, dbValue);
    }

    /// <summary>
    /// Binds the value of one search alternative, typed for the column the condition reads. Alternatives that test
    /// for <c>IS NULL</c> bind no parameter; the search builder renders them itself.
    /// </summary>
    /// <param name="name">The positional parameter name.</param>
    /// <param name="target">The kind of column the condition reads.</param>
    /// <param name="fieldType">
    /// The data field's type for a page field; <see langword="null"/> for a system target.
    /// </param>
    /// <param name="column">
    /// The reader-built column the condition reads: for a page field its page-table column, which must admit the
    /// field type and whose type decides how a number is bound; for <c>GlobalRecordId</c> a character column; for
    /// <c>RecStatus</c> an integer column.
    /// </param>
    /// <param name="value">The alternative's value, of the kind the search criteria parser gives the target.</param>
    /// <returns>
    /// For <c>GlobalRecordId</c> and text, multiline and legal-values fields, Phone Number included, a
    /// <see cref="DbType.String"/> of size <c>-1</c>, so the whole criterion reaches SQL Server even against a shorter
    /// column and <c>SUBSTRING(@p, 1, 255)</c> sees all of it. For <c>RecStatus</c>, its integer as
    /// <see cref="DbType.Int32"/>. For a date, <see cref="DbType.DateTime"/> at 00:00:00; a checkbox,
    /// <see cref="DbType.Boolean"/>; an option, its index as <see cref="DbType.Int32"/>. For a number on a
    /// <c>float</c> column, its nearest double as <see cref="DbType.Double"/>. For a number on an integer column, a
    /// parameter SQL Server compares exactly, without converting the column to <c>float</c>, which would round
    /// distinct <c>bigint</c> values beyond 2^53 onto one double: the exact 64-bit integer as
    /// <see cref="DbType.Int64"/> when the number holds one; otherwise, as <see cref="DbType.Decimal"/>, its floor
    /// plus 0.5 when that floor lies within the <c>bigint</c> range (<c>9007199254740992.5</c> for
    /// <c>9007199254740992.5</c>, 2.5 for <c>2.0000000000000001</c>), and for a number beyond the range
    /// <c>-9223372036854775808.5</c> or <c>9223372036854775807.5</c> by its sign. Each lies between the same two
    /// consecutive integers as the criterion, or beyond every <c>bigint</c> on the same side, so every comparison
    /// operator gives every column value the answer the exact criterion gives.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="column"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> is blank; the value is the null value; the target and field type name no searchable
    /// column; the column is not one the target reads, or a page field's column does not admit its field type; the
    /// value's kind does not fit the column; or a <c>RecStatus</c> number is not an <c>int</c> or a date is outside
    /// the <c>datetime</c> range.
    /// </exception>
    internal static SqlParameterValue ToSearchParameter(
        string name,
        SearchTarget target,
        FieldType? fieldType,
        ColumnMetadata column,
        FieldValue value)
    {
        ArgumentNullException.ThrowIfNull(column);

        if (value.IsNull)
        {
            throw new ArgumentException(SearchNullMessage, nameof(value));
        }

        var (dbType, kind) = (target, fieldType) switch
        {
            (SearchTarget.BaseGlobalRecordId, null) when column.Family == ColumnFamily.Character =>
                (DbType.String, FieldValueKind.Text),
            (SearchTarget.BaseRecStatus, null) when column.Family == ColumnFamily.Integer =>
                (DbType.Int32, FieldValueKind.Number),
            (SearchTarget.PageField, { } type) => PageFieldSearchType(column, type),
            _ => throw new ArgumentException(SearchTargetMessage, nameof(column)),
        };

        if (value.Kind != kind)
        {
            throw new ArgumentException(SearchKindMismatchMessage, nameof(value));
        }

        // A page-field number carries its column's type: float is bound below, an integer type here.
        if (target == SearchTarget.PageField && kind == FieldValueKind.Number && dbType != DbType.Double)
        {
            var (integerColumnType, integerColumnValue) = IntegerColumnCriterion(value);
            return new SqlParameterValue(name, integerColumnType, integerColumnValue, null);
        }

        // Each arm is boxed as its own type, so the bound CLR type always matches the DbType.
        var bound = dbType switch
        {
            DbType.String => (object)value.AsText(),
            DbType.Double => (object)value.AsNumber(),
            DbType.Boolean => (object)value.AsBoolean(),
            DbType.DateTime => FieldTypeMapper.IsRepresentable(DateTimeCriterionType, value)
                ? (object)ToMidnight(value.AsDate())
                : throw new ArgumentException(NotRepresentableMessage, nameof(value)),

            // Int32: the RecStatus integer or the option index.
            _ => FieldTypeMapper.IsRepresentable(Int32CriterionType, value)
                ? (object)checked((int)WholeNumber(value))
                : throw new ArgumentException(NotRepresentableMessage, nameof(value)),
        };

        return new SqlParameterValue(name, dbType, bound, dbType == DbType.String ? MaxSize : null);
    }

    // The parameter type and value kind of a page-field criterion. FieldBinding checks that the column admits the
    // field type; a number takes the column's own type, which decides how it is bound, while text is bound at full
    // length whatever the column's size and the other types as their fixed criterion types.
    private static (DbType DbType, FieldValueKind Kind) PageFieldSearchType(ColumnMetadata column, FieldType fieldType)
    {
        var columnType = FieldBinding(column, fieldType).DbType;
        return fieldType switch
        {
            FieldType.Number => (columnType, FieldValueKind.Number),
            FieldType.Date => (DbType.DateTime, FieldValueKind.Date),
            FieldType.Checkbox => (DbType.Boolean, FieldValueKind.Boolean),
            FieldType.Option => (DbType.Int32, FieldValueKind.Index),
            _ when FieldTypeMapper.IsTextFamily(fieldType) => (DbType.String, FieldValueKind.Text),
            _ => throw new ArgumentException(SearchTargetMessage, nameof(fieldType)),
        };
    }

    // A number criterion on an integer column. Its nearest double would make SQL Server convert the column to float,
    // which beyond 2^53 rounds adjacent bigint values onto one double, and the double itself may have lost which
    // integers the criterion lies between (9007199254740992.5 has the nearest double 9007199254740992). Against
    // integers, a criterion that holds no exact integer compares only by the integer interval it lies strictly inside,
    // so every arm binds a value SQL Server compares exactly, converting the integer column to bigint or decimal:
    // - an exact 64-bit integer as that bigint;
    // - otherwise, with a floor f within the bigint range, the decimal f + 0.5, strictly inside the same interval
    //   (f, f + 1), so =, <>, <, <=, > and >= give every integer the answer the exact criterion gives;
    // - otherwise the number lies beyond the bigint range, and the decimal just beyond its end on the number's side,
    //   -2^63 - 0.5 or 2^63 - 0.5, lies on the same side of every column value.
    // Each arm is boxed as its own type, so the bound CLR type always matches the DbType.
    private static (DbType DbType, object Value) IntegerColumnCriterion(FieldValue value)
    {
        if (value.TryGetInteger(out var whole))
        {
            return (DbType.Int64, (object)whole);
        }

        if (value.TryGetFloor(out var floor))
        {
            return (DbType.Decimal, (object)(floor + 0.5m));
        }

        return (DbType.Decimal, (object)(value.AsNumber() < 0d
            ? (decimal)long.MinValue - 0.5m
            : (decimal)long.MaxValue + 0.5m));
    }

    // The binding of a data field's value: the column's type, which the mapper's column-family rule must admit for the
    // field type, so no caller can write or read a column through a pairing the mapper never admits.
    private static ColumnBinding FieldBinding(ColumnMetadata column, FieldType fieldType)
    {
        ArgumentNullException.ThrowIfNull(column);

        if (!FieldTypeMapper.IsDataFieldType(fieldType))
        {
            throw new ArgumentException(NotDataFieldMessage, nameof(fieldType));
        }

        if (BindingFor(column) is not { } binding || !FieldTypeMapper.IsAdmitted(fieldType, column))
        {
            throw new ArgumentException(ColumnNotAdmittedMessage, nameof(column));
        }

        return binding;
    }

    // The DbType and size of each admitted SQL Server type; null for every other type. A family that disagrees with
    // the type name is refused too, because the mapper's admission and representability rules would then judge a type
    // other than the one bound.
    private static ColumnBinding? BindingFor(ColumnMetadata column)
    {
        var typeName = column.SqlTypeName?.Trim().ToLowerInvariant();
        if (column.Family != FieldTypeMapper.ClassifyColumn(typeName))
        {
            return null;
        }

        return typeName switch
        {
            "nvarchar" or "nchar" => column.MaxCharacters switch
            {
                null => new ColumnBinding(DbType.String, MaxSize),
                { } characters when characters > 0 => new ColumnBinding(DbType.String, characters),
                _ => null,
            },
            "ntext" => new ColumnBinding(DbType.String, MaxSize),
            "tinyint" => new ColumnBinding(DbType.Byte, null),
            "smallint" => new ColumnBinding(DbType.Int16, null),
            "int" => new ColumnBinding(DbType.Int32, null),
            "bigint" => new ColumnBinding(DbType.Int64, null),
            "float" => new ColumnBinding(DbType.Double, null),
            "datetime" => new ColumnBinding(DbType.DateTime, null),
            "bit" => new ColumnBinding(DbType.Boolean, null),
            _ => null,
        };
    }

    // The CLR value bound for a field value the column has accepted as representable. Each arm is boxed as its own
    // type, so the bound CLR type always matches the DbType.
    private static object ToClrValue(DbType dbType, FieldValue value) => dbType switch
    {
        DbType.String => (object)value.AsText(),
        DbType.Double => (object)value.AsNumber(),
        DbType.DateTime => (object)ToMidnight(value.AsDate()),
        DbType.Boolean => (object)value.AsBoolean(),
        DbType.Byte => (object)checked((byte)WholeNumber(value)),
        DbType.Int16 => (object)checked((short)WholeNumber(value)),
        DbType.Int32 => (object)checked((int)WholeNumber(value)),
        DbType.Int64 => (object)WholeNumber(value),
        _ => throw new ArgumentException(UnsupportedColumnTypeMessage, nameof(dbType)),
    };

    // The CLR value bound for a system column, converted to exactly the column's type.
    private static object ToSystemClrValue(DbType dbType, ColumnMetadata column, object value)
    {
        switch (dbType)
        {
            case DbType.String:
                if (value is not string text)
                {
                    throw new ArgumentException(SystemValueTypeMessage, nameof(value));
                }

                return FieldTypeMapper.IsRepresentable(column, FieldValue.FromText(text))
                    ? text
                    : throw new ArgumentException(NotRepresentableMessage, nameof(value));

            case DbType.Byte or DbType.Int16 or DbType.Int32 or DbType.Int64:
                if (!IsIntegral(value))
                {
                    throw new ArgumentException(SystemValueTypeMessage, nameof(value));
                }

                if (!TryWiden(value, out var whole)
                    || !FieldTypeMapper.IsRepresentable(column, FieldValue.FromInteger(whole)))
                {
                    throw new ArgumentException(NotRepresentableMessage, nameof(value));
                }

                // Each arm is boxed as its own type; without the casts the arms would share the natural type long.
                return dbType switch
                {
                    DbType.Byte => (object)checked((byte)whole),
                    DbType.Int16 => (object)checked((short)whole),
                    DbType.Int32 => (object)checked((int)whole),
                    _ => (object)whole,
                };

            case DbType.Double:
                if (value is double or float)
                {
                    var number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                    return double.IsFinite(number)
                        ? number
                        : throw new ArgumentException(NotRepresentableMessage, nameof(value));
                }

                if (!IsIntegral(value))
                {
                    throw new ArgumentException(SystemValueTypeMessage, nameof(value));
                }

                return TryWiden(value, out var integral)
                    && integral is >= -MaxExactDoubleInteger and <= MaxExactDoubleInteger
                    ? (double)integral
                    : throw new ArgumentException(NotRepresentableMessage, nameof(value));

            case DbType.DateTime:
                if (value is not DateTime time)
                {
                    throw new ArgumentException(SystemValueTypeMessage, nameof(value));
                }

                // Save stamps keep their time of day; only date fields are stored at midnight.
                return FieldTypeMapper.IsRepresentable(column, FieldValue.FromDate(DateOnly.FromDateTime(time)))
                    ? time
                    : throw new ArgumentException(NotRepresentableMessage, nameof(value));

            case DbType.Boolean:
                return value is bool flag ? flag : throw new ArgumentException(SystemValueTypeMessage, nameof(value));

            default:
                throw new ArgumentException(UnsupportedColumnTypeMessage, nameof(dbType));
        }
    }

    // A value IsRepresentable has accepted for an integer column is an option index or a number holding an exact
    // 64-bit integer. The number's double is never used, because it can round a non-integral number into a whole one.
    private static long WholeNumber(FieldValue value)
    {
        if (value.TryGetIndex(out var index))
        {
            return index;
        }

        return value.TryGetInteger(out var whole)
            ? whole
            : throw new ArgumentException(NotRepresentableMessage, nameof(value));
    }

    // A date field is date-only: it is stored at 00:00:00, with the unspecified kind SqlClient sends as is.
    private static DateTime ToMidnight(DateOnly date) => date.ToDateTime(TimeOnly.MinValue);

    private static bool IsIntegral(object value) =>
        value is byte or sbyte or short or ushort or int or uint or long or ulong;

    // A stored integer widened to long exactly, accepted only as the type SqlClient returns for the column bound as
    // dbType: byte for tinyint, short for smallint, int for int and long for bigint. Any other runtime type, another
    // integer width or a double included, did not come from the column as stored.
    private static bool TryReadNativeInteger(DbType dbType, object value, out long whole)
    {
        long? native = (dbType, value) switch
        {
            (DbType.Byte, byte tiny) => tiny,
            (DbType.Int16, short small) => small,
            (DbType.Int32, int regular) => regular,
            (DbType.Int64, long big) => big,
            _ => null,
        };

        whole = native.GetValueOrDefault();
        return native.HasValue;
    }

    // A system or computed integer, accepted only as the type SqlClient returns for its native type, widened exactly
    // and narrowed to the int the port carries; a value beyond the int range fails rather than wrapping.
    private static int ReadNativeInt32(DbType nativeType, object? dbValue)
    {
        if (dbValue is null or DBNull)
        {
            throw new InvalidOperationException(MissingSystemValueMessage);
        }

        if (!TryReadNativeInteger(nativeType, dbValue, out var whole))
        {
            throw new InvalidOperationException(UnexpectedSystemValueMessage);
        }

        return whole is >= int.MinValue and <= int.MaxValue
            ? (int)whole
            : throw new InvalidOperationException(SystemValueRangeMessage);
    }

    // Widens an integral CLR number to long exactly; a ulong above long.MaxValue fits no admitted column.
    private static bool TryWiden(object value, out long whole)
    {
        if (value is ulong unsigned && unsigned > long.MaxValue)
        {
            whole = 0L;
            return false;
        }

        whole = Convert.ToInt64(value, CultureInfo.InvariantCulture);
        return true;
    }

    private readonly record struct ColumnBinding(DbType DbType, int? Size);
}
