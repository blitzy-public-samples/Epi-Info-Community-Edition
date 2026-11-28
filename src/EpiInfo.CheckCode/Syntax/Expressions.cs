using EpiInfo.Domain.Values;

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

    /// <summary>
    /// A numeric literal; its finite invariant-culture value is in <see cref="LiteralNode.Number"/> and, when it
    /// was parsed from source, its spelling as written is in <see cref="LiteralNode.Text"/>.
    /// </summary>
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
/// <see cref="Line"/> is the 1-based line, in the form's <c>metaViews.CheckCode</c> text, of the token the
/// construct is attributed to: the literal or identifier token for <see cref="LiteralNode"/> and
/// <see cref="IdentifierNode"/>, the function name for <see cref="FunctionCallNode"/>, the <c>NOT</c> keyword for
/// <see cref="NotNode"/>, the <c>AND</c> keyword for <see cref="AndNode"/> and the <c>=</c> operator for
/// <see cref="EqualsNode"/>. When an expression sits on one line, a binary node's operator shares that line with
/// its left operand's first token. On a multiline expression a binary node carries its operator's line, the line
/// the parser reports operator diagnostics at, and each operand node carries its own line by these same rules.
/// The tree does not record where a binary node's left operand begins: that operand can itself be a binary node
/// carrying its operator's line, or a parenthesised expression whose <c>(</c> has no node. With
/// <c>IF A = "1"</c> on line 3 and <c>AND B = "2" THEN</c> on line 4, the condition is an <see cref="AndNode"/>
/// with line 4 whose left <see cref="EqualsNode"/> has line 3 and whose right <see cref="EqualsNode"/> has line 4.
/// With <c>IF A</c> on line 3 and <c>= "1" THEN</c> on line 4, the condition is an <see cref="EqualsNode"/> with
/// line 4 whose left <see cref="IdentifierNode"/> has line 3 and whose right <see cref="LiteralNode"/> has line 4.
/// In the chain <c>a = b = c</c> the outer <see cref="EqualsNode"/> carries the line of the first <c>=</c> and the
/// inner one the line of the second. Nodes carry that line rather than a token, so the tree has no dependency on
/// the lexer.
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
/// <param name="Line">The 1-based source line the construct is attributed to; see remarks.</param>
public abstract record ExpressionNode(int Line);

/// <summary>
/// A literal value. Build it through <see cref="FromString"/>, <see cref="FromNumber(string, double, int)"/>,
/// <see cref="FromNumber(double, int)"/>, <see cref="Missing"/> or <see cref="Unchecked"/>, which keep
/// <see cref="Text"/> and <see cref="Number"/> consistent with <see cref="Kind"/>: a
/// <see cref="LiteralKind.String"/> has text and no number, a <see cref="LiteralKind.Number"/> has a number and,
/// when it was parsed from source, the text it was spelled with, and the other kinds have neither.
/// <see cref="Number"/> is always finite: the constructor and a <see langword="with"/> expression reject NaN and
/// infinities, as <c>FieldValue.FromNumber</c> does for the value the literal evaluates to.
/// </summary>
/// <param name="Kind">The literal's kind.</param>
/// <param name="Text">
/// For a string literal, its content with the surrounding double quotes removed and nothing else changed: no
/// trimming and no escape processing, because the grammar's <c>String</c> terminal has no escapes. For a numeric
/// literal parsed from source, its spelling as written, such as <c>2.0000000000000001</c>, from which its exact
/// value is read: <see cref="Number"/> holds only the nearest double, which can be a whole number when the
/// literal is not. Otherwise <see langword="null"/>, a numeric literal built from a double included.
/// </param>
/// <param name="Number">
/// For a numeric literal, its finite invariant-culture value; otherwise <see langword="null"/>.
/// </param>
/// <param name="Line">The 1-based source line of the literal.</param>
/// <exception cref="ArgumentOutOfRangeException"><paramref name="Number"/> is NaN or infinite.</exception>
public sealed record LiteralNode(LiteralKind Kind, string? Text, double? Number, int Line) : ExpressionNode(Line)
{
    private const string NotFiniteMessage = "A numeric literal must be a finite number.";

    private readonly double? number = ValidateNumber(Number, nameof(Number));

    /// <summary>For a numeric literal, its finite invariant-culture value; otherwise <see langword="null"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is NaN or infinite.</exception>
    public double? Number
    {
        get => number;
        // Validated here as well as in the constructor, so a `with` expression cannot bypass the rule.
        init => number = ValidateNumber(value, nameof(Number));
    }

    /// <summary>Creates a string literal whose content, quotes already removed, is <paramref name="text"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    public static LiteralNode FromString(string text, int line)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new LiteralNode(LiteralKind.String, text, null, line);
    }

    /// <summary>Creates a numeric literal with the finite invariant-culture <paramref name="value"/>.</summary>
    /// <remarks>
    /// <see cref="double.TryParse(string, System.Globalization.NumberStyles, IFormatProvider, out double)"/>
    /// succeeds with an infinity for an overflowing literal such as <c>1E999</c>. A caller converting source text
    /// therefore tests <see cref="double.IsFinite(double)"/> first and reports such a literal as an unsupported
    /// numeric literal (a <c>NUMBER-LITERAL</c> warning, with only that statement skipped) instead of building a
    /// node.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is NaN or infinite.</exception>
    public static LiteralNode FromNumber(double value, int line)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), NotFiniteMessage);
        }

        return new LiteralNode(LiteralKind.Number, null, value, line);
    }

    /// <summary>
    /// Builds a numeric literal parsed from source: <paramref name="text"/> is its spelling as written and
    /// <paramref name="value"/> the finite invariant-culture double that spelling parses to.
    /// </summary>
    /// <remarks>
    /// The spelling is kept because the double alone cannot tell whether the literal is a whole number:
    /// <c>2.0000000000000001</c> and <c>2</c> parse to the same double, and <c>9007199254740993</c> parses to
    /// 9007199254740992. As for <see cref="FromNumber(double, int)"/>, a caller converting source text tests
    /// <see cref="double.IsFinite(double)"/> first and reports an overflowing literal such as <c>1E999</c> as an
    /// unsupported numeric literal instead of building a node.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="text"/> is empty, or is not an invariant-culture spelling of <paramref name="value"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is NaN or infinite.</exception>
    public static LiteralNode FromNumber(string text, double value, int line)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), NotFiniteMessage);
        }

        // -0 == 0, so a spelling of zero matches either signed zero.
        if (!FieldValueCoercion.TryParseInvariantNumber(text, out double spelled) || spelled != value)
        {
            throw new ArgumentException("A numeric literal's text must be a spelling of its number.", nameof(text));
        }

        return new LiteralNode(LiteralKind.Number, text, value, line);
    }

    /// <summary>Creates the missing-value literal <c>(.)</c>.</summary>
    public static LiteralNode Missing(int line) =>
        new(LiteralKind.Missing, null, null, line);

    /// <summary>Creates the unchecked literal <c>(-)</c>.</summary>
    public static LiteralNode Unchecked(int line) =>
        new(LiteralKind.Unchecked, null, null, line);

    // Callers pass the name to report, the Number constructor parameter or property, because it is not a
    // parameter of this helper.
    private static double? ValidateNumber(double? value, string paramName)
    {
        if (value.HasValue && !double.IsFinite(value.Value))
        {
            throw new ArgumentOutOfRangeException(paramName, NotFiniteMessage);
        }

        return value;
    }
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
/// The vocabulary of check-code functions the subset executes: <c>YEARS</c> and <c>EPIWEEK</c>.
/// </summary>
/// <remarks>
/// <para>
/// The parser builds a <see cref="FunctionCallNode"/> only for a name in this vocabulary and reports a call of any
/// other function as a <c>warning</c> diagnostic, with no node. The function registry maps each name to its
/// implementation. Both read the names from here, so they are written once and the syntax tree depends on neither.
/// </para>
/// <para>
/// Names match case-insensitively and exactly, as check-code keywords do (grammar line 4): <c>years</c> and
/// <c>EpiWeek</c> match, while <c>YEAR</c>, <c>EPIWEEKS</c> and a name with surrounding spaces do not. The
/// vocabulary is built once during type initialisation and never changes, so it is safe to use from any number of
/// threads.
/// </para>
/// </remarks>
public static class FunctionNames
{
    /// <summary>The canonical name of <c>YEARS(first, second)</c>.</summary>
    public const string Years = "YEARS";

    /// <summary>The canonical name of <c>EPIWEEK(date[, firstDay])</c>.</summary>
    public const string EpiWeek = "EPIWEEK";

    /// <summary>
    /// The canonical names of the vocabulary: <see cref="Years"/>, then <see cref="EpiWeek"/>. An immutable list
    /// that exposes no writable storage: writes through its collection interfaces throw
    /// <see cref="NotSupportedException"/>, and <c>ICollection.SyncRoot</c> is the list itself.
    /// </summary>
    public static IReadOnlyCollection<string> Supported { get; } =
        System.Collections.Immutable.ImmutableList.Create(Years, EpiWeek);

    // Declared after Supported, which it is built from: static initialisers run in textual order.
    private static readonly System.Collections.Frozen.FrozenSet<string> Lookup =
        System.Collections.Frozen.FrozenSet.ToFrozenSet(Supported, StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether a name is in the vocabulary.</summary>
    /// <param name="name">The function name as written in the program. <see langword="null"/> matches nothing.</param>
    /// <returns>Whether <paramref name="name"/> names a supported function, ignoring case.</returns>
    public static bool IsSupported(string? name) => name is not null && Lookup.Contains(name);
}

/// <summary>
/// A call of a registered function (<c>YEARS</c> or <c>EPIWEEK</c>); any other function is a diagnostic and
/// never becomes a node. <see cref="Arguments"/> is an immutable snapshot taken by the constructor and by a
/// <see langword="with"/> expression, so later changes to the caller's list or array never reach the node, and no
/// cast or collection interface on the snapshot reaches writable storage. Equality compares <see cref="Name"/>
/// ordinally and <see cref="Arguments"/> element by element, so a parsed tree equals a separately built expected
/// tree.
/// </summary>
/// <param name="Name">
/// The function name, stored in upper-invariant form (<c>YEARS</c>, <c>EPIWEEK</c>). Function names are
/// case-insensitive like keywords (grammar line 4) and <see cref="FunctionNames"/> matches them
/// case-insensitively, so <c>Years</c>, <c>years</c> and <c>YEARS</c> build equal nodes.
/// </param>
/// <param name="Arguments">
/// The argument expressions in source order, copied into an immutable snapshot.
/// </param>
/// <param name="Line">The 1-based source line of the function name.</param>
/// <exception cref="ArgumentNullException">
/// <paramref name="Name"/> or <paramref name="Arguments"/> is <see langword="null"/>.
/// </exception>
/// <exception cref="ArgumentException">
/// <paramref name="Arguments"/> contains a <see langword="null"/> element.
/// </exception>
public sealed record FunctionCallNode(string Name, IReadOnlyList<ExpressionNode> Arguments, int Line)
    : ExpressionNode(Line)
{
    private readonly string name = CanonicalName(Name);

    private readonly System.Collections.Immutable.ImmutableList<ExpressionNode> arguments =
        SyntaxLists.Snapshot(Arguments, nameof(Arguments));

    /// <summary>
    /// The function name in upper-invariant form (<c>YEARS</c>, <c>EPIWEEK</c>), whatever its spelling in the
    /// program.
    /// </summary>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    public string Name
    {
        get => name;
        // Canonicalised here as well as in the constructor, so a `with` expression cannot bypass the rule.
        init => name = CanonicalName(value);
    }

    /// <summary>
    /// The argument expressions in source order: an immutable snapshot that later changes to the caller's list
    /// or array never reach. It exposes no writable storage: writes through its collection interfaces throw
    /// <see cref="NotSupportedException"/>, and its own modifying methods return a new list.
    /// </summary>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The value contains a <see langword="null"/> element.</exception>
    public IReadOnlyList<ExpressionNode> Arguments
    {
        get => arguments;
        // Copied here as well as in the constructor, so a `with` expression cannot alias a mutable list.
        init => arguments = SyntaxLists.Snapshot(value, nameof(Arguments));
    }

    /// <inheritdoc />
    public bool Equals(FunctionCallNode? other) =>
        other is not null
        && base.Equals(other)
        && string.Equals(Name, other.Name, StringComparison.Ordinal)
        && SyntaxEquality.ListEquals(Arguments, other.Arguments);

    /// <inheritdoc />
    public override int GetHashCode() =>
        HashCode.Combine(base.GetHashCode(), Name, SyntaxEquality.ListHash(Arguments));

    private static string CanonicalName(string value)
    {
        ArgumentNullException.ThrowIfNull(value, nameof(Name));
        return value.ToUpperInvariant();
    }
}

/// <summary>The negation <c>NOT &lt;comparison&gt;</c>.</summary>
/// <param name="Operand">The negated comparison.</param>
/// <param name="Line">The 1-based source line of the <c>NOT</c> keyword.</param>
public sealed record NotNode(ExpressionNode Operand, int Line) : ExpressionNode(Line);

/// <summary>The conjunction <c>&lt;left&gt; AND &lt;right&gt;</c>; a chain nests to the right.</summary>
/// <param name="Left">The left operand, a <c>NOT</c> term.</param>
/// <param name="Right">The right operand, the rest of the chain.</param>
/// <param name="Line">The 1-based source line of the <c>AND</c> keyword.</param>
public sealed record AndNode(ExpressionNode Left, ExpressionNode Right, int Line) : ExpressionNode(Line);

/// <summary>The comparison <c>&lt;left&gt; = &lt;right&gt;</c>; a chain nests to the right.</summary>
/// <param name="Left">The left operand.</param>
/// <param name="Right">The right operand, the rest of the chain.</param>
/// <param name="Line">The 1-based source line of the <c>=</c> operator.</param>
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

/// <summary>
/// Immutable snapshots for the list members of syntax records. Every syntax record holding a list copies it through
/// these two methods, in its constructor and in the member's <see langword="init"/> accessor, so neither the
/// constructor nor a <see langword="with"/> expression can alias the caller's list or array.
/// </summary>
internal static class SyntaxLists
{
    // An immutable list exposes no writable storage: its collection-interface writes throw NotSupportedException,
    // ICollection.SyncRoot is the list itself (a ReadOnlyCollection's would be its backing array), and its own
    // modifying methods return a new list. Each element is read once, so a null cannot slip past the check. The
    // member name is passed as paramName because the list is not a parameter of this helper.

    /// <summary>
    /// Copies <paramref name="value"/>, in order, into a new immutable list, reporting a failure under
    /// <paramref name="paramName"/>, the name of the record member being set.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="value"/> contains a <see langword="null"/> element.
    /// </exception>
    public static System.Collections.Immutable.ImmutableList<T> Snapshot<T>(IReadOnlyList<T> value, string paramName)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(value, paramName);
        var builder = System.Collections.Immutable.ImmutableList.CreateBuilder<T>();
        var count = value.Count;
        for (var i = 0; i < count; i++)
        {
            builder.Add(value[i]
                ?? throw new ArgumentException($"{paramName} must not contain a null element.", paramName));
        }

        return builder.ToImmutable();
    }

    /// <summary>
    /// <see cref="Snapshot{T}"/> for an optional list member: <see langword="null"/> when <paramref name="value"/> is
    /// <see langword="null"/>, otherwise the snapshot of <paramref name="value"/>.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="value"/> contains a <see langword="null"/> element.
    /// </exception>
    public static System.Collections.Immutable.ImmutableList<T>? SnapshotOrNull<T>(
        IReadOnlyList<T>? value, string paramName)
        where T : class =>
        value is null ? null : Snapshot(value, paramName);
}
