using System.Globalization;
using System.Text;
using EpiInfo.Data.Metadata;
using EpiInfo.Data.Sql;
using EpiInfo.Domain.Metadata;
using EpiInfo.Domain.Records;
using EpiInfo.Domain.Storage;
using EpiInfo.Domain.Values;

namespace EpiInfo.Data.Records;

/// <summary>
/// Allocates the positional parameter names of one statement, <c>@p0</c>, <c>@p1</c>, ..., and collects the
/// parameters in binding order.
/// </summary>
/// <remarks>
/// Names are positional so that no field name or value can ever shape the statement text. One instance serves one
/// statement and is not shared across threads.
/// </remarks>
internal sealed class PositionalParameters
{
    private const string MismatchedNameMessage = "A parameter factory must bind the name it is given.";

    private readonly List<SqlParameterValue> _parameters = [];

    /// <summary>The number of parameters allocated so far.</summary>
    public int Count => _parameters.Count;

    /// <summary>
    /// Allocates the next positional name, lets <paramref name="factory"/> bind the value under it, and keeps the
    /// parameter.
    /// </summary>
    /// <param name="factory">
    /// Builds the parameter for the allocated name, normally a <see cref="ColumnValueConverter"/> method.
    /// </param>
    /// <returns>The allocated name, such as <c>@p3</c>, to place in the statement text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The factory bound a different name.</exception>
    public string Add(Func<string, SqlParameterValue> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        var name = "@p" + _parameters.Count.ToString(CultureInfo.InvariantCulture);
        var parameter = factory(name);
        if (!string.Equals(parameter.Name, name, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(MismatchedNameMessage);
        }

        _parameters.Add(parameter);
        return name;
    }

    /// <summary>Pairs <paramref name="text"/> with the parameters allocated so far.</summary>
    /// <param name="text">The statement text, holding the allocated names and no value.</param>
    /// <returns>The statement; it holds a copy of the parameters, so later additions do not reach it.</returns>
    /// <exception cref="ArgumentException"><paramref name="text"/> is blank.</exception>
    public SqlStatement ToStatement(string text) => new(text, _parameters);
}

/// <summary>One data-field column in a <c>SELECT</c> list, read back by its ordinal.</summary>
/// <param name="Field">The data field whose value the column holds.</param>
/// <param name="Column">The reader-built page-table column, the only form in which its name reaches SQL text.</param>
/// <param name="Ordinal">The column's 0-based ordinal in the result, aliased <c>[c{Ordinal}]</c>.</param>
internal sealed record ProjectedColumn(DataFieldMetadata Field, ColumnSchema Column, int Ordinal);

/// <summary>
/// The ordinal of a joined page's <c>GlobalRecordId</c> in a record <c>SELECT</c>: a non-<c>NULL</c> value means the
/// record has a row in that page table.
/// </summary>
/// <param name="Page">The joined page.</param>
/// <param name="Ordinal">The 0-based ordinal of <c>p{i}.[GlobalRecordId]</c> in the result.</param>
internal sealed record PagePresence(PageTableSchema Page, int Ordinal);

/// <summary>
/// A record <c>SELECT</c> with the layout of its result: ordinals <see cref="RecordSqlBuilder.RecordGlobalRecordIdOrdinal"/>,
/// <see cref="RecordSqlBuilder.RecordUniqueKeyOrdinal"/> and <see cref="RecordSqlBuilder.RecordRecStatusOrdinal"/>
/// (0, 1, 2) are the base row's <c>GlobalRecordId</c>, <c>UniqueKey</c> and <c>RecStatus</c>; when
/// <see cref="HasIntrinsicCoordinates"/> is set, ordinals <see cref="RecordSqlBuilder.RecordIntrinsicPositionOrdinal"/>
/// and <see cref="RecordSqlBuilder.RecordIntrinsicCountOrdinal"/> (3, 4) are the record's intrinsic position and
/// count, both <c>bigint</c>; then one <see cref="PagePresence"/> per joined page; then one
/// <see cref="ProjectedColumn"/> per data field. Each presence and column records its own ordinal, which starts at 5
/// with the coordinates and at 3 without them.
/// </summary>
/// <param name="Statement">The statement to run.</param>
/// <param name="Columns">The data-field columns in the order of the data fields passed to the builder.</param>
/// <param name="Pages">The joined pages, in <c>FormSchema.Pages</c> order.</param>
/// <param name="HasIntrinsicCoordinates">
/// Whether ordinals 3 and 4 hold the record's intrinsic position and count, computed by the same statement that reads
/// the row: set for <see cref="RecordSqlBuilder.RecordAt"/> and <see cref="RecordSqlBuilder.RecordById"/>, clear for
/// <see cref="RecordSqlBuilder.RecordByUniqueKey"/>.
/// </param>
internal sealed record RecordSelect(
    SqlStatement Statement,
    IReadOnlyList<ProjectedColumn> Columns,
    IReadOnlyList<PagePresence> Pages,
    bool HasIntrinsicCoordinates);

/// <summary>
/// The keyword that joins the selected-set predicate to a statement, chosen by
/// <see cref="RecordSqlBuilder.AppendSelectedSetPredicate"/>. The keyword text is fixed inside the builder, so a
/// caller selects a clause and never supplies SQL text.
/// </summary>
internal enum SelectedSetClause
{
    /// <summary>The predicate opens the statement's <c>WHERE</c> clause: <c> WHERE b.[RecStatus] = @pN</c>.</summary>
    Where,

    /// <summary>
    /// The predicate extends a <c>WHERE</c> clause the caller has already opened: <c> AND b.[RecStatus] = @pN</c>.
    /// </summary>
    And,
}

/// <summary>
/// Builds every statement that reads or writes a form's base table and page tables, except search and the grid list,
/// which <c>SearchSqlBuilder</c> renders with the helpers defined here. It is a pure text-and-parameter builder: no
/// connection, no I/O, no logging and no cache.
/// </summary>
/// <remarks>
/// <para>
/// <b>Identifiers.</b> Every table and column name comes from a reader-built <see cref="FormSchema"/>,
/// <see cref="PageTableSchema"/> or <see cref="ColumnSchema"/> and reaches the text only through
/// <see cref="SqlIdentifier.Quote(TableSchema)"/> or <see cref="SqlIdentifier.Quote(ColumnSchema)"/>. The aliases
/// (<c>b</c>, <c>i</c>, <c>p{i}</c>, <c>[c{n}]</c>, <c>[ip]</c>, <c>[ic]</c>) and SQL keywords are fixed literals.
/// Result columns are aliased by ordinal, so two tables with a column of the same name never collide.
/// </para>
/// <para>
/// <b>Values.</b> Every value is a positional parameter built by <see cref="ColumnValueConverter"/>, typed by the
/// column the reader discovered; a route-supplied <c>globalRecordId</c> that the base table has not yet confirmed is
/// bound as <see cref="ColumnValueConverter.Text"/> at full length.
/// </para>
/// <para>
/// <b>Record identity.</b> SQL Server compares strings as if the shorter were padded with trailing spaces. Every
/// predicate this builder renders that matches a base row against a <c>GlobalRecordId</c> parameter, in
/// <see cref="RecordById"/>, <see cref="SetPositionLookup"/>, <see cref="LockBase"/> and the base-row key of
/// <see cref="RecordByUniqueKey"/>, <see cref="UpdateBaseStamp"/> and <see cref="SoftDelete"/>, compares under the
/// column's collation and, because of that padding, also requires equal byte lengths (<c>DATALENGTH</c>): an id
/// longer than the column, or one that differs from a stored id only by trailing spaces, finds no row instead of
/// failing to bind or matching that stored id, while letter case still follows the collation. The page statements
/// (<see cref="UpdatePageColumns"/>, <see cref="InsertPageKey"/>) bind the stored id the locked read returned, and the
/// page join of a record read compares two stored columns. Neither meets a request's spelling, so the page
/// <c>UPDATE</c> and the join keep plain equality. The by-id reads return the id as the base row stores it, and every
/// later statement binds that stored spelling, never the request's.
/// </para>
/// <para>
/// <b>Aliases.</b> Reads alias the base table <c>b</c> and each page <c>p{i}</c>, where <c>i</c> is the page's index in
/// <see cref="FormSchema.Pages"/>, so a column has the same reference in every statement. <see cref="RecordAt"/> and
/// <see cref="RecordById"/> read the base table as <c>i</c> inside a derived table aliased <c>b</c>, which keeps the
/// base system columns under their catalogue names and adds <c>[ip]</c> and <c>[ic]</c>, so their outer query
/// references the base row exactly as the other reads do. Statements that write use no alias.
/// </para>
/// <para>
/// <b>Locking.</b> The locked read (<see cref="LockBase"/>) takes an update lock on the base row it matches and holds
/// it to the end of the transaction, so two writers of one record serialise: that is the guarantee the save path
/// relies on. The read has no <c>TOP</c>, so every base row carrying the id is locked, and the caller takes the first
/// in <c>UniqueKey</c> order. The seeded base tables key on <c>UniqueKey</c> (<c>int IDENTITY PRIMARY KEY</c>) and do
/// not index <c>GlobalRecordId</c>, and the schema is never changed, so the lookup scans the base table. Whether the
/// scan keeps the update locks it takes on rows that do not match depends on the plan the optimizer chooses, and SQL
/// Server guarantees neither outcome. In the plans observed on the seeded tables, the comparison with the full-length
/// (<c>nvarchar(max)</c>) id parameter this builder binds ran in a Filter operator above the clustered index scan, so
/// every scanned row stayed locked and a writer of another record of the table waited; with a parameter of the
/// column's own type the equality ran inside the scan, and only the matching row stayed locked. Nothing may therefore
/// depend on serialisation across a whole table, or assume its absence. Every later base statement addresses the row by
/// <c>UniqueKey</c> and <c>GlobalRecordId</c> together, a clustered-key seek, and page tables key on
/// <c>GlobalRecordId</c>, so page statements seek. A writer can still wait on another record's writer, in the locked
/// scan or in the snapshot counts; a deadlock that ends such a wait (1205) is reported by the repository as not
/// written.
/// </para>
/// <para>
/// <b>Read hint.</b> The database runs locking <c>READ COMMITTED</c>, with neither read-committed snapshot nor
/// snapshot isolation, and its settings are never changed. There a scan releases each shared lock as it moves on, so
/// a concurrent write committed mid-statement could leave a record's <c>RecStatus</c> and the rows counted for its
/// position from different states. <see cref="RecordAt"/> and <see cref="RecordById"/> therefore read the base table
/// <c>WITH (HOLDLOCK)</c>: every shared and key-range lock that read takes is held until the statement completes, so
/// no base row it read can change, and no row can be inserted among them, before it returns, and the row, its
/// intrinsic position and its count describe one committed state. Those reads run outside any transaction, so the
/// hint lasts one statement. Only the base table carries it: held page-row locks could deadlock with an update that
/// holds exclusive page-row locks. Every application write updates the base row before any of its page rows (a base
/// table with neither last-save column, which no seeded form has, takes only the update lock first), so a page row
/// read while the record's base row is held cannot change either.
/// </para>
/// <para>
/// Every exception message is fixed text naming no table, column, value or id, as the redaction policy requires.
/// The class holds no state and is safe to call from any thread.
/// </para>
/// </remarks>
internal static class RecordSqlBuilder
{
    /// <summary>The alias of the base table in every read.</summary>
    internal const string BaseAlias = "b";

    /// <summary>
    /// The ordinal of the base row's <c>GlobalRecordId</c>, as stored, in a <see cref="RecordSelect"/>.
    /// </summary>
    internal const int RecordGlobalRecordIdOrdinal = 0;

    /// <summary>The ordinal of the base row's <c>UniqueKey</c> in a <see cref="RecordSelect"/>.</summary>
    internal const int RecordUniqueKeyOrdinal = 1;

    /// <summary>The ordinal of the base row's <c>RecStatus</c> in a <see cref="RecordSelect"/>.</summary>
    internal const int RecordRecStatusOrdinal = 2;

    /// <summary>
    /// The ordinal of <c>UniqueKey</c> in the base-row reads <see cref="SetPositionLookup"/> and <see cref="LockBase"/>.
    /// </summary>
    internal const int BaseRowUniqueKeyOrdinal = 0;

    /// <summary>
    /// The ordinal of <c>RecStatus</c> in the base-row reads <see cref="SetPositionLookup"/> and <see cref="LockBase"/>.
    /// </summary>
    internal const int BaseRowRecStatusOrdinal = 1;

    /// <summary>
    /// The ordinal of <c>GlobalRecordId</c>, as stored, in the base-row reads <see cref="SetPositionLookup"/> and
    /// <see cref="LockBase"/>. The caller binds this spelling, never the request's, in every later statement of the
    /// operation: <see cref="RecordByUniqueKey"/>, <see cref="UpdateBaseStamp"/>, <see cref="UpdatePageColumns"/>,
    /// <see cref="InsertPageKey"/> and <see cref="SoftDelete"/>.
    /// </summary>
    internal const int BaseRowGlobalRecordIdOrdinal = 2;

    /// <summary>
    /// The ordinal of the record's intrinsic position, a <c>bigint</c>, in a <see cref="RecordSelect"/> whose
    /// <see cref="RecordSelect.HasIntrinsicCoordinates"/> is set.
    /// </summary>
    internal const int RecordIntrinsicPositionOrdinal = 3;

    /// <summary>
    /// The ordinal of the record's intrinsic count, a <c>bigint</c>, in a <see cref="RecordSelect"/> whose
    /// <see cref="RecordSelect.HasIntrinsicCoordinates"/> is set.
    /// </summary>
    internal const int RecordIntrinsicCountOrdinal = 4;

    /// <summary>
    /// The most columns one page <c>UPDATE</c> sets, the chunk size legacy Epi Info uses
    /// (<c>Epi.Core/Data/Services/CollectedDataProvider.cs:1274-1340</c>).
    /// </summary>
    internal const int MaxColumnsPerUpdate = 100;

    // The number of base columns every record SELECT starts with: GlobalRecordId, UniqueKey, RecStatus.
    private const int RecordBaseColumnCount = 3;

    // The alias of the base table inside the derived table of an intrinsic record read, and the fixed names that
    // derived table gives the intrinsic position and count. None of them reaches SQL text from a caller.
    private const string IntrinsicSourceAlias = "i";
    private const string IntrinsicPositionColumn = "[ip]";
    private const string IntrinsicCountColumn = "[ic]";

    // Catalogue lookup key of a page table's key column, matched through TableSchema.FindColumn, which returns the
    // reader-built column. The literal never reaches SQL text.
    private const string PageKeyColumnName = "GlobalRecordId";

    // Fixed messages: the redaction policy keeps identifiers, values and ids out of exception text.
    private const string MissingSystemColumnMessage = "The form's base table lacks a required system column.";
    private const string UnknownFieldMessage = "The field is not a column of the form.";
    private const string MissingPageKeyMessage = "A page table lacks its record key column.";
    private const string UnknownPageMessage = "The page is not a page of the form.";
    private const string PageWithoutTableMessage = "The page has no table to write.";
    private const string ForeignColumnMessage = "The column is not a column of the page table.";
    private const string DuplicateColumnMessage = "A column cannot be written twice in one statement.";
    private const string AmbiguousValueMessage = "Two submitted values name the same field.";
    private const string NullEntryMessage = "The list must not hold a null entry.";
    private const string UnknownClauseMessage = "The selected-set clause is not a defined member.";

    /// <summary>The alias of a joined page table: <c>p</c> followed by the page's index.</summary>
    /// <param name="pageIndex">The page's 0-based index in <see cref="FormSchema.Pages"/>.</param>
    /// <returns>For example <c>p0</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pageIndex"/> is negative.</exception>
    internal static string PageAlias(int pageIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);

        return "p" + pageIndex.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The alias of a result column: <c>[c</c>, its ordinal, <c>]</c>.</summary>
    /// <param name="ordinal">The column's 0-based ordinal in the result.</param>
    /// <returns>For example <c>[c5]</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="ordinal"/> is negative.</exception>
    internal static string ColumnAlias(int ordinal)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);

        return "[c" + ordinal.ToString(CultureInfo.InvariantCulture) + "]";
    }

    /// <summary>
    /// Appends the selected-set predicate: the keyword <paramref name="clause"/> selects, then
    /// <c>b.[RecStatus] = @pN</c>, for the active set; nothing when every row is selected. It is the one rendering of
    /// that predicate in the data layer; the rule itself is <see cref="RecordQueryRules.RequiredRecStatus"/>. Every
    /// piece of the text is fixed here or quoted from the reader-built column, and the caller supplies none of it.
    /// </summary>
    /// <param name="sql">
    /// The statement text being built, whose base table carries the alias <see cref="BaseAlias"/>.
    /// </param>
    /// <param name="schema">The form's catalogue view.</param>
    /// <param name="includeDeleted">Whether the caller selected every row.</param>
    /// <param name="parameters">The statement's parameters; the status value is added to them.</param>
    /// <param name="clause">
    /// The keyword written immediately before the predicate and only with it: <c>" WHERE "</c> for
    /// <see cref="SelectedSetClause.Where"/> and <c>" AND "</c> for <see cref="SelectedSetClause.And"/>, so a caller
    /// never leaves a dangling keyword when no predicate applies.
    /// </param>
    /// <returns>Whether a predicate was appended.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="sql"/>, <paramref name="schema"/> or <paramref name="parameters"/> is null.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="clause"/> is not a defined <see cref="SelectedSetClause"/>. Nothing is appended and no parameter
    /// is added, whatever <paramref name="includeDeleted"/> is.
    /// </exception>
    /// <exception cref="InvalidOperationException">The base table has no <c>RecStatus</c> column.</exception>
    internal static bool AppendSelectedSetPredicate(
        StringBuilder sql,
        FormSchema schema,
        bool includeDeleted,
        PositionalParameters parameters,
        SelectedSetClause clause)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(parameters);

        var keyword = clause switch
        {
            SelectedSetClause.Where => " WHERE ",
            SelectedSetClause.And => " AND ",
            _ => throw new ArgumentOutOfRangeException(nameof(clause), UnknownClauseMessage),
        };

        if (RecordQueryRules.RequiredRecStatus(includeDeleted) is not { } recStatus)
        {
            return false;
        }

        // The converter narrows the status to the column's own integer type, smallint in the seeded tables.
        var column = Require(schema.RecStatus);
        var name = parameters.Add(n => ColumnValueConverter.ToSystemParameter(n, column, recStatus));
        sql.Append(keyword);
        AppendBaseColumn(sql, column);
        sql.Append(" = ").Append(name);
        return true;
    }

    /// <summary>Returns a base-table system column the record SQL cannot do without.</summary>
    /// <param name="column">The column the reader found, such as <see cref="FormSchema.UniqueKey"/>.</param>
    /// <returns><paramref name="column"/>.</returns>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="column"/> is null: the base table lacks <c>UniqueKey</c>, <c>RecStatus</c> or
    /// <c>GlobalRecordId</c>.
    /// </exception>
    internal static ColumnSchema Require(ColumnSchema? column) =>
        column ?? throw new InvalidOperationException(MissingSystemColumnMessage);

    /// <summary>
    /// Resolves a data field to the reader-built column that stores it, through
    /// <see cref="FormSchema.FindDataColumn"/>.
    /// </summary>
    /// <param name="schema">The form's catalogue view.</param>
    /// <param name="field">A data field of the form.</param>
    /// <param name="ordinal">The ordinal the column takes in the statement's result.</param>
    /// <returns>The projected column.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="schema"/> or <paramref name="field"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="ordinal"/> is negative.</exception>
    /// <exception cref="ArgumentException">
    /// The schema holds no column for the field (parameter <c>values</c>). The repository resolves every submitted
    /// field through here before it sends any base- or page-table command, so an unknown field is refused first.
    /// </exception>
    internal static ProjectedColumn ResolveDataColumn(FormSchema schema, DataFieldMetadata field, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(field);
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);

        var column = schema.FindDataColumn(field) ?? throw new ArgumentException(UnknownFieldMessage, "values");
        return new ProjectedColumn(field, column, ordinal);
    }

    /// <summary>
    /// The index of a page in <see cref="FormSchema.Pages"/>, the number in its alias <see cref="PageAlias"/>.
    /// </summary>
    /// <param name="schema">The form's catalogue view.</param>
    /// <param name="pageId">The page's <c>metaPages.PageId</c>.</param>
    /// <returns>The index of the first page with that id, the page <see cref="FormSchema.FindPage"/> returns.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="schema"/> is null.</exception>
    /// <exception cref="ArgumentException">The form has no page with that id.</exception>
    internal static int PageIndexOf(FormSchema schema, int pageId)
    {
        ArgumentNullException.ThrowIfNull(schema);

        var pages = schema.Pages;
        for (var index = 0; index < pages.Count; index++)
        {
            if (pages[index].Page.PageId == pageId)
            {
                return index;
            }
        }

        throw new ArgumentException(UnknownPageMessage, nameof(pageId));
    }

    /// <summary>
    /// The qualified reference of a data-field column in a read: its page alias, a dot and the quoted column name.
    /// </summary>
    /// <param name="schema">The form's catalogue view.</param>
    /// <param name="column">A column from <see cref="ResolveDataColumn"/> on the same schema.</param>
    /// <returns>For example <c>p0.[LastName]</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="schema"/> or <paramref name="column"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// The column's page is not a page of the form, or the column is not a column of that page's table.
    /// </exception>
    internal static string ColumnReference(FormSchema schema, ProjectedColumn column)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(column);

        var index = PageIndexOf(schema, column.Field.Page.PageId);
        var table = schema.Pages[index].Table ?? throw new ArgumentException(ForeignColumnMessage, nameof(column));
        EnsureColumnOf(table, column.Column);

        return string.Concat(PageAlias(index), ".", SqlIdentifier.Quote(column.Column));
    }

    /// <summary>
    /// A new <c>GlobalRecordId</c> for an inserted record: a GUID in the lower-case hyphenated <c>D</c> format, the
    /// form of the seeded ids and of legacy <c>Guid.NewGuid().ToString()</c>.
    /// </summary>
    /// <returns>For example <c>3f2504e0-4f89-11d3-9a0c-0305e82c3301</c>.</returns>
    internal static string NewGlobalRecordId() => Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture);

    /// <summary>
    /// Counts the selected set: <c>SELECT COUNT_BIG(*) FROM [base] b [WHERE b.[RecStatus] = @p0]</c>.
    /// </summary>
    /// <param name="schema">The form's catalogue view.</param>
    /// <param name="includeDeleted">Whether every row is counted rather than the active set.</param>
    /// <returns>A statement returning one <c>bigint</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="schema"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The base table lacks <c>RecStatus</c>.</exception>
    internal static SqlStatement Count(FormSchema schema, bool includeDeleted)
    {
        ArgumentNullException.ThrowIfNull(schema);

        var parameters = new PositionalParameters();
        var sql = new StringBuilder("SELECT COUNT_BIG(*) FROM ");
        AppendBaseTable(sql, schema);
        AppendSelectedSetPredicate(sql, schema, includeDeleted, parameters, SelectedSetClause.Where);

        return parameters.ToStatement(sql.ToString());
    }

    /// <summary>
    /// The 1-based position of the record with key <paramref name="uniqueKey"/> in the selected set, the legacy
    /// formula <c>Count(*) ... Where UniqueKey &lt;= k</c> (<c>Epi.Enter.CheckCodeEngine/RunTimeView.cs:673-694</c>):
    /// <c>SELECT COUNT_BIG(*) FROM [base] b WHERE b.[UniqueKey] &lt;= @p0 [AND b.[RecStatus] = @p1]</c>. It places a
    /// record in the caller's selected set for <c>IDataStore.GetSetPositionAsync</c>; <see cref="RecordAt"/> and
    /// <see cref="RecordById"/> compute a record's intrinsic position in their own statement instead.
    /// </summary>
    /// <param name="schema">The form's catalogue view.</param>
    /// <param name="uniqueKey">The record's <c>UniqueKey</c>.</param>
    /// <param name="includeDeleted">Whether every row is counted rather than the active set.</param>
    /// <returns>A statement returning one <c>bigint</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="schema"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The base table lacks <c>UniqueKey</c> or <c>RecStatus</c>.</exception>
    internal static SqlStatement Position(FormSchema schema, int uniqueKey, bool includeDeleted)
    {
        ArgumentNullException.ThrowIfNull(schema);

        var uniqueKeyColumn = Require(schema.UniqueKey);
        var parameters = new PositionalParameters();
        var sql = new StringBuilder("SELECT COUNT_BIG(*) FROM ");
        AppendBaseTable(sql, schema);
        sql.Append(" WHERE ");
        AppendBaseColumn(sql, uniqueKeyColumn);
        sql.Append(" <= ")
            .Append(parameters.Add(n => ColumnValueConverter.ToSystemParameter(n, uniqueKeyColumn, uniqueKey)));
        AppendSelectedSetPredicate(sql, schema, includeDeleted, parameters, SelectedSetClause.And);

        return parameters.ToStatement(sql.ToString());
    }

    /// <summary>
    /// Finds a record's base row by id, reading the base table only, so a base row that lacks a page row is still
    /// placed: <c>SELECT b.[UniqueKey] AS [c0], b.[RecStatus] AS [c1], b.[GlobalRecordId] AS [c2] FROM [base] b WHERE
    /// b.[GlobalRecordId] = @p0 AND DATALENGTH(b.[GlobalRecordId]) = DATALENGTH(@p0) ORDER BY b.[UniqueKey]</c>. The
    /// caller takes the first row, and binds its stored id, not the request's, in any later statement.
    /// </summary>
    /// <param name="schema">The form's catalogue view.</param>
    /// <param name="globalRecordId">The id from the request, bound at full length and matched exactly.</param>
    /// <returns>
    /// A statement returning <c>UniqueKey</c> at <see cref="BaseRowUniqueKeyOrdinal"/>, <c>RecStatus</c> at
    /// <see cref="BaseRowRecStatusOrdinal"/> and the stored <c>GlobalRecordId</c> at
    /// <see cref="BaseRowGlobalRecordIdOrdinal"/>; no row for an unknown id, including one that differs from a stored
    /// id only by trailing spaces or is longer than the column.
    /// </returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="InvalidOperationException">The base table lacks a required system column.</exception>
    internal static SqlStatement SetPositionLookup(FormSchema schema, string globalRecordId)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(globalRecordId);

        return BaseRowById(schema, globalRecordId, tableHints: null);
    }

    /// <summary>
    /// Reads the record at a 1-based position of the selected set in <c>UniqueKey</c> order, with its intrinsic
    /// position and count from the same statement: the record <c>SELECT</c> over the intrinsic base read,
    /// <c>[WHERE b.[RecStatus] = @p1]</c>, then <c>ORDER BY b.[UniqueKey] OFFSET @pN ROWS FETCH NEXT 1 ROWS ONLY</c>.
    /// The offset is a <c>bigint</c>, so no position can wrap at 32 bits.
    /// </summary>
    /// <remarks>
    /// <para>The statement has this shape:</para>
    /// <code>
    /// SELECT b.[GlobalRecordId] AS [c0], b.[UniqueKey] AS [c1], b.[RecStatus] AS [c2], b.[ip] AS [c3], b.[ic] AS [c4],
    ///   p0.[GlobalRecordId] AS [c5], ..., p0.[col] AS [cN], ...
    /// FROM (SELECT i.[GlobalRecordId], i.[UniqueKey], i.[RecStatus],
    ///   CASE WHEN i.[RecStatus] = @p0 THEN SUM(CASE WHEN i.[RecStatus] = @p0 THEN CAST(1 AS bigint)
    ///     ELSE CAST(0 AS bigint) END) OVER (ORDER BY i.[UniqueKey] ROWS UNBOUNDED PRECEDING)
    ///     ELSE ROW_NUMBER() OVER (ORDER BY i.[UniqueKey]) END AS [ip],
    ///   CASE WHEN i.[RecStatus] = @p0 THEN SUM(CASE WHEN i.[RecStatus] = @p0 THEN CAST(1 AS bigint)
    ///     ELSE CAST(0 AS bigint) END) OVER () ELSE COUNT_BIG(*) OVER () END AS [ic]
    ///   FROM [base] i WITH (HOLDLOCK)) b
    /// LEFT JOIN [page] p0 ON p0.[GlobalRecordId] = b.[GlobalRecordId] ...
    /// [WHERE b.[RecStatus] = @p1]
    /// ORDER BY b.[UniqueKey] OFFSET @p2 ROWS FETCH NEXT 1 ROWS ONLY
    /// </code>
    /// <para>
    /// <c>@p0</c> is <see cref="RecordQueryRules.ActiveRecStatus"/>, bound once with the <c>RecStatus</c> column's own
    /// type. The derived table computes, for every row of the base table, the intrinsic position and count of
    /// <see cref="RecordQueryRules.IntrinsicIncludesDeleted"/>: an active row gets the number of active rows whose key
    /// is at most its own and the active count; any other row gets its row number over every row and the count of every
    /// row. Those are the legacy formula <c>Count(*) ... Where UniqueKey &lt;= k</c> over the record's own set, because
    /// <c>UniqueKey</c> is unique. The windows have no <c>PARTITION BY</c>, so they are computed over the whole table
    /// before the outer predicate selects a row, whichever set <paramref name="includeDeleted"/> names. The read runs
    /// under the base-table hint described in the class remarks.
    /// </para>
    /// </remarks>
    /// <param name="schema">The form's catalogue view.</param>
    /// <param name="dataFields">The data fields to project, in data-field order.</param>
    /// <param name="includeDeleted">Whether the selected set holds every row rather than the active set.</param>
    /// <param name="position">The 1-based position, at least 1.</param>
    /// <returns>
    /// The statement and its result layout, with <see cref="RecordSelect.HasIntrinsicCoordinates"/> set; no row when
    /// the position is past the end.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="schema"/> or <paramref name="dataFields"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="position"/> is less than 1.</exception>
    /// <exception cref="ArgumentException">A data field is not a column of the form, or the list holds a null entry.</exception>
    /// <exception cref="InvalidOperationException">A table lacks a required system or key column.</exception>
    internal static RecordSelect RecordAt(
        FormSchema schema,
        IReadOnlyList<DataFieldMetadata> dataFields,
        bool includeDeleted,
        int position)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(dataFields);

        var offset = RecordQueryRules.PositionToOffset(position);
        var parameters = new PositionalParameters();
        var core = RecordSelectCore(schema, dataFields, intrinsicParameters: parameters);
        AppendSelectedSetPredicate(core.Sql, schema, includeDeleted, parameters, SelectedSetClause.Where);
        AppendOrderByUniqueKey(core.Sql, schema);
        core.Sql.Append(" OFFSET ")
            .Append(parameters.Add(n => ColumnValueConverter.Int64(n, offset)))
            .Append(" ROWS FETCH NEXT 1 ROWS ONLY");

        return core.Finish(parameters);
    }

    /// <summary>
    /// Reads a record by id, deleted records included, with its intrinsic position and count from the same statement:
    /// the record <c>SELECT</c> over the intrinsic base read of <see cref="RecordAt"/>, then
    /// <c>WHERE b.[GlobalRecordId] = @p1 AND DATALENGTH(b.[GlobalRecordId]) = DATALENGTH(@p1) ORDER BY
    /// b.[UniqueKey]</c>. The caller takes the first row, and binds the stored id it returns at
    /// <see cref="RecordGlobalRecordIdOrdinal"/>, not the request's, in any later statement.
    /// </summary>
    /// <param name="schema">The form's catalogue view.</param>
    /// <param name="dataFields">The data fields to project, in data-field order.</param>
    /// <param name="globalRecordId">The id from the request, bound at full length and matched exactly.</param>
    /// <returns>
    /// The statement and its result layout, with <see cref="RecordSelect.HasIntrinsicCoordinates"/> set; no row for an
    /// unknown id, including one that differs from a stored id only by trailing spaces or is longer than the column.
    /// </returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">A data field is not a column of the form, or the list holds a null entry.</exception>
    /// <exception cref="InvalidOperationException">A table lacks a required system or key column.</exception>
    internal static RecordSelect RecordById(
        FormSchema schema,
        IReadOnlyList<DataFieldMetadata> dataFields,
        string globalRecordId)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(dataFields);
        ArgumentNullException.ThrowIfNull(globalRecordId);

        var parameters = new PositionalParameters();
        var core = RecordSelectCore(schema, dataFields, intrinsicParameters: parameters);
        core.Sql.Append(" WHERE ");
        AppendGlobalRecordIdMatch(
            core.Sql,
            schema,
            parameters,
            BaseAlias,
            (n, _) => ColumnValueConverter.Text(n, globalRecordId));
        AppendOrderByUniqueKey(core.Sql, schema);

        return core.Finish(parameters);
    }

    /// <summary>
    /// Reads a record whose base row the caller has already located, by its clustered key and id together: the record
    /// <c>SELECT</c>, then <c>WHERE b.[UniqueKey] = @p0 AND b.[GlobalRecordId] = @p1 AND
    /// DATALENGTH(b.[GlobalRecordId]) = DATALENGTH(@p1)</c>. Write transactions use it for the page-row read and the
    /// response snapshot, so the base row is reached by a seek rather than a scan.
    /// </summary>
    /// <param name="schema">The form's catalogue view.</param>
    /// <param name="dataFields">The data fields to project, in data-field order.</param>
    /// <param name="uniqueKey">The base row's <c>UniqueKey</c>.</param>
    /// <param name="globalRecordId">
    /// The base row's <c>GlobalRecordId</c>, as stored: the value <see cref="LockBase"/> returned at
    /// <see cref="BaseRowGlobalRecordIdOrdinal"/>. A spelling of another length, such as one with trailing spaces,
    /// matches no row.
    /// </param>
    /// <returns>
    /// The statement and its result layout, with <see cref="RecordSelect.HasIntrinsicCoordinates"/> clear.
    /// </returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">
    /// A data field is not a column of the form, the list holds a null entry, or the id does not fit the column.
    /// </exception>
    /// <exception cref="InvalidOperationException">A table lacks a required system or key column.</exception>
    internal static RecordSelect RecordByUniqueKey(
        FormSchema schema,
        IReadOnlyList<DataFieldMetadata> dataFields,
        int uniqueKey,
        string globalRecordId)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(dataFields);
        ArgumentNullException.ThrowIfNull(globalRecordId);

        var core = RecordSelectCore(schema, dataFields, intrinsicParameters: null);
        var parameters = new PositionalParameters();
        core.Sql.Append(" WHERE ");
        AppendBaseRowKey(core.Sql, schema, parameters, uniqueKey, globalRecordId, BaseAlias);

        return core.Finish(parameters);
    }

    /// <summary>
    /// Inserts a record's base row and returns its new key: <c>INSERT INTO [base] ([GlobalRecordId], [RecStatus][,
    /// [FirstSaveLogonName]][, [FirstSaveTime]][, [LastSaveLogonName]][, [LastSaveTime]]) OUTPUT INSERTED.[UniqueKey]
    /// VALUES (@p0, @p1, ...)</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>RecStatus</c> is <see cref="RecordQueryRules.ActiveRecStatus"/>. Each stamp column is named only when the
    /// base table has it, as legacy checks (<c>Epi.Core/Data/Services/CollectedDataProvider.cs:966-997</c>), and all
    /// four take their values from the one <paramref name="stamp"/>. Legacy wrote the first-save pair with the insert
    /// and the last-save pair in a separate update with its own clock reading (<c>:963-1004</c>); one stamp for both
    /// pairs is a deliberate reconciliation recorded in the parity notes.
    /// </para>
    /// <para>
    /// <c>FKEY</c> is never named. Legacy names it only when the form has a foreign-key field
    /// (<c>:936-961</c>), neither seeded form has one, so the column keeps its <c>NULL</c> default as legacy leaves it.
    /// </para>
    /// </remarks>
    /// <param name="schema">The form's catalogue view.</param>
    /// <param name="globalRecordId">The new record's id, normally from <see cref="NewGlobalRecordId"/>.</param>
    /// <param name="stamp">The save stamp of this write.</param>
    /// <returns>A statement returning the new <c>UniqueKey</c> as one <c>int</c>.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="globalRecordId"/> is blank, or a value does not fit its column.
    /// </exception>
    /// <exception cref="InvalidOperationException">The base table lacks a required system column.</exception>
    internal static SqlStatement InsertBase(FormSchema schema, string globalRecordId, SaveStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(globalRecordId);
        ArgumentNullException.ThrowIfNull(stamp);

        var uniqueKeyColumn = Require(schema.UniqueKey);
        var assignments = new List<(ColumnSchema Column, object Value)>
        {
            (Require(schema.GlobalRecordId), globalRecordId),
            (Require(schema.RecStatus), RecordQueryRules.ActiveRecStatus),
        };
        AddIfPresent(assignments, schema.FirstSaveLogonName, stamp.LogonName);
        AddIfPresent(assignments, schema.FirstSaveTime, stamp.Time);
        AddIfPresent(assignments, schema.LastSaveLogonName, stamp.LogonName);
        AddIfPresent(assignments, schema.LastSaveTime, stamp.Time);

        var parameters = new PositionalParameters();
        var names = new List<string>(assignments.Count);
        var sql = new StringBuilder("INSERT INTO ").Append(SqlIdentifier.Quote(schema.BaseTable)).Append(" (");
        for (var index = 0; index < assignments.Count; index++)
        {
            var (column, value) = assignments[index];
            sql.Append(index == 0 ? string.Empty : ", ").Append(SqlIdentifier.Quote(column));
            names.Add(parameters.Add(n => ColumnValueConverter.ToSystemParameter(n, column, value)));
        }

        sql.Append(") OUTPUT INSERTED.").Append(SqlIdentifier.Quote(uniqueKeyColumn))
            .Append(" VALUES (").AppendJoin(", ", names).Append(')');

        return parameters.ToStatement(sql.ToString());
    }

    /// <summary>
    /// Inserts a new record's row into one page table: <c>INSERT INTO [page] ([GlobalRecordId], [col1], ...) VALUES
    /// (@p0, @p1, ...)</c>, naming the key and every data-field column given.
    /// </summary>
    /// <remarks>
    /// The caller passes every data-field column of the page. A column without a submitted value is written
    /// <c>NULL</c>. Page-table columns that are not data-field columns, such as the seeded <c>Yes/No</c> fields on
    /// <c>tinyint</c> columns, are never named, so they keep <c>NULL</c>.
    /// </remarks>
    /// <param name="page">The page whose table receives the row.</param>
    /// <param name="globalRecordId">The new record's id, as written to the base row.</param>
    /// <param name="pageColumns">The page's data-field columns, from <see cref="ResolveDataColumn"/>.</param>
    /// <param name="values">
    /// The submitted values keyed by field name; the canonical name is matched exactly first, then ignoring case.
    /// </param>
    /// <returns>The insert statement.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">
    /// The page has no table; the id is blank or does not fit the key column; a column is not one of the page table's,
    /// is given twice or is null; two values name the same field ignoring case; or a value does not fit its column.
    /// </exception>
    /// <exception cref="InvalidOperationException">The page table lacks its key column.</exception>
    internal static SqlStatement InsertPage(
        PageTableSchema page,
        string globalRecordId,
        IReadOnlyList<ProjectedColumn> pageColumns,
        IReadOnlyDictionary<string, FieldValue> values)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentException.ThrowIfNullOrWhiteSpace(globalRecordId);
        ArgumentNullException.ThrowIfNull(pageColumns);
        ArgumentNullException.ThrowIfNull(values);

        var table = RequireTable(page);
        var key = RequirePageKey(table);
        EnsurePageColumns(page, table, key, pageColumns, static column => column, nameof(pageColumns));

        var parameters = new PositionalParameters();
        var names = new List<string>(pageColumns.Count + 1)
        {
            parameters.Add(n => ColumnValueConverter.ToSystemParameter(n, key, globalRecordId)),
        };
        var sql = new StringBuilder("INSERT INTO ").Append(SqlIdentifier.Quote(table))
            .Append(" (").Append(SqlIdentifier.Quote(key));
        foreach (var column in pageColumns)
        {
            var value = SubmittedValue(values, column.Field.Name);
            sql.Append(", ").Append(SqlIdentifier.Quote(column.Column));
            names.Add(parameters.Add(n =>
                ColumnValueConverter.ToParameter(n, column.Column.Metadata, column.Field.FieldType, value)));
        }

        sql.Append(") VALUES (").AppendJoin(", ", names).Append(')');

        return parameters.ToStatement(sql.ToString());
    }

    /// <summary>
    /// Locks and reads a record's base row at the start of an update or a soft delete: <c>SELECT b.[UniqueKey] AS
    /// [c0], b.[RecStatus] AS [c1], b.[GlobalRecordId] AS [c2] FROM [base] b WITH (UPDLOCK, ROWLOCK) WHERE
    /// b.[GlobalRecordId] = @p0 AND DATALENGTH(b.[GlobalRecordId]) = DATALENGTH(@p0) ORDER BY b.[UniqueKey]</c>.
    /// </summary>
    /// <remarks>
    /// The update lock on the matched base row is held to the end of the transaction, so two writers of the same record
    /// serialise here, and two saves of one record can never both pass the required gate on stale state. The read has
    /// no <c>TOP</c>, so every base row carrying the id is locked, and the caller takes the first in <c>UniqueKey</c>
    /// order. The base table does not index <c>GlobalRecordId</c>, so the read scans it, and whether the locks it takes
    /// on rows that do not match are held as well depends on the plan, as the class remarks describe: no caller may
    /// rely on this read to serialise the writers of different records, and a wait on such a writer that ends in a
    /// deadlock (1205) is reported as not written. It returns <c>UniqueKey</c> and the stored <c>GlobalRecordId</c>
    /// because every later base statement of the transaction seeks the row by both, and every later page statement
    /// binds that stored id.
    /// </remarks>
    /// <param name="schema">The form's catalogue view.</param>
    /// <param name="globalRecordId">The id from the request, bound at full length and matched exactly.</param>
    /// <returns>
    /// A statement returning <c>UniqueKey</c> at <see cref="BaseRowUniqueKeyOrdinal"/>, <c>RecStatus</c> at
    /// <see cref="BaseRowRecStatusOrdinal"/> and the stored <c>GlobalRecordId</c> at
    /// <see cref="BaseRowGlobalRecordIdOrdinal"/>; no row for an unknown id, including one that differs from a stored
    /// id only by trailing spaces or is longer than the column, so the caller reports it missing before any write. The
    /// caller takes the first row.
    /// </returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="InvalidOperationException">The base table lacks a required system column.</exception>
    internal static SqlStatement LockBase(FormSchema schema, string globalRecordId)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(globalRecordId);

        return BaseRowById(schema, globalRecordId, tableHints: " WITH (UPDLOCK, ROWLOCK)");
    }

    /// <summary>
    /// Stamps a record's base row on update: <c>UPDATE [base] SET [LastSaveLogonName] = @p0, [LastSaveTime] = @p1
    /// WHERE [UniqueKey] = @p2 AND [GlobalRecordId] = @p3 AND DATALENGTH([GlobalRecordId]) = DATALENGTH(@p3)</c>,
    /// naming only the stamp columns the base table has.
    /// </summary>
    /// <param name="schema">The form's catalogue view.</param>
    /// <param name="uniqueKey">The locked base row's <c>UniqueKey</c>.</param>
    /// <param name="globalRecordId">
    /// The locked base row's <c>GlobalRecordId</c>, as stored: the value <see cref="LockBase"/> returned at
    /// <see cref="BaseRowGlobalRecordIdOrdinal"/>. A spelling of another length, such as one with trailing spaces,
    /// matches no row.
    /// </param>
    /// <param name="stamp">The save stamp of this write.</param>
    /// <returns>The update, or <see langword="null"/> when the base table has neither last-save column.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">A value does not fit its column.</exception>
    /// <exception cref="InvalidOperationException">The base table lacks a required system column.</exception>
    internal static SqlStatement? UpdateBaseStamp(
        FormSchema schema,
        int uniqueKey,
        string globalRecordId,
        SaveStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(globalRecordId);
        ArgumentNullException.ThrowIfNull(stamp);

        var assignments = new List<(ColumnSchema Column, object Value)>(2);
        AddIfPresent(assignments, schema.LastSaveLogonName, stamp.LogonName);
        AddIfPresent(assignments, schema.LastSaveTime, stamp.Time);

        return assignments.Count == 0 ? null : UpdateBaseRow(schema, uniqueKey, globalRecordId, assignments);
    }

    /// <summary>
    /// Writes the submitted columns of one page row: <c>UPDATE [page] SET [colA] = @p0, ... WHERE [GlobalRecordId] =
    /// @pN</c>, at most <see cref="MaxColumnsPerUpdate"/> columns per statement, as legacy chunks its page updates
    /// (<c>Epi.Core/Data/Services/CollectedDataProvider.cs:1274-1340</c>).
    /// </summary>
    /// <remarks>
    /// Only submitted columns are named, so two concurrent saves of different fields both keep their values. Legacy
    /// rewrote every column of every page (<c>:1225-1392</c>); the delta is a deliberate reconciliation recorded in the
    /// parity notes. Each statement has its own parameters, far below the SQL Server limit of 2,100. When the first
    /// statement affects no row, the caller sends <see cref="InsertPageKey"/> and repeats it, as legacy does
    /// (<c>:1360-1380</c>).
    /// </remarks>
    /// <param name="page">The page whose table holds the row.</param>
    /// <param name="globalRecordId">The record's id, as stored in the locked base row.</param>
    /// <param name="submitted">The page's submitted columns and their values, in the order to write them.</param>
    /// <returns>The statements in order; empty when nothing is submitted.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">
    /// The page has no table; the id is blank or does not fit the key column; a column is not one of the page table's,
    /// is given twice or is null; or a value does not fit its column.
    /// </exception>
    /// <exception cref="InvalidOperationException">The page table lacks its key column.</exception>
    internal static IReadOnlyList<SqlStatement> UpdatePageColumns(
        PageTableSchema page,
        string globalRecordId,
        IReadOnlyList<(ProjectedColumn Column, FieldValue Value)> submitted)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentException.ThrowIfNullOrWhiteSpace(globalRecordId);
        ArgumentNullException.ThrowIfNull(submitted);

        var table = RequireTable(page);
        var key = RequirePageKey(table);
        EnsurePageColumns(page, table, key, submitted, static entry => entry.Column, nameof(submitted));

        var statements = new List<SqlStatement>((submitted.Count + MaxColumnsPerUpdate - 1) / MaxColumnsPerUpdate);
        for (var start = 0; start < submitted.Count; start += MaxColumnsPerUpdate)
        {
            var end = Math.Min(start + MaxColumnsPerUpdate, submitted.Count);
            var parameters = new PositionalParameters();
            var sql = new StringBuilder("UPDATE ").Append(SqlIdentifier.Quote(table)).Append(" SET ");
            for (var index = start; index < end; index++)
            {
                var (column, value) = submitted[index];
                sql.Append(index == start ? string.Empty : ", ")
                    .Append(SqlIdentifier.Quote(column.Column))
                    .Append(" = ")
                    .Append(parameters.Add(n =>
                        ColumnValueConverter.ToParameter(n, column.Column.Metadata, column.Field.FieldType, value)));
            }

            sql.Append(" WHERE ").Append(SqlIdentifier.Quote(key)).Append(" = ")
                .Append(parameters.Add(n => ColumnValueConverter.ToSystemParameter(n, key, globalRecordId)));
            statements.Add(parameters.ToStatement(sql.ToString()));
        }

        return statements.AsReadOnly();
    }

    /// <summary>
    /// Inserts a page row holding only the record key, for a record whose page row is missing:
    /// <c>INSERT INTO [page] ([GlobalRecordId]) VALUES (@p0)</c>, the legacy repair of
    /// <c>Epi.Core/Data/Services/CollectedDataProvider.cs:1360-1380</c>.
    /// </summary>
    /// <param name="page">The page whose table receives the row.</param>
    /// <param name="globalRecordId">The record's id, as stored in the locked base row.</param>
    /// <returns>The insert statement.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="page"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// The page has no table, or the id is blank or does not fit the key column.
    /// </exception>
    /// <exception cref="InvalidOperationException">The page table lacks its key column.</exception>
    internal static SqlStatement InsertPageKey(PageTableSchema page, string globalRecordId)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentException.ThrowIfNullOrWhiteSpace(globalRecordId);

        var table = RequireTable(page);
        var key = RequirePageKey(table);
        var parameters = new PositionalParameters();
        var sql = new StringBuilder("INSERT INTO ").Append(SqlIdentifier.Quote(table))
            .Append(" (").Append(SqlIdentifier.Quote(key)).Append(") VALUES (")
            .Append(parameters.Add(n => ColumnValueConverter.ToSystemParameter(n, key, globalRecordId)))
            .Append(')');

        return parameters.ToStatement(sql.ToString());
    }

    /// <summary>
    /// Soft-deletes a record on its base row only: <c>UPDATE [base] SET [RecStatus] = @p0[, [LastSaveLogonName] =
    /// @p1][, [LastSaveTime] = @p2] WHERE [UniqueKey] = @pN AND [GlobalRecordId] = @pM AND
    /// DATALENGTH([GlobalRecordId]) = DATALENGTH(@pM)</c>.
    /// </summary>
    /// <remarks>
    /// <c>RecStatus</c> becomes <see cref="RecordQueryRules.DeletedRecStatus"/> and the last-save stamps are written
    /// when the base table has them. No page table is named and nothing cascades: legacy also rewrote every page row
    /// and cascaded to related forms (<c>Epi.Windows.Enter/PresentationLogic/GuiMediator.Event.Handlers.cs:641-722</c>),
    /// which the base-only soft delete does not carry.
    /// </remarks>
    /// <param name="schema">The form's catalogue view.</param>
    /// <param name="uniqueKey">The locked base row's <c>UniqueKey</c>.</param>
    /// <param name="globalRecordId">
    /// The locked base row's <c>GlobalRecordId</c>, as stored: the value <see cref="LockBase"/> returned at
    /// <see cref="BaseRowGlobalRecordIdOrdinal"/>. A spelling of another length, such as one with trailing spaces,
    /// matches no row.
    /// </param>
    /// <param name="stamp">The save stamp of this write.</param>
    /// <returns>The update statement.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">A value does not fit its column.</exception>
    /// <exception cref="InvalidOperationException">The base table lacks a required system column.</exception>
    internal static SqlStatement SoftDelete(FormSchema schema, int uniqueKey, string globalRecordId, SaveStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(globalRecordId);
        ArgumentNullException.ThrowIfNull(stamp);

        var assignments = new List<(ColumnSchema Column, object Value)>(3)
        {
            (Require(schema.RecStatus), RecordQueryRules.DeletedRecStatus),
        };
        AddIfPresent(assignments, schema.LastSaveLogonName, stamp.LogonName);
        AddIfPresent(assignments, schema.LastSaveTime, stamp.Time);

        return UpdateBaseRow(schema, uniqueKey, globalRecordId, assignments);
    }

    // The record SELECT list and FROM clause shared by every record read: the base system columns, the intrinsic
    // position and count when intrinsicParameters is given, one key column per joined page, then every data-field
    // column, over the base table LEFT JOINed to each page that has a table. A LEFT JOIN reproduces legacy's per-page
    // load, where a missing page row reads as null values (Epi.Core/Data/Services/CollectedDataProvider.cs:1652-1759).
    // With intrinsicParameters the base table is read through the derived table of AppendIntrinsicBaseSource, which
    // takes the first parameter of the statement; without it the base table is read directly.
    private static SelectCore RecordSelectCore(
        FormSchema schema,
        IReadOnlyList<DataFieldMetadata> dataFields,
        PositionalParameters? intrinsicParameters)
    {
        var globalRecordIdColumn = Require(schema.GlobalRecordId);
        var uniqueKeyColumn = Require(schema.UniqueKey);
        var recStatusColumn = Require(schema.RecStatus);
        var hasIntrinsicCoordinates = intrinsicParameters is not null;

        var sql = new StringBuilder("SELECT ");
        AppendProjection(sql, BaseAlias, globalRecordIdColumn, RecordGlobalRecordIdOrdinal);
        sql.Append(", ");
        AppendProjection(sql, BaseAlias, uniqueKeyColumn, RecordUniqueKeyOrdinal);
        sql.Append(", ");
        AppendProjection(sql, BaseAlias, recStatusColumn, RecordRecStatusOrdinal);

        var ordinal = RecordBaseColumnCount;
        if (hasIntrinsicCoordinates)
        {
            sql.Append(", ").Append(BaseAlias).Append('.').Append(IntrinsicPositionColumn)
                .Append(" AS ").Append(ColumnAlias(RecordIntrinsicPositionOrdinal))
                .Append(", ").Append(BaseAlias).Append('.').Append(IntrinsicCountColumn)
                .Append(" AS ").Append(ColumnAlias(RecordIntrinsicCountOrdinal));
            ordinal = RecordIntrinsicCountOrdinal + 1;
        }

        var joins = new StringBuilder();
        var presences = new List<PagePresence>(schema.Pages.Count);
        for (var index = 0; index < schema.Pages.Count; index++)
        {
            var page = schema.Pages[index];
            if (page.Table is not { } table)
            {
                continue;
            }

            var key = RequirePageKey(table);
            var alias = PageAlias(index);
            sql.Append(", ");
            AppendProjection(sql, alias, key, ordinal);
            presences.Add(new PagePresence(page, ordinal));
            ordinal++;

            joins.Append(" LEFT JOIN ").Append(SqlIdentifier.Quote(table)).Append(' ').Append(alias).Append(" ON ");
            AppendQualified(joins, alias, key).Append(" = ");
            AppendQualified(joins, BaseAlias, globalRecordIdColumn);
        }

        var columns = new List<ProjectedColumn>(dataFields.Count);
        foreach (var field in dataFields)
        {
            if (field is null)
            {
                throw new ArgumentException(NullEntryMessage, nameof(dataFields));
            }

            var column = ResolveDataColumn(schema, field, ordinal);
            sql.Append(", ").Append(ColumnReference(schema, column)).Append(" AS ").Append(ColumnAlias(ordinal));
            columns.Add(column);
            ordinal++;
        }

        sql.Append(" FROM ");
        if (intrinsicParameters is null)
        {
            AppendBaseTable(sql, schema);
        }
        else
        {
            AppendIntrinsicBaseSource(sql, schema, intrinsicParameters);
        }

        sql.Append(joins);

        return new SelectCore(sql, columns.AsReadOnly(), presences.AsReadOnly(), hasIntrinsicCoordinates);
    }

    // (SELECT i.[GlobalRecordId], i.[UniqueKey], i.[RecStatus], <position> AS [ip], <count> AS [ic]
    //  FROM [base] i WITH (HOLDLOCK)) b, the shape RecordAt documents. The derived table exposes the base system
    // columns under their catalogue names, so the outer query's b.[...] references stay those of a direct base read.
    // Its windows run over the whole base table before the outer predicate, and @active, RecStatus = 1 bound with the
    // column's own type, is one parameter used four times. Only the base table carries HOLDLOCK (class remarks).
    private static void AppendIntrinsicBaseSource(
        StringBuilder sql,
        FormSchema schema,
        PositionalParameters parameters)
    {
        var globalRecordIdColumn = Require(schema.GlobalRecordId);
        var uniqueKeyColumn = Require(schema.UniqueKey);
        var recStatusColumn = Require(schema.RecStatus);
        var active = parameters.Add(n =>
            ColumnValueConverter.ToSystemParameter(n, recStatusColumn, RecordQueryRules.ActiveRecStatus));

        var isActive = new StringBuilder();
        AppendQualified(isActive, IntrinsicSourceAlias, recStatusColumn).Append(" = ").Append(active);
        var orderByKey = new StringBuilder("ORDER BY ");
        AppendQualified(orderByKey, IntrinsicSourceAlias, uniqueKeyColumn);
        var activeRows = new StringBuilder("SUM(CASE WHEN ").Append(isActive)
            .Append(" THEN CAST(1 AS bigint) ELSE CAST(0 AS bigint) END)");

        sql.Append("(SELECT ");
        AppendQualified(sql, IntrinsicSourceAlias, globalRecordIdColumn).Append(", ");
        AppendQualified(sql, IntrinsicSourceAlias, uniqueKeyColumn).Append(", ");
        AppendQualified(sql, IntrinsicSourceAlias, recStatusColumn);
        sql.Append(", CASE WHEN ").Append(isActive).Append(" THEN ").Append(activeRows)
            .Append(" OVER (").Append(orderByKey).Append(" ROWS UNBOUNDED PRECEDING) ELSE ROW_NUMBER() OVER (")
            .Append(orderByKey).Append(") END AS ").Append(IntrinsicPositionColumn);
        sql.Append(", CASE WHEN ").Append(isActive).Append(" THEN ").Append(activeRows)
            .Append(" OVER () ELSE COUNT_BIG(*) OVER () END AS ").Append(IntrinsicCountColumn);
        sql.Append(" FROM ").Append(SqlIdentifier.Quote(schema.BaseTable)).Append(' ').Append(IntrinsicSourceAlias)
            .Append(" WITH (HOLDLOCK)) ").Append(BaseAlias);
    }

    // SELECT b.[UniqueKey] AS [c0], b.[RecStatus] AS [c1], b.[GlobalRecordId] AS [c2] FROM [base] b<hints> WHERE
    // b.[GlobalRecordId] = @p0 AND DATALENGTH(b.[GlobalRecordId]) = DATALENGTH(@p0) ORDER BY b.[UniqueKey]. The id
    // comes from a request and the base table has not confirmed it yet, so it is bound at full length and matched
    // exactly: an id longer than the column, or one that differs from a stored id only by trailing spaces, finds no row
    // rather than failing to bind or matching that stored id. The row returns the id as stored, the spelling every
    // later statement of the caller binds at the column's own size.
    private static SqlStatement BaseRowById(FormSchema schema, string globalRecordId, string? tableHints)
    {
        var uniqueKeyColumn = Require(schema.UniqueKey);
        var recStatusColumn = Require(schema.RecStatus);
        var globalRecordIdColumn = Require(schema.GlobalRecordId);

        var parameters = new PositionalParameters();
        var sql = new StringBuilder("SELECT ");
        AppendProjection(sql, BaseAlias, uniqueKeyColumn, BaseRowUniqueKeyOrdinal);
        sql.Append(", ");
        AppendProjection(sql, BaseAlias, recStatusColumn, BaseRowRecStatusOrdinal);
        sql.Append(", ");
        AppendProjection(sql, BaseAlias, globalRecordIdColumn, BaseRowGlobalRecordIdOrdinal);
        sql.Append(" FROM ");
        AppendBaseTable(sql, schema);
        sql.Append(tableHints).Append(" WHERE ");
        AppendGlobalRecordIdMatch(
            sql,
            schema,
            parameters,
            BaseAlias,
            (n, _) => ColumnValueConverter.Text(n, globalRecordId));
        AppendOrderByUniqueKey(sql, schema);

        return parameters.ToStatement(sql.ToString());
    }

    // UPDATE [base] SET [a] = @p0, ... WHERE [UniqueKey] = @pN AND [GlobalRecordId] = @pM AND
    // DATALENGTH([GlobalRecordId]) = DATALENGTH(@pM), with no alias.
    private static SqlStatement UpdateBaseRow(
        FormSchema schema,
        int uniqueKey,
        string globalRecordId,
        IReadOnlyList<(ColumnSchema Column, object Value)> assignments)
    {
        var parameters = new PositionalParameters();
        var sql = new StringBuilder("UPDATE ").Append(SqlIdentifier.Quote(schema.BaseTable)).Append(" SET ");
        for (var index = 0; index < assignments.Count; index++)
        {
            var (column, value) = assignments[index];
            sql.Append(index == 0 ? string.Empty : ", ")
                .Append(SqlIdentifier.Quote(column))
                .Append(" = ")
                .Append(parameters.Add(n => ColumnValueConverter.ToSystemParameter(n, column, value)));
        }

        sql.Append(" WHERE ");
        AppendBaseRowKey(sql, schema, parameters, uniqueKey, globalRecordId, alias: null);

        return parameters.ToStatement(sql.ToString());
    }

    // [alias.][UniqueKey] = @pN AND [alias.][GlobalRecordId] = @pM AND DATALENGTH([alias.][GlobalRecordId]) =
    // DATALENGTH(@pM): a clustered-key seek that also confirms the stored id exactly, both bound with the types of the
    // reader-built columns.
    private static void AppendBaseRowKey(
        StringBuilder sql,
        FormSchema schema,
        PositionalParameters parameters,
        int uniqueKey,
        string globalRecordId,
        string? alias)
    {
        var uniqueKeyColumn = Require(schema.UniqueKey);

        AppendQualified(sql, alias, uniqueKeyColumn)
            .Append(" = ")
            .Append(parameters.Add(n => ColumnValueConverter.ToSystemParameter(n, uniqueKeyColumn, uniqueKey)))
            .Append(" AND ");
        AppendGlobalRecordIdMatch(
            sql,
            schema,
            parameters,
            alias,
            (n, column) => ColumnValueConverter.ToSystemParameter(n, column, globalRecordId));
    }

    // [alias.][GlobalRecordId] = @pN AND DATALENGTH([alias.][GlobalRecordId]) = DATALENGTH(@pN): the one exact match on
    // a base row's id. SQL Server compares strings as if the shorter were padded with trailing spaces, so equality
    // alone would let an id that differs only by trailing spaces match; equal byte lengths rule that out, while letter
    // case still follows the column's collation. One positional parameter, referenced twice, carries the id; bind
    // receives its name and the reader-built column, so the caller keeps the binding choice: full length for a request
    // id, the column's own size for a stored id.
    private static void AppendGlobalRecordIdMatch(
        StringBuilder sql,
        FormSchema schema,
        PositionalParameters parameters,
        string? alias,
        Func<string, ColumnSchema, SqlParameterValue> bind)
    {
        var column = Require(schema.GlobalRecordId);
        var name = parameters.Add(n => bind(n, column));

        AppendQualified(sql, alias, column).Append(" = ").Append(name).Append(" AND DATALENGTH(");
        AppendQualified(sql, alias, column).Append(") = DATALENGTH(").Append(name).Append(')');
    }

    // [base] b
    private static void AppendBaseTable(StringBuilder sql, FormSchema schema) =>
        sql.Append(SqlIdentifier.Quote(schema.BaseTable)).Append(' ').Append(BaseAlias);

    // b.[column]
    private static void AppendBaseColumn(StringBuilder sql, ColumnSchema column) =>
        AppendQualified(sql, BaseAlias, column);

    // ORDER BY b.[UniqueKey]
    private static void AppendOrderByUniqueKey(StringBuilder sql, FormSchema schema)
    {
        sql.Append(" ORDER BY ");
        AppendBaseColumn(sql, Require(schema.UniqueKey));
    }

    // alias.[column] AS [cN]
    private static void AppendProjection(StringBuilder sql, string alias, ColumnSchema column, int ordinal) =>
        AppendQualified(sql, alias, column).Append(" AS ").Append(ColumnAlias(ordinal));

    // alias.[column], or [column] alone when the statement uses no alias.
    private static StringBuilder AppendQualified(StringBuilder sql, string? alias, ColumnSchema column)
    {
        if (alias is not null)
        {
            sql.Append(alias).Append('.');
        }

        return sql.Append(SqlIdentifier.Quote(column));
    }

    private static void AddIfPresent(List<(ColumnSchema Column, object Value)> assignments, ColumnSchema? column, object value)
    {
        if (column is not null)
        {
            assignments.Add((column, value));
        }
    }

    private static TableSchema RequireTable(PageTableSchema page) =>
        page.Table ?? throw new ArgumentException(PageWithoutTableMessage, nameof(page));

    /// <summary>
    /// Returns a page table's record key column, the reader-built <c>GlobalRecordId</c> that joins it to the base
    /// table. Record reads, record writes and <c>SearchSqlBuilder</c> all find the key here.
    /// </summary>
    /// <param name="table">The page table from <see cref="PageTableSchema.Table"/>.</param>
    /// <returns>The key column with its catalogue spelling.</returns>
    /// <exception cref="InvalidOperationException">The page table lacks its record key column.</exception>
    internal static ColumnSchema RequirePageKey(TableSchema table) =>
        table.FindColumn(PageKeyColumnName) ?? throw new InvalidOperationException(MissingPageKeyMessage);

    // Every column written to a page row must be the reader-built column of that very page table, at most once, and
    // never the key column, which the statement names itself.
    private static void EnsurePageColumns<T>(
        PageTableSchema page,
        TableSchema table,
        ColumnSchema key,
        IReadOnlyList<T> entries,
        Func<T, ProjectedColumn?> columnOf,
        string paramName)
    {
        var seen = new HashSet<ColumnSchema>(entries.Count + 1, ReferenceEqualityComparer.Instance) { key };
        foreach (var entry in entries)
        {
            if (columnOf(entry) is not { } column)
            {
                throw new ArgumentException(NullEntryMessage, paramName);
            }

            if (column.Field.Page.PageId != page.Page.PageId)
            {
                throw new ArgumentException(ForeignColumnMessage, paramName);
            }

            EnsureColumnOf(table, column.Column);
            if (!seen.Add(column.Column))
            {
                throw new ArgumentException(DuplicateColumnMessage, paramName);
            }
        }
    }

    // A column belongs to a table only when the table's own lookup returns that very instance.
    private static void EnsureColumnOf(TableSchema table, ColumnSchema column)
    {
        if (!ReferenceEquals(table.FindColumn(column.Name), column))
        {
            throw new ArgumentException(ForeignColumnMessage, nameof(column));
        }
    }

    // The submitted value of a field: the exact canonical key first, then a key equal to it ignoring case, so the
    // lookup does not depend on the dictionary's comparer; FieldValue.Null when the field was not submitted.
    private static FieldValue SubmittedValue(IReadOnlyDictionary<string, FieldValue> values, string name)
    {
        if (values.TryGetValue(name, out var exact))
        {
            return exact;
        }

        FieldValue? match = null;
        foreach (var (key, value) in values)
        {
            if (!string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (match is not null)
            {
                throw new ArgumentException(AmbiguousValueMessage, nameof(values));
            }

            match = value;
        }

        return match ?? FieldValue.Null;
    }

    // The text and layout of a record SELECT before its WHERE and ORDER BY clauses are appended.
    private sealed class SelectCore(
        StringBuilder sql,
        IReadOnlyList<ProjectedColumn> columns,
        IReadOnlyList<PagePresence> pages,
        bool hasIntrinsicCoordinates)
    {
        public StringBuilder Sql { get; } = sql;

        public RecordSelect Finish(PositionalParameters parameters) =>
            new(parameters.ToStatement(Sql.ToString()), columns, pages, hasIntrinsicCoordinates);
    }
}
