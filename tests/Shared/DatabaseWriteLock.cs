using System.Data;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.SqlClient;

namespace EpiInfo.Tests.Shared;

/// <summary>
/// How a test holds the database-wide test lock <see cref="DatabaseWriteLock.ResourceName"/>.
/// </summary>
public enum DatabaseLockMode
{
    /// <summary>
    /// Held by tests whose expected values come from SQL counts or positions that must not move while the
    /// test runs, such as <c>NavigationTests</c> and the position cases of <c>ListAndSearchTests</c>. Shared
    /// holders run side by side; an exclusive request waits for all of them.
    /// </summary>
    Shared,

    /// <summary>
    /// Held by every test that writes, that holds a table lock, or that compares before-and-after snapshots.
    /// It waits for every other holder, shared or exclusive, and keeps all of them out until it is released.
    /// </summary>
    Exclusive,
}

/// <summary>
/// The database-wide application lock that keeps concurrent test processes from disturbing each other's
/// expected values in the rehearsal database.
/// </summary>
/// <remarks>
/// <para>
/// <c>dotnet test EpiInfo.Web.sln</c> runs the test projects concurrently, each in its own process, against the
/// one database. The non-parallel xUnit collection (<see cref="DatabaseCollection"/>) orders tests inside one
/// assembly only, so isolation across processes comes from this lock: <c>sp_getapplock</c> on
/// <see cref="ResourceName"/>, owned by a dedicated session. The Playwright smoke test
/// (<c>web/e2e/support/sql.ts</c>) takes the same resource exclusively, with a transaction as owner, for its
/// whole run. Application locks are scoped to the current database, so every holder connects to the database
/// the resolved connection string names, the one the API under test uses.
/// </para>
/// <para>Which mode a test takes:</para>
/// <list type="bullet">
/// <item><description>
/// <see cref="DatabaseLockMode.Exclusive"/>: every test that writes, through the API or directly (records created
/// through the API, the removed page-two row of a test-created record); every test that holds a table lock
/// (<c>TransactionalSaveTests</c>' <c>TABLOCKX</c> on <c>FoodHistory3</c>); and every test that compares
/// before-and-after <c>SqlSnapshot</c>s (<c>StoredDataContractTests</c>, <c>ReadEquivalenceTests</c>,
/// <c>ConcurrentSaveTests</c>, the save cases of <c>CheckCodeScenarioTests</c>). It is held from the first
/// measurement to the last verification read, so a snapshot comparison sees only the test's own changes.
/// </description></item>
/// <item><description>
/// <see cref="DatabaseLockMode.Shared"/>: tests whose expected values come from SQL counts or positions that
/// must not move during the test, such as <c>NavigationTests</c> and the position cases of
/// <c>ListAndSearchTests</c>.
/// </description></item>
/// <item><description>None: tests that never touch <c>EpiRehearsal</c>.</description></item>
/// </list>
/// <para>
/// The lock session is opened with pooling off, so closing it ends the session and releases the lock even
/// when the release call fails; a lock can never outlive its test. Acquiring waits up to 300 seconds. The lock
/// changes no data and runs no DDL.
/// </para>
/// <para>
/// Take it once per test. A second acquisition in the same test opens a second session, and an exclusive
/// request then waits on the first until it times out.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// await using var dbLock = await DatabaseWriteLock.AcquireExclusiveAsync();
/// </code>
/// </example>
public sealed class DatabaseWriteLock : IAsyncDisposable
{
    /// <summary>
    /// The application lock resource. It must equal the resource <c>web/e2e/support/sql.ts</c> locks.
    /// </summary>
    public const string ResourceName = "EpiInfo.Web.Tests";

    private const string LockOwner = "Session";
    private const int LockTimeoutMilliseconds = 300_000;

    // Above the lock wait, so the SqlClient default of 30 seconds never aborts a legitimate wait.
    private const int AcquireCommandTimeoutSeconds = 330;
    private const int ReleaseCommandTimeoutSeconds = 30;

    // Names the lock session in sys.dm_exec_sessions.program_name.
    private const string SessionApplicationName = "EpiInfo.Web.Tests lock";

    private SqlConnection? _connection;

    private DatabaseWriteLock(SqlConnection connection, DatabaseLockMode mode)
    {
        _connection = connection;
        Mode = mode;
    }

    /// <summary>The mode this lock was granted in.</summary>
    public DatabaseLockMode Mode { get; }

    /// <summary>
    /// Takes the lock exclusively, waiting up to 300 seconds for every other holder to release it.
    /// </summary>
    /// <param name="cancellationToken">Cancels the connection and the wait.</param>
    /// <returns>The held lock; dispose it to release.</returns>
    /// <exception cref="TestDatabaseUnavailableException">
    /// The database is not configured, SqlClient rejects its connection string, or it is not reachable.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// <c>sp_getapplock</c> did not grant the lock: it timed out, was cancelled, chose this request as a deadlock
    /// victim, or rejected the call.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static Task<DatabaseWriteLock> AcquireExclusiveAsync(CancellationToken cancellationToken = default) =>
        AcquireAsync(DatabaseLockMode.Exclusive, cancellationToken);

    /// <summary>
    /// Takes the lock in shared mode, waiting up to 300 seconds for an exclusive holder to release it.
    /// </summary>
    /// <param name="cancellationToken">Cancels the connection and the wait.</param>
    /// <returns>The held lock; dispose it to release.</returns>
    /// <exception cref="TestDatabaseUnavailableException">
    /// The database is not configured, SqlClient rejects its connection string, or it is not reachable.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// <c>sp_getapplock</c> did not grant the lock: it timed out, was cancelled, chose this request as a deadlock
    /// victim, or rejected the call.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static Task<DatabaseWriteLock> AcquireSharedAsync(CancellationToken cancellationToken = default) =>
        AcquireAsync(DatabaseLockMode.Shared, cancellationToken);

    /// <summary>
    /// Releases the lock on its own session, then closes that session. A second call does nothing.
    /// </summary>
    /// <remarks>
    /// A failed release never throws, so it cannot replace an exception the test raised inside its
    /// <c>await using</c> block. Closing the unpooled session has released the lock by then, so the failure is
    /// only written to <see cref="Trace"/>, and a trace listener that throws is ignored.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        SqlConnection? connection = Interlocked.Exchange(ref _connection, null);
        if (connection is null)
        {
            return;
        }

        string? releaseFailure = null;
        string? closeFailure = null;
        try
        {
            int code = await ReleaseAsync(connection).ConfigureAwait(false);
            if (code < 0)
            {
                releaseFailure = string.Create(CultureInfo.InvariantCulture, $"sp_releaseapplock returned {code}");
            }
        }
        catch (Exception exception)
        {
            // Deliberately swallowed: the release is best effort, and ending the session below releases the lock.
            releaseFailure = TypeName(exception);
        }
        finally
        {
            try
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // Swallowed for the same reason: a throwing DisposeAsync would replace the test's own exception.
                closeFailure = TypeName(exception);
            }
        }

        if (closeFailure is not null)
        {
            string release = releaseFailure is null ? "succeeded" : "also failed (" + releaseFailure + ")";
            TraceBestEffort(
                $"DatabaseWriteLock: closing the session of the {Mode} lock '{ResourceName}' failed ({closeFailure}); " +
                $"the release {release}.");
        }
        else if (releaseFailure is not null)
        {
            TraceBestEffort(
                $"DatabaseWriteLock: releasing the {Mode} lock '{ResourceName}' failed ({releaseFailure}); " +
                "closing its unpooled session released the lock.");
        }
    }

    private static async Task<DatabaseWriteLock> AcquireAsync(DatabaseLockMode mode, CancellationToken cancellationToken)
    {
        SqlConnectionStringBuilder target = TestDatabase.Builder();

        // Pooling must stay off: a pooled connection keeps its session alive after Close, and the session owns
        // the lock, so the lock would outlive the test.
        target.Pooling = false;
        target.ApplicationName = SessionApplicationName;

        // Created before the try: a connection SqlClient rejects was never created, so there is nothing to dispose.
        SqlConnection connection = TestDatabase.CreateConnection(target);
        try
        {
            await OpenAsync(connection, target, cancellationToken).ConfigureAwait(false);

            int code = await RequestAsync(connection, mode, cancellationToken).ConfigureAwait(false);
            if (code is not (0 or 1))
            {
                string codeText = code.ToString(CultureInfo.InvariantCulture);
                throw new InvalidOperationException(
                    $"The {mode} test lock '{ResourceName}' was not granted: sp_getapplock returned {codeText} " +
                    $"({DescribeRequestCode(code)}).");
            }

            return new DatabaseWriteLock(connection, mode);
        }
        catch (Exception failure) when (
            cancellationToken.IsCancellationRequested && failure is not OperationCanceledException)
        {
            // SqlClient reports a cancelled command as a SqlException; the caller asked for cancellation. The failure
            // is named, not attached, because a SqlClient message can quote connection-string content; only a
            // TestDatabaseUnavailableException, already redacted, is kept as the inner exception.
            await TestDatabase.DisposeQuietlyAsync(connection).ConfigureAwait(false);
            throw new OperationCanceledException(
                $"Acquiring the {mode} test lock '{ResourceName}' was cancelled " +
                $"({TestDatabase.DescribeFailure(failure)}).",
                failure as TestDatabaseUnavailableException,
                cancellationToken);
        }
        catch
        {
            await TestDatabase.DisposeQuietlyAsync(connection).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task OpenAsync(
        SqlConnection connection,
        SqlConnectionStringBuilder target,
        CancellationToken cancellationToken)
    {
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (
            !cancellationToken.IsCancellationRequested &&
            failure is SqlException or InvalidOperationException or NotSupportedException)
        {
            // NotSupportedException: SqlClient raises PlatformNotSupportedException for a transport this platform
            // lacks, such as a named pipe or LocalDB on Linux. A failure once cancellation is requested, and any other
            // type, propagates unchanged to AcquireAsync.
            throw TestDatabase.Unreachable(
                $"The test lock '{ResourceName}' cannot reach the rehearsal database", target, failure);
        }
    }

    private static async Task<int> RequestAsync(
        SqlConnection connection,
        DatabaseLockMode mode,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("sys.sp_getapplock", connection)
        {
            CommandType = CommandType.StoredProcedure,
            CommandTimeout = AcquireCommandTimeoutSeconds,
        };
        command.Parameters.Add("@Resource", SqlDbType.NVarChar, 255).Value = ResourceName;
        command.Parameters.Add("@LockMode", SqlDbType.VarChar, 32).Value =
            mode == DatabaseLockMode.Exclusive ? "Exclusive" : "Shared";
        command.Parameters.Add("@LockOwner", SqlDbType.VarChar, 32).Value = LockOwner;
        command.Parameters.Add("@LockTimeout", SqlDbType.Int).Value = LockTimeoutMilliseconds;
        SqlParameter returnValue = command.Parameters.Add("@ReturnValue", SqlDbType.Int);
        returnValue.Direction = ParameterDirection.ReturnValue;

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return ReturnCode(returnValue, "sp_getapplock");
    }

    private static async Task<int> ReleaseAsync(SqlConnection connection)
    {
        await using var command = new SqlCommand("sys.sp_releaseapplock", connection)
        {
            CommandType = CommandType.StoredProcedure,
            CommandTimeout = ReleaseCommandTimeoutSeconds,
        };
        command.Parameters.Add("@Resource", SqlDbType.NVarChar, 255).Value = ResourceName;
        command.Parameters.Add("@LockOwner", SqlDbType.VarChar, 32).Value = LockOwner;
        SqlParameter returnValue = command.Parameters.Add("@ReturnValue", SqlDbType.Int);
        returnValue.Direction = ParameterDirection.ReturnValue;

        _ = await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        return ReturnCode(returnValue, "sp_releaseapplock");
    }

    private static int ReturnCode(SqlParameter returnValue, string procedure) =>
        returnValue.Value is int code
            ? code
            : throw new InvalidOperationException($"{procedure} completed without a return code.");

    private static string TypeName(Exception exception) => exception.GetType().FullName ?? exception.GetType().Name;

    // Trace calls every registered listener; one that throws must not turn a best-effort diagnostic into an
    // exception from DisposeAsync. The message names only the mode, the resource, a return code or a type.
    private static void TraceBestEffort(string message)
    {
        try
        {
            Trace.WriteLine(message);
        }
        catch (Exception)
        {
            // Deliberately swallowed: the diagnostic is optional, and the release and close have already run.
        }
    }

    private static string DescribeRequestCode(int code) => code switch
    {
        -1 => "the request timed out after 300 seconds; another test process or the Playwright smoke test still holds it",
        -2 => "the request was cancelled",
        -3 => "the request was chosen as a deadlock victim",
        -999 => "a parameter validation or other call error",
        _ => "an undocumented return code",
    };
}
