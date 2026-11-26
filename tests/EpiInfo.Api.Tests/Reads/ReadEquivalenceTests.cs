using System.Data;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using EpiInfo.Api.Tests.Infrastructure;
using EpiInfo.Domain.Forms;
using EpiInfo.Tests.Shared;
using Microsoft.Data.SqlClient;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace EpiInfo.Api.Tests.Reads;

/// <summary>
/// SC-4 read equivalence and scenario <c>DATA-01</c>: loading every record of both seeded forms through the API
/// returns exactly what SQL Server stores, issues no write and runs no check code.
/// </summary>
/// <remarks>
/// The procedure itself is <see cref="ReadEquivalenceProcedure"/>, which the <c>DATA-01</c> scenario case runs as
/// well. The test never skips: without a reachable rehearsal database <see cref="ApiFactory.ForDatabaseAsync"/> fails
/// with the <see cref="TestDatabase"/> message naming <c>SQL_HOST</c> and <c>MSSQL_SA_PASSWORD</c>.
/// </remarks>
[Trait("Category", "Integration")]
[Collection(DatabaseCollections.Database)]
public sealed class ReadEquivalenceTests
{
    // Every record of both forms is read twice, by position and by id; the bound only stops a hung run.
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(10);

    private readonly ITestOutputHelper _output;

    /// <summary>Creates the test with xUnit's output sink, which receives the per-form summary.</summary>
    /// <param name="output">The test output.</param>
    public ReadEquivalenceTests(ITestOutputHelper output)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
    }

    [Fact]
    public async Task LoadingEveryRecordReturnsStoredValuesAndWritesNothing()
    {
        await using var factory = await ApiFactory.ForDatabaseAsync();
        using var timeout = new CancellationTokenSource(TestTimeout);

        await ReadEquivalenceProcedure.RunAsync(factory, _output, timeout.Token);
    }
}

/// <summary>
/// The one owner of the read-comparison procedure (SC-4, <c>DATA-01</c>): every read route of both seeded forms is
/// compared, value by value, with a direct-SQL <see cref="SqlSnapshot"/>, and the load must leave the five data tables
/// unchanged, send no <c>INSERT</c>, <c>UPDATE</c>, <c>DELETE</c> or <c>MERGE</c>, and never run check code.
/// </summary>
/// <remarks>
/// <para>
/// Expected values come from the snapshot only, never from the code under test and never from literals: the sets,
/// their <c>UniqueKey</c> order and every count are computed from the rows present when the run starts, so records
/// that other tests left behind are compared like seeded ones. The only literals are the form names, the grid columns
/// and the seeded unsupported field names the plan names.
/// </para>
/// <para>
/// Mismatch messages name the form, the record id, the field and the JSON kind or stored CLR type, never a field
/// value.
/// </para>
/// </remarks>
public static partial class ReadEquivalenceProcedure
{
    private const string CaseReport = "CaseReport";
    private const string FoodHistory = "FoodHistory";

    private const string GlobalRecordIdColumn = "GlobalRecordId";
    private const string UniqueKeyColumn = "UniqueKey";
    private const string RecStatusColumn = "RecStatus";

    // The active set is RecStatus = 1; every other value belongs only to the set including deleted records.
    private const int ActiveRecStatus = 1;

    // The largest page the list admits, so the list is read in the fewest requests.
    private const int ListPageSize = 200;

    private const int MismatchListingLimit = 20;

    private const string YesNoTypeName = "Yes/No";
    private const string OnsetWeekField = "OnsetWeek";
    private const string OnsetDateField = "OnsetDate";

    private static readonly string[] FormNames = [CaseReport, FoodHistory];

    // The grid columns the record list returns, in order: the first six text, number, date or legal-values data
    // fields by page Position, then TabIndex.
    private static readonly Dictionary<string, string[]> GridColumns = new(StringComparer.Ordinal)
    {
        [CaseReport] = ["PhoneNumber", "LastName", "FirstName", "Address", "City", "State"],
        [FoodHistory] = ["CaseID", "DateofInterview", "FirstName", "LastName", "Sex", "DOB"],
    };

    // Seeded fields the definition must flag unsupported, so a Record's values must never carry them: the seven
    // Yes/No fields, whose tinyint columns are never mapped or written, and the command button.
    private static readonly Dictionary<string, string[]> SeededUnsupportedFields = new(StringComparer.Ordinal)
    {
        [CaseReport] = [],
        [FoodHistory] =
        [
            "ILL", "DoctorVisit", "Hospitalized", "Antibiotics", "Died", "StoolSubmitted", "O157H7isolated",
            "GetCoordinates",
        ],
    };

    // The wire spelling of each field type, as the API's JSON settings write it (camelCase member names).
    private static readonly Dictionary<string, FieldType> WireFieldTypes = Enum.GetValues<FieldType>()
        .ToDictionary(type => JsonNamingPolicy.CamelCase.ConvertName(type.ToString()), StringComparer.Ordinal);

    /// <summary>
    /// Loads both seeded forms through <paramref name="factory"/> with <c>GET</c> requests only and compares every
    /// response with SQL Server: the form definition's data fields, the counts of both sets, every list page of both
    /// sets, <c>at/{position}</c> across every row and <c>GET</c> by id for every record.
    /// </summary>
    /// <remarks>
    /// The exclusive <see cref="DatabaseWriteLock"/> is taken first and held until the final snapshot comparison, so
    /// no other writer can change a row in between. Callers must not already hold a <see cref="DatabaseWriteLock"/>:
    /// the lock is owned by a dedicated session, so a second acquisition from the same test would wait on its own
    /// lock until it times out. The recorder and the check-code counters of <paramref name="factory"/> are cleared
    /// first, so the assertions cover only this load.
    /// </remarks>
    /// <param name="factory">A database host from <see cref="ApiFactory.ForDatabaseAsync"/>.</param>
    /// <param name="output">Receives the per-form summary; <see langword="null"/> writes nothing.</param>
    /// <param name="ct">Cancels the lock wait, the snapshots and every request.</param>
    /// <returns>A task that completes when every comparison has passed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is <see langword="null"/>.</exception>
    /// <exception cref="XunitException">A response or a stored value breaks the read contract.</exception>
    public static async Task RunAsync(ApiFactory factory, ITestOutputHelper? output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(factory);

        await using DatabaseWriteLock dbLock = await DatabaseWriteLock.AcquireExclusiveAsync(ct);

        // The snapshot reads through TestDatabase, not through the API's observer, so the recorder sees only the
        // commands the API sends.
        factory.Recorder.Clear();
        factory.Engine.Reset();
        SqlSnapshot before = await SqlSnapshot.CaptureAsync(ct);

        var mismatches = new MismatchLog();
        var summaries = new List<FormSummary>(FormNames.Length);
        using (HttpClient client = factory.CreateClient())
        {
            foreach (string formName in FormNames)
            {
                summaries.Add(await LoadAndCompareFormAsync(client, before, formName, mismatches, ct));
            }
        }

        SqlSnapshot after = await SqlSnapshot.CaptureAsync(ct);
        SnapshotDiff diff = SqlSnapshot.Compare(before, after);
        IReadOnlyList<RecordedCommand> commands = factory.Recorder.Commands;
        IReadOnlyList<RecordedCommand> writes = factory.Recorder.Writes();
        int runCalls = factory.Engine.RunCalls;
        int analyzeCalls = factory.Engine.AnalyzeCalls;

        WriteSummary(output, summaries, commands.Count, analyzeCalls, runCalls);

        Assert.True(diff.IsEmpty, diff.ToString());
        Assert.NotEmpty(commands);
        // A write on every read would list thousands of commands, so the message lists the first few, without their
        // parameter values, and counts the rest.
        Assert.True(
            writes.Count == 0,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Loading records sent {writes.Count} write command(s); the first " +
                $"{Math.Min(writes.Count, MismatchListingLimit)}:{Environment.NewLine}") +
            string.Join(
                Environment.NewLine,
                writes.Take(MismatchListingLimit).Select(command => command.Describe(false))));
        Assert.Equal(0, runCalls);
        mismatches.AssertEmpty();

        // Each historical week that disagrees with the reference EPIWEEK came back as stored on both read routes.
        foreach (FormSummary summary in summaries)
        {
            Assert.Equal(summary.OnsetWeekDiffering, summary.OnsetWeekConfirmedByPosition);
            Assert.Equal(summary.OnsetWeekDiffering, summary.OnsetWeekConfirmedById);
        }
    }

    // One form: the definition, the stored-data premises, then every read route, each compared with the snapshot.
    private static async Task<FormSummary> LoadAndCompareFormAsync(
        HttpClient client,
        SqlSnapshot snapshot,
        string formName,
        MismatchLog mismatches,
        CancellationToken ct)
    {
        RecordSets sets = RecordSets.FromSnapshot(snapshot, formName);
        var summary = new FormSummary(formName, sets.Active.Count, sets.All.Count);

        int viewId = await ApiTestClient.GetViewIdAsync(client, formName, ct);
        string formPath = string.Create(CultureInfo.InvariantCulture, $"/api/forms/{viewId}");
        FormModel form = ParseDefinition(formName, viewId, await GetJsonAsync(client, formPath, ct));
        summary.DataFieldCount = form.DataFields.Count;

        IReadOnlySet<string> yesNoFields = await ReadYesNoFieldNamesAsync(formName, ct);
        AssertDataFieldsMatchPageColumns(snapshot, form, yesNoFields);
        CheckStoredDatesAreDateOnly(snapshot, form, mismatches, summary);
        IReadOnlyDictionary<string, OnsetWeekExpectation> onsetWeek =
            FindHistoricalOnsetWeeks(snapshot, form, sets, summary);

        var context = new ComparisonContext(form, sets, snapshot, onsetWeek, mismatches, summary);
        string recordsPath = formPath + "/records";

        await CompareCountAsync(client, recordsPath + "/count", sets.Active.Count, ct);
        await CompareCountAsync(client, recordsPath + "/count?includeDeleted=true", sets.All.Count, ct);

        await CompareListAsync(client, recordsPath, includeDeleted: false, sets.Active, context, ct);
        await CompareListAsync(client, recordsPath, includeDeleted: true, sets.All, context, ct);

        // Every row of the set including deleted records, by its position in that set.
        for (int position = 1; position <= sets.All.Count; position++)
        {
            string at = "at/" + position.ToString(CultureInfo.InvariantCulture);
            JsonElement record = await GetJsonAsync(client, $"{recordsPath}/{at}?includeDeleted=true", ct);
            if (CompareRecord(context, sets.All[position - 1], record, at))
            {
                summary.OnsetWeekConfirmedByPosition++;
            }

            summary.RecordsReadByPosition++;
        }

        // Every record by id, deleted records included.
        foreach (BaseRecord expected in sets.All)
        {
            string path = recordsPath + "/" + Uri.EscapeDataString(expected.GlobalRecordId);
            JsonElement record = await GetJsonAsync(client, path, ct);
            if (CompareRecord(context, expected, record, "by id"))
            {
                summary.OnsetWeekConfirmedById++;
            }

            summary.RecordsReadById++;
        }

        return summary;
    }

    // The data fields, excluded names and page tables of a form definition, with the definition's own invariants.
    private static FormModel ParseDefinition(string formName, int viewId, JsonElement definition)
    {
        string context = "GET /api/forms/" + viewId.ToString(CultureInfo.InvariantCulture);
        Assert.Equal(JsonValueKind.Object, definition.ValueKind);
        Assert.Equal(viewId, RequireInt32(definition, "viewId", context));
        Assert.Equal(formName, RequireString(definition, "name", context));

        var dataFields = new List<DataField>();
        var excluded = new HashSet<string>(StringComparer.Ordinal);
        var unsupported = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var pageTables = new List<string>();

        foreach (JsonElement page in RequireArray(definition, "pages", context).EnumerateArray())
        {
            int pageId = RequireInt32(page, "pageId", context);
            string pageTable = formName + pageId.ToString(CultureInfo.InvariantCulture);
            Assert.True(
                SqlSnapshot.Tables.Contains(pageTable, StringComparer.Ordinal),
                $"{formName} page {pageId} names page table {pageTable}, which is not one of the snapshot tables " +
                $"[{string.Join(", ", SqlSnapshot.Tables)}].");
            pageTables.Add(pageTable);

            foreach (JsonElement field in RequireArray(page, "fields", context).EnumerateArray())
            {
                string name = RequireString(field, "name", context);
                Assert.True(names.Add(name), $"{context} lists field {name} more than once.");
                Assert.Equal(pageId, RequireInt32(field, "pageId", $"{context} field {name}"));

                string wireType = RequireString(field, "fieldType", $"{context} field {name}");
                Assert.True(
                    WireFieldTypes.TryGetValue(wireType, out FieldType fieldType),
                    $"{context} field {name} has a fieldType that is not one of " +
                    $"[{string.Join(", ", WireFieldTypes.Keys)}].");
                bool flaggedUnsupported = RequireBoolean(field, "unsupported", $"{context} field {name}");

                if (flaggedUnsupported || fieldType == FieldType.Unsupported)
                {
                    unsupported.Add(name);
                    excluded.Add(name);
                }
                else if (fieldType is FieldType.Label or FieldType.Group)
                {
                    excluded.Add(name);
                }
                else
                {
                    dataFields.Add(new DataField(name, fieldType, pageTable));
                }
            }
        }

        foreach (string name in SeededUnsupportedFields[formName])
        {
            Assert.True(
                unsupported.Contains(name),
                $"{context} does not flag seeded field {name} unsupported, so its absence from values proves nothing.");
        }

        foreach (string name in GridColumns[formName])
        {
            Assert.True(
                dataFields.Exists(field => string.Equals(field.Name, name, StringComparison.Ordinal)),
                $"{context} has no data field {name}, which the record list returns as a grid column.");
        }

        return new FormModel(formName, dataFields, excluded, unsupported, pageTables);
    }

    // The names of the form's Yes/No fields, read straight from the metadata on a test connection of its own, so the
    // API's command recorder never sees it.
    private static async Task<IReadOnlySet<string>> ReadYesNoFieldNamesAsync(string formName, CancellationToken ct)
    {
        const string Query = """
            SELECT f.[Name]
            FROM [dbo].[metaFields] AS f
            INNER JOIN [dbo].[metaFieldTypes] AS t ON t.[FieldTypeId] = f.[FieldTypeId]
            INNER JOIN [dbo].[metaViews] AS v ON v.[ViewId] = f.[ViewId]
            WHERE v.[Name] = @formName AND t.[Name] = @typeName;
            """;

        await using SqlConnection connection = await TestDatabase.OpenConnectionAsync(ct);
        await using var command = new SqlCommand(Query, connection);
        command.Parameters.Add(new SqlParameter("@formName", SqlDbType.NVarChar, 64) { Value = formName });
        command.Parameters.Add(new SqlParameter("@typeName", SqlDbType.NVarChar, 50) { Value = YesNoTypeName });

        var names = new HashSet<string>(StringComparer.Ordinal);
        await using SqlDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    // The independent check of the data-field set: per page table, the stored columns other than GlobalRecordId and
    // the Yes/No columns, which the API never maps, are exactly the definition's data fields on that page.
    private static void AssertDataFieldsMatchPageColumns(
        SqlSnapshot snapshot,
        FormModel form,
        IReadOnlySet<string> yesNoFields)
    {
        // Every snapshot page table of this form is a page of the definition, and the reverse.
        string[] snapshotPageTables = SqlSnapshot.Tables
            .Where(table => IsPageTableOf(table, form.Name))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(snapshotPageTables, form.PageTables.Order(StringComparer.Ordinal).ToArray());

        var pageColumns = new HashSet<string>(StringComparer.Ordinal);
        foreach (string pageTable in form.PageTables)
        {
            SnapshotRow first = snapshot.Rows(pageTable).FirstOrDefault()
                ?? throw new XunitException($"Page table {pageTable} holds no row, so its columns cannot be read.");
            string[] columns = first.Values.Keys
                .Where(column => !string.Equals(column, GlobalRecordIdColumn, StringComparison.Ordinal))
                .ToArray();
            pageColumns.UnionWith(columns);

            var expected = new SortedSet<string>(
                columns.Where(column => !yesNoFields.Contains(column)),
                StringComparer.Ordinal);
            var actual = new SortedSet<string>(
                form.DataFields
                    .Where(field => string.Equals(field.PageTable, pageTable, StringComparison.Ordinal))
                    .Select(field => field.Name),
                StringComparer.Ordinal);
            Assert.True(
                expected.SetEquals(actual),
                $"{form.Name} page table {pageTable}: columns without a data field " +
                $"[{string.Join(", ", expected.Except(actual, StringComparer.Ordinal))}], " +
                "data fields without a column " +
                $"[{string.Join(", ", actual.Except(expected, StringComparer.Ordinal))}].");

            // The Yes/No columns removed above are the tinyint columns the API leaves unmapped (C-2).
            foreach (SnapshotRow row in snapshot.Rows(pageTable))
            {
                foreach (string column in columns.Where(yesNoFields.Contains))
                {
                    object? value = row[column];
                    Assert.True(
                        value is null or byte,
                        $"{pageTable} {row.GlobalRecordId} Yes/No column {column} holds a {DescribeStored(value)}, " +
                        "not a tinyint.");
                }
            }
        }

        foreach (string name in yesNoFields)
        {
            Assert.True(pageColumns.Contains(name), $"{form.Name} Yes/No field {name} has no page-table column.");
            Assert.True(
                form.UnsupportedNames.Contains(name),
                $"{form.Name} Yes/No field {name} is not flagged unsupported.");
        }
    }

    // A page table is the form name followed by its PageId (legacy Page.TableName).
    private static bool IsPageTableOf(string table, string formName) =>
        table.Length > formName.Length &&
        table.StartsWith(formName, StringComparison.Ordinal) &&
        table[formName.Length..].All(char.IsAsciiDigit);

    // Every stored value of a date field is at 00:00:00, so the date-only wire value loses nothing (C-15). The
    // save stamps of the base table are not date fields and are not checked.
    private static void CheckStoredDatesAreDateOnly(
        SqlSnapshot snapshot,
        FormModel form,
        MismatchLog mismatches,
        FormSummary summary)
    {
        foreach (DataField field in form.DataFields.Where(field => field.Type == FieldType.Date))
        {
            foreach (SnapshotRow row in snapshot.Rows(field.PageTable))
            {
                switch (row[field.Name])
                {
                    case null:
                        break;
                    case DateTime stored when stored.TimeOfDay == TimeSpan.Zero:
                        summary.StoredDatesChecked++;
                        break;
                    case DateTime:
                        summary.StoredDatesChecked++;
                        mismatches.Add(
                            $"{form.Name} {row.GlobalRecordId} {field.Name} [stored]: " +
                            "the stored date has a time of day.");
                        break;
                    case object other:
                        mismatches.Add(
                            $"{form.Name} {row.GlobalRecordId} {field.Name} [stored]: a date field stores a " +
                            $"{DescribeStored(other)}, not a DateTime.");
                        break;
                }
            }
        }
    }

    // DATA-01: the stored OnsetWeek values that disagree with the reference EPIWEEK of their OnsetDate. The API must
    // return each of them as stored, which proves it does not recompute the week on load.
    private static Dictionary<string, OnsetWeekExpectation> FindHistoricalOnsetWeeks(
        SqlSnapshot snapshot,
        FormModel form,
        RecordSets sets,
        FormSummary summary)
    {
        var differing = new Dictionary<string, OnsetWeekExpectation>(StringComparer.Ordinal);
        if (!string.Equals(form.Name, FoodHistory, StringComparison.Ordinal))
        {
            return differing;
        }

        DataField week = form.Require(OnsetWeekField, FieldType.Number);
        DataField date = form.Require(OnsetDateField, FieldType.Date);
        foreach (BaseRecord record in sets.All)
        {
            object? storedWeek = snapshot.Row(week.PageTable, record.GlobalRecordId)?[week.Name];
            object? storedDate = snapshot.Row(date.PageTable, record.GlobalRecordId)?[date.Name];
            if (storedWeek is not double weekValue || storedDate is not DateTime onsetDate)
            {
                // A wrong CLR type is reported by the value comparison; only pairs of stored values count here.
                continue;
            }

            summary.OnsetWeekPairsStored++;
            if (ReferenceEpiWeek(onsetDate) is int reference && weekValue != reference)
            {
                differing.Add(record.GlobalRecordId, new OnsetWeekExpectation(weekValue, reference));
            }
        }

        summary.OnsetWeekDiffering = differing.Count;
        Assert.True(
            differing.Count > 0,
            string.Create(
                CultureInfo.InvariantCulture,
                $"None of the {summary.OnsetWeekPairsStored} stored {week.Name} values differs from the reference " +
                $"EPIWEEK of {date.Name}, so DATA-01 cannot show that loading keeps historical values; the seed " +
                $"holds 286 such records."));
        return differing;
    }

    // count over the active set, or over every row with includeDeleted=true.
    private static async Task CompareCountAsync(HttpClient client, string path, int expected, CancellationToken ct)
    {
        JsonElement body = await GetJsonAsync(client, path, ct);
        Assert.Equal(JsonValueKind.Object, body.ValueKind);
        Assert.Equal(expected, RequireInt32(body, "count", "GET " + path));
    }

    // Every page of the list over one set, then one page past the end, which must be empty.
    private static async Task CompareListAsync(
        HttpClient client,
        string recordsPath,
        bool includeDeleted,
        IReadOnlyList<BaseRecord> set,
        ComparisonContext context,
        CancellationToken ct)
    {
        string flag = includeDeleted ? "true" : "false";
        string[] gridColumns = GridColumns[context.Form.Name];
        var listedIds = new List<string>(set.Count);
        int page = 0;

        // Bounded, so a list that never reaches its total cannot loop for ever.
        int maxPages = (set.Count / ListPageSize) + 1;
        while (page < maxPages)
        {
            page++;
            string path = ListPath(recordsPath, flag, page);
            JsonElement body = await ReadListPageAsync(client, path, set.Count, page, ct);
            context.Summary.ListPagesRead++;

            JsonElement items = body.GetProperty("items");
            foreach (JsonElement item in items.EnumerateArray())
            {
                string where = string.Create(CultureInfo.InvariantCulture, $"list includeDeleted={flag} page {page}");
                string id = RequireString(item, "globalRecordId", where);
                int index = listedIds.Count;
                listedIds.Add(id);
                if (index >= set.Count || !string.Equals(set[index].GlobalRecordId, id, StringComparison.Ordinal))
                {
                    // The order check after the loop names the first position that differs.
                    continue;
                }

                CompareSummary(context, set[index], item, gridColumns, where);
            }

            if (items.GetArrayLength() == 0 || listedIds.Count >= set.Count)
            {
                break;
            }
        }

        AssertSameIds(set, listedIds, $"{context.Form.Name} list includeDeleted={flag}");

        string pastEnd = ListPath(recordsPath, flag, page + 1);
        JsonElement empty = await ReadListPageAsync(client, pastEnd, set.Count, page + 1, ct);
        context.Summary.ListPagesRead++;
        Assert.True(
            empty.GetProperty("items").GetArrayLength() == 0,
            $"GET {pastEnd}, one page past the end, returned items; it must return an empty items array.");
    }

    private static string ListPath(string recordsPath, string flag, int page) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{recordsPath}?includeDeleted={flag}&page={page}&pageSize={ListPageSize}");

    // One list page with its envelope checked: total is the set size and page and pageSize echo the request.
    private static async Task<JsonElement> ReadListPageAsync(
        HttpClient client,
        string path,
        int expectedTotal,
        int page,
        CancellationToken ct)
    {
        JsonElement body = await GetJsonAsync(client, path, ct);
        string context = "GET " + path;
        Assert.Equal(JsonValueKind.Object, body.ValueKind);
        Assert.Equal(expectedTotal, RequireInt32(body, "total", context));
        Assert.Equal(page, RequireInt32(body, "page", context));
        Assert.Equal(ListPageSize, RequireInt32(body, "pageSize", context));
        _ = RequireArray(body, "items", context);
        return body;
    }

    // The listed ids are the set's ids in UniqueKey order; the message names the first position that differs.
    private static void AssertSameIds(IReadOnlyList<BaseRecord> set, List<string> listedIds, string what)
    {
        int common = Math.Min(set.Count, listedIds.Count);
        for (int index = 0; index < common; index++)
        {
            if (!string.Equals(set[index].GlobalRecordId, listedIds[index], StringComparison.Ordinal))
            {
                throw new XunitException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{what}: item {index + 1} is {listedIds[index]}, expected {set[index].GlobalRecordId} " +
                        $"in UniqueKey order."));
            }
        }

        Assert.True(
            set.Count == listedIds.Count,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{what}: listed {listedIds.Count} record(s); the set holds {set.Count}."));
    }


    // A list item: its identity and status, then its grid columns in order, each equal to the stored value.
    private static void CompareSummary(
        ComparisonContext context,
        BaseRecord expected,
        JsonElement item,
        string[] gridColumns,
        string where)
    {
        string form = context.Form.Name;
        string id = expected.GlobalRecordId;
        CompareInt32Member(context, item, "uniqueKey", expected.UniqueKey, id, where);
        CompareInt32Member(context, item, "recStatus", expected.RecStatus, id, where);

        if (!item.TryGetProperty("values", out JsonElement values) || values.ValueKind != JsonValueKind.Object)
        {
            context.Mismatches.Add($"{form} {id} [{where}]: the item has no values object.");
            return;
        }

        string[] keys = values.EnumerateObject().Select(property => property.Name).ToArray();
        if (!keys.SequenceEqual(gridColumns, StringComparer.Ordinal))
        {
            context.Mismatches.Add(
                $"{form} {id} [{where}]: values keys are [{string.Join(", ", keys)}], expected the grid columns " +
                $"[{string.Join(", ", gridColumns)}] in that order.");
            return;
        }

        foreach (JsonProperty property in values.EnumerateObject())
        {
            DataField field = context.Form.ByName[property.Name];
            CompareValue(context, id, field, StoredValue(context, id, field), property.Value, where);
        }

        context.Summary.ListItemsCompared++;
    }

    // A Record DTO: identity, status, intrinsic coordinates and every data field's value. Returns whether the record
    // is one whose historical OnsetWeek disagrees with the reference week and came back as stored.
    private static bool CompareRecord(ComparisonContext context, BaseRecord expected, JsonElement record, string where)
    {
        string form = context.Form.Name;
        string id = expected.GlobalRecordId;
        if (record.ValueKind != JsonValueKind.Object)
        {
            context.Mismatches.Add($"{form} {id} [{where}]: the body is not a JSON object.");
            return false;
        }

        if (!record.TryGetProperty("globalRecordId", out JsonElement returnedId) ||
            returnedId.ValueKind != JsonValueKind.String ||
            !string.Equals(TryGetString(returnedId), id, StringComparison.Ordinal))
        {
            // Another record's values would be compared against this one's, so nothing more is checked.
            context.Mismatches.Add($"{form} {id} [{where}]: the response is not this record.");
            return false;
        }

        CompareInt32Member(context, record, "uniqueKey", expected.UniqueKey, id, where);
        CompareInt32Member(context, record, "recStatus", expected.RecStatus, id, where);

        // Position and count are intrinsic to the record's own RecStatus, whatever set the request selected (C-17).
        (int position, int count) = context.Sets.Intrinsic(expected);
        CompareInt32Member(context, record, "position", position, id, where);
        CompareInt32Member(context, record, "count", count, id, where);

        if (!record.TryGetProperty("values", out JsonElement values) || values.ValueKind != JsonValueKind.Object)
        {
            context.Mismatches.Add($"{form} {id} [{where}]: the record has no values object.");
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in values.EnumerateObject())
        {
            string name = property.Name;
            if (!seen.Add(name))
            {
                context.Mismatches.Add($"{form} {id} {name} [{where}]: values holds the field more than once.");
            }
            else if (context.Form.ExcludedNames.Contains(name))
            {
                context.Mismatches.Add(
                    $"{form} {id} {name} [{where}]: values holds an unsupported, label or group field.");
            }
            else if (context.Form.ByName.TryGetValue(name, out DataField? field))
            {
                CompareValue(context, id, field, StoredValue(context, id, field), property.Value, where);
            }
            else
            {
                context.Mismatches.Add($"{form} {id} {name} [{where}]: values holds a name that is not a data field.");
            }
        }

        foreach (DataField field in context.Form.DataFields.Where(field => !seen.Contains(field.Name)))
        {
            context.Mismatches.Add($"{form} {id} {field.Name} [{where}]: values has no entry for the data field.");
        }

        return ConfirmOnsetWeekAsStored(context, id, values, where);
    }

    private static bool ConfirmOnsetWeekAsStored(ComparisonContext context, string id, JsonElement values, string where)
    {
        if (!context.OnsetWeek.TryGetValue(id, out OnsetWeekExpectation? expectation))
        {
            return false;
        }

        if (values.TryGetProperty(OnsetWeekField, out JsonElement week) &&
            week.ValueKind == JsonValueKind.Number &&
            week.TryGetDouble(out double returned) &&
            returned == expectation.Stored &&
            returned != expectation.Reference)
        {
            return true;
        }

        context.Mismatches.Add(
            $"{context.Form.Name} {id} {OnsetWeekField} [{where}]: the historical week, which differs from the " +
            "reference EPIWEEK, was not returned as stored.");
        return false;
    }

    // The stored value of a data field: null when the record has no row on the field's page, as the API's LEFT JOIN
    // reads it.
    private static object? StoredValue(ComparisonContext context, string id, DataField field) =>
        context.Snapshot.Row(field.PageTable, id) is { } row ? row[field.Name] : null;

    private static void CompareInt32Member(
        ComparisonContext context,
        JsonElement body,
        string member,
        int expected,
        string id,
        string where)
    {
        if (!body.TryGetProperty(member, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out int actual))
        {
            context.Mismatches.Add($"{context.Form.Name} {id} [{where}]: {member} is missing or not a 32-bit integer.");
        }
        else if (actual != expected)
        {
            context.Mismatches.Add(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{context.Form.Name} {id} [{where}]: {member} is {actual}, expected {expected}."));
        }
    }

    // The wire contract per field type: null for a database null; a string ordinal-equal to stored text; a number
    // exactly equal to the stored number; a YYYY-MM-DD string for a stored date; a boolean for a bit; the integer
    // index of an option. A stored CLR type the field type does not admit is itself a mismatch.
    private static void CompareValue(
        ComparisonContext context,
        string id,
        DataField field,
        object? stored,
        JsonElement actual,
        string where)
    {
        string? problem = stored is null
            ? actual.ValueKind == JsonValueKind.Null ? null : $"expected null, the API returned {actual.ValueKind}"
            : field.Type switch
            {
                FieldType.Text or FieldType.Multiline or FieldType.LegalValues => CompareText(stored, actual),
                FieldType.Number => CompareNumber(stored, actual),
                FieldType.Date => CompareDate(stored, actual),
                FieldType.Checkbox => CompareCheckbox(stored, actual),
                FieldType.Option => CompareOption(stored, actual),
                _ => $"field type {field.Type} has no value on the wire",
            };

        if (problem is not null)
        {
            context.Mismatches.Add($"{context.Form.Name} {id} {field.Name} ({field.Type}) [{where}]: {problem}.");
        }
    }

    private static string? CompareText(object stored, JsonElement actual)
    {
        if (stored is not string text)
        {
            return $"a text field stores a {DescribeStored(stored)}";
        }

        if (actual.ValueKind != JsonValueKind.String)
        {
            return $"expected a String, the API returned {actual.ValueKind}";
        }

        return string.Equals(TryGetString(actual), text, StringComparison.Ordinal)
            ? null
            : "the String is not ordinal-equal to the stored text";
    }

    private static string? CompareNumber(object stored, JsonElement actual)
    {
        if (actual.ValueKind != JsonValueKind.Number)
        {
            return $"expected a Number, the API returned {actual.ValueKind}";
        }

        switch (stored)
        {
            // A bigint beyond 2^53 has no exact double, so it is compared as the integer the API writes digit for
            // digit.
            case long whole:
                return actual.TryGetInt64(out long returnedWhole) && returnedWhole == whole
                    ? null
                    : "the Number is not the stored bigint";
            case double or byte or short or int:
                double expected = Convert.ToDouble(stored, CultureInfo.InvariantCulture);
                return actual.TryGetDouble(out double returned) && returned == expected
                    ? null
                    : "the Number is not exactly the stored number";
            default:
                return $"a number field stores a {DescribeStored(stored)}";
        }
    }

    private static string? CompareDate(object stored, JsonElement actual)
    {
        if (stored is not DateTime date)
        {
            return $"a date field stores a {DescribeStored(stored)}";
        }

        if (actual.ValueKind != JsonValueKind.String)
        {
            return $"expected a YYYY-MM-DD String, the API returned {actual.ValueKind}";
        }

        string? text = TryGetString(actual);
        if (text is null || !IsoDate().IsMatch(text))
        {
            return "the String is not of the form YYYY-MM-DD";
        }

        return string.Equals(text, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            ? null
            : "the String is not the stored date";
    }

    private static string? CompareCheckbox(object stored, JsonElement actual)
    {
        if (stored is not bool flag)
        {
            return $"a checkbox field stores a {DescribeStored(stored)}";
        }

        return actual.ValueKind switch
        {
            JsonValueKind.True when flag => null,
            JsonValueKind.False when !flag => null,
            JsonValueKind.True or JsonValueKind.False => "the boolean is not the stored bit",
            _ => $"expected a boolean, the API returned {actual.ValueKind}",
        };
    }

    private static string? CompareOption(object stored, JsonElement actual)
    {
        if (stored is not (byte or short or int or long))
        {
            return $"an option field stores a {DescribeStored(stored)}";
        }

        if (actual.ValueKind != JsonValueKind.Number)
        {
            return $"expected a Number index, the API returned {actual.ValueKind}";
        }

        long index = Convert.ToInt64(stored, CultureInfo.InvariantCulture);
        return actual.TryGetInt64(out long returned) && returned == index
            ? null
            : "the Number is not the stored option index";
    }


    // The reference EPIWEEK, ported line for line from testdata/tools/epi_functions.py (epiweek with fdow = 0): an
    // oracle independent of the API's interpreter. Null where the start of the year before or after the date is not
    // representable, which no datetime column value of a seeded record reaches.
    private static int? ReferenceEpiWeek(DateTime d)
    {
        const int FirstDayOfWeek = 0;
        if (d.Year <= DateTime.MinValue.Year + 1 || d.Year >= DateTime.MaxValue.Year)
        {
            return null;
        }

        // The start is never after the date, so TimeSpan.Days truncates exactly as Python's floor does.
        return ((d - MmwrStart(d, FirstDayOfWeek)).Days / 7) + 1;
    }

    // _mmwr_start_year: Python's (weekday() + 1) % 7 is .NET's DayOfWeek, Sunday = 0.
    private static DateTime MmwrStartYear(int year, int firstDayOfWeek)
    {
        var jan1 = new DateTime(year, 1, 1);
        int dow = (int)jan1.DayOfWeek;
        if (dow <= firstDayOfWeek + 3)
        {
            return jan1.AddDays(firstDayOfWeek - dow);
        }

        return jan1.AddDays(7 - firstDayOfWeek - dow);
    }

    // _mmwr_start: only the sign of each difference is tested, on which Python's floor and .NET's truncation agree.
    private static DateTime MmwrStart(DateTime d, int firstDayOfWeek)
    {
        DateTime s0 = MmwrStartYear(d.Year - 1, firstDayOfWeek);
        DateTime s = MmwrStartYear(d.Year, firstDayOfWeek);
        DateTime s1 = MmwrStartYear(d.Year + 1, firstDayOfWeek);
        if ((s - d).Days > 0)
        {
            return s0;
        }

        if ((s1 - d).Days > 0)
        {
            return s;
        }

        return s1;
    }

    // A GET that must answer 200 with a JSON body. Failures name the path and status only, never the body, which can
    // hold field values.
    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string path, CancellationToken ct)
    {
        using HttpResponseMessage response = await client.GetAsync(path, ct);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new XunitException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"GET {path} returned {(int)response.StatusCode} {response.StatusCode}, expected 200."));
        }

        if (!string.Equals(
                response.Content.Headers.ContentType?.MediaType,
                ApiTestClient.JsonMediaType,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new XunitException($"GET {path} answered 200 without the media type {ApiTestClient.JsonMediaType}.");
        }

        return await ApiTestClient.ReadJsonElementAsync(response, ct);
    }

    private static int RequireInt32(JsonElement body, string member, string context) =>
        body.ValueKind == JsonValueKind.Object &&
        body.TryGetProperty(member, out JsonElement value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out int number)
            ? number
            : throw new XunitException($"{context}: {member} is missing or not a 32-bit integer.");

    private static string RequireString(JsonElement body, string member, string context) =>
        body.ValueKind == JsonValueKind.Object &&
        body.TryGetProperty(member, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String &&
        TryGetString(value) is { } text
            ? text
            : throw new XunitException($"{context}: {member} is missing or not a String.");

    private static bool RequireBoolean(JsonElement body, string member, string context) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(member, out JsonElement value)
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw new XunitException($"{context}: {member} is not a boolean."),
            }
            : throw new XunitException($"{context}: {member} is missing.");

    private static JsonElement RequireArray(JsonElement body, string member, string context) =>
        body.ValueKind == JsonValueKind.Object &&
        body.TryGetProperty(member, out JsonElement value) &&
        value.ValueKind == JsonValueKind.Array
            ? value
            : throw new XunitException($"{context}: {member} is missing or not an Array.");

    // The text of a String element, or null for an escaped lone surrogate, which has no string form.
    private static string? TryGetString(JsonElement element)
    {
        try
        {
            return element.GetString();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    // A stored value by its CLR type alone, so a message never carries field data.
    private static string DescribeStored(object? value) => value is null ? "null" : value.GetType().Name;

    private static int ToInt32(SnapshotRow row, string column) => row[column] switch
    {
        int number => number,
        short number => number,
        byte number => number,
        var other => throw new XunitException(
            $"{row.Table} {row.GlobalRecordId} {column} holds a {DescribeStored(other)}, not an integer."),
    };

    private static void WriteSummary(
        ITestOutputHelper? output,
        IReadOnlyList<FormSummary> summaries,
        int commandsRecorded,
        int analyzeCalls,
        int runCalls)
    {
        if (output is null)
        {
            return;
        }

        foreach (FormSummary summary in summaries)
        {
            string onsetWeek = string.Equals(summary.Form, FoodHistory, StringComparison.Ordinal)
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $" {OnsetWeekField}: {summary.OnsetWeekPairsStored} stored with an {OnsetDateField}, " +
                    $"{summary.OnsetWeekDiffering} of them different from the reference EPIWEEK, returned as stored " +
                    $"{summary.OnsetWeekConfirmedByPosition} times by position and {summary.OnsetWeekConfirmedById} " +
                    $"times by id.")
                : string.Empty;
            output.WriteLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{summary.Form}: {summary.ActiveCount} active of {summary.AllCount} records; " +
                    $"{summary.DataFieldCount} data fields; {summary.ListPagesRead} list pages read, " +
                    $"{summary.ListItemsCompared} items compared; {summary.RecordsReadByPosition} records compared " +
                    $"by position and {summary.RecordsReadById} by id; {summary.StoredDatesChecked} stored dates " +
                    $"at 00:00:00.{onsetWeek}"));
        }

        output.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"API commands recorded: {commandsRecorded}; check-code Analyze calls: {analyzeCalls}; " +
                $"Run calls: {runCalls}."));
    }

    [GeneratedRegex(@"\A[0-9]{4}-[0-9]{2}-[0-9]{2}\z", RegexOptions.CultureInvariant)]
    private static partial Regex IsoDate();

    private sealed record BaseRecord(string GlobalRecordId, int UniqueKey, int RecStatus);

    private sealed record DataField(string Name, FieldType Type, string PageTable);

    private sealed record OnsetWeekExpectation(double Stored, int Reference);

    private sealed record ComparisonContext(
        FormModel Form,
        RecordSets Sets,
        SqlSnapshot Snapshot,
        IReadOnlyDictionary<string, OnsetWeekExpectation> OnsetWeek,
        MismatchLog Mismatches,
        FormSummary Summary);

    // A form definition as the comparison needs it: data fields keyed by canonical name, the names a Record's values
    // must never hold, and the page tables.
    private sealed class FormModel
    {
        public FormModel(
            string name,
            List<DataField> dataFields,
            HashSet<string> excludedNames,
            HashSet<string> unsupportedNames,
            List<string> pageTables)
        {
            Name = name;
            DataFields = dataFields.AsReadOnly();
            ByName = dataFields.ToDictionary(field => field.Name, StringComparer.Ordinal);
            ExcludedNames = excludedNames;
            UnsupportedNames = unsupportedNames;
            PageTables = pageTables.AsReadOnly();
        }

        public string Name { get; }

        public IReadOnlyList<DataField> DataFields { get; }

        public IReadOnlyDictionary<string, DataField> ByName { get; }

        // Unsupported fields, labels and groups.
        public IReadOnlySet<string> ExcludedNames { get; }

        public IReadOnlySet<string> UnsupportedNames { get; }

        public IReadOnlyList<string> PageTables { get; }

        public DataField Require(string name, FieldType type) =>
            ByName.TryGetValue(name, out DataField? field) && field.Type == type
                ? field
                : throw new XunitException($"{Name} has no {type} data field {name}.");
    }

    // The all set and the active set of a form's base rows, each in UniqueKey order, with 1-based positions.
    private sealed class RecordSets
    {
        private readonly Dictionary<string, int> _allPositions;
        private readonly Dictionary<string, int> _activePositions;

        private RecordSets(List<BaseRecord> all)
        {
            All = all.AsReadOnly();
            Active = all.Where(record => record.RecStatus == ActiveRecStatus).ToList().AsReadOnly();
            _allPositions = Positions(All);
            _activePositions = Positions(Active);
        }

        public IReadOnlyList<BaseRecord> All { get; }

        public IReadOnlyList<BaseRecord> Active { get; }

        public static RecordSets FromSnapshot(SqlSnapshot snapshot, string baseTable)
        {
            var records = snapshot.Rows(baseTable)
                .Select(row => new BaseRecord(
                    row.GlobalRecordId,
                    ToInt32(row, UniqueKeyColumn),
                    ToInt32(row, RecStatusColumn)))
                .OrderBy(record => record.UniqueKey)
                .ToList();

            // UniqueKey is the base table's identity key; a repeated key would leave the order undefined.
            for (int index = 1; index < records.Count; index++)
            {
                Assert.True(
                    records[index].UniqueKey != records[index - 1].UniqueKey,
                    $"{baseTable} holds UniqueKey {records[index].UniqueKey} more than once.");
            }

            return new RecordSets(records);
        }

        // The record's intrinsic coordinates: over the active set when it is active, over every row otherwise.
        public (int Position, int Count) Intrinsic(BaseRecord record) =>
            record.RecStatus == ActiveRecStatus
                ? (_activePositions[record.GlobalRecordId], Active.Count)
                : (_allPositions[record.GlobalRecordId], All.Count);

        private static Dictionary<string, int> Positions(IReadOnlyList<BaseRecord> set)
        {
            var positions = new Dictionary<string, int>(set.Count, StringComparer.Ordinal);
            for (int index = 0; index < set.Count; index++)
            {
                positions.Add(set[index].GlobalRecordId, index + 1);
            }

            return positions;
        }
    }

    private sealed class FormSummary(string form, int activeCount, int allCount)
    {
        public string Form { get; } = form;

        public int ActiveCount { get; } = activeCount;

        public int AllCount { get; } = allCount;

        public int DataFieldCount { get; set; }

        public int ListPagesRead { get; set; }

        public int ListItemsCompared { get; set; }

        public int RecordsReadByPosition { get; set; }

        public int RecordsReadById { get; set; }

        public int StoredDatesChecked { get; set; }

        public int OnsetWeekPairsStored { get; set; }

        public int OnsetWeekDiffering { get; set; }

        public int OnsetWeekConfirmedByPosition { get; set; }

        public int OnsetWeekConfirmedById { get; set; }
    }

    // Every mismatch is counted; the first few are kept for the one failure message.
    private sealed class MismatchLog
    {
        private readonly List<string> _listed = [];

        public int Count { get; private set; }

        public void Add(string mismatch)
        {
            Count++;
            if (_listed.Count < MismatchListingLimit)
            {
                _listed.Add(mismatch);
            }
        }

        public void AssertEmpty()
        {
            if (Count == 0)
            {
                return;
            }

            throw new XunitException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{Count} mismatch(es) between the API's reads and SQL Server; the first {_listed.Count}:") +
                Environment.NewLine +
                string.Join(Environment.NewLine, _listed));
        }
    }
}

