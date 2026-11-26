using EpiInfo.Data.Metadata;
using EpiInfo.Data.Permissions;
using EpiInfo.Data.Records;
using EpiInfo.Data.Sql;
using EpiInfo.Domain.Forms;
using EpiInfo.Domain.Metadata;
using EpiInfo.Domain.Records;
using EpiInfo.Domain.Search;
using EpiInfo.Domain.Storage;
using EpiInfo.Domain.Validation;
using EpiInfo.Domain.Values;
using Microsoft.Data.SqlClient;

namespace EpiInfo.Data;

/// <summary>
/// The SQL Server implementation of <see cref="IDataStore"/>: a thin facade over the metadata reader, the record
/// repository and the permission reader of this assembly, with one failure-classification boundary around every member.
/// </summary>
/// <remarks>
/// <para>
/// <b>Composition.</b> The constructor builds one <see cref="SqlConnectionFactory"/>, <see cref="SqlMetadataReader"/>,
/// <see cref="SqlRecordRepository"/> and <see cref="SqlPermissionReader"/>, each given the same
/// <see cref="DatabaseOptions"/> and <see cref="ISqlCommandObserver"/>, so every command any of them sends is observed
/// and runs with the configured timeout. Each call opens its own pooled connection through the factory and disposes it
/// before it returns. Nothing is cached: metadata, catalogue schemas, counts, records and permissions are read again on
/// every call, and every identifier in record SQL comes from the reader's catalogue read for that call.
/// </para>
/// <para>
/// <b>Failures.</b> Every member runs inside the same boundary, which lets exactly three kinds of failure through
/// unchanged and turns everything else into a store exception:
/// </para>
/// <list type="bullet">
/// <item><description>
/// a <see cref="DataStoreUnavailableException"/> or <see cref="DataStoreWriteException"/> a collaborator already
/// raised, whose <see cref="WriteOutcome"/> was decided where the progress of the transaction is known and is never
/// rewritten here;
/// </description></item>
/// <item><description>
/// an <see cref="ArgumentException"/>, the port's channel for an argument that breaks a member's contract, raised
/// before any base- or page-table command is sent;
/// </description></item>
/// <item><description>
/// the client's cancellation, as <see cref="SqlErrorClassifier.Classify"/> recognises it: an
/// <see cref="OperationCanceledException"/> under the cancelled request token passes unchanged, and SqlClient's
/// acknowledgement of a cancelled command becomes an <see cref="OperationCanceledException"/> carrying that token.
/// </description></item>
/// </list>
/// <para>
/// Any other failure, such as a <see cref="SqlException"/> of a read or a reader's
/// <see cref="InvalidOperationException"/>, is classified by <see cref="SqlErrorClassifier"/>: unavailable becomes
/// <see cref="DataStoreUnavailableException"/> (503) and anything else <see cref="DataStoreWriteException"/> (500),
/// each carrying the member's fixed operation name, the SQL error number and the original failure as its inner
/// exception only. No message is ever built from SQL text, a parameter or field value, an identifier, the connection
/// string or a database message.
/// </para>
/// <para>
/// <b>Threading.</b> The instance holds only immutable collaborators, so one instance serves every request.
/// </para>
/// </remarks>
public sealed class SqlServerDataStore : IDataStore
{
    // The health query; it names no table and binds no value.
    private static readonly SqlStatement PingStatement = new("SELECT 1;", Array.Empty<SqlParameterValue>());

    private readonly ISqlCommandObserver observer;
    private readonly SqlConnectionFactory connections;
    private readonly SqlMetadataReader metadataReader;
    private readonly SqlRecordRepository recordRepository;
    private readonly SqlPermissionReader permissionReader;

    /// <summary>Creates the store and its collaborators.</summary>
    /// <param name="options">
    /// The connection string and command timeout. A missing or blank connection string is not an error here: every
    /// member that needs the database then throws <see cref="DataStoreUnavailableException"/> before any connection
    /// object is created.
    /// </param>
    /// <param name="observer">
    /// Receives every SQL command the store sends; <see cref="NullSqlCommandObserver"/> in production.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="options"/> or <paramref name="observer"/> is <see langword="null"/>.
    /// </exception>
    public SqlServerDataStore(DatabaseOptions options, ISqlCommandObserver observer)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(observer);

        this.observer = observer;
        connections = new SqlConnectionFactory(options);
        metadataReader = new SqlMetadataReader(connections, options, observer);
        recordRepository = new SqlRecordRepository(connections, metadataReader, options, observer);
        permissionReader = new SqlPermissionReader(connections, metadataReader, options, observer);
    }

    // Which outcome a failure the collaborators did not classify reports. Reads never write, so theirs is NotWritten.
    private enum OperationKind
    {
        Read,
        Write,
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<FormSummary>> ListFormsAsync(CancellationToken cancellationToken) =>
        RunAsync(
            nameof(IDataStore.ListFormsAsync),
            OperationKind.Read,
            () => metadataReader.ListFormsAsync(cancellationToken),
            cancellationToken);

    /// <inheritdoc />
    public Task<FormMetadata?> GetFormMetadataAsync(int viewId, CancellationToken cancellationToken) =>
        RunAsync(
            nameof(IDataStore.GetFormMetadataAsync),
            OperationKind.Read,
            () => metadataReader.GetFormMetadataAsync(viewId, cancellationToken),
            cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, IReadOnlyList<FieldOption>>> GetOptionsAsync(
        FormMetadata form,
        CancellationToken cancellationToken) =>
        RunAsync(
            nameof(IDataStore.GetOptionsAsync),
            OperationKind.Read,
            () => metadataReader.GetOptionsAsync(form, cancellationToken),
            cancellationToken);

    /// <inheritdoc />
    public Task<string?> GetCheckCodeAsync(int viewId, CancellationToken cancellationToken) =>
        RunAsync(
            nameof(IDataStore.GetCheckCodeAsync),
            OperationKind.Read,
            () => metadataReader.GetCheckCodeAsync(viewId, cancellationToken),
            cancellationToken);

    /// <inheritdoc />
    public Task<int> CountAsync(FormMetadata form, bool includeDeleted, CancellationToken cancellationToken) =>
        RunAsync(
            nameof(IDataStore.CountAsync),
            OperationKind.Read,
            () => recordRepository.CountAsync(form, includeDeleted, cancellationToken),
            cancellationToken);

    /// <inheritdoc />
    public Task<RecordDto?> GetAtPositionAsync(
        FormMetadata form,
        int position,
        bool includeDeleted,
        CancellationToken cancellationToken) =>
        RunAsync(
            nameof(IDataStore.GetAtPositionAsync),
            OperationKind.Read,
            () => recordRepository.GetAtPositionAsync(form, position, includeDeleted, cancellationToken),
            cancellationToken);

    /// <inheritdoc />
    public Task<RecordDto?> GetByIdAsync(
        FormMetadata form,
        string globalRecordId,
        CancellationToken cancellationToken) =>
        RunAsync(
            nameof(IDataStore.GetByIdAsync),
            OperationKind.Read,
            () => recordRepository.GetByIdAsync(form, globalRecordId, cancellationToken),
            cancellationToken);

    /// <inheritdoc />
    public Task<NavigationPosition?> GetSetPositionAsync(
        FormMetadata form,
        string globalRecordId,
        bool includeDeleted,
        CancellationToken cancellationToken) =>
        RunAsync(
            nameof(IDataStore.GetSetPositionAsync),
            OperationKind.Read,
            () => recordRepository.GetSetPositionAsync(form, globalRecordId, includeDeleted, cancellationToken),
            cancellationToken);

    /// <inheritdoc />
    public Task<RecordPage> ListAsync(FormMetadata form, RecordListQuery query, CancellationToken cancellationToken) =>
        RunAsync(
            nameof(IDataStore.ListAsync),
            OperationKind.Read,
            () => recordRepository.ListAsync(form, query, cancellationToken),
            cancellationToken);

    /// <inheritdoc />
    public Task<SearchResponse> SearchAsync(
        FormMetadata form,
        IReadOnlyList<SearchCondition> conditions,
        bool includeDeleted,
        string? excludeGlobalRecordId,
        CancellationToken cancellationToken) =>
        RunAsync(
            nameof(IDataStore.SearchAsync),
            OperationKind.Read,
            () => recordRepository.SearchAsync(
                form,
                conditions,
                includeDeleted,
                excludeGlobalRecordId,
                cancellationToken),
            cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// Opens a connection and runs <c>SELECT 1</c> through the observed command path. Every failure, a missing
    /// connection string included, is thrown as the classified store exception rather than returned as
    /// <see langword="false"/>; <see langword="false"/> means only that the server answered something other than 1.
    /// </remarks>
    public Task<bool> PingAsync(CancellationToken cancellationToken) =>
        RunAsync(
            nameof(IDataStore.PingAsync),
            OperationKind.Read,
            () => PingCoreAsync(cancellationToken),
            cancellationToken);

    /// <inheritdoc />
    public Task<RecordDto> InsertAsync(
        FormMetadata form,
        IReadOnlyDictionary<string, FieldValue> values,
        SaveStamp stamp,
        CancellationToken cancellationToken) =>
        RunAsync(
            nameof(IDataStore.InsertAsync),
            OperationKind.Write,
            () => recordRepository.InsertAsync(form, values, stamp, cancellationToken),
            cancellationToken);

    /// <inheritdoc />
    public Task<RecordWriteResult> UpdateAsync(
        FormMetadata form,
        string globalRecordId,
        IReadOnlyDictionary<string, FieldValue> submitted,
        SaveStamp stamp,
        Func<StoredRecord, RecordValidationResult> requiredGate,
        FormPermissions? permissions,
        CancellationToken cancellationToken) =>
        RunAsync(
            nameof(IDataStore.UpdateAsync),
            OperationKind.Write,
            () => recordRepository.UpdateAsync(
                form,
                globalRecordId,
                submitted,
                stamp,
                requiredGate,
                permissions,
                cancellationToken),
            cancellationToken);

    /// <inheritdoc />
    public Task<SoftDeleteResult> SoftDeleteAsync(
        FormMetadata form,
        string globalRecordId,
        SaveStamp stamp,
        CancellationToken cancellationToken) =>
        RunAsync(
            nameof(IDataStore.SoftDeleteAsync),
            OperationKind.Write,
            () => recordRepository.SoftDeleteAsync(form, globalRecordId, stamp, cancellationToken),
            cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// Always reports permissions: the result is never <see langword="null"/>, and a table the catalogue does not hold
    /// has no entry, so <see cref="FormPermissions"/> denies it.
    /// </remarks>
    public Task<FormPermissions?> GetPermissionsAsync(FormMetadata form, CancellationToken cancellationToken) =>
        RunAsync(
            nameof(IDataStore.GetPermissionsAsync),
            OperationKind.Read,
            () => permissionReader.GetPermissionsAsync(form, cancellationToken),
            cancellationToken);

    private async Task<bool> PingCoreAsync(CancellationToken cancellationToken)
    {
        SqlConnection connection = await connections
            .OpenAsync(nameof(IDataStore.PingAsync), cancellationToken)
            .ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            int answer = await SqlExecution.ExecuteScalarAsync<int>(
                    connection,
                    transaction: null,
                    nameof(IDataStore.PingAsync),
                    PingStatement,
                    connections.CommandTimeoutSeconds,
                    observer,
                    cancellationToken)
                .ConfigureAwait(false);

            return answer == 1;
        }
    }

    // The one classification boundary of every member. The operation names follow the store-exception convention of
    // the IDataStore method name (nameof), which the in-memory test store also reports, so a log line names the same
    // operation whichever store served the request.
    private static async Task<T> RunAsync<T>(
        string operation,
        OperationKind kind,
        Func<Task<T>> body,
        CancellationToken cancellationToken)
    {
        try
        {
            return await body().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception
            is DataStoreUnavailableException
            or DataStoreWriteException
            or ArgumentException)
        {
            // Already a store exception, with the outcome its raiser could prove, or the port's contract-violation
            // channel: both reach the caller exactly as raised. SqlClient's ArgumentException for a malformed
            // connection string never arrives here, because the connection factory turns it into a store exception.
            throw;
        }
        catch (Exception exception)
        {
            // The classifier decides cancellation too: a failure that merely coincides with an aborted request, such
            // as a command timeout, keeps its own class, as in the connection factory and the record repository.
            SqlFailure failure = SqlErrorClassifier.Classify(exception, cancellationToken);
            if (failure.Kind == SqlFailureKind.Cancelled)
            {
                if (exception is OperationCanceledException)
                {
                    throw;
                }

                throw new OperationCanceledException(SqlErrorClassifier.CancelledMessage, exception, cancellationToken);
            }

            throw SqlErrorClassifier.ToDataStoreException(failure, operation, OutcomeOf(kind), exception);
        }
    }

    // The record repository classifies every failure of a write itself, with the outcome its transaction stage proves,
    // so a write failure reaching here unclassified carries no evidence about the data. It reports Unknown, the one
    // outcome a client never resends automatically, rather than NotWritten, which would claim a proof this boundary
    // does not have. A read never writes, so its outcome is always NotWritten.
    private static WriteOutcome OutcomeOf(OperationKind kind) =>
        kind == OperationKind.Write ? WriteOutcome.Unknown : WriteOutcome.NotWritten;
}
