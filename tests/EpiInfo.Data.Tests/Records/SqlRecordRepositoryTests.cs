using System.Data.Common;
using EpiInfo.Data.Metadata;
using EpiInfo.Data.Records;
using EpiInfo.Data.Sql;
using EpiInfo.Domain.Forms;
using EpiInfo.Domain.Metadata;
using EpiInfo.Tests.Shared;
using Microsoft.Data.SqlClient;

namespace EpiInfo.Data.Tests.Records;

/// <summary>
/// Integration tests of the record statements the repository runs, against the seeded forms of the rehearsal
/// database. These cases pin record identity: every lookup by <c>GlobalRecordId</c>, the locked read before a write
/// included, matches a stored id exactly, so a spelling that differs from it only by trailing spaces, within or beyond
/// the column's width, finds no row; and the base-row reads return the id as stored, the spelling every later
/// statement binds.
/// </summary>
/// <remarks>
/// The tests only read. The locked read runs in a transaction that is rolled back, and the record they look up is the
/// seeded row with the lowest <c>UniqueKey</c>, chosen by SQL rather than hard-coded. Failure messages are fixed text,
/// so no id reaches the output except through an equality assertion.
/// </remarks>
[Collection(DatabaseCollections.Database)]
[Trait("Category", "Integration")]
public sealed class SqlRecordRepositoryTests
{
    // The fixed operation name every command of these tests reports to the recorder.
    private const string Operation = "test.recordIdentity";

    // Trailing spaces that keep the padded id within the seeded nvarchar(255) column.
    private const int WithinWidthPadding = 3;

    // Trailing spaces that take the padded id beyond the seeded nvarchar(255) column.
    private const int OverWidthPadding = 300;

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
