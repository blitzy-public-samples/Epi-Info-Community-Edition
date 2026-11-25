using EpiInfo.Api.Health;
using EpiInfo.Api.Tests.Infrastructure;
using EpiInfo.Domain.Storage;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace EpiInfo.Api.Tests.Hosting;

/// <summary>
/// The check-level contract of <see cref="SqlServerHealthCheck"/>, the check behind <c>GET /health</c> (AAP 0.3.4.7):
/// Healthy only when the store's ping answers <see langword="true"/>; Unhealthy with the fixed description, no
/// exception and no data for a <see langword="false"/> answer and for every store failure, whatever the registration's
/// failure status; and only the caller's own cancellation propagated. Its log lines carry no exception and name only
/// the exception type, the store operation and the SQL error number (AAP 0.9.3).
/// </summary>
/// <remarks>
/// The class needs no database: it runs the check directly over <see cref="FakeDataStore"/>, carries no
/// <c>Integration</c> trait and joins no database collection, so <c>--filter "Category!=Integration"</c> selects it.
/// These cases exercise the check itself over that store; the response body of <c>GET /health</c>, 200
/// <c>{"status":"healthy"}</c> or 503 <c>{"status":"unhealthy"}</c>, is the contract of
/// <see cref="EpiInfo.Api.Endpoints.HealthEndpoints"/>.
/// </remarks>
public sealed class HealthTests
{
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

    // The structured state holds exactly the template's arguments plus the template itself, so the line can carry
    // nothing the template does not name.
    private static void AssertStateKeys(CapturedLogEntry entry, params string[] arguments) =>
        Assert.Equal(
            arguments.Append("{OriginalFormat}").Order(StringComparer.Ordinal),
            entry.State.Select(pair => pair.Key).Order(StringComparer.Ordinal));

    private static object? StateValue(CapturedLogEntry entry, string key) =>
        Assert.Single(entry.State, pair => pair.Key == key).Value;

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
