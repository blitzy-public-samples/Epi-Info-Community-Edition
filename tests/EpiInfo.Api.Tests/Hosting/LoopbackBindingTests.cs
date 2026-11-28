using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using EpiInfo.Api.Errors;
using EpiInfo.Api.Hosting;
using EpiInfo.Api.Tests.Infrastructure;
using EpiInfo.Domain.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

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

    // The listen URL a real-Kestrel test host must request: IPv4 loopback on port 0, so the operating system picks a
    // free port. Written here rather than read from the factory, so a factory that requests another URL fails.
    private const string EphemeralLoopbackUrl = "http://127.0.0.1:0";

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
    // the operating system chose, and serves. Two hosts run at once, as concurrent test classes run them: each requests
    // port 0, gets a port of its own that is not the committed one, and answers /health from its own store.
    [Fact]
    public async Task KestrelBindsOnlyIpv4Loopback()
    {
        var firstStore = new FakeDataStore();
        var secondStore = new FakeDataStore();
        await using var first = new KestrelApiFactory(ApiFactoryOptions.ForFake(firstStore));
        await using var second = new KestrelApiFactory(ApiFactoryOptions.ForFake(secondStore));

        // Both listen before either is examined, so every assertion below holds while the other host is alive.
        first.Start();
        second.Start();

        IReadOnlySet<int> firstPorts = AssertListensOnEphemeralIpv4LoopbackOnly(first);
        IReadOnlySet<int> secondPorts = AssertListensOnEphemeralIpv4LoopbackOnly(second);
        Assert.False(
            firstPorts.Overlaps(secondPorts),
            $"Two hosts alive at once share port {string.Join(", ", firstPorts.Intersect(secondPorts))}.");

        // Each request reaches its own host's socket and store: the other store records no ping for it.
        await AssertServesHealthAsync(first, firstPorts);
        Assert.Single(firstStore.Calls, IsPing);
        Assert.DoesNotContain(secondStore.Calls, IsPing);

        await AssertServesHealthAsync(second, secondPorts);
        Assert.Single(secondStore.Calls, IsPing);
        Assert.Single(firstStore.Calls, IsPing);
    }

    // Asserts that the started host's effective configuration requests the ephemeral loopback URL, and that every
    // address it bound names IPv4 loopback exactly, on a port the operating system chose other than the committed one.
    // Returns those ports.
    private static IReadOnlySet<int> AssertListensOnEphemeralIpv4LoopbackOnly(KestrelApiFactory factory)
    {
        IConfiguration configuration = factory.Services.GetRequiredService<IConfiguration>();
        Assert.Equal(EphemeralLoopbackUrl, configuration[HttpEndpointUrlKey]);

        int committedPort = new Uri(AllowedUrl).Port;
        IReadOnlyList<string> addresses = factory.BoundAddresses;
        Assert.NotEmpty(addresses);

        var ports = new HashSet<int>();
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
            Assert.NotEqual(committedPort, uri.Port);
            Assert.True(LoopbackBindingGuard.IsAllowedListenUrl(address), $"'{address}' breaks the listen rule.");
            ports.Add(uri.Port);
        }

        return ports;
    }

    // GET /health through the host's own socket client, whose base address must be one of the host's bound ports.
    private static async Task AssertServesHealthAsync(KestrelApiFactory factory, IReadOnlySet<int> ports)
    {
        using HttpClient client = factory.CreateKestrelClient();
        Assert.NotNull(client.BaseAddress);
        Assert.Contains(client.BaseAddress.Port, ports);

        using HttpResponseMessage response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static bool IsPing(FakeStoreCall call) => call.Method == nameof(IDataStore.PingAsync);

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

    // The guard answers before routing, so no operation lists its 400: the contract states the Host precondition once,
    // in info.description, and the document keeps its title.
    [Fact]
    public async Task DocumentStatesTheHostPreconditionInItsDescription()
    {
        await using ApiFactory factory = CreateFactory();
        using HttpClient client = factory.CreateClient();

        ReceivedResponse response = await GetAsync(client, "/openapi/v1.json", "127.0.0.1:5000");
        Assert.Equal(HttpStatusCode.OK, response.Status);

        using JsonDocument document = JsonDocument.Parse(response.Text);
        JsonElement info = document.RootElement.GetProperty("info");
        Assert.Equal("EpiInfo.Api | v1", info.GetProperty("title").GetString());

        string description = info.GetProperty("description").GetString() ?? string.Empty;
        Assert.Contains(LoopbackBindingGuard.AllowedHost, description, StringComparison.Ordinal);
        Assert.Contains(LoopbackBindingGuard.LocalhostName, description, StringComparison.Ordinal);
        Assert.Contains(ApiProblems.HostNotAllowedDetail, description, StringComparison.Ordinal);
        Assert.Contains("400", description, StringComparison.Ordinal);
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

/// <summary>
/// The client copy of <see cref="ClientAppHosting"/>: the host reads its client folder into memory once, at startup,
/// and serves only that copy, so no link, edit, addition or deletion made afterwards changes what is served. Links
/// below the folder, the server's own paths and the files a physical provider hides are left out, and a folder over
/// the limits, or outside Windows a folder that every user can write or that holds a folder or file every user can
/// write, serves no client and logs one fixed warning. The copy is sent with the Cache-Control of its path, and its
/// text, like the API's JSON, compressed for a client that accepts br or gzip. Every 200, HEAD and 304 of a file of a
/// coded type varies by Accept-Encoding, and one sent, or whose 200 would be sent, in a coding carries the weak form
/// of the file's entity tag, which revalidates through If-None-Match but authorizes no If-Range or If-Match.
/// </summary>
/// <remarks>
/// The class needs no database: it carries no <c>Integration</c> trait and joins no database collection, and each case
/// hosts the API over a <see cref="FakeDataStore"/> on TestServer. Each case has its own temporary directory holding
/// the client folder and, beside it, a folder outside the client that holds a secret. A case that needs a symbolic
/// link returns early where the operating system refuses to create one for want of the privilege, as Windows does
/// outside Developer Mode; the cases of the Linux handle check return early on other systems, which report no
/// handle's path; and the cases of file modes return early on Windows, which the host does not check.
/// </remarks>
public sealed class ClientAppSnapshotTests : IDisposable
{
    private const string AssetPath = "assets/app.js";

    private const string ImagePath = "assets/logo.png";

    private const string IndexContent = "<!doctype html><title>snapshot client</title>";

    private const string AssetContent = "console.log('snapshot client');";

    private const string SecretContent = "secret outside the client folder";

    private const string SecretFile = "secret.js";

    // A root file outside assets/, which a case writes when it needs one.
    private const string RootFilePath = "robots.txt";

    private const string RootFileContent = "User-agent: *";

    // The lifetime of a content-hashed asset: any cache may keep it for a year and never revalidate it.
    private const string AssetCacheControl = "public, max-age=31536000, immutable";

    // 0775 and 0664: the owner and the group may write, and other users may only read and enter a folder.
    private const UnixFileMode GroupWritableFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite
        | UnixFileMode.OtherRead;

    private const UnixFileMode GroupWritableFolderMode =
        GroupWritableFileMode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    // Bytes that are not valid UTF-8, so an exact match shows that nothing decoded or re-encoded the file.
    private static readonly byte[] ImageContent = [0x89, 0x50, 0x4E, 0x47, 0x00, 0xFF, 0xFE, 0x80, 0x0D, 0x0A];

    private readonly DirectoryInfo _temp;

    private readonly string _client;

    private readonly string _outside;

    /// <summary>Writes the client folder and the outside folder, in a new temporary directory.</summary>
    public ClientAppSnapshotTests()
    {
        _temp = Directory.CreateTempSubdirectory("epiinfo-client-copy-");
        _client = Path.Join(_temp.FullName, "client");
        _outside = Path.Join(_temp.FullName, "outside");
        WriteFile(_client, ClientAppHosting.IndexFileName, IndexContent);
        WriteFile(_client, AssetPath, AssetContent);
        File.WriteAllBytes(Path.Join(_client, ImagePath), ImageContent);
        WriteFile(_outside, SecretFile, SecretContent);
    }

    /// <summary>Removes the temporary directory; a recursive delete removes each link itself, never its target.</summary>
    public void Dispose() => _temp.Delete(recursive: true);

    // The page, a client route answered with it, a script and a binary image: each response is exactly the file's
    // bytes as they were at startup.
    [Theory]
    [InlineData("/", ClientAppHosting.IndexFileName)]
    [InlineData("/" + ClientAppHosting.IndexFileName, ClientAppHosting.IndexFileName)]
    [InlineData("/forms/1", ClientAppHosting.IndexFileName)]
    [InlineData("/" + AssetPath, AssetPath)]
    [InlineData("/" + ImagePath, ImagePath)]
    public async Task ServesTheExactBytesOfTheFile(string path, string file)
    {
        byte[] expected = await File.ReadAllBytesAsync(Path.Join(_client, file));
        await using ApiFactory factory = CreateFactory();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, await response.Content.ReadAsByteArrayAsync());
    }

    // Links present at startup: a file link and a folder link to the outside folder, a link inside assets/, and a link
    // whose target stays inside the client folder. None is served, and the rest of the client still is.
    [Theory]
    [InlineData("linked.js", "outside/" + SecretFile, false, "/linked.js")]
    [InlineData("linked", "outside", true, "/linked/" + SecretFile)]
    [InlineData("assets/linked.js", "outside/" + SecretFile, false, "/assets/linked.js")]
    [InlineData("assets/alias.js", "client/" + AssetPath, false, "/assets/alias.js")]
    public async Task LinkPresentAtStartupIsNotServed(string link, string target, bool linksFolder, string path)
    {
        if (!TryCreateSymbolicLink(Path.Join(_client, link), Path.Join(_temp.FullName, target), linksFolder))
        {
            return;
        }

        await using ApiFactory factory = CreateFactory();
        using HttpClient client = factory.CreateClient();

        ReceivedResponse linked = await GetAsync(client, path);
        Assert.Equal(HttpStatusCode.NotFound, linked.Status);
        Assert.DoesNotContain(SecretContent, linked.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(AssetContent, linked.Text, StringComparison.Ordinal);

        ReceivedResponse asset = await GetAsync(client, "/" + AssetPath);
        Assert.Equal(HttpStatusCode.OK, asset.Status);
        Assert.Equal(AssetContent, asset.Text);
    }

    // The copy leaves links out, so an index.html that is one leaves the folder without its page: the missing-client
    // warning, and no client, as for a folder never built.
    [Fact]
    public async Task IndexThatIsALinkCountsAsMissing()
    {
        string index = Path.Join(_client, ClientAppHosting.IndexFileName);
        string outsideIndex = Path.Join(_outside, ClientAppHosting.IndexFileName);
        File.Move(index, outsideIndex);
        if (!TryCreateSymbolicLink(index, outsideIndex, linksFolder: false))
        {
            return;
        }

        await using ApiFactory factory = CreateFactory();
        using HttpClient client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(client, "/")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(client, "/" + AssetPath)).Status);
        CapturedLogEntry warning = AssertOneClientWarning(factory);
        Assert.Equal(1, warning.EventId.Id);
        Assert.Equal("ClientAppMissing", warning.EventId.Name);
    }

    // After startup the folder is never read again: an asset replaced by a link to a secret outside, its folder
    // replaced by a link to the outside folder, an asset edited, a file added and an asset deleted. The asset keeps its
    // startup bytes, and a file that was not in the folder at startup stays a 404.
    [Theory]
    [InlineData("asset replaced by a link")]
    [InlineData("folder replaced by a link")]
    [InlineData("asset edited")]
    [InlineData("file added")]
    [InlineData("asset deleted")]
    public async Task ChangeAfterStartupChangesNothingServed(string change)
    {
        await using ApiFactory factory = CreateFactory();
        using HttpClient client = factory.CreateClient();
        Assert.Equal(AssetContent, (await GetAsync(client, "/" + AssetPath)).Text);

        string assets = Path.Join(_client, "assets");
        string asset = Path.Join(_client, AssetPath);
        switch (change)
        {
            case "asset replaced by a link":
                File.Delete(asset);
                if (!TryCreateSymbolicLink(asset, Path.Join(_outside, SecretFile), linksFolder: false))
                {
                    return;
                }

                break;

            case "folder replaced by a link":
                Directory.Move(assets, Path.Join(_temp.FullName, "moved-assets"));
                WriteFile(_outside, "app.js", SecretContent);
                if (!TryCreateSymbolicLink(assets, _outside, linksFolder: true))
                {
                    return;
                }

                break;

            case "asset edited":
                await File.WriteAllTextAsync(asset, SecretContent);
                break;

            case "file added":
                await File.WriteAllTextAsync(Path.Join(assets, SecretFile), SecretContent);
                break;

            case "asset deleted":
                File.Delete(asset);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(change), change, "Not a change this case makes.");
        }

        ReceivedResponse served = await GetAsync(client, "/" + AssetPath);
        Assert.Equal(HttpStatusCode.OK, served.Status);
        Assert.Equal(AssetContent, served.Text);

        ReceivedResponse added = await GetAsync(client, "/assets/" + SecretFile);
        Assert.Equal(HttpStatusCode.NotFound, added.Status);
        Assert.DoesNotContain(SecretContent, added.Text, StringComparison.Ordinal);
    }

    // The server's own paths, in any case, and the files a physical provider hides: a file under api/ or openapi/, a
    // root file named health, a dot-file at the root and one in a folder. None is served: the health check answers
    // /health as it does without the file, and every other path is the 404 it is without the file.
    [Theory]
    [InlineData("api/forms.json", "/api/forms.json", HttpStatusCode.NotFound)]
    [InlineData("API/forms.json", "/API/forms.json", HttpStatusCode.NotFound)]
    [InlineData("openapi/client.json", "/openapi/client.json", HttpStatusCode.NotFound)]
    [InlineData("health", "/health", HttpStatusCode.OK)]
    [InlineData("Health", "/Health", HttpStatusCode.OK)]
    [InlineData(".env", "/.env", HttpStatusCode.NotFound)]
    [InlineData("assets/.hidden.js", "/assets/.hidden.js", HttpStatusCode.NotFound)]
    public async Task ServerPathOrHiddenFileIsNotServed(string file, string path, HttpStatusCode expected)
    {
        const string Marker = "a client-folder file that must not be served";
        WriteFile(_client, file, Marker);
        await using ApiFactory factory = CreateFactory();
        using HttpClient client = factory.CreateClient();

        ReceivedResponse response = await GetAsync(client, path);

        Assert.Equal(expected, response.Status);
        Assert.DoesNotContain(Marker, response.Text, StringComparison.Ordinal);
        Assert.Equal(AssetContent, (await GetAsync(client, "/" + AssetPath)).Text);
    }

    // The middleware streams a file of the copy from memory, so HEAD answers its length without a body and a range
    // request answers exactly the bytes asked for.
    [Fact]
    public async Task HeadAndRangeRequestsAreAnsweredFromTheCopy()
    {
        await using ApiFactory factory = CreateFactory();
        using HttpClient client = factory.CreateClient();

        using (var head = new HttpRequestMessage(HttpMethod.Head, "/" + AssetPath))
        using (HttpResponseMessage response = await client.SendAsync(head))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal<long?>(AssetContent.Length, response.Content.Headers.ContentLength);
            Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        }

        using var range = new HttpRequestMessage(HttpMethod.Get, "/" + AssetPath);
        range.Headers.Range = new RangeHeaderValue(8, 11);
        using HttpResponseMessage partial = await client.SendAsync(range);

        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.Equal(new ContentRangeHeaderValue(8, 11, AssetContent.Length), partial.Content.Headers.ContentRange);
        Assert.Equal(AssetContent.Substring(8, 4), await partial.Content.ReadAsStringAsync());
    }

    // The page is revalidated on every load, under its own path and every client route the fallback answers with it;
    // a file under assets/, whose name Vite derives from its content, is kept for a year and never revalidated; and
    // any other file keeps the static-file defaults, which set no Cache-Control.
    [Theory]
    [InlineData("/", "no-cache")]
    [InlineData("/" + ClientAppHosting.IndexFileName, "no-cache")]
    [InlineData("/forms/1", "no-cache")]
    [InlineData("/" + AssetPath, AssetCacheControl)]
    [InlineData("/" + ImagePath, AssetCacheControl)]
    [InlineData("/" + RootFilePath, null)]
    public async Task CacheControlFollowsThePathOfTheFile(string path, string? expected)
    {
        WriteFile(_client, RootFilePath, RootFileContent);
        await using ApiFactory factory = CreateFactory();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, RawCacheControl(response));
    }

    // A conditional request with the ETag a first answer gave gets the 304, which carries the same Cache-Control, so
    // the browser's stored copy keeps the lifetime its path calls for.
    [Theory]
    [InlineData("/" + AssetPath, AssetCacheControl)]
    [InlineData("/", "no-cache")]
    public async Task RevalidationKeepsTheCacheControlOfThePath(string path, string expected)
    {
        await using ApiFactory factory = CreateFactory();
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage first = await client.GetAsync(path);
        EntityTagHeaderValue etag = Assert.IsType<EntityTagHeaderValue>(first.Headers.ETag);

        using var revalidation = new HttpRequestMessage(HttpMethod.Get, path);
        revalidation.Headers.IfNoneMatch.Add(etag);
        using HttpResponseMessage response = await client.SendAsync(revalidation);

        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
        Assert.Equal(expected, RawCacheControl(response));
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    // A 412 answers the request's If-Match and a 416 its Range, not the file, so neither may be kept as the asset.
    [Theory]
    [InlineData("If-Match", "\"not-the-asset\"", HttpStatusCode.PreconditionFailed)]
    [InlineData("Range", "bytes=1000-2000", HttpStatusCode.RequestedRangeNotSatisfiable)]
    public async Task AssetAnswerToTheRequestHeadersCarriesNoLifetime(
        string header,
        string value,
        HttpStatusCode expected)
    {
        await using ApiFactory factory = CreateFactory();
        using HttpClient client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/" + AssetPath);
        Assert.True(request.Headers.TryAddWithoutValidation(header, value), $"The {header} header was not added.");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(expected, response.StatusCode);
        Assert.Null(RawCacheControl(response));
    }

    // A script, the page and the API's JSON: without Accept-Encoding each is sent as it is, a file with its
    // Content-Length; with br or gzip accepted each is sent in that coding, varying by Accept-Encoding and without the
    // identity length, decodes to exactly the identity bytes, and keeps the hardening headers and its path's
    // Cache-Control.
    [Theory]
    [InlineData("/" + AssetPath, "br", AssetCacheControl, true)]
    [InlineData("/" + AssetPath, "gzip", AssetCacheControl, true)]
    [InlineData("/", "br", "no-cache", true)]
    [InlineData("/", "gzip", "no-cache", true)]
    [InlineData("/api/forms", "br", "no-store", false)]
    [InlineData("/api/forms", "gzip", "no-store", false)]
    public async Task TextResponseIsCompressedForAClientThatAcceptsIt(
        string path,
        string encoding,
        string cacheControl,
        bool isFile)
    {
        await using ApiFactory factory = new(
            ClientOptions(_client) with { DataStore = FakeDataStore.WithStandardForms() });
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage identity = await client.GetAsync(path);
        byte[] identityBytes = await identity.Content.ReadAsByteArrayAsync();
        Assert.Equal(HttpStatusCode.OK, identity.StatusCode);
        Assert.Empty(identity.Content.Headers.ContentEncoding);
        Assert.Equal(
            isFile ? identityBytes.Length.ToString(CultureInfo.InvariantCulture) : null,
            RawHeader(identity, "Content-Length"));
        Assert.NotEmpty(identityBytes);

        using HttpResponseMessage encoded = await SendAcceptingAsync(client, path, encoding);

        await AssertEncodedAsync(encoded, encoding, identityBytes, cacheControl);
    }

    // On real Kestrel the coded script reaches the socket as it does on TestServer: in the coding asked for, varying by
    // Accept-Encoding, with the hardening headers and the asset lifetime, and decoding to exactly the file.
    [Theory]
    [InlineData("br")]
    [InlineData("gzip")]
    public async Task KestrelSendsTheScriptCompressed(string encoding)
    {
        await using var factory = new KestrelApiFactory(ClientOptions(_client));
        using HttpClient client = factory.CreateKestrelClient();

        using HttpResponseMessage encoded = await SendAcceptingAsync(client, "/" + AssetPath, encoding);

        await AssertEncodedAsync(encoded, encoding, Encoding.UTF8.GetBytes(AssetContent), AssetCacheControl);
    }

    // An image, whose type is outside the compressed types, and a range answer, whose Content-Range counts identity
    // bytes, are sent to a client that accepts a coding exactly as to one that does not: the same identity bytes, the
    // same strong entity tag and no Vary, because neither answer depends on Accept-Encoding.
    [Theory]
    [InlineData("/" + ImagePath, null, "br")]
    [InlineData("/" + ImagePath, null, "gzip")]
    [InlineData("/" + AssetPath, "bytes=8-11", "br")]
    [InlineData("/" + AssetPath, "bytes=8-11", "gzip")]
    public async Task ImageAndRangeAnswersAreNeverCompressed(string path, string? range, string encoding)
    {
        await using ApiFactory factory = CreateFactory();
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage identityRequest = Request(path, range, acceptEncoding: null);
        using HttpResponseMessage identity = await client.SendAsync(identityRequest);
        byte[] identityBytes = await identity.Content.ReadAsByteArrayAsync();

        using HttpRequestMessage request = Request(path, range, encoding);
        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(range is null ? HttpStatusCode.OK : HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Empty(response.Content.Headers.ContentEncoding);
        Assert.Equal(RawHeader(identity, "Content-Range"), RawHeader(response, "Content-Range"));
        Assert.Equal(identityBytes, await response.Content.ReadAsByteArrayAsync());
        byte[] expected = range is null ? ImageContent : Encoding.UTF8.GetBytes(AssetContent.Substring(8, 4));
        Assert.Equal(expected, identityBytes);
        Assert.Equal(expected.Length.ToString(CultureInfo.InvariantCulture), RawHeader(identity, "Content-Length"));
        Assert.Equal(RawHeader(identity, "Content-Length"), RawHeader(response, "Content-Length"));
        Assert.False(Assert.IsType<EntityTagHeaderValue>(identity.Headers.ETag).IsWeak);
        Assert.Equal(RawHeader(identity, "ETag"), RawHeader(response, "ETag"));
        Assert.Null(RawHeader(identity, "Vary"));
        Assert.Null(RawHeader(response, "Vary"));
    }

    // An error body, written by status-code pages outside the compression and of a type it leaves alone, is the plain
    // fixed ProblemDetails whatever coding the client accepts.
    [Theory]
    [InlineData("/api/nope")]
    [InlineData("/assets/missing.js")]
    public async Task ErrorBodyIsNotCompressed(string path)
    {
        await using ApiFactory factory = CreateFactory();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await SendAcceptingAsync(client, path, "br, gzip");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(response.Content.Headers.ContentEncoding);
        Assert.Equal(ApiProblems.ProblemContentType, response.Content.Headers.ContentType?.MediaType);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        ApiTestClient.AssertProblemDetails(document.RootElement, StatusCodes.Status404NotFound);
    }

    // Without Accept-Encoding the script, the page and a client route are sent as they are, but a client that accepts
    // br or gzip gets them coded, so their identity 200 and HEAD vary by Accept-Encoding, once, and keep the strong tag
    // of the identity bytes; an image, whose type is never coded, carries no Vary.
    [Theory]
    [InlineData("GET", "/" + AssetPath, true)]
    [InlineData("HEAD", "/" + AssetPath, true)]
    [InlineData("GET", "/", true)]
    [InlineData("HEAD", "/", true)]
    [InlineData("GET", "/forms/1", true)]
    [InlineData("GET", "/" + ImagePath, false)]
    [InlineData("HEAD", "/" + ImagePath, false)]
    public async Task IdentityAnswerVariesByAcceptEncodingWhereItsTypeIsCoded(string method, string path, bool varies)
    {
        await using ApiFactory factory = CreateFactory();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await SendWithHeadersAsync(client, new HttpMethod(method), path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(response.Content.Headers.ContentEncoding);
        Assert.Equal(varies ? "Accept-Encoding" : null, RawHeader(response, "Vary"));
        Assert.False(Assert.IsType<EntityTagHeaderValue>(response.Headers.ETag).IsWeak);
    }

    // Each revalidation: a path, its Cache-Control, the form of the tag the client sends and the coding it accepts.
    public static TheoryData<string, string, string, string?> Revalidations()
    {
        var data = new TheoryData<string, string, string, string?>();
        (string Path, string CacheControl)[] files = [("/" + AssetPath, AssetCacheControl), ("/", "no-cache")];
        foreach ((string path, string cacheControl) in files)
        {
            foreach (string sent in new[] { "strong", "weak", "weak among others" })
            {
                foreach (string? encoding in new[] { "br", "gzip", null })
                {
                    data.Add(path, cacheControl, sent, encoding);
                }
            }
        }

        return data;
    }

    // If-None-Match compares weakly, so the strong tag of the identity bytes, its weak form, which a coded answer gave,
    // and that weak form listed after another tag each revalidate the file with a 304. The 304 carries what its 200
    // would: Vary: Accept-Encoding, once, the weak tag where the 200 would be coded and the strong tag where it would
    // not, and the Cache-Control of its path.
    [Theory]
    [MemberData(nameof(Revalidations))]
    public async Task RevalidationCarriesTheVaryAndTheTagOfTheAnswerItStandsFor(
        string path,
        string cacheControl,
        string sent,
        string? encoding)
    {
        await using ApiFactory factory = CreateFactory();
        using HttpClient client = factory.CreateClient();
        string tag = await IdentityTagAsync(client, path);
        string ifNoneMatch = sent switch
        {
            "strong" => tag,
            "weak" => "W/" + tag,
            "weak among others" => "\"not-the-file\", W/" + tag,
            _ => throw new ArgumentOutOfRangeException(nameof(sent), sent, "Not a tag form this case sends."),
        };

        using HttpResponseMessage response = await SendWithHeadersAsync(
            client,
            HttpMethod.Get,
            path,
            ("Accept-Encoding", encoding),
            ("If-None-Match", ifNoneMatch));

        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
        Assert.Equal("Accept-Encoding", RawHeader(response, "Vary"));
        Assert.Equal(encoding is null ? tag : "W/" + tag, RawHeader(response, "ETag"));
        Assert.Equal(cacheControl, RawCacheControl(response));
        Assert.Empty(response.Content.Headers.ContentEncoding);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    // A 200 or HEAD sent in br or gzip, of the script, the page or a client route, carries the weak form of the tag of
    // the identity bytes, so its tag never claims to name the identity bytes, and Vary: Accept-Encoding once.
    [Theory]
    [InlineData("GET", "/" + AssetPath, "br")]
    [InlineData("GET", "/" + AssetPath, "gzip")]
    [InlineData("HEAD", "/" + AssetPath, "br")]
    [InlineData("HEAD", "/" + AssetPath, "gzip")]
    [InlineData("GET", "/", "br")]
    [InlineData("HEAD", "/", "gzip")]
    [InlineData("GET", "/forms/1", "gzip")]
    [InlineData("HEAD", "/forms/1", "br")]
    public async Task CodedAnswerCarriesTheWeakTagOfTheFile(string method, string path, string encoding)
    {
        await using ApiFactory factory = CreateFactory();
        using HttpClient client = factory.CreateClient();
        string tag = await IdentityTagAsync(client, path);

        using HttpResponseMessage response = await SendWithHeadersAsync(
            client,
            new HttpMethod(method),
            path,
            ("Accept-Encoding", encoding));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new[] { encoding }, response.Content.Headers.ContentEncoding);
        Assert.Equal("W/" + tag, RawHeader(response, "ETag"));
        Assert.Equal("Accept-Encoding", RawHeader(response, "Vary"));
    }

    // If-Range and If-Match compare strongly. On an identity request for bytes 8 to 11, the weak tag a br answer gave
    // makes If-Range ignore the Range, so the whole file is sent, and makes If-Match fail with a 412; the strong tag of
    // the identity bytes lets either send the range. A range answer is never coded, so it keeps the strong tag and
    // carries no Vary, while the whole file sent in its place varies by Accept-Encoding like any identity 200.
    [Theory]
    [InlineData("If-Range", true, HttpStatusCode.OK)]
    [InlineData("If-Range", false, HttpStatusCode.PartialContent)]
    [InlineData("If-Match", true, HttpStatusCode.PreconditionFailed)]
    [InlineData("If-Match", false, HttpStatusCode.PartialContent)]
    public async Task OnlyTheIdentityTagAuthorizesARangeOrAMatch(string header, bool codedTag, HttpStatusCode expected)
    {
        await using ApiFactory factory = CreateFactory();
        using HttpClient client = factory.CreateClient();
        string tag = await IdentityTagAsync(client, "/" + AssetPath);
        string sent = tag;
        if (codedTag)
        {
            using HttpResponseMessage coded = await SendAcceptingAsync(client, "/" + AssetPath, "br");
            Assert.Equal(new[] { "br" }, coded.Content.Headers.ContentEncoding);
            sent = Assert.IsType<string>(RawHeader(coded, "ETag"));
            Assert.Equal("W/" + tag, sent);
        }

        using HttpResponseMessage response = await SendWithHeadersAsync(
            client,
            HttpMethod.Get,
            "/" + AssetPath,
            ("Range", "bytes=8-11"),
            (header, sent));

        Assert.Equal(expected, response.StatusCode);
        Assert.Empty(response.Content.Headers.ContentEncoding);
        string body = await response.Content.ReadAsStringAsync();
        switch (expected)
        {
            case HttpStatusCode.OK:
                Assert.Equal(AssetContent, body);
                Assert.Null(RawHeader(response, "Content-Range"));
                Assert.Equal(tag, RawHeader(response, "ETag"));
                Assert.Equal("Accept-Encoding", RawHeader(response, "Vary"));
                break;

            case HttpStatusCode.PartialContent:
                Assert.Equal(AssetContent.Substring(8, 4), body);
                Assert.Equal(
                    new ContentRangeHeaderValue(8, 11, AssetContent.Length),
                    response.Content.Headers.ContentRange);
                Assert.Equal(tag, RawHeader(response, "ETag"));
                Assert.Null(RawHeader(response, "Vary"));
                break;

            default:
                Assert.DoesNotContain(AssetContent.Substring(8, 4), body, StringComparison.Ordinal);
                Assert.Null(RawHeader(response, "ETag"));
                Assert.Null(RawHeader(response, "Vary"));
                break;
        }
    }

    // On real Kestrel a browser's revalidation of the coded script, with the weak tag it was given and the coding it
    // accepts, is a 304 carrying Vary: Accept-Encoding once, that weak tag and the asset lifetime, as on TestServer.
    [Theory]
    [InlineData("br")]
    [InlineData("gzip")]
    public async Task KestrelRevalidatesTheCodedScriptWithVaryAndItsWeakTag(string encoding)
    {
        await using var factory = new KestrelApiFactory(ClientOptions(_client));
        using HttpClient client = factory.CreateKestrelClient();
        string tag = await IdentityTagAsync(client, "/" + AssetPath);
        using HttpResponseMessage coded = await SendAcceptingAsync(client, "/" + AssetPath, encoding);
        Assert.Equal(new[] { encoding }, coded.Content.Headers.ContentEncoding);
        string weak = Assert.IsType<string>(RawHeader(coded, "ETag"));
        Assert.Equal("W/" + tag, weak);

        using HttpResponseMessage response = await SendWithHeadersAsync(
            client,
            HttpMethod.Get,
            "/" + AssetPath,
            ("Accept-Encoding", encoding),
            ("If-None-Match", weak));

        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
        Assert.Equal("Accept-Encoding", RawHeader(response, "Vary"));
        Assert.Equal(weak, RawHeader(response, "ETag"));
        Assert.Equal(AssetCacheControl, RawCacheControl(response));
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    // One entry past the entry limit, or one file past the byte limit: the folder is not served at all, and the host
    // logs one warning, the fixed text naming the configuration key and never the folder.
    [Theory]
    [InlineData("entries")]
    [InlineData("bytes")]
    public async Task FolderOverTheLimitsServesNoClientAndLogsOneFixedWarning(string limit)
    {
        if (limit == "entries")
        {
            string many = Path.Join(_client, "many");
            Directory.CreateDirectory(many);
            for (int i = 0; i < ClientAppHosting.MaxClientEntries; i++)
            {
                File.WriteAllBytes(Path.Join(many, i.ToString(CultureInfo.InvariantCulture)), []);
            }
        }
        else
        {
            // Sparse where the file system allows, so the case writes no 64 MiB; the read refuses it from its length.
            using FileStream large = File.Create(Path.Join(_client, "assets", "large.js"));
            large.SetLength(ClientAppHosting.MaxClientBytes + 1);
        }

        await using ApiFactory factory = CreateFactory();
        using HttpClient client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(client, "/")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(client, "/" + AssetPath)).Status);
        CapturedLogEntry warning = AssertOneClientWarning(factory);
        Assert.Equal(3, warning.EventId.Id);
        Assert.Equal("ClientAppUnconfirmed", warning.EventId.Name);
        Assert.Contains(ApiOptions.ClientPathKey, warning.Message, StringComparison.Ordinal);
        Assert.Equal("{OriginalFormat}", Assert.Single(warning.State).Key);
        Assert.Null(warning.ExceptionType);
        Assert.DoesNotContain(_temp.Name, warning.Message, StringComparison.Ordinal);
    }

    // Outside Windows, a client folder every user can write, judged through a configured link on the folder it
    // resolves to, or a folder, an asset or the page in it that every user can write: the folder is not served at all,
    // the API answers as it does without a client, and the host logs one warning, the fixed text naming the
    // configuration key and never the folder, the entry or its mode. Every other entry is group-writable only.
    [Theory]
    [InlineData("", false)]
    [InlineData("", true)]
    [InlineData("assets", false)]
    [InlineData(AssetPath, false)]
    [InlineData(ClientAppHosting.IndexFileName, false)]
    public async Task FolderEveryUserCanWriteServesNoClientAndLogsOneFixedWarning(string entry, bool throughLink)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        SetModes(GroupWritableFolderMode, GroupWritableFileMode);
        string path = Path.Join(_client, entry);
        File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.OtherWrite);

        await using ApiFactory factory = CreateFactory(throughLink ? LinkToClient() : _client);
        using HttpClient client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(client, "/")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(client, "/" + AssetPath)).Status);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, "/api/forms")).Status);
        CapturedLogEntry warning = AssertOneClientWarning(factory);
        Assert.Equal(4, warning.EventId.Id);
        Assert.Equal("ClientAppUntrusted", warning.EventId.Name);
        Assert.Contains(ApiOptions.ClientPathKey, warning.Message, StringComparison.Ordinal);
        Assert.Null(warning.ExceptionType);

        // The state is the template alone, which is the message, so neither holds anything the message does not.
        KeyValuePair<string, object?> state = Assert.Single(warning.State);
        Assert.Equal("{OriginalFormat}", state.Key);
        Assert.Equal(warning.Message, state.Value);
        Assert.Null(warning.StateMessage);
        Assert.DoesNotContain(_temp.Name, warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(UnixFileMode.OtherWrite), warning.Message, StringComparison.Ordinal);
        if (entry.Length > 0)
        {
            Assert.DoesNotContain(Path.GetFileName(entry), warning.Message, StringComparison.Ordinal);
        }
    }

    // Outside Windows, group write is allowed, because where each user has a private group the default umask 002 leaves
    // every build group-writable, and a configured link, whose own mode lets every user write, is judged on the folder
    // it resolves to: each file is served exactly as it was at startup, and the host logs nothing.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FolderOnlyItsOwnerAndGroupCanWriteIsServed(bool throughLink)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        SetModes(GroupWritableFolderMode, GroupWritableFileMode);
        await using ApiFactory factory = CreateFactory(throughLink ? LinkToClient() : _client);
        using HttpClient client = factory.CreateClient();

        foreach ((string path, string file) in new[]
                 {
                     ("/", ClientAppHosting.IndexFileName),
                     ("/" + AssetPath, AssetPath),
                     ("/" + ImagePath, ImagePath),
                 })
        {
            using HttpResponseMessage response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(
                await File.ReadAllBytesAsync(Path.Join(_client, file)),
                await response.Content.ReadAsByteArrayAsync());
        }

        Assert.Empty(factory.Logs.Find(entry => entry.Category == ClientAppHosting.LoggerCategory));
    }

    // The Linux confirmation: a handle's own path, every link resolved, is compared with the real path of the file the
    // listing found, so a handle opened through a folder link or a file link is refused, and one opened at the file's
    // real path is accepted.
    [Theory]
    [InlineData(AssetPath, true)]
    [InlineData("linked-folder/app.js", false)]
    [InlineData("linked-file.js", false)]
    public void HandleIsConfirmedOnlyAtTheRealPathOfTheEntryItWasOpenedFor(string relativePath, bool confirmed)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        Directory.CreateSymbolicLink(Path.Join(_client, "linked-folder"), Path.Join(_client, "assets"));
        File.CreateSymbolicLink(Path.Join(_client, "linked-file.js"), Path.Join(_outside, SecretFile));
        string realPath = Path.Join(ClientAppHosting.ResolveRealPath(_client), relativePath);

        using SafeFileHandle handle = File.OpenHandle(Path.Join(_client, relativePath));

        Assert.Equal(confirmed, ClientAppHosting.IsOpenedAt(handle, realPath));
    }

    // The real path the Linux confirmation expects: a relative and an absolute link are both followed, a ".." after a
    // link climbs from its target, and a loop of links is refused instead of followed for ever.
    [Fact]
    public void ResolveRealPathFollowsEveryLinkAndRefusesALoop()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        string realTemp = ClientAppHosting.ResolveRealPath(_temp.FullName);
        Directory.CreateSymbolicLink(Path.Join(_client, "up"), "..");
        Directory.CreateSymbolicLink(Path.Join(_outside, "to-assets"), Path.Join(_client, "assets"));
        string linked = Path.Join(_client, "up", "outside", "to-assets", "..", AssetPath);

        Assert.Equal(Path.Join(realTemp, "client", AssetPath), ClientAppHosting.ResolveRealPath(linked));

        File.CreateSymbolicLink(Path.Join(_temp.FullName, "loop-a"), Path.Join(_temp.FullName, "loop-b"));
        File.CreateSymbolicLink(Path.Join(_temp.FullName, "loop-b"), Path.Join(_temp.FullName, "loop-a"));

        // Caught here rather than by Assert.Throws, whose lambda the platform guard above would not cover.
        IOException? refusal = null;
        try
        {
            _ = ClientAppHosting.ResolveRealPath(Path.Join(_temp.FullName, "loop-a"));
        }
        catch (IOException exception)
        {
            refusal = exception;
        }

        Assert.IsType<IOException>(refusal);
    }

    // Creates a symbolic link, or returns false after asserting that the operating system refused it for want of the
    // privilege to create one, as ForbiddenReferenceTests does: Windows lets an unprivileged process create a symbolic
    // link only in Developer Mode. Any other failure still fails the case.
    private static bool TryCreateSymbolicLink(string path, string target, bool linksFolder)
    {
        const int PrivilegeNotHeld = unchecked((int)0x80070522);
        try
        {
            if (linksFolder)
            {
                Directory.CreateSymbolicLink(path, target);
            }
            else
            {
                File.CreateSymbolicLink(path, target);
            }

            return true;
        }
        catch (Exception refusal) when (OperatingSystem.IsWindows() &&
                                        refusal is UnauthorizedAccessException or IOException)
        {
            Assert.True(
                refusal is UnauthorizedAccessException || refusal.HResult == PrivilegeNotHeld,
                $"Creating a symbolic link failed for a reason other than the missing privilege: {refusal}");
            return false;
        }
    }

    private static void WriteFile(string folder, string relativePath, string content)
    {
        string path = Path.Join(folder, relativePath);
        Directory.CreateDirectory(
            Path.GetDirectoryName(path) ?? throw new InvalidOperationException($"'{path}' has no parent directory."));
        File.WriteAllText(path, content);
    }

    private static async Task<ReceivedResponse> GetAsync(HttpClient client, string path)
    {
        using HttpResponseMessage response = await client.GetAsync(path);
        return new ReceivedResponse(response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static string? RawCacheControl(HttpResponseMessage response) => RawHeader(response, "Cache-Control");

    // A response or content header as the server wrote it, unparsed and never computed by the client, so the order and
    // spelling of a value are asserted too, and a Content-Length the server did not send is absent; null when the
    // response has none.
    private static string? RawHeader(HttpResponseMessage response, string name) =>
        response.Headers.NonValidated.TryGetValues(name, out HeaderStringValues values)
        || response.Content.Headers.NonValidated.TryGetValues(name, out values)
            ? values.ToString()
            : null;

    // A GET of path, with a Range header when range is given and an Accept-Encoding header when acceptEncoding is.
    private static HttpRequestMessage Request(string path, string? range, string? acceptEncoding)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (range is not null)
        {
            Assert.True(request.Headers.TryAddWithoutValidation("Range", range), "The Range header was not added.");
        }

        if (acceptEncoding is not null)
        {
            Assert.True(
                request.Headers.TryAddWithoutValidation("Accept-Encoding", acceptEncoding),
                "The Accept-Encoding header was not added.");
        }

        return request;
    }

    // Neither client decodes a coded body itself, so the header is sent as given and the coded bytes are received.
    private static async Task<HttpResponseMessage> SendAcceptingAsync(
        HttpClient client,
        string path,
        string acceptEncoding)
    {
        using HttpRequestMessage request = Request(path, range: null, acceptEncoding);
        return await client.SendAsync(request);
    }

    // A request of method for path carrying each header whose value is given, added unvalidated, so a weak tag or a
    // list of tags is sent exactly as written; a header whose value is null is not sent.
    private static async Task<HttpResponseMessage> SendWithHeadersAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        params (string Name, string? Value)[] headers)
    {
        using var request = new HttpRequestMessage(method, path);
        foreach ((string name, string? value) in headers)
        {
            if (value is not null)
            {
                Assert.True(request.Headers.TryAddWithoutValidation(name, value), $"The {name} header was not added.");
            }
        }

        return await client.SendAsync(request);
    }

    // The entity tag, quoted, of the identity 200 to a GET of path without Accept-Encoding, which must be strong.
    private static async Task<string> IdentityTagAsync(HttpClient client, string path)
    {
        using HttpResponseMessage identity = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, identity.StatusCode);
        Assert.Empty(identity.Content.Headers.ContentEncoding);
        EntityTagHeaderValue etag = Assert.IsType<EntityTagHeaderValue>(identity.Headers.ETag);
        Assert.False(etag.IsWeak, "The tag of the identity answer is weak.");
        Assert.Equal(etag.Tag, RawHeader(identity, "ETag"));
        return etag.Tag;
    }

    // The 200 of a compressed text response: its one Content-Encoding is the coding asked for, its Vary is exactly
    // Accept-Encoding, listed once, it has no Content-Length, its body decodes to exactly the identity bytes, and it
    // carries the hardening headers and exactly the Cache-Control of its path.
    private static async Task AssertEncodedAsync(
        HttpResponseMessage response,
        string encoding,
        byte[] identityBytes,
        string cacheControl)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new[] { encoding }, response.Content.Headers.ContentEncoding);
        Assert.Equal("Accept-Encoding", RawHeader(response, "Vary"));
        Assert.Null(RawHeader(response, "Content-Length"));
        Assert.Equal(identityBytes, Decode(encoding, await response.Content.ReadAsByteArrayAsync()));
        Assert.Equal("nosniff", RawHeader(response, "X-Content-Type-Options"));
        Assert.Equal("DENY", RawHeader(response, "X-Frame-Options"));
        Assert.Equal("frame-ancestors 'none'", RawHeader(response, "Content-Security-Policy"));
        Assert.Equal(cacheControl, RawCacheControl(response));
    }

    private static byte[] Decode(string encoding, byte[] body)
    {
        using var coded = new MemoryStream(body);
        using Stream decoder = encoding switch
        {
            "br" => new BrotliStream(coded, CompressionMode.Decompress),
            "gzip" => new GZipStream(coded, CompressionMode.Decompress),
            _ => throw new ArgumentOutOfRangeException(nameof(encoding), encoding, "Not a coding the API sends."),
        };
        using var decoded = new MemoryStream();
        decoder.CopyTo(decoded);
        return decoded.ToArray();
    }

    // The one entry the client host logged; only its warnings use that category.
    private static CapturedLogEntry AssertOneClientWarning(ApiFactory factory)
    {
        CapturedLogEntry warning = Assert.Single(
            factory.Logs.Entries,
            entry => entry.Category == ClientAppHosting.LoggerCategory);
        Assert.Equal(LogLevel.Warning, warning.Level);
        return warning;
    }

    // Gives the client folder and every folder below it folderMode, and every file in it fileMode, so a case does not
    // depend on the umask the test run inherits.
    [UnsupportedOSPlatform("windows")]
    private void SetModes(UnixFileMode folderMode, UnixFileMode fileMode)
    {
        File.SetUnixFileMode(_client, folderMode);
        foreach (string folder in Directory.EnumerateDirectories(_client, "*", SearchOption.AllDirectories))
        {
            File.SetUnixFileMode(folder, folderMode);
        }

        foreach (string file in Directory.EnumerateFiles(_client, "*", SearchOption.AllDirectories))
        {
            File.SetUnixFileMode(file, fileMode);
        }
    }

    // A link beside the client folder to it, for a case that configures the link in the folder's place.
    private string LinkToClient()
    {
        string link = Path.Join(_temp.FullName, "client-link");
        Directory.CreateSymbolicLink(link, _client);
        return link;
    }

    // A host over a fake store serving the client folder at clientPath.
    private static ApiFactoryOptions ClientOptions(string clientPath) =>
        ApiFactoryOptions.ForFake(new FakeDataStore()) with
        {
            ExtraSettings = new Dictionary<string, string?> { [ApiOptions.ClientPathKey] = clientPath },
        };

    private ApiFactory CreateFactory(string? clientPath = null) => new(ClientOptions(clientPath ?? _client));

    // What a case reads from a response, taken before the response is disposed.
    private sealed record ReceivedResponse(HttpStatusCode Status, string Text);
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
