namespace EpiInfo.Api.Tests.Infrastructure;

/// <summary>
/// A deterministic <see cref="TimeProvider"/> for API tests: the current instant moves only when a test sets or
/// advances it, so every save stamp the API writes is a value the test chose.
/// </summary>
/// <remarks>
/// <para>
/// <c>ApiFactory</c> replaces the <see cref="TimeProvider"/> singleton, which production registers as
/// <see cref="TimeProvider.System"/>, with one instance of this class.
/// <see cref="EpiInfo.Api.Services.SaveStampProvider"/> reads <see cref="TimeProvider.GetLocalNow"/> from it and
/// truncates the local time with <see cref="EpiInfo.Domain.Storage.SaveStamp.TruncateToSeconds"/>, so the
/// <c>FirstSaveTime</c> and <c>LastSaveTime</c> values found in SQL are exactly the times the test set.
/// </para>
/// <para>
/// Usage contract: set a whole-second time before each write operation, with consecutive operations a minute apart,
/// so that a changed <c>LastSaveTime</c> is asserted as changed rather than merely allowed. The class itself accepts
/// sub-second values; the stamp provider removes the fraction before the stamp is written.
/// </para>
/// <para>
/// With the default <see cref="TimeZoneInfo.Utc"/> zone, local time is the value passed to <see cref="SetLocalNow"/>,
/// so the expected stamps are the literal values the test sets. A test that needs a non-UTC offset passes a fixed zone
/// built with <see cref="TimeZoneInfo.CreateCustomTimeZone(string, TimeSpan, string, string)"/>, which has no daylight
/// saving time.
/// </para>
/// <para>
/// Only the wall clock is controlled. <see cref="GetTimestamp"/>, <see cref="TimestampFrequency"/> and the timers the
/// base class creates stay real, so framework components that measure elapsed time or schedule work through the
/// resolved <see cref="TimeProvider"/> keep working. The instance is thread-safe: concurrent requests read one
/// consistent instant.
/// </para>
/// </remarks>
public sealed class ControlledTimeProvider : TimeProvider
{
    private readonly Lock gate = new();
    private readonly TimeZoneInfo localTimeZone;

    // Always held with a zero offset, so readers never see a value whose meaning depends on how it was set.
    private DateTimeOffset utcNow;

    /// <summary>
    /// Creates a clock in the <see cref="TimeZoneInfo.Utc"/> zone whose local time is <see cref="DefaultLocalStart"/>.
    /// </summary>
    public ControlledTimeProvider()
        : this(TimeZoneInfo.Utc)
    {
    }

    /// <summary>Creates a clock in a fixed local zone.</summary>
    /// <param name="localTimeZone">The zone <see cref="TimeProvider.GetLocalNow"/> converts the instant with.</param>
    /// <param name="localStart">
    /// The initial wall-clock time in <paramref name="localTimeZone"/>, whatever its <see cref="DateTime.Kind"/>;
    /// <see cref="DefaultLocalStart"/> when <see langword="null"/>.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="localTimeZone"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="localStart"/> is not a valid time in <paramref name="localTimeZone"/>.
    /// </exception>
    public ControlledTimeProvider(TimeZoneInfo localTimeZone, DateTime? localStart = null)
    {
        ArgumentNullException.ThrowIfNull(localTimeZone);

        this.localTimeZone = localTimeZone;
        utcNow = ToUtcInstant(localStart ?? DefaultLocalStart, localTimeZone);
    }

    /// <summary>The initial local time of a clock created without one: 2024-06-03 09:00:00, a whole second.</summary>
    public static DateTime DefaultLocalStart { get; } = new DateTime(2024, 6, 3, 9, 0, 0, DateTimeKind.Unspecified);

    /// <summary>The fixed zone given at construction.</summary>
    public override TimeZoneInfo LocalTimeZone => localTimeZone;

    /// <summary>
    /// The current local time with an <see cref="DateTimeKind.Unspecified"/> kind: the exact value
    /// <see cref="EpiInfo.Api.Services.SaveStampProvider.Create"/> truncates to whole seconds.
    /// </summary>
    public DateTime LocalNow => GetLocalNow().DateTime;

    /// <inheritdoc/>
    public override long TimestampFrequency => TimeProvider.System.TimestampFrequency;

    /// <summary>Returns the controlled instant, which never advances on its own.</summary>
    /// <returns>The instant last set or advanced to, with a zero offset.</returns>
    public override DateTimeOffset GetUtcNow()
    {
        lock (gate)
        {
            return utcNow;
        }
    }

    /// <summary>
    /// Returns the real high-resolution timestamp, so durations the framework measures (request timing, timeouts)
    /// still advance while the wall clock stands still.
    /// </summary>
    /// <returns>The current value of <see cref="TimeProvider.System"/>'s timestamp.</returns>
    public override long GetTimestamp() => TimeProvider.System.GetTimestamp();

    /// <summary>Sets the clock so that local time in <see cref="LocalTimeZone"/> is <paramref name="local"/>.</summary>
    /// <param name="local">
    /// The wall-clock time in <see cref="LocalTimeZone"/>, whatever its <see cref="DateTime.Kind"/>.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="local"/> is not a valid time in <see cref="LocalTimeZone"/>.
    /// </exception>
    public void SetLocalNow(DateTime local)
    {
        var instant = ToUtcInstant(local, localTimeZone);
        lock (gate)
        {
            utcNow = instant;
        }
    }

    /// <summary>Sets the clock to the instant <paramref name="utc"/>, whatever its offset.</summary>
    /// <param name="utc">The new instant.</param>
    public void SetUtcNow(DateTimeOffset utc)
    {
        var instant = utc.ToUniversalTime();
        lock (gate)
        {
            utcNow = instant;
        }
    }

    /// <summary>Moves the clock forward by <paramref name="delta"/>.</summary>
    /// <param name="delta">The non-negative amount to advance by.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="delta"/> is negative, because test time never runs backwards, or the result is outside the
    /// range of <see cref="DateTimeOffset"/>.
    /// </exception>
    public void Advance(TimeSpan delta)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delta, TimeSpan.Zero);

        lock (gate)
        {
            utcNow = utcNow.Add(delta);
        }
    }

    // The kind is reset so the value is read as wall-clock time in the zone: a Local or Utc kind would otherwise make
    // ConvertTimeToUtc reject the call or reinterpret the value against the machine's own zone.
    private static DateTimeOffset ToUtcInstant(DateTime local, TimeZoneInfo zone) =>
        new(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone));
}
