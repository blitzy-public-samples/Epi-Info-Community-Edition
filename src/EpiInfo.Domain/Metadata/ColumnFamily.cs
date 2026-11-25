namespace EpiInfo.Domain.Metadata;

/// <summary>
/// The family of a page-table column's SQL Server type, limited to the types the application
/// converts exactly. A field is admitted for writing only when its field type is compatible with
/// the family of the column it owns; every other column is <see cref="Other"/>, so the discovered
/// column decides and no write can reach a type the application cannot convert.
/// </summary>
/// <remarks>
/// <c>FieldTypeMapper.ClassifyColumn</c> is the only place a <c>sys.types</c> name becomes a family;
/// every other component reads the family from <c>ColumnMetadata</c>.
/// </remarks>
public enum ColumnFamily
{
    /// <summary>
    /// Unicode character types: <c>nvarchar(n)</c>, <c>nchar(n)</c>, <c>nvarchar(max)</c> and
    /// <c>ntext</c> (<c>sys.types</c> names <c>nvarchar</c>, <c>nchar</c>, <c>ntext</c>).
    /// Non-Unicode <c>varchar</c>, <c>char</c> and <c>text</c> are not in this family.
    /// </summary>
    Character,

    /// <summary>
    /// Exact integer types: <c>tinyint</c>, <c>smallint</c>, <c>int</c> and <c>bigint</c>.
    /// The range of each comes from <c>ColumnMetadata.SqlTypeName</c>.
    /// </summary>
    Integer,

    /// <summary>
    /// <c>float</c>, an IEEE double. <c>real</c> is single precision and is not in this family.
    /// </summary>
    Float,

    /// <summary>
    /// <c>datetime</c> only, covering 1753-01-01 to 9999-12-31. <c>date</c>, <c>datetime2</c> and
    /// <c>smalldatetime</c> are not in this family.
    /// </summary>
    DateTime,

    /// <summary>
    /// <c>bit</c>.
    /// </summary>
    Bit,

    /// <summary>
    /// Every other type, for example <c>varchar</c>, <c>char</c>, <c>text</c>, <c>decimal</c>,
    /// <c>numeric</c>, <c>money</c>, <c>real</c>, <c>date</c>, <c>datetime2</c>,
    /// <c>smalldatetime</c> and <c>uniqueidentifier</c>. A field whose column is in this family is
    /// mapped <c>unsupported</c> and never written.
    /// </summary>
    Other,
}
