using System.Collections.ObjectModel;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using EpiInfo.Data.Metadata;
using EpiInfo.Data.Records;
using EpiInfo.Data.Sql;
using EpiInfo.Domain.Forms;
using EpiInfo.Domain.Metadata;
using EpiInfo.Domain.Records;
using EpiInfo.Domain.Search;
using EpiInfo.Domain.Storage;
using EpiInfo.Domain.Validation;
using EpiInfo.Domain.Values;
using EpiInfo.Tests.Shared;
using Microsoft.Data.SqlClient;
using Xunit.Abstractions;

namespace EpiInfo.Data.Tests.Records;

/// <summary>
/// Integration tests of the record statements the repository builds and runs, against the seeded forms of the
/// rehearsal database. The metadata reader supplies every form, page, table and column schema, so each identifier a
/// statement names is one the catalogue returned. The cases pin five contracts:
/// <list type="bullet">
/// <item><description>
/// The form write lock a create and an update take first, <see cref="RecordSqlBuilder.AcquireFormWriteLock"/>: it
/// names no table, binds every value as a typed parameter, and holds the form's application lock exclusive for a
/// create and shared for an update until its transaction ends; a write whose lock request conflicts with a held
/// lock waits there, and the command timeout fails it as unavailable with nothing written.
/// </description></item>
/// <item><description>
/// The page-row statements the repository sends when it creates and updates a record,
/// <see cref="RecordSqlBuilder.InsertPage"/> and <see cref="RecordSqlBuilder.UpdatePageColumns"/>: they name writable
/// columns only, and each column binds only its own field's value.
/// </description></item>
/// <item><description>
/// Record identity: every lookup by <c>GlobalRecordId</c>, the locked read before a write included, matches a stored
/// id exactly, so a spelling that differs from it only by trailing spaces, within or beyond the column's width, finds
/// no row; the base-row reads return the id as stored, the spelling every later statement binds; and Find's exclusion
/// of the open record matches the same way, so such a spelling excludes nothing.
/// </description></item>
/// <item><description>
/// Stored reads: every stored data value of the seeded forms, with the runtime type SqlClient returns for its column,
/// converts through <see cref="ColumnValueConverter.FromDbValue"/> to the value stored.
/// </description></item>
/// <item><description>
/// The repository's reads (AAP 0.3.4.4 and <c>DATA-01</c>): on a seeded record, every read of
/// <see cref="SqlRecordRepository"/> sends only <c>SELECT</c> and changes nothing stored.
/// </description></item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// The class only reads. The reader's commands are all <c>SELECT</c>, the page-row write statements are only built,
/// never sent, and the stored-read and repository-read cases send only <c>SELECT</c>. The identity lookups run their
/// locked read in a transaction that is rolled back, and the record they look up is the seeded row with the lowest
/// <c>UniqueKey</c>, chosen by SQL rather than hard-coded; the Find exclusion uses the lowest such active row with a
/// row on every page, and the repository reads the lowest such row whose <c>LastName</c> is plain seeded text. The
/// identity cases' failure messages are fixed text, so no id reaches the output except through an equality assertion;
/// the repository-read case describes commands with <see cref="RecordedCommand.Describe"/> without parameter values.
/// The form-write-lock cases take only application locks, in a transaction they roll back or on a session they close,
/// and change no data: the lock-wait case's insert and its update of an id no row holds both fail at the lock, before
/// any INSERT or UPDATE, and it compares snapshots to prove it.
/// Cases that compare snapshots hold the exclusive <see cref="DatabaseWriteLock"/>, the others the shared one.
/// </para>
/// <para>
/// The repository's writes are proved by <c>SqlRecordRepositoryWriteTests</c> in <c>EpiInfo.Api.Tests</c>: the insert
/// of the base row and then one row on every page in page order in one transaction, the delta update, the legacy
/// repair of a missing page row (<c>Epi.Core/Data/Services/CollectedDataProvider.cs:1360-1380</c>), the base-only soft
/// delete, and the refused update of a deleted or unknown record. Tests may write only records created through the API
/// and the few fixtures AAP 0.3.7 ("Data reach of tests") lists, and this assembly references the data layer alone,
/// so it cannot create such a record; that class creates each one through the API and observes the repository's
/// statements through the host's command observer.
/// </para>
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

    // The form write lock: the operation its case reports, the resource prefix and owner RecordSqlBuilder binds, and
    // the modes APPLOCK_MODE reports, which are also the modes the builder binds.
    private const string LockOperation = "test.formWriteLock";
    private const string FormWriteLockResourcePrefix = "EpiInfo.Web.RecordWrites/";
    private const string TransactionLockOwner = "Transaction";
    private const string ExclusiveLockMode = "Exclusive";
    private const string SharedLockMode = "Shared";
    private const string NoLockMode = "NoLock";

    // The command timeout of the lock-wait case, in seconds, and the SQL error number SqlClient gives a command that
    // outlasts it.
    private const int LockWaitTimeoutSeconds = 2;
    private const int CommandTimeoutErrorNumber = -2;

    // Trailing spaces that keep the padded id within the seeded nvarchar(255) column.
    private const int WithinWidthPadding = 3;

    // Trailing spaces that take the padded id beyond the seeded nvarchar(255) column.
    private const int OverWidthPadding = 300;

    // FoodHistory's page-one Text field (FoodHistory2.LastName, nvarchar(4000)), which the repository-read case filters
    // and searches on with a seeded value (testdata/sql/03_forms.sql, 05_data_tables.sql).
    private const string LastNameField = "LastName";

    // The seeded form with two pages, on which the repository-read case runs.
    private const string TwoPageForm = "FoodHistory";

    // The repository-read case's choice of its seeded record: the RecStatus of an active record, the test's own value,
    // and the patterns of text starting with an ASCII letter and of any other character, each bound as a parameter
    // and compared under a binary collation, in which [A-Za-z] is exactly the ASCII letters.
    private const short ActiveStatus = 1;
    private const string LetterFirstPattern = "[A-Za-z]%";
    private const string NonLetterPattern = "%[^A-Za-z]%";
    private const string BinaryCollation = "Latin1_General_BIN2";

    // Covers a wait of up to 300 seconds for the test lock and the work behind it.
    private static readonly TimeSpan RepositoryTestTimeout = TimeSpan.FromMinutes(10);

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

    // The form write lock of a create (exclusive) and of an update (shared): fixed text that names no table and holds
    // no value, every value bound by position with an explicit type, the resource being the reader-built base-table
    // name after the fixed prefix. Run in a transaction, it is granted, the transaction holds the form's application
    // lock in that mode, and the rollback releases it.
    [Theory]
    [InlineData("CaseReport", true, ExclusiveLockMode)]
    [InlineData("CaseReport", false, SharedLockMode)]
    [InlineData("FoodHistory", true, ExclusiveLockMode)]
    [InlineData("FoodHistory", false, SharedLockMode)]
    public async Task AcquireFormWriteLock_BindsEveryValueAndHoldsTheFormLockInItsModeUntilTheTransactionEnds(
        string formName,
        bool exclusive,
        string expectedMode)
    {
        await using DatabaseWriteLock dbLock = await DatabaseWriteLock.AcquireSharedAsync();
        ReaderForm form = await ReaderForm.ReadAsync(formName);
        string resource = FormWriteLockResourcePrefix + form.Schema.BaseTable.Name;

        SqlStatement statement = RecordSqlBuilder.AcquireFormWriteLock(form.Schema, exclusive);

        Assert.Equal(
            "DECLARE @result int; EXEC @result = sys.sp_getapplock @Resource = @p0, @LockMode = @p1, " +
            "@LockOwner = @p2, @LockTimeout = @p3; SELECT @result",
            statement.Text);
        Assert.Empty(SqlCommandRecorder.BracketedIdentifiers(statement.Text));
        Assert.Equal(
            new SqlParameterValue[]
            {
                new("@p0", DbType.String, resource, ColumnValueConverter.MaxSize),
                new("@p1", DbType.String, expectedMode, ColumnValueConverter.MaxSize),
                new("@p2", DbType.String, TransactionLockOwner, ColumnValueConverter.MaxSize),
                new("@p3", DbType.Int32, -1, null),
            },
            statement.Parameters);

        var options = new DatabaseOptions(TestDatabase.ConnectionString);
        await using SqlConnection connection = await TestDatabase.OpenConnectionAsync();
        await using (SqlTransaction transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted))
        {
            int? result = await SqlExecution.ExecuteScalarAsync<int?>(
                connection,
                transaction,
                LockOperation,
                statement,
                options.CommandTimeoutSeconds,
                NullSqlCommandObserver.Instance,
                CancellationToken.None);
            Assert.True(result is 0 or 1, "The form write lock was not granted.");
            Assert.Equal(expectedMode, await ApplicationLockModeAsync(connection, transaction, resource));
            await transaction.RollbackAsync();
        }

        await using (SqlTransaction later = connection.BeginTransaction(IsolationLevel.ReadCommitted))
        {
            Assert.Equal(NoLockMode, await ApplicationLockModeAsync(connection, later, resource));
            await later.RollbackAsync();
        }
    }

    // The mode in which the transaction holds the application lock on resource, as APPLOCK_MODE names it.
    private static async Task<string> ApplicationLockModeAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string resource)
    {
        await using SqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT APPLOCK_MODE(N'public', @resource, N'Transaction');";
        command.Parameters.Add(new SqlParameter("@resource", SqlDbType.NVarChar, 255) { Value = resource });
        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }

    // A second session holds the form's application lock in a mode that conflicts with the write's: shared, as an
    // update holds it, against a create's exclusive request; exclusive, as a create holds it, against an update's
    // shared request. The write waits at its lock until the command timeout ends the wait, which fails it as
    // unavailable (SQL error -2) with nothing written: it sent no INSERT or UPDATE, the snapshot shows no change, and
    // its update targets an id no row holds, so no record exists for it to change.
    [Theory]
    [InlineData(SharedLockMode, true)]
    [InlineData(ExclusiveLockMode, false)]
    public async Task FormWriteLock_WriteWaitingPastTheCommandTimeout_FailsAsUnavailableWithNothingWritten(
        string heldMode,
        bool insert)
    {
        using var timeout = new CancellationTokenSource(RepositoryTestTimeout);
        CancellationToken cancellationToken = timeout.Token;
        await using DatabaseWriteLock dbLock = await DatabaseWriteLock.AcquireExclusiveAsync(cancellationToken);

        ReaderForm form = await ReaderForm.ReadAsync(TwoPageForm);
        var options = new DatabaseOptions(TestDatabase.ConnectionString, LockWaitTimeoutSeconds);
        var connections = new SqlConnectionFactory(options);
        var recorder = new SqlCommandRecorder();
        var repository = new SqlRecordRepository(
            connections,
            new SqlMetadataReader(connections, options, recorder),
            options,
            recorder);
        SqlSnapshot before = await SqlSnapshot.CaptureAsync(cancellationToken);

        await using SqlConnection holder = await TestDatabase.OpenConnectionAsync(cancellationToken);
        await using (SqlCommand take = holder.CreateCommand())
        {
            take.CommandText =
                "DECLARE @result int; EXEC @result = sys.sp_getapplock @Resource = @resource, @LockMode = @mode, " +
                "@LockOwner = N'Session', @LockTimeout = 0; SELECT @result;";
            take.Parameters.Add(new SqlParameter("@resource", SqlDbType.NVarChar, 255)
            {
                Value = FormWriteLockResourcePrefix + form.Schema.BaseTable.Name,
            });
            take.Parameters.Add(new SqlParameter("@mode", SqlDbType.NVarChar, 32) { Value = heldMode });
            Assert.True(
                await take.ExecuteScalarAsync(cancellationToken) is 0,
                "The second session could not take the form's application lock.");
        }

        IReadOnlyDictionary<string, FieldValue> values =
            Values(ignoreCase: false, (LastNameField, FieldValue.FromText(TestDatabase.NewMarker())));
        var stamp = new SaveStamp(Environment.UserName, SaveStamp.TruncateToSeconds(DateTime.Now));
        DataStoreUnavailableException failure = insert
            ? await Assert.ThrowsAsync<DataStoreUnavailableException>(() =>
                repository.InsertAsync(form.Metadata, values, stamp, permissions: null, cancellationToken))
            : await Assert.ThrowsAsync<DataStoreUnavailableException>(() =>
                repository.UpdateAsync(
                    form.Metadata,
                    Guid.NewGuid().ToString(),
                    values,
                    () => stamp,
                    _ => RecordValidationResult.Success(new Dictionary<string, FieldValue>(StringComparer.Ordinal)),
                    permissions: null,
                    cancellationToken));

        Assert.Equal(WriteOutcome.NotWritten, failure.Outcome);
        Assert.Equal(CommandTimeoutErrorNumber, failure.SqlErrorNumber);
        Assert.Empty(recorder.Writes());
        RecordedCommand waited = Assert.Single(recorder.Failed());
        Assert.Contains("sp_getapplock", waited.CommandText, StringComparison.Ordinal);
        Assert.Equal(CommandTimeoutErrorNumber, waited.SqlErrorNumber);
        Assert.True(
            SqlSnapshot.Compare(before, await SqlSnapshot.CaptureAsync(cancellationToken)).IsEmpty,
            "The write that waited past the command timeout changed the record tables.");
    }

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

    // The same two pages, with both seeded Age rows kept as they are beside FoodHistory's Occupation: one exact name on
    // two pages, which metaFields allows and no seeded form has. Both pages hold Position 0 and CaseReport's PageId is
    // the lower, so storage order makes the CaseReport row the form's one data field Age, though the FoodHistory row
    // is listed first. The FoodHistory row is no data field, so the submitted Age binds CaseReport1.[Age] alone and
    // the FoodHistory2 row, built from that page's data-field columns, never names [Age], whatever the map's comparer.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InsertPage_ExactNameOnTwoPages_BindsOnlyTheFirstInStorageOrder(bool mapIgnoresCase)
    {
        ReaderForm caseReport = await ReaderForm.ReadAsync("CaseReport");
        ReaderForm foodHistory = await ReaderForm.ReadAsync("FoodHistory");
        DataFieldMetadata caseReportAge = caseReport.Column("Age").Field;
        DataFieldMetadata foodHistoryAge = foodHistory.Column("Age").Field;
        ProjectedColumn occupation = foodHistory.Column("Occupation");
        Assert.Equal(caseReportAge.Page.Position, foodHistoryAge.Page.Position);
        Assert.True(caseReportAge.Page.PageId < foodHistoryAge.Page.PageId);
        Assert.Equal(foodHistoryAge.Page.PageId, occupation.Field.Page.PageId);
        FormMetadata repeatedNameForm = caseReport.Metadata with
        {
            Pages = [foodHistoryAge.Page, caseReportAge.Page],
            Fields = [foodHistoryAge.Field, occupation.Field.Field, caseReportAge.Field],
        };

        // Each field resolves against its own form's reader-built schema, the only one that holds its page.
        ProjectedColumn[] columns = FormDefinitionFactory.GetDataFields(repeatedNameForm)
            .Select((field, ordinal) => RecordSqlBuilder.ResolveDataColumn(
                field.Page.PageId == caseReportAge.Page.PageId ? caseReport.Schema : foodHistory.Schema,
                field,
                ordinal))
            .ToArray();
        Assert.Equal(new[] { "Age", "Occupation" }, columns.Select(column => column.Field.Name));
        ProjectedColumn age = Assert.Single(
            columns,
            column => string.Equals(column.Field.Name, "Age", StringComparison.Ordinal));
        Assert.Same(caseReportAge.Field, age.Field.Field);
        Assert.Same(caseReport.Column("Age").Column, age.Column);

        IReadOnlyDictionary<string, FieldValue> values = Values(
            mapIgnoresCase,
            ("Age", FieldValue.FromNumber(5)),
            ("Occupation", FieldValue.FromText("Cook")));

        SqlStatement caseReportRow = RecordSqlBuilder.InsertPage(
            caseReport.PageOf(age.Field),
            GlobalRecordId,
            columns.Where(column => column.Field.Page.PageId == caseReportAge.Page.PageId).ToArray(),
            values);
        SqlStatement foodHistoryRow = RecordSqlBuilder.InsertPage(
            foodHistory.PageOf(occupation.Field),
            GlobalRecordId,
            columns.Where(column => column.Field.Page.PageId == foodHistoryAge.Page.PageId).ToArray(),
            values);

        Assert.StartsWith("INSERT INTO [CaseReport1] (", caseReportRow.Text, StringComparison.Ordinal);
        Assert.StartsWith("INSERT INTO [FoodHistory2] (", foodHistoryRow.Text, StringComparison.Ordinal);
        AssertBound(PageInsert.Columns(caseReportRow), "[Age]", 5d);

        IReadOnlyList<(string Column, SqlParameterValue Parameter)> foodHistoryColumns =
            PageInsert.Columns(foodHistoryRow);
        Assert.Equal(
            new[] { "[GlobalRecordId]", SqlIdentifier.Quote(occupation.Column) },
            foodHistoryColumns.Select(entry => entry.Column));
        Assert.Equal("Cook", foodHistoryColumns[1].Parameter.Value);
        Assert.DoesNotContain("[Age]", foodHistoryRow.Text, StringComparison.Ordinal);
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
        Assert.Equal(
            "UPDATE [CaseReport1] SET [LastName] = @p0 WHERE [GlobalRecordId] = @p1; SELECT @@ROWCOUNT",
            statement.Text);
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
            RecordSqlBuilder.SetPositionLookup(form.Schema, form.GlobalRecordId, includeDeleted: false));
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
            RecordSqlBuilder.SetPositionLookup(form.Schema, paddedId, includeDeleted: false));
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

    // AAP 0.3.4.4 "Open record": Find leaves the open record out on the server, before the total and the cap. The
    // exclusion matches the id as every lookup by id does, so the stored id excludes its record, while a spelling that
    // differs from it only by trailing spaces, within or beyond the column's width, identifies no record, as the
    // position lookup shows, and excludes nothing. The record is the active seeded one with the lowest UniqueKey that
    // has a row on every page, chosen by SQL, and a criterion on its id selects it alone.
    [Theory]
    [MemberData(nameof(PaddedIds))]
    public async Task Find_ExcludedId_LeavesOutOnlyTheRecordALookupByThatIdFinds(string formName, int padding)
    {
        await TestDatabase.EnsureReachableAsync();
        await using DatabaseWriteLock dbLock = await DatabaseWriteLock.AcquireSharedAsync();

        var recorder = new SqlCommandRecorder();
        await using SeededForm form = await SeededForm.OpenAsync(formName, recorder);
        IdRow record = await form.ReadCompleteActiveRowAsync();
        string paddedId = record.GlobalRecordId + new string(' ', padding);
        bool overWidth = paddedId.Length * sizeof(char) > form.GlobalRecordIdColumn.MaxLengthBytes;
        Assert.True(
            overWidth == (padding == OverWidthPadding),
            "The padded id is not on the intended side of the id column's width.");
        recorder.Clear();

        IReadOnlyList<FoundRow> unexcluded = await form.FindByIdAsync(record.GlobalRecordId, excludeGlobalRecordId: null);
        AssertSingleFoundRow(unexcluded, record, "The criterion on the record's id did not find exactly that record.");

        IReadOnlyList<FoundRow> excluded = await form.FindByIdAsync(record.GlobalRecordId, record.GlobalRecordId);
        Assert.True(excluded.Count == 0, "The stored id did not exclude its record and its total.");

        IReadOnlyList<IdRow> positioned = await form.ReadBaseRowsAsync(
            transaction: null,
            RecordSqlBuilder.SetPositionLookup(form.Schema, paddedId, includeDeleted: false));
        Assert.True(positioned.Count == 0, "The position lookup matched a padded id to a stored row.");

        IReadOnlyList<FoundRow> padded = await form.FindByIdAsync(record.GlobalRecordId, paddedId);
        AssertSingleFoundRow(padded, record, "A padded id excluded the record it does not identify.");

        AssertOnlySuccessfulReads(recorder, expectedCommands: 4);
    }

    private static void AssertSingleFoundRow(IReadOnlyList<FoundRow> rows, IdRow record, string message)
    {
        Assert.True(rows.Count == 1 && rows[0].Total == 1, message);
        Assert.Equal(record.UniqueKey, rows[0].UniqueKey);
        Assert.Equal(record.GlobalRecordId, rows[0].GlobalRecordId, StringComparer.Ordinal);
    }

    // Every stored value of every data column of the form, read uncoerced with GetValue, converts through
    // FromDbValue to the value stored, and a float column's number is the stored double bit for bit. Each page table
    // is read whole, by a SELECT naming only reader-built identifiers, and the record projection the repository
    // reads, RecordAt at the first position of the full set, is converted the same way.
    [Theory]
    [MemberData(nameof(Forms))]
    public async Task FromDbValue_EveryStoredDataValueOfSeededForm_ConvertsToTheStoredValue(string formName)
    {
        await TestDatabase.EnsureReachableAsync();
        await using DatabaseWriteLock dbLock = await DatabaseWriteLock.AcquireSharedAsync();

        ReaderForm form = await ReaderForm.ReadAsync(formName);
        var recorder = new SqlCommandRecorder();
        await using SqlConnection connection = await TestDatabase.OpenConnectionAsync();

        int pageReads = 0;
        int floatsCompared = 0;
        foreach (PageTableSchema page in form.Schema.Pages)
        {
            if (page.Table is not { } table)
            {
                continue;
            }

            IReadOnlyList<ProjectedColumn> columns = form.ColumnsOn(page);
            Assert.NotEmpty(columns);

            // The statement names only reader-built identifiers and holds no value, so it has no parameter.
            var wholePage = new SqlStatement(
                "SELECT " + string.Join(", ", columns.Select(column => SqlIdentifier.Quote(column.Column))) +
                " FROM " + SqlIdentifier.Quote(table),
                []);
            (int rows, int floats) = await ReadAndConvertAsync(
                connection,
                recorder,
                wholePage,
                [.. columns.Select((column, ordinal) => (column, ordinal))]);
            Assert.True(rows > 0, "A seeded page table returned no row.");
            pageReads++;
            floatsCompared += floats;
        }

        RecordSelect first = RecordSqlBuilder.RecordAt(
            form.Schema,
            [.. form.DataColumns.Select(column => column.Field)],
            includeDeleted: true,
            position: 1);
        (int recordRows, int recordFloats) = await ReadAndConvertAsync(
            connection,
            recorder,
            first.Statement,
            [.. first.Columns.Select(column => (column, column.Ordinal))]);
        Assert.True(recordRows == 1, "The record read at the first position did not return exactly one row.");

        Assert.True(pageReads > 0, "The seeded form has no page table.");
        Assert.True(floatsCompared + recordFloats > 0, "No stored float value was compared.");
        AssertOnlySuccessfulReads(recorder, expectedCommands: pageReads + 1);
    }

    // Runs a SELECT and converts the value at each column's result ordinal of every row through FromDbValue, checking
    // it against the raw value. Returns the rows read and the non-null float values compared.
    private static Task<(int Rows, int Floats)> ReadAndConvertAsync(
        SqlConnection connection,
        SqlCommandRecorder recorder,
        SqlStatement statement,
        IReadOnlyList<(ProjectedColumn Column, int Ordinal)> columns) =>
        SqlExecution.ReadAsync(
            connection,
            transaction: null,
            Operation,
            statement,
            DatabaseOptions.DefaultCommandTimeoutSeconds,
            recorder,
            CancellationToken.None,
            async (rows, token) =>
            {
                int read = 0;
                int floats = 0;
                while (await rows.ReadAsync(token))
                {
                    read++;
                    foreach ((ProjectedColumn column, int ordinal) in columns)
                    {
                        object stored = rows.GetValue(ordinal);
                        FieldValue value = ColumnValueConverter.FromDbValue(
                            column.Column.Metadata,
                            column.Field.FieldType,
                            stored);
                        if (AssertStoredValue(column.Field.FieldType, stored, value))
                        {
                            floats++;
                        }
                    }
                }

                return (read, floats);
            });

    // The converted value is the stored value as its field type reads it. Returns true for a non-null float, whose
    // number must be the stored double bit for bit.
    private static bool AssertStoredValue(FieldType fieldType, object stored, FieldValue value)
    {
        if (stored is DBNull)
        {
            Assert.True(value.IsNull, "A database NULL did not convert to the null value.");
            return false;
        }

        switch (fieldType)
        {
            case FieldType.Number when stored is double number:
                Assert.True(value.TryGetNumber(out double converted), "A stored float did not convert to a number.");
                Assert.Equal(BitConverter.DoubleToInt64Bits(number), BitConverter.DoubleToInt64Bits(converted));
                return true;

            case FieldType.Number:
                Assert.True(value.TryGetInteger(out long whole), "A stored integer did not convert exactly.");
                Assert.Equal(Convert.ToInt64(stored, CultureInfo.InvariantCulture), whole);
                return false;

            case FieldType.Date:
                Assert.Equal(DateOnly.FromDateTime(Assert.IsType<DateTime>(stored)), value.AsDate());
                return false;

            case FieldType.Checkbox:
                Assert.Equal(Assert.IsType<bool>(stored), value.AsBoolean());
                return false;

            case FieldType.Option:
                Assert.Equal(Convert.ToInt32(stored, CultureInfo.InvariantCulture), value.AsIndex());
                return false;

            default:
                Assert.Equal(Assert.IsType<string>(stored), value.AsText());
                return false;
        }
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

    // AAP 0.3.4.4 and DATA-01: every read of the repository sends only SELECT statements and changes nothing stored.
    // The reads run on a seeded record chosen by SQL, so the case writes nothing; the repository's writes are proved on
    // records created through the API, by SqlRecordRepositoryWriteTests in EpiInfo.Api.Tests.
    [Fact]
    public async Task ReadsIssueOnlySelect()
    {
        await TestDatabase.EnsureReachableAsync();
        using var timeout = new CancellationTokenSource(RepositoryTestTimeout);
        CancellationToken cancellationToken = timeout.Token;

        // Exclusive, because the test compares snapshots taken before and after it.
        await using var writeLock = await DatabaseWriteLock.AcquireExclusiveAsync(cancellationToken);

        // The repository as the store builds it, over one recorder that hears every command the repository and its
        // metadata reader send.
        var options = new DatabaseOptions(TestDatabase.ConnectionString);
        var recorder = new SqlCommandRecorder();
        var connections = new SqlConnectionFactory(options);
        var repository = new SqlRecordRepository(
            connections,
            new SqlMetadataReader(connections, options, recorder),
            options,
            recorder);
        ReaderForm form = await ReaderForm.ReadAsync(TwoPageForm);
        (string id, string lastName) = await ReadPlainSeededRecordAsync(form, cancellationToken);
        SearchParseResult criteria = SearchCriteriaParser.Parse(
            FormDefinitionFactory.GetDataFields(form.Metadata),
            [new SearchCriterion(LastNameField, FieldValue.FromText(lastName))]);
        Assert.True(criteria.IsSuccess, "The seeded LastName criterion did not parse.");

        SqlSnapshot before = await SqlSnapshot.CaptureAsync(cancellationToken);
        recorder.Clear();
        int activeCount = await repository.CountAsync(form.Metadata, includeDeleted: false, cancellationToken);
        int allCount = await repository.CountAsync(form.Metadata, includeDeleted: true, cancellationToken);
        RecordDto? first =
            await repository.GetAtPositionAsync(form.Metadata, 1, includeDeleted: false, cancellationToken);
        RecordDto? byId = await repository.GetByIdAsync(form.Metadata, id, cancellationToken);
        NavigationPosition? activePosition =
            await repository.GetSetPositionAsync(form.Metadata, id, includeDeleted: false, cancellationToken);
        NavigationPosition? allPosition =
            await repository.GetSetPositionAsync(form.Metadata, id, includeDeleted: true, cancellationToken);
        RecordPage firstPage = await repository.ListAsync(form.Metadata, RecordListQuery.Default, cancellationToken);
        RecordPage filtered =
            await repository.ListAsync(form.Metadata, RecordListQuery.Default with { Q = lastName }, cancellationToken);
        SearchResponse found = await repository.SearchAsync(
            form.Metadata,
            criteria.Conditions,
            includeDeleted: false,
            excludeGlobalRecordId: null,
            cancellationToken);
        IReadOnlyList<RecordedCommand> commands = recorder.Commands;
        SqlSnapshot after = await SqlSnapshot.CaptureAsync(cancellationToken);

        Assert.True(recorder.Writes().Count == 0, Describe("A read sent a write statement.", commands));
        Assert.NotEmpty(commands);
        Assert.All(commands, command =>
        {
            Assert.True(command.Completed && command.Succeeded, Describe("A read did not succeed.", [command]));
            Assert.True(IsReadStatement(command), Describe("A read sent a statement other than a query.", [command]));
        });
        SnapshotDiff diff = SqlSnapshot.Compare(before, after);
        Assert.True(diff.IsEmpty, diff.ToString());

        Assert.True(activeCount > 0 && allCount >= activeCount, "The counts do not describe the seeded form.");
        Assert.NotNull(first);
        Assert.NotEmpty(firstPage.Items);
        Assert.NotNull(byId);
        Assert.Equal(id, byId.GlobalRecordId);
        Assert.NotNull(activePosition);
        Assert.NotNull(allPosition);
        Assert.NotNull(activePosition.Position);
        Assert.True(
            filtered.Total >= 1 && filtered.Items.Any(item => item.GlobalRecordId == id),
            "The records-list q of the record's LastName did not list the record.");
        SearchResult[] hits = found.Items.Where(item => item.GlobalRecordId == id).ToArray();
        Assert.True(hits.Length == 1, "The LastName criterion did not find the record exactly once.");
        Assert.Equal(activePosition.Position, hits[0].Position);
    }

    // Picks by SQL the active seeded base row with the lowest UniqueKey that has a row on every page and whose LastName
    // is ASCII letters only: no test marker, which holds "-" and digits, and a plain Find criterion, with no wildcard,
    // alternative or comparison prefix. Returns its stored id and LastName. The statement names only reader-built
    // identifiers and binds every value as a typed parameter.
    private static async Task<(string GlobalRecordId, string LastName)> ReadPlainSeededRecordAsync(
        ReaderForm form,
        CancellationToken cancellationToken)
    {
        ProjectedColumn lastName = form.Column(LastNameField);
        TableSchema lastNameTable = Assert.IsType<TableSchema>(form.PageOf(lastName.Field).Table);
        string baseId = SqlIdentifier.Quote(RecordSqlBuilder.Require(form.Schema.GlobalRecordId));
        string storedName = "n." + SqlIdentifier.Quote(lastName.Column);
        string binaryName = storedName + " COLLATE " + BinaryCollation;
        var sql = new StringBuilder("SELECT TOP (1) b.").Append(baseId).Append(", ").Append(storedName)
            .Append(" FROM ").Append(SqlIdentifier.Quote(form.Schema.BaseTable)).Append(" b INNER JOIN ")
            .Append(SqlIdentifier.Quote(lastNameTable)).Append(" n ON n.")
            .Append(SqlIdentifier.Quote(RecordSqlBuilder.RequirePageKey(lastNameTable))).Append(" = b.").Append(baseId)
            .Append(" WHERE b.").Append(SqlIdentifier.Quote(RecordSqlBuilder.Require(form.Schema.RecStatus)))
            .Append(" = @recStatus AND ").Append(binaryName).Append(" LIKE @letterFirst AND ").Append(binaryName)
            .Append(" NOT LIKE @nonLetter");
        foreach (PageTableSchema page in form.Schema.Pages)
        {
            if (page.Table is { } table)
            {
                sql.Append(" AND EXISTS (SELECT 1 FROM ").Append(SqlIdentifier.Quote(table)).Append(" p WHERE p.")
                    .Append(SqlIdentifier.Quote(RecordSqlBuilder.RequirePageKey(table))).Append(" = b.")
                    .Append(baseId).Append(')');
            }
        }

        sql.Append(" ORDER BY b.").Append(SqlIdentifier.Quote(RecordSqlBuilder.Require(form.Schema.UniqueKey)));
        await using SqlConnection connection = await TestDatabase.OpenConnectionAsync(cancellationToken);
        await using var command = new SqlCommand(sql.ToString(), connection);
        command.Parameters.Add("@recStatus", SqlDbType.SmallInt).Value = ActiveStatus;
        command.Parameters.Add("@letterFirst", SqlDbType.NVarChar, LetterFirstPattern.Length).Value =
            LetterFirstPattern;
        command.Parameters.Add("@nonLetter", SqlDbType.NVarChar, NonLetterPattern.Length).Value = NonLetterPattern;
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(
            await reader.ReadAsync(cancellationToken),
            "The seeded form has no active record with a row on every page and a plain LastName.");
        return (reader.GetString(0), reader.GetString(1));
    }

    // An assertion message that lists commands without their parameter values.
    private static string Describe(string message, IReadOnlyList<RecordedCommand> commands) =>
        commands.Count == 0
            ? message + " No command was recorded."
            : message + Environment.NewLine + string.Join(
                Environment.NewLine,
                commands.Select(command => command.Describe(includeParameterValues: false)));

    // The leading keyword of a command's text, upper-cased: INSERT, UPDATE, SELECT, WITH or IF.
    private static string Verb(RecordedCommand command)
    {
        ReadOnlySpan<char> text = command.CommandText.AsSpan().TrimStart();
        int length = 0;
        while (length < text.Length && char.IsAsciiLetter(text[length]))
        {
            length++;
        }

        return text[..length].ToString().ToUpperInvariant();
    }

    // A record read starts with SELECT, or with WITH for Find's common table expression. The metadata reader's
    // catalogue query (MetadataQueries.Columns) starts with IF, choosing one of two SELECT forms by the database
    // compatibility level, so a metadata read may start with IF too; the write scan covers both of its branches.
    private static bool IsReadStatement(RecordedCommand command)
    {
        string verb = Verb(command);
        return !command.IsWrite
            && (verb is "SELECT" or "WITH"
                || (verb == "IF" && command.Operation.StartsWith("metadata.", StringComparison.Ordinal)));
    }

    // A base row's stored id and key, as a by-id statement returned them.
    private readonly record struct IdRow(string GlobalRecordId, int UniqueKey);

    // A Find row's stored id and key, and the total of matches every row carries.
    private readonly record struct FoundRow(string GlobalRecordId, int UniqueKey, long Total);

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
            DataFieldSet dataFields,
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

        public DataFieldSet DataFields { get; }

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
                DataFieldSet dataFields = FormDefinitionFactory.GetDataFields(read.Value.Form);
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

        // Picks by SQL the active base row with the lowest UniqueKey that has a row on every page table, a record Find
        // can return. The statement names only reader-built identifiers and binds the test's active status as
        // @activeStatus, typed as the smallint RecStatus column of both seeded base tables.
        public async Task<IdRow> ReadCompleteActiveRowAsync()
        {
            string baseId = SqlIdentifier.Quote(GlobalRecordIdColumn);
            string uniqueKey = SqlIdentifier.Quote(RecordSqlBuilder.Require(Schema.UniqueKey));
            var sql = new StringBuilder("SELECT TOP (1) b.").Append(baseId).Append(", b.").Append(uniqueKey)
                .Append(" FROM ").Append(SqlIdentifier.Quote(Schema.BaseTable)).Append(" b WHERE b.")
                .Append(SqlIdentifier.Quote(RecordSqlBuilder.Require(Schema.RecStatus))).Append(" = @activeStatus");
            foreach (PageTableSchema page in Schema.Pages)
            {
                if (page.Table is { } table)
                {
                    sql.Append(" AND EXISTS (SELECT 1 FROM ").Append(SqlIdentifier.Quote(table)).Append(" p WHERE p.")
                        .Append(SqlIdentifier.Quote(RecordSqlBuilder.RequirePageKey(table))).Append(" = b.")
                        .Append(baseId).Append(')');
                }
            }

            sql.Append(" ORDER BY b.").Append(uniqueKey);
            IReadOnlyList<IdRow> rows = await ReadRowsAsync(
                _connection,
                transaction: null,
                _commandTimeoutSeconds,
                _observer,
                new SqlStatement(
                    sql.ToString(),
                    [new SqlParameterValue("@activeStatus", DbType.Int16, ActiveStatus, Size: null)]),
                globalRecordIdOrdinal: 0,
                uniqueKeyOrdinal: 1);
            Assert.True(rows.Count == 1, "The seeded form has no active record with a row on every page.");
            return rows[0];
        }

        // Parses a GlobalRecordId criterion as a client sends it, builds the Find statement over the active set with
        // the given exclusion, runs it, and reads each row's stored id and key and the total every row carries.
        public async Task<IReadOnlyList<FoundRow>> FindByIdAsync(string criterionId, string? excludeGlobalRecordId)
        {
            SearchParseResult parsed = SearchCriteriaParser.Parse(
                DataFields,
                [new SearchCriterion(SearchCriteriaParser.GlobalRecordIdFieldName, FieldValue.FromText(criterionId))]);
            Assert.True(parsed.IsSuccess, "The GlobalRecordId criterion did not parse.");

            SearchSelect select = SearchSqlBuilder.Search(
                Schema,
                DataFields,
                RecordQueryRules.GetGridColumns(DataFields),
                parsed.Conditions,
                includeDeleted: false,
                excludeGlobalRecordId);
            return await SqlExecution.ReadAsync(
                _connection,
                transaction: null,
                Operation,
                select.Statement,
                _commandTimeoutSeconds,
                _observer,
                CancellationToken.None,
                ReadFoundRowsAsync);
        }

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

        private static async Task<IReadOnlyList<FoundRow>> ReadFoundRowsAsync(DbDataReader rows, CancellationToken token)
        {
            var result = new List<FoundRow>();
            while (await rows.ReadAsync(token))
            {
                result.Add(new FoundRow(
                    rows.GetString(SearchSqlBuilder.IdOrdinal),
                    rows.GetInt32(SearchSqlBuilder.UniqueKeyOrdinal),
                    rows.GetInt64(SearchSqlBuilder.TotalOrdinal)));
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
/// cannot reach the API, so it writes nothing; the repository's write proofs are <c>SqlRecordRepositoryWriteTests</c>
/// in <c>EpiInfo.Api.Tests</c>. This class pins the list and Find SQL against the seeded rows alone, so cases that
/// need a stored literal pattern character belong to <c>ListAndSearchTests</c>. Each test holds the shared
/// <see cref="DatabaseWriteLock"/> for its whole body, so the seeded rows its expected values come from cannot move,
/// and ends by asserting that the recorder saw no write.
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

    // The table hint every held read carries, and the plan hints only the list ends with, which make the base table the
    // outer input of each nested-loops page join.
    private const string HoldLockHint = " WITH (HOLDLOCK)";
    private const string ListPlanHints = " OPTION (FORCE ORDER, LOOP JOIN)";

    // The seeded CaseReport text field the Find pattern cases search, and the shortest seeded value they are built
    // from, long enough for every case's slices.
    private const string PatternField = "LastName";
    private const int PatternMinimumLength = 5;

    // A binary collation, under which the set [A-Za-z] is exactly the ASCII letters.
    private const string BinaryCollation = "Latin1_General_BIN2";

    // Fixed operation names reported to the command observer.
    private const string ListPageOperation = "test.listPage";
    private const string FindOperation = "test.find";

    // The selected-set case searches a window of complete records: this many before the first deleted one, and this
    // many in all, which keeps every match under the cap.
    private const int RecordsBeforeFirstDeleted = 25;
    private const int SearchedRecords = 150;

    // The RecStatus of an active record (AAP 0.3.4.6), a smallint in both seeded base tables: the test's own value
    // rather than the production rules' constant.
    private const short ActiveStatus = 1;

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

        // One statement returns both the page and the total of the filtered set.
        var list = form.BuildList(q);
        var listed = await form.ListAsync(list, cancellationToken);
        Assert.Equal(expected.Ids, listed.Ids);
        Assert.Equal(expected.Total, listed.Total);

        var pattern = Assert.Single(list.Parameters, parameter => parameter.DbType == DbType.String);
        Assert.Equal(escapedPattern, Assert.IsType<string>(pattern.Value));

        // Every grid column's term compares with that pattern under the fixed collation and declares ESCAPE '\'.
        var term = $" LIKE {pattern.Name} COLLATE {GridFilterCollation}{GridFilterEscapeClause}";
        Assert.Equal(form.Grid.Count, CountOccurrences(list.Text, term));

        // The count and the page read the filtered set twice in one statement, so every table it reads is held: the
        // base table and each page joined for a grid column. The plan hints that read the base table first, before
        // each of its page rows, end the statement.
        var heldJoins = form.HeldListJoins();
        Assert.NotEmpty(heldJoins);
        Assert.Equal(heldJoins.Count + 1, CountOccurrences(list.Text, HoldLockHint));
        Assert.Contains(
            $"{SqlIdentifier.Quote(form.Schema.BaseTable)} {RecordSqlBuilder.BaseAlias}{HoldLockHint}",
            list.Text,
            StringComparison.Ordinal);
        Assert.All(heldJoins, join => Assert.Contains(join, list.Text, StringComparison.Ordinal));
        Assert.EndsWith(ListPlanHints, list.Text, StringComparison.Ordinal);

        // The builder's statement is a read: the recorder saw it once and no write.
        Assert.Single(form.Recorder.ForOperation(ListPageOperation));
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

    // AAP 0.3.4.4 "Order and cap": each result's position is its ROW_NUMBER() over the selected set by UniqueKey,
    // numbered before the criteria, so it equals the navigation position, and its id, key and status are the base
    // row's. Membership, position and status must describe one committed state, so the Find statement names the base
    // table exactly once, in the numbered set, with HOLDLOCK on that read, and takes every base column it projects or
    // compares from the numbered rows. A behavioural test would soft-delete a seeded row during the statement, a write
    // this assembly may not make (AAP 0.3.7, "Data reach of tests"), so the statement's one held base read stands in
    // for it. The rows are checked against direct SQL that places each record by the legacy formula, the number of
    // selected rows whose UniqueKey is at most its own, over a window of complete records holding deleted ones, so the
    // two sets number some of them differently.
    [Theory]
    [InlineData(CaseReportForm, false)]
    [InlineData(CaseReportForm, true)]
    [InlineData(FoodHistoryForm, false)]
    [InlineData(FoodHistoryForm, true)]
    public async Task Find_SelectedSet_ReadsTheBaseTableOnceAndPlacesRecordsAsDirectSql(string formName, bool includeDeleted)
    {
        await TestDatabase.EnsureReachableAsync();
        using var timeout = new CancellationTokenSource(TestTimeout);
        var cancellationToken = timeout.Token;
        await using var sharedLock = await DatabaseWriteLock.AcquireSharedAsync(cancellationToken);

        var form = await FormUnderTest.LoadAsync(formName, cancellationToken);
        var complete = await form.PlacedCompleteRecordsAsync(cancellationToken);
        var firstDeleted = complete.FindIndex(record => record.RecStatus != ActiveStatus);
        Assert.True(firstDeleted >= 0, $"No deleted {formName} record has a row on every page.");
        var window = complete
            .Skip(Math.Max(0, firstDeleted - RecordsBeforeFirstDeleted))
            .Take(SearchedRecords)
            .ToArray();
        Assert.True(
            window.Any(record =>
                record.RecStatus == ActiveStatus && record.ActivePosition != record.EveryPosition),
            $"The {formName} window holds no active record that the two sets place differently.");

        var selected = window
            .Where(record => includeDeleted || record.RecStatus == ActiveStatus)
            .ToArray();
        var expected = selected
            .Select(record => new PlacedRow(
                record.GlobalRecordId,
                record.UniqueKey,
                record.RecStatus,
                includeDeleted ? record.EveryPosition : record.ActivePosition,
                selected.Length))
            .ToArray();

        var find = form.BuildFind(
            SearchCriteriaParser.GlobalRecordIdFieldName,
            string.Join(" or ", window.Select(record => record.GlobalRecordId)),
            includeDeleted);

        // The base table is named once, in the numbered set, and that read alone carries the hint.
        var baseTable = SqlIdentifier.Quote(form.Schema.BaseTable);
        Assert.Equal(1, CountOccurrences(find.Statement.Text, baseTable));
        Assert.Equal(1, CountOccurrences(find.Statement.Text, "HOLDLOCK"));
        Assert.Contains(
            $"{baseTable} {RecordSqlBuilder.BaseAlias} WITH (HOLDLOCK)",
            find.Statement.Text,
            StringComparison.Ordinal);

        // Find reads each row once, so unlike the list its page joins carry no hint and it forces no join order.
        Assert.DoesNotContain("FORCE ORDER", find.Statement.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("LOOP JOIN", find.Statement.Text, StringComparison.Ordinal);

        Assert.Equal(expected, await form.FindPlacedAsync(find, cancellationToken));

        // The builder's statement is a read: the recorder saw it and no write.
        Assert.NotEmpty(form.Recorder.ForOperation(FindOperation));
        Assert.Empty(form.Recorder.Writes());
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

    /// <summary>The ids of one list page, in the order returned, and the list's total.</summary>
    private sealed record ListRows(IReadOnlyList<string> Ids, long Total);

    /// <summary>The ids a Find returned, in the order returned, and the total of its matches.</summary>
    private sealed record FindRows(IReadOnlyList<string> Ids, long Total);

    /// <summary>
    /// A base row with a row on every page table, as direct SQL reads it: its stored id, key and status, and its
    /// position by the legacy formula in the active set and in the set of every row.
    /// </summary>
    private sealed record PlacedRecord(
        string GlobalRecordId,
        long UniqueKey,
        long RecStatus,
        long ActivePosition,
        long EveryPosition);

    /// <summary>One Find row at its five fixed ordinals: the id, key and status, the position and the total.</summary>
    private sealed record PlacedRow(string GlobalRecordId, long UniqueKey, long RecStatus, long Position, long Total);

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
        /// Builds the one statement of the first list page of the active set, at the largest page size, filtered by
        /// <paramref name="q"/>, which also returns the total of the same filtered set.
        /// </summary>
        public SqlStatement BuildList(string q)
        {
            var query = new RecordListQuery(q, IncludeDeleted: false, RecordQueryRules.DefaultPage, RecordQueryRules.MaxPageSize);
            return SearchSqlBuilder.ListPage(Schema, Grid, query).Statement;
        }

        /// <summary>
        /// The held join the list writes for each page table it joins, the pages with a table that hold a grid column,
        /// in page order: <c>LEFT JOIN [page] p{i} WITH (HOLDLOCK) ON </c>.
        /// </summary>
        public IReadOnlyList<string> HeldListJoins() =>
        [
            .. Grid
                .Select(field => RecordSqlBuilder.PageIndexOf(Schema, field.Page.PageId))
                .Distinct()
                .Order()
                .Where(index => Schema.Pages[index].Table is not null)
                .Select(index =>
                    $"LEFT JOIN {SqlIdentifier.Quote(Schema.Pages[index].Table!)} " +
                    $"{RecordSqlBuilder.PageAlias(index)}{HoldLockHint} ON "),
        ];

        /// <summary>
        /// Runs a list statement and reads the ids of the rows it flags as on the page, in the order returned, and the
        /// total every row carries.
        /// </summary>
        public async Task<ListRows> ListAsync(SqlStatement list, CancellationToken cancellationToken)
        {
            await using var connection = await TestDatabase.OpenConnectionAsync(cancellationToken);
            return await SqlExecution.ReadAsync(
                connection,
                transaction: null,
                ListPageOperation,
                list,
                Options.CommandTimeoutSeconds,
                Recorder,
                cancellationToken,
                ReadListRowsAsync);
        }

        // The ids of the rows a list statement flags as on the page, in the order returned, and the total every row
        // carries; an empty page arrives as one row flagged 0, which carries the total alone.
        private static async Task<ListRows> ReadListRowsAsync(DbDataReader reader, CancellationToken cancellationToken)
        {
            var ids = new List<string>();
            long total = 0;
            while (await reader.ReadAsync(cancellationToken))
            {
                total = reader.GetInt64(SearchSqlBuilder.TotalOrdinal);
                if (reader.GetInt32(SearchSqlBuilder.InPageOrdinal) == 1)
                {
                    ids.Add(reader.GetString(SearchSqlBuilder.IdOrdinal));
                }
            }

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
        /// Parses one criterion with the search criteria parser and builds the Find statement over the selected set of
        /// <paramref name="includeDeleted"/>, the active set by default, excluding no record.
        /// </summary>
        public SearchSelect BuildFind(string fieldName, string criterion, bool includeDeleted = false)
        {
            var parsed = SearchCriteriaParser.Parse(
                DataFields,
                [new SearchCriterion(fieldName, FieldValue.FromText(criterion))]);
            Assert.True(
                parsed.IsSuccess,
                $"The {fieldName} criterion did not parse: {parsed.RequestError} " +
                string.Join(" ", parsed.FieldErrors.Values.SelectMany(messages => messages)));

            return SearchSqlBuilder.Search(
                Schema, DataFields, Grid, parsed.Conditions, includeDeleted, excludeGlobalRecordId: null);
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

        /// <summary>Runs a Find statement and reads every row at its five fixed ordinals, in the order returned.</summary>
        public async Task<IReadOnlyList<PlacedRow>> FindPlacedAsync(SearchSelect find, CancellationToken cancellationToken)
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
                ReadPlacedRowsAsync);
        }

        /// <summary>
        /// Every base row with a row on every page table, in <c>UniqueKey</c> order, read by a direct query: its
        /// stored id, key and status, and its position by the legacy formula, the number of active rows
        /// (<c>RecStatus = 1</c>) and of all rows of the base table alone whose <c>UniqueKey</c> is at most its own.
        /// </summary>
        public async Task<List<PlacedRecord>> PlacedCompleteRecordsAsync(CancellationToken cancellationToken)
        {
            // The all-page join of the Find source is the same whichever field it is built for.
            var (from, _) = FindSource(Grid[0].Name);
            var baseTable = SqlIdentifier.Quote(Schema.BaseTable);
            var baseId = SqlIdentifier.Quote(RecordSqlBuilder.Require(Schema.GlobalRecordId));
            var uniqueKey = SqlIdentifier.Quote(RecordSqlBuilder.Require(Schema.UniqueKey));
            var recStatus = SqlIdentifier.Quote(RecordSqlBuilder.Require(Schema.RecStatus));
            var placed = $"(SELECT COUNT_BIG(*) FROM {baseTable} s WHERE s.{uniqueKey} <= b.{uniqueKey}";
            var sql = $"SELECT b.{baseId}, b.{uniqueKey}, b.{recStatus}, {placed} AND s.{recStatus} = @activeStatus)," +
                $" {placed}){from} ORDER BY b.{uniqueKey};";

            await using var connection = await TestDatabase.OpenConnectionAsync(cancellationToken);
            await using var command = new SqlCommand(sql, connection) { CommandTimeout = Options.CommandTimeoutSeconds };
            command.Parameters.Add(ActiveStatusParameter());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var records = new List<PlacedRecord>();
            while (await reader.ReadAsync(cancellationToken))
            {
                records.Add(new PlacedRecord(
                    reader.GetString(0),
                    Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
                    Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture),
                    reader.GetInt64(3),
                    reader.GetInt64(4)));
            }

            return records;
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

            // The operand is a parameter typed as its column: datetime, float, or bit for a checkbox state.
            var parameters = new List<SqlParameter>();
            switch (operand)
            {
                case DateTime date:
                    parameters.Add(new SqlParameter("@operand", SqlDbType.DateTime) { Value = date });
                    break;
                case double number:
                    parameters.Add(new SqlParameter("@operand", SqlDbType.Float) { Value = number });
                    break;
                case bool flag:
                    parameters.Add(new SqlParameter("@operand", SqlDbType.Bit) { Value = flag });
                    break;
                default:
                    throw new ArgumentException("The operand is not a date, a number or a checkbox state.", nameof(operand));
            }

            return await DirectFindAsync(from, $"{criterionColumn} {comparison} @operand", parameters, cancellationToken);
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
                    parameters.Add(new SqlParameter(name + "Start", SqlDbType.Int) { Value = start });
                    parameters.Add(new SqlParameter(name + "Length", SqlDbType.Int) { Value = text.Length });
                    terms.Add($"SUBSTRING({criterionColumn}, {name}Start, {name}Length) = {name}");
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
                value => (fractionalOnly ? $" AND {value} <> FLOOR({value})" : string.Empty, []),
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
                value => (
                    $" AND LEN({value}) >= @minimumLength" +
                        $" AND PATINDEX(@nonLetterPattern, {value} COLLATE {BinaryCollation}) = 0",
                    [
                        new SqlParameter("@minimumLength", SqlDbType.Int) { Value = minimumLength },
                        new SqlParameter("@nonLetterPattern", SqlDbType.NVarChar, NonLetterPattern.Length)
                        {
                            Value = NonLetterPattern,
                        },
                    ]),
                cancellationToken));

        /// <summary>The number of stored values of a <c>datetime</c> field, in any record, that carry a time of day.</summary>
        public async Task<long> CountWithTimeOfDayAsync(string fieldName, CancellationToken cancellationToken)
        {
            var (table, column) = PageColumn(fieldName);
            var sql = $"SELECT COUNT_BIG(*) FROM {table} WHERE {column} IS NOT NULL" +
                $" AND CAST({column} AS time) <> @midnight;";

            await using var connection = await TestDatabase.OpenConnectionAsync(cancellationToken);
            await using var command = new SqlCommand(sql, connection) { CommandTimeout = Options.CommandTimeoutSeconds };
            command.Parameters.Add(new SqlParameter("@midnight", SqlDbType.Time) { Value = TimeSpan.Zero });
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
        // written on the value's column reference, ties broken by the smaller value. The condition returns its SQL
        // text and the parameters that text names.
        private async Task<object> MostFrequentAsync(
            string fieldName,
            Func<string, (string Text, SqlParameter[] Parameters)> condition,
            CancellationToken cancellationToken)
        {
            var (table, column) = PageColumn(fieldName);
            var value = $"t.{column}";
            var baseId = SqlIdentifier.Quote(RecordSqlBuilder.Require(Schema.GlobalRecordId));
            var recStatus = SqlIdentifier.Quote(RecordSqlBuilder.Require(Schema.RecStatus));
            var (conditionText, conditionParameters) = condition(value);
            var sql = $"SELECT TOP (1) {value} FROM {SqlIdentifier.Quote(Schema.BaseTable)} b" +
                $" INNER JOIN {table} t ON t.{PageKey(fieldName)} = b.{baseId}" +
                $" WHERE b.{recStatus} = @activeStatus AND {value} IS NOT NULL" +
                conditionText +
                $" GROUP BY {value} ORDER BY COUNT_BIG(*) DESC, {value};";

            await using var connection = await TestDatabase.OpenConnectionAsync(cancellationToken);
            await using var command = new SqlCommand(sql, connection) { CommandTimeout = Options.CommandTimeoutSeconds };
            command.Parameters.Add(ActiveStatusParameter());
            command.Parameters.AddRange(conditionParameters);
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
            var where = $" WHERE b.{SqlIdentifier.Quote(RecordSqlBuilder.Require(Schema.RecStatus))} = @activeStatus" +
                $" AND {predicate}";
            var sql = $"SELECT TOP (@cap) b.{baseId}{from}{where}" +
                $" ORDER BY b.{SqlIdentifier.Quote(RecordSqlBuilder.Require(Schema.UniqueKey))};" +
                $" SELECT COUNT_BIG(*){from}{where};";

            var (ids, total) = await ReadIdsAndTotalAsync(
                sql,
                [
                    new SqlParameter("@cap", SqlDbType.Int) { Value = RecordQueryRules.SearchCap },
                    ActiveStatusParameter(),
                    .. parameters,
                ],
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
            var where = $" WHERE b.{SqlIdentifier.Quote(RecordSqlBuilder.Require(Schema.RecStatus))} = @activeStatus" +
                $" AND ({string.Join(" OR ", terms)})";
            var sql = $"SELECT TOP (@pageSize) b.{baseId}{from}{where}" +
                $" ORDER BY b.{SqlIdentifier.Quote(RecordSqlBuilder.Require(Schema.UniqueKey))};" +
                $" SELECT COUNT_BIG(*){from}{where};";

            var (ids, total) = await ReadIdsAndTotalAsync(
                sql,
                [
                    TextParameter("@q", q),
                    new SqlParameter("@pageSize", SqlDbType.Int) { Value = RecordQueryRules.MaxPageSize },
                    ActiveStatusParameter(),
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

        // The pattern of any character that is not an ASCII letter, read exactly under BinaryCollation.
        private const string NonLetterPattern = "%[^A-Za-z]%";

        // The test's active status as @activeStatus of a direct query, typed as the smallint RecStatus column of both
        // seeded base tables.
        private static SqlParameter ActiveStatusParameter() =>
            new("@activeStatus", SqlDbType.SmallInt) { Value = ActiveStatus };

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

        private static async Task<IReadOnlyList<PlacedRow>> ReadPlacedRowsAsync(
            DbDataReader reader,
            CancellationToken cancellationToken)
        {
            var rows = new List<PlacedRow>();
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new PlacedRow(
                    reader.GetString(SearchSqlBuilder.IdOrdinal),
                    Convert.ToInt64(reader.GetValue(SearchSqlBuilder.UniqueKeyOrdinal), CultureInfo.InvariantCulture),
                    Convert.ToInt64(reader.GetValue(SearchSqlBuilder.RecStatusOrdinal), CultureInfo.InvariantCulture),
                    reader.GetInt64(SearchSqlBuilder.PositionOrdinal),
                    reader.GetInt64(SearchSqlBuilder.TotalOrdinal)));
            }

            return rows;
        }
    }
}

/// <summary>
/// Integration tests of the SQL Server bounds of the Find statement the repository runs, against the rehearsal
/// database through <see cref="SqlExecution"/>: a search beyond 2,000 alternatives other than <c>IS NULL</c> binds its
/// values packed and returns exactly the rows the per-alternative statement returns, the statement never exceeds SQL
/// Server's parameter limit, a <c>LIKE</c> pattern or grid filter past SQL Server's 4,000-code-unit pattern limit runs
/// a long-pattern program that returns exactly the rows of native <c>LIKE</c>, a pattern holding a <c>[...]</c> set
/// that no SQL Server construct evaluates is refused before any SQL is built, and the costliest searches the parser
/// admits at its bounds run through the repository within the default command timeout.
/// </summary>
/// <remarks>
/// <para>
/// Criteria are written as a client writes them and parsed by <see cref="SearchCriteriaParser"/> over the form's data
/// fields; a threshold search holding more alternatives than the parser admits in one value or one search is parsed in
/// chunks within its bounds, whose alternatives form the one condition the builder receives, since the builder binds a
/// condition of any size. Their values are read from the database by direct SQL, from seeded active records with a row
/// on every page, so each search matches something whatever rows the database holds. Every record a test creates
/// carries a test marker at the start of <c>LastName</c>, so the samples leave out each record whose <c>LastName</c>
/// starts with <see cref="TestDatabase.MarkerPrefix"/>: records other tests leave behind, such as a <c>LastName</c> at
/// the column's full length, never become a criterion, and a rerun draws the same values. No count is hard-coded: every
/// expected result is another statement's result, the per-alternative statement or the union of per-alternative
/// statements over chunks of the alternatives.
/// </para>
/// <para>
/// The tests only read. Each holds the shared <see cref="DatabaseWriteLock"/>, because it compares the results of
/// several statements that no writer may change in between.
/// </para>
/// <para>
/// No statement may use <c>OPENJSON</c>, which needs database compatibility level 130 or higher: packed values travel
/// in a list every level decodes alike. Every packed statement, and no per-alternative one, ends with the plan hints
/// that make SQL Server decode each packed list once rather than once per row, and every statement runs under the
/// default command timeout, so a plan that decodes a long list per row fails its test by timeout. Run with
/// <c>ConnectionStrings__EpiInfo</c> naming a database below level 130, the class proves the packed statements
/// there; no test changes a database setting.
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

    // The element-token alias every packed list's decoding declares and no per-alternative statement has, and the
    // carrier no statement may use because it needs database compatibility level 130 or higher.
    private const string PackedListMarker = "AS [e]([t])";
    private const string LevelBoundCarrier = "OPENJSON";

    // The plan hints every packed statement ends with, and the start of any query hint, which no per-alternative
    // statement has.
    private const string PackedPlanHints =
        " OPTION (USE HINT ('DISABLE_OPTIMIZER_ROWGOAL', 'FORCE_LEGACY_CARDINALITY_ESTIMATION'))";
    private const string QueryHint = " OPTION (";

    // The length of the equality and LIKE list on GlobalRecordId, five times the scalar budget: a plan that decodes
    // and sorts the list again for every row takes minutes over it at every compatibility level, and one that decodes
    // it once takes a few seconds.
    private const int PerRowDecodingAlternatives = 5 * ScalarParameterBudget;

    // Session options unlike SqlClient's defaults in each respect a decoding could depend on: quoted identifiers, which
    // xml methods need; arithmetic aborts and warnings, and rounding aborts; NULL concatenation; and the language, date
    // format and first weekday, which govern date text. The SELECT reads one option back.
    private const string NonDefaultSessionOptions =
        "SET QUOTED_IDENTIFIER OFF; SET ARITHABORT OFF; SET ANSI_WARNINGS OFF; SET NUMERIC_ROUNDABORT ON;"
        + " SET CONCAT_NULL_YIELDS_NULL OFF; SET LANGUAGE Deutsch; SET DATEFORMAT ydm; SET DATEFIRST 3;"
        + " SELECT CAST(SESSIONPROPERTY('QUOTED_IDENTIFIER') AS int);";

    // SQL Server's LIKE pattern limit in UTF-16 code units: error 8152 refuses a longer nvarchar pattern, so a longer
    // one runs through its long-pattern program. The forced native limits make short patterns long: windows of one
    // element with every seek too long to wrap in '%' (0), one-element seeks (3) and windows of several elements (8).
    private const int NativeLikeLimit = 4000;
    private static readonly int[] ForcedLikeLimits = [0, 3, 8];

    // The collation cases' building blocks: the letters seeded values are sampled for, the ligature U+FB01 that
    // collations may expand to them, and a supplementary character, which SQL_Latin1_General_CP1_CI_AS ignores. Up to
    // CollationSamples values of each sampled column, of at most MaxCollationTextLength characters, are taken.
    private const string LigatureLetters = "fi";
    private const string Ligature = "\uFB01";
    private const string Supplementary = "\uD83D\uDE00";
    private const int CollationSamples = 2;
    private const int MaxCollationTextLength = 100;

    // What a statement running a long-pattern program holds: the first program's CTE and its recursion hint.
    private const string LongPatternCte = "[x0] ([k], [v], [l], [z], [st]) AS (";
    private const string LongPatternHint = " OPTION (MAXRECURSION 0)";

    // What every list statement holds, long filter or not: the table hint of each base- and page-table read, and the
    // plan hints that read a base row before its page rows, which a long filter's statement puts after the recursion
    // hint.
    private const string ListHoldLock = " WITH (HOLDLOCK)";
    private const string ListPlanHints = " OPTION (FORCE ORDER, LOOP JOIN)";
    private const string LongListPlanHints = " OPTION (MAXRECURSION 0, FORCE ORDER, LOOP JOIN)";

    // The page size of the list cases, the largest the API admits.
    private const int ListPageSize = 200;

    // Chunks of the threshold searches, run per alternative to build the expected union.
    private const int ChunkSize = 500;

    // The legacy separator of a criterion's alternatives, as a client writes it.
    private const string OrSeparator = " or ";

    // The shapes of the costliest searches at the parser's bounds: one long pattern holding the whole pattern text,
    // six long patterns sharing it, or the most packed patterns over five fields.
    private const string LongPatternShape = "long pattern";
    private const string SixLongPatternsShape = "six long patterns";
    private const string PackedPatternsShape = "packed patterns";

    // A character no seeded or test-created value holds, which the packed patterns carry so that none matches; the
    // first of the characters, one per packed pattern, that tell the patterns apart; and the pattern each packed
    // condition ends with, which matches every value of at least one character.
    private const string AbsentCharacter = "\u00A7";
    private const char FirstDistinctCharacter = '\u4E00';
    private const string AnyTextPattern = "_%";

    // The field of each form's long pattern: a text column of CaseReport and an ntext column of FoodHistory.
    private static readonly IReadOnlyDictionary<string, string> LongPatternFields = new Dictionary<string, string>
    {
        [CaseReport] = "LastName",
        [FoodHistory] = "BuyLastTenDays",
    };

    // The six text fields of CaseReport, each holding one of the six long patterns, and a text field of each form that
    // holds no long pattern of one field.
    private static readonly string[] SixLongPatternFields =
        ["LastName", "FirstName", "Address", "City", "State", "County"];
    private static readonly IReadOnlyDictionary<string, string> OtherTextFields = new Dictionary<string, string>
    {
        [CaseReport] = "State",
        [FoodHistory] = "State",
    };

    // The five text fields of each form's packed patterns, an ntext field on each page of FoodHistory among them.
    private static readonly IReadOnlyDictionary<string, string[]> PackedPatternFields =
        new Dictionary<string, string[]>
        {
            [CaseReport] = ["State", "LastName", "FirstName", "Address", "City"],
            [FoodHistory] = ["State", "LastName", "FirstName", "PatientAddress", "BuyLastTenDays"],
        };

    // Each packed field's column in the joins of CompleteRecords.
    private static readonly IReadOnlyDictionary<string, string> PackedPatternColumns = new Dictionary<string, string>
    {
        ["State"] = "p1.[State]",
        ["LastName"] = "p1.[LastName]",
        ["FirstName"] = "p1.[FirstName]",
        ["Address"] = "p1.[Address]",
        ["City"] = "p1.[City]",
        ["PatientAddress"] = "p1.[PatientAddress]",
        ["BuyLastTenDays"] = "p2.[BuyLastTenDays]",
    };

    private readonly ITestOutputHelper output;

    public SearchStatementBoundsTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    // The most rows the threshold searches' seeded values may match, so that each chunk's result and the union stay
    // under the 200-row cap and are therefore complete.
    private const int MaxSeededMatches = 120;

    // The RecStatus of an active record (C-16), a value the test owns, and the most rows a sample reads.
    private const short ActiveStatus = 1;
    private const int SampleRows = 40;

    // The seeded active rows of CompleteRecords, as a WHERE clause whose values AddSeededActiveParameters binds: an
    // active record whose LastName, p1 on both forms, is NULL or does not start with the test marker prefix. Every
    // record a test creates starts its LastName with a marker; the one exception, the C-10 record of unchecked
    // checkboxes only, is NULL in every sampled column but GlobalRecordId.
    private const string SeededActiveRecords =
        " WHERE b.[RecStatus] = @activeStatus"
        + " AND (p1.[LastName] IS NULL OR LEFT(p1.[LastName], @markerPrefixLength) <> @markerPrefix)";

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
    // and total, in both selected sets and under session options unlike SqlClient's defaults.
    [Theory]
    [InlineData(CaseReport, false, false)]
    [InlineData(CaseReport, true, false)]
    [InlineData(FoodHistory, false, false)]
    [InlineData(FoodHistory, true, false)]
    [InlineData(CaseReport, false, true)]
    [InlineData(FoodHistory, false, true)]
    public async Task Search_PackedStatement_ReturnsExactlyTheRowsOfTheScalarStatement(
        string formName,
        bool includeDeleted,
        bool nonDefaultSessionOptions)
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        var token = cancellation.Token;
        await using var databaseLock = await DatabaseWriteLock.AcquireSharedAsync(token);
        await using var connection = nonDefaultSessionOptions
            ? await OpenUnpooledConnectionAsync(token)
            : await TestDatabase.OpenConnectionAsync(token);
        var form = await ReadFormAsync(connection, formName, token);

        var cases = formName == CaseReport
            ? await CaseReportCriteriaAsync(connection, token)
            : await FoodHistoryCriteriaAsync(connection, token);

        // Set after the form and the samples are read, so only the Find statements run under them.
        if (nonDefaultSessionOptions)
        {
            await SetNonDefaultSessionOptionsAsync(connection, token);
        }

        foreach (var (label, criteria) in cases)
        {
            var conditions = Parse(form, criteria);
            var scalar = SearchSqlBuilder.Search(
                form.Schema, form.DataFields, form.GridColumns, conditions, includeDeleted, null);
            var packed = SearchSqlBuilder.Search(
                form.Schema, form.DataFields, form.GridColumns, conditions, includeDeleted, null, 0);

            AssertCarrier(scalar, packed: false);
            AssertCarrier(packed, packed: true);

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

        var seeded = await SeededLastNamesAsync(connection, form, token);
        var excluded = await FirstActiveIdAsync(connection, seeded[0], token);
        var alternatives = ThresholdAlternatives(seeded, size);
        var condition = ParseInChunks(form, "LastName", alternatives);
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
            AssertCarrier(bound, packed: false);
        }
        else
        {
            Assert.Equal(packedParameters, bound.Statement.Parameters.Count);
            AssertCarrier(bound, packed: true);
        }

        Assert.Equal(packedParameters, packed.Statement.Parameters.Count);
        AssertCarrier(packed, packed: true);

        var union = new Dictionary<string, SearchRow>(StringComparer.Ordinal);
        foreach (var chunk in condition.Alternatives.Chunk(ChunkSize))
        {
            var part = new SearchCondition(condition.FieldName, condition.Target, condition.FieldType, chunk);
            var select = SearchSqlBuilder.Search(
                form.Schema, form.DataFields, form.GridColumns, [part], includeDeleted: false, excluded);
            AssertCarrier(select, packed: false);

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

    // Equality and LIKE alternatives on the unique GlobalRecordId far past the scalar budget, two of each matching a
    // seeded record and the other patterns nothing: the packed statement, the only one with the plan hints, runs
    // within the default command timeout at the database's compatibility level and returns the union of the
    // per-alternative statements over chunks of the alternatives. The parser gives GlobalRecordId no wildcard path, so
    // the patterns are built directly; they take the packed LIKE path a text field's patterns take, where an
    // unhinted plan, or one under the row goal alone at level 130 and later, decodes the list once per row.
    [Fact]
    public async Task Search_PackedEqualityAndLikeOnGlobalRecordId_DecodesOnceWithinTheDefaultTimeout()
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        var token = cancellation.Token;
        await using var databaseLock = await DatabaseWriteLock.AcquireSharedAsync(token);
        await using var connection = await TestDatabase.OpenConnectionAsync(token);
        var form = await ReadFormAsync(connection, CaseReport, token);

        var ids = await SampleAsync(connection, CaseReport, "b.[GlobalRecordId]", 4, token);
        var parsed = Assert.Single(Parse(form, [Text(SearchCriteriaParser.GlobalRecordIdFieldName, ids[0])]));
        SearchAlternative Prefix(string id) =>
            new(SearchOperator.Like, FieldValue.FromText(id[..Math.Max(1, id.Length / 2)] + "%"));

        var alternatives = new List<SearchAlternative>(PerRowDecodingAlternatives)
        {
            new(SearchOperator.Equal, FieldValue.FromText(ids[0])),
            Prefix(ids[1]),
            new(SearchOperator.Equal, FieldValue.FromText(ids[2])),
            Prefix(ids[3]),
        };
        while (alternatives.Count < PerRowDecodingAlternatives)
        {
            alternatives.Add(new(SearchOperator.Like, FieldValue.FromText(NoMatch(alternatives.Count) + "%")));
        }

        var condition = new SearchCondition(parsed.FieldName, parsed.Target, parsed.FieldType, alternatives);
        var packed = SearchSqlBuilder.Search(
            form.Schema, form.DataFields, form.GridColumns, [condition], includeDeleted: false, null);
        AssertCarrier(packed, packed: true);
        Assert.Equal(FixedActiveSetParameters + 2, packed.Statement.Parameters.Count);

        var union = new Dictionary<string, SearchRow>(StringComparer.Ordinal);
        foreach (var chunk in alternatives.Chunk(ChunkSize))
        {
            var part = new SearchCondition(condition.FieldName, condition.Target, condition.FieldType, chunk);
            var select = SearchSqlBuilder.Search(
                form.Schema, form.DataFields, form.GridColumns, [part], includeDeleted: false, null);
            AssertCarrier(select, packed: false);

            var rows = await RunAsync(connection, select, token);
            Assert.All(rows, row => Assert.Equal(rows.Count, row.Total));
            foreach (var row in rows)
            {
                union[row.Id] = row;
            }
        }

        Assert.Superset(ids.ToHashSet(StringComparer.Ordinal), union.Keys.ToHashSet(StringComparer.Ordinal));
        Assert.True(union.Count <= RecordQueryRules.SearchCap, "The seeded ids match more rows than one page.");
        var expected = union.Values
            .OrderBy(row => row.UniqueKey)
            .Select(row => row with { Total = union.Count })
            .ToArray();

        Assert.Equal(expected, await RunAsync(connection, packed, token));
    }

    // A packed list decodes every element at the lengths where the digit tables that number its elements grow: an
    // equality list whose first and last elements are two seeded values, each matching complete active records, and
    // whose others match nothing, returns exactly the rows of its per-alternative statement.
    [Theory]
    [InlineData(2)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(100)]
    [InlineData(101)]
    [InlineData(1000)]
    [InlineData(1001)]
    public async Task Search_PackedListOfAnyLength_DecodesItsFirstAndLastElements(int length)
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        var token = cancellation.Token;
        await using var databaseLock = await DatabaseWriteLock.AcquireSharedAsync(token);
        await using var connection = await TestDatabase.OpenConnectionAsync(token);
        var form = await ReadFormAsync(connection, CaseReport, token);
        var field = Assert.Single(form.DataFields, candidate => candidate.Name == "LastName");
        var seeded = await SeededLastNamesAsync(connection, form, token);
        Assert.True(seeded.Count >= 2, "The database holds too few seeded LastName values.");

        var condition = new SearchCondition(
            field.Name,
            SearchTarget.PageField,
            field.FieldType,
            [.. Enumerable.Range(0, length).Select(index => new SearchAlternative(
                SearchOperator.Equal,
                FieldValue.FromText(index == 0 ? seeded[0] : index == length - 1 ? seeded[1] : NoMatch(index))))]);

        var scalar = SearchSqlBuilder.Search(form.Schema, form.DataFields, form.GridColumns, [condition], false, null);
        var packed = SearchSqlBuilder.Search(
            form.Schema, form.DataFields, form.GridColumns, [condition], false, null, 0);
        AssertCarrier(scalar, packed: false);
        AssertCarrier(packed, packed: true);
        Assert.Equal(FixedActiveSetParameters + 1, packed.Statement.Parameters.Count);

        var expected = await RunAsync(connection, scalar, token);
        Assert.NotEmpty(expected);
        Assert.Equal(expected, await RunAsync(connection, packed, token));
    }

    // SQL Server refuses an nvarchar LIKE pattern of more than 4,000 code units (error 8152): a pattern of 4,000 '%' is
    // one native LIKE and one of 4,001 or 8,001 '%' runs through its program, per alternative and packed, on an
    // nvarchar and an ntext column, and each matches exactly what '%' matches.
    [Theory]
    [InlineData(CaseReport, "LastName")]
    [InlineData(FoodHistory, "BuyLastTenDays")]
    public async Task Search_PercentPatternOfAnyLength_MatchesWhatOnePercentMatches(string formName, string fieldName)
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        var token = cancellation.Token;
        await using var databaseLock = await DatabaseWriteLock.AcquireSharedAsync(token);
        await using var connection = await TestDatabase.OpenConnectionAsync(token);
        var form = await ReadFormAsync(connection, formName, token);
        var field = Assert.Single(form.DataFields, candidate => candidate.Name == fieldName);

        var collapsed = await RunAsync(
            connection,
            SearchSqlBuilder.Search(
                form.Schema, form.DataFields, form.GridColumns, [LikeCondition(field, "%")], false, null),
            token);
        Assert.NotEmpty(collapsed);

        foreach (var (length, longPattern) in new[] { (NativeLikeLimit, false), (4001, true), (8001, true) })
        {
            foreach (var budget in new[] { ScalarParameterBudget, 0 })
            {
                var select = SearchSqlBuilder.Search(
                    form.Schema,
                    form.DataFields,
                    form.GridColumns,
                    [LikeCondition(field, new string('%', length))],
                    false,
                    null,
                    budget);

                AssertProgram(select.Statement, longPattern);
                Assert.Equal(collapsed, await RunAsync(connection, select, token));
            }
        }
    }

    // At the default limit, a pattern with a set, which the parser never collapses, and runs of '%' that take it past
    // 4,000 code units runs through its program and returns exactly the rows of the native LIKE of the same pattern
    // with each run written as one '%', which matches the same text. The patterns are built from stored values of a
    // text column and an ntext column, so they match rows.
    [Theory]
    [InlineData(CaseReport, "LastName", "p1.[LastName]")]
    [InlineData(FoodHistory, "BuyLastTenDays", "CAST(p2.[BuyLastTenDays] AS nvarchar(max))")]
    public async Task Search_PatternLongerThanTheLimit_ReturnsTheRowsOfItsCollapsedForm(
        string formName,
        string fieldName,
        string expression)
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        var token = cancellation.Token;
        await using var databaseLock = await DatabaseWriteLock.AcquireSharedAsync(token);
        await using var connection = await TestDatabase.OpenConnectionAsync(token);
        var form = await ReadFormAsync(connection, formName, token);
        var field = Assert.Single(form.DataFields, candidate => candidate.Name == fieldName);
        var value = (await SampleAsync(connection, formName, expression, 1, token))[0];
        Assert.True(value.Length >= 3, $"{formName}.{fieldName}: the sampled value is too short to build patterns.");

        var run = new string('%', NativeLikeLimit);
        var first = "[" + value[..1] + "]";
        var last = "[" + value[^1..] + "]";
        (string Long, string Collapsed)[] pairs =
        [
            ("%" + first + run + value[1..], "%" + first + "%" + value[1..]),
            (value[..1] + run + last, value[..1] + "%" + last),
            (run + first + value[1..^1] + run, "%" + first + value[1..^1] + "%"),
            ("_" + run + "[^]" + run + last, "_%[^]%" + last),
            (first + run + "_" + run, first + "%_%"),
        ];

        var matched = 0;
        foreach (var (longPattern, collapsed) in pairs)
        {
            Assert.True(longPattern.Length > NativeLikeLimit);
            var native = SearchSqlBuilder.Search(
                form.Schema, form.DataFields, form.GridColumns, [LikeCondition(field, collapsed)], true, null);
            var program = SearchSqlBuilder.Search(
                form.Schema, form.DataFields, form.GridColumns, [LikeCondition(field, longPattern)], true, null);
            AssertProgram(native.Statement, longPattern: false);
            AssertProgram(program.Statement, longPattern: true);

            var expected = await RunAsync(connection, native, token);
            matched += expected.Count > 0 ? 1 : 0;
            Assert.Equal(expected, await RunAsync(connection, program, token));
        }

        Assert.True(matched >= 3, $"{formName}.{fieldName}: only {matched} patterns match a seeded row.");
    }

    // Differential proof of the long-pattern program: the same pattern rendered as native LIKE and, with the native
    // limit forced low, through the program, so short patterns exercise every instruction, return exactly the same
    // rows, positions and totals on the seeded rows, for a text column and an ntext column. The corpus covers
    // literals, '_', sets (an empty set, a negated empty set, an unclosed set, ranges, a hyphen after a range and
    // members '%', '_' and '['), runs of '%', leading, trailing and inner '%', several segments, trailing blanks and
    // letter case, and patterns built from stored values, so each matches rows; the forced limits give windows of one
    // element and seeks too long to wrap (0), single-element seeks (3) and windows of several elements (8).
    [Theory]
    [InlineData(CaseReport, "LastName", "p1.[LastName]")]
    [InlineData(FoodHistory, "BuyLastTenDays", "CAST(p2.[BuyLastTenDays] AS nvarchar(max))")]
    public async Task Search_LongPatternProgram_ReturnsExactlyTheRowsOfNativeLike(
        string formName,
        string fieldName,
        string expression)
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        var token = cancellation.Token;
        await using var databaseLock = await DatabaseWriteLock.AcquireSharedAsync(token);
        await using var connection = await TestDatabase.OpenConnectionAsync(token);
        var form = await ReadFormAsync(connection, formName, token);
        var field = Assert.Single(form.DataFields, candidate => candidate.Name == fieldName);
        var corpus = PatternCorpus(await SampleAsync(connection, formName, expression, 3, token));

        var matched = 0;
        for (var index = 0; index < corpus.Count; index++)
        {
            var condition = LikeCondition(field, corpus[index]);
            var native = SearchSqlBuilder.Search(
                form.Schema, form.DataFields, form.GridColumns, [condition], true, null);
            AssertProgram(native.Statement, longPattern: false);
            var expected = await RunAsync(connection, native, token);
            matched += expected.Count > 0 ? 1 : 0;

            foreach (var limit in ForcedLikeLimits)
            {
                var program = SearchSqlBuilder.Search(
                    form.Schema,
                    form.DataFields,
                    form.GridColumns,
                    [condition],
                    true,
                    null,
                    ScalarParameterBudget,
                    limit);
                AssertProgram(program.Statement, longPattern: corpus[index].Length > limit);

                // The program replaces the pattern's one parameter, however long the pattern is.
                Assert.Equal(native.Statement.Parameters.Count, program.Statement.Parameters.Count);
                Assert.True(
                    expected.SequenceEqual(await RunAsync(connection, program, token)),
                    $"{formName}.{fieldName}: corpus pattern {index} at limit {limit} differs from native LIKE.");
            }
        }

        Assert.True(matched > corpus.Count / 2, $"Only {matched} of {corpus.Count} patterns match a seeded row.");
    }

    // A search mixing native and long patterns in one condition, long patterns in two conditions, packed or not, has
    // one program per condition and returns the rows of the all-native statement.
    [Theory]
    [InlineData(ScalarParameterBudget)]
    [InlineData(0)]
    public async Task Search_NativeAndLongPatternsAcrossConditions_ReturnTheRowsOfNativeLike(int budget)
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        var token = cancellation.Token;
        await using var databaseLock = await DatabaseWriteLock.AcquireSharedAsync(token);
        await using var connection = await TestDatabase.OpenConnectionAsync(token);
        var form = await ReadFormAsync(connection, CaseReport, token);
        var lastName = Assert.Single(form.DataFields, candidate => candidate.Name == "LastName");
        var firstName = Assert.Single(form.DataFields, candidate => candidate.Name == "FirstName");
        var lastNames = await SampleAsync(connection, CaseReport, "p1.[LastName]", 3, token);
        var anchor = await AnchorAsync(connection, CaseReport, ["p1.[LastName]", "p1.[FirstName]"], token);

        SearchCondition[] conditions =
        [
            new(lastName.Name, SearchTarget.PageField, lastName.FieldType,
            [
                new SearchAlternative(SearchOperator.Like, FieldValue.FromText(lastNames[0][..1] + "%")),
                new SearchAlternative(SearchOperator.Equal, FieldValue.FromText(lastNames[1])),
                new SearchAlternative(SearchOperator.Like, FieldValue.FromText("%" + lastNames[2][1..] + "%")),
                new SearchAlternative(SearchOperator.Like, FieldValue.FromText(anchor[0] + "%")),
                SearchAlternative.Null,
            ]),
            new(firstName.Name, SearchTarget.PageField, firstName.FieldType,
            [
                new SearchAlternative(SearchOperator.Like, FieldValue.FromText("%" + anchor[1][^2..])),
                new SearchAlternative(SearchOperator.Like, FieldValue.FromText("_%")),
            ]),
        ];

        var native = SearchSqlBuilder.Search(
            form.Schema, form.DataFields, form.GridColumns, conditions, false, null, budget);
        AssertProgram(native.Statement, longPattern: false);
        var expected = await RunAsync(connection, native, token);
        Assert.NotEmpty(expected);

        // Limit 2 keeps the patterns of up to two characters native and makes every longer one a long pattern, so each
        // condition mixes native and long patterns.
        var mixed = SearchSqlBuilder.Search(
            form.Schema, form.DataFields, form.GridColumns, conditions, false, null, budget, 2);
        AssertProgram(mixed.Statement, longPattern: true);
        Assert.Contains("[x0]", mixed.Statement.Text, StringComparison.Ordinal);
        Assert.Contains("[x1]", mixed.Statement.Text, StringComparison.Ordinal);
        Assert.Equal(budget == 0, mixed.Statement.Text.Contains(PackedListMarker, StringComparison.Ordinal));
        Assert.EndsWith(
            budget == 0 ? " OPTION (MAXRECURSION 0, " + PackedPlanHints[" OPTION (".Length..] : LongPatternHint,
            mixed.Statement.Text,
            StringComparison.Ordinal);
        Assert.Equal(expected, await RunAsync(connection, mixed, token));
    }

    // The grid q: a filter matched through the program, with the native limit forced low, lists exactly the page and
    // the total of the native contains predicate, on both forms and both selected sets, for filters built from stored
    // grid values, in upper case too, which each match, and filters of the pattern and escape characters and a
    // supplementary character, which must match literally. LastName is a grid column of both forms.
    [Theory]
    [InlineData(CaseReport, false)]
    [InlineData(CaseReport, true)]
    [InlineData(FoodHistory, true)]
    public async Task List_LongFilterProgram_ListsExactlyThePageAndTotalOfTheNativeFilter(
        string formName,
        bool includeDeleted)
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        var token = cancellation.Token;
        await using var databaseLock = await DatabaseWriteLock.AcquireSharedAsync(token);
        await using var connection = await TestDatabase.OpenConnectionAsync(token);
        var form = await ReadFormAsync(connection, formName, token);
        var lastNames = await SampleAsync(connection, formName, "p1.[LastName]", 2, token);
        Assert.True(lastNames[0].Length >= 3, $"{formName}: the sampled LastName is too short for an inner filter.");

        // The filters built from stored values come first.
        const int storedFilters = 3;
        string[] filters =
        [
            lastNames[0],
            lastNames[1].ToUpperInvariant(),
            lastNames[0][1..^1],
            "a",
            "e n",
            "-0",
            "20",
            "%",
            "_",
            "[",
            "]",
            "\\",
            "^",
            "a%",
            "[a]",
            "e_",
            "T-",
            "a\uD83D\uDE00",
        ];

        for (var index = 0; index < filters.Length; index++)
        {
            var filter = filters[index];
            var query = new RecordListQuery(filter, includeDeleted, 1, ListPageSize);
            var native = SearchSqlBuilder.ListPage(form.Schema, form.GridColumns, query);
            AssertProgram(native.Statement, longPattern: false);
            AssertHeldList(form, native.Statement, longPattern: false);
            var (expectedIds, expectedTotal) = await ListRowsAsync(connection, native.Statement, token);
            Assert.True(
                index >= storedFilters || expectedTotal > 0,
                $"{formName}: the stored-value filter {index} matches no row.");

            // The native contains pattern: the filter between two '%', each %, _, [ and \ escaped by one more unit.
            var containsLength = filter.Length + 2 + filter.Count(character => character is '%' or '_' or '[' or '\\');
            foreach (var limit in ForcedLikeLimits)
            {
                var page = SearchSqlBuilder.ListPage(form.Schema, form.GridColumns, query, limit);
                AssertProgram(page.Statement, longPattern: containsLength > limit);
                AssertHeldList(form, page.Statement, longPattern: containsLength > limit);
                var (ids, total) = await ListRowsAsync(connection, page.Statement, token);
                Assert.Equal(expectedIds, ids);
                Assert.Equal(expectedTotal, total);
            }
        }
    }

    // A [...] set longer than SQL Server's 4,000-unit pattern limit is cut into pieces that keep its members and its
    // negation, so a pattern holding one returns exactly the rows of the native LIKE of the same pattern with each
    // distinct member written once: a sampled value's leading characters and a set of its last character repeated past
    // the limit, which finds its record, and of a character no value holds, which does not; their negations; distinct
    // letters and ranges repeated, at the start, in the middle and at the end; and a set of exactly the limit's length,
    // which no PATINDEX wraps.
    [Fact]
    public async Task Search_SetLongerThanTheLimit_ReturnsTheRowsOfItsDeduplicatedSet()
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        var token = cancellation.Token;
        await using var databaseLock = await DatabaseWriteLock.AcquireSharedAsync(token);
        await using var connection = await TestDatabase.OpenConnectionAsync(token);
        var form = await ReadFormAsync(connection, CaseReport, token);
        var field = Assert.Single(form.DataFields, candidate => candidate.Name == "LastName");
        var value = (await SampleAsync(connection, CaseReport, "p1.[LastName]", 1, token))[0];
        Assert.True(value.Length >= 2, "The sampled LastName is too short for a leading run and a set.");
        var id = await FirstActiveIdAsync(connection, value, token);
        var head = value[..^1];
        var last = value[^1..];
        const string Absent = "\u00A7";
        static string Repeated(string members, int count) => string.Concat(Enumerable.Repeat(members, count));
        var atLimit = "[" + Repeated("abcde", NativeLikeLimit / 5)[..(NativeLikeLimit - 2)] + "]";
        Assert.Equal(NativeLikeLimit, atLimit.Length);

        // Each long pattern, its native equivalent, and whether it must find the sampled record, or null when either
        // outcome is the oracle's alone.
        (string Long, string Native, bool? FindsSample)[] cases =
        [
            (head + "[" + Repeated(last, 4096) + "]%", head + "[" + last + "]%", true),
            (head + "[" + Repeated(Absent, 4096) + "]%", head + "[" + Absent + "]%", false),
            (head + "[^" + Repeated(Absent, 4096) + "]%", head + "[^" + Absent + "]%", true),
            (head + "[^" + Repeated(last, 4096) + "]%", head + "[^" + last + "]%", false),
            ("[" + Repeated("abcde", 820) + "]%", "[abcde]%", null),
            ("%[" + Repeated("abcde", 820) + "]%", "%[abcde]%", true),
            ("%[" + Repeated("abcde", 820) + "]", "%[abcde]", null),
            ("%[^" + Repeated("abcde", 820) + "]", "%[^abcde]", null),
            ("%[" + Repeated("a-e0-9", 700) + "]%", "%[a-e0-9]%", true),
            ("%" + atLimit + "%", "%[abcde]%", true),
            (atLimit + "%", "[abcde]%", null),
            ("%" + atLimit, "%[abcde]", null),
        ];
        foreach (var (longPattern, nativePattern, findsSample) in cases)
        {
            Assert.True(longPattern.Length > NativeLikeLimit);
            var native = SearchSqlBuilder.Search(
                form.Schema, form.DataFields, form.GridColumns, [LikeCondition(field, nativePattern)], false, null);
            var program = SearchSqlBuilder.Search(
                form.Schema, form.DataFields, form.GridColumns, [LikeCondition(field, longPattern)], false, null);
            AssertProgram(native.Statement, longPattern: false);
            AssertProgram(program.Statement, longPattern: true);

            var expected = await RunAsync(connection, native, token);
            if (findsSample is { } finds)
            {
                Assert.True(
                    finds == expected.Any(row => row.Id == id),
                    $"The native pattern {nativePattern} does not select the sampled record as the case expects.");
            }

            Assert.True(
                expected.SequenceEqual(await RunAsync(connection, program, token)),
                $"The long form of {nativePattern} differs from its native LIKE.");
        }
    }

    // The one pattern no SQL Server construct can evaluate, one holding a set longer than the limit that is unclosed,
    // whose meaning depends on the collation, or whose members have no cut that keeps them and its negation (a chain
    // of ranges, a run of combining marks, a negated set whose members start with '^'), is refused before any SQL is
    // built, at the default limit and the forced ones, as the search-criteria parser refuses it with a 400.
    [Theory]
    [InlineData("unclosed")]
    [InlineData("ranges")]
    [InlineData("combining")]
    [InlineData("caret")]
    public async Task Search_UnsearchableSet_ThrowsBeforeSql(string kind)
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        var token = cancellation.Token;
        await using var databaseLock = await DatabaseWriteLock.AcquireSharedAsync(token);
        await using var connection = await TestDatabase.OpenConnectionAsync(token);
        var form = await ReadFormAsync(connection, CaseReport, token);
        var field = Assert.Single(form.DataFields, candidate => candidate.Name == "LastName");
        var pattern = kind switch
        {
            "unclosed" => "%[" + new string('a', NativeLikeLimit + 1),
            "ranges" => "%[" + string.Concat(Enumerable.Repeat("a-", 2001)) + "a]%",
            "combining" => "%[e" + new string('\u0301', NativeLikeLimit + 1) + "]%",
            "caret" => "%[^^" + string.Concat(Enumerable.Repeat("ab", 2001)) + "]%",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown set kind."),
        };
        Assert.True(LikePatternSyntax.HoldsUnsearchableSet(pattern));

        foreach (var limit in ForcedLikeLimits.Append(NativeLikeLimit))
        {
            Assert.Throws<ArgumentException>(
                "pattern",
                () => SearchSqlBuilder.Search(
                    form.Schema,
                    form.DataFields,
                    form.GridColumns,
                    [LikeCondition(field, pattern)],
                    false,
                    null,
                    ScalarParameterBudget,
                    limit));
        }
    }

    // Differential proof of the long-pattern program on patterns whose matching turns on the collation, over seeded
    // values alone, so the test creates no record. The values are those of a text column of the seeded active records
    // that hold "fi" in either case, and the patterns built from each carry the collation-sensitive part: the ligature
    // U+FB01 in place of "fi", with either end open, inside and in either case; '_' in place of it, beside it and for
    // either letter; sets of it, of its first letter and negated; NUL, U+00AD and a supplementary character inside the
    // literal, which a collation may ignore; a combining acute appended or in place of a letter and accented letters,
    // which no seeded value holds; and tails led by a set, which past a forced limit run backwards from the value's
    // end. With the native limit forced low, each returns exactly the rows, positions and totals of native LIKE under
    // the database collation, so an expansion is never cut between steps and an ignorable character is skipped as LIKE
    // skips it, and more than half of the patterns match a seeded record, so the comparisons are not of empty results.
    [Theory]
    [InlineData("LastName", "p1.[LastName]")]
    [InlineData("City", "p1.[City]")]
    public async Task Search_LongPatternProgram_MatchesCollationSensitivePatternsAsNativeLike(
        string fieldName,
        string expression)
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        var token = cancellation.Token;
        await using var databaseLock = await DatabaseWriteLock.AcquireSharedAsync(token);
        await using var connection = await TestDatabase.OpenConnectionAsync(token);
        var form = await ReadFormAsync(connection, CaseReport, token);
        var field = Assert.Single(form.DataFields, candidate => candidate.Name == fieldName);
        var seeded = await SeededActiveIdsAsync(connection, token);
        var corpus = (await SeededTextsHoldingAsync(connection, [expression], LigatureLetters, token))
            .SelectMany(CollationCorpus)
            .ToList();

        var matched = 0;
        for (var index = 0; index < corpus.Count; index++)
        {
            var condition = LikeCondition(field, corpus[index]);
            var native = SearchSqlBuilder.Search(
                form.Schema, form.DataFields, form.GridColumns, [condition], true, null);
            AssertProgram(native.Statement, longPattern: false);
            var expected = await RunAsync(connection, native, token);
            matched += expected.Any(row => seeded.Contains(row.Id)) ? 1 : 0;

            foreach (var limit in ForcedLikeLimits)
            {
                var program = SearchSqlBuilder.Search(
                    form.Schema,
                    form.DataFields,
                    form.GridColumns,
                    [condition],
                    true,
                    null,
                    ScalarParameterBudget,
                    limit);
                AssertProgram(program.Statement, longPattern: corpus[index].Length > limit);
                Assert.True(
                    expected.SequenceEqual(await RunAsync(connection, program, token)),
                    $"{fieldName}: collation pattern {index} at limit {limit} differs from native LIKE.");
            }
        }

        Assert.True(
            matched > corpus.Count / 2,
            $"{fieldName}: only {matched} of {corpus.Count} patterns match a seeded record.");
    }

    // The grid q over seeded values alone, so the test creates no record: filters built from the seeded grid texts of
    // LastName and City that hold "fi" in either case. Matched through the program with the native limit forced low,
    // each lists exactly the page and the total of the native contains predicate, under Latin1_General_100_CI_AS
    // whatever the database collation. The ligature U+FB01 alone and in place of "fi", with the text before or after
    // it and in either case, lists a record, since that collation expands it, so the expansion is exercised; a
    // combining acute appended or in place of a letter, or an accented letter, lists none; and NUL, U+00AD and a
    // supplementary character inside the literal, or a set of the ligature written literally, are the collation's
    // alone.
    [Fact]
    public async Task List_LongFilterProgram_MatchesCollationSensitivePatternsAsTheNativeFilter()
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        var token = cancellation.Token;
        await using var databaseLock = await DatabaseWriteLock.AcquireSharedAsync(token);
        await using var connection = await TestDatabase.OpenConnectionAsync(token);
        var form = await ReadFormAsync(connection, CaseReport, token);
        var texts = await SeededTextsHoldingAsync(
            connection, ["p1.[LastName]", "p1.[City]"], LigatureLetters, token);

        // Each filter with whether its native total must be above zero or zero, or null when the collation alone
        // decides.
        List<(string Filter, bool? Lists)> filters = [(Ligature, true), ("[" + Ligature + "]", null)];
        filters.AddRange(texts.SelectMany(CollationFilters));
        for (var index = 0; index < filters.Count; index++)
        {
            var (filter, lists) = filters[index];
            var query = new RecordListQuery(filter, false, 1, ListPageSize);
            var native = SearchSqlBuilder.ListPage(form.Schema, form.GridColumns, query);
            AssertProgram(native.Statement, longPattern: false);
            AssertHeldList(form, native.Statement, longPattern: false);
            var (expectedIds, expectedTotal) = await ListRowsAsync(connection, native.Statement, token);
            Assert.True(
                lists is not { } listsAny || listsAny == expectedTotal > 0,
                $"The native filter {index} lists {expectedTotal} records, not {(lists == true ? "some" : "none")}.");

            // The native contains pattern: the filter between two '%', each %, _, [ and \ escaped by one more unit.
            var containsLength = filter.Length + 2 + filter.Count(character => character is '%' or '_' or '[' or '\\');
            foreach (var limit in ForcedLikeLimits)
            {
                var page = SearchSqlBuilder.ListPage(form.Schema, form.GridColumns, query, limit);
                AssertProgram(page.Statement, longPattern: containsLength > limit);
                AssertHeldList(form, page.Statement, longPattern: containsLength > limit);
                var (ids, total) = await ListRowsAsync(connection, page.Statement, token);
                Assert.Equal(expectedIds, ids);
                Assert.Equal(expectedTotal, total);
            }
        }
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

    // The costliest searches the parser admits, written as a client writes them at exactly its bounds, parse and run
    // through the repository, its Find admission included, within the default 30-second command timeout, and one more
    // pattern or code unit is the parser's one message for the bound it passes:
    // - one '%_%_…%' pattern holding the whole 32,767 code units of pattern text one search admits, on a text column
    //   and on an ntext column, and six such patterns sharing those units on the six text fields of CaseReport. They
    //   run through the long-pattern program, whose compilation grows with the pattern text, the cost that bound caps;
    // - the 6,554 alternatives other than equality and IS NULL one search admits, as '%§x%' patterns of four code
    //   units, x a character that differs between them, spread over five text fields of the form's pages, an ntext
    //   field on each page of FoodHistory among them. '§' is a character no value holds, and each condition ends in
    //   '_%', which sorts after every pattern led by '%' under the database collation and matches every value of at
    //   least one character, so every condition is packed and scans its whole list for every row it reads, and the
    //   search matches exactly the complete active records whose five columns each hold a character, which direct SQL
    //   counts.
    [Theory]
    [InlineData(CaseReport, LongPatternShape)]
    [InlineData(FoodHistory, LongPatternShape)]
    [InlineData(CaseReport, SixLongPatternsShape)]
    [InlineData(CaseReport, PackedPatternsShape)]
    [InlineData(FoodHistory, PackedPatternsShape)]
    public async Task Search_CostliestCriteriaAtTheBounds_RunWithinTheDefaultCommandTimeout(
        string formName,
        string shape)
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        var token = cancellation.Token;
        await using var databaseLock = await DatabaseWriteLock.AcquireSharedAsync(token);
        await using var connection = await TestDatabase.OpenConnectionAsync(token);

        var options = new DatabaseOptions(TestDatabase.ConnectionString);
        Assert.Equal(DatabaseOptions.DefaultCommandTimeoutSeconds, options.CommandTimeoutSeconds);
        var connections = new SqlConnectionFactory(options);
        var recorder = new SqlCommandRecorder();
        var reader = new SqlMetadataReader(connections, options, recorder);
        var repository = new SqlRecordRepository(connections, reader, options, recorder);
        var summary = Assert.Single(await reader.ListFormsAsync(token), form => form.Name == formName);
        var metadata = await reader.GetFormMetadataAsync(summary.ViewId, token);
        Assert.NotNull(metadata);
        var dataFields = FormDefinitionFactory.GetDataFields(metadata);

        string[] fields = shape switch
        {
            LongPatternShape => [LongPatternFields[formName]],
            SixLongPatternsShape => SixLongPatternFields,
            _ => PackedPatternFields[formName],
        };
        var (criteria, overBound, expectedMessage) = shape == PackedPatternsShape
            ? PackedPatternCriteria(fields)
            : LongPatternCriteria(fields, OtherTextFields[formName]);

        var refused = SearchCriteriaParser.Parse(dataFields, overBound);
        var (key, messages) = Assert.Single(refused.FieldErrors);
        Assert.Equal(SearchCriteriaParser.CriteriaKey, key);
        Assert.Equal(new[] { expectedMessage }, messages);

        var parsed = SearchCriteriaParser.Parse(dataFields, criteria);
        Assert.True(parsed.IsSuccess, "The criteria at the bounds did not parse.");
        Assert.Equal(fields, parsed.Conditions.Select(condition => condition.FieldName));
        var alternatives = parsed.Conditions.SelectMany(condition => condition.Alternatives).ToArray();
        Assert.All(alternatives, alternative => Assert.Equal(SearchOperator.Like, alternative.Operator));
        if (shape == PackedPatternsShape)
        {
            Assert.Equal(RecordQueryRules.MaxRowComparedAlternatives, alternatives.Length);
        }
        else
        {
            Assert.Equal(
                RecordQueryRules.MaxSearchPatternTextLength,
                alternatives.Sum(alternative => alternative.Value!.Value.AsText().Length));
        }

        recorder.Clear();
        var clock = Stopwatch.StartNew();
        var response = await repository.SearchAsync(metadata, parsed.Conditions, false, null, token);
        clock.Stop();
        output.WriteLine($"{formName} {shape}: {clock.Elapsed.TotalSeconds:F2} s, {response.Total} matches.");

        Assert.True(
            clock.Elapsed < TimeSpan.FromSeconds(DatabaseOptions.DefaultCommandTimeoutSeconds),
            $"{formName} {shape} took {clock.Elapsed.TotalSeconds:F2} s.");
        var find = Assert.Single(recorder.ForOperation(RecordOperations.Search));
        Assert.Null(find.SqlErrorNumber);
        if (shape == PackedPatternsShape)
        {
            Assert.Contains(PackedListMarker, find.CommandText, StringComparison.Ordinal);
            Assert.True(
                alternatives.Length > ScalarParameterBudget,
                "The patterns at the bound do not take the packed path.");
            var expected = await CompleteActiveRecordsHoldingTextAsync(
                connection, formName, fields.Select(field => PackedPatternColumns[field]).ToArray(), token);
            Assert.True(expected > 0, $"{formName}: no complete active record holds text in all five columns.");
            Assert.Equal(expected, response.Total);
        }
        else
        {
            Assert.Contains(LongPatternCte, find.CommandText, StringComparison.Ordinal);
            Assert.Equal(Math.Min(response.Total, RecordQueryRules.SearchCap), response.Items.Count);
        }
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

    // The packed criteria at exactly the bound of alternatives other than equality and IS NULL, as a client writes
    // them: an equal share of the 6,554 for each field, the first fields taking the remainder, as distinct '%§x%'
    // patterns of four code units before the closing '_%'; their pattern text, 26,206 units over five fields, stays
    // within its own bound. Over the bound, the last field holds one pattern more.
    private static (SearchCriterion[] Criteria, SearchCriterion[] OverBound, string Message) PackedPatternCriteria(
        IReadOnlyList<string> fields)
    {
        var values = new string[fields.Count];
        var next = 0;
        for (var index = 0; index < fields.Count; index++)
        {
            var count = (RecordQueryRules.MaxRowComparedAlternatives / fields.Count)
                + (index < RecordQueryRules.MaxRowComparedAlternatives % fields.Count ? 1 : 0);
            var patterns = Enumerable.Range(0, count - 1).Select(_ => DistinctPattern(next++)).Append(AnyTextPattern);
            values[index] = Or(patterns);
        }

        SearchCriterion[] criteria = [.. fields.Select((field, index) => Text(field, values[index]))];
        var overBound = criteria[..^1]
            .Append(Text(fields[^1], DistinctPattern(next) + OrSeparator + values[^1]))
            .ToArray();
        return (
            criteria,
            overBound,
            "One search can hold at most 6554 wildcard, comparison and multiline alternatives in all.");
    }

    // A '%§x%' pattern of four code units, x the index-th character from FirstDistinctCharacter, a CJK ideograph no
    // pattern syntax reads and no other pattern repeats.
    private static string DistinctPattern(int index) =>
        "%" + AbsentCharacter + (char)(FirstDistinctCharacter + index) + "%";

    // The long patterns at exactly the bound of pattern text, as a client writes them: '%_%_…', '%' and '_' in turn,
    // one per field, of an equal share of the 32,767 code units, the first fields taking the remainder, so none
    // collapses and their text as the patterns run is the whole bound. Over the bound by one unit: beside one pattern,
    // whose value holds the whole bound of one value, a one-unit pattern on another text field of the form; beside
    // several, one unit more in the last.
    private static (SearchCriterion[] Criteria, SearchCriterion[] OverBound, string Message) LongPatternCriteria(
        IReadOnlyList<string> fields,
        string otherField)
    {
        var lengths = new int[fields.Count];
        for (var index = 0; index < fields.Count; index++)
        {
            lengths[index] = (RecordQueryRules.MaxSearchPatternTextLength / fields.Count)
                + (index < RecordQueryRules.MaxSearchPatternTextLength % fields.Count ? 1 : 0);
        }

        SearchCriterion[] criteria =
            [.. fields.Select((field, index) => Text(field, AlternatingPattern(lengths[index])))];
        SearchCriterion[] overBound;
        if (fields.Count == 1)
        {
            Assert.NotEqual(otherField, fields[0]);
            overBound = [.. criteria, Text(otherField, "%")];
        }
        else
        {
            overBound = [.. criteria[..^1], Text(fields[^1], AlternatingPattern(lengths[^1] + 1))];
        }

        return (
            criteria,
            overBound,
            "The wildcard patterns of one search can hold at most 32767 characters in all.");
    }

    // '%' and '_' in turn, starting with '%', for length code units: a pattern with no run of '%' to collapse.
    private static string AlternatingPattern(int length) =>
        string.Create(length, 0, (text, _) =>
        {
            for (var position = 0; position < text.Length; position++)
            {
                text[position] = position % 2 == 0 ? '%' : '_';
            }
        });

    // The number of the form's complete active records whose columns each hold at least one character, as
    // LIKE N'_%' reads them under the column's collation: what a search matches whose every condition ends in '_%'
    // and whose other patterns hold a character no value holds.
    private static async Task<long> CompleteActiveRecordsHoldingTextAsync(
        SqlConnection connection,
        string formName,
        IReadOnlyList<string> expressions,
        CancellationToken token)
    {
        var sql = "SELECT COUNT_BIG(*) FROM " + CompleteRecords[formName]
            + " WHERE b.[RecStatus] = @activeStatus AND "
            + string.Join(" AND ", expressions.Select(expression => expression + " LIKE @anyText"));
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add(new SqlParameter("@activeStatus", SqlDbType.SmallInt) { Value = ActiveStatus });
        command.Parameters.Add(
            new SqlParameter("@anyText", SqlDbType.NVarChar, AnyTextPattern.Length) { Value = AnyTextPattern });
        return Assert.IsType<long>(await command.ExecuteScalarAsync(token));
    }

    // The threshold searches' LastName alternatives: the seeded values, a trailing and a leading wildcard pattern for
    // each, spread evenly across the list, and values that match nothing in the rest.
    private static string[] ThresholdAlternatives(IReadOnlyList<string> seeded, int size)
    {
        var matching = seeded.SelectMany(ThresholdAlternativesOf).ToArray();
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

    // A seeded value's threshold alternatives: the value, and its trailing and leading wildcard patterns; a
    // one-character value has no leading pattern.
    private static string[] ThresholdAlternativesOf(string value) =>
        value.Length > 1 ? [value, value + "*", "*" + value[1..]] : [value, value + "*"];

    // Whether a value's threshold alternatives each fit the character length of the LastName column the reader
    // describes and parse as one LastName condition with one alternative each. A value at the column's full length
    // fails: its trailing pattern is one character longer than the column.
    private static bool FitsThresholdSearch(SeededForm form, ColumnMetadata column, string value)
    {
        var alternatives = ThresholdAlternativesOf(value);
        if (column.MaxCharacters is { } maxCharacters
            && alternatives.Any(alternative => alternative.Length > maxCharacters))
        {
            return false;
        }

        var parsed = SearchCriteriaParser.Parse(form.DataFields, [Text("LastName", Or(alternatives))]);
        return parsed.IsSuccess
            && parsed.Conditions.Count == 1
            && parsed.Conditions[0].Alternatives.Count == alternatives.Length;
    }

    // LastName values of seeded active CaseReport records, fewest matches first, while their matches together stay
    // within MaxSeededMatches; at least two, so excluding one record leaves a match. Records other tests create are
    // left out by their marker, so they neither become a value nor crowd the seeded values out, and a value is taken
    // only when its threshold alternatives fit the column and parse. A value of NativeLikeLimit characters or more is
    // skipped as well, whatever the column's length: its trailing-wildcard pattern is a long pattern, which binds no
    // parameter of its own, so the threshold searches' parameter counts would not hold.
    private static async Task<IReadOnlyList<string>> SeededLastNamesAsync(
        SqlConnection connection,
        SeededForm form,
        CancellationToken token)
    {
        var field = Assert.Single(form.DataFields, candidate => candidate.Name == "LastName");
        var column = form.Schema.FindDataColumn(field);
        Assert.NotNull(column);

        var sql = "SELECT p1.[LastName], COUNT_BIG(*) FROM " + CompleteRecords[CaseReport]
            + SeededActiveRecords + " AND p1.[LastName] IS NOT NULL GROUP BY p1.[LastName]"
            + " ORDER BY COUNT_BIG(*), p1.[LastName]";
        await using var command = new SqlCommand(sql, connection);
        AddSeededActiveParameters(command);
        await using var reader = await command.ExecuteReaderAsync(token);

        var values = new List<string>();
        var matches = 0L;
        while (await reader.ReadAsync(token))
        {
            var value = reader.GetString(0);
            var count = reader.GetInt64(1);
            if (!IsPlainCriterionText(value)
                || value.Length >= NativeLikeLimit
                || !FitsThresholdSearch(form, column.Metadata, value))
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
            + " WHERE b.[RecStatus] = @activeStatus AND p1.[LastName] = @lastName ORDER BY b.[UniqueKey]";
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add(new SqlParameter("@activeStatus", SqlDbType.SmallInt) { Value = ActiveStatus });
        command.Parameters.Add(new SqlParameter("@lastName", SqlDbType.NVarChar, -1) { Value = lastName });
        var id = await command.ExecuteScalarAsync(token);
        return Assert.IsType<string>(id);
    }

    // Up to count distinct values of one column of the form's seeded complete active records, as criterion text.
    private static async Task<IReadOnlyList<string>> SampleAsync(
        SqlConnection connection,
        string formName,
        string expression,
        int count,
        CancellationToken token)
    {
        var sql = "SELECT DISTINCT TOP (@sampleRows) " + expression + " AS [v] FROM " + CompleteRecords[formName]
            + SeededActiveRecords + " AND " + expression + " IS NOT NULL ORDER BY [v]";
        await using var command = new SqlCommand(sql, connection);
        AddSeededActiveParameters(command);
        command.Parameters.Add(new SqlParameter("@sampleRows", SqlDbType.Int) { Value = SampleRows });
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

    // One seeded complete active record's values of the columns, as criterion text, for a multi-field search it must
    // match.
    private static async Task<IReadOnlyList<string>> AnchorAsync(
        SqlConnection connection,
        string formName,
        IReadOnlyList<string> expressions,
        CancellationToken token)
    {
        var sql = "SELECT TOP (@sampleRows) " + string.Join(", ", expressions) + " FROM " + CompleteRecords[formName]
            + SeededActiveRecords + " AND "
            + string.Join(" AND ", expressions.Select(expression => expression + " IS NOT NULL"))
            + " ORDER BY b.[UniqueKey]";
        await using var command = new SqlCommand(sql, connection);
        AddSeededActiveParameters(command);
        command.Parameters.Add(new SqlParameter("@sampleRows", SqlDbType.Int) { Value = SampleRows });
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

    // Binds the values of SeededActiveRecords: the active status as the smallint RecStatus column, the marker prefix
    // and its length.
    private static void AddSeededActiveParameters(SqlCommand command)
    {
        command.Parameters.Add(new SqlParameter("@activeStatus", SqlDbType.SmallInt) { Value = ActiveStatus });
        command.Parameters.Add(
            new SqlParameter("@markerPrefix", SqlDbType.NVarChar, TestDatabase.MarkerPrefix.Length)
            {
                Value = TestDatabase.MarkerPrefix,
            });
        command.Parameters.Add(
            new SqlParameter("@markerPrefixLength", SqlDbType.Int) { Value = TestDatabase.MarkerPrefix.Length });
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

    // The differential corpus: fixed patterns over every LIKE construct, then patterns built from each stored value so
    // that the corpus matches rows: the value itself, with one end or both open, with '_' for a character, a set for
    // its first character, its upper case, an inner run, the value with one more '_' and with a trailing blank. The
    // characters a collation expands or ignores are CollationCorpus's.
    private static List<string> PatternCorpus(IReadOnlyList<string> samples)
    {
        var corpus = new List<string>
        {
            "%", "%%", "_%", "%_", "__%", "%_%_%_%", "%a%", "%e%n%", "%A%E%", "%%%a%%%e%%", "a%", "%s", "S%",
            "%[aeiou]%", "%[^a-m]%", "[A-M]%", "%[a-c-e]%", "[]%", "%[]", "%[^]%", "[^]%[^]", "%[a", "%[", "%[^",
            "%[%]%", "%[_]%", "%[[]%", "% %", "%  %", "%y", "%[0-9]%", "%-%", "%]%", "%^%", "%e_%", "%[a-]%",
            "%_e_%_",
        };

        foreach (var value in samples)
        {
            corpus.Add(value);
            corpus.Add(value + "%");
            corpus.Add("%" + value);
            corpus.Add("%" + value + "%");
            corpus.Add(value[..^1] + "_");
            corpus.Add("_" + value[1..]);
            corpus.Add("[" + value[..1] + "]" + value[1..] + "%");
            corpus.Add(value.ToUpperInvariant() + "%");
            corpus.Add(value + "_");
            corpus.Add(value + " ");
            if (value.Length >= 3)
            {
                corpus.Add("%" + value[1..^1] + "%");
                corpus.Add(value[..1] + "%" + value[^1..]);
                corpus.Add("%" + value[..2] + "%" + value[^2..] + "%");
                corpus.Add(value[..1] + "%" + value[1..2] + "%" + value[2..]);
            }
        }

        return corpus;
    }

    // The GlobalRecordId of every seeded complete active CaseReport record, so a match of one can be told from a match
    // of a record another test created.
    private static async Task<HashSet<string>> SeededActiveIdsAsync(SqlConnection connection, CancellationToken token)
    {
        var sql = "SELECT b.[GlobalRecordId] FROM " + CompleteRecords[CaseReport] + SeededActiveRecords;
        await using var command = new SqlCommand(sql, connection);
        AddSeededActiveParameters(command);
        await using var reader = await command.ExecuteReaderAsync(token);

        var ids = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(token))
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    // Up to CollationSamples distinct values of each column of the seeded complete active CaseReport records that hold
    // the fragment in either case, at least one in all. The fragment is sought in the value lowercased under a binary
    // collation, so the database collation does not decide which values hold it. A value is taken only when two or more
    // characters follow the fragment's first occurrence, it holds no '%', '_' or '[', and it is at most
    // MaxCollationTextLength characters long, so every pattern built from it stays a native LIKE pattern of that value.
    private static async Task<IReadOnlyList<string>> SeededTextsHoldingAsync(
        SqlConnection connection,
        IReadOnlyList<string> expressions,
        string fragment,
        CancellationToken token)
    {
        var texts = new List<string>();
        foreach (var expression in expressions)
        {
            var sql = "SELECT DISTINCT " + expression + " AS [v] FROM " + CompleteRecords[CaseReport]
                + SeededActiveRecords + " AND CHARINDEX(@fragment, LOWER(" + expression
                + " COLLATE Latin1_General_100_BIN2)) > 0 ORDER BY [v]";
            await using var command = new SqlCommand(sql, connection);
            AddSeededActiveParameters(command);
            command.Parameters.Add(
                new SqlParameter("@fragment", SqlDbType.NVarChar, fragment.Length) { Value = fragment });
            await using var reader = await command.ExecuteReaderAsync(token);

            var taken = 0;
            while (taken < CollationSamples && await reader.ReadAsync(token))
            {
                var text = reader.GetString(0);
                var at = text.IndexOf(fragment, StringComparison.OrdinalIgnoreCase);
                if (at >= 0
                    && at + fragment.Length + 2 <= text.Length
                    && text.Length <= MaxCollationTextLength
                    && text.IndexOfAny(['%', '_', '[']) < 0)
                {
                    texts.Add(text);
                    taken++;
                }
            }
        }

        Assert.True(
            texts.Count > 0,
            $"No seeded value of {string.Join(", ", expressions)} holds \"{fragment}\" in either case.");
        return texts;
    }

    // The collation corpus over one seeded value, built around the first occurrence of LigatureLetters in it, with the
    // head before them and the tail after: the ligature in place of them, with either end open, inside and in either
    // case; '_' in place of them, beside the ligature and for either letter; sets of the ligature, which as one member
    // stands for two letters only if the collation says so, of the first letter and negated; NUL, U+00AD and a
    // supplementary character inside the literal; a combining acute on the last letter, appended with an 'e' and in
    // place of the tail's first letter, and accented letters, which no seeded value holds; tails led by a set, which
    // past a forced limit run backwards from the value's end, an ignorable character among them; and the value itself
    // with one end or both open.
    private static string[] CollationCorpus(string value)
    {
        var at = value.IndexOf(LigatureLetters, StringComparison.OrdinalIgnoreCase);
        var head = value[..at];
        var tail = value[(at + LigatureLetters.Length)..];
        var (first, second) = (value[at], value[at + 1]);
        var ligated = head + Ligature + tail;
        return
        [
            ligated, "%" + Ligature + tail, head + Ligature + "%", "%" + Ligature + "%", ligated.ToUpperInvariant(),
            ligated.ToLowerInvariant(), "%" + Ligature + tail.ToUpperInvariant(),
            head + "_" + tail, head + first + "_" + tail, head + "_" + second + tail, head + "__" + tail,
            head + Ligature + "_" + tail[1..], "%_" + Ligature + "%",
            head + "[" + first + "]" + second + tail, head + "[" + Ligature + "]" + tail,
            head + "[" + Ligature + first + "]" + second + tail, head + "[^" + Ligature + "]" + second + tail,
            "%[" + Ligature + "]%",
            head + first + "\0" + second + tail, head + first + "\u00AD" + second + tail,
            head + first + Supplementary + second + tail, head + Ligature + "\0" + tail, "%" + Ligature + "\u00AD%",
            value + "\u0301", value + "e\u0301", head + first + second + "e\u0301" + tail[1..],
            head + first + "\u00ED" + tail, value[..^1] + "\u00E9",
            "%[a-z]" + Ligature + tail, "%[a-z]" + tail, "%[a-z]" + second + tail, "%[a-z]" + tail[..^1] + "_",
            "%[a-z]" + tail + "\0", "%[a-z]" + tail[..^1] + "\u00AD" + tail[^1..], "%[a-z]" + tail + Supplementary,
            value, value + "%", "%" + value[1..], "%" + value + "%",
        ];
    }

    // The grid filters over one seeded text, built around the first occurrence of LigatureLetters in it, each with
    // whether it lists a record: the ligature in place of them with the text before and after it, after it only,
    // before it only, and in upper and lower case, each of which lists the text's record; NUL, U+00AD and a
    // supplementary character inside the literal, which Latin1_General_100_CI_AS alone decides; and a combining acute
    // on the last letter or in place of the tail's first letter, and accented letters in place of the second ligature
    // letter and of the last letter, which no seeded text holds, so each lists none.
    private static (string Filter, bool? Lists)[] CollationFilters(string text)
    {
        var at = text.IndexOf(LigatureLetters, StringComparison.OrdinalIgnoreCase);
        var head = text[..at];
        var tail = text[(at + LigatureLetters.Length)..];
        var (first, second) = (text[at], text[at + 1]);
        var ligated = head + Ligature + tail;
        return
        [
            (ligated, true), (Ligature + tail, true), (head + Ligature, true), (ligated.ToUpperInvariant(), true),
            (ligated.ToLowerInvariant(), true),
            (head + first + "\0" + second + tail, null), (head + first + "\u00AD" + second + tail, null),
            (head + first + Supplementary + second + tail, null), (head + Ligature + "\0" + tail, null),
            (text + "\u0301", false), (head + first + second + "e\u0301" + tail[1..], false),
            (head + first + "\u00ED" + tail, false), (text[..^1] + "\u00E9", false),
        ];
    }

    private static SearchCondition LikeCondition(DataFieldMetadata field, string pattern) => new(
        field.Name,
        SearchTarget.PageField,
        field.FieldType,
        [new SearchAlternative(SearchOperator.Like, FieldValue.FromText(pattern))]);

    // A statement runs a long-pattern program exactly when it should, and is in every case one read: a single SELECT or
    // WITH statement with no batch separator and no write.
    private static void AssertProgram(SqlStatement statement, bool longPattern)
    {
        Assert.DoesNotContain(";", statement.Text, StringComparison.Ordinal);
        Assert.False(SqlCommandRecorder.ContainsWriteStatement(statement.Text), "A read holds a write statement.");
        if (longPattern)
        {
            Assert.Contains(LongPatternCte, statement.Text, StringComparison.Ordinal);
            Assert.Contains(LongPatternHint.Trim()[..^1], statement.Text, StringComparison.Ordinal);
        }
        else
        {
            Assert.DoesNotContain(LongPatternCte, statement.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("MAXRECURSION", statement.Text, StringComparison.Ordinal);
        }
    }

    // A list statement is one held read whether or not its filter is long: the base table and each page table it
    // joins carry HOLDLOCK wherever they are read, once by the filtered set and, for a long filter, once more by the
    // program, and the statement ends with the list's plan hints, after the recursion hint when it runs a program.
    private static void AssertHeldList(SeededForm form, SqlStatement statement, bool longPattern)
    {
        var text = statement.Text;
        var baseTable = SqlIdentifier.Quote(form.Schema.BaseTable);
        Assert.Equal(longPattern ? 2 : 1, Occurrences(text, " FROM " + baseTable + " "));
        var heldBase = baseTable + " " + RecordSqlBuilder.BaseAlias + ListHoldLock;
        Assert.Equal(longPattern ? 2 : 1, Occurrences(text, heldBase));
        var pageJoins = Occurrences(text, " LEFT JOIN ");
        Assert.NotEqual(0, pageJoins);
        Assert.Equal(pageJoins, Occurrences(text, ListHoldLock + " ON "));
        Assert.EndsWith(longPattern ? LongListPlanHints : ListPlanHints, text, StringComparison.Ordinal);
    }

    // The number of non-overlapping ordinal occurrences of value in text.
    private static int Occurrences(string text, string value)
    {
        var count = 0;
        for (var at = text.IndexOf(value, StringComparison.Ordinal);
             at >= 0;
             at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    // The GlobalRecordId of every row a list statement flags as on the page, in order, and the total of the filtered
    // set every row carries; an empty page arrives as one row flagged 0, which carries the total alone.
    private static Task<(IReadOnlyList<string> Ids, long Total)> ListRowsAsync(
        SqlConnection connection,
        SqlStatement statement,
        CancellationToken token) =>
        SqlExecution.ReadAsync(
            connection,
            null,
            Operation,
            statement,
            DatabaseOptions.DefaultCommandTimeoutSeconds,
            NullSqlCommandObserver.Instance,
            token,
            async (reader, cancellationToken) =>
            {
                var ids = new List<string>();
                long? total = null;
                while (await reader.ReadAsync(cancellationToken))
                {
                    total = reader.GetInt64(SearchSqlBuilder.TotalOrdinal);
                    if (reader.GetInt32(SearchSqlBuilder.InPageOrdinal) == 1)
                    {
                        ids.Add(reader.GetString(SearchSqlBuilder.IdOrdinal));
                    }
                }

                Assert.True(total.HasValue, "The list statement returned no row, so no total.");
                return ((IReadOnlyList<string>)ids, total.Value);
            });

    // A statement decodes a packed list, and ends with the packed plan hints, exactly when it binds its values packed;
    // a per-alternative statement has no query hint, and none uses OPENJSON.
    private static void AssertCarrier(SearchSelect select, bool packed)
    {
        Assert.DoesNotContain(LevelBoundCarrier, select.Statement.Text, StringComparison.Ordinal);
        if (packed)
        {
            Assert.Contains(PackedListMarker, select.Statement.Text, StringComparison.Ordinal);
            Assert.EndsWith(PackedPlanHints, select.Statement.Text, StringComparison.Ordinal);
        }
        else
        {
            Assert.DoesNotContain(PackedListMarker, select.Statement.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(QueryHint, select.Statement.Text, StringComparison.Ordinal);
        }
    }

    // A connection with an unpooled session of its own, so the SET options a test changes end when it is disposed.
    private static async Task<SqlConnection> OpenUnpooledConnectionAsync(CancellationToken token)
    {
        var builder = TestDatabase.Builder();
        builder.Pooling = false;
        var connection = TestDatabase.CreateConnection(builder);
        try
        {
            await connection.OpenAsync(token);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    // Applies NonDefaultSessionOptions to the connection's session and checks that they took effect.
    private static async Task SetNonDefaultSessionOptionsAsync(SqlConnection connection, CancellationToken token)
    {
        await using var command = new SqlCommand(NonDefaultSessionOptions, connection);
        var quotedIdentifier = await command.ExecuteScalarAsync(token);
        Assert.Equal(0, Convert.ToInt32(quotedIdentifier, CultureInfo.InvariantCulture));
    }

    private static string Or(IEnumerable<string> alternatives) => string.Join(OrSeparator, alternatives);

    private static SearchCriterion Text(string field, string value) => new(field, FieldValue.FromText(value));

    private static IReadOnlyList<SearchCondition> Parse(SeededForm form, SearchCriterion[] criteria)
    {
        var result = SearchCriteriaParser.Parse(form.DataFields, criteria);
        Assert.True(
            result.IsSuccess,
            "The criteria did not parse; errors under: " + string.Join(", ", result.FieldErrors.Keys));
        return result.Conditions;
    }

    // One condition on the field holding the alternatives exactly as the parser reads them, for more alternatives than
    // one value or one search admits: the parser reads them in chunks whose text each fits
    // RecordQueryRules.MaxSearchValueLength, a value that alone stays within the search-wide bounds, and their
    // alternatives, in order, form the one condition the search builder receives, which binds a condition of any size.
    // The alternatives carry no surrounding blanks, so trimming each chunk, as the parser trims a value, reads every
    // alternative as one value of them all would.
    private static SearchCondition ParseInChunks(SeededForm form, string fieldName, IReadOnlyList<string> alternatives)
    {
        Assert.All(alternatives, alternative => Assert.Equal(alternative.Trim(), alternative));

        var parts = new List<SearchCondition>();
        var chunk = new List<string>();
        var length = 0;
        foreach (var alternative in alternatives)
        {
            var separator = chunk.Count == 0 ? 0 : OrSeparator.Length;
            if (chunk.Count > 0 && length + separator + alternative.Length > RecordQueryRules.MaxSearchValueLength)
            {
                parts.Add(Assert.Single(Parse(form, [Text(fieldName, Or(chunk))])));
                chunk.Clear();
                length = 0;
                separator = 0;
            }

            chunk.Add(alternative);
            length += separator + alternative.Length;
        }

        parts.Add(Assert.Single(Parse(form, [Text(fieldName, Or(chunk))])));
        var first = parts[0];
        return new SearchCondition(
            first.FieldName,
            first.Target,
            first.FieldType,
            [.. parts.SelectMany(part => part.Alternatives)]);
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

/// <summary>
/// Unit tests of the Find admission of <see cref="SqlRecordRepository.SearchAsync"/>, <see cref="FindAdmission"/>: a
/// search takes a slot before it opens its connection; while every slot is held it waits, past the command timeout,
/// with no failure of its own, no connection opened and nothing sent; it is admitted as soon as a slot is given back;
/// it gives its own back whatever its outcome; and it ends with the request's cancellation, having taken no slot and
/// sent nothing, when it is cancelled while it waits.
/// </summary>
/// <remarks>
/// <para>
/// No database is used: the class carries no <c>Integration</c> trait and joins no database collection. The
/// repository's connection string is malformed, so an admitted search fails at once in the connection factory, as a
/// <see cref="DataStoreWriteException"/> with no SQL error number, before any connection exists; a search still
/// running while a case watches it has therefore not reached the factory and has opened no connection. Each case
/// hands the repository a one-slot admission it holds itself through the repository's internal constructor, the
/// command recorder shows that no case sends a command, and every wait is bounded by the case's cancellation source
/// or a prompt deadline, so a regression fails its case instead of hanging the run.
/// </para>
/// <para>
/// The class name ends with <c>SqlRecordRepositoryTests</c>, so a filter on that name selects it with the
/// repository's integration tests.
/// </para>
/// </remarks>
public sealed class FindAdmissionSqlRecordRepositoryTests
{
    // A connection string SqlClient rejects before any connection exists: an admitted search fails there.
    private const string MalformedConnectionString = "Not A Keyword=1";

    // The shortest command timeout DatabaseOptions admits, which the waiting case outlasts three times over.
    private const int ShortCommandTimeoutSeconds = 1;

    // How long searches waiting on a held slot are watched: well past the short command timeout.
    private static readonly TimeSpan PastTheCommandTimeout = TimeSpan.FromSeconds(3 * ShortCommandTimeoutSeconds);

    // How long a waiting search is given to show that it stays waiting while every slot is held.
    private static readonly TimeSpan StillWaiting = TimeSpan.FromMilliseconds(500);

    // The longest a case waits for a slot it expects free, or for an admitted search to end.
    private static readonly TimeSpan Prompt = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(2);

    // A synthetic one-page form with one text field. No case gets as far as reading metadata, so no table is named.
    private static readonly PageMetadata Page = new(1, "Page 1", 0, "AdmissionForm1");

    private static readonly FormMetadata Form = new(
        1,
        "AdmissionForm",
        "AdmissionForm",
        [Page],
        [
            new FieldMetadata
            {
                FieldId = 1,
                Name = "LastName",
                FieldTypeId = 1,
                FieldTypeName = "Text",
                PageId = Page.PageId,
                TabIndex = 1,
                Column = FieldTypeMapper.DescribeColumn("LastName", "nvarchar", 8000),
            },
        ]);

    private static readonly SearchCondition[] Conditions =
    [
        new("LastName", SearchTarget.PageField, FieldType.Text, [new(SearchOperator.Like, FieldValue.FromText("Sm%"))]),
    ];

    // Every slot held: two searches outlast the command timeout, one second here, three times over and are still
    // waiting, with no failure of their own; neither reached the connection factory, which fails the malformed
    // connection string at once, so no connection was opened and nothing was sent. One slot given back admits both in
    // turn, the second only because the first gave the slot back although it failed in the factory, a failure that is
    // never the database's unavailability; and the slot is free once both have failed.
    [Fact]
    public async Task Search_EverySlotHeld_WaitsPastTheCommandTimeoutUntilASlotIsGivenBack()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        var admission = new FindAdmission(1);
        await TakeFreeSlotAsync(admission, timeout.Token);
        var (repository, recorder) = CreateRepository(ShortCommandTimeoutSeconds, admission);

        Task<SearchResponse>[] waiting =
        [
            repository.SearchAsync(Form, Conditions, includeDeleted: false, null, timeout.Token),
            repository.SearchAsync(Form, Conditions, includeDeleted: false, null, timeout.Token),
        ];
        await Task.Delay(PastTheCommandTimeout, timeout.Token);

        Assert.All(waiting, search => Assert.False(search.IsCompleted, "A search ended while every slot was held."));
        Assert.Empty(recorder.Commands);

        admission.Release();

        foreach (var search in waiting)
        {
            AssertFailedInTheConnectionFactory(
                await Assert.ThrowsAsync<DataStoreWriteException>(() => search.WaitAsync(Prompt, timeout.Token)));
        }

        Assert.Empty(recorder.Commands);
        await TakeFreeSlotAsync(admission, timeout.Token);
        admission.Release();
    }

    // A search cancelled while it waits for a slot ends with the request's cancellation, takes no slot, leaves no
    // waiter behind and sends nothing; a search whose request is already cancelled takes no slot even when one is free.
    [Fact]
    public async Task Search_CancelledWhileWaitingForASlot_ThrowsTheRequestsCancellationAndTakesNoSlot()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        var admission = new FindAdmission(1);
        await TakeFreeSlotAsync(admission, timeout.Token);
        var (repository, recorder) = CreateRepository(DatabaseOptions.DefaultCommandTimeoutSeconds, admission);

        using var request = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var waiting = repository.SearchAsync(Form, Conditions, includeDeleted: false, null, request.Token);
        await Task.Delay(StillWaiting, timeout.Token);
        Assert.False(waiting.IsCompleted, "The search ended while every slot was held.");

        await request.CancelAsync();

        var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => waiting.WaitAsync(Prompt, timeout.Token));
        Assert.Equal(request.Token, cancelled.CancellationToken);

        // The slot given back goes to no cancelled waiter, so it is free.
        admission.Release();
        await TakeFreeSlotAsync(admission, timeout.Token);
        admission.Release();

        var early = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => repository.SearchAsync(Form, Conditions, includeDeleted: false, null, request.Token));
        Assert.Equal(request.Token, early.CancellationToken);

        Assert.Empty(recorder.Commands);
        await TakeFreeSlotAsync(admission, timeout.Token);
        admission.Release();
    }

    // The admission's own contract: two slots for a production repository, at least one slot, no release of a slot
    // that was never taken, and no repository without an admission.
    [Fact]
    public void Admission_RejectsAnInvalidSlotCountOrRelease()
    {
        Assert.Equal(2, FindAdmission.DefaultSlots);
        Assert.Throws<ArgumentOutOfRangeException>("slotCount", () => new FindAdmission(0));

        var admission = new FindAdmission(1);
        Assert.Throws<SemaphoreFullException>(admission.Release);
        Assert.Throws<ArgumentNullException>(
            "findAdmission",
            () => new SqlRecordRepository(
                new SqlConnectionFactory(new DatabaseOptions(MalformedConnectionString)),
                new SqlMetadataReader(
                    new SqlConnectionFactory(new DatabaseOptions(MalformedConnectionString)),
                    new DatabaseOptions(MalformedConnectionString),
                    NullSqlCommandObserver.Instance),
                new DatabaseOptions(MalformedConnectionString),
                NullSqlCommandObserver.Instance,
                null!));
    }

    // Takes the one slot of a one-slot admission, failing the case unless the slot is free and taken promptly.
    private static Task TakeFreeSlotAsync(FindAdmission admission, CancellationToken cancellationToken) =>
        admission.EnterAsync(cancellationToken).WaitAsync(Prompt, cancellationToken);

    // An admitted search over the malformed connection string fails in the connection factory, for the Find
    // operation, with NotWritten and no SQL error number.
    private static void AssertFailedInTheConnectionFactory(DataStoreWriteException failure)
    {
        Assert.Equal("records.search", failure.Operation);
        Assert.Equal(WriteOutcome.NotWritten, failure.Outcome);
        Assert.Null(failure.SqlErrorNumber);
    }

    // A repository over the malformed connection string, with the given command timeout and admission, and the
    // recorder that observes every command it sends.
    private static (SqlRecordRepository Repository, SqlCommandRecorder Recorder) CreateRepository(
        int commandTimeoutSeconds,
        FindAdmission admission)
    {
        var options = new DatabaseOptions(MalformedConnectionString, commandTimeoutSeconds);
        var connections = new SqlConnectionFactory(options);
        var recorder = new SqlCommandRecorder();
        var repository = new SqlRecordRepository(
            connections,
            new SqlMetadataReader(connections, options, recorder),
            options,
            recorder,
            admission);
        return (repository, recorder);
    }
}

/// <summary>
/// Unit tests of the converter's readers of base-table system columns and of the integers a statement computes:
/// <see cref="ColumnValueConverter.FromSystemInteger"/>, <see cref="ColumnValueConverter.FromSystemText"/> and
/// <see cref="ColumnValueConverter.FromComputedInteger"/>. A stored system integer is accepted only with the runtime
/// type SqlClient returns for its reader-built column, <c>byte</c> for <c>tinyint</c>, <c>short</c> for
/// <c>smallint</c> and <c>int</c> for <c>int</c>; a computed integer only with the type its builder declares,
/// <c>long</c> for <c>bigint</c> and <c>int</c> for <c>int</c>; and each is narrowed to the port's <c>int</c>
/// without wrapping. A system text is returned exactly as stored, trailing spaces included. Every refusal of a read
/// value is fixed text that names no value.
/// </summary>
/// <remarks>
/// The columns are the reader-built columns of the seeded <c>FoodHistory</c> form, read with
/// <c>SqlMetadataReader.ReadFormAsync</c>, which sends only <c>SELECT</c>: its base table's <c>UniqueKey</c>
/// (<c>int</c>), <c>RecStatus</c> (<c>smallint</c>) and <c>GlobalRecordId</c> (<c>nvarchar(255)</c>), and the first
/// page-table column of each other SQL type a case names. No seeded table has a <c>bigint</c> column and no test
/// creates a schema object, so the <c>bigint</c> arm, whose exact-type check and narrowing the system reader shares
/// with the computed one, is pinned through <see cref="DbType.Int64"/> computed integers. The cases read only the
/// catalogue, which no test changes, so they hold no <see cref="DatabaseWriteLock"/>; every converted value is given
/// in-process.
/// </remarks>
[Collection(DatabaseCollections.Database)]
[Trait("Category", "Integration")]
public sealed class SystemValueConversionTests : IAsyncLifetime
{
    // The fixed text of each refusal of a read value, naming no value, column or field.
    private const string MissingValueMessage = "A system column or computed integer was read with no value.";
    private const string UnexpectedTypeMessage =
        "A system column or computed integer was not read as its native type.";
    private const string OutOfRangeMessage = "A system column or computed integer lies outside the int range.";

    // The seeded form whose base and page tables hold every column type the cases name
    // (testdata/sql/05_data_tables.sql: FoodHistory, FoodHistory2, FoodHistory3).
    private const string SeededForm = "FoodHistory";

    private FormSchema? _schema;

    public async Task InitializeAsync()
    {
        await TestDatabase.EnsureReachableAsync();

        var options = new DatabaseOptions(TestDatabase.ConnectionString);
        var reader = new SqlMetadataReader(new SqlConnectionFactory(options), options, NullSqlCommandObserver.Instance);
        FormSummary summary = Assert.Single(
            await reader.ListFormsAsync(CancellationToken.None),
            form => string.Equals(form.Name, SeededForm, StringComparison.Ordinal));

        await using SqlConnection connection = await TestDatabase.OpenConnectionAsync();
        (FormMetadata Form, FormSchema Schema)? read =
            await reader.ReadFormAsync(connection, transaction: null, summary.ViewId, CancellationToken.None);
        Assert.True(read is not null, "The seeded form has no metadata.");
        _schema = read.Value.Schema;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // Each stored value is given by its runtime type and invariant text, so every row keeps its exact CLR type and
    // shows it in the test name.
    [Theory]
    [InlineData("UniqueKey", TypeCode.Int32, "2147483647", 2147483647)]
    [InlineData("UniqueKey", TypeCode.Int32, "1", 1)]
    [InlineData("RecStatus", TypeCode.Int16, "-32768", -32768)]
    [InlineData("RecStatus", TypeCode.Int16, "1", 1)]
    [InlineData("tinyint", TypeCode.Byte, "255", 255)]
    [InlineData("tinyint", TypeCode.Byte, "0", 0)]
    public void FromSystemInteger_ValueOfTheColumnsNativeType_IsThatInteger(
        string column,
        TypeCode storedType,
        string storedText,
        int expected)
    {
        Assert.Equal(expected, ColumnValueConverter.FromSystemInteger(Column(column), Stored(storedType, storedText)));
    }

    // Another integer width, a double, a decimal or text from an integer column did not come from it as stored.
    [Theory]
    [InlineData("UniqueKey", TypeCode.Int64, "17")]
    [InlineData("UniqueKey", TypeCode.Int16, "17")]
    [InlineData("UniqueKey", TypeCode.Byte, "17")]
    [InlineData("UniqueKey", TypeCode.Double, "17")]
    [InlineData("UniqueKey", TypeCode.Decimal, "17")]
    [InlineData("UniqueKey", TypeCode.String, "17")]
    [InlineData("RecStatus", TypeCode.Int32, "17")]
    [InlineData("RecStatus", TypeCode.Int64, "17")]
    [InlineData("RecStatus", TypeCode.Byte, "17")]
    [InlineData("RecStatus", TypeCode.Double, "17")]
    [InlineData("RecStatus", TypeCode.String, "17")]
    [InlineData("tinyint", TypeCode.Int16, "17")]
    [InlineData("tinyint", TypeCode.Int32, "17")]
    [InlineData("tinyint", TypeCode.Int64, "17")]
    [InlineData("tinyint", TypeCode.SByte, "17")]
    [InlineData("tinyint", TypeCode.Double, "17")]
    [InlineData("tinyint", TypeCode.String, "17")]
    public void FromSystemInteger_ValueOfAnotherRuntimeType_IsRefusedWithFixedText(
        string column,
        TypeCode storedType,
        string storedText)
    {
        object stored = Stored(storedType, storedText);
        Assert.Equal(storedType, Type.GetTypeCode(stored.GetType()));

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
            () => ColumnValueConverter.FromSystemInteger(Column(column), stored));

        Assert.Equal(UnexpectedTypeMessage, failure.Message);
        Assert.DoesNotContain(storedText, failure.Message, StringComparison.Ordinal);
    }

    // A system column never reads NULL; DBNull and null are refused alike.
    [Theory]
    [InlineData("UniqueKey")]
    [InlineData("RecStatus")]
    [InlineData("tinyint")]
    public void FromSystemInteger_NoValue_IsRefusedWithFixedText(string column)
    {
        ColumnSchema schemaColumn = Column(column);

        Assert.Equal(
            MissingValueMessage,
            Assert.Throws<InvalidOperationException>(
                () => ColumnValueConverter.FromSystemInteger(schemaColumn, DBNull.Value)).Message);
        Assert.Equal(
            MissingValueMessage,
            Assert.Throws<InvalidOperationException>(
                () => ColumnValueConverter.FromSystemInteger(schemaColumn, null)).Message);
    }

    // The column is checked before the value, so a column that is not an integer type is refused for any value.
    [Theory]
    [InlineData("GlobalRecordId")]
    [InlineData("float")]
    [InlineData("bit")]
    [InlineData("datetime")]
    [InlineData("ntext")]
    public void FromSystemInteger_ColumnThatIsNotAnIntegerType_IsRefusedWhateverTheValue(string column)
    {
        ColumnSchema schemaColumn = Column(column);

        Assert.Throws<ArgumentException>("column", () => ColumnValueConverter.FromSystemInteger(schemaColumn, 1));
        Assert.Throws<ArgumentException>(
            "column",
            () => ColumnValueConverter.FromSystemInteger(schemaColumn, DBNull.Value));
    }

    // A spelling with trailing or leading spaces, and the empty string, come back exactly as given.
    [Theory]
    [InlineData("GlobalRecordId", "3f2504e0-4f89-11d3-9a0c-0305e82c3301")]
    [InlineData("GlobalRecordId", "3f2504e0-4f89-11d3-9a0c-0305e82c3301   ")]
    [InlineData("GlobalRecordId", "")]
    [InlineData("ntext", "  stored text  ")]
    public void FromSystemText_TextColumn_ReturnsTheTextExactlyAsStored(string column, string stored)
    {
        Assert.Equal(stored, ColumnValueConverter.FromSystemText(Column(column), stored));
    }

    [Theory]
    [InlineData(TypeCode.Int32, "17")]
    [InlineData(TypeCode.Int64, "17")]
    [InlineData(TypeCode.Double, "17")]
    [InlineData(TypeCode.Char, "x")]
    [InlineData(TypeCode.Boolean, "True")]
    public void FromSystemText_ValueThatIsNotAString_IsRefusedWithFixedText(TypeCode storedType, string storedText)
    {
        object stored = Stored(storedType, storedText);
        Assert.Equal(storedType, Type.GetTypeCode(stored.GetType()));

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
            () => ColumnValueConverter.FromSystemText(Column("GlobalRecordId"), stored));

        Assert.Equal(UnexpectedTypeMessage, failure.Message);
        Assert.DoesNotContain(storedText, failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("GlobalRecordId")]
    [InlineData("ntext")]
    public void FromSystemText_NoValue_IsRefusedWithFixedText(string column)
    {
        ColumnSchema schemaColumn = Column(column);

        Assert.Equal(
            MissingValueMessage,
            Assert.Throws<InvalidOperationException>(
                () => ColumnValueConverter.FromSystemText(schemaColumn, DBNull.Value)).Message);
        Assert.Equal(
            MissingValueMessage,
            Assert.Throws<InvalidOperationException>(
                () => ColumnValueConverter.FromSystemText(schemaColumn, null)).Message);
    }

    [Theory]
    [InlineData("UniqueKey")]
    [InlineData("RecStatus")]
    [InlineData("tinyint")]
    [InlineData("float")]
    [InlineData("bit")]
    [InlineData("datetime")]
    public void FromSystemText_ColumnThatIsNotACharacterType_IsRefusedWhateverTheValue(string column)
    {
        ColumnSchema schemaColumn = Column(column);

        Assert.Throws<ArgumentException>("column", () => ColumnValueConverter.FromSystemText(schemaColumn, "text"));
        Assert.Throws<ArgumentException>(
            "column",
            () => ColumnValueConverter.FromSystemText(schemaColumn, DBNull.Value));
    }

    // COUNT_BIG, ROW_NUMBER and the bigint SUM windows are declared Int64; the list's page flag is declared Int32.
    [Theory]
    [InlineData(DbType.Int64, TypeCode.Int64, "2147483647", 2147483647)]
    [InlineData(DbType.Int64, TypeCode.Int64, "-2147483648", -2147483648)]
    [InlineData(DbType.Int64, TypeCode.Int64, "0", 0)]
    [InlineData(DbType.Int32, TypeCode.Int32, "1", 1)]
    [InlineData(DbType.Int32, TypeCode.Int32, "0", 0)]
    public void FromComputedInteger_ValueOfTheDeclaredType_IsThatInteger(
        DbType nativeType,
        TypeCode storedType,
        string storedText,
        int expected)
    {
        Assert.Equal(expected, ColumnValueConverter.FromComputedInteger(nativeType, Stored(storedType, storedText)));
    }

    [Theory]
    [InlineData(DbType.Int64, TypeCode.Int32, "17")]
    [InlineData(DbType.Int64, TypeCode.Int16, "17")]
    [InlineData(DbType.Int64, TypeCode.Byte, "17")]
    [InlineData(DbType.Int64, TypeCode.Double, "17")]
    [InlineData(DbType.Int64, TypeCode.Decimal, "17")]
    [InlineData(DbType.Int64, TypeCode.String, "17")]
    [InlineData(DbType.Int32, TypeCode.Int64, "17")]
    [InlineData(DbType.Int32, TypeCode.Int16, "17")]
    [InlineData(DbType.Int32, TypeCode.Byte, "17")]
    [InlineData(DbType.Int32, TypeCode.Double, "17")]
    [InlineData(DbType.Int32, TypeCode.String, "17")]
    public void FromComputedInteger_ValueOfAnotherRuntimeType_IsRefusedWithFixedText(
        DbType nativeType,
        TypeCode storedType,
        string storedText)
    {
        object stored = Stored(storedType, storedText);
        Assert.Equal(storedType, Type.GetTypeCode(stored.GetType()));

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
            () => ColumnValueConverter.FromComputedInteger(nativeType, stored));

        Assert.Equal(UnexpectedTypeMessage, failure.Message);
        Assert.DoesNotContain(storedText, failure.Message, StringComparison.Ordinal);
    }

    // A bigint just beyond each end of the int range, and the bigint extremes, fail rather than wrap.
    [Theory]
    [InlineData("2147483648")]
    [InlineData("-2147483649")]
    [InlineData("9223372036854775807")]
    [InlineData("-9223372036854775808")]
    public void FromComputedInteger_BigintBeyondTheIntRange_IsRefusedWithFixedText(string storedText)
    {
        object stored = Stored(TypeCode.Int64, storedText);

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
            () => ColumnValueConverter.FromComputedInteger(DbType.Int64, stored));

        Assert.Equal(OutOfRangeMessage, failure.Message);
        Assert.DoesNotContain(storedText, failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DbType.Int64)]
    [InlineData(DbType.Int32)]
    public void FromComputedInteger_NoValue_IsRefusedWithFixedText(DbType nativeType)
    {
        Assert.Equal(
            MissingValueMessage,
            Assert.Throws<InvalidOperationException>(
                () => ColumnValueConverter.FromComputedInteger(nativeType, DBNull.Value)).Message);
        Assert.Equal(
            MissingValueMessage,
            Assert.Throws<InvalidOperationException>(
                () => ColumnValueConverter.FromComputedInteger(nativeType, null)).Message);
    }

    // No computed column the builders declare has another type, so any other declaration is a programming error.
    [Theory]
    [InlineData(DbType.Byte)]
    [InlineData(DbType.Int16)]
    [InlineData(DbType.UInt64)]
    [InlineData(DbType.Double)]
    [InlineData(DbType.Decimal)]
    [InlineData(DbType.String)]
    public void FromComputedInteger_UndeclaredNativeType_IsRefusedWhateverTheValue(DbType nativeType)
    {
        Assert.Throws<ArgumentException>("nativeType", () => ColumnValueConverter.FromComputedInteger(nativeType, 1));
        Assert.Throws<ArgumentException>(
            "nativeType",
            () => ColumnValueConverter.FromComputedInteger(nativeType, DBNull.Value));
    }

    // A value of the given runtime type, parsed from its invariant text and boxed as a data reader boxes it.
    private static object Stored(TypeCode type, string text) =>
        Convert.ChangeType(text, type, CultureInfo.InvariantCulture)
        ?? throw new ArgumentException("The stored text did not convert to a value.", nameof(text));

    // The reader-built column a case names: a base-table system column by its name, or else the first column of that
    // SQL type in the form's page tables, in page and column order.
    private ColumnSchema Column(string name)
    {
        FormSchema schema = _schema ?? throw new InvalidOperationException("The seeded form schema was not read.");
        return name switch
        {
            nameof(FormSchema.UniqueKey) => RecordSqlBuilder.Require(schema.UniqueKey),
            nameof(FormSchema.RecStatus) => RecordSqlBuilder.Require(schema.RecStatus),
            nameof(FormSchema.GlobalRecordId) => RecordSqlBuilder.Require(schema.GlobalRecordId),
            _ => PageColumnOfType(schema, name),
        };
    }

    private static ColumnSchema PageColumnOfType(FormSchema schema, string sqlType)
    {
        ColumnSchema? column = schema.Pages
            .SelectMany(page => page.Table?.Columns ?? Array.Empty<ColumnSchema>())
            .FirstOrDefault(candidate =>
                string.Equals(candidate.SqlTypeName, sqlType, StringComparison.OrdinalIgnoreCase));
        Assert.True(column is not null, "No page table of the seeded form has a column of the SQL type.");
        return column;
    }
}

/// <summary>
/// Unit tests of <see cref="ColumnValueConverter.FromDbValue"/>, the read side of the SQL value conversion, on columns
/// described by <see cref="FieldTypeMapper.DescribeColumn"/> as the metadata reader describes them. A stored number
/// or option index is accepted only with the runtime type SqlClient returns for its column: <c>byte</c> for
/// <c>tinyint</c>, <c>short</c> for <c>smallint</c>, <c>int</c> for <c>int</c>, <c>long</c> for <c>bigint</c> and a
/// finite <c>double</c> for <c>float</c>. A widened, narrowed or integer/float-swapped projection is refused with
/// fixed text that names no value. No database is used.
/// </summary>
public sealed class StoredValueConversionTests
{
    // The fixed text of every refused stored value, naming no value, column or field.
    private const string UnexpectedStoredValueMessage =
        "A stored value does not have a type its column and field admit.";

    // Each stored value is given by its runtime type and invariant text, so every row keeps its exact CLR type and
    // shows it in the test name. The last row is 2^53 + 1, the first integer a double cannot hold.
    [Theory]
    [InlineData("tinyint", TypeCode.Byte, "255", 255L)]
    [InlineData("smallint", TypeCode.Int16, "-32768", -32768L)]
    [InlineData("int", TypeCode.Int32, "2147483647", 2147483647L)]
    [InlineData("bigint", TypeCode.Int64, "9007199254740993", 9007199254740993L)]
    public void FromDbValue_NumberOnIntegerColumnWithItsNativeType_IsTheExactInteger(
        string sqlType,
        TypeCode storedType,
        string storedText,
        long expected)
    {
        FieldValue value = ColumnValueConverter.FromDbValue(
            Column(sqlType),
            FieldType.Number,
            Stored(storedType, storedText));

        Assert.True(value.TryGetInteger(out long whole), "The stored integer did not read as an exact integer.");
        Assert.Equal(expected, whole);
    }

    // 37.5 is a float like a seeded Age; -0.1 has no exact binary form; the extremes bound the positive finite doubles.
    [Theory]
    [InlineData(37.5)]
    [InlineData(-0.1)]
    [InlineData(double.MaxValue)]
    [InlineData(double.Epsilon)]
    public void FromDbValue_NumberOnFloatColumnWithADouble_IsThatDoubleBitForBit(double stored)
    {
        FieldValue value = ColumnValueConverter.FromDbValue(Column("float"), FieldType.Number, stored);

        Assert.True(value.TryGetNumber(out double number), "The stored float did not read as a number.");
        Assert.Equal(BitConverter.DoubleToInt64Bits(stored), BitConverter.DoubleToInt64Bits(number));
    }

    // The admitted option column types, each with an index of its native type.
    [Theory]
    [InlineData("tinyint", TypeCode.Byte, "0", 0)]
    [InlineData("smallint", TypeCode.Int16, "2", 2)]
    [InlineData("int", TypeCode.Int32, "7", 7)]
    public void FromDbValue_OptionOnIntegerColumnWithItsNativeType_IsTheIndex(
        string sqlType,
        TypeCode storedType,
        string storedText,
        int expected)
    {
        FieldValue value = ColumnValueConverter.FromDbValue(
            Column(sqlType),
            FieldType.Option,
            Stored(storedType, storedText));

        Assert.True(value.TryGetIndex(out int index), "The stored option did not read as an index.");
        Assert.Equal(expected, index);
    }

    // Stored values whose runtime type is not the one SqlClient returns for the column, or that no field value of the
    // field type can hold.
    [Theory]
    // A bigint projected as a double, where 2^53 + 1 has already become 2^53, and a narrower integer.
    [InlineData("bigint", FieldType.Number, TypeCode.Double, "9007199254740992")]
    [InlineData("bigint", FieldType.Number, TypeCode.Int32, "5")]
    // Integer/float swaps on a float column such as the seeded Age and OnsetWeek, a single-precision float, and
    // the non-finite doubles.
    [InlineData("float", FieldType.Number, TypeCode.Int32, "37")]
    [InlineData("float", FieldType.Number, TypeCode.Int64, "37")]
    [InlineData("float", FieldType.Number, TypeCode.Single, "37.5")]
    [InlineData("float", FieldType.Number, TypeCode.Double, "NaN")]
    [InlineData("float", FieldType.Number, TypeCode.Double, "Infinity")]
    // Another integer width, a double or a decimal on an integer column.
    [InlineData("int", FieldType.Number, TypeCode.Int64, "5")]
    [InlineData("int", FieldType.Number, TypeCode.Int16, "5")]
    [InlineData("int", FieldType.Number, TypeCode.Double, "5")]
    [InlineData("int", FieldType.Number, TypeCode.Decimal, "5")]
    [InlineData("smallint", FieldType.Number, TypeCode.Int32, "5")]
    [InlineData("tinyint", FieldType.Number, TypeCode.Int16, "5")]
    [InlineData("tinyint", FieldType.Number, TypeCode.Int32, "5")]
    // An option index of another width, or a negative one.
    [InlineData("smallint", FieldType.Option, TypeCode.Int32, "1")]
    [InlineData("tinyint", FieldType.Option, TypeCode.Int32, "1")]
    [InlineData("int", FieldType.Option, TypeCode.Int64, "1")]
    [InlineData("smallint", FieldType.Option, TypeCode.Int16, "-1")]
    [InlineData("int", FieldType.Option, TypeCode.Int32, "-1")]
    public void FromDbValue_ValueOutsideTheColumnsNativeTypeOrRange_IsRefusedWithFixedText(
        string sqlType,
        FieldType fieldType,
        TypeCode storedType,
        string storedText)
    {
        object stored = Stored(storedType, storedText);
        Assert.Equal(storedType, Type.GetTypeCode(stored.GetType()));

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
            () => ColumnValueConverter.FromDbValue(Column(sqlType), fieldType, stored));

        Assert.Equal(UnexpectedStoredValueMessage, failure.Message);
        Assert.DoesNotContain(storedText, failure.Message, StringComparison.Ordinal);
    }

    // A missing page row is read through a left join as DBNull; null is treated the same.
    [Theory]
    [InlineData("tinyint", FieldType.Number)]
    [InlineData("smallint", FieldType.Number)]
    [InlineData("int", FieldType.Number)]
    [InlineData("bigint", FieldType.Number)]
    [InlineData("float", FieldType.Number)]
    [InlineData("tinyint", FieldType.Option)]
    [InlineData("smallint", FieldType.Option)]
    [InlineData("int", FieldType.Option)]
    public void FromDbValue_DatabaseNullOnNumericColumn_IsTheNullValue(string sqlType, FieldType fieldType)
    {
        ColumnMetadata column = Column(sqlType);

        Assert.True(ColumnValueConverter.FromDbValue(column, fieldType, DBNull.Value).IsNull);
        Assert.True(ColumnValueConverter.FromDbValue(column, fieldType, null).IsNull);
    }

    // The pairing is checked before the value, so a pairing the mapper does not admit is refused for a null too.
    [Theory]
    [InlineData("bigint", FieldType.Option)]
    [InlineData("float", FieldType.Option)]
    [InlineData("nvarchar", FieldType.Number)]
    public void FromDbValue_PairingTheMapperDoesNotAdmit_IsRefusedForTheNullValueToo(
        string sqlType,
        FieldType fieldType)
    {
        ColumnMetadata column = Column(sqlType);
        Assert.False(FieldTypeMapper.IsAdmitted(fieldType, column));

        Assert.Throws<ArgumentException>(
            "column",
            () => ColumnValueConverter.FromDbValue(column, fieldType, DBNull.Value));
    }

    // SQL Server pads a shorter write to an nchar(n) column's width, and the read returns the stored text as it is, the
    // padding and an all-space text included, never trimmed.
    [Theory]
    [InlineData(FieldType.Text, "abc  ")]
    [InlineData(FieldType.LegalValues, "U    ")]
    [InlineData(FieldType.Multiline, "     ")]
    public void FromDbValue_TextOnNcharColumn_IsTheStoredTextWithItsPadding(FieldType fieldType, string stored)
    {
        FieldValue value = ColumnValueConverter.FromDbValue(NcharColumn(5), fieldType, stored);

        Assert.True(
            value.TryGetText(out string? text) && string.Equals(stored, text, StringComparison.Ordinal),
            "The stored nchar text did not read back exactly as stored.");
    }

    // Text longer than nchar(5) cannot be stored, so it is refused before it is bound, trailing spaces counting as
    // characters.
    [Theory]
    [InlineData(FieldType.Text, "abcdef")]
    [InlineData(FieldType.LegalValues, "U     ")]
    public void ToParameter_TextLongerThanTheNcharWidth_IsRefusedBeforeBinding(FieldType fieldType, string text)
    {
        Assert.Throws<ArgumentException>(
            "value",
            () => ColumnValueConverter.ToParameter("@p0", NcharColumn(5), fieldType, FieldValue.FromText(text)));
    }

    // Shorter and full-width text, trailing spaces included, is bound unchanged as a string the size of the column;
    // SQL Server pads the shorter text with spaces to five when it stores it.
    [Theory]
    [InlineData(FieldType.Text, "abc")]
    [InlineData(FieldType.Text, "ab ")]
    [InlineData(FieldType.LegalValues, "U")]
    [InlineData(FieldType.Text, "abcde")]
    [InlineData(FieldType.Text, "ab   ")]
    [InlineData(FieldType.LegalValues, "U    ")]
    public void ToParameter_TextUpToTheNcharWidth_IsBoundUnchanged(FieldType fieldType, string text)
    {
        SqlParameterValue parameter = ColumnValueConverter.ToParameter(
            "@p0",
            NcharColumn(5),
            fieldType,
            FieldValue.FromText(text));

        Assert.Equal(DbType.String, parameter.DbType);
        Assert.Equal(5, parameter.Size);
        Assert.True(
            parameter.Value is string bound && string.Equals(text, bound, StringComparison.Ordinal),
            "The nchar text was not bound exactly as given.");
    }

    // An nchar(n) column as the reader describes it: sys.columns.max_length is 2·n bytes.
    private static ColumnMetadata NcharColumn(int characters) =>
        FieldTypeMapper.DescribeColumn("Stored", "nchar", 2 * characters);

    // A stored value of the given runtime type, parsed from its invariant text and boxed as a data reader boxes it.
    private static object Stored(TypeCode type, string text) =>
        Convert.ChangeType(text, type, CultureInfo.InvariantCulture)
        ?? throw new ArgumentException("The stored text did not convert to a value.", nameof(text));

    // A page-table column as the reader describes it, with the sys.columns.max_length SQL Server reports for the type.
    private static ColumnMetadata Column(string sqlType) => FieldTypeMapper.DescribeColumn(
        "Stored",
        sqlType,
        sqlType switch
        {
            "tinyint" => 1,
            "smallint" => 2,
            "int" => 4,
            "nvarchar" => 510,
            _ => 8,
        });
}
