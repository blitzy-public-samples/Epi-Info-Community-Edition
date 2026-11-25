using System.Globalization;
using Microsoft.AspNetCore.Routing.Constraints;
using Microsoft.AspNetCore.Routing.Matching;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

namespace EpiInfo.Api.Hosting;

/// <summary>
/// Serves the built web client from <c>Web:ClientPath</c> in every environment, with an <c>index.html</c> fallback
/// for client routes. The client is same-origin, so no CORS policy is registered.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="UseClientApp"/> is called after <c>UseRouting</c> and after every endpoint is mapped. The static-file
/// middleware skips any request whose matched endpoint has a request delegate, so an API route is never shadowed by
/// a file, and a request that matches no endpoint can still be answered from the client folder.
/// </para>
/// <para>
/// The fallback answers <c>GET</c> and <c>HEAD</c> only, never matches <c>/api/**</c>, <c>/openapi/**</c> or
/// <c>/health</c>, never matches a path whose last segment looks like a file name, and has the lowest route order,
/// so every concrete route wins and a missing asset or an unknown API route stays a 404.
/// </para>
/// </remarks>
public static partial class ClientAppHosting
{
    /// <summary>The logger category of the startup warning.</summary>
    public const string LoggerCategory = "EpiInfo.Api.Hosting.ClientAppHosting";

    /// <summary>The page every client route is answered with.</summary>
    public const string IndexFileName = "index.html";

    private const string FallbackDisplayName = "Client fallback";

    private const string FirstSegmentParameter = "first";

    private const string RestParameter = "rest";

    private const string ClientRoutePattern = "{" + FirstSegmentParameter + "}/{*" + RestParameter + "}";

    // Fixed text with no arguments: the resolved folder is a local path, which the log redaction policy keeps out of
    // every log line. The configuration key is a compile-time constant, not a value.
    private const string ClientMissingMessage =
        $"Client application not found at the folder configured by {ApiOptions.ClientPathKey} ({IndexFileName} " +
        "missing); only /api, /health and /openapi are served. Run npm run build in web/.";

    private static readonly string[] FallbackMethods = [HttpMethods.Get, HttpMethods.Head];

    /// <summary>
    /// Serves the client folder that <see cref="ApiOptions.ClientPath"/> names, resolved against the content root,
    /// and maps the <c>index.html</c> fallback. When the folder or its <c>index.html</c> is missing, logs one warning
    /// and registers nothing, so <c>/api</c>, <c>/health</c> and <c>/openapi</c> keep serving.
    /// </summary>
    /// <param name="app">The application, after routing and every endpoint have been added.</param>
    /// <returns>The same application, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> is <see langword="null"/>.</exception>
    public static WebApplication UseClientApp(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        ApiOptions options = app.Services.GetRequiredService<ApiOptions>();
        string folder = ResolveClientPath(app.Environment.ContentRootPath, options.ClientPath);

        if (!Directory.Exists(folder) || !File.Exists(Path.Combine(folder, IndexFileName)))
        {
            // A warning, not a startup failure: dotnet test runs before npm run build in the documented sequence.
            LogClientMissing(app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(LoggerCategory));
            return app;
        }

        var fileProvider = new PhysicalFileProvider(folder);
        app.Lifetime.ApplicationStopped.Register(fileProvider.Dispose);

        var staticOptions = new StaticFileOptions
        {
            FileProvider = fileProvider,
            OnPrepareResponse = MarkIndexUncached,
        };

        app.UseStaticFiles(staticOptions);
        MapClientFallback(app, staticOptions);
        return app;
    }

    /// <summary>
    /// Resolves the configured client folder to a full path. A relative <paramref name="clientPath"/> is taken from
    /// <paramref name="contentRootPath"/>; an absolute one, such as the Docker image's <c>/app/client</c>, is used as
    /// it stands.
    /// </summary>
    /// <param name="contentRootPath">The application's content root, <c>src/EpiInfo.Api</c> in a checkout.</param>
    /// <param name="clientPath">The configured <c>Web:ClientPath</c>.</param>
    /// <returns>The normalised full path of the client folder.</returns>
    /// <exception cref="ArgumentException">Either argument is <see langword="null"/>, empty or whitespace.</exception>
    public static string ResolveClientPath(string contentRootPath, string clientPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientPath);

        // Path.Combine returns an absolute second argument unchanged, so a configured absolute folder wins.
        return Path.GetFullPath(Path.Combine(contentRootPath, clientPath));
    }

    [LoggerMessage(
        EventId = 1,
        EventName = "ClientAppMissing",
        Level = LogLevel.Warning,
        Message = ClientMissingMessage)]
    private static partial void LogClientMissing(ILogger logger);

    // The page names the current hashed bundles, so browsers must revalidate it; the bundles themselves are
    // content-addressed and keep the default caching.
    private static void MarkIndexUncached(StaticFileResponseContext context)
    {
        if (string.Equals(context.File.Name, IndexFileName, StringComparison.OrdinalIgnoreCase))
        {
            context.Context.Response.Headers.CacheControl = "no-cache";
        }
    }

    // Built the way MapFallbackToFile builds its endpoint, except for the route. Its single "{*path:nonfile}" pattern
    // with a GET/HEAD restriction would sit in the route-table node of every path, /api/** included, and the
    // HTTP-method policy gives such a node a 405 endpoint before any constraint runs, so a POST to /api/unknown would
    // be a 405 instead of the 404 an unknown API route must return. The route table only prunes a route from the
    // api and openapi nodes through a required simple parameter, never through a catch-all or an optional
    // parameter, so client routes take two endpoints: the root, and a required first segment plus a catch-all rest.
    private static void MapClientFallback(WebApplication app, StaticFileOptions staticOptions)
    {
        RequestDelegate serveIndex = BuildServeIndex(app, staticOptions);

        ConfigureFallback(app.Map("/", serveIndex));

        RoutePattern clientRoutes = RoutePatternFactory.Parse(
            ClientRoutePattern,
            defaults: null,
            parameterPolicies: new RouteValueDictionary { [FirstSegmentParameter] = new ClientRouteConstraint() });
        ConfigureFallback(app.Map(clientRoutes, serveIndex));
    }

    private static RequestDelegate BuildServeIndex(WebApplication app, StaticFileOptions staticOptions)
    {
        IApplicationBuilder fallbackApp = ((IEndpointRouteBuilder)app).CreateApplicationBuilder();
        fallbackApp.Use(next => context =>
        {
            context.Request.Path = "/" + IndexFileName;

            // Clearing the endpoint lets the static-file middleware serve the file: it skips requests whose matched
            // endpoint has a delegate, which this fallback endpoint has.
            context.SetEndpoint(null);
            return next(context);
        });
        fallbackApp.UseStaticFiles(staticOptions);
        return fallbackApp.Build();
    }

    private static void ConfigureFallback(IEndpointConventionBuilder endpoint)
    {
        endpoint
            .WithMetadata(new HttpMethodMetadata(FallbackMethods))
            .WithDisplayName(FallbackDisplayName)
            // Registered only when the client is built, so it must stay out of the OpenAPI document: otherwise the
            // committed document would depend on whether web/dist exists and the staleness check would flap.
            .ExcludeFromDescription()
            .Add(builder => ((RouteEndpointBuilder)builder).Order = int.MaxValue);
    }

    /// <summary>
    /// The policy of the first-segment parameter of <see cref="ClientRoutePattern"/>. It matches every client route
    /// outside <c>/api/**</c>, <c>/openapi/**</c> and <c>/health</c>, compared case-insensitively as routing compares
    /// literals, whose last segment does not look like a file name, so a missing asset stays a 404.
    /// </summary>
    private sealed class ClientRouteConstraint : IRouteConstraint, IParameterLiteralNodeMatchingPolicy
    {
        private const string HealthPath = "health";

        private static readonly string[] ServerPrefixes = ["api", "openapi"];

        // The framework's own "nonfile" rule, applied to whichever parameter holds the last path segment.
        private static readonly NonFileNameRouteConstraint NonFileName = new();

        public bool Match(
            HttpContext? httpContext,
            IRouter? route,
            string routeKey,
            RouteValueDictionary values,
            RouteDirection routeDirection)
        {
            // Constraints run once every segment is captured, so the catch-all rest is already in the values.
            string first = ReadValue(values, routeKey);
            if (IsServerPrefix(first))
            {
                return false;
            }

            if (ReadValue(values, RestParameter).Trim('/').Length > 0)
            {
                return NonFileName.Match(httpContext, route, RestParameter, values, routeDirection);
            }

            return !string.Equals(first, HealthPath, StringComparison.OrdinalIgnoreCase)
                && NonFileName.Match(httpContext, route, routeKey, values, routeDirection);
        }

        // Asked for each literal first segment of the other routes, such as api, health and openapi. Returning false
        // keeps the fallback out of that literal's whole subtree in the route table. /health is left to Match,
        // because the health literal also begins client routes such as /health/x.
        public bool MatchesLiteral(string parameterName, string literal) => !IsServerPrefix(literal);

        private static bool IsServerPrefix(string segment) =>
            ServerPrefixes.Contains(segment, StringComparer.OrdinalIgnoreCase);

        private static string ReadValue(RouteValueDictionary values, string key) =>
            values.TryGetValue(key, out object? value)
                ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
                : string.Empty;
    }
}
