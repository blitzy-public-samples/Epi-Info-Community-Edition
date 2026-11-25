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

    private const string MarkerPrefix = "T-";
    private const string Login = "sa";
    private const int ProbeTimeoutSeconds = 5;
    private const string UnreachableSubject = "The rehearsal database is not reachable";

    private const string Guidance =
        "Integration tests need the rehearsal database. Set " + ConnectionStringVariable +
        " to its connection string, or set " + SqlHostVariable + " (default " + DefaultServer + ") and " +
        PasswordVariable + ". See testdata/README.md: start SQL Server 2022 with testdata/docker-compose.yml " +
        "or point " + SqlHostVariable + " at one, load it with testdata/init.sh (which drops and recreates " +
        DatabaseName + ", so run it only against the rehearsal server), and confirm every 99_verify row shows ok = 1.";

    // One probe per test process. The task keeps a failure as well, so after one failed probe every later
    // test class fails at once with the same message instead of waiting out the connect timeout again.
    private static readonly Lazy<Task> Probe = new(ProbeAsync, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Whether a database is configured: <see cref="ConnectionStringVariable"/> or
    /// <see cref="PasswordVariable"/> is set and not blank. It never throws, so a host that only needs
    /// an in-memory store can check it without forcing resolution.
    /// </summary>
    public static bool IsConfigured => IsSet(ConnectionStringVariable) || IsSet(PasswordVariable);

    /// <summary>
    /// The resolved connection string, read from the environment on every access.
    /// </summary>
    /// <exception cref="TestDatabaseUnavailableException">Neither variable that configures it is set.</exception>
    public static string ConnectionString => Resolve();

    /// <summary>
    /// A new builder over <see cref="ConnectionString"/> on every call, so a caller can change settings
    /// such as <c>Pooling</c>, <c>ConnectTimeout</c> or <c>ApplicationName</c> without affecting others.
    /// </summary>
    /// <exception cref="TestDatabaseUnavailableException">
    /// The connection string is not configured, or <see cref="ConnectionStringVariable"/> does not parse. The
    /// parser's exception is not attached, because its message can quote part of the value.
    /// </exception>
    public static SqlConnectionStringBuilder Builder()
    {
        string connectionString = ConnectionString;
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

    /// <summary>Opens a connection to the resolved database; the caller disposes it.</summary>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <exception cref="TestDatabaseUnavailableException">
    /// The database is not configured, SqlClient rejects its connection string, or the connection cannot be opened,
    /// including over a transport this platform does not support. An open failure's message names the server, the
    /// database, the failure's type and its SQL error number; the SqlClient exception is not attached.
    /// </exception>
    public static Task<SqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default) =>
        OpenAsync(Builder(), cancellationToken);

    /// <summary>
    /// Fails unless the rehearsal database answers <c>SELECT DB_NAME()</c> within five seconds. Every
    /// integration test class awaits it before its first test, for example in
    /// <see cref="IAsyncLifetime.InitializeAsync"/>. The probe runs once per process and its outcome,
    /// success or failure, is shared by every later caller.
    /// </summary>
    /// <exception cref="TestDatabaseUnavailableException">
    /// The database is not configured, SqlClient rejects its connection string, or it is not reachable, refuses the
    /// login or does not answer in time.
    /// </exception>
    public static Task EnsureReachableAsync() => Probe.Value;

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

    private static string Resolve()
    {
        string? connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            return connectionString;
        }

        string? password = Environment.GetEnvironmentVariable(PasswordVariable);
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new TestDatabaseUnavailableException(
                $"Neither {ConnectionStringVariable} nor {PasswordVariable} is set. {Guidance}");
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

    private static async Task ProbeAsync()
    {
        SqlConnectionStringBuilder target = Builder();
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
