using System.Globalization;
using EpiInfo.CheckCode.Lexing;
using EpiInfo.CheckCode.Parsing;
using EpiInfo.Domain.Diagnostics;
using EpiInfo.Domain.Events;
using EpiInfo.Domain.Metadata;
using EpiInfo.Domain.Values;
using FieldSpec = (string Field, int TypeId, string TypeName, string? SqlType, int Bytes);

namespace EpiInfo.CheckCode.Tests;

/// <summary>
/// The diagnostic contract of <see cref="ICheckCodeEngine"/>: a construct outside the supported subset is a
/// <see cref="DiagnosticSeverity.Warning"/> naming its form, line and command while the rest of the program still
/// runs; a <c>GEOCODE</c> statement is an <see cref="DiagnosticSeverity.Info"/>; <see cref="ICheckCodeEngine.Analyze"/>
/// reports only what the program text reveals; and <see cref="ICheckCodeEngine.Run"/> reports the executed block's
/// static diagnostics plus its runtime errors, in source order. Both stay within the engine's budgets: a program past
/// <see cref="CheckCodeEngine.MaxProgramLength"/> or <see cref="CheckCodeEngine.MaxTokens"/> is one <c>LIMIT</c>
/// warning, and no call returns more than <see cref="CheckCodeEngine.MaxDiagnostics"/> diagnostics.
/// </summary>
/// <remarks>
/// Programs are inline raw string literals whose line 1 is the literal's first line, and each test that asserts a
/// line first checks the program text on that line, so the literal's indentation trimming cannot shift the numbering
/// unnoticed. Messages are never compared with literal text: only that they are present and carry no field value, and
/// that a <c>LIMIT</c> warning's message carries its budget or its count.
/// </remarks>
public sealed class DiagnosticsTests
{
    // Seeded metaFieldTypes ids and names (testdata/sql/02_reference_data.sql).
    private const int TextTypeId = 1;
    private const int NumberTypeId = 5;
    private const int DateTypeId = 7;
    private const int CheckboxTypeId = 10;
    private const int CommandButtonTypeId = 13;
    private const int CommentLegalTypeId = 19;

    private const string ValueSentinel = "SENTINEL-7f3a";

    private static readonly FormMetadata SyntheticForm = Form(
        "SyntheticForm",
        TextField("X"),
        TextField("Y"),
        TextField("Z"),
        TextField("A"));

    // Every field the seeded FoodHistory program names; OnsetWeek is read-only, as seeded.
    private static readonly FormMetadata FoodHistoryForm = WithReadOnly(
        Form(
            "FoodHistory",
            DateField("DOB"),
            DateField("DateofInterview"),
            DateField("OnsetDate"),
            NumberField("Age"),
            NumberField("OnsetWeek"),
            NumberField("FeverTemp"),
            NumberField("Latitude"),
            NumberField("Longitude"),
            CommandButtonField("GetCoordinates"),
            TextField("PatientAddress"),
            CheckboxField("Fever"),
            CheckboxField("Asian"),
            CheckboxField("Black"),
            CheckboxField("AmericanIndianAlaskanNative"),
            CheckboxField("NativeHawaiianOtherPacificIslander"),
            CheckboxField("White"),
            CheckboxField("Multiracial"),
            CheckboxField("UnknownOther")),
        "OnsetWeek");

    private static readonly FormMetadata CaseReportForm = Form(
        "CaseReport",
        TextField("LastName"),
        CommentLegalField("Sex"),
        CommentLegalField("Pregnant"));

    private readonly ICheckCodeEngine engine = new CheckCodeEngine();

    [Fact]
    public void UnsupportedStatement_YieldsWarning_AndLaterAssignStillApplies()
    {
        string program = UnsupportedStatementProgram("GOTO Y");
        AssertProgramLine(program, 3, "GOTO Y");

        FieldEventResponse response = engine.Run(program, SyntheticForm, FieldEventKind.FieldAfter, "X", Values());

        AssertOnlyAssigned(response, "Z", FieldValue.FromText("done"));
        AssertDiagnostic(Assert.Single(response.Diagnostics), DiagnosticSeverity.Warning, SyntheticForm, 3, "GOTO");

        Diagnostic analyzed = Assert.Single(engine.Analyze(program, SyntheticForm));
        AssertDiagnostic(analyzed, DiagnosticSeverity.Warning, SyntheticForm, 3, "GOTO");
    }

    [Theory]
    [InlineData("GOTO Y", "GOTO")]
    [InlineData("GOTO 2", "GOTO")]
    [InlineData("HIDE Y", "HIDE")]
    [InlineData("SET-REQUIRED Y", "SET-REQUIRED")]
    public void UnsupportedStatementKinds_AreReportedByKeyword(string statement, string command)
    {
        string program = UnsupportedStatementProgram(statement);
        AssertProgramLine(program, 3, statement);

        FieldEventResponse response = engine.Run(program, SyntheticForm, FieldEventKind.FieldAfter, "X", Values());

        AssertDiagnostic(Assert.Single(response.Diagnostics), DiagnosticSeverity.Warning, SyntheticForm, 3, command);
        AssertOnlyAssigned(response, "Z", FieldValue.FromText("done"));
        AssertDiagnostic(
            Assert.Single(engine.Analyze(program, SyntheticForm)),
            DiagnosticSeverity.Warning,
            SyntheticForm,
            3,
            command);
    }

    [Fact]
    public void Geocode_IsInfo_FromAnalyzeAndRun()
    {
        const string program = """
            Field GetCoordinates
            	Click
            		GEOCODE PatientAddress, Latitude, Longitude
            	End-Click
            End-Field
            """;
        AssertProgramLine(program, 3, "GEOCODE PatientAddress, Latitude, Longitude");

        Diagnostic analyzed = Assert.Single(engine.Analyze(program, FoodHistoryForm));
        AssertDiagnostic(analyzed, DiagnosticSeverity.Info, FoodHistoryForm, 3, "GEOCODE");

        // Scenario EC-GEO-01: the statement is skipped, so the click changes nothing and reports only the Info.
        FieldEventResponse response = engine.Run(
            program,
            FoodHistoryForm,
            FieldEventKind.FieldClick,
            "GetCoordinates",
            Values(("PatientAddress", FieldValue.FromText("1 Example St"))));

        AssertNoEffects(response);
        AssertDiagnostic(Assert.Single(response.Diagnostics), DiagnosticSeverity.Info, FoodHistoryForm, 3, "GEOCODE");
    }

    [Fact]
    public void Analyze_NeverReturnsRuntimeErrors()
    {
        const string program = """
            Field OnsetDate
            	After
            		ASSIGN OnsetWeek = EPIWEEK(OnsetDate)
            		ASSIGN NoSuchField = 1
            	End-After
            End-Field
            """;
        AssertProgramLine(program, 3, "ASSIGN OnsetWeek = EPIWEEK(OnsetDate)");
        AssertProgramLine(program, 4, "ASSIGN NoSuchField = 1");

        // Every construct is in the subset, so the text alone reveals nothing: no warning and never an error.
        Assert.Empty(engine.Analyze(program, FoodHistoryForm));

        FieldEventResponse response = engine.Run(
            program,
            FoodHistoryForm,
            FieldEventKind.FieldAfter,
            "OnsetDate",
            Values(("OnsetDate", FieldValue.Null)));

        // The missing EPIWEEK date and the unknown field each fail only when the block runs.
        Assert.Collection(
            response.Diagnostics,
            missingDate => AssertDiagnostic(missingDate, DiagnosticSeverity.Error, FoodHistoryForm, 3, "ASSIGN"),
            unknownField => AssertDiagnostic(unknownField, DiagnosticSeverity.Error, FoodHistoryForm, 4, "ASSIGN"));
        AssertNoEffects(response);
    }

    [Fact]
    public void RuntimeErrorMessage_ContainsNoFieldValue()
    {
        // Age is a number and PatientAddress holds text, so the ASSIGN fails on a type mismatch at runtime.
        const string program = """
            Field PatientAddress
            	After
            		ASSIGN Age = PatientAddress
            	End-After
            End-Field
            """;
        AssertProgramLine(program, 3, "ASSIGN Age = PatientAddress");

        FieldEventResponse response = engine.Run(
            program,
            FoodHistoryForm,
            FieldEventKind.FieldAfter,
            "PatientAddress",
            Values(("PatientAddress", FieldValue.FromText(ValueSentinel))));

        // The field-event response returns each diagnostic to the client, so its message and command may name fields
        // and constructs but never carry a field value.
        Diagnostic error = Assert.Single(response.Diagnostics);
        AssertDiagnostic(error, DiagnosticSeverity.Error, FoodHistoryForm, 3, "ASSIGN");
        Assert.DoesNotContain(ValueSentinel, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ValueSentinel, error.Command, StringComparison.Ordinal);
        AssertNoEffects(response);
    }

    [Fact]
    public void Run_ReturnsOnlyTheExecutedBlocksDiagnostics()
    {
        const string program = """
            View
            	Before
            		DIALOG "hi"
            	End-Before
            End-View

            Field A
            	Click
            		GEOCODE X, Y, Z
            	End-Click
            End-Field

            Field X
            	After
            		GOTO Y
            		ASSIGN Z = "done"
            	End-After
            End-Field
            """;
        AssertProgramLine(program, 1, "View");
        AssertProgramLine(program, 9, "GEOCODE X, Y, Z");
        AssertProgramLine(program, 15, "GOTO Y");

        Assert.Collection(
            engine.Analyze(program, SyntheticForm),
            view => AssertDiagnostic(view, DiagnosticSeverity.Warning, SyntheticForm, 1, "VIEW"),
            geocode => AssertDiagnostic(geocode, DiagnosticSeverity.Info, SyntheticForm, 9, "GEOCODE"),
            unsupported => AssertDiagnostic(unsupported, DiagnosticSeverity.Warning, SyntheticForm, 15, "GOTO"));

        FieldEventResponse after = engine.Run(program, SyntheticForm, FieldEventKind.FieldAfter, "X", Values());
        AssertDiagnostic(Assert.Single(after.Diagnostics), DiagnosticSeverity.Warning, SyntheticForm, 15, "GOTO");
        AssertOnlyAssigned(after, "Z", FieldValue.FromText("done"));

        FieldEventResponse click = engine.Run(program, SyntheticForm, FieldEventKind.FieldClick, "A", Values());
        AssertDiagnostic(Assert.Single(click.Diagnostics), DiagnosticSeverity.Info, SyntheticForm, 9, "GEOCODE");
        AssertNoEffects(click);

        // A field with no block for the event executes nothing, so none of the program's diagnostics reach it, and
        // the skipped View block's DIALOG never runs.
        FieldEventResponse before = engine.Run(program, SyntheticForm, FieldEventKind.FieldBefore, "X", Values());
        Assert.Empty(before.Diagnostics);
        AssertNoEffects(before);
    }

    // A Field block answers its field whatever the letter case of either spelling, brackets included.
    [Theory]
    [InlineData("x", "X")]
    [InlineData("X", "x")]
    [InlineData("[x]", "X")]
    [InlineData("[X]", "x")]
    public void Run_MatchesTheFieldBlockToTheEventFieldWithoutRegardToCase(string programName, string requestName)
    {
        string program = $"""
            Field {programName}
            	After
            		GOTO Y
            		ASSIGN Z = "done"
            	End-After
            End-Field
            """;
        AssertProgramLine(program, 1, "Field " + programName);
        AssertProgramLine(program, 3, "GOTO Y");

        FieldEventResponse response = engine.Run(
            program,
            SyntheticForm,
            FieldEventKind.FieldAfter,
            requestName,
            Values());

        AssertOnlyAssigned(response, "Z", FieldValue.FromText("done"));
        AssertDiagnostic(Assert.Single(response.Diagnostics), DiagnosticSeverity.Warning, SyntheticForm, 3, "GOTO");
    }

    // Legacy registration let the last Field block of a name replace every section of an earlier one, so only the
    // second declaration runs, and an event it lacks finds nothing rather than the first declaration's section.
    [Fact]
    public void Run_UsesOnlyTheLastFieldBlockOfAName_WithNoFallBackToAnEarlierOne()
    {
        const string program = """
            Field X
            	Before
            		DIALOG "first-before"
            	End-Before
            	After
            		HIDE Y
            		ASSIGN Z = "first"
            	End-After
            End-Field

            Field x
            	After
            		GOTO Y
            		ASSIGN Z = "second"
            	End-After
            End-Field
            """;
        AssertProgramLine(program, 6, "HIDE Y");
        AssertProgramLine(program, 11, "Field x");
        AssertProgramLine(program, 13, "GOTO Y");

        // Both declarations are still analysed: the choice between them is made only when an event runs.
        Assert.Collection(
            engine.Analyze(program, SyntheticForm),
            hide => AssertDiagnostic(hide, DiagnosticSeverity.Warning, SyntheticForm, 6, "HIDE"),
            unsupported => AssertDiagnostic(unsupported, DiagnosticSeverity.Warning, SyntheticForm, 13, "GOTO"));

        FieldEventResponse after = engine.Run(program, SyntheticForm, FieldEventKind.FieldAfter, "X", Values());
        AssertOnlyAssigned(after, "Z", FieldValue.FromText("second"));
        AssertDiagnostic(Assert.Single(after.Diagnostics), DiagnosticSeverity.Warning, SyntheticForm, 13, "GOTO");

        FieldEventResponse before = engine.Run(program, SyntheticForm, FieldEventKind.FieldBefore, "X", Values());
        AssertNoEffects(before);
        Assert.Empty(before.Diagnostics);
    }

    // The section on line 5 breaks the grammar's one Before, then After, then Click (lines 620-627 of
    // EpiInfo.Enter.Grammar.grm): a second After, or a Before after the After. It is skipped with one warning, so its
    // ASSIGN runs on no event, and the warning reaches Analyze but not the response of the block that does run.
    [Theory]
    [InlineData("After", "End-After", "AFTER")]
    [InlineData("Before", "End-Before", "BEFORE")]
    public void Run_NeverExecutesAnEventSectionTheGrammarDoesNotAllow(string keyword, string terminator, string command)
    {
        string program = $"""
            Field X
            	After
            		ASSIGN Z = "first"
            	End-After
            	{keyword}
            		ASSIGN Y = "second"
            	{terminator}
            End-Field
            """;
        AssertProgramLine(program, 2, "After");
        AssertProgramLine(program, 5, keyword);

        FieldEventResponse after = engine.Run(program, SyntheticForm, FieldEventKind.FieldAfter, "X", Values());
        AssertOnlyAssigned(after, "Z", FieldValue.FromText("first"));
        Assert.Empty(after.Diagnostics);

        FieldEventResponse before = engine.Run(program, SyntheticForm, FieldEventKind.FieldBefore, "X", Values());
        AssertNoEffects(before);
        Assert.Empty(before.Diagnostics);

        AssertDiagnostic(
            Assert.Single(engine.Analyze(program, SyntheticForm)),
            DiagnosticSeverity.Warning,
            SyntheticForm,
            5,
            command);
    }

    // The After on line 4 repeats the one on line 2, so it is skipped through its own End-After on line 10. The Click
    // on line 6 stands inside that section's IF, so it belongs to the skipped section: FieldClick runs only the Click
    // on line 11, and Analyze reports the skipped After alone.
    [Fact]
    public void Run_NeverExecutesAnEventSectionInsideASkippedOne()
    {
        const string program = """
            Field X
            	After
            	End-After
            	After
            		IF (.) THEN
            			Click
            				DIALOG "inside skipped section"
            			End-Click
            		END-IF
            	End-After
            	Click
            		DIALOG "valid sibling"
            	End-Click
            End-Field
            """;
        AssertProgramLine(program, 4, "After");
        AssertProgramLine(program, 6, "Click");
        AssertProgramLine(program, 10, "End-After");
        AssertProgramLine(program, 11, "Click");

        FieldEventResponse click = engine.Run(program, SyntheticForm, FieldEventKind.FieldClick, "X", Values());
        Assert.Equal(new[] { new DialogMessage("valid sibling", null) }, click.Dialogs);
        Assert.Empty(click.Enabled);
        Assert.Empty(click.Disabled);
        Assert.Empty(click.Cleared);
        Assert.Empty(click.Assigned);
        Assert.Empty(click.Diagnostics);

        AssertDiagnostic(
            Assert.Single(engine.Analyze(program, SyntheticForm)),
            DiagnosticSeverity.Warning,
            SyntheticForm,
            4,
            "AFTER");
    }

    [Fact]
    public void Diagnostics_AreInSourceOrder()
    {
        const string program = """
            Field X
            	After
            		HIDE Y
            		GOTO Y
            	End-After
            End-Field

            Field Z
            	After
            		ASSIGN NoSuchField = 1
            		SET-REQUIRED Y
            	End-After
            End-Field
            """;
        AssertProgramLine(program, 3, "HIDE Y");
        AssertProgramLine(program, 4, "GOTO Y");
        AssertProgramLine(program, 10, "ASSIGN NoSuchField = 1");
        AssertProgramLine(program, 11, "SET-REQUIRED Y");

        Assert.Collection(
            engine.Analyze(program, SyntheticForm),
            hide => AssertDiagnostic(hide, DiagnosticSeverity.Warning, SyntheticForm, 3, "HIDE"),
            unsupported => AssertDiagnostic(unsupported, DiagnosticSeverity.Warning, SyntheticForm, 4, "GOTO"),
            required => AssertDiagnostic(required, DiagnosticSeverity.Warning, SyntheticForm, 11, "SET-REQUIRED"));

        Assert.Collection(
            engine.Run(program, SyntheticForm, FieldEventKind.FieldAfter, "X", Values()).Diagnostics,
            hide => AssertDiagnostic(hide, DiagnosticSeverity.Warning, SyntheticForm, 3, "HIDE"),
            unsupported => AssertDiagnostic(unsupported, DiagnosticSeverity.Warning, SyntheticForm, 4, "GOTO"));

        // The static warning on line 11 follows the ASSIGN that fails at run time on line 10, so it is added after
        // that statement's runtime error and the two arrive in source order.
        Assert.Collection(
            engine.Run(program, SyntheticForm, FieldEventKind.FieldAfter, "Z", Values()).Diagnostics,
            error => AssertDiagnostic(error, DiagnosticSeverity.Error, SyntheticForm, 10, "ASSIGN"),
            required => AssertDiagnostic(required, DiagnosticSeverity.Warning, SyntheticForm, 11, "SET-REQUIRED"));
    }

    // Line 3 holds a statement that fails at run time (an unknown field) and a construct the parser skips, so one
    // diagnostic of each kind shares the line: in either order, at the top level, and inside an IF's condition, its
    // THEN branch and its ELSE branch. X is empty, so X = (.) holds and X = "1" does not. The diagnostics keep the
    // order their constructs have on the line, and the ASSIGN on line 4 still runs.
    [Theory]
    [InlineData(
        "ASSIGN NoSuchField = 1 HIDE Y",
        new[] { DiagnosticSeverity.Error, DiagnosticSeverity.Warning },
        new[] { "ASSIGN", "HIDE" })]
    [InlineData(
        "HIDE Y ASSIGN NoSuchField = 1",
        new[] { DiagnosticSeverity.Warning, DiagnosticSeverity.Error },
        new[] { "HIDE", "ASSIGN" })]
    [InlineData(
        "IF NoSuchField = 1 THEN GOTO Y END-IF",
        new[] { DiagnosticSeverity.Error, DiagnosticSeverity.Warning },
        new[] { "IF", "GOTO" })]
    [InlineData(
        "IF X = (.) THEN ASSIGN NoSuchField = 1 GOTO Y END-IF",
        new[] { DiagnosticSeverity.Error, DiagnosticSeverity.Warning },
        new[] { "ASSIGN", "GOTO" })]
    [InlineData(
        "IF X = \"1\" THEN ENABLE Y GOTO Y ELSE ASSIGN NoSuchField = 1 HIDE Y END-IF",
        new[] { DiagnosticSeverity.Warning, DiagnosticSeverity.Error, DiagnosticSeverity.Warning },
        new[] { "GOTO", "ASSIGN", "HIDE" })]
    public void Run_StaticAndRuntimeDiagnosticsOnOneLine_AreInSourceOrder(
        string statements,
        DiagnosticSeverity[] severities,
        string[] commands)
    {
        string program = UnsupportedStatementProgram(statements);
        AssertProgramLine(program, 3, statements);

        FieldEventResponse response = engine.Run(program, SyntheticForm, FieldEventKind.FieldAfter, "X", Values());

        Assert.Equal(commands, response.Diagnostics.Select(diagnostic => diagnostic.Command));
        for (int i = 0; i < commands.Length; i++)
        {
            AssertDiagnostic(response.Diagnostics[i], severities[i], SyntheticForm, 3, commands[i]);
        }

        AssertOnlyAssigned(response, "Z", FieldValue.FromText("done"));
    }

    [Fact]
    public void SeededPrograms_AnalyzeAsExpected()
    {
        Assert.Empty(engine.Analyze(CaseReportProgram, CaseReportForm));

        // Assumption A4: of the seeded check code, only the GEOCODE of scenario EC-GEO-01 yields a diagnostic.
        AssertProgramLine(FoodHistoryProgram, 1, "Field DOB");
        AssertProgramLine(FoodHistoryProgram, 12, "GEOCODE PatientAddress, Latitude, Longitude");
        Diagnostic geocode = Assert.Single(engine.Analyze(FoodHistoryProgram, FoodHistoryForm));
        AssertDiagnostic(geocode, DiagnosticSeverity.Info, FoodHistoryForm, 12, "GEOCODE");
    }

    // A program exactly at the length or the token budget is parsed, and its block on X runs. The padding after the
    // block is a comment, which adds no token, or bare names outside every block, which add one warning but nothing
    // to X's block.
    [Theory]
    [InlineData(nameof(CheckCodeEngine.MaxProgramLength))]
    [InlineData(nameof(CheckCodeEngine.MaxTokens))]
    public void ProgramAtABudget_IsParsedAndRuns(string budget)
    {
        string program = BudgetProgram(budget, overBy: 0);

        Assert.DoesNotContain(
            engine.Analyze(program, SyntheticForm),
            diagnostic => diagnostic.Command == Parser.Commands.Limit);

        FieldEventResponse response = engine.Run(program, SyntheticForm, FieldEventKind.FieldAfter, "X", Values());

        AssertOnlyAssigned(response, "Z", FieldValue.FromText("done"));
        Assert.Empty(response.Diagnostics);
    }

    // One code unit or one token past its budget, the same program is refused whole: one LIMIT warning at line 1 whose
    // message names the budget, from Analyze and from Run, and the block on X runs nothing.
    [Theory]
    [InlineData(nameof(CheckCodeEngine.MaxProgramLength), CheckCodeEngine.MaxProgramLength)]
    [InlineData(nameof(CheckCodeEngine.MaxTokens), CheckCodeEngine.MaxTokens)]
    public void ProgramPastABudget_IsOneLimitWarningAndRunsNothing(string budget, int limit)
    {
        string program = BudgetProgram(budget, overBy: 1);

        Diagnostic analyzed = Assert.Single(engine.Analyze(program, SyntheticForm));
        AssertDiagnostic(analyzed, DiagnosticSeverity.Warning, SyntheticForm, 1, Parser.Commands.Limit);
        Assert.Contains(limit.ToString(CultureInfo.InvariantCulture), analyzed.Message, StringComparison.Ordinal);

        FieldEventResponse response = engine.Run(program, SyntheticForm, FieldEventKind.FieldAfter, "X", Values());

        AssertNoEffects(response);
        Assert.Equal(analyzed, Assert.Single(response.Diagnostics));
    }

    // The length is tested before the blank test: a program past MaxProgramLength is refused although white space leads
    // it, as one cut by the store to its first MaxProgramLength + 1 code units can be white space only, or as white
    // space up to the limit can put the block on X past it. From Analyze and from Run it is one LIMIT warning at line 1
    // naming the budget, never an empty program, and the block runs nothing.
    [Theory]
    [InlineData(CheckCodeEngine.MaxProgramLength + 1, false)]
    [InlineData(CheckCodeEngine.MaxProgramLength, true)]
    public void ProgramPastTheLengthBudget_IsOneLimitWarningWhenWhiteSpaceLeadsIt(int whiteSpace, bool blockAfter)
    {
        string leading = string.Concat(Enumerable.Repeat(" \t\r\n", (whiteSpace / 4) + 1))[..whiteSpace];
        string program = blockAfter ? leading + UnsupportedStatementProgram("// the block under test") : leading;
        Assert.True(program.Length > CheckCodeEngine.MaxProgramLength);
        Assert.True(string.IsNullOrWhiteSpace(program[..CheckCodeEngine.MaxProgramLength]));
        Assert.Equal(blockAfter, !string.IsNullOrWhiteSpace(program));

        Diagnostic analyzed = Assert.Single(engine.Analyze(program, SyntheticForm));
        AssertDiagnostic(analyzed, DiagnosticSeverity.Warning, SyntheticForm, 1, Parser.Commands.Limit);
        Assert.Contains(
            CheckCodeEngine.MaxProgramLength.ToString(CultureInfo.InvariantCulture),
            analyzed.Message,
            StringComparison.Ordinal);

        FieldEventResponse response = engine.Run(program, SyntheticForm, FieldEventKind.FieldAfter, "X", Values());

        AssertNoEffects(response);
        Assert.Equal(analyzed, Assert.Single(response.Diagnostics));
    }

    // Exactly MaxDiagnostics warnings are all listed, from Analyze and from Run, with no summary.
    [Fact]
    public void DiagnosticsAtTheBudget_AreAllListed()
    {
        string program = ManyWarningsProgram(CheckCodeEngine.MaxDiagnostics);
        AssertProgramLine(program, 3, "GOTO Y");
        AssertProgramLine(program, 2 + CheckCodeEngine.MaxDiagnostics, "GOTO Y");

        IReadOnlyList<Diagnostic> analyzed = engine.Analyze(program, SyntheticForm);
        FieldEventResponse response = engine.Run(program, SyntheticForm, FieldEventKind.FieldAfter, "X", Values());

        foreach (IReadOnlyList<Diagnostic> diagnostics in new[] { analyzed, response.Diagnostics })
        {
            Assert.Equal(CheckCodeEngine.MaxDiagnostics, diagnostics.Count);
            for (int i = 0; i < diagnostics.Count; i++)
            {
                AssertDiagnostic(diagnostics[i], DiagnosticSeverity.Warning, SyntheticForm, 3 + i, "GOTO");
            }
        }

        AssertOnlyAssigned(response, "Z", FieldValue.FromText("done"));
    }

    // Past MaxDiagnostics, Analyze and Run each return exactly MaxDiagnostics entries: the first warnings in source
    // order, then one LIMIT warning at the line of the first warning left out, counting those left out. The block's
    // effects are kept unchanged.
    [Theory]
    [InlineData(CheckCodeEngine.MaxDiagnostics + 1)]
    [InlineData(5_000)]
    public void DiagnosticsPastTheBudget_EndInOneCountingLimitWarning(int warnings)
    {
        string program = ManyWarningsProgram(warnings);
        const int Listed = CheckCodeEngine.MaxDiagnostics - 1;
        string omitted = (warnings - Listed).ToString(CultureInfo.InvariantCulture);
        AssertProgramLine(program, 3, "GOTO Y");
        AssertProgramLine(program, 3 + Listed, "GOTO Y");

        IReadOnlyList<Diagnostic> analyzed = engine.Analyze(program, SyntheticForm);
        FieldEventResponse response = engine.Run(program, SyntheticForm, FieldEventKind.FieldAfter, "X", Values());

        foreach (IReadOnlyList<Diagnostic> diagnostics in new[] { analyzed, response.Diagnostics })
        {
            Assert.Equal(CheckCodeEngine.MaxDiagnostics, diagnostics.Count);
            for (int i = 0; i < Listed; i++)
            {
                AssertDiagnostic(diagnostics[i], DiagnosticSeverity.Warning, SyntheticForm, 3 + i, "GOTO");
            }

            Diagnostic summary = diagnostics[Listed];
            AssertDiagnostic(summary, DiagnosticSeverity.Warning, SyntheticForm, 3 + Listed, Parser.Commands.Limit);
            Assert.Contains(omitted, summary.Message, StringComparison.Ordinal);
        }

        AssertOnlyAssigned(response, "Z", FieldValue.FromText("done"));
    }

    // A cancelled call is no diagnostic: Analyze and Run stop with the cancellation itself on a non-empty program.
    [Fact]
    public void CancelledCall_ThrowsInsteadOfReporting()
    {
        string program = UnsupportedStatementProgram("GOTO Y");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => engine.Analyze(program, SyntheticForm, cancellation.Token));
        Assert.Throws<OperationCanceledException>(
            () => engine.Run(program, SyntheticForm, FieldEventKind.FieldAfter, "X", Values(), cancellation.Token));
    }

    // UnsupportedStatementProgram's block on X, which only assigns Z, padded to overBy past the budget: to
    // MaxProgramLength + overBy code units with a trailing comment, or to MaxTokens + overBy tokens with bare names.
    private static string BudgetProgram(string budget, int overBy)
    {
        string block = UnsupportedStatementProgram("// the block under test") + "\n";
        switch (budget)
        {
            case nameof(CheckCodeEngine.MaxProgramLength):
                int commentLength = CheckCodeEngine.MaxProgramLength + overBy - block.Length;
                string program = block + "//" + new string('x', commentLength - 2);
                Assert.Equal(CheckCodeEngine.MaxProgramLength + overBy, program.Length);
                return program;

            case nameof(CheckCodeEngine.MaxTokens):
                int nameCount = CheckCodeEngine.MaxTokens + overBy - (Lexer.Tokenize(block).Count - 1);
                return block + string.Concat(Enumerable.Repeat("a ", nameCount));

            default:
                throw new ArgumentOutOfRangeException(nameof(budget), budget, "Not a program budget.");
        }
    }

    // A block on X with one GOTO per line from line 3, each one warning, then the ASSIGN of Z, which still runs.
    private static string ManyWarningsProgram(int warnings) =>
        UnsupportedStatementProgram(string.Join("\n", Enumerable.Repeat("GOTO Y", warnings)));

    // Line 3 holds the statement under test; the ASSIGN on line 4 must still run after it is skipped.
    private static string UnsupportedStatementProgram(string statement) => $"""
        Field X
        	After
        		{statement}
        		ASSIGN Z = "done"
        	End-After
        End-Field
        """;

    private static FieldSpec TextField(string name) => (name, TextTypeId, "Text", "nvarchar", 8000);

    private static FieldSpec NumberField(string name) => (name, NumberTypeId, "Number", "float", 8);

    private static FieldSpec DateField(string name) => (name, DateTypeId, "Date", "datetime", 8);

    private static FieldSpec CheckboxField(string name) => (name, CheckboxTypeId, "Checkbox", "bit", 1);

    private static FieldSpec CommandButtonField(string name) =>
        (name, CommandButtonTypeId, "Command Button", null, 0);

    private static FieldSpec CommentLegalField(string name) =>
        (name, CommentLegalTypeId, "Comment Legal", "nvarchar", 510);

    /// <summary>
    /// A one-page form whose fields sit on that page in the given order; a field without a SQL type, such as a
    /// command button, has no page-table column.
    /// </summary>
    private static FormMetadata Form(string name, params FieldSpec[] fields)
    {
        var page = new PageMetadata(1, "Page 1", 0, name + "1");

        // The static fixtures share this list, so it is built read-only: unlike an array, no cast makes it writable.
        IReadOnlyList<FieldMetadata> metadata =
        [
            .. fields.Select((field, index) => new FieldMetadata
            {
                FieldId = index + 1,
                Name = field.Field,
                FieldTypeId = field.TypeId,
                FieldTypeName = field.TypeName,
                Prompt = field.Field,
                PageId = page.PageId,
                TabIndex = index + 1,
                Column = field.SqlType is null
                    ? null
                    : FieldTypeMapper.DescribeColumn(field.Field, field.SqlType, field.Bytes),
            }),
        ];

        return new FormMetadata(1, name, name, [page], metadata);
    }

    private static FormMetadata WithReadOnly(FormMetadata form, string fieldName) =>
        form with
        {
            Fields =
            [
                .. form.Fields.Select(field => field.Name == fieldName ? field with { IsReadOnly = true } : field),
            ],
        };

    private static IReadOnlyDictionary<string, FieldValue> Values(params (string Field, FieldValue Value)[] entries)
    {
        var values = new Dictionary<string, FieldValue>(StringComparer.OrdinalIgnoreCase);
        foreach ((string field, FieldValue value) in entries)
        {
            values.Add(field, value);
        }

        return values;
    }

    // The 1-based program line, trimmed, must be the given text: this pins the numbering the assertions rely on.
    private static void AssertProgramLine(string program, int line, string text)
    {
        string[] lines = program.Split('\n');
        Assert.InRange(line, 1, lines.Length);
        Assert.Equal(text, lines[line - 1].Trim());
    }

    private static void AssertDiagnostic(
        Diagnostic diagnostic,
        DiagnosticSeverity severity,
        FormMetadata form,
        int line,
        string command)
    {
        Assert.Equal(severity, diagnostic.Severity);
        Assert.Equal(form.Name, diagnostic.Form);
        Assert.Equal(line, diagnostic.Line);
        Assert.Equal(command, diagnostic.Command);
        Assert.False(string.IsNullOrWhiteSpace(diagnostic.Message));
    }

    private static void AssertNoEffects(FieldEventResponse response)
    {
        Assert.Empty(response.Enabled);
        Assert.Empty(response.Disabled);
        Assert.Empty(response.Cleared);
        Assert.Empty(response.Assigned);
        Assert.Empty(response.Dialogs);
    }

    private static void AssertOnlyAssigned(FieldEventResponse response, string field, FieldValue value)
    {
        Assert.Empty(response.Enabled);
        Assert.Empty(response.Disabled);
        Assert.Empty(response.Cleared);
        Assert.Empty(response.Dialogs);
        KeyValuePair<string, FieldValue> assigned = Assert.Single(response.Assigned);
        Assert.Equal(field, assigned.Key);
        Assert.Equal(value, assigned.Value);
    }

    // metaViews.CheckCode of CaseReport: testdata/sql/03_forms.sql lines 26-53 hold program lines 1-28. The copy is
    // byte for byte, tabs, comments and the closing line break included.
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

    // metaViews.CheckCode of FoodHistory: testdata/sql/03_forms.sql lines 55-161 hold program lines 1-107. The copy is
    // byte for byte, tabs, comments and the closing line break included, with the SQL '' escape of the DIALOG line
    // turned back into '.
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
