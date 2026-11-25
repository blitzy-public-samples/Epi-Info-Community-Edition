using EpiInfo.Domain.Metadata;

namespace EpiInfo.Data.Metadata;

/// <summary>
/// One column of a table as the SQL Server catalogue reports it: a <c>sys.columns</c> row joined to
/// <c>sys.types</c>. Its <see cref="Name"/> is the only form in which a column identifier can reach record SQL.
/// </summary>
/// <remarks>
/// <para>
/// Only <c>SqlMetadataReader</c> constructs this type, from the rows of its catalogue query, so every column name
/// the application can quote is one the database returned. The constructor is internal, the type has no setter and
/// no public factory, and <c>SqlIdentifier.Quote(ColumnSchema)</c> is the only way to turn it into SQL text.
/// </para>
/// <para>The instance is immutable after construction and safe to share across threads.</para>
/// </remarks>
public sealed class ColumnSchema
{
    // Fixed messages: the redaction policy keeps identifiers out of exception text.
    private const string MissingNameMessage = "A catalogue column must have a non-blank name.";
    private const string MissingTypeMessage = "A catalogue column must have a non-blank type name.";

    /// <summary>
    /// Builds a column from one catalogue row. Called only by <c>SqlMetadataReader</c>.
    /// </summary>
    /// <param name="name">The column name exactly as <c>sys.columns.name</c> returns it.</param>
    /// <param name="sqlTypeName">The <c>sys.types.name</c> joined on <c>user_type_id</c>.</param>
    /// <param name="maxLengthBytes">The raw <c>sys.columns.max_length</c>.</param>
    /// <param name="isNullable"><c>sys.columns.is_nullable</c>.</param>
    /// <param name="isIdentity"><c>sys.columns.is_identity</c>.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> or <paramref name="sqlTypeName"/> is null, empty or whitespace.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The type is <c>nvarchar</c> or <c>nchar</c> and <paramref name="maxLengthBytes"/> is a size the catalogue
    /// never reports for it, as <see cref="FieldTypeMapper.DescribeColumn"/> decides.
    /// </exception>
    internal ColumnSchema(string name, string sqlTypeName, int maxLengthBytes, bool isNullable, bool isIdentity)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException(MissingNameMessage, nameof(name));
        }

        if (string.IsNullOrWhiteSpace(sqlTypeName))
        {
            throw new ArgumentException(MissingTypeMessage, nameof(sqlTypeName));
        }

        Name = name;
        SqlTypeName = sqlTypeName;
        MaxLengthBytes = maxLengthBytes;
        IsNullable = isNullable;
        IsIdentity = isIdentity;

        // FieldTypeMapper is the one owner of column-family and length rules; this type only carries its answer.
        Metadata = FieldTypeMapper.DescribeColumn(name, sqlTypeName, maxLengthBytes);
    }

    /// <summary>
    /// The column name with the exact spelling the catalogue returned, for example <c>LastName</c> or
    /// <c>loosestoolsperday</c>.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// The <c>sys.types.name</c> of the column, joined on <c>user_type_id</c>, as the catalogue returned it. A column
    /// of an alias type reports the alias name, which <see cref="FieldTypeMapper"/> classifies as
    /// <see cref="ColumnFamily.Other"/>.
    /// </summary>
    public string SqlTypeName { get; }

    /// <summary>
    /// The raw <c>sys.columns.max_length</c>: the size in bytes (<c>8000</c> for <c>nvarchar(4000)</c>), <c>-1</c>
    /// for a <c>(max)</c> type and <c>16</c> for <c>ntext</c>.
    /// </summary>
    public int MaxLengthBytes { get; }

    /// <summary>Whether the column accepts <c>NULL</c>.</summary>
    public bool IsNullable { get; }

    /// <summary>Whether the column is an identity column, such as the seeded base-table <c>UniqueKey</c>.</summary>
    public bool IsIdentity { get; }

    /// <summary>
    /// The column as the Domain sees it, from <see cref="FieldTypeMapper.DescribeColumn"/>: its normalised type
    /// name, its length in UTF-16 code units for <c>nvarchar(n)</c> and <c>nchar(n)</c>, and its
    /// <see cref="ColumnFamily"/>.
    /// </summary>
    public ColumnMetadata Metadata { get; }

    /// <summary>Returns <see cref="Name"/>, for test diagnostics only.</summary>
    /// <returns>The catalogue column name.</returns>
    /// <remarks>Never used to build SQL; identifiers reach SQL text only through <c>SqlIdentifier.Quote</c>.</remarks>
    public override string ToString() => Name;
}

/// <summary>
/// One table as the SQL Server catalogue reports it: its name and its columns in <c>column_id</c> order. A form's
/// base table, a page table, a code table, or a session temporary table for the reserved-word test fixture.
/// </summary>
/// <remarks>
/// <para>
/// Only <c>SqlMetadataReader</c> constructs this type, so every table name the application can quote is one the
/// database returned. <c>SqlIdentifier.Quote(TableSchema)</c> is the only way to turn it into SQL text.
/// </para>
/// <para>The instance is immutable after construction and safe to share across threads.</para>
/// </remarks>
public sealed class TableSchema
{
    // Fixed messages: the redaction policy keeps identifiers out of exception text.
    private const string MissingNameMessage = "A catalogue table must have a non-blank name.";
    private const string NullColumnMessage = "A catalogue table cannot hold a null column.";

    private readonly Dictionary<string, ColumnSchema> _columnsByExactName;
    private readonly Dictionary<string, ColumnSchema> _columnsByName;

    /// <summary>
    /// Builds a table from its catalogue rows. Called only by <c>SqlMetadataReader</c>.
    /// </summary>
    /// <param name="name">
    /// The table name exactly as the catalogue returned it, or the session name (such as <c>#Reserved</c>) of a
    /// temporary table, whose <c>tempdb</c> catalogue name is mangled.
    /// </param>
    /// <param name="columns">The table's columns in <c>column_id</c> order; the order is kept.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> is null, empty or whitespace, or <paramref name="columns"/> holds a null entry.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="columns"/> is null.</exception>
    internal TableSchema(string name, IEnumerable<ColumnSchema> columns)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException(MissingNameMessage, nameof(name));
        }

        ArgumentNullException.ThrowIfNull(columns);

        var ordered = columns.ToArray();
        _columnsByExactName = new Dictionary<string, ColumnSchema>(ordered.Length, StringComparer.Ordinal);
        _columnsByName = new Dictionary<string, ColumnSchema>(ordered.Length, StringComparer.OrdinalIgnoreCase);
        foreach (var column in ordered)
        {
            if (column is null)
            {
                throw new ArgumentException(NullColumnMessage, nameof(columns));
            }

            // Names that differ only by case can exist only in a case-sensitive database; the first in
            // column_id order answers a case-insensitive lookup, and each still answers its exact spelling.
            _columnsByExactName.TryAdd(column.Name, column);
            _columnsByName.TryAdd(column.Name, column);
        }

        Name = name;
        Columns = Array.AsReadOnly(ordered);
    }

    /// <summary>
    /// The table name with the exact spelling the catalogue returned, for example <c>CaseReport</c>,
    /// <c>FoodHistory2</c> or <c>codestate</c>.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// The table's columns in <c>column_id</c> order. The list is a read-only wrapper, so it cannot be cast back to a
    /// mutable collection.
    /// </summary>
    public IReadOnlyList<ColumnSchema> Columns { get; }

    /// <summary>
    /// Finds a column by name, mapping a <c>metaFields.Name</c> or a <c>values</c> key onto the catalogue column.
    /// </summary>
    /// <param name="name">The name to look up, in any case.</param>
    /// <returns>
    /// The column whose name equals <paramref name="name"/> ordinally; otherwise the first column, in
    /// <c>column_id</c> order, whose name equals it ignoring case; otherwise <see langword="null"/>. The column
    /// returned always carries the catalogue spelling.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    public ColumnSchema? FindColumn(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (_columnsByExactName.TryGetValue(name, out var exact))
        {
            return exact;
        }

        return _columnsByName.TryGetValue(name, out var column) ? column : null;
    }

    /// <summary>Returns <see cref="Name"/>, for test diagnostics only.</summary>
    /// <returns>The catalogue table name.</returns>
    /// <remarks>Never used to build SQL; identifiers reach SQL text only through <c>SqlIdentifier.Quote</c>.</remarks>
    public override string ToString() => Name;
}

/// <summary>
/// One page of a form with the catalogue table that stores its values.
/// </summary>
/// <remarks>
/// <para>
/// Only <c>SqlMetadataReader</c> constructs this type. The page table is named, as legacy Epi Info names it, by the
/// form's base table name followed by the page id (seeded <c>CaseReport1</c>, <c>FoodHistory2</c>,
/// <c>FoodHistory3</c>); <see cref="Table"/> is the catalogue's answer for that name.
/// </para>
/// <para>The instance is immutable after construction and safe to share across threads.</para>
/// </remarks>
public sealed class PageTableSchema
{
    /// <summary>
    /// Pairs a page with its catalogue table. Called only by <c>SqlMetadataReader</c>.
    /// </summary>
    /// <param name="page">The page's <c>metaPages</c> row.</param>
    /// <param name="table">The page table from the catalogue, or <see langword="null"/> when it does not exist.</param>
    /// <exception cref="ArgumentNullException"><paramref name="page"/> is null.</exception>
    internal PageTableSchema(PageMetadata page, TableSchema? table)
    {
        ArgumentNullException.ThrowIfNull(page);

        Page = page;
        Table = table;
    }

    /// <summary>The page's <c>metaPages</c> row: <c>PageId</c>, <c>Name</c>, <c>Position</c> and table name.</summary>
    public PageMetadata Page { get; }

    /// <summary>
    /// The page table as the catalogue reports it, or <see langword="null"/> when the database holds no table of
    /// the page's name. A page without a table stores no values and is never named in record SQL.
    /// </summary>
    public TableSchema? Table { get; }
}

/// <summary>
/// The catalogue view of one form: its base table, its pages in <c>metaPages.Position</c> order with their tables,
/// and the base-table system columns record SQL addresses. It is the only source of the identifiers in the SQL that
/// reads and writes the form's records.
/// </summary>
/// <remarks>
/// <para>
/// Only <c>SqlMetadataReader</c> constructs this type, and it reads the catalogue again for each record operation,
/// so a form's schema is never cached.
/// </para>
/// <para>The instance is immutable after construction and safe to share across threads.</para>
/// </remarks>
public sealed class FormSchema
{
    // Catalogue lookup keys for the base-table system columns, matched through TableSchema.FindColumn, which
    // returns the catalogue spelling. They are never placed in SQL text. FKEY is deliberately absent: the record
    // path never writes it for a form without a foreign-key field, so it stays NULL as legacy leaves it.
    private const string UniqueKeyColumn = "UniqueKey";
    private const string RecStatusColumn = "RecStatus";
    private const string GlobalRecordIdColumn = "GlobalRecordId";
    private const string FirstSaveLogonNameColumn = "FirstSaveLogonName";
    private const string FirstSaveTimeColumn = "FirstSaveTime";
    private const string LastSaveLogonNameColumn = "LastSaveLogonName";
    private const string LastSaveTimeColumn = "LastSaveTime";

    // Fixed message: the redaction policy keeps identifiers out of exception text.
    private const string NullPageMessage = "A form schema cannot hold a null page.";

    private readonly Dictionary<int, PageTableSchema> _pagesById;

    /// <summary>
    /// Builds the catalogue view of a form. Called only by <c>SqlMetadataReader</c>.
    /// </summary>
    /// <param name="viewId">The form's <c>metaViews.ViewId</c>.</param>
    /// <param name="baseTable">The form's base table from the catalogue.</param>
    /// <param name="pages">
    /// The form's pages ordered by <c>metaPages.Position</c>, then <c>PageId</c>; the order is kept.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="baseTable"/> or <paramref name="pages"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="pages"/> holds a null entry.</exception>
    internal FormSchema(int viewId, TableSchema baseTable, IEnumerable<PageTableSchema> pages)
    {
        ArgumentNullException.ThrowIfNull(baseTable);
        ArgumentNullException.ThrowIfNull(pages);

        var ordered = pages.ToArray();
        _pagesById = new Dictionary<int, PageTableSchema>(ordered.Length);
        foreach (var page in ordered)
        {
            if (page is null)
            {
                throw new ArgumentException(NullPageMessage, nameof(pages));
            }

            // metaPages keys on PageId, so a repeat cannot come from the catalogue; the first entry answers.
            _pagesById.TryAdd(page.Page.PageId, page);
        }

        ViewId = viewId;
        BaseTable = baseTable;
        Pages = Array.AsReadOnly(ordered);

        UniqueKey = baseTable.FindColumn(UniqueKeyColumn);
        RecStatus = baseTable.FindColumn(RecStatusColumn);
        GlobalRecordId = baseTable.FindColumn(GlobalRecordIdColumn);
        FirstSaveLogonName = baseTable.FindColumn(FirstSaveLogonNameColumn);
        FirstSaveTime = baseTable.FindColumn(FirstSaveTimeColumn);
        LastSaveLogonName = baseTable.FindColumn(LastSaveLogonNameColumn);
        LastSaveTime = baseTable.FindColumn(LastSaveTimeColumn);
    }

    /// <summary>The form's <c>metaViews.ViewId</c>.</summary>
    public int ViewId { get; }

    /// <summary>The form's base table, named by <c>metaFields.DataTableName</c> and confirmed by the catalogue.</summary>
    public TableSchema BaseTable { get; }

    /// <summary>
    /// The form's pages in <c>metaPages.Position</c> order, then <c>PageId</c>, each with its table or
    /// <see langword="null"/> when the table is missing. The list is a read-only wrapper.
    /// </summary>
    public IReadOnlyList<PageTableSchema> Pages { get; }

    /// <summary>The base-table <c>UniqueKey</c> identity column, or <see langword="null"/> when absent.</summary>
    public ColumnSchema? UniqueKey { get; }

    /// <summary>The base-table <c>RecStatus</c> column, or <see langword="null"/> when absent.</summary>
    public ColumnSchema? RecStatus { get; }

    /// <summary>The base-table <c>GlobalRecordId</c> column, or <see langword="null"/> when absent.</summary>
    public ColumnSchema? GlobalRecordId { get; }

    /// <summary>
    /// The base-table <c>FirstSaveLogonName</c> stamp column, or <see langword="null"/> when absent. Stamp columns
    /// are written only when they exist, as legacy Epi Info checks.
    /// </summary>
    public ColumnSchema? FirstSaveLogonName { get; }

    /// <summary>The base-table <c>FirstSaveTime</c> stamp column, or <see langword="null"/> when absent.</summary>
    public ColumnSchema? FirstSaveTime { get; }

    /// <summary>The base-table <c>LastSaveLogonName</c> stamp column, or <see langword="null"/> when absent.</summary>
    public ColumnSchema? LastSaveLogonName { get; }

    /// <summary>The base-table <c>LastSaveTime</c> stamp column, or <see langword="null"/> when absent.</summary>
    public ColumnSchema? LastSaveTime { get; }

    /// <summary>Finds a page of the form by its id.</summary>
    /// <param name="pageId">The <c>metaPages.PageId</c> to look up.</param>
    /// <returns>The page with that id, or <see langword="null"/> when the form has none.</returns>
    public PageTableSchema? FindPage(int pageId) => _pagesById.TryGetValue(pageId, out var page) ? page : null;

    /// <summary>
    /// Finds the catalogue column that stores a data field's value: the column of the field's page table whose name
    /// matches the field's column, through <see cref="TableSchema.FindColumn"/>.
    /// </summary>
    /// <param name="field">A data field of this form.</param>
    /// <returns>
    /// The catalogue-cased column, or <see langword="null"/> when the field's page is not a page of this form, the
    /// page has no table, or the table has no such column.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="field"/> is null.</exception>
    public ColumnSchema? FindDataColumn(DataFieldMetadata field)
    {
        ArgumentNullException.ThrowIfNull(field);

        return FindPage(field.Page.PageId)?.Table?.FindColumn(field.Column.Name);
    }
}
