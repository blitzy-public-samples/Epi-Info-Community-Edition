using System.Globalization;
using System.Text;

namespace EpiInfo.Data;

/// <summary>
/// Settings for the SQL Server data layer. The API builds this record from configuration
/// (<c>ConnectionStrings:EpiInfo</c> and <c>Database:CommandTimeoutSeconds</c>); this assembly
/// reads no configuration itself.
/// </summary>
/// <param name="ConnectionString">
/// The SQL Server connection string, or <see langword="null"/> when none is configured. It is not
/// parsed here: a malformed value surfaces when the first connection is created.
/// </param>
/// <param name="CommandTimeoutSeconds">
/// The timeout applied to every command, in whole seconds; at least 1.
/// </param>
public sealed record DatabaseOptions(
    string? ConnectionString,
    int CommandTimeoutSeconds = DatabaseOptions.DefaultCommandTimeoutSeconds)
{
    /// <summary>
    /// The command timeout used when configuration sets none. Legacy Epi Info waited 1,500 seconds
    /// on every command; the web service deliberately fails a stalled command after 30.
    /// </summary>
    public const int DefaultCommandTimeoutSeconds = 30;

    private readonly int commandTimeoutSeconds = ValidateCommandTimeout(CommandTimeoutSeconds);

    /// <summary>The timeout applied to every command, in whole seconds; at least 1.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than 1.</exception>
    public int CommandTimeoutSeconds
    {
        get => commandTimeoutSeconds;
        // Validated here as well as in the constructor, so a `with` expression cannot bypass the rule.
        init => commandTimeoutSeconds = ValidateCommandTimeout(value);
    }

    /// <summary>
    /// Whether a usable connection string is configured: <see langword="false"/> when it is absent,
    /// empty or whitespace. Whenever this is <see langword="false"/>, each store call that needs a
    /// connection reports the database unavailable, throwing <c>DataStoreUnavailableException</c>
    /// before any connection object is created, and a call that needs none still succeeds.
    /// </summary>
    public bool HasConnectionString => !string.IsNullOrWhiteSpace(ConnectionString);

    private static int ValidateCommandTimeout(int value)
    {
        // SqlClient reads a timeout of 0 as "wait indefinitely", and a negative one is invalid.
        ArgumentOutOfRangeException.ThrowIfLessThan(value, 1, nameof(CommandTimeoutSeconds));
        return value;
    }

    // Replaces the generated member list so ToString() never contains the connection string, which
    // carries the database password: it reports only whether one is set.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append(
            CultureInfo.InvariantCulture,
            $"ConnectionString = {(HasConnectionString ? "set" : "absent")}, CommandTimeoutSeconds = {CommandTimeoutSeconds}");
        return true;
    }
}
