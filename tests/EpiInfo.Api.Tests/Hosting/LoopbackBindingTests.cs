using System.Net;
using System.Text.Json;
using EpiInfo.Api.Errors;
using EpiInfo.Api.Hosting;
using EpiInfo.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Http;

namespace EpiInfo.Api.Tests.Hosting;

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
