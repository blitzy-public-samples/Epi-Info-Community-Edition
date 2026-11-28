using System.Data;
using System.Diagnostics;
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
/// The concurrency rule of the save path: two concurrent <c>PUT</c>s of one record serialise on the update lock of its
/// base row, so a field only one save submits keeps that save's value, fields both saves submit hold the values of the
/// later commit, together, and the page writes of the two saves never interleave. There is no row-version check: the
/// later writer wins, as in legacy Epi Info. Creates on the form, sent together with each other or beside an update of
/// another record, all commit, each with its base row and one row on every page: no two such writes form a lock
/// cycle, so none is ended as a deadlock victim and answered 503. Soft deletes, Find searches and counts sent beside
/// creates all succeed too: each soft delete takes the form's write lock shared, so no reader closes a cycle between
/// it and a create.
/// </summary>
/// <remarks>
/// <para>
/// Each test creates its own <c>FoodHistory</c> records, each carrying a <see cref="TestDatabase.NewMarker"/> marker
/// in <c>LastName</c>, and leaves them in the database. Its saves submit only <c>LastName</c> (page one,
/// <c>FoodHistory2</c>, <c>nvarchar(4000)</c>) and <c>Lab</c> (page two, <c>FoodHistory3</c>, <c>nvarchar(4000)</c>),
/// two text fields with no pattern, no maximum length and no legal values, so every value a test builds passes
/// validation. No seeded row, metadata row or schema object is touched.
/// </para>
/// <para>
/// Requests go over HTTP to the API hosted by <see cref="ApiFactory.ForDatabaseAsync"/>, and every check of stored
/// data reads SQL Server directly through <see cref="SqlSnapshot"/>. Each test holds the exclusive
/// <see cref="DatabaseWriteLock"/> for its whole run, so a snapshot comparison holds exactly the changes its own
/// requests made. The host clock is <see cref="ApiFactoryBase.Clock"/>: the record is created at the test's start, a
/// whole second, and each concurrent pair runs one minute later than the step before it, so both saves of a pair write
/// one known stamp.
/// </para>
/// <para>
/// The order of the two saves of a pair is read from <see cref="ApiFactoryBase.Recorder"/>, cleared before each pair.
/// On each save's connection the first command naming a record table is the locked read of the base row, the
/// <c>SELECT</c> with the <c>UPDLOCK</c> hint. Before its first write the save reads the record's rows of both page
/// tables, by the key and id of its base row, and then writes, all in one transaction. The update lock of the earlier
/// save's locked read is held to its commit, so the later save's locked read returns only after that commit, and every
/// command the later save sends after its locked read follows the last command of the earlier save. Each of those
/// commands, the page read its required gate runs on and every write, is a statement that starts after the earlier
/// commit, so it sees the committed rows whether the database answers <c>READ COMMITTED</c> reads with locks or, with
/// <c>READ_COMMITTED_SNAPSHOT</c> on, from the row versions committed when the statement starts: the gate never runs on
/// stale state.
/// </para>
/// <para>
/// Two requests sent together can still run one after the other, so one test makes the contention certain with a
/// gate: a second session takes an update lock on the test's own base row by its key, the pair is sent, and the gate
/// is released only once the recorder shows both locked reads started and unfinished, no other record-table command,
/// and SQL Server shows both sessions waiting for a base-table key lock behind the gate. The gate is a read: it writes
/// nothing, and its transaction is always rolled back in a <c>finally</c>, so the lock is released even when an
/// assertion fails.
/// </para>
/// <para>
/// Which save of a pair commits last depends on scheduling, so the tests never assume an order: they take it from the
/// recorded commands, identify an overlapping pair's winner from the values SQL holds, require the two to agree, and
/// every assertion holds for either order.
/// </para>
/// <para>
/// The create cases send rounds of two and of ten simultaneous creates, and rounds of one create beside an update of
/// another record the test created. Requests sent together can still run one after the other, so each case repeats
/// to give the transactions many chances to overlap. Every request must succeed, and the snapshot taken before the
/// first round, compared with the one after the last, must show exactly one new row per record table for each created
/// record, its marker in <c>LastName</c>, and no other change than the update's own columns. Their messages name
/// tables, columns, ids and counts, never a stored or submitted value.
/// </para>
/// <para>
/// The mixed case sends rounds of creates, soft deletes of records the round created first, Find searches and counts,
/// all in flight together. Every request must succeed, no recorded command may end with SQL error 1205, every soft
/// delete's locked read must follow its transaction's shared form write lock, and the record tables must hold exactly
/// the created records, the deleted ones at <c>RecStatus</c> 0 and the others at 1.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(DatabaseCollections.Database)]
public sealed partial class ConcurrentSaveTests : IAsyncLifetime
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
    private const string GlobalRecordIdColumn = "GlobalRecordId";
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

    // Rounds of two simultaneous creates, then rounds of a burst of simultaneous creates, and rounds of a create sent
    // beside an update of another record: requests sent together can still run one after the other, so each case
    // repeats to give the transactions many chances to overlap.
    private const int CreatePairRounds = 10;
    private const int CreateBurstRounds = 2;
    private const int CreateBurstSize = 10;
    private const int CreateBesideUpdateRounds = 10;

    // Rounds of the mixed burst: in each, creates, soft deletes of records the round created first, Find searches and
    // counts on the form all in flight together, the mix whose HOLDLOCK readers once closed a cycle between a soft
    // delete and a create's snapshot.
    private const int MixedBurstRounds = 10;
    private const int MixedBurstCreates = 3;
    private const int MixedBurstDeletes = 3;
    private const int MixedBurstFinds = 2;
    private const int MixedBurstCounts = 2;

    // The record repository's operation name for a soft delete, the fixed opening of the form write lock it sends
    // ("DECLARE @result int; EXEC @result = sys.sp_getapplock @Resource = @p0, @LockMode = @p1, ..."), the mode it
    // binds for a soft delete, and SQL Server's deadlock-victim error number.
    private const string DeleteOperation = "records.delete";
    private const string FormWriteLockPrefix = "DECLARE @result int; EXEC @result = sys.sp_getapplock ";
    private const string SharedLockMode = "Shared";
    private const int DeadlockVictimError = 1205;

    // The Find criterion of the mixed burst: every LastName that starts like a test marker, so the search reads the
    // base and page rows the creates and soft deletes of the burst change.
    private const string MarkerSearchPattern = TestDatabase.MarkerPrefix + "*";

    // RecStatus of an active record, as the save path writes it on insert, and of a soft-deleted one.
    private const short ActiveRecStatus = 1;
    private const short DeletedRecStatus = 0;

    // FoodHistory.GlobalRecordId is nvarchar(255) (testdata/sql/05_data_tables.sql).
    private const int GlobalRecordIdLength = 255;

    // The gate: an update lock on the test's own base row, taken by a seek on its clustered key UniqueKey, so no other
    // row is locked. It is a read and writes nothing; the transaction that holds it is always rolled back.
    private const string TakeBaseRowGateSql =
        "SELECT [" + RecStatusColumn + "] FROM [dbo].[" + BaseTable + "] WITH (UPDLOCK, ROWLOCK) WHERE [" +
        UniqueKeyColumn + "] = @uniqueKey AND [" + GlobalRecordIdColumn + "] = @globalRecordId;";

    // This session's id, and the number of key locks of a mode and status it holds on the base table. Object ids are
    // unique only within a database, so the table is resolved in the current one. Every compared value is bound.
    private const string GateLocksSql =
        """
        SELECT @@SPID AS session_id, COUNT(*) AS locks
        FROM sys.dm_tran_locks AS l
        JOIN sys.partitions AS p ON p.hobt_id = l.resource_associated_entity_id
        WHERE l.request_session_id = @@SPID
          AND l.resource_database_id = DB_ID()
          AND l.resource_type = @resourceType
          AND l.request_mode = @requestMode
          AND l.request_status = @requestStatus
          AND p.object_id = OBJECT_ID(@baseTable);
        """;

    // The requests of other sessions in the current database whose statement carries the lock hint and that wait for
    // a key lock on the base table, each with the session it waits on. Every compared value is bound.
    private const string WaitingLockedReadsSql =
        """
        SELECT r.session_id, r.blocking_session_id
        FROM sys.dm_exec_requests AS r
        CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) AS t
        WHERE r.database_id = DB_ID()
          AND r.session_id <> @@SPID
          AND t.text LIKE @lockHintPattern
          AND EXISTS (
              SELECT 1
              FROM sys.dm_tran_locks AS l
              JOIN sys.partitions AS p ON p.hobt_id = l.resource_associated_entity_id
              WHERE l.request_session_id = r.session_id
                AND l.resource_database_id = DB_ID()
                AND l.resource_type = @resourceType
                AND l.request_status = @requestStatus
                AND p.object_id = OBJECT_ID(@baseTable))
        ORDER BY r.session_id;
        """;

    // The spellings the lock queries compare with, bound as parameters: sys.dm_tran_locks names its resource type,
    // mode and status in nvarchar(60), and OBJECT_ID takes an nvarchar(776) name. The statement-text pattern is bound
    // at its own length; the hint holds no LIKE wildcard, so it matches any text that contains the hint.
    private const string KeyResourceType = "KEY";
    private const string UpdateLockMode = "U";
    private const string GrantedStatus = "GRANT";
    private const string WaitingStatus = "WAIT";
    private const string UpdateLockHint = "UPDLOCK";
    private const string UpdateLockHintPattern = "%" + UpdateLockHint + "%";
    private const string QualifiedBaseTable = "dbo." + BaseTable;
    private const int LockNameLength = 60;
    private const int ObjectNameLength = 776;

    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(5);

    // How often the gated test polls the recorder and the server, and how long it waits for both saves to reach the
    // gate: far below the API's 30-second command timeout, so a waiting locked read never times out first.
    private static readonly TimeSpan GatePollInterval = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan GateDeadline = TimeSpan.FromSeconds(10);

    // The form's record tables, base and pages, and its page tables.
    private static readonly string[] RecordTables = [BaseTable, PageOneTable, PageTwoTable];
    private static readonly string[] PageTables = [PageOneTable, PageTwoTable];

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
        int uniqueKey = StoredUniqueKey(before, globalRecordId);

        // Each save submits one field, on a different page: a delta, so neither may overwrite the other's field.
        StampAt(minutes: 1);
        string lastName = marker + "-A";
        string lab = "lab-B-" + Guid.NewGuid().ToString("N");
        const string Pair = "the disjoint pair";
        var saveA = new SaveRequest("PUT A", FieldValues((PageOneField, lastName)));
        var saveB = new SaveRequest("PUT B", FieldValues((PageTwoField, lab)));
        (JsonElement savedA, JsonElement savedB, _) =
            await SaveConcurrentlyAsync(viewId, globalRecordId, uniqueKey, saveA, saveB, Pair, ct);

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
        int uniqueKey = StoredUniqueKey(await SqlSnapshot.CaptureAsync(ct), globalRecordId);

        for (int round = 1; round <= OverlappingRounds; round++)
        {
            StampAt(minutes: round);
            string pair = string.Create(CultureInfo.InvariantCulture, $"the overlapping pair of round {round}");
            FieldPair c = NewPair(marker, "C", round);
            FieldPair d = NewPair(marker, "D", round);

            (JsonElement savedC, JsonElement savedD, SaveRequest later) = await SaveConcurrentlyAsync(
                viewId, globalRecordId, uniqueKey, c.ToRequest(), d.ToRequest(), pair, ct);

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

            // The later commit wins: the save the recorder shows passing the locked read second is the one SQL holds.
            Assert.True(
                string.Equals(winner.Name, later.Name, StringComparison.Ordinal),
                $"After {pair}, {PageOneTable}.{PageOneField} and {PageTwoTable}.{PageTwoField} of GlobalRecordId " +
                $"{globalRecordId} must hold the values of the later save, which the recorded commands show passed " +
                $"the locked read of {BaseTable} after the other committed: expected the values of {later.Name}, " +
                $"actual the values of {winner.Name}.");

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
        int uniqueKey = StoredUniqueKey(before, globalRecordId);

        const string DisjointPair = "the disjoint pair";
        DateTime disjointTime = StampAt(minutes: 1);
        await SaveConcurrentlyAsync(
            viewId,
            globalRecordId,
            uniqueKey,
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
            uniqueKey,
            NewPair(marker, "C", round: 1).ToRequest(),
            NewPair(marker, "D", round: 1).ToRequest(),
            OverlappingPair,
            ct);
        SqlSnapshot afterOverlapping = await SqlSnapshot.CaptureAsync(ct);
        AssertSaveStamp(afterOverlapping, globalRecordId, overlappingTime, OverlappingPair);
        AssertBaseColumnsUnchanged(before, afterOverlapping, globalRecordId, OverlappingPair);
        AssertUnsupportedColumnsUntouched(before, afterOverlapping, globalRecordId, OverlappingPair);
    }

    [Fact]
    public async Task Concurrent_saves_wait_at_the_locked_base_row_read_and_the_later_save_reads_the_earlier_commit()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        CancellationToken ct = timeout.Token;
        await using DatabaseWriteLock dbLock = await DatabaseWriteLock.AcquireExclusiveAsync(ct);

        Factory.Clock.SetLocalNow(TestStart);
        int viewId = await ApiTestClient.GetViewIdAsync(Client, FormName, ct);
        string marker = TestDatabase.NewMarker();
        string globalRecordId = await CreateRecordAsync(viewId, marker, ct);
        SqlSnapshot before = await SqlSnapshot.CaptureAsync(ct);
        int uniqueKey = StoredUniqueKey(before, globalRecordId);
        string initialLastName = StoredText(before, PageOneTable, PageOneField, globalRecordId);
        string initialLab = StoredText(before, PageTwoTable, PageTwoField, globalRecordId);

        // Each save submits one field, on a different page, so each response shows which state its transaction read.
        const string Pair = "the gated pair";
        DateTime pairTime = StampAt(minutes: 1);
        string lastName = marker + "-A";
        string lab = "lab-B-" + Guid.NewGuid().ToString("N");
        var saveA = new SaveRequest("PUT A", FieldValues((PageOneField, lastName)));
        var saveB = new SaveRequest("PUT B", FieldValues((PageTwoField, lab)));
        string recordPath = RecordPath(viewId, globalRecordId);

        Task<HttpResponseMessage>? putA = null;
        Task<HttpResponseMessage>? putB = null;
        try
        {
            SqlConnection gateConnection = await TestDatabase.OpenConnectionAsync(ct);
            SqlTransaction? gateTransaction = null;
            string? rollbackFailure;
            try
            {
                gateTransaction = gateConnection.BeginTransaction(IsolationLevel.ReadCommitted);
                short gateSession = await TakeBaseRowGateAsync(
                    gateConnection, gateTransaction, uniqueKey, globalRecordId, ct);

                Factory.Recorder.Clear();
                putA = ApiTestClient.PutJsonAsync(Client, recordPath, new { values = saveA.Values }, ct);
                putB = ApiTestClient.PutJsonAsync(Client, recordPath, new { values = saveB.Values }, ct);
                await WaitForBothSavesAtTheGateAsync(
                    gateConnection,
                    gateTransaction,
                    gateSession,
                    new InFlightSave(saveA, putA),
                    new InFlightSave(saveB, putB),
                    globalRecordId,
                    Pair,
                    ct);
            }
            finally
            {
                rollbackFailure = await ReleaseGateAsync(gateConnection, gateTransaction);
            }

            Assert.True(
                rollbackFailure is null,
                $"Rolling back the transaction that held the gate's update lock on {BaseTable} row GlobalRecordId " +
                $"{globalRecordId} failed with {rollbackFailure}.");

            await Task.WhenAll(putA, putB);
            JsonElement savedA = await ReadSavedValuesAsync(await putA, globalRecordId, $"{saveA.Name} of {Pair}", ct);
            JsonElement savedB = await ReadSavedValuesAsync(await putB, globalRecordId, $"{saveB.Name} of {Pair}", ct);
            SaveRequest later = AssertSerializedAtLockedRead(
                Factory.Recorder.Commands, saveA, saveB, globalRecordId, uniqueKey, Pair);

            // The earlier save read and answered the state before the pair for the field only the later save submits.
            // The later save's locked read returned only after the earlier commit, so its page read, its required gate
            // and its response all saw the earlier save's committed value, never the state before the pair.
            bool aIsLater = string.Equals(later.Name, saveA.Name, StringComparison.Ordinal);
            (JsonElement earlierValues, string earlierRequest, JsonElement laterValues, string laterRequest) = aIsLater
                ? (savedB, $"{saveB.Name} of {Pair}, the earlier save", savedA, $"{saveA.Name} of {Pair}, the later save")
                : (savedA, $"{saveA.Name} of {Pair}, the earlier save", savedB, $"{saveB.Name} of {Pair}, the later save");
            AssertResponseValue(
                earlierValues, PageOneField, aIsLater ? initialLastName : lastName, globalRecordId, earlierRequest);
            AssertResponseValue(earlierValues, PageTwoField, aIsLater ? lab : initialLab, globalRecordId, earlierRequest);
            AssertResponseValue(laterValues, PageOneField, lastName, globalRecordId, laterRequest);
            AssertResponseValue(laterValues, PageTwoField, lab, globalRecordId, laterRequest);

            SqlSnapshot after = await SqlSnapshot.CaptureAsync(ct);
            AssertStoredText(after, PageOneTable, PageOneField, globalRecordId, lastName, Pair);
            AssertStoredText(after, PageTwoTable, PageTwoField, globalRecordId, lab, Pair);
            AssertSaveStamp(after, globalRecordId, pairTime, Pair);
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
        finally
        {
            // A request still in flight after a failure is awaited, so the host never stops under it, and any response
            // is disposed; its own outcome is not observed here, so it never replaces the failure being raised.
            await SettleAsync(putA);
            await SettleAsync(putB);
        }
    }

    [Fact]
    public async Task Simultaneous_creates_on_the_form_all_commit_each_with_every_row_of_its_record()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        CancellationToken ct = timeout.Token;
        await using DatabaseWriteLock dbLock = await DatabaseWriteLock.AcquireExclusiveAsync(ct);

        Factory.Clock.SetLocalNow(TestStart);
        int viewId = await ApiTestClient.GetViewIdAsync(Client, FormName, ct);
        SqlSnapshot before = await SqlSnapshot.CaptureAsync(ct);

        // Each created record's marker, by the id its response returned.
        var created = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int round = 1; round <= CreatePairRounds; round++)
        {
            await CreateSimultaneouslyAsync(
                viewId,
                count: 2,
                string.Create(CultureInfo.InvariantCulture, $"round {round} of two simultaneous creates"),
                created,
                ct);
        }

        for (int round = 1; round <= CreateBurstRounds; round++)
        {
            await CreateSimultaneouslyAsync(
                viewId,
                CreateBurstSize,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"round {round} of {CreateBurstSize} simultaneous creates"),
                created,
                ct);
        }

        SqlSnapshot after = await SqlSnapshot.CaptureAsync(ct);
        AssertOnlyCreatedRecordsAdded(
            SqlSnapshot.Compare(before, after),
            after,
            created,
            changedRecord: null,
            allowedChanges: new HashSet<(string Table, string Column)>(),
            "the simultaneous creates");
    }

    [Fact]
    public async Task A_create_sent_beside_an_update_of_another_record_commits_and_the_update_commits()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        CancellationToken ct = timeout.Token;
        await using DatabaseWriteLock dbLock = await DatabaseWriteLock.AcquireExclusiveAsync(ct);

        Factory.Clock.SetLocalNow(TestStart);
        int viewId = await ApiTestClient.GetViewIdAsync(Client, FormName, ct);
        string updatedId = await CreateRecordAsync(viewId, TestDatabase.NewMarker(), ct);
        string updatedPath = RecordPath(viewId, updatedId);
        SqlSnapshot before = await SqlSnapshot.CaptureAsync(ct);

        var created = new Dictionary<string, string>(StringComparer.Ordinal);
        string? lastLab = null;
        for (int round = 1; round <= CreateBesideUpdateRounds; round++)
        {
            string label = string.Create(CultureInfo.InvariantCulture, $"round {round} of a create beside an update");
            StampAt(minutes: round);
            string marker = TestDatabase.NewMarker();
            string lab = string.Create(CultureInfo.InvariantCulture, $"lab-U{round}-{Guid.NewGuid():N}");

            // Both requests are in flight before either is awaited, so the create's transaction and the update's can
            // overlap.
            Task<CreatedRecord> create =
                ApiTestClient.CreateRecordAsync(Client, viewId, FieldValues((PageOneField, marker)), ct);
            Task<HttpResponseMessage> update = ApiTestClient.PutJsonAsync(
                Client, updatedPath, new { values = FieldValues((PageTwoField, lab)) }, ct);
            try
            {
                await WhenAllSettledAsync(create, update);

                Assert.True(
                    create.IsCompletedSuccessfully,
                    $"{label}: the POST must return 201 with its record; {FailureOf(create)}");
                RecordCreated(created, await create, marker, $"the POST of {label}");

                Assert.True(
                    update.IsCompletedSuccessfully,
                    $"{label}: the PUT of GlobalRecordId {updatedId} sent no response; {FailureOf(update)}");
                JsonElement updatedValues =
                    await ReadSavedValuesAsync(await update, updatedId, $"the PUT of {label}", ct);
                AssertResponseText(updatedValues, PageTwoField, lab, updatedId, $"the PUT of {label}");
            }
            finally
            {
                await SettleAsync(update);
                await WhenAllSettledAsync(create);
            }

            lastLab = lab;
        }

        Assert.NotNull(lastLab);
        SqlSnapshot after = await SqlSnapshot.CaptureAsync(ct);
        AssertStoredTextWritten(after, PageTwoTable, PageTwoField, updatedId, lastLab, "the last update");
        AssertOnlyCreatedRecordsAdded(
            SqlSnapshot.Compare(before, after),
            after,
            created,
            updatedId,
            new HashSet<(string Table, string Column)>
            {
                (PageTwoTable, PageTwoField),
                (BaseTable, LastSaveLogonNameColumn),
                (BaseTable, LastSaveTimeColumn),
            },
            "the creates beside the updates");
    }

    // Creates, soft deletes, Find searches and counts of one form sent together: the soft delete takes the form's write
    // lock shared before its locked read, so it never runs beside a create, which holds the lock exclusive, and no
    // HOLDLOCK reader can close a cycle between the two writers. Every request succeeds, no command ends as a deadlock
    // victim (1205), each soft delete's transaction takes the shared lock right before its locked read, and the record
    // tables hold exactly the created records, the deleted ones at RecStatus 0 and the others at 1.
    [Fact]
    public async Task Creates_deletes_finds_and_counts_sent_together_all_succeed_without_a_deadlock()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        CancellationToken ct = timeout.Token;
        await using DatabaseWriteLock dbLock = await DatabaseWriteLock.AcquireExclusiveAsync(ct);

        Factory.Clock.SetLocalNow(TestStart);
        int viewId = await ApiTestClient.GetViewIdAsync(Client, FormName, ct);
        SqlSnapshot before = await SqlSnapshot.CaptureAsync(ct);
        var created = new Dictionary<string, string>(StringComparer.Ordinal);
        var deleted = new HashSet<string>(StringComparer.Ordinal);
        Factory.Recorder.Clear();

        for (int round = 1; round <= MixedBurstRounds; round++)
        {
            string label = string.Create(CultureInfo.InvariantCulture, $"round {round} of the mixed burst");
            StampAt(minutes: round);

            // The records this round deletes exist before the burst starts, so each delete finds an active record.
            var toDelete = new Dictionary<string, string>(StringComparer.Ordinal);
            await CreateSimultaneouslyAsync(viewId, MixedBurstDeletes, $"the deletable records of {label}", toDelete, ct);

            string[] markers = [.. Enumerable.Range(0, MixedBurstCreates).Select(_ => TestDatabase.NewMarker())];
            Task<CreatedRecord>[] creates =
            [
                .. markers.Select(marker =>
                    ApiTestClient.CreateRecordAsync(Client, viewId, FieldValues((PageOneField, marker)), ct)),
            ];
            Task<HttpResponseMessage>[] deletes =
                [.. toDelete.Keys.Select(id => Client.DeleteAsync(RecordPath(viewId, id), ct))];
            Task<HttpResponseMessage>[] finds =
            [
                .. Enumerable.Range(0, MixedBurstFinds).Select(_ => ApiTestClient.PostJsonAsync(
                    Client,
                    string.Create(CultureInfo.InvariantCulture, $"/api/forms/{viewId}/records/search"),
                    new { criteria = new[] { new { field = PageOneField, value = MarkerSearchPattern } } },
                    ct)),
            ];
            Task<HttpResponseMessage>[] counts =
            [
                .. Enumerable.Range(0, MixedBurstCounts).Select(_ => Client.GetAsync(
                    string.Create(CultureInfo.InvariantCulture, $"/api/forms/{viewId}/records/count"),
                    ct)),
            ];
            Task<HttpResponseMessage>[] responses = [.. deletes, .. finds, .. counts];
            try
            {
                await WhenAllSettledAsync([.. creates, .. responses]);

                string[] failures = [.. creates.Where(create => !create.IsCompletedSuccessfully).Select(FailureOf)];
                Assert.True(
                    failures.Length == 0,
                    $"{label}: every POST must return 201 with its record; {string.Join(" | ", failures)}");
                for (int index = 0; index < creates.Length; index++)
                {
                    RecordCreated(created, await creates[index], markers[index], $"POST {index + 1} of {label}");
                }

                await AssertEachStatusAsync(deletes, HttpStatusCode.NoContent, "DELETE", label, ct);
                await AssertEachStatusAsync(finds, HttpStatusCode.OK, "POST records/search", label, ct);
                await AssertEachStatusAsync(counts, HttpStatusCode.OK, "GET records/count", label, ct);
            }
            finally
            {
                foreach (Task<HttpResponseMessage> response in responses)
                {
                    await SettleAsync(response);
                }

                await WhenAllSettledAsync(creates);
            }

            foreach ((string id, string marker) in toDelete)
            {
                created.Add(id, marker);
                deleted.Add(id);
            }
        }

        IReadOnlyList<RecordedCommand> commands = Factory.Recorder.Commands;
        RecordedCommand[] victims = [.. commands.Where(command => command.SqlErrorNumber == DeadlockVictimError)];
        Assert.True(
            victims.Length == 0,
            $"No command of the mixed burst may end as a deadlock victim (SQL error {DeadlockVictimError}):" +
            DescribeCommands(victims));
        AssertEachDeleteTookTheSharedFormLockFirst(commands, MixedBurstRounds * MixedBurstDeletes);

        SqlSnapshot after = await SqlSnapshot.CaptureAsync(ct);
        AssertOnlyCreatedRecordsAdded(
            SqlSnapshot.Compare(before, after),
            after,
            created,
            changedRecord: null,
            allowedChanges: new HashSet<(string Table, string Column)>(),
            "the mixed burst");
        string[] wrongStatus =
        [
            .. created.Keys
                .Where(id => StoredCell(after, BaseTable, RecStatusColumn, id) is not short status
                    || status != (deleted.Contains(id) ? DeletedRecStatus : ActiveRecStatus))
                .Order(StringComparer.Ordinal),
        ];
        Assert.True(
            wrongStatus.Length == 0,
            $"After the mixed burst each deleted record must hold RecStatus 0 and every other created record 1; these " +
            $"GlobalRecordIds do not: [{string.Join(", ", wrongStatus)}].");
    }

    // Each response of requests answered expected; a message names the request, its index, the status and the
    // problem's title and error keys, never a value.
    private static async Task AssertEachStatusAsync(
        IReadOnlyList<Task<HttpResponseMessage>> requests,
        HttpStatusCode expected,
        string request,
        string label,
        CancellationToken ct)
    {
        for (int index = 0; index < requests.Count; index++)
        {
            string name = string.Create(CultureInfo.InvariantCulture, $"{request} {index + 1} of {label}");
            Assert.True(requests[index].IsCompletedSuccessfully, $"{name} sent no response; {FailureOf(requests[index])}");
            await AssertStatusAsync(await requests[index], expected, name, ct);
        }
    }

    // On each connection, every locked base read a soft delete sends inside its transaction directly follows that
    // transaction's form write lock, taken shared and granted, and the burst sent exactly expected such reads.
    private static void AssertEachDeleteTookTheSharedFormLockFirst(IReadOnlyList<RecordedCommand> commands, int expected)
    {
        int lockedReads = 0;
        var violations = new List<RecordedCommand>();
        foreach (IGrouping<Guid, RecordedCommand> connection in commands
                     .Where(command => string.Equals(command.Operation, DeleteOperation, StringComparison.Ordinal))
                     .GroupBy(command => command.ClientConnectionId))
        {
            RecordedCommand? formLock = null;
            foreach (RecordedCommand command in connection.OrderBy(command => command.Sequence))
            {
                if (command.CommandText.StartsWith(FormWriteLockPrefix, StringComparison.Ordinal))
                {
                    formLock = command;
                    continue;
                }

                if (IsLockedBaseRead(command))
                {
                    lockedReads++;
                    bool sharedFirst = formLock is not null
                        && formLock.InTransaction
                        && formLock.Succeeded
                        && command.InTransaction
                        && Binds(formLock, SharedLockMode);
                    if (!sharedFirst)
                    {
                        violations.Add(command);
                    }
                }

                formLock = null;
            }
        }

        Assert.True(
            lockedReads == expected && violations.Count == 0,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Each soft delete must take the form's write lock {SharedLockMode} inside its transaction right before " +
                $"its locked read of {BaseTable}: expected {expected} locked reads, actual {lockedReads}, " +
                $"{violations.Count} without the lock before them:{DescribeCommands(violations)}"));
    }

    // Sends count creates, each with a fresh marker in LastName, with every request in flight before any is awaited,
    // so their write transactions can overlap. Every one must return 201 with a new id and its own marker; each is
    // added to created.
    private async Task CreateSimultaneouslyAsync(
        int viewId,
        int count,
        string label,
        Dictionary<string, string> created,
        CancellationToken ct)
    {
        string[] markers = [.. Enumerable.Range(0, count).Select(_ => TestDatabase.NewMarker())];
        Task<CreatedRecord>[] creates =
        [
            .. markers.Select(marker =>
                ApiTestClient.CreateRecordAsync(Client, viewId, FieldValues((PageOneField, marker)), ct)),
        ];
        await WhenAllSettledAsync(creates);

        string[] failures = [.. creates.Where(create => !create.IsCompletedSuccessfully).Select(FailureOf)];
        Assert.True(
            failures.Length == 0,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{label}: every POST must return 201 with its record; {failures.Length} of {count} did not: " +
                $"{string.Join(" | ", failures)}"));

        for (int index = 0; index < creates.Length; index++)
        {
            RecordCreated(
                created,
                await creates[index],
                markers[index],
                string.Create(CultureInfo.InvariantCulture, $"POST {index + 1} of {label}"));
        }
    }

    // Adds a created record to created, keyed by its new id: an id no earlier create returned, whose response values
    // hold the marker the request sent.
    private static void RecordCreated(
        Dictionary<string, string> created,
        CreatedRecord record,
        string marker,
        string request)
    {
        Assert.True(
            created.TryAdd(record.GlobalRecordId, marker),
            $"{request} returned GlobalRecordId {record.GlobalRecordId}, which an earlier create of the test returned.");
        if (record.Record.ValueKind != JsonValueKind.Object ||
            !record.Record.TryGetProperty("values", out JsonElement values) ||
            values.ValueKind != JsonValueKind.Object)
        {
            throw new XunitException(
                $"{request} returned a Record of GlobalRecordId {record.GlobalRecordId} without a values object.");
        }

        AssertResponseText(values, PageOneField, marker, record.GlobalRecordId, request);
    }

    // Waits until every request has completed, successfully or not, without observing any outcome, so the caller
    // inspects each one and no failure hides another.
    private static async Task WhenAllSettledAsync(params Task[] requests) =>
        await Task.WhenAll(requests).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    // Why a request task did not complete with a value: the exception's type and message, which the API test client
    // writes from the response's shape alone and never from a value, or the task's status.
    private static string FailureOf(Task request) =>
        request.Exception?.InnerException is { } failure
            ? $"{failure.GetType().Name}: {failure.Message}"
            : $"task status {request.Status}";

    // The test's records are the only change: every created record added exactly one row to each of the form's record
    // tables, with its marker in LastName; no other row was added or removed; no column set changed; and the only
    // changed cells are allowedChanges of changedRecord. Messages name tables, columns, ids and counts, never a value.
    private static void AssertOnlyCreatedRecordsAdded(
        SnapshotDiff diff,
        SqlSnapshot after,
        IReadOnlyDictionary<string, string> created,
        string? changedRecord,
        IReadOnlySet<(string Table, string Column)> allowedChanges,
        string context)
    {
        var violations = new List<string>();
        violations.AddRange(diff.SchemaChanges.Select(change => $"the columns of {change.Table} changed"));
        violations.AddRange(diff.RemovedRows.Select(row => $"a {row.Table} row {row.GlobalRecordId} was removed"));
        violations.AddRange(diff.AddedRows
            .Where(row => !created.ContainsKey(row.GlobalRecordId))
            .Select(row => $"a {row.Table} row {row.GlobalRecordId} of no created record was added"));
        violations.AddRange(diff.ChangedCells
            .Where(cell => !string.Equals(cell.GlobalRecordId, changedRecord, StringComparison.Ordinal) ||
                !allowedChanges.Contains((cell.Table, cell.Column)))
            .Select(cell => $"{cell.Table}.{cell.Column} of GlobalRecordId {cell.GlobalRecordId} changed"));

        foreach ((string globalRecordId, string marker) in created)
        {
            foreach (string table in RecordTables)
            {
                int added = diff.AddedRows.Count(row =>
                    string.Equals(row.Table, table, StringComparison.Ordinal) &&
                    string.Equals(row.GlobalRecordId, globalRecordId, StringComparison.Ordinal));
                if (added != 1)
                {
                    violations.Add(string.Create(
                        CultureInfo.InvariantCulture,
                        $"created GlobalRecordId {globalRecordId} added {added} {table} rows, not 1"));
                }
            }

            if (after.Row(PageOneTable, globalRecordId)?[PageOneField] is not string stored ||
                !string.Equals(stored, marker, StringComparison.Ordinal))
            {
                violations.Add(
                    $"{PageOneTable}.{PageOneField} of created GlobalRecordId {globalRecordId} does not hold its marker");
            }
        }

        Assert.True(
            violations.Count == 0,
            string.Create(
                CultureInfo.InvariantCulture,
                $"After {context}, the record tables must hold the {created.Count} created records, each with one row " +
                $"per table, and no other change; {violations.Count} violations: {string.Join("; ", violations)}."));
    }

    // The response's values carry expected as the text of field; the message names the field and id, never the text.
    private static void AssertResponseText(
        JsonElement values,
        string field,
        string expected,
        string globalRecordId,
        string request) =>
        Assert.True(
            values.TryGetProperty(field, out JsonElement value) &&
            value.ValueKind == JsonValueKind.String &&
            string.Equals(value.GetString(), expected, StringComparison.Ordinal),
            $"{request} must return in {field} of GlobalRecordId {globalRecordId} the text it sent; it returned a " +
            $"{(values.TryGetProperty(field, out JsonElement returned) ? returned.ValueKind.ToString() : "missing")} " +
            "value that differs.");

    // The record's row of a table holds expected in column; the message names the cell, never the text.
    private static void AssertStoredTextWritten(
        SqlSnapshot snapshot,
        string table,
        string column,
        string globalRecordId,
        string expected,
        string context) =>
        Assert.True(
            snapshot.Row(table, globalRecordId)?[column] is string stored &&
            string.Equals(stored, expected, StringComparison.Ordinal),
            $"After {context}, {table}.{column} of GlobalRecordId {globalRecordId} does not hold the text it wrote.");

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
    // can overlap and contend for the base row's update lock. Both must answer 200, and the commands recorded for the
    // pair must show the two saves serialised at their locked reads (AssertSerializedAtLockedRead), each reading the
    // record's page rows by its base row's uniqueKey and globalRecordId before writing. Returns each response's values
    // and the save that passed its locked read second, the later commit.
    private async Task<(JsonElement First, JsonElement Second, SaveRequest Later)> SaveConcurrentlyAsync(
        int viewId,
        string globalRecordId,
        int uniqueKey,
        SaveRequest first,
        SaveRequest second,
        string pair,
        CancellationToken ct)
    {
        string recordPath = RecordPath(viewId, globalRecordId);
        Factory.Recorder.Clear();
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
            SaveRequest later = AssertSerializedAtLockedRead(
                Factory.Recorder.Commands, first, second, globalRecordId, uniqueKey, pair);
            return (firstValues, secondValues, later);
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

    // Waits for a request that may still be in flight without observing its outcome, then disposes its response.
    private static async Task SettleAsync(Task<HttpResponseMessage>? request)
    {
        if (request is null)
        {
            return;
        }

        await ((Task)request).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        await DisposeResponseAsync(request);
    }

    private static string RecordPath(int viewId, string globalRecordId) =>
        string.Create(CultureInfo.InvariantCulture, $"/api/forms/{viewId}/records/{globalRecordId}");

    // Takes the gate inside the open transaction: an update lock on the test's own base row, by its key, then proves it
    // granted as the one update lock this session holds on a base-table key. Fails before any request is sent when it
    // is not. Returns the gate's session id. The read writes nothing.
    private static async Task<short> TakeBaseRowGateAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int uniqueKey,
        string globalRecordId,
        CancellationToken ct)
    {
        var statuses = new List<short>();
        await using (SqlCommand take = connection.CreateCommand())
        {
            take.Transaction = transaction;
            take.CommandText = TakeBaseRowGateSql;
            take.Parameters.Add(new SqlParameter("@uniqueKey", SqlDbType.Int) { Value = uniqueKey });
            take.Parameters.Add(
                new SqlParameter("@globalRecordId", SqlDbType.NVarChar, GlobalRecordIdLength) { Value = globalRecordId });
            await using SqlDataReader reader = await take.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                statuses.Add(reader.GetInt16(0));
            }
        }

        Assert.True(
            statuses.Count == 1 && statuses[0] == ActiveRecStatus,
            string.Create(
                CultureInfo.InvariantCulture,
                $"The gate's read of {BaseTable} row {UniqueKeyColumn} {uniqueKey}, GlobalRecordId {globalRecordId} " +
                $"must return the one active row: expected {RecStatusColumn} [{ActiveRecStatus}], actual " +
                $"[{string.Join(", ", statuses)}]. No request was sent."));

        await using SqlCommand check = connection.CreateCommand();
        check.Transaction = transaction;
        check.CommandText = GateLocksSql;
        AddLockName(check, "@resourceType", KeyResourceType);
        AddLockName(check, "@requestMode", UpdateLockMode);
        AddLockName(check, "@requestStatus", GrantedStatus);
        AddObjectName(check, "@baseTable", QualifiedBaseTable);
        short session;
        int held;
        await using (SqlDataReader reader = await check.ExecuteReaderAsync(ct))
        {
            Assert.True(await reader.ReadAsync(ct), "The gate's lock query returned no row.");
            session = reader.GetInt16(0);
            held = reader.GetInt32(1);
        }

        Assert.True(
            held == 1,
            string.Create(
                CultureInfo.InvariantCulture,
                $"After its UPDLOCK, ROWLOCK read of {BaseTable} row {UniqueKeyColumn} {uniqueKey}, GlobalRecordId " +
                $"{globalRecordId}, the gate session must hold exactly one granted {UpdateLockMode} lock on a " +
                $"{KeyResourceType} of dbo.{BaseTable}: expected 1, actual {held}. No request was sent."));
        return session;
    }

    // Polls until both saves wait at their locked base-row reads behind the gate. The recorder must show each save's
    // locked read started inside a transaction, on a connection of its own, and unfinished, with no other record-table
    // command; and SQL Server must show two other sessions, each running a statement with the UPDLOCK hint and waiting
    // for a key lock on the base table, held by the gate or by the other. Fails at once when a save answers or a
    // record-table command gets past the gate, and once GateDeadline passes otherwise.
    private async Task WaitForBothSavesAtTheGateAsync(
        SqlConnection gate,
        SqlTransaction gateTransaction,
        short gateSession,
        InFlightSave first,
        InFlightSave second,
        string globalRecordId,
        string pair,
        CancellationToken ct)
    {
        string gated = string.Create(
            CultureInfo.InvariantCulture,
            $"While the gate session {gateSession} held its update lock on {BaseTable} row GlobalRecordId " +
            $"{globalRecordId} for {pair}");
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            foreach (InFlightSave save in new[] { first, second })
            {
                Assert.False(
                    save.Response.IsCompleted,
                    $"{gated}, {save.Request.Name} answered, so its locked read did not wait for the lock: expected " +
                    $"it in flight, actual task status {save.Response.Status}.");
            }

            List<RecordedCommand> recordCommands = Factory.Recorder.Commands.Where(IsRecordCommand).ToList();
            List<RecordedCommand> passed = recordCommands
                .Where(command => !IsLockedBaseRead(command) || command.Completed || !command.InTransaction)
                .ToList();
            Assert.True(
                passed.Count == 0 && recordCommands.Count <= 2,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{gated}, the record tables may have received only the two saves' locked reads of {BaseTable}, " +
                    $"started in their transactions and still waiting: expected at most 2 such commands and nothing " +
                    $"else, actual {recordCommands.Count} record-table commands, {passed.Count} of them past the gate " +
                    $"or not a waiting locked read:{DescribeCommands(recordCommands)}"));

            int connections = recordCommands.Select(command => command.ClientConnectionId).Distinct().Count();
            IReadOnlyList<WaitingSession> waiting = recordCommands.Count == 2 && connections == 2
                ? await ReadWaitingLockedReadsAsync(gate, gateTransaction, ct)
                : [];
            if (waiting.Count == 2 && WaitBehindGate(waiting, gateSession))
            {
                return;
            }

            if (elapsed.Elapsed >= GateDeadline)
            {
                Assert.Fail(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{gated}, both saves must reach their locked reads within {GateDeadline.TotalSeconds} s: " +
                        $"expected 2 waiting locked reads on 2 connections, and 2 other sessions waiting for a " +
                        $"{KeyResourceType} lock on dbo.{BaseTable} behind the gate or each other; actual " +
                        $"{recordCommands.Count} on {connections} connection(s):{DescribeCommands(recordCommands)}" +
                        $"{Environment.NewLine}and waiting sessions [{string.Join(", ", waiting)}]."));
            }

            await Task.Delay(GatePollInterval, ct);
        }
    }

    // Each waiting session waits on the gate or on the other waiting session, and at least one waits on the gate, so
    // both are held by the gate's lock.
    private static bool WaitBehindGate(IReadOnlyList<WaitingSession> waiting, short gateSession) =>
        waiting.Any(session => session.BlockedBy == gateSession) &&
        waiting.All(session =>
            session.BlockedBy == gateSession ||
            waiting.Any(other => other.Session != session.Session && other.Session == session.BlockedBy));

    private static async Task<IReadOnlyList<WaitingSession>> ReadWaitingLockedReadsAsync(
        SqlConnection gate,
        SqlTransaction gateTransaction,
        CancellationToken ct)
    {
        await using SqlCommand command = gate.CreateCommand();
        command.Transaction = gateTransaction;
        command.CommandText = WaitingLockedReadsSql;
        command.Parameters.Add(
            new SqlParameter("@lockHintPattern", SqlDbType.NVarChar, UpdateLockHintPattern.Length)
            {
                Value = UpdateLockHintPattern,
            });
        AddLockName(command, "@resourceType", KeyResourceType);
        AddLockName(command, "@requestStatus", WaitingStatus);
        AddObjectName(command, "@baseTable", QualifiedBaseTable);
        var waiting = new List<WaitingSession>();
        await using SqlDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            waiting.Add(new WaitingSession(reader.GetInt16(0), reader.IsDBNull(1) ? (short)0 : reader.GetInt16(1)));
        }

        return waiting;
    }

    private static void AddLockName(SqlCommand command, string name, string value) =>
        command.Parameters.Add(new SqlParameter(name, SqlDbType.NVarChar, LockNameLength) { Value = value });

    private static void AddObjectName(SqlCommand command, string name, string value) =>
        command.Parameters.Add(new SqlParameter(name, SqlDbType.NVarChar, ObjectNameLength) { Value = value });

    // Rolls the gate's transaction back, then disposes it and its connection. A failed rollback is returned, not
    // thrown, so it never replaces an assertion raised while the gate was held; closing the connection rolls back a
    // pending transaction, so the update lock is released either way. The gate wrote nothing, so the rollback undoes
    // nothing but the lock.
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

    // The two saves of a pair ran one after the other from their locked reads on: each save's connection shows the
    // save path's order (TraceSaves), the DML of each trace binds the values of exactly one save, and every command the
    // later save sent after its locked read follows the last command of the earlier save. The earlier save's update lock
    // on the base row is held to its commit, so the later save's locked read can return, and its page read, required
    // gate and writes run, only after that commit: each is a statement that starts after it and sees the committed
    // rows, with or without READ_COMMITTED_SNAPSHOT. Returns the later save.
    private static SaveRequest AssertSerializedAtLockedRead(
        IReadOnlyList<RecordedCommand> commands,
        SaveRequest first,
        SaveRequest second,
        string globalRecordId,
        int uniqueKey,
        string pair)
    {
        IReadOnlyList<SaveTrace> traces = TraceSaves(commands, globalRecordId, uniqueKey, pair);
        Assert.True(
            traces.Count == 2,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{pair} of GlobalRecordId {globalRecordId} must send one locked read of {BaseTable} per save: " +
                $"expected 2, actual {traces.Count}. Record-table commands:" +
                $"{DescribeCommands(commands.Where(IsRecordCommand))}"));

        SaveRequest ownerOfFirst = OwnerOf(traces[0], first, second, globalRecordId, pair);
        SaveRequest ownerOfSecond = OwnerOf(traces[1], first, second, globalRecordId, pair);
        Assert.False(
            string.Equals(ownerOfFirst.Name, ownerOfSecond.Name, StringComparison.Ordinal),
            $"{pair} of GlobalRecordId {globalRecordId}: both locked reads were followed by the writes of " +
            $"{ownerOfFirst.Name}; expected one trace for {first.Name} and one for {second.Name}.");

        bool firstTraceRanFirst = traces[0].AfterLock[0].Sequence < traces[1].AfterLock[0].Sequence;
        (SaveTrace earlier, SaveRequest earlierSave, SaveTrace later, SaveRequest laterSave) = firstTraceRanFirst
            ? (traces[0], ownerOfFirst, traces[1], ownerOfSecond)
            : (traces[1], ownerOfSecond, traces[0], ownerOfFirst);

        long earlierLast = earlier.AfterLock[^1].Sequence;
        List<RecordedCommand> interleaved = later.AfterLock.Where(command => command.Sequence <= earlierLast).ToList();
        Assert.True(
            interleaved.Count == 0,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{pair} of GlobalRecordId {globalRecordId}: the update lock of {earlierSave.Name}'s locked read of " +
                $"{BaseTable} is held to its commit, so every command {laterSave.Name} sends after its own locked " +
                $"read must follow #{earlierLast}, the last command of {earlierSave.Name}; expected 0 earlier, actual " +
                $"{interleaved.Count}:{DescribeCommands(interleaved)}{Environment.NewLine}{earlierSave.Name}:" +
                $"{DescribeCommands(earlier.All)}{Environment.NewLine}{laterSave.Name}:{DescribeCommands(later.All)}"));
        return laterSave;
    }

    // Splits the record-table commands of each connection at its locked base-row reads, one trace per save, since a
    // pooled connection can serve both saves of a pair one after the other. On each connection the first record-table
    // command must be a locked read, and each trace must follow the save path (AssertSavePathOrder).
    private static IReadOnlyList<SaveTrace> TraceSaves(
        IReadOnlyList<RecordedCommand> commands,
        string globalRecordId,
        int uniqueKey,
        string pair)
    {
        var traces = new List<SaveTrace>();
        foreach (IGrouping<Guid, RecordedCommand> connection in commands
                     .Where(IsRecordCommand)
                     .GroupBy(command => command.ClientConnectionId))
        {
            List<RecordedCommand> ordered = connection.OrderBy(command => command.Sequence).ToList();
            Assert.True(
                IsLockedBaseRead(ordered[0]),
                $"{pair} of GlobalRecordId {globalRecordId}: the first command naming a record table on connection " +
                $"{connection.Key:D} must be the locked read of {BaseTable}, a SELECT with the {UpdateLockHint} hint; " +
                $"actual {ordered[0].Describe(includeParameterValues: false)}. The connection sent:" +
                DescribeCommands(ordered));

            int start = 0;
            for (int index = 1; index <= ordered.Count; index++)
            {
                if (index == ordered.Count || IsLockedBaseRead(ordered[index]))
                {
                    traces.Add(new SaveTrace(ordered[start], ordered.GetRange(start + 1, index - start - 1)));
                    start = index;
                }
            }
        }

        foreach (SaveTrace trace in traces)
        {
            AssertSavePathOrder(trace, globalRecordId, uniqueKey, pair);
        }

        return traces;
    }

    // The save path of one trace: the locked read of the record, in the transaction and successful; then, before its
    // first write, reads only, which together read the record's rows of both page tables, each page table named by a
    // read that binds the base row's UniqueKey and GlobalRecordId, the input of the overlay and the required gate; then
    // at least one write; every command in the transaction and successful.
    private static void AssertSavePathOrder(SaveTrace trace, string globalRecordId, int uniqueKey, string pair)
    {
        RecordedCommand lockedRead = trace.LockedRead;
        List<RecordedCommand> beforeFirstWrite = trace.AfterLock.TakeWhile(command => !command.IsWrite).ToList();
        string[] unread = PageTables
            .Where(table => !beforeFirstWrite.Any(command =>
                NamesAny(command, [table]) && Binds(command, globalRecordId) && Binds(command, uniqueKey)))
            .ToArray();
        bool expected =
            lockedRead.InTransaction && lockedRead.Succeeded && Binds(lockedRead, globalRecordId) &&
            unread.Length == 0 &&
            beforeFirstWrite.Count < trace.AfterLock.Count &&
            trace.AfterLock.All(command => command.InTransaction && command.Succeeded);
        Assert.True(
            expected,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{pair} of GlobalRecordId {globalRecordId}: on connection {lockedRead.ClientConnectionId:D} a save " +
                $"must send its locked read of {BaseTable} for that GlobalRecordId; then, before its first write, " +
                $"only reads, which read the record's rows of {PageOneTable} and {PageTwoTable}, each table named by " +
                $"a read that binds {UniqueKeyColumn} {uniqueKey} and that GlobalRecordId; then its writes; every one " +
                $"inside its transaction and successful. Page tables not read so before the first write: " +
                $"[{string.Join(", ", unread)}]; {beforeFirstWrite.Count} of {trace.AfterLock.Count} commands after " +
                $"the locked read precede the first write. It sent:{DescribeCommands(trace.All)}"));
    }

    // The save whose submitted text the trace's writes bind: exactly one of the two.
    private static SaveRequest OwnerOf(
        SaveTrace trace,
        SaveRequest first,
        SaveRequest second,
        string globalRecordId,
        string pair)
    {
        List<RecordedCommand> writes = trace.AfterLock.Where(command => command.IsWrite).ToList();
        bool bindsFirst = writes.Any(command => BindsAnyValueOf(command, first));
        bool bindsSecond = writes.Any(command => BindsAnyValueOf(command, second));
        Assert.True(
            bindsFirst != bindsSecond,
            $"{pair} of GlobalRecordId {globalRecordId}: the writes after the locked read on connection " +
            $"{trace.LockedRead.ClientConnectionId:D} must bind the values of exactly one save; actual " +
            $"{first.Name} {(bindsFirst ? "bound" : "not bound")}, {second.Name} " +
            $"{(bindsSecond ? "bound" : "not bound")}:{DescribeCommands(writes)}");
        return bindsFirst ? first : second;
    }

    private static bool BindsAnyValueOf(RecordedCommand command, SaveRequest save) =>
        save.Values.Values.OfType<string>().Any(value => Binds(command, value));

    private static bool Binds(RecordedCommand command, string value) =>
        command.Parameters.Any(
            parameter => parameter.Value is string bound && string.Equals(bound, value, StringComparison.Ordinal));

    // UniqueKey is an int column, which the save path binds as an Int32.
    private static bool Binds(RecordedCommand command, int value) =>
        command.Parameters.Any(parameter => parameter.Value is int bound && bound == value);

    // Whether the command names one of the form's record tables, base or page, as a bracketed identifier.
    private static bool IsRecordCommand(RecordedCommand command) => NamesAny(command, RecordTables);

    // The save path's locked read of the base row: not a write, and a SELECT from the base table with UPDLOCK.
    private static bool IsLockedBaseRead(RecordedCommand command) =>
        !command.IsWrite && LockedBaseRead().IsMatch(command.CommandText);

    private static bool NamesAny(RecordedCommand command, string[] tables) =>
        SqlCommandRecorder.BracketedIdentifiers(command.CommandText)
            .Any(name => tables.Contains(name, StringComparer.OrdinalIgnoreCase));

    // Commands by sequence, connection, transaction, outcome and text, never by a parameter value.
    private static string DescribeCommands(IEnumerable<RecordedCommand> commands)
    {
        var text = new StringBuilder();
        foreach (RecordedCommand command in commands)
        {
            text.AppendLine().Append("  ").Append(command.Describe(includeParameterValues: false));
        }

        return text.Length == 0 ? " none" : text.ToString();
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

    // The record's clustered key, an int column of the base table.
    private static int StoredUniqueKey(SqlSnapshot snapshot, string globalRecordId)
    {
        object? stored = StoredCell(snapshot, BaseTable, UniqueKeyColumn, globalRecordId);
        return stored is int key
            ? key
            : throw new XunitException(
                $"{BaseTable}.{UniqueKeyColumn} of GlobalRecordId {globalRecordId}: expected an int, actual " +
                $"{Describe(stored)}.");
    }

    // A text cell the test wrote through the API, which therefore holds text.
    private static string StoredText(SqlSnapshot snapshot, string table, string column, string globalRecordId)
    {
        object? stored = StoredCell(snapshot, table, column, globalRecordId);
        return stored as string
            ?? throw new XunitException(
                $"{table}.{column} of GlobalRecordId {globalRecordId}: expected the text the test wrote, actual " +
                $"{Describe(stored)}.");
    }

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

    // The save path's locked read of the base row: a SELECT from the base table, optionally dbo-qualified and aliased,
    // whose table hints include UPDLOCK; keywords in any case.
    [GeneratedRegex(
        @"\A\s*SELECT\b.*?\bFROM\s+(?:\[dbo\]\s*\.\s*)?\[" + BaseTable +
        @"\](?:\s+(?:AS\s+)?[A-Za-z_][A-Za-z0-9_]*)?\s+WITH\s*\([^)]*\b" + UpdateLockHint + @"\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex LockedBaseRead();

    /// <summary>One save of a concurrent pair.</summary>
    /// <param name="Name">How failure messages name the save, for example <c>PUT A</c>.</param>
    /// <param name="Values">The <c>values</c> object the save submits.</param>
    private sealed record SaveRequest(string Name, Dictionary<string, object?> Values);

    /// <summary>A save of the gated pair and its request, still in flight while the gate is held.</summary>
    /// <param name="Request">The save.</param>
    /// <param name="Response">The <c>PUT</c> that sends it.</param>
    private sealed record InFlightSave(SaveRequest Request, Task<HttpResponseMessage> Response);

    /// <summary>One save's record-table commands on its connection, from its locked base-row read on.</summary>
    /// <param name="LockedRead">The locked read of the base row that starts the save's record commands.</param>
    /// <param name="AfterLock">The save's later record-table commands, in sequence order.</param>
    private sealed record SaveTrace(RecordedCommand LockedRead, IReadOnlyList<RecordedCommand> AfterLock)
    {
        public IEnumerable<RecordedCommand> All => AfterLock.Prepend(LockedRead);
    }

    /// <summary>A session waiting for a lock, and the session it waits on.</summary>
    /// <param name="Session">The waiting session's id.</param>
    /// <param name="BlockedBy">The id of the session holding the lock it waits for, or 0.</param>
    private sealed record WaitingSession(short Session, short BlockedBy)
    {
        public override string ToString() =>
            string.Create(CultureInfo.InvariantCulture, $"session {Session} waits on {BlockedBy}");
    }

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
