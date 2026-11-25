using EpiInfo.Data.Metadata;
using EpiInfo.Data.Sql;
using EpiInfo.Domain.Metadata;
using EpiInfo.Domain.Storage;
using Microsoft.Data.SqlClient;

namespace EpiInfo.Data.Permissions;

/// <summary>
/// Reads the INSERT and UPDATE permissions the configured login holds on each data table of one form: one
/// <see cref="TablePermission"/> for the base table, then one per page table in <c>metaPages.Position</c> order.
/// </summary>
/// <remarks>
/// <para>
/// Each flag is <c>HAS_PERMS_BY_NAME(&lt;table&gt;, N'OBJECT', &lt;permission&gt;)</c>. Only the value 1 grants. Both 0
/// and <c>NULL</c> deny; SQL Server returns <c>NULL</c> when the securable or the permission cannot be resolved. This
/// class reports the raw per-table flags and nothing more: <see cref="FormPermissions"/> owns the rule that derives the
/// insert, update and delete decisions from them, and it denies any table without an entry, such as a page whose
/// table is missing from the catalogue.
/// </para>
/// <para>
/// Legacy Epi Info (<c>Epi.Data.SqlServer/SqlDatabase.cs:1804-1935</c>, <c>GetPermissions</c>) enumerates every
/// <c>sys.tables</c> row and, for INSERT, UPDATE, DELETE and SELECT in turn, concatenates the table name into the
/// statement text. This reader departs from it in the ways recorded in <c>docs/parity-notes.md</c>:
/// </para>
/// <list type="bullet">
/// <item><description>
/// The table name travels only as the <c>@securable</c> parameter value, never inside the statement text, and is the
/// bracket-quoted name of a reader-built <see cref="TableSchema"/>.
/// </description></item>
/// <item><description>
/// Only INSERT and UPDATE are read, both from one statement per table: a soft delete updates the base row, so DELETE
/// and SELECT decide nothing.
/// </description></item>
/// <item><description>Only the form's own base and page tables are checked.</description></item>
/// </list>
/// <para>
/// Every statement is a <c>SELECT</c> and changes nothing. Permissions and the form's schema are read again on every
/// call, never cached, so a grant or revoke applies to the next request. Failures are not caught here: the connection
/// factory classifies a failure to open, and the store that owns this reader classifies the rest. The instance holds
/// only immutable collaborators and is safe to share across threads.
/// </para>
/// </remarks>
/// <param name="connectionFactory">Opens the one connection each call uses.</param>
/// <param name="metadataReader">Reads the form's catalogue schema, the only source of the table names.</param>
/// <param name="options">Supplies the command timeout.</param>
/// <param name="observer">Receives every command this reader sends.</param>
internal sealed class SqlPermissionReader(
    SqlConnectionFactory connectionFactory,
    SqlMetadataReader metadataReader,
    DatabaseOptions options,
    ISqlCommandObserver observer)
{
    // The fixed operation name reported to the connection factory, the command observer and failure classification.
    // It names no table, so no identifier reaches an exception or a log line.
    private const string Operation = "GetPermissions";

    private const string SecurableParameter = "@securable";

    // Fixed text: the table reaches SQL Server only as the value of @securable. A one-part bracket-quoted name
    // resolves in the caller's default schema, then dbo, which is how the metadata reader confirms each catalogue
    // table, so the permission checked is the permission on the table record SQL addresses.
    private const string PermissionSql = """
        SELECT HAS_PERMS_BY_NAME(@securable, N'OBJECT', N'INSERT') AS CanInsert,
               HAS_PERMS_BY_NAME(@securable, N'OBJECT', N'UPDATE') AS CanUpdate;
        """;

    private readonly SqlConnectionFactory connectionFactory =
        connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));

    private readonly SqlMetadataReader metadataReader =
        metadataReader ?? throw new ArgumentNullException(nameof(metadataReader));

    private readonly DatabaseOptions options = options ?? throw new ArgumentNullException(nameof(options));

    private readonly ISqlCommandObserver observer = observer ?? throw new ArgumentNullException(nameof(observer));

    /// <summary>
    /// Reads the login's INSERT and UPDATE permissions on the base table and every page table of
    /// <paramref name="form"/>, on one connection and outside any transaction.
    /// </summary>
    /// <param name="form">
    /// The form's metadata. Only its <see cref="FormMetadata.ViewId"/> is used: the tables are resolved again from the
    /// catalogue for this call, so every name checked is one the database returned. The parameter list mirrors
    /// <see cref="IDataStore.GetPermissionsAsync"/>, so the store forwards its arguments unchanged.
    /// </param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>
    /// One entry per table that exists in the catalogue, base table first and then the pages in
    /// <c>metaPages.Position</c> order, each named with its catalogue spelling and with a flag set only where
    /// <c>HAS_PERMS_BY_NAME</c> returned 1. A page whose table is missing has no entry. <see langword="null"/> when the
    /// form's <c>ViewId</c> is no longer in <c>metaViews</c>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="form"/> is null.</exception>
    /// <exception cref="DataStoreUnavailableException">
    /// No connection string is configured, or the connection could not be opened for a reason the classifier treats as
    /// unavailable.
    /// </exception>
    /// <exception cref="DataStoreWriteException">The connection could not be opened for any other reason.</exception>
    /// <exception cref="InvalidOperationException">
    /// The form's metadata does not name exactly one data table, or that table is not in the catalogue.
    /// </exception>
    /// <exception cref="SqlException">A permission or metadata command failed; the owning store classifies it.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<FormPermissions?> GetPermissionsAsync(FormMetadata form, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);

        SqlConnection connection = await connectionFactory
            .OpenAsync(Operation, cancellationToken)
            .ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            (FormMetadata Form, FormSchema Schema)? read = await metadataReader
                .ReadFormAsync(connection, transaction: null, form.ViewId, cancellationToken)
                .ConfigureAwait(false);
            if (read is null)
            {
                return null;
            }

            FormSchema schema = read.Value.Schema;
            var tables = new List<TablePermission>(schema.Pages.Count + 1)
            {
                await ReadTableAsync(connection, schema.BaseTable, cancellationToken).ConfigureAwait(false),
            };

            foreach (PageTableSchema page in schema.Pages)
            {
                if (page.Table is { } pageTable)
                {
                    tables.Add(await ReadTableAsync(connection, pageTable, cancellationToken).ConfigureAwait(false));
                }
            }

            return new FormPermissions(tables);
        }
    }

    private async Task<TablePermission> ReadTableAsync(
        SqlConnection connection,
        TableSchema table,
        CancellationToken cancellationToken)
    {
        var statement = new SqlStatement(
            PermissionSql,
            [ColumnValueConverter.Text(SecurableParameter, SqlIdentifier.Quote(table))]);

        IReadOnlyList<PermissionRow> rows = await SqlExecution.QueryAsync<PermissionRow>(
                connection,
                transaction: null,
                Operation,
                statement,
                options.CommandTimeoutSeconds,
                observer,
                cancellationToken)
            .ConfigureAwait(false);

        // The statement always returns one row; a missing row is read as denied, so the result can only fail closed.
        PermissionRow? row = rows.Count > 0 ? rows[0] : null;

        return new TablePermission(table.Name, row?.CanInsert == 1, row?.CanUpdate == 1);
    }

    // Dapper row type: the property names match the column aliases of PermissionSql, and int? matches the int, possibly
    // NULL, that HAS_PERMS_BY_NAME returns.
    private sealed class PermissionRow
    {
        public int? CanInsert { get; set; }

        public int? CanUpdate { get; set; }
    }
}
