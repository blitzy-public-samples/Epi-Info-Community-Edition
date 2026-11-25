namespace EpiInfo.Domain.Metadata;

/// <summary>
/// One column of a page table, as the metadata reader discovers it from <c>sys.columns</c> joined
/// to <c>sys.types</c> on each request. It carries what the field-type mapper and the record
/// validator need to admit a field for writing and to bound its values.
/// </summary>
/// <remarks>
/// The record holds catalogue facts only. <c>FieldTypeMapper.DescribeColumn</c> derives
/// <see cref="MaxCharacters"/> and <see cref="Family"/> from the catalogue values, so the
/// column-family rules have one owner.
/// </remarks>
/// <param name="Name">
/// The column name exactly as <c>sys.columns.name</c> returns it. It equals the canonical
/// <c>metaFields.Name</c> of the field that owns the column, for example <c>LastName</c>.
/// </param>
/// <param name="SqlTypeName">
/// The lower-case <c>sys.types.name</c>, for example <c>nvarchar</c>, <c>ntext</c>, <c>tinyint</c>,
/// <c>smallint</c>, <c>int</c>, <c>float</c>, <c>datetime</c> or <c>bit</c>. The family alone does
/// not tell the integer types apart, so their ranges and Option admission are read from this name.
/// </param>
/// <param name="MaxCharacters">
/// For <c>nvarchar(n)</c> and <c>nchar(n)</c>, the length n in UTF-16 code units, which is
/// <c>sys.columns.max_length / 2</c> and is how SQL Server counts these types: <c>nvarchar(4000)</c>
/// is 4000 and <c>nvarchar(20)</c> is 20. <see langword="null"/> means unbounded for
/// <c>nvarchar(max)</c> (<c>max_length = -1</c>) and <c>ntext</c>, and is always
/// <see langword="null"/> for a non-character type.
/// </param>
/// <param name="Family">
/// The family <c>FieldTypeMapper.ClassifyColumn</c> assigns to <see cref="SqlTypeName"/>.
/// </param>
public sealed record ColumnMetadata(string Name, string SqlTypeName, int? MaxCharacters, ColumnFamily Family);
