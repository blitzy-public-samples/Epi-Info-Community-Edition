using System.Collections.Frozen;
using System.Collections.Immutable;
using EpiInfo.CheckCode.Evaluation;
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
    // Parser.Commands has no constant for these spellings: an unsupported statement is reported under its own keyword,
    // an unregistered function under its name, and a missing event terminator under that terminator, all in upper
    // case.
    private const string Goto = "GOTO";
    private const string Hide = "HIDE";
    private const string Always = "ALWAYS";
    private const string Days = "DAYS";
    private const string SystemDate = "SYSTEMDATE";
    private const string EndBefore = "END-BEFORE";
    private const string EndClick = "END-CLICK";

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

    // The IF on line 3 holds an ASSIGN to the padded name "[ END ]" on line 4 and a valid ASSIGN on line 5; its END-IF
    // is line 6.
    private const string PaddedEndProgram = """
        Field X
        	After
        		IF X = "1" THEN
        			ASSIGN [ END ] = "discard"
        			ASSIGN Z = "conditional"
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

    // Page and Record are block keywords (lines 630, 632); bracketed, each is an Identifier (line 30). Each use below
    // spells the name in a different case, and each resolves to the canonical field.
    [Fact]
    public void BracketedReservedWordNamesResolveToTheirCanonicalFields()
    {
        const string program = """
            Field [Page]
            	After
            		IF [page] = "1" THEN
            			ASSIGN [record] = [PAGE]
            			ENABLE [Record]
            		END-IF
            	End-After
            End-Field
            """;
        FormMetadata form = TextForm("Page", "Record");
        ICheckCodeEngine engine = new CheckCodeEngine();

        Assert.All(new[] { "Page", "Record" }, name =>
        {
            Token bare = Lexer.Tokenize(name)[0];
            Token bracketed = Lexer.Tokenize("[" + name + "]")[0];
            Assert.Equal((TokenKind.Keyword, name.ToUpperInvariant()), (bare.Kind, bare.Keyword));
            Assert.Equal((TokenKind.Identifier, name), (bracketed.Kind, bracketed.Text));
        });

        FieldEventResponse response = engine.Run(
            program,
            form,
            FieldEventKind.FieldAfter,
            "Page",
            TextValues(("Page", "1")));

        Assert.Equal(new[] { "Record" }, response.Enabled);
        KeyValuePair<string, FieldValue> assigned = Assert.Single(response.Assigned);
        Assert.Equal(("Record", "1"), (assigned.Key, assigned.Value.AsText()));
        Assert.Empty(response.Diagnostics);
        Assert.Empty(engine.Analyze(program, form));
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

    // Each case's skipped construct starts on line 3; the ASSIGN after it must survive the recovery, and running the
    // block has no other effect, even with A = "1" and B = "2", under which the XOR case's comparisons both hold.
    [Theory]
    [InlineData(Goto, "GOTO Z")]
    [InlineData(Hide, "HIDE A", "B")]
    [InlineData(Days, "ASSIGN Y = DAYS(A, B)")]
    [InlineData(Parser.Commands.Plus, "ASSIGN Y = 1 + 2")]
    [InlineData(Parser.Commands.Concatenate, "ASSIGN Y = A & B")]
    [InlineData(Parser.Commands.EnableExcept, "ENABLE * EXCEPT A")]
    [InlineData(Parser.Commands.CheckedLiteral, "IF A = (+) THEN", "ASSIGN Y = \"1\"", "END-IF")]
    [InlineData(Parser.Commands.NotEqual, "IF A <> \"1\" THEN", "ASSIGN Y = \"1\"", "END-IF")]
    [InlineData(Parser.Commands.DisableExcept, "DISABLE * EXCEPT A")]
    [InlineData(Parser.Commands.Clear, "CLEAR *")]
    [InlineData(Parser.Commands.Clear, "CLEAR A, B")]
    [InlineData(Parser.Commands.Disable, "DISABLE A + B")]
    [InlineData(Parser.Commands.Dialog, "DIALOG \"m\" B YN")]
    [InlineData(Parser.Commands.Dialog, "DIALOG B")]
    [InlineData(Parser.Commands.Xor, "IF A = \"1\" XOR B = \"2\" THEN", "ASSIGN Y = \"1\"", "END-IF")]
    [InlineData(Parser.Commands.Less, "IF A < 1 THEN", "ASSIGN Y = \"1\"", "END-IF")]
    [InlineData(Parser.Commands.Greater, "IF A > 1 THEN", "ASSIGN Y = \"1\"", "END-IF")]
    [InlineData(Parser.Commands.Like, "IF A LIKE \"x\" THEN", "ASSIGN Y = \"1\"", "END-IF")]
    [InlineData(Parser.Commands.DateLiteral, "ASSIGN Y = 01/15/2011")]
    [InlineData(Parser.Commands.Or, "ASSIGN Y = A OR B")]
    public void UnsupportedConstructIsDroppedWithOneWarningAndTheNextStatementIsKept(
        string command,
        params string[] skippedLines)
    {
        string text = InAfterBlock([.. skippedLines, "ASSIGN Z = \"x\""]);
        ProgramNode program = Parse(text);

        EventBlockNode after = OnlyEvent(program);
        AssertAssign(Assert.Single(after.Statements), "Z", "x");

        // One warning only: a name list continued on the next line, such as HIDE's B, is skipped with its statement.
        Assert.Equal(new[] { Warning(command, 3) }, Summaries(after.Diagnostics));
        Assert.Equal(new[] { Warning(command, 3) }, Summaries(program.Diagnostics));

        ICheckCodeEngine engine = new CheckCodeEngine();
        FieldEventResponse response = engine.Run(
            text,
            TextForm("X", "A", "B", "Y", "Z"),
            FieldEventKind.FieldAfter,
            "X",
            TextValues(("A", "1"), ("B", "2")));

        AssertOnlyAssigned(response, ("Z", "x"));
        Assert.Equal(new[] { Warning(command, 3) }, Summaries(response.Diagnostics));
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

    // Line 3 holds tokens that start no statement; the ASSIGN on line 4 is where the parse resynchronises.
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

        // The junk is one warning and the ASSIGN after it survives; Y must parse untouched.
        var expected = new[] { Warning(Parser.Commands.Syntax, 3) };
        Assert.Equal(expected, Summaries(program.Diagnostics));
        FieldBlockNode x = Assert.Single(program.FieldBlocks, block => block.FieldName == "X");
        EventBlockNode xAfter = Assert.Single(x.Events);
        AssertAssign(Assert.Single(xAfter.Statements), "Z", "x");
        Assert.Equal(expected, Summaries(xAfter.Diagnostics));

        FieldBlockNode y = Assert.Single(program.FieldBlocks, block => block.FieldName == "Y");
        EventBlockNode after = Assert.Single(y.Events);
        Assert.Equal(FieldEventKind.FieldAfter, after.Event);
        AssertAssign(Assert.Single(after.Statements), "W", "y");
        Assert.Empty(after.Diagnostics);

        ICheckCodeEngine engine = new CheckCodeEngine();
        FormMetadata form = TextForm("X", "Y", "Z", "W");

        FieldEventResponse xResponse = engine.Run(text, form, FieldEventKind.FieldAfter, "X", TextValues());
        AssertOnlyAssigned(xResponse, ("Z", "x"));
        Assert.Equal(expected, Summaries(xResponse.Diagnostics));

        FieldEventResponse yResponse = engine.Run(text, form, FieldEventKind.FieldAfter, "Y", TextValues());
        AssertOnlyAssigned(yResponse, ("W", "y"));
        Assert.Empty(yResponse.Diagnostics);
    }

    // The junk on lines 7-8 holds no command and no name, so nothing resynchronises before End-After: the rest of
    // the After block is skipped with one warning. The statements before it end with an IF closed by END-IF on
    // line 6, so the junk never trails an ASSIGN expression.
    [Fact]
    public void TokensThatCannotBeResynchronisedSkipTheRestOfTheirEventBlockOnly()
    {
        const string text = """
            Field X
            	After
            		ASSIGN Z = "before"
            		IF A = "1" THEN
            			ASSIGN Y = "if"
            		END-IF
            		) ( , = , "s" 5
            		, 7
            	End-After
            	Click
            		ASSIGN Z = "click"
            	End-Click
            End-Field
            Field Y
            	After
            		ASSIGN W = "y"
            	End-After
            End-Field
            """;

        ProgramNode program = Parse(text);

        // One warning, and no missing END-AFTER: the skip stops before the terminator, which closes the block.
        var expected = new[] { Warning(Parser.Commands.Syntax, 7) };
        Assert.Equal(expected, Summaries(program.Diagnostics));

        Assert.Equal(new[] { "X", "Y" }, program.FieldBlocks.Select(block => block.FieldName));
        FieldBlockNode x = program.FieldBlocks[0];
        Assert.Equal(
            new[] { FieldEventKind.FieldAfter, FieldEventKind.FieldClick },
            x.Events.Select(eventBlock => eventBlock.Event));
        Assert.Collection(
            x.Events[0].Statements,
            statement => AssertAssign(statement, "Z", "before"),
            statement =>
            {
                IfStatementNode ifStatement = Assert.IsType<IfStatementNode>(statement);
                AssertAssign(Assert.Single(ifStatement.Then), "Y", "if");
                Assert.Null(ifStatement.Else);
            });
        Assert.Equal(expected, Summaries(x.Events[0].Diagnostics));
        AssertAssign(Assert.Single(x.Events[1].Statements), "Z", "click");
        Assert.Empty(x.Events[1].Diagnostics);

        EventBlockNode yAfter = Assert.Single(program.FieldBlocks[1].Events);
        AssertAssign(Assert.Single(yAfter.Statements), "W", "y");
        Assert.Empty(yAfter.Diagnostics);

        ICheckCodeEngine engine = new CheckCodeEngine();
        FormMetadata form = TextForm("X", "Y", "Z", "W", "A");
        Dictionary<string, FieldValue> values = TextValues(("A", "1"));

        FieldEventResponse xAfterResponse = engine.Run(text, form, FieldEventKind.FieldAfter, "X", values);
        AssertOnlyAssigned(xAfterResponse, ("Z", "before"), ("Y", "if"));
        Assert.Equal(expected, Summaries(xAfterResponse.Diagnostics));

        FieldEventResponse xClickResponse = engine.Run(text, form, FieldEventKind.FieldClick, "X", values);
        AssertOnlyAssigned(xClickResponse, ("Z", "click"));
        Assert.Empty(xClickResponse.Diagnostics);

        FieldEventResponse yResponse = engine.Run(text, form, FieldEventKind.FieldAfter, "Y", values);
        AssertOnlyAssigned(yResponse, ("W", "y"));
        Assert.Empty(yResponse.Diagnostics);
    }

    // Parentheses count toward Parser.MaxNestingDepth. One level past it gives one warning on line 4, where the
    // parentheses open, keeps the statement before it and skips the rest of X's After block, which still closes, so
    // Field Y (line 8) runs.
    [Theory]
    [InlineData(Parser.MaxNestingDepth)]
    [InlineData(Parser.MaxNestingDepth + 1)]
    public void NestingCeilingKeepsTheStatementsBeforeItAndTheBlocksAfterIt(int depth)
    {
        string text = InAfterBlock(
                "ASSIGN Z = \"before\"",
                "ASSIGN Y = " + new string('(', depth) + "1" + new string(')', depth),
                "ASSIGN W = \"after\"")
            + "\nField Y\n\tAfter\n\t\tASSIGN V = \"y\"\n\tEnd-After\nEnd-Field";
        bool exceeded = depth > Parser.MaxNestingDepth;

        ProgramNode program = Parse(text);

        (DiagnosticSeverity Severity, string Command, int Line)[] expected =
            exceeded ? [Warning(Parser.Commands.Nesting, 4)] : [];
        Assert.Equal(expected, Summaries(program.Diagnostics));
        Assert.Equal(new[] { "X", "Y" }, program.FieldBlocks.Select(block => block.FieldName));
        Assert.Equal(8, program.FieldBlocks[1].Line);

        EventBlockNode xAfter = Assert.Single(program.FieldBlocks[0].Events);
        Assert.Equal(expected, Summaries(xAfter.Diagnostics));
        if (exceeded)
        {
            AssertAssign(Assert.Single(xAfter.Statements), "Z", "before");
        }
        else
        {
            Assert.Collection(
                xAfter.Statements,
                statement => AssertAssign(statement, "Z", "before"),
                statement =>
                {
                    // The tree has no grouping node, so every parenthesis unwraps to the literal.
                    AssignStatementNode deep = Assert.IsType<AssignStatementNode>(statement);
                    Assert.Equal("Y", deep.Target);
                    LiteralNode value = Assert.IsType<LiteralNode>(deep.Value);
                    Assert.Equal(LiteralKind.Number, value.Kind);
                    Assert.Equal(1.0, value.Number);
                },
                statement => AssertAssign(statement, "W", "after"));
        }

        EventBlockNode yAfter = Assert.Single(program.FieldBlocks[1].Events);
        AssertAssign(Assert.Single(yAfter.Statements), "V", "y");
        Assert.Empty(yAfter.Diagnostics);

        ICheckCodeEngine engine = new CheckCodeEngine();
        FormMetadata form = TextForm("X", "Y", "Z", "W", "V");

        FieldEventResponse xResponse = engine.Run(text, form, FieldEventKind.FieldAfter, "X", TextValues());
        if (exceeded)
        {
            AssertOnlyAssigned(xResponse, ("Z", "before"));
        }
        else
        {
            // The number 1 is assigned to a Text field, so it arrives as its invariant text.
            AssertOnlyAssigned(xResponse, ("Z", "before"), ("Y", "1"), ("W", "after"));
        }

        Assert.Equal(expected, Summaries(xResponse.Diagnostics));

        FieldEventResponse yResponse = engine.Run(text, form, FieldEventKind.FieldAfter, "Y", TextValues());
        AssertOnlyAssigned(yResponse, ("V", "y"));
        Assert.Empty(yResponse.Diagnostics);
    }

    // Line 275: ALWAYS … END is one unsupported statement. Skipped inside the IF's THEN branch (lines 4-6), its END
    // closes only the ALWAYS, so the IF still ends at its own END-IF on line 8 and keeps the ASSIGN on line 7.
    [Fact]
    public void AlwaysBlockInsideAnIfBranchIsSkippedWithoutClosingTheIf()
    {
        string text = InAfterBlock(
            "IF A = \"1\" THEN",
            "ALWAYS",
            "GOTO B",
            "END",
            "ASSIGN Y = \"in\"",
            "END-IF",
            "ASSIGN Z = \"x\"");

        ProgramNode program = Parse(text);

        EventBlockNode after = OnlyEvent(program);
        Assert.Collection(
            after.Statements,
            statement =>
            {
                IfStatementNode ifStatement = Assert.IsType<IfStatementNode>(statement);
                AssertAssign(Assert.Single(ifStatement.Then), "Y", "in");
                Assert.Null(ifStatement.Else);
            },
            statement => AssertAssign(statement, "Z", "x"));

        // The GOTO is skipped with the ALWAYS, so it adds no warning of its own.
        var expected = new[] { Warning(Always, 4) };
        Assert.Equal(expected, Summaries(after.Diagnostics));
        Assert.Equal(expected, Summaries(program.Diagnostics));

        ICheckCodeEngine engine = new CheckCodeEngine();
        FieldEventResponse response = engine.Run(
            text,
            TextForm("X", "A", "B", "Y", "Z"),
            FieldEventKind.FieldAfter,
            "X",
            TextValues(("A", "1")));

        AssertOnlyAssigned(response, ("Y", "in"), ("Z", "x"));
        Assert.Equal(expected, Summaries(response.Diagnostics));
    }

    // An ALWAYS at event level (line 3) counts the IF nested in it against that IF's END-IF (line 6), so its own END
    // on line 7 ends the skip and End-After still closes the block.
    [Fact]
    public void IfInsideASkippedAlwaysBlockIsSkippedWithIt()
    {
        string text = InAfterBlock(
            "ALWAYS",
            "IF A = \"1\" THEN",
            "HIDE B",
            "END-IF",
            "END",
            "ASSIGN Z = \"x\"");

        ProgramNode program = Parse(text);

        // One warning: no HIDE, no stray END and no missing END-AFTER.
        EventBlockNode after = OnlyEvent(program);
        AssertAssign(Assert.Single(after.Statements), "Z", "x");
        var expected = new[] { Warning(Always, 3) };
        Assert.Equal(expected, Summaries(after.Diagnostics));
        Assert.Equal(expected, Summaries(program.Diagnostics));

        ICheckCodeEngine engine = new CheckCodeEngine();
        FieldEventResponse response = engine.Run(
            text,
            TextForm("X", "A", "B", "Z"),
            FieldEventKind.FieldAfter,
            "X",
            TextValues(("A", "1")));

        AssertOnlyAssigned(response, ("Z", "x"));
        Assert.Equal(expected, Summaries(response.Diagnostics));
    }

    // The skip of an unsupported statement in an IF branch (line 4) stops at the IF's END-IF (line 5), which still
    // closes the IF, now with an empty THEN branch.
    [Fact]
    public void UnsupportedStatementInAnIfBranchStopsAtTheIfTerminator()
    {
        string text = InAfterBlock(
            "IF A = \"1\" THEN",
            "HIDE B",
            "END-IF",
            "ASSIGN Z = \"x\"");

        ProgramNode program = Parse(text);

        EventBlockNode after = OnlyEvent(program);
        Assert.Collection(
            after.Statements,
            statement =>
            {
                IfStatementNode ifStatement = Assert.IsType<IfStatementNode>(statement);
                EqualsNode condition = Assert.IsType<EqualsNode>(ifStatement.Condition);
                AssertIdentifier(condition.Left, "A");
                AssertStringLiteral(condition.Right, "1");
                Assert.Empty(ifStatement.Then);
                Assert.Null(ifStatement.Else);
            },
            statement => AssertAssign(statement, "Z", "x"));

        var expected = new[] { Warning(Hide, 4) };
        Assert.Equal(expected, Summaries(after.Diagnostics));
        Assert.Equal(expected, Summaries(program.Diagnostics));

        ICheckCodeEngine engine = new CheckCodeEngine();
        FieldEventResponse response = engine.Run(
            text,
            TextForm("X", "A", "B", "Z"),
            FieldEventKind.FieldAfter,
            "X",
            TextValues(("A", "1")));

        AssertOnlyAssigned(response, ("Z", "x"));
        Assert.Equal(expected, Summaries(response.Diagnostics));
    }

    /// <summary>
    /// Asserts that a field event assigned exactly <paramref name="expected"/>, as text and in that order, and had no
    /// other effect: nothing enabled, disabled or cleared and no dialog.
    /// </summary>
    private static void AssertOnlyAssigned(FieldEventResponse response, params (string Name, string Text)[] expected)
    {
        Assert.Empty(response.Enabled);
        Assert.Empty(response.Disabled);
        Assert.Empty(response.Cleared);
        Assert.Empty(response.Dialogs);
        Assert.Equal(expected.Select(entry => entry.Name), response.Assigned.Keys);
        Assert.All(expected, entry => Assert.Equal(entry.Text, response.Assigned[entry.Name].AsText()));
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

    // Lines 620-627 allow one Before, then one After, then one Click. A section marked "!" repeats a kind or follows a
    // later kind, so it is skipped with one warning at its keyword, and its GOTO and ASSIGN yield neither a diagnostic
    // nor a node. Section i (0-based) opens on line 2 + 4i, with its GOTO on line 3 + 4i and its ASSIGN on 4 + 4i.
    [Theory]
    [InlineData("After", "After!")]
    [InlineData("After", "Before!")]
    [InlineData("Click", "After!")]
    [InlineData("Before", "After", "Before!")]
    [InlineData("Click", "Click!")]
    [InlineData("After", "Before!", "Click")]
    public void MisplacedEventBlockIsSkippedWithOneWarningAndTheOtherBlocksAreKept(params string[] sections)
    {
        string[] keywords = [.. sections.Select(section => section.TrimEnd('!'))];
        string text = "Field F\n"
            + string.Concat(keywords.Select((keyword, i) =>
                $"\t{keyword}\n\t\tGOTO Z\n\t\tASSIGN A = \"s{i}\"\n\tEnd-{keyword}\n"))
            + "End-Field\nField G\n\tBefore\n\t\tASSIGN B = \"next\"\n\tEnd-Before\nEnd-Field";

        ProgramNode program = Parse(text);

        Assert.Equal(new[] { "F", "G" }, program.FieldBlocks.Select(block => block.FieldName));
        int[] kept = [.. Enumerable.Range(0, sections.Length).Where(i => !sections[i].EndsWith('!'))];
        IReadOnlyList<EventBlockNode> events = program.FieldBlocks[0].Events;
        Assert.Equal(kept.Length, events.Count);
        foreach ((int index, EventBlockNode eventBlock) in kept.Zip(events))
        {
            Assert.Equal(Enum.Parse<FieldEventKind>("Field" + keywords[index]), eventBlock.Event);
            Assert.Equal(2 + (4 * index), eventBlock.Line);
            AssertAssign(Assert.Single(eventBlock.Statements), "A", $"s{index}");

            // A kept block reports only its own GOTO: no skip warning lands inside it.
            Assert.Equal(new[] { Warning(Goto, 3 + (4 * index)) }, Summaries(eventBlock.Diagnostics));
        }

        Assert.Equal(
            sections.Select((section, i) => section.EndsWith('!')
                ? Warning(keywords[i].ToUpperInvariant(), 2 + (4 * i))
                : Warning(Goto, 3 + (4 * i))),
            Summaries(program.Diagnostics));

        // The block after the skipped section's field block parses untouched.
        EventBlockNode next = Assert.Single(program.FieldBlocks[1].Events);
        Assert.Equal(FieldEventKind.FieldBefore, next.Event);
        AssertAssign(Assert.Single(next.Statements), "B", "next");
        Assert.Empty(next.Diagnostics);
    }

    // A skipped section ends where a parsed one would: before the next event start (line 7), through any event
    // terminator (End-After closes the Before of line 10), and before End-Field (line 15), which still closes F.
    [Fact]
    public void SkippedEventBlockEndsWhereAParsedEventBlockWould()
    {
        const string text = """
            Field F
            	After
            		ASSIGN A = "kept"
            	End-After
            	After
            		GOTO Z
            	Click
            		ASSIGN A = "click"
            	End-Click
            	Before
            		HIDE Y
            	End-After
            	Click
            		ASSIGN A = "dropped"
            End-Field
            Field G
            	Before
            		ASSIGN B = "next"
            	End-Before
            End-Field
            """;

        ProgramNode program = Parse(text);

        Assert.Equal(new[] { "F", "G" }, program.FieldBlocks.Select(block => block.FieldName));
        Assert.Collection(
            program.FieldBlocks[0].Events,
            after =>
            {
                Assert.Equal(FieldEventKind.FieldAfter, after.Event);
                AssertAssign(Assert.Single(after.Statements), "A", "kept");
                Assert.Empty(after.Diagnostics);
            },
            click =>
            {
                Assert.Equal(FieldEventKind.FieldClick, click.Event);
                Assert.Equal(7, click.Line);
                AssertAssign(Assert.Single(click.Statements), "A", "click");
                Assert.Empty(click.Diagnostics);
            });
        AssertAssign(Assert.Single(Assert.Single(program.FieldBlocks[1].Events).Statements), "B", "next");

        // One warning per skipped section; no missing or unexpected terminator, and nothing from their contents.
        Assert.Equal(
            new[] { Warning("AFTER", 5), Warning("BEFORE", 10), Warning("CLICK", 13) },
            Summaries(program.Diagnostics));
    }

    // The After on line 4 repeats the kept one on line 2 and is skipped; its body is lines 5 to 4 + n, and the Click on
    // line 5 + n is the field's real Click. The body counts each nested IF against its END-IF or END (lines 434-440),
    // never below zero, and AUTOSEARCH's trailing ALWAYS (lines 290-293) opens nothing, so the Click and End-Click
    // inside the IF belong to the skipped section. Rows: the IF closed, then End-After; the IF unterminated before
    // End-After, which still ends the section; the IF closed by END-IF or by END with End-After missing, so the next
    // event start ends the section; a stray END-IF before the IF; and an AUTOSEARCH … ALWAYS inside the IF.
    [Theory]
    [InlineData("IF (.) THEN", "Click", "DIALOG \"inside skipped section\"", "End-Click", "END-IF", "End-After")]
    [InlineData("IF (.) THEN", "Click", "DIALOG \"inside skipped section\"", "End-Click", "End-After")]
    [InlineData("IF (.) THEN", "Click", "DIALOG \"inside skipped section\"", "End-Click", "END-IF")]
    [InlineData("IF (.) THEN", "Click", "DIALOG \"inside skipped section\"", "End-Click", "END")]
    [InlineData(
        "END-IF", "IF (.) THEN", "Click", "DIALOG \"inside skipped section\"", "End-Click", "END-IF", "End-After")]
    [InlineData(
        "IF (.) THEN", "AUTOSEARCH Y ALWAYS", "Click", "DIALOG \"inside skipped section\"", "End-Click", "END-IF")]
    public void EventKeywordsInsideANestedIfBelongToTheSkippedSection(params string[] bodyLines)
    {
        string text = "Field X\n\tAfter\n\tEnd-After\n\tAfter\n"
            + string.Concat(bodyLines.Select(line => "\t\t" + line + "\n"))
            + "\tClick\n\t\tDIALOG \"valid sibling\"\n\tEnd-Click\nEnd-Field";

        ProgramNode program = Parse(text);

        Assert.Collection(
            Assert.Single(program.FieldBlocks).Events,
            after =>
            {
                Assert.Equal(FieldEventKind.FieldAfter, after.Event);
                Assert.Equal(2, after.Line);
                Assert.Empty(after.Statements);
                Assert.Empty(after.Diagnostics);
            },
            click =>
            {
                Assert.Equal(FieldEventKind.FieldClick, click.Event);
                Assert.Equal(5 + bodyLines.Length, click.Line);
                DialogStatementNode dialog = Assert.IsType<DialogStatementNode>(Assert.Single(click.Statements));
                Assert.Equal("valid sibling", dialog.Message);
                Assert.Empty(click.Diagnostics);
            });

        // The skipped section's one warning: nothing from its contents, and no missing or unexpected terminator.
        Assert.Equal(new[] { Warning("AFTER", 4) }, Summaries(program.Diagnostics));
    }

    // The skipped After on line 4 leaves its IF unterminated and has no End-After, yet End-Field on line 9 still ends
    // it and closes F, so Field G on line 10 parses untouched.
    [Fact]
    public void UnterminatedIfInASkippedSectionNeverPassesEndField()
    {
        const string text = """
            Field F
            	After
            	End-After
            	After
            		IF (.) THEN
            			Click
            				DIALOG "inside skipped section"
            			End-Click
            End-Field
            Field G
            	Click
            		DIALOG "next field"
            	End-Click
            End-Field
            """;

        ProgramNode program = Parse(text);

        Assert.Equal(new[] { "F", "G" }, program.FieldBlocks.Select(block => block.FieldName));
        EventBlockNode after = Assert.Single(program.FieldBlocks[0].Events);
        Assert.Equal(FieldEventKind.FieldAfter, after.Event);
        Assert.Empty(after.Statements);
        EventBlockNode click = Assert.Single(program.FieldBlocks[1].Events);
        Assert.Equal(FieldEventKind.FieldClick, click.Event);
        Assert.Equal(11, click.Line);
        Assert.Equal("next field", Assert.IsType<DialogStatementNode>(Assert.Single(click.Statements)).Message);
        Assert.Equal(new[] { Warning("AFTER", 4) }, Summaries(program.Diagnostics));
    }

    // The node keeps the grammar's order itself, through its constructor and through a with expression alike.
    [Theory]
    [InlineData(FieldEventKind.FieldAfter, FieldEventKind.FieldAfter)]
    [InlineData(FieldEventKind.FieldAfter, FieldEventKind.FieldBefore)]
    [InlineData(FieldEventKind.FieldClick, FieldEventKind.FieldAfter)]
    [InlineData(FieldEventKind.FieldBefore, FieldEventKind.FieldAfter, FieldEventKind.FieldBefore)]
    [InlineData(FieldEventKind.FieldClick, FieldEventKind.FieldClick)]
    [InlineData((FieldEventKind)3)]
    public void FieldBlockNodeRejectsEventBlocksOutsideTheGrammarOrder(params FieldEventKind[] kinds)
    {
        EventBlockNode[] events = [.. kinds.Select((kind, i) => new EventBlockNode(kind, 2 + i, [], []))];
        var empty = new FieldBlockNode("F", 1, []);

        ArgumentException constructed = Assert.Throws<ArgumentException>(() => new FieldBlockNode("F", 1, events));
        Assert.Equal(nameof(FieldBlockNode.Events), constructed.ParamName);
        ArgumentException copied = Assert.Throws<ArgumentException>(() => empty with { Events = events });
        Assert.Equal(nameof(FieldBlockNode.Events), copied.ParamName);
    }

    [Theory]
    [InlineData(FieldEventKind.FieldClick)]
    [InlineData(FieldEventKind.FieldBefore, FieldEventKind.FieldClick)]
    [InlineData(FieldEventKind.FieldAfter, FieldEventKind.FieldClick)]
    [InlineData(FieldEventKind.FieldBefore, FieldEventKind.FieldAfter, FieldEventKind.FieldClick)]
    public void FieldBlockNodeAcceptsEventBlocksInTheGrammarOrder(params FieldEventKind[] kinds)
    {
        EventBlockNode[] events = [.. kinds.Select((kind, i) => new EventBlockNode(kind, 2 + i, [], []))];

        var constructed = new FieldBlockNode("F", 1, events);
        FieldBlockNode copied = new FieldBlockNode("F", 1, []) with { Events = events };

        Assert.Equal(kinds, constructed.Events.Select(eventBlock => eventBlock.Event));
        Assert.Equal(constructed, copied);
    }

    /// <summary>How a test hands a caller-owned list to a list member of a syntax record.</summary>
    public enum SnapshotPath
    {
        /// <summary>The list is a constructor argument.</summary>
        Constructor,

        /// <summary>
        /// The list is set by a <see langword="with"/> expression on a record built from an empty list.
        /// </summary>
        With,
    }

    public static TheoryData<string, SnapshotPath> ListMembers
    {
        get
        {
            var data = new TheoryData<string, SnapshotPath>();
            foreach (string member in ListMemberChecks.Keys)
            {
                data.Add(member, SnapshotPath.Constructor);
                data.Add(member, SnapshotPath.With);
            }

            return data;
        }
    }

    // Every list member of a syntax record is a snapshot taken by the constructor and by a `with` expression, so
    // changing the caller's list afterwards never reaches the record, whichever path handed the list over.
    [Theory]
    [MemberData(nameof(ListMembers))]
    public void ListMemberKeepsItsElementsWhenTheCallersListChanges(string member, SnapshotPath path) =>
        ListMemberChecks[member](path);

    private static readonly ExpressionNode SnapshotCondition =
        new EqualsNode(new IdentifierNode("A", 3), LiteralNode.FromString("1", 3), 3);

    private static readonly ImmutableArray<StatementNode> SnapshotStatements =
    [
        new AssignStatementNode("A", LiteralNode.FromString("a", 4), 4),
        new AssignStatementNode("B", LiteralNode.FromString("b", 5), 5),
    ];

    private static readonly ImmutableArray<string> SnapshotNames = ["A", "B"];

    private static readonly ImmutableArray<ExpressionNode> SnapshotArguments =
        [new IdentifierNode("DOB", 3), new IdentifierNode("DateofInterview", 3)];

    private static readonly ImmutableArray<Diagnostic> SnapshotDiagnostics =
    [
        new Diagnostic(DiagnosticSeverity.Warning, "TestForm", 3, Goto, "Unsupported command."),
        new Diagnostic(DiagnosticSeverity.Info, "TestForm", 4, Parser.Commands.Geocode, "Skipped."),
    ];

    private static readonly ImmutableArray<EventBlockNode> SnapshotEvents =
    [
        new EventBlockNode(FieldEventKind.FieldBefore, 2, [], []),
        new EventBlockNode(FieldEventKind.FieldAfter, 5, [], []),
    ];

    private static readonly ImmutableArray<FieldBlockNode> SnapshotFieldBlocks =
        [new FieldBlockNode("X", 1, []), new FieldBlockNode("Y", 8, [])];

    // One check per list member of the syntax records, keyed by record and member. Each builds the record from the
    // list it is given (once by the constructor, once by `with`) and reads the member back.
    private static readonly FrozenDictionary<string, Action<SnapshotPath>> ListMemberChecks =
        new Dictionary<string, Action<SnapshotPath>>
        {
            ["IfStatementNode.Then"] = path => AssertListMemberIsASnapshot(
                path,
                SnapshotStatements,
                list => new IfStatementNode(SnapshotCondition, list, null, 3),
                (node, list) => node with { Then = list },
                node => node.Then),
            ["IfStatementNode.Else"] = path => AssertListMemberIsASnapshot(
                path,
                SnapshotStatements,
                list => new IfStatementNode(SnapshotCondition, [], list, 3),
                (node, list) => node with { Else = list },
                node => node.Else),
            ["EnableStatementNode.Names"] = path => AssertListMemberIsASnapshot(
                path,
                SnapshotNames,
                list => new EnableStatementNode(list, 3),
                (node, list) => node with { Names = list },
                node => node.Names),
            ["DisableStatementNode.Names"] = path => AssertListMemberIsASnapshot(
                path,
                SnapshotNames,
                list => new DisableStatementNode(list, 3),
                (node, list) => node with { Names = list },
                node => node.Names),
            ["ClearStatementNode.Names"] = path => AssertListMemberIsASnapshot(
                path,
                SnapshotNames,
                list => new ClearStatementNode(list, 3),
                (node, list) => node with { Names = list },
                node => node.Names),
            ["GeocodeStatementNode.Arguments"] = path => AssertListMemberIsASnapshot(
                path,
                SnapshotNames,
                list => new GeocodeStatementNode(list, 3),
                (node, list) => node with { Arguments = list },
                node => node.Arguments),
            ["FunctionCallNode.Arguments"] = path => AssertListMemberIsASnapshot(
                path,
                SnapshotArguments,
                list => new FunctionCallNode("YEARS", list, 3),
                (node, list) => node with { Arguments = list },
                node => node.Arguments),
            ["EventBlockNode.Statements"] = path => AssertListMemberIsASnapshot(
                path,
                SnapshotStatements,
                list => new EventBlockNode(FieldEventKind.FieldAfter, 2, list, []),
                (node, list) => node with { Statements = list },
                node => node.Statements),
            ["EventBlockNode.Diagnostics"] = path => AssertListMemberIsASnapshot(
                path,
                SnapshotDiagnostics,
                list => new EventBlockNode(FieldEventKind.FieldAfter, 2, [], list),
                (node, list) => node with { Diagnostics = list },
                node => node.Diagnostics),
            ["FieldBlockNode.Events"] = path => AssertListMemberIsASnapshot(
                path,
                SnapshotEvents,
                list => new FieldBlockNode("X", 1, list),
                (node, list) => node with { Events = list },
                node => node.Events),
            ["ProgramNode.FieldBlocks"] = path => AssertListMemberIsASnapshot(
                path,
                SnapshotFieldBlocks,
                list => new ProgramNode(list, []),
                (node, list) => node with { FieldBlocks = list },
                node => node.FieldBlocks),
            ["ProgramNode.Diagnostics"] = path => AssertListMemberIsASnapshot(
                path,
                SnapshotDiagnostics,
                list => new ProgramNode([], list),
                (node, list) => node with { Diagnostics = list },
                node => node.Diagnostics),
        }.ToFrozenDictionary(StringComparer.Ordinal);

    // Hands the record a caller-owned copy of the two elements by the given path, then overwrites, appends to and
    // finally clears that list, asserting after each change that the member still holds both elements, in order.
    private static void AssertListMemberIsASnapshot<TNode, T>(
        SnapshotPath path,
        ImmutableArray<T> elements,
        Func<IReadOnlyList<T>, TNode> construct,
        Func<TNode, IReadOnlyList<T>, TNode> withMember,
        Func<TNode, IReadOnlyList<T>?> readMember)
        where T : class
    {
        List<T> callersList = [.. elements];
        TNode node = path == SnapshotPath.Constructor
            ? construct(callersList)
            : withMember(construct([]), callersList);
        AssertHoldsExactly(elements, readMember(node));

        callersList[0] = callersList[1];
        callersList.Add(elements[0]);
        AssertHoldsExactly(elements, readMember(node));

        callersList.Clear();
        AssertHoldsExactly(elements, readMember(node));
    }

    // Compares elements by reference, so the check never rests on the records' value equality.
    private static void AssertHoldsExactly<T>(ImmutableArray<T> expected, IReadOnlyList<T>? actual)
        where T : class
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.Length, actual.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Same(expected[i], actual[i]);
        }
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

    // Line 285: an implicit assignment has no keyword, yet it starts the next statement, so a name list ends before
    // its target and keeps every name in front of it; the qualified and grid targets are the Qualified ID forms of
    // lines 173-181. The list is line 3, the assignment line 4 and its one warning the only diagnostic.
    [Theory]
    [InlineData(Parser.Commands.Enable, "A", "B = 1")]
    [InlineData(Parser.Commands.Disable, "A", "B = 1")]
    [InlineData(Parser.Commands.Clear, "A B", "C = 1")]
    [InlineData(Parser.Commands.Enable, "A", "B.C = 1")]
    [InlineData(Parser.Commands.Enable, "A", "B[1, C] = 1")]
    [InlineData(Parser.Commands.Enable, "NUMERIC", "B = 1")]
    public void NameListEndsBeforeAnImplicitAssignment(string command, string names, string assignment)
    {
        string text = InAfterBlock($"{command} {names}", assignment, "ASSIGN Z = \"next\"");
        ProgramNode program = Parse(text);

        EventBlockNode after = OnlyEvent(program);
        Assert.Equal(2, after.Statements.Count);
        IReadOnlyList<string> kept = command switch
        {
            Parser.Commands.Enable => Assert.IsType<EnableStatementNode>(after.Statements[0]).Names,
            Parser.Commands.Disable => Assert.IsType<DisableStatementNode>(after.Statements[0]).Names,
            _ => Assert.IsType<ClearStatementNode>(after.Statements[0]).Names,
        };
        Assert.Equal(names.Split(' '), kept);
        AssertAssign(after.Statements[1], "Z", "next");

        var expected = new[] { Warning(Parser.Commands.ImplicitAssign, 4) };
        Assert.Equal(expected, Summaries(after.Diagnostics));
        Assert.Equal(expected, Summaries(program.Diagnostics));

        FieldEventResponse response = new CheckCodeEngine().Run(
            text,
            TextForm("X", "A", "B", "C", "NUMERIC", "Z"),
            FieldEventKind.FieldAfter,
            "X",
            TextValues());

        IReadOnlyList<string> affected = command switch
        {
            Parser.Commands.Enable => response.Enabled,
            Parser.Commands.Disable => response.Disabled,
            _ => response.Cleared,
        };
        Assert.Equal(names.Split(' '), affected);
        Assert.Equal(new[] { "Z" }, response.Assigned.Keys);
        Assert.Equal("next", response.Assigned["Z"].AsText());
        Assert.Equal(expected, Summaries(response.Diagnostics));
    }

    // DIALOG (lines 364, 385) ends before an implicit assignment on line 4, with or without its title, and still runs.
    [Theory]
    [InlineData("DIALOG \"m\"", null)]
    [InlineData("DIALOG \"m\" TITLETEXT = \"T\"", "T")]
    public void DialogEndsBeforeAnImplicitAssignment(string statement, string? title)
    {
        string text = InAfterBlock(statement, "B = 1", "ASSIGN Z = \"next\"");
        ProgramNode program = Parse(text);

        EventBlockNode after = OnlyEvent(program);
        Assert.Collection(
            after.Statements,
            kept =>
            {
                DialogStatementNode dialog = Assert.IsType<DialogStatementNode>(kept);
                Assert.Equal(("m", title), (dialog.Message, dialog.Title));
            },
            kept => AssertAssign(kept, "Z", "next"));

        var expected = new[] { Warning(Parser.Commands.ImplicitAssign, 4) };
        Assert.Equal(expected, Summaries(after.Diagnostics));
        Assert.Equal(expected, Summaries(program.Diagnostics));

        FieldEventResponse response = new CheckCodeEngine().Run(
            text,
            TextForm("X", "B", "Z"),
            FieldEventKind.FieldAfter,
            "X",
            TextValues());

        DialogMessage shown = Assert.Single(response.Dialogs);
        Assert.Equal(("m", title), (shown.Message, shown.Title));
        Assert.Equal(new[] { "Z" }, response.Assigned.Keys);
        Assert.Equal("next", response.Assigned["Z"].AsText());
        Assert.Equal(expected, Summaries(response.Diagnostics));
    }

    // GEOCODE (line 636) ends before an implicit assignment on line 4: it keeps its node and its one info diagnostic.
    [Fact]
    public void GeocodeEndsBeforeAnImplicitAssignment()
    {
        string text = InAfterBlock("GEOCODE A, B, C", "D = 1", "ASSIGN Z = \"next\"");
        ProgramNode program = Parse(text);

        EventBlockNode after = OnlyEvent(program);
        Assert.Collection(
            after.Statements,
            kept => Assert.Equal(new[] { "A", "B", "C" }, Assert.IsType<GeocodeStatementNode>(kept).Arguments),
            kept => AssertAssign(kept, "Z", "next"));

        var expected = new[]
        {
            (DiagnosticSeverity.Info, Parser.Commands.Geocode, 3),
            Warning(Parser.Commands.ImplicitAssign, 4),
        };
        Assert.Equal(expected, Summaries(after.Diagnostics));
        Assert.Equal(expected, Summaries(program.Diagnostics));

        FieldEventResponse response = new CheckCodeEngine().Run(
            text,
            TextForm("X", "A", "B", "C", "D", "Z"),
            FieldEventKind.FieldAfter,
            "X",
            TextValues());

        Assert.Equal(new[] { "Z" }, response.Assigned.Keys);
        Assert.Equal(expected, Summaries(response.Diagnostics));
    }

    // A skipped statement on line 3 ends where an implicit assignment starts after a token that can end it: a name, a
    // literal, character literals included, a File or Url operand (lines 25-26, 41), a built-in value (line 254), a
    // command that is a whole statement alone (BEEP, EXIT, NEWRECORD, QUIT, SAVE-RECORD; lines 297, 404, 452, 609,
    // 645), ')' or the '*' of UNHIDE *, SET-REQUIRED *, UNDEFINE * or UNHIGHLIGHT * (lines 422, 427, 468, 476). The
    // assignment on line 4 is reported once, and the ASSIGN after it runs. Where a statement starts, a keyword no
    // statement reserves, such as NUMERIC, is the target of an implicit assignment, as it is a name elsewhere.
    [Theory]
    [InlineData("BEEP", "BEEP")]
    [InlineData("EXIT", "EXIT")]
    [InlineData("NEWRECORD", "NEWRECORD")]
    [InlineData("QUIT", "QUIT")]
    [InlineData("SAVE-RECORD", "SAVE-RECORD")]
    [InlineData(Goto, "GOTO Z")]
    [InlineData("UNHIDE", "UNHIDE *")]
    [InlineData("SET-REQUIRED", "SET-REQUIRED *")]
    [InlineData("UNDEFINE", "UNDEFINE *")]
    [InlineData("UNHIGHLIGHT", "UNHIGHLIGHT *")]
    [InlineData(Parser.Commands.Plus, "ASSIGN Y = (1 + 2)")]
    [InlineData(SystemDate, "ASSIGN Y = SYSTEMDATE")]
    [InlineData(Parser.Commands.ImplicitAssign, "C = D")]
    [InlineData(Parser.Commands.ImplicitAssign, "NUMERIC = 1")]
    [InlineData(Parser.Commands.Syntax, "\"text\"")]
    [InlineData("EXECUTE", "EXECUTE http://x")]
    [InlineData("EXECUTE", "EXECUTE 'C:\\a b\\x.exe'")]
    [InlineData("COMMANDLINE", "COMMANDLINE C:\\x\\p.prj")]
    [InlineData(Parser.Commands.Syntax, "'x'")]
    public void SkippedStatementEndsBeforeAnImplicitAssignment(string command, string statement)
    {
        string text = InAfterBlock(statement, "B = 1", "ASSIGN Z = \"next\"");
        ProgramNode program = Parse(text);

        EventBlockNode after = OnlyEvent(program);
        AssertAssign(Assert.Single(after.Statements), "Z", "next");

        var expected = new[] { Warning(command, 3), Warning(Parser.Commands.ImplicitAssign, 4) };
        Assert.Equal(expected, Summaries(after.Diagnostics));
        Assert.Equal(expected, Summaries(program.Diagnostics));

        FieldEventResponse response = new CheckCodeEngine().Run(
            text,
            TextForm("X", "B", "C", "D", "Y", "Z"),
            FieldEventKind.FieldAfter,
            "X",
            TextValues());

        Assert.Equal(new[] { "Z" }, response.Assigned.Keys);
        Assert.Equal(expected, Summaries(response.Diagnostics));
    }

    // A command that is a whole statement alone (lines 297, 404, 452, 609, 645) ends before an implicit assignment on
    // its own line as well, so line 3 gives the command's warning and then the assignment's, and only the ASSIGN on
    // line 4 runs.
    [Theory]
    [InlineData("BEEP")]
    [InlineData("EXIT")]
    [InlineData("NEWRECORD")]
    [InlineData("QUIT")]
    [InlineData("SAVE-RECORD")]
    public void ZeroOperandCommandEndsBeforeAnImplicitAssignmentOnItsLine(string command)
    {
        string text = InAfterBlock($"{command} B = 1", "ASSIGN Z = \"next\"");
        ProgramNode program = Parse(text);

        EventBlockNode after = OnlyEvent(program);
        AssertAssign(Assert.Single(after.Statements), "Z", "next");

        var expected = new[] { Warning(command, 3), Warning(Parser.Commands.ImplicitAssign, 3) };
        Assert.Equal(expected, Summaries(after.Diagnostics));
        Assert.Equal(expected, Summaries(program.Diagnostics));

        FieldEventResponse response = new CheckCodeEngine().Run(
            text,
            TextForm("X", "B", "Z"),
            FieldEventKind.FieldAfter,
            "X",
            TextValues());

        AssertOnlyAssigned(response, ("Z", "next"));
        Assert.Equal(expected, Summaries(response.Diagnostics));
    }

    // After an operator, a command or a keyword that takes an operand, "B = ..." is that operand followed by a
    // comparison, never an implicit assignment, so each case keeps exactly one warning, on line 3. Inside a statement
    // a keyword followed by '=' is that statement's option, never an assignment target (lines 173-181 build the
    // target from Identifier): COMMANDLINE takes PGMNAME = File and VIEWNAME = File : String in either order, on one
    // line or across two (lines 308-326). Neither the dropped IF's ASSIGN nor any skipped assignment runs; the ASSIGN
    // after the case does.
    [Theory]
    [InlineData(Parser.Commands.NotEqual, "IF A <> \"1\" AND B = \"2\" THEN", "ASSIGN Y = \"1\"", "END-IF")]
    [InlineData(Parser.Commands.Plus, "ASSIGN Y = 1 + A = 2")]
    [InlineData(Parser.Commands.Multiply, "ASSIGN Y = A * B = 1")]
    [InlineData("LET", "LET A = 1")]
    [InlineData("DEFINE", "DEFINE V = 1")]
    [InlineData(Parser.Commands.Enable, "ENABLE B = 1")]
    [InlineData(Parser.Commands.ImplicitAssign, "B = C = 1")]
    [InlineData("COMMANDLINE", "COMMANDLINE PGMNAME = 'C:\\first.prj'", "VIEWNAME = 'C:\\second.prj' : \"v\"")]
    [InlineData("COMMANDLINE", "COMMANDLINE PGMNAME = 'C:\\first.prj' VIEWNAME = 'C:\\second.prj' : \"v\"")]
    [InlineData("COMMANDLINE", "COMMANDLINE VIEWNAME = 'C:\\second.prj' : \"v\"", "PGMNAME = 'C:\\first.prj'")]
    [InlineData("COMMANDLINE", "COMMANDLINE VIEWNAME = 'C:\\second.prj' : \"v\" PGMNAME = 'C:\\first.prj'")]
    public void NameAfterAnOperatorOrAnOperandKeywordStartsNoImplicitAssignment(
        string command,
        params string[] lines)
    {
        string text = InAfterBlock([.. lines, "ASSIGN Z = \"next\""]);
        ProgramNode program = Parse(text);

        EventBlockNode after = OnlyEvent(program);
        AssertAssign(Assert.Single(after.Statements), "Z", "next");

        var expected = new[] { Warning(command, 3) };
        Assert.Equal(expected, Summaries(after.Diagnostics));
        Assert.Equal(expected, Summaries(program.Diagnostics));

        FieldEventResponse response = new CheckCodeEngine().Run(
            text,
            TextForm("X", "A", "B", "C", "V", "Y", "Z"),
            FieldEventKind.FieldAfter,
            "X",
            TextValues(("A", "1"), ("B", "2")));

        Assert.Equal(new[] { "Z" }, response.Assigned.Keys);
        Assert.Equal(expected, Summaries(response.Diagnostics));
    }

    // The ALWAYS after AUTOSEARCH's operands (lines 290-293) is its modifier unless it opens ALWAYS ... END (line 275):
    // it does only when a statement starts its body (line 74) and, with it open, the terminators before End-After close
    // it, the ALWAYS being skipped and every enclosing IF, END closing any construct and END-IF only an IF. Line breaks
    // carry no meaning. An ALWAYS is skipped whole with one warning, each IF and ALWAYS nested in it matched against
    // its own terminator, and a statement-level AUTOSEARCH has one warning of its own. Each expected warning is written
    // COMMAND:line and each assignment Field=text; ASSIGN Z = "next" follows every case, and nothing else runs.
    [Theory]
    [InlineData("ALWAYS:3", "Z=next", "ALWAYS", "AUTOSEARCH A ALWAYS", "END")]
    [InlineData("ALWAYS:3", "Z=next", "ALWAYS", "AUTOSEARCH A DISPLAYLIST B ALWAYS", "END")]
    [InlineData("ALWAYS:3", "Z=next", "ALWAYS", "IF A = \"1\" THEN", "AUTOSEARCH A ALWAYS", "END-IF", "END")]
    [InlineData("ALWAYS:3", "Z=next", "ALWAYS", "IF A = \"1\" THEN", "ALWAYS", "HIDE B", "END", "END-IF", "END")]
    [InlineData("AUTOSEARCH:3", "Z=next", "AUTOSEARCH A DISPLAYLIST B ALWAYS")]
    [InlineData(
        "ALWAYS:3",
        "Z=next",
        "ALWAYS",
        "AUTOSEARCH A",
        "ALWAYS",
        "ASSIGN B = \"inner\"",
        "END",
        "ASSIGN W = \"outer\"",
        "END")]
    [InlineData("ALWAYS:3", "Z=next", "ALWAYS AUTOSEARCH A ALWAYS ASSIGN B = \"inner\" END ASSIGN W = \"outer\" END")]
    [InlineData("ALWAYS:3", "Z=next", "ALWAYS", "AUTOSEARCH A", "ALWAYS", "AUTOSEARCH B ALWAYS", "END", "END")]
    [InlineData(
        "ALWAYS:3",
        "Z=next",
        "ALWAYS",
        "AUTOSEARCH A",
        "ALWAYS",
        "AUTOSEARCH B",
        "ALWAYS",
        "GOTO C",
        "END",
        "END",
        "END")]
    [InlineData("ALWAYS:4", "Z=next", "IF A = \"1\" THEN", "ALWAYS", "AUTOSEARCH A ALWAYS", "END", "END")]
    [InlineData(
        "ALWAYS:4",
        "Z=next",
        "IF A = \"1\" THEN",
        "ALWAYS",
        "AUTOSEARCH A ALWAYS",
        "ASSIGN B = \"inner\"",
        "END",
        "END")]
    [InlineData(
        "ALWAYS:4",
        "Z=next",
        "IF A = \"1\" THEN",
        "ALWAYS",
        "AUTOSEARCH A",
        "ALWAYS",
        "ASSIGN B = \"inner\"",
        "END",
        "END",
        "END-IF")]
    [InlineData(
        "ALWAYS:4 END-IF:8",
        "Z=next",
        "IF A = \"1\" THEN",
        "ALWAYS",
        "AUTOSEARCH A ALWAYS",
        "END",
        "END",
        "END-IF")]
    [InlineData("AUTOSEARCH:3", "Y=y Z=next", "AUTOSEARCH A ALWAYS", "ASSIGN Y = \"y\"")]
    [InlineData("AUTOSEARCH:3 ALWAYS:4", "Z=next", "AUTOSEARCH A", "ALWAYS", "GOTO B", "END")]
    [InlineData("AUTOSEARCH:3 END:4", "Z=next", "AUTOSEARCH A ALWAYS", "END")]
    [InlineData("AUTOSEARCH:4", "Y=y Z=next", "IF A = \"1\" THEN", "AUTOSEARCH A ALWAYS", "ASSIGN Y = \"y\"", "END")]
    [InlineData(
        "AUTOSEARCH:4 ALWAYS:5",
        "Z=next",
        "IF A = \"1\" THEN",
        "AUTOSEARCH A",
        "ALWAYS",
        "ASSIGN Y = \"y\"",
        "END",
        "END-IF")]
    public void AutoSearchModifierOpensNoBlockAndAlwaysIsSkippedWhole(
        string warnings,
        string assigned,
        params string[] lines)
    {
        string text = InAfterBlock([.. lines, "ASSIGN Z = \"next\""]);
        ProgramNode program = Parse(text);

        EventBlockNode after = OnlyEvent(program);
        AssertAssign(after.Statements[^1], "Z", "next");

        (DiagnosticSeverity Severity, string Command, int Line)[] expected =
        [
            .. warnings.Split(' ')
                .Select(entry => entry.Split(':'))
                .Select(parts => Warning(
                    parts[0],
                    int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture))),
        ];
        Assert.Equal(expected, Summaries(after.Diagnostics));
        Assert.Equal(expected, Summaries(program.Diagnostics));

        FieldEventResponse response = new CheckCodeEngine().Run(
            text,
            TextForm("X", "A", "B", "C", "W", "Y", "Z"),
            FieldEventKind.FieldAfter,
            "X",
            TextValues(("A", "1")));

        AssertOnlyAssigned(
            response,
            [.. assigned.Split(' ').Select(entry => entry.Split('=')).Select(parts => (parts[0], parts[1]))]);
        Assert.Equal(expected, Summaries(response.Diagnostics));
    }

    // The WAITFOREXIT or NOWAITFOREXIT after EXECUTE is its modifier (lines 392-400), so EXECUTE gets the one warning
    // on line 3; written first, WAITFOREXIT is its own statement (line 581) with its own one warning.
    [Theory]
    [InlineData("EXECUTE", "EXECUTE WAITFOREXIT \"x\"")]
    [InlineData("EXECUTE", "EXECUTE NOWAITFOREXIT \"x\"")]
    [InlineData("WAITFOREXIT", "WAITFOREXIT \"a\" \"b\"")]
    public void ExecuteModifierIsPartOfItsStatement(string command, string statement)
    {
        string text = InAfterBlock(statement, "ASSIGN Z = \"next\"");
        ProgramNode program = Parse(text);

        EventBlockNode after = OnlyEvent(program);
        AssertAssign(Assert.Single(after.Statements), "Z", "next");

        var expected = new[] { Warning(command, 3) };
        Assert.Equal(expected, Summaries(after.Diagnostics));
        Assert.Equal(expected, Summaries(program.Diagnostics));

        FieldEventResponse response = new CheckCodeEngine().Run(
            text,
            TextForm("X", "Z"),
            FieldEventKind.FieldAfter,
            "X",
            TextValues());

        Assert.Equal(new[] { "Z" }, response.Assigned.Keys);
        Assert.Equal("next", response.Assigned["Z"].AsText());
        Assert.Equal(expected, Summaries(response.Diagnostics));
    }

    [Theory]
    [InlineData("YEARS")]
    [InlineData("years")]
    [InlineData("Years")]
    [InlineData("[YEARS]")]
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

    // Line 30 makes a bracketed word an identifier, so it never plays a keyword's role, and a required name slot
    // (lines 281, 636) never takes the keyword that starts the next statement. Each case starts on line 3, gives
    // exactly one warning there, and leaves the ASSIGN after it to parse and run; B = "1" would run any IF that
    // wrongly survived.
    [Theory]
    [InlineData(Parser.Commands.ImplicitAssign, "[ASSIGN] = \"1\"")]
    [InlineData(Parser.Commands.If, "IF B = \"1\" [THEN] ASSIGN Y = \"1\"", "END-IF")]
    [InlineData(SystemDate, "ASSIGN Y = SYSTEMDATE")]
    [InlineData(Parser.Commands.Assign, "ASSIGN")]
    [InlineData(Parser.Commands.Geocode, "GEOCODE")]
    [InlineData(Parser.Commands.Geocode, "GEOCODE A")]
    [InlineData(Parser.Commands.Geocode, "GEOCODE A,")]
    [InlineData(Parser.Commands.Geocode, "GEOCODE A, B")]
    [InlineData(Parser.Commands.Geocode, "GEOCODE A, B,")]
    public void KeywordRoleErrorIsReportedOnceAndTheNextStatementRuns(string command, params string[] lines)
    {
        string text = InAfterBlock([.. lines, "ASSIGN Z = \"next\""]);

        EventBlockNode after = OnlyEvent(Parse(text));
        AssertAssign(Assert.Single(after.Statements), "Z", "next");
        Assert.Equal(new[] { Warning(command, 3) }, Summaries(after.Diagnostics));

        FieldEventResponse response = new CheckCodeEngine().Run(
            text,
            TextForm("X", "A", "B", "Y", "Z"),
            FieldEventKind.FieldAfter,
            "X",
            TextValues(("B", "1")));

        Assert.Equal(new[] { "Z" }, response.Assigned.Keys);
        Assert.Equal("next", response.Assigned["Z"].AsText());
    }

    // [ASSIGN] is a name, so it starts no statement and A is never assigned: the name alone is junk, and the
    // A = "1" after it is an implicit assignment (line 285), each reported on line 3 in that order.
    [Fact]
    public void BracketedCommandWordStartsNoStatement()
    {
        string text = InAfterBlock("[ASSIGN] A = \"1\"", "ASSIGN Z = \"next\"");
        ProgramNode program = Parse(text);

        EventBlockNode after = OnlyEvent(program);
        AssertAssign(Assert.Single(after.Statements), "Z", "next");

        var expected = new[] { Warning(Parser.Commands.Syntax, 3), Warning(Parser.Commands.ImplicitAssign, 3) };
        Assert.Equal(expected, Summaries(after.Diagnostics));
        Assert.Equal(expected, Summaries(program.Diagnostics));

        FieldEventResponse response = new CheckCodeEngine().Run(
            text,
            TextForm("X", "A", "Z"),
            FieldEventKind.FieldAfter,
            "X",
            TextValues());

        AssertOnlyAssigned(response, ("Z", "next"));
        Assert.Equal(expected, Summaries(response.Diagnostics));
    }

    // [TITLETEXT] is a name, not the option of line 385, so DIALOG "m" is complete without a title (line 364) and
    // [TITLETEXT] = "t" after it is an implicit assignment (line 285), reported on line 3.
    [Fact]
    public void BracketedTitleTextIsNotTheDialogOption()
    {
        string text = InAfterBlock("DIALOG \"m\" [TITLETEXT] = \"t\"", "ASSIGN Z = \"next\"");
        ProgramNode program = Parse(text);

        EventBlockNode after = OnlyEvent(program);
        Assert.Collection(
            after.Statements,
            kept =>
            {
                DialogStatementNode dialog = Assert.IsType<DialogStatementNode>(kept);
                Assert.Equal("m", dialog.Message);
                Assert.Null(dialog.Title);
            },
            kept => AssertAssign(kept, "Z", "next"));

        var expected = new[] { Warning(Parser.Commands.ImplicitAssign, 3) };
        Assert.Equal(expected, Summaries(after.Diagnostics));
        Assert.Equal(expected, Summaries(program.Diagnostics));

        FieldEventResponse response = new CheckCodeEngine().Run(
            text,
            TextForm("X", "Z"),
            FieldEventKind.FieldAfter,
            "X",
            TextValues());

        DialogMessage shown = Assert.Single(response.Dialogs);
        Assert.Equal("m", shown.Message);
        Assert.Null(shown.Title);
        Assert.Empty(response.Enabled);
        Assert.Empty(response.Disabled);
        Assert.Empty(response.Cleared);
        Assert.Equal(new[] { "Z" }, response.Assigned.Keys);
        Assert.Equal("next", response.Assigned["Z"].AsText());
        Assert.Equal(expected, Summaries(response.Diagnostics));
    }

    // Only the keyword is the built-in value of line 254; bracketed, SYSTEMDATE is the field of that name (line 30).
    [Fact]
    public void BracketedBuiltInWordIsAFieldReference()
    {
        string text = InAfterBlock("ASSIGN Y = [SYSTEMDATE]");

        ProgramNode program = Parse(text);
        AssignStatementNode assign = Assert.IsType<AssignStatementNode>(Assert.Single(OnlyEvent(program).Statements));
        Assert.Equal("Y", assign.Target);
        AssertIdentifier(assign.Value, SystemDate);
        Assert.Empty(program.Diagnostics);

        FieldEventResponse response = new CheckCodeEngine().Run(
            text,
            TextForm("X", SystemDate, "Y"),
            FieldEventKind.FieldAfter,
            "X",
            TextValues((SystemDate, "stored")));

        Assert.Equal(new[] { "Y" }, response.Assigned.Keys);
        Assert.Equal("stored", response.Assigned["Y"].AsText());
        Assert.Empty(response.Diagnostics);
    }

    // GEOCODE's missing last operand leaves line 4's After to the field block, so the After block parses and runs;
    // After also closes the Before block, which reports its missing END-BEFORE there.
    [Fact]
    public void GeocodeMissingAnOperandLeavesTheNextEventBlockToRun()
    {
        const string text = """
            Field X
            	Before
            		GEOCODE A, B,
            	After
            		ASSIGN Z = "1"
            	End-After
            End-Field
            """;

        ProgramNode program = Parse(text);

        Assert.Collection(
            Assert.Single(program.FieldBlocks).Events,
            before =>
            {
                Assert.Equal(FieldEventKind.FieldBefore, before.Event);
                Assert.Empty(before.Statements);
            },
            after =>
            {
                Assert.Equal(FieldEventKind.FieldAfter, after.Event);
                AssertAssign(Assert.Single(after.Statements), "Z", "1");
                Assert.Empty(after.Diagnostics);
            });
        Assert.Equal(
            new[] { Warning(Parser.Commands.Geocode, 3), Warning(EndBefore, 4) },
            Summaries(program.Diagnostics));

        FieldEventResponse response = new CheckCodeEngine().Run(
            text,
            TextForm("X", "A", "B", "Z"),
            FieldEventKind.FieldAfter,
            "X",
            TextValues());

        Assert.Equal("1", response.Assigned["Z"].AsText());
        Assert.Empty(response.Diagnostics);
    }

    // A GEOCODE without operands leaves line 4's Field to the program, so Field Y is a block of its own that parses
    // and runs; Field also closes the Click block and Field X, each reporting its missing terminator there.
    [Fact]
    public void GeocodeWithoutOperandsLeavesTheNextFieldBlockToRun()
    {
        const string text = """
            Field X
            	Click
            		GEOCODE
            Field Y
            	After
            		ASSIGN Z = "1"
            	End-After
            End-Field
            """;

        ProgramNode program = Parse(text);

        Assert.Collection(
            program.FieldBlocks,
            x =>
            {
                Assert.Equal("X", x.FieldName);
                EventBlockNode click = Assert.Single(x.Events);
                Assert.Equal(FieldEventKind.FieldClick, click.Event);
                Assert.Empty(click.Statements);
            },
            y =>
            {
                Assert.Equal("Y", y.FieldName);
                Assert.Equal(4, y.Line);
                EventBlockNode after = Assert.Single(y.Events);
                Assert.Equal(FieldEventKind.FieldAfter, after.Event);
                AssertAssign(Assert.Single(after.Statements), "Z", "1");
                Assert.Empty(after.Diagnostics);
            });
        Assert.Equal(
            new[] { Warning(Parser.Commands.Geocode, 3), Warning(EndClick, 4), Warning(Parser.Commands.EndField, 4) },
            Summaries(program.Diagnostics));

        FieldEventResponse response = new CheckCodeEngine().Run(
            text,
            TextForm("X", "Y", "Z"),
            FieldEventKind.FieldAfter,
            "Y",
            TextValues());

        Assert.Equal("1", response.Assigned["Z"].AsText());
        Assert.Empty(response.Diagnostics);
    }

    // Bracketed, a keyword is a name (line 30), so it fills the ASSIGN target (line 281) and each GEOCODE operand
    // (line 636).
    [Fact]
    public void BracketedKeywordNamesFillTheNameSlots()
    {
        string text = InAfterBlock("ASSIGN [Then] = \"1\"", "GEOCODE [Field], [After], [Click]");

        ProgramNode program = Parse(text);

        EventBlockNode after = OnlyEvent(program);
        Assert.Collection(
            after.Statements,
            statement => AssertAssign(statement, "Then", "1"),
            statement => Assert.Equal(
                new[] { "Field", "After", "Click" },
                Assert.IsType<GeocodeStatementNode>(statement).Arguments));

        var expected = new[] { (DiagnosticSeverity.Info, Parser.Commands.Geocode, 4) };
        Assert.Equal(expected, Summaries(after.Diagnostics));
        Assert.Equal(expected, Summaries(program.Diagnostics));

        FieldEventResponse response = new CheckCodeEngine().Run(
            text,
            TextForm("X", "Then", "Field", "After", "Click"),
            FieldEventKind.FieldAfter,
            "X",
            TextValues());

        Assert.Equal("1", response.Assigned["Then"].AsText());
    }

    // Line 42 gives the boolean literals; (+) is outside the subset and pinned among the recovery cases. A number
    // keeps its spelling, because its double alone cannot tell 2.0000000000000001 from the whole number 2.
    [Theory]
    [InlineData("101.2", LiteralKind.Number, "101.2", 101.2)]
    [InlineData("2.0000000000000001", LiteralKind.Number, "2.0000000000000001", 2.0)]
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

    // An ASSIGN ends with its expression (line 281), so a token left before the next statement boundary, on its line
    // or the next, drops the whole statement with one ASSIGN warning on line 3: A is never assigned, even though
    // each value before the leftover token is valid, and the ASSIGN after it runs.
    [Theory]
    [InlineData("ASSIGN A = B C")]
    [InlineData("ASSIGN A = B", "C")]
    [InlineData("ASSIGN A = \"1\" THEN")]
    [InlineData("ASSIGN A = B )")]
    [InlineData("ASSIGN A = B `")]
    [InlineData("ASSIGN A = YEARS(B, C) C")]
    public void AssignFollowedByLeftoverTokensIsDroppedWhole(params string[] lines)
    {
        string text = InAfterBlock([.. lines, "ASSIGN Z = \"next\""]);
        ProgramNode program = Parse(text);

        EventBlockNode after = OnlyEvent(program);
        AssertAssign(Assert.Single(after.Statements), "Z", "next");

        var expected = new[] { Warning(Parser.Commands.Assign, 3) };
        Assert.Equal(expected, Summaries(after.Diagnostics));
        Assert.Equal(expected, Summaries(program.Diagnostics));

        FieldEventResponse response = new CheckCodeEngine().Run(
            text,
            TextForm("X", "A", "B", "C", "Z"),
            FieldEventKind.FieldAfter,
            "X",
            TextValues(("B", "b")));

        AssertOnlyAssigned(response, ("Z", "next"));
        Assert.Equal(expected, Summaries(response.Diagnostics));
    }

    // An implicit assignment (line 285) is a statement boundary, so ASSIGN A = B C = 1 ends after B and runs, and
    // C = 1 is the next statement, reported once on line 3 and never run.
    [Fact]
    public void AssignEndsBeforeAnImplicitAssignmentOnItsLine()
    {
        string text = InAfterBlock("ASSIGN A = B C = 1", "ASSIGN Z = \"next\"");
        ProgramNode program = Parse(text);

        EventBlockNode after = OnlyEvent(program);
        Assert.Collection(
            after.Statements,
            statement =>
            {
                AssignStatementNode assign = Assert.IsType<AssignStatementNode>(statement);
                Assert.Equal(("A", 3), (assign.Target, assign.Line));
                AssertIdentifier(assign.Value, "B");
            },
            statement => AssertAssign(statement, "Z", "next"));

        var expected = new[] { Warning(Parser.Commands.ImplicitAssign, 3) };
        Assert.Equal(expected, Summaries(after.Diagnostics));
        Assert.Equal(expected, Summaries(program.Diagnostics));

        FieldEventResponse response = new CheckCodeEngine().Run(
            text,
            TextForm("X", "A", "B", "C", "Z"),
            FieldEventKind.FieldAfter,
            "X",
            TextValues(("B", "b")));

        AssertOnlyAssigned(response, ("A", "b"), ("Z", "next"));
        Assert.Equal(expected, Summaries(response.Diagnostics));
    }

    // A legacy literal outside the subset is one token (lines 37-41), so it is reported once, on line 3, under its
    // kind: a suffixed DecLiteral or a HexLiteral as NUMBER-LITERAL and a CharLiteral as CHAR-LITERAL. Its ASSIGN is
    // dropped whole, so neither A = 12 nor any other prefix runs, and the ASSIGN after it does. A quote or '#' that
    // starts no literal stays a malformed ASSIGN expression.
    [Theory]
    [InlineData(Parser.Commands.NumberLiteral, "12L")]
    [InlineData(Parser.Commands.NumberLiteral, "12U")]
    [InlineData(Parser.Commands.NumberLiteral, "12UL")]
    [InlineData(Parser.Commands.NumberLiteral, "12lu")]
    [InlineData(Parser.Commands.NumberLiteral, "#1a")]
    [InlineData(Parser.Commands.CharLiteral, "'x'")]
    [InlineData(Parser.Commands.Assign, "'")]
    [InlineData(Parser.Commands.Assign, "#")]
    [InlineData(Parser.Commands.Assign, "#12")]
    public void LegacyLiteralDropsItsAssignWithOneWarning(string command, string literal)
    {
        string text = InAfterBlock($"ASSIGN A = {literal}", "ASSIGN Z = \"next\"");
        ProgramNode program = Parse(text);

        EventBlockNode after = OnlyEvent(program);
        AssertAssign(Assert.Single(after.Statements), "Z", "next");

        var expected = new[] { Warning(command, 3) };
        Assert.Equal(expected, Summaries(after.Diagnostics));
        Assert.Equal(expected, Summaries(program.Diagnostics));

        // The diagnostic names the literal's kind, never its spelling.
        Diagnostic diagnostic = Assert.Single(program.Diagnostics);
        Assert.DoesNotContain(literal, diagnostic.Message, StringComparison.Ordinal);

        FieldEventResponse response = new CheckCodeEngine().Run(
            text,
            TextForm("X", "A", "Z"),
            FieldEventKind.FieldAfter,
            "X",
            TextValues());

        AssertOnlyAssigned(response, ("Z", "next"));
        Assert.Equal(expected, Summaries(response.Diagnostics));
    }

    // A File or Url operand (lines 25-26) is one opaque token, so the reserved words and '//' inside it neither close
    // the After block nor hide what follows: each case gives exactly one warning, on line 3, for the unsupported
    // command, or SYNTAX for an operand starting a statement, or ASSIGN for one used as a value, and the ASSIGN Z
    // after it, on its own line or on the same line after a Url, is kept and runs. Nothing is executed, and no
    // diagnostic repeats the path or URL.
    [Theory]
    [InlineData("EXECUTE", "EXECUTE http://example.org/a ASSIGN Z = \"next\"")]
    [InlineData("EXECUTE", "EXECUTE HTTP://Example.org//End-After ASSIGN Z = \"next\"")]
    [InlineData("EXECUTE", "EXECUTE C:\\Program Files\\End-Field\\app.exe", "ASSIGN Z = \"next\"")]
    [InlineData("EXECUTE", "EXECUTE \\\\srv\\share\\End-After\\x.exe", "ASSIGN Z = \"next\"")]
    [InlineData("EXECUTE", "EXECUTE 'C:\\End-Field\\x.exe'", "ASSIGN Z = \"next\"")]
    [InlineData("EXECUTE", "EXECUTE WAITFOREXIT .\\End-If\\x.exe", "ASSIGN Z = \"next\"")]
    [InlineData("EXECUTE", "EXECUTE \\End-After\\x.exe // a comment", "ASSIGN Z = \"next\"")]
    [InlineData("WAITFOR", "WAITFOR ..\\End-Field\\a.txt \"x\"", "ASSIGN Z = \"next\"")]
    [InlineData(Always, "ALWAYS", "EXECUTE C:\\End\\If\\x.exe", "END", "ASSIGN Z = \"next\"")]
    [InlineData("COMMANDLINE", "COMMANDLINE C:\\x\\End-Sub\\p.prj", "ASSIGN Z = \"next\"")]
    [InlineData(Parser.Commands.Syntax, "C:\\End-After\\x.exe", "ASSIGN Z = \"next\"")]
    [InlineData(Parser.Commands.Assign, "ASSIGN A = C:\\End-After\\x.exe", "ASSIGN Z = \"next\"")]
    [InlineData(Parser.Commands.Assign, "ASSIGN A = http://x/End-After", "ASSIGN Z = \"next\"")]
    public void FileOrUrlOperandIsSkippedWithItsStatementAndTheNextStatementRuns(string command, params string[] lines)
    {
        string text = InAfterBlock(lines);
        ProgramNode program = Parse(text);

        EventBlockNode after = OnlyEvent(program);
        AssertAssign(Assert.Single(after.Statements), "Z", "next");

        var expected = new[] { Warning(command, 3) };
        Assert.Equal(expected, Summaries(after.Diagnostics));
        Assert.Equal(expected, Summaries(program.Diagnostics));

        Diagnostic diagnostic = Assert.Single(program.Diagnostics);
        Assert.DoesNotContain("End-", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\\", diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("//", diagnostic.Message, StringComparison.Ordinal);

        FieldEventResponse response = new CheckCodeEngine().Run(
            text,
            TextForm("X", "A", "Z"),
            FieldEventKind.FieldAfter,
            "X",
            TextValues());

        AssertOnlyAssigned(response, ("Z", "next"));
        Assert.Equal(expected, Summaries(response.Diagnostics));
    }

    // A reserved word inside a path never closes the View block it stands in (line 629), so the View is skipped to
    // its own End-View on line 5 with its one warning, and Field X after it parses and runs with no diagnostic.
    [Fact]
    public void PathInsideAnUnsupportedBlockDoesNotCloseIt()
    {
        const string text = """
            View
            	Before
            		EXECUTE C:\x\End-View\y.exe
            	End-Before
            End-View
            Field X
            	After
            		ASSIGN Z = "next"
            	End-After
            End-Field
            """;

        ProgramNode program = Parse(text);

        FieldBlockNode block = Assert.Single(program.FieldBlocks);
        Assert.Equal(("X", 6), (block.FieldName, block.Line));
        EventBlockNode after = Assert.Single(block.Events);
        AssertAssign(Assert.Single(after.Statements), "Z", "next");
        Assert.Empty(after.Diagnostics);
        Assert.Equal(new[] { Warning(Parser.Commands.View, 1) }, Summaries(program.Diagnostics));

        FieldEventResponse response = new CheckCodeEngine().Run(
            text,
            TextForm("X", "Z"),
            FieldEventKind.FieldAfter,
            "X",
            TextValues());

        AssertOnlyAssigned(response, ("Z", "next"));
        Assert.Empty(response.Diagnostics);
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

    // A one-line IF without END-IF or END: the parser finds the missing terminator only after the statements of the
    // branch, but reports it at the IF, which stands first on the line, so both lists order it first. The nested case
    // finds the inner IF's terminator missing before the outer one's.
    [Theory]
    [InlineData("IF A = \"1\" THEN HIDE Y", new[] { Parser.Commands.If, Hide })]
    [InlineData("IF A = \"1\" THEN IF A = \"2\" THEN HIDE Y", new[] { Parser.Commands.If, Parser.Commands.If, Hide })]
    public void DiagnosticsFoundOutOfOrderOnOneLineAreInSourceOrder(string statement, string[] commands)
    {
        ProgramNode program = Parse(InAfterBlock(statement));

        EventBlockNode after = OnlyEvent(program);
        (DiagnosticSeverity Severity, string Command, int Line)[] expected =
            [.. commands.Select(command => Warning(command, 3))];
        Assert.Equal(expected, Summaries(program.Diagnostics));
        Assert.Equal(expected, Summaries(after.Diagnostics));
        Assert.Empty(after.Statements);
    }

    // Tokens no lexer produces: default(Token), and hand-built tokens at line or column 0 or below, without an end
    // token, so the parser's own end token copies the last token's position, or with an explicit one. Each case
    // gives the commands its diagnostics carry, in order.
    public static TheoryData<Token[], string[]> NonPositivePositionCases => new()
    {
        { [default], [Parser.Commands.Syntax] },
        { [new Token(TokenKind.Keyword, "GOTO", 0, 0)], [Goto] },
        {
            [
                new Token(TokenKind.Keyword, "FIELD", 0, 0),
                new Token(TokenKind.Identifier, "X", 0, 0),
                new Token(TokenKind.Keyword, "AFTER", 0, 0),
            ],
            ["END-AFTER", Parser.Commands.EndField]
        },
        {
            [
                new Token(TokenKind.Keyword, "FIELD", 1, 1),
                new Token(TokenKind.Identifier, "X", 1, 7),
                new Token(TokenKind.Keyword, "AFTER", 1, 9),
                new Token(TokenKind.EndOfInput, string.Empty, -1, -1),
            ],
            ["END-AFTER", Parser.Commands.EndField]
        },
    };

    // A diagnostic's line is 1-based, so a position before the first line is reported as line 1.
    [Theory]
    [MemberData(nameof(NonPositivePositionCases), DisableDiscoveryEnumeration = true)]
    public void TokensBeforeTheFirstLineReportLineOne(Token[] tokens, string[] commands)
    {
        ProgramNode program = Parser.Parse(tokens, "F");

        Assert.Equal(commands, program.Diagnostics.Select(diagnostic => diagnostic.Command));
        Assert.All(program.Diagnostics, diagnostic => Assert.Equal(1, diagnostic.Line));
    }

    // Fixed spellings of each kind, written out, and every Parser.Commands constant, read by reflection so that a
    // constant added later is covered too.
    public static TheoryData<string> VocabularySpellings
    {
        get
        {
            var data = new TheoryData<string>
            {
                Goto, Hide, Always, SystemDate, EndBefore, EndClick, "FORMAT", "SET-REQUIRED", "END-VIEW",
                FunctionNames.Years, FunctionNames.EpiWeek,
            };
            foreach (System.Reflection.FieldInfo constant in typeof(Parser.Commands).GetFields())
            {
                if (constant.IsLiteral && constant.GetRawConstantValue() is string spelling)
                {
                    data.Add(spelling);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(VocabularySpellings))]
    public void IsVocabularyCommandAcceptsTheFixedSpellings(string command) =>
        Assert.True(Parser.IsVocabularyCommand(command), $"'{command}' is not vocabulary.");

    // Only the exact upper-case spellings the parser reports count; anything else is text the caller must not repeat.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(Days)]
    [InlineData("goto")]
    [InlineData(" GOTO")]
    [InlineData("SECRET PII\r\n TOKEN")]
    public void IsVocabularyCommandRejectsOtherText(string? command) =>
        Assert.False(Parser.IsVocabularyCommand(command));

    // A cancelled parse stops with the cancellation itself, through either overload: the last-resort net for a parser
    // defect never turns it into a SYNTAX warning.
    [Fact]
    public void ParseStopsOnACancelledToken()
    {
        IReadOnlyList<Token> tokens = Lexer.Tokenize(InAfterBlock("GOTO Y", "ASSIGN Y = 1"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => Parser.Parse(tokens, "F", cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => Parser.Parse(tokens, TextForm("Y"), cancellation.Token));
    }

    // An unsupported function is reported under its name as written, upper-cased: an unregistered name, a field's
    // name, or a bracketed name that keeps its inner whitespace and line breaks. None of those is vocabulary, which
    // is how the API tells that name, program text, from a command it may log.
    [Theory]
    [InlineData("ASSIGN Y = DAYS(A, B)", Days)]
    [InlineData("ASSIGN Y = LastName(A)", "LASTNAME")]
    [InlineData("ASSIGN Y = [Secret Pii\r\n Token](1)", "SECRET PII\r\n TOKEN")]
    public void UnsupportedFunctionIsReportedUnderItsWrittenNameOutsideTheVocabulary(string statement, string name)
    {
        Diagnostic diagnostic = Assert.Single(Parse(InAfterBlock(statement)).Diagnostics);

        Assert.Equal((DiagnosticSeverity.Warning, name, 3), (diagnostic.Severity, diagnostic.Command, diagnostic.Line));
        Assert.False(Parser.IsVocabularyCommand(diagnostic.Command));
    }

    // Every unsupported function's diagnostic is recognised whatever its name's spelling: a bracketed name spelled
    // like a statement keyword or holding a line break, a lexer keyword the parser does not reserve, an unregistered
    // name, and a built-in value written without parentheses (line 254). The first, third and last are spelled like
    // vocabulary, which is why the spelling alone cannot tell them from a statement's keyword.
    [Theory]
    [InlineData("ASSIGN Y = [GOTO](1)", Goto)]
    [InlineData("ASSIGN Y = [Secret\r\n Name](1)", "SECRET\r\n NAME")]
    [InlineData("ASSIGN Y = FORMAT(A)", "FORMAT")]
    [InlineData("ASSIGN Y = DAYS(A, B)", Days)]
    [InlineData("ASSIGN Y = SYSTEMDATE", SystemDate)]
    public void ReportsUnsupportedFunctionRecognisesEveryUnsupportedFunction(string statement, string command)
    {
        Diagnostic diagnostic = Assert.Single(Parse(InAfterBlock(statement)).Diagnostics);

        Assert.Equal(Warning(command, 3), (diagnostic.Severity, diagnostic.Command, diagnostic.Line));
        Assert.True(Parser.ReportsUnsupportedFunction(diagnostic));
    }

    // A construct other than an unsupported function is not such a report, though it carries the same or a function's
    // spelling: a GOTO statement, a malformed call of a supported function, an unsupported operator, unrecognised
    // text and a GEOCODE.
    [Theory]
    [InlineData("GOTO Z", DiagnosticSeverity.Warning, Goto)]
    [InlineData("ASSIGN Y = YEARS(A, B", DiagnosticSeverity.Warning, FunctionNames.Years)]
    [InlineData("ASSIGN Y = 1 + 2", DiagnosticSeverity.Warning, Parser.Commands.Plus)]
    [InlineData("junk", DiagnosticSeverity.Warning, Parser.Commands.Syntax)]
    [InlineData("GEOCODE A, B, C", DiagnosticSeverity.Info, Parser.Commands.Geocode)]
    public void ReportsUnsupportedFunctionRejectsOtherStaticDiagnostics(
        string statement,
        DiagnosticSeverity severity,
        string command)
    {
        Diagnostic diagnostic = Assert.Single(Parse(InAfterBlock(statement)).Diagnostics);

        Assert.Equal((severity, command, 3), (diagnostic.Severity, diagnostic.Command, diagnostic.Line));
        Assert.False(Parser.ReportsUnsupportedFunction(diagnostic));
    }

    // Runtime errors carry the failing statement's command: the engine's unknown-field error, and the evaluator's own
    // guard against an unregistered function in a hand-built tree, which names the function under ASSIGN.
    [Fact]
    public void ReportsUnsupportedFunctionRejectsRuntimeErrors()
    {
        FormMetadata form = TextForm("X", "A", "Y");
        FieldEventResponse engineResponse = new CheckCodeEngine().Run(
            InAfterBlock("ASSIGN Unknown = \"1\""),
            form,
            FieldEventKind.FieldAfter,
            "X",
            TextValues());
        var block = new EventBlockNode(
            FieldEventKind.FieldAfter,
            2,
            [new AssignStatementNode("Y", new FunctionCallNode(Days, [], 3), 3)],
            []);
        FieldEventResponse evaluatorResponse = Evaluator.Run(form, [block], TextValues());

        Diagnostic[] errors = [.. engineResponse.Diagnostics, .. evaluatorResponse.Diagnostics];
        Assert.Equal(2, errors.Length);
        Assert.All(errors, diagnostic =>
        {
            Assert.Equal(
                (DiagnosticSeverity.Error, Parser.Commands.Assign, 3),
                (diagnostic.Severity, diagnostic.Command, diagnostic.Line));
            Assert.False(Parser.ReportsUnsupportedFunction(diagnostic));
        });
    }

    [Fact]
    public void ReportsUnsupportedFunctionRejectsNull() =>
        Assert.Throws<ArgumentNullException>("diagnostic", () => Parser.ReportsUnsupportedFunction(null!));

    // Every construct the parser reports, other than an unsupported function, with none whose name is program text:
    // top-level and field-level junk, misplaced and unterminated blocks, unsupported commands, statements, operators,
    // literals, references and keyword-named functions, malformed supported statements and calls, and GEOCODE.
    private const string VocabularyProgram = """
        junk
        Field X
        	HIDE A
        	Before
        		GOTO Z
        		HIDE A B
        		ALWAYS
        			ASSIGN Y = "1"
        		END
        		SET-REQUIRED A
        		A = 1
        		ASSIGN Y = SYSTEMDATE
        		ASSIGN Y = FORMAT(A)
        		ASSIGN Y = 1 + 2
        		ASSIGN Y = -A
        		ASSIGN Y = A & B
        		ASSIGN Y = A * B
        		ASSIGN Y = A / B
        		ASSIGN Y = A ^ B
        		ASSIGN Y = A % B
        		ASSIGN Y = A MOD B
        		ASSIGN Y = A!B
        		ASSIGN Y = 12L
        		ASSIGN Y = #1a
        		ASSIGN Y = 'x'
        		ASSIGN Y = 01/15/2011
        		ASSIGN Y = 10:30
        		ASSIGN Y = (A, B)
        		ASSIGN Y = Other.Name
        		ASSIGN Y = Grid[1, 2]
        		ASSIGN Y = YEARS(A, B
        		ASSIGN Y =
        		ENABLE * EXCEPT A
        		DISABLE * EXCEPT A
        		CLEAR
        		DIALOG B
        		DIALOG "m" B YN
        		GEOCODE A, B, C
        		GEOCODE
        		IF A <> "1" THEN ASSIGN Y = "1" END-IF
        		IF A < 1 THEN ASSIGN Y = "1" END-IF
        		IF A > 1 THEN ASSIGN Y = "1" END-IF
        		IF A <= 1 THEN ASSIGN Y = "1" END-IF
        		IF A >= 1 THEN ASSIGN Y = "1" END-IF
        		IF A = "1" OR B = "2" THEN ASSIGN Y = "1" END-IF
        		IF A = "1" XOR B = "2" THEN ASSIGN Y = "1" END-IF
        		IF A LIKE "x" THEN ASSIGN Y = "1" END-IF
        		IF A = (+) THEN ASSIGN Y = "1" END-IF
        		IF A = "1" THEN ASSIGN Y = "1" ELSE-IF B = "2" THEN ASSIGN Y = "2" END-IF
        		IF A = "1" THEN ASSIGN Y = "1" ELSE ASSIGN Y = "2" ELSE ASSIGN Y = "3" END-IF
        		IF THEN ASSIGN Y = "1" END-IF
        		END-IF
        	End-Before
        	After
        	End-After
        	Before
        	End-Before
        	End-Click
        End-Field
        View
        End-View
        Field
        End-Field
        Field Y
        	After
        		IF A = "1" THEN
        			ASSIGN Y = "1"
        End-Field
        """;

    // Every command reported for the program, for every truncation of its tokens, which leaves each construct open at
    // each point, and for the nesting ceiling and an unterminated comment, is vocabulary; so is every runtime error's.
    [Fact]
    public void EveryCommandReportedWithoutAnUnsupportedFunctionIsVocabulary()
    {
        IReadOnlyList<Token> tokens = Lexer.Tokenize(VocabularyProgram);
        var reported = new SortedSet<string>(StringComparer.Ordinal);
        for (int length = 0; length <= tokens.Count; length++)
        {
            Token[] truncated = [.. tokens.Take(length)];
            reported.UnionWith(Parser.Parse(truncated, "F").Diagnostics.Select(diagnostic => diagnostic.Command));
        }

        string nested = new string('(', Parser.MaxNestingDepth + 1) + "1" + new string(')', Parser.MaxNestingDepth + 1);
        foreach (string program in new[] { InAfterBlock("ASSIGN Y = " + nested), InAfterBlock("/* open") })
        {
            reported.UnionWith(Parse(program).Diagnostics.Select(diagnostic => diagnostic.Command));
        }

        ICheckCodeEngine engine = new CheckCodeEngine();
        FieldEventResponse response = engine.Run(
            InAfterBlock("ASSIGN Unknown = \"1\"", "ENABLE Unknown", "IF Unknown = \"1\" THEN", "END-IF"),
            TextForm("X", "A", "B", "Y", "Z"),
            FieldEventKind.FieldAfter,
            "X",
            TextValues(("A", "1")));
        Assert.Contains(response.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        reported.UnionWith(response.Diagnostics.Select(diagnostic => diagnostic.Command));

        Assert.All(reported, command => Assert.True(Parser.IsVocabularyCommand(command), $"'{command}' is reported."));
        Assert.Superset(
            new SortedSet<string>(StringComparer.Ordinal)
            {
                Goto, Hide, Always, SystemDate, "FORMAT", "SET-REQUIRED", "BEFORE", "END-CLICK", "END-AFTER",
                "VIEW", Parser.Commands.Syntax, Parser.Commands.Nesting, Parser.Commands.ImplicitAssign,
                Parser.Commands.Plus, Parser.Commands.Minus, Parser.Commands.Concatenate, Parser.Commands.LibraryCall,
                Parser.Commands.NumberLiteral, Parser.Commands.CharLiteral, Parser.Commands.DateLiteral,
                Parser.Commands.TimeLiteral, Parser.Commands.CheckedLiteral, Parser.Commands.ExpressionList,
                Parser.Commands.QualifiedName, Parser.Commands.GridField, Parser.Commands.EnableExcept,
                Parser.Commands.DisableExcept, Parser.Commands.NotEqual, Parser.Commands.Or, Parser.Commands.Xor,
                Parser.Commands.Like, Parser.Commands.ElseIf, Parser.Commands.Else, Parser.Commands.EndIf,
                Parser.Commands.Field, Parser.Commands.EndField, Parser.Commands.If, Parser.Commands.Assign,
                Parser.Commands.Enable, Parser.Commands.Clear, Parser.Commands.Dialog, Parser.Commands.Geocode,
                FunctionNames.Years,
            },
            reported);
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
    // path that handles a missing terminator. No cut may report an error, point past the last token it kept, or keep
    // the outer IF (line 3) before its END-IF (line 11), because an unterminated IF never runs.
    [Fact]
    public void EveryTruncationOfTheNestedProgramParsesWithoutThrowing()
    {
        IReadOnlyList<Token> tokens = Lexer.Tokenize(NestedRecoveryProgram);

        // The inner IF closes with END, so the only END-IF is the outer one.
        int outerEndIf = Enumerable.Range(0, tokens.Count).Single(i => tokens[i].IsKeyword(Parser.Commands.EndIf));
        Assert.Equal(11, tokens[outerEndIf].Line);

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

            int lastLine = end.Line;
            IEnumerable<EventBlockNode> events = program.FieldBlocks.SelectMany(block => block.Events);
            Assert.All(
                program.Diagnostics.Concat(events.SelectMany(eventBlock => eventBlock.Diagnostics)),
                diagnostic =>
                {
                    Assert.Contains(diagnostic.Severity, new[] { DiagnosticSeverity.Warning, DiagnosticSeverity.Info });
                    Assert.InRange(diagnostic.Line, 1, lastLine);
                });
            Assert.All(
                events.SelectMany(eventBlock => AllStatements(eventBlock.Statements)),
                statement => Assert.InRange(statement.Line, 1, lastLine));
            if (length <= outerEndIf)
            {
                Assert.All(
                    events,
                    eventBlock => Assert.DoesNotContain(
                        eventBlock.Statements,
                        statement => statement is IfStatementNode));
            }
        }

        static IEnumerable<StatementNode> AllStatements(IEnumerable<StatementNode> statements)
        {
            foreach (StatementNode statement in statements)
            {
                yield return statement;
                if (statement is IfStatementNode ifStatement)
                {
                    foreach (StatementNode nested in AllStatements(ifStatement.Then.Concat(ifStatement.Else ?? [])))
                    {
                        yield return nested;
                    }
                }
            }
        }
    }

    // A '/*' without its '*/' (lines 50-51) hides the rest of the program, here a whole Field block, so the one
    // warning is reported where the comment opens and the hidden block never runs.
    [Fact]
    public void UnterminatedBlockCommentAtTheStartIsReportedOnceAndHidesTheProgram()
    {
        ICheckCodeEngine engine = new CheckCodeEngine();
        FormMetadata form = TextForm("X", "Y");
        string text = "/* never closed\n" + InAfterBlock("ASSIGN Y = \"1\"");

        Assert.Equal(new[] { Warning(Parser.Commands.Syntax, 1) }, Summaries(engine.Analyze(text, form)));

        FieldEventResponse response = engine.Run(text, form, FieldEventKind.FieldAfter, "X", TextValues());
        Assert.Empty(response.Enabled);
        Assert.Empty(response.Disabled);
        Assert.Empty(response.Cleared);
        Assert.Empty(response.Assigned);
        Assert.Empty(response.Dialogs);
    }

    // The comment opening on line 4 swallows End-After and End-Field; it is reported at its opening, and the ASSIGN
    // before it still runs.
    [Fact]
    public void UnterminatedBlockCommentInABlockIsReportedAndTheStatementsBeforeItRun()
    {
        ICheckCodeEngine engine = new CheckCodeEngine();
        FormMetadata form = TextForm("X", "Y");
        string text = InAfterBlock("ASSIGN Y = \"1\"", "/* never closed");

        Assert.Equal(new[] { Warning(Parser.Commands.Syntax, 4) }, Summaries(engine.Analyze(text, form)));

        FieldEventResponse response = engine.Run(text, form, FieldEventKind.FieldAfter, "X", TextValues());
        Assert.Equal("1", response.Assigned["Y"].AsText());
    }

    // The comment on line 4 hides End-After and End-Field. The complete statement on line 3 still runs alone, and the
    // comment is the one warning, at its own line, in place of the terminators it hides.
    [Theory]
    [InlineData("DIALOG \"kept\"", "dialogs: kept")]
    [InlineData("ENABLE Y", "enabled: Y")]
    [InlineData("DISABLE Y", "disabled: Y")]
    [InlineData("CLEAR Y", "cleared: Y")]
    [InlineData("ASSIGN Y = \"1\"", "assigned: Y=1")]
    public void StatementBeforeAnUnterminatedBlockCommentRunsAndTheCommentIsTheOneWarning(
        string statement,
        string effect)
    {
        ICheckCodeEngine engine = new CheckCodeEngine();
        FormMetadata form = TextForm("X", "Y");
        string text = InAfterBlock(statement, "/* never closed");

        Assert.Equal(new[] { Warning(Parser.Commands.Syntax, 4) }, Summaries(engine.Analyze(text, form)));

        FieldEventResponse response = engine.Run(text, form, FieldEventKind.FieldAfter, "X", TextValues());
        Assert.Equal(new[] { effect }, Effects(response));
        Assert.Equal(new[] { Warning(Parser.Commands.Syntax, 4) }, Summaries(response.Diagnostics));
    }

    // The comment on line 5 hides the END-IF of the IF on line 3, so that IF stays open and is dropped, and its ASSIGN
    // never runs. The comment is the one warning: no IF, END-AFTER or END-FIELD is reported missing.
    [Fact]
    public void IfLeftOpenByAnUnterminatedBlockCommentIsDroppedAndOnlyTheCommentIsReported()
    {
        ICheckCodeEngine engine = new CheckCodeEngine();
        FormMetadata form = TextForm("X", "Y");
        string text = InAfterBlock("IF X = \"1\" THEN", "ASSIGN Y = \"1\"", "/* never closed");

        Assert.Equal(new[] { Warning(Parser.Commands.Syntax, 5) }, Summaries(engine.Analyze(text, form)));

        FieldEventResponse response = engine.Run(
            text,
            form,
            FieldEventKind.FieldAfter,
            "X",
            TextValues(("X", "1")));
        Assert.Empty(Effects(response));
        Assert.Equal(new[] { Warning(Parser.Commands.Syntax, 5) }, Summaries(response.Diagnostics));
    }

    // Outside an event block the comment is still the one warning, at its own line: on line 5 it hides End-Field, and
    // on line 6 it follows the closed Field block. Either way the After block ran to its End-After and reports nothing.
    [Theory]
    [InlineData("\tEnd-After\n/* never closed\nEnd-Field", 5)]
    [InlineData("\tEnd-After\nEnd-Field\n/* never closed", 6)]
    public void UnterminatedBlockCommentAfterAnEventBlockIsTheOneWarningAndTheBlockRuns(string tail, int commentLine)
    {
        ICheckCodeEngine engine = new CheckCodeEngine();
        FormMetadata form = TextForm("X", "Y");
        string text = "Field X\n\tAfter\n\t\tASSIGN Y = \"1\"\n" + tail;

        Assert.Equal(new[] { Warning(Parser.Commands.Syntax, commentLine) }, Summaries(engine.Analyze(text, form)));

        FieldEventResponse response = engine.Run(text, form, FieldEventKind.FieldAfter, "X", TextValues());
        Assert.Equal(new[] { "assigned: Y=1" }, Effects(response));
        Assert.Empty(response.Diagnostics);
    }

    // U+0001 is outside {String Chars} (line 10), so the quoted text is no String (line 24) and the DIALOG on line 3
    // is malformed; recovery drops that statement alone.
    [Fact]
    public void DialogWithACharacterOutsideTheStringSetIsDroppedAndTheNextStatementRuns()
    {
        ICheckCodeEngine engine = new CheckCodeEngine();
        string text = InAfterBlock("DIALOG \"a\u0001b\"", "ASSIGN Y = \"kept\"");

        FieldEventResponse response = engine.Run(
            text,
            TextForm("X", "Y"),
            FieldEventKind.FieldAfter,
            "X",
            TextValues());

        Assert.Empty(response.Dialogs);
        Assert.Equal("kept", response.Assigned["Y"].AsText());
        Assert.Contains(Warning(Parser.Commands.Dialog, 3), Summaries(response.Diagnostics));
    }

    // Line 30 puts an ID head straight after '[', so "[ Y ]" names no field and the ASSIGN on line 3 is malformed;
    // recovery drops that statement alone.
    [Fact]
    public void AssignToABracketPaddedNameIsDroppedAndTheNextStatementRuns()
    {
        ICheckCodeEngine engine = new CheckCodeEngine();
        string text = InAfterBlock("ASSIGN [ Y ] = \"1\"", "ASSIGN Z = \"2\"");

        FieldEventResponse response = engine.Run(
            text,
            TextForm("X", "Y", "Z"),
            FieldEventKind.FieldAfter,
            "X",
            TextValues());

        Assert.Equal(new[] { "Z" }, response.Assigned.Keys);
        Assert.Equal("2", response.Assigned["Z"].AsText());
        Assert.Contains(
            response.Diagnostics,
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning && diagnostic.Line == 3);
    }

    // "[ END ]" is one Unknown token, so its END cannot close the IF on line 3: recovery drops only the ASSIGN on
    // line 4, and the ASSIGN on line 5 stays inside the IF. Unset, X fails the condition, so nothing is assigned.
    [Fact]
    public void PaddedNameHoldingEndKeepsTheEnclosingIfWhenItsConditionFails()
    {
        ICheckCodeEngine engine = new CheckCodeEngine();

        FieldEventResponse response = engine.Run(
            PaddedEndProgram,
            TextForm("X", "Z"),
            FieldEventKind.FieldAfter,
            "X",
            TextValues());

        Assert.Empty(response.Assigned);
        Assert.Contains(Warning(Parser.Commands.Assign, 4), Summaries(response.Diagnostics));
    }

    // The same program with the condition holding runs the kept ASSIGN alone.
    [Fact]
    public void PaddedNameHoldingEndKeepsTheEnclosingIfWhenItsConditionHolds()
    {
        ICheckCodeEngine engine = new CheckCodeEngine();

        FieldEventResponse response = engine.Run(
            PaddedEndProgram,
            TextForm("X", "Z"),
            FieldEventKind.FieldAfter,
            "X",
            TextValues(("X", "1")));

        Assert.Equal(new[] { "Z" }, response.Assigned.Keys);
        Assert.Equal("conditional", response.Assigned["Z"].AsText());
    }

    // "[ IF ]" is one Unknown token, so its IF opens no statement that would swallow the ASSIGN on line 4.
    [Fact]
    public void PaddedNameHoldingIfIsDroppedAndTheNextStatementRuns()
    {
        ICheckCodeEngine engine = new CheckCodeEngine();
        string text = InAfterBlock("ASSIGN [ IF ] = \"1\"", "ASSIGN Z = \"kept\"");

        FieldEventResponse response = engine.Run(
            text,
            TextForm("X", "Z"),
            FieldEventKind.FieldAfter,
            "X",
            TextValues());

        Assert.Equal(new[] { "Z" }, response.Assigned.Keys);
        Assert.Equal("kept", response.Assigned["Z"].AsText());
        Assert.Contains(Warning(Parser.Commands.Assign, 3), Summaries(response.Diagnostics));
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

    // Each non-empty effect list of a response as "<list>: <entries>", so one comparison also pins the empty lists.
    private static string[] Effects(FieldEventResponse response)
    {
        (string List, IEnumerable<string> Entries)[] lists =
        [
            ("enabled", response.Enabled),
            ("disabled", response.Disabled),
            ("cleared", response.Cleared),
            ("assigned", response.Assigned.Select(pair => $"{pair.Key}={pair.Value.AsText()}")),
            ("dialogs", response.Dialogs.Select(dialog => dialog.Message)),
        ];

        return
        [
            .. lists
                .Where(list => list.Entries.Any())
                .Select(list => $"{list.List}: {string.Join(", ", list.Entries)}"),
        ];
    }

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
