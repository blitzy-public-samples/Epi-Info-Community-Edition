using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using EpiInfo.Api.Tests.Infrastructure;
using EpiInfo.Tests.Shared;
using Xunit.Sdk;

namespace EpiInfo.Api.Tests.Persistence;

/// <summary>
/// The concurrency rule of the save path: two concurrent <c>PUT</c>s of one record serialise on the update lock of its
/// base row, so a field only one save submits keeps that save's value, fields both saves submit hold the values of the
/// later commit, together, and the page writes of the two saves never interleave. There is no row-version check: the
/// later writer wins, as in legacy Epi Info.
/// </summary>
/// <remarks>
/// <para>
/// Each test creates its own <c>FoodHistory</c> record, carrying a <see cref="TestDatabase.NewMarker"/> marker in
/// <c>LastName</c>, and leaves it in the database. Its saves submit only <c>LastName</c> (page one,
/// <c>FoodHistory2</c>, <c>nvarchar(4000)</c>) and <c>Lab</c> (page two, <c>FoodHistory3</c>, <c>nvarchar(4000)</c>),
/// two text fields with no pattern, no maximum length and no legal values, so every value a test builds passes
/// validation. No seeded row, metadata row or schema object is touched.
/// </para>
/// <para>
/// Requests go over HTTP to the API hosted by <see cref="ApiFactory.ForDatabaseAsync"/>, and every check reads SQL
/// Server directly through <see cref="SqlSnapshot"/>. Each test holds the exclusive <see cref="DatabaseWriteLock"/>
/// for its whole run, so a snapshot comparison holds exactly the changes its own requests made. The host clock is
/// <see cref="ApiFactoryBase.Clock"/>: the record is created at the test's start, a whole second, and each concurrent
/// pair runs one minute later than the step before it, so both saves of a pair write one known stamp.
/// </para>
/// <para>
/// Which save of an overlapping pair commits last depends on scheduling, so the tests never assume an order: they
/// identify the winner from the values SQL holds, and every assertion holds for either order.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(DatabaseCollections.Database)]
public sealed class ConcurrentSaveTests : IAsyncLifetime
{
    private const string FormName = "FoodHistory";
    private const string BaseTable = "FoodHistory";
    private const string PageOneTable = "FoodHistory2";
    private const string PageTwoTable = "FoodHistory3";

    // The two submitted fields, each the name of its column.
    private const string PageOneField = TestDatabase.MarkerField;
    private const string PageTwoField = "Lab";

    private const string UniqueKeyColumn = "UniqueKey";
    private const string RecStatusColumn = "RecStatus";
    private const string FirstSaveLogonNameColumn = "FirstSaveLogonName";
    private const string FirstSaveTimeColumn = "FirstSaveTime";
    private const string LastSaveLogonNameColumn = "LastSaveLogonName";
    private const string LastSaveTimeColumn = "LastSaveTime";
    private const string ForeignKeyColumn = "FKEY";

    // fieldType spellings of the form definition (camelCase, as the wire contract writes them).
    private const string LabelFieldType = "label";
    private const string GroupFieldType = "group";
    private const string UnsupportedFieldType = "unsupported";
    private const string DateFieldType = "date";

    private const string IsoDateFormat = "yyyy-MM-dd";

    // Rounds of the overlapping pair: more rounds give the two transactions more chances to contend for the lock.
    private const int OverlappingRounds = 5;

    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(5);

    // The test's start: a whole second, the time the record is created. Each concurrent pair runs whole minutes later.
    private static readonly DateTime TestStart = ControlledTimeProvider.DefaultLocalStart;

    // The base columns a save never writes: the identity, the status and the first-save pair.
    private static readonly string[] BaseColumnsNeverSaved =
        [UniqueKeyColumn, RecStatusColumn, FirstSaveLogonNameColumn, FirstSaveTimeColumn, ForeignKeyColumn];

    // The unsupported Yes/No tinyint columns of the page tables, which the API never writes.
    private static readonly IReadOnlyDictionary<string, string[]> UnsupportedColumns =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [PageOneTable] = ["ILL", "DoctorVisit", "Hospitalized", "Antibiotics", "Died"],
            [PageTwoTable] = ["StoolSubmitted", "O157H7isolated"],
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
    public async Task Disjoint_concurrent_saves_both_survive()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        CancellationToken ct = timeout.Token;
        await using DatabaseWriteLock dbLock = await DatabaseWriteLock.AcquireExclusiveAsync(ct);

        Factory.Clock.SetLocalNow(TestStart);
        int viewId = await ApiTestClient.GetViewIdAsync(Client, FormName, ct);
        string marker = TestDatabase.NewMarker();
        string globalRecordId = await CreateRecordAsync(viewId, marker, ct);
        SqlSnapshot before = await SqlSnapshot.CaptureAsync(ct);

        // Each save submits one field, on a different page: a delta, so neither may overwrite the other's field.
        StampAt(minutes: 1);
        string lastName = marker + "-A";
        string lab = "lab-B-" + Guid.NewGuid().ToString("N");
        const string Pair = "the disjoint pair";
        var saveA = new SaveRequest("PUT A", FieldValues((PageOneField, lastName)));
        var saveB = new SaveRequest("PUT B", FieldValues((PageTwoField, lab)));
        (JsonElement savedA, JsonElement savedB) =
            await SaveConcurrentlyAsync(viewId, globalRecordId, saveA, saveB, Pair, ct);

        // Each response was read inside its own transaction, after its own write.
        AssertResponseValue(savedA, PageOneField, lastName, globalRecordId, $"{saveA.Name} of {Pair}");
        AssertResponseValue(savedB, PageTwoField, lab, globalRecordId, $"{saveB.Name} of {Pair}");

        SqlSnapshot after = await SqlSnapshot.CaptureAsync(ct);
        AssertStoredText(after, PageOneTable, PageOneField, globalRecordId, lastName, Pair);
        AssertStoredText(after, PageTwoTable, PageTwoField, globalRecordId, lab, Pair);

        AssertOnlyAllowedChanges(
            SqlSnapshot.Compare(before, after),
            globalRecordId,
            new HashSet<(string Table, string Column)>
            {
                (PageOneTable, PageOneField),
                (PageTwoTable, PageTwoField),
                (BaseTable, LastSaveLogonNameColumn),
                (BaseTable, LastSaveTimeColumn),
            },
            Pair);
    }

    [Fact]
    public async Task Overlapping_concurrent_saves_keep_both_fields_from_the_same_save()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        CancellationToken ct = timeout.Token;
        await using DatabaseWriteLock dbLock = await DatabaseWriteLock.AcquireExclusiveAsync(ct);

        Factory.Clock.SetLocalNow(TestStart);
        int viewId = await ApiTestClient.GetViewIdAsync(Client, FormName, ct);
        IReadOnlyDictionary<string, string> dataFields = await ReadDataFieldTypesAsync(viewId, ct);
        string marker = TestDatabase.NewMarker();
        string globalRecordId = await CreateRecordAsync(viewId, marker, ct);

        for (int round = 1; round <= OverlappingRounds; round++)
        {
            StampAt(minutes: round);
            string pair = string.Create(CultureInfo.InvariantCulture, $"the overlapping pair of round {round}");
            FieldPair c = NewPair(marker, "C", round);
            FieldPair d = NewPair(marker, "D", round);

            (JsonElement savedC, JsonElement savedD) =
                await SaveConcurrentlyAsync(viewId, globalRecordId, c.ToRequest(), d.ToRequest(), pair, ct);

            SqlSnapshot after = await SqlSnapshot.CaptureAsync(ct);
            object? storedLastName = StoredCell(after, PageOneTable, PageOneField, globalRecordId);
            object? storedLab = StoredCell(after, PageTwoTable, PageTwoField, globalRecordId);

            // Both fields of one save, never one from each: the two saves' page writes did not interleave. The save
            // whose values SQL holds is the winner, the later commit.
            (FieldPair winner, JsonElement winnerValues, FieldPair loser, JsonElement loserValues) =
                c.IsStored(storedLastName, storedLab) ? (c, savedC, d, savedD)
                : d.IsStored(storedLastName, storedLab) ? (d, savedD, c, savedC)
                : throw new XunitException(
                    $"After {pair}, {PageOneTable}.{PageOneField} and {PageTwoTable}.{PageTwoField} of GlobalRecordId " +
                    $"{globalRecordId} must hold both values of one save, ({Describe(c.LastName)}, {Describe(c.Lab)}) " +
                    $"from {c.Name} or ({Describe(d.LastName)}, {Describe(d.Lab)}) from {d.Name}; actual " +
                    $"({Describe(storedLastName)}, {Describe(storedLab)}).");

            // The winner's response snapshot is the final state of the record.
            AssertResponseMatchesStored(winnerValues, after, globalRecordId, dataFields, $"{winner.Name} of {pair}");

            // The loser's response snapshot was read inside its own transaction, which the base-row lock serialised
            // before the winner's, so it carries the loser's own pair.
            string loserRequest = $"{loser.Name} of {pair}";
            AssertResponseValue(loserValues, PageOneField, loser.LastName, globalRecordId, loserRequest);
            AssertResponseValue(loserValues, PageTwoField, loser.Lab, globalRecordId, loserRequest);
        }
    }

    [Fact]
    public async Task Concurrent_saves_write_only_their_audit_columns()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        CancellationToken ct = timeout.Token;
        await using DatabaseWriteLock dbLock = await DatabaseWriteLock.AcquireExclusiveAsync(ct);

        Factory.Clock.SetLocalNow(TestStart);
        int viewId = await ApiTestClient.GetViewIdAsync(Client, FormName, ct);
        string marker = TestDatabase.NewMarker();
        string globalRecordId = await CreateRecordAsync(viewId, marker, ct);
        SqlSnapshot before = await SqlSnapshot.CaptureAsync(ct);

        const string DisjointPair = "the disjoint pair";
        DateTime disjointTime = StampAt(minutes: 1);
        await SaveConcurrentlyAsync(
            viewId,
            globalRecordId,
            new SaveRequest("PUT A", FieldValues((PageOneField, marker + "-A"))),
            new SaveRequest("PUT B", FieldValues((PageTwoField, "lab-B-" + Guid.NewGuid().ToString("N")))),
            DisjointPair,
            ct);
        SqlSnapshot afterDisjoint = await SqlSnapshot.CaptureAsync(ct);
        AssertSaveStamp(afterDisjoint, globalRecordId, disjointTime, DisjointPair);
        AssertBaseColumnsUnchanged(before, afterDisjoint, globalRecordId, DisjointPair);
        AssertUnsupportedColumnsUntouched(before, afterDisjoint, globalRecordId, DisjointPair);

        const string OverlappingPair = "the overlapping pair";
        DateTime overlappingTime = StampAt(minutes: 2);
        await SaveConcurrentlyAsync(
            viewId,
            globalRecordId,
            NewPair(marker, "C", round: 1).ToRequest(),
            NewPair(marker, "D", round: 1).ToRequest(),
            OverlappingPair,
            ct);
        SqlSnapshot afterOverlapping = await SqlSnapshot.CaptureAsync(ct);
        AssertSaveStamp(afterOverlapping, globalRecordId, overlappingTime, OverlappingPair);
        AssertBaseColumnsUnchanged(before, afterOverlapping, globalRecordId, OverlappingPair);
        AssertUnsupportedColumnsUntouched(before, afterOverlapping, globalRecordId, OverlappingPair);
    }

    // Creates the test's record at the current clock time, with the marker in LastName and an initial Lab, so both
    // fields the saves submit start with a value of their own. Returns the id as the base row stores it.
    private async Task<string> CreateRecordAsync(int viewId, string marker, CancellationToken ct)
    {
        CreatedRecord created = await ApiTestClient.CreateRecordAsync(
            Client,
            viewId,
            FieldValues((PageOneField, marker), (PageTwoField, "lab-initial-" + Guid.NewGuid().ToString("N"))),
            ct);
        return created.GlobalRecordId;
    }

    // Sends both saves of a pair with both requests in flight before either is awaited, so their write transactions
    // can overlap and contend for the base row's update lock. Both must answer 200; returns each response's values.
    private async Task<(JsonElement First, JsonElement Second)> SaveConcurrentlyAsync(
        int viewId,
        string globalRecordId,
        SaveRequest first,
        SaveRequest second,
        string pair,
        CancellationToken ct)
    {
        string recordPath = string.Create(
            CultureInfo.InvariantCulture,
            $"/api/forms/{viewId}/records/{globalRecordId}");
        Task<HttpResponseMessage> firstPut =
            ApiTestClient.PutJsonAsync(Client, recordPath, new { values = first.Values }, ct);
        Task<HttpResponseMessage> secondPut =
            ApiTestClient.PutJsonAsync(Client, recordPath, new { values = second.Values }, ct);
        try
        {
            await Task.WhenAll(firstPut, secondPut);
            JsonElement firstValues =
                await ReadSavedValuesAsync(await firstPut, globalRecordId, $"{first.Name} of {pair}", ct);
            JsonElement secondValues =
                await ReadSavedValuesAsync(await secondPut, globalRecordId, $"{second.Name} of {pair}", ct);
            return (firstValues, secondValues);
        }
        finally
        {
            // Task.WhenAll completes only once both requests have, so every response that arrived is disposed here,
            // also when the other request failed.
            await DisposeResponseAsync(firstPut);
            await DisposeResponseAsync(secondPut);
        }
    }

    private static async Task DisposeResponseAsync(Task<HttpResponseMessage> request)
    {
        if (request.IsCompletedSuccessfully)
        {
            (await request).Dispose();
        }
    }

    // The values object of a 200 Record response for the record.
    private static async Task<JsonElement> ReadSavedValuesAsync(
        HttpResponseMessage response,
        string globalRecordId,
        string request,
        CancellationToken ct)
    {
        await AssertStatusAsync(response, HttpStatusCode.OK, request, ct);
        JsonElement record = await ApiTestClient.ReadJsonElementAsync(response, ct);
        if (record.ValueKind != JsonValueKind.Object)
        {
            throw new XunitException($"{request} returned a JSON {record.ValueKind}, not a Record object.");
        }

        Assert.True(
            record.TryGetProperty("globalRecordId", out JsonElement id) &&
            id.ValueKind == JsonValueKind.String &&
            string.Equals(id.GetString(), globalRecordId, StringComparison.Ordinal),
            $"{request} must return the Record of GlobalRecordId {globalRecordId}; its globalRecordId is " +
            $"{DescribeEntry(record, "globalRecordId")}.");

        if (!record.TryGetProperty("values", out JsonElement values) || values.ValueKind != JsonValueKind.Object)
        {
            throw new XunitException(
                $"{request} returned a Record of GlobalRecordId {globalRecordId} without a values object.");
        }

        return values;
    }

    // The data fields of the form by canonical name, with their fieldType: every field of every page of
    // GET /api/forms/{viewId} that is neither a label, a group nor unsupported. These are exactly the keys of a Record's
    // values.
    private async Task<IReadOnlyDictionary<string, string>> ReadDataFieldTypesAsync(int viewId, CancellationToken ct)
    {
        string path = string.Create(CultureInfo.InvariantCulture, $"/api/forms/{viewId}");
        string request = "GET " + path;
        using HttpResponseMessage response = await Client.GetAsync(path, ct);
        await AssertStatusAsync(response, HttpStatusCode.OK, request, ct);
        JsonElement definition = await ApiTestClient.ReadJsonElementAsync(response, ct);
        if (definition.ValueKind != JsonValueKind.Object ||
            !definition.TryGetProperty("pages", out JsonElement pages) ||
            pages.ValueKind != JsonValueKind.Array)
        {
            throw new XunitException($"{request} returned no form definition with a pages array.");
        }

        var dataFields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonElement page in pages.EnumerateArray())
        {
            if (page.ValueKind != JsonValueKind.Object ||
                !page.TryGetProperty("fields", out JsonElement fields) ||
                fields.ValueKind != JsonValueKind.Array)
            {
                throw new XunitException($"{request} returned a page without a fields array.");
            }

            foreach (JsonElement field in fields.EnumerateArray())
            {
                if (field.ValueKind != JsonValueKind.Object ||
                    !field.TryGetProperty("name", out JsonElement name) ||
                    name.ValueKind != JsonValueKind.String ||
                    name.GetString() is not { Length: > 0 } fieldName ||
                    !field.TryGetProperty("fieldType", out JsonElement type) ||
                    type.ValueKind != JsonValueKind.String ||
                    type.GetString() is not { Length: > 0 } fieldType)
                {
                    throw new XunitException($"{request} returned a field without a string name and fieldType.");
                }

                if (fieldType is LabelFieldType or GroupFieldType or UnsupportedFieldType)
                {
                    continue;
                }

                if (!dataFields.TryAdd(fieldName, fieldType))
                {
                    throw new XunitException($"{request} lists data field {fieldName} more than once.");
                }
            }
        }

        Assert.True(
            dataFields.ContainsKey(PageOneField) && dataFields.ContainsKey(PageTwoField),
            $"{request} must list {PageOneField} and {PageTwoField} as data fields; it lists " +
            $"[{string.Join(", ", dataFields.Keys)}].");
        return dataFields;
    }

    // The response's values hold exactly the form's data fields, and each equals, through the wire contract, the
    // column of the same name on the record's row of FoodHistory2 or FoodHistory3 in the snapshot.
    private static void AssertResponseMatchesStored(
        JsonElement values,
        SqlSnapshot snapshot,
        string globalRecordId,
        IReadOnlyDictionary<string, string> dataFields,
        string request)
    {
        SnapshotRow pageOne = StoredRow(snapshot, PageOneTable, globalRecordId);
        SnapshotRow pageTwo = StoredRow(snapshot, PageTwoTable, globalRecordId);

        var mismatches = new List<string>();
        var returned = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty entry in values.EnumerateObject())
        {
            if (!returned.Add(entry.Name))
            {
                mismatches.Add($"values holds {entry.Name} more than once");
                continue;
            }

            if (!dataFields.TryGetValue(entry.Name, out string? fieldType))
            {
                mismatches.Add($"values holds {entry.Name}, which is not a data field of {FormName}");
                continue;
            }

            bool onPageOne = pageOne.Values.ContainsKey(entry.Name);
            bool onPageTwo = pageTwo.Values.ContainsKey(entry.Name);
            if (onPageOne == onPageTwo)
            {
                mismatches.Add(onPageOne
                    ? $"{entry.Name} is a column of both {PageOneTable} and {PageTwoTable}"
                    : $"{entry.Name} is a column of neither {PageOneTable} nor {PageTwoTable}");
                continue;
            }

            SnapshotRow row = onPageOne ? pageOne : pageTwo;
            object? stored = row[entry.Name];
            if (!WireMatchesStored(fieldType, entry.Value, stored))
            {
                mismatches.Add(
                    $"{row.Table}.{entry.Name} ({fieldType}): response {entry.Value.GetRawText()}, " +
                    $"stored {Describe(stored)}");
            }
        }

        mismatches.AddRange(
            dataFields.Keys
                .Where(field => !returned.Contains(field))
                .Select(field => $"values lacks data field {field}"));

        Assert.True(
            mismatches.Count == 0,
            string.Create(
                CultureInfo.InvariantCulture,
                $"The values of {request} must be the stored page values of GlobalRecordId {globalRecordId}, one " +
                $"entry per data field of {FormName}; {mismatches.Count} differ: {string.Join("; ", mismatches)}."));
    }

    // The wire contract of a values entry: a string is the same text (ordinal), a date field's string is YYYY-MM-DD
    // and the datetime holds that date at 00:00:00, a number is the stored double or integer, a boolean the stored
    // bit, and null a SQL NULL.
    private static bool WireMatchesStored(string fieldType, JsonElement wire, object? stored) => wire.ValueKind switch
    {
        JsonValueKind.Null => stored is null,
        JsonValueKind.String when string.Equals(fieldType, DateFieldType, StringComparison.Ordinal) =>
            DateTime.TryParseExact(
                wire.GetString(),
                IsoDateFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTime date) &&
            stored is DateTime storedDate &&
            storedDate == date,
        JsonValueKind.String => stored is string text && string.Equals(text, wire.GetString(), StringComparison.Ordinal),
        JsonValueKind.Number => stored switch
        {
            double number => wire.TryGetDouble(out double sent) && SqlSnapshot.ValuesEqual(number, sent),
            byte or short or int or long =>
                wire.TryGetInt64(out long index) && index == Convert.ToInt64(stored, CultureInfo.InvariantCulture),
            _ => false,
        },
        JsonValueKind.True or JsonValueKind.False => stored is bool flag && flag == wire.GetBoolean(),
        _ => false,
    };

    // The response's values carry expected as the text of field.
    private static void AssertResponseValue(
        JsonElement values,
        string field,
        string expected,
        string globalRecordId,
        string request) =>
        Assert.True(
            values.TryGetProperty(field, out JsonElement value) &&
            value.ValueKind == JsonValueKind.String &&
            string.Equals(value.GetString(), expected, StringComparison.Ordinal),
            $"{request} must return {field} {Describe(expected)} for GlobalRecordId {globalRecordId}; it returned " +
            $"{DescribeEntry(values, field)}.");

    // The record's row of a table holds expected in column.
    private static void AssertStoredText(
        SqlSnapshot snapshot,
        string table,
        string column,
        string globalRecordId,
        string expected,
        string pair)
    {
        object? actual = StoredCell(snapshot, table, column, globalRecordId);
        Assert.True(
            SqlSnapshot.ValuesEqual(expected, actual),
            $"After {pair}, {table}.{column} of GlobalRecordId {globalRecordId}: expected {Describe(expected)}, " +
            $"actual {Describe(actual)}.");
    }

    // The pair changed only allowed columns of the record's own rows: no row added or removed, no column of another
    // record or outside the allowed set changed, and no table's column set changed.
    private static void AssertOnlyAllowedChanges(
        SnapshotDiff diff,
        string globalRecordId,
        IReadOnlySet<(string Table, string Column)> allowed,
        string pair)
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
                violations.Add(
                    $"{cell.Table}.{cell.Column}, which neither save submitted, changed from " +
                    $"{Describe(cell.OldValue)} to {Describe(cell.NewValue)}");
            }
        }

        Assert.True(
            violations.Count == 0,
            $"{pair} may change only [{string.Join(", ", allowed.Select(cell => $"{cell.Table}.{cell.Column}"))}] of " +
            $"GlobalRecordId {globalRecordId}, but {string.Join("; ", violations)}. The diff holds: {diff}");
    }


    // The base row carries the save stamp of the pair: the logon name of this process, which hosts the API, and a
    // LastSaveTime not earlier than the test's start that is the pair's own stamp time.
    private static void AssertSaveStamp(SqlSnapshot snapshot, string globalRecordId, DateTime pairTime, string pair)
    {
        SnapshotRow row = StoredRow(snapshot, BaseTable, globalRecordId);

        string logon = Environment.UserName;
        object? lastSaveLogonName = row[LastSaveLogonNameColumn];
        Assert.True(
            SqlSnapshot.ValuesEqual(logon, lastSaveLogonName),
            $"After {pair}, {BaseTable}.{LastSaveLogonNameColumn} of GlobalRecordId {globalRecordId} must be the " +
            $"stamp identity: expected {Describe(logon)}, actual {Describe(lastSaveLogonName)}.");

        object? lastSaveTime = row[LastSaveTimeColumn];
        Assert.True(
            lastSaveTime is DateTime time && time >= TestStart,
            $"After {pair}, {BaseTable}.{LastSaveTimeColumn} of GlobalRecordId {globalRecordId} must not be earlier " +
            $"than the test's start {Describe(TestStart)}; actual {Describe(lastSaveTime)}.");

        // Both saves of a pair read the one controlled instant, so whichever commits last writes this exact stamp.
        Assert.True(
            SqlSnapshot.ValuesEqual(pairTime, lastSaveTime),
            $"After {pair}, {BaseTable}.{LastSaveTimeColumn} of GlobalRecordId {globalRecordId} must be the pair's " +
            $"stamp time: expected {Describe(pairTime)}, actual {Describe(lastSaveTime)}.");
    }

    // The base columns a save never writes hold what they held before the first pair.
    private static void AssertBaseColumnsUnchanged(
        SqlSnapshot before,
        SqlSnapshot after,
        string globalRecordId,
        string pair)
    {
        SnapshotRow earlier = StoredRow(before, BaseTable, globalRecordId);
        SnapshotRow later = StoredRow(after, BaseTable, globalRecordId);
        var changed = BaseColumnsNeverSaved
            .Where(column => !SqlSnapshot.ValuesEqual(earlier[column], later[column]))
            .Select(column => $"{column}: before {Describe(earlier[column])}, after {Describe(later[column])}")
            .ToList();

        Assert.True(
            changed.Count == 0,
            $"{pair} must leave {string.Join(", ", BaseColumnsNeverSaved)} of {BaseTable} row {globalRecordId} " +
            $"unchanged, but {string.Join("; ", changed)}.");
    }

    // The unsupported Yes/No columns stay NULL on the record and appear nowhere in its snapshot diff.
    private static void AssertUnsupportedColumnsUntouched(
        SqlSnapshot before,
        SqlSnapshot after,
        string globalRecordId,
        string pair)
    {
        SnapshotDiff diff = SqlSnapshot.Compare(before, after).ChangesFor(globalRecordId);
        var violations = new List<string>();
        foreach ((string table, string[] columns) in UnsupportedColumns)
        {
            SnapshotRow earlier = StoredRow(before, table, globalRecordId);
            SnapshotRow later = StoredRow(after, table, globalRecordId);
            foreach (string column in columns)
            {
                if (earlier[column] is not null || later[column] is not null)
                {
                    violations.Add(
                        $"{table}.{column} must be NULL before and after: before {Describe(earlier[column])}, " +
                        $"after {Describe(later[column])}");
                }

                if (diff.ChangedCells.Any(cell =>
                        string.Equals(cell.Table, table, StringComparison.Ordinal) &&
                        string.Equals(cell.Column, column, StringComparison.Ordinal)))
                {
                    violations.Add($"{table}.{column} is in the snapshot diff");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            $"{pair} must not write an unsupported Yes/No column of GlobalRecordId {globalRecordId}, but " +
            $"{string.Join("; ", violations)}. The record's diff holds: {diff}");
    }

    // On an unexpected status the message names the problem's title and the errors keys, which are canonical field or
    // parameter names, never a submitted value.
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

    private static SnapshotRow StoredRow(SqlSnapshot snapshot, string table, string globalRecordId) =>
        snapshot.Row(table, globalRecordId)
            ?? throw new XunitException($"{table} holds no row for GlobalRecordId {globalRecordId}.");

    private static object? StoredCell(SqlSnapshot snapshot, string table, string column, string globalRecordId) =>
        StoredRow(snapshot, table, globalRecordId)[column];

    // Sets the host clock the save stamps are read from to a whole number of minutes after the test's start; datetime
    // holds whole seconds exactly, so the stored stamp is the time returned.
    private DateTime StampAt(int minutes)
    {
        DateTime time = TestStart.AddMinutes(minutes);
        Factory.Clock.SetLocalNow(time);
        return time;
    }

    // One save of an overlapping pair: both fields, each value unique to the save and the round.
    private static FieldPair NewPair(string marker, string save, int round) =>
        new(
            "PUT " + save,
            string.Create(CultureInfo.InvariantCulture, $"{marker}-{save}{round}"),
            string.Create(CultureInfo.InvariantCulture, $"lab-{save}{round}-{Guid.NewGuid():N}"));

    // A values object; a field named twice is a test error, so Add throws on it.
    private static Dictionary<string, object?> FieldValues(params (string Field, object? Value)[] entries)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach ((string field, object? value) in entries)
        {
            values.Add(field, value);
        }

        return values;
    }

    private static string Describe(object? value) =>
        value is null ? "NULL" : $"{SqlSnapshot.FormatValue(value)} ({value.GetType().Name})";

    // A JSON member as the response wrote it; the values are test data.
    private static string DescribeEntry(JsonElement body, string member) =>
        body.TryGetProperty(member, out JsonElement value) ? value.GetRawText() : "no such member";

    /// <summary>One save of a concurrent pair.</summary>
    /// <param name="Name">How failure messages name the save, for example <c>PUT A</c>.</param>
    /// <param name="Values">The <c>values</c> object the save submits.</param>
    private sealed record SaveRequest(string Name, Dictionary<string, object?> Values);

    /// <summary>The two values one save of an overlapping pair submits.</summary>
    /// <param name="Name">How failure messages name the save, for example <c>PUT C</c>.</param>
    /// <param name="LastName">The page-one value.</param>
    /// <param name="Lab">The page-two value.</param>
    private sealed record FieldPair(string Name, string LastName, string Lab)
    {
        public SaveRequest ToRequest() => new(Name, FieldValues((PageOneField, LastName), (PageTwoField, Lab)));

        // Whether SQL holds both of this save's values.
        public bool IsStored(object? storedLastName, object? storedLab) =>
            storedLastName is string lastName &&
            storedLab is string lab &&
            string.Equals(lastName, LastName, StringComparison.Ordinal) &&
            string.Equals(lab, Lab, StringComparison.Ordinal);
    }
}

