using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using EpiInfo.Api.Errors;
using EpiInfo.Api.Hosting;
using EpiInfo.Api.Tests.Infrastructure;
using EpiInfo.Domain.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace EpiInfo.Api.Tests.Hosting;

/// <summary>
/// The listen-address rule of <see cref="LoopbackBindingGuard"/>: every non-empty listen setting, from every source,
/// must bind <c>127.0.0.1</c> exactly, or the API refuses to start before any socket opens, with a message that names
/// the offending key and never its value. Real Kestrel then reports IPv4 loopback addresses only.
/// </summary>
/// <remarks>
/// No case needs SQL Server: the class carries no <c>Integration</c> trait and joins no database collection, and every
/// host it starts runs over a <see cref="FakeDataStore"/>. The cases that set listen environment variables are in
/// <see cref="LoopbackBindingEnvironmentTests"/>, the one place that changes the process environment.
/// </remarks>
public sealed class LoopbackBindingTests
{
    /// <summary>The non-parallel collection of the cases that set listen environment variables.</summary>
    public const string EnvironmentCollection = "LoopbackBindingEnvironment";

    /// <summary>The listen URL <c>appsettings.json</c> commits.</summary>
    private const string AllowedUrl = "http://127.0.0.1:5000";

    private const string LocalhostUrl = "http://localhost:5000";

    private static readonly string HttpEndpointUrlKey =
        ConfigurationPath.Combine(LoopbackBindingGuard.KestrelEndpointsSection, "Http", "Url");

    // A second named endpoint: every child of the endpoints section is checked, not only the committed one.
    private static readonly string OtherEndpointUrlKey =
        ConfigurationPath.Combine(LoopbackBindingGuard.KestrelEndpointsSection, "Other", "Url");

    // The keys that hold a listen URL, as opposed to a ports-only value.
    private static readonly string[] UrlKeys = [HttpEndpointUrlKey, OtherEndpointUrlKey, LoopbackBindingGuard.UrlsKey];

    // Every listen key in the order FindViolations reports them: endpoint URLs in section order, then urls and the
    // ports-only keys.
    private static readonly string[] AllListenKeys =
        [.. UrlKeys, LoopbackBindingGuard.HttpPortsKey, LoopbackBindingGuard.HttpsPortsKey];

    // Each rejected URL with the text that identifies its host, which a message must never carry. "*" and "+" are
    // single characters too generic to assert absent from a message, so only their full URL is checked for them.
    private static readonly (string Url, string? HostText)[] RejectedUrls =
    [
        (LocalhostUrl, "localhost"),
        ("http://[::1]:5000", "::1"),
        ("http://127.0.0.2:5000", "127.0.0.2"),
        ("http://*:5000", null),
        ("http://+:5000", null),
        ("http://0.0.0.0:5000", "0.0.0.0"),
    ];

    /// <summary>Every URL key crossed with every rejected URL and its host text.</summary>
    public static TheoryData<string, string, string?> RejectedListenUrls
    {
        get
        {
            var data = new TheoryData<string, string, string?>();
            foreach (string key in UrlKeys)
            {
                foreach ((string url, string? hostText) in RejectedUrls)
                {
                    data.Add(key, url, hostText);
                }
            }

            return data;
        }
    }

    /// <summary>The keys that hold a listen URL.</summary>
    public static TheoryData<string> UrlSettingKeys => new(UrlKeys);

    /// <summary>Every listen key with a blank value, which counts as absent.</summary>
    public static TheoryData<string, string> BlankListenSettings
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (string key in AllListenKeys)
            {
                data.Add(key, string.Empty);
                data.Add(key, "   ");
            }

            return data;
        }
    }

    /// <summary>Whether each URL, exactly as written, is an allowed listen URL.</summary>
    public static TheoryData<string, bool> ListenUrlVerdicts
    {
        get
        {
            var data = new TheoryData<string, bool> { { AllowedUrl, true }, { "http://127.0.0.1:0", true } };
            foreach ((string url, _) in RejectedUrls)
            {
                data.Add(url, false);
            }

            data.Add("http://localhost:5001", false);
            data.Add("http://0.0.0.0:5001", false);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(RejectedListenUrls))]
    public void NonLoopbackListenUrlIsRejectedByKeyWithoutItsValue(string key, string url, string? hostText)
    {
        string[] forbidden = hostText is null ? [url, ":5000"] : [url, hostText, ":5000"];

        ListenSettingAssertions.AssertRejected(Configure((key, url)), key, forbidden);
    }

    // Every ';'-separated entry of urls is checked, so one non-loopback entry rejects the whole setting, whatever the
    // surrounding whitespace the guard trims.
    [Theory]
    [InlineData("http://127.0.0.1:5000;http://localhost:5001", "localhost")]
    [InlineData("http://127.0.0.1:5000;http://0.0.0.0:5001", "0.0.0.0")]
    [InlineData("http://127.0.0.1:5000; http://localhost:5001 ", "localhost")]
    public void UrlsListWithANonLoopbackEntryIsRejected(string urls, string hostText)
    {
        ListenSettingAssertions.AssertRejected(
            Configure((LoopbackBindingGuard.UrlsKey, urls)),
            LoopbackBindingGuard.UrlsKey,
            urls,
            hostText,
            ":5001");
    }

    [Theory]
    [InlineData("http://127.0.0.1:5000;https://127.0.0.1:5001")]
    [InlineData(" http://127.0.0.1:5000 ; http://127.0.0.1:5001 ")]
    public void UrlsListOfLoopbackEntriesIsAccepted(string urls)
    {
        ListenSettingAssertions.AssertAccepted(Configure((LoopbackBindingGuard.UrlsKey, urls)));
    }

    // Ports-only settings bind every interface, whatever the port, so any non-empty value is refused.
    [Theory]
    [InlineData(LoopbackBindingGuard.HttpPortsKey, "5000")]
    [InlineData(LoopbackBindingGuard.HttpPortsKey, "5000;5001")]
    [InlineData(LoopbackBindingGuard.HttpsPortsKey, "5000")]
    [InlineData(LoopbackBindingGuard.HttpsPortsKey, "5000;5001")]
    public void PortsOnlySettingIsRejectedByKeyWithoutItsValue(string key, string ports)
    {
        ListenSettingAssertions.AssertRejected(Configure((key, ports)), key, ports, "5000");
    }

    [Theory]
    [MemberData(nameof(UrlSettingKeys))]
    public void LoopbackListenUrlIsAccepted(string key)
    {
        ListenSettingAssertions.AssertAccepted(Configure((key, AllowedUrl)));
    }

    [Theory]
    [MemberData(nameof(BlankListenSettings))]
    public void BlankListenSettingCountsAsAbsent(string key, string value)
    {
        ListenSettingAssertions.AssertAccepted(Configure((key, value)));
    }

    [Fact]
    public void ConfigurationWithoutListenSettingsHasNoViolation()
    {
        ListenSettingAssertions.AssertAccepted(Configure(("Database:CommandTimeoutSeconds", "30")));
    }

    // Validate names every offending key at once, in the documented order, and none of their values.
    [Fact]
    public void EveryOffendingKeyIsNamedAtOnce()
    {
        IConfigurationRoot configuration = Configure(
            (HttpEndpointUrlKey, LocalhostUrl),
            (OtherEndpointUrlKey, "http://0.0.0.0:5001"),
            (LoopbackBindingGuard.UrlsKey, "http://[::1]:5002"),
            (LoopbackBindingGuard.HttpPortsKey, "5003"),
            (LoopbackBindingGuard.HttpsPortsKey, "5004"));

        Assert.Equal(AllListenKeys, LoopbackBindingGuard.FindViolations(configuration));

        var exception = Assert.Throws<InvalidOperationException>(() => LoopbackBindingGuard.Validate(configuration));
        foreach (string key in AllListenKeys)
        {
            Assert.Contains(key, exception.Message, StringComparison.Ordinal);
        }

        foreach (string value in new[] { "localhost", "0.0.0.0", "::1", "5001", "5002", "5003", "5004" })
        {
            Assert.DoesNotContain(value, exception.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(ListenUrlVerdicts))]
    public void IsAllowedListenUrlAcceptsOnlyIpv4Loopback(string url, bool expected)
    {
        Assert.Equal(expected, LoopbackBindingGuard.IsAllowedListenUrl(url));
    }

    // The committed settings files, layered as the host layers them for each environment: appsettings.json, then the
    // optional appsettings.{environment}.json.
    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public void CommittedConfigurationBindsLoopbackOnly(string environment)
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .SetBasePath(RepositoryPaths.ApiProjectDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .AddJsonFile($"appsettings.{environment}.json", optional: true, reloadOnChange: false)
            .Build();

        Assert.Empty(LoopbackBindingGuard.FindViolations(configuration));
        Assert.Equal(AllowedUrl, configuration[HttpEndpointUrlKey]);
    }

    // The urls key is the configuration path ASPNETCORE_URLS, DOTNET_URLS and --urls feed; the factory passes it to
    // Program.cs on its command line.
    [Fact]
    public async Task StartFailsWhenUrlsUsesLocalhost()
    {
        var factory = new ApiFactory(new ApiFactoryOptions
        {
            DataStore = new FakeDataStore(),
            ExtraSettings = new Dictionary<string, string?> { [LoopbackBindingGuard.UrlsKey] = LocalhostUrl },
        });

        await ListenSettingAssertions.AssertStartFailsAsync(
            factory,
            () => _ = factory.Server,
            LoopbackBindingGuard.UrlsKey,
            LocalhostUrl,
            "localhost:5000");
    }

    // On the real-socket host the same rule holds for the committed endpoint key: Kestrel never binds a rejected URL.
    [Fact]
    public async Task KestrelStartFailsWhenEndpointUsesLocalhost()
    {
        const string rejectedUrl = "http://localhost:0";
        var factory = new KestrelApiFactory(ApiFactoryOptions.ForFake(new FakeDataStore()) with
        {
            ExtraSettings = new Dictionary<string, string?> { [KestrelApiFactory.ListenUrlKey] = rejectedUrl },
        });

        await ListenSettingAssertions.AssertStartFailsAsync(
            factory,
            factory.Start,
            HttpEndpointUrlKey,
            rejectedUrl,
            "localhost");
    }

    // The bound addresses IServerAddressesFeature reports are the proof: each names IPv4 loopback exactly, on the port
    // the operating system chose, and serves.
    [Fact]
    public async Task KestrelBindsOnlyIpv4Loopback()
    {
        var store = new FakeDataStore();
        await using var factory = new KestrelApiFactory(ApiFactoryOptions.ForFake(store));
        factory.Start();

        IReadOnlyList<string> addresses = factory.BoundAddresses;

        Assert.NotEmpty(addresses);
        foreach (string address in addresses)
        {
            // Uri cannot parse "*" or "+" hosts, so the raw text is checked before parsing.
            Assert.DoesNotContain("localhost", address, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("[", address, StringComparison.Ordinal);
            Assert.DoesNotContain("*", address, StringComparison.Ordinal);
            Assert.DoesNotContain("+", address, StringComparison.Ordinal);
            Assert.DoesNotContain("0.0.0.0", address, StringComparison.Ordinal);

            var uri = new Uri(address);
            Assert.Equal(LoopbackBindingGuard.AllowedHost, uri.Host);
            Assert.True(IPAddress.TryParse(uri.Host, out IPAddress? ip), $"'{address}' does not name an IP address.");
            Assert.Equal(AddressFamily.InterNetwork, ip.AddressFamily);
            Assert.Equal(IPAddress.Loopback, ip);
            Assert.NotEqual(0, uri.Port);
            Assert.True(LoopbackBindingGuard.IsAllowedListenUrl(address), $"'{address}' breaks the listen rule.");
        }

        using HttpClient client = factory.CreateKestrelClient();
        using HttpResponseMessage response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(store.Calls, call => call.Method == nameof(IDataStore.PingAsync));
    }

    private static IConfigurationRoot Configure(params (string Key, string? Value)[] settings)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, string? value) in settings)
        {
            values.Add(key, value);
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}

/// <summary>
/// The non-parallel collection of <see cref="LoopbackBindingEnvironmentTests"/>. xUnit v2 runs such a collection after
/// every parallel collection and one at a time, so no other host in the process starts while a listen environment
/// variable is set.
/// </summary>
[CollectionDefinition(LoopbackBindingTests.EnvironmentCollection, DisableParallelization = true)]
public sealed class LoopbackBindingEnvironmentCollection
{
}

/// <summary>
/// The environment variables that feed the listen keys: a non-loopback value in any of them fails the start, naming the
/// configuration key it feeds and never the value. These are the only cases that change the process environment, and
/// each restores the previous value, absence included.
/// </summary>
[Collection(LoopbackBindingTests.EnvironmentCollection)]
public sealed class LoopbackBindingEnvironmentTests
{
    [Theory]
    [InlineData("ASPNETCORE_URLS", "http://localhost:5000", LoopbackBindingGuard.UrlsKey)]
    [InlineData("DOTNET_URLS", "http://localhost:5000", LoopbackBindingGuard.UrlsKey)]
    [InlineData("ASPNETCORE_HTTP_PORTS", "5000", LoopbackBindingGuard.HttpPortsKey)]
    [InlineData("ASPNETCORE_HTTPS_PORTS", "5001", LoopbackBindingGuard.HttpsPortsKey)]
    public async Task StartFailsWhenEnvironmentSetsNonLoopbackListenSetting(string variable, string value, string key)
    {
        string? previous = Environment.GetEnvironmentVariable(variable);
        Environment.SetEnvironmentVariable(variable, value);
        try
        {
            var factory = new ApiFactory(ApiFactoryOptions.ForFake(new FakeDataStore()));
            await ListenSettingAssertions.AssertStartFailsAsync(factory, () => _ = factory.Server, key, value);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }
}

/// <summary>
/// The per-request authority check of <see cref="LoopbackBindingGuard"/>: the API listens on 127.0.0.1 only, and it
/// also answers a request whose <c>Host</c> is not 127.0.0.1 or localhost, as a DNS-rebound name would be, with the
/// request-level 400 before routing, an endpoint, the OpenAPI document or a client file runs.
/// </summary>
/// <remarks>
/// The class needs no database: it carries no <c>Integration</c> trait and joins no database collection, and each
/// case hosts the API over a <see cref="FakeDataStore"/> on TestServer. That host is given its own client folder
/// holding <c>index.html</c> and <c>assets/app.js</c>, so the static client is there to be served and the rejection
/// is shown on files that exist.
/// </remarks>
public sealed class LoopbackHostGuardTests : IDisposable
{
    private const string RejectedHostName = "evil.example";

    private const string RejectedAuthority = RejectedHostName + ":5000";

    private const string AssetPath = "assets/app.js";

    private const string IndexContent = "<!doctype html><title>host guard client</title>";

    private const string AssetContent = "console.log('host guard client');";

    private readonly DirectoryInfo _clientFolder;

    /// <summary>Writes the client folder this case's host serves, in a new temporary directory.</summary>
    public LoopbackHostGuardTests()
    {
        _clientFolder = Directory.CreateTempSubdirectory("epiinfo-host-guard-");
        File.WriteAllText(Path.Join(_clientFolder.FullName, ClientAppHosting.IndexFileName), IndexContent);
        Directory.CreateDirectory(Path.Join(_clientFolder.FullName, "assets"));
        File.WriteAllText(Path.Join(_clientFolder.FullName, AssetPath), AssetContent);
    }

    /// <summary>Removes the client folder once the case's host has stopped.</summary>
    public void Dispose() => _clientFolder.Delete(recursive: true);

    // Only the host part counts, case-insensitively and with any port; an empty Host, another name or address, a name
    // that merely starts or ends with an allowed one, a trailing dot and user information all fail. HostString wraps
    // a bare "::1" in brackets and keeps "evil@localhost" whole, so neither reaches an allowed name.
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.0.0.1:5000", true)]
    [InlineData("localhost", true)]
    [InlineData("localhost:5173", true)]
    [InlineData("LOCALHOST:5000", true)]
    [InlineData("", false)]
    [InlineData("evil.example", false)]
    [InlineData("evil.example:5000", false)]
    [InlineData("127.0.0.1.evil.example", false)]
    [InlineData("localhost.evil.example", false)]
    [InlineData("localhost.", false)]
    [InlineData("[::1]:5000", false)]
    [InlineData("::1", false)]
    [InlineData("127.0.0.2:5000", false)]
    [InlineData("0.0.0.0:5000", false)]
    [InlineData("evil@localhost", false)]
    public void IsAllowedRequestHostAllowsOnlyTheLoopbackNames(string host, bool expected)
    {
        Assert.Equal(expected, LoopbackBindingGuard.IsAllowedRequestHost(new HostString(host)));
    }

    // The reported exposure: a DNS-rebound name reached the client page, its assets and every route. Each path is
    // refused with the fixed request-level ProblemDetails, which never echoes the Host it refused.
    [Theory]
    [InlineData("/")]
    [InlineData("/" + AssetPath)]
    [InlineData("/api/forms")]
    [InlineData("/health")]
    [InlineData("/openapi/v1.json")]
    public async Task RequestNamingAnotherHostIsRefusedBeforeAnythingIsServed(string path)
    {
        await using ApiFactory factory = CreateFactory();
        using HttpClient client = factory.CreateClient();

        ReceivedResponse response = await GetAsync(client, path, RejectedAuthority);

        Assert.Equal(HttpStatusCode.BadRequest, response.Status);
        Assert.Equal(ApiProblems.ProblemContentType, response.MediaType);

        using JsonDocument document = JsonDocument.Parse(response.Text);
        JsonElement body = ApiTestClient.AssertProblemDetails(document.RootElement, StatusCodes.Status400BadRequest);
        Assert.Equal(ApiProblems.HostNotAllowedDetail, body.GetProperty("detail").GetString());
        Assert.False(body.TryGetProperty("errors", out _), $"A request-level 400 has no errors: {response.Text}");
        Assert.DoesNotContain(RejectedHostName, response.Text, StringComparison.OrdinalIgnoreCase);
    }

    // The names the documented clients send: the WebApplicationFactory default and the E2E_BASE_URL default
    // (localhost), the committed listen address, the Vite dev origin its proxy forwards unchanged, and a Host in
    // another case. Each gets the client and its asset, and /api is not refused for its Host; the API routes are
    // not asserted further here.
    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1:5000")]
    [InlineData("localhost:5173")]
    [InlineData("LOCALHOST")]
    public async Task RequestNamingALoopbackHostIsServed(string authority)
    {
        await using ApiFactory factory = CreateFactory();
        using HttpClient client = factory.CreateClient();

        ReceivedResponse page = await GetAsync(client, "/", authority);
        Assert.Equal(HttpStatusCode.OK, page.Status);
        Assert.Equal(IndexContent, page.Text);

        ReceivedResponse asset = await GetAsync(client, "/" + AssetPath, authority);
        Assert.Equal(HttpStatusCode.OK, asset.Status);
        Assert.Equal(AssetContent, asset.Text);

        ReceivedResponse api = await GetAsync(client, "/api/forms", authority);
        Assert.DoesNotContain(ApiProblems.HostNotAllowedDetail, api.Text, StringComparison.Ordinal);
    }

    // Sent with an explicit Host header, which TestServer passes to the request as given instead of deriving it from
    // the client's base address, so a Host in any case reaches the guard unchanged.
    private static async Task<ReceivedResponse> GetAsync(HttpClient client, string path, string authority)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Host = authority;

        using HttpResponseMessage response = await client.SendAsync(request);
        string text = await response.Content.ReadAsStringAsync();
        return new ReceivedResponse(response.StatusCode, response.Content.Headers.ContentType?.MediaType, text);
    }

    private ApiFactory CreateFactory() =>
        new(ApiFactoryOptions.ForFake(new FakeDataStore()) with
        {
            ExtraSettings = new Dictionary<string, string?> { [ApiOptions.ClientPathKey] = _clientFolder.FullName },
        });

    // What a case reads from a response, taken before the response is disposed.
    private sealed record ReceivedResponse(HttpStatusCode Status, string? MediaType, string Text);
}

/// <summary>The assertions every listen-setting case shares.</summary>
file static class ListenSettingAssertions
{
    /// <summary>
    /// Asserts that <paramref name="key"/> is the only violation of <paramref name="configuration"/> and that
    /// <see cref="LoopbackBindingGuard.Validate"/> throws a message naming it and holding none of
    /// <paramref name="forbiddenTexts"/>.
    /// </summary>
    public static void AssertRejected(IConfiguration configuration, string key, params string[] forbiddenTexts)
    {
        Assert.Equal(new[] { key }, LoopbackBindingGuard.FindViolations(configuration));

        var exception = Assert.Throws<InvalidOperationException>(() => LoopbackBindingGuard.Validate(configuration));
        Assert.Contains(key, exception.Message, StringComparison.Ordinal);
        AssertCarriesNone(exception, forbiddenTexts);
    }

    /// <summary>Asserts that <paramref name="configuration"/> has no violation and passes the start check.</summary>
    public static void AssertAccepted(IConfiguration configuration)
    {
        Assert.Empty(LoopbackBindingGuard.FindViolations(configuration));
        Assert.Null(Record.Exception(() => LoopbackBindingGuard.Validate(configuration)));
    }

    /// <summary>
    /// Runs <paramref name="start"/>, then disposes <paramref name="factory"/>, and asserts that the start failed with
    /// the guard's <see cref="InvalidOperationException"/> naming <paramref name="key"/>, and that no exception the
    /// failure carries holds any of <paramref name="forbiddenTexts"/>.
    /// </summary>
    public static async Task AssertStartFailsAsync(
        ApiFactoryBase factory,
        Action start,
        string key,
        params string[] forbiddenTexts)
    {
        Exception? failure;
        try
        {
            failure = Record.Exception(start);
        }
        finally
        {
            await factory.DisposeAsync();
        }

        Assert.NotNull(failure);
        IReadOnlyList<Exception> chain = Flatten(failure);
        Assert.Contains(
            chain,
            exception => exception is InvalidOperationException
                && exception.Message.Contains(key, StringComparison.Ordinal));
        foreach (Exception exception in chain)
        {
            AssertCarriesNone(exception, forbiddenTexts);
        }
    }

    private static void AssertCarriesNone(Exception exception, string[] forbiddenTexts)
    {
        foreach (string text in forbiddenTexts)
        {
            Assert.DoesNotContain(text, exception.Message, StringComparison.Ordinal);
        }
    }

    // The exception and every exception it wraps: each inner exception, and each member of an aggregate.
    private static IReadOnlyList<Exception> Flatten(Exception root)
    {
        var chain = new List<Exception>();
        var pending = new Stack<Exception>();
        pending.Push(root);
        while (pending.TryPop(out Exception? current))
        {
            chain.Add(current);
            if (current is AggregateException aggregate)
            {
                foreach (Exception inner in aggregate.InnerExceptions)
                {
                    pending.Push(inner);
                }
            }
            else if (current.InnerException is { } inner)
            {
                pending.Push(inner);
            }
        }

        return chain;
    }
}
