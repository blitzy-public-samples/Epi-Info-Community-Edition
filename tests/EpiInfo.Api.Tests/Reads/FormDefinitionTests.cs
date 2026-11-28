using System.Collections.Concurrent;
using System.Data;
using System.Globalization;
using System.Net;
using System.Text.Json;
using EpiInfo.Api.Endpoints;
using EpiInfo.Api.Services;
using EpiInfo.Api.Tests.Infrastructure;
using EpiInfo.CheckCode;
using EpiInfo.Domain.Storage;
using EpiInfo.Tests.Shared;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace EpiInfo.Api.Tests.Reads;

/// <summary>
/// SC-8 for the form definition on the rehearsal database: <c>GET /api/forms</c> and <c>GET /api/forms/{viewId}</c>
/// for both seeded forms, each fact compared with the metadata rows read directly from SQL Server.
/// </summary>
/// <remarks>
/// <para>
/// Expected values come from <c>metaViews</c>, <c>metaPages</c>, <c>metaFields</c> joined to <c>metaFieldTypes</c>,
/// the code tables the Legal Values fields name and the page tables' <c>sys.columns</c>, all read by
/// <see cref="FormDefinitionFixture"/>. The only fixed numbers are the reconciled totals of the seeded forms: 61 fields
/// on the <c>CaseReport</c> page and 55 and 33 on the two <c>FoodHistory</c> pages; 43 and 67 data fields; 43 and 74
/// page-table data columns, the difference being the seven <c>Yes/No</c> fields, which sit on <c>tinyint</c> columns
/// and are therefore unsupported and never written. The seeded lengths and bounds the validation rules name are
/// checked by value as well, and so is the number of <c>GEOCODE</c> statements in each form's program, none in
/// <c>CaseReport</c> and one in <c>FoodHistory</c>; their lines are read from the <c>metaViews.CheckCode</c> text.
/// </para>
/// <para>
/// The class reads only metadata, code tables and catalogue views, which no test writes, so its expected values cannot
/// move during a test and it takes no <see cref="DatabaseWriteLock"/>. Bodies are read as JSON elements, never as
/// Domain records, because bounds are wire scalars. Failure messages name the form, page and field; they quote only
/// metadata, never record values or connection settings.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(DatabaseCollections.Database)]
public sealed class FormDefinitionTests : IClassFixture<FormDefinitionFixture>
{
    private const string CaseReport = "CaseReport";
    private const string FoodHistory = "FoodHistory";

    // fieldType spellings of the wire contract.
    private const string LabelType = "label";
    private const string GroupType = "group";
    private const string TextType = "text";
    private const string MultilineType = "multiline";
    private const string NumberType = "number";
    private const string DateType = "date";
    private const string CheckboxType = "checkbox";
    private const string OptionType = "option";
    private const string LegalValuesType = "legalValues";
    private const string UnsupportedType = "unsupported";

    private const string YesNoTypeName = "Yes/No";
    private const string GroupTypeName = "Group";
    private const string GeocodeCommand = "GEOCODE";

    // Stored bounds of date fields, such as 01-15-2011, and their wire form.
    private const string MetadataDateFormat = "MM-dd-yyyy";
    private const string IsoDateFormat = "yyyy-MM-dd";

    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(2);

    private static readonly string[] SeededFormNames = [CaseReport, FoodHistory];

    // The metaFields rows with a null PageId, in ordinal order.
    private static readonly string[] SystemFieldNames = ["GlobalRecordId", "RecStatus", "UniqueKey"];

    // The line breaks of check-code text, \r\n first so that it ends one line rather than two.
    private static readonly string[] CheckCodeLineBreaks = ["\r\n", "\n", "\r"];

    // The fieldType of every seeded metaFieldTypes.Name. Phone Number and Comment Legal are outside the supported
    // names and sit on character columns, so they are text; Yes/No sits on tinyint and Command Button is never
    // supported, so both are unsupported.
    private static readonly IReadOnlyDictionary<string, string> FieldTypeBySeededName =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Text"] = TextType,
            ["Label/Title"] = LabelType,
            [GroupTypeName] = GroupType,
            ["Number"] = NumberType,
            ["Date"] = DateType,
            ["Checkbox"] = CheckboxType,
            ["Multiline"] = MultilineType,
            ["Legal Values"] = LegalValuesType,
            ["Phone Number"] = TextType,
            ["Comment Legal"] = TextType,
            [YesNoTypeName] = UnsupportedType,
            ["Command Button"] = UnsupportedType,
        };

    // The kinds that hold a value in a page-table column; label, group and unsupported fields do not.
    private static readonly HashSet<string> DataFieldTypes = new(StringComparer.Ordinal)
    {
        TextType, MultilineType, NumberType, DateType, CheckboxType, OptionType, LegalValuesType,
    };

    private readonly FormDefinitionFixture _fixture;
    private readonly ITestOutputHelper _output;

    /// <summary>Creates the test class over the shared fixture.</summary>
    /// <param name="fixture">The API host and the per-form SQL and definition reads.</param>
    /// <param name="output">Receives the inventories the parity notes record.</param>
    public FormDefinitionTests(FormDefinitionFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture ?? throw new ArgumentNullException(nameof(fixture));
        _output = output ?? throw new ArgumentNullException(nameof(output));
    }

    [Fact]
    public async Task FormsListNamesBothSeededFormsWithTheirViewIdsAndPageCounts()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        CancellationToken ct = timeout.Token;
        const string FormsPath = "/api/forms";

        using HttpResponseMessage response = await _fixture.Client.GetAsync(FormsPath, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement forms = await ApiTestClient.ReadJsonElementAsync(response, ct);
        ExpectKind(JsonValueKind.Array, forms, "GET " + FormsPath);

        foreach (string formName in SeededFormNames)
        {
            FormDefinitionFixture.SeededForm form = await _fixture.GetFormAsync(formName);
            string context = $"GET {FormsPath} entry {formName}";
            JsonElement[] entries = forms.EnumerateArray()
                .Where(entry => entry.ValueKind == JsonValueKind.Object
                    && entry.TryGetProperty("name", out JsonElement name)
                    && name.ValueKind == JsonValueKind.String
                    && string.Equals(name.GetString(), formName, StringComparison.Ordinal))
                .ToArray();
            Expect(1, entries.Length, context + " count");

            Expect(form.ViewId, GetInt32(entries[0], "viewId", context), context + " viewId");
            Expect(form.Pages.Count, GetInt32(entries[0], "pageCount", context), context + " pageCount");
            _output.WriteLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{formName}: viewId {form.ViewId}, {form.Pages.Count} metaPages rows"));
        }
    }

    [Theory]
    [InlineData(CaseReport)]
    [InlineData(FoodHistory)]
    public async Task PagesFollowMetaPagesPositionOrder(string formName)
    {
        FormDefinitionFixture.SeededForm form = await _fixture.GetFormAsync(formName);

        Expect(form.ViewId, GetInt32(form.Definition, "viewId", formName), formName + " viewId");
        Expect(formName, GetString(form.Definition, "name", formName), formName + " name");

        JsonElement[] pages = Pages(form);
        string[] expected = form.Pages
            .Select(page => DescribePage(page.PageId, page.Name, page.Position))
            .ToArray();
        string[] actual = pages
            .Select(page => DescribePage(
                GetInt32(page, "pageId", formName),
                GetString(page, "name", formName),
                GetInt32(page, "position", formName)))
            .ToArray();
        ExpectSequence(expected, actual, $"{formName}: pages as [pageId 'name' position] in metaPages.Position order");
    }

    [Theory]
    [InlineData(CaseReport, new[] { 61 })]
    [InlineData(FoodHistory, new[] { 55, 33 })]
    public async Task EachPageListsItsMetaFieldsRowsInTabIndexOrder(string formName, int[] reconciledCounts)
    {
        FormDefinitionFixture.SeededForm form = await _fixture.GetFormAsync(formName);
        JsonElement[] pages = Pages(form);
        var counts = new List<int>();

        foreach (FormDefinitionFixture.SeededPage page in form.Pages)
        {
            string pageContext = PageContext(formName, page.PageId);
            JsonElement listedPage = SinglePage(pages, page.PageId, formName);
            FormDefinitionFixture.SeededField[] expected = form.FieldsOnPage(page.PageId);
            JsonElement[] fields = GetArray(listedPage, "fields", pageContext);

            ExpectSequence(
                expected.Select(row => row.Name),
                fields.Select(field => GetString(field, "name", pageContext)),
                $"{pageContext}: field names in metaFields.TabIndex order");

            for (int index = 0; index < expected.Length; index++)
            {
                FormDefinitionFixture.SeededField row = expected[index];
                string context = FieldContext(formName, page.PageId, row.Name);
                decimal tabIndex = row.TabIndex
                    ?? throw new XunitException($"{context}: the page-bound metaFields row has no TabIndex.");

                Expect(page.PageId, GetInt32(fields[index], "pageId", context), context + " pageId");
                JsonElement listedTabIndex = Member(fields[index], "tabIndex", context);
                ExpectKind(JsonValueKind.Number, listedTabIndex, context + " tabIndex");
                Expect(tabIndex, listedTabIndex.GetDecimal(), context + " tabIndex");
                Expect(row.Prompt, GetNullableString(fields[index], "prompt", context), context + " prompt");
            }

            counts.Add(fields.Length);
            _output.WriteLine(
                string.Create(CultureInfo.InvariantCulture, $"{pageContext} '{page.Name}': {fields.Length} fields"));
        }

        ExpectSequence(reconciledCounts, counts, $"{formName}: field counts per page in Position order");
    }

    [Theory]
    [InlineData(CaseReport)]
    [InlineData(FoodHistory)]
    public async Task SystemFieldsAreNotListedOnAnyPage(string formName)
    {
        FormDefinitionFixture.SeededForm form = await _fixture.GetFormAsync(formName);

        ExpectSequence(
            SystemFieldNames,
            form.Fields.Where(row => row.PageId is null).Select(row => row.Name).Order(StringComparer.Ordinal),
            $"{formName}: metaFields rows with a null PageId");

        var listedNames = new List<string>();
        foreach (JsonElement page in Pages(form))
        {
            string pageContext = PageContext(formName, GetInt32(page, "pageId", formName));
            listedNames.AddRange(
                GetArray(page, "fields", pageContext).Select(field => GetString(field, "name", pageContext)));
        }

        foreach (string systemField in SystemFieldNames)
        {
            Assert.False(
                listedNames.Contains(systemField, StringComparer.OrdinalIgnoreCase),
                $"{formName}: the system field {systemField} is listed on a page.");
        }

        Expect(
            form.Fields.Count(row => row.PageId is not null),
            listedNames.Count,
            $"{formName}: listed fields against page-bound metaFields rows");
    }

    [Theory]
    [InlineData(CaseReport, 43, 43)]
    [InlineData(FoodHistory, 67, 74)]
    public async Task DataFieldsAreThePageTableColumnsWithoutTheYesNoFields(
        string formName,
        int reconciledDataFields,
        int reconciledColumns)
    {
        FormDefinitionFixture.SeededForm form = await _fixture.GetFormAsync(formName);
        IReadOnlyList<ListedField> listed = ListedFields(form);
        int dataFieldTotal = 0;
        int columnTotal = 0;
        var perTable = new List<string>();

        foreach (FormDefinitionFixture.SeededPage page in form.Pages)
        {
            string table = form.PageTable(page.PageId);
            string pageContext = $"{PageContext(formName, page.PageId)} (table [{table}])";
            IReadOnlyList<FormDefinitionFixture.SeededColumn> columns = form.Columns[page.PageId];
            var columnTypes = columns.ToDictionary(column => column.Name, column => column.TypeName, StringComparer.Ordinal);

            string[] dataFields = listed
                .Where(field => field.PageId == page.PageId
                    && GetString(field.Element, "fieldType", field.Context)
                        is not (LabelType or GroupType or UnsupportedType))
                .Select(field => field.Name)
                .ToArray();

            foreach (string name in dataFields)
            {
                Assert.True(
                    columnTypes.ContainsKey(name),
                    $"{FieldContext(formName, page.PageId, name)} is a data field with no column in [{table}].");
            }

            // The columns no data field claims are exactly the Yes/No fields of the page, each on a tinyint column.
            string[] unclaimed = columnTypes.Keys
                .Except(dataFields, StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            string[] yesNoFields = form.FieldsOnPage(page.PageId)
                .Where(row => string.Equals(row.FieldTypeName, YesNoTypeName, StringComparison.Ordinal))
                .Select(row => row.Name)
                .Order(StringComparer.Ordinal)
                .ToArray();
            ExpectSequence(yesNoFields, unclaimed, $"{pageContext}: columns without a data field");
            foreach (string name in yesNoFields)
            {
                Expect("tinyint", columnTypes[name], $"{FieldContext(formName, page.PageId, name)} column type");
            }

            dataFieldTotal += dataFields.Length;
            columnTotal += columns.Count;
            perTable.Add(string.Create(CultureInfo.InvariantCulture, $"{table} {columns.Count}"));
        }

        // The data-field count the seeded type names give, so the totals are checked against SQL as well.
        int mappedDataFields = listed.Count(field => DataFieldTypes.Contains(ExpectedFieldType(field.Row, field.Context)));
        Expect(mappedDataFields, dataFieldTotal, $"{formName}: data fields against the mapped metaFields rows");
        Expect(reconciledDataFields, dataFieldTotal, $"{formName}: data fields");
        Expect(reconciledColumns, columnTotal, $"{formName}: page-table data columns");
        _output.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{formName}: {dataFieldTotal} data fields; {columnTotal} page-table data columns " +
                $"({string.Join(", ", perTable)})"));
    }

    [Theory]
    [InlineData(CaseReport)]
    [InlineData(FoodHistory)]
    public async Task FieldTypesFollowTheSeededTypeNames(string formName)
    {
        FormDefinitionFixture.SeededForm form = await _fixture.GetFormAsync(formName);
        IReadOnlyList<ListedField> listed = ListedFields(form);

        foreach (ListedField field in listed)
        {
            string fieldType = GetString(field.Element, "fieldType", field.Context);
            Expect(ExpectedFieldType(field.Row, field.Context), fieldType, field.Context + " fieldType");

            bool unsupported = GetBoolean(field.Element, "unsupported", field.Context);
            Expect(fieldType == UnsupportedType, unsupported, field.Context + " unsupported");
            Expect(
                unsupported ? field.Row.FieldTypeName : null,
                GetNullableString(field.Element, "unsupportedType", field.Context),
                field.Context + " unsupportedType");
        }

        _output.WriteLine($"{formName} metaFieldTypes.Name counts: {CountBy(listed, field => field.Row.FieldTypeName ?? "(none)")}");
        _output.WriteLine(
            $"{formName} fieldType counts: {CountBy(listed, field => GetString(field.Element, "fieldType", field.Context))}");
    }

    [Theory]
    [InlineData(CaseReport)]
    [InlineData(FoodHistory)]
    public async Task GroupsListTheirMembersInStoredListOrder(string formName)
    {
        FormDefinitionFixture.SeededForm form = await _fixture.GetFormAsync(formName);
        var groups = new List<string>();

        foreach (ListedField field in ListedFields(form))
        {
            string context = field.Context + " members";
            JsonElement members = Member(field.Element, "members", field.Context);
            if (ExpectedFieldType(field.Row, field.Context) != GroupType)
            {
                ExpectKind(JsonValueKind.Null, members, context);
                continue;
            }

            // metaFields.List order, which is not tab-index order: the client sorts a group's members itself.
            string[] expected = field.Row.List?.Split(
                ',',
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) ?? [];
            ExpectKind(JsonValueKind.Array, members, context);
            string[] actual = members.EnumerateArray()
                .Select((member, index) =>
                {
                    string memberContext = string.Create(CultureInfo.InvariantCulture, $"{context}[{index}]");
                    ExpectKind(JsonValueKind.String, member, memberContext);
                    return member.GetString() ?? string.Empty;
                })
                .ToArray();
            ExpectSequence(expected, actual, context + " in metaFields.List order");
            groups.Add(string.Create(CultureInfo.InvariantCulture, $"{field.Name} ({actual.Length})"));
        }

        Assert.True(groups.Count > 0, $"{formName} lists no group, so no members were checked.");
        _output.WriteLine($"{formName} groups: {string.Join(", ", groups)}");
    }

    [Theory]
    [InlineData(CaseReport)]
    [InlineData(FoodHistory)]
    public async Task LegalValuesFieldsOfferTheirCodeTableTexts(string formName)
    {
        FormDefinitionFixture.SeededForm form = await _fixture.GetFormAsync(formName);
        var checkedFields = new List<string>();

        foreach (ListedField field in ListedFields(form))
        {
            string context = field.Context + " options";
            JsonElement options = Member(field.Element, "options", field.Context);
            if (ExpectedFieldType(field.Row, field.Context) != LegalValuesType)
            {
                // Comment Legal fields name a code table too, but they are text and offer no options.
                ExpectKind(JsonValueKind.Null, options, context);
                continue;
            }

            if (!form.CodeTables.TryGetValue(field.Name, out FormDefinitionFixture.SeededCodeTable? codeTable))
            {
                throw new XunitException($"{context}: the fixture read no code table for this Legal Values field.");
            }

            // The API offers each distinct stored text once and never a NULL, so the rows equal the options exactly
            // only while the code table holds neither; the seeded tables hold neither.
            string source = $"[{codeTable.Table}].[{codeTable.Column}]";
            Assert.True(
                codeTable.Texts.All(text => text is not null),
                $"{context}: {source} holds a NULL, which is not an option.");
            string[] rows = codeTable.Texts.Select(text => text!).Order(StringComparer.Ordinal).ToArray();
            Assert.True(
                rows.Distinct(StringComparer.Ordinal).Count() == rows.Length,
                $"{context}: {source} repeats a text, which is offered once.");

            ExpectKind(JsonValueKind.Array, options, context);
            string[] labels = options.EnumerateArray()
                .Select((option, index) =>
                {
                    string optionContext = string.Create(CultureInfo.InvariantCulture, $"{context}[{index}]");
                    string label = GetString(option, "label", optionContext);
                    Expect(label, GetString(option, "value", optionContext), optionContext + " value");
                    return label;
                })
                .Order(StringComparer.Ordinal)
                .ToArray();

            // Unordered: the code table is read without ORDER BY and no seeded field sets Sort.
            ExpectSequence(rows, labels, $"{context} against the rows of {source}, sorted ordinally");
            checkedFields.Add(string.Create(CultureInfo.InvariantCulture, $"{field.Name} -> {source} ({rows.Length})"));
        }

        _output.WriteLine(
            $"{formName} legalValues fields: {(checkedFields.Count == 0 ? "none" : string.Join(", ", checkedFields))}");
    }

    [Theory]
    [InlineData(CaseReport, new[] { "CDCIDNumber", "ReportedToCDCDate", "StateIDNumber" })]
    [InlineData(FoodHistory, new[] { "OnsetWeek" })]
    public async Task ValidationCarriesEachFieldRowsMetadata(string formName, string[] readOnlyFields)
    {
        FormDefinitionFixture.SeededForm form = await _fixture.GetFormAsync(formName);
        var listedReadOnly = new List<string>();

        foreach (ListedField field in ListedFields(form))
        {
            string context = field.Context + " validation";
            JsonElement validation = Member(field.Element, "validation", field.Context);
            ExpectKind(JsonValueKind.Object, validation, context);
            string fieldType = GetString(field.Element, "fieldType", field.Context);

            bool required = GetBoolean(validation, "required", context);
            bool readOnly = GetBoolean(validation, "readOnly", context);
            JsonElement maxLength = Member(validation, "maxLength", context);
            JsonElement lower = Member(validation, "lower", context);
            JsonElement upper = Member(validation, "upper", context);
            string? pattern = GetNullableString(validation, "pattern", context);
            if (readOnly)
            {
                listedReadOnly.Add(field.Name);
            }

            Assert.False(required, $"{context}.required is true, but no seeded field is required.");

            if (!DataFieldTypes.Contains(fieldType))
            {
                // A label, group or unsupported field holds no value, so it carries no validation.
                Expect(false, readOnly, context + ".readOnly");
                ExpectKind(JsonValueKind.Null, maxLength, context + ".maxLength");
                ExpectKind(JsonValueKind.Null, lower, context + ".lower");
                ExpectKind(JsonValueKind.Null, upper, context + ".upper");
                Expect(null, pattern, context + ".pattern");
                continue;
            }

            Expect(field.Row.IsRequired, required, context + ".required");
            Expect(field.Row.IsReadOnly, readOnly, context + ".readOnly");
            ExpectNullableInt32(field.Row.MaxLength is > 0 ? field.Row.MaxLength : null, maxLength, context + ".maxLength");
            ExpectBound(field.Row.Lower, fieldType, lower, context + ".lower");
            ExpectBound(field.Row.Upper, fieldType, upper, context + ".upper");
            Expect(
                string.IsNullOrWhiteSpace(field.Row.Pattern) ? null : field.Row.Pattern,
                pattern,
                context + ".pattern");
        }

        Assert.False(
            form.Fields.Any(row => row.IsRequired),
            $"{formName}: a metaFields row has IsRequired = 1, but no seeded field is required.");

        string[] sqlReadOnly = form.Fields
            .Where(row => row.PageId is not null && row.IsReadOnly)
            .Select(row => row.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        ExpectSequence(
            sqlReadOnly,
            listedReadOnly.Order(StringComparer.Ordinal),
            $"{formName}: fields whose validation.readOnly is true against the IsReadOnly = 1 rows");
        ExpectSequence(
            readOnlyFields.Order(StringComparer.Ordinal),
            sqlReadOnly,
            $"{formName}: the seeded read-only fields");
    }

    [Theory]
    [InlineData(CaseReport, "Zip", "maxLength", "5")]
    [InlineData(CaseReport, "Age", "lower", "0")]
    [InlineData(CaseReport, "Age", "upper", "120")]
    [InlineData(CaseReport, "AntibioticUseDays", "lower", "null")]
    [InlineData(CaseReport, "AntibioticUseDays", "upper", "null")]
    [InlineData(CaseReport, "ReportedToCDCDate", "lower", "null")]
    [InlineData(CaseReport, "ReportedToCDCDate", "upper", "null")]
    [InlineData(FoodHistory, "FeverTemp", "lower", "80")]
    [InlineData(FoodHistory, "FeverTemp", "upper", "110")]
    [InlineData(FoodHistory, "OnsetWeek", "lower", "null")]
    [InlineData(FoodHistory, "OnsetWeek", "upper", "null")]
    [InlineData(FoodHistory, "DateofInterview", "lower", "\"2011-01-15\"")]
    [InlineData(FoodHistory, "DateofInterview", "upper", "\"2012-12-31\"")]
    [InlineData(FoodHistory, "DOB", "lower", "null")]
    [InlineData(FoodHistory, "DOB", "upper", "null")]
    public async Task SeededLengthsAndBoundsReachTheWire(
        string formName,
        string fieldName,
        string member,
        string expectedJson)
    {
        FormDefinitionFixture.SeededForm form = await _fixture.GetFormAsync(formName);
        ListedField[] matches = ListedFields(form)
            .Where(field => string.Equals(field.Name, fieldName, StringComparison.Ordinal))
            .ToArray();
        Expect(1, matches.Length, $"{formName}: fields named {fieldName}");

        string context = matches[0].Context + " validation";
        JsonElement validation = Member(matches[0].Element, "validation", matches[0].Context);
        using JsonDocument expected = JsonDocument.Parse(expectedJson);
        ExpectScalar(expected.RootElement, Member(validation, member, context), $"{context}.{member}");
    }

    [Theory]
    [InlineData(CaseReport)]
    [InlineData(FoodHistory)]
    public async Task UnsupportedCheckCodeHoldsOnlyTheGeocodeNotice(string formName)
    {
        FormDefinitionFixture.SeededForm form = await _fixture.GetFormAsync(formName);
        JsonElement[] diagnostics = GetArray(form.Definition, "unsupportedCheckCode", formName);
        int[] geocodeLines = GeocodeStatementLines(form.CheckCode);
        _output.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{formName} unsupportedCheckCode: {diagnostics.Length} entries; GEOCODE statements of " +
                $"metaViews.CheckCode at lines [{DescribeLines(geocodeLines)}]"));

        // Both seeded programs use only the supported subset, so the one construct the definition may report is a
        // GEOCODE statement of the requested form's own program, skipped with an info notice and reported once at its
        // line. The facade without the interpreter reports nothing, which holds here as well; an entry on another
        // form, at a line holding no GEOCODE, or repeating a line fails.
        if (diagnostics.Length > geocodeLines.Length)
        {
            throw new XunitException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{formName} unsupportedCheckCode: {diagnostics.Length} entries, but the program holds " +
                    $"{geocodeLines.Length} GEOCODE statements."));
        }

        var reportedLines = new HashSet<int>();
        for (int index = 0; index < diagnostics.Length; index++)
        {
            string context = string.Create(CultureInfo.InvariantCulture, $"{formName} unsupportedCheckCode[{index}]");
            string severity = GetString(diagnostics[index], "severity", context);
            Assert.False(
                severity is "warning" or "error",
                $"{context}: severity is {severity}, but the seeded programs use only the supported subset.");
            Expect("info", severity, context + " severity");
            Expect(GeocodeCommand, GetString(diagnostics[index], "command", context), context + " command");
            Expect(formName, GetString(diagnostics[index], "form", context), context + " form");

            int line = GetInt32(diagnostics[index], "line", context);
            if (!geocodeLines.Contains(line))
            {
                throw new XunitException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{context}: line {line} holds no GEOCODE statement of the {formName} program, whose " +
                        $"GEOCODE lines are [{DescribeLines(geocodeLines)}]."));
            }

            if (!reportedLines.Add(line))
            {
                throw new XunitException(
                    string.Create(CultureInfo.InvariantCulture, $"{context}: line {line} is reported a second time."));
            }
        }
    }

    // P1, committed with the delivered interpreter, whose Analyze reports each GEOCODE statement it skips. The check
    // above is the permanent P0 one and stays valid for the facade, which reports nothing.
    [Theory]
    [InlineData(CaseReport, 0)]
    [InlineData(FoodHistory, 1)]
    public async Task UnsupportedCheckCodeReportsEachSeededGeocodeOnItsOwnForm(string formName, int seededGeocodes)
    {
        FormDefinitionFixture.SeededForm form = await _fixture.GetFormAsync(formName);
        int[] geocodeLines = GeocodeStatementLines(form.CheckCode);
        Expect(seededGeocodes, geocodeLines.Length, $"{formName}: GEOCODE statements of metaViews.CheckCode");

        // Exactly those statements in source order, each an info notice on the requested form at its own line.
        JsonElement[] diagnostics = GetArray(form.Definition, "unsupportedCheckCode", formName);
        Expect(
            geocodeLines.Length,
            diagnostics.Length,
            $"{formName} unsupportedCheckCode entries against the GEOCODE statements");
        for (int index = 0; index < diagnostics.Length; index++)
        {
            string context = string.Create(CultureInfo.InvariantCulture, $"{formName} unsupportedCheckCode[{index}]");
            Expect("info", GetString(diagnostics[index], "severity", context), context + " severity");
            Expect(formName, GetString(diagnostics[index], "form", context), context + " form");
            Expect(GeocodeCommand, GetString(diagnostics[index], "command", context), context + " command");
            Expect(geocodeLines[index], GetInt32(diagnostics[index], "line", context), context + " line");
            Assert.False(
                string.IsNullOrWhiteSpace(GetString(diagnostics[index], "message", context)),
                $"{context}: the message is blank.");
        }

        _output.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{formName}: {diagnostics.Length} GEOCODE notices at lines [{DescribeLines(geocodeLines)}]"));
    }

    // The 1-based lines of the program's GEOCODE statements: each line whose first token after its indentation is the
    // keyword, compared ignoring case as the grammar's keywords are. Lines end at \r\n, \n or \r. The scan reads the
    // text alone and lexes no comment or string; the seeded programs hold no GEOCODE inside either, and the seed
    // counts UnsupportedCheckCodeReportsEachSeededGeocodeOnItsOwnForm pins fail on a program that does.
    private static int[] GeocodeStatementLines(string? program)
    {
        if (string.IsNullOrEmpty(program))
        {
            return [];
        }

        string[] lines = program.Split(CheckCodeLineBreaks, StringSplitOptions.None);
        return Enumerable.Range(0, lines.Length)
            .Where(index => StartsWithGeocodeToken(lines[index].TrimStart()))
            .Select(index => index + 1)
            .ToArray();
    }

    // The keyword is a whole token when no identifier character, a letter, digit or underscore, follows it.
    private static bool StartsWithGeocodeToken(string text) =>
        text.StartsWith(GeocodeCommand, StringComparison.OrdinalIgnoreCase)
        && (text.Length == GeocodeCommand.Length
            || !(char.IsLetterOrDigit(text[GeocodeCommand.Length]) || text[GeocodeCommand.Length] == '_'));

    private static string DescribeLines(IEnumerable<int> lines) =>
        string.Join(", ", lines.Select(line => line.ToString(CultureInfo.InvariantCulture)));

    // The expected fieldType of a metaFields row: unsupported when encrypted, otherwise by its seeded type name.
    private static string ExpectedFieldType(FormDefinitionFixture.SeededField row, string context)
    {
        if (row.FieldTypeName is null)
        {
            throw new XunitException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{context}: FieldTypeId {row.FieldTypeId} has no metaFieldTypes row."));
        }

        if (row.IsEncrypted)
        {
            return UnsupportedType;
        }

        return FieldTypeBySeededName.TryGetValue(row.FieldTypeName, out string? fieldType)
            ? fieldType
            : throw new XunitException(
                $"{context}: the seeded metaFieldTypes name '{row.FieldTypeName}' has no expected fieldType in this " +
                "test's mapping table.");
    }

    // Every listed field with its metaFields row, matched by page and name.
    private static IReadOnlyList<ListedField> ListedFields(FormDefinitionFixture.SeededForm form)
    {
        var rows = new Dictionary<(int PageId, string Name), FormDefinitionFixture.SeededField>();
        foreach (FormDefinitionFixture.SeededField row in form.Fields)
        {
            if (row.PageId is int pageId && !rows.TryAdd((pageId, row.Name), row))
            {
                throw new XunitException(
                    $"{FieldContext(form.Name, pageId, row.Name)}: two metaFields rows share this name on the page.");
            }
        }

        var listed = new List<ListedField>();
        foreach (JsonElement page in Pages(form))
        {
            int pageId = GetInt32(page, "pageId", form.Name);
            string pageContext = PageContext(form.Name, pageId);
            foreach (JsonElement field in GetArray(page, "fields", pageContext))
            {
                string name = GetString(field, "name", pageContext);
                string context = FieldContext(form.Name, pageId, name);
                if (!rows.TryGetValue((pageId, name), out FormDefinitionFixture.SeededField? row))
                {
                    throw new XunitException($"{context} is listed but has no metaFields row on that page.");
                }

                listed.Add(new ListedField(pageId, name, field, row, context));
            }
        }

        return listed;
    }

    private static JsonElement[] Pages(FormDefinitionFixture.SeededForm form) =>
        GetArray(form.Definition, "pages", form.Name);

    private static JsonElement SinglePage(JsonElement[] pages, int pageId, string formName)
    {
        JsonElement[] matches = pages.Where(page => GetInt32(page, "pageId", formName) == pageId).ToArray();
        Expect(1, matches.Length, $"{PageContext(formName, pageId)}: pages with this pageId");
        return matches[0];
    }

    private static string PageContext(string formName, int pageId) =>
        string.Create(CultureInfo.InvariantCulture, $"{formName} page {pageId}");

    private static string FieldContext(string formName, int pageId, string fieldName) =>
        $"{PageContext(formName, pageId)} field {fieldName}";

    private static string DescribePage(int pageId, string name, int position) =>
        string.Create(CultureInfo.InvariantCulture, $"[{pageId} '{name}' {position}]");

    private static string CountBy(IEnumerable<ListedField> fields, Func<ListedField, string> key) =>
        string.Join(
            ", ",
            fields.GroupBy(key, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => string.Create(CultureInfo.InvariantCulture, $"{group.Key} {group.Count()}")));

    // A number bound is the invariant number stored; a date bound is the stored MM-DD-YYYY date as YYYY-MM-DD; a
    // blank bound, or a bound on any other type, is null.
    private static void ExpectBound(string? stored, string fieldType, JsonElement actual, string what)
    {
        if (string.IsNullOrWhiteSpace(stored) || fieldType is not (NumberType or DateType))
        {
            ExpectKind(JsonValueKind.Null, actual, what);
            return;
        }

        if (fieldType == NumberType)
        {
            if (!double.TryParse(stored, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
            {
                throw new XunitException($"{what}: the stored bound '{stored}' is not an invariant number.");
            }

            ExpectKind(JsonValueKind.Number, actual, what);
            Expect(number, actual.GetDouble(), what);
            return;
        }

        if (!DateTime.TryParseExact(
                stored,
                MetadataDateFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTime date))
        {
            throw new XunitException($"{what}: the stored bound '{stored}' is not an {MetadataDateFormat} date.");
        }

        ExpectKind(JsonValueKind.String, actual, what);
        Expect(date.ToString(IsoDateFormat, CultureInfo.InvariantCulture), actual.GetString(), what);
    }

    private static void ExpectNullableInt32(int? expected, JsonElement actual, string what)
    {
        if (expected is not int value)
        {
            ExpectKind(JsonValueKind.Null, actual, what);
            return;
        }

        ExpectKind(JsonValueKind.Number, actual, what);
        Assert.True(actual.TryGetInt32(out int found), $"{what} is not a 32-bit integer.");
        Expect(value, found, what);
    }

    private static void ExpectScalar(JsonElement expected, JsonElement actual, string what)
    {
        ExpectKind(expected.ValueKind, actual, what);
        switch (expected.ValueKind)
        {
            case JsonValueKind.Number:
                Expect(expected.GetDouble(), actual.GetDouble(), what);
                break;
            case JsonValueKind.String:
                Expect(expected.GetString(), actual.GetString(), what);
                break;
        }
    }

    private static JsonElement Member(JsonElement element, string name, string context)
    {
        ExpectKind(JsonValueKind.Object, element, context);
        return element.TryGetProperty(name, out JsonElement value)
            ? value
            : throw new XunitException($"{context}: member '{name}' is missing.");
    }

    private static JsonElement[] GetArray(JsonElement element, string name, string context)
    {
        JsonElement value = Member(element, name, context);
        ExpectKind(JsonValueKind.Array, value, $"{context} {name}");
        return [.. value.EnumerateArray()];
    }

    private static string GetString(JsonElement element, string name, string context)
    {
        JsonElement value = Member(element, name, context);
        ExpectKind(JsonValueKind.String, value, $"{context} {name}");
        return value.GetString() ?? string.Empty;
    }

    private static string? GetNullableString(JsonElement element, string name, string context)
    {
        JsonElement value = Member(element, name, context);
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        ExpectKind(JsonValueKind.String, value, $"{context} {name}");
        return value.GetString();
    }

    private static int GetInt32(JsonElement element, string name, string context)
    {
        JsonElement value = Member(element, name, context);
        ExpectKind(JsonValueKind.Number, value, $"{context} {name}");
        return value.TryGetInt32(out int number)
            ? number
            : throw new XunitException($"{context} {name} is not a 32-bit integer.");
    }

    private static bool GetBoolean(JsonElement element, string name, string context)
    {
        JsonElement value = Member(element, name, context);
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new XunitException($"{context} {name} is of kind {value.ValueKind}, not a boolean."),
        };
    }

    private static void ExpectKind(JsonValueKind expected, JsonElement actual, string what)
    {
        if (actual.ValueKind != expected)
        {
            throw new XunitException($"{what}: expected a JSON {expected}, found {actual.ValueKind}.");
        }
    }

    private static void Expect<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new XunitException($"{what}: expected {Show(expected)}, found {Show(actual)}.");
        }
    }

    private static void ExpectSequence<T>(IEnumerable<T> expected, IEnumerable<T> actual, string what)
    {
        T[] expectedItems = [.. expected];
        T[] actualItems = [.. actual];
        if (!expectedItems.SequenceEqual(actualItems))
        {
            throw new XunitException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{what}: expected {expectedItems.Length} " +
                    $"[{string.Join(", ", expectedItems.Select(item => Show(item)))}], " +
                    $"found {actualItems.Length} [{string.Join(", ", actualItems.Select(item => Show(item)))}]."));
        }
    }

    private static string Show<T>(T value) => value switch
    {
        null => "null",
        string text => "'" + text + "'",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };

    // A field of the definition with the metaFields row it was matched to and the context its messages name.
    private sealed record ListedField(
        int PageId,
        string Name,
        JsonElement Element,
        FormDefinitionFixture.SeededField Row,
        string Context);
}

/// <summary>
/// The shared state of <see cref="FormDefinitionTests"/>: one API host on the rehearsal database and, per seeded form,
/// its metadata rows read directly from SQL Server and its form definition read once over HTTP.
/// </summary>
/// <remarks>
/// <see cref="InitializeAsync"/> proves the database reachable before any test runs, so a database that is not
/// configured or not reachable fails every test with the <see cref="TestDatabase"/> message; nothing is skipped. Each
/// form is loaded on first use and the result, a failure included, is shared by the later tests. Every value in the
/// SQL is a parameter, and page and code tables are resolved in <c>dbo</c>, never in the login's default schema. The
/// code-table and column identifiers come from the fixture's fixed map of the seeded Legal Values fields, and are
/// bracket-quoted. Each field's <c>metaFields</c> pointer must equal its map entry ordinally, and is then resolved
/// in the <c>dbo</c> catalogue under an explicit case-insensitive collation, so alike on a case-sensitive and a
/// case-insensitive database: the table first, then the column within it, each by the name spelled exactly as the
/// pointer spells it or else by the only case-insensitive match. The resolved table and column must equal the
/// entry's physical names ordinally.
/// </remarks>
public sealed class FormDefinitionFixture : IAsyncLifetime
{
    private const string GlobalRecordIdColumn = "GlobalRecordId";
    private const string LegalValuesTypeName = "Legal Values";

    // The schema testdata/sql creates the page and code tables in. It is bound or quoted explicitly, never left to
    // the login's default schema.
    private const string SeedSchema = "dbo";

    private const string ViewIdSql = "SELECT [ViewId] FROM [metaViews] WHERE [Name] = @name;";

    // The form's check-code program as stored, an ntext value.
    private const string CheckCodeSql = "SELECT [CheckCode] FROM [metaViews] WHERE [Name] = @name;";

    private const string PagesSql = """
        SELECT [PageId], [Name], [Position]
        FROM [metaPages]
        WHERE [ViewId] = @viewId
        ORDER BY [Position], [PageId];
        """;

    private const string FieldsSql = """
        SELECT f.[FieldId], f.[Name], f.[PromptText], f.[TabIndex], f.[PageId], f.[FieldTypeId],
               t.[Name] AS FieldTypeName, f.[IsReadOnly], f.[IsRequired], f.[MaxLength], f.[Lower], f.[Upper],
               f.[Pattern], f.[List], f.[SourceTableName], f.[TextColumnName], f.[Sort], f.[IsEncrypted]
        FROM [metaFields] AS f
        LEFT JOIN [metaFieldTypes] AS t ON t.[FieldTypeId] = f.[FieldTypeId]
        WHERE f.[ViewId] = @viewId
        ORDER BY f.[FieldId];
        """;

    // The page table in the bound schema, dbo, as the legacy page-table name rule gives it.
    private const string ColumnsSql = """
        SELECT c.[name] AS ColumnName, ty.[name] AS TypeName
        FROM sys.columns AS c
        JOIN sys.tables AS tb ON tb.[object_id] = c.[object_id]
        JOIN sys.schemas AS s ON s.[schema_id] = tb.[schema_id]
        JOIN sys.types AS ty ON ty.[user_type_id] = c.[user_type_id]
        WHERE s.[name] = @schema AND tb.[name] = @table
        ORDER BY c.[column_id];
        """;

    // Every table of the bound schema whose name matches a code-table pointer, each with every column whose name
    // matches the pointer's text column, or with a NULL column when none does. Both sides of each name comparison are
    // collated case-insensitively, so the candidates are the same under a case-sensitive and a case-insensitive
    // database collation; the caller picks the exact spelling among them, else the only one.
    private const string CodeTableCandidatesSql = """
        SELECT tb.[name] AS TableName, c.[name] AS ColumnName
        FROM sys.tables AS tb
        JOIN sys.schemas AS s ON s.[schema_id] = tb.[schema_id]
        LEFT JOIN sys.columns AS c
            ON c.[object_id] = tb.[object_id]
            AND c.[name] COLLATE Latin1_General_100_CI_AS = @column COLLATE Latin1_General_100_CI_AS
        WHERE s.[name] = @schema
            AND tb.[name] COLLATE Latin1_General_100_CI_AS = @table COLLATE Latin1_General_100_CI_AS;
        """;

    private static readonly TimeSpan LoadTimeout = TimeSpan.FromMinutes(2);

    // Every page-bound Legal Values field the seed defines (testdata/sql/03_forms.sql, 04_code_tables.sql); CaseReport
    // has none, its code-table pointers belonging to Comment Legal fields, which offer no options. Each entry holds
    // the pointer as metaFields spells it and the dbo table and column it resolves to, as the catalogue spells them.
    // Sex and EthnicityGroup resolve exactly; the metaFields row of LooseStoolsPerDay names the text column
    // LooseStoolsPerDay, which 04_code_tables.sql creates as loosestoolsperday, so that column resolves by its only
    // case-insensitive match. Code-table SQL is built only from these physical names, never from metadata text.
    private static readonly LegalValuesCodeTable[] SeededLegalValuesCodeTables =
    [
        new(
            Form: "FoodHistory",
            Field: "Sex",
            SourceTableName: "codephvs_administrativesex_hl7_2x",
            TextColumnName: "phvs_administrativesex_hl7_2x",
            Table: "codephvs_administrativesex_hl7_2x",
            Column: "phvs_administrativesex_hl7_2x"),
        new(
            Form: "FoodHistory",
            Field: "EthnicityGroup",
            SourceTableName: "codephvs_ethnicitygroup_cdc",
            TextColumnName: "phvs_ethnicitygroup_cdc",
            Table: "codephvs_ethnicitygroup_cdc",
            Column: "phvs_ethnicitygroup_cdc"),
        new(
            Form: "FoodHistory",
            Field: "LooseStoolsPerDay",
            SourceTableName: "codeloosestoolsperday",
            TextColumnName: "LooseStoolsPerDay",
            Table: "codeloosestoolsperday",
            Column: "loosestoolsperday"),
    ];

    private readonly ConcurrentDictionary<string, Lazy<Task<SeededForm>>> _forms = new(StringComparer.Ordinal);

    private ApiFactory? _factory;
    private HttpClient? _client;

    /// <summary>The client of the API host on the rehearsal database.</summary>
    /// <exception cref="InvalidOperationException">The fixture has not been initialised.</exception>
    public HttpClient Client =>
        _client ?? throw new InvalidOperationException("The API client is created in InitializeAsync.");

    /// <summary>Proves the rehearsal database reachable, then starts the API host on it.</summary>
    public async Task InitializeAsync()
    {
        await TestDatabase.EnsureReachableAsync();
        _factory = await ApiFactory.ForDatabaseAsync();
        _client = _factory.CreateClient();
    }

    /// <summary>Disposes the client and stops the host.</summary>
    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
    }

    /// <summary>The metadata rows and the served definition of the form named <paramref name="formName"/>.</summary>
    /// <param name="formName">The <c>metaViews.Name</c> of a seeded form.</param>
    /// <returns>The form, loaded once per fixture.</returns>
    public Task<SeededForm> GetFormAsync(string formName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(formName);
        return _forms.GetOrAdd(
                formName,
                name => new Lazy<Task<SeededForm>>(
                    () => LoadAsync(name),
                    LazyThreadSafetyMode.ExecutionAndPublication))
            .Value;
    }

    private async Task<SeededForm> LoadAsync(string formName)
    {
        using var timeout = new CancellationTokenSource(LoadTimeout);
        CancellationToken ct = timeout.Token;

        int viewId;
        string? checkCode;
        IReadOnlyList<SeededPage> pages;
        IReadOnlyList<SeededField> fields;
        var columns = new Dictionary<int, IReadOnlyList<SeededColumn>>();
        var codeTables = new Dictionary<string, SeededCodeTable>(StringComparer.Ordinal);

        await using (SqlConnection connection = await TestDatabase.OpenConnectionAsync(ct))
        {
            viewId = await ReadViewIdAsync(connection, formName, ct);
            checkCode = await ReadCheckCodeAsync(connection, formName, ct);
            pages = await ReadPagesAsync(connection, viewId, ct);
            fields = await ReadFieldsAsync(connection, viewId, ct);

            foreach (SeededPage page in pages)
            {
                string table = PageTableName(formName, page.PageId);
                IReadOnlyList<SeededColumn> tableColumns = await ReadColumnsAsync(connection, table, ct);
                if (tableColumns.Count == 0)
                {
                    throw new XunitException(
                        $"{formName} page {page.PageId}: the page table [{SeedSchema}].[{table}] has no data column " +
                        "or does not exist.");
                }

                columns.Add(page.PageId, tableColumns);
            }

            Dictionary<string, LegalValuesCodeTable> mapped = SeededLegalValuesCodeTables
                .Where(entry => string.Equals(entry.Form, formName, StringComparison.Ordinal))
                .ToDictionary(entry => entry.Field, StringComparer.Ordinal);
            foreach (SeededField field in fields)
            {
                if (field.PageId is not null
                    && string.Equals(field.FieldTypeName, LegalValuesTypeName, StringComparison.Ordinal))
                {
                    if (!mapped.TryGetValue(field.Name, out LegalValuesCodeTable? entry))
                    {
                        throw new XunitException(
                            string.Create(
                                CultureInfo.InvariantCulture,
                                $"{formName} page {field.PageId} field {field.Name}: the Legal Values field is not " +
                                $"in the fixture's map of seeded code tables."));
                    }

                    codeTables.Add(field.Name, await ReadCodeTableAsync(connection, formName, field, entry, ct));
                }
            }

            // The map equals the seeded set, so an entry no page-bound Legal Values row matched fails as well.
            string[] unmatched = mapped.Keys
                .Where(name => !codeTables.ContainsKey(name))
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (unmatched.Length > 0)
            {
                throw new XunitException(
                    $"{formName}: the fixture's map of seeded code tables names {string.Join(", ", unmatched)}, " +
                    "which metaFields does not hold as page-bound Legal Values fields.");
            }
        }

        JsonElement definition = await ReadDefinitionAsync(formName, viewId, ct);
        return new SeededForm(formName, viewId, checkCode, pages, fields, columns, codeTables, definition);
    }

    private async Task<JsonElement> ReadDefinitionAsync(string formName, int viewId, CancellationToken ct)
    {
        string path = string.Create(CultureInfo.InvariantCulture, $"/api/forms/{viewId}");
        using HttpResponseMessage response = await Client.GetAsync(path, ct);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new XunitException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"GET {path} for {formName} returned {(int)response.StatusCode}, expected 200."));
        }

        return await ApiTestClient.ReadJsonElementAsync(response, ct);
    }

    private static async Task<int> ReadViewIdAsync(SqlConnection connection, string formName, CancellationToken ct)
    {
        await using var command = new SqlCommand(ViewIdSql, connection);
        command.Parameters.Add("@name", SqlDbType.NVarChar, 128).Value = formName;

        var viewIds = new List<int>();
        await using (SqlDataReader reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                viewIds.Add(Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture));
            }
        }

        return viewIds.Count == 1
            ? viewIds[0]
            : throw new XunitException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"metaViews holds {viewIds.Count} rows named {formName}, expected 1."));
    }

    // The metaViews.CheckCode text of the form, unchanged; null for a SQL NULL.
    private static async Task<string?> ReadCheckCodeAsync(
        SqlConnection connection,
        string formName,
        CancellationToken ct)
    {
        await using var command = new SqlCommand(CheckCodeSql, connection);
        command.Parameters.Add("@name", SqlDbType.NVarChar, 128).Value = formName;

        var programs = new List<string?>();
        await using (SqlDataReader reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                programs.Add(reader.IsDBNull(0) ? null : reader.GetString(0));
            }
        }

        return programs.Count == 1
            ? programs[0]
            : throw new XunitException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"metaViews holds {programs.Count} rows named {formName}, expected 1."));
    }

    private static async Task<IReadOnlyList<SeededPage>> ReadPagesAsync(
        SqlConnection connection,
        int viewId,
        CancellationToken ct)
    {
        await using var command = new SqlCommand(PagesSql, connection);
        command.Parameters.Add("@viewId", SqlDbType.Int).Value = viewId;

        var pages = new List<SeededPage>();
        await using SqlDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            pages.Add(new SeededPage(
                Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture),
                reader.GetString(1),
                Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture)));
        }

        return pages.AsReadOnly();
    }

    private static async Task<IReadOnlyList<SeededField>> ReadFieldsAsync(
        SqlConnection connection,
        int viewId,
        CancellationToken ct)
    {
        await using var command = new SqlCommand(FieldsSql, connection);
        command.Parameters.Add("@viewId", SqlDbType.Int).Value = viewId;

        var fields = new List<SeededField>();
        await using SqlDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            fields.Add(new SeededField(
                FieldId: Convert.ToInt32(reader["FieldId"], CultureInfo.InvariantCulture),
                Name: (string)reader["Name"],
                Prompt: NullableString(reader, "PromptText"),
                TabIndex: reader["TabIndex"] is DBNull ? null : Convert.ToDecimal(reader["TabIndex"], CultureInfo.InvariantCulture),
                PageId: NullableInt32(reader, "PageId"),
                FieldTypeId: Convert.ToInt32(reader["FieldTypeId"], CultureInfo.InvariantCulture),
                FieldTypeName: NullableString(reader, "FieldTypeName"),
                IsReadOnly: Flag(reader, "IsReadOnly"),
                IsRequired: Flag(reader, "IsRequired"),
                MaxLength: NullableInt32(reader, "MaxLength"),
                Lower: NullableString(reader, "Lower"),
                Upper: NullableString(reader, "Upper"),
                Pattern: NullableString(reader, "Pattern"),
                List: NullableString(reader, "List"),
                SourceTableName: NullableString(reader, "SourceTableName"),
                TextColumnName: NullableString(reader, "TextColumnName"),
                Sort: Flag(reader, "Sort"),
                IsEncrypted: Flag(reader, "IsEncrypted")));
        }

        return fields.AsReadOnly();
    }

    // The page table's columns, GlobalRecordId excluded: it keys the row and is no data column.
    private static async Task<IReadOnlyList<SeededColumn>> ReadColumnsAsync(
        SqlConnection connection,
        string table,
        CancellationToken ct)
    {
        await using var command = new SqlCommand(ColumnsSql, connection);
        command.Parameters.Add("@schema", SqlDbType.NVarChar, 128).Value = SeedSchema;
        command.Parameters.Add("@table", SqlDbType.NVarChar, 128).Value = table;

        var columns = new List<SeededColumn>();
        await using SqlDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            string name = reader.GetString(0);
            if (!string.Equals(name, GlobalRecordIdColumn, StringComparison.Ordinal))
            {
                columns.Add(new SeededColumn(name, reader.GetString(1)));
            }
        }

        return columns.AsReadOnly();
    }

    // Every row of the code table's text column, read without ORDER BY as legacy reads a code table. The field's
    // metaFields pointer must equal its map entry ordinally; the pointer is then resolved in the dbo catalogue, and
    // the resolved table and column must equal the entry's physical names ordinally. The SELECT is built only from
    // those physical names, never from metadata text.
    private static async Task<SeededCodeTable> ReadCodeTableAsync(
        SqlConnection connection,
        string formName,
        SeededField field,
        LegalValuesCodeTable entry,
        CancellationToken ct)
    {
        string context = string.Create(
            CultureInfo.InvariantCulture,
            $"{formName} page {field.PageId} field {field.Name}");
        if (field.SourceTableName is not { } sourceTable
            || field.TextColumnName is not { } textColumn
            || !string.Equals(sourceTable, entry.SourceTableName, StringComparison.Ordinal)
            || !string.Equals(textColumn, entry.TextColumnName, StringComparison.Ordinal))
        {
            throw new XunitException(
                $"{context}: metaFields names the code table {Show(field.SourceTableName)} and text column " +
                $"{Show(field.TextColumnName)}, but the fixture's map of seeded code tables expects " +
                $"{Show(entry.SourceTableName)} and {Show(entry.TextColumnName)}, compared ordinally.");
        }

        (string resolvedTable, string resolvedColumn) =
            await ResolveCodeTableColumnAsync(connection, context, sourceTable, textColumn, ct);
        if (!string.Equals(resolvedTable, entry.Table, StringComparison.Ordinal)
            || !string.Equals(resolvedColumn, entry.Column, StringComparison.Ordinal))
        {
            throw new XunitException(
                $"{context}: the {SeedSchema} catalogue resolves the metaFields pointer {Show(sourceTable)}." +
                $"{Show(textColumn)} to [{SeedSchema}].[{resolvedTable}].[{resolvedColumn}], but the fixture's map " +
                $"of seeded code tables names [{SeedSchema}].[{entry.Table}].[{entry.Column}], compared ordinally.");
        }

        string sql = "SELECT " + QuoteIdentifier(entry.Column) + " FROM " + QuoteIdentifier(SeedSchema) + "." +
            QuoteIdentifier(entry.Table) + ";";
        await using var command = new SqlCommand(sql, connection);

        var texts = new List<string?>();
        await using SqlDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            texts.Add(reader.IsDBNull(0) ? null : reader.GetString(0));
        }

        return new SeededCodeTable(entry.Table, entry.Column, texts.AsReadOnly());
    }

    // The dbo table and text column a metaFields code-table pointer names, spelled as the catalogue holds them. One
    // catalogue read returns every table matching the pointer's table ignoring case, with its columns matching the
    // pointer's column ignoring case. The table is resolved first among those tables, then the column among the
    // resolved table's columns, each by ResolveCatalogueName.
    private static async Task<(string Table, string Column)> ResolveCodeTableColumnAsync(
        SqlConnection connection,
        string context,
        string pointerTable,
        string pointerColumn,
        CancellationToken ct)
    {
        await using var command = new SqlCommand(CodeTableCandidatesSql, connection);
        command.Parameters.Add("@schema", SqlDbType.NVarChar, 128).Value = SeedSchema;
        command.Parameters.Add("@table", SqlDbType.NVarChar, 128).Value = pointerTable;
        command.Parameters.Add("@column", SqlDbType.NVarChar, 128).Value = pointerColumn;

        var candidates = new List<(string Table, string? Column)>();
        await using (SqlDataReader reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                candidates.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
            }
        }

        string[] tables = candidates
            .Select(candidate => candidate.Table)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string table = ResolveCatalogueName(context, "code table", pointerTable, tables);

        string[] columns = candidates
            .Where(candidate => string.Equals(candidate.Table, table, StringComparison.Ordinal))
            .Select(candidate => candidate.Column)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string column = ResolveCatalogueName(
            context,
            $"text column of [{SeedSchema}].[{table}]",
            pointerColumn,
            columns);

        return (table, column);
    }

    // The catalogue name a pointer resolves to among the names matching it ignoring case: the one spelled ordinally as
    // the pointer spells it, else the only match. No match, or several with none spelled exactly, fails and names the
    // pointer and every match.
    private static string ResolveCatalogueName(string context, string what, string pointer, string[] matches)
    {
        string? exact = Array.Find(matches, name => string.Equals(name, pointer, StringComparison.Ordinal));
        if (exact is not null)
        {
            return exact;
        }

        if (matches.Length == 1)
        {
            return matches[0];
        }

        if (matches.Length == 0)
        {
            throw new XunitException(
                $"{context}: the {SeedSchema} catalogue holds no {what} matching the metaFields pointer " +
                $"{Show(pointer)}, compared ignoring case.");
        }

        throw new XunitException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{context}: the {SeedSchema} catalogue holds {matches.Length} matches ignoring case for the {what} " +
                $"the metaFields pointer {Show(pointer)} names, none spelled exactly as the pointer: " +
                $"{string.Join(", ", matches.Select(name => Show(name)))}; the pointer is ambiguous."));
    }

    private static string PageTableName(string formName, int pageId) =>
        formName + pageId.ToString(CultureInfo.InvariantCulture);

    private static string QuoteIdentifier(string name) => "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";

    // A metadata name as a failure message quotes it.
    private static string Show(string? name) => name is null ? "null" : "'" + name + "'";

    private static string? NullableString(SqlDataReader reader, string column) =>
        reader[column] is string text ? text : null;

    private static int? NullableInt32(SqlDataReader reader, string column) =>
        reader[column] is DBNull ? null : Convert.ToInt32(reader[column], CultureInfo.InvariantCulture);

    // A NULL flag is unset, as metadata readers take it.
    private static bool Flag(SqlDataReader reader, string column) =>
        reader[column] is bool flag && flag;

    /// <summary>One seeded form: its SQL rows and its served definition.</summary>
    /// <param name="Name">The <c>metaViews.Name</c>.</param>
    /// <param name="ViewId">The <c>metaViews.ViewId</c>.</param>
    /// <param name="CheckCode">
    /// The <c>metaViews.CheckCode</c> program as stored; <see langword="null"/> for a SQL <c>NULL</c>.
    /// </param>
    /// <param name="Pages">The <c>metaPages</c> rows in <c>Position</c> order, then <c>PageId</c>.</param>
    /// <param name="Fields">Every <c>metaFields</c> row of the view, system rows included, in <c>FieldId</c> order.</param>
    /// <param name="Columns">Each page table's columns other than <c>GlobalRecordId</c>, by page id.</param>
    /// <param name="CodeTables">The code table of each page-bound Legal Values field, by field name.</param>
    /// <param name="Definition">The body of <c>GET /api/forms/{viewId}</c>.</param>
    public sealed record SeededForm(
        string Name,
        int ViewId,
        string? CheckCode,
        IReadOnlyList<SeededPage> Pages,
        IReadOnlyList<SeededField> Fields,
        IReadOnlyDictionary<int, IReadOnlyList<SeededColumn>> Columns,
        IReadOnlyDictionary<string, SeededCodeTable> CodeTables,
        JsonElement Definition)
    {
        /// <summary>The page table of a page: the form name followed by the page id.</summary>
        /// <param name="pageId">The page id.</param>
        /// <returns>The table name, such as <c>FoodHistory2</c>.</returns>
        public string PageTable(int pageId) => PageTableName(Name, pageId);

        /// <summary>The page's <c>metaFields</c> rows in <c>TabIndex</c> order, then <c>FieldId</c>.</summary>
        /// <param name="pageId">The page id.</param>
        /// <returns>The rows.</returns>
        public SeededField[] FieldsOnPage(int pageId) =>
            Fields.Where(field => field.PageId == pageId)
                .OrderBy(field => field.TabIndex)
                .ThenBy(field => field.FieldId)
                .ToArray();
    }

    /// <summary>A <c>metaPages</c> row.</summary>
    /// <param name="PageId">The page id.</param>
    /// <param name="Name">The page name.</param>
    /// <param name="Position">The 0-based position.</param>
    public sealed record SeededPage(int PageId, string Name, int Position);

    /// <summary>A <c>metaFields</c> row joined to its <c>metaFieldTypes.Name</c>.</summary>
    /// <param name="FieldId">The field id.</param>
    /// <param name="Name">The canonical field name.</param>
    /// <param name="Prompt">The <c>PromptText</c>.</param>
    /// <param name="TabIndex">The <c>TabIndex</c>.</param>
    /// <param name="PageId">The page id; <see langword="null"/> for a system row.</param>
    /// <param name="FieldTypeId">The <c>FieldTypeId</c>.</param>
    /// <param name="FieldTypeName">The <c>metaFieldTypes.Name</c>; <see langword="null"/> without a type row.</param>
    /// <param name="IsReadOnly">The <c>IsReadOnly</c> flag.</param>
    /// <param name="IsRequired">The <c>IsRequired</c> flag.</param>
    /// <param name="MaxLength">The <c>MaxLength</c>.</param>
    /// <param name="Lower">The stored lower bound.</param>
    /// <param name="Upper">The stored upper bound.</param>
    /// <param name="Pattern">The stored pattern.</param>
    /// <param name="List">The stored <c>List</c>, a group's member names.</param>
    /// <param name="SourceTableName">The code table of a Legal Values or Comment Legal field.</param>
    /// <param name="TextColumnName">The code table's text column.</param>
    /// <param name="Sort">The <c>Sort</c> flag.</param>
    /// <param name="IsEncrypted">The <c>IsEncrypted</c> flag.</param>
    public sealed record SeededField(
        int FieldId,
        string Name,
        string? Prompt,
        decimal? TabIndex,
        int? PageId,
        int FieldTypeId,
        string? FieldTypeName,
        bool IsReadOnly,
        bool IsRequired,
        int? MaxLength,
        string? Lower,
        string? Upper,
        string? Pattern,
        string? List,
        string? SourceTableName,
        string? TextColumnName,
        bool Sort,
        bool IsEncrypted);

    /// <summary>A page-table column from <c>sys.columns</c> and <c>sys.types</c>.</summary>
    /// <param name="Name">The column name.</param>
    /// <param name="TypeName">The SQL type name, such as <c>tinyint</c>.</param>
    public sealed record SeededColumn(string Name, string TypeName);

    /// <summary>The rows of a code table's text column, in the order SQL Server returned them.</summary>
    /// <param name="Table">The <c>dbo</c> code table read, spelled as the catalogue holds it.</param>
    /// <param name="Column">The text column read, spelled as the catalogue holds it.</param>
    /// <param name="Texts">Each row's text; <see langword="null"/> for a SQL <c>NULL</c>.</param>
    public sealed record SeededCodeTable(string Table, string Column, IReadOnlyList<string?> Texts);

    // A seeded Legal Values field of the fixture's map: its form and field name, the code-table pointer its metaFields
    // row holds, and the dbo table and column that pointer resolves to, as the catalogue spells them.
    private sealed record LegalValuesCodeTable(
        string Form,
        string Field,
        string SourceTableName,
        string TextColumnName,
        string Table,
        string Column);
}

/// <summary>
/// The P1 proof for the form-definition route <c>GET /api/forms/{viewId}</c>, mapped by <see cref="FormEndpoints"/>
/// and served by <see cref="FormService.GetDefinitionAsync"/>: with the interpreter delivered, the response's
/// <c>unsupportedCheckCode</c> holds the static <c>GEOCODE</c> diagnostic, the diagnostic is logged once without its
/// message or any program value, and the program is analysed but never run.
/// </summary>
/// <remarks>
/// <para>
/// It runs over <see cref="FakeDataStore"/> because the property it proves needs a program the seeded database does
/// not hold: runnable statements carrying a sentinel beside exactly one <c>GEOCODE</c>, on a form whose name and view
/// id are known. The fake also records every store call, so the test needs no SQL Server: the class carries no
/// <c>Integration</c> trait and joins no database collection, which keeps it independent of the database-backed P0
/// definition cases and selected by <c>--filter "Category!=Integration"</c>.
/// </para>
/// <para>
/// The request goes to the API as <c>src/EpiInfo.Api/Program.cs</c> composes it, hosted on TestServer by
/// <see cref="ApiFactory"/>: the committed <c>appsettings.json</c> and <c>appsettings.{environment}.json</c> of the API
/// project, its JSON settings, its service registrations, its pipeline and routes, and its JSON console logging. The
/// factory replaces only what the test observes: <see cref="IDataStore"/> by the fake, and
/// <see cref="ICheckCodeEngine"/> by its <see cref="ApiFactoryBase.Engine"/>, a <see cref="CountingCheckCodeEngine"/>
/// over the real <see cref="CheckCodeEngine"/>; and it adds its <see cref="ApiFactoryBase.Logs"/>, a
/// <see cref="CapturingLoggerProvider"/>, as one more provider under the committed <c>Logging</c> filters. A
/// registration, route or logging setting that <c>Program.cs</c> lacks therefore fails the test, and the captured
/// entries are exactly what production writes. Each environment whose logging configuration is committed is checked.
/// </para>
/// </remarks>
public sealed class FormDefinitionCheckCodeTests
{
    private const string FormName = "FakeCheckCode";

    private const string GeocodeCommand = "GEOCODE";

    // The command of a program or a response over one of the check-code engine's budgets.
    private const string LimitCommand = "LIMIT";

    private const string DiagnosticEventName = "CheckCodeDiagnostic";

    private const int DiagnosticEventId = 1;

    // The line of the statement TwoDiagnosticsProgram places before its GEOCODE.
    private const int StatementLine = 3;

    // The request path of the fake form's definition.
    private static readonly string DefinitionPath =
        string.Create(CultureInfo.InvariantCulture, $"/api/forms/{FakeForms.CheckCodeViewId}");

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public async Task DefinitionReportsAndLogsGeocodeWithoutRunningTheProgram(string environment)
    {
        // The sentinel stands for program text the redaction policy keeps out of the log: a string the program would
        // assign and show only if it ran.
        string sentinel = "Sentinel-Geo-" + Guid.NewGuid().ToString("N");
        string program = SyntheticProgram(sentinel);
        int geocodeLine = SingleLineNumber(program, GeocodeCommand + " ");

        var store = new FakeDataStore(FakeForms.WithCheckCode(program));
        await using var factory = new ApiFactory(ApiFactoryOptions.ForFake(store, environment));
        CountingCheckCodeEngine engine = factory.Engine;
        CapturingLoggerProvider logs = factory.Logs;

        // Creating the client builds and starts the host through Program.cs.
        using HttpClient client = factory.CreateClient();

        // Starting the host is not part of the read, so only the request below is counted and captured.
        engine.Reset();
        store.ClearCalls();
        logs.Clear();

        using HttpResponseMessage response = await client.GetAsync(
            string.Create(CultureInfo.InvariantCulture, $"/api/forms/{FakeForms.CheckCodeViewId}"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ApiTestClient.JsonMediaType, response.Content.Headers.ContentType?.MediaType);
        JsonElement definition = await ApiTestClient.ReadJsonElementAsync(response);
        Assert.Equal(JsonValueKind.Object, definition.ValueKind);
        Assert.Equal(FakeForms.CheckCodeViewId, definition.GetProperty("viewId").GetInt32());
        Assert.Equal(FormName, definition.GetProperty("name").GetString());

        // The static analysis knows the GEOCODE from the text alone; the program holds nothing unsupported, and a GET
        // never yields a runtime error. The members and the severity are spelt as the API's JSON settings write them.
        JsonElement unsupported = definition.GetProperty("unsupportedCheckCode");
        Assert.Equal(JsonValueKind.Array, unsupported.ValueKind);
        JsonElement diagnostic = Assert.Single(unsupported.EnumerateArray());
        Assert.Equal("info", diagnostic.GetProperty("severity").GetString());
        Assert.Equal(FormName, diagnostic.GetProperty("form").GetString());
        JsonElement line = diagnostic.GetProperty("line");
        Assert.Equal(JsonValueKind.Number, line.ValueKind);
        Assert.Equal(geocodeLine, line.GetInt32());
        Assert.Equal(GeocodeCommand, diagnostic.GetProperty("command").GetString());
        JsonElement messageElement = diagnostic.GetProperty("message");
        Assert.Equal(JsonValueKind.String, messageElement.ValueKind);
        string message = messageElement.GetString() ?? string.Empty;
        Assert.False(string.IsNullOrWhiteSpace(message), "The GEOCODE diagnostic has no message.");

        // Analysed once and never evaluated, and the read wrote nothing.
        Assert.Equal(1, engine.AnalyzeCalls);
        Assert.Equal(0, engine.RunCalls);
        Assert.Contains(store.Calls, call => call.Method == nameof(IDataStore.GetCheckCodeAsync));
        Assert.Empty(store.WriteCalls);

        // One entry for the one diagnostic, through the fixed template: form, view id, line and command only.
        CapturedLogEntry entry = Assert.Single(
            logs.Find(captured => captured.Category == typeof(FormService).FullName));
        Assert.Equal(DiagnosticEventId, entry.EventId.Id);
        Assert.Equal(DiagnosticEventName, entry.EventId.Name);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal(FormName, StateValue(entry, "Form"));
        Assert.Equal(FakeForms.CheckCodeViewId, StateValue(entry, "ViewId"));
        Assert.Equal(geocodeLine, StateValue(entry, "Line"));
        Assert.Equal(GeocodeCommand, StateValue(entry, "Command"));
        Assert.Null(entry.ExceptionType);
        Assert.Null(entry.ExceptionMessage);

        // Neither the program's values nor the diagnostic's message reach any captured line, whatever its category.
        Assert.False(logs.ContainsText(sentinel), "A captured log entry holds the program sentinel.");
        Assert.False(logs.ContainsText(message), "A captured log entry holds the diagnostic's message.");
    }

    // An unsupported function's name is program text, and a bracketed one keeps its whitespace and line breaks. The
    // definition reports each unsupported function as the engine did, while its log line names the fixed
    // UNREGISTERED-FUNCTION code whatever its name's spelling: [GOTO] and FORMAT are spelled like vocabulary, yet only
    // the GOTO statement after them is logged as GOTO. The GEOCODE is vocabulary and keeps its own command in the log.
    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public async Task UnsupportedFunctionNameIsReportedButLoggedAsTheFixedCode(string environment)
    {
        string sentinel = "SentinelFn" + Guid.NewGuid().ToString("N");
        string functionName = sentinel + "\r\n Secret";
        string program = TwoDiagnosticsProgram(
            $"ASSIGN LastName = [{functionName}](1)\n"
            + "ASSIGN LastName = [GOTO](1)\n"
            + "ASSIGN LastName = FORMAT(1)\n"
            + "GOTO LastName");
        int keywordFunctionLine = SingleLineNumber(program, "ASSIGN LastName = [GOTO]");
        int formatLine = SingleLineNumber(program, "ASSIGN LastName = FORMAT");
        int gotoLine = SingleLineNumber(program, "GOTO ");
        int geocodeLine = SingleLineNumber(program, GeocodeCommand + " ");

        var store = new FakeDataStore(FakeForms.WithCheckCode(program));
        await using var factory = new ApiFactory(ApiFactoryOptions.ForFake(store, environment));
        CapturingLoggerProvider logs = factory.Logs;

        // Creating the client builds and starts the host through Program.cs; only the request below is captured.
        using HttpClient client = factory.CreateClient();
        logs.Clear();

        using HttpResponseMessage response = await client.GetAsync(DefinitionPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement definition = await ApiTestClient.ReadJsonElementAsync(response);
        Assert.Equal(
            new (string?, string?, int)[]
            {
                ("warning", functionName.ToUpperInvariant(), StatementLine),
                ("warning", "GOTO", keywordFunctionLine),
                ("warning", "FORMAT", formatLine),
                ("warning", "GOTO", gotoLine),
                ("info", GeocodeCommand, geocodeLine),
            },
            Summaries(definition.GetProperty("unsupportedCheckCode")));

        IReadOnlyList<CapturedLogEntry> entries =
            logs.Find(captured => captured.Category == typeof(FormService).FullName);
        Assert.Equal(
            new object?[]
            {
                FormService.UnregisteredFunctionCommand,
                FormService.UnregisteredFunctionCommand,
                FormService.UnregisteredFunctionCommand,
                "GOTO",
                GeocodeCommand,
            },
            entries.Select(entry => StateValue(entry, "Command")));
        Assert.Equal(
            new object?[] { StatementLine, keywordFunctionLine, formatLine, gotoLine, geocodeLine },
            entries.Select(entry => StateValue(entry, "Line")));
        Assert.False(logs.ContainsText(sentinel), "A captured log entry holds the function name.");
        Assert.False(logs.ContainsText(sentinel.ToUpperInvariant()), "A captured log entry holds the function name.");
    }

    // A log sink that throws for every diagnostic line changes nothing the definition answers, and the lines still
    // reach every other sink: each diagnostic's line is attempted on its own.
    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public async Task ThrowingLogSinkLeavesTheDefinitionAndItsDiagnosticsUnchanged(string environment)
    {
        string program = TwoDiagnosticsProgram("GOTO LastName");
        int geocodeLine = SingleLineNumber(program, GeocodeCommand + " ");

        var store = new FakeDataStore(FakeForms.WithCheckCode(program));
        using var throwing = new ThrowingLoggerProvider(typeof(FormService));
        await using var factory = new ApiFactory(ApiFactoryOptions.ForFake(store, environment));
        CapturingLoggerProvider logs = factory.Logs;

        // The derived factory runs the factory's own configuration first, so the throwing sink stands beside the
        // capturing one, and disposing the factory disposes it too.
        using HttpClient client = factory
            .WithWebHostBuilder(builder => builder.ConfigureTestServices(
                services => services.AddSingleton<ILoggerProvider>(throwing)))
            .CreateClient();
        logs.Clear();

        using HttpResponseMessage response = await client.GetAsync(DefinitionPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement definition = await ApiTestClient.ReadJsonElementAsync(response);
        Assert.Equal(FakeForms.CheckCodeViewId, definition.GetProperty("viewId").GetInt32());
        Assert.Equal(
            new (string?, string?, int)[] { ("warning", "GOTO", StatementLine), ("info", GeocodeCommand, geocodeLine) },
            Summaries(definition.GetProperty("unsupportedCheckCode")));

        Assert.Equal(2, throwing.Throws);
        Assert.Equal(
            new object?[] { "GOTO", GeocodeCommand },
            logs.Find(captured => captured.Category == typeof(FormService).FullName)
                .Select(entry => StateValue(entry, "Command")));
        Assert.Empty(logs.Find(captured => captured.ExceptionType is not null));
    }

    // A stored program longer than the store serves whole: the store hands over its first MaxCheckCodeLength + 1 code
    // units, and the definition is still a 200 whose unsupportedCheckCode is one LIMIT warning at line 1, logged once
    // under LIMIT. The program is analysed, never run, and none of its text reaches the log.
    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public async Task OversizedProgramIsOneLimitWarning(string environment)
    {
        string sentinel = "Sentinel-Limit-" + Guid.NewGuid().ToString("N");
        string program = SyntheticProgram(sentinel) + "\n//" + new string('x', IDataStore.MaxCheckCodeLength);

        var store = new FakeDataStore(FakeForms.WithCheckCode(program));
        await using var factory = new ApiFactory(ApiFactoryOptions.ForFake(store, environment));
        CountingCheckCodeEngine engine = factory.Engine;
        CapturingLoggerProvider logs = factory.Logs;

        // Creating the client builds and starts the host through Program.cs; only the request below is counted.
        using HttpClient client = factory.CreateClient();
        engine.Reset();
        logs.Clear();

        using HttpResponseMessage response = await client.GetAsync(DefinitionPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement definition = await ApiTestClient.ReadJsonElementAsync(response);
        Assert.Equal(FakeForms.CheckCodeViewId, definition.GetProperty("viewId").GetInt32());
        Assert.Equal(
            new (string?, string?, int)[] { ("warning", LimitCommand, 1) },
            Summaries(definition.GetProperty("unsupportedCheckCode")));
        Assert.Equal(1, engine.AnalyzeCalls);
        Assert.Equal(0, engine.RunCalls);

        CapturedLogEntry entry = Assert.Single(
            logs.Find(captured => captured.Category == typeof(FormService).FullName));
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(1, StateValue(entry, "Line"));
        Assert.Equal(LimitCommand, StateValue(entry, "Command"));
        Assert.False(logs.ContainsText(sentinel), "A captured log entry holds program text.");

        // The store honours its contract: the first MaxCheckCodeLength + 1 code units, and no more.
        string? served = await store.GetCheckCodeAsync(FakeForms.CheckCodeViewId, CancellationToken.None);
        Assert.Equal(program[..(IDataStore.MaxCheckCodeLength + 1)], served);
    }

    // More diagnostics than one definition lists: 500 GOTO warnings and the GEOCODE notice. unsupportedCheckCode holds
    // exactly MaxDiagnostics entries, the listed warnings in source order and then one LIMIT warning at the line of the
    // first one left out, and the service writes one log line per listed entry, not one per diagnostic found.
    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public async Task DiagnosticsPastTheBudgetAreListedAndLoggedUpToIt(string environment)
    {
        const int Gotos = 500;
        const int Listed = CheckCodeEngine.MaxDiagnostics - 1;
        string program = TwoDiagnosticsProgram(string.Join("\n", Enumerable.Repeat("GOTO LastName", Gotos)));

        var store = new FakeDataStore(FakeForms.WithCheckCode(program));
        await using var factory = new ApiFactory(ApiFactoryOptions.ForFake(store, environment));
        CapturingLoggerProvider logs = factory.Logs;

        // Creating the client builds and starts the host through Program.cs; only the request below is captured.
        using HttpClient client = factory.CreateClient();
        logs.Clear();

        using HttpResponseMessage response = await client.GetAsync(DefinitionPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement definition = await ApiTestClient.ReadJsonElementAsync(response);
        JsonElement unsupported = definition.GetProperty("unsupportedCheckCode");
        (string?, string?, int)[] expected =
        [
            .. Enumerable.Range(0, Listed).Select(index => ("warning", "GOTO", StatementLine + index)),
            ("warning", LimitCommand, StatementLine + Listed),
        ];
        Assert.Equal(expected, Summaries(unsupported));

        // The GEOCODE notice is among those left out: 500 warnings and 1 notice, of which 99 are listed.
        string omitted = (Gotos + 1 - Listed).ToString(CultureInfo.InvariantCulture);
        string? summary = unsupported[Listed].GetProperty("message").GetString();
        Assert.NotNull(summary);
        Assert.Contains(omitted, summary, StringComparison.Ordinal);

        object?[] loggedCommands = [.. Enumerable.Repeat<object?>("GOTO", Listed), LimitCommand];
        Assert.Equal(
            loggedCommands,
            logs.Find(captured => captured.Category == typeof(FormService).FullName)
                .Select(entry => StateValue(entry, "Command")));
    }

    // One Click block on LastName: the statement text from line 3, one statement or several, then the program's one
    // GEOCODE. Line breaks are normalised before the text is placed, so a line break the text itself holds is kept as
    // written.
    private static string TwoDiagnosticsProgram(string statement) => """
        Field LastName
            Click
                {0}
                GEOCODE LastName, Age, OnsetWeek
            End-Click
        End-Field
        """.ReplaceLineEndings("\n").Replace("{0}", statement, StringComparison.Ordinal);

    // Three Field blocks over the fake form's fields. The DOB and Asian blocks hold runnable statements of the
    // supported subset, the Asian block's carrying the sentinel; the LastName Click block holds the program's one
    // GEOCODE. Line breaks are normalised so the 1-based line numbers do not depend on the source file's endings.
    private static string SyntheticProgram(string sentinel) => $"""
        Field DOB
            After
                ASSIGN Age = YEARS(DOB, DOB)
            End-After
        End-Field

        Field Asian
            Click
                IF Asian = (-) THEN
                    CLEAR Age
                ELSE
                    ASSIGN LastName = "{sentinel}"
                    DIALOG "{sentinel}" TITLETEXT = "{sentinel}"
                END-IF
            End-Click
        End-Field

        Field LastName
            Click
                GEOCODE LastName, Age, OnsetWeek
            End-Click
        End-Field
        """.ReplaceLineEndings("\n");

    // The 1-based line of the program's only line that starts, after indentation, with the prefix.
    private static int SingleLineNumber(string program, string prefix)
    {
        string[] lines = program.Split('\n');
        int[] matches = Enumerable.Range(0, lines.Length)
            .Where(index => lines[index].TrimStart().StartsWith(prefix, StringComparison.Ordinal))
            .ToArray();
        return Assert.Single(matches) + 1;
    }

    private static object? StateValue(CapturedLogEntry entry, string key) =>
        Assert.Single(entry.State, pair => pair.Key == key).Value;

    // Each diagnostic of an unsupportedCheckCode array as its severity, command and line, in array order.
    private static (string? Severity, string? Command, int Line)[] Summaries(JsonElement diagnostics) =>
    [
        .. diagnostics.EnumerateArray().Select(diagnostic => (
            diagnostic.GetProperty("severity").GetString(),
            diagnostic.GetProperty("command").GetString(),
            diagnostic.GetProperty("line").GetInt32())),
    ];
}

/// <summary>
/// The published wire schema of the form definition's narrowed values (AAP 0.3.4.3): a validation bound is
/// <c>null</c> when unset, a number on a number field and a <c>YYYY-MM-DD</c> string on a date field, never a boolean;
/// an option value is an Option item's 0-based index or a Legal Values item's text, never <c>null</c>. Each is a oneOf
/// of type-disjoint branches with no reference to the <c>FieldValue</c> union.
/// </summary>
/// <remarks>
/// The document is the one the API serves at <c>/openapi/v1.json</c>, built by the same transformers as the committed
/// document. Describing the endpoints reads no data, so the host has no connection string and the class needs no
/// SQL Server: it carries no <c>Integration</c> trait and joins no database collection, which keeps it selected by
/// <c>--filter "Category!=Integration"</c>.
/// </remarks>
public sealed class FormDefinitionWireSchemaTests
{
    [Theory]
    [InlineData("lower")]
    [InlineData("upper")]
    public async Task BoundIsNullNumberOrDateStringAndNeverTheUnion(string bound)
    {
        JsonElement validation = (await PublishedSchemasAsync()).GetProperty("FieldValidation");

        JsonElement member = validation.GetProperty("properties").GetProperty(bound);
        Assert.Equal(new[] { "oneOf" }, member.EnumerateObject().Select(keyword => keyword.Name));
        Assert.Equal(
            new[] { "null", "number", "string:date" },
            member.GetProperty("oneOf").EnumerateArray().Select(Describe));
        Assert.Contains(bound, validation.GetProperty("required").EnumerateArray().Select(name => name.GetString()));
    }

    // An Option item's value is its 0-based index and a Legal Values item's its text: never null, a boolean or a
    // fraction.
    [Fact]
    public async Task OptionValueIsANonNegativeIndexOrText()
    {
        JsonElement option = (await PublishedSchemasAsync()).GetProperty("FieldOption");

        JsonElement member = option.GetProperty("properties").GetProperty("value");
        Assert.Equal(new[] { "oneOf" }, member.EnumerateObject().Select(keyword => keyword.Name));
        Assert.Equal(
            new[] { "integer:int32>=0", "string" },
            member.GetProperty("oneOf").EnumerateArray().Select(Describe));
        Assert.Contains("value", option.GetProperty("required").EnumerateArray().Select(name => name.GetString()));
    }

    // The document's component schemas, from a host with no connection string.
    private static async Task<JsonElement> PublishedSchemasAsync()
    {
        await using var factory = new ApiFactory(new ApiFactoryOptions { ConnectionMode = ApiConnectionMode.None });
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement document = await ApiTestClient.ReadJsonElementAsync(response);
        return document.GetProperty("components").GetProperty("schemas");
    }

    // A branch as type, then :format and >=minimum when present. Any other keyword, a $ref included, fails the test.
    private static string Describe(JsonElement branch)
    {
        string[] keywords = ["format", "minimum", "type"];
        Assert.All(branch.EnumerateObject(), keyword => Assert.Contains(keyword.Name, keywords));

        string text = branch.GetProperty("type").GetString() ?? string.Empty;
        if (branch.TryGetProperty("format", out JsonElement format))
        {
            text += ":" + format.GetString();
        }

        if (branch.TryGetProperty("minimum", out JsonElement minimum))
        {
            text += ">=" + minimum.GetRawText();
        }

        return text;
    }
}
