using System.Data;
using System.Globalization;
using System.Text.Json;
using EpiInfo.Data.Sql;
using EpiInfo.Domain.Forms;
using EpiInfo.Domain.Metadata;
using EpiInfo.Domain.Values;
using Microsoft.Data.SqlClient;

namespace EpiInfo.Data.Metadata;

/// <summary>
/// Reads the Epi Info form metadata (<c>metaViews</c>, <c>metaPages</c>, <c>metaFields</c>, <c>metaFieldTypes</c>),
/// the legal-values code tables and the SQL Server catalogue, and builds the catalogue schema objects
/// (<see cref="TableSchema"/>, <see cref="ColumnSchema"/>, <see cref="PageTableSchema"/>, <see cref="FormSchema"/>)
/// that are the only source of identifiers in record and code-table SQL.
/// </summary>
/// <remarks>
/// <para>
/// Nothing is cached: every call reads the metadata and the catalogue again, so a changed form or column type is seen
/// by the next request. Every statement is a <c>SELECT</c>, runs through <see cref="SqlExecution"/> with the configured
/// command timeout and the command observer, and binds every value as a typed parameter. The one statement with
/// dynamic text, the code-table read, names only a table and a column this class built from the catalogue.
/// </para>
/// <para>
/// Failures to open a connection or to run a command are not caught here: <see cref="SqlConnectionFactory"/> and the
/// store that owns this reader classify them. The exceptions raised here carry fixed messages that name no table,
/// column, value or SQL text. The instance holds only immutable collaborators and is safe to share across threads.
/// </para>
/// </remarks>
/// <param name="connectionFactory">Opens the connections of the port reads.</param>
/// <param name="options">Supplies the command timeout.</param>
/// <param name="observer">Receives every command this reader sends.</param>
internal sealed class SqlMetadataReader(
    SqlConnectionFactory connectionFactory,
    DatabaseOptions options,
    ISqlCommandObserver observer)
{
    // Fixed operation names reported to the connection factory and the command observer.
    private const string ListFormsOperation = "metadata.listForms";
    private const string ReadFormOperation = "metadata.readForm";
    private const string ReadOptionsOperation = "metadata.readOptions";
    private const string ReadCheckCodeOperation = "metadata.readCheckCode";
    private const string ReadTempTableOperation = "metadata.readTempTable";

    // Fixed messages: the redaction policy keeps identifiers, values and SQL out of exception text.
    private const string DataTableNameMessage = "The form metadata does not name exactly one data table.";
    private const string BaseTableMissingMessage = "The form's data table was not found in the database catalogue.";
    private const string TempTableMissingMessage = "The temporary table was not found.";
    private const string ConnectionNotOpenMessage = "The connection must be open before a temporary table is read.";
    private const string TempTableNameMessage = "A session temporary table name must be non-blank and start with '#'.";

    private static readonly IReadOnlyList<FieldOption> NoOptions = Array.AsReadOnly(Array.Empty<FieldOption>());
    private static readonly IReadOnlyList<SqlParameterValue> NoParameters =
        Array.AsReadOnly(Array.Empty<SqlParameterValue>());

    private readonly SqlConnectionFactory connectionFactory =
        connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));

    private readonly DatabaseOptions options = options ?? throw new ArgumentNullException(nameof(options));

    private readonly ISqlCommandObserver observer = observer ?? throw new ArgumentNullException(nameof(observer));

    /// <summary>Lists every form in <c>metaViews</c> with its page count, ordered by <c>ViewId</c>.</summary>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>One summary per form; seeded <c>(1, CaseReport, 1)</c> and <c>(2, FoodHistory, 2)</c>.</returns>
    public async Task<IReadOnlyList<FormSummary>> ListFormsAsync(CancellationToken cancellationToken)
    {
        SqlConnection connection = await connectionFactory
            .OpenAsync(ListFormsOperation, cancellationToken)
            .ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            IReadOnlyList<FormRow> rows = await QueryAsync<FormRow>(
                    connection,
                    transaction: null,
                    ListFormsOperation,
                    new SqlStatement(MetadataQueries.ListForms, NoParameters),
                    cancellationToken)
                .ConfigureAwait(false);

            var forms = new FormSummary[rows.Count];
            for (int index = 0; index < forms.Length; index++)
            {
                FormRow row = rows[index];
                forms[index] = new FormSummary(row.ViewId, row.Name, row.PageCount);
            }

            return Array.AsReadOnly(forms);
        }
    }

    /// <summary>Reads one form's metadata, with each field's page-table column as the catalogue reports it.</summary>
    /// <param name="viewId">The form's <c>metaViews.ViewId</c>.</param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>The form's metadata, or <see langword="null"/> when <paramref name="viewId"/> is not in <c>metaViews</c>.</returns>
    /// <exception cref="InvalidOperationException">
    /// The form's fields do not name exactly one data table, or that table is not in the catalogue.
    /// </exception>
    public async Task<FormMetadata?> GetFormMetadataAsync(int viewId, CancellationToken cancellationToken)
    {
        SqlConnection connection = await connectionFactory
            .OpenAsync(ReadFormOperation, cancellationToken)
            .ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            (FormMetadata Form, FormSchema Schema)? form = await ReadFormAsync(
                    connection,
                    transaction: null,
                    viewId,
                    cancellationToken)
                .ConfigureAwait(false);

            return form?.Form;
        }
    }

    /// <summary>
    /// Reads the options of every Legal Values field of <paramref name="form"/>: one entry per field that
    /// <see cref="FieldTypeMapper.Map"/> maps to a supported <see cref="FieldType.LegalValues"/>.
    /// </summary>
    /// <param name="form">The form's metadata, as <see cref="GetFormMetadataAsync"/> returned it.</param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>
    /// The options keyed by canonical field name, keys compared ignoring case. A field without a usable code table
    /// has an empty list. No connection is opened when the form has no Legal Values field.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="form"/> is null.</exception>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<FieldOption>>> GetOptionsAsync(
        FormMetadata form,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);

        var optionsByField = new Dictionary<string, IReadOnlyList<FieldOption>>(StringComparer.OrdinalIgnoreCase);
        var legalValuesFields = new List<FieldMetadata>();
        foreach (FieldMetadata field in form.Fields)
        {
            if (FieldTypeMapper.Map(field) is (FieldType.LegalValues, false, _)
                && optionsByField.TryAdd(field.Name, NoOptions))
            {
                // The key is taken by the first field of that name, so a later row differing only by case cannot
                // replace its options.
                legalValuesFields.Add(field);
            }
        }

        if (legalValuesFields.Count == 0)
        {
            return optionsByField.AsReadOnly();
        }

        SqlConnection connection = await connectionFactory
            .OpenAsync(ReadOptionsOperation, cancellationToken)
            .ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            // One catalogue read resolves every code table of the form; each field then reads its own column.
            var codeTableNames = new List<string>(legalValuesFields.Count);
            foreach (FieldMetadata field in legalValuesFields)
            {
                if (CodeTableReference(field) is { } reference)
                {
                    codeTableNames.Add(reference.Table);
                }
            }

            IReadOnlyList<TableSchema> tables = await ReadTablesAsync(
                    connection,
                    transaction: null,
                    codeTableNames,
                    ReadOptionsOperation,
                    cancellationToken)
                .ConfigureAwait(false);

            foreach (FieldMetadata field in legalValuesFields)
            {
                optionsByField[field.Name] = await ReadOptionsCoreAsync(
                        connection,
                        transaction: null,
                        field,
                        tables,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return optionsByField.AsReadOnly();
    }

    /// <summary>Reads a form's check-code program, <c>metaViews.CheckCode</c>.</summary>
    /// <param name="viewId">The form's <c>metaViews.ViewId</c>.</param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>
    /// The program text unchanged, or <see langword="null"/> for an unknown form or a <c>NULL</c> program.
    /// </returns>
    public async Task<string?> GetCheckCodeAsync(int viewId, CancellationToken cancellationToken)
    {
        SqlConnection connection = await connectionFactory
            .OpenAsync(ReadCheckCodeOperation, cancellationToken)
            .ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            return await SqlExecution.ExecuteScalarAsync<string>(
                    connection,
                    transaction: null,
                    ReadCheckCodeOperation,
                    ViewIdStatement(MetadataQueries.CheckCode, viewId),
                    options.CommandTimeoutSeconds,
                    observer,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads one form's metadata and its catalogue schema on a connection the caller owns. The record repository
    /// calls it inside its write transaction, so every record operation sees the schema as it stands.
    /// </summary>
    /// <param name="connection">An open connection; this method neither opens nor closes it.</param>
    /// <param name="transaction">The transaction every command joins, or <see langword="null"/>.</param>
    /// <param name="viewId">The form's <c>metaViews.ViewId</c>.</param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>
    /// The form's metadata and schema, or <see langword="null"/> when <paramref name="viewId"/> is not in
    /// <c>metaViews</c>. Pages are in <c>Position</c>, then <c>PageId</c> order and fields in <c>FieldId</c> order,
    /// the system rows with a null <c>PageId</c> (<c>UniqueKey</c>, <c>RecStatus</c>, <c>GlobalRecordId</c>) included.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// The form's fields do not name exactly one non-blank data table, or that table is not in the catalogue.
    /// </exception>
    internal async Task<(FormMetadata Form, FormSchema Schema)?> ReadFormAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        int viewId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        IReadOnlyList<ViewRow> views = await QueryAsync<ViewRow>(
                connection,
                transaction,
                ReadFormOperation,
                ViewIdStatement(MetadataQueries.View, viewId),
                cancellationToken)
            .ConfigureAwait(false);
        if (views.Count == 0)
        {
            return null;
        }

        ViewRow view = views[0];

        // Legacy GetDataTableName takes the first row, or an empty string when there is none. A form whose fields
        // name no data table, or several, has no single base table to address, so it is treated as broken metadata.
        IReadOnlyList<string?> dataTableNames = await QueryAsync<string?>(
                connection,
                transaction,
                ReadFormOperation,
                ViewIdStatement(MetadataQueries.DataTableName, viewId),
                cancellationToken)
            .ConfigureAwait(false);
        string[] nonBlankNames = dataTableNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .ToArray();
        if (nonBlankNames.Length != 1)
        {
            throw new InvalidOperationException(DataTableNameMessage);
        }

        string baseTableName = nonBlankNames[0];

        IReadOnlyList<PageRow> pageRows = await QueryAsync<PageRow>(
                connection,
                transaction,
                ReadFormOperation,
                ViewIdStatement(MetadataQueries.Pages, viewId),
                cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<FieldRow> fieldRows = await QueryAsync<FieldRow>(
                connection,
                transaction,
                ReadFormOperation,
                ViewIdStatement(MetadataQueries.Fields, viewId),
                cancellationToken)
            .ConfigureAwait(false);

        // A page's table is named, as legacy Page.TableName names it, by the form's table name followed by the page id.
        var pageTableNames = new string[pageRows.Count];
        for (int index = 0; index < pageTableNames.Length; index++)
        {
            pageTableNames[index] = baseTableName + pageRows[index].PageId.ToString(CultureInfo.InvariantCulture);
        }

        IReadOnlyList<TableSchema> tables = await ReadTablesAsync(
                connection,
                transaction,
                pageTableNames.Prepend(baseTableName),
                ReadFormOperation,
                cancellationToken)
            .ConfigureAwait(false);

        TableSchema baseTable = MatchTable(tables, baseTableName)
            ?? throw new InvalidOperationException(BaseTableMissingMessage);

        var pages = new PageMetadata[pageRows.Count];
        var pageSchemas = new PageTableSchema[pageRows.Count];
        var tablesByPageId = new Dictionary<int, TableSchema?>(pageRows.Count);
        for (int index = 0; index < pages.Length; index++)
        {
            PageRow row = pageRows[index];
            TableSchema? pageTable = MatchTable(tables, pageTableNames[index]);
            pages[index] = new PageMetadata(
                row.PageId,
                row.Name,
                row.Position,
                pageTable?.Name ?? pageTableNames[index]);
            pageSchemas[index] = new PageTableSchema(pages[index], pageTable);
            tablesByPageId.TryAdd(row.PageId, pageTable);
        }

        var fields = new FieldMetadata[fieldRows.Count];
        for (int index = 0; index < fields.Length; index++)
        {
            FieldRow row = fieldRows[index];

            // Only a field on one of the form's pages owns a column, and only when that page's table has a column
            // of the field's name. Labels, groups, command buttons and the system rows therefore have none.
            ColumnMetadata? column = row.PageId is int pageId
                && tablesByPageId.TryGetValue(pageId, out TableSchema? pageTable)
                && pageTable is not null
                    ? pageTable.FindColumn(row.Name)?.Metadata
                    : null;

            fields[index] = new FieldMetadata
            {
                FieldId = row.FieldId,
                Name = row.Name,
                FieldTypeId = row.FieldTypeId,
                FieldTypeName = row.FieldTypeName,
                Prompt = row.Prompt,
                TabIndex = row.TabIndex,
                PageId = row.PageId,
                IsRequired = row.IsRequired ?? false,
                IsReadOnly = row.IsReadOnly ?? false,
                IsEncrypted = row.IsEncrypted ?? false,
                MaxLength = row.MaxLength,
                Lower = row.Lower,
                Upper = row.Upper,
                Pattern = row.Pattern,
                SourceTableName = row.SourceTableName,
                CodeColumnName = row.CodeColumnName,
                TextColumnName = row.TextColumnName,
                List = row.List,
                Sort = row.Sort ?? false,
                Column = column,
            };
        }

        var form = new FormMetadata(
            viewId,
            view.Name,
            baseTable.Name,
            Array.AsReadOnly(pages),
            Array.AsReadOnly(fields));
        var schema = new FormSchema(viewId, baseTable, pageSchemas);
        return (form, schema);
    }

    /// <summary>
    /// Reads the options of one Legal Values field from its code table, on a connection the caller owns.
    /// </summary>
    /// <param name="connection">An open connection; this method neither opens nor closes it.</param>
    /// <param name="transaction">The transaction every command joins, or <see langword="null"/>.</param>
    /// <param name="field">The field whose <c>SourceTableName</c> and <c>TextColumnName</c> name the code table.</param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>
    /// The distinct non-blank texts of the code-table column, each as <c>new FieldOption(FieldValue.FromText(text),
    /// text)</c>, in the order SQL Server returned them, or sorted by text when <see cref="FieldMetadata.Sort"/> is
    /// set. An empty list when either name is missing, or the table, or a character column of that name, is not in
    /// the catalogue.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/> or <paramref name="field"/> is null.</exception>
    internal async Task<IReadOnlyList<FieldOption>> ReadOptionsAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        FieldMetadata field,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(field);

        if (CodeTableReference(field) is not { } reference)
        {
            return NoOptions;
        }

        IReadOnlyList<TableSchema> tables = await ReadTablesAsync(
                connection,
                transaction,
                [reference.Table],
                ReadOptionsOperation,
                cancellationToken)
            .ConfigureAwait(false);

        return await ReadOptionsCoreAsync(connection, transaction, field, tables, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the catalogue schema of a session temporary table, for the reserved-word identifier test fixture only.
    /// </summary>
    /// <param name="open">
    /// An open connection with no pending transaction: the commands run without one. It is the session that owns
    /// the temporary table.
    /// </param>
    /// <param name="tempTableName">The session name of the table, starting with <c>#</c>, such as <c>#Reserved</c>.</param>
    /// <param name="cancellationToken">The caller's abort token.</param>
    /// <returns>
    /// The table under its session name, because the <c>tempdb</c> catalogue holds a mangled one, with its columns in
    /// <c>column_id</c> order.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="open"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="tempTableName"/> is blank or does not start with <c>#</c>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The connection is not open, or the session has no such temporary table or the table has no column.
    /// </exception>
    internal async Task<TableSchema> ReadTempTableSchemaAsync(
        SqlConnection open,
        string tempTableName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(open);

        if (open.State != ConnectionState.Open)
        {
            throw new InvalidOperationException(ConnectionNotOpenMessage);
        }

        if (string.IsNullOrWhiteSpace(tempTableName) || !tempTableName.StartsWith('#'))
        {
            throw new ArgumentException(TempTableNameMessage, nameof(tempTableName));
        }

        SqlParameterValue[] tableNameParameter =
        [
            ColumnValueConverter.Text(MetadataQueries.TableNameParameter, tempTableName),
        ];

        int? objectId = await SqlExecution.ExecuteScalarAsync<int?>(
                open,
                transaction: null,
                ReadTempTableOperation,
                new SqlStatement(MetadataQueries.TempTableObjectId, tableNameParameter),
                options.CommandTimeoutSeconds,
                observer,
                cancellationToken)
            .ConfigureAwait(false);
        if (objectId is null)
        {
            throw new InvalidOperationException(TempTableMissingMessage);
        }

        IReadOnlyList<ColumnRow> rows = await QueryAsync<ColumnRow>(
                open,
                transaction: null,
                ReadTempTableOperation,
                new SqlStatement(MetadataQueries.TempdbColumns, tableNameParameter),
                cancellationToken)
            .ConfigureAwait(false);
        if (rows.Count == 0)
        {
            throw new InvalidOperationException(TempTableMissingMessage);
        }

        return new TableSchema(tempTableName, ToColumns(rows));
    }

    // Resolves table names against the catalogue in one statement. Each returned table carries the catalogue's own
    // spelling of its table and column names; a name the catalogue does not know is simply absent from the result.
    private async Task<IReadOnlyList<TableSchema>> ReadTablesAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        IEnumerable<string> requestedNames,
        string operation,
        CancellationToken cancellationToken)
    {
        string[] names = requestedNames.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (names.Length == 0)
        {
            return Array.AsReadOnly(Array.Empty<TableSchema>());
        }

        // One JSON array keeps the statement text constant whatever the number of names; Text binds it as
        // DbType.String with size -1, the nvarchar(max) the statement expects.
        SqlParameterValue[] parameters =
        [
            ColumnValueConverter.Text(MetadataQueries.NamesParameter, JsonSerializer.Serialize(names)),
        ];

        IReadOnlyList<ColumnRow> rows = await QueryAsync<ColumnRow>(
                connection,
                transaction,
                operation,
                new SqlStatement(MetadataQueries.Columns, parameters),
                cancellationToken)
            .ConfigureAwait(false);

        // Grouped ordinally, so two tables whose names differ only by case (possible in a case-sensitive database)
        // stay apart; MatchTable then decides which one a requested name means.
        var rowsByTable = new Dictionary<string, List<ColumnRow>>(StringComparer.Ordinal);
        var tableOrder = new List<string>();
        foreach (ColumnRow row in rows)
        {
            if (!rowsByTable.TryGetValue(row.TableName, out List<ColumnRow>? tableRows))
            {
                tableRows = [];
                rowsByTable.Add(row.TableName, tableRows);
                tableOrder.Add(row.TableName);
            }

            tableRows.Add(row);
        }

        var tables = new TableSchema[tableOrder.Count];
        for (int index = 0; index < tables.Length; index++)
        {
            string tableName = tableOrder[index];
            tables[index] = new TableSchema(tableName, ToColumns(rowsByTable[tableName]));
        }

        return Array.AsReadOnly(tables);
    }

    // The exact ordinal match wins; otherwise the one case-insensitive match, because seeded metadata spells code
    // tables differently from the catalogue (codeState against codestate). Two case-insensitive matches with no exact
    // one, possible only in a case-sensitive database, are ambiguous and resolve to no table.
    private static TableSchema? MatchTable(IReadOnlyList<TableSchema> tables, string requested)
    {
        TableSchema? caseInsensitiveMatch = null;
        int caseInsensitiveCount = 0;
        foreach (TableSchema table in tables)
        {
            if (string.Equals(table.Name, requested, StringComparison.Ordinal))
            {
                return table;
            }

            if (string.Equals(table.Name, requested, StringComparison.OrdinalIgnoreCase))
            {
                caseInsensitiveMatch = table;
                caseInsensitiveCount++;
            }
        }

        return caseInsensitiveCount == 1 ? caseInsensitiveMatch : null;
    }

    // The code table and text column a Legal Values field names, trimmed as legacy trims TextColumnName, or null when
    // either is missing or blank.
    private static (string Table, string Column)? CodeTableReference(FieldMetadata field)
    {
        string? table = field.SourceTableName?.Trim();
        string? column = field.TextColumnName?.Trim();
        return string.IsNullOrEmpty(table) || string.IsNullOrEmpty(column) ? null : (table, column);
    }

    // Reads the field's code-table column once its table has been resolved from the catalogue. Legacy reads the whole
    // table without ORDER BY (SqlDatabase.GetTableData), keeps the distinct texts of TextColumnName under a
    // case-insensitive DataTable, and sorts the list only when the field's Sort flag is set.
    private async Task<IReadOnlyList<FieldOption>> ReadOptionsCoreAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        FieldMetadata field,
        IReadOnlyList<TableSchema> tables,
        CancellationToken cancellationToken)
    {
        if (CodeTableReference(field) is not { } reference
            || MatchTable(tables, reference.Table) is not { } table
            || table.FindColumn(reference.Column) is not { } column
            || column.Metadata.Family != ColumnFamily.Character)
        {
            // Includes legacy's "<form> - <page>" references to a form used as a code table, which are not supported.
            return NoOptions;
        }

        // The only statement with dynamic text: both identifiers are catalogue names this reader built.
        string text = "SELECT " + SqlIdentifier.Quote(column) + " FROM " + SqlIdentifier.Quote(table) + ";";
        IReadOnlyList<string?> values = await QueryAsync<string?>(
                connection,
                transaction,
                ReadOptionsOperation,
                new SqlStatement(text, NoParameters),
                cancellationToken)
            .ConfigureAwait(false);

        var seen = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);
        var texts = new List<string>(values.Count);
        foreach (string? value in values)
        {
            // Each text is kept exactly as stored, untrimmed, because a saved legal value must equal an option.
            if (!string.IsNullOrWhiteSpace(value) && seen.Add(value))
            {
                texts.Add(value);
            }
        }

        IEnumerable<string> ordered = field.Sort
            ? texts.OrderBy(value => value, StringComparer.InvariantCultureIgnoreCase)
            : texts;

        return Array.AsReadOnly(ordered.Select(value => new FieldOption(FieldValue.FromText(value), value)).ToArray());
    }

    private static ColumnSchema[] ToColumns(IEnumerable<ColumnRow> rows) =>
        rows.OrderBy(row => row.ColumnId)
            .Select(row => new ColumnSchema(
                row.ColumnName,
                row.TypeName,
                row.MaxLengthBytes,
                row.IsNullable ?? true,
                row.IsIdentity))
            .ToArray();

    private static SqlStatement ViewIdStatement(string text, int viewId) =>
        new(text, [ColumnValueConverter.Int32(MetadataQueries.ViewIdParameter, viewId)]);

    private Task<IReadOnlyList<T>> QueryAsync<T>(
        SqlConnection connection,
        SqlTransaction? transaction,
        string operation,
        SqlStatement statement,
        CancellationToken cancellationToken) =>
        SqlExecution.QueryAsync<T>(
            connection,
            transaction,
            operation,
            statement,
            options.CommandTimeoutSeconds,
            observer,
            cancellationToken);

    // Dapper row types: property names and CLR types match the column aliases and SQL types of MetadataQueries.

    private sealed class FormRow
    {
        public int ViewId { get; set; }

        public string Name { get; set; } = string.Empty;

        public int PageCount { get; set; }
    }

    private sealed class ViewRow
    {
        public int ViewId { get; set; }

        public string Name { get; set; } = string.Empty;
    }

    private sealed class PageRow
    {
        public int PageId { get; set; }

        public string Name { get; set; } = string.Empty;

        public short Position { get; set; }
    }

    private sealed class FieldRow
    {
        public int FieldId { get; set; }

        public string Name { get; set; } = string.Empty;

        public string? Prompt { get; set; }

        public decimal? TabIndex { get; set; }

        public int? PageId { get; set; }

        public bool? IsRequired { get; set; }

        public bool? IsReadOnly { get; set; }

        public bool? IsEncrypted { get; set; }

        public short? MaxLength { get; set; }

        public string? Lower { get; set; }

        public string? Upper { get; set; }

        public string? Pattern { get; set; }

        public string? SourceTableName { get; set; }

        public string? CodeColumnName { get; set; }

        public string? TextColumnName { get; set; }

        public bool? Sort { get; set; }

        public string? List { get; set; }

        public int FieldTypeId { get; set; }

        public string FieldTypeName { get; set; } = string.Empty;
    }

    private sealed class ColumnRow
    {
        // Empty for the tempdb fixture rows, whose statement returns no table name.
        public string TableName { get; set; } = string.Empty;

        public string ColumnName { get; set; } = string.Empty;

        public string TypeName { get; set; } = string.Empty;

        public short MaxLengthBytes { get; set; }

        public bool? IsNullable { get; set; }

        public bool IsIdentity { get; set; }

        public int ColumnId { get; set; }
    }
}
