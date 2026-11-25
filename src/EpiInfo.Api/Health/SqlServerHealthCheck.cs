using EpiInfo.Domain.Storage;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace EpiInfo.Api.Health;

/// <summary>
/// The health check behind <c>GET /health</c>: Healthy when the data store answers a trivial query
/// (<c>SELECT 1</c>, through <see cref="IDataStore.PingAsync"/>), Unhealthy otherwise. <c>Program.cs</c> registers it
/// under <see cref="Name"/>, and the health endpoint turns its status into 200 <c>{"status":"healthy"}</c> or 503
/// <c>{"status":"unhealthy"}</c>; this class knows nothing about the response body.
/// </summary>
/// <remarks>
/// <para>
/// <b>Redaction.</b> Every result carries only the fixed <see cref="HealthyDescription"/> or
/// <see cref="UnhealthyDescription"/>: never an exception object, an exception message or a data dictionary. The
/// health-check service logs a result's description and exception under its own log category, which the
/// <c>Microsoft.AspNetCore</c> level filter does not cover, and a store exception's inner exception may hold a
/// connection string or SQL text. The check therefore catches every store failure itself, and its own log line names
/// only the exception type, the failed store operation and the SQL error number.
/// </para>
/// <para>
/// <b>Failures.</b> A ping that returns <see langword="false"/> and a ping that throws are both Unhealthy, whatever
/// the registration's failure status, so <c>/health</c> answers 503 for an unreachable server, a failed login, a
/// timeout and a missing connection string alike; the last makes the store throw
/// <see cref="DataStoreUnavailableException"/> before any connection is attempted, and the process keeps serving.
/// Only the caller's own cancellation propagates, as <see cref="OperationCanceledException"/>.
/// </para>
/// <para>
/// <b>State.</b> The check holds no state between runs, caches no result, and neither retries nor applies a timeout of
/// its own: the store applies the configured command timeout. It reaches the database only through
/// <see cref="IDataStore"/>, so the API never handles a SQL client type.
/// </para>
/// </remarks>
public sealed partial class SqlServerHealthCheck : IHealthCheck
{
    /// <summary>The name the check is registered under: <c>AddCheck&lt;SqlServerHealthCheck&gt;(Name)</c>.</summary>
    public const string Name = "sql";

    /// <summary>The description of every Healthy result.</summary>
    public const string HealthyDescription = "The data store answered.";

    /// <summary>The description of every Unhealthy result; fixed text that names no server, value or error.</summary>
    public const string UnhealthyDescription = "The data store did not answer.";

    private readonly IDataStore _dataStore;
    private readonly ILogger<SqlServerHealthCheck> _logger;

    /// <summary>Initialises the check over the registered data store.</summary>
    /// <param name="dataStore">The store whose <see cref="IDataStore.PingAsync"/> answers for the database.</param>
    /// <param name="logger">The logger for the check's own failure line.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="dataStore"/> or <paramref name="logger"/> is <see langword="null"/>.
    /// </exception>
    public SqlServerHealthCheck(IDataStore dataStore, ILogger<SqlServerHealthCheck> logger)
    {
        ArgumentNullException.ThrowIfNull(dataStore);
        ArgumentNullException.ThrowIfNull(logger);

        _dataStore = dataStore;
        _logger = logger;
    }

    /// <summary>Pings the data store once and reports whether it answered.</summary>
    /// <param name="context">The run's context; its registration's failure status is not used.</param>
    /// <param name="cancellationToken">The caller's abort token, passed to the store.</param>
    /// <returns>
    /// <see cref="HealthCheckResult.Healthy"/> with <see cref="HealthyDescription"/> when the store answered;
    /// otherwise <see cref="HealthCheckResult.Unhealthy"/> with <see cref="UnhealthyDescription"/>, no exception and
    /// no data.
    /// </returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        bool answered;
        try
        {
            answered = await _dataStore.PingAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // The exception is never passed to the logger or the result: its ToString includes the inner exception,
            // which may hold a connection string or SQL text.
            var (operation, sqlErrorNumber) = DescribeStoreFailure(exception);
            LogPingFailed(_logger, Name, exception.GetType().Name, operation, sqlErrorNumber);
            return HealthCheckResult.Unhealthy(UnhealthyDescription);
        }

        if (!answered)
        {
            LogPingNotAnswered(_logger, Name);
            return HealthCheckResult.Unhealthy(UnhealthyDescription);
        }

        return HealthCheckResult.Healthy(HealthyDescription);
    }

    // Only the members the store exceptions document as safe to log: a fixed operation name and the SQL error number.
    private static (string? Operation, int? SqlErrorNumber) DescribeStoreFailure(Exception exception) =>
        exception switch
        {
            DataStoreUnavailableException unavailable => (unavailable.Operation, unavailable.SqlErrorNumber),
            DataStoreWriteException failed => (failed.Operation, failed.SqlErrorNumber),
            _ => (null, null),
        };

    [LoggerMessage(
        EventId = 1,
        EventName = "HealthCheckPingFailed",
        Level = LogLevel.Warning,
        Message = "Health check {CheckName} failed with {ExceptionType} during {Operation} (SQL error {SqlErrorNumber}).")]
    private static partial void LogPingFailed(
        ILogger logger,
        string checkName,
        string exceptionType,
        string? operation,
        int? sqlErrorNumber);

    [LoggerMessage(
        EventId = 2,
        EventName = "HealthCheckPingNotAnswered",
        Level = LogLevel.Warning,
        Message = "Health check {CheckName} failed: the data store reported that the database did not answer.")]
    private static partial void LogPingNotAnswered(ILogger logger, string checkName);
}
