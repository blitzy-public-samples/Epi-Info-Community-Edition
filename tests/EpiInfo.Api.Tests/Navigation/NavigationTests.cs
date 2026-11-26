using System.Data;
using System.Globalization;
using System.Net;
using System.Text.Json;
using EpiInfo.Api.Tests.Infrastructure;
using EpiInfo.Domain.Records;
using EpiInfo.Tests.Shared;
using Microsoft.Data.SqlClient;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace EpiInfo.Api.Tests.Navigation;

/// <summary>
/// SC-5 over HTTP against the seeded rehearsal database: record counts, the record at a 1-based position, the
/// navigator's selected-set position and the Record DTO's intrinsic coordinates (AAP 0.3.4.1, 0.3.4.3, 0.3.4.4; C-5,
/// C-16, C-17).
/// </summary>
/// <remarks>
/// <para>
/// The rules proved: one active-set predicate, <c>RecStatus = 1</c>, with every row selected by
/// <c>includeDeleted=true</c>; <c>UniqueKey</c> order; and position = <c>COUNT(*)</c> of the selected set with
/// <c>UniqueKey &lt;= k</c>. That is the legacy record-number formula of
/// <c>Epi.Enter.CheckCodeEngine/RunTimeView.cs</c> (line 687), which legacy applies to every row; the target filters
/// deleted rows out by default, and <c>includeDeleted=true</c> reproduces the legacy set. Every case checks two things
/// separately: the id found at the selected-set index, and the Record's intrinsic <c>position</c> and <c>count</c>,
/// which follow the record's own <c>recStatus</c> rather than the caller's toggle.
/// </para>
/// <para>
/// Expected values come from direct SQL on the base tables, never from literals, because other tests leave the
/// records they create in the database. Read cases hold the shared <see cref="DatabaseWriteLock"/> for their whole
/// run so those values cannot move. The one writing case holds it exclusively; its only writes are one record created
/// through the API with a marker, and the removal of that record's own <c>FoodHistory3</c> row, the missing-page
/// fixture AAP 0.3.7 allows. No seeded row, metadata row or schema object is touched.
/// </para>
/// <para>
/// Table names in the SQL below come from a closed map of bracket-quoted literals, and every value is a parameter.
/// An unconfigured or unreachable database fails every case through <see cref="ApiFactory.ForDatabaseAsync"/>; no
/// case skips.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(DatabaseCollections.Database)]
public sealed class NavigationTests : IAsyncLifetime
{
    private const string CaseReport = "CaseReport";

    private const string FoodHistory = "FoodHistory";

    private const string CaseReportBaseTable = "[dbo].[CaseReport]";

    private const string FoodHistoryBaseTable = "[dbo].[FoodHistory]";

    // The FoodHistory page tables, named after the base table plus the page id (seeded metaPages 2 and 3). The
    // missing-page case checks the page-two name against the form definition before it deletes anything.
    private const string FoodHistoryPageOneTable = "[dbo].[FoodHistory2]";

    private const string FoodHistoryPageTwoTable = "[dbo].[FoodHistory3]";

    private const string FoodHistoryPageTwoName = "FoodHistory3";

    // The selected-set predicate of C-16, with @all choosing every row, so one statement serves both sets.
    private const string SetPredicate = "(@all = 1 OR [RecStatus] = 1)";

    // Longer than DatabaseWriteLock's 300-second wait, so a hung request fails the test instead of the whole run.
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(10);

    private readonly ITestOutputHelper _output;

    private ApiFactory? _factory;

    private HttpClient? _client;

    /// <summary>Creates the test class with the xUnit output used to report a parity gap.</summary>
    /// <param name="output">The test's output helper.</param>
    public NavigationTests(ITestOutputHelper output)
    {
        ArgumentNullException.ThrowIfNull(output);
        _output = output;
    }

    private ApiFactory Factory => _factory ?? throw new InvalidOperationException("The API host was not started.");

    private HttpClient Client => _client ?? throw new InvalidOperationException("The API client was not created.");

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        // Proves the database reachable first and throws otherwise, so a missing database fails every case.
        _factory = await ApiFactory.ForDatabaseAsync();
        _client = _factory.CreateClient();
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(CaseReport)]
    [InlineData(FoodHistory)]
    public async Task Count_MatchesSqlForActiveAndAllRows(string form)
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        var ct = timeout.Token;
        await using var dbLock = await DatabaseWriteLock.AcquireSharedAsync(ct);
        Factory.Recorder.Clear();

        var viewId = await ApiTestClient.GetViewIdAsync(Client, form, ct);
        var active = await CountAsync(form, includeDeleted: false, ct);
        var all = await CountAsync(form, includeDeleted: true, ct);
        Assert.True(active <= all, $"{form}: the active set cannot outnumber every row.");

        Assert.Equal(active, await GetCountAsync(viewId, includeDeleted: null, ct));
        Assert.Equal(all, await GetCountAsync(viewId, "true", ct));
        Assert.Equal(all, await GetCountAsync(viewId, "TRUE", ct));
        Assert.Equal(active, await GetCountAsync(viewId, "false", ct));

        // The count route documents the same coercion 400 as every other route that takes the toggle (C-19).
        await NavigationApi.AssertParameterProblemAsync(
            Client,
            NavigationApi.RecordsPath(viewId, "/count", "maybe"),
            NavigationApi.IncludeDeletedParameter,
            ct);

        AssertNoWritesIssued();
    }

    [Theory]
    [InlineData(CaseReport)]
    [InlineData(FoodHistory)]
    public async Task At_StartMiddleEnd_ActiveSet(string form)
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        var ct = timeout.Token;
        await using var dbLock = await DatabaseWriteLock.AcquireSharedAsync(ct);
        Factory.Recorder.Clear();

        var viewId = await ApiTestClient.GetViewIdAsync(Client, form, ct);
        var n = await CountAsync(form, includeDeleted: false, ct);
        Assert.True(n >= 1, $"{form} has no active record; the seeded precondition (AAP 0.7.3) is not met.");

        foreach (var index in StartMiddleEnd(n))
        {
            var expectedId = await IdAtAsync(form, includeDeleted: false, index, ct);
            var record = await NavigationApi.GetOkAsync(Client, NavigationApi.RecordsPath(viewId, At(index)), ct);

            // The id at the selected-set index.
            Assert.Equal(expectedId, NavigationApi.GetString(record, "globalRecordId"));

            // The intrinsic coordinates: an active record is numbered within the active set, which is the set asked.
            Assert.Equal(RecordQueryRules.ActiveRecStatus, NavigationApi.GetInt(record, "recStatus"));
            Assert.Equal(index, NavigationApi.GetInt(record, "position"));
            Assert.Equal(n, NavigationApi.GetInt(record, "count"));
            await AssertIntrinsicAsync(form, record, ct);
        }

        AssertNoWritesIssued();
    }

    [Theory]
    [InlineData(CaseReport)]
    [InlineData(FoodHistory)]
    public async Task At_StartMiddleEnd_AllRows(string form)
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        var ct = timeout.Token;
        await using var dbLock = await DatabaseWriteLock.AcquireSharedAsync(ct);
        Factory.Recorder.Clear();

        var viewId = await ApiTestClient.GetViewIdAsync(Client, form, ct);
        var all = await CountAsync(form, includeDeleted: true, ct);
        Assert.True(all >= 1, $"{form} has no record; the seeded precondition (AAP 0.7.3) is not met.");

        foreach (var index in StartMiddleEnd(all))
        {
            var expectedId = await IdAtAsync(form, includeDeleted: true, index, ct);
            var record = await NavigationApi.GetOkAsync(
                Client,
                NavigationApi.RecordsPath(viewId, At(index), "true"),
                ct);

            Assert.Equal(expectedId, NavigationApi.GetString(record, "globalRecordId"));

            // An active record keeps its active-set position here, not the index asked (C-17); a deleted one is
            // numbered over every row, so its position is the index.
            await AssertIntrinsicAsync(form, record, ct);
        }

        AssertNoWritesIssued();
    }

    [Theory]
    [InlineData(CaseReport)]
    [InlineData(FoodHistory)]
    public async Task At_OutOfRangeAndNonInteger(string form)
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        var ct = timeout.Token;
        await using var dbLock = await DatabaseWriteLock.AcquireSharedAsync(ct);
        Factory.Recorder.Clear();

        var viewId = await ApiTestClient.GetViewIdAsync(Client, form, ct);
        var active = await CountAsync(form, includeDeleted: false, ct);
        var all = await CountAsync(form, includeDeleted: true, ct);

        await AssertNotFoundAsync(NavigationApi.RecordsPath(viewId, At(0)), ct);
        await AssertNotFoundAsync(NavigationApi.RecordsPath(viewId, At(0), "true"), ct);
        await AssertNotFoundAsync(NavigationApi.RecordsPath(viewId, At(-1)), ct);

        // One past the active set is outside it even where a deleted row makes that index exist over every row.
        await AssertNotFoundAsync(NavigationApi.RecordsPath(viewId, At(active + 1)), ct);
        if (active + 1 <= all)
        {
            var record = await NavigationApi.GetOkAsync(
                Client,
                NavigationApi.RecordsPath(viewId, At(active + 1), "true"),
                ct);
            Assert.Equal(
                await IdAtAsync(form, includeDeleted: true, active + 1, ct),
                NavigationApi.GetString(record, "globalRecordId"));
        }

        await AssertNotFoundAsync(NavigationApi.RecordsPath(viewId, At(all + 1), "true"), ct);

        // Coercion failures are field-level 400s keyed by the parameter, never a route miss.
        await NavigationApi.AssertParameterProblemAsync(
            Client,
            NavigationApi.RecordsPath(viewId, "/at/abc"),
            NavigationApi.PositionParameter,
            ct);
        await NavigationApi.AssertParameterProblemAsync(
            Client,
            NavigationApi.RecordsPath(viewId, At(1), "maybe"),
            NavigationApi.IncludeDeletedParameter,
            ct);

        AssertNoWritesIssued();
    }

    [Theory]
    [InlineData(CaseReport)]
    [InlineData(FoodHistory)]
    public async Task DeletedRecord_AppearsOnlyInAllRowsSet(string form)
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        var ct = timeout.Token;
        await using var dbLock = await DatabaseWriteLock.AcquireSharedAsync(ct);
        Factory.Recorder.Clear();

        var viewId = await ApiTestClient.GetViewIdAsync(Client, form, ct);
        var (id, uniqueKey) = await FirstDeletedAsync(form, ct);
        var active = await CountAsync(form, includeDeleted: false, ct);
        var all = await CountAsync(form, includeDeleted: true, ct);
        var allPosition = await PositionOfAsync(form, includeDeleted: true, uniqueKey, ct);
        var activeBefore = await PositionOfAsync(form, includeDeleted: false, uniqueKey, ct);

        // Outside the active set the record has no position, though the set is still counted.
        var hidden = await NavigationApi.GetOkAsync(Client, NavigationApi.RecordsPath(viewId, PositionOf(id)), ct);
        NavigationApi.AssertNavigationPosition(hidden, expectedPosition: null, active);

        var shown = await NavigationApi.GetOkAsync(
            Client,
            NavigationApi.RecordsPath(viewId, PositionOf(id), "true"),
            ct);
        NavigationApi.AssertNavigationPosition(shown, allPosition, all);

        // At its all-rows index it is returned deleted, with coordinates intrinsic over every row (C-17).
        var atIndex = await NavigationApi.GetOkAsync(
            Client,
            NavigationApi.RecordsPath(viewId, At(allPosition), "true"),
            ct);
        AssertDeletedRecord(atIndex, id, allPosition, all);
        await AssertIntrinsicAsync(form, atIndex, ct);

        // Opened by id, a deleted record is returned with the same intrinsic coordinates (DATA-02).
        var byId = await NavigationApi.GetOkAsync(Client, NavigationApi.RecordsPath(viewId, ById(id)), ct);
        AssertDeletedRecord(byId, id, allPosition, all);

        // The active walk steps over it: the active neighbours on either side are other, active records.
        if (activeBefore >= 1)
        {
            var previous = await NavigationApi.GetOkAsync(
                Client,
                NavigationApi.RecordsPath(viewId, At(activeBefore)),
                ct);
            AssertActiveNeighbour(previous, id, uniqueKey, before: true);
        }

        if (activeBefore + 1 <= active)
        {
            var next = await NavigationApi.GetOkAsync(
                Client,
                NavigationApi.RecordsPath(viewId, At(activeBefore + 1)),
                ct);
            Assert.Equal(
                await IdAtAsync(form, includeDeleted: false, activeBefore + 1, ct),
                NavigationApi.GetString(next, "globalRecordId"));
            AssertActiveNeighbour(next, id, uniqueKey, before: false);
        }

        AssertNoWritesIssued();
    }

    [Theory]
    [InlineData(CaseReport)]
    [InlineData(FoodHistory)]
    public async Task MixedSet_PositionDiffersBySetButRecordStaysIntrinsic(string form)
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        var ct = timeout.Token;
        await using var dbLock = await DatabaseWriteLock.AcquireSharedAsync(ct);
        Factory.Recorder.Clear();

        var viewId = await ApiTestClient.GetViewIdAsync(Client, form, ct);
        var (id, uniqueKey) = await FirstActiveAfterDeletedAsync(form, ct);
        var active = await CountAsync(form, includeDeleted: false, ct);
        var all = await CountAsync(form, includeDeleted: true, ct);
        var activePosition = await PositionOfAsync(form, includeDeleted: false, uniqueKey, ct);
        var allPosition = await PositionOfAsync(form, includeDeleted: true, uniqueKey, ct);
        var deletedBefore = await DeletedBeforeAsync(form, uniqueKey, ct);

        Assert.Equal(activePosition + deletedBefore, allPosition);
        Assert.True(
            allPosition > activePosition,
            $"{form}: a deleted row precedes the record, so its all-rows position must exceed its active one.");

        // The navigator's coordinates follow the toggle.
        var activeSet = await NavigationApi.GetOkAsync(Client, NavigationApi.RecordsPath(viewId, PositionOf(id)), ct);
        NavigationApi.AssertNavigationPosition(activeSet, activePosition, active);

        var allRows = await NavigationApi.GetOkAsync(
            Client,
            NavigationApi.RecordsPath(viewId, PositionOf(id), "true"),
            ct);
        NavigationApi.AssertNavigationPosition(allRows, allPosition, all);

        // The same record found at its all-rows index still carries its active coordinates (C-17).
        var atAllIndex = await NavigationApi.GetOkAsync(
            Client,
            NavigationApi.RecordsPath(viewId, At(allPosition), "true"),
            ct);
        AssertActiveRecord(atAllIndex, id, activePosition, active);
        await AssertIntrinsicAsync(form, atAllIndex, ct);

        var atActiveIndex = await NavigationApi.GetOkAsync(
            Client,
            NavigationApi.RecordsPath(viewId, At(activePosition)),
            ct);
        AssertActiveRecord(atActiveIndex, id, activePosition, active);

        var byId = await NavigationApi.GetOkAsync(Client, NavigationApi.RecordsPath(viewId, ById(id)), ct);
        AssertActiveRecord(byId, id, activePosition, active);

        AssertNoWritesIssued();
    }

    [Theory]
    [InlineData(CaseReport)]
    [InlineData(FoodHistory)]
    public async Task PositionEndpoint_UnknownRecord_Returns404(string form)
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        var ct = timeout.Token;
        await using var dbLock = await DatabaseWriteLock.AcquireSharedAsync(ct);
        Factory.Recorder.Clear();

        var viewId = await ApiTestClient.GetViewIdAsync(Client, form, ct);
        var unknown = Guid.NewGuid().ToString("D");
        Assert.Null(await BaseRowAsync(form, unknown, ct));

        await AssertNotFoundAsync(NavigationApi.RecordsPath(viewId, PositionOf(unknown)), ct);
        await AssertNotFoundAsync(NavigationApi.RecordsPath(viewId, PositionOf(unknown), "true"), ct);
        await AssertNotFoundAsync(NavigationApi.RecordsPath(viewId, ById(unknown)), ct);

        var known = await IdAtAsync(form, includeDeleted: false, 1, ct);
        await NavigationApi.AssertParameterProblemAsync(
            Client,
            NavigationApi.RecordsPath(viewId, PositionOf(known), "maybe"),
            NavigationApi.IncludeDeletedParameter,
            ct);

        AssertNoWritesIssued();
    }

    // C-16: count, at, positions, the list and search all select RecStatus = 1, where legacy Find used RecStatus > 0.
    // The two predicates agree only while the stored values stay within {0, 1}, so any other value is reported as a
    // parity gap before the assertion fails on it.
    [Fact]
    public async Task RecStatusValues_AreSubsetOfZeroAndOne()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        var ct = timeout.Token;
        await using var dbLock = await DatabaseWriteLock.AcquireSharedAsync(ct);
        Factory.Recorder.Clear();

        int[] admitted = [RecordQueryRules.DeletedRecStatus, RecordQueryRules.ActiveRecStatus];
        foreach (var form in new[] { CaseReport, FoodHistory })
        {
            var values = await DistinctRecStatusAsync(form, ct);
            foreach (var value in values.Where(value => !admitted.Contains(value)))
            {
                _output.WriteLine(
                    $"Parity gap (C-16): {form} holds RecStatus {value.ToString(CultureInfo.InvariantCulture)}; " +
                    "active set is RecStatus = 1, legacy Find used RecStatus > 0");
            }

            Assert.All(values, value => Assert.Contains(value, admitted));
            Assert.Contains(RecordQueryRules.ActiveRecStatus, values);

            // The seeded synthetic rows include soft-deleted records in both forms (testdata/sql/99_verify.sql).
            Assert.Contains(RecordQueryRules.DeletedRecStatus, values);
        }

        AssertNoWritesIssued();
    }

    // AAP 0.3.4.4: the position endpoint reads only the base table, and record reads LEFT JOIN every page table, so a
    // base row without its page-two row is still placed and reads with null page-two values.
    [Fact]
    public async Task MissingPageRow_IsStillPlacedAndReadWithNulls()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        var ct = timeout.Token;
        await using var dbLock = await DatabaseWriteLock.AcquireExclusiveAsync(ct);

        var viewId = await ApiTestClient.GetViewIdAsync(Client, FoodHistory, ct);
        var definition = await NavigationApi.GetOkAsync(
            Client,
            string.Create(CultureInfo.InvariantCulture, $"/api/forms/{viewId}"),
            ct);
        var pages = NavigationApi.GetArray(definition, "pages")
            .OrderBy(page => NavigationApi.GetInt(page, "position"))
            .ToList();
        Assert.True(pages.Count >= 2, $"{FoodHistory} has fewer than two pages.");

        // The page table the fixture deletes from is the one the metadata names for the second page.
        var pageTwoId = NavigationApi.GetInt(pages[1], "pageId");
        Assert.Equal(
            FoodHistoryPageTwoName,
            FoodHistory + pageTwoId.ToString(CultureInfo.InvariantCulture));

        var pageTwoFields = NavigationApi.GetArray(pages[1], "fields")
            .Where(field => NavigationApi.GetString(field, "fieldType") is not ("label" or "group" or "unsupported"))
            .ToList();
        Assert.NotEmpty(pageTwoFields);

        // A stored page-two value makes the later nulls the work of the LEFT JOIN, not of the insert.
        var checkbox = pageTwoFields.FirstOrDefault(
            field => NavigationApi.GetString(field, "fieldType") == "checkbox" &&
                NavigationApi.GetProperty(NavigationApi.GetProperty(field, "validation"), "readOnly").ValueKind ==
                JsonValueKind.False);
        Assert.True(
            checkbox.ValueKind == JsonValueKind.Object,
            $"Page two of {FoodHistory} has no writable checkbox to store a value in.");
        var checkboxName = NavigationApi.GetString(checkbox, "name");

        var marker = TestDatabase.NewMarker();
        var created = await ApiTestClient.CreateRecordAsync(
            Client,
            viewId,
            new Dictionary<string, object?> { [TestDatabase.MarkerField] = marker, [checkboxName] = true },
            ct);
        var id = created.GlobalRecordId;
        var createdValues = NavigationApi.GetProperty(created.Record, "values");
        Assert.Equal(marker, NavigationApi.GetString(createdValues, TestDatabase.MarkerField));
        Assert.Equal(JsonValueKind.True, NavigationApi.GetProperty(createdValues, checkboxName).ValueKind);

        // The fixture: the one direct write, on the record this test created.
        Assert.Equal(1, await DeletePageTwoRowAsync(id, ct));
        Assert.Equal(1, await CountRowsAsync(FoodHistoryBaseTable, id, ct));
        Assert.Equal(1, await CountRowsAsync(FoodHistoryPageOneTable, id, ct));
        Assert.Equal(0, await CountRowsAsync(FoodHistoryPageTwoTable, id, ct));

        // From here on the test only reads.
        Factory.Recorder.Clear();
        var baseRow = await BaseRowAsync(FoodHistory, id, ct);
        Assert.True(baseRow.HasValue, "The created record has no base row.");
        var (uniqueKey, recStatus) = baseRow.Value;
        Assert.Equal(RecordQueryRules.ActiveRecStatus, recStatus);

        var active = await CountAsync(FoodHistory, includeDeleted: false, ct);
        var all = await CountAsync(FoodHistory, includeDeleted: true, ct);
        var activePosition = await PositionOfAsync(FoodHistory, includeDeleted: false, uniqueKey, ct);
        var allPosition = await PositionOfAsync(FoodHistory, includeDeleted: true, uniqueKey, ct);

        var placedActive = await NavigationApi.GetOkAsync(
            Client,
            NavigationApi.RecordsPath(viewId, PositionOf(id)),
            ct);
        NavigationApi.AssertNavigationPosition(placedActive, activePosition, active);

        var placedAll = await NavigationApi.GetOkAsync(
            Client,
            NavigationApi.RecordsPath(viewId, PositionOf(id), "true"),
            ct);
        NavigationApi.AssertNavigationPosition(placedAll, allPosition, all);

        var atPosition = await NavigationApi.GetOkAsync(
            Client,
            NavigationApi.RecordsPath(viewId, At(activePosition)),
            ct);
        Assert.Equal(id, NavigationApi.GetString(atPosition, "globalRecordId"));
        AssertMarkerAndMissingPage(atPosition, marker, pageTwoFields);
        AssertActiveRecord(atPosition, id, activePosition, active);
        await AssertIntrinsicAsync(FoodHistory, atPosition, ct);

        var byId = await NavigationApi.GetOkAsync(Client, NavigationApi.RecordsPath(viewId, ById(id)), ct);
        AssertMarkerAndMissingPage(byId, marker, pageTwoFields);
        AssertActiveRecord(byId, id, activePosition, active);

        // The record stays in the database, as every test-created record does (AAP 0.7.5).
        AssertNoWritesIssued();
    }

    // The first, middle and last 1-based index of a set of count rows, each once.
    private static SortedSet<int> StartMiddleEnd(int count) => [1, (count + 1) / 2, count];

    private static string At(int position) =>
        string.Create(CultureInfo.InvariantCulture, $"/at/{position}");

    private static string ById(string globalRecordId) => "/" + Uri.EscapeDataString(globalRecordId);

    private static string PositionOf(string globalRecordId) => ById(globalRecordId) + "/position";

    // A Record DTO for a deleted record: numbered over every row, whatever set it was opened from.
    private static void AssertDeletedRecord(JsonElement record, string globalRecordId, int position, int count)
    {
        Assert.Equal(globalRecordId, NavigationApi.GetString(record, "globalRecordId"));
        Assert.Equal(RecordQueryRules.DeletedRecStatus, NavigationApi.GetInt(record, "recStatus"));
        Assert.Equal(position, NavigationApi.GetInt(record, "position"));
        Assert.Equal(count, NavigationApi.GetInt(record, "count"));
    }

    // A Record DTO for an active record: numbered over the active set, whatever set it was opened from.
    private static void AssertActiveRecord(JsonElement record, string globalRecordId, int position, int count)
    {
        Assert.Equal(globalRecordId, NavigationApi.GetString(record, "globalRecordId"));
        Assert.Equal(RecordQueryRules.ActiveRecStatus, NavigationApi.GetInt(record, "recStatus"));
        Assert.Equal(position, NavigationApi.GetInt(record, "position"));
        Assert.Equal(count, NavigationApi.GetInt(record, "count"));
    }

    // An active record next to a deleted one in the active walk: another record, on the given side of its key.
    private static void AssertActiveNeighbour(JsonElement record, string deletedId, int deletedKey, bool before)
    {
        Assert.NotEqual(deletedId, NavigationApi.GetString(record, "globalRecordId"));
        Assert.Equal(RecordQueryRules.ActiveRecStatus, NavigationApi.GetInt(record, "recStatus"));
        var uniqueKey = NavigationApi.GetInt(record, "uniqueKey");
        Assert.True(
            before ? uniqueKey < deletedKey : uniqueKey > deletedKey,
            $"The active neighbour {(before ? "before" : "after")} the deleted record is on the wrong side of it.");
    }

    // The created record's page-one marker survives, and every page-two data field reads as JSON null.
    private static void AssertMarkerAndMissingPage(
        JsonElement record,
        string marker,
        IReadOnlyList<JsonElement> pageTwoFields)
    {
        var values = NavigationApi.GetProperty(record, "values");
        Assert.Equal(marker, NavigationApi.GetString(values, TestDatabase.MarkerField));
        foreach (var field in pageTwoFields)
        {
            var name = NavigationApi.GetString(field, "name");
            Assert.True(
                NavigationApi.GetProperty(values, name).ValueKind == JsonValueKind.Null,
                $"Page-two field '{name}' does not read as null for a record without a page-two row.");
        }
    }

    // Reads never write (DATA-01): the API sent no INSERT, UPDATE, DELETE or MERGE since the recorder was cleared.
    private void AssertNoWritesIssued()
    {
        var writes = Factory.Recorder.Writes();
        Assert.True(
            writes.Count == 0,
            $"A read sent {writes.Count} write command(s): {string.Join("; ", writes)}");
    }

    // AAP 0.3.4.3, C-17: a Record's position and count are taken over its intrinsic set, the active rows when its
    // recStatus is 1 and every row otherwise, and never over the set the request selected. Its uniqueKey and recStatus
    // are those of its base row.
    private static async Task AssertIntrinsicAsync(string form, JsonElement record, CancellationToken ct)
    {
        var globalRecordId = NavigationApi.GetString(record, "globalRecordId");
        var uniqueKey = NavigationApi.GetInt(record, "uniqueKey");
        var recStatus = NavigationApi.GetInt(record, "recStatus");

        var baseRow = await BaseRowAsync(form, globalRecordId, ct);
        Assert.True(baseRow.HasValue, $"{form}: the returned record has no base row.");
        Assert.Equal(baseRow.Value.UniqueKey, uniqueKey);
        Assert.Equal(baseRow.Value.RecStatus, recStatus);

        var intrinsicIncludesDeleted = recStatus != RecordQueryRules.ActiveRecStatus;
        Assert.Equal(
            await PositionOfAsync(form, intrinsicIncludesDeleted, uniqueKey, ct),
            NavigationApi.GetInt(record, "position"));
        Assert.Equal(
            await CountAsync(form, intrinsicIncludesDeleted, ct),
            NavigationApi.GetInt(record, "count"));
    }

    private Task AssertNotFoundAsync(string path, CancellationToken ct) =>
        NavigationApi.GetProblemAsync(Client, path, HttpStatusCode.NotFound, ct);

    private async Task<int> GetCountAsync(int viewId, string? includeDeleted, CancellationToken ct)
    {
        var body = await NavigationApi.GetOkAsync(
            Client,
            NavigationApi.RecordsPath(viewId, "/count", includeDeleted),
            ct);
        return NavigationApi.GetInt(body, "count");
    }

    // The closed map of base tables; any other form name is a test error.
    private static string BaseTable(string form) => form switch
    {
        CaseReport => CaseReportBaseTable,
        FoodHistory => FoodHistoryBaseTable,
        _ => throw new ArgumentOutOfRangeException(nameof(form), form, "No base table is mapped for this form."),
    };

    private static async Task<int> CountAsync(string form, bool includeDeleted, CancellationToken ct)
    {
        await using var connection = await TestDatabase.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {BaseTable(form)} WHERE {SetPredicate};";
        AddSetParameter(command, includeDeleted);
        return Assert.IsType<int>(await command.ExecuteScalarAsync(ct));
    }

    // The GlobalRecordId at a 1-based index of the selected set, in UniqueKey order.
    private static async Task<string> IdAtAsync(string form, bool includeDeleted, int position, CancellationToken ct)
    {
        await using var connection = await TestDatabase.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT [GlobalRecordId] FROM {BaseTable(form)} WHERE {SetPredicate} " +
            "ORDER BY [UniqueKey] OFFSET @offset ROWS FETCH NEXT 1 ROWS ONLY;";
        AddSetParameter(command, includeDeleted);
        command.Parameters.Add(new SqlParameter("@offset", SqlDbType.BigInt) { Value = (long)position - 1 });
        return await command.ExecuteScalarAsync(ct) as string ?? throw new XunitException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{form} has no row at index {position} of the {(includeDeleted ? "all-rows" : "active")} set."));
    }

    // The legacy record number restricted to the selected set: the rows whose UniqueKey is at most k.
    private static async Task<int> PositionOfAsync(
        string form,
        bool includeDeleted,
        int uniqueKey,
        CancellationToken ct)
    {
        await using var connection = await TestDatabase.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT COUNT(*) FROM {BaseTable(form)} WHERE {SetPredicate} AND [UniqueKey] <= @k;";
        AddSetParameter(command, includeDeleted);
        command.Parameters.Add(new SqlParameter("@k", SqlDbType.Int) { Value = uniqueKey });
        return Assert.IsType<int>(await command.ExecuteScalarAsync(ct));
    }

    // The record's base row, or null when the base table holds no such GlobalRecordId.
    private static async Task<(int UniqueKey, int RecStatus)?> BaseRowAsync(
        string form,
        string globalRecordId,
        CancellationToken ct)
    {
        await using var connection = await TestDatabase.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT [UniqueKey], [RecStatus] FROM {BaseTable(form)} WHERE [GlobalRecordId] = @id;";
        AddIdParameter(command, globalRecordId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        (int UniqueKey, int RecStatus) row = (reader.GetInt32(0), reader.GetInt16(1));
        Assert.False(await reader.ReadAsync(ct), $"{form} holds more than one base row for one GlobalRecordId.");
        return row;
    }

    // The deleted row with the lowest key. Records tests create get higher keys, so this is a seeded one.
    private static Task<(string GlobalRecordId, int UniqueKey)> FirstDeletedAsync(string form, CancellationToken ct) =>
        ReadFirstRowAsync(
            form,
            $"SELECT TOP (1) [GlobalRecordId], [UniqueKey] FROM {BaseTable(form)} WHERE [RecStatus] = 0 " +
            "ORDER BY [UniqueKey];",
            "a soft-deleted record",
            ct);

    // The first active row whose immediate predecessor in UniqueKey order is deleted.
    private static Task<(string GlobalRecordId, int UniqueKey)> FirstActiveAfterDeletedAsync(
        string form,
        CancellationToken ct) =>
        ReadFirstRowAsync(
            form,
            "SELECT TOP (1) x.[GlobalRecordId], x.[UniqueKey] FROM (" +
            "SELECT [GlobalRecordId], [UniqueKey], [RecStatus], " +
            $"LAG([RecStatus]) OVER (ORDER BY [UniqueKey]) AS [PrevStatus] FROM {BaseTable(form)}) x " +
            "WHERE x.[RecStatus] = 1 AND x.[PrevStatus] = 0 ORDER BY x.[UniqueKey];",
            "an active record directly after a soft-deleted one",
            ct);

    private static async Task<(string GlobalRecordId, int UniqueKey)> ReadFirstRowAsync(
        string form,
        string commandText,
        string description,
        CancellationToken ct)
    {
        await using var connection = await TestDatabase.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            throw new XunitException(
                $"{form} holds no {description}; the seeded precondition (AAP 0.7.3) is not met: every 99_verify " +
                "row must show ok = 1.");
        }

        return (reader.GetString(0), reader.GetInt32(1));
    }

    // The rows before k that the active set leaves out.
    private static async Task<int> DeletedBeforeAsync(string form, int uniqueKey, CancellationToken ct)
    {
        await using var connection = await TestDatabase.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT COUNT(*) FROM {BaseTable(form)} WHERE [RecStatus] <> 1 AND [UniqueKey] < @k;";
        command.Parameters.Add(new SqlParameter("@k", SqlDbType.Int) { Value = uniqueKey });
        return Assert.IsType<int>(await command.ExecuteScalarAsync(ct));
    }

    private static async Task<IReadOnlyList<int>> DistinctRecStatusAsync(string form, CancellationToken ct)
    {
        await using var connection = await TestDatabase.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT DISTINCT [RecStatus] FROM {BaseTable(form)} ORDER BY [RecStatus];";
        await using var reader = await command.ExecuteReaderAsync(ct);
        var values = new List<int>();
        while (await reader.ReadAsync(ct))
        {
            values.Add(reader.GetInt16(0));
        }

        return values.AsReadOnly();
    }

    // The missing-page fixture: removes the page-two row of a record this test created, and nothing else.
    private static async Task<int> DeletePageTwoRowAsync(string globalRecordId, CancellationToken ct)
    {
        await using var connection = await TestDatabase.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM {FoodHistoryPageTwoTable} WHERE [GlobalRecordId] = @id;";
        AddIdParameter(command, globalRecordId);
        return await command.ExecuteNonQueryAsync(ct);
    }

    // The rows of one of the FoodHistory tables that carry the GlobalRecordId.
    private static async Task<int> CountRowsAsync(string table, string globalRecordId, CancellationToken ct)
    {
        if (table is not (FoodHistoryBaseTable or FoodHistoryPageOneTable or FoodHistoryPageTwoTable))
        {
            throw new ArgumentOutOfRangeException(nameof(table), table, "The table is not a FoodHistory table.");
        }

        await using var connection = await TestDatabase.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE [GlobalRecordId] = @id;";
        AddIdParameter(command, globalRecordId);
        return Assert.IsType<int>(await command.ExecuteScalarAsync(ct));
    }

    private static void AddSetParameter(SqlCommand command, bool includeDeleted) =>
        command.Parameters.Add(new SqlParameter("@all", SqlDbType.Bit) { Value = includeDeleted });

    private static void AddIdParameter(SqlCommand command, string globalRecordId) =>
        command.Parameters.Add(new SqlParameter("@id", SqlDbType.NVarChar, 255) { Value = globalRecordId });
}

/// <summary>
/// SC-5 on an empty form, over <see cref="FakeDataStore"/> (AAP 0.7.1): the seeded database has no empty form and
/// gains none, so the in-memory store supplies one. The count is 0, no position exists, and the list and search
/// return no records without error. No case writes.
/// </summary>
/// <remarks>
/// The class needs no database: it carries no <c>Integration</c> trait, joins no database collection and takes no
/// lock, so <c>--filter "Category!=Integration"</c> selects it. The host replaces the SQL Server store with the fake
/// and has no connection string, so nothing reaches SQL Server.
/// </remarks>
public sealed class EmptyFormNavigationTests : IAsyncLifetime
{
    private readonly FakeForm _form = FakeForms.Empty;

    private readonly FakeDataStore _store = new();

    private ApiFactory? _factory;

    private HttpClient? _client;

    private int _viewId;

    private HttpClient Client => _client ?? throw new InvalidOperationException("The API client was not created.");

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _store.AddForm(_form);
        _factory = new ApiFactory(ApiFactoryOptions.ForFake(_store));
        _client = _factory.CreateClient();

        _viewId = await ApiTestClient.GetViewIdAsync(_client, _form.Metadata.Name);
        Assert.Equal(_form.Metadata.ViewId, _viewId);
        Assert.Empty(_store.GetRecords(_viewId));
        _store.ClearCalls();
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
    }

    [Fact]
    public async Task Count_IsZero()
    {
        foreach (var includeDeleted in new[] { null, "false", "true" })
        {
            var body = await NavigationApi.GetOkAsync(
                Client,
                NavigationApi.RecordsPath(_viewId, "/count", includeDeleted));
            Assert.Equal(0, NavigationApi.GetInt(body, "count"));
        }

        Assert.Empty(_store.WriteCalls);
    }

    [Fact]
    public async Task At1_Returns404()
    {
        await NavigationApi.GetProblemAsync(
            Client,
            NavigationApi.RecordsPath(_viewId, "/at/1"),
            HttpStatusCode.NotFound);
        await NavigationApi.GetProblemAsync(
            Client,
            NavigationApi.RecordsPath(_viewId, "/at/1", "true"),
            HttpStatusCode.NotFound);

        Assert.Empty(_store.WriteCalls);
    }

    [Fact]
    public async Task List_ReturnsEmptyPage()
    {
        foreach (var includeDeleted in new[] { null, "true" })
        {
            var page = await NavigationApi.GetOkAsync(
                Client,
                NavigationApi.RecordsPath(_viewId, string.Empty, includeDeleted));
            Assert.Empty(NavigationApi.GetArray(page, "items"));
            Assert.Equal(0, NavigationApi.GetInt(page, "total"));
            Assert.Equal(RecordQueryRules.DefaultPage, NavigationApi.GetInt(page, "page"));
            Assert.Equal(RecordQueryRules.DefaultPageSize, NavigationApi.GetInt(page, "pageSize"));
        }

        Assert.Empty(_store.WriteCalls);
    }

    [Fact]
    public async Task Search_ReturnsEmpty()
    {
        // A valid criterion: the form's first text field with a non-blank value, else the searchable system field.
        var definition = await NavigationApi.GetOkAsync(
            Client,
            string.Create(CultureInfo.InvariantCulture, $"/api/forms/{_viewId}"));
        var textField = NavigationApi.GetArray(definition, "pages")
            .SelectMany(page => NavigationApi.GetArray(page, "fields"))
            .Where(field => NavigationApi.GetString(field, "fieldType") == "text")
            .Select(field => NavigationApi.GetString(field, "name"))
            .FirstOrDefault();
        var criterion = textField is not null
            ? new { field = textField, value = "x" }
            : new { field = "GlobalRecordId", value = Guid.NewGuid().ToString("D") };

        using var response = await ApiTestClient.PostJsonAsync(
            Client,
            NavigationApi.RecordsPath(_viewId, "/search"),
            new { criteria = new[] { criterion } });
        var result = await NavigationApi.ReadOkAsync(response, "POST search");
        Assert.Empty(NavigationApi.GetArray(result, "items"));
        Assert.Equal(0, NavigationApi.GetInt(result, "total"));
        Assert.Equal(JsonValueKind.False, NavigationApi.GetProperty(result, "truncated").ValueKind);

        Assert.Empty(_store.WriteCalls);
    }
}

// HTTP and JSON helpers shared by the two classes above. Failure messages name the request path and the member, never
// a member's value, beyond the ids and numbers the tests compare.
file static class NavigationApi
{
    public const string IncludeDeletedParameter = "includeDeleted";

    public const string PositionParameter = "position";

    private const string ProblemMediaType = "application/problem+json";

    // A path under /api/forms/{viewId}/records, with the deleted toggle as sent when includeDeleted is not null.
    public static string RecordsPath(int viewId, string relative, string? includeDeleted = null)
    {
        var path = string.Create(CultureInfo.InvariantCulture, $"/api/forms/{viewId}/records{relative}");
        return includeDeleted is null
            ? path
            : $"{path}?{IncludeDeletedParameter}={Uri.EscapeDataString(includeDeleted)}";
    }

    public static async Task<JsonElement> GetOkAsync(HttpClient client, string path, CancellationToken ct = default)
    {
        using var response = await client.GetAsync(path, ct);
        return await ReadOkAsync(response, "GET " + path, ct);
    }

    public static async Task<JsonElement> ReadOkAsync(
        HttpResponseMessage response,
        string request,
        CancellationToken ct = default)
    {
        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{request} returned {(int)response.StatusCode} {response.StatusCode}, expected 200."));
        Assert.Equal(ApiTestClient.JsonMediaType, response.Content.Headers.ContentType?.MediaType);
        return await ApiTestClient.ReadJsonElementAsync(response, ct);
    }

    // A ProblemDetails response with the given status.
    public static async Task<JsonElement> GetProblemAsync(
        HttpClient client,
        string path,
        HttpStatusCode status,
        CancellationToken ct = default)
    {
        using var response = await client.GetAsync(path, ct);
        Assert.True(
            response.StatusCode == status,
            string.Create(
                CultureInfo.InvariantCulture,
                $"GET {path} returned {(int)response.StatusCode} {response.StatusCode}, expected {(int)status}."));
        Assert.Equal(ProblemMediaType, response.Content.Headers.ContentType?.MediaType);
        var body = await ApiTestClient.ReadJsonElementAsync(response, ct);
        return ApiTestClient.AssertProblemDetails(body, (int)status);
    }

    // A field-level 400 whose errors map names exactly the one parameter, with at least one message.
    public static async Task AssertParameterProblemAsync(
        HttpClient client,
        string path,
        string parameter,
        CancellationToken ct = default)
    {
        var body = await GetProblemAsync(client, path, HttpStatusCode.BadRequest, ct);
        var errors = GetProperty(body, "errors");
        Assert.Equal(JsonValueKind.Object, errors.ValueKind);
        var keys = errors.EnumerateObject().Select(member => member.Name).ToList();
        Assert.True(
            keys.Count == 1 && keys[0] == parameter,
            $"GET {path}: errors must name exactly '{parameter}'; it names [{string.Join(", ", keys)}].");
        var messages = GetArray(errors, parameter);
        Assert.NotEmpty(messages);
        Assert.All(messages, message => Assert.Equal(JsonValueKind.String, message.ValueKind));
    }

    // A NavigationPosition body: position null when the record is outside the selected set.
    public static void AssertNavigationPosition(JsonElement body, int? expectedPosition, int expectedCount)
    {
        var position = GetProperty(body, "position");
        if (expectedPosition is int expected)
        {
            Assert.Equal(JsonValueKind.Number, position.ValueKind);
            Assert.Equal(expected, position.GetInt32());
        }
        else
        {
            Assert.Equal(JsonValueKind.Null, position.ValueKind);
        }

        Assert.Equal(expectedCount, GetInt(body, "count"));
    }

    public static JsonElement GetProperty(JsonElement element, string name)
    {
        Assert.Equal(JsonValueKind.Object, element.ValueKind);
        Assert.True(element.TryGetProperty(name, out var value), $"The JSON object has no '{name}' member.");
        return value;
    }

    public static int GetInt(JsonElement element, string name)
    {
        var value = GetProperty(element, name);
        Assert.True(
            value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _),
            $"Member '{name}' must be a 32-bit integer; it is of kind {value.ValueKind}.");
        return value.GetInt32();
    }

    public static string GetString(JsonElement element, string name)
    {
        var value = GetProperty(element, name);
        Assert.True(
            value.ValueKind == JsonValueKind.String,
            $"Member '{name}' must be a string; it is of kind {value.ValueKind}.");
        return value.GetString() ?? string.Empty;
    }

    public static IReadOnlyList<JsonElement> GetArray(JsonElement element, string name)
    {
        var value = GetProperty(element, name);
        Assert.True(
            value.ValueKind == JsonValueKind.Array,
            $"Member '{name}' must be an array; it is of kind {value.ValueKind}.");
        return value.EnumerateArray().ToList();
    }
}
