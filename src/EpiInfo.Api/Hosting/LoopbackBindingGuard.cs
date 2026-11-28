using EpiInfo.Api.Errors;

namespace EpiInfo.Api.Hosting;

/// <summary>
/// Refuses to start the API unless every effective listen setting binds the IPv4 loopback address
/// <c>127.0.0.1</c> exactly. It reads configuration only, so it runs before any socket opens and a rejected
/// setting never listens. For a server that opens sockets, <see cref="FreezeListenSettings"/> then reads the listen
/// settings once more, fails when none is configured, and gives Kestrel and the host a fixed copy of exactly the
/// values it verified, so a settings file that changes afterwards cannot alter what is bound. Once the API runs,
/// <see cref="UseLoopbackHostGuard"/> answers every request whose HTTP <c>Host</c> is not <see cref="AllowedHost"/>
/// or <see cref="LocalhostName"/> with a 400, before anything is served.
/// </summary>
/// <remarks>
/// <para>
/// A loopback socket bounds which machine can connect, not which HTTP authority a request names. Through DNS
/// rebinding, a page from another site points its own host name at <c>127.0.0.1</c>, so the browser sends the
/// page's requests to this unauthenticated API and lets the page read the answers as same-origin. Such a request
/// still carries that other name in its <c>Host</c>, which is why every request's authority is checked as well as
/// every listen setting.
/// </para>
/// <para>
/// The allowed request hosts are fixed in code and read from no configuration, as the listen settings are pinned, so
/// no setting or environment variable can widen them. The framework's host filtering is not relied on: it takes its
/// list from the <c>AllowedHosts</c> setting, allows every host when that is unset, and answers a rejected host with
/// a body that is not ProblemDetails.
/// </para>
/// </remarks>
public static class LoopbackBindingGuard
{
    /// <summary>Host key fed by <c>ASPNETCORE_URLS</c>, <c>DOTNET_URLS</c> and <c>--urls</c>; may hold several URLs separated by <c>;</c>.</summary>
    public const string UrlsKey = "urls";

    /// <summary>Host key fed by <c>ASPNETCORE_HTTP_PORTS</c> and <c>DOTNET_HTTP_PORTS</c>.</summary>
    public const string HttpPortsKey = "http_ports";

    /// <summary>Host key fed by <c>ASPNETCORE_HTTPS_PORTS</c> and <c>DOTNET_HTTPS_PORTS</c>.</summary>
    public const string HttpsPortsKey = "https_ports";

    /// <summary>Section whose named children each carry one Kestrel endpoint <c>Url</c>.</summary>
    public const string KestrelEndpointsSection = "Kestrel:Endpoints";

    /// <summary>The only host a listen URL may name, and one of the two a request <c>Host</c> may carry.</summary>
    public const string AllowedHost = "127.0.0.1";

    /// <summary>
    /// The client name a request <c>Host</c> may carry besides <see cref="AllowedHost"/>: the default
    /// <c>E2E_BASE_URL</c> <c>http://localhost:5000</c>, and the Vite development origin <c>localhost:5173</c>, whose
    /// proxy forwards the browser's <c>Host</c>. It is never allowed as a listen host.
    /// </summary>
    public const string LocalhostName = "localhost";

    private const string EndpointUrlKey = "Url";

    private const string KestrelSection = "Kestrel";

    private const string EndpointsKey = "Endpoints";

    // Host key that decides whether the urls addresses replace the Kestrel endpoints.
    private const string PreferHostingUrlsKey = "preferHostingUrls";

    // Fixed text naming keys only, never a value, like every message of the guard.
    private const string NoListenAddressMessage =
        "No listen address is configured: set 'Kestrel:Endpoints:Http:Url' (committed in appsettings.json) or 'urls' "
        + "to an http://127.0.0.1 URL; Kestrel's default would also bind IPv6 loopback.";

    private const string NotPinnableMessage =
        "The listen settings cannot be pinned: the configuration given to the Kestrel setup is not the host's "
        + "configuration builder, so the host could bind settings that were never verified.";

    // Ports-only settings make the host bind every interface, whatever the port, so any non-empty value is refused.
    private static readonly string[] PortsOnlyKeys = [HttpPortsKey, HttpsPortsKey];

    // The host keys the generic web host reads when it starts the server, beside the Kestrel section.
    private static readonly string[] HostListenKeys = [UrlsKey, HttpPortsKey, HttpsPortsKey, PreferHostingUrlsKey];

    // The host names a request may carry, fixed here and never read from configuration, so no setting can widen them.
    private static readonly string[] AllowedRequestHosts = [AllowedHost, LocalhostName];

    /// <summary>
    /// Throws when any listen setting in <paramref name="configuration"/> breaks the loopback rule, naming every
    /// offending key at once.
    /// </summary>
    /// <exception cref="InvalidOperationException">One or more listen settings are not allowed.</exception>
    public static void Validate(IConfiguration configuration)
    {
        IReadOnlyList<string> violations = FindViolations(configuration);
        if (violations.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(DescribeViolations(violations));
    }

    /// <summary>
    /// Returns the full configuration keys whose listen setting breaks the loopback rule: Kestrel endpoint URLs in
    /// section order, then <see cref="UrlsKey"/>, <see cref="HttpPortsKey"/> and <see cref="HttpsPortsKey"/>.
    /// Each setting is checked in the form <see cref="FreezeListenSettings"/> hands to the server: an endpoint URL and
    /// each <c>;</c>-separated <see cref="UrlsKey"/> entry are trimmed, and a null, empty or whitespace setting or
    /// entry counts as absent. A configuration with no listen setting at all therefore passes here, which keeps hosts
    /// that open no socket, such as the in-memory test server, usable; a server that opens sockets fails closed on it
    /// in <see cref="FreezeListenSettings"/>.
    /// </summary>
    public static IReadOnlyList<string> FindViolations(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return Inspect(configuration).Violations;
    }

    /// <summary>
    /// Verifies the listen settings of <paramref name="configuration"/> for a server that opens sockets, and returns
    /// the <c>Kestrel</c> section for <c>KestrelServerOptions.Configure</c> as a fixed copy of the values verified,
    /// while pinning the host's own listen keys to that same copy. Call it from the <c>ConfigureKestrel</c> callback
    /// of the host that owns <paramref name="configuration"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The live sources are read once. The returned copy never reloads and holds every setting of the <c>Kestrel</c>
    /// section, with each endpoint <c>Url</c> replaced by its trimmed, verified text; an endpoint whose <c>Url</c> is
    /// missing or blank is left out with all of its settings, because Kestrel would reject it and abort startup.
    /// </para>
    /// <para>
    /// One in-memory source is added last to <paramref name="configuration"/>, so it outranks every reloadable
    /// settings file when the host starts the server: <see cref="UrlsKey"/> holds the verified entries joined by
    /// <c>;</c>, or is empty when there are none; <see cref="HttpPortsKey"/> and <see cref="HttpsPortsKey"/> are
    /// empty; and <c>preferHostingUrls</c> keeps the value read only while <see cref="UrlsKey"/> has entries, and is
    /// <c>false</c> otherwise, so the verified endpoints win over any address the host captured before this call.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// A listen setting is not allowed (the message <see cref="Validate"/> gives); no listen address is configured,
    /// in which case Kestrel would bind its <c>localhost</c> default, IPv6 loopback included; or
    /// <paramref name="configuration"/> is not a configuration builder the pinned values can be added to.
    /// </exception>
    public static IConfiguration FreezeListenSettings(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // Everything below checks and hands on this one copy, so a settings file reloading meanwhile cannot change
        // what the server is given.
        IConfigurationRoot snapshot = CopyListenSettings(configuration);
        ListenSettings settings = Inspect(snapshot);

        if (settings.Violations.Count > 0)
        {
            throw new InvalidOperationException(DescribeViolations(settings.Violations));
        }

        if (!settings.HasAddress)
        {
            throw new InvalidOperationException(NoListenAddressMessage);
        }

        if (configuration is not IConfigurationBuilder hostConfiguration)
        {
            throw new InvalidOperationException(NotPinnableMessage);
        }

        string? preferHostingUrls = snapshot[PreferHostingUrlsKey];
        hostConfiguration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [UrlsKey] = string.Join(';', settings.Urls),
            [HttpPortsKey] = string.Empty,
            [HttpsPortsKey] = string.Empty,
            [PreferHostingUrlsKey] = settings.Urls.Count > 0 && preferHostingUrls is not null ? preferHostingUrls : "false",
        });

        return FreezeKestrelSection(snapshot, settings);
    }

    /// <summary>
    /// Whether <paramref name="url"/>, exactly as written, is an <c>http</c> or <c>https</c> URL that Kestrel binds to
    /// <c>127.0.0.1</c> alone, on any port (<c>0</c> included). Nothing is trimmed: Kestrel parses the text it is given
    /// unchanged, so surrounding whitespace fails, and a setting is normalised before it is checked. Never throws.
    /// </summary>
    public static bool IsAllowedListenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        if (uri.HostNameType != UriHostNameType.IPv4 || !string.Equals(uri.Host, AllowedHost, StringComparison.Ordinal))
        {
            return false;
        }

        return SpellsLoopbackUrl(url);
    }

    /// <summary>
    /// Whether <paramref name="host"/>, a request's <c>Host</c>, names <see cref="AllowedHost"/> or
    /// <see cref="LocalhostName"/>. Only the host part is compared, case-insensitively, and any port is ignored. A
    /// missing or empty value fails, and so does every other name or address: another <c>127.x</c> address,
    /// <c>[::1]</c>, <c>0.0.0.0</c>, a name that only begins or ends with an allowed one, a name with a trailing dot,
    /// and a value carrying user information. Never throws.
    /// </summary>
    /// <param name="host">The request's <c>Host</c>, as <see cref="HttpRequest.Host"/> holds it.</param>
    public static bool IsAllowedRequestHost(HostString host)
    {
        if (!host.HasValue)
        {
            return false;
        }

        return AllowedRequestHosts.Contains(host.Host, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Adds the per-request authority check: a request whose <c>Host</c> fails <see cref="IsAllowedRequestHost"/> is
    /// answered with the request-level 400 <see cref="ApiProblems.HostNotAllowedDetail"/>, and the rest of the
    /// pipeline never runs. Call it after the exception handler and status-code pages and before <c>UseRouting</c>,
    /// so no endpoint, OpenAPI document or client file is served under any other name. Nothing is logged, and the
    /// rejected value is never written to the response.
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> is <see langword="null"/>.</exception>
    public static IApplicationBuilder UseLoopbackHostGuard(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.Use(next => context => IsAllowedRequestHost(context.Request.Host)
            ? next(context)
            : ApiProblems.WriteAsync(context, ApiProblems.HostNotAllowed()));
    }

    // Kestrel does not parse listen URLs with Uri. It takes the scheme as the text before "://" and the host as the
    // text between "://" and the last ':' before the first '/', and binds every interface when that host text is
    // neither "localhost" nor an IP address. Uri ignores surrounding whitespace, reports host 127.0.0.1 for forms
    // Kestrel reads otherwise ("http://user@127.0.0.1:5000", "http://127.0.0.1:5000?x", "http://127.0.0.1:"), and
    // normalises shorthand such as "http://127.1", so the URL text itself must spell "http" or "https" in any case, as
    // Kestrel compares it, then "://127.0.0.1" or "://127.0.0.1:<digits>", optionally followed by a single '/'.
    private static bool SpellsLoopbackUrl(string url)
    {
        int schemeDelimiter = url.IndexOf(Uri.SchemeDelimiter, StringComparison.Ordinal);
        if (schemeDelimiter < 0)
        {
            return false;
        }

        ReadOnlySpan<char> scheme = url.AsSpan(0, schemeDelimiter);
        if (!scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        ReadOnlySpan<char> rest = url.AsSpan(schemeDelimiter + Uri.SchemeDelimiter.Length);
        int pathStart = rest.IndexOf('/');
        if (pathStart >= 0 && pathStart != rest.Length - 1)
        {
            return false;
        }

        ReadOnlySpan<char> authority = pathStart < 0 ? rest : rest[..pathStart];
        if (!authority.StartsWith(AllowedHost, StringComparison.Ordinal))
        {
            return false;
        }

        ReadOnlySpan<char> portPart = authority[AllowedHost.Length..];
        if (portPart.IsEmpty)
        {
            return true;
        }

        ReadOnlySpan<char> digits = portPart[1..];
        return portPart[0] == ':' && !digits.IsEmpty && !digits.ContainsAnyExceptInRange('0', '9');
    }

    // Keys only, never values: a configured URL can carry credentials or internal host names.
    private static string DescribeViolations(IReadOnlyList<string> violations)
    {
        string keys = string.Join(", ", violations.Select(key => $"'{key}'"));
        string subject = violations.Count == 1 ? $"Listen setting {keys} is" : $"Listen settings {keys} are";
        return $"{subject} not allowed: the API must bind to http://127.0.0.1 only (ports-only settings are rejected).";
    }

    // The single reading of the listen settings that both FindViolations and FreezeListenSettings use, so the text
    // checked is the text the server is given: each endpoint Url trimmed, a blank one absent; urls split on ';' into
    // trimmed, non-empty entries; and any non-blank ports-only setting refused.
    private static ListenSettings Inspect(IConfiguration configuration)
    {
        var endpoints = new List<EndpointSetting>();
        var violations = new List<string>();

        foreach (IConfigurationSection endpoint in configuration.GetSection(KestrelEndpointsSection).GetChildren())
        {
            string? url = endpoint[EndpointUrlKey]?.Trim() is { Length: > 0 } trimmed ? trimmed : null;
            endpoints.Add(new EndpointSetting(endpoint.Key, url));
            if (url is not null && !IsAllowedListenUrl(url))
            {
                violations.Add(ConfigurationPath.Combine(KestrelEndpointsSection, endpoint.Key, EndpointUrlKey));
            }
        }

        string[] urls = (configuration[UrlsKey] ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!urls.All(IsAllowedListenUrl))
        {
            violations.Add(UrlsKey);
        }

        foreach (string portsKey in PortsOnlyKeys)
        {
            if (!string.IsNullOrWhiteSpace(configuration[portsKey]))
            {
                violations.Add(portsKey);
            }
        }

        return new ListenSettings(endpoints, urls, violations);
    }

    // Reads the Kestrel section and the host listen keys from the live sources into memory in one pass, full keys
    // unchanged. The section keeps its exact shape: every key it enumerates is copied, null-valued nodes included,
    // because a settings file stores an empty object or array (an SNI host name with no settings, the "*" SNI
    // fallback) as a key with a null value, and the in-memory copy enumerates such a key as a child the same way.
    // The host listen keys are scalars and are copied only when set.
    private static IConfigurationRoot CopyListenSettings(IConfiguration configuration)
    {
        var copy = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string?> setting in configuration.GetSection(KestrelSection).AsEnumerable())
        {
            copy[setting.Key] = setting.Value;
        }

        foreach (string key in HostListenKeys)
        {
            if (configuration[key] is string value)
            {
                copy[key] = value;
            }
        }

        return new ConfigurationBuilder().AddInMemoryCollection(copy).Build();
    }

    // The Kestrel section as the server loads it, with keys relative to the section. It keeps the copy's exact shape,
    // null-valued nodes (empty objects and arrays) included, so an SNI allowlist entry or "*" fallback with no
    // settings still reaches Kestrel. Each endpoint without a Url is left out with its whole subtree, its own node
    // included, and each kept endpoint Url is replaced by its verified text.
    private static IConfigurationRoot FreezeKestrelSection(IConfiguration snapshot, ListenSettings settings)
    {
        string[] droppedEndpoints = settings.Endpoints
            .Where(endpoint => endpoint.Url is null)
            .Select(endpoint => ConfigurationPath.Combine(EndpointsKey, endpoint.Name))
            .ToArray();

        var frozen = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string?> setting in
                 snapshot.GetSection(KestrelSection).AsEnumerable(makePathsRelative: true))
        {
            if (!droppedEndpoints.Any(path => IsPathOrDescendant(setting.Key, path)))
            {
                frozen[setting.Key] = setting.Value;
            }
        }

        foreach (EndpointSetting endpoint in settings.Endpoints)
        {
            if (endpoint.Url is not null)
            {
                frozen[ConfigurationPath.Combine(EndpointsKey, endpoint.Name, EndpointUrlKey)] = endpoint.Url;
            }
        }

        return new ConfigurationBuilder().AddInMemoryCollection(frozen).Build();
    }

    // Configuration keys compare without case.
    private static bool IsPathOrDescendant(string key, string path) =>
        key.Equals(path, StringComparison.OrdinalIgnoreCase)
        || key.StartsWith(path + ConfigurationPath.KeyDelimiter, StringComparison.OrdinalIgnoreCase);

    // One child of Kestrel:Endpoints: its name, and its trimmed Url, or null when the Url is missing or blank.
    private sealed record EndpointSetting(string Name, string? Url);

    // The normalised listen settings, with the offending keys in the order FindViolations documents.
    private sealed record ListenSettings(
        IReadOnlyList<EndpointSetting> Endpoints,
        IReadOnlyList<string> Urls,
        IReadOnlyList<string> Violations)
    {
        public bool HasAddress => Urls.Count > 0 || Endpoints.Any(endpoint => endpoint.Url is not null);
    }
}
