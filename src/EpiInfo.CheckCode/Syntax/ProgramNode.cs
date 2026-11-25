using System.Collections.Immutable;
using EpiInfo.Domain.Diagnostics;

namespace EpiInfo.CheckCode.Syntax;

/// <summary>
/// The root of a parsed <c>metaViews.CheckCode</c> program: the grammar's
/// <c>&lt;Program&gt; ::= &lt;CheckCodeBlocks&gt; | !Eof</c> (grammar line 56), so a program with no blocks is
/// valid and is <see cref="Empty"/>.
/// </summary>
/// <remarks>
/// <para>
/// <c>ICheckCodeEngine.Analyze</c> returns <see cref="Diagnostics"/> unchanged; it is the form definition's
/// <c>unsupportedCheckCode</c>. <c>ICheckCodeEngine.Run</c> searches <see cref="FieldBlocks"/> for the block of the
/// event's <c>fieldName</c> and the <see cref="EventBlockNode"/> of the event raised, and evaluates only that block.
/// </para>
/// <para>
/// <see cref="FieldBlocks"/> and <see cref="Diagnostics"/> are immutable snapshots taken by the constructor and by a
/// <see langword="with"/> expression, so later changes to the caller's lists or arrays never reach the node.
/// Equality compares both lists element by element, so a parsed tree equals a separately built expected tree and
/// <c>new ProgramNode([], [])</c> equals <see cref="Empty"/>.
/// </para>
/// </remarks>
/// <param name="FieldBlocks">
/// Every <c>Field</c> block in source order, copied into an immutable snapshot. Two blocks for one field name are
/// both kept; which one runs is the engine's decision, not the parser's. The other top-level blocks the grammar
/// allows (<c>View</c> or <c>Form</c>, <c>Record</c>, <c>Page</c>, <c>DefineVariables</c>, <c>Sub</c>) are outside the
/// supported subset and have no node; each is reported in <paramref name="Diagnostics"/> instead.
/// </param>
/// <param name="Diagnostics">
/// Every static diagnostic of the whole program in source order, copied into an immutable snapshot: a
/// <see cref="DiagnosticSeverity.Warning"/> for each unsupported command, function, operator, literal, block,
/// <c>ELSE-IF</c> or run of tokens that cannot be resynchronised, and a <see cref="DiagnosticSeverity.Info"/> for each
/// <c>GEOCODE</c>. It holds every <see cref="EventBlockNode.Diagnostics"/> entry, plus those raised outside any event
/// block: an unsupported top-level block, text between blocks, and text inside a <c>Field</c> block but outside its
/// <c>Before</c>, <c>After</c> and <c>Click</c> blocks. It never holds a <see cref="DiagnosticSeverity.Error"/>:
/// runtime errors arise only while <c>Run</c> evaluates a block, and reading a form definition never runs the
/// program.
/// </param>
/// <exception cref="ArgumentNullException">
/// <paramref name="FieldBlocks"/> or <paramref name="Diagnostics"/> is <see langword="null"/>.
/// </exception>
/// <exception cref="ArgumentException">
/// <paramref name="FieldBlocks"/> or <paramref name="Diagnostics"/> contains a <see langword="null"/> element.
/// </exception>
public sealed record ProgramNode(IReadOnlyList<FieldBlockNode> FieldBlocks, IReadOnlyList<Diagnostic> Diagnostics)
{
    private readonly ImmutableList<FieldBlockNode> fieldBlocks =
        SyntaxLists.Snapshot(FieldBlocks, nameof(FieldBlocks));

    private readonly ImmutableList<Diagnostic> diagnostics = SyntaxLists.Snapshot(Diagnostics, nameof(Diagnostics));

    /// <summary>
    /// The program with no blocks and no diagnostics: the parse of a <see langword="null"/>, blank or comment-only
    /// program.
    /// </summary>
    public static ProgramNode Empty { get; } = new([], []);

    /// <summary>
    /// Every <c>Field</c> block in source order: an immutable snapshot that later changes to the caller's list or
    /// array never reach.
    /// </summary>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The value contains a <see langword="null"/> element.</exception>
    public IReadOnlyList<FieldBlockNode> FieldBlocks
    {
        get => fieldBlocks;
        // Copied here as well as in the constructor, so a `with` expression cannot alias a mutable list.
        init => fieldBlocks = SyntaxLists.Snapshot(value, nameof(FieldBlocks));
    }

    /// <summary>The program's static diagnostics in source order, as an immutable snapshot.</summary>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The value contains a <see langword="null"/> element.</exception>
    public IReadOnlyList<Diagnostic> Diagnostics
    {
        get => diagnostics;
        init => diagnostics = SyntaxLists.Snapshot(value, nameof(Diagnostics));
    }

    /// <inheritdoc />
    public bool Equals(ProgramNode? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (other is null)
        {
            return false;
        }

        return SyntaxEquality.ListEquals(FieldBlocks, other.FieldBlocks)
            && SyntaxEquality.ListEquals(Diagnostics, other.Diagnostics);
    }

    /// <inheritdoc />
    public override int GetHashCode() =>
        HashCode.Combine(SyntaxEquality.ListHash(FieldBlocks), SyntaxEquality.ListHash(Diagnostics));
}
