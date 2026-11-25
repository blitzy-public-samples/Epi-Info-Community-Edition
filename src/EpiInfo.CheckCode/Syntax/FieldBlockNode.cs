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
/// The event blocks present, in source order, copied into an immutable snapshot. As the grammar allows, it holds at
/// most one block of each kind, in the order <c>Before</c>, <c>After</c>, <c>Click</c>, and the node rejects any
/// other list; the parser reports a section that repeats a kind or follows a later kind as a warning and skips it,
/// so the tree never holds it. An absent block has no entry, an empty block such as <c>After End-After</c> is an
/// entry with no statements, and a field block without event blocks has an empty list.
/// </param>
/// <exception cref="ArgumentNullException"><paramref name="Events"/> is <see langword="null"/>.</exception>
/// <exception cref="ArgumentException">
/// <paramref name="Events"/> contains a <see langword="null"/> element, or its blocks are not in strictly
/// increasing <c>Before</c>, <c>After</c>, <c>Click</c> order, at most one of each kind.
/// </exception>
public sealed record FieldBlockNode(string FieldName, int Line, IReadOnlyList<EventBlockNode> Events)
{
    private readonly ImmutableList<EventBlockNode> events =
        InGrammarOrder(SyntaxLists.Snapshot(Events, nameof(Events)));

    /// <summary>
    /// The event blocks in source order, at most one of each kind in <c>Before</c>, <c>After</c>, <c>Click</c>
    /// order: an immutable snapshot that later changes to the caller's list or array never reach.
    /// </summary>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The value contains a <see langword="null"/> element, or its blocks are not in strictly increasing
    /// <c>Before</c>, <c>After</c>, <c>Click</c> order.
    /// </exception>
    public IReadOnlyList<EventBlockNode> Events
    {
        get => events;
        // Copied and checked here as well as in the constructor, so a `with` expression can neither alias a mutable
        // list nor bring in a block order the grammar does not allow.
        init => events = InGrammarOrder(SyntaxLists.Snapshot(value, nameof(Events)));
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

    /// <summary>
    /// Returns <paramref name="snapshot"/> once each block's kind is past the previous block's, in the order of the
    /// <c>Field</c> production (grammar lines 620-627): <c>Before</c>, then <c>After</c>, then <c>Click</c>.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// A block repeats a kind, follows a later kind, or has a kind that is not a field event.
    /// </exception>
    private static ImmutableList<EventBlockNode> InGrammarOrder(ImmutableList<EventBlockNode> snapshot)
    {
        var previousSlot = -1;
        foreach (EventBlockNode block in snapshot)
        {
            var slot = block.Event switch
            {
                FieldEventKind.FieldBefore => 0,
                FieldEventKind.FieldAfter => 1,
                FieldEventKind.FieldClick => 2,
                _ => throw new ArgumentException(
                    $"{nameof(Events)} must hold only Before, After and Click blocks.",
                    nameof(Events)),
            };

            if (slot <= previousSlot)
            {
                throw new ArgumentException(
                    $"{nameof(Events)} must hold at most one Before, After and Click block, in that order.",
                    nameof(Events));
            }

            previousSlot = slot;
        }

        return snapshot;
    }
}

/// <summary>
/// One event block of a <c>Field</c> block: <c>Before</c> … <c>End-Before</c>, <c>After</c> … <c>End-After</c> or
/// <c>Click</c> … <c>End-Click</c> (grammar lines 620-622). Each wraps a statement list, which may be empty.
/// </summary>
/// <remarks>
/// <see cref="Statements"/> and <see cref="Diagnostics"/> are immutable snapshots taken by the constructor and by a
/// <see langword="with"/> expression, so later changes to the caller's lists or arrays never reach the node.
/// Equality compares <see cref="Event"/>, <see cref="Line"/>, and both lists element by element. A
/// <see cref="Diagnostic"/> holds only scalars, so its elements compare by value. The internal
/// <see cref="DiagnosticAnchors"/> are not compared: they place the diagnostics among the statements for the
/// evaluator and add nothing to what the block holds.
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
/// merged in source order with the runtime errors the evaluator adds. The same diagnostics also appear in
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

    private ImmutableArray<int> diagnosticAnchors = [];

    /// <summary>
    /// The supported statements in source order: an immutable snapshot that later changes to the caller's list or
    /// array never reach.
    /// </summary>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The value contains a <see langword="null"/> element.</exception>
    public IReadOnlyList<StatementNode> Statements
    {
        get => statements;
        // Copied here as well as in the constructor, so a `with` expression cannot alias a mutable list. The anchors
        // count the replaced statements, so they no longer apply.
        init
        {
            statements = SyntaxLists.Snapshot(value, nameof(Statements));
            diagnosticAnchors = [];
        }
    }

    /// <summary>The block's static diagnostics in source order, as an immutable snapshot.</summary>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The value contains a <see langword="null"/> element.</exception>
    public IReadOnlyList<Diagnostic> Diagnostics
    {
        get => diagnostics;
        // The anchors place the replaced diagnostics, so they no longer apply.
        init
        {
            diagnostics = SyntaxLists.Snapshot(value, nameof(Diagnostics));
            diagnosticAnchors = [];
        }
    }

    /// <summary>
    /// Where each entry of <see cref="Diagnostics"/>, at the same index, stands among <see cref="Statements"/>: the
    /// number of the block's statements that start before it in the source, counted in pre-order (each statement,
    /// then its <c>THEN</c> branch, then its <c>ELSE</c> branch, as <see cref="PreOrderSize(StatementNode)"/>
    /// counts them). The evaluator adds a diagnostic just before the first statement it runs whose pre-order index
    /// is at least the anchor, or after the block when it runs none, so static diagnostics and runtime errors reach
    /// the response in source order. An immutable snapshot of the value.
    /// </summary>
    /// <remarks>
    /// The parser sets it. A block built without it, or whose anchors do not cover <see cref="Diagnostics"/>, has
    /// every diagnostic at anchor 0, so they are all added before its first statement runs. A <see langword="with"/>
    /// expression that replaces <see cref="Statements"/> or <see cref="Diagnostics"/> clears it.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    internal IReadOnlyList<int> DiagnosticAnchors
    {
        get => diagnosticAnchors;
        init
        {
            ArgumentNullException.ThrowIfNull(value, nameof(DiagnosticAnchors));
            diagnosticAnchors = [.. value];
        }
    }

    /// <summary>
    /// The anchor of the diagnostic at <paramref name="diagnosticIndex"/> in <see cref="Diagnostics"/>, as
    /// <see cref="DiagnosticAnchors"/> describes it; 0 when the anchors do not cover <see cref="Diagnostics"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="diagnosticIndex"/> is not an index of <see cref="Diagnostics"/>.
    /// </exception>
    internal int AnchorOf(int diagnosticIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(diagnosticIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(diagnosticIndex, diagnostics.Count);
        return diagnosticAnchors.Length == diagnostics.Count ? diagnosticAnchors[diagnosticIndex] : 0;
    }

    /// <summary>
    /// How many statements <paramref name="statement"/> takes in the pre-order numbering of
    /// <see cref="DiagnosticAnchors"/>: one for itself, plus, for an <c>IF</c>, those of its <c>THEN</c> and
    /// <c>ELSE</c> branches. Counted without recursion, so no depth of nesting can exhaust the call stack.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="statement"/> is <see langword="null"/>.</exception>
    internal static int PreOrderSize(StatementNode statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        return statement is IfStatementNode ? CountPreOrder([statement]) : 1;
    }

    /// <summary>
    /// The pre-order size of a statement list: the sum of its statements' sizes. Counted without recursion, so no
    /// depth of nesting can exhaust the call stack.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="statements"/> or one of its elements is <see langword="null"/>.
    /// </exception>
    internal static int PreOrderSize(IReadOnlyList<StatementNode> statements)
    {
        ArgumentNullException.ThrowIfNull(statements);
        return CountPreOrder(statements);
    }

    // Counts every statement of the list and of the THEN and ELSE branches beneath it, keeping the branches still to
    // count on an explicit stack: memory grows with the tree, never the call stack. A count does not depend on order.
    private static int CountPreOrder(IReadOnlyList<StatementNode> statements)
    {
        var size = 0;
        var pending = new Stack<IReadOnlyList<StatementNode>>();
        pending.Push(statements);
        while (pending.TryPop(out IReadOnlyList<StatementNode>? list))
        {
            foreach (StatementNode statement in list)
            {
                ArgumentNullException.ThrowIfNull(statement);
                size++;
                if (statement is IfStatementNode ifStatement)
                {
                    pending.Push(ifStatement.Then);
                    if (ifStatement.Else is { } elseBranch)
                    {
                        pending.Push(elseBranch);
                    }
                }
            }
        }

        return size;
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
