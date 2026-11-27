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
/// <c>DELETE</c> of an already-deleted record: nothing, stamps included, and no stamp is created.
/// </description></item>
/// </list>
/// <para>
/// An insert creates its stamp after validation and the permission check, just before the store call, because an
/// insert always writes. An update and a soft delete hand <see cref="Create"/> to the store, which calls it inside its
/// transaction only once its locked checks decide that the operation writes, and before its first data statement.
/// </para>
/// <para>
/// The logon name is <see cref="Environment.UserName"/>, because the API acts as the OS user of its process. Legacy
/// wrote the Windows identity in <c>DOMAIN\user</c> form; the plain user name is the target's deliberate choice.
/// </para>
/// <para>
/// A process whose OS user has no name, such as one running under a user id with no account entry, cannot say who
/// saved, so it gets no stamp: <see cref="Create"/> throws an <see cref="InvalidOperationException"/> rather than
/// write an invented name to the audit columns. The refusal applies only to a write that would store a stamp: an
/// insert, an update, and a soft delete of an active record. Each is refused with nothing written, and the API answers
/// it as a 500 whose <c>writeOutcome</c> is <c>notWritten</c>. A request that writes nothing keeps its usual answer:
/// the 404 of an unknown record, the 409 of an update of a deleted record, the 400 of a failed required gate, the 403
/// of a denied permission, and the 204 of a <c>DELETE</c> of an already-deleted record.
/// </para>
/// <para>
/// The time comes only from the registered <see cref="TimeProvider"/>, never from the system clock directly, so a test
/// host that replaces the provider controls every stamp value. The type holds no mutable state and is safe to share as
/// a singleton.
/// </para>
/// </remarks>
public sealed class SaveStampProvider
{
    // Fixed text that names no value, so neither an error body nor a log line can carry one.
    private const string NoUserNameMessage =
        "The API process has no OS user name, so the save stamp cannot name who saved; nothing was written.";

    private readonly TimeProvider timeProvider;

    private readonly Func<string?> userName;

    /// <summary>
    /// Creates the provider the API registers, whose logon name is <see cref="Environment.UserName"/>, read on every
    /// call.
    /// </summary>
    /// <param name="timeProvider">The clock whose local time stamps each write.</param>
    /// <exception cref="ArgumentNullException"><paramref name="timeProvider"/> is <see langword="null"/>.</exception>
    public SaveStampProvider(TimeProvider timeProvider)
        : this(timeProvider, static () => Environment.UserName)
    {
    }

    /// <summary>
    /// Creates a provider that reads the logon name from <paramref name="userName"/>, so tests can stand in for a
    /// process whose OS user has no name. Dependency injection sees only the public constructor.
    /// </summary>
    /// <param name="timeProvider">The clock whose local time stamps each write.</param>
    /// <param name="userName">The source of the logon name, called once per stamp.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="timeProvider"/> or <paramref name="userName"/> is <see langword="null"/>.
    /// </exception>
    internal SaveStampProvider(TimeProvider timeProvider, Func<string?> userName)
    {
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.userName = userName ?? throw new ArgumentNullException(nameof(userName));
    }

    /// <summary>Creates the stamp for one write operation, reading the user name and the clock on each call.</summary>
    /// <returns>
    /// A stamp whose logon name is the process user name and whose time is the provider's local time with its
    /// sub-second part removed.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// The process user name is <see langword="null"/>, empty or whitespace. The message is fixed text.
    /// </exception>
    public SaveStamp Create()
    {
        var logonName = userName();
        if (string.IsNullOrWhiteSpace(logonName))
        {
            throw new InvalidOperationException(NoUserNameMessage);
        }

        // Local time, as legacy's DateTime.Now, and never converted to UTC: DateTimeOffset.DateTime has an
        // Unspecified kind, which truncation keeps. Whole seconds are what legacy's DateTime.Now.ToString() round trip
        // stored, and the stamp refuses anything finer.
        var time = SaveStamp.TruncateToSeconds(timeProvider.GetLocalNow().DateTime);

        return new SaveStamp(logonName, time);
    }
}
