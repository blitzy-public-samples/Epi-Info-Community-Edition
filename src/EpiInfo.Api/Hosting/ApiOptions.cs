using System.Globalization;
using System.Text;

namespace EpiInfo.Api.Hosting;

/// <summary>
/// The API settings that hosting code reads, taken from configuration in one place.
/// </summary>
/// <param name="ConnectionString">
/// The <c>ConnectionStrings:EpiInfo</c> value, or <see langword="null"/> when it is absent or blank. Its absence is
/// not a startup error: each data store call that needs a connection then reports the database unavailable, throwing
/// <c>DataStoreUnavailableException</c> before any connection object is created, and a call that needs none still
/// succeeds.
/// </param>
/// <param name="CommandTimeoutSeconds">The SQL command timeout, at least 1 second.</param>
/// <param name="ClientPath">
/// The built web client folder as configured, still relative to the content root unless configured absolute.
/// </param>
/// <param name="MaxRequestBodySize">
/// The largest request body accepted, in bytes, from 1 to <see cref="DefaultMaxRequestBodySize"/> (1 MiB).
/// </param>
public sealed record ApiOptions(
    string? ConnectionString,
    int CommandTimeoutSeconds,
    string ClientPath,
    long MaxRequestBodySize)
{
    /// <summary>The name passed to <c>GetConnectionString</c>.</summary>
    public const string ConnectionStringName = "EpiInfo";

    /// <summary>
    /// The configuration key of the connection string; its environment form is <c>ConnectionStrings__EpiInfo</c>.
    /// </summary>
    public const string ConnectionStringKey = "ConnectionStrings:" + ConnectionStringName;

    /// <summary>The configuration key of the SQL command timeout, in seconds.</summary>
    public const string CommandTimeoutKey = "Database:CommandTimeoutSeconds";

    /// <summary>The command timeout used when none is configured; legacy Epi Info waited 1,500 seconds.</summary>
    public const int DefaultCommandTimeoutSeconds = 30;

    /// <summary>The configuration key of the built web client folder.</summary>
    public const string ClientPathKey = "Web:ClientPath";

    /// <summary>
    /// The client folder used when none is configured, relative to the content root <c>src/EpiInfo.Api</c>.
    /// </summary>
    public const string DefaultClientPath = "../../web/dist";

    /// <summary>The configuration key of the request body limit, shared with Kestrel's own limit.</summary>
    public const string MaxRequestBodySizeKey = "Kestrel:Limits:MaxRequestBodySize";

    /// <summary>
    /// The request body limit used when none is configured, and the largest limit configuration may set: 1 MiB.
    /// </summary>
    public const long DefaultMaxRequestBodySize = 1_048_576L;

    /// <summary>Whether a non-blank connection string is configured.</summary>
    public bool HasConnectionString => !string.IsNullOrWhiteSpace(ConnectionString);

    /// <summary>
    /// Reads the settings from <paramref name="configuration"/>, applying the defaults to absent or blank values.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="configuration"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The command timeout is set to something other than a positive integer, or the request body limit to something
    /// other than an integer from 1 to <see cref="DefaultMaxRequestBodySize"/>.
    /// </exception>
    public static ApiOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        string? connectionString = configuration.GetConnectionString(ConnectionStringName);
        string? clientPath = configuration[ClientPathKey];

        return new ApiOptions(
            string.IsNullOrWhiteSpace(connectionString) ? null : connectionString,
            ReadCommandTimeoutSeconds(configuration),
            string.IsNullOrWhiteSpace(clientPath) ? DefaultClientPath : clientPath.Trim(),
            ReadMaxRequestBodySize(configuration));
    }

    private static int ReadCommandTimeoutSeconds(IConfiguration configuration)
    {
        string? value = configuration[CommandTimeoutKey];
        if (string.IsNullOrWhiteSpace(value))
        {
            return DefaultCommandTimeoutSeconds;
        }

        // 0 is rejected with the negatives: ADO.NET reads a zero CommandTimeout as "wait indefinitely".
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds) && seconds >= 1)
        {
            return seconds;
        }

        throw NotAPositiveInteger(CommandTimeoutKey);
    }

    private static long ReadMaxRequestBodySize(IConfiguration configuration)
    {
        string? value = configuration[MaxRequestBodySizeKey];
        if (string.IsNullOrWhiteSpace(value))
        {
            return DefaultMaxRequestBodySize;
        }

        // The default is also the cap: a lower limit is accepted, a higher one would let both hosts take larger bodies.
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long bytes) &&
            bytes is >= 1 and <= DefaultMaxRequestBodySize)
        {
            return bytes;
        }

        throw NotWithinRequestBodyCap();
    }

    // The message names the key only: a rejected value may be a mistyped secret, and startup errors reach the logs.
    private static InvalidOperationException NotAPositiveInteger(string key) =>
        new($"Configuration value '{key}' must be a positive integer.");

    // The same rule: the message names the key and the fixed cap, never the configured value.
    private static InvalidOperationException NotWithinRequestBodyCap() =>
        new(string.Create(
            CultureInfo.InvariantCulture,
            $"Configuration value '{MaxRequestBodySizeKey}' must be a positive integer no greater than " +
            $"{DefaultMaxRequestBodySize}."));

    // Replaces the record's generated member list so ToString() reports whether a connection string is set but never
    // its value, which carries the password.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append(
            CultureInfo.InvariantCulture,
            $"HasConnectionString = {HasConnectionString}, CommandTimeoutSeconds = {CommandTimeoutSeconds}, " +
            $"ClientPath = {ClientPath}, MaxRequestBodySize = {MaxRequestBodySize}");
        return true;
    }
}
