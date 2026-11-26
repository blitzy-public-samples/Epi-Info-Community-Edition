using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using EpiInfo.Api.Tests.Infrastructure;
using EpiInfo.Tests.Shared;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace EpiInfo.Api.Tests.Performance;

/// <summary>
/// SC-11: over 100 sequential calls, <c>GET /api/forms/{viewId}/records/at/{position}</c> has a p95 of at most 300 ms
/// and a <c>POST</c> save a p95 of at most 500 ms.
/// </summary>
/// <remarks>
/// <para>
/// The class carries the <c>Performance</c> category beside <c>Integration</c>, so the default filter of
/// <c>EpiInfo.Api.Tests.csproj</c> (<c>Category!=Performance</c>) and the non-performance integration run
/// (<c>Category=Integration&amp;Category!=Performance</c>) leave it out, and a timing result can never fail a bare
/// <c>dotnet test EpiInfo.Web.sln</c>. It runs with <c>dotnet test tests/EpiInfo.Api.Tests --filter
/// Category=Performance</c>.
/// </para>
/// <para>
/// Each fact hosts the API in process on the rehearsal database (<see cref="ApiFactory.ForDatabaseAsync"/>), so a
/// database that is not configured or not reachable fails the fact with the <see cref="TestDatabase"/> message and
/// never skips. Host startup, one warm-up call, request construction and every assertion lie outside the timed
/// window; the window holds the request and the read of the whole response body. Durations come from
/// <see cref="Stopwatch"/> only, never from the host's <see cref="TimeProvider"/>, which the factory replaces with a
/// controlled clock for the save stamps.
/// </para>
/// <para>
/// The read fact holds the shared <see cref="DatabaseWriteLock"/>, because its positions come from a count that must
/// not move while it runs. The save fact holds the exclusive lock, because it writes: 101 <c>CaseReport</c> records,
/// each carrying a fresh <see cref="TestDatabase.NewMarker"/> marker in <see cref="TestDatabase.MarkerField"/> and
/// nothing else. They stay in the database, so reruns need no reset. No seeded row is edited or deleted, and no SQL is
/// sent directly.
/// </para>
/// <para>
/// Output is timings, counts, the form name and its view id only: never a connection string, a marker or a record
/// value.
/// </para>
/// </remarks>
[Collection(DatabaseCollections.Database)]
[Trait("Category", "Integration")]
[Trait("Category", "Performance")]
public sealed class PerformanceTests
{
    private const int SampleCount = 100;
    private const int ReadP95ThresholdMs = 300;
    private const int SaveP95ThresholdMs = 500;
    private const string FormName = "CaseReport";

    // Bounds each fact, lock waits included, so a hung database cannot stall the run.
    private static readonly TimeSpan FactTimeout = TimeSpan.FromMinutes(10);

    private readonly ITestOutputHelper _output;

    /// <summary>Creates the test class with the output that receives each fact's latency summary.</summary>
    /// <param name="output">The xUnit test output.</param>
    public PerformanceTests(ITestOutputHelper output)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
    }

    [Fact]
    public async Task RecordAtPosition_P95_IsWithin300Milliseconds()
    {
        using var timeout = new CancellationTokenSource(FactTimeout);
        CancellationToken ct = timeout.Token;

        await using ApiFactory factory = await ApiFactory.ForDatabaseAsync();
        using HttpClient client = factory.CreateClient();
        await using DatabaseWriteLock dbLock = await DatabaseWriteLock.AcquireSharedAsync(ct);

        int viewId = await ApiTestClient.GetViewIdAsync(client, FormName, ct);
        string recordsPath = RecordsPath(viewId);

        // The active set: no includeDeleted, so an active record's intrinsic position equals its index in the set.
        int count = await ReadActiveCountAsync(client, recordsPath, ct);
        Assert.True(count >= 1, $"Form {FormName} (view {viewId}) has no active record to open.");

        // Warm-up, untimed and discarded: the first request compiles the endpoint and opens the first connection.
        using (HttpResponseMessage warmUp = await client.GetAsync(RecordAtPath(recordsPath, 1), ct))
        {
            _ = await warmUp.Content.ReadAsByteArrayAsync(ct);
            Assert.True(
                warmUp.StatusCode == HttpStatusCode.OK,
                $"Warm-up GET at/1 returned {DescribeStatus(warmUp.StatusCode)}, expected 200.");
        }

        var durations = new double[SampleCount];
        for (int i = 0; i < SampleCount; i++)
        {
            // Spreads the samples over 1..count, both ends included; with fewer than SampleCount records they repeat.
            int position = 1 + (int)((long)i * (count - 1) / (SampleCount - 1));
            string path = RecordAtPath(recordsPath, position);

            long start = Stopwatch.GetTimestamp();
            using HttpResponseMessage response = await client.GetAsync(path, ct);
            byte[] body = await response.Content.ReadAsByteArrayAsync(ct);
            durations[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

            AssertRecordAt(response.StatusCode, body, position, i);
        }

        LatencySummary summary = LatencySummary.From(durations);
        string line = string.Create(
            CultureInfo.InvariantCulture,
            $"SC-11 GET /api/forms/{{viewId}}/records/at/{{position}} ({FormName}, view {viewId}, active set, " +
            $"count={count}, in-process host): {summary}; threshold p95 <= {ReadP95ThresholdMs} ms");
        _output.WriteLine(line);

        Assert.True(
            summary.P95 <= ReadP95ThresholdMs,
            string.Create(
                CultureInfo.InvariantCulture,
                $"GET at/{{position}} p95 is {summary.P95:F1} ms, above the {ReadP95ThresholdMs} ms threshold. " +
                $"{line}"));
    }

    [Fact]
    public async Task PostSave_P95_IsWithin500Milliseconds()
    {
        using var timeout = new CancellationTokenSource(FactTimeout);
        CancellationToken ct = timeout.Token;

        await using ApiFactory factory = await ApiFactory.ForDatabaseAsync();
        using HttpClient client = factory.CreateClient();
        await using DatabaseWriteLock dbLock = await DatabaseWriteLock.AcquireExclusiveAsync(ct);

        int viewId = await ApiTestClient.GetViewIdAsync(client, FormName, ct);
        string recordsPath = RecordsPath(viewId);

        // Warm-up, untimed and discarded: CreateRecordAsync asserts the 201, the globalRecordId and the Location.
        _ = await ApiTestClient.CreateRecordAsync(client, viewId, MarkerValues(), ct);

        var durations = new double[SampleCount];
        for (int i = 0; i < SampleCount; i++)
        {
            // Built before the window opens, so the timing holds the request and the response only.
            using StringContent content = MarkerBody();

            long start = Stopwatch.GetTimestamp();
            using HttpResponseMessage response = await client.PostAsync(recordsPath, content, ct);
            byte[] body = await response.Content.ReadAsByteArrayAsync(ct);
            durations[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

            AssertCreated(response.StatusCode, body, i);
        }

        LatencySummary summary = LatencySummary.From(durations);
        string line = string.Create(
            CultureInfo.InvariantCulture,
            $"SC-11 POST /api/forms/{{viewId}}/records ({FormName}, view {viewId}, in-process host): {summary}; " +
            $"threshold p95 <= {SaveP95ThresholdMs} ms");
        _output.WriteLine(line);

        Assert.True(
            summary.P95 <= SaveP95ThresholdMs,
            string.Create(
                CultureInfo.InvariantCulture,
                $"POST save p95 is {summary.P95:F1} ms, above the {SaveP95ThresholdMs} ms threshold. {line}"));
    }

    private static string RecordsPath(int viewId) =>
        string.Create(CultureInfo.InvariantCulture, $"/api/forms/{viewId}/records");

    private static string RecordAtPath(string recordsPath, int position) =>
        string.Create(CultureInfo.InvariantCulture, $"{recordsPath}/at/{position}");

    // The record count of the active set, from GET .../records/count without includeDeleted.
    private static async Task<int> ReadActiveCountAsync(HttpClient client, string recordsPath, CancellationToken ct)
    {
        string path = recordsPath + "/count";
        using HttpResponseMessage response = await client.GetAsync(path, ct);
        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"GET {path} returned {DescribeStatus(response.StatusCode)}, expected 200.");

        JsonElement body = await ApiTestClient.ReadJsonElementAsync(response, ct);
        if (body.ValueKind == JsonValueKind.Object &&
            body.TryGetProperty("count", out JsonElement count) &&
            count.ValueKind == JsonValueKind.Number &&
            count.TryGetInt32(out int value))
        {
            return value;
        }

        throw new XunitException($"GET {path} returned no 32-bit integer count; the body is {body.ValueKind}.");
    }

    // Checks a timed at/{position} response outside the window: 200, and a Record whose intrinsic position is the
    // requested index, which holds for an active record opened from the active set.
    private static void AssertRecordAt(HttpStatusCode status, byte[] body, int position, int sample)
    {
        Assert.True(
            status == HttpStatusCode.OK,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Sample {sample}: GET at/{position} returned {DescribeStatus(status)}, expected 200."));

        using JsonDocument document = ParseBody(body, sample);
        JsonElement root = document.RootElement;
        int? returned = root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("position", out JsonElement member) &&
            member.ValueKind == JsonValueKind.Number &&
            member.TryGetInt32(out int value)
                ? value
                : null;
        Assert.True(
            returned == position,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Sample {sample}: GET at/{position} returned a Record whose position is " +
                $"{returned?.ToString(CultureInfo.InvariantCulture) ?? "not a 32-bit integer"}."));
    }

    // Checks a timed POST response outside the window: 201 and a non-empty string globalRecordId, which is not printed.
    private static void AssertCreated(HttpStatusCode status, byte[] body, int sample)
    {
        Assert.True(
            status == HttpStatusCode.Created,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Sample {sample}: POST returned {DescribeStatus(status)}, expected 201."));

        using JsonDocument document = ParseBody(body, sample);
        JsonElement root = document.RootElement;
        bool hasId = root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("globalRecordId", out JsonElement id) &&
            id.ValueKind == JsonValueKind.String &&
            !string.IsNullOrEmpty(id.GetString());
        Assert.True(
            hasId,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Sample {sample}: POST returned a 201 body without a non-empty string globalRecordId."));
    }

    // Parses a response body; a parse failure is reported by position only, because the parser's message can quote
    // the body.
    private static JsonDocument ParseBody(byte[] body, int sample)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException failure)
        {
            throw new XunitException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Sample {sample}: the response body of {body.Length} bytes is not JSON (line " +
                    $"{failure.LineNumber?.ToString(CultureInfo.InvariantCulture) ?? "?"}, byte " +
                    $"{failure.BytePositionInLine?.ToString(CultureInfo.InvariantCulture) ?? "?"})."));
        }
    }

    // The one data field a performance save submits: a fresh marker in LastName, nvarchar(4000) on CaseReport1 with
    // no pattern, no MaxLength and no legal values. No seeded field is required, so the body is valid on its own.
    private static Dictionary<string, object?> MarkerValues() =>
        new(StringComparer.Ordinal) { [TestDatabase.MarkerField] = TestDatabase.NewMarker() };

    // {"values": {...}} as application/json; charset=utf-8, serialised with the wire settings of the API tests.
    private static StringContent MarkerBody() =>
        new(
            JsonSerializer.Serialize(new { values = MarkerValues() }, ApiTestClient.Json),
            Encoding.UTF8,
            ApiTestClient.JsonMediaType);

    private static string DescribeStatus(HttpStatusCode status) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)status} {status}");

    // Nearest-rank latency percentiles of one fact's samples, in milliseconds.
    private readonly record struct LatencySummary(int Count, double P50, double P95, double Max)
    {
        public static LatencySummary From(double[] durations)
        {
            double[] sorted = [.. durations];
            Array.Sort(sorted);
            return new LatencySummary(
                sorted.Length,
                NearestRank(sorted, 50),
                NearestRank(sorted, 95),
                NearestRank(sorted, 100));
        }

        public override string ToString() =>
            string.Create(
                CultureInfo.InvariantCulture,
                $"n={Count} p50={P50:F1} ms p95={P95:F1} ms max={Max:F1} ms");

        // The value at 1-based rank ceil(percent / 100 × n), in integer arithmetic so no rounding moves the rank: for
        // n = 100, p50 is index 49, p95 index 94 and the maximum index 99.
        private static double NearestRank(double[] sortedAscending, int percent)
        {
            if (sortedAscending.Length == 0)
            {
                throw new ArgumentException("A percentile needs at least one sample.", nameof(sortedAscending));
            }

            int rank = ((percent * sortedAscending.Length) + 99) / 100;
            return sortedAscending[Math.Clamp(rank, 1, sortedAscending.Length) - 1];
        }
    }
}
