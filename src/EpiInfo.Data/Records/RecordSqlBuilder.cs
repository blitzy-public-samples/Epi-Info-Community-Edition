using System.Data;
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
/// the row: set for <see cref="RecordSqlBuilder.RecordAt"/>, <see cref="RecordSqlBuilder.RecordById"/> and
/// <see cref="RecordSqlBuilder.RecordSnapshot"/>, clear for <see cref="RecordSqlBuilder.RecordByUniqueKey"/>.
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
/// (<c>b</c>, <c>i</c>, <c>p{i}</c>, <c>[c{n}]</c>) and SQL keywords are fixed literals. Result columns are aliased by
/// ordinal, so two tables with a column of the same name never collide.
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
/// <see cref="RecordByUniqueKey"/>, <see cref="RecordSnapshot"/>, <see cref="UpdateBaseStamp"/> and
/// <see cref="SoftDelete"/>, compares under the column's collation and, because of that padding, also requires equal
/// byte lengths (<c>DATALENGTH</c>): an id longer than the column, or one that differs from a stored id only by
/// trailing spaces, finds no row instead of failing to bind or matching that stored id, while letter case still
/// follows the collation. The Find exclusion (<see cref="AppendGlobalRecordIdExclusion"/>) negates that same match, so
/// such an id excludes no row. The page statements (<see cref="UpdatePageColumns"/>, <see cref="InsertPageKey"/>)
/// bind the stored id the locked read returned, and the page join of a record read compares two stored columns.
/// Neither meets a request's spelling, so the page <c>UPDATE</c> and the join keep plain equality. The by-id reads
/// return the id as the base row stores it, and every later statement binds that stored spelling, never the request's.
/// </para>
/// <para>
/// <b>Aliases.</b> Reads alias the base table <c>b</c> and each page <c>p{i}</c>, where <c>i</c> is the page's index in
/// <see cref="FormSchema.Pages"/>, so a column has the same reference in every statement. <see cref="RecordAt"/>,
/// <see cref="RecordById"/> and <see cref="SetPositionLookup"/> read their one base row in a derived table aliased
/// <c>b</c>, which keeps the base system columns under their catalogue names, so each outer query references the base
/// row exactly as the other reads do. The position and count of
/// <see cref="RecordAt"/>, <see cref="RecordById"/>, <see cref="RecordSnapshot"/> and <see cref="SetPositionLookup"/>
/// are subqueries that read the base table again as <c>i</c> and count against the outer row <c>b</c>. Statements that
/// write use no alias.
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
/// scan or in the snapshot read, which scans the base table for the intrinsic coordinates under <c>HOLDLOCK</c> while
/// the writer holds its own base row exclusively: the new key of a create, the stamped row of an update. Two writes
/// waiting there for each other's row would deadlock, so a create and an update each take the form's write lock
/// (<see cref="AcquireFormWriteLock"/>) first, exclusive for a create and shared for an update. No create then runs
/// beside another create or an update of the same form, which removes every cycle a create can close, while updates
/// still run together and serialise per record on the locked read. Two updates of different records can therefore
/// still wait for each other's stamped row in their snapshot reads where the locked scan keeps only the matching row
/// locked; a deadlock that ends such a wait (1205), or one with a writer outside the application, is reported by the
/// repository as not written, and so is a wait for the form's write lock that ends without the lock.
/// </para>
/// <para>
/// <b>Read hint.</b> The database runs locking <c>READ COMMITTED</c>, with neither read-committed snapshot nor
/// snapshot isolation, and its settings are never changed. There a scan releases each shared lock as it moves on, so
/// a concurrent write committed mid-statement, or between two statements, could leave a record's <c>RecStatus</c>
/// and the rows counted for its position or for a total from different states. Every read that pairs rows with
/// coordinates or a total computed from the base table is therefore one statement in which every reference to the
/// base table carries <c>WITH (HOLDLOCK)</c>: <see cref="RecordAt"/>, <see cref="RecordById"/>,
/// <see cref="RecordSnapshot"/> and <see cref="SetPositionLookup"/> here, and <c>SearchSqlBuilder.Search</c> and
/// <c>SearchSqlBuilder.ListPage</c>.
/// Every shared and key-range lock that read takes is held until the statement completes, so no base row it read can
/// change, and no row can be inserted among them, before it returns, and the rows, their status, positions, counts
/// and totals describe one committed state. The reads other than <see cref="RecordSnapshot"/> run outside any
/// transaction, so the hint lasts one statement. <see cref="RecordSnapshot"/> runs inside a write transaction, where
/// the locks last until the commit the repository sends immediately after it, so no later statement waits on them.
/// In every read except <c>SearchSqlBuilder.ListPage</c> only the base table carries the hint: held page-row locks
/// could deadlock with an update that holds exclusive page-row locks. Every application write locks the base row
/// exclusively before any of its page rows (an update of a base table with neither last-save column, which no seeded
/// form has, takes only the update lock first), so on the seeded forms a page row cannot change through the
/// application while one of these reads holds its base row, and each of them reads that row once. These reads fix no
/// join order, so a page row a plan reads before it holds that row's base row can still change before the base row
/// is held, as <c>SearchSqlBuilder.Search</c>'s remarks state. <c>SearchSqlBuilder.ListPage</c> is the exception: it
/// holds its page tables too, with or without a grid filter. With one, its set joins the pages and is read twice in one
/// statement, once for the total and once for the page, so a page row changed between the two reads without its base
/// row, by a writer outside the application or by an update of a base table with neither last-save column, would make
/// them describe different sets; without one, the set reads the base table alone and each page row is read once, for a
/// fetched row only. Its plan hints <c>OPTION (FORCE ORDER, LOOP JOIN)</c> make held base rows the outer input of every
/// page join, so it reads each page row only once it holds that row's base row, the order every application write locks
/// them in. That removes the lock-order inversion between a held page row and its base row, so the list and an
/// application write never wait for each other through the rows of one record; an application write that inserts a page
/// row (a create, or an update that repairs a missing page row) can still deadlock with it through a key range the list
/// locked for another record. Its remarks state every deadlock that order leaves possible and how each is reported.
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
    /// operation: <see cref="RecordByUniqueKey"/>, <see cref="RecordSnapshot"/>, <see cref="UpdateBaseStamp"/>,
    /// <see cref="UpdatePageColumns"/>, <see cref="InsertPageKey"/> and <see cref="SoftDelete"/>.
    /// </summary>
    internal const int BaseRowGlobalRecordIdOrdinal = 2;

    /// <summary>
    /// The ordinal of the record's intrinsic position, a <c>bigint</c>, in a <see cref="RecordSelect"/> whose
    /// <see cref="RecordSelect.HasIntrinsicCoordinates"/> is set.
    /// </summary>
    internal const int RecordIntrinsicPositionOrdinal = 3;

    /// <summary>
    /// The native type of the intrinsic position at <see cref="RecordIntrinsicPositionOrdinal"/>: <c>bigint</c>, the
    /// type of the <c>COUNT_BIG(*)</c> in each branch of its <c>CASE</c>.
    /// </summary>
    internal const DbType RecordIntrinsicPositionType = DbType.Int64;

    /// <summary>
    /// The ordinal of the record's intrinsic count, a <c>bigint</c>, in a <see cref="RecordSelect"/> whose
    /// <see cref="RecordSelect.HasIntrinsicCoordinates"/> is set.
    /// </summary>
    internal const int RecordIntrinsicCountOrdinal = 4;

    /// <summary>
    /// The native type of the intrinsic count at <see cref="RecordIntrinsicCountOrdinal"/>: <c>bigint</c>, the type of
    /// the <c>COUNT_BIG(*)</c> in each branch of its <c>CASE</c>.
    /// </summary>
    internal const DbType RecordIntrinsicCountType = DbType.Int64;

    /// <summary>
    /// The ordinal of the record's position in the selected set, a <c>bigint</c>, in <see cref="SetPositionLookup"/>.
    /// It is a position only for a row inside that set.
    /// </summary>
    internal const int SetPositionOrdinal = 3;

    /// <summary>
    /// The native type of the selected-set position at <see cref="SetPositionOrdinal"/>: <c>bigint</c>, the type of
    /// its <c>COUNT_BIG(*)</c>.
    /// </summary>
    internal const DbType SetPositionType = DbType.Int64;

    /// <summary>The ordinal of the selected-set count, a <c>bigint</c>, in <see cref="SetPositionLookup"/>.</summary>
    internal const int SetCountOrdinal = 4;

    /// <summary>
    /// The native type of the selected-set count at <see cref="SetCountOrdinal"/>: <c>bigint</c>, the type of its
    /// <c>COUNT_BIG(*)</c>.
    /// </summary>
    internal const DbType SetCountType = DbType.Int64;

    /// <summary>
    /// The native type of the one value <see cref="Count"/> returns: <c>bigint</c>, the type of <c>COUNT_BIG(*)</c>.
    /// </summary>
    internal const DbType CountType = DbType.Int64;

    /// <summary>
    /// The most columns one page <c>UPDATE</c> sets, the chunk size legacy Epi Info uses
    /// (<c>Epi.Core/Data/Services/CollectedDataProvider.cs:1274-1340</c>).
    /// </summary>
    internal const int MaxColumnsPerUpdate = 100;

    /// <summary>
    /// The result of <see cref="AcquireFormWriteLock"/> when the lock was not granted before its wait ended: the
    /// <c>sp_getapplock</c> return code for a timed-out request. Results of 0 and above mean the lock was granted.
    /// </summary>
    internal const int FormWriteLockTimedOut = -1;

    /// <summary>
    /// The result of <see cref="AcquireFormWriteLock"/> when SQL Server chose the request as a deadlock victim: the
    /// <c>sp_getapplock</c> return code for that case.
    /// </summary>
    internal const int FormWriteLockDeadlockVictim = -3;

    /// <summary>
    /// The native type of the one value <see cref="AcquireFormWriteLock"/> returns: <c>int</c>, the type of the
    /// <c>sp_getapplock</c> return code it selects.
    /// </summary>
    internal const DbType FormWriteLockResultType = DbType.Int32;

    /// <summary>
    /// The native type of the one value each counted <c>UPDATE</c> returns (<see cref="UpdatePageColumns"/>,
    /// <see cref="UpdateBaseStamp"/>, <see cref="SoftDelete"/>): <c>int</c>, the type of <c>@@ROWCOUNT</c>.
    /// </summary>
    internal const DbType RowCountType = DbType.Int32;

    // The number of base columns every record SELECT starts with: GlobalRecordId, UniqueKey, RecStatus.
    private const int RecordBaseColumnCount = 3;

    // The alias of the base table inside the held counts that give a record's intrinsic or selected-set position and
    // count, and the table hint every base-table reference of a held read carries (class remarks, "Read hint").
    // Neither reaches SQL text from a caller.
    private const string IntrinsicSourceAlias = "i";
    private const string HoldLockHint = " WITH (HOLDLOCK)";

    // The values AcquireFormWriteLock binds: the fixed prefix of every form's lock resource, which the base table's
    // reader-built name completes; the two lock modes; the transaction as the lock's owner; and the lock timeout -1,
    // which waits without a limit of its own, so the command timeout bounds the wait. Each is bound as a parameter, so
    // none reaches SQL text.
    private const string FormWriteLockResourcePrefix = "EpiInfo.Web.RecordWrites/";
    private const string ExclusiveLockMode = "Exclusive";
    private const string SharedLockMode = "Shared";
    private const string TransactionLockOwner = "Transaction";
    private const int LockWaitWithoutLimit = -1;

    // Catalogue lookup key of a page table's key column, matched through TableSchema.FindColumn, which returns the
    // reader-built column. The literal never reaches SQL text.
    private const string PageKeyColumnName = "GlobalRecordId";

    // The tail of every UPDATE whose row count the repository decides on (UpdatePageColumns, UpdateBaseStamp and
    // SoftDelete): the statement returns its own count as one row. @@ROWCOUNT holds the count of the UPDATE just run
    // whatever the session's NOCOUNT setting, whereas the provider's affected-row count is -1 under NOCOUNT ON, which
    // a server's 'user options' can make every session's default.
    private const string RowCountSelect = "; SELECT @@ROWCOUNT";

    // Fixed messages: the redaction policy keeps identifiers, values and ids out of exception text.
    private const string MissingSystemColumnMessage = "The form's base table lacks a required system column.";
    private const string UnknownFieldMessage = "The field is not a column of the form.";
    private const string MissingPageKeyMessage = "A page table lacks its record key column.";
    private const string UnknownPageMessage = "The page is not a page of the form.";
    private const string PageWithoutTableMessage = "The page has no table to write.";
    private const string ForeignColumnMessage = "The column is not a column of the page table.";
    private const string DuplicateColumnMessage = "A column cannot be written twice in one statement.";
    private const string AmbiguousValueMessage = "Two submitted values name the same field.";
    private const string ReadOnlyFieldMessage = "A read-only field is never written.";
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
    /// that predicate on a statement's base row in the data layer, and the counts of <see cref="SetPositionLookup"/>
    /// test the same status on their own base-table reference; the rule itself is
    /// <see cref="RecordQueryRules.RequiredRecStatus"/>. Every piece of the text is fixed here or quoted from the
    /// reader-built column, and the caller supplies none of it.
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

    /// <summary>
    /// Appends the exclusion of a request-supplied id, the open record a Find leaves out:
    /// <c>NOT (b.[GlobalRecordId] = @pN AND DATALENGTH(b.[GlobalRecordId]) = DATALENGTH(@pN))</c>. It negates the exact
    /// match every lookup by id renders (class remarks, "Record identity"), so it excludes exactly the rows a lookup by
    /// that id would find: a spelling that differs from a stored id only by trailing spaces, or one longer than the
    /// column, excludes nothing, while letter case follows the column's collation. The id is bound at full length with
    /// <see cref="ColumnValueConverter.Text"/>, like every request id the base table has not confirmed. Every piece of
    /// the text is fixed here or quoted from the reader-built column, and the caller supplies none of it.
    /// </summary>
    /// <param name="sql">
    /// The statement text being built, whose base table, or the derived set that keeps its columns, carries the alias
    /// <see cref="BaseAlias"/>, and whose <c>WHERE</c> or <c>AND</c> keyword the caller has already written.
    /// </param>
    /// <param name="schema">The form's catalogue view.</param>
    /// <param name="parameters">The statement's parameters; the id is added to them.</param>
    /// <param name="globalRecordId">The id from the request whose record is left out.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// The base table has no <c>GlobalRecordId</c> column. Nothing is appended and no parameter is added.
    /// </exception>
    internal static void AppendGlobalRecordIdExclusion(
        StringBuilder sql,
        FormSchema schema,
        PositionalParameters parameters,
        string globalRecordId)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(globalRecordId);

        // Built apart, so a missing column leaves the statement text unchanged.
        var match = new StringBuilder();
        AppendGlobalRecordIdMatch(
            match,
            schema,
            parameters,
            BaseAlias,
            (n, _) => ColumnValueConverter.Text(n, globalRecordId));
        sql.Append("NOT (").Append(match).Append(')');
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
    /// Places a record in the caller's selected set for <c>IDataStore.GetSetPositionAsync</c>, in one statement that
    /// reads the base table only, so a base row that lacks a page row is still placed: the record's base row by id,
    /// with its position in the selected set and the selected-set count computed by the same statement. The base table
    /// does not constrain <c>GlobalRecordId</c> to be unique, so a derived table takes the exact match with the lowest
    /// <c>UniqueKey</c>; the statement returns at most that one row, whose coordinates alone are computed.
    /// </summary>
    /// <remarks>
    /// <para>For the active set the statement has this shape:</para>
    /// <code>
    /// SELECT b.[UniqueKey] AS [c0], b.[RecStatus] AS [c1], b.[GlobalRecordId] AS [c2],
    ///   (SELECT COUNT_BIG(*) FROM [base] i WITH (HOLDLOCK)
    ///     WHERE i.[RecStatus] = @p0 AND i.[UniqueKey] &lt;= b.[UniqueKey]) AS [c3],
    ///   (SELECT COUNT_BIG(*) FROM [base] i WITH (HOLDLOCK) WHERE i.[RecStatus] = @p0) AS [c4]
    /// FROM (SELECT TOP (1) b.[GlobalRecordId], b.[UniqueKey], b.[RecStatus] FROM [base] b WITH (HOLDLOCK)
    ///   WHERE b.[GlobalRecordId] = @p1 AND DATALENGTH(b.[GlobalRecordId]) = DATALENGTH(@p1)
    ///   ORDER BY b.[UniqueKey]) b
    /// </code>
    /// <para>
    /// <c>@p0</c> is the status <see cref="RecordQueryRules.RequiredRecStatus"/> gives, bound with the <c>RecStatus</c>
    /// column's own type. With every row selected no status is bound, the counts have no <c>RecStatus</c> predicate,
    /// and the id is <c>@p0</c>. Either way <c>[c3]</c> is <c>COUNT_BIG(*)</c> of the selected rows whose key is at
    /// most the row's own, the legacy formula <c>Count(*) ... Where UniqueKey &lt;= k</c>
    /// (<c>Epi.Enter.CheckCodeEngine/RunTimeView.cs:673-694</c>), because <c>UniqueKey</c> is unique, and <c>[c4]</c>
    /// is the selected-set count. Both are computed for the one row the id selects, not for every base row and not
    /// for any other row carrying the id. The derived table has no <c>RecStatus</c> predicate, so the row is chosen
    /// whatever its status, and for a row outside the selected set <c>[c3]</c> is no position: the caller tests the
    /// <c>RecStatus</c> the same row returns with <see cref="RecordQueryRules.IsInSelectedSet"/> and reports none.
    /// Every base-table reference carries the hint described in the class remarks, so the status, the position and the
    /// count describe one committed state even under a concurrent soft delete.
    /// </para>
    /// </remarks>
    /// <param name="schema">The form's catalogue view.</param>
    /// <param name="globalRecordId">The id from the request, bound at full length and matched exactly.</param>
    /// <param name="includeDeleted">Whether the selected set holds every row rather than the active set.</param>
    /// <returns>
    /// A statement returning <c>UniqueKey</c> at <see cref="BaseRowUniqueKeyOrdinal"/>, <c>RecStatus</c> at
    /// <see cref="BaseRowRecStatusOrdinal"/>, the stored <c>GlobalRecordId</c> at
    /// <see cref="BaseRowGlobalRecordIdOrdinal"/>, and the selected-set position and count, both <c>bigint</c>, at
    /// <see cref="SetPositionOrdinal"/> and <see cref="SetCountOrdinal"/>; at most one row, the lowest-keyed exact
    /// match; no row for an unknown id, including one that differs from a stored id only by trailing spaces or is
    /// longer than the column.
    /// </returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="InvalidOperationException">The base table lacks a required system column.</exception>
    internal static SqlStatement SetPositionLookup(FormSchema schema, string globalRecordId, bool includeDeleted)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(globalRecordId);

        var uniqueKeyColumn = Require(schema.UniqueKey);
        var recStatusColumn = Require(schema.RecStatus);
        var globalRecordIdColumn = Require(schema.GlobalRecordId);

        // Every system column is required before the status is bound, so a missing one binds none.
        var parameters = new PositionalParameters();
        var status = RecordQueryRules.RequiredRecStatus(includeDeleted) is { } recStatus
            ? parameters.Add(n => ColumnValueConverter.ToSystemParameter(n, recStatusColumn, recStatus))
            : null;
        var sql = new StringBuilder("SELECT ");
        AppendProjection(sql, BaseAlias, uniqueKeyColumn, BaseRowUniqueKeyOrdinal);
        sql.Append(", ");
        AppendProjection(sql, BaseAlias, recStatusColumn, BaseRowRecStatusOrdinal);
        sql.Append(", ");
        AppendProjection(sql, BaseAlias, globalRecordIdColumn, BaseRowGlobalRecordIdOrdinal);
        sql.Append(", ");
        AppendHeldCount(sql, schema, status, throughRow: true);
        sql.Append(" AS ").Append(ColumnAlias(SetPositionOrdinal)).Append(", ");
        AppendHeldCount(sql, schema, status, throughRow: false);
        sql.Append(" AS ").Append(ColumnAlias(SetCountOrdinal));
        sql.Append(" FROM ");
        AppendBaseRowById(sql, schema, globalRecordId, parameters);

        return parameters.ToStatement(sql.ToString());
    }

    /// <summary>
    /// Reads the record at a 1-based position of the selected set in <c>UniqueKey</c> order, with its intrinsic
    /// position and count from the same statement: the record <c>SELECT</c> over a derived table that reads the base
    /// keys of the selected set, <c>[WHERE b.[RecStatus] = @p1]</c>, <c>ORDER BY b.[UniqueKey] OFFSET @pN ROWS FETCH
    /// NEXT 1 ROWS ONLY</c>, before any page table is joined. The offset is a <c>bigint</c>, so no position can wrap at
    /// 32 bits.
    /// </summary>
    /// <remarks>
    /// <para>The statement has this shape:</para>
    /// <code>
    /// SELECT b.[GlobalRecordId] AS [c0], b.[UniqueKey] AS [c1], b.[RecStatus] AS [c2],
    ///   CASE WHEN b.[RecStatus] = @p0
    ///     THEN (SELECT COUNT_BIG(*) FROM [base] i WITH (HOLDLOCK)
    ///       WHERE i.[RecStatus] = @p0 AND i.[UniqueKey] &lt;= b.[UniqueKey])
    ///     ELSE (SELECT COUNT_BIG(*) FROM [base] i WITH (HOLDLOCK)
    ///       WHERE i.[UniqueKey] &lt;= b.[UniqueKey]) END AS [c3],
    ///   CASE WHEN b.[RecStatus] = @p0
    ///     THEN (SELECT COUNT_BIG(*) FROM [base] i WITH (HOLDLOCK) WHERE i.[RecStatus] = @p0)
    ///     ELSE (SELECT COUNT_BIG(*) FROM [base] i WITH (HOLDLOCK)) END AS [c4],
    ///   p0.[GlobalRecordId] AS [c5], ..., p0.[col] AS [cN], ...
    /// FROM (SELECT b.[GlobalRecordId], b.[UniqueKey], b.[RecStatus] FROM [base] b WITH (HOLDLOCK)
    ///   [WHERE b.[RecStatus] = @p1] ORDER BY b.[UniqueKey] OFFSET @p2 ROWS FETCH NEXT 1 ROWS ONLY) b
    /// LEFT JOIN [page] p0 ON p0.[GlobalRecordId] = b.[GlobalRecordId] ...
    /// </code>
    /// <para>
    /// <c>@p0</c> is <see cref="RecordQueryRules.ActiveRecStatus"/>, bound once with the <c>RecStatus</c> column's own
    /// type. The derived table fetches the one base row at the offset, so only that row's page rows are read. Its
    /// intrinsic position and count are computed for that row alone, over the set
    /// <see cref="RecordQueryRules.IntrinsicIncludesDeleted"/> gives its status, whichever set
    /// <paramref name="includeDeleted"/> names: for an active row, <c>COUNT_BIG(*)</c> of the active rows whose key is
    /// at most its own and of every active row; for any other status, the same counts over every row. Those are the
    /// legacy formula <c>Count(*) ... Where UniqueKey &lt;= k</c> over the record's own set, because <c>UniqueKey</c>
    /// is unique. Every base-table reference carries the hint described in the class remarks, and no page table
    /// does.
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
        var core = RecordSelectCore(
            schema,
            dataFields,
            intrinsicParameters: parameters,
            sql => AppendBaseRowAt(sql, schema, includeDeleted, offset, parameters));

        return core.Finish(parameters);
    }

    /// <summary>
    /// Reads a record by id, deleted records included, with its intrinsic position and count from the same statement:
    /// the record <c>SELECT</c> with the intrinsic counts of <see cref="RecordAt"/>, over a derived table that reads
    /// the one base row the id selects, left-joined to each page table. The base table does not constrain
    /// <c>GlobalRecordId</c> to be unique, so the derived table takes the exact match with the lowest
    /// <c>UniqueKey</c>; the statement returns at most that one row, and computes the coordinates and reads the page
    /// rows of that row alone. The caller binds the stored id it returns at <see cref="RecordGlobalRecordIdOrdinal"/>,
    /// not the request's, in any later statement.
    /// </summary>
    /// <remarks>
    /// <para>The statement has this shape:</para>
    /// <code>
    /// SELECT b.[GlobalRecordId] AS [c0], b.[UniqueKey] AS [c1], b.[RecStatus] AS [c2],
    ///   CASE WHEN b.[RecStatus] = @p0 THEN ... END AS [c3], CASE WHEN b.[RecStatus] = @p0 THEN ... END AS [c4],
    ///   p0.[GlobalRecordId] AS [c5], ..., p0.[col] AS [cN], ...
    /// FROM (SELECT TOP (1) b.[GlobalRecordId], b.[UniqueKey], b.[RecStatus] FROM [base] b WITH (HOLDLOCK)
    ///   WHERE b.[GlobalRecordId] = @p1 AND DATALENGTH(b.[GlobalRecordId]) = DATALENGTH(@p1)
    ///   ORDER BY b.[UniqueKey]) b
    /// LEFT JOIN [page] p0 ON p0.[GlobalRecordId] = b.[GlobalRecordId] ...
    /// </code>
    /// <para>
    /// <c>@p0</c> is <see cref="RecordQueryRules.ActiveRecStatus"/>, and <c>[c3]</c> and <c>[c4]</c> are the held
    /// intrinsic counts <see cref="RecordAt"/> describes, computed for the selected row alone. Every base-table
    /// reference carries the hint described in the class remarks, and no page table does.
    /// </para>
    /// </remarks>
    /// <param name="schema">The form's catalogue view.</param>
    /// <param name="dataFields">The data fields to project, in data-field order.</param>
    /// <param name="globalRecordId">The id from the request, bound at full length and matched exactly.</param>
    /// <returns>
    /// The statement and its result layout, with <see cref="RecordSelect.HasIntrinsicCoordinates"/> set; at most one
    /// row, the lowest-keyed exact match; no row for an unknown id, including one that differs from a stored id only
    /// by trailing spaces or is longer than the column.
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
        var core = RecordSelectCore(
            schema,
            dataFields,
            intrinsicParameters: parameters,
            sql => AppendBaseRowById(sql, schema, globalRecordId, parameters));

        return core.Finish(parameters);
    }

    /// <summary>
    /// Reads a record whose base row the caller has already located, by its clustered key and id together: the record
    /// <c>SELECT</c>, then <c>WHERE b.[UniqueKey] = @p0 AND b.[GlobalRecordId] = @p1 AND
    /// DATALENGTH(b.[GlobalRecordId]) = DATALENGTH(@p1)</c>. An update's transaction uses it for the page-row read, so
    /// the base row is reached by a seek rather than a scan; the response snapshot, which needs the intrinsic
    /// coordinates too, is <see cref="RecordSnapshot"/>.
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

        var core = RecordSelectCore(
            schema,
            dataFields,
            intrinsicParameters: null,
            sql => AppendBaseTable(sql, schema));
        var parameters = new PositionalParameters();
        core.Sql.Append(" WHERE ");
        AppendBaseRowKey(core.Sql, schema, parameters, uniqueKey, globalRecordId, BaseAlias);

        return core.Finish(parameters);
    }

    /// <summary>
    /// Reads the response snapshot of a write inside its transaction, after every write statement and before the
    /// commit: the record's base and page rows and its intrinsic position and count, all from one statement. It is the
    /// record <c>SELECT</c> of <see cref="RecordById"/>, addressed as <see cref="RecordByUniqueKey"/> addresses the
    /// row: <c>WHERE b.[UniqueKey] = @p1 AND b.[GlobalRecordId] = @p2 AND DATALENGTH(b.[GlobalRecordId]) =
    /// DATALENGTH(@p2)</c>.
    /// </summary>
    /// <remarks>
    /// The intrinsic counts are computed for the written row alone, and the count reads the whole base table. Every
    /// base-table reference carries the hint described in the class remarks, so the row, its position and its count
    /// describe one committed state, including the transaction's own writes. Inside the write transaction the hint's
    /// locks last until its commit, which the repository sends immediately after this read; no later statement of the
    /// transaction waits on them. The form's write lock the transaction took first (<see cref="AcquireFormWriteLock"/>)
    /// keeps a create's read from meeting the uncommitted base row of another create or of an update, and an update's
    /// read from meeting a create's.
    /// </remarks>
    /// <param name="schema">The form's catalogue view.</param>
    /// <param name="dataFields">The data fields to project, in data-field order.</param>
    /// <param name="uniqueKey">The written base row's <c>UniqueKey</c>.</param>
    /// <param name="globalRecordId">
    /// The written base row's <c>GlobalRecordId</c>, as stored: the id the insert wrote, or the value
    /// <see cref="LockBase"/> returned at <see cref="BaseRowGlobalRecordIdOrdinal"/>. A spelling of another length,
    /// such as one with trailing spaces, matches no row.
    /// </param>
    /// <returns>
    /// The statement and its result layout, with <see cref="RecordSelect.HasIntrinsicCoordinates"/> set.
    /// </returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">
    /// A data field is not a column of the form, the list holds a null entry, or the id does not fit the column.
    /// </exception>
    /// <exception cref="InvalidOperationException">A table lacks a required system or key column.</exception>
    internal static RecordSelect RecordSnapshot(
        FormSchema schema,
        IReadOnlyList<DataFieldMetadata> dataFields,
        int uniqueKey,
        string globalRecordId)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(dataFields);
        ArgumentNullException.ThrowIfNull(globalRecordId);

        var parameters = new PositionalParameters();
        var core = RecordSelectCore(
            schema,
            dataFields,
            intrinsicParameters: parameters,
            sql => AppendHeldBaseTable(sql, schema));
        core.Sql.Append(" WHERE ");
        AppendBaseRowKey(core.Sql, schema, parameters, uniqueKey, globalRecordId, BaseAlias);

        return core.Finish(parameters);
    }

    /// <summary>
    /// Takes the form's write lock inside a write transaction: <c>DECLARE @result int; EXEC @result =
    /// sys.sp_getapplock @Resource = @p0, @LockMode = @p1, @LockOwner = @p2, @LockTimeout = @p3; SELECT @result</c>,
    /// an application lock whose resource is a fixed prefix followed by the form's base-table name, owned by the
    /// transaction, so its commit or rollback releases it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A create takes the lock exclusive and an update takes it shared, each before any statement of its transaction
    /// that holds a lock on a record table. The response snapshot (<see cref="RecordSnapshot"/>) reads the whole base
    /// table under <c>HOLDLOCK</c> after the write's own exclusive lock on its base row, the new key of a create or the
    /// stamped row of an update. Two such writes in progress together would each wait in that read for the other's
    /// uncommitted row and end in a deadlock (1205). Under the lock a create of the form runs beside no other create
    /// and no update of the form, so that read meets no uncommitted row of another create or update; updates still run
    /// beside each other and serialise per record on <see cref="LockBase"/>. A soft delete takes no lock: between its
    /// <see cref="LockBase"/> read and its UPDATE it sends at most the permission read, which touches no record table,
    /// and it sends no statement after its exclusive lock on its base row, so while it holds a lock on that row it
    /// waits on no other writer and closes no cycle. Reads take no lock.
    /// </para>
    /// <para>
    /// The statement names no table: the resource, the mode, the owner and the lock timeout are all positional
    /// parameters. The resource is the reader-built base-table name, at most 128 characters, after the fixed prefix,
    /// so it stays within the 255 characters <c>sp_getapplock</c> takes, and two forms never share a lock. The lock
    /// timeout is -1, which waits without a limit of its own, so the command timeout bounds the wait and a wait that
    /// outlasts it fails as a command timeout.
    /// </para>
    /// </remarks>
    /// <param name="schema">The form's catalogue view, whose base table names the resource.</param>
    /// <param name="exclusive">
    /// <see langword="true"/> for the exclusive lock of a create; <see langword="false"/> for the shared lock of an
    /// update.
    /// </param>
    /// <returns>
    /// A statement returning the <c>sp_getapplock</c> return code as one <c>int</c>: 0 or 1 when the lock is granted,
    /// <see cref="FormWriteLockTimedOut"/> or <see cref="FormWriteLockDeadlockVictim"/> when SQL Server ended the
    /// wait without granting it, and another negative code when it refused the request.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="schema"/> is null.</exception>
    internal static SqlStatement AcquireFormWriteLock(FormSchema schema, bool exclusive)
    {
        ArgumentNullException.ThrowIfNull(schema);

        var parameters = new PositionalParameters();
        var resource = parameters.Add(n =>
            ColumnValueConverter.Text(n, FormWriteLockResourcePrefix + schema.BaseTable.Name));
        var mode = parameters.Add(n => ColumnValueConverter.Text(n, exclusive ? ExclusiveLockMode : SharedLockMode));
        var owner = parameters.Add(n => ColumnValueConverter.Text(n, TransactionLockOwner));
        var timeout = parameters.Add(n => ColumnValueConverter.Int32(n, LockWaitWithoutLimit));
        var sql = new StringBuilder("DECLARE @result int; EXEC @result = sys.sp_getapplock @Resource = ")
            .Append(resource)
            .Append(", @LockMode = ").Append(mode)
            .Append(", @LockOwner = ").Append(owner)
            .Append(", @LockTimeout = ").Append(timeout)
            .Append("; SELECT @result");

        return parameters.ToStatement(sql.ToString());
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
    /// (@p0, @p1, ...)</c>, naming the key and every writable data-field column given.
    /// </summary>
    /// <remarks>
    /// The caller passes every data-field column of the page. The columns come from the form's data-field set
    /// (<see cref="FormDefinitionFactory.GetDataFields"/>), which holds one field per exact canonical name, so a
    /// submitted value binds exactly one column of the record. A writable column without a submitted value is written
    /// <c>NULL</c>. A read-only data field (<c>metaFields.IsReadOnly</c>, such as the seeded <c>StateIDNumber</c>,
    /// <c>CDCIDNumber</c>, <c>ReportedToCDCDate</c> and <c>OnsetWeek</c>) is never written from the client, so its
    /// column is left out of the statement, as are page-table columns that are not data-field columns, such as the
    /// seeded <c>Yes/No</c> fields on <c>tinyint</c> columns; each keeps its <c>NULL</c> default.
    /// </remarks>
    /// <param name="page">The page whose table receives the row.</param>
    /// <param name="globalRecordId">The new record's id, as written to the base row.</param>
    /// <param name="pageColumns">The page's data-field columns, from <see cref="ResolveDataColumn"/>.</param>
    /// <param name="values">
    /// The submitted values keyed by canonical field name. A column takes only the entry whose key equals its field's
    /// canonical name ordinally, whatever comparer the dictionary uses, so a key that differs only by case is another
    /// field's and never this column's value. It may hold the values of other pages' fields, which this page ignores,
    /// and must hold none for a read-only field.
    /// </param>
    /// <returns>The insert statement.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">
    /// The page has no table; the id is blank or does not fit the key column; a column is not one of the page table's,
    /// is given twice or is null; <paramref name="values"/> enumerates one exact key twice; a value, even null, is
    /// given for a read-only field of the page (parameter <c>values</c>); or a value does not fit its column.
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
        var exactValues = ExactValues(values);
        foreach (var column in pageColumns)
        {
            // Only the entry keyed by the field's exact canonical name is its value. A key that differs only by case
            // is another field's, so an omitted field binds NULL rather than reusing a case-variant sibling's value.
            var submitted = exactValues.TryGetValue(column.Field.Name, out var value);
            if (column.Field.Field.IsReadOnly)
            {
                // A read-only field is never written from the client, so a new record leaves its column to the
                // table's default, NULL in the seeded tables. A value submitted for it, even null, is refused rather
                // than dropped.
                if (submitted)
                {
                    throw new ArgumentException(ReadOnlyFieldMessage, nameof(values));
                }

                continue;
            }

            if (!submitted)
            {
                value = FieldValue.Null;
            }

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
    /// WHERE [UniqueKey] = @p2 AND [GlobalRecordId] = @p3 AND DATALENGTH([GlobalRecordId]) = DATALENGTH(@p3);
    /// SELECT @@ROWCOUNT</c>, naming only the stamp columns the base table has.
    /// </summary>
    /// <param name="schema">The form's catalogue view.</param>
    /// <param name="uniqueKey">The locked base row's <c>UniqueKey</c>.</param>
    /// <param name="globalRecordId">
    /// The locked base row's <c>GlobalRecordId</c>, as stored: the value <see cref="LockBase"/> returned at
    /// <see cref="BaseRowGlobalRecordIdOrdinal"/>. A spelling of another length, such as one with trailing spaces,
    /// matches no row.
    /// </param>
    /// <param name="stamp">The save stamp of this write.</param>
    /// <returns>
    /// The update, whose one result row holds the number of base rows it changed, whatever the session's
    /// <c>NOCOUNT</c> setting; or <see langword="null"/> when the base table has neither last-save column.
    /// </returns>
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
    /// @pN; SELECT @@ROWCOUNT</c>, at most <see cref="MaxColumnsPerUpdate"/> columns per statement, as legacy chunks
    /// its page updates (<c>Epi.Core/Data/Services/CollectedDataProvider.cs:1274-1340</c>).
    /// </summary>
    /// <remarks>
    /// Only submitted columns are named, so two concurrent saves of different fields both keep their values. Legacy
    /// rewrote every column of every page (<c>:1225-1392</c>); the delta is a deliberate reconciliation recorded in the
    /// parity notes. Each statement has its own parameters, far below the SQL Server limit of 2,100. Each returns the
    /// number of page rows it changed as its one result row, whatever the session's <c>NOCOUNT</c> setting. When the
    /// first statement reports none, the caller sends <see cref="InsertPageKey"/> and repeats it, as legacy does
    /// (<c>:1360-1380</c>).
    /// </remarks>
    /// <param name="page">The page whose table holds the row.</param>
    /// <param name="globalRecordId">The record's id, as stored in the locked base row.</param>
    /// <param name="submitted">
    /// The page's submitted columns and their values, in the order to write them; never a read-only field's column.
    /// </param>
    /// <returns>The statements in order; empty when nothing is submitted.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">
    /// The page has no table; the id is blank or does not fit the key column; a column is not one of the page table's,
    /// is given twice or is null; a column is a read-only field's, refused before any statement is built (parameter
    /// <c>submitted</c>); or a value does not fit its column.
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
        foreach (var (column, _) in submitted)
        {
            if (column.Field.Field.IsReadOnly)
            {
                throw new ArgumentException(ReadOnlyFieldMessage, nameof(submitted));
            }
        }

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
                .Append(parameters.Add(n => ColumnValueConverter.ToSystemParameter(n, key, globalRecordId)))
                .Append(RowCountSelect);
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
    /// DATALENGTH([GlobalRecordId]) = DATALENGTH(@pM); SELECT @@ROWCOUNT</c>.
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
    /// <returns>
    /// The update statement, whose one result row holds the number of base rows it changed, whatever the session's
    /// <c>NOCOUNT</c> setting.
    /// </returns>
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
    // column, over the base-row source appendBaseSource writes, aliased b, LEFT JOINed to each page that has a table. A
    // LEFT JOIN reproduces legacy's per-page load, where a missing page row reads as null values
    // (Epi.Core/Data/Services/CollectedDataProvider.cs:1652-1759). With intrinsicParameters the coordinates are the
    // held counts of AppendIntrinsicCoordinate, whose status parameter is the first of the statement; any parameter
    // the source binds follows it.
    private static SelectCore RecordSelectCore(
        FormSchema schema,
        IReadOnlyList<DataFieldMetadata> dataFields,
        PositionalParameters? intrinsicParameters,
        Action<StringBuilder> appendBaseSource)
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
        if (intrinsicParameters is not null)
        {
            // RecordQueryRules.ActiveRecStatus, bound once with the column's own type and used four times.
            var active = intrinsicParameters.Add(n =>
                ColumnValueConverter.ToSystemParameter(n, recStatusColumn, RecordQueryRules.ActiveRecStatus));
            AppendIntrinsicCoordinate(sql, schema, active, throughRow: true, RecordIntrinsicPositionOrdinal);
            AppendIntrinsicCoordinate(sql, schema, active, throughRow: false, RecordIntrinsicCountOrdinal);
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
        appendBaseSource(sql);
        sql.Append(joins);

        return new SelectCore(sql, columns.AsReadOnly(), presences.AsReadOnly(), hasIntrinsicCoordinates);
    }

    // , CASE WHEN b.[RecStatus] = @active THEN <active rows> ELSE <every row> END AS [cN]: one intrinsic coordinate of
    // the outer base row, over the set RecordQueryRules.IntrinsicIncludesDeleted gives its RecStatus, the active set
    // for an active row and every row for any other status, NULL included. Each branch is a held count of the base
    // table (AppendHeldCount), keyed through the row for the position and unkeyed for the count.
    private static void AppendIntrinsicCoordinate(
        StringBuilder sql,
        FormSchema schema,
        string active,
        bool throughRow,
        int ordinal)
    {
        sql.Append(", CASE WHEN ");
        AppendBaseColumn(sql, Require(schema.RecStatus));
        sql.Append(" = ").Append(active).Append(" THEN ");
        AppendHeldCount(sql, schema, active, throughRow);
        sql.Append(" ELSE ");
        AppendHeldCount(sql, schema, status: null, throughRow);
        sql.Append(" END AS ").Append(ColumnAlias(ordinal));
    }

    // (SELECT COUNT_BIG(*) FROM [base] i WITH (HOLDLOCK)[ WHERE i.[RecStatus] = <status>][ AND i.[UniqueKey] <=
    // b.[UniqueKey]]): the number of base rows with the bound status, or of every row when status is null, and with
    // throughRow only those whose key is at most the outer base row's. UniqueKey is unique, so the keyed count is that
    // row's position in the set, the legacy formula Count(*) ... Where UniqueKey <= k
    // (Epi.Enter.CheckCodeEngine/RunTimeView.cs:673-694), read as a range of the clustered key; the unkeyed count is
    // the size of the set. It is computed for the outer row alone, and it reads the base table as i under the
    // base-table hint (class remarks).
    private static void AppendHeldCount(StringBuilder sql, FormSchema schema, string? status, bool throughRow)
    {
        sql.Append("(SELECT COUNT_BIG(*) FROM ").Append(SqlIdentifier.Quote(schema.BaseTable)).Append(' ')
            .Append(IntrinsicSourceAlias).Append(HoldLockHint);
        var keyword = " WHERE ";
        if (status is not null)
        {
            sql.Append(keyword);
            AppendQualified(sql, IntrinsicSourceAlias, Require(schema.RecStatus)).Append(" = ").Append(status);
            keyword = " AND ";
        }

        if (throughRow)
        {
            var uniqueKeyColumn = Require(schema.UniqueKey);
            sql.Append(keyword);
            AppendQualified(sql, IntrinsicSourceAlias, uniqueKeyColumn).Append(" <= ");
            AppendBaseColumn(sql, uniqueKeyColumn);
        }

        sql.Append(')');
    }

    // (SELECT b.[GlobalRecordId], b.[UniqueKey], b.[RecStatus] FROM [base] b WITH (HOLDLOCK)[ WHERE b.[RecStatus] =
    // @pN] ORDER BY b.[UniqueKey] OFFSET @pM ROWS FETCH NEXT 1 ROWS ONLY) b, the base-row source RecordAt documents.
    // The offset is applied to the base keys of the selected set alone, so the page tables are joined to the fetched
    // row only. The derived table exposes the base system columns under their catalogue names, so the outer query's
    // b.[...] references stay those of a direct base read.
    private static void AppendBaseRowAt(
        StringBuilder sql,
        FormSchema schema,
        bool includeDeleted,
        long offset,
        PositionalParameters parameters)
    {
        sql.Append("(SELECT ");
        AppendBaseColumn(sql, Require(schema.GlobalRecordId));
        sql.Append(", ");
        AppendBaseColumn(sql, Require(schema.UniqueKey));
        sql.Append(", ");
        AppendBaseColumn(sql, Require(schema.RecStatus));
        sql.Append(" FROM ");
        AppendHeldBaseTable(sql, schema);
        AppendSelectedSetPredicate(sql, schema, includeDeleted, parameters, SelectedSetClause.Where);
        AppendOrderByUniqueKey(sql, schema);
        sql.Append(" OFFSET ")
            .Append(parameters.Add(n => ColumnValueConverter.Int64(n, offset)))
            .Append(" ROWS FETCH NEXT 1 ROWS ONLY) ")
            .Append(BaseAlias);
    }

    // (SELECT TOP (1) b.[GlobalRecordId], b.[UniqueKey], b.[RecStatus] FROM [base] b WITH (HOLDLOCK) WHERE
    // b.[GlobalRecordId] = @pN AND DATALENGTH(b.[GlobalRecordId]) = DATALENGTH(@pN) ORDER BY b.[UniqueKey]) b, the
    // base-row source RecordById and SetPositionLookup document. The base table keys on UniqueKey alone and does not
    // constrain GlobalRecordId to be unique, so the derived table chooses the one exact match with the lowest key
    // before the outer query computes coordinates or joins a page table, and only that row's are computed. The id
    // comes from a request and the base table has not confirmed it yet, so it is bound at full length and matched
    // exactly. Like AppendBaseRowAt, the derived table exposes the base system columns under their catalogue names.
    private static void AppendBaseRowById(
        StringBuilder sql,
        FormSchema schema,
        string globalRecordId,
        PositionalParameters parameters)
    {
        sql.Append("(SELECT TOP (1) ");
        AppendBaseColumn(sql, Require(schema.GlobalRecordId));
        sql.Append(", ");
        AppendBaseColumn(sql, Require(schema.UniqueKey));
        sql.Append(", ");
        AppendBaseColumn(sql, Require(schema.RecStatus));
        sql.Append(" FROM ");
        AppendHeldBaseTable(sql, schema);
        sql.Append(" WHERE ");
        AppendGlobalRecordIdMatch(
            sql,
            schema,
            parameters,
            BaseAlias,
            (n, _) => ColumnValueConverter.Text(n, globalRecordId));
        AppendOrderByUniqueKey(sql, schema);
        sql.Append(") ").Append(BaseAlias);
    }

    // [base] b WITH (HOLDLOCK): the base-table reference that yields the row b, directly or inside a derived table, of a
    // read whose rows and coordinates must describe one committed state (class remarks, "Read hint").
    private static void AppendHeldBaseTable(StringBuilder sql, FormSchema schema)
    {
        AppendBaseTable(sql, schema);
        sql.Append(HoldLockHint);
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
    // DATALENGTH([GlobalRecordId]) = DATALENGTH(@pM); SELECT @@ROWCOUNT, with no alias: the update returns its own
    // row count, which the repository requires to be 1, the locked row.
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
        sql.Append(RowCountSelect);

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

    // The submitted values keyed ordinally by the keys the caller's dictionary enumerates, so a column's lookup by its
    // field's canonical name never depends on that dictionary's comparer, which may ignore case: two data fields whose
    // canonical names differ only by case are distinct columns. A dictionary that enumerates one exact key twice
    // cannot say which value is the field's, so it is refused rather than one value dropped.
    private static Dictionary<string, FieldValue> ExactValues(IReadOnlyDictionary<string, FieldValue> values)
    {
        var exact = new Dictionary<string, FieldValue>(values.Count, StringComparer.Ordinal);
        foreach (var (name, value) in values)
        {
            if (!exact.TryAdd(name, value))
            {
                throw new ArgumentException(AmbiguousValueMessage, nameof(values));
            }
        }

        return exact;
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
