using System.Collections.ObjectModel;
using EpiInfo.Domain.Diagnostics;
using EpiInfo.Domain.Values;

namespace EpiInfo.Domain.Events;

/// <summary>
/// The effects of one check-code field event: what <c>POST /api/forms/{viewId}/checkcode/field-event</c> returns
/// and the client applies to the open record. On the wire it is
/// <c>{enabled, disabled, cleared, assigned, dialogs, diagnostics}</c>.
/// </summary>
/// <remarks>
/// <para>
/// Each request is evaluated on its own, so the effects of the executed block travel back as data instead of being
/// applied to the screen by the interpreter. Nothing here is written to the database.
/// </para>
/// <para>
/// The type keeps the order it is given and enforces no membership rule. Ordering each list by a field's first
/// occurrence, keeping <see cref="Dialogs"/> in execution order and <see cref="Diagnostics"/> in source order, and
/// placing a field in at most one of <see cref="Enabled"/>/<see cref="Disabled"/> and one of
/// <see cref="Cleared"/>/<see cref="Assigned"/>, are the interpreter's job before it constructs the response.
/// </para>
/// <para>
/// Every member is a read-only copy taken by the constructor and is never <see langword="null"/>, so a response
/// cannot change after construction and one instance can be shared across threads.
/// </para>
/// <para>
/// The declaration order of the six properties fixes the JSON property order, and the empty envelope
/// <c>{"enabled":[],"disabled":[],"cleared":[],"assigned":{},"dialogs":[],"diagnostics":[]}</c> is part of the wire
/// contract. The type therefore exposes no other public instance property, because the serialiser would write it.
/// Property names and value spellings come from the API's JSON configuration, so the type carries no serialisation
/// attribute. The constructor's parameters match the properties by name and type, which lets the serialiser read a
/// response back through it.
/// </para>
/// <para>
/// The synthesised record equality compares the six collections by reference, so two responses with the same
/// content built separately are not equal; compare the members to test content.
/// </para>
/// </remarks>
public sealed record FieldEventResponse
{
    private const string BlankFieldNameMessage = "A field name must not be null or empty.";
    private const string DuplicateAssignedFieldMessage = "Each assigned field name must be unique.";
    private const string NullDialogMessage = "A dialog entry must not be null.";
    private const string NullDiagnosticMessage = "A diagnostic must not be null.";

    /// <summary>Creates a response from the effects of an executed event block.</summary>
    /// <param name="enabled">The fields check code enabled, in the order given.</param>
    /// <param name="disabled">The fields check code disabled, in the order given.</param>
    /// <param name="cleared">The fields check code cleared, in the order given.</param>
    /// <param name="assigned">
    /// The fields check code assigned, with their values. Entries are kept in the source's enumeration order, and
    /// names are compared ordinally: two canonical names that differ only by case are two distinct fields.
    /// </param>
    /// <param name="dialogs">The dialogs to show, in the order given.</param>
    /// <param name="diagnostics">The diagnostics of the executed block, in the order given.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// A field name in <paramref name="enabled"/>, <paramref name="disabled"/>, <paramref name="cleared"/> or the
    /// keys of <paramref name="assigned"/> is <see langword="null"/> or empty; <paramref name="assigned"/> enumerates
    /// the same key twice; or an entry of <paramref name="dialogs"/> or
    /// <paramref name="diagnostics"/> is <see langword="null"/>.
    /// </exception>
    public FieldEventResponse(
        IReadOnlyList<string> enabled,
        IReadOnlyList<string> disabled,
        IReadOnlyList<string> cleared,
        IReadOnlyDictionary<string, FieldValue> assigned,
        IReadOnlyList<DialogMessage> dialogs,
        IReadOnlyList<Diagnostic> diagnostics)
    {
        Enabled = CopyFieldNames(enabled, nameof(enabled));
        Disabled = CopyFieldNames(disabled, nameof(disabled));
        Cleared = CopyFieldNames(cleared, nameof(cleared));
        Assigned = CopyAssigned(assigned, nameof(assigned));
        Dialogs = CopyEntries(dialogs, nameof(dialogs), NullDialogMessage);
        Diagnostics = CopyEntries(diagnostics, nameof(diagnostics), NullDiagnosticMessage);
    }

    /// <summary>
    /// The shared response with no effects and no diagnostics, returned for a field with no block for the event
    /// and for an empty program. It serialises to
    /// <c>{"enabled":[],"disabled":[],"cleared":[],"assigned":{},"dialogs":[],"diagnostics":[]}</c>.
    /// </summary>
    public static FieldEventResponse Empty { get; } =
        new([], [], [], ReadOnlyDictionary<string, FieldValue>.Empty, [], []);

    /// <summary>
    /// The canonical names of the fields check code enabled. The client lifts only the check-code disable on each,
    /// so a field protected by read-only metadata, a deleted record or an unsupported type stays disabled.
    /// </summary>
    public IReadOnlyList<string> Enabled { get; }

    /// <summary>The canonical names of the fields check code disabled.</summary>
    public IReadOnlyList<string> Disabled { get; }

    /// <summary>The canonical names of the fields check code cleared; the client sets each value to empty.</summary>
    public IReadOnlyList<string> Cleared { get; }

    /// <summary>
    /// The values check code assigned, keyed by canonical field name, compared ordinally, and enumerated in
    /// assignment order.
    /// <see cref="FieldValue.Null"/> is an assigned empty value, as <c>ASSIGN X = (.)</c> produces. A read-only
    /// field may appear here: the client shows its value and never saves it.
    /// </summary>
    public IReadOnlyDictionary<string, FieldValue> Assigned { get; }

    /// <summary>The <c>DIALOG</c> messages to show, one modal each, in list order.</summary>
    public IReadOnlyList<DialogMessage> Dialogs { get; }

    /// <summary>
    /// The diagnostics of the executed block: its unsupported constructs, skipped <c>GEOCODE</c> statements and
    /// runtime errors. A runtime error keeps the effects of the statements before it and never blocks a save.
    /// </summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    private static IReadOnlyList<string> CopyFieldNames(IReadOnlyList<string> source, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(source, parameterName);

        // Validate the snapshot rather than the source, so what is checked is exactly what is kept.
        var copy = source.ToArray();
        foreach (var name in copy)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException(BlankFieldNameMessage, parameterName);
            }
        }

        return Freeze(copy);
    }

    private static IReadOnlyList<T> CopyEntries<T>(IReadOnlyList<T> source, string parameterName, string nullEntryMessage)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(source, parameterName);

        var copy = source.ToArray();
        foreach (var entry in copy)
        {
            if (entry is null)
            {
                throw new ArgumentException(nullEntryMessage, parameterName);
            }
        }

        return Freeze(copy);
    }

    private static IReadOnlyDictionary<string, FieldValue> CopyAssigned(
        IReadOnlyDictionary<string, FieldValue> source,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(source, parameterName);

        // OrderedDictionary, not Dictionary: the JSON key order of "assigned" is the enumeration order, and only an
        // ordered dictionary guarantees that it stays the order the entries were given in.
        var ordered = new OrderedDictionary<string, FieldValue>(source.Count, StringComparer.Ordinal);
        foreach (var (name, value) in source)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException(BlankFieldNameMessage, parameterName);
            }

            if (!ordered.TryAdd(name, value))
            {
                throw new ArgumentException(DuplicateAssignedFieldMessage, parameterName);
            }
        }

        return ordered.Count == 0
            ? ReadOnlyDictionary<string, FieldValue>.Empty
            : new ReadOnlyDictionary<string, FieldValue>(ordered);
    }

    private static IReadOnlyList<T> Freeze<T>(T[] copy) =>
        copy.Length == 0 ? ReadOnlyCollection<T>.Empty : Array.AsReadOnly(copy);
}
