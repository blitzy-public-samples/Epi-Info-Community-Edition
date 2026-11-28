using System.Globalization;
using Microsoft.Data.SqlClient;

namespace EpiInfo.Tests.Shared;

/// <summary>
/// The one place the test suites resolve the rehearsal database (<c>EpiRehearsal</c>, loaded from
/// <c>testdata/</c>) and its connection string. Its TypeScript counterpart,
/// <c>web/e2e/support/connection.ts</c>, resolves the same variables with the same precedence.
/// </summary>
/// <remarks>
/// <para>
/// Resolution: <c>ConnectionStrings__EpiInfo</c> verbatim when it is set; otherwise a string in the form
/// <c>testdata/README.md</c> documents, built from server <c>SQL_HOST</c> (default <c>localhost,1433</c>),
/// database <c>EpiRehearsal</c>, login <c>sa</c> with the <c>MSSQL_SA_PASSWORD</c> password, and
/// <c>TrustServerCertificate</c>. No password is held in code.
/// </para>
/// <para>
/// Tests touch only records they create. They never edit or delete a seeded row, a metadata row or
/// a schema object. <c>testdata/init.sh</c> drops and recreates <c>EpiRehearsal</c>, so it is only
/// ever pointed at the designated rehearsal server. This class never writes and never runs DDL:
/// its only statement is the <c>SELECT DB_NAME()</c> probe.
/// </para>
/// <para>
/// <see cref="ConnectionString"/>, <see cref="Builder"/> and <see cref="OpenConnectionAsync"/>, and so
/// <see cref="DatabaseWriteLock"/> and <see cref="SqlSnapshot"/>, read the environment on every access. Until the probe
/// of <see cref="EnsureReachableAsync"/> succeeds, that is all they do. From then on the probed connection string is
/// the one target of the process: each still resolves the environment, but fails before any connection opens when that
/// resolution differs from the probed one. A test therefore cannot reach a database the probe did not prove, or write
/// to one database while it locks and reads back another, even when a variable changes during the run.
/// <see cref="IsConfigured"/> reads the environment alone, before and after the probe.
/// </para>
/// <para>
/// An unconfigured or unreachable database makes an integration test fail with
/// <see cref="TestDatabaseUnavailableException"/>; integration tests never skip. Messages name the
/// server and database at most, never the password or the connection string, plus the failure's type and
/// SQL error number. The SqlClient exception behind the failure is never attached as the inner exception,
/// because its text can quote the login or other connection-string content and test runners print every
/// inner exception.
/// </para>
/// </remarks>
public static class TestDatabase
{
    /// <summary>Environment variable holding a complete connection string; it wins when set.</summary>
    public const string ConnectionStringVariable = "ConnectionStrings__EpiInfo";

    /// <summary>
    /// Environment variable naming the server, used verbatim as the data source, so it may carry a port
    /// (<c>host,port</c>); a bare host means port 1433.
    /// </summary>
    public const string SqlHostVariable = "SQL_HOST";

    /// <summary>Environment variable holding the <c>sa</c> password of the rehearsal server.</summary>
    public const string PasswordVariable = "MSSQL_SA_PASSWORD";

    /// <summary>The database the fallback connection string names.</summary>
    public const string DatabaseName = "EpiRehearsal";

    /// <summary>The data source used when <see cref="SqlHostVariable"/> is not set.</summary>
    public const string DefaultServer = "localhost,1433";

    /// <summary>
    /// The data field that carries a test marker on both seeded forms (<c>nvarchar(4000)</c> on
    /// <c>CaseReport1</c> and <c>FoodHistory2</c>).
    /// </summary>
    public const string MarkerField = "LastName";

    /// <summary>The length of every marker <see cref="NewMarker"/> returns.</summary>
    public const int MarkerLength = 34;

    /// <summary>The text every marker starts with, which tells a test-created record from a seeded one.</summary>
    public const string MarkerPrefix = "T-";

    private const string Login = "sa";
    private const int ProbeTimeoutSeconds = 5;
    private const string UnreachableSubject = "The rehearsal database is not reachable";

    private const string Guidance =
        "Integration tests need the rehearsal database. Set " + ConnectionStringVariable +
        " to its connection string, or set " + SqlHostVariable + " (default " + DefaultServer + ") and " +
        PasswordVariable + ". See testdata/README.md: start SQL Server 2022 with testdata/docker-compose.yml " +
        "or point " + SqlHostVariable + " at one, load it with testdata/init.sh (which drops and recreates " +
        DatabaseName + ", so run it only against the rehearsal server), and confirm every 99_verify row shows ok = 1.";

    // Names the variables only, never a value: either the old or the new resolution can carry a password.
    private const string ChangedAfterProbe =
        "The rehearsal database settings changed after the probe proved the database reachable: " +
        ConnectionStringVariable + ", " + SqlHostVariable + " and " + PasswordVariable + " now resolve a different " +
        "connection string, so a test could reach a database that the probe, the test lock and the snapshots do not " +
        "cover. Each test process probes once, so keep these variables unchanged for the whole run. " + Guidance;

    // One probe per test process. The task keeps a failure as well, so after one failed probe every later
    // test class fails at once with the same message instead of waiting out the connect timeout again. Its result is
    // the connection string it proved reachable.
    private static readonly Lazy<Task<string>> Probe = new(ProbeAsync, LazyThreadSafetyMode.ExecutionAndPublication);

    // The connection string the probe proved reachable, exactly as resolved, before the probe set its own connect
    // timeout; null until a probe has succeeded. The probe writes it once, just before its task completes successfully,
    // and volatile makes that write visible to every thread that reads it afterwards, without a lock.
    private static volatile string? _probedConnectionString;

    /// <summary>
    /// Whether a database is configured: <see cref="ConnectionStringVariable"/> or
    /// <see cref="PasswordVariable"/> is set and not blank. It never throws, so a host that only needs
    /// an in-memory store can check it without forcing resolution.
    /// </summary>
    public static bool IsConfigured => IsSet(ConnectionStringVariable) || IsSet(PasswordVariable);

    /// <summary>
    /// The resolved connection string, read from the environment on every access. Once the probe of
    /// <see cref="EnsureReachableAsync"/> has succeeded, it is always the probed connection string.
    /// </summary>
    /// <exception cref="TestDatabaseUnavailableException">
    /// Neither variable that configures it is set, or the probe has succeeded and the variables now resolve a
    /// different connection string. Neither message names a value.
    /// </exception>
    public static string ConnectionString => Resolve();

    /// <summary>
    /// A new builder over <see cref="ConnectionString"/> on every call, so a caller can change settings
    /// such as <c>Pooling</c>, <c>ConnectTimeout</c> or <c>ApplicationName</c> without affecting others.
    /// </summary>
    /// <exception cref="TestDatabaseUnavailableException">
    /// The connection string is not configured, the probe has succeeded and the variables now resolve a different
    /// connection string, or <see cref="ConnectionStringVariable"/> does not parse. The parser's exception is not
    /// attached, because its message can quote part of the value.
    /// </exception>
    public static SqlConnectionStringBuilder Builder() => Parse(ConnectionString);

    /// <summary>Opens a connection to the resolved database; the caller disposes it.</summary>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <exception cref="TestDatabaseUnavailableException">
    /// The database is not configured, the probe has succeeded and the variables now resolve a different connection
    /// string (checked before any connection opens), SqlClient rejects its connection string, or the connection cannot
    /// be opened, including over a transport this platform does not support. An open failure's message names the
    /// server, the database, the failure's type and its SQL error number; the SqlClient exception is not attached.
    /// </exception>
    public static Task<SqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default) =>
        OpenAsync(Builder(), cancellationToken);

    /// <summary>
    /// Opens a connection to the resolved database with pooling off; the caller disposes it. Disposing it ends its
    /// session, and with it every lock the session owns, such as an application lock taken with
    /// <c>@LockOwner = 'Session'</c>, as closing <see cref="DatabaseWriteLock"/>'s own session does.
    /// </summary>
    /// <remarks>
    /// A test that takes a session-owned lock opens its session here. A connection from
    /// <see cref="OpenConnectionAsync"/> goes back to SqlClient's pool when disposed, with its session and every lock
    /// that session owns still held, so the lock would outlive the test and its <see cref="DatabaseWriteLock"/>, and
    /// block another test process for as long as the pool keeps that session.
    /// </remarks>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <exception cref="TestDatabaseUnavailableException">As for <see cref="OpenConnectionAsync"/>.</exception>
    public static Task<SqlConnection> OpenUnpooledConnectionAsync(CancellationToken cancellationToken = default)
    {
        SqlConnectionStringBuilder target = Builder();
        target.Pooling = false;
        return OpenAsync(target, cancellationToken);
    }

    /// <summary>
    /// Fails unless the rehearsal database answers <c>SELECT DB_NAME()</c> within five seconds. Every
    /// integration test class awaits it before its first test, for example in
    /// <see cref="IAsyncLifetime.InitializeAsync"/>. The probe runs once per process and its outcome,
    /// success or failure, is shared by every later caller. A successful probe fixes the process's target: from then
    /// on <see cref="ConnectionString"/>, <see cref="Builder"/> and <see cref="OpenConnectionAsync"/> fail when the
    /// variables resolve a different connection string.
    /// </summary>
    /// <exception cref="TestDatabaseUnavailableException">
    /// The database is not configured, SqlClient rejects its connection string, or it is not reachable, refuses the
    /// login or does not answer in time.
    /// </exception>
    public static Task EnsureReachableAsync() => Probe.Value;

    /// <summary>
    /// Awaits the probe of <see cref="EnsureReachableAsync"/> and returns the connection string it proved reachable,
    /// exactly as resolved and without the probe's own connect timeout, so a caller such as the API test host can pin
    /// that target instead of resolving the environment again.
    /// </summary>
    /// <returns>
    /// The probed connection string. It carries the database password, so it is never printed or put in a message.
    /// </returns>
    /// <exception cref="TestDatabaseUnavailableException">
    /// The probe failed, as for <see cref="EnsureReachableAsync"/>.
    /// </exception>
    internal static Task<string> EnsureReachableTargetAsync() => Probe.Value;

    /// <summary>
    /// Returns a new test marker: <c>T-</c> followed by a GUID in <c>N</c> format, exactly
    /// <see cref="MarkerLength"/> characters. Playwright builds the same form from
    /// <c>crypto.randomUUID()</c> with the hyphens removed.
    /// </summary>
    /// <remarks>
    /// A marker goes only into a <c>text</c>-family data field that has no pattern, no <c>MaxLength</c>,
    /// no legal values and a column of at least <see cref="MarkerLength"/> characters. On both seeded forms
    /// that is <see cref="MarkerField"/>, <c>nvarchar(4000)</c> on <c>CaseReport1</c> and
    /// <c>FoodHistory2</c> (<c>testdata/sql/05_data_tables.sql</c>). It never goes into a numeric column
    /// such as <c>FoodHistory2.CaseID</c>, which is <c>float</c>.
    /// </remarks>
    public static string NewMarker() => MarkerPrefix + Guid.NewGuid().ToString("N");

    // The environment's resolution, held to the probed target once a probe has succeeded. After that, settings that no
    // longer resolve at all are a change as well, so the failure reports the change rather than a missing setting.
    private static string Resolve()
    {
        string? connectionString = ResolveEnvironment();
        string? probed = _probedConnectionString;
        if (probed is not null && !string.Equals(connectionString, probed, StringComparison.Ordinal))
        {
            throw new TestDatabaseUnavailableException(ChangedAfterProbe);
        }

        return connectionString ?? throw new TestDatabaseUnavailableException(
            $"Neither {ConnectionStringVariable} nor {PasswordVariable} is set. {Guidance}");
    }

    // The connection string the variables give now, or null when neither variable that configures it is set.
    private static string? ResolveEnvironment()
    {
        string? connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            return connectionString;
        }

        string? password = Environment.GetEnvironmentVariable(PasswordVariable);
        if (string.IsNullOrWhiteSpace(password))
        {
            return null;
        }

        string? host = Environment.GetEnvironmentVariable(SqlHostVariable);
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = string.IsNullOrWhiteSpace(host) ? DefaultServer : host,
            InitialCatalog = DatabaseName,
            UserID = Login,
            Password = password,
            TrustServerCertificate = true,
        };
        return builder.ConnectionString;
    }

    // A builder over a resolved connection string; every failure the parser raises becomes one that quotes nothing.
    private static SqlConnectionStringBuilder Parse(string connectionString)
    {
        try
        {
            return new SqlConnectionStringBuilder(connectionString);
        }
        catch (Exception failure) when (
            failure is ArgumentException or FormatException or OverflowException or NotSupportedException)
        {
            // Every failure the parser raises: a malformed string, an unsupported keyword or an out-of-range value
            // (ArgumentException), a boolean or integer setting that does not convert (FormatException), an integer
            // beyond Int32 (OverflowException), or a keyword this platform does not support (NotSupportedException).
            // Neither the parser's message nor its exception is passed on: an unsupported keyword and a value that
            // does not convert are quoted in it, and that text can be part of a password that holds an unquoted ';'.
            throw new TestDatabaseUnavailableException(
                $"The value of {ConnectionStringVariable} is not a valid SQL Server connection string. {Guidance}");
        }
    }

    private static async Task<SqlConnection> OpenAsync(
        SqlConnectionStringBuilder target,
        CancellationToken cancellationToken)
    {
        // Created before the try: a connection SqlClient rejects was never created, so there is nothing to dispose.
        SqlConnection connection = CreateConnection(target);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch (Exception failure)
        {
            await DisposeQuietlyAsync(connection).ConfigureAwait(false);

            // NotSupportedException: SqlClient raises PlatformNotSupportedException for a transport this platform
            // lacks, such as a named pipe or LocalDB on Linux. An OperationCanceledException or any other type is
            // rethrown unchanged.
            if (failure is SqlException or InvalidOperationException or NotSupportedException)
            {
                throw Unreachable(UnreachableSubject, target, failure);
            }

            throw;
        }
    }

    private static async Task<string> ProbeAsync()
    {
        // Resolved once, so the string recorded below is exactly the one this probe connected with.
        string connectionString = Resolve();
        SqlConnectionStringBuilder target = Parse(connectionString);
        target.ConnectTimeout = ProbeTimeoutSeconds;

        SqlConnection connection = await OpenAsync(target, CancellationToken.None).ConfigureAwait(false);
        var command = new SqlCommand("SELECT DB_NAME();", connection)
        {
            CommandTimeout = ProbeTimeoutSeconds,
        };
        try
        {
            _ = await command.ExecuteScalarAsync().ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            // The probe has failed, so cleanup is best effort: a disposal error must not replace that failure.
            await DisposeQuietlyAsync(command).ConfigureAwait(false);
            await DisposeQuietlyAsync(connection).ConfigureAwait(false);
            if (failure is SqlException or InvalidOperationException)
            {
                throw Unreachable(UnreachableSubject, target, failure);
            }

            throw;
        }

        try
        {
            await command.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        // Only now has the probe succeeded: a failure above leaves the process without a probed target.
        _probedConnectionString = connectionString;
        return connectionString;
    }

    /// <summary>
    /// The one mapping from a failure to open or query the rehearsal database to the failure a test reports,
    /// shared with <see cref="DatabaseWriteLock"/>, which passes its own <paramref name="subject"/>.
    /// </summary>
    /// <remarks>
    /// Names the server and database only: the password, the full connection string and
    /// <c>SqlConnectionStringBuilder.ToString()</c> never reach a message.
    /// </remarks>
    /// <param name="subject">The start of the message, naming what cannot reach the database.</param>
    /// <param name="target">The settings of the failed connection; only its server and database are named.</param>
    /// <param name="failure">The failure; only its type and SQL error number are named.</param>
    /// <returns>The exception to throw, carrying the setup guidance and no inner exception.</returns>
    internal static TestDatabaseUnavailableException Unreachable(
        string subject,
        SqlConnectionStringBuilder target,
        Exception failure)
    {
        string server = string.IsNullOrWhiteSpace(target.DataSource) ? "(not set)" : target.DataSource;
        string database = string.IsNullOrWhiteSpace(target.InitialCatalog) ? "(login default)" : target.InitialCatalog;

        // The failure is not attached as the inner exception: a SqlClient message can quote the login, such as
        // "Login failed for user '...'", or other connection-string content, and test runners print every inner
        // exception of a failed test.
        return new TestDatabaseUnavailableException(
            $"{subject}: server '{server}', database '{database}' " +
            $"(connection string {DescribeSource()}) failed with {DescribeFailure(failure)}. {Guidance}");
    }

    /// <summary>
    /// The one place a test connection is created, shared with <see cref="DatabaseWriteLock"/>. SqlClient checks
    /// some settings only here, after <see cref="Builder"/> has accepted the string: an unknown
    /// <c>Type System Version</c> or <c>Transaction Binding</c>, a value over its 128-character limit such as a long
    /// <c>Application Name</c> or password, a <c>Min Pool Size</c> above <c>Max Pool Size</c>, or an
    /// <c>Authentication</c> setting that conflicts with another.
    /// </summary>
    /// <param name="target">The settings to connect with.</param>
    /// <returns>The connection, not yet open; the caller disposes it.</returns>
    /// <exception cref="TestDatabaseUnavailableException">
    /// SqlClient rejects the settings. The message names where the connection string came from and carries the
    /// setup guidance; it names no setting's value, and SqlClient's exception is not attached.
    /// </exception>
    internal static SqlConnection CreateConnection(SqlConnectionStringBuilder target)
    {
        try
        {
            return new SqlConnection(target.ConnectionString);
        }
        catch (Exception failure) when (failure is ArgumentException or NotSupportedException)
        {
            // Neither SqlClient's message nor its exception is passed on: either can quote connection-string content,
            // and test runners print every inner exception.
            throw new TestDatabaseUnavailableException(
                $"The connection string {DescribeSource()} is not a valid SQL Server connection string. {Guidance}");
        }
    }

    /// <summary>
    /// Names a failure by its type and, for a <see cref="SqlException"/>, its SQL error number, never by its
    /// message, which can quote connection-string content.
    /// </summary>
    /// <param name="failure">The failure to name.</param>
    /// <returns>For example <c>SqlException (error number 18456)</c>.</returns>
    internal static string DescribeFailure(Exception failure) =>
        failure is SqlException sqlException
            ? string.Create(CultureInfo.InvariantCulture, $"{failure.GetType().Name} (error number {sqlException.Number})")
            : failure.GetType().Name;

    /// <summary>
    /// Disposes a connection or command on a failure path without throwing, so a disposal error cannot replace
    /// the failure or cancellation the caller goes on to report.
    /// </summary>
    /// <param name="resource">The connection or command to dispose.</param>
    /// <returns>A task that completes once disposal has finished or failed.</returns>
    internal static async ValueTask DisposeQuietlyAsync(IAsyncDisposable resource)
    {
        try
        {
            await resource.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Deliberately swallowed: the caller rethrows the failure that led here, which this error would replace.
        }
    }

    // Names where the connection string came from, never its value.
    private static string DescribeSource() =>
        IsSet(ConnectionStringVariable)
            ? $"from {ConnectionStringVariable}"
            : $"built from {SqlHostVariable} and {PasswordVariable}";

    private static bool IsSet(string variable) =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable));
}

/// <summary>
/// The rehearsal database is not configured or cannot be reached. Integration tests let it propagate,
/// so they fail with its message rather than skip.
/// </summary>
public sealed class TestDatabaseUnavailableException : Exception
{
    /// <summary>Creates the exception with its message.</summary>
    /// <param name="message">What is missing or failed, and how to provide the database.</param>
    public TestDatabaseUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with its message and the failure that caused it.</summary>
    /// <param name="message">What is missing or failed, and how to provide the database.</param>
    /// <param name="inner">
    /// The original failure. Pass only an exception whose text carries no connection-string content: test runners
    /// print every inner exception, so a SqlClient or connection-string parser exception is never passed here.
    /// </param>
    public TestDatabaseUnavailableException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
