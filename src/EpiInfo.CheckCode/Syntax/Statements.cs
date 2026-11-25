using System.Collections.Immutable;

namespace EpiInfo.CheckCode.Syntax;

/// <summary>
/// Base of the immutable statement nodes the check-code parser produces and the evaluator executes: the
/// supported subset of <c>IF</c>, <c>ENABLE</c>, <c>DISABLE</c>, <c>CLEAR</c>, <c>ASSIGN</c>, <c>DIALOG</c> and
/// <c>GEOCODE</c>.
/// </summary>
/// <remarks>
/// <para>
/// Unsupported statements are not represented. The parser omits each one and records a <c>Warning</c>
/// diagnostic, so the evaluator can never run one. That covers <c>GOTO</c>, <c>HIDE</c>, <c>UNHIDE</c>,
/// <c>SET-REQUIRED</c>, <c>DEFINE</c>, <c>LET</c> and the implicit assignment <c>x = y</c>, the <c>DIALOG</c> forms
/// other than <c>DIALOG "text" [TITLETEXT = "title"]</c>, <c>CALL</c>, the <c>'*' EXCEPT</c> forms of
/// <c>ENABLE</c> and <c>DISABLE</c>, every other command, and an <c>IF</c> whose condition is unsupported or that
/// contains <c>ELSE-IF</c>, which is omitted whole so neither branch runs.
/// </para>
/// <para>
/// Every list member is an immutable snapshot taken by the constructor and by a <see langword="with"/>
/// expression, so later changes to the caller's list or array never reach the node. Equality compares list
/// members element by element, so a parsed tree equals a separately built expected tree.
/// </para>
/// </remarks>
/// <param name="Line">
/// The 1-based line, in the form's <c>metaViews.CheckCode</c> text, of the statement keyword (<c>IF</c>,
/// <c>ENABLE</c>, <c>ASSIGN</c>, …). Nodes carry that line rather than a token, so the tree has no dependency on
/// the lexer.
/// </param>
public abstract record StatementNode(int Line);

/// <summary>
/// <c>IF &lt;condition&gt; THEN &lt;statements&gt; [ELSE &lt;statements&gt;] END-IF</c>, where <c>END</c> may
/// replace <c>END-IF</c> (grammar lines 434-438). Both terminators give the same node; the terminator is not
/// stored.
/// </summary>
/// <param name="Condition">The condition; only a boolean <see langword="true"/> runs <see cref="Then"/>.</param>
/// <param name="Then">
/// The supported statements of the <c>THEN</c> branch in source order, copied into an immutable snapshot. It is
/// empty when the branch is empty or every statement in it was unsupported and omitted.
/// </param>
/// <param name="Else">
/// The supported statements of the <c>ELSE</c> branch in source order, copied into an immutable snapshot;
/// <see langword="null"/> when there is no <c>ELSE</c>, and an empty list for an <c>ELSE</c> whose body is empty
/// or wholly unsupported.
/// </param>
/// <param name="Line">The 1-based source line of the <c>IF</c> keyword.</param>
/// <exception cref="ArgumentNullException"><paramref name="Then"/> is <see langword="null"/>.</exception>
/// <exception cref="ArgumentException">
/// <paramref name="Then"/> or <paramref name="Else"/> contains a <see langword="null"/> element.
/// </exception>
public sealed record IfStatementNode(
    ExpressionNode Condition,
    IReadOnlyList<StatementNode> Then,
    IReadOnlyList<StatementNode>? Else,
    int Line) : StatementNode(Line)
{
    private readonly ImmutableList<StatementNode> thenBranch = SyntaxLists.Snapshot(Then, nameof(Then));

    private readonly ImmutableList<StatementNode>? elseBranch = SyntaxLists.SnapshotOrNull(Else, nameof(Else));

    /// <summary>
    /// The <c>THEN</c> statements in source order: an immutable snapshot that later changes to the caller's list
    /// or array never reach.
    /// </summary>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The value contains a <see langword="null"/> element.</exception>
    public IReadOnlyList<StatementNode> Then
    {
        get => thenBranch;
        // Copied here as well as in the constructor, so a `with` expression cannot alias a mutable list.
        init => thenBranch = SyntaxLists.Snapshot(value, nameof(Then));
    }

    /// <summary>
    /// The <c>ELSE</c> statements in source order, as an immutable snapshot; <see langword="null"/> when the
    /// statement has no <c>ELSE</c>.
    /// </summary>
    /// <exception cref="ArgumentException">The value contains a <see langword="null"/> element.</exception>
    public IReadOnlyList<StatementNode>? Else
    {
        get => elseBranch;
        init => elseBranch = SyntaxLists.SnapshotOrNull(value, nameof(Else));
    }

    /// <inheritdoc />
    public bool Equals(IfStatementNode? other) =>
        other is not null
        && base.Equals(other)
        && EqualityComparer<ExpressionNode>.Default.Equals(Condition, other.Condition)
        && SyntaxEquality.ListEquals(Then, other.Then)
        && SyntaxEquality.ListEquals(Else, other.Else);

    /// <inheritdoc />
    public override int GetHashCode() =>
        HashCode.Combine(
            base.GetHashCode(),
            Condition,
            SyntaxEquality.ListHash(Then),
            SyntaxEquality.ListHash(Else));
}

/// <summary>
/// <c>ENABLE &lt;names&gt;</c> (grammar line 423). Each name may be a field or a group; the evaluator expands a
/// group to its <c>metaFields.List</c> members, as legacy did.
/// </summary>
/// <param name="Names">
/// The space-separated names in source order, each as written with any surrounding <c>[</c> and <c>]</c> removed
/// and resolved case-insensitively at run time, copied into an immutable snapshot.
/// </param>
/// <param name="Line">The 1-based source line of the <c>ENABLE</c> keyword.</param>
/// <exception cref="ArgumentNullException"><paramref name="Names"/> is <see langword="null"/>.</exception>
/// <exception cref="ArgumentException"><paramref name="Names"/> contains a <see langword="null"/> element.</exception>
public sealed record EnableStatementNode(IReadOnlyList<string> Names, int Line) : StatementNode(Line)
{
    private readonly ImmutableList<string> names = SyntaxLists.Snapshot(Names, nameof(Names));

    /// <summary>The names in source order, as an immutable snapshot.</summary>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The value contains a <see langword="null"/> element.</exception>
    public IReadOnlyList<string> Names
    {
        get => names;
        init => names = SyntaxLists.Snapshot(value, nameof(Names));
    }

    /// <inheritdoc />
    public bool Equals(EnableStatementNode? other) =>
        other is not null && base.Equals(other) && SyntaxEquality.ListEquals(Names, other.Names);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(base.GetHashCode(), SyntaxEquality.ListHash(Names));
}

/// <summary>
/// <c>DISABLE &lt;names&gt;</c> (grammar line 425). Each name may be a field or a group; the evaluator expands a
/// group to its <c>metaFields.List</c> members, as legacy did.
/// </summary>
/// <param name="Names">
/// The space-separated names in source order, each as written with any surrounding <c>[</c> and <c>]</c> removed
/// and resolved case-insensitively at run time, copied into an immutable snapshot.
/// </param>
/// <param name="Line">The 1-based source line of the <c>DISABLE</c> keyword.</param>
/// <exception cref="ArgumentNullException"><paramref name="Names"/> is <see langword="null"/>.</exception>
/// <exception cref="ArgumentException"><paramref name="Names"/> contains a <see langword="null"/> element.</exception>
public sealed record DisableStatementNode(IReadOnlyList<string> Names, int Line) : StatementNode(Line)
{
    private readonly ImmutableList<string> names = SyntaxLists.Snapshot(Names, nameof(Names));

    /// <summary>The names in source order, as an immutable snapshot.</summary>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The value contains a <see langword="null"/> element.</exception>
    public IReadOnlyList<string> Names
    {
        get => names;
        init => names = SyntaxLists.Snapshot(value, nameof(Names));
    }

    /// <inheritdoc />
    public bool Equals(DisableStatementNode? other) =>
        other is not null && base.Equals(other) && SyntaxEquality.ListEquals(Names, other.Names);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(base.GetHashCode(), SyntaxEquality.ListHash(Names));
}

/// <summary>
/// <c>CLEAR &lt;names&gt;</c> (grammar line 301). Each name may be a field or a group; the evaluator expands a
/// group to its <c>metaFields.List</c> members, as legacy did.
/// </summary>
/// <param name="Names">
/// The space-separated names in source order, each as written with any surrounding <c>[</c> and <c>]</c> removed
/// and resolved case-insensitively at run time, copied into an immutable snapshot.
/// </param>
/// <param name="Line">The 1-based source line of the <c>CLEAR</c> keyword.</param>
/// <exception cref="ArgumentNullException"><paramref name="Names"/> is <see langword="null"/>.</exception>
/// <exception cref="ArgumentException"><paramref name="Names"/> contains a <see langword="null"/> element.</exception>
public sealed record ClearStatementNode(IReadOnlyList<string> Names, int Line) : StatementNode(Line)
{
    private readonly ImmutableList<string> names = SyntaxLists.Snapshot(Names, nameof(Names));

    /// <summary>The names in source order, as an immutable snapshot.</summary>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The value contains a <see langword="null"/> element.</exception>
    public IReadOnlyList<string> Names
    {
        get => names;
        init => names = SyntaxLists.Snapshot(value, nameof(Names));
    }

    /// <inheritdoc />
    public bool Equals(ClearStatementNode? other) =>
        other is not null && base.Equals(other) && SyntaxEquality.ListEquals(Names, other.Names);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(base.GetHashCode(), SyntaxEquality.ListHash(Names));
}

/// <summary>
/// <c>ASSIGN &lt;field&gt; = &lt;expression&gt;</c> (grammar line 281). The evaluator coerces the value by the
/// target's field type; <c>(.)</c> assigns no value.
/// </summary>
/// <param name="Target">
/// The target field name as written, with any surrounding <c>[</c> and <c>]</c> removed. It is resolved
/// case-insensitively to the canonical <c>metaFields.Name</c> at run time, not here: the seeded <c>AGE</c> names
/// the field <c>Age</c>.
/// </param>
/// <param name="Value">The assigned expression.</param>
/// <param name="Line">The 1-based source line of the <c>ASSIGN</c> keyword.</param>
public sealed record AssignStatementNode(string Target, ExpressionNode Value, int Line) : StatementNode(Line);

/// <summary>
/// <c>DIALOG "message" [TITLETEXT = "title"]</c> (grammar lines 364 and 383-385), the only <c>DIALOG</c> form in
/// the subset. Both strings are program text, never field values.
/// </summary>
/// <param name="Message">
/// The message: the string literal's content without its surrounding double quotes, as the parser extracts it.
/// Seeded example: <c>Warning: A fever temperature has been entered, but 'fever' was not selected as a
/// symptom.</c>
/// </param>
/// <param name="Title">
/// The <c>TITLETEXT</c> string's content without its quotes; <see langword="null"/> without <c>TITLETEXT</c>, in
/// which case the client shows "Message".
/// </param>
/// <param name="Line">The 1-based source line of the <c>DIALOG</c> keyword.</param>
public sealed record DialogStatementNode(string Message, string? Title, int Line) : StatementNode(Line);

/// <summary>
/// <c>GEOCODE address, latitude, longitude</c> (grammar line 636). It is never executed: the evaluator skips it,
/// and its <c>Info</c> diagnostic (scenario <c>EC-GEO-01</c>) is already among the enclosing event block's and the
/// program's diagnostics. The node stays in the tree so the skip can be logged at its line.
/// </summary>
/// <param name="Arguments">
/// The comma-separated field names in source order (address, latitude, longitude), each as written with any
/// surrounding <c>[</c> and <c>]</c> removed, copied into an immutable snapshot.
/// </param>
/// <param name="Line">The 1-based source line of the <c>GEOCODE</c> keyword.</param>
/// <exception cref="ArgumentNullException"><paramref name="Arguments"/> is <see langword="null"/>.</exception>
/// <exception cref="ArgumentException">
/// <paramref name="Arguments"/> contains a <see langword="null"/> element.
/// </exception>
public sealed record GeocodeStatementNode(IReadOnlyList<string> Arguments, int Line) : StatementNode(Line)
{
    private readonly ImmutableList<string> arguments = SyntaxLists.Snapshot(Arguments, nameof(Arguments));

    /// <summary>The field names in source order, as an immutable snapshot.</summary>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The value contains a <see langword="null"/> element.</exception>
    public IReadOnlyList<string> Arguments
    {
        get => arguments;
        init => arguments = SyntaxLists.Snapshot(value, nameof(Arguments));
    }

    /// <inheritdoc />
    public bool Equals(GeocodeStatementNode? other) =>
        other is not null && base.Equals(other) && SyntaxEquality.ListEquals(Arguments, other.Arguments);

    /// <inheritdoc />
    public override int GetHashCode() =>
        HashCode.Combine(base.GetHashCode(), SyntaxEquality.ListHash(Arguments));
}
