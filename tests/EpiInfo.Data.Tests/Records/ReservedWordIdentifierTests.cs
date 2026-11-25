using System.Data;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using EpiInfo.Data.Metadata;
using EpiInfo.Data.Records;
using EpiInfo.Data.Sql;
using EpiInfo.Domain.Metadata;
using EpiInfo.Domain.Records;
using EpiInfo.Domain.Search;
using EpiInfo.Domain.Storage;
using EpiInfo.Domain.Values;
using EpiInfo.Tests.Shared;
using Microsoft.Data.SqlClient;

namespace EpiInfo.Data.Tests.Records;

/// <summary>
/// Integration tests on session temporary tables, for the identifiers and column types the seeded forms do not have.
/// Each test creates its <c>#</c> tables on one dedicated connection, reads their schema through
/// <see cref="SqlMetadataReader.ReadTempTableSchemaAsync"/>, builds an in-memory form over them (no <c>meta*</c> row
/// is written), writes and reads through <see cref="RecordSqlBuilder"/>, <see cref="SearchSqlBuilder"/> and
/// <see cref="SqlExecution"/>, and verifies with plain parameterised <see cref="SqlCommand"/> reads on the same
/// connection. The temporary tables live in <c>tempdb</c> for that session only and are dropped in <c>finally</c>, so
/// no seeded row, metadata row or schema object of the database is touched.
/// </summary>
/// <remarks>
/// Assertion messages never quote a parameter or field value; values are compared with
/// <see cref="Assert.True(bool, string)"/> and a fixed message.
/// </remarks>
[Trait("Category", "Integration")]
[Collection(DatabaseCollections.Database)]
public sealed class ReservedWordIdentifierTests
{
    private const string FixtureOperation = "test.reservedWordIdentifier";
    private const string StampLogonName = "ReservedWordIdentifierTests";

    // The in-memory form's ids. No seeded view has id 0; the form exists only in the test.
    private const int FixtureViewId = 0;
    private const int FixturePageId = 1;

    // metaFieldTypes ids and names, as testdata/sql/02_reference_data.sql seeds them.
    private const int TextTypeId = 1;
    private const string TextTypeName = "Text";
    private const int NumberTypeId = 5;
    private const string NumberTypeName = "Number";
    private const int OptionTypeId = 12;
    private const string OptionTypeName = "Option";

    // The items of every Option field: indexes 0, 1 and 2.
    private const string OptionItems = "A,B,C";

    // Field and column names of the fixtures.
    private const string BigCount = "BigCount";
    private const string SmallCount = "SmallCount";
    private const string Measure = "Measure";
    private const string TinyChoice = "TinyChoice";
    private const string SmallChoice = "SmallChoice";
    private const string IntChoice = "IntChoice";
    private const string Score = "Score";

    // Dapper's literal token for parameter p1. Dapper's own DynamicParameters would replace it in the command text with
    // the value of @p1, even inside a bracketed identifier.
    private const string BinderMarkerColumn = "{=p1}";

    // Session temporary tables, one pair per fixture.
    private const string ExactBase = "#ExactBase";
    private const string ExactPage = "#ExactPage";
    private const string SearchBase = "#SearchBase";
    private const string SearchPage = "#SearchPage";
    private const string MarkerBase = "#MarkerBase";
    private const string MarkerPage = "#MarkerPage";

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// A <c>bigint</c> number field and Option fields on <c>tinyint</c>, <c>smallint</c> and <c>int</c> columns
    /// are bound with the column's exact type and CLR value, stored exactly, and read back exactly, before and after
    /// an update to an adjacent integer that a double could not tell apart from the inserted one.
    /// </summary>
    [Theory]
    [InlineData(9007199254740993L, 9007199254740992L)]
    [InlineData(-9007199254740993L, -9007199254740992L)]
    [InlineData(long.MaxValue, long.MaxValue - 1)]
    [InlineData(long.MinValue, long.MinValue + 1)]
    public async Task IntegerColumns_WriteAndReadBackExactly(long inserted, long updated)
    {
        await TestDatabase.EnsureReachableAsync();
        using var timeout = new CancellationTokenSource(TestTimeout);
        var cancellationToken = timeout.Token;
        await using var writeLock = await DatabaseWriteLock.AcquireExclusiveAsync(cancellationToken);
        await using var connection = await TestDatabase.OpenConnectionAsync(cancellationToken);
        try
        {
            var fixture = await CreateFixtureAsync(
                connection,
                ExactBase,
                ExactPage,
                $"[{BigCount}] bigint, [{TinyChoice}] tinyint, [{SmallChoice}] smallint, [{IntChoice}] int",
                [
                    new FixtureField(BigCount, NumberTypeId, NumberTypeName),
                    new FixtureField(TinyChoice, OptionTypeId, OptionTypeName, OptionItems),
                    new FixtureField(SmallChoice, OptionTypeId, OptionTypeName, OptionItems),
                    new FixtureField(IntChoice, OptionTypeId, OptionTypeName, OptionItems),
                ],
                cancellationToken);

            var globalRecordId = await InsertBaseAsync(fixture, connection, cancellationToken);
            var insert = RecordSqlBuilder.InsertPage(
                fixture.Page,
                globalRecordId,
                fixture.PageColumns,
                Values(
                    (BigCount, FieldValue.FromInteger(inserted)),
                    (TinyChoice, FieldValue.FromIndex(2)),
                    (SmallChoice, FieldValue.FromIndex(1)),
                    (IntChoice, FieldValue.FromIndex(0))));

            // INSERT INTO [page] ([GlobalRecordId], [col1], ...) VALUES (@p0, @p1, ...): each column's value follows
            // the key's, in page-column order.
            AssertBound(insert.Parameters[1 + fixture.IndexOf(BigCount)], DbType.Int64, inserted, BigCount);
            AssertBound(insert.Parameters[1 + fixture.IndexOf(TinyChoice)], DbType.Byte, (byte)2, TinyChoice);
            AssertBound(insert.Parameters[1 + fixture.IndexOf(SmallChoice)], DbType.Int16, (short)1, SmallChoice);
            AssertBound(insert.Parameters[1 + fixture.IndexOf(IntChoice)], DbType.Int32, 0, IntChoice);

            Assert.Equal(1, await ExecuteAsync(fixture, connection, insert, cancellationToken));
            await AssertStoredExactlyAsync(fixture, connection, globalRecordId, inserted, 2, 1, 0, cancellationToken);

            var updates = RecordSqlBuilder.UpdatePageColumns(
                fixture.Page,
                globalRecordId,
                [
                    (fixture.Column(BigCount), FieldValue.FromInteger(updated)),
                    (fixture.Column(TinyChoice), FieldValue.FromIndex(0)),
                    (fixture.Column(SmallChoice), FieldValue.FromIndex(2)),
                    (fixture.Column(IntChoice), FieldValue.FromIndex(1)),
                ]);
            var update = Assert.Single(updates);

            // UPDATE [page] SET [colA] = @p0, ... WHERE [GlobalRecordId] = @pN: values in submitted order.
            AssertBound(update.Parameters[0], DbType.Int64, updated, BigCount);
            AssertBound(update.Parameters[1], DbType.Byte, (byte)0, TinyChoice);
            AssertBound(update.Parameters[2], DbType.Int16, (short)2, SmallChoice);
            AssertBound(update.Parameters[3], DbType.Int32, 1, IntChoice);

            Assert.Equal(1, await ExecuteAsync(fixture, connection, update, cancellationToken));
            await AssertStoredExactlyAsync(fixture, connection, globalRecordId, updated, 0, 2, 1, cancellationToken);
        }
        finally
        {
            await DropTablesAsync(connection, ExactPage, ExactBase);
        }
    }

    /// <summary>
    /// Find on integer and float number columns returns exactly the rows the criterion selects, with SQL Server never
    /// rounding a <c>bigint</c> column to <c>float</c>: adjacent values beyond 2^53 stay distinct for equality and
    /// every ordering operator; a criterion that holds no exact integer is bound as the decimal half-way inside its
    /// integer interval, even where its nearest double is whole; criteria beyond the <c>bigint</c> range fall on the
    /// correct side of every value, <c>long.MinValue</c> and <c>long.MaxValue</c> included; a JSON number criterion
    /// keeps the same exact interval; and a float column binds its criterion as before.
    /// </summary>
    /// <param name="field">The searched field.</param>
    /// <param name="criterion">The criterion text, as the API receives it, or a JSON number literal.</param>
    /// <param name="expectedRows">The labels of the rows it must return, in <c>UniqueKey</c> order.</param>
    /// <param name="expectedType">The type the criterion must be bound with.</param>
    /// <param name="expectedValue">
    /// The CLR value the criterion must be bound with; for <see cref="DbType.Decimal"/> its invariant text, because an
    /// attribute cannot hold a decimal constant.
    /// </param>
    /// <param name="jsonNumber">
    /// Whether <paramref name="criterion"/> is a JSON number literal, read from its UTF-8 bytes as JSON criteria are.
    /// </param>
    [Theory]
    [InlineData(BigCount, "9007199254740993", "B", DbType.Int64, 9007199254740993L)]
    [InlineData(BigCount, "<>9007199254740993", "ACDE", DbType.Int64, 9007199254740993L)]
    [InlineData(BigCount, "<9007199254740993", "AE", DbType.Int64, 9007199254740993L)]
    [InlineData(BigCount, ">9007199254740992", "BCD", DbType.Int64, 9007199254740992L)]
    [InlineData(BigCount, "<=9007199254740993", "ABE", DbType.Int64, 9007199254740993L)]
    [InlineData(BigCount, ">=9007199254740994", "CD", DbType.Int64, 9007199254740994L)]
    [InlineData(BigCount, ">=9223372036854775807", "D", DbType.Int64, long.MaxValue)]
    [InlineData(BigCount, "-9223372036854775808", "E", DbType.Int64, long.MinValue)]
    // 9007199254740992.5 has the nearest double 9007199254740992, yet is bound inside (9007199254740992, ...993).
    [InlineData(BigCount, "9007199254740992.5", "", DbType.Decimal, "9007199254740992.5")]
    [InlineData(BigCount, ">9007199254740992.5", "BCD", DbType.Decimal, "9007199254740992.5")]
    [InlineData(BigCount, "<9007199254740992.5", "AE", DbType.Decimal, "9007199254740992.5")]
    [InlineData(BigCount, "9007199254740992.5", "", DbType.Decimal, "9007199254740992.5", true)]
    [InlineData(BigCount, ">9223372036854775806.9", "D", DbType.Decimal, "9223372036854775806.5")]
    [InlineData(BigCount, "<9223372036854775807.5", "ABCDE", DbType.Decimal, "9223372036854775807.5")]
    [InlineData(BigCount, "<-9223372036854775807.5", "E", DbType.Decimal, "-9223372036854775807.5")]
    // Beyond the bigint range, whose nearest doubles 2^63 and -2^63 bigint values convert to, the criterion is bound
    // just beyond the range's end on its side.
    [InlineData(BigCount, "<9223372036854775808", "ABCDE", DbType.Decimal, "9223372036854775807.5")]
    [InlineData(BigCount, "9223372036854775808", "", DbType.Decimal, "9223372036854775807.5")]
    [InlineData(BigCount, "-9223372036854775809", "", DbType.Decimal, "-9223372036854775808.5")]
    [InlineData(BigCount, ">-9223372036854775809", "ABCDE", DbType.Decimal, "-9223372036854775808.5")]
    [InlineData(BigCount, ">-1e19", "ABCDE", DbType.Decimal, "-9223372036854775808.5")]
    [InlineData(BigCount, "<-1e19", "", DbType.Decimal, "-9223372036854775808.5")]
    [InlineData(SmallCount, "3", "B", DbType.Int64, 3L)]
    [InlineData(SmallCount, ">2.5", "B", DbType.Decimal, "2.5")]
    [InlineData(SmallCount, "2.5", "", DbType.Decimal, "2.5")]
    // Both have the nearest double 2, but lie inside (2, 3) and (1, 2).
    [InlineData(SmallCount, "2.0000000000000001", "", DbType.Decimal, "2.5")]
    [InlineData(SmallCount, "<2.0000000000000001", "A", DbType.Decimal, "2.5")]
    [InlineData(SmallCount, ">1.9999999999999999", "AB", DbType.Decimal, "1.5")]
    [InlineData(Measure, "37.5", "A", DbType.Double, 37.5d)]
    [InlineData(Measure, ">37.5", "B", DbType.Double, 37.5d)]
    public async Task NumberColumns_FindComparesCriteriaExactly(
        string field,
        string criterion,
        string expectedRows,
        DbType expectedType,
        object expectedValue,
        bool jsonNumber = false)
    {
        var expected = expectedType == DbType.Decimal
            ? decimal.Parse(Assert.IsType<string>(expectedValue), NumberStyles.Number, CultureInfo.InvariantCulture)
            : expectedValue;
        var criterionValue = jsonNumber ? JsonNumber(criterion) : FieldValue.FromText(criterion);

        await TestDatabase.EnsureReachableAsync();
        using var timeout = new CancellationTokenSource(TestTimeout);
        var cancellationToken = timeout.Token;
        await using var writeLock = await DatabaseWriteLock.AcquireExclusiveAsync(cancellationToken);
        await using var connection = await TestDatabase.OpenConnectionAsync(cancellationToken);
        try
        {
            var fixture = await CreateFixtureAsync(
                connection,
                SearchBase,
                SearchPage,
                $"[{BigCount}] bigint, [{SmallCount}] int, [{Measure}] float",
                [
                    new FixtureField(BigCount, NumberTypeId, NumberTypeName),
                    new FixtureField(SmallCount, NumberTypeId, NumberTypeName),
                    new FixtureField(Measure, NumberTypeId, NumberTypeName),
                ],
                cancellationToken);

            // Every row is active (InsertBase writes RecStatus 1) and has its page row, which Find joins.
            var rows = new (string Label, (string Name, FieldValue Value)[] Values)[]
            {
                ("A", [
                    (BigCount, FieldValue.FromInteger(9007199254740992L)),
                    (SmallCount, FieldValue.FromInteger(2)),
                    (Measure, FieldValue.FromNumber(37.5d)),
                ]),
                ("B", [
                    (BigCount, FieldValue.FromInteger(9007199254740993L)),
                    (SmallCount, FieldValue.FromInteger(3)),
                    (Measure, FieldValue.FromNumber(38.25d)),
                ]),
                ("C", [(BigCount, FieldValue.FromInteger(9007199254740994L))]),
                ("D", [(BigCount, FieldValue.FromInteger(long.MaxValue))]),
                ("E", [(BigCount, FieldValue.FromInteger(long.MinValue))]),
            };

            var labels = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (label, values) in rows)
            {
                var globalRecordId = await InsertBaseAsync(fixture, connection, cancellationToken);
                var insert = RecordSqlBuilder.InsertPage(
                    fixture.Page, globalRecordId, fixture.PageColumns, Values(values));
                Assert.Equal(1, await ExecuteAsync(fixture, connection, insert, cancellationToken));
                labels.Add(globalRecordId, label);
            }

            var parsed = SearchCriteriaParser.Parse(fixture.DataFields, [new SearchCriterion(field, criterionValue)]);
            Assert.True(parsed.IsSuccess, "The criterion must parse as a number criterion.");

            var search = SearchSqlBuilder.Search(
                fixture.Schema,
                fixture.DataFields,
                RecordQueryRules.GetGridColumns(fixture.DataFields),
                parsed.Conditions,
                includeDeleted: false,
                excludeGlobalRecordId: null);

            var bound = CriterionParameter(search.Statement);
            Assert.Equal(expectedType, bound.DbType);
            Assert.Null(bound.Size);
            Assert.IsType(expected.GetType(), bound.Value);
            Assert.True(expected.Equals(bound.Value), "The criterion is not bound with the expected value.");

            Task<List<string>> FindAsync(SqlStatement statement) => SqlExecution.ReadAsync(
                connection,
                transaction: null,
                FixtureOperation,
                statement,
                fixture.CommandTimeoutSeconds,
                fixture.Recorder,
                cancellationToken,
                static async (reader, token) =>
                {
                    var ids = new List<string>();
                    while (await reader.ReadAsync(token))
                    {
                        ids.Add(reader.GetString(SearchSqlBuilder.IdOrdinal));
                    }

                    return ids;
                });

            var found = await FindAsync(search.Statement);
            Assert.Equal(expectedRows, string.Concat(found.Select(id => labels[id])));

            // Packed, as a search beyond the scalar budget binds it, the criterion travels as a JSON array element and
            // selects exactly the same rows: bigint and decimal criteria decode to their own exact values.
            var packed = SearchSqlBuilder.Search(
                fixture.Schema,
                fixture.DataFields,
                RecordQueryRules.GetGridColumns(fixture.DataFields),
                parsed.Conditions,
                includeDeleted: false,
                excludeGlobalRecordId: null,
                scalarParameterBudget: 0);
            Assert.Contains("OPENJSON", packed.Statement.Text, StringComparison.Ordinal);

            var packedFound = await FindAsync(packed.Statement);
            Assert.Equal(expectedRows, string.Concat(packedFound.Select(id => labels[id])));
        }
        finally
        {
            await DropTablesAsync(connection, SearchPage, SearchBase);
        }
    }

    /// <summary>
    /// A page column named like Dapper's literal token for a numeric parameter of the same statement, <c>{=p1}</c>,
    /// is written by an insert and an update whose command text keeps the bracketed name exactly as the recorder saw
    /// it, which proves that the data layer's binder never rewrote the text after the observer was told about it.
    /// </summary>
    [Fact]
    public async Task BinderMarkerColumnName_IsNeverRewrittenAfterObservation()
    {
        await TestDatabase.EnsureReachableAsync();
        using var timeout = new CancellationTokenSource(TestTimeout);
        var cancellationToken = timeout.Token;
        await using var writeLock = await DatabaseWriteLock.AcquireExclusiveAsync(cancellationToken);
        await using var connection = await TestDatabase.OpenConnectionAsync(cancellationToken);
        try
        {
            // Score comes first, so @p1 of the insert is its number; the update sets the marker column first, so @p1
            // is the number there too.
            var fixture = await CreateFixtureAsync(
                connection,
                MarkerBase,
                MarkerPage,
                $"[{Score}] float, [{BinderMarkerColumn}] nvarchar(4000)",
                [
                    new FixtureField(Score, NumberTypeId, NumberTypeName),
                    new FixtureField(BinderMarkerColumn, TextTypeId, TextTypeName),
                ],
                cancellationToken);
            const string quotedMarker = "[" + BinderMarkerColumn + "]";
            const string insertedText = "Inserted beside a binder-marker column name";
            const string updatedText = "Updated beside a binder-marker column name";

            var globalRecordId = await InsertBaseAsync(fixture, connection, cancellationToken);
            var insert = RecordSqlBuilder.InsertPage(
                fixture.Page,
                globalRecordId,
                fixture.PageColumns,
                Values((Score, FieldValue.FromNumber(12.5d)), (BinderMarkerColumn, FieldValue.FromText(insertedText))));
            Assert.Equal(DbType.Double, insert.Parameters[1].DbType);
            Assert.Contains(quotedMarker, insert.Text, StringComparison.Ordinal);
            Assert.Equal(1, await ExecuteAsync(fixture, connection, insert, cancellationToken));
            AssertObservedAsSent(fixture.Recorder, insert, BinderMarkerColumn);
            var afterInsert = await ReadMarkerColumnAsync(connection, globalRecordId, cancellationToken);
            Assert.True(
                string.Equals(insertedText, afterInsert, StringComparison.Ordinal),
                "The marker column does not hold the inserted text.");

            var update = Assert.Single(RecordSqlBuilder.UpdatePageColumns(
                fixture.Page,
                globalRecordId,
                [
                    (fixture.Column(BinderMarkerColumn), FieldValue.FromText(updatedText)),
                    (fixture.Column(Score), FieldValue.FromNumber(13.25d)),
                ]));
            Assert.Equal(DbType.Double, update.Parameters[1].DbType);
            Assert.Contains(quotedMarker, update.Text, StringComparison.Ordinal);
            Assert.Equal(1, await ExecuteAsync(fixture, connection, update, cancellationToken));
            AssertObservedAsSent(fixture.Recorder, update, BinderMarkerColumn);
            var afterUpdate = await ReadMarkerColumnAsync(connection, globalRecordId, cancellationToken);
            Assert.True(
                string.Equals(updatedText, afterUpdate, StringComparison.Ordinal),
                "The marker column does not hold the updated text.");
        }
        finally
        {
            await DropTablesAsync(connection, MarkerPage, MarkerBase);
        }
    }

    // Creates the base and page tables on the test's connection, reads them back through the reader's temporary-table
    // overload, and builds the in-memory form over the reader-built tables. Constraints are unnamed, so the session's
    // tables never collide with another session's.
    private static async Task<Fixture> CreateFixtureAsync(
        SqlConnection connection,
        string baseName,
        string pageName,
        string pageColumnDefinitions,
        IReadOnlyList<FixtureField> fields,
        CancellationToken cancellationToken)
    {
        // The base table has the seeded base tables' columns (testdata/sql/05_data_tables.sql).
        await ExecuteDdlAsync(
            connection,
            $"""
            CREATE TABLE [{baseName}] (
                [UniqueKey] int IDENTITY(1,1) PRIMARY KEY NOT NULL,
                [RecStatus] smallint NOT NULL DEFAULT 1,
                [GlobalRecordId] nvarchar(255) NOT NULL,
                [FirstSaveLogonName] nvarchar(255),
                [FirstSaveTime] datetime,
                [LastSaveLogonName] nvarchar(255),
                [LastSaveTime] datetime,
                [FKEY] nvarchar(255));
            CREATE TABLE [{pageName}] (
                [GlobalRecordId] nvarchar(255) PRIMARY KEY NOT NULL,
                {pageColumnDefinitions});
            """,
            cancellationToken);

        var options = new DatabaseOptions(TestDatabase.ConnectionString);
        var recorder = new SqlCommandRecorder();
        var reader = new SqlMetadataReader(new SqlConnectionFactory(options), options, recorder);
        var baseTable = await reader.ReadTempTableSchemaAsync(connection, baseName, cancellationToken);
        var pageTable = await reader.ReadTempTableSchemaAsync(connection, pageName, cancellationToken);

        var page = new PageMetadata(FixturePageId, "Page 1", Position: 0, pageName);
        var fieldRows = new List<FieldMetadata>(fields.Count);
        for (var index = 0; index < fields.Count; index++)
        {
            var field = fields[index];
            var column = pageTable.FindColumn(field.Name)
                ?? throw new InvalidOperationException("The reader did not return a fixture column.");
            fieldRows.Add(new FieldMetadata
            {
                FieldId = index + 1,
                Name = field.Name,
                FieldTypeId = field.FieldTypeId,
                FieldTypeName = field.FieldTypeName,
                Prompt = field.Name,
                TabIndex = index + 1,
                PageId = FixturePageId,
                List = field.List,
                Column = column.Metadata,
            });
        }

        var form = new FormMetadata(FixtureViewId, "ReservedWordFixture", baseName, [page], fieldRows);
        var dataFields = FormDefinitionFactory.GetDataFields(form);
        Assert.Equal(fields.Count, dataFields.Count);

        var pageSchema = new PageTableSchema(page, pageTable);
        var schema = new FormSchema(FixtureViewId, baseTable, [pageSchema]);
        var pageColumns = dataFields
            .Select((field, ordinal) => RecordSqlBuilder.ResolveDataColumn(schema, field, ordinal))
            .ToArray();

        recorder.Clear();
        return new Fixture(schema, pageSchema, dataFields, pageColumns, recorder, options.CommandTimeoutSeconds);
    }

    // Inserts an active base row with a new id through the builder and returns the id.
    private static async Task<string> InsertBaseAsync(
        Fixture fixture,
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        var globalRecordId = RecordSqlBuilder.NewGlobalRecordId();
        var stamp = new SaveStamp(StampLogonName, SaveStamp.TruncateToSeconds(DateTime.Now));
        var uniqueKey = await SqlExecution.ExecuteScalarAsync<int>(
            connection,
            transaction: null,
            FixtureOperation,
            RecordSqlBuilder.InsertBase(fixture.Schema, globalRecordId, stamp),
            fixture.CommandTimeoutSeconds,
            fixture.Recorder,
            cancellationToken);
        Assert.True(uniqueKey > 0, "The base insert must return the new UniqueKey.");
        return globalRecordId;
    }

    private static Task<int> ExecuteAsync(
        Fixture fixture,
        SqlConnection connection,
        SqlStatement statement,
        CancellationToken cancellationToken) =>
        SqlExecution.ExecuteAsync(
            connection,
            transaction: null,
            FixtureOperation,
            statement,
            fixture.CommandTimeoutSeconds,
            fixture.Recorder,
            cancellationToken);

    // Verifies the stored values twice: by a plain parameterised read, which must return exactly the column's CLR
    // type, and by the record read the API uses, converted back through ColumnValueConverter.
    private static async Task AssertStoredExactlyAsync(
        Fixture fixture,
        SqlConnection connection,
        string globalRecordId,
        long expectedCount,
        int expectedTiny,
        int expectedSmall,
        int expectedInt,
        CancellationToken cancellationToken)
    {
        await using (var command = new SqlCommand(
            $"SELECT [{BigCount}], [{TinyChoice}], [{SmallChoice}], [{IntChoice}] FROM [{ExactPage}] " +
            "WHERE [GlobalRecordId] = @id",
            connection))
        {
            command.Parameters.Add(new SqlParameter("@id", SqlDbType.NVarChar, 255) { Value = globalRecordId });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            Assert.True(await reader.ReadAsync(cancellationToken), "The page row was not found.");
            AssertExact(expectedCount, reader.GetValue(0), BigCount);
            AssertExact((byte)expectedTiny, reader.GetValue(1), TinyChoice);
            AssertExact((short)expectedSmall, reader.GetValue(2), SmallChoice);
            AssertExact(expectedInt, reader.GetValue(3), IntChoice);
            Assert.False(await reader.ReadAsync(cancellationToken), "The page holds more than one row for the id.");
        }

        var select = RecordSqlBuilder.RecordById(fixture.Schema, fixture.DataFields, globalRecordId);
        var values = await SqlExecution.ReadAsync(
            connection,
            transaction: null,
            FixtureOperation,
            select.Statement,
            fixture.CommandTimeoutSeconds,
            fixture.Recorder,
            cancellationToken,
            async (reader, token) =>
            {
                var read = new Dictionary<string, FieldValue>(StringComparer.Ordinal);
                if (!await reader.ReadAsync(token))
                {
                    return read;
                }

                foreach (var column in select.Columns)
                {
                    read.Add(
                        column.Field.Name,
                        ColumnValueConverter.FromDbValue(
                            column.Column.Metadata, column.Field.FieldType, reader.GetValue(column.Ordinal)));
                }

                return read;
            });

        Assert.Equal(fixture.DataFields.Count, values.Count);
        Assert.True(
            values[BigCount].TryGetInteger(out var count) && count == expectedCount,
            BigCount + " read back through the converter is not the exact integer written.");
        AssertIndex(expectedTiny, values[TinyChoice], TinyChoice);
        AssertIndex(expectedSmall, values[SmallChoice], SmallChoice);
        AssertIndex(expectedInt, values[IntChoice], IntChoice);
    }

    // The marker column read by a plain parameterised command that names the bracketed identifier itself.
    private static async Task<string?> ReadMarkerColumnAsync(
        SqlConnection connection,
        string globalRecordId,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            $"SELECT [{BinderMarkerColumn}] FROM [{MarkerPage}] WHERE [GlobalRecordId] = @id",
            connection);
        command.Parameters.Add(new SqlParameter("@id", SqlDbType.NVarChar, 255) { Value = globalRecordId });
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value as string;
    }

    // The recorder saw exactly the statement's text, holding the bracketed marker, and the command succeeded.
    private static void AssertObservedAsSent(SqlCommandRecorder recorder, SqlStatement statement, string identifier)
    {
        var observed = Assert.Single(
            recorder.Commands,
            command => string.Equals(command.CommandText, statement.Text, StringComparison.Ordinal));
        Assert.True(observed.Completed && observed.Succeeded, "The observed command did not succeed.");
        Assert.Contains(identifier, SqlCommandRecorder.BracketedIdentifiers(observed.CommandText));
    }

    // A JSON number criterion as the API reads it: FieldValueJsonConverter parses the literal's UTF-8 bytes.
    private static FieldValue JsonNumber(string literal)
    {
        Assert.True(
            FieldValueCoercion.TryParseNumber(Encoding.UTF8.GetBytes(literal), out var value),
            "The JSON number literal must parse.");
        return value;
    }

    // With one alternative and no excluded id, the outer WHERE clause names exactly one parameter: the criterion's.
    private static SqlParameterValue CriterionParameter(SqlStatement statement)
    {
        var where = statement.Text[statement.Text.LastIndexOf(" WHERE ", StringComparison.Ordinal)..];
        return Assert.Single(
            statement.Parameters,
            parameter => Regex.IsMatch(where, Regex.Escape(parameter.Name) + "(?![0-9])"));
    }

    private static void AssertBound<T>(SqlParameterValue parameter, DbType expectedType, T expected, string column)
        where T : struct
    {
        Assert.Equal(expectedType, parameter.DbType);
        Assert.Null(parameter.Size);
        AssertExact(expected, parameter.Value, column);
    }

    // Compares without quoting either value, so no field or parameter value reaches the test output.
    private static void AssertExact<T>(T expected, object? actual, string column)
        where T : struct
    {
        var typed = Assert.IsType<T>(actual);
        Assert.True(EqualityComparer<T>.Default.Equals(expected, typed), column + " does not hold the exact value.");
    }

    private static void AssertIndex(int expected, FieldValue value, string column) =>
        Assert.True(
            value.TryGetIndex(out var index) && index == expected,
            column + " read back through the converter is not the option index written.");

    private static Dictionary<string, FieldValue> Values(params (string Name, FieldValue Value)[] entries)
    {
        var values = new Dictionary<string, FieldValue>(StringComparer.Ordinal);
        foreach (var (name, value) in entries)
        {
            values.Add(name, value);
        }

        return values;
    }

    private static async Task ExecuteDdlAsync(SqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // Runs even after a timeout, on the connection that owns the tables; a closed connection has dropped them already.
    private static async Task DropTablesAsync(SqlConnection connection, params string[] tableNames)
    {
        if (connection.State != ConnectionState.Open)
        {
            return;
        }

        foreach (var tableName in tableNames)
        {
            await ExecuteDdlAsync(connection, $"DROP TABLE IF EXISTS [{tableName}];", CancellationToken.None);
        }
    }

    private sealed record FixtureField(string Name, int FieldTypeId, string FieldTypeName, string? List = null);

    // The in-memory form over reader-built temporary tables, with the recorder every fixture command reports to.
    private sealed record Fixture(
        FormSchema Schema,
        PageTableSchema Page,
        DataFieldSet DataFields,
        IReadOnlyList<ProjectedColumn> PageColumns,
        SqlCommandRecorder Recorder,
        int CommandTimeoutSeconds)
    {
        public ProjectedColumn Column(string name) => PageColumns[IndexOf(name)];

        public int IndexOf(string name)
        {
            for (var index = 0; index < PageColumns.Count; index++)
            {
                if (string.Equals(PageColumns[index].Field.Name, name, StringComparison.Ordinal))
                {
                    return index;
                }
            }

            throw new InvalidOperationException("The fixture has no such column.");
        }
    }
}
