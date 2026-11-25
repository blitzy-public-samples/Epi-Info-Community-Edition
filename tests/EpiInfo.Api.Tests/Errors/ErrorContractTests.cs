using System.Globalization;
using System.Net;
using System.Text.Json;
using EpiInfo.Api.Endpoints;
using EpiInfo.Api.Errors;
using EpiInfo.Api.Hosting;
using EpiInfo.Api.Json;
using EpiInfo.Api.Services;
using EpiInfo.Api.Tests.Infrastructure;
using EpiInfo.CheckCode;
using EpiInfo.Domain.Records;
using EpiInfo.Domain.Storage;
using EpiInfo.Domain.Values;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EpiInfo.Api.Tests.Errors;

/// <summary>
/// The write-outcome bookkeeping of the three record writes (AAP 0.3.4.2, 0.3.4.6), proved over HTTP through the real
/// record endpoints, <see cref="RecordService"/> and <see cref="ApiExceptionHandler"/>: a commit the store returned is
/// reported <c>written</c>, a definitive result that wrote nothing <c>notWritten</c>, and a started write that fails
/// without a classification <c>unknown</c>; a throwing audit logger never changes a committed response; and the 404
/// and 409 of a write send no data statement and change nothing.
/// </summary>
/// <remarks>
/// <para>
/// The class needs no database: it carries no <c>Integration</c> trait, joins no database collection and takes no
/// <c>DatabaseWriteLock</c>, so <c>--filter "Category!=Integration"</c> selects it. Every case starts its own
/// <see cref="FakeDataStore"/>, clock and host. The planned <c>ErrorContractTests</c> class, with the error-contract,
/// CORS and redaction cases hosted on <c>ApiFactory</c>, belongs beside it in this file.
/// </para>
/// <para>
/// A failure after the endpoint has chosen its result is injected by a one-shot seam that makes the response body
/// throw before the response starts, so the exception handler still answers and states what the request recorded.
/// </para>
/// </remarks>
public sealed class RecordWriteOutcomeTests
{
    private const string Post = "POST";
    private const string Put = "PUT";
    private const string Delete = "DELETE";

    private const string PutMissingRecord = "PUT on a missing record";
    private const string PutDeletedRecord = "PUT on a deleted record";
    private const string PutFailingRequiredGate = "PUT failing the stored required gate";
    private const string PutDeniedMissingPageInsert = "PUT needing a denied missing-page INSERT";
    private const string DeleteMissingRecord = "DELETE of a missing record";
    private const string DeleteDeletedRecord = "DELETE of an already-deleted record";

    private const string WriteOutcomeMember = "writeOutcome";
    private const string StoreFailureText = "store failure injected by RecordWriteOutcomeTests";

    private const string LastName = "LastName";
    private const string Age = "Age";
    private const string Sex = "Sex";
    private const string Ill = "Ill";
    private const string Notes = "Notes";
    private const string Comment = "Comment";

    private const string CreatedLastName = "Created";
    private const double CreatedAge = 42;
    private const string CreatedNotes = "first visit";
    private const string SeededLastName = "Seeded";
    private const double SeededAge = 30;
    private const string SeededNotes = "seeded note";
    private const string UpdatedLastName = "Updated";
    private const string SeededComment = "seeded comment";
    private const string UpdatedComment = "updated comment";

    private const int TwoPageViewId = FakeForms.TwoPageViewId;

    private static readonly DateTime WriteTime = new(2024, 6, 3, 10, 0, 0, DateTimeKind.Unspecified);

    /// <summary>The three record writes.</summary>
    public static TheoryData<string> WriteMethods => new() { Post, Put, Delete };

    /// <summary>Each record write with each stage at which the store throws an unclassified failure.</summary>
    public static TheoryData<string, FakeFailureStage> UnclassifiedStoreFailures => new()
    {
        { Post, FakeFailureStage.BeforeWrite },
        { Post, FakeFailureStage.AfterWrite },
        { Put, FakeFailureStage.BeforeWrite },
        { Put, FakeFailureStage.AfterWrite },
        { Delete, FakeFailureStage.BeforeWrite },
        { Delete, FakeFailureStage.AfterWrite },
    };

    /// <summary>Every result by which the store reports that a started write wrote nothing.</summary>
    public static TheoryData<string> DefinitiveNoWriteResults => new()
    {
        PutMissingRecord,
        PutDeletedRecord,
        PutFailingRequiredGate,
        PutDeniedMissingPageInsert,
        DeleteMissingRecord,
        DeleteDeletedRecord,
    };

    /// <summary>A valid <c>PUT</c> and a <c>DELETE</c> naming a record the base table does not hold.</summary>
    public static TheoryData<string> MissingRecordWrites => new() { PutMissingRecord, DeleteMissingRecord };

    // A returned commit is marked written by the service itself, so a failure that follows it, before the
    // response starts, reports written, and the change the store committed is the one the request asked for.
    [Theory]
    [MemberData(nameof(WriteMethods))]
    public async Task CommittedWriteThatFailsBeforeTheResponseStartsReportsWritten(string method)
    {
        var (store, request) = ArrangeCommittableWrite(method);
        await using var host = await RecordWriteHost.StartAsync(store);
        var before = store.GetRecords(request.ViewId);
        host.Clock.SetLocalNow(WriteTime);
        host.Seam.Arm();

        using var response = await SendAsync(host.Client, request);

        Assert.True(host.Seam.Fired, "The post-result failure seam never raised its failure.");
        await AssertWriteProblemAsync(response, WriteOutcomeExtensions.WireWritten);
        Assert.Equal(StoreWriteMethod(method), Assert.Single(store.WriteCalls).Method);
        AssertCommitted(store, request, before);
    }

    // The post-commit audit line is written so that its own failure cannot change the response (AAP 0.3.4.6).
    [Theory]
    [MemberData(nameof(WriteMethods))]
    public async Task ThrowingAuditLoggerLeavesTheCommittedResponseUnchanged(string method)
    {
        var (store, request) = ArrangeCommittableWrite(method);
        var auditLogger = new ThrowingAuditLoggerProvider();
        await using var host = await RecordWriteHost.StartAsync(store, auditLogger);
        var before = store.GetRecords(request.ViewId);
        host.Clock.SetLocalNow(WriteTime);

        using var response = await SendAsync(host.Client, request);

        Assert.Equal(1, auditLogger.Throws);
        switch (method)
        {
            case Post:
                Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                var created = await ApiTestClient.ReadJsonElementAsync(response);
                var createdId = created.GetProperty("globalRecordId").GetString();
                Assert.NotNull(createdId);
                Assert.Equal(RecordPath(request.ViewId, createdId), response.Headers.Location?.OriginalString);
                Assert.Equal(CreatedLastName, created.GetProperty("values").GetProperty(LastName).GetString());
                Assert.Equal(Assert.Single(store.GetRecords(request.ViewId)).GlobalRecordId, createdId);
                break;

            case Put:
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var updated = await ApiTestClient.ReadJsonElementAsync(response);
                Assert.Equal(request.GlobalRecordId, updated.GetProperty("globalRecordId").GetString());
                Assert.Equal(UpdatedLastName, updated.GetProperty("values").GetProperty(LastName).GetString());
                break;

            case Delete:
                Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
                Assert.Empty(await response.Content.ReadAsByteArrayAsync());
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(method), method, "Not a record write.");
        }

        Assert.Empty(host.Logs.Find(entry => entry.Category == typeof(ApiExceptionHandler).FullName));
        Assert.Equal(StoreWriteMethod(method), Assert.Single(store.WriteCalls).Method);
        AssertCommitted(store, request, before);
    }

    // Once the store call is issued, a failure that is not a store exception cannot say whether data was
    // written, so it is unknown whether the fake threw before its change or after applying it.
    [Theory]
    [MemberData(nameof(UnclassifiedStoreFailures))]
    public async Task StartedWriteThatFailsUnclassifiedReportsUnknown(string method, FakeFailureStage stage)
    {
        var (store, request) = ArrangeCommittableWrite(method);
        store.ThrowOn(StoreWriteMethod(method), new InvalidOperationException(StoreFailureText), stage);
        await using var host = await RecordWriteHost.StartAsync(store);
        var before = store.GetRecords(request.ViewId);
        host.Clock.SetLocalNow(WriteTime);

        using var response = await SendAsync(host.Client, request);

        await AssertWriteProblemAsync(response, WriteOutcomeExtensions.WireUnknown);
        if (stage == FakeFailureStage.BeforeWrite)
        {
            Assert.Empty(store.WriteCalls);
            AssertSameRecords(before, store.GetRecords(request.ViewId));
        }
        else
        {
            Assert.Equal(StoreWriteMethod(method), Assert.Single(store.WriteCalls).Method);
            AssertCommitted(store, request, before);
        }
    }

    // Every result other than a commit means the store wrote nothing (IDataStore), so a failure while that
    // result's response is written, before it starts, reports notWritten rather than unknown.
    [Theory]
    [MemberData(nameof(DefinitiveNoWriteResults))]
    public async Task DefinitiveNoWriteResultThatFailsBeforeTheResponseStartsReportsNotWritten(string scenario)
    {
        var (store, request) = ArrangeNoWrite(scenario);
        await using var host = await RecordWriteHost.StartAsync(store);
        var before = store.GetRecords(request.ViewId);
        host.Clock.SetLocalNow(WriteTime);
        host.Seam.Arm();

        using var response = await SendAsync(host.Client, request);

        Assert.True(host.Seam.Fired, "The post-result failure seam never raised its failure.");
        await AssertWriteProblemAsync(response, WriteOutcomeExtensions.WireNotWritten);
        Assert.Contains(store.Calls, call => call.Method == StoreWriteMethod(request.Method) && !call.IsWrite);
        Assert.Empty(store.WriteCalls);
        AssertSameRecords(before, store.GetRecords(request.ViewId));
    }

    // A PUT on a record the test created and then deleted is the fixed 409, decided by the store's locked read
    // with no data statement, and the record, its stamps included, is unchanged.
    [Fact]
    public async Task PutOnARecordTheTestDeletedIs409AndWritesNothing()
    {
        var store = new FakeDataStore(FakeForms.TwoPage);
        await using var host = await RecordWriteHost.StartAsync(store);
        host.Clock.SetLocalNow(WriteTime);
        var created = await ApiTestClient.CreateRecordAsync(host.Client, TwoPageViewId, CreatedValues());
        var path = RecordPath(TwoPageViewId, created.GlobalRecordId);

        host.Clock.Advance(TimeSpan.FromMinutes(1));
        using (var deleted = await host.Client.DeleteAsync(path))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        var before = store.GetRecord(TwoPageViewId, created.GlobalRecordId);
        Assert.NotNull(before);
        Assert.Equal(RecordQueryRules.DeletedRecStatus, before.RecStatus);
        store.ClearCalls();
        host.Clock.Advance(TimeSpan.FromMinutes(1));

        using var response = await ApiTestClient.PutJsonAsync(host.Client, path, new { values = UpdatedValues() });

        await AssertPlainProblemAsync(response, StatusCodes.Status409Conflict, ApiProblems.RecordDeletedDetail);
        Assert.Contains(store.Calls, call => call.Method == nameof(IDataStore.UpdateAsync) && !call.IsWrite);
        Assert.Empty(store.WriteCalls);
        AssertSameRecord(before, store.GetRecord(TwoPageViewId, created.GlobalRecordId));
    }

    // A valid PUT or DELETE naming an id the base table does not hold is the fixed 404, decided by the store's
    // locked read with no data statement, and the form's records are unchanged.
    [Theory]
    [MemberData(nameof(MissingRecordWrites))]
    public async Task ValidWriteOnAMissingRecordIs404AndWritesNothing(string scenario)
    {
        var (store, request) = ArrangeNoWrite(scenario);
        await using var host = await RecordWriteHost.StartAsync(store);
        var before = store.GetRecords(request.ViewId);
        host.Clock.SetLocalNow(WriteTime);

        using var response = await SendAsync(host.Client, request);

        await AssertPlainProblemAsync(response, StatusCodes.Status404NotFound, ApiProblems.RecordNotFoundDetail);
        Assert.Contains(store.Calls, call => call.Method == StoreWriteMethod(request.Method) && !call.IsWrite);
        Assert.Empty(store.WriteCalls);
        AssertSameRecords(before, store.GetRecords(request.ViewId));
    }

    // A definitive no-write result ends the started write, so an unclassified failure after it is notWritten;
    // a started write without one stays unknown; and a commit never reverts.
    [Fact]
    public void NotWrittenEndsAStartedWriteButNeverUndoesACommit()
    {
        var failure = new InvalidOperationException(StoreFailureText);

        var started = new WriteOutcomeFeature();
        started.MarkWriteStarted();
        Assert.Equal(WriteOutcome.Unknown, ApiExceptionHandler.ResolveWriteOutcome(started, failure));

        started.MarkNotWritten();
        Assert.False(started.WriteStarted);
        Assert.False(started.Written);
        Assert.Equal(WriteOutcome.NotWritten, ApiExceptionHandler.ResolveWriteOutcome(started, failure));

        var committed = new WriteOutcomeFeature();
        committed.MarkWritten();
        committed.MarkNotWritten();
        Assert.True(committed.Written);
        Assert.True(committed.WriteStarted);
        Assert.Equal(WriteOutcome.Written, ApiExceptionHandler.ResolveWriteOutcome(committed, failure));
    }

    // A two-page store and a write the store commits: a POST of a new record, or a PUT or DELETE of a seeded active
    // one.
    private static (FakeDataStore Store, WriteRequest Request) ArrangeCommittableWrite(string method)
    {
        var store = new FakeDataStore(FakeForms.TwoPage);
        switch (method)
        {
            case Post:
                return (store, new WriteRequest(Post, TwoPageViewId, null, new { values = CreatedValues() }));

            case Put:
                var updated = store.SeedRecord(TwoPageViewId, SeededValues());
                return (store, new WriteRequest(Put, TwoPageViewId, updated, new { values = UpdatedValues() }));

            case Delete:
                var deleted = store.SeedRecord(TwoPageViewId, SeededValues());
                return (store, new WriteRequest(Delete, TwoPageViewId, deleted, null));

            default:
                throw new ArgumentOutOfRangeException(nameof(method), method, "Not a record write.");
        }
    }

    // A store and a write that passes every check before the store call, for which the store returns a result that
    // wrote nothing. Each store also holds a record the write must leave alone, so "unchanged" is never vacuous.
    private static (FakeDataStore Store, WriteRequest Request) ArrangeNoWrite(string scenario)
    {
        if (scenario == PutFailingRequiredGate)
        {
            // The stored required field is null and the PUT does not submit it, so only the store's gate, run on the
            // stored record overlaid with the submitted values, can reject the save.
            var requiredStore = new FakeDataStore(FakeForms.WithRequiredField);
            var incomplete = requiredStore.SeedRecord(
                FakeForms.RequiredViewId,
                new Dictionary<string, FieldValue> { [Comment] = FieldValue.FromText(SeededComment) });
            var values = new Dictionary<string, object?> { [Comment] = UpdatedComment };
            return (requiredStore, new WriteRequest(Put, FakeForms.RequiredViewId, incomplete, new { values }));
        }

        var form = FakeForms.TwoPage;
        var store = new FakeDataStore(form);
        var update = new { values = UpdatedValues() };
        switch (scenario)
        {
            case PutMissingRecord:
                store.SeedRecord(TwoPageViewId, SeededValues());
                return (store, new WriteRequest(Put, TwoPageViewId, MissingId(), update));

            case PutDeletedRecord:
                var deletedForPut = store.SeedRecord(TwoPageViewId, SeededValues(), RecordQueryRules.DeletedRecStatus);
                return (store, new WriteRequest(Put, TwoPageViewId, deletedForPut, update));

            case PutDeniedMissingPageInsert:
                // UPDATE everywhere passes the service's check; the record's page-two row is missing and INSERT on that
                // page table is denied, which only the store's locked read can find.
                var pages = form.Metadata.Pages;
                var partial = store.SeedRecord(TwoPageViewId, SeededValues(), missingPageIds: [pages[1].PageId]);
                store.SetPermissions(
                    TwoPageViewId,
                    new FormPermissions(
                    [
                        new TablePermission(form.Metadata.BaseTableName, CanInsert: true, CanUpdate: true),
                        new TablePermission(pages[0].TableName, CanInsert: true, CanUpdate: true),
                        new TablePermission(pages[1].TableName, CanInsert: false, CanUpdate: true),
                    ]));
                return (store, new WriteRequest(Put, TwoPageViewId, partial, update));

            case DeleteMissingRecord:
                store.SeedRecord(TwoPageViewId, SeededValues());
                return (store, new WriteRequest(Delete, TwoPageViewId, MissingId(), null));

            case DeleteDeletedRecord:
                var deleted = store.SeedRecord(TwoPageViewId, SeededValues(), RecordQueryRules.DeletedRecStatus);
                return (store, new WriteRequest(Delete, TwoPageViewId, deleted, null));

            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unknown no-write scenario.");
        }
    }

    // The change each committed write makes, stamped with the controlled clock: a new active record holding the
    // submitted values with both stamps; the submitted value and the last-save stamp; or RecStatus 0 and the last-save
    // stamp. Nothing else may differ.
    private static void AssertCommitted(
        FakeDataStore store,
        WriteRequest request,
        IReadOnlyList<FakeRecordState> before)
    {
        var stamp = new SaveStamp(Environment.UserName, WriteTime);
        var after = store.GetRecords(request.ViewId);
        switch (request.Method)
        {
            case Post:
                Assert.Empty(before);
                var created = Assert.Single(after);
                var allPages = FakeForms.TwoPage.Metadata.Pages.Select(page => page.PageId).ToHashSet();
                var expectedValues = new Dictionary<string, FieldValue>(StringComparer.OrdinalIgnoreCase)
                {
                    [LastName] = FieldValue.FromText(CreatedLastName),
                    [Age] = FieldValue.FromNumber(CreatedAge),
                    [Sex] = FieldValue.Null,
                    [Ill] = FieldValue.FromBoolean(true),
                    [Notes] = FieldValue.FromText(CreatedNotes),
                };
                AssertSameRecord(
                    new FakeRecordState(
                        created.GlobalRecordId,
                        created.UniqueKey,
                        RecordQueryRules.ActiveRecStatus,
                        expectedValues,
                        allPages,
                        stamp,
                        stamp),
                    created);
                break;

            case Put:
                var original = Assert.Single(before);
                var updatedValues = new Dictionary<string, FieldValue>(
                    original.Values,
                    StringComparer.OrdinalIgnoreCase)
                {
                    [LastName] = FieldValue.FromText(UpdatedLastName),
                };
                AssertSameRecord(original with { Values = updatedValues, LastSave = stamp }, Assert.Single(after));
                break;

            case Delete:
                var active = Assert.Single(before);
                AssertSameRecord(
                    active with { RecStatus = RecordQueryRules.DeletedRecStatus, LastSave = stamp },
                    Assert.Single(after));
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(request), request.Method, "Not a record write.");
        }
    }

    // FakeRecordState holds dictionaries and sets, which records compare by reference, so the comparison is per field.
    private static void AssertSameRecords(
        IReadOnlyList<FakeRecordState> expected,
        IReadOnlyList<FakeRecordState> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var index = 0; index < expected.Count; index++)
        {
            AssertSameRecord(expected[index], actual[index]);
        }
    }

    private static void AssertSameRecord(FakeRecordState expected, FakeRecordState? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.GlobalRecordId, actual.GlobalRecordId);
        Assert.Equal(expected.UniqueKey, actual.UniqueKey);
        Assert.Equal(expected.RecStatus, actual.RecStatus);
        Assert.Equal(expected.Values.Count, actual.Values.Count);
        foreach (var (name, value) in expected.Values)
        {
            Assert.True(actual.Values.TryGetValue(name, out var stored), $"The record holds no value for '{name}'.");
            Assert.Equal(value, stored);
        }

        Assert.Equal(expected.PresentPageIds.Order(), actual.PresentPageIds.Order());
        Assert.Equal(expected.FirstSave, actual.FirstSave);
        Assert.Equal(expected.LastSave, actual.LastSave);
    }

    // The 500 of a record write: fixed ProblemDetails whose detail is the generic sentence, which may be followed by
    // the trace id (AAP 0.3.4.2), and the expected writeOutcome.
    private static async Task AssertWriteProblemAsync(HttpResponseMessage response, string expectedOutcome)
    {
        var body = await ReadProblemAsync(response, StatusCodes.Status500InternalServerError);
        Assert.StartsWith(
            ApiProblems.UnexpectedDetail,
            body.GetProperty("detail").GetString(),
            StringComparison.Ordinal);
        Assert.True(
            body.TryGetProperty(WriteOutcomeMember, out var outcome),
            $"The write failure carries no {WriteOutcomeMember}: {body.GetRawText()}");
        Assert.Equal(expectedOutcome, outcome.GetString());
    }

    // A 4xx of a record write: fixed ProblemDetails with its own detail and no writeOutcome, which only a 500 or 503
    // of a write carries.
    private static async Task AssertPlainProblemAsync(HttpResponseMessage response, int status, string detail)
    {
        var body = await ReadProblemAsync(response, status);
        Assert.Equal(detail, body.GetProperty("detail").GetString());
        Assert.False(
            body.TryGetProperty(WriteOutcomeMember, out _),
            $"Unexpected {WriteOutcomeMember}: {body.GetRawText()}");
    }

    private static async Task<JsonElement> ReadProblemAsync(HttpResponseMessage response, int status)
    {
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(ApiProblems.ProblemContentType, response.Content.Headers.ContentType?.MediaType);
        return ApiTestClient.AssertProblemDetails(await ApiTestClient.ReadJsonElementAsync(response), status);
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, WriteRequest request) => request.Method switch
    {
        Post => ApiTestClient.PostJsonAsync(client, request.Path, request.Body),
        Put => ApiTestClient.PutJsonAsync(client, request.Path, request.Body),
        Delete => client.DeleteAsync(request.Path),
        _ => throw new ArgumentOutOfRangeException(nameof(request), request.Method, "Not a record write."),
    };

    // The IDataStore member each record write ends in.
    private static string StoreWriteMethod(string method) => method switch
    {
        Post => nameof(IDataStore.InsertAsync),
        Put => nameof(IDataStore.UpdateAsync),
        Delete => nameof(IDataStore.SoftDeleteAsync),
        _ => throw new ArgumentOutOfRangeException(nameof(method), method, "Not a record write."),
    };

    private static string RecordsPath(int viewId) =>
        string.Create(CultureInfo.InvariantCulture, $"/api/forms/{viewId}/records");

    private static string RecordPath(int viewId, string globalRecordId) => RecordsPath(viewId) + "/" + globalRecordId;

    // The format the stores write, for an id no store holds.
    private static string MissingId() => Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture);

    private static Dictionary<string, object?> CreatedValues() => new()
    {
        [LastName] = CreatedLastName,
        [Age] = CreatedAge,
        [Ill] = true,
        [Notes] = CreatedNotes,
    };

    private static Dictionary<string, FieldValue> SeededValues() => new()
    {
        [LastName] = FieldValue.FromText(SeededLastName),
        [Age] = FieldValue.FromNumber(SeededAge),
        [Notes] = FieldValue.FromText(SeededNotes),
    };

    private static Dictionary<string, object?> UpdatedValues() => new() { [LastName] = UpdatedLastName };

    /// <summary>One record write as the test sends it.</summary>
    /// <param name="Method">The HTTP method: <c>POST</c>, <c>PUT</c> or <c>DELETE</c>.</param>
    /// <param name="ViewId">The form's view id.</param>
    /// <param name="GlobalRecordId">
    /// The record a <c>PUT</c> or <c>DELETE</c> names; <see langword="null"/> for a <c>POST</c>.
    /// </param>
    /// <param name="Body">The JSON body; <see langword="null"/> for a <c>DELETE</c>, which sends none.</param>
    private sealed record WriteRequest(string Method, int ViewId, string? GlobalRecordId, object? Body)
    {
        /// <summary>The records path for a <c>POST</c>, or the record's own path.</summary>
        public string Path => GlobalRecordId is null ? RecordsPath(ViewId) : RecordPath(ViewId, GlobalRecordId);
    }

    /// <summary>
    /// A private TestServer host that composes the record-write pipeline as <c>Program.cs</c> plans it (AAP 0.3.4.7):
    /// the ProblemDetails customisation, <see cref="ApiExceptionHandler"/>, the API's JSON settings,
    /// <c>ThrowOnBadRequest</c>, status-code pages, routing, the media-type guard and the record endpoints over
    /// <see cref="RecordService"/>, with a <see cref="FakeDataStore"/> and a <see cref="ControlledTimeProvider"/>.
    /// </summary>
    /// <remarks>
    /// These cases therefore run before, and independently of, the final composition root. The host runs in the
    /// Production environment with <c>src/EpiInfo.Api</c> as its content root, so the committed
    /// <c>appsettings.json</c> logging filters apply, and its only logger providers are a
    /// <see cref="CapturingLoggerProvider"/> and, when given, one more. <see cref="PostResultFailureSeam"/> sits after
    /// routing, so its failure is raised inside the matched write endpoint's request.
    /// </remarks>
    private sealed class RecordWriteHost : IAsyncDisposable
    {
        private readonly WebApplication app;

        private RecordWriteHost(
            WebApplication app,
            ControlledTimeProvider clock,
            PostResultFailureSeam seam,
            CapturingLoggerProvider logs)
        {
            this.app = app;
            Clock = clock;
            Seam = seam;
            Logs = logs;
            Client = app.GetTestClient();
        }

        /// <summary>The client of the started host.</summary>
        public HttpClient Client { get; }

        /// <summary>The clock the save stamps are read from; set it before each write.</summary>
        public ControlledTimeProvider Clock { get; }

        /// <summary>The one-shot post-result failure; arm it before the request it should fail.</summary>
        public PostResultFailureSeam Seam { get; }

        /// <summary>Every log entry the committed filters let through.</summary>
        public CapturingLoggerProvider Logs { get; }

        /// <summary>Builds and starts a host over <paramref name="store"/>.</summary>
        /// <param name="store">The store the services use.</param>
        /// <param name="extraLoggerProvider">One more logger provider, or <see langword="null"/>.</param>
        public static async Task<RecordWriteHost> StartAsync(
            FakeDataStore store,
            ILoggerProvider? extraLoggerProvider = null)
        {
            ArgumentNullException.ThrowIfNull(store);

            var clock = new ControlledTimeProvider();
            var seam = new PostResultFailureSeam();
            var logs = new CapturingLoggerProvider();

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Production,
                ContentRootPath = RepositoryPaths.ApiProjectDirectory,
            });
            builder.WebHost.UseTestServer();

            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(logs);
            if (extraLoggerProvider is not null)
            {
                builder.Logging.AddProvider(extraLoggerProvider);
            }

            var services = builder.Services;
            services.AddProblemDetails(options => options.CustomizeProblemDetails = ApiProblems.Customize);
            services.AddExceptionHandler<ApiExceptionHandler>();
            services.AddApiJson();
            services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
            services.AddSingleton<IDataStore>(store);
            services.AddSingleton<ICheckCodeEngine>(new CheckCodeEngine());
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton<SaveStampProvider>();
            services.AddScoped<FormService>();
            services.AddScoped<RecordService>();

            var app = builder.Build();
            try
            {
                app.UseExceptionHandler();
                app.UseStatusCodePages();
                app.UseRouting();
                app.Use(seam.InvokeAsync);
                app.UseJsonContentTypeGuard();
                app.MapRecordEndpoints();
                await app.StartAsync();
                return new RecordWriteHost(app, clock, seam, logs);
            }
            catch
            {
                await app.DisposeAsync();
                throw;
            }
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    /// <summary>
    /// Middleware that, once armed, fails the next request after its endpoint has chosen a result and before the
    /// response starts, as a failing serialisation or delivery would. Disarmed, it only passes the request on.
    /// </summary>
    /// <remarks>
    /// The armed request writes to a replacement body whose every write and flush throws
    /// <see cref="InjectedResponseFailureException"/>. The replacement has no prior feature, so starting it never
    /// starts the real response. The real body is restored before the failure leaves the seam, so the exception
    /// handler writes its 500 to it. A result that writes no body, such as 204, is failed after it returns, while the
    /// response has still not started.
    /// </remarks>
    private sealed class PostResultFailureSeam
    {
        private int armed;
        private int fired;

        /// <summary>Whether the seam has raised its failure.</summary>
        public bool Fired => Volatile.Read(ref fired) == 1;

        /// <summary>Arms the seam for the next request only.</summary>
        public void Arm() => Interlocked.Exchange(ref armed, 1);

        /// <summary>The middleware body.</summary>
        /// <param name="context">The current request.</param>
        /// <param name="next">The rest of the pipeline.</param>
        public async Task InvokeAsync(HttpContext context, RequestDelegate next)
        {
            if (Interlocked.Exchange(ref armed, 0) == 0)
            {
                await next(context);
                return;
            }

            var original = context.Features.GetRequiredFeature<IHttpResponseBodyFeature>();
            context.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(new FailingBodyStream(this)));
            try
            {
                await next(context);
            }
            finally
            {
                context.Features.Set(original);
            }

            if (!context.Response.HasStarted)
            {
                throw Raise();
            }
        }

        /// <summary>Records that the failure was raised and returns it.</summary>
        public InjectedResponseFailureException Raise()
        {
            Interlocked.Exchange(ref fired, 1);
            return new InjectedResponseFailureException();
        }
    }

    /// <summary>A write-only response body whose every write and flush raises the seam's failure.</summary>
    /// <param name="seam">The seam that records the failure.</param>
    private sealed class FailingBodyStream(PostResultFailureSeam seam) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw seam.Raise();

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.FromException(seam.Raise());

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw seam.Raise();

        public override void Write(ReadOnlySpan<byte> buffer) => throw seam.Raise();

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromException(seam.Raise());

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException(seam.Raise());
    }

    /// <summary>
    /// The failure the seam injects. It is neither an <see cref="IOException"/> nor an
    /// <see cref="OperationCanceledException"/>, so the exception handler never takes it for a client abort.
    /// </summary>
    private sealed class InjectedResponseFailureException()
        : Exception("Response failure injected by RecordWriteOutcomeTests.");

    /// <summary>
    /// A logger provider whose loggers for the <see cref="RecordService"/> category are enabled at every level and
    /// throw from every log call, counting each throw; every other category gets a logger that writes nothing.
    /// </summary>
    private sealed class ThrowingAuditLoggerProvider : ILoggerProvider
    {
        private static readonly string AuditCategory = typeof(RecordService).FullName
            ?? throw new InvalidOperationException("RecordService has no full type name.");

        private int throws;
        private int disposed;

        /// <summary>How many log calls have thrown.</summary>
        public int Throws => Volatile.Read(ref throws);

        /// <inheritdoc />
        /// <exception cref="ObjectDisposedException">The provider has been disposed.</exception>
        public ILogger CreateLogger(string categoryName)
        {
            ArgumentNullException.ThrowIfNull(categoryName);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) == 1, this);
            return string.Equals(categoryName, AuditCategory, StringComparison.Ordinal)
                ? new ThrowingLogger(this)
                : NullLogger.Instance;
        }

        /// <summary>Marks the provider disposed; <see cref="Throws"/> stays readable.</summary>
        public void Dispose() => Volatile.Write(ref disposed, 1);

        private sealed class ThrowingLogger(ThrowingAuditLoggerProvider provider) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                Interlocked.Increment(ref provider.throws);
                throw new InvalidOperationException("Audit log failure injected by RecordWriteOutcomeTests.");
            }
        }
    }
}
