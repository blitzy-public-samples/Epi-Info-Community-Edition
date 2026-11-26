using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using EpiInfo.Api.Tests.Infrastructure;
using EpiInfo.Data.Sql;
using EpiInfo.Tests.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
/// run so those values cannot move. The writing cases hold it exclusively. The missing-page case writes one record
/// created through the API with a marker, and removes that record's own <c>FoodHistory3</c> row, the missing-page
/// fixture AAP 0.3.7 allows. The two interleaving cases each create one record with a marker and soft-delete it, and
/// the write-response case creates one record with a marker and updates it, all through the API. The held-page case
/// creates its records through the API with one marker, and on a second connection takes an exclusive lock on one of
/// their <c>CaseReport1</c> rows in a transaction it rolls back, a lock that changes no data. No seeded row, metadata
/// row or schema object is touched.
/// </para>
/// <para>
/// Table names in the SQL below come from a closed map of bracket-quoted literals, and every value is a parameter.
/// The row states 0 and 1 are test-owned constants, in the SQL and in the assertions, never production values.
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

    // The CaseReport page table, which holds the grid columns the held-page case filters on.
    private const string CaseReportPageOneTable = "[dbo].[CaseReport1]";

    // The sys.dm_tran_locks names the held-page case binds: a granted key lock, in the mode the gate takes and in the
    // modes a held shared read takes on a key, S on a row it found and RangeS-S on a key range.
    private const string KeyLockType = "KEY";

    private const string GrantedLockStatus = "GRANT";

    private const string ExclusiveMode = "X";

    private const string SharedMode = "S";

    private const string RangeSharedMode = "RangeS-S";

    // The held-page case stops creating records at the first one whose GlobalRecordId is above the lowest created
    // before it, so it reaches this bound only if all of its records arrive in strictly decreasing GlobalRecordId
    // order: for random ids, a probability of 1/32!.
    private const int MaxHeldPageRecords = 32;

    // Test-owned row states, never read from production code, typed as the smallint RecStatus column: 1 is the active
    // set (C-16) and 0 a soft-deleted record (DELETE sets RECSTATUS=0, AAP 0.3.4.1).
    private const short ActiveRecStatus = 1;

    private const short DeletedRecStatus = 0;

    // The selected-set predicate of C-16, with @all choosing every row, so one statement serves both sets. Bound by
    // AddSetParameter.
    private const string SetPredicate = "(@all = 1 OR [RecStatus] = @active)";

    // Test-owned copies of the fixed operation names the data store reports to its command observer for the
    // position endpoint, the records list, a create and an update.
    private const string SetPositionOperation = "records.setPosition";

    private const string ListOperation = "records.list";

    private const string InsertOperation = "records.insert";

    private const string UpdateOperation = "records.update";

    // Longer than DatabaseWriteLock's 300-second wait, so a hung request fails the test instead of the whole run.
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(10);

    // How long the held-page case waits for the list to block on the gate's row, well inside the API's 30-second command
    // timeout, and how often it looks.
    private static readonly TimeSpan BlockedRequestWait = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan BlockedRequestPoll = TimeSpan.FromMilliseconds(25);

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
            Assert.Equal(ActiveRecStatus, NavigationApi.GetInt(record, "recStatus"));
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

        int[] admitted = [DeletedRecStatus, ActiveRecStatus];
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
            Assert.Contains(ActiveRecStatus, values);

            // The seeded synthetic rows include soft-deleted records in both forms (testdata/sql/99_verify.sql).
            Assert.Contains(DeletedRecStatus, values);
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
        Assert.Equal(ActiveRecStatus, recStatus);

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

    // AAP 0.3.4.4, C-16: the position endpoint takes the record's membership, its position and the count from one
    // committed state. The host's observer commits a soft delete of the record through the API as soon as the
    // endpoint's first set-position statement has completed, before the endpoint answers. The answer must still
    // describe one state, the one before the delete: the position and the active count SQL read beforehand, never a
    // position counted after the record left the set. The next request sees the delete: no position, one fewer row.
    [Fact]
    public async Task PositionEndpoint_DeleteCommittedDuringTheRequest_AnswersFromOneState()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        var ct = timeout.Token;
        await using var dbLock = await DatabaseWriteLock.AcquireExclusiveAsync(ct);

        await using var host = await InterleavingFactory.ForDatabaseAsync();
        using var client = host.CreateClient();
        using var deleteClient = host.CreateClient();

        var viewId = await ApiTestClient.GetViewIdAsync(client, CaseReport, ct);
        var id = await CreateMarkedRecordAsync(client, viewId, TestDatabase.NewMarker(), ct);

        // The expected answer, read by SQL before the delete: the active record's position and the active count.
        var baseRow = await BaseRowAsync(CaseReport, id, ct);
        Assert.True(baseRow.HasValue, "The created record has no base row.");
        var (uniqueKey, recStatus) = baseRow.Value;
        Assert.Equal(ActiveRecStatus, recStatus);
        var active = await CountAsync(CaseReport, includeDeleted: false, ct);
        var position = await PositionOfAsync(CaseReport, includeDeleted: false, uniqueKey, ct);

        host.Interleaver.Arm(SetPositionOperation, deleteClient, NavigationApi.RecordsPath(viewId, ById(id)), ct);
        var placed = await NavigationApi.GetOkAsync(client, NavigationApi.RecordsPath(viewId, PositionOf(id)), ct);
        await AssertSoftDeletedDuringRequestAsync(host, id, ct);

        NavigationApi.AssertNavigationPosition(placed, position, active);

        var activeAfter = await CountAsync(CaseReport, includeDeleted: false, ct);
        Assert.Equal(active - 1, activeAfter);
        var after = await NavigationApi.GetOkAsync(client, NavigationApi.RecordsPath(viewId, PositionOf(id)), ct);
        NavigationApi.AssertNavigationPosition(after, null, activeAfter);
    }

    // AAP 0.3.4.4 "Records list q": the list's items and its total count one filtered set. The q filter is the
    // record's own marker, in LastName, a CaseReport grid column, so the active filtered set is that record alone. The
    // host's observer commits a soft delete of the record through the API as soon as the list's first statement has
    // completed, before the list answers. The page and the total must still describe one state, the one before the
    // delete: the record, and a total of 1. The next request sees the delete: no item and a total of 0.
    [Fact]
    public async Task List_DeleteCommittedDuringTheRequest_PageAndTotalDescribeOneState()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        var ct = timeout.Token;
        await using var dbLock = await DatabaseWriteLock.AcquireExclusiveAsync(ct);

        await using var host = await InterleavingFactory.ForDatabaseAsync();
        using var client = host.CreateClient();
        using var deleteClient = host.CreateClient();

        var viewId = await ApiTestClient.GetViewIdAsync(client, CaseReport, ct);
        var marker = TestDatabase.NewMarker();
        var id = await CreateMarkedRecordAsync(client, viewId, marker, ct);
        var filtered = NavigationApi.RecordsPath(viewId, "?q=" + Uri.EscapeDataString(marker));

        host.Interleaver.Arm(ListOperation, deleteClient, NavigationApi.RecordsPath(viewId, ById(id)), ct);
        var listed = await NavigationApi.GetOkAsync(client, filtered, ct);
        await AssertSoftDeletedDuringRequestAsync(host, id, ct);

        var item = Assert.Single(NavigationApi.GetArray(listed, "items"));
        Assert.Equal(id, NavigationApi.GetString(item, "globalRecordId"));
        Assert.Equal(1, NavigationApi.GetInt(listed, "total"));

        var after = await NavigationApi.GetOkAsync(client, filtered, ct);
        Assert.Empty(NavigationApi.GetArray(after, "items"));
        Assert.Equal(0, NavigationApi.GetInt(after, "total"));
    }

    // The list's items and its total count one filtered set even when a page row changes between the count and the
    // page, which the list's one statement reads in two evaluations: it holds every page row it reads, as well as the
    // base rows, until it completes. The test creates CaseReport records through the API, all carrying one marker in
    // LastName. The target is the record with the lowest GlobalRecordId created so far, and the gate is the first
    // record created with a GlobalRecordId above the target's; a record created below it becomes the target instead.
    // Created after the target, the gate also has the higher UniqueKey, so the list reads the target's page row before
    // the gate's whichever table leads the join. A second connection takes an exclusive lock on the gate's CaseReport1
    // row in a transaction it rolls back, a lock that changes no data, and the list filtered by the marker blocks on
    // that row. While it waits it must still hold a shared lock on the target's page row: a list that had released it
    // could see that row change before its second evaluation and answer items and a total from two states. Once the
    // lock is rolled back, the list answers every created record, with their number as the total.
    [Fact]
    public async Task List_BlockedOnALaterPageRow_StillHoldsThePageRowsItRead()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        var ct = timeout.Token;
        await using var dbLock = await DatabaseWriteLock.AcquireExclusiveAsync(ct);

        var viewId = await ApiTestClient.GetViewIdAsync(Client, CaseReport, ct);
        var marker = TestDatabase.NewMarker();
        var created = new List<string> { await CreateMarkedRecordAsync(Client, viewId, marker, ct) };
        var target = created[0];
        string? gate = null;
        while (gate is null)
        {
            Assert.True(
                created.Count < MaxHeldPageRecords,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The {created.Count} records created arrived in strictly decreasing GlobalRecordId order."));
            var candidate = await CreateMarkedRecordAsync(Client, viewId, marker, ct);
            created.Add(candidate);
            if (StringComparer.OrdinalIgnoreCase.Compare(candidate, target) > 0)
            {
                gate = candidate;
            }
            else
            {
                target = candidate;
            }
        }

        var targetRow = await BaseRowAsync(CaseReport, target, ct);
        var gateRow = await BaseRowAsync(CaseReport, gate, ct);
        Assert.True(targetRow.HasValue && gateRow.HasValue, "A created record has no base row.");
        Assert.True(
            targetRow.Value.UniqueKey < gateRow.Value.UniqueKey,
            "The gate record does not follow the target record in UniqueKey order.");

        // The lock resources of the two page rows, read before any lock is taken.
        var targetResource = await PageOneLockResourceAsync(target, ct);
        var gateResource = await PageOneLockResourceAsync(gate, ct);
        var filtered = NavigationApi.RecordsPath(viewId, "?q=" + Uri.EscapeDataString(marker));

        Factory.Recorder.Clear();
        Task<HttpResponseMessage>? listing = null;
        string? rollbackFailure;
        await using var monitor = await TestDatabase.OpenConnectionAsync(ct);
        var gateConnection = await TestDatabase.OpenConnectionAsync(ct);
        SqlTransaction? gateTransaction = null;
        try
        {
            gateTransaction = gateConnection.BeginTransaction();
            await LockPageOneRowAsync(gateConnection, gateTransaction, gate, ct);
            var gateSession = gateConnection.ServerProcessId;
            Assert.Contains(ExclusiveMode, await GrantedPageOneKeyModesAsync(monitor, gateSession, gateResource, ct));

            listing = Client.GetAsync(filtered, ct);
            var listSession = await WaitForRequestBlockedByAsync(monitor, gateSession, gateResource, listing, ct);
            var held = await GrantedPageOneKeyModesAsync(monitor, listSession, targetResource, ct);
            Assert.True(
                held.Contains(SharedMode) || held.Contains(RangeSharedMode),
                $"While blocked on the gate's page row, the list holds no granted {SharedMode} or {RangeSharedMode} " +
                $"key lock on the target's page row it read first; it holds [{string.Join(", ", held)}].");
        }
        finally
        {
            rollbackFailure = await ReleaseGateAsync(gateConnection, gateTransaction);
        }

        Assert.True(
            rollbackFailure is null,
            $"Rolling back the transaction that held the gate's page-row lock failed with {rollbackFailure}.");
        Assert.NotNull(listing);
        using var response = await listing;
        var listed = await NavigationApi.ReadOkAsync(response, "GET " + filtered, ct);
        Assert.Equal(
            created,
            NavigationApi.GetArray(listed, "items").Select(item => NavigationApi.GetString(item, "globalRecordId")));
        Assert.Equal(created.Count, NavigationApi.GetInt(listed, "total"));
        AssertNoWritesIssued();
    }

    // AAP 0.3.4.6 "Transaction boundary", 0.3.4.3: the response of a create and of an update is read inside the write's
    // transaction, after its writes, by one statement that returns the rows with their intrinsic position and count,
    // so the coordinates and the row describe one committed state. Each response's coordinates equal SQL's.
    [Fact]
    public async Task WriteResponse_IsOneSnapshotReadAfterTheWritesInsideTheTransaction()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        var ct = timeout.Token;
        await using var dbLock = await DatabaseWriteLock.AcquireExclusiveAsync(ct);

        var viewId = await ApiTestClient.GetViewIdAsync(Client, CaseReport, ct);
        Factory.Recorder.Clear();
        var created = await ApiTestClient.CreateRecordAsync(
            Client,
            viewId,
            new Dictionary<string, object?> { [TestDatabase.MarkerField] = TestDatabase.NewMarker() },
            ct);
        AssertOneSnapshotReadAfterTheWrites(InsertOperation);
        await AssertIntrinsicAsync(CaseReport, created.Record, ct);

        Factory.Recorder.Clear();
        using var response = await ApiTestClient.PutJsonAsync(
            Client,
            NavigationApi.RecordsPath(viewId, ById(created.GlobalRecordId)),
            new { values = new Dictionary<string, object?> { [TestDatabase.MarkerField] = TestDatabase.NewMarker() } },
            ct);
        var updated = await NavigationApi.ReadOkAsync(response, "PUT record", ct);
        AssertOneSnapshotReadAfterTheWrites(UpdateOperation);
        await AssertIntrinsicAsync(CaseReport, updated, ct);
    }

    // Creates a CaseReport record whose LastName holds marker, through the API, and returns its id.
    private static async Task<string> CreateMarkedRecordAsync(
        HttpClient client,
        int viewId,
        string marker,
        CancellationToken ct)
    {
        var created = await ApiTestClient.CreateRecordAsync(
            client,
            viewId,
            new Dictionary<string, object?> { [TestDatabase.MarkerField] = marker },
            ct);
        return created.GlobalRecordId;
    }

    // The interleaved DELETE answered 204 inside the request, and SQL holds the record soft-deleted.
    private static async Task AssertSoftDeletedDuringRequestAsync(
        InterleavingFactory host,
        string globalRecordId,
        CancellationToken ct)
    {
        Assert.Equal(HttpStatusCode.NoContent, host.Interleaver.DeleteStatus());
        var deletedRow = await BaseRowAsync(CaseReport, globalRecordId, ct);
        Assert.True(deletedRow.HasValue, "The soft-deleted record has no base row.");
        Assert.Equal(DeletedRecStatus, deletedRow.Value.RecStatus);
    }

    // The write's commands under its operation: every one succeeded inside one transaction, and after its first write
    // exactly one statement reads, the snapshot, which is the last command the write sends.
    private void AssertOneSnapshotReadAfterTheWrites(string operation)
    {
        var commands = Factory.Recorder.ForOperation(operation);
        Assert.NotEmpty(commands);
        Assert.All(commands, command => Assert.True(
            command.Completed && command.Succeeded && command.InTransaction,
            $"A {operation} command did not succeed inside the transaction: {command}"));
        Assert.Single(commands.Select(command => command.ClientConnectionId).Distinct());

        var firstWrite = commands.First(command => command.IsWrite);
        var readsAfterWrites = commands
            .Where(command => command.Sequence > firstWrite.Sequence && !command.IsWrite)
            .ToList();
        var snapshot = Assert.Single(readsAfterWrites);
        Assert.Equal(commands[^1].Sequence, snapshot.Sequence);
    }

    // The rehearsal-database API host of the interleaving cases: ApiFactoryBase's host, with its command observer
    // replaced by an Interleaver that forwards every command to the factory's Recorder.
    private sealed class InterleavingFactory : ApiFactoryBase
    {
        private InterleavingFactory(DatabaseHost host)
            : base(host)
        {
            Interleaver = new DeleteAfterCommand(Recorder);
        }

        // The observer every command of the host reaches.
        public DeleteAfterCommand Interleaver { get; }

        // Proves the database reachable first and throws otherwise, as ApiFactory.ForDatabaseAsync does.
        public static async Task<InterleavingFactory> ForDatabaseAsync() =>
            new(await DatabaseHost.PrepareAsync(options: null));

        // Runs after the base class's own test services, so this registration replaces the Recorder's.
        protected override void ConfigureHost(IWebHostBuilder builder) =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ISqlCommandObserver>();
                services.AddSingleton<ISqlCommandObserver>(Interleaver);
            });
    }

    // Forwards every command to the inner observer. Once armed, it sends one DELETE through its client inside
    // OnExecuted of the next command of the armed operation, and waits there for the response, so the delete is
    // committed after that statement completed and before the request that sent it can send another or answer.
    private sealed class DeleteAfterCommand(ISqlCommandObserver inner) : ISqlCommandObserver
    {
        private readonly Lock _gate = new();

        private (string Operation, HttpClient Client, string Path, CancellationToken Token)? _armed;

        private HttpStatusCode? _status;

        private Exception? _failure;

        // Arms one DELETE of path; the next command of operation sends it, and later commands are only forwarded.
        public void Arm(string operation, HttpClient client, string path, CancellationToken token)
        {
            lock (_gate)
            {
                _armed = (operation, client, path, token);
                _status = null;
                _failure = null;
            }
        }

        public void OnExecuting(SqlCommandInfo command) => inner.OnExecuting(command);

        public void OnExecuted(SqlCommandInfo command, int? sqlErrorNumber, Exception? error)
        {
            inner.OnExecuted(command, sqlErrorNumber, error);

            (string Operation, HttpClient Client, string Path, CancellationToken Token) armed;
            lock (_gate)
            {
                if (_armed is not { } pending
                    || !string.Equals(command.Operation, pending.Operation, StringComparison.Ordinal))
                {
                    return;
                }

                armed = pending;
                _armed = null;
            }

            try
            {
                // Blocking on purpose: the observer is called synchronously, so the request that ran the statement
                // continues only once the DELETE, a request of its own on another connection, has answered.
                using var response = armed.Client.DeleteAsync(armed.Path, armed.Token).GetAwaiter().GetResult();
                lock (_gate)
                {
                    _status = response.StatusCode;
                }
            }
            catch (Exception failure)
            {
                // An observer must not throw; the failure is raised by DeleteStatus instead.
                lock (_gate)
                {
                    _failure = failure;
                }
            }
        }

        // The status the interleaved DELETE answered with.
        public HttpStatusCode DeleteStatus()
        {
            lock (_gate)
            {
                if (_failure is not null)
                {
                    throw new XunitException("The interleaved DELETE failed.", _failure);
                }

                return _status ?? throw new XunitException(
                    "No command of the armed operation completed while the DELETE was armed, so it was never sent.");
            }
        }
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
        Assert.Equal(DeletedRecStatus, NavigationApi.GetInt(record, "recStatus"));
        Assert.Equal(position, NavigationApi.GetInt(record, "position"));
        Assert.Equal(count, NavigationApi.GetInt(record, "count"));
    }

    // A Record DTO for an active record: numbered over the active set, whatever set it was opened from.
    private static void AssertActiveRecord(JsonElement record, string globalRecordId, int position, int count)
    {
        Assert.Equal(globalRecordId, NavigationApi.GetString(record, "globalRecordId"));
        Assert.Equal(ActiveRecStatus, NavigationApi.GetInt(record, "recStatus"));
        Assert.Equal(position, NavigationApi.GetInt(record, "position"));
        Assert.Equal(count, NavigationApi.GetInt(record, "count"));
    }

    // An active record next to a deleted one in the active walk: another record, on the given side of its key.
    private static void AssertActiveNeighbour(JsonElement record, string deletedId, int deletedKey, bool before)
    {
        Assert.NotEqual(deletedId, NavigationApi.GetString(record, "globalRecordId"));
        Assert.Equal(ActiveRecStatus, NavigationApi.GetInt(record, "recStatus"));
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

        // The set is chosen from the row state SQL read, so the oracle does not rest on the response under test.
        var intrinsicIncludesDeleted = baseRow.Value.RecStatus != ActiveRecStatus;
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
            $"SELECT TOP (1) [GlobalRecordId], [UniqueKey] FROM {BaseTable(form)} WHERE [RecStatus] = @deleted " +
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
            "WHERE x.[RecStatus] = @active AND x.[PrevStatus] = @deleted ORDER BY x.[UniqueKey];",
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

        // Both row states are bound, so each caller's query compares RecStatus only through @active and @deleted.
        AddActiveParameter(command);
        AddDeletedParameter(command);
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
            $"SELECT COUNT(*) FROM {BaseTable(form)} WHERE [RecStatus] <> @active AND [UniqueKey] < @k;";
        AddActiveParameter(command);
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

    // The lock resource of a record's CaseReport1 row, as sys.dm_tran_locks describes a key lock on it.
    private static async Task<string> PageOneLockResourceAsync(string globalRecordId, CancellationToken ct)
    {
        await using var connection = await TestDatabase.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT %%lockres%% FROM {CaseReportPageOneTable} WHERE [GlobalRecordId] = @id;";
        AddIdParameter(command, globalRecordId);
        return await command.ExecuteScalarAsync(ct) as string
            ?? throw new XunitException("The created record has no CaseReport1 row.");
    }

    // The held-page case's gate: an exclusive lock on the record's CaseReport1 row, held by the open transaction until
    // it is rolled back. The read changes no data.
    private static async Task LockPageOneRowAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string globalRecordId,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"SELECT [GlobalRecordId] FROM {CaseReportPageOneTable} WITH (XLOCK, ROWLOCK, HOLDLOCK) " +
            "WHERE [GlobalRecordId] = @id;";
        AddIdParameter(command, globalRecordId);
        Assert.True(
            await command.ExecuteScalarAsync(ct) is string,
            "The gate record has no CaseReport1 row to lock; no request was sent.");
    }

    // The modes of the granted key locks a session holds on one CaseReport1 row in this database.
    private static async Task<IReadOnlyList<string>> GrantedPageOneKeyModesAsync(
        SqlConnection monitor,
        int session,
        string lockResource,
        CancellationToken ct)
    {
        await using var command = monitor.CreateCommand();
        command.CommandText =
            "SELECT l.[request_mode] FROM sys.dm_tran_locks AS l " +
            "WHERE l.[request_session_id] = @session AND l.[resource_database_id] = DB_ID() " +
            "AND l.[resource_type] = @type AND l.[request_status] = @status " +
            "AND RTRIM(l.[resource_description]) = @resource " +
            "AND l.[resource_associated_entity_id] IN (SELECT p.[hobt_id] FROM sys.partitions AS p " +
            $"WHERE p.[object_id] = OBJECT_ID(N'{CaseReportPageOneTable}'));";
        command.Parameters.Add(new SqlParameter("@session", SqlDbType.Int) { Value = session });
        command.Parameters.Add(new SqlParameter("@type", SqlDbType.NVarChar, 60) { Value = KeyLockType });
        command.Parameters.Add(new SqlParameter("@status", SqlDbType.NVarChar, 60) { Value = GrantedLockStatus });
        command.Parameters.Add(new SqlParameter("@resource", SqlDbType.NVarChar, 256) { Value = lockResource });
        await using var reader = await command.ExecuteReaderAsync(ct);
        var modes = new List<string>();
        while (await reader.ReadAsync(ct))
        {
            modes.Add(reader.GetString(0));
        }

        return modes.AsReadOnly();
    }

    // Waits until a request in this database is blocked by the gate session on the gate's row, and returns its session.
    // The list answering first, or no such wait within BlockedRequestWait, fails the test.
    private static async Task<int> WaitForRequestBlockedByAsync(
        SqlConnection monitor,
        int gateSession,
        string gateResource,
        Task<HttpResponseMessage> listing,
        CancellationToken ct)
    {
        var waited = Stopwatch.StartNew();
        while (true)
        {
            await using (var command = monitor.CreateCommand())
            {
                command.CommandText =
                    "SELECT TOP (1) r.[session_id], r.[wait_resource] FROM sys.dm_exec_requests AS r " +
                    "WHERE r.[blocking_session_id] = @gate AND r.[database_id] = DB_ID();";
                command.Parameters.Add(new SqlParameter("@gate", SqlDbType.Int) { Value = gateSession });
                await using var reader = await command.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    var session = reader.GetInt16(0);
                    var waitResource = reader.GetString(1);
                    Assert.True(
                        waitResource.Contains(gateResource, StringComparison.Ordinal),
                        "The request the gate session blocks waits on another resource than the gate's page row.");
                    return session;
                }
            }

            if (listing.IsCompleted)
            {
                throw new XunitException(
                    "The list answered before it waited on the gate's page row, so its held locks were not observed.");
            }

            if (waited.Elapsed > BlockedRequestWait)
            {
                throw new XunitException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"No request waited on the gate's page row within {BlockedRequestWait.TotalSeconds} seconds."));
            }

            await Task.Delay(BlockedRequestPoll, ct);
        }
    }

    // Rolls the gate's transaction back, then disposes it and its connection. A failed rollback is returned, not
    // thrown, so it never replaces an assertion raised while the lock was held; closing the connection rolls back a
    // pending transaction, so the lock is released either way.
    private static async Task<string?> ReleaseGateAsync(SqlConnection connection, SqlTransaction? transaction)
    {
        string? failure = null;
        try
        {
            if (transaction is not null)
            {
                try
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                }
                catch (Exception exception) when (exception is SqlException or InvalidOperationException)
                {
                    failure = TestDatabase.DescribeFailure(exception);
                }
                finally
                {
                    await transaction.DisposeAsync();
                }
            }
        }
        finally
        {
            await connection.DisposeAsync();
        }

        return failure;
    }

    // The parameters of SetPredicate: the deleted toggle, and the active row state.
    private static void AddSetParameter(SqlCommand command, bool includeDeleted)
    {
        command.Parameters.Add(new SqlParameter("@all", SqlDbType.Bit) { Value = includeDeleted });
        AddActiveParameter(command);
    }

    private static void AddActiveParameter(SqlCommand command) =>
        command.Parameters.Add(new SqlParameter("@active", SqlDbType.SmallInt) { Value = ActiveRecStatus });

    private static void AddDeletedParameter(SqlCommand command) =>
        command.Parameters.Add(new SqlParameter("@deleted", SqlDbType.SmallInt) { Value = DeletedRecStatus });

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
    // Test-owned list defaults, never read from production code: a missing page is 1 and a missing pageSize is 50
    // (AAP 0.3.4.1).
    private const int DefaultPage = 1;

    private const int DefaultPageSize = 50;

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
            Assert.Equal(DefaultPage, NavigationApi.GetInt(page, "page"));
            Assert.Equal(DefaultPageSize, NavigationApi.GetInt(page, "pageSize"));
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
