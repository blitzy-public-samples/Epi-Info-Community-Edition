using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using EpiInfo.Api.Endpoints;
using EpiInfo.Api.Health;
using EpiInfo.Api.Hosting;
using EpiInfo.Api.Tests.Infrastructure;
using EpiInfo.Domain.Storage;
using EpiInfo.Tests.Shared;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace EpiInfo.Api.Tests.Hosting;

/// <summary>
/// The <c>GET /health</c> contract of <see cref="EpiInfo.Api.Endpoints.HealthEndpoints"/> (AAP 0.3.4.7) without a
/// database: 503 <c>{"status":"unhealthy"}</c>, plain JSON and not ProblemDetails, for an unreachable server, for a
/// server that accepts the connection and never answers, within the check's own bound, for a store ping that ignores
/// cancellation, each request within that bound and all from one ping, and for a missing connection string; 200
/// <c>{"status":"healthy"}</c> when the store's ping answers; and the OpenAPI document publishing each status with its
/// own <c>status</c> literal. Beneath it, the check-level contract of
/// <see cref="SqlServerHealthCheck"/>: Healthy only when the store's ping answers <see langword="true"/>; Unhealthy
/// with the fixed description, no exception and no data for a <see langword="false"/> answer, for every store
/// failure and for a ping still pending at the check's bound, whatever the registration's failure status; one ping in
/// flight, which overlapping and later runs join and whose end lets the next run ping again; each run bounded from
/// the start of the ping it shares, so a joining run waits only what is left of that bound and answers at once when
/// the ping is overdue; no run's bound held by a store that blocks its caller before returning its task; an
/// abandoned ping's failure observed and logged by nothing; and only the caller's own cancellation propagated, ending
/// that caller's wait alone. Its log lines carry no exception and name only the check, the exception type, the store
/// operation and the SQL error number (AAP 0.9.3).
/// </summary>
/// <remarks>
/// The class needs no SQL Server: the endpoint cases run the API on TestServer against a port where nothing listens,
/// against a loopback listener that never answers,
/// without a connection string, or over <see cref="FakeDataStore"/>, the document cases read <c>/openapi/v1.json</c>
/// from a host without a connection string, and the check cases run the check directly over
/// that store. It carries no <c>Integration</c> trait and joins no database collection, so
/// <c>--filter "Category!=Integration"</c> selects it. <see cref="HealthIntegrationTests"/> holds the cases that need
/// the rehearsal database.
/// </remarks>
public sealed class HealthTests
{
    // The password of the unreachable connection string: a sentinel no real login uses, searched for in every log line.
    private const string Sentinel = "Sentinel-Health-7f3c9a2e";

    // Port 1 on IPv4 loopback, where nothing listens, so the real SQL Server store fails to connect within the
    // 2-second connect timeout. The string names no database, because the server is never reached and the tests never
    // name a database.
    private const string UnreachableConnectionString =
        "Server=127.0.0.1,1;User Id=sa;Password=" + Sentinel +
        ";TrustServerCertificate=True;Connect Timeout=2";

    private const string SqlTextFailure = "unclassified exception holding SQL text";
    private const string LoginFailure = "unavailable: login failed";
    private const string MissingConnectionStringFailure = "unavailable: no connection string";
    private const string UnexpectedFailure = "unexpected store failure";
    private const string InternalCancellation = "internal OperationCanceledException";
    private const string InternalTaskCancellation = "internal TaskCanceledException";
    private const string InternalTokenCancellation = "internal token cancelled";

    // How a held ping is released besides a store failure: answered true, or answered false.
    private const string ReleasedAnswering = "released answering";
    private const string ReleasedNotAnswering = "released not answering";

    private const string PingOperation = nameof(IDataStore.PingAsync);

    private const int LoginFailedNumber = 18456;
    private const int ConstraintViolationNumber = 547;

    private const int PingFailedEventId = 1;
    private const int PingNotAnsweredEventId = 2;
    private const int PingTimedOutEventId = 3;

    // SqlClient's default connect timeout, which an open against a server that never answers would wait out in full.
    private const int DefaultConnectTimeoutSeconds = 15;

    // The 405 wire contract written out here rather than read from ApiProblems, so a changed product text fails.
    private const string ProblemMediaType = "application/problem+json";
    private const string ExpectedMethodNotAllowedDetail = "method not allowed";

    private const string CheckNameKey = "CheckName";
    private const string ExceptionTypeKey = "ExceptionType";
    private const string OperationKey = "Operation";
    private const string SqlErrorNumberKey = "SqlErrorNumber";

    // The logger category of the check's own lines.
    private static readonly string HealthCheckCategory = typeof(SqlServerHealthCheck).FullName!;

    // The logger category of the framework's health-check service, which runs the check for the endpoint.
    private const string HealthCheckServiceCategory =
        "Microsoft.Extensions.Diagnostics.HealthChecks.DefaultHealthCheckService";

    // A bound short enough for a check-level case, and one no case reaches, so only the ping's release or the caller
    // ends a run's wait.
    private static readonly TimeSpan ShortPingTimeout = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan UnreachedPingTimeout = TimeSpan.FromMinutes(5);

    // A bound long enough that a run can join the ping half-way through it, with room for the timer's lateness on a
    // busy host before the joining run's wait could be mistaken for a full bound.
    private static readonly TimeSpan JoinedPingTimeout = TimeSpan.FromSeconds(2);

    // How much earlier than its bound a run may be measured to end, for the timer's and the stopwatch's resolution.
    private static readonly TimeSpan TimerTolerance = TimeSpan.FromMilliseconds(50);

    // What a run of the endpoint may take beyond the check's bound, for the hosting and the store's own work; the sum
    // stays well inside the connect timeout the ping would otherwise wait out.
    private static readonly TimeSpan EndpointMargin = TimeSpan.FromSeconds(5);

    // How long a held-ping case waits for the check before it fails, so a check that never ends fails the case
    // instead of hanging the run.
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    /// <summary>Each method sent to <c>/health</c>, with the client served from a folder and with it absent.</summary>
    public static TheoryData<bool, string> HealthMethodCases
    {
        get
        {
            string[] methods = ["GET", "HEAD", "POST", "PUT", "PATCH", "DELETE", "OPTIONS"];
            var data = new TheoryData<bool, string>();
            foreach (bool clientServed in new[] { true, false })
            {
                foreach (string method in methods)
                {
                    data.Add(clientServed, method);
                }
            }

            return data;
        }
    }

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

    // The real SQL Server store against a server that accepts the connection and never answers, as an unreachable or
    // hung server does, with SqlClient's default 15-second connect timeout: the check's own bound ends the ping, so
    // /health answers 503 within about PingTimeout instead of waiting out the connect timeout, and the one line is
    // the timeout's, with no exception and no password sentinel.
    [Fact]
    public async Task StalledServerReturnsUnhealthyWithinThePingTimeout()
    {
        await using StalledServer server = StalledServer.Start();
        await using var factory = new ApiFactory(new ApiFactoryOptions
        {
            ConnectionMode = ApiConnectionMode.Supplied,
            ConnectionString = string.Create(
                CultureInfo.InvariantCulture,
                $"Server=tcp:127.0.0.1,{server.Port};User Id=sa;Password={Sentinel};TrustServerCertificate=True;" +
                $"Connect Timeout={DefaultConnectTimeoutSeconds}"),
        });
        using HttpClient client = factory.CreateClient();
        var clock = Stopwatch.StartNew();

        using HttpResponseMessage response = await client.GetAsync(HealthResponses.RequestPath);

        TimeSpan elapsed = clock.Elapsed;
        await HealthResponses.AssertAsync(response, HttpStatusCode.ServiceUnavailable, HealthResponses.Unhealthy);
        Assert.True(
            elapsed < SqlServerHealthCheck.PingTimeout + EndpointMargin,
            string.Create(
                CultureInfo.InvariantCulture,
                $"/health took {elapsed.TotalSeconds:F1} s against a server that never answers; the check's bound is " +
                $"{SqlServerHealthCheck.PingTimeout.TotalSeconds} s and the connect timeout " +
                $"{DefaultConnectTimeoutSeconds} s."));
        Assert.False(factory.Logs.ContainsText(Sentinel), "A captured log entry holds the password sentinel.");
        AssertNoExceptionLogged(factory.Logs);
        Assert.DoesNotContain(factory.Logs.Entries, candidate => candidate.Category == HealthCheckServiceCategory);
        AssertPingTimedOut(Assert.Single(factory.Logs.Entries, candidate => candidate.Category == HealthCheckCategory));
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

    // GET is the one method /health maps, whether or not the client is served. Every other method, HEAD included, is
    // the 405 ProblemDetails whose Allow lists GET alone, and the check never runs: the client fallback, which answers
    // GET and HEAD, excludes the health path by its route, so it adds no method there. GET keeps the health body, never
    // the client's index.html.
    [Theory]
    [MemberData(nameof(HealthMethodCases))]
    public async Task HealthAllowsGetAloneWithOrWithoutTheClient(bool clientServed, string method)
    {
        string clientFolder = clientServed
            ? Directory.CreateTempSubdirectory("epiinfo-health-client-").FullName
            : Path.Combine(Path.GetTempPath(), "epiinfo-absent-client-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (clientServed)
            {
                await File.WriteAllTextAsync(
                    Path.Combine(clientFolder, ClientAppHosting.IndexFileName),
                    "<!doctype html><title>client</title>");
            }

            var store = new FakeDataStore();
            await using var factory = new ApiFactory(ApiFactoryOptions.ForFake(store) with
            {
                ExtraSettings = new Dictionary<string, string?> { [ApiOptions.ClientPathKey] = clientFolder },
            });
            using HttpClient client = factory.CreateClient();
            using var request = new HttpRequestMessage(new HttpMethod(method), HealthResponses.RequestPath);

            using HttpResponseMessage response = await client.SendAsync(request);

            if (method == HttpMethod.Get.Method)
            {
                await HealthResponses.AssertAsync(response, HttpStatusCode.OK, HealthResponses.Healthy);
                return;
            }

            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
            Assert.Equal(new[] { HttpMethod.Get.Method }, response.Content.Headers.Allow);
            Assert.Empty(store.Calls);

            // A HEAD response carries no body to read.
            if (method != HttpMethod.Head.Method)
            {
                Assert.Equal(ProblemMediaType, response.Content.Headers.ContentType?.MediaType);
                JsonElement problem = ApiTestClient.AssertProblemDetails(
                    await ApiTestClient.ReadJsonElementAsync(response),
                    (int)HttpStatusCode.MethodNotAllowed);
                Assert.Equal(ExpectedMethodNotAllowedDetail, Text(problem.GetProperty("detail")));
            }
        }
        finally
        {
            if (clientServed)
            {
                Directory.Delete(clientFolder, recursive: true);
            }
        }
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
        Assert.Equal(LogLevel.Error, entry.Level);
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
    // result nor any log line; the one Error line names only the exception type, the operation and the SQL error
    // number.
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
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal(PingFailedEventId, entry.EventId.Id);
        AssertStateKeys(entry, CheckNameKey, ExceptionTypeKey, OperationKey, SqlErrorNumberKey);
        Assert.Equal<object?>(SqlServerHealthCheck.Name, StateValue(entry, CheckNameKey));
        Assert.Equal<object?>(expectedExceptionType, StateValue(entry, ExceptionTypeKey));
        Assert.Equal<object?>(expectedOperation, StateValue(entry, OperationKey));
        Assert.Equal<object?>(expectedSqlErrorNumber, StateValue(entry, SqlErrorNumberKey));
    }

    // A log sink that throws for the check's line changes no result: a ping that fails and a ping answering false
    // (a null failure) are still the fixed Unhealthy result with no exception, the check never throws, and its one
    // line still reaches the working sink.
    [Theory]
    [InlineData(LoginFailure, PingFailedEventId)]
    [InlineData(SqlTextFailure, PingFailedEventId)]
    [InlineData(null, PingNotAnsweredEventId)]
    public async Task ThrowingLogSinkLeavesTheUnhealthyResultUnchanged(string? failure, int eventId)
    {
        var sentinel = "Sentinel-Health-" + Guid.NewGuid().ToString("N");
        using var throwing = new ThrowingLoggerProvider(typeof(SqlServerHealthCheck));
        using var harness = new CheckHarness(throwing);
        if (failure is null)
        {
            harness.Store.SetPingResult(false);
        }
        else
        {
            harness.Store.ThrowOn(PingOperation, CreateFailure(failure, sentinel));
        }

        var result = await harness.CheckAsync(HealthStatus.Unhealthy);

        AssertUnhealthy(result);
        Assert.Equal(1, throwing.Throws);
        Assert.Equal(eventId, Assert.Single(harness.Logs.Entries).EventId.Id);
        AssertNoExceptionLogged(harness.Logs);
        Assert.False(harness.Logs.ContainsText(sentinel));
    }

    // A caller that has cancelled before the run gets its cancellation, carrying its token, and no ping is started for
    // a result nobody reads: the store is not called and nothing is logged.
    [Fact]
    public async Task CallerCancelledBeforeTheRunPropagatesWithoutAPing()
    {
        using var harness = new CheckHarness();
        using var caller = new CancellationTokenSource();
        await caller.CancelAsync();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.CheckAsync(HealthStatus.Degraded, caller.Token));

        Assert.Equal(caller.Token, exception.CancellationToken);
        Assert.Empty(harness.Store.Calls);
        Assert.Empty(harness.Logs.Entries);
    }

    // A ping that ignores cancellation, as SqlClient's command on a session whose server has stopped answering does, is
    // ended for the run by the check's own bound on its wait: Unhealthy with no exception or data, after about the
    // bound given to the check and long before the public five seconds, with one Error line naming only the check. The
    // ping stays pending, so a later run joins it instead of pinging again; the ping is overdue by then, so that run
    // answers Unhealthy at once, with the same line. A Degraded registration proves the check reports Unhealthy itself.
    [Fact]
    public async Task PingIgnoringCancellationIsUnhealthyAtTheBoundAndALaterRunJoinsIt()
    {
        using var harness = new CheckHarness(pingTimeout: ShortPingTimeout);
        harness.Store.HoldPings();
        try
        {
            var clock = Stopwatch.StartNew();
            Task<HealthCheckResult> first = harness.CheckAsync(HealthStatus.Degraded);
            await AssertEndsAsync(first);
            TimeSpan elapsed = clock.Elapsed;

            AssertUnhealthy(await first);
            Assert.True(
                elapsed >= ShortPingTimeout - TimerTolerance && elapsed < SqlServerHealthCheck.PingTimeout,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The first run over the held ping ended after {elapsed.TotalMilliseconds:F0} ms; the check's " +
                    $"bound was {ShortPingTimeout.TotalMilliseconds} ms."));
            await AssertEndsAsync(harness.Store.WaitForPingCallsAsync(1));
            Assert.Equal(PingOperation, Assert.Single(harness.Store.Calls).Method);
            await WaitPastTheBoundAsync(clock, ShortPingTimeout);

            Task<HealthCheckResult> later = harness.CheckAsync(HealthStatus.Degraded);

            Assert.True(later.IsCompleted, "The later run waited on the overdue ping instead of answering at once.");
            AssertUnhealthy(await later);
            Assert.Equal(PingOperation, Assert.Single(harness.Store.Calls).Method);
            AssertNoExceptionLogged(harness.Logs);
            Assert.Equal(2, harness.Logs.Entries.Count);
            Assert.All(harness.Logs.Entries, AssertPingTimedOut);
        }
        finally
        {
            harness.Store.ReleasePings(answered: true);
        }
    }

    // A store that blocks the thread calling it before it returns its task, as SqlClient's asynchronous open does
    // while it replaces a dead pooled connection synchronously, holds no run's bound: the check calls it off the run's
    // thread, so the run that starts the ping still answers Unhealthy at about its bound, and a run arriving while the
    // store is blocked joins that ping rather than calling the store again. The block outlasts every bound, so only
    // the check's own bound can end either run in time.
    [Fact]
    public async Task StoreBlockingItsCallerHoldsNoRunsBoundAndAConcurrentRunJoinsThePing()
    {
        using var harness = new CheckHarness(pingTimeout: ShortPingTimeout);
        harness.Store.BlockPings(HangGuard);
        try
        {
            var clock = Stopwatch.StartNew();
            Task<HealthCheckResult> first = harness.CheckAsync(HealthStatus.Degraded);
            await AssertEndsAsync(harness.Store.WaitForPingCallsAsync(1));
            Task<HealthCheckResult> second = harness.CheckAsync(HealthStatus.Degraded);
            await AssertEndsAsync(first);
            TimeSpan elapsed = clock.Elapsed;
            await AssertEndsAsync(second);

            AssertUnhealthy(await first);
            AssertUnhealthy(await second);
            Assert.True(
                elapsed >= ShortPingTimeout - TimerTolerance && elapsed < SqlServerHealthCheck.PingTimeout,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The run that started the blocked ping ended after {elapsed.TotalMilliseconds:F0} ms; the " +
                    $"check's bound was {ShortPingTimeout.TotalMilliseconds} ms and the store blocked for up to " +
                    $"{HangGuard.TotalSeconds} s."));
            Assert.Equal(PingOperation, Assert.Single(harness.Store.Calls).Method);
            AssertNoExceptionLogged(harness.Logs);
            Assert.Equal(2, harness.Logs.Entries.Count);
            Assert.All(harness.Logs.Entries, AssertPingTimedOut);
        }
        finally
        {
            // No thread stays blocked once the case ends, whatever it found.
            harness.Store.UnblockPings();
        }
    }

    // A run that joins a ping part-way through its bound waits only what is left of that bound: it answers at the
    // ping's own deadline, with the run that started it, rather than a full bound after it joined.
    [Fact]
    public async Task RunJoiningAPendingPingWaitsOnlyWhatIsLeftOfItsBound()
    {
        using var harness = new CheckHarness(pingTimeout: JoinedPingTimeout);
        harness.Store.HoldPings();
        try
        {
            var clock = Stopwatch.StartNew();
            Task<HealthCheckResult> first = harness.CheckAsync(HealthStatus.Degraded);
            await Task.Delay(JoinedPingTimeout / 2);
            var joined = Stopwatch.StartNew();
            Task<HealthCheckResult> second = harness.CheckAsync(HealthStatus.Degraded);
            await AssertEndsAsync(second);
            TimeSpan waited = joined.Elapsed;
            TimeSpan endedAt = clock.Elapsed;
            await AssertEndsAsync(first);

            AssertUnhealthy(await first);
            AssertUnhealthy(await second);
            Assert.True(
                endedAt >= JoinedPingTimeout - TimerTolerance,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The joining run ended {endedAt.TotalMilliseconds:F0} ms after the ping started, before the " +
                    $"ping's {JoinedPingTimeout.TotalMilliseconds} ms bound."));
            Assert.True(
                waited < JoinedPingTimeout - TimerTolerance,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The joining run waited {waited.TotalMilliseconds:F0} ms, a full bound of " +
                    $"{JoinedPingTimeout.TotalMilliseconds} ms after it joined."));
            await AssertEndsAsync(harness.Store.WaitForPingCallsAsync(1));
            Assert.Equal(PingOperation, Assert.Single(harness.Store.Calls).Method);
            AssertNoExceptionLogged(harness.Logs);
            Assert.Equal(2, harness.Logs.Entries.Count);
            Assert.All(harness.Logs.Entries, AssertPingTimedOut);
        }
        finally
        {
            harness.Store.ReleasePings(answered: true);
        }
    }

    // A run that finds the shared ping pending for longer than the bound answers Unhealthy at once, without waiting,
    // with the timed-out line and no ping of its own, even when no run waited that ping out: here the run that started
    // it was cancelled by its caller and logged nothing. The overdue ping still ends on its own, after which the next
    // run pings afresh and reports the store's new answer.
    [Fact]
    public async Task RunJoiningAnOverduePingIsUnhealthyAtOnceAndTheRunAfterItEndsPingsAgain()
    {
        using var harness = new CheckHarness(pingTimeout: ShortPingTimeout);
        harness.Store.HoldPings();
        using var caller = new CancellationTokenSource();

        var clock = Stopwatch.StartNew();
        Task<HealthCheckResult> starter = harness.CheckAsync(HealthStatus.Degraded, caller.Token);
        await caller.CancelAsync();
        await AssertEndsAsync(starter);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starter);
        await AssertEndsAsync(harness.Store.WaitForPingCallsAsync(1));
        Assert.Empty(harness.Logs.Entries);
        await WaitPastTheBoundAsync(clock, ShortPingTimeout);

        Task<HealthCheckResult> overdue = harness.CheckAsync(HealthStatus.Degraded);

        Assert.True(overdue.IsCompleted, "The run over the overdue ping waited instead of answering at once.");
        AssertUnhealthy(await overdue);
        Assert.Equal(PingOperation, Assert.Single(harness.Store.Calls).Method);
        AssertNoExceptionLogged(harness.Logs);
        AssertPingTimedOut(Assert.Single(harness.Logs.Entries));

        // The release settles the ping before it returns (FakeDataStore.ReleasePings), so the next run starts a new
        // one.
        harness.Store.ReleasePings(answered: true);
        harness.Logs.Clear();
        var next = await harness.CheckAsync(HealthStatus.Degraded);

        Assert.Equal(HealthStatus.Healthy, next.Status);
        Assert.Equal(SqlServerHealthCheck.HealthyDescription, next.Description);
        Assert.Equal(2, harness.Store.Calls.Count);
        Assert.All(harness.Store.Calls, call => Assert.Equal(new FakeStoreCall(PingOperation, IsWrite: false), call));
        Assert.Empty(harness.Logs.Entries);
    }


    // Runs that overlap share the ping in flight: the store sees one call, and its outcome, once released, reaches
    // every waiting run, which reports it with its own line and no exception. The ping has then ended, so the next run
    // pings again rather than reading that outcome, and gets the store's new answer: a failure is never cached.
    [Theory]
    [InlineData(ReleasedAnswering, null)]
    [InlineData(ReleasedNotAnswering, PingNotAnsweredEventId)]
    [InlineData(LoginFailure, PingFailedEventId)]
    public async Task OverlappingRunsShareOnePingAndTheRunAfterItEndsPingsAgain(string release, int? expectedEventId)
    {
        var sentinel = "Sentinel-Health-" + Guid.NewGuid().ToString("N");
        using var harness = new CheckHarness(pingTimeout: UnreachedPingTimeout);
        harness.Store.HoldPings();

        Task<HealthCheckResult> first = harness.CheckAsync(HealthStatus.Degraded);
        Task<HealthCheckResult> second = harness.CheckAsync(HealthStatus.Degraded);
        Assert.False(first.IsCompleted || second.IsCompleted, "A run ended while the shared ping was held.");

        // The check calls the store on the thread pool, so the call is awaited before it is counted or released.
        await AssertEndsAsync(harness.Store.WaitForPingCallsAsync(1));
        Assert.Equal(PingOperation, Assert.Single(harness.Store.Calls).Method);

        switch (release)
        {
            case ReleasedAnswering:
                harness.Store.ReleasePings(answered: true);
                break;
            case ReleasedNotAnswering:
                harness.Store.ReleasePings(answered: false);
                break;
            default:
                harness.Store.ReleasePings(CreateFailure(release, sentinel));
                break;
        }

        Task<HealthCheckResult[]> both = Task.WhenAll(first, second);
        await AssertEndsAsync(both);
        foreach (HealthCheckResult result in await both)
        {
            if (expectedEventId is null)
            {
                Assert.Equal(HealthStatus.Healthy, result.Status);
                Assert.Equal(SqlServerHealthCheck.HealthyDescription, result.Description);
            }
            else
            {
                AssertUnhealthy(result);
            }
        }

        Assert.Equal(PingOperation, Assert.Single(harness.Store.Calls).Method);
        Assert.Equal(expectedEventId is null ? 0 : 2, harness.Logs.Entries.Count);
        Assert.All(harness.Logs.Entries, entry => Assert.Equal<int?>(expectedEventId, entry.EventId.Id));
        if (expectedEventId == PingFailedEventId)
        {
            Assert.All(harness.Logs.Entries, entry =>
            {
                AssertStateKeys(entry, CheckNameKey, ExceptionTypeKey, OperationKey, SqlErrorNumberKey);
                Assert.Equal<object?>(nameof(DataStoreUnavailableException), StateValue(entry, ExceptionTypeKey));
                Assert.Equal<object?>(PingOperation, StateValue(entry, OperationKey));
                Assert.Equal<object?>(LoginFailedNumber, StateValue(entry, SqlErrorNumberKey));
            });
        }

        AssertNoExceptionLogged(harness.Logs);
        Assert.False(harness.Logs.ContainsText(sentinel), "A captured log entry holds the store failure's sentinel.");

        harness.Logs.Clear();
        var next = await harness.CheckAsync(HealthStatus.Degraded);

        Assert.Equal(HealthStatus.Healthy, next.Status);
        Assert.Equal(2, harness.Store.Calls.Count);
        Assert.All(harness.Store.Calls, call => Assert.Equal(new FakeStoreCall(PingOperation, IsWrite: false), call));
        Assert.Empty(harness.Logs.Entries);
    }

    // The caller's cancellation of a run waiting on a shared ping ends that run alone, before the check's bound: it
    // propagates carrying the caller's token, and is neither reported as Unhealthy nor logged. The ping, which never
    // sees that token, goes on, and the run sharing it still gets its answer from that one ping.
    [Fact]
    public async Task CallerCancellationEndsOnlyItsOwnWaitOnASharedPing()
    {
        using var harness = new CheckHarness(pingTimeout: UnreachedPingTimeout);
        harness.Store.HoldPings();
        using var caller = new CancellationTokenSource();

        Task<HealthCheckResult> cancelled = harness.CheckAsync(HealthStatus.Degraded, caller.Token);
        Task<HealthCheckResult> other = harness.CheckAsync(HealthStatus.Degraded);
        Assert.False(cancelled.IsCompleted, "The run ended before its caller cancelled.");
        await caller.CancelAsync();
        await AssertEndsAsync(cancelled);
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

        Assert.Equal(caller.Token, exception.CancellationToken);
        Assert.False(other.IsCompleted, "The other run sharing the ping ended when one caller cancelled.");

        harness.Store.ReleasePings(answered: true);
        await AssertEndsAsync(other);

        Assert.Equal(HealthStatus.Healthy, (await other).Status);
        Assert.Equal(PingOperation, Assert.Single(harness.Store.Calls).Method);
        Assert.Empty(harness.Logs.Entries);
    }

    // A ping every run has given up on and that then fails is settled without a trace: no line is logged for it, its
    // exception, whose message holds a sentinel, reaches no log, and the exception is observed, so collecting the
    // failed task raises no unobserved task exception. The next run pings again.
    [Fact]
    public async Task AbandonedPingThatFailsLogsNothingAndItsFaultIsObserved()
    {
        var sentinel = "Sentinel-Health-" + Guid.NewGuid().ToString("N");
        var unobserved = new ConcurrentQueue<string>();
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs args)
        {
            // The event is process-wide, so only this test's failure counts; other tests' are ignored.
            if (args.Exception.Flatten().InnerExceptions.Any(
                inner => inner.Message.Contains(sentinel, StringComparison.Ordinal)))
            {
                unobserved.Enqueue(args.Exception.GetType().Name);
            }
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            using var harness = new CheckHarness(pingTimeout: ShortPingTimeout);
            harness.Store.HoldPings();

            AssertUnhealthy(await harness.CheckAsync(HealthStatus.Degraded));
            AssertPingTimedOut(Assert.Single(harness.Logs.Entries));

            // The check calls the store on the thread pool, so the call, and with it the hold, is awaited first.
            await AssertEndsAsync(harness.Store.WaitForPingCallsAsync(1));

            // No run waits for the ping any more. The release settles it before returning (FakeDataStore.ReleasePings),
            // and the store keeps no reference to the failed task, so only the check could have observed its fault.
            FailHeldPings(harness.Store, sentinel);
            CollectGarbage();

            Assert.Empty(unobserved);
            Assert.Single(harness.Logs.Entries);

            var next = await harness.CheckAsync(HealthStatus.Degraded);

            Assert.Equal(HealthStatus.Healthy, next.Status);
            Assert.Equal(2, harness.Store.Calls.Count);
            Assert.Single(harness.Logs.Entries);
            AssertNoExceptionLogged(harness.Logs);
            Assert.False(
                harness.Logs.ContainsText(sentinel),
                "A captured log entry holds the abandoned ping's sentinel.");
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    // The check is the host's singleton, so its ping in flight outlives a request: with the store's ping held, ignoring
    // cancellation, each GET /health answers 503 within about the five-second bound, and the second request joins the
    // first one's ping instead of adding one, answering at once because that ping is overdue by then. Once the ping is
    // released, the next request pings again and answers 200, without a restart.
    [Fact]
    public async Task HeldPingAnswersEachRequestUnhealthyWithinTheBoundFromOnePing()
    {
        var store = new FakeDataStore();
        store.HoldPings();
        await using var factory = new ApiFactory(ApiFactoryOptions.ForFake(store));
        using HttpClient client = factory.CreateClient();

        foreach (string request in new[] { "first", "second" })
        {
            var clock = Stopwatch.StartNew();
            using HttpResponseMessage response = await client.GetAsync(HealthResponses.RequestPath);
            TimeSpan elapsed = clock.Elapsed;

            await HealthResponses.AssertAsync(response, HttpStatusCode.ServiceUnavailable, HealthResponses.Unhealthy);
            Assert.True(
                elapsed < SqlServerHealthCheck.PingTimeout + EndpointMargin,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The {request} /health over the held ping took {elapsed.TotalSeconds:F1} s; the check's bound " +
                    $"is {SqlServerHealthCheck.PingTimeout.TotalSeconds} s."));
        }

        Assert.Equal(new FakeStoreCall(PingOperation, IsWrite: false), Assert.Single(store.Calls));
        AssertNoExceptionLogged(factory.Logs);
        IReadOnlyList<CapturedLogEntry> lines = factory.Logs.Find(entry => entry.Category == HealthCheckCategory);
        Assert.Equal(2, lines.Count);
        Assert.All(lines, AssertPingTimedOut);

        store.ReleasePings(answered: true);
        using HttpResponseMessage recovered = await client.GetAsync(HealthResponses.RequestPath);

        await HealthResponses.AssertAsync(recovered, HttpStatusCode.OK, HealthResponses.Healthy);
        Assert.Equal(2, store.Calls.Count);
    }

    // Kept out of the async test method, so no local of it can keep the failed task reachable.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void FailHeldPings(FakeDataStore store, string sentinel) =>
        store.ReleasePings(FakeDataStore.SqlTextException(sentinel));

    // Collects every unreachable object and runs its finalizer, which is where a faulted task whose exception was
    // never observed raises TaskScheduler.UnobservedTaskException.
    private static void CollectGarbage()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        GC.WaitForPendingFinalizers();
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
    // unreachable server and for a missing connection string alike. It is the only health-failure line: the
    // health-check service writes none of its own for the same result.
    private static CapturedLogEntry AssertPingFailureLogged(CapturingLoggerProvider logs)
    {
        Assert.DoesNotContain(logs.Entries, candidate => candidate.Category == HealthCheckServiceCategory);
        CapturedLogEntry entry = Assert.Single(logs.Entries, candidate => candidate.Category == HealthCheckCategory);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal(PingFailedEventId, entry.EventId.Id);
        AssertStateKeys(entry, CheckNameKey, ExceptionTypeKey, OperationKey, SqlErrorNumberKey);
        Assert.Equal<object?>(SqlServerHealthCheck.Name, StateValue(entry, CheckNameKey));
        Assert.Equal<object?>(nameof(DataStoreUnavailableException), StateValue(entry, ExceptionTypeKey));
        Assert.Equal<object?>(PingOperation, StateValue(entry, OperationKey));
        return entry;
    }

    // Fails, rather than hangs, when the check has not ended within HangGuard.
    private static async Task AssertEndsAsync(Task run) =>
        Assert.True(
            ReferenceEquals(run, await Task.WhenAny(run, Task.Delay(HangGuard))),
            $"The check had not ended after {HangGuard.TotalSeconds} s.");

    // Returns once more than bound, and the timer's tolerance, has passed on a clock started before the ping, so the
    // ping is overdue by the check's own clock whatever the delay timer's resolution.
    private static async Task WaitPastTheBoundAsync(Stopwatch sincePingStart, TimeSpan bound)
    {
        TimeSpan target = bound + TimerTolerance;
        while (true)
        {
            TimeSpan left = target - sincePingStart.Elapsed;
            if (left <= TimeSpan.Zero)
            {
                return;
            }

            await Task.Delay(left);
        }
    }

    // The check's one line for a ping its bound ended: Error, the timeout event, naming the check alone.
    private static void AssertPingTimedOut(CapturedLogEntry entry)
    {
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal(PingTimedOutEventId, entry.EventId.Id);
        AssertStateKeys(entry, CheckNameKey);
        Assert.Equal<object?>(SqlServerHealthCheck.Name, StateValue(entry, CheckNameKey));
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

    /// <summary>
    /// The check over a fresh store, logging only to a capturing provider that accepts every level, with the public
    /// constructor's bound or, when <c>pingTimeout</c> is given, that bound.
    /// </summary>
    private sealed class CheckHarness : IDisposable
    {
        private readonly ILoggerFactory _loggerFactory;

        public CheckHarness(ILoggerProvider? extraLogs = null, TimeSpan? pingTimeout = null)
        {
            Store = new FakeDataStore();
            Logs = new CapturingLoggerProvider();
            _loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.SetMinimumLevel(LogLevel.Trace).AddProvider(Logs);
                if (extraLogs is not null)
                {
                    builder.AddProvider(extraLogs);
                }
            });
            ILogger<SqlServerHealthCheck> logger = _loggerFactory.CreateLogger<SqlServerHealthCheck>();
            Check = pingTimeout is TimeSpan bound
                ? new SqlServerHealthCheck(Store, logger, bound)
                : new SqlServerHealthCheck(Store, logger);
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
/// <c>{"status":"healthy"}</c> from one <c>SELECT 1</c> through the SQL Server store; and, through a relay to that
/// server, a pooled session that stops receiving the server's bytes makes <c>/health</c> answer 503 within the check's
/// bound, and then 200 again without a restart.
/// </summary>
/// <remarks>
/// The cases write nothing and depend on no count, so they take no <see cref="DatabaseWriteLock"/>; their only
/// statement is <c>SELECT 1</c>. They never skip: an unreachable or unconfigured database fails them through
/// <see cref="TestDatabase"/>, naming <c>SQL_HOST</c> and <c>MSSQL_SA_PASSWORD</c>. The relayed case's pool is keyed
/// by the relay's port and a unique application name, so it is shared with no other test.
/// </remarks>
[Collection(DatabaseCollections.Database)]
[Trait("Category", "Integration")]
public sealed class HealthIntegrationTests
{
    private const int DefaultSqlServerPort = 1433;
    private const string TcpPrefix = "tcp:";
    private const string ApplicationNamePrefix = "EpiInfo.Web.Tests health stall ";
    private const int PingTimedOutEventId = 3;

    // Short, so a new session through the relay opens quickly; long enough for a login relayed over loopback.
    private const int ConnectTimeoutSeconds = 5;

    // The store's command timeout for the relayed case: well past the check's five-second bound and the margin, so a
    // 503 within them comes from the bound, and short enough that a ping on a session that never resumes ends soon.
    private const int StalledCommandTimeoutSeconds = 12;

    // How long SqlClient waits for the server to acknowledge the attention it sends when a command times out, before
    // it fails the command and discards the session.
    private const int AttentionWaitSeconds = 5;

    // What a run of the endpoint may take beyond the check's bound, for the hosting and the relay.
    private static readonly TimeSpan EndpointMargin = TimeSpan.FromSeconds(5);

    // How long /health may answer 503 after the stall before it must answer 200 again: the pending ping's command
    // timeout and SqlClient's attention wait, one more bounded run that joined it, and a margin for the new session.
    private static readonly TimeSpan RecoveryBound = TimeSpan.FromSeconds(
        StalledCommandTimeoutSeconds + AttentionWaitSeconds + SqlServerHealthCheck.PingTimeout.TotalSeconds + 10);

    // The pause after each 503 of the recovery poll: a run over the overdue stalled ping answers at once, so without it
    // the poll would spin, and short beside the recovery it waits for.
    private static readonly TimeSpan RecoveryPollInterval = TimeSpan.FromMilliseconds(250);

    private static readonly string HealthCheckCategory = typeof(SqlServerHealthCheck).FullName!;

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

    // The session /health pooled from two healthy runs stops receiving the server's bytes, as when a network path
    // drops them, while new connections still get through. SqlClient does not end a cancelled command on such a
    // session, so only the check's bound on its wait can answer: /health answers 503 within about five seconds, long
    // before the store's command timeout, with no exception logged. It then answers 200 again without a restart,
    // whether the session resumes delivering, which ends the pending ping, or never does: the uncancelled ping then
    // ends under the command timeout, SqlClient discards the session, and the next ping opens a new one.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StalledPooledSessionIsUnhealthyWithinTheBoundAndRecoversWithoutARestart(bool sessionResumes)
    {
        await TestDatabase.EnsureReachableAsync();
        SqlConnectionStringBuilder target = TestDatabase.Builder();
        string password = target.Password;
        (string host, int port) = UpstreamEndpoint(target.DataSource);
        await using StallableRelay relay = StallableRelay.Start(host, port);
        await using var factory = new ApiFactory(new ApiFactoryOptions
        {
            ConnectionMode = ApiConnectionMode.Supplied,
            ConnectionString = RelayedConnectionString(target, host, relay.Port),
            CommandTimeoutSeconds = StalledCommandTimeoutSeconds,
        });
        using HttpClient client = factory.CreateClient();

        for (int run = 0; run < 2; run++)
        {
            using HttpResponseMessage healthy = await client.GetAsync(HealthResponses.RequestPath);
            await HealthResponses.AssertAsync(healthy, HttpStatusCode.OK, HealthResponses.Healthy);
        }

        // Both runs used one physical connection: the pooled session the stall silences.
        Assert.Equal(1, relay.AcceptedConnections);

        relay.StallEstablished();
        var clock = Stopwatch.StartNew();
        using (HttpResponseMessage stalled = await client.GetAsync(HealthResponses.RequestPath))
        {
            TimeSpan elapsed = clock.Elapsed;
            await HealthResponses.AssertAsync(stalled, HttpStatusCode.ServiceUnavailable, HealthResponses.Unhealthy);
            Assert.True(
                elapsed < SqlServerHealthCheck.PingTimeout + EndpointMargin,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"/health took {elapsed.TotalSeconds:F1} s over the stalled session; the check's bound is " +
                    $"{SqlServerHealthCheck.PingTimeout.TotalSeconds} s and the command timeout " +
                    $"{StalledCommandTimeoutSeconds} s."));
        }

        CapturedLogEntry timedOut = Assert.Single(factory.Logs.Entries, entry => entry.Category == HealthCheckCategory);
        Assert.Equal(LogLevel.Error, timedOut.Level);
        Assert.Equal(PingTimedOutEventId, timedOut.EventId.Id);

        if (sessionResumes)
        {
            relay.Resume();
        }

        // Every 503 on the way comes within one bound, and at once while the stalled ping is overdue, so the poll paces
        // itself and notices the recovery soon after it happens.
        var recovery = Stopwatch.StartNew();
        HttpStatusCode status;
        do
        {
            using HttpResponseMessage response = await client.GetAsync(HealthResponses.RequestPath);
            status = response.StatusCode;
            if (status == HttpStatusCode.OK)
            {
                await HealthResponses.AssertAsync(response, HttpStatusCode.OK, HealthResponses.Healthy);
            }
            else
            {
                await HealthResponses.AssertAsync(
                    response,
                    HttpStatusCode.ServiceUnavailable,
                    HealthResponses.Unhealthy);
                await Task.Delay(RecoveryPollInterval);
            }
        }
        while (status != HttpStatusCode.OK && recovery.Elapsed < RecoveryBound);

        Assert.True(
            status == HttpStatusCode.OK,
            string.Create(
                CultureInfo.InvariantCulture,
                $"/health still answered {(int)status} {recovery.Elapsed.TotalSeconds:F1} s after the stall " +
                $"(session resumed: {sessionResumes})."));

        // A resumed session serves the recovery itself; a silent one was discarded, and the recovery opened a new one.
        Assert.Equal(sessionResumes ? 1 : 2, relay.AcceptedConnections);
        Assert.All(factory.Logs.Entries, entry =>
        {
            Assert.Null(entry.ExceptionType);
            Assert.Null(entry.ExceptionMessage);
        });
        Assert.False(
            password.Length > 0 && factory.Logs.ContainsText(password),
            "A captured log entry holds the database password.");
    }

    // The test server's connection string, relayed through the relay on relayPort with a short connect timeout and a
    // pool of its own.
    private static string RelayedConnectionString(SqlConnectionStringBuilder target, string host, int relayPort)
    {
        // The certificate is still checked against the server's own name, not the relay's address.
        if (!target.ShouldSerialize("Host Name In Certificate"))
        {
            target.HostNameInCertificate = host;
        }

        target.DataSource = string.Create(CultureInfo.InvariantCulture, $"{TcpPrefix}127.0.0.1,{relayPort}");
        target.ConnectTimeout = ConnectTimeoutSeconds;
        target.ApplicationName = ApplicationNamePrefix + Guid.NewGuid().ToString("N");

        // The stall must silence a pooled session, so the pool is on whatever the environment's string says.
        target.Pooling = true;
        return target.ConnectionString;
    }

    // The TCP endpoint of the test server's data source: "host" (port 1433) or "host,port", optionally prefixed "tcp:",
    // where an "\instance" is ignored when a port is given, as SqlClient ignores it. An instance without a port is
    // found through the SQL Server Browser, and a named pipe or LocalDB has no TCP endpoint, so the relay cannot stand
    // in for either; the test then fails naming the data source, which holds no secret.
    private static (string Host, int Port) UpstreamEndpoint(string dataSource)
    {
        string source = dataSource.Trim();
        if (source.StartsWith(TcpPrefix, StringComparison.OrdinalIgnoreCase))
        {
            source = source[TcpPrefix.Length..];
        }

        string[] parts = source.Split(',');
        string server = parts[0].Trim();
        int instanceStart = server.IndexOf('\\', StringComparison.Ordinal);
        string host = instanceStart < 0 ? server : server[..instanceStart];
        if (host is "." || string.Equals(host, "(local)", StringComparison.OrdinalIgnoreCase))
        {
            host = "localhost";
        }

        int port = DefaultSqlServerPort;
        bool usable = parts.Length <= 2
            && host.Length > 0
            && !host.Contains(':', StringComparison.Ordinal)
            && !host.StartsWith('(')
            && (parts.Length == 2
                ? int.TryParse(parts[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out port)
                    && port is > 0 and <= IPEndPoint.MaxPort
                : instanceStart < 0);
        if (!usable)
        {
            Assert.Fail(
                $"{nameof(HealthIntegrationTests)} relays to the test server over TCP, so its data source must be " +
                $"host or host,port, optionally prefixed {TcpPrefix}; '{dataSource}' is not. Set " +
                $"{TestDatabase.SqlHostVariable} or {TestDatabase.ConnectionStringVariable} to such a data source.");
        }

        return (host, port);
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

/// <summary>
/// A loopback TCP listener that accepts every connection and never sends a byte, as a server that cannot be used
/// although the connection is made. Each connection is held open, unread, until the listener is disposed.
/// </summary>
file sealed class StalledServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<Socket> _held = [];
    private Task _accepting = Task.CompletedTask;

    /// <summary>The port it listens on, on 127.0.0.1.</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Starts listening on a free loopback port.</summary>
    /// <returns>The running listener; the caller disposes it.</returns>
    public static StalledServer Start()
    {
        var server = new StalledServer();
        server._listener.Start();
        server._accepting = server.AcceptAsync(server._stopping.Token);
        return server;
    }

    /// <summary>Stops listening and closes every connection it held.</summary>
    /// <returns>A task that completes when the listener has stopped.</returns>
    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        _listener.Stop();
        await _accepting;

        // The accept loop has ended, so no connection is added while these are closed.
        foreach (Socket held in _held)
        {
            held.Dispose();
        }

        _listener.Dispose();
        _stopping.Dispose();
    }

    private async Task AcceptAsync(CancellationToken stop)
    {
        while (true)
        {
            try
            {
                _held.Add(await _listener.AcceptSocketAsync(stop));
            }
            catch (Exception failure)
                when (stop.IsCancellationRequested
                    && failure is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }
        }
    }
}

/// <summary>
/// A loopback TCP relay to the test server that can silence the connections it has already relayed: after
/// <see cref="StallEstablished"/>, each of them stops delivering the server's bytes to the client and holds them,
/// while the client's bytes still reach the server and a connection accepted later relays normally, as when a network
/// path drops one session's replies; <see cref="Resume"/> delivers the held bytes and lets those connections flow
/// again.
/// </summary>
file sealed class StallableRelay : IAsyncDisposable
{
    private const int BufferSize = 16 * 1024;

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _linksLock = new();
    private readonly List<Link> _links = [];
    private readonly List<Task> _relays = [];
    private readonly string _upstreamHost;
    private readonly int _upstreamPort;
    private Task _accepting = Task.CompletedTask;
    private int _accepted;

    private StallableRelay(string upstreamHost, int upstreamPort)
    {
        _upstreamHost = upstreamHost;
        _upstreamPort = upstreamPort;
    }

    /// <summary>The port it listens on, on 127.0.0.1.</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>How many connections it has accepted so far.</summary>
    public int AcceptedConnections => Volatile.Read(ref _accepted);

    /// <summary>Starts listening on a free loopback port, relaying every connection it accepts to the server.</summary>
    /// <param name="upstreamHost">The test server's host.</param>
    /// <param name="upstreamPort">The test server's TCP port.</param>
    /// <returns>The running relay; the caller disposes it.</returns>
    public static StallableRelay Start(string upstreamHost, int upstreamPort)
    {
        var relay = new StallableRelay(upstreamHost, upstreamPort);
        relay._listener.Start();
        relay._accepting = relay.AcceptAsync(relay._stopping.Token);
        return relay;
    }

    /// <summary>Stops delivering the server's bytes on every connection accepted so far, holding them.</summary>
    public void StallEstablished()
    {
        lock (_linksLock)
        {
            foreach (Link link in _links)
            {
                link.Stall();
            }
        }
    }

    /// <summary>Delivers the held bytes and lets every stalled connection flow again.</summary>
    public void Resume()
    {
        lock (_linksLock)
        {
            foreach (Link link in _links)
            {
                link.Resume();
            }
        }
    }

    /// <summary>
    /// Stops listening, ends every relay, a stalled one included, and returns once each has closed both of its sockets.
    /// </summary>
    /// <returns>A task that completes when the relay has stopped.</returns>
    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        _listener.Stop();
        await _accepting;

        // The accept loop has ended, so no relay is added while these are awaited.
        Task[] relays;
        lock (_linksLock)
        {
            relays = [.. _relays];
        }

        await Task.WhenAll(relays);
        _listener.Dispose();
        _stopping.Dispose();
    }

    private async Task AcceptAsync(CancellationToken stop)
    {
        while (true)
        {
            Socket client;
            try
            {
                client = await _listener.AcceptSocketAsync(stop);
            }
            catch (Exception failure) when (stop.IsCancellationRequested && IsTransportEnd(failure))
            {
                return;
            }

            Interlocked.Increment(ref _accepted);
            var link = new Link();
            lock (_linksLock)
            {
                _links.Add(link);
                _relays.Add(RelayAsync(client, link, stop));
            }
        }
    }

    // Relays one accepted connection until either side closes it or the relay stops. A transport error ends the relay
    // quietly: the test asserts what /health answers, not how the relay ended.
    private async Task RelayAsync(Socket client, Link link, CancellationToken stop)
    {
        using var upstream = new TcpClient();
        try
        {
            try
            {
                await upstream.ConnectAsync(_upstreamHost, _upstreamPort, stop);
            }
            catch (Exception failure) when (IsTransportEnd(failure))
            {
                return;
            }

            await using var clientStream = new NetworkStream(client, ownsSocket: false);
            NetworkStream serverStream = upstream.GetStream();
            Task toServer = PumpAsync(clientStream, serverStream, link: null, stop);
            Task toClient = PumpAsync(serverStream, clientStream, link, stop);
            await Task.WhenAny(toServer, toClient);

            // One side has closed: closing both sockets ends the other direction as well, unless it is stalled, which
            // the relay's stop ends.
            client.Close();
            upstream.Close();
            await Task.WhenAll(toServer, toClient);
        }
        finally
        {
            client.Dispose();
        }
    }

    // Copies source to destination; with a link, each chunk read waits while that link is stalled before it is written.
    private static async Task PumpAsync(Stream source, Stream destination, Link? link, CancellationToken stop)
    {
        var buffer = new byte[BufferSize];
        try
        {
            while (true)
            {
                int read = await source.ReadAsync(buffer, stop);
                if (read == 0)
                {
                    return;
                }

                if (link is not null)
                {
                    await link.WaitUntilFlowingAsync(stop);
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), stop);
            }
        }
        catch (Exception failure) when (IsTransportEnd(failure))
        {
            // The connection closed or the relay stopped; either way this direction is done.
        }
    }

    private static bool IsTransportEnd(Exception failure) =>
        failure is IOException or SocketException or OperationCanceledException or ObjectDisposedException;

    // Whether one relayed connection delivers the server's bytes: stalled while it holds an incomplete source.
    private sealed class Link
    {
        private readonly object _lock = new();
        private TaskCompletionSource? _stalled;

        public void Stall()
        {
            lock (_lock)
            {
                _stalled ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public void Resume()
        {
            TaskCompletionSource? stalled;
            lock (_lock)
            {
                stalled = _stalled;
                _stalled = null;
            }

            stalled?.TrySetResult();
        }

        public Task WaitUntilFlowingAsync(CancellationToken stop)
        {
            lock (_lock)
            {
                return _stalled is { } stalled ? stalled.Task.WaitAsync(stop) : Task.CompletedTask;
            }
        }
    }
}
