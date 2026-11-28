using System.Data;
using System.Data.Common;
using Dapper;
using EpiInfo.Domain.Storage;
using Microsoft.Data.SqlClient;

namespace EpiInfo.Data.Sql;

/// <summary>
/// Opens the SQL Server connections of the data layer from <see cref="DatabaseOptions"/>, and reports every failure to
/// open one as the store's own exceptions, so no SqlClient exception leaves the data layer from here.
/// </summary>
/// <remarks>
/// <para>
/// Each call opens a new connection that the caller owns and disposes (<c>await using</c>); SqlClient pools the
/// physical connections. Nothing is retried or logged here, and no connection, result or failure is cached: the one
/// state kept from call to call is the outage gate (<see cref="SqlOutageGate"/>), which records whether SQL Server
/// was last found unreachable.
/// </para>
/// <para>
/// SqlClient's pool blocking period is disabled (<see cref="PoolBlockingPeriod.NeverBlock"/>) unless the connection
/// string sets <c>Pool Blocking Period</c> (or <c>PoolBlockingPeriod</c>) itself, in which case that setting is kept:
/// <see cref="EffectiveConnectionString"/> puts the default ahead of the configured string, whose own setting SqlClient
/// reads last and keeps. With blocking enabled, the SqlClient default for a server outside Azure SQL, one failed open
/// makes the pool answer every open for the next 5 to 60 seconds with that cached failure, without contacting the
/// server, so the API would go on reporting the database unavailable after SQL Server is back. With it disabled, each
/// open after a failure can contact the server again, so the next request after SQL Server returns succeeds without a
/// restart. That is not a retry: each call still opens at most once, and the failure of that open is what it reports.
/// The cost is that an open that contacts an unreachable server takes as long as SqlClient's own attempt, up to the
/// connection string's connect timeout, before it fails; and SqlClient's pool makes one physical connection at a
/// time, so the opens waiting in the pool behind that attempt would each wait out an attempt of their own in turn, the
/// k-th about k connect timeouts. The outage gate and each open's deadline bound that cost; a connection string that
/// prefers the cached failure sets <c>Pool Blocking Period</c> itself.
/// </para>
/// <para>
/// <b>Outage gate.</b> When an open fails because SQL Server could not be reached or used, which is a
/// <see cref="SqlException"/> that <see cref="SqlErrorClassifier"/> classifies as unavailable (a transport failure, a
/// connect timeout, a failed login, a database that cannot be opened), the factory records an outage. At that first
/// failure it also releases every other open then in flight: each opens under a token linked to its caller's and to
/// the gate's current release generation, which the failure cancels, so an open still waiting inside SqlClient's pool
/// ends at once as unavailable instead of waiting out its own attempt. While the outage is recorded, one open at a
/// time goes through to SqlClient as the probe, and every other is refused at once, without asking SqlClient, as
/// unavailable with no SQL error number. A probe that opens, or fails in a way showing that the server answered, such
/// as pool exhaustion, clears the outage, so the next request opens normally, without a restart; a probe that fails
/// as unreachable keeps it, and the next open probes again. A client's cancellation changes nothing. Recording the
/// outage retires the generation, so an open admitted before it changes nothing when it ends: its late success
/// cannot clear the outage while the probe runs, nor its late failure record another once a probe has cleared it.
/// Clearing starts no generation, because the probe that clears it is the only open of its generation then in flight.
/// </para>
/// <para>
/// <b>Deadline.</b> Every open the gate admits, normal or probe, also has a deadline of its own: the connection
/// string's connect timeout plus a one-second grace, or none for a connect timeout of 0, which waits indefinitely.
/// SqlClient cannot stop an attempt it has begun, so an open waiting in the pool behind an attempt begun for an open
/// that has already ended, released or given up by its caller, whose failure no open hears, would begin an attempt of
/// its own only when that one fails, and fail one connect timeout later. The deadline ends such an open first, as
/// unavailable with no SQL error number: no connection within the connect timeout shows that the server did not
/// answer, so it records the outage, as an unreachable server's failure does, and releases every other open in
/// flight. The grace keeps SqlClient's pool-timeout failure, due at the connect timeout, ahead of the deadline, so pool
/// exhaustion keeps its own classification. During an outage every admitted open therefore answers within about one
/// connect timeout, and every refused one at once.
/// </para>
/// <para>
/// The connection string carries the database password, so it never appears in an exception this class raises: the
/// exceptions carry their fixed messages, the caller's fixed operation name and, at most, a SQL error number. The
/// original failure is attached only as the inner exception, which the API never logs or returns.
/// </para>
/// <para>
/// The instance holds immutable options and its outage gate, whose state changes only under the gate's lock, so it is
/// safe to share across threads. The store builds one factory, and the API registers one store per process, so the
/// recorded outage is the process's.
/// </para>
/// </remarks>
/// <param name="options">The connection string and command timeout.</param>
internal sealed class SqlConnectionFactory(DatabaseOptions options)
{
    // The default every connection string with a setting of its own opens behind. It goes first, never last: SqlClient
    // keeps the last value it reads for a keyword, under either of its spellings, so a Pool Blocking Period or
    // PoolBlockingPeriod the configured string sets wins over it.
    private const string NeverBlockDefault = "Pool Blocking Period=NeverBlock;";

    // How far past its connect timeout an admitted open's deadline falls. SqlClient ends its own wait for a pooled
    // connection at the connect timeout with its pool-timeout InvalidOperationException; the grace keeps that exception
    // ahead of the deadline, so an exhausted pool is still reported from SqlClient's own failure, as pool exhaustion,
    // rather than as an expired deadline.
    private const int DeadlineGraceSeconds = 1;

    // The longest deadline set, about 24.8 days, well within what a CancellationTokenSource timer accepts.
    private static readonly TimeSpan MaxDeadline = TimeSpan.FromMilliseconds(int.MaxValue);

    private readonly DatabaseOptions options = options ?? throw new ArgumentNullException(nameof(options));

    private readonly SqlOutageGate outageGate = new();

    /// <summary>
    /// The timeout, in whole seconds, every command must run with: <c>Database:CommandTimeoutSeconds</c>, 30 by default.
    /// Callers pass it to <see cref="SqlExecution"/>.
    /// </summary>
    internal int CommandTimeoutSeconds => options.CommandTimeoutSeconds;

    /// <summary>Opens a new connection to the configured database.</summary>
    /// <remarks>
    /// <para>
    /// The connection opens with <see cref="EffectiveConnectionString"/>: the configured string, behind a default that
    /// disables the pool blocking period unless the string sets it. SqlClient reads and validates that string when the
    /// connection is created, on each call and never at construction, exactly as it reads the configured string alone,
    /// so the default changes neither which strings are accepted nor any other setting. After a failed open, the next
    /// call the outage gate lets through therefore contacts the server again instead of receiving the pool's cached
    /// failure, and succeeds as soon as SQL Server answers.
    /// </para>
    /// <para>
    /// The steps run in this order: the <paramref name="operation"/> guard; the absent connection string; the
    /// connection object, whose creation rejects a malformed string; then, for a caller whose token is already
    /// cancelled, the open exactly as SqlClient refuses it, which neither consults nor changes the gate, because it
    /// learns nothing about the server; otherwise the gate's admission (<see cref="SqlOutageGate.Admit"/>) and the open
    /// it admits:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// with no outage recorded, a normal open under a token linked to <paramref name="cancellationToken"/>, to the
    /// gate's release generation and to the open's deadline. A <see cref="SqlException"/> classified as unavailable
    /// records an outage and releases every other normal open in flight, unless an earlier failure has already
    /// retired the generation the open was admitted under; such a released open is reported as unavailable with no
    /// SQL error number and its cancellation as the inner exception;
    /// </description></item>
    /// <item><description>
    /// with an outage recorded and no probe in flight, the probe, under a token linked to
    /// <paramref name="cancellationToken"/> and to the open's deadline;
    /// </description></item>
    /// <item><description>
    /// with an outage recorded and a probe in flight, nothing: the connection object is released and the call fails at
    /// once as unavailable, with no SQL error number and no inner exception, having asked SqlClient for nothing.
    /// </description></item>
    /// </list>
    /// <para>
    /// The deadline (<see cref="OpenDeadline"/>) is the connection's <see cref="SqlConnection.ConnectionTimeout"/> plus
    /// a one-second grace, counted from just before SqlClient is asked, and there is none for a connect timeout of 0.
    /// An open whose deadline passes before it has a connection, and before <paramref name="cancellationToken"/> is
    /// cancelled or a release ends it, is released and reported as unavailable with no SQL error number and its
    /// cancellation as the inner exception, and records the outage as an unavailable <see cref="SqlException"/> does,
    /// releasing every other normal open in flight. Every admitted open therefore answers within about one connect
    /// timeout, even one SqlClient holds in its pool behind an attempt begun for an open that has since ended, and
    /// every refused one at once.
    /// </para>
    /// <para>
    /// A connection that opens, and an open failure that is neither such an unavailable <see cref="SqlException"/>, an
    /// expired deadline nor a cancellation, clear the outage; a cancellation by <paramref name="cancellationToken"/>
    /// leaves it as it was. An open admitted under a generation that a later recorded outage has retired changes the
    /// gate's state in none of these ways. A probe frees the probe slot however it ends.
    /// </para>
    /// <para>
    /// A connection that fails to open is released here, only after its failure is classified
    /// (<see cref="OpenFailureAsync"/>). An error from that release is discarded, so the exception raised is always the
    /// one for the open failure.
    /// </para>
    /// </remarks>
    /// <param name="operation">
    /// The fixed name of the store operation that needs the connection, such as <c>InsertAsync</c>; never request input.
    /// </param>
    /// <param name="cancellationToken">Cancels the open when the client aborts the request.</param>
    /// <returns>An open connection, owned and disposed by the caller.</returns>
    /// <exception cref="ArgumentException"><paramref name="operation"/> is null, empty or whitespace.</exception>
    /// <exception cref="DataStoreUnavailableException">
    /// No connection string is configured, detected before any connection object exists; opening failed in a way
    /// <see cref="SqlErrorClassifier"/> classifies as unavailable (unreachable server, failed login, database that
    /// cannot be opened, timeout, pool exhaustion); the outage gate refused the open, or released it when another
    /// open found the server unreachable; or the open's deadline passed before it had a connection; the last three with
    /// no SQL error number. Its outcome is <see cref="WriteOutcome.NotWritten"/>.
    /// </exception>
    /// <exception cref="DataStoreWriteException">
    /// The connection string is malformed, or opening failed for any other reason. Its outcome is
    /// <see cref="WriteOutcome.NotWritten"/>.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> cancelled the open. Once the gate has admitted the open, the exception
    /// carries <paramref name="cancellationToken"/>, never the linked token, which is the factory's own.
    /// </exception>
    internal async Task<SqlConnection> OpenAsync(string operation, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        if (!options.HasConnectionString)
        {
            throw new DataStoreUnavailableException(operation, WriteOutcome.NotWritten);
        }

        SqlConnection connection;
        try
        {
            // Not null: HasConnectionString, checked above, is false for a null string.
            connection = new SqlConnection(EffectiveConnectionString(options.ConnectionString!));
        }
        catch (Exception exception) when (IsConnectionStringFault(exception))
        {
            // SqlClient parses the string here and rejects a malformed one; that is a configuration fault (500), not an
            // unavailable server.
            throw new DataStoreWriteException(operation, WriteOutcome.NotWritten, sqlErrorNumber: null, exception);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            // An aborted request learns nothing about the server: SqlClient refuses the open without contacting it, and
            // the refusal is reported as it always was, outside the gate.
            await OpenUngatedAsync(connection, operation, cancellationToken).ConfigureAwait(false);
            return connection;
        }

        SqlOutageGate.Ticket ticket = outageGate.Admit();
        if (ticket.Admission == SqlOutageGate.Admission.Refused)
        {
            // A probe is finding out whether the recorded outage is over; until it ends, the answer is unavailable,
            // given before SqlClient is asked, so no request waits out a connect timeout of its own.
            await ReleaseUnopenedAsync(connection).ConfigureAwait(false);
            throw new DataStoreUnavailableException(operation, WriteOutcome.NotWritten);
        }

        // What the gate hears once the open ends. A failure nothing below foresees leaves it Cancelled, which changes
        // no state but frees a probe's slot.
        SqlOutageGate.OpenEnd end = SqlOutageGate.OpenEnd.Cancelled;

        // The open's own deadline, a source of its own so that its expiry stays distinct from the caller's cancellation
        // and from a release; none when the connect timeout is 0, which SqlClient reads as waiting indefinitely.
        CancellationTokenSource? deadline = OpenDeadline(connection.ConnectionTimeout) is TimeSpan due
            ? new CancellationTokenSource(due)
            : null;

        // A probe runs only while an outage is recorded, when no release can be pending, so it opens under the caller's
        // token and its deadline; a normal open is also ended by the release of its generation. For a probe, the
        // ticket's release token is CancellationToken.None, which the linked source ignores.
        CancellationTokenSource? linked = ticket.Admission == SqlOutageGate.Admission.Normal || deadline is not null
            ? CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                ticket.Release,
                deadline?.Token ?? CancellationToken.None)
            : null;
        try
        {
            await connection.OpenAsync(linked?.Token ?? cancellationToken).ConfigureAwait(false);
            end = SqlOutageGate.OpenEnd.Opened;
            return connection;
        }
        catch (OperationCanceledException exception)
            when (ticket.Release.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Released: another open found the server unreachable while this one waited in SqlClient's pool. Nothing
            // was sent, and the answer is the outage that open found, not a cancellation the client never asked for.
            // A release that came with this open's deadline is taken as the release: the outage is already recorded.
            await ReleaseUnopenedAsync(connection).ConfigureAwait(false);
            throw new DataStoreUnavailableException(
                operation,
                WriteOutcome.NotWritten,
                sqlErrorNumber: null,
                exception);
        }
        catch (OperationCanceledException exception)
            when (deadline is not null
                && deadline.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested)
        {
            // No connection within the connect timeout and its grace, whether SqlClient attempted this open or held it
            // in its pool behind an attempt begun for an open that has since ended, whose failure no open hears. Either
            // way the server did not answer in time: the outage evidence an unreachable server gives, so the gate
            // records it and releases every other open in flight. Nothing was sent.
            end = SqlOutageGate.OpenEnd.ServerUnreachable;
            await ReleaseUnopenedAsync(connection).ConfigureAwait(false);
            throw new DataStoreUnavailableException(
                operation,
                WriteOutcome.NotWritten,
                sqlErrorNumber: null,
                exception);
        }
        catch (Exception exception)
        {
            Exception failure = await OpenFailureAsync(connection, operation, exception, cancellationToken)
                .ConfigureAwait(false);
            end = SqlOutageGate.EndOf(exception, failure);
            if (failure is OperationCanceledException cancellation
                && cancellation.CancellationToken != cancellationToken)
            {
                throw CallerCancellation(cancellation, exception, cancellationToken);
            }

            if (ReferenceEquals(failure, exception))
            {
                throw;
            }

            throw failure;
        }
        finally
        {
            // Disposed first, so a release this open's own ending triggers never reaches its linked source; the
            // deadline after the source linked to it, which also stops its timer.
            linked?.Dispose();
            deadline?.Dispose();
            outageGate.Complete(ticket, end);
        }
    }

    /// <summary>
    /// The deadline of an open the outage gate admits: <paramref name="connectTimeoutSeconds"/> plus a one-second
    /// grace (<see cref="DeadlineGraceSeconds"/>), at most about 24.8 days, or none for a connect timeout of 0, which
    /// SqlClient reads as waiting indefinitely.
    /// </summary>
    /// <remarks>
    /// Internal, rather than private, so that <c>SqlErrorClassifierTests</c> can check the rule without waiting out a
    /// deadline.
    /// </remarks>
    /// <param name="connectTimeoutSeconds">
    /// The connection's <see cref="SqlConnection.ConnectionTimeout"/>, in seconds, which SqlClient never lets fall
    /// below 0.
    /// </param>
    /// <returns>How long the open may wait for a connection; <see langword="null"/> for no deadline.</returns>
    internal static TimeSpan? OpenDeadline(int connectTimeoutSeconds)
    {
        if (connectTimeoutSeconds <= 0)
        {
            return null;
        }

        TimeSpan deadline = TimeSpan.FromSeconds(connectTimeoutSeconds + (long)DeadlineGraceSeconds);
        return deadline < MaxDeadline ? deadline : MaxDeadline;
    }

    // The open of a caller whose token is already cancelled, exactly as every open ran before the outage gate existed.
    private static async Task OpenUngatedAsync(
        SqlConnection connection,
        string operation,
        CancellationToken cancellationToken)
    {
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Exception failure = await OpenFailureAsync(connection, operation, exception, cancellationToken)
                .ConfigureAwait(false);
            if (ReferenceEquals(failure, exception))
            {
                throw;
            }

            throw failure;
        }
    }

    // Releases a connection that never opened: refused by the gate, or released by it. Nothing can have been sent on
    // it, so an error from the release is discarded, as OpenFailureAsync discards one, and what is reported stands.
    private static async Task ReleaseUnopenedAsync(SqlConnection connection)
    {
        try
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Discarded by design: the unavailable answer is what is reported, and nothing was sent on this connection.
        }
    }

    // The cancellation an admitted open raises: SqlClient's own carries no token, or the linked token, which the caller
    // never sees, so the one raised carries the caller's token, with the fixed message, the original failure as its
    // inner exception, and any SQL error number the classified cancellation kept.
    private static OperationCanceledException CallerCancellation(
        OperationCanceledException cancellation,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var raised = new OperationCanceledException(SqlErrorClassifier.CancelledMessage, exception, cancellationToken);
        if (cancellation.Data[SqlErrorClassifier.SqlErrorNumberKey] is int number)
        {
            raised.Data[SqlErrorClassifier.SqlErrorNumberKey] = number;
        }

        return raised;
    }

    /// <summary>
    /// The connection string every connection opens with: <c>Pool Blocking Period=NeverBlock;</c> followed by
    /// <paramref name="configured"/>, verbatim. SqlClient reads a connection string's settings in order and keeps the
    /// last value of each keyword, under either of its spellings, so a <c>Pool Blocking Period</c> or
    /// <c>PoolBlockingPeriod</c> the configured string sets is the one that applies, and the default applies only when
    /// it sets none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The string is not parsed here, so the default changes neither which strings are accepted nor how any setting
    /// is read: <see cref="SqlConnection"/> parses and validates the result as it would the configured string alone.
    /// The default's closing <c>;</c> returns SqlClient's parser to the state it starts in, so the configured string is
    /// read from that state, and SqlClient validates only the value it keeps for each keyword. A configured string
    /// SqlClient rejects, an invalid <c>Pool Blocking Period</c> included, is still rejected; one it accepts, with an
    /// earlier duplicate holding an invalid value or with leading or trailing separators, is still accepted with the
    /// same settings.
    /// </para>
    /// <para>
    /// A configured string of nothing but <c>;</c> separators, white space and NUL terminators holds no setting, and
    /// is returned unchanged. SqlClient refuses to open such a string without contacting any server, or rejects it
    /// when a <c>;</c> follows a NUL; behind the default, the default would be its one setting, and the open would
    /// contact the default local server instead.
    /// </para>
    /// <para>
    /// Internal, rather than private, so that <c>SqlErrorClassifierTests</c> can check the string and clear the pool
    /// it keys.
    /// </para>
    /// </remarks>
    /// <param name="configured">
    /// The configured connection string, not null. It carries the database password, so neither it nor the result is
    /// ever put in a message.
    /// </param>
    /// <returns>
    /// The effective connection string; <paramref name="configured"/> itself when it holds no setting.
    /// </returns>
    internal static string EffectiveConnectionString(string configured) =>
        HoldsNoSetting(configured) ? configured : NeverBlockDefault + configured;

    // SqlClient's parser skips ';' and white space between settings, and after a NUL accepts only NULs and white
    // space, so any other character starts a setting or makes SqlClient reject the string.
    private static bool HoldsNoSetting(string configured) =>
        configured.All(static character => character is ';' or '\0' || char.IsWhiteSpace(character));

    // Every way SqlConnection rejects a connection string before any connection exists. It raises an
    // ArgumentException for a syntax error, an unknown keyword, a value that does not convert, is out of range or is
    // too long, and for settings that contradict each other, such as a Min Pool Size above Max Pool Size; and a
    // NotSupportedException for a keyword this platform does not support, such as Network Library.
    private static bool IsConnectionStringFault(Exception exception) =>
        exception is ArgumentException or NotSupportedException;

    /// <summary>
    /// Classifies a failure to open <paramref name="connection"/>, then releases the connection, and returns the
    /// exception <see cref="OpenAsync"/> raises for it.
    /// </summary>
    /// <remarks>
    /// The order is the point: the failure is classified, and its exception built, before the release. A release that
    /// throws therefore cannot replace the open failure or leave it unclassified, which would let a write's caller
    /// report <see cref="WriteOutcome.Unknown"/> for a save that provably sent nothing. A release error is discarded:
    /// nothing can have been sent on a connection that never opened, and the open failure is what is reported.
    /// </remarks>
    /// <param name="connection">The connection that failed to open; released here.</param>
    /// <param name="operation">The fixed name of the store operation that needed the connection.</param>
    /// <param name="exception">The failure the open raised.</param>
    /// <param name="cancellationToken">The token the open ran under.</param>
    /// <returns>
    /// <paramref name="exception"/> itself, to be rethrown unchanged, when it is an
    /// <see cref="OperationCanceledException"/> the classifier recognises as the client's cancellation. Otherwise the
    /// <see cref="SqlErrorClassifier.ToDataStoreException"/> result for its classification, with
    /// <paramref name="exception"/> as its inner exception: a <see cref="DataStoreUnavailableException"/> or
    /// <see cref="DataStoreWriteException"/> with outcome <see cref="WriteOutcome.NotWritten"/>, or, for any other
    /// failure classified as cancelled, an <see cref="OperationCanceledException"/>, so no SqlException leaves the data
    /// layer from here.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="connection"/> or <paramref name="exception"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="operation"/> is null, empty or whitespace.</exception>
    internal static async Task<Exception> OpenFailureAsync(
        IAsyncDisposable connection,
        string operation,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(exception);

        // Opening is the one place where SqlClient's pool-timeout InvalidOperationException means unavailable.
        SqlFailure failure = SqlErrorClassifier.Classify(exception, cancellationToken, duringOpen: true);
        Exception result = failure.Kind == SqlFailureKind.Cancelled && exception is OperationCanceledException
            ? exception
            : SqlErrorClassifier.ToDataStoreException(failure, operation, WriteOutcome.NotWritten, exception);

        try
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Discarded by design: the open failure, already classified, is what is reported, and a release error must
            // not replace it. Nothing was sent on this connection, so NotWritten still holds.
        }

        return result;
    }
}

/// <summary>
/// The outage gate of one <see cref="SqlConnectionFactory"/>: whether an outage of SQL Server is recorded, whether the
/// one probe open allowed through during it is in flight, and the release generation whose cancellation ends every
/// normal open in flight when an outage is first recorded.
/// </summary>
/// <remarks>
/// <para>
/// <b>Admission</b> (<see cref="Admit"/>). Every open gets a <see cref="Ticket"/>: how it is let through and the
/// generation current at its admission. With no outage recorded, every open is <see cref="Admission.Normal"/> and
/// opens under the current generation's release token, linked to its caller's and to its deadline, which the factory
/// sets. With an outage recorded, the first open to find no probe in flight is the <see cref="Admission.Probe"/>, and
/// every other is <see cref="Admission.Refused"/> until that probe ends.
/// </para>
/// <para>
/// <b>Ending</b> (<see cref="Complete"/>). A probe frees the probe slot however it ends. Only an open of the current
/// generation changes the state; one of a retired generation changes nothing, whatever it reports, because it was
/// admitted before the outage that retired it was recorded, so its answer is older than the state. For an open of the
/// current generation: <see cref="OpenEnd.ServerUnreachable"/> with no outage recorded records one, installs a fresh
/// generation with a release source of its own and then, outside the lock, cancels the release source of the one it
/// replaced, so every normal open admitted under it ends at once; with an outage already recorded it changes nothing.
/// <see cref="OpenEnd.Opened"/> and
/// <see cref="OpenEnd.ServerAnswered"/> clear the outage. <see cref="OpenEnd.Cancelled"/> changes nothing.
/// <see cref="EndOf"/> derives an open failure's ending from the exception
/// <see cref="SqlConnectionFactory.OpenFailureAsync"/> built from the classification of
/// <see cref="SqlErrorClassifier"/>, so the gate holds no classification of its own.
/// </para>
/// <para>
/// <b>Generations.</b> Only recording an outage starts a generation; clearing one starts none. Every open admitted
/// before the recording belongs to the generation it retires, so a success or failure such an open reports late can
/// neither clear the outage while its probe runs nor record another once a probe has cleared it. Between the
/// recording and the clearing, the only opens of the new generation are probes, admitted one at a time, each ending
/// before the next is admitted, and the clearing is the last probe's own ending; so no other open of the generation
/// is in flight when the outage clears, and the normal opens admitted from then on are the generation's first.
/// </para>
/// <para>
/// <b>Threading.</b> Every state change runs under one lock, held for a few assignments and never while SqlClient runs
/// or a token is cancelled, so the healthy path costs two uncontended lock entries per open, and a ticket, a struct,
/// allocates nothing. A generation's release source is cancelled at most once and never disposed: an open admitted
/// under it may still link to it, and a source with no timer whose wait handle is never read holds no unmanaged
/// resource. Each open disposes its own linked and deadline sources.
/// </para>
/// <para>Nothing here logs, retries, or reads request data, and the internal members exist for its unit tests.</para>
/// </remarks>
internal sealed class SqlOutageGate
{
    private readonly Lock sync = new();
    private bool outageRecorded;
    private bool probeInFlight;
    private CancellationTokenSource release = new();

    // The current generation, advanced with each fresh release source. It starts at 1, so a default ticket, whose
    // generation is 0, is never of the current generation and changes nothing.
    private long generation = 1;

    /// <summary>How <see cref="Admit"/> lets one open through.</summary>
    internal enum Admission
    {
        /// <summary>
        /// No outage is recorded: the open runs under its caller's token, the release token and its own deadline.
        /// </summary>
        Normal,

        /// <summary>
        /// An outage is recorded and no probe was in flight: the open runs, under its caller's token and its own
        /// deadline.
        /// </summary>
        Probe,

        /// <summary>An outage is recorded and a probe is in flight: the open does not run.</summary>
        Refused,
    }

    /// <summary>How an admitted open ended, as <see cref="Complete"/> hears it.</summary>
    internal enum OpenEnd
    {
        /// <summary>The connection opened: the server answered.</summary>
        Opened,

        /// <summary>
        /// The open failed with a <see cref="SqlException"/> classified as unavailable, or its deadline passed before
        /// it had a connection: SQL Server could not be reached or used.
        /// </summary>
        ServerUnreachable,

        /// <summary>
        /// The open failed otherwise, which shows that the server answered: pool exhaustion, raised by a pool whose
        /// connections are open, or an unexpected failure.
        /// </summary>
        ServerAnswered,

        /// <summary>
        /// The open was cancelled by its caller or released by the gate: nothing was learnt about the server.
        /// </summary>
        Cancelled,
    }

    /// <summary>
    /// What <see cref="Admit"/> hands one open, and <see cref="Complete"/> takes back once an admitted open ends: how
    /// the open is let through, its release token and the generation it was admitted under.
    /// </summary>
    internal readonly struct Ticket
    {
        internal Ticket(Admission admission, long generation, CancellationToken release)
        {
            Admission = admission;
            Generation = generation;
            Release = release;
        }

        /// <summary>How the open is let through.</summary>
        internal Admission Admission { get; }

        /// <summary>The generation current when the open was admitted.</summary>
        internal long Generation { get; }

        /// <summary>
        /// For <see cref="Admission.Normal"/>, the release token of <see cref="Generation"/>, cancelled when an open of
        /// that generation records an outage, which retires it; otherwise <see cref="CancellationToken.None"/>.
        /// </summary>
        internal CancellationToken Release { get; }
    }

    /// <summary>Whether an outage is recorded.</summary>
    internal bool OutageRecorded
    {
        get
        {
            lock (sync)
            {
                return outageRecorded;
            }
        }
    }

    /// <summary>Whether the probe admitted during the recorded outage has yet to end.</summary>
    internal bool ProbeInFlight
    {
        get
        {
            lock (sync)
            {
                return probeInFlight;
            }
        }
    }

    /// <summary>
    /// How an open failure ends for the gate, from <paramref name="reported"/>, the exception
    /// <see cref="SqlConnectionFactory.OpenFailureAsync"/> built for <paramref name="openFailure"/>.
    /// </summary>
    /// <param name="openFailure">The failure the open raised.</param>
    /// <param name="reported">The exception the factory raises for it.</param>
    /// <returns>
    /// <see cref="OpenEnd.Cancelled"/> for an <see cref="OperationCanceledException"/>, the classifier's client
    /// cancellation; <see cref="OpenEnd.ServerUnreachable"/> for a <see cref="DataStoreUnavailableException"/> raised
    /// for a <see cref="SqlException"/>, a transport failure, a connect timeout, a failed login or a database that
    /// cannot be opened; <see cref="OpenEnd.ServerAnswered"/> for anything else, including the
    /// <see cref="DataStoreUnavailableException"/> of SqlClient's pool-timeout <see cref="InvalidOperationException"/>,
    /// which a pool full of open connections raises, and every <see cref="DataStoreWriteException"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="openFailure"/> or <paramref name="reported"/> is null.
    /// </exception>
    internal static OpenEnd EndOf(Exception openFailure, Exception reported)
    {
        ArgumentNullException.ThrowIfNull(openFailure);
        ArgumentNullException.ThrowIfNull(reported);

        return reported switch
        {
            OperationCanceledException => OpenEnd.Cancelled,
            DataStoreUnavailableException when openFailure is SqlException => OpenEnd.ServerUnreachable,
            _ => OpenEnd.ServerAnswered,
        };
    }

    /// <summary>Admits one open (class remarks).</summary>
    /// <returns>
    /// The open's ticket, of the current generation: <see cref="Admission.Normal"/> with that generation's release
    /// token, or <see cref="Admission.Probe"/> or <see cref="Admission.Refused"/> with
    /// <see cref="CancellationToken.None"/>.
    /// </returns>
    internal Ticket Admit()
    {
        lock (sync)
        {
            if (!outageRecorded)
            {
                return new Ticket(Admission.Normal, generation, release.Token);
            }

            if (probeInFlight)
            {
                return new Ticket(Admission.Refused, generation, CancellationToken.None);
            }

            probeInFlight = true;
            return new Ticket(Admission.Probe, generation, CancellationToken.None);
        }
    }

    /// <summary>Hears how an open <see cref="Admit"/> let through ended (class remarks).</summary>
    /// <param name="ticket">
    /// The open's ticket, of <see cref="Admission.Normal"/> or <see cref="Admission.Probe"/>, as admitted. The state
    /// changes only when its generation is still the current one.
    /// </param>
    /// <param name="end">How the open ended.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="ticket"/> is <see cref="Admission.Refused"/>, which never opens, or its admission or
    /// <paramref name="end"/> is not a defined value. Nothing changes then.
    /// </exception>
    internal void Complete(Ticket ticket, OpenEnd end)
    {
        if (ticket.Admission is not (Admission.Normal or Admission.Probe))
        {
            throw new ArgumentOutOfRangeException(nameof(ticket));
        }

        if (!Enum.IsDefined(end))
        {
            throw new ArgumentOutOfRangeException(nameof(end));
        }

        CancellationTokenSource? replaced = null;
        lock (sync)
        {
            if (ticket.Admission == Admission.Probe)
            {
                probeInFlight = false;
            }

            // An open of a retired generation was admitted before the outage that retired it was recorded: what it
            // reports is older than the state, so it changes nothing.
            if (ticket.Generation == generation)
            {
                switch (end)
                {
                    case OpenEnd.Opened or OpenEnd.ServerAnswered:
                        outageRecorded = false;
                        break;
                    case OpenEnd.ServerUnreachable when !outageRecorded:
                        outageRecorded = true;
                        replaced = release;
                        release = new CancellationTokenSource();
                        generation++;
                        break;
                }
            }
        }

        if (replaced is not null)
        {
            ReleaseGeneration(replaced);
        }
    }

    // Ends every normal open admitted under the replaced generation. Its callbacks are those opens' linked sources and,
    // through them, SqlClient's cancellation of an open waiting in its pool; Cancel runs every one of them before it
    // reports any failure. A failing callback is discarded: the open that found the server unreachable reports that,
    // and each open whose source was cancelled still ends.
    private static void ReleaseGeneration(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
        }
        catch (AggregateException)
        {
            // Discarded by design, as the comment above sets out.
        }
    }
}


/// <summary>
/// The single path through which <c>EpiInfo.Data</c> sends a <see cref="SqlStatement"/> to SQL Server. Every command
/// runs with an explicit timeout, binds every value as a typed input parameter, and is reported to the
/// <see cref="ISqlCommandObserver"/> before it is sent and once after it completes or fails.
/// </summary>
/// <remarks>
/// <para>
/// Every method takes an open connection, the transaction the command joins (or <see langword="null"/>), the fixed
/// operation name, the statement, the timeout from <see cref="SqlConnectionFactory.CommandTimeoutSeconds"/>, the
/// observer and the caller's cancellation token. <see cref="ExecuteAsync"/> and <see cref="ExecuteScalarAsync{T}"/>
/// also take an optional dispatch callback, which tells a caller that the command passed every local check and is
/// being sent, as distinct from one those checks refused.
/// </para>
/// <para>
/// A command failure is reported to the observer and rethrown unchanged: classifying it, and deciding what a failed
/// write means for the stored data, belong to the caller. An observer that throws never changes a command's result or
/// error; its exception is discarded.
/// </para>
/// <para>
/// The statement text is sent exactly as given. Values travel only in parameters, each bound with the
/// <see cref="DbType"/> and size the statement carries.
/// </para>
/// </remarks>
internal static class SqlExecution
{
    private const string ConnectionNotOpenMessage = "A SQL command runs only on an open connection.";
    private const string ForeignTransactionMessage =
        "The transaction is not an active transaction of the connection the command runs on.";

    /// <summary>Runs a statement that returns no rows, such as an <c>INSERT</c> or <c>UPDATE</c>.</summary>
    /// <param name="connection">An open connection.</param>
    /// <param name="transaction">The transaction the command joins, or <see langword="null"/>.</param>
    /// <param name="operation">The fixed operation name reported to the observer.</param>
    /// <param name="statement">The statement to run.</param>
    /// <param name="commandTimeoutSeconds">The command timeout in whole seconds; at least 1.</param>
    /// <param name="observer">The command observer.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <param name="onDispatch">
    /// Called once, after the guards, immediately before the command is sent; never when a guard rejects it.
    /// <see langword="null"/> for no callback.
    /// </param>
    /// <returns>The number of rows affected.</returns>
    /// <exception cref="InvalidOperationException">
    /// The connection is not open, or the transaction is not an active transaction of it.
    /// </exception>
    internal static Task<int> ExecuteAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        string operation,
        SqlStatement statement,
        int commandTimeoutSeconds,
        ISqlCommandObserver observer,
        CancellationToken cancellationToken,
        Action? onDispatch = null) =>
        RunObservedAsync(
            connection,
            transaction,
            operation,
            statement,
            commandTimeoutSeconds,
            observer,
            static (open, command, _) => open.ExecuteAsync(command),
            onDispatch,
            cancellationToken);

    /// <summary>Runs a statement and returns the first column of its first row.</summary>
    /// <typeparam name="T">The type the value is converted to.</typeparam>
    /// <param name="connection">An open connection.</param>
    /// <param name="transaction">The transaction the command joins, or <see langword="null"/>.</param>
    /// <param name="operation">The fixed operation name reported to the observer.</param>
    /// <param name="statement">The statement to run.</param>
    /// <param name="commandTimeoutSeconds">The command timeout in whole seconds; at least 1.</param>
    /// <param name="observer">The command observer.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <param name="onDispatch">
    /// Called once, after the guards, immediately before the command is sent; never when a guard rejects it.
    /// <see langword="null"/> for no callback.
    /// </param>
    /// <returns>The value, or the default of <typeparamref name="T"/> when there is no row or the value is NULL.</returns>
    /// <exception cref="InvalidOperationException">
    /// The connection is not open, or the transaction is not an active transaction of it.
    /// </exception>
    internal static Task<T?> ExecuteScalarAsync<T>(
        SqlConnection connection,
        SqlTransaction? transaction,
        string operation,
        SqlStatement statement,
        int commandTimeoutSeconds,
        ISqlCommandObserver observer,
        CancellationToken cancellationToken,
        Action? onDispatch = null) =>
        RunObservedAsync(
            connection,
            transaction,
            operation,
            statement,
            commandTimeoutSeconds,
            observer,
            static (open, command, _) => open.ExecuteScalarAsync<T>(command),
            onDispatch,
            cancellationToken);

    /// <summary>Runs a query and maps every row of its first result set to <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">The row type.</typeparam>
    /// <param name="connection">An open connection.</param>
    /// <param name="transaction">The transaction the command joins, or <see langword="null"/>.</param>
    /// <param name="operation">The fixed operation name reported to the observer.</param>
    /// <param name="statement">The statement to run.</param>
    /// <param name="commandTimeoutSeconds">The command timeout in whole seconds; at least 1.</param>
    /// <param name="observer">The command observer.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns>The rows, fully read before the observer hears that the command completed.</returns>
    /// <exception cref="InvalidOperationException">
    /// The connection is not open, or the transaction is not an active transaction of it.
    /// </exception>
    internal static Task<IReadOnlyList<T>> QueryAsync<T>(
        SqlConnection connection,
        SqlTransaction? transaction,
        string operation,
        SqlStatement statement,
        int commandTimeoutSeconds,
        ISqlCommandObserver observer,
        CancellationToken cancellationToken) =>
        RunObservedAsync(
            connection,
            transaction,
            operation,
            statement,
            commandTimeoutSeconds,
            observer,
            static async (open, command, _) =>
            {
                IEnumerable<T> rows = await open.QueryAsync<T>(command).ConfigureAwait(false);
                return (IReadOnlyList<T>)rows.AsList().AsReadOnly();
            },
            onDispatch: null,
            cancellationToken);

    /// <summary>
    /// Runs a statement and hands its data reader to <paramref name="read"/>, for multiple result sets or typed row
    /// reads. Reading and disposing the reader happen inside the observed command, so a failure while fetching rows,
    /// such as a timeout mid-read, is reported to the observer as the command's failure.
    /// </summary>
    /// <typeparam name="T">The result <paramref name="read"/> builds.</typeparam>
    /// <param name="connection">An open connection.</param>
    /// <param name="transaction">The transaction the command joins, or <see langword="null"/>.</param>
    /// <param name="operation">The fixed operation name reported to the observer.</param>
    /// <param name="statement">The statement to run.</param>
    /// <param name="commandTimeoutSeconds">The command timeout in whole seconds; at least 1.</param>
    /// <param name="observer">The command observer.</param>
    /// <param name="cancellationToken">Cancels the command; also passed to <paramref name="read"/>.</param>
    /// <param name="read">
    /// Reads the rows. The reader is valid only until the returned task completes; it is disposed afterwards.
    /// </param>
    /// <returns>The result of <paramref name="read"/>.</returns>
    /// <exception cref="InvalidOperationException">
    /// The connection is not open, or the transaction is not an active transaction of it.
    /// </exception>
    internal static Task<T> ReadAsync<T>(
        SqlConnection connection,
        SqlTransaction? transaction,
        string operation,
        SqlStatement statement,
        int commandTimeoutSeconds,
        ISqlCommandObserver observer,
        CancellationToken cancellationToken,
        Func<DbDataReader, CancellationToken, Task<T>> read)
    {
        ArgumentNullException.ThrowIfNull(read);

        return RunObservedAsync(
            connection,
            transaction,
            operation,
            statement,
            commandTimeoutSeconds,
            observer,
            async (open, command, token) =>
            {
                DbDataReader reader = await open.ExecuteReaderAsync(command).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    return await read(reader, token).ConfigureAwait(false);
                }
            },
            onDispatch: null,
            cancellationToken);
    }

    // The one place a command is sent: argument and state guards, then onDispatch, OnExecuting, the command (with any
    // reading it does), and exactly one OnExecuted with the outcome. A guard failure sends nothing, so it is neither
    // dispatched nor observed. onDispatch runs when every local step that can refuse the command has passed, before
    // the observer hears of it, so a callback that throws also sends nothing and leaves nothing half-observed.
    private static async Task<TResult> RunObservedAsync<TResult>(
        SqlConnection connection,
        SqlTransaction? transaction,
        string operation,
        SqlStatement statement,
        int commandTimeoutSeconds,
        ISqlCommandObserver observer,
        Func<SqlConnection, CommandDefinition, CancellationToken, Task<TResult>> run,
        Action? onDispatch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(statement);
        // SqlClient reads a timeout of 0 as "wait indefinitely", which would defeat the configured limit.
        ArgumentOutOfRangeException.ThrowIfLessThan(commandTimeoutSeconds, 1);
        ArgumentNullException.ThrowIfNull(observer);

        if (connection.State != ConnectionState.Open)
        {
            throw new InvalidOperationException(ConnectionNotOpenMessage);
        }

        // A committed or rolled-back SqlTransaction reports no connection, so this also rejects a finished one.
        if (transaction is not null && !ReferenceEquals(transaction.Connection, connection))
        {
            throw new InvalidOperationException(ForeignTransactionMessage);
        }

        var command = new CommandDefinition(
            commandText: statement.Text,
            parameters: new StatementParameters(statement.Parameters),
            transaction: transaction,
            commandTimeout: commandTimeoutSeconds,
            commandType: CommandType.Text,
            flags: CommandFlags.Buffered,
            cancellationToken: cancellationToken);
        SqlCommandInfo info = Describe(operation, statement, connection, transaction);

        onDispatch?.Invoke();
        NotifyExecuting(observer, info);
        TResult result;
        try
        {
            result = await run(connection, command, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            NotifyExecuted(observer, info, (exception as SqlException)?.Number, exception);
            throw;
        }

        NotifyExecuted(observer, info, sqlErrorNumber: null, error: null);
        return result;
    }

    private static SqlCommandInfo Describe(
        string operation,
        SqlStatement statement,
        SqlConnection connection,
        SqlTransaction? transaction)
    {
        var parameters = new SqlCommandParameterInfo[statement.Parameters.Count];
        for (int index = 0; index < parameters.Length; index++)
        {
            SqlParameterValue parameter = statement.Parameters[index];
            parameters[index] = new SqlCommandParameterInfo(
                parameter.Name,
                parameter.DbType,
                parameter.Value is DBNull ? null : parameter.Value);
        }

        return new SqlCommandInfo(
            operation,
            statement.Text,
            Array.AsReadOnly(parameters),
            connection.ClientConnectionId,
            InTransaction: transaction is not null);
    }

    private static void NotifyExecuting(ISqlCommandObserver observer, SqlCommandInfo info)
    {
        try
        {
            observer.OnExecuting(info);
        }
        catch (Exception)
        {
            // Discarded by design: an observer is a test seam and must never cancel, fail or replace a command. This
            // file logs nothing, because the command text and parameter values must not reach a log.
        }
    }

    private static void NotifyExecuted(ISqlCommandObserver observer, SqlCommandInfo info, int? sqlErrorNumber, Exception? error)
    {
        try
        {
            observer.OnExecuted(info, sqlErrorNumber, error);
        }
        catch (Exception)
        {
            // Discarded by design, as in NotifyExecuting: the command's own result or error stands.
        }
    }

    // Binds the statement's parameters exactly as given: input direction, the explicit DbType and, when set, the size.
    // Dapper's DynamicParameters is deliberately not used: it rewrites "{=name}" literal tokens in the command text,
    // inlining a numeric value even inside a quoted identifier, so a catalogue name holding such a token would put a
    // value into SQL text. Through this binder Dapper never touches the text.
    private sealed class StatementParameters(IReadOnlyList<SqlParameterValue> values) : SqlMapper.IDynamicParameters
    {
        public void AddParameters(IDbCommand command, SqlMapper.Identity identity)
        {
            foreach (SqlParameterValue value in values)
            {
                // DbType is set before Value, so SqlClient binds the declared type and never infers one from the value.
                var parameter = new SqlParameter
                {
                    ParameterName = value.Name,
                    DbType = value.DbType,
                    Direction = ParameterDirection.Input,
                    Value = value.Value,
                };

                if (value.Size is int size)
                {
                    parameter.Size = size;
                }

                command.Parameters.Add(parameter);
            }
        }
    }
}
