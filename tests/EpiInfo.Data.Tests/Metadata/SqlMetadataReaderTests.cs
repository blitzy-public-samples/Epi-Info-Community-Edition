using System.Data;
using System.Globalization;
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

namespace EpiInfo.Data.Tests.Metadata;

/// <summary>
/// Integration tests of <see cref="SqlMetadataReader"/> against the rehearsal database. Every expected value comes
/// from a direct query on a separate verification connection, never from the reader. The tests only read: they send
/// no DDL or DML, take no <c>DatabaseWriteLock</c>, and build any synthetic field or form in memory, so no metadata
/// row is written.
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

    // Two seeded code tables with nvarchar(255) text columns and no text in common (testdata/sql/04_code_tables.sql):
    // the administrative-sex table of FoodHistory's Legal Values field Sex, and the state table, named exactly as the
    // catalogue spells them.
    private const string SexTable = "codephvs_administrativesex_hl7_2x";
    private const string SexColumn = "phvs_administrativesex_hl7_2x";
    private const string StateTable = "codestate";
    private const string StateColumn = "State";

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(60);

    // The rows with a null PageId: system fields, never listed on a page (C-7).
    private static readonly string[] SystemFieldNames = ["GlobalRecordId", "RecStatus", "UniqueKey"];

    // The seeded forms as testdata/sql/03_forms.sql and 05_data_tables.sql define them. Each test also reads the same
    // facts by direct SQL, so the literals, the database and the reader must all agree.
    private static readonly IReadOnlyDictionary<int, SeededForm> SeededForms = new Dictionary<int, SeededForm>
    {
        [1] = new SeededForm(
            ViewId: 1,
            Name: "CaseReport",
            Pages: [new PageMetadata(1, "Page 1", 0, "CaseReport1")],
            FieldCount: 64,
            FieldsPerPage: new Dictionary<int, int> { [1] = 61 },
            DataColumnsPerTable: new Dictionary<string, int>(StringComparer.Ordinal) { ["CaseReport1"] = 43 },
            DataFieldCount: 43,
            FieldTypeCounts: new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["Text"] = 16,
                ["Label/Title"] = 14,
                ["Comment Legal"] = 18,
                ["Group"] = 4,
                ["Date"] = 4,
                ["Phone Number"] = 3,
                ["Number"] = 2,
            },
            YesNoFields: []),
        [2] = new SeededForm(
            ViewId: 2,
            Name: "FoodHistory",
            Pages: [new PageMetadata(2, "Page 1", 0, "FoodHistory2"), new PageMetadata(3, "Page 2", 1, "FoodHistory3")],
            FieldCount: 91,
            FieldsPerPage: new Dictionary<int, int> { [2] = 55, [3] = 33 },
            DataColumnsPerTable: new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["FoodHistory2"] = 45,
                ["FoodHistory3"] = 29,
            },
            DataFieldCount: 67,
            FieldTypeCounts: new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["Checkbox"] = 40,
                ["Label/Title"] = 9,
                ["Text"] = 8,
                ["Date"] = 7,
                ["Yes/No"] = 7,
                ["Number"] = 6,
                ["Group"] = 4,
                ["Legal Values"] = 3,
                ["Multiline"] = 2,
                ["Phone Number"] = 1,
                ["Command Button"] = 1,
            },
            YesNoFields:
            ["Antibiotics", "Died", "DoctorVisit", "Hospitalized", "ILL", "O157H7isolated", "StoolSubmitted"]),
    };

    // The fieldType each seeded metaFieldTypes name maps to. Phone Number and Comment Legal sit on character columns
    // and are text; Yes/No sits on tinyint and is unsupported, so it is never written (C-2); Command Button is always
    // unsupported.
    private static readonly IReadOnlyDictionary<string, FieldType> SeededTypeMapping =
        new Dictionary<string, FieldType>(StringComparer.Ordinal)
        {
            ["Text"] = FieldType.Text,
            ["Label/Title"] = FieldType.Label,
            ["Group"] = FieldType.Group,
            ["Number"] = FieldType.Number,
            ["Date"] = FieldType.Date,
            ["Phone Number"] = FieldType.Text,
            ["Comment Legal"] = FieldType.Text,
            ["Multiline"] = FieldType.Multiline,
            ["Checkbox"] = FieldType.Checkbox,
            ["Legal Values"] = FieldType.LegalValues,
            ["Yes/No"] = FieldType.Unsupported,
            ["Command Button"] = FieldType.Unsupported,
        };

    // The metaFieldTypes rows neither seeded form uses (testdata/sql/02_reference_data.sql): Option, the types the
    // AAP keeps unsupported, and ForeignKey, whose absence means the base insert never names FKEY (A7, A15).
    private static readonly IReadOnlyDictionary<int, string> AbsentFieldTypes = new Dictionary<int, string>
    {
        [12] = "Option",
        [14] = "Image",
        [15] = "Mirror",
        [16] = "Grid",
        [20] = "Relate",
        [24] = "ForeignKey",
        [25] = "GUID",
    };

    // The sys.types names the seeded base and page tables may use: exactly the types the admitted families cover.
    private static readonly string[] SeededColumnTypes =
        ["bit", "datetime", "float", "int", "ntext", "nvarchar", "smallint", "tinyint"];

    // The family each catalogue sys.types name belongs to, written from the AAP's admitted-types table (0.3.4.3)
    // rather than from FieldTypeMapper, so a misclassification in the product cannot appear on both sides: text,
    // multiline and legalValues admit nvarchar(n), nchar(n), nvarchar(max) and ntext; number admits the four integer
    // types and float; option admits tinyint, smallint and int; date admits datetime; checkbox admits bit. The types
    // the table names as mismatches, and uniqueidentifier, are Other. A catalogue type missing here fails the test
    // rather than passing unchecked.
    private static readonly IReadOnlyDictionary<string, ColumnFamily> ExpectedColumnFamilies =
        new Dictionary<string, ColumnFamily>(StringComparer.Ordinal)
        {
            ["nvarchar"] = ColumnFamily.Character,
            ["nchar"] = ColumnFamily.Character,
            ["ntext"] = ColumnFamily.Character,
            ["tinyint"] = ColumnFamily.Integer,
            ["smallint"] = ColumnFamily.Integer,
            ["int"] = ColumnFamily.Integer,
            ["bigint"] = ColumnFamily.Integer,
            ["float"] = ColumnFamily.Float,
            ["datetime"] = ColumnFamily.DateTime,
            ["bit"] = ColumnFamily.Bit,
            ["varchar"] = ColumnFamily.Other,
            ["char"] = ColumnFamily.Other,
            ["text"] = ColumnFamily.Other,
            ["decimal"] = ColumnFamily.Other,
            ["numeric"] = ColumnFamily.Other,
            ["money"] = ColumnFamily.Other,
            ["real"] = ColumnFamily.Other,
            ["date"] = ColumnFamily.Other,
            ["datetime2"] = ColumnFamily.Other,
            ["smalldatetime"] = ColumnFamily.Other,
            ["uniqueidentifier"] = ColumnFamily.Other,
        };

    // Seeded character columns with their catalogue type, length in UTF-16 code units and raw max_length.
    private static readonly CharacterColumn[] SeededCharacterColumns =
    [
        new(1, "LastName", "CaseReport1", "nvarchar", 4000, 8000),
        new(2, "LastName", "FoodHistory2", "nvarchar", 4000, 8000),
        new(1, "PhoneNumber", "CaseReport1", "nvarchar", 20, 40),
        new(2, "HomePhone", "FoodHistory2", "nvarchar", 20, 40),
        new(2, "PatientAddress", "FoodHistory2", "ntext", null, 16),
        new(2, "BuyLastTenDays", "FoodHistory3", "ntext", null, 16),
    ];

    // FoodHistory's three Legal Values fields (testdata/sql/03_forms.sql) with their code tables as metaFields names
    // them, the catalogue spelling of the text column, and the seeded texts (testdata/sql/04_code_tables.sql).
    private static readonly SeededLegalValuesField[] SeededLegalValuesFields =
    [
        new("Sex", SexTable, SexColumn, SexColumn, ["F-Female", "M-Male", "U-Unknown"]),
        new(
            "EthnicityGroup",
            "codephvs_ethnicitygroup_cdc",
            "phvs_ethnicitygroup_cdc",
            "phvs_ethnicitygroup_cdc",
            ["2135:2-Hispanic or Latino", "2186:5-Not Hispanic or Latino"]),
        new(
            "LooseStoolsPerDay",
            LooseStoolsTable,
            LooseStoolsTextColumn,
            "loosestoolsperday",
            ["1-3 per day", "4-6 per day", "7-10 per day", "10+ per day"]),
    ];

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

    /// <summary>
    /// <c>Choice</c> (page 1) and <c>CHOICE</c> (page 2) are Legal Values fields whose names differ only by letter
    /// case and that name different code tables. Each key holds the rows of its own field's code table, read directly
    /// here, and the map compares keys ordinally, so no spelling but the exact one finds either list.
    /// </summary>
    [Fact]
    public async Task GetOptionsAsync_CaseVariantLegalValuesFields_EachKeyHoldsItsOwnCodeTable()
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        CancellationToken token = cancellation.Token;
        var recorder = new SqlCommandRecorder();
        SqlMetadataReader reader = CreateReader(recorder);
        await using SqlConnection verification = await TestDatabase.OpenConnectionAsync(token);

        var form = new FormMetadata(
            ViewId: 9101,
            Name: "LegalValuesCase",
            BaseTableName: "LegalValuesCase",
            Pages:
            [
                new PageMetadata(1, "Page 1", 0, "LegalValuesCase1"),
                new PageMetadata(2, "Page 2", 1, "LegalValuesCase2"),
            ],
            Fields:
            [
                CaseVariantLegalValuesField(1, "Choice", pageId: 1, SexTable, SexColumn),
                CaseVariantLegalValuesField(2, "CHOICE", pageId: 2, StateTable, StateColumn),
            ]);
        IReadOnlyList<string?> sexRows = await ReadColumnAsync(verification, SexTable, SexColumn, token);
        IReadOnlyList<string?> stateRows = await ReadColumnAsync(verification, StateTable, StateColumn, token);

        // Preconditions on the seed: both tables hold rows, each row is non-blank and distinct even ignoring case, so
        // every row is an option, and no text is in both tables, so a shared list cannot pass for either table's.
        Assert.All(new[] { sexRows, stateRows }, rows =>
        {
            Assert.NotEmpty(rows);
            Assert.All(rows, text => Assert.False(string.IsNullOrWhiteSpace(text)));
            Assert.Equal(rows.Count, rows.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        });
        Assert.Empty(sexRows.Intersect(stateRows, StringComparer.OrdinalIgnoreCase));

        IReadOnlyDictionary<string, IReadOnlyList<FieldOption>> options = await reader.GetOptionsAsync(form, token);

        Assert.Equal(["CHOICE", "Choice"], options.Keys.Order(StringComparer.Ordinal));
        AssertOptionsAre(sexRows, options["Choice"]);
        AssertOptionsAre(stateRows, options["CHOICE"]);
        Assert.False(options.ContainsKey("choice"));
        Assert.False(options.TryGetValue("cHOICE", out _));
        Assert.True(recorder.AnyCommandTextContains("[" + SexTable + "]"));
        Assert.True(recorder.AnyCommandTextContains("[" + StateTable + "]"));
        Assert.Empty(recorder.Writes());
    }

    /// <summary>
    /// Two Legal Values rows spelled exactly <c>Choice</c>: the page-1 row, with the higher <c>FieldId</c>, names the
    /// administrative-sex table, and the page-2 row, with the lower <c>FieldId</c> and listed first, names the state
    /// table. The data field is the first in storage order, the page-1 row, so its code table alone is read and holds
    /// the one <c>Choice</c> entry; the definition lists the page-2 row as unsupported, and the validator accepts the
    /// data field's options and rejects the state table's.
    /// </summary>
    [Fact]
    public async Task GetOptionsAsync_ExactRepeatOfALegalValuesName_ReadsOnlyTheDataFieldsCodeTable()
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        CancellationToken token = cancellation.Token;
        var recorder = new SqlCommandRecorder();
        SqlMetadataReader reader = CreateReader(recorder);
        await using SqlConnection verification = await TestDatabase.OpenConnectionAsync(token);

        var form = new FormMetadata(
            ViewId: 9102,
            Name: "LegalValuesRepeat",
            BaseTableName: "LegalValuesRepeat",
            Pages:
            [
                new PageMetadata(1, "Page 1", 0, "LegalValuesRepeat1"),
                new PageMetadata(2, "Page 2", 1, "LegalValuesRepeat2"),
            ],
            Fields:
            [
                CaseVariantLegalValuesField(1, "Choice", pageId: 2, StateTable, StateColumn),
                CaseVariantLegalValuesField(2, "Choice", pageId: 1, SexTable, SexColumn),
            ]);
        IReadOnlyList<string?> sexRows = await ReadColumnAsync(verification, SexTable, SexColumn, token);
        IReadOnlyList<string?> stateRows = await ReadColumnAsync(verification, StateTable, StateColumn, token);

        // Preconditions on the seed, as in the case-variant test: both tables hold distinct non-blank rows and no text
        // is in both, so the list read and the validator's verdicts each tell the two tables apart.
        Assert.All(new[] { sexRows, stateRows }, rows =>
        {
            Assert.NotEmpty(rows);
            Assert.All(rows, text => Assert.False(string.IsNullOrWhiteSpace(text)));
            Assert.Equal(rows.Count, rows.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        });
        Assert.Empty(sexRows.Intersect(stateRows, StringComparer.OrdinalIgnoreCase));

        IReadOnlyDictionary<string, IReadOnlyList<FieldOption>> options = await reader.GetOptionsAsync(form, token);

        Assert.Equal(["Choice"], options.Keys);
        AssertOptionsAre(sexRows, options["Choice"]);
        Assert.True(recorder.AnyCommandTextContains("[" + SexTable + "]"));

        // Neither the catalogue read, which binds the code-table names as one parameter, nor the code-table batch
        // names the repeating row's table.
        Assert.DoesNotContain(recorder.Commands, command =>
            command.CommandText.Contains(StateTable, StringComparison.Ordinal)
            || command.Parameters.Any(parameter =>
                parameter.Value is string bound && bound.Contains(StateTable, StringComparison.Ordinal)));
        Assert.Empty(recorder.Writes());

        FormDefinition definition = FormDefinitionFactory.Create(form, options, []);
        FieldDefinition dataField = Assert.Single(Assert.Single(definition.Pages, page => page.PageId == 1).Fields);
        FieldDefinition repeat = Assert.Single(Assert.Single(definition.Pages, page => page.PageId == 2).Fields);
        Assert.Equal(FieldType.LegalValues, dataField.FieldType);
        Assert.NotNull(dataField.Options);
        AssertOptionsAre(sexRows, dataField.Options);
        Assert.True(repeat.Unsupported);
        Assert.Equal(FieldType.Unsupported, repeat.FieldType);
        Assert.Null(repeat.Options);

        var validator = new RecordValidator(form, definition);
        RecordValidationResult accepted = validator.ValidateInput(
            [new KeyValuePair<string, FieldValue>("Choice", FieldValue.FromText(sexRows[0]!))]);
        RecordValidationResult rejected = validator.ValidateInput(
            [new KeyValuePair<string, FieldValue>("Choice", FieldValue.FromText(stateRows[0]!))]);

        Assert.True(accepted.IsValid, "An option of the data field's own code table was rejected.");
        Assert.False(rejected.IsValid, "A text of the repeating row's code table was accepted.");
        Assert.Equal(["Choice"], rejected.FieldErrors.Keys);
        Assert.Equal([ValidationMessages.NotALegalValue], rejected.FieldErrors["Choice"]);
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

    /// <summary>
    /// A Legal Values field whose metadata names a readable character column outside the code-table role, a page
    /// table's data column, a metadata table's column or a base table's system column, gets no options. The catalogue
    /// resolves the table and its character column, yet the table is no code table, so no code-table read follows
    /// the catalogue read and none of its rows is published as an option. <c>metaPages</c>, with its identity
    /// <c>PageId</c>, and <c>metaDbInfo</c>, with its <c>uniqueidentifier</c> and <c>int</c> columns, are seeded tables
    /// whose columns, beside their names, keep them from the code-table shape.
    /// </summary>
    [Theory]
    [InlineData("CaseReport1", "LastName")]
    [InlineData("metaFields", "Name")]
    [InlineData("FoodHistory", "GlobalRecordId")]
    [InlineData("metaPages", "Name")]
    [InlineData("metaDbInfo", "ProjectName")]
    public async Task ReadOptionsAsync_ColumnOutsideTheCodeTableRole_ReturnsNoOptionsAndReadsNoRow(
        string sourceTableName,
        string textColumnName)
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        CancellationToken token = cancellation.Token;
        var recorder = new SqlCommandRecorder();
        SqlMetadataReader reader = CreateReader(recorder);
        await using SqlConnection connection = await TestDatabase.OpenConnectionAsync(token);

        // Preconditions: the column is a character column holding rows, and the reader's own catalogue read resolves
        // it as one, so only the table's role keeps its rows out.
        Assert.Contains(
            await CatalogueColumnsAsync(connection, sourceTableName, token),
            column => column.Name == textColumnName && column.TypeName == "nvarchar");
        Assert.NotEmpty(await ReadColumnAsync(connection, sourceTableName, textColumnName, token));
        TableSchema? resolved = Assert.Single(await CreateReader(new SqlCommandRecorder())
            .ResolveTablesAsync(connection, transaction: null, [sourceTableName], token));
        Assert.NotNull(resolved);
        Assert.Equal(ColumnFamily.Character, resolved.FindColumn(textColumnName)?.Metadata.Family);

        IReadOnlyList<FieldOption> options = await reader.ReadOptionsAsync(
            connection,
            transaction: null,
            LooseStoolsField(sourceTableName, sort: false) with { TextColumnName = textColumnName },
            token);

        Assert.Empty(options);
        RecordedCommand catalogueRead = Assert.Single(recorder.Commands);
        Assert.Equal(MetadataQueries.Columns, catalogueRead.CommandText);
        Assert.True(catalogueRead.Succeeded);
        Assert.Empty(recorder.Writes());
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

    /// <summary>
    /// The form list is <c>metaViews</c> with each view's <c>metaPages</c> count: exactly the seeded
    /// <c>(1, CaseReport, 1)</c> and <c>(2, FoodHistory, 2)</c>, as a direct query also reads them.
    /// </summary>
    [Fact]
    public async Task ListFormsAsync_ReturnsSeededFormsWithPageCounts()
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        CancellationToken token = cancellation.Token;
        var recorder = new SqlCommandRecorder();
        SqlMetadataReader reader = CreateReader(recorder);
        await using SqlConnection verification = await TestDatabase.OpenConnectionAsync(token);

        IReadOnlyList<FormSummary> stored = await QueryRowsAsync(
            verification,
            """
            SELECT v.[ViewId], v.[Name], (SELECT COUNT(*) FROM [metaPages] AS p WHERE p.[ViewId] = v.[ViewId])
            FROM [metaViews] AS v;
            """,
            row => new FormSummary(Int(row, 0), row.GetString(1), Int(row, 2)),
            token);

        IReadOnlyList<FormSummary> forms = await reader.ListFormsAsync(token);

        FormSummary[] seeded = [new FormSummary(1, "CaseReport", 1), new FormSummary(2, "FoodHistory", 2)];
        Assert.Equal(seeded, forms.OrderBy(form => form.ViewId));
        Assert.Equal(stored.OrderBy(form => form.ViewId), forms.OrderBy(form => form.ViewId));
        AssertSelectOnly(recorder);
    }

    /// <summary>
    /// The base table is the view's one <c>metaFields.DataTableName</c>, read by legacy's own query, and each page
    /// table is that name followed by the page id, as legacy <c>Page.TableName</c> builds it; every one of them is a
    /// table of the catalogue under exactly that spelling (A16).
    /// </summary>
    [Theory]
    [InlineData(1, "CaseReport")]
    [InlineData(2, "FoodHistory")]
    public async Task GetFormMetadataAsync_ReadsTableNamesFromMetadata(int viewId, string name)
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        CancellationToken token = cancellation.Token;
        var recorder = new SqlCommandRecorder();
        SqlMetadataReader reader = CreateReader(recorder);
        await using SqlConnection verification = await TestDatabase.OpenConnectionAsync(token);

        // Legacy MetadataDbProvider.GetDataTableName (Epi.Core/Data/Services/MetadataDbProvider.cs:2165-2192).
        string legacyBaseTable = Assert.Single(await QueryRowsAsync(
            verification,
            "select distinct [DataTableName] from [metaFields] where [ViewId] = @viewId and [DataTableName] is not null;",
            row => row.GetString(0),
            token,
            ViewIdParameter(viewId)));
        IReadOnlyList<PageMetadata> storedPages = await QueryRowsAsync(
            verification,
            "SELECT [PageId], [Name], [Position] FROM [metaPages] WHERE [ViewId] = @viewId ORDER BY [Position], [PageId];",
            row => new PageMetadata(
                Int(row, 0),
                row.GetString(1),
                Int(row, 2),
                legacyBaseTable + Int(row, 0).ToString(CultureInfo.InvariantCulture)),
            token,
            ViewIdParameter(viewId));

        FormMetadata? form = await reader.GetFormMetadataAsync(viewId, token);

        Assert.NotNull(form);
        Assert.Equal(viewId, form.ViewId);
        Assert.Equal(name, form.Name);
        Assert.Equal(name, legacyBaseTable);
        Assert.Equal(legacyBaseTable, form.BaseTableName);
        Assert.True(
            await TableExistsAsync(verification, form.BaseTableName, token),
            $"Base table {form.BaseTableName} is not in sys.tables under that spelling.");

        PageMetadata[] pages = [.. form.Pages.OrderBy(page => page.Position).ThenBy(page => page.PageId)];
        Assert.Equal(SeededForms[viewId].Pages, pages);
        Assert.Equal(storedPages, pages);
        foreach (PageMetadata page in pages)
        {
            Assert.Equal(form.BaseTableName + page.PageId.ToString(CultureInfo.InvariantCulture), page.TableName);
            Assert.True(
                await TableExistsAsync(verification, page.TableName, token),
                $"Page table {page.TableName} is not in sys.tables under that spelling.");
        }

        AssertSelectOnly(recorder);
    }

    /// <summary>
    /// On a connection the caller owns, the reader builds the form's catalogue schema: the base table with its
    /// <c>RecStatus</c>, <c>UniqueKey</c> and <c>GlobalRecordId</c> columns spelled and typed as the catalogue holds
    /// them, and one table per page with the seeded number of data columns (A16).
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ReadFormAsync_BuildsSchemaFromCatalogue(int viewId)
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        CancellationToken token = cancellation.Token;
        var recorder = new SqlCommandRecorder();
        SqlMetadataReader reader = CreateReader(recorder);
        await using SqlConnection connection = await TestDatabase.OpenConnectionAsync(token);
        SeededForm seeded = SeededForms[viewId];

        (FormMetadata Form, FormSchema Schema)? read =
            await reader.ReadFormAsync(connection, transaction: null, viewId, token);
        FormMetadata? viaPort = await reader.GetFormMetadataAsync(viewId, token);

        Assert.True(read.HasValue, $"ReadFormAsync returned null for seeded view {viewId}.");
        (FormMetadata form, FormSchema schema) = read.Value;
        Assert.Equal(viewId, schema.ViewId);
        Assert.Equal(form.BaseTableName, schema.BaseTable.Name);

        IReadOnlyList<CatalogueColumn> baseColumns = await CatalogueColumnsAsync(connection, form.BaseTableName, token);
        Assert.Equal("smallint", Assert.Single(baseColumns, column => column.Name == "RecStatus").TypeName);
        Assert.Equal("int", Assert.Single(baseColumns, column => column.Name == "UniqueKey").TypeName);
        Assert.NotNull(schema.RecStatus);
        Assert.Equal("RecStatus", schema.RecStatus.Name);
        Assert.Equal("smallint", schema.RecStatus.SqlTypeName, ignoreCase: true);
        Assert.NotNull(schema.UniqueKey);
        Assert.Equal("UniqueKey", schema.UniqueKey.Name);
        Assert.Equal("int", schema.UniqueKey.SqlTypeName, ignoreCase: true);
        Assert.NotNull(schema.GlobalRecordId);
        Assert.Equal("GlobalRecordId", schema.GlobalRecordId.Name);

        Assert.Equal(form.Pages.Select(page => page.PageId), schema.Pages.Select(page => page.Page.PageId));
        foreach (PageTableSchema page in schema.Pages)
        {
            Assert.NotNull(page.Table);
            Assert.Equal(page.Page.TableName, page.Table.Name);
            IReadOnlyList<CatalogueColumn> catalogue = await CatalogueColumnsAsync(connection, page.Table.Name, token);
            int dataColumns = page.Table.Columns.Count(column => !IsGlobalRecordId(column.Name));
            Assert.Equal(seeded.DataColumnsPerTable[page.Table.Name], dataColumns);
            Assert.Equal(catalogue.Count(column => !IsGlobalRecordId(column.Name)), dataColumns);
        }

        Assert.NotNull(viaPort);
        Assert.Equal(form.ViewId, viaPort.ViewId);
        Assert.Equal(form.BaseTableName, viaPort.BaseTableName);
        Assert.Equal(form.Pages.Select(page => page.PageId), viaPort.Pages.Select(page => page.PageId));
        Assert.Equal(form.Fields.Count, viaPort.Fields.Count);
        AssertSelectOnly(recorder);
    }

    /// <summary>
    /// Neither seeded form's metadata claims a table of the other. A direct query that pairs every other form's
    /// data-table name with each of its page ids finds no claim equal to one of the form's own names, and the claims
    /// statement, run read-only with the form's id and its stored data-table name, returns no row. The reader then
    /// reads the form, sending that statement once, after its catalogue read, with the id bound as
    /// <see cref="DbType.Int32"/> and the name as <see cref="DbType.String"/>; every command is a <c>SELECT</c>.
    /// </summary>
    [Theory]
    [InlineData(1, "CaseReport")]
    [InlineData(2, "FoodHistory")]
    public async Task TableClaims_SeededForms_ReturnNoClaimAndTheFormsStillRead(int viewId, string name)
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        CancellationToken token = cancellation.Token;
        var recorder = new SqlCommandRecorder();
        SqlMetadataReader reader = CreateReader(recorder);
        await using SqlConnection connection = await TestDatabase.OpenConnectionAsync(token);

        string baseTableName = Assert.Single(await QueryRowsAsync(
            connection,
            "SELECT DISTINCT [DataTableName] FROM [metaFields] WHERE [ViewId] = @viewId AND [DataTableName] IS NOT NULL;",
            row => row.GetString(0),
            token,
            ViewIdParameter(viewId)));
        Assert.Equal(name, baseTableName);

        // Every claim of every other form, name by name and page by page, against the form's base and page names.
        int pairedClaims = Assert.Single(await QueryRowsAsync(
            connection,
            """
            SELECT COUNT(*)
            FROM [metaFields] AS f
            LEFT JOIN [metaPages] AS p ON p.[ViewId] = f.[ViewId]
            WHERE f.[ViewId] <> @viewId
              AND f.[DataTableName] IS NOT NULL
              AND EXISTS (
                  SELECT 1
                  FROM (SELECT @name AS [Name]
                        UNION ALL
                        SELECT @name + CAST(own.[PageId] AS nvarchar(11)) FROM [metaPages] AS own WHERE own.[ViewId] = @viewId) AS o
                  WHERE o.[Name] COLLATE Latin1_General_100_CI_AS = f.[DataTableName] COLLATE Latin1_General_100_CI_AS
                     OR o.[Name] COLLATE Latin1_General_100_CI_AS
                        = (f.[DataTableName] + CAST(p.[PageId] AS nvarchar(11))) COLLATE Latin1_General_100_CI_AS);
            """,
            row => Int(row, 0),
            token,
            ViewIdParameter(viewId),
            new SqlParameter("@name", SqlDbType.NVarChar, 128) { Value = baseTableName }));
        Assert.Equal(0, pairedClaims);

        IReadOnlyList<int> claims = await QueryRowsAsync(
            connection,
            MetadataQueries.TableClaims,
            row => Int(row, 0),
            token,
            ViewIdParameter(viewId),
            new SqlParameter(MetadataQueries.BaseNameParameter, SqlDbType.NVarChar, -1) { Value = baseTableName });
        Assert.Empty(claims);

        (FormMetadata Form, FormSchema Schema)? read =
            await reader.ReadFormAsync(connection, transaction: null, viewId, token);

        Assert.True(read.HasValue, $"ReadFormAsync returned null for seeded view {viewId}.");
        Assert.Equal(baseTableName, read.Value.Form.BaseTableName);
        Assert.Equal(SeededForms[viewId].Pages, read.Value.Form.Pages);
        RecordedCommand claimsRead = Assert.Single(
            recorder.Commands,
            command => command.CommandText == MetadataQueries.TableClaims);
        Assert.True(claimsRead.Succeeded, claimsRead.ToString());
        Assert.Equal(
            new (string, DbType, object?)[]
            {
                (MetadataQueries.ViewIdParameter, DbType.Int32, viewId),
                (MetadataQueries.BaseNameParameter, DbType.String, baseTableName),
            },
            claimsRead.Parameters.Select(parameter => (parameter.Name, parameter.DbType, parameter.Value)));
        RecordedCommand catalogueRead = Assert.Single(
            recorder.Commands,
            command => command.CommandText == MetadataQueries.Columns);
        Assert.True(catalogueRead.Sequence < claimsRead.Sequence, claimsRead.ToString());
        AssertSelectOnly(recorder);
    }

    /// <summary>
    /// The form's fields are every <c>metaFields</c> row of the view: 61 on <c>CaseReport</c>'s page, 55 and 33 on
    /// <c>FoodHistory</c>'s, plus the three system rows with no page, 64 and 91 in all (A12, C-7).
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Fields_ListEveryMetaFieldsRowWithPageBinding(int viewId)
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        CancellationToken token = cancellation.Token;
        var recorder = new SqlCommandRecorder();
        SqlMetadataReader reader = CreateReader(recorder);
        await using SqlConnection verification = await TestDatabase.OpenConnectionAsync(token);
        SeededForm seeded = SeededForms[viewId];

        IReadOnlyList<(int? PageId, int Count)> stored = await QueryRowsAsync(
            verification,
            "SELECT [PageId], COUNT(*) FROM [metaFields] WHERE [ViewId] = @viewId GROUP BY [PageId];",
            row => (row.IsDBNull(0) ? (int?)null : Int(row, 0), Int(row, 1)),
            token,
            ViewIdParameter(viewId));

        FormMetadata? form = await reader.GetFormMetadataAsync(viewId, token);

        Assert.NotNull(form);
        Assert.Equal(seeded.FieldCount, form.Fields.Count);
        Assert.Equal(stored.Sum(row => row.Count), form.Fields.Count);

        (int PageId, int Count)[] perPage =
        [
            .. form.Fields
                .Where(field => field.PageId is not null)
                .GroupBy(field => field.PageId.GetValueOrDefault())
                .Select(group => (group.Key, group.Count()))
                .OrderBy(entry => entry.Key),
        ];
        Assert.Equal(seeded.FieldsPerPage.OrderBy(entry => entry.Key).Select(entry => (entry.Key, entry.Value)), perPage);
        Assert.Equal(
            stored.Where(row => row.PageId is not null)
                .Select(row => (row.PageId.GetValueOrDefault(), row.Count))
                .OrderBy(entry => entry.Item1),
            perPage);
        Assert.All(form.Fields.Where(field => field.PageId is not null), field =>
            Assert.NotNull(form.FindPage(field.PageId.GetValueOrDefault())));

        string[] systemRows = [.. form.Fields.Where(field => field.PageId is null).Select(field => field.Name)];
        Assert.Equal(SystemFieldNames, systemRows.Order(StringComparer.Ordinal));
        Assert.Equal(SystemFieldNames.Length, Assert.Single(stored, row => row.PageId is null).Count);
        AssertSelectOnly(recorder);
    }

    /// <summary>
    /// The C-7 reconciliation: the page-bound fields that own a column are exactly the page tables' data columns,
    /// 43 and 74, and the form definition's data fields are 43 and 67, because the seven Yes/No fields on
    /// <c>tinyint</c> columns are unsupported (C-2).
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task PageTableDataColumns_ReconcileWithDtoDataFields(int viewId)
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        CancellationToken token = cancellation.Token;
        var recorder = new SqlCommandRecorder();
        SqlMetadataReader reader = CreateReader(recorder);
        await using SqlConnection verification = await TestDatabase.OpenConnectionAsync(token);
        SeededForm seeded = SeededForms[viewId];

        FormMetadata? form = await reader.GetFormMetadataAsync(viewId, token);
        Assert.NotNull(form);

        var catalogueKeys = new List<string>();
        foreach (PageMetadata page in form.Pages)
        {
            IReadOnlyList<CatalogueColumn> columns = await CatalogueColumnsAsync(verification, page.TableName, token);
            catalogueKeys.AddRange(
                columns.Where(column => !IsGlobalRecordId(column.Name)).Select(column => page.TableName + "." + column.Name));
        }

        Assert.Equal(seeded.DataColumnsPerTable.Values.Sum(), catalogueKeys.Count);

        var columnFieldKeys = new List<string>();
        var columnFieldNames = new List<string>();
        foreach (FieldMetadata field in form.Fields)
        {
            if (field.PageId is int pageId && field.Column is { } column)
            {
                PageMetadata? page = form.FindPage(pageId);
                Assert.NotNull(page);
                Assert.Equal(field.Name, column.Name, ignoreCase: true);
                columnFieldKeys.Add(page.TableName + "." + column.Name);
                columnFieldNames.Add(field.Name);
            }
        }

        Assert.Equal(
            catalogueKeys.Order(StringComparer.OrdinalIgnoreCase),
            columnFieldKeys.Order(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

        DataFieldSet dataFields = FormDefinitionFactory.GetDataFields(form);
        Assert.Equal(seeded.DataFieldCount, dataFields.Count);

        string[] dropped =
        [
            .. columnFieldNames
                .Except(dataFields.Select(field => field.Name), StringComparer.Ordinal)
                .Order(StringComparer.Ordinal),
        ];
        string[] yesNo =
        [
            .. form.Fields
                .Where(field => field.FieldTypeName == "Yes/No")
                .Select(field => field.Name)
                .Order(StringComparer.Ordinal),
        ];
        Assert.Equal(seeded.YesNoFields, dropped);
        Assert.Equal(yesNo, dropped);

        int pageBound = form.Fields.Count(field => field.PageId is not null);
        _output.WriteLine(
            $"{form.Name}: metaFields {form.Fields.Count} ({pageBound} page-bound, {form.Fields.Count - pageBound} " +
            $"system); page-table data columns {catalogueKeys.Count}; DTO data fields {dataFields.Count}" +
            (dropped.Length == 0 ? string.Empty : $" ({dropped.Length} Yes/No unsupported: {string.Join(", ", dropped)})"));
        AssertSelectOnly(recorder);
    }

    /// <summary>
    /// The page-bound fields per <c>metaFieldTypes</c> name match the seed on both forms, and each maps to the
    /// fieldType decided for its name: Phone Number and Comment Legal are text, Yes/No and Command Button are
    /// unsupported under their stored names, and every other field's column is in its type's admitted set (A8).
    /// </summary>
    [Fact]
    public async Task FieldTypes_PerFormMatchSeed()
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        CancellationToken token = cancellation.Token;
        var recorder = new SqlCommandRecorder();
        SqlMetadataReader reader = CreateReader(recorder);
        await using SqlConnection verification = await TestDatabase.OpenConnectionAsync(token);

        var totals = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (SeededForm seeded in SeededForms.Values)
        {
            IReadOnlyList<KeyValuePair<string, int>> stored = await QueryRowsAsync(
                verification,
                """
                SELECT t.[Name], COUNT(*)
                FROM [metaFields] AS f
                INNER JOIN [metaFieldTypes] AS t ON t.[FieldTypeId] = f.[FieldTypeId]
                WHERE f.[ViewId] = @viewId AND f.[PageId] IS NOT NULL
                GROUP BY t.[Name];
                """,
                row => KeyValuePair.Create(row.GetString(0), Int(row, 1)),
                token,
                ViewIdParameter(seeded.ViewId));

            FormMetadata? form = await reader.GetFormMetadataAsync(seeded.ViewId, token);
            Assert.NotNull(form);
            FieldMetadata[] pageBound = [.. form.Fields.Where(field => field.PageId is not null)];

            string[] counts = DescribeCounts(pageBound.CountBy(field => field.FieldTypeName));
            Assert.Equal(DescribeCounts(seeded.FieldTypeCounts), counts);
            Assert.Equal(DescribeCounts(stored), counts);

            // One line per field, so a failure names the field whose mapping differs.
            string[] expectedMappings =
            [
                .. pageBound.Select(field => DescribeMapping(
                    field,
                    SeededTypeMapping[field.FieldTypeName],
                    SeededTypeMapping[field.FieldTypeName] == FieldType.Unsupported ? field.FieldTypeName : null)),
            ];
            string[] mappings =
            [
                .. pageBound.Select(field =>
                {
                    (FieldType fieldType, bool unsupported, string? unsupportedType) = FieldTypeMapper.Map(field);
                    Assert.Equal(fieldType == FieldType.Unsupported, unsupported);
                    return DescribeMapping(field, fieldType, unsupportedType);
                }),
            ];
            Assert.Equal(expectedMappings, mappings);

            foreach (IGrouping<string, FieldMetadata> type in pageBound.GroupBy(field => field.FieldTypeName))
            {
                totals[type.Key] = totals.GetValueOrDefault(type.Key) + type.Count();
                (FieldType fieldType, bool unsupported, _) = FieldTypeMapper.Map(type.First());
                string[] columnTypes =
                [
                    .. type.Select(field => field.Column)
                        .OfType<ColumnMetadata>()
                        .Select(DescribeColumnType)
                        .Distinct(StringComparer.Ordinal)
                        .Order(StringComparer.Ordinal),
                ];
                _output.WriteLine(
                    $"{form.Name} | {type.Key} | {type.Count()} | {WireName(fieldType)}" +
                    (unsupported ? $" ({type.Key})" : string.Empty) +
                    $" | {(columnTypes.Length == 0 ? "no column" : string.Join(", ", columnTypes))}");
            }
        }

        Assert.Equal(4, totals["Phone Number"]);
        Assert.Equal(18, totals["Comment Legal"]);
        Assert.Equal(7, totals["Yes/No"]);
        Assert.Equal(1, totals["Command Button"]);
        AssertSelectOnly(recorder);
    }

    /// <summary>
    /// No row of either form, system rows included, is an Option, Image, Mirror, Grid, Relate, ForeignKey or GUID
    /// field, and none is encrypted (A7). With no ForeignKey field the base insert never names <c>FKEY</c>, which
    /// both base tables hold and leave <c>NULL</c> (A15).
    /// </summary>
    [Fact]
    public async Task SeededForms_HaveNoRelateOptionGridImageMirrorGuidForeignKeyOrEncryptedFields()
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        CancellationToken token = cancellation.Token;
        var recorder = new SqlCommandRecorder();
        SqlMetadataReader reader = CreateReader(recorder);
        await using SqlConnection verification = await TestDatabase.OpenConnectionAsync(token);

        // The ids stand for the names the seed gives them.
        IReadOnlyList<KeyValuePair<int, string>> fieldTypes = await QueryRowsAsync(
            verification,
            "SELECT [FieldTypeId], [Name] FROM [metaFieldTypes];",
            row => KeyValuePair.Create(Int(row, 0), row.GetString(1)),
            token);
        Assert.All(AbsentFieldTypes, absent => Assert.Contains(absent, fieldTypes));

        foreach (SeededForm seeded in SeededForms.Values)
        {
            IReadOnlyList<(int FieldTypeId, string FieldTypeName, bool IsEncrypted)> stored = await QueryRowsAsync(
                verification,
                """
                SELECT f.[FieldTypeId], t.[Name], f.[IsEncrypted]
                FROM [metaFields] AS f
                INNER JOIN [metaFieldTypes] AS t ON t.[FieldTypeId] = f.[FieldTypeId]
                WHERE f.[ViewId] = @viewId;
                """,
                row => (Int(row, 0), row.GetString(1), !row.IsDBNull(2) && row.GetBoolean(2)),
                token,
                ViewIdParameter(seeded.ViewId));
            Assert.Equal(seeded.FieldCount, stored.Count);
            Assert.DoesNotContain(stored, row => AbsentFieldTypes.ContainsKey(row.FieldTypeId));
            Assert.DoesNotContain(
                stored,
                row => AbsentFieldTypes.Values.Contains(row.FieldTypeName, StringComparer.Ordinal));
            Assert.DoesNotContain(stored, row => row.IsEncrypted);

            FormMetadata? form = await reader.GetFormMetadataAsync(seeded.ViewId, token);
            Assert.NotNull(form);
            Assert.Equal(seeded.FieldCount, form.Fields.Count);
            Assert.DoesNotContain(form.Fields, field => AbsentFieldTypes.ContainsKey(field.FieldTypeId));
            Assert.DoesNotContain(
                form.Fields,
                field => AbsentFieldTypes.Values.Contains(field.FieldTypeName, StringComparer.Ordinal));
            Assert.DoesNotContain(form.Fields, field => field.IsEncrypted);

            IReadOnlyList<CatalogueColumn> baseColumns =
                await CatalogueColumnsAsync(verification, form.BaseTableName, token);
            CatalogueColumn foreignKey = Assert.Single(baseColumns, column => column.Name == "FKEY");

            _output.WriteLine(
                $"{form.Name}: no {string.Join(", ", AbsentFieldTypes.Values)} field and no encrypted field among " +
                $"{form.Fields.Count} metaFields rows; no ForeignKey field, so the base insert never names FKEY; " +
                $"{form.BaseTableName}.FKEY exists ({foreignKey.TypeName}, {foreignKey.MaxLengthBytes} bytes).");
        }

        AssertSelectOnly(recorder);
    }

    /// <summary>
    /// The seeded base and page tables use only the types the admitted families cover, and every column the reader
    /// returns, as a schema column or as a field's column, carries the catalogue's name, type and length with the
    /// family the AAP's admitted-types table gives the catalogue type, held independently of the product in
    /// <see cref="ExpectedColumnFamilies"/> (A17).
    /// </summary>
    [Fact]
    public async Task ColumnFamilies_MatchCatalogue()
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        CancellationToken token = cancellation.Token;
        var recorder = new SqlCommandRecorder();
        SqlMetadataReader reader = CreateReader(recorder);
        await using SqlConnection connection = await TestDatabase.OpenConnectionAsync(token);

        var catalogueTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (SeededForm seeded in SeededForms.Values)
        {
            (FormMetadata Form, FormSchema Schema)? read =
                await reader.ReadFormAsync(connection, transaction: null, seeded.ViewId, token);
            Assert.True(read.HasValue, $"ReadFormAsync returned null for seeded view {seeded.ViewId}.");
            (FormMetadata form, FormSchema schema) = read.Value;

            var catalogue = new Dictionary<string, IReadOnlyList<CatalogueColumn>>(StringComparer.Ordinal);
            TableSchema[] tables =
                [schema.BaseTable, .. schema.Pages.Select(page => page.Table).OfType<TableSchema>()];
            Assert.Equal(seeded.Pages.Length + 1, tables.Length);
            foreach (TableSchema table in tables)
            {
                IReadOnlyList<CatalogueColumn> columns = await CatalogueColumnsAsync(connection, table.Name, token);
                catalogue.Add(table.Name, columns);
                catalogueTypes.UnionWith(columns.Select(column => column.TypeName));

                Assert.Equal(columns.Select(column => column.Name), table.Columns.Select(column => column.Name));
                foreach ((CatalogueColumn expected, ColumnSchema column) in columns.Zip(table.Columns))
                {
                    Assert.Equal(expected.TypeName, column.SqlTypeName, ignoreCase: true);
                    Assert.Equal(expected.MaxLengthBytes, column.MaxLengthBytes);
                    AssertColumnMatches(expected, column.Metadata);
                }

                _output.WriteLine(
                    $"{table.Name}: " + string.Join(
                        ", ",
                        table.Columns
                            .GroupBy(column => (Type: DescribeColumnType(column.Metadata), column.Metadata.Family))
                            .Select(group => $"{group.Key.Type} [{group.Key.Family}] {group.Count()}")));
            }

            foreach (FieldMetadata field in form.Fields)
            {
                if (field.Column is not { } column)
                {
                    continue;
                }

                PageMetadata? page = field.PageId is int pageId ? form.FindPage(pageId) : null;
                Assert.NotNull(page);
                AssertColumnMatches(Assert.Single(catalogue[page.TableName], entry => entry.Name == column.Name), column);
            }

            foreach (string name in seeded.YesNoFields)
            {
                FieldMetadata yesNo = Assert.Single(form.Fields, field => field.Name == name);
                Assert.Equal("Yes/No", yesNo.FieldTypeName);
                Assert.NotNull(yesNo.Column);
                Assert.Equal("tinyint", yesNo.Column.SqlTypeName);
                Assert.Equal(ColumnFamily.Integer, yesNo.Column.Family);
            }

            foreach (CharacterColumn expected in SeededCharacterColumns.Where(entry => entry.ViewId == seeded.ViewId))
            {
                FieldMetadata field = Assert.Single(form.Fields, candidate => candidate.Name == expected.Field);
                Assert.NotNull(field.Column);
                Assert.Equal(expected.TypeName, field.Column.SqlTypeName);
                Assert.Equal(expected.MaxCharacters, field.Column.MaxCharacters);
                Assert.Equal(ColumnFamily.Character, field.Column.Family);

                TableSchema? table = tables.SingleOrDefault(candidate => candidate.Name == expected.Table);
                Assert.NotNull(table);
                ColumnSchema? stored = table.FindColumn(expected.Field);
                Assert.NotNull(stored);
                Assert.Equal(expected.MaxLengthBytes, stored.MaxLengthBytes);
            }
        }

        Assert.Subset(new HashSet<string>(SeededColumnTypes, StringComparer.Ordinal), catalogueTypes);
        _output.WriteLine("Column types in the seeded tables: " + string.Join(", ", catalogueTypes.Order(StringComparer.Ordinal)));
        AssertSelectOnly(recorder);
    }

    /// <summary>
    /// FoodHistory's three Legal Values fields name their code tables and text columns in <c>metaFields</c>, and the
    /// single-field read gives each the rows of that column in the order SQL Server returns them to legacy's unsorted
    /// query, each option's value and label the stored text (A9a). <c>CaseReport</c> has no Legal Values field; its
    /// Comment Legal fields also name code tables but map to text, so they get no options.
    /// </summary>
    [Fact]
    public async Task LegalValuesOptions_EqualCodeTableRows()
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        CancellationToken token = cancellation.Token;
        var recorder = new SqlCommandRecorder();
        SqlMetadataReader reader = CreateReader(recorder);
        await using SqlConnection connection = await TestDatabase.OpenConnectionAsync(token);

        FormMetadata? caseReport = await reader.GetFormMetadataAsync(1, token);
        Assert.NotNull(caseReport);
        Assert.DoesNotContain(caseReport.Fields, field => field.FieldTypeName == LegalValuesTypeName);

        FormMetadata? foodHistory = await reader.GetFormMetadataAsync(2, token);
        Assert.NotNull(foodHistory);
        FieldMetadata[] legalValues =
        [
            .. foodHistory.Fields
                .Where(field => field.FieldTypeName == LegalValuesTypeName)
                .OrderBy(field => field.Name, StringComparer.Ordinal),
        ];
        IReadOnlyList<LegalValuesField> stored = await LegalValuesFieldsAsync(connection, foodHistory.ViewId, token);
        Assert.Equal(
            SeededLegalValuesFields.Select(field => field.Name).Order(StringComparer.Ordinal),
            legalValues.Select(field => field.Name));
        Assert.Equal(
            stored.OrderBy(field => field.Name, StringComparer.Ordinal),
            legalValues.Select(field => new LegalValuesField(
                field.Name,
                field.SourceTableName ?? string.Empty,
                field.TextColumnName ?? string.Empty)));

        foreach (FieldMetadata field in legalValues)
        {
            SeededLegalValuesField seeded = Assert.Single(SeededLegalValuesFields, entry => entry.Name == field.Name);
            Assert.Equal(seeded.SourceTableName, field.SourceTableName);
            Assert.Equal(seeded.TextColumnName, field.TextColumnName);
            Assert.Equal(2, field.PageId);
            Assert.False(field.Sort);

            (string table, string column) = await ResolveCodeColumnAsync(
                connection,
                seeded.SourceTableName,
                seeded.TextColumnName,
                token);
            Assert.Equal(seeded.CatalogueColumn, column);
            IReadOnlyList<string?> rows = await ReadColumnAsync(connection, table, column, token);

            IReadOnlyList<FieldOption> options =
                await reader.ReadOptionsAsync(connection, transaction: null, field, token);

            Assert.Equal(rows, options.Select(option => option.Label));
            Assert.Equal(seeded.Texts.Order(StringComparer.Ordinal), options.Select(option => option.Label).Order(StringComparer.Ordinal));
            Assert.All(options, option =>
            {
                Assert.Equal(FieldValueKind.Text, option.Value.Kind);
                Assert.Equal(option.Label, option.Value.AsText());
            });

            _output.WriteLine(
                $"{foodHistory.Name}.{field.Name} | {field.SourceTableName}.{field.TextColumnName} " +
                $"(catalogue {table}.{column}) | Sort {field.Sort} | {options.Count} options: " +
                string.Join("; ", options.Select(option => option.Label)));
        }

        AssertSelectOnly(recorder);
    }

    /// <summary>
    /// The check-code program is <c>metaViews.CheckCode</c> returned unchanged, for both seeded forms, each far shorter
    /// than <see cref="IDataStore.MaxCheckCodeLength"/>. The read is one command that binds the form's id and a length
    /// of <see cref="IDataStore.MaxCheckCodeLength"/> + 1 code units, both as <see cref="DbType.Int32"/>, so the server
    /// never sends more of an oversized program.
    /// </summary>
    [Fact]
    public async Task GetCheckCodeAsync_ReturnsStoredProgram()
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        CancellationToken token = cancellation.Token;
        var recorder = new SqlCommandRecorder();
        SqlMetadataReader reader = CreateReader(recorder);
        await using SqlConnection verification = await TestDatabase.OpenConnectionAsync(token);

        foreach (SeededForm seeded in SeededForms.Values)
        {
            string stored = Assert.Single(await QueryRowsAsync(
                verification,
                "SELECT [CheckCode] FROM [metaViews] WHERE [ViewId] = @viewId;",
                row => row.IsDBNull(0) ? string.Empty : row.GetString(0),
                token,
                ViewIdParameter(seeded.ViewId)));
            Assert.NotEmpty(stored);
            Assert.True(stored.Length <= IDataStore.MaxCheckCodeLength);

            recorder.Clear();
            string? program = await reader.GetCheckCodeAsync(seeded.ViewId, token);

            Assert.Equal(stored, program);
            RecordedCommand read = Assert.Single(recorder.Commands);
            Assert.True(read.Succeeded, read.ToString());
            Assert.Equal(
                new (string, DbType, object?)[]
                {
                    (MetadataQueries.ViewIdParameter, DbType.Int32, seeded.ViewId),
                    (MetadataQueries.MaxLengthParameter, DbType.Int32, IDataStore.MaxCheckCodeLength + 1),
                },
                read.Parameters.Select(parameter => (parameter.Name, parameter.DbType, parameter.Value)));
        }

        string? caseReport = await reader.GetCheckCodeAsync(1, token);
        Assert.NotNull(caseReport);
        Assert.Contains("Field Sex", caseReport, StringComparison.Ordinal);
        AssertSelectOnly(recorder);
    }

    /// <summary>
    /// Each metadata read sends its own commands, so a second read of the same form sends them again, and every
    /// command is a <c>SELECT</c> that succeeds.
    /// </summary>
    [Fact]
    public async Task Reads_AreUncachedAndSelectOnly()
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        CancellationToken token = cancellation.Token;
        var recorder = new SqlCommandRecorder();
        SqlMetadataReader reader = CreateReader(recorder);

        recorder.Clear();
        Assert.NotNull(await reader.GetFormMetadataAsync(2, token));
        int first = recorder.Commands.Count;
        Assert.True(first > 0, "The first metadata read sent no command.");

        Assert.NotNull(await reader.GetFormMetadataAsync(2, token));

        Assert.True(
            recorder.Commands.Count >= 2 * first,
            $"The second read sent {recorder.Commands.Count - first} commands; the first sent {first}.");
        Assert.Empty(recorder.Failed());
        AssertSelectOnly(recorder);
    }

    /// <summary>A view id absent from <c>metaViews</c> reads as no form, through the port and on a caller's connection.</summary>
    [Fact]
    public async Task UnknownView_ReturnsNull()
    {
        const int unknownViewId = 999;
        using var cancellation = new CancellationTokenSource(TestTimeout);
        CancellationToken token = cancellation.Token;
        var recorder = new SqlCommandRecorder();
        SqlMetadataReader reader = CreateReader(recorder);
        await using SqlConnection connection = await TestDatabase.OpenConnectionAsync(token);

        Assert.Empty(await QueryRowsAsync(
            connection,
            "SELECT [ViewId] FROM [metaViews] WHERE [ViewId] = @viewId;",
            row => Int(row, 0),
            token,
            ViewIdParameter(unknownViewId)));

        FormMetadata? form = await reader.GetFormMetadataAsync(unknownViewId, token);
        (FormMetadata Form, FormSchema Schema)? read =
            await reader.ReadFormAsync(connection, transaction: null, unknownViewId, token);

        Assert.Null(form);
        Assert.False(read.HasValue);
        AssertSelectOnly(recorder);
    }

    /// <summary>
    /// Through the store, each record read given the very instance <c>GetFormMetadataAsync</c> returned uses the
    /// catalogue schema that read built and sends no metadata command, so a request reads its form once. Given a
    /// <see langword="with"/> copy, equal as a record but another instance, the same read first reads the form again
    /// on its own connection, with as many metadata commands as <c>GetFormMetadataAsync</c> sent, then sends the same
    /// record statements. Both instances return the same facts of the first row in <c>UniqueKey</c> order.
    /// </summary>
    [Theory]
    [InlineData("CaseReport")]
    [InlineData("FoodHistory")]
    public async Task StoreRecordReads_ReuseTheSchemaOnlyForTheInstanceTheReaderReturned(string formName)
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        CancellationToken token = cancellation.Token;
        var recorder = new SqlCommandRecorder();
        var store = new SqlServerDataStore(new DatabaseOptions(TestDatabase.ConnectionString), recorder);
        await using SqlConnection verification = await TestDatabase.OpenConnectionAsync(token);

        int viewId = await ViewIdAsync(verification, formName, token);
        string baseTable = Assert.Single(await QueryRowsAsync(
            verification,
            "SELECT DISTINCT [DataTableName] FROM [metaFields] "
            + "WHERE [ViewId] = @viewId AND [DataTableName] IS NOT NULL;",
            row => row.GetString(0),
            token,
            ViewIdParameter(viewId)));

        // A seeded row, which no test edits or deletes, and every row a test adds gets a higher UniqueKey, so this row
        // stays first in the set that includes deleted records while the test runs.
        string firstId = Assert.Single(await QueryRowsAsync(
            verification,
            "SELECT TOP (1) [GlobalRecordId] FROM " + Bracket(baseTable) + " ORDER BY [UniqueKey];",
            row => row.GetString(0),
            token));

        recorder.Clear();
        FormMetadata? form = await store.GetFormMetadataAsync(viewId, token);
        Assert.NotNull(form);
        int formReadCommands = MetadataCommands(recorder.Commands).Count;
        Assert.True(formReadCommands > 0, "GetFormMetadataAsync sent no metadata command.");

        FormMetadata copy = form with { };
        Assert.Equal(form, copy);
        Assert.NotSame(form, copy);

        var byId = new SearchCondition(
            "GlobalRecordId",
            SearchTarget.BaseGlobalRecordId,
            FieldType: null,
            [new SearchAlternative(SearchOperator.Equal, FieldValue.FromText(firstId))]);
        (string Operation, string Expected, Func<FormMetadata, Task<string?>> Read)[] reads =
        [
            (RecordOperations.Count, bool.TrueString, async instance =>
                (await store.CountAsync(instance, includeDeleted: true, token) > 0).ToString()),
            (RecordOperations.At, firstId, async instance =>
                (await store.GetAtPositionAsync(instance, 1, includeDeleted: true, token))?.GlobalRecordId),
            (RecordOperations.Get, firstId, async instance =>
                (await store.GetByIdAsync(instance, firstId, token))?.GlobalRecordId),
            (RecordOperations.SetPosition, "1", async instance =>
                (await store.GetSetPositionAsync(instance, firstId, includeDeleted: true, token))?.Position?.ToString(
                    CultureInfo.InvariantCulture)),
            (RecordOperations.List, firstId, async instance =>
                Assert.Single((await store.ListAsync(
                        instance,
                        new RecordListQuery(Q: null, IncludeDeleted: true, Page: 1, PageSize: 1),
                        token)).Items)
                    .GlobalRecordId),
            (RecordOperations.Search, firstId, async instance =>
                Assert.Single((await store.SearchAsync(
                        instance,
                        [byId],
                        includeDeleted: true,
                        excludeGlobalRecordId: null,
                        token)).Items)
                    .GlobalRecordId),
        ];

        foreach ((string operation, string expected, Func<FormMetadata, Task<string?>> read) in reads)
        {
            recorder.Clear();
            Assert.Equal(expected, await read(form));
            IReadOnlyList<RecordedCommand> reused = recorder.Commands;
            Assert.True(
                MetadataCommands(reused).Count == 0,
                DescribeCommands(operation + " read the metadata again for the instance the reader returned.", reused));
            AssertOneConnectionOfSucceededReads(operation, reused);
            Assert.True(
                reused.All(command => string.Equals(command.Operation, operation, StringComparison.Ordinal)),
                DescribeCommands(operation + " sent a command under another operation.", reused));

            recorder.Clear();
            Assert.Equal(expected, await read(copy));
            IReadOnlyList<RecordedCommand> reread = recorder.Commands;
            IReadOnlyList<RecordedCommand> metadataReads = MetadataCommands(reread);
            Assert.True(
                metadataReads.Count == formReadCommands,
                DescribeCommands(operation + " did not read the form again for a copy.", reread));
            AssertOneConnectionOfSucceededReads(operation, reread);
            RecordedCommand[] recordStatements = [.. reread.Except(metadataReads)];
            Assert.True(
                metadataReads.Max(command => command.Sequence) < recordStatements.Min(command => command.Sequence),
                DescribeCommands(operation + " sent a record statement before its metadata read.", reread));
            Assert.Equal(
                reused.Select(command => command.CommandText),
                recordStatements.Select(command => command.CommandText));
        }
    }

    // The commands the metadata reader sent, by its fixed operation-name prefix.
    private static IReadOnlyList<RecordedCommand> MetadataCommands(IReadOnlyList<RecordedCommand> commands) =>
        [.. commands.Where(command => command.Operation.StartsWith("metadata.", StringComparison.Ordinal))];

    // One read's commands: at least one, each completed and succeeded and none a write, all on one connection.
    private static void AssertOneConnectionOfSucceededReads(string operation, IReadOnlyList<RecordedCommand> commands)
    {
        Assert.True(commands.Count > 0, operation + " sent no command.");
        Guid connection = commands[0].ClientConnectionId;
        Assert.True(
            commands.All(command =>
                command.ClientConnectionId == connection
                && command.Completed
                && command.Succeeded
                && !command.IsWrite),
            DescribeCommands(
                operation + " sent a command on another connection, a failed command or a write.",
                commands));
    }

    private static string DescribeCommands(string message, IReadOnlyList<RecordedCommand> commands) =>
        message
        + Environment.NewLine
        + string.Join(Environment.NewLine, commands.Select(command => command.ToString()));

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

    // An in-memory Legal Values row on an nvarchar(255) page-table column, naming its code table and text column.
    private static FieldMetadata CaseVariantLegalValuesField(
        int fieldId,
        string name,
        int pageId,
        string table,
        string column) =>
        new()
        {
            FieldId = fieldId,
            Name = name,
            FieldTypeId = 17,
            FieldTypeName = LegalValuesTypeName,
            TabIndex = 1,
            PageId = pageId,
            SourceTableName = table,
            CodeColumnName = column,
            TextColumnName = column,
            Column = FieldTypeMapper.DescribeColumn(name, "nvarchar", 510),
        };

    // The code-table rows arrive in the database's own unsorted order, which no second read is promised to repeat, so
    // the texts are compared as a set; each option's value and label are the stored text.
    private static void AssertOptionsAre(IReadOnlyList<string?> rows, IReadOnlyList<FieldOption> options)
    {
        Assert.Equal(
            rows.Order(StringComparer.Ordinal),
            options.Select(option => option.Label).Order(StringComparer.Ordinal));
        Assert.All(options, option => Assert.Equal(FieldValue.FromText(option.Label), option.Value));
    }

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

    // Every command the reader sent was a SELECT, and it sent at least one.
    private static void AssertSelectOnly(SqlCommandRecorder recorder)
    {
        Assert.Empty(recorder.Writes());
        Assert.NotEmpty(recorder.Commands);
        Assert.All(recorder.Commands, command => Assert.False(command.IsWrite));
    }

    // A direct verification query on the test's own connection, outside the reader and its observer. Every value is
    // a parameter.
    private static async Task<IReadOnlyList<T>> QueryRowsAsync<T>(
        SqlConnection connection,
        string text,
        Func<SqlDataReader, T> map,
        CancellationToken token,
        params SqlParameter[] parameters)
    {
        await using var command = new SqlCommand(text, connection);
        command.Parameters.AddRange(parameters);

        var rows = new List<T>();
        await using SqlDataReader reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            rows.Add(map(reader));
        }

        return rows;
    }

    private static SqlParameter ViewIdParameter(int viewId) => new("@viewId", SqlDbType.Int) { Value = viewId };

    // The schema of every seeded table, bound as SCHEMA_ID's sysname argument.
    private static SqlParameter SchemaParameter() => new("@schema", SqlDbType.NVarChar, 128) { Value = "dbo" };

    // Integer columns are read whatever their integer type (metaPages.Position is smallint, COUNT(*) is int).
    private static int Int(SqlDataReader row, int ordinal) =>
        Convert.ToInt32(row.GetValue(ordinal), CultureInfo.InvariantCulture);

    // Whether dbo holds a user table spelled exactly so.
    private static async Task<bool> TableExistsAsync(SqlConnection connection, string table, CancellationToken token)
    {
        IReadOnlyList<int> matches = await QueryRowsAsync(
            connection,
            """
            SELECT t.[object_id]
            FROM sys.tables AS t
            WHERE t.[name] COLLATE Latin1_General_100_BIN2 = @name AND t.[schema_id] = SCHEMA_ID(@schema);
            """,
            row => row.GetInt32(0),
            token,
            new SqlParameter("@name", SqlDbType.NVarChar, 128) { Value = table },
            SchemaParameter());
        return matches.Count == 1;
    }

    // The catalogue columns of the dbo table spelled exactly so, in column_id order, with their sys.types name joined
    // on user_type_id and their raw max_length.
    private static Task<IReadOnlyList<CatalogueColumn>> CatalogueColumnsAsync(
        SqlConnection connection,
        string table,
        CancellationToken token) =>
        QueryRowsAsync(
            connection,
            """
            SELECT c.[name], ty.[name], c.[max_length]
            FROM sys.tables AS t
            INNER JOIN sys.columns AS c ON c.[object_id] = t.[object_id]
            INNER JOIN sys.types AS ty ON ty.[user_type_id] = c.[user_type_id]
            WHERE t.[name] COLLATE Latin1_General_100_BIN2 = @name AND t.[schema_id] = SCHEMA_ID(@schema)
            ORDER BY c.[column_id];
            """,
            row => new CatalogueColumn(row.GetString(0), row.GetString(1), Int(row, 2)),
            token,
            new SqlParameter("@name", SqlDbType.NVarChar, 128) { Value = table },
            SchemaParameter());

    private static bool IsGlobalRecordId(string column) =>
        string.Equals(column, "GlobalRecordId", StringComparison.OrdinalIgnoreCase);

    // A column the reader returned agrees with its catalogue row: the exact name, the type, the length in UTF-16 code
    // units for nvarchar(n) and nchar(n) (max_length / 2, none for max and ntext), and the family
    // ExpectedColumnFamilies gives the catalogue's type name.
    private static void AssertColumnMatches(CatalogueColumn expected, ColumnMetadata column)
    {
        Assert.Equal(expected.Name, column.Name);
        Assert.Equal(expected.TypeName, column.SqlTypeName, ignoreCase: true);
        int? maxCharacters = expected.TypeName is "nvarchar" or "nchar" && expected.MaxLengthBytes > 0
            ? expected.MaxLengthBytes / 2
            : null;
        Assert.Equal(maxCharacters, column.MaxCharacters);
        Assert.True(
            ExpectedColumnFamilies.TryGetValue(expected.TypeName, out ColumnFamily family),
            $"Column {expected.Name} has catalogue type {expected.TypeName}, " +
            "which ExpectedColumnFamilies does not list.");
        Assert.Equal(family, column.Family);
    }

    private static string[] DescribeCounts(IEnumerable<KeyValuePair<string, int>> counts) =>
        [.. counts.Select(entry => $"{entry.Key}: {entry.Value}").Order(StringComparer.Ordinal)];

    private static string DescribeMapping(FieldMetadata field, FieldType fieldType, string? unsupportedType) =>
        $"{field.Name} ({field.FieldTypeName}) -> {WireName(fieldType)} {unsupportedType ?? "-"}";

    // The column type as the parity notes write it, for example nvarchar(255), nvarchar(max) or ntext.
    private static string DescribeColumnType(ColumnMetadata column) => column switch
    {
        { MaxCharacters: int length } => $"{column.SqlTypeName}({length})",
        { SqlTypeName: "nvarchar" or "nchar" } => $"{column.SqlTypeName}(max)",
        _ => column.SqlTypeName,
    };

    // The camelCase spelling the API gives a field type.
    private static string WireName(FieldType fieldType)
    {
        string name = fieldType.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    private sealed record LegalValuesField(string Name, string SourceTableName, string TextColumnName);

    private sealed record SeededForm(
        int ViewId,
        string Name,
        PageMetadata[] Pages,
        int FieldCount,
        IReadOnlyDictionary<int, int> FieldsPerPage,
        IReadOnlyDictionary<string, int> DataColumnsPerTable,
        int DataFieldCount,
        IReadOnlyDictionary<string, int> FieldTypeCounts,
        string[] YesNoFields);

    private sealed record CatalogueColumn(string Name, string TypeName, int MaxLengthBytes);

    private sealed record CharacterColumn(
        int ViewId,
        string Field,
        string Table,
        string TypeName,
        int? MaxCharacters,
        int MaxLengthBytes);

    private sealed record SeededLegalValuesField(
        string Name,
        string SourceTableName,
        string TextColumnName,
        string CatalogueColumn,
        string[] Texts);
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

/// <summary>
/// Unit tests of the object roles <see cref="SqlMetadataReader"/> enforces on what metadata names: which tables a form
/// is read and written through (<see cref="SqlMetadataReader.ResolveRecordTables"/>,
/// <see cref="SqlMetadataReader.IsClaimedByAnotherForm"/>, <see cref="SqlMetadataReader.IsRecordBaseTable"/>,
/// <see cref="SqlMetadataReader.IsRecordPageTable"/>) and which table may serve Legal Values options
/// (<see cref="SqlMetadataReader.IsCodeTable"/>). They need no database: the claims are synthetic rows and the tables
/// are built in memory, so no metadata row or schema object is written, which is why poisoned metadata is proved here
/// rather than stored.
/// </summary>
public sealed class SqlMetadataReaderObjectRoleTests
{
    private const int ViewId = 1;
    private const int OtherViewId = 2;

    /// <summary>
    /// The form's base table, its page ids, the catalogue tables one read returned for its names ("name|shape", as
    /// <see cref="CatalogueTable"/> builds them, the form's own base table first), another form's data-table name and
    /// page ids, and whether that form's claims refuse this one.
    /// </summary>
    public static TheoryData<string, int[], string[], string?, int[], bool> ClaimCases => new()
    {
        // Another form names the base table exactly, or in another letter case that resolves to it.
        { "FoodHistory", [1], ["FoodHistory|base", "FoodHistory1|page"], "FoodHistory", [2, 3], true },
        { "Cases", [1], ["Cases|base", "Cases1|page"], "CASES", [2], true },
        // The base table is one of another form's page tables.
        { "FoodHistory2", [1], ["FoodHistory2|base", "FoodHistory21|page"], "FoodHistory", [2, 3], true },
        // A page table is another form's base table, or one of its page tables, exactly or in another letter case;
        // "Food1" + "2" and "Food" + "12" name the same table.
        { "CaseReport", [1], ["CaseReport|base", "CaseReport1|page"], "CaseReport1", [], true },
        { "Survey", [12], ["Survey|base", "Survey12|page"], "Survey1", [2], true },
        { "Food", [12], ["Food|base", "Food12|page"], "Food1", [2], true },
        { "Food", [12], ["Food|base", "Food12|page"], "FOOD1", [2], true },
        { "Food1", [2], ["Food1|base", "Food12|page"], "Food", [12], true },
        // Two forms that each name only their own tables, as the seeded forms do.
        { "CaseReport", [1], ["CaseReport|base", "CaseReport1|page"], "FoodHistory", [2, 3], false },
        // Distinct tables whose names differ only by letter case, in a case-sensitive database: each form owns the one
        // it names exactly, so neither refuses the other.
        { "Cases", [1], ["Cases|base", "Cases1|page", "cases|base", "cases2|page"], "cases", [2], false },
        { "cases", [2], ["cases|base", "cases2|page", "Cases|base", "Cases1|page"], "Cases", [1], false },
        // A spelling that matches two case variants and neither exactly reaches no table, for either form.
        { "Food", [1], ["Food|base", "Food1|page", "FOOD|base"], "food", [2], false },
        // Another form with no data-table name, or a blank one, claims nothing.
        { "CaseReport", [1], ["CaseReport|base", "CaseReport1|page"], null, [2], false },
        { "3", [1], ["3|base", "31|page"], "", [3], false },
        { "\t3", [1], ["\t3|base", "\t31|page"], "\t", [3], false },
    };

    /// <summary>
    /// A claimed name aliases the form only when it resolves, as the form resolves its own names, to the very base or
    /// page table the form bound. The claims hold the form's own rows, which never count, and another form's base
    /// claim and page claims. The answer does not depend on the order the rows arrive in.
    /// </summary>
    [Theory]
    [MemberData(nameof(ClaimCases))]
    public void IsClaimedByAnotherForm_DecidesByTheTableEachClaimResolvesTo(
        string baseTableName,
        int[] pageIds,
        string[] catalogue,
        string? otherDataTableName,
        int[] otherPageIds,
        bool expected)
    {
        TableSchema[] tables = [.. catalogue.Select(CatalogueTable)];
        (TableSchema baseTable, TableSchema[] pageTables) =
            SqlMetadataReader.ResolveRecordTables(tables, baseTableName, PageTableNames(baseTableName, pageIds));
        List<(int ViewId, string? DataTableName, int? PageId)> claims = [(ViewId, baseTableName, null)];
        claims.AddRange(pageIds.Select(pageId => (ViewId, (string?)baseTableName, (int?)pageId)));
        claims.Add((OtherViewId, otherDataTableName, null));
        claims.AddRange(otherPageIds.Select(pageId => (OtherViewId, otherDataTableName, (int?)pageId)));

        TableSchema[] ownTables = [baseTable, .. pageTables];
        Assert.Equal(expected, SqlMetadataReader.IsClaimedByAnotherForm(ViewId, tables, ownTables, claims));
        Assert.Equal(
            expected,
            SqlMetadataReader.IsClaimedByAnotherForm(ViewId, tables, ownTables, Enumerable.Reverse(claims)));
    }

    /// <summary>
    /// A large malformed claim set, 200,000 rows of one other form with as many data-table names as page ids, none
    /// resolving to the form's tables, is decided in one pass over the rows, each costing one lookup: the rows are
    /// enumerated exactly once and the answer arrives within seconds, where pairing each name with each page id would
    /// mean 10^10 candidates. A last row that does alias the base table is still found.
    /// </summary>
    [Fact]
    public void IsClaimedByAnotherForm_DecidesALargeMalformedClaimSetInOnePass()
    {
        const int rowsPerKind = 100_000;
        TableSchema[] tables = [CatalogueTable("CaseReport|base"), CatalogueTable("CaseReport1|page")];
        (TableSchema baseTable, TableSchema[] pageTables) =
            SqlMetadataReader.ResolveRecordTables(tables, "CaseReport", PageTableNames("CaseReport", [1]));
        TableSchema[] ownTables = [baseTable, .. pageTables];

        int enumerated = 0;
        IEnumerable<(int ViewId, string? DataTableName, int? PageId)> Claims(bool endWithAlias)
        {
            for (int index = 0; index < rowsPerKind; index++)
            {
                enumerated++;
                string name = "Malformed" + index.ToString(CultureInfo.InvariantCulture);
                yield return (OtherViewId, name, null);
                enumerated++;
                yield return (OtherViewId, name, index + 2);
            }

            if (endWithAlias)
            {
                enumerated++;
                yield return (OtherViewId, "CASEREPORT", null);
            }
        }

        var timer = System.Diagnostics.Stopwatch.StartNew();
        bool unrelated = SqlMetadataReader.IsClaimedByAnotherForm(ViewId, tables, ownTables, Claims(endWithAlias: false));
        timer.Stop();

        Assert.False(unrelated);
        Assert.Equal(2 * rowsPerKind, enumerated);
        Assert.True(
            timer.Elapsed < TimeSpan.FromSeconds(5),
            $"Deciding {2 * rowsPerKind} claims took {timer.Elapsed}.");

        enumerated = 0;
        Assert.True(SqlMetadataReader.IsClaimedByAnotherForm(ViewId, tables, ownTables, Claims(endWithAlias: true)));
        Assert.Equal((2 * rowsPerKind) + 1, enumerated);
    }

    /// <summary>
    /// A base table must hold the system columns record SQL addresses: <c>UniqueKey</c> as an identity column,
    /// <c>RecStatus</c> and <c>GlobalRecordId</c>. The seeded base tables hold all three.
    /// </summary>
    [Theory]
    [InlineData(true, true, true, true, true)]
    [InlineData(false, false, true, true, false)]
    [InlineData(true, false, true, true, false)]
    [InlineData(true, true, false, true, false)]
    [InlineData(true, true, true, false, false)]
    public void IsRecordBaseTable_RequiresAnIdentityUniqueKeyRecStatusAndGlobalRecordId(
        bool hasUniqueKey,
        bool uniqueKeyIsIdentity,
        bool hasRecStatus,
        bool hasGlobalRecordId,
        bool expected)
    {
        var columns = new List<ColumnSchema>();
        if (hasUniqueKey)
        {
            columns.Add(Column("UniqueKey", "int", isIdentity: uniqueKeyIsIdentity));
        }

        if (hasRecStatus)
        {
            columns.Add(Column("RecStatus", "smallint"));
        }

        if (hasGlobalRecordId)
        {
            columns.Add(Column("GlobalRecordId", "nvarchar"));
        }

        columns.Add(Column("LastSaveTime", "datetime"));

        Assert.Equal(expected, SqlMetadataReader.IsRecordBaseTable(new TableSchema("SyntheticBase", columns)));
    }

    // The fixed messages of the form refusals ResolveRecordTables raises; they name no table.
    private const string BaseTableMissingMessage = "The form's data table was not found in the database catalogue.";
    private const string PageTableMissingMessage = "A page's data table was not found in the database catalogue.";
    private const string NotRecordTablesMessage = "The form's data tables are not Epi Info record tables.";

    /// <summary>
    /// Base table, page ids, the catalogue tables one read returned for the form's names (each "name|shape", the
    /// shape one of <see cref="CatalogueTable"/>'s), and the refusal's message, or null when the form binds.
    /// </summary>
    public static TheoryData<string, int[], string[], string?> RecordTableCases => new()
    {
        // The seeded shapes: every page has its page table, resolved exactly or ignoring case.
        { "CaseReport", [1], ["CaseReport|base", "CaseReport1|page"], null },
        { "FoodHistory", [2, 3], ["FoodHistory|base", "FoodHistory2|page", "FoodHistory3|page"], null },
        { "foodhistory", [2, 3], ["FoodHistory|base", "FoodHistory2|page", "FoodHistory3|page"], null },
        // DataTableName swapped between the seeded forms: CaseReport's page 1 needs FoodHistory1, and FoodHistory's
        // pages 2 and 3 need CaseReport2 and CaseReport3, tables the other form's identity page ids never name.
        { "FoodHistory", [1], ["FoodHistory|base"], PageTableMissingMessage },
        { "CaseReport", [2, 3], ["CaseReport|base"], PageTableMissingMessage },
        // A record-shaped table no form names, with no table for the page id, and a form missing one of two pages.
        { "Orphan", [7], ["Orphan|base"], PageTableMissingMessage },
        { "FoodHistory", [2, 3], ["FoodHistory|base", "FoodHistory2|page"], PageTableMissingMessage },
        // Two case variants of a page table and no exact match, possible only in a case-sensitive database.
        { "Food", [1], ["Food|base", "FOOD1|page", "food1|page"], PageTableMissingMessage },
        // A page table without GlobalRecordId, a missing base table, and a base table without the record columns.
        { "FoodHistory", [2], ["FoodHistory|base", "FoodHistory2|code"], NotRecordTablesMessage },
        { "CaseReport", [1], ["CaseReport1|page"], BaseTableMissingMessage },
        { "codestate", [1], ["codestate|code", "codestate1|page"], NotRecordTablesMessage },
    };

    /// <summary>
    /// A form binds its base table and the table of every one of its pages, each the very catalogue table its name
    /// resolves to, or is refused with a fixed message that names no table. A missing page table refuses the form, so
    /// metadata pointing a form at another form's base table, or at an orphaned record table, fails for want of a
    /// table for each of its own identity page ids.
    /// </summary>
    [Theory]
    [MemberData(nameof(RecordTableCases))]
    public void ResolveRecordTables_BindsTheBaseAndEveryPageTableOrRefusesTheForm(
        string baseTableName,
        int[] pageIds,
        string[] catalogue,
        string? expectedMessage)
    {
        TableSchema[] tables = [.. catalogue.Select(CatalogueTable)];
        string[] pageTableNames = PageTableNames(baseTableName, pageIds);

        if (expectedMessage is not null)
        {
            InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(() =>
                SqlMetadataReader.ResolveRecordTables(tables, baseTableName, pageTableNames));
            Assert.Equal(expectedMessage, refusal.Message);
            Assert.All(tables, table => Assert.DoesNotContain(table.Name, refusal.Message, StringComparison.Ordinal));
            return;
        }

        (TableSchema baseTable, TableSchema[] pageTables) =
            SqlMetadataReader.ResolveRecordTables(tables, baseTableName, pageTableNames);

        Assert.Same(tables[0], baseTable);
        Assert.Equal(pageTableNames.Length, pageTables.Length);
        for (int index = 0; index < pageTables.Length; index++)
        {
            Assert.Same(tables[index + 1], pageTables[index]);
        }
    }

    /// <summary>
    /// A page table must hold <c>GlobalRecordId</c>, the key that joins it to its base row, as every seeded page table
    /// does; a table of data columns alone, such as a code table, is no page table.
    /// </summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void IsRecordPageTable_RequiresGlobalRecordId(bool hasGlobalRecordId, bool expected)
    {
        var columns = new List<ColumnSchema>();
        if (hasGlobalRecordId)
        {
            columns.Add(Column("GlobalRecordId", "nvarchar"));
        }

        columns.Add(Column("LastName", "nvarchar"));

        Assert.Equal(expected, SqlMetadataReader.IsRecordPageTable(new TableSchema("SyntheticPage", columns)));
    }

    /// <summary>
    /// A code table has the shape legacy gives every code table: a name with the <c>code</c> prefix, in any letter
    /// case, and character columns only, none of them an identity column, as every seeded code table has. It holds no
    /// <c>GlobalRecordId</c>, which every base and page table holds. Beside its <c>State</c> column, one more column
    /// of the given name, type and identity flag, if any, decides: another character column keeps a code table, while
    /// an identity, integer or date column, or <c>GlobalRecordId</c>, makes the table some other object. A page table,
    /// a metadata table or a name with <c>code</c> anywhere but at its start is no code table.
    /// </summary>
    [Theory]
    [InlineData("codestate", null, null, false, true)]
    [InlineData("CODEx", null, null, false, true)]
    [InlineData("codecity", "City", "nvarchar", false, true)]
    [InlineData("codestate", "Notes", "ntext", false, true)]
    [InlineData("codestate", "GlobalRecordId", "nvarchar", false, false)]
    [InlineData("codestate", "Id", "int", true, false)]
    [InlineData("codestate", "Id", "nvarchar", true, false)]
    [InlineData("codestate", "Rank", "int", false, false)]
    [InlineData("codestate", "Updated", "datetime", false, false)]
    [InlineData("CaseReport1", null, null, false, false)]
    [InlineData("metaFields", null, null, false, false)]
    [InlineData("xcode", null, null, false, false)]
    public void IsCodeTable_RequiresTheCodePrefixCharacterColumnsOnlyNoIdentityAndNoGlobalRecordId(
        string tableName,
        string? otherColumnName,
        string? otherColumnType,
        bool otherColumnIsIdentity,
        bool expected)
    {
        var columns = new List<ColumnSchema>();
        if (otherColumnName is not null && otherColumnType is not null)
        {
            columns.Add(Column(otherColumnName, otherColumnType, otherColumnIsIdentity));
        }

        columns.Add(Column("State", "nvarchar"));

        Assert.Equal(expected, SqlMetadataReader.IsCodeTable(new TableSchema(tableName, columns)));
    }

    // Each page's table name, the base table name followed by the page id, as the reader derives it.
    private static string[] PageTableNames(string baseTableName, int[] pageIds) =>
        [.. pageIds.Select(pageId => baseTableName + pageId.ToString(CultureInfo.InvariantCulture))];

    // An in-memory catalogue table from "name|shape": a base table as legacy creates one (an identity UniqueKey,
    // RecStatus, GlobalRecordId and a stamp column), a page table (GlobalRecordId and a data column), or a code table
    // (one nvarchar(255) column).
    private static TableSchema CatalogueTable(string entry)
    {
        string[] parts = entry.Split('|');
        ColumnSchema[] columns = parts[1] switch
        {
            "base" =>
            [
                Column("UniqueKey", "int", isIdentity: true),
                Column("RecStatus", "smallint"),
                Column("GlobalRecordId", "nvarchar"),
                Column("LastSaveTime", "datetime"),
            ],
            "page" => [Column("GlobalRecordId", "nvarchar"), Column("LastName", "nvarchar")],
            "code" => [Column("State", "nvarchar")],
            _ => throw new ArgumentOutOfRangeException(nameof(entry), entry, "Unknown table shape."),
        };

        return new TableSchema(parts[0], columns);
    }

    // An in-memory catalogue column with the max_length sys.columns reports for its type; nvarchar is nvarchar(255).
    private static ColumnSchema Column(string name, string sqlTypeName, bool isIdentity = false) =>
        new(
            name,
            sqlTypeName,
            sqlTypeName switch
            {
                "nvarchar" => 510,
                "ntext" => 16,
                "smallint" => 2,
                "int" => 4,
                _ => 8,
            },
            isNullable: !isIdentity,
            isIdentity);
}
