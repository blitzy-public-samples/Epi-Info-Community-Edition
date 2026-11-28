using System.Data;
using EpiInfo.Data.Metadata;
using EpiInfo.Data.Sql;
using EpiInfo.Domain.Metadata;
using EpiInfo.Tests.Shared;
using Microsoft.Data.SqlClient;

namespace EpiInfo.Data.Tests.Metadata;

/// <summary>
/// Integration tests of the write-time form verification of <see cref="SqlMetadataReader"/> against the rehearsal
/// database: <see cref="SqlMetadataReader.VerifyOrReadFormAsync"/> and
/// <see cref="SqlMetadataReader.FingerprintMatchesAsync"/>. A write's transaction verifies the request's own form read
/// with the one <see cref="MetadataQueries.FormFingerprint"/> statement and reuses that read only when the server's
/// digest equals the digest of the rows the read returned; every other form instance is read again with the six
/// statements of <see cref="SqlMetadataReader.ReadFormAsync"/>.
/// </summary>
/// <remarks>
/// The tests only read. Their transactions send no DML and are rolled back, they take no <c>DatabaseWriteLock</c>, and
/// no metadata row or schema object changes, so a digest that does not match is shown with the metadata of another
/// form or of an unknown view id rather than by editing metadata.
/// </remarks>
[Trait("Category", "Integration")]
[Collection(DatabaseCollections.Database)]
public sealed class FormVerificationTests : IAsyncLifetime
{
    // The fixed operation names of the metadata reader's form read and of its verification probe.
    private const string ReadFormOperation = "metadata.readForm";
    private const string VerifyFormOperation = "metadata.verifyForm";

    // The statements of one form read: View, DataTableName, Pages, Fields, Columns and TableClaims.
    private const int FormReadStatements = 6;

    // Every data table of the seed (testdata/sql/05_data_tables.sql).
    private static readonly string[] SeededTables =
        ["CaseReport", "CaseReport1", "FoodHistory", "FoodHistory2", "FoodHistory3"];

    // xUnit builds a new instance for every test case, so each test gets its own 60-second budget.
    private readonly CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));

    /// <summary>Each seeded form: its view id and its base table.</summary>
    public static TheoryData<int, string> SeededForms => new()
    {
        { 1, "CaseReport" },
        { 2, "FoodHistory" },
    };

    /// <summary>Each seeded form: its view id, its base table, and the view id of the other seeded form.</summary>
    public static TheoryData<int, string, int> SeededFormPairs => new()
    {
        { 1, "CaseReport", 2 },
        { 2, "FoodHistory", 1 },
    };

    /// <inheritdoc />
    public Task InitializeAsync() => TestDatabase.EnsureReachableAsync();

    /// <inheritdoc />
    public Task DisposeAsync()
    {
        timeout.Dispose();
        return Task.CompletedTask;
    }

    // The fast path of every write: the instance GetFormMetadataAsync returned is verified inside the transaction by
    // one probe, which binds only the view id and the base name, names no record table, and is sent again on every
    // call, so no verification is kept; the very instance and its attached schema come back.
    [Theory]
    [MemberData(nameof(SeededForms))]
    public async Task RequestsOwnRead_IsVerifiedInsideTheTransactionWithOneStatementOnEveryCall(
        int viewId,
        string baseTable)
    {
        CancellationToken ct = timeout.Token;
        var recorder = new SqlCommandRecorder();
        SqlMetadataReader reader = CreateReader(recorder);
        FormMetadata form = await ReadAsync(reader, viewId, ct);
        Assert.True(reader.TryGetAttachedSchema(form, out FormSchema? attached), "No schema is attached to the read.");
        Assert.Equal(baseTable, attached.BaseTable.Name);

        await using SqlConnection connection = await TestDatabase.OpenConnectionAsync(ct);
        await using var transaction =
            (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        for (int call = 1; call <= 2; call++)
        {
            recorder.Clear();

            (FormMetadata Form, FormSchema Schema)? verified =
                await reader.VerifyOrReadFormAsync(connection, transaction, form, ct);

            Assert.True(verified is not null, $"Call {call} found no form.");
            Assert.Same(form, verified.Value.Form);
            Assert.Same(attached, verified.Value.Schema);
            IReadOnlyList<RecordedCommand> commands = recorder.Commands;
            Assert.True(commands.Count == 1, $"Call {call}: " + Describe(commands));
            RecordedCommand probe = commands[0];
            Assert.Equal(VerifyFormOperation, probe.Operation);
            Assert.Equal(MetadataQueries.FormFingerprint, probe.CommandText);
            Assert.True(
                probe.Completed && probe.Succeeded && probe.InTransaction && !probe.IsWrite,
                probe.Describe(includeParameterValues: false));
            Assert.Equal(
                new (string Name, DbType DbType, object? Value)[]
                {
                    (MetadataQueries.ViewIdParameter, DbType.Int32, viewId),
                    (MetadataQueries.BaseNameParameter, DbType.String, baseTable),
                },
                probe.Parameters.Select(parameter => (parameter.Name, parameter.DbType, parameter.Value)));
            Assert.Empty(SqlCommandRecorder.BracketedIdentifiers(probe.CommandText)
                .Intersect(SeededTables, StringComparer.OrdinalIgnoreCase));
        }

        await transaction.RollbackAsync(ct);
    }

    // A copy of the request's form carries no attached read, so the transaction reads the form again with the six
    // statements of a form read, and sends no probe; the form it returns is a new instance of the same form.
    [Theory]
    [MemberData(nameof(SeededForms))]
    public async Task AnotherInstance_IsReadAgainInsideTheTransaction(int viewId, string baseTable)
    {
        CancellationToken ct = timeout.Token;
        var recorder = new SqlCommandRecorder();
        SqlMetadataReader reader = CreateReader(recorder);
        FormMetadata form = await ReadAsync(reader, viewId, ct);
        FormMetadata copy = form with { };
        Assert.False(reader.TryGetAttachedSchema(copy, out _), "A copy must carry no attached schema.");

        await using SqlConnection connection = await TestDatabase.OpenConnectionAsync(ct);
        await using var transaction =
            (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        recorder.Clear();

        (FormMetadata Form, FormSchema Schema)? read =
            await reader.VerifyOrReadFormAsync(connection, transaction, copy, ct);

        Assert.True(read is not null, "The copy's form was not found.");
        Assert.NotSame(copy, read.Value.Form);
        Assert.Equal(viewId, read.Value.Form.ViewId);
        Assert.Equal(baseTable, read.Value.Form.BaseTableName);
        Assert.Equal(form.Pages, read.Value.Form.Pages);
        Assert.Equal(form.Fields.Select(field => field.Name), read.Value.Form.Fields.Select(field => field.Name));
        IReadOnlyList<RecordedCommand> commands = recorder.Commands;
        Assert.True(
            commands.Count == FormReadStatements
                && commands.All(command => command.Operation == ReadFormOperation
                    && command.InTransaction
                    && command.Succeeded
                    && !command.IsWrite),
            Describe(commands));

        await transaction.RollbackAsync(ct);
    }

    // The probe digests the metadata as it stands for the view id it is sent with, so the attached read matches its
    // own form and neither another form's metadata nor a view id no metaViews row holds; a form with no attached read
    // matches nothing and sends no command.
    [Theory]
    [MemberData(nameof(SeededFormPairs))]
    public async Task Digest_MatchesOnlyTheMetadataTheReadReturned(int viewId, string baseTable, int otherViewId)
    {
        CancellationToken ct = timeout.Token;
        var recorder = new SqlCommandRecorder();
        SqlMetadataReader reader = CreateReader(recorder);
        FormMetadata form = await ReadAsync(reader, viewId, ct);
        Assert.Equal(baseTable, form.BaseTableName);
        int unknownViewId = await UnknownViewIdAsync(ct);

        await using SqlConnection connection = await TestDatabase.OpenConnectionAsync(ct);
        recorder.Clear();
        Assert.True(await reader.FingerprintMatchesAsync(connection, transaction: null, viewId, form, ct));
        Assert.False(await reader.FingerprintMatchesAsync(connection, transaction: null, otherViewId, form, ct));
        Assert.False(await reader.FingerprintMatchesAsync(connection, transaction: null, unknownViewId, form, ct));
        IReadOnlyList<RecordedCommand> probes = recorder.Commands;
        Assert.True(
            probes.Count == 3
                && probes.All(command => command.Operation == VerifyFormOperation && command.Succeeded),
            Describe(probes));

        recorder.Clear();
        Assert.False(await reader.FingerprintMatchesAsync(connection, transaction: null, viewId, form with { }, ct));
        Assert.Empty(recorder.Commands);
    }

    private static SqlMetadataReader CreateReader(ISqlCommandObserver observer)
    {
        var options = new DatabaseOptions(TestDatabase.ConnectionString);
        return new SqlMetadataReader(new SqlConnectionFactory(options), options, observer);
    }

    private static async Task<FormMetadata> ReadAsync(SqlMetadataReader reader, int viewId, CancellationToken ct)
    {
        FormMetadata? form = await reader.GetFormMetadataAsync(viewId, ct);
        Assert.True(form is not null, $"View {viewId} has no metadata.");
        return form;
    }

    // A view id no metaViews row holds, read by direct SQL.
    private static async Task<int> UnknownViewIdAsync(CancellationToken ct)
    {
        await using SqlConnection connection = await TestDatabase.OpenConnectionAsync(ct);
        await using var command = new SqlCommand("SELECT MAX([ViewId]) + 1 FROM [metaViews];", connection);
        return Assert.IsType<int>(await command.ExecuteScalarAsync(ct));
    }

    private static string Describe(IReadOnlyList<RecordedCommand> commands) =>
        commands.Count + " command(s): " +
        string.Join(" || ", commands.Select(command => command.Describe(includeParameterValues: false)));
}

/// <summary>
/// The row encoding of the form verification digest, <see cref="SqlMetadataReader.EncodeFingerprintRow"/>, which the
/// probe <see cref="MetadataQueries.FormFingerprint"/> mirrors on the server. It needs no database.
/// </summary>
public sealed class FormFingerprintEncodingTests
{
    /// <summary>Pairs of different value lists of one row, which must never encode alike.</summary>
    public static TheoryData<string?[], string?[]> DifferentRows => new()
    {
        { [null], [string.Empty] },
        { [string.Empty], ["-"] },
        { [null, "x"], ["-", "x"] },
        { ["ab", "c"], ["a", "bc"] },
        { ["12"], ["1", "2"] },
        { ["1:a"], ["1", "a"] },
        { ["0"], ["0:"] },
        { ["a", null], [null, "a"] },
    };

    [Theory]
    [MemberData(nameof(DifferentRows))]
    public void DifferentValues_NeverEncodeAlike(string?[] first, string?[] second) =>
        Assert.NotEqual(
            SqlMetadataReader.EncodeFingerprintRow('F', first),
            SqlMetadataReader.EncodeFingerprintRow('F', second));

    // The encoding the probe's CONCAT builds for a seeded page row (2, Page 1, 0) and for a row with a null value: the
    // tag, then each value as its length in UTF-16 code units, a colon and its text, or "-" for NULL.
    [Fact]
    public void Row_IsItsTagThenEachValueLengthPrefixed()
    {
        Assert.Equal("P1:26:Page 11:0", SqlMetadataReader.EncodeFingerprintRow('P', "2", "Page 1", "0"));
        Assert.Equal("L1:1-2:12", SqlMetadataReader.EncodeFingerprintRow('L', "1", null, "12"));
        Assert.Equal("T2:\ud83d\ude00", SqlMetadataReader.EncodeFingerprintRow('T', "\ud83d\ude00"));
    }
}
