using System.Globalization;
using System.Runtime.Versioning;
using System.Security;
using Microsoft.AspNetCore.Http.Headers;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.Routing.Constraints;
using Microsoft.AspNetCore.Routing.Matching;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using Microsoft.Win32.SafeHandles;

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
/// <para>
/// Assets and the fallback's <c>index.html</c> are served from one in-memory copy of the client folder, which
/// <see cref="UseClientApp"/> reads once, at startup. Requests never reach the file system, so nothing done to the
/// folder afterwards, a link added included, changes what is served, and the API is restarted after a rebuild. The
/// copy leaves out a file under <c>api/</c> or <c>openapi/</c>, compared case-insensitively, and a root file named
/// <c>health</c>, so a request under a server path stays the route miss; the files a <see cref="PhysicalFileProvider"/>
/// excludes by default, whose name starts with a dot or which are hidden or system files; and every symbolic link,
/// junction or other reparse point below the folder, with everything beneath it, so nothing outside the folder is
/// served. The folder itself may be a link.
/// </para>
/// <para>
/// Where response compression is registered, each answer describes the representation it stands for. A file of a
/// type the compression middleware codes carries <c>Vary: Accept-Encoding</c> on its 200, HEAD and 304, coded or not;
/// an answer sent in a coding, or a 304 or HEAD whose 200 would be, carries the weak form of the file's entity tag;
/// and an identity answer and a range answer, which is never coded, keep the strong tag. <c>If-None-Match</c> is
/// compared weakly, so a weak tag still revalidates, while <c>If-Match</c> and <c>If-Range</c> compare strongly.
/// </para>
/// <para>
/// Each file is read through one handle and confirmed to be the entry the listing found. On Linux the handle's own
/// path must be the file's real path. On every system, once the read ends, neither the entry nor a folder above it may
/// have become a link, and the entry's length and write time must be the handle's. A folder that changes while it is
/// read, that cannot be confirmed, or that exceeds <see cref="MaxClientEntries"/> or <see cref="MaxClientBytes"/> is
/// not served at all. On other systems the comparison finds a concurrent rebuild, not a writer who restores a file's
/// length and write time, so the folder must be writable only by the account that runs the API or by an
/// administrator, as the Docker image's root-owned <c>/app/client</c> is.
/// </para>
/// <para>
/// Outside Windows part of that precondition is enforced: a client folder whose mode grants other users write
/// permission, judged on the folder it resolves to when it is a link, is not served at all, and neither is one holding
/// a folder the read enters or a file the copy keeps whose mode grants it. Each mode is read from the entry the listing
/// found. Group write is allowed, because where each user has a private group the default umask leaves every build
/// output group-writable, and neither the folders above the client folder nor the owner of an entry is checked. On
/// Windows, whose access control lists the mode does not describe, the precondition stands as stated.
/// </para>
/// </remarks>
public static partial class ClientAppHosting
{
    /// <summary>The logger category of the startup warnings.</summary>
    public const string LoggerCategory = "EpiInfo.Api.Hosting.ClientAppHosting";

    /// <summary>The page every client route is answered with.</summary>
    public const string IndexFileName = "index.html";

    // Bounds on the copy read at startup, so a wrong or swollen folder can neither exhaust memory nor stall startup:
    // the built client is a few files, well under 1 MB. Every listed entry counts, links and left-out entries included.
    internal const int MaxClientEntries = 10_000;

    internal const long MaxClientBytes = 64L * 1024 * 1024;

    // The most symbolic links one path may pass through, as on Linux.
    private const int MaxLinkHops = 40;

    private const string FallbackDisplayName = "Client fallback";

    private const string FirstSegmentParameter = "first";

    private const string RestParameter = "rest";

    private const string ClientRoutePattern = "{" + FirstSegmentParameter + "}/{*" + RestParameter + "}";

    // The server's own paths: /api/**, /openapi/** and /health. The fallback route and asset serving both read this
    // one list, so a file in the client folder can never answer a request that belongs to the server.
    private const string HealthPath = "health";

    private static readonly string[] ServerPrefixes = ["api", "openapi"];

    // Fixed text with no arguments: the resolved folder is a local path, which the log redaction policy keeps out of
    // every log line. The configuration key is a compile-time constant, not a value.
    private const string ClientMissingMessage =
        $"Client application not found at the folder configured by {ApiOptions.ClientPathKey} ({IndexFileName} " +
        "missing); only /api, /health and /openapi are served. Run npm run build in web/.";

    // Fixed text with no arguments, for the same reason: the rejected value is a local path.
    private const string ClientPathInvalidMessage =
        $"The folder configured by {ApiOptions.ClientPathKey} is not a usable path; only /api, /health and /openapi " +
        "are served. Set it to the folder npm run build writes.";

    // Fixed text with no arguments, for the same reason. The copy is read once, so a restart is part of the remedy.
    private const string ClientUnconfirmedMessage =
        $"The client application in the folder configured by {ApiOptions.ClientPathKey} changed while it was read, " +
        "could not be confirmed or exceeds the size limits; only /api, /health and /openapi are served. Rebuild it " +
        "with npm run build in web/, then restart the API.";

    // Fixed text with no arguments, for the same reason: it names no folder, no entry and no file mode.
    private const string ClientUntrustedMessage =
        $"The client application in the folder configured by {ApiOptions.ClientPathKey} can be changed by every user " +
        "of this computer; only /api, /health and /openapi are served. Remove other users' write permission from the " +
        "folder and from every folder and file in it, then restart the API.";

    private static readonly string[] FallbackMethods = [HttpMethods.Get, HttpMethods.Head];

    private const string IndexCacheControl = "no-cache";

    private const string AssetCacheControl = "public, max-age=31536000, immutable";

    // Matched by whole segment, so a folder such as assets-old/ is not under it, and ignoring case, because where the
    // copy looks names up ignoring case /ASSETS/app.js is the same asset.
    private static readonly PathString AssetsPath = new("/assets");

    /// <summary>
    /// Reads the client folder that <see cref="ApiOptions.ClientPath"/> names, resolved against the content root,
    /// into memory once, serves that copy and maps the <c>index.html</c> fallback. When the folder or its
    /// <c>index.html</c> is missing, an <c>index.html</c> that is a link included, when the configured path is not a
    /// usable path, when the copy could not be confirmed or exceeds the limits, or when, outside Windows, other users
    /// can write to the folder or to a folder or file the copy keeps, logs one warning that names the configuration
    /// key but not its value, and registers nothing, so <c>/api</c>, <c>/health</c> and <c>/openapi</c> keep serving.
    /// </summary>
    /// <param name="app">The application, after routing and every endpoint have been added.</param>
    /// <returns>The same application, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> is <see langword="null"/>.</exception>
    public static WebApplication UseClientApp(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        ApiOptions options = app.Services.GetRequiredService<ApiOptions>();
        ILogger logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(LoggerCategory);

        // The client is optional, so a configured path the file system rejects leaves the API serving. Only the
        // failures of resolving a path and reading the folder are caught; the exception is not logged, because its
        // message carries the path.
        ClientFileProvider? fileProvider;
        ClientFolderRefusal refusal;
        try
        {
            fileProvider = ReadClientFolder(app.Environment.ContentRootPath, options.ClientPath, out refusal);
        }
        catch (ArgumentException)
        {
            // Path.GetFullPath rejects a null character, among other malformed paths.
            LogClientPathInvalid(logger);
            return app;
        }
        catch (NotSupportedException)
        {
            LogClientPathInvalid(logger);
            return app;
        }
        catch (IOException)
        {
            // A path too long for the platform, among others; a folder removed meanwhile is handled as missing.
            LogClientPathInvalid(logger);
            return app;
        }
        catch (UnauthorizedAccessException)
        {
            LogClientPathInvalid(logger);
            return app;
        }
        catch (SecurityException)
        {
            LogClientPathInvalid(logger);
            return app;
        }

        if (refusal == ClientFolderRefusal.Unconfirmed)
        {
            LogClientUnconfirmed(logger);
            return app;
        }

        if (refusal == ClientFolderRefusal.Untrusted)
        {
            LogClientUntrusted(logger);
            return app;
        }

        if (fileProvider is null)
        {
            // A warning, not a startup failure: dotnet test runs before npm run build in the documented sequence.
            LogClientMissing(logger);
            return app;
        }

        // Optional: a host that registers no response compression sends every file as it is, so its answers keep the
        // static-file defaults, a strong entity tag and no Vary, and If-None-Match keeps the middleware's comparison.
        IResponseCompressionProvider? compression = app.Services.GetService<IResponseCompressionProvider>();
        var staticOptions = new StaticFileOptions
        {
            FileProvider = fileProvider,
            OnPrepareResponse = compression is null
                ? SetCacheControl
                : context =>
                {
                    SetCacheControl(context);
                    DescribeRepresentation(context, compression);
                },
        };

        bool coded = compression is not null;
        UseClientFiles(app, staticOptions, coded);
        MapClientFallback(app, staticOptions, coded);
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
    /// <exception cref="ArgumentException">
    /// Either argument is <see langword="null"/>, empty or whitespace, or the combined path contains a null character.
    /// </exception>
    public static string ResolveClientPath(string contentRootPath, string clientPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientPath);

        // Path.Combine returns an absolute second argument unchanged, so a configured absolute folder wins.
        return Path.GetFullPath(Path.Combine(contentRootPath, clientPath));
    }

    /// <summary>
    /// Whether the file <paramref name="handle"/> has open is the file at <paramref name="realPath"/>. Linux names an
    /// open file's path at <c>/proc/self/fd</c> with every link resolved, so a handle opened through a link reports
    /// the link's target, never the path that was opened. Without <c>/proc</c> nothing is confirmed.
    /// </summary>
    /// <param name="handle">An open file handle.</param>
    /// <param name="realPath">
    /// The path the file must have, every link resolved, as <see cref="ResolveRealPath"/> gives it.
    /// </param>
    /// <returns><see langword="true"/> when the open file's path is exactly <paramref name="realPath"/>.</returns>
    [SupportedOSPlatform("linux")]
    internal static bool IsOpenedAt(SafeFileHandle handle, string realPath)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(realPath);

        // The reference keeps the descriptor from being closed and reused for another file while its path is read.
        bool added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            long descriptor = handle.DangerousGetHandle().ToInt64();
            string? opened = new FileInfo(
                string.Create(CultureInfo.InvariantCulture, $"/proc/self/fd/{descriptor}")).LinkTarget;
            return string.Equals(opened, realPath, StringComparison.Ordinal);
        }
        finally
        {
            if (added)
            {
                handle.DangerousRelease();
            }
        }
    }

    /// <summary>
    /// The full path of <paramref name="path"/> with the symbolic link of every component resolved, as Linux reports
    /// the path of an open file: each component in turn, so a <c>..</c> that follows a link climbs from the link's
    /// target, not from the link.
    /// </summary>
    /// <param name="path">The path to resolve; a relative one is taken from the current directory.</param>
    /// <returns>The resolved full path.</returns>
    /// <exception cref="IOException">The path passes through more than 40 links, as a link loop does.</exception>
    [SupportedOSPlatform("linux")]
    internal static string ResolveRealPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        // Not Path.GetFullPath, which would remove a ".." against the link before it instead of the link's target.
        var pending = new Stack<string>();
        PushSegments(pending, Path.IsPathRooted(path) ? path : Path.Join(Environment.CurrentDirectory, path));

        string resolved = "/";
        int hops = 0;
        while (pending.TryPop(out string? segment))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                resolved = Path.GetDirectoryName(resolved) ?? "/";
                continue;
            }

            string candidate = Path.Join(resolved, segment);
            string? target = new FileInfo(candidate).LinkTarget;
            if (target is null)
            {
                resolved = candidate;
                continue;
            }

            if (++hops > MaxLinkHops)
            {
                throw new IOException("A path passes through too many symbolic links.");
            }

            // A relative target continues from the folder holding the link, which is where resolution stands.
            if (Path.IsPathRooted(target))
            {
                resolved = "/";
            }

            PushSegments(pending, target);
        }

        return resolved;
    }

    private static void PushSegments(Stack<string> pending, string path)
    {
        string[] segments = path.Split('/');
        for (int i = segments.Length - 1; i >= 0; i--)
        {
            pending.Push(segments[i]);
        }
    }

    // Returns null when the folder or its index.html is missing, an index.html that is a link included, because the
    // copy leaves links out. Sets refusal, and returns null, when the copy exceeds the limits or a file in it could not
    // be confirmed, or when other users can write to the folder or to a folder or file the copy keeps.
    private static ClientFileProvider? ReadClientFolder(
        string contentRootPath,
        string clientPath,
        out ClientFolderRefusal refusal)
    {
        refusal = ClientFolderRefusal.None;
        string folder = ResolveClientPath(contentRootPath, clientPath);
        if (!Directory.Exists(folder))
        {
            return null;
        }

        ClientFolderReader reader;
        try
        {
            reader = new ClientFolderReader(folder);
            refusal = reader.Read();
            if (refusal != ClientFolderRefusal.None)
            {
                return null;
            }
        }
        catch (DirectoryNotFoundException) when (!Directory.Exists(folder))
        {
            // Removed between the existence check and the read: missing, not unusable.
            return null;
        }

        ClientFileProvider fileProvider = reader.CreateProvider();
        return fileProvider.GetFileInfo(IndexFileName).Exists ? fileProvider : null;
    }

    [LoggerMessage(
        EventId = 1,
        EventName = "ClientAppMissing",
        Level = LogLevel.Warning,
        Message = ClientMissingMessage)]
    private static partial void LogClientMissing(ILogger logger);

    [LoggerMessage(
        EventId = 2,
        EventName = "ClientAppPathInvalid",
        Level = LogLevel.Warning,
        Message = ClientPathInvalidMessage)]
    private static partial void LogClientPathInvalid(ILogger logger);

    [LoggerMessage(
        EventId = 3,
        EventName = "ClientAppUnconfirmed",
        Level = LogLevel.Warning,
        Message = ClientUnconfirmedMessage)]
    private static partial void LogClientUnconfirmed(ILogger logger);

    [LoggerMessage(
        EventId = 4,
        EventName = "ClientAppUntrusted",
        Level = LogLevel.Warning,
        Message = ClientUntrustedMessage)]
    private static partial void LogClientUntrusted(ILogger logger);

    // The page names the current hashed bundles, so browsers must revalidate it, under whichever path it is served:
    // the fallback answers every client route with it. Vite writes every file under assets/ with a hash of its content
    // in its name, so a changed file is a new URL: an asset, which holds no user data, may be kept by any cache for a
    // year and is not revalidated, a reload included. Only the answers that describe the file, 200, 206 and 304, get
    // that lifetime: the middleware also calls this for a 412 or a 416, which answer the request's conditional or Range
    // headers. Any other file keeps the static-file defaults, which set no Cache-Control.
    private static void SetCacheControl(StaticFileResponseContext context)
    {
        HttpResponse response = context.Context.Response;
        if (string.Equals(context.File.Name, IndexFileName, StringComparison.OrdinalIgnoreCase))
        {
            response.Headers.CacheControl = IndexCacheControl;
        }
        else if (response.StatusCode < StatusCodes.Status400BadRequest
                 && context.Context.Request.Path.StartsWithSegments(AssetsPath, StringComparison.OrdinalIgnoreCase))
        {
            response.Headers.CacheControl = AssetCacheControl;
        }
    }

    // A file of a type the compression middleware codes, which it decides on the Content-Type the static-file
    // middleware has set by now, is sent br or gzip to a client that accepts a coding and as it is to one that does
    // not. Every answer that describes it, 200, HEAD and 304, therefore varies by Accept-Encoding (RFC 9110, section
    // 12.5.5), the identity 200 included, so that no cache reuses it for a request of another Accept-Encoding, and a
    // 304 carries the Vary and the entity tag its 200 would have carried (section 15.4.5). The middleware adds Vary
    // only to an answer it is about to code, never to an identity answer or a 304, and adds none where the value is
    // already listed, so it is listed once.
    // The middleware also leaves the entity tag the static-file middleware set, which names the identity bytes. A
    // strong tag names one representation's exact bytes (section 8.8.3), so an answer sent in a coding, or a 304 or
    // HEAD whose 200 would be, gets the weak form of that tag: a tag taken from coded bytes then fails the strong
    // comparison of If-Range, whose Range is then ignored and the whole identity file sent, and of If-Match, which then
    // fails. Revalidation is unaffected, because If-None-Match compares weakly (CompareIfNoneMatchWeakly). A range
    // answer, 206, is never coded, because the middleware leaves an answer with a Content-Range alone, as
    // ShouldCompressResponse reports: its bytes are the identity bytes whatever the request accepts, so it keeps the
    // strong tag and gets no Vary. A 412 or a 416 describes no file and gets neither.
    private static void DescribeRepresentation(
        StaticFileResponseContext context,
        IResponseCompressionProvider compression)
    {
        HttpContext httpContext = context.Context;
        HttpResponse response = httpContext.Response;
        if (response.StatusCode >= StatusCodes.Status400BadRequest || !compression.ShouldCompressResponse(httpContext))
        {
            return;
        }

        // Compared as the middleware compares the values it finds, so neither adds a second Accept-Encoding.
        IHeaderDictionary headers = response.Headers;
        if (!headers.GetCommaSeparatedValues(HeaderNames.Vary)
                .Contains(HeaderNames.AcceptEncoding, StringComparer.OrdinalIgnoreCase))
        {
            headers.Append(HeaderNames.Vary, HeaderNames.AcceptEncoding);
        }

        // The middleware's own decision: it codes only for a request that names a coding and only where a provider is
        // chosen, so Accept-Encoding: identity, or a coding whose weight is 0, keeps the strong tag.
        if (compression.CheckRequestAcceptsCompression(httpContext)
            && compression.GetCompressionProvider(httpContext) is not null)
        {
            ResponseHeaders typed = response.GetTypedHeaders();
            if (typed.ETag is { IsWeak: false } etag)
            {
                typed.ETag = new EntityTagHeaderValue(etag.Tag, isWeak: true);
            }
        }
    }

    // RFC 9110 (section 13.1.2) requires If-None-Match to compare entity tags weakly, by their opaque tags alone, but
    // the static-file middleware compares them strongly, so the weak tag of a coded answer would never match the strong
    // tag it computes and each revalidation of a coded file would resend the whole file. Each weak tag of the
    // If-None-Match of a request the middleware may serve, a GET or HEAD with no endpoint to run, is therefore replaced
    // by the strong tag of the same opaque tag before the middleware reads it. Every tag the middleware computes is
    // strong, so its strong comparison then decides exactly as the weak comparison would. An If-None-Match holding no
    // weak tag is left as it is, and If-Match and If-Range keep the strong comparison the RFC requires of them.
    private static Task CompareIfNoneMatchWeakly(HttpContext context, RequestDelegate next)
    {
        HttpRequest request = context.Request;
        if (context.GetEndpoint()?.RequestDelegate is null
            && (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)))
        {
            // The middleware's own parse, so the tags rewritten are exactly the tags it compares.
            RequestHeaders typed = request.GetTypedHeaders();
            IList<EntityTagHeaderValue> tags = typed.IfNoneMatch;
            if (tags.Any(tag => tag.IsWeak))
            {
                typed.IfNoneMatch = tags.Select(tag => tag.IsWeak ? new EntityTagHeaderValue(tag.Tag) : tag).ToList();
            }
        }

        return next(context);
    }

    // Both serving paths, the root static-file middleware and the index.html fallback, serve through here, so both
    // decide a conditional request alike.
    private static void UseClientFiles(IApplicationBuilder builder, StaticFileOptions staticOptions, bool coded)
    {
        if (coded)
        {
            builder.Use(CompareIfNoneMatchWeakly);
        }

        builder.UseStaticFiles(staticOptions);
    }

    // Built the way MapFallbackToFile builds its endpoint, except for the route. Its single "{*path:nonfile}" pattern
    // with a GET/HEAD restriction would sit in the route-table node of every path, /api/** included, and nonfile
    // accepts api/unknown, so the HTTP-method policy would answer a POST to /api/unknown with a 405 instead of the 404
    // an unknown API route must return. The route table only prunes a route from the api and openapi nodes through a
    // required simple parameter, never through a catch-all or an optional parameter, so client routes take two
    // endpoints: the root, and a required first segment plus a catch-all rest.
    private static void MapClientFallback(WebApplication app, StaticFileOptions staticOptions, bool coded)
    {
        RequestDelegate serveIndex = BuildServeIndex(app, staticOptions, coded);

        ConfigureFallback(app.Map("/", serveIndex));

        RoutePattern clientRoutes = RoutePatternFactory.Parse(
            ClientRoutePattern,
            defaults: null,
            parameterPolicies: new RouteValueDictionary { [FirstSegmentParameter] = new ClientRouteConstraint() });
        ConfigureFallback(app.Map(clientRoutes, serveIndex));
    }

    private static RequestDelegate BuildServeIndex(WebApplication app, StaticFileOptions staticOptions, bool coded)
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
        UseClientFiles(fallbackApp, staticOptions, coded);
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

    // Case-insensitive, as routing compares literals.
    private static bool IsServerPrefix(string segment) =>
        ServerPrefixes.Contains(segment, StringComparer.OrdinalIgnoreCase);

    private static bool IsHealthPath(string path) =>
        string.Equals(path, HealthPath, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The policy of the first-segment parameter of <see cref="ClientRoutePattern"/>. It matches every client route
    /// outside <c>/api/**</c>, <c>/openapi/**</c> and <c>/health</c>, compared case-insensitively as routing compares
    /// literals, whose last segment does not look like a file name, so a missing asset stays a 404.
    /// </summary>
    private sealed class ClientRouteConstraint : IRouteConstraint, IParameterLiteralNodeMatchingPolicy
    {
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

            return !IsHealthPath(first) && NonFileName.Match(httpContext, route, routeKey, values, routeDirection);
        }

        // Asked for each literal first segment of the other routes, such as api, health and openapi. Returning false
        // keeps the fallback out of that literal's whole subtree in the route table. /health is left to Match,
        // because the health literal also begins client routes such as /health/x. The API's
        // RouteConstraintMatcherPolicy runs Match before the HTTP-method policy, so the fallback's GET and HEAD never
        // join the methods /health allows.
        public bool MatchesLiteral(string parameterName, string literal) => !IsServerPrefix(literal);

        private static string ReadValue(RouteValueDictionary values, string key) =>
            values.TryGetValue(key, out object? value)
                ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
                : string.Empty;
    }

    /// <summary>
    /// The one provider both serving paths read, the root static-file middleware and the <c>index.html</c> fallback:
    /// the in-memory copy <see cref="ClientFolderReader"/> took at startup. It never touches the file system, lists no
    /// folder and reports no change.
    /// </summary>
    private sealed class ClientFileProvider : IFileProvider
    {
        private static readonly char[] Separators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

        private readonly Dictionary<string, ClientFile> files;

        // Names are compared as the default file system of the operating system compares them, as serving from the
        // folder did: NTFS and APFS ignore case, Linux file systems do not. Two names that differ only in case prove
        // that the folder's file system tells them apart, so lookups then stay exact.
        public ClientFileProvider(Dictionary<string, ClientFile> exactFiles)
        {
            files = exactFiles;
            if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
            {
                return;
            }

            var ignoringCase = new Dictionary<string, ClientFile>(exactFiles.Count, StringComparer.OrdinalIgnoreCase);
            foreach ((string relativePath, ClientFile file) in exactFiles)
            {
                if (!ignoringCase.TryAdd(relativePath, file))
                {
                    return;
                }
            }

            files = ignoringCase;
        }

        public IFileInfo GetFileInfo(string subpath)
        {
            string? relativePath = ToRelativePath(subpath);
            return relativePath is not null && files.TryGetValue(relativePath, out ClientFile? file)
                ? file
                : new NotFoundFileInfo(subpath);
        }

        // The client host serves files only: it registers neither default files nor directory browsing, so nothing
        // lists a folder.
        public IDirectoryContents GetDirectoryContents(string subpath) => NotFoundDirectoryContents.Singleton;

        // The copy never changes.
        public IChangeToken Watch(string filter) => NullChangeToken.Singleton;

        // The request path as a key of the copy: leading separators dropped and the segments joined with '/'. An
        // empty, "." or ".." segment names nothing, because the copy holds plain relative names only.
        private static string? ToRelativePath(string subpath)
        {
            string[] segments = subpath.TrimStart(Separators).Split(Separators);
            foreach (string segment in segments)
            {
                if (segment.Length == 0 || segment == "." || segment == "..")
                {
                    return null;
                }
            }

            return string.Join('/', segments);
        }
    }

    /// <summary>Why a client folder that exists is not served.</summary>
    private enum ClientFolderRefusal
    {
        /// <summary>Nothing refused the folder.</summary>
        None,

        /// <summary>The folder changed while it was read, could not be confirmed or exceeds the limits.</summary>
        Unconfirmed,

        /// <summary>Other users can write to the folder, or to a folder or file the copy keeps.</summary>
        Untrusted,
    }

    /// <summary>
    /// Reads the client folder into memory, once: every file below it except the ones the copy leaves out, each
    /// confirmed to be the entry the listing found. Outside Windows, the folder, every folder below it that the read
    /// enters and every file the copy keeps must deny other users write permission. The first failure of that check,
    /// a confirmation or a limit ends the read, and nothing of the folder is served.
    /// </summary>
    private sealed class ClientFolderReader
    {
        // Everything is listed, hidden and system entries included, so the exclusions are decided here, and a folder
        // that cannot be read fails the read instead of being skipped.
        private static readonly EnumerationOptions Listing = new()
        {
            AttributesToSkip = 0,
            IgnoreInaccessible = false,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
        };

        private readonly string root;

        // The root with every link resolved, against which Linux confirms each handle; null elsewhere.
        private readonly string? realRoot;

        private readonly Dictionary<string, ClientFile> files = new(StringComparer.Ordinal);

        private int entries;

        private long bytes;

        public ClientFolderReader(string root)
        {
            this.root = root;
            if (OperatingSystem.IsLinux())
            {
                realRoot = ResolveRealPath(root);
            }
        }

        // Returns Unconfirmed when the folder exceeds the limits or a file could not be confirmed, and Untrusted when
        // other users can write to the folder or to a folder or file the copy keeps.
        public ClientFolderRefusal Read()
        {
            // The root as the listing opens it: a root that is a link reports the mode of the folder it resolves to.
            var rootFolder = new DirectoryInfo(root);
            if (IsWritableByOthers(rootFolder))
            {
                return ClientFolderRefusal.Untrusted;
            }

            var pending = new Stack<(DirectoryInfo Folder, string RelativePath)>();
            pending.Push((rootFolder, string.Empty));
            while (pending.TryPop(out (DirectoryInfo Folder, string RelativePath) current))
            {
                bool atRoot = current.RelativePath.Length == 0;
                foreach (FileSystemInfo entry in current.Folder.EnumerateFileSystemInfos("*", Listing))
                {
                    if (++entries > MaxClientEntries)
                    {
                        return ClientFolderRefusal.Unconfirmed;
                    }

                    // A link is left out with everything beneath it, wherever its target lies: the built client holds
                    // none, and only the root may be one.
                    if (IsLink(entry) || (atRoot && IsReservedForServer(entry)))
                    {
                        continue;
                    }

                    string relativePath = atRoot ? entry.Name : current.RelativePath + "/" + entry.Name;
                    if (entry is DirectoryInfo folder)
                    {
                        if (IsWritableByOthers(folder))
                        {
                            return ClientFolderRefusal.Untrusted;
                        }

                        pending.Push((folder, relativePath));
                    }
                    else if (entry is FileInfo file && !IsSensitive(file))
                    {
                        if (IsWritableByOthers(file))
                        {
                            return ClientFolderRefusal.Untrusted;
                        }

                        if (!TryReadFile(file, relativePath))
                        {
                            return ClientFolderRefusal.Unconfirmed;
                        }
                    }
                }
            }

            return ClientFolderRefusal.None;
        }

        public ClientFileProvider CreateProvider() => new(files);

        private static bool IsLink(FileSystemInfo entry) =>
            entry.LinkTarget is not null || (entry.Attributes & FileAttributes.ReparsePoint) != 0;

        // Whether the mode of the entry, as the listing described it, lets other users write to it. Group write is
        // allowed: where each user has a private group, the default umask 002 leaves every build output group-writable
        // by a group holding only its owner. Windows keeps access in control lists, which the mode does not describe,
        // so nothing is refused there. An entry gone since it was listed reports no mode and is left to the read.
        private static bool IsWritableByOthers(FileSystemInfo entry) =>
            !OperatingSystem.IsWindows() && entry.Exists && (entry.UnixFileMode & UnixFileMode.OtherWrite) != 0;

        // The server's own paths: everything under api/ or openapi/, and a root file named health.
        private static bool IsReservedForServer(FileSystemInfo entry) =>
            IsServerPrefix(entry.Name) || (entry is FileInfo && IsHealthPath(entry.Name));

        // The files PhysicalFileProvider leaves out by default (ExclusionFilters.Sensitive), judged on the file alone.
        private static bool IsSensitive(FileInfo file) =>
            file.Name.StartsWith('.') || (file.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;

        // The entry at path as the file system describes it now, or null unless it is a file that is not a link.
        // FileInfo describes a link itself, not its target, so a link's length and time never stand in for a file's.
        private static FileInfo? StatPlainFile(string path)
        {
            var file = new FileInfo(path);
            return file.Exists && !IsLink(file) ? file : null;
        }

        // Fills content from the start of the file and confirms that the file ends there, neither shorter nor longer.
        private static bool ReadToEnd(SafeFileHandle handle, byte[] content)
        {
            int read = 0;
            while (read < content.Length)
            {
                int count = RandomAccess.Read(handle, content.AsSpan(read), read);
                if (count == 0)
                {
                    return false;
                }

                read += count;
            }

            Span<byte> beyond = stackalloc byte[1];
            return RandomAccess.Read(handle, beyond, read) == 0 && RandomAccess.GetLength(handle) == content.Length;
        }

        // Reads one file through one handle and confirms it is the entry the listing found: on Linux the handle's own
        // path must be the file's real path, and on every system, once the read ends, the entry must still be a file
        // that is not a link, with the handle's length and write time, below folders that are not links either.
        private bool TryReadFile(FileInfo listed, string relativePath)
        {
            string path = listed.FullName;
            FileInfo? before = StatPlainFile(path);
            if (before is null)
            {
                return false;
            }

            byte[] content;
            DateTime modified;
            if (before.Length == 0)
            {
                // Nothing to read, so nothing is opened: a FIFO or another special file lists as an empty file, and
                // opening one would block startup.
                content = [];
                modified = before.LastWriteTimeUtc;
            }
            else
            {
                if (before.Length > MaxClientBytes - bytes)
                {
                    return false;
                }

                using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (OperatingSystem.IsLinux() && !IsOpenedAt(handle, Path.Join(realRoot, relativePath)))
                {
                    return false;
                }

                long length = RandomAccess.GetLength(handle);
                if (length > MaxClientBytes - bytes)
                {
                    return false;
                }

                content = new byte[length];
                if (!ReadToEnd(handle, content))
                {
                    return false;
                }

                modified = File.GetLastWriteTimeUtc(handle);
            }

            FileInfo? after = StatPlainFile(path);
            if (after is null
                || after.Length != content.LongLength
                || after.LastWriteTimeUtc != modified
                || FolderPassesThroughLink(relativePath))
            {
                return false;
            }

            bytes += content.LongLength;
            files.Add(relativePath, new ClientFile(listed.Name, content, new DateTimeOffset(modified)));
            return true;
        }

        // Each folder between the root and the file, checked again once the file is read, so a folder that became a
        // link while the read was inside it is found.
        private bool FolderPassesThroughLink(string relativePath)
        {
            string[] segments = relativePath.Split('/');
            string path = root;
            for (int i = 0; i < segments.Length - 1; i++)
            {
                path = Path.Join(path, segments[i]);
                var folder = new DirectoryInfo(path);
                if (!folder.Exists || IsLink(folder))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// One file of the copy. It has no physical path, so the static-file middleware streams it from memory, ranges and
    /// <c>HEAD</c> included, and never opens a path.
    /// </summary>
    private sealed class ClientFile : IFileInfo
    {
        private readonly byte[] content;

        public ClientFile(string name, byte[] content, DateTimeOffset lastModified)
        {
            Name = name;
            this.content = content;
            LastModified = lastModified;
        }

        public bool Exists => true;

        public long Length => content.LongLength;

        public string? PhysicalPath => null;

        public string Name { get; }

        public DateTimeOffset LastModified { get; }

        public bool IsDirectory => false;

        public Stream CreateReadStream() => new MemoryStream(content, writable: false);
    }
}
