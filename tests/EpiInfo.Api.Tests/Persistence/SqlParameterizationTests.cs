using System.Data;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using EpiInfo.Api.Tests.Infrastructure;
using EpiInfo.Tests.Shared;
using Microsoft.Data.SqlClient;

namespace EpiInfo.Api.Tests.Persistence;

/// <summary>
/// Every value reaches SQL Server as a command parameter (AAP 0.7.6, "Parameterised SQL for every value"): a
/// test-created <c>CaseReport</c> record whose <c>LastName</c> holds a quote, a statement separator, <c>DROP TABLE</c>
/// and a <c>--</c> comment is stored verbatim, the same text finds exactly that record as the records-list <c>q</c>
/// and as a Find criterion, and <see cref="SqlCommandRecorder"/> shows the text in no command text, only in parameter
/// values.
/// </summary>
/// <remarks>
/// <para>
/// The test drives the API over HTTP on the SQL Server store and verifies with direct SQL; it uses no
/// <c>EpiInfo.Data</c> internals. It writes, so it holds the exclusive <see cref="DatabaseWriteLock"/> throughout
/// (AAP 0.7.5). Its only write is the one record it creates, which carries a fresh marker and stays in the database,
/// so reruns find exactly their own record.
/// </para>
/// <para>
/// Failure messages name a command by its sequence number and operation, and may quote its SQL text; they never
/// print a bound parameter value.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(DatabaseCollections.Database)]
public sealed partial class SqlParameterizationTests : IAsyncLifetime
{
    private const string FormName = "CaseReport";

    private const string BaseTable = "CaseReport";

    // The page table of LastName, nvarchar(4000) (testdata/sql/05_data_tables.sql).
    private const string PageTable = "CaseReport1";

    // Quote, statement separator, DDL and a line comment. Neither it nor the marker holds %, _, [, \, *, ?, a leading
    // comparison prefix or " or ", so the grid's "contains" filter and Find's equality both match it literally
    // (AAP 0.3.4.4).
    private const string HostileSuffix = "O'Brien'; DROP TABLE x;--";

    private const string QuotedFragment = "O'Brien'";

    private const string DropFragment = "DROP TABLE x";

    // The characters and prefixes that would give the payload a pattern, prefix or alternative meaning in q or Find.
    private static readonly char[] PatternCharacters = ['%', '_', '[', '\\', '*', '?'];

    private static readonly string[] ComparisonPrefixes = ["<=", ">=", "<>", ">", "<"];

    // Longer than DatabaseWriteLock's 300-second wait, so a hung request fails the test instead of the whole run.
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(10);

    private ApiFactory? _factory;

    private HttpClient? _client;

    // How a parameter value must relate to the payload for a command to count as carrying it.
    private enum PayloadBinding
    {
        // The value is the payload itself: the insert column and the Find equality.
        Exact,

        // The value contains the payload: the grid filter's escaped LIKE pattern.
        Contained,
    }

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        // An unconfigured or unreachable database fails the test with TestDatabase's guidance; it never skips.
        await TestDatabase.EnsureReachableAsync();
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

    [Fact]
    public async Task Hostile_text_is_stored_verbatim_and_reaches_SQL_only_as_a_parameter()
    {
        Assert.NotNull(_factory);
        Assert.NotNull(_client);
        var recorder = _factory.Recorder;
        var client = _client;

        using var timeout = new CancellationTokenSource(TestTimeout);
        var ct = timeout.Token;
        await using var dbLock = await DatabaseWriteLock.AcquireExclusiveAsync(ct);

        var viewId = await ApiTestClient.GetViewIdAsync(client, FormName, ct);
        var payload = TestDatabase.NewMarker() + HostileSuffix;
        AssertMatchesLiterally(payload);

        // Create.
        recorder.Clear();
        var created = await ApiTestClient.CreateRecordAsync(
            client,
            viewId,
            new Dictionary<string, object?> { [TestDatabase.MarkerField] = payload },
            ct);
        var createCommands = recorder.Commands;
        Assert.Equal(payload, ValueOf(created.Record, TestDatabase.MarkerField));

        // Stored verbatim, read back with the id and the payload as parameters.
        await AssertStoredVerbatimAsync(created.GlobalRecordId, payload, ct);

        // The records-list q; LastName is a grid column of CaseReport (AAP 0.3.4.3).
        recorder.Clear();
        var listPath = string.Create(
            CultureInfo.InvariantCulture,
            $"/api/forms/{viewId}/records?q={Uri.EscapeDataString(payload)}");
        JsonElement list;
        using (var response = await client.GetAsync(listPath, ct))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            list = await ApiTestClient.ReadJsonElementAsync(response, ct);
        }

        var listCommands = recorder.Commands;
        Assert.Equal(1, Property(list, "total").GetInt32());
        AssertOnlyRecord(list, created.GlobalRecordId, payload);

        // A Find criterion on LastName.
        recorder.Clear();
        var searchPath = string.Create(CultureInfo.InvariantCulture, $"/api/forms/{viewId}/records/search");
        var searchBody = new { criteria = new[] { new { field = TestDatabase.MarkerField, value = payload } } };
        JsonElement search;
        using (var response = await ApiTestClient.PostJsonAsync(client, searchPath, searchBody, ct))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            search = await ApiTestClient.ReadJsonElementAsync(response, ct);
        }

        var searchCommands = recorder.Commands;
        Assert.Equal(1, Property(search, "total").GetInt32());
        Assert.Equal(JsonValueKind.False, Property(search, "truncated").ValueKind);
        AssertOnlyRecord(search, created.GlobalRecordId, payload);

        // The text travelled only as a parameter value, and the insert that bound it wrote the page table.
        AssertTravelsOnlyAsParameter(
            "POST records", createCommands, payload, PayloadBinding.Exact, mustWritePageTable: true);
        AssertTravelsOnlyAsParameter(
            "GET records?q", listCommands, payload, PayloadBinding.Contained, mustWritePageTable: false);
        AssertTravelsOnlyAsParameter(
            "POST records/search", searchCommands, payload, PayloadBinding.Exact, mustWritePageTable: false);

        await AssertTablesExistAsync(ct);
    }

    // The payload must keep its literal meaning in the grid filter and in Find, or the matches below prove nothing.
    private static void AssertMatchesLiterally(string payload)
    {
        Assert.True(
            payload.IndexOfAny(PatternCharacters) < 0,
            "The payload must hold no LIKE or Find wildcard character.");
        Assert.DoesNotContain(ComparisonPrefixes, prefix => payload.StartsWith(prefix, StringComparison.Ordinal));
        Assert.DoesNotContain(" or ", payload, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task AssertStoredVerbatimAsync(string globalRecordId, string payload, CancellationToken ct)
    {
        await using var connection = await TestDatabase.OpenConnectionAsync(ct);

        await using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT [LastName] FROM [dbo].[CaseReport1] WHERE [GlobalRecordId] = @id;";
            read.Parameters.Add(new SqlParameter("@id", SqlDbType.NVarChar, 255) { Value = globalRecordId });
            var stored = Assert.IsType<string>(await read.ExecuteScalarAsync(ct));
            Assert.Equal(payload.Length, stored.Length);
            Assert.Equal(payload, stored);
        }

        await using (var count = connection.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM [dbo].[CaseReport1] WHERE [LastName] = @payload;";
            count.Parameters.Add(new SqlParameter("@payload", SqlDbType.NVarChar, 4000) { Value = payload });
            Assert.Equal(1, Assert.IsType<int>(await count.ExecuteScalarAsync(ct)));
        }
    }

    // The hostile DROP TABLE was never executed against the form's tables.
    private static async Task AssertTablesExistAsync(CancellationToken ct)
    {
        await using var connection = await TestDatabase.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT OBJECT_ID(N'dbo.CaseReport', N'U'), OBJECT_ID(N'dbo.CaseReport1', N'U');";
        await using var reader = await command.ExecuteReaderAsync(ct);
        Assert.True(await reader.ReadAsync(ct));
        Assert.False(await reader.IsDBNullAsync(0, ct), $"Table dbo.{BaseTable} no longer exists.");
        Assert.False(await reader.IsDBNullAsync(1, ct), $"Table dbo.{PageTable} no longer exists.");
    }

    // A list or search body whose items are exactly the created record, its LastName returned verbatim.
    private static void AssertOnlyRecord(JsonElement body, string globalRecordId, string payload)
    {
        var items = Property(body, "items");
        Assert.Equal(JsonValueKind.Array, items.ValueKind);
        var item = Assert.Single(items.EnumerateArray());
        Assert.Equal(globalRecordId, Property(item, "globalRecordId").GetString());
        Assert.Equal(payload, ValueOf(item, TestDatabase.MarkerField));
    }

    private static void AssertTravelsOnlyAsParameter(
        string request,
        IReadOnlyList<RecordedCommand> commands,
        string payload,
        PayloadBinding binding,
        bool mustWritePageTable)
    {
        Assert.True(commands.Count > 0, $"{request} recorded no SQL command.");

        foreach (var command in commands)
        {
            Assert.False(
                command.CommandText.Contains(payload, StringComparison.Ordinal),
                $"{request}: {Name(command)} carries the payload in its SQL text: {command.CommandText}");
            Assert.False(
                command.CommandText.Contains(QuotedFragment, StringComparison.Ordinal),
                $"{request}: {Name(command)} carries {QuotedFragment} in its SQL text: {command.CommandText}");
            Assert.False(
                command.CommandText.Contains(DropFragment, StringComparison.OrdinalIgnoreCase),
                $"{request}: {Name(command)} carries {DropFragment} in its SQL text: {command.CommandText}");
        }

        var carriers = commands
            .Where(command => command.Succeeded && command.Parameters.Any(
                parameter => parameter.Value is string value && Binds(value, payload, binding)))
            .ToList();
        Assert.True(
            carriers.Count > 0,
            $"{request}: no successful command bound the payload as a parameter value ({Describe(binding)}); " +
            $"commands: {Names(commands)}.");

        if (mustWritePageTable)
        {
            Assert.True(
                carriers.Any(command => command.IsWrite && PageTableWriteTarget().IsMatch(command.CommandText)),
                $"{request}: no write to [{PageTable}] bound the payload; it was bound by {Names(carriers)}.");
        }
    }

    private static bool Binds(string value, string payload, PayloadBinding binding) => binding switch
    {
        PayloadBinding.Exact => string.Equals(value, payload, StringComparison.Ordinal),
        PayloadBinding.Contained => value.Contains(payload, StringComparison.Ordinal),
        _ => throw new ArgumentOutOfRangeException(nameof(binding), binding, "Unknown payload binding."),
    };

    private static string Describe(PayloadBinding binding) => binding switch
    {
        PayloadBinding.Exact => "a value equal to it",
        PayloadBinding.Contained => "a value containing it",
        _ => throw new ArgumentOutOfRangeException(nameof(binding), binding, "Unknown payload binding."),
    };

    private static string Name(RecordedCommand command) =>
        string.Create(CultureInfo.InvariantCulture, $"command #{command.Sequence} {command.Operation}");

    private static string Names(IEnumerable<RecordedCommand> commands) =>
        string.Join(
            ", ",
            commands.Select(command =>
                string.Create(CultureInfo.InvariantCulture, $"#{command.Sequence} {command.Operation}")));

    private static JsonElement Property(JsonElement element, string name)
    {
        Assert.Equal(JsonValueKind.Object, element.ValueKind);
        Assert.True(element.TryGetProperty(name, out var value), $"The response has no '{name}' member.");
        return value;
    }

    private static string? ValueOf(JsonElement record, string field) =>
        Property(Property(record, "values"), field).GetString();

    // An INSERT or UPDATE whose target is the page table, optionally dbo-qualified; the keywords in any case.
    [GeneratedRegex(@"(?i:\b(?:INSERT(?:\s+INTO)?|UPDATE)\s+)(?:\[dbo\]\s*\.\s*)?\[" + PageTable + @"\]")]
    private static partial Regex PageTableWriteTarget();
}
