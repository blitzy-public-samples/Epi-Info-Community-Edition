using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

namespace EpiInfo.Domain.Events;

/// <summary>
/// A check-code event the browser raises on one field. Each kind selects one section of the field's
/// <c>Field &lt;name&gt;</c> … <c>End-Field</c> block in the form's check-code program.
/// </summary>
/// <remarks>
/// The member names are the exact wire spellings of the <c>event</c> property of
/// <c>POST /api/forms/{viewId}/checkcode/field-event</c>; parse them only with
/// <see cref="FieldEventKindNames.TryParseExact"/>. The numeric values are not part of the wire contract.
/// View, page, record and navigation events are not field events and have no member here.
/// </remarks>
public enum FieldEventKind
{
    /// <summary>
    /// Raised when an input gains focus; selects the field's <c>Before</c> … <c>End-Before</c> block.
    /// </summary>
    FieldBefore = 0,

    /// <summary>
    /// Raised when an input loses focus; selects the field's <c>After</c> … <c>End-After</c> block.
    /// </summary>
    FieldAfter = 1,

    /// <summary>
    /// Raised when a checkbox or radio button changes, after its value has toggled; selects the field's
    /// <c>Click</c> … <c>End-Click</c> block.
    /// </summary>
    FieldClick = 2,
}

/// <summary>
/// Wire spellings and check-code block keywords of <see cref="FieldEventKind"/>, and the strict parser for the
/// <c>event</c> property of a field-event request.
/// </summary>
public static class FieldEventKindNames
{
    private static readonly ReadOnlyCollection<string> AllWireNames = Array.AsReadOnly(
    [
        nameof(FieldEventKind.FieldBefore),
        nameof(FieldEventKind.FieldAfter),
        nameof(FieldEventKind.FieldClick),
    ]);

    /// <summary>
    /// The three wire spellings, <c>FieldBefore</c>, <c>FieldAfter</c> and <c>FieldClick</c>, in
    /// <see cref="FieldEventKind"/> order. The list cannot be modified.
    /// </summary>
    public static IReadOnlyList<string> WireNames => AllWireNames;

    /// <summary>
    /// Parses a wire spelling. Only <c>FieldBefore</c>, <c>FieldAfter</c> and <c>FieldClick</c>, compared
    /// ordinally, are accepted.
    /// </summary>
    /// <param name="value">The <c>event</c> value as received, or <see langword="null"/> when it is missing.</param>
    /// <param name="kind">
    /// The parsed kind when the method returns <see langword="true"/>; otherwise <see langword="default"/>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="value"/> is exactly one of the three wire spellings;
    /// <see langword="false"/> for <see langword="null"/>, empty, padded or differently cased text, numeric
    /// strings, comma-separated lists and any other text.
    /// </returns>
    public static bool TryParseExact([NotNullWhen(true)] string? value, out FieldEventKind kind)
    {
        // Enum.TryParse is deliberately not used: it accepts numeric strings such as "1" and comma-combined
        // names such as "FieldBefore,FieldAfter", which the wire contract rejects. A string switch compares
        // ordinally and sends null to the default branch.
        switch (value)
        {
            case nameof(FieldEventKind.FieldBefore):
                kind = FieldEventKind.FieldBefore;
                return true;
            case nameof(FieldEventKind.FieldAfter):
                kind = FieldEventKind.FieldAfter;
                return true;
            case nameof(FieldEventKind.FieldClick):
                kind = FieldEventKind.FieldClick;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    /// <summary>Returns the wire spelling of <paramref name="kind"/>.</summary>
    /// <param name="kind">A defined field-event kind.</param>
    /// <returns><c>FieldBefore</c>, <c>FieldAfter</c> or <c>FieldClick</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is not a defined member.</exception>
    public static string ToWireName(this FieldEventKind kind) => kind switch
    {
        FieldEventKind.FieldBefore => nameof(FieldEventKind.FieldBefore),
        FieldEventKind.FieldAfter => nameof(FieldEventKind.FieldAfter),
        FieldEventKind.FieldClick => nameof(FieldEventKind.FieldClick),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "The value is not a defined field-event kind."),
    };

    /// <summary>
    /// Returns the canonical spelling of the check-code block keyword that <paramref name="kind"/> selects.
    /// </summary>
    /// <remarks>
    /// Check-code keywords are case-insensitive, so a parser matching a block against this keyword compares
    /// without regard to case; this method only supplies the canonical spelling.
    /// </remarks>
    /// <param name="kind">A defined field-event kind.</param>
    /// <returns><c>Before</c>, <c>After</c> or <c>Click</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is not a defined member.</exception>
    public static string ToBlockKeyword(this FieldEventKind kind) => kind switch
    {
        FieldEventKind.FieldBefore => "Before",
        FieldEventKind.FieldAfter => "After",
        FieldEventKind.FieldClick => "Click",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "The value is not a defined field-event kind."),
    };
}
