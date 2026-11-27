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
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace EpiInfo.Api.Tests.Persistence;

/// <summary>
/// SC-7, transactional save: a <c>FoodHistory</c> insert whose last page-table statement fails leaves no partial
/// record, because the base row and every page row are written in one transaction that rolls back, and the API
/// reports the failure as 503 with <c>writeOutcome</c> <c>notWritten</c>.
/// </summary>
/// <remarks>
/// <para>
/// Legacy Epi Info sends the base and page inserts without a transaction, so a failure part-way leaves a base row
/// without its page rows. Here a second session holds <c>TABLOCKX</c> on <c>FoodHistory3</c>, the host's command
/// timeout is 2 seconds, and the API's insert succeeds on <c>FoodHistory</c> and <c>FoodHistory2</c>, then times out
/// on <c>FoodHistory3</c> with SQL error -2. A timeout is unavailability and a completed rollback writes nothing, so
/// the response is 503 with <c>writeOutcome</c> <c>notWritten</c>.
/// </para>
/// <para>
/// A save the store refuses before any record statement reports <c>notWritten</c> too, whatever its status: a
/// <c>POST</c> and a <c>PUT</c> that submit a field the API's metadata read found writable, but that the store's
/// transaction, reading the metadata again, finds read-only, as when the field is made read-only between the two
/// reads. Each is a 500 with <c>writeOutcome</c> <c>notWritten</c>, and nothing is stored. The host's store returns
/// its own metadata read with the seeded read-only <c>OnsetWeek</c> writable, so no metadata row changes; the update
/// names an id no row holds.
/// </para>
/// <para>
/// The tests drive the API over HTTP on the SQL Server store, follow the requests' statements through
/// <see cref="ApiFactoryBase.Recorder"/>, and verify with direct SQL; they use no <c>EpiInfo.Data</c> internals. Each
/// holds the exclusive <see cref="DatabaseWriteLock"/> from the first count to the last verification read, so no other
/// test process writes to the three tables in between and the counts can be compared exactly.
/// </para>
/// <para>
/// Failure messages describe commands with <see cref="RecordedCommand.Describe"/> without parameter values, and
/// responses by their status, media type and member names; they never print the marker, a bound value or a body.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(DatabaseCollections.Database)]
public sealed partial class TransactionalSaveTests : IAsyncLifetime
{
    private const string FormName = "FoodHistory";

    private const string BaseTable = "FoodHistory";

    // The page table of LastName, nvarchar(4000) (testdata/sql/05_data_tables.sql).
    private const string PageOneTable = "FoodHistory2";

    // The last page table the insert writes, in metaPages.Position order; it receives a row even with no value.
    private const string PageTwoTable = "FoodHistory3";

    private const string ProblemMediaType = "application/problem+json";

    private const string NotWritten = "notWritten";

    // Short enough that the blocked FoodHistory3 insert fails quickly, long enough for every unblocked statement.
    private const int CommandTimeoutSeconds = 2;

    // SqlException.Number of a command timeout.
    private const int TimeoutErrorNumber = -2;

    private const int GlobalRecordIdLength = 255;

    private const int MarkerColumnLength = 4000;

    // FoodHistory2's seeded read-only number field, which check code assigns (testdata/sql/03_forms.sql), and a value
    // its float column holds.
    private const string ReadOnlyField = "OnsetWeek";

    private const int ReadOnlyFieldValue = 17;

    private const string TakeTableLockSql =
        "SELECT COUNT(*) FROM [dbo].[" + PageTwoTable + "] WITH (TABLOCKX, HOLDLOCK);";

    // Object ids are unique only within a database, so the lock is matched in the current database. The object name
    // and the lock's resource type, mode and status are bound as parameters.
    private const string TableLockHeldSql =
        """
        SELECT COUNT(*)
        FROM sys.dm_tran_locks
        WHERE request_session_id = @@SPID
          AND resource_database_id = DB_ID()
          AND resource_type = @resourceType
          AND resource_associated_entity_id = OBJECT_ID(@objectName)
          AND request_mode = @requestMode
          AND request_status = @requestStatus;
        """;

    // The sys.dm_tran_locks values of a granted exclusive lock on a whole object.
    private const string ObjectResourceType = "OBJECT";

    private const string ExclusiveMode = "X";

    private const string GrantedStatus = "GRANT";

    // sys.dm_tran_locks declares resource_type, request_mode and request_status as nvarchar(60).
    private const int LockColumnLength = 60;

    // nvarchar(776), the size SQL Server's own procedures, such as sp_help, give a multi-part object name.
    private const int ObjectNameLength = 776;

    private static readonly string[] Tables = [BaseTable, PageOneTable, PageTwoTable];

    // The wire values of writeOutcome; any other string is described, never quoted.
    private static readonly string[] WriteOutcomes = [NotWritten, "unknown", "written"];

    // The media types a failure message names; any other value is described, never quoted.
    private static readonly string[] KnownMediaTypes =
        [ApiTestClient.JsonMediaType, ProblemMediaType, "text/plain", "text/html"];

    // Longer than DatabaseWriteLock's 300-second wait, so a hung request fails the test instead of the whole run.
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(10);

    private ApiFactory? _factory;

    private HttpClient? _client;

    private ApiFactory Factory =>
        _factory ?? throw new InvalidOperationException("The API host is created in InitializeAsync.");

    private HttpClient Client =>
        _client ?? throw new InvalidOperationException("The API client is created in InitializeAsync.");

    /// <summary>
    /// Proves the rehearsal database reachable, then starts the API host on it with a 2-second command timeout. A
    /// database that is not configured or not reachable fails the test with the <see cref="TestDatabase"/> message; it
    /// never skips.
    /// </summary>
    public async Task InitializeAsync()
    {
        await TestDatabase.EnsureReachableAsync();
        _factory = await ApiFactory.ForDatabaseAsync(
            new ApiFactoryOptions { CommandTimeoutSeconds = CommandTimeoutSeconds });
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
    public async Task Insert_that_times_out_on_the_last_page_rolls_back_every_row()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        CancellationToken ct = timeout.Token;
        await using DatabaseWriteLock dbLock = await DatabaseWriteLock.AcquireExclusiveAsync(ct);

        // Resolved, and the host started, before the table lock is taken, so no setup read can wait on it.
        int viewId = await ApiTestClient.GetViewIdAsync(Client, FormName, ct);
        string path = string.Create(CultureInfo.InvariantCulture, $"/api/forms/{viewId}/records");
        string marker = TestDatabase.NewMarker();

        TableCounts before;
        await using (SqlConnection counting = await TestDatabase.OpenConnectionAsync(ct))
        {
            before = await ReadCountsAsync(counting, ct);
        }

        string attemptedGlobalRecordId;
        string? rollbackFailure;
        SqlConnection lockConnection = await TestDatabase.OpenConnectionAsync(ct);
        SqlTransaction? lockTransaction = null;
        try
        {
            lockTransaction = lockConnection.BeginTransaction();
            await TakePageTwoTableLockAsync(lockConnection, lockTransaction, ct);

            Factory.Recorder.Clear();
            var body = new { values = new Dictionary<string, object?> { [TestDatabase.MarkerField] = marker } };
            using (HttpResponseMessage response = await ApiTestClient.PostJsonAsync(Client, path, body, ct))
            {
                await AssertProblemAndNotWrittenAsync(
                    response,
                    "POST " + path,
                    HttpStatusCode.ServiceUnavailable,
                    $"the timed-out {PageTwoTable} insert",
                    ct);
            }

            attemptedGlobalRecordId = AssertInsertSequence(Factory.Recorder, marker);
        }
        finally
        {
            rollbackFailure = await ReleaseTableLockAsync(lockConnection, lockTransaction);
        }

        Assert.True(
            rollbackFailure is null,
            $"Rolling back the transaction that held the {PageTwoTable} table lock failed with {rollbackFailure}.");

        await AssertNothingStoredAsync(attemptedGlobalRecordId, marker, before, ct);
    }

    [Fact]
    public async Task Save_of_a_field_made_read_only_after_the_metadata_read_is_not_written()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        CancellationToken ct = timeout.Token;
        await using DatabaseWriteLock dbLock = await DatabaseWriteLock.AcquireExclusiveAsync(ct);

        await using WebApplicationFactory<Program> host = Factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => UseWritableFieldStore(services, ReadOnlyField)));
        using HttpClient client = host.CreateClient();

        int viewId = await ApiTestClient.GetViewIdAsync(client, FormName, ct);
        string createPath = string.Create(CultureInfo.InvariantCulture, $"/api/forms/{viewId}/records");
        string updatePath = string.Create(CultureInfo.InvariantCulture, $"{createPath}/{Guid.NewGuid()}");
        string marker = TestDatabase.NewMarker();
        var body = new
        {
            values = new Dictionary<string, object?>
            {
                [TestDatabase.MarkerField] = marker,
                [ReadOnlyField] = ReadOnlyFieldValue,
            },
        };

        TableCounts before;
        await using (SqlConnection counting = await TestDatabase.OpenConnectionAsync(ct))
        {
            before = await ReadCountsAsync(counting, ct);
        }

        Factory.Recorder.Clear();
        using (HttpResponseMessage response = await ApiTestClient.PostJsonAsync(client, createPath, body, ct))
        {
            await AssertProblemAndNotWrittenAsync(
                response,
                "POST " + createPath,
                HttpStatusCode.InternalServerError,
                "the store's refusal of the read-only field",
                ct);
        }

        AssertNoRecordStatement(Factory.Recorder, "POST " + createPath);

        Factory.Recorder.Clear();
        using (HttpResponseMessage response = await ApiTestClient.PutJsonAsync(client, updatePath, body, ct))
        {
            await AssertProblemAndNotWrittenAsync(
                response,
                "PUT " + updatePath,
                HttpStatusCode.InternalServerError,
                "the store's refusal of the read-only field",
                ct);
        }

        AssertNoRecordStatement(Factory.Recorder, "PUT " + updatePath);

        await using SqlConnection connection = await TestDatabase.OpenConnectionAsync(ct);
        TableCounts after = await ReadCountsAsync(connection, ct);
        AssertCountUnchanged(BaseTable, before.Base, after.Base);
        AssertCountUnchanged(PageOneTable, before.PageOne, after.PageOne);
        AssertCountUnchanged(PageTwoTable, before.PageTwo, after.PageTwo);
        int markerRows = await CountMatchesAsync(
            connection, PageOneTable, TestDatabase.MarkerField, marker, MarkerColumnLength, ct);
        Assert.True(
            markerRows == 0,
            string.Create(
                CultureInfo.InvariantCulture,
                $"[{PageOneTable}] holds {markerRows} row(s) with the request's {TestDatabase.MarkerField} marker."));
    }

    // Replaces the host's one IDataStore registration, the SQL Server store built by a factory, by a WritableFieldStore
    // around the store that factory builds, with the same lifetime.
    private static void UseWritableFieldStore(IServiceCollection services, string fieldName)
    {
        ServiceDescriptor[] registrations = services
            .Where(descriptor => descriptor.ServiceType == typeof(IDataStore) && !descriptor.IsKeyedService)
            .ToArray();
        if (registrations.Length != 1 || registrations[0].ImplementationFactory is not { } createReal)
        {
            throw new InvalidOperationException(string.Create(
                CultureInfo.InvariantCulture,
                $"The host registers {registrations.Length} {nameof(IDataStore)} services; expected exactly one, " +
                $"built by a factory."));
        }

        ServiceDescriptor real = registrations[0];
        services.Remove(real);
        services.Add(new ServiceDescriptor(
            typeof(IDataStore),
            provider => new WritableFieldStore((IDataStore)createReal(provider), fieldName),
            real.Lifetime));
    }

    // No statement of the request wrote or named a record table: the store refused the write after its metadata reads.
    private static void AssertNoRecordStatement(SqlCommandRecorder recorder, string request)
    {
        List<RecordedCommand> reached = recorder.Commands
            .Where(command => command.IsWrite || SqlCommandRecorder.BracketedIdentifiers(command.CommandText)
                .Any(name => Tables.Contains(name, StringComparer.OrdinalIgnoreCase)))
            .ToList();
        Assert.True(
            reached.Count == 0,
            $"{request} must send no statement that writes or names a {FormName} record table. It sent:" +
            Describe(reached));
    }

    // Takes an exclusive table lock on FoodHistory3 inside the open transaction and proves it granted, failing before
    // any request is sent when it is not.
    private static async Task TakePageTwoTableLockAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CancellationToken ct)
    {
        await using (SqlCommand take = connection.CreateCommand())
        {
            take.Transaction = transaction;
            take.CommandText = TakeTableLockSql;
            _ = await take.ExecuteScalarAsync(ct);
        }

        await using SqlCommand check = connection.CreateCommand();
        check.Transaction = transaction;
        check.CommandText = TableLockHeldSql;
        check.Parameters.Add(
            new SqlParameter("@resourceType", SqlDbType.NVarChar, LockColumnLength) { Value = ObjectResourceType });
        check.Parameters.Add(
            new SqlParameter("@objectName", SqlDbType.NVarChar, ObjectNameLength) { Value = "dbo." + PageTwoTable });
        check.Parameters.Add(
            new SqlParameter("@requestMode", SqlDbType.NVarChar, LockColumnLength) { Value = ExclusiveMode });
        check.Parameters.Add(
            new SqlParameter("@requestStatus", SqlDbType.NVarChar, LockColumnLength) { Value = GrantedStatus });
        int granted = Assert.IsType<int>(await check.ExecuteScalarAsync(ct));
        Assert.True(
            granted >= 1,
            $"The lock session holds no granted X lock on the dbo.{PageTwoTable} object after a TABLOCKX, HOLDLOCK " +
            "read in its transaction, so the insert could not be made to time out; no request was sent.");
    }

    // Rolls the lock transaction back, then disposes it and its connection. A failed rollback is returned, not thrown,
    // so it never replaces an assertion raised while the lock was held; closing the connection rolls back a pending
    // transaction, so the table lock is released either way.
    private static async Task<string?> ReleaseTableLockAsync(SqlConnection connection, SqlTransaction? transaction)
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

    // The expected status as application/problem+json, a complete ProblemDetails with that status, and writeOutcome
    // exactly notWritten. request is the method and path, and cause the failure the status is expected from; both are
    // test text, named in failure messages.
    private static async Task AssertProblemAndNotWrittenAsync(
        HttpResponseMessage response,
        string request,
        HttpStatusCode expected,
        string cause,
        CancellationToken ct)
    {
        int status = (int)expected;
        if (response.StatusCode != expected)
        {
            string text = await response.Content.ReadAsStringAsync(ct);
            Assert.Fail(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{request} returned {(int)response.StatusCode} {response.StatusCode}, expected {status} " +
                    $"{expected} from {cause}; {DescribeMediaType(response)}, body {DescribeBody(text)}."));
        }

        Assert.True(
            string.Equals(
                response.Content.Headers.ContentType?.MediaType,
                ProblemMediaType,
                StringComparison.OrdinalIgnoreCase),
            string.Create(
                CultureInfo.InvariantCulture,
                $"{request} returned {status} with {DescribeMediaType(response)}, expected {ProblemMediaType}."));

        JsonElement problem = ApiTestClient.AssertProblemDetails(
            await ApiTestClient.ReadJsonElementAsync(response, ct),
            status);
        Assert.True(
            problem.TryGetProperty("writeOutcome", out JsonElement outcome) &&
            outcome.ValueKind == JsonValueKind.String &&
            outcome.ValueEquals(NotWritten),
            string.Create(
                CultureInfo.InvariantCulture,
                $"The {status} ProblemDetails member 'writeOutcome' must be the String \"{NotWritten}\"; it is " +
                $"{DescribeWriteOutcome(problem)}."));
    }

    // The request's statements on its own connection, from the base insert on, must be exactly the three inserts in
    // page order inside one transaction: two that succeeded and the FoodHistory3 insert that timed out. Returns the
    // GlobalRecordId the attempt bound.
    private static string AssertInsertSequence(SqlCommandRecorder recorder, string marker)
    {
        IReadOnlyList<RecordedCommand> commands = recorder.Commands;
        List<RecordedCommand> baseInserts = commands
            .Where(command => command.IsWrite && IsInsertInto(command, BaseTable))
            .ToList();
        Assert.True(
            baseInserts.Count == 1,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Expected exactly one INSERT into [{BaseTable}], because the API never retries; the request " +
                $"recorded {baseInserts.Count}. Commands:{Describe(commands)}"));
        RecordedCommand baseInsert = baseInserts[0];

        IReadOnlyList<RecordedCommand> onConnection = recorder.ForConnection(baseInsert.ClientConnectionId);
        List<RecordedCommand> writes = onConnection
            .Where(command => command.Sequence >= baseInsert.Sequence && command.IsWrite)
            .ToList();
        bool expected =
            writes.Count == 3 &&
            IsInsertInto(writes[0], BaseTable) && writes[0].Succeeded &&
            IsInsertInto(writes[1], PageOneTable) && writes[1].Succeeded &&
            IsInsertInto(writes[2], PageTwoTable) && writes[2].Completed && !writes[2].Succeeded &&
            writes[2].SqlErrorNumber == TimeoutErrorNumber &&
            string.Equals(writes[2].ExceptionType, typeof(SqlException).FullName, StringComparison.Ordinal);
        Assert.True(
            expected,
            string.Create(
                CultureInfo.InvariantCulture,
                $"From the base insert on, the request's connection must send exactly: INSERT into [{BaseTable}], " +
                $"succeeded; INSERT into [{PageOneTable}], succeeded; INSERT into [{PageTwoTable}], failed with " +
                $"SqlException {TimeoutErrorNumber}; and no later write. It recorded:{Describe(onConnection)}"));
        Assert.True(
            writes.All(command => command.InTransaction),
            $"Every insert of the attempt must run inside the save transaction. It recorded:{Describe(writes)}");

        // Builders bind positional names (@p0, ...), so the id is found by its value: the base insert binds no other
        // string that parses as a GUID.
        List<string> ids = baseInsert.Parameters
            .Select(parameter => parameter.Value)
            .OfType<string>()
            .Where(value => Guid.TryParse(value, out _))
            .ToList();
        Assert.True(
            ids.Count == 1,
            string.Create(
                CultureInfo.InvariantCulture,
                $"The [{BaseTable}] insert binds {ids.Count} string parameters that parse as a GUID; expected " +
                $"exactly one, the new GlobalRecordId. {baseInsert.Describe(includeParameterValues: false)}"));
        string globalRecordId = ids[0];

        foreach (RecordedCommand pageInsert in writes.Skip(1))
        {
            Assert.True(
                Binds(pageInsert, globalRecordId),
                $"A page insert does not bind the GlobalRecordId of the [{BaseTable}] insert. " +
                pageInsert.Describe(includeParameterValues: false));
        }

        Assert.True(
            Binds(writes[1], marker),
            $"The [{PageOneTable}] insert does not bind the request's {TestDatabase.MarkerField} marker. " +
            writes[1].Describe(includeParameterValues: false));

        return globalRecordId;
    }

    // With the table lock released, a fresh connection finds no row of the attempt and every count unchanged.
    private static async Task AssertNothingStoredAsync(
        string globalRecordId,
        string marker,
        TableCounts before,
        CancellationToken ct)
    {
        await using SqlConnection connection = await TestDatabase.OpenConnectionAsync(ct);

        foreach (string table in Tables)
        {
            int rows = await CountMatchesAsync(
                connection, table, "GlobalRecordId", globalRecordId, GlobalRecordIdLength, ct);
            Assert.True(
                rows == 0,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"[{table}] holds {rows} row(s) with the attempted GlobalRecordId; the failed insert left a " +
                    $"partial record."));
        }

        TableCounts after = await ReadCountsAsync(connection, ct);
        AssertCountUnchanged(BaseTable, before.Base, after.Base);
        AssertCountUnchanged(PageOneTable, before.PageOne, after.PageOne);
        AssertCountUnchanged(PageTwoTable, before.PageTwo, after.PageTwo);

        int markerRows = await CountMatchesAsync(
            connection, PageOneTable, TestDatabase.MarkerField, marker, MarkerColumnLength, ct);
        Assert.True(
            markerRows == 0,
            string.Create(
                CultureInfo.InvariantCulture,
                $"[{PageOneTable}] holds {markerRows} row(s) with the request's {TestDatabase.MarkerField} marker."));
    }

    private static void AssertCountUnchanged(string table, int before, int after) =>
        Assert.True(
            before == after,
            string.Create(
                CultureInfo.InvariantCulture,
                $"[{table}] held {before} rows before the failed write and {after} after; it must be unchanged."));

    private static async Task<TableCounts> ReadCountsAsync(SqlConnection connection, CancellationToken ct) =>
        new(
            await CountAsync(connection, BaseTable, ct),
            await CountAsync(connection, PageOneTable, ct),
            await CountAsync(connection, PageTwoTable, ct));

    // The table is one of this class's constants, never input, so it is written into the text.
    private static async Task<int> CountAsync(SqlConnection connection, string table, CancellationToken ct)
    {
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM [dbo].[{table}];";
        return Assert.IsType<int>(await command.ExecuteScalarAsync(ct));
    }

    // Rows of a constant table whose constant column equals value, bound as an nvarchar parameter of the column's size.
    private static async Task<int> CountMatchesAsync(
        SqlConnection connection,
        string table,
        string column,
        string value,
        int size,
        CancellationToken ct)
    {
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM [dbo].[{table}] WHERE [{column}] = @value;";
        command.Parameters.Add(new SqlParameter("@value", SqlDbType.NVarChar, size) { Value = value });
        return Assert.IsType<int>(await command.ExecuteScalarAsync(ct));
    }

    private static bool IsInsertInto(RecordedCommand command, string table) =>
        string.Equals(TargetTable(command), table, StringComparison.OrdinalIgnoreCase);

    // The table an INSERT INTO statement names, optionally dbo-qualified, unescaped; null for any other statement.
    private static string? TargetTable(RecordedCommand command) =>
        InsertIntoBracketedTable().IsMatch(command.CommandText)
            ? SqlCommandRecorder.BracketedIdentifiers(command.CommandText)
                .FirstOrDefault(name => !string.Equals(name, "dbo", StringComparison.OrdinalIgnoreCase))
            : null;

    private static bool Binds(RecordedCommand command, string value) =>
        command.Parameters.Any(
            parameter => parameter.Value is string bound && string.Equals(bound, value, StringComparison.Ordinal));

    private static string Describe(IEnumerable<RecordedCommand> commands)
    {
        var text = new StringBuilder();
        foreach (RecordedCommand command in commands)
        {
            text.AppendLine().Append("  ").Append(command.Describe(includeParameterValues: false));
        }

        return text.Length == 0 ? " none" : text.ToString();
    }

    // The media type, named only when it is a known one; any other value is server text.
    private static string DescribeMediaType(HttpResponseMessage response)
    {
        string? mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is null)
        {
            return "no media type";
        }

        string? known = KnownMediaTypes.FirstOrDefault(
            candidate => string.Equals(candidate, mediaType, StringComparison.OrdinalIgnoreCase));
        return known is null ? "another media type" : "media type " + known;
    }

    // A body by its shape: its JSON kind, the names of its members and of its errors entries when they have the form
    // of an identifier, and a known writeOutcome. A response body can carry field values, so no value is quoted.
    private static string DescribeBody(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "empty";
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return $"a JSON {root.ValueKind}";
            }

            var description = new StringBuilder("a JSON object with members ").Append(DescribeNames(root));
            if (root.TryGetProperty("errors", out JsonElement errors) && errors.ValueKind == JsonValueKind.Object)
            {
                description.Append(", errors for ").Append(DescribeNames(errors));
            }

            if (root.TryGetProperty("writeOutcome", out _))
            {
                description.Append(", writeOutcome ").Append(DescribeWriteOutcome(root));
            }

            return description.ToString();
        }
        catch (JsonException)
        {
            return string.Create(CultureInfo.InvariantCulture, $"of {text.Length} characters that is not JSON");
        }
    }

    private static string DescribeNames(JsonElement element)
    {
        var names = new List<string>();
        int others = 0;
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (Identifier().IsMatch(property.Name))
            {
                names.Add(property.Name);
            }
            else
            {
                others++;
            }
        }

        string rest = others == 0
            ? string.Empty
            : string.Create(CultureInfo.InvariantCulture, $" and {others} other name(s)");
        return $"[{string.Join(", ", names)}]{rest}";
    }

    private static string DescribeWriteOutcome(JsonElement problem)
    {
        if (!problem.TryGetProperty("writeOutcome", out JsonElement outcome))
        {
            return "missing";
        }

        if (outcome.ValueKind != JsonValueKind.String)
        {
            return $"of kind {outcome.ValueKind}";
        }

        string? known = WriteOutcomes.FirstOrDefault(value => outcome.ValueEquals(value));
        return known is null ? "a String that is not a write outcome" : $"\"{known}\"";
    }

    // INSERT INTO followed by a bracketed table name, optionally dbo-qualified; the keywords in any case.
    [GeneratedRegex(
        @"\A\s*INSERT\s+INTO\s+(?:\[dbo\]\s*\.\s*)?\[",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InsertIntoBracketedTable();

    // The form of a DTO member or Epi Info field name: a letter, then up to 63 letters, digits or underscores.
    [GeneratedRegex(@"\A[A-Za-z][A-Za-z0-9_]{0,63}\z")]
    private static partial Regex Identifier();

    private readonly record struct TableCounts(int Base, int PageOne, int PageTwo);

    /// <summary>
    /// The host's real store, except that a form's metadata read returns the named field writable, as a caller holds
    /// metadata it read before the field was made read-only. Every other call, and every argument and result, passes
    /// through unchanged, so the real store's write transaction reads the metadata again and finds the field
    /// read-only.
    /// </summary>
    /// <param name="real">The store every call is forwarded to.</param>
    /// <param name="fieldName">The canonical name of the field returned writable.</param>
    private sealed class WritableFieldStore(IDataStore real, string fieldName) : IDataStore
    {
        public async Task<FormMetadata?> GetFormMetadataAsync(int viewId, CancellationToken cancellationToken)
        {
            FormMetadata? form = await real.GetFormMetadataAsync(viewId, cancellationToken);
            return form is null
                ? null
                : form with
                {
                    Fields =
                    [
                        .. form.Fields.Select(field => string.Equals(field.Name, fieldName, StringComparison.Ordinal)
                            ? field with { IsReadOnly = false }
                            : field),
                    ],
                };
        }

        public Task<IReadOnlyList<FormSummary>> ListFormsAsync(CancellationToken cancellationToken) =>
            real.ListFormsAsync(cancellationToken);

        public Task<IReadOnlyDictionary<string, IReadOnlyList<FieldOption>>> GetOptionsAsync(
            FormMetadata form,
            CancellationToken cancellationToken) =>
            real.GetOptionsAsync(form, cancellationToken);

        public Task<string?> GetCheckCodeAsync(int viewId, CancellationToken cancellationToken) =>
            real.GetCheckCodeAsync(viewId, cancellationToken);

        public Task<int> CountAsync(FormMetadata form, bool includeDeleted, CancellationToken cancellationToken) =>
            real.CountAsync(form, includeDeleted, cancellationToken);

        public Task<RecordDto?> GetAtPositionAsync(
            FormMetadata form,
            int position,
            bool includeDeleted,
            CancellationToken cancellationToken) =>
            real.GetAtPositionAsync(form, position, includeDeleted, cancellationToken);

        public Task<RecordDto?> GetByIdAsync(
            FormMetadata form,
            string globalRecordId,
            CancellationToken cancellationToken) =>
            real.GetByIdAsync(form, globalRecordId, cancellationToken);

        public Task<NavigationPosition?> GetSetPositionAsync(
            FormMetadata form,
            string globalRecordId,
            bool includeDeleted,
            CancellationToken cancellationToken) =>
            real.GetSetPositionAsync(form, globalRecordId, includeDeleted, cancellationToken);

        public Task<RecordPage> ListAsync(
            FormMetadata form,
            RecordListQuery query,
            CancellationToken cancellationToken) =>
            real.ListAsync(form, query, cancellationToken);

        public Task<SearchResponse> SearchAsync(
            FormMetadata form,
            IReadOnlyList<SearchCondition> conditions,
            bool includeDeleted,
            string? excludeGlobalRecordId,
            CancellationToken cancellationToken) =>
            real.SearchAsync(form, conditions, includeDeleted, excludeGlobalRecordId, cancellationToken);

        public Task<bool> PingAsync(CancellationToken cancellationToken) => real.PingAsync(cancellationToken);

        public Task<RecordDto> InsertAsync(
            FormMetadata form,
            IReadOnlyDictionary<string, FieldValue> values,
            SaveStamp stamp,
            CancellationToken cancellationToken) =>
            real.InsertAsync(form, values, stamp, cancellationToken);

        public Task<RecordWriteResult> UpdateAsync(
            FormMetadata form,
            string globalRecordId,
            IReadOnlyDictionary<string, FieldValue> submitted,
            Func<SaveStamp> createStamp,
            Func<StoredRecord, RecordValidationResult> requiredGate,
            FormPermissions? permissions,
            CancellationToken cancellationToken) =>
            real.UpdateAsync(
                form,
                globalRecordId,
                submitted,
                createStamp,
                requiredGate,
                permissions,
                cancellationToken);

        public Task<SoftDeleteResult> SoftDeleteAsync(
            FormMetadata form,
            string globalRecordId,
            Func<SaveStamp> createStamp,
            CancellationToken cancellationToken) =>
            real.SoftDeleteAsync(form, globalRecordId, createStamp, cancellationToken);

        public Task<FormPermissions?> GetPermissionsAsync(FormMetadata form, CancellationToken cancellationToken) =>
            real.GetPermissionsAsync(form, cancellationToken);
    }
}
