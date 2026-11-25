using System.Globalization;
using System.Net;
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
using EpiInfo.Domain.Storage;
using EpiInfo.Domain.Values;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EpiInfo.Api.Tests.Hosting;

/// <summary>The server a <see cref="GuardPipelineHost"/> runs on.</summary>
internal enum GuardHostKind
{
    /// <summary>The in-process TestServer, which applies no Kestrel limit.</summary>
    TestServer,

    /// <summary>Real Kestrel on an ephemeral <c>127.0.0.1</c> port, reached over a socket.</summary>
    Kestrel,
}

/// <summary>The order in which a <see cref="GuardPipelineHost"/> adds the two request guards after routing.</summary>
internal enum GuardOrder
{
    /// <summary><c>UseRequestBodyLimit</c>, then <c>UseJsonContentTypeGuard</c>.</summary>
    BodyLimitFirst,

    /// <summary><c>UseJsonContentTypeGuard</c>, then <c>UseRequestBodyLimit</c>.</summary>
    MediaTypeFirst,
}

/// <summary>
/// A test host that isolates the API's request guards, <see cref="RequestBodyLimitMiddleware"/> and
/// <see cref="JsonContentTypeMiddleware"/>, behind the API's real error contract, JSON settings, endpoints and
/// services, over a <see cref="FakeDataStore"/>, on TestServer or on real Kestrel.
/// </summary>
/// <remarks>
/// <para>
/// Configuration: the committed <c>appsettings.json</c> read from the content root <c>src/EpiInfo.Api</c> in the
/// <c>Production</c> environment, so <c>Kestrel:Limits:MaxRequestBodySize</c> is the committed 1,048,576 bytes, with
/// every logging provider removed. Kestrel's <c>MaxRequestBodySize</c> is set from
/// <see cref="ApiOptions.FromConfiguration"/>, the same source the API's composition root applies it from. On Kestrel
/// the endpoint <c>Kestrel:Endpoints:Http:Url</c> is set to <c>http://127.0.0.1:0</c>, and the listener settings are
/// validated before any socket opens, as in the API's composition root: <see cref="LoopbackBindingGuard.Validate"/>
/// checks the configuration before and after the host is built, and Kestrel loads, with reload off, the copy that
/// <see cref="LoopbackBindingGuard.FreezeListenSettings"/> verified. A listen setting from any other source that breaks
/// the loopback rule, such as another Kestrel endpoint or <c>ASPNETCORE_URLS</c>, therefore fails the start before
/// anything binds. The bound address is then the proof, not the enforcement: it must be the only one, with the host
/// <c>127.0.0.1</c> and a non-zero port, and <see cref="Client"/> sends over a socket to it with no proxy. On
/// TestServer, which opens no socket, the client is in-process.
/// </para>
/// <para>
/// Services: ProblemDetails customised by <see cref="ApiProblems.Customize"/>, <see cref="ApiExceptionHandler"/>,
/// the API JSON settings, <see cref="RouteHandlerOptions.ThrowOnBadRequest"/> on, <see cref="ApiOptions"/> from
/// configuration, the store as <see cref="IDataStore"/>, the real <see cref="CheckCodeEngine"/>,
/// <see cref="TimeProvider.System"/>, <see cref="SaveStampProvider"/>, and the scoped <see cref="FormService"/>,
/// <see cref="RecordService"/> and <see cref="FieldEventService"/>.
/// </para>
/// <para>
/// Pipeline, in order, as the API's composition root builds it: <c>UseExceptionHandler</c>,
/// <c>UseApiExceptionBoundary</c>, <c>UseStatusCodePages</c> with <see cref="ApiProblems.WriteStatusCodePageAsync"/>,
/// <c>UseLoopbackHostGuard</c>, <c>UseRouting</c>, the two guards in the <see cref="GuardOrder"/> given, the form,
/// record and check-code endpoints, the two probe routes, and <c>UseClientApp</c> last.
/// </para>
/// <para>
/// Probes: <c>POST</c> <see cref="GuardedProbePath"/> carries <see cref="JsonBodyMetadata"/> and
/// <c>POST</c> <see cref="UnguardedProbePath"/> does not. Each reads
/// <see cref="IHttpMaxRequestBodySizeFeature.MaxRequestBodySize"/> before touching the body, reads the body to its
/// end, and answers 200 with <see cref="ProbeBytesProperty"/> (the bytes read) and
/// <see cref="ProbeMaxRequestBodySizeProperty"/> (the server's limit, or <c>null</c> where the server exposes none or
/// sets no limit). A failure while reading, such as a 413 <see cref="BadHttpRequestException"/>, propagates to the
/// exception handler.
/// </para>
/// </remarks>
internal sealed class GuardPipelineHost : IAsyncDisposable
{
    /// <summary>The probe route that carries <see cref="JsonBodyMetadata"/>.</summary>
    public const string GuardedProbePath = "/test/guarded-body";

    /// <summary>The probe route without <see cref="JsonBodyMetadata"/>.</summary>
    public const string UnguardedProbePath = "/test/unguarded-body";

    /// <summary>The probe response member holding the number of body bytes the probe read.</summary>
    public const string ProbeBytesProperty = "bytes";

    /// <summary>
    /// The probe response member holding <see cref="IHttpMaxRequestBodySizeFeature.MaxRequestBodySize"/> as the
    /// request reached the probe, or <c>null</c>.
    /// </summary>
    public const string ProbeMaxRequestBodySizeProperty = "maxRequestBodySize";

    private const string EnvironmentName = "Production";

    private const string KestrelUrlKey = "Kestrel:Endpoints:Http:Url";

    private const string EphemeralLoopbackUrl = "http://127.0.0.1:0";

    private const string LoopbackHost = "127.0.0.1";

    private const int ProbeBufferSize = 16 * 1024;

    private readonly WebApplication _app;

    private bool _disposed;

    private GuardPipelineHost(GuardHostKind kind, WebApplication app, HttpClient client)
    {
        Kind = kind;
        _app = app;
        Client = client;
        BaseAddress = client.BaseAddress
            ?? throw new InvalidOperationException("The host's client has no base address.");
    }

    /// <summary>The server the host runs on.</summary>
    public GuardHostKind Kind { get; }

    /// <summary>
    /// The client of the host, with <see cref="BaseAddress"/> set; on Kestrel it sends over a socket to the bound
    /// <c>127.0.0.1</c> address.
    /// </summary>
    public HttpClient Client { get; }

    /// <summary>The address <see cref="Client"/> sends relative paths to.</summary>
    public Uri BaseAddress { get; }

    /// <summary>The application's services.</summary>
    public IServiceProvider Services => _app.Services;

    /// <summary>Builds and starts a host, and creates its client.</summary>
    /// <param name="kind">The server to run on.</param>
    /// <param name="store">The store registered as <see cref="IDataStore"/>.</param>
    /// <param name="order">The order of the two request guards after routing.</param>
    /// <param name="cancellationToken">Cancels the start.</param>
    /// <returns>The started host; the caller disposes it.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> or <paramref name="order"/> is not a defined value.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// On Kestrel, a listen setting breaks the loopback rule; it is thrown before any socket opens.
    /// </exception>
    public static async Task<GuardPipelineHost> StartAsync(
        GuardHostKind kind,
        FakeDataStore store,
        GuardOrder order = GuardOrder.BodyLimitFirst,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a defined host kind.");
        }

        if (!Enum.IsDefined(order))
        {
            throw new ArgumentOutOfRangeException(nameof(order), order, "Not a defined guard order.");
        }

        ArgumentNullException.ThrowIfNull(store);

        WebApplication app = Build(kind, store, order);
        var started = false;
        try
        {
            if (kind == GuardHostKind.Kestrel)
            {
                // Checked again on the built configuration, which also holds the sources applied during Build, before
                // the server starts, as in the API's composition root.
                LoopbackBindingGuard.Validate(app.Configuration);
            }

            await app.StartAsync(cancellationToken).ConfigureAwait(false);
            started = true;
            HttpClient client = kind == GuardHostKind.Kestrel ? CreateSocketClient(app) : app.GetTestClient();
            return new GuardPipelineHost(kind, app, client);
        }
        catch
        {
            await StopAndDisposeAsync(app, started).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Disposes the client, stops the application and disposes it. Later calls do nothing.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Client.Dispose();
        await StopAndDisposeAsync(_app, started: true).ConfigureAwait(false);
    }

    private static WebApplication Build(GuardHostKind kind, FakeDataStore store, GuardOrder order)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = RepositoryPaths.ApiProjectDirectory,
            EnvironmentName = EnvironmentName,
        });
        builder.Logging.ClearProviders();

        if (kind == GuardHostKind.Kestrel)
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [KestrelUrlKey] = EphemeralLoopbackUrl,
            });

            // As in the API's composition root, every listen setting from every source is checked before the host is
            // built, so one that breaks the loopback rule never opens a socket. A rejected start releases the
            // configuration's settings-file watchers, since no host exists yet to dispose them.
            try
            {
                LoopbackBindingGuard.Validate(builder.Configuration);
            }
            catch
            {
                builder.Configuration.Dispose();
                throw;
            }
        }
        else
        {
            builder.WebHost.UseTestServer();
        }

        builder.WebHost.ConfigureKestrel((context, options) =>
        {
            // Kestrel loads the guard's frozen copy of the verified listen settings with reload off, as the API does.
            options.Configure(LoopbackBindingGuard.FreezeListenSettings(context.Configuration), reloadOnChange: false);
            options.Limits.MaxRequestBodySize = ApiOptions.FromConfiguration(context.Configuration).MaxRequestBodySize;
        });

        IServiceCollection services = builder.Services;
        services.AddProblemDetails(options => options.CustomizeProblemDetails = ApiProblems.Customize);
        services.AddExceptionHandler<ApiExceptionHandler>();
        services.AddApiJson();
        services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
        services.AddSingleton(provider => ApiOptions.FromConfiguration(provider.GetRequiredService<IConfiguration>()));
        services.AddSingleton<IDataStore>(store);
        services.AddSingleton<ICheckCodeEngine, CheckCodeEngine>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<SaveStampProvider>();
        services.AddScoped<FormService>();
        services.AddScoped<RecordService>();
        services.AddScoped<FieldEventService>();

        WebApplication app = builder.Build();
        app.UseExceptionHandler();
        app.UseApiExceptionBoundary();
        app.UseStatusCodePages(ApiProblems.WriteStatusCodePageAsync);
        app.UseLoopbackHostGuard();
        app.UseRouting();

        if (order == GuardOrder.BodyLimitFirst)
        {
            app.UseRequestBodyLimit();
            app.UseJsonContentTypeGuard();
        }
        else
        {
            app.UseJsonContentTypeGuard();
            app.UseRequestBodyLimit();
        }

        app.MapFormEndpoints();
        app.MapRecordEndpoints();
        app.MapCheckCodeEndpoints();

        // Typed as Func so MapPost takes the route-handler overload: a method group returning Task<IResult> would also
        // convert to RequestDelegate, which discards the result.
        Func<HttpContext, Task<IResult>> probe = ReadProbeBodyAsync;
        app.MapPost(GuardedProbePath, probe).RequireJsonBody();
        app.MapPost(UnguardedProbePath, probe);
        app.UseClientApp();
        return app;
    }

    private static HttpClient CreateSocketClient(WebApplication app)
    {
        IServerAddressesFeature? feature = app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>();
        Assert.NotNull(feature);

        var address = new Uri(Assert.Single(feature.Addresses), UriKind.Absolute);
        Assert.Equal(LoopbackHost, address.Host);
        Assert.NotEqual(0, address.Port);

        var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false };
        return new HttpClient(handler, disposeHandler: true) { BaseAddress = address };
    }

    private static async Task StopAndDisposeAsync(WebApplication app, bool started)
    {
        try
        {
            if (started)
            {
                await app.StopAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            await app.DisposeAsync().ConfigureAwait(false);
        }
    }

    // The limit is captured before the body is touched, so it is the one in force when the request reached the probe;
    // Kestrel makes the feature read-only once reading starts.
    private static async Task<IResult> ReadProbeBodyAsync(HttpContext context)
    {
        long? limit = context.Features.Get<IHttpMaxRequestBodySizeFeature>()?.MaxRequestBodySize;

        var buffer = new byte[ProbeBufferSize];
        long total = 0;
        int read;
        while ((read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted).ConfigureAwait(false)) > 0)
        {
            total += read;
        }

        return TypedResults.Ok(new ProbeResponse(total, limit));
    }

    private sealed record ProbeResponse(
        [property: JsonPropertyName(ProbeBytesProperty)] long Bytes,
        [property: JsonPropertyName(ProbeMaxRequestBodySizeProperty)] long? MaxRequestBodySize);
}

/// <summary>
/// The shared real-Kestrel host of <see cref="MediaTypeTests"/>: a <see cref="GuardPipelineHost"/> over a
/// <see cref="FakeDataStore"/> holding <see cref="FakeForms.TwoPage"/> with one seeded record.
/// </summary>
public sealed class MediaTypeHostFixture : IAsyncLifetime
{
    private GuardPipelineHost? _host;

    /// <summary>Creates the store and seeds the record; the host starts in <see cref="InitializeAsync"/>.</summary>
    public MediaTypeHostFixture()
    {
        Store = new FakeDataStore(FakeForms.TwoPage);
        RecordId = Store.SeedRecord(FakeForms.TwoPageViewId);
    }

    /// <summary>The store the host serves.</summary>
    public FakeDataStore Store { get; }

    /// <summary>The <c>GlobalRecordId</c> of the seeded record, the target of every <c>PUT</c>.</summary>
    public string RecordId { get; }

    /// <summary>The started host.</summary>
    /// <exception cref="InvalidOperationException">The host has not been started.</exception>
    internal GuardPipelineHost Host => _host ?? throw new InvalidOperationException("The host has not been started.");

    /// <inheritdoc />
    public async Task InitializeAsync() =>
        _host = await GuardPipelineHost.StartAsync(GuardHostKind.Kestrel, Store);

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }
    }
}

/// <summary>
/// The media-type rule and the request-level 400s of the four JSON body endpoints on real Kestrel (AAP 0.3.4.7,
/// 0.7.6): <c>POST</c> records, <c>PUT</c> record, <c>POST</c> search and <c>POST</c> field-event.
/// </summary>
/// <remarks>
/// <para>
/// Only <c>application/json</c>, case-insensitive, with no parameter or exactly one <c>charset=utf-8</c> (quoted or
/// not), reaches a handler, and each such request gets that handler's exact success. Every other media type, a
/// missing <c>Content-Type</c>, <c>+json</c> types, another or empty charset, and any extra or repeated parameter is
/// the fixed 415 ProblemDetails, with or without a body and ahead of the empty-body 400, and nothing is written.
/// Malformed JSON and an empty body are two distinct request-level 400s. Outside the body endpoints a route miss
/// stays 404 and a method mismatch 405.
/// </para>
/// <para>
/// The class needs no database: it carries no <c>Integration</c> trait and joins no database collection. Each
/// <c>Content-Type</c> is added without validation, so it reaches the server exactly as written.
/// </para>
/// </remarks>
public sealed class MediaTypeTests : IClassFixture<MediaTypeHostFixture>
{
    private const string ContentTypeHeader = "Content-Type";

    private const string JsonMediaType = "application/json";

    private const string QuotedUtf8JsonMediaType = "application/json; charset=\"utf-8\"";

    private const string CreateBody = """{"values":{"LastName":"x"}}""";

    private const string SearchBody = """{"criteria":[{"field":"LastName","value":"x"}]}""";

    private const string FieldEventBody = """{"event":"FieldAfter","fieldName":"LastName","values":{"LastName":"x"}}""";

    private const string MalformedBody = """{"values":""";

    private const string EmptyFieldEventEnvelope =
        """{"enabled":[],"disabled":[],"cleared":[],"assigned":{},"dialogs":[],"diagnostics":[]}""";

    private const string SubmittedText = "x";

    private const string RecordPathFormat = "/api/forms/{0}/records/{1}";

    private static readonly BodyEndpoint[] AllEndpoints =
        [BodyEndpoint.Create, BodyEndpoint.Update, BodyEndpoint.Search, BodyEndpoint.FieldEvent];

    private readonly MediaTypeHostFixture _fixture;

    /// <summary>Creates the test class over the shared host.</summary>
    /// <param name="fixture">The shared real-Kestrel host.</param>
    public MediaTypeTests(MediaTypeHostFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>The four endpoints that take a JSON body.</summary>
    public enum BodyEndpoint
    {
        /// <summary><c>POST /api/forms/{viewId}/records</c>.</summary>
        Create,

        /// <summary><c>PUT /api/forms/{viewId}/records/{globalRecordId}</c>.</summary>
        Update,

        /// <summary><c>POST /api/forms/{viewId}/records/search</c>.</summary>
        Search,

        /// <summary><c>POST /api/forms/{viewId}/checkcode/field-event</c>.</summary>
        FieldEvent,
    }

    /// <summary>The body a case sends.</summary>
    public enum BodyShape
    {
        /// <summary>The endpoint's valid JSON fixture.</summary>
        Valid,

        /// <summary>No bytes, sent with <c>Content-Length: 0</c>.</summary>
        Empty,

        /// <summary>Truncated JSON.</summary>
        Malformed,
    }

    /// <summary>Each endpoint with each non-JSON or missing media type, with its valid body and with an empty one.</summary>
    public static TheoryData<BodyEndpoint, string?, BodyShape> UnsupportedMediaTypeCases
    {
        get
        {
            string?[] mediaTypes =
            [
                "text/plain",
                "application/merge-patch+json",
                "application/vnd.x+json",
                "application/problem+json",
                null,
            ];

            var data = new TheoryData<BodyEndpoint, string?, BodyShape>();
            foreach (BodyEndpoint endpoint in AllEndpoints)
            {
                foreach (string? mediaType in mediaTypes)
                {
                    data.Add(endpoint, mediaType, BodyShape.Valid);
                    data.Add(endpoint, mediaType, BodyShape.Empty);
                }
            }

            return data;
        }
    }

    /// <summary>Each endpoint with <c>application/json</c> carrying a parameter list the rule rejects.</summary>
    public static TheoryData<BodyEndpoint, string> RejectedParameterCases => ForEachEndpoint(
        "application/json; charset=utf-16",
        "application/json; charset=utf8",
        "application/json; charset=latin1",
        "application/json; charset=\"utf-16\"",
        "application/json; charset=",
        "application/json; foo=bar",
        "application/json; charset=utf-8; foo=bar",
        "application/json; charset=utf-8; charset=utf-8",
        "application/json; charset=utf-8; charset=utf-16");

    /// <summary>Each endpoint with each spelling of <c>application/json</c> the rule accepts.</summary>
    public static TheoryData<BodyEndpoint, string> AcceptedMediaTypeCases => ForEachEndpoint(
        "application/json",
        "application/json; charset=utf-8",
        "APPLICATION/JSON",
        "Application/Json; Charset=UTF-8",
        QuotedUtf8JsonMediaType,
        "application/json;charset=utf-8");

    /// <summary>Each endpoint with a malformed body and with an empty one.</summary>
    public static TheoryData<BodyEndpoint, BodyShape> RequestLevelBodyCases
    {
        get
        {
            var data = new TheoryData<BodyEndpoint, BodyShape>();
            foreach (BodyEndpoint endpoint in AllEndpoints)
            {
                data.Add(endpoint, BodyShape.Malformed);
                data.Add(endpoint, BodyShape.Empty);
            }

            return data;
        }
    }

    private HttpClient Client => _fixture.Host.Client;

    // AAP 0.7.6: text/plain, +json types and a missing Content-Type are a 415 with or without a body; the media type is
    // checked before the body, so an empty body still gets the 415, never the 400 (AAP 0.3.4.7).
    [Theory]
    [MemberData(nameof(UnsupportedMediaTypeCases))]
    public async Task NonJsonMediaTypeIsUnsupportedWithOrWithoutBody(
        BodyEndpoint endpoint,
        string? contentType,
        BodyShape shape)
    {
        int writesBefore = _fixture.Store.WriteCalls.Count;

        using HttpRequestMessage request = CreateRequest(endpoint, shape, contentType);
        using HttpResponseMessage response = await Client.SendAsync(request);

        await AssertProblemAsync(
            response,
            HttpStatusCode.UnsupportedMediaType,
            ApiProblems.UnsupportedMediaTypeDetail);
        Assert.Equal(writesBefore, _fixture.Store.WriteCalls.Count);
    }

    // Only one charset parameter, valued utf-8, is allowed: another charset, the misspelling utf8 (an unknown encoding
    // to binding, which would fail with a 500), an empty charset, any other parameter and a repeated parameter are a
    // 415 even with a valid body, and no write reaches the store.
    [Theory]
    [MemberData(nameof(RejectedParameterCases))]
    public async Task JsonWithAnyOtherParameterListIsUnsupported(BodyEndpoint endpoint, string contentType)
    {
        int writesBefore = _fixture.Store.WriteCalls.Count;

        using HttpRequestMessage request = CreateRequest(endpoint, BodyShape.Valid, contentType);
        using HttpResponseMessage response = await Client.SendAsync(request);

        await AssertProblemAsync(
            response,
            HttpStatusCode.UnsupportedMediaType,
            ApiProblems.UnsupportedMediaTypeDetail);
        Assert.Equal(writesBefore, _fixture.Store.WriteCalls.Count);
    }

    // Every accepted spelling reaches the handler and gets its exact success, so a 404 or 500 cannot pass as "not 415".
    [Theory]
    [MemberData(nameof(AcceptedMediaTypeCases))]
    public async Task JsonMediaTypeReachesTheHandler(BodyEndpoint endpoint, string contentType)
    {
        int writesBefore = _fixture.Store.WriteCalls.Count;

        using HttpRequestMessage request = CreateRequest(endpoint, BodyShape.Valid, contentType);
        using HttpResponseMessage response = await Client.SendAsync(request);

        HttpStatusCode expected = endpoint == BodyEndpoint.Create ? HttpStatusCode.Created : HttpStatusCode.OK;
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(JsonMediaType, response.Content.Headers.ContentType?.MediaType);
        JsonElement body = await ApiTestClient.ReadJsonElementAsync(response);

        switch (endpoint)
        {
            case BodyEndpoint.Create:
                string createdId = AssertRecordWithSubmittedText(body);
                Assert.Equal($"{CreatePath()}/{createdId}", response.Headers.Location?.OriginalString);
                Assert.Equal(
                    FieldValue.FromText(SubmittedText),
                    Assert.IsType<FakeRecordState>(_fixture.Store.GetRecord(FakeForms.TwoPageViewId, createdId))
                        .Values["LastName"]);
                Assert.Equal(writesBefore + 1, _fixture.Store.WriteCalls.Count);
                break;

            case BodyEndpoint.Update:
                Assert.Equal(_fixture.RecordId, AssertRecordWithSubmittedText(body));
                Assert.Equal(writesBefore + 1, _fixture.Store.WriteCalls.Count);
                break;

            case BodyEndpoint.Search:
                Assert.Equal(
                    new[] { "items", "total", "truncated" },
                    body.EnumerateObject().Select(member => member.Name));
                Assert.Equal(JsonValueKind.Array, body.GetProperty("items").ValueKind);
                Assert.Equal(JsonValueKind.Number, body.GetProperty("total").ValueKind);
                Assert.Contains(body.GetProperty("truncated").ValueKind, new[] { JsonValueKind.True, JsonValueKind.False });
                Assert.Equal(writesBefore, _fixture.Store.WriteCalls.Count);
                break;

            case BodyEndpoint.FieldEvent:
                using (JsonDocument envelope = JsonDocument.Parse(EmptyFieldEventEnvelope))
                {
                    Assert.True(JsonElement.DeepEquals(envelope.RootElement, body), "Not the empty field-event envelope.");
                }

                Assert.Equal(writesBefore, _fixture.Store.WriteCalls.Count);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(endpoint), endpoint, "Not a body endpoint.");
        }
    }

    // AAP 0.3.4.2: with application/json, malformed JSON and an empty body are two different request-level 400s, each
    // a plain ProblemDetails with no errors map, and neither writes.
    [Theory]
    [MemberData(nameof(RequestLevelBodyCases))]
    public async Task MalformedAndEmptyJsonBodiesAreDistinctRequestLevel400s(BodyEndpoint endpoint, BodyShape shape)
    {
        Assert.NotEqual(ApiProblems.MalformedJsonDetail, ApiProblems.RequestBodyRequiredDetail);
        string expectedDetail = shape switch
        {
            BodyShape.Malformed => ApiProblems.MalformedJsonDetail,
            BodyShape.Empty => ApiProblems.RequestBodyRequiredDetail,
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "Not a request-level 400 body."),
        };
        int writesBefore = _fixture.Store.WriteCalls.Count;

        using HttpRequestMessage request = CreateRequest(endpoint, shape, JsonMediaType);
        HttpContent content = Assert.IsAssignableFrom<HttpContent>(request.Content);
        Assert.Equal(shape == BodyShape.Empty, content.Headers.ContentLength == 0);
        using HttpResponseMessage response = await Client.SendAsync(request);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, expectedDetail);
        Assert.Equal(writesBefore, _fixture.Store.WriteCalls.Count);
    }

    // AAP 0.7.6: outside the four body endpoints the guard never answers, so a route miss stays 404 and a method the
    // record route does not map stays 405, each the framework's ProblemDetails, whatever the media type.
    [Theory]
    [InlineData("POST", "/api/unknown", HttpStatusCode.NotFound, ApiProblems.ResourceNotFoundDetail)]
    [InlineData("PATCH", RecordPathFormat, HttpStatusCode.MethodNotAllowed, ApiProblems.MethodNotAllowedDetail)]
    public async Task OutsideTheBodyEndpointsRoutingKeepsItsStatus(
        string method,
        string pathFormat,
        HttpStatusCode expected,
        string expectedDetail)
    {
        string path = string.Format(
            CultureInfo.InvariantCulture,
            pathFormat,
            FakeForms.TwoPageViewId,
            Uri.EscapeDataString(_fixture.RecordId));

        using HttpRequestMessage request = CreateRequest(new HttpMethod(method), path, "hello"u8.ToArray(), "text/plain");
        using HttpResponseMessage response = await Client.SendAsync(request);

        await AssertProblemAsync(response, expected, expectedDetail);
    }

    // AAP 0.3.4.7: the guard acts only on an endpoint that carries JsonBodyMetadata. The probes differ in that marker
    // alone, and neither declares Accepts metadata, so routing never answers for them.
    [Theory]
    [InlineData(GuardPipelineHost.GuardedProbePath, "text/plain", HttpStatusCode.UnsupportedMediaType)]
    [InlineData(GuardPipelineHost.GuardedProbePath, null, HttpStatusCode.UnsupportedMediaType)]
    [InlineData(GuardPipelineHost.GuardedProbePath, JsonMediaType, HttpStatusCode.OK)]
    [InlineData(GuardPipelineHost.UnguardedProbePath, "text/plain", HttpStatusCode.OK)]
    [InlineData(GuardPipelineHost.UnguardedProbePath, null, HttpStatusCode.OK)]
    public async Task GuardActsOnlyOnEndpointsMarkedForJsonBodies(
        string path,
        string? contentType,
        HttpStatusCode expected)
    {
        byte[] body = Encoding.UTF8.GetBytes(CreateBody);

        using HttpRequestMessage request = CreateRequest(HttpMethod.Post, path, body, contentType);
        using HttpResponseMessage response = await Client.SendAsync(request);

        if (expected == HttpStatusCode.UnsupportedMediaType)
        {
            await AssertProblemAsync(response, expected, ApiProblems.UnsupportedMediaTypeDetail);
            return;
        }

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(JsonMediaType, response.Content.Headers.ContentType?.MediaType);
        JsonElement probe = await ApiTestClient.ReadJsonElementAsync(response);
        Assert.Equal(body.LongLength, probe.GetProperty(GuardPipelineHost.ProbeBytesProperty).GetInt64());

        // A body with Content-Length keeps Kestrel's configured limit on either probe.
        Assert.Equal(
            _fixture.Host.Services.GetRequiredService<ApiOptions>().MaxRequestBodySize,
            probe.GetProperty(GuardPipelineHost.ProbeMaxRequestBodySizeProperty).GetInt64());
    }

    // The same composition on TestServer gives the same 415 and the same handler success, quoted charset included, so
    // either host can carry the guard tests that need it.
    [Fact]
    public async Task TestServerHostAppliesTheSameMediaTypeRule()
    {
        var store = new FakeDataStore(FakeForms.TwoPage);
        await using GuardPipelineHost host = await GuardPipelineHost.StartAsync(GuardHostKind.TestServer, store);
        Assert.Equal(GuardHostKind.TestServer, host.Kind);
        byte[] body = Encoding.UTF8.GetBytes(CreateBody);

        using (HttpRequestMessage rejected = CreateRequest(HttpMethod.Post, CreatePath(), body, "text/plain"))
        using (HttpResponseMessage response = await host.Client.SendAsync(rejected))
        {
            await AssertProblemAsync(
                response,
                HttpStatusCode.UnsupportedMediaType,
                ApiProblems.UnsupportedMediaTypeDetail);
        }

        Assert.Empty(store.WriteCalls);

        using (HttpRequestMessage accepted = CreateRequest(HttpMethod.Post, CreatePath(), body, JsonMediaType))
        using (HttpResponseMessage response = await host.Client.SendAsync(accepted))
        {
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            AssertRecordWithSubmittedText(await ApiTestClient.ReadJsonElementAsync(response));
        }

        Assert.Single(store.WriteCalls);

        using (HttpRequestMessage quoted = CreateRequest(HttpMethod.Post, CreatePath(), body, QuotedUtf8JsonMediaType))
        using (HttpResponseMessage response = await host.Client.SendAsync(quoted))
        {
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            AssertRecordWithSubmittedText(await ApiTestClient.ReadJsonElementAsync(response));
        }

        Assert.Equal(2, store.WriteCalls.Count);
    }

    private static TheoryData<BodyEndpoint, string> ForEachEndpoint(params string[] contentTypes)
    {
        var data = new TheoryData<BodyEndpoint, string>();
        foreach (BodyEndpoint endpoint in AllEndpoints)
        {
            foreach (string contentType in contentTypes)
            {
                data.Add(endpoint, contentType);
            }
        }

        return data;
    }

    private static string CreatePath() =>
        string.Create(CultureInfo.InvariantCulture, $"/api/forms/{FakeForms.TwoPageViewId}/records");

    // The raw header is added without validation, so parameter edge cases reach the server exactly as written; a null
    // content type sends no Content-Type at all.
    private static HttpRequestMessage CreateRequest(HttpMethod method, string path, byte[] body, string? contentType)
    {
        var content = new ByteArrayContent(body);
        if (contentType is null)
        {
            Assert.False(content.Headers.Contains(ContentTypeHeader));
        }
        else
        {
            Assert.True(content.Headers.TryAddWithoutValidation(ContentTypeHeader, contentType));
        }

        return new HttpRequestMessage(method, path) { Content = content };
    }

    // The fixed body of every error on these routes: the status's ProblemDetails as application/problem+json, with
    // the expected detail, a traceId and no errors map.
    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string detail)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(ApiProblems.ProblemContentType, response.Content.Headers.ContentType?.MediaType);
        JsonElement body = ApiTestClient.AssertProblemDetails(
            await ApiTestClient.ReadJsonElementAsync(response),
            (int)status);
        Assert.Equal(detail, body.GetProperty("detail").GetString());
        Assert.False(body.TryGetProperty("errors", out _), "A request-level problem carries no errors member.");
    }

    // A Record DTO whose LastName is the submitted text; returns its globalRecordId.
    private static string AssertRecordWithSubmittedText(JsonElement body)
    {
        Assert.Equal(JsonValueKind.Object, body.ValueKind);
        string? id = body.GetProperty("globalRecordId").GetString();
        Assert.False(string.IsNullOrEmpty(id));
        Assert.Equal(SubmittedText, body.GetProperty("values").GetProperty("LastName").GetString());
        return id;
    }

    private HttpRequestMessage CreateRequest(BodyEndpoint endpoint, BodyShape shape, string? contentType)
    {
        (HttpMethod method, string path, string validBody) = endpoint switch
        {
            BodyEndpoint.Create => (HttpMethod.Post, CreatePath(), CreateBody),
            BodyEndpoint.Update => (HttpMethod.Put, RecordPath(), CreateBody),
            BodyEndpoint.Search => (HttpMethod.Post, $"{CreatePath()}/search", SearchBody),
            BodyEndpoint.FieldEvent => (
                HttpMethod.Post,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"/api/forms/{FakeForms.TwoPageViewId}/checkcode/field-event"),
                FieldEventBody),
            _ => throw new ArgumentOutOfRangeException(nameof(endpoint), endpoint, "Not a body endpoint."),
        };

        byte[] body = shape switch
        {
            BodyShape.Valid => Encoding.UTF8.GetBytes(validBody),
            BodyShape.Empty => [],
            BodyShape.Malformed => Encoding.UTF8.GetBytes(MalformedBody),
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "Not a body shape."),
        };

        return CreateRequest(method, path, body, contentType);
    }

    private string RecordPath() => string.Format(
        CultureInfo.InvariantCulture,
        RecordPathFormat,
        FakeForms.TwoPageViewId,
        Uri.EscapeDataString(_fixture.RecordId));
}
