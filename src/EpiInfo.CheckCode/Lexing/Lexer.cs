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
/// <see cref="TokenKind.Unknown"/> token, which the parser reports with its line. Whitespace and closed comments
/// produce no token, and the list always ends with exactly one <see cref="TokenKind.EndOfInput"/>.
/// </para>
/// <para>
/// Each terminal the grammar admits is one token, chosen by the longest match as the grammar's tokenizer chooses it,
/// whether or not the parser's subset runs it: a <c>DecLiteral</c> with its <c>U</c>/<c>L</c> suffix and a
/// <c>HexLiteral</c> are <see cref="TokenKind.Number"/> (lines 38-39), a <c>CharLiteral</c> is
/// <see cref="TokenKind.Character"/> (line 41), and the <c>File</c> and <c>Url</c> operands are
/// <see cref="TokenKind.File"/> and <see cref="TokenKind.Url"/> (lines 25-26). Lexing is context free: a
/// <c>File</c> runs over the printable rest of its line, and a <c>//</c>, a <c>/*</c> or a reserved word inside a
/// <c>File</c> or <c>Url</c> belongs to that token, so it starts no comment and delimits no block.
/// </para>
/// <para>
/// Every call works on its own scanner and the keyword tables are immutable, so the lexer is safe to call
/// concurrently.
/// </para>
/// </remarks>
public static class Lexer
{
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

    private static bool IsDateSeparator(char c) => c is '/' or '-';

    // {Printable} (lines 10-19): U+0020 to U+007E and the no-break space U+00A0.
    private static bool IsPrintable(char c) => c is (>= ' ' and <= '~') or '\u00A0';

    // {Letter Extended}: the Latin-1 letters U+00C0 to U+00FF, which exclude U+00D7 and U+00F7.
    private static bool IsLetterExtended(char c) => c is >= '\u00C0' and <= '\u00FF' and not '\u00D7' and not '\u00F7';

    // {Char Ch} (line 11): a printable character or an extended letter, except the single quote.
    private static bool IsCharLiteralChar(char c) => (IsPrintable(c) || IsLetterExtended(c)) && c != '\'';

    // The [abcdef] of line 39, in either case because the grammar ignores case (line 5).
    private static bool IsHexLetter(char c) => c is (>= 'a' and <= 'f') or (>= 'A' and <= 'F');

    // {Url Chars} (line 18): a printable character or an extended letter, except whitespace.
    private static bool IsUrlChar(char c) => (IsPrintable(c) || IsLetterExtended(c)) && !char.IsWhiteSpace(c);

    /// <summary>
    /// Whether <paramref name="c"/> may stand inside a string: <c>{String Chars} = {Printable} + {Letter Extended} -
    /// ["] + {Cyrillic}</c> (line 10). <c>{Printable}</c> is U+0020-U+007E and the no-break space U+00A0;
    /// <c>{Letter Extended}</c> is U+00C0-U+00FF without U+00D7 and U+00F7; <c>{Cyrillic}</c> is U+0400-U+04FF.
    /// Tab, the other control codes, DEL, the rest of Latin-1 and every other code unit, surrogates included, are
    /// rejected. The <c>"</c> never reaches this check, because it ends the string.
    /// </summary>
    private static bool IsStringChar(char c) =>
        c is (>= ' ' and <= '~') or '\u00A0' or (>= '\u00C0' and <= '\u00FF' and not '\u00D7' and not '\u00F7')
            or (>= '\u0400' and <= '\u04FF');

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
            else if (c == '#' && MatchHex() is > 0 and int hexLength)
            {
                Emit(TokenKind.Number, hexLength);
            }
            else if (c == '\'')
            {
                ScanQuote();
            }
            else if (MatchUrl() is > 0 and int urlLength)
            {
                // Longer than the identifier "http", so the longest match makes it a URL, '//' and all (line 26).
                Emit(TokenKind.Url, urlLength);
            }
            else if (FileHeadLength(0) is > 0 and int headLength)
            {
                // Longer than the one-letter identifier, '.' or '\' it starts with (line 25).
                Emit(TokenKind.File, headLength + PrintableRun(headLength));
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

        /// <summary>
        /// Adds a token at the current position, then moves past <paramref name="length"/> characters that may hold
        /// line breaks, counting each one as <see cref="SkipLineBreak"/> does, so the line and column after it equal
        /// those of a character-by-character scan.
        /// </summary>
        /// <remarks>
        /// The span must not end between the two characters of a <c>\r\n</c> pair; a span ending at a <c>]</c> never
        /// does.
        /// </remarks>
        private void EmitSpanningLines(TokenKind kind, string tokenText, int length)
        {
            tokens.Add(new Token(kind, tokenText, line, column));

            int end = index + length;
            while (index < end)
            {
                if (IsLineBreak(text[index]))
                {
                    SkipLineBreak();
                }
                else
                {
                    Advance(1);
                }
            }

            Debug.Assert(index == end, "A span must not end between the two characters of a \\r\\n pair.");
        }

        /// <summary>Skips a <c>//</c> comment up to, not including, the line break that ends it (line 49).</summary>
        private void SkipLineComment()
        {
            int length = text.AsSpan(index + 2).IndexOfAny('\r', '\n');
            Advance(length < 0 ? text.Length - index : length + 2);
        }

        /// <summary>
        /// Skips a <c>/* */</c> comment through the first <c>*/</c> after the opening <c>/*</c>, counting its line
        /// breaks (lines 50-51). Comments do not nest. An unterminated one runs to the end of the text and becomes one
        /// <see cref="TokenKind.Unknown"/> token holding that raw text, at the line and column of its <c>/*</c>.
        /// </summary>
        private void SkipBlockComment()
        {
            int start = index;
            int startLine = line;
            int startColumn = column;

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

            // Added after the scan, so the end-of-input position stays just past the last character; nothing follows
            // the comment, so this token is still the last before EndOfInput.
            tokens.Add(new Token(TokenKind.Unknown, text.Substring(start), startLine, startColumn));
        }

        /// <summary>
        /// Lexes a double-quoted string, which has no escape and ends on its own line (line 24). Its content admits
        /// only <c>{String Chars}</c> (line 10, see <see cref="IsStringChar"/>). A closed string holding any other
        /// character becomes one <see cref="TokenKind.Unknown"/> token with the quoted text, quotes included, and
        /// scanning resumes after its closing quote. Without a closing quote, the run from the <c>"</c> to the end of
        /// the line becomes one <see cref="TokenKind.Unknown"/> token.
        /// </summary>
        private void ScanString()
        {
            int contentStart = index + 1;
            int contentLength = text.AsSpan(contentStart).IndexOfAny('"', '\r', '\n');

            if (contentLength >= 0 && text[contentStart + contentLength] == '"')
            {
                bool valid = true;
                foreach (char c in text.AsSpan(contentStart, contentLength))
                {
                    if (!IsStringChar(c))
                    {
                        valid = false;
                        break;
                    }
                }

                if (valid)
                {
                    Emit(TokenKind.String, text.Substring(contentStart, contentLength), contentLength + 2);
                }
                else
                {
                    Emit(TokenKind.Unknown, contentLength + 2);
                }
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
        /// Lexes <c>[Some Name]</c> as one identifier (line 30): an ID head or <c>#</c> straight after the <c>[</c>,
        /// then ID tail characters and whitespace up to the <c>]</c>. As everywhere in this lexer, whitespace is any
        /// <see cref="char.IsWhiteSpace(char)"/> character, which covers the grammar's <c>{WhiteSpace}</c> and its
        /// line breaks, so a name may span lines. The identifier holds the content with its trailing whitespace, line
        /// breaks included, removed and its inner whitespace and line breaks kept. The same shape with whitespace
        /// between the <c>[</c> and its head, as in <c>[ Name ]</c>, is no identifier: the whole span from <c>[</c> to
        /// <c>]</c> becomes one <see cref="TokenKind.Unknown"/> token holding the raw text, so a reserved word inside
        /// it, as in <c>[ END ]</c>, never reaches the parser as a keyword. Anything else leaves the <c>[</c> alone as
        /// <see cref="TokenKind.LeftBracket"/>: no closing <c>]</c>, a character outside the shape before it, or no
        /// head after the <c>[</c> and its padding, as in the grid reference <c>Grid[1,2]</c> or <c>Grid[ 1,2]</c>.
        /// Either span token sits at its <c>[</c>, and the line breaks inside it are counted as everywhere else, so the
        /// line and column after it are exact.
        /// </summary>
        private void ScanBracket()
        {
            int headOffset = 1;
            while (char.IsWhiteSpace(Peek(headOffset)))
            {
                headOffset++;
            }

            char head = Peek(headOffset);
            if (IsIdHead(head) || head == '#')
            {
                for (int offset = headOffset + 1; ; offset++)
                {
                    char c = Peek(offset);
                    if (c == ']')
                    {
                        int length = offset + 1;
                        if (headOffset == 1)
                        {
                            // The head is never whitespace, so only the end of the content can need trimming.
                            EmitSpanningLines(
                                TokenKind.Identifier,
                                text.Substring(index + 1, offset - 1).TrimEnd(),
                                length);
                        }
                        else
                        {
                            EmitSpanningLines(TokenKind.Unknown, text.Substring(index, length), length);
                        }

                        return;
                    }

                    if (!IsIdTail(c) && !char.IsWhiteSpace(c))
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
        /// number only when digits complete it, so <c>5.</c> leaves the dot and <c>2E</c> leaves the <c>E</c>. Digits
        /// with neither keep the <c>DecLiteral</c> suffix that follows them, <c>U</c>, <c>L</c>, <c>UL</c> or
        /// <c>LU</c> in either case (line 38), so <c>12L</c> is one token; the parser reports it as unsupported.
        /// </summary>
        private int MatchNumber()
        {
            int integerDigits = DigitRun(0, int.MaxValue);
            int length = integerDigits;
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

            return length == integerDigits ? length + IntegerSuffixLength(length) : length;
        }

        /// <summary>
        /// The length of the <c>DecLiteral</c> suffix at <paramref name="offset"/> (line 38): 1 for <c>U</c> or
        /// <c>L</c>, 2 for <c>UL</c> or <c>LU</c>, in either case, else 0.
        /// </summary>
        private int IntegerSuffixLength(int offset)
        {
            char first = char.ToUpperInvariant(Peek(offset));
            if (first is not 'U' and not 'L')
            {
                return 0;
            }

            char second = char.ToUpperInvariant(Peek(offset + 1));
            return second is 'U' or 'L' && second != first ? 2 : 1;
        }

        /// <summary>
        /// The length of a <c>HexLiteral</c> such as <c>#1a</c> at the current <c>#</c>, or 0: one or more pairs of
        /// a digit and a letter <c>a</c>-<c>f</c> in either case (line 39). Without a pair, as in <c>#12</c>, the
        /// <c>#</c> is left to lex as <see cref="TokenKind.Unknown"/>.
        /// </summary>
        private int MatchHex()
        {
            int length = 1;
            while (char.IsAsciiDigit(Peek(length)) && IsHexLetter(Peek(length + 1)))
            {
                length += 2;
            }

            return length > 1 ? length : 0;
        }

        /// <summary>
        /// The length of a <c>CharLiteral</c> such as <c>'x'</c> at the current <c>'</c>, or 0: exactly one
        /// <c>{Char Ch}</c> between two single quotes (lines 11, 41). An unmatched <c>'</c> is left to lex as
        /// <see cref="TokenKind.Unknown"/>.
        /// </summary>
        private int MatchCharLiteral() => IsCharLiteralChar(Peek(1)) && Peek(2) == '\'' ? 3 : 0;

        /// <summary>
        /// Lexes at a <c>'</c> the longer of a <c>CharLiteral</c> and a quoted <c>File</c> (lines 25, 41), the
        /// <c>CharLiteral</c> on a tie, as <c>'\'</c> is both; with neither, the <c>'</c> alone is
        /// <see cref="TokenKind.Unknown"/>.
        /// </summary>
        private void ScanQuote()
        {
            int charLength = MatchCharLiteral();
            int fileLength = MatchQuotedFile();
            if (fileLength > charLength)
            {
                Emit(TokenKind.File, fileLength);
            }
            else if (charLength > 0)
            {
                Emit(TokenKind.Character, charLength);
            }
            else
            {
                Emit(TokenKind.Unknown, 1);
            }
        }

        /// <summary>
        /// The length of a quoted <c>File</c> such as <c>'C:\Data\app.exe'</c> at the current <c>'</c>, or 0: a
        /// <c>'</c>, a file head (<see cref="FileHeadLength"/>), then the printable characters of its line through
        /// the last <c>'</c> among them (line 25). The path may itself hold a <c>'</c>, so the grammar's longest
        /// match ends at the last one, never at the first.
        /// </summary>
        private int MatchQuotedFile()
        {
            int head = FileHeadLength(1);
            if (head == 0)
            {
                return 0;
            }

            int bodyStart = 1 + head;
            int closing = text.AsSpan(index + bodyStart, PrintableRun(bodyStart)).LastIndexOf('\'');
            return closing < 0 ? 0 : bodyStart + closing + 1;
        }

        /// <summary>
        /// The length of the head of an unquoted <c>File</c> at <paramref name="offset"/>, or 0 (line 25): a letter
        /// <c>A</c>-<c>Z</c> in either case and <c>:</c>, as in <c>C:</c>; <c>..\</c>; <c>.\</c>; or <c>\</c>, which
        /// also starts a <c>\\server</c> share. The body that follows is any run of printable characters, so the
        /// longer heads of the grammar, <c>\\server</c> and repeated <c>..\</c>, give the same token.
        /// </summary>
        private int FileHeadLength(int offset)
        {
            char first = Peek(offset);
            if (char.IsAsciiLetter(first) && Peek(offset + 1) == ':')
            {
                return 2;
            }

            if (first == '\\')
            {
                return 1;
            }

            if (first != '.')
            {
                return 0;
            }

            if (Peek(offset + 1) == '\\')
            {
                return 2;
            }

            return Peek(offset + 1) == '.' && Peek(offset + 2) == '\\' ? 3 : 0;
        }

        /// <summary>
        /// The number of <c>{Printable}</c> characters from <paramref name="offset"/> on: the body of a
        /// <c>File</c>, <c>(['\']?{Printable}+)*</c> (line 25), which runs over spaces, quotes, reserved words and
        /// <c>//</c> alike and stops at a line break, a tab or any character outside the set.
        /// </summary>
        private int PrintableRun(int offset)
        {
            int count = 0;
            while (IsPrintable(Peek(offset + count)))
            {
                count++;
            }

            return count;
        }

        /// <summary>
        /// The length of a <c>Url</c> at the current position, or 0: <c>http://</c> in either case followed by one
        /// or more <c>{Url Chars}</c>, which stop at whitespace (lines 18, 26).
        /// </summary>
        private int MatchUrl()
        {
            const string Scheme = "http://";
            if (!text.AsSpan(index).StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            int length = Scheme.Length;
            while (IsUrlChar(Peek(length)))
            {
                length++;
            }

            return length > Scheme.Length ? length : 0;
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
        /// Lexes a word (lines 15-16, 30), taking the longest match as the grammar's tokenizer does. Identifiers
        /// cannot hold a <c>-</c>, so when one follows the first word, the longest hyphenated keyword that the text at
        /// this position starts with, ignoring case, becomes one keyword token spelled as written, even when a word
        /// character follows it: <c>End-After2</c> gives <c>End-After</c> and the number <c>2</c>, <c>END-IFx</c> gives
        /// <c>END-IF</c> and the identifier <c>x</c>, and <c>End-Afterwards</c> gives <c>End-After</c> and the
        /// identifier <c>wards</c>. Without such a keyword the first word alone is a keyword or an identifier and the
        /// <c>-</c> lexes as <see cref="TokenKind.MinusSign"/>, so <c>SET-NOTX</c> gives <c>SET</c>, <c>-</c> and
        /// <c>NOTX</c>.
        /// </summary>
        private void ScanWord()
        {
            int firstLength = WordLength(0);

            // The first part of every hyphenated keyword is letters, so a keyword can match only when the first word
            // ends at a '-'.
            if (Peek(firstLength) == '-')
            {
                ReadOnlySpan<char> rest = text.AsSpan(index);
                int keywordLength = 0;
                foreach (string keyword in HyphenatedKeywordSet)
                {
                    if (keyword.Length > keywordLength && rest.StartsWith(keyword, StringComparison.OrdinalIgnoreCase))
                    {
                        keywordLength = keyword.Length;
                    }
                }

                if (keywordLength > 0)
                {
                    Emit(TokenKind.Keyword, keywordLength);
                    return;
                }
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
