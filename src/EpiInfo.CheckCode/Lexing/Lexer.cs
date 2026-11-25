using System.Collections.Frozen;
using System.Diagnostics;

namespace EpiInfo.CheckCode.Lexing;

/// <summary>
/// Turns check-code program text, as stored in <c>metaViews.CheckCode</c>, into the token list the parser reads.
/// The lexical rules are those of <c>EpiInfo.Enter.Grammar.grm</c>, lines 1-51; line numbers in the comments of this
/// file refer to that grammar.
/// </summary>
/// <remarks>
/// <para>
/// The lexer never fails and reports no diagnostics: anything the grammar does not admit becomes a
/// <see cref="TokenKind.Unknown"/> token, which the parser reports with its line. Whitespace and comments produce no
/// token, and the list always ends with exactly one <see cref="TokenKind.EndOfInput"/>.
/// </para>
/// <para>
/// Every call works on its own scanner and the keyword tables are immutable, so the lexer is safe to call
/// concurrently.
/// </para>
/// </remarks>
public static class Lexer
{
    // SET-NOT-REQUIRED is the only three-part hyphenated keyword, so no candidate needs more parts.
    private const int MaxHyphenatedParts = 3;

    private static readonly FrozenSet<string> KeywordSet = new[]
    {
        // Blocks (lines 620-640).
        "FIELD", "BEFORE", "AFTER", "CLICK", "VIEW", "FORM", "RECORD", "PAGE", "DEFINEVARIABLES", "SUB",

        // Control flow and operators (lines 434-444, 486-515).
        "IF", "THEN", "ELSE", "END", "NOT", "AND", "OR", "XOR", "LIKE", "MOD",

        // Statements (lines 275-645).
        "ALWAYS", "ASSIGN", "AUTOSEARCH", "BEEP", "CALL", "CLEAR", "COMMANDLINE", "DEFINE", "DIALOG", "DISABLE",
        "ENABLE", "EXECUTE", "EXIT", "FILEDIALOG", "GEOCODE", "GETPATH", "GOTO", "GOTOFORM", "GOTOPAGE", "HELP",
        "HIDE", "HIGHLIGHT", "IOCODE", "LET", "NEWRECORD", "QUIT", "UNDEFINE", "UNHIDE", "UNHIGHLIGHT", "WAITFOR",
        "WAITFOREXIT", "WAITFORFILEEXISTS",

        // Statement modifiers (lines 199-202, 290-418).
        "EXCEPT", "TITLETEXT", "DATABASES", "DATEFORMAT", "DATETIMEFORMAT", "DBVALUES", "DBVARIABLES", "DBVIEWS",
        "DISPLAYLIST", "DLLOBJECT", "GLOBAL", "GROUPVAR", "NETOBJECT", "NOWAITFOREXIT", "NUMERIC", "PERMANENT",
        "PGMNAME", "READ", "STANDARD", "TEXTINPUT", "TIMEFORMAT", "VIEWNAME", "WRITE", "YN",

        // Built-in function words (lines 254, 260). Other functions, YEARS and EPIWEEK included, are written as
        // <Qualified ID> '(' (line 259), so their names are identifiers.
        "FORMAT", "RECORDCOUNT", "SYSALTITUDE", "SYSBARCODE", "SYSLATITUDE", "SYSLONGITUDE", "SYSTEMDATE",
        "SYSTEMTIME",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> HyphenatedKeywordSet = new[]
    {
        // Control flow (lines 434-444).
        "END-IF", "ELSE-IF",

        // Block terminators (lines 620-640).
        "END-BEFORE", "END-AFTER", "END-CLICK", "END-FIELD", "END-VIEW", "END-FORM", "END-RECORD", "END-PAGE",
        "END-DEFINEVARIABLES", "END-SUB",

        // Statements (lines 427-428, 645).
        "SET-REQUIRED", "SET-NOT-REQUIRED", "SAVE-RECORD",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The single-word reserved words, in upper case; lookups ignore case (<c>"Case Sensitive" = False</c>, line 5).
    /// </summary>
    /// <remarks>
    /// A word in this set is lexed as <see cref="TokenKind.Keyword"/>, yet it may still serve as a name where the
    /// grammar allows an identifier: the parser decides by position, using <see cref="Token.CanBeName"/>. No seeded
    /// field name collides with a keyword. <c>TRUE</c>, <c>FALSE</c>, <c>YES</c>, <c>NO</c>, and <c>SET</c> or
    /// <c>SAVE</c> on their own are not reserved.
    /// </remarks>
    public static IReadOnlySet<string> Keywords => KeywordSet;

    /// <summary>
    /// The hyphenated reserved words such as <c>END-IF</c> and <c>SET-NOT-REQUIRED</c>, in upper case; lookups ignore
    /// case. Each is lexed as one <see cref="TokenKind.Keyword"/> token only when written without whitespace around
    /// its hyphens.
    /// </summary>
    public static IReadOnlySet<string> HyphenatedKeywords => HyphenatedKeywordSet;

    /// <summary>Splits check-code program text into tokens.</summary>
    /// <param name="text">The program text; <see langword="null"/> is treated as empty.</param>
    /// <returns>
    /// Every token in source order, followed by exactly one <see cref="TokenKind.EndOfInput"/> token with empty
    /// text at the line and column just past the last character, which is (1, 1) for empty text. The method never
    /// throws for any input.
    /// </returns>
    public static IReadOnlyList<Token> Tokenize(string? text) => new Scanner(text ?? string.Empty).Run();

    private static bool IsIdHead(char c) => char.IsLetter(c) || c is '_' or '@';

    private static bool IsIdTail(char c) => char.IsLetter(c) || char.IsAsciiDigit(c) || c is '_' or '@' or '#' or '$';

    private static bool IsLineBreak(char c) => c is '\r' or '\n';

    private static bool IsInlineWhiteSpace(char c) => !IsLineBreak(c) && char.IsWhiteSpace(c);

    private static bool IsDateSeparator(char c) => c is '/' or '-';

    /// <summary>
    /// One tokenization pass over one text. <see cref="Peek"/> returns <c>'\0'</c> past the end, so lookahead never
    /// reads out of range; a <c>'\0'</c> inside the text matches no rule but the last and lexes as
    /// <see cref="TokenKind.Unknown"/>, which keeps the two cases equivalent.
    /// </summary>
    private sealed class Scanner(string text)
    {
        private readonly List<Token> tokens = [];
        private int index;
        private int line = 1;
        private int column = 1;

        public List<Token> Run()
        {
            while (index < text.Length)
            {
                int start = index;
                ScanNext();
                Debug.Assert(index > start, "Every scanning step must consume at least one character.");
            }

            tokens.Add(new Token(TokenKind.EndOfInput, string.Empty, line, column));
            return tokens;
        }

        /// <summary>Applies the first scanning rule that matches at the current position.</summary>
        private void ScanNext()
        {
            char c = text[index];

            if (IsLineBreak(c))
            {
                SkipLineBreak();
            }
            else if (char.IsWhiteSpace(c))
            {
                Advance(1);
            }
            else if (c == '/' && Peek(1) == '/')
            {
                SkipLineComment();
            }
            else if (c == '/' && Peek(1) == '*')
            {
                SkipBlockComment();
            }
            else if (c == '"')
            {
                ScanString();
            }
            else if (c == '(' && Peek(2) == ')' && BooleanLiteralKind(Peek(1)) is { } booleanKind)
            {
                Emit(booleanKind, 3);
            }
            else if (c == '[')
            {
                ScanBracket();
            }
            else if (char.IsAsciiDigit(c) || (c == '.' && char.IsAsciiDigit(Peek(1))))
            {
                ScanNumeric();
            }
            else if (IsIdHead(c))
            {
                ScanWord();
            }
            else if (OperatorAt(c) is { } punctuation)
            {
                Emit(punctuation.Kind, punctuation.Length);
            }
            else
            {
                // A surrogate pair stays one token, so an Unknown token never splits a character.
                Emit(TokenKind.Unknown, char.IsHighSurrogate(c) && char.IsLowSurrogate(Peek(1)) ? 2 : 1);
            }
        }

        private char Peek(int offset)
        {
            int position = index + offset;
            return (uint)position < (uint)text.Length ? text[position] : '\0';
        }

        /// <summary>Moves past <paramref name="length"/> characters that contain no line break.</summary>
        private void Advance(int length)
        {
            index += length;
            column += length;
        }

        /// <summary>Moves past one line break: <c>\r\n</c>, <c>\n</c> or a lone <c>\r</c> (lines 12, 23).</summary>
        private void SkipLineBreak()
        {
            index += text[index] == '\r' && Peek(1) == '\n' ? 2 : 1;
            line++;
            column = 1;
        }

        private void Emit(TokenKind kind, int length) => Emit(kind, text.Substring(index, length), length);

        private void Emit(TokenKind kind, string tokenText, int length)
        {
            tokens.Add(new Token(kind, tokenText, line, column));
            Advance(length);
        }

        /// <summary>Skips a <c>//</c> comment up to, not including, the line break that ends it (line 49).</summary>
        private void SkipLineComment()
        {
            int length = text.AsSpan(index + 2).IndexOfAny('\r', '\n');
            Advance(length < 0 ? text.Length - index : length + 2);
        }

        /// <summary>
        /// Skips a <c>/* */</c> comment through the first <c>*/</c> after the opening <c>/*</c>, counting its line
        /// breaks (lines 50-51). Comments do not nest, and an unterminated one runs to the end of the text.
        /// </summary>
        private void SkipBlockComment()
        {
            Advance(2);
            while (index < text.Length)
            {
                char c = text[index];
                if (c == '*' && Peek(1) == '/')
                {
                    Advance(2);
                    return;
                }

                if (IsLineBreak(c))
                {
                    SkipLineBreak();
                }
                else
                {
                    Advance(1);
                }
            }
        }

        /// <summary>
        /// Lexes a double-quoted string, which has no escape and ends on its own line (line 24). Its content admits
        /// every character but <c>"</c> and line breaks. Without a closing quote, the run from the <c>"</c> to the end
        /// of the line becomes one <see cref="TokenKind.Unknown"/> token.
        /// </summary>
        private void ScanString()
        {
            int contentStart = index + 1;
            int contentLength = text.AsSpan(contentStart).IndexOfAny('"', '\r', '\n');

            if (contentLength >= 0 && text[contentStart + contentLength] == '"')
            {
                Emit(TokenKind.String, text.Substring(contentStart, contentLength), contentLength + 2);
            }
            else
            {
                Emit(TokenKind.Unknown, (contentLength < 0 ? text.Length - contentStart : contentLength) + 1);
            }
        }

        private static TokenKind? BooleanLiteralKind(char inner) => inner switch
        {
            '+' => TokenKind.Plus,
            '-' => TokenKind.Minus,
            '.' => TokenKind.Missing,
            _ => null,
        };

        /// <summary>
        /// Lexes <c>[Some Name]</c> as one identifier holding the trimmed content (line 30), or else the <c>[</c>
        /// alone as <see cref="TokenKind.LeftBracket"/>, as in the grid reference <c>Grid[1,2]</c>. The match never
        /// crosses a line break, which keeps line accounting exact.
        /// </summary>
        private void ScanBracket()
        {
            // Line 30 puts an ID head or '#' straight after '['; whitespace before it is accepted here so that
            // [ Name ] is the identifier Name, which is what trimming the content means. A grid reference is
            // unaffected, because its first element is a number.
            int offset = 1;
            while (IsInlineWhiteSpace(Peek(offset)))
            {
                offset++;
            }

            char head = Peek(offset);
            if (IsIdHead(head) || head == '#')
            {
                for (offset++; ; offset++)
                {
                    char c = Peek(offset);
                    if (c == ']')
                    {
                        Emit(TokenKind.Identifier, text.Substring(index + 1, offset - 1).Trim(), offset + 1);
                        return;
                    }

                    if (!IsIdTail(c) && !IsInlineWhiteSpace(c))
                    {
                        break;
                    }
                }
            }

            Emit(TokenKind.LeftBracket, 1);
        }

        /// <summary>
        /// Lexes a date, time or number, choosing the longest of the three matches as the grammar's tokenizer does
        /// (lines 34-38, 44-45); on a tie the date wins, then the time.
        /// </summary>
        private void ScanNumeric()
        {
            int dateLength = MatchDate();
            int timeLength = MatchTime();
            int numberLength = MatchNumber();
            Debug.Assert(numberLength > 0, "A numeric scan starts at a digit or at '.' followed by a digit.");

            if (dateLength > 0 && dateLength >= timeLength && dateLength >= numberLength)
            {
                Emit(TokenKind.Date, dateLength);
            }
            else if (timeLength > 0 && timeLength >= numberLength)
            {
                Emit(TokenKind.Time, timeLength);
            }
            else
            {
                Emit(TokenKind.Number, numberLength);
            }
        }

        /// <summary>
        /// The length of a date such as <c>01/15/2011</c> at the current position, or 0: one or two digits, a
        /// <c>/</c> or <c>-</c>, one or two digits, a <c>/</c> or <c>-</c>, then one to four digits (line 44).
        /// </summary>
        private int MatchDate()
        {
            // Counting up to three digits tells a one- or two-digit run from a longer one that fails the shape.
            int first = DigitRun(0, 3);
            if (first is < 1 or > 2 || !IsDateSeparator(Peek(first)))
            {
                return 0;
            }

            int secondStart = first + 1;
            int second = DigitRun(secondStart, 3);
            if (second is < 1 or > 2 || !IsDateSeparator(Peek(secondStart + second)))
            {
                return 0;
            }

            int yearStart = secondStart + second + 1;
            int year = DigitRun(yearStart, 4);
            return year == 0 ? 0 : yearStart + year;
        }

        /// <summary>
        /// The length of a time such as <c>12:30</c> or <c>9:05:59</c> at the current position, or 0: an hour up to
        /// 24, <c>:</c>, a minute and an optional <c>:</c> second (line 45).
        /// </summary>
        private int MatchTime()
        {
            int hour = HourLength();
            if (hour == 0 || Peek(hour) != ':')
            {
                return 0;
            }

            int minuteStart = hour + 1;
            int minute = SexagesimalLength(minuteStart);
            if (minute == 0)
            {
                return 0;
            }

            int end = minuteStart + minute;
            if (Peek(end) == ':')
            {
                int second = SexagesimalLength(end + 1);
                if (second > 0)
                {
                    end += 1 + second;
                }
            }

            return end;
        }

        /// <summary>An hour: two digits led by 0 or 1, or 2 followed by 0-4; else one digit; else 0.</summary>
        private int HourLength()
        {
            char first = Peek(0);
            char next = Peek(1);
            if ((first is '0' or '1' && char.IsAsciiDigit(next)) || (first == '2' && next is >= '0' and <= '4'))
            {
                return 2;
            }

            return char.IsAsciiDigit(first) ? 1 : 0;
        }

        /// <summary>A minute or second: two digits led by 0-5, else one digit, else 0.</summary>
        private int SexagesimalLength(int offset)
        {
            char first = Peek(offset);
            if (first is >= '0' and <= '5' && char.IsAsciiDigit(Peek(offset + 1)))
            {
                return 2;
            }

            return char.IsAsciiDigit(first) ? 1 : 0;
        }

        /// <summary>
        /// The length of an unsigned decimal or real number at the current position (lines 34-38): digits with an
        /// optional fraction, or a fraction alone, then an optional exponent. A <c>.</c> or exponent belongs to the
        /// number only when digits complete it, so <c>5.</c> leaves the dot and <c>2E</c> leaves the <c>E</c>. The
        /// <c>U</c>/<c>L</c> integer suffix is not recognised.
        /// </summary>
        private int MatchNumber()
        {
            int length = DigitRun(0, int.MaxValue);
            if (length > 0)
            {
                if (Peek(length) == '.' && char.IsAsciiDigit(Peek(length + 1)))
                {
                    length += 1 + DigitRun(length + 1, int.MaxValue);
                }
            }
            else if (Peek(0) == '.' && char.IsAsciiDigit(Peek(1)))
            {
                length = 1 + DigitRun(1, int.MaxValue);
            }
            else
            {
                return 0;
            }

            if (Peek(length) is 'e' or 'E')
            {
                int exponentStart = Peek(length + 1) is '+' or '-' ? length + 2 : length + 1;
                int exponentDigits = DigitRun(exponentStart, int.MaxValue);
                if (exponentDigits > 0)
                {
                    length = exponentStart + exponentDigits;
                }
            }

            return length;
        }

        private int DigitRun(int offset, int maximum)
        {
            int count = 0;
            while (count < maximum && char.IsAsciiDigit(Peek(offset + count)))
            {
                count++;
            }

            return count;
        }

        /// <summary>
        /// Lexes a word (lines 15-16, 30). The longest hyphen-joined run of up to three words that is a hyphenated
        /// keyword, such as <c>End-After</c>, becomes one keyword token; otherwise the first word alone is a keyword
        /// or an identifier, and a following <c>-</c> lexes as <see cref="TokenKind.MinusSign"/>.
        /// </summary>
        private void ScanWord()
        {
            int firstLength = WordLength(0);
            int end = firstLength;
            int keywordLength = 0;

            for (int parts = 1; parts < MaxHyphenatedParts && Peek(end) == '-' && IsIdHead(Peek(end + 1)); parts++)
            {
                end += 1 + WordLength(end + 1);
                if (HyphenatedKeywordSet.Contains(text.Substring(index, end)))
                {
                    keywordLength = end;
                }
            }

            if (keywordLength > 0)
            {
                Emit(TokenKind.Keyword, keywordLength);
                return;
            }

            string word = text.Substring(index, firstLength);
            Emit(KeywordSet.Contains(word) ? TokenKind.Keyword : TokenKind.Identifier, word, firstLength);
        }

        /// <summary>The length of the maximal word starting at <paramref name="offset"/>, which holds an ID head.</summary>
        private int WordLength(int offset)
        {
            int length = 1;
            while (IsIdTail(Peek(offset + length)))
            {
                length++;
            }

            return length;
        }

        /// <summary>
        /// The operator or punctuation token at the current position, two-character comparisons first (lines 282,
        /// 486-528), or <see langword="null"/> when the character is none.
        /// </summary>
        private (TokenKind Kind, int Length)? OperatorAt(char c) => c switch
        {
            '<' when Peek(1) == '>' => (TokenKind.NotEqual, 2),
            '<' when Peek(1) == '=' => (TokenKind.LessOrEqual, 2),
            '>' when Peek(1) == '=' => (TokenKind.GreaterOrEqual, 2),
            '=' => (TokenKind.Equal, 1),
            '<' => (TokenKind.Less, 1),
            '>' => (TokenKind.Greater, 1),
            '(' => (TokenKind.LeftParen, 1),
            ')' => (TokenKind.RightParen, 1),
            ',' => (TokenKind.Comma, 1),
            '.' => (TokenKind.Dot, 1),
            '[' => (TokenKind.LeftBracket, 1),
            ']' => (TokenKind.RightBracket, 1),
            '&' => (TokenKind.Ampersand, 1),
            '+' => (TokenKind.PlusSign, 1),
            '-' => (TokenKind.MinusSign, 1),
            '*' => (TokenKind.Asterisk, 1),
            '/' => (TokenKind.Slash, 1),
            '^' => (TokenKind.Caret, 1),
            '%' => (TokenKind.Percent, 1),
            '!' => (TokenKind.Bang, 1),
            ':' => (TokenKind.Colon, 1),
            _ => null,
        };
    }
}
