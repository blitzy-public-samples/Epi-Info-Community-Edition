using System.Globalization;
using EpiInfo.CheckCode;
using EpiInfo.CheckCode.Evaluation;
using EpiInfo.CheckCode.Lexing;
using EpiInfo.CheckCode.Parsing;
using EpiInfo.CheckCode.Syntax;
using EpiInfo.Domain.Diagnostics;
using EpiInfo.Domain.Events;
using EpiInfo.Domain.Metadata;
using EpiInfo.Domain.Values;

namespace EpiInfo.CheckCode.Tests.Evaluation;

/// <summary>
/// Pins the tree-walking evaluator of the check-code subset against legacy Epi Info: the <c>=</c> rule and literal
/// values of <c>Rule_CompareExp</c> and <c>Rule_Value</c>, <c>NOT</c> and <c>AND</c>, <c>IF/ELSE</c>,
/// <c>ENABLE</c>, <c>DISABLE</c> and <c>CLEAR</c> with group expansion, typed <c>ASSIGN</c> with its conversions,
/// option bounds and rejected targets, <c>YEARS</c> and <c>EPIWEEK</c>, <c>DIALOG</c>, case-insensitive identifiers,
/// runtime-error recovery, bounded stack use for long chains and deep nesting, and statelessness.
/// </summary>
/// <remarks>
/// Scenario values from <c>testdata/scenarios/check_code_scenarios.json</c> are restated inline and the file is never
/// read, so this stays a pure unit test. The seeded programs are verbatim copies of <c>metaViews.CheckCode</c> in
/// <c>testdata/sql/03_forms.sql</c>, and the form metadata mirrors the seeded <c>metaFields</c> rows and page-table
/// columns of the fields those programs touch. Synthetic forms stand in where no seeded field or program reaches a
/// case.
/// </remarks>
public sealed class EvaluatorTests
{
    private const string FeverDialogMessage =
        "Warning: A fever temperature has been entered, but 'fever' was not selected as a symptom.";

    // Far past the roughly 9,000 terms at which evaluation that recursed once per term overflowed a pool thread.
    private const int LongChainTerms = 100_000;

    // An explicit stack for the long-chain cases, so their outcome never depends on the host's default thread size.
    private const int SmallStackBytes = 1024 * 1024;

    // The fixed, value-free reason of a condition or NOT/AND operand with no value.
    private const string MissingConditionValueReason = "A condition has no value.";

    // The line of the IF in ConditionProgram.
    private const int ConditionLine = 4;

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

    // Synthetic: metaFields.List is free text that legacy GroupField.ChildFieldNameArray splits on commas
    // [Epi.Core/Services/Fields/GrouplField.cs:126-133]. This List pads, changes letter case, repeats a member and
    // holds an unknown name and empty items, in an order unlike the fields' own. The label Heading and the group Signs
    // are members with no value, and Signs' own member Rash is not a member of Symptoms. Cough lies outside both
    // groups, and the Option WorstSymptom has a List that names fields.
    private static readonly FormMetadata MessyGroupForm = new(
        ViewId: 96,
        Name: "SyntheticMessyGroup",
        BaseTableName: "SyntheticMessyGroup",
        Pages: [new PageMetadata(1, "Page 1", 0, "SyntheticMessyGroup1")],
        Fields:
        [
            F(1, "Symptoms", 21, "Group", 1, 1, null, 0, list: " chills , FEVER,Chills,NoSuchField,, ,Heading, signs "),
            F(2, "Heading", 2, "Label/Title", 1, 2, null, 0),
            F(3, "Fever", 10, "Checkbox", 1, 3, "bit", 1),
            F(4, "Chills", 10, "Checkbox", 1, 4, "bit", 1),
            F(5, "Cough", 10, "Checkbox", 1, 5, "bit", 1),
            F(6, "WorstSymptom", 12, "Option", 1, 6, "smallint", 2, list: "Fever,Chills"),
            F(7, "Signs", 21, "Group", 1, 7, null, 0, list: "Rash"),
            F(8, "Rash", 10, "Checkbox", 1, 8, "bit", 1),
        ]);

    // Synthetic: no seeded field is an Option and no seeded program assigns a label, group or unsupported field, so
    // typed ASSIGN needs its own form. Colour's items are the List items before "||", Red, Green and Blue at indexes
    // 0 to 2 [Epi.Core/Services/Fields/OptionField.cs:84-102], in a smallint column as legacy stores them
    // [Epi.Core/Services/Fields/OptionField.cs:45-56]. Command Button is always unsupported, and Yes/No is
    // unsupported on its tinyint column.
    private static readonly FormMetadata TypedForm = new(
        ViewId: 97,
        Name: "SyntheticTypes",
        BaseTableName: "SyntheticTypes",
        Pages: [new PageMetadata(1, "Page 1", 0, "SyntheticTypes1")],
        Fields:
        [
            F(1, "Heading", 2, "Label/Title", 1, 1, null, 0),
            F(2, "Contact", 21, "Group", 1, 2, null, 0, list: "LastName,Score"),
            F(3, "LastName", 1, "Text", 1, 3, "nvarchar", 8000),
            F(4, "Remarks", 4, "Multiline", 1, 4, "ntext", 16),
            F(5, "Score", 5, "Number", 1, 5, "float", 8),
            F(6, "Visited", 7, "Date", 1, 6, "datetime", 8),
            F(7, "Seen", 7, "Date", 1, 7, "datetime", 8),
            F(8, "Colour", 12, "Option", 1, 8, "smallint", 2, list: "Red,Green,Blue||10,20"),
            F(9, "Locate", 13, "Command Button", 1, 9, null, 0),
            F(10, "Consent", 11, "Yes/No", 1, 10, "tinyint", 1),
        ]);

    public static TheoryData<string, FieldValue, FieldValue, bool> EqualityCases => new()
    {
        // Rule_CompareExp.cs:73-76: both sides empty under "=" are equal; Util.IsEmpty counts "" as empty.
        { "both null", FieldValue.Null, FieldValue.Null, true },
        { "empty text equals null", Text(""), FieldValue.Null, true },

        // Rule_CompareExp.cs:81-90: exactly one empty side is never equal.
        { "null vs text", FieldValue.Null, Text("1"), false },
        { "text vs null", Text("1"), FieldValue.Null, false },
        { "unset checkbox vs (-)", FieldValue.Null, Bool(false), false },

        // Rule_CompareExp.cs:224-227: two strings compare trimmed, ignoring case and linguistically, so a
        // precomposed accent equals the same letter followed by a combining accent.
        { "trimmed, case-insensitive", Text(" abc "), Text("ABC"), true },
        { "different text", Text("abc"), Text("abd"), false },
        { "precomposed vs decomposed accent", Text("caf\u00E9"), Text("cafe\u0301"), true },

        // Rule_CompareExp.cs:111-115: two numerics, an option index included, compare as doubles.
        { "numbers", N(101.2), N(101.2), true },
        { "different numbers", N(101.2), N(101.3), false },
        { "number vs the same option index", N(2), FieldValue.FromIndex(2), true },
        { "number vs another option index", N(2), FieldValue.FromIndex(1), false },

        // Rule_CompareExp.cs:228-230: two dates compare by value.
        { "same date", Date(2011, 1, 15), Date(2011, 1, 15), true },
        { "different dates", Date(2011, 1, 15), Date(2011, 1, 16), false },

        // Rule_CompareExp.cs:123-175: numeric text against a number is parsed with double.TryParse(string), whose
        // styles allow thousands separators, and compared as a double.
        { "number vs numeric string", N(25), Text("25"), true },
        { "numeric string vs number", Text("25"), N(25), true },
        { "grouped numeric string vs number", Text("1,000"), N(1000), true },
        { "number vs grouped numeric string", N(1000), Text("1,000"), true },
        { "grouped decimal string vs number", Text("1,000.5"), N(1000.5), true },

        // Rule_CompareExp.cs:191-217: text double.TryParse rejects, such as "25" followed by a non-breaking space,
        // is compared with the number's string form untrimmed, so it is not equal.
        { "non-breaking-space numeric string vs number", Text("25\u00A0"), N(25), false },
        { "number vs non-breaking-space numeric string", N(25), Text("25\u00A0"), false },

        // Rule_Value.cs:78-81: (-) is boolean false, so it equals an unchecked checkbox and not a checked one.
        { "false vs (-)", Bool(false), Bool(false), true },
        { "true vs (-)", Bool(true), Bool(false), false },

        // Rule_CompareExp.cs:119-121: a boolean on the right is compared with "the left side is not empty", so a
        // non-empty left side never equals (-) and always equals true, whatever it says.
        { "text vs (-)", Text("x"), Bool(false), false },
        { "number vs (-)", N(5), Bool(false), false },
        { "\"No\" vs (-)", Text("No"), Bool(false), false },
        { "text vs a checked checkbox", Text("x"), Bool(true), true },

        // Rule_CompareExp.cs:182-185 with EnterRule.cs:64-89: a boolean on the left is compared with the right
        // text converted by ConvertStringToBoolean, in any letter case: (+), YES, Y, TRUE, T are true and (-),
        // NO, N, FALSE, F are false.
        { "unchecked vs \"No\"", Bool(false), Text("No"), true },
        { "checked vs \"No\"", Bool(true), Text("No"), false },
        { "unchecked vs \"False\"", Bool(false), Text("False"), true },
        { "unchecked vs \"(-)\"", Bool(false), Text("(-)"), true },
        { "unchecked vs \"n\"", Bool(false), Text("n"), true },
        { "unchecked vs \"f\"", Bool(false), Text("f"), true },
        { "checked vs \"FALSE\"", Bool(true), Text("FALSE"), false },
        { "checked vs \"yes\"", Bool(true), Text("yes"), true },
        { "checked vs \"y\"", Bool(true), Text("y"), true },
        { "checked vs \"True\"", Bool(true), Text("True"), true },
        { "checked vs \"T\"", Bool(true), Text("T"), true },
        { "checked vs \"(+)\"", Bool(true), Text("(+)"), true },

        // Util.cs:995-1001: IsEmpty tests string.IsNullOrEmpty, so whitespace-only text is a non-empty side, which
        // Rule_CompareExp.cs:81-90 never makes equal to an empty one.
        { "whitespace-only text vs null", Text(" "), FieldValue.Null, false },
        { "null vs whitespace-only text", FieldValue.Null, Text(" "), false },

        // Rule_CompareExp.cs:111-115: an option index on the left compares with a number as a double too.
        { "option index vs the same number", FieldValue.FromIndex(2), N(2), true },
        { "option index vs another number", FieldValue.FromIndex(2), N(25), false },

        // Rule_CompareExp.cs:123-175: double.TryParse accepts surrounding white space, and text that does not parse
        // falls through to the string comparison of lines 191-217.
        { "padded numeric string vs number", Text(" 25 "), N(25), true },
        { "number vs padded numeric string", N(25), Text(" 25 "), true },
        { "non-numeric string vs number", Text("abc"), N(25), false },
        { "number vs non-numeric string", N(25), Text("abc"), false },

        // Rule_CompareExp.cs:191-217: a date against text compares the string forms of both sides, untrimmed. A
        // date's form is DateTime.ToString(), which carries a time, so a date never equals date-only text, its own
        // ISO form included, on either side. The server writes that form in the invariant culture.
        { "date vs text of another date", Date(2011, 1, 15), Text("2011-01-16"), false },
        { "text of another date vs date", Text("2011-01-16"), Date(2011, 1, 15), false },
        { "date vs its ISO text", Date(2011, 1, 15), Text("2011-01-15"), false },
        { "its ISO text vs date", Text("2011-01-15"), Date(2011, 1, 15), false },
        { "date vs its invariant date-and-time text", Date(2011, 1, 15), Text("01/15/2011 00:00:00"), true },
        { "its invariant date-and-time text vs date", Text("01/15/2011 00:00:00"), Date(2011, 1, 15), true },
        { "date vs padded date-and-time text", Date(2011, 1, 15), Text(" 01/15/2011 00:00:00 "), false },
        { "date vs en-US date-and-time text", Date(2011, 1, 15), Text("1/15/2011 12:00:00 AM"), false },
    };

    // Rule_CompareExp.cs:182-185: legacy's ConvertStringToBoolean (EnterRule.cs:64-89) has no value for these right
    // sides, matching untrimmed text only, so the cast of its null result throws.
    public static TheoryData<string, FieldValue, FieldValue> UncomparableCases => new()
    {
        { "checked vs other text", Bool(true), Text("abc") },
        { "unchecked vs untrimmed \" No \"", Bool(false), Text(" No ") },
        { "checked vs number", Bool(true), N(1) },
        { "unchecked vs date", Bool(false), Date(2011, 1, 15) },
        { "checked vs option index", Bool(true), FieldValue.FromIndex(0) },
    };

    // Rule_If_Then_Else_End.cs:170 runs THEN when the condition's ToString().ToLowerInvariant() is "true", which a
    // checked checkbox gives and an unchecked one, a number, a date and an option index never give. A text,
    // multiline or legal-values field with no value reads as "" (Rule_Value.cs:400-407), so, like empty text, it
    // runs ELSE without an error. A field absent from the values has no value. The text that is true is pinned by
    // IfNotAndAnd_OverATextField_FollowTheLegacyTextTruth.
    public static TheoryData<FormMetadata, string, FieldValue?, bool> IfConditionCases => new()
    {
        { FoodHistoryForm, "Fever", Bool(true), true },
        { FoodHistoryForm, "Fever", Bool(false), false },
        { FoodHistoryForm, "LastName", Text(""), false },
        { FoodHistoryForm, "FeverTemp", N(101.2), false },
        { FoodHistoryForm, "OnsetDate", Date(2011, 1, 15), false },
        { TypedForm, "Colour", FieldValue.FromIndex(1), false },
        { FoodHistoryForm, "Sex", null, false },
        { TypedForm, "Remarks", null, false },
    };

    // Rule_If_Then_Else_End.cs:170 calls ToString() on the condition, which throws for a null value before either
    // branch runs. Rule_Value.cs:358-386 reads an empty checkbox, number or date field as null, Rule_Value.cs:72-110
    // makes (.) null, and YEARS without two dates returns null (Rule_Years.cs).
    public static TheoryData<ExpressionNode> ValuelessConditionCases => new()
    {
        new IdentifierNode("Fever", 3),
        new IdentifierNode("FeverTemp", 3),
        new IdentifierNode("OnsetDate", 3),
        LiteralNode.Missing(3),
        new FunctionCallNode(
            "YEARS",
            new ExpressionNode[] { new IdentifierNode("DOB", 3), new IdentifierNode("DateofInterview", 3) },
            3),
    };

    // Rule_NotExp.cs:39 and Rule_AndExp.cs:41-42 call ToString() on each operand, which throws for the empty
    // checkbox Fever (Rule_Value.cs:358-367). The left operand is converted before the right one runs, so an empty
    // left operand fails before the unknown field on the right is reached, and a false left operand still lets an
    // empty right one fail.
    public static TheoryData<string, (string, FieldValue)[]> ValuelessOperandCases => new()
    {
        { "NOT Fever", [] },
        { "Fever AND LastName", [("LastName", Text("true"))] },
        { "Fever AND NoSuchField", [] },
        { "LastName AND Fever", [("LastName", Text("1"))] },
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

    // Rule_Value.cs:72-110: (.) has no value, (-) is false, a number is a double and a string is its text. A number
    // parsed from source is typed by its spelling, as the same text in values is, so a whole spelling keeps its exact
    // integer beyond 2^53, where its double is rounded; text that is not a spelling of the number is ignored.
    public static TheoryData<LiteralNode, FieldValue> LiteralCases => new()
    {
        { LiteralNode.Missing(1), FieldValue.Null },
        { LiteralNode.Unchecked(1), Bool(false) },
        { LiteralNode.FromNumber(25, 1), N(25) },
        { LiteralNode.FromNumber("2.0", 2, 1), N(2) },
        { LiteralNode.FromNumber("9007199254740993", 9007199254740992d, 1), FieldValue.FromInteger(9007199254740993) },
        { new LiteralNode(LiteralKind.Number, "3", 2, 1), N(2) },
        { LiteralNode.FromString("x", 1), Text("x") },
    };

    // Synthetic: no seeded field is an Option. Opt has three items, so its indexes are 0 to 2, and Num's bigint
    // column holds integers beyond 2^53 exactly.
    private static readonly FormMetadata OptionForm = new(
        ViewId: 98,
        Name: "SyntheticOption",
        BaseTableName: "SyntheticOption",
        Pages: [new PageMetadata(1, "Page 1", 0, "SyntheticOption1")],
        Fields:
        [
            F(1, "Opt", 12, "Option", 1, 1, "smallint", 2, list: "A,B,C"),
            F(2, "Txt", 1, "Text", 1, 2, "nvarchar", 8000),
            F(3, "Num", 5, "Number", 1, 3, "bigint", 8),
        ]);

    // A whole spelling is typed exactly, as the same JSON value is: 2 and 2.0 are option index 2, and
    // 9007199254740993 is not rounded to the double 9007199254740992.
    public static TheoryData<string, string, FieldValue> ExactNumberAssignments => new()
    {
        { "ASSIGN Opt = 2", "Opt", FieldValue.FromIndex(2) },
        { "ASSIGN Opt = 2.0", "Opt", FieldValue.FromIndex(2) },
        { "ASSIGN Num = 9007199254740993", "Num", FieldValue.FromInteger(9007199254740993) },
    };

    public static TheoryData<FormMetadata, string, string, FieldValue, (string, FieldValue)[]> TypedAssignments => new()
    {
        { FoodHistoryForm, "ASSIGN Age = 25", "Age", N(25), [] },
        { FoodHistoryForm, "ASSIGN LastName = \"Smith\"", "LastName", Text("Smith"), [] },
        { FoodHistoryForm, "ASSIGN White = (-)", "White", Bool(false), [] },

        // NOT is a boolean outside =, so a checkbox takes it; LastName is absent, an empty text field, which is not
        // true.
        { FoodHistoryForm, "ASSIGN White = NOT LastName", "White", Bool(true), [] },

        // (.) assigns an empty value: it is reported in Assigned, never as a clear.
        { FoodHistoryForm, "ASSIGN Age = (.)", "Age", FieldValue.Null, [] },

        // ASSIGN types its value as values does: a date field holds a date, carried as YYYY-MM-DD.
        { TypedForm, "ASSIGN Seen = Visited", "Seen", Date(2011, 1, 15), [("Visited", Date(2011, 1, 15))] },

        // A text-family field holds a string: a number as its invariant round-trip text, a whole one without a
        // decimal point, and a date as the YYYY-MM-DD text a date has in values.
        { TypedForm, "ASSIGN LastName = 37.5", "LastName", Text("37.5"), [] },
        { TypedForm, "ASSIGN LastName = 25", "LastName", Text("25"), [] },
        { TypedForm, "ASSIGN LastName = Visited", "LastName", Text("2011-01-15"), [("Visited", Date(2011, 1, 15))] },
        { TypedForm, "ASSIGN Remarks = Visited", "Remarks", Text("2011-01-15"), [("Visited", Date(2011, 1, 15))] },

        // An option holds the 0-based index of its item [Epi.Windows.Enter/Forms/canvas.render.cs:184-200], from 0
        // to the item count minus 1, and a whole number, written or read from a number field, is that index.
        { TypedForm, "ASSIGN Colour = 0", "Colour", FieldValue.FromIndex(0), [] },
        { TypedForm, "ASSIGN Colour = 2", "Colour", FieldValue.FromIndex(2), [] },
        { TypedForm, "ASSIGN Colour = Score", "Colour", FieldValue.FromIndex(1), [("Score", N(1))] },

        // A number field reads an option as its index.
        { TypedForm, "ASSIGN Score = Colour", "Score", N(2), [("Colour", FieldValue.FromIndex(2))] },
    };

    // Each statement has no exact conversion to its target (see TypedForm), so it fails with an error diagnostic.
    // The string is the assigned value's text, which the message must not carry.
    public static TheoryData<string, string, string, (string, FieldValue)[]> RejectedAssignments => new()
    {
        // An option index is a whole number from 0 to the item count minus 1. Colour has three items, so 3 is out of
        // range even though the List holds five comma-separated items once its "||" tail is counted. A negative
        // index comes from a number field, because the subset has no unary minus.
        { "ASSIGN Colour = 1.5", "Colour", "1.5", [] },
        { "ASSIGN Colour = 3", "Colour", "3", [] },
        { "ASSIGN Colour = Score", "Colour", "-1", [("Score", N(-1))] },

        // An option holds an index, never text, even numeric text, or a boolean.
        { "ASSIGN Colour = \"1\"", "Colour", "1", [] },
        { "ASSIGN Colour = (-)", "Colour", "false", [] },

        // Labels, groups and unsupported fields are not data fields: they have no value to assign.
        { "ASSIGN Heading = \"Smith\"", "Heading", "Smith", [] },
        { "ASSIGN Contact = 7", "Contact", "7", [] },
        { "ASSIGN Locate = \"Smith\"", "Locate", "Smith", [] },
        { "ASSIGN Consent = 1", "Consent", "1", [] },

        // A date field holds only a date, and a number field only a number or an option index.
        { "ASSIGN Visited = \"Smith\"", "Visited", "Smith", [] },
        { "ASSIGN Score = (-)", "Score", "false", [] },
    };

    [Theory]
    [MemberData(nameof(EqualityCases), DisableDiscoveryEnumeration = true)]
    public void TryAreEqual_FollowsTheLegacyComparisonRule(
        string caseName, FieldValue left, FieldValue right, bool expected)
    {
        bool compared = ValueComparer.TryAreEqual(left, right, out bool actual);

        Assert.True(compared, $"{caseName}: TryAreEqual({left}, {right}) could not compare the operands.");
        Assert.True(
            actual == expected,
            $"{caseName}: TryAreEqual({left}, {right}) gave {actual}; expected {expected}.");
    }

    [Theory]
    [MemberData(nameof(UncomparableCases), DisableDiscoveryEnumeration = true)]
    public void TryAreEqual_RejectsABooleanOnTheLeftThatLegacyCannotConvert(
        string caseName, FieldValue left, FieldValue right)
    {
        bool compared = ValueComparer.TryAreEqual(left, right, out bool equal);

        Assert.False(compared, $"{caseName}: TryAreEqual({left}, {right}) compared operands legacy cannot compare.");
        Assert.False(equal, $"{caseName}: TryAreEqual({left}, {right}) gave true without comparing.");
    }

    [Fact]
    public void TryAreEqual_DoesNotDependOnTheCurrentCulture()
    {
        // de-DE reads "." as the group separator and "," as the decimal point; the comparison reads neither so.
        CultureInfo culture = CultureInfo.CurrentCulture;
        CultureInfo uiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de-DE");

            Assert.True(ValueComparer.TryAreEqual(Text("37.5"), N(37.5), out bool decimalEqual));
            Assert.True(decimalEqual, "\"37.5\" must equal 37.5 under de-DE.");
            Assert.True(ValueComparer.TryAreEqual(Text("1,000"), N(1000), out bool groupedEqual));
            Assert.True(groupedEqual, "\"1,000\" must equal 1000 under de-DE.");

            // de-DE writes the date 15.01.2011 00:00:00; the comparison writes it in the invariant culture.
            Assert.True(ValueComparer.TryAreEqual(Date(2011, 1, 15), Text("01/15/2011 00:00:00"), out bool dateEqual));
            Assert.True(dateEqual, "The invariant date-and-time text must equal the date under de-DE.");
            Assert.True(ValueComparer.TryAreEqual(Date(2011, 1, 15), Text("15.01.2011 00:00:00"), out bool localEqual));
            Assert.False(localEqual, "The de-DE date-and-time text must not equal the date.");
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = uiCulture;
        }
    }

    [Theory]
    [InlineData(false, "T")] // ConvertStringToBoolean reads "No" as false, so an unchecked Fever equals it
    [InlineData(true, "F")]
    public void IfCheckboxEqualsYesNoText_ComparesWithTheConvertedText(bool fever, string branch)
    {
        FieldEventResponse response = Run(
            CheckboxAgainstTextProgram("No"),
            FoodHistoryForm,
            FieldEventKind.FieldAfter,
            "FeverTemp",
            ("Fever", Bool(fever)));

        Assert.Equal(new[] { branch, "post" }, response.Dialogs.Select(dialog => dialog.Message));
        AssertNoFieldEffects(response);
        Assert.Empty(response.Diagnostics);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IfCheckboxEqualsOtherText_ReportsATypeMismatchAndRunsTheNextStatement(bool fever)
    {
        // Legacy's conversion of "abc" fails and throws (Rule_CompareExp.cs:182-185); the port reports the IF.
        string program = CheckboxAgainstTextProgram("abc");
        Assert.Equal("IF Fever = \"abc\" THEN", ProgramLine(program, 3));

        FieldEventResponse response = RunWithoutThrowing(
            program,
            FoodHistoryForm,
            FieldEventKind.FieldAfter,
            "FeverTemp",
            ("Fever", Bool(fever)));

        Diagnostic diagnostic = Assert.Single(response.Diagnostics);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("IF", diagnostic.Command);
        Assert.Equal(3, diagnostic.Line);
        Assert.Equal("FoodHistory", diagnostic.Form);
        Assert.False(string.IsNullOrWhiteSpace(diagnostic.Message));
        Assert.DoesNotContain("abc", diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal(new[] { "post" }, response.Dialogs.Select(dialog => dialog.Message));
        AssertNoFieldEffects(response);
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

    // No double builds the expected value: the nearest double of 2.0000000000000001 is the whole number 2, while the
    // literal is not whole, so the two parts of the value are checked separately.
    [Fact]
    public void LiteralToValue_OfANonIntegralSpellingWithAWholeDouble_IsNotAnInteger()
    {
        FieldValue value = ValueComparer.LiteralToValue(LiteralNode.FromNumber("2.0000000000000001", 2, 1));

        Assert.Equal(FieldValueKind.Number, value.Kind);
        Assert.False(value.TryGetInteger(out _));
        Assert.True(value.TryGetNumber(out double nearest));
        Assert.Equal(2.0, nearest);
    }

    [Theory]
    [InlineData(null, 2d, typeof(ArgumentNullException))]
    [InlineData("", 2d, typeof(ArgumentException))]
    [InlineData("3", 2d, typeof(ArgumentException))]
    [InlineData("two", 2d, typeof(ArgumentException))]
    [InlineData("1E999", double.PositiveInfinity, typeof(ArgumentOutOfRangeException))]
    public void FromNumber_WithASpelling_RejectsTextThatIsNotASpellingOfAFiniteNumber(
        string? text, double value, Type expected)
    {
        Exception? exception = Record.Exception(() => LiteralNode.FromNumber(text!, value, 1));

        Assert.NotNull(exception);
        Assert.IsType(expected, exception);
    }

    [Fact]
    public void Assign_OfANonIntegralNumberToAnOption_ReportsAnErrorAndKeepsRunning()
    {
        // The JSON value 2.0000000000000001 is no option index, so neither is the literal, whose double is 2.
        const string Program = """
            Field Txt
                After
                    ASSIGN Opt = 2.0000000000000001
                    ASSIGN Txt = "post"
                End-After
            End-Field
            """;
        Assert.Equal("ASSIGN Opt = 2.0000000000000001", ProgramLine(Program, 3));

        FieldEventResponse response = RunWithoutThrowing(Program, OptionForm, FieldEventKind.FieldAfter, "Txt");

        Diagnostic diagnostic = Assert.Single(response.Diagnostics);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("ASSIGN", diagnostic.Command);
        Assert.Equal(3, diagnostic.Line);
        Assert.Equal("SyntheticOption", diagnostic.Form);
        Assert.False(string.IsNullOrWhiteSpace(diagnostic.Message));

        // The failing statement assigns nothing, and the statement after it still runs.
        Assert.Equal(new[] { "Txt" }, response.Assigned.Keys);
        Assert.Equal(Text("post"), response.Assigned["Txt"]);
    }

    [Theory]
    [MemberData(nameof(ExactNumberAssignments), DisableDiscoveryEnumeration = true)]
    public void Assign_OfAWholeNumberLiteral_KeepsItsExactInteger(string statement, string field, FieldValue expected)
    {
        string program = $"""
            Field Txt
                After
                    {statement}
                End-After
            End-Field
            """;

        FieldEventResponse response = Run(program, OptionForm, FieldEventKind.FieldAfter, "Txt");

        Assert.Equal(new[] { field }, response.Assigned.Keys);
        Assert.Equal(expected, response.Assigned[field]);
        Assert.Empty(response.Diagnostics);
    }

    [Fact]
    public void Equals_OfANonIntegralLiteralWithAWholeDouble_ComparesItsNearestDouble()
    {
        // Rule_CompareExp.cs:111-115 compares numerics as doubles, so exact typing leaves "=" unchanged.
        const string Program = """
            Field Txt
                After
                    IF Num = 2.0000000000000001 THEN
                        ASSIGN Txt = "equal"
                    END-IF
                End-After
            End-Field
            """;

        FieldEventResponse response = Run(
            Program,
            OptionForm,
            FieldEventKind.FieldAfter,
            "Txt",
            ("Num", FieldValue.FromInteger(2)));

        Assert.Equal(new[] { "Txt" }, response.Assigned.Keys);
        Assert.Equal(Text("equal"), response.Assigned["Txt"]);
        Assert.Empty(response.Diagnostics);
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

    // Legacy truth is the text "true" in any letter case, untrimmed. Rule_If_Then_Else_End.cs:170 and
    // Rule_AndExp.cs:41-44 lower the operand's ToString() and compare it ordinally with "true", which isTrue gives;
    // Rule_NotExp.cs:40 compares it with "true" in a culture comparison that ignores case, which notIsTrue gives. A
    // text field with no value, which legacy read as "" (Rule_Value.cs:400-407), is not true. The soft-hyphen row is
    // where the two differ: NOT's culture comparison ignores that character, so NOT finds the text true and is false,
    // while IF and AND find it not true.
    [Theory]
    [InlineData("TRUE", true, true)]
    [InlineData("true", true, true)]
    [InlineData("True", true, true)]
    [InlineData(" true", false, false)]
    [InlineData("yes", false, false)]
    [InlineData("1", false, false)]
    [InlineData("false", false, false)]
    [InlineData("tr\u00ADue", false, true)]
    [InlineData(null, false, false)]
    public void IfNotAndAnd_OverATextField_FollowTheLegacyTextTruth(string? lastName, bool isTrue, bool notIsTrue)
    {
        const string Program = """
            Field LastName
                After
                    IF LastName THEN
                        DIALOG "IF T"
                    ELSE
                        DIALOG "IF F"
                    END-IF
                    IF NOT LastName THEN
                        DIALOG "NOT T"
                    ELSE
                        DIALOG "NOT F"
                    END-IF
                    IF LastName AND Sex = "1" THEN
                        DIALOG "AND T"
                    ELSE
                        DIALOG "AND F"
                    END-IF
                    IF Sex = "1" AND LastName THEN
                        DIALOG "AND-RIGHT T"
                    ELSE
                        DIALOG "AND-RIGHT F"
                    END-IF
                End-After
            End-Field
            """;
        string holds = isTrue ? "T" : "F";
        string negated = notIsTrue ? "F" : "T";

        FieldEventResponse response = Run(
            Program,
            CaseReportForm,
            FieldEventKind.FieldAfter,
            "LastName",
            ("LastName", TextOrNull(lastName)),
            ("Sex", Text("1")));

        Assert.Equal(
            new[] { $"IF {holds}", $"NOT {negated}", $"AND {holds}", $"AND-RIGHT {holds}" },
            response.Dialogs.Select(dialog => dialog.Message));
        AssertNoFieldEffects(response);
        Assert.Empty(response.Diagnostics);
    }

    [Theory]
    [InlineData("LastName", false)] // the text "True"
    [InlineData("Sex", false)] // "TRUE" with a soft hyphen, which NOT's culture comparison ignores
    [InlineData("Fever", false)] // a checked checkbox
    [InlineData("White", true)] // an unchecked checkbox
    [InlineData("Age", true)] // a number is never true
    [InlineData("DateofInterview", true)] // nor is a date
    public void Evaluate_Not_IsFalseOnlyForATrueOperand(string field, bool expected)
    {
        var expression = new NotNode(new IdentifierNode(field, 1), 1);

        FieldValue result = Evaluator.Evaluate(expression, TruthContext(), "IF");

        Assert.Equal(Bool(expected), result);
    }

    [Theory]
    [InlineData("LastName", "Fever", true)]
    [InlineData("Fever", "LastName", true)]
    [InlineData("LastName", "LastName", true)]
    [InlineData("LastName", "White", false)]
    [InlineData("Age", "LastName", false)]
    public void Evaluate_And_IsTrueOnlyWhenBothOperandsAreTrue(string left, string right, bool expected)
    {
        var expression = new AndNode(new IdentifierNode(left, 1), new IdentifierNode(right, 1), 1);

        FieldValue result = Evaluator.Evaluate(expression, TruthContext(), "IF");

        Assert.Equal(Bool(expected), result);
    }

    // The operands of the NOT and AND truth cases: the text "True", the text "TRUE" with a soft hyphen, a checked and
    // an unchecked checkbox, a number and a date.
    private static EvaluationContext TruthContext() => new(
        FoodHistoryForm,
        new Dictionary<string, FieldValue>
        {
            ["LastName"] = Text("True"),
            ["Sex"] = Text("TR\u00ADUE"),
            ["Fever"] = Bool(true),
            ["White"] = Bool(false),
            ["Age"] = N(1),
            ["DateofInterview"] = Date(2011, 5, 11),
        });

    // Legacy NOT gives the text "true" or "false" (Rule_NotExp.cs:36-47), which parentheses pass through, so beside =
    // it is compared as text by Rule_CompareExp: trimmed and ignoring case against text (224-227), as "the left side
    // is not empty" against a boolean on the right (119-121), converted against a boolean on the left (182-185), and
    // as a string against a number (191-217). The last four rows guard NOT's boolean outside =.
    [Theory]
    [InlineData("\"false\" = (NOT \"true\")", null, null, "T")]
    [InlineData("(NOT \"true\") = \" false \"", null, null, "T")]
    [InlineData("(NOT \"true\") = \"FALSE\"", null, null, "T")]
    [InlineData("(NOT LastName) = (-)", "x", null, "F")] // the text "true" is not empty, so it does not equal (-)
    [InlineData("(NOT LastName) = Fever", "true", true, "T")] // the text "false" is not empty: equals a checked box
    [InlineData("Fever = (NOT LastName)", "true", false, "T")] // the text "false" converts to false
    [InlineData("(NOT LastName) = 1", "x", null, "F")] // "true" is not a number, so the string forms differ
    [InlineData("NOT LastName", "x", null, "T")]
    [InlineData("NOT LastName", "true", null, "F")]
    [InlineData("(NOT LastName) AND Sex = \"1\"", "x", null, "T")]
    [InlineData("(NOT LastName) AND Sex = \"1\"", "true", null, "F")]
    public void IfNotBesideEquals_ComparesTheLegacyText(string condition, string? lastName, bool? fever, string branch)
    {
        string program = $$"""
            Field FeverTemp
                After
                    IF {{condition}} THEN
                        DIALOG "T"
                    ELSE
                        DIALOG "F"
                    END-IF
                    DIALOG "post"
                End-After
            End-Field
            """;

        FieldEventResponse response = Run(
            program,
            FoodHistoryForm,
            FieldEventKind.FieldAfter,
            "FeverTemp",
            ("LastName", TextOrNull(lastName)),
            ("Fever", fever is { } isChecked ? Bool(isChecked) : FieldValue.Null),
            ("Sex", Text("1")));

        Assert.Equal(new[] { branch, "post" }, response.Dialogs.Select(dialog => dialog.Message));
        AssertNoFieldEffects(response);
        Assert.Empty(response.Diagnostics);
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

    [Fact]
    public void GetGroupMembers_ReadsTheListAsCanonicalFieldsOnceInFirstOccurrenceOrder()
    {
        // Each List item resolves case-insensitively to its canonical metaFields.Name; unknown and empty items are
        // dropped, a repeated member keeps its first place, and a member group is returned without its own members.
        var context = new EvaluationContext(MessyGroupForm, new Dictionary<string, FieldValue>());

        IReadOnlyList<FieldMetadata> members = context.GetGroupMembers(MessyGroupForm.FindField("Symptoms")!);

        Assert.Equal(new[] { "Chills", "Fever", "Heading", "Signs" }, members.Select(member => member.Name));
    }

    [Theory]
    [InlineData("Cough")] // a checkbox without a List
    [InlineData("WorstSymptom")] // an Option whose List names fields of the form
    public void GetGroupMembers_OfAFieldThatIsNotAGroup_IsEmpty(string field)
    {
        var context = new EvaluationContext(MessyGroupForm, new Dictionary<string, FieldValue>());

        Assert.Empty(context.GetGroupMembers(MessyGroupForm.FindField(field)!));
    }

    [Theory]
    [InlineData("ENABLE")]
    [InlineData("DISABLE")]
    public void EnablementOfAGroup_TargetsOnlyItsCanonicalMembersOnItsOwnAxis(string command)
    {
        // GuiMediator.IEnterCheckCode.cs:2086-2220: ENABLE and DISABLE take the named field, then each member once in
        // List order whatever its kind, so the label Heading and the group Signs are targets and Signs' member Rash
        // is not. The group's own entry is left unpinned.
        string program = $"""
            Field Cough
                Click
                    {command} Symptoms
                End-Click
            End-Field
            """;

        FieldEventResponse response = Run(
            program,
            MessyGroupForm,
            FieldEventKind.FieldClick,
            "Cough",
            ("Fever", Bool(true)),
            ("Chills", Bool(false)),
            ("Cough", Bool(true)),
            ("Rash", Bool(true)));

        (IReadOnlyList<string> affected, IReadOnlyList<string> other) = command switch
        {
            "ENABLE" => (response.Enabled, response.Disabled),
            "DISABLE" => (response.Disabled, response.Enabled),
            _ => throw new ArgumentOutOfRangeException(nameof(command), command, "Not an enablement statement."),
        };
        Assert.Equal(new[] { "Chills", "Fever", "Heading", "Signs" }, affected.Where(name => name != "Symptoms"));
        Assert.Empty(other);
        Assert.Empty(response.Cleared);
        Assert.Empty(response.Assigned);
        Assert.Empty(response.Dialogs);
        Assert.Empty(response.Diagnostics);
    }

    [Fact]
    public void ClearOfAGroup_ClearsOnlyItsDataMembers()
    {
        // GuiMediator.IEnterCheckCode.cs:1631-1694: CLEAR of a group clears its IInputField members only, so the
        // group, its label Heading, its member group Signs, Signs' member Rash and Cough, outside it, keep their state.
        const string Program = """
            Field Cough
                Click
                    CLEAR Symptoms
                End-Click
            End-Field
            """;

        FieldEventResponse response = Run(
            Program,
            MessyGroupForm,
            FieldEventKind.FieldClick,
            "Cough",
            ("Fever", Bool(true)),
            ("Chills", Bool(false)),
            ("Cough", Bool(true)),
            ("Rash", Bool(true)));

        Assert.Equal(new[] { "Chills", "Fever" }, response.Cleared);
        Assert.Empty(response.Enabled);
        Assert.Empty(response.Disabled);
        Assert.Empty(response.Assigned);
        Assert.Empty(response.Dialogs);
        Assert.Empty(response.Diagnostics);
    }

    [Fact]
    public void Run_WithAHandBuiltClearOfAGroup_ClearsItsDataMembersForLaterStatements()
    {
        // The CLEAR expansion without the parser. The IF reads the cleared Fever as missing and Cough, outside the
        // group, as still set.
        var feverIsMissing = new EqualsNode(new IdentifierNode("Fever", 4), LiteralNode.Missing(4), 4);
        var coughIsSet = new NotNode(new EqualsNode(new IdentifierNode("Cough", 4), LiteralNode.Missing(4), 4), 4);
        var block = new EventBlockNode(
            FieldEventKind.FieldClick,
            2,
            new StatementNode[]
            {
                new ClearStatementNode(new[] { "Symptoms" }, 3),
                new IfStatementNode(
                    new AndNode(feverIsMissing, coughIsSet, 4),
                    new StatementNode[] { new DialogStatementNode("Fever was cleared.", null, 5) },
                    null,
                    4),
            },
            Array.Empty<Diagnostic>());

        FieldEventResponse response = Evaluator.Run(
            MessyGroupForm,
            new[] { block },
            new Dictionary<string, FieldValue>
            {
                ["Fever"] = Bool(true),
                ["Chills"] = Bool(false),
                ["Cough"] = Bool(true),
                ["Rash"] = Bool(true),
            });

        Assert.Equal(new[] { "Chills", "Fever" }, response.Cleared);
        Assert.Equal(new DialogMessage("Fever was cleared.", null), Assert.Single(response.Dialogs));
        Assert.Empty(response.Enabled);
        Assert.Empty(response.Disabled);
        Assert.Empty(response.Assigned);
        Assert.Empty(response.Diagnostics);
    }

    [Fact]
    public void Run_PlacesAParsedBlocksStaticDiagnosticsAmongItsStatements_AndAnUnanchoredBlocksFirst()
    {
        // The ASSIGN fails at run time and the HIDE after it on the same line is skipped by the parser.
        const string Program = """
            Field Sex
                After
                    ASSIGN NoSuchField = 1 HIDE Pregnant
                End-After
            End-Field
            """;
        Assert.Equal("ASSIGN NoSuchField = 1 HIDE Pregnant", ProgramLine(Program, 3));
        EventBlockNode parsed = Assert.Single(
            Assert.Single(Parser.Parse(Lexer.Tokenize(Program), CaseReportForm).FieldBlocks).Events);
        var values = new Dictionary<string, FieldValue>();

        // The parser places the warning after the ASSIGN that starts before it, so the runtime error comes first.
        Assert.Equal(
            new[] { "ASSIGN", "HIDE" },
            Evaluator.Run(CaseReportForm, new[] { parsed }, values).Diagnostics.Select(d => d.Command));

        // A block built by hand, or rebuilt by a with expression that replaces its diagnostics, carries no placement:
        // it equals the parsed block, and its static diagnostics are all added before its first statement runs.
        EventBlockNode[] unanchored =
        [
            new EventBlockNode(parsed.Event, parsed.Line, parsed.Statements, parsed.Diagnostics),
            parsed with { Diagnostics = [.. parsed.Diagnostics] },
        ];
        Assert.All(
            unanchored,
            block =>
            {
                Assert.Equal(parsed, block);
                Assert.Equal(
                    new[] { "HIDE", "ASSIGN" },
                    Evaluator.Run(CaseReportForm, new[] { block }, values).Diagnostics.Select(d => d.Command));
            });
    }

    // A branch that does not run is never walked when no static diagnostic waits for a statement index: Execute
    // places none, a block without diagnostics has none, and an unanchored block adds its diagnostics before its first
    // statement. However deep the skipped THEN, the ELSE and the statement after the IF still run.
    [Theory]
    [InlineData("Execute")]
    [InlineData("Run")]
    [InlineData("Run with an unanchored diagnostic")]
    public void SkippedDeepBranch_DoesNotPreventTheStatementsAfterIt(string path)
    {
        // Far deeper than any call stack can recurse; built bottom-up, so building it recurses nowhere.
        const int Depth = 50_000;
        StatementNode deepest = new DialogStatementNode("Never shown", null, 4);
        for (var i = 0; i < Depth; i++)
        {
            deepest = new IfStatementNode(LiteralNode.FromNumber(1, 4), [deepest], null, 4);
        }

        // "1" = "2" is false, so the deep THEN is skipped and the ELSE runs.
        StatementNode[] statements =
        [
            new IfStatementNode(
                new EqualsNode(LiteralNode.FromString("1", 3), LiteralNode.FromString("2", 3), 3),
                [deepest],
                [new DialogStatementNode("Else", null, 5)],
                3),
            new DialogStatementNode("After", null, 6),
        ];
        var warning = new Diagnostic(DiagnosticSeverity.Warning, CaseReportForm.Name, 7, "HIDE", "Unsupported.");
        Diagnostic[] diagnostics = path == "Run with an unanchored diagnostic" ? [warning] : [];
        var values = new Dictionary<string, FieldValue>();

        FieldEventResponse ExecuteAlone()
        {
            var effects = new EffectAccumulator();
            Evaluator.Execute(statements, new EvaluationContext(CaseReportForm, values), effects);
            return effects.ToResponse();
        }

        FieldEventResponse? response = null;
        Exception? exception = Record.Exception(() => response = path switch
        {
            "Execute" => ExecuteAlone(),
            "Run" or "Run with an unanchored diagnostic" => Evaluator.Run(
                CaseReportForm,
                new[] { new EventBlockNode(FieldEventKind.FieldAfter, 2, statements, diagnostics) },
                values),
            _ => throw new ArgumentOutOfRangeException(nameof(path), path, "Not an evaluator entry point."),
        });

        Assert.Null(exception);
        Assert.NotNull(response);
        Assert.Equal(new[] { "Else", "After" }, response.Dialogs.Select(dialog => dialog.Message));
        Assert.Equal(diagnostics, response.Diagnostics);
        AssertNoFieldEffects(response);
    }

    // An ELSE branch is numbered after every statement of its THEN branch, a nested IF and its own ELSE included, so
    // a construct the parser skips in the ELSE is placed exactly among the statements there: after the ASSIGN that
    // fails at run time when it follows it on the line, and before it when it precedes it. A THEN sized one short or
    // one long would swap one of the two orders.
    [Theory]
    [InlineData("ASSIGN NoSuchField = 1 HIDE Pregnant", new[] { "ASSIGN", "HIDE" })]
    [InlineData("HIDE Pregnant ASSIGN NoSuchField = 1", new[] { "HIDE", "ASSIGN" })]
    public void Run_NumbersAnElseBranchAfterTheWholeThenBranch(string elseLine, string[] commands)
    {
        string program = $"""
            Field Sex
                After
                    IF Sex = "1" THEN
                        IF Sex = "1" THEN
                            ENABLE Pregnant
                        ELSE
                            DISABLE Pregnant
                        END-IF
                        ENABLE Pregnant
                    ELSE
                        {elseLine}
                    END-IF
                End-After
            End-Field
            """;
        Assert.Equal(elseLine, ProgramLine(program, 11));
        EventBlockNode parsed = Assert.Single(
            Assert.Single(Parser.Parse(Lexer.Tokenize(program), CaseReportForm).FieldBlocks).Events);

        // Sex is empty, so the outer THEN, four statements in pre-order, is skipped and the ELSE runs.
        FieldEventResponse response = Evaluator.Run(
            CaseReportForm,
            new[] { parsed },
            new Dictionary<string, FieldValue>());

        Assert.Equal(commands, response.Diagnostics.Select(diagnostic => diagnostic.Command));
        AssertNoFieldEffects(response);
    }

    [Theory]
    [MemberData(nameof(TypedAssignments), DisableDiscoveryEnumeration = true)]
    public void Assign_StoresTheValueTypedByTheTargetField(
        FormMetadata form, string statement, string field, FieldValue expected, (string, FieldValue)[] values)
    {
        string program = $"""
            Field LastName
                After
                    {statement}
                End-After
            End-Field
            """;

        FieldEventResponse response = Run(program, form, FieldEventKind.FieldAfter, "LastName", values);

        Assert.Equal(new[] { field }, response.Assigned.Keys);
        Assert.Equal(expected, response.Assigned[field]);
        Assert.Empty(response.Cleared);
        Assert.Empty(response.Diagnostics);
    }

    [Theory]
    [MemberData(nameof(RejectedAssignments), DisableDiscoveryEnumeration = true)]
    public void Assign_WithNoExactConversion_ReportsAnErrorAndKeepsRunning(
        string statement, string target, string valueText, (string, FieldValue)[] values)
    {
        string program = $"""
            Field LastName
                After
                    {statement}
                    ASSIGN Remarks = "after"
                End-After
            End-Field
            """;
        Assert.Equal(statement, ProgramLine(program, 3));

        FieldEventResponse response = RunWithoutThrowing(
            program,
            TypedForm,
            FieldEventKind.FieldAfter,
            "LastName",
            values);

        Diagnostic diagnostic = Assert.Single(response.Diagnostics);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("ASSIGN", diagnostic.Command);
        Assert.Equal(3, diagnostic.Line);
        Assert.Equal("SyntheticTypes", diagnostic.Form);
        Assert.False(string.IsNullOrWhiteSpace(diagnostic.Message));
        Assert.DoesNotContain(valueText, diagnostic.Message, StringComparison.Ordinal);

        // The failing statement assigns nothing, and the statement after it still runs.
        Assert.DoesNotContain(target, response.Assigned.Keys);
        Assert.Equal(new[] { "Remarks" }, response.Assigned.Keys);
        Assert.Equal(Text("after"), response.Assigned["Remarks"]);
        Assert.Empty(response.Cleared);
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
    [MemberData(nameof(IfConditionCases), DisableDiscoveryEnumeration = true)]
    public void Run_WithAHandBuiltIf_RunsThenOnlyForATrueCondition(
        FormMetadata form, string conditionField, FieldValue? value, bool runsThen)
    {
        // A bare field as the condition, built without the parser, so its value reaches the IF as it is. Each
        // branch enables or disables that same field, which every form here has.
        var values = new Dictionary<string, FieldValue>();
        if (value is { } present)
        {
            values.Add(conditionField, present);
        }

        var statement = new IfStatementNode(
            new IdentifierNode(conditionField, 3),
            new StatementNode[] { new EnableStatementNode(new[] { conditionField }, 4) },
            new StatementNode[] { new DisableStatementNode(new[] { conditionField }, 6) },
            3);
        var block = new EventBlockNode(
            FieldEventKind.FieldAfter,
            2,
            new StatementNode[] { statement },
            Array.Empty<Diagnostic>());

        FieldEventResponse response = Evaluator.Run(form, new[] { block }, values);

        string[] thenEffect = runsThen ? [conditionField] : [];
        string[] elseEffect = runsThen ? [] : [conditionField];
        Assert.Equal(thenEffect, response.Enabled);
        Assert.Equal(elseEffect, response.Disabled);
        Assert.Empty(response.Cleared);
        Assert.Empty(response.Assigned);
        Assert.Empty(response.Diagnostics);
    }

    [Theory]
    [MemberData(nameof(ValuelessConditionCases), DisableDiscoveryEnumeration = true)]
    public void Run_WithAHandBuiltIfWhoseConditionHasNoValue_ReportsAnErrorAndRunsNeitherBranch(
        ExpressionNode condition)
    {
        // No field has a value. The ENABLE before the IF must stand and the one after it must still run.
        var statements = new StatementNode[]
        {
            new EnableStatementNode(new[] { "DOB" }, 2),
            new IfStatementNode(
                condition,
                new StatementNode[] { new EnableStatementNode(new[] { "Age" }, 4) },
                new StatementNode[] { new DisableStatementNode(new[] { "Age" }, 6) },
                3),
            new EnableStatementNode(new[] { "LastName" }, 8),
        };
        var block = new EventBlockNode(FieldEventKind.FieldAfter, 1, statements, Array.Empty<Diagnostic>());

        FieldEventResponse response = Evaluator.Run(
            FoodHistoryForm,
            new[] { block },
            new Dictionary<string, FieldValue>());

        AssertMissingConditionValueError(response, 3);
        Assert.Equal(new[] { "DOB", "LastName" }, response.Enabled);
        Assert.Empty(response.Disabled);
        Assert.Empty(response.Cleared);
        Assert.Empty(response.Assigned);
    }

    [Theory]
    [MemberData(nameof(ValuelessOperandCases), DisableDiscoveryEnumeration = true)]
    public void NotAndAnd_WithAnOperandThatHasNoValue_ReportAnErrorAndRunNeitherBranch(
        string condition, (string, FieldValue)[] values)
    {
        string program = ConditionProgram(condition);
        Assert.Equal($"IF {condition} THEN", ProgramLine(program, ConditionLine));

        FieldEventResponse response = RunWithoutThrowing(
            program,
            FoodHistoryForm,
            FieldEventKind.FieldAfter,
            "Sex",
            values);

        AssertMissingConditionValueError(response, ConditionLine);
        Assert.Equal(new[] { "DOB", "OnsetDate" }, response.Enabled);
        Assert.Empty(response.Disabled);
        Assert.Empty(response.Cleared);
        Assert.Empty(response.Assigned);
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
    public void Run_OnOneEngine_GivesEachCallItsOwnInputsAndEffects()
    {
        // One engine runs one block three times. The first call carries LastName and assigns Age, the second carries
        // neither, so either value surviving the first call sends the second to the ELSE branch or drops its dialog.
        // The third call repeats the first call's inputs and must answer exactly as the first did.
        const string Program = """
            Field Sex
                After
                    IF Age = (.) AND Sex = "1" THEN
                        ENABLE Pregnant
                    ELSE
                        DISABLE Pregnant
                        CLEAR Pregnant
                        ASSIGN Age = 30
                    END-IF
                    IF LastName = (.) THEN
                        DIALOG "no name"
                    END-IF
                End-After
            End-Field
            """;
        ICheckCodeEngine engine = new CheckCodeEngine();
        KeyValuePair<string, FieldValue>[] firstInput = [new("Sex", Text("2")), new("LastName", Text("Smith"))];
        KeyValuePair<string, FieldValue>[] secondInput = [new("Sex", Text("1"))];
        var firstValues = new Dictionary<string, FieldValue>(firstInput);
        var secondValues = new Dictionary<string, FieldValue>(secondInput);
        var thirdValues = new Dictionary<string, FieldValue>(firstInput);

        FieldEventResponse first = RunSexAfter(firstValues);
        FieldEventResponse second = RunSexAfter(secondValues);
        FieldEventResponse third = RunSexAfter(thirdValues);

        AssertFirstCallResponse(first);
        Assert.Equal(new[] { "Pregnant" }, second.Enabled);
        Assert.Empty(second.Disabled);
        Assert.Empty(second.Cleared);
        Assert.Empty(second.Assigned);
        Assert.Equal(new DialogMessage("no name", null), Assert.Single(second.Dialogs));
        Assert.Empty(second.Diagnostics);
        AssertFirstCallResponse(third);
        Assert.Equal(firstInput, firstValues);
        Assert.Equal(secondInput, secondValues);
        Assert.Equal(firstInput, thirdValues);

        FieldEventResponse RunSexAfter(Dictionary<string, FieldValue> values) =>
            engine.Run(Program, CaseReportForm, FieldEventKind.FieldAfter, "Sex", values);

        static void AssertFirstCallResponse(FieldEventResponse response)
        {
            Assert.Empty(response.Enabled);
            Assert.Equal(new[] { "Pregnant" }, response.Disabled);
            Assert.Equal(new[] { "Pregnant" }, response.Cleared);
            Assert.Equal(new[] { ("Age", N(30)) }, response.Assigned.Select(entry => (entry.Key, entry.Value)));
            Assert.Empty(response.Dialogs);
            Assert.Empty(response.Diagnostics);
        }
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

    // A FoodHistory Sex After block whose IF at ConditionLine enables Age for THEN and disables it for ELSE, between
    // an ENABLE before it and one after it.
    private static string ConditionProgram(string condition) => $"""
        Field Sex
            After
                ENABLE DOB
                IF {condition} THEN
                    ENABLE Age
                ELSE
                    DISABLE Age
                END-IF
                ENABLE OnsetDate
            End-After
        End-Field
        """;

    private static void AssertMissingConditionValueError(FieldEventResponse response, int line)
    {
        Diagnostic diagnostic = Assert.Single(response.Diagnostics);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("IF", diagnostic.Command);
        Assert.Equal(line, diagnostic.Line);
        Assert.Equal("FoodHistory", diagnostic.Form);
        Assert.Equal(MissingConditionValueReason, diagnostic.Message);
    }

    private static void AssertNoFieldEffects(FieldEventResponse response)
    {
        Assert.Empty(response.Enabled);
        Assert.Empty(response.Disabled);
        Assert.Empty(response.Cleared);
        Assert.Empty(response.Assigned);
    }

    // The checkbox-against-text cases: the IF picks a dialog, and the DIALOG after it shows that the statement
    // after the IF still runs. Line 3 is the IF.
    private static string CheckboxAgainstTextProgram(string text) => $$"""
        Field FeverTemp
            After
                IF Fever = "{{text}}" THEN
                    DIALOG "T"
                ELSE
                    DIALOG "F"
                END-IF
                DIALOG "post"
            End-After
        End-Field
        """;

    private static FieldValue Text(string value) => FieldValue.FromText(value);

    private static FieldValue TextOrNull(string? value) => value is null ? FieldValue.Null : FieldValue.FromText(value);

    private static FieldValue N(double value) => FieldValue.FromNumber(value);

    private static FieldValue Bool(bool value) => FieldValue.FromBoolean(value);

    private static FieldValue Date(int year, int month, int day) => FieldValue.FromDate(new DateOnly(year, month, day));
}
