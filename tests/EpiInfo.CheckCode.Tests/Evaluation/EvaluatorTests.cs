using EpiInfo.CheckCode;
using EpiInfo.CheckCode.Evaluation;
using EpiInfo.CheckCode.Syntax;
using EpiInfo.Domain.Diagnostics;
using EpiInfo.Domain.Events;
using EpiInfo.Domain.Metadata;
using EpiInfo.Domain.Values;

namespace EpiInfo.CheckCode.Tests.Evaluation;

/// <summary>
/// Pins the tree-walking evaluator of the check-code subset against legacy Epi Info: the <c>=</c> rule and literal
/// values of <c>Rule_CompareExp</c> and <c>Rule_Value</c>, <c>NOT</c> and <c>AND</c>, <c>IF/ELSE</c>,
/// <c>ENABLE</c>, <c>DISABLE</c> and <c>CLEAR</c> with group expansion, typed <c>ASSIGN</c> with <c>YEARS</c> and
/// <c>EPIWEEK</c>, <c>DIALOG</c>, case-insensitive identifiers, runtime-error recovery, bounded stack use for long
/// chains and deep nesting, and statelessness.
/// </summary>
/// <remarks>
/// Scenario values from <c>testdata/scenarios/check_code_scenarios.json</c> are restated inline and the file is never
/// read, so this stays a pure unit test. The seeded programs are verbatim copies of <c>metaViews.CheckCode</c> in
/// <c>testdata/sql/03_forms.sql</c>, and the form metadata mirrors the seeded <c>metaFields</c> rows and page-table
/// columns of the fields those programs touch.
/// </remarks>
public sealed class EvaluatorTests
{
    private const string FeverDialogMessage =
        "Warning: A fever temperature has been entered, but 'fever' was not selected as a symptom.";

    // Far past the roughly 9,000 terms at which evaluation that recursed once per term overflowed a pool thread.
    private const int LongChainTerms = 100_000;

    // An explicit stack for the long-chain cases, so their outcome never depends on the host's default thread size.
    private const int SmallStackBytes = 1024 * 1024;

    // A verbatim copy of the seeded metaViews.CheckCode of CaseReport (ViewId 1), tabs and final newline included,
    // so its line numbers are the seeded ones [testdata/sql/03_forms.sql:26-54].
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

    // A verbatim copy of the seeded metaViews.CheckCode of FoodHistory (ViewId 2), tabs and final newline included,
    // so its line numbers are the seeded ones [testdata/sql/03_forms.sql:55-162]. The SQL literal doubles the
    // quotes of 'fever'; the program text holds single quotes.
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

    // Shared by the "later statements read earlier results" and statelessness cases: the ASSIGN changes the value
    // the IF then reads, and must never reach the caller's dictionary.
    private const string AssignSexThenTestItProgram = """
        Field Sex
            After
                ASSIGN Sex = "1"
                IF Sex = "1" THEN
                    ENABLE Pregnant
                END-IF
            End-After
        End-Field
        """;

    private static readonly FormMetadata CaseReportForm = new(
        ViewId: 1,
        Name: "CaseReport",
        BaseTableName: "CaseReport",
        Pages: [new PageMetadata(1, "Page 1", 0, "CaseReport1")],
        Fields:
        [
            F(6, "LastName", 1, "Text", 1, 2, "nvarchar", 8000),
            F(7, "FirstName", 1, "Text", 1, 3, "nvarchar", 8000),
            F(10, "City", 1, "Text", 1, 5, "nvarchar", 8000),
            F(16, "Age", 5, "Number", 1, 15, "float", 8),
            F(17, "Sex", 19, "Comment Legal", 1, 16, "nvarchar", 510),
            F(43, "Pregnant", 19, "Comment Legal", 1, 41, "nvarchar", 510),
        ]);

    private static readonly FormMetadata FoodHistoryForm = new(
        ViewId: 2,
        Name: "FoodHistory",
        BaseTableName: "FoodHistory",
        Pages: [new PageMetadata(2, "Page 1", 0, "FoodHistory2"), new PageMetadata(3, "Page 2", 1, "FoodHistory3")],
        Fields:
        [
            F(1005, "DateofInterview", 7, "Date", 2, 7, "datetime", 8),
            F(1008, "LastName", 1, "Text", 2, 11, "nvarchar", 8000),
            F(1009, "Sex", 17, "Legal Values", 2, 12, "nvarchar", 510),
            F(1010, "DOB", 7, "Date", 2, 13, "datetime", 8),
            F(1011, "Age", 5, "Number", 2, 14, "float", 8),
            F(1014, "White", 10, "Checkbox", 2, 17, "bit", 1),
            F(1015, "NativeHawaiianOtherPacificIslander", 10, "Checkbox", 2, 18, "bit", 1),
            F(1016, "UnknownOther", 10, "Checkbox", 2, 19, "bit", 1),
            F(1017, "Black", 10, "Checkbox", 2, 20, "bit", 1),
            F(1018, "AmericanIndianAlaskanNative", 10, "Checkbox", 2, 21, "bit", 1),
            F(1019, "Asian", 10, "Checkbox", 2, 22, "bit", 1),
            F(1020, "Multiracial", 10, "Checkbox", 2, 23, "bit", 1),
            F(1036, "OnsetDate", 7, "Date", 2, 34, "datetime", 8),
            F(1157, "OnsetWeek", 5, "Number", 2, 35, "float", 8, readOnly: true),
            F(1040, "Fever", 10, "Checkbox", 2, 38, "bit", 1),
            F(1145, "FeverTemp", 5, "Number", 2, 47, "float", 8),
        ]);

    // Synthetic: no seeded group is named by a seeded program, so group expansion needs its own form.
    private static readonly FormMetadata GroupForm = new(
        ViewId: 99,
        Name: "Synthetic",
        BaseTableName: "Synthetic",
        Pages: [new PageMetadata(1, "Page 1", 0, "Synthetic1")],
        Fields:
        [
            F(1, "Symptoms", 21, "Group", 1, 1, null, 0, list: "Fever,Chills"),
            F(2, "Fever", 10, "Checkbox", 1, 2, "bit", 1),
            F(3, "Chills", 10, "Checkbox", 1, 3, "bit", 1),
        ]);

    public static TheoryData<string, FieldValue, FieldValue, bool> EqualityCases => new()
    {
        // Rule_CompareExp.cs:73-76: both sides empty under "=" are equal; Util.IsEmpty counts "" as empty.
        { "both null", FieldValue.Null, FieldValue.Null, true },
        { "empty text equals null", Text(""), FieldValue.Null, true },

        // Rule_CompareExp.cs:81-90: exactly one empty side is never equal.
        { "null vs text", FieldValue.Null, Text("1"), false },
        { "text vs null", Text("1"), FieldValue.Null, false },
        { "unset checkbox vs (-)", FieldValue.Null, UncheckedLiteralValue(), false },

        // Rule_CompareExp.cs:224-227: two strings compare trimmed and ignoring case.
        { "trimmed, case-insensitive", Text(" abc "), Text("ABC"), true },
        { "different text", Text("abc"), Text("abd"), false },

        // Rule_CompareExp.cs:111-115: two numerics compare as doubles.
        { "numbers", N(101.2), N(101.2), true },

        // Rule_CompareExp.cs:123-175: numeric text against a number is parsed and compared as a double.
        { "number vs numeric string", N(25), Text("25"), true },
        { "numeric string vs number", Text("25"), N(25), true },

        // Rule_Value.cs:78-81: (-) is boolean false, so it equals an unchecked checkbox and not a checked one.
        { "false vs (-)", Bool(false), UncheckedLiteralValue(), true },
        { "true vs (-)", Bool(true), UncheckedLiteralValue(), false },
    };

    // Whitespace-only text is deliberately absent: legacy treats it as non-empty and the plan does not decide it.
    public static TheoryData<FieldValue, bool> EmptinessCases => new()
    {
        { FieldValue.Null, true },
        { Text(""), true },
        { Bool(false), false },
        { N(0), false },
        { Text("x"), false },
    };

    // Rule_Value.cs:72-110: (.) has no value, (-) is false, a number is a double and a string is its text.
    public static TheoryData<LiteralNode, FieldValue> LiteralCases => new()
    {
        { LiteralNode.Missing(1), FieldValue.Null },
        { LiteralNode.Unchecked(1), Bool(false) },
        { LiteralNode.FromNumber(25, 1), N(25) },
        { LiteralNode.FromString("x", 1), Text("x") },
    };

    public static TheoryData<string, string, FieldValue> TypedAssignments => new()
    {
        { "ASSIGN Age = 25", "Age", N(25) },
        { "ASSIGN LastName = \"Smith\"", "LastName", Text("Smith") },
        { "ASSIGN White = (-)", "White", Bool(false) },

        // (.) assigns an empty value: it is reported in Assigned, never as a clear.
        { "ASSIGN Age = (.)", "Age", FieldValue.Null },
    };

    [Theory]
    [MemberData(nameof(EqualityCases), DisableDiscoveryEnumeration = true)]
    public void AreEqual_FollowsTheLegacyComparisonRule(
        string caseName, FieldValue left, FieldValue right, bool expected)
    {
        bool actual = ValueComparer.AreEqual(left, right);

        Assert.True(
            actual == expected,
            $"{caseName}: AreEqual({left}, {right}) returned {actual}; expected {expected}.");
    }

    [Theory]
    [MemberData(nameof(EmptinessCases), DisableDiscoveryEnumeration = true)]
    public void IsEmpty_IsTrueOnlyForNullAndEmptyText(FieldValue value, bool expected)
    {
        Assert.Equal(expected, ValueComparer.IsEmpty(value));
    }

    [Theory]
    [MemberData(nameof(LiteralCases), DisableDiscoveryEnumeration = true)]
    public void LiteralToValue_GivesTheLegacyLiteralValue(LiteralNode literal, FieldValue expected)
    {
        Assert.Equal(expected, ValueComparer.LiteralToValue(literal));
    }

    [Theory]
    [InlineData(101.2, true)]
    [InlineData(null, false)]
    public void Evaluate_NotEqualsMissing_IsTrueOnlyWhenTheFieldHasAValue(double? feverTemp, bool expected)
    {
        var values = new Dictionary<string, FieldValue>
        {
            ["FeverTemp"] = feverTemp is { } temperature ? N(temperature) : FieldValue.Null,
        };
        var context = new EvaluationContext(FoodHistoryForm, values);
        var expression = new NotNode(
            new EqualsNode(new IdentifierNode("FeverTemp", 1), LiteralNode.Missing(1), 1),
            1);

        FieldValue result = Evaluator.Evaluate(expression, context, "IF");

        Assert.Equal(Bool(expected), result);
    }

    [Fact]
    public void FeverTempAfter_WithTemperatureAndUncheckedFever_ShowsTheWarningDialog()
    {
        // EC-FEVER-01: NOT FeverTemp = (.) AND Fever = (-) holds.
        FieldEventResponse response = Run(
            FoodHistoryProgram,
            FoodHistoryForm,
            FieldEventKind.FieldAfter,
            "FeverTemp",
            ("FeverTemp", N(101.2)),
            ("Fever", Bool(false)));

        DialogMessage dialog = Assert.Single(response.Dialogs);
        Assert.Equal(FeverDialogMessage, dialog.Message);
        Assert.Null(dialog.Title);
        AssertNoFieldEffects(response);
        Assert.Empty(response.Diagnostics);
    }

    [Theory]
    [InlineData(101.2, true)] // EC-FEVER-02: the fever checkbox is checked
    [InlineData(null, false)] // EC-FEVER-03: no temperature was entered
    public void FeverTempAfter_WithoutTheWarningCondition_ShowsNoDialog(double? feverTemp, bool fever)
    {
        FieldEventResponse response = Run(
            FoodHistoryProgram,
            FoodHistoryForm,
            FieldEventKind.FieldAfter,
            "FeverTemp",
            ("FeverTemp", feverTemp is { } temperature ? N(temperature) : FieldValue.Null),
            ("Fever", Bool(fever)));

        Assert.Empty(response.Dialogs);
        AssertNoFieldEffects(response);
        Assert.Empty(response.Diagnostics);
    }

    [Fact]
    public void Dialog_WithTitleText_CarriesMessageAndTitle()
    {
        const string Program = """
            Field FeverTemp
                After
                    DIALOG "m" TITLETEXT = "T"
                End-After
            End-Field
            """;

        FieldEventResponse response = Run(Program, FoodHistoryForm, FieldEventKind.FieldAfter, "FeverTemp");

        Assert.Equal(new DialogMessage("m", "T"), Assert.Single(response.Dialogs));
        Assert.Empty(response.Diagnostics);
    }

    [Fact]
    public void SexAfter_WithMale_EnablesPregnant()
    {
        // LY-01: codeSex "1" is Male, so the THEN branch runs.
        FieldEventResponse response = Run(
            CaseReportProgram,
            CaseReportForm,
            FieldEventKind.FieldAfter,
            "Sex",
            ("Sex", Text("1")));

        Assert.Equal(new[] { "Pregnant" }, response.Enabled);
        Assert.Empty(response.Disabled);
        Assert.Empty(response.Cleared);
        Assert.Empty(response.Assigned);
        Assert.Empty(response.Dialogs);
        Assert.Empty(response.Diagnostics);
    }

    [Theory]
    [InlineData(FieldEventKind.FieldAfter, "Sex", "2", "1")] // LY-02: kept legacy defect, "2" is Female
    [InlineData(FieldEventKind.FieldBefore, "LastName", "2", "2")] // LY-03: the same rule on LastName Before
    [InlineData(FieldEventKind.FieldAfter, "Sex", null, null)] // LY-04: an empty Sex is not "1"
    public void SexOtherThanMale_DisablesAndClearsPregnant(
        FieldEventKind kind, string field, string? sex, string? pregnant)
    {
        (string Name, FieldValue Value)[] values = pregnant is null
            ? [("Sex", TextOrNull(sex))]
            : [("Sex", TextOrNull(sex)), ("Pregnant", Text(pregnant))];

        FieldEventResponse response = Run(CaseReportProgram, CaseReportForm, kind, field, values);

        Assert.Empty(response.Enabled);
        Assert.Equal(new[] { "Pregnant" }, response.Disabled);
        Assert.Equal(new[] { "Pregnant" }, response.Cleared);
        Assert.Empty(response.Assigned);
        Assert.Empty(response.Dialogs);
        Assert.Empty(response.Diagnostics);
    }

    [Theory]
    [InlineData("ENABLE")]
    [InlineData("DISABLE")]
    [InlineData("CLEAR")]
    public void GroupName_ExpandsToItsMembers(string command)
    {
        // GuiMediator.IEnterCheckCode.cs:1631-1694, 2086-2220: a group stands for its metaFields.List members.
        string program = $"""
            Field Fever
                Click
                    {command} Symptoms
                End-Click
            End-Field
            """;

        FieldEventResponse response = Run(
            program,
            GroupForm,
            FieldEventKind.FieldClick,
            "Fever",
            ("Fever", Bool(true)),
            ("Chills", Bool(false)));

        IReadOnlyList<string> affected = command switch
        {
            "ENABLE" => response.Enabled,
            "DISABLE" => response.Disabled,
            "CLEAR" => response.Cleared,
            _ => throw new ArgumentOutOfRangeException(nameof(command), command, "Not a group statement."),
        };
        Assert.Contains("Fever", affected);
        Assert.Contains("Chills", affected);
        Assert.Empty(response.Diagnostics);
    }

    [Fact]
    public void Run_WithAHandBuiltEnableOfAGroup_EnablesItsMembers()
    {
        // The same expansion without the parser, so the evaluator is pinned on its own.
        var block = new EventBlockNode(
            FieldEventKind.FieldClick,
            2,
            new StatementNode[] { new EnableStatementNode(new[] { "Symptoms" }, 3) },
            Array.Empty<Diagnostic>());

        FieldEventResponse response = Evaluator.Run(
            GroupForm,
            new[] { block },
            new Dictionary<string, FieldValue> { ["Fever"] = Bool(true) });

        Assert.Contains("Fever", response.Enabled);
        Assert.Contains("Chills", response.Enabled);
        Assert.Empty(response.Diagnostics);
    }

    [Theory]
    [MemberData(nameof(TypedAssignments), DisableDiscoveryEnumeration = true)]
    public void Assign_StoresTheValueTypedByTheTargetField(string statement, string field, FieldValue expected)
    {
        string program = $"""
            Field LastName
                After
                    {statement}
                End-After
            End-Field
            """;

        FieldEventResponse response = Run(program, FoodHistoryForm, FieldEventKind.FieldAfter, "LastName");

        Assert.Equal(new[] { field }, response.Assigned.Keys);
        Assert.Equal(expected, response.Assigned[field]);
        Assert.Empty(response.Cleared);
        Assert.Empty(response.Diagnostics);
    }

    [Fact]
    public void DobAfter_AssignsYearsToTheCanonicalAgeField()
    {
        // EC-AGE-01: the program writes AGE; years(1986-01-01, 2011-05-11) is 25 in epi_functions.py.
        FieldEventResponse response = Run(
            FoodHistoryProgram,
            FoodHistoryForm,
            FieldEventKind.FieldAfter,
            "DOB",
            ("DOB", Date(1986, 1, 1)),
            ("DateofInterview", Date(2011, 5, 11)));

        Assert.Equal(new[] { "Age" }, response.Assigned.Keys);
        Assert.Equal(N(25), response.Assigned["Age"]);
        Assert.Empty(response.Diagnostics);
    }

    [Fact]
    public void DobAfter_WithoutInterviewDate_AssignsNullWithoutAnError()
    {
        // EC-AGE-05: YEARS is null unless both arguments are dates (Rule_Years.cs), which is not a runtime error.
        FieldEventResponse response = Run(
            FoodHistoryProgram,
            FoodHistoryForm,
            FieldEventKind.FieldAfter,
            "DOB",
            ("DOB", Date(2011, 1, 20)),
            ("DateofInterview", FieldValue.Null));

        Assert.True(response.Assigned.TryGetValue("Age", out FieldValue age));
        Assert.Equal(FieldValue.Null, age);
        Assert.DoesNotContain(response.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void OnsetDateAfter_AssignsEpiWeekToTheReadOnlyOnsetWeek()
    {
        // EC-WEEK-01: epiweek(2011-01-01) is 52 in epi_functions.py. OnsetWeek is read-only, and the engine still
        // reports the assignment: read-only protection belongs to the API and the client.
        Assert.True(FoodHistoryForm.FindField("OnsetWeek")!.IsReadOnly);

        FieldEventResponse response = Run(
            FoodHistoryProgram,
            FoodHistoryForm,
            FieldEventKind.FieldAfter,
            "OnsetDate",
            ("OnsetDate", Date(2011, 1, 1)));

        Assert.Equal(new[] { "OnsetWeek" }, response.Assigned.Keys);
        Assert.Equal(N(52), response.Assigned["OnsetWeek"]);
        Assert.Empty(response.Diagnostics);
    }

    [Fact]
    public void Identifier_InAnyCase_ResolvesToTheCanonicalFieldName()
    {
        const string Program = """
            Field Sex
                After
                    ENABLE pregnant
                End-After
            End-Field
            """;

        FieldEventResponse response = Run(Program, CaseReportForm, FieldEventKind.FieldAfter, "Sex");

        Assert.Equal(new[] { "Pregnant" }, response.Enabled);
        Assert.Empty(response.Diagnostics);
    }

    [Fact]
    public void Context_TryResolve_IgnoresCaseAndReturnsTheCanonicalField()
    {
        var context = new EvaluationContext(CaseReportForm, new Dictionary<string, FieldValue>());

        Assert.True(context.TryResolve("PREGNANT", out FieldMetadata? field));
        Assert.Equal("Pregnant", field!.Name);
    }

    [Fact]
    public void Context_Resolve_OfAnUnknownField_ThrowsWithTheStatementLocation()
    {
        var context = new EvaluationContext(CaseReportForm, new Dictionary<string, FieldValue>());

        var exception = Assert.Throws<CheckCodeRuntimeException>(() => context.Resolve("NoSuchField", 4, "ASSIGN"));

        Assert.Equal(4, exception.Line);
        Assert.Equal("ASSIGN", exception.Command);
    }

    [Fact]
    public void Context_Read_OfAKnownFieldAbsentFromTheInput_IsNull()
    {
        var context = new EvaluationContext(CaseReportForm, new Dictionary<string, FieldValue>());
        Assert.True(context.TryResolve("Age", out FieldMetadata? age));

        Assert.Equal(FieldValue.Null, context.Read(age));
    }

    [Fact]
    public void LaterStatement_ReadsAnEarlierAssign()
    {
        FieldEventResponse response = Run(
            AssignSexThenTestItProgram,
            CaseReportForm,
            FieldEventKind.FieldAfter,
            "Sex",
            ("Sex", Text("2")));

        Assert.Equal(new[] { "Pregnant" }, response.Enabled);
        Assert.Equal(Text("1"), response.Assigned["Sex"]);
        Assert.Empty(response.Diagnostics);
    }

    [Fact]
    public void LaterStatement_ReadsAnEarlierClear()
    {
        const string Program = """
            Field Sex
                After
                    CLEAR Sex
                    IF Sex = (.) THEN
                        DISABLE Pregnant
                    END-IF
                End-After
            End-Field
            """;

        FieldEventResponse response = Run(
            Program,
            CaseReportForm,
            FieldEventKind.FieldAfter,
            "Sex",
            ("Sex", Text("1")));

        Assert.Equal(new[] { "Pregnant" }, response.Disabled);
        Assert.Equal(new[] { "Sex" }, response.Cleared);
        Assert.Empty(response.Diagnostics);
    }

    [Fact]
    public void UnknownAssignTarget_ReportsAnErrorAndKeepsRunning()
    {
        const string Program = """
            Field Sex
                After
                    ENABLE Pregnant
                    ASSIGN NoSuchField = 1
                    ENABLE LastName
                End-After
            End-Field
            """;
        Assert.Equal("ASSIGN NoSuchField = 1", ProgramLine(Program, 4));

        FieldEventResponse response = RunWithoutThrowing(Program, CaseReportForm, FieldEventKind.FieldAfter, "Sex");

        Diagnostic diagnostic = Assert.Single(response.Diagnostics);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("ASSIGN", diagnostic.Command);
        Assert.Equal(4, diagnostic.Line);
        Assert.Equal("CaseReport", diagnostic.Form);
        Assert.False(string.IsNullOrWhiteSpace(diagnostic.Message));

        // The effect before the failing statement stands, and the statement after it still runs.
        Assert.Equal(new[] { "Pregnant", "LastName" }, response.Enabled);
        Assert.Empty(response.Assigned);
    }

    [Fact]
    public void EpiWeekWithoutADate_ReportsAnErrorAndAssignsNothing()
    {
        // Legacy throws from new DateTime(0, 1, 1) for a missing date (Rule_EPIWEEK.cs); the port reports it.
        const int AssignLine = 18;
        Assert.Equal("ASSIGN OnsetWeek = EPIWEEK(OnsetDate)", ProgramLine(FoodHistoryProgram, AssignLine));

        FieldEventResponse response = RunWithoutThrowing(
            FoodHistoryProgram,
            FoodHistoryForm,
            FieldEventKind.FieldAfter,
            "OnsetDate",
            ("OnsetDate", FieldValue.Null));

        Diagnostic diagnostic = Assert.Single(response.Diagnostics);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("ASSIGN", diagnostic.Command);
        Assert.Equal(AssignLine, diagnostic.Line);
        Assert.Equal("FoodHistory", diagnostic.Form);
        Assert.False(string.IsNullOrWhiteSpace(diagnostic.Message));
        Assert.False(response.Assigned.ContainsKey("OnsetWeek"));
    }

    [Fact]
    public void UnknownIdentifierInACondition_ReportsAnErrorAndRunsNeitherBranch()
    {
        const string Program = """
            Field Sex
                After
                    IF NoSuchField = "1" THEN
                        ENABLE Pregnant
                    ELSE
                        DISABLE Pregnant
                    END-IF
                End-After
            End-Field
            """;
        Assert.Equal("IF NoSuchField = \"1\" THEN", ProgramLine(Program, 3));

        FieldEventResponse response = RunWithoutThrowing(
            Program,
            CaseReportForm,
            FieldEventKind.FieldAfter,
            "Sex",
            ("Sex", Text("1")));

        Diagnostic diagnostic = Assert.Single(response.Diagnostics);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("IF", diagnostic.Command);
        Assert.Equal(3, diagnostic.Line);
        Assert.Equal("CaseReport", diagnostic.Form);
        Assert.False(string.IsNullOrWhiteSpace(diagnostic.Message));
        Assert.Empty(response.Enabled);
        Assert.Empty(response.Disabled);
    }

    [Theory]
    // Every term holds, so the whole AND chain does.
    [InlineData("Sex = \"1\"", " AND ", "Sex = \"1\"", LongChainTerms, "then")]
    // n (-) operands folded to the right, Eq(a, Eq(b, ...)), are true exactly when n is even.
    [InlineData("(-)", " = ", "(-)", LongChainTerms, "then")]
    [InlineData("(-)", " = ", "(-)", LongChainTerms - 1, "else")]
    // Right association: (.) is compared with the boolean the rest gives, and one empty side is never equal. A left
    // fold, Eq(Eq((.), (-)), (-)) ..., would be true for an odd operand count.
    [InlineData("(.)", " = ", "(-)", 3, "else")]
    [InlineData("(.)", " = ", "(-)", LongChainTerms + 1, "else")]
    public void FlatChain_GivesTheRightFoldWithoutExhaustingASmallStack(
        string first, string separator, string term, int terms, string branch)
    {
        string program = ChainProgram(Chain(first, separator, term, terms));

        FieldEventResponse response = RunOnSmallStack(
            () => Run(program, CaseReportForm, FieldEventKind.FieldAfter, "LastName", ("Sex", Text("1"))));

        Assert.Equal(new[] { "FirstName", "City" }, response.Assigned.Keys);
        Assert.Equal(Text(branch), response.Assigned["FirstName"]);
        Assert.Equal(Text("post"), response.Assigned["City"]);
        Assert.Empty(response.Diagnostics);
    }

    [Fact]
    public void LongAndChain_EvaluatesEveryTerm_SoAnErrorInTheLastFailsTheIf()
    {
        // The first term is already false; with no short circuit the unknown field in the last term still surfaces.
        string program = ChainProgram(
            Chain("Sex = \"2\"", " AND ", "Sex = \"1\"", LongChainTerms - 1) + " AND NoSuchField = \"1\"");
        Assert.StartsWith("IF Sex = \"2\" AND Sex = \"1\" AND", ProgramLine(program, 3), StringComparison.Ordinal);

        FieldEventResponse response = RunOnSmallStack(
            () => Run(program, CaseReportForm, FieldEventKind.FieldAfter, "LastName", ("Sex", Text("1"))));

        Diagnostic diagnostic = Assert.Single(response.Diagnostics);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("IF", diagnostic.Command);
        Assert.Equal(3, diagnostic.Line);
        Assert.Equal("CaseReport", diagnostic.Form);
        Assert.False(string.IsNullOrWhiteSpace(diagnostic.Message));

        // Neither branch ran, and the statement after the IF still did.
        Assert.Equal(new[] { "City" }, response.Assigned.Keys);
        Assert.Equal(Text("post"), response.Assigned["City"]);
    }

    [Theory]
    [InlineData("AND", "IF")]
    [InlineData("AND", "ASSIGN")]
    [InlineData("=", "IF")]
    [InlineData("=", "ASSIGN")]
    [InlineData("NOT", "IF")]
    [InlineData("NOT", "ASSIGN")]
    public void NestingDeeperThanTheStack_FailsOnlyItsStatement(string shape, string command)
    {
        // Left-nested, which the parser never builds from a flat chain, so the evaluator must recurse into it. The
        // tree is never printed or compared: its synthesized ToString and Equals would recurse as deep.
        const int Depth = 200_000;
        const int DeepLine = 4;
        var leaf = new IdentifierNode("Sex", DeepLine);
        ExpressionNode deep = leaf;
        for (var i = 0; i < Depth; i++)
        {
            deep = shape switch
            {
                "AND" => new AndNode(deep, leaf, DeepLine),
                "=" => new EqualsNode(deep, leaf, DeepLine),
                "NOT" => new NotNode(deep, DeepLine),
                _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "Not a nesting shape."),
            };
        }

        StatementNode middle = command switch
        {
            "IF" => new IfStatementNode(
                deep,
                new StatementNode[] { new AssignStatementNode("LastName", LiteralNode.FromString("then", 5), 5) },
                null,
                DeepLine),
            "ASSIGN" => new AssignStatementNode("LastName", deep, DeepLine),
            _ => throw new ArgumentOutOfRangeException(nameof(command), command, "Not a statement under test."),
        };
        var block = new EventBlockNode(
            FieldEventKind.FieldAfter,
            2,
            new StatementNode[]
            {
                new AssignStatementNode("FirstName", LiteralNode.FromString("before", 3), 3),
                middle,
                new AssignStatementNode("City", LiteralNode.FromString("after", 7), 7),
            },
            Array.Empty<Diagnostic>());
        var values = new Dictionary<string, FieldValue> { ["Sex"] = Text("1") };

        FieldEventResponse response = RunOnSmallStack(() => Evaluator.Run(CaseReportForm, new[] { block }, values));

        Diagnostic diagnostic = Assert.Single(response.Diagnostics);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal(command, diagnostic.Command);
        Assert.Equal(DeepLine, diagnostic.Line);
        Assert.Equal("CaseReport", diagnostic.Form);
        Assert.Equal("Expression is nested too deeply to evaluate.", diagnostic.Message);

        // The statements before and after the failing one both took effect.
        Assert.Equal(new[] { "FirstName", "City" }, response.Assigned.Keys);
        Assert.Equal(Text("before"), response.Assigned["FirstName"]);
        Assert.Equal(Text("after"), response.Assigned["City"]);
    }

    [Fact]
    public void AsianClick_UnchecksTheOtherRaceCheckboxesInProgramOrder()
    {
        // EC-RACE-01: the client sends Asian already toggled to true, as the click raises the event after it.
        FieldEventResponse response = Run(
            FoodHistoryProgram,
            FoodHistoryForm,
            FieldEventKind.FieldClick,
            "Asian",
            ("Asian", Bool(true)),
            ("White", Bool(true)));

        Assert.Equal(
            new[]
            {
                "Black",
                "AmericanIndianAlaskanNative",
                "NativeHawaiianOtherPacificIslander",
                "White",
                "Multiracial",
                "UnknownOther",
            },
            response.Assigned.Keys);
        Assert.All(response.Assigned.Values, value => Assert.Equal(Bool(false), value));
        Assert.False(response.Assigned.ContainsKey("Asian"));
        Assert.Empty(response.Enabled);
        Assert.Empty(response.Disabled);
        Assert.Empty(response.Cleared);
        Assert.Empty(response.Dialogs);
        Assert.Empty(response.Diagnostics);
    }

    [Fact]
    public void Run_NeverChangesTheCallersValues()
    {
        var values = new Dictionary<string, FieldValue> { ["Sex"] = Text("2") };
        KeyValuePair<string, FieldValue>[] before = values.ToArray();

        FieldEventResponse response = ((ICheckCodeEngine)new CheckCodeEngine()).Run(
            AssignSexThenTestItProgram,
            CaseReportForm,
            FieldEventKind.FieldAfter,
            "Sex",
            values);

        // The run did assign Sex, so an unchanged dictionary shows the assignment stayed inside the run.
        Assert.Equal(Text("1"), response.Assigned["Sex"]);
        Assert.Equal(before.Length, values.Count);
        foreach ((string name, FieldValue value) in before)
        {
            Assert.Equal(value, values[name]);
        }
    }

    [Fact]
    public void Run_RepeatedOnOneEngine_GivesTheSameResponse()
    {
        ICheckCodeEngine engine = new CheckCodeEngine();
        var values = new Dictionary<string, FieldValue> { ["Sex"] = Text("2") };

        FieldEventResponse first = engine.Run(
            AssignSexThenTestItProgram,
            CaseReportForm,
            FieldEventKind.FieldAfter,
            "Sex",
            values);
        FieldEventResponse second = engine.Run(
            AssignSexThenTestItProgram,
            CaseReportForm,
            FieldEventKind.FieldAfter,
            "Sex",
            values);

        Assert.Equal(new[] { "Pregnant" }, first.Enabled);
        Assert.Equal(first.Enabled, second.Enabled);
        Assert.Equal(first.Disabled, second.Disabled);
        Assert.Equal(first.Cleared, second.Cleared);
        Assert.Equal(first.Dialogs, second.Dialogs);
        Assert.Equal(first.Diagnostics, second.Diagnostics);
        Assert.Equal(
            first.Assigned.Select(entry => (entry.Key, entry.Value)),
            second.Assigned.Select(entry => (entry.Key, entry.Value)));
    }

    // Column metadata comes only from FieldTypeMapper.DescribeColumn, the one owner of the column-family rules.
    private static FieldMetadata F(
        int id,
        string name,
        int typeId,
        string typeName,
        int pageId,
        decimal tab,
        string? sqlType,
        int bytes,
        bool readOnly = false,
        string? list = null)
    {
        return new FieldMetadata
        {
            FieldId = id,
            Name = name,
            FieldTypeId = typeId,
            FieldTypeName = typeName,
            PageId = pageId,
            TabIndex = tab,
            IsReadOnly = readOnly,
            List = list,
            Column = sqlType is null ? null : FieldTypeMapper.DescribeColumn(name, sqlType, bytes),
        };
    }

    private static FieldEventResponse Run(
        string program,
        FormMetadata form,
        FieldEventKind kind,
        string field,
        params (string Name, FieldValue Value)[] values)
    {
        var dictionary = new Dictionary<string, FieldValue>();
        foreach ((string name, FieldValue value) in values)
        {
            dictionary.Add(name, value);
        }

        return ((ICheckCodeEngine)new CheckCodeEngine()).Run(program, form, kind, field, dictionary);
    }

    // The engine's contract is that no program text or values make it throw; a runtime error is a diagnostic.
    private static FieldEventResponse RunWithoutThrowing(
        string program,
        FormMetadata form,
        FieldEventKind kind,
        string field,
        params (string Name, FieldValue Value)[] values)
    {
        FieldEventResponse? response = null;

        Exception? exception = Record.Exception(() => response = Run(program, form, kind, field, values));

        Assert.Null(exception);
        Assert.NotNull(response);
        return response;
    }

    // Runs on a dedicated thread with a small, explicit stack, so a run whose stack use grew with a chain's length
    // would overflow here on any host. A stack overflow cannot be caught: it ends the test process and fails the run.
    private static FieldEventResponse RunOnSmallStack(Func<FieldEventResponse> run)
    {
        FieldEventResponse? response = null;
        Exception? exception = null;
        var thread = new Thread(() => exception = Record.Exception(() => response = run()), SmallStackBytes);
        thread.Start();
        thread.Join();

        Assert.Null(exception);
        Assert.NotNull(response);
        return response;
    }

    // first, then count - 1 more terms, each after the separator: "first SEP term SEP term ...".
    private static string Chain(string first, string separator, string term, int count) =>
        string.Join(separator, Enumerable.Repeat(term, count - 1).Prepend(first));

    // The IF over the chain stands on line 3; the ASSIGN after it runs whatever the condition gives.
    private static string ChainProgram(string condition) => $"""
        Field LastName
            After
                IF {condition} THEN
                    ASSIGN FirstName = "then"
                ELSE
                    ASSIGN FirstName = "else"
                END-IF
                ASSIGN City = "post"
            End-After
        End-Field
        """;

    // Line numbers are 1-based, as diagnostics report them; indentation is not part of the statement.
    private static string ProgramLine(string program, int line) => program.Split('\n')[line - 1].Trim();

    private static void AssertNoFieldEffects(FieldEventResponse response)
    {
        Assert.Empty(response.Enabled);
        Assert.Empty(response.Disabled);
        Assert.Empty(response.Cleared);
        Assert.Empty(response.Assigned);
    }

    private static FieldValue UncheckedLiteralValue() => ValueComparer.LiteralToValue(LiteralNode.Unchecked(1));

    private static FieldValue Text(string value) => FieldValue.FromText(value);

    private static FieldValue TextOrNull(string? value) => value is null ? FieldValue.Null : FieldValue.FromText(value);

    private static FieldValue N(double value) => FieldValue.FromNumber(value);

    private static FieldValue Bool(bool value) => FieldValue.FromBoolean(value);

    private static FieldValue Date(int year, int month, int day) => FieldValue.FromDate(new DateOnly(year, month, day));
}
