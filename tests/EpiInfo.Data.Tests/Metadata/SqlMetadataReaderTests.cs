using System.Data;
using System.Globalization;
using EpiInfo.Data.Metadata;
using EpiInfo.Data.Sql;
using EpiInfo.Domain.Forms;
using EpiInfo.Domain.Metadata;
using EpiInfo.Domain.Values;
using EpiInfo.Tests.Shared;
using Microsoft.Data.SqlClient;
using Xunit.Abstractions;

namespace EpiInfo.Data.Tests.Metadata;

/// <summary>
/// Integration tests of <see cref="SqlMetadataReader"/> against the rehearsal database. Every expected value comes
/// from a direct query on a separate verification connection, never from the reader. The tests only read: they send
/// no DDL or DML, take no <c>DatabaseWriteLock</c>, and build any synthetic field in memory, so no metadata row is
/// written.
/// </summary>
[Trait("Category", "Integration")]
[Collection(DatabaseCollections.Database)]
public sealed class SqlMetadataReaderTests : IAsyncLifetime
{
    private const string LegalValuesTypeName = "Legal Values";

    // FoodHistory's LooseStoolsPerDay field names this code table and text column, spelled as metaFields spells them;
    // the catalogue spells the column loosestoolsperday.
    private const string LooseStoolsTable = "codeloosestoolsperday";
    private const string LooseStoolsTextColumn = "LooseStoolsPerDay";

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(60);

    private readonly ITestOutputHelper _output;

    public SqlMetadataReaderTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public Task InitializeAsync() => TestDatabase.EnsureReachableAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// The options of a whole form cost two commands, the catalogue read and one code-table command holding one
    /// <c>SELECT</c> per distinct code table, and each field's options are its code-table rows in the order SQL Server
    /// returns them, each with its text as both value and label.
    /// </summary>
    [Fact]
    public async Task GetOptionsAsync_FoodHistory_ReadsEveryCodeTableInOneBatchInStoredRowOrder()
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        CancellationToken token = cancellation.Token;
        var recorder = new SqlCommandRecorder();
        SqlMetadataReader reader = CreateReader(recorder);
        await using SqlConnection verification = await TestDatabase.OpenConnectionAsync(token);

        int viewId = await ViewIdAsync(verification, "FoodHistory", token);
        IReadOnlyList<LegalValuesField> expectedFields = await LegalValuesFieldsAsync(verification, viewId, token);
        Assert.Equal(
            ["EthnicityGroup", "LooseStoolsPerDay", "Sex"],
            expectedFields.Select(field => field.Name).Order(StringComparer.Ordinal));

        FormMetadata? form = await reader.GetFormMetadataAsync(viewId, token);
        Assert.NotNull(form);
        recorder.Clear();

        IReadOnlyDictionary<string, IReadOnlyList<FieldOption>> options = await reader.GetOptionsAsync(form, token);

        IReadOnlyList<RecordedCommand> commands = recorder.Commands;
        foreach (RecordedCommand command in commands)
        {
            _output.WriteLine(command.ToString());
        }

        Assert.Equal(2, commands.Count);
        Assert.All(commands, command => Assert.True(command.Succeeded));
        RecordedCommand codeTableRead = commands[1];
        Assert.Empty(codeTableRead.Parameters);
        Assert.DoesNotContain("ORDER BY", codeTableRead.CommandText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(expectedFields.Count, CountOccurrences(codeTableRead.CommandText, "SELECT "));

        Assert.Equal(
            expectedFields.Select(field => field.Name).Order(StringComparer.OrdinalIgnoreCase),
            options.Keys.Order(StringComparer.OrdinalIgnoreCase));
        foreach (LegalValuesField field in expectedFields)
        {
            (string table, string column) = await ResolveCodeColumnAsync(
                verification,
                field.SourceTableName,
                field.TextColumnName,
                token);
            Assert.Contains(
                "SELECT " + Bracket(column) + " FROM " + Bracket(table) + ";",
                codeTableRead.CommandText,
                StringComparison.Ordinal);

            IReadOnlyList<string?> rows = await ReadColumnAsync(verification, table, column, token);
            Assert.NotEmpty(rows);
            IReadOnlyList<FieldOption> fieldOptions = options[field.Name];
            Assert.Equal(rows, fieldOptions.Select(option => option.Label));
            Assert.All(fieldOptions, option => Assert.Equal(FieldValue.FromText(option.Label), option.Value));
        }

        Assert.Empty(recorder.Writes());
        Assert.NotEmpty(recorder.Commands);
        Assert.All(recorder.Commands, command => Assert.False(command.IsWrite));
    }

    /// <summary>
    /// A form whose metadata holds no Legal Values field gets an empty map without any command, because nothing names
    /// a code table to read.
    /// </summary>
    [Fact]
    public async Task GetOptionsAsync_CaseReport_ReturnsAnEmptyMapAndSendsNoCommand()
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        CancellationToken token = cancellation.Token;
        var recorder = new SqlCommandRecorder();
        SqlMetadataReader reader = CreateReader(recorder);
        await using SqlConnection verification = await TestDatabase.OpenConnectionAsync(token);

        int viewId = await ViewIdAsync(verification, "CaseReport", token);
        Assert.Empty(await LegalValuesFieldsAsync(verification, viewId, token));

        FormMetadata? form = await reader.GetFormMetadataAsync(viewId, token);
        Assert.NotNull(form);
        recorder.Clear();

        IReadOnlyDictionary<string, IReadOnlyList<FieldOption>> options = await reader.GetOptionsAsync(form, token);

        Assert.Empty(options);
        Assert.Empty(recorder.Writes());
        Assert.Empty(recorder.Commands);
    }

    // The seeded loosestoolsperday texts in two cultures' string sort. The empty name is the invariant culture, which
    // ranks "-" before "0", so "1-3" comes first. Thai collation ignores punctuation, so it compares "10+" as 10 and
    // "1-3" as 13 and puts "10+" first.
    public static TheoryData<string, string[]> SortedLooseStoolsCases => new()
    {
        { "", ["1-3 per day", "10+ per day", "4-6 per day", "7-10 per day"] },
        { "th-TH", ["10+ per day", "1-3 per day", "4-6 per day", "7-10 per day"] },
    };

    /// <summary>
    /// With <c>Sort</c> set the reader itself orders the texts, under the current culture at the read with string
    /// sort, so a read straight from the reader, with no form-definition step after it, already returns the sorted
    /// list, and the same rows sort differently when the current culture does.
    /// </summary>
    [Theory]
    [MemberData(nameof(SortedLooseStoolsCases))]
    public async Task ReadOptionsAsync_SortedField_ReturnsTheTextsSortedByTheReader(
        string cultureName,
        string[] expected)
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        CancellationToken token = cancellation.Token;
        var recorder = new SqlCommandRecorder();
        SqlMetadataReader reader = CreateReader(recorder);
        await using SqlConnection connection = await TestDatabase.OpenConnectionAsync(token);

        // The precondition fails, rather than the read, on a host whose globalization data lacks the culture's
        // collation.
        CultureInfo culture = CultureInfo.GetCultureInfo(cultureName);
        Assert.All(
            expected.Zip(expected.Skip(1)),
            pair => Assert.True(
                culture.CompareInfo.Compare(pair.First, pair.Second, CompareOptions.StringSort) < 0,
                $"The {culture.DisplayName} culture does not sort \"{pair.First}\" before \"{pair.Second}\"."));

        IReadOnlyList<FieldOption> options;
        CultureInfo original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = culture;
        try
        {
            options = await reader.ReadOptionsAsync(
                connection,
                transaction: null,
                LooseStoolsField(LooseStoolsTable, sort: true),
                token);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }

        Assert.Equal(expected, options.Select(option => option.Label));
        Assert.Equal(expected.Select(FieldValue.FromText), options.Select(option => option.Value));
        Assert.Equal(2, recorder.Commands.Count);
        Assert.Empty(recorder.Writes());
        Assert.NotEmpty(recorder.Commands);
        Assert.All(recorder.Commands, command => Assert.False(command.IsWrite));
    }

    /// <summary>
    /// Without <c>Sort</c>, a field naming its code table exactly as stored gets every row of it, in the order SQL
    /// Server returns them.
    /// </summary>
    [Fact]
    public async Task ReadOptionsAsync_UnsortedFieldNamingItsTableExactly_ReturnsEveryRowInStoredOrder()
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        CancellationToken token = cancellation.Token;
        var recorder = new SqlCommandRecorder();
        SqlMetadataReader reader = CreateReader(recorder);
        await using SqlConnection connection = await TestDatabase.OpenConnectionAsync(token);

        (string table, string column) = await ResolveCodeColumnAsync(
            connection,
            LooseStoolsTable,
            LooseStoolsTextColumn,
            token);
        IReadOnlyList<string?> rows = await ReadColumnAsync(connection, table, column, token);
        Assert.Equal(4, rows.Count);

        IReadOnlyList<FieldOption> options = await reader.ReadOptionsAsync(
            connection,
            transaction: null,
            LooseStoolsField(LooseStoolsTable, sort: false),
            token);

        Assert.Equal(rows, options.Select(option => option.Label));
        Assert.All(options, option => Assert.Equal(FieldValue.FromText(option.Label), option.Value));
        Assert.Equal(2, recorder.Commands.Count);
        Assert.Empty(recorder.Writes());
        Assert.NotEmpty(recorder.Commands);
        Assert.All(recorder.Commands, command => Assert.False(command.IsWrite));
    }

    /// <summary>
    /// A padded <c>SourceTableName</c> is looked up exactly as stored, as legacy looks it up, so it never reaches the
    /// unpadded code table: the catalogue read runs and no code-table read follows.
    /// </summary>
    [Theory]
    [InlineData(" " + LooseStoolsTable)]
    [InlineData(LooseStoolsTable + " ")]
    [InlineData(LooseStoolsTable + "\t")]
    public async Task ReadOptionsAsync_PaddedTableName_MatchesNoCodeTable(string sourceTableName)
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        CancellationToken token = cancellation.Token;
        var recorder = new SqlCommandRecorder();
        SqlMetadataReader reader = CreateReader(recorder);
        await using SqlConnection connection = await TestDatabase.OpenConnectionAsync(token);

        IReadOnlyList<FieldOption> options = await reader.ReadOptionsAsync(
            connection,
            transaction: null,
            LooseStoolsField(sourceTableName, sort: false),
            token);

        Assert.Empty(options);
        Assert.Single(recorder.Commands);
        Assert.Empty(recorder.Writes());
        Assert.NotEmpty(recorder.Commands);
        Assert.All(recorder.Commands, command => Assert.False(command.IsWrite));
    }

    /// <summary>A field naming no code table, or only white space, gets no options and sends no command.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public async Task ReadOptionsAsync_MissingOrBlankTableName_ReturnsNoOptionsAndSendsNoCommand(string? sourceTableName)
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        CancellationToken token = cancellation.Token;
        var recorder = new SqlCommandRecorder();
        SqlMetadataReader reader = CreateReader(recorder);
        await using SqlConnection connection = await TestDatabase.OpenConnectionAsync(token);

        IReadOnlyList<FieldOption> options = await reader.ReadOptionsAsync(
            connection,
            transaction: null,
            LooseStoolsField(sourceTableName, sort: false),
            token);

        Assert.Empty(options);
        Assert.Empty(recorder.Writes());
        Assert.Empty(recorder.Commands);
    }

    private static SqlMetadataReader CreateReader(SqlCommandRecorder recorder)
    {
        var options = new DatabaseOptions(TestDatabase.ConnectionString);
        return new SqlMetadataReader(new SqlConnectionFactory(options), options, recorder);
    }

    // An in-memory Legal Values field; it is never stored, so its id is synthetic.
    private static FieldMetadata LooseStoolsField(string? sourceTableName, bool sort) => new()
    {
        FieldId = -1,
        Name = "LooseStoolsPerDay",
        FieldTypeId = 17,
        FieldTypeName = LegalValuesTypeName,
        SourceTableName = sourceTableName,
        TextColumnName = LooseStoolsTextColumn,
        Sort = sort,
    };

    private static async Task<int> ViewIdAsync(SqlConnection connection, string formName, CancellationToken token)
    {
        await using var command = new SqlCommand("SELECT [ViewId] FROM [metaViews] WHERE [Name] = @name;", connection);
        command.Parameters.Add("@name", SqlDbType.NVarChar, 64).Value = formName;

        object? viewId = await command.ExecuteScalarAsync(token);
        return Assert.IsType<int>(viewId);
    }

    private static async Task<IReadOnlyList<LegalValuesField>> LegalValuesFieldsAsync(
        SqlConnection connection,
        int viewId,
        CancellationToken token)
    {
        const string text = """
            SELECT f.[Name], f.[SourceTableName], f.[TextColumnName]
            FROM [metaFields] AS f
            INNER JOIN [metaFieldTypes] AS t ON t.[FieldTypeId] = f.[FieldTypeId]
            WHERE f.[ViewId] = @viewId AND t.[Name] = @typeName
            ORDER BY f.[FieldId];
            """;
        await using var command = new SqlCommand(text, connection);
        command.Parameters.Add("@viewId", SqlDbType.Int).Value = viewId;
        command.Parameters.Add("@typeName", SqlDbType.NVarChar, 50).Value = LegalValuesTypeName;

        var fields = new List<LegalValuesField>();
        await using SqlDataReader reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            Assert.False(reader.IsDBNull(1), "A seeded Legal Values field names no code table.");
            Assert.False(reader.IsDBNull(2), "A seeded Legal Values field names no text column.");
            fields.Add(new LegalValuesField(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return fields;
    }

    // The catalogue spelling of a code table and its text column, matched ignoring case as the metadata spells them
    // differently from the catalogue.
    private static async Task<(string Table, string Column)> ResolveCodeColumnAsync(
        SqlConnection connection,
        string tableName,
        string columnName,
        CancellationToken token)
    {
        const string text = """
            SELECT t.[name], c.[name]
            FROM sys.tables AS t
            INNER JOIN sys.columns AS c ON c.[object_id] = t.[object_id]
            WHERE t.[name] COLLATE Latin1_General_100_CI_AS = @table COLLATE Latin1_General_100_CI_AS
              AND c.[name] COLLATE Latin1_General_100_CI_AS = @column COLLATE Latin1_General_100_CI_AS;
            """;
        await using var command = new SqlCommand(text, connection);
        command.Parameters.Add("@table", SqlDbType.NVarChar, 128).Value = tableName;
        command.Parameters.Add("@column", SqlDbType.NVarChar, 128).Value = columnName;

        var matches = new List<(string Table, string Column)>();
        await using SqlDataReader reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            matches.Add((reader.GetString(0), reader.GetString(1)));
        }

        return Assert.Single(matches);
    }

    // The column's rows with the unsorted query legacy uses, so they come back in the order SQL Server returns them.
    private static async Task<IReadOnlyList<string?>> ReadColumnAsync(
        SqlConnection connection,
        string table,
        string column,
        CancellationToken token)
    {
        await using var command = new SqlCommand("SELECT " + Bracket(column) + " FROM " + Bracket(table) + ";", connection);

        var rows = new List<string?>();
        await using SqlDataReader reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            rows.Add(reader.IsDBNull(0) ? null : reader.GetString(0));
        }

        return rows;
    }

    // The verification queries quote catalogue names themselves, independently of SqlIdentifier.
    private static string Bracket(string name) => "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        for (int index = text.IndexOf(value, StringComparison.Ordinal);
            index >= 0;
            index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private sealed record LegalValuesField(string Name, string SourceTableName, string TextColumnName);
}

/// <summary>
/// Unit tests of <see cref="SqlMetadataReader.ToOptions"/>, where a code-table column's texts become a Legal Values
/// field's options and where the one sort of a <c>Sort</c> = 1 list happens. They need no database. Each passes its
/// culture explicitly and never changes the current culture.
/// </summary>
public sealed class SqlMetadataReaderToOptionsTests
{
    // "Aland" with U+00C5, A with ring above: Swedish sorts that letter after Z as a letter of its own; English and the
    // invariant culture sort it with A.
    private const string Aland = "\u00c5land";

    public static TheoryData<string?[], string[]> UnsortedCases => new()
    {
        // A SQL NULL is skipped; every other text keeps its first position.
        { [null, "b", null, "a"], ["b", "a"] },
        // The empty text and white-space texts are stored choices, kept untrimmed.
        { ["", "  ", "x", "\t"], ["", "  ", "x", "\t"] },
        // Case variants are distinct choices.
        { ["a", "A"], ["a", "A"] },
        // Only an exact repeat collapses, at its first occurrence; a padded text is a distinct choice.
        { ["x", "y", "x", "y ", "y"], ["x", "y", "y "] },
    };

    public static TheoryData<string?[], string[]> SortedCases => new()
    {
        // String sort under the invariant culture is case-sensitive: a before A, then b before B.
        { ["b", "A", "a", "B"], ["a", "A", "b", "B"] },
        // The seeded loosestoolsperday rows, in their stored order.
        {
            ["1-3 per day", "4-6 per day", "7-10 per day", "10+ per day"],
            ["1-3 per day", "10+ per day", "4-6 per day", "7-10 per day"]
        },
        // NULLs, blank texts and exact repeats follow the unsorted rule before the sort.
        { ["M", null, "  ", "F", "M", ""], ["", "  ", "F", "M"] },
    };

    // The same two texts, in either stored order, under a culture that sorts U+00C5 after Z and two that sort it with
    // A. The empty name is the invariant culture.
    public static TheoryData<string, string[], string[]> CultureOrderCases => new()
    {
        { "sv-SE", ["Zebra", Aland], ["Zebra", Aland] },
        { "sv-SE", [Aland, "Zebra"], ["Zebra", Aland] },
        { "en-US", ["Zebra", Aland], [Aland, "Zebra"] },
        { "en-US", [Aland, "Zebra"], [Aland, "Zebra"] },
        { "", ["Zebra", Aland], [Aland, "Zebra"] },
        { "", [Aland, "Zebra"], [Aland, "Zebra"] },
    };

    [Theory]
    [MemberData(nameof(UnsortedCases))]
    public void ToOptions_Unsorted_KeepsEachDistinctStoredTextAtItsFirstPosition(string?[] storedTexts, string[] expected)
    {
        IReadOnlyList<FieldOption> options =
            SqlMetadataReader.ToOptions(storedTexts, sort: false, CultureInfo.InvariantCulture);

        Assert.Equal(expected.Select(TextOption), options);
    }

    [Theory]
    [MemberData(nameof(SortedCases))]
    public void ToOptions_Sorted_OrdersTheDistinctTextsByInvariantStringSort(string?[] storedTexts, string[] expected)
    {
        IReadOnlyList<FieldOption> options =
            SqlMetadataReader.ToOptions(storedTexts, sort: true, CultureInfo.InvariantCulture);

        Assert.Equal(expected.Select(TextOption), options);
    }

    // The sort follows the culture it is given, as legacy follows the current culture of its process.
    [Theory]
    [MemberData(nameof(CultureOrderCases))]
    public void ToOptions_Sorted_OrdersTheTextsByTheGivenCulture(
        string cultureName,
        string[] storedTexts,
        string[] expected)
    {
        // The precondition fails, rather than the sort, on a host whose globalization data lacks the culture's
        // collation.
        CultureInfo culture = CultureInfo.GetCultureInfo(cultureName);
        Assert.True(
            culture.CompareInfo.Compare(expected[0], expected[1], CompareOptions.StringSort) < 0,
            $"The {culture.DisplayName} culture does not sort \"{expected[0]}\" before \"{expected[1]}\".");

        IReadOnlyList<FieldOption> options = SqlMetadataReader.ToOptions(storedTexts, sort: true, culture);

        Assert.Equal(expected.Select(TextOption), options);
    }

    // The invariant culture ranks the decomposed and the precomposed e-acute equal, so only the ordinal tie-break fixes
    // their order, whichever of them the code table holds first.
    [Theory]
    [InlineData("e\u0301", "\u00e9")]
    [InlineData("\u00e9", "e\u0301")]
    public void ToOptions_Sorted_OrdersTextsTheCultureRanksEqualOrdinally(string first, string second)
    {
        Assert.Equal(0, CultureInfo.InvariantCulture.CompareInfo.Compare(first, second, CompareOptions.StringSort));

        IReadOnlyList<FieldOption> options =
            SqlMetadataReader.ToOptions([first, second], sort: true, CultureInfo.InvariantCulture);

        Assert.Equal([TextOption("e\u0301"), TextOption("\u00e9")], options);
    }

    // A null culture is refused even for an unsorted list, which would not use it. The empty name is the invariant
    // culture.
    [Theory]
    [InlineData(null, "", "storedTexts")]
    [InlineData(new string[] { "a" }, null, "culture")]
    public void ToOptions_NullArgument_Throws(string?[]? storedTexts, string? cultureName, string parameterName)
    {
        CultureInfo? culture = cultureName is null ? null : CultureInfo.GetCultureInfo(cultureName);

        ArgumentNullException failure = Assert.Throws<ArgumentNullException>(
            () => SqlMetadataReader.ToOptions(storedTexts!, sort: false, culture!));

        Assert.Equal(parameterName, failure.ParamName);
    }

    private static FieldOption TextOption(string text) => new(FieldValue.FromText(text), text);
}

/// <summary>
/// Unit tests of the name guard of <see cref="SqlMetadataReader.ReadTempTableSchemaAsync"/>, the reserved-word
/// fixture's seam: only a session-local <c>#</c> table name passes, and a global <c>##</c> or malformed name is refused
/// before any command reaches SQL Server. They need no database: the connection is never opened.
/// </summary>
public sealed class SqlMetadataReaderTempTableNameTests
{
    // SQL Server's length limit for a local temporary table name, its '#' included.
    private const int MaxSessionNameLength = 116;

    public static TheoryData<string?> RejectedNames =>
    [
        // Absent or empty.
        null,
        "",
        // White space only, or a '#' with no name after it.
        " ",
        "#",
        // Not a temporary table, or a global one, which every session can see.
        "Rw",
        "##RwGlobal",
        "###Rw",
        // White space just after the '#', at the end, or before it.
        "# Rw",
        "#\tRw",
        "#Rw ",
        " #Rw",
        // One character over the limit.
        "#" + new string('x', MaxSessionNameLength),
    ];

    public static TheoryData<string> AcceptedNames =>
    [
        "#RwBase",
        // A '.' or ']' is admitted as part of the one name.
        "#Rw.Dot",
        "#Rw]Bracket",
        // Exactly the limit.
        "#" + new string('x', MaxSessionNameLength - 1),
    ];

    [Theory]
    [MemberData(nameof(RejectedNames))]
    public async Task ReadTempTableSchemaAsync_NotASessionLocalName_ThrowsAFixedMessageBeforeAnyCommand(string? name)
    {
        var recorder = new SqlCommandRecorder();
        SqlMetadataReader reader = CreateReader(recorder);
        await using var connection = new SqlConnection();

        ArgumentException failure = await Assert.ThrowsAsync<ArgumentException>(() =>
            reader.ReadTempTableSchemaAsync(connection, name!, CancellationToken.None));
        ArgumentException absent = await Assert.ThrowsAsync<ArgumentException>(() =>
            reader.ReadTempTableSchemaAsync(connection, null!, CancellationToken.None));

        Assert.Equal("tempTableName", failure.ParamName);
        // The message does not vary with the name: it is the text an absent name gets. The fixed text itself holds
        // '#' and spaces, so only a name with a letter or digit can be looked for in it.
        Assert.Equal(absent.Message, failure.Message);
        if (name is not null && name.Any(char.IsLetterOrDigit))
        {
            Assert.DoesNotContain(name, failure.Message, StringComparison.Ordinal);
        }

        Assert.Empty(recorder.Commands);
    }

    [Theory]
    [MemberData(nameof(AcceptedNames))]
    public async Task ReadTempTableSchemaAsync_SessionLocalNameOnAClosedConnection_PassesTheGuardAndThrowsNotOpen(
        string name)
    {
        var recorder = new SqlCommandRecorder();
        SqlMetadataReader reader = CreateReader(recorder);
        await using var connection = new SqlConnection();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            reader.ReadTempTableSchemaAsync(connection, name, CancellationToken.None));

        Assert.Empty(recorder.Commands);
    }

    private static SqlMetadataReader CreateReader(SqlCommandRecorder recorder)
    {
        // No connection string is needed: these tests never open a connection through the factory.
        var options = new DatabaseOptions(ConnectionString: null);
        return new SqlMetadataReader(new SqlConnectionFactory(options), options, recorder);
    }
}
