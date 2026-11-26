using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using EpiInfo.Api.Errors;
using EpiInfo.Api.Hosting;
using EpiInfo.Api.Tests.Infrastructure;
using EpiInfo.Domain.Values;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit.Sdk;
using BadHttpRequestException = Microsoft.AspNetCore.Http.BadHttpRequestException;

namespace EpiInfo.Api.Tests.Hosting;

/// <summary>
/// The shared hosts of <see cref="BodyLimitTests"/>: a <see cref="GuardPipelineHost"/> on TestServer and on real
/// Kestrel in each <see cref="GuardOrder"/>, all over one <see cref="FakeDataStore"/> holding
/// <see cref="FakeForms.TwoPage"/> with one seeded record.
/// </summary>
public sealed class BodyLimitHostFixture : IAsyncLifetime
{
    private readonly Dictionary<(GuardHostKind Kind, GuardOrder Order), GuardPipelineHost> _hosts = new();

    /// <summary>Creates the store and seeds the record; the hosts start in <see cref="InitializeAsync"/>.</summary>
    public BodyLimitHostFixture()
    {
        Store = new FakeDataStore(FakeForms.TwoPage);
        RecordId = Store.SeedRecord(FakeForms.TwoPageViewId);
    }

    /// <summary>The store every host serves.</summary>
    public FakeDataStore Store { get; }

    /// <summary>The <c>GlobalRecordId</c> of the seeded record, the target of every <c>PUT</c> and <c>PATCH</c>.</summary>
    public string RecordId { get; }

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        try
        {
            foreach (GuardHostKind kind in Enum.GetValues<GuardHostKind>())
            {
                foreach (GuardOrder order in Enum.GetValues<GuardOrder>())
                {
                    _hosts[(kind, order)] = await GuardPipelineHost.StartAsync(kind, Store, order);
                }
            }
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        GuardPipelineHost[] hosts = [.. _hosts.Values];
        _hosts.Clear();

        List<Exception>? failures = null;
        foreach (GuardPipelineHost host in hosts)
        {
            try
            {
                await host.DisposeAsync();
            }
            catch (Exception failure)
            {
                (failures ??= []).Add(failure);
            }
        }

        if (failures is not null)
        {
            throw new AggregateException("Stopping a body-limit test host failed.", failures);
        }
    }

    /// <summary>The started host of <paramref name="kind"/> with its guards in <paramref name="order"/>.</summary>
    /// <exception cref="InvalidOperationException">The hosts have not been started.</exception>
    internal GuardPipelineHost Host(GuardHostKind kind, GuardOrder order) =>
        _hosts.TryGetValue((kind, order), out GuardPipelineHost? host)
            ? host
            : throw new InvalidOperationException("The host has not been started.");
}

/// <summary>
/// The 1,048,576-byte request body cap of the four JSON body endpoints (<c>POST</c> records, <c>PUT</c> record,
/// <c>POST</c> search, <c>POST</c> field-event), on TestServer and on real Kestrel.
/// </summary>
/// <remarks>
/// <para>
/// Kestrel's own cap is proved apart from <see cref="RequestBodyLimitMiddleware"/>: the API's composition sets
/// <see cref="KestrelServerLimits.MaxRequestBodySize"/> to the committed limit, and on the probe route the guard never
/// acts on, Kestrel alone answers 413 while TestServer, which applies no Kestrel limit, reads the same body.
/// </para>
/// <para>
/// On the body endpoints a body one byte over the limit, declared or chunked, is the fixed 413 ProblemDetails and
/// reaches no handler; a valid JSON body padded with insignificant whitespace to exactly the limit gets the handler's
/// exact success. Both rules are proved through the API's own composition, <c>Program.cs</c> hosted by
/// <see cref="ApiFactory"/> on TestServer and by <see cref="KestrelApiFactory"/> on real Kestrel, each case on its own
/// host and store, and again on the shared <see cref="GuardPipelineHost"/> hosts, which run the same guards in either
/// order. Kestrel counts the framing of a chunked body against its own cap, so the guard raises that cap for a
/// chunked request by a framing budget of 5 × limit + 5 bytes, and within that budget the decoded bytes decide: the
/// exact limit passes and one byte more fails whatever the chunk size. The budget's own edge is pinned with chunk
/// extensions: framing up to exactly the budget is created, and framing past it, by one byte or by many, is Kestrel's
/// own 413. TestServer's streamed boundary is pinned on its own, byte for byte. An unacceptable media type keeps its
/// 415, a route miss its 404 and a method mismatch its 405 however large the body, in either guard order.
/// </para>
/// <para>
/// The guard's own rules are also pinned by invoking it directly: which requests get the raised server limit, which
/// pass through untouched, and the counting stream's read bound.
/// </para>
/// <para>
/// TestServer is reached through its in-process <see cref="HttpClient"/>, and the composition's Kestrel through
/// <see cref="KestrelApiFactory.CreateKestrelClient"/>, whose bodies stay within one byte of the limit. The
/// guard-pipeline Kestrel hosts, whose cases include exact chunk framing of several megabytes on the wire, are reached
/// through a raw HTTP/1.1 socket client that sends the exact framing given and reads the response while the body is
/// still being sent: <see cref="HttpClient"/> waits for the whole body to be sent before it surfaces a response, so a
/// server that answers early and closes the connection would turn the answer into a send failure. No case resends a
/// request.
/// </para>
/// <para>
/// The class needs no database: it carries no <c>Integration</c> trait and joins no database collection.
/// </para>
/// </remarks>
public sealed class BodyLimitTests : IClassFixture<BodyLimitHostFixture>
{
    private const string ContentTypeHeader = "Content-Type";

    private const string JsonMediaType = "application/json";

    private const string LoopbackHost = "127.0.0.1";

    private const string SubmittedText = "x";

    private const string CreateBody = """{"values":{"LastName":"x"}}""";

    private const string SearchBody = """{"criteria":[{"field":"LastName","value":"x"}]}""";

    private const string FieldEventBody = """{"event":"FieldAfter","fieldName":"LastName","values":{"LastName":"x"}}""";

    private const string EmptyFieldEventEnvelope =
        """{"enabled":[],"disabled":[],"cleared":[],"assigned":{},"dialogs":[],"diagnostics":[]}""";

    private const string UnknownApiPath = "/api/unknown";

    // The chunk size the raw client uses unless a case sets its own.
    private const int DefaultChunkSize = 64 * 1024;

    // An error body is a fixed ProblemDetails: a few hundred bytes, never a copy of the request.
    private const int MaxErrorBodyLength = 1024;

    // The limit of the direct-invocation read cases, small so their buffers stay tiny.
    private const int SmallLimit = 64;

    // A large read buffer asks for more than the whole body; a small one makes many reads.
    private const int LargeBuffer = SmallLimit * 4;

    private const int SmallBuffer = 3;

    private const char Padding = ' ';

    private static readonly CommittedSettings Committed = CommittedSettings.Read();

    // A chunk-signature value in the aws-chunked shape, 64 lowercase hexadecimal digits, assembled here so no
    // signature-like literal sits in the source.
    private static readonly string ChunkSignature = string.Concat(Enumerable.Repeat("0123456789abcdef", 4));

    private static readonly BodyEndpoint[] AllEndpoints =
        [BodyEndpoint.Create, BodyEndpoint.Update, BodyEndpoint.Search, BodyEndpoint.FieldEvent];

    private static readonly ServerKind[] AllServers = [ServerKind.TestServer, ServerKind.Kestrel];

    private static readonly GuardSequence[] AllOrders = [GuardSequence.BodyLimitFirst, GuardSequence.MediaTypeFirst];

    private static readonly BodyFraming[] AllFramings = [BodyFraming.ContentLength, BodyFraming.Chunked];

    private static readonly ReadOverload[] AllReadOverloads =
        [ReadOverload.ArraySync, ReadOverload.ArrayAsync, ReadOverload.Span, ReadOverload.Memory];

    // Request text an error body must never echo: the padding and the field names of the bodies sent.
    private static readonly string[] RequestFragments =
        [new string(Padding, 8), "LastName", "criteria", "fieldName", "FieldAfter"];

    private readonly BodyLimitHostFixture _fixture;

    /// <summary>Creates the test class over the shared hosts.</summary>
    /// <param name="fixture">The shared TestServer and Kestrel hosts.</param>
    public BodyLimitTests(BodyLimitHostFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>The server a case runs on.</summary>
    public enum ServerKind
    {
        /// <summary>The in-process TestServer, which applies no Kestrel limit.</summary>
        TestServer,

        /// <summary>Real Kestrel on an ephemeral <c>127.0.0.1</c> port.</summary>
        Kestrel,
    }

    /// <summary>The order of the two request guards after routing.</summary>
    public enum GuardSequence
    {
        /// <summary>The body limit, then the media-type guard.</summary>
        BodyLimitFirst,

        /// <summary>The media-type guard, then the body limit.</summary>
        MediaTypeFirst,
    }

    /// <summary>The four endpoints that take a JSON body.</summary>
    public enum BodyEndpoint
    {
        /// <summary><c>POST /api/forms/{viewId}/records</c>.</summary>
        Create,

        /// <summary><c>PUT /api/forms/{viewId}/records/{globalRecordId}</c>.</summary>
        Update,

        /// <summary><c>POST /api/forms/{viewId}/records/search</c>.</summary>
        Search,

        /// <summary><c>POST /api/forms/{viewId}/checkcode/field-event</c>.</summary>
        FieldEvent,
    }

    /// <summary>How the body's length reaches the server.</summary>
    public enum BodyFraming
    {
        /// <summary>Declared by <c>Content-Length</c>.</summary>
        ContentLength,

        /// <summary>HTTP/1.1 chunked transfer coding, with no <c>Content-Length</c>.</summary>
        Chunked,
    }

    /// <summary>A route outside the four body endpoints.</summary>
    public enum OutsideRoute
    {
        /// <summary><c>POST /api/unknown</c>, which matches no route.</summary>
        UnknownApiRoute,

        /// <summary><c>PATCH</c> on a record, a method the record route does not map.</summary>
        RecordPatch,
    }

    /// <summary>The endpoint a directly invoked guard sees.</summary>
    public enum EndpointShape
    {
        /// <summary>No endpoint matched.</summary>
        None,

        /// <summary>An endpoint without <see cref="JsonBodyMetadata"/>.</summary>
        Unmarked,

        /// <summary>An endpoint carrying <see cref="JsonBodyMetadata"/>.</summary>
        JsonBody,
    }

    /// <summary>The <see cref="Stream"/> read overload a case reads the guarded body with.</summary>
    public enum ReadOverload
    {
        /// <summary><see cref="Stream.Read(byte[], int, int)"/>.</summary>
        ArraySync,

        /// <summary><see cref="Stream.ReadAsync(byte[], int, int, CancellationToken)"/>.</summary>
        ArrayAsync,

        /// <summary><see cref="Stream.Read(Span{byte})"/>.</summary>
        Span,

        /// <summary><see cref="Stream.ReadAsync(Memory{byte}, CancellationToken)"/>.</summary>
        Memory,
    }

    /// <summary>An invalid argument list for the array read overloads.</summary>
    public enum InvalidBuffer
    {
        /// <summary>A <see langword="null"/> buffer.</summary>
        NullBuffer,

        /// <summary>A negative offset.</summary>
        NegativeOffset,

        /// <summary>A negative count.</summary>
        NegativeCount,

        /// <summary>An offset and count that run past the end of the buffer.</summary>
        CountPastEnd,
    }

    /// <summary>The configured limit: 1,048,576 bytes, read from the committed <c>appsettings.json</c>.</summary>
    private static long Limit => Committed.Options.MaxRequestBodySize;

    /// <summary>
    /// The probe route the guard never acts on, on each server: Kestrel's cap alone holds it, TestServer holds it to
    /// nothing.
    /// </summary>
    public static TheoryData<ServerKind, BodyFraming, long, HttpStatusCode> UnguardedProbeCases => new()
    {
        { ServerKind.Kestrel, BodyFraming.ContentLength, 0, HttpStatusCode.OK },
        { ServerKind.Kestrel, BodyFraming.ContentLength, 1, HttpStatusCode.RequestEntityTooLarge },
        { ServerKind.Kestrel, BodyFraming.Chunked, 0, HttpStatusCode.RequestEntityTooLarge },
        { ServerKind.TestServer, BodyFraming.ContentLength, 1, HttpStatusCode.OK },
        { ServerKind.TestServer, BodyFraming.Chunked, 1, HttpStatusCode.OK },
    };

    /// <summary>Each server, guard order and framing.</summary>
    public static TheoryData<ServerKind, GuardSequence, BodyFraming> ServerOrderAndFramingCases
    {
        get
        {
            var data = new TheoryData<ServerKind, GuardSequence, BodyFraming>();
            foreach (ServerKind server in AllServers)
            {
                foreach (GuardSequence order in AllOrders)
                {
                    foreach (BodyFraming framing in AllFramings)
                    {
                        data.Add(server, order, framing);
                    }
                }
            }

            return data;
        }
    }

    /// <summary>Each server, guard order, body endpoint and framing.</summary>
    public static TheoryData<ServerKind, GuardSequence, BodyEndpoint, BodyFraming> EndpointCases
    {
        get
        {
            var data = new TheoryData<ServerKind, GuardSequence, BodyEndpoint, BodyFraming>();
            foreach (ServerKind server in AllServers)
            {
                foreach (GuardSequence order in AllOrders)
                {
                    foreach (BodyEndpoint endpoint in AllEndpoints)
                    {
                        foreach (BodyFraming framing in AllFramings)
                        {
                            data.Add(server, order, endpoint, framing);
                        }
                    }
                }
            }

            return data;
        }
    }

    /// <summary>
    /// Each server the API's own composition runs on (<see cref="ApiFactory"/> on TestServer,
    /// <see cref="KestrelApiFactory"/> on real Kestrel), body endpoint and framing.
    /// </summary>
    public static TheoryData<ServerKind, BodyEndpoint, BodyFraming> CompositionCases
    {
        get
        {
            var data = new TheoryData<ServerKind, BodyEndpoint, BodyFraming>();
            foreach (ServerKind server in AllServers)
            {
                foreach (BodyEndpoint endpoint in AllEndpoints)
                {
                    foreach (BodyFraming framing in AllFramings)
                    {
                        data.Add(server, endpoint, framing);
                    }
                }
            }

            return data;
        }
    }

    /// <summary>
    /// Chunk sizes on Kestrel: the exact limit in one chunk and in ever smaller ones down to one byte, and one byte
    /// over the limit in large, small and one-byte chunks.
    /// </summary>
    public static TheoryData<int, long, HttpStatusCode> ChunkSizeCases => new()
    {
        { 1_048_576, 0, HttpStatusCode.Created },
        { 64 * 1024, 0, HttpStatusCode.Created },
        { 1024, 0, HttpStatusCode.Created },
        { 16, 0, HttpStatusCode.Created },
        { 1, 0, HttpStatusCode.Created },
        { 64 * 1024, 1, HttpStatusCode.RequestEntityTooLarge },
        { 1024, 1, HttpStatusCode.RequestEntityTooLarge },
        { 1, 1, HttpStatusCode.RequestEntityTooLarge },
    };

    /// <summary>
    /// Chunkings of a body of exactly the limit on Kestrel around the framing budget: the payload bytes per chunk, the
    /// extension every data chunk carries, the extension the first chunk carries instead (<see langword="null"/> for
    /// the same), the body's exact wire length, and the status. Signed 64 KiB chunks and 16-byte chunks with a bare
    /// extension fit the budget; two-byte chunks with a five-byte extension spend exactly the budget, and one more
    /// extension byte on the first chunk exceeds it by one; one-byte chunks with a two-byte extension exceed it by
    /// 2 × limit.
    /// </summary>
    public static TheoryData<int, string, string?, long, HttpStatusCode> FramingBudgetCases => new()
    {
        { 64 * 1024, ";chunk-signature=" + ChunkSignature, null, 1_050_021, HttpStatusCode.Created },
        { 16, ";e", null, 1_572_869, HttpStatusCode.Created },
        { 2, ";e=xy", null, 6_291_461, HttpStatusCode.Created },
        { 2, ";e=xy", ";e=xyz", 6_291_462, HttpStatusCode.RequestEntityTooLarge },
        { 1, ";x", null, 8_388_613, HttpStatusCode.RequestEntityTooLarge },
    };

    /// <summary>Bodies around the limit on TestServer's guarded probe, declared and streamed.</summary>
    public static TheoryData<BodyFraming, long> TestServerBoundaryCases
    {
        get
        {
            var data = new TheoryData<BodyFraming, long>();
            foreach (BodyFraming framing in AllFramings)
            {
                foreach (long overLimit in new[] { -1L, 0L, 1L, 2L, Limit })
                {
                    data.Add(framing, overLimit);
                }
            }

            return data;
        }
    }

    /// <summary>
    /// Each server, guard order, body endpoint and framing with each media type the JSON guard rejects, a missing
    /// <c>Content-Type</c> included.
    /// </summary>
    public static TheoryData<ServerKind, GuardSequence, BodyEndpoint, string?, BodyFraming> UnacceptableMediaTypeCases
    {
        get
        {
            string?[] mediaTypes =
            [
                "text/plain",
                "application/merge-patch+json",
                null,
                "application/json; charset=utf-16",
            ];

            var data = new TheoryData<ServerKind, GuardSequence, BodyEndpoint, string?, BodyFraming>();
            foreach ((ServerKind server, GuardSequence order, BodyEndpoint endpoint, BodyFraming framing) in
                     EndpointCombinations())
            {
                foreach (string? mediaType in mediaTypes)
                {
                    data.Add(server, order, endpoint, mediaType, framing);
                }
            }

            return data;
        }
    }

    /// <summary>Each server, guard order and framing on each route outside the body endpoints, as text and as JSON.</summary>
    public static TheoryData<ServerKind, GuardSequence, OutsideRoute, string, BodyFraming> OutsideRouteCases
    {
        get
        {
            var data = new TheoryData<ServerKind, GuardSequence, OutsideRoute, string, BodyFraming>();
            foreach (ServerKind server in AllServers)
            {
                foreach (GuardSequence order in AllOrders)
                {
                    foreach (OutsideRoute route in new[] { OutsideRoute.UnknownApiRoute, OutsideRoute.RecordPatch })
                    {
                        foreach (string contentType in new[] { "text/plain", JsonMediaType })
                        {
                            foreach (BodyFraming framing in AllFramings)
                            {
                                data.Add(server, order, route, contentType, framing);
                            }
                        }
                    }
                }
            }

            return data;
        }
    }

    /// <summary>Each server, guard order and body endpoint.</summary>
    public static TheoryData<ServerKind, GuardSequence, BodyEndpoint> EmptyBodyCases
    {
        get
        {
            var data = new TheoryData<ServerKind, GuardSequence, BodyEndpoint>();
            foreach (ServerKind server in AllServers)
            {
                foreach (GuardSequence order in AllOrders)
                {
                    foreach (BodyEndpoint endpoint in AllEndpoints)
                    {
                        data.Add(server, order, endpoint);
                    }
                }
            }

            return data;
        }
    }

    /// <summary>
    /// A marked JSON request's framing headers and server limit, and whether the guard raises that limit: only a
    /// chunked body without <c>Content-Length</c> whose writable limit is below the framing allowance is raised.
    /// </summary>
    public static TheoryData<long?, bool, bool, long?, bool> ServerLimitCases
    {
        get
        {
            long allowance = ChunkedFramingAllowance(Limit);
            return new TheoryData<long?, bool, bool, long?, bool>
            {
                { null, true, false, Limit, true },
                { null, true, false, 1, true },
                { null, true, false, allowance - 1, true },
                { Limit, false, false, Limit, false },
                { 1, false, false, Limit, false },
                { 1, true, false, Limit, false },
                { null, false, false, Limit, false },
                { null, true, true, Limit, false },
                { null, true, false, null, false },
                { null, true, false, allowance, false },
                { null, true, false, allowance + 1, false },
                { null, true, false, long.MaxValue, false },
            };
        }
    }

    /// <summary>
    /// Requests the guard leaves alone whatever their size: no endpoint, an unmarked endpoint, and a marked endpoint
    /// with a media type the JSON guard rejects.
    /// </summary>
    public static TheoryData<EndpointShape, string?, long?, bool> PassThroughCases => new()
    {
        { EndpointShape.None, JsonMediaType, Limit + 1, false },
        { EndpointShape.None, JsonMediaType, null, true },
        { EndpointShape.Unmarked, JsonMediaType, Limit + 1, false },
        { EndpointShape.Unmarked, JsonMediaType, null, true },
        { EndpointShape.JsonBody, "text/plain", Limit + 1, false },
        { EndpointShape.JsonBody, null, Limit + 1, false },
        { EndpointShape.JsonBody, "application/json; charset=utf-16", Limit + 1, false },
        { EndpointShape.JsonBody, "application/merge-patch+json", null, true },
    };

    /// <summary>Declared lengths over the limit.</summary>
    public static TheoryData<long> DeclaredOverLimitCases => new() { Limit + 1, long.MaxValue };

    /// <summary>
    /// Each read overload with a large and a small buffer, over an inner stream that returns all it is asked for or at
    /// most seven bytes a read, holding one byte or a hundred bytes more than the limit.
    /// </summary>
    public static TheoryData<ReadOverload, int, int, int> OverLimitReadCases
    {
        get
        {
            var data = new TheoryData<ReadOverload, int, int, int>();
            foreach ((ReadOverload overload, int bufferSize, int maxPerRead) in ReadCombinations())
            {
                data.Add(overload, bufferSize, maxPerRead, 1);
                data.Add(overload, bufferSize, maxPerRead, 100);
            }

            return data;
        }
    }

    /// <summary>Each read overload with a large and a small buffer, over a whole or a trickling inner stream.</summary>
    public static TheoryData<ReadOverload, int, int> ExactLimitReadCases
    {
        get
        {
            var data = new TheoryData<ReadOverload, int, int>();
            foreach ((ReadOverload overload, int bufferSize, int maxPerRead) in ReadCombinations())
            {
                data.Add(overload, bufferSize, maxPerRead);
            }

            return data;
        }
    }

    /// <summary>Each read overload.</summary>
    public static TheoryData<ReadOverload> ReadOverloadCases
    {
        get
        {
            var data = new TheoryData<ReadOverload>();
            foreach (ReadOverload overload in AllReadOverloads)
            {
                data.Add(overload);
            }

            return data;
        }
    }

    /// <summary>Each invalid argument list on the synchronous and the asynchronous array overload.</summary>
    public static TheoryData<bool, InvalidBuffer> InvalidBufferCases
    {
        get
        {
            var data = new TheoryData<bool, InvalidBuffer>();
            foreach (bool asynchronous in new[] { false, true })
            {
                foreach (InvalidBuffer invalid in Enum.GetValues<InvalidBuffer>())
                {
                    data.Add(asynchronous, invalid);
                }
            }

            return data;
        }
    }

    // The API's own composition, Program.cs on real Kestrel, sets Kestrel's cap to the committed limit, which is also
    // the default and the largest limit the API options allow.
    [Fact]
    public async Task ApiCompositionSetsKestrelsOwnCapToTheCommittedLimit()
    {
        Assert.Equal(1_048_576L, Committed.ConfiguredLimit);
        Assert.Equal(ApiOptions.DefaultMaxRequestBodySize, Committed.ConfiguredLimit);
        Assert.Equal(Committed.ConfiguredLimit, Limit);

        var store = new FakeDataStore(FakeForms.TwoPage);
        await using var factory = new KestrelApiFactory(ApiFactoryOptions.ForFake(store));
        factory.Start();

        Assert.IsNotType<TestServer>(factory.Services.GetRequiredService<IServer>());
        IReadOnlyList<string> addresses = factory.BoundAddresses;
        Assert.NotEmpty(addresses);
        Assert.All(addresses, address => Assert.Equal(LoopbackHost, new Uri(address).Host));

        KestrelServerOptions kestrel = factory.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value;
        Assert.Equal(Committed.ConfiguredLimit, kestrel.Limits.MaxRequestBodySize);
        Assert.Equal(Committed.ConfiguredLimit, factory.Services.GetRequiredService<ApiOptions>().MaxRequestBodySize);
    }

    // On the probe route without JsonBodyMetadata the guard never acts, so a 413 there is Kestrel's own: one byte over
    // the declared limit fails, and so does a chunked body of exactly the limit, because Kestrel counts the chunk
    // framing too. TestServer applies no Kestrel limit and reads the whole over-limit body, which is why the guard
    // exists.
    [Theory]
    [MemberData(nameof(UnguardedProbeCases))]
    public async Task UnguardedRouteIsHeldOnlyByTheServerCap(
        ServerKind server,
        BodyFraming framing,
        long overLimit,
        HttpStatusCode expected)
    {
        long size = Limit + overLimit;
        var request = new BodyRequest(
            HttpMethod.Post,
            GuardPipelineHost.UnguardedProbePath,
            JsonMediaType,
            PaddedJson(CreateBody, size),
            framing);

        ReceivedResponse response = await SendAsync(Host(server, GuardSequence.BodyLimitFirst), request);

        if (expected == HttpStatusCode.RequestEntityTooLarge)
        {
            AssertProblem(response, expected, ApiProblems.RequestTooLargeDetail);
            AssertEchoesNoRequestContent(response);
            return;
        }

        Assert.Equal(HttpStatusCode.OK, expected);
        AssertProbe(response, size, server == ServerKind.Kestrel ? Limit : null);
    }

    // On a guarded route Kestrel keeps the configured limit for a body with Content-Length and gets the framing
    // allowance, 6 x limit + 5, for a chunked one. TestServer exposes no server limit, so there is nothing to raise.
    [Theory]
    [MemberData(nameof(ServerOrderAndFramingCases))]
    public async Task GuardedRouteRaisesTheServerCapOnlyForChunkedBodies(
        ServerKind server,
        GuardSequence order,
        BodyFraming framing)
    {
        long? expectedServerLimit = server switch
        {
            ServerKind.TestServer => null,
            ServerKind.Kestrel => framing == BodyFraming.Chunked ? ChunkedFramingAllowance(Limit) : Limit,
            _ => throw new ArgumentOutOfRangeException(nameof(server), server, "Not a server kind."),
        };
        var request = new BodyRequest(
            HttpMethod.Post,
            GuardPipelineHost.GuardedProbePath,
            JsonMediaType,
            PaddedJson(CreateBody, Limit),
            framing);

        ReceivedResponse response = await SendAsync(Host(server, order), request);

        AssertProbe(response, Limit, expectedServerLimit);
    }

    // One byte over the limit, declared or chunked, is the fixed 413 on every body endpoint, server and guard order:
    // nothing of the request is echoed and no store call is made.
    [Theory]
    [MemberData(nameof(EndpointCases))]
    public async Task BodyOneByteOverTheLimitIsTheFixed413(
        ServerKind server,
        GuardSequence order,
        BodyEndpoint endpoint,
        BodyFraming framing)
    {
        int callsBefore = _fixture.Store.Calls.Count;
        int writesBefore = _fixture.Store.WriteCalls.Count;

        ReceivedResponse response = await SendAsync(
            Host(server, order),
            EndpointRequest(endpoint, _fixture.RecordId, Limit + 1, JsonMediaType, framing));

        AssertProblem(response, HttpStatusCode.RequestEntityTooLarge, ApiProblems.RequestTooLargeDetail);
        AssertEchoesNoRequestContent(response);
        Assert.Equal(callsBefore, _fixture.Store.Calls.Count);
        Assert.Equal(writesBefore, _fixture.Store.WriteCalls.Count);
    }

    // A valid JSON body padded with insignificant whitespace to exactly the limit reaches the handler and gets its
    // exact success, declared or chunked, so an unrelated failure cannot pass as "not rejected for size".
    [Theory]
    [MemberData(nameof(EndpointCases))]
    public async Task BodyOfExactlyTheLimitGetsTheHandlersSuccess(
        ServerKind server,
        GuardSequence order,
        BodyEndpoint endpoint,
        BodyFraming framing)
    {
        int writesBefore = _fixture.Store.WriteCalls.Count;
        BodyRequest request = EndpointRequest(endpoint, _fixture.RecordId, Limit, JsonMediaType, framing);
        Assert.Equal(1_048_576, request.Body.Length);

        ReceivedResponse response = await SendAsync(Host(server, order), request);

        Assert.True(response.BodyFullySent ?? true, "The request body was not fully sent before the response.");
        AssertHandlerSuccess(response, endpoint, _fixture.Store, _fixture.RecordId, writesBefore);
    }

    // Through the API's own composition, Program.cs on ApiFactory's TestServer and on KestrelApiFactory's real
    // Kestrel, one byte over the limit, declared or chunked, is the fixed 413 on every body endpoint: nothing of the
    // request is echoed and the store is never called, so nothing is written. Each case has its own host and store.
    [Theory]
    [MemberData(nameof(CompositionCases))]
    public async Task ApiCompositionAnswersOneByteOverTheLimitWithTheFixed413(
        ServerKind server,
        BodyEndpoint endpoint,
        BodyFraming framing)
    {
        await using ComposedApiHost api = ComposedApiHost.Start(server);
        BodyRequest request = EndpointRequest(endpoint, api.RecordId, Limit + 1, JsonMediaType, framing);
        Assert.Equal(1_048_577, request.Body.Length);

        ReceivedResponse response = await api.SendAsync(request);

        AssertProblem(response, HttpStatusCode.RequestEntityTooLarge, ApiProblems.RequestTooLargeDetail);
        AssertEchoesNoRequestContent(response);
        Assert.Empty(api.Store.Calls);
    }

    // Through the API's own composition, a valid JSON body padded to exactly the limit, declared or chunked, reaches
    // the handler on both servers and gets its exact success, so the cap rejects nothing at the limit.
    [Theory]
    [MemberData(nameof(CompositionCases))]
    public async Task ApiCompositionGivesABodyOfExactlyTheLimitTheHandlersSuccess(
        ServerKind server,
        BodyEndpoint endpoint,
        BodyFraming framing)
    {
        await using ComposedApiHost api = ComposedApiHost.Start(server);
        BodyRequest request = EndpointRequest(endpoint, api.RecordId, Limit, JsonMediaType, framing);
        Assert.Equal(1_048_576, request.Body.Length);

        ReceivedResponse response = await api.SendAsync(request);

        Assert.True(response.BodyFullySent ?? true, "The request body was not fully sent before the response.");
        AssertHandlerSuccess(response, endpoint, api.Store, api.RecordId, writesBefore: 0);
    }

    // Exact HTTP/1.1 chunk framing on Kestrel: every chunking of the exact limit carries more wire bytes than the
    // configured cap, one-byte chunks exactly the framing allowance, and each is created; one byte more is a 413 in
    // large, small and one-byte chunks alike.
    [Theory]
    [MemberData(nameof(ChunkSizeCases))]
    public async Task KestrelHoldsDecodedBytesToTheLimitWhateverTheChunkSize(
        int chunkSize,
        long overLimit,
        HttpStatusCode expected)
    {
        long size = Limit + overLimit;
        long wireLength = ChunkedLength(size, chunkSize);
        Assert.True(wireLength > Limit, "The chunked wire length does not exceed the configured cap.");
        if (overLimit == 0)
        {
            Assert.True(
                wireLength <= ChunkedFramingAllowance(Limit),
                "The chunked wire length of the exact limit exceeds the framing allowance.");
        }

        if (overLimit == 0 && chunkSize == 1)
        {
            Assert.Equal(ChunkedFramingAllowance(Limit), wireLength);
        }

        int writesBefore = _fixture.Store.WriteCalls.Count;
        var request = new BodyRequest(
            HttpMethod.Post,
            CreatePath(),
            JsonMediaType,
            PaddedJson(CreateBody, size),
            BodyFraming.Chunked,
            chunkSize);

        ReceivedResponse response = await SendAsync(Host(ServerKind.Kestrel, GuardSequence.BodyLimitFirst), request);

        if (expected == HttpStatusCode.Created)
        {
            Assert.True(response.BodyFullySent ?? true, "The request body was not fully sent before the response.");
            AssertCreated(response, _fixture.Store, writesBefore);
            return;
        }

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, expected);
        AssertProblem(response, expected, ApiProblems.RequestTooLargeDetail);
        AssertEchoesNoRequestContent(response);
        Assert.Equal(writesBefore, _fixture.Store.WriteCalls.Count);
    }

    // The framing budget, 5 x limit + 5 bytes on top of the payload, is Kestrel's edge for a chunked body of exactly
    // the limit, whatever spends it: chunk extensions within the budget, up to exactly the budget, are created, and
    // framing past it, by one byte or by many, is Kestrel's own 413, surfaced as the fixed ProblemDetails, although the
    // decoded bytes are the limit the guard lets through. The raw client sends exactly the computed wire length; its
    // encoder asserts every byte it writes.
    [Theory]
    [MemberData(nameof(FramingBudgetCases))]
    public async Task KestrelHoldsChunkFramingToTheBudget(
        int chunkSize,
        string extension,
        string? firstExtension,
        long expectedWireLength,
        HttpStatusCode expected)
    {
        var request = new BodyRequest(
            HttpMethod.Post,
            CreatePath(),
            JsonMediaType,
            PaddedJson(CreateBody, Limit),
            BodyFraming.Chunked,
            chunkSize,
            extension,
            firstExtension);
        Assert.Equal(1_048_576, request.Body.Length);

        long wireLength = ChunkedLength(request.Body.LongLength, chunkSize, extension, firstExtension);
        Assert.Equal(expectedWireLength, wireLength);
        Assert.True(wireLength > Limit, "The chunked wire length does not exceed the configured cap.");
        long framing = wireLength - request.Body.LongLength;
        Assert.True(
            (framing <= ChunkFramingBudget(Limit)) == (expected == HttpStatusCode.Created),
            "The case's framing is not on the side of the budget its expected status implies.");

        int writesBefore = _fixture.Store.WriteCalls.Count;
        ReceivedResponse response = await SendAsync(Host(ServerKind.Kestrel, GuardSequence.BodyLimitFirst), request);

        if (expected == HttpStatusCode.Created)
        {
            Assert.True(response.BodyFullySent ?? true, "The request body was not fully sent before the response.");
            AssertCreated(response, _fixture.Store, writesBefore);
            return;
        }

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, expected);
        AssertProblem(response, expected, ApiProblems.RequestTooLargeDetail);
        AssertEchoesNoRequestContent(response);
        Assert.Equal(writesBefore, _fixture.Store.WriteCalls.Count);
    }

    // TestServer has no chunk framing to count, so its boundary is the guard's alone: up to the limit the probe reads
    // every byte, and from one byte over the limit the request is the 413, declared or streamed.
    [Theory]
    [MemberData(nameof(TestServerBoundaryCases))]
    public async Task TestServerHoldsTheStreamedBoundaryExactly(BodyFraming framing, long overLimit)
    {
        long size = Limit + overLimit;
        var request = new BodyRequest(
            HttpMethod.Post,
            GuardPipelineHost.GuardedProbePath,
            JsonMediaType,
            PaddedJson(CreateBody, size),
            framing);

        ReceivedResponse response = await SendAsync(Host(ServerKind.TestServer, GuardSequence.BodyLimitFirst), request);

        Assert.Null(response.BodyFullySent);
        if (overLimit > 0)
        {
            AssertProblem(response, HttpStatusCode.RequestEntityTooLarge, ApiProblems.RequestTooLargeDetail);
            AssertEchoesNoRequestContent(response);
            return;
        }

        AssertProbe(response, size, maxRequestBodySize: null);
    }

    // The media type is decided before the size: an unacceptable media type is the 415 however large the body,
    // declared or chunked, in either guard order, and no store call is made.
    [Theory]
    [MemberData(nameof(UnacceptableMediaTypeCases))]
    public async Task UnacceptableMediaTypeIs415WhateverTheBodySize(
        ServerKind server,
        GuardSequence order,
        BodyEndpoint endpoint,
        string? contentType,
        BodyFraming framing)
    {
        int callsBefore = _fixture.Store.Calls.Count;

        ReceivedResponse response = await SendAsync(
            Host(server, order),
            EndpointRequest(endpoint, _fixture.RecordId, Limit + 1, contentType, framing));

        AssertProblem(response, HttpStatusCode.UnsupportedMediaType, ApiProblems.UnsupportedMediaTypeDetail);
        AssertEchoesNoRequestContent(response);
        Assert.Equal(callsBefore, _fixture.Store.Calls.Count);
    }

    // Outside the four body endpoints the guards never answer, so an oversized body keeps routing's 404 for a route
    // miss and 405 for a method mismatch, as text or JSON, declared or chunked, in either guard order.
    [Theory]
    [MemberData(nameof(OutsideRouteCases))]
    public async Task OutsideTheBodyEndpointsAnOversizedBodyKeepsTheRoutingStatus(
        ServerKind server,
        GuardSequence order,
        OutsideRoute route,
        string contentType,
        BodyFraming framing)
    {
        (HttpMethod method, string path, HttpStatusCode expected, string detail) = route switch
        {
            OutsideRoute.UnknownApiRoute => (
                HttpMethod.Post,
                UnknownApiPath,
                HttpStatusCode.NotFound,
                ApiProblems.ResourceNotFoundDetail),
            OutsideRoute.RecordPatch => (
                HttpMethod.Patch,
                RecordPath(_fixture.RecordId),
                HttpStatusCode.MethodNotAllowed,
                ApiProblems.MethodNotAllowedDetail),
            _ => throw new ArgumentOutOfRangeException(nameof(route), route, "Not an outside route."),
        };
        int callsBefore = _fixture.Store.Calls.Count;
        var request = new BodyRequest(method, path, contentType, PaddedJson(CreateBody, Limit + 1), framing);

        ReceivedResponse response = await SendAsync(Host(server, order), request);

        AssertProblem(response, expected, detail);
        AssertEchoesNoRequestContent(response);
        Assert.Equal(callsBefore, _fixture.Store.Calls.Count);
    }

    // An empty application/json body is the request-level 400, not a size problem, in either guard order.
    [Theory]
    [MemberData(nameof(EmptyBodyCases))]
    public async Task EmptyJsonBodyIsTheRequestLevel400(ServerKind server, GuardSequence order, BodyEndpoint endpoint)
    {
        int callsBefore = _fixture.Store.Calls.Count;
        (HttpMethod method, string path, _) = EndpointTarget(endpoint, _fixture.RecordId);
        var request = new BodyRequest(method, path, JsonMediaType, [], BodyFraming.ContentLength);

        ReceivedResponse response = await SendAsync(Host(server, order), request);

        AssertProblem(response, HttpStatusCode.BadRequest, ApiProblems.RequestBodyRequiredDetail);
        Assert.Equal(callsBefore, _fixture.Store.Calls.Count);
    }

    // Invoked directly: a marked JSON request has its body counted, and only a chunked body without Content-Length
    // has a writable server limit below the framing allowance raised to that allowance; every other limit is left as
    // it is.
    [Theory]
    [MemberData(nameof(ServerLimitCases))]
    public async Task ServerLimitIsRaisedOnlyForAChunkedBodyBelowTheAllowance(
        long? contentLength,
        bool chunked,
        bool readOnly,
        long? serverLimit,
        bool raised)
    {
        var feature = new RecordingMaxRequestBodySizeFeature(readOnly, serverLimit);
        using var original = new RecordingBodyStream(0, int.MaxValue);
        DefaultHttpContext context = CreateContext(
            EndpointShape.JsonBody,
            JsonMediaType,
            contentLength,
            chunked,
            original,
            feature);
        var nextCalls = 0;
        long? limitSeenByNext = null;
        Stream? bodySeenByNext = null;

        await InvokeGuardAsync(context, Limit, downstream =>
        {
            nextCalls++;
            limitSeenByNext = feature.Value;
            bodySeenByNext = downstream.Request.Body;
            return Task.CompletedTask;
        });

        Assert.Equal(1, nextCalls);
        Assert.NotNull(bodySeenByNext);
        Assert.NotSame(original, bodySeenByNext);
        Assert.Equal(raised ? ChunkedFramingAllowance(Limit) : serverLimit, limitSeenByNext);
        Assert.Equal(raised ? 1 : 0, feature.Writes);
        Assert.Empty(original.Requests);
    }

    // Invoked directly: without a marked endpoint, or with a media type the JSON guard rejects, the guard passes the
    // request on whatever its declared length, without wrapping the body and without touching the server limit.
    [Theory]
    [MemberData(nameof(PassThroughCases))]
    public async Task RequestOutsideTheGuardPassesThroughUntouched(
        EndpointShape endpoint,
        string? contentType,
        long? contentLength,
        bool chunked)
    {
        var feature = new RecordingMaxRequestBodySizeFeature(isReadOnly: false, Limit);
        using var original = new RecordingBodyStream(0, int.MaxValue);
        DefaultHttpContext context = CreateContext(endpoint, contentType, contentLength, chunked, original, feature);
        var nextCalls = 0;
        Stream? bodySeenByNext = null;

        Exception? failure = await Record.ExceptionAsync(() => InvokeGuardAsync(context, Limit, downstream =>
        {
            nextCalls++;
            bodySeenByNext = downstream.Request.Body;
            return Task.CompletedTask;
        }));

        Assert.Null(failure);
        Assert.Equal(1, nextCalls);
        Assert.Same(original, bodySeenByNext);
        Assert.Same(original, context.Request.Body);
        Assert.Equal(0, feature.Reads);
        Assert.Equal(0, feature.Writes);
        Assert.Equal(Limit, feature.Value);
        Assert.Empty(original.Requests);
    }

    // Invoked directly: a declared length over the limit is the 413 before the rest of the pipeline runs, before any
    // byte is read and before the server limit is touched.
    [Theory]
    [MemberData(nameof(DeclaredOverLimitCases))]
    public async Task DeclaredLengthOverTheLimitFailsBeforeAnyRead(long contentLength)
    {
        var feature = new RecordingMaxRequestBodySizeFeature(isReadOnly: false, Limit);
        using var original = new RecordingBodyStream(0, int.MaxValue);
        DefaultHttpContext context = CreateContext(
            EndpointShape.JsonBody,
            JsonMediaType,
            contentLength,
            chunked: false,
            original,
            feature);
        var nextCalls = 0;

        BadHttpRequestException failure = await Assert.ThrowsAsync<BadHttpRequestException>(
            () => InvokeGuardAsync(context, Limit, _ =>
            {
                nextCalls++;
                return Task.CompletedTask;
            }));

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, failure.StatusCode);
        Assert.Equal(0, nextCalls);
        Assert.Same(original, context.Request.Body);
        Assert.Equal(0, feature.Reads);
        Assert.Equal(0, feature.Writes);
        Assert.Empty(original.Requests);
    }

    // Invoked directly over a body longer than the limit: each read asks the inner stream for no more than the bytes
    // the limit still allows plus one, so exactly one byte past the limit is taken; that read fails with the 413 and
    // a further read fails again without reaching the inner stream. A large buffer and a small one alike.
    [Theory]
    [MemberData(nameof(OverLimitReadCases))]
    public async Task CountingStreamTakesAtMostOneBytePastTheLimit(
        ReadOverload overload,
        int bufferSize,
        int maxPerRead,
        int extra)
    {
        using var inner = new RecordingBodyStream(SmallLimit + extra, maxPerRead);
        DefaultHttpContext context = CreateContext(
            EndpointShape.JsonBody,
            JsonMediaType,
            contentLength: null,
            chunked: true,
            inner,
            feature: null);
        long returned = 0;
        BadHttpRequestException? crossing = null;
        BadHttpRequestException? further = null;
        int? readAfterFailure = null;
        var requestsBeforeFurther = -1;
        var requestsAfterFurther = -1;

        await InvokeGuardAsync(context, SmallLimit, async downstream =>
        {
            Stream body = downstream.Request.Body;
            Assert.NotSame(inner, body);
            var buffer = new byte[bufferSize];
            try
            {
                int read;
                while ((read = await ReadOnceAsync(body, overload, buffer)) > 0)
                {
                    returned += read;
                }
            }
            catch (BadHttpRequestException failure)
            {
                crossing = failure;
            }

            requestsBeforeFurther = inner.Requests.Count;
            try
            {
                readAfterFailure = await ReadOnceAsync(body, overload, buffer);
            }
            catch (BadHttpRequestException failure)
            {
                further = failure;
            }

            requestsAfterFurther = inner.Requests.Count;
        });

        Assert.NotNull(crossing);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, crossing.StatusCode);
        Assert.Null(readAfterFailure);
        Assert.NotNull(further);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, further.StatusCode);
        Assert.Equal(requestsBeforeFurther, requestsAfterFurther);
        Assert.Equal(SmallLimit + 1, inner.Supplied);
        Assert.True(returned <= SmallLimit, "The guard returned more bytes than the limit before failing.");
        AssertEveryReadBounded(inner, SmallLimit);
    }

    // Invoked directly over a body of exactly the limit: every byte is returned and the body ends with an ordinary
    // zero-byte read, the last one asking the inner stream for the single byte that would prove it too large.
    [Theory]
    [MemberData(nameof(ExactLimitReadCases))]
    public async Task CountingStreamEndsABodyOfExactlyTheLimitNormally(
        ReadOverload overload,
        int bufferSize,
        int maxPerRead)
    {
        using var inner = new RecordingBodyStream(SmallLimit, maxPerRead);
        DefaultHttpContext context = CreateContext(
            EndpointShape.JsonBody,
            JsonMediaType,
            contentLength: null,
            chunked: true,
            inner,
            feature: null);
        long returned = 0;
        var lastRead = -1;

        await InvokeGuardAsync(context, SmallLimit, async downstream =>
        {
            var buffer = new byte[bufferSize];
            int read;
            while ((read = await ReadOnceAsync(downstream.Request.Body, overload, buffer)) > 0)
            {
                returned += read;
            }

            lastRead = read;
        });

        Assert.Equal(SmallLimit, returned);
        Assert.Equal(0, lastRead);
        Assert.Equal(SmallLimit, inner.Supplied);
        ReadRequest final = inner.Requests[inner.Requests.Count - 1];
        Assert.Equal(1, final.Requested);
        Assert.Equal(SmallLimit, final.SuppliedBefore);
        AssertEveryReadBounded(inner, SmallLimit);
    }

    // Invoked directly: a zero-length read reaches the inner stream as a zero-length read and returns 0.
    [Theory]
    [MemberData(nameof(ReadOverloadCases))]
    public async Task ZeroLengthReadReachesTheInnerStreamAsZero(ReadOverload overload)
    {
        using var inner = new RecordingBodyStream(SmallLimit, int.MaxValue);
        DefaultHttpContext context = CreateContext(
            EndpointShape.JsonBody,
            JsonMediaType,
            contentLength: null,
            chunked: true,
            inner,
            feature: null);
        var read = -1;

        await InvokeGuardAsync(context, SmallLimit, async downstream =>
        {
            read = await ReadOnceAsync(downstream.Request.Body, overload, []);
        });

        Assert.Equal(0, read);
        ReadRequest only = Assert.Single(inner.Requests);
        Assert.Equal(0, only.Requested);
        Assert.Equal(0, inner.Supplied);
    }

    // Invoked directly: invalid array arguments fail with the standard argument exceptions before the inner stream is
    // reached.
    [Theory]
    [MemberData(nameof(InvalidBufferCases))]
    public async Task InvalidBufferArgumentsFailBeforeTheInnerStream(bool asynchronous, InvalidBuffer invalid)
    {
        (byte[]? buffer, int offset, int count, Type expected) = invalid switch
        {
            InvalidBuffer.NullBuffer => ((byte[]?)null, 0, 1, typeof(ArgumentNullException)),
            InvalidBuffer.NegativeOffset => (new byte[8], -1, 1, typeof(ArgumentOutOfRangeException)),
            InvalidBuffer.NegativeCount => (new byte[8], 0, -1, typeof(ArgumentOutOfRangeException)),
            InvalidBuffer.CountPastEnd => (new byte[8], 4, 5, typeof(ArgumentOutOfRangeException)),
            _ => throw new ArgumentOutOfRangeException(nameof(invalid), invalid, "Not an invalid buffer case."),
        };
        using var inner = new RecordingBodyStream(SmallLimit, int.MaxValue);
        DefaultHttpContext context = CreateContext(
            EndpointShape.JsonBody,
            JsonMediaType,
            contentLength: null,
            chunked: true,
            inner,
            feature: null);
        ArgumentException? failure = null;
        int? readWithoutFailure = null;

        await InvokeGuardAsync(context, SmallLimit, async downstream =>
        {
            Stream body = downstream.Request.Body;
            try
            {
                readWithoutFailure = asynchronous
                    ? await body.ReadAsync(buffer!, offset, count)
                    : body.Read(buffer!, offset, count);
            }
            catch (ArgumentException argumentFailure)
            {
                failure = argumentFailure;
            }
        });

        Assert.Null(readWithoutFailure);
        Assert.NotNull(failure);
        Assert.IsType(expected, failure);
        Assert.Empty(inner.Requests);
    }

    // The framing budget the guard gives a chunked body on top of its payload: one-byte chunks carry the most framing
    // an extension-free, canonically sized chunking can, "1\r\n" before and "\r\n" after each byte, 5 bytes per payload
    // byte, and the last chunk "0\r\n" with the "\r\n" ending an empty trailer adds 5.
    private static long ChunkFramingBudget(long limit) => (5 * limit) + 5;

    // The server limit the guard gives a chunked body: the payload limit plus its framing budget, 6 x limit + 5.
    private static long ChunkedFramingAllowance(long limit) => limit + ChunkFramingBudget(limit);

    // The wire length of size payload bytes in chunks of chunkSize bytes, with lowercase hexadecimal sizes, extension
    // after the size of every data chunk, firstExtension instead on the first one when given, and an empty trailer,
    // exactly as the raw client sends them.
    private static long ChunkedLength(long size, int chunkSize, string extension = "", string? firstExtension = null)
    {
        long fullChunks = size / chunkSize;
        var remainder = (int)(size % chunkSize);
        long length = fullChunks * ChunkCost(chunkSize, extension);
        if (remainder > 0)
        {
            length += ChunkCost(remainder, extension);
        }

        if (size > 0 && firstExtension is not null)
        {
            length += firstExtension.Length - extension.Length;
        }

        return length + RawHttp1Client.LastChunk.Length;

        static long ChunkCost(int chunk, string chunkExtension) =>
            chunk.ToString("x", CultureInfo.InvariantCulture).Length + chunkExtension.Length + 2 + chunk + 2;
    }

    // A JSON object padded with insignificant whitespace after its opening brace to exactly size bytes, still the
    // same valid JSON.
    private static byte[] PaddedJson(string json, long size)
    {
        byte[] compact = Encoding.UTF8.GetBytes(json);
        Assert.Equal((byte)'{', compact[0]);
        int length = checked((int)size);
        Assert.True(length >= compact.Length, "The padded size is smaller than the JSON it pads.");

        var padded = new byte[length];
        int paddingLength = length - compact.Length;
        padded[0] = compact[0];
        padded.AsSpan(1, paddingLength).Fill((byte)Padding);
        compact.AsSpan(1).CopyTo(padded.AsSpan(1 + paddingLength));
        return padded;
    }

    private static IEnumerable<(ServerKind Server, GuardSequence Order, BodyEndpoint Endpoint, BodyFraming Framing)>
        EndpointCombinations()
    {
        foreach (ServerKind server in AllServers)
        {
            foreach (GuardSequence order in AllOrders)
            {
                foreach (BodyEndpoint endpoint in AllEndpoints)
                {
                    foreach (BodyFraming framing in AllFramings)
                    {
                        yield return (server, order, endpoint, framing);
                    }
                }
            }
        }
    }

    private static IEnumerable<(ReadOverload Overload, int BufferSize, int MaxPerRead)> ReadCombinations()
    {
        foreach (ReadOverload overload in AllReadOverloads)
        {
            foreach (int bufferSize in new[] { LargeBuffer, SmallBuffer })
            {
                foreach (int maxPerRead in new[] { int.MaxValue, 7 })
                {
                    yield return (overload, bufferSize, maxPerRead);
                }
            }
        }
    }

    private static string CreatePath() =>
        string.Create(CultureInfo.InvariantCulture, $"/api/forms/{FakeForms.TwoPageViewId}/records");

    private static Task<ReceivedResponse> SendAsync(GuardPipelineHost host, BodyRequest request) =>
        host.Kind switch
        {
            GuardHostKind.Kestrel => RawHttp1Client.SendAsync(host.BaseAddress, request),
            GuardHostKind.TestServer => SendThroughClientAsync(host.Client, request),
            _ => throw new ArgumentOutOfRangeException(nameof(host), host.Kind, "Not a host kind."),
        };

    // The raw Content-Type is added without validation, so it reaches the server exactly as written, and a null one
    // sends none. A chunked body comes from a forward-only stream, so no Content-Length can be computed or sent.
    private static async Task<ReceivedResponse> SendThroughClientAsync(HttpClient client, BodyRequest request)
    {
        HttpContent content = request.Framing switch
        {
            BodyFraming.ContentLength => new ByteArrayContent(request.Body),
            BodyFraming.Chunked => new StreamContent(new ForwardOnlyStream(request.Body)),
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.Framing, "Not a body framing."),
        };
        using var message = new HttpRequestMessage(request.Method, request.Path) { Content = content };

        if (request.ContentType is null)
        {
            Assert.False(content.Headers.Contains(ContentTypeHeader));
        }
        else
        {
            Assert.True(content.Headers.TryAddWithoutValidation(ContentTypeHeader, request.ContentType));
        }

        if (request.Framing == BodyFraming.Chunked)
        {
            message.Headers.TransferEncodingChunked = true;
            Assert.Null(content.Headers.ContentLength);
        }
        else
        {
            Assert.Equal(request.Body.LongLength, content.Headers.ContentLength);
        }

        using HttpResponseMessage response = await client.SendAsync(message);
        byte[] body = await response.Content.ReadAsByteArrayAsync();
        return new ReceivedResponse(
            (int)response.StatusCode,
            response.Content.Headers.ContentType?.MediaType,
            response.Headers.Location?.OriginalString,
            body,
            BodyFullySent: null);
    }

    // Parses a response body without ever quoting it, or the parser's view of it, in a failure message.
    private static JsonDocument ParseJson(ReceivedResponse response)
    {
        try
        {
            return JsonDocument.Parse(response.Body);
        }
        catch (JsonException)
        {
            throw new XunitException("The response body is not JSON.");
        }
    }

    private static JsonDocument ParseSuccess(ReceivedResponse response, HttpStatusCode status)
    {
        Assert.Equal((int)status, response.StatusCode);
        Assert.Equal(JsonMediaType, response.MediaType);
        return ParseJson(response);
    }

    // The fixed body of every error on these routes: the status's ProblemDetails as application/problem+json, with
    // the expected detail, a traceId and no errors map.
    private static void AssertProblem(ReceivedResponse response, HttpStatusCode status, string detail)
    {
        Assert.Equal((int)status, response.StatusCode);
        Assert.Equal(ApiProblems.ProblemContentType, response.MediaType);
        using JsonDocument document = ParseJson(response);
        JsonElement body = document.RootElement;
        Assert.Equal(JsonValueKind.Object, body.ValueKind);

        foreach (string member in new[] { "type", "title", "detail" })
        {
            Assert.True(
                body.TryGetProperty(member, out JsonElement text) && text.ValueKind == JsonValueKind.String,
                $"ProblemDetails member '{member}' is missing or not a string.");
        }

        Assert.True(
            body.TryGetProperty("status", out JsonElement statusMember) &&
            statusMember.ValueKind == JsonValueKind.Number,
            "ProblemDetails member 'status' is missing or not a number.");
        Assert.Equal((int)status, statusMember.GetInt32());
        Assert.Equal(detail, body.GetProperty("detail").GetString());
        Assert.True(
            body.TryGetProperty(ApiProblems.TraceIdKey, out JsonElement traceId) &&
            traceId.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(traceId.GetString()),
            "ProblemDetails member 'traceId' is missing or empty.");
        Assert.False(body.TryGetProperty("errors", out _), "A request-level problem carries no errors member.");
    }

    private static void AssertEchoesNoRequestContent(ReceivedResponse response)
    {
        Assert.True(response.Body.Length <= MaxErrorBodyLength, "The error body is larger than a fixed ProblemDetails.");
        string text = Encoding.UTF8.GetString(response.Body);
        foreach (string fragment in RequestFragments)
        {
            bool echoed = text.IndexOf(fragment, StringComparison.Ordinal) >= 0;
            Assert.False(echoed, $"The error body echoes the request text '{fragment}'.");
        }
    }

    // The probe's answer: the bytes it read and the server limit in force when the request reached it.
    private static void AssertProbe(ReceivedResponse response, long bytes, long? maxRequestBodySize)
    {
        using JsonDocument document = ParseSuccess(response, HttpStatusCode.OK);
        JsonElement probe = document.RootElement;
        Assert.Equal(bytes, probe.GetProperty(GuardPipelineHost.ProbeBytesProperty).GetInt64());

        JsonElement limit = probe.GetProperty(GuardPipelineHost.ProbeMaxRequestBodySizeProperty);
        if (maxRequestBodySize is long expected)
        {
            Assert.Equal(JsonValueKind.Number, limit.ValueKind);
            Assert.Equal(expected, limit.GetInt64());
        }
        else
        {
            Assert.Equal(JsonValueKind.Null, limit.ValueKind);
        }
    }

    // A Record DTO whose LastName is the submitted text; returns its globalRecordId.
    private static string AssertRecordWithSubmittedText(JsonElement body)
    {
        Assert.Equal(JsonValueKind.Object, body.ValueKind);
        string? id = body.GetProperty("globalRecordId").GetString();
        Assert.False(string.IsNullOrEmpty(id));
        Assert.Equal(SubmittedText, body.GetProperty("values").GetProperty("LastName").GetString());
        return id;
    }

    private static void AssertEveryReadBounded(RecordingBodyStream inner, long limit)
    {
        foreach (ReadRequest read in inner.Requests)
        {
            Assert.True(
                read.Requested <= limit - read.SuppliedBefore + 1,
                "The guard asked the inner stream for more than the bytes the limit still allows plus one.");
        }
    }

    private static DefaultHttpContext CreateContext(
        EndpointShape endpoint,
        string? contentType,
        long? contentLength,
        bool chunked,
        Stream body,
        IHttpMaxRequestBodySizeFeature? feature)
    {
        var context = new DefaultHttpContext();
        HttpRequest request = context.Request;
        request.Method = HttpMethods.Post;
        request.ContentType = contentType;
        request.ContentLength = contentLength;
        if (chunked)
        {
            request.Headers.TransferEncoding = "chunked";
        }

        request.Body = body;
        if (feature is not null)
        {
            context.Features.Set(feature);
        }

        switch (endpoint)
        {
            case EndpointShape.None:
                break;

            case EndpointShape.Unmarked:
                context.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(), "unmarked"));
                break;

            case EndpointShape.JsonBody:
                context.SetEndpoint(
                    new Endpoint(null, new EndpointMetadataCollection(JsonBodyMetadata.Instance), "body"));
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(endpoint), endpoint, "Not an endpoint shape.");
        }

        return context;
    }

    private static Task InvokeGuardAsync(HttpContext context, long limit, RequestDelegate next) =>
        new RequestBodyLimitMiddleware(next, Committed.Options with { MaxRequestBodySize = limit })
            .InvokeAsync(context);

    private static async Task<int> ReadOnceAsync(Stream stream, ReadOverload overload, byte[] buffer) =>
        overload switch
        {
            ReadOverload.ArraySync => stream.Read(buffer, 0, buffer.Length),
            ReadOverload.ArrayAsync => await stream.ReadAsync(buffer, 0, buffer.Length),
            ReadOverload.Span => stream.Read(buffer.AsSpan()),
            ReadOverload.Memory => await stream.ReadAsync(buffer.AsMemory()),
            _ => throw new ArgumentOutOfRangeException(nameof(overload), overload, "Not a read overload."),
        };

    private GuardPipelineHost Host(ServerKind server, GuardSequence order)
    {
        GuardHostKind kind = server switch
        {
            ServerKind.TestServer => GuardHostKind.TestServer,
            ServerKind.Kestrel => GuardHostKind.Kestrel,
            _ => throw new ArgumentOutOfRangeException(nameof(server), server, "Not a server kind."),
        };
        GuardOrder guards = order switch
        {
            GuardSequence.BodyLimitFirst => GuardOrder.BodyLimitFirst,
            GuardSequence.MediaTypeFirst => GuardOrder.MediaTypeFirst,
            _ => throw new ArgumentOutOfRangeException(nameof(order), order, "Not a guard order."),
        };
        return _fixture.Host(kind, guards);
    }

    private static string RecordPath(string recordId) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"/api/forms/{FakeForms.TwoPageViewId}/records/{Uri.EscapeDataString(recordId)}");

    private static BodyRequest EndpointRequest(
        BodyEndpoint endpoint,
        string recordId,
        long size,
        string? contentType,
        BodyFraming framing)
    {
        (HttpMethod method, string path, string json) = EndpointTarget(endpoint, recordId);
        return new BodyRequest(method, path, contentType, PaddedJson(json, size), framing);
    }

    // The method, path and compact valid JSON body of each body endpoint, aimed at the form and the seeded record.
    private static (HttpMethod Method, string Path, string Json) EndpointTarget(
        BodyEndpoint endpoint,
        string recordId) =>
        endpoint switch
        {
            BodyEndpoint.Create => (HttpMethod.Post, CreatePath(), CreateBody),
            BodyEndpoint.Update => (HttpMethod.Put, RecordPath(recordId), CreateBody),
            BodyEndpoint.Search => (HttpMethod.Post, $"{CreatePath()}/search", SearchBody),
            BodyEndpoint.FieldEvent => (
                HttpMethod.Post,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"/api/forms/{FakeForms.TwoPageViewId}/checkcode/field-event"),
                FieldEventBody),
            _ => throw new ArgumentOutOfRangeException(nameof(endpoint), endpoint, "Not a body endpoint."),
        };

    // 201 with the new record, its Location, the submitted text stored and exactly one more write.
    private static void AssertCreated(ReceivedResponse response, FakeDataStore store, int writesBefore)
    {
        using JsonDocument document = ParseSuccess(response, HttpStatusCode.Created);
        string createdId = AssertRecordWithSubmittedText(document.RootElement);
        Assert.Equal($"{CreatePath()}/{createdId}", response.Location);
        Assert.Equal(
            FieldValue.FromText(SubmittedText),
            Assert.IsType<FakeRecordState>(store.GetRecord(FakeForms.TwoPageViewId, createdId))
                .Values["LastName"]);
        Assert.Equal(writesBefore + 1, store.WriteCalls.Count);
    }

    // The exact success each body endpoint gives a valid body of exactly the limit, so an unrelated failure cannot
    // pass as "not rejected for size": the created record, the updated record, an empty search result shape, or the
    // empty field-event envelope, with exactly the writes each one makes.
    private static void AssertHandlerSuccess(
        ReceivedResponse response,
        BodyEndpoint endpoint,
        FakeDataStore store,
        string recordId,
        int writesBefore)
    {
        switch (endpoint)
        {
            case BodyEndpoint.Create:
                AssertCreated(response, store, writesBefore);
                break;

            case BodyEndpoint.Update:
                using (JsonDocument record = ParseSuccess(response, HttpStatusCode.OK))
                {
                    Assert.Equal(recordId, AssertRecordWithSubmittedText(record.RootElement));
                }

                Assert.Equal(writesBefore + 1, store.WriteCalls.Count);
                break;

            case BodyEndpoint.Search:
                using (JsonDocument matches = ParseSuccess(response, HttpStatusCode.OK))
                {
                    JsonElement body = matches.RootElement;
                    Assert.Equal(
                        new[] { "items", "total", "truncated" },
                        body.EnumerateObject().Select(member => member.Name));
                    Assert.Equal(JsonValueKind.Array, body.GetProperty("items").ValueKind);
                    Assert.Equal(JsonValueKind.Number, body.GetProperty("total").ValueKind);
                    Assert.Contains(
                        body.GetProperty("truncated").ValueKind,
                        new[] { JsonValueKind.True, JsonValueKind.False });
                }

                Assert.Equal(writesBefore, store.WriteCalls.Count);
                break;

            case BodyEndpoint.FieldEvent:
                using (JsonDocument effects = ParseSuccess(response, HttpStatusCode.OK))
                using (JsonDocument envelope = JsonDocument.Parse(EmptyFieldEventEnvelope))
                {
                    Assert.True(
                        JsonElement.DeepEquals(envelope.RootElement, effects.RootElement),
                        "Not the empty field-event envelope.");
                }

                Assert.Equal(writesBefore, store.WriteCalls.Count);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(endpoint), endpoint, "Not a body endpoint.");
        }
    }

    /// <summary>
    /// The API's own composition, <c>Program.cs</c>, hosted for one case: <see cref="ApiFactory"/> on TestServer or
    /// <see cref="KestrelApiFactory"/> on real Kestrel, over a fresh <see cref="FakeDataStore"/> holding
    /// <see cref="FakeForms.TwoPage"/> with one seeded record, so the store's call log holds that case's calls alone.
    /// </summary>
    /// <remarks>
    /// Requests go through the factory's own client: in process on TestServer, over a loopback socket on Kestrel. A
    /// body here is at most one byte over the limit, small enough for the socket client to surface Kestrel's early 413
    /// reliably, so these cases need no raw-socket client; the larger framings of the guard-pipeline cases do.
    /// </remarks>
    private sealed class ComposedApiHost : IAsyncDisposable
    {
        private readonly ApiFactoryBase _factory;

        private readonly HttpClient _client;

        private ComposedApiHost(FakeDataStore store, string recordId, ApiFactoryBase factory, HttpClient client)
        {
            Store = store;
            RecordId = recordId;
            _factory = factory;
            _client = client;
        }

        /// <summary>The store the host serves; seeded with one record and no call.</summary>
        public FakeDataStore Store { get; }

        /// <summary>The <c>GlobalRecordId</c> of the seeded record, the target of the <c>PUT</c>.</summary>
        public string RecordId { get; }

        /// <summary>Starts the composition on <paramref name="server"/>; the caller disposes the host.</summary>
        /// <param name="server">The server to host it on.</param>
        /// <returns>The started host.</returns>
        public static ComposedApiHost Start(ServerKind server)
        {
            var store = new FakeDataStore(FakeForms.TwoPage);
            string recordId = store.SeedRecord(FakeForms.TwoPageViewId);
            ApiFactoryOptions options = ApiFactoryOptions.ForFake(store);

            ApiFactoryBase factory = server switch
            {
                ServerKind.TestServer => new ApiFactory(options),
                ServerKind.Kestrel => new KestrelApiFactory(options),
                _ => throw new ArgumentOutOfRangeException(nameof(server), server, "Not a server kind."),
            };

            try
            {
                HttpClient client = factory is KestrelApiFactory kestrel
                    ? kestrel.CreateKestrelClient()
                    : factory.CreateClient();
                return new ComposedApiHost(store, recordId, factory, client);
            }
            catch
            {
                factory.Dispose();
                throw;
            }
        }

        /// <summary>Sends <paramref name="request"/> to the host once; nothing is retried.</summary>
        public Task<ReceivedResponse> SendAsync(BodyRequest request) => SendThroughClientAsync(_client, request);

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await _factory.DisposeAsync();
        }
    }

    /// <summary>A request a case sends.</summary>
    /// <param name="Method">The HTTP method.</param>
    /// <param name="Path">The origin-form path.</param>
    /// <param name="ContentType">The raw <c>Content-Type</c>, or <see langword="null"/> to send none.</param>
    /// <param name="Body">The body bytes.</param>
    /// <param name="Framing">How the body's length reaches the server.</param>
    /// <param name="ChunkSize">The payload bytes per chunk when the raw client sends the body chunked.</param>
    /// <param name="ChunkExtension">
    /// The ASCII chunk extension, from its <c>;</c>, the raw client writes after the size of every data chunk; empty
    /// for none.
    /// </param>
    /// <param name="FirstChunkExtension">
    /// The extension the first chunk carries instead, or <see langword="null"/> for <paramref name="ChunkExtension"/>.
    /// </param>
    private sealed record BodyRequest(
        HttpMethod Method,
        string Path,
        string? ContentType,
        byte[] Body,
        BodyFraming Framing,
        int ChunkSize = DefaultChunkSize,
        string ChunkExtension = "",
        string? FirstChunkExtension = null);

    /// <summary>A response as received.</summary>
    /// <param name="StatusCode">The status code.</param>
    /// <param name="MediaType">The media type of <c>Content-Type</c>, without parameters.</param>
    /// <param name="Location">The raw <c>Location</c> header, or <see langword="null"/>.</param>
    /// <param name="Body">The decoded body bytes.</param>
    /// <param name="BodyFullySent">
    /// Whether every request byte was handed to the connection before the exchange ended, or <see langword="null"/>
    /// where the in-process client cannot tell.
    /// </param>
    private sealed record ReceivedResponse(
        int StatusCode,
        string? MediaType,
        string? Location,
        byte[] Body,
        bool? BodyFullySent);

    /// <summary>One read the guard asked of the inner stream.</summary>
    /// <param name="Requested">The bytes asked for.</param>
    /// <param name="SuppliedBefore">The bytes the inner stream had supplied before this read.</param>
    private readonly record struct ReadRequest(int Requested, long SuppliedBefore);

    /// <summary>The configured limit as committed in <c>src/EpiInfo.Api/appsettings.json</c>.</summary>
    /// <param name="ConfiguredLimit"><c>Kestrel:Limits:MaxRequestBodySize</c>, parsed as written.</param>
    /// <param name="Options">The API options read from the same file.</param>
    private sealed record CommittedSettings(long ConfiguredLimit, ApiOptions Options)
    {
        public static CommittedSettings Read()
        {
            IConfigurationRoot configuration = new ConfigurationBuilder()
                .SetBasePath(RepositoryPaths.ApiProjectDirectory)
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
                .Build();
            using (configuration as IDisposable)
            {
                string raw = configuration[ApiOptions.MaxRequestBodySizeKey]
                    ?? throw new InvalidOperationException(
                        $"The committed appsettings.json sets no '{ApiOptions.MaxRequestBodySizeKey}'.");
                long configured = long.Parse(raw, NumberStyles.None, CultureInfo.InvariantCulture);
                return new CommittedSettings(configured, ApiOptions.FromConfiguration(configuration));
            }
        }
    }

    /// <summary>
    /// A server limit feature that counts every access and, like Kestrel's, refuses a change once read-only.
    /// </summary>
    private sealed class RecordingMaxRequestBodySizeFeature : IHttpMaxRequestBodySizeFeature
    {
        private readonly bool _isReadOnly;

        private long? _value;

        public RecordingMaxRequestBodySizeFeature(bool isReadOnly, long? value)
        {
            _isReadOnly = isReadOnly;
            _value = value;
        }

        /// <summary>The number of reads of either property.</summary>
        public int Reads { get; private set; }

        /// <summary>The number of changes of <see cref="MaxRequestBodySize"/>.</summary>
        public int Writes { get; private set; }

        /// <summary>The current limit, read without counting.</summary>
        public long? Value => _value;

        public bool IsReadOnly
        {
            get
            {
                Reads++;
                return _isReadOnly;
            }
        }

        public long? MaxRequestBodySize
        {
            get
            {
                Reads++;
                return _value;
            }

            set
            {
                if (_isReadOnly)
                {
                    throw new InvalidOperationException("The server limit is read-only once the body is being read.");
                }

                Writes++;
                _value = value;
            }
        }
    }

    /// <summary>
    /// A request body of a fixed length that records every read asked of it and returns at most a set number of bytes
    /// a read.
    /// </summary>
    private sealed class RecordingBodyStream : Stream
    {
        private readonly long _length;

        private readonly int _maxPerRead;

        private readonly List<ReadRequest> _requests = new();

        public RecordingBodyStream(long length, int maxPerRead)
        {
            _length = length;
            _maxPerRead = maxPerRead;
        }

        /// <summary>Every read asked of the stream, in order.</summary>
        public IReadOnlyList<ReadRequest> Requests => _requests;

        /// <summary>The bytes supplied so far.</summary>
        public long Supplied { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Serve(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer) => Serve(buffer);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(Serve(buffer.AsSpan(offset, count)));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Serve(buffer.Span));

        public override void Flush()
        {
            // Read-only: nothing is ever written, so there is nothing to flush.
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Serve(Span<byte> destination)
        {
            _requests.Add(new ReadRequest(destination.Length, Supplied));
            var served = (int)Math.Min(Math.Min(destination.Length, _maxPerRead), _length - Supplied);
            destination[..served].Fill((byte)Padding);
            Supplied += served;
            return served;
        }
    }

    /// <summary>A read-only, non-seekable view of a byte array, so its length cannot be computed.</summary>
    private sealed class ForwardOnlyStream : Stream
    {
        private readonly byte[] _content;

        private int _position;

        public ForwardOnlyStream(byte[] content)
        {
            _content = content;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ValidateBufferArguments(buffer, offset, count);
            return Read(buffer.AsSpan(offset, count));
        }

        public override int Read(Span<byte> buffer)
        {
            int count = Math.Min(buffer.Length, _content.Length - _position);
            _content.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ValidateBufferArguments(buffer, offset, count);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Read(buffer.AsSpan(offset, count)));
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override void Flush()
        {
            // Read-only: nothing is ever written, so there is nothing to flush.
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// A minimal HTTP/1.1 client over one loopback socket per request. It sends the request with the exact framing
    /// given while it reads the response, so a server that answers before reading the whole body, and then closes the
    /// connection, still has its answer read. The response body may be framed by <c>Content-Length</c>, by chunking,
    /// or by the end of the connection.
    /// </summary>
    private static class RawHttp1Client
    {
        /// <summary>The last chunk and the empty trailer that end a chunked body.</summary>
        public static readonly byte[] LastChunk = "0\r\n\r\n"u8.ToArray();

        private static readonly byte[] Crlf = "\r\n"u8.ToArray();

        // Bounds an exchange so a server that never answers fails the case instead of hanging the run.
        private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromMinutes(2);

        public static async Task<ReceivedResponse> SendAsync(Uri baseAddress, BodyRequest request)
        {
            Assert.Equal(LoopbackHost, baseAddress.Host);
            byte[] wire = Encode(baseAddress, request);

            using var timeout = new CancellationTokenSource(ExchangeTimeout);
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true,
            };
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, baseAddress.Port), timeout.Token);
            using var stream = new NetworkStream(socket, ownsSocket: false);
            using var sending = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);

            Task send = stream.WriteAsync(wire, sending.Token).AsTask();
            ReceivedResponse response;
            try
            {
                response = await ReadResponseAsync(new ResponseReader(stream, timeout.Token));
            }
            catch
            {
                await sending.CancelAsync();
                await CompleteWriteAsync(send);
                throw;
            }

            // Once the response is in, any body the server did not read is abandoned.
            await sending.CancelAsync();
            return response with { BodyFullySent = await CompleteWriteAsync(send) };
        }

        // A server that answers early may close the connection while the body is still being written, so a failed or
        // abandoned write is an outcome the case can check, not a failure of the exchange.
        private static async Task<bool> CompleteWriteAsync(Task send)
        {
            try
            {
                await send;
                return true;
            }
            catch (Exception failure) when (failure is OperationCanceledException or IOException or SocketException)
            {
                return false;
            }
        }

        private static byte[] Encode(Uri baseAddress, BodyRequest request)
        {
            var head = new StringBuilder()
                .Append(CultureInfo.InvariantCulture, $"{request.Method.Method} {request.Path} HTTP/1.1\r\n")
                .Append(CultureInfo.InvariantCulture, $"Host: {baseAddress.Authority}\r\n");
            if (request.ContentType is not null)
            {
                head.Append(CultureInfo.InvariantCulture, $"{ContentTypeHeader}: {request.ContentType}\r\n");
            }

            long bodyLength = request.Framing switch
            {
                BodyFraming.ContentLength => request.Body.LongLength,
                BodyFraming.Chunked => ChunkedLength(
                    request.Body.LongLength,
                    request.ChunkSize,
                    request.ChunkExtension,
                    request.FirstChunkExtension),
                _ => throw new ArgumentOutOfRangeException(nameof(request), request.Framing, "Not a body framing."),
            };
            head.Append(
                request.Framing == BodyFraming.ContentLength
                    ? string.Create(CultureInfo.InvariantCulture, $"Content-Length: {request.Body.Length}\r\n")
                    : "Transfer-Encoding: chunked\r\n");
            head.Append("\r\n");

            byte[] headBytes = Encoding.ASCII.GetBytes(head.ToString());
            var wire = new byte[checked(headBytes.Length + (int)bodyLength)];
            headBytes.CopyTo(wire, 0);
            int written = headBytes.Length;
            if (request.Framing == BodyFraming.ContentLength)
            {
                request.Body.CopyTo(wire, written);
                written += request.Body.Length;
            }
            else
            {
                written = WriteChunks(wire, written, request);
            }

            Assert.Equal(wire.Length, written);
            return wire;
        }

        private static int WriteChunks(byte[] wire, int offset, BodyRequest request)
        {
            byte[] body = request.Body;
            int chunkSize = request.ChunkSize;
            for (var start = 0; start < body.Length; start += chunkSize)
            {
                int size = Math.Min(chunkSize, body.Length - start);
                string extension = start == 0
                    ? request.FirstChunkExtension ?? request.ChunkExtension
                    : request.ChunkExtension;
                offset += Encoding.ASCII.GetBytes(size.ToString("x", CultureInfo.InvariantCulture), wire.AsSpan(offset));
                offset += Encoding.ASCII.GetBytes(extension, wire.AsSpan(offset));
                Crlf.CopyTo(wire, offset);
                offset += Crlf.Length;
                body.AsSpan(start, size).CopyTo(wire.AsSpan(offset));
                offset += size;
                Crlf.CopyTo(wire, offset);
                offset += Crlf.Length;
            }

            LastChunk.CopyTo(wire, offset);
            return offset + LastChunk.Length;
        }

        private static async Task<ReceivedResponse> ReadResponseAsync(ResponseReader reader)
        {
            string statusLine = await reader.ReadLineAsync();
            string[] parts = statusLine.Split(' ', 3);
            if (parts.Length < 2 ||
                !parts[0].StartsWith("HTTP/1.", StringComparison.Ordinal) ||
                !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int status) ||
                status is < 200 or > 599)
            {
                throw new IOException("The response does not start with a final HTTP/1.x status line.");
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (string line = await reader.ReadLineAsync(); line.Length > 0; line = await reader.ReadLineAsync())
            {
                int colon = line.IndexOf(':', StringComparison.Ordinal);
                if (colon <= 0)
                {
                    throw new IOException("A response header line has no name.");
                }

                string name = line[..colon].Trim();
                string value = line[(colon + 1)..].Trim();
                headers[name] = headers.TryGetValue(name, out string? earlier) ? $"{earlier}, {value}" : value;
            }

            using var body = new MemoryStream();
            await ReadBodyAsync(reader, status, headers, body);

            string? mediaType = headers.TryGetValue(ContentTypeHeader, out string? contentType) &&
                MediaTypeHeaderValue.TryParse(contentType, out MediaTypeHeaderValue? parsed)
                    ? parsed.MediaType
                    : null;
            return new ReceivedResponse(
                status,
                mediaType,
                headers.GetValueOrDefault("Location"),
                body.ToArray(),
                BodyFullySent: null);
        }

        // 204 and 304 carry no body; any other response is framed by chunking, by Content-Length, or by the end of the
        // connection.
        private static async Task ReadBodyAsync(
            ResponseReader reader,
            int status,
            Dictionary<string, string> headers,
            MemoryStream body)
        {
            if (status is 204 or 304)
            {
                return;
            }

            if (headers.TryGetValue("Transfer-Encoding", out string? transferEncoding))
            {
                if (!transferEncoding.EndsWith("chunked", StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("The response's transfer coding does not end with chunked.");
                }

                await reader.CopyChunkedAsync(body);
                return;
            }

            if (headers.TryGetValue("Content-Length", out string? contentLength))
            {
                if (!long.TryParse(contentLength, NumberStyles.None, CultureInfo.InvariantCulture, out long length))
                {
                    throw new IOException("The response's Content-Length is not a number.");
                }

                await reader.CopyExactlyAsync(body, length);
                return;
            }

            await reader.CopyToEndAsync(body);
        }
    }

    /// <summary>Buffered reads of an HTTP/1.1 response from a socket stream.</summary>
    private sealed class ResponseReader
    {
        private const int MaxLineLength = 16 * 1024;

        private readonly NetworkStream _stream;

        private readonly CancellationToken _cancellationToken;

        private readonly byte[] _buffer = new byte[16 * 1024];

        private int _start;

        private int _end;

        public ResponseReader(NetworkStream stream, CancellationToken cancellationToken)
        {
            _stream = stream;
            _cancellationToken = cancellationToken;
        }

        /// <summary>Reads one CRLF-terminated line, without the CRLF.</summary>
        public async Task<string> ReadLineAsync()
        {
            using var line = new MemoryStream();
            while (true)
            {
                if (_start == _end && !await FillAsync())
                {
                    throw new IOException("The connection ended inside a response line.");
                }

                int newline = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
                int stop = newline < 0 ? _end : newline;
                line.Write(_buffer, _start, stop - _start);
                _start = newline < 0 ? _end : newline + 1;
                if (line.Length > MaxLineLength)
                {
                    throw new IOException("A response line is too long.");
                }

                if (newline < 0)
                {
                    continue;
                }

                byte[] bytes = line.ToArray();
                if (bytes.Length == 0 || bytes[^1] != (byte)'\r')
                {
                    throw new IOException("A response line does not end with CRLF.");
                }

                return Encoding.ASCII.GetString(bytes, 0, bytes.Length - 1);
            }
        }

        /// <summary>Copies exactly <paramref name="count"/> bytes.</summary>
        public async Task CopyExactlyAsync(MemoryStream destination, long count)
        {
            while (count > 0)
            {
                if (_start == _end && !await FillAsync())
                {
                    throw new IOException("The connection ended inside the response body.");
                }

                var take = (int)Math.Min(count, _end - _start);
                destination.Write(_buffer, _start, take);
                _start += take;
                count -= take;
            }
        }

        /// <summary>Decodes a chunked body and reads its trailer.</summary>
        public async Task CopyChunkedAsync(MemoryStream destination)
        {
            while (true)
            {
                string sizeLine = await ReadLineAsync();
                int extension = sizeLine.IndexOf(';', StringComparison.Ordinal);
                string digits = (extension < 0 ? sizeLine : sizeLine[..extension]).Trim();
                if (!long.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out long size))
                {
                    throw new IOException("A response chunk size is not hexadecimal.");
                }

                if (size == 0)
                {
                    string trailer;
                    do
                    {
                        trailer = await ReadLineAsync();
                    }
                    while (trailer.Length > 0);

                    return;
                }

                await CopyExactlyAsync(destination, size);
                if ((await ReadLineAsync()).Length != 0)
                {
                    throw new IOException("A response chunk does not end with CRLF.");
                }
            }
        }

        /// <summary>
        /// Copies everything up to the end of the connection, the framing of a response that has neither
        /// <c>Content-Length</c> nor chunking. A reset after the server's close also ends it, because a server that
        /// closes with request bytes still unread resets the connection.
        /// </summary>
        public async Task CopyToEndAsync(MemoryStream destination)
        {
            while (true)
            {
                destination.Write(_buffer, _start, _end - _start);
                _start = _end;
                try
                {
                    if (!await FillAsync())
                    {
                        return;
                    }
                }
                catch (IOException failure) when (
                    failure.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionReset })
                {
                    return;
                }
            }
        }

        private async Task<bool> FillAsync()
        {
            _start = 0;
            _end = 0;
            _end = await _stream.ReadAsync(_buffer, _cancellationToken);
            return _end > 0;
        }
    }
}
