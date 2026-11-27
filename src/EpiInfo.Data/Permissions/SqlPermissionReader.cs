using System.Text;
using EpiInfo.Data.Metadata;
using EpiInfo.Data.Records;
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
/// and <c>NULL</c> deny, so a check SQL Server cannot evaluate never grants. This class reports the raw per-table flags
/// and nothing more: <see cref="FormPermissions"/> owns the rule that derives the insert, update and delete decisions
/// from them, and it denies any table without an entry, such as a page whose table is missing from the catalogue.
/// </para>
/// <para>
/// Each <see cref="GetPermissionsAsync"/> call uses one connection, outside any transaction. It sends at most one
/// catalogue read, through <see cref="SqlMetadataReader.ResolveTablesAsync"/>: it confirms the form's own base and page
/// tables and supplies each one's catalogue spelling, without reading the form's metadata again. That read is not sent
/// when the base and page table names are all blank. Then one parameterised statement checks INSERT and UPDATE on up
/// to 1,000 confirmed tables, and one more is sent for each further 1,000 or part of 1,000. A table the catalogue does
/// not hold gets no entry, so it is denied; when no table is confirmed, no permission statement is sent and the result
/// has no entry.
/// </para>
/// <para>
/// <see cref="ReadResolvedAsync"/> sends the same permission statements, and builds the same entries, for tables a
/// caller already resolved, on that caller's connection and inside its transaction, and sends no catalogue read. The
/// record repository uses it within each write given permissions: an insert and an update, for the base table and
/// every page table, and a soft delete, for the base table alone. So the INSERT an insert requires, the UPDATE and
/// missing-page INSERT an update requires and the base-table UPDATE a soft delete requires are each decided inside
/// the write's own transaction, on the tables that transaction's schema read confirmed.
/// </para>
/// <para>
/// Each entry carries the catalogue spelling of its table, and <see cref="FormPermissions.ResolvedTableNames"/> maps
/// each form table name the catalogue confirmed, as the metadata spells it, to that entry. Lookups are exact, so a
/// metadata name in another letter case reaches only the table the catalogue confirmed for it, and a name it left
/// unresolved reaches none.
/// </para>
/// <para>
/// Legacy Epi Info (<c>Epi.Data.SqlServer/SqlDatabase.cs:1804-1935</c>, <c>GetPermissions</c>) first runs one
/// <c>sys.tables</c> query that computes SELECT on each schema-qualified, <c>QUOTENAME</c>-quoted name as
/// <c>have_select</c>, and keeps only the table names: that flag decides nothing. It then sends one statement per table
/// for INSERT, UPDATE, DELETE and SELECT in turn, each concatenating the unquoted one-part name into
/// <c>SELECT HAS_PERMS_BY_NAME('&lt;table&gt;', 'OBJECT', '&lt;permission&gt;')</c>, and reads only "1" as granted.
/// This reader departs from it in the ways recorded in <c>docs/parity-notes.md</c>:
/// </para>
/// <list type="bullet">
/// <item><description>
/// Each table name travels only as the value of its own positional parameter, never inside the statement text, and
/// is the bracket-quoted name of a reader-built <see cref="TableSchema"/>.
/// </description></item>
/// <item><description>
/// Only INSERT and UPDATE are read, for up to 1,000 of the form's tables in one statement: a soft delete updates the
/// base row, so DELETE and SELECT decide nothing.
/// </description></item>
/// <item><description>
/// Only the form's own base and page tables are checked, as the catalogue confirms them.
/// </description></item>
/// </list>
/// <para>
/// Every statement is a <c>SELECT</c> and changes nothing. Nothing is cached: no call reuses an earlier call's
/// catalogue or permission result, so a grant or revoke applies to the next request. Failures are not caught here:
/// the connection factory classifies a failure to open, and the store that owns this reader, or the write whose
/// transaction <see cref="ReadResolvedAsync"/> joins, classifies the rest. The instance holds only immutable
/// collaborators and is safe to share across threads.
/// </para>
/// </remarks>
/// <param name="connectionFactory">Opens the one connection each call uses.</param>
/// <param name="metadataReader">Resolves the form's tables in the catalogue, the only source of table names.</param>
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

    // Fixed text: the redaction policy keeps table names out of exception messages.
    private const string MismatchedResolutionMessage = "Each requested table name needs exactly one resolution.";

    // The permission statement is these two fixed fragments around one "(@pN)" row per table, joined by ", ". Each row
    // value is a bracket-quoted table name, returned as Securable so a result row maps back to its table. A one-part
    // bracket-quoted name resolves in the caller's default schema, then dbo, which is how the metadata reader confirms
    // each catalogue table, so the permission checked is the permission on the table record SQL addresses.
    private const string PermissionSqlHead = """
        SELECT s.[Securable] AS Securable,
               HAS_PERMS_BY_NAME(s.[Securable], N'OBJECT', N'INSERT') AS CanInsert,
               HAS_PERMS_BY_NAME(s.[Securable], N'OBJECT', N'UPDATE') AS CanUpdate
        FROM (VALUES
        """;

    private const string PermissionSqlTail = ") AS s ([Securable]);";

    // SQL Server accepts at most 2,100 parameters in one request. A form never comes near that, but a longer table
    // list is split into statements of this many tables so the check cannot fail on the limit.
    private const int MaxTablesPerStatement = 1000;

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
    /// The form's metadata, as the store's metadata reader returned it for the current request. Its
    /// <see cref="FormMetadata.BaseTableName"/> and each page's <see cref="PageMetadata.TableName"/> name the tables
    /// to check, and each name is confirmed against the catalogue for this call, so every name checked is one the
    /// database returned. A blank name, or one the catalogue does not hold, is not checked. The parameter list mirrors
    /// <see cref="IDataStore.GetPermissionsAsync"/>, so the store forwards its arguments unchanged.
    /// </param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>
    /// One entry per table that exists in the catalogue, base table first and then the pages in
    /// <c>metaPages.Position</c> order, each named with its catalogue spelling and with a flag set only where
    /// <c>HAS_PERMS_BY_NAME</c> returned 1. A table that is missing, the base table included, has no entry, and two
    /// names that reach one table give one entry. <see cref="FormPermissions.ResolvedTableNames"/> maps each requested
    /// name the catalogue confirmed, exactly as the metadata spells it, to its entry's name, so every lookup is exact.
    /// Never <see langword="null"/>: the nullable return type only mirrors
    /// <see cref="IDataStore.GetPermissionsAsync"/>, where <see langword="null"/> means a store that checks no
    /// permissions.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="form"/> is null.</exception>
    /// <exception cref="DataStoreUnavailableException">
    /// No connection string is configured, or the connection could not be opened for a reason the classifier treats as
    /// unavailable.
    /// </exception>
    /// <exception cref="DataStoreWriteException">The connection could not be opened for any other reason.</exception>
    /// <exception cref="SqlException">
    /// A catalogue or permission command failed; the owning store classifies it.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<FormPermissions?> GetPermissionsAsync(FormMetadata form, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);

        List<string> requestedNames = RequestedTableNames(form);

        SqlConnection connection = await connectionFactory
            .OpenAsync(Operation, cancellationToken)
            .ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            IReadOnlyList<TableSchema?> resolved = await metadataReader
                .ResolveTablesAsync(connection, transaction: null, requestedNames, cancellationToken)
                .ConfigureAwait(false);

            return await ReadResolvedAsync(
                    connection,
                    transaction: null,
                    Operation,
                    requestedNames,
                    resolved,
                    options.CommandTimeoutSeconds,
                    observer,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads the login's INSERT and UPDATE permissions on tables the caller already resolved, on the caller's
    /// connection and, when one is given, inside the caller's transaction. No catalogue read is sent.
    /// </summary>
    /// <remarks>
    /// The record repository calls it inside each write given permissions, before the write's first data statement:
    /// inside an insert, after the form's exclusive write lock, for the base table and every page table; inside an
    /// update, after the locked read and the required gate, for the base table and every page table; and inside a soft
    /// delete, after the locked read, for the base table alone. So the permissions each write requires are decided by
    /// the grants in force at that point of its transaction, on tables that transaction's own schema read confirmed.
    /// </remarks>
    /// <param name="connection">An open connection; this method neither opens nor closes it.</param>
    /// <param name="transaction">The transaction the statements join, or <see langword="null"/> for none.</param>
    /// <param name="operation">The fixed operation name reported to the command observer.</param>
    /// <param name="requestedNames">The table names, exactly as the metadata spells them.</param>
    /// <param name="resolved">
    /// For each entry of <paramref name="requestedNames"/>, at the same index, the reader-built table the catalogue
    /// confirmed for it, or <see langword="null"/> when it confirmed none.
    /// </param>
    /// <param name="commandTimeoutSeconds">The command timeout in whole seconds.</param>
    /// <param name="observer">Receives every command this method sends.</param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>
    /// One entry per distinct resolved table, in request order, named with its catalogue spelling and with a flag set
    /// only where <c>HAS_PERMS_BY_NAME</c> returned 1, and <see cref="FormPermissions.ResolvedTableNames"/> mapping
    /// each requested name that was resolved to its entry's name. When no table is resolved, no statement is sent and
    /// the result has no entry.
    /// </returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="resolved"/> and <paramref name="requestedNames"/> differ in length.
    /// </exception>
    /// <exception cref="SqlException">A permission command failed; the caller classifies it.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    internal static async Task<FormPermissions> ReadResolvedAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        string operation,
        IReadOnlyList<string> requestedNames,
        IReadOnlyList<TableSchema?> resolved,
        int commandTimeoutSeconds,
        ISqlCommandObserver observer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(requestedNames);
        ArgumentNullException.ThrowIfNull(resolved);
        ArgumentNullException.ThrowIfNull(observer);
        if (resolved.Count != requestedNames.Count)
        {
            throw new ArgumentException(MismatchedResolutionMessage, nameof(resolved));
        }

        // Only tables the catalogue holds are checked, in request order and once each. A missing table gets no
        // entry, which FormPermissions denies, so the result can only fail closed.
        var tables = new List<TableSchema>(resolved.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (TableSchema? table in resolved)
        {
            if (table is not null && seen.Add(table.Name))
            {
                tables.Add(table);
            }
        }

        if (tables.Count == 0)
        {
            return new FormPermissions([]);
        }

        var rowsBySecurable = new Dictionary<string, PermissionRow>(tables.Count, StringComparer.Ordinal);
        for (int start = 0; start < tables.Count; start += MaxTablesPerStatement)
        {
            int count = Math.Min(MaxTablesPerStatement, tables.Count - start);
            IReadOnlyList<PermissionRow> rows = await SqlExecution.QueryAsync<PermissionRow>(
                    connection,
                    transaction,
                    operation,
                    PermissionStatement(tables, start, count),
                    commandTimeoutSeconds,
                    observer,
                    cancellationToken)
                .ConfigureAwait(false);

            foreach (PermissionRow row in rows)
            {
                if (row.Securable is { } securable)
                {
                    rowsBySecurable.TryAdd(securable, row);
                }
            }
        }

        var permissions = new TablePermission[tables.Count];
        for (int index = 0; index < permissions.Length; index++)
        {
            TableSchema table = tables[index];

            // A table with no result row is denied, and only the value 1 grants: 0 and NULL deny.
            rowsBySecurable.TryGetValue(SqlIdentifier.Quote(table), out PermissionRow? row);
            permissions[index] = new TablePermission(table.Name, row?.CanInsert == 1, row?.CanUpdate == 1);
        }

        // Each requested metadata name the catalogue confirmed maps to the entry of the table it reached, so a name
        // spelled in another letter case finds that entry exactly. A name the catalogue left unresolved, missing or
        // ambiguous, gets no key and so reaches no entry.
        var resolvedTableNames = new Dictionary<string, string>(requestedNames.Count, StringComparer.Ordinal);
        for (int index = 0; index < requestedNames.Count; index++)
        {
            if (resolved[index] is { } table)
            {
                resolvedTableNames.TryAdd(requestedNames[index], table.Name);
            }
        }

        return new FormPermissions(permissions) { ResolvedTableNames = resolvedTableNames };
    }

    // The base table, then each page table in metaPages.Position order. The sort is stable, so pages that share a
    // position keep the PageId order the metadata reader gives them. A blank name names no table and is left out.
    private static List<string> RequestedTableNames(FormMetadata form)
    {
        var names = new List<string>(form.Pages.Count + 1);
        if (!string.IsNullOrWhiteSpace(form.BaseTableName))
        {
            names.Add(form.BaseTableName);
        }

        foreach (PageMetadata page in form.Pages.OrderBy(page => page.Position))
        {
            if (!string.IsNullOrWhiteSpace(page.TableName))
            {
                names.Add(page.TableName);
            }
        }

        return names;
    }

    // Builds one permission statement for tables[start] to tables[start + count - 1]. The text holds only the fixed
    // fragments and the generated names @p0, @p1, ...: each table reaches SQL Server only as the nvarchar(max) value
    // of its own parameter, the bracket-quoted name of a reader-built TableSchema, so no identifier or value ever
    // shapes the statement text.
    private static SqlStatement PermissionStatement(List<TableSchema> tables, int start, int count)
    {
        var parameters = new PositionalParameters();
        var text = new StringBuilder(PermissionSqlHead);
        for (int index = start; index < start + count; index++)
        {
            string securable = SqlIdentifier.Quote(tables[index]);
            text.Append(index == start ? "(" : ", (")
                .Append(parameters.Add(name => ColumnValueConverter.Text(name, securable)))
                .Append(')');
        }

        text.Append(PermissionSqlTail);
        return parameters.ToStatement(text.ToString());
    }

    // Dapper row type: the property names match the column aliases of the permission statement. Securable is the
    // bracket-quoted name bound for the row, and int? matches the int, possibly NULL, that HAS_PERMS_BY_NAME returns.
    private sealed class PermissionRow
    {
        public string? Securable { get; set; }

        public int? CanInsert { get; set; }

        public int? CanUpdate { get; set; }
    }
}
