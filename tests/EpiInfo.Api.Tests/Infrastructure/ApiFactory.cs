using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using EpiInfo.Api.Hosting;
using EpiInfo.CheckCode;
using EpiInfo.Data.Sql;
using EpiInfo.Domain.Storage;
using EpiInfo.Tests.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Xunit.Sdk;

namespace EpiInfo.Api.Tests.Infrastructure;

/// <summary>
/// Where an <see cref="ApiFactoryBase"/> host takes its <c>ConnectionStrings:EpiInfo</c> setting from.
/// </summary>
public enum ApiConnectionMode
{
    /// <summary>
    /// The rehearsal database resolved by <see cref="TestDatabase.ConnectionString"/>; with a
    /// <see cref="ApiFactoryOptions.DataStore"/>, the credential-free
    /// <see cref="ApiFactoryBase.FakeDataStoreConnectionString"/> instead, because nothing connects.
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
    /// <summary>
    /// The connection string a <see cref="FakeDataStore"/> host is given: it holds no credential, and nothing connects
    /// with it, because the store is replaced. Being non-blank, it keeps the missing-connection-string path out of
    /// tests that do not ask for it.
    /// </summary>
    public const string FakeDataStoreConnectionString = "Server=fake-data-store;Database=EpiRehearsal";

    /// <summary>Validates <paramref name="options"/> and creates the doubles.</summary>
    /// <param name="options">The host configuration; the defaults when <see langword="null"/>.</param>
    /// <exception cref="ArgumentException">The options are inconsistent; the message names the member.</exception>
    protected ApiFactoryBase(ApiFactoryOptions? options)
    {
        Options = Validate(options ?? new ApiFactoryOptions());
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

        // The committed appsettings.json and appsettings.Development.json supply the binding, the body limit and the
        // Logging:LogLevel filters, so every test runs under the production configuration.
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

        return options;
    }

    // Configuration keys compare case-insensitively, so an extra setting replaces a computed one whatever its casing.
    private Dictionary<string, string?> ResolveSettings()
    {
        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        // An extra setting for the connection string wins anyway, so the test database is not resolved, and cannot
        // fail for a missing password, when one is given.
        bool connectionStringOverridden = Options.ExtraSettings.Keys.Any(
            key => string.Equals(key, ApiOptions.ConnectionStringKey, StringComparison.OrdinalIgnoreCase));
        if (!connectionStringOverridden)
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

        return settings;
    }

    // Read at host build, never earlier: TestDatabase is the only connection resolution of the tests, and a factory
    // that is constructed but never started must not require the database variables.
    private string ResolveConnectionString() => Options.ConnectionMode switch
    {
        ApiConnectionMode.None => string.Empty,
        ApiConnectionMode.Supplied => Options.ConnectionString
            ?? throw new InvalidOperationException("A supplied connection string was validated as present."),
        _ when Options.DataStore is not null => FakeDataStoreConnectionString,
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

    /// <summary>
    /// Creates a factory whose host uses the SQL Server store and the rehearsal database, after proving that database
    /// reachable.
    /// </summary>
    /// <param name="options">
    /// The host configuration, in <see cref="ApiConnectionMode.Default"/> mode and without a data store; the defaults
    /// when <see langword="null"/>.
    /// </param>
    /// <returns>The factory, not yet started.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="options"/> sets a data store or another connection mode: the database host must use the real
    /// store on the connection <see cref="TestDatabase"/> resolves and probes.
    /// </exception>
    /// <exception cref="TestDatabaseUnavailableException">
    /// The database is not configured or cannot be reached. Integration tests let it fail the test, naming
    /// <c>SQL_HOST</c>, <c>MSSQL_SA_PASSWORD</c> and <c>ConnectionStrings__EpiInfo</c>; they never skip.
    /// </exception>
    public static async Task<ApiFactory> ForDatabaseAsync(ApiFactoryOptions? options = null)
    {
        if (options?.DataStore is not null)
        {
            throw new ArgumentException(
                $"A database host uses the SQL Server store; create a {nameof(FakeDataStore)} host with the " +
                "constructor.",
                nameof(options));
        }

        if (options is not null && options.ConnectionMode != ApiConnectionMode.Default)
        {
            throw new ArgumentException(
                $"A database host connects through {nameof(TestDatabase)}, so its connection mode must be " +
                $"{nameof(ApiConnectionMode)}.{nameof(ApiConnectionMode.Default)}.",
                nameof(options));
        }

        await TestDatabase.EnsureReachableAsync().ConfigureAwait(false);
        return new ApiFactory(options);
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
/// Failure messages carry the response status and body, which hold only what the API chose to return; a test that
/// checks redaction asserts on the body itself.
/// </remarks>
public static class ApiTestClient
{
    /// <summary>The media type of every body these helpers send.</summary>
    public const string JsonMediaType = "application/json";

    // A failure message quotes at most this much of a response body.
    private const int MaxQuotedBodyLength = 4096;

    // The ProblemDetails members that must be strings.
    private static readonly string[] ProblemTextMembers = ["type", "title", "detail"];

    /// <summary>
    /// The serializer settings of request bodies and response reads: camelCase names and case-insensitive reads.
    /// Dictionary keys, such as field names in <c>values</c>, are written unchanged.
    /// </summary>
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web);

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
    /// <exception cref="XunitException">The body is not JSON of that shape, or is the literal <c>null</c>.</exception>
    public static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        string text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        T? value;
        try
        {
            value = JsonSerializer.Deserialize<T>(text, Json);
        }
        catch (JsonException failure)
        {
            throw new XunitException(
                $"The response body is not a {typeof(T).Name}: {failure.Message} {Describe(response, text)}",
                failure);
        }

        return value ?? throw new XunitException($"The response body is null. {Describe(response, text)}");
    }

    /// <summary>Reads the response body as a JSON element that outlives the response.</summary>
    /// <param name="response">The response to read.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>A clone of the body's root element.</returns>
    /// <exception cref="XunitException">The body is empty or not JSON.</exception>
    public static async Task<JsonElement> ReadJsonElementAsync(
        HttpResponseMessage response,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        string text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
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
    /// <exception cref="XunitException">The list does not answer 200, or names no such form.</exception>
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
            throw new XunitException(
                $"GET {FormsPath} did not return an array. {Describe(response, forms.GetRawText())}");
        }

        var names = new List<string>();
        foreach (JsonElement form in forms.EnumerateArray())
        {
            if (form.ValueKind != JsonValueKind.Object ||
                !form.TryGetProperty("name", out JsonElement name) ||
                name.GetString() is not { } text)
            {
                continue;
            }

            names.Add(text);
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

            throw new XunitException($"Form '{formName}' has no integer viewId: {form.GetRawText()}");
        }

        throw new XunitException(
            $"GET {FormsPath} lists no form named '{formName}'; it lists: [{string.Join(", ", names)}].");
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
            id.GetString() is not { Length: > 0 } globalRecordId)
        {
            throw new XunitException(
                $"POST {path} returned no globalRecordId. {Describe(response, record.GetRawText())}");
        }

        Assert.Equal($"{path}/{globalRecordId}", response.Headers.Location?.OriginalString);
        return new CreatedRecord(record, globalRecordId);
    }

    /// <summary>
    /// Asserts that <paramref name="body"/> is an RFC 7807 problem: string <c>type</c>, <c>title</c> and
    /// <c>detail</c>, a <c>status</c> equal to <paramref name="status"/>, and a non-empty <c>traceId</c>.
    /// </summary>
    /// <param name="body">The response body.</param>
    /// <param name="status">The expected status code.</param>
    /// <returns><paramref name="body"/>, for further assertions.</returns>
    public static JsonElement AssertProblemDetails(JsonElement body, int status)
    {
        Assert.Equal(JsonValueKind.Object, body.ValueKind);

        foreach (string member in ProblemTextMembers)
        {
            Assert.True(
                body.TryGetProperty(member, out JsonElement value) && value.ValueKind == JsonValueKind.String,
                $"ProblemDetails member '{member}' is missing or not a string: {body.GetRawText()}");
        }

        Assert.True(
            body.TryGetProperty("status", out JsonElement actual) && actual.ValueKind == JsonValueKind.Number,
            $"ProblemDetails member 'status' is missing or not a number: {body.GetRawText()}");
        Assert.Equal(status, actual.GetInt32());

        Assert.True(
            body.TryGetProperty("traceId", out JsonElement traceId) &&
            traceId.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(traceId.GetString()),
            $"ProblemDetails member 'traceId' is missing or empty: {body.GetRawText()}");

        return body;
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
        string text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
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
            throw new XunitException(
                $"The response body is not JSON: {failure.Message} {Describe(response, text)}",
                failure);
        }
    }

    private static string Describe(HttpResponseMessage response, string body)
    {
        string quoted = body.Length <= MaxQuotedBodyLength ? body : body[..MaxQuotedBodyLength] + "...";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"Status {(int)response.StatusCode} {response.StatusCode}, body: {quoted}");
    }
}
