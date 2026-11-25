namespace EpiInfo.Api.Hosting;

/// <summary>
/// Refuses to start the API unless every effective listen setting binds the IPv4 loopback address
/// <c>127.0.0.1</c> exactly. It reads configuration only, so it runs before any socket opens and a rejected
/// setting never listens.
/// </summary>
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

    /// <summary>The only host a listen URL may name.</summary>
    public const string AllowedHost = "127.0.0.1";

    private const string EndpointUrlKey = "Url";

    // Ports-only settings make the host bind every interface, whatever the port, so any non-empty value is refused.
    private static readonly string[] PortsOnlyKeys = [HttpPortsKey, HttpsPortsKey];

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

        // Keys only, never values: a configured URL can carry credentials or internal host names.
        string keys = string.Join(", ", violations.Select(key => $"'{key}'"));
        string subject = violations.Count == 1 ? $"Listen setting {keys} is" : $"Listen settings {keys} are";
        throw new InvalidOperationException(
            $"{subject} not allowed: the API must bind to http://127.0.0.1 only (ports-only settings are rejected).");
    }

    /// <summary>
    /// Returns the full configuration keys whose listen setting breaks the loopback rule: Kestrel endpoint URLs in
    /// section order, then <see cref="UrlsKey"/>, <see cref="HttpPortsKey"/> and <see cref="HttpsPortsKey"/>.
    /// A null, empty or whitespace setting counts as absent, so a configuration with no listen setting at all passes.
    /// The API therefore relies on the committed <c>appsettings.json</c> endpoint being loaded; without it Kestrel
    /// falls back to its own default, <c>http://localhost:5000</c>.
    /// </summary>
    public static IReadOnlyList<string> FindViolations(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var violations = new List<string>();

        foreach (IConfigurationSection endpoint in configuration.GetSection(KestrelEndpointsSection).GetChildren())
        {
            string? url = endpoint[EndpointUrlKey];
            if (!string.IsNullOrWhiteSpace(url) && !IsAllowedListenUrl(url))
            {
                violations.Add(ConfigurationPath.Combine(KestrelEndpointsSection, endpoint.Key, EndpointUrlKey));
            }
        }

        string? urls = configuration[UrlsKey];
        if (!string.IsNullOrWhiteSpace(urls)
            && !urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).All(IsAllowedListenUrl))
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

        return violations;
    }

    /// <summary>
    /// Whether <paramref name="url"/> is an <c>http</c> or <c>https</c> URL that Kestrel binds to <c>127.0.0.1</c>
    /// alone, on any port (<c>0</c> included). Never throws.
    /// </summary>
    public static bool IsAllowedListenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        string candidate = url.Trim();
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri))
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

        return SpellsLoopbackAuthority(candidate);
    }

    // Kestrel does not parse listen URLs with Uri. It takes the host as the text between "://" and the last ':'
    // before the first '/', and binds every interface when that text is neither "localhost" nor an IP address.
    // Uri reports host 127.0.0.1 for forms Kestrel reads otherwise ("http://user@127.0.0.1:5000",
    // "http://127.0.0.1:5000?x", "http://127.0.0.1:"), and normalises shorthand such as "http://127.1", so the URL
    // text itself must spell "127.0.0.1" or "127.0.0.1:<digits>", optionally followed by a single '/'.
    private static bool SpellsLoopbackAuthority(string url)
    {
        int schemeDelimiter = url.IndexOf(Uri.SchemeDelimiter, StringComparison.Ordinal);
        if (schemeDelimiter < 0)
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
}
