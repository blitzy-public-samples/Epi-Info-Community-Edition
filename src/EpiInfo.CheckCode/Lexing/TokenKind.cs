namespace EpiInfo.CheckCode.Lexing;

/// <summary>
/// The category of one check-code token. The lexer emits no token for whitespace (grammar line 23) or for
/// <c>//</c> and <c>/* */</c> comments (lines 49-51), except inside a <see cref="File"/> or <see cref="Url"/>, whose
/// text they belong to, and it ends every token list with exactly one <see cref="EndOfInput"/>.
/// </summary>
/// <remarks>
/// Line numbers refer to <c>EpiInfo.Enter.Grammar.grm</c>, the check-code language specification. The boolean
/// literals <c>(+)</c> and <c>(-)</c> are <see cref="Plus"/> and <see cref="Minus"/>; the <c>+</c> and <c>-</c>
/// operators are <see cref="PlusSign"/> and <see cref="MinusSign"/>. Member names are what the parser and its tests
/// match on; the numeric values carry no meaning.
/// </remarks>
public enum TokenKind
{
    /// <summary>A bare name <c>{ID Head}{ID Tail}*</c> or a bracketed <c>[Some Name]</c> (line 30); function names such as <c>YEARS</c> and <c>EPIWEEK</c> are identifiers.</summary>
    Identifier,

    /// <summary>A double-quoted string such as <c>"1"</c> (line 24); it has no escape sequence and cannot span lines.</summary>
    String,

    /// <summary>A <c>DecLiteral</c>, <c>RealLiteral</c> or <c>HexLiteral</c> with no leading sign, such as <c>12</c>, <c>37.5</c>, <c>.5</c>, <c>2E-3</c>, the suffixed <c>12L</c>, <c>12U</c>, <c>12UL</c> or <c>12LU</c> in either case, or <c>#1a</c> (lines 34-39); a leading <c>+</c> or <c>-</c> is a separate <see cref="PlusSign"/> or <see cref="MinusSign"/> token (lines 34, 37, 521-522), while an exponent sign, as in <c>2E-3</c>, belongs to the number (lines 35-36), and the parser reports a suffixed or hexadecimal number as unsupported.</summary>
    Number,

    /// <summary>A date literal such as <c>01/15/2011</c> or <c>1-15-2011</c> (line 44).</summary>
    Date,

    /// <summary>A time literal such as <c>12:30</c> or <c>9:05:59</c> (line 45).</summary>
    Time,

    /// <summary>A <c>CharLiteral</c>: one printable character or extended letter other than <c>'</c> between single quotes, such as <c>'x'</c> (lines 11, 41); its text keeps the quotes, and the parser reports it as unsupported.</summary>
    Character,

    /// <summary>A <c>File</c> operand such as <c>C:\Program Files\app.exe</c>, <c>\\server\share\x.exe</c>, <c>..\a.txt</c>, <c>.\a.txt</c> or <c>\a.txt</c>, whose body runs over the printable characters of the rest of its line, spaces and reserved words included, or such a path between single quotes (line 25); one opaque token that only unsupported commands take.</summary>
    File,

    /// <summary>A <c>Url</c> operand: <c>http://</c> in either case followed by characters other than whitespace, such as <c>http://example.org/a</c>, a <c>//</c> among them included (lines 18, 26); one opaque token that only unsupported commands take.</summary>
    Url,

    /// <summary>The boolean literal <c>(+)</c>, checked (line 42); NOT the <c>+</c> operator, which is <see cref="PlusSign"/>.</summary>
    Plus,

    /// <summary>The boolean literal <c>(-)</c>, unchecked or false (line 42); not the <c>-</c> operator, which is <see cref="MinusSign"/>.</summary>
    Minus,

    /// <summary>The boolean literal <c>(.)</c>, the missing value (line 42).</summary>
    Missing,

    /// <summary>The comparison and assignment operator <c>=</c> (lines 281, 497).</summary>
    Equal,

    /// <summary>The comparison operator <c>&lt;&gt;</c> (line 498).</summary>
    NotEqual,

    /// <summary>The comparison operator <c>&lt;</c> (line 501).</summary>
    Less,

    /// <summary>The comparison operator <c>&gt;</c> (line 499).</summary>
    Greater,

    /// <summary>The comparison operator <c>&lt;=</c> (line 502).</summary>
    LessOrEqual,

    /// <summary>The comparison operator <c>&gt;=</c> (line 500).</summary>
    GreaterOrEqual,

    /// <summary>An opening parenthesis <c>(</c> that does not start a boolean literal (lines 282, 528).</summary>
    LeftParen,

    /// <summary>A closing parenthesis <c>)</c> (lines 282, 528).</summary>
    RightParen,

    /// <summary>A list separator <c>,</c> (lines 483, 636).</summary>
    Comma,

    /// <summary>A qualified-name separator <c>.</c> that does not start a number or a <see cref="File"/> such as <c>.\a.txt</c> (lines 25, 181).</summary>
    Dot,

    /// <summary>An opening bracket <c>[</c> that does not start a bracketed identifier, as in a grid reference (line 177).</summary>
    LeftBracket,

    /// <summary>A closing bracket <c>]</c> outside a bracketed identifier (line 177).</summary>
    RightBracket,

    /// <summary>The concatenation operator <c>&amp;</c> (line 505).</summary>
    Ampersand,

    /// <summary>The addition and unary plus operator <c>+</c> (lines 508, 522).</summary>
    PlusSign,

    /// <summary>The subtraction and negation operator <c>-</c> (lines 509, 521).</summary>
    MinusSign,

    /// <summary>The multiplication operator <c>*</c> (line 512).</summary>
    Asterisk,

    /// <summary>The division operator <c>/</c> that does not start a comment (line 513).</summary>
    Slash,

    /// <summary>The power operator <c>^</c> (line 518).</summary>
    Caret,

    /// <summary>The modulus operator <c>%</c> (line 515).</summary>
    Percent,

    /// <summary>The library-call separator <c>!</c> of the DLL form of <c>ASSIGN</c> (line 282).</summary>
    Bang,

    /// <summary>A colon <c>:</c> outside a time literal and a <see cref="File"/> head such as <c>C:</c> (lines 25, 319).</summary>
    Colon,

    /// <summary>A reserved word, including hyphenated forms such as <c>END-IF</c> and <c>End-After</c>; <c>Token.Keyword</c> gives its normalised spelling, and the parser may still accept it as a name where the grammar allows one.</summary>
    Keyword,

    /// <summary>A character or run the grammar does not admit, so lexing never fails: an unterminated string, a string holding a character outside <c>{String Chars}</c> (line 10), a bracketed name with whitespace straight after its <c>[</c> (line 30), an unterminated <c>/*</c> comment, or any other character.</summary>
    Unknown,

    /// <summary>The end of the program text: always the last token, emitted exactly once.</summary>
    EndOfInput,
}
