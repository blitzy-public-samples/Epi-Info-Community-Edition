// Composition root of the EpiInfo web API. It builds the ASP.NET Core host, refuses every listen setting that does
// not bind 127.0.0.1 exactly and answers every request whose Host is not 127.0.0.1 or localhost with a 400
// (LoopbackBindingGuard), loads the Kestrel endpoints with configuration reload off and applies the configured
// request body limit, logs through the JSON console provider only, and warns at startup when the database connection
// string is not configured. It registers the wire JSON settings (JsonSetup), the OpenAPI document v1 with its schema
// transformers, the SQL Server data store (DataStoreRegistration), the check-code engine, the save stamp, the form,
// record and field-event services, and the database health check. Its pipeline answers every failure through the
// API's error contract (ApiExceptionHandler and its exception boundary), turns every empty error response into the
// API's fixed ProblemDetails (ApiProblems), enforces the body limit and the JSON media type on the body endpoints
// after routing, maps /openapi/v1.json, the /api form, record and field-event routes and /health, and serves the
// built web client with its index.html fallback (ClientAppHosting) last.
using EpiInfo.Api.Endpoints;
using EpiInfo.Api.Errors;
using EpiInfo.Api.Health;
using EpiInfo.Api.Hosting;
using EpiInfo.Api.Json;
using EpiInfo.Api.OpenApi;
using EpiInfo.Api.Services;
using EpiInfo.CheckCode;

var builder = WebApplication.CreateBuilder(args);

// Every listen setting is checked before the host is built, so a rejected one never opens a socket.
LoopbackBindingGuard.Validate(builder.Configuration);

builder.WebHost.ConfigureKestrel((context, options) =>
{
    // Reload stays off, Kestrel loads the guard's frozen copy of the verified Kestrel section, and the host's urls and
    // ports settings read values the guard pinned to that same copy, so a changed appsettings.json can neither alter
    // the first bind nor rebind the running server later. An endpoint with a blank Url is dropped rather than
    // rejected by Kestrel, and a server with no listen address fails here instead of binding Kestrel's dual-stack
    // localhost default.
    options.Configure(LoopbackBindingGuard.FreezeListenSettings(context.Configuration), reloadOnChange: false);

    // Loading the Kestrel section binds endpoints, not limits, so the body cap is applied from configuration here.
    options.Limits.MaxRequestBodySize = ApiOptions.FromConfiguration(context.Configuration).MaxRequestBodySize;
});

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();

// Bodies framework writers produce through the ProblemDetails service, such as the Development exception page, keep
// only the API's fixed texts and traceId.
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = ApiProblems.Customize);
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);

// One set of wire JSON settings for binding, results, ProblemDetails and the OpenAPI schemas.
builder.Services.AddApiJson();

// The default document v1, served at /openapi/v1.json and written by build-time generation. The transformers publish
// the wire schemas rather than the binding types: ScalarSchemaTransformer the bodies and responses, in all three of its
// roles, and ParameterSchemaTransformer the coerced query and route parameters.
builder.Services.AddOpenApi(options =>
{
    ScalarSchemaTransformer.Register(options);
    options.AddOperationTransformer<ParameterSchemaTransformer>();
});

// ApiOptions, DatabaseOptions, the SQL command observer, the SQL Server store and the clock, each registered once and
// read from the built configuration on first use, so settings a test host applies during Build are honoured.
builder.Services.AddEpiInfoDataStore();

builder.Services.AddSingleton<ICheckCodeEngine, CheckCodeEngine>();
builder.Services.AddSingleton<SaveStampProvider>();
builder.Services.AddScoped<FormService>();
builder.Services.AddScoped<RecordService>();
builder.Services.AddScoped<FieldEventService>();

builder.Services.AddHealthChecks().AddCheck<SqlServerHealthCheck>(SqlServerHealthCheck.Name);

var app = builder.Build();

// Checked again on the built configuration, which also holds sources applied during Build, such as test-host settings.
LoopbackBindingGuard.Validate(app.Configuration);

if (!DataStoreRegistration.HasConnectionString(app.Configuration))
{
    // The key only, never a value: a connection string carries the database password.
    app.Logger.LogWarning(
        "Configuration key {ConfigurationKey} is not set; database-backed endpoints and /health return 503.",
        ApiOptions.ConnectionStringKey);
}

app.UseExceptionHandler();

// Inside the exception handler, which would otherwise settle a client abort or a failure after the response started
// itself, unredacted; outside status-code pages, which would otherwise write a body for an aborted request.
app.UseApiExceptionBoundary();

// Empty error responses produced inside the pipeline (route miss 404, 405, routing 415) become the fixed
// ProblemDetails of their status. The API's own writer is passed because the default status-code page goes through
// the ProblemDetails service, whose default writer declines an Accept header that admits neither application/json nor
// application/problem+json, and then falls back to a text/plain body without traceId.
app.UseStatusCodePages(ApiProblems.WriteStatusCodePageAsync);

// Before routing, the endpoints, OpenAPI and the static client, so a request whose Host is not 127.0.0.1 or localhost
// is answered 400 before anything is served. Exception handling and status-code pages, when registered, come before
// it; nothing that serves content does.
app.UseLoopbackHostGuard();

app.UseRouting();

// After routing, so each guard reads the matched endpoint's metadata, and before the endpoints, so nothing is bound
// before the body limit's counting stream is in place and the media type is checked. Their order changes no status.
app.UseRequestBodyLimit();
app.UseJsonContentTypeGuard();

app.MapOpenApi();
app.MapFormEndpoints();
app.MapRecordEndpoints();
app.MapCheckCodeEndpoints();
app.MapHealthEndpoints();

// Last, after every endpoint is mapped: the client fallback has the lowest route order and never shadows a route.
app.UseClientApp();

app.Run();

/// <summary>The API entry point, public so <c>WebApplicationFactory&lt;Program&gt;</c> can host it in tests.</summary>
public partial class Program;
