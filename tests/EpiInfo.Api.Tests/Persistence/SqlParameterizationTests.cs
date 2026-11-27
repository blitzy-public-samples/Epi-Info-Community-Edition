using System.Data;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EpiInfo.Api.Tests.Infrastructure;
using EpiInfo.Domain.Metadata;
using EpiInfo.Domain.Storage;
using EpiInfo.Tests.Shared;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

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

    // OBJECT_ID's object-type code of a user table; sys.objects.type is char(2).
    private const string UserTableType = "U";

    // nvarchar(776), the size SQL Server's own procedures, such as sp_help, give a multi-part object name.
    private const int ObjectNameLength = 776;

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

    // After the requests, both form tables, dbo.CaseReport and dbo.CaseReport1, still exist as user tables. The
    // recorder checks of AssertTravelsOnlyAsParameter, not this catalogue read, establish that the payload stayed out
    // of every command's text.
    private static async Task AssertTablesExistAsync(CancellationToken ct)
    {
        await using var connection = await TestDatabase.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT OBJECT_ID(@baseTable, @userTable), OBJECT_ID(@pageTable, @userTable);";
        command.Parameters.Add(
            new SqlParameter("@baseTable", SqlDbType.NVarChar, ObjectNameLength) { Value = "dbo." + BaseTable });
        command.Parameters.Add(
            new SqlParameter("@pageTable", SqlDbType.NVarChar, ObjectNameLength) { Value = "dbo." + PageTable });
        command.Parameters.Add(new SqlParameter("@userTable", SqlDbType.NVarChar, 2) { Value = UserTableType });
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

/// <summary>
/// The valid-save half of the AAP 0.7.6 proof "Identifiers from the metadata reader, bracket-quoted": on each seeded
/// form, a record created, updated and soft-deleted through the API sends record commands in which every table and
/// column identifier is bracket-quoted and is a name the metadata reader returned for that form.
/// </summary>
/// <remarks>
/// <para>
/// <c>IdentifierProvenanceTests</c> in <c>EpiInfo.Data.Tests</c> keeps the reflection half and the refusal of a
/// <c>values</c> key the reader did not return. This half runs through the API because the only records a test may
/// write are records created through the API (AAP 0.3.7, "Data reach of tests"), and the data-layer test project
/// references <c>EpiInfo.Data</c> alone.
/// </para>
/// <para>
/// The record commands are the ones <c>SqlRecordRepository</c> sends under its insert, update and soft-delete
/// operations. Each is read as T-SQL tokens, skipping comments, parameters and numbers, and falls in one of two
/// classes. A builder statement names a table or column of either seeded form, bracketed, double-quoted or bare,
/// compared ignoring case; any other command is a fixed statement of the store. The two classes are told apart
/// because AAP 0.7.6 compares only the dynamic identifiers with the reader and leaves fixed names and aliases
/// uncompared, and because the store also sends fixed text inside a write: an insert, an update and a soft delete
/// each read the permissions again in their transaction through <c>SqlPermissionReader</c>, whose statement binds
/// every table name as a parameter value and so names no table or column at all; and an insert and an update first
/// take the form's write lock through <c>sys.sp_getapplock</c>, whose statement binds every value, the lock resource
/// holding the base table's name included, as a parameter, and so names none either.
/// </para>
/// <para>
/// A builder statement is held to the closed grammar of <c>RecordSqlBuilder</c>'s fixed text, position by position,
/// string literals skipped:
/// </para>
/// <list type="number">
/// <item><description>
/// Every bare word is a keyword or built-in that the builder's fixed text writes, or one of its table aliases
/// <c>b</c>, <c>i</c> and <c>p0</c> to <c>p{n}</c>, one per page of the form, both compared ignoring case. An alias
/// stands only right after a table reference, right after <c>AS</c>, or as the qualifier before a <c>.</c>. Any other
/// bare word is an identifier written without brackets and fails, catalogue name or not, although the statement is
/// still valid SQL. Neither list shares a name, ignoring case, with a table or column of either seeded form, so a
/// column that lost its brackets cannot pass as a keyword or an alias.
/// </description></item>
/// <item><description>
/// The name after <c>FROM</c>, <c>JOIN</c>, <c>INTO</c> or <c>UPDATE</c> is a bracketed table the reader returned for
/// the form through the host's <see cref="IDataStore"/> (its base table or a page table), compared exactly. An optional
/// <c>dbo</c> schema qualifier may precede it and is not compared. A parenthesis there opens a derived table, after
/// <c>FROM</c> or <c>JOIN</c> only.
/// </description></item>
/// <item><description>
/// Every other bracketed name is a column: a <c>sys.columns</c> column of the form's tables, the catalogue the reader
/// reads for the key, stamp and field columns, compared exactly. The name after a <c>.</c> qualifier is bracketed, and
/// after a derived table's alias it may also be a column that derived table declares. A bracketed table of the form
/// stands only in a table position or as the qualifier before a <c>.</c>.
/// </description></item>
/// <item><description>
/// A bracketed alias stands only right after <c>AS</c>: one of the result-column aliases <c>[c0]</c> to <c>[cN]</c>,
/// bounded by the form's pages and data fields, or inside a derived table one of its columns <c>[ip]</c> and
/// <c>[ic]</c>. A result-column alias is referenced, unqualified, only after the <c>AS</c> that declares it.
/// </description></item>
/// <item><description>No identifier names a table of the other form, and none is double-quoted.</description></item>
/// </list>
/// <para>
/// A fixed statement is held, position by position, to a closed rule under which it reaches no stored object:
/// </para>
/// <list type="number">
/// <item><description>It sends no write.</description></item>
/// <item><description>
/// Every table position opens a derived table: a <c>(</c> right after <c>FROM</c> or <c>JOIN</c>. Nothing may follow
/// <c>INTO</c> or <c>UPDATE</c>.
/// </description></item>
/// <item><description>
/// Every bare word is a keyword of the builders, a call of a built-in function the store's fixed reads use
/// (<c>HAS_PERMS_BY_NAME</c>), a keyword of the form write lock's fixed text (<c>DECLARE</c>, <c>EXEC</c>,
/// <c>int</c>), the system procedure that lock calls, written <c>sys.sp_getapplock</c> right after <c>EXEC</c> or
/// <c>EXEC @variable =</c>, or a name the statement itself declares with <c>AS</c>: a column alias, a derived
/// table's alias, or a name of the column list after that alias. An <c>AS</c> inside a <c>CAST</c> gives a type and
/// declares nothing.
/// </description></item>
/// <item><description>
/// Every bracketed name is likewise declared in the statement. A qualifier before a <c>.</c> is a derived-table alias
/// the statement declares, and the name after it is bracketed and is a column that derived table declares; the one
/// other qualified name is that called system procedure.
/// </description></item>
/// <item><description>
/// No string literal names a table or column of either form, read as T-SQL, and no identifier is double-quoted. No
/// identifier names one either, or the statement would be a builder statement.
/// </description></item>
/// </list>
/// <para>
/// Record commands name no catalogue view, so neither class declares one. The test holds the exclusive
/// <see cref="DatabaseWriteLock"/> (AAP 0.7.5) and writes only the records it creates, which stay in the database:
/// <c>LastName</c> carries a fresh marker, and on <c>FoodHistory</c> the page-two field <c>Lab</c> carries independent
/// fixture text, so every page table receives a column write. Catalogue reads bind the table name as a parameter.
/// Failure messages name the token and the command without its parameter values.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(DatabaseCollections.Database)]
public sealed class RecordCommandIdentifierProvenanceTests : IAsyncLifetime
{
    // The operation names SqlRecordRepository gives the commands of a record insert, update and soft delete.
    private const string InsertOperation = "records.insert";

    private const string UpdateOperation = "records.update";

    private const string DeleteOperation = "records.delete";

    // A record SELECT of the builders projects at most the three base columns and the intrinsic position and count,
    // ordinals 0 to 4, before one key column per page and one column per data field.
    private const int FixedResultColumns = 5;

    // The independent text of the page-two field, which carries no marker (markers go into LastName only).
    private const string LaterPageTextPrefix = "lab-";

    // RecordSqlBuilder's table aliases, which this assembly cannot reference: BaseAlias, of the base table or of the
    // derived table that reads it; IntrinsicSourceAlias, of the base table inside that derived table; and the prefix
    // of PageAlias(n), of the page at index n.
    private const string BaseAlias = "b";

    private const string IntrinsicSourceAlias = "i";

    private const string PageAliasPrefix = "p";

    // The one schema qualifier a table name may carry; AAP 0.7.6 does not compare schema qualifiers with the reader.
    private const string SchemaQualifier = "dbo";

    private static readonly string[] RecordWriteOperations = [InsertOperation, UpdateOperation, DeleteOperation];

    // The closed vocabulary of record commands: every keyword and built-in that RecordSqlBuilder's fixed text writes,
    // in any of its statements, compared ignoring case as SQL Server reads them.
    private static readonly HashSet<string> BuilderKeywords = new(
        [
            "AND", "AS", "bigint", "BY", "CASE", "CAST", "COUNT_BIG", "DATALENGTH", "ELSE", "END", "FETCH", "FROM",
            "HOLDLOCK", "INSERT", "INSERTED", "INTO", "JOIN", "LEFT", "NEXT", "NOT", "OFFSET", "ON", "ONLY", "ORDER",
            "OUTPUT", "OVER", "PRECEDING", "ROW_NUMBER", "ROWLOCK", "ROWS", "SELECT", "SET", "SUM", "THEN", "UNBOUNDED",
            "UPDATE", "UPDLOCK", "VALUES", "WHEN", "WHERE", "WITH",
        ],
        StringComparer.OrdinalIgnoreCase);

    // The T-SQL built-in functions of the fixed statements the store sends inside a record write, compared ignoring
    // case: HAS_PERMS_BY_NAME, of SqlPermissionReader's permission read, which each write sends in its transaction.
    private static readonly HashSet<string> FixedStatementBuiltIns = new(
        ["HAS_PERMS_BY_NAME"],
        StringComparer.OrdinalIgnoreCase);

    // The keywords of the form write lock, the fixed statement an insert and an update send first in their
    // transaction, "DECLARE @result int; EXEC @result = sys.sp_getapplock @Resource = @p0, ...; SELECT @result",
    // beyond the builders' own; and the one system procedure it calls, qualified by the system schema. Both compared
    // ignoring case.
    private static readonly HashSet<string> FixedStatementKeywords = new(
        ["DECLARE", "EXEC", "int"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> FixedStatementProcedures = new(
        ["sp_getapplock"],
        StringComparer.OrdinalIgnoreCase);

    private const string SystemSchema = "sys";

    private const string ExecuteKeyword = "EXEC";

    // RecordSqlBuilder's IntrinsicPositionColumn and IntrinsicCountColumn without brackets: the columns its derived
    // table declares with AS.
    private static readonly HashSet<string> DerivedTableColumns = new(["ip", "ic"], StringComparer.Ordinal);

    // The keywords after which T-SQL reads a table reference, and those of them after which "(" opens a derived table.
    private static readonly string[] TablePositionKeywords = ["FROM", "JOIN", "INTO", "UPDATE"];

    private static readonly string[] DerivedTableKeywords = ["FROM", "JOIN"];

    private static readonly string[] SeededForms = ["CaseReport", "FoodHistory"];

    // SQL Server ends a "--" comment at a carriage return or a line feed, and at no other character.
    private static readonly char[] LineCommentTerminators = ['\r', '\n'];

    // Longer than DatabaseWriteLock's 300-second wait, so a hung request fails the test instead of the whole run.
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(10);

    // Whole seconds a minute apart, as the controlled clock's usage contract asks of consecutive writes.
    private static readonly DateTime CreateTime = ControlledTimeProvider.DefaultLocalStart;

    private static readonly DateTime UpdateTime = CreateTime.AddMinutes(1);

    private static readonly DateTime DeleteTime = CreateTime.AddMinutes(2);

    private ApiFactory? _factory;

    private HttpClient? _client;

    // The lexical elements of T-SQL the checks tell apart; whitespace and comments yield no token.
    private enum TokenKind
    {
        Bracketed,
        DoubleQuoted,
        Word,
        Dot,
        Parameter,
        Number,
        StringLiteral,
        Symbol,
    }

    /// <summary>
    /// Each seeded form with the page-two text field it writes beside <c>LastName</c>, so every page table gets a
    /// column write: none on single-page <c>CaseReport</c>; <c>Lab</c>, on <c>FoodHistory3</c>, on
    /// <c>FoodHistory</c>, whose <c>LastName</c> is on <c>FoodHistory2</c>.
    /// </summary>
    public static TheoryData<string, string?> RecordWrites => new()
    {
        { "CaseReport", null },
        { "FoodHistory", "Lab" },
    };

    private ApiFactory Factory =>
        _factory ?? throw new InvalidOperationException("The API host is created in InitializeAsync.");

    private HttpClient Client =>
        _client ?? throw new InvalidOperationException("The API client is created in InitializeAsync.");

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

    [Theory]
    [MemberData(nameof(RecordWrites))]
    public async Task Valid_save_commands_name_only_bracketed_identifiers_the_reader_returned(
        string formName,
        string? laterPageTextField)
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        var ct = timeout.Token;
        await using var dbLock = await DatabaseWriteLock.AcquireExclusiveAsync(ct);

        var form = await ReadCatalogueAsync(formName, ct);
        var otherForm = await ReadCatalogueAsync(
            Assert.Single(SeededForms, name => !string.Equals(name, formName, StringComparison.Ordinal)),
            ct);
        Assert.Empty(form.Tables.Intersect(otherForm.Tables, StringComparer.OrdinalIgnoreCase));
        var catalogueNames = new HashSet<string>(form.Names, StringComparer.OrdinalIgnoreCase);
        catalogueNames.UnionWith(otherForm.Names);

        // A keyword or alias spelled like a catalogue name would let that name pass without brackets.
        string[] shadowing =
        [
            .. BuilderKeywords.Concat(form.Aliases).Where(catalogueNames.Contains).Order(StringComparer.Ordinal),
        ];
        Assert.True(
            shadowing.Length == 0,
            $"{formName}: a builder keyword or alias is also a table or column name of a seeded form: " +
            string.Join(", ", shadowing));

        // Create, update and soft delete one record of this test, identified by its marker and its returned id.
        var recorder = Factory.Recorder;
        recorder.Clear();
        Factory.Clock.SetLocalNow(CreateTime);
        var marker = TestDatabase.NewMarker();
        var created = await ApiTestClient.CreateRecordAsync(
            Client, form.ViewId, Values(marker, laterPageTextField), ct);
        Assert.Equal(marker, TextValue(created.Record, TestDatabase.MarkerField));

        var recordPath = string.Create(
            CultureInfo.InvariantCulture, $"/api/forms/{form.ViewId}/records/{created.GlobalRecordId}");
        Factory.Clock.SetLocalNow(UpdateTime);
        using (var response = await ApiTestClient.PutJsonAsync(
            Client, recordPath, new { values = Values(TestDatabase.NewMarker(), laterPageTextField) }, ct))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Factory.Clock.SetLocalNow(DeleteTime);
        using (var response = await Client.DeleteAsync(recordPath, ct))
        {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        var commands = recorder.Commands;
        RecordedCommand[] recordCommands =
            [.. commands.Where(command => RecordWriteOperations.Contains(command.Operation, StringComparer.Ordinal))];
        foreach (var operation in RecordWriteOperations)
        {
            Assert.True(
                recordCommands.Any(command => string.Equals(command.Operation, operation, StringComparison.Ordinal)),
                $"{formName}: no command was recorded under {operation}.");
        }

        // A write sent under any other operation would be record SQL that escaped the checks below.
        Assert.All(
            commands.Where(command => command.IsWrite),
            command => Assert.True(
                RecordWriteOperations.Contains(command.Operation, StringComparer.Ordinal),
                "A write was sent outside the record operations: " + command.Describe(includeParameterValues: false)));

        // The insert writes the base row and a row on every page; the update stamps the base row and writes every
        // page, because a submitted field sits on each; the soft delete updates the base row only.
        string[] formTables = [.. form.Tables.Order(StringComparer.Ordinal)];
        Assert.Equal(formTables, WriteTargets(recordCommands, InsertOperation, "INSERT"));
        Assert.Equal(formTables, WriteTargets(recordCommands, UpdateOperation, "UPDATE"));
        Assert.Equal(new[] { form.BaseTable }, WriteTargets(recordCommands, DeleteOperation, "UPDATE"));

        string[] violations =
        [
            .. recordCommands.SelectMany(command => Violations(command, form, otherForm, catalogueNames)
                .Select(violation =>
                    $"{formName}: {violation}, in {command.Describe(includeParameterValues: false)}")),
        ];
        Assert.True(
            violations.Length == 0,
            "A record command names an identifier the metadata reader did not return, writes one unquoted, or places " +
            "a name outside the builders' grammar:" +
            Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    // The values of a write: a marker in LastName and, on a form with a later page, independent text in its field.
    private static Dictionary<string, object?> Values(string marker, string? laterPageTextField)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal) { [TestDatabase.MarkerField] = marker };
        if (laterPageTextField is not null)
        {
            values[laterPageTextField] = LaterPageTextPrefix + Guid.NewGuid().ToString("N");
        }

        return values;
    }

    private static string? TextValue(JsonElement record, string field)
    {
        Assert.Equal(JsonValueKind.Object, record.ValueKind);
        Assert.True(record.TryGetProperty("values", out var values), "The record has no 'values' member.");
        Assert.True(values.TryGetProperty(field, out var value), $"The record's values have no '{field}' member.");
        return value.GetString();
    }

    // The form as the reader returned it through the host's store, with the sys.columns columns of its tables, the
    // result-column aliases its record SELECT can project and the table aliases of its pages.
    private async Task<FormCatalogue> ReadCatalogueAsync(string formName, CancellationToken ct)
    {
        var viewId = await ApiTestClient.GetViewIdAsync(Client, formName, ct);
        var metadata = await Factory.Services.GetRequiredService<IDataStore>().GetFormMetadataAsync(viewId, ct);
        Assert.True(metadata is not null, $"The store returned no metadata for {formName}.");

        string[] tables =
        [
            metadata.BaseTableName,
            .. metadata.Pages.OrderBy(page => page.Position).ThenBy(page => page.PageId).Select(page => page.TableName),
        ];
        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using (var connection = await TestDatabase.OpenConnectionAsync(ct))
        {
            foreach (var table in tables)
            {
                columns.UnionWith(await ReadCatalogueColumnsAsync(connection, formName, table, ct));
            }
        }

        // The reader's field columns carry the catalogue spelling, so each is one of those catalogue columns.
        HashSet<string> fieldColumns = [.. metadata.Fields.Select(field => field.Column?.Name).OfType<string>()];
        Assert.NotEmpty(fieldColumns);
        Assert.Subset(columns, fieldColumns);

        var names = new HashSet<string>(tables, StringComparer.Ordinal);
        names.UnionWith(columns);

        var aliasCount =
            FixedResultColumns + metadata.Pages.Count + FormDefinitionFactory.GetDataFields(metadata).Count;
        HashSet<string> aliases =
        [
            .. Enumerable.Range(0, aliasCount).Select(ordinal => "c" + ordinal.ToString(CultureInfo.InvariantCulture)),
        ];

        // PageAlias numbers a page by its index among the form's pages.
        var tableAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { BaseAlias, IntrinsicSourceAlias };
        tableAliases.UnionWith(Enumerable.Range(0, metadata.Pages.Count)
            .Select(index => PageAliasPrefix + index.ToString(CultureInfo.InvariantCulture)));

        return new FormCatalogue(
            formName, viewId, metadata.BaseTableName, tables, columns, names, aliases, tableAliases);
    }

    // The columns of one table as sys.columns names them. The name is bound as a parameter and resolved in the default
    // schema, as the reader resolves it, and the catalogue's own spelling of the table must be the reader's.
    private static async Task<string[]> ReadCatalogueColumnsAsync(
        SqlConnection connection,
        string formName,
        string table,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT t.[name], c.[name] FROM sys.tables AS t " +
            "INNER JOIN sys.columns AS c ON c.[object_id] = t.[object_id] " +
            "WHERE t.[object_id] = OBJECT_ID(QUOTENAME(@table), N'U') ORDER BY c.[column_id];";
        command.Parameters.Add(new SqlParameter("@table", SqlDbType.NVarChar, 128) { Value = table });

        var columns = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                Assert.Equal(table, reader.GetString(0));
                columns.Add(reader.GetString(1));
            }
        }

        Assert.True(columns.Count > 0, $"{formName}: the table {table} the reader returned has no sys.columns row.");
        return [.. columns];
    }

    // The tables one operation's writes target, distinct and sorted. Each write must read "<verb> [table]", with INTO
    // after INSERT, the shape every write statement of the builders has.
    private static string[] WriteTargets(IEnumerable<RecordedCommand> recordCommands, string operation, string verb)
    {
        var isInsert = string.Equals(verb, "INSERT", StringComparison.Ordinal);
        var targetIndex = isInsert ? 2 : 1;
        var targets = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var command in recordCommands.Where(command =>
            command.IsWrite && string.Equals(command.Operation, operation, StringComparison.Ordinal)))
        {
            var tokens = Tokenize(command.CommandText);
            var shaped = tokens.Count > targetIndex
                && IsWord(tokens[0], verb)
                && (!isInsert || IsWord(tokens[1], "INTO"))
                && tokens[targetIndex].Kind == TokenKind.Bracketed;
            Assert.True(
                shaped,
                $"A {operation} write is not {verb}{(isInsert ? " INTO" : string.Empty)} [table]: " +
                command.Describe(includeParameterValues: false));
            targets.Add(tokens[targetIndex].Text);
        }

        return [.. targets];
    }

    private static bool IsWord(SqlToken token, string keyword) =>
        token.Kind == TokenKind.Word && string.Equals(token.Text, keyword, StringComparison.OrdinalIgnoreCase);

    private static bool IsSymbol(SqlToken token, string symbol) =>
        token.Kind == TokenKind.Symbol && string.Equals(token.Text, symbol, StringComparison.Ordinal);

    // The checks of the class remarks over one record command, one message per violation, each naming its token: the
    // builders' closed grammar over a statement that names a table or column of either form, and the fixed-statement
    // rule over any other.
    private static IEnumerable<string> Violations(
        RecordedCommand command,
        FormCatalogue form,
        FormCatalogue otherForm,
        IReadOnlySet<string> catalogueNames)
    {
        var tokens = Tokenize(command.CommandText);
        return NamesCatalogue(tokens, catalogueNames)
            ? BuilderViolations(tokens, form, otherForm, catalogueNames)
            : FixedStatementViolations(command, tokens, catalogueNames);
    }

    // Whether an identifier of the text, bracketed, double-quoted or bare, is a table or column name of either form,
    // compared ignoring case as catalogueNames compares. A dynamic name that lost its brackets still names one, so it
    // keeps its statement under the builders' grammar.
    private static bool NamesCatalogue(IReadOnlyList<SqlToken> tokens, IReadOnlySet<string> catalogueNames) =>
        tokens.Any(token =>
            (token.Kind is TokenKind.Bracketed or TokenKind.DoubleQuoted or TokenKind.Word)
            && catalogueNames.Contains(token.Text));

    // The builders' closed grammar of the class remarks over one builder statement.
    private static IEnumerable<string> BuilderViolations(
        IReadOnlyList<SqlToken> tokens,
        FormCatalogue form,
        FormCatalogue otherForm,
        IReadOnlySet<string> catalogueNames)
    {
        var shape = CommandShape.Of(tokens);

        // The result-column aliases an AS outside a derived table has declared so far, which a later unqualified
        // reference, such as ORDER BY [c0], may name.
        var declaredResultAliases = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if ((token.Kind is TokenKind.Bracketed or TokenKind.Word) && otherForm.TableSet.Contains(token.Text))
            {
                yield return token.Kind == TokenKind.Bracketed
                    ? $"{Display(token)} is a table of {otherForm.Name}"
                    : $"{Display(token)} is a table of {otherForm.Name}, written without brackets";
            }

            if (shape.TablePositions.Contains(index)
                && !(token.Kind == TokenKind.Bracketed && form.Tables.Contains(token.Text, StringComparer.Ordinal))
                && !shape.DerivedTableOpenings.Contains(index))
            {
                yield return $"{Display(token)} stands in a table position, where only a bracketed table the " +
                    $"metadata reader returned for {form.Name} may stand";
            }

            var violation = token.Kind switch
            {
                TokenKind.DoubleQuoted => $"{Display(token)} is a double-quoted identifier, not a bracket-quoted one",
                TokenKind.Word => WordViolation(tokens, index, form, otherForm, catalogueNames, shape),
                TokenKind.Bracketed => BracketedViolation(tokens, index, form, shape, declaredResultAliases),
                TokenKind.Dot when index + 1 >= tokens.Count || tokens[index + 1].Kind != TokenKind.Bracketed =>
                    "the name after a '.' qualifier is not bracket-quoted: " +
                    (index + 1 < tokens.Count ? Display(tokens[index + 1]) : "the end of the text"),
                _ => null,
            };
            if (violation is not null)
            {
                yield return violation;
            }
        }
    }

    // A bare word passes as a keyword of the builders anywhere, as a table alias of the form in an alias position, or
    // as the schema qualifier of a table position. A table of the other form is reported by the caller.
    private static string? WordViolation(
        IReadOnlyList<SqlToken> tokens,
        int index,
        FormCatalogue form,
        FormCatalogue otherForm,
        IReadOnlySet<string> catalogueNames,
        CommandShape shape)
    {
        var word = tokens[index].Text;
        if (BuilderKeywords.Contains(word)
            || shape.SchemaQualifiers.Contains(index)
            || otherForm.TableSet.Contains(word))
        {
            return null;
        }

        if (form.Aliases.Contains(word))
        {
            return IsAliasPosition(tokens, index, shape)
                ? null
                : $"{word} is a builder alias outside an alias position: right after a table reference or AS, or " +
                    "before a '.' qualifier";
        }

        return catalogueNames.Contains(word)
            ? $"{word} is a catalogue table or column name written without brackets"
            : $"{word} is neither a T-SQL keyword of the builders nor a declared alias: an identifier written " +
                "without brackets";
    }

    private static bool IsAliasPosition(IReadOnlyList<SqlToken> tokens, int index, CommandShape shape) =>
        shape.TableReferenceEnds.Contains(index - 1)
        || (index > 0 && IsWord(tokens[index - 1], "AS"))
        || (index + 1 < tokens.Count && tokens[index + 1].Kind == TokenKind.Dot);

    // A bracketed name outside a table position: a result-column alias right after AS, a table of the form as the
    // qualifier before a '.', and otherwise a column of the form, or a column its derived table declares after that
    // table's alias, or a result-column alias declared earlier.
    private static string? BracketedViolation(
        IReadOnlyList<SqlToken> tokens,
        int index,
        FormCatalogue form,
        CommandShape shape,
        HashSet<string> declaredResultAliases)
    {
        var name = tokens[index].Text;
        if (shape.TablePositions.Contains(index) || shape.SchemaQualifiers.Contains(index))
        {
            // The caller's table-position check judges a table; a schema qualifier is not compared.
            return null;
        }

        if (index > 0 && IsWord(tokens[index - 1], "AS"))
        {
            var inDerivedTable = shape.DerivedTableDeclarations.Contains(index);
            if (form.ResultAliases.Contains(name))
            {
                if (!inDerivedTable)
                {
                    declaredResultAliases.Add(name);
                }

                return null;
            }

            if (inDerivedTable && DerivedTableColumns.Contains(name))
            {
                return null;
            }

            return $"[{name}] follows AS but is not a result-column alias [c0] to [c{form.ResultAliases.Count - 1}]" +
                (inDerivedTable ? " or a derived-table column [ip] or [ic]" : string.Empty);
        }

        if (shape.TableReferenceEnds.Contains(index - 1))
        {
            return $"[{name}] follows a table reference, where only a builder alias written without brackets may stand";
        }

        if (index + 1 < tokens.Count && tokens[index + 1].Kind == TokenKind.Dot)
        {
            return form.Tables.Contains(name, StringComparer.Ordinal)
                ? null
                : $"[{name}] qualifies a name but is not a table the metadata reader returned for {form.Name}; the " +
                    "builders write their aliases without brackets";
        }

        if (index > 0 && tokens[index - 1].Kind == TokenKind.Dot)
        {
            var declaredByDerivedTable = index > 1
                && tokens[index - 2].Kind == TokenKind.Word
                && shape.DerivedTableColumnsByAlias.TryGetValue(tokens[index - 2].Text, out var declared)
                && declared.Contains(name);
            return form.Columns.Contains(name) || declaredByDerivedTable
                ? null
                : $"[{name}] follows a '.' qualifier but is neither a column the metadata reader returned for " +
                    $"{form.Name} nor a column the derived table of that qualifier declares";
        }

        if (form.Columns.Contains(name) || declaredResultAliases.Contains(name))
        {
            return null;
        }

        return form.ResultAliases.Contains(name) || DerivedTableColumns.Contains(name)
            ? $"[{name}] is a result-column alias outside the AS that declares it"
            : $"[{name}] stands in a column position but is not a column the metadata reader returned for {form.Name}";
    }

    // The fixed-statement rule of the class remarks over a record command that names no table or column of either
    // form: it writes nothing and reads only derived tables, no string literal in it names a table or column of either
    // form, and every name in it is a keyword of the builders, a built-in of the store's fixed reads, or a name the
    // statement itself declares with AS.
    private static IEnumerable<string> FixedStatementViolations(
        RecordedCommand command,
        IReadOnlyList<SqlToken> tokens,
        IReadOnlySet<string> catalogueNames)
    {
        if (command.IsWrite)
        {
            yield return "the statement names no table or column of either form, yet it writes";
        }

        var shape = FixedStatementShape.Of(tokens);
        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            var violation = token.Kind switch
            {
                TokenKind.DoubleQuoted => $"{Display(token)} is a double-quoted identifier, not a bracket-quoted one",
                TokenKind.Word => FixedWordViolation(tokens, index, shape),
                TokenKind.Bracketed => FixedBracketedViolation(tokens, index, shape),
                TokenKind.Dot => FixedDotViolation(tokens, index),

                // A name read as T-SQL from the literal's content, as an object-name argument would read it.
                TokenKind.StringLiteral when NamesCatalogue(Tokenize(token.Text), catalogueNames) =>
                    $"{Display(token)} names a table or column of either form in the statement text, where a fixed " +
                    "statement takes a table name only as a parameter value",
                _ => null,
            };
            if (violation is not null)
            {
                yield return violation;
            }
        }
    }

    // A bare word of a fixed statement: a table-position keyword followed by the "(" of a derived table, a qualifier
    // that is a derived-table alias the statement declares, the system schema or the system procedure of the form write
    // lock's call, a keyword of the builders or of the form write lock, a called built-in of the store's fixed reads,
    // or a name the statement declares with AS.
    private static string? FixedWordViolation(IReadOnlyList<SqlToken> tokens, int index, FixedStatementShape shape)
    {
        var word = tokens[index].Text;
        var hasNext = index + 1 < tokens.Count;
        if (TablePositionKeywords.Contains(word, StringComparer.OrdinalIgnoreCase))
        {
            var following = hasNext ? Display(tokens[index + 1]) : "the end of the text";
            return DerivedTableKeywords.Contains(word, StringComparer.OrdinalIgnoreCase)
                && hasNext
                && IsSymbol(tokens[index + 1], "(")
                    ? null
                    : $"{following} stands in the table position after {word}, where a statement that names no " +
                        "table of either form may only open a derived table after FROM or JOIN";
        }

        if (IsCalledSystemProcedure(tokens, index) || IsCalledSystemProcedure(tokens, index + 2))
        {
            return null;
        }

        if (hasNext && tokens[index + 1].Kind == TokenKind.Dot)
        {
            return shape.DerivedTableColumns.ContainsKey(word)
                ? null
                : $"{word} qualifies a name but is not a derived-table alias the statement declares with AS";
        }

        if (FixedStatementProcedures.Contains(word))
        {
            return $"{word} is the system procedure of the form write lock but is not called as " +
                $"{SystemSchema}.{word} right after {ExecuteKeyword} or {ExecuteKeyword} @variable =";
        }

        if (BuilderKeywords.Contains(word) || FixedStatementKeywords.Contains(word))
        {
            return null;
        }

        if (FixedStatementBuiltIns.Contains(word))
        {
            return hasNext && IsSymbol(tokens[index + 1], "(")
                ? null
                : $"{word} is a built-in function of the store's fixed reads but is not called";
        }

        return shape.DeclaredWords.Contains(word)
            ? null
            : $"{word} is neither a T-SQL keyword of the builders, a built-in of the store's fixed reads, nor a name " +
                "the statement declares with AS: an identifier written without brackets";
    }

    // A bracketed name of a fixed statement: a qualifier that is a derived-table alias the statement declares, a
    // column that derived table declares after that qualifier, or otherwise a name the statement declares with AS.
    private static string? FixedBracketedViolation(IReadOnlyList<SqlToken> tokens, int index, FixedStatementShape shape)
    {
        var name = tokens[index].Text;
        if (index + 1 < tokens.Count && tokens[index + 1].Kind == TokenKind.Dot)
        {
            return shape.DerivedTableColumns.ContainsKey(name)
                ? null
                : $"[{name}] qualifies a name but is not a derived-table alias the statement declares with AS";
        }

        if (index > 0 && tokens[index - 1].Kind == TokenKind.Dot)
        {
            var declaredByQualifier = index > 1
                && (tokens[index - 2].Kind is TokenKind.Word or TokenKind.Bracketed)
                && shape.DerivedTableColumns.TryGetValue(tokens[index - 2].Text, out var columns)
                && columns.Contains(name);
            return declaredByQualifier
                ? null
                : $"[{name}] follows a '.' qualifier but is not a column the derived table of that qualifier declares";
        }

        return shape.Declarations.Contains(index) || shape.DeclaredNames.Contains(name)
            ? null
            : $"[{name}] is not a name the statement declares with AS";
    }

    // A '.' of a fixed statement joins a name before it, which FixedWordViolation and FixedBracketedViolation judge as
    // a qualifier, to a bracketed name after it; or it joins the system schema to the system procedure of the form
    // write lock's call.
    private static string? FixedDotViolation(IReadOnlyList<SqlToken> tokens, int index)
    {
        if (IsCalledSystemProcedure(tokens, index + 1))
        {
            return null;
        }

        if (index == 0 || tokens[index - 1].Kind is not (TokenKind.Word or TokenKind.Bracketed))
        {
            return "a '.' qualifier follows " +
                (index == 0 ? "the start of the text" : Display(tokens[index - 1])) +
                ", not a derived-table alias the statement declares";
        }

        return index + 1 < tokens.Count && tokens[index + 1].Kind == TokenKind.Bracketed
            ? null
            : "the name after a '.' qualifier is not bracket-quoted: " +
                (index + 1 < tokens.Count ? Display(tokens[index + 1]) : "the end of the text");
    }

    // Whether the token at procedureIndex is the system procedure of the form write lock, called as the lock's fixed
    // text calls it: "sys" "." procedure, right after EXEC or after EXEC @variable =.
    private static bool IsCalledSystemProcedure(IReadOnlyList<SqlToken> tokens, int procedureIndex)
    {
        if (procedureIndex < 3
            || procedureIndex >= tokens.Count
            || tokens[procedureIndex].Kind != TokenKind.Word
            || !FixedStatementProcedures.Contains(tokens[procedureIndex].Text)
            || tokens[procedureIndex - 1].Kind != TokenKind.Dot
            || !IsWord(tokens[procedureIndex - 2], SystemSchema))
        {
            return false;
        }

        var call = procedureIndex - 3;
        return IsWord(tokens[call], ExecuteKeyword)
            || (call >= 2
                && IsSymbol(tokens[call], "=")
                && tokens[call - 1].Kind == TokenKind.Parameter
                && IsWord(tokens[call - 2], ExecuteKeyword));
    }

    // A token as a failure message names it: an identifier with its quotes, a string literal never by its content.
    private static string Display(SqlToken token) => token.Kind switch
    {
        TokenKind.Bracketed => "[" + token.Text + "]",
        TokenKind.DoubleQuoted => "\"" + token.Text + "\"",
        TokenKind.StringLiteral => "a string literal",
        _ => token.Text,
    };

    // Splits T-SQL text into tokens. Whitespace, "--" comments and nested /* */ comments yield none; a string literal,
    // N'...' included, yields one token holding its unescaped content, which no message shows; a bracketed or
    // double-quoted identifier yields its name with the doubled closing character unescaped. A regular identifier or
    // keyword starts with a letter, '_' or '#' and continues with letters, digits, '_', '#', '$' or '@'. An
    // unterminated span runs to the end of the text.
    private static List<SqlToken> Tokenize(string text)
    {
        var tokens = new List<SqlToken>();
        var index = 0;
        while (index < text.Length)
        {
            var current = text[index];
            var next = index + 1 < text.Length ? text[index + 1] : '\0';
            var start = index;

            if (char.IsWhiteSpace(current))
            {
                index++;
            }
            else if (current == '-' && next == '-')
            {
                var lineEnd = text.IndexOfAny(LineCommentTerminators, index + 2);
                index = lineEnd < 0 ? text.Length : lineEnd;
            }
            else if (current == '/' && next == '*')
            {
                index = SkipBlockComment(text, index);
            }
            else if (current == '\'' || (current is 'N' or 'n' && next == '\''))
            {
                var content = new StringBuilder();
                index = ReadDelimited(text, current == '\'' ? index : index + 1, '\'', content);
                tokens.Add(new SqlToken(TokenKind.StringLiteral, content.ToString()));
            }
            else if (current is '[' or '"')
            {
                var bracketed = current == '[';
                var name = new StringBuilder();
                index = ReadDelimited(text, index, bracketed ? ']' : '"', name);
                tokens.Add(new SqlToken(bracketed ? TokenKind.Bracketed : TokenKind.DoubleQuoted, name.ToString()));
            }
            else if (current == '@')
            {
                index = SkipWhile(text, index + 1, IsIdentifierPart);
                tokens.Add(new SqlToken(TokenKind.Parameter, text[start..index]));
            }
            else if (char.IsAsciiDigit(current))
            {
                index = SkipNumber(text, index);
                tokens.Add(new SqlToken(TokenKind.Number, text[start..index]));
            }
            else if (char.IsLetter(current) || current is '_' or '#')
            {
                index = SkipWhile(text, index + 1, IsIdentifierPart);
                tokens.Add(new SqlToken(TokenKind.Word, text[start..index]));
            }
            else
            {
                index++;
                tokens.Add(new SqlToken(current == '.' ? TokenKind.Dot : TokenKind.Symbol, text[start..index]));
            }
        }

        return tokens;
    }

    private static bool IsIdentifierPart(char character) =>
        char.IsLetterOrDigit(character) || character is '_' or '#' or '$' or '@';

    private static int SkipWhile(string text, int index, Func<char, bool> predicate)
    {
        while (index < text.Length && predicate(text[index]))
        {
            index++;
        }

        return index;
    }

    // A numeric literal: 0x and hexadecimal digits, or digits with an optional fraction and exponent.
    private static int SkipNumber(string text, int index)
    {
        if (text[index] == '0' && index + 1 < text.Length && text[index + 1] is 'x' or 'X')
        {
            return SkipWhile(text, index + 2, char.IsAsciiHexDigit);
        }

        index = SkipWhile(text, index, char.IsAsciiDigit);
        if (index < text.Length && text[index] == '.')
        {
            index = SkipWhile(text, index + 1, char.IsAsciiDigit);
        }

        if (index < text.Length && text[index] is 'e' or 'E')
        {
            var exponent = index + 1 < text.Length && text[index + 1] is '+' or '-' ? index + 2 : index + 1;
            if (exponent < text.Length && char.IsAsciiDigit(text[exponent]))
            {
                index = SkipWhile(text, exponent, char.IsAsciiDigit);
            }
        }

        return index;
    }

    // Reads a span opened at text[start] and closed by 'close', where a doubled 'close' is an escaped character.
    // Returns the index after the span, appending its unescaped content to 'content' when one is supplied.
    private static int ReadDelimited(string text, int start, char close, StringBuilder? content)
    {
        var index = start + 1;
        while (index < text.Length)
        {
            if (text[index] == close)
            {
                if (index + 1 < text.Length && text[index + 1] == close)
                {
                    content?.Append(close);
                    index += 2;
                    continue;
                }

                return index + 1;
            }

            content?.Append(text[index]);
            index++;
        }

        return index;
    }

    // T-SQL block comments nest, so the comment ends only when every opened "/*" is closed.
    private static int SkipBlockComment(string text, int start)
    {
        var depth = 0;
        var index = start;
        while (index < text.Length)
        {
            if (text[index] == '/' && index + 1 < text.Length && text[index + 1] == '*')
            {
                depth++;
                index += 2;
            }
            else if (text[index] == '*' && index + 1 < text.Length && text[index + 1] == '/')
            {
                depth--;
                index += 2;
                if (depth == 0)
                {
                    return index;
                }
            }
            else
            {
                index++;
            }
        }

        return index;
    }

    private readonly record struct SqlToken(TokenKind Kind, string Text);

    // One seeded form as the reader returned it: its tables, base first; the sys.columns columns of those tables, and
    // every table and column name, both compared exactly; the builders' [cN] result-column aliases without brackets;
    // and the builders' table aliases for its pages, compared ignoring case.
    private sealed record FormCatalogue(
        string Name,
        int ViewId,
        string BaseTable,
        IReadOnlyList<string> Tables,
        IReadOnlySet<string> Columns,
        IReadOnlySet<string> Names,
        IReadOnlySet<string> ResultAliases,
        IReadOnlySet<string> Aliases)
    {
        // The tables compared ignoring case, as a case-insensitive collation resolves an identifier.
        public IReadOnlySet<string> TableSet { get; } = new HashSet<string>(Tables, StringComparer.OrdinalIgnoreCase);
    }

    // Where the builders' grammar places the tokens of one command, read before any check runs, because an outer query
    // names a derived table's columns before the text that declares them. Each set holds token indexes.
    private sealed class CommandShape
    {
        // The token a FROM, JOIN, INTO or UPDATE puts in a table position, past an optional schema qualifier.
        public HashSet<int> TablePositions { get; } = [];

        // The "(" in a table position after FROM or JOIN, which opens a derived table.
        public HashSet<int> DerivedTableOpenings { get; } = [];

        // The schema qualifier before the table of a table position.
        public HashSet<int> SchemaQualifiers { get; } = [];

        // The last token of a table reference, after which its alias may stand: the token in a table position, or the
        // ")" that closes a derived table.
        public HashSet<int> TableReferenceEnds { get; } = [];

        // The bracketed names an AS declares inside a derived table.
        public HashSet<int> DerivedTableDeclarations { get; } = [];

        // The names each derived table declares with AS, keyed by the alias that follows the derived table.
        public Dictionary<string, HashSet<string>> DerivedTableColumnsByAlias { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public static CommandShape Of(IReadOnlyList<SqlToken> tokens)
        {
            var shape = new CommandShape();

            // One entry per open parenthesis: the names its derived table declares, or null when it opens none.
            var open = new Stack<HashSet<string>?>();
            for (var index = 0; index < tokens.Count; index++)
            {
                var token = tokens[index];
                if (token.Kind == TokenKind.Word
                    && TablePositionKeywords.Contains(token.Text, StringComparer.OrdinalIgnoreCase))
                {
                    shape.MarkTablePosition(tokens, index + 1);
                }
                else if (IsSymbol(token, "("))
                {
                    var opensDerivedTable = shape.TablePositions.Contains(index)
                        && DerivedTableKeywords.Contains(tokens[index - 1].Text, StringComparer.OrdinalIgnoreCase);
                    if (opensDerivedTable)
                    {
                        shape.DerivedTableOpenings.Add(index);
                    }

                    open.Push(opensDerivedTable ? new HashSet<string>(StringComparer.Ordinal) : null);
                }
                else if (IsSymbol(token, ")") && open.TryPop(out var declared) && declared is not null)
                {
                    shape.TableReferenceEnds.Add(index);
                    var aliasIndex =
                        index + 1 < tokens.Count && IsWord(tokens[index + 1], "AS") ? index + 2 : index + 1;
                    if (aliasIndex < tokens.Count && tokens[aliasIndex].Kind == TokenKind.Word)
                    {
                        shape.DerivedTableColumnsByAlias[tokens[aliasIndex].Text] = declared;
                    }
                }
                else if (token.Kind == TokenKind.Bracketed
                    && index > 0
                    && IsWord(tokens[index - 1], "AS")
                    && open.FirstOrDefault(names => names is not null) is { } enclosing)
                {
                    enclosing.Add(token.Text);
                    shape.DerivedTableDeclarations.Add(index);
                }
            }

            return shape;
        }

        // The table position that starts at index: an optional schema qualifier and its ".", then the table.
        private void MarkTablePosition(IReadOnlyList<SqlToken> tokens, int index)
        {
            if (index + 1 < tokens.Count
                && tokens[index].Kind is TokenKind.Word or TokenKind.Bracketed
                && string.Equals(tokens[index].Text, SchemaQualifier, StringComparison.OrdinalIgnoreCase)
                && tokens[index + 1].Kind == TokenKind.Dot)
            {
                SchemaQualifiers.Add(index);
                index += 2;
            }

            if (index < tokens.Count)
            {
                TablePositions.Add(index);
                if (tokens[index].Kind is TokenKind.Bracketed or TokenKind.Word)
                {
                    TableReferenceEnds.Add(index);
                }
            }
        }
    }

    // The names a fixed statement declares with AS, read before any check runs, because its select list names a
    // derived table's columns before the text that declares them. A declaration is a bare or bracketed name right
    // after an AS that does not give a CAST its type: a column alias, or the alias of a derived table opened after
    // FROM or JOIN, which a parenthesised list of that derived table's column names may follow. Each set holds names
    // or token indexes.
    private sealed class FixedStatementShape
    {
        private const string CastKeyword = "CAST";

        // What an open parenthesis opens: an AS directly inside a CAST's parentheses names a type, not a declaration.
        private enum Parenthesis
        {
            Other,
            DerivedTable,
            Cast,
        }

        // The declarations themselves.
        public HashSet<int> Declarations { get; } = [];

        // Every declared name, compared ignoring case for a bare word, as the builders' aliases are compared.
        public HashSet<string> DeclaredWords { get; } = new(StringComparer.OrdinalIgnoreCase);

        // Every declared name, compared exactly for a bracketed name, as the builders' bracketed aliases are compared.
        public HashSet<string> DeclaredNames { get; } = new(StringComparer.Ordinal);

        // The columns of each derived table, keyed by the alias its AS declares: the names of its column list or,
        // without one, the names its own select list declares with AS.
        public Dictionary<string, HashSet<string>> DerivedTableColumns { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public static FixedStatementShape Of(IReadOnlyList<SqlToken> tokens)
        {
            var shape = new FixedStatementShape();

            // One entry per open parenthesis: what it opens and, for a derived table, the names its select list
            // declares.
            var open = new Stack<(Parenthesis Kind, HashSet<string>? SelectListNames)>();
            for (var index = 0; index < tokens.Count; index++)
            {
                var token = tokens[index];
                if (IsSymbol(token, "("))
                {
                    var kind = Opening(tokens, index);
                    var selectListNames =
                        kind == Parenthesis.DerivedTable ? new HashSet<string>(StringComparer.Ordinal) : null;
                    open.Push((kind, selectListNames));
                }
                else if (IsSymbol(token, ")"))
                {
                    if (open.TryPop(out var closed) && closed.SelectListNames is { } declared)
                    {
                        shape.DeclareDerivedTable(tokens, index + 1, declared);
                    }
                }
                else if (IsName(token)
                    && index > 0
                    && IsWord(tokens[index - 1], "AS")
                    && !shape.Declarations.Contains(index)
                    && !(open.TryPeek(out var innermost) && innermost.Kind == Parenthesis.Cast))
                {
                    // A column alias, which the innermost enclosing derived table, if any, exposes as its column.
                    shape.Declare(index, token.Text);
                    open.FirstOrDefault(entry => entry.SelectListNames is not null).SelectListNames?.Add(token.Text);
                }
            }

            return shape;
        }

        private static bool IsName(SqlToken token) => token.Kind is TokenKind.Word or TokenKind.Bracketed;

        // A "(" right after FROM or JOIN opens a derived table, and one right after CAST its argument.
        private static Parenthesis Opening(IReadOnlyList<SqlToken> tokens, int index)
        {
            if (index == 0)
            {
                return Parenthesis.Other;
            }

            var previous = tokens[index - 1];
            if (previous.Kind == TokenKind.Word
                && DerivedTableKeywords.Contains(previous.Text, StringComparer.OrdinalIgnoreCase))
            {
                return Parenthesis.DerivedTable;
            }

            return IsWord(previous, CastKeyword) ? Parenthesis.Cast : Parenthesis.Other;
        }

        // "AS alias", optionally followed by "(name, ...)", at index, right after the ")" of a derived table. Without
        // that AS the derived table declares no alias, so no qualifier can name it.
        private void DeclareDerivedTable(IReadOnlyList<SqlToken> tokens, int index, HashSet<string> selectListNames)
        {
            if (index + 1 >= tokens.Count || !IsWord(tokens[index], "AS") || !IsName(tokens[index + 1]))
            {
                return;
            }

            Declare(index + 1, tokens[index + 1].Text);
            DerivedTableColumns[tokens[index + 1].Text] = DeclareColumnList(tokens, index + 2) ?? selectListNames;
        }

        // The column list "(name, name, ...)" that starts at index, declared and returned when it has exactly that
        // form; otherwise nothing is declared and the result is null.
        private HashSet<string>? DeclareColumnList(IReadOnlyList<SqlToken> tokens, int index)
        {
            if (index >= tokens.Count || !IsSymbol(tokens[index], "("))
            {
                return null;
            }

            var names = new List<int>();
            index++;
            while (true)
            {
                if (index >= tokens.Count || !IsName(tokens[index]))
                {
                    return null;
                }

                names.Add(index);
                index++;
                if (index < tokens.Count && IsSymbol(tokens[index], ")"))
                {
                    break;
                }

                if (index >= tokens.Count || !IsSymbol(tokens[index], ","))
                {
                    return null;
                }

                index++;
            }

            var columns = new HashSet<string>(StringComparer.Ordinal);
            foreach (var name in names)
            {
                Declare(name, tokens[name].Text);
                columns.Add(tokens[name].Text);
            }

            return columns;
        }

        private void Declare(int index, string name)
        {
            Declarations.Add(index);
            DeclaredWords.Add(name);
            DeclaredNames.Add(name);
        }
    }

}
