using System.Data;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EpiInfo.Api.Tests.Infrastructure;
using EpiInfo.Tests.Shared;
using Microsoft.Data.SqlClient;
using Xunit.Sdk;

namespace EpiInfo.Api.Tests.Persistence;

/// <summary>
/// SC-3, stored-data equivalence: a record created, updated and soft-deleted through the API leaves exactly the base
/// and page rows legacy Epi Info writes and reads, and nothing else in the five data tables changes.
/// </summary>
/// <remarks>
/// <para>
/// The expected rows come from the observed legacy write path (<c>CollectedDataProvider.SaveRecord</c> and
/// <c>UpdateBaseTable</c>), the target departures the plan records for it, and the shape of the seeded rows, never
/// from the new code: a lower-case <c>D</c>-format <c>GlobalRecordId</c> keying one base row and one row on every
/// page table, <c>RecStatus</c> 1, <c>FKEY</c> left <c>NULL</c> because neither form has a foreign-key field, the four
/// save stamps written with one stamp on insert, every value the type its column stores, an empty text written as
/// <c>NULL</c>, and every column the request did not set left <c>NULL</c>. An update writes only the submitted
/// columns and the base last-save pair; a soft delete writes only the base <c>RecStatus</c> and last-save pair; a
/// delete of a deleted record writes nothing.
/// </para>
/// <para>
/// Every request goes over HTTP to the API hosted by <see cref="ApiFactory.ForDatabaseAsync"/>, and every check reads
/// SQL Server directly through <see cref="SqlSnapshot"/>. The exclusive <see cref="DatabaseWriteLock"/> is held from
/// the first snapshot to the last, so each comparison holds exactly the changes the request made and any other
/// addition, removal or change fails. The stamp values are known because the host's clock is
/// <see cref="ApiFactoryBase.Clock"/>: each operation runs at a whole-second time one minute after the previous one,
/// in the clock's UTC zone, so the stamp SQL Server holds is the literal time set. The expected logon name is
/// <see cref="Environment.UserName"/> of this process, which hosts the API.
/// </para>
/// <para>
/// Each test creates its own records, carrying a <see cref="TestDatabase.NewMarker"/> marker in
/// <see cref="TestDatabase.MarkerField"/>, and leaves them in the database; no seeded row, metadata row or schema
/// object is touched, and no count or view id is hard-coded.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(DatabaseCollections.Database)]
public sealed partial class StoredDataContractTests : IAsyncLifetime
{
    private const string CaseReport = "CaseReport";
    private const string CaseReportPage = "CaseReport1";
    private const string FoodHistory = "FoodHistory";
    private const string FoodHistoryPageOne = "FoodHistory2";
    private const string FoodHistoryPageTwo = "FoodHistory3";

    private const string GlobalRecordIdColumn = "GlobalRecordId";
    private const string UniqueKeyColumn = "UniqueKey";
    private const string RecStatusColumn = "RecStatus";
    private const string FirstSaveLogonNameColumn = "FirstSaveLogonName";
    private const string FirstSaveTimeColumn = "FirstSaveTime";
    private const string LastSaveLogonNameColumn = "LastSaveLogonName";
    private const string LastSaveTimeColumn = "LastSaveTime";
    private const string ForeignKeyColumn = "FKEY";

    private const string IsoDateFormat = "yyyy-MM-dd";

    private const short ActiveStatus = 1;
    private const short DeletedStatus = 0;

    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(5);

    // Whole seconds a minute apart, so a datetime column holds each exactly and a changed LastSaveTime is a change.
    private static readonly DateTime CreateTime = ControlledTimeProvider.DefaultLocalStart;
    private static readonly DateTime UpdateTime = CreateTime.AddMinutes(1);
    private static readonly DateTime DeleteTime = CreateTime.AddMinutes(2);
    private static readonly DateTime RedeleteTime = CreateTime.AddMinutes(3);

    // Columns no request of these tests may write: read-only fields, which the API never accepts from a client, and
    // the unsupported Yes/No tinyint columns, which it never writes. Each must exist and stay NULL on created rows.
    private static readonly IReadOnlyDictionary<string, string[]> NeverWrittenColumns =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [CaseReportPage] = ["StateIDNumber", "CDCIDNumber", "ReportedToCDCDate"],
            [FoodHistoryPageOne] = ["OnsetWeek", "ILL", "DoctorVisit", "Hospitalized", "Antibiotics", "Died"],
            [FoodHistoryPageTwo] = ["StoolSubmitted", "O157H7isolated"],
        };

    private ApiFactory? _factory;
    private HttpClient? _client;

    private ApiFactory Factory =>
        _factory ?? throw new InvalidOperationException("The API host is created in InitializeAsync.");

    private HttpClient Client =>
        _client ?? throw new InvalidOperationException("The API client is created in InitializeAsync.");

    /// <summary>
    /// Proves the rehearsal database reachable, then starts the API host on it. A database that is not configured or
    /// not reachable fails every test with the <see cref="TestDatabase"/> message; nothing is caught or skipped.
    /// </summary>
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

    [Fact]
    public async Task CaseReport_create_update_delete_match_the_stored_data_contract()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        CancellationToken ct = timeout.Token;
        await using DatabaseWriteLock dbLock = await DatabaseWriteLock.AcquireExclusiveAsync(ct);

        // ErythemaMigrans is a Comment Legal field over the codeyesno table; the value is read, never assumed.
        string yesNo = await ReadCodeValueAsync("codeyesno", "YesNo", ct);

        var form = new FormContract(
            CaseReport,
            CaseReport,
            [CaseReportPage],
            Create:
            [
                Text(CaseReportPage, TestDatabase.MarkerField, TestDatabase.NewMarker()),

                // Outside ASCII, so nvarchar must hold every UTF-16 code unit exactly.
                Text(CaseReportPage, "FirstName", "Zoë 測試"),
                Text(CaseReportPage, "PhoneNumber", "555-014-8636"),
                Text(CaseReportPage, "Address", "1600 Clifton Road NE"),
                Text(CaseReportPage, "City", "Atlanta"),
                Text(CaseReportPage, "Zip", "30333"),
                Number(CaseReportPage, "Age", 42),
                Date(CaseReportPage, "OnsetDate", "2011-06-15"),
                Text(CaseReportPage, "ErythemaMigrans", yesNo),
            ],
            Update:
            [
                Text(CaseReportPage, "FirstName", "Åsa Ñúñez"),

                // An explicit null and an empty text both leave the column NULL, as legacy writes an empty string.
                Text(CaseReportPage, "City", null),
                Text(CaseReportPage, "Address", string.Empty),
                Number(CaseReportPage, "Age", 43),
                Date(CaseReportPage, "OnsetDate", "2011-07-01"),
            ]);

        await RunLifecycleAsync(form, ct);
    }

    [Fact]
    public async Task FoodHistory_create_update_delete_match_the_stored_data_contract()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        CancellationToken ct = timeout.Token;
        await using DatabaseWriteLock dbLock = await DatabaseWriteLock.AcquireExclusiveAsync(ct);

        // Sex is a Legal Values field, so the value must be a row of its code table.
        string sex = await ReadCodeValueAsync("codephvs_administrativesex_hl7_2x", "phvs_administrativesex_hl7_2x", ct);

        var form = new FormContract(
            FoodHistory,
            FoodHistory,
            [FoodHistoryPageOne, FoodHistoryPageTwo],
            Create:
            [
                Text(FoodHistoryPageOne, TestDatabase.MarkerField, TestDatabase.NewMarker()),
                Text(FoodHistoryPageOne, "FirstName", "Renée"),
                Number(FoodHistoryPageOne, "CaseID", 12345),
                Date(FoodHistoryPageOne, "DateofInterview", "2011-03-15"),
                Text(FoodHistoryPageOne, "Sex", sex),
                Date(FoodHistoryPageOne, "DOB", "1980-02-29"),
                Number(FoodHistoryPageOne, "FeverTemp", 101.5),
                Checkbox(FoodHistoryPageOne, "White", true),

                // An explicit false is a value: the bit column holds 0, not NULL.
                Checkbox(FoodHistoryPageOne, "Asian", false),
                Text(FoodHistoryPageOne, "PatientAddress", "12 Main Street\nApartment 4\nSpringfield"),
                Text(FoodHistoryPageOne, "HomePhone", "(555) 010-2030"),
                Text(FoodHistoryPageTwo, "BuyLastTenDays", "Beef jerky\nAmerican cheese\nStrawberries"),
                Checkbox(FoodHistoryPageTwo, "Beefjerkey", true),
                Date(FoodHistoryPageTwo, "StoolCollectionDate", "2011-04-01"),
                Text(FoodHistoryPageTwo, "Lab", "State Public Health Laboratory"),
                Text(FoodHistoryPageTwo, "LabResults", "E. coli O157:H7 isolated"),
            ],
            Update:
            [
                Date(FoodHistoryPageOne, "DOB", "1981-01-02"),
                Checkbox(FoodHistoryPageOne, "White", false),
                Text(FoodHistoryPageTwo, "Lab", "County Laboratory"),
                Checkbox(FoodHistoryPageTwo, "Breastmilk", true),
            ]);

        await RunLifecycleAsync(form, ct);
    }

    [Fact]
    public async Task FoodHistory_create_with_page_one_values_only_still_adds_a_page_two_row()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        CancellationToken ct = timeout.Token;
        await using DatabaseWriteLock dbLock = await DatabaseWriteLock.AcquireExclusiveAsync(ct);

        // Legacy inserts one row per page whatever the page holds, so the page-two row exists with every value NULL.
        var form = new FormContract(
            FoodHistory,
            FoodHistory,
            [FoodHistoryPageOne, FoodHistoryPageTwo],
            Create:
            [
                Text(FoodHistoryPageOne, TestDatabase.MarkerField, TestDatabase.NewMarker()),
                Text(FoodHistoryPageOne, "FirstName", "Page One"),
                Number(FoodHistoryPageOne, "CaseID", 67890),
                Date(FoodHistoryPageOne, "DateofInterview", "2011-05-20"),
                Checkbox(FoodHistoryPageOne, "White", true),
            ],
            Update: []);

        int viewId = await ApiTestClient.GetViewIdAsync(Client, form.Name, ct);
        SqlSnapshot before = await SqlSnapshot.CaptureAsync(ct);
        (string globalRecordId, _, SqlSnapshot after) = await CreateAndVerifyAsync(form, viewId, before, ct);

        Assert.All(form.Create, write => Assert.Equal(FoodHistoryPageOne, write.Table));
        AssertRowHolds(after, FoodHistoryPageTwo, globalRecordId, NeverWritten(FoodHistoryPageTwo));
    }

    // Create, update, soft delete and delete again. Each step is compared with the snapshot taken after the step
    // before it, so the chain also proves that nothing changed between two steps.
    private async Task RunLifecycleAsync(FormContract form, CancellationToken ct)
    {
        int viewId = await ApiTestClient.GetViewIdAsync(Client, form.Name, ct);
        string logon = Environment.UserName;

        SqlSnapshot beforeCreate = await SqlSnapshot.CaptureAsync(ct);
        (string globalRecordId, int uniqueKey, SqlSnapshot afterCreate) =
            await CreateAndVerifyAsync(form, viewId, beforeCreate, ct);
        string recordPath = RecordsPath(viewId) + "/" + globalRecordId;

        // Update: a delta, so only the submitted columns and the base last-save pair may change.
        Stamp(UpdateTime);
        string put = "PUT " + recordPath;
        using (HttpResponseMessage response =
            await ApiTestClient.PutJsonAsync(Client, recordPath, new { values = Values(form.Update) }, ct))
        {
            await AssertStatusAsync(response, HttpStatusCode.OK, put, ct);
            JsonElement body = await ApiTestClient.ReadJsonElementAsync(response, ct);
            (string returnedId, int returnedKey) = ReadRecordBody(body, put, ActiveStatus);
            Assert.Equal(globalRecordId, returnedId);
            Assert.Equal(uniqueKey, returnedKey);
        }

        SqlSnapshot afterUpdate = await SqlSnapshot.CaptureAsync(ct);
        SnapshotDiff updateDiff = SqlSnapshot.Compare(afterCreate, afterUpdate);
        var updateAllowed = new HashSet<(string Table, string Column)>(
            form.Update.Select(write => (write.Table, write.Field)))
        {
            (form.BaseTable, LastSaveLogonNameColumn),
            (form.BaseTable, LastSaveTimeColumn),
        };
        AssertOnlyAllowedChanges(updateDiff, globalRecordId, updateAllowed, put);
        AssertCellChanged(updateDiff, form.BaseTable, globalRecordId, LastSaveTimeColumn, CreateTime, UpdateTime, put);
        AssertBaseRow(
            afterUpdate, form.BaseTable, globalRecordId, uniqueKey, ActiveStatus, logon, CreateTime, UpdateTime);
        Dictionary<string, Dictionary<string, object?>> updatedPages = ExpectedPageRows(form, form.Create, form.Update);
        AssertPageRows(afterUpdate, form, globalRecordId, updatedPages);

        // Soft delete: the base status and last-save pair only; page rows stay as they are and nothing cascades.
        Stamp(DeleteTime);
        string delete = "DELETE " + recordPath;
        using (HttpResponseMessage response = await Client.DeleteAsync(recordPath, ct))
        {
            await AssertStatusAsync(response, HttpStatusCode.NoContent, delete, ct);
        }

        SqlSnapshot afterDelete = await SqlSnapshot.CaptureAsync(ct);
        SnapshotDiff deleteDiff = SqlSnapshot.Compare(afterUpdate, afterDelete);
        var deleteAllowed = new HashSet<(string Table, string Column)>
        {
            (form.BaseTable, RecStatusColumn),
            (form.BaseTable, LastSaveLogonNameColumn),
            (form.BaseTable, LastSaveTimeColumn),
        };
        AssertOnlyAllowedChanges(deleteDiff, globalRecordId, deleteAllowed, delete);
        AssertCellChanged(
            deleteDiff, form.BaseTable, globalRecordId, RecStatusColumn, ActiveStatus, DeletedStatus, delete);
        AssertCellChanged(
            deleteDiff, form.BaseTable, globalRecordId, LastSaveTimeColumn, UpdateTime, DeleteTime, delete);
        AssertBaseRow(
            afterDelete, form.BaseTable, globalRecordId, uniqueKey, DeletedStatus, logon, CreateTime, DeleteTime);
        AssertPageRows(afterDelete, form, globalRecordId, updatedPages);

        // A delete of a deleted record answers 204 and writes nothing, its stamps included.
        Stamp(RedeleteTime);
        using (HttpResponseMessage response = await Client.DeleteAsync(recordPath, ct))
        {
            await AssertStatusAsync(response, HttpStatusCode.NoContent, "repeated " + delete, ct);
        }

        SqlSnapshot afterRedelete = await SqlSnapshot.CaptureAsync(ct);
        SnapshotDiff redeleteDiff = SqlSnapshot.Compare(afterDelete, afterRedelete);
        Assert.True(
            redeleteDiff.IsEmpty,
            $"A repeated {delete} of the deleted record {globalRecordId} must write nothing; the diff holds: " +
            redeleteDiff);
        AssertBaseRow(
            afterRedelete, form.BaseTable, globalRecordId, uniqueKey, DeletedStatus, logon, CreateTime, DeleteTime);
    }

    // Creates the record at CreateTime and checks the rows it added against the contract; returns the record's id,
    // its UniqueKey and the snapshot taken after the create.
    private async Task<(string GlobalRecordId, int UniqueKey, SqlSnapshot After)> CreateAndVerifyAsync(
        FormContract form,
        int viewId,
        SqlSnapshot before,
        CancellationToken ct)
    {
        string recordsPath = RecordsPath(viewId);
        string post = "POST " + recordsPath;

        Stamp(CreateTime);
        string globalRecordId;
        int uniqueKey;
        using (HttpResponseMessage response =
            await ApiTestClient.PostJsonAsync(Client, recordsPath, new { values = Values(form.Create) }, ct))
        {
            await AssertStatusAsync(response, HttpStatusCode.Created, post, ct);
            JsonElement body = await ApiTestClient.ReadJsonElementAsync(response, ct);
            (globalRecordId, uniqueKey) = ReadRecordBody(body, post, ActiveStatus);
            Assert.Equal(recordsPath + "/" + globalRecordId, response.Headers.Location?.OriginalString);
        }

        SqlSnapshot after = await SqlSnapshot.CaptureAsync(ct);
        AssertExactlyAdded(
            SqlSnapshot.Compare(before, after), globalRecordId, [form.BaseTable, .. form.PageTables], post);

        // The id has the shape of the seeded ids and of legacy's Guid.NewGuid().ToString(): lower-case D format.
        Assert.True(
            Guid.TryParseExact(globalRecordId, "D", out _) &&
            string.Equals(globalRecordId, globalRecordId.ToLowerInvariant(), StringComparison.Ordinal),
            $"{post} returned GlobalRecordId '{globalRecordId}', which is not a lower-case GUID in D format.");
        int occurrences = await CountBaseRowsAsync(form.BaseTable, globalRecordId, ct);
        Assert.True(
            occurrences == 1,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{form.BaseTable} holds GlobalRecordId {globalRecordId} {occurrences} times; it must be unique."));

        string logon = Environment.UserName;
        AssertBaseRow(after, form.BaseTable, globalRecordId, uniqueKey, ActiveStatus, logon, CreateTime, CreateTime);
        AssertPageRows(after, form, globalRecordId, ExpectedPageRows(form, form.Create));
        return (globalRecordId, uniqueKey, after);
    }

    // The base row holds the record's key, its status, FKEY NULL and the stamp pairs; any further column is NULL.
    private static void AssertBaseRow(
        SqlSnapshot snapshot,
        string baseTable,
        string globalRecordId,
        int uniqueKey,
        short recStatus,
        string logonName,
        DateTime firstSaveTime,
        DateTime lastSaveTime) =>
        AssertRowHolds(
            snapshot,
            baseTable,
            globalRecordId,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [UniqueKeyColumn] = uniqueKey,
                [RecStatusColumn] = recStatus,
                [FirstSaveLogonNameColumn] = logonName,
                [FirstSaveTimeColumn] = firstSaveTime,
                [LastSaveLogonNameColumn] = logonName,
                [LastSaveTimeColumn] = lastSaveTime,
                [ForeignKeyColumn] = null,
            });

    private static void AssertPageRows(
        SqlSnapshot snapshot,
        FormContract form,
        string globalRecordId,
        IReadOnlyDictionary<string, Dictionary<string, object?>> expectedPages)
    {
        foreach (string pageTable in form.PageTables)
        {
            AssertRowHolds(snapshot, pageTable, globalRecordId, expectedPages[pageTable]);
        }
    }


    // Every column of the row holds exactly its expected typed value; a column absent from expected must be NULL, and
    // GlobalRecordId is the record's id. Every mismatch is listed at once.
    private static void AssertRowHolds(
        SqlSnapshot snapshot,
        string table,
        string globalRecordId,
        IReadOnlyDictionary<string, object?> expected)
    {
        SnapshotRow row = snapshot.Row(table, globalRecordId)
            ?? throw new XunitException($"{table} holds no row for GlobalRecordId {globalRecordId}.");

        var mismatches = new List<string>();
        foreach (string column in expected.Keys.Where(column => !row.Values.ContainsKey(column)))
        {
            mismatches.Add($"column {column} does not exist");
        }

        foreach ((string column, object? actual) in row.Values)
        {
            object? wanted = string.Equals(column, GlobalRecordIdColumn, StringComparison.Ordinal)
                ? globalRecordId
                : expected.GetValueOrDefault(column);
            if (!SqlSnapshot.ValuesEqual(wanted, actual))
            {
                mismatches.Add($"{column}: expected {Describe(wanted)}, actual {Describe(actual)}");
            }
        }

        Assert.True(
            mismatches.Count == 0,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{table} row {globalRecordId} breaks the stored-data contract in {mismatches.Count} column(s): " +
                $"{string.Join("; ", mismatches)}."));
    }

    // The operation added one row keyed by the record's id to each named table, and did nothing else.
    private static void AssertExactlyAdded(
        SnapshotDiff diff,
        string globalRecordId,
        IReadOnlyCollection<string> tables,
        string operation)
    {
        var expected = tables.Select(table => (table, globalRecordId)).ToHashSet();
        var added = diff.AddedRows.Select(row => (row.Table, row.GlobalRecordId)).ToList();
        bool exact = diff.SchemaChanges.Count == 0 &&
            diff.RemovedRows.Count == 0 &&
            diff.ChangedCells.Count == 0 &&
            added.Count == expected.Count &&
            added.All(expected.Contains);

        Assert.True(
            exact,
            $"{operation} must add exactly one row for GlobalRecordId {globalRecordId} to each of " +
            $"[{string.Join(", ", tables)}] and change nothing else; the diff holds: {diff}");
    }

    // The operation changed only allowed columns of the record's own rows: no row added or removed, no column of
    // another record or outside the allowed set changed, and no table's column set changed.
    private static void AssertOnlyAllowedChanges(
        SnapshotDiff diff,
        string globalRecordId,
        IReadOnlySet<(string Table, string Column)> allowed,
        string operation)
    {
        var violations = new List<string>();
        violations.AddRange(diff.SchemaChanges.Select(change => $"the columns of {change.Table} changed"));
        violations.AddRange(diff.AddedRows.Select(row => $"a {row.Table} row {row.GlobalRecordId} was added"));
        violations.AddRange(diff.RemovedRows.Select(row => $"a {row.Table} row {row.GlobalRecordId} was removed"));
        foreach (SnapshotCellChange cell in diff.ChangedCells)
        {
            if (!string.Equals(cell.GlobalRecordId, globalRecordId, StringComparison.Ordinal))
            {
                violations.Add($"{cell.Table}.{cell.Column} of another record, {cell.GlobalRecordId}, changed");
            }
            else if (!allowed.Contains((cell.Table, cell.Column)))
            {
                violations.Add($"{cell.Table}.{cell.Column}, which the operation may not write, changed");
            }
        }

        string allowedList = string.Join(", ", allowed.Select(cell => $"{cell.Table}.{cell.Column}"));
        Assert.True(
            violations.Count == 0,
            $"{operation} may change only [{allowedList}] of record {globalRecordId}, but " +
            $"{string.Join("; ", violations)}. The diff holds: {diff}");
    }

    // The diff holds exactly this change of the record's column, from oldValue to newValue.
    private static void AssertCellChanged(
        SnapshotDiff diff,
        string table,
        string globalRecordId,
        string column,
        object oldValue,
        object newValue,
        string operation)
    {
        bool changed = diff.ChangedCells.Any(cell =>
            string.Equals(cell.Table, table, StringComparison.Ordinal) &&
            string.Equals(cell.GlobalRecordId, globalRecordId, StringComparison.Ordinal) &&
            string.Equals(cell.Column, column, StringComparison.Ordinal) &&
            SqlSnapshot.ValuesEqual(cell.OldValue, oldValue) &&
            SqlSnapshot.ValuesEqual(cell.NewValue, newValue));

        Assert.True(
            changed,
            $"{operation} must change {table}.{column} of record {globalRecordId} from {Describe(oldValue)} to " +
            $"{Describe(newValue)}; the diff holds: {diff}");
    }

    // On an unexpected status the message names the problem's title and the errors keys, which are canonical field
    // or parameter names, never a submitted value.
    private static async Task AssertStatusAsync(
        HttpResponseMessage response,
        HttpStatusCode expected,
        string request,
        CancellationToken ct)
    {
        if (response.StatusCode == expected)
        {
            return;
        }

        string body = await response.Content.ReadAsStringAsync(ct);
        Assert.Fail(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{request} returned {(int)response.StatusCode} {response.StatusCode}, expected {(int)expected} " +
                $"{expected}. {DescribeProblem(body)}"));
    }

    private static string DescribeProblem(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "The body is empty.";
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return $"The body is a JSON {root.ValueKind}.";
            }

            var text = new StringBuilder();
            if (root.TryGetProperty("title", out JsonElement title) && title.ValueKind == JsonValueKind.String)
            {
                text.Append("Title: ").Append(title.GetString()).Append('.');
            }

            if (root.TryGetProperty("errors", out JsonElement errors) && errors.ValueKind == JsonValueKind.Object)
            {
                text.Append(" Errors for: [")
                    .AppendJoin(", ", errors.EnumerateObject().Select(error => error.Name))
                    .Append("].");
            }

            return text.Length == 0 ? "The body is a JSON object without title or errors." : text.ToString().Trim();
        }
        catch (JsonException)
        {
            return "The body is not JSON.";
        }
    }

    // The Record DTO's id and UniqueKey, after checking its status.
    private static (string GlobalRecordId, int UniqueKey) ReadRecordBody(
        JsonElement body,
        string request,
        short recStatus)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            Assert.Fail($"{request} returned a JSON {body.ValueKind}, not a Record object.");
        }

        if (!body.TryGetProperty("globalRecordId", out JsonElement id) ||
            id.ValueKind != JsonValueKind.String ||
            id.GetString() is not { Length: > 0 } globalRecordId)
        {
            throw new XunitException($"{request} returned no non-empty string globalRecordId.");
        }

        if (!body.TryGetProperty("uniqueKey", out JsonElement key) ||
            key.ValueKind != JsonValueKind.Number ||
            !key.TryGetInt32(out int uniqueKey))
        {
            throw new XunitException($"{request} returned no 32-bit integer uniqueKey.");
        }

        Assert.True(
            body.TryGetProperty("recStatus", out JsonElement status) &&
            status.ValueKind == JsonValueKind.Number &&
            status.TryGetInt32(out int returnedStatus) &&
            returnedStatus == recStatus,
            string.Create(CultureInfo.InvariantCulture, $"{request} must return recStatus {recStatus}."));

        return (globalRecordId, uniqueKey);
    }


    // The expected cells of every page row: the never-written columns NULL, then each layer of writes in order, a
    // later write of a column replacing an earlier one. Any column left out is expected NULL by AssertRowHolds.
    private static Dictionary<string, Dictionary<string, object?>> ExpectedPageRows(
        FormContract form,
        params IReadOnlyList<FieldWrite>[] layers)
    {
        var pages = form.PageTables.ToDictionary(table => table, NeverWritten, StringComparer.Ordinal);
        foreach (FieldWrite write in layers.SelectMany(layer => layer))
        {
            if (!pages.TryGetValue(write.Table, out Dictionary<string, object?>? cells))
            {
                throw new InvalidOperationException(
                    $"Field {write.Field} names {write.Table}, which is not a page table of {form.Name}.");
            }

            if (NeverWrittenColumns.TryGetValue(write.Table, out string[]? never) && never.Contains(write.Field))
            {
                throw new InvalidOperationException(
                    $"Field {write.Field} of {write.Table} is read-only or unsupported, so no test may submit it.");
            }

            cells[write.Field] = write.Stored;
        }

        return pages;
    }

    // The read-only and unsupported columns of a page table, each expected NULL.
    private static Dictionary<string, object?> NeverWritten(string pageTable) =>
        NeverWrittenColumns.GetValueOrDefault(pageTable, [])
            .ToDictionary(column => column, _ => (object?)null, StringComparer.Ordinal);

    // The request's values object; a field named twice is a test error, so Add throws on it.
    private static Dictionary<string, object?> Values(IEnumerable<FieldWrite> writes)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (FieldWrite write in writes)
        {
            values.Add(write.Field, write.Wire);
        }

        return values;
    }

    // Sets the host clock the save stamps are read from. datetime stores whole seconds exactly, so a whole-second time
    // makes the stored stamp equal to the time set.
    private void Stamp(DateTime localTime)
    {
        if (localTime.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            throw new ArgumentException("A save stamp time must be a whole second.", nameof(localTime));
        }

        Factory.Clock.SetLocalNow(localTime);
    }

    // One value of a seeded code table, so no test hard-codes a legal value.
    private static async Task<string> ReadCodeValueAsync(string table, string column, CancellationToken ct)
    {
        string quotedColumn = Quote(column);
        await using SqlConnection connection = await TestDatabase.OpenConnectionAsync(ct);
        await using var command = new SqlCommand(
            $"SELECT TOP (1) {quotedColumn} FROM [dbo].{Quote(table)} " +
            $"WHERE {quotedColumn} IS NOT NULL AND {quotedColumn} <> @empty ORDER BY {quotedColumn};",
            connection);
        // The seeded code-table text columns are nvarchar(255) (testdata/sql/04_code_tables.sql).
        command.Parameters.Add(new SqlParameter("@empty", SqlDbType.NVarChar, 255) { Value = string.Empty });
        return await command.ExecuteScalarAsync(ct) as string
            ?? throw new InvalidOperationException($"Code table {table} holds no {column} value.");
    }

    private static async Task<int> CountBaseRowsAsync(string baseTable, string globalRecordId, CancellationToken ct)
    {
        await using SqlConnection connection = await TestDatabase.OpenConnectionAsync(ct);
        await using var command = new SqlCommand(
            $"SELECT COUNT(*) FROM [dbo].{Quote(baseTable)} WHERE [{GlobalRecordIdColumn}] = @globalRecordId;",
            connection);
        command.Parameters.Add(new SqlParameter("@globalRecordId", SqlDbType.NVarChar, 255) { Value = globalRecordId });
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    // Brackets a table or column name of this file; only plain identifiers are accepted, so none needs escaping.
    private static string Quote(string identifier) =>
        PlainIdentifier().IsMatch(identifier)
            ? "[" + identifier + "]"
            : throw new ArgumentException($"'{identifier}' is not a plain SQL identifier.", nameof(identifier));

    private static string RecordsPath(int viewId) =>
        string.Create(CultureInfo.InvariantCulture, $"/api/forms/{viewId}/records");

    private static string Describe(object? value) =>
        value is null ? "NULL" : $"{SqlSnapshot.FormatValue(value)} ({value.GetType().Name})";

    // A text-family value; an empty text is stored as NULL, as legacy writes an empty string.
    private static FieldWrite Text(string table, string field, string? text) =>
        new(table, field, text, string.IsNullOrEmpty(text) ? null : text);

    // A number is sent as a JSON number and stored in a float column as the same double.
    private static FieldWrite Number(string table, string field, double number) =>
        new(table, field, number, number);

    // A date is sent as YYYY-MM-DD and stored in a datetime column at 00:00:00.
    private static FieldWrite Date(string table, string field, string isoDate) =>
        new(
            table,
            field,
            isoDate,
            DateTime.ParseExact(isoDate, IsoDateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None));

    // A checkbox is sent as a JSON boolean and stored in a bit column.
    private static FieldWrite Checkbox(string table, string field, bool value) =>
        new(table, field, value, value);

    [GeneratedRegex(@"\A[A-Za-z_][A-Za-z0-9_]*\z")]
    private static partial Regex PlainIdentifier();

    /// <summary>One field a request submits.</summary>
    /// <param name="Table">The page table holding the field's column.</param>
    /// <param name="Field">The field name, which is also its column name.</param>
    /// <param name="Wire">The value as the request's <c>values</c> object carries it.</param>
    /// <param name="Stored">The typed value the column must then hold, as <see cref="SqlSnapshot"/> reads it.</param>
    private sealed record FieldWrite(string Table, string Field, object? Wire, object? Stored);

    /// <summary>A seeded form, its tables and the values one test submits to it.</summary>
    /// <param name="Name">The form name, resolved to its view id through <c>GET /api/forms</c>.</param>
    /// <param name="BaseTable">The base table.</param>
    /// <param name="PageTables">Every page table, in page order.</param>
    /// <param name="Create">The values of the create.</param>
    /// <param name="Update">The delta of the update.</param>
    private sealed record FormContract(
        string Name,
        string BaseTable,
        IReadOnlyList<string> PageTables,
        IReadOnlyList<FieldWrite> Create,
        IReadOnlyList<FieldWrite> Update);
}
