using System.Data;
using System.Text.Json;
using EpiInfo.Data.Metadata;
using EpiInfo.Data.Permissions;
using EpiInfo.Data.Sql;
using EpiInfo.Domain.Metadata;
using EpiInfo.Domain.Storage;
using EpiInfo.Tests.Shared;
using Microsoft.Data.SqlClient;

namespace EpiInfo.Data.Tests.Permissions;

/// <summary>
/// Integration tests of <see cref="SqlPermissionReader"/> against the rehearsal database, as the seeded login. They
/// only read: no lock is taken and nothing is written. No denied principal is created, because a new login or user
/// would change the database, which the unchanged-database rule excludes; denial is covered with <c>FakeDataStore</c>
/// in <c>PermissionTests</c>, and here a table missing from the catalogue stands in for a denied one. The untested real
/// denial is recorded as a gap in <c>docs/parity-notes.md</c>.
/// </summary>
[Trait("Category", "Integration")]
[Collection(DatabaseCollections.Database)]
public sealed class SqlPermissionReaderTests : IAsyncLifetime
{
    private const string PermissionFunction = "HAS_PERMS_BY_NAME";

    // Every data table of the seed (testdata/sql/05_data_tables.sql).
    private static readonly string[] SeededTables =
        ["CaseReport", "CaseReport1", "FoodHistory", "FoodHistory2", "FoodHistory3"];

    /// <summary>Each seeded form: its view id, its name, and its base table then its page tables in page order.</summary>
    public static TheoryData<int, string, string[]> SeededForms => new()
    {
        { 1, "CaseReport", ["CaseReport", "CaseReport1"] },
        { 2, "FoodHistory", ["FoodHistory", "FoodHistory2", "FoodHistory3"] },
    };

    // xUnit builds a new instance for every test case, so each test gets its own 60-second budget for every
    // connection, command and read it makes.
    private readonly CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));

    /// <inheritdoc />
    public Task InitializeAsync() => TestDatabase.EnsureReachableAsync();

    /// <inheritdoc />
    public Task DisposeAsync()
    {
        timeout.Dispose();
        return Task.CompletedTask;
    }

    [Theory]
    [MemberData(nameof(SeededForms))]
    public async Task SeededLogin_GetsEveryPerTableFlag_AsHasPermsByNameReportsIt(
        int viewId,
        string formName,
        string[] expectedTables)
    {
        FormMetadata form = await LoadFormAsync(viewId, formName, expectedTables, timeout.Token);

        FormPermissions? permissions = await CreateReader(NullSqlCommandObserver.Instance)
            .GetPermissionsAsync(form, timeout.Token);

        Assert.NotNull(permissions);
        Assert.Equal(expectedTables, permissions.Tables.Select(table => table.TableName));
        Assert.Equal(
            expectedTables.Select(table => (table, table)).OrderBy(pair => pair.Item1, StringComparer.Ordinal),
            Pairs(permissions.ResolvedTableNames));

        await using SqlConnection connection = await TestDatabase.OpenConnectionAsync(timeout.Token);
        foreach (TablePermission table in permissions.Tables)
        {
            (bool canInsert, bool canUpdate) = await HasPermsByNameAsync(connection, table.TableName, timeout.Token);
            Assert.True(table.CanInsert, table.TableName);
            Assert.True(table.CanUpdate, table.TableName);
            Assert.Equal(canInsert, table.CanInsert);
            Assert.Equal(canUpdate, table.CanUpdate);
        }

        foreach (string expected in expectedTables)
        {
            TablePermission? found = permissions.Find(expected);
            Assert.True(found is not null, $"view {viewId}: Find({expected}) returned no entry");
            Assert.Equal(expected, found.TableName);
        }

        Assert.True(permissions.CanInsert(form));
        Assert.True(permissions.CanUpdate(form));
        Assert.True(permissions.CanDelete(form));
        Assert.All(form.Pages, page => Assert.True(permissions.CanInsertInto(page.TableName), page.TableName));
    }

    // The N+1 regression pin: one catalogue read for all of the form's tables and one permission statement for all of
    // them, never a metadata re-read or a statement per table.
    [Theory]
    [MemberData(nameof(SeededForms))]
    public async Task OneCall_SendsOneCatalogueReadAndOneBatchedPermissionStatement(
        int viewId,
        string formName,
        string[] expectedTables)
    {
        FormMetadata form = await LoadFormAsync(viewId, formName, expectedTables, timeout.Token);
        var recorder = new SqlCommandRecorder();

        await CreateReader(recorder).GetPermissionsAsync(form, timeout.Token);

        IReadOnlyList<RecordedCommand> commands = recorder.Commands;
        Assert.True(commands.Count == 2, DescribeCommands(commands));
        Assert.All(commands, command =>
        {
            Assert.True(command.Completed, command.ToString());
            Assert.True(command.Succeeded, command.ToString());
            Assert.False(command.IsWrite, command.ToString());
            Assert.False(command.InTransaction, command.ToString());
        });
        Assert.Single(commands.Select(command => command.ClientConnectionId).Distinct());

        RecordedCommand catalogue = commands[0];
        Assert.DoesNotContain(PermissionFunction, catalogue.CommandText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sys.columns", catalogue.CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain("metaViews", catalogue.CommandText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("metaFields", catalogue.CommandText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("metaPages", catalogue.CommandText, StringComparison.OrdinalIgnoreCase);
        SqlCommandParameterInfo names = Assert.Single(catalogue.Parameters);
        Assert.Equal(
            expectedTables.Order(StringComparer.Ordinal),
            JsonSerializer.Deserialize<string[]>(Assert.IsType<string>(names.Value))!.Order(StringComparer.Ordinal));

        RecordedCommand permission = commands[1];
        Assert.StartsWith("SELECT", permission.CommandText.TrimStart(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(PermissionFunction, permission.CommandText, StringComparison.Ordinal);
        Assert.Equal(
            expectedTables.Select(table => "[" + table + "]").Order(StringComparer.Ordinal),
            permission.Parameters
                .Select(parameter => Assert.IsType<string>(parameter.Value))
                .Order(StringComparer.Ordinal));
        Assert.All(permission.Parameters, parameter => Assert.Equal(DbType.String, parameter.DbType));
        foreach (string table in SeededTables)
        {
            Assert.DoesNotContain(table, permission.CommandText, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("N'INSERT'", permission.CommandText, StringComparison.Ordinal);
        Assert.Contains("N'UPDATE'", permission.CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain("N'DELETE'", permission.CommandText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("N'SELECT'", permission.CommandText, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(SeededForms))]
    public async Task RepeatedCalls_OnOneReader_ReadTheCatalogueAndPermissionsAgain(
        int viewId,
        string formName,
        string[] expectedTables)
    {
        FormMetadata form = await LoadFormAsync(viewId, formName, expectedTables, timeout.Token);
        var recorder = new SqlCommandRecorder();
        SqlPermissionReader reader = CreateReader(recorder);

        FormPermissions? firstResult = await reader.GetPermissionsAsync(form, timeout.Token);
        IReadOnlyList<RecordedCommand> first = recorder.Commands;
        recorder.Clear();
        FormPermissions? secondResult = await reader.GetPermissionsAsync(form, timeout.Token);
        IReadOnlyList<RecordedCommand> second = recorder.Commands;

        Assert.NotNull(firstResult);
        Assert.NotNull(secondResult);
        Assert.Equal(firstResult.Tables, secondResult.Tables);
        Assert.Equal(Pairs(firstResult.ResolvedTableNames), Pairs(secondResult.ResolvedTableNames));

        Assert.True(first.Count == 2, DescribeCommands(first));
        Assert.True(second.Count == 2, DescribeCommands(second));
        for (int index = 0; index < first.Count; index++)
        {
            Assert.Equal(first[index].CommandText, second[index].CommandText);
            Assert.Equal(
                first[index].Parameters.Select(parameter => parameter.Value),
                second[index].Parameters.Select(parameter => parameter.Value));
            Assert.True(second[index].Succeeded, second[index].ToString());
        }
    }

    // IDataStore.GetPermissionsAsync is the seam RecordService and FormService call, so the store must hand back the
    // reader's result unchanged, through the reader's own two read-only commands.
    [Theory]
    [MemberData(nameof(SeededForms))]
    public async Task DataStore_ReportsTheReadersPermissions(int viewId, string formName, string[] expectedTables)
    {
        FormMetadata form = await LoadFormAsync(viewId, formName, expectedTables, timeout.Token);
        FormPermissions? fromReader = await CreateReader(NullSqlCommandObserver.Instance)
            .GetPermissionsAsync(form, timeout.Token);
        var recorder = new SqlCommandRecorder();
        var store = new SqlServerDataStore(new DatabaseOptions(TestDatabase.ConnectionString), recorder);

        FormPermissions? fromStore = await store.GetPermissionsAsync(form, timeout.Token);

        Assert.NotNull(fromReader);
        Assert.NotNull(fromStore);
        Assert.Equal(fromReader.Tables, fromStore.Tables);
        Assert.Equal(Pairs(fromReader.ResolvedTableNames), Pairs(fromStore.ResolvedTableNames));
        Assert.True(fromStore.CanInsert(form), $"view {viewId}: CanInsert");
        Assert.True(fromStore.CanUpdate(form), $"view {viewId}: CanUpdate");
        Assert.True(fromStore.CanDelete(form), $"view {viewId}: CanDelete");

        IReadOnlyList<RecordedCommand> commands = recorder.Commands;
        Assert.True(commands.Count == 2, DescribeCommands(commands));
        Assert.All(commands, command =>
        {
            Assert.True(command.Completed, command.ToString());
            Assert.True(command.Succeeded, command.ToString());
            Assert.False(command.IsWrite, command.ToString());
        });
        RecordedCommand permission = Assert.Single(
            commands,
            command => command.CommandText.Contains(PermissionFunction, StringComparison.Ordinal));
        Assert.Equal(
            expectedTables.Select(table => "[" + table + "]").Order(StringComparer.Ordinal),
            permission.Parameters
                .Select(parameter => Assert.IsType<string>(parameter.Value))
                .Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(SeededForms))]
    public async Task MissingPageTable_GetsNoEntry_AndDeniesInsertAndUpdate(
        int viewId,
        string formName,
        string[] expectedTables)
    {
        FormMetadata seeded = await LoadFormAsync(viewId, formName, expectedTables, timeout.Token);
        string missingTable = seeded.BaseTableName + "99";
        await AssertNotInCatalogueAsync(missingTable, timeout.Token);
        int lastPosition = seeded.Pages.Max(page => page.Position);
        FormMetadata form = seeded with
        {
            Pages = [.. seeded.Pages, new PageMetadata(99, "Page 99", lastPosition + 1, missingTable)],
        };
        var recorder = new SqlCommandRecorder();

        FormPermissions? permissions = await CreateReader(recorder).GetPermissionsAsync(form, timeout.Token);

        Assert.NotNull(permissions);
        Assert.Equal(expectedTables, permissions.Tables.Select(table => table.TableName));
        Assert.Null(permissions.Find(missingTable));
        Assert.False(permissions.CanInsertInto(missingTable));
        Assert.False(permissions.CanInsert(form));
        Assert.False(permissions.CanUpdate(form));
        Assert.True(permissions.CanDelete(form));

        RecordedCommand permission = Assert.Single(
            recorder.Commands,
            command => command.CommandText.Contains(PermissionFunction, StringComparison.Ordinal));
        Assert.Equal(expectedTables.Length, permission.Parameters.Count);
    }

    [Fact]
    public async Task MissingBaseTable_GetsNoEntry_AndDeniesEveryFormAction()
    {
        FormMetadata seeded = await LoadFormAsync(
            2,
            "FoodHistory",
            ["FoodHistory", "FoodHistory2", "FoodHistory3"],
            timeout.Token);
        const string missingTable = "FoodHistoryMissing";
        await AssertNotInCatalogueAsync(missingTable, timeout.Token);
        FormMetadata form = seeded with { BaseTableName = missingTable };

        FormPermissions? permissions = await CreateReader(NullSqlCommandObserver.Instance)
            .GetPermissionsAsync(form, timeout.Token);

        Assert.NotNull(permissions);
        Assert.Equal(["FoodHistory2", "FoodHistory3"], permissions.Tables.Select(table => table.TableName));
        Assert.Null(permissions.Find(missingTable));
        Assert.False(permissions.CanInsert(form));
        Assert.False(permissions.CanUpdate(form));
        Assert.False(permissions.CanDelete(form));
        Assert.True(permissions.CanInsertInto("FoodHistory2"));
    }

    [Fact]
    public async Task NoTableInTheCatalogue_SendsNoPermissionStatement_AndDeniesEverything()
    {
        FormMetadata seeded = await LoadFormAsync(1, "CaseReport", ["CaseReport", "CaseReport1"], timeout.Token);
        const string missingBase = "CaseReportMissing";
        const string missingPage = "CaseReportMissing1";
        await AssertNotInCatalogueAsync(missingBase, timeout.Token);
        await AssertNotInCatalogueAsync(missingPage, timeout.Token);
        FormMetadata form = seeded with
        {
            BaseTableName = missingBase,
            Pages = [.. seeded.Pages.Select(page => page with { TableName = missingPage })],
        };
        var recorder = new SqlCommandRecorder();

        FormPermissions? permissions = await CreateReader(recorder).GetPermissionsAsync(form, timeout.Token);

        Assert.NotNull(permissions);
        Assert.Empty(permissions.Tables);
        Assert.False(permissions.CanInsert(form));
        Assert.False(permissions.CanUpdate(form));
        Assert.False(permissions.CanDelete(form));
        RecordedCommand catalogue = Assert.Single(recorder.Commands);
        Assert.True(catalogue.Succeeded, catalogue.ToString());
        Assert.DoesNotContain(PermissionFunction, catalogue.CommandText, StringComparison.OrdinalIgnoreCase);
    }

    // Seeded metadata can spell a table differently from the catalogue, so each entry carries the catalogue spelling,
    // and the result maps each requested spelling to it. Lookups are exact: a spelling that was neither requested nor
    // the catalogue's reaches no entry.
    [Theory]
    [MemberData(nameof(SeededForms))]
    public async Task NamesInAnotherLetterCase_ComeBackInTheCatalogueSpelling(
        int viewId,
        string formName,
        string[] expectedTables)
    {
        FormMetadata seeded = await LoadFormAsync(viewId, formName, expectedTables, timeout.Token);
        FormMetadata form = seeded with
        {
            BaseTableName = seeded.BaseTableName.ToUpperInvariant(),
            Pages = [.. seeded.Pages.Select(page => page with { TableName = page.TableName.ToLowerInvariant() })],
        };

        FormPermissions? permissions = await CreateReader(NullSqlCommandObserver.Instance)
            .GetPermissionsAsync(form, timeout.Token);

        Assert.NotNull(permissions);
        Assert.Equal(expectedTables, permissions.Tables.Select(table => table.TableName));
        Assert.All(permissions.Tables, table => Assert.True(table.CanInsert && table.CanUpdate, table.TableName));
        Assert.True(permissions.CanInsert(form));
        Assert.True(permissions.CanUpdate(form));
        Assert.True(permissions.CanDelete(form));
        Assert.All(form.Pages, page => Assert.True(permissions.CanInsertInto(page.TableName), page.TableName));

        // The requested names are the base table in upper case and the pages in lower case, in page order.
        string[] requested =
        [
            form.BaseTableName,
            .. form.Pages.OrderBy(page => page.Position).Select(page => page.TableName),
        ];
        Assert.Equal(
            requested.Zip(expectedTables).OrderBy(pair => pair.First, StringComparer.Ordinal),
            Pairs(permissions.ResolvedTableNames));

        string[] unrequested =
        [
            seeded.BaseTableName.ToLowerInvariant(),
            .. seeded.Pages.Select(page => page.TableName.ToUpperInvariant()),
        ];
        Assert.All(unrequested, name =>
        {
            Assert.DoesNotContain(name, requested, StringComparer.Ordinal);
            Assert.DoesNotContain(name, expectedTables, StringComparer.Ordinal);
            Assert.Null(permissions.Find(name));
            Assert.False(permissions.CanInsertInto(name), name);
        });
    }

    // The map's pairs in ordinal key order, so two maps compare regardless of insertion order.
    private static IEnumerable<(string Key, string Value)> Pairs(IReadOnlyDictionary<string, string> map) =>
        map.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => (pair.Key, pair.Value));

    // The reader under test reports its catalogue read and its permission statement to the observer.
    private static SqlPermissionReader CreateReader(ISqlCommandObserver observer)
    {
        var options = new DatabaseOptions(TestDatabase.ConnectionString);
        var factory = new SqlConnectionFactory(options);
        return new SqlPermissionReader(factory, new SqlMetadataReader(factory, options, observer), options, observer);
    }

    // Loads the form as the store would for a request, through a metadata reader whose commands nobody records, after
    // confirming by SQL that the view id names the form; the metadata must name the expected tables.
    private static async Task<FormMetadata> LoadFormAsync(
        int viewId,
        string formName,
        string[] expectedTables,
        CancellationToken cancellationToken)
    {
        await using (SqlConnection connection = await TestDatabase.OpenConnectionAsync(cancellationToken))
        {
            await using var command = new SqlCommand(
                "SELECT [Name] FROM [metaViews] WHERE [ViewId] = @viewId;",
                connection);
            command.Parameters.Add(new SqlParameter("@viewId", SqlDbType.Int) { Value = viewId });
            Assert.Equal(formName, await command.ExecuteScalarAsync(cancellationToken) as string);
        }

        var options = new DatabaseOptions(TestDatabase.ConnectionString);
        var metadataReader = new SqlMetadataReader(
            new SqlConnectionFactory(options),
            options,
            NullSqlCommandObserver.Instance);
        FormMetadata? form = await metadataReader.GetFormMetadataAsync(viewId, cancellationToken);

        Assert.NotNull(form);
        Assert.Equal(
            expectedTables,
            form.Pages.OrderBy(page => page.Position).Select(page => page.TableName).Prepend(form.BaseTableName));
        return form;
    }

    // An independent read of the same permissions, quoting the one-part name itself rather than through SqlIdentifier.
    private static async Task<(bool CanInsert, bool CanUpdate)> HasPermsByNameAsync(
        SqlConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            "SELECT HAS_PERMS_BY_NAME(@securable, N'OBJECT', N'INSERT'), " +
            "HAS_PERMS_BY_NAME(@securable, N'OBJECT', N'UPDATE');",
            connection);
        command.Parameters.Add(new SqlParameter("@securable", SqlDbType.NVarChar, 258)
        {
            Value = "[" + tableName.Replace("]", "]]", StringComparison.Ordinal) + "]",
        });

        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        return (Grants(reader, 0), Grants(reader, 1));

        static bool Grants(SqlDataReader row, int ordinal) => !row.IsDBNull(ordinal) && row.GetInt32(ordinal) == 1;
    }

    private static async Task AssertNotInCatalogueAsync(string tableName, CancellationToken cancellationToken)
    {
        await using SqlConnection connection = await TestDatabase.OpenConnectionAsync(cancellationToken);
        await using var command = new SqlCommand(
            "SELECT COUNT(*) FROM sys.tables WHERE [name] = @name;",
            connection);
        command.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, 128) { Value = tableName });
        Assert.Equal(0, (int)(await command.ExecuteScalarAsync(cancellationToken))!);
    }

    private static string DescribeCommands(IReadOnlyList<RecordedCommand> commands) =>
        commands.Count + " command(s): " + string.Join(" || ", commands.Select(command => command.ToString()));
}
