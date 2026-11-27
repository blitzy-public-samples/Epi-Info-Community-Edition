using System.Data;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EpiInfo.Api.Tests.Infrastructure;
using EpiInfo.Domain.Forms;
using EpiInfo.Domain.Metadata;
using EpiInfo.Domain.Records;
using EpiInfo.Domain.Search;
using EpiInfo.Domain.Storage;
using EpiInfo.Domain.Validation;
using EpiInfo.Domain.Values;
using EpiInfo.Tests.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Sdk;

namespace EpiInfo.Api.Tests.Persistence;

/// <summary>
/// The write half of <c>SqlRecordRepositoryTests</c> in <c>EpiInfo.Data.Tests</c>: the statements the record
/// repository sends, in the order AAP 0.3.4.6 fixes, for an insert, a delta update, the legacy repair of a missing page
/// row (<c>Epi.Core/Data/Services/CollectedDataProvider.cs:1360-1380</c>), a soft delete and a refused update, each on
/// a record created through the API.
/// </summary>
/// <remarks>
/// <para>
/// Tests may write only records created through the API, and remove the page-two row of a record they created (AAP
/// 0.3.7, "Data reach of tests"). <c>EpiInfo.Data.Tests</c> references the data layer alone and cannot reach the API,
/// so these proofs live here: each record is created with <c>POST</c> on the API that
/// <see cref="GateObservingApiFactory.ForDatabaseAsync"/> hosts, the host <see cref="ApiFactory.ForDatabaseAsync"/>
/// builds, carrying a <see cref="TestDatabase.NewMarker"/> marker in <see cref="TestDatabase.MarkerField"/> and in no
/// other field, and every later write is a request on it, except where a case needs what only the store's interface
/// offers, its result or its stamp source: that case calls the host's own store, which every request of the host
/// reaches, on its own record. The records stay in the database, so a rerun needs no reset.
/// </para>
/// <para>
/// The required gate the API hands <see cref="IDataStore.UpdateAsync"/> is observed by a forwarding decorator over the
/// host's real store: <see cref="GateObservingStore"/> takes the place of the host's own <see cref="IDataStore"/>
/// registration, forwards every member to the store that registration builds, and wraps the gate so that each call is
/// counted, with the sequence of the last command the recorder had seen, before the API's gate runs; the gate's result
/// is returned unchanged. An eligible update runs the gate once, after its locked base read and its page reads and
/// before its first write; an update the locked read refuses never runs it.
/// </para>
/// <para>
/// The repository's statements are read from <see cref="ApiFactoryBase.Recorder"/>, the host's command observer. A
/// write's own commands are those that ran inside a transaction on the connection of its first command named for the
/// repository operation (<c>records.insert</c>, <c>records.update</c> or <c>records.delete</c>); the API's form,
/// metadata and permission reads before it run outside any transaction. The data layer's catalogue types are internal
/// to it, so table and column names are the seeded ones, written as literals, and the page order is read from
/// <c>GET /api/forms/{viewId}</c>.
/// </para>
/// <para>
/// Each test holds the exclusive <see cref="DatabaseWriteLock"/> for its whole body, so every snapshot diff holds
/// exactly its own changes, and sets <see cref="ApiFactoryBase.Clock"/> to whole seconds a minute apart before each
/// write, so every stamp is known and a changed <c>LastSaveTime</c> is asserted as changed. Status values are this
/// class's own 1 and 0, never the production rules' constants. Failure messages describe commands without parameter
/// values.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(DatabaseCollections.Database)]
public sealed class SqlRecordRepositoryWriteTests : IAsyncLifetime
{
    private const string CaseReport = "CaseReport";
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

    // The page-one Text field both seeded forms share beside the marker field, and FoodHistory's page-two Text field
    // (FoodHistory3.Lab, nvarchar(4000), no pattern); each column is named as its field (testdata/sql/03_forms.sql,
    // 05_data_tables.sql).
    private const string FirstNameField = "FirstName";
    private const string LabField = "Lab";

    // The fixed operation names the record repository reports to the command observer for its three writes, and the
    // prefix of the metadata reader's, whose form read each write also runs inside its transaction.
    private const string InsertOperation = "records.insert";
    private const string UpdateOperation = "records.update";
    private const string DeleteOperation = "records.delete";
    private const string MetadataOperationPrefix = "metadata.";

    private const short ActiveStatus = 1;
    private const short DeletedStatus = 0;

    // Covers a wait of up to 300 seconds for the test lock and the work behind it.
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(10);

    // Whole seconds a minute apart, so a datetime column holds each exactly and a changed LastSaveTime is a change.
    private static readonly DateTime FirstTime = ControlledTimeProvider.DefaultLocalStart;
    private static readonly DateTime SecondTime = FirstTime.AddMinutes(1);
    private static readonly DateTime ThirdTime = FirstTime.AddMinutes(2);
    private static readonly DateTime FourthTime = FirstTime.AddMinutes(3);

    private GateObservingApiFactory? _factory;
    private HttpClient? _client;

    /// <summary>Each seeded form with its page tables in <c>metaPages.Position</c> order.</summary>
    public static TheoryData<string, string[]> FormsWithPageTables => new()
    {
        { CaseReport, new[] { "CaseReport1" } },
        { FoodHistory, new[] { FoodHistoryPageOne, FoodHistoryPageTwo } },
    };

    private GateObservingApiFactory Factory =>
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
        _factory = await GateObservingApiFactory.ForDatabaseAsync();
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

    // AAP 0.3.4.6, insert: the base row first, returning its key through OUTPUT INSERTED.UniqueKey with FKEY never
    // named, then one row on every page in metaPages.Position order, a page with no submitted value included, all in
    // one transaction on one connection. One stamp fills both pairs, so no write is an UPDATE, and nothing else in the
    // five tables changes.
    [Theory]
    [MemberData(nameof(FormsWithPageTables))]
    public async Task InsertWritesBaseThenEveryPageInPositionOrderInOneTransaction(string formName, string[] pageTables)
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        CancellationToken ct = timeout.Token;
        await using DatabaseWriteLock dbLock = await DatabaseWriteLock.AcquireExclusiveAsync(ct);

        ApiForm form = await ReadFormAsync(formName, ct);
        Assert.Equal(pageTables, form.Pages.Select(page => page.Table));
        string marker = TestDatabase.NewMarker();

        Stamp(FirstTime);
        SqlSnapshot before = await SqlSnapshot.CaptureAsync(ct);
        Factory.Recorder.Clear();
        CreatedRecord created = await ApiTestClient.CreateRecordAsync(
            Client,
            form.ViewId,
            new Dictionary<string, object?> { [TestDatabase.MarkerField] = marker },
            ct);
        IReadOnlyList<RecordedCommand> writes = WritesOf(Factory.Recorder.Commands, InsertOperation);
        SqlSnapshot after = await SqlSnapshot.CaptureAsync(ct);
        string id = created.GlobalRecordId;

        Assert.True(
            writes.Count == 1 + form.Pages.Count,
            Describe("The insert did not send exactly one base and one page write per page.", writes));
        RecordedCommand baseInsert = writes[0];
        Assert.True(
            Verb(baseInsert) == "INSERT" && form.TargetTable(baseInsert) == form.BaseTable,
            Describe("The first write of the insert is not the base-table INSERT.", writes));
        Assert.Contains("OUTPUT INSERTED.", baseInsert.CommandText, StringComparison.Ordinal);
        IReadOnlyList<string> baseIdentifiers = SqlCommandRecorder.BracketedIdentifiers(baseInsert.CommandText);
        Assert.Contains(UniqueKeyColumn, baseIdentifiers);
        Assert.DoesNotContain(ForeignKeyColumn, baseIdentifiers);
        Assert.Equal(
            form.Pages.Select(page => ("INSERT", (string?)page.Table)),
            writes.Skip(1).Select(write => (Verb(write), form.TargetTable(write))));

        // The response is the active record holding the marker and no other value.
        JsonElement record = created.Record;
        Assert.Equal<int>(ActiveStatus, ReadInt32(record, "recStatus"));
        JsonElement values = ReadMember(record, "values", JsonValueKind.Object);
        Assert.Equal(
            form.DataFields.Order(StringComparer.Ordinal),
            values.EnumerateObject().Select(member => member.Name).Order(StringComparer.Ordinal));
        Assert.Equal(marker, ReadText(values, TestDatabase.MarkerField));
        Assert.All(
            values.EnumerateObject().Where(member => member.Name != TestDatabase.MarkerField),
            member => Assert.True(
                member.Value.ValueKind == JsonValueKind.Null,
                "A field the insert did not submit was returned with a value."));

        AssertCells(
            RowOf(after, form.BaseTable, id),
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [UniqueKeyColumn] = ReadInt32(record, "uniqueKey"),
                [RecStatusColumn] = ActiveStatus,
                [FirstSaveLogonNameColumn] = Environment.UserName,
                [FirstSaveTimeColumn] = FirstTime,
                [LastSaveLogonNameColumn] = Environment.UserName,
                [LastSaveTimeColumn] = FirstTime,
                [ForeignKeyColumn] = null,
            });

        // Every column of each page row but its key is NULL, the marker column alone excepted: read-only and
        // unsupported columns are never named, and the other writable columns are written NULL.
        foreach (ApiPage page in form.Pages)
        {
            bool holdsMarker = page.DataFields.Contains(TestDatabase.MarkerField, StringComparer.Ordinal);
            foreach ((string column, object? value) in RowOf(after, page.Table, id).Values)
            {
                object? expected = column == GlobalRecordIdColumn
                    ? id
                    : holdsMarker && column == TestDatabase.MarkerField ? marker : null;
                Assert.True(
                    SqlSnapshot.ValuesEqual(expected, value),
                    $"{page.Table}.{column} does not hold what the insert wrote: its key, the marker or NULL.");
            }
        }

        SnapshotDiff diff = SqlSnapshot.Compare(before, after);
        Assert.True(
            diff.RemovedRows.Count == 0 && diff.ChangedCells.Count == 0 && diff.SchemaChanges.Count == 0,
            diff.ToString());
        Assert.Equal(
            form.Tables.Order(StringComparer.Ordinal),
            diff.AddedRows.Select(row => row.Table).Order(StringComparer.Ordinal));
        Assert.True(diff.AddedRows.All(row => row.GlobalRecordId == id), diff.ToString());
    }

    // AAP 0.3.4.6, the missing page row: an update of a page-two field on a record whose page-two row is gone finds no
    // row, inserts the page row holding only its key and repeats the update, as legacy repairs it
    // (Epi.Core/Data/Services/CollectedDataProvider.cs:1360-1380), after the base stamp and without touching page one.
    // The required gate runs once, on the record as the locked read and the page read found it, before any write.
    // NavigationTests.MissingPageRow_IsStillPlacedAndReadWithNulls pins the reads of such a record.
    [Fact]
    public async Task UpdateInsertsMissingPageRowAndRepeatsUpdate()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        CancellationToken ct = timeout.Token;
        await using DatabaseWriteLock dbLock = await DatabaseWriteLock.AcquireExclusiveAsync(ct);

        ApiForm form = await ReadFormAsync(FoodHistory, ct);
        Assert.Equal(new[] { FoodHistoryPageOne, FoodHistoryPageTwo }, form.Pages.Select(page => page.Table));
        Assert.Equal(FoodHistoryPageTwo, form.PageHolding(LabField).Table);
        string marker = TestDatabase.NewMarker();
        Stamp(FirstTime);
        CreatedRecord created = await ApiTestClient.CreateRecordAsync(
            Client,
            form.ViewId,
            new Dictionary<string, object?> { [TestDatabase.MarkerField] = marker },
            ct);
        string id = created.GlobalRecordId;

        // The fixture AAP 0.3.7 allows: removing the page-two row of the record this test created.
        Assert.Equal(1, await DeletePageTwoRowAsync(id, ct));

        string lab = NewLabText();
        Stamp(SecondTime);
        SqlSnapshot before = await SqlSnapshot.CaptureAsync(ct);
        Factory.Recorder.Clear();
        Factory.ClearGateCalls();
        JsonElement updated = await PutAsync(
            RecordPath(form.ViewId, id),
            new Dictionary<string, object?> { [LabField] = lab },
            HttpStatusCode.OK,
            ct);
        IReadOnlyList<RecordedCommand> writes = WritesOf(Factory.Recorder.Commands, UpdateOperation);
        SqlSnapshot after = await SqlSnapshot.CaptureAsync(ct);

        AssertGateRanOnceBetweenReadsAndWrites(form, Factory.Recorder.Commands, Factory.GateCalls);
        Assert.True(
            writes.Count == 4 && Verb(writes[0]) == "UPDATE" && form.TargetTable(writes[0]) == form.BaseTable,
            Describe("The update did not send the base stamp first and three page writes.", writes));
        Assert.Equal(StampColumns, form.AssignedColumns(writes[0]));
        Assert.Equal(
            new[] { "UPDATE", "INSERT", "UPDATE" },
            writes.Where(write => form.TargetTable(write) == FoodHistoryPageTwo).Select(Verb));
        Assert.Equal(new[] { GlobalRecordIdColumn }, IdentifiersExcept(writes[2], FoodHistoryPageTwo));
        Assert.All(
            new[] { writes[1], writes[3] },
            update => Assert.Equal(new[] { LabField }, form.AssignedColumns(update)));
        Assert.True(
            !writes.Any(write => form.TargetTable(write) == FoodHistoryPageOne),
            Describe("The update wrote the page it submitted no field for.", writes));

        SnapshotDiff diff = SqlSnapshot.Compare(before, after);
        Assert.True(diff.RemovedRows.Count == 0 && diff.SchemaChanges.Count == 0, diff.ToString());
        Assert.True(diff.AddedRows.Count == 1, diff.ToString());
        SnapshotRowChange added = diff.AddedRows[0];
        Assert.Equal((FoodHistoryPageTwo, id), (added.Table, added.GlobalRecordId));
        foreach ((string column, object? value) in added.Row.Values)
        {
            object? expected = column == GlobalRecordIdColumn ? id : column == LabField ? lab : null;
            Assert.True(
                SqlSnapshot.ValuesEqual(expected, value),
                $"The repaired {FoodHistoryPageTwo} row does not hold only its key and the update: see {column}.");
        }

        Assert.Equal(
            new[] { new SnapshotCellChange(form.BaseTable, id, LastSaveTimeColumn, FirstTime, SecondTime) },
            diff.ChangedCells);

        JsonElement returned = ReadMember(updated, "values", JsonValueKind.Object);
        Assert.Equal(lab, ReadText(returned, LabField));
        Assert.Equal(marker, ReadText(returned, TestDatabase.MarkerField));
    }

    // AAP 0.3.4.6, update scope: a PUT is a delta, so the page holding the submitted field gets an UPDATE of that one
    // column, the base row only its last-save stamp, and every other column and page stays as it was. The required gate
    // runs once, after the locked read and the page reads and before the first write.
    [Fact]
    public async Task UpdateWritesOnlySubmittedColumns()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        CancellationToken ct = timeout.Token;
        await using DatabaseWriteLock dbLock = await DatabaseWriteLock.AcquireExclusiveAsync(ct);

        ApiForm form = await ReadFormAsync(FoodHistory, ct);
        ApiPage firstNamePage = form.PageHolding(FirstNameField);
        Assert.Equal(FoodHistoryPageOne, firstNamePage.Table);
        Stamp(FirstTime);
        CreatedRecord created = await ApiTestClient.CreateRecordAsync(
            Client,
            form.ViewId,
            new Dictionary<string, object?>
            {
                [TestDatabase.MarkerField] = TestDatabase.NewMarker(),
                [FirstNameField] = "F",
            },
            ct);
        string id = created.GlobalRecordId;

        Stamp(SecondTime);
        SqlSnapshot before = await SqlSnapshot.CaptureAsync(ct);
        Factory.Recorder.Clear();
        Factory.ClearGateCalls();
        await PutAsync(
            RecordPath(form.ViewId, id),
            new Dictionary<string, object?> { [FirstNameField] = "G" },
            HttpStatusCode.OK,
            ct);
        IReadOnlyList<RecordedCommand> writes = WritesOf(Factory.Recorder.Commands, UpdateOperation);
        SqlSnapshot after = await SqlSnapshot.CaptureAsync(ct);

        AssertGateRanOnceBetweenReadsAndWrites(form, Factory.Recorder.Commands, Factory.GateCalls);
        Assert.True(
            writes.Count == 2,
            Describe("The update did not send exactly the base stamp and one page write.", writes));
        Assert.Equal(("UPDATE", (string?)form.BaseTable), (Verb(writes[0]), form.TargetTable(writes[0])));
        Assert.Equal(StampColumns, form.AssignedColumns(writes[0]));
        Assert.Equal(("UPDATE", (string?)firstNamePage.Table), (Verb(writes[1]), form.TargetTable(writes[1])));
        Assert.Equal(new[] { FirstNameField }, form.AssignedColumns(writes[1]));
        Assert.All(
            form.Pages.Where(page => page.Table != firstNamePage.Table),
            page => Assert.True(
                !writes.Any(write => form.TargetTable(write) == page.Table),
                Describe("The update wrote a page it submitted no field for.", writes)));

        SnapshotDiff diff = SqlSnapshot.Compare(before, after);
        Assert.True(
            diff.AddedRows.Count == 0 && diff.RemovedRows.Count == 0 && diff.SchemaChanges.Count == 0,
            diff.ToString());
        Assert.Equal(
            new[]
            {
                new SnapshotCellChange(form.BaseTable, id, LastSaveTimeColumn, FirstTime, SecondTime),
                new SnapshotCellChange(firstNamePage.Table, id, FirstNameField, "F", "G"),
            }.OrderBy(cell => cell.Table, StringComparer.Ordinal),
            diff.ChangedCells.OrderBy(cell => cell.Table, StringComparer.Ordinal));

        // The logon name is the same identity on both saves, so the diff cannot show it; it is read directly.
        AssertCells(
            RowOf(after, form.BaseTable, id),
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [LastSaveLogonNameColumn] = Environment.UserName,
            });
    }

    // AAP 0.3.4.6 and C-6, soft delete: the locked base read, then one base UPDATE of RecStatus and the last-save
    // stamp; no page row is touched and nothing cascades. A repeated delete, or one of an unknown id, writes nothing.
    [Fact]
    public async Task SoftDeleteChangesOnlyBaseStatusAndStamps()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        CancellationToken ct = timeout.Token;
        await using DatabaseWriteLock dbLock = await DatabaseWriteLock.AcquireExclusiveAsync(ct);

        ApiForm form = await ReadFormAsync(FoodHistory, ct);
        Stamp(FirstTime);
        CreatedRecord created = await ApiTestClient.CreateRecordAsync(
            Client,
            form.ViewId,
            new Dictionary<string, object?>
            {
                [TestDatabase.MarkerField] = TestDatabase.NewMarker(),
                [LabField] = NewLabText(),
            },
            ct);
        string id = created.GlobalRecordId;
        string recordPath = RecordPath(form.ViewId, id);

        Stamp(SecondTime);
        SqlSnapshot before = await SqlSnapshot.CaptureAsync(ct);
        Factory.Recorder.Clear();
        await DeleteAsync(recordPath, HttpStatusCode.NoContent, ct);
        IReadOnlyList<RecordedCommand> transaction = TransactionOf(Factory.Recorder.Commands, DeleteOperation);
        SqlSnapshot afterDelete = await SqlSnapshot.CaptureAsync(ct);

        RecordedCommand[] writes = transaction.Where(command => command.IsWrite).ToArray();
        Assert.True(writes.Length == 1, Describe("The soft delete did not send exactly one write.", writes));
        RecordedCommand update = writes[0];
        Assert.Equal(("UPDATE", (string?)form.BaseTable), (Verb(update), form.TargetTable(update)));
        Assert.Equal(
            new[] { RecStatusColumn }.Concat(StampColumns).Order(StringComparer.Ordinal),
            form.AssignedColumns(update));
        Assert.True(
            transaction.Any(command =>
                command.Sequence < update.Sequence
                && Verb(command) == "SELECT"
                && form.TargetTable(command) == form.BaseTable
                && command.CommandText.Contains("UPDLOCK", StringComparison.Ordinal)),
            Describe("The soft delete did not lock the base row before its update.", transaction));

        // Two cells of the base row change; the page rows, both holding data, are left byte for byte as they were.
        SnapshotDiff diff = SqlSnapshot.Compare(before, afterDelete);
        Assert.True(
            diff.AddedRows.Count == 0 && diff.RemovedRows.Count == 0 && diff.SchemaChanges.Count == 0,
            diff.ToString());
        Assert.Equal(
            new[]
            {
                new SnapshotCellChange(form.BaseTable, id, RecStatusColumn, ActiveStatus, DeletedStatus),
                new SnapshotCellChange(form.BaseTable, id, LastSaveTimeColumn, FirstTime, SecondTime),
            }.OrderBy(cell => cell.Column, StringComparer.Ordinal),
            diff.ChangedCells.OrderBy(cell => cell.Column, StringComparer.Ordinal));

        // A second delete finds RecStatus 0, answers 204 and writes nothing, stamps included.
        Stamp(ThirdTime);
        Factory.Recorder.Clear();
        await DeleteAsync(recordPath, HttpStatusCode.NoContent, ct);
        Assert.True(
            Factory.Recorder.Writes().Count == 0,
            Describe("A repeated delete sent a write.", Factory.Recorder.Commands));
        SqlSnapshot afterRepeat = await SqlSnapshot.CaptureAsync(ct);
        SnapshotDiff repeatDiff = SqlSnapshot.Compare(afterDelete, afterRepeat);
        Assert.True(repeatDiff.IsEmpty, repeatDiff.ToString());
        AssertCells(
            RowOf(afterRepeat, form.BaseTable, id),
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [RecStatusColumn] = DeletedStatus,
                [LastSaveTimeColumn] = SecondTime,
            });

        // An id the base table does not hold is not found, and nothing is written.
        Stamp(FourthTime);
        Factory.Recorder.Clear();
        await DeleteAsync(RecordPath(form.ViewId, Guid.NewGuid().ToString()), HttpStatusCode.NotFound, ct);
        Assert.True(
            Factory.Recorder.Writes().Count == 0,
            Describe("A delete of an unknown id sent a write.", Factory.Recorder.Commands));
    }

    // The soft delete's result carries the GlobalRecordId its locked read returned, as stored, which the API's
    // post-commit log line names. Whether an upper-cased spelling finds the lower-case stored id is the column
    // collation's decision, which testdata/sql/00_create_database.sql leaves to the server, so SQL Server is asked on
    // the test's own connection with the locked read's predicate. Where it matches, the upper-cased delete is Deleted
    // with the stored spelling; where it does not, that delete is NotFound with nothing written, and the stored
    // spelling then deletes the record. Either way the base row keyed by the stored spelling is the one that changes,
    // a repeat in the same spelling finds it deleted and carries the stored spelling, and an unknown id carries none.
    // The record's id always holds a letter, so its upper-cased spelling always differs. The result can only be read
    // on the store, so this case calls the host's own store, the one every request of the host reaches, on a record
    // created through the API.
    [Fact]
    public async Task SoftDeleteResultCarriesTheStoredSpellingOfTheId()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        CancellationToken ct = timeout.Token;
        await using DatabaseWriteLock dbLock = await DatabaseWriteLock.AcquireExclusiveAsync(ct);

        ApiForm form = await ReadFormAsync(FoodHistory, ct);
        Stamp(FirstTime);
        string id = await CreateRecordWithALetterAsync(form.ViewId, ct);
        string upperCased = id.ToUpperInvariant();
        Assert.NotEqual(id, upperCased);
        bool upperCasedMatches = await MatchesStoredIdAsync(form.BaseTable, upperCased, id, ct);

        IDataStore store = Factory.Services.GetRequiredService<IDataStore>();
        FormMetadata metadata = await store.GetFormMetadataAsync(form.ViewId, ct)
            ?? throw new XunitException($"The store holds no form {form.BaseTable}.");

        SqlSnapshot before = await SqlSnapshot.CaptureAsync(ct);
        string deleteSpelling = upperCased;
        if (!upperCasedMatches)
        {
            // The collation tells the letter cases apart, so the upper-cased spelling names no row: the delete finds
            // nothing, carries no id and writes nothing.
            SoftDeleteResult missed = await store.SoftDeleteAsync(
                metadata,
                upperCased,
                () => new SaveStamp(Environment.UserName, SecondTime),
                ct);
            Assert.Equal(
                (SoftDeleteStatus.NotFound, false, (string?)null),
                (missed.Status, missed.IsFound, missed.GlobalRecordId));
            SnapshotDiff missedDiff = SqlSnapshot.Compare(before, await SqlSnapshot.CaptureAsync(ct));
            Assert.True(missedDiff.IsEmpty, missedDiff.ToString());
            deleteSpelling = id;
        }

        SoftDeleteResult deleted = await store.SoftDeleteAsync(
            metadata,
            deleteSpelling,
            () => new SaveStamp(Environment.UserName, SecondTime),
            ct);
        SqlSnapshot afterDelete = await SqlSnapshot.CaptureAsync(ct);

        Assert.Equal(
            (SoftDeleteStatus.Deleted, true, (string?)id),
            (deleted.Status, deleted.IsFound, deleted.GlobalRecordId));
        SnapshotDiff diff = SqlSnapshot.Compare(before, afterDelete);
        Assert.True(
            diff.AddedRows.Count == 0 && diff.RemovedRows.Count == 0 && diff.SchemaChanges.Count == 0,
            diff.ToString());
        Assert.Equal(
            new[]
            {
                new SnapshotCellChange(form.BaseTable, id, RecStatusColumn, ActiveStatus, DeletedStatus),
                new SnapshotCellChange(form.BaseTable, id, LastSaveTimeColumn, FirstTime, SecondTime),
            }.OrderBy(cell => cell.Column, StringComparer.Ordinal),
            diff.ChangedCells.OrderBy(cell => cell.Column, StringComparer.Ordinal));

        SoftDeleteResult repeated = await store.SoftDeleteAsync(
            metadata,
            deleteSpelling,
            () => new SaveStamp(Environment.UserName, ThirdTime),
            ct);
        Assert.Equal(
            (SoftDeleteStatus.AlreadyDeleted, true, (string?)id),
            (repeated.Status, repeated.IsFound, repeated.GlobalRecordId));

        SoftDeleteResult unknown = await store.SoftDeleteAsync(
            metadata,
            Guid.NewGuid().ToString().ToUpperInvariant(),
            () => new SaveStamp(Environment.UserName, FourthTime),
            ct);
        Assert.Equal(
            (SoftDeleteStatus.NotFound, false, (string?)null),
            (unknown.Status, unknown.IsFound, unknown.GlobalRecordId));

        // Neither the repeat nor the unknown id wrote anything, stamps included.
        SnapshotDiff unchanged = SqlSnapshot.Compare(afterDelete, await SqlSnapshot.CaptureAsync(ct));
        Assert.True(unchanged.IsEmpty, unchanged.ToString());
    }

    // AAP 0.3.4.6, update step 1: the locked read stops an update at RecStatus 0 (409) or at a missing base row (404),
    // before the page rows are read and the required gate runs on them, and before any write.
    [Fact]
    public async Task UpdateOnDeletedRecordReturnsDeletedAndWritesNothing()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        CancellationToken ct = timeout.Token;
        await using DatabaseWriteLock dbLock = await DatabaseWriteLock.AcquireExclusiveAsync(ct);

        ApiForm form = await ReadFormAsync(FoodHistory, ct);
        Stamp(FirstTime);
        CreatedRecord created = await ApiTestClient.CreateRecordAsync(
            Client,
            form.ViewId,
            new Dictionary<string, object?> { [TestDatabase.MarkerField] = TestDatabase.NewMarker() },
            ct);
        string id = created.GlobalRecordId;
        Stamp(SecondTime);
        await DeleteAsync(RecordPath(form.ViewId, id), HttpStatusCode.NoContent, ct);
        var submitted = new Dictionary<string, object?> { [FirstNameField] = "X" };

        Stamp(ThirdTime);
        SqlSnapshot before = await SqlSnapshot.CaptureAsync(ct);
        Factory.Recorder.Clear();
        Factory.ClearGateCalls();
        await PutAsync(RecordPath(form.ViewId, id), submitted, HttpStatusCode.Conflict, ct);
        AssertStoppedAtLockedRead(
            form,
            Factory.Recorder.Commands,
            Factory.GateCalls,
            "An update of a deleted record");

        Stamp(FourthTime);
        Factory.Recorder.Clear();
        Factory.ClearGateCalls();
        await PutAsync(RecordPath(form.ViewId, Guid.NewGuid().ToString()), submitted, HttpStatusCode.NotFound, ct);
        AssertStoppedAtLockedRead(
            form,
            Factory.Recorder.Commands,
            Factory.GateCalls,
            "An update of an unknown id");

        SnapshotDiff diff = SqlSnapshot.Compare(before, await SqlSnapshot.CaptureAsync(ct));
        Assert.True(diff.IsEmpty, diff.ToString());
    }

    // AAP 0.3.4.6 (save stamp, statement order) and 0.3.4.2: the store asks for the save stamp only once its locked
    // checks decide that the operation writes, inside the transaction and before its first data statement. A source
    // that throws or returns no stamp, on an active record, rolls the update and the soft delete back with nothing
    // written and reports NotWritten. An update the required gate refuses, an update of a deleted or unknown record,
    // and a soft delete of an already-deleted or unknown record never ask. A successful update and soft delete each
    // ask exactly once, after the locked read and the gate and before every write, and store that stamp. The source is
    // only reachable on the store, so this case calls the host's own store, the one every request of the host
    // reaches, on a record created through the API.
    [Fact]
    public async Task StoreAsksForTheStampOnlyOnceAWriteIsDecided()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        CancellationToken ct = timeout.Token;
        await using DatabaseWriteLock dbLock = await DatabaseWriteLock.AcquireExclusiveAsync(ct);

        ApiForm form = await ReadFormAsync(FoodHistory, ct);
        Stamp(FirstTime);
        CreatedRecord created = await ApiTestClient.CreateRecordAsync(
            Client,
            form.ViewId,
            new Dictionary<string, object?> { [TestDatabase.MarkerField] = TestDatabase.NewMarker() },
            ct);
        string id = created.GlobalRecordId;
        string unknownId = Guid.NewGuid().ToString();

        IDataStore store = Factory.Services.GetRequiredService<IDataStore>();
        FormMetadata metadata = await store.GetFormMetadataAsync(form.ViewId, ct)
            ?? throw new XunitException($"The store holds no form {form.BaseTable}.");
        var submitted = new Dictionary<string, FieldValue> { [FirstNameField] = FieldValue.FromText("Stamped") };
        Func<StoredRecord, RecordValidationResult> passes =
            _ => RecordValidationResult.Success(new Dictionary<string, FieldValue>());

        // Each call of a counting source records the sequence of the last command the recorder had seen, as the
        // gate's observer does, and returns a stamp at the given time.
        var stampCalls = new List<long>();
        Func<SaveStamp> CountingSource(DateTime time) => () =>
        {
            IReadOnlyList<RecordedCommand> commands = Factory.Recorder.Commands;
            stampCalls.Add(commands.Count == 0 ? 0 : commands[^1].Sequence);
            return new SaveStamp(Environment.UserName, time);
        };

        // A refusing source on the active record: asked once per call, and each call rolls back with nothing written.
        SqlSnapshot before = await SqlSnapshot.CaptureAsync(ct);
        Factory.Recorder.Clear();
        var refusal = new InvalidOperationException("The test's stamp source refuses.");
        var refusing = new (string Name, Func<SaveStamp> Source, Action<Exception?> AssertInner)[]
        {
            (
                "throws",
                () =>
                {
                    stampCalls.Add(-1);
                    throw refusal;
                },
                inner => Assert.Same(refusal, inner)),
            (
                "returns no stamp",
                () =>
                {
                    stampCalls.Add(-1);
                    return null!;
                },
                inner => Assert.IsType<InvalidOperationException>(inner)),
        };
        foreach ((string name, Func<SaveStamp> source, Action<Exception?> assertInner) in refusing)
        {
            stampCalls.Clear();
            DataStoreWriteException update = await Assert.ThrowsAsync<DataStoreWriteException>(
                () => store.UpdateAsync(metadata, id, submitted, source, passes, permissions: null, ct));
            DataStoreWriteException delete = await Assert.ThrowsAsync<DataStoreWriteException>(
                () => store.SoftDeleteAsync(metadata, id, source, ct));

            Assert.True(stampCalls.Count == 2, $"A source that {name} was not asked once per write.");
            Assert.Equal(
                (UpdateOperation, WriteOutcome.NotWritten, (int?)null),
                (update.Operation, update.Outcome, update.SqlErrorNumber));
            Assert.Equal(
                (DeleteOperation, WriteOutcome.NotWritten, (int?)null),
                (delete.Operation, delete.Outcome, delete.SqlErrorNumber));
            assertInner(update.InnerException);
            assertInner(delete.InnerException);
        }

        Assert.True(
            Factory.Recorder.Writes().Count == 0,
            Describe("A write whose stamp was refused sent a write.", Factory.Recorder.Commands));
        SnapshotDiff refusedDiff = SqlSnapshot.Compare(before, await SqlSnapshot.CaptureAsync(ct));
        Assert.True(refusedDiff.IsEmpty, refusedDiff.ToString());

        // An update the required gate refuses writes nothing, so it never asks.
        stampCalls.Clear();
        RecordWriteResult gated = await store.UpdateAsync(
            metadata,
            id,
            submitted,
            CountingSource(SecondTime),
            _ => RecordValidationResult.RequestFailure("The test's gate refuses the record."),
            permissions: null,
            ct);
        Assert.Equal((RecordWriteStatus.ValidationFailed, 0), (gated.Status, stampCalls.Count));

        // A successful update asks once, after the locked read and the gate and before its first write.
        stampCalls.Clear();
        Factory.Recorder.Clear();
        Factory.ClearGateCalls();
        RecordWriteResult updated = await store.UpdateAsync(
            metadata,
            id,
            submitted,
            CountingSource(SecondTime),
            passes,
            permissions: null,
            ct);
        Assert.Equal(RecordWriteStatus.Success, updated.Status);
        long atStamp = Assert.Single(stampCalls);
        long atGate = Assert.Single(Factory.GateCalls);
        IReadOnlyList<RecordedCommand> updateWrites = WritesOf(Factory.Recorder.Commands, UpdateOperation);
        Assert.True(
            atGate <= atStamp && updateWrites.Count > 0 && updateWrites.All(write => write.Sequence > atStamp),
            Describe(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The update asked for its stamp after command #{atStamp}."),
                Factory.Recorder.Commands));

        // A soft delete of the active record asks once, after its locked read and before its one write.
        stampCalls.Clear();
        Factory.Recorder.Clear();
        SoftDeleteResult deleted = await store.SoftDeleteAsync(metadata, id, CountingSource(ThirdTime), ct);
        Assert.Equal((SoftDeleteStatus.Deleted, (string?)id), (deleted.Status, deleted.GlobalRecordId));
        atStamp = Assert.Single(stampCalls);
        IReadOnlyList<RecordedCommand> deleteTransaction = TransactionOf(Factory.Recorder.Commands, DeleteOperation);
        RecordedCommand deleteWrite = Assert.Single(deleteTransaction, command => command.IsWrite);
        Assert.True(
            deleteTransaction.Any(command => !command.IsWrite && command.Sequence <= atStamp)
            && deleteWrite.Sequence > atStamp,
            Describe(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The delete asked for its stamp after command #{atStamp}."),
                deleteTransaction));

        // Both writes stored the stamp the source gave them.
        SqlSnapshot afterWrites = await SqlSnapshot.CaptureAsync(ct);
        AssertCells(
            RowOf(afterWrites, form.BaseTable, id),
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [RecStatusColumn] = DeletedStatus,
                [LastSaveLogonNameColumn] = Environment.UserName,
                [LastSaveTimeColumn] = ThirdTime,
            });
        AssertCells(
            RowOf(afterWrites, FoodHistoryPageOne, id),
            new Dictionary<string, object?>(StringComparer.Ordinal) { [FirstNameField] = "Stamped" });

        // The record is now deleted: an update of it, a repeated soft delete, and both writes of an unknown id write
        // nothing, so none asks.
        stampCalls.Clear();
        Factory.Recorder.Clear();
        RecordWriteResult updateOfDeleted = await store.UpdateAsync(
            metadata, id, submitted, CountingSource(FourthTime), passes, permissions: null, ct);
        SoftDeleteResult repeated = await store.SoftDeleteAsync(metadata, id, CountingSource(FourthTime), ct);
        RecordWriteResult updateOfUnknown = await store.UpdateAsync(
            metadata, unknownId, submitted, CountingSource(FourthTime), passes, permissions: null, ct);
        SoftDeleteResult deleteOfUnknown =
            await store.SoftDeleteAsync(metadata, unknownId, CountingSource(FourthTime), ct);

        Assert.Equal(
            (RecordWriteStatus.Deleted, SoftDeleteStatus.AlreadyDeleted, RecordWriteStatus.NotFound,
                SoftDeleteStatus.NotFound, 0),
            (updateOfDeleted.Status, repeated.Status, updateOfUnknown.Status, deleteOfUnknown.Status,
                stampCalls.Count));
        Assert.True(
            Factory.Recorder.Writes().Count == 0,
            Describe("A write the store answered without writing sent a write.", Factory.Recorder.Commands));
        SnapshotDiff unchanged = SqlSnapshot.Compare(afterWrites, await SqlSnapshot.CaptureAsync(ct));
        Assert.True(unchanged.IsEmpty, unchanged.ToString());
    }

    // The base-table last-save stamp columns an update writes, in ordinal order.
    private static string[] StampColumns { get; } =
        new[] { LastSaveLogonNameColumn, LastSaveTimeColumn }.Order(StringComparer.Ordinal).ToArray();

    // An update the locked read refused: it sent no write, the one repository command of its transaction is the locked
    // read of the base row, which names no page table, so no page row was read, and the required gate never ran.
    private static void AssertStoppedAtLockedRead(
        ApiForm form,
        IReadOnlyList<RecordedCommand> commands,
        IReadOnlyList<long> gateCalls,
        string update)
    {
        IReadOnlyList<RecordedCommand> transaction = TransactionOf(commands, UpdateOperation);
        Assert.True(commands.All(command => !command.IsWrite), Describe(update + " sent a write.", commands));
        RecordedCommand[] reads = transaction.Where(command => command.Operation == UpdateOperation).ToArray();
        Assert.True(
            reads.Length == 1
            && Verb(reads[0]) == "SELECT"
            && form.TargetTable(reads[0]) == form.BaseTable
            && reads[0].CommandText.Contains("UPDLOCK", StringComparison.Ordinal),
            Describe(update + " did not stop at the locked read of the base row.", transaction));
        IReadOnlyList<string> identifiers = SqlCommandRecorder.BracketedIdentifiers(reads[0].CommandText);
        Assert.True(
            !form.Pages.Any(page => identifiers.Contains(page.Table, StringComparer.Ordinal)),
            Describe(update + " read a page row.", transaction));
        Assert.True(
            gateCalls.Count == 0,
            Describe(
                string.Create(CultureInfo.InvariantCulture, $"{update} ran the required gate {gateCalls.Count} times."),
                transaction));
    }

    // An eligible update ran the required gate exactly once, in the order AAP 0.3.4.6 fixes: when it ran, the update's
    // transaction had sent the locked read of the base row and, after it, a read naming every page table, and no write
    // had started. Each gate call holds the sequence of the last command the recorder had seen, and a command takes its
    // sequence when it starts, so a write sent before the gate would hold a sequence at or below the gate's.
    private static void AssertGateRanOnceBetweenReadsAndWrites(
        ApiForm form,
        IReadOnlyList<RecordedCommand> commands,
        IReadOnlyList<long> gateCalls)
    {
        IReadOnlyList<RecordedCommand> transaction = TransactionOf(commands, UpdateOperation);
        Assert.True(
            gateCalls.Count == 1,
            Describe(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The update ran the required gate {gateCalls.Count} times, not once."),
                transaction));
        long atGate = gateCalls[0];
        string gatePoint = string.Create(CultureInfo.InvariantCulture, $" The gate ran after command #{atGate}.");
        RecordedCommand[] reads = transaction
            .Where(command => command.Operation == UpdateOperation && !command.IsWrite)
            .ToArray();
        RecordedCommand lockedRead = reads.FirstOrDefault(command =>
                Verb(command) == "SELECT"
                && form.TargetTable(command) == form.BaseTable
                && command.CommandText.Contains("UPDLOCK", StringComparison.Ordinal))
            ?? throw new XunitException(Describe("The update sent no locked read of the base row.", transaction));
        Assert.True(
            lockedRead.Sequence <= atGate,
            Describe("The required gate ran before the locked read of the base row." + gatePoint, transaction));
        Assert.All(
            form.Pages,
            page => Assert.True(
                reads.Any(read =>
                    read.Sequence > lockedRead.Sequence
                    && read.Sequence <= atGate
                    && SqlCommandRecorder.BracketedIdentifiers(read.CommandText)
                        .Contains(page.Table, StringComparer.Ordinal)),
                Describe(
                    $"The required gate ran before the locked update read {page.Table}." + gatePoint,
                    transaction)));
        Assert.True(
            commands.Where(command => command.IsWrite).All(write => write.Sequence > atGate),
            Describe("The required gate ran after a write of the update had started." + gatePoint, transaction));
    }

    // The commands of one write's transaction, in execution order: those that ran inside a transaction on the
    // connection of the first command named operation. Every command named operation, and every write the request
    // sent, must be one of them, and each must have completed successfully and be the repository's own or the form
    // read the repository runs inside its transaction.
    private static IReadOnlyList<RecordedCommand> TransactionOf(
        IReadOnlyList<RecordedCommand> commands,
        string operation)
    {
        RecordedCommand first = commands.FirstOrDefault(command => command.Operation == operation)
            ?? throw new XunitException(Describe($"The request sent no {operation} command.", commands));
        RecordedCommand[] transaction = commands
            .Where(command => command.ClientConnectionId == first.ClientConnectionId && command.InTransaction)
            .ToArray();
        Assert.True(
            commands
                .Where(command => command.Operation == operation || command.IsWrite)
                .All(command => transaction.Contains(command)),
            Describe("A command of the write ran on another connection or outside its transaction.", commands));
        Assert.True(
            transaction.All(command =>
                command.Completed
                && command.Succeeded
                && (command.Operation == operation
                    || command.Operation.StartsWith(MetadataOperationPrefix, StringComparison.Ordinal))),
            Describe("A command of the write's transaction failed or is not the write's own.", transaction));
        return transaction;
    }

    // The write statements of one write's transaction, in execution order.
    private static IReadOnlyList<RecordedCommand> WritesOf(IReadOnlyList<RecordedCommand> commands, string operation) =>
        TransactionOf(commands, operation).Where(command => command.IsWrite).ToArray();

    // An assertion message that lists commands without their parameter values.
    private static string Describe(string message, IReadOnlyList<RecordedCommand> commands) =>
        commands.Count == 0
            ? message + " No command was recorded."
            : message + Environment.NewLine + string.Join(
                Environment.NewLine,
                commands.Select(command => command.Describe(includeParameterValues: false)));

    // The leading keyword of a command's text, upper-cased: INSERT, UPDATE, SELECT, WITH or IF.
    private static string Verb(RecordedCommand command)
    {
        ReadOnlySpan<char> text = command.CommandText.AsSpan().TrimStart();
        int length = 0;
        while (length < text.Length && char.IsAsciiLetter(text[length]))
        {
            length++;
        }

        return text[..length].ToString().ToUpperInvariant();
    }

    // The distinct bracketed identifiers of a command other than the excluded names, in ordinal order.
    private static string[] IdentifiersExcept(RecordedCommand command, params string[] excluded) =>
        SqlCommandRecorder.BracketedIdentifiers(command.CommandText)
            .Where(name => !excluded.Contains(name, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    // Resolves the form's view id through GET /api/forms, then reads its pages from GET /api/forms/{viewId} in
    // metaPages.Position order, then PageId, each with its table, the base table name followed by the PageId as legacy
    // names page tables, and its data fields: every field whose type is neither label, group nor unsupported. The base
    // table of each seeded form is named as the form.
    private async Task<ApiForm> ReadFormAsync(string formName, CancellationToken ct)
    {
        int viewId = await ApiTestClient.GetViewIdAsync(Client, formName, ct);
        string path = string.Create(CultureInfo.InvariantCulture, $"/api/forms/{viewId}");
        JsonElement definition;
        using (HttpResponseMessage response = await Client.GetAsync(path, ct))
        {
            AssertStatus(response, HttpStatusCode.OK, "GET " + path);
            definition = await ApiTestClient.ReadJsonElementAsync(response, ct);
        }

        ApiPage[] pages = ReadMember(definition, "pages", JsonValueKind.Array)
            .EnumerateArray()
            .Select(page => (
                Position: ReadInt32(page, "position"),
                PageId: ReadInt32(page, "pageId"),
                Fields: ReadMember(page, "fields", JsonValueKind.Array)))
            .OrderBy(page => page.Position)
            .ThenBy(page => page.PageId)
            .Select(page => new ApiPage(
                formName + page.PageId.ToString(CultureInfo.InvariantCulture),
                page.Fields.EnumerateArray()
                    .Where(field => ReadText(field, "fieldType") is not ("label" or "group" or "unsupported"))
                    .Select(field => ReadText(field, "name"))
                    .ToArray()))
            .ToArray();
        Assert.True(pages.Length > 0, $"GET {path} lists no page.");
        return new ApiForm(viewId, formName, pages);
    }

    // Sends PUT {"values": values} and asserts its status; returns the body of a 200, or an undefined element.
    private async Task<JsonElement> PutAsync(
        string path,
        IDictionary<string, object?> values,
        HttpStatusCode expected,
        CancellationToken ct)
    {
        using HttpResponseMessage response = await ApiTestClient.PutJsonAsync(Client, path, new { values }, ct);
        AssertStatus(response, expected, "PUT " + path);
        return expected == HttpStatusCode.OK ? await ApiTestClient.ReadJsonElementAsync(response, ct) : default;
    }

    private async Task DeleteAsync(string path, HttpStatusCode expected, CancellationToken ct)
    {
        using HttpResponseMessage response = await Client.DeleteAsync(path, ct);
        AssertStatus(response, expected, "DELETE " + path);
    }

    // Sets the host clock the save stamps are read from; every time this class sets is a whole second.
    private void Stamp(DateTime localTime) => Factory.Clock.SetLocalNow(localTime);

    // Creates a record through POST carrying a new marker and returns its id, creating another while the generated id
    // holds no letter, so the id always has a spelling in the other letter case. A version-4 GUID string lacks a letter
    // only when every hex digit but its fixed version digit is a decimal digit, about once in 2.7 million ids, so even
    // a second attempt is rare. Every record created carries its own marker and stays in the database, as the class's
    // records do.
    private async Task<string> CreateRecordWithALetterAsync(int viewId, CancellationToken ct)
    {
        const int Attempts = 8;
        for (int attempt = 0; attempt < Attempts; attempt++)
        {
            CreatedRecord created = await ApiTestClient.CreateRecordAsync(
                Client,
                viewId,
                new Dictionary<string, object?> { [TestDatabase.MarkerField] = TestDatabase.NewMarker() },
                ct);
            if (created.GlobalRecordId.Any(char.IsAsciiLetter))
            {
                return created.GlobalRecordId;
            }
        }

        throw new XunitException(string.Create(
            CultureInfo.InvariantCulture,
            $"{Attempts} records created through the API all have an id without a letter."));
    }

    // Whether spelling finds the stored id under the base column's own collation, by the predicate of the store's
    // locked read (RecordSqlBuilder.LockBase): equal text and equal byte length, with spelling bound as nvarchar(max),
    // as the store binds it. Ids are unique GUID strings, so the only row it may find is the test's own record; any
    // other row fails the test.
    private static async Task<bool> MatchesStoredIdAsync(
        string baseTable,
        string spelling,
        string storedId,
        CancellationToken ct)
    {
        await using SqlConnection connection = await TestDatabase.OpenConnectionAsync(ct);
        await using var command = new SqlCommand(
            $"SELECT [{GlobalRecordIdColumn}] FROM [dbo].[{baseTable}] WHERE [{GlobalRecordIdColumn}] = @spelling "
                + $"AND DATALENGTH([{GlobalRecordIdColumn}]) = DATALENGTH(@spelling);",
            connection);
        command.Parameters.Add(new SqlParameter("@spelling", SqlDbType.NVarChar, -1) { Value = spelling });

        var matched = new List<string>();
        await using (SqlDataReader reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                matched.Add(reader.GetString(0));
            }
        }

        Assert.True(
            matched.Count == 0 || (matched.Count == 1 && string.Equals(matched[0], storedId, StringComparison.Ordinal)),
            string.Create(
                CultureInfo.InvariantCulture,
                $"The spelling matched {matched.Count} rows of {baseTable} that are not only the test's record."));
        return matched.Count == 1;
    }

    // The page-two row of a record the test created, removed by the id bound as a typed parameter; returns the rows
    // removed.
    private static async Task<int> DeletePageTwoRowAsync(string globalRecordId, CancellationToken ct)
    {
        await using SqlConnection connection = await TestDatabase.OpenConnectionAsync(ct);
        await using var command = new SqlCommand(
            $"DELETE FROM [dbo].[{FoodHistoryPageTwo}] WHERE [{GlobalRecordIdColumn}] = @globalRecordId;",
            connection);
        command.Parameters.Add(new SqlParameter("@globalRecordId", SqlDbType.NVarChar, 255) { Value = globalRecordId });
        return await command.ExecuteNonQueryAsync(ct);
    }

    // A Lab value independent of the marker, which only LastName carries.
    private static string NewLabText() => "lab-" + Guid.NewGuid().ToString("N");

    private static string RecordPath(int viewId, string globalRecordId) =>
        string.Create(CultureInfo.InvariantCulture, $"/api/forms/{viewId}/records/{globalRecordId}");

    // The response has the expected status; the message names both statuses and never quotes the body.
    private static void AssertStatus(HttpResponseMessage response, HttpStatusCode expected, string request) =>
        Assert.True(
            response.StatusCode == expected,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{request} returned {(int)response.StatusCode} {response.StatusCode}, expected {(int)expected} " +
                $"{expected}."));

    private static SnapshotRow RowOf(SqlSnapshot snapshot, string table, string globalRecordId) =>
        snapshot.Row(table, globalRecordId)
            ?? throw new XunitException($"{table} holds no row for GlobalRecordId {globalRecordId}.");

    // Each named column of the row holds exactly its expected typed value.
    private static void AssertCells(SnapshotRow row, IReadOnlyDictionary<string, object?> expected)
    {
        foreach ((string column, object? value) in expected)
        {
            Assert.True(
                row.Values.TryGetValue(column, out object? actual) && SqlSnapshot.ValuesEqual(value, actual),
                $"{row.Table}.{column} of {row.GlobalRecordId} does not hold {SqlSnapshot.FormatValue(value)}.");
        }
    }

    // The member of a JSON object, which must exist with the given kind; the message names the member, never a value.
    private static JsonElement ReadMember(JsonElement element, string name, JsonValueKind kind)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(name, out JsonElement member)
            || member.ValueKind != kind)
        {
            throw new XunitException($"The response has no {kind} member '{name}'.");
        }

        return member;
    }

    private static int ReadInt32(JsonElement element, string name) =>
        ReadMember(element, name, JsonValueKind.Number).TryGetInt32(out int value)
            ? value
            : throw new XunitException($"The response member '{name}' is not a 32-bit integer.");

    private static string ReadText(JsonElement element, string name) =>
        ReadMember(element, name, JsonValueKind.String).GetString()
            ?? throw new XunitException($"The response member '{name}' is not a string.");

    /// <summary>A seeded form as <c>GET /api/forms/{viewId}</c> describes it.</summary>
    /// <param name="ViewId">The form's view id.</param>
    /// <param name="BaseTable">The base table.</param>
    /// <param name="Pages">Every page, in <c>metaPages.Position</c> order.</param>
    private sealed record ApiForm(int ViewId, string BaseTable, IReadOnlyList<ApiPage> Pages)
    {
        /// <summary>The base table, then every page table in page order.</summary>
        public IReadOnlyList<string> Tables => [BaseTable, .. Pages.Select(page => page.Table)];

        /// <summary>The names of every data field, page by page.</summary>
        public IEnumerable<string> DataFields => Pages.SelectMany(page => page.DataFields);

        /// <summary>The one page that holds the data field <paramref name="field"/>.</summary>
        public ApiPage PageHolding(string field)
        {
            ApiPage[] pages = Pages.Where(page => page.DataFields.Contains(field, StringComparer.Ordinal)).ToArray();
            Assert.True(pages.Length == 1, $"The form {BaseTable} does not hold exactly one data field {field}.");
            return pages[0];
        }

        /// <summary>
        /// The first bracketed identifier of a command that names a table of the form; <see langword="null"/> for a
        /// command that names none, such as a metadata read.
        /// </summary>
        public string? TargetTable(RecordedCommand command) =>
            SqlCommandRecorder.BracketedIdentifiers(command.CommandText)
                .FirstOrDefault(name => Tables.Contains(name, StringComparer.Ordinal));

        /// <summary>
        /// The columns a base or page write assigns: its identifiers other than the form's tables and the key columns
        /// <c>GlobalRecordId</c> and <c>UniqueKey</c>, in ordinal order.
        /// </summary>
        public string[] AssignedColumns(RecordedCommand command) =>
            IdentifiersExcept(command, [.. Tables, GlobalRecordIdColumn, UniqueKeyColumn]);
    }

    /// <summary>One page of a seeded form.</summary>
    /// <param name="Table">The page table.</param>
    /// <param name="DataFields">The names of the page's data fields, each also its column name.</param>
    private sealed record ApiPage(string Table, IReadOnlyList<string> DataFields);

    /// <summary>
    /// The database host <see cref="ApiFactory.ForDatabaseAsync"/> builds, on the same pinned connection and with the
    /// same doubles, whose <see cref="IDataStore"/> is the host's own registration behind a
    /// <see cref="GateObservingStore"/> that records each call of the required gate.
    /// </summary>
    /// <remarks>
    /// The host's own registration is moved, as it is, under a private service key, so the container builds, shares
    /// and disposes the real store exactly as it would have, and the decorator takes its place with its lifetime. The
    /// real store's type is never named: it is whatever the API registers, built on the base host's
    /// <see cref="ApiFactoryBase.Recorder"/>, so every statement it sends is still recorded. The store is one instance
    /// serving every request of the host, so gate calls are recorded under a lock.
    /// </remarks>
    private sealed class GateObservingApiFactory : ApiFactoryBase
    {
        // The service key the host's own IDataStore registration is moved to; only the decorator resolves it.
        private const string RealStoreKey = nameof(SqlRecordRepositoryWriteTests) + ".RealDataStore";

        private readonly Lock _gateCallsLock = new();
        private readonly List<long> _gateCalls = [];

        private GateObservingApiFactory(DatabaseHost host)
            : base(host)
        {
        }

        /// <summary>
        /// One entry per required-gate call since the host started or <see cref="ClearGateCalls"/> last ran, in call
        /// order: the <see cref="RecordedCommand.Sequence"/> of the last command <see cref="ApiFactoryBase.Recorder"/>
        /// held when the gate was called, or 0 when it held none. Each read is a new copy.
        /// </summary>
        public IReadOnlyList<long> GateCalls
        {
            get
            {
                lock (_gateCallsLock)
                {
                    return _gateCalls.ToArray();
                }
            }
        }

        /// <summary>
        /// Creates the factory as <see cref="ApiFactory.ForDatabaseAsync"/> does with default options: the rehearsal
        /// database is proved reachable first, and the host connects with the connection string that probe pinned.
        /// </summary>
        /// <returns>The factory, not yet started.</returns>
        /// <exception cref="TestDatabaseUnavailableException">
        /// The database is not configured or cannot be reached; the test fails, and never skips.
        /// </exception>
        public static async Task<GateObservingApiFactory> ForDatabaseAsync()
        {
            DatabaseHost host = await DatabaseHost.PrepareAsync(options: null).ConfigureAwait(false);
            return new GateObservingApiFactory(host);
        }

        /// <summary>Forgets every recorded gate call, so a test counts only the calls of its next request.</summary>
        public void ClearGateCalls()
        {
            lock (_gateCallsLock)
            {
                _gateCalls.Clear();
            }
        }

        /// <inheritdoc />
        protected override void ConfigureHost(IWebHostBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            base.ConfigureHost(builder);

            // Runs after Program.cs and after the base host's ConfigureTestServices, so the registration found here is
            // the production one, and the observer it is built on is already the recorder.
            builder.ConfigureTestServices(services =>
            {
                ServiceDescriptor[] registrations = services
                    .Where(descriptor => descriptor.ServiceType == typeof(IDataStore) && !descriptor.IsKeyedService)
                    .ToArray();
                if (registrations.Length != 1)
                {
                    throw new InvalidOperationException(string.Create(
                        CultureInfo.InvariantCulture,
                        $"The host registers {registrations.Length} {nameof(IDataStore)} services, not exactly one."));
                }

                ServiceDescriptor real = registrations[0];
                services.Remove(real);
                services.Add(UnderRealStoreKey(real));
                services.Add(new ServiceDescriptor(
                    typeof(IDataStore),
                    provider => new GateObservingStore(
                        provider.GetRequiredKeyedService<IDataStore>(RealStoreKey),
                        Observe),
                    real.Lifetime));
            });
        }

        // The host's own registration, unchanged but for its service key: the same instance, factory or type, with
        // the same lifetime.
        private static ServiceDescriptor UnderRealStoreKey(ServiceDescriptor real) =>
            real.ImplementationInstance is { } instance
                ? new ServiceDescriptor(typeof(IDataStore), RealStoreKey, instance)
                : real.ImplementationFactory is { } factory
                    ? new ServiceDescriptor(
                        typeof(IDataStore),
                        RealStoreKey,
                        (provider, _) => factory(provider),
                        real.Lifetime)
                    : new ServiceDescriptor(
                        typeof(IDataStore),
                        RealStoreKey,
                        real.ImplementationType
                            ?? throw new InvalidOperationException(
                                $"The host's {nameof(IDataStore)} registration names no implementation."),
                        real.Lifetime);

        // The API's gate, wrapped: each call records the recorder's last sequence, then runs the API's gate on the
        // same record and returns its result, or lets its exception propagate, unchanged. A recorder that failed to
        // record a command throws here, which the store reports as a gate exception and the API as a 500, so the
        // update fails rather than pass on an incomplete record of its commands.
        private Func<StoredRecord, RecordValidationResult> Observe(Func<StoredRecord, RecordValidationResult> gate) =>
            stored =>
            {
                IReadOnlyList<RecordedCommand> commands = Recorder.Commands;
                long sequence = commands.Count == 0 ? 0 : commands[^1].Sequence;
                lock (_gateCallsLock)
                {
                    _gateCalls.Add(sequence);
                }

                return gate(stored);
            };
    }

    /// <summary>
    /// An <see cref="IDataStore"/> that forwards every member to the host's real store, handing
    /// <see cref="UpdateAsync"/> the API's required gate wrapped by the observer it was given.
    /// </summary>
    /// <remarks>
    /// Nothing is cached, altered or reordered: every argument but the gate reaches the real store as given, and its
    /// result or exception reaches the API as the real store produced it. The wrapped gate runs where and when the
    /// real store calls it.
    /// </remarks>
    /// <param name="real">The store every call is forwarded to.</param>
    /// <param name="observe">Wraps the API's required gate so that each of its calls is recorded.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    private sealed class GateObservingStore(
        IDataStore real,
        Func<Func<StoredRecord, RecordValidationResult>, Func<StoredRecord, RecordValidationResult>> observe)
        : IDataStore
    {
        private readonly IDataStore _real = real ?? throw new ArgumentNullException(nameof(real));

        private readonly Func<Func<StoredRecord, RecordValidationResult>, Func<StoredRecord, RecordValidationResult>>
            _observe = observe ?? throw new ArgumentNullException(nameof(observe));

        /// <inheritdoc />
        public Task<IReadOnlyList<FormSummary>> ListFormsAsync(CancellationToken cancellationToken) =>
            _real.ListFormsAsync(cancellationToken);

        /// <inheritdoc />
        public Task<FormMetadata?> GetFormMetadataAsync(int viewId, CancellationToken cancellationToken) =>
            _real.GetFormMetadataAsync(viewId, cancellationToken);

        /// <inheritdoc />
        public Task<IReadOnlyDictionary<string, IReadOnlyList<FieldOption>>> GetOptionsAsync(
            FormMetadata form,
            CancellationToken cancellationToken) =>
            _real.GetOptionsAsync(form, cancellationToken);

        /// <inheritdoc />
        public Task<string?> GetCheckCodeAsync(int viewId, CancellationToken cancellationToken) =>
            _real.GetCheckCodeAsync(viewId, cancellationToken);

        /// <inheritdoc />
        public Task<int> CountAsync(FormMetadata form, bool includeDeleted, CancellationToken cancellationToken) =>
            _real.CountAsync(form, includeDeleted, cancellationToken);

        /// <inheritdoc />
        public Task<RecordDto?> GetAtPositionAsync(
            FormMetadata form,
            int position,
            bool includeDeleted,
            CancellationToken cancellationToken) =>
            _real.GetAtPositionAsync(form, position, includeDeleted, cancellationToken);

        /// <inheritdoc />
        public Task<RecordDto?> GetByIdAsync(
            FormMetadata form,
            string globalRecordId,
            CancellationToken cancellationToken) =>
            _real.GetByIdAsync(form, globalRecordId, cancellationToken);

        /// <inheritdoc />
        public Task<NavigationPosition?> GetSetPositionAsync(
            FormMetadata form,
            string globalRecordId,
            bool includeDeleted,
            CancellationToken cancellationToken) =>
            _real.GetSetPositionAsync(form, globalRecordId, includeDeleted, cancellationToken);

        /// <inheritdoc />
        public Task<RecordPage> ListAsync(
            FormMetadata form,
            RecordListQuery query,
            CancellationToken cancellationToken) =>
            _real.ListAsync(form, query, cancellationToken);

        /// <inheritdoc />
        public Task<SearchResponse> SearchAsync(
            FormMetadata form,
            IReadOnlyList<SearchCondition> conditions,
            bool includeDeleted,
            string? excludeGlobalRecordId,
            CancellationToken cancellationToken) =>
            _real.SearchAsync(form, conditions, includeDeleted, excludeGlobalRecordId, cancellationToken);

        /// <inheritdoc />
        public Task<bool> PingAsync(CancellationToken cancellationToken) => _real.PingAsync(cancellationToken);

        /// <inheritdoc />
        public Task<RecordDto> InsertAsync(
            FormMetadata form,
            IReadOnlyDictionary<string, FieldValue> values,
            SaveStamp stamp,
            CancellationToken cancellationToken) =>
            _real.InsertAsync(form, values, stamp, cancellationToken);

        /// <inheritdoc />
        /// <remarks>
        /// The real store receives <paramref name="requiredGate"/> wrapped by the observer. A missing gate is refused
        /// with the <see cref="ArgumentNullException"/> the real store raises for one, since there is nothing to wrap.
        /// </remarks>
        public Task<RecordWriteResult> UpdateAsync(
            FormMetadata form,
            string globalRecordId,
            IReadOnlyDictionary<string, FieldValue> submitted,
            Func<SaveStamp> createStamp,
            Func<StoredRecord, RecordValidationResult> requiredGate,
            FormPermissions? permissions,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(requiredGate);
            return _real.UpdateAsync(
                form,
                globalRecordId,
                submitted,
                createStamp,
                _observe(requiredGate),
                permissions,
                cancellationToken);
        }

        /// <inheritdoc />
        public Task<SoftDeleteResult> SoftDeleteAsync(
            FormMetadata form,
            string globalRecordId,
            Func<SaveStamp> createStamp,
            CancellationToken cancellationToken) =>
            _real.SoftDeleteAsync(form, globalRecordId, createStamp, cancellationToken);

        /// <inheritdoc />
        public Task<FormPermissions?> GetPermissionsAsync(FormMetadata form, CancellationToken cancellationToken) =>
            _real.GetPermissionsAsync(form, cancellationToken);
    }
}

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

/// <summary>
/// Holds the numbers <see cref="FakeDataStore"/> stores to what SQL Server's columns hold and the SQL store reads back,
/// so the API tests that run on the double see the values the stored-data contract produces. A <c>float</c> column
/// holds a binary64 double, which the SQL store binds as <see cref="DbType.Double"/> and reads back as a double, so a
/// seed, an insert and an update each store a number as its nearest double; an integer column holds the exact integer,
/// beyond 2^53 included.
/// </summary>
/// <remarks>
/// The class needs no database: it carries no <c>Integration</c> trait, joins no database collection and takes no
/// lock. Its form is synthetic: view 9120 on the unseeded base table <c>FakeStoredValue</c>, with the number
/// <c>Weight</c> on a <c>float</c> column and the number <c>Count</c> on a <c>bigint</c> column on one page. Each
/// number is parsed exactly, as the API reads a JSON number, so <c>9007199254740993</c> keeps its exact integer and
/// <c>2.0000000000000001</c> holds none until a float column stores it. The API cases host the production pipeline
/// over the double through <see cref="ApiFactoryOptions.ForFake"/> and read the number the response's JSON carries.
/// </remarks>
public sealed class FakeDataStoreStoredNumberTests
{
    private const int ViewId = 9120;
    private const int PageId = 912001;
    private const string BaseTable = "FakeStoredValue";
    private const string WeightField = "Weight";
    private const string CountField = "Count";

    private static readonly FormMetadata Form = CreateForm();

    private static readonly SaveStamp Stamp = new("tester", new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc));

    // The form has no required field, so its required gate always passes.
    private static readonly Func<StoredRecord, RecordValidationResult> Pass =
        _ => RecordValidationResult.Success(new Dictionary<string, FieldValue>());

    /// <summary>How a number reaches the store.</summary>
    public enum Write
    {
        /// <summary><see cref="FakeDataStore.SeedRecord"/>.</summary>
        Seed,

        /// <summary><see cref="FakeDataStore.InsertAsync"/>, or <c>POST</c> through the API.</summary>
        Insert,

        /// <summary>
        /// <see cref="FakeDataStore.UpdateAsync"/> of a seeded record whose fields are null, or <c>PUT</c> through the
        /// API.
        /// </summary>
        Update,
    }

    /// <summary>
    /// Each write, a number field, the number the write carries and the number its column then holds.
    /// </summary>
    public static TheoryData<Write, string, string, string> StoredNumbers
    {
        get
        {
            var cases = new TheoryData<Write, string, string, string>();
            foreach (var write in Enum.GetValues<Write>())
            {
                // 2^53 + 1 is not a double, so a float column holds its nearest double, 2^53, which it keeps as is.
                // 2.0000000000000001 holds no integer, yet its nearest double is the whole 2.
                cases.Add(write, WeightField, "9007199254740993", "9007199254740992");
                cases.Add(write, WeightField, "9007199254740992", "9007199254740992");
                cases.Add(write, WeightField, "2.0000000000000001", "2");

                // A bigint column holds every Int64 exactly, 2^53 + 1 and both range limits included.
                cases.Add(write, CountField, "9007199254740993", "9007199254740993");
                cases.Add(write, CountField, "9223372036854775807", "9223372036854775807");
                cases.Add(write, CountField, "-9223372036854775808", "-9223372036854775808");
            }

            return cases;
        }
    }

    // The record the write returns, the record read by id, the inspection snapshot and the stored record a later
    // update's required gate is handed all hold the number the column holds, as its exact integer.
    [Theory]
    [MemberData(nameof(StoredNumbers))]
    public async Task EachWriteStoresTheNumberItsColumnHolds(Write write, string field, string number, string stored)
    {
        var store = CreateStore();
        var expected = Number(stored);
        var expectedInteger = long.Parse(stored, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);

        var (id, returned) = await WriteAsync(store, write, field, Number(number));
        var read = Assert.IsType<RecordDto>(await store.GetByIdAsync(Form, id, CancellationToken.None));
        var state = Assert.IsType<FakeRecordState>(store.GetRecord(ViewId, id));
        var gated = await GatedRecordAsync(store, id);

        var values = new List<FieldValue> { read.Values[field], state.Values[field], gated.Values[field] };
        if (returned is not null)
        {
            values.Add(returned.Values[field]);
        }

        Assert.All(values, value =>
        {
            Assert.Equal(expected, value);
            Assert.True(value.TryGetInteger(out var integer));
            Assert.Equal(expectedInteger, integer);
        });
    }

    // The API answers a write with the stored record, so its JSON carries the number the column holds, as the SQL
    // store reads it back: 2^53 + 1 on the float Weight answers 2^53, and on the bigint Count itself.
    [Theory]
    [InlineData(Write.Insert, WeightField, "9007199254740993", "9007199254740992")]
    [InlineData(Write.Insert, WeightField, "2.0000000000000001", "2")]
    [InlineData(Write.Insert, CountField, "9007199254740993", "9007199254740993")]
    [InlineData(Write.Update, WeightField, "9007199254740993", "9007199254740992")]
    [InlineData(Write.Update, CountField, "9007199254740993", "9007199254740993")]
    public async Task ApiAnswersWithTheNumberItsColumnHolds(Write write, string field, string number, string json)
    {
        var store = CreateStore();
        await using var factory = new ApiFactory(ApiFactoryOptions.ForFake(store));
        using var client = factory.CreateClient();
        var records = string.Create(CultureInfo.InvariantCulture, $"/api/forms/{ViewId}/records");

        // The number goes on the wire as written, which no double could carry.
        using var body = JsonDocument.Parse("{\"values\":{\"" + field + "\":" + number + "}}");
        using var response = write switch
        {
            Write.Insert => await ApiTestClient.PostJsonAsync(client, records, body.RootElement),
            Write.Update => await ApiTestClient.PutJsonAsync(
                client, records + "/" + Uri.EscapeDataString(store.SeedRecord(ViewId)), body.RootElement),
            _ => throw new ArgumentOutOfRangeException(nameof(write), write, "The API writes only by POST and PUT."),
        };

        Assert.Equal(write == Write.Insert ? HttpStatusCode.Created : HttpStatusCode.OK, response.StatusCode);
        var record = await ApiTestClient.ReadJsonElementAsync(response);
        Assert.Equal(json, record.GetProperty("values").GetProperty(field).GetRawText());
        var id = record.GetProperty("globalRecordId").GetString();
        Assert.NotNull(id);
        Assert.Equal(Number(json), Assert.IsType<FakeRecordState>(store.GetRecord(ViewId, id)).Values[field]);
    }

    private static FakeDataStore CreateStore() => new(new FakeForm(
        Form,
        new Dictionary<string, IReadOnlyList<FieldOption>>(StringComparer.OrdinalIgnoreCase),
        null));

    // A number parsed as the API reads a JSON number, exactly.
    private static FieldValue Number(string text)
    {
        Assert.True(FieldValueCoercion.TryParseNumber(text, out var number));
        return number;
    }

    // Writes the number to a new record. Returns the record's id and the record the write returns, which a seed has
    // none of.
    private static async Task<(string Id, RecordDto? Returned)> WriteAsync(
        FakeDataStore store,
        Write write,
        string field,
        FieldValue number)
    {
        var values = new Dictionary<string, FieldValue> { [field] = number };
        switch (write)
        {
            case Write.Seed:
                return (store.SeedRecord(ViewId, values), null);

            case Write.Insert:
                var inserted = await store.InsertAsync(Form, values, Stamp, CancellationToken.None);
                return (inserted.GlobalRecordId, inserted);

            case Write.Update:
                var id = store.SeedRecord(ViewId);
                var result = await store.UpdateAsync(
                    Form, id, values, () => Stamp, Pass, permissions: null, CancellationToken.None);
                Assert.True(result.IsSuccess);
                return (id, result.Record);

            default:
                throw new ArgumentOutOfRangeException(nameof(write), write, "No case uses this write.");
        }
    }

    // The stored record an update's required gate is handed, after the locked read and before any write; the empty
    // delta changes no value.
    private static async Task<StoredRecord> GatedRecordAsync(FakeDataStore store, string id)
    {
        StoredRecord? gated = null;
        var result = await store.UpdateAsync(
            Form,
            id,
            new Dictionary<string, FieldValue>(),
            () => Stamp,
            record =>
            {
                gated = record;
                return Pass(record);
            },
            permissions: null,
            CancellationToken.None);
        Assert.True(result.IsSuccess);
        return Assert.IsType<StoredRecord>(gated);
    }

    // The system rows and field-type ids are the seeded metaFields and metaFieldTypes rows, as FakeForms builds them.
    private static FormMetadata CreateForm()
    {
        PageMetadata[] pages = [new(PageId, "Page 1", 0, BaseTable + PageId.ToString(CultureInfo.InvariantCulture))];
        FieldMetadata[] fields =
        [
            SystemRow(1, "UniqueKey", 23),
            SystemRow(2, "RecStatus", 22),
            SystemRow(3, "GlobalRecordId", 26),
            NumberField(4, 1m, WeightField, "float"),
            NumberField(5, 2m, CountField, "bigint"),
        ];
        return new FormMetadata(ViewId, BaseTable, BaseTable, Array.AsReadOnly(pages), Array.AsReadOnly(fields));
    }

    private static FieldMetadata SystemRow(int ordinal, string name, int fieldTypeId) => new()
    {
        FieldId = (ViewId * 1000) + ordinal,
        Name = name,
        FieldTypeId = fieldTypeId,
        FieldTypeName = name,
        TabIndex = 0m,
    };

    // A Number field (metaFieldTypes 5) whose column has the type's 8-byte catalogue size.
    private static FieldMetadata NumberField(int ordinal, decimal tabIndex, string name, string sqlTypeName) => new()
    {
        FieldId = (ViewId * 1000) + ordinal,
        Name = name,
        FieldTypeId = 5,
        FieldTypeName = "Number",
        Prompt = name,
        TabIndex = tabIndex,
        PageId = PageId,
        Column = FieldTypeMapper.DescribeColumn(name, sqlTypeName, 8),
    };
}

/// <summary>
/// The structured log line that follows a soft delete (AAP 0.3.4.6: form, <c>globalRecordId</c>, operation, outcome)
/// names the record by its <c>GlobalRecordId</c> as stored, as the line of its insert does, whatever letter case the
/// <c>DELETE</c> route spells the id in, so every line about one record carries one spelling.
/// </summary>
/// <remarks>
/// The class needs no database: it carries no <c>Integration</c> trait, joins no database collection and takes no
/// lock. The host runs the production pipeline over <see cref="FakeDataStore"/>, whose lookup ignores letter case as a
/// case-insensitive column collation does, and its lines are read from <see cref="ApiFactoryBase.Logs"/>, which holds
/// what the committed logging configuration lets through. <c>SqlRecordRepositoryWriteTests</c> proves on SQL Server,
/// under the collation it finds there, that the store's result carries the stored spelling. Every id these cases
/// delete holds a letter, so its spelling in the other letter case always differs from the stored one.
/// </remarks>
public sealed class StoredDataContractSoftDeleteLogTests
{
    private const int ViewId = FakeForms.TwoPageViewId;
    private const string LastNameField = "LastName";

    // The record service's fixed post-commit log event and the operation and outcome texts it writes.
    private const int RecordWriteEventId = 1;
    private const string SoftDeleteOperation = "softDelete";
    private const string CommittedOutcome = "committed";
    private const string UnchangedOutcome = "unchanged";

    private static readonly string RecordServiceCategory = typeof(EpiInfo.Api.Services.RecordService).FullName!;

    // A record created through POST holds the lower-case id the store generates, and the route spells it upper-case; a
    // seeded record holds an upper-case id, which the store keeps as given, and the route spells it lower-case. Either
    // way the first DELETE commits and the second finds the record deleted, each answering 204, and both lines, like
    // the insert's, carry the stored spelling, while no line's text or state carries the route's. A POST is repeated
    // while its generated id holds no letter; a repeated record's lines name its own id, so only the deleted record's
    // lines are counted.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SoftDeleteLogLinesNameTheStoredId(bool seededUpperCase)
    {
        var store = new FakeDataStore(FakeForms.TwoPage);
        await using var factory = new ApiFactory(ApiFactoryOptions.ForFake(store));
        using var client = factory.CreateClient();

        string stored = seededUpperCase
            ? store.SeedRecord(
                ViewId,
                new Dictionary<string, FieldValue> { [LastNameField] = FieldValue.FromText("Seeded") },
                globalRecordId: GuidWithALetter().ToUpperInvariant())
            : await CreateRecordWithALetterAsync(client);
        string routed = seededUpperCase ? stored.ToLowerInvariant() : stored.ToUpperInvariant();
        Assert.NotEqual(stored, routed);

        string path = string.Create(
            CultureInfo.InvariantCulture,
            $"/api/forms/{ViewId}/records/{Uri.EscapeDataString(routed)}");
        for (int attempt = 0; attempt < 2; attempt++)
        {
            using HttpResponseMessage response = await client.DeleteAsync(path);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        FakeRecordState? record = store.GetRecord(ViewId, stored);
        Assert.NotNull(record);
        Assert.Equal((stored, RecordQueryRules.DeletedRecStatus), (record.GlobalRecordId, record.RecStatus));

        CapturedLogEntry[] writes = factory.Logs
            .Find(entry => entry.Category == RecordServiceCategory
                && entry.EventId.Id == RecordWriteEventId
                && StateValue(entry, "GlobalRecordId") is string named
                && string.Equals(named, stored, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.Equal(
            new (object?, object?)[] { (stored, CommittedOutcome), (stored, UnchangedOutcome) },
            writes
                .Where(entry => Equals(StateValue(entry, "Operation"), SoftDeleteOperation))
                .Select(entry => (StateValue(entry, "GlobalRecordId"), StateValue(entry, "Outcome"))));
        Assert.All(writes, entry => Assert.Equal(stored, StateValue(entry, "GlobalRecordId")));
        Assert.Equal(seededUpperCase ? 2 : 3, writes.Length);

        // No line's text or state names the record as the route spelled it. The request's scope carries the path,
        // which the JSON console never writes (IncludeScopes is off), so scopes are not searched.
        Assert.DoesNotContain(
            factory.Logs.Entries,
            entry => entry.Message.Contains(routed, StringComparison.Ordinal)
                || entry.State.Any(pair =>
                    pair.Value is string text && text.Contains(routed, StringComparison.Ordinal)));
    }

    // The result the log line reads its id from carries the matched row's GlobalRecordId exactly, whatever text the
    // NOT NULL nvarchar(255) column holds (testdata/sql/05_data_tables.sql), an empty, whitespace or space-padded id
    // included, so a found record's 204 and its log line never fail on the id. Only a missing id is refused, because a
    // row the locked read matched always has one.
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t \r\n")]
    [InlineData("0aec0af6-64e6-4e94-a88b-b1b11474e101 ")]
    public void SoftDeleteResultCarriesTheMatchedIdUnchanged(string stored)
    {
        var factories = new (SoftDeleteStatus Status, Func<string, SoftDeleteResult> Create)[]
        {
            (SoftDeleteStatus.Deleted, SoftDeleteResult.Deleted),
            (SoftDeleteStatus.AlreadyDeleted, SoftDeleteResult.AlreadyDeleted),
        };
        foreach ((SoftDeleteStatus status, Func<string, SoftDeleteResult> create) in factories)
        {
            SoftDeleteResult result = create(stored);
            Assert.Equal((status, true), (result.Status, result.IsFound));
            Assert.Same(stored, result.GlobalRecordId);
            Assert.Equal(
                "globalRecordId",
                Assert.Throws<ArgumentNullException>(() => create(null!)).ParamName);
        }
    }

    // A version-4 GUID string in D format whose first hex digit is a letter, so it always differs from its spelling in
    // the other letter case.
    private static string GuidWithALetter() =>
        "a" + Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture)[1..];

    // POSTs a record holding LastName "Created" and returns the id the store generated, posting another while that id
    // holds no letter, which a version-4 GUID string lacks about once in 2.7 million ids.
    private static async Task<string> CreateRecordWithALetterAsync(HttpClient client)
    {
        const int Attempts = 8;
        for (int attempt = 0; attempt < Attempts; attempt++)
        {
            string id = (await ApiTestClient.CreateRecordAsync(
                client,
                ViewId,
                new Dictionary<string, object?> { [LastNameField] = "Created" })).GlobalRecordId;
            if (id.Any(char.IsAsciiLetter))
            {
                return id;
            }
        }

        throw new XunitException(string.Create(
            CultureInfo.InvariantCulture,
            $"{Attempts} records created through the API all have an id without a letter."));
    }

    private static object? StateValue(CapturedLogEntry entry, string key) =>
        Assert.Single(entry.State, pair => pair.Key == key).Value;
}
