using System.Globalization;

namespace EpiInfo.CheckCode.Lexing;

/// <summary>
/// One token of the check-code language, as the lexer produces it and the parser reads it. Its
/// <see cref="Line"/> is what diagnostics report as the line in the form's <c>metaViews.CheckCode</c> text.
/// </summary>
/// <remarks>
/// <para>
/// Keywords are case-insensitive (<c>"Case Sensitive" = False</c>, line 5 of <c>EpiInfo.Enter.Grammar.grm</c>),
/// so callers compare them through <see cref="Keyword"/> or <see cref="IsKeyword(string)"/>, never through
/// <see cref="Text"/>.
/// </para>
/// <para>
/// The token holds no parsed value: the parser converts <see cref="TokenKind.Number"/> text with
/// <see cref="CultureInfo.InvariantCulture"/> itself, so a suffixed or hexadecimal number such as <c>12L</c> or
/// <c>#1a</c>, which has no such value, is reported as unsupported. Record-struct equality compares all four members,
/// which is how tests compare token sequences.
/// </para>
/// </remarks>
/// <param name="Kind">The category of the token.</param>
/// <param name="Text">
/// The source spelling exactly as written, so a <see cref="TokenKind.Character"/> and a quoted
/// <see cref="TokenKind.File"/> keep their single quotes, a <see cref="TokenKind.File"/> or <see cref="TokenKind.Url"/>
/// its spaces and slashes, and a <see cref="TokenKind.Number"/> its suffix or <c>#</c>, with these exceptions, all
/// applied by the lexer: a <see cref="TokenKind.String"/> holds its content without the surrounding double quotes; a
/// bracketed <see cref="TokenKind.Identifier"/> holds its content without <c>[</c> and <c>]</c>, with its trailing
/// whitespace, line breaks included, trimmed and its inner whitespace and line breaks kept; an
/// <see cref="TokenKind.Unknown"/> token holds its raw source text, which for an unterminated string runs from the
/// <c>"</c> to the end of the line, for a string holding a character outside the grammar's string set is the whole
/// quoted text, for a bracketed name with whitespace straight after its <c>[</c> runs from the <c>[</c> through the
/// <c>]</c>, and for an unterminated block comment runs from the <c>/*</c> to the end of the program; and
/// <see cref="TokenKind.EndOfInput"/> holds the empty string.
/// </param>
/// <param name="Line">
/// The 1-based line in the program text where the token starts; <c>\r\n</c>, <c>\n</c> and a lone <c>\r</c> each count
/// as one line break.
/// </param>
/// <param name="Column">
/// The 1-based column, counted in UTF-16 code units from the start of the line; a tab counts as one.
/// </param>
public readonly record struct Token(TokenKind Kind, string Text, int Line, int Column)
{
    /// <summary>
    /// The keyword spelling upper-cased with the invariant culture, so <c>End-After</c>, <c>end-after</c> and
    /// <c>END-AFTER</c> all give <c>END-AFTER</c>; <see langword="null"/> unless <see cref="Kind"/> is
    /// <see cref="TokenKind.Keyword"/>.
    /// </summary>
    /// <remarks>
    /// The invariant conversion keeps the result independent of the current culture: under <c>tr-TR</c> a
    /// culture-sensitive conversion would turn the <c>i</c> of <c>if</c> into a dotted capital I (U+0130).
    /// </remarks>
    public string? Keyword => Kind == TokenKind.Keyword ? Text.ToUpperInvariant() : null;

    /// <summary>
    /// Whether the token can stand where the grammar allows a name: any <see cref="TokenKind.Identifier"/>, or a
    /// <see cref="TokenKind.Keyword"/> without a hyphen, such as <c>Page</c>. The parser decides by position
    /// whether such a keyword is a name. A hyphenated keyword such as <c>END-IF</c> never is, because grammar
    /// identifiers cannot contain <c>-</c>.
    /// </summary>
    public bool CanBeName =>
        Kind == TokenKind.Identifier || (Kind == TokenKind.Keyword && !Text.Contains('-', StringComparison.Ordinal));

    /// <summary>
    /// Whether the token is the keyword <paramref name="upperName"/>, compared ordinally ignoring case, so
    /// <c>"END-IF"</c> and <c>"End-If"</c> both match the source spelling <c>end-if</c>.
    /// </summary>
    /// <param name="upperName">The keyword to test for, conventionally in upper case.</param>
    /// <returns>
    /// <see langword="true"/> when <see cref="Kind"/> is <see cref="TokenKind.Keyword"/> and <see cref="Keyword"/>
    /// equals <paramref name="upperName"/> ignoring case; otherwise <see langword="false"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="upperName"/> is <see langword="null"/>.</exception>
    public bool IsKeyword(string upperName)
    {
        ArgumentNullException.ThrowIfNull(upperName);

        // An ordinal ignore-case comparison already applies the invariant upper-case mapping that Keyword applies,
        // so comparing Text gives the same answer as comparing Keyword without allocating its upper-case copy.
        return Kind == TokenKind.Keyword && string.Equals(Text, upperName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Describes the token as <c>Kind 'Text' @Line:Column</c>, for readable test failures.</summary>
    /// <returns>The kind, the text in single quotes, and the 1-based line and column.</returns>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Kind} '{Text}' @{Line}:{Column}");
}
