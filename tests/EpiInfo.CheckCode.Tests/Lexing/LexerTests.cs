using EpiInfo.CheckCode.Lexing;

namespace EpiInfo.CheckCode.Tests.Lexing;

/// <summary>
/// The check-code lexer against the lexical rules of <c>EpiInfo.Enter.Grammar.grm</c> (lines 1-51) and the two
/// seeded <c>metaViews.CheckCode</c> programs. Tests compare kinds, text and lines, never whole tokens, so columns
/// are pinned only where a test says so.
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
    [InlineData("[ Spaced ]", TokenKind.Identifier, "Spaced")]
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
        // A date is one token, not number, slash, number; a bracketed name loses its brackets and outer spaces.
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
    public void BlockCommentProducesNoTokenAndCountsItsLines(string source, TokenKind kind, string text, int line)
    {
        var tokens = Lex(source);

        Assert.Equal(new[] { kind, TokenKind.EndOfInput }, Kinds(tokens));
        Assert.Equal(text, tokens[0].Text);
        Assert.Equal(line, tokens[0].Line);
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

        // Recovery is the lexer's choice; only the final-token invariant is pinned.
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
        // The copy is exact, so these lines are read from the seeded text rather than from the plan's 15 and 27.
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
