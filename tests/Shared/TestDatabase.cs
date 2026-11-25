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
/// server and database at most, never the password or the connection string.
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
    /// The connection string is not configured, or <see cref="ConnectionStringVariable"/> does not parse.
    /// </exception>
    public static SqlConnectionStringBuilder Builder()
    {
        string connectionString = ConnectionString;
        try
        {
            return new SqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException failure)
        {
            // The parser's message names a keyword or an index, never a value; the value is not repeated.
            throw new TestDatabaseUnavailableException(
                $"The value of {ConnectionStringVariable} is not a valid SQL Server connection string. {Guidance}",
                failure);
        }
    }

    /// <summary>Opens a connection to the resolved database; the caller disposes it.</summary>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <exception cref="TestDatabaseUnavailableException">
    /// The database is not configured, or the connection cannot be opened; the original failure is the
    /// inner exception.
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
    /// The database is not configured, not reachable, refuses the login or does not answer in time.
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
        var connection = new SqlConnection(target.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch (Exception failure)
        {
            connection.Dispose();
            if (failure is SqlException or InvalidOperationException)
            {
                throw Unreachable(target, failure);
            }

            throw;
        }
    }

    private static async Task ProbeAsync()
    {
        SqlConnectionStringBuilder target = Builder();
        target.ConnectTimeout = ProbeTimeoutSeconds;

        await using SqlConnection connection = await OpenAsync(target, CancellationToken.None).ConfigureAwait(false);
        await using var command = new SqlCommand("SELECT DB_NAME();", connection)
        {
            CommandTimeout = ProbeTimeoutSeconds,
        };
        try
        {
            _ = await command.ExecuteScalarAsync().ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is SqlException or InvalidOperationException)
        {
            throw Unreachable(target, failure);
        }
    }

    // Names the server and database only: the password, the full connection string and
    // SqlConnectionStringBuilder.ToString() never reach a message.
    private static TestDatabaseUnavailableException Unreachable(SqlConnectionStringBuilder target, Exception failure)
    {
        string server = string.IsNullOrWhiteSpace(target.DataSource) ? "(not set)" : target.DataSource;
        string database = string.IsNullOrWhiteSpace(target.InitialCatalog) ? "(login default)" : target.InitialCatalog;
        string source = IsSet(ConnectionStringVariable)
            ? $"from {ConnectionStringVariable}"
            : $"built from {SqlHostVariable} and {PasswordVariable}";
        string error = failure is SqlException sqlException
            ? string.Create(CultureInfo.InvariantCulture, $"{failure.GetType().Name} (error number {sqlException.Number})")
            : failure.GetType().Name;

        return new TestDatabaseUnavailableException(
            $"The rehearsal database is not reachable: server '{server}', database '{database}' " +
            $"(connection string {source}) failed with {error}. {Guidance}",
            failure);
    }

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
    /// <param name="inner">The original failure.</param>
    public TestDatabaseUnavailableException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
