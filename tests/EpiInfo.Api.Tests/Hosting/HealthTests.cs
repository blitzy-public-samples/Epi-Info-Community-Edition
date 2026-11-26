using System.Globalization;
using System.Net;
using System.Text.Json;
using EpiInfo.Api.Endpoints;
using EpiInfo.Api.Health;
using EpiInfo.Api.Hosting;
using EpiInfo.Api.Tests.Infrastructure;
using EpiInfo.Domain.Storage;
using EpiInfo.Tests.Shared;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace EpiInfo.Api.Tests.Hosting;

/// <summary>
/// The <c>GET /health</c> contract of <see cref="EpiInfo.Api.Endpoints.HealthEndpoints"/> (AAP 0.3.4.7) without a
/// database: 503 <c>{"status":"unhealthy"}</c>, plain JSON and not ProblemDetails, for an unreachable server and for a
/// missing connection string; 200
/// <c>{"status":"healthy"}</c> when the store's ping answers; and the OpenAPI document publishing each status with its
/// own <c>status</c> literal. Beneath it, the check-level contract of
/// <see cref="SqlServerHealthCheck"/>: Healthy only when the store's ping answers <see langword="true"/>; Unhealthy
/// with the fixed description, no exception and no data for a <see langword="false"/> answer and for every store
/// failure, whatever the registration's failure status; and only the caller's own cancellation propagated. Its log
/// lines carry no exception and name only the exception type, the store operation and the SQL error number (AAP 0.9.3).
/// </summary>
/// <remarks>
/// The class needs no SQL Server: the endpoint cases run the API on TestServer against a port where nothing listens,
/// without a connection string, or over <see cref="FakeDataStore"/>, the document cases read <c>/openapi/v1.json</c>
/// from a host without a connection string, and the check cases run the check directly over
/// that store. It carries no <c>Integration</c> trait and joins no database collection, so
/// <c>--filter "Category!=Integration"</c> selects it. <see cref="HealthIntegrationTests"/> holds the case that needs
/// the rehearsal database.
/// </remarks>
public sealed class HealthTests
{
    // The password of the unreachable connection string: a sentinel no real login uses, searched for in every log line.
    private const string Sentinel = "Sentinel-Health-7f3c9a2e";

    // Port 1 on IPv4 loopback, where nothing listens, so the real SQL Server store fails to connect within the
    // 2-second connect timeout.
    private const string UnreachableConnectionString =
        "Server=127.0.0.1,1;Database=EpiRehearsal;User Id=sa;Password=" + Sentinel +
        ";TrustServerCertificate=True;Connect Timeout=2";

    private const string SqlTextFailure = "unclassified exception holding SQL text";
    private const string LoginFailure = "unavailable: login failed";
    private const string MissingConnectionStringFailure = "unavailable: no connection string";
    private const string UnexpectedFailure = "unexpected store failure";
    private const string InternalCancellation = "internal OperationCanceledException";
    private const string InternalTaskCancellation = "internal TaskCanceledException";
    private const string InternalTokenCancellation = "internal token cancelled";

    private const string PingOperation = nameof(IDataStore.PingAsync);

    private const int LoginFailedNumber = 18456;
    private const int ConstraintViolationNumber = 547;

    private const int PingFailedEventId = 1;
    private const int PingNotAnsweredEventId = 2;

    private const string CheckNameKey = "CheckName";
    private const string ExceptionTypeKey = "ExceptionType";
    private const string OperationKey = "Operation";
    private const string SqlErrorNumberKey = "SqlErrorNumber";

    // The logger category of the check's own lines.
    private static readonly string HealthCheckCategory = typeof(SqlServerHealthCheck).FullName!;

    // The real SQL Server store runs: the connection to port 1 fails, the store reports the database unavailable, and
    // the check answers Unhealthy without passing the exception on, so neither the body nor any log line carries the
    // password sentinel or exception text.
    [Fact]
    public async Task UnreachableServerReturnsUnhealthy()
    {
        await using var factory = new ApiFactory(new ApiFactoryOptions
        {
            ConnectionMode = ApiConnectionMode.Supplied,
            ConnectionString = UnreachableConnectionString,
        });
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync(HealthResponses.RequestPath);

        await HealthResponses.AssertAsync(response, HttpStatusCode.ServiceUnavailable, HealthResponses.Unhealthy);
        Assert.False(factory.Logs.ContainsText(Sentinel), "A captured log entry holds the password sentinel.");
        AssertNoExceptionLogged(factory.Logs);
        AssertPingFailureLogged(factory.Logs);
    }

    // A blank connection string is absent: the API warns at startup naming the key, never a value, keeps serving, and
    // the store fails the ping before any connection is attempted.
    [Fact]
    public async Task MissingConnectionStringReturnsUnhealthy()
    {
        await using var factory = new ApiFactory(new ApiFactoryOptions { ConnectionMode = ApiConnectionMode.None });
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync(HealthResponses.RequestPath);

        await HealthResponses.AssertAsync(response, HttpStatusCode.ServiceUnavailable, HealthResponses.Unhealthy);
        Assert.True(
            factory.Logs.ContainsText(ApiOptions.ConnectionStringKey),
            $"No captured log entry names the configuration key {ApiOptions.ConnectionStringKey}.");
        AssertNoExceptionLogged(factory.Logs);
        CapturedLogEntry pingFailed = AssertPingFailureLogged(factory.Logs);
        Assert.Null(StateValue(pingFailed, SqlErrorNumberKey));
    }

    // The healthy branch without SQL Server: the endpoint answers 200 from exactly one ping of the store, and the check
    // logs nothing.
    [Fact]
    public async Task AnsweringStoreReturnsHealthy()
    {
        var store = new FakeDataStore();
        await using var factory = new ApiFactory(ApiFactoryOptions.ForFake(store));
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync(HealthResponses.RequestPath);

        await HealthResponses.AssertAsync(response, HttpStatusCode.OK, HealthResponses.Healthy);
        Assert.Equal(new FakeStoreCall(PingOperation, IsWrite: false), Assert.Single(store.Calls));
        Assert.DoesNotContain(factory.Logs.Entries, entry => entry.Category == HealthCheckCategory);
    }

    // The published contract pins each status to its one body: the response is the HealthResponse component narrowed to
    // a required status whose only value is that status's literal, so a swapped or other status fails validation.
    [Theory]
    [InlineData(HttpStatusCode.OK, HealthResponse.HealthyStatus)]
    [InlineData(HttpStatusCode.ServiceUnavailable, HealthResponse.UnhealthyStatus)]
    public async Task PublishedResponseAdmitsOnlyItsStatusLiteral(HttpStatusCode status, string literal)
    {
        JsonElement document = await PublishedDocumentAsync();

        JsonElement schema = document.GetProperty("paths").GetProperty(HealthResponses.RequestPath).GetProperty("get")
            .GetProperty("responses").GetProperty(((int)status).ToString(CultureInfo.InvariantCulture))
            .GetProperty("content").GetProperty(ApiTestClient.JsonMediaType).GetProperty("schema");
        Assert.Equal(new[] { "allOf" }, schema.EnumerateObject().Select(keyword => keyword.Name));
        JsonElement[] parts = [.. schema.GetProperty("allOf").EnumerateArray()];
        Assert.Equal(2, parts.Length);
        Assert.Equal("#/components/schemas/HealthResponse", parts[0].GetProperty("$ref").GetString());
        Assert.Equal(new[] { "status" }, parts[1].GetProperty("required").EnumerateArray().Select(Text));
        Assert.Equal(
            new[] { literal },
            parts[1].GetProperty("properties").GetProperty("status").GetProperty("enum").EnumerateArray().Select(Text));
    }

    // The component alone admits exactly the two bodies the handler writes.
    [Fact]
    public async Task PublishedComponentAdmitsOnlyTheTwoStatuses()
    {
        JsonElement component = (await PublishedDocumentAsync())
            .GetProperty("components").GetProperty("schemas").GetProperty("HealthResponse");

        Assert.Equal(new[] { "status" }, component.GetProperty("required").EnumerateArray().Select(Text));
        JsonElement statusSchema = component.GetProperty("properties").GetProperty("status");
        Assert.Equal("string", statusSchema.GetProperty("type").GetString());
        Assert.Equal(
            new[] { HealthResponse.HealthyStatus, HealthResponse.UnhealthyStatus },
            statusSchema.GetProperty("enum").EnumerateArray().Select(Text));
    }

    [Fact]
    public async Task PingAnsweredIsHealthy()
    {
        using var harness = new CheckHarness();

        var result = await harness.CheckAsync(HealthStatus.Unhealthy);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal(SqlServerHealthCheck.HealthyDescription, result.Description);
        Assert.Null(result.Exception);
        Assert.Empty(result.Data);
        var call = Assert.Single(harness.Store.Calls);
        Assert.Equal(new FakeStoreCall(PingOperation, IsWrite: false), call);
        Assert.Empty(harness.Logs.Entries);
    }

    // IDataStore.PingAsync permits a store to report the database as not answering by returning false instead of
    // throwing. A Degraded registration proves the check reports Unhealthy itself rather than the failure status.
    [Fact]
    public async Task PingAnsweringFalseIsUnhealthyWithoutExceptionOrData()
    {
        using var harness = new CheckHarness();
        harness.Store.SetPingResult(false);

        var result = await harness.CheckAsync(HealthStatus.Degraded);

        AssertUnhealthy(result);
        Assert.Equal(PingOperation, Assert.Single(harness.Store.Calls).Method);
        AssertNoExceptionLogged(harness.Logs);
        var entry = Assert.Single(harness.Logs.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(PingNotAnsweredEventId, entry.EventId.Id);
        AssertStateKeys(entry, CheckNameKey);
        Assert.Equal<object?>(SqlServerHealthCheck.Name, StateValue(entry, CheckNameKey));

        // The check caches no result: once the store answers again, the next run is Healthy and logs nothing.
        harness.Store.SetPingResult(true);
        harness.Logs.Clear();

        var recovered = await harness.CheckAsync(HealthStatus.Degraded);

        Assert.Equal(HealthStatus.Healthy, recovered.Status);
        Assert.Equal(SqlServerHealthCheck.HealthyDescription, recovered.Description);
        Assert.Empty(harness.Logs.Entries);
    }

    // Every failure other than the caller's own cancellation is Unhealthy, cancellation inside the store included. The
    // sentinel sits in the message or the inner exception, and so in Exception.ToString, which must reach neither the
    // result nor any log line; the one Warning names only the exception type, the operation and the SQL error number.
    [Theory]
    [InlineData(SqlTextFailure, nameof(InvalidOperationException), null, null)]
    [InlineData(LoginFailure, nameof(DataStoreUnavailableException), PingOperation, LoginFailedNumber)]
    [InlineData(MissingConnectionStringFailure, nameof(DataStoreUnavailableException), PingOperation, null)]
    [InlineData(UnexpectedFailure, nameof(DataStoreWriteException), PingOperation, ConstraintViolationNumber)]
    [InlineData(InternalCancellation, nameof(OperationCanceledException), null, null)]
    [InlineData(InternalTaskCancellation, nameof(TaskCanceledException), null, null)]
    [InlineData(InternalTokenCancellation, nameof(TaskCanceledException), null, null)]
    public async Task StoreFailureIsUnhealthyAndLogsOnlyItsKind(
        string failure,
        string expectedExceptionType,
        string? expectedOperation,
        int? expectedSqlErrorNumber)
    {
        var sentinel = "Sentinel-Health-" + Guid.NewGuid().ToString("N");
        using var harness = new CheckHarness();
        harness.Store.ThrowOn(PingOperation, CreateFailure(failure, sentinel));

        var result = await harness.CheckAsync(HealthStatus.Degraded);

        AssertUnhealthy(result);
        Assert.Equal(PingOperation, Assert.Single(harness.Store.Calls).Method);
        Assert.False(harness.Logs.ContainsText(sentinel));
        AssertNoExceptionLogged(harness.Logs);
        var entry = Assert.Single(harness.Logs.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(PingFailedEventId, entry.EventId.Id);
        AssertStateKeys(entry, CheckNameKey, ExceptionTypeKey, OperationKey, SqlErrorNumberKey);
        Assert.Equal<object?>(SqlServerHealthCheck.Name, StateValue(entry, CheckNameKey));
        Assert.Equal<object?>(expectedExceptionType, StateValue(entry, ExceptionTypeKey));
        Assert.Equal<object?>(expectedOperation, StateValue(entry, OperationKey));
        Assert.Equal<object?>(expectedSqlErrorNumber, StateValue(entry, SqlErrorNumberKey));
    }

    // The check passes the caller's token to the store and lets the store's cancellation for it propagate.
    [Fact]
    public async Task CallerCancellationPropagatesFromTheStore()
    {
        using var harness = new CheckHarness();
        using var caller = new CancellationTokenSource();
        await caller.CancelAsync();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.CheckAsync(HealthStatus.Degraded, caller.Token));

        Assert.Equal(caller.Token, exception.CancellationToken);
        Assert.Equal(PingOperation, Assert.Single(harness.Store.Calls).Method);
        Assert.Empty(harness.Logs.Entries);
    }

    // Once the caller has cancelled, an OperationCanceledException from the store propagates even when it carries no
    // token, as a driver aborted mid-command may raise it; it is neither reported as Unhealthy nor logged.
    [Fact]
    public async Task StoreCancellationPropagatesOnceTheCallerHasCancelled()
    {
        using var harness = new CheckHarness();
        var storeCancellation = new OperationCanceledException();
        harness.Store.ThrowOn(PingOperation, storeCancellation);
        using var caller = new CancellationTokenSource();
        await caller.CancelAsync();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.CheckAsync(HealthStatus.Degraded, caller.Token));

        Assert.Same(storeCancellation, exception);
        Assert.Equal(PingOperation, Assert.Single(harness.Store.Calls).Method);
        Assert.Empty(harness.Logs.Entries);
    }

    private static Exception CreateFailure(string failure, string sentinel) =>
        failure switch
        {
            SqlTextFailure => FakeDataStore.SqlTextException(sentinel),

            // A classified failure keeps the driver's exception as its inner exception, as the SQL Server store
            // attaches it, so the SQL text and the sentinel are in its ToString but never in its Message.
            LoginFailure => new DataStoreUnavailableException(
                PingOperation,
                WriteOutcome.NotWritten,
                LoginFailedNumber,
                FakeDataStore.SqlTextException(sentinel)),
            MissingConnectionStringFailure => new DataStoreUnavailableException(
                PingOperation,
                WriteOutcome.NotWritten),
            UnexpectedFailure => new DataStoreWriteException(
                PingOperation,
                WriteOutcome.NotWritten,
                ConstraintViolationNumber,
                FakeDataStore.SqlTextException(sentinel)),

            // Cancellation the caller did not request: no token, or a token the store cancelled itself, such as a
            // command-timeout source's. The fake faults the task with the first and cancels it with the second.
            InternalCancellation => new OperationCanceledException(sentinel),
            InternalTaskCancellation => new TaskCanceledException(sentinel),
            InternalTokenCancellation => new OperationCanceledException(
                sentinel,
                new CancellationToken(canceled: true)),

            _ => throw new ArgumentOutOfRangeException(nameof(failure), failure, "Unknown store failure."),
        };

    private static void AssertUnhealthy(HealthCheckResult result)
    {
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal(SqlServerHealthCheck.UnhealthyDescription, result.Description);
        Assert.Null(result.Exception);
        Assert.Empty(result.Data);
    }

    private static void AssertNoExceptionLogged(CapturingLoggerProvider logs) =>
        Assert.All(logs.Entries, entry =>
        {
            Assert.Null(entry.ExceptionType);
            Assert.Null(entry.ExceptionMessage);
        });

    // The check's one line for a ping the store failed by reporting the database unavailable, as it does for an
    // unreachable server and for a missing connection string alike.
    private static CapturedLogEntry AssertPingFailureLogged(CapturingLoggerProvider logs)
    {
        CapturedLogEntry entry = Assert.Single(logs.Entries, candidate => candidate.Category == HealthCheckCategory);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(PingFailedEventId, entry.EventId.Id);
        AssertStateKeys(entry, CheckNameKey, ExceptionTypeKey, OperationKey, SqlErrorNumberKey);
        Assert.Equal<object?>(SqlServerHealthCheck.Name, StateValue(entry, CheckNameKey));
        Assert.Equal<object?>(nameof(DataStoreUnavailableException), StateValue(entry, ExceptionTypeKey));
        Assert.Equal<object?>(PingOperation, StateValue(entry, OperationKey));
        return entry;
    }

    // The structured state holds exactly the template's arguments plus the template itself, so the line can carry
    // nothing the template does not name.
    private static void AssertStateKeys(CapturedLogEntry entry, params string[] arguments) =>
        Assert.Equal(
            arguments.Append("{OriginalFormat}").Order(StringComparer.Ordinal),
            entry.State.Select(pair => pair.Key).Order(StringComparer.Ordinal));

    private static object? StateValue(CapturedLogEntry entry, string key) =>
        Assert.Single(entry.State, pair => pair.Key == key).Value;

    // The document the API serves at /openapi/v1.json. Describing the endpoints reads no data, so the host has no
    // connection string.
    private static async Task<JsonElement> PublishedDocumentAsync()
    {
        await using var factory = new ApiFactory(new ApiFactoryOptions { ConnectionMode = ApiConnectionMode.None });
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ApiTestClient.ReadJsonElementAsync(response);
    }

    private static string? Text(JsonElement value) => value.GetString();

    /// <summary>The check over a fresh store, logging only to a capturing provider that accepts every level.</summary>
    private sealed class CheckHarness : IDisposable
    {
        private readonly ILoggerFactory _loggerFactory;

        public CheckHarness()
        {
            Store = new FakeDataStore();
            Logs = new CapturingLoggerProvider();
            _loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(Logs));
            Check = new SqlServerHealthCheck(Store, _loggerFactory.CreateLogger<SqlServerHealthCheck>());
        }

        public FakeDataStore Store { get; }

        public CapturingLoggerProvider Logs { get; }

        public SqlServerHealthCheck Check { get; }

        /// <summary>
        /// Runs the check once under a registration whose failure status is <paramref name="failureStatus"/>.
        /// </summary>
        public Task<HealthCheckResult> CheckAsync(
            HealthStatus failureStatus,
            CancellationToken cancellationToken = default) =>
            Check.CheckHealthAsync(
                new HealthCheckContext
                {
                    Registration = new HealthCheckRegistration(
                        SqlServerHealthCheck.Name,
                        Check,
                        failureStatus,
                        tags: null),
                },
                cancellationToken);

        public void Dispose()
        {
            _loggerFactory.Dispose();
            Logs.Dispose();
        }
    }
}

/// <summary>
/// The SC-1 health proof (AAP 0.7.1): with the rehearsal database reachable, <c>GET /health</c> answers 200
/// <c>{"status":"healthy"}</c> from one <c>SELECT 1</c> through the SQL Server store.
/// </summary>
/// <remarks>
/// The case writes nothing and depends on no count, so it takes no <see cref="DatabaseWriteLock"/>. It never skips:
/// an unreachable or unconfigured database fails it through <see cref="TestDatabase"/>, naming <c>SQL_HOST</c> and
/// <c>MSSQL_SA_PASSWORD</c>.
/// </remarks>
[Collection(DatabaseCollections.Database)]
[Trait("Category", "Integration")]
public sealed class HealthIntegrationTests
{
    [Fact]
    public async Task HealthyWhenDatabaseAnswers()
    {
        await using ApiFactory factory = await ApiFactory.ForDatabaseAsync();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync(HealthResponses.RequestPath);

        await HealthResponses.AssertAsync(response, HttpStatusCode.OK, HealthResponses.Healthy);

        // The one command of the request is the ping's trivial query, outside a transaction, and it succeeded.
        RecordedCommand ping = Assert.Single(factory.Recorder.Commands);
        Assert.Equal(nameof(IDataStore.PingAsync), ping.Operation);
        Assert.Equal("SELECT 1;", ping.CommandText.Trim());
        Assert.Empty(ping.Parameters);
        Assert.False(ping.InTransaction);
        Assert.True(ping.Succeeded);
    }
}

/// <summary>The two <c>GET /health</c> bodies and the assertion both test classes apply to a response.</summary>
file static class HealthResponses
{
    public const string RequestPath = "/health";
    public const string Healthy = """{"status":"healthy"}""";
    public const string Unhealthy = """{"status":"unhealthy"}""";

    /// <summary>Asserts the status, the plain JSON media type and the exact body.</summary>
    /// <remarks>
    /// Exact equality is the shape and redaction proof: it admits no ProblemDetails member (<c>type</c>,
    /// <c>title</c>, <c>detail</c>, <c>traceId</c>), no exception text, server address or password, no whitespace and
    /// no other member. <c>application/json</c> rather than <c>application/problem+json</c> shows the 503 is not
    /// ProblemDetails. The failure message gives the body's length only, never the body, as every message of the suite
    /// describes a response by its shape.
    /// </remarks>
    public static async Task AssertAsync(HttpResponseMessage response, HttpStatusCode status, string expectedBody)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(ApiTestClient.JsonMediaType, response.Content.Headers.ContentType?.MediaType);

        string body = await response.Content.ReadAsStringAsync();
        Assert.True(
            string.Equals(expectedBody, body, StringComparison.Ordinal),
            $"The {RequestPath} body is not exactly {expectedBody}; it has {body.Length} characters.");
    }
}

