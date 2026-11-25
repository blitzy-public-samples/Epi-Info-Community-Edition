using System.Globalization;
using EpiInfo.Domain.Diagnostics;
using EpiInfo.Domain.Forms;
using EpiInfo.Domain.Metadata;
using EpiInfo.Domain.Records;
using EpiInfo.Domain.Validation;
using EpiInfo.Domain.Values;

namespace EpiInfo.Domain.Tests.Validation;

/// <summary>
/// Unit tests of <see cref="RecordValidator"/>, the server's one write gate, together with
/// <see cref="RecordValidationResult"/> and <see cref="ValidationMessages"/>: the phase-1 input checks of every
/// <c>POST</c> and <c>PUT</c>, the empty-record rule and required gate of a <c>POST</c>, and the required gate of a
/// <c>PUT</c> over the stored record.
/// </summary>
/// <remarks>
/// <para>
/// These are the Domain half of SC-6; <c>tests/EpiInfo.Api.Tests/Validation/ValidationTests.cs</c> repeats the seeded
/// cases over HTTP against the database. Here every form is synthetic metadata and no database is used.
/// <c>MirrorForm</c> copies the seeded fields of <c>testdata/sql/03_forms.sql</c> whose rules the validator enforces;
/// the other forms cover what no seeded field has: required fields, a field without a prompt, and integer, Option,
/// <c>ntext</c> and unbounded <c>datetime</c> columns.
/// </para>
/// <para>
/// Submitted values use the raw JSON-scalar kinds the API's values reader hands over (text, number, boolean or
/// null), so a date arrives as <c>YYYY-MM-DD</c> text and an option index as a number. Normalised values carry the
/// kind of their field type: text, number, date, boolean or option index.
/// </para>
/// </remarks>
public sealed class RecordValidatorTests
{
    // metaFieldTypes ids as seeded by testdata/sql/02_reference_data.sql; SeededTypeName gives their names.
    private const int TextType = 1;
    private const int LabelType = 2;
    private const int MultilineType = 4;
    private const int NumberType = 5;
    private const int PhoneNumberType = 6;
    private const int DateType = 7;
    private const int CheckboxType = 10;
    private const int YesNoType = 11;
    private const int OptionType = 12;
    private const int LegalValuesType = 17;
    private const int GroupType = 21;
    private const int RecStatusType = 22;
    private const int UniqueKeyType = 23;
    private const int GlobalRecordIdType = 26;

    private const string PatientNamePrompt = "Patient's Name";
    private const string CollectionDatePrompt = "Collection date:";
    private const string PhoneMask = "###-###-####";
    private const string Sentinel = "SENTINEL-123456";

    /// <summary>
    /// The rows of the seeded code table <c>codephvs_administrativesex_hl7_2x</c>
    /// (<c>testdata/sql/04_code_tables.sql</c>), in insert order; value and label are both the stored text.
    /// </summary>
    private static readonly IReadOnlyList<FieldOption> SexOptions = Array.AsReadOnly(
        new[] { "F-Female", "M-Male", "U-Unknown" }
            .Select(text => new FieldOption(FieldValue.FromText(text), text))
            .ToArray());

    // FormDefinitionFactory.Create keys the options of a Legal Values field by the field's name.
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<FieldOption>> MirrorLegalValues =
        new Dictionary<string, IReadOnlyList<FieldOption>>(StringComparer.Ordinal) { ["Sex"] = SexOptions };

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<FieldOption>> NoLegalValues =
        new Dictionary<string, IReadOnlyList<FieldOption>>(StringComparer.Ordinal);

    /// <summary>
    /// Fixture A: the seeded fields whose rules the validator enforces, with their seeded metadata. <c>LastName</c>
    /// (prompt as on <c>FoodHistory</c>), <c>Zip</c>, <c>PhoneNumber</c>, <c>Age</c>, <c>Sex</c> and
    /// <c>StateIDNumber</c> come from <c>CaseReport</c>; <c>DateofInterview</c>, <c>Asian</c>, <c>FeverTemp</c> and
    /// <c>OnsetWeek</c> from <c>FoodHistory</c>. <c>FreeNumber</c> carries the blank bounds and pattern of the seeded
    /// <c>FoodHistory</c> <c>Age</c> row, and <c>Ill</c> is a <c>Yes/No</c> field on <c>tinyint</c>, which maps to
    /// unsupported. A label, a group and the three system rows complete the form. No field is required.
    /// </summary>
    private static readonly FormMetadata MirrorForm = new(
        ViewId: 1,
        Name: "Mirror",
        BaseTableName: "Mirror",
        Pages: [new PageMetadata(1, "Page 1", 0, "Mirror1"), new PageMetadata(2, "Page 2", 1, "Mirror2")],
        Fields:
        [
            Row(1, "UniqueKey", UniqueKeyType, pageId: null),
            Row(2, "RecStatus", RecStatusType, pageId: null),
            Row(3, "GlobalRecordId", GlobalRecordIdType, pageId: null),
            Row(4, "Title1", LabelType, pageId: 1),
            Row(5, "NameGroup", GroupType, pageId: 1) with { List = "LastName,Zip" },
            Row(6, "LastName", TextType, pageId: 1, Nvarchar(4000)) with { Prompt = "Last Name" },
            Row(7, "Zip", TextType, pageId: 1, Nvarchar(4000)) with { MaxLength = 5 },
            Row(8, "PhoneNumber", PhoneNumberType, pageId: 1, Nvarchar(20)) with { Pattern = PhoneMask },
            Row(9, "Age", NumberType, pageId: 1, FloatColumn) with { Lower = "0", Upper = "120", Pattern = "###" },
            Row(10, "Sex", LegalValuesType, pageId: 1, Nvarchar(255)) with
            {
                SourceTableName = "codephvs_administrativesex_hl7_2x",
                TextColumnName = "phvs_administrativesex_hl7_2x",
                Sort = false,
            },
            Row(11, "StateIDNumber", TextType, pageId: 1, Nvarchar(4000)) with { IsReadOnly = true },
            Row(12, "DateofInterview", DateType, pageId: 2, DatetimeColumn) with
            {
                Lower = "01-15-2011",
                Upper = "12-31-2012",
                Pattern = "MM-DD-YYYY",
            },
            Row(13, "Asian", CheckboxType, pageId: 2, BitColumn),
            Row(14, "FeverTemp", NumberType, pageId: 2, FloatColumn) with
            {
                Lower = "80",
                Upper = "110",
                Pattern = "###.#",
            },
            Row(15, "OnsetWeek", NumberType, pageId: 2, FloatColumn) with { IsReadOnly = true },
            Row(16, "FreeNumber", NumberType, pageId: 2, FloatColumn) with { Lower = "", Upper = "", Pattern = "" },
            Row(17, "Ill", YesNoType, pageId: 2, TinyIntColumn),
        ]);

    /// <summary>
    /// Fixture B: two required fields on different pages, because no seeded field has <c>IsRequired = 1</c>, plus an
    /// optional text field and an optional checkbox.
    /// </summary>
    private static readonly FormMetadata RequiredForm = new(
        ViewId: 2,
        Name: "Required",
        BaseTableName: "Required",
        Pages: [new PageMetadata(1, "Page 1", 0, "Required1"), new PageMetadata(2, "Page 2", 1, "Required2")],
        Fields:
        [
            Row(1, "PatientName", TextType, pageId: 1, Nvarchar(4000)) with
            {
                Prompt = PatientNamePrompt,
                IsRequired = true,
            },
            Row(2, "Notes", TextType, pageId: 1, Nvarchar(4000)),
            Row(3, "CollectionDate", DateType, pageId: 2, DatetimeColumn) with
            {
                Prompt = CollectionDatePrompt,
                IsRequired = true,
            },
            Row(4, "Flag", CheckboxType, pageId: 2, BitColumn),
        ]);

    /// <summary>Fixture C: a required field whose prompt is NULL, and an optional one.</summary>
    private static readonly FormMetadata NullPromptForm = new(
        ViewId: 3,
        Name: "NullPrompt",
        BaseTableName: "NullPrompt",
        Pages: [new PageMetadata(1, "Page 1", 0, "NullPrompt1")],
        Fields:
        [
            Row(1, "CaseCode", TextType, pageId: 1, Nvarchar(4000)) with { IsRequired = true },
            Row(2, "Other", TextType, pageId: 1, Nvarchar(4000)),
        ]);

    /// <summary>
    /// Fixture D: the storage ranges of the admitted column types no seeded field exercises: integer number columns,
    /// Option fields with and without items, an unbounded <c>datetime</c> date, <c>nvarchar(4000)</c> and <c>ntext</c>.
    /// </summary>
    private static readonly FormMetadata StorageForm = new(
        ViewId: 4,
        Name: "Storage",
        BaseTableName: "Storage",
        Pages: [new PageMetadata(1, "Page 1", 0, "Storage1")],
        Fields:
        [
            Row(1, "SmallCount", NumberType, pageId: 1, SmallIntColumn),
            Row(2, "Choice", OptionType, pageId: 1, TinyIntColumn) with { List = "A,B,C" },
            Row(3, "EmptyChoice", OptionType, pageId: 1, SmallIntColumn),
            Row(4, "WholeCount", NumberType, pageId: 1, IntColumn),
            Row(5, "EventDate", DateType, pageId: 1, DatetimeColumn),
            Row(6, "LongText", TextType, pageId: 1, Nvarchar(4000)),
            Row(7, "Memo", MultilineType, pageId: 1, NTextColumn),
        ]);

    // Column catalogue facts as sys.columns.max_length reports them, in bytes: nvarchar(n) is 2·n.
    private static (string SqlType, int MaxLengthBytes) FloatColumn => ("float", 8);

    private static (string SqlType, int MaxLengthBytes) DatetimeColumn => ("datetime", 8);

    private static (string SqlType, int MaxLengthBytes) BitColumn => ("bit", 1);

    private static (string SqlType, int MaxLengthBytes) TinyIntColumn => ("tinyint", 1);

    private static (string SqlType, int MaxLengthBytes) SmallIntColumn => ("smallint", 2);

    private static (string SqlType, int MaxLengthBytes) IntColumn => ("int", 4);

    private static (string SqlType, int MaxLengthBytes) NTextColumn => ("ntext", 16);

    public static TheoryData<string, string[]> FixtureDataFields => new()
    {
        {
            "Mirror",
            [
                "LastName", "Zip", "PhoneNumber", "Age", "Sex", "StateIDNumber",
                "DateofInterview", "Asian", "FeverTemp", "OnsetWeek", "FreeNumber",
            ]
        },
        { "Required", ["PatientName", "Notes", "CollectionDate", "Flag"] },
        { "NullPrompt", ["CaseCode", "Other"] },
        { "Storage", ["SmallCount", "Choice", "EmptyChoice", "WholeCount", "EventDate", "LongText", "Memo"] },
    };

    /// <summary>
    /// A fixture mistake must fail here rather than skew the cases below: each form has exactly the data fields the
    /// cases rely on, in storage order, and the definition built from it lists the same ones.
    /// </summary>
    [Theory]
    [MemberData(nameof(FixtureDataFields))]
    public void Fixture_HasExactlyTheExpectedDataFields(string formName, string[] expected)
    {
        var form = FixtureForm(formName);
        var definition = FormDefinitionFactory.Create(form, LegalValuesOf(form), Array.Empty<Diagnostic>());

        Assert.Equal(expected, FormDefinitionFactory.GetDataFields(form).Select(field => field.Name));
        Assert.Equal(
            expected,
            definition.Pages
                .SelectMany(page => page.Fields)
                .Where(field => FieldTypeMapper.IsDataFieldType(field.FieldType))
                .Select(field => field.Name));
        Assert.NotNull(new RecordValidator(form, definition));
    }

    [Fact]
    public void MirrorForm_MapsTheSeededTypesTheCasesRelyOn()
    {
        var fields = FormDefinitionFactory.Create(MirrorForm, MirrorLegalValues, Array.Empty<Diagnostic>())
            .Pages
            .SelectMany(page => page.Fields)
            .ToDictionary(field => field.Name, StringComparer.Ordinal);

        Assert.Equal(FieldType.Text, fields["PhoneNumber"].FieldType);
        Assert.Equal(FieldType.LegalValues, fields["Sex"].FieldType);
        Assert.Equal(SexOptions, fields["Sex"].Options);
        Assert.Equal(FieldType.Unsupported, fields["Ill"].FieldType);
        Assert.Equal(FieldType.Label, fields["Title1"].FieldType);
        Assert.Equal(FieldType.Group, fields["NameGroup"].FieldType);
        Assert.Equal(new[] { "LastName", "Zip" }, fields["NameGroup"].Members);
        Assert.True(fields["StateIDNumber"].Validation.ReadOnly);
        Assert.True(fields["OnsetWeek"].Validation.ReadOnly);
        Assert.DoesNotContain("RecStatus", fields.Keys);
    }

    // ---- Phase 1: ValidateInput ----

    [Fact]
    public void ValidateInput_NullValues_IsRequestLevelFailure()
    {
        var result = MirrorValidator().ValidateInput(null);

        AssertRequestError(result, ValidationMessages.ValuesMustBeObject);
    }

    /// <summary>The <c>detail</c> texts of the two request-level 400s, as AAP 0.3.4.2 words them.</summary>
    [Fact]
    public void RequestLevelMessages_AreTheContractedTexts()
    {
        Assert.Equal("values must be an object", ValidationMessages.ValuesMustBeObject);
        Assert.Equal("the record has no values", ValidationMessages.RecordHasNoValues);
    }

    /// <summary>
    /// An empty <c>values</c> object passes the input checks; emptiness is a phase-2 rule of a create.
    /// </summary>
    [Fact]
    public void ValidateInput_EmptySubmission_IsValidWithNoValues()
    {
        var values = AssertValid(Validate(MirrorValidator()));

        Assert.Empty(values);
    }

    [Fact]
    public void ValidateInput_NameInAnyCase_IsKeyedByItsCanonicalName()
    {
        var values = AssertValid(Validate(MirrorValidator(), ("lastname", FieldValue.FromText("Smith"))));

        Assert.Equal(new[] { "LastName" }, values.Keys);
        Assert.Equal(FieldValue.FromText("Smith"), values["LASTNAME"]);
    }

    /// <summary>Only submitted fields are returned, in submission order, so a <c>PUT</c> stays a delta.</summary>
    [Fact]
    public void ValidateInput_ValidSubmission_HoldsOnlySubmittedFieldsInSubmissionOrder()
    {
        var values = AssertValid(Validate(
            MirrorValidator(),
            ("FeverTemp", FieldValue.FromNumber(98.6)),
            ("lastName", FieldValue.FromText("Smith")),
            ("Asian", FieldValue.FromBoolean(true))));

        Assert.Equal(new[] { "FeverTemp", "LastName", "Asian" }, values.Keys);
    }

    /// <summary>
    /// A name the form has no <c>metaFields</c> row for has no canonical spelling, so it is keyed as sent.
    /// </summary>
    [Fact]
    public void ValidateInput_UnknownName_IsFieldErrorKeyedAsSubmitted()
    {
        var result = Validate(MirrorValidator(), ("Bogus", FieldValue.FromText("x")));

        AssertOnlyFieldError(result, "Bogus", ValidationMessages.UnknownField);
    }

    [Theory]
    [InlineData("LastName")]
    [InlineData("lastname")]
    public void ValidateInput_SameFieldTwice_IsDuplicateErrorUnderCanonicalName(string secondName)
    {
        var result = Validate(
            MirrorValidator(),
            ("LastName", FieldValue.FromText("Smith")),
            (secondName, FieldValue.FromText("Jones")));

        AssertOnlyFieldError(result, "LastName", ValidationMessages.DuplicateField);
    }

    /// <summary>
    /// Callers can never supply a system column, a label, a group or an unsupported field (the <c>Yes/No</c> field
    /// on <c>tinyint</c>): none is a data field. A row the form does have is keyed by its canonical spelling.
    /// </summary>
    [Theory]
    [InlineData("RecStatus", "RecStatus")]
    [InlineData("uniquekey", "UniqueKey")]
    [InlineData("GlobalRecordId", "GlobalRecordId")]
    [InlineData("Ill", "Ill")]
    [InlineData("Title1", "Title1")]
    [InlineData("NameGroup", "NameGroup")]
    public void ValidateInput_NonDataField_IsRejectedUnderCanonicalName(string submittedName, string expectedKey)
    {
        var result = Validate(MirrorValidator(), (submittedName, FieldValue.FromText("1")));

        AssertOnlyFieldError(result, expectedKey, ValidationMessages.UnknownField);
    }

    /// <summary>C-3: presence alone rejects a read-only field, whatever the value, an explicit null included.</summary>
    [Theory]
    [InlineData("StateIDNumber", "x")]
    [InlineData("StateIDNumber", null)]
    [InlineData("OnsetWeek", 5d)]
    [InlineData("OnsetWeek", null)]
    public void ValidateInput_ReadOnlyFieldPresent_IsRejected(string field, object? raw)
    {
        var result = Validate(MirrorValidator(), (field, Wire(raw)));

        AssertOnlyFieldError(result, field, ValidationMessages.ReadOnlyField);
    }

    /// <summary>
    /// Legacy writes <c>NULL</c> for an empty string (<c>CollectedDataProvider.cs</c>, lines 1293-1300), so empty text
    /// on a text-family field is no value, and no length, pattern or legal-values check applies to it.
    /// </summary>
    [Theory]
    [InlineData("LastName")]
    [InlineData("Sex")]
    [InlineData("PhoneNumber")]
    [InlineData("Zip")]
    public void ValidateInput_EmptyTextOnTextFamilyField_BecomesNull(string field)
    {
        var values = AssertValid(Validate(MirrorValidator(), (field, FieldValue.FromText(string.Empty))));

        Assert.Equal(FieldValue.Null, values[field]);
    }

    /// <summary>An explicit null is kept, and no type, range, length or pattern check applies to it.</summary>
    [Theory]
    [InlineData("LastName")]
    [InlineData("Age")]
    [InlineData("DateofInterview")]
    [InlineData("Asian")]
    [InlineData("PhoneNumber")]
    [InlineData("Zip")]
    public void ValidateInput_ExplicitNull_StaysNull(string field)
    {
        var values = AssertValid(Validate(MirrorValidator(), (field, FieldValue.Null)));

        Assert.Equal(FieldValue.Null, values[field]);
    }

    /// <summary>
    /// Legacy normalises only <c>string.IsNullOrEmpty</c>: whitespace is data and nothing is trimmed.
    /// </summary>
    [Fact]
    public void ValidateInput_WhitespaceText_IsKeptAsEntered()
    {
        var values = AssertValid(Validate(MirrorValidator(), ("LastName", FieldValue.FromText("   "))));

        Assert.Equal(FieldValue.FromText("   "), values["LastName"]);
    }

    /// <summary>Normalisation is limited to the text family, so empty text elsewhere fails the type check.</summary>
    [Theory]
    [InlineData("Age", ValidationMessages.NumberExpected)]
    [InlineData("DateofInterview", ValidationMessages.DateExpected)]
    public void ValidateInput_EmptyTextOnNonTextField_FailsTheTypeCheck(string field, string expected)
    {
        var result = Validate(MirrorValidator(), (field, FieldValue.FromText(string.Empty)));

        AssertOnlyFieldError(result, field, expected);
    }


    [Fact]
    public void ValidateInput_TextFieldGivenNumber_IsTextExpected()
    {
        var result = Validate(MirrorValidator(), ("LastName", FieldValue.FromNumber(5)));

        AssertOnlyFieldError(result, "LastName", ValidationMessages.TextExpected);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ValidateInput_CheckboxGivenBoolean_IsKeptAsBoolean(bool raw)
    {
        var values = AssertValid(Validate(MirrorValidator(), ("Asian", FieldValue.FromBoolean(raw))));

        Assert.Equal(FieldValueKind.Boolean, values["Asian"].Kind);
        Assert.Equal(FieldValue.FromBoolean(raw), values["Asian"]);
    }

    [Theory]
    [InlineData("true")]
    [InlineData(1d)]
    public void ValidateInput_CheckboxGivenNonBoolean_IsBooleanExpected(object raw)
    {
        var result = Validate(MirrorValidator(), ("Asian", Wire(raw)));

        AssertOnlyFieldError(result, "Asian", ValidationMessages.BooleanExpected);
    }

    [Fact]
    public void ValidateInput_NumberGivenNumber_IsKeptAsNumber()
    {
        var values = AssertValid(Validate(MirrorValidator(), ("FeverTemp", FieldValue.FromNumber(98.6))));

        Assert.Equal(FieldValueKind.Number, values["FeverTemp"].Kind);
        Assert.Equal(FieldValue.FromNumber(98.6), values["FeverTemp"]);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("98")]
    [InlineData(true)]
    public void ValidateInput_NumberGivenNonNumber_IsNumberExpected(object raw)
    {
        var result = Validate(MirrorValidator(), ("FeverTemp", Wire(raw)));

        AssertOnlyFieldError(result, "FeverTemp", ValidationMessages.NumberExpected);
    }

    [Fact]
    public void ValidateInput_DateGivenIsoText_BecomesDate()
    {
        var values = AssertValid(Validate(MirrorValidator(), ("DateofInterview", FieldValue.FromText("2011-01-15"))));

        Assert.Equal(FieldValueKind.Date, values["DateofInterview"].Kind);
        Assert.Equal(FieldValue.FromDate(new DateOnly(2011, 1, 15)), values["DateofInterview"]);
    }

    /// <summary>Only a valid calendar date in exactly the form <c>YYYY-MM-DD</c> is a date on the wire.</summary>
    [Theory]
    [InlineData("01/15/2011")]
    [InlineData("2011-02-30")]
    [InlineData("2011-1-15")]
    [InlineData(20110115d)]
    public void ValidateInput_DateGivenOtherInput_IsDateExpected(object raw)
    {
        var result = Validate(MirrorValidator(), ("DateofInterview", Wire(raw)));

        AssertOnlyFieldError(result, "DateofInterview", ValidationMessages.DateExpected);
    }

    /// <summary>An option is the 0-based index of its item; <c>Choice</c> sits on a <c>tinyint</c> column.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ValidateInput_OptionIndexWithinList_BecomesIndex(int index)
    {
        var values = AssertValid(Validate(Validator(StorageForm), ("Choice", FieldValue.FromNumber(index))));

        Assert.Equal(FieldValueKind.Index, values["Choice"].Kind);
        Assert.Equal(FieldValue.FromIndex(index), values["Choice"]);
    }

    /// <summary>A whole number outside the three items of <c>A,B,C</c> names no item.</summary>
    [Theory]
    [InlineData(-1d)]
    [InlineData(3d)]
    public void ValidateInput_OptionWholeNumberOutsideList_IsOutOfRange(double raw)
    {
        var result = Validate(Validator(StorageForm), ("Choice", FieldValue.FromNumber(raw)));

        AssertOnlyFieldError(result, "Choice", ValidationMessages.FormatOptionIndexOutOfRange(3));
    }

    [Theory]
    [InlineData(1.5d)]
    [InlineData("1")]
    public void ValidateInput_OptionGivenNonWholeNumber_IsOptionIndexExpected(object raw)
    {
        var result = Validate(Validator(StorageForm), ("Choice", Wire(raw)));

        AssertOnlyFieldError(result, "Choice", ValidationMessages.OptionIndexExpected);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(1d)]
    public void ValidateInput_OptionFieldWithoutItems_RejectsEveryIndex(double raw)
    {
        var result = Validate(Validator(StorageForm), ("EmptyChoice", FieldValue.FromNumber(raw)));

        AssertOnlyFieldError(result, "EmptyChoice", ValidationMessages.NoOptionsAvailable);
    }

    // ---- Storage range: out-of-domain input is a field error, never a storage exception ----

    [Theory]
    [InlineData("SmallCount", 32767d)]
    [InlineData("SmallCount", -32768d)]
    [InlineData("WholeCount", 2147483647d)]
    public void ValidateInput_NumberWithinIntegerColumn_IsAccepted(string field, double raw)
    {
        var values = AssertValid(Validate(Validator(StorageForm), (field, FieldValue.FromNumber(raw))));

        Assert.Equal(FieldValue.FromNumber(raw), values[field]);
    }

    [Theory]
    [InlineData("SmallCount", 32768d)]
    [InlineData("SmallCount", -32769d)]
    [InlineData("SmallCount", 1.5d)]
    [InlineData("WholeCount", 2147483648d)]
    [InlineData("WholeCount", 1.5d)]
    public void ValidateInput_NumberOutsideIntegerColumn_IsNotStorable(string field, double raw)
    {
        var result = Validate(Validator(StorageForm), (field, FieldValue.FromNumber(raw)));

        AssertOnlyFieldError(result, field, ValidationMessages.NumberNotStorable);
    }

    [Theory]
    [InlineData("1753-01-01")]
    [InlineData("9999-12-31")]
    public void ValidateInput_DateWithinDatetimeRange_IsAccepted(string raw)
    {
        var values = AssertValid(Validate(Validator(StorageForm), ("EventDate", FieldValue.FromText(raw))));

        Assert.Equal(IsoDate(raw), values["EventDate"]);
    }

    [Theory]
    [InlineData("1752-12-31")]
    [InlineData("0001-01-01")]
    public void ValidateInput_DateBeforeDatetimeRange_IsNotStorable(string raw)
    {
        var result = Validate(Validator(StorageForm), ("EventDate", FieldValue.FromText(raw)));

        AssertOnlyFieldError(result, "EventDate", ValidationMessages.DateNotStorable);
    }

    [Fact]
    public void ValidateInput_TextOfColumnLength_IsAccepted()
    {
        var text = new string('a', 4000);

        var values = AssertValid(Validate(Validator(StorageForm), ("LongText", FieldValue.FromText(text))));

        Assert.Equal(FieldValue.FromText(text), values["LongText"]);
    }

    public static TheoryData<string> TextOneCodeUnitTooLong => new()
    {
        new string('a', 4001),

        // 3999 characters and one astral character, a surrogate pair: SQL Server counts nvarchar(n) in UTF-16
        // code units, so this is 4001 units.
        new string('a', 3999) + "\U0001F600",
    };

    [Theory]
    [MemberData(nameof(TextOneCodeUnitTooLong))]
    public void ValidateInput_TextBeyondColumnLength_IsTooLongForColumn(string text)
    {
        Assert.Equal(4001, text.Length);

        var result = Validate(Validator(StorageForm), ("LongText", FieldValue.FromText(text)));

        AssertOnlyFieldError(result, "LongText", ValidationMessages.FormatTextTooLongForColumn(4000));
    }

    [Fact]
    public void ValidateInput_TextOnNtext_IsUnbounded()
    {
        var text = new string('m', 10_000);

        var values = AssertValid(Validate(Validator(StorageForm), ("Memo", FieldValue.FromText(text))));

        Assert.Equal(FieldValue.FromText(text), values["Memo"]);
    }

    // ---- MaxLength, range, pattern and legal values ----

    [Fact]
    public void ValidateInput_TextOfMaxLength_IsAccepted()
    {
        var values = AssertValid(Validate(MirrorValidator(), ("Zip", FieldValue.FromText("12345"))));

        Assert.Equal(FieldValue.FromText("12345"), values["Zip"]);
    }

    [Fact]
    public void ValidateInput_TextBeyondMaxLength_IsMaxLengthExceeded()
    {
        var result = Validate(MirrorValidator(), ("Zip", FieldValue.FromText("123456")));

        AssertOnlyFieldError(result, "Zip", ValidationMessages.FormatMaxLengthExceeded(5));
    }

    [Theory]
    [InlineData("Age", 0d)]
    [InlineData("Age", 120d)]
    [InlineData("FeverTemp", 80d)]
    [InlineData("FeverTemp", 110d)]
    public void ValidateInput_NumberOnInclusiveBound_IsAccepted(string field, double raw)
    {
        var values = AssertValid(Validate(MirrorValidator(), (field, FieldValue.FromNumber(raw))));

        Assert.Equal(FieldValue.FromNumber(raw), values[field]);
    }


    /// <summary><c>FeverTemp</c> 112 is scenario <c>EC-VAL-02</c>.</summary>
    [Theory]
    [InlineData("Age", -1d, 0d, 120d)]
    [InlineData("Age", 121d, 0d, 120d)]
    [InlineData("FeverTemp", 79.9d, 80d, 110d)]
    [InlineData("FeverTemp", 110.1d, 80d, 110d)]
    [InlineData("FeverTemp", 112d, 80d, 110d)]
    public void ValidateInput_NumberOutsideRange_IsRangeError(string field, double raw, double lower, double upper)
    {
        var result = Validate(MirrorValidator(), (field, FieldValue.FromNumber(raw)));

        AssertOnlyFieldError(result, field, ValidationMessages.FormatRangeBetween(lower, upper));
    }

    /// <summary>The bounds are the stored <c>MM-DD-YYYY</c> texts <c>01-15-2011</c> and <c>12-31-2012</c>.</summary>
    [Theory]
    [InlineData("2011-01-15")]
    [InlineData("2012-12-31")]
    public void ValidateInput_DateOnInclusiveBound_IsAccepted(string raw)
    {
        AssertValid(Validate(MirrorValidator(), ("DateofInterview", FieldValue.FromText(raw))));
    }

    /// <summary><c>2011-01-01</c> is scenario <c>EC-VAL-01</c>.</summary>
    [Theory]
    [InlineData("2011-01-14")]
    [InlineData("2013-01-01")]
    [InlineData("2011-01-01")]
    public void ValidateInput_DateOutsideRange_IsRangeError(string raw)
    {
        var result = Validate(MirrorValidator(), ("DateofInterview", FieldValue.FromText(raw)));

        AssertOnlyFieldError(
            result,
            "DateofInterview",
            ValidationMessages.FormatRangeBetween(new DateOnly(2011, 1, 15), new DateOnly(2012, 12, 31)));
    }

    /// <summary>Blank stored bounds and a blank pattern are no rule at all.</summary>
    [Theory]
    [InlineData(500d)]
    [InlineData(12.5d)]
    public void ValidateInput_NumberWithBlankBoundsAndPattern_IsUnchecked(double raw)
    {
        AssertValid(Validate(MirrorValidator(), ("FreeNumber", FieldValue.FromNumber(raw))));
    }

    [Fact]
    public void ValidateInput_PhoneMatchingMask_IsAccepted()
    {
        var values = AssertValid(Validate(MirrorValidator(), ("PhoneNumber", FieldValue.FromText("555-014-8636"))));

        Assert.Equal(FieldValue.FromText("555-014-8636"), values["PhoneNumber"]);
    }

    /// <summary>
    /// <c>(555) 0148-6367</c> is a seeded phone value; the server rule is stronger than the legacy entry mask.
    /// </summary>
    [Theory]
    [InlineData("5550148636")]
    [InlineData("555-014-863")]
    [InlineData("(555) 0148-6367")]
    public void ValidateInput_PhoneNotMatchingMask_IsPatternMismatch(string raw)
    {
        var result = Validate(MirrorValidator(), ("PhoneNumber", FieldValue.FromText(raw)));

        AssertOnlyFieldError(result, "PhoneNumber", ValidationMessages.FormatPatternMismatch(PhoneMask));
    }

    /// <summary>Both numbers are within range, so only the digit budget of the mask rejects them.</summary>
    [Theory]
    [InlineData("Age", 12.5d, "###")]
    [InlineData("FeverTemp", 98.65d, "###.#")]
    public void ValidateInput_NumberBeyondMaskDigits_IsPatternMismatch(string field, double raw, string mask)
    {
        var result = Validate(MirrorValidator(), (field, FieldValue.FromNumber(raw)));

        AssertOnlyFieldError(result, field, ValidationMessages.FormatPatternMismatch(mask));
    }

    /// <summary>
    /// <c>MM-DD-YYYY</c> is a display and entry format for the client; ISO input is not checked against it.
    /// </summary>
    [Fact]
    public void ValidateInput_DatePattern_IsNotCheckedOnIsoInput()
    {
        AssertValid(Validate(MirrorValidator(), ("DateofInterview", FieldValue.FromText("2011-06-01"))));
    }

    [Theory]
    [InlineData("F-Female")]
    [InlineData("M-Male")]
    [InlineData("U-Unknown")]
    public void ValidateInput_LegalValue_IsAccepted(string raw)
    {
        var values = AssertValid(Validate(MirrorValidator(), ("Sex", FieldValue.FromText(raw))));

        Assert.Equal(FieldValue.FromText(raw), values["Sex"]);
    }

    /// <summary>
    /// <c>X-Other</c> is scenario <c>EC-VAL-03</c>. A legal value must equal an option exactly, so a code prefix
    /// and a different letter case are rejected too.
    /// </summary>
    [Theory]
    [InlineData("X-Other")]
    [InlineData("F")]
    [InlineData("f-female")]
    public void ValidateInput_TextOutsideLegalValues_IsNotALegalValue(string raw)
    {
        var result = Validate(MirrorValidator(), ("Sex", FieldValue.FromText(raw)));

        AssertOnlyFieldError(result, "Sex", ValidationMessages.NotALegalValue);
    }

    /// <summary>Every failing entry is reported in one result, in submission order.</summary>
    [Fact]
    public void ValidateInput_SeveralInvalidEntries_ReportsEveryOne()
    {
        var result = Validate(
            MirrorValidator(),
            ("Zip", FieldValue.FromText("123456")),
            ("Age", FieldValue.FromNumber(121)),
            ("LastName", FieldValue.FromText("Smith")),
            ("Sex", FieldValue.FromText("X-Other")),
            ("Bogus", FieldValue.FromText("x")));

        AssertFieldFailure(result);
        Assert.Equal(new[] { "Zip", "Age", "Sex", "Bogus" }, result.FieldErrors.Keys);
        Assert.Equal(new[] { ValidationMessages.FormatMaxLengthExceeded(5) }, result.FieldErrors["Zip"]);
        Assert.Equal(new[] { ValidationMessages.FormatRangeBetween(0d, 120d) }, result.FieldErrors["Age"]);
        Assert.Equal(new[] { ValidationMessages.NotALegalValue }, result.FieldErrors["Sex"]);
        Assert.Equal(new[] { ValidationMessages.UnknownField }, result.FieldErrors["Bogus"]);
    }

    /// <summary>
    /// The redaction policy (AAP 0.9.3): no message ever carries a submitted value, whichever check rejects it.
    /// </summary>
    [Fact]
    public void ValidateInput_RejectedValues_NeverAppearInAnyMessage()
    {
        var mirror = Validate(
            MirrorValidator(),
            ("Zip", FieldValue.FromText(Sentinel)),
            ("Age", FieldValue.FromText(Sentinel)),
            ("Sex", FieldValue.FromText(Sentinel)),
            ("PhoneNumber", FieldValue.FromText(Sentinel)));
        var storage = Validate(
            Validator(StorageForm),
            ("LongText", FieldValue.FromText(Sentinel + new string('a', 4001 - Sentinel.Length))));

        AssertFieldFailure(mirror);
        Assert.Equal(new[] { "Zip", "Age", "Sex", "PhoneNumber" }, mirror.FieldErrors.Keys);
        AssertFieldFailure(storage);
        Assert.Equal(new[] { "LongText" }, storage.FieldErrors.Keys);

        foreach (var result in new[] { mirror, storage })
        {
            Assert.Null(result.RequestError);
            Assert.All(
                result.FieldErrors.Values.SelectMany(messages => messages),
                message => Assert.DoesNotContain("SENTINEL", message, StringComparison.Ordinal));
        }
    }

    /// <summary>The required gate is a phase-2 rule: the input checks never report a missing required field.</summary>
    [Fact]
    public void ValidateInput_RequiredFieldsAbsent_IsValid()
    {
        var values = AssertValid(Validate(Validator(RequiredForm), ("Notes", FieldValue.FromText("x"))));

        Assert.Equal(new[] { "Notes" }, values.Keys);
    }


    // ---- Phase 2 of a POST: CheckNewRecord ----

    /// <summary>
    /// C-10: a create whose every value is null, an empty one included, is the request-level empty record.
    /// </summary>
    [Theory]
    [InlineData(new object[] { new string[0] })]
    [InlineData(new object[] { new[] { "LastName", "Age", "DateofInterview", "Asian", "Sex" } })]
    public void CheckNewRecord_EveryValueNull_IsEmptyRecord(string[] nullFields)
    {
        var normalised = Normalised(nullFields.Select(field => (field, FieldValue.Null)).ToArray());

        var result = MirrorValidator().CheckNewRecord(normalised);

        AssertRequestError(result, ValidationMessages.RecordHasNoValues);
    }

    /// <summary>
    /// C-10: an explicit <c>false</c> is a value, so a record of unchecked checkboxes only is not empty.
    /// </summary>
    [Fact]
    public void CheckNewRecord_UncheckedCheckboxOnly_IsValid()
    {
        var result = MirrorValidator().CheckNewRecord(Normalised(("Asian", FieldValue.FromBoolean(false))));

        var values = AssertValid(result);
        Assert.Equal(FieldValue.FromBoolean(false), values["Asian"]);
    }

    [Fact]
    public void CheckNewRecord_UncheckedCheckboxAndEnteredValue_IsValid()
    {
        var values = AssertValid(MirrorValidator().CheckNewRecord(Normalised(
            ("Asian", FieldValue.FromBoolean(false)),
            ("LastName", FieldValue.FromText("x")))));

        Assert.Equal(new[] { "Asian", "LastName" }, values.Keys);
    }

    /// <summary>Empty text normalised to null by the input checks leaves nothing to store.</summary>
    [Fact]
    public void CheckNewRecord_OnlyEmptyText_IsEmptyRecord()
    {
        var validator = MirrorValidator();
        var input = AssertValid(Validate(validator, ("LastName", FieldValue.FromText(string.Empty))));

        var result = validator.CheckNewRecord(input);

        AssertRequestError(result, ValidationMessages.RecordHasNoValues);
    }

    /// <summary>
    /// Every missing required field is listed, where legacy stopped at the first
    /// (<c>GuiMediator.Events.ViewExplorer.cs</c>, lines 58-139).
    /// </summary>
    [Fact]
    public void CheckNewRecord_RequiredFieldsAbsent_ListsEveryOne()
    {
        var result = Validator(RequiredForm).CheckNewRecord(Normalised(("Notes", FieldValue.FromText("x"))));

        AssertFieldFailure(result);
        Assert.Equal(new[] { "PatientName", "CollectionDate" }, result.FieldErrors.Keys);
        Assert.Equal(new[] { RequiredMessage(PatientNamePrompt, "Page 1") }, result.FieldErrors["PatientName"]);
        Assert.Equal(new[] { RequiredMessage(CollectionDatePrompt, "Page 2") }, result.FieldErrors["CollectionDate"]);
        Assert.Equal(
            ValidationMessages.FormatFieldIsRequired(PatientNamePrompt, "PatientName", "Page 1"),
            result.FieldErrors["PatientName"][0]);
        Assert.Equal(
            ValidationMessages.FormatFieldIsRequired(CollectionDatePrompt, "CollectionDate", "Page 2"),
            result.FieldErrors["CollectionDate"][0]);
    }

    [Fact]
    public void CheckNewRecord_OneRequiredFieldNull_ListsOnlyThatField()
    {
        var result = Validator(RequiredForm).CheckNewRecord(Normalised(
            ("PatientName", FieldValue.Null),
            ("CollectionDate", FieldValue.FromDate(new DateOnly(2011, 2, 1)))));

        AssertOnlyFieldError(result, "PatientName", RequiredMessage(PatientNamePrompt, "Page 1"));
    }

    [Fact]
    public void CheckNewRecord_RequiredFieldsSupplied_IsValid()
    {
        var values = AssertValid(Validator(RequiredForm).CheckNewRecord(Normalised(
            ("PatientName", FieldValue.FromText("Jones")),
            ("CollectionDate", FieldValue.FromDate(new DateOnly(2011, 2, 1))))));

        Assert.Equal(new[] { "PatientName", "CollectionDate" }, values.Keys);
    }

    /// <summary>
    /// The empty-record rule comes first: an all-null create is "the record has no values", the request-level 400
    /// AAP 0.3.4.2 names, not a list of missing required fields.
    /// </summary>
    [Fact]
    public void CheckNewRecord_AllNullOnFormWithRequiredFields_IsEmptyRecord()
    {
        var result = Validator(RequiredForm).CheckNewRecord(Normalised(
            ("PatientName", FieldValue.Null),
            ("Notes", FieldValue.Null),
            ("CollectionDate", FieldValue.Null),
            ("Flag", FieldValue.Null)));

        AssertRequestError(result, ValidationMessages.RecordHasNoValues);
    }

    /// <summary>A field without a prompt is named by its field name in the required message.</summary>
    [Fact]
    public void CheckNewRecord_RequiredFieldWithoutPrompt_IsNamedByFieldName()
    {
        var result = Validator(NullPromptForm).CheckNewRecord(Normalised(("Other", FieldValue.FromText("x"))));

        AssertOnlyFieldError(result, "CaseCode", ValidationMessages.FormatFieldIsRequired(null, "CaseCode", "Page 1"));
        Assert.Equal(RequiredMessage("CaseCode", "Page 1"), result.FieldErrors["CaseCode"][0]);
    }

    // ---- Phase 2 of a PUT: CheckRequired over the stored record ----

    /// <summary>
    /// The submitted values, explicit nulls included, are laid over the stored ones, and the required gate runs on the
    /// result. A null <paramref name="submittedField"/> is an empty submission; a null <paramref name="submittedText"/>
    /// submits an explicit null.
    /// </summary>
    [Theory]
    [InlineData("Smith", "2011-02-01", "Notes", "x", new string[0])]
    [InlineData("Smith", "2011-02-01", "PatientName", null, new[] { "PatientName" })]
    [InlineData(null, "2011-02-01", "PatientName", "Jones", new string[0])]
    [InlineData(null, "2011-02-01", null, null, new[] { "PatientName" })]
    [InlineData(null, null, null, null, new[] { "PatientName", "CollectionDate" })]
    public void CheckRequired_SubmittedOverStored_GatesTheEffectiveRecord(
        string? storedPatientName,
        string? storedCollectionDate,
        string? submittedField,
        string? submittedText,
        string[] expectedMissing)
    {
        var stored = Stored(Normalised(
            ("PatientName", storedPatientName is null ? FieldValue.Null : FieldValue.FromText(storedPatientName)),
            ("Notes", FieldValue.FromText("stored note")),
            ("CollectionDate", storedCollectionDate is null ? FieldValue.Null : IsoDate(storedCollectionDate)),
            ("Flag", FieldValue.FromBoolean(true))));
        var submitted = submittedField is null
            ? Normalised()
            : Normalised((
                submittedField,
                submittedText is null ? FieldValue.Null : FieldValue.FromText(submittedText)));

        var result = Validator(RequiredForm).CheckRequired(stored, submitted);

        if (expectedMissing.Length == 0)
        {
            var values = AssertValid(result);
            Assert.Equal(submitted.Keys, values.Keys);
            return;
        }

        AssertFieldFailure(result);
        Assert.Equal(expectedMissing, result.FieldErrors.Keys);
        foreach (var field in expectedMissing)
        {
            var expected = field == "PatientName"
                ? RequiredMessage(PatientNamePrompt, "Page 1")
                : RequiredMessage(CollectionDatePrompt, "Page 2");
            Assert.Equal(new[] { expected }, result.FieldErrors[field]);
        }
    }

    /// <summary>A <c>PUT</c> may clear fields: the empty-record rule of a create does not apply to it.</summary>
    [Fact]
    public void CheckRequired_ClearingAField_IsValid()
    {
        var stored = Stored(MirrorStoredValues(phoneNumber: FieldValue.FromText("555-014-8636")));

        var values = AssertValid(MirrorValidator().CheckRequired(stored, Normalised(("LastName", FieldValue.Null))));

        Assert.Equal(new[] { "LastName" }, values.Keys);
        Assert.Equal(FieldValue.Null, values["LastName"]);
    }

    /// <summary>
    /// Stored values the request does not touch are never re-validated, so a seeded phone value that breaks the mask
    /// stays editable (AAP 0.3.4.5); the result holds the submitted values only.
    /// </summary>
    [Fact]
    public void CheckRequired_StoredValueBreakingCurrentRules_IsNotRevalidated()
    {
        var stored = Stored(MirrorStoredValues(phoneNumber: FieldValue.FromText("(555) 0148-6367")));

        var result = MirrorValidator().CheckRequired(stored, Normalised(("LastName", FieldValue.FromText("x"))));

        var values = AssertValid(result);
        Assert.Equal(new[] { "LastName" }, values.Keys);
        Assert.Equal(FieldValue.FromText("x"), values["LastName"]);
    }

    /// <summary>
    /// C-12: the legacy <c>FIELD_IS_REQUIRED_LONG</c> text of <c>Epi.Core/SharedStrings.resx</c> line 638, trailing
    /// space included.
    /// </summary>
    [Fact]
    public void FieldIsRequiredLong_IsTheLegacyTextVerbatim()
    {
        Assert.Equal(
            "{0} is a required field (page {1}). Please fill in this field before attempting to leave the record, save data, or close the form. ",
            ValidationMessages.FieldIsRequiredLong);
        Assert.EndsWith("form. ", ValidationMessages.FieldIsRequiredLong, StringComparison.Ordinal);
    }


    // ---- Fixture and assertion helpers ----

    private static FormMetadata FixtureForm(string name) => name switch
    {
        "Mirror" => MirrorForm,
        "Required" => RequiredForm,
        "NullPrompt" => NullPromptForm,
        "Storage" => StorageForm,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "No fixture form has that name."),
    };

    private static IReadOnlyDictionary<string, IReadOnlyList<FieldOption>> LegalValuesOf(FormMetadata form) =>
        ReferenceEquals(form, MirrorForm) ? MirrorLegalValues : NoLegalValues;

    private static RecordValidator MirrorValidator() => Validator(MirrorForm, MirrorLegalValues);

    // The validator as the API builds it: over the definition the factory assembles from the same metadata.
    private static RecordValidator Validator(
        FormMetadata form,
        IReadOnlyDictionary<string, IReadOnlyList<FieldOption>>? legalValues = null) =>
        new(form, FormDefinitionFactory.Create(form, legalValues ?? NoLegalValues, Array.Empty<Diagnostic>()));

    // The entries of a values object in document order, repeats kept, as the API's values reader produces them.
    private static IReadOnlyList<KeyValuePair<string, FieldValue>> Submit(
        params (string Name, FieldValue Value)[] entries) =>
        Array.ConvertAll(entries, entry => new KeyValuePair<string, FieldValue>(entry.Name, entry.Value));

    // Runs the input checks, failing the test explicitly if they throw: a value the column cannot hold must come back
    // as a field error, never as an exception that would surface as a 500.
    private static RecordValidationResult Validate(
        RecordValidator validator,
        params (string Name, FieldValue Value)[] entries)
    {
        var submitted = Submit(entries);
        RecordValidationResult? result = null;

        var exception = Record.Exception(() => result = validator.ValidateInput(submitted));

        Assert.Null(exception);
        Assert.NotNull(result);
        return result;
    }

    // A map of normalised values, keyed as a valid ValidateInput result or the store's read keys them.
    private static IReadOnlyDictionary<string, FieldValue> Normalised(params (string Name, FieldValue Value)[] entries)
    {
        var values = new Dictionary<string, FieldValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in entries)
        {
            values.Add(name, value);
        }

        return values;
    }

    // The raw kind the API's values reader gives a JSON scalar: a string, a number, a boolean or null.
    private static FieldValue Wire(object? raw) => raw switch
    {
        null => FieldValue.Null,
        string text => FieldValue.FromText(text),
        bool boolean => FieldValue.FromBoolean(boolean),
        double number => FieldValue.FromNumber(number),
        int number => FieldValue.FromNumber(number),
        _ => throw new ArgumentException("A wire value is a string, a number, a boolean or null.", nameof(raw)),
    };

    private static FieldValue IsoDate(string text) =>
        FieldValue.FromDate(DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture));

    private static StoredRecord Stored(IReadOnlyDictionary<string, FieldValue> values) =>
        new(Guid.NewGuid().ToString(), UniqueKey: 1, RecStatus: 1, values);

    // Every data field of MirrorForm with a stored value; Zip, Age and Sex hold historical values that break today's
    // MaxLength, range and legal-values rules, as stored data may.
    private static IReadOnlyDictionary<string, FieldValue> MirrorStoredValues(FieldValue phoneNumber) => Normalised(
        ("LastName", FieldValue.FromText("Smith")),
        ("Zip", FieldValue.FromText("123456")),
        ("PhoneNumber", phoneNumber),
        ("Age", FieldValue.FromNumber(500)),
        ("Sex", FieldValue.FromText("X-Other")),
        ("StateIDNumber", FieldValue.FromText("S-1")),
        ("DateofInterview", FieldValue.FromDate(new DateOnly(2011, 6, 1))),
        ("Asian", FieldValue.FromBoolean(false)),
        ("FeverTemp", FieldValue.FromNumber(101.5)),
        ("OnsetWeek", FieldValue.FromNumber(22)),
        ("FreeNumber", FieldValue.Null));

    // The required message as legacy formats FIELD_IS_REQUIRED_LONG: {0} the prompt, {1} the page name.
    private static string RequiredMessage(string prompt, string pageName) =>
        string.Format(CultureInfo.InvariantCulture, ValidationMessages.FieldIsRequiredLong, prompt, pageName);

    private static IReadOnlyDictionary<string, FieldValue> AssertValid(RecordValidationResult result)
    {
        Assert.True(result.IsValid);
        Assert.Null(result.RequestError);
        Assert.Empty(result.FieldErrors);
        Assert.NotNull(result.Values);
        return result.Values;
    }

    private static void AssertRequestError(RecordValidationResult result, string expected)
    {
        Assert.False(result.IsValid);
        Assert.Equal(expected, result.RequestError);
        Assert.Empty(result.FieldErrors);
        Assert.Null(result.Values);
    }

    private static void AssertFieldFailure(RecordValidationResult result)
    {
        Assert.False(result.IsValid);
        Assert.Null(result.RequestError);
        Assert.NotEmpty(result.FieldErrors);
        Assert.Null(result.Values);
    }

    // A field-level failure holding exactly one message under exactly one key.
    private static void AssertOnlyFieldError(RecordValidationResult result, string field, string expected)
    {
        AssertFieldFailure(result);
        var (key, messages) = Assert.Single(result.FieldErrors);
        Assert.Equal(field, key);
        Assert.Equal(new[] { expected }, messages);
    }

    // One metaFields row; a page-bound row's TabIndex is its FieldId, so tab order follows declaration order.
    private static FieldMetadata Row(
        int fieldId,
        string name,
        int typeId,
        int? pageId,
        (string SqlType, int MaxLengthBytes)? column = null)
    {
        return new FieldMetadata
        {
            FieldId = fieldId,
            Name = name,
            FieldTypeId = typeId,
            FieldTypeName = SeededTypeName(typeId),
            PageId = pageId,
            TabIndex = pageId is null ? null : (decimal)fieldId,
            Column = column is { } catalogue
                ? FieldTypeMapper.DescribeColumn(name, catalogue.SqlType, catalogue.MaxLengthBytes)
                : null,
        };
    }

    private static (string SqlType, int MaxLengthBytes) Nvarchar(int characters) => ("nvarchar", 2 * characters);

    // metaFieldTypes.Name as seeded by testdata/sql/02_reference_data.sql.
    private static string SeededTypeName(int typeId) => typeId switch
    {
        TextType => "Text",
        LabelType => "Label/Title",
        MultilineType => "Multiline",
        NumberType => "Number",
        PhoneNumberType => "Phone Number",
        DateType => "Date",
        CheckboxType => "Checkbox",
        YesNoType => "Yes/No",
        OptionType => "Option",
        LegalValuesType => "Legal Values",
        GroupType => "Group",
        RecStatusType => "RecStatus",
        UniqueKeyType => "UniqueKey",
        GlobalRecordIdType => "GlobalRecordId",
        _ => throw new ArgumentOutOfRangeException(nameof(typeId), typeId, "Not a seeded type these fixtures use."),
    };
}
