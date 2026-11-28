using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using EpiInfo.Api.Hosting;
using EpiInfo.CheckCode;
using EpiInfo.Data.Sql;
using EpiInfo.Domain.Storage;
using EpiInfo.Tests.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Xunit.Sdk;

namespace EpiInfo.Api.Tests.Infrastructure;

/// <summary>
/// Where an <see cref="ApiFactoryBase"/> host takes its <c>ConnectionStrings:EpiInfo</c> setting from.
/// </summary>
/// <remarks>
/// A database host, such as one from <see cref="ApiFactory.ForDatabaseAsync"/>, accepts only
/// <see cref="Default"/> and always connects with the connection string <see cref="TestDatabase"/> resolved when its
/// probe succeeded. <see cref="Supplied"/>, <see cref="None"/> and an <see cref="ApiFactoryOptions.ExtraSettings"/>
/// connection override are for hosts created with a constructor, such as the negative connectivity tests.
/// </remarks>
public enum ApiConnectionMode
{
    /// <summary>
    /// The rehearsal database resolved by <see cref="TestDatabase.ConnectionString"/>. With a
    /// <see cref="ApiFactoryOptions.DataStore"/>, an empty value instead: nothing connects, because that store replaces
    /// the SQL Server one, so the only effect is the API's startup warning that the key is not set. The empty value
    /// also overrides any <c>ConnectionStrings__EpiInfo</c> in the process environment.
    /// </summary>
    Default,

    /// <summary><see cref="ApiFactoryOptions.ConnectionString"/>, verbatim.</summary>
    Supplied,

    /// <summary>
    /// An empty value. The API reads a blank connection string as absent and answers database-backed endpoints and
    /// <c>/health</c> with 503; the empty value also overrides any <c>ConnectionStrings__EpiInfo</c> in the process
    /// environment.
    /// </summary>
    None,
}

/// <summary>How an <see cref="ApiFactoryBase"/> configures the API host it starts.</summary>
/// <remarks>
/// The generated <see cref="ToString"/> never prints <see cref="ConnectionString"/> or the values of
/// <see cref="ExtraSettings"/>: a connection string carries a password, and options can reach test output as theory
/// data or in assertion messages.
/// </remarks>
public sealed record ApiFactoryOptions
{
    /// <summary>The source of the connection string; <see cref="ApiConnectionMode.Default"/> unless set.</summary>
    public ApiConnectionMode ConnectionMode { get; init; } = ApiConnectionMode.Default;

    /// <summary>
    /// The connection string for <see cref="ApiConnectionMode.Supplied"/>, where it is required, for example an
    /// unreachable <c>Server=127.0.0.1,1</c> carrying a password sentinel. It must be <see langword="null"/> in the
    /// other modes.
    /// </summary>
    public string? ConnectionString { get; init; }

    /// <summary>
    /// The <c>Database:CommandTimeoutSeconds</c> value, for example 2 to make a blocked insert time out; the
    /// committed <c>appsettings.json</c> value applies when <see langword="null"/>.
    /// </summary>
    public int? CommandTimeoutSeconds { get; init; }

    /// <summary>
    /// The hosting environment, <c>Production</c> unless set; the redaction checks also use <c>Development</c>.
    /// </summary>
    public string EnvironmentName { get; init; } = "Production";

    /// <summary>
    /// The in-memory store that replaces the SQL Server <see cref="IDataStore"/>, or <see langword="null"/>.
    /// </summary>
    public FakeDataStore? DataStore { get; init; }

    /// <summary>
    /// Further host settings, applied after the connection string and timeout so they can override any setting, for
    /// example <c>urls</c> for the binding guard cases.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The factory copies the entries when it validates the options, so a later change to the dictionary passed here
    /// changes nothing. Configuration keys are case-insensitive, so two keys that differ only in case are rejected.
    /// </para>
    /// <para>
    /// A host created with a constructor lets a <c>ConnectionStrings:EpiInfo</c> entry replace the connection string
    /// its <see cref="ConnectionMode"/> gives. A database host, such as one from
    /// <see cref="ApiFactory.ForDatabaseAsync"/>, rejects an entry that addresses the connection string, in any casing
    /// and in the <c>ConnectionStrings__EpiInfo</c> environment form, because its writes must reach the database
    /// <see cref="TestDatabase"/> probes, locks and reads back.
    /// </para>
    /// </remarks>
    public IReadOnlyDictionary<string, string?> ExtraSettings { get; init; } = new Dictionary<string, string?>();

    /// <summary>Options for a host whose data store is <paramref name="store"/>; no SQL Server is needed.</summary>
    /// <param name="store">The in-memory store the host uses.</param>
    /// <param name="environment">The hosting environment.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is <see langword="null"/>.</exception>
    public static ApiFactoryOptions ForFake(FakeDataStore store, string environment = "Production")
    {
        ArgumentNullException.ThrowIfNull(store);
        return new ApiFactoryOptions { DataStore = store, EnvironmentName = environment };
    }

    // Replaces the generated member list so a connection string, and any secret passed as an extra setting, never
    // reaches test output. Extra settings are listed by key only.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append(
            CultureInfo.InvariantCulture,
            $"ConnectionMode = {ConnectionMode}, " +
            $"HasConnectionString = {!string.IsNullOrWhiteSpace(ConnectionString)}, ");
        builder.Append(
            CultureInfo.InvariantCulture,
            $"CommandTimeoutSeconds = {CommandTimeoutSeconds?.ToString(CultureInfo.InvariantCulture) ?? "default"}, ");
        builder.Append(CultureInfo.InvariantCulture, $"EnvironmentName = {EnvironmentName}, ");
        builder.Append(CultureInfo.InvariantCulture, $"HasDataStore = {DataStore is not null}, ");
        builder.Append("ExtraSettings = [").Append(string.Join(", ", ExtraSettings?.Keys ?? [])).Append(']');
        return true;
    }
}

/// <summary>
/// The shared host of the API tests: <c>src/EpiInfo.Api/Program.cs</c> run through
/// <see cref="WebApplicationFactory{TEntryPoint}"/>, with exactly the registrations the tests observe replaced by the
/// doubles this factory exposes.
/// </summary>
/// <remarks>
/// <para>
/// Replaced: <see cref="ICheckCodeEngine"/> by <see cref="Engine"/>, which wraps the real
/// <see cref="CheckCodeEngine"/>; <see cref="ISqlCommandObserver"/> by <see cref="Recorder"/>;
/// <see cref="TimeProvider"/> by <see cref="Clock"/>; and, when <see cref="ApiFactoryOptions.DataStore"/> is set,
/// <see cref="IDataStore"/> by that store. <see cref="Logs"/> is added as one more logger provider. Everything else,
/// <see cref="ApiOptions"/> and the data-store options included, is built from configuration exactly as in
/// production.
/// </para>
/// <para>
/// The doubles are created once per factory, before the host is built, so a test may arrange or clear them before its
/// first request. Disposing the factory stops the host; another factory in the same process starts a fresh one.
/// </para>
/// </remarks>
public abstract class ApiFactoryBase : WebApplicationFactory<Program>
{
    // The connection string a database host was pinned to when its probe succeeded; null for a host created from
    // options alone. Never printed and never exposed: it carries the database password.
    private readonly string? _databaseConnectionString;

    /// <summary>Validates <paramref name="options"/> and creates the doubles.</summary>
    /// <param name="options">The host configuration; the defaults when <see langword="null"/>.</param>
    /// <exception cref="ArgumentException">The options are inconsistent; the message names the member.</exception>
    protected ApiFactoryBase(ApiFactoryOptions? options)
        : this(Validate(options ?? new ApiFactoryOptions()), databaseConnectionString: null)
    {
    }

    /// <summary>
    /// Creates a database host from <paramref name="host"/>: the options its preparation validated, and the connection
    /// string pinned when its probe succeeded, which no setting can replace.
    /// </summary>
    /// <param name="host">The prepared host, from <see cref="DatabaseHost.PrepareAsync"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="host"/> is <see langword="null"/>.</exception>
    protected ApiFactoryBase(DatabaseHost host)
        : this((host ?? throw new ArgumentNullException(nameof(host))).Options, host.ConnectionString)
    {
    }

    private ApiFactoryBase(ApiFactoryOptions validatedOptions, string? databaseConnectionString)
    {
        Options = validatedOptions;
        _databaseConnectionString = databaseConnectionString;
        Recorder = new SqlCommandRecorder();
        Clock = new ControlledTimeProvider();
        Logs = new CapturingLoggerProvider();
        Engine = new CountingCheckCodeEngine(new CheckCodeEngine());
    }

    /// <summary>The validated options the host is built with.</summary>
    public ApiFactoryOptions Options { get; }

    /// <summary>Every SQL command the host's data store executes.</summary>
    public SqlCommandRecorder Recorder { get; }

    /// <summary>The clock the save stamps are read from; set it before each write.</summary>
    public ControlledTimeProvider Clock { get; }

    /// <summary>Every log entry the committed <c>Logging:LogLevel</c> filters let through.</summary>
    public CapturingLoggerProvider Logs { get; }

    /// <summary>The check-code engine the host uses, counting its calls.</summary>
    public CountingCheckCodeEngine Engine { get; }

    /// <summary>The in-memory store the host uses, or <see langword="null"/> for the SQL Server store.</summary>
    public FakeDataStore? DataStore => Options.DataStore;

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        base.ConfigureWebHost(builder);

        // The content root is the API project directory, so the host loads the committed appsettings.json (the binding,
        // the body limit and the Logging:LogLevel filters) and the committed file of the selected environment,
        // Options.EnvironmentName (Production unless a test sets it): appsettings.Development.json under Development;
        // no Production file is committed. The test settings applied below take precedence over both.
        builder.UseContentRoot(RepositoryPaths.ApiProjectDirectory);
        builder.UseEnvironment(Options.EnvironmentName);

        // Mvc.Testing 10.0.15 passes these values to Program.cs as --key=value command-line arguments, so
        // builder.Configuration holds them before Build(), not only afterwards: an extra "urls" setting already fails
        // the guard's pre-build check. The command line is the last configuration source, so they outrank
        // appsettings*.json and environment variables for every read: ApiOptions at its first resolution, Kestrel's
        // endpoints at start, and the guard's checks. A null value arrives as an empty string. A test asserts on the
        // effect of a setting, never on when Program.cs first reads it.
        foreach ((string key, string? value) in ResolveSettings())
        {
            builder.UseSetting(key, value);
        }

        // ConfigureTestServices runs after Program.cs registers its services, so each RemoveAll drops the production
        // registration. The logger provider is added, never substituted: the JSON console stays, and no filter or level
        // changes, so Logs sees exactly what production writes.
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ICheckCodeEngine>();
            services.AddSingleton<ICheckCodeEngine>(Engine);

            services.RemoveAll<ISqlCommandObserver>();
            services.AddSingleton<ISqlCommandObserver>(Recorder);

            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);

            if (Options.DataStore is { } store)
            {
                services.RemoveAll<IDataStore>();
                services.AddSingleton<IDataStore>(store);
            }

            services.TryAddEnumerable(ServiceDescriptor.Singleton<ILoggerProvider>(Logs));
        });

        ConfigureHost(builder);
    }

    /// <summary>
    /// Runs last in <see cref="ConfigureWebHost"/>, so a derived host, such as the real-Kestrel factory, adds its own
    /// settings on top of everything this class configures.
    /// </summary>
    /// <param name="builder">The web host builder.</param>
    protected virtual void ConfigureHost(IWebHostBuilder builder)
    {
    }

    /// <summary>
    /// The validated options of a database host and the connection string it is pinned to: the one the
    /// <see cref="TestDatabase"/> probe proved reachable. Only <see cref="PrepareAsync"/> creates one, so every
    /// database host writes to the database that the probe, <see cref="DatabaseWriteLock"/> and
    /// <see cref="SqlSnapshot"/> reach. If the variables change after the probe, preparing a host, taking the lock and
    /// taking a snapshot all fail instead of reaching another database.
    /// </summary>
    /// <remarks>
    /// A derived factory's <c>ForDatabaseAsync</c> awaits <see cref="PrepareAsync"/> and passes the result to the
    /// <see cref="ApiFactoryBase(DatabaseHost)"/> constructor. <see cref="ToString"/> never prints the connection
    /// string.
    /// </remarks>
    protected sealed class DatabaseHost
    {
        private DatabaseHost(ApiFactoryOptions options, string connectionString)
        {
            Options = options;
            ConnectionString = connectionString;
        }

        /// <summary>The validated options, holding the copy of the extra settings taken at validation.</summary>
        internal ApiFactoryOptions Options { get; }

        // The pinned connection string; it carries the database password.
        internal string ConnectionString { get; }

        /// <summary>
        /// Validates <paramref name="options"/> for a database host, then proves the rehearsal database reachable and
        /// pins the connection string the <see cref="TestDatabase"/> probe proved, after checking that the variables
        /// still resolve it.
        /// </summary>
        /// <param name="options">
        /// The host configuration, in <see cref="ApiConnectionMode.Default"/> mode, without a data store and without
        /// an extra setting that addresses the connection string; the defaults when <see langword="null"/>.
        /// </param>
        /// <returns>The prepared host.</returns>
        /// <exception cref="ArgumentException">
        /// The options are inconsistent, set a data store or another connection mode, or carry an extra setting that
        /// addresses <c>ConnectionStrings:EpiInfo</c>. It is thrown before the probe, and its message names the member,
        /// never a value.
        /// </exception>
        /// <exception cref="TestDatabaseUnavailableException">
        /// The database is not configured or cannot be reached, or its variables changed after the process's probe
        /// succeeded. It is thrown before the host exists, so nothing is written.
        /// </exception>
        public static async Task<DatabaseHost> PrepareAsync(ApiFactoryOptions? options)
        {
            ApiFactoryOptions validated = ValidateDatabaseHost(Validate(options ?? new ApiFactoryOptions()));
            string probed = await TestDatabase.EnsureReachableTargetAsync().ConfigureAwait(false);

            // The probe runs once per process, so its target can predate a change to the variables. Resolving them
            // again throws on such a change, so the factory fails here, before any write, rather than pin a target
            // that DatabaseWriteLock and SqlSnapshot no longer resolve.
            _ = TestDatabase.ConnectionString;
            return new DatabaseHost(validated, probed);
        }

        /// <inheritdoc />
        public override string ToString() => $"{nameof(DatabaseHost)} {{ {nameof(Options)} = {Options} }}";
    }

    // A database host connects only with the connection its probe proved: the SQL Server store, the Default mode, and
    // no extra setting that would replace that connection.
    private static ApiFactoryOptions ValidateDatabaseHost(ApiFactoryOptions options)
    {
        if (options.DataStore is not null)
        {
            throw new ArgumentException(
                $"A database host uses the SQL Server store; create a {nameof(FakeDataStore)} host with the " +
                "constructor.",
                nameof(options));
        }

        if (options.ConnectionMode != ApiConnectionMode.Default)
        {
            throw new ArgumentException(
                $"A database host connects through {nameof(TestDatabase)}, so its connection mode must be " +
                $"{nameof(ApiConnectionMode)}.{nameof(ApiConnectionMode.Default)}.",
                nameof(options));
        }

        if (options.ExtraSettings.Keys.Any(AddressesConnectionString))
        {
            throw new ArgumentException(
                $"A database host connects through {nameof(TestDatabase)}, so " +
                $"{nameof(ApiFactoryOptions.ExtraSettings)} must not set {ApiOptions.ConnectionStringKey}; create a " +
                "host with another connection with the constructor.",
                nameof(options));
        }

        return options;
    }

    // Whether a setting key names the connection string: configuration compares keys case-insensitively, and the
    // environment-variable form writes the ':' section delimiter as "__".
    private static bool AddressesConnectionString(string key) =>
        string.Equals(
            key.Trim().Replace("__", ConfigurationPath.KeyDelimiter, StringComparison.Ordinal),
            ApiOptions.ConnectionStringKey,
            StringComparison.OrdinalIgnoreCase);

    private static ApiFactoryOptions Validate(ApiFactoryOptions options)
    {
        if (!Enum.IsDefined(options.ConnectionMode))
        {
            throw new ArgumentException(
                $"{nameof(ApiFactoryOptions.ConnectionMode)} is not a defined {nameof(ApiConnectionMode)} value.",
                nameof(options));
        }

        if (options.ConnectionMode == ApiConnectionMode.Supplied && string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            throw new ArgumentException(
                $"{nameof(ApiConnectionMode)}.{nameof(ApiConnectionMode.Supplied)} requires a non-blank " +
                $"{nameof(ApiFactoryOptions.ConnectionString)}.",
                nameof(options));
        }

        // A connection string the chosen mode ignores would silently test a different database than the caller meant.
        if (options.ConnectionMode != ApiConnectionMode.Supplied && options.ConnectionString is not null)
        {
            throw new ArgumentException(
                $"{nameof(ApiFactoryOptions.ConnectionString)} is used only with " +
                $"{nameof(ApiConnectionMode)}.{nameof(ApiConnectionMode.Supplied)}.",
                nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.EnvironmentName))
        {
            throw new ArgumentException(
                $"{nameof(ApiFactoryOptions.EnvironmentName)} must be a non-blank environment name.",
                nameof(options));
        }

        if (options.ExtraSettings is null)
        {
            throw new ArgumentException(
                $"{nameof(ApiFactoryOptions.ExtraSettings)} must not be null; leave it unset for no extra settings.",
                nameof(options));
        }

        if (options.ExtraSettings.Keys.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                $"{nameof(ApiFactoryOptions.ExtraSettings)} must not hold a blank key.",
                nameof(options));
        }

        var extraSettings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, string? value) in options.ExtraSettings)
        {
            // Configuration keys are case-insensitive, so two such keys would set one value, whichever came last.
            if (!extraSettings.TryAdd(key, value))
            {
                throw new ArgumentException(
                    $"{nameof(ApiFactoryOptions.ExtraSettings)} must not hold two keys that differ only in case; " +
                    "configuration keys are case-insensitive.",
                    nameof(options));
            }
        }

        // The host reads the settings when it is built, so it gets a copy taken now that the caller cannot reach: a
        // later change to the caller's dictionary cannot bypass these checks.
        return options with { ExtraSettings = extraSettings.AsReadOnly() };
    }

    // Configuration keys compare case-insensitively, so an extra setting replaces a computed one whatever its casing.
    private Dictionary<string, string?> ResolveSettings()
    {
        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        // A database host uses only the connection pinned when its probe succeeded, applied last below. On a host
        // created from options alone, an extra setting for the connection string wins, for the negative connectivity
        // tests, so the test database is not resolved, and cannot fail for a missing password, when one is given.
        bool connectionStringOverridden = Options.ExtraSettings.Keys.Any(
            key => string.Equals(key, ApiOptions.ConnectionStringKey, StringComparison.OrdinalIgnoreCase));
        if (_databaseConnectionString is null && !connectionStringOverridden)
        {
            settings[ApiOptions.ConnectionStringKey] = ResolveConnectionString();
        }

        if (Options.CommandTimeoutSeconds is int seconds)
        {
            settings[ApiOptions.CommandTimeoutKey] = seconds.ToString(CultureInfo.InvariantCulture);
        }

        foreach ((string key, string? value) in Options.ExtraSettings)
        {
            settings[key] = value;
        }

        // After the extra settings, so none can replace it; preparing the host also rejected every one that addresses
        // the connection string.
        if (_databaseConnectionString is not null)
        {
            settings[ApiOptions.ConnectionStringKey] = _databaseConnectionString;
        }

        return settings;
    }

    // Read at host build, never earlier: TestDatabase is the only connection resolution of the tests, and a factory
    // that is constructed but never started must not require the database variables.
    private string ResolveConnectionString() => Options.ConnectionMode switch
    {
        ApiConnectionMode.None => string.Empty,
        ApiConnectionMode.Supplied => Options.ConnectionString
            ?? throw new InvalidOperationException("A supplied connection string was validated as present."),
        _ when Options.DataStore is not null => string.Empty,
        _ => TestDatabase.ConnectionString,
    };
}

/// <summary>
/// The in-process API host on TestServer, the default for API tests. Kestrel limits do not apply on TestServer, which
/// is why the API carries its own request body limit middleware.
/// </summary>
/// <remarks>
/// Create it per test or in <c>IAsyncLifetime</c>, and dispose it when done; <see cref="ForDatabaseAsync"/> is the way
/// to start one against the rehearsal database. It cannot be an xUnit class fixture: xUnit supplies no value for the
/// options parameter, optional or not, and reports it as an unresolved constructor argument.
/// </remarks>
public sealed class ApiFactory : ApiFactoryBase
{
    /// <summary>Creates the factory; the host starts on the first client or <c>Services</c> access.</summary>
    /// <param name="options">The host configuration; the defaults when <see langword="null"/>.</param>
    /// <exception cref="ArgumentException">The options are inconsistent.</exception>
    public ApiFactory(ApiFactoryOptions? options = null)
        : base(options)
    {
    }

    private ApiFactory(DatabaseHost host)
        : base(host)
    {
    }

    /// <summary>
    /// Creates a factory whose host uses the SQL Server store and the rehearsal database, after proving that database
    /// reachable. The host connects with the connection string the <see cref="TestDatabase"/> probe proved reachable,
    /// the database <see cref="DatabaseWriteLock"/> locks and <see cref="SqlSnapshot"/> reads, and no setting can
    /// replace it.
    /// </summary>
    /// <param name="options">
    /// The host configuration, in <see cref="ApiConnectionMode.Default"/> mode, without a data store and without an
    /// <see cref="ApiFactoryOptions.ExtraSettings"/> entry that addresses <c>ConnectionStrings:EpiInfo</c>, in any
    /// casing or in its <c>ConnectionStrings__EpiInfo</c> form; the defaults when <see langword="null"/>. A host with
    /// another connection is created with the constructor.
    /// </param>
    /// <returns>The factory, not yet started.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="options"/> is inconsistent, sets a data store or another connection mode, or sets the
    /// connection string through <see cref="ApiFactoryOptions.ExtraSettings"/>. It is thrown before the probe.
    /// </exception>
    /// <exception cref="TestDatabaseUnavailableException">
    /// The database is not configured or cannot be reached, or its variables changed after the process's probe
    /// succeeded. Integration tests let it fail the test, naming <c>SQL_HOST</c>, <c>MSSQL_SA_PASSWORD</c> and
    /// <c>ConnectionStrings__EpiInfo</c>; they never skip.
    /// </exception>
    public static async Task<ApiFactory> ForDatabaseAsync(ApiFactoryOptions? options = null)
    {
        DatabaseHost host = await DatabaseHost.PrepareAsync(options).ConfigureAwait(false);
        return new ApiFactory(host);
    }
}

/// <summary>
/// Read-only paths in the repository checkout, found from the test assembly's location. Tests read these files where
/// they lie and never copy or write them.
/// </summary>
public static class RepositoryPaths
{
    /// <summary>The file that marks the repository root.</summary>
    public const string SolutionFileName = "EpiInfo.Web.sln";

    private static readonly Lazy<string> LazyRoot = new(FindRoot, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// The repository root: the nearest directory at or above the test assembly that holds the solution.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// No directory at or above the test assembly holds the solution.
    /// </exception>
    public static string Root => LazyRoot.Value;

    /// <summary>The API project directory, <c>src/EpiInfo.Api</c>, the content root of every test host.</summary>
    /// <exception cref="InvalidOperationException">The directory does not exist.</exception>
    public static string ApiProjectDirectory
    {
        get
        {
            string path = Combine("src", "EpiInfo.Api");
            if (!Directory.Exists(path))
            {
                throw new InvalidOperationException(
                    $"The API project directory '{path}' does not exist under the repository root '{Root}'.");
            }

            return path;
        }
    }

    /// <summary>The check-code scenario catalogue, <c>testdata/scenarios/check_code_scenarios.json</c>.</summary>
    public static string ScenarioFile => Combine("testdata", "scenarios", "check_code_scenarios.json");

    /// <summary>The parity ledger, <c>docs/parity-notes.md</c>.</summary>
    public static string ParityNotes => Combine("docs", "parity-notes.md");

    /// <summary>The absolute path of a location given relative to the repository root.</summary>
    /// <param name="relative">The path segments below the root, for example <c>"src"</c>.</param>
    /// <returns>The normalised absolute path, which need not exist.</returns>
    /// <exception cref="ArgumentException">
    /// A segment is null, blank or rooted, or the result lies outside the repository root.
    /// </exception>
    public static string Combine(params string[] relative)
    {
        ArgumentNullException.ThrowIfNull(relative);
        foreach (string segment in relative)
        {
            if (string.IsNullOrWhiteSpace(segment) || Path.IsPathRooted(segment))
            {
                throw new ArgumentException(
                    "Each segment must be a non-blank path relative to the repository root.",
                    nameof(relative));
            }
        }

        string root = Root;
        string path = Path.GetFullPath(Path.Combine([root, .. relative]));
        string rootWithSeparator = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!string.Equals(path, root, StringComparison.Ordinal) &&
            !path.StartsWith(rootWithSeparator, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"'{Path.Combine(relative)}' resolves outside the repository root '{root}'.",
                nameof(relative));
        }

        return path;
    }

    private static string FindRoot()
    {
        string start = AppContext.BaseDirectory;
        for (DirectoryInfo? directory = new(start); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, SolutionFileName)))
            {
                return Path.TrimEndingDirectorySeparator(directory.FullName);
            }
        }

        throw new InvalidOperationException(
            $"No directory at or above '{start}' contains {SolutionFileName}; " +
            "run the tests from a repository checkout.");
    }
}

/// <summary>A record the API created: its response body and its id.</summary>
/// <param name="Record">The Record DTO the 201 response returned.</param>
/// <param name="GlobalRecordId">The record's <c>globalRecordId</c>.</param>
public sealed record CreatedRecord(JsonElement Record, string GlobalRecordId);

/// <summary>
/// HTTP helpers shared by the API tests, so request bodies, form lookups, record creation and ProblemDetails checks
/// are written once.
/// </summary>
/// <remarks>
/// Failure messages describe a response by its shape alone: the status code and name, the media type only when it is
/// <c>application/json</c>, <c>application/problem+json</c>, <c>text/plain</c> or <c>text/html</c>, the body's
/// length and JSON kind with its member or item count, and the <c>traceId</c> only when it has the form of a trace
/// identifier. A form lookup lists the form names that have the form of an identifier and counts the rest, and a
/// ProblemDetails <c>status</c> is printed only within the range of HTTP status codes. Messages never quote the body,
/// another member value, the <c>Location</c> header, a character set, a decoder or parser message, and they attach
/// no inner exception, because a failing assertion is exactly when a response may carry a connection string, SQL text
/// or field values, and test runners print every message and inner exception. A test that checks redaction asserts on
/// the body itself.
/// </remarks>
public static partial class ApiTestClient
{
    /// <summary>The media type of every body these helpers send.</summary>
    public const string JsonMediaType = "application/json";

    // How a failure message describes a body without a traceId member.
    private const string NoTraceId = "no traceId";

    // The ProblemDetails members that must be strings.
    private static readonly string[] ProblemTextMembers = ["type", "title", "detail"];

    // The media types a failure message names, in lowercase; DescribeMediaType compares them case-insensitively.
    private static readonly string[] KnownMediaTypes =
        [JsonMediaType, "application/problem+json", "text/plain", "text/html"];

    /// <summary>
    /// The serializer settings of request bodies and response reads: camelCase names, case-insensitive reads and
    /// strict numbers, as the API's wire contract has them, so a typed read rejects a number written as a string such
    /// as <c>"1"</c>. Dictionary keys, such as field names in <c>values</c>, are written unchanged.
    /// </summary>
    /// <remarks>
    /// The instance is read-only and shared by every test, so no test can change how another reads; setting any of
    /// its properties throws <see cref="InvalidOperationException"/>. A test that needs other settings copies it with
    /// <c>new JsonSerializerOptions(ApiTestClient.Json)</c>.
    /// </remarks>
    public static JsonSerializerOptions Json { get; } = CreateJsonOptions();

    /// <summary>Sends <paramref name="body"/> as <c>application/json; charset=utf-8</c> with <c>POST</c>.</summary>
    /// <param name="client">The client of a test host.</param>
    /// <param name="uri">The request path, for example <c>/api/forms/1/records</c>.</param>
    /// <param name="body">The value to serialize; <see langword="null"/> sends the JSON literal <c>null</c>.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The response; the caller disposes it.</returns>
    public static Task<HttpResponseMessage> PostJsonAsync(
        HttpClient client,
        string uri,
        object? body,
        CancellationToken ct = default) =>
        SendJsonAsync(client, HttpMethod.Post, uri, body, ct);

    /// <summary>Sends <paramref name="body"/> as <c>application/json; charset=utf-8</c> with <c>PUT</c>.</summary>
    /// <param name="client">The client of a test host.</param>
    /// <param name="uri">The request path, for example <c>/api/forms/1/records/{id}</c>.</param>
    /// <param name="body">The value to serialize; <see langword="null"/> sends the JSON literal <c>null</c>.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The response; the caller disposes it.</returns>
    public static Task<HttpResponseMessage> PutJsonAsync(
        HttpClient client,
        string uri,
        object? body,
        CancellationToken ct = default) =>
        SendJsonAsync(client, HttpMethod.Put, uri, body, ct);

    /// <summary>Reads the response body as <typeparamref name="T"/>.</summary>
    /// <param name="response">The response to read.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The deserialised value.</returns>
    /// <exception cref="XunitException">
    /// The body cannot be decoded as text, is not JSON of that shape, holds a number as a string, is the literal
    /// <c>null</c>, or has a member of a type the serializer does not support. The message names the target type and
    /// the parser's position, and describes the response by shape only.
    /// </exception>
    public static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        string text = await ReadBodyTextAsync(response, ct).ConfigureAwait(false);

        T? value;
        try
        {
            value = JsonSerializer.Deserialize<T>(text, Json);
        }
        catch (JsonException failure)
        {
            // The parser's message and path can quote the body, so only its position is reported, and the exception
            // is not attached.
            throw new XunitException(
                $"The response body is not a {typeof(T).Name}{DescribePosition(failure)}. {Describe(response, text)}");
        }
        catch (NotSupportedException)
        {
            // The serializer's message ends with the JSON path it reached, which names the body's members, so it is
            // not quoted and the exception is not attached.
            throw new XunitException(
                $"The response body cannot be read as a {typeof(T).Name}, because the serializer does not support " +
                $"that type or one of its members. {Describe(response, text)}");
        }

        return value ?? throw new XunitException(
            $"The response body is the JSON literal null, not a {typeof(T).Name}. {Describe(response, text)}");
    }

    /// <summary>Reads the response body as a JSON element that outlives the response.</summary>
    /// <param name="response">The response to read.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>A clone of the body's root element.</returns>
    /// <exception cref="XunitException">The body cannot be decoded as text, is empty, or is not JSON.</exception>
    public static async Task<JsonElement> ReadJsonElementAsync(
        HttpResponseMessage response,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        string text = await ReadBodyTextAsync(response, ct).ConfigureAwait(false);
        return ParseBody(response, text);
    }

    /// <summary>
    /// The <c>viewId</c> of the form named <paramref name="formName"/>, from <c>GET /api/forms</c>, so no test
    /// hard-codes a seeded id.
    /// </summary>
    /// <param name="client">The client of a test host.</param>
    /// <param name="formName">The form name, compared ordinally, for example <c>CaseReport</c>.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The form's view id.</returns>
    /// <exception cref="XunitException">
    /// The list does not answer 200, or names no such form. The message lists the names that have the form of an Epi
    /// Info identifier and only counts the others.
    /// </exception>
    public static async Task<int> GetViewIdAsync(HttpClient client, string formName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(formName);

        const string FormsPath = "/api/forms";
        using HttpResponseMessage response = await client.GetAsync(FormsPath, ct).ConfigureAwait(false);
        JsonElement forms = await ReadExpectedAsync(response, HttpStatusCode.OK, "GET " + FormsPath, ct)
            .ConfigureAwait(false);
        if (forms.ValueKind != JsonValueKind.Array)
        {
            throw new XunitException($"GET {FormsPath} did not return an array. {Describe(response, forms)}");
        }

        // Names are listed only when they have the form of an identifier; any other name could be data.
        var names = new List<string>();
        int otherNames = 0;
        int unnamed = 0;
        foreach (JsonElement form in forms.EnumerateArray())
        {
            if (form.ValueKind != JsonValueKind.Object ||
                !form.TryGetProperty("name", out JsonElement name) ||
                name.ValueKind != JsonValueKind.String)
            {
                unnamed++;
                continue;
            }

            string? text = TryGetString(name);
            if (text is not null && FormIdentifier().IsMatch(text))
            {
                names.Add(text);
            }
            else
            {
                otherNames++;
            }

            if (!string.Equals(text, formName, StringComparison.Ordinal))
            {
                continue;
            }

            if (form.TryGetProperty("viewId", out JsonElement viewId) &&
                viewId.ValueKind == JsonValueKind.Number &&
                viewId.TryGetInt32(out int id))
            {
                return id;
            }

            throw new XunitException(
                $"Form '{formName}' in GET {FormsPath} has no 32-bit integer viewId: viewId is " +
                $"{DescribeMember(form, "viewId")}, in a form that is {DescribeShape(form)}.");
        }

        string others = otherNames switch
        {
            0 => string.Empty,
            1 => " and 1 name that is not an identifier",
            _ => string.Create(CultureInfo.InvariantCulture, $" and {otherNames} names that are not identifiers"),
        };
        string withoutName = unnamed == 0 ? string.Empty : $", and {Count(unnamed, "item")} without a String name";
        throw new XunitException(
            $"GET {FormsPath} lists no form named '{formName}'; it lists [{string.Join(", ", names)}]{others}" +
            $"{withoutName}. {Describe(response, forms)}");
    }

    /// <summary>
    /// Creates a record with <c>POST /api/forms/{viewId}/records</c> and checks the 201 response and its
    /// <c>Location</c> header.
    /// </summary>
    /// <param name="client">The client of a test host.</param>
    /// <param name="viewId">The form's view id.</param>
    /// <param name="values">The <c>values</c> object, keyed by field name.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The created record's body and id.</returns>
    /// <exception cref="XunitException">
    /// The response is not 201, carries no <c>globalRecordId</c>, or its <c>Location</c> is not
    /// <c>/api/forms/{viewId}/records/{globalRecordId}</c>.
    /// </exception>
    public static async Task<CreatedRecord> CreateRecordAsync(
        HttpClient client,
        int viewId,
        IDictionary<string, object?> values,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(values);

        string path = string.Create(CultureInfo.InvariantCulture, $"/api/forms/{viewId}/records");
        using HttpResponseMessage response =
            await PostJsonAsync(client, path, new { values }, ct).ConfigureAwait(false);
        JsonElement record = await ReadExpectedAsync(response, HttpStatusCode.Created, "POST " + path, ct)
            .ConfigureAwait(false);

        if (record.ValueKind != JsonValueKind.Object ||
            !record.TryGetProperty("globalRecordId", out JsonElement id) ||
            id.ValueKind != JsonValueKind.String ||
            TryGetString(id) is not { Length: > 0 } globalRecordId)
        {
            throw new XunitException(
                $"POST {path} returned no non-empty string globalRecordId: globalRecordId is " +
                $"{DescribeMember(record, "globalRecordId")}. {Describe(response, record)}");
        }

        // Compared here rather than by an equality assertion, whose message would print both the returned id and the
        // header: the server wrote both, so either could be data.
        if (response.Headers.Location is not { } location)
        {
            string header = response.Headers.NonValidated.Contains("Location")
                ? "a Location header that is not a URI reference"
                : "no Location header";
            throw new XunitException($"POST {path} returned {header}. {Describe(response, record)}");
        }

        if (!string.Equals(location.OriginalString, $"{path}/{globalRecordId}", StringComparison.Ordinal))
        {
            throw new XunitException(
                $"POST {path} returned a Location header that is not {path}/ followed by the returned " +
                $"globalRecordId. {Describe(response, record)}");
        }

        return new CreatedRecord(record, globalRecordId);
    }

    /// <summary>
    /// Asserts that <paramref name="body"/> is an RFC 7807 problem: string <c>type</c>, <c>title</c> and
    /// <c>detail</c>, a <c>status</c> equal to <paramref name="status"/>, and a non-empty <c>traceId</c>.
    /// </summary>
    /// <param name="body">The response body.</param>
    /// <param name="status">The expected status code.</param>
    /// <returns><paramref name="body"/>, for further assertions.</returns>
    /// <exception cref="XunitException">
    /// The body is not such a problem. The message names the member and the kind of value found, never the value; a
    /// wrong <c>status</c> is printed only when it lies in the HTTP status range 100 to 599.
    /// </exception>
    public static JsonElement AssertProblemDetails(JsonElement body, int status)
    {
        Assert.Equal(JsonValueKind.Object, body.ValueKind);

        foreach (string member in ProblemTextMembers)
        {
            Assert.True(
                body.TryGetProperty(member, out JsonElement value) && value.ValueKind == JsonValueKind.String,
                $"ProblemDetails member '{member}' must be of kind String; it is {DescribeMember(body, member)}.");
        }

        if (!body.TryGetProperty("status", out JsonElement actual) ||
            actual.ValueKind != JsonValueKind.Number ||
            !actual.TryGetInt32(out int actualStatus))
        {
            throw new XunitException(
                "ProblemDetails member 'status' must be a Number that is a 32-bit integer; it is " +
                $"{DescribeMember(body, "status")}.");
        }

        if (actualStatus != status)
        {
            // Not an equality assertion, which would print any integer the body holds; a value outside the range of
            // HTTP status codes could be data.
            string found = actualStatus is >= 100 and <= 599
                ? actualStatus.ToString(CultureInfo.InvariantCulture)
                : "an integer outside the HTTP status range 100 to 599";
            throw new XunitException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"ProblemDetails member 'status' must be {status}; it is {found}."));
        }

        Assert.True(
            body.TryGetProperty("traceId", out JsonElement traceId) &&
            traceId.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(TryGetString(traceId)),
            $"ProblemDetails member 'traceId' must be a non-empty String; it is {DescribeMember(body, "traceId")}.");

        return body;
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            NumberHandling = JsonNumberHandling.Strict,
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    private static async Task<HttpResponseMessage> SendJsonAsync(
        HttpClient client,
        HttpMethod method,
        string uri,
        object? body,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);

        // Serialised by the runtime type, so anonymous objects and dictionaries keep every member.
        string json = JsonSerializer.Serialize(body, body?.GetType() ?? typeof(object), Json);
        using var request = new HttpRequestMessage(method, uri)
        {
            Content = new StringContent(json, Encoding.UTF8, JsonMediaType),
        };
        return await client.SendAsync(request, ct).ConfigureAwait(false);
    }

    private static async Task<JsonElement> ReadExpectedAsync(
        HttpResponseMessage response,
        HttpStatusCode expected,
        string request,
        CancellationToken ct)
    {
        string text = await ReadBodyTextAsync(response, ct).ConfigureAwait(false);
        if (response.StatusCode != expected)
        {
            throw new XunitException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{request} returned {(int)response.StatusCode}, expected {(int)expected}. " +
                    $"{Describe(response, text)}"));
        }

        return ParseBody(response, text);
    }

    // The body as text, the one place a helper decodes it. HttpContent reports a Content-Type charset it cannot
    // decode with an InvalidOperationException whose inner exception quotes that charset, so the failure is described
    // by status alone and the exception is not attached. Cancellation, a disposed response and transport failures
    // propagate unchanged.
    private static async Task<string> ReadBodyTextAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException failure) when (failure is not ObjectDisposedException)
        {
            throw new XunitException(
                "The response body cannot be decoded as text, for example because its Content-Type names a " +
                $"character set this runtime does not support. {DescribeStatus(response)}.");
        }
    }

    private static JsonElement ParseBody(HttpResponseMessage response, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new XunitException($"The response body is empty. {Describe(response, text)}");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException failure)
        {
            // The parser's message and path can quote the body, so only its position is reported, and the exception
            // is not attached.
            throw new XunitException(
                $"The response body is not JSON{DescribePosition(failure)}. {Describe(response, text)}");
        }
    }

    // Describes a response by its status, media type, body length, JSON kind and trace identifier, never its text.
    private static string Describe(HttpResponseMessage response, string body)
    {
        string shape;
        string trace;
        if (body.Length == 0)
        {
            (shape, trace) = ("empty", NoTraceId);
        }
        else if (string.IsNullOrWhiteSpace(body))
        {
            (shape, trace) = ("only whitespace", NoTraceId);
        }
        else
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(body);
                (shape, trace) = (DescribeShape(document.RootElement), DescribeTraceId(document.RootElement));
            }
            catch (JsonException)
            {
                (shape, trace) = ("not JSON", NoTraceId);
            }
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{DescribeStatus(response)}, body of {body.Length} characters that is {shape}, {trace}.");
    }

    // Describes a response whose body has already been parsed to body.
    private static string Describe(HttpResponseMessage response, JsonElement body) =>
        $"{DescribeStatus(response)}, body that is {DescribeShape(body)}, {DescribeTraceId(body)}.";

    private static string DescribeStatus(HttpResponseMessage response) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"Status {(int)response.StatusCode} {response.StatusCode}, {DescribeMediaType(response)}");

    // The Content-Type media type, named only when it is one of KnownMediaTypes, in that list's lowercase form; any
    // other value is server text, which could be data.
    private static string DescribeMediaType(HttpResponseMessage response)
    {
        if (response.Content.Headers.ContentType?.MediaType is not { } mediaType)
        {
            // A header that is present but does not parse is not absent.
            return response.Content.Headers.NonValidated.Contains("Content-Type")
                ? "another media type"
                : "media type none";
        }

        foreach (string known in KnownMediaTypes)
        {
            if (string.Equals(known, mediaType, StringComparison.OrdinalIgnoreCase))
            {
                return "media type " + known;
            }
        }

        return "another media type";
    }

    // The JSON kind of element, with the member or item count of an object or array; never a value.
    private static string DescribeShape(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => "a JSON object with " + Count(element.GetPropertyCount(), "member"),
        JsonValueKind.Array => "a JSON array with " + Count(element.GetArrayLength(), "item"),
        JsonValueKind.String => "a JSON string",
        JsonValueKind.Number => "a JSON number",
        JsonValueKind.True or JsonValueKind.False => "a JSON boolean",
        JsonValueKind.Null => "the JSON literal null",
        _ => "no JSON value",
    };

    private static string Count(int count, string noun) =>
        string.Create(CultureInfo.InvariantCulture, $"{count} {noun}{(count == 1 ? string.Empty : "s")}");

    // The kind of value an object holds under member, or "missing"; never the value itself.
    private static string DescribeMember(JsonElement body, string member)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty(member, out JsonElement value))
        {
            return "missing";
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => TryGetString(value) switch
            {
                null => "a String with an unpaired surrogate",
                { } text when string.IsNullOrWhiteSpace(text) => "an empty or whitespace String",
                _ => "a String",
            },
            JsonValueKind.Number => value.TryGetInt32(out _)
                ? "a Number that is a 32-bit integer"
                : "a Number that is not a 32-bit integer",
            JsonValueKind.Object => "an Object",
            JsonValueKind.Array => "an Array",
            _ => $"of kind {value.ValueKind}",
        };
    }

    // The traceId of an object body, quoted only when it has the form of a trace identifier the API writes: an
    // Activity id in W3C format, or an ASP.NET Core request identifier. Any other value could be data.
    private static string DescribeTraceId(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("traceId", out JsonElement traceId))
        {
            return NoTraceId;
        }

        return traceId.ValueKind == JsonValueKind.String &&
            TryGetString(traceId) is { } value &&
            TraceIdentifier().IsMatch(value)
                ? $"traceId {value}"
                : "a traceId that is not a trace identifier";
    }

    // The parser's zero-based position, the one part of a JsonException that cannot quote the input.
    private static string DescribePosition(JsonException failure) =>
        failure.LineNumber is long line && failure.BytePositionInLine is long position
            ? string.Create(
                CultureInfo.InvariantCulture,
                $" (reading failed at zero-based line {line}, byte {position} of that line)")
            : string.Empty;

    // The text of a String element, or null when it holds an escaped lone surrogate, which has no string form.
    private static string? TryGetString(JsonElement element)
    {
        try
        {
            return element.GetString();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    [GeneratedRegex(
        @"\A(?:[0-9a-f]{2}-[0-9a-f]{32}-[0-9a-f]{16}-[0-9a-f]{2}|[0-9A-Z]{13}(?::[0-9A-F]{8})?)\z")]
    private static partial Regex TraceIdentifier();

    // The form of an Epi Info form name: a letter, then up to 63 letters, digits or underscores.
    [GeneratedRegex(@"\A[A-Za-z][A-Za-z0-9_]{0,63}\z")]
    private static partial Regex FormIdentifier();
}
