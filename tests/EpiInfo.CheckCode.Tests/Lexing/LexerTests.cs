using System.Globalization;
using EpiInfo.CheckCode.Lexing;

namespace EpiInfo.CheckCode.Tests.Lexing;

/// <summary>
/// The check-code lexer against the lexical rules of <c>EpiInfo.Enter.Grammar.grm</c> (lines 1-51) and the two
/// seeded <c>metaViews.CheckCode</c> programs. Tests compare kinds, text and lines, and whole tokens only in the test
/// of token equality, so columns are pinned only where a test says so.
/// </summary>
public sealed class LexerTests
{
    private const string DialogMessage =
        "Warning: A fever temperature has been entered, but 'fever' was not selected as a symptom.";

    [Theory]
    [InlineData("IF", "IF")]
    [InlineData("if", "IF")]
    [InlineData("If", "IF")]
    [InlineData("END-IF", "END-IF")]
    [InlineData("end-if", "END-IF")]
    [InlineData("End-After", "END-AFTER")]
    [InlineData("ELSE-IF", "ELSE-IF")]
    [InlineData("Else-If", "ELSE-IF")]
    [InlineData("End-Field", "END-FIELD")]
    [InlineData("SET-NOT-REQUIRED", "SET-NOT-REQUIRED")]
    [InlineData("SET-REQUIRED", "SET-REQUIRED")]
    public void KeywordIsOneCaseInsensitiveToken(string source, string keyword)
    {
        var tokens = Lex(source);

        // A hyphenated keyword is one token, never identifier, minus sign, identifier.
        Assert.Equal(new[] { TokenKind.Keyword, TokenKind.EndOfInput }, Kinds(tokens));
        Assert.Equal(source, tokens[0].Text);
        Assert.Equal(keyword, tokens[0].Keyword);
        Assert.True(tokens[0].IsKeyword(keyword));
        Assert.False(tokens[0].IsKeyword("THEN"));

        // Grammar identifiers cannot contain '-', so only a single-word keyword can stand as a name.
        Assert.Equal(!keyword.Contains('-', StringComparison.Ordinal), tokens[0].CanBeName);
    }

    [Theory]
    [InlineData("END-IF", true)]
    [InlineData("ELSE-IF", true)]
    [InlineData("END-BEFORE", true)]
    [InlineData("END-AFTER", true)]
    [InlineData("END-CLICK", true)]
    [InlineData("END-FIELD", true)]
    [InlineData("end-if", true)]
    [InlineData("IF", false)]
    [InlineData("THEN", false)]
    [InlineData("ELSE", false)]
    [InlineData("END", false)]
    [InlineData("FIELD", false)]
    [InlineData("AND", false)]
    [InlineData("NOT", false)]
    [InlineData("ASSIGN", false)]
    [InlineData("GEOCODE", false)]
    [InlineData("if", false)]
    public void KeywordSetsContainTheReservedWordsIgnoringCase(string name, bool hyphenated)
    {
        Assert.Contains(name, hyphenated ? Lexer.HyphenatedKeywords : Lexer.Keywords);
    }

    [Fact]
    public void KeywordSetEntriesAreUpperCase()
    {
        Assert.All(
            Lexer.Keywords.Concat(Lexer.HyphenatedKeywords),
            entry => Assert.Equal(entry.ToUpperInvariant(), entry));
    }

    [Fact]
    public void ConditionSeparatesKeywordsNamesOperatorsAndStrings()
    {
        var tokens = Lex("IF Sex = \"1\" THEN");

        Assert.Equal(
            new[]
            {
                TokenKind.Keyword, TokenKind.Identifier, TokenKind.Equal, TokenKind.String, TokenKind.Keyword,
                TokenKind.EndOfInput,
            },
            Kinds(tokens));
        Assert.Equal("IF", tokens[0].Keyword);
        Assert.Equal("Sex", tokens[1].Text);
        Assert.Null(tokens[1].Keyword);
        Assert.True(tokens[1].CanBeName);
        Assert.Equal("1", tokens[3].Text);
        Assert.Equal("THEN", tokens[4].Keyword);
    }

    [Fact]
    public void SeededDialogTextIsOneStringWithoutItsQuotes()
    {
        var tokens = Lex("\"" + DialogMessage + "\"");

        Assert.Equal(new[] { TokenKind.String, TokenKind.EndOfInput }, Kinds(tokens));
        Assert.Equal(DialogMessage, tokens[0].Text);
    }

    [Fact]
    public void DialogMessageAndTitleAreSeparateStrings()
    {
        var strings = Lex("DIALOG \"Hi\" TITLETEXT = \"T\"")
            .Where(token => token.Kind == TokenKind.String)
            .Select(token => token.Text);

        Assert.Equal(new[] { "Hi", "T" }, strings);
    }

    [Theory]
    [InlineData("\"\"", TokenKind.String, "")]
    [InlineData("DateofInterview", TokenKind.Identifier, "DateofInterview")]
    [InlineData("NativeHawaiianOtherPacificIslander", TokenKind.Identifier, "NativeHawaiianOtherPacificIslander")]
    [InlineData("[My Field]", TokenKind.Identifier, "My Field")]
    [InlineData("[Some  Name ]", TokenKind.Identifier, "Some  Name")]
    [InlineData("[#tag]", TokenKind.Identifier, "#tag")]
    [InlineData("101.2", TokenKind.Number, "101.2")]
    [InlineData("120", TokenKind.Number, "120")]
    [InlineData("0", TokenKind.Number, "0")]
    [InlineData("01/15/2011", TokenKind.Date, "01/15/2011")]
    [InlineData("=", TokenKind.Equal, "=")]
    [InlineData("<>", TokenKind.NotEqual, "<>")]
    [InlineData("<", TokenKind.Less, "<")]
    [InlineData(">", TokenKind.Greater, ">")]
    [InlineData("<=", TokenKind.LessOrEqual, "<=")]
    [InlineData(">=", TokenKind.GreaterOrEqual, ">=")]
    [InlineData(",", TokenKind.Comma, ",")]
    [InlineData("(", TokenKind.LeftParen, "(")]
    [InlineData(")", TokenKind.RightParen, ")")]
    public void SingleTokenSourceGivesThatToken(string source, TokenKind kind, string text)
    {
        // A date is one token, not number, slash, number; a bracketed name loses its brackets and the spaces before
        // its ']', and keeps its inner spaces.
        var tokens = Lex(source);

        Assert.Equal(new[] { kind, TokenKind.EndOfInput }, Kinds(tokens));
        Assert.Equal(text, tokens[0].Text);
    }

    [Theory]
    [InlineData("(+)", TokenKind.Plus)]
    [InlineData("(-)", TokenKind.Minus)]
    [InlineData("(.)", TokenKind.Missing)]
    public void BooleanLiteralIsOneToken(string source, TokenKind kind)
    {
        var tokens = Lex(source);

        Assert.Equal(new[] { kind, TokenKind.EndOfInput }, Kinds(tokens));
        Assert.DoesNotContain(
            tokens,
            token => token.Kind is TokenKind.LeftParen or TokenKind.PlusSign or TokenKind.MinusSign
                or TokenKind.Dot or TokenKind.RightParen);
    }

    [Fact]
    public void AssignOfUncheckedLiteral()
    {
        var tokens = Lex("ASSIGN White = (-)");

        Assert.Equal(
            new[] { TokenKind.Keyword, TokenKind.Identifier, TokenKind.Equal, TokenKind.Minus, TokenKind.EndOfInput },
            Kinds(tokens));
        Assert.Equal("ASSIGN", tokens[0].Keyword);
        Assert.Equal("White", tokens[1].Text);
    }

    [Fact]
    public void SeededFeverConditionMixesKeywordsAndBooleanLiterals()
    {
        var tokens = Lex("IF NOT FeverTemp = (.) AND Fever = (-) THEN");

        Assert.Equal(
            new[]
            {
                TokenKind.Keyword, TokenKind.Keyword, TokenKind.Identifier, TokenKind.Equal, TokenKind.Missing,
                TokenKind.Keyword, TokenKind.Identifier, TokenKind.Equal, TokenKind.Minus, TokenKind.Keyword,
                TokenKind.EndOfInput,
            },
            Kinds(tokens));
        Assert.Equal(
            new[] { "IF", "NOT", "AND", "THEN" },
            tokens.Where(token => token.Kind == TokenKind.Keyword).Select(token => token.Keyword));
    }

    [Fact]
    public void FunctionCallSplitsIntoNameParenthesesAndArguments()
    {
        var tokens = Lex("YEARS(DOB, DateofInterview)");

        // Whether a function name is a keyword or an identifier is the lexer's choice; the parser accepts both.
        Assert.Equal("YEARS", tokens[0].Text);
        Assert.Contains(tokens[0].Kind, new[] { TokenKind.Keyword, TokenKind.Identifier });
        Assert.Equal(
            new[]
            {
                TokenKind.LeftParen, TokenKind.Identifier, TokenKind.Comma, TokenKind.Identifier, TokenKind.RightParen,
                TokenKind.EndOfInput,
            },
            tokens.Skip(1).Select(token => token.Kind));
        Assert.Equal("DOB", tokens[2].Text);
        Assert.Equal("DateofInterview", tokens[4].Text);
    }

    [Fact]
    public void LineCommentEndsAtTheLineBreak()
    {
        var tokens = Lex("A // comment\nB");

        Assert.Equal(new[] { TokenKind.Identifier, TokenKind.Identifier, TokenKind.EndOfInput }, Kinds(tokens));
        Assert.Equal(("A", 1), (tokens[0].Text, tokens[0].Line));
        Assert.Equal(("B", 2), (tokens[1].Text, tokens[1].Line));
    }

    [Theory]
    [InlineData("/* a\nb */ X", TokenKind.Identifier, "X", 2)]
    [InlineData("/* says \"quoted\" here */ Y", TokenKind.Identifier, "Y", 1)]
    [InlineData("/*\nx\n*/\nField", TokenKind.Keyword, "Field", 4)]
    [InlineData("/**/X", TokenKind.Identifier, "X", 1)]
    public void BlockCommentProducesNoTokenAndCountsItsLines(string source, TokenKind kind, string text, int line)
    {
        var tokens = Lex(source);

        Assert.Equal(new[] { kind, TokenKind.EndOfInput }, Kinds(tokens));
        Assert.Equal(text, tokens[0].Text);
        Assert.Equal(line, tokens[0].Line);
    }

    // Without its '*/' (line 51) a comment runs to the end of the program. It becomes one Unknown token holding the
    // raw text from its '/*' at the position of that '/*', and EndOfInput stays just past the last character.
    [Theory]
    [InlineData("/* unterminated", "/* unterminated", 1, 1, 1, 16)]
    [InlineData("/*/", "/*/", 1, 1, 1, 4)]
    [InlineData("X /* a\nb", "/* a\nb", 1, 3, 2, 2, TokenKind.Identifier)]
    [InlineData("X\r\n/* a\r\n*", "/* a\r\n*", 2, 1, 3, 2, TokenKind.Identifier)]
    public void UnterminatedBlockCommentIsOneUnknownAtItsOpening(
        string source,
        string text,
        int line,
        int column,
        int endLine,
        int endColumn,
        params TokenKind[] before)
    {
        var tokens = Lex(source);

        TokenKind[] expected = [.. before, TokenKind.Unknown, TokenKind.EndOfInput];
        Assert.Equal(expected, Kinds(tokens));
        Token comment = tokens[tokens.Count - 2];
        Assert.Equal((text, line, column), (comment.Text, comment.Line, comment.Column));
        Token end = tokens[tokens.Count - 1];
        Assert.Equal((endLine, endColumn), (end.Line, end.Column));
    }

    // {String Chars} (line 10) is {Printable} (U+0020-U+007E and U+00A0), {Letter Extended} (U+00C0-U+00FF without
    // U+00D7 and U+00F7) and {Cyrillic} (U+0400-U+04FF), less '"'. The cases cover each set and its edges.
    [Theory]
    [InlineData(" !#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`abcdefghijklmnopqrstuvwxyz{|}~")]
    [InlineData("no-break\u00A0space")]
    [InlineData("\u00C4rzte \u00E9 \u00FF")]
    [InlineData("\u00C0\u00D6\u00D8\u00F6\u00F8")]
    [InlineData("\u0418\u043C\u044F")]
    [InlineData("\u0400\u04FF")]
    public void StringOfGrammarCharactersIsOneString(string content)
    {
        var tokens = Lex("\"" + content + "\"");

        Assert.Equal(new[] { TokenKind.String, TokenKind.EndOfInput }, Kinds(tokens));
        Assert.Equal(content, tokens[0].Text);
    }

    // A closed string holding a code unit outside {String Chars} (line 10) is one Unknown token with its quotes, at
    // the opening quote, and the token after the closing quote is lexed as usual.
    [Theory]
    [InlineData("a\u0001b")]
    [InlineData("a\tb")]
    [InlineData("a\u007Fb")]
    [InlineData("\u001F")]
    [InlineData("\u0085")]
    [InlineData("\u00A1")]
    [InlineData("\u00B0")]
    [InlineData("\u00BF")]
    [InlineData("\u00D7")]
    [InlineData("\u00F7")]
    [InlineData("\u03FF")]
    [InlineData("\u0500")]
    [InlineData("\u20AC")]
    [InlineData("\u4E2D")]
    [InlineData("\uD83D\uDE00")]
    public void StringWithACharacterOutsideTheGrammarSetIsOneUnknownAndScanningResumes(string content)
    {
        string quoted = "\"" + content + "\"";

        var tokens = Lex(quoted + " X");

        Assert.Equal(new[] { TokenKind.Unknown, TokenKind.Identifier, TokenKind.EndOfInput }, Kinds(tokens));
        Assert.Equal((quoted, 1, 1), (tokens[0].Text, tokens[0].Line, tokens[0].Column));
        Assert.Equal(("X", 1, quoted.Length + 2), (tokens[1].Text, tokens[1].Line, tokens[1].Column));
    }

    // Line 30 puts an ID head or '#' straight after '[', so a name padded after its '[' is no identifier. The whole
    // span through its ']' is one Unknown token holding the raw text, so a reserved word inside it, such as END or IF,
    // never reaches the parser as a keyword, and lexing resumes after the ']'.
    [Theory]
    [InlineData("[ Spaced ]", "[ Spaced ]", null)]
    [InlineData("[\tName]", "[\tName]", null)]
    [InlineData("[ END ] X", "[ END ]", 9)]
    [InlineData("[ IF ] X", "[ IF ]", 8)]
    public void PaddedBracketedNameIsOneUnknownToken(string source, string span, int? nameColumn)
    {
        var expected = new List<(TokenKind, string, int, int)> { (TokenKind.Unknown, span, 1, 1) };
        if (nameColumn is { } column)
        {
            expected.Add((TokenKind.Identifier, "X", 1, column));
        }

        expected.Add((TokenKind.EndOfInput, string.Empty, 1, source.Length + 1));

        Assert.Equal(expected, Positions(Lex(source)));
    }

    // A grid reference is no bracketed name, padded or not, because its first element is a number.
    [Theory]
    [InlineData("Grid[1,2]")]
    [InlineData("Grid[ 1,2]")]
    public void GridReferenceKeepsItsBracketsAsSeparateTokens(string source)
    {
        var tokens = Lex(source);

        Assert.Equal(
            new[]
            {
                TokenKind.Identifier, TokenKind.LeftBracket, TokenKind.Number, TokenKind.Comma, TokenKind.Number,
                TokenKind.RightBracket, TokenKind.EndOfInput,
            },
            Kinds(tokens));
        Assert.Equal("Grid", tokens[0].Text);
    }

    // Line 30 admits {WhiteSpace} between the brackets, and the grammar's {WhiteSpace} holds CR and LF, so a bracketed
    // span, padded or not, may cross lines. The span token sits at its '[', an identifier keeps its inner line breaks
    // and loses its trailing ones, and the token after the span sits where a character-by-character scan puts it:
    // "\r\n", "\n" and a lone "\r" are one line break each, and a line break resets the column to 1. Tokens are
    // compared through their "Kind 'Text' @Line:Column" description.
    [Theory]
    [InlineData("[a\nb] Z", "Identifier 'a\nb' @1:1", "Identifier 'Z' @2:4", "EndOfInput '' @2:5")]
    [InlineData("[a\r\nb] Z", "Identifier 'a\r\nb' @1:1", "Identifier 'Z' @2:4", "EndOfInput '' @2:5")]
    [InlineData("[a\rb]", "Identifier 'a\rb' @1:1", "EndOfInput '' @2:3")]
    [InlineData("[a\n]", "Identifier 'a' @1:1", "EndOfInput '' @2:2")]
    [InlineData("[ a\nb ] Z", "Unknown '[ a\nb ]' @1:1", "Identifier 'Z' @2:5", "EndOfInput '' @2:6")]
    [InlineData("[\r\nName]", "Unknown '[\r\nName]' @1:1", "EndOfInput '' @2:6")]
    public void BracketedSpanCrossesLineBreaksAndKeepsTheLineAndColumnAfterIt(string source, params string[] expected)
    {
        Assert.Equal(expected, Lex(source).Select(token => token.ToString()));
    }

    [Theory]
    [InlineData("A\nB\nC")]
    [InlineData("A\r\nB\r\nC")]
    public void EachLineBreakAdvancesTheLineOnce(string source)
    {
        var tokens = Lex(source);

        Assert.Equal(
            new[] { TokenKind.Identifier, TokenKind.Identifier, TokenKind.Identifier, TokenKind.EndOfInput },
            Kinds(tokens));
        Assert.Equal(
            new[] { ("A", 1), ("B", 2), ("C", 3) },
            tokens.Take(3).Select(token => (token.Text, token.Line)));
    }

    [Fact]
    public void LinesAndColumnsAreOneBased()
    {
        var tokens = Lex("X\nY");

        Assert.Equal(("X", 1, 1), (tokens[0].Text, tokens[0].Line, tokens[0].Column));
        Assert.Equal(("Y", 2, 1), (tokens[1].Text, tokens[1].Line, tokens[1].Column));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void TokensCarryTheirSourceLine(string lineBreak)
    {
        var source = string.Join(
            lineBreak,
            "Field Sex",
            "\tAfter",
            "\t\tIF Sex = \"1\" THEN",
            "\t\t\tENABLE Pregnant",
            "\t\tEND-IF",
            "\tEnd-After",
            "End-Field");

        var tokens = Lex(source);

        Assert.Equal(
            new[]
            {
                (TokenKind.Keyword, "Field", 1),
                (TokenKind.Identifier, "Sex", 1),
                (TokenKind.Keyword, "After", 2),
                (TokenKind.Keyword, "IF", 3),
                (TokenKind.Identifier, "Sex", 3),
                (TokenKind.Equal, "=", 3),
                (TokenKind.String, "1", 3),
                (TokenKind.Keyword, "THEN", 3),
                (TokenKind.Keyword, "ENABLE", 4),
                (TokenKind.Identifier, "Pregnant", 4),
                (TokenKind.Keyword, "END-IF", 5),
                (TokenKind.Keyword, "End-After", 6),
                (TokenKind.Keyword, "End-Field", 7),
            },
            tokens.Take(tokens.Count - 1).Select(token => (token.Kind, token.Text, token.Line)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \t\r\n  ")]
    [InlineData("// text")]
    public void SourceWithoutTokensGivesOnlyEndOfInput(string? source)
    {
        var tokens = Lex(source);

        Assert.Equal(TokenKind.EndOfInput, Assert.Single(tokens).Kind);
    }

    [Theory]
    [InlineData("\"abc")]
    [InlineData("`")]
    [InlineData("/* unterminated")]
    [InlineData("[unterminated")]
    [InlineData("(+")]
    [InlineData("\u0000")]
    public void HostileInputNeverThrows(string source)
    {
        Assert.Null(Record.Exception(() => Lexer.Tokenize(source)));

        // This theory pins only that lexing does not throw and the final-token invariant; the tokens each input
        // recovers to are pinned by the dedicated tests.
        Lex(source);
    }

    [Theory]
    [InlineData("CaseReport")]
    [InlineData("FoodHistory")]
    public void SeededProgramHasNoUnknownToken(string form)
    {
        var tokens = Lex(SeededProgram(form));

        var unknown = tokens
            .Where(token => token.Kind == TokenKind.Unknown)
            .Select(token => $"'{token.Text}' on line {token.Line}")
            .ToArray();
        Assert.Empty(unknown);
    }

    [Fact]
    public void CaseReportLeadingBlockCommentProducesNoToken()
    {
        var tokens = Lex(CaseReportProgram);

        var first = tokens[0];
        Assert.Equal(TokenKind.Keyword, first.Kind);
        Assert.Equal("Field", first.Text);
        Assert.Equal("FIELD", first.Keyword);
        Assert.Equal((6, 1), (first.Line, first.Column));
        Assert.DoesNotContain(tokens, token => token.Line < 6);

        // The comment quotes "find code block", so its first real string is the one on line 9.
        var firstString = tokens.First(token => token.Kind == TokenKind.String);
        Assert.Equal(("1", 9), (firstString.Text, firstString.Line));
    }

    [Fact]
    public void CaseReportTokensSitOnTheirSeededLines()
    {
        var tokens = Lex(CaseReportProgram);

        Assert.Equal(
            new[] { 6, 18 },
            tokens.Where(token => token.Keyword == "FIELD").Select(token => token.Line));

        // The seeded program closes its IF blocks on lines 14 and 26; End-After and End-Before follow on 15 and 27.
        Assert.Equal(
            new[] { 14, 26 },
            tokens.Where(token => token.Keyword == "END-IF").Select(token => token.Line));

        var last = tokens[tokens.Count - 2];
        Assert.Equal(("End-Field", "END-FIELD", 28), (last.Text, last.Keyword, last.Line));
    }

    [Fact]
    public void FoodHistoryTokensSitOnTheirSeededLines()
    {
        var tokens = Lex(FoodHistoryProgram);

        Assert.Equal(6, Assert.Single(tokens, token => SpelledAs(token, "YEARS")).Line);
        Assert.Equal(12, Assert.Single(tokens, token => token.Keyword == "GEOCODE").Line);
        Assert.Equal(18, Assert.Single(tokens, token => SpelledAs(token, "EPIWEEK")).Line);
        Assert.Equal(103, Assert.Single(tokens, token => token.Keyword == "IF").Line);

        var dialog = Assert.Single(tokens, token => token.Kind == TokenKind.String);
        Assert.Equal((DialogMessage, 104), (dialog.Text, dialog.Line));

        var last = tokens[tokens.Count - 2];
        Assert.Equal(("End-Field", "END-FIELD", 107), (last.Text, last.Keyword, last.Line));
    }

    // A string cannot span lines (line 24), so an unclosed one is a single Unknown from its quote to the line end,
    // and the next line lexes as usual.
    [Theory]
    [InlineData("\"abc\nX", 1, "Unknown \"abc", "Identifier X")]
    [InlineData("\"abc\r\nX", 1, "Unknown \"abc", "Identifier X")]
    [InlineData("DIALOG \"abc\nASSIGN", 8, "Keyword DIALOG", "Unknown \"abc", "Keyword ASSIGN")]
    public void UnterminatedStringIsOneUnknownFromItsQuoteAndLexingResumesOnTheNextLine(
        string source,
        int quoteColumn,
        params string[] expected)
    {
        var tokens = Lex(source);

        Assert.Equal(Pairs(expected), KindsAndTexts(tokens));
        var unknown = Assert.Single(tokens, token => token.Kind == TokenKind.Unknown);
        Assert.Equal((1, quoteColumn), (unknown.Line, unknown.Column));
        var resumed = tokens[tokens.Count - 2];
        Assert.Equal((2, 1), (resumed.Line, resumed.Column));
    }

    [Fact]
    public void HyphenatedKeywordBrokenAcrossALineIsNotJoined()
    {
        var tokens = Lex("END-\nIF");

        Assert.Equal(
            new[] { (TokenKind.Keyword, "END", 1), (TokenKind.MinusSign, "-", 1), (TokenKind.Keyword, "IF", 2) },
            tokens.Take(tokens.Count - 1).Select(token => (token.Kind, token.Text, token.Line)));
    }

    // "\n\r" is a line feed followed by a lone carriage return, so it is two line breaks, never a reversed "\r\n".
    [Theory]
    [InlineData("A\rB\rC", 1, 2, 3)]
    [InlineData("A\r\rB", 1, 3)]
    [InlineData("A\n\rB", 1, 3)]
    public void LoneCarriageReturnIsOneLineBreak(string source, params int[] lines)
    {
        var tokens = Lex(source);

        Assert.All(tokens.Take(tokens.Count - 1), token => Assert.Equal(TokenKind.Identifier, token.Kind));
        Assert.Equal(lines, tokens.Take(tokens.Count - 1).Select(token => token.Line));
    }

    [Fact]
    public void KeywordsIgnoreTheTurkishCulture()
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        CultureInfo uiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo turkish = CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentCulture = turkish;
            CultureInfo.CurrentUICulture = turkish;

            // The culture is in force: a culture-sensitive upper-casing now gives a dotted capital I (U+0130).
            Assert.Equal("\u0130F", "if".ToUpper(CultureInfo.CurrentCulture));

            var tokens = Lex("if field end-if");

            Assert.Equal(
                new (TokenKind, string?)[]
                {
                    (TokenKind.Keyword, "IF"), (TokenKind.Keyword, "FIELD"), (TokenKind.Keyword, "END-IF"),
                },
                tokens.Take(tokens.Count - 1).Select(token => (token.Kind, token.Keyword)));
            Assert.True(tokens[0].IsKeyword("IF"));
            Assert.Contains("if", Lexer.Keywords);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = uiCulture;
        }
    }

    // Each source is one token spelled as written: a number with a leading point or an exponent (lines 35-36), a time
    // (line 45) or a date (line 44); a plain name, whose {ID Head} admits '_', '@' and letters beyond ASCII and whose
    // {ID Tail} adds digits, '#' and '$' (lines 15-16, 30); or a one-character operator or punctuation mark.
    [Theory]
    [InlineData(".5", TokenKind.Number)]
    [InlineData("1e5", TokenKind.Number)]
    [InlineData("2E-3", TokenKind.Number)]
    [InlineData("1.5E+10", TokenKind.Number)]
    [InlineData(".5e2", TokenKind.Number)]
    [InlineData("37.5", TokenKind.Number)]
    [InlineData("12:30", TokenKind.Time)]
    [InlineData("9:05:59", TokenKind.Time)]
    [InlineData("24:00", TokenKind.Time)]
    [InlineData("1:2:3", TokenKind.Time)]
    [InlineData("1-15-2011", TokenKind.Date)]
    [InlineData("12/31/12", TokenKind.Date)]
    [InlineData("_x@1#$", TokenKind.Identifier)]
    [InlineData("\u00C4rzte", TokenKind.Identifier)]
    [InlineData("\u0418\u043C\u044F", TokenKind.Identifier)]
    [InlineData("@a", TokenKind.Identifier)]
    [InlineData("&", TokenKind.Ampersand)]
    [InlineData("+", TokenKind.PlusSign)]
    [InlineData("-", TokenKind.MinusSign)]
    [InlineData("*", TokenKind.Asterisk)]
    [InlineData("/", TokenKind.Slash)]
    [InlineData("^", TokenKind.Caret)]
    [InlineData("%", TokenKind.Percent)]
    [InlineData("!", TokenKind.Bang)]
    [InlineData(":", TokenKind.Colon)]
    [InlineData(".", TokenKind.Dot)]
    [InlineData("]", TokenKind.RightBracket)]
    public void SourceIsOneTokenSpelledAsWritten(string source, TokenKind kind)
    {
        Assert.Equal(new[] { (kind, source) }, KindsAndTexts(Lex(source)));
    }

    // Each token is the longest match at its position, and whatever it leaves lexes on its own:
    // - a hyphenated keyword is the longest match even when a word character follows it, so END-IF-x, END-IFx,
    //   End-After2 and End-Afterwards keep END-IF or End-After, while a hyphen outside a whole keyword splits, as in
    //   Age-Years, SET-NOT, SET-NOTX and a hyphen with spaces around it;
    // - a point or exponent joins a number only when digits complete it, an hour stops at 24 and a year at four
    //   digits;
    // - a parenthesised sign is a boolean literal (line 42) only as "(+)", "(-)" or "(.)", and a two-character
    //   comparison is one token only when its characters touch;
    // - a character that starts no terminal is one Unknown token, and lexing resumes at the character after it;
    // - a '[' stays a token of its own, padded or not, when no ']' closes it through ID characters and whitespace.
    [Theory]
    [InlineData("END-IF-x", "Keyword END-IF", "MinusSign -", "Identifier x")]
    [InlineData("END-IFx", "Keyword END-IF", "Identifier x")]
    [InlineData("End-After2", "Keyword End-After", "Number 2")]
    [InlineData("End-Afterwards", "Keyword End-After", "Identifier wards")]
    [InlineData("END - IF", "Keyword END", "MinusSign -", "Keyword IF")]
    [InlineData("Age-Years", "Identifier Age", "MinusSign -", "Identifier Years")]
    [InlineData("SET-NOT", "Identifier SET", "MinusSign -", "Keyword NOT")]
    [InlineData("SET-NOTX", "Identifier SET", "MinusSign -", "Identifier NOTX")]
    [InlineData("5.", "Number 5", "Dot .")]
    [InlineData("2E", "Number 2", "Identifier E")]
    [InlineData("1e+", "Number 1", "Identifier e", "PlusSign +")]
    [InlineData("25:00", "Number 25", "Colon :", "Number 00")]
    [InlineData("12:30:", "Time 12:30", "Colon :")]
    [InlineData("1/2/20115", "Date 1/2/2011", "Number 5")]
    [InlineData("123-4-5", "Number 123", "MinusSign -", "Number 4", "MinusSign -", "Number 5")]
    [InlineData("1-2", "Number 1", "MinusSign -", "Number 2")]
    [InlineData("( - )", "LeftParen (", "MinusSign -", "RightParen )")]
    [InlineData("(+", "LeftParen (", "PlusSign +")]
    [InlineData("a<>b", "Identifier a", "NotEqual <>", "Identifier b")]
    [InlineData("a<=b", "Identifier a", "LessOrEqual <=", "Identifier b")]
    [InlineData("a>=b", "Identifier a", "GreaterOrEqual >=", "Identifier b")]
    [InlineData("< >", "Less <", "Greater >")]
    [InlineData("; ASSIGN", "Unknown ;", "Keyword ASSIGN")]
    [InlineData("` Y", "Unknown `", "Identifier Y")]
    [InlineData("[ Name", "LeftBracket [", "Identifier Name")]
    [InlineData("[Name", "LeftBracket [", "Identifier Name")]
    [InlineData("[ a = b]", "LeftBracket [", "Identifier a", "Equal =", "Identifier b", "RightBracket ]")]
    public void LongestMatchSplitsTheSourceIntoTheseTokens(string source, params string[] expected)
    {
        Assert.Equal(Pairs(expected), KindsAndTexts(Lex(source)));
    }

    // A character that begins no token is one Unknown token holding it, and a surrogate pair stays one token. The
    // cases are chars because a custom attribute stores a string argument as UTF-8, which cannot hold a lone
    // surrogate.
    [Theory]
    [InlineData(';')]
    [InlineData('?')]
    [InlineData('{')]
    [InlineData('`')]
    [InlineData('\u0001')]
    [InlineData('\0')]
    [InlineData('\uD800')]
    [InlineData('\uD83D', '\uDE00')]
    public void CharacterThatBeginsNoTokenIsOneUnknownToken(params char[] characters)
    {
        string source = new(characters);

        Assert.Equal(new[] { (TokenKind.Unknown, source) }, KindsAndTexts(Lex(source)));
    }

    [Fact]
    public void LoneSurrogateIsOneUnknownAndTheLetterAfterItIsAnIdentifier()
    {
        Assert.Equal(
            new[] { (TokenKind.Unknown, "\uD800"), (TokenKind.Identifier, "X") },
            KindsAndTexts(Lex("\uD800X")));
    }

    [Fact]
    public void BlockCommentOverMixedLineBreaksKeepsTheLineAndColumnAfterIt()
    {
        var tokens = Lex("/* a\n b\r\n c */ Field X");

        Assert.Equal(new[] { TokenKind.Keyword, TokenKind.Identifier, TokenKind.EndOfInput }, Kinds(tokens));
        Assert.Equal(
            new[] { ("Field", 3, 7), ("X", 3, 13) },
            tokens.Take(2).Select(token => (token.Text, token.Line, token.Column)));
    }

    [Fact]
    public void FoodHistoryTokensSitOnTheirSeededColumns()
    {
        var tokens = Lex(FoodHistoryProgram);

        // Line 6 is two tabs, then ASSIGN AGE = YEARS(DOB, DateofInterview); a tab counts as one column.
        Assert.Equal(
            new[]
            {
                ("ASSIGN", 3), ("AGE", 10), ("=", 14), ("YEARS", 16), ("(", 21), ("DOB", 22), (",", 25),
                ("DateofInterview", 27), (")", 42),
            },
            tokens.Where(token => token.Line == 6).Select(token => (token.Text, token.Column)));

        var geocodeLine = tokens.Where(token => token.Line == 12).ToArray();
        Assert.Equal(3, geocodeLine[0].Column);
        Assert.Equal(
            new[]
            {
                (TokenKind.Keyword, "GEOCODE"), (TokenKind.Identifier, "PatientAddress"), (TokenKind.Comma, ","),
                (TokenKind.Identifier, "Latitude"), (TokenKind.Comma, ","), (TokenKind.Identifier, "Longitude"),
            },
            geocodeLine.Select(token => (token.Kind, token.Text)));

        Assert.Equal(
            new[]
            {
                TokenKind.Keyword, TokenKind.Keyword, TokenKind.Identifier, TokenKind.Equal, TokenKind.Missing,
                TokenKind.Keyword, TokenKind.Identifier, TokenKind.Equal, TokenKind.Minus, TokenKind.Keyword,
            },
            Kinds(tokens.Where(token => token.Line == 103)));

        // The program ends with the line break after End-Field on line 107.
        var end = tokens[tokens.Count - 1];
        Assert.Equal((108, 1), (end.Line, end.Column));
    }

    [Fact]
    public void CaseReportTokensSitOnTheirSeededColumns()
    {
        var tokens = Lex(CaseReportProgram);

        // The first field block closes on lines 14-16, indented by two tabs, one tab and none.
        Assert.Equal(
            new (string?, int, int)[] { ("END-IF", 14, 3), ("END-AFTER", 15, 2), ("END-FIELD", 16, 1) },
            tokens
                .SkipWhile(token => token.Keyword != "END-IF")
                .Take(3)
                .Select(token => (token.Keyword, token.Line, token.Column)));

        Assert.Equal(
            new[] { (TokenKind.Identifier, "Sex", 6), (TokenKind.Identifier, "LastName", 18) },
            tokens
                .Where((_, index) => index > 0 && tokens[index - 1].Keyword == "FIELD")
                .Select(token => (token.Kind, token.Text, token.Line)));

        // The program ends with the line break after End-Field on line 28.
        var end = tokens[tokens.Count - 1];
        Assert.Equal((29, 1), (end.Line, end.Column));
    }

    [Theory]
    [InlineData("CaseReport", "\r\n")]
    [InlineData("CaseReport", "\r")]
    [InlineData("FoodHistory", "\r\n")]
    [InlineData("FoodHistory", "\r")]
    public void SeededProgramLexesTheSameWithEveryLineBreak(string form, string lineBreak)
    {
        string program = SeededProgram(form);

        Assert.Equal(
            Lex(program).Select(token => (token.Kind, token.Text, token.Line)),
            Lex(program.Replace("\n", lineBreak, StringComparison.Ordinal))
                .Select(token => (token.Kind, token.Text, token.Line)));
    }

    [Theory]
    [InlineData(null, 1, 1)]
    [InlineData("", 1, 1)]
    [InlineData("   \t\r\n  ", 2, 3)]
    [InlineData("// text", 1, 8)]
    [InlineData("X", 1, 2)]
    [InlineData("X\n", 2, 1)]
    [InlineData("X\r\n", 2, 1)]
    [InlineData("X\r", 2, 1)]
    public void EndOfInputSitsJustPastTheLastCharacter(string? source, int line, int column)
    {
        var tokens = Lex(source);

        var end = tokens[tokens.Count - 1];
        Assert.Equal((string.Empty, line, column), (end.Text, end.Line, end.Column));
    }

    // A fixed seed lexes the same inputs on every run, so a failure names an input that reproduces it. Three code
    // units in four come from the characters the lexer branches on; the fourth is any UTF-16 code unit, lone
    // surrogates included.
    [Fact]
    public void RandomTextKeepsTheTokenListInvariants()
    {
        const string Branching =
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789[]()/*-.+:#'<>=,& \t\r\n\"";
        var random = new Random(20110115);

        for (int i = 0; i < 10_000; i++)
        {
            var units = new char[random.Next(41)];
            for (int k = 0; k < units.Length; k++)
            {
                units[k] = random.Next(4) == 0 ? (char)random.Next(0x10000) : Branching[random.Next(Branching.Length)];
            }

            string text = new(units);
            string input = $"Input {i} \"{Escape(text)}\"";

            IReadOnlyList<Token> tokens = [];
            Exception? error = Record.Exception(() => tokens = Lexer.Tokenize(text));

            Assert.True(error is null, $"{input} threw {error}");
            Assert.True(
                tokens.Count > 0 && tokens[tokens.Count - 1].Kind == TokenKind.EndOfInput,
                $"{input} does not end with EndOfInput.");
            Assert.True(
                tokens.Count(token => token.Kind == TokenKind.EndOfInput) == 1,
                $"{input} holds more than one EndOfInput.");
            Assert.True(
                tokens.All(token => token.Line >= 1 && token.Column >= 1),
                $"{input} has a token before line 1 or column 1.");
            Assert.True(
                tokens.Zip(tokens.Skip(1)).All(pair => pair.Second.Line >= pair.First.Line),
                $"{input} has a token on an earlier line than the token before it.");
        }

        // Writes the input as the content of a C# string literal, so a failing case can be pasted back.
        static string Escape(string text) => string.Concat(text.Select(c =>
            c is >= ' ' and <= '~' and not '"' and not '\\'
                ? c.ToString()
                : "\\u" + ((int)c).ToString("X4", CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void TokenEqualityComparesEveryMember()
    {
        var token = new Token(TokenKind.Keyword, "IF", 1, 1);
        var same = new Token(TokenKind.Keyword, "IF", 1, 1);

        Assert.Equal(token, same);
        Assert.Equal(token.GetHashCode(), same.GetHashCode());
        Assert.True(token == same);
        Assert.NotEqual(token, new Token(TokenKind.Identifier, "IF", 1, 1));
        Assert.NotEqual(token, new Token(TokenKind.Keyword, "THEN", 1, 1));
        Assert.NotEqual(token, token with { Line = 2 });
        Assert.NotEqual(token, token with { Column = 2 });

        // Lexing the same text twice gives separate, equal lists; the only place whole token lists are compared.
        var first = Lexer.Tokenize(FoodHistoryProgram);
        var second = Lexer.Tokenize(FoodHistoryProgram);
        Assert.NotSame(first, second);
        Assert.Equal(first, second);
    }

    [Fact]
    public void TokenDescribesItselfAndRejectsANullKeywordQuery()
    {
        var token = new Token(TokenKind.Keyword, "IF", 1, 1);

        Assert.Equal("Keyword 'IF' @1:1", token.ToString());
        Assert.Null(new Token(TokenKind.Identifier, "IF", 1, 1).Keyword);
        Assert.Throws<ArgumentNullException>(() => token.IsKeyword(null!));
    }

    /// <summary>
    /// Tokenizes <paramref name="text"/> and checks the invariant every token list keeps: it ends with exactly one
    /// <see cref="TokenKind.EndOfInput"/>.
    /// </summary>
    private static IReadOnlyList<Token> Lex(string? text)
    {
        var tokens = Lexer.Tokenize(text);

        Assert.NotEmpty(tokens);
        Assert.Equal(TokenKind.EndOfInput, tokens[tokens.Count - 1].Kind);
        Assert.Single(tokens, token => token.Kind == TokenKind.EndOfInput);
        return tokens;
    }

    private static IEnumerable<TokenKind> Kinds(IEnumerable<Token> tokens) => tokens.Select(token => token.Kind);

    // Function names are matched by spelling, whichever kind the lexer gives them.
    private static bool SpelledAs(Token token, string name) =>
        string.Equals(token.Text, name, StringComparison.OrdinalIgnoreCase);

    /// <summary>The kind and text of every token before <see cref="TokenKind.EndOfInput"/>.</summary>
    private static IEnumerable<(TokenKind Kind, string Text)> KindsAndTexts(IReadOnlyList<Token> tokens) =>
        tokens.Take(tokens.Count - 1).Select(token => (token.Kind, token.Text));

    /// <summary>The kind, text, line and column of every token, <see cref="TokenKind.EndOfInput"/> included.</summary>
    private static IEnumerable<(TokenKind Kind, string Text, int Line, int Column)> Positions(
        IEnumerable<Token> tokens) =>
        tokens.Select(token => (token.Kind, token.Text, token.Line, token.Column));

    /// <summary>
    /// Reads expected tokens written as "Kind Text" pairs such as <c>"MinusSign -"</c>: a <see cref="TokenKind"/>
    /// name, one space, then the exact token text.
    /// </summary>
    private static IEnumerable<(TokenKind Kind, string Text)> Pairs(IEnumerable<string> pairs) =>
        pairs.Select(pair =>
        {
            int space = pair.IndexOf(' ', StringComparison.Ordinal);
            Assert.True(space > 0, $"'{pair}' is not a \"Kind Text\" pair.");
            return (Enum.Parse<TokenKind>(pair[..space]), pair[(space + 1)..]);
        });

    private static string SeededProgram(string form) => form switch
    {
        "CaseReport" => CaseReportProgram,
        "FoodHistory" => FoodHistoryProgram,
        _ => throw new ArgumentOutOfRangeException(nameof(form), form, "Not a seeded form."),
    };

    // metaViews.CheckCode of CaseReport: testdata/sql/03_forms.sql lines 26-53 hold program lines 1-28 (program line
    // N is file line 25 + N). The copy is byte for byte, tabs and the closing line break included.
    private static readonly string CaseReportProgram = """
        /*
        	1. use the "find code block" to navigate and create code blocks.
        	2. all check code needs to appear inside of checkcode blocks.
        	
        */
        Field Sex
        	After
        		//add code here
        		IF Sex = "1" THEN
        			ENABLE Pregnant
        		ELSE
        			DISABLE Pregnant
        			CLEAR Pregnant
        		END-IF
        	End-After
        End-Field

        Field LastName
        	Before
        		//add code here
        		IF Sex = "1" THEN
        			ENABLE Pregnant
        		ELSE
        			DISABLE Pregnant
        			CLEAR Pregnant
        		END-IF
        	End-Before
        End-Field

        """.ReplaceLineEndings("\n");

    // metaViews.CheckCode of FoodHistory: testdata/sql/03_forms.sql lines 55-161 hold program lines 1-107 (program
    // line N is file line 54 + N). The copy is byte for byte, tabs and the closing line break included, with the SQL
    // '' escape of the DIALOG line turned back into '.
    private static readonly string FoodHistoryProgram = """
        Field DOB
        	After
        		// Assigns the difference in years between the value entered into DOB and the value
        		// entered into DateOfInterview to the AGE field. This acts as an automatic age
        		// calculator during data entry.
        		ASSIGN AGE = YEARS(DOB, DateofInterview)
        	End-After
        End-Field

        Field GetCoordinates
        	Click
        		GEOCODE PatientAddress, Latitude, Longitude
        	End-Click
        End-Field

        Field OnsetDate
        	After
        		ASSIGN OnsetWeek = EPIWEEK(OnsetDate)
        	End-After
        End-Field

        Field Asian
        	Click
        		ASSIGN Black = (-)
        		ASSIGN AmericanIndianAlaskanNative = (-)
        		ASSIGN NativeHawaiianOtherPacificIslander = (-)
        		ASSIGN White = (-)
        		ASSIGN Multiracial = (-)
        		ASSIGN UnknownOther = (-)
        	End-Click
        End-Field

        Field Black
        	Click
        		ASSIGN Asian = (-)
        		ASSIGN AmericanIndianAlaskanNative = (-)
        		ASSIGN NativeHawaiianOtherPacificIslander = (-)
        		ASSIGN White = (-)
        		ASSIGN Multiracial = (-)
        		ASSIGN UnknownOther = (-)
        	End-Click
        End-Field

        Field Multiracial
        	Click
        		ASSIGN Asian = (-)
        		ASSIGN AmericanIndianAlaskanNative = (-)
        		ASSIGN NativeHawaiianOtherPacificIslander = (-)
        		ASSIGN White = (-)
        		ASSIGN Black = (-)
        		ASSIGN UnknownOther = (-)
        	End-Click
        End-Field

        Field NativeHawaiianOtherPacificIslander
        	Click
        		ASSIGN Asian = (-)
        		ASSIGN AmericanIndianAlaskanNative = (-)
        		ASSIGN Multiracial = (-)
        		ASSIGN White = (-)
        		ASSIGN Black = (-)
        		ASSIGN UnknownOther = (-)
        	End-Click
        End-Field

        Field UnknownOther
        	Click
        		ASSIGN Asian = (-)
        		ASSIGN AmericanIndianAlaskanNative = (-)
        		ASSIGN Multiracial = (-)
        		ASSIGN White = (-)
        		ASSIGN Black = (-)
        		ASSIGN NativeHawaiianOtherPacificIslander = (-)
        	End-Click
        End-Field

        Field AmericanIndianAlaskanNative
        	Click
        		ASSIGN Asian = (-)
        		ASSIGN UnknownOther = (-)
        		ASSIGN Multiracial = (-)
        		ASSIGN White = (-)
        		ASSIGN Black = (-)
        		ASSIGN NativeHawaiianOtherPacificIslander = (-)
        	End-Click
        End-Field

        Field White
        	Click
        		ASSIGN Asian = (-)
        		ASSIGN UnknownOther = (-)
        		ASSIGN Multiracial = (-)
        		ASSIGN AmericanIndianAlaskanNative = (-)
        		ASSIGN Black = (-)
        		ASSIGN NativeHawaiianOtherPacificIslander = (-)
        	End-Click
        End-Field

        Field FeverTemp
        	After
        		// if the fever temperature field has been filled in and the fever checkbox is unchecked,
        		// prompt the user that there might be a problem.
        		IF NOT FeverTemp = (.) AND Fever = (-) THEN
        			DIALOG "Warning: A fever temperature has been entered, but 'fever' was not selected as a symptom."
        		END-IF
        	End-After
        End-Field

        """.ReplaceLineEndings("\n");
}
