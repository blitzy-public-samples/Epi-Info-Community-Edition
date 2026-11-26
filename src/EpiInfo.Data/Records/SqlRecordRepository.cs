using System.Collections.ObjectModel;
using System.Data;
using System.Data.Common;
using EpiInfo.Data.Metadata;
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
/// The record half of the SQL Server store: every record read and every record write of <see cref="IDataStore"/>,
/// over the statements <see cref="RecordSqlBuilder"/> and <see cref="SearchSqlBuilder"/> build. Each method has the
/// signature of its <see cref="IDataStore"/> member, so <c>SqlServerDataStore</c> forwards to it unchanged.
/// </summary>
/// <remarks>
/// <para>
/// <b>Metadata.</b> Every public method opens one connection and reads the form's metadata and catalogue schema again
/// on it through <see cref="SqlMetadataReader.ReadFormAsync"/>, by <see cref="FormMetadata.ViewId"/>, inside the
/// transaction for a write. Nothing is cached, so every identifier a statement names comes from that read.
/// </para>
/// <para>
/// <b>Reads</b> send only <c>SELECT</c> statements, in no transaction. Their failures propagate unchanged for
/// <c>SqlServerDataStore</c> to classify.
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
/// <b>Contract violations.</b> A <c>values</c> key that names no writable data field, a repeated field, or a value
/// its field and column cannot hold is an <see cref="ArgumentException"/>, raised before any base- or page-table
/// statement is sent, after the transaction is rolled back, and never wrapped.
/// </para>
/// <para>
/// The instance holds only immutable collaborators and is safe to share across threads. It logs nothing, and every
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
    private const string MismatchedSchemaMessage = "The form schema does not describe the form.";
    private const string ConnectionNotOpenMessage = "The connection must be open.";
    private const string MissingSystemValueMessage = "A stored system column holds no value.";
    private const string UnexpectedSystemValueMessage = "A stored system column holds a value of an unexpected type.";
    private const string MissingRecordMessage = "The written record could not be read back inside its transaction.";
    private const string MissingCoordinatesMessage = "The record read carries no intrinsic position and count.";
    private const string RowNotWrittenMessage = "A write statement affected no row of the locked record.";
    private const string GateResultMessage = "The required gate returned no result.";

    // The name a submitted value is bound under to test it against its column; that parameter is never sent.
    private const string ProbeParameterName = "@p0";

    private readonly SqlConnectionFactory connections =
        connections ?? throw new ArgumentNullException(nameof(connections));

    private readonly SqlMetadataReader metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));

    private readonly DatabaseOptions options = options ?? throw new ArgumentNullException(nameof(options));

    private readonly ISqlCommandObserver observer = observer ?? throw new ArgumentNullException(nameof(observer));

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
            if (await metadata.ReadFormAsync(connection, transaction: null, form.ViewId, cancellationToken)
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
            if (await metadata.ReadFormAsync(connection, transaction: null, form.ViewId, cancellationToken)
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
            if (await metadata.ReadFormAsync(connection, transaction: null, form.ViewId, cancellationToken)
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
    /// still placed.
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
            if (await metadata.ReadFormAsync(connection, transaction: null, form.ViewId, cancellationToken)
                    .ConfigureAwait(false) is not { } read)
            {
                return null;
            }

            FormSchema schema = read.Schema;
            if (await ReadBaseRowAsync(
                    connection,
                    transaction: null,
                    RecordOperations.SetPosition,
                    RecordSqlBuilder.SetPositionLookup(schema, globalRecordId),
                    cancellationToken)
                .ConfigureAwait(false) is not { } baseRow)
            {
                return null;
            }

            int count = await ReadCountAsync(
                    connection,
                    transaction: null,
                    RecordOperations.SetPosition,
                    RecordSqlBuilder.Count(schema, includeDeleted),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!RecordQueryRules.IsInSelectedSet(baseRow.RecStatus, includeDeleted))
            {
                return new NavigationPosition(null, count);
            }

            int position = await ReadCountAsync(
                    connection,
                    transaction: null,
                    RecordOperations.SetPosition,
                    RecordSqlBuilder.Position(schema, baseRow.UniqueKey, includeDeleted),
                    cancellationToken)
                .ConfigureAwait(false);
            return new NavigationPosition(position, count);
        }
    }

    /// <summary>
    /// Reads one page of the records list, as <see cref="IDataStore.ListAsync"/>: the selected set, filtered by the
    /// grid filter, ordered by <c>UniqueKey</c> and paged, holding the grid columns the form's data fields select.
    /// </summary>
    /// <param name="form">The form's metadata.</param>
    /// <param name="query">The coerced list parameters.</param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>
    /// The page's summaries with the total of the filtered set; no items and a total of 0 when the form is no longer
    /// present. A valid page past the end has no items and the correct total.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// The filter fails <see cref="RecordQueryRules.IsRepresentableFilter"/>, raised before any base- or page-table
    /// statement is sent.
    /// </exception>
    public async Task<RecordPage> ListAsync(FormMetadata form, RecordListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(query);

        SqlConnection connection = await connections
            .OpenAsync(RecordOperations.List, cancellationToken)
            .ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            if (await metadata.ReadFormAsync(connection, transaction: null, form.ViewId, cancellationToken)
                    .ConfigureAwait(false) is not { } read)
            {
                return new RecordPage(Array.Empty<RecordSummary>(), 0, query.Page, query.PageSize);
            }

            IReadOnlyList<DataFieldMetadata> gridColumns =
                RecordQueryRules.GetGridColumns(FormDefinitionFactory.GetDataFields(read.Form));

            // Both statements are built before either is sent, so a filter the builders refuse sends nothing.
            ListSelect page = SearchSqlBuilder.ListPage(read.Schema, gridColumns, query);
            SqlStatement total = SearchSqlBuilder.ListCount(read.Schema, gridColumns, query);

            IReadOnlyList<RecordSummary> items = await SqlExecution.ReadAsync(
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
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                        {
                            summaries.Add(new RecordSummary(
                                ReadText(reader, SearchSqlBuilder.IdOrdinal),
                                ReadInt32(reader, SearchSqlBuilder.UniqueKeyOrdinal),
                                ReadInt32(reader, SearchSqlBuilder.RecStatusOrdinal),
                                ReadValues(reader, page.Columns)));
                        }

                        return (IReadOnlyList<RecordSummary>)summaries.AsReadOnly();
                    })
                .ConfigureAwait(false);

            int count = await ReadCountAsync(
                    connection,
                    transaction: null,
                    RecordOperations.List,
                    total,
                    cancellationToken)
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
    public async Task<SearchResponse> SearchAsync(
        FormMetadata form,
        IReadOnlyList<SearchCondition> conditions,
        bool includeDeleted,
        string? excludeGlobalRecordId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(conditions);

        SqlConnection connection = await connections
            .OpenAsync(RecordOperations.Search, cancellationToken)
            .ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            if (await metadata.ReadFormAsync(connection, transaction: null, form.ViewId, cancellationToken)
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
                                total = ReadInt32(reader, SearchSqlBuilder.TotalOrdinal);
                            }

                            results.Add(new SearchResult(
                                ReadText(reader, SearchSqlBuilder.IdOrdinal),
                                ReadInt32(reader, SearchSqlBuilder.UniqueKeyOrdinal),
                                ReadInt32(reader, SearchSqlBuilder.RecStatusOrdinal),
                                ReadInt32(reader, SearchSqlBuilder.PositionOrdinal),
                                ReadValues(reader, select.Columns)));
                        }

                        return ((IReadOnlyList<SearchResult>)results.AsReadOnly(), total);
                    })
                .ConfigureAwait(false);

            return SearchResponse.Create(items, matches);
        }
    }


    /// <summary>
    /// Inserts a new active record, as <see cref="IDataStore.InsertAsync"/>: the base row with one save stamp for both
    /// stamp pairs, one row on every page in page order holding every writable data column (<c>NULL</c> when absent),
    /// then the response snapshot, all in one transaction that then commits.
    /// </summary>
    /// <param name="form">The form's metadata, as the store's metadata read returned it.</param>
    /// <param name="values">The validated values of writable data fields, keyed by canonical name.</param>
    /// <param name="stamp">The save stamp of this write.</param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>The new record as committed, with its intrinsic position and count.</returns>
    /// <exception cref="ArgumentException">
    /// The form is no longer in the metadata, or <paramref name="values"/> breaks the contract (class remarks); raised
    /// before any base- or page-table statement is sent.
    /// </exception>
    /// <exception cref="DataStoreUnavailableException">The database was unavailable; the outcome says what is known.</exception>
    /// <exception cref="DataStoreWriteException">Any other failure; the outcome says what is known.</exception>
    public async Task<RecordDto> InsertAsync(
        FormMetadata form,
        IReadOnlyDictionary<string, FieldValue> values,
        SaveStamp stamp,
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
                    // RecordService reads the metadata before it calls the store, so a form that has vanished since
                    // is a broken precondition rather than a record that is not found.
                    (FormMetadata Form, FormSchema Schema) read = await metadata
                            .ReadFormAsync(scope.Connection, scope.Transaction, form.ViewId, cancellationToken)
                            .ConfigureAwait(false)
                        ?? throw scope.Reject(new ArgumentException(UnknownFormMessage, nameof(form)));

                    return await InsertCoreAsync(scope, read.Form, read.Schema, values, stamp, cancellationToken)
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
    /// In order: the locked base read (<c>NotFound</c>, or <c>Deleted</c> for <c>RecStatus</c> 0); the page-row read;
    /// the permission check of every missing page row; the required gate; the base last-save stamp; for each page
    /// holding a submitted field, in page order, the submitted columns, inserting the page row first when the update
    /// finds none, as legacy repairs it; then the snapshot and the commit. Concurrent saves of one record serialise
    /// on the base-row lock and the later commit wins for a field both submitted, with no row-version check.
    /// </remarks>
    /// <param name="form">The form's metadata, as the store's metadata read returned it.</param>
    /// <param name="globalRecordId">The record's base-table <c>GlobalRecordId</c>, matched exactly.</param>
    /// <param name="submitted">The validated values of the writable data fields the request submitted.</param>
    /// <param name="stamp">The save stamp of this write.</param>
    /// <param name="requiredGate">The required-field check of the stored record overlaid with the submitted values.</param>
    /// <param name="permissions">The login's per-table permissions, or <see langword="null"/> for no check.</param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>
    /// <see cref="RecordWriteStatus.Success"/> with the updated record when the transaction committed; any other
    /// status, the form no longer being present included (<see cref="RecordWriteStatus.NotFound"/>), when it rolled
    /// back with nothing written.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="submitted"/> breaks the contract (class remarks); raised before any base- or page-table
    /// statement is sent.
    /// </exception>
    /// <exception cref="DataStoreUnavailableException">The database was unavailable; the outcome says what is known.</exception>
    /// <exception cref="DataStoreWriteException">
    /// Any other failure, an exception of <paramref name="requiredGate"/> included, which reports
    /// <see cref="WriteOutcome.NotWritten"/> with no SQL error number.
    /// </exception>
    public async Task<RecordWriteResult> UpdateAsync(
        FormMetadata form,
        string globalRecordId,
        IReadOnlyDictionary<string, FieldValue> submitted,
        SaveStamp stamp,
        Func<StoredRecord, RecordValidationResult> requiredGate,
        FormPermissions? permissions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(globalRecordId);
        ArgumentNullException.ThrowIfNull(submitted);
        ArgumentNullException.ThrowIfNull(stamp);
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
                            globalRecordId,
                            submitted,
                            stamp,
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
    /// with the last-save stamp, in one transaction. Page rows are never touched and nothing cascades.
    /// </summary>
    /// <param name="form">The form's metadata, as the store's metadata read returned it.</param>
    /// <param name="globalRecordId">The record's base-table <c>GlobalRecordId</c>, matched exactly.</param>
    /// <param name="stamp">The save stamp, written only when the record is active.</param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>
    /// <see cref="SoftDeleteResult.Deleted"/> when the transaction committed; <see cref="SoftDeleteResult.AlreadyDeleted"/>
    /// with nothing written, stamps included, for a record with <c>RecStatus</c> 0; <see cref="SoftDeleteResult.NotFound"/>
    /// for an unknown id or a form no longer present.
    /// </returns>
    /// <exception cref="DataStoreUnavailableException">The database was unavailable; the outcome says what is known.</exception>
    /// <exception cref="DataStoreWriteException">Any other failure; the outcome says what is known.</exception>
    public async Task<SoftDeleteResult> SoftDeleteAsync(
        FormMetadata form,
        string globalRecordId,
        SaveStamp stamp,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(globalRecordId);
        ArgumentNullException.ThrowIfNull(stamp);

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
                        return WriteDecision<SoftDeleteResult>.RolledBack(SoftDeleteResult.NotFound);
                    }

                    return await SoftDeleteCoreAsync(scope, read.Schema, globalRecordId, stamp, cancellationToken)
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
    /// and observer as <see cref="InsertAsync(FormMetadata, IReadOnlyDictionary{string, FieldValue}, SaveStamp, CancellationToken)"/>.
    /// The connection is never disposed here.
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

        return await RunWriteAsync(
                connection,
                ownsConnection: false,
                RecordOperations.Insert,
                scope => InsertCoreAsync(scope, form, schema, values, stamp, cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Updates a record on a connection the caller owns, with a form and schema the caller supplies, through the same
    /// path as the public <c>UpdateAsync</c>; the reserved-word identifier fixture's entry. The connection is never
    /// disposed here.
    /// </summary>
    /// <param name="connection">An open connection with no pending transaction.</param>
    /// <param name="form">The in-memory metadata of the fixture form.</param>
    /// <param name="schema">The fixture's catalogue schema, with the view id of <paramref name="form"/>.</param>
    /// <param name="globalRecordId">The record's base-table <c>GlobalRecordId</c>.</param>
    /// <param name="submitted">The values of the writable data fields to write.</param>
    /// <param name="stamp">The save stamp of this write.</param>
    /// <param name="requiredGate">The required-field check of the stored record.</param>
    /// <param name="permissions">Per-table permissions, or <see langword="null"/> for no check.</param>
    /// <param name="cancellationToken">The caller's abort token.</param>
    /// <returns>The update's result, as the public overload returns it.</returns>
    internal async Task<RecordWriteResult> UpdateAsync(
        SqlConnection connection,
        FormMetadata form,
        FormSchema schema,
        string globalRecordId,
        IReadOnlyDictionary<string, FieldValue> submitted,
        SaveStamp stamp,
        Func<StoredRecord, RecordValidationResult> requiredGate,
        FormPermissions? permissions,
        CancellationToken cancellationToken)
    {
        RequireFixture(connection, form, schema);
        ArgumentNullException.ThrowIfNull(globalRecordId);
        ArgumentNullException.ThrowIfNull(submitted);
        ArgumentNullException.ThrowIfNull(stamp);
        ArgumentNullException.ThrowIfNull(requiredGate);

        return await RunWriteAsync(
                connection,
                ownsConnection: false,
                RecordOperations.Update,
                scope => UpdateCoreAsync(
                    scope,
                    form,
                    schema,
                    globalRecordId,
                    submitted,
                    stamp,
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
                select,
                cancellationToken)
            .ConfigureAwait(false);

        return row?.ToDto();
    }

    // The insert both InsertAsync overloads share, inside the transaction RunWriteAsync opened.
    private async Task<WriteDecision<RecordDto>> InsertCoreAsync(
        WriteScope scope,
        FormMetadata form,
        FormSchema schema,
        IReadOnlyDictionary<string, FieldValue> values,
        SaveStamp stamp,
        CancellationToken cancellationToken)
    {
        DataFieldSet dataFields = FormDefinitionFactory.GetDataFields(form);
        SubmittedValues resolved = scope.Check(() => ResolveValues(schema, dataFields, values, nameof(values)));

        // Every statement is built before the first is sent, so a statement the builders refuse sends nothing.
        string globalRecordId = RecordSqlBuilder.NewGlobalRecordId();
        SqlStatement baseInsert = RecordSqlBuilder.InsertBase(schema, globalRecordId, stamp);
        var pageInserts = new List<SqlStatement>(schema.Pages.Count);
        foreach (PageTableSchema page in schema.Pages)
        {
            // Legacy inserts a row on every page whatever it holds, so a page with no submitted value still gets one.
            if (page.Table is not null)
            {
                pageInserts.Add(RecordSqlBuilder.InsertPage(
                    page,
                    globalRecordId,
                    PageColumns(schema, dataFields, page),
                    resolved.ByName));
            }
        }

        scope.MarkDmlSent();
        int uniqueKey = await SqlExecution.ExecuteScalarAsync<int>(
                scope.Connection,
                scope.Transaction,
                RecordOperations.Insert,
                baseInsert,
                options.CommandTimeoutSeconds,
                observer,
                cancellationToken)
            .ConfigureAwait(false);

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
        return WriteDecision<RecordDto>.Committed(snapshot);
    }

    // The update both UpdateAsync overloads share, inside the transaction RunWriteAsync opened.
    private async Task<WriteDecision<RecordWriteResult>> UpdateCoreAsync(
        WriteScope scope,
        FormMetadata form,
        FormSchema schema,
        string globalRecordId,
        IReadOnlyDictionary<string, FieldValue> submitted,
        SaveStamp stamp,
        Func<StoredRecord, RecordValidationResult> requiredGate,
        FormPermissions? permissions,
        CancellationToken cancellationToken)
    {
        DataFieldSet dataFields = FormDefinitionFactory.GetDataFields(form);

        // Resolved before the locked read, the first base-table statement, so an unknown key reaches no record table.
        SubmittedValues resolved = scope.Check(() => ResolveValues(schema, dataFields, submitted, nameof(submitted)));

        if (await ReadBaseRowAsync(
                scope.Connection,
                scope.Transaction,
                RecordOperations.Update,
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
                RecordSqlBuilder.RecordByUniqueKey(schema, dataFields, locked.UniqueKey, locked.GlobalRecordId),
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(MissingRecordMessage);

        // The port's order puts the permission check before the required gate, and it covers every page whose row
        // the locked read found missing, not only the pages a submitted field sits on, as the permission rule states
        // it for PUT; the narrower per-submitted-page reading of the build plan is not followed.
        if (permissions is not null
            && stored.MissingPages.Any(page => !permissions.CanInsertInto(page.Page.TableName)))
        {
            return WriteDecision<RecordWriteResult>.RolledBack(RecordWriteResult.Forbidden());
        }

        RecordValidationResult gate = requiredGate(stored.Record)
            ?? throw new InvalidOperationException(GateResultMessage);
        if (!gate.IsValid)
        {
            return WriteDecision<RecordWriteResult>.RolledBack(RecordWriteResult.ValidationFailed(gate));
        }

        SqlStatement? baseStamp = RecordSqlBuilder.UpdateBaseStamp(
            schema,
            locked.UniqueKey,
            locked.GlobalRecordId,
            stamp);
        IReadOnlyList<PageUpdate> pageUpdates = PlanPageUpdates(schema, resolved, locked.GlobalRecordId);

        scope.MarkDmlSent();
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

    // The base-only soft delete, inside the transaction RunWriteAsync opened. A record already deleted is left exactly
    // as it is, stamps included, so a repeated DELETE writes nothing.
    private async Task<WriteDecision<SoftDeleteResult>> SoftDeleteCoreAsync(
        WriteScope scope,
        FormSchema schema,
        string globalRecordId,
        SaveStamp stamp,
        CancellationToken cancellationToken)
    {
        if (await ReadBaseRowAsync(
                scope.Connection,
                scope.Transaction,
                RecordOperations.Delete,
                RecordSqlBuilder.LockBase(schema, globalRecordId),
                cancellationToken)
            .ConfigureAwait(false) is not { } locked)
        {
            return WriteDecision<SoftDeleteResult>.RolledBack(SoftDeleteResult.NotFound);
        }

        if (locked.RecStatus == RecordQueryRules.DeletedRecStatus)
        {
            return WriteDecision<SoftDeleteResult>.RolledBack(SoftDeleteResult.AlreadyDeleted);
        }

        SqlStatement softDelete = RecordSqlBuilder.SoftDelete(
            schema,
            locked.UniqueKey,
            locked.GlobalRecordId,
            stamp);

        scope.MarkDmlSent();
        await ExecuteOnLockedRowAsync(scope, RecordOperations.Delete, softDelete, cancellationToken)
            .ConfigureAwait(false);

        return WriteDecision<SoftDeleteResult>.Committed(SoftDeleteResult.Deleted);
    }

    // The response snapshot of a write, read inside its transaction: the base and page rows by the clustered key, then
    // the record's intrinsic position and count over the set its RecStatus selects. The DTO is built here, before the
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
                RecordSqlBuilder.RecordByUniqueKey(schema, dataFields, uniqueKey, globalRecordId),
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(MissingRecordMessage);

        StoredRecord record = row.Record;
        bool intrinsicIncludesDeleted = RecordQueryRules.IntrinsicIncludesDeleted(record.RecStatus);
        int position = await ReadCountAsync(
                scope.Connection,
                scope.Transaction,
                operation,
                RecordSqlBuilder.Position(schema, record.UniqueKey, intrinsicIncludesDeleted),
                cancellationToken)
            .ConfigureAwait(false);
        int count = await ReadCountAsync(
                scope.Connection,
                scope.Transaction,
                operation,
                RecordSqlBuilder.Count(schema, intrinsicIncludesDeleted),
                cancellationToken)
            .ConfigureAwait(false);

        return RecordDto.From(record, position, count);
    }

    // One page's submitted columns, chunk by chunk. When the first chunk finds no page row, the row is inserted holding
    // only its key and the chunk is repeated, as legacy repairs a missing page row
    // (Epi.Core/Data/Services/CollectedDataProvider.cs:1360-1380); later chunks then find the row.
    private async Task RunPageUpdateAsync(WriteScope scope, PageUpdate pageUpdate, CancellationToken cancellationToken)
    {
        IReadOnlyList<SqlStatement> chunks = pageUpdate.Chunks;
        int affected = await ExecuteAsync(scope, RecordOperations.Update, chunks[0], cancellationToken)
            .ConfigureAwait(false);
        if (affected == 0)
        {
            await ExecuteAsync(scope, RecordOperations.Update, pageUpdate.InsertKey, cancellationToken)
                .ConfigureAwait(false);
            await ExecuteOnLockedRowAsync(scope, RecordOperations.Update, chunks[0], cancellationToken)
                .ConfigureAwait(false);
        }

        for (int index = 1; index < chunks.Count; index++)
        {
            await ExecuteOnLockedRowAsync(scope, RecordOperations.Update, chunks[index], cancellationToken)
                .ConfigureAwait(false);
        }
    }

    // A statement that must change exactly the locked record's row; a statement that finds no row means the record's
    // rows changed under the lock, so the write is abandoned and rolled back.
    private async Task ExecuteOnLockedRowAsync(
        WriteScope scope,
        string operation,
        SqlStatement statement,
        CancellationToken cancellationToken)
    {
        if (await ExecuteAsync(scope, operation, statement, cancellationToken).ConfigureAwait(false) == 0)
        {
            throw new InvalidOperationException(RowNotWrittenMessage);
        }
    }

    private Task<int> ExecuteAsync(
        WriteScope scope,
        string operation,
        SqlStatement statement,
        CancellationToken cancellationToken) =>
        SqlExecution.ExecuteAsync(
            scope.Connection,
            scope.Transaction,
            operation,
            statement,
            options.CommandTimeoutSeconds,
            observer,
            cancellationToken);


    // Runs one write transaction on connection and, when it owns the connection, releases it. A release failure after
    // a commit is the one failure left to the store once the data is written, so it is reported as written; after a
    // rollback, or when the write already failed, nothing was written and the result or failure already decided
    // stands.
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
            // SqlClient has no asynchronous begin: its BeginTransactionAsync makes this same call synchronously.
            transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
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
            else
            {
                scope.MarkRollbackAttempted();
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (ReferenceEquals(exception, scope.ContractViolation))
        {
            // A broken argument is refused before any base- or page-table statement, so the rollback has nothing to
            // undo, and the ArgumentException reaches the caller unwrapped.
            await RollbackQuietlyAsync(scope).ConfigureAwait(false);
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

    // Rolls back a failed write unless the commit was already called, then builds the exception the caller throws.
    // The rollback's own failure never replaces the original: it is only noted on the exception raised for it.
    private static async Task<Exception> FailAsync(
        WriteScope scope,
        string operation,
        Exception exception,
        CancellationToken cancellationToken)
    {
        bool rollbackCompleted = false;
        Exception? rollbackError = null;
        if (scope.Stage != WriteStage.Committing && !scope.RollbackAttempted)
        {
            scope.MarkRollbackAttempted();
            try
            {
                await scope.Transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                rollbackCompleted = true;
            }
            catch (Exception error)
            {
                rollbackError = error;
            }
        }

        return BuildFailure(exception, operation, scope.Stage, rollbackCompleted, rollbackError, cancellationToken);
    }

    // The exception a failed write raises: the original cancellation unchanged, or one with the caller's token for
    // SqlClient's acknowledgement of a cancelled command; otherwise the classified store exception carrying the
    // outcome the stage and the rollback prove, and the type and SQL error number of a rollback that failed.
    private static Exception BuildFailure(
        Exception exception,
        string operation,
        WriteStage stage,
        bool rollbackCompleted,
        Exception? rollbackError,
        CancellationToken cancellationToken)
    {
        (SqlFailure failure, Exception inner) = ClassifyOriginal(exception, cancellationToken);
        if (failure.Kind == SqlFailureKind.Cancelled)
        {
            return exception is OperationCanceledException
                ? exception
                : new OperationCanceledException(SqlErrorClassifier.CancelledMessage, exception, cancellationToken);
        }

        WriteOutcome outcome = SqlErrorClassifier.ResolveWriteOutcome(stage, rollbackCompleted, failure);
        Exception result = SqlErrorClassifier.ToDataStoreException(failure, operation, outcome, inner);
        if (rollbackError is not null)
        {
            result.Data[SqlErrorClassifier.RollbackExceptionTypeKey] = rollbackError.GetType().Name;
            if (rollbackError is SqlException sqlError)
            {
                result.Data[SqlErrorClassifier.RollbackSqlErrorNumberKey] = sqlError.Number;
            }
        }

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

    // The rollback of a write refused for a broken argument: no data statement was sent, so a failed rollback leaves
    // nothing behind and must not replace the ArgumentException.
    private static async Task RollbackQuietlyAsync(WriteScope scope)
    {
        scope.MarkRollbackAttempted();
        try
        {
            await scope.Transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Discarded by design: see the comment above; closing the connection ends the transaction in any case.
        }
    }


    // The contract check of a write's values, run before any base- or page-table statement: each key resolves to a
    // writable data field of the form (exact name, else the one canonical name equal to it ignoring case), no two keys
    // name the same field ignoring case, as the validator refuses them, and each value binds to its column exactly as
    // the write statement will bind it, so a value of the wrong kind or one the column cannot hold is refused here.
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

            ProjectedColumn column = RecordSqlBuilder.ResolveDataColumn(schema, field, byName.Count);
            _ = ColumnValueConverter.ToParameter(ProbeParameterName, column.Column.Metadata, field.FieldType, value);
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

    // Reads the first row of a record SELECT, or null when it returns none.
    private Task<RecordRow?> ReadRecordAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        string operation,
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
                await reader.ReadAsync(token).ConfigureAwait(false) ? ReadRecordRow(reader, select) : null);

    // Reads the first row of a base-row lookup (SetPositionLookup or LockBase), or null when it returns none.
    private Task<BaseRow?> ReadBaseRowAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        string operation,
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
                        ReadInt32(reader, RecordSqlBuilder.BaseRowUniqueKeyOrdinal),
                        ReadInt32(reader, RecordSqlBuilder.BaseRowRecStatusOrdinal),
                        ReadText(reader, RecordSqlBuilder.BaseRowGlobalRecordIdOrdinal))
                    : null);

    // Runs a COUNT_BIG statement and narrows its bigint to the int the port reports, failing rather than wrapping.
    private async Task<int> ReadCountAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        string operation,
        SqlStatement statement,
        CancellationToken cancellationToken)
    {
        long count = await SqlExecution.ExecuteScalarAsync<long>(
                connection,
                transaction,
                operation,
                statement,
                options.CommandTimeoutSeconds,
                observer,
                cancellationToken)
            .ConfigureAwait(false);
        return checked((int)count);
    }

    // One row of a record SELECT, by the ordinals its RecordSelect records: the base system columns, the intrinsic
    // coordinates when the statement computes them, the page-row presence keys and every data-field value. A page with
    // no row for the record reads NULL in each of its columns, so each of its fields is FieldValue.Null.
    private static RecordRow ReadRecordRow(DbDataReader reader, RecordSelect select)
    {
        var record = new StoredRecord(
            ReadText(reader, RecordSqlBuilder.RecordGlobalRecordIdOrdinal),
            ReadInt32(reader, RecordSqlBuilder.RecordUniqueKeyOrdinal),
            ReadInt32(reader, RecordSqlBuilder.RecordRecStatusOrdinal),
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
            position = ReadInt32(reader, RecordSqlBuilder.RecordIntrinsicPositionOrdinal);
            count = ReadInt32(reader, RecordSqlBuilder.RecordIntrinsicCountOrdinal);
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

    // A system integer (UniqueKey, RecStatus, a COUNT_BIG or ROW_NUMBER) as the int the port carries, accepting each
    // integer width SQL Server returns and failing, rather than wrapping, on a bigint beyond the int range.
    private static int ReadInt32(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            throw new InvalidOperationException(MissingSystemValueMessage);
        }

        return reader.GetValue(ordinal) switch
        {
            int value => value,
            short value => value,
            byte value => value,
            long value => checked((int)value),
            _ => throw new InvalidOperationException(UnexpectedSystemValueMessage),
        };
    }

    // A system text (GlobalRecordId) exactly as stored.
    private static string ReadText(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            throw new InvalidOperationException(MissingSystemValueMessage);
        }

        return reader.GetValue(ordinal) as string ?? throw new InvalidOperationException(UnexpectedSystemValueMessage);
    }

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

    // The state of one write transaction: how far it got, whether a rollback was already tried, and the one
    // ArgumentException its contract check raised, which alone leaves the repository unwrapped. Used by one request
    // on one thread.
    private sealed class WriteScope(SqlConnection connection, SqlTransaction transaction)
    {
        public SqlConnection Connection { get; } = connection;

        public SqlTransaction Transaction { get; } = transaction;

        public WriteStage Stage { get; private set; } = WriteStage.BeforeDml;

        public bool RollbackAttempted { get; private set; }

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

        // Marks exception as the caller's broken argument, for the caller to throw.
        public ArgumentException Reject(ArgumentException exception)
        {
            ContractViolation = exception;
            return exception;
        }

        // Called immediately before the first INSERT or UPDATE is sent.
        public void MarkDmlSent() => Stage = WriteStage.DmlSent;

        // Called immediately before Commit.
        public void MarkCommitting() => Stage = WriteStage.Committing;

        public void MarkRollbackAttempted() => RollbackAttempted = true;
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

    // A write's values after the contract check: by canonical name, and with their columns in data-field order.
    private sealed record SubmittedValues(
        IReadOnlyDictionary<string, FieldValue> ByName,
        IReadOnlyList<(ProjectedColumn Column, FieldValue Value)> Columns);

    // One page's update statements and the key-only insert that repairs its missing row.
    private sealed record PageUpdate(IReadOnlyList<SqlStatement> Chunks, SqlStatement InsertKey);
}

