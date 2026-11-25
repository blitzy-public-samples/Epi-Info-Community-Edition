using EpiInfo.Domain.Storage;

namespace EpiInfo.Api.Services;

/// <summary>
/// Supplies the save stamp of a record write: the OS user of the API process and the local time of the injected
/// clock, in whole seconds.
/// </summary>
/// <remarks>
/// <para>
/// Each write operation creates exactly one stamp, and the store writes that one value wherever the operation writes
/// stamps to the base table:
/// </para>
/// <list type="bullet">
/// <item><description>
/// Insert: both the <c>FirstSaveLogonName</c>/<c>FirstSaveTime</c> and the <c>LastSaveLogonName</c>/<c>LastSaveTime</c>
/// pairs. Legacy Epi Info took a separate <c>DateTime.Now</c> for each pair, so its last-save time could fall a second
/// later; one stamp for both pairs is a deliberate departure.
/// </description></item>
/// <item><description>Update, and soft delete of an active record: the <c>LastSave*</c> pair.</description></item>
/// <item><description>
/// <c>DELETE</c> of an already-deleted record: nothing, stamps included. Callers create a stamp only on a write path,
/// and the store ignores it when the operation writes nothing.
/// </description></item>
/// </list>
/// <para>
/// The logon name is <see cref="Environment.UserName"/>, because the API acts as the OS user of its process. Legacy
/// wrote the Windows identity in <c>DOMAIN\user</c> form; the plain user name is the target's deliberate choice.
/// </para>
/// <para>
/// The time comes only from the registered <see cref="TimeProvider"/>, never from the system clock directly, so a test
/// host that replaces the provider controls every stamp value. The type holds no mutable state and is safe to share as
/// a singleton.
/// </para>
/// </remarks>
/// <param name="timeProvider">The clock whose local time stamps each write.</param>
/// <exception cref="ArgumentNullException"><paramref name="timeProvider"/> is <see langword="null"/>.</exception>
public sealed class SaveStampProvider(TimeProvider timeProvider)
{
    // Written when the process reports no user name, because a stamp's logon name may never be blank.
    private const string UnknownLogonName = "unknown";

    private readonly TimeProvider timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    /// <summary>Creates the stamp for one write operation, reading the user name and the clock on each call.</summary>
    /// <returns>
    /// A stamp whose logon name is the process user name, or <c>unknown</c> when the process reports none, and whose
    /// time is the provider's local time with its sub-second part removed.
    /// </returns>
    public SaveStamp Create()
    {
        var logonName = Environment.UserName;
        if (string.IsNullOrWhiteSpace(logonName))
        {
            logonName = UnknownLogonName;
        }

        // Local time, as legacy's DateTime.Now, and never converted to UTC: DateTimeOffset.DateTime has an
        // Unspecified kind, which truncation keeps. Whole seconds are what legacy's DateTime.Now.ToString() round trip
        // stored, and the stamp refuses anything finer.
        var time = SaveStamp.TruncateToSeconds(timeProvider.GetLocalNow().DateTime);

        return new SaveStamp(logonName, time);
    }
}
