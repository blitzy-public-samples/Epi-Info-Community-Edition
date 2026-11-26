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
/// command timeout and the command observer, and binds every value as a typed parameter. The one command with
/// dynamic text, the code-table read, holds one <c>SELECT</c> per distinct code-table column a request needs, and
/// names only tables and columns this class built from the catalogue.
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
    private const string ResolveTablesOperation = "metadata.resolveTables";

    // Fixed messages: the redaction policy keeps identifiers, values and SQL out of exception text.
    private const string DataTableNameMessage = "The form metadata does not name exactly one data table.";
    private const string BaseTableMissingMessage = "The form's data table was not found in the database catalogue.";
    private const string CodeTableResultsMessage = "The code-table read returned fewer result sets than it requested.";
    private const string TempTableMissingMessage = "The temporary table was not found.";
    private const string ConnectionNotOpenMessage = "The connection must be open before a temporary table is read.";
    private const string TempTableNameMessage =
        "A session temporary table name must be one '#' followed by 1 to 115 characters that neither start nor end with white space.";
    private const string NullTableNameMessage = "A requested table name is null.";

    // SQL Server's length limit for a local temporary table name, its '#' included.
    private const int MaxTempTableNameLength = 116;

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
    /// Reads the options of every Legal Values data field of <paramref name="form"/>: one entry per field of
    /// <see cref="FormDefinitionFactory.GetDataFields"/> whose type is <see cref="FieldType.LegalValues"/>.
    /// </summary>
    /// <remarks>
    /// At most two commands are sent, whatever the number of fields: one catalogue read resolves every code table
    /// the fields name, then one batch holds a <c>SELECT</c> of each distinct resolved table and column, so fields
    /// sharing a code table read it once. Nothing is kept for a later call.
    /// </remarks>
    /// <param name="form">The form's metadata, as <see cref="GetFormMetadataAsync"/> returned it.</param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>
    /// The options keyed by canonical field name, compared ordinally, so two fields whose names differ only by letter
    /// case each have an entry holding their own code table's rows. The entries are the Legal Values data fields of
    /// <see cref="FormDefinitionFactory.GetDataFields"/>, so a row repeating a data field's name exactly, which the
    /// form definition lists as unsupported, neither supplies the entry of that name nor has its code table read. Each
    /// list is in the order SQL Server returned the rows, or sorted by text when the field's
    /// <see cref="FieldMetadata.Sort"/> is set. A field without a usable code table has an empty list. No connection
    /// is opened when no Legal Values data field names both a code table and a column.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="form"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The code-table batch returned fewer result sets than it holds.</exception>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<FieldOption>>> GetOptionsAsync(
        FormMetadata form,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);

        var optionsByField = new Dictionary<string, IReadOnlyList<FieldOption>>(StringComparer.Ordinal);
        var legalValuesFields = new List<FieldMetadata>();
        foreach (DataFieldMetadata dataField in FormDefinitionFactory.GetDataFields(form))
        {
            // The data-field set is the one field-identity rule the definition, the validator and the record SQL
            // follow: it holds one field per exact canonical name, the first in storage order, so the row whose code
            // table is read is the row those consumers use. Fields whose names differ only by letter case are
            // distinct data fields, each reading its own code table, and never share a list.
            if (dataField.FieldType == FieldType.LegalValues)
            {
                optionsByField.Add(dataField.Name, NoOptions);
                legalValuesFields.Add(dataField.Field);
            }
        }

        // A field that names no code table or no column keeps its empty list without any read.
        if (!legalValuesFields.Exists(field => CodeTableReference(field) is not null))
        {
            return optionsByField.AsReadOnly();
        }

        SqlConnection connection = await connectionFactory
            .OpenAsync(ReadOptionsOperation, cancellationToken)
            .ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            IReadOnlyList<FieldOption>[] fieldOptions = await ReadOptionsCoreAsync(
                    connection,
                    transaction: null,
                    legalValuesFields,
                    cancellationToken)
                .ConfigureAwait(false);

            for (int index = 0; index < fieldOptions.Length; index++)
            {
                optionsByField[legalValuesFields[index].Name] = fieldOptions[index];
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
    /// Reads the options of one Legal Values field from its code table, on a connection the caller owns, with the
    /// same catalogue read and code-table read <see cref="GetOptionsAsync"/> batches for a whole form.
    /// </summary>
    /// <param name="connection">An open connection; this method neither opens nor closes it.</param>
    /// <param name="transaction">The transaction every command joins, or <see langword="null"/>.</param>
    /// <param name="field">The field whose <c>SourceTableName</c> and <c>TextColumnName</c> name the code table.</param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>
    /// The code-table column's texts as <see cref="ToOptions"/> builds them: every distinct non-null text exactly as
    /// stored, empty and white-space texts included, in the order SQL Server returned them, or sorted by text under
    /// <see cref="CultureInfo.CurrentCulture"/> when <see cref="FieldMetadata.Sort"/> is set. An empty list when either
    /// name is missing, or the table, or a character column of that name, is not in the catalogue.
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

        IReadOnlyList<FieldOption>[] fieldOptions = await ReadOptionsCoreAsync(
                connection,
                transaction,
                [field],
                cancellationToken)
            .ConfigureAwait(false);

        return fieldOptions[0];
    }

    /// <summary>
    /// Resolves table names against the catalogue alone, on a connection the caller owns: one <c>sys.tables</c> and
    /// <c>sys.columns</c> read for all of them, with no <c>meta*</c> read. The permission reader uses it to confirm a
    /// form's own base and page tables before it checks them.
    /// </summary>
    /// <param name="connection">An open connection; this method neither opens nor closes it.</param>
    /// <param name="transaction">The transaction the command joins, or <see langword="null"/>.</param>
    /// <param name="tableNames">
    /// The names to resolve, in any letter case. Repeats are read once. No command is sent when the list is empty.
    /// </param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>
    /// One entry per requested name, in request order: the table built from the catalogue, under its catalogue
    /// spelling, as an exact name match or else the one match ignoring case; or <see langword="null"/> when the
    /// unqualified name reaches no user table, or reaches several that differ only by case and none exactly.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="connection"/> or <paramref name="tableNames"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="tableNames"/> holds a null name.</exception>
    internal async Task<IReadOnlyList<TableSchema?>> ResolveTablesAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        IReadOnlyList<string> tableNames,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(tableNames);

        foreach (string name in tableNames)
        {
            if (name is null)
            {
                throw new ArgumentException(NullTableNameMessage, nameof(tableNames));
            }
        }

        IReadOnlyList<TableSchema> tables = await ReadTablesAsync(
                connection,
                transaction,
                tableNames,
                ResolveTablesOperation,
                cancellationToken)
            .ConfigureAwait(false);

        var resolved = new TableSchema?[tableNames.Count];
        for (int index = 0; index < resolved.Length; index++)
        {
            resolved[index] = MatchTable(tables, tableNames[index]);
        }

        return Array.AsReadOnly(resolved);
    }

    /// <summary>
    /// Builds the catalogue schema of a session temporary table, for the reserved-word identifier test fixture only.
    /// </summary>
    /// <remarks>
    /// Only a session-local name is admitted, so the fixture never reaches a global <c>##</c> table, which every
    /// session can see. The name is resolved once, as a single bracket-quoted part restricted to a user table, among
    /// the session's own temporary tables, and the columns are then read by the resolved <c>tempdb</c> object id.
    /// </remarks>
    /// <param name="open">
    /// An open connection with no pending transaction: the commands run without one. It is the session that owns
    /// the temporary table.
    /// </param>
    /// <param name="tempTableName">
    /// The session name of the table: one <c>#</c> followed by 1 to 115 characters that neither start nor end with
    /// white space, such as <c>#Reserved</c>. A <c>.</c> or <c>]</c> in it is part of the one name.
    /// </param>
    /// <param name="cancellationToken">The caller's abort token.</param>
    /// <returns>
    /// The table under its session name, because the <c>tempdb</c> catalogue holds a mangled one, with its columns in
    /// <c>column_id</c> order.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="open"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="tempTableName"/> is null, is not a session-local name (it lacks the leading <c>#</c> or starts
    /// with <c>##</c>), has nothing or white space just after the <c>#</c>, ends with white space, or is longer than
    /// the 116 characters SQL Server allows a local temporary table name. No command is sent.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The connection is not open, or the session has no temporary table of that name, including one that only
    /// another session holds, or the table has no column.
    /// </exception>
    internal async Task<TableSchema> ReadTempTableSchemaAsync(
        SqlConnection open,
        string tempTableName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(open);

        if (!IsSessionTempTableName(tempTableName))
        {
            throw new ArgumentException(TempTableNameMessage, nameof(tempTableName));
        }

        if (open.State != ConnectionState.Open)
        {
            throw new InvalidOperationException(ConnectionNotOpenMessage);
        }

        int? objectId = await SqlExecution.ExecuteScalarAsync<int?>(
                open,
                transaction: null,
                ReadTempTableOperation,
                new SqlStatement(
                    MetadataQueries.TempTableObjectId,
                    [ColumnValueConverter.Text(MetadataQueries.TableNameParameter, tempTableName)]),
                options.CommandTimeoutSeconds,
                observer,
                cancellationToken)
            .ConfigureAwait(false);
        if (objectId is not { } tempdbObjectId)
        {
            throw new InvalidOperationException(TempTableMissingMessage);
        }

        IReadOnlyList<ColumnRow> rows = await QueryAsync<ColumnRow>(
                open,
                transaction: null,
                ReadTempTableOperation,
                new SqlStatement(
                    MetadataQueries.TempdbColumns,
                    [ColumnValueConverter.Int32(MetadataQueries.ObjectIdParameter, tempdbObjectId)]),
                cancellationToken)
            .ConfigureAwait(false);
        if (rows.Count == 0)
        {
            throw new InvalidOperationException(TempTableMissingMessage);
        }

        return new TableSchema(tempTableName, ToColumns(rows));
    }

    // A session-local temporary table name: one '#', then a name that neither starts nor ends with white space, at
    // most MaxTempTableNameLength characters in all. A second leading '#' would name a global temporary table.
    private static bool IsSessionTempTableName(string? name) =>
        name is { Length: >= 2 and <= MaxTempTableNameLength }
        && name[0] == '#'
        && name[1] != '#'
        && !char.IsWhiteSpace(name[1])
        && !char.IsWhiteSpace(name[^1]);

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

    // The code table and text column a Legal Values field names, or null when either is missing or blank. Legacy looks
    // the table up by SourceTableName exactly as stored and trims only TextColumnName (ControlFactory.GetControls), so
    // a padded table name is kept as it is and matches no catalogue table, rather than reaching the unpadded one.
    private static (string Table, string Column)? CodeTableReference(FieldMetadata field)
    {
        string? table = field.SourceTableName;
        string? column = field.TextColumnName?.Trim();
        return string.IsNullOrWhiteSpace(table) || string.IsNullOrEmpty(column) ? null : (table, column);
    }

    // Resolves the code table and text column of every field with one catalogue read, then reads each distinct
    // resolved column once, all in one batch. Legacy reads the whole code table without ORDER BY
    // (SqlDatabase.GetTableData) for each field; one read of a table two fields share returns the same rows. The
    // result holds one list per field, in the order of fields.
    private async Task<IReadOnlyList<FieldOption>[]> ReadOptionsCoreAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        IReadOnlyList<FieldMetadata> fields,
        CancellationToken cancellationToken)
    {
        var fieldOptions = new IReadOnlyList<FieldOption>[fields.Count];
        Array.Fill(fieldOptions, NoOptions);

        var references = new (string Table, string Column)?[fields.Count];
        var codeTableNames = new List<string>(fields.Count);
        for (int index = 0; index < fields.Count; index++)
        {
            references[index] = CodeTableReference(fields[index]);
            if (references[index] is { } reference)
            {
                codeTableNames.Add(reference.Table);
            }
        }

        if (codeTableNames.Count == 0)
        {
            return fieldOptions;
        }

        IReadOnlyList<TableSchema> tables = await ReadTablesAsync(
                connection,
                transaction,
                codeTableNames,
                ReadOptionsOperation,
                cancellationToken)
            .ConfigureAwait(false);

        // Each resolved pair is read once. The key holds the catalogue spellings, which a value tuple of strings
        // compares ordinally, so fields that reach one column through differently cased metadata share its read.
        var pairs = new List<(TableSchema Table, ColumnSchema Column)>();
        var pairIndexes = new Dictionary<(string Table, string Column), int>();
        var fieldPairs = new int?[fields.Count];
        for (int index = 0; index < fields.Count; index++)
        {
            if (references[index] is not { } reference
                || MatchTable(tables, reference.Table) is not { } table
                || table.FindColumn(reference.Column) is not { } column
                || column.Metadata.Family != ColumnFamily.Character)
            {
                // Includes legacy's "<form> - <page>" references to a form used as a code table, which are not supported.
                continue;
            }

            if (!pairIndexes.TryGetValue((table.Name, column.Name), out int pairIndex))
            {
                pairIndex = pairs.Count;
                pairIndexes.Add((table.Name, column.Name), pairIndex);
                pairs.Add((table, column));
            }

            fieldPairs[index] = pairIndex;
        }

        if (pairs.Count == 0)
        {
            return fieldOptions;
        }

        IReadOnlyList<string?>[] storedTexts = await ReadCodeTableColumnsAsync(
                connection,
                transaction,
                pairs,
                cancellationToken)
            .ConfigureAwait(false);

        // Legacy sorts under the current culture of the process running data entry; the API process is the local
        // user's own, so its current culture is read here, when the lists are built, and never kept.
        CultureInfo culture = CultureInfo.CurrentCulture;
        for (int index = 0; index < fields.Count; index++)
        {
            if (fieldPairs[index] is int pairIndex)
            {
                fieldOptions[index] = ToOptions(storedTexts[pairIndex], fields[index].Sort, culture);
            }
        }

        return fieldOptions;
    }

    // The only command with dynamic text: one SELECT per pair, each naming a catalogue table and column this reader
    // built, with no ORDER BY, as legacy reads a code table. The result sets are read in the order of the pairs, and a
    // SQL NULL text arrives as null.
    private Task<IReadOnlyList<string?>[]> ReadCodeTableColumnsAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        IReadOnlyList<(TableSchema Table, ColumnSchema Column)> pairs,
        CancellationToken cancellationToken)
    {
        var statements = new string[pairs.Count];
        for (int index = 0; index < statements.Length; index++)
        {
            (TableSchema table, ColumnSchema column) = pairs[index];
            statements[index] = "SELECT " + SqlIdentifier.Quote(column) + " FROM " + SqlIdentifier.Quote(table) + ";";
        }

        return SqlExecution.ReadAsync(
            connection,
            transaction,
            ReadOptionsOperation,
            new SqlStatement(string.Join('\n', statements), NoParameters),
            options.CommandTimeoutSeconds,
            observer,
            cancellationToken,
            async (reader, token) =>
            {
                var columns = new IReadOnlyList<string?>[pairs.Count];
                for (int index = 0; index < columns.Length; index++)
                {
                    if (index > 0 && !await reader.NextResultAsync(token).ConfigureAwait(false))
                    {
                        throw new InvalidOperationException(CodeTableResultsMessage);
                    }

                    var texts = new List<string?>();
                    while (await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        texts.Add(reader.IsDBNull(0) ? null : reader.GetString(0));
                    }

                    columns[index] = texts.AsReadOnly();
                }

                return columns;
            });
    }

    /// <summary>
    /// Turns the texts of one code-table column, in the order SQL Server returned them, into a field's options.
    /// </summary>
    /// <remarks>
    /// Legacy keeps the distinct texts with <c>DataView.ToTable(true, ...)</c>, which compares values with
    /// <c>Equals</c>, so case variants stay apart, and it lists blank texts. Each text here is likewise kept exactly
    /// as stored, untrimmed, and only an exact repeat is collapsed, because a saved legal value must equal an option
    /// ordinally. A SQL <c>NULL</c> is no stored text, so it is never offered as a choice.
    /// <para>
    /// Legacy sorts a <c>Sort</c> = 1 list with <c>ComboBox.Sorted</c>, whose item comparer calls the current culture
    /// of the process running data entry with <see cref="CompareOptions.StringSort"/>, case-sensitively. The reader
    /// passes the API process's current culture, so the list sorts as legacy sorts it on the same machine.
    /// </para>
    /// </remarks>
    /// <param name="storedTexts">The column's texts, <see langword="null"/> for a SQL <c>NULL</c>.</param>
    /// <param name="sort">The field's <see cref="FieldMetadata.Sort"/> flag.</param>
    /// <param name="culture">
    /// The culture a sorted list is compared under; it does not affect an unsorted list.
    /// </param>
    /// <returns>
    /// One <c>new FieldOption(FieldValue.FromText(text), text)</c> per distinct non-null text, the empty text and
    /// white-space texts included, at its first occurrence in the given order. When <paramref name="sort"/> is set
    /// they are sorted by text instead: <paramref name="culture"/> with <see cref="CompareOptions.StringSort"/>,
    /// case-sensitive, then ordinal for texts the culture ranks equal, whose order legacy leaves undefined.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="storedTexts"/> or <paramref name="culture"/> is null.
    /// </exception>
    internal static IReadOnlyList<FieldOption> ToOptions(
        IEnumerable<string?> storedTexts,
        bool sort,
        CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(storedTexts);
        ArgumentNullException.ThrowIfNull(culture);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var texts = new List<string>();
        foreach (string? value in storedTexts)
        {
            if (value is not null && seen.Add(value))
            {
                texts.Add(value);
            }
        }

        // The one sort of a Legal Values list: the form definition keeps this order as given. The ordinal tie-break
        // gives a fixed order to distinct texts the culture ranks equal, such as "e\u0301" and "\u00e9".
        IEnumerable<string> ordered = sort
            ? texts.OrderBy(value => value, culture.CompareInfo.GetStringComparer(CompareOptions.StringSort))
                .ThenBy(value => value, StringComparer.Ordinal)
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
