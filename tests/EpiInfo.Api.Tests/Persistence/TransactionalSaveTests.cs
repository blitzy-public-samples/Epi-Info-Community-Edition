using System.Data;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EpiInfo.Api.Tests.Infrastructure;
using EpiInfo.Tests.Shared;
using Microsoft.Data.SqlClient;

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
/// The test drives the API over HTTP on the SQL Server store, follows the request's statements through
/// <see cref="ApiFactoryBase.Recorder"/>, and verifies with direct SQL; it uses no <c>EpiInfo.Data</c> internals. It
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

    private const string TakeTableLockSql =
        "SELECT COUNT(*) FROM [dbo].[" + PageTwoTable + "] WITH (TABLOCKX, HOLDLOCK);";

    // Object ids are unique only within a database, so the lock is matched in the current database.
    private const string TableLockHeldSql =
        """
        SELECT COUNT(*)
        FROM sys.dm_tran_locks
        WHERE request_session_id = @@SPID
          AND resource_database_id = DB_ID()
          AND resource_type = 'OBJECT'
          AND resource_associated_entity_id = OBJECT_ID(N'dbo.FoodHistory3')
          AND request_mode = 'X'
          AND request_status = 'GRANT';
        """;

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
                await AssertUnavailableAndNotWrittenAsync(response, path, ct);
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

    // 503 application/problem+json, a complete ProblemDetails with status 503, and writeOutcome exactly notWritten.
    private static async Task AssertUnavailableAndNotWrittenAsync(
        HttpResponseMessage response,
        string path,
        CancellationToken ct)
    {
        if (response.StatusCode != HttpStatusCode.ServiceUnavailable)
        {
            string text = await response.Content.ReadAsStringAsync(ct);
            Assert.Fail(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"POST {path} returned {(int)response.StatusCode} {response.StatusCode}, expected 503 " +
                    $"ServiceUnavailable from the timed-out {PageTwoTable} insert; {DescribeMediaType(response)}, " +
                    $"body {DescribeBody(text)}."));
        }

        Assert.True(
            string.Equals(
                response.Content.Headers.ContentType?.MediaType,
                ProblemMediaType,
                StringComparison.OrdinalIgnoreCase),
            $"POST {path} returned 503 with {DescribeMediaType(response)}, expected {ProblemMediaType}.");

        JsonElement problem = ApiTestClient.AssertProblemDetails(
            await ApiTestClient.ReadJsonElementAsync(response, ct),
            (int)HttpStatusCode.ServiceUnavailable);
        Assert.True(
            problem.TryGetProperty("writeOutcome", out JsonElement outcome) &&
            outcome.ValueKind == JsonValueKind.String &&
            outcome.ValueEquals(NotWritten),
            $"The 503 ProblemDetails member 'writeOutcome' must be the String \"{NotWritten}\"; it is " +
            $"{DescribeWriteOutcome(problem)}.");
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
                $"[{table}] held {before} rows before the failed insert and {after} after; it must be unchanged."));

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
}
