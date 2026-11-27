// Composition root of the EpiInfo web API. It builds the ASP.NET Core host, refuses every listen setting that does
// not bind 127.0.0.1 exactly and answers every request whose Host is not 127.0.0.1 or localhost with a 400
// (LoopbackBindingGuard), loads the Kestrel endpoints with configuration reload off and applies the configured
// request body limit, logs through the JSON console provider only, with UTC timestamps and without scopes, under
// minimum levels for the protected framework categories that no logging configuration can lower (ProtectedLogLevels),
// and warns at startup when the database connection string is not configured. It registers the wire JSON settings
// (JsonSetup), the OpenAPI document v1 with its schema transformers, the SQL Server data store
// (DataStoreRegistration), the check-code engine, the save stamp, the form, record and field-event services, the
// database health check, and the routing policy that settles route constraints before the HTTP-method and content-type
// policies (RouteConstraintMatcherPolicy), so a path no route matches is the 404 whatever its method or media type.
// Its pipeline answers every failure through the API's error contract (ApiExceptionHandler and its exception
// boundary), turns every empty error response into the API's fixed ProblemDetails (ApiProblems),
// enforces the body limit and the JSON media type on the body endpoints after routing, maps /openapi/v1.json, the /api
// form, record and field-event routes and /health, and serves the built web client with its index.html fallback
// (ClientAppHosting) last.
using EpiInfo.Api.Endpoints;
using EpiInfo.Api.Errors;
using EpiInfo.Api.Health;
using EpiInfo.Api.Hosting;
using EpiInfo.Api.Json;
using EpiInfo.Api.OpenApi;
using EpiInfo.Api.Services;
using EpiInfo.CheckCode;
using Microsoft.AspNetCore.Routing.Matching;
using Microsoft.AspNetCore.Routing.Patterns;
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

// Routing asks every registered matcher policy; this one settles route constraints such as {viewId:int} before the
// HTTP-method and content-type policies can answer 405 or 415 for a path no route matches.
builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<MatcherPolicy, RouteConstraintMatcherPolicy>());

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

/// <summary>
/// Minimum levels for the framework log categories whose lower-level events carry request or configuration values,
/// enforced beneath every configured logging rule, so no <c>Logging</c> setting can lower them.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Apply"/> runs as a post-configuration of <see cref="LoggerFilterOptions"/>, after the rules bound from
/// <c>Logging:LogLevel</c> and <c>Logging:{Provider}:LogLevel</c>. For each provider and category the logger factory
/// chooses one rule, preferring a provider-specific rule and then the longest matching category, and lets an event
/// through only when it passes that rule's level and filter. Every rule therefore gets the floors added to its filter,
/// and a rule carrying only the floors stands first for the categories no configured rule matches, so the floors hold
/// whichever rule is chosen.
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
        // The hosting request lines carry the path and query string; the routing, parameter-binding and Kestrel debug
        // lines carry route values and connection details.
        ("Microsoft.AspNetCore", LogLevel.Warning),

        // The startup lines carry the listen address, the environment name and the content root. The warning about a
        // missing connection string is the API's own category and is not affected.
        ("Microsoft.Hosting.Lifetime", LogLevel.Warning),

        // Its per-check result line repeats, at Error, the one failure line SqlServerHealthCheck writes for the same
        // Unhealthy result, and its debug lines would write a result's data. Its error line for a check that throws
        // never occurs: the one registered check catches every failure and rethrows only the caller's cancellation,
        // which this service does not log.
        ("Microsoft.Extensions.Diagnostics.HealthChecks.DefaultHealthCheckService", LogLevel.None),
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
                    : (provider, category, level) => IsAllowed(category, level) && filter(provider, category, level));
        }

        // Any configured rule that matches is chosen over this one; where none matches, it applies the global minimum
        // level the factory would otherwise use, with the floors.
        options.Rules.Insert(
            0,
            new LoggerFilterRule(null, null, options.MinLevel, (_, category, level) => IsAllowed(category, level)));
    }

    /// <summary>Whether an event of <paramref name="level"/> in <paramref name="category"/> clears its floor.</summary>
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
/// A routing policy that settles the route constraints of a route-table node before the HTTP-method and content-type
/// policies choose among its endpoints, so a request the constraints of every constrained route reject is the route
/// miss 404 instead of one of those policies' 405 or 415 answers.
/// </summary>
/// <remarks>
/// <para>
/// Routing walks the request path to the node holding every endpoint whose literal segments the path matches, then
/// lets each node policy, in <see cref="Order"/>, split that set: the HTTP-method policy by method, the content-type
/// policy by <c>Content-Type</c>. Constraints such as <c>{viewId:int}</c> run only on the endpoints left at the end,
/// and the 405 and 415 endpoints those policies add carry none. Left to themselves, they would answer a
/// <c>text/plain</c> <c>POST</c> to <c>/api/forms/abc/records/search</c> with 415 and a <c>PATCH</c> of
/// <c>/api/forms/abc/records/x</c> with 405, although no route matches a non-integer form id, and <c>HEAD /health</c>
/// with 404 while every other method's 405 listed <c>HEAD</c>, taken from the client fallback, whose constraint
/// excludes <c>health</c>.
/// </para>
/// <para>
/// Running first, it gives each node holding a constrained route two branches: every endpoint of the node, taken when
/// the constraints of at least one constrained route accept the request, and only the node's unconstrained endpoints,
/// taken otherwise. The later policies split whichever branch is taken, so their 405 lists only methods of routes the
/// path can match, and a request no route matches reaches an empty branch, which status-code pages answer with the
/// 404 ProblemDetails. Routing still runs every constraint on the branch taken, so the policy removes only candidates
/// their constraints reject and never admits one; endpoint metadata, and so the OpenAPI document, is unchanged.
/// </para>
/// <para>
/// A constrained route's values are the ones routing gives its constraints. The request path is split into segments as
/// routing splits it, an empty segment between two slashes included and a trailing slash adding none; the values start
/// from the pattern's defaults, to which a parameter they do not name adds its required value; a parameter with a
/// required value captures nothing; a catch-all takes the path from its segment to the end; and any other parameter
/// takes its segment when that segment exists and is not empty. A path such as <c>/health//</c> therefore reaches the
/// client route's constraint with the same values routing passes it, and is the route miss for every method as it is
/// for GET. A route with a complex segment, such as <c>{name}.{extension}</c>, counts as accepted, because routing
/// fills that segment's values through a matcher it does not expose; the node is then left to routing's own handling,
/// so the policy can never turn a match into a miss. Nodes holding dynamic endpoints are left alone, as the
/// framework's policies leave them. Everything a jump table holds is built with the route table and never changes, and
/// each request's values are its own, so one table serves concurrent requests. Nothing here logs, so no path or route
/// value reaches a log.
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

    /// <summary>Whether <paramref name="endpoints"/>, one node's set, holds a route with a route constraint.</summary>
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
    /// endpoint, and the branch taken when none does, holding only the endpoints without a route constraint, each in
    /// the given order.
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
    /// <param name="edges">The edges of one node, each carrying a state object <see cref="GetEdges"/> created.</param>
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

    // Each route constraint of a route endpoint, keyed by its parameter name, resolved as routing resolves them when it
    // builds its candidates; empty for any other endpoint. A parameter policy that is not a constraint, such as an
    // outbound transformer, is skipped, as routing skips it when matching.
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

        // The values before capture: the pattern's defaults, then one slot per parameter of a simple segment that the
        // defaults do not name, holding its required value, the catch-all's default (null when it has none), or
        // nothing. An empty slot, whose key is null, is dropped when the values are built.
        private readonly KeyValuePair<string, object?>[] slots;

        // Each parameter that takes one whole segment: its name, the index of that segment and its slot.
        private readonly (string Name, int SegmentIndex, int SlotIndex)[] captures;

        // The catch-all parameter, which takes the path from its segment to the end; the name is null when there is
        // none.
        private readonly (string? Name, int SegmentIndex, int SlotIndex) catchAll;

        // How many leading segments of the path the captures and the catch-all read.
        private readonly int segmentsRead;

        // Whether the pattern has a complex segment, one of more than a single part, such as {name}.{extension}.
        private readonly bool hasComplexSegment;

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
            for (int segmentIndex = 0; segmentIndex < pattern.PathSegments.Count; segmentIndex++)
            {
                RoutePatternPathSegment segment = pattern.PathSegments[segmentIndex];
                if (!segment.IsSimple)
                {
                    hasComplexSegment = true;
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
        }

        /// <summary>
        /// Whether the route's constraints accept the route values routing captures from the request path for this
        /// route.
        /// </summary>
        /// <param name="httpContext">The request, whose path supplies the route values.</param>
        /// <returns>
        /// <see langword="false"/> when a constraint rejects those values; <see langword="true"/> when every constraint
        /// accepts them, and for a pattern with a complex segment.
        /// </returns>
        public bool Accepts(HttpContext httpContext)
        {
            // Routing fills a complex segment's values through its own matcher, which it does not expose. Without them
            // a constraint could reject what routing accepts, so such a route is left to routing, and the policy never
            // turns a match into a miss.
            if (hasComplexSegment)
            {
                return true;
            }

            // The path routing reads; a missing one has no segments.
            string path = httpContext.Request.Path.Value ?? string.Empty;
            Span<PathSegment> buffer = segmentsRead <= StackSegmentLimit
                ? stackalloc PathSegment[segmentsRead]
                : new PathSegment[segmentsRead];
            int count = Tokenize(path, buffer);

            // An array of its own per call: route values are request data, and the layout is shared by every request.
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
            foreach ((string parameterName, IRouteConstraint constraint) in constraints)
            {
                if (!constraint.Match(httpContext, null, parameterName, routeValues, RouteDirection.IncomingRequest))
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

    /// <summary>One segment of a request path: where it starts in the path and how many characters it holds.</summary>
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
