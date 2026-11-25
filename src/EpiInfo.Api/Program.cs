// Composition root of the EpiInfo web API. It builds the ASP.NET Core host, refuses every listen setting that does
// not bind 127.0.0.1 exactly (LoopbackBindingGuard), loads the Kestrel endpoints with configuration reload off and
// applies the configured request body limit, logs through the JSON console provider only, warns at startup when the
// database connection string is not configured, and serves the built web client with its index.html fallback
// (ClientAppHosting).
using EpiInfo.Api.Hosting;

var builder = WebApplication.CreateBuilder(args);

// Every listen setting is checked before the host is built, so a rejected one never opens a socket.
LoopbackBindingGuard.Validate(builder.Configuration);

builder.WebHost.ConfigureKestrel((context, options) =>
{
    // Reload off: a changed appsettings.json cannot rebind the running server to an endpoint the guard never saw.
    options.Configure(context.Configuration.GetSection("Kestrel"), reloadOnChange: false);

    // Loading the Kestrel section binds endpoints, not limits, so the body cap is applied from configuration here.
    options.Limits.MaxRequestBodySize = ApiOptions.FromConfiguration(context.Configuration).MaxRequestBodySize;
});

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();

// Read from the built configuration on first use, so settings a test host applies during Build are honoured.
builder.Services.AddSingleton(services => ApiOptions.FromConfiguration(services.GetRequiredService<IConfiguration>()));

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

app.UseRouting();

// Last, after every endpoint is mapped: the client fallback has the lowest route order and never shadows a route.
app.UseClientApp();

app.Run();

/// <summary>The API entry point, public so <c>WebApplicationFactory&lt;Program&gt;</c> can host it in tests.</summary>
public partial class Program;
