using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EpiInfo.Api.Tests.Errors;

/// <summary>
/// The error contract of AAP 0.3.4.2: every error response under <c>/api</c> is a ProblemDetails with exactly
/// <c>type</c>, <c>title</c>, <c>status</c>, <c>detail</c> and <c>traceId</c>, and a 500's <c>detail</c> is the fixed
/// generic sentence plus that <c>traceId</c>; and the redaction policy of AAP 0.9.3, proved through the API's error
/// pipeline with the committed <c>appsettings*.json</c> logging configuration.
/// </summary>
/// <remarks>
/// <para>
/// Empty error responses produced inside the pipeline, the route miss 404 and the 405, are requested with no
/// <c>Accept</c> header and with <c>Accept</c> values that admit no JSON type, which the framework's default
/// ProblemDetails writer declines. Every HTTP case runs in the Production and the Development environment.
/// </para>
/// <para>
/// No case needs a database. The contract cases start their own <see cref="ApiFactory"/> with no connection string,
/// and the 500 and 503 cases execute the <see cref="ApiProblems"/> writers on a <see cref="DefaultHttpContext"/>; their
/// assertion messages are fixed texts that never quote a response body. The body-classification, response
/// serialization and exception-boundary cases start an <see cref="ErrorPipelineHost"/>, which composes the real
/// exception handler, guards, endpoints and services in the pipeline order <c>Program.cs</c> uses, over a
/// <see cref="FakeDataStore"/>. The class carries no <c>Integration</c> trait, joins no database collection and takes
/// no <c>DatabaseWriteLock</c>.
/// </para>
/// </remarks>
public sealed class ErrorContractTests
{
    private const string ProblemMediaType = "application/problem+json";

    private const string NotFoundType = "https://tools.ietf.org/html/rfc9110#section-15.5.5";

    private const string MethodNotAllowedType = "https://tools.ietf.org/html/rfc9110#section-15.5.6";

    private const string CustomizePath = "Customize";

    private const string StatusCodePagePath = "StatusCodePage";

    private const string UnavailableSource = "Unavailable";

    private const string UnavailableWriteSource = "UnavailableWrite";

    // The 500 detail written out here rather than read from ApiProblems, so a change to the product's text fails
    // these cases: one fixed sentence, then the label that introduces the body's traceId.
    private const string ExpectedUnexpectedSentence = "An unexpected error occurred.";

    private const string ExpectedTraceIdLabel = " Trace id: ";

    // The members of every plain ProblemDetails the API writes, in ordinal order.
    private static readonly string[] ProblemMembers = ["detail", "status", "title", "traceId", "type"];

    // Accept values that admit neither application/json nor application/problem+json, so the framework's default
    // ProblemDetails writer declines them; the last is a browser navigation without a */* entry.
    private static readonly string[] NonJsonAcceptHeaders =
    [
        "text/html",
        "text/plain",
        "application/xml",
        "text/html,application/xhtml+xml;q=0.9",
    ];

    private static string NewSentinel() => "SENTINEL-" + Guid.NewGuid().ToString("N");

    // The four body routes, on the standard fake forms.
    private const string CreateRoute = "create";

    private const string UpdateRoute = "update";

    private const string SearchRoute = "search";

    private const string FieldEventRoute = "field-event";

    // How a body travels: TestServer's client with a declared or an unknown length (sent chunked), or exact HTTP/1.1
    // bytes on Kestrel.
    private const string TestServerContentLength = "TestServer, Content-Length";

    private const string TestServerUnknownLength = "TestServer, unknown length";

    private const string KestrelContentLength = "Kestrel, Content-Length";

    private const string KestrelChunked = "Kestrel, chunked";

    private const string KestrelUnframed = "Kestrel, neither Content-Length nor Transfer-Encoding";

    private static readonly string RecordsPath =
        string.Create(CultureInfo.InvariantCulture, $"/api/forms/{FakeForms.EmptyViewId}/records");

    private static readonly string FieldEventPath =
        string.Create(CultureInfo.InvariantCulture, $"/api/forms/{FakeForms.CheckCodeViewId}/checkcode/field-event");

    private static readonly string HandlerCategory = typeof(ApiExceptionHandler).FullName!;

    // The test-only endpoint of the exception-boundary cases, and what its failures carry.
    private const string BoundaryTestPath = "/test/boundary";

    private const string PartialBody = "partial";

    private const string SecretSqlText = "SELECT * FROM [dbo].[Secret]";

    // The event names of the handler category's lines.
    private const string RequestFailedEvent = "RequestFailed";

    private const string RequestAbortedEvent = "RequestAborted";

    private const string ResponseAbortedEvent = "ResponseAborted";

    // The longest a hosted case waits for its endpoint to start or for the server to finish its request.
    private static readonly TimeSpan HostedWait = TimeSpan.FromSeconds(10);

    /// <summary>The hosting environments every HTTP case runs in.</summary>
    public static TheoryData<string> Environments => new() { "Production", "Development" };

    /// <summary>
    /// Each environment with no <c>Accept</c> header (<see langword="null"/>) and with each non-JSON value.
    /// </summary>
    public static TheoryData<string, string?> EnvironmentsAndAcceptHeaders
    {
        get
        {
            var data = new TheoryData<string, string?>();
            foreach (string environment in EnvironmentNames)
            {
                data.Add(environment, null);
                foreach (string accept in NonJsonAcceptHeaders)
                {
                    data.Add(environment, accept);
                }
            }

            return data;
        }
    }

    /// <summary>Each environment with each non-JSON <c>Accept</c> value.</summary>
    public static TheoryData<string, string> EnvironmentsAndNonJsonAcceptHeaders
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (string environment in EnvironmentNames)
            {
                foreach (string accept in NonJsonAcceptHeaders)
                {
                    data.Add(environment, accept);
                }
            }

            return data;
        }
    }

    /// <summary>
    /// A method the client fallback does not allow on each of its two routes, the root and a client route, in each
    /// environment, with no <c>Accept</c> header (<see langword="null"/>) and with each non-JSON value.
    /// </summary>
    public static TheoryData<string, string, string, string?> WrongMethodCases
    {
        get
        {
            (string Method, string Path)[] requests = [("POST", "/client/route"), ("PUT", "/")];
            var data = new TheoryData<string, string, string, string?>();
            foreach (string environment in EnvironmentNames)
            {
                foreach ((string method, string path) in requests)
                {
                    data.Add(environment, method, path, null);
                    foreach (string accept in NonJsonAcceptHeaders)
                    {
                        data.Add(environment, method, path, accept);
                    }
                }
            }

            return data;
        }
    }

    /// <summary>
    /// The generic 500 results, <see cref="ApiProblems.Unexpected"/> (<see langword="null"/> outcome) and
    /// <see cref="ApiProblems.UnexpectedWrite"/> for each outcome, written with and without a current activity.
    /// </summary>
    public static TheoryData<WriteOutcome?, bool> UnexpectedResults
    {
        get
        {
            var data = new TheoryData<WriteOutcome?, bool> { { null, false }, { null, true } };
            foreach (WriteOutcome outcome in Enum.GetValues<WriteOutcome>())
            {
                data.Add(outcome, false);
                data.Add(outcome, true);
            }

            return data;
        }
    }

    /// <summary>
    /// The two bodies built from a status alone, a framework body shaped by <see cref="ApiProblems.Customize"/> and a
    /// status-code page, for 500 and another generic 5xx, with and without a current activity.
    /// </summary>
    public static TheoryData<string, int, bool> StatusOnlyServerErrors
    {
        get
        {
            int[] statuses = [StatusCodes.Status500InternalServerError, StatusCodes.Status502BadGateway];
            var data = new TheoryData<string, int, bool>();
            foreach (string path in new[] { CustomizePath, StatusCodePagePath })
            {
                foreach (int status in statuses)
                {
                    data.Add(path, status, false);
                    data.Add(path, status, true);
                }
            }

            return data;
        }
    }

    /// <summary>Every writer of a 503 body.</summary>
    public static TheoryData<string> UnavailableSources =>
        new() { UnavailableSource, UnavailableWriteSource, CustomizePath, StatusCodePagePath };

    /// <summary>
    /// Client-error statuses a status-code page can answer, including the routing 415 no mapped route produces yet,
    /// with the fixed detail of each; any other 4xx is the generic invalid-request text.
    /// </summary>
    public static TheoryData<int, string> StatusCodePageClientErrors => new()
    {
        { StatusCodes.Status400BadRequest, ApiProblems.InvalidRequestDetail },
        { StatusCodes.Status404NotFound, ApiProblems.ResourceNotFoundDetail },
        { StatusCodes.Status405MethodNotAllowed, ApiProblems.MethodNotAllowedDetail },
        { StatusCodes.Status413PayloadTooLarge, ApiProblems.RequestTooLargeDetail },
        { StatusCodes.Status415UnsupportedMediaType, ApiProblems.UnsupportedMediaTypeDetail },
        { StatusCodes.Status418ImATeapot, ApiProblems.InvalidRequestDetail },
    };

    /// <summary>A status the page never answers, and an error status of a request the client aborted.</summary>
    public static TheoryData<int, bool> StatusCodePagesThatWriteNothing => new()
    {
        { StatusCodes.Status200OK, false },
        { StatusCodes.Status404NotFound, true },
    };

    /// <summary>
    /// The two record writes whose success response is serialised after the commit, on TestServer and on Kestrel, in
    /// each environment.
    /// </summary>
    public static TheoryData<string, string, bool> CommittedWrites
    {
        get
        {
            var data = new TheoryData<string, string, bool>();
            foreach (var environment in EnvironmentNames)
            {
                foreach (var route in new[] { CreateRoute, UpdateRoute })
                {
                    data.Add(environment, route, false);
                    data.Add(environment, route, true);
                }
            }

            return data;
        }
    }

    /// <summary>
    /// Bodies the four body routes cannot bind, each with the request-level <c>detail</c> it must get, on every
    /// framing, in each environment.
    /// </summary>
    public static TheoryData<string, string, string, string, string> UnusableBodies
    {
        get
        {
            var data = new TheoryData<string, string, string, string, string>();
            foreach (var environment in EnvironmentNames)
            {
                foreach (var route in new[] { CreateRoute, UpdateRoute, SearchRoute, FieldEventRoute })
                {
                    foreach (var framing in new[]
                             {
                                 TestServerContentLength, TestServerUnknownLength, KestrelContentLength, KestrelChunked,
                             })
                    {
                        // Content that is not JSON: an unterminated object and a bare word.
                        data.Add(environment, framing, route, "{", ApiProblems.MalformedJsonDetail);
                        data.Add(environment, framing, route, "x", ApiProblems.MalformedJsonDetail);

                        // No content: Content-Length 0, or a body of unknown length whose first read ends it (on
                        // Kestrel the zero-length chunk alone).
                        data.Add(environment, framing, route, string.Empty, ApiProblems.RequestBodyRequiredDetail);

                        // One byte that holds no JSON token is content, not an empty body.
                        data.Add(environment, framing, route, " ", ApiProblems.MalformedJsonDetail);
                    }

                    // A missing body: an HTTP/1.1 request without framing headers cannot carry one.
                    data.Add(environment, KestrelUnframed, route, string.Empty, ApiProblems.RequestBodyRequiredDetail);
                }
            }

            return data;
        }
    }

    // TheoryData implements more than one IEnumerable<T>, so LINQ is pointed at its object[] rows.
    private static IEnumerable<string> EnvironmentNames =>
        ((IEnumerable<object[]>)Environments).Select(row => (string)row[0]);

    // AAP 0.3.4.2: a route miss is a 404 ProblemDetails whatever the Accept header; the framework's default writer
    // declines a non-JSON Accept and would leave text/plain without traceId. The client is deliberately absent
    // (AbsentClientFolder).
    [Theory]
    [MemberData(nameof(EnvironmentsAndAcceptHeaders))]
    public async Task RouteMissIsProblemDetailsWhateverTheAcceptHeader(string environment, string? accept)
    {
        await using ApiFactory factory = CreateFactory(environment, AbsentClientFolder());
        using HttpClient client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/nope");
        AddAccept(request, accept);

        using HttpResponseMessage response = await client.SendAsync(request);

        await AssertFixedProblemAsync(
            response,
            HttpStatusCode.NotFound,
            NotFoundType,
            "Not Found",
            ApiProblems.ResourceNotFoundDetail);
    }

    // AAP 0.7.6: a text/plain POST to an unknown /api route is the route miss, never a 415, and with a non-JSON Accept
    // it is still the 404 ProblemDetails. The client is deliberately absent (AbsentClientFolder).
    [Theory]
    [MemberData(nameof(EnvironmentsAndNonJsonAcceptHeaders))]
    public async Task TextPlainPostToUnknownApiRouteIsNotFound(string environment, string accept)
    {
        await using ApiFactory factory = CreateFactory(environment, AbsentClientFolder());
        using HttpClient client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/nope")
        {
            Content = new StringContent("plain text, not JSON", Encoding.UTF8, "text/plain"),
        };
        AddAccept(request, accept);

        using HttpResponseMessage response = await client.SendAsync(request);

        await AssertFixedProblemAsync(
            response,
            HttpStatusCode.NotFound,
            NotFoundType,
            "Not Found",
            ApiProblems.ResourceNotFoundDetail);
    }

    // AAP 0.3.4.2 and 0.3.4.7: a method mismatch stays a 405, becomes the 405 ProblemDetails whatever the Accept
    // header, and keeps the Allow header routing set. The client fallback (GET and HEAD) is the route used, mapped from
    // a temporary client folder.
    [Theory]
    [MemberData(nameof(WrongMethodCases))]
    public async Task WrongMethodIsProblemDetailsAndKeepsAllow(
        string environment,
        string method,
        string path,
        string? accept)
    {
        DirectoryInfo clientFolder = Directory.CreateTempSubdirectory("epiinfo-error-contract-");
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(clientFolder.FullName, ClientAppHosting.IndexFileName),
                "<!doctype html><title>client</title>");

            await using ApiFactory factory = CreateFactory(environment, clientFolder.FullName);
            using HttpClient client = factory.CreateClient();
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            AddAccept(request, accept);

            using HttpResponseMessage response = await client.SendAsync(request);

            await AssertFixedProblemAsync(
                response,
                HttpStatusCode.MethodNotAllowed,
                MethodNotAllowedType,
                "Method Not Allowed",
                ApiProblems.MethodNotAllowedDetail);

            ICollection<string> allow = response.Content.Headers.Allow;
            Assert.True(
                allow.Contains(HttpMethods.Get) && allow.Contains(HttpMethods.Head),
                "The 405 lost the Allow header listing GET and HEAD.");
        }
        finally
        {
            clientFolder.Delete(recursive: true);
        }
    }

    // AAP 0.3.4.2: the 500 detail is the fixed generic sentence plus the traceId the same body carries, which is the
    // current activity's id when there is one and the request's trace identifier otherwise.
    [Theory]
    [MemberData(nameof(UnexpectedResults))]
    public async Task UnexpectedResultsQuoteTheirTraceIdInDetail(WriteOutcome? outcome, bool withActivity)
    {
        using Activity? activity = StartActivity(withActivity);
        IResult result = outcome is { } written ? ApiProblems.UnexpectedWrite(written) : ApiProblems.Unexpected();
        (DefaultHttpContext context, MemoryStream body) = NewContext();

        await result.ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.Equal(ProblemMediaType, context.Response.ContentType);
        JsonElement problem = ApiTestClient.AssertProblemDetails(Parse(body), StatusCodes.Status500InternalServerError);
        string? traceId = StringMember(problem, ApiProblems.TraceIdKey);
        AssertTraceId(traceId, activity, context);
        AssertUnexpectedDetail(StringMember(problem, "detail"), traceId);

        if (outcome is { } expected)
        {
            Assert.True(
                string.Equals(expected.ToWireValue(), StringMember(problem, "writeOutcome"), StringComparison.Ordinal),
                "The write 500 does not carry its writeOutcome.");
        }
        else
        {
            Assert.False(problem.TryGetProperty("writeOutcome", out _), "A non-write 500 carries writeOutcome.");
        }
    }

    // A result written again rebuilds its detail from the constant and the trace id of the request it is written to,
    // never keeping the first request's trace id.
    [Fact]
    public async Task UnexpectedResultWrittenTwiceQuotesEachRequestsTraceId()
    {
        Activity.Current = null;
        IResult result = ApiProblems.Unexpected();
        string[] traceIds = ["first-request", "second-request"];

        foreach (string traceId in traceIds)
        {
            (DefaultHttpContext context, MemoryStream body) = NewContext();
            context.TraceIdentifier = traceId;

            await result.ExecuteAsync(context);

            JsonElement problem = Parse(body);
            Assert.True(
                string.Equals(traceId, StringMember(problem, ApiProblems.TraceIdKey), StringComparison.Ordinal),
                "The traceId member is not the trace identifier of the request written to.");
            AssertUnexpectedDetail(StringMember(problem, "detail"), traceId);
        }
    }

    // AAP 0.3.4.2: the framework and status-code page paths give a generic 5xx the same correlated detail as the
    // factories.
    [Theory]
    [MemberData(nameof(StatusOnlyServerErrors))]
    public async Task StatusOnlyServerErrorsQuoteTheirTraceIdInDetail(string path, int status, bool withActivity)
    {
        using Activity? activity = StartActivity(withActivity);
        (DefaultHttpContext context, MemoryStream body) = NewContext();

        (string? detail, string? traceId) = await ProduceStatusOnlyProblemAsync(path, status, context, body);

        AssertTraceId(traceId, activity, context);
        AssertUnexpectedDetail(detail, traceId);
    }

    // AAP 0.3.4.2: a 503 keeps its fixed unavailable text; only the generic 500 detail carries the trace id.
    [Theory]
    [MemberData(nameof(UnavailableSources))]
    public async Task UnavailableBodiesKeepTheFixedDetail(string source)
    {
        Activity.Current = null;
        (DefaultHttpContext context, MemoryStream body) = NewContext();

        string? detail;
        string? traceId;
        switch (source)
        {
            case UnavailableSource:
            case UnavailableWriteSource:
                IResult result = source == UnavailableSource
                    ? ApiProblems.Unavailable()
                    : ApiProblems.UnavailableWrite(WriteOutcome.NotWritten);
                await result.ExecuteAsync(context);
                Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
                JsonElement problem = ApiTestClient.AssertProblemDetails(
                    Parse(body),
                    StatusCodes.Status503ServiceUnavailable);
                detail = StringMember(problem, "detail");
                traceId = StringMember(problem, ApiProblems.TraceIdKey);
                break;

            default:
                (detail, traceId) = await ProduceStatusOnlyProblemAsync(
                    source,
                    StatusCodes.Status503ServiceUnavailable,
                    context,
                    body);
                break;
        }

        AssertTraceId(traceId, activity: null, context);
        Assert.True(
            string.Equals(ApiProblems.UnavailableDetail, detail, StringComparison.Ordinal),
            "The 503 detail is not the fixed unavailable text.");
    }

    // AAP 0.3.4.2: a status-code page is the fixed ProblemDetails of its status, as application/problem+json, and
    // leaves the headers routing set, such as a 405's Allow, as they were.
    [Theory]
    [MemberData(nameof(StatusCodePageClientErrors))]
    public async Task StatusCodePagesWriteTheFixedTextOfTheirStatus(int status, string detail)
    {
        (DefaultHttpContext context, MemoryStream body) = NewContext();
        context.Response.StatusCode = status;
        context.Response.Headers.Allow = "GET, HEAD";

        await ApiProblems.WriteStatusCodePageAsync(
            new StatusCodeContext(context, new StatusCodePagesOptions(), _ => Task.CompletedTask));

        Assert.Equal(status, context.Response.StatusCode);
        Assert.Equal(ProblemMediaType, context.Response.ContentType);
        Assert.True(
            string.Equals("GET, HEAD", context.Response.Headers.Allow, StringComparison.Ordinal),
            "The status-code page changed a header it does not own.");
        JsonElement problem = ApiTestClient.AssertProblemDetails(Parse(body), status);
        Assert.True(
            ProblemMembers.SequenceEqual(
                problem.EnumerateObject().Select(member => member.Name).Order(StringComparer.Ordinal),
                StringComparer.Ordinal),
            "The status-code page does not hold exactly type, title, status, detail and traceId.");
        Assert.True(
            string.Equals(detail, StringMember(problem, "detail"), StringComparison.Ordinal),
            "The status-code page's detail is not the fixed text of its status.");
        AssertTraceId(StringMember(problem, ApiProblems.TraceIdKey), Activity.Current, context);
    }

    // A status outside 400 to 599 gets no page, and a client that aborted the request receives no body.
    [Theory]
    [MemberData(nameof(StatusCodePagesThatWriteNothing))]
    public async Task StatusCodePageWritesNothingOutsideAnErrorOrAfterAbort(int status, bool aborted)
    {
        (DefaultHttpContext context, MemoryStream body) = NewContext();
        using var abort = new CancellationTokenSource();
        if (aborted)
        {
            await abort.CancelAsync();
        }

        context.RequestAborted = abort.Token;
        context.Response.StatusCode = status;

        await ApiProblems.WriteStatusCodePageAsync(
            new StatusCodeContext(context, new StatusCodePagesOptions(), _ => Task.CompletedTask));

        Assert.Equal(status, context.Response.StatusCode);
        Assert.Null(context.Response.ContentType);
        Assert.Equal(0, body.Length);
    }

    // AAP 0.3.4.2 and 0.3.4.6: once the store has committed, a failure while the response DTO is serialised, before
    // any header is sent, is the unexpected 500 whose writeOutcome is "written", so the client never resends a save
    // that was applied. It is never the 400 of a malformed request: binding wraps every request-body JSON failure in
    // a BadHttpRequestException, so a bare JsonException comes from server-side work such as this.
    [Theory]
    [MemberData(nameof(CommittedWrites))]
    public async Task ResponseSerializationFailureAfterCommitIsUnexpectedAndWritten(
        string environment,
        string route,
        bool useKestrel)
    {
        var sentinel = NewSentinel();
        await using var host = await ErrorPipelineHost.StartAsync(new ErrorPipelineHostOptions
        {
            EnvironmentName = environment,
            UseKestrel = useKestrel,
            ConfigureJson = json => json.Converters.Insert(0, new FailingWriteConverter<RecordDto>(sentinel)),
        });
        var store = host.Store;
        var seeded = route == UpdateRoute
            ? store.SeedRecord(
                FakeForms.EmptyViewId,
                new Dictionary<string, FieldValue> { ["LastName"] = FieldValue.FromText("Before") })
            : null;
        var body = new { values = new Dictionary<string, object?> { ["LastName"] = "After" } };
        using var client = host.CreateClient();

        using var response = seeded is null
            ? await ApiTestClient.PostJsonAsync(client, RecordsPath, body)
            : await ApiTestClient.PutJsonAsync(client, $"{RecordsPath}/{seeded}", body);
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(ApiProblems.ProblemContentType, response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(text);
        var problem = ApiTestClient.AssertProblemDetails(
            document.RootElement,
            (int)HttpStatusCode.InternalServerError);
        Assert.Contains(ApiProblems.UnexpectedDetail, problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Equal(WriteOutcomeExtensions.WireWritten, problem.GetProperty("writeOutcome").GetString());
        Assert.DoesNotContain(sentinel, text, StringComparison.Ordinal);

        // The save was applied before its response failed.
        var record = Assert.Single(store.GetRecords(FakeForms.EmptyViewId));
        Assert.Equal("After", record.Values["LastName"].AsText());
        var write = Assert.Single(store.WriteCalls);
        if (seeded is null)
        {
            Assert.Equal(nameof(IDataStore.InsertAsync), write.Method);
        }
        else
        {
            Assert.Equal(seeded, record.GlobalRecordId);
            Assert.Equal(nameof(IDataStore.UpdateAsync), write.Method);
        }

        var entry = Assert.Single(host.Logs.Find(captured => captured.Category == HandlerCategory));
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal((int)HttpStatusCode.InternalServerError, StateValue(entry, "StatusCode"));
        Assert.Equal(typeof(JsonException).FullName, StateValue(entry, "ExceptionType"));
        Assert.Equal(WriteOutcomeExtensions.WireWritten, StateValue(entry, "WriteOutcome"));
        Assert.False(host.Logs.ContainsText(sentinel));
    }

    // Outside a record write the same bare JsonException is the plain unexpected 500: no writeOutcome member, which
    // only the record writes document.
    [Theory]
    [MemberData(nameof(Environments))]
    public async Task ResponseSerializationFailureOnAReadIsAPlainUnexpectedError(string environment)
    {
        var sentinel = NewSentinel();
        await using var host = await ErrorPipelineHost.StartAsync(new ErrorPipelineHostOptions
        {
            EnvironmentName = environment,
            ConfigureJson = json =>
                json.Converters.Insert(0, new FailingWriteConverter<RecordCountResponse>(sentinel)),
        });
        using var client = host.CreateClient();

        using var response = await client.GetAsync($"{RecordsPath}/count");
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(ApiProblems.ProblemContentType, response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(text);
        var problem = ApiTestClient.AssertProblemDetails(
            document.RootElement,
            (int)HttpStatusCode.InternalServerError);
        Assert.Contains(ApiProblems.UnexpectedDetail, problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.False(problem.TryGetProperty("writeOutcome", out _), text);
        Assert.DoesNotContain(sentinel, text, StringComparison.Ordinal);
        Assert.Empty(host.Store.WriteCalls);

        var entry = Assert.Single(host.Logs.Find(captured => captured.Category == HandlerCategory));
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal(typeof(JsonException).FullName, StateValue(entry, "ExceptionType"));
        Assert.Null(StateValue(entry, "WriteOutcome"));
        Assert.False(host.Logs.ContainsText(sentinel));
    }

    // AAP 0.3.4.2 and 0.3.4.7: on each of the four body routes a body that cannot be bound is a request-level 400
    // whose detail names the problem, with no errors map, no writeOutcome and nothing written.
    [Theory]
    [MemberData(nameof(UnusableBodies))]
    public async Task UnusableBodyIsARequestLevel400NamingTheProblem(
        string environment,
        string framing,
        string route,
        string body,
        string expectedDetail)
    {
        await using var host = await ErrorPipelineHost.StartAsync(new ErrorPipelineHostOptions
        {
            EnvironmentName = environment,
            UseKestrel = framing is KestrelContentLength or KestrelChunked or KestrelUnframed,
        });
        var (method, path) = ResolveRoute(host.Store, route);

        var (status, mediaType, text) = await SendBodyAsync(host, method, path, framing, body);

        Assert.Equal((int)HttpStatusCode.BadRequest, status);
        Assert.Equal(ApiProblems.ProblemContentType, mediaType);
        using var document = JsonDocument.Parse(text);
        var problem = ApiTestClient.AssertProblemDetails(document.RootElement, (int)HttpStatusCode.BadRequest);
        Assert.Equal(expectedDetail, problem.GetProperty("detail").GetString());
        Assert.False(problem.TryGetProperty("errors", out _), text);
        Assert.False(problem.TryGetProperty("writeOutcome", out _), text);
        Assert.Empty(host.Store.WriteCalls);
    }

    // AAP 0.3.4.6: a caller that aborts its request gets no response and leaves one Information line. The
    // exception-handler middleware would settle the cancellation itself, at a level the committed filter drops, so the
    // API's exception boundary is what answers and logs it.
    [Theory]
    [MemberData(nameof(Environments))]
    public async Task CallerAbortedRequestWritesNoResponseAndLogsOneInformationLine(string environment)
    {
        var started = NewSignal();
        var finished = NewSignal();
        await using var host = await ErrorPipelineHost.StartAsync(new ErrorPipelineHostOptions
        {
            EnvironmentName = environment,
            MapEndpoints = endpoints => endpoints.MapGet(BoundaryTestPath, async context =>
            {
                SignalWhenFinished(context, finished);
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, context.RequestAborted);
            }),
        });
        using var client = host.CreateClient();
        using var cancellation = new CancellationTokenSource();

        var request = client.GetAsync(BoundaryTestPath, cancellation.Token);
        await WaitForAsync(started.Task, "the endpoint to start");
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await WaitForAsync(finished.Task, "the server to finish the aborted request");
        AssertOneClientAbortLine(host.Logs);
    }

    // The boundary alone, for either exception a client abort raises: a cancelled RequestAborted means status 499, no
    // body byte, one Information line, and no trace of the exception's message.
    [Theory]
    [InlineData(typeof(OperationCanceledException))]
    [InlineData(typeof(IOException))]
    public async Task BoundaryAnswersCallerAbortWithNoBodyAndOneInformationLine(Type exceptionType)
    {
        var sentinel = NewSentinel();
        using var logs = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(logging => logging.AddProvider(logs));
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        using var body = new MemoryStream();
        var context = new DefaultHttpContext { RequestAborted = aborted.Token };
        context.Response.Body = body;
        var failure = (Exception)Activator.CreateInstance(exceptionType, sentinel)!;
        var boundary = new ApiExceptionBoundaryMiddleware(
            _ => Task.FromException(failure),
            loggerFactory.CreateLogger<ApiExceptionHandler>());

        await boundary.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status499ClientClosedRequest, context.Response.StatusCode);
        Assert.Equal(0, body.Length);
        AssertOneClientAbortLine(logs);
        Assert.False(logs.ContainsText(sentinel));
    }

    // AAP 0.3.4.2: a cancellation the API raises itself while the caller is still connected is not an abort. It is the
    // fixed unexpected 500, logged once at Error with its type, and its message reaches neither the body nor a log.
    [Theory]
    [MemberData(nameof(Environments))]
    public async Task InternalCancellationOnAnActiveRequestIsTheFixedUnexpectedError(string environment)
    {
        var sentinel = NewSentinel();
        await using var host = await ErrorPipelineHost.StartAsync(new ErrorPipelineHostOptions
        {
            EnvironmentName = environment,
            MapEndpoints = endpoints => endpoints.MapGet(
                BoundaryTestPath,
                _ => throw new OperationCanceledException(sentinel)),
        });
        using var client = host.CreateClient();

        using var response = await client.GetAsync(BoundaryTestPath);
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(ApiProblems.ProblemContentType, response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(text);
        var problem = ApiTestClient.AssertProblemDetails(
            document.RootElement,
            (int)HttpStatusCode.InternalServerError);
        Assert.Contains(ApiProblems.UnexpectedDetail, problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain(sentinel, text, StringComparison.Ordinal);

        var entry = Assert.Single(host.Logs.Find(captured => captured.Category == HandlerCategory));
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal(RequestFailedEvent, entry.EventId.Name);
        Assert.Null(entry.ExceptionType);
        Assert.Equal(typeof(OperationCanceledException).FullName, StateValue(entry, "ExceptionType"));
        Assert.Empty(host.Logs.Find(IsFrameworkExceptionLine));
        Assert.False(host.Logs.ContainsText(sentinel));
    }

    // Only a cancelled RequestAborted makes a cancellation an abort: otherwise the boundary lets the same exception
    // through, untouched and unlogged, to the exception handler and its 500.
    [Fact]
    public async Task BoundaryRethrowsCancellationOnAnActiveRequestUnchanged()
    {
        using var logs = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(logging => logging.AddProvider(logs));
        var context = new DefaultHttpContext();
        var failure = new OperationCanceledException(NewSentinel());
        var boundary = new ApiExceptionBoundaryMiddleware(
            _ => Task.FromException(failure),
            loggerFactory.CreateLogger<ApiExceptionHandler>());

        var thrown = await Assert.ThrowsAsync<OperationCanceledException>(() => boundary.InvokeAsync(context));

        Assert.Same(failure, thrown);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Empty(logs.Entries);
    }

    // AAP 0.9.3: a failure after the response started cannot change the status the client already holds. The boundary
    // aborts the response, so it stays visibly incomplete (no last chunk), and logs one redacted Error line; neither
    // the exception-handler middleware nor Kestrel logs the exception, its message or its SQL text. The endpoint fails
    // only once the client holds the partial body, because an abort discards output the socket has not sent yet.
    [Theory]
    [MemberData(nameof(Environments))]
    public async Task FailureAfterTheResponseStartedOnKestrelLeavesItIncompleteAndLogsOneRedactedLine(
        string environment)
    {
        var sentinel = NewSentinel();
        var received = NewSignal();
        var finished = NewSignal();
        await using var host = await ErrorPipelineHost.StartAsync(new ErrorPipelineHostOptions
        {
            EnvironmentName = environment,
            UseKestrel = true,
            MapEndpoints = endpoints => endpoints.MapGet(BoundaryTestPath, async context =>
            {
                SignalWhenFinished(context, finished);
                await WritePartialThenFailAsync(context, sentinel, received.Task);
            }),
        });

        var response = await GetRawUntilClosedAsync(host, BoundaryTestPath, PartialBody, received);
        await WaitForAsync(finished.Task, "the server to finish the failed request");

        Assert.StartsWith("HTTP/1.1 200 ", response, StringComparison.Ordinal);
        Assert.Contains("Transfer-Encoding: chunked", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(PartialBody, response, StringComparison.Ordinal);
        Assert.DoesNotContain("\r\n0\r\n\r\n", response, StringComparison.Ordinal);
        var entry = AssertOneStartedResponseLine(host.Logs, sentinel, StatusCodes.Status200OK);
        Assert.Null(StateValue(entry, "WriteOutcome"));
    }

    // The same failure on TestServer: the client receives the head, its read of the body then fails, and the log is
    // the same single redacted line.
    [Theory]
    [MemberData(nameof(Environments))]
    public async Task FailureAfterTheResponseStartedOnTestServerFailsTheReadAndLogsOneRedactedLine(string environment)
    {
        var sentinel = NewSentinel();
        var finished = NewSignal();
        await using var host = await ErrorPipelineHost.StartAsync(new ErrorPipelineHostOptions
        {
            EnvironmentName = environment,
            MapEndpoints = endpoints => endpoints.MapGet(BoundaryTestPath, async context =>
            {
                SignalWhenFinished(context, finished);
                await WritePartialThenFailAsync(context, sentinel);
            }),
        });
        using var client = host.CreateClient();

        using var response = await client.GetAsync(BoundaryTestPath, HttpCompletionOption.ResponseHeadersRead);
        var read = await Record.ExceptionAsync(() => response.Content.ReadAsStringAsync());
        await WaitForAsync(finished.Task, "the server to finish the failed request");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(read);
        var entry = AssertOneStartedResponseLine(host.Logs, sentinel, StatusCodes.Status200OK);
        Assert.Null(StateValue(entry, "WriteOutcome"));
    }

    // AAP 0.3.4.2: a record write that committed before its started response failed is logged as "written", with the
    // status already sent and the write's operation.
    [Theory]
    [MemberData(nameof(Environments))]
    public async Task FailureAfterTheResponseStartedOnACommittedWriteLogsWritten(string environment)
    {
        var sentinel = NewSentinel();
        var finished = NewSignal();
        await using var host = await ErrorPipelineHost.StartAsync(new ErrorPipelineHostOptions
        {
            EnvironmentName = environment,
            MapEndpoints = endpoints => endpoints
                .MapPost(BoundaryTestPath, async context =>
                {
                    SignalWhenFinished(context, finished);
                    var outcome = WriteOutcomeFeature.GetOrCreate(context);
                    outcome.MarkWriteStarted();
                    outcome.MarkWritten();
                    context.Response.StatusCode = StatusCodes.Status201Created;
                    await WritePartialThenFailAsync(context, sentinel);
                })
                .WithMetadata(WriteEndpointMetadata.Insert),
        });
        using var client = host.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, BoundaryTestPath);

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        var read = await Record.ExceptionAsync(() => response.Content.ReadAsStringAsync());
        await WaitForAsync(finished.Task, "the server to finish the failed request");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(read);
        var entry = AssertOneStartedResponseLine(host.Logs, sentinel, StatusCodes.Status201Created);
        Assert.Equal(WriteOutcomeExtensions.WireWritten, StateValue(entry, "WriteOutcome"));
        Assert.Equal(WriteEndpointMetadata.Insert.Operation, StateValue(entry, "Operation"));
    }

    // A caller that aborts after the response started is still an abort, not a failure: one Information line and no
    // Error line.
    [Theory]
    [MemberData(nameof(Environments))]
    public async Task CallerAbortAfterTheResponseStartedLogsOnlyTheAbort(string environment)
    {
        var started = NewSignal();
        var finished = NewSignal();
        await using var host = await ErrorPipelineHost.StartAsync(new ErrorPipelineHostOptions
        {
            EnvironmentName = environment,
            MapEndpoints = endpoints => endpoints.MapGet(BoundaryTestPath, async context =>
            {
                SignalWhenFinished(context, finished);
                await context.Response.WriteAsync(PartialBody);
                await context.Response.Body.FlushAsync();
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, context.RequestAborted);
            }),
        });
        using var client = host.CreateClient();

        var response = await client.GetAsync(BoundaryTestPath, HttpCompletionOption.ResponseHeadersRead);
        var status = response.StatusCode;
        await WaitForAsync(started.Task, "the response to start");

        // Once its head has arrived, a TestServer client walks away from a request by disposing the unread response.
        response.Dispose();
        await WaitForAsync(finished.Task, "the server to finish the aborted request");

        Assert.Equal(HttpStatusCode.OK, status);
        AssertOneClientAbortLine(host.Logs);
    }

    // Every case names its client folder, so none falls back to the committed default (the repository's web/dist),
    // whose presence depends on whether the client was built.
    private static ApiFactory CreateFactory(string environment, string clientFolder) =>
        new(new ApiFactoryOptions
        {
            EnvironmentName = environment,
            ConnectionMode = ApiConnectionMode.None,
            ExtraSettings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [ApiOptions.ClientPathKey] = clientFolder,
            },
        });

    // A client folder that does not exist and is never created, so the host serves no client and maps no client
    // fallback. The /api miss cases select it so they do not depend on a built web/dist: with the client served, an
    // /api miss stays a 404 only once the API's own /api routes are mapped.
    private static string AbsentClientFolder() =>
        Path.Combine(Path.GetTempPath(), "epiinfo-absent-client-" + Guid.NewGuid().ToString("N"));

    private static void AddAccept(HttpRequestMessage request, string? accept)
    {
        if (accept is not null)
        {
            Assert.True(request.Headers.TryAddWithoutValidation("Accept", accept), "The Accept header was not added.");
        }
    }

    private static async Task AssertFixedProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode status,
        string type,
        string title,
        string detail)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.True(
            string.Equals(ProblemMediaType, response.Content.Headers.ContentType?.MediaType, StringComparison.Ordinal),
            "The error response is not application/problem+json.");

        JsonElement body = ApiTestClient.AssertProblemDetails(
            await ApiTestClient.ReadJsonElementAsync(response),
            (int)status);

        Assert.True(
            ProblemMembers.SequenceEqual(
                body.EnumerateObject().Select(member => member.Name).Order(StringComparer.Ordinal),
                StringComparer.Ordinal),
            "The error body does not hold exactly type, title, status, detail and traceId.");
        Assert.True(
            string.Equals(type, StringMember(body, "type"), StringComparison.Ordinal),
            "The error body's type is not the status default.");
        Assert.True(
            string.Equals(title, StringMember(body, "title"), StringComparison.Ordinal),
            "The error body's title is not the status default.");
        Assert.True(
            string.Equals(detail, StringMember(body, "detail"), StringComparison.Ordinal),
            "The error body's detail is not the fixed text of its status.");
    }

    // The detail and trace id a status-only path produces: a framework body shaped by Customize, which writes nothing,
    // or the status-code page written to an empty response.
    private static async Task<(string? Detail, string? TraceId)> ProduceStatusOnlyProblemAsync(
        string path,
        int status,
        DefaultHttpContext context,
        MemoryStream body)
    {
        switch (path)
        {
            case CustomizePath:
                var problemContext = new ProblemDetailsContext
                {
                    HttpContext = context,
                    ProblemDetails = new ProblemDetails { Status = status },
                };
                ApiProblems.Customize(problemContext);
                Assert.Equal(status, problemContext.ProblemDetails.Status);
                return (
                    problemContext.ProblemDetails.Detail,
                    problemContext.ProblemDetails.Extensions.TryGetValue(ApiProblems.TraceIdKey, out object? traceId)
                        ? traceId as string
                        : null);

            case StatusCodePagePath:
                context.Response.StatusCode = status;
                await ApiProblems.WriteStatusCodePageAsync(
                    new StatusCodeContext(context, new StatusCodePagesOptions(), _ => Task.CompletedTask));
                Assert.Equal(status, context.Response.StatusCode);
                Assert.Equal(ProblemMediaType, context.Response.ContentType);
                JsonElement problem = ApiTestClient.AssertProblemDetails(Parse(body), status);
                return (StringMember(problem, "detail"), StringMember(problem, ApiProblems.TraceIdKey));

            default:
                throw new ArgumentOutOfRangeException(nameof(path), path, "Unknown status-only path.");
        }
    }

    // The no-activity cases clear any ambient activity, so the request's trace identifier is the expected trace id.
    private static Activity? StartActivity(bool withActivity)
    {
        if (!withActivity)
        {
            Activity.Current = null;
            return null;
        }

        return new Activity(nameof(ErrorContractTests)).Start();
    }

    private static void AssertTraceId(string? traceId, Activity? activity, HttpContext context)
    {
        string expected = activity?.Id ?? context.TraceIdentifier;
        Assert.False(string.IsNullOrWhiteSpace(expected), "The expected trace id is blank.");
        Assert.True(
            string.Equals(expected, traceId, StringComparison.Ordinal),
            activity is null
                ? "The traceId member is not the request's trace identifier."
                : "The traceId member is not the current activity's id.");
    }

    private static void AssertUnexpectedDetail(string? detail, string? traceId)
    {
        Assert.False(string.IsNullOrWhiteSpace(traceId), "The 500 body has no traceId.");
        Assert.True(
            string.Equals(ExpectedUnexpectedSentence, ApiProblems.UnexpectedDetail, StringComparison.Ordinal),
            "The generic 500 text is not the one fixed sentence.");
        Assert.True(
            string.Equals(
                string.Concat(ExpectedUnexpectedSentence, ExpectedTraceIdLabel, traceId),
                detail,
                StringComparison.Ordinal),
            "The 500 detail is not the fixed sentence followed by the body's traceId.");
        Assert.True(
            string.Equals(ApiProblems.UnexpectedDetailFor(traceId), detail, StringComparison.Ordinal),
            "The 500 detail is not the generic sentence plus the body's traceId.");
        Assert.True(
            detail is not null && detail.StartsWith(ApiProblems.UnexpectedDetail, StringComparison.Ordinal),
            "The 500 detail does not open with the generic sentence.");
    }

    private static (DefaultHttpContext Context, MemoryStream Body) NewContext()
    {
        var body = new MemoryStream();
        var context = new DefaultHttpContext();
        context.Response.Body = body;
        return (context, body);
    }

    private static JsonElement Parse(MemoryStream body)
    {
        using JsonDocument document = JsonDocument.Parse(body.ToArray());
        return document.RootElement.Clone();
    }

    private static string? StringMember(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object
        && body.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    // The method and path of a body route; the update route gets a freshly seeded active record.
    private static (HttpMethod Method, string Path) ResolveRoute(FakeDataStore store, string route) => route switch
    {
        CreateRoute => (HttpMethod.Post, RecordsPath),
        UpdateRoute => (HttpMethod.Put, $"{RecordsPath}/{store.SeedRecord(FakeForms.EmptyViewId)}"),
        SearchRoute => (HttpMethod.Post, $"{RecordsPath}/search"),
        FieldEventRoute => (HttpMethod.Post, FieldEventPath),
        _ => throw new ArgumentOutOfRangeException(nameof(route), route, "Unknown body route."),
    };

    // Sends body as application/json exactly as framing says, and returns the status, media type and body text.
    private static async Task<(int Status, string? MediaType, string Body)> SendBodyAsync(
        ErrorPipelineHost host,
        HttpMethod method,
        string path,
        string framing,
        string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        if (framing is TestServerContentLength or TestServerUnknownLength)
        {
            HttpContent content = framing == TestServerUnknownLength
                ? new UnknownLengthContent(bytes)
                : new ByteArrayContent(bytes);
            content.Headers.ContentType = new(ApiTestClient.JsonMediaType);
            using var client = host.CreateClient();
            using var request = new HttpRequestMessage(method, path) { Content = content };
            using var response = await client.SendAsync(request);
            return (
                (int)response.StatusCode,
                response.Content.Headers.ContentType?.MediaType,
                await response.Content.ReadAsStringAsync());
        }

        var raw = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"{method.Method} {path} HTTP/1.1\r\n")
            .Append("Host: 127.0.0.1\r\n")
            .Append(CultureInfo.InvariantCulture, $"Content-Type: {ApiTestClient.JsonMediaType}\r\n")
            .Append("Connection: close\r\n");
        if (framing == KestrelChunked)
        {
            raw.Append("Transfer-Encoding: chunked\r\n\r\n");
            if (bytes.Length > 0)
            {
                raw.Append(CultureInfo.InvariantCulture, $"{bytes.Length:x}\r\n{body}\r\n");
            }

            // The last chunk: for an empty body it is the only one.
            raw.Append("0\r\n\r\n");
        }
        else if (framing == KestrelUnframed)
        {
            Assert.Empty(bytes);
            raw.Append("\r\n");
        }
        else
        {
            raw.Append(CultureInfo.InvariantCulture, $"Content-Length: {bytes.Length}\r\n\r\n").Append(body);
        }

        var exchange = await host.SendRawAsync(raw.ToString());
        Assert.False(exchange.ConnectionReset, exchange.Response);
        return ParseRawResponse(exchange.Response);
    }

    // Splits a raw HTTP/1.1 response into its status, media type and body, undoing chunked framing.
    private static (int Status, string? MediaType, string Body) ParseRawResponse(string response)
    {
        var headEnd = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        Assert.True(headEnd > 0, $"The response has no complete head: {response}");

        var lines = response[..headEnd].Split("\r\n");
        var statusLine = lines[0].Split(' ', 3);
        Assert.True(statusLine.Length >= 2 && statusLine[0] == "HTTP/1.1", $"Unexpected status line: {lines[0]}");
        var status = int.Parse(statusLine[1], NumberStyles.None, CultureInfo.InvariantCulture);

        string? mediaType = null;
        var chunked = false;
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            Assert.True(colon > 0, $"Malformed response header: {line}");
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                mediaType = value.Split(';', 2)[0].Trim();
            }
            else if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
            {
                chunked = value.Contains("chunked", StringComparison.OrdinalIgnoreCase);
            }
        }

        var body = response[(headEnd + 4)..];
        return (status, mediaType, chunked ? Unchunk(body) : body);
    }

    // The payload of a chunked body: each chunk's hexadecimal size line, data and CRLF, up to the zero-size chunk.
    private static string Unchunk(string body)
    {
        var payload = new StringBuilder();
        var index = 0;
        while (true)
        {
            var sizeEnd = body.IndexOf("\r\n", index, StringComparison.Ordinal);
            Assert.True(sizeEnd >= 0, $"The chunked body ends before its last chunk: {body}");
            var size = int.Parse(
                body.AsSpan(index, sizeEnd - index),
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture);
            if (size == 0)
            {
                return payload.ToString();
            }

            Assert.True(sizeEnd + 2 + size <= body.Length, $"A chunk is cut short: {body}");
            payload.Append(body, sizeEnd + 2, size);
            index = sizeEnd + 2 + size + 2;
        }
    }

    private static object? StateValue(CapturedLogEntry entry, string key) =>
        Assert.Single(entry.State, pair => pair.Key == key).Value;

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Signals once the server has finished the request, after every middleware has returned, so every line the
    // request logs has been captured by then.
    private static void SignalWhenFinished(HttpContext context, TaskCompletionSource finished) =>
        context.Response.OnCompleted(() =>
        {
            finished.TrySetResult();
            return Task.CompletedTask;
        });

    private static async Task WaitForAsync(Task signal, string what)
    {
        var completed = await Task.WhenAny(signal, Task.Delay(HostedWait));
        Assert.True(completed == signal, "Timed out waiting for " + what + ".");
        await signal;
    }

    // Sends the head and part of the body, waits for clientReceived when given, then fails with an exception whose
    // message holds a sentinel and SQL text, as the inner exception of a store failure can.
    private static async Task WritePartialThenFailAsync(
        HttpContext context,
        string sentinel,
        Task? clientReceived = null)
    {
        await context.Response.WriteAsync(PartialBody);
        await context.Response.Body.FlushAsync();
        if (clientReceived is not null)
        {
            await clientReceived.WaitAsync(HostedWait);
        }

        throw new InvalidOperationException(sentinel + " " + SecretSqlText);
    }

    // Sends a GET for path over a new TCP connection to the Kestrel listener, signals received once the response text
    // holds expected, and reads until the server ends the connection, by a close or a reset. Returns every byte
    // received, as text.
    private static async Task<string> GetRawUntilClosedAsync(
        ErrorPipelineHost host,
        string path,
        string expected,
        TaskCompletionSource received)
    {
        var address = host.Address ?? throw new InvalidOperationException("Raw HTTP needs the Kestrel host.");
        using var timeout = new CancellationTokenSource(HostedWait);
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(address.Host, address.Port, timeout.Token);
        var stream = tcp.GetStream();
        var request = "GET " + path + " HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), timeout.Token);

        var response = new StringBuilder();
        var buffer = new byte[4096];
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer, timeout.Token)) > 0)
            {
                response.Append(Encoding.UTF8.GetString(buffer, 0, read));
                if (response.ToString().Contains(expected, StringComparison.Ordinal))
                {
                    received.TrySetResult();
                }
            }
        }
        catch (IOException)
        {
            // An aborted connection can end with a reset; what arrived before it is the response.
        }

        return response.ToString();
    }

    // Exactly one line for a client abort: the handler category's Information RequestAborted, with no exception
    // object, no failure line beside it, and no framework line about the exception.
    private static void AssertOneClientAbortLine(CapturingLoggerProvider logs)
    {
        var entry = Assert.Single(logs.Find(captured => captured.Category == HandlerCategory));
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal(RequestAbortedEvent, entry.EventId.Name);
        Assert.Null(entry.ExceptionType);
        Assert.Empty(logs.Find(IsFrameworkExceptionLine));
    }

    // Exactly one line for a failure after the response started: the handler category's Error ResponseAborted with
    // the status already sent and the exception's type only, no framework line about the exception, and neither the
    // sentinel nor the SQL text of the exception's message in any line.
    private static CapturedLogEntry AssertOneStartedResponseLine(
        CapturingLoggerProvider logs,
        string sentinel,
        int sentStatus)
    {
        var entry = Assert.Single(logs.Find(captured => captured.Category == HandlerCategory));
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal(ResponseAbortedEvent, entry.EventId.Name);
        Assert.Null(entry.ExceptionType);
        Assert.Equal(sentStatus, StateValue(entry, "StatusCode"));
        Assert.Equal(typeof(InvalidOperationException).FullName, StateValue(entry, "ExceptionType"));
        Assert.Empty(logs.Find(IsFrameworkExceptionLine));
        Assert.False(logs.ContainsText(sentinel));
        Assert.False(logs.ContainsText(SecretSqlText));
        return entry;
    }

    // A line the framework writes about an exception it saw: anything from the diagnostics middleware (the exception
    // handler and the developer exception page), Kestrel's application-error line, or any line carrying an exception.
    private static bool IsFrameworkExceptionLine(CapturedLogEntry entry) =>
        entry.Category.StartsWith("Microsoft.AspNetCore.Diagnostics", StringComparison.Ordinal)
        || entry.EventId.Name == "ApplicationError"
        || entry.ExceptionType is not null;

    /// <summary>
    /// Fails every serialisation of <typeparamref name="T"/> with the serializer's own exception type, carrying a
    /// sentinel where a real failure would carry the path and value it failed on.
    /// </summary>
    private sealed class FailingWriteConverter<T>(string sentinel) : JsonConverter<T>
    {
        public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException("Response types are never read from a request.");

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            throw new JsonException(sentinel);
    }

    /// <summary>
    /// Request content whose length is not known in advance, as a streamed upload is: the client sends it chunked
    /// and the server sees no <c>Content-Length</c>.
    /// </summary>
    private sealed class UnknownLengthContent(byte[] content) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(content.AsMemory()).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}

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

/// <summary>What <see cref="ErrorPipelineHost.StartAsync"/> builds.</summary>
internal sealed record ErrorPipelineHostOptions
{
    /// <summary>The host environment, which selects the committed <c>appsettings.{Environment}.json</c>.</summary>
    public string EnvironmentName { get; init; } = "Production";

    /// <summary>
    /// <see langword="true"/> to serve on real Kestrel at <c>http://127.0.0.1:0</c>; otherwise TestServer, which
    /// applies no Kestrel limit and no HTTP/1.1 framing.
    /// </summary>
    public bool UseKestrel { get; init; }

    /// <summary>The store the services use; <see cref="FakeDataStore.WithStandardForms"/> when not set.</summary>
    public FakeDataStore? Store { get; init; }

    /// <summary>
    /// Runs after the API's own JSON settings on the minimal-API serializer options, for example to add a converter
    /// whose failure stands in for a response-serialization failure.
    /// </summary>
    public Action<JsonSerializerOptions>? ConfigureJson { get; init; }

    /// <summary>Maps extra endpoints after the API's own, for failures no API endpoint can be made to raise.</summary>
    public Action<IEndpointRouteBuilder>? MapEndpoints { get; init; }
}

/// <summary>
/// A host of the API's error pipeline for tests that need no database: the real <see cref="ApiExceptionHandler"/>,
/// its <see cref="ApiExceptionBoundaryMiddleware"/>, <see cref="ApiProblems.Customize"/>, the status-code pages of
/// <see cref="ApiProblems.WriteStatusCodePageAsync"/>, the request <c>Host</c> guard, the request guards, endpoints and
/// services, composed in the order <c>Program.cs</c> uses, over a <see cref="FakeDataStore"/>,
/// with the committed configuration of the API project and its logging as <c>Program.cs</c> composes it: the JSON
/// console, with a <see cref="CapturingLoggerProvider"/> added beside it as one more sink.
/// </summary>
/// <remarks>
/// <para>
/// The content root is <c>src/EpiInfo.Api</c>, so <c>appsettings.json</c> and <c>appsettings.{Environment}.json</c>
/// supply the <c>Logging:LogLevel</c> filters and the body limit. Those committed filters govern the JSON console and
/// the capture provider alike; the host changes no level, filter or console option, and the provider has no filter
/// of its own, so it captures exactly what production emits. Only the Kestrel endpoint is overridden, to
/// <c>http://127.0.0.1:0</c>, so a Kestrel host never binds the committed port.
/// </para>
/// <para>Each host is independent: create one per test and dispose it, so captured logs never mix.</para>
/// </remarks>
internal sealed class ErrorPipelineHost : IAsyncDisposable
{
    private const string KestrelUrlKey = "Kestrel:Endpoints:Http:Url";

    private const string EphemeralLoopbackUrl = "http://127.0.0.1:0";

    private ErrorPipelineHost(WebApplication app, CapturingLoggerProvider logs, FakeDataStore store, Uri? address)
    {
        App = app;
        Logs = logs;
        Store = store;
        Address = address;
    }

    /// <summary>The running application.</summary>
    public WebApplication App { get; }

    /// <summary>Every log entry the committed filters let through.</summary>
    public CapturingLoggerProvider Logs { get; }

    /// <summary>The store behind every endpoint.</summary>
    public FakeDataStore Store { get; }

    /// <summary>The Kestrel listen address, or <see langword="null"/> on TestServer.</summary>
    public Uri? Address { get; }

    /// <summary>Builds and starts the host.</summary>
    /// <param name="options">What to build; the defaults when <see langword="null"/>.</param>
    /// <returns>The started host.</returns>
    public static async Task<ErrorPipelineHost> StartAsync(ErrorPipelineHostOptions? options = null)
    {
        options ??= new ErrorPipelineHostOptions();
        var store = options.Store ?? FakeDataStore.WithStandardForms();
        var logs = new CapturingLoggerProvider();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = RepositoryPaths.ApiProjectDirectory,
            EnvironmentName = options.EnvironmentName,
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [KestrelUrlKey] = EphemeralLoopbackUrl,
        });

        if (options.UseKestrel)
        {
            // As Program.cs does: loading the Kestrel section binds endpoints, not limits.
            builder.WebHost.ConfigureKestrel((context, kestrel) =>
                kestrel.Limits.MaxRequestBodySize =
                    ApiOptions.FromConfiguration(context.Configuration).MaxRequestBodySize);
        }
        else
        {
            builder.WebHost.UseTestServer();
        }

        // As Program.cs does, with the capture provider added beside the JSON console rather than in its place.
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole();
        builder.Logging.AddProvider(logs);

        var services = builder.Services;
        services.AddSingleton(provider => ApiOptions.FromConfiguration(provider.GetRequiredService<IConfiguration>()));
        services.AddProblemDetails(problem => problem.CustomizeProblemDetails = ApiProblems.Customize);
        services.AddExceptionHandler<ApiExceptionHandler>();
        services.Configure<RouteHandlerOptions>(routing => routing.ThrowOnBadRequest = true);
        services.AddApiJson();
        if (options.ConfigureJson is { } configureJson)
        {
            services.ConfigureHttpJsonOptions(json => configureJson(json.SerializerOptions));
        }

        services.AddSingleton<IDataStore>(store);
        services.AddSingleton<ICheckCodeEngine, CheckCodeEngine>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<SaveStampProvider>();
        services.AddScoped<FormService>();
        services.AddScoped<RecordService>();
        services.AddScoped<FieldEventService>();

        var app = builder.Build();

        app.UseExceptionHandler();
        app.UseApiExceptionBoundary();
        app.UseStatusCodePages(ApiProblems.WriteStatusCodePageAsync);
        app.UseLoopbackHostGuard();
        app.UseRouting();
        app.UseRequestBodyLimit();
        app.UseJsonContentTypeGuard();

        app.MapFormEndpoints();
        app.MapRecordEndpoints();
        app.MapCheckCodeEndpoints();
        options.MapEndpoints?.Invoke(app);

        await app.StartAsync();

        Uri? address = null;
        if (options.UseKestrel)
        {
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
            address = new Uri(Assert.Single(addresses?.Addresses ?? []));
        }

        return new ErrorPipelineHost(app, logs, store, address);
    }

    /// <summary>A client for the host: TestServer's own, or one addressed to the Kestrel listener.</summary>
    /// <returns>A client the caller disposes.</returns>
    public HttpClient CreateClient() =>
        Address is null ? App.GetTestClient() : new HttpClient { BaseAddress = Address };

    /// <summary>
    /// Sends <paramref name="request"/> byte for byte over a new TCP connection to the Kestrel listener and reads
    /// until the server closes it, so a test can see the exact HTTP/1.1 framing, chunk terminator included.
    /// </summary>
    /// <param name="request">The complete request, head and body; it should carry <c>Connection: close</c>.</param>
    /// <param name="cancellationToken">Ends the exchange.</param>
    /// <returns>
    /// The raw response text, and whether the connection ended with a reset or I/O failure rather than an orderly
    /// close.
    /// </returns>
    /// <exception cref="InvalidOperationException">The host is not on Kestrel.</exception>
    public async Task<RawExchange> SendRawAsync(string request, CancellationToken cancellationToken = default)
    {
        if (Address is null)
        {
            throw new InvalidOperationException("Raw HTTP needs the Kestrel host.");
        }

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(Address.Host, Address.Port, cancellationToken);
        var stream = tcp.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), cancellationToken);

        using var received = new MemoryStream();
        var reset = false;
        try
        {
            await stream.CopyToAsync(received, cancellationToken);
        }
        catch (IOException)
        {
            reset = true;
        }

        return new RawExchange(Encoding.UTF8.GetString(received.ToArray()), reset);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await App.StopAsync();
        await App.DisposeAsync();
        Logs.Dispose();
    }
}

/// <summary>A raw HTTP/1.1 exchange with the Kestrel host.</summary>
/// <param name="Response">Every byte the server sent, as text.</param>
/// <param name="ConnectionReset">Whether the connection ended with a reset or I/O failure.</param>
internal sealed record RawExchange(string Response, bool ConnectionReset);
