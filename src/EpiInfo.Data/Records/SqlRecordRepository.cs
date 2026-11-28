using System.Collections.ObjectModel;
using System.Data;
using System.Data.Common;
using System.Runtime.ExceptionServices;
using EpiInfo.Data.Metadata;
using EpiInfo.Data.Permissions;
using EpiInfo.Data.Sql;
using EpiInfo.Domain.Metadata;
using EpiInfo.Domain.Records;
using EpiInfo.Domain.Search;
using EpiInfo.Domain.Storage;
using EpiInfo.Domain.Validation;
using EpiInfo.Domain.Values;
using Microsoft.Data.SqlClient;

namespace EpiInfo.Data.Records;

/// <summary>
/// The fixed operation names <see cref="SqlRecordRepository"/> reports to the connection factory, to the command
/// observer and in the store exceptions it raises. None is ever derived from request data, so each is safe to log.
/// </summary>
internal static class RecordOperations
{
    /// <summary><see cref="SqlRecordRepository.CountAsync"/>.</summary>
    internal const string Count = "records.count";

    /// <summary><see cref="SqlRecordRepository.GetAtPositionAsync"/>.</summary>
    internal const string At = "records.at";

    /// <summary>Both <c>GetByIdAsync</c> overloads.</summary>
    internal const string Get = "records.get";

    /// <summary><see cref="SqlRecordRepository.GetSetPositionAsync"/>.</summary>
    internal const string SetPosition = "records.setPosition";

    /// <summary><see cref="SqlRecordRepository.ListAsync"/>.</summary>
    internal const string List = "records.list";

    /// <summary><see cref="SqlRecordRepository.SearchAsync"/>.</summary>
    internal const string Search = "records.search";

    /// <summary>Both <c>InsertAsync</c> overloads.</summary>
    internal const string Insert = "records.insert";

    /// <summary>Both <c>UpdateAsync</c> overloads.</summary>
    internal const string Update = "records.update";

    /// <summary><see cref="SqlRecordRepository.SoftDeleteAsync"/>.</summary>
    internal const string Delete = "records.delete";
}

/// <summary>
/// The admission of Find statements to SQL Server: at most a fixed number of searches hold a slot at once, and a
/// search that finds every slot taken waits, before it opens a connection, until one comes free or its request is
/// aborted. Each <see cref="SqlRecordRepository"/> holds one, and the store builds one repository, which the API
/// registers once per process, so the slots bound every Find statement the API process sends.
/// </summary>
/// <remarks>
/// <para>
/// The search-criteria parser bounds the alternatives and the pattern text of one statement: it admits at most
/// <see cref="RecordQueryRules.MaxSearchValueLength"/> code units in one value, and in one search at most
/// <see cref="RecordQueryRules.MaxRowComparedAlternatives"/> alternatives that SQL Server compares row by row and
/// <see cref="RecordQueryRules.MaxSearchPatternTextLength"/> code units of wildcard pattern text, so a Find's work
/// grows only with the rows and the text it scans, and the command timeout bounds how long it runs. Those bounds hold
/// per statement.
/// The slots bound how many such statements occupy SQL Server workers together, so concurrent searches cannot take
/// every worker that saves and record reads need. A search holds its slot from before its connection opens until its
/// statement has completed and the connection is released.
/// </para>
/// <para>
/// The wait has no deadline and no failure of its own, so a busy admission is never reported as an unavailable
/// database. Every admitted search runs its statements under the command timeout and gives its slot back when it
/// ends, so slots keep coming free; a waiting search holds no connection and has sent nothing, and it ends without a
/// slot only when its request is aborted, as when its client gives up.
/// </para>
/// <para>
/// The slots are a <see cref="SemaphoreSlim"/>, which is safe to share across threads and admits waiters in no
/// promised order. It is never disposed: it lives as long as its repository and its wait handle is never created, so
/// it holds no unmanaged resource. Nothing here logs or reads request data.
/// </para>
/// </remarks>
internal sealed class FindAdmission
{
    /// <summary>
    /// The Find statements one repository runs at once: two, so one search can still run while another occupies a SQL
    /// Server worker until its end, and saves and record reads keep every other worker.
    /// </summary>
    internal const int DefaultSlots = 2;

    private readonly SemaphoreSlim slots;

    /// <summary>Creates an admission with <paramref name="slotCount"/> slots, all of them free.</summary>
    /// <param name="slotCount">The searches admitted at once; at least 1.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slotCount"/> is less than 1.</exception>
    internal FindAdmission(int slotCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(slotCount, 1);
        slots = new SemaphoreSlim(slotCount, slotCount);
    }

    /// <summary>
    /// Takes a slot, waiting while every slot is taken until one comes free or <paramref name="cancellationToken"/> is
    /// cancelled, with no deadline of its own (class remarks).
    /// </summary>
    /// <param name="cancellationToken">
    /// The request's abort token. Once it is cancelled no slot is taken, even one that is free.
    /// </param>
    /// <returns>
    /// A task that completes once a slot is taken, which the caller gives back with <see cref="Release"/> once its
    /// statement has completed.
    /// </returns>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled before a slot was taken; nothing is taken.
    /// </exception>
    internal Task EnterAsync(CancellationToken cancellationToken) => slots.WaitAsync(cancellationToken);

    /// <summary>Gives back a slot that <see cref="EnterAsync"/> took.</summary>
    /// <exception cref="SemaphoreFullException">No slot is taken, so none can be given back.</exception>
    internal void Release() => slots.Release();
}

/// <summary>
/// The record half of the SQL Server store: every record read and every record write of <see cref="IDataStore"/>,
/// over the statements <see cref="RecordSqlBuilder"/> and <see cref="SearchSqlBuilder"/> build. Each method has the
/// signature of its <see cref="IDataStore"/> member, so <c>SqlServerDataStore</c> forwards to it unchanged.
/// </summary>
/// <remarks>
/// <para>
/// <b>Metadata.</b> Every public method that sends a statement opens one connection. A read takes the form's metadata
/// and catalogue schema from <see cref="SqlMetadataReader.ReuseOrReadFormAsync"/>: for the instance
/// <see cref="SqlMetadataReader.GetFormMetadataAsync"/> returned earlier in the same request, the schema that read
/// built, with no metadata command; for any other instance, a fresh read on the connection by
/// <see cref="FormMetadata.ViewId"/>. A write always reads both again through
/// <see cref="SqlMetadataReader.ReadFormAsync"/> inside its transaction. Nothing is kept across requests, and every
/// identifier a statement names comes from a schema the reader built from the catalogue.
/// </para>
/// <para>
/// <b>Reads</b> send only <c>SELECT</c> statements, in no transaction. Their failures propagate unchanged for
/// <c>SqlServerDataStore</c> to classify.
/// </para>
/// <para>
/// <b>Find admission.</b> <see cref="SearchAsync"/> takes one of the repository's <see cref="FindAdmission"/> slots,
/// <see cref="FindAdmission.DefaultSlots"/> in all, before it opens its connection, and gives it back once its
/// statement has completed, whatever the outcome. So at most two Find statements of the repository run at once, each
/// limited by the command timeout, and concurrent searches cannot hold every SQL Server worker that saves and record
/// reads need. While every slot is taken a search waits, holding no connection and sending nothing, until one comes
/// free or its request is aborted. The wait has no deadline and no failure of its own, so it gives the search no
/// response of its own: an admitted search answers as any search does, and one cancelled while it waits ends with the
/// <see cref="OperationCanceledException"/> of the request's token, having taken no slot and sent nothing. No other
/// method takes a slot.
/// </para>
/// <para>
/// <b>Writes</b> each run in one <see cref="SqlTransaction"/> at <see cref="IsolationLevel.ReadCommitted"/>, in the
/// statement order of the port: the locked read and the gates, every write, then the response snapshot, whose
/// <see cref="RecordDto"/> is built before the commit. After the commit no statement is sent and nothing is computed
/// from data. A failure before the commit rolls the transaction back and surfaces as the store exception that
/// <see cref="SqlErrorClassifier"/> chooses, carrying the <see cref="WriteOutcome"/> the progress of the
/// transaction proves (<see cref="WriteStage"/>).
/// </para>
/// <para>
/// <b>Concurrency.</b> The response snapshot reads the whole base table under <c>HOLDLOCK</c> while the write holds
/// its own base row exclusively, so two writes in progress together could each wait there for the other's row. Before
/// its first lock on a record table, after the values passed the contract check, an insert therefore takes the form's
/// write lock exclusive and an update takes it shared (<see cref="RecordSqlBuilder.AcquireFormWriteLock"/>), both
/// owned by the transaction. Creates of a form run one at a time and never beside an update of it, so neither forms
/// a lock cycle with the other; updates still run together and serialise per record on the update lock of the
/// locked read. A soft delete takes no form lock: between its locked read and its UPDATE it sends at most the
/// permission read, which touches no record table, and it sends nothing after its exclusive base-row lock, so while it
/// holds a record lock it waits on no other writer. A wait for the form's lock is bounded by the command timeout, and
/// one that SQL Server ends without the lock is reported as unavailable with nothing written.
/// </para>
/// <para>
/// <b>Permissions.</b> A write given non-null permissions reads them again on its own connection and inside its
/// transaction, through <see cref="SqlPermissionReader.ReadResolvedAsync"/>, for the tables its schema read resolved,
/// and decides from that read alone: an insert after the form's exclusive lock, on the base table and every page
/// table; an update after the required gate, on the base table and every page table; a soft delete after its locked
/// read, on the base table only. Each read comes before the write's first data statement, so a refusal rolls back
/// with nothing written and the caller's earlier read never decides it.
/// </para>
/// <para>
/// <b>Contract violations.</b> A <c>values</c> key that names no writable data field, a repeated field, or a value
/// its field and column cannot hold is an <see cref="ArgumentException"/>, raised before any base- or page-table
/// statement is sent, after the transaction is rolled back, and never wrapped.
/// </para>
/// <para>
/// <b>Metadata changed since the caller read it.</b> The public <c>InsertAsync</c> and <c>UpdateAsync</c> check the
/// values against the metadata their transaction reads, which can differ from the <see cref="FormMetadata"/> the
/// caller validated them against. A refusal there is the caller's broken argument only when the caller's own metadata
/// refuses the values too, by the same rules. When it accepts them, or when an insert's transaction no longer finds
/// the form, the metadata changed after the caller's read: that is no contract violation, and the write rolls back
/// before any base- or page-table statement and fails as a <see cref="DataStoreWriteException"/> with
/// <see cref="WriteOutcome.NotWritten"/> and no SQL error number.
/// </para>
/// <para>
/// The instance holds only thread-safe collaborators and is safe to share across threads. It logs nothing, and every
/// exception message it builds is fixed text naming no table, column, value or id.
/// </para>
/// </remarks>
/// <param name="connections">Opens the connection of each operation.</param>
/// <param name="metadata">Reads the form's metadata and catalogue schema on that connection.</param>
/// <param name="options">Supplies the command timeout every statement runs with.</param>
/// <param name="observer">Receives every statement this repository sends.</param>
internal sealed class SqlRecordRepository(
    SqlConnectionFactory connections,
    SqlMetadataReader metadata,
    DatabaseOptions options,
    ISqlCommandObserver observer)
{
    // Fixed messages: the redaction policy keeps identifiers, values and ids out of exception text.
    private const string UnknownFormMessage = "The form is not in the form metadata.";
    private const string UnknownFieldMessage = "A value names no data field of the form.";
    private const string ReadOnlyFieldMessage = "A read-only field is never written.";
    private const string RepeatedFieldMessage = "Two values name the same field ignoring case.";
    private const string MetadataChangedMessage = "The form's metadata changed after the caller read it.";
    private const string MismatchedSchemaMessage = "The form schema does not describe the form.";
    private const string ConnectionNotOpenMessage = "The connection must be open.";
    private const string MissingRecordMessage = "The written record could not be read back inside its transaction.";
    private const string MissingCoordinatesMessage = "The record read carries no intrinsic position and count.";
    private const string RowNotWrittenMessage = "A write statement affected no row of the locked record.";
    private const string UnexpectedRowCountMessage = "A write statement reported an unexpected number of changed rows.";
    private const string GateResultMessage = "The required gate returned no result.";
    private const string FormWriteLockRefusedMessage = "SQL Server refused the form's write lock.";
    private const string StampResultMessage = "The save stamp source returned no stamp.";
    private const string UnexpectedInsertResultMessage = "An insert given no permissions returned no committed record.";

    // The name a submitted value is bound under to test it against its column; that parameter is never sent.
    private const string ProbeParameterName = "@p0";

    private readonly SqlConnectionFactory connections =
        connections ?? throw new ArgumentNullException(nameof(connections));

    private readonly SqlMetadataReader metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));

    private readonly DatabaseOptions options = options ?? throw new ArgumentNullException(nameof(options));

    private readonly ISqlCommandObserver observer = observer ?? throw new ArgumentNullException(nameof(observer));

    // The Find slots every search of this repository takes before it opens its connection (class remarks).
    private readonly FindAdmission findAdmission = new(FindAdmission.DefaultSlots);

    /// <summary>
    /// Creates a repository whose searches take their slots from <paramref name="findAdmission"/> instead of an
    /// admission of its own: the seam through which a test holds every slot, or shares one admission between
    /// repositories. Production code uses the four-parameter constructor.
    /// </summary>
    /// <param name="connections">Opens the connection of each operation.</param>
    /// <param name="metadata">Reads the form's metadata and catalogue schema on that connection.</param>
    /// <param name="options">Supplies the command timeout every statement runs with.</param>
    /// <param name="observer">Receives every statement this repository sends.</param>
    /// <param name="findAdmission">The Find slots <see cref="SearchAsync"/> takes.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    internal SqlRecordRepository(
        SqlConnectionFactory connections,
        SqlMetadataReader metadata,
        DatabaseOptions options,
        ISqlCommandObserver observer,
        FindAdmission findAdmission)
        : this(connections, metadata, options, observer)
    {
        this.findAdmission = findAdmission ?? throw new ArgumentNullException(nameof(findAdmission));
    }

    /// <summary>Counts the records of the selected set, as <see cref="IDataStore.CountAsync"/>.</summary>
    /// <param name="form">The form's metadata; its view id identifies the form.</param>
    /// <param name="includeDeleted">
    /// <see langword="false"/> for the active set (<c>RecStatus = 1</c>); <see langword="true"/> for every row.
    /// </param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>The number of base-table rows in the selected set; 0 for a form with none or no longer present.</returns>
    public async Task<int> CountAsync(FormMetadata form, bool includeDeleted, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);

        SqlConnection connection = await connections
            .OpenAsync(RecordOperations.Count, cancellationToken)
            .ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            if (await metadata.ReuseOrReadFormAsync(connection, form, cancellationToken)
                    .ConfigureAwait(false) is not { } read)
            {
                return 0;
            }

            return await ReadCountAsync(
                    connection,
                    transaction: null,
                    RecordOperations.Count,
                    RecordSqlBuilder.Count(read.Schema, includeDeleted),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads the record at a 1-based position of the selected set, with every page row and its intrinsic position
    /// and count from the same statement, as <see cref="IDataStore.GetAtPositionAsync"/>.
    /// </summary>
    /// <param name="form">The form's metadata.</param>
    /// <param name="position">The 1-based position.</param>
    /// <param name="includeDeleted">Whether the selected set holds every row rather than the active set.</param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>
    /// The record, or <see langword="null"/> when <paramref name="position"/> is outside 1 to the selected-set count
    /// or the form is no longer present.
    /// </returns>
    public async Task<RecordDto?> GetAtPositionAsync(
        FormMetadata form,
        int position,
        bool includeDeleted,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);

        // No row can sit before position 1, so no statement is needed to answer it.
        if (!RecordQueryRules.IsValidPosition(position))
        {
            return null;
        }

        SqlConnection connection = await connections
            .OpenAsync(RecordOperations.At, cancellationToken)
            .ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            if (await metadata.ReuseOrReadFormAsync(connection, form, cancellationToken)
                    .ConfigureAwait(false) is not { } read)
            {
                return null;
            }

            RecordSelect select = RecordSqlBuilder.RecordAt(
                read.Schema,
                FormDefinitionFactory.GetDataFields(read.Form),
                includeDeleted,
                position);
            RecordRow? row = await ReadRecordAsync(
                    connection,
                    transaction: null,
                    RecordOperations.At,
                    read.Schema,
                    select,
                    cancellationToken)
                .ConfigureAwait(false);

            return row?.ToDto();
        }
    }

    /// <summary>
    /// Reads one record by its <c>GlobalRecordId</c>, deleted records included, with every page row and its
    /// intrinsic position and count from the same statement, as <see cref="IDataStore.GetByIdAsync"/>.
    /// </summary>
    /// <param name="form">The form's metadata.</param>
    /// <param name="globalRecordId">The record's base-table <c>GlobalRecordId</c>, matched exactly.</param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>
    /// The record, or <see langword="null"/> when the id is not in the base table or the form is no longer present.
    /// </returns>
    public async Task<RecordDto?> GetByIdAsync(
        FormMetadata form,
        string globalRecordId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(globalRecordId);

        SqlConnection connection = await connections
            .OpenAsync(RecordOperations.Get, cancellationToken)
            .ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            if (await metadata.ReuseOrReadFormAsync(connection, form, cancellationToken)
                    .ConfigureAwait(false) is not { } read)
            {
                return null;
            }

            return await GetByIdCoreAsync(connection, read.Form, read.Schema, globalRecordId, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Places a record in the caller's selected set, for the navigator's "[n] of N", as
    /// <see cref="IDataStore.GetSetPositionAsync"/>. Only the base table is read, so a record missing a page row is
    /// still placed. One statement returns the record's <c>RecStatus</c>, its selected-set position and the
    /// selected-set count, so a concurrent write can never pair a position with a membership or a count of another
    /// committed state.
    /// </summary>
    /// <param name="form">The form's metadata.</param>
    /// <param name="globalRecordId">The record's base-table <c>GlobalRecordId</c>, matched exactly.</param>
    /// <param name="includeDeleted">Whether the selected set holds every row rather than the active set.</param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>
    /// <see langword="null"/> when the id is not in the base table or the form is no longer present; otherwise the
    /// selected-set count with the record's position in that set, or a <see langword="null"/> position when the
    /// record is outside it.
    /// </returns>
    public async Task<NavigationPosition?> GetSetPositionAsync(
        FormMetadata form,
        string globalRecordId,
        bool includeDeleted,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(globalRecordId);

        SqlConnection connection = await connections
            .OpenAsync(RecordOperations.SetPosition, cancellationToken)
            .ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            if (await metadata.ReuseOrReadFormAsync(connection, form, cancellationToken)
                    .ConfigureAwait(false) is not { } read)
            {
                return null;
            }

            // One statement reads the row's status, its position and the count, so they describe one committed state.
            if (await SqlExecution.ReadAsync<SetPlacement?>(
                    connection,
                    transaction: null,
                    RecordOperations.SetPosition,
                    RecordSqlBuilder.SetPositionLookup(read.Schema, globalRecordId, includeDeleted),
                    options.CommandTimeoutSeconds,
                    observer,
                    cancellationToken,
                    async (reader, token) =>
                        await reader.ReadAsync(token).ConfigureAwait(false)
                            ? new SetPlacement(
                                ReadSystemInteger(
                                    reader,
                                    read.Schema.RecStatus,
                                    RecordSqlBuilder.BaseRowRecStatusOrdinal),
                                ReadComputedInteger(
                                    reader,
                                    RecordSqlBuilder.SetPositionType,
                                    RecordSqlBuilder.SetPositionOrdinal),
                                ReadComputedInteger(
                                    reader,
                                    RecordSqlBuilder.SetCountType,
                                    RecordSqlBuilder.SetCountOrdinal))
                            : null)
                .ConfigureAwait(false) is not { } placement)
            {
                return null;
            }

            return new NavigationPosition(
                RecordQueryRules.IsInSelectedSet(placement.RecStatus, includeDeleted) ? placement.Position : null,
                placement.Count);
        }
    }

    /// <summary>
    /// Reads one page of the records list, as <see cref="IDataStore.ListAsync"/>: the selected set, filtered by the
    /// grid filter, ordered by <c>UniqueKey</c> and paged, holding the grid columns the form's data fields select. One
    /// statement returns the page and the total of the filtered set and holds every base and page row it reads until
    /// it completes
    /// (<see cref="SearchSqlBuilder.ListPage(FormSchema, IReadOnlyList{DataFieldMetadata}, RecordListQuery)"/>), so
    /// both describe one committed state.
    /// </summary>
    /// <param name="form">The form's metadata.</param>
    /// <param name="query">The coerced list parameters.</param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>
    /// The page's summaries with the total of the filtered set; no items and a total of 0 when the form is no longer
    /// present. A valid page past the end has no items and the correct total.
    /// </returns>
    public async Task<RecordPage> ListAsync(FormMetadata form, RecordListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(query);

        SqlConnection connection = await connections
            .OpenAsync(RecordOperations.List, cancellationToken)
            .ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            if (await metadata.ReuseOrReadFormAsync(connection, form, cancellationToken)
                    .ConfigureAwait(false) is not { } read)
            {
                return new RecordPage(Array.Empty<RecordSummary>(), 0, query.Page, query.PageSize);
            }

            IReadOnlyList<DataFieldMetadata> gridColumns =
                RecordQueryRules.GetGridColumns(FormDefinitionFactory.GetDataFields(read.Form));

            // One statement returns the page and the total of the filtered set, holding every row it reads, so both
            // describe one committed state. It is built before it is sent, so a grid column the builder refuses sends
            // nothing.
            ListSelect page = SearchSqlBuilder.ListPage(read.Schema, gridColumns, query);

            (IReadOnlyList<RecordSummary> items, int count) = await SqlExecution.ReadAsync(
                    connection,
                    transaction: null,
                    RecordOperations.List,
                    page.Statement,
                    options.CommandTimeoutSeconds,
                    observer,
                    cancellationToken,
                    async (reader, token) =>
                    {
                        var summaries = new List<RecordSummary>();
                        int total = 0;
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                        {
                            // Every row carries the same total. An empty page, past the end or of an empty set, still
                            // returns one row, flagged as no item, so the total always arrives.
                            total = ReadComputedInteger(
                                reader,
                                SearchSqlBuilder.TotalType,
                                SearchSqlBuilder.TotalOrdinal);
                            if (ReadComputedInteger(
                                    reader,
                                    SearchSqlBuilder.InPageType,
                                    SearchSqlBuilder.InPageOrdinal) == 0)
                            {
                                continue;
                            }

                            summaries.Add(new RecordSummary(
                                ReadSystemText(reader, read.Schema.GlobalRecordId, SearchSqlBuilder.IdOrdinal),
                                ReadSystemInteger(reader, read.Schema.UniqueKey, SearchSqlBuilder.UniqueKeyOrdinal),
                                ReadSystemInteger(reader, read.Schema.RecStatus, SearchSqlBuilder.RecStatusOrdinal),
                                ReadValues(reader, page.Columns)));
                        }

                        return ((IReadOnlyList<RecordSummary>)summaries.AsReadOnly(), total);
                    })
                .ConfigureAwait(false);

            return new RecordPage(items, count, query.Page, query.PageSize);
        }
    }

    /// <summary>
    /// Finds the records matching the parsed Find criteria, as <see cref="IDataStore.SearchAsync"/>: one statement
    /// over the base table joined to every page table, ordered by <c>UniqueKey</c> and capped at
    /// <see cref="RecordQueryRules.SearchCap"/>, each result carrying its selected-set position and grid columns.
    /// </summary>
    /// <param name="form">The form's metadata.</param>
    /// <param name="conditions">The parsed criteria; AND across conditions, OR within one.</param>
    /// <param name="includeDeleted">Whether the selected set holds every row rather than the active set.</param>
    /// <param name="excludeGlobalRecordId">The open record's id to leave out, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>
    /// The capped results with the uncapped match count; no match, or a form no longer present, gives no results and
    /// a total of 0.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// A condition names no data field of the form or holds a pattern the builder refuses, raised before any base- or
    /// page-table statement is sent.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled, while the search waited for a Find slot (class remarks), in
    /// which case it took no slot, opened no connection and sent nothing, or later.
    /// </exception>
    public async Task<SearchResponse> SearchAsync(
        FormMetadata form,
        IReadOnlyList<SearchCondition> conditions,
        bool includeDeleted,
        string? excludeGlobalRecordId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(conditions);

        // The slot is taken before the connection opens, so a search that waits holds no pooled connection and has sent
        // nothing. The wait ends when an admitted search, whose statements run under the command timeout, gives its
        // slot back, or when the request is aborted.
        await findAdmission.EnterAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await SearchAdmittedAsync(form, conditions, includeDeleted, excludeGlobalRecordId, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            // Given back once the statement has completed or failed and its connection is released, whatever the
            // outcome, so a failed search never keeps its slot.
            findAdmission.Release();
        }
    }

    // The search itself, run while it holds a Find slot: one connection, the form's metadata and schema, then the one
    // Find statement, as SearchAsync documents.
    private async Task<SearchResponse> SearchAdmittedAsync(
        FormMetadata form,
        IReadOnlyList<SearchCondition> conditions,
        bool includeDeleted,
        string? excludeGlobalRecordId,
        CancellationToken cancellationToken)
    {
        SqlConnection connection = await connections
            .OpenAsync(RecordOperations.Search, cancellationToken)
            .ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            if (await metadata.ReuseOrReadFormAsync(connection, form, cancellationToken)
                    .ConfigureAwait(false) is not { } read)
            {
                return SearchResponse.Create(Array.Empty<SearchResult>(), 0);
            }

            DataFieldSet dataFields = FormDefinitionFactory.GetDataFields(read.Form);
            SearchSelect select = SearchSqlBuilder.Search(
                read.Schema,
                dataFields,
                RecordQueryRules.GetGridColumns(dataFields),
                conditions,
                includeDeleted,
                excludeGlobalRecordId);

            (IReadOnlyList<SearchResult> items, int matches) = await SqlExecution.ReadAsync(
                    connection,
                    transaction: null,
                    RecordOperations.Search,
                    select.Statement,
                    options.CommandTimeoutSeconds,
                    observer,
                    cancellationToken,
                    async (reader, token) =>
                    {
                        var results = new List<SearchResult>();
                        int total = 0;
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                        {
                            // Every row carries the same uncapped total, counted before TOP applies.
                            if (results.Count == 0)
                            {
                                total = ReadComputedInteger(
                                    reader,
                                    SearchSqlBuilder.TotalType,
                                    SearchSqlBuilder.TotalOrdinal);
                            }

                            results.Add(new SearchResult(
                                ReadSystemText(reader, read.Schema.GlobalRecordId, SearchSqlBuilder.IdOrdinal),
                                ReadSystemInteger(reader, read.Schema.UniqueKey, SearchSqlBuilder.UniqueKeyOrdinal),
                                ReadSystemInteger(reader, read.Schema.RecStatus, SearchSqlBuilder.RecStatusOrdinal),
                                ReadComputedInteger(
                                    reader,
                                    SearchSqlBuilder.PositionType,
                                    SearchSqlBuilder.PositionOrdinal),
                                ReadValues(reader, select.Columns)));
                        }

                        return ((IReadOnlyList<SearchResult>)results.AsReadOnly(), total);
                    })
                .ConfigureAwait(false);

            return SearchResponse.Create(items, matches);
        }
    }

    /// <summary>
    /// Inserts a new active record, as <see cref="IDataStore.InsertAsync"/>: the form's exclusive write lock; when
    /// <paramref name="permissions"/> is not <see langword="null"/>, one permission read inside this transaction for
    /// the base table and every page table, which must grant INSERT on each of them (<c>Forbidden</c>); then the base
    /// row with one save stamp for both stamp pairs, one row on every page in page order holding every writable data
    /// column (<c>NULL</c> when absent), then the response snapshot, all in one transaction that then commits. The
    /// exclusive lock runs inserts of the form one at a time and never beside an update of it, so concurrent inserts
    /// all commit rather than deadlock in their snapshot reads (class remarks).
    /// </summary>
    /// <param name="form">The form's metadata, as the store's metadata read returned it.</param>
    /// <param name="values">The validated values of writable data fields, keyed by canonical name.</param>
    /// <param name="stamp">The save stamp of this write.</param>
    /// <param name="permissions">
    /// Non-null turns on the INSERT check, which this transaction decides from its own permission read; the caller's
    /// earlier read passed here never decides it. <see langword="null"/> means no check and no permission statement.
    /// </param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>
    /// <see cref="RecordWriteStatus.Success"/> with the new record as committed, with its intrinsic position and
    /// count; or <see cref="RecordWriteStatus.Forbidden"/> when this transaction's permission read denied the insert
    /// and it rolled back with nothing written.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="values"/> breaks the contract against <paramref name="form"/> (class remarks); raised before
    /// any base- or page-table statement is sent.
    /// </exception>
    /// <exception cref="DataStoreUnavailableException">The database was unavailable; the outcome says what is known.</exception>
    /// <exception cref="DataStoreWriteException">
    /// Any other failure; the outcome says what is known. A page whose table, or its record key column, the catalogue
    /// does not hold is refused this way, with <see cref="WriteOutcome.NotWritten"/> and nothing written, as are a form
    /// the transaction's metadata read no longer finds and values that read refuses although <paramref name="form"/>
    /// accepts them (class remarks).
    /// </exception>
    public async Task<RecordWriteResult> InsertAsync(
        FormMetadata form,
        IReadOnlyDictionary<string, FieldValue> values,
        SaveStamp stamp,
        FormPermissions? permissions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(stamp);

        SqlConnection connection = await connections
            .OpenAsync(RecordOperations.Insert, cancellationToken)
            .ConfigureAwait(false);
        return await RunWriteAsync(
                connection,
                ownsConnection: true,
                RecordOperations.Insert,
                async scope =>
                {
                    // The caller's form came from the store's own metadata read, so a form this transaction no longer
                    // finds was removed after that read: the metadata changed, which is no broken argument, and the
                    // failure, raised before any record statement, is reported as writing nothing.
                    (FormMetadata Form, FormSchema Schema) read = await metadata
                            .ReadFormAsync(scope.Connection, scope.Transaction, form.ViewId, cancellationToken)
                            .ConfigureAwait(false)
                        ?? throw new InvalidOperationException(UnknownFormMessage);

                    return await InsertCoreAsync(
                            scope,
                            read.Form,
                            read.Schema,
                            callerForm: form,
                            values,
                            stamp,
                            permissions,
                            cancellationToken)
                        .ConfigureAwait(false);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Updates the submitted fields of an existing record as a delta, as <see cref="IDataStore.UpdateAsync"/>, inside
    /// one transaction that first locks the base row and re-checks the required fields against the stored state.
    /// </summary>
    /// <remarks>
    /// In order: the form's shared write lock; the locked base read (<c>NotFound</c>, or <c>Deleted</c> for
    /// <c>RecStatus</c> 0); the page-row read; the required gate; when <paramref name="permissions"/> is not
    /// <see langword="null"/>, one permission read inside this transaction for the base table and every page table,
    /// which must grant UPDATE on each of them and INSERT on the table of every page whose row is missing
    /// (<c>Forbidden</c>); the one call of <paramref name="createStamp"/>; the base last-save stamp; for each page
    /// holding a submitted field, in page order, the submitted columns, inserting the page row first when the update
    /// finds none, as legacy repairs it; then the snapshot and the commit.
    /// Updates hold the shared lock together, and none runs beside a create of the form, which holds it exclusive
    /// (class remarks). Concurrent saves of one record serialise on the base-row lock and the later commit wins for a
    /// field both submitted, with no row-version check.
    /// </remarks>
    /// <param name="form">The form's metadata, as the store's metadata read returned it.</param>
    /// <param name="globalRecordId">The record's base-table <c>GlobalRecordId</c>, matched exactly.</param>
    /// <param name="submitted">The validated values of the writable data fields the request submitted.</param>
    /// <param name="createStamp">
    /// Creates the save stamp of this write; called once, only after every check has decided the update writes and
    /// before its first data statement, as <see cref="IDataStore.UpdateAsync"/> defines.
    /// </param>
    /// <param name="requiredGate">The required-field check of the stored record overlaid with the submitted values.</param>
    /// <param name="permissions">
    /// Non-null turns on the UPDATE and missing-page INSERT check, which this transaction decides from its own
    /// permission read; the caller's earlier read passed here never decides it. <see langword="null"/> means no check
    /// and no permission statement.
    /// </param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>
    /// <see cref="RecordWriteStatus.Success"/> with the updated record when the transaction committed; any other
    /// status, the form no longer being present included (<see cref="RecordWriteStatus.NotFound"/>), when it rolled
    /// back with nothing written.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="submitted"/> breaks the contract against <paramref name="form"/> (class remarks); raised before
    /// any base- or page-table statement is sent.
    /// </exception>
    /// <exception cref="DataStoreUnavailableException">The database was unavailable; the outcome says what is known.</exception>
    /// <exception cref="DataStoreWriteException">
    /// Any other failure, an exception or a <see langword="null"/> result of <paramref name="requiredGate"/> or
    /// <paramref name="createStamp"/> included, which reports <see cref="WriteOutcome.NotWritten"/> with no SQL error
    /// number, as do values the transaction's metadata read refuses although <paramref name="form"/> accepts them
    /// (class remarks).
    /// </exception>
    public async Task<RecordWriteResult> UpdateAsync(
        FormMetadata form,
        string globalRecordId,
        IReadOnlyDictionary<string, FieldValue> submitted,
        Func<SaveStamp> createStamp,
        Func<StoredRecord, RecordValidationResult> requiredGate,
        FormPermissions? permissions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(globalRecordId);
        ArgumentNullException.ThrowIfNull(submitted);
        ArgumentNullException.ThrowIfNull(createStamp);
        ArgumentNullException.ThrowIfNull(requiredGate);

        SqlConnection connection = await connections
            .OpenAsync(RecordOperations.Update, cancellationToken)
            .ConfigureAwait(false);
        return await RunWriteAsync(
                connection,
                ownsConnection: true,
                RecordOperations.Update,
                async scope =>
                {
                    if (await metadata
                            .ReadFormAsync(scope.Connection, scope.Transaction, form.ViewId, cancellationToken)
                            .ConfigureAwait(false) is not { } read)
                    {
                        return WriteDecision<RecordWriteResult>.RolledBack(RecordWriteResult.NotFound());
                    }

                    return await UpdateCoreAsync(
                            scope,
                            read.Form,
                            read.Schema,
                            callerForm: form,
                            globalRecordId,
                            submitted,
                            createStamp,
                            requiredGate,
                            permissions,
                            cancellationToken)
                        .ConfigureAwait(false);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Soft-deletes a record on its base row only, as <see cref="IDataStore.SoftDeleteAsync"/>: <c>RecStatus</c> 0
    /// with the last-save stamp, in one transaction. Page rows are never touched and nothing cascades. In order: the
    /// locked base read (<c>NotFound</c>, or <c>AlreadyDeleted</c> for <c>RecStatus</c> 0); when
    /// <paramref name="permissions"/> is not <see langword="null"/>, one permission read inside this transaction for
    /// the base table alone, which must grant UPDATE on it (<c>Forbidden</c>); the one call of
    /// <paramref name="createStamp"/>; the base UPDATE; the commit. It takes no form write lock: between its locked
    /// read and its UPDATE it sends at most that permission read, which touches no record table, and it sends nothing
    /// after its exclusive lock on the base row, so it closes no lock cycle with an insert or an update (class
    /// remarks).
    /// </summary>
    /// <param name="form">The form's metadata, as the store's metadata read returned it.</param>
    /// <param name="globalRecordId">
    /// The record's base-table <c>GlobalRecordId</c>, matched as <see cref="RecordSqlBuilder.LockBase"/> matches it:
    /// letter case follows the column's collation, and the byte length must be equal.
    /// </param>
    /// <param name="createStamp">
    /// Supplies the save stamp; called once, only when the locked read found an active record and the permission check
    /// passed, and before the soft delete's data statement; never for an unknown, already-deleted or refused record.
    /// </param>
    /// <param name="permissions">
    /// Non-null turns on the base-table UPDATE check, which this transaction decides from its own permission read; the
    /// caller's earlier read passed here never decides it. <see langword="null"/> means no check and no permission
    /// statement.
    /// </param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>
    /// <see cref="SoftDeleteStatus.Deleted"/> when the transaction committed;
    /// <see cref="SoftDeleteStatus.AlreadyDeleted"/> with nothing written, stamps included, for a record with
    /// <c>RecStatus</c> 0; each with the base row's <c>GlobalRecordId</c> as the locked read returned it, the stored
    /// spelling. <see cref="SoftDeleteStatus.NotFound"/>, with no id, for an unknown id or a form no longer present.
    /// <see cref="SoftDeleteStatus.Forbidden"/>, with no id and nothing written, when this transaction's permission
    /// read denied the soft delete of an active record.
    /// </returns>
    /// <exception cref="DataStoreUnavailableException">The database was unavailable; the outcome says what is known.</exception>
    /// <exception cref="DataStoreWriteException">
    /// Any other failure, the outcome saying what is known; an exception or a <see langword="null"/> result of
    /// <paramref name="createStamp"/> reports <see cref="WriteOutcome.NotWritten"/> with no SQL error number.
    /// </exception>
    public async Task<SoftDeleteResult> SoftDeleteAsync(
        FormMetadata form,
        string globalRecordId,
        Func<SaveStamp> createStamp,
        FormPermissions? permissions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(globalRecordId);
        ArgumentNullException.ThrowIfNull(createStamp);

        SqlConnection connection = await connections
            .OpenAsync(RecordOperations.Delete, cancellationToken)
            .ConfigureAwait(false);
        return await RunWriteAsync(
                connection,
                ownsConnection: true,
                RecordOperations.Delete,
                async scope =>
                {
                    if (await metadata
                            .ReadFormAsync(scope.Connection, scope.Transaction, form.ViewId, cancellationToken)
                            .ConfigureAwait(false) is not { } read)
                    {
                        return WriteDecision<SoftDeleteResult>.RolledBack(SoftDeleteResult.NotFound());
                    }

                    return await SoftDeleteCoreAsync(
                            scope,
                            read.Form,
                            read.Schema,
                            globalRecordId,
                            createStamp,
                            permissions,
                            cancellationToken)
                        .ConfigureAwait(false);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Inserts a record on a connection the caller owns, with a form and schema the caller supplies: the
    /// reserved-word identifier fixture's entry to the production insert path, over session temporary tables.
    /// </summary>
    /// <remarks>
    /// It skips the connection open and the metadata read and runs the same transaction, statements, stage tracking
    /// and observer as
    /// <see cref="InsertAsync(FormMetadata, IReadOnlyDictionary{string, FieldValue}, SaveStamp, FormPermissions?, CancellationToken)"/>
    /// given no permissions, so it sends no permission statement and always commits or throws. With no metadata read
    /// of its own, a refused value is always the caller's broken argument, an unwrapped
    /// <see cref="ArgumentException"/>. The connection is never disposed here.
    /// </remarks>
    /// <param name="connection">An open connection with no pending transaction.</param>
    /// <param name="form">The in-memory metadata of the fixture form.</param>
    /// <param name="schema">
    /// The fixture's catalogue schema, composed from tables <see cref="SqlMetadataReader.ReadTempTableSchemaAsync"/>
    /// built, with the view id of <paramref name="form"/>.
    /// </param>
    /// <param name="values">The values of writable data fields, keyed by canonical name.</param>
    /// <param name="stamp">The save stamp of this write.</param>
    /// <param name="cancellationToken">The caller's abort token.</param>
    /// <returns>The new record as committed, with its intrinsic position and count.</returns>
    internal async Task<RecordDto> InsertAsync(
        SqlConnection connection,
        FormMetadata form,
        FormSchema schema,
        IReadOnlyDictionary<string, FieldValue> values,
        SaveStamp stamp,
        CancellationToken cancellationToken)
    {
        RequireFixture(connection, form, schema);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(stamp);

        RecordWriteResult result = await RunWriteAsync(
                connection,
                ownsConnection: false,
                RecordOperations.Insert,
                scope => InsertCoreAsync(
                    scope,
                    form,
                    schema,
                    callerForm: null,
                    values,
                    stamp,
                    permissions: null,
                    cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);

        // With no permissions the insert has no refusal, so a committed record is the only result it can return.
        return result.IsSuccess ? result.Record : throw new InvalidOperationException(UnexpectedInsertResultMessage);
    }

    /// <summary>
    /// Updates a record on a connection the caller owns, with a form and schema the caller supplies, through the same
    /// path as the public <c>UpdateAsync</c>; the reserved-word identifier fixture's entry. With no metadata read of
    /// its own, a refused value is always the caller's broken argument, an unwrapped <see cref="ArgumentException"/>.
    /// The connection is never disposed here.
    /// </summary>
    /// <param name="connection">An open connection with no pending transaction.</param>
    /// <param name="form">The in-memory metadata of the fixture form.</param>
    /// <param name="schema">The fixture's catalogue schema, with the view id of <paramref name="form"/>.</param>
    /// <param name="globalRecordId">The record's base-table <c>GlobalRecordId</c>.</param>
    /// <param name="submitted">The values of the writable data fields to write.</param>
    /// <param name="createStamp">
    /// Creates the save stamp of this write, called as the public overload calls it: once, only when the update
    /// writes, before its first data statement.
    /// </param>
    /// <param name="requiredGate">The required-field check of the stored record.</param>
    /// <param name="permissions">
    /// Non-null turns on the UPDATE and missing-page INSERT check, decided from this transaction's own permission
    /// read; <see langword="null"/> means no check.
    /// </param>
    /// <param name="cancellationToken">The caller's abort token.</param>
    /// <returns>The update's result, as the public overload returns it.</returns>
    internal async Task<RecordWriteResult> UpdateAsync(
        SqlConnection connection,
        FormMetadata form,
        FormSchema schema,
        string globalRecordId,
        IReadOnlyDictionary<string, FieldValue> submitted,
        Func<SaveStamp> createStamp,
        Func<StoredRecord, RecordValidationResult> requiredGate,
        FormPermissions? permissions,
        CancellationToken cancellationToken)
    {
        RequireFixture(connection, form, schema);
        ArgumentNullException.ThrowIfNull(globalRecordId);
        ArgumentNullException.ThrowIfNull(submitted);
        ArgumentNullException.ThrowIfNull(createStamp);
        ArgumentNullException.ThrowIfNull(requiredGate);

        return await RunWriteAsync(
                connection,
                ownsConnection: false,
                RecordOperations.Update,
                scope => UpdateCoreAsync(
                    scope,
                    form,
                    schema,
                    callerForm: null,
                    globalRecordId,
                    submitted,
                    createStamp,
                    requiredGate,
                    permissions,
                    cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Reads a record by id on a connection the caller owns, with a form and schema the caller supplies, through the
    /// same statement and row mapping as the public <c>GetByIdAsync</c>; the reserved-word identifier fixture's entry.
    /// The connection is never disposed here.
    /// </summary>
    /// <param name="connection">An open connection.</param>
    /// <param name="form">The in-memory metadata of the fixture form.</param>
    /// <param name="schema">The fixture's catalogue schema, with the view id of <paramref name="form"/>.</param>
    /// <param name="globalRecordId">The record's base-table <c>GlobalRecordId</c>.</param>
    /// <param name="cancellationToken">The caller's abort token.</param>
    /// <returns>The record, or <see langword="null"/> when the id is not in the base table.</returns>
    internal async Task<RecordDto?> GetByIdAsync(
        SqlConnection connection,
        FormMetadata form,
        FormSchema schema,
        string globalRecordId,
        CancellationToken cancellationToken)
    {
        RequireFixture(connection, form, schema);
        ArgumentNullException.ThrowIfNull(globalRecordId);

        return await GetByIdCoreAsync(connection, form, schema, globalRecordId, cancellationToken)
            .ConfigureAwait(false);
    }

    // The by-id read both GetByIdAsync overloads share: one statement returning the row, every page row and the
    // record's intrinsic position and count.
    private async Task<RecordDto?> GetByIdCoreAsync(
        SqlConnection connection,
        FormMetadata form,
        FormSchema schema,
        string globalRecordId,
        CancellationToken cancellationToken)
    {
        RecordSelect select = RecordSqlBuilder.RecordById(
            schema,
            FormDefinitionFactory.GetDataFields(form),
            globalRecordId);
        RecordRow? row = await ReadRecordAsync(
                connection,
                transaction: null,
                RecordOperations.Get,
                schema,
                select,
                cancellationToken)
            .ConfigureAwait(false);

        return row?.ToDto();
    }

    // The insert both InsertAsync overloads share, inside the transaction RunWriteAsync opened. callerForm is the
    // metadata the caller validated the values against, when this transaction read form again itself; null when form
    // is the caller's own (ResolveSubmitted).
    private async Task<WriteDecision<RecordWriteResult>> InsertCoreAsync(
        WriteScope scope,
        FormMetadata form,
        FormSchema schema,
        FormMetadata? callerForm,
        IReadOnlyDictionary<string, FieldValue> values,
        SaveStamp stamp,
        FormPermissions? permissions,
        CancellationToken cancellationToken)
    {
        DataFieldSet dataFields = FormDefinitionFactory.GetDataFields(form);
        SubmittedValues resolved = ResolveSubmitted(scope, schema, dataFields, callerForm, values, nameof(values));

        // Every base and page INSERT is built before the form lock and before any statement on a record table, so an
        // insert the builders refuse takes no lock and sends no record statement. The lock, the permission read and
        // the response snapshot are built later, each when it is sent.
        string globalRecordId = RecordSqlBuilder.NewGlobalRecordId();
        SqlStatement baseInsert = RecordSqlBuilder.InsertBase(schema, globalRecordId, stamp);
        var pageInserts = new List<SqlStatement>(schema.Pages.Count);
        foreach (PageTableSchema page in schema.Pages)
        {
            // Every page of the form gets its row, as legacy inserts one whatever the page holds, so a page with no
            // submitted value still gets one. A page whose table or record key the catalogue lacks makes the builder
            // refuse the whole insert here, before the form lock and any record statement, so a record is never
            // committed without one of its page rows.
            pageInserts.Add(RecordSqlBuilder.InsertPage(
                page,
                globalRecordId,
                PageColumns(schema, dataFields, page),
                resolved.ByName));
        }

        // The form's exclusive write lock comes before the base INSERT, the transaction's first lock on a record table,
        // and after every base and page INSERT is built, so an insert the builders refuse takes no lock and sends no
        // record statement.
        await AcquireFormWriteLockAsync(scope, RecordOperations.Insert, schema, exclusive: true, cancellationToken)
            .ConfigureAwait(false);

        // The insert needs INSERT on the base table and on every page table. It is checked once the form's lock is
        // held, so after any wait for it, and before the base INSERT, from the permissions read on this transaction's
        // connection for the tables this transaction resolved, never from the caller's earlier read, which only turns
        // the check on. A grant revoked while the insert waited is therefore a refusal with nothing written, not a
        // failed statement.
        if (permissions is not null)
        {
            FormPermissions current = await ReadPermissionsInTransactionAsync(
                    scope,
                    RecordOperations.Insert,
                    form,
                    schema,
                    includePages: true,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!current.CanInsert(form))
            {
                return WriteDecision<RecordWriteResult>.RolledBack(RecordWriteResult.Forbidden());
            }
        }

        // OUTPUT INSERTED returns the new key uncoerced, so the converter checks it against its reader-built column.
        object? insertedKey = await SqlExecution.ExecuteScalarAsync<object>(
                scope.Connection,
                scope.Transaction,
                RecordOperations.Insert,
                baseInsert,
                options.CommandTimeoutSeconds,
                observer,
                cancellationToken,
                onDispatch: scope.MarkDmlSent)
            .ConfigureAwait(false);
        int uniqueKey = ColumnValueConverter.FromSystemInteger(RecordSqlBuilder.Require(schema.UniqueKey), insertedKey);

        foreach (SqlStatement pageInsert in pageInserts)
        {
            await ExecuteAsync(scope, RecordOperations.Insert, pageInsert, cancellationToken).ConfigureAwait(false);
        }

        RecordDto snapshot = await ReadSnapshotAsync(
                scope,
                RecordOperations.Insert,
                schema,
                dataFields,
                uniqueKey,
                globalRecordId,
                cancellationToken)
            .ConfigureAwait(false);
        return WriteDecision<RecordWriteResult>.Committed(RecordWriteResult.Success(snapshot));
    }

    // The update both UpdateAsync overloads share, inside the transaction RunWriteAsync opened. callerForm is as for
    // InsertCoreAsync.
    private async Task<WriteDecision<RecordWriteResult>> UpdateCoreAsync(
        WriteScope scope,
        FormMetadata form,
        FormSchema schema,
        FormMetadata? callerForm,
        string globalRecordId,
        IReadOnlyDictionary<string, FieldValue> submitted,
        Func<SaveStamp> createStamp,
        Func<StoredRecord, RecordValidationResult> requiredGate,
        FormPermissions? permissions,
        CancellationToken cancellationToken)
    {
        DataFieldSet dataFields = FormDefinitionFactory.GetDataFields(form);

        // Resolved before the locked read, the first base-table statement, so an unknown key reaches no record table.
        SubmittedValues resolved =
            ResolveSubmitted(scope, schema, dataFields, callerForm, submitted, nameof(submitted));

        // The form's shared write lock comes before the locked read, the transaction's first lock on a record table:
        // updates hold it together and serialise per record on the locked read, and none runs beside a create.
        await AcquireFormWriteLockAsync(scope, RecordOperations.Update, schema, exclusive: false, cancellationToken)
            .ConfigureAwait(false);

        if (await ReadBaseRowAsync(
                scope.Connection,
                scope.Transaction,
                RecordOperations.Update,
                schema,
                RecordSqlBuilder.LockBase(schema, globalRecordId),
                cancellationToken)
            .ConfigureAwait(false) is not { } locked)
        {
            return WriteDecision<RecordWriteResult>.RolledBack(RecordWriteResult.NotFound());
        }

        if (locked.RecStatus == RecordQueryRules.DeletedRecStatus)
        {
            return WriteDecision<RecordWriteResult>.RolledBack(RecordWriteResult.Deleted());
        }

        // Every later statement binds the id as the base row stores it, never the request's spelling.
        RecordRow stored = await ReadRecordAsync(
                scope.Connection,
                scope.Transaction,
                RecordOperations.Update,
                schema,
                RecordSqlBuilder.RecordByUniqueKey(schema, dataFields, locked.UniqueKey, locked.GlobalRecordId),
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(MissingRecordMessage);

        RecordValidationResult gate = requiredGate(stored.Record)
            ?? throw new InvalidOperationException(GateResultMessage);
        if (!gate.IsValid)
        {
            return WriteDecision<RecordWriteResult>.RolledBack(RecordWriteResult.ValidationFailed(gate));
        }

        // The update needs UPDATE on the base table and on every page table, and INSERT on the table of every page
        // whose row the locked read found missing, whether or not a submitted field sits on it. Both are checked after
        // the required gate and before any statement that changes data, from the permissions read on this
        // transaction's connection for the tables this transaction resolved, never from the caller's earlier read,
        // which only turns the check on. A grant revoked while the update waited on the lock is therefore a refusal
        // with nothing written, not a failed statement.
        if (permissions is not null)
        {
            FormPermissions current = await ReadPermissionsInTransactionAsync(
                    scope,
                    RecordOperations.Update,
                    form,
                    schema,
                    includePages: true,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!current.CanUpdate(form)
                || !stored.MissingPages.All(page => current.CanInsertInto(page.Page.TableName)))
            {
                return WriteDecision<RecordWriteResult>.RolledBack(RecordWriteResult.Forbidden());
            }
        }

        // Every check has decided that the update writes, and no data statement has been sent, so the stamp is created
        // here: an update the checks refuse needs none, and a stamp that cannot be made rolls back with nothing written.
        SqlStatement? baseStamp = RecordSqlBuilder.UpdateBaseStamp(
            schema,
            locked.UniqueKey,
            locked.GlobalRecordId,
            CreateStamp(createStamp));
        IReadOnlyList<PageUpdate> pageUpdates = PlanPageUpdates(schema, resolved, locked.GlobalRecordId);

        if (baseStamp is not null)
        {
            await ExecuteOnLockedRowAsync(scope, RecordOperations.Update, baseStamp, cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (PageUpdate pageUpdate in pageUpdates)
        {
            await RunPageUpdateAsync(scope, pageUpdate, cancellationToken).ConfigureAwait(false);
        }

        RecordDto snapshot = await ReadSnapshotAsync(
                scope,
                RecordOperations.Update,
                schema,
                dataFields,
                locked.UniqueKey,
                locked.GlobalRecordId,
                cancellationToken)
            .ConfigureAwait(false);
        return WriteDecision<RecordWriteResult>.Committed(RecordWriteResult.Success(snapshot));
    }

    // The login's permissions as read here, inside the write's transaction, on its connection, for the tables this
    // transaction's schema read confirmed: the base table, then, when includePages is set, each page of schema in
    // order. A table the schema read did not confirm gets no entry and so denies. Each write applies its own rule of
    // FormPermissions to the result: an insert CanInsert, an update CanUpdate and CanInsertInto each missing page, a
    // soft delete CanDelete. Each permission statement is a SELECT that touches no record table, reported under the
    // write's own operation; none marks DML, so a failure here is classified before any DML.
    private async Task<FormPermissions> ReadPermissionsInTransactionAsync(
        WriteScope scope,
        string operation,
        FormMetadata form,
        FormSchema schema,
        bool includePages,
        CancellationToken cancellationToken)
    {
        int pageCount = includePages ? schema.Pages.Count : 0;
        var requestedNames = new string[pageCount + 1];
        var resolved = new TableSchema?[pageCount + 1];
        requestedNames[0] = form.BaseTableName;
        resolved[0] = schema.BaseTable;
        for (int index = 0; index < pageCount; index++)
        {
            requestedNames[index + 1] = schema.Pages[index].Page.TableName;
            resolved[index + 1] = schema.Pages[index].Table;
        }

        return await SqlPermissionReader.ReadResolvedAsync(
                scope.Connection,
                scope.Transaction,
                operation,
                requestedNames,
                resolved,
                options.CommandTimeoutSeconds,
                observer,
                cancellationToken)
            .ConfigureAwait(false);
    }

    // The base-only soft delete, inside the transaction RunWriteAsync opened. A record already deleted is left exactly
    // as it is, stamps included, so a repeated DELETE writes nothing, and neither it nor an unknown id reads
    // permissions. A Deleted or AlreadyDeleted result carries the id as the locked read returned it, so the caller
    // names the record by its stored spelling without another statement; a Forbidden result, for an active record the
    // permission read refused, carries none.
    private async Task<WriteDecision<SoftDeleteResult>> SoftDeleteCoreAsync(
        WriteScope scope,
        FormMetadata form,
        FormSchema schema,
        string globalRecordId,
        Func<SaveStamp> createStamp,
        FormPermissions? permissions,
        CancellationToken cancellationToken)
    {
        if (await ReadBaseRowAsync(
                scope.Connection,
                scope.Transaction,
                RecordOperations.Delete,
                schema,
                RecordSqlBuilder.LockBase(schema, globalRecordId),
                cancellationToken)
            .ConfigureAwait(false) is not { } locked)
        {
            return WriteDecision<SoftDeleteResult>.RolledBack(SoftDeleteResult.NotFound());
        }

        if (locked.RecStatus == RecordQueryRules.DeletedRecStatus)
        {
            return WriteDecision<SoftDeleteResult>.RolledBack(SoftDeleteResult.AlreadyDeleted(locked.GlobalRecordId));
        }

        // The soft delete needs UPDATE on the base table alone, because it changes only the base row. It is checked
        // after the locked read, so after any wait for the row, and before the stamp and the UPDATE, from the
        // permissions read on this transaction's connection for the base table this transaction resolved, never from
        // the caller's earlier read, which only turns the check on. A grant revoked while the delete waited is
        // therefore a refusal with nothing written, not a failed statement.
        if (permissions is not null)
        {
            FormPermissions current = await ReadPermissionsInTransactionAsync(
                    scope,
                    RecordOperations.Delete,
                    form,
                    schema,
                    includePages: false,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!current.CanDelete(form))
            {
                return WriteDecision<SoftDeleteResult>.RolledBack(SoftDeleteResult.Forbidden());
            }
        }

        // Only an active record the checks let through is written, so only it needs a stamp, made before the one data
        // statement.
        SqlStatement softDelete = RecordSqlBuilder.SoftDelete(
            schema,
            locked.UniqueKey,
            locked.GlobalRecordId,
            CreateStamp(createStamp));

        await ExecuteOnLockedRowAsync(scope, RecordOperations.Delete, softDelete, cancellationToken)
            .ConfigureAwait(false);

        return WriteDecision<SoftDeleteResult>.Committed(SoftDeleteResult.Deleted(locked.GlobalRecordId));
    }

    // Takes the form's write lock inside the write's transaction (RecordSqlBuilder.AcquireFormWriteLock): exclusive
    // for a create, shared for an update, released by the commit or the rollback. It is no INSERT or UPDATE, so it
    // marks no DML and a failure here is classified before any DML. A wait that SQL Server ends without the lock, by
    // its timeout or as a deadlock victim, is unavailability with nothing written, as a deadlock or a command timeout
    // of any statement is; a command timeout while waiting reaches the caller as SqlClient's own timeout. Any other
    // refusal is unexpected, and so is a result the converter refuses: none at all, or one not of the int the
    // statement selects.
    private async Task AcquireFormWriteLockAsync(
        WriteScope scope,
        string operation,
        FormSchema schema,
        bool exclusive,
        CancellationToken cancellationToken)
    {
        object? returned = await SqlExecution.ExecuteScalarAsync<object>(
                scope.Connection,
                scope.Transaction,
                operation,
                RecordSqlBuilder.AcquireFormWriteLock(schema, exclusive),
                options.CommandTimeoutSeconds,
                observer,
                cancellationToken)
            .ConfigureAwait(false);
        int result = ColumnValueConverter.FromComputedInteger(RecordSqlBuilder.FormWriteLockResultType, returned);

        switch (result)
        {
            case >= 0:
                return;
            case RecordSqlBuilder.FormWriteLockTimedOut or RecordSqlBuilder.FormWriteLockDeadlockVictim:
                throw new DataStoreUnavailableException(operation, WriteOutcome.NotWritten);
            default:
                throw new InvalidOperationException(FormWriteLockRefusedMessage);
        }
    }

    // The one call of an update's or soft delete's stamp source, inside its transaction once the write is decided and
    // before its first data statement. An exception of the source, or the null stamp that breaks its contract, is
    // raised before any DML, so the transaction rolls back and the write reports NotWritten.
    private static SaveStamp CreateStamp(Func<SaveStamp> createStamp) =>
        createStamp() ?? throw new InvalidOperationException(StampResultMessage);

    // The response snapshot of a write, read inside its transaction after every write and before the commit: one
    // statement returns the base and page rows by the clustered key with the record's intrinsic position and count over
    // the set its RecStatus selects, so all of them describe one committed state. The DTO is built here, before the
    // commit, so nothing after the commit computes from data.
    private async Task<RecordDto> ReadSnapshotAsync(
        WriteScope scope,
        string operation,
        FormSchema schema,
        DataFieldSet dataFields,
        int uniqueKey,
        string globalRecordId,
        CancellationToken cancellationToken)
    {
        RecordRow row = await ReadRecordAsync(
                scope.Connection,
                scope.Transaction,
                operation,
                schema,
                RecordSqlBuilder.RecordSnapshot(schema, dataFields, uniqueKey, globalRecordId),
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(MissingRecordMessage);

        return row.ToDto();
    }

    // One page's submitted columns, chunk by chunk, each deciding on the row count it selects itself. The page table
    // keys on GlobalRecordId, so the first chunk changes no row or one. When it finds no page row, the row is inserted
    // holding only its key and the chunk is repeated, as legacy repairs a missing page row
    // (Epi.Core/Data/Services/CollectedDataProvider.cs:1360-1380); the repeat and every later chunk must then change
    // exactly that row. Any other count means the statement did not run as built, so the write is abandoned.
    private async Task RunPageUpdateAsync(WriteScope scope, PageUpdate pageUpdate, CancellationToken cancellationToken)
    {
        IReadOnlyList<SqlStatement> chunks = pageUpdate.Chunks;
        int changed = await ExecuteCountedAsync(scope, RecordOperations.Update, chunks[0], cancellationToken)
            .ConfigureAwait(false);
        if (changed == 0)
        {
            await ExecuteAsync(scope, RecordOperations.Update, pageUpdate.InsertKey, cancellationToken)
                .ConfigureAwait(false);
            await ExecuteOnLockedRowAsync(scope, RecordOperations.Update, chunks[0], cancellationToken)
                .ConfigureAwait(false);
        }
        else if (changed != 1)
        {
            throw new InvalidOperationException(UnexpectedRowCountMessage);
        }

        for (int index = 1; index < chunks.Count; index++)
        {
            await ExecuteOnLockedRowAsync(scope, RecordOperations.Update, chunks[index], cancellationToken)
                .ConfigureAwait(false);
        }
    }

    // An UPDATE that must change exactly the locked record's row, as the row count it selects itself reports. No row
    // means the record's rows changed under the lock; more than one means the statement did not run as built, as does
    // no count, which ExecuteCountedAsync refuses. Either way the write is abandoned and rolled back.
    private async Task ExecuteOnLockedRowAsync(
        WriteScope scope,
        string operation,
        SqlStatement statement,
        CancellationToken cancellationToken)
    {
        int changed = await ExecuteCountedAsync(scope, operation, statement, cancellationToken).ConfigureAwait(false);
        if (changed != 1)
        {
            throw new InvalidOperationException(changed == 0 ? RowNotWrittenMessage : UnexpectedRowCountMessage);
        }
    }

    // Sends one UPDATE of the write, built to end by selecting its own row count (RecordSqlBuilder.UpdateBaseStamp,
    // SoftDelete and UpdatePageColumns), and returns that count, read through the converter as the int @@ROWCOUNT is
    // (RecordSqlBuilder.RowCountType), so a statement that returned no count, or one of another type, fails rather than
    // reading as 0. The count is read from the statement's result, never from the provider's affected-row count, which
    // is -1 under NOCOUNT ON for a row changed and for none alike. The scope's DML is marked as ExecuteAsync marks it.
    private async Task<int> ExecuteCountedAsync(
        WriteScope scope,
        string operation,
        SqlStatement statement,
        CancellationToken cancellationToken)
    {
        object? count = await SqlExecution.ExecuteScalarAsync<object>(
                scope.Connection,
                scope.Transaction,
                operation,
                statement,
                options.CommandTimeoutSeconds,
                observer,
                cancellationToken,
                onDispatch: scope.MarkDmlSent)
            .ConfigureAwait(false);
        return ColumnValueConverter.FromComputedInteger(RecordSqlBuilder.RowCountType, count);
    }

    // Sends one INSERT of the write. SqlExecution marks the scope's DML as sent once the statement has passed its local
    // checks and immediately before it goes out; one those checks refuse, such as a statement on a closed connection,
    // sends nothing and marks nothing. The provider's affected-row count is not returned: it is -1 under NOCOUNT ON,
    // so no write decides on it, and an UPDATE goes through ExecuteCountedAsync. Reads go through SqlExecution
    // directly and mark nothing.
    private async Task ExecuteAsync(
        WriteScope scope,
        string operation,
        SqlStatement statement,
        CancellationToken cancellationToken) =>
        await SqlExecution.ExecuteAsync(
                scope.Connection,
                scope.Transaction,
                operation,
                statement,
                options.CommandTimeoutSeconds,
                observer,
                cancellationToken,
                onDispatch: scope.MarkDmlSent)
            .ConfigureAwait(false);

    // Runs one write transaction on connection and, when it owns the connection, releases it. A release failure after
    // a confirmed commit is reported as written (ReleaseCommittedAsync), the one outcome the store can then prove. On
    // every other path a release failure is discarded and the result or exception already decided stands unchanged,
    // whatever outcome it carries, unknown included for a failed or unconfirmed commit or a failed rollback.
    private static async Task<T> RunWriteAsync<T>(
        SqlConnection connection,
        bool ownsConnection,
        string operation,
        Func<WriteScope, Task<WriteDecision<T>>> body,
        CancellationToken cancellationToken)
    {
        WriteDecision<T> decision;
        try
        {
            decision = await RunTransactionAsync(connection, operation, body, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            if (ownsConnection)
            {
                await ReleaseQuietlyAsync(connection).ConfigureAwait(false);
            }

            throw;
        }

        if (ownsConnection)
        {
            if (decision.Commit)
            {
                await ReleaseCommittedAsync(connection, operation).ConfigureAwait(false);
            }
            else
            {
                await ReleaseQuietlyAsync(connection).ConfigureAwait(false);
            }
        }

        return decision.Value;
    }

    // One transaction at READ COMMITTED around body, which reads, gates and writes, then decides whether to commit.
    // The stage the body reached decides the write outcome of a failure (SqlErrorClassifier.ResolveWriteOutcome).
    private static async Task<WriteDecision<T>> RunTransactionAsync<T>(
        SqlConnection connection,
        string operation,
        Func<WriteScope, Task<WriteDecision<T>>> body,
        CancellationToken cancellationToken)
    {
        SqlTransaction transaction;
        try
        {
            // The start observes the request's token and leaves the request's thread free (BeginTransactionAsync); a
            // cancellation or failure here sends no DML, so it is classified at BeforeDml.
            transaction = await BeginTransactionAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Exception failure = BuildFailure(
                exception,
                operation,
                WriteStage.BeforeDml,
                rollbackCompleted: false,
                rollbackError: null,
                cancellationToken);
            if (ReferenceEquals(failure, exception))
            {
                throw;
            }

            throw failure;
        }

        var scope = new WriteScope(connection, transaction);
        WriteDecision<T> decision;
        try
        {
            decision = await body(scope).ConfigureAwait(false);
            if (decision.Commit)
            {
                // The commit and the rollback ignore the request's token: once the decision is made, an abort must not
                // leave the transaction's fate to whichever call it interrupts.
                scope.MarkCommitting();
                await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            else if (await RollbackAsync(scope).ConfigureAwait(false) is { } rollbackError)
            {
                // A rollback that fails is the write's failure: raised here, it reaches the catch below, which
                // classifies it with the stage the body reached and the rollback failure the scope recorded.
                ExceptionDispatchInfo.Capture(rollbackError).Throw();
            }
        }
        catch (Exception exception) when (ReferenceEquals(exception, scope.ContractViolation))
        {
            // A broken argument is refused before any base- or page-table statement, so the rollback has nothing to
            // undo, and the ArgumentException reaches the caller unwrapped, carrying only the type and SQL error
            // number of a rollback that failed.
            Exception? rollbackError = await RollbackAsync(scope).ConfigureAwait(false);
            SqlErrorClassifier.AttachRollbackFailure(exception, rollbackError);
            await ReleaseQuietlyAsync(transaction).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            Exception failure = await FailAsync(scope, operation, exception, cancellationToken).ConfigureAwait(false);
            await ReleaseQuietlyAsync(transaction).ConfigureAwait(false);
            if (ReferenceEquals(failure, exception))
            {
                throw;
            }

            throw failure;
        }

        if (decision.Commit)
        {
            await ReleaseCommittedAsync(transaction, operation).ConfigureAwait(false);
        }
        else
        {
            await ReleaseQuietlyAsync(transaction).ConfigureAwait(false);
        }

        return decision;
    }

    // Rolls back a failed write unless the commit or the rollback was already called, then builds the exception the
    // caller throws from the stage and the rollback the scope recorded. The rollback's own failure never replaces the
    // original: it is only noted on the exception raised for it.
    private static async Task<Exception> FailAsync(
        WriteScope scope,
        string operation,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (!scope.CommitAttempted && !scope.RollbackAttempted)
        {
            await RollbackAsync(scope).ConfigureAwait(false);
        }

        return BuildFailure(
            exception,
            operation,
            scope.Stage,
            scope.RollbackCompleted,
            scope.RollbackError,
            cancellationToken);
    }

    // The exception a failed write raises, which keeps the original failure's classification: the original
    // cancellation itself, or one with the caller's token and the classified SQL error number for SqlClient's
    // acknowledgement of a cancelled command; otherwise the classified store exception carrying the outcome the stage
    // and the rollback prove. Either also carries the type and SQL error number of a rollback that failed, never its
    // message. Internal for its unit tests.
    internal static Exception BuildFailure(
        Exception exception,
        string operation,
        WriteStage stage,
        bool rollbackCompleted,
        Exception? rollbackError,
        CancellationToken cancellationToken)
    {
        (SqlFailure failure, Exception inner) = ClassifyOriginal(exception, cancellationToken);
        Exception result = failure.Kind == SqlFailureKind.Cancelled
            ? SqlErrorClassifier.ToCancellation(failure, exception, cancellationToken)
            : SqlErrorClassifier.ToDataStoreException(
                failure,
                operation,
                SqlErrorClassifier.ResolveWriteOutcome(stage, rollbackCompleted, failure),
                inner);
        SqlErrorClassifier.AttachRollbackFailure(result, rollbackError);
        return result;
    }

    // A store exception a collaborator already raised keeps the class it was given and its SQL error number, falling
    // back to the number of its inner failure, and is raised again with the outcome this transaction's stage proves;
    // any other failure is classified here.
    private static (SqlFailure Failure, Exception Inner) ClassifyOriginal(
        Exception exception,
        CancellationToken cancellationToken) =>
        exception switch
        {
            DataStoreUnavailableException unavailable => (
                new SqlFailure(
                    SqlFailureKind.Unavailable,
                    unavailable.SqlErrorNumber ?? InnerErrorNumber(unavailable, cancellationToken)),
                unavailable.InnerException ?? unavailable),
            DataStoreWriteException failed => (
                new SqlFailure(
                    SqlFailureKind.Unexpected,
                    failed.SqlErrorNumber ?? InnerErrorNumber(failed, cancellationToken)),
                failed.InnerException ?? failed),
            _ => (SqlErrorClassifier.Classify(exception, cancellationToken), exception),
        };

    private static int? InnerErrorNumber(Exception storeException, CancellationToken cancellationToken) =>
        storeException.InnerException is { } inner
            ? SqlErrorClassifier.Classify(inner, cancellationToken).SqlErrorNumber
            : null;

    // The slots of the transaction starts that may wait on the server at once, process-wide (BeginTransactionAsync).
    private static readonly SemaphoreSlim TransactionStarts = CreateTransactionStartGate();

    // Half the thread pool's configured minimum of worker threads, and at least one. Each start blocks the pool thread
    // it runs on for one round trip, so the bound keeps at least half of the workers the pool supplies without delay
    // free of those waits however many writes arrive together; the writes over it wait for a slot holding no thread.
    private static SemaphoreSlim CreateTransactionStartGate()
    {
        ThreadPool.GetMinThreads(out int workerThreads, out _);
        int slots = Math.Max(1, workerThreads / 2);
        return new SemaphoreSlim(slots, slots);
    }

    // Begins the write's one transaction at READ COMMITTED without blocking the caller's thread. SqlClient overrides
    // only the synchronous BeginDbTransaction, so DbConnection.BeginTransactionAsync would make that same blocking
    // round trip on the request's thread; here it runs on a pool thread, never more of them at once than
    // TransactionStarts allows. The token is observed until the provider's start is entered: before the slot, while
    // waiting for it, while the work item waits for a pool thread (a work item cancelled before it runs never runs, and
    // its task ends cancelled), and as the work item's first step. Once entered, the start runs to its end and is
    // awaited, never abandoned, because an abandoned start would leave the connection mid-begin; a cancellation after
    // that reaches the first statement, and the rollback that follows undoes the start. The awaited task ends only when
    // the start has returned or thrown, or when its work item was cancelled before running, so the slot is never
    // released while a start is still running.
    private static async Task<SqlTransaction> BeginTransactionAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await TransactionStarts.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(
                    () =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        return connection.BeginTransaction(IsolationLevel.ReadCommitted);
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            TransactionStarts.Release();
        }
    }

    // Releases a transaction or connection after a confirmed commit. The port requires a failure here to be reported
    // as written, the one outcome the store can prove once the commit returned; the request's token is not consulted,
    // because a release cannot be the client's abort.
    private static async Task ReleaseCommittedAsync(IAsyncDisposable resource, string operation)
    {
        try
        {
            await resource.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            SqlFailure failure = SqlErrorClassifier.Classify(exception, CancellationToken.None);
            throw SqlErrorClassifier.ToDataStoreException(failure, operation, WriteOutcome.Written, exception);
        }
    }

    // Releases a transaction or connection when nothing was committed, or when a failure is already on its way out.
    private static async Task ReleaseQuietlyAsync(IAsyncDisposable resource)
    {
        try
        {
            await resource.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Discarded by design: the result or the exception already decided stands, and a release error must not
            // replace it. Nothing was committed on this path, or the exception already reports what is known.
        }
    }

    // The one rollback of a write, for every path that rolls back. It ignores the request's token, as the commit does,
    // and never throws: it records on the scope that it was attempted and whether it completed, or what it threw, and
    // returns that failure, or null when it completed. The caller decides what the failure means for the write.
    private static async Task<Exception?> RollbackAsync(WriteScope scope)
    {
        scope.MarkRollbackAttempted();
        try
        {
            await scope.Transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            scope.MarkRollbackFailed(error);
            return error;
        }

        scope.MarkRollbackCompleted();
        return null;
    }

    // The contract check of a write's values inside its transaction (ResolveValues), and whose error a refusal is.
    // Without callerForm, the transaction writes with the caller's own metadata, so a refusal is the caller's broken
    // argument (WriteScope.Check). With it, the transaction read the metadata again after the caller validated the
    // values against callerForm, so a refusal is the caller's broken argument only when callerForm refuses the values
    // too, by the same rules (ApplyValueRules, which sends nothing); that refusal is then raised. When callerForm
    // accepts them, the metadata changed after the caller read it. That is no contract violation but a failure before
    // any record statement, which RunTransactionAsync rolls back and reports as writing nothing, with the transaction's
    // refusal as its inner exception.
    private static SubmittedValues ResolveSubmitted(
        WriteScope scope,
        FormSchema schema,
        DataFieldSet dataFields,
        FormMetadata? callerForm,
        IReadOnlyDictionary<string, FieldValue> values,
        string paramName)
    {
        if (callerForm is null)
        {
            return scope.Check(() => ResolveValues(schema, dataFields, values, paramName));
        }

        try
        {
            return ResolveValues(schema, dataFields, values, paramName);
        }
        catch (ArgumentException refused)
        {
            DataFieldSet callerFields = FormDefinitionFactory.GetDataFields(callerForm);
            _ = scope.Check(() => ApplyValueRules(
                callerFields,
                values,
                paramName,
                (callerField, _) => callerField.Column,
                column => column));
            throw new InvalidOperationException(MetadataChangedMessage, refused);
        }
    }

    // The contract check of a write's values against the metadata and catalogue schema the transaction uses, run
    // before any base- or page-table statement, by the rules of ApplyValueRules with each field's catalogue column.
    // The result holds the values under their canonical names and their columns in data-field order.
    private static SubmittedValues ResolveValues(
        FormSchema schema,
        DataFieldSet dataFields,
        IReadOnlyDictionary<string, FieldValue> values,
        string paramName)
    {
        var byName = new Dictionary<string, (ProjectedColumn Column, FieldValue Value)>(
            values.Count,
            StringComparer.Ordinal);
        foreach ((DataFieldMetadata field, ProjectedColumn column, FieldValue value) in ApplyValueRules(
                     dataFields,
                     values,
                     paramName,
                     (dataField, ordinal) => RecordSqlBuilder.ResolveDataColumn(schema, dataField, ordinal),
                     projected => projected.Column.Metadata))
        {
            byName.Add(field.Name, (column, value));
        }

        var exact = new Dictionary<string, FieldValue>(byName.Count, StringComparer.Ordinal);
        var columns = new List<(ProjectedColumn Column, FieldValue Value)>(byName.Count);
        foreach (DataFieldMetadata field in dataFields)
        {
            if (byName.TryGetValue(field.Name, out (ProjectedColumn Column, FieldValue Value) entry))
            {
                exact.Add(field.Name, entry.Value);
                columns.Add(entry);
            }
        }

        return new SubmittedValues(new ReadOnlyDictionary<string, FieldValue>(exact), columns.AsReadOnly());
    }

    // The contract rules of a write's values, stated once for both checks that apply them: the transaction's
    // (ResolveValues, with the catalogue column) and the caller's (ResolveSubmitted, with the column the caller's
    // metadata names). Each key resolves to a writable data field of dataFields (exact name, else the one canonical
    // name equal to it ignoring case); no two keys name the same field ignoring case, as the validator refuses them;
    // and each value binds to the column resolveColumn gives its field, typed as columnType describes it, exactly as
    // the write statement will bind it, so a value of the wrong kind or one the column cannot hold is refused. The
    // first broken rule throws an ArgumentException for paramName with a fixed message. Otherwise the entries come
    // back in values order, each with its field, its column, resolved with its 0-based place among them, and its
    // value.
    private static List<(DataFieldMetadata Field, TColumn Column, FieldValue Value)> ApplyValueRules<TColumn>(
        DataFieldSet dataFields,
        IReadOnlyDictionary<string, FieldValue> values,
        string paramName,
        Func<DataFieldMetadata, int, TColumn> resolveColumn,
        Func<TColumn, ColumnMetadata> columnType)
    {
        var entries = new List<(DataFieldMetadata Field, TColumn Column, FieldValue Value)>(values.Count);
        var seen = new HashSet<string>(values.Count, StringComparer.OrdinalIgnoreCase);
        foreach ((string name, FieldValue value) in values)
        {
            if (name is null || !dataFields.TryGet(name, out DataFieldMetadata? field))
            {
                throw new ArgumentException(UnknownFieldMessage, paramName);
            }

            if (field.Field.IsReadOnly)
            {
                throw new ArgumentException(ReadOnlyFieldMessage, paramName);
            }

            if (!seen.Add(field.Name))
            {
                throw new ArgumentException(RepeatedFieldMessage, paramName);
            }

            TColumn column = resolveColumn(field, entries.Count);
            _ = ColumnValueConverter.ToParameter(ProbeParameterName, columnType(column), field.FieldType, value);
            entries.Add((field, column, value));
        }

        return entries;
    }

    // Every data-field column of one page, in data-field order: the columns a new record's page row names.
    private static List<ProjectedColumn> PageColumns(FormSchema schema, DataFieldSet dataFields, PageTableSchema page)
    {
        var columns = new List<ProjectedColumn>();
        foreach (DataFieldMetadata field in dataFields)
        {
            if (field.Page.PageId == page.Page.PageId)
            {
                columns.Add(RecordSqlBuilder.ResolveDataColumn(schema, field, columns.Count));
            }
        }

        return columns;
    }

    // The page statements of an update, in page order: for each page holding a submitted column, its chunked UPDATE of
    // the submitted columns only, and the key-only INSERT that repairs a missing row. Built before any data statement.
    private static IReadOnlyList<PageUpdate> PlanPageUpdates(
        FormSchema schema,
        SubmittedValues resolved,
        string storedGlobalRecordId)
    {
        var updates = new List<PageUpdate>();
        foreach (PageTableSchema page in schema.Pages)
        {
            (ProjectedColumn Column, FieldValue Value)[] pageColumns = resolved.Columns
                .Where(entry => entry.Column.Field.Page.PageId == page.Page.PageId)
                .ToArray();
            if (pageColumns.Length == 0)
            {
                continue;
            }

            updates.Add(new PageUpdate(
                RecordSqlBuilder.UpdatePageColumns(page, storedGlobalRecordId, pageColumns),
                RecordSqlBuilder.InsertPageKey(page, storedGlobalRecordId)));
        }

        return updates.AsReadOnly();
    }

    // Reads the first row of a record SELECT built from schema, or null when it returns none.
    private Task<RecordRow?> ReadRecordAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        string operation,
        FormSchema schema,
        RecordSelect select,
        CancellationToken cancellationToken) =>
        SqlExecution.ReadAsync<RecordRow?>(
            connection,
            transaction,
            operation,
            select.Statement,
            options.CommandTimeoutSeconds,
            observer,
            cancellationToken,
            async (reader, token) =>
                await reader.ReadAsync(token).ConfigureAwait(false) ? ReadRecordRow(reader, schema, select) : null);

    // Reads the first row of the locked base-row read (LockBase) built from schema, or null when it returns none.
    private Task<BaseRow?> ReadBaseRowAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        string operation,
        FormSchema schema,
        SqlStatement statement,
        CancellationToken cancellationToken) =>
        SqlExecution.ReadAsync<BaseRow?>(
            connection,
            transaction,
            operation,
            statement,
            options.CommandTimeoutSeconds,
            observer,
            cancellationToken,
            async (reader, token) =>
                await reader.ReadAsync(token).ConfigureAwait(false)
                    ? new BaseRow(
                        ReadSystemInteger(reader, schema.UniqueKey, RecordSqlBuilder.BaseRowUniqueKeyOrdinal),
                        ReadSystemInteger(reader, schema.RecStatus, RecordSqlBuilder.BaseRowRecStatusOrdinal),
                        ReadSystemText(reader, schema.GlobalRecordId, RecordSqlBuilder.BaseRowGlobalRecordIdOrdinal))
                    : null);

    // Runs a COUNT_BIG statement and reads its raw bigint through the converter, which narrows it to the int the port
    // reports, failing rather than wrapping.
    private async Task<int> ReadCountAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        string operation,
        SqlStatement statement,
        CancellationToken cancellationToken)
    {
        object? count = await SqlExecution.ExecuteScalarAsync<object>(
                connection,
                transaction,
                operation,
                statement,
                options.CommandTimeoutSeconds,
                observer,
                cancellationToken)
            .ConfigureAwait(false);
        return ColumnValueConverter.FromComputedInteger(RecordSqlBuilder.CountType, count);
    }

    // One row of a record SELECT built from schema, by the ordinals its RecordSelect records: the base system columns,
    // the intrinsic coordinates when the statement computes them, the page-row presence keys and every data-field
    // value. A page with no row for the record reads NULL in each of its columns, so each of its fields is
    // FieldValue.Null.
    private static RecordRow ReadRecordRow(DbDataReader reader, FormSchema schema, RecordSelect select)
    {
        var record = new StoredRecord(
            ReadSystemText(reader, schema.GlobalRecordId, RecordSqlBuilder.RecordGlobalRecordIdOrdinal),
            ReadSystemInteger(reader, schema.UniqueKey, RecordSqlBuilder.RecordUniqueKeyOrdinal),
            ReadSystemInteger(reader, schema.RecStatus, RecordSqlBuilder.RecordRecStatusOrdinal),
            ReadValues(reader, select.Columns));

        var missingPages = new List<PageTableSchema>();
        foreach (PagePresence presence in select.Pages)
        {
            if (reader.IsDBNull(presence.Ordinal))
            {
                missingPages.Add(presence.Page);
            }
        }

        int? position = null;
        int? count = null;
        if (select.HasIntrinsicCoordinates)
        {
            position = ReadComputedInteger(
                reader,
                RecordSqlBuilder.RecordIntrinsicPositionType,
                RecordSqlBuilder.RecordIntrinsicPositionOrdinal);
            count = ReadComputedInteger(
                reader,
                RecordSqlBuilder.RecordIntrinsicCountType,
                RecordSqlBuilder.RecordIntrinsicCountOrdinal);
        }

        return new RecordRow(record, missingPages.AsReadOnly(), position, count);
    }

    // The values of projected data-field columns, keyed by canonical name in the order of the columns, each read
    // exactly as stored through the converter of its reader-built column.
    private static IReadOnlyDictionary<string, FieldValue> ReadValues(
        DbDataReader reader,
        IReadOnlyList<ProjectedColumn> columns)
    {
        OrderedDictionary<string, FieldValue> values = RecordQueryRules.CreateValuesMap(columns.Count);
        foreach (ProjectedColumn column in columns)
        {
            object? stored = reader.IsDBNull(column.Ordinal) ? null : reader.GetValue(column.Ordinal);
            values.TryAdd(
                column.Field.Name,
                ColumnValueConverter.FromDbValue(column.Column.Metadata, column.Field.FieldType, stored));
        }

        return new ReadOnlyDictionary<string, FieldValue>(values);
    }

    // The system integer (UniqueKey or RecStatus) at ordinal, read raw and converted for the reader-built column the
    // statement projected there.
    private static int ReadSystemInteger(DbDataReader reader, ColumnSchema? column, int ordinal) =>
        ColumnValueConverter.FromSystemInteger(RecordSqlBuilder.Require(column), reader.GetValue(ordinal));

    // The system text (GlobalRecordId) at ordinal, read raw and converted, exactly as stored, for the reader-built
    // column the statement projected there.
    private static string ReadSystemText(DbDataReader reader, ColumnSchema? column, int ordinal) =>
        ColumnValueConverter.FromSystemText(RecordSqlBuilder.Require(column), reader.GetValue(ordinal));

    // The integer the statement computed at ordinal, read raw and converted for the native type its builder declares.
    private static int ReadComputedInteger(DbDataReader reader, DbType nativeType, int ordinal) =>
        ColumnValueConverter.FromComputedInteger(nativeType, reader.GetValue(ordinal));

    // The fixture overloads take a schema the caller composed, so they confirm it describes the form they are given.
    private static void RequireFixture(SqlConnection connection, FormMetadata form, FormSchema schema)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(schema);

        if (connection.State != ConnectionState.Open)
        {
            throw new InvalidOperationException(ConnectionNotOpenMessage);
        }

        if (schema.ViewId != form.ViewId)
        {
            throw new ArgumentException(MismatchedSchemaMessage, nameof(schema));
        }
    }

    // What a write body decided once its gates ran: commit the value it built inside the transaction, or roll back
    // and return a result that wrote nothing.
    private readonly record struct WriteDecision<T>(T Value, bool Commit)
    {
        public static WriteDecision<T> Committed(T value) => new(value, Commit: true);

        public static WriteDecision<T> RolledBack(T value) => new(value, Commit: false);
    }

    // The state of one write transaction: whether an INSERT or UPDATE was dispatched, whether the commit or the
    // rollback was tried and how the rollback ended, and the one ArgumentException its contract check raised, which
    // alone leaves the repository unwrapped. It belongs to one write, whose steps run one after another: no two run at
    // once, though an awaited step may resume on another thread, so the state needs no synchronisation.
    private sealed class WriteScope(SqlConnection connection, SqlTransaction transaction)
    {
        public SqlConnection Connection { get; } = connection;

        public SqlTransaction Transaction { get; } = transaction;

        // Set when the first INSERT or UPDATE is dispatched, and never cleared.
        public bool DmlSent { get; private set; }

        public bool CommitAttempted { get; private set; }

        public bool RollbackAttempted { get; private set; }

        public bool RollbackCompleted { get; private set; }

        // The failure of the rollback, when it was attempted and did not complete.
        public Exception? RollbackError { get; private set; }

        // How far the write got, decided by what it dispatched: a commit of a transaction that sent no INSERT or UPDATE
        // is still BeforeDml, because it held nothing the server could keep.
        public WriteStage Stage =>
            !DmlSent ? WriteStage.BeforeDml : CommitAttempted ? WriteStage.Committing : WriteStage.DmlSent;

        public ArgumentException? ContractViolation { get; private set; }

        // Runs the contract check, marking an ArgumentException it raises as the caller's broken argument.
        public T Check<T>(Func<T> check)
        {
            try
            {
                return check();
            }
            catch (ArgumentException exception)
            {
                ContractViolation = exception;
                throw;
            }
        }

        // Called by SqlExecution immediately before each INSERT or UPDATE is sent, after its local checks.
        public void MarkDmlSent() => DmlSent = true;

        // Called immediately before Commit.
        public void MarkCommitting() => CommitAttempted = true;

        // Called immediately before Rollback.
        public void MarkRollbackAttempted() => RollbackAttempted = true;

        // Called when Rollback returned without error.
        public void MarkRollbackCompleted() => RollbackCompleted = true;

        // Called when Rollback threw, with what it threw.
        public void MarkRollbackFailed(Exception error) => RollbackError = error;
    }

    // One record row as read: the stored record, the joined pages with no row for it, and its intrinsic coordinates
    // when the statement computed them.
    private sealed record RecordRow(
        StoredRecord Record,
        IReadOnlyList<PageTableSchema> MissingPages,
        int? Position,
        int? Count)
    {
        public RecordDto ToDto() =>
            Position is int position && Count is int count
                ? RecordDto.From(Record, position, count)
                : throw new InvalidOperationException(MissingCoordinatesMessage);
    }

    // A base row as a lookup by id returns it, its GlobalRecordId spelled as stored.
    private sealed record BaseRow(int UniqueKey, int RecStatus, string GlobalRecordId);

    // A record's placement as SetPositionLookup returns it: its RecStatus, which decides membership of the selected
    // set, the position it holds there when it is a member, and the selected-set count, all from one statement.
    private sealed record SetPlacement(int RecStatus, int Position, int Count);

    // A write's values after the contract check: by canonical name, and with their columns in data-field order.
    private sealed record SubmittedValues(
        IReadOnlyDictionary<string, FieldValue> ByName,
        IReadOnlyList<(ProjectedColumn Column, FieldValue Value)> Columns);

    // One page's update statements and the key-only insert that repairs its missing row.
    private sealed record PageUpdate(IReadOnlyList<SqlStatement> Chunks, SqlStatement InsertKey);
}
