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
using EpiInfo.Domain.Forms;
using EpiInfo.Domain.Metadata;
using EpiInfo.Domain.Storage;
using EpiInfo.Domain.Values;
using EpiInfo.Tests.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Matching;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
/// <see cref="TimeProvider.System"/>, <see cref="SaveStampProvider"/>, the scoped <see cref="FormService"/>,
/// <see cref="RecordService"/> and <see cref="FieldEventService"/>, and the routing policy
/// <see cref="RouteConstraintMatcherPolicy"/>.
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
        services.TryAddEnumerable(ServiceDescriptor.Singleton<MatcherPolicy, RouteConstraintMatcherPolicy>());

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
/// The shared real-Kestrel hosts of <see cref="MediaTypeTests"/>, each reached over a socket.
/// </summary>
/// <remarks>
/// <para>
/// The API itself is <c>src/EpiInfo.Api/Program.cs</c> hosted by <see cref="KestrelApiFactory"/> over
/// <see cref="Store"/>, a <see cref="FakeDataStore"/> holding <see cref="FakeForms.TwoPage"/> with one seeded record,
/// so the four body endpoints are proved through the composition root's own pipeline: dropping the media-type guard
/// from <c>Program.cs</c> lets the <c>+json</c> types through and fails the 415 cases.
/// </para>
/// <para>
/// <see cref="GuardHost"/> is a <see cref="GuardPipelineHost"/> over a store of its own, used only for the probe routes
/// that differ in <see cref="JsonBodyMetadata"/> alone, which the API does not map.
/// </para>
/// <para>
/// The form's view id, its writable text field and the seeded record id come from <see cref="FakeForms.TwoPage"/> and
/// the store, never from literals. Every valid body carries a marker of its own, <see cref="TestDatabase.NewMarker"/>,
/// as its text, and an accepted case writes or finds only a record of its own: a create adds one, an update changes
/// one seeded for it with another marker, and a search finds the one seeded for it with its text. No case writes the
/// seeded <see cref="RecordId"/>, the target of every rejected <c>PUT</c>, so the 415 cases assert it unchanged; the
/// 400 cases, whose bodies carry no text, assert that nothing is written. Each test reads the store's write count
/// before sending, and xUnit runs the tests of one class one at a time.
/// </para>
/// </remarks>
public sealed class MediaTypeFixture : IAsyncLifetime
{
    private readonly KestrelApiFactory _factory;

    private HttpClient? _client;

    private GuardPipelineHost? _guardHost;

    /// <summary>
    /// Creates the store, seeds the record and creates the API factory; both hosts start in
    /// <see cref="InitializeAsync"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <see cref="FakeForms.TwoPage"/> has no writable text field.
    /// </exception>
    public MediaTypeFixture()
    {
        FakeForm form = FakeForms.TwoPage;
        ViewId = form.Metadata.ViewId;
        TextField = WritableTextField(form.Metadata);
        Store = new FakeDataStore(form);
        RecordId = Store.SeedRecord(ViewId);
        _factory = new KestrelApiFactory(ApiFactoryOptions.ForFake(Store));
    }

    /// <summary>The store the API serves.</summary>
    public FakeDataStore Store { get; }

    /// <summary>The view id of <see cref="FakeForms.TwoPage"/>.</summary>
    public int ViewId { get; }

    /// <summary>
    /// The <c>GlobalRecordId</c> of the seeded record, the target of every rejected <c>PUT</c> and of the
    /// <c>PATCH</c>; no case writes it.
    /// </summary>
    public string RecordId { get; }

    /// <summary>
    /// A writable, page-bound <c>text</c> data field of the form with no length limit or pattern, so a marker passes
    /// validation.
    /// </summary>
    public string TextField { get; }

    /// <summary>
    /// The API's client, sending real HTTP over a socket to the <c>127.0.0.1</c> address Kestrel bound.
    /// </summary>
    /// <exception cref="InvalidOperationException">The API has not been started.</exception>
    public HttpClient Client => _client ?? throw new InvalidOperationException("The API has not been started.");

    /// <summary>The probe host.</summary>
    /// <exception cref="InvalidOperationException">The probe host has not been started.</exception>
    internal GuardPipelineHost GuardHost =>
        _guardHost ?? throw new InvalidOperationException("The probe host has not been started.");

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        // Starts Kestrel; the loopback guard has checked every listen setting before any socket opened.
        _client = _factory.CreateKestrelClient();
        _guardHost = await GuardPipelineHost.StartAsync(GuardHostKind.Kestrel, new FakeDataStore(FakeForms.TwoPage));
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        try
        {
            if (_guardHost is not null)
            {
                await _guardHost.DisposeAsync();
            }
        }
        finally
        {
            // Disposes the client with the host and frees the port.
            await _factory.DisposeAsync();
        }
    }

    private static string WritableTextField(FormMetadata metadata) =>
        metadata.Fields.FirstOrDefault(field =>
            field.PageId is not null
            && !field.IsReadOnly
            && field.MaxLength is not > 0
            && string.IsNullOrEmpty(field.Pattern)
            && FieldTypeMapper.Map(field) is (FieldType.Text, false, _))?.Name
        ?? throw new InvalidOperationException(
            "The form has no writable text field without a length limit or pattern.");
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
/// stays 404 and a method mismatch 405, and a form id the <c>int</c> constraint rejects is the route miss whatever
/// the method or media type.
/// </para>
/// <para>
/// Every body-endpoint case runs on the API's own composition root through <see cref="KestrelApiFactory"/>, where the
/// framework's binding alone would accept the <c>+json</c> types. The class needs no database: it carries no
/// <c>Integration</c> trait and joins no database collection. Each <c>Content-Type</c> is added without validation, so
/// it reaches the server exactly as written.
/// </para>
/// </remarks>
public sealed class MediaTypeTests : IClassFixture<MediaTypeFixture>
{
    private const string ContentTypeHeader = "Content-Type";

    private const string JsonMediaType = "application/json";

    private const string QuotedUtf8JsonMediaType = "application/json; charset=\"utf-8\"";

    private const string MalformedBody = """{"values":""";

    private const string EmptyFieldEventEnvelope =
        """{"enabled":[],"disabled":[],"cleared":[],"assigned":{},"dialogs":[],"diagnostics":[]}""";

    private const string RecordPathFormat = "/api/forms/{0}/records/{1}";

    // The wire contract written out here rather than read from ApiProblems, so a changed or swapped product text fails
    // these cases instead of moving the expectation with it.
    private const string ProblemMediaType = "application/problem+json";

    private const string ExpectedUnsupportedMediaTypeDetail = "unsupported media type; use application/json";

    private const string ExpectedMalformedJsonDetail = "malformed JSON";

    private const string ExpectedRequestBodyRequiredDetail = "request body is required";

    private const string ExpectedResourceNotFoundDetail = "the resource does not exist";

    private const string ExpectedMethodNotAllowedDetail = "method not allowed";

    private static readonly BodyEndpoint[] AllEndpoints =
        [BodyEndpoint.Create, BodyEndpoint.Update, BodyEndpoint.Search, BodyEndpoint.FieldEvent];

    private readonly MediaTypeFixture _fixture;

    /// <summary>Creates the test class over the shared hosts.</summary>
    /// <param name="fixture">The shared real-Kestrel hosts.</param>
    public MediaTypeTests(MediaTypeFixture fixture)
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

    /// <summary>
    /// Each form, record and field-event route with a form id the <c>int</c> constraint rejects, under every media type
    /// that decides a matched route's status, and each body route with an integer form id that names no form.
    /// Formats take the form id as <c>{0}</c> and the seeded record id as <c>{1}</c>.
    /// </summary>
    public static TheoryData<string, string, string?, HttpStatusCode, string> FormIdConstraintCases
    {
        get
        {
            (string Method, string PathFormat)[] nonIntegerFormIdRoutes =
            [
                ("POST", "/api/forms/abc/records/search"),
                ("POST", "/api/forms/abc/records"),
                ("PUT", "/api/forms/abc/records/{1}"),
                ("POST", "/api/forms/abc/checkcode/field-event"),
                ("POST", "/api/forms/1.5/records/search"),
                ("POST", "/api/forms/99999999999/records/search"),
                ("PATCH", "/api/forms/abc/records/{1}"),
                ("POST", "/api/forms/abc"),
                ("POST", "/api/forms/abc/records/count"),
                ("POST", "/api/forms/abc/records/search/"),
                ("POST", "/API/Forms/abc/Records/Search"),
                ("PATCH", "/api/forms/abc/records/{1}/"),
            ];
            string?[] mediaTypes = ["text/plain", "application/merge-patch+json", null, JsonMediaType];

            var data = new TheoryData<string, string, string?, HttpStatusCode, string>();
            foreach ((string method, string pathFormat) in nonIntegerFormIdRoutes)
            {
                foreach (string? mediaType in mediaTypes)
                {
                    data.Add(method, pathFormat, mediaType, HttpStatusCode.NotFound, ExpectedResourceNotFoundDetail);
                }
            }

            (string Method, string PathFormat)[] unknownIntegerFormIdRoutes =
            [
                ("POST", "/api/forms/-1/records/search"),
                ("POST", "/api/forms/-1/records"),
                ("PUT", "/api/forms/-1/records/{1}"),
                ("POST", "/api/forms/-1/checkcode/field-event"),
            ];
            foreach ((string method, string pathFormat) in unknownIntegerFormIdRoutes)
            {
                data.Add(
                    method,
                    pathFormat,
                    "text/plain",
                    HttpStatusCode.UnsupportedMediaType,
                    ExpectedUnsupportedMediaTypeDetail);
            }

            return data;
        }
    }

    private HttpClient Client => _fixture.Client;

    // AAP 0.7.6: text/plain, +json types and a missing Content-Type are a 415 with or without a body; the media type is
    // checked before the body, so an empty body still gets the 415, never the 400 (AAP 0.3.4.7).
    [Theory]
    [MemberData(nameof(UnsupportedMediaTypeCases))]
    public async Task NonJsonMediaTypeIsUnsupportedWithOrWithoutBody(
        BodyEndpoint endpoint,
        string? contentType,
        BodyShape shape)
    {
        FakeRecordState seeded = SeededRecord();
        int writesBefore = _fixture.Store.WriteCalls.Count;

        using HttpRequestMessage request = CreateRequest(endpoint, shape, contentType);
        using HttpResponseMessage response = await Client.SendAsync(request);

        await AssertProblemAsync(
            response,
            HttpStatusCode.UnsupportedMediaType,
            ExpectedUnsupportedMediaTypeDetail);
        Assert.Equal(writesBefore, _fixture.Store.WriteCalls.Count);
        AssertSeededRecordUnchanged(seeded);
    }

    // Only one charset parameter, valued utf-8, is allowed: another charset, the misspelling utf8 (an unknown encoding
    // to binding, which would fail with a 500), an empty charset, any other parameter and a repeated parameter are a
    // 415 even with a valid body, and no write reaches the store.
    [Theory]
    [MemberData(nameof(RejectedParameterCases))]
    public async Task JsonWithAnyOtherParameterListIsUnsupported(BodyEndpoint endpoint, string contentType)
    {
        FakeRecordState seeded = SeededRecord();
        int writesBefore = _fixture.Store.WriteCalls.Count;

        using HttpRequestMessage request = CreateRequest(endpoint, BodyShape.Valid, contentType);
        using HttpResponseMessage response = await Client.SendAsync(request);

        await AssertProblemAsync(
            response,
            HttpStatusCode.UnsupportedMediaType,
            ExpectedUnsupportedMediaTypeDetail);
        Assert.Equal(writesBefore, _fixture.Store.WriteCalls.Count);
        AssertSeededRecordUnchanged(seeded);
    }

    // Every accepted spelling reaches the handler and gets its exact success, so a 404 or 500 cannot pass as "not 415".
    // Each row sends a marker of its own and writes or finds only a record of its own, so its result can come from its
    // own request alone: the record it created holding its text, the record seeded for it with another marker now
    // holding its text, or exactly the one record seeded with its text.
    [Theory]
    [MemberData(nameof(AcceptedMediaTypeCases))]
    public async Task JsonMediaTypeReachesTheHandler(BodyEndpoint endpoint, string contentType)
    {
        string text = TestDatabase.NewMarker();
        string recordId = endpoint switch
        {
            BodyEndpoint.Update => SeedRecordWithText(TestDatabase.NewMarker()),
            BodyEndpoint.Search => SeedRecordWithText(text),
            BodyEndpoint.Create or BodyEndpoint.FieldEvent => _fixture.RecordId,
            _ => throw new ArgumentOutOfRangeException(nameof(endpoint), endpoint, "Not a body endpoint."),
        };
        int writesBefore = _fixture.Store.WriteCalls.Count;

        using HttpRequestMessage request = CreateRequest(endpoint, BodyShape.Valid, contentType, text, recordId);
        using HttpResponseMessage response = await Client.SendAsync(request);

        HttpStatusCode expected = endpoint == BodyEndpoint.Create ? HttpStatusCode.Created : HttpStatusCode.OK;
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(JsonMediaType, response.Content.Headers.ContentType?.MediaType);
        JsonElement body = await ApiTestClient.ReadJsonElementAsync(response);

        switch (endpoint)
        {
            case BodyEndpoint.Create:
                string createdId = AssertRecordWithSubmittedText(body, text);
                Assert.Equal($"{RecordsPath()}/{createdId}", response.Headers.Location?.OriginalString);
                Assert.Equal(FieldValue.FromText(text), StoredText(createdId));
                Assert.Equal(writesBefore + 1, _fixture.Store.WriteCalls.Count);
                break;

            case BodyEndpoint.Update:
                Assert.Equal(recordId, AssertRecordWithSubmittedText(body, text));
                Assert.Equal(FieldValue.FromText(text), StoredText(recordId));
                Assert.Equal(writesBefore + 1, _fixture.Store.WriteCalls.Count);
                break;

            case BodyEndpoint.Search:
                Assert.Equal(
                    new[] { "items", "total", "truncated" },
                    body.EnumerateObject().Select(member => member.Name));
                Assert.Equal(
                    new[] { recordId },
                    body.GetProperty("items")
                        .EnumerateArray()
                        .Select(item => item.GetProperty("globalRecordId").GetString()));
                Assert.Equal(JsonValueKind.Number, body.GetProperty("total").ValueKind);
                Assert.Equal(1, body.GetProperty("total").GetInt32());
                Assert.Equal(JsonValueKind.False, body.GetProperty("truncated").ValueKind);
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
        string expectedDetail = shape switch
        {
            BodyShape.Malformed => ExpectedMalformedJsonDetail,
            BodyShape.Empty => ExpectedRequestBodyRequiredDetail,
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
    [InlineData("POST", "/api/unknown", HttpStatusCode.NotFound, ExpectedResourceNotFoundDetail)]
    [InlineData("PATCH", RecordPathFormat, HttpStatusCode.MethodNotAllowed, ExpectedMethodNotAllowedDetail)]
    public async Task OutsideTheBodyEndpointsRoutingKeepsItsStatus(
        string method,
        string pathFormat,
        HttpStatusCode expected,
        string expectedDetail)
    {
        string path = string.Format(
            CultureInfo.InvariantCulture,
            pathFormat,
            _fixture.ViewId,
            Uri.EscapeDataString(_fixture.RecordId));

        using HttpRequestMessage request = CreateRequest(new HttpMethod(method), path, "hello"u8.ToArray(), "text/plain");
        using HttpResponseMessage response = await Client.SendAsync(request);

        await AssertProblemAsync(response, expected, expectedDetail);
    }

    // AAP 0.3.4.1 and 0.3.4.7: a form id the int constraint rejects matches no route, so it is the route miss 404
    // whatever the method or media type, never the 405 or 415 that routing's method and media-type checks give a
    // matched route. An integer form id satisfies the constraint even when no form has it, so the body routes still
    // answer their 415 first. The body is valid JSON for a create, so only routing can turn it away, and nothing is
    // written.
    [Theory]
    [MemberData(nameof(FormIdConstraintCases))]
    public async Task FormIdConstraintDecidesBeforeTheMethodAndMediaType(
        string method,
        string pathFormat,
        string? contentType,
        HttpStatusCode expected,
        string expectedDetail)
    {
        FakeRecordState seeded = SeededRecord();
        int writesBefore = _fixture.Store.WriteCalls.Count;
        string path = string.Format(
            CultureInfo.InvariantCulture,
            pathFormat,
            _fixture.ViewId,
            Uri.EscapeDataString(_fixture.RecordId));
        byte[] body = Encoding.UTF8.GetBytes(ValidBody(BodyEndpoint.Create, TestDatabase.NewMarker()));

        using HttpRequestMessage request = CreateRequest(new HttpMethod(method), path, body, contentType);
        using HttpResponseMessage response = await Client.SendAsync(request);

        await AssertProblemAsync(response, expected, expectedDetail);
        Assert.Equal(writesBefore, _fixture.Store.WriteCalls.Count);
        AssertSeededRecordUnchanged(seeded);
    }

    // AAP 0.3.4.7: the guard acts only on an endpoint that carries JsonBodyMetadata. The probes differ in that marker
    // alone, and neither declares Accepts metadata, so routing never answers for them. The API maps no such pair, so
    // they run on the probe host, which composes the same guards in the same order.
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
        GuardPipelineHost host = _fixture.GuardHost;
        byte[] body = Encoding.UTF8.GetBytes(ValidBody(BodyEndpoint.Create, TestDatabase.NewMarker()));

        using HttpRequestMessage request = CreateRequest(HttpMethod.Post, path, body, contentType);
        using HttpResponseMessage response = await host.Client.SendAsync(request);

        if (expected == HttpStatusCode.UnsupportedMediaType)
        {
            await AssertProblemAsync(response, expected, ExpectedUnsupportedMediaTypeDetail);
            return;
        }

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(JsonMediaType, response.Content.Headers.ContentType?.MediaType);
        JsonElement probe = await ApiTestClient.ReadJsonElementAsync(response);
        Assert.Equal(body.LongLength, probe.GetProperty(GuardPipelineHost.ProbeBytesProperty).GetInt64());

        // A body with Content-Length keeps Kestrel's configured limit on either probe.
        Assert.Equal(
            host.Services.GetRequiredService<ApiOptions>().MaxRequestBodySize,
            probe.GetProperty(GuardPipelineHost.ProbeMaxRequestBodySizeProperty).GetInt64());
    }

    // The probe host's composition on TestServer gives the same 415 and the same handler success, quoted charset
    // included, so either of its servers can carry the guard tests that need it.
    [Fact]
    public async Task TestServerHostAppliesTheSameMediaTypeRule()
    {
        var store = new FakeDataStore(FakeForms.TwoPage);
        await using GuardPipelineHost host = await GuardPipelineHost.StartAsync(GuardHostKind.TestServer, store);
        Assert.Equal(GuardHostKind.TestServer, host.Kind);
        string text = TestDatabase.NewMarker();
        byte[] body = Encoding.UTF8.GetBytes(ValidBody(BodyEndpoint.Create, text));

        using (HttpRequestMessage rejected = CreateRequest(HttpMethod.Post, RecordsPath(), body, "text/plain"))
        using (HttpResponseMessage response = await host.Client.SendAsync(rejected))
        {
            await AssertProblemAsync(
                response,
                HttpStatusCode.UnsupportedMediaType,
                ExpectedUnsupportedMediaTypeDetail);
        }

        Assert.Empty(store.WriteCalls);

        using (HttpRequestMessage accepted = CreateRequest(HttpMethod.Post, RecordsPath(), body, JsonMediaType))
        using (HttpResponseMessage response = await host.Client.SendAsync(accepted))
        {
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            AssertRecordWithSubmittedText(await ApiTestClient.ReadJsonElementAsync(response), text);
        }

        Assert.Single(store.WriteCalls);

        using (HttpRequestMessage quoted = CreateRequest(HttpMethod.Post, RecordsPath(), body, QuotedUtf8JsonMediaType))
        using (HttpResponseMessage response = await host.Client.SendAsync(quoted))
        {
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            AssertRecordWithSubmittedText(await ApiTestClient.ReadJsonElementAsync(response), text);
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
        Assert.Equal(ProblemMediaType, response.Content.Headers.ContentType?.MediaType);
        JsonElement body = ApiTestClient.AssertProblemDetails(
            await ApiTestClient.ReadJsonElementAsync(response),
            (int)status);
        Assert.Equal(detail, body.GetProperty("detail").GetString());
        Assert.False(body.TryGetProperty("errors", out _), "A request-level problem carries no errors member.");
    }

    // The record's state now equals its state before: the same status, values, page rows and stamps.
    private static void AssertSameRecordState(FakeRecordState before, FakeRecordState after)
    {
        Assert.Equal(before.RecStatus, after.RecStatus);
        Assert.Equal(before.Values, after.Values);
        Assert.True(before.PresentPageIds.SetEquals(after.PresentPageIds), "The record's page rows changed.");
        Assert.Equal(before.FirstSave, after.FirstSave);
        Assert.Equal(before.LastSave, after.LastSave);
    }

    // A Record DTO whose text field holds the submitted text; returns its globalRecordId.
    private string AssertRecordWithSubmittedText(JsonElement body, string submittedText)
    {
        Assert.Equal(JsonValueKind.Object, body.ValueKind);
        string? id = body.GetProperty("globalRecordId").GetString();
        Assert.False(string.IsNullOrEmpty(id));
        Assert.Equal(submittedText, body.GetProperty("values").GetProperty(_fixture.TextField).GetString());
        return id;
    }

    // The seeded record as the store holds it now; reading it logs no store call.
    private FakeRecordState SeededRecord() =>
        Assert.IsType<FakeRecordState>(_fixture.Store.GetRecord(_fixture.ViewId, _fixture.RecordId));

    // A rejected request left the seeded record, its target, as it was.
    private void AssertSeededRecordUnchanged(FakeRecordState before) => AssertSameRecordState(before, SeededRecord());

    // Adds an active record with every page row whose text field holds text, as a seeded row; no call is logged.
    private string SeedRecordWithText(string text) => _fixture.Store.SeedRecord(
        _fixture.ViewId,
        new Dictionary<string, FieldValue> { [_fixture.TextField] = FieldValue.FromText(text) });

    // The value the store holds in the text field of the record.
    private FieldValue StoredText(string recordId) =>
        Assert.IsType<FakeRecordState>(_fixture.Store.GetRecord(_fixture.ViewId, recordId)).Values[_fixture.TextField];

    // The method, path and valid JSON body of each body endpoint, built from the fixture's form and text field, the
    // text the body submits or searches for, and the record an update targets, which the other endpoints ignore.
    // Update sends the create body to that record.
    private (HttpMethod Method, string Path, string ValidBody) Describe(
        BodyEndpoint endpoint,
        string text,
        string recordId) =>
        endpoint switch
        {
            BodyEndpoint.Create => (HttpMethod.Post, RecordsPath(), ValidBody(endpoint, text)),
            BodyEndpoint.Update => (HttpMethod.Put, RecordPath(recordId), ValidBody(endpoint, text)),
            BodyEndpoint.Search => (HttpMethod.Post, $"{RecordsPath()}/search", ValidBody(endpoint, text)),
            BodyEndpoint.FieldEvent => (
                HttpMethod.Post,
                string.Create(CultureInfo.InvariantCulture, $"/api/forms/{_fixture.ViewId}/checkcode/field-event"),
                ValidBody(endpoint, text)),
            _ => throw new ArgumentOutOfRangeException(nameof(endpoint), endpoint, "Not a body endpoint."),
        };

    // The field name and the text are JSON-encoded, so the bodies stay valid JSON whatever the form names its field.
    private string ValidBody(BodyEndpoint endpoint, string text)
    {
        string field = JsonEncodedText.Encode(_fixture.TextField).Value;
        string value = JsonEncodedText.Encode(text).Value;
        string values = $$"""{"{{field}}":"{{value}}"}""";
        return endpoint switch
        {
            BodyEndpoint.Create or BodyEndpoint.Update => $$"""{"values":{{values}}}""",
            BodyEndpoint.Search => $$"""{"criteria":[{"field":"{{field}}","value":"{{value}}"}]}""",
            BodyEndpoint.FieldEvent => $$"""{"event":"FieldAfter","fieldName":"{{field}}","values":{{values}}}""",
            _ => throw new ArgumentOutOfRangeException(nameof(endpoint), endpoint, "Not a body endpoint."),
        };
    }

    // A rejected case's request: a marker of its own as the text, and the seeded record as the update's target.
    private HttpRequestMessage CreateRequest(BodyEndpoint endpoint, BodyShape shape, string? contentType) =>
        CreateRequest(endpoint, shape, contentType, TestDatabase.NewMarker(), _fixture.RecordId);

    private HttpRequestMessage CreateRequest(
        BodyEndpoint endpoint,
        BodyShape shape,
        string? contentType,
        string text,
        string recordId)
    {
        (HttpMethod method, string path, string validBody) = Describe(endpoint, text, recordId);

        byte[] body = shape switch
        {
            BodyShape.Valid => Encoding.UTF8.GetBytes(validBody),
            BodyShape.Empty => [],
            BodyShape.Malformed => Encoding.UTF8.GetBytes(MalformedBody),
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "Not a body shape."),
        };

        return CreateRequest(method, path, body, contentType);
    }

    private string RecordsPath() =>
        string.Create(CultureInfo.InvariantCulture, $"/api/forms/{_fixture.ViewId}/records");

    private string RecordPath(string recordId) => string.Format(
        CultureInfo.InvariantCulture,
        RecordPathFormat,
        _fixture.ViewId,
        Uri.EscapeDataString(recordId));
}
