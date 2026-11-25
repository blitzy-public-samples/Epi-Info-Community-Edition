namespace EpiInfo.CheckCode.Syntax;

/// <summary>
/// The kinds of literal the check-code subset evaluates. The grammar's <c>Boolean</c> terminal is
/// <c>(+)</c>, <c>(-)</c> or <c>(.)</c>; only <c>(-)</c> and <c>(.)</c> are in the subset, so <c>(+)</c>
/// has no member here and is only ever reported as an unsupported-construct diagnostic.
/// </summary>
public enum LiteralKind
{
    /// <summary>A double-quoted string literal; its content is in <see cref="LiteralNode.Text"/>.</summary>
    String,

    /// <summary>A numeric literal; its invariant-culture value is in <see cref="LiteralNode.Number"/>.</summary>
    Number,

    /// <summary>The missing-value literal <c>(.)</c>, which evaluates to no value.</summary>
    Missing,

    /// <summary>The unchecked literal <c>(-)</c>, which evaluates to boolean false.</summary>
    Unchecked,
}

/// <summary>
/// Base of the immutable expression tree the check-code parser produces and the evaluator walks.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Line"/> is the 1-based line, in the form's <c>metaViews.CheckCode</c> text, of the construct's
/// first token: the <c>NOT</c> keyword for <see cref="NotNode"/>, the left operand's first token for
/// <see cref="AndNode"/> and <see cref="EqualsNode"/>, and the function name for <see cref="FunctionCallNode"/>.
/// Nodes carry that line rather than a token, so the tree has no dependency on the lexer.
/// </para>
/// <para>
/// Precedence follows the grammar's expression ladder: <c>NOT</c> binds one comparison and <c>AND</c> binds
/// <c>NOT</c> terms, so the seeded <c>NOT FeverTemp = (.) AND Fever = (-)</c> is
/// <c>AndNode(NotNode(EqualsNode(IdentifierNode("FeverTemp"), LiteralNode Missing)),
/// EqualsNode(IdentifierNode("Fever"), LiteralNode Unchecked))</c>. Both productions are right-recursive, so
/// <c>a AND b AND c</c> is <c>AndNode(a, AndNode(b, c))</c> and <c>a = b = c</c> is
/// <c>EqualsNode(a, EqualsNode(b, c))</c>. Other seeded shapes: <c>Sex = "1"</c> is
/// <c>EqualsNode(IdentifierNode("Sex"), LiteralNode String "1")</c>, and <c>YEARS(DOB, DateofInterview)</c> is
/// <c>FunctionCallNode("YEARS", [IdentifierNode("DOB"), IdentifierNode("DateofInterview")])</c>.
/// </para>
/// <para>
/// There is deliberately no parenthesised-expression node: the parser unwraps <c>( expr )</c> to the inner
/// node, so expected trees in tests never contain a grouping level.
/// </para>
/// <para>
/// Constructs outside the subset have no node: <c>OR</c>, <c>XOR</c>, <c>&lt;&gt;</c>, <c>&lt;</c>, <c>&gt;</c>,
/// <c>&lt;=</c>, <c>&gt;=</c>, arithmetic, unary minus, <c>&amp;</c>, <c>LIKE</c>, <c>(+)</c>, date and time
/// literals, grid and qualified identifiers, and unregistered functions are reported as diagnostics and never
/// reach the tree.
/// </para>
/// </remarks>
/// <param name="Line">The 1-based source line of the construct's first token.</param>
public abstract record ExpressionNode(int Line);

/// <summary>
/// A literal value. Build it through <see cref="FromString"/>, <see cref="FromNumber"/>, <see cref="Missing"/>
/// or <see cref="Unchecked"/>, which keep <see cref="Text"/> and <see cref="Number"/> consistent with
/// <see cref="Kind"/>: only a <see cref="LiteralKind.String"/> has text and only a
/// <see cref="LiteralKind.Number"/> has a number.
/// </summary>
/// <param name="Kind">The literal's kind.</param>
/// <param name="Text">
/// For a string literal, its content with the surrounding double quotes removed and nothing else changed: no
/// trimming and no escape processing, because the grammar's <c>String</c> terminal has no escapes. Otherwise
/// <see langword="null"/>.
/// </param>
/// <param name="Number">For a numeric literal, its invariant-culture value; otherwise <see langword="null"/>.</param>
/// <param name="Line">The 1-based source line of the literal.</param>
public sealed record LiteralNode(LiteralKind Kind, string? Text, double? Number, int Line) : ExpressionNode(Line)
{
    /// <summary>Creates a string literal whose content, quotes already removed, is <paramref name="text"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    public static LiteralNode FromString(string text, int line)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new LiteralNode(LiteralKind.String, text, null, line);
    }

    /// <summary>Creates a numeric literal with the invariant-culture <paramref name="value"/>.</summary>
    public static LiteralNode FromNumber(double value, int line) =>
        new(LiteralKind.Number, null, value, line);

    /// <summary>Creates the missing-value literal <c>(.)</c>.</summary>
    public static LiteralNode Missing(int line) =>
        new(LiteralKind.Missing, null, null, line);

    /// <summary>Creates the unchecked literal <c>(-)</c>.</summary>
    public static LiteralNode Unchecked(int line) =>
        new(LiteralKind.Unchecked, null, null, line);
}

/// <summary>A reference to a field of the form.</summary>
/// <param name="Name">
/// The field name as written, with any surrounding <c>[</c> and <c>]</c> removed. It is resolved
/// case-insensitively to the canonical <c>metaFields.Name</c> at run time, not here: the seeded <c>AGE</c>
/// names the field <c>Age</c>.
/// </param>
/// <param name="Line">The 1-based source line of the identifier.</param>
public sealed record IdentifierNode(string Name, int Line) : ExpressionNode(Line);

/// <summary>
/// A call of a registered function (<c>YEARS</c> or <c>EPIWEEK</c>); any other function is a diagnostic and
/// never becomes a node. Equality compares <see cref="Arguments"/> element by element, so a parsed tree equals
/// a separately built expected tree.
/// </summary>
/// <param name="Name">The function name as written.</param>
/// <param name="Arguments">The argument expressions in source order.</param>
/// <param name="Line">The 1-based source line of the function name.</param>
public sealed record FunctionCallNode(string Name, IReadOnlyList<ExpressionNode> Arguments, int Line)
    : ExpressionNode(Line)
{
    /// <inheritdoc />
    public bool Equals(FunctionCallNode? other) =>
        other is not null
        && base.Equals(other)
        && string.Equals(Name, other.Name, StringComparison.Ordinal)
        && SyntaxEquality.ListEquals(Arguments, other.Arguments);

    /// <inheritdoc />
    public override int GetHashCode() =>
        HashCode.Combine(base.GetHashCode(), Name, SyntaxEquality.ListHash(Arguments));
}

/// <summary>The negation <c>NOT &lt;comparison&gt;</c>.</summary>
/// <param name="Operand">The negated comparison.</param>
/// <param name="Line">The 1-based source line of the <c>NOT</c> keyword.</param>
public sealed record NotNode(ExpressionNode Operand, int Line) : ExpressionNode(Line);

/// <summary>The conjunction <c>&lt;left&gt; AND &lt;right&gt;</c>; a chain nests to the right.</summary>
/// <param name="Left">The left operand, a <c>NOT</c> term.</param>
/// <param name="Right">The right operand, the rest of the chain.</param>
/// <param name="Line">The 1-based source line of the left operand's first token.</param>
public sealed record AndNode(ExpressionNode Left, ExpressionNode Right, int Line) : ExpressionNode(Line);

/// <summary>The comparison <c>&lt;left&gt; = &lt;right&gt;</c>; a chain nests to the right.</summary>
/// <param name="Left">The left operand.</param>
/// <param name="Right">The right operand, the rest of the chain.</param>
/// <param name="Line">The 1-based source line of the left operand's first token.</param>
public sealed record EqualsNode(ExpressionNode Left, ExpressionNode Right, int Line) : ExpressionNode(Line);

/// <summary>
/// Value equality for the list members of syntax records. Synthesized record equality compares an
/// <see cref="IReadOnlyList{T}"/> member by reference, so every syntax record holding a list compares and
/// hashes it through these two methods instead.
/// </summary>
internal static class SyntaxEquality
{
    /// <summary>
    /// True when both lists are <see langword="null"/>, or both have the same count and equal elements in order
    /// under <see cref="EqualityComparer{T}.Default"/> (ordinal for strings).
    /// </summary>
    public static bool ListEquals<T>(IReadOnlyList<T>? left, IReadOnlyList<T>? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null || left.Count != right.Count)
        {
            return false;
        }

        var comparer = EqualityComparer<T>.Default;
        for (var i = 0; i < left.Count; i++)
        {
            if (!comparer.Equals(left[i], right[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A hash consistent with <see cref="ListEquals{T}"/>: 0 for <see langword="null"/>, otherwise the count
    /// combined with each element's hash in order.
    /// </summary>
    public static int ListHash<T>(IReadOnlyList<T>? list)
    {
        if (list is null)
        {
            return 0;
        }

        var comparer = EqualityComparer<T>.Default;
        var hash = new HashCode();
        hash.Add(list.Count);
        for (var i = 0; i < list.Count; i++)
        {
            hash.Add(list[i], comparer);
        }

        return hash.ToHashCode();
    }
}
