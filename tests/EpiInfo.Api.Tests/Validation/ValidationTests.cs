using System.Net;
using System.Text;
using System.Text.Json;
using EpiInfo.Api.Endpoints;
using EpiInfo.Api.Errors;
using EpiInfo.Api.Json;
using EpiInfo.Api.Services;
using EpiInfo.Api.Tests.Infrastructure;
using EpiInfo.CheckCode;
using EpiInfo.Domain.Storage;
using EpiInfo.Domain.Validation;
using EpiInfo.Domain.Values;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EpiInfo.Api.Tests.Validation;

/// <summary>
/// The validation 400s of the record writes, <c>POST /api/forms/{viewId}/records</c> and
/// <c>PUT /api/forms/{viewId}/records/{globalRecordId}</c>, for every way the <c>values</c> reader can reject an
/// entry: a nested object or array, a number that is not finite, a name the form has no row for, a label or system
/// row, a field named twice, and a name or string the JSON text cannot decode.
/// </summary>
/// <remarks>
/// <para>
/// Each request is sent as raw JSON text, so repeats, case variants, <c>1e400</c> and escaped lone surrogates reach
/// the API exactly as written. Every rejection must be a 400 <c>application/problem+json</c> body that keys each
/// error by the canonical <c>metaFields.Name</c> or by <see cref="RecordValidator.ValuesKey"/>, never by a name as
/// submitted, carries no submitted text, and leaves the store unwritten.
/// </para>
/// <para>
/// The cases run on <see cref="FakeForms.TwoPage"/> in a <see cref="FakeDataStore"/>, so they need no database and
/// carry no <c>Integration</c> trait.
/// </para>
/// </remarks>
public sealed class ValidationTests
{
    private const string Sentinel = "SENTINEL-PII-4711";

    private const string Post = "POST";

    private const string Put = "PUT";

    private const int ViewId = FakeForms.TwoPageViewId;

    /// <summary>
    /// Field-level rejections as (method, <c>values</c> JSON, the one expected <c>errors</c> key, its one message),
    /// each sent as a <c>POST</c> and as a <c>PUT</c> of a seeded record.
    /// </summary>
    public static TheoryData<string, string, string, string> FieldLevelCases
    {
        get
        {
            const string ValuesKey = RecordValidator.ValuesKey;
            const string Unknown = ValidationMessages.UnknownValuesName;
            const string NotData = ValidationMessages.UnknownField;
            const string Duplicate = ValidationMessages.DuplicateField;
            const string Shape = ValuesReader.ScalarValueExpected;
            const string NotFinite = ValidationMessages.NumberExpected;

            var data = new TheoryData<string, string, string, string>();
            foreach (var method in new[] { Post, Put })
            {
                // An object or array under a case variant of a data field: the canonical name, the shape message.
                data.Add(method, $$$"""{"lastname":{"first":"{{{Sentinel}}}"}}""", "LastName", Shape);
                data.Add(method, $$"""{"LASTNAME":["{{Sentinel}}"]}""", "LastName", Shape);

                // A number JSON parsing accepts but no double holds, under a case variant of a number field.
                data.Add(method, """{"age":1e400}""", "Age", NotFinite);
                data.Add(method, """{"AGE":-1e400}""", "Age", NotFinite);

                // A name the form has no metaFields row for, whatever its value: only the fixed values key.
                data.Add(method, $$$"""{"{{{Sentinel}}}":{"nested":"{{{Sentinel}}}"}}""", ValuesKey, Unknown);
                data.Add(method, $$"""{"{{Sentinel}}":["{{Sentinel}}"]}""", ValuesKey, Unknown);
                data.Add(method, $$"""{"{{Sentinel}}":1e400}""", ValuesKey, Unknown);
                data.Add(method, $$"""{"{{Sentinel}}":"{{Sentinel}}"}""", ValuesKey, Unknown);

                // A label or system row holds no value: its canonical name, whatever the value's shape.
                data.Add(method, $$"""{"title1":"{{Sentinel}}"}""", "Title1", NotData);
                data.Add(method, $$$"""{"TITLE1":{"nested":"{{{Sentinel}}}"}}""", "Title1", NotData);
                data.Add(method, """{"recstatus":1}""", "RecStatus", NotData);
                data.Add(method, $$"""{"globalRecordID":"{{Sentinel}}"}""", "GlobalRecordId", NotData);

                // A field named twice, exactly or differing only by case: one error under the canonical name.
                data.Add(method, $$"""{"LastName":"{{Sentinel}}","LastName":"{{Sentinel}}"}""", "LastName", Duplicate);
                data.Add(method, $$"""{"LastName":"{{Sentinel}}","lastNAME":"{{Sentinel}}"}""", "LastName", Duplicate);
            }

            return data;
        }
    }

    /// <summary>
    /// Bodies where a raw fault sits beside other entries, as (method, <c>values</c> JSON, the expected <c>errors</c>
    /// keys, one message each in the same order), each sent as a <c>POST</c> and as a <c>PUT</c> of a seeded record.
    /// A key listed twice expects both messages under it, in that order.
    /// </summary>
    public static TheoryData<string, string, string[], string[]> FaultBesideOtherEntriesCases
    {
        get
        {
            const string ValuesKey = RecordValidator.ValuesKey;
            const string Unknown = ValidationMessages.UnknownValuesName;
            const string NotData = ValidationMessages.UnknownField;
            const string Duplicate = ValidationMessages.DuplicateField;
            const string Shape = ValuesReader.ScalarValueExpected;
            const string NotFinite = ValidationMessages.NumberExpected;

            var data = new TheoryData<string, string, string[], string[]>();
            foreach (var method in new[] { Post, Put })
            {
                // A data field named again, whichever occurrence is faulted: the repeat is a duplicate under the
                // canonical name, and a faulted first occurrence keeps its own fault ahead of it.
                data.Add(method, $$"""{"Age":1,"AGE":["{{Sentinel}}"]}""", ["Age"], [Duplicate]);
                data.Add(method, $$"""{"AGE":["{{Sentinel}}"],"Age":1}""", ["Age", "Age"], [Shape, Duplicate]);
                data.Add(method, """{"age":1e400,"AGE":30}""", ["Age", "Age"], [NotFinite, Duplicate]);

                // A name the form has no row for beside a faulted data field: each under its own key.
                data.Add(
                    method,
                    $$$"""{"lastname":{"first":"{{{Sentinel}}}"},"{{{Sentinel}}}":"{{{Sentinel}}}"}""",
                    ["LastName", ValuesKey],
                    [Shape, Unknown]);

                // A scalar system row beside a faulted label: neither holds a value, so both read the same message.
                data.Add(
                    method,
                    $$$"""{"title1":{"nested":"{{{Sentinel}}}"},"recstatus":1}""",
                    ["Title1", "RecStatus"],
                    [NotData, NotData]);

                // A data field read without fault and named once adds nothing beside the fault.
                data.Add(method, $$"""{"LastName":"{{Sentinel}}","age":[]}""", ["Age"], [Shape]);
            }

            return data;
        }
    }

    /// <summary>
    /// <c>values</c> objects holding a property name or a string value whose escaped lone surrogate decodes to no
    /// text, as (method, <c>values</c> JSON), each sent as a <c>POST</c> and as a <c>PUT</c> of a seeded record.
    /// </summary>
    public static TheoryData<string, string> UndecodableCases
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var method in new[] { Post, Put })
            {
                data.Add(method, $$"""{"\uD800":"{{Sentinel}}"}""");
                data.Add(method, $$"""{"{{Sentinel}}\uDC00":1}""");
                data.Add(method, $$"""{"LastName":"{{Sentinel}}\uD800"}""");
                data.Add(method, $$"""{"{{Sentinel}}":"\uDFFF"}""");
            }

            return data;
        }
    }

    /// <summary>
    /// A rejected entry yields exactly one error, under the canonical field name or the fixed values key, with the
    /// reader's or validator's fixed message, and nothing reaches the store's write methods.
    /// </summary>
    [Theory]
    [MemberData(nameof(FieldLevelCases))]
    public async Task RejectedEntryIsKeyedByCanonicalNameOrValuesKey(
        string method,
        string valuesJson,
        string expectedKey,
        string expectedMessage)
    {
        await using var host = await EndpointHost.StartAsync(new FakeDataStore(FakeForms.TwoPage));

        var problem = await SendRejectedWriteAsync(host, method, valuesJson);

        Assert.Equal(ApiProblems.ValidationFailedDetail, problem.GetProperty("detail").GetString());
        AssertErrors(problem, (expectedKey, expectedMessage));
    }

    /// <summary>
    /// Every raw fault of one body is reported under its own key, so an unknown name next to faulted data fields
    /// neither hides them nor is echoed, and a case variant of a faulted field is also reported as a duplicate.
    /// </summary>
    [Theory]
    [InlineData(Post)]
    [InlineData(Put)]
    public async Task EveryRawFaultOfOneBodyIsReportedUnderItsOwnKey(string method)
    {
        await using var host = await EndpointHost.StartAsync(new FakeDataStore(FakeForms.TwoPage));

        var problem = await SendRejectedWriteAsync(
            host,
            method,
            $$"""{"lastname":{},"{{Sentinel}}":["{{Sentinel}}"],"age":1e400,"title1":{},"AGE":1e400}""");

        AssertErrors(
            problem,
            ("LastName", ValuesReader.ScalarValueExpected),
            (RecordValidator.ValuesKey, ValidationMessages.UnknownValuesName),
            ("Age", ValidationMessages.NumberExpected),
            ("Title1", ValidationMessages.UnknownField),
            ("Age", ValidationMessages.DuplicateField));
    }

    /// <summary>
    /// A raw fault does not hide the entries beside it: every property is resolved in document order by the
    /// precedence the validator applies, so a name that is not a data field reads its own error, a data field named
    /// again is a duplicate under its canonical name, a faulted first occurrence keeps its fault, and nothing is
    /// written.
    /// </summary>
    [Theory]
    [MemberData(nameof(FaultBesideOtherEntriesCases))]
    public async Task FaultBesideOtherEntriesIsResolvedPerPropertyInDocumentOrder(
        string method,
        string valuesJson,
        string[] expectedKeys,
        string[] expectedMessages)
    {
        Assert.Equal(expectedKeys.Length, expectedMessages.Length);
        await using var host = await EndpointHost.StartAsync(new FakeDataStore(FakeForms.TwoPage));

        var problem = await SendRejectedWriteAsync(host, method, valuesJson);

        Assert.Equal(ApiProblems.ValidationFailedDetail, problem.GetProperty("detail").GetString());
        AssertErrors(problem, [.. expectedKeys.Zip(expectedMessages)]);
    }

    /// <summary>
    /// A name or string that cannot be decoded is the request-level "malformed JSON" 400 with no <c>errors</c>
    /// member, not a field error and not a 500.
    /// </summary>
    [Theory]
    [MemberData(nameof(UndecodableCases))]
    public async Task UndecodableNameOrStringIsRequestLevelMalformedJson(string method, string valuesJson)
    {
        await using var host = await EndpointHost.StartAsync(new FakeDataStore(FakeForms.TwoPage));

        var problem = await SendRejectedWriteAsync(host, method, valuesJson);

        Assert.Equal(ApiProblems.MalformedJsonDetail, problem.GetProperty("detail").GetString());
        Assert.False(
            problem.TryGetProperty("errors", out _),
            $"A request-level 400 has no errors member: {problem.GetRawText()}");
    }

    // Sends {"values": <valuesJson>} as raw JSON with the given method: a POST to the form, or a PUT of a record seeded
    // for this request. Asserts the shared rejection contract and returns the ProblemDetails body.
    private static async Task<JsonElement> SendRejectedWriteAsync(EndpointHost host, string method, string valuesJson)
    {
        Assert.True(method is Post or Put, $"Unexpected method {method}.");

        var path = $"/api/forms/{ViewId}/records";
        string? seededId = null;
        FakeRecordState? before = null;
        if (method == Put)
        {
            seededId = host.Store.SeedRecord(
                ViewId,
                new Dictionary<string, FieldValue>
                {
                    ["LastName"] = FieldValue.FromText("Seeded"),
                    ["Age"] = FieldValue.FromNumber(30),
                });
            before = host.Store.GetRecord(ViewId, seededId);
            path += "/" + seededId;
        }

        var json = $$"""{"values":{{valuesJson}}}""";
        using var request = new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = new StringContent(json, Encoding.UTF8, ApiTestClient.JsonMediaType),
        };
        using var response = await host.Client.SendAsync(request, host.Timeout);
        var text = await response.Content.ReadAsStringAsync(host.Timeout);

        Assert.True(
            response.StatusCode == HttpStatusCode.BadRequest,
            $"{method} {json} returned {(int)response.StatusCode}: {text}");
        Assert.Equal(ApiProblems.ProblemContentType, response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain(Sentinel, text, StringComparison.Ordinal);

        // Rejected before any write call and without check code: nothing written, nothing evaluated, and a seeded
        // record keeps every value and stamp.
        Assert.Equal(0, host.Engine.RunCalls);
        Assert.Empty(host.Store.WriteCalls);
        Assert.DoesNotContain(host.Store.Calls, call => call.Method is nameof(IDataStore.InsertAsync)
            or nameof(IDataStore.UpdateAsync)
            or nameof(IDataStore.SoftDeleteAsync));
        if (seededId is not null)
        {
            var after = host.Store.GetRecord(ViewId, seededId);
            Assert.NotNull(before);
            Assert.NotNull(after);
            Assert.Equal(before.RecStatus, after.RecStatus);
            Assert.Equal(before.Values, after.Values);
            Assert.Equal(before.FirstSave, after.FirstSave);
            Assert.Equal(before.LastSave, after.LastSave);
        }

        using var document = JsonDocument.Parse(text);
        return ApiTestClient.AssertProblemDetails(document.RootElement.Clone(), StatusCodes.Status400BadRequest);
    }

    // Asserts the errors map holds exactly the expected keys, compared ordinally, each with exactly the messages of its
    // expected entries in their order: a key listed once expects one message, a key listed twice expects two.
    private static void AssertErrors(JsonElement problem, params (string Key, string Message)[] expected)
    {
        Assert.True(
            problem.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object,
            $"A field-level 400 carries an errors object: {problem.GetRawText()}");

        var expectedByKey = expected.GroupBy(entry => entry.Key, StringComparer.Ordinal).ToArray();
        Assert.Equal(
            expectedByKey.Select(group => group.Key).Order(StringComparer.Ordinal),
            errors.EnumerateObject().Select(entry => entry.Name).Order(StringComparer.Ordinal));

        foreach (var group in expectedByKey)
        {
            Assert.Equal(
                group.Select(entry => (string?)entry.Message),
                errors.GetProperty(group.Key).EnumerateArray().Select(item => item.GetString()));
        }
    }

    /// <summary>
    /// An in-process TestServer host over the real record endpoints and the services behind them, with the API's JSON
    /// settings, <see cref="FakeDataStore"/> as the store and the real check-code engine behind a
    /// <see cref="CountingCheckCodeEngine"/>. The test server replaces Kestrel, so nothing binds a port, and logging
    /// has no provider.
    /// </summary>
    private sealed class EndpointHost : IAsyncDisposable
    {
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

        private readonly WebApplication _app;

        private readonly CancellationTokenSource _timeout = new(RequestTimeout);

        private EndpointHost(WebApplication app, FakeDataStore store, CountingCheckCodeEngine engine)
        {
            _app = app;
            Store = store;
            Engine = engine;
            Client = app.GetTestClient();
        }

        /// <summary>The store the host reads and writes.</summary>
        public FakeDataStore Store { get; }

        /// <summary>The check-code engine the host uses, counting its calls.</summary>
        public CountingCheckCodeEngine Engine { get; }

        /// <summary>A client whose requests go straight to the test server.</summary>
        public HttpClient Client { get; }

        /// <summary>Cancels a request or read that has not finished within the host's time limit.</summary>
        public CancellationToken Timeout => _timeout.Token;

        /// <summary>Builds and starts a host over <paramref name="store"/>.</summary>
        /// <param name="store">The in-memory store the endpoints use.</param>
        /// <returns>The started host; the caller disposes it.</returns>
        public static async Task<EndpointHost> StartAsync(FakeDataStore store)
        {
            var builder = WebApplication.CreateSlimBuilder(
                new WebApplicationOptions { EnvironmentName = Environments.Production });
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();

            var engine = new CountingCheckCodeEngine(new CheckCodeEngine());
            builder.Services.AddApiJson();
            builder.Services.AddSingleton<IDataStore>(store);
            builder.Services.AddSingleton<ICheckCodeEngine>(engine);
            builder.Services.AddSingleton<TimeProvider>(new ControlledTimeProvider());
            builder.Services.AddSingleton<SaveStampProvider>();
            builder.Services.AddScoped<FormService>();
            builder.Services.AddScoped<RecordService>();

            var app = builder.Build();
            app.MapRecordEndpoints();

            using var startTimeout = new CancellationTokenSource(RequestTimeout);
            await app.StartAsync(startTimeout.Token);
            return new EndpointHost(app, store, engine);
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
            _timeout.Dispose();
        }
    }
}
