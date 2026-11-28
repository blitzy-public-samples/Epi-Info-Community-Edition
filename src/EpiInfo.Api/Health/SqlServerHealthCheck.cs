using System.Diagnostics;
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
/// health-check service logs a result's description and exception, and the exception of a check that throws, under
/// its own log category, and a store exception's inner exception may hold a connection string or SQL text. The check
/// therefore catches every store failure itself, and its own log line names at most the exception type, the failed
/// store operation and the SQL error number.
/// </para>
/// <para>
/// <b>Logging.</b> The health-check service's own per-check line is suppressed by the API's logging floors
/// (<c>ProtectedLogLevels</c> in <c>Program.cs</c>), so this check's single Error line is the one event a failed
/// health check writes; a healthy run writes none.
/// </para>
/// <para>
/// <b>Failures.</b> A ping that returns <see langword="false"/>, a ping that throws and a ping that has not answered
/// within <see cref="PingTimeout"/> are all Unhealthy, whatever the registration's failure status, so <c>/health</c>
/// answers 503 for an unreachable server, a failed login, a timeout and a missing connection string alike; the last
/// makes the store throw <see cref="DataStoreUnavailableException"/> before any connection is attempted, and the
/// process keeps serving. Only the caller's own cancellation propagates, as <see cref="OperationCanceledException"/>
/// carrying the caller's token.
/// </para>
/// <para>
/// <b>One ping in flight.</b> The check keeps at most one store ping in flight. A run that finds none starts one; a
/// run that finds one, started by a concurrent or an earlier run, joins it instead of pinging again, so however many
/// runs a silent database leaves waiting, the store holds one pending ping, and the next ping starts only after that
/// one has ended. The ping belongs to the check, not to the run that started it: no caller's token reaches the store,
/// so one caller's abort cannot end a ping that other runs share, and the check never cancels it either. SqlClient
/// meets the cancellation of a command on a session whose server has stopped answering by waiting for an
/// acknowledgement that does not come, and its command timeout then no longer applies, so a cancelled ping on such a
/// session would stay pending until the server answered again, and every later run would join it. Uncancelled, every
/// ping ends on its own under the store's timeouts: the connection's connect timeout, which the connection factory's
/// open deadline enforces, and the configured command timeout, after which SqlClient discards the silent session, so
/// the next ping opens a new one. A ping's outcome reaches only the runs waiting for it: the check caches no result,
/// never retries, and starts a ping only for a run.
/// </para>
/// <para>
/// <b>Off the caller's thread.</b> The run that starts a ping hands the store call to the thread pool and begins its
/// wait at once, so no run's bound depends on how long the store works before it returns its task. A store may work
/// synchronously before its first await, and SqlClient does so for up to the connect timeout: when its asynchronous
/// open takes an idle pooled connection that it finds dead, it discards that connection and opens the replacement
/// synchronously on the calling thread. Called on the run's own thread, the store would hold the starting run, and its
/// bound, until it returned. A store that throws instead of returning a task, or returns none, fails the ping as a
/// faulted ping task does.
/// </para>
/// <para>
/// <b>Bound.</b> Each run answers within <see cref="PingTimeout"/>, five seconds, of the start of the ping it shares,
/// and at once when that ping is already overdue. The run that starts a ping waits for it the full bound; a run that
/// joins one waits only what is left of the bound, counted from that ping's start; and a run that finds the ping
/// pending for longer than the bound reports Unhealthy at once, with the same timed-out line, and starts no ping. At
/// the end of its wait a run reports Unhealthy itself, leaving the ping pending. <c>/health</c> therefore answers 503
/// within about five seconds while SQL Server does not answer, however long the ping takes, even when the driver does
/// not end it, and however many requests arrive while it is pending. Recovery needs no restart: an overdue ping still
/// ends on its own under the store's timeouts, after which the next run pings afresh. The bound is the check's own,
/// not the registration's timeout: the health-check service would report that timeout's cancellation as Unhealthy
/// with the exception attached, and log it. A ping that no run waits for any more is settled when it ends: its
/// outcome, a fault included, is read and discarded, so nothing is logged for it and no exception of it goes
/// unobserved.
/// </para>
/// <para>
/// <b>Lifetime.</b> <c>Program.cs</c> registers the check as a singleton, so the ping in flight is the process's.
/// That state changes only under the check's lock, so concurrent runs are safe. The check reaches the database only
/// through <see cref="IDataStore"/>, so the API never handles a SQL client type.
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

    /// <summary>
    /// How long the runs sharing a store ping wait for it, counted from its start, before each reports Unhealthy: five
    /// seconds, well inside SqlClient's default 15-second connect timeout and the 30-second default command timeout
    /// (class remarks).
    /// </summary>
    public static TimeSpan PingTimeout { get; } = TimeSpan.FromSeconds(5);

    // The message of every cancellation raised for the caller's token; fixed text that names no server or value.
    private const string CancelledMessage = "The health check was cancelled by its caller.";

    // The message of the failure of a ping whose store returned no task; fixed text that names no server or value.
    private const string NoPingTaskMessage = "The data store returned no ping task.";

    // The longest bound accepted, about 24.8 days, well within what Task.WaitAsync accepts.
    private static readonly TimeSpan MaxPingTimeout = TimeSpan.FromMilliseconds(int.MaxValue);

    private readonly IDataStore _dataStore;
    private readonly ILogger<SqlServerHealthCheck> _logger;
    private readonly TimeSpan _pingTimeout;

    // Guards _pingInFlight, the settlement of the ping in flight, which never faults or cancels, null when no ping is
    // in flight; and _pingStartedAt, that ping's start as a Stopwatch timestamp, which a change of the wall clock
    // cannot move.
    private readonly object _pingLock = new();
    private Task<PingOutcome>? _pingInFlight;
    private long _pingStartedAt;

    /// <summary>Initialises the check over the registered data store, bounded by <see cref="PingTimeout"/>.</summary>
    /// <remarks>
    /// The one public constructor, which the container uses for the singleton that <c>Program.cs</c> registers and
    /// <c>AddCheck&lt;SqlServerHealthCheck&gt;</c> resolves for every run.
    /// </remarks>
    /// <param name="dataStore">The store whose <see cref="IDataStore.PingAsync"/> answers for the database.</param>
    /// <param name="logger">The logger for the check's own failure line.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="dataStore"/> or <paramref name="logger"/> is <see langword="null"/>.
    /// </exception>
    public SqlServerHealthCheck(IDataStore dataStore, ILogger<SqlServerHealthCheck> logger)
        : this(dataStore, logger, PingTimeout)
    {
    }

    /// <summary>
    /// Initialises the check with a bound of its caller's choosing, for tests that cannot wait five seconds.
    /// </summary>
    /// <param name="dataStore">The store whose <see cref="IDataStore.PingAsync"/> answers for the database.</param>
    /// <param name="logger">The logger for the check's own failure line.</param>
    /// <param name="pingTimeout">
    /// How long the runs sharing a ping wait for it, counted from its start, before each reports Unhealthy; greater
    /// than zero and at most <see cref="int.MaxValue"/> milliseconds.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="dataStore"/> or <paramref name="logger"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pingTimeout"/> is out of range.</exception>
    internal SqlServerHealthCheck(IDataStore dataStore, ILogger<SqlServerHealthCheck> logger, TimeSpan pingTimeout)
    {
        ArgumentNullException.ThrowIfNull(dataStore);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(pingTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pingTimeout, MaxPingTimeout);

        _dataStore = dataStore;
        _logger = logger;
        _pingTimeout = pingTimeout;
    }

    /// <summary>
    /// Waits for the store's ping, starting one unless one is already in flight, and reports whether it answered
    /// within the check's bound.
    /// </summary>
    /// <param name="context">The run's context; its registration's failure status is not used.</param>
    /// <param name="cancellationToken">
    /// The caller's abort token. It ends only this run's wait: the ping, which other runs may share, never receives it.
    /// </param>
    /// <returns>
    /// <see cref="HealthCheckResult.Healthy"/> with <see cref="HealthyDescription"/> when the ping answered within the
    /// bound; otherwise, including a ping still pending when the bound passed,
    /// <see cref="HealthCheckResult.Unhealthy"/> with <see cref="UnhealthyDescription"/>, no exception and no data.
    /// </returns>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled before the run, which then starts no ping, or while it
    /// waited, which leaves the ping in flight. The exception carries <paramref name="cancellationToken"/> and a
    /// fixed message.
    /// </exception>
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        // A caller that has already given up reads no result, so no ping is started for it.
        if (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(CancelledMessage, cancellationToken);
        }

        (Task<PingOutcome> ping, TimeSpan wait) = JoinOrStartPing();
        if (wait <= TimeSpan.Zero)
        {
            // The shared ping has been pending for longer than the bound, so the database has not answered in time:
            // this run answers at once, as a wait that reached the bound does, and starts no ping of its own.
            return ReportTimedOut();
        }

        PingOutcome outcome;
        try
        {
            // The settlement never faults or cancels, so the wait ends only with the outcome, what is left of the
            // ping's bound or the caller's cancellation.
            outcome = await ping.WaitAsync(wait, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The bound passed with the ping still pending: the database did not answer in time. The ping is left to
            // end on its own; a run that joins it later answers at once.
            return ReportTimedOut();
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            // The caller's own cancellation of its wait; the shared ping goes on for the other runs.
            throw new OperationCanceledException(CancelledMessage, exception, cancellationToken);
        }

        switch (outcome.Kind)
        {
            case PingOutcomeKind.Answered:
                return HealthCheckResult.Healthy(HealthyDescription);
            case PingOutcomeKind.NotAnswered:
                LogBestEffort(() => LogPingNotAnswered(_logger, Name));
                return HealthCheckResult.Unhealthy(UnhealthyDescription);
            default:
                LogBestEffort(() => LogPingFailed(
                    _logger,
                    Name,
                    outcome.ExceptionType,
                    outcome.Operation,
                    outcome.SqlErrorNumber));
                return HealthCheckResult.Unhealthy(UnhealthyDescription);
        }
    }

    // The settlement of the ping in flight, or of a new one this run starts, and how long this run may wait for it: the
    // full bound for the run that starts the ping; for a run that joins it, what is left of the bound counted from that
    // ping's start, never more than the bound, and zero or less once the ping is overdue.
    private (Task<PingOutcome> Ping, TimeSpan Wait) JoinOrStartPing()
    {
        TaskCompletionSource<PingOutcome> settlement;
        lock (_pingLock)
        {
            if (_pingInFlight is { } inFlight)
            {
                return (inFlight, _pingTimeout - Stopwatch.GetElapsedTime(_pingStartedAt));
            }

            // Its continuations run asynchronously, so no waiting run resumes on the thread that settles the ping.
            settlement = new TaskCompletionSource<PingOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pingInFlight = settlement.Task;
            _pingStartedAt = Stopwatch.GetTimestamp();
        }

        // Outside the lock; a run arriving meanwhile joins the settlement. No token: the class remarks give why the
        // ping is never cancelled.
        try
        {
            // On the thread pool, never on this run's thread, so this run's wait begins at once: a store may work
            // synchronously before its first await, as SqlClient does for up to its connect timeout when it replaces a
            // dead pooled connection (class remarks). Task.Run's proxy carries the store's task, and a store that
            // throws instead of returning one faults the proxy with that exception; one that returns none faults it
            // with the fixed failure below.
            Task<bool> ping = Task.Run(
                () => _dataStore.PingAsync(CancellationToken.None)
                    ?? throw new InvalidOperationException(NoPingTaskMessage));

            // The continuation's own task is not kept: Settle catches every failure, so it cannot fault.
            _ = ping.ContinueWith(
                static (ended, state) =>
                {
                    var (check, pending) = ((SqlServerHealthCheck, TaskCompletionSource<PingOutcome>))state!;
                    check.Settle(ended, pending);
                },
                (this, settlement),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        catch (Exception exception)
        {
            // Handing the ping over failed, so the store was never reached; the ping must still settle, or every
            // later run would join a settlement that never comes.
            Publish(settlement, PingOutcome.Failed(exception));
        }

        return (settlement.Task, _pingTimeout);
    }

    // Runs once the ping has ended, whether or not a run still waits for it.
    private void Settle(Task<bool> ended, TaskCompletionSource<PingOutcome> settlement)
    {
        PingOutcome outcome;
        try
        {
            // Reading the result observes a fault, so a ping no run waits for leaves no unobserved exception.
            outcome = ended.GetAwaiter().GetResult() ? PingOutcome.Answered : PingOutcome.NotAnswered;
        }
        catch (Exception exception)
        {
            // Only what DescribeStoreFailure keeps is carried on: never the exception, its message or inner exception.
            outcome = PingOutcome.Failed(exception);
        }

        Publish(settlement, outcome);
    }

    // Clears the ping in flight before the waiting runs resume, so a run that sees this outcome, and every run after
    // it, starts a new ping rather than reading this one's result again.
    private void Publish(TaskCompletionSource<PingOutcome> settlement, PingOutcome outcome)
    {
        lock (_pingLock)
        {
            if (ReferenceEquals(_pingInFlight, settlement.Task))
            {
                _pingInFlight = null;
            }
        }

        settlement.TrySetResult(outcome);
    }

    // A ping still pending at the bound. Like every failure, it is reported without an exception, and its one line
    // names only the check.
    private HealthCheckResult ReportTimedOut()
    {
        LogBestEffort(() => LogPingTimedOut(_logger, Name));
        return HealthCheckResult.Unhealthy(UnhealthyDescription);
    }

    private static void LogBestEffort(Action log)
    {
        try
        {
            log();
        }
        catch (Exception)
        {
            // Deliberately swallowed: a failing log provider must never replace the Unhealthy result, since the
            // health-check service would then report the check as thrown, with the logger's exception attached.
        }
    }

    // Only the members the store exceptions document as safe to log: a fixed operation name and the SQL error number.
    private static (string? Operation, int? SqlErrorNumber) DescribeStoreFailure(Exception exception) =>
        exception switch
        {
            DataStoreUnavailableException unavailable => (unavailable.Operation, unavailable.SqlErrorNumber),
            DataStoreWriteException failed => (failed.Operation, failed.SqlErrorNumber),
            _ => (null, null),
        };

    private enum PingOutcomeKind
    {
        Answered,
        NotAnswered,
        Failed,
    }

    // How a ping ended, holding only what the failure line may carry and never the exception, so a settlement shared
    // by many runs, or by none, holds nothing that a log or result could leak. ExceptionType is empty unless Failed.
    private readonly record struct PingOutcome(
        PingOutcomeKind Kind,
        string ExceptionType,
        string? Operation,
        int? SqlErrorNumber)
    {
        public static PingOutcome Answered { get; } = new(PingOutcomeKind.Answered, string.Empty, null, null);

        public static PingOutcome NotAnswered { get; } = new(PingOutcomeKind.NotAnswered, string.Empty, null, null);

        public static PingOutcome Failed(Exception exception)
        {
            var (operation, sqlErrorNumber) = DescribeStoreFailure(exception);
            return new PingOutcome(PingOutcomeKind.Failed, exception.GetType().Name, operation, sqlErrorNumber);
        }
    }

    [LoggerMessage(
        EventId = 1,
        EventName = "HealthCheckPingFailed",
        Level = LogLevel.Error,
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
        Level = LogLevel.Error,
        Message = "Health check {CheckName} failed: the data store reported that the database did not answer.")]
    private static partial void LogPingNotAnswered(ILogger logger, string checkName);

    [LoggerMessage(
        EventId = 3,
        EventName = "HealthCheckPingTimedOut",
        Level = LogLevel.Error,
        Message = "Health check {CheckName} failed: the data store did not answer within the check's ping timeout.")]
    private static partial void LogPingTimedOut(ILogger logger, string checkName);
}
