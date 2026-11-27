using System.Data;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using EpiInfo.Data.Sql;
using EpiInfo.Domain.Forms;
using EpiInfo.Domain.Metadata;
using EpiInfo.Domain.Storage;
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
/// Nothing is kept across requests: <see cref="GetFormMetadataAsync"/> and every other read method send the commands
/// they need on every call, so a changed form or column type is seen by the next request. Within a request, the
/// catalogue schema <see cref="GetFormMetadataAsync"/> built stays attached to the exact instance it returned and is
/// released with it, so a record read given that instance takes the schema through
/// <see cref="ReuseOrReadFormAsync"/> with no command; any other instance is read again. Every statement is a
/// <c>SELECT</c>, runs through <see cref="SqlExecution"/> with the configured command timeout and the command
/// observer, and binds every value as a typed parameter. The one command with dynamic text, the code-table read, holds
/// one <c>SELECT</c> per distinct code-table column a request needs, and names only tables and columns this class built
/// from the catalogue and admitted as code tables.
/// </para>
/// <para>
/// Metadata names an object only in its role. A form is read and written only through its own record tables
/// (<see cref="ReadFormAsync"/>): its base table and a table for every one of its pages, whose names end in the pages'
/// identity <c>metaPages.PageId</c> values, must exist with the Epi Info record-table columns, and no name another
/// form's metadata claims may resolve to any of them, compared by the resolved catalogue table rather than by
/// spelling. A Legal Values field's options come only from a table of the shape legacy gives every code table: a name
/// that starts with <c>code</c>, character columns only, no identity column and no <c>GlobalRecordId</c> column
/// (<see cref="IsCodeTable"/>).
/// </para>
/// <para>
/// Failures to open a connection or to run a command are not caught here: <see cref="SqlConnectionFactory"/> and the
/// store that owns this reader classify them. The exceptions raised here carry fixed messages that name no table,
/// column, value or SQL text. The instance holds immutable collaborators and a thread-safe weak table of immutable
/// schemas, and is safe to share across threads.
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
    private const string PageTableMissingMessage = "A page's data table was not found in the database catalogue.";
    private const string TablesClaimedMessage = "The form's data tables are also named by another form's metadata.";
    private const string NotRecordTablesMessage = "The form's data tables are not Epi Info record tables.";
    private const string CodeTableResultsMessage = "The code-table read returned fewer result sets than it requested.";
    private const string TempTableMissingMessage = "The temporary table was not found.";
    private const string ConnectionNotOpenMessage = "The connection must be open before a temporary table is read.";
    private const string TempTableNameMessage =
        "A session temporary table name must be one '#' followed by 1 to 115 characters that neither start nor end with white space.";
    private const string NullTableNameMessage = "A requested table name is null.";

    // SQL Server's length limit for a local temporary table name, its '#' included.
    private const int MaxTempTableNameLength = 116;

    // Catalogue lookup keys of the columns every Epi Info record table holds, matched through TableSchema.FindColumn as
    // FormSchema matches them. They are never placed in SQL text.
    private const string UniqueKeyColumn = "UniqueKey";
    private const string RecStatusColumn = "RecStatus";
    private const string GlobalRecordIdColumn = "GlobalRecordId";

    // The prefix legacy gives every code table: Make View adds it to a new code table's name and lists only the tables
    // that carry it (Epi.Windows.MakeView/Dialogs/FieldDefinitionDialogs/CodeTableDefinition.cs:39-40,
    // Epi.Data.SqlServer/SqlDatabase.cs:2065-2070).
    private const string CodeTablePrefix = "code";

    private static readonly IReadOnlyList<FieldOption> NoOptions = Array.AsReadOnly(Array.Empty<FieldOption>());
    private static readonly IReadOnlyList<SqlParameterValue> NoParameters =
        Array.AsReadOnly(Array.Empty<SqlParameterValue>());

    private readonly SqlConnectionFactory connectionFactory =
        connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));

    private readonly DatabaseOptions options = options ?? throw new ArgumentNullException(nameof(options));

    private readonly ISqlCommandObserver observer = observer ?? throw new ArgumentNullException(nameof(observer));

    // The catalogue schema GetFormMetadataAsync built with each form it returned, keyed by that exact instance. The
    // table compares keys by reference, never by record equality, so a copy or another read of the same form finds
    // nothing, and it holds each key weakly, so an entry lives only as long as its form instance, which in the API ends
    // with the request that read it. Nothing is answered from it except to a caller holding that very instance.
    private readonly ConditionalWeakTable<FormMetadata, FormSchema> schemasByForm = new();

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
    /// <remarks>
    /// Every call opens a connection and sends the commands of <see cref="ReadFormAsync"/>; nothing is answered from
    /// an earlier call. The catalogue schema built by the same read stays attached to the returned instance alone, so
    /// a record read given that instance takes it through <see cref="ReuseOrReadFormAsync"/> without a command. The
    /// attachment ends when the instance is no longer referenced, which for the API is the end of the request. A form
    /// is returned only when its tables are its own record tables, as <see cref="ReadFormAsync"/> decides: its base
    /// table and a table for every one of its pages exist with the record-table columns, and no other form's metadata
    /// resolves to any of them.
    /// </remarks>
    /// <param name="viewId">The form's <c>metaViews.ViewId</c>.</param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>The form's metadata, or <see langword="null"/> when <paramref name="viewId"/> is not in <c>metaViews</c>.</returns>
    /// <exception cref="InvalidOperationException">
    /// The form's fields do not name exactly one data table; its base table or one of its page tables is not in the
    /// catalogue; one of them lacks the record-table columns; or a name another form's metadata claims resolves to one
    /// of them.
    /// </exception>
    public async Task<FormMetadata?> GetFormMetadataAsync(int viewId, CancellationToken cancellationToken)
    {
        SqlConnection connection = await connectionFactory
            .OpenAsync(ReadFormOperation, cancellationToken)
            .ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            if (await ReadFormAsync(connection, transaction: null, viewId, cancellationToken)
                    .ConfigureAwait(false) is not { } read)
            {
                return null;
            }

            // The instance was built by this read, so no entry can exist for it yet.
            schemasByForm.Add(read.Form, read.Schema);
            return read.Form;
        }
    }

    /// <summary>
    /// Reads the options of every Legal Values data field of <paramref name="form"/>: one entry per field of
    /// <see cref="FormDefinitionFactory.GetDataFields"/> whose type is <see cref="FieldType.LegalValues"/>.
    /// </summary>
    /// <remarks>
    /// At most two commands are sent, whatever the number of fields: one catalogue read resolves every code table
    /// the fields name, then one batch holds a <c>SELECT</c> of each distinct resolved table and column, so fields
    /// sharing a code table read it once. Only a usable code table is read: a table that <see cref="IsCodeTable"/>
    /// admits by its name and its all-character, non-identity columns, with a character column of the field's
    /// <c>TextColumnName</c>. The catalogue read that resolves the tables already carries every column, so the shape
    /// check sends no command of its own. Nothing is kept for a later call.
    /// </remarks>
    /// <param name="form">The form's metadata, as <see cref="GetFormMetadataAsync"/> returned it.</param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>
    /// The options keyed by canonical field name, compared ordinally, so two fields whose names differ only by letter
    /// case each have an entry holding their own code table's rows. The entries are the Legal Values data fields of
    /// <see cref="FormDefinitionFactory.GetDataFields"/>, so a row repeating a data field's name exactly, which the
    /// form definition lists as unsupported, neither supplies the entry of that name nor has its code table read. Each
    /// list is in the order SQL Server returned the rows, or sorted by text when the field's
    /// <see cref="FieldMetadata.Sort"/> is set. A field without a usable code table has an empty list: either name is
    /// missing, the table is not in the catalogue or is no code table (its name does not start with <c>code</c>, it
    /// has a column outside <see cref="ColumnFamily.Character"/> or an identity column, or it has a
    /// <c>GlobalRecordId</c> column), or it has no character column of that name. No connection is opened when no
    /// Legal Values data field names both a code table and a column.
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
    /// <remarks>
    /// The statement asks the server for at most <see cref="IDataStore.MaxCheckCodeLength"/> + 1 UTF-16 code units, so
    /// a program over the limit arrives one code unit longer than the limit, which the check-code engine reports, and
    /// the rest of it is never sent.
    /// </remarks>
    /// <param name="viewId">The form's <c>metaViews.ViewId</c>.</param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>
    /// The program text unchanged when it is at most <see cref="IDataStore.MaxCheckCodeLength"/> code units long,
    /// otherwise its first <see cref="IDataStore.MaxCheckCodeLength"/> + 1 code units; <see langword="null"/> for an
    /// unknown form or a <c>NULL</c> program.
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
                    new SqlStatement(
                        MetadataQueries.CheckCode,
                        [
                            ColumnValueConverter.Int32(MetadataQueries.ViewIdParameter, viewId),
                            ColumnValueConverter.Int32(
                                MetadataQueries.MaxLengthParameter,
                                IDataStore.MaxCheckCodeLength + 1),
                        ]),
                    options.CommandTimeoutSeconds,
                    observer,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads one form's metadata and its catalogue schema on a connection the caller owns, sending every command on
    /// each call; nothing is cached. The record repository calls it inside each write transaction, so a write sees the
    /// schema as it stands, and through <see cref="ReuseOrReadFormAsync"/> for a read whose form instance this reader
    /// did not return.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A form's tables must be its own record tables, decided on the same connection and transaction before any record
    /// SQL names them. After the catalogue read, <see cref="ResolveRecordTables"/> binds the base table and the table
    /// of every page: each must be in the catalogue, the base table must hold an identity <c>UniqueKey</c>,
    /// <c>RecStatus</c> and <c>GlobalRecordId</c>, and each page table must hold <c>GlobalRecordId</c>. A page whose
    /// table is missing refuses the form, so the binding rests on the form's identity <c>metaPages.PageId</c> values,
    /// which end its page table names.
    /// </para>
    /// <para>
    /// Then <see cref="MetadataQueries.TableClaims"/> reads the claims of the other forms' metadata that can name one
    /// of this form's tables, and the form is refused when one of them resolves to the very base or page table this
    /// form bound (<see cref="IsClaimedByAnotherForm"/>). A refused form is broken metadata, so no record read,
    /// permission check or write reaches its tables.
    /// </para>
    /// </remarks>
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
    /// The form's fields do not name exactly one non-blank data table; its base table or one of its page tables is not
    /// in the catalogue; one of them lacks the record-table columns; or a name another form's metadata claims resolves
    /// to one of them.
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

        (TableSchema baseTable, TableSchema[] pageTables) = ResolveRecordTables(tables, baseTableName, pageTableNames);

        // Legacy addresses whatever table DataTableName names (MetadataDbProvider.GetDataTableName), so metadata naming
        // another form's base or page table would have this form read that form's records and soft-delete them. A
        // form is therefore read and written only through tables no other form's metadata resolves to, as that
        // metadata stands on this connection and transaction, so a write checks it inside its own transaction before
        // any record SQL. The statement returns only the other forms' claims that can name one of this form's tables.
        IReadOnlyList<TableClaimRow> claims = await QueryAsync<TableClaimRow>(
                connection,
                transaction,
                ReadFormOperation,
                new SqlStatement(
                    MetadataQueries.TableClaims,
                    [
                        ColumnValueConverter.Int32(MetadataQueries.ViewIdParameter, viewId),
                        ColumnValueConverter.Text(MetadataQueries.BaseNameParameter, baseTableName),
                    ]),
                cancellationToken)
            .ConfigureAwait(false);
        if (IsClaimedByAnotherForm(
                viewId,
                tables,
                [baseTable, .. pageTables],
                claims.Select(claim => (claim.ViewId, claim.DataTableName, claim.PageId))))
        {
            throw new InvalidOperationException(TablesClaimedMessage);
        }

        var pages = new PageMetadata[pageRows.Count];
        var pageSchemas = new PageTableSchema[pageRows.Count];
        var tablesByPageId = new Dictionary<int, TableSchema?>(pageRows.Count);
        for (int index = 0; index < pages.Length; index++)
        {
            PageRow row = pageRows[index];
            TableSchema pageTable = pageTables[index];
            pages[index] = new PageMetadata(
                row.PageId,
                row.Name,
                row.Position,
                pageTable.Name);
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
    /// Supplies a form's metadata and catalogue schema to a record read outside any transaction. When
    /// <paramref name="form"/> is the very instance <see cref="GetFormMetadataAsync"/> returned, the schema that read
    /// built is returned with it and no command is sent; any other instance, a <see langword="with"/> copy or one
    /// built in memory included, is read again with <see cref="ReadFormAsync"/> on <paramref name="connection"/> by
    /// <see cref="FormMetadata.ViewId"/>. Either way the schema was built by this reader from the catalogue, from tables
    /// that <see cref="ReadFormAsync"/> found to be the form's own record tables.
    /// </summary>
    /// <param name="connection">An open connection; this method neither opens nor closes it.</param>
    /// <param name="form">The form's metadata, as the caller was given it for this request.</param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>
    /// The metadata and schema, the metadata being <paramref name="form"/> itself when its schema is attached; or
    /// <see langword="null"/> when a read finds the form's view id no longer in <c>metaViews</c>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/> or <paramref name="form"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// A read finds that the form's fields do not name exactly one non-blank data table; that its base table or one of
    /// its page tables is not in the catalogue; that one of them lacks the record-table columns; or that a name another
    /// form's metadata claims resolves to one of them.
    /// </exception>
    internal async Task<(FormMetadata Form, FormSchema Schema)?> ReuseOrReadFormAsync(
        SqlConnection connection,
        FormMetadata form,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(form);

        if (schemasByForm.TryGetValue(form, out FormSchema? schema))
        {
            return (form, schema);
        }

        return await ReadFormAsync(connection, transaction: null, form.ViewId, cancellationToken)
            .ConfigureAwait(false);
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
    /// name is missing, the table is not in the catalogue, the table is no code table as <see cref="IsCodeTable"/>
    /// decides from its name and its columns, or a character column of that name is not in it.
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

    /// <summary>
    /// Decides whether another form's metadata resolves to one of a form's own tables, the ownership rule
    /// <see cref="ReadFormAsync"/> applies once it has resolved them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Another form claims each of its non-blank data-table names and, for each of its pages, that name followed by the
    /// page id in invariant digits, the rule a form's own page tables follow. A claimed name is resolved as the form
    /// resolves its own names, with <c>MatchTable</c> among <paramref name="tables"/>: the exact ordinal match, else the
    /// one match ignoring case. It aliases the form when it resolves to the very instance of one of
    /// <paramref name="ownTables"/>. So two distinct tables whose names differ only by letter case, possible in a
    /// case-sensitive database, each belong to the form that names it exactly, while any spelling another form uses
    /// to reach one of this form's tables refuses this form.
    /// </para>
    /// <para>
    /// The catalogue read returns every table whose name equals a requested name under the case-insensitive collation
    /// of <see cref="MetadataQueries.Columns"/>, so every table a claimed name can resolve to, if it resolves to one of
    /// this form's tables, is in <paramref name="tables"/>, and the resolution here is the one the claiming form
    /// would make. Each claim costs one lookup, so the decision is linear in the claims. Blank names, and the rows of
    /// the form itself, claim nothing.
    /// </para>
    /// </remarks>
    /// <param name="viewId">The form's <c>metaViews.ViewId</c>.</param>
    /// <param name="tables">The catalogue tables one read returned for the form's base and page table names.</param>
    /// <param name="ownTables">The form's base table and page tables, as <see cref="ResolveRecordTables"/> bound them.</param>
    /// <param name="claims">
    /// The rows of <see cref="MetadataQueries.TableClaims"/>: a row's non-blank <c>DataTableName</c> claims that name
    /// when its <c>PageId</c> is null, and that name followed by the page id otherwise.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when a name another form claims resolves to one of <paramref name="ownTables"/>;
    /// otherwise <see langword="false"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="tables"/>, <paramref name="ownTables"/> or <paramref name="claims"/> is null.
    /// </exception>
    internal static bool IsClaimedByAnotherForm(
        int viewId,
        IReadOnlyList<TableSchema> tables,
        IReadOnlyCollection<TableSchema> ownTables,
        IEnumerable<(int ViewId, string? DataTableName, int? PageId)> claims)
    {
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(ownTables);
        ArgumentNullException.ThrowIfNull(claims);

        var own = new HashSet<TableSchema>(ownTables, ReferenceEqualityComparer.Instance);

        // The tables each claimed name can resolve to are exactly those equal to it ignoring case, so grouping them
        // by that key once lets every claim run MatchTable over its own group instead of over every table.
        var tablesByName = new Dictionary<string, List<TableSchema>>(StringComparer.OrdinalIgnoreCase);
        foreach (TableSchema table in tables)
        {
            if (!tablesByName.TryGetValue(table.Name, out List<TableSchema>? sameName))
            {
                sameName = [];
                tablesByName.Add(table.Name, sameName);
            }

            sameName.Add(table);
        }

        foreach ((int claimViewId, string? dataTableName, int? pageId) in claims)
        {
            if (claimViewId == viewId || string.IsNullOrWhiteSpace(dataTableName))
            {
                continue;
            }

            string claimedName = pageId is int claimPageId
                ? dataTableName + claimPageId.ToString(CultureInfo.InvariantCulture)
                : dataTableName;
            if (tablesByName.TryGetValue(claimedName, out List<TableSchema>? candidates)
                && MatchTable(candidates, claimedName) is { } resolved
                && own.Contains(resolved))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Binds a form's base table and the table of each of its pages to the catalogue tables their names resolve to,
    /// the binding <see cref="ReadFormAsync"/> applies before any record SQL can name them.
    /// </summary>
    /// <remarks>
    /// Each name resolves as <see cref="ReadTablesAsync"/> and <c>MatchTable</c> resolve every name: the exact ordinal
    /// match, else the one match ignoring case. Every page must have its table. Legacy creates the base table and one
    /// table per page together (<c>Epi.Core/Data/Services/CollectedDataProvider.cs:2568-2638</c>), and a page table
    /// name ends in its <c>metaPages.PageId</c>, an identity value metadata DML cannot set. A form whose page table
    /// is missing is therefore refused rather than read without it, so a form whose <c>DataTableName</c> names
    /// another form's base table, or an orphaned record table, also needs a table for each of its own page ids.
    /// </remarks>
    /// <param name="tables">The catalogue tables one read returned for the form's base and page table names.</param>
    /// <param name="baseTableName">The form's one non-blank <c>DataTableName</c>.</param>
    /// <param name="pageTableNames">Each page's table name, the base table name followed by the page id, in page order.</param>
    /// <returns>
    /// The base table and one table per page, in the order of <paramref name="pageTableNames"/>, each the very
    /// instance of <paramref name="tables"/> its name resolved to.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="tables"/>, <paramref name="baseTableName"/> or <paramref name="pageTableNames"/> is null.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The base table or a page table resolves to no table; the base table lacks an identity <c>UniqueKey</c>, a
    /// <c>RecStatus</c> or a <c>GlobalRecordId</c> (<see cref="IsRecordBaseTable"/>); or a page table lacks
    /// <c>GlobalRecordId</c> (<see cref="IsRecordPageTable"/>). The message is fixed and names no table.
    /// </exception>
    internal static (TableSchema BaseTable, TableSchema[] PageTables) ResolveRecordTables(
        IReadOnlyList<TableSchema> tables,
        string baseTableName,
        IReadOnlyList<string> pageTableNames)
    {
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(baseTableName);
        ArgumentNullException.ThrowIfNull(pageTableNames);

        TableSchema baseTable = MatchTable(tables, baseTableName)
            ?? throw new InvalidOperationException(BaseTableMissingMessage);
        if (!IsRecordBaseTable(baseTable))
        {
            throw new InvalidOperationException(NotRecordTablesMessage);
        }

        var pageTables = new TableSchema[pageTableNames.Count];
        for (int index = 0; index < pageTables.Length; index++)
        {
            TableSchema pageTable = MatchTable(tables, pageTableNames[index])
                ?? throw new InvalidOperationException(PageTableMissingMessage);
            if (!IsRecordPageTable(pageTable))
            {
                throw new InvalidOperationException(NotRecordTablesMessage);
            }

            pageTables[index] = pageTable;
        }

        return (baseTable, pageTables);
    }

    /// <summary>
    /// Decides whether a catalogue table has the shape of an Epi Info base table: an identity <c>UniqueKey</c>, a
    /// <c>RecStatus</c> and a <c>GlobalRecordId</c> column, the system columns record SQL addresses.
    /// </summary>
    /// <param name="table">The table the form's data-table name resolved to.</param>
    /// <returns>
    /// <see langword="true"/> when <see cref="TableSchema.FindColumn"/> finds all three columns and <c>UniqueKey</c>
    /// is an identity column; otherwise <see langword="false"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="table"/> is null.</exception>
    internal static bool IsRecordBaseTable(TableSchema table)
    {
        ArgumentNullException.ThrowIfNull(table);

        return table.FindColumn(UniqueKeyColumn) is { IsIdentity: true }
            && table.FindColumn(RecStatusColumn) is not null
            && table.FindColumn(GlobalRecordIdColumn) is not null;
    }

    /// <summary>
    /// Decides whether a catalogue table has the shape of an Epi Info page table: a <c>GlobalRecordId</c> column, the
    /// key that joins it to its base row.
    /// </summary>
    /// <param name="table">The table a page's table name resolved to.</param>
    /// <returns>
    /// <see langword="true"/> when <see cref="TableSchema.FindColumn"/> finds <c>GlobalRecordId</c>; otherwise
    /// <see langword="false"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="table"/> is null.</exception>
    internal static bool IsRecordPageTable(TableSchema table)
    {
        ArgumentNullException.ThrowIfNull(table);

        return table.FindColumn(GlobalRecordIdColumn) is not null;
    }

    /// <summary>
    /// Decides whether a catalogue table may serve a Legal Values field's options: it has the shape legacy gives every
    /// code table, a name starting with <c>code</c> in any letter case and character columns only, none of them an
    /// identity column, and it is no form's record table, so it has no <c>GlobalRecordId</c> column.
    /// </summary>
    /// <remarks>
    /// Legacy reads whatever <c>SourceTableName</c> names, routing a name without the prefix to the collected data
    /// (<c>Epi.Core/Project.cs:1315-1325</c>), while Make View lists and creates code tables only with it. Legacy creates
    /// a code table only through <c>MetadataDbProvider.CreateCodeTable</c>, whose every column is a 255-character
    /// string (<c>Epi.Core/Data/Services/MetadataDbProvider.cs:6083-6178, 6185-6199</c>), so a table with a numeric,
    /// date, binary or identity column is some other object that only its name makes look like a code table. Every
    /// Epi Info base and page table holds <c>GlobalRecordId</c>, a character column, so the last condition keeps out a
    /// record table whose form name starts with <c>code</c>, or whose form no longer exists.
    /// </remarks>
    /// <param name="table">The table a field's <c>SourceTableName</c> resolved to.</param>
    /// <returns>
    /// <see langword="true"/> when the catalogue name starts with <c>code</c> ignoring case, every column is in
    /// <see cref="ColumnFamily.Character"/> and none is an identity column, and <see cref="TableSchema.FindColumn"/>
    /// finds no <c>GlobalRecordId</c>; otherwise <see langword="false"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="table"/> is null.</exception>
    internal static bool IsCodeTable(TableSchema table)
    {
        ArgumentNullException.ThrowIfNull(table);

        return table.Name.StartsWith(CodeTablePrefix, StringComparison.OrdinalIgnoreCase)
            && table.Columns.All(column => column.Metadata.Family == ColumnFamily.Character && !column.IsIdentity)
            && table.FindColumn(GlobalRecordIdColumn) is null;
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
                || !IsCodeTable(table)
                || table.FindColumn(reference.Column) is not { } column
                || column.Metadata.Family != ColumnFamily.Character)
            {
                // Includes legacy's "<form> - <page>" references to a form used as a code table, which are not
                // supported, and any table outside the code-table role, such as a form's record table or a meta table,
                // whose rows the form definition would otherwise publish as options.
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
    /// ordinally. A SQL <c>NULL</c> is no stored text, so it is never offered as a choice. An <c>nchar(n)</c>
    /// code-table column returns every text padded with spaces to n, and each option keeps that padding verbatim. An
    /// option shorter than an <c>nchar</c> page column is saved as given and SQL Server pads it to the column's
    /// width, so it reads back padded, as a text that is no option.
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

    private sealed class TableClaimRow
    {
        public int ViewId { get; set; }

        // The claiming form's data-table name, spelled as stored; nullable as its metaFields column is.
        public string? DataTableName { get; set; }

        // Null on a base claim; the claiming form's page id on a page claim.
        public int? PageId { get; set; }
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
