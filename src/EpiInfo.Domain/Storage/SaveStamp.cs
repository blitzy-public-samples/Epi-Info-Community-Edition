namespace EpiInfo.Domain.Storage;

/// <summary>
/// The save stamp of one record write: who saved and when. The stamps written to the base table are the
/// only audit of writes; no audit table exists.
/// </summary>
/// <remarks>
/// <para>
/// <c>EpiInfo.Api.Services.SaveStampProvider</c> builds one stamp per write operation. The logon name is
/// <see cref="Environment.UserName"/> of the API process, which is the identity the API acts as. The time is
/// the local time of the injected <see cref="TimeProvider"/>, reduced with <see cref="TruncateToSeconds"/>;
/// tests substitute a controlled clock, so every stamp value they assert is known.
/// </para>
/// <para>How a store writes the stamp to the base table:</para>
/// <list type="bullet">
/// <item><description>
/// Insert: the one stamp fills <c>FirstSaveLogonName</c>, <c>FirstSaveTime</c>, <c>LastSaveLogonName</c> and
/// <c>LastSaveTime</c>, each written only when that column exists in the base table.
/// </description></item>
/// <item><description>Update: <c>LastSaveLogonName</c> and <c>LastSaveTime</c>.</description></item>
/// <item><description>Soft delete of an active record: <c>LastSaveLogonName</c> and <c>LastSaveTime</c>.</description></item>
/// <item><description>
/// <c>DELETE</c> of an already-deleted record: nothing is written, stamps included.
/// </description></item>
/// </list>
/// <para>
/// Legacy Epi Info took a separate <c>DateTime.Now</c> for the first-save and the last-save pair, so its
/// <c>LastSaveTime</c> could fall a second after <c>FirstSaveTime</c>. One stamp for both pairs is a deliberate
/// reconciliation. Callers can never supply stamps: a record's <c>values</c> accept data fields only, and the
/// stamp columns are system columns.
/// </para>
/// </remarks>
/// <param name="LogonName">The logon name of the saving identity; never <see langword="null"/>, empty or whitespace.</param>
/// <param name="Time">
/// The save time in whole seconds, the precision legacy stores. Its <see cref="DateTime.Kind"/> is kept as given,
/// and the store writes the value unchanged to the <c>datetime</c> stamp columns.
/// </param>
public sealed record SaveStamp(string LogonName, DateTime Time)
{
    private readonly string logonName = ValidateLogonName(LogonName);
    private readonly DateTime time = ValidateTime(Time, nameof(Time));

    /// <summary>The logon name of the saving identity; never <see langword="null"/>, empty or whitespace.</summary>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The value is empty or whitespace.</exception>
    public string LogonName
    {
        get => logonName;
        // Validated here as well as in the constructor, so a `with` expression cannot bypass the rule.
        init => logonName = ValidateLogonName(value);
    }

    /// <summary>The save time, a whole second, with its <see cref="DateTime.Kind"/> kept as given.</summary>
    /// <exception cref="ArgumentException">The value has a sub-second part.</exception>
    public DateTime Time
    {
        get => time;
        init => time = ValidateTime(value, nameof(Time));
    }

    /// <summary>
    /// Returns <paramref name="time"/> with its sub-second part removed and its <see cref="DateTime.Kind"/> kept,
    /// which is the value a valid stamp holds.
    /// </summary>
    /// <param name="time">Any time.</param>
    public static DateTime TruncateToSeconds(DateTime time) =>
        new(time.Ticks - time.Ticks % TimeSpan.TicksPerSecond, time.Kind);

    // Exception messages are fixed text and never echo the supplied value.
    private static string ValidateLogonName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, nameof(LogonName));
        return value;
    }

    // The caller passes nameof(Time), so the exception names the record property rather than this helper's parameter.
    private static DateTime ValidateTime(DateTime value, string paramName)
    {
        // Stamp values are compared exactly against what the database holds, so a sub-second part, which the
        // legacy string round trip never produced, is refused rather than silently rounded.
        if (value.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            throw new ArgumentException("The save time must be a whole second.", paramName);
        }

        return value;
    }
}
