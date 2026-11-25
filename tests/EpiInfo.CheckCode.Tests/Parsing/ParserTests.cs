using EpiInfo.CheckCode.Lexing;
using EpiInfo.CheckCode.Parsing;
using EpiInfo.CheckCode.Syntax;
using EpiInfo.Domain.Diagnostics;
using EpiInfo.Domain.Events;
using EpiInfo.Domain.Metadata;
using EpiInfo.Domain.Values;

namespace EpiInfo.CheckCode.Tests.Parsing;

/// <summary>
/// Pins the check-code <see cref="Parser"/>: the executed subset, operator precedence, and recovery at grammar
/// boundaries, where every construct outside the subset becomes one diagnostic and the rest of the program still
/// parses. Grammar citations in comments, such as "(line 629)", refer to <c>EpiInfo.Enter.Grammar.grm</c>; program
/// line 1 is the first content line of each raw literal. Diagnostics are compared by severity, command and line,
/// never by message text.
/// </summary>
public sealed class ParserTests
{
    // Parser.Commands has no constant for these spellings: an unsupported statement is reported under its own keyword
    // and an unregistered function under its name, both in upper case.
    private const string Goto = "GOTO";
    private const string Hide = "HIDE";
    private const string Days = "DAYS";

    // The nested recovery program: line 3 is the outer IF, 4 the inner IF with OR, 8 its END, 9 the GOTO, 10 the
    // ASSIGN after it, 11 the outer END-IF and 12 the ASSIGN after the outer IF.
    private const string NestedRecoveryProgram = """
        Field X
        	After
        		IF A = "1" THEN
        			IF A = "2" OR A = "3" THEN
        				ASSIGN Y = "inner-then"
        			ELSE
        				ASSIGN Y = "inner-else"
        			END
        			GOTO Z
        			ASSIGN Z = "after-goto"
        		END-IF
        		ASSIGN W = "after-outer"
        	End-After
        End-Field
        """;

    // One block of each unsupported kind (lines 624-640), opening on lines 1, 6, 11, 16 and 19; Field X is line 22.
    private const string UnsupportedBlocksProgram = """
        View
        	Before
        		ASSIGN Z = "view"
        	End-Before
        End-View
        Record
        	After
        		ASSIGN Z = "record"
        	End-After
        End-Record
        Page Page1
        	Before
        		ASSIGN Z = "page"
        	End-Before
        End-Page
        DefineVariables
        	DEFINE V NUMERIC
        End-DefineVariables
        Sub S
        	ASSIGN Z = "sub"
        End-Sub
        Field X
        	After
        		ASSIGN Z = "ok"
        	End-After
        End-Field
        """;

    // Form is the synonym of View (line 629).
    private const string FormBlockProgram = """
        Form
        	After
        		ASSIGN Z = "f"
        	End-After
        End-Form
        Field X
        	After
        		ASSIGN Z = "ok"
        	End-After
        End-Field
        """;

    private const string UpperCaseProgram = """
        FIELD X
        	AFTER
        		IF A = "1" THEN
        			ASSIGN Y = "1"
        		END-IF
        	END-AFTER
        END-FIELD
        """;

    private const string MixedCaseProgram = """
        field X
        	after
        		if A = "1" then
        			assign Y = "1"
        		end-if
        	End-After
        end-field
        """;

    // Field X is line 4, the IF line 7 and the ASSIGN line 10, after a block comment spanning lines 8-9.
    private const string CommentedProgram = """
        // A line comment before the block.
        /* A block comment
           over two lines. */
        Field X
        	After
        		// A line comment inside the block.
        		IF A = "1" THEN
        			/* A block comment
        			   inside the IF. */
        			ASSIGN Y = "1"
        		END-IF
        	End-After
        End-Field
        """;

    [Fact]
    public void NestedProgramKeepsTheStatementsAroundItsSkippedConstructs()
    {
        ProgramNode program = Parse(NestedRecoveryProgram);

        FieldBlockNode block = Assert.Single(program.FieldBlocks);
        Assert.Equal("X", block.FieldName);
        EventBlockNode after = Assert.Single(block.Events);
        Assert.Equal(FieldEventKind.FieldAfter, after.Event);

        Assert.Collection(
            after.Statements,
            statement =>
            {
                IfStatementNode outer = Assert.IsType<IfStatementNode>(statement);
                EqualsNode condition = Assert.IsType<EqualsNode>(outer.Condition);
                AssertIdentifier(condition.Left, "A");
                AssertStringLiteral(condition.Right, "1");

                // The inner IF uses OR, so it is dropped whole; the GOTO is dropped alone.
                AssertAssign(Assert.Single(outer.Then), "Z", "after-goto");
                Assert.Null(outer.Else);
            },
            statement => AssertAssign(statement, "W", "after-outer"));

        var expected = new[] { Warning(Parser.Commands.Or, 4), Warning(Goto, 9) };
        Assert.Equal(expected, Summaries(after.Diagnostics));
        Assert.Equal(expected, Summaries(program.Diagnostics));
    }

    [Fact]
    public void NestedProgramRunsTheKeptStatementsWhenTheOuterConditionHolds()
    {
        ICheckCodeEngine engine = new CheckCodeEngine();

        FieldEventResponse response = engine.Run(
            NestedRecoveryProgram,
            TextForm("X", "A", "Y", "Z", "W"),
            FieldEventKind.FieldAfter,
            "X",
            TextValues(("A", "1")));

        Assert.Equal(new[] { "Z", "W" }, response.Assigned.Keys);
        Assert.Equal("after-goto", response.Assigned["Z"].AsText());
        Assert.Equal("after-outer", response.Assigned["W"].AsText());

        // Neither branch of the dropped inner IF ran.
        Assert.False(response.Assigned.ContainsKey("Y"));
        Assert.DoesNotContain(response.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void NestedProgramRunsOnlyTheStatementAfterTheOuterIfWhenItsConditionFails()
    {
        ICheckCodeEngine engine = new CheckCodeEngine();

        FieldEventResponse response = engine.Run(
            NestedRecoveryProgram,
            TextForm("X", "A", "Y", "Z", "W"),
            FieldEventKind.FieldAfter,
            "X",
            TextValues(("A", "2")));

        Assert.Equal(new[] { "W" }, response.Assigned.Keys);
    }

    [Fact]
    public void ElseIfDropsItsWholeIf()
    {
        EventBlockNode after = OnlyEvent(Parse(InAfterBlock(
            "IF A = \"1\" THEN",
            "ASSIGN Y = \"one\"",
            "ELSE-IF A = \"2\" THEN",
            "ASSIGN Y = \"two\"",
            "END-IF",
            "ASSIGN Z = \"after\"")));

        AssertAssign(Assert.Single(after.Statements), "Z", "after");
        Assert.Equal(new[] { Warning(Parser.Commands.ElseIf, 5) }, Summaries(after.Diagnostics));
    }

    [Fact]
    public void UnsupportedBlocksAreSkippedToTheirTerminators()
    {
        ProgramNode program = Parse(UnsupportedBlocksProgram);

        FieldBlockNode block = Assert.Single(program.FieldBlocks);
        Assert.Equal("X", block.FieldName);
        Assert.Equal(22, block.Line);
        EventBlockNode after = Assert.Single(block.Events);
        AssertAssign(Assert.Single(after.Statements), "Z", "ok");
        Assert.Empty(after.Diagnostics);

        Assert.Equal(
            new[]
            {
                Warning(Parser.Commands.View, 1),
                Warning(Parser.Commands.Record, 6),
                Warning(Parser.Commands.Page, 11),
                Warning(Parser.Commands.DefineVariables, 16),
                Warning(Parser.Commands.Sub, 19),
            },
            Summaries(program.Diagnostics));
    }

    [Fact]
    public void FormBlockIsSkippedLikeAViewBlock()
    {
        ProgramNode program = Parse(FormBlockProgram);

        Assert.Equal("X", Assert.Single(program.FieldBlocks).FieldName);
        Assert.Equal(new[] { Warning(Parser.Commands.Form, 1) }, Summaries(program.Diagnostics));
    }

    // Each case's skipped construct starts on line 3; the ASSIGN after it must survive the recovery.
    [Theory]
    [InlineData(Goto, "GOTO Z")]
    [InlineData(Hide, "HIDE A", "B")]
    [InlineData(Days, "ASSIGN Y = DAYS(A, B)")]
    [InlineData(Parser.Commands.Plus, "ASSIGN Y = 1 + 2")]
    [InlineData(Parser.Commands.Concatenate, "ASSIGN Y = A & B")]
    [InlineData(Parser.Commands.EnableExcept, "ENABLE * EXCEPT A")]
    [InlineData(Parser.Commands.CheckedLiteral, "IF A = (+) THEN", "ASSIGN Y = \"1\"", "END-IF")]
    [InlineData(Parser.Commands.NotEqual, "IF A <> \"1\" THEN", "ASSIGN Y = \"1\"", "END-IF")]
    public void UnsupportedConstructIsDroppedWithOneWarningAndTheNextStatementIsKept(
        string command,
        params string[] skippedLines)
    {
        ProgramNode program = Parse(InAfterBlock([.. skippedLines, "ASSIGN Z = \"x\""]));

        EventBlockNode after = OnlyEvent(program);
        AssertAssign(Assert.Single(after.Statements), "Z", "x");

        // One warning only: a name list continued on the next line, such as HIDE's B, is skipped with its statement.
        Assert.Equal(new[] { Warning(command, 3) }, Summaries(after.Diagnostics));
        Assert.Equal(new[] { Warning(command, 3) }, Summaries(program.Diagnostics));
    }

    [Fact]
    public void SkippedIfMatchesItsNestedIfWithItsOwnTerminator()
    {
        EventBlockNode after = OnlyEvent(Parse(InAfterBlock(
            "IF A <> \"1\" THEN",
            "IF B = \"2\" THEN",
            "ASSIGN Y = \"a\"",
            "END",
            "ASSIGN Y = \"b\"",
            "END-IF",
            "ASSIGN Z = \"kept\"")));

        AssertAssign(Assert.Single(after.Statements), "Z", "kept");
        Assert.Equal(new[] { Warning(Parser.Commands.NotEqual, 3) }, Summaries(after.Diagnostics));
    }

    [Fact]
    public void UnrecognisedTokensDoNotStopTheBlocksThatFollow()
    {
        const string text = """
            Field X
            	After
            		) ( , = ,
            		ASSIGN Z = "x"
            	End-After
            End-Field
            Field Y
            	After
            		ASSIGN W = "y"
            	End-After
            End-Field
            """;

        ProgramNode? program = null;
        Exception? error = Record.Exception(() => program = Parse(text));

        Assert.Null(error);
        Assert.NotNull(program);
        Assert.Contains(program.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning);

        // What survives inside X is the recovery's choice; Y must parse untouched.
        FieldBlockNode y = Assert.Single(program.FieldBlocks, block => block.FieldName == "Y");
        EventBlockNode after = Assert.Single(y.Events);
        Assert.Equal(FieldEventKind.FieldAfter, after.Event);
        AssertAssign(Assert.Single(after.Statements), "W", "y");
    }

    [Fact]
    public void FieldBlockHoldsItsEventBlocksInSourceOrderAndAllowsAnEmptyOne()
    {
        const string text = """
            Field F
            	Before
            		ASSIGN A = "b"
            	End-Before
            	After
            	End-After
            	Click
            		ASSIGN A = "c"
            	End-Click
            End-Field
            """;

        ProgramNode program = Parse(text);

        FieldBlockNode block = Assert.Single(program.FieldBlocks);
        Assert.Equal("F", block.FieldName);
        Assert.Equal(1, block.Line);
        Assert.Equal(
            new[] { FieldEventKind.FieldBefore, FieldEventKind.FieldAfter, FieldEventKind.FieldClick },
            block.Events.Select(eventBlock => eventBlock.Event));
        AssertAssign(Assert.Single(block.Events[0].Statements), "A", "b");
        Assert.Empty(block.Events[1].Statements);
        AssertAssign(Assert.Single(block.Events[2].Statements), "A", "c");
        Assert.All(block.Events, eventBlock => Assert.Empty(eventBlock.Diagnostics));
        Assert.Empty(program.Diagnostics);
    }

    // Lines 434-440: an IF closes with END-IF or END, and both give the same node.
    [Theory]
    [InlineData(Parser.Commands.EndIf)]
    [InlineData(Parser.Commands.End)]
    public void IfElseParsesWithEitherTerminator(string terminator)
    {
        ProgramNode program = Parse(InAfterBlock(
            "IF A = \"1\" THEN",
            "ASSIGN Y = \"a\"",
            "ELSE",
            "ASSIGN Y = \"b\"",
            terminator));

        IfStatementNode ifStatement = Assert.IsType<IfStatementNode>(Assert.Single(OnlyEvent(program).Statements));
        AssertAssign(Assert.Single(ifStatement.Then), "Y", "a");
        Assert.NotNull(ifStatement.Else);
        AssertAssign(Assert.Single(ifStatement.Else), "Y", "b");
        Assert.Empty(program.Diagnostics);
    }

    [Fact]
    public void NotBindsOneComparisonAndAndBindsNotTerms()
    {
        ProgramNode program = Parse(InAfterBlock(
            "IF NOT FeverTemp = (.) AND Fever = (-) THEN",
            "ASSIGN Y = \"1\"",
            "END-IF"));

        IfStatementNode ifStatement = Assert.IsType<IfStatementNode>(Assert.Single(OnlyEvent(program).Statements));
        AndNode and = Assert.IsType<AndNode>(ifStatement.Condition);

        NotNode not = Assert.IsType<NotNode>(and.Left);
        EqualsNode negated = Assert.IsType<EqualsNode>(not.Operand);
        AssertIdentifier(negated.Left, "FeverTemp");
        Assert.Equal(LiteralKind.Missing, Assert.IsType<LiteralNode>(negated.Right).Kind);

        EqualsNode right = Assert.IsType<EqualsNode>(and.Right);
        AssertIdentifier(right.Left, "Fever");
        Assert.Equal(LiteralKind.Unchecked, Assert.IsType<LiteralNode>(right.Right).Kind);
        Assert.Empty(program.Diagnostics);
    }

    [Fact]
    public void AndChainNestsToTheRight()
    {
        ProgramNode program = Parse(InAfterBlock(
            "IF A = \"1\" AND B = \"2\" AND C = \"3\" THEN",
            "ASSIGN Y = \"1\"",
            "END-IF"));

        IfStatementNode ifStatement = Assert.IsType<IfStatementNode>(Assert.Single(OnlyEvent(program).Statements));
        AndNode outer = Assert.IsType<AndNode>(ifStatement.Condition);
        AssertIdentifier(Assert.IsType<EqualsNode>(outer.Left).Left, "A");

        AndNode inner = Assert.IsType<AndNode>(outer.Right);
        AssertIdentifier(Assert.IsType<EqualsNode>(inner.Left).Left, "B");
        AssertIdentifier(Assert.IsType<EqualsNode>(inner.Right).Left, "C");
        Assert.Empty(program.Diagnostics);
    }

    // Lines 168-171: names are separated by whitespace, and a list ends at the next line's command keyword.
    [Fact]
    public void NameListsEndAtTheNextCommand()
    {
        ProgramNode program = Parse(InAfterBlock("ENABLE A B", "DISABLE A B", "CLEAR A B"));

        Assert.Collection(
            OnlyEvent(program).Statements,
            statement => Assert.Equal(new[] { "A", "B" }, Assert.IsType<EnableStatementNode>(statement).Names),
            statement => Assert.Equal(new[] { "A", "B" }, Assert.IsType<DisableStatementNode>(statement).Names),
            statement => Assert.Equal(new[] { "A", "B" }, Assert.IsType<ClearStatementNode>(statement).Names));
        Assert.Empty(program.Diagnostics);
    }

    [Theory]
    [InlineData("YEARS")]
    [InlineData("years")]
    [InlineData("Years")]
    public void YearsCallKeepsItsArgumentsAndAnUpperCaseName(string spelling)
    {
        ProgramNode program = Parse(InAfterBlock($"ASSIGN AGE = {spelling}(DOB, DateofInterview)"));

        AssignStatementNode assign = Assert.IsType<AssignStatementNode>(Assert.Single(OnlyEvent(program).Statements));
        Assert.Equal("AGE", assign.Target, ignoreCase: true);
        FunctionCallNode call = Assert.IsType<FunctionCallNode>(assign.Value);
        Assert.Equal("YEARS", call.Name);
        Assert.Collection(
            call.Arguments,
            argument => AssertIdentifier(argument, "DOB"),
            argument => AssertIdentifier(argument, "DateofInterview"));
        Assert.Empty(program.Diagnostics);
    }

    [Fact]
    public void EpiWeekCallTakesOneArgument()
    {
        ProgramNode program = Parse(InAfterBlock("ASSIGN OnsetWeek = EPIWEEK(OnsetDate)"));

        AssignStatementNode assign = Assert.IsType<AssignStatementNode>(Assert.Single(OnlyEvent(program).Statements));
        Assert.Equal("OnsetWeek", assign.Target);
        FunctionCallNode call = Assert.IsType<FunctionCallNode>(assign.Value);
        Assert.Equal("EPIWEEK", call.Name);
        AssertIdentifier(Assert.Single(call.Arguments), "OnsetDate");
        Assert.Empty(program.Diagnostics);
    }

    // Lines 364 and 383-385.
    [Theory]
    [InlineData("DIALOG \"m\"", null)]
    [InlineData("DIALOG \"m\" TITLETEXT = \"T\"", "T")]
    public void DialogKeepsItsMessageAndOptionalTitle(string statement, string? title)
    {
        ProgramNode program = Parse(InAfterBlock(statement));

        DialogStatementNode dialog = Assert.IsType<DialogStatementNode>(Assert.Single(OnlyEvent(program).Statements));
        Assert.Equal("m", dialog.Message);
        Assert.Equal(title, dialog.Title);
        Assert.Empty(program.Diagnostics);
    }

    [Fact]
    public void GeocodeIsKeptWithOneInfoDiagnostic()
    {
        ProgramNode program = Parse(InAfterBlock("GEOCODE PatientAddress, Latitude, Longitude"));

        EventBlockNode after = OnlyEvent(program);
        GeocodeStatementNode geocode = Assert.IsType<GeocodeStatementNode>(Assert.Single(after.Statements));
        Assert.Equal(new[] { "PatientAddress", "Latitude", "Longitude" }, geocode.Arguments);

        var expected = new[] { (DiagnosticSeverity.Info, Parser.Commands.Geocode, 3) };
        Assert.Equal(expected, Summaries(after.Diagnostics));
        Assert.Equal(expected, Summaries(program.Diagnostics));
    }

    // Line 42 gives the boolean literals; (+) is outside the subset and pinned among the recovery cases.
    [Theory]
    [InlineData("101.2", LiteralKind.Number, null, 101.2)]
    [InlineData("\"x\"", LiteralKind.String, "x", null)]
    [InlineData("(.)", LiteralKind.Missing, null, null)]
    [InlineData("(-)", LiteralKind.Unchecked, null, null)]
    public void LiteralParsesToItsKind(string literal, LiteralKind kind, string? text, double? number)
    {
        ProgramNode program = Parse(InAfterBlock($"ASSIGN Y = {literal}"));

        AssignStatementNode assign = Assert.IsType<AssignStatementNode>(Assert.Single(OnlyEvent(program).Statements));
        LiteralNode value = Assert.IsType<LiteralNode>(assign.Value);
        Assert.Equal(kind, value.Kind);
        Assert.Equal(text, value.Text);
        Assert.Equal(number, value.Number);
        Assert.Empty(program.Diagnostics);
    }

    // Keywords are case-insensitive (line 4), and comments produce nothing (lines 49-51), so these programs share one
    // shape.
    [Theory]
    [InlineData(UpperCaseProgram)]
    [InlineData(MixedCaseProgram)]
    [InlineData(CommentedProgram)]
    public void KeywordCaseAndCommentsDoNotChangeTheTree(string text)
    {
        ProgramNode program = Parse(text);

        EventBlockNode after = OnlyEvent(program);
        Assert.Equal(FieldEventKind.FieldAfter, after.Event);
        IfStatementNode ifStatement = Assert.IsType<IfStatementNode>(Assert.Single(after.Statements));
        AssertAssign(Assert.Single(ifStatement.Then), "Y", "1");
        Assert.Null(ifStatement.Else);
        Assert.Empty(program.Diagnostics);
    }

    [Fact]
    public void LinesCountTheLinesInsideComments()
    {
        FieldBlockNode block = Assert.Single(Parse(CommentedProgram).FieldBlocks);
        Assert.Equal(4, block.Line);

        EventBlockNode after = Assert.Single(block.Events);
        IfStatementNode ifStatement = Assert.IsType<IfStatementNode>(Assert.Single(after.Statements));
        Assert.Equal(7, ifStatement.Line);
        Assert.Equal(10, Assert.IsType<AssignStatementNode>(Assert.Single(ifStatement.Then)).Line);
    }

    [Fact]
    public void DiagnosticsNameTheFormTheProgramBelongsTo()
    {
        string text = InAfterBlock("GOTO Z");

        Assert.Equal("MyForm", Assert.Single(Parse(text, "MyForm").Diagnostics).Form);

        FormMetadata form = TextForm("X", "Z") with { Name = "FoodHistory" };
        Assert.Equal("FoodHistory", Assert.Single(Parser.Parse(Lexer.Tokenize(text), form).Diagnostics).Form);
    }

    [Fact]
    public void SeededCaseReportProgramParsesWithoutDiagnostics()
    {
        ProgramNode program = Parse(CaseReportProgram, "CaseReport");

        Assert.Equal(new[] { "Sex", "LastName" }, program.FieldBlocks.Select(block => block.FieldName));
        Assert.Equal(new[] { 6, 18 }, program.FieldBlocks.Select(block => block.Line));
        Assert.Equal(FieldEventKind.FieldAfter, Assert.Single(program.FieldBlocks[0].Events).Event);
        Assert.Equal(FieldEventKind.FieldBefore, Assert.Single(program.FieldBlocks[1].Events).Event);

        Assert.All(program.FieldBlocks, block =>
        {
            IfStatementNode ifStatement =
                Assert.IsType<IfStatementNode>(Assert.Single(Assert.Single(block.Events).Statements));
            EnableStatementNode enable = Assert.IsType<EnableStatementNode>(Assert.Single(ifStatement.Then));
            Assert.Equal(new[] { "Pregnant" }, enable.Names);
            Assert.NotNull(ifStatement.Else);
            Assert.Collection(
                ifStatement.Else,
                statement => Assert.Equal(new[] { "Pregnant" }, Assert.IsType<DisableStatementNode>(statement).Names),
                statement => Assert.Equal(new[] { "Pregnant" }, Assert.IsType<ClearStatementNode>(statement).Names));
        });
        Assert.Empty(program.Diagnostics);
    }

    [Fact]
    public void SeededFoodHistoryProgramReportsOnlyItsGeocode()
    {
        ProgramNode program = Parse(FoodHistoryProgram, "FoodHistory");

        Assert.Equal(
            new[]
            {
                "DOB", "GetCoordinates", "OnsetDate", "Asian", "Black", "Multiracial",
                "NativeHawaiianOtherPacificIslander", "UnknownOther", "AmericanIndianAlaskanNative", "White",
                "FeverTemp",
            },
            program.FieldBlocks.Select(block => block.FieldName));

        // Field DOB is line 1, Field GetCoordinates line 10, its Click line 11 and the GEOCODE line 12.
        Diagnostic geocode = Assert.Single(program.Diagnostics);
        Assert.Equal(
            (DiagnosticSeverity.Info, Parser.Commands.Geocode, 12, "FoodHistory"),
            (geocode.Severity, geocode.Command, geocode.Line, geocode.Form));

        FieldBlockNode asian = Assert.Single(program.FieldBlocks, block => block.FieldName == "Asian");
        EventBlockNode click = Assert.Single(asian.Events);
        Assert.Equal(FieldEventKind.FieldClick, click.Event);
        Assert.Equal(
            new[]
            {
                "Black", "AmericanIndianAlaskanNative", "NativeHawaiianOtherPacificIslander", "White", "Multiracial",
                "UnknownOther",
            },
            click.Statements.Select(statement => Assert.IsType<AssignStatementNode>(statement).Target));
        Assert.All(
            click.Statements,
            statement => Assert.Equal(
                LiteralKind.Unchecked,
                Assert.IsType<LiteralNode>(((AssignStatementNode)statement).Value).Kind));
    }

    [Fact]
    public void EmptyInputParsesToAnEmptyProgram()
    {
        ProgramNode fromEndToken = Parser.Parse(new[] { new Token(TokenKind.EndOfInput, string.Empty, 1, 1) }, "F");
        ProgramNode fromEmptyText = Parse(string.Empty);

        Assert.Empty(fromEndToken.FieldBlocks);
        Assert.Empty(fromEndToken.Diagnostics);
        Assert.Empty(fromEmptyText.FieldBlocks);
        Assert.Empty(fromEmptyText.Diagnostics);
    }

    // Cutting the nested program after each token leaves constructs open at every depth, so the sweep reaches each
    // path that handles a missing terminator.
    [Fact]
    public void EveryTruncationOfTheNestedProgramParsesWithoutThrowing()
    {
        IReadOnlyList<Token> tokens = Lexer.Tokenize(NestedRecoveryProgram);

        for (int length = 0; length < tokens.Count; length++)
        {
            Token end = length == 0
                ? new Token(TokenKind.EndOfInput, string.Empty, 1, 1)
                : new Token(TokenKind.EndOfInput, string.Empty, tokens[length - 1].Line, tokens[length - 1].Column);
            Token[] truncated = [.. tokens.Take(length), end];

            ProgramNode? program = null;
            Exception? error = Record.Exception(() => program = Parser.Parse(truncated, "F"));

            Assert.Null(error);
            Assert.NotNull(program);
        }
    }

    // metaViews.CheckCode of CaseReport, copied from testdata/sql/03_forms.sql lines 26-53 with every line kept: the
    // comment fills program lines 1-5, line 4 holds only a tab, Field Sex is line 6 and Field LastName line 18.
    private const string CaseReportProgram = """
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
        """;

    // metaViews.CheckCode of FoodHistory, copied from testdata/sql/03_forms.sql lines 55-161 with every line kept and
    // the SQL '' escape of the DIALOG text turned back into '.
    private const string FoodHistoryProgram = """
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
        """;

    private static ProgramNode Parse(string text, string formName = "TestForm") =>
        Parser.Parse(Lexer.Tokenize(text), formName);

    /// <summary>Asserts a single field block holding a single event block, and returns that event block.</summary>
    private static EventBlockNode OnlyEvent(ProgramNode program) =>
        Assert.Single(Assert.Single(program.FieldBlocks).Events);

    /// <summary>
    /// Wraps the lines in <c>Field X</c> / <c>After</c> … <c>End-After</c> / <c>End-Field</c>, one statement line per
    /// entry, so entry <c>i</c> (0-based) is program line <c>3 + i</c>.
    /// </summary>
    private static string InAfterBlock(params string[] statementLines) =>
        "Field X\n\tAfter\n"
        + string.Concat(statementLines.Select(line => "\t\t" + line + "\n"))
        + "\tEnd-After\nEnd-Field";

    /// <summary>
    /// A one-page form whose fields are all Text (seeded <c>metaFieldTypes</c> row 1) on <c>nvarchar(4000)</c> columns.
    /// </summary>
    private static FormMetadata TextForm(params string[] names)
    {
        PageMetadata[] pages = [new PageMetadata(1, "Page 1", 0, "TestForm1")];
        FieldMetadata[] fields =
        [
            .. names.Select((name, i) => new FieldMetadata
            {
                FieldId = i + 1,
                Name = name,
                FieldTypeId = 1,
                FieldTypeName = "Text",
                PageId = 1,
                TabIndex = i + 1,
                Column = FieldTypeMapper.DescribeColumn(name, "nvarchar", 8000),
            }),
        ];

        return new FormMetadata(1, "TestForm", "TestForm", pages, fields);
    }

    private static Dictionary<string, FieldValue> TextValues(params (string Name, string Text)[] entries) =>
        entries.ToDictionary(
            entry => entry.Name,
            entry => FieldValue.FromText(entry.Text),
            StringComparer.OrdinalIgnoreCase);

    private static (DiagnosticSeverity Severity, string Command, int Line) Warning(string command, int line) =>
        (DiagnosticSeverity.Warning, command, line);

    // Severity, command and line identify a parser diagnostic; the message is display text and is not pinned.
    private static (DiagnosticSeverity Severity, string Command, int Line)[] Summaries(
        IEnumerable<Diagnostic> diagnostics) =>
        [.. diagnostics.Select(diagnostic => (diagnostic.Severity, diagnostic.Command, diagnostic.Line))];

    private static void AssertIdentifier(ExpressionNode expression, string name) =>
        Assert.Equal(name, Assert.IsType<IdentifierNode>(expression).Name);

    private static void AssertStringLiteral(ExpressionNode expression, string text)
    {
        LiteralNode literal = Assert.IsType<LiteralNode>(expression);
        Assert.Equal(LiteralKind.String, literal.Kind);
        Assert.Equal(text, literal.Text);
    }

    private static void AssertAssign(StatementNode statement, string target, string text)
    {
        AssignStatementNode assign = Assert.IsType<AssignStatementNode>(statement);
        Assert.Equal(target, assign.Target);
        AssertStringLiteral(assign.Value, text);
    }
}
