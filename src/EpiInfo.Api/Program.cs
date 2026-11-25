// Composition root of the EpiInfo web API. It builds the ASP.NET Core host, refuses every listen setting that does
// not bind 127.0.0.1 exactly and answers every request whose Host is not 127.0.0.1 or localhost with a 400
// (LoopbackBindingGuard), loads the Kestrel endpoints with configuration reload off and applies the configured
// request body limit, logs through the JSON console provider only, warns at startup when the database connection
// string is not configured, turns every empty error response into the API's fixed ProblemDetails (ApiProblems), and
// serves the built web client with its index.html fallback (ClientAppHosting).
using EpiInfo.Api.Errors;
using EpiInfo.Api.Hosting;

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

// Read from the built configuration on first use, so settings a test host applies during Build are honoured.
builder.Services.AddSingleton(services => ApiOptions.FromConfiguration(services.GetRequiredService<IConfiguration>()));

// Bodies framework writers produce through the ProblemDetails service, such as the Development exception page, keep
// only the API's fixed texts and traceId.
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = ApiProblems.Customize);

var app = builder.Build();

// Checked again on the built configuration, which also holds sources applied during Build, such as test-host settings.
LoopbackBindingGuard.Validate(app.Configuration);

if (!ApiOptions.FromConfiguration(app.Configuration).HasConnectionString)
{
    // The key only, never a value: a connection string carries the database password.
    app.Logger.LogWarning(
        "Configuration key {ConfigurationKey} is not set; database-backed endpoints and /health return 503.",
        ApiOptions.ConnectionStringKey);
}

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

// Last, after every endpoint is mapped: the client fallback has the lowest route order and never shadows a route.
app.UseClientApp();

app.Run();

/// <summary>The API entry point, public so <c>WebApplicationFactory&lt;Program&gt;</c> can host it in tests.</summary>
public partial class Program;
