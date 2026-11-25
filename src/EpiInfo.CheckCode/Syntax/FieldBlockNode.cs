using System.Collections.Immutable;
using EpiInfo.Domain.Diagnostics;
using EpiInfo.Domain.Events;

namespace EpiInfo.CheckCode.Syntax;

/// <summary>
/// A <c>Field &lt;name&gt;</c> … <c>End-Field</c> block (grammar lines 620-627): the check code one field runs on
/// its <c>Before</c>, <c>After</c> and <c>Click</c> events.
/// </summary>
/// <remarks>
/// <para>
/// A program may hold two <c>Field</c> blocks for the same name. The tree keeps both, in source order; which one
/// runs is the engine's decision, not the parser's. Legacy registration replaced the earlier block: running each
/// block removed whatever was registered under the name and then added itself, so the last block won.
/// </para>
/// <para>
/// <see cref="Events"/> is an immutable snapshot taken by the constructor and by a <see langword="with"/>
/// expression, so later changes to the caller's list or array never reach the node. Equality compares
/// <see cref="FieldName"/> ordinally, <see cref="Line"/>, and <see cref="Events"/> element by element, so a parsed
/// tree equals a separately built expected tree.
/// </para>
/// </remarks>
/// <param name="FieldName">
/// The field name as written, with any surrounding <c>[</c> and <c>]</c> removed and its case preserved. It is
/// matched case-insensitively to a form field at run time, never here: <c>Field dob</c> still names field
/// <c>DOB</c>.
/// </param>
/// <param name="Line">
/// The 1-based line, in the form's <c>metaViews.CheckCode</c> text, of the <c>Field</c> keyword.
/// </param>
/// <param name="Events">
/// The event blocks present, in source order, copied into an immutable snapshot. The grammar allows at most one
/// block of each kind, in the order <c>Before</c>, <c>After</c>, <c>Click</c>; the node does not enforce that, so
/// a program departing from it keeps every block it holds, in source order. An absent block has no entry, an empty
/// block such as <c>After End-After</c> is an entry with no statements, and a field block without event blocks has
/// an empty list.
/// </param>
/// <exception cref="ArgumentNullException"><paramref name="Events"/> is <see langword="null"/>.</exception>
/// <exception cref="ArgumentException"><paramref name="Events"/> contains a <see langword="null"/> element.</exception>
public sealed record FieldBlockNode(string FieldName, int Line, IReadOnlyList<EventBlockNode> Events)
{
    private readonly ImmutableList<EventBlockNode> events = SyntaxLists.Snapshot(Events, nameof(Events));

    /// <summary>
    /// The event blocks in source order: an immutable snapshot that later changes to the caller's list or array
    /// never reach.
    /// </summary>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The value contains a <see langword="null"/> element.</exception>
    public IReadOnlyList<EventBlockNode> Events
    {
        get => events;
        // Copied here as well as in the constructor, so a `with` expression cannot alias a mutable list.
        init => events = SyntaxLists.Snapshot(value, nameof(Events));
    }

    /// <inheritdoc />
    public bool Equals(FieldBlockNode? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (other is null)
        {
            return false;
        }

        return string.Equals(FieldName, other.FieldName, StringComparison.Ordinal)
            && Line == other.Line
            && SyntaxEquality.ListEquals(Events, other.Events);
    }

    /// <inheritdoc />
    public override int GetHashCode() =>
        HashCode.Combine(FieldName, Line, SyntaxEquality.ListHash(Events));
}

/// <summary>
/// One event block of a <c>Field</c> block: <c>Before</c> … <c>End-Before</c>, <c>After</c> … <c>End-After</c> or
/// <c>Click</c> … <c>End-Click</c> (grammar lines 620-622). Each wraps a statement list, which may be empty.
/// </summary>
/// <remarks>
/// <see cref="Statements"/> and <see cref="Diagnostics"/> are immutable snapshots taken by the constructor and by a
/// <see langword="with"/> expression, so later changes to the caller's lists or arrays never reach the node.
/// Equality compares <see cref="Event"/>, <see cref="Line"/>, and both lists element by element. A
/// <see cref="Diagnostic"/> holds only scalars, so its elements compare by value.
/// </remarks>
/// <param name="Event">
/// The event the block answers: <c>Before</c> is <see cref="FieldEventKind.FieldBefore"/>, <c>After</c> is
/// <see cref="FieldEventKind.FieldAfter"/> and <c>Click</c> is <see cref="FieldEventKind.FieldClick"/>, the mapping
/// <see cref="FieldEventKindNames.ToBlockKeyword(FieldEventKind)"/> gives in the other direction.
/// </param>
/// <param name="Line">
/// The 1-based line, in the form's <c>metaViews.CheckCode</c> text, of the <c>Before</c>, <c>After</c> or
/// <c>Click</c> keyword.
/// </param>
/// <param name="Statements">
/// The supported statements in source order, copied into an immutable snapshot. Unsupported statements are absent:
/// the parser omits each one and reports it in <paramref name="Diagnostics"/>.
/// </param>
/// <param name="Diagnostics">
/// The static diagnostics whose line falls inside this block, in source order, copied into an immutable snapshot:
/// a <see cref="DiagnosticSeverity.Warning"/> for each unsupported construct and a
/// <see cref="DiagnosticSeverity.Info"/> for each <c>GEOCODE</c>. It never holds a
/// <see cref="DiagnosticSeverity.Error"/>. <c>ICheckCodeEngine.Run</c> returns these for the block it executes,
/// followed by the runtime errors the evaluator adds. The same diagnostics also appear in
/// <c>ProgramNode.Diagnostics</c>.
/// </param>
/// <exception cref="ArgumentNullException">
/// <paramref name="Statements"/> or <paramref name="Diagnostics"/> is <see langword="null"/>.
/// </exception>
/// <exception cref="ArgumentException">
/// <paramref name="Statements"/> or <paramref name="Diagnostics"/> contains a <see langword="null"/> element.
/// </exception>
public sealed record EventBlockNode(
    FieldEventKind Event,
    int Line,
    IReadOnlyList<StatementNode> Statements,
    IReadOnlyList<Diagnostic> Diagnostics)
{
    private readonly ImmutableList<StatementNode> statements = SyntaxLists.Snapshot(Statements, nameof(Statements));

    private readonly ImmutableList<Diagnostic> diagnostics = SyntaxLists.Snapshot(Diagnostics, nameof(Diagnostics));

    /// <summary>
    /// The supported statements in source order: an immutable snapshot that later changes to the caller's list or
    /// array never reach.
    /// </summary>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The value contains a <see langword="null"/> element.</exception>
    public IReadOnlyList<StatementNode> Statements
    {
        get => statements;
        // Copied here as well as in the constructor, so a `with` expression cannot alias a mutable list.
        init => statements = SyntaxLists.Snapshot(value, nameof(Statements));
    }

    /// <summary>The block's static diagnostics in source order, as an immutable snapshot.</summary>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The value contains a <see langword="null"/> element.</exception>
    public IReadOnlyList<Diagnostic> Diagnostics
    {
        get => diagnostics;
        init => diagnostics = SyntaxLists.Snapshot(value, nameof(Diagnostics));
    }

    /// <inheritdoc />
    public bool Equals(EventBlockNode? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (other is null)
        {
            return false;
        }

        return Event == other.Event
            && Line == other.Line
            && SyntaxEquality.ListEquals(Statements, other.Statements)
            && SyntaxEquality.ListEquals(Diagnostics, other.Diagnostics);
    }

    /// <inheritdoc />
    public override int GetHashCode() =>
        HashCode.Combine(
            Event,
            Line,
            SyntaxEquality.ListHash(Statements),
            SyntaxEquality.ListHash(Diagnostics));
}
