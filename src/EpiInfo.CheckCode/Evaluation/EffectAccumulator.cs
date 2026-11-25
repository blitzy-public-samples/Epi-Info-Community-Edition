using EpiInfo.Domain.Diagnostics;
using EpiInfo.Domain.Events;
using EpiInfo.Domain.Values;

namespace EpiInfo.CheckCode.Evaluation;

/// <summary>
/// Collects the effects and diagnostics of one field-event run and builds its <see cref="FieldEventResponse"/>.
/// It is the only place the response's membership and ordering rules are applied: the evaluator reports each
/// <c>ENABLE</c>, <c>DISABLE</c>, <c>CLEAR</c>, <c>ASSIGN</c> and <c>DIALOG</c> effect here as it executes, and
/// never builds a response list itself.
/// </summary>
/// <remarks>
/// <para>
/// Legacy Enter applied each command straight to the on-screen controls, so the last command affecting a control
/// won. The response reproduces that final state as data, with a deterministic order:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Membership.</b> A field is in at most one of <see cref="FieldEventResponse.Enabled"/> and
/// <see cref="FieldEventResponse.Disabled"/>, and in at most one of <see cref="FieldEventResponse.Cleared"/> and
/// <see cref="FieldEventResponse.Assigned"/>. The last call affecting the field on each axis decides which. A later
/// <see cref="Assign"/> of the same field replaces its value.
/// </description></item>
/// <item><description>
/// <b>Order.</b> For each of the four lists, the first time each field entered that list is recorded and never
/// changed, whatever happens to the field afterwards. Each list, and the key order of
/// <see cref="FieldEventResponse.Assigned"/>, is sorted by that first entry among the fields whose final membership
/// is in it. Later calls decide membership, never order.
/// </description></item>
/// <item><description>
/// <see cref="FieldEventResponse.Dialogs"/> keep execution order, and <see cref="FieldEventResponse.Diagnostics"/>
/// keep source order: sorted by line, with diagnostics on the same line in the order they were added.
/// </description></item>
/// </list>
/// <para>Worked examples, each a sequence of calls and the response it produces:</para>
/// <list type="bullet">
/// <item><description>
/// <c>ENABLE A; ENABLE B; DISABLE A; ENABLE A</c> gives <c>enabled [A, B]</c> and <c>disabled []</c>, because A
/// first entered <c>enabled</c> before B.
/// </description></item>
/// <item><description><c>DISABLE A; ENABLE B; ENABLE A</c> gives <c>enabled [B, A]</c>.</description></item>
/// <item><description>
/// <c>CLEAR X; CLEAR Y; ASSIGN X = 1; CLEAR X</c> gives <c>cleared [X, Y]</c> and <c>assigned {}</c>.
/// </description></item>
/// <item><description>
/// <c>ASSIGN X = 1; ASSIGN Y = 2; CLEAR X; ASSIGN X = 3</c> gives <c>assigned {X: 3, Y: 2}</c> and
/// <c>cleared []</c>.
/// </description></item>
/// <item><description><c>CLEAR X; ASSIGN X = 1</c> gives <c>assigned {X: 1}</c> only.</description></item>
/// <item><description><c>ASSIGN X = 1; CLEAR X</c> gives <c>cleared [X]</c> only.</description></item>
/// </list>
/// <para>
/// Field names arrive canonical (<c>metaFields.Name</c>) from the evaluation context and are stored as given. They
/// are matched without regard to case, so two spellings of one field are one field, reported with the spelling of
/// its first call for that list.
/// </para>
/// <para>
/// One instance serves one evaluator run and holds no static state. An instance is not thread-safe; responses it
/// returns are immutable and independent of it, so it may keep accumulating after <see cref="ToResponse"/>.
/// </para>
/// </remarks>
public sealed class EffectAccumulator
{
    private const string NullDiagnosticMessage = "A diagnostic must not be null.";

    // The sequence number of the first time each field entered each list. Set once and never changed, so a later
    // call can move a field out of a list and back without changing where it sorts.
    private readonly Dictionary<string, long> _enabledFirstEntry = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _disabledFirstEntry = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _clearedFirstEntry = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _assignedFirstEntry = new(StringComparer.OrdinalIgnoreCase);

    // Final state per axis, decided by the last call: true is enabled, false disabled; Cleared, or assigned Value.
    private readonly Dictionary<string, bool> _enablement = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (bool Cleared, FieldValue Value)> _valueState =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly List<DialogMessage> _dialogs = [];
    private readonly List<Diagnostic> _diagnostics = [];

    private long _sequence;

    /// <summary>Records that check code enabled <paramref name="fieldName"/>.</summary>
    /// <param name="fieldName">The canonical field name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="fieldName"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="fieldName"/> is empty.</exception>
    public void Enable(string fieldName) => SetEnablement(fieldName, enabled: true);

    /// <summary>Records that check code disabled <paramref name="fieldName"/>.</summary>
    /// <param name="fieldName">The canonical field name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="fieldName"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="fieldName"/> is empty.</exception>
    public void Disable(string fieldName) => SetEnablement(fieldName, enabled: false);

    /// <summary>Records that check code cleared <paramref name="fieldName"/>, replacing any earlier assignment.</summary>
    /// <param name="fieldName">The canonical field name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="fieldName"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="fieldName"/> is empty.</exception>
    public void Clear(string fieldName)
    {
        ValidateFieldName(fieldName);
        RecordFirstEntry(_clearedFirstEntry, fieldName);
        _valueState[fieldName] = (Cleared: true, Value: FieldValue.Null);
    }

    /// <summary>
    /// Records that check code assigned <paramref name="value"/> to <paramref name="fieldName"/>, replacing any
    /// earlier assignment or clear of the field.
    /// </summary>
    /// <param name="fieldName">The canonical field name.</param>
    /// <param name="value">
    /// The value, already coerced to the field's type. <see cref="FieldValue.Null"/> is an assigned empty value, as
    /// <c>ASSIGN X = (.)</c> produces, and is reported in <see cref="FieldEventResponse.Assigned"/>, not as a clear.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="fieldName"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="fieldName"/> is empty.</exception>
    public void Assign(string fieldName, FieldValue value)
    {
        ValidateFieldName(fieldName);
        RecordFirstEntry(_assignedFirstEntry, fieldName);
        _valueState[fieldName] = (Cleared: false, Value: value);
    }

    /// <summary>Appends a <c>DIALOG</c> message; dialogs are reported in the order they were added.</summary>
    /// <param name="dialog">The message to show.</param>
    /// <exception cref="ArgumentNullException"><paramref name="dialog"/> is <see langword="null"/>.</exception>
    public void AddDialog(DialogMessage dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        _dialogs.Add(dialog);
    }

    /// <summary>
    /// Adds the parser's diagnostics of an executed event block. Call it before the block runs, so a static
    /// diagnostic precedes a runtime error reported on the same line.
    /// </summary>
    /// <param name="diagnostics">The block's static diagnostics.</param>
    /// <exception cref="ArgumentNullException"><paramref name="diagnostics"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// An entry of <paramref name="diagnostics"/> is <see langword="null"/>; nothing is added.
    /// </exception>
    public void AddStaticDiagnostics(IEnumerable<Diagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        // Checked on a snapshot so that a rejected batch adds nothing.
        var batch = diagnostics.ToArray();
        foreach (var diagnostic in batch)
        {
            if (diagnostic is null)
            {
                throw new ArgumentException(NullDiagnosticMessage, nameof(diagnostics));
            }
        }

        _diagnostics.AddRange(batch);
    }

    /// <summary>Adds a runtime diagnostic, such as the <see cref="DiagnosticSeverity.Error"/> of a failed statement.</summary>
    /// <param name="diagnostic">The diagnostic.</param>
    /// <exception cref="ArgumentNullException"><paramref name="diagnostic"/> is <see langword="null"/>.</exception>
    public void AddDiagnostic(Diagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        _diagnostics.Add(diagnostic);
    }

    /// <summary>
    /// Builds the response from everything recorded so far, applying the membership and ordering rules described on
    /// the type. Each call returns a new, immutable response.
    /// </summary>
    /// <returns>
    /// The response, or <see cref="FieldEventResponse.Empty"/> when nothing was recorded.
    /// </returns>
    public FieldEventResponse ToResponse()
    {
        if (_enablement.Count == 0 && _valueState.Count == 0 && _dialogs.Count == 0 && _diagnostics.Count == 0)
        {
            return FieldEventResponse.Empty;
        }

        // OrderedDictionary, not Dictionary: the key order of "assigned" is part of the response, and only an ordered
        // dictionary guarantees enumeration in insertion order.
        var assigned = new OrderedDictionary<string, FieldValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in FinalMembersInFirstEntryOrder(_assignedFirstEntry, field => !_valueState[field].Cleared))
        {
            assigned.Add(name, _valueState[name].Value);
        }

        return new FieldEventResponse(
            FinalMembersInFirstEntryOrder(_enabledFirstEntry, name => _enablement[name]),
            FinalMembersInFirstEntryOrder(_disabledFirstEntry, name => !_enablement[name]),
            FinalMembersInFirstEntryOrder(_clearedFirstEntry, name => _valueState[name].Cleared),
            assigned,
            _dialogs.ToArray(),

            // OrderBy is a stable sort, so diagnostics on one line keep the order they were added in.
            _diagnostics.OrderBy(diagnostic => diagnostic.Line).ToArray());
    }

    private void SetEnablement(string fieldName, bool enabled)
    {
        ValidateFieldName(fieldName);
        RecordFirstEntry(enabled ? _enabledFirstEntry : _disabledFirstEntry, fieldName);
        _enablement[fieldName] = enabled;
    }

    private void RecordFirstEntry(Dictionary<string, long> firstEntry, string fieldName)
    {
        _sequence++;
        firstEntry.TryAdd(fieldName, _sequence);
    }

    // Sorted by the recorded sequence numbers, which are unique, so the result never depends on the enumeration order
    // of the dictionary.
    private static string[] FinalMembersInFirstEntryOrder(
        Dictionary<string, long> firstEntry,
        Func<string, bool> isFinalMember) =>
        firstEntry
            .Where(entry => isFinalMember(entry.Key))
            .OrderBy(entry => entry.Value)
            .Select(entry => entry.Key)
            .ToArray();

    // An empty name is rejected here, not only when FieldEventResponse is built, so the failing call is the one
    // reported.
    private static void ValidateFieldName(string fieldName) =>
        ArgumentException.ThrowIfNullOrEmpty(fieldName, nameof(fieldName));
}
