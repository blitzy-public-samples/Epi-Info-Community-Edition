using System.IO.Compression;
using EpiInfo.Api;
using EpiInfo.Api.Endpoints;
using EpiInfo.Api.Errors;
using EpiInfo.Api.Health;
using EpiInfo.Api.Hosting;
using EpiInfo.Api.Json;
using EpiInfo.Api.OpenApi;
using EpiInfo.Api.Services;
using EpiInfo.CheckCode;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.Routing.Matching;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.AspNetCore.Routing.Template;
using Microsoft.Extensions.DependencyInjection.Extensions;

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

    // No Server header: it names the server software to every client and serves none of them.
    options.AddServerHeader = false;
});

builder.Logging.ClearProviders();

// This configure delegate is registered after the console provider's binding of Logging:Console:FormatterOptions, so it
// runs after that binding on every build and reload of the formatter options: scopes, which carry the request path and
// connection id, stay off whatever configuration says, every line carries a UTC timestamp, and the JSON writer stays
// unindented, so each event is one line while the other writer settings configuration supplies still apply. "O" is the
// culture-invariant ISO 8601 round-trip format; a custom pattern would take its separators and calendar from the
// process culture.
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = false;
    options.TimestampFormat = "O";
    options.UseUtcTimestamp = true;
    options.JsonWriterOptions = options.JsonWriterOptions with { Indented = false };
});

// A post-configuration runs after the configuration-bound Logging rules on every rebuild of the filter options, a
// reload of appsettings*.json included, so no level configured for any provider or category lowers a protected
// framework floor (ProtectedLogLevels); a configured level above a floor still applies.
builder.Services.PostConfigure<LoggerFilterOptions>(ProtectedLogLevels.Apply);

// Any body a framework writer produces through the ProblemDetails service keeps only the API's fixed texts and
// traceId.
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

// A singleton, because the check holds the one ping in flight that concurrent and later runs join: AddCheck<T> takes T
// from the container when it is registered there and otherwise creates a new instance for every run.
builder.Services.AddSingleton<SqlServerHealthCheck>();
builder.Services.AddHealthChecks().AddCheck<SqlServerHealthCheck>(SqlServerHealthCheck.Name);

// Routing asks every registered matcher policy; this one settles route constraints such as {viewId:int} before the
// HTTP-method and content-type policies can answer 405 or 415 for a path no route matches.
builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<MatcherPolicy, RouteConstraintMatcherPolicy>());

// Text responses, the client's page, script and styles and the API's JSON, are compressed for a client that accepts
// it; images, ProblemDetails bodies and every other type outside the framework's default list are sent as they are.
// Brotli is listed first, so a browser that accepts both at equal weight, as current browsers do, gets br; gzip
// serves a client that accepts only gzip. Both use Optimal, Brotli quality 4 and gzip level 6, because the middleware
// compresses each response as it is sent: Optimal costs a few milliseconds on the client's script and sends markedly
// fewer bytes than the providers' default Fastest, while Brotli's SmallestSize saves little more at a cost of hundreds
// of milliseconds per response.
// The middleware adds Vary: Accept-Encoding only to a response it codes, or to a HEAD it would code, and leaves the
// ETag as it is. The static client's files are the only responses that carry an ETag, and their host, ClientAppHosting,
// gives each answer of a type the middleware codes that Vary itself, the identity answer and a 304 included, and an
// answer sent, or whose 200 would be sent, in a coding the weak form of the file's ETag, which If-None-Match there
// compares weakly.
// EnableForHttps stays false. The API listens on plain HTTP on 127.0.0.1 only, so the setting never applies, and a
// BREACH-style attack, which recovers a secret from the encrypted length of compressed responses that also reflect
// text the attacker chooses, has neither an encrypted channel nor a secret here: no authentication or anti-forgery
// token exists in local single-user mode, and no network observer sees loopback traffic. An HTTPS listener added
// later must opt in on purpose.
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = false;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes;
});
builder.Services.Configure<BrotliCompressionProviderOptions>(options => options.Level = CompressionLevel.Optimal);
builder.Services.Configure<GzipCompressionProviderOptions>(options => options.Level = CompressionLevel.Optimal);

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

// First, so no exception leaves the pipeline. The exception handler rethrows a failure it could not answer, such as
// one whose handler threw; past this boundary it would reach the Development exception page, which CreateBuilder
// installs ahead of this pipeline and which renders the exception and the request headers, or the server, which logs
// the exception object.
app.UseApiOutermostExceptionBoundary();

// Ahead of everything else that writes a response, so every response carries the API's response headers, an error
// body written after the response is cleared included.
app.Use(ResponseHeaderPolicy.ApplyAsync);

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

// Inside the exception handler, the API's exception boundary and status-code pages. An exception from a response
// being compressed unwinds through this middleware, which puts the original response body back, before the handler
// clears the response and writes its fixed ProblemDetails uncompressed, and a client abort, or any failure once the
// response started, during the final compressed flush reaches the exception boundary, which settles it. After the host
// guard, so a refused Host is answered before anything is served; before routing, so the endpoints, OpenAPI, /health
// and the static client all pass through it.
app.UseResponseCompression();

app.UseRouting();

// After routing, so each guard reads the matched endpoint's metadata, and before the endpoints, so nothing is bound
// before the body limit's counting stream is in place and the media type is checked. Their order changes no status.
app.UseRequestBodyLimit();
app.UseJsonContentTypeGuard();

// Only the v1 document's own name matches, so any other name is a route miss, which status-code pages answer with the
// fixed 404 ProblemDetails; the OpenAPI handler would answer a text/plain 404 quoting the name. The name is a route
// parameter because a pattern without one lets the handler bind it from the query string instead. \z, unlike $,
// does not match before a final newline, so v1 followed by an encoded newline is a miss too.
app.MapOpenApi(@"/openapi/{documentName:regex(^v1\z)}.json");
app.MapFormEndpoints();
app.MapRecordEndpoints();
app.MapCheckCodeEndpoints();
app.MapHealthEndpoints();

// Last, after every endpoint is mapped: the client fallback has the lowest route order and never shadows a route.
app.UseClientApp();

app.Run();

/// <summary>The API entry point, public so <c>WebApplicationFactory&lt;Program&gt;</c> can host it in tests.</summary>
public partial class Program;

namespace EpiInfo.Api
{
    /// <summary>
    /// Minimum levels for the framework log categories whose lower-level events carry request or configuration values,
    /// enforced beneath every configured logging rule, so no <c>Logging</c> setting can lower them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Apply"/> runs as a post-configuration of <see cref="LoggerFilterOptions"/>, after the rules bound
    /// from <c>Logging:LogLevel</c> and <c>Logging:{Provider}:LogLevel</c>. For each provider and category the logger
    /// factory chooses one rule, preferring a provider-specific rule and then the longest matching category, and lets
    /// an event through only when it passes that rule's level and filter. Every rule therefore gets the floors added to
    /// its filter, and a rule carrying only the floors stands first for the categories no configured rule matches, so
    /// the floors hold whichever rule is chosen.
    /// </para>
    /// <para>
    /// A floor is a minimum: a configured level above it still applies, and every category outside the floors keeps its
    /// configured level. A category falls under a floor when it starts with the floor's prefix, ignoring case, as a
    /// configured category rule matches.
    /// </para>
    /// </remarks>
    internal static class ProtectedLogLevels
    {
        // Each category prefix with the lowest level it lets through.
        private static readonly (string CategoryPrefix, LogLevel MinimumLevel)[] Floors =
        [
            // The hosting request lines carry the path and query string; the routing, parameter-binding and Kestrel
            // debug lines carry route values and connection details.
            ("Microsoft.AspNetCore", LogLevel.Warning),

            // The startup lines carry the listen address, the environment name and the content root. The warning about
            // a missing connection string is the API's own category and is not affected.
            ("Microsoft.Hosting.Lifetime", LogLevel.Warning),

            // Its per-check result line repeats, at Error, the one failure line SqlServerHealthCheck writes for the
            // same Unhealthy result, and its debug lines would write a result's data. Its error line for a check that
            // throws never occurs: the one registered check catches every failure and rethrows only the caller's
            // cancellation, which this service does not log.
            ("Microsoft.Extensions.Diagnostics.HealthChecks.DefaultHealthCheckService", LogLevel.None),

            // .NET 10 already keeps an exception a handler reports handled out of its log. Its remaining lines, for an
            // exception no handler answers or a handler that throws, carry the exception object, whose message can
            // hold SQL text or values; the API's exception boundaries write the one redacted line for that failure.
            ("Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware", LogLevel.None),
        ];

        /// <summary>
        /// Adds the floors to the filter of every rule in <paramref name="options"/> and puts a rule carrying only the
        /// floors first.
        /// </summary>
        /// <param name="options">The filter options, after every configuration source has been applied.</param>
        /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
        public static void Apply(LoggerFilterOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            for (int index = 0; index < options.Rules.Count; index++)
            {
                LoggerFilterRule rule = options.Rules[index];
                Func<string?, string?, LogLevel, bool>? filter = rule.Filter;
                options.Rules[index] = new LoggerFilterRule(
                    rule.ProviderName,
                    rule.CategoryName,
                    rule.LogLevel,
                    filter is null
                        ? (_, category, level) => IsAllowed(category, level)
                        : (provider, category, level) =>
                            IsAllowed(category, level) && filter(provider, category, level));
            }

            // Any configured rule that matches is chosen over this one; where none matches, it applies the global
            // minimum level the factory would otherwise use, with the floors.
            options.Rules.Insert(
                0,
                new LoggerFilterRule(null, null, options.MinLevel, (_, category, level) => IsAllowed(category, level)));
        }

        /// <summary>
        /// Whether an event of <paramref name="level"/> in <paramref name="category"/> clears its floor.
        /// </summary>
        /// <param name="category">The logger category; a <see langword="null"/> category falls under no floor.</param>
        /// <param name="level">The level of the event.</param>
        /// <returns>
        /// <see langword="false"/> when the category starts with a floor's prefix and the level is below that floor;
        /// otherwise <see langword="true"/>.
        /// </returns>
        public static bool IsAllowed(string? category, LogLevel level)
        {
            if (category is null)
            {
                return true;
            }

            foreach ((string prefix, LogLevel minimum) in Floors)
            {
                if (level < minimum && category.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// A routing policy that settles the route constraints of a route-table node before the HTTP-method and
    /// content-type policies choose among its endpoints, so a request the constraints of every constrained route reject
    /// is the route miss 404 instead of one of those policies' 405 or 415 answers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Routing walks the request path to the node holding every endpoint whose literal segments the path matches, then
    /// lets each node policy, in <see cref="Order"/>, split that set: the HTTP-method policy by method, the
    /// content-type policy by <c>Content-Type</c>. Constraints such as <c>{viewId:int}</c> run only on the endpoints
    /// left at the end, and the 405 and 415 endpoints those policies add carry none. Left to themselves, they would
    /// answer a <c>text/plain</c> <c>POST</c> to <c>/api/forms/abc/records/search</c> with 415 and a <c>PATCH</c> of
    /// <c>/api/forms/abc/records/x</c> with 405, although no route matches a non-integer form id, and
    /// <c>HEAD /health</c> with 404 while every other method's 405 listed <c>HEAD</c>, taken from the client fallback,
    /// whose constraint excludes <c>health</c>.
    /// </para>
    /// <para>
    /// Running first, it gives each node holding a constrained route two branches: every endpoint of the node, taken
    /// when the constraints of at least one constrained route accept the request, and only the node's unconstrained
    /// endpoints, taken otherwise. The later policies split whichever branch is taken, so their 405 lists only methods
    /// of routes the path can match, and a request no route matches reaches an empty branch, which status-code pages
    /// answer with the 404 ProblemDetails. Routing still runs every constraint on the branch taken, so the policy
    /// removes only candidates their constraints reject and never admits one; endpoint metadata, and so the OpenAPI
    /// document, is unchanged.
    /// </para>
    /// <para>
    /// A constrained route's values are the ones routing gives its constraints. The request path is split into segments
    /// as routing splits it, an empty segment between two slashes included and a trailing slash adding none; the values
    /// start from the pattern's defaults, to which a parameter they do not name adds its required value; a parameter
    /// with a required value captures nothing; a catch-all takes the path from its segment to the end; any other
    /// parameter of a simple segment takes its segment when that segment exists and is not empty; and each complex
    /// segment, such as <c>{name}.{extension}</c>, is then matched against its path segment by a
    /// <see cref="TemplateMatcher"/> over that segment alone, which runs routing's own complex-segment matching and
    /// adds the segment's parameters, so a path segment it cannot match, an empty one included, rejects the route as
    /// routing drops it. A path such as <c>/health//</c> therefore reaches the client route's constraint with the same
    /// values routing passes it, and is the route miss for every method as it is for GET; so is every method on an
    /// OpenAPI document name the route's constraint rejects, such as <c>/openapi/v2.json</c>, while
    /// <c>POST /openapi/v1.json</c> keeps the 405 that lists <c>GET</c>. A route the policy rejects is one routing
    /// rejects, so the policy never turns a match into a miss.
    /// Nodes holding dynamic endpoints are left alone, as the framework's policies leave them. Everything a jump table
    /// holds is built with the route table and never changes, and each request's values are its own, so one table
    /// serves concurrent requests. Nothing here logs, so no path or route value reaches a log.
    /// </para>
    /// </remarks>
    /// <param name="parameterPolicyFactory">
    /// Resolves a route pattern's parameter policy references, inline names such as <c>int</c> included, to the policy
    /// instances routing itself evaluates.
    /// </param>
    internal sealed class RouteConstraintMatcherPolicy(ParameterPolicyFactory parameterPolicyFactory)
        : MatcherPolicy, INodeBuilderPolicy
    {
        /// <summary>
        /// Ahead of the HTTP-method policy (-1000) and the content-type policy (-100), whose rejection endpoints would
        /// otherwise answer before any constraint runs.
        /// </summary>
        public override int Order => -1001;

        /// <summary>
        /// Whether <paramref name="endpoints"/>, one node's set, holds a route with a route constraint.
        /// </summary>
        /// <param name="endpoints">The endpoints of a route-table node.</param>
        /// <returns>
        /// <see langword="true"/> when at least one is a route endpoint with a route constraint and none is dynamic;
        /// otherwise <see langword="false"/>.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="endpoints"/> is <see langword="null"/>.</exception>
        public bool AppliesToEndpoints(IReadOnlyList<Endpoint> endpoints)
        {
            ArgumentNullException.ThrowIfNull(endpoints);

            if (ContainsDynamicEndpoints(endpoints))
            {
                return false;
            }

            foreach (Endpoint endpoint in endpoints)
            {
                if (RouteConstraints(endpoint).Length > 0)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Splits one node's set into the branch taken when a constrained route accepts the request, holding every
        /// endpoint, and the branch taken when none does, holding only the endpoints without a route constraint, each
        /// in the given order.
        /// </summary>
        /// <param name="endpoints">The endpoints of a node this policy applies to.</param>
        /// <returns>The accepted and the rejected edge, each with a state object of its own.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="endpoints"/> is <see langword="null"/>.</exception>
        public IReadOnlyList<PolicyNodeEdge> GetEdges(IReadOnlyList<Endpoint> endpoints)
        {
            ArgumentNullException.ThrowIfNull(endpoints);

            var routes = new List<ConstrainedRoute>();
            var unconstrained = new List<Endpoint>();
            foreach (Endpoint endpoint in endpoints)
            {
                KeyValuePair<string, IRouteConstraint>[] constraints = RouteConstraints(endpoint);
                if (constraints.Length == 0)
                {
                    unconstrained.Add(endpoint);
                    continue;
                }

                // Only a route endpoint yields constraints.
                routes.Add(new ConstrainedRoute(((RouteEndpoint)endpoint).RoutePattern, constraints));
            }

            ConstrainedRoute[] constrained = [.. routes];
            return
            [
                new PolicyNodeEdge(new ConstraintBranch(accepted: true, constrained), endpoints.ToArray()),
                new PolicyNodeEdge(new ConstraintBranch(accepted: false, constrained), unconstrained.ToArray()),
            ];
        }

        /// <summary>Builds the per-request choice between the two branches <see cref="GetEdges"/> created.</summary>
        /// <param name="exitDestination">The destination routing uses when no edge applies.</param>
        /// <param name="edges">
        /// The edges of one node, each carrying a state object <see cref="GetEdges"/> created.
        /// </param>
        /// <returns>The node's jump table.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="edges"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">An edge carries a state this policy did not create.</exception>
        public PolicyJumpTable BuildJumpTable(int exitDestination, IReadOnlyList<PolicyJumpTableEdge> edges)
        {
            ArgumentNullException.ThrowIfNull(edges);

            int acceptedDestination = exitDestination;
            int rejectedDestination = exitDestination;
            ConstrainedRoute[] routes = [];
            foreach (PolicyJumpTableEdge edge in edges)
            {
                if (edge.State is not ConstraintBranch branch)
                {
                    throw new ArgumentException("An edge carries a state this policy did not create.", nameof(edges));
                }

                routes = branch.Routes;
                if (branch.Accepted)
                {
                    acceptedDestination = edge.Destination;
                }
                else
                {
                    rejectedDestination = edge.Destination;
                }
            }

            return new ConstraintJumpTable(routes, acceptedDestination, rejectedDestination);
        }

        // Each route constraint of a route endpoint, keyed by its parameter name, resolved as routing resolves them
        // when it builds its candidates; empty for any other endpoint. A parameter policy that is not a constraint,
        // such as an outbound transformer, is skipped, as routing skips it when matching.
        private KeyValuePair<string, IRouteConstraint>[] RouteConstraints(Endpoint endpoint)
        {
            if (endpoint is not RouteEndpoint routeEndpoint)
            {
                return [];
            }

            RoutePattern pattern = routeEndpoint.RoutePattern;
            List<KeyValuePair<string, IRouteConstraint>>? constraints = null;
            foreach ((string name, IReadOnlyList<RoutePatternParameterPolicyReference> references)
                in pattern.ParameterPolicies)
            {
                // Null for a policy keyed by a name the pattern has no parameter for, which the factory accepts.
                RoutePatternParameterPart? parameter = pattern.GetParameter(name);
                foreach (RoutePatternParameterPolicyReference reference in references)
                {
                    if (parameterPolicyFactory.Create(parameter, reference) is IRouteConstraint constraint)
                    {
                        (constraints ??= []).Add(new KeyValuePair<string, IRouteConstraint>(name, constraint));
                    }
                }
            }

            return constraints is null ? [] : [.. constraints];
        }

        /// <summary>
        /// One constrained route of a node: where routing takes each of its route values from, laid out as routing lays
        /// them out when it builds the route's candidate, and its route constraints.
        /// </summary>
        private sealed class ConstrainedRoute
        {
            // Patterns with up to this many segments read the path through a stack buffer.
            private const int StackSegmentLimit = 32;

            // The values before capture: the pattern's defaults, then one slot per parameter of a simple segment that
            // the defaults do not name, holding its required value, the catch-all's default (null when it has none), or
            // nothing. An empty slot, whose key is null, is dropped when the values are built.
            private readonly KeyValuePair<string, object?>[] slots;

            // Each parameter that takes one whole segment: its name, the index of that segment and its slot.
            private readonly (string Name, int SegmentIndex, int SlotIndex)[] captures;

            // The catch-all parameter, which takes the path from its segment to the end; the name is null when there is
            // none.
            private readonly (string? Name, int SegmentIndex, int SlotIndex) catchAll;

            // How many leading segments of the path the captures, the catch-all and the complex segments read.
            private readonly int segmentsRead;

            // Each complex segment, one of more than a single part such as {name}.{extension}: its index and a matcher
            // over that segment alone, which adds the segment's parameters to the values.
            private readonly (int SegmentIndex, TemplateMatcher Matcher)[] complexSegments;

            private readonly KeyValuePair<string, IRouteConstraint>[] constraints;

            /// <summary>Lays out the route values of <paramref name="pattern"/> as routing does.</summary>
            /// <param name="pattern">The route's pattern.</param>
            /// <param name="constraints">The route's constraints, keyed by parameter name.</param>
            public ConstrainedRoute(RoutePattern pattern, KeyValuePair<string, IRouteConstraint>[] constraints)
            {
                this.constraints = constraints;

                // Parameter names compare as routing compares them, ignoring case.
                var slotIndexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var slotList = new List<KeyValuePair<string, object?>>();
                foreach (KeyValuePair<string, object?> value in pattern.Defaults)
                {
                    slotIndexes.Add(value.Key, slotList.Count);
                    slotList.Add(value);
                }

                var captureList = new List<(string Name, int SegmentIndex, int SlotIndex)>();
                var complexList = new List<(int SegmentIndex, TemplateMatcher Matcher)>();
                for (int segmentIndex = 0; segmentIndex < pattern.PathSegments.Count; segmentIndex++)
                {
                    RoutePatternPathSegment segment = pattern.PathSegments[segmentIndex];
                    if (!segment.IsSimple)
                    {
                        complexList.Add((segmentIndex, SegmentMatcher(segment)));
                        segmentsRead = segmentIndex + 1;
                        continue;
                    }

                    if (segment.Parts[0] is not RoutePatternParameterPart parameter)
                    {
                        continue;
                    }

                    bool hasRequiredValue = TryGetRequiredValue(pattern, parameter, out object? requiredValue);
                    if (!slotIndexes.TryGetValue(parameter.Name, out int slotIndex))
                    {
                        slotIndex = slotList.Count;
                        slotIndexes.Add(parameter.Name, slotIndex);
                        slotList.Add(
                            hasRequiredValue ? new KeyValuePair<string, object?>(parameter.Name, requiredValue)
                            : parameter.Default is not null || parameter.IsCatchAll
                                ? new KeyValuePair<string, object?>(parameter.Name, parameter.Default)
                                : default);
                    }

                    // Routing matches a parameter with a required value as that value's literal and captures nothing.
                    if (hasRequiredValue)
                    {
                        continue;
                    }

                    if (parameter.IsCatchAll)
                    {
                        catchAll = (parameter.Name, segmentIndex, slotIndex);
                    }
                    else
                    {
                        captureList.Add((parameter.Name, segmentIndex, slotIndex));
                    }

                    segmentsRead = segmentIndex + 1;
                }

                slots = [.. slotList];
                captures = [.. captureList];
                complexSegments = [.. complexList];
            }

            /// <summary>
            /// Whether the route's constraints accept the route values routing captures from the request path for this
            /// route.
            /// </summary>
            /// <param name="httpContext">The request, whose path supplies the route values.</param>
            /// <returns>
            /// <see langword="false"/> when a complex segment does not match its path segment, where routing drops the
            /// route, or when a constraint rejects those values; <see langword="true"/> when every constraint accepts
            /// them.
            /// </returns>
            public bool Accepts(HttpContext httpContext)
            {
                // The path routing reads; a missing one has no segments.
                string path = httpContext.Request.Path.Value ?? string.Empty;
                Span<PathSegment> buffer = segmentsRead <= StackSegmentLimit
                    ? stackalloc PathSegment[segmentsRead]
                    : new PathSegment[segmentsRead];
                int count = Tokenize(path, buffer);

                // An array of its own per call: route values are request data, and the layout is shared by every
                // request.
                KeyValuePair<string, object?>[] values = [.. slots];
                foreach ((string name, int segmentIndex, int slotIndex) in captures)
                {
                    // An empty segment captures nothing, so the slot keeps its default or stays empty.
                    if (segmentIndex < count && buffer[segmentIndex].Length > 0)
                    {
                        PathSegment segment = buffer[segmentIndex];
                        values[slotIndex] =
                            new KeyValuePair<string, object?>(name, path.Substring(segment.Start, segment.Length));
                    }
                }

                if (catchAll.Name is not null && catchAll.SegmentIndex < count)
                {
                    values[catchAll.SlotIndex] = new KeyValuePair<string, object?>(
                        catchAll.Name,
                        path.Substring(buffer[catchAll.SegmentIndex].Start));
                }

                RouteValueDictionary routeValues = RouteValueDictionary.FromArray(values);

                // Routing matches each complex segment once the other values are in place and drops the route when one
                // does not match. An empty segment never matches, because each parameter of a complex segment needs a
                // value. The matcher reads the segment, with the slash before it, as a path of one segment; the
                // PathString constructor keeps the text as routing reads it, where the string conversion would
                // unescape it.
                foreach ((int segmentIndex, TemplateMatcher matcher) in complexSegments)
                {
                    if (segmentIndex >= count || buffer[segmentIndex].Length == 0)
                    {
                        return false;
                    }

                    PathSegment segment = buffer[segmentIndex];
                    var segmentPath = new PathString(path.Substring(segment.Start - 1, segment.Length + 1));
                    if (!matcher.TryMatch(segmentPath, routeValues))
                    {
                        return false;
                    }
                }

                foreach ((string parameterName, IRouteConstraint constraint) in constraints)
                {
                    if (!constraint.Match(
                            httpContext,
                            null,
                            parameterName,
                            routeValues,
                            RouteDirection.IncomingRequest))
                    {
                        return false;
                    }
                }

                return true;
            }

            // Routing's own rule: a required value stands for its literal unless it is null or empty.
            private static bool TryGetRequiredValue(
                RoutePattern pattern,
                RoutePatternParameterPart parameter,
                out object? value) =>
                pattern.RequiredValues.TryGetValue(parameter.Name, out value)
                && !RouteValueEqualityComparer.Default.Equals(value, string.Empty);

            // A matcher over segment alone, which runs routing's own complex-segment matching: it compares the literals
            // ignoring case, divides the rest of the text among the parameters, adds their values to the ones it is
            // given and leaves the others as they are. Its parameters carry no policies and no defaults, because the
            // constraints run afterwards and the defaults are already among the values.
            private static TemplateMatcher SegmentMatcher(RoutePatternPathSegment segment)
            {
                RoutePatternPart[] parts =
                [
                    .. segment.Parts.Select(part => part is RoutePatternParameterPart parameter
                        ? RoutePatternFactory.ParameterPart(parameter.Name, null, parameter.ParameterKind)
                        : part),
                ];
                RoutePattern segmentPattern = RoutePatternFactory.Pattern(RoutePatternFactory.Segment(parts));
                return new TemplateMatcher(new RouteTemplate(segmentPattern), new RouteValueDictionary());
            }

            // Splits path into segments as routing does: after the leading slash, each piece that ends in a slash is a
            // segment, an empty piece between two slashes included, and the text after the last slash is one more only
            // when it is not empty, so a trailing slash adds none. It stops once segments is full; the segments before
            // that point are the ones routing finds.
            private static int Tokenize(string path, Span<PathSegment> segments)
            {
                if (path.Length == 0)
                {
                    return 0;
                }

                int count = 0;
                int start = 1;
                ReadOnlySpan<char> rest = path.AsSpan(start);
                int end;
                while (count < segments.Length && (end = rest.IndexOf('/')) >= 0)
                {
                    segments[count++] = new PathSegment(start, end);
                    start += end + 1;
                    rest = path.AsSpan(start);
                }

                if (rest.Length > 0 && count < segments.Length)
                {
                    segments[count++] = new PathSegment(start, rest.Length);
                }

                return count;
            }
        }

        /// <summary>
        /// One segment of a request path: where it starts in the path and how many characters it holds.
        /// </summary>
        private readonly record struct PathSegment(int Start, int Length);

        /// <summary>
        /// The state of one of a node's two edges. Each edge has an instance of its own, because routing keys a node's
        /// edges by their states; both carry the node's constrained routes for <see cref="BuildJumpTable"/>.
        /// </summary>
        private sealed class ConstraintBranch(bool accepted, ConstrainedRoute[] routes)
        {
            /// <summary>Whether this is the branch taken when a constrained route accepts the request.</summary>
            public bool Accepted { get; } = accepted;

            /// <summary>The node's constrained routes.</summary>
            public ConstrainedRoute[] Routes { get; } = routes;

            /// <summary>The edge's label in routing's graph diagnostics.</summary>
            public override string ToString() =>
                Accepted ? "route constraints: accepted" : "route constraints: rejected";
        }

        /// <summary>
        /// Takes the accepted branch as soon as one constrained route accepts the request, and the rejected branch when
        /// none does.
        /// </summary>
        private sealed class ConstraintJumpTable(
            ConstrainedRoute[] routes,
            int acceptedDestination,
            int rejectedDestination) : PolicyJumpTable
        {
            public override int GetDestination(HttpContext httpContext)
            {
                foreach (ConstrainedRoute route in routes)
                {
                    if (route.Accepts(httpContext))
                    {
                        return acceptedDestination;
                    }
                }

                return rejectedDestination;
            }
        }
    }

    /// <summary>
    /// The headers the API sets on every response it sends, whichever part of the pipeline writes it: the endpoints,
    /// OpenAPI, <c>/health</c>, the static client and its <c>index.html</c> fallback, the error handling and the
    /// status-code pages. Every response carries <c>X-Content-Type-Options: nosniff</c>, so a browser never reads a
    /// body as a type other than the one it is sent as, and <c>X-Frame-Options: DENY</c> with
    /// <c>Content-Security-Policy: frame-ancestors 'none'</c>, so no page can frame the client. A response to a
    /// request under <c>/api</c>, success and error alike, also carries <c>Cache-Control: no-store</c>: record, list
    /// and search bodies hold the records' field values, which no browser cache may keep.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ApplyAsync"/> runs ahead of every middleware that writes a response and registers the headers through
    /// <see cref="HttpResponse.OnStarting(Func{object, Task}, object)"/>, so they are set as the response starts. They
    /// therefore survive the exception handler and the API's exception boundaries clearing the response before an
    /// error body, and, because start callbacks run in the reverse order of their registration, they are applied after
    /// any callback registered later, such as the exception handler's cache headers, whose <c>Cache-Control</c> an
    /// <c>/api</c> response replaces. Whether a request is under <c>/api</c> is decided on its path at entry, by whole
    /// segment and ignoring case, as routing matches the <c>api</c> literal.
    /// </para>
    /// <para>
    /// Nothing else is set or changed. The Content-Security-Policy holds the one directive <c>frame-ancestors</c>,
    /// which restricts nothing the client itself loads. Caching outside <c>/api</c> stays with its owner, the static
    /// client host: the hashed client bundles under <c>/assets</c> are <c>public, max-age=31536000, immutable</c> and
    /// <c>index.html</c> is <c>no-cache</c>, and that host gives each file's answer the <c>Vary</c> and the entity tag
    /// of the representation it sends. No HSTS header is sent, because the API listens on plain HTTP on the
    /// loopback address only, and no CORS header, because the client is same-origin.
    /// </para>
    /// </remarks>
    internal static class ResponseHeaderPolicy
    {
        private const string NoSniff = "nosniff";

        private const string DenyFraming = "DENY";

        private const string NoFrameAncestors = "frame-ancestors 'none'";

        private const string NoStore = "no-store";

        private static readonly PathString ApiPath = new("/api");

        // One delegate each, shared by every request; the state each start callback receives is its response.
        private static readonly Func<object, Task> SetHeaders = response => Apply((HttpResponse)response, api: false);

        private static readonly Func<object, Task> SetApiHeaders = response => Apply((HttpResponse)response, api: true);

        /// <summary>
        /// Registers the headers of the response to <paramref name="httpContext"/>, then calls the rest of the
        /// pipeline. <c>Program.cs</c> adds it with <c>app.Use(ResponseHeaderPolicy.ApplyAsync)</c>.
        /// </summary>
        /// <param name="httpContext">The current request, whose response has not started.</param>
        /// <param name="next">The rest of the pipeline.</param>
        /// <returns>The rest of the pipeline's task.</returns>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="httpContext"/> or <paramref name="next"/> is <see langword="null"/>.
        /// </exception>
        public static Task ApplyAsync(HttpContext httpContext, RequestDelegate next)
        {
            ArgumentNullException.ThrowIfNull(httpContext);
            ArgumentNullException.ThrowIfNull(next);

            HttpResponse response = httpContext.Response;
            bool api = httpContext.Request.Path.StartsWithSegments(ApiPath, StringComparison.OrdinalIgnoreCase);
            response.OnStarting(api ? SetApiHeaders : SetHeaders, response);
            return next(httpContext);
        }

        private static Task Apply(HttpResponse response, bool api)
        {
            IHeaderDictionary headers = response.Headers;
            headers.XContentTypeOptions = NoSniff;
            headers.XFrameOptions = DenyFraming;
            headers.ContentSecurityPolicy = NoFrameAncestors;
            if (api)
            {
                headers.CacheControl = NoStore;
            }

            return Task.CompletedTask;
        }
    }
}
