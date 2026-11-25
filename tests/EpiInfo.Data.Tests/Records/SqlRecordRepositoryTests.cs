using System.Collections.ObjectModel;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using EpiInfo.Data.Metadata;
using EpiInfo.Data.Records;
using EpiInfo.Data.Sql;
using EpiInfo.Domain.Forms;
using EpiInfo.Domain.Metadata;
using EpiInfo.Domain.Records;
using EpiInfo.Domain.Search;
using EpiInfo.Domain.Values;
using EpiInfo.Tests.Shared;
using Microsoft.Data.SqlClient;

namespace EpiInfo.Data.Tests.Records;

/// <summary>
/// Integration tests of the record statements the repository builds and runs, against the seeded forms of the
/// rehearsal database. The metadata reader supplies every form, page, table and column schema, so each identifier a
/// statement names is one the catalogue returned. The cases pin two contracts:
/// <list type="bullet">
/// <item><description>
/// The page-row statements the repository sends when it creates and updates a record,
/// <see cref="RecordSqlBuilder.InsertPage"/> and <see cref="RecordSqlBuilder.UpdatePageColumns"/>: they name writable
/// columns only, and each column binds only its own field's value.
/// </description></item>
/// <item><description>
/// Record identity: every lookup by <c>GlobalRecordId</c>, the locked read before a write included, matches a stored
/// id exactly, so a spelling that differs from it only by trailing spaces, within or beyond the column's width, finds
/// no row; and the base-row reads return the id as stored, the spelling every later statement binds.
/// </description></item>
/// </list>
/// </summary>
/// <remarks>
/// The tests only read. The reader's commands are all <c>SELECT</c>, and the page-row statements are only built, never
/// sent. The identity lookups run their locked read in a transaction that is rolled back, and the record they look up
/// is the seeded row with the lowest <c>UniqueKey</c>, chosen by SQL rather than hard-coded. Their failure messages
/// are fixed text, so no id reaches the output except through an equality assertion.
/// </remarks>
[Collection(DatabaseCollections.Database)]
[Trait("Category", "Integration")]
public sealed class SqlRecordRepositoryTests : IAsyncLifetime
{
    private const string GlobalRecordId = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";
    private const string ReadOnlyValue = "R-1";
    private const double ReadOnlyNumber = 17;

    // The fixed operation name every command of the identity tests reports to the recorder.
    private const string Operation = "test.recordIdentity";

    // Trailing spaces that keep the padded id within the seeded nvarchar(255) column.
    private const int WithinWidthPadding = 3;

    // Trailing spaces that take the padded id beyond the seeded nvarchar(255) column.
    private const int OverWidthPadding = 300;

    private static readonly IReadOnlyDictionary<string, FieldValue> NoValues = new Dictionary<string, FieldValue>();

    /// <summary>The seeded forms.</summary>
    public static TheoryData<string> Forms => new() { "CaseReport", "FoodHistory" };

    /// <summary>Each seeded form with each padding: within and beyond the width of its id column.</summary>
    public static TheoryData<string, int> PaddedIds => new()
    {
        { "CaseReport", WithinWidthPadding },
        { "CaseReport", OverWidthPadding },
        { "FoodHistory", WithinWidthPadding },
        { "FoodHistory", OverWidthPadding },
    };

    public Task InitializeAsync() => TestDatabase.EnsureReachableAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // CaseReport1 holds the seeded read-only data fields StateIDNumber, CDCIDNumber and ReportedToCDCDate
    // (testdata/sql/03_forms.sql) beside the writable Age and LastName.
    [Fact]
    public async Task InsertPage_ReadOnlyField_IsLeftOutOfTheColumnAndParameterLists()
    {
        ReaderForm form = await ReaderForm.ReadAsync("CaseReport");
        PageTableSchema page = form.PageOf(form.Column("Age").Field);
        IReadOnlyList<ProjectedColumn> pageColumns = form.ColumnsOn(page);
        IReadOnlyDictionary<string, FieldValue> values = Values(
            ignoreCase: false,
            ("Age", FieldValue.FromNumber(5)),
            ("LastName", FieldValue.FromText("Ada")));

        SqlStatement statement = RecordSqlBuilder.InsertPage(page, GlobalRecordId, pageColumns, values);

        IReadOnlyList<(string Column, SqlParameterValue Parameter)> inserted = PageInsert.Columns(statement);
        string[] writable = pageColumns
            .Where(column => !column.Field.Field.IsReadOnly)
            .Select(column => SqlIdentifier.Quote(column.Column))
            .ToArray();
        Assert.Equal(writable.Prepend("[GlobalRecordId]"), inserted.Select(entry => entry.Column));
        Assert.Equal(writable.Length + 1, statement.Parameters.Count);
        Assert.All(new[] { "StateIDNumber", "CDCIDNumber", "ReportedToCDCDate" }, name =>
        {
            Assert.True(form.Column(name).Field.Field.IsReadOnly);
            Assert.DoesNotContain("[" + name + "]", statement.Text, StringComparison.Ordinal);
        });
        Assert.Equal(GlobalRecordId, inserted[0].Parameter.Value);
        AssertBound(inserted, "[Age]", 5d);
        Assert.Equal(
            "Ada",
            Assert.Single(
                inserted,
                entry => string.Equals(entry.Column, "[LastName]", StringComparison.Ordinal)).Parameter.Value);
        Assert.All(
            inserted.Skip(1).Where(entry => entry.Column is not ("[Age]" or "[LastName]")),
            entry => Assert.Same(DBNull.Value, entry.Parameter.Value));
    }

    // An in-memory form, whose rows are written to no metadata table, joins the reader's CaseReport page (table
    // CaseReport1) and FoodHistory page (table FoodHistory2), each with its own seeded Age field and column. The
    // FoodHistory field is renamed AGE, so Age and AGE are distinct canonical fields whose names differ only by case.
    // Each column takes only the value keyed by its own field's exact name and the other binds NULL, whether the
    // caller's map compares keys ordinally or ignoring case.
    [Theory]
    [InlineData("Age", 5d, true, 5d, null)]
    [InlineData("Age", 5d, false, 5d, null)]
    [InlineData("AGE", 7d, true, null, 7d)]
    [InlineData("AGE", 7d, false, null, 7d)]
    public async Task InsertPage_CaseVariantFieldsOnTwoPages_EachColumnBindsOnlyItsExactKey(
        string submittedName,
        double submittedValue,
        bool mapIgnoresCase,
        double? expectedCaseReportAge,
        double? expectedFoodHistoryAge)
    {
        ReaderForm caseReport = await ReaderForm.ReadAsync("CaseReport");
        ReaderForm foodHistory = await ReaderForm.ReadAsync("FoodHistory");
        DataFieldMetadata caseReportAge = caseReport.Column("Age").Field;
        DataFieldMetadata foodHistoryAge = foodHistory.Column("Age").Field;
        FormMetadata caseVariantForm = caseReport.Metadata with
        {
            Pages = [caseReportAge.Page, foodHistoryAge.Page],
            Fields = [caseReportAge.Field, foodHistoryAge.Field with { Name = "AGE" }],
        };

        // Each field resolves against its own form's reader-built schema, the only one that holds its page.
        ProjectedColumn[] columns = FormDefinitionFactory.GetDataFields(caseVariantForm)
            .Select((field, ordinal) => RecordSqlBuilder.ResolveDataColumn(
                field.Page.PageId == caseReportAge.Page.PageId ? caseReport.Schema : foodHistory.Schema,
                field,
                ordinal))
            .ToArray();
        Assert.Equal(new[] { "Age", "AGE" }, columns.Select(column => column.Field.Name));
        Assert.Same(caseReport.Column("Age").Column, columns[0].Column);
        Assert.Same(foodHistory.Column("Age").Column, columns[1].Column);

        IReadOnlyDictionary<string, FieldValue> values =
            Values(mapIgnoresCase, (submittedName, FieldValue.FromNumber(submittedValue)));
        Assert.Equal(mapIgnoresCase, values.ContainsKey("age"));

        SqlStatement caseReportRow = RecordSqlBuilder.InsertPage(
            caseReport.PageOf(columns[0].Field),
            GlobalRecordId,
            [columns[0]],
            values);
        SqlStatement foodHistoryRow = RecordSqlBuilder.InsertPage(
            foodHistory.PageOf(columns[1].Field),
            GlobalRecordId,
            [columns[1]],
            values);

        // Both seeded columns are spelled Age, so the two rows differ by their table.
        Assert.StartsWith("INSERT INTO [CaseReport1] (", caseReportRow.Text, StringComparison.Ordinal);
        Assert.StartsWith("INSERT INTO [FoodHistory2] (", foodHistoryRow.Text, StringComparison.Ordinal);
        AssertBound(PageInsert.Columns(caseReportRow), "[Age]", expectedCaseReportAge);
        AssertBound(PageInsert.Columns(foodHistoryRow), "[Age]", expectedFoodHistoryAge);
    }

    // A read-only field present in values is refused whether its value is of its type or null: presence is the rule.
    // Seeded StateIDNumber (CaseReport1) is text and OnsetWeek (FoodHistory2) a number; Age shares each page.
    [Theory]
    [InlineData("CaseReport", "StateIDNumber", false)]
    [InlineData("CaseReport", "StateIDNumber", true)]
    [InlineData("FoodHistory", "OnsetWeek", false)]
    [InlineData("FoodHistory", "OnsetWeek", true)]
    public async Task InsertPage_ValueForReadOnlyField_IsRefused(string formName, string readOnlyField, bool submitNull)
    {
        ReaderForm form = await ReaderForm.ReadAsync(formName);
        ProjectedColumn readOnly = form.Column(readOnlyField);
        Assert.True(readOnly.Field.Field.IsReadOnly);
        PageTableSchema page = form.PageOf(readOnly.Field);
        (FieldValue sample, string sampleText) = SampleValue(readOnly.Field);
        IReadOnlyDictionary<string, FieldValue> values = Values(
            ignoreCase: false,
            ("Age", FieldValue.FromNumber(5)),
            (readOnlyField, submitNull ? FieldValue.Null : sample));

        ArgumentException failure = Assert.Throws<ArgumentException>(() =>
            RecordSqlBuilder.InsertPage(page, GlobalRecordId, form.ColumnsOn(page), values));

        Assert.Equal("values", failure.ParamName);
        Assert.DoesNotContain(sampleText, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(readOnlyField, failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("CaseReport", "StateIDNumber")]
    [InlineData("FoodHistory", "OnsetWeek")]
    public async Task UpdatePageColumns_ReadOnlyColumn_IsRefusedBeforeAnyStatementIsBuilt(
        string formName,
        string readOnlyField)
    {
        ReaderForm form = await ReaderForm.ReadAsync(formName);
        ProjectedColumn readOnly = form.Column(readOnlyField);
        ProjectedColumn lastName = form.Column("LastName");
        Assert.True(readOnly.Field.Field.IsReadOnly);

        // Both columns are on one page, so the refusal cannot come from a column foreign to the page.
        Assert.Equal(readOnly.Field.Page.PageId, lastName.Field.Page.PageId);
        (FieldValue sample, string sampleText) = SampleValue(readOnly.Field);
        var submitted = new List<(ProjectedColumn Column, FieldValue Value)>
        {
            (lastName, FieldValue.FromText("Ada")),
            (readOnly, sample),
        };

        ArgumentException failure = Assert.Throws<ArgumentException>(() =>
            RecordSqlBuilder.UpdatePageColumns(form.PageOf(readOnly.Field), GlobalRecordId, submitted));

        Assert.Equal("submitted", failure.ParamName);
        Assert.DoesNotContain(sampleText, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(readOnlyField, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdatePageColumns_WritableColumns_SetOnlyTheSubmittedColumns()
    {
        ReaderForm form = await ReaderForm.ReadAsync("CaseReport");
        ProjectedColumn lastName = form.Column("LastName");
        var submitted = new List<(ProjectedColumn Column, FieldValue Value)>
        {
            (lastName, FieldValue.FromText("Ada")),
        };

        IReadOnlyList<SqlStatement> statements =
            RecordSqlBuilder.UpdatePageColumns(form.PageOf(lastName.Field), GlobalRecordId, submitted);

        SqlStatement statement = Assert.Single(statements);
        Assert.Equal("UPDATE [CaseReport1] SET [LastName] = @p0 WHERE [GlobalRecordId] = @p1", statement.Text);
        Assert.Equal(new object[] { "Ada", GlobalRecordId }, statement.Parameters.Select(parameter => parameter.Value));
    }

    // The seeded read-only data fields (testdata/sql/03_forms.sql): StateIDNumber, CDCIDNumber and ReportedToCDCDate
    // on CaseReport1, and OnsetWeek on FoodHistory2.
    [Theory]
    [InlineData("CaseReport", new[] { "StateIDNumber", "CDCIDNumber", "ReportedToCDCDate" })]
    [InlineData("FoodHistory", new[] { "OnsetWeek" })]
    public async Task InsertPage_EveryDataColumnOfSeededForm_NamesTheWritableColumnsAndNoReadOnlyOne(
        string formName,
        string[] readOnlyFields)
    {
        ReaderForm form = await ReaderForm.ReadAsync(formName);

        // Each seeded read-only field is a data field handed to the builder, so its absence below is an exclusion.
        foreach (string name in readOnlyFields)
        {
            Assert.True(form.Column(name).Field.Field.IsReadOnly);
        }

        var texts = new List<string>();
        foreach (PageTableSchema page in form.Schema.Pages)
        {
            if (page.Table is not { } table)
            {
                continue;
            }

            IReadOnlyList<ProjectedColumn> pageColumns = form.ColumnsOn(page);
            SqlStatement statement = RecordSqlBuilder.InsertPage(
                page,
                RecordSqlBuilder.NewGlobalRecordId(),
                pageColumns,
                NoValues);

            IReadOnlyList<(string Column, SqlParameterValue Parameter)> inserted = PageInsert.Columns(statement);
            IEnumerable<string> expected = pageColumns
                .Where(column => !column.Field.Field.IsReadOnly)
                .Select(column => SqlIdentifier.Quote(column.Column))
                .Prepend(SqlIdentifier.Quote(RecordSqlBuilder.RequirePageKey(table)));
            Assert.Equal(expected, inserted.Select(entry => entry.Column));

            // Provenance: every bracketed column of the INSERT is a column the reader returned for this page table.
            Assert.All(inserted, entry =>
                Assert.Contains(entry.Column, table.Columns.Select(column => SqlIdentifier.Quote(column))));
            texts.Add(statement.Text);
        }

        Assert.All(readOnlyFields, name =>
            Assert.All(texts, text => Assert.DoesNotContain("[" + name + "]", text, StringComparison.Ordinal)));
        Assert.Contains(texts, text => text.Contains("[LastName]", StringComparison.Ordinal));
    }

    // The float column's parameter holds the expected number, or DBNull when the field was not submitted.
    private static void AssertBound(
        IReadOnlyList<(string Column, SqlParameterValue Parameter)> inserted,
        string column,
        double? expected)
    {
        SqlParameterValue parameter = Assert.Single(
            inserted,
            entry => string.Equals(entry.Column, column, StringComparison.Ordinal)).Parameter;
        Assert.Equal(DbType.Double, parameter.DbType);
        if (expected is { } number)
        {
            Assert.Equal(number, parameter.Value);
        }
        else
        {
            Assert.Same(DBNull.Value, parameter.Value);
        }
    }

    // A read-only values map keyed by canonical field names, whose lookups compare ordinally or ignore case.
    private static IReadOnlyDictionary<string, FieldValue> Values(
        bool ignoreCase,
        params (string Name, FieldValue Value)[] entries)
    {
        var values = new Dictionary<string, FieldValue>(
            entries.Length,
            ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach ((string name, FieldValue value) in entries)
        {
            values.Add(name, value);
        }

        return new ReadOnlyDictionary<string, FieldValue>(values);
    }

    // A value of the read-only field's own type with its text, so the refusal rests on the field's presence alone.
    private static (FieldValue Value, string Text) SampleValue(DataFieldMetadata field) => field.FieldType switch
    {
        FieldType.Text => (FieldValue.FromText(ReadOnlyValue), ReadOnlyValue),
        FieldType.Number => (
            FieldValue.FromNumber(ReadOnlyNumber),
            ReadOnlyNumber.ToString(CultureInfo.InvariantCulture)),
        _ => throw new ArgumentOutOfRangeException(
            nameof(field),
            field.FieldType,
            "The tests sample read-only text and number fields only."),
    };

    [Theory]
    [MemberData(nameof(Forms))]
    public async Task IdLookups_ExactStoredId_ReturnTheOneRowWithItsStoredId(string formName)
    {
        await TestDatabase.EnsureReachableAsync();
        await using DatabaseWriteLock dbLock = await DatabaseWriteLock.AcquireSharedAsync();

        var recorder = new SqlCommandRecorder();
        await using SeededForm form = await SeededForm.OpenAsync(formName, recorder);
        recorder.Clear();

        IReadOnlyList<IdRow> positioned = await form.ReadBaseRowsAsync(
            transaction: null,
            RecordSqlBuilder.SetPositionLookup(form.Schema, form.GlobalRecordId));
        AssertSingleStoredRow(positioned, form, "The position lookup did not return exactly the seeded row.");

        IReadOnlyList<IdRow> loaded = await form.ReadRecordRowsAsync(
            transaction: null,
            RecordSqlBuilder.RecordById(form.Schema, form.DataFields, form.GlobalRecordId));
        AssertSingleStoredRow(loaded, form, "The read by id did not return exactly the seeded row.");

        await using (SqlTransaction transaction = await form.BeginTransactionAsync())
        {
            IReadOnlyList<IdRow> locked = await form.ReadBaseRowsAsync(
                transaction,
                RecordSqlBuilder.LockBase(form.Schema, form.GlobalRecordId));
            AssertSingleStoredRow(locked, form, "The locked read did not return exactly the seeded row.");

            // The later statements of a write bind the key and the id the locked read returned.
            IReadOnlyList<IdRow> reread = await form.ReadRecordRowsAsync(
                transaction,
                RecordSqlBuilder.RecordByUniqueKey(
                    form.Schema,
                    form.DataFields,
                    locked[0].UniqueKey,
                    locked[0].GlobalRecordId));
            AssertSingleStoredRow(reread, form, "The read by key did not return exactly the locked row.");

            await transaction.RollbackAsync();
        }

        AssertOnlySuccessfulReads(recorder, expectedCommands: 4);
    }

    [Theory]
    [MemberData(nameof(PaddedIds))]
    public async Task IdLookups_IdWithTrailingSpaces_FindNoRowAndChangeNothing(string formName, int padding)
    {
        await TestDatabase.EnsureReachableAsync();

        // Exclusive, because the test compares snapshots taken before and after it.
        await using DatabaseWriteLock dbLock = await DatabaseWriteLock.AcquireExclusiveAsync();

        var recorder = new SqlCommandRecorder();
        await using SeededForm form = await SeededForm.OpenAsync(formName, recorder);
        string paddedId = form.GlobalRecordId + new string(' ', padding);
        bool overWidth = paddedId.Length * sizeof(char) > form.GlobalRecordIdColumn.MaxLengthBytes;
        Assert.True(
            overWidth == (padding == OverWidthPadding),
            "The padded id is not on the intended side of the id column's width.");

        SqlSnapshot before = await SqlSnapshot.CaptureAsync();
        recorder.Clear();

        IReadOnlyList<IdRow> positioned = await form.ReadBaseRowsAsync(
            transaction: null,
            RecordSqlBuilder.SetPositionLookup(form.Schema, paddedId));
        Assert.True(positioned.Count == 0, "The position lookup matched a padded id to a stored row.");

        IReadOnlyList<IdRow> loaded = await form.ReadRecordRowsAsync(
            transaction: null,
            RecordSqlBuilder.RecordById(form.Schema, form.DataFields, paddedId));
        Assert.True(loaded.Count == 0, "The read by id matched a padded id to a stored row.");

        await using (SqlTransaction transaction = await form.BeginTransactionAsync())
        {
            IReadOnlyList<IdRow> locked = await form.ReadBaseRowsAsync(
                transaction,
                RecordSqlBuilder.LockBase(form.Schema, paddedId));
            Assert.True(locked.Count == 0, "The locked read matched a padded id to a stored row.");

            await transaction.RollbackAsync();
        }

        AssertOnlySuccessfulReads(recorder, expectedCommands: 3);

        SqlSnapshot after = await SqlSnapshot.CaptureAsync();
        Assert.True(SqlSnapshot.Compare(before, after).IsEmpty, "The lookups changed stored data.");
    }

    [Theory]
    [MemberData(nameof(Forms))]
    public async Task RecordByUniqueKey_StoredKeyWithTrailingSpacesOnTheId_FindsNoRow(string formName)
    {
        await TestDatabase.EnsureReachableAsync();
        await using DatabaseWriteLock dbLock = await DatabaseWriteLock.AcquireSharedAsync();

        var recorder = new SqlCommandRecorder();
        await using SeededForm form = await SeededForm.OpenAsync(formName, recorder);
        recorder.Clear();

        IReadOnlyList<IdRow> rows = await form.ReadRecordRowsAsync(
            transaction: null,
            RecordSqlBuilder.RecordByUniqueKey(
                form.Schema,
                form.DataFields,
                form.UniqueKey,
                form.GlobalRecordId + new string(' ', WithinWidthPadding)));

        Assert.True(rows.Count == 0, "The read by key matched a padded id to the stored row.");
        AssertOnlySuccessfulReads(recorder, expectedCommands: 1);
    }

    private static void AssertSingleStoredRow(IReadOnlyList<IdRow> rows, SeededForm form, string message)
    {
        Assert.True(rows.Count == 1, message);
        Assert.Equal(form.UniqueKey, rows[0].UniqueKey);
        Assert.Equal(form.GlobalRecordId, rows[0].GlobalRecordId, StringComparer.Ordinal);
    }

    // Every command the test sent is one of its own, completed successfully, and none holds a write statement.
    private static void AssertOnlySuccessfulReads(SqlCommandRecorder recorder, int expectedCommands)
    {
        IReadOnlyList<RecordedCommand> commands = recorder.Commands;
        Assert.True(commands.Count == expectedCommands, "The test did not send exactly its own record commands.");
        Assert.True(
            commands.All(command => command.Completed && command.Succeeded),
            "A record command did not complete successfully.");
        Assert.True(recorder.Writes().Count == 0, "A record lookup sent a write statement.");
    }

    // A base row's stored id and key, as a by-id statement returned them.
    private readonly record struct IdRow(string GlobalRecordId, int UniqueKey);

    /// <summary>
    /// A seeded form exactly as the metadata reader returns it: its metadata, its catalogue schema and the
    /// reader-built column of each data field, in storage order.
    /// </summary>
    private sealed class ReaderForm
    {
        private ReaderForm(FormMetadata metadata, FormSchema schema, IReadOnlyList<ProjectedColumn> dataColumns)
        {
            Metadata = metadata;
            Schema = schema;
            DataColumns = dataColumns;
        }

        public FormMetadata Metadata { get; }

        public FormSchema Schema { get; }

        public IReadOnlyList<ProjectedColumn> DataColumns { get; }

        /// <summary>
        /// Finds the form named <paramref name="formName"/> through <c>ListFormsAsync</c> and reads its metadata and
        /// schema with <c>ReadFormAsync</c>, both of which issue only <c>SELECT</c>.
        /// </summary>
        public static async Task<ReaderForm> ReadAsync(string formName)
        {
            var options = new DatabaseOptions(TestDatabase.ConnectionString);
            var reader = new SqlMetadataReader(
                new SqlConnectionFactory(options),
                options,
                NullSqlCommandObserver.Instance);
            IReadOnlyList<FormSummary> forms = await reader.ListFormsAsync(CancellationToken.None);
            FormSummary summary = Assert.Single(
                forms,
                form => string.Equals(form.Name, formName, StringComparison.Ordinal));

            (FormMetadata Form, FormSchema Schema)? read;
            await using (SqlConnection connection = await TestDatabase.OpenConnectionAsync())
            {
                read = await reader.ReadFormAsync(
                    connection,
                    transaction: null,
                    summary.ViewId,
                    CancellationToken.None);
            }

            Assert.NotNull(read);
            (FormMetadata metadata, FormSchema schema) = read.Value;
            ProjectedColumn[] dataColumns = FormDefinitionFactory.GetDataFields(metadata)
                .Select((field, ordinal) => RecordSqlBuilder.ResolveDataColumn(schema, field, ordinal))
                .ToArray();
            return new ReaderForm(metadata, schema, dataColumns);
        }

        /// <summary>
        /// The reader-built column of the data field whose canonical name is exactly <paramref name="name"/>.
        /// </summary>
        public ProjectedColumn Column(string name) =>
            Assert.Single(DataColumns, column => string.Equals(column.Field.Name, name, StringComparison.Ordinal));

        /// <summary>The reader-built page of this form that holds <paramref name="field"/>, with its table.</summary>
        public PageTableSchema PageOf(DataFieldMetadata field)
        {
            PageTableSchema? page = Schema.FindPage(field.Page.PageId);
            Assert.NotNull(page);
            Assert.NotNull(page.Table);
            return page;
        }

        /// <summary>The data-field columns on <paramref name="page"/>, in storage order.</summary>
        public IReadOnlyList<ProjectedColumn> ColumnsOn(PageTableSchema page) =>
            DataColumns.Where(column => column.Field.Page.PageId == page.Page.PageId).ToArray();
    }

    // One seeded form opened for the record statements: its reader-built schema and data fields, an open connection,
    // and the stored id and key of its seeded row with the lowest UniqueKey.
    private sealed class SeededForm : IAsyncDisposable
    {
        private readonly SqlConnection _connection;
        private readonly int _commandTimeoutSeconds;
        private readonly ISqlCommandObserver _observer;

        private SeededForm(
            SqlConnection connection,
            int commandTimeoutSeconds,
            ISqlCommandObserver observer,
            FormSchema schema,
            IReadOnlyList<DataFieldMetadata> dataFields,
            ColumnSchema globalRecordIdColumn,
            string globalRecordId,
            int uniqueKey)
        {
            _connection = connection;
            _commandTimeoutSeconds = commandTimeoutSeconds;
            _observer = observer;
            Schema = schema;
            DataFields = dataFields;
            GlobalRecordIdColumn = globalRecordIdColumn;
            GlobalRecordId = globalRecordId;
            UniqueKey = uniqueKey;
        }

        public FormSchema Schema { get; }

        public IReadOnlyList<DataFieldMetadata> DataFields { get; }

        public ColumnSchema GlobalRecordIdColumn { get; }

        public string GlobalRecordId { get; }

        public int UniqueKey { get; }

        // Resolves the form by name through the metadata reader, then picks its seeded row by SQL.
        public static async Task<SeededForm> OpenAsync(string formName, ISqlCommandObserver observer)
        {
            var options = new DatabaseOptions(TestDatabase.ConnectionString);
            var factory = new SqlConnectionFactory(options);
            var reader = new SqlMetadataReader(factory, options, observer);

            IReadOnlyList<FormSummary> forms = await reader.ListFormsAsync(CancellationToken.None);
            FormSummary? summary = forms.FirstOrDefault(form => string.Equals(form.Name, formName, StringComparison.Ordinal));
            Assert.True(summary is not null, "The seeded form is not listed.");

            SqlConnection connection = await factory.OpenAsync(Operation, CancellationToken.None);
            try
            {
                (FormMetadata Form, FormSchema Schema)? read =
                    await reader.ReadFormAsync(connection, transaction: null, summary.ViewId, CancellationToken.None);
                Assert.True(read is not null, "The seeded form has no metadata.");

                FormSchema schema = read.Value.Schema;
                IReadOnlyList<DataFieldMetadata> dataFields = FormDefinitionFactory.GetDataFields(read.Value.Form);
                ColumnSchema globalRecordIdColumn = RecordSqlBuilder.Require(schema.GlobalRecordId);
                ColumnSchema uniqueKeyColumn = RecordSqlBuilder.Require(schema.UniqueKey);

                // The statement names only reader-built identifiers and holds no value, so it has no parameter.
                var lowestKey = new SqlStatement(
                    "SELECT TOP (1) " + SqlIdentifier.Quote(globalRecordIdColumn) + ", " +
                    SqlIdentifier.Quote(uniqueKeyColumn) + " FROM " + SqlIdentifier.Quote(schema.BaseTable) +
                    " ORDER BY " + SqlIdentifier.Quote(uniqueKeyColumn),
                    Array.Empty<SqlParameterValue>());
                IReadOnlyList<IdRow> seeded = await ReadRowsAsync(
                    connection,
                    transaction: null,
                    factory.CommandTimeoutSeconds,
                    observer,
                    lowestKey,
                    globalRecordIdOrdinal: 0,
                    uniqueKeyOrdinal: 1);
                Assert.True(seeded.Count == 1, "The seeded form has no record.");
                Assert.True(
                    seeded[0].GlobalRecordId.Length > 0 && !char.IsWhiteSpace(seeded[0].GlobalRecordId[^1]),
                    "The seeded id is blank or already ends in white space.");

                return new SeededForm(
                    connection,
                    factory.CommandTimeoutSeconds,
                    observer,
                    schema,
                    dataFields,
                    globalRecordIdColumn,
                    seeded[0].GlobalRecordId,
                    seeded[0].UniqueKey);
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        }

        public async Task<SqlTransaction> BeginTransactionAsync() =>
            (SqlTransaction)await _connection.BeginTransactionAsync(CancellationToken.None);

        // Runs SetPositionLookup or LockBase and reads each row's stored id and key at the base-row ordinals.
        public Task<IReadOnlyList<IdRow>> ReadBaseRowsAsync(SqlTransaction? transaction, SqlStatement statement) =>
            ReadRowsAsync(
                _connection,
                transaction,
                _commandTimeoutSeconds,
                _observer,
                statement,
                RecordSqlBuilder.BaseRowGlobalRecordIdOrdinal,
                RecordSqlBuilder.BaseRowUniqueKeyOrdinal);

        // Runs a record SELECT and reads each row's stored id and key at the record ordinals.
        public Task<IReadOnlyList<IdRow>> ReadRecordRowsAsync(SqlTransaction? transaction, RecordSelect select) =>
            ReadRowsAsync(
                _connection,
                transaction,
                _commandTimeoutSeconds,
                _observer,
                select.Statement,
                RecordSqlBuilder.RecordGlobalRecordIdOrdinal,
                RecordSqlBuilder.RecordUniqueKeyOrdinal);

        public ValueTask DisposeAsync() => _connection.DisposeAsync();

        private static Task<IReadOnlyList<IdRow>> ReadRowsAsync(
            SqlConnection connection,
            SqlTransaction? transaction,
            int commandTimeoutSeconds,
            ISqlCommandObserver observer,
            SqlStatement statement,
            int globalRecordIdOrdinal,
            int uniqueKeyOrdinal) =>
            SqlExecution.ReadAsync(
                connection,
                transaction,
                Operation,
                statement,
                commandTimeoutSeconds,
                observer,
                CancellationToken.None,
                (rows, token) => ReadIdRowsAsync(rows, globalRecordIdOrdinal, uniqueKeyOrdinal, token));

        private static async Task<IReadOnlyList<IdRow>> ReadIdRowsAsync(
            DbDataReader rows,
            int globalRecordIdOrdinal,
            int uniqueKeyOrdinal,
            CancellationToken token)
        {
            var result = new List<IdRow>();
            while (await rows.ReadAsync(token))
            {
                result.Add(new IdRow(rows.GetString(globalRecordIdOrdinal), rows.GetInt32(uniqueKeyOrdinal)));
            }

            return result.AsReadOnly();
        }
    }
}

/// <summary>Reads the column list of a page <c>INSERT</c> together with the parameter bound to each column.</summary>
internal static class PageInsert
{
    private const string ColumnListStart = " (";
    private const string ValuesClause = ") VALUES (";

    /// <summary>
    /// Pairs each quoted column of <c>INSERT INTO [t] ([a], [b]) VALUES (@p0, @p1)</c> with its parameter, after
    /// checking that the statement binds exactly the parameters its value list names, in that order.
    /// </summary>
    public static IReadOnlyList<(string Column, SqlParameterValue Parameter)> Columns(SqlStatement statement)
    {
        string text = statement.Text;
        int columnsAt = text.IndexOf(ColumnListStart, StringComparison.Ordinal);
        int valuesAt = text.IndexOf(ValuesClause, StringComparison.Ordinal);
        Assert.StartsWith("INSERT INTO ", text, StringComparison.Ordinal);
        Assert.EndsWith(")", text, StringComparison.Ordinal);
        Assert.InRange(columnsAt, 0, valuesAt);

        string[] columns = text[(columnsAt + ColumnListStart.Length)..valuesAt].Split(", ");
        string[] names = text[(valuesAt + ValuesClause.Length)..^1].Split(", ");
        Assert.Equal(names, statement.Parameters.Select(parameter => parameter.Name));
        Assert.Equal(columns.Length, names.Length);

        return columns.Zip(statement.Parameters, (column, parameter) => (Column: column, Parameter: parameter)).ToArray();
    }
}

/// <summary>
/// Runs the records-list and Find statements that <see cref="SearchSqlBuilder"/> renders against the seeded rows of
/// the rehearsal database, and checks the rows they return against direct SQL that does not use the builder's
/// predicate (AAP 0.3.4.4).
/// </summary>
/// <remarks>
/// <para>
/// The forms, their schemas and their data fields are read through <see cref="SqlMetadataReader"/>, by form name, so
/// no view id, table or column name is fixed here. The seeded values a case needs are chosen by SQL, and expected
/// values come from a direct, typed query in the test, never from a hard-coded count.
/// </para>
/// <para>
/// The class is read-only and seed-backed: it creates no record and sends only <c>SELECT</c> statements, the
/// builder's through <see cref="SqlExecution"/> and the command recorder, and the direct queries. Tests may write only
/// records created through the API and the few fixtures AAP 0.3.7 ("Data reach of tests") lists, and this assembly
/// reaches the data layer, not the API. Cases that need a stored literal pattern character therefore belong to
/// <c>ListAndSearchTests</c>. Each test holds the shared <see cref="DatabaseWriteLock"/> for its whole body, so the
/// seeded rows its expected values come from cannot move, and ends by asserting that the recorder saw no write.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(DatabaseCollections.Database)]
public sealed class ListAndFindSqlTests
{
    private const string CaseReportForm = "CaseReport";
    private const string FoodHistoryForm = "FoodHistory";

    // Seeded FoodHistory fields of the typed Find cases: a date, a checkbox on each page and two numbers.
    private const string DateField = "DateofInterview";
    private const string PageTwoCheckbox = "Fever";
    private const string PageThreeCheckbox = "Grapes";
    private const string FractionalField = "FeverTemp";
    private const string ThresholdField = "Age";

    // The catalogue name of every page table's record key, looked up through the reader-built table.
    private const string PageKeyColumn = "GlobalRecordId";

    // AAP 0.3.4.4 "Records list q": the one collation both sides of the grid filter compare under, the CONVERT style
    // of a datetime grid column (yyyy-mm-dd), and the escape clause every column's term declares.
    private const string GridFilterCollation = "Latin1_General_100_CI_AS";
    private const string IsoDateStyle = "23";
    private const string GridFilterEscapeClause = @" ESCAPE '\'";

    // The seeded CaseReport text field the Find pattern cases search, and the shortest seeded value they are built
    // from, long enough for every case's slices.
    private const string PatternField = "LastName";
    private const int PatternMinimumLength = 5;

    // A binary collation, under which the set [A-Za-z] is exactly the ASCII letters.
    private const string BinaryCollation = "Latin1_General_BIN2";

    // Fixed operation names reported to the command observer.
    private const string ListPageOperation = "test.listPage";
    private const string ListCountOperation = "test.listCount";
    private const string FindOperation = "test.find";

    // Covers a wait of up to 300 seconds for the test lock and the work behind it.
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Grid filters holding one pattern character each, chosen so that a pattern reading matches seeded rows, with the
    /// pattern the list must bind: every <c>\</c>, <c>%</c>, <c>_</c> and <c>[</c> prefixed with <c>\</c>, and the
    /// whole wrapped in <c>%</c>.
    /// </summary>
    public static TheoryData<string, string> GridFilterPatternCharacters => new()
    {
        { "a%e", @"%a\%e%" },
        { "a_e", @"%a\_e%" },
        { "[a-z]", @"%\[a-z]%" },
        { @"\a", @"%\\a%" },
    };

    // AAP 0.3.4.4 "Records list q": a case-insensitive "contains" over the grid columns, each written as
    // CONVERT(nvarchar(max), column[, 23]) COLLATE Latin1_General_100_CI_AS LIKE @q ... ESCAPE '\', with %, _, [ and \
    // escaped in @q. Both sides compare under that one collation, so the match is collation-independent by design; it
    // has no legacy counterpart. The expected rows come from CHARINDEX, which knows no pattern character. The seeded
    // grid holds none of the four, so each literal match is empty while the pattern reading of the same q matches
    // seeded rows, and dropping the escaping changes the result. Dropping the ESCAPE clause changes no seeded result,
    // so the statement text and the bound pattern are checked too. Positive matches against a stored literal need
    // records created through the API, so they belong to ListAndSearchTests once the record routes can store records in
    // the database (AAP 0.3.7, "Data reach of tests").
    [Theory]
    [MemberData(nameof(GridFilterPatternCharacters))]
    public async Task GridFilter_MatchesPatternCharactersLiterally(string q, string escapedPattern)
    {
        await TestDatabase.EnsureReachableAsync();
        using var timeout = new CancellationTokenSource(TestTimeout);
        var cancellationToken = timeout.Token;
        await using var sharedLock = await DatabaseWriteLock.AcquireSharedAsync(cancellationToken);

        var form = await FormUnderTest.LoadAsync(CaseReportForm, cancellationToken);
        var expected = await form.ExpectedListAsync(q, cancellationToken);

        // The pattern reading of q, its escaping left out under the list's own ESCAPE clause, matches more rows.
        var patternReading = await form.PatternReadingListAsync(q, cancellationToken);
        Assert.True(
            patternReading.Total > expected.Total,
            $"The pattern reading of {q} matches {patternReading.Total} active records, no more than its literal match.");

        var list = form.BuildList(q);
        var listed = await form.ListAsync(list, cancellationToken);
        Assert.Equal(expected.Ids, listed.Ids);
        Assert.Equal(expected.Total, listed.Total);

        foreach (var statement in new[] { list.Page, list.Count })
        {
            var pattern = Assert.Single(statement.Parameters, parameter => parameter.DbType == DbType.String);
            Assert.Equal(escapedPattern, Assert.IsType<string>(pattern.Value));

            // Every grid column's term compares with that pattern under the fixed collation and declares ESCAPE '\'.
            var term = $" LIKE {pattern.Name} COLLATE {GridFilterCollation}{GridFilterEscapeClause}";
            Assert.Equal(form.Grid.Count, CountOccurrences(statement.Text, term));
        }

        // The builder's statements are reads: the recorder saw them and no write.
        Assert.NotEmpty(form.Recorder.ForOperation(ListPageOperation));
        Assert.NotEmpty(form.Recorder.ForOperation(ListCountOperation));
        Assert.Empty(form.Recorder.Writes());
    }

    /// <summary>
    /// A text criterion on <c>LastName</c>, built from the seeded last name L that
    /// <see cref="FormUnderTest.MostFrequentLetterValueAsync"/> picks.
    /// </summary>
    public enum PatternCriterion
    {
        /// <summary>L's first three letters and <c>*</c>: a <c>LIKE</c> prefix.</summary>
        Prefix,

        /// <summary>
        /// L's first letter, <c>_</c>, its third and fourth letters and <c>*</c>: <c>_</c> matches any one character.
        /// </summary>
        Underscore,

        /// <summary>
        /// L with its second letter written as the set <c>[x]</c>, and <c>*</c>: the set keeps its SQL meaning.
        /// </summary>
        CharacterSet,

        /// <summary>L with <c>?</c> for its second letter, and <c>*</c>: <c>?</c> matches only a stored <c>?</c>.</summary>
        QuestionMark,

        /// <summary>L with a quote after its first letter, and <c>*</c>: the quote stays in the pattern.</summary>
        Quote,

        /// <summary>L itself: no wildcard, so an equality.</summary>
        NoWildcard,
    }

    /// <summary>Every pattern criterion case.</summary>
    public static TheoryData<PatternCriterion> PatternCriteria => new()
    {
        PatternCriterion.Prefix,
        PatternCriterion.Underscore,
        PatternCriterion.CharacterSet,
        PatternCriterion.QuestionMark,
        PatternCriterion.Quote,
        PatternCriterion.NoWildcard,
    };

    // AAP 0.3.4.4, text row and "Target choices": a value holding %, * or ? is a LIKE pattern with * rewritten to %,
    // bound as a parameter so its quotes stay; with no ESCAPE clause, _ and [...] keep their SQL meaning and ? matches
    // itself, as in legacy, which inlined the pattern with its single quotes removed
    // (Epi.Core/Data/Services/CollectedDataProvider.cs:2195-2206). A value with no wildcard is an equality (:2224-2231).
    // Find compares under the column's collation, as legacy does, and so does the expected query, which uses SUBSTRING
    // equality, not LIKE. L is chosen by SQL among the seeded rows. The ? and quote cases match no seeded row, while the
    // reading the target rejects (? as any one character, the quote removed) matches some.
    [Theory]
    [MemberData(nameof(PatternCriteria))]
    public async Task Find_TextCriterion_KeepsQuotesAndSqlPatternMeaning(PatternCriterion criterion)
    {
        await TestDatabase.EnsureReachableAsync();
        using var timeout = new CancellationTokenSource(TestTimeout);
        var cancellationToken = timeout.Token;
        await using var sharedLock = await DatabaseWriteLock.AcquireSharedAsync(cancellationToken);

        var form = await FormUnderTest.LoadAsync(CaseReportForm, cancellationToken);
        var lastName = await form.MostFrequentLetterValueAsync(PatternField, PatternMinimumLength, cancellationToken);
        var arranged = ArrangePattern(lastName, criterion);

        var expected = await form.ExpectedTextFindAsync(PatternField, arranged.Expected, cancellationToken);
        if (arranged.RejectedReading is { } rejectedReading)
        {
            // The reading the target rejects selects a record the literal reading does not, so a target that read the
            // pattern that way would return different rows.
            var rejected = await form.ExpectedTextFindAsync(PatternField, rejectedReading, cancellationToken);
            Assert.True(
                rejected.Ids.Any(id => !expected.Ids.Contains(id, StringComparer.Ordinal)),
                $"The rejected reading of the {criterion} case selects no record the literal reading does not.");
        }
        else
        {
            Assert.True(expected.Total > 0, $"No active seeded CaseReport record matches the {criterion} case.");
        }

        var find = form.BuildFind(PatternField, arranged.Criterion);
        var found = await form.FindAsync(find, cancellationToken);
        Assert.Equal(expected.Ids, found.Ids);
        Assert.Equal(expected.Total, found.Total);

        var operand = Assert.Single(find.Statement.Parameters, parameter => parameter.DbType == DbType.String);
        Assert.Equal(arranged.Operand, Assert.IsType<string>(operand.Value));
        Assert.Contains(
            $"{form.ColumnReference(PatternField)} {arranged.Operator} {operand.Name}",
            find.Statement.Text,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ESCAPE", find.Statement.Text, StringComparison.Ordinal);

        // The builder's statement is a read: the recorder saw it and no write.
        Assert.NotEmpty(form.Recorder.ForOperation(FindOperation));
        Assert.Empty(form.Recorder.Writes());
    }

    // Builds a pattern case from L: the criterion, the comparison and operand the parser must produce, the rows it must
    // match written without LIKE, and for the ? and quote cases the reading the target rejects.
    private static PatternCase ArrangePattern(string lastName, PatternCriterion criterion)
    {
        var first = lastName[..1];
        switch (criterion)
        {
            case PatternCriterion.Prefix:
                var prefix = lastName[..3];
                return new PatternCase(prefix + "*", "LIKE", prefix + "%", TextMatch.WithSegments((1, prefix)), null);

            case PatternCriterion.Underscore:
                var underscore = first + "_" + lastName[2..4];
                return new PatternCase(
                    underscore + "*", "LIKE", underscore + "%", TextMatch.WithSegments((1, first), (3, lastName[2..4])), null);

            case PatternCriterion.CharacterSet:
                var set = first + "[" + lastName[1] + "]" + lastName[2..];
                return new PatternCase(set + "*", "LIKE", set + "%", TextMatch.WithSegments((1, lastName)), null);

            case PatternCriterion.QuestionMark:
                var question = first + "?" + lastName[2..];
                return new PatternCase(
                    question + "*",
                    "LIKE",
                    question + "%",
                    TextMatch.WithSegments((1, question)),
                    TextMatch.WithSegments((1, first), (3, lastName[2..])));

            case PatternCriterion.Quote:
                var quoted = first + "'" + lastName[1..];
                return new PatternCase(
                    quoted + "*",
                    "LIKE",
                    quoted + "%",
                    TextMatch.WithSegments((1, quoted)),
                    TextMatch.WithSegments((1, lastName)));

            case PatternCriterion.NoWildcard:
                return new PatternCase(lastName, "=", lastName, TextMatch.Equal(lastName), null);

            default:
                throw new ArgumentOutOfRangeException(nameof(criterion), criterion, "Unknown pattern criterion.");
        }
    }

    /// <summary>A typed Find criterion on a seeded <c>FoodHistory</c> field.</summary>
    public enum TypedCriterion
    {
        /// <summary><c>DateofInterview</c> equal to its most frequent seeded value.</summary>
        DateEqual,

        /// <summary><c>DateofInterview</c> after its most frequent seeded value.</summary>
        DateGreater,

        /// <summary>The page-two checkbox <c>Fever</c> searched with <c>Yes</c>.</summary>
        CheckboxYes,

        /// <summary>The page-three checkbox <c>Grapes</c> searched with <c>(-)</c>.</summary>
        CheckboxUnchecked,

        /// <summary><c>FeverTemp</c> equal to its most frequent seeded fractional value.</summary>
        NumberEqual,

        /// <summary><c>Age</c> above 30.</summary>
        NumberGreater,
    }

    /// <summary>Every typed criterion case.</summary>
    public static TheoryData<TypedCriterion> TypedCriteria => new()
    {
        TypedCriterion.DateEqual,
        TypedCriterion.DateGreater,
        TypedCriterion.CheckboxYes,
        TypedCriterion.CheckboxUnchecked,
        TypedCriterion.NumberEqual,
        TypedCriterion.NumberGreater,
    };

    // AAP 0.3.4.4, date, checkbox and number rows. A date is YYYY-MM-DD bound as datetime at 00:00:00 with its prefix as
    // the operator (legacy bound DbType.DateTime, Epi.Core/Data/Services/CollectedDataProvider.cs:2135-2169); a checkbox's
    // Yes or (+) is = 1 and No or (-) is = 0, bound as a bit (legacy DbType.Boolean, :2022-2045); a number is its
    // invariant value with the prefix as the operator (legacy inlined it, :2046-2066). The result is the active base rows
    // with a row on every page, in UniqueKey order, capped, with the total counted before the cap. Seeded rows only.
    [Theory]
    [MemberData(nameof(TypedCriteria))]
    public async Task Find_TypedCriterion_MatchesDirectSqlAndBindsTypedOperand(TypedCriterion criterion)
    {
        await TestDatabase.EnsureReachableAsync();
        using var timeout = new CancellationTokenSource(TestTimeout);
        var cancellationToken = timeout.Token;
        await using var sharedLock = await DatabaseWriteLock.AcquireSharedAsync(cancellationToken);

        var form = await FormUnderTest.LoadAsync(FoodHistoryForm, cancellationToken);
        var arranged = await ArrangeAsync(form, criterion, cancellationToken);

        var expected = await form.ExpectedFindAsync(arranged.FieldName, arranged.Operator, arranged.Operand, cancellationToken);
        Assert.True(expected.Total > 0, $"No active seeded FoodHistory record matches the {criterion} case.");

        var find = form.BuildFind(arranged.FieldName, arranged.Criterion);
        var found = await form.FindAsync(find, cancellationToken);
        Assert.Equal(expected.Ids, found.Ids);
        Assert.Equal(expected.Total, found.Total);

        var operand = Assert.Single(find.Statement.Parameters, parameter => parameter.DbType == arranged.DbType);
        Assert.Equal(arranged.Operand, operand.Value);
        Assert.Contains(
            $"{form.ColumnReference(arranged.FieldName)} {arranged.Operator} {operand.Name}",
            find.Statement.Text,
            StringComparison.Ordinal);

        // The builder's statement is a read: the recorder saw it and no write.
        Assert.NotEmpty(form.Recorder.ForOperation(FindOperation));
        Assert.Empty(form.Recorder.Writes());
    }

    // Chooses each case's field, criterion text and expected operand; seeded values are picked by SQL.
    private static async Task<TypedCase> ArrangeAsync(
        FormUnderTest form,
        TypedCriterion criterion,
        CancellationToken cancellationToken)
    {
        switch (criterion)
        {
            case TypedCriterion.DateEqual or TypedCriterion.DateGreater:
                // A date field is date-only, so the operand equals a stored value only if no stored value has a time.
                Assert.Equal(0L, await form.CountWithTimeOfDayAsync(DateField, cancellationToken));
                var day = Assert.IsType<DateTime>(
                    await form.MostFrequentValueAsync(DateField, fractionalOnly: false, cancellationToken));
                var dayText = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                return criterion == TypedCriterion.DateEqual
                    ? new TypedCase(DateField, dayText, "=", DbType.DateTime, day)
                    : new TypedCase(DateField, ">" + dayText, ">", DbType.DateTime, day);

            case TypedCriterion.CheckboxYes:
                return new TypedCase(PageTwoCheckbox, "Yes", "=", DbType.Boolean, true);

            case TypedCriterion.CheckboxUnchecked:
                // The second checkbox sits on another page table, so the criterion reaches a second joined page.
                Assert.NotEqual(form.Field(PageTwoCheckbox).Page.PageId, form.Field(PageThreeCheckbox).Page.PageId);
                return new TypedCase(PageThreeCheckbox, "(-)", "=", DbType.Boolean, false);

            case TypedCriterion.NumberEqual:
                var fraction = Assert.IsType<double>(
                    await form.MostFrequentValueAsync(FractionalField, fractionalOnly: true, cancellationToken));
                return new TypedCase(
                    FractionalField, fraction.ToString("R", CultureInfo.InvariantCulture), "=", DbType.Double, fraction);

            case TypedCriterion.NumberGreater:
                return new TypedCase(ThresholdField, ">30", ">", DbType.Double, 30d);

            default:
                throw new ArgumentOutOfRangeException(nameof(criterion), criterion, "Unknown typed criterion.");
        }
    }

    // The number of non-overlapping ordinal occurrences of value in text.
    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal);
            index >= 0;
            index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>The two list statements the builder renders for one query: a page, and the count of the filtered set.</summary>
    private sealed record ListStatements(SqlStatement Page, SqlStatement Count);

    /// <summary>The ids of one list page, in the order returned, and the list's total.</summary>
    private sealed record ListRows(IReadOnlyList<string> Ids, long Total);

    /// <summary>The ids a Find returned, in the order returned, and the total of its matches.</summary>
    private sealed record FindRows(IReadOnlyList<string> Ids, long Total);

    /// <summary>
    /// One typed criterion: the field, the criterion text, the comparison it renders, and the type and value of the
    /// operand it binds, which the direct query binds too (a bit as the literal 1 or 0).
    /// </summary>
    private sealed record TypedCase(string FieldName, string Criterion, string Operator, DbType DbType, object Operand);

    /// <summary>
    /// One pattern case: the criterion text, the comparison and the string operand the Find statement must render, the
    /// rows the criterion matches, and for the <c>?</c> and quote cases the reading the target rejects.
    /// </summary>
    private sealed record PatternCase(
        string Criterion,
        string Operator,
        string Operand,
        TextMatch Expected,
        TextMatch? RejectedReading);

    /// <summary>
    /// The values a text criterion matches, written without <c>LIKE</c>: equal to <see cref="Whole"/>, or holding each
    /// segment's text at the segment's 1-based start, with any characters between and after the segments.
    /// </summary>
    private sealed record TextMatch(string? Whole, IReadOnlyList<(int Start, string Text)> Segments)
    {
        /// <summary>The values equal to <paramref name="text"/>.</summary>
        public static TextMatch Equal(string text) => new(text, []);

        /// <summary>The values that hold every segment's text at its start.</summary>
        public static TextMatch WithSegments(params (int Start, string Text)[] segments) => new(null, segments);
    }

    /// <summary>
    /// One seeded form as the data layer sees it, with the collaborators every statement runs through.
    /// </summary>
    private sealed record FormUnderTest(
        FormSchema Schema,
        DataFieldSet DataFields,
        IReadOnlyList<DataFieldMetadata> Grid,
        DatabaseOptions Options,
        SqlCommandRecorder Recorder)
    {
        /// <summary>Reads a form by name through the metadata reader.</summary>
        public static async Task<FormUnderTest> LoadAsync(string formName, CancellationToken cancellationToken)
        {
            var options = new DatabaseOptions(TestDatabase.ConnectionString);
            var recorder = new SqlCommandRecorder();
            var reader = new SqlMetadataReader(new SqlConnectionFactory(options), options, recorder);

            var forms = await reader.ListFormsAsync(cancellationToken);
            var viewId = Assert.Single(forms, form => string.Equals(form.Name, formName, StringComparison.Ordinal)).ViewId;

            await using var connection = await TestDatabase.OpenConnectionAsync(cancellationToken);
            var read = await reader.ReadFormAsync(connection, transaction: null, viewId, cancellationToken);
            Assert.True(read.HasValue, $"The metadata reader returned no form for {formName}.");

            var (form, schema) = read.Value;
            var dataFields = FormDefinitionFactory.GetDataFields(form);
            return new FormUnderTest(schema, dataFields, RecordQueryRules.GetGridColumns(dataFields), options, recorder);
        }

        /// <summary>
        /// Builds the first list page of the active set, at the largest page size, filtered by <paramref name="q"/>,
        /// and the count of the same filtered set.
        /// </summary>
        public ListStatements BuildList(string q)
        {
            var query = new RecordListQuery(q, IncludeDeleted: false, RecordQueryRules.DefaultPage, RecordQueryRules.MaxPageSize);
            return new ListStatements(
                SearchSqlBuilder.ListPage(Schema, Grid, query).Statement,
                SearchSqlBuilder.ListCount(Schema, Grid, query));
        }

        /// <summary>Runs a list page and its count, and reads the ids the page returns and the total.</summary>
        public async Task<ListRows> ListAsync(ListStatements list, CancellationToken cancellationToken)
        {
            await using var connection = await TestDatabase.OpenConnectionAsync(cancellationToken);
            var ids = await SqlExecution.ReadAsync(
                connection,
                transaction: null,
                ListPageOperation,
                list.Page,
                Options.CommandTimeoutSeconds,
                Recorder,
                cancellationToken,
                ReadIdsAsync);
            var total = await SqlExecution.ExecuteScalarAsync<long>(
                connection,
                transaction: null,
                ListCountOperation,
                list.Count,
                Options.CommandTimeoutSeconds,
                Recorder,
                cancellationToken);

            return new ListRows(ids, total);
        }

        /// <summary>
        /// The list result computed by a direct query that uses no <c>LIKE</c>: the active base rows, joined with
        /// <c>LEFT JOIN</c> to each page holding a grid column as the list joins them, where some grid column, converted
        /// as the list converts it, holds <paramref name="q"/> by <c>CHARINDEX</c> under the grid filter's collation;
        /// the first <see cref="RecordQueryRules.MaxPageSize"/> ids in <c>UniqueKey</c> order, and the count of every
        /// match.
        /// </summary>
        public Task<ListRows> ExpectedListAsync(string q, CancellationToken cancellationToken) =>
            DirectListAsync(q, column => $"CHARINDEX(@q COLLATE {GridFilterCollation}, {column}) > 0", cancellationToken);

        /// <summary>
        /// The list result of <paramref name="q"/> read as a pattern: wrapped in <c>%</c> but not escaped, under the
        /// list's own conversion, collation and <c>ESCAPE '\'</c> clause, so <c>%</c>, <c>_</c> and <c>[...]</c> are
        /// wildcards and a <c>\</c> makes the character after it literal. Without the clause a <c>\</c> would match
        /// only itself, and the reading would not differ from the literal match.
        /// </summary>
        public Task<ListRows> PatternReadingListAsync(string q, CancellationToken cancellationToken) =>
            DirectListAsync(
                q,
                column => $"{column} LIKE (N'%' + @q + N'%') COLLATE {GridFilterCollation}{GridFilterEscapeClause}",
                cancellationToken);

        /// <summary>
        /// Parses one criterion with the search criteria parser and builds the Find statement over the active set,
        /// excluding no record.
        /// </summary>
        public SearchSelect BuildFind(string fieldName, string criterion)
        {
            var parsed = SearchCriteriaParser.Parse(
                DataFields,
                [new SearchCriterion(fieldName, FieldValue.FromText(criterion))]);
            Assert.True(
                parsed.IsSuccess,
                $"The {fieldName} criterion did not parse: {parsed.RequestError} " +
                string.Join(" ", parsed.FieldErrors.Values.SelectMany(messages => messages)));

            return SearchSqlBuilder.Search(
                Schema, DataFields, Grid, parsed.Conditions, includeDeleted: false, excludeGlobalRecordId: null);
        }

        /// <summary>Runs a Find statement and reads the ids it returns and the total every row carries.</summary>
        public async Task<FindRows> FindAsync(SearchSelect find, CancellationToken cancellationToken)
        {
            await using var connection = await TestDatabase.OpenConnectionAsync(cancellationToken);
            return await SqlExecution.ReadAsync(
                connection,
                transaction: null,
                FindOperation,
                find.Statement,
                Options.CommandTimeoutSeconds,
                Recorder,
                cancellationToken,
                ReadFindRowsAsync);
        }

        /// <summary>
        /// The Find result computed by a direct query: the active base rows joined with <c>INNER JOIN</c> to every page
        /// table, filtered by <c>column comparison operand</c>, the first <see cref="RecordQueryRules.SearchCap"/> ids
        /// in <c>UniqueKey</c> order, and the count of every match.
        /// </summary>
        public async Task<FindRows> ExpectedFindAsync(
            string fieldName,
            string comparison,
            object operand,
            CancellationToken cancellationToken)
        {
            var (from, criterionColumn) = FindSource(fieldName);

            // A bit is compared with the literal 1 or 0, any other operand with a parameter typed as its column.
            var operandText = operand is bool flag ? (flag ? "1" : "0") : "@operand";
            var parameters = new List<SqlParameter>();
            switch (operand)
            {
                case DateTime date:
                    parameters.Add(new SqlParameter("@operand", SqlDbType.DateTime) { Value = date });
                    break;
                case double number:
                    parameters.Add(new SqlParameter("@operand", SqlDbType.Float) { Value = number });
                    break;
                case bool:
                    break;
                default:
                    throw new ArgumentException("The operand is not a date, a number or a checkbox state.", nameof(operand));
            }

            return await DirectFindAsync(from, $"{criterionColumn} {comparison} {operandText}", parameters, cancellationToken);
        }

        /// <summary>
        /// The Find result of a text criterion computed by a direct query that uses no <c>LIKE</c>: the rows of
        /// <see cref="ExpectedFindAsync"/> whose value, compared under the column's own collation as Find compares it,
        /// equals <see cref="TextMatch.Whole"/>, or holds each segment as <c>SUBSTRING(column, start, length)</c>.
        /// </summary>
        public async Task<FindRows> ExpectedTextFindAsync(
            string fieldName,
            TextMatch match,
            CancellationToken cancellationToken)
        {
            var (from, criterionColumn) = FindSource(fieldName);
            var parameters = new List<SqlParameter>();
            string predicate;
            if (match.Whole is { } whole)
            {
                parameters.Add(TextParameter("@whole", whole));
                predicate = $"{criterionColumn} = @whole";
            }
            else
            {
                Assert.NotEmpty(match.Segments);
                var terms = new List<string>(match.Segments.Count);
                foreach (var (start, text) in match.Segments)
                {
                    var name = "@segment" + terms.Count.ToString(CultureInfo.InvariantCulture);
                    parameters.Add(TextParameter(name, text));
                    terms.Add(string.Create(
                        CultureInfo.InvariantCulture, $"SUBSTRING({criterionColumn}, {start}, {text.Length}) = {name}"));
                }

                predicate = string.Join(" AND ", terms);
            }

            return await DirectFindAsync(from, predicate, parameters, cancellationToken);
        }

        /// <summary>
        /// The most frequent non-null stored value of a field among active base rows, ties broken by the smaller
        /// value; with <paramref name="fractionalOnly"/>, among values that are not whole numbers.
        /// </summary>
        public Task<object> MostFrequentValueAsync(
            string fieldName,
            bool fractionalOnly,
            CancellationToken cancellationToken) =>
            MostFrequentAsync(
                fieldName,
                value => fractionalOnly ? $" AND {value} <> FLOOR({value})" : string.Empty,
                cancellationToken);

        /// <summary>
        /// The most frequent stored value of a text field among active base rows that is at least
        /// <paramref name="minimumLength"/> characters long and holds ASCII letters only, ties broken by the smaller
        /// value.
        /// </summary>
        public async Task<string> MostFrequentLetterValueAsync(
            string fieldName,
            int minimumLength,
            CancellationToken cancellationToken) =>
            Assert.IsType<string>(await MostFrequentAsync(
                fieldName,
                value => string.Create(CultureInfo.InvariantCulture, $" AND LEN({value}) >= {minimumLength}") +
                    $" AND PATINDEX(N'%[^A-Za-z]%', {value} COLLATE {BinaryCollation}) = 0",
                cancellationToken));

        /// <summary>The number of stored values of a <c>datetime</c> field, in any record, that carry a time of day.</summary>
        public async Task<long> CountWithTimeOfDayAsync(string fieldName, CancellationToken cancellationToken)
        {
            var (table, column) = PageColumn(fieldName);
            var sql = $"SELECT COUNT_BIG(*) FROM {table} WHERE {column} IS NOT NULL" +
                $" AND CAST({column} AS time) <> CAST('00:00:00' AS time);";

            await using var connection = await TestDatabase.OpenConnectionAsync(cancellationToken);
            await using var command = new SqlCommand(sql, connection) { CommandTimeout = Options.CommandTimeoutSeconds };
            return Assert.IsType<long>(await command.ExecuteScalarAsync(cancellationToken));
        }

        /// <summary>The qualified reference the Find statement uses for a data field's column, such as <c>p0.[Age]</c>.</summary>
        public string ColumnReference(string fieldName) =>
            RecordSqlBuilder.ColumnReference(Schema, RecordSqlBuilder.ResolveDataColumn(Schema, Field(fieldName), ordinal: 0));

        /// <summary>The data field of that name; the test fails when the form has none.</summary>
        public DataFieldMetadata Field(string fieldName)
        {
            var field = DataFields.Find(fieldName);
            Assert.NotNull(field);
            return field;
        }

        private ColumnSchema DataColumn(DataFieldMetadata field)
        {
            var column = Schema.FindDataColumn(field);
            Assert.NotNull(column);
            return column;
        }

        // The quoted page table and column that store a data field.
        private (string Table, string Column) PageColumn(string fieldName)
        {
            var field = Field(fieldName);
            var table = Schema.FindPage(field.Page.PageId)?.Table;
            Assert.NotNull(table);
            return (SqlIdentifier.Quote(table), SqlIdentifier.Quote(DataColumn(field)));
        }

        // The quoted record key of the page table that stores a data field.
        private string PageKey(string fieldName)
        {
            var table = Schema.FindPage(Field(fieldName).Page.PageId)?.Table;
            Assert.NotNull(table);
            var key = table.FindColumn(PageKeyColumn);
            Assert.NotNull(key);
            return SqlIdentifier.Quote(key);
        }

        // The most frequent non-null stored value of a field among active base rows that also meet the condition
        // written on the value's column reference, ties broken by the smaller value.
        private async Task<object> MostFrequentAsync(
            string fieldName,
            Func<string, string> condition,
            CancellationToken cancellationToken)
        {
            var (table, column) = PageColumn(fieldName);
            var value = $"t.{column}";
            var baseId = SqlIdentifier.Quote(RecordSqlBuilder.Require(Schema.GlobalRecordId));
            var recStatus = SqlIdentifier.Quote(RecordSqlBuilder.Require(Schema.RecStatus));
            var sql = $"SELECT TOP (1) {value} FROM {SqlIdentifier.Quote(Schema.BaseTable)} b" +
                $" INNER JOIN {table} t ON t.{PageKey(fieldName)} = b.{baseId}" +
                $" WHERE b.{recStatus} = 1 AND {value} IS NOT NULL" +
                condition(value) +
                $" GROUP BY {value} ORDER BY COUNT_BIG(*) DESC, {value};";

            await using var connection = await TestDatabase.OpenConnectionAsync(cancellationToken);
            await using var command = new SqlCommand(sql, connection) { CommandTimeout = Options.CommandTimeoutSeconds };
            var result = await command.ExecuteScalarAsync(cancellationToken);
            Assert.NotNull(result);
            Assert.IsNotType<DBNull>(result);
            return result;
        }

        // FROM the base table joined with INNER JOIN to every page table, as Find joins them, and the qualified column
        // that stores the field.
        private (string From, string Column) FindSource(string fieldName)
        {
            var field = Field(fieldName);
            var baseId = SqlIdentifier.Quote(RecordSqlBuilder.Require(Schema.GlobalRecordId));
            var from = new StringBuilder(" FROM ").Append(SqlIdentifier.Quote(Schema.BaseTable)).Append(" b");
            string? criterionColumn = null;
            for (var index = 0; index < Schema.Pages.Count; index++)
            {
                if (Schema.Pages[index].Table is not { } table)
                {
                    continue;
                }

                var alias = "t" + index.ToString(CultureInfo.InvariantCulture);
                var key = table.FindColumn(PageKeyColumn);
                Assert.NotNull(key);
                from.Append(" INNER JOIN ").Append(SqlIdentifier.Quote(table)).Append(' ').Append(alias)
                    .Append(" ON ").Append(alias).Append('.').Append(SqlIdentifier.Quote(key))
                    .Append(" = b.").Append(baseId);
                if (Schema.Pages[index].Page.PageId == field.Page.PageId)
                {
                    criterionColumn = alias + "." + SqlIdentifier.Quote(DataColumn(field));
                }
            }

            Assert.NotNull(criterionColumn);
            return (from.ToString(), criterionColumn);
        }

        // A direct Find query over the source: the active set is RecStatus = 1, then the predicate; the first SearchCap
        // ids in UniqueKey order, and the count of every match.
        private async Task<FindRows> DirectFindAsync(
            string from,
            string predicate,
            IReadOnlyList<SqlParameter> parameters,
            CancellationToken cancellationToken)
        {
            var baseId = SqlIdentifier.Quote(RecordSqlBuilder.Require(Schema.GlobalRecordId));
            var where = $" WHERE b.{SqlIdentifier.Quote(RecordSqlBuilder.Require(Schema.RecStatus))} = 1 AND {predicate}";
            var sql = $"SELECT TOP (@cap) b.{baseId}{from}{where}" +
                $" ORDER BY b.{SqlIdentifier.Quote(RecordSqlBuilder.Require(Schema.UniqueKey))};" +
                $" SELECT COUNT_BIG(*){from}{where};";

            var (ids, total) = await ReadIdsAndTotalAsync(
                sql,
                [new SqlParameter("@cap", SqlDbType.Int) { Value = RecordQueryRules.SearchCap }, .. parameters],
                cancellationToken);
            return new FindRows(ids, total);
        }

        // A direct list query: the active base rows, joined with LEFT JOIN to each page holding a grid column, where
        // some grid column, converted as AAP 0.3.4.4 defines (CONVERT(nvarchar(max), column), style 23 for a datetime,
        // under the grid filter's collation), satisfies the term; the first page of ids in UniqueKey order and the total.
        private async Task<ListRows> DirectListAsync(string q, Func<string, string> term, CancellationToken cancellationToken)
        {
            var baseId = SqlIdentifier.Quote(RecordSqlBuilder.Require(Schema.GlobalRecordId));
            var from = new StringBuilder(" FROM ").Append(SqlIdentifier.Quote(Schema.BaseTable)).Append(" b");
            var joinedPages = new HashSet<int>();
            var terms = new List<string>(Grid.Count);
            foreach (var field in Grid)
            {
                var (table, column) = PageColumn(field.Name);
                var alias = "t" + field.Page.PageId.ToString(CultureInfo.InvariantCulture);
                if (joinedPages.Add(field.Page.PageId))
                {
                    from.Append(" LEFT JOIN ").Append(table).Append(' ').Append(alias)
                        .Append(" ON ").Append(alias).Append('.').Append(PageKey(field.Name))
                        .Append(" = b.").Append(baseId);
                }

                var isDateTime = DataColumn(field).Metadata.Family == ColumnFamily.DateTime;
                var style = isDateTime ? ", " + IsoDateStyle : string.Empty;
                terms.Add(term($"CONVERT(nvarchar(max), {alias}.{column}{style}) COLLATE {GridFilterCollation}"));
            }

            Assert.NotEmpty(terms);
            var where = $" WHERE b.{SqlIdentifier.Quote(RecordSqlBuilder.Require(Schema.RecStatus))} = 1" +
                $" AND ({string.Join(" OR ", terms)})";
            var sql = $"SELECT TOP (@pageSize) b.{baseId}{from}{where}" +
                $" ORDER BY b.{SqlIdentifier.Quote(RecordSqlBuilder.Require(Schema.UniqueKey))};" +
                $" SELECT COUNT_BIG(*){from}{where};";

            var (ids, total) = await ReadIdsAndTotalAsync(
                sql,
                [
                    TextParameter("@q", q),
                    new SqlParameter("@pageSize", SqlDbType.Int) { Value = RecordQueryRules.MaxPageSize },
                ],
                cancellationToken);
            return new ListRows(ids, total);
        }

        // Runs a direct query, outside the recorder, whose first result holds ids and whose second holds one count.
        private async Task<(IReadOnlyList<string> Ids, long Total)> ReadIdsAndTotalAsync(
            string sql,
            IReadOnlyList<SqlParameter> parameters,
            CancellationToken cancellationToken)
        {
            await using var connection = await TestDatabase.OpenConnectionAsync(cancellationToken);
            await using var command = new SqlCommand(sql, connection) { CommandTimeout = Options.CommandTimeoutSeconds };
            foreach (var parameter in parameters)
            {
                command.Parameters.Add(parameter);
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var ids = new List<string>();
            while (await reader.ReadAsync(cancellationToken))
            {
                ids.Add(reader.GetString(0));
            }

            Assert.True(await reader.NextResultAsync(cancellationToken), "The direct query returned no count.");
            Assert.True(await reader.ReadAsync(cancellationToken), "The direct query returned no count row.");
            return (ids, reader.GetInt64(0));
        }

        // A text parameter of a direct query, nvarchar(max), so it takes the collation of the column it is compared with.
        private static SqlParameter TextParameter(string name, string value) =>
            new(name, SqlDbType.NVarChar, -1) { Value = value };

        private static async Task<FindRows> ReadFindRowsAsync(DbDataReader reader, CancellationToken cancellationToken)
        {
            var ids = new List<string>();
            var totals = new HashSet<long>();
            while (await reader.ReadAsync(cancellationToken))
            {
                ids.Add(reader.GetString(SearchSqlBuilder.IdOrdinal));
                totals.Add(reader.GetInt64(SearchSqlBuilder.TotalOrdinal));
            }

            // Every row carries the same total; a Find with no row has no match.
            return new FindRows(ids, totals.Count == 0 ? 0 : Assert.Single(totals));
        }

        private static async Task<IReadOnlyList<string>> ReadIdsAsync(DbDataReader reader, CancellationToken cancellationToken)
        {
            var ids = new List<string>();
            while (await reader.ReadAsync(cancellationToken))
            {
                ids.Add(reader.GetString(SearchSqlBuilder.IdOrdinal));
            }

            return ids;
        }
    }
}

/// <summary>
/// Integration tests of the SQL Server bounds of the Find statement the repository runs, against the rehearsal
/// database through <see cref="SqlExecution"/>: a search beyond 2,000 alternatives other than <c>IS NULL</c> binds its
/// values packed and returns exactly the rows the per-alternative statement returns, the statement never exceeds SQL
/// Server's parameter limit, and a <c>LIKE</c> pattern is bounded at SQL Server's 4,000 code units before any SQL is
/// built.
/// </summary>
/// <remarks>
/// <para>
/// Criteria are written as a client writes them and parsed by <see cref="SearchCriteriaParser"/> over the form's data
/// fields; their values are read from the database by direct SQL, from active records with a row on every page, so
/// each search matches something whatever rows the database holds. No count is hard-coded: every expected result is
/// another statement's result, the per-alternative statement or the union of per-alternative statements over chunks
/// of the alternatives.
/// </para>
/// <para>
/// The tests only read. Each holds the shared <see cref="DatabaseWriteLock"/>, because it compares the results of
/// several statements that no writer may change in between.
/// </para>
/// </remarks>
[Collection(DatabaseCollections.Database)]
[Trait("Category", "Integration")]
public sealed class SearchStatementBoundsTests : IAsyncLifetime
{
    private const string CaseReport = "CaseReport";
    private const string FoodHistory = "FoodHistory";

    private const string Operation = "tests.searchStatementBounds";

    // The builder's scalar budget and the parameters every Find statement over the active set binds besides its
    // criteria: the RecStatus of the active set and the TOP cap.
    private const int ScalarParameterBudget = 2000;
    private const int FixedActiveSetParameters = 2;

    // Chunks of the threshold searches, run per alternative to build the expected union.
    private const int ChunkSize = 500;

    // The most rows the threshold searches' seeded values may match, so that each chunk's result and the union stay
    // under the 200-row cap and are therefore complete.
    private const int MaxSeededMatches = 120;

    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(10);

    // The joins of each seeded form's complete records: the base table with a row on every page table.
    private static readonly IReadOnlyDictionary<string, string> CompleteRecords = new Dictionary<string, string>
    {
        [CaseReport] = "[CaseReport] b INNER JOIN [CaseReport1] p1 ON p1.[GlobalRecordId] = b.[GlobalRecordId]",
        [FoodHistory] = "[FoodHistory] b INNER JOIN [FoodHistory2] p1 ON p1.[GlobalRecordId] = b.[GlobalRecordId]"
            + " INNER JOIN [FoodHistory3] p2 ON p2.[GlobalRecordId] = b.[GlobalRecordId]",
    };

    public Task InitializeAsync() => TestDatabase.EnsureReachableAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // Every searchable kind, rendered per alternative and forced into packed form, returns the same rows, positions
    // and total, in both selected sets.
    [Theory]
    [InlineData(CaseReport, false)]
    [InlineData(CaseReport, true)]
    [InlineData(FoodHistory, false)]
    [InlineData(FoodHistory, true)]
    public async Task Search_PackedStatement_ReturnsExactlyTheRowsOfTheScalarStatement(
        string formName,
        bool includeDeleted)
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        var token = cancellation.Token;
        await using var databaseLock = await DatabaseWriteLock.AcquireSharedAsync(token);
        await using var connection = await TestDatabase.OpenConnectionAsync(token);
        var form = await ReadFormAsync(connection, formName, token);

        var cases = formName == CaseReport
            ? await CaseReportCriteriaAsync(connection, token)
            : await FoodHistoryCriteriaAsync(connection, token);

        foreach (var (label, criteria) in cases)
        {
            var conditions = Parse(form, criteria);
            var scalar = SearchSqlBuilder.Search(
                form.Schema, form.DataFields, form.GridColumns, conditions, includeDeleted, null);
            var packed = SearchSqlBuilder.Search(
                form.Schema, form.DataFields, form.GridColumns, conditions, includeDeleted, null, 0);

            Assert.DoesNotContain("OPENJSON", scalar.Statement.Text, StringComparison.Ordinal);
            Assert.Contains("OPENJSON", packed.Statement.Text, StringComparison.Ordinal);

            var scalarRows = await RunAsync(connection, scalar, token);
            var packedRows = await RunAsync(connection, packed, token);

            Assert.True(scalarRows.Count > 0, $"{formName} {label}: the seeded values match no row.");
            Assert.True(
                scalarRows.SequenceEqual(packedRows),
                $"{formName} {label}: {scalarRows.Count} scalar rows (total {scalarRows[0].Total}) and "
                + $"{packedRows.Count} packed rows differ.");
        }
    }

    // Across the scalar budget: 2,000 alternatives bind one parameter each, 2,003 with the active set, the cap and the
    // excluded id; 2,001 and 5,000 bind packed. Every size, packed or not, returns the union of the per-alternative
    // statements over chunks of its alternatives.
    [Theory]
    [InlineData(2000)]
    [InlineData(2001)]
    [InlineData(5000)]
    public async Task Search_AlternativesAcrossTheScalarBudget_ReturnTheUnionOfChunkedScalarSearches(int size)
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        var token = cancellation.Token;
        await using var databaseLock = await DatabaseWriteLock.AcquireSharedAsync(token);
        await using var connection = await TestDatabase.OpenConnectionAsync(token);
        var form = await ReadFormAsync(connection, CaseReport, token);

        var seeded = await SeededLastNamesAsync(connection, token);
        var excluded = await FirstActiveIdAsync(connection, seeded[0], token);
        var alternatives = ThresholdAlternatives(seeded, size);
        var condition = Assert.Single(Parse(form, [Text("LastName", string.Join(" or ", alternatives))]));
        Assert.Equal(size, condition.Alternatives.Count);

        var bound = SearchSqlBuilder.Search(
            form.Schema, form.DataFields, form.GridColumns, [condition], includeDeleted: false, excluded);
        var packed = SearchSqlBuilder.Search(
            form.Schema, form.DataFields, form.GridColumns, [condition], includeDeleted: false, excluded, 0);

        var operators = condition.Alternatives.Select(alternative => alternative.Operator).Distinct().Count();
        var packedParameters = FixedActiveSetParameters + operators + 1;
        if (size <= ScalarParameterBudget)
        {
            Assert.Equal(size + FixedActiveSetParameters + 1, bound.Statement.Parameters.Count);
            Assert.DoesNotContain("OPENJSON", bound.Statement.Text, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(packedParameters, bound.Statement.Parameters.Count);
            Assert.Contains("OPENJSON", bound.Statement.Text, StringComparison.Ordinal);
        }

        Assert.Equal(packedParameters, packed.Statement.Parameters.Count);

        var union = new Dictionary<string, SearchRow>(StringComparer.Ordinal);
        foreach (var chunk in condition.Alternatives.Chunk(ChunkSize))
        {
            var part = new SearchCondition(condition.FieldName, condition.Target, condition.FieldType, chunk);
            var select = SearchSqlBuilder.Search(
                form.Schema, form.DataFields, form.GridColumns, [part], includeDeleted: false, excluded);
            Assert.DoesNotContain("OPENJSON", select.Statement.Text, StringComparison.Ordinal);

            var rows = await RunAsync(connection, select, token);
            Assert.All(rows, row => Assert.Equal(rows.Count, row.Total));
            foreach (var row in rows)
            {
                union[row.Id] = row;
            }
        }

        Assert.NotEmpty(union);
        Assert.True(union.Count <= RecordQueryRules.SearchCap, "The seeded values match more rows than one page.");
        Assert.DoesNotContain(excluded, union.Keys);
        var expected = union.Values
            .OrderBy(row => row.UniqueKey)
            .Select(row => row with { Total = union.Count })
            .ToArray();

        Assert.Equal(expected, await RunAsync(connection, bound, token));
        Assert.Equal(expected, await RunAsync(connection, packed, token));
    }

    // SQL Server limits an nvarchar LIKE pattern to 4,000 code units: a 4,000-character pattern runs, per alternative
    // and packed, on an nvarchar and an ntext column, and matches what its collapsed form '%' matches; one more
    // character is refused before any SQL is built.
    [Theory]
    [InlineData(CaseReport, "LastName")]
    [InlineData(FoodHistory, "BuyLastTenDays")]
    public async Task Search_LikePatternAtTheLimit_RunsAndOneLongerThrowsBeforeSql(string formName, string fieldName)
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        var token = cancellation.Token;
        await using var databaseLock = await DatabaseWriteLock.AcquireSharedAsync(token);
        await using var connection = await TestDatabase.OpenConnectionAsync(token);
        var form = await ReadFormAsync(connection, formName, token);
        var field = Assert.Single(form.DataFields, candidate => candidate.Name == fieldName);

        SearchCondition Like(int length) => new(
            field.Name,
            SearchTarget.PageField,
            field.FieldType,
            [new SearchAlternative(SearchOperator.Like, FieldValue.FromText(new string('%', length)))]);

        var collapsed = await RunAsync(
            connection,
            SearchSqlBuilder.Search(form.Schema, form.DataFields, form.GridColumns, [Like(1)], false, null),
            token);
        Assert.NotEmpty(collapsed);

        foreach (var budget in new[] { ScalarParameterBudget, 0 })
        {
            var select = SearchSqlBuilder.Search(
                form.Schema,
                form.DataFields,
                form.GridColumns,
                [Like(RecordQueryRules.MaxLikePatternLength)],
                false,
                null,
                budget);

            Assert.Equal(collapsed, await RunAsync(connection, select, token));
        }

        var tooLong = Like(RecordQueryRules.MaxLikePatternLength + 1);
        Assert.Throws<ArgumentException>(
            "conditions",
            () => SearchSqlBuilder.Search(form.Schema, form.DataFields, form.GridColumns, [tooLong], false, null));
        Assert.Throws<ArgumentException>(
            "conditions",
            () => SearchSqlBuilder.Search(form.Schema, form.DataFields, form.GridColumns, [tooLong], false, null, 0));
    }

    // A command carries at most 2,098 parameters of its own: a statement of exactly 2,098 runs, and the builder refuses
    // one that would bind 2,099 before it is returned, whatever the scalar budget allows.
    [Theory]
    [InlineData(2096, true)]
    [InlineData(2097, false)]
    public async Task Search_StatementParameterCount_IsBoundedAtSqlServersLimit(int alternatives, bool builds)
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        var token = cancellation.Token;
        await using var databaseLock = await DatabaseWriteLock.AcquireSharedAsync(token);
        await using var connection = await TestDatabase.OpenConnectionAsync(token);
        var form = await ReadFormAsync(connection, CaseReport, token);
        var field = Assert.Single(form.DataFields, candidate => candidate.Name == "LastName");

        var condition = new SearchCondition(
            field.Name,
            SearchTarget.PageField,
            field.FieldType,
            [.. Enumerable.Range(0, alternatives).Select(index => new SearchAlternative(
                SearchOperator.Equal, FieldValue.FromText(NoMatch(index))))]);

        SearchSelect Build() => SearchSqlBuilder.Search(
            form.Schema, form.DataFields, form.GridColumns, [condition], false, null, int.MaxValue);

        if (!builds)
        {
            Assert.Throws<ArgumentException>("conditions", Build);
            return;
        }

        var select = Build();
        Assert.Equal(alternatives + FixedActiveSetParameters, select.Statement.Parameters.Count);
        Assert.Empty(await RunAsync(connection, select, token));
    }

    // CaseReport: text equality, LIKE and a bare prefix, Phone Number comparisons, fractional and negative number
    // comparisons, date comparisons, Comment Legal text, GlobalRecordId and RecStatus, and three fields at once.
    private static async Task<IReadOnlyList<(string Label, SearchCriterion[] Criteria)>> CaseReportCriteriaAsync(
        SqlConnection connection,
        CancellationToken token)
    {
        var lastNames = await SampleAsync(connection, CaseReport, "p1.[LastName]", 2, token);
        var firstNames = await SampleAsync(connection, CaseReport, "p1.[FirstName]", 1, token);
        var phones = await SampleAsync(connection, CaseReport, "p1.[PhoneNumber]", 2, token);
        var ages = await SampleAsync(connection, CaseReport, "p1.[Age]", 3, token);
        var useDays = await SampleAsync(connection, CaseReport, "p1.[AntibioticUseDays]", 2, token);
        var onsets = await SampleAsync(connection, CaseReport, "p1.[OnsetDate]", 2, token);
        var sexes = await SampleAsync(connection, CaseReport, "p1.[Sex]", 1, token);
        var ids = await SampleAsync(connection, CaseReport, "b.[GlobalRecordId]", 2, token);
        var anchor = await AnchorAsync(connection, CaseReport, ["p1.[LastName]", "p1.[Age]", "p1.[OnsetDate]"], token);

        return
        [
            ("text", [Text("LastName", Or([.. lastNames, "Zq*", "?", "*" + lastNames[0][1..], ">"]))]),
            ("text pattern", [Text("FirstName", Or([firstNames[0], "J*", "*a*"]))]),
            ("phone", [Text("PhoneNumber", Or([phones[0], ">" + phones[1], "<=(555) 0100", "<>" + phones[0], ">="]))]),
            ("number", [Text("Age", Or([ages[0], ">=" + ages[1], "<" + ages[2], "<>-3.75", "<=30.5", ">90.25"]))]),
            ("number days", [Text("AntibioticUseDays", Or([useDays[0], ">" + useDays[1], "<-1.5"]))]),
            ("date", [Text("OnsetDate", Or([onsets[0], ">=" + onsets[1], "<2007-01-01", "<>1999-12-31"]))]),
            ("comment legal", [Text("Sex", Or([sexes[0], "2*", ">"]))]),
            ("global record id", [Text("GlobalRecordId", Or([ids[0], ">=" + ids[1], "<0", "<>" + ids[0]]))]),
            ("rec status", [Text("RecStatus", "1 or 0")]),
            (
                "three fields",
                [
                    Text("LastName", Or([anchor[0], "Zq*"])),
                    Text("Age", Or([anchor[1], ">1000"])),
                    Text("OnsetDate", Or([anchor[2], "<1800-01-01"])),
                ]
            ),
        ];
    }

    // FoodHistory: fractional and negative float equality and comparisons, a checkbox, Legal Values, a date, Phone
    // Number, ntext multiline 255-character equality and LIKE on both pages, GlobalRecordId, and fields of both pages.
    private static async Task<IReadOnlyList<(string Label, SearchCriterion[] Criteria)>> FoodHistoryCriteriaAsync(
        SqlConnection connection,
        CancellationToken token)
    {
        var caseIds = await SampleAsync(connection, FoodHistory, "p1.[CaseID]", 2, token);
        var latitudes = await SampleAsync(connection, FoodHistory, "p1.[Latitude]", 3, token);
        var longitudes = await SampleAsync(connection, FoodHistory, "p1.[Longitude]", 3, token);
        var fevers = await SampleAsync(connection, FoodHistory, "p1.[FeverTemp]", 2, token);
        var sexes = await SampleAsync(connection, FoodHistory, "p1.[Sex]", 1, token);
        var births = await SampleAsync(connection, FoodHistory, "p1.[DOB]", 2, token);
        var phones = await SampleAsync(connection, FoodHistory, "p1.[HomePhone]", 1, token);
        var lastNames = await SampleAsync(connection, FoodHistory, "p1.[LastName]", 2, token);
        var addresses = await SampleAsync(
            connection, FoodHistory, "CAST(p1.[PatientAddress] AS nvarchar(max))", 2, token);
        var purchases = await SampleAsync(
            connection, FoodHistory, "CAST(p2.[BuyLastTenDays] AS nvarchar(max))", 2, token);
        var ids = await SampleAsync(connection, FoodHistory, "b.[GlobalRecordId]", 1, token);
        var anchor = await AnchorAsync(
            connection,
            FoodHistory,
            ["p1.[Latitude]", "p1.[White]", "CAST(p2.[BuyLastTenDays] AS nvarchar(max))"],
            token);

        return
        [
            ("case id", [Text("CaseID", Or([caseIds[0], ">=" + caseIds[1], "<=2", ">300.5"]))]),
            ("latitude", [Text("Latitude", Or([.. latitudes, ">33.6", "<>-0.1"]))]),
            ("longitude", [Text("Longitude", Or([.. longitudes, "<-85.5", ">=-81.917"]))]),
            ("fever", [Text("FeverTemp", Or([fevers[0], "<" + fevers[1], ">101.65"]))]),
            ("checkbox", [Text("White", "Yes or (-)")]),
            ("legal values", [Text("Sex", Or([sexes[0], "M*", ">"]))]),
            ("date", [Text("DOB", Or([births[0], "<" + births[1], ">=2000-02-29"]))]),
            ("phone", [Text("HomePhone", Or([phones[0], ">(555) 0103", "<=(555) 0100"]))]),
            ("text", [Text("LastName", Or([.. lastNames, "Ba*", "*e_"]))]),
            (
                "multiline page two",
                [Text("PatientAddress", Or([.. addresses, addresses[0] + new string('x', 300), "*125*", ">"]))]
            ),
            ("multiline page three", [Text("BuyLastTenDays", Or([.. purchases, "Synthetic note 9*", "*Days."]))]),
            ("global record id", [Text("GlobalRecordId", Or([ids[0], "<" + ids[0]]))]),
            ("rec status", [Text("RecStatus", "0 or 1")]),
            (
                "both pages",
                [
                    Text("Latitude", Or([anchor[0], ">1000"])),
                    Text("White", anchor[1]),
                    Text("BuyLastTenDays", Or([anchor[2], "Zq*"])),
                ]
            ),
        ];
    }

    // The threshold searches' LastName alternatives: the seeded values, a trailing and a leading wildcard pattern for
    // each, spread evenly across the list, and values that match nothing in the rest.
    private static string[] ThresholdAlternatives(IReadOnlyList<string> seeded, int size)
    {
        var matching = seeded
            .SelectMany(value => value.Length > 1
                ? new[] { value, value + "*", "*" + value[1..] }
                : new[] { value, value + "*" })
            .ToArray();
        Assert.True(matching.Length < size);

        var alternatives = new string[size];
        var stride = size / matching.Length;
        var next = 0;
        for (var index = 0; index < size; index++)
        {
            alternatives[index] = next < matching.Length && index == next * stride
                ? matching[next++]
                : index % 2 == 0 ? NoMatch(index) : "Zq*" + index.ToString(CultureInfo.InvariantCulture);
        }

        return alternatives;
    }

    // Seeded LastName values of active CaseReport records, fewest matches first, while their matches together stay
    // within MaxSeededMatches; at least two, so excluding one record leaves a match.
    private static async Task<IReadOnlyList<string>> SeededLastNamesAsync(
        SqlConnection connection,
        CancellationToken token)
    {
        var sql = "SELECT p1.[LastName], COUNT_BIG(*) FROM " + CompleteRecords[CaseReport]
            + " WHERE b.[RecStatus] = 1 AND p1.[LastName] IS NOT NULL GROUP BY p1.[LastName]"
            + " ORDER BY COUNT_BIG(*), p1.[LastName]";
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(token);

        var values = new List<string>();
        var matches = 0L;
        while (await reader.ReadAsync(token))
        {
            var value = reader.GetString(0);
            var count = reader.GetInt64(1);
            if (!IsPlainCriterionText(value))
            {
                continue;
            }

            if (matches + count > MaxSeededMatches)
            {
                break;
            }

            values.Add(value);
            matches += count;
        }

        Assert.True(matches >= 2, "The database holds too few seeded LastName values.");
        return values;
    }

    // The GlobalRecordId of the first active record, by UniqueKey, whose LastName is the value.
    private static async Task<string> FirstActiveIdAsync(
        SqlConnection connection,
        string lastName,
        CancellationToken token)
    {
        var sql = "SELECT TOP (1) b.[GlobalRecordId] FROM " + CompleteRecords[CaseReport]
            + " WHERE b.[RecStatus] = 1 AND p1.[LastName] = @lastName ORDER BY b.[UniqueKey]";
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add(new SqlParameter("@lastName", SqlDbType.NVarChar, -1) { Value = lastName });
        var id = await command.ExecuteScalarAsync(token);
        return Assert.IsType<string>(id);
    }

    // Up to count distinct values of one column of the form's complete active records, as criterion text.
    private static async Task<IReadOnlyList<string>> SampleAsync(
        SqlConnection connection,
        string formName,
        string expression,
        int count,
        CancellationToken token)
    {
        var sql = "SELECT DISTINCT TOP (40) " + expression + " AS [v] FROM " + CompleteRecords[formName]
            + " WHERE b.[RecStatus] = 1 AND " + expression + " IS NOT NULL ORDER BY [v]";
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(token);

        var values = new List<string>();
        while (values.Count < count && await reader.ReadAsync(token))
        {
            if (CriterionText(reader.GetValue(0)) is { } text)
            {
                values.Add(text);
            }
        }

        Assert.True(values.Count == count, $"{formName} holds too few values of {expression}.");
        return values;
    }

    // One complete active record's values of the columns, as criterion text, for a multi-field search it must match.
    private static async Task<IReadOnlyList<string>> AnchorAsync(
        SqlConnection connection,
        string formName,
        IReadOnlyList<string> expressions,
        CancellationToken token)
    {
        var sql = "SELECT TOP (40) " + string.Join(", ", expressions) + " FROM " + CompleteRecords[formName]
            + " WHERE b.[RecStatus] = 1 AND "
            + string.Join(" AND ", expressions.Select(expression => expression + " IS NOT NULL"))
            + " ORDER BY b.[UniqueKey]";
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(token);

        while (await reader.ReadAsync(token))
        {
            var texts = new List<string>(expressions.Count);
            for (var index = 0; index < expressions.Count; index++)
            {
                if (CriterionText(reader.GetValue(index)) is { } text)
                {
                    texts.Add(text);
                }
            }

            if (texts.Count == expressions.Count)
            {
                return texts;
            }
        }

        Assert.Fail($"{formName} holds no record with every anchor value.");
        return [];
    }

    // A stored value as the text a client would search it with, or null when the parser would not read it back as
    // the same value: floats round-trip, dates are ISO, checkboxes Yes or No.
    private static string? CriterionText(object value) => value switch
    {
        string text => IsPlainCriterionText(text) ? text : null,
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        DateTime date when date.TimeOfDay == TimeSpan.Zero => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        bool flag => flag ? "Yes" : "No",
        _ => null,
    };

    // Text the parser keeps as one equality alternative: no wildcard, no " or " separator, no leading comparison
    // prefix and nothing trimmed.
    private static bool IsPlainCriterionText(string text) =>
        text.Length > 0
        && text == text.Trim()
        && text.IndexOfAny(['%', '*', '?']) < 0
        && !text.Contains(" or ", StringComparison.OrdinalIgnoreCase)
        && !text.StartsWith('<')
        && !text.StartsWith('>');

    private static string NoMatch(int index) =>
        "NoMatch-" + index.ToString(CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");

    private static string Or(IEnumerable<string> alternatives) => string.Join(" or ", alternatives);

    private static SearchCriterion Text(string field, string value) => new(field, FieldValue.FromText(value));

    private static IReadOnlyList<SearchCondition> Parse(SeededForm form, SearchCriterion[] criteria)
    {
        var result = SearchCriteriaParser.Parse(form.DataFields, criteria);
        Assert.True(
            result.IsSuccess,
            "The criteria did not parse; errors under: " + string.Join(", ", result.FieldErrors.Keys));
        return result.Conditions;
    }

    // The form as the store reads it: the reader's metadata and catalogue schema, the data fields and grid columns.
    private static async Task<SeededForm> ReadFormAsync(
        SqlConnection connection,
        string formName,
        CancellationToken token)
    {
        var options = new DatabaseOptions(TestDatabase.ConnectionString);
        var reader = new SqlMetadataReader(new SqlConnectionFactory(options), options, NullSqlCommandObserver.Instance);
        var summary = Assert.Single(await reader.ListFormsAsync(token), form => form.Name == formName);
        var read = await reader.ReadFormAsync(connection, null, summary.ViewId, token);
        Assert.NotNull(read);

        var (metadata, schema) = read.Value;
        return new SeededForm(
            schema,
            FormDefinitionFactory.GetDataFields(metadata),
            RecordQueryRules.GetGridColumns(metadata));
    }

    private static Task<IReadOnlyList<SearchRow>> RunAsync(
        SqlConnection connection,
        SearchSelect select,
        CancellationToken token) =>
        SqlExecution.ReadAsync(
            connection,
            null,
            Operation,
            select.Statement,
            DatabaseOptions.DefaultCommandTimeoutSeconds,
            NullSqlCommandObserver.Instance,
            token,
            ReadRowsAsync);

    private static async Task<IReadOnlyList<SearchRow>> ReadRowsAsync(DbDataReader reader, CancellationToken token)
    {
        var rows = new List<SearchRow>();
        while (await reader.ReadAsync(token))
        {
            rows.Add(new SearchRow(
                reader.GetString(SearchSqlBuilder.IdOrdinal),
                Convert.ToInt64(reader.GetValue(SearchSqlBuilder.UniqueKeyOrdinal), CultureInfo.InvariantCulture),
                Convert.ToInt64(reader.GetValue(SearchSqlBuilder.RecStatusOrdinal), CultureInfo.InvariantCulture),
                reader.GetInt64(SearchSqlBuilder.PositionOrdinal),
                reader.GetInt64(SearchSqlBuilder.TotalOrdinal)));
        }

        return rows;
    }

    private sealed record SeededForm(
        FormSchema Schema,
        DataFieldSet DataFields,
        IReadOnlyList<DataFieldMetadata> GridColumns);

    // One Find result as the statement returns it: id, key, status, navigation position and the uncapped total.
    private readonly record struct SearchRow(string Id, long UniqueKey, long RecStatus, long Position, long Total);
}
