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
/// static diagnostics plus its runtime errors, in source order.
/// </summary>
/// <remarks>
/// Programs are inline raw string literals whose line 1 is the literal's first line, and each test that asserts a
/// line first checks the program text on that line, so the literal's indentation trimming cannot shift the numbering
/// unnoticed. Messages are never compared with literal text: only that they are present and carry no field value.
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

        // Diagnostics are logged, so a message may name fields and constructs but never carry a field value.
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

        // The block's static warning is collected before the block runs and its runtime error after, so this order
        // shows that the response is sorted by line rather than by when each diagnostic arose.
        Assert.Collection(
            engine.Run(program, SyntheticForm, FieldEventKind.FieldAfter, "Z", Values()).Diagnostics,
            error => AssertDiagnostic(error, DiagnosticSeverity.Error, SyntheticForm, 10, "ASSIGN"),
            required => AssertDiagnostic(required, DiagnosticSeverity.Warning, SyntheticForm, 11, "SET-REQUIRED"));
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
        FieldMetadata[] metadata = fields
            .Select((field, index) => new FieldMetadata
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
            })
            .ToArray();

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
