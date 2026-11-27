using System.Globalization;
using System.Numerics;
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
/// the other forms cover what no seeded field has: required fields, a field without a prompt, a field both required
/// and read-only, one-sided ranges, number bounds beyond 2^53, numbers on <c>tinyint</c> to <c>bigint</c> columns,
/// <c>ntext</c> and unbounded <c>datetime</c> columns, and Option fields, among them two on <c>tinyint</c>: one whose
/// 256 items all fit the column and one whose 257th item leaves it unsupported.
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
    private const string ReporterPrompt = "Reported by:";
    private const string AssignedCodePrompt = "Assigned code:";

    // FIELD_IS_REQUIRED_LONG after its {0}, with {1} the page name "Page 1", typed out so the prompt cases compare with
    // text built independently of ValidationMessages; note the trailing space.
    private const string IsRequiredOnPage1 =
        " is a required field (page Page 1). Please fill in this field before attempting to leave the record, save data, or close the form. ";
    private const string AgePrompt = "Age in years";
    private const string AgeGroupPrompt = "Age group";
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
    /// The options of <see cref="FixedWidthForm"/>'s <c>Grade</c>, each its stored text: <c>F-Fem</c> at the column's
    /// full width of five, <c>M    </c> padded to it as an <c>nchar(5)</c> code table returns it, and the short
    /// <c>U</c>, as a code table of another type could hold it.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<FieldOption>> FixedWidthLegalValues =
        new Dictionary<string, IReadOnlyList<FieldOption>>(StringComparer.Ordinal)
        {
            ["Grade"] = Array.AsReadOnly(
                new[] { "F-Fem", "M    ", "U" }
                    .Select(text => new FieldOption(FieldValue.FromText(text), text))
                    .ToArray()),
        };

    /// <summary>
    /// Fixture A: the seeded fields whose rules the validator enforces, with their seeded metadata. <c>Zip</c>
    /// (<c>MaxLength</c> 5), <c>PhoneNumber</c> (mask <c>###-###-####</c>), <c>Age</c> (bounds 0 and 120, pattern
    /// <c>###</c>) and read-only <c>StateIDNumber</c> come from <c>CaseReport</c>. <c>LastName</c> carries the
    /// <c>FoodHistory</c> prompt "Last Name" (<c>CaseReport</c> prompts "Patient's Name"). <c>Sex</c> is the
    /// <c>FoodHistory</c> Legal Values field on <c>codephvs_administrativesex_hl7_2x</c>, not the <c>CaseReport</c>
    /// Comment Legal <c>Sex</c>. <c>DateofInterview</c>, <c>Asian</c>, <c>FeverTemp</c> and read-only
    /// <c>OnsetWeek</c> also come from <c>FoodHistory</c>. <c>FreeNumber</c> carries the blank bounds and pattern of
    /// the seeded <c>FoodHistory</c> <c>Age</c> row, and <c>Ill</c> stands for the <c>FoodHistory</c>
    /// <c>Yes/No</c> <c>ILL</c> row on <c>tinyint</c>, which maps to unsupported. The field ids and the two-page
    /// placement are synthetic, not seed positions. A label, a group and the three system rows complete the form.
    /// No field is required.
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
    /// Fixture D: the storage ranges of the admitted column types no seeded field exercises: number fields on
    /// <c>tinyint</c>, <c>smallint</c>, <c>int</c> and <c>bigint</c>, Option fields with and without items, an
    /// unbounded <c>datetime</c> date, <c>nvarchar(4000)</c> and <c>ntext</c>. <c>WideChoice</c> and
    /// <c>OverfullChoice</c> are Options on <c>tinyint</c>, which stores 0 to 255, with 256 and 257 items, and both
    /// offer every item. The 256 indexes of <c>WideChoice</c> all fit the column. The 257th item of
    /// <c>OverfullChoice</c> has index 256, which <c>tinyint</c> cannot store, so the storage-range check rejects it.
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
            Row(8, "TinyCount", NumberType, pageId: 1, TinyIntColumn),
            Row(9, "BigCount", NumberType, pageId: 1, BigIntColumn),
            Row(10, "WideChoice", OptionType, pageId: 1, TinyIntColumn) with { List = OptionList(256) },
            Row(11, "OverfullChoice", OptionType, pageId: 1, TinyIntColumn) with { List = OptionList(257) },
        ]);

    /// <summary>
    /// Fixture E: one-sided ranges, because every seeded bounded field sets both bounds. Each number field on
    /// <c>float</c> and each date field on <c>datetime</c> sets a single bound, dates in the stored <c>MM-DD-YYYY</c>
    /// form; the other bound is NULL on one field of each kind and blank on the other, the two unset forms
    /// <c>metaFields</c> holds.
    /// </summary>
    private static readonly FormMetadata OneSidedRangeForm = new(
        ViewId: 11,
        Name: "OneSidedRange",
        BaseTableName: "OneSidedRange",
        Pages: [new PageMetadata(1, "Page 1", 0, "OneSidedRange1")],
        Fields:
        [
            Row(1, "LowerOnlyNumber", NumberType, pageId: 1, FloatColumn) with { Lower = "10.5", Upper = "" },
            Row(2, "UpperOnlyNumber", NumberType, pageId: 1, FloatColumn) with { Upper = "99.5" },
            Row(3, "LowerOnlyDate", DateType, pageId: 1, DatetimeColumn) with { Lower = "03-01-2011" },
            Row(4, "UpperOnlyDate", DateType, pageId: 1, DatetimeColumn) with { Lower = "", Upper = "06-30-2012" },
        ]);

    /// <summary>
    /// Fixture F: a field both required and read-only, which no seeded field is, beside a required writable field and
    /// an optional one. It pins how the two flags combine: submitting the read-only field is rejected (C-3), and the
    /// required gate still takes it, because legacy's required check exempts no read-only field. Its stored value
    /// decides a <c>PUT</c>, and a <c>POST</c>, which can never supply it, always lists it as missing once the create
    /// passes the empty-record rule.
    /// </summary>
    private static readonly FormMetadata RequiredReadOnlyForm = new(
        ViewId: 10,
        Name: "RequiredReadOnly",
        BaseTableName: "RequiredReadOnly",
        Pages: [new PageMetadata(1, "Page 1", 0, "RequiredReadOnly1")],
        Fields:
        [
            Row(1, "AssignedCode", TextType, pageId: 1, Nvarchar(4000)) with
            {
                Prompt = AssignedCodePrompt,
                IsRequired = true,
                IsReadOnly = true,
            },
            Row(2, "Reporter", TextType, pageId: 1, Nvarchar(4000)) with
            {
                Prompt = ReporterPrompt,
                IsRequired = true,
            },
            Row(3, "Remarks", TextType, pageId: 1, Nvarchar(4000)),
        ]);

    /// <summary>
    /// Fixture G: number bounds a double cannot hold, which no seeded field has. <c>9007199254740993</c> is 2^53 + 1,
    /// the first integer a double cannot hold, and fits the <c>nvarchar(20)</c> of
    /// <c>metaFields.Lower</c>/<c>Upper</c>. The first <c>bigint</c> fields set an upper bound only, a lower bound
    /// only, and both; the first <c>float</c> fields pair an exact integer bound with a fractional one, in each order.
    /// <c>FloatLowerBig</c> and <c>FloatUpperBig</c> set that integer as a single bound on <c>float</c>, which stores
    /// the binary64 9007199254740992 for it. The <c>bigint</c> fractional bounds round across an integer as doubles:
    /// <c>9007199254740992.5</c> and <c>-9007199254740992.5</c> to ±9007199254740992, and <c>9007199254740993.5</c>
    /// to 9007199254740994; on <c>int</c>, <c>2.0000000000000001</c> rounds to 2. <c>FractionsOnInt</c> has the
    /// ordinary fractional bounds <c>0.5</c> and <c>99.5</c> on <c>int</c>, so its limits are 1 and 99, and
    /// <c>LowerBeyondBigint</c> has the lower bound <c>1e30</c>, beyond the <c>bigint</c> range, on <c>bigint</c>.
    /// </summary>
    private static readonly FormMetadata BigBoundForm = new(
        ViewId: 12,
        Name: "BigBound",
        BaseTableName: "BigBound",
        Pages: [new PageMetadata(1, "Page 1", 0, "BigBound1")],
        Fields:
        [
            Row(1, "UpperOnlyBig", NumberType, pageId: 1, BigIntColumn) with { Upper = "9007199254740993" },
            Row(2, "LowerOnlyBig", NumberType, pageId: 1, BigIntColumn) with { Lower = "9007199254740993" },
            Row(3, "BothBig", NumberType, pageId: 1, BigIntColumn) with
            {
                Lower = "-9007199254740993",
                Upper = "9007199254740993",
            },
            Row(4, "FractionToBig", NumberType, pageId: 1, FloatColumn) with
            {
                Lower = "-0.5",
                Upper = "9007199254740993",
            },
            Row(5, "BigToFraction", NumberType, pageId: 1, FloatColumn) with
            {
                Lower = "-9007199254740993",
                Upper = "0.5",
            },
            Row(6, "FloatLowerBig", NumberType, pageId: 1, FloatColumn) with { Lower = "9007199254740993" },
            Row(7, "FloatUpperBig", NumberType, pageId: 1, FloatColumn) with { Upper = "9007199254740993" },
            Row(8, "UpperFractionBig", NumberType, pageId: 1, BigIntColumn) with { Upper = "9007199254740992.5" },
            Row(9, "LowerFractionBig", NumberType, pageId: 1, BigIntColumn) with { Lower = "-9007199254740992.5" },
            Row(10, "UpperFractionPastBig", NumberType, pageId: 1, BigIntColumn) with { Upper = "9007199254740993.5" },
            Row(11, "LowerNearTwo", NumberType, pageId: 1, IntColumn) with { Lower = "2.0000000000000001" },
            Row(12, "FractionsOnInt", NumberType, pageId: 1, IntColumn) with { Lower = "0.5", Upper = "99.5" },
            Row(13, "LowerBeyondBigint", NumberType, pageId: 1, BigIntColumn) with { Lower = "1e30" },
        ]);

    /// <summary>
    /// Fixture H: two required data fields on different pages whose names differ only by letter case, which no seeded
    /// form has: <c>Age</c>, a number on page 1, and <c>AGE</c>, a text field on page 2; plus an optional text field.
    /// </summary>
    private static readonly FormMetadata CaseVariantForm = new(
        ViewId: 6,
        Name: "CaseVariant",
        BaseTableName: "CaseVariant",
        Pages: [new PageMetadata(1, "Page 1", 0, "CaseVariant1"), new PageMetadata(2, "Page 2", 1, "CaseVariant2")],
        Fields:
        [
            Row(1, "Age", NumberType, pageId: 1, FloatColumn) with { Prompt = AgePrompt, IsRequired = true },
            Row(2, "Notes", TextType, pageId: 1, Nvarchar(4000)),
            Row(3, "AGE", TextType, pageId: 2, Nvarchar(20)) with { Prompt = AgeGroupPrompt, IsRequired = true },
        ]);

    /// <summary>
    /// Fixture I: a label <c>aGe</c>, first in metadata order, beside the data fields <c>Age</c> and <c>AGE</c>, whose
    /// names make every other spelling of <c>age</c> ambiguous, which no seeded form has. The label is the first row
    /// equal to each such spelling ignoring case.
    /// </summary>
    private static readonly FormMetadata CaseVariantLabelForm = new(
        ViewId: 7,
        Name: "CaseVariantLabel",
        BaseTableName: "CaseVariantLabel",
        Pages:
        [
            new PageMetadata(1, "Page 1", 0, "CaseVariantLabel1"),
            new PageMetadata(2, "Page 2", 1, "CaseVariantLabel2"),
        ],
        Fields:
        [
            Row(1, "aGe", LabelType, pageId: 1),
            Row(2, "Age", NumberType, pageId: 1, FloatColumn),
            Row(3, "AGE", TextType, pageId: 2, Nvarchar(20)),
        ]);

    /// <summary>
    /// Fixture J: two required number rows spelled exactly <c>Age</c>, which no seeded form has. The row on page 1 is
    /// first in storage order, so it is the data field; the row on page 2, first in metadata order and with the lower
    /// <c>FieldId</c>, repeats its name and is listed unsupported. Plus an optional text field.
    /// </summary>
    private static readonly FormMetadata RepeatedNameForm = new(
        ViewId: 8,
        Name: "RepeatedName",
        BaseTableName: "RepeatedName",
        Pages: [new PageMetadata(1, "Page 1", 0, "RepeatedName1"), new PageMetadata(2, "Page 2", 1, "RepeatedName2")],
        Fields:
        [
            Row(1, "Age", NumberType, pageId: 2, FloatColumn) with { Prompt = AgeGroupPrompt, IsRequired = true },
            Row(2, "Age", NumberType, pageId: 1, FloatColumn) with { Prompt = AgePrompt, IsRequired = true },
            Row(3, "Notes", TextType, pageId: 1, Nvarchar(4000)),
        ]);

    /// <summary>
    /// Fixture K: a Text field and a Legal Values field on fixed-width <c>nchar(5)</c> columns, which no seeded column
    /// is; <c>Grade</c> offers <see cref="FixedWidthLegalValues"/>.
    /// </summary>
    private static readonly FormMetadata FixedWidthForm = new(
        ViewId: 13,
        Name: "FixedWidth",
        BaseTableName: "FixedWidth",
        Pages: [new PageMetadata(1, "Page 1", 0, "FixedWidth1")],
        Fields:
        [
            Row(1, "Code", TextType, pageId: 1, Nchar(5)),
            Row(2, "Grade", LegalValuesType, pageId: 1, Nchar(5)),
        ]);

    // Column catalogue facts as sys.columns.max_length reports them, in bytes: nvarchar(n) is 2·n.
    private static (string SqlType, int MaxLengthBytes) FloatColumn => ("float", 8);

    private static (string SqlType, int MaxLengthBytes) DatetimeColumn => ("datetime", 8);

    private static (string SqlType, int MaxLengthBytes) BitColumn => ("bit", 1);

    private static (string SqlType, int MaxLengthBytes) TinyIntColumn => ("tinyint", 1);

    private static (string SqlType, int MaxLengthBytes) SmallIntColumn => ("smallint", 2);

    private static (string SqlType, int MaxLengthBytes) IntColumn => ("int", 4);

    private static (string SqlType, int MaxLengthBytes) BigIntColumn => ("bigint", 8);

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
        {
            "Storage",
            [
                "SmallCount", "Choice", "EmptyChoice", "WholeCount", "EventDate", "LongText", "Memo",
                "TinyCount", "BigCount", "WideChoice", "OverfullChoice",
            ]
        },
        { "OneSidedRange", ["LowerOnlyNumber", "UpperOnlyNumber", "LowerOnlyDate", "UpperOnlyDate"] },
        { "RequiredReadOnly", ["AssignedCode", "Reporter", "Remarks"] },
        {
            "BigBound",
            [
                "UpperOnlyBig", "LowerOnlyBig", "BothBig", "FractionToBig", "BigToFraction", "FloatLowerBig",
                "FloatUpperBig", "UpperFractionBig", "LowerFractionBig", "UpperFractionPastBig", "LowerNearTwo",
                "FractionsOnInt", "LowerBeyondBigint",
            ]
        },
        { "CaseVariant", ["Age", "Notes", "AGE"] },
        { "CaseVariantLabel", ["Age", "AGE"] },
        { "RepeatedName", ["Age", "Notes"] },
        { "FixedWidth", ["Code", "Grade"] },
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

    /// <summary>
    /// The integer-column cases rely on number fields staying <c>number</c> on <c>tinyint</c> and <c>bigint</c>, on
    /// <c>WideChoice</c> staying an Option that offers one item per <c>tinyint</c> index, 0 to 255, and on
    /// <c>OverfullChoice</c> staying an Option that offers all 257 items, 0 to 256, though <c>tinyint</c> cannot
    /// store the last index.
    /// </summary>
    [Fact]
    public void StorageForm_MapsTheIntegerColumnsTheCasesRelyOn()
    {
        var fields = FormDefinitionFactory.Create(StorageForm, NoLegalValues, Array.Empty<Diagnostic>())
            .Pages
            .SelectMany(page => page.Fields)
            .ToDictionary(field => field.Name, StringComparer.Ordinal);

        Assert.Equal(FieldType.Number, fields["TinyCount"].FieldType);
        Assert.Equal(FieldType.Number, fields["BigCount"].FieldType);
        Assert.Equal(FieldType.Option, fields["WideChoice"].FieldType);
        Assert.False(fields["WideChoice"].Unsupported);
        Assert.Null(fields["WideChoice"].UnsupportedType);
        Assert.Equal(ExpectedOptions(256), fields["WideChoice"].Options);
        Assert.Equal(FieldType.Option, fields["OverfullChoice"].FieldType);
        Assert.False(fields["OverfullChoice"].Unsupported);
        Assert.Null(fields["OverfullChoice"].UnsupportedType);
        Assert.Equal(ExpectedOptions(257), fields["OverfullChoice"].Options);

        static FieldOption[] ExpectedOptions(int count) =>
            Enumerable.Range(0, count)
                .Select(index => new FieldOption(
                    FieldValue.FromIndex(index),
                    "L" + index.ToString(CultureInfo.InvariantCulture)))
                .ToArray();
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
        Assert.Equal(FieldValue.FromText("Smith"), values["LastName"]);
        Assert.False(values.ContainsKey("lastname"));
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
    /// A name the form has no <c>metaFields</c> row for has no canonical spelling, so it is reported under the
    /// <c>values</c> parameter key with a fixed message: the submitted name reaches neither a key nor a message.
    /// </summary>
    [Fact]
    public void ValidateInput_UnknownName_IsFieldErrorUnderValuesKey()
    {
        const string submittedName = "SENTINEL-PII-Bogus-4711";

        var result = Validate(MirrorValidator(), (submittedName, FieldValue.FromText("x")));

        AssertOnlyFieldError(result, RecordValidator.ValuesKey, ValidationMessages.UnknownValuesName);
        Assert.All(result.FieldErrors.Keys, key => Assert.DoesNotContain("SENTINEL", key, StringComparison.Ordinal));
        Assert.All(
            result.FieldErrors.Values.SelectMany(messages => messages),
            message => Assert.DoesNotContain("SENTINEL", message, StringComparison.Ordinal));
    }

    /// <summary>
    /// The one rule for a name that is not a data field, key and message together, which
    /// <see cref="RecordValidator.NotADataFieldError"/> and <see cref="RecordValidator.ValidateInput"/> both apply:
    /// any letter case of a label, group, unsupported or system row gives the row's canonical spelling with the
    /// not-a-data-field message; a name with no row, the empty name included, gives the <c>values</c> parameter key
    /// with its own fixed message; and a name ambiguous between the data fields <c>Age</c> and <c>AGE</c>, though it
    /// equals both rows ignoring case, gives the <c>values</c> parameter key with the ambiguous-name message, never a
    /// canonical name it might mean. An exact spelling decides before ambiguity, and ambiguity before a match ignoring
    /// case: the label <c>aGe</c> spelled exactly is reported as itself, while <c>age</c> and <c>AGe</c>, which equal
    /// that label only ignoring case, are ambiguous.
    /// </summary>
    [Theory]
    [InlineData("Mirror", "title1", "Title1", ValidationMessages.UnknownField)]
    [InlineData("Mirror", "TITLE1", "Title1", ValidationMessages.UnknownField)]
    [InlineData("Mirror", "namegroup", "NameGroup", ValidationMessages.UnknownField)]
    [InlineData("Mirror", "NAMEGROUP", "NameGroup", ValidationMessages.UnknownField)]
    [InlineData("Mirror", "ill", "Ill", ValidationMessages.UnknownField)]
    [InlineData("Mirror", "recstatus", "RecStatus", ValidationMessages.UnknownField)]
    [InlineData("Mirror", "UNIQUEKEY", "UniqueKey", ValidationMessages.UnknownField)]
    [InlineData("Mirror", "GLOBALRECORDID", "GlobalRecordId", ValidationMessages.UnknownField)]
    [InlineData("Mirror", "globalRecordID", "GlobalRecordId", ValidationMessages.UnknownField)]
    [InlineData("Mirror", "SENTINEL-PII-Bogus-4711", RecordValidator.ValuesKey, ValidationMessages.UnknownValuesName)]
    [InlineData("Mirror", "", RecordValidator.ValuesKey, ValidationMessages.UnknownValuesName)]
    [InlineData("CaseVariant", "age", RecordValidator.ValuesKey, ValidationMessages.AmbiguousValuesName)]
    [InlineData("CaseVariantLabel", "aGe", "aGe", ValidationMessages.UnknownField)]
    [InlineData("CaseVariantLabel", "age", RecordValidator.ValuesKey, ValidationMessages.AmbiguousValuesName)]
    [InlineData("CaseVariantLabel", "AGe", RecordValidator.ValuesKey, ValidationMessages.AmbiguousValuesName)]
    public void NotADataFieldError_SubmittedName_IsCanonicalRowOrValuesKeyWithItsMessage(
        string formName,
        string submittedName,
        string expectedKey,
        string expectedMessage)
    {
        var form = FixtureForm(formName);

        var (key, message) = RecordValidator.NotADataFieldError(form, submittedName);

        Assert.Equal(expectedKey, key);
        Assert.Equal(expectedMessage, message);
        AssertOnlyFieldError(
            Validate(Validator(form, LegalValuesOf(form)), (submittedName, FieldValue.FromText("x"))),
            expectedKey,
            expectedMessage);
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

    /// <summary>
    /// Two non-data rows whose names differ only by letter case: a spelling of either row is keyed by that row, and
    /// any other spelling by the first row in metadata order, as <see cref="FormMetadata.FindField"/> resolves the
    /// name. The rows' two spellings make no data field ambiguous, so
    /// <see cref="RecordValidator.NotADataFieldError"/> gives the same error as the validator.
    /// </summary>
    [Theory]
    [InlineData("notice", "Notice")]
    [InlineData("NOTICE", "NOTICE")]
    [InlineData("Notice", "Notice")]
    public void ValidateInput_NonDataNamesDifferingOnlyByCase_AreRejectedUnderTheExactOrFirstRowsName(
        string submittedName,
        string expectedKey)
    {
        var form = new FormMetadata(
            ViewId: 5,
            Name: "CaseVariants",
            BaseTableName: "CaseVariants",
            Pages: [new PageMetadata(1, "Page 1", 0, "CaseVariants1")],
            Fields:
            [
                Row(1, "Notice", LabelType, pageId: 1),
                Row(2, "NOTICE", LabelType, pageId: 1),
                Row(3, "LastName", TextType, pageId: 1, Nvarchar(4000)),
            ]);

        var result = Validate(Validator(form), (submittedName, FieldValue.FromText("1")));

        AssertOnlyFieldError(result, expectedKey, ValidationMessages.UnknownField);
        Assert.Equal(
            new KeyValuePair<string, string>(expectedKey, ValidationMessages.UnknownField),
            RecordValidator.NotADataFieldError(form, submittedName));
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

    /// <summary>
    /// The ends of the <c>tinyint</c> and <c>bigint</c> ranges are accepted and kept as the exact integers sent. A
    /// double would blur them: the nearest double of <c>long.MaxValue</c> is 2^63, past the range, and that of
    /// 9007199254740993 is 9007199254740992.
    /// </summary>
    [Theory]
    [InlineData("TinyCount", "0", 0L)]
    [InlineData("TinyCount", "255", 255L)]
    [InlineData("BigCount", "-9223372036854775808", long.MinValue)]
    [InlineData("BigCount", "9223372036854775807", long.MaxValue)]
    [InlineData("BigCount", "9007199254740993", 9007199254740993L)]
    [InlineData("BigCount", "-9007199254740993", -9007199254740993L)]
    public void ValidateInput_ExactIntegerInIntegerColumn_IsKeptExact(string field, string literal, long expected)
    {
        var values = AssertValid(Validate(Validator(StorageForm), (field, ExactNumber(literal))));

        Assert.True(values[field].TryGetInteger(out var integer));
        Assert.Equal(expected, integer);
        Assert.Equal(FieldValue.FromInteger(expected), values[field]);
    }

    /// <summary>
    /// One past either end of the <c>tinyint</c> and <c>bigint</c> ranges is a field error. The reader keeps the two
    /// <c>bigint</c> cases only as their nearest doubles, and that of -9223372036854775809 is -2^63,
    /// <c>long.MinValue</c> itself, so a check on the double would admit it.
    /// </summary>
    [Theory]
    [InlineData("TinyCount", "-1")]
    [InlineData("TinyCount", "256")]
    [InlineData("BigCount", "9223372036854775808")]
    [InlineData("BigCount", "-9223372036854775809")]
    public void ValidateInput_ExactNumberOutsideIntegerColumn_IsNotStorable(string field, string literal)
    {
        var result = Validate(Validator(StorageForm), (field, ExactNumber(literal)));

        AssertOnlyFieldError(result, field, ValidationMessages.NumberNotStorable);
    }

    /// <summary>
    /// <c>2.0000000000000001</c> is not a whole number, though its nearest double is exactly 2. Integer columns and
    /// option indexes decide on the exact value, so it is neither stored as 2 nor read as the third item.
    /// </summary>
    [Theory]
    [InlineData("TinyCount", ValidationMessages.NumberNotStorable)]
    [InlineData("SmallCount", ValidationMessages.NumberNotStorable)]
    [InlineData("WholeCount", ValidationMessages.NumberNotStorable)]
    [InlineData("BigCount", ValidationMessages.NumberNotStorable)]
    [InlineData("Choice", ValidationMessages.OptionIndexExpected)]
    public void ValidateInput_NearIntegralNumber_IsNeverWhole(string field, string expected)
    {
        var raw = ExactNumber("2.0000000000000001");
        Assert.Equal(2d, raw.AsNumber());
        Assert.False(raw.TryGetInteger(out _));

        var result = Validate(Validator(StorageForm), (field, raw));

        AssertOnlyFieldError(result, field, expected);
    }

    /// <summary>
    /// 0 and 255, the smallest and largest indexes <c>tinyint</c> stores, are the first and last items of
    /// <c>WideChoice</c>, and are accepted on <c>OverfullChoice</c> too, whose 257th item has the index 256.
    /// </summary>
    [Theory]
    [InlineData("WideChoice", 0)]
    [InlineData("WideChoice", 255)]
    [InlineData("OverfullChoice", 0)]
    [InlineData("OverfullChoice", 255)]
    public void ValidateInput_OptionIndexWithinTinyint_BecomesIndex(string field, int index)
    {
        var values = AssertValid(Validate(Validator(StorageForm), (field, FieldValue.FromInteger(index))));

        Assert.Equal(FieldValue.FromIndex(index), values[field]);
    }

    public static TheoryData<string, int, string> UnwritableOptionIndexOnTinyint => new()
    {
        // One past the 256 items of WideChoice: the option count rejects it before the column is consulted.
        { "WideChoice", 256, ValidationMessages.FormatOptionIndexOutOfRange(256) },

        // 256, the index of the 257th item of OverfullChoice, is within its option count, but tinyint cannot store it.
        { "OverfullChoice", 256, ValidationMessages.ValueNotStorable },
    };

    /// <summary>
    /// An option index a <c>tinyint</c> field cannot take is one field error under the canonical name, never a storage
    /// exception. <c>WideChoice</c> rejects 256 on its option count. <c>OverfullChoice</c> offers 256 as its 257th
    /// item and rejects it on the storage range, the column's limit.
    /// </summary>
    [Theory]
    [MemberData(nameof(UnwritableOptionIndexOnTinyint))]
    public void ValidateInput_UnwritableOptionIndexOnTinyint_IsOneFieldError(string field, int index, string expected)
    {
        var result = Validate(Validator(StorageForm), (field.ToLowerInvariant(), FieldValue.FromInteger(index)));

        AssertOnlyFieldError(result, field, expected);
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

    // An nchar(5) column admits text of up to five characters, which SQL Server pads with spaces to five, so short
    // text, a short legal value and a padded code-table text are all accepted unchanged, never padded or trimmed here.
    [Theory]
    [InlineData("Code", "abc")]
    [InlineData("Grade", "U")]
    [InlineData("Grade", "M    ")]
    public void ValidateInput_TextUpToTheNcharWidth_IsAcceptedAsSubmitted(string field, string text)
    {
        var values = AssertValid(
            Validate(Validator(FixedWidthForm, FixedWidthLegalValues), (field, FieldValue.FromText(text))));

        Assert.Equal(FieldValue.FromText(text), values[field]);
    }

    // Text longer than an nchar(5) column gets only the storage message naming its length, before the legal-values
    // check.
    [Theory]
    [InlineData("Code")]
    [InlineData("Grade")]
    public void ValidateInput_TextLongerThanTheNcharWidth_IsTooLongForTheColumn(string field)
    {
        var result = Validate(
            Validator(FixedWidthForm, FixedWidthLegalValues),
            (field, FieldValue.FromText("abcdef")));

        AssertOnlyFieldError(result, field, ValidationMessages.FormatTextTooLongForColumn(5));
    }

    // SQL Server stores the short option U padded to U followed by four spaces, which is no option, so resubmitting
    // that read-back text verbatim is refused by the ordinal legal-values check; choosing U again is accepted above.
    [Fact]
    public void ValidateInput_PaddedReadBackOfAShortNcharOption_IsNotALegalValue()
    {
        var result = Validate(
            Validator(FixedWidthForm, FixedWidthLegalValues),
            ("Grade", FieldValue.FromText("U    ")));

        AssertOnlyFieldError(result, "Grade", ValidationMessages.NotALegalValue);
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

    /// <summary>
    /// A bound a double cannot hold is inclusive by what the column stores, and every accepted integer keeps its exact
    /// value. On <c>bigint</c> and <c>int</c> the comparison is exact: the bound itself and the adjacent integer inside
    /// it are accepted, as is the limit of <c>bigint</c> on the open side, and a fractional bound admits the integers
    /// up to its floor or down to its ceiling. Through a double the upper bound <c>9007199254740993</c> would read as
    /// <c>9007199254740992</c> and reject itself. On <c>float</c> value and bound compare as the binary64 the column
    /// stores, so 9007199254740992 and 9007199254740993, one double, both meet the bound <c>9007199254740993</c>.
    /// </summary>
    [Theory]
    [InlineData("UpperOnlyBig", 9007199254740993L)]
    [InlineData("UpperOnlyBig", 9007199254740992L)]
    [InlineData("UpperOnlyBig", long.MinValue)]
    [InlineData("LowerOnlyBig", 9007199254740993L)]
    [InlineData("LowerOnlyBig", 9007199254740994L)]
    [InlineData("LowerOnlyBig", long.MaxValue)]
    [InlineData("BothBig", 9007199254740993L)]
    [InlineData("BothBig", -9007199254740993L)]
    [InlineData("BothBig", 0L)]
    [InlineData("FractionToBig", 9007199254740993L)]
    [InlineData("BigToFraction", -9007199254740993L)]
    [InlineData("FloatLowerBig", 9007199254740992L)]
    [InlineData("FloatLowerBig", 9007199254740993L)]
    [InlineData("FloatUpperBig", 9007199254740993L)]
    [InlineData("FloatUpperBig", 9007199254740992L)]
    [InlineData("UpperFractionBig", 9007199254740992L)]
    [InlineData("UpperFractionBig", long.MinValue)]
    [InlineData("LowerFractionBig", -9007199254740992L)]
    [InlineData("LowerFractionBig", long.MaxValue)]
    [InlineData("UpperFractionPastBig", 9007199254740993L)]
    [InlineData("LowerNearTwo", 3L)]
    [InlineData("LowerNearTwo", int.MaxValue)]
    [InlineData("FractionsOnInt", 1L)]
    [InlineData("FractionsOnInt", 99L)]
    public void ValidateInput_IntegerWithinBoundADoubleCannotHold_IsAcceptedExactly(string field, long raw)
    {
        var values = AssertValid(Validate(Validator(BigBoundForm), (field, FieldValue.FromInteger(raw))));

        Assert.Equal(FieldValue.FromInteger(raw), values[field]);
        Assert.True(values[field].TryGetInteger(out var integer));
        Assert.Equal(raw, integer);
    }

    /// <summary>
    /// The adjacent integer outside a bound a double cannot hold is rejected with exactly one message, which names
    /// the limit the check enforces. On <c>bigint</c> and <c>int</c> that is the bound's exact integer limit with
    /// every digit, never its nearest double: <c>9007199254740993.5</c> reads "at most 9007199254740993", not
    /// 9007199254740994; <c>2.0000000000000001</c> "at least 3", not 2; <c>0.5</c> to <c>99.5</c> "between 1 and 99".
    /// Only a limit beyond the signed 64-bit range is named by its bound's shortest round-trip double, so
    /// <c>1e30</c> rejects even <see cref="long.MaxValue"/> "at least 1E+30". Through a double the lower bound
    /// <c>9007199254740993</c> would read as <c>9007199254740992</c> and admit that integer on <c>bigint</c>, and the
    /// upper bound <c>9007199254740992.5</c> would read as <c>9007199254740992</c> and admit 9007199254740993. On
    /// <c>float</c> the message names the bound as the form definition carries it, an exact integer with every digit
    /// and a fractional bound as its shortest round-trip double, and the first double outside the bound's binary64
    /// is rejected: 9007199254740994 above <c>9007199254740993</c>, and 9007199254740990 below it.
    /// </summary>
    [Theory]
    [InlineData("UpperOnlyBig", 9007199254740994L, "The value must be at most 9007199254740993.")]
    [InlineData("LowerOnlyBig", 9007199254740992L, "The value must be at least 9007199254740993.")]
    [InlineData("BothBig", 9007199254740994L, "The value must be between -9007199254740993 and 9007199254740993.")]
    [InlineData("BothBig", -9007199254740994L, "The value must be between -9007199254740993 and 9007199254740993.")]
    [InlineData("FractionToBig", -1L, "The value must be between -0.5 and 9007199254740993.")]
    [InlineData("BigToFraction", 1L, "The value must be between -9007199254740993 and 0.5.")]
    [InlineData("FloatLowerBig", 9007199254740990L, "The value must be at least 9007199254740993.")]
    [InlineData("FloatUpperBig", 9007199254740994L, "The value must be at most 9007199254740993.")]
    [InlineData("UpperFractionBig", 9007199254740993L, "The value must be at most 9007199254740992.")]
    [InlineData("LowerFractionBig", -9007199254740993L, "The value must be at least -9007199254740992.")]
    [InlineData("UpperFractionPastBig", 9007199254740994L, "The value must be at most 9007199254740993.")]
    [InlineData("LowerNearTwo", 2L, "The value must be at least 3.")]
    [InlineData("FractionsOnInt", 0L, "The value must be between 1 and 99.")]
    [InlineData("FractionsOnInt", 100L, "The value must be between 1 and 99.")]
    [InlineData("LowerBeyondBigint", long.MaxValue, "The value must be at least 1E+30.")]
    public void ValidateInput_IntegerBeyondBoundADoubleCannotHold_IsOneRangeError(
        string field,
        long raw,
        string expected)
    {
        var result = Validate(Validator(BigBoundForm), (field, FieldValue.FromInteger(raw)));

        AssertOnlyFieldError(result, field, expected);
    }

    /// <summary>
    /// On <c>float</c> a fraction is compared as the double the column stores: <c>9007199254740992.5</c> rounds to
    /// 9007199254740992, the binary64 of the lower bound <c>9007199254740993</c>, so it meets that bound as
    /// 9007199254740992 does, and it is kept exactly as submitted.
    /// </summary>
    [Fact]
    public void ValidateInput_FractionOnFloatColumnSharingTheBoundsDouble_IsAcceptedAsSubmitted()
    {
        var raw = ExactNumber("9007199254740992.5");
        Assert.Equal(9007199254740992d, raw.AsNumber());
        Assert.False(raw.TryGetInteger(out _));

        var values = AssertValid(Validate(Validator(BigBoundForm), ("FloatLowerBig", raw)));

        Assert.Equal(raw, values["FloatLowerBig"]);
    }

    /// <summary>
    /// An integer column takes its exact limits from the metadata text its definition bound was parsed from. A
    /// definition built from other metadata, whose bound that text is unset, unreadable or another number for, is
    /// refused, rather than enforcing and naming limits its bound does not give. <c>9007199254740992</c> is refused
    /// though its double equals the bound's, because it is an exact integer and the bound is not.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("NaN")]
    [InlineData("9007199254740992")]
    [InlineData("9007199254740993.5")]
    public void Constructor_IntegerColumnBoundTheMetadataDoesNotState_IsRejected(string? upper)
    {
        var definition = FormDefinitionFactory.Create(BigBoundForm, NoLegalValues, Array.Empty<Diagnostic>());
        var otherMetadata = BigBoundForm with
        {
            Fields = BigBoundForm.Fields
                .Select(field => field.Name == "UpperFractionBig" ? field with { Upper = upper } : field)
                .ToArray(),
        };

        Assert.Throws<ArgumentException>("definition", () => new RecordValidator(otherMetadata, definition));
    }

    // Metadata bound texts beside the exact floor and ceiling an integer column takes from them.
    public static TheoryData<string, string, string> IntegerLimitsOfBoundText => new()
    {
        { "9007199254740993", "9007199254740993", "9007199254740993" },
        { " 2e3 ", "2000", "2000" },
        { "9007199254740992.5", "9007199254740992", "9007199254740993" },
        { "-9007199254740992.5", "-9007199254740993", "-9007199254740992" },
        { "9007199254740993.5", "9007199254740993", "9007199254740994" },
        { "2.0000000000000001", "2", "3" },
        { "12.5e-1", "1", "2" },
        { "-0.5", "-1", "0" },
        { ".5", "0", "1" },
        { "5.", "5", "5" },
        { "-0.0", "0", "0" },
        { "-0e-999999999999999999999", "0", "0" },
        { "1e-400", "0", "1" },
        { "-1e-400", "-1", "0" },
        { "1e30", "1000000000000000000000000000000", "1000000000000000000000000000000" },
        { "-9223372036854775808.5", "-9223372036854775809", "-9223372036854775808" },
    };

    /// <summary>
    /// The limits are exact and unbounded: a fraction a double rounds away still moves them, a magnitude below 1 at
    /// any exponent gives the integers on either side of 0, and a whole number beyond the signed 64-bit range keeps
    /// every digit. Negative zero is 0.
    /// </summary>
    [Theory]
    [MemberData(nameof(IntegerLimitsOfBoundText))]
    public void TryParseIntegerLimits_FiniteNumberText_GivesTheExactFloorAndCeiling(
        string text,
        string floor,
        string ceiling)
    {
        Assert.True(FieldValueCoercion.TryParseIntegerLimits(text, out var actualFloor, out var actualCeiling));

        Assert.Equal(BigInteger.Parse(floor, CultureInfo.InvariantCulture), actualFloor);
        Assert.Equal(BigInteger.Parse(ceiling, CultureInfo.InvariantCulture), actualCeiling);
    }

    /// <summary>The limits are read only from a text the number parse accepts as a finite number.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("NaN")]
    [InlineData("-Infinity")]
    [InlineData("1e309")]
    [InlineData("1,000")]
    [InlineData("1e")]
    [InlineData("- 5")]
    public void TryParseIntegerLimits_TextTheNumberParseRefuses_IsRefused(string? text)
    {
        Assert.False(FieldValueCoercion.TryParseNumber(text, out _));

        Assert.False(FieldValueCoercion.TryParseIntegerLimits(text, out var floor, out var ceiling));
        Assert.Equal(BigInteger.Zero, floor);
        Assert.Equal(BigInteger.Zero, ceiling);
    }

    // Number texts in the invariant grammar beside the exact JSON number each keeps, or null for a number whose
    // nearest double already keeps its integrality: an exact integer, zero, or a double that is not whole.
    public static TheoryData<string, string?> JsonNumberOfNumberText => new()
    {
        { "2.0000000000000001", "2.0000000000000001" },
        { " +2.0000000000000001\t", "2.0000000000000001" },
        { "9007199254740992.50\0", "9007199254740992.50" },
        { "-9223372036854775808.5", "-9223372036854775808.5" },
        { "1e-400", "1e-400" },
        { "-1E-400", "-1e-400" },
        { ".5e-400", "0.5e-400" },
        { "+0005.E-0400", "5e-0400" },
        { "9223372036854775808", "9223372036854775808" },
        { "1e20", "1e20" },
        { "9007199254740993", null },
        { "2.0", null },
        { "-0e-400", null },
        { "37.5", null },
        { "0.1", null },
    };

    /// <summary>
    /// A number that holds no exact signed 64-bit integer although its nearest double is whole keeps an RFC 8259 number
    /// exactly equal in value to its text, spelled from the parts the scanner reads: no white space, NUL or <c>+</c>, no
    /// leading zero in the integer part, a 0 before a bare point and no point without fraction digits. Reading that
    /// JSON number again gives an equal value with the same JSON number. Every other number keeps none.
    /// </summary>
    [Theory]
    [MemberData(nameof(JsonNumberOfNumberText))]
    public void TryParseNumber_NumberWithAWholeNearestDouble_KeepsItsExactJsonNumber(string text, string? expected)
    {
        var value = ExactNumber(text);

        Assert.Equal(expected is not null, value.TryGetJsonNumber(out var jsonNumber));
        Assert.Equal(expected, jsonNumber);
        if (expected is null)
        {
            return;
        }

        using var document = System.Text.Json.JsonDocument.Parse(expected);
        Assert.Equal(System.Text.Json.JsonValueKind.Number, document.RootElement.ValueKind);
        var reread = ExactNumber(expected);
        Assert.Equal(value, reread);
        Assert.True(reread.TryGetJsonNumber(out var rereadNumber));
        Assert.Equal(expected, rereadNumber);
    }

    /// <summary>
    /// The JSON number is a spelling of a number, not part of its value: two numbers whose nearest doubles and floors
    /// agree are equal, with equal hash codes, however differently they are spelled, while
    /// <c>1.9999999999999999</c>, whose floor is 1, still differs from <c>2.0000000000000001</c>.
    /// </summary>
    [Fact]
    public void TryParseNumber_SpellingsOfTheSameSlots_AreEqualWithEqualHashCodes()
    {
        var plain = ExactNumber("2.0000000000000001");
        var respelled = ExactNumber("+2.00000000000000010e0");
        Assert.True(plain.TryGetJsonNumber(out var plainNumber));
        Assert.True(respelled.TryGetJsonNumber(out var respelledNumber));
        Assert.NotEqual(plainNumber, respelledNumber);

        Assert.Equal(plain, respelled);
        Assert.True(plain == respelled);
        Assert.Equal(plain.GetHashCode(), respelled.GetHashCode());
        Assert.NotEqual(plain, ExactNumber("1.9999999999999999"));
    }

    // Numeric primitives beside the exact JSON number each keeps as a number value, or null: a decimal or ulong whose
    // nearest double is whole although it holds no exact signed 64-bit integer keeps one, and a double never needs one,
    // because it is exactly the number it stands for.
    public static TheoryData<object, string?> JsonNumberOfNumericPrimitive => new()
    {
        { 2.0000000000000001m, "2.0000000000000001" },
        { -9223372036854775808.5m, "-9223372036854775808.5" },
        { 79228162514264337593543950335m, "79228162514264337593543950335" },
        { 37.5m, null },
        { 2m, null },
        { 18446744073709551615UL, "18446744073709551615" },
        { 9223372036854775807UL, null },
        { 1e20, null },
        { 2.5, null },
    };

    /// <summary>
    /// A decimal or ulong number keeps the JSON number of its exact value exactly when its nearest double is whole
    /// although it holds no exact signed 64-bit integer, so <c>2.0000000000000001m</c> is never written as 2.
    /// </summary>
    [Theory]
    [MemberData(nameof(JsonNumberOfNumericPrimitive))]
    public void Coerce_NumericPrimitive_KeepsAJsonNumberOnlyWhereItsDoubleWouldMisstateIt(object raw, string? expected)
    {
        var result = FieldValueCoercion.Coerce(raw, FieldValueKind.Number);

        Assert.True(result.Succeeded);
        Assert.Equal(expected is not null, result.Value.TryGetJsonNumber(out var jsonNumber));
        Assert.Equal(expected, jsonNumber);
    }

    // Each range-message helper for an exact integer bound, beside its literal text.
    public static TheoryData<string, string> ExactIntegerRangeMessages => new()
    {
        { ValidationMessages.FormatRangeAtMost(9007199254740993L), "The value must be at most 9007199254740993." },
        { ValidationMessages.FormatRangeAtLeast(9007199254740993L), "The value must be at least 9007199254740993." },
        {
            ValidationMessages.FormatRangeBetween(-9007199254740993L, 9007199254740993L),
            "The value must be between -9007199254740993 and 9007199254740993."
        },
        {
            ValidationMessages.FormatRangeBetween(-9007199254740993L, 37.5d),
            "The value must be between -9007199254740993 and 37.5."
        },
        {
            ValidationMessages.FormatRangeBetween(-37.5d, 9007199254740993L),
            "The value must be between -37.5 and 9007199254740993."
        },
        { ValidationMessages.FormatRangeAtMost(long.MinValue), "The value must be at most -9223372036854775808." },
    };

    /// <summary>
    /// An exact integer bound keeps every digit, and in a mixed pair the other bound keeps its shortest round-trip
    /// form, so neither is rounded through the other's type.
    /// </summary>
    [Theory]
    [MemberData(nameof(ExactIntegerRangeMessages))]
    public void RangeMessage_ExactIntegerBound_KeepsEveryDigit(string actual, string expected)
    {
        Assert.Equal(expected, actual);
    }

    /// <summary>The fractional bound of a mixed pair keeps the finite check of every number bound.</summary>
    [Fact]
    public void RangeMessage_MixedPairWithNonFiniteBound_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            "upper",
            () => ValidationMessages.FormatRangeBetween(9007199254740993L, double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(
            "lower",
            () => ValidationMessages.FormatRangeBetween(double.NegativeInfinity, 9007199254740993L));
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

    // The bound itself, the adjacent representable double inside it, and a number far beyond on the open side.
    public static TheoryData<string, double> NumberWithinOneSidedRange => new()
    {
        { "LowerOnlyNumber", 10.5d },
        { "LowerOnlyNumber", Math.BitIncrement(10.5d) },
        { "LowerOnlyNumber", 1e300d },
        { "UpperOnlyNumber", 99.5d },
        { "UpperOnlyNumber", Math.BitDecrement(99.5d) },
        { "UpperOnlyNumber", -1e300d },
    };

    /// <summary>
    /// A single bound is inclusive and constrains its own side only: the unset bound, NULL or blank, is no limit.
    /// </summary>
    [Theory]
    [MemberData(nameof(NumberWithinOneSidedRange))]
    public void ValidateInput_NumberWithinOneSidedRange_IsAccepted(string field, double raw)
    {
        var values = AssertValid(Validate(Validator(OneSidedRangeForm), (field, FieldValue.FromNumber(raw))));

        Assert.Equal(FieldValue.FromNumber(raw), values[field]);
    }

    // The adjacent representable double outside the bound, and a number far beyond it.
    public static TheoryData<string, double, string> NumberBeyondOneSidedRange => new()
    {
        { "LowerOnlyNumber", Math.BitDecrement(10.5d), ValidationMessages.FormatRangeAtLeast(10.5d) },
        { "LowerOnlyNumber", -1e300d, ValidationMessages.FormatRangeAtLeast(10.5d) },
        { "UpperOnlyNumber", Math.BitIncrement(99.5d), ValidationMessages.FormatRangeAtMost(99.5d) },
        { "UpperOnlyNumber", 1e300d, ValidationMessages.FormatRangeAtMost(99.5d) },
    };

    /// <summary>A value past the single bound reads only that bound's message, never the two-bound one.</summary>
    [Theory]
    [MemberData(nameof(NumberBeyondOneSidedRange))]
    public void ValidateInput_NumberBeyondOneSidedRange_IsSingleBoundError(string field, double raw, string expected)
    {
        var result = Validate(Validator(OneSidedRangeForm), (field, FieldValue.FromNumber(raw)));

        AssertOnlyFieldError(result, field, expected);
    }

    /// <summary>
    /// The bounds are the stored <c>MM-DD-YYYY</c> texts <c>03-01-2011</c> and <c>06-30-2012</c>; each value is the
    /// bound, the day inside it, or the <c>datetime</c> limit on the open side.
    /// </summary>
    [Theory]
    [InlineData("LowerOnlyDate", "2011-03-01")]
    [InlineData("LowerOnlyDate", "2011-03-02")]
    [InlineData("LowerOnlyDate", "9999-12-31")]
    [InlineData("UpperOnlyDate", "2012-06-30")]
    [InlineData("UpperOnlyDate", "2012-06-29")]
    [InlineData("UpperOnlyDate", "1753-01-01")]
    public void ValidateInput_DateWithinOneSidedRange_IsAccepted(string field, string raw)
    {
        var values = AssertValid(Validate(Validator(OneSidedRangeForm), (field, FieldValue.FromText(raw))));

        Assert.Equal(IsoDate(raw), values[field]);
    }

    // The day outside the bound, and the datetime limit beyond it.
    public static TheoryData<string, string, string> DateBeyondOneSidedRange => new()
    {
        { "LowerOnlyDate", "2011-02-28", ValidationMessages.FormatRangeAtLeast(new DateOnly(2011, 3, 1)) },
        { "LowerOnlyDate", "1753-01-01", ValidationMessages.FormatRangeAtLeast(new DateOnly(2011, 3, 1)) },
        { "UpperOnlyDate", "2012-07-01", ValidationMessages.FormatRangeAtMost(new DateOnly(2012, 6, 30)) },
        { "UpperOnlyDate", "9999-12-31", ValidationMessages.FormatRangeAtMost(new DateOnly(2012, 6, 30)) },
    };

    [Theory]
    [MemberData(nameof(DateBeyondOneSidedRange))]
    public void ValidateInput_DateBeyondOneSidedRange_IsSingleBoundError(string field, string raw, string expected)
    {
        var result = Validate(Validator(OneSidedRangeForm), (field, FieldValue.FromText(raw)));

        AssertOnlyFieldError(result, field, expected);
    }

    /// <summary>
    /// The single-bound messages as the client reads them, numbers in shortest round-trip form and dates as
    /// <c>YYYY-MM-DD</c>, so a swapped or reworded template fails here even though the cases above take their
    /// expected text from the same <see cref="ValidationMessages"/> helpers the validator calls.
    /// </summary>
    [Theory]
    [InlineData("LowerOnlyNumber", 10d, "The value must be at least 10.5.")]
    [InlineData("UpperOnlyNumber", 100d, "The value must be at most 99.5.")]
    [InlineData("LowerOnlyDate", "2011-02-28", "The value must be at least 2011-03-01.")]
    [InlineData("UpperOnlyDate", "2012-07-01", "The value must be at most 2012-06-30.")]
    public void ValidateInput_BeyondOneSidedRange_ReadsTheBound(string field, object raw, string expected)
    {
        var result = Validate(Validator(OneSidedRangeForm), (field, Wire(raw)));

        AssertOnlyFieldError(result, field, expected);
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
    /// A number mask counts a number as its column stores it. A <c>bigint</c> column stores the exact integer, so the
    /// 18-digit <c>999999999999999999</c> fits 18 positions and <c>1000000000000000000</c> does not; a <c>float</c>
    /// column stores the nearest double, <c>1E+18</c>, whose 19 digits do not fit either.
    /// </summary>
    [Theory]
    [InlineData("BigCount", 999999999999999999L, true)]
    [InlineData("BigCount", 1000000000000000000L, false)]
    [InlineData("FloatCount", 999999999999999999L, false)]
    public void ValidateInput_IntegerUnderNumberMask_CountsTheDigitsItsColumnStores(
        string field,
        long raw,
        bool accepted)
    {
        const string mask = "##################";
        var form = new FormMetadata(
            ViewId: 9,
            Name: "BigNumber",
            BaseTableName: "BigNumber",
            Pages: [new PageMetadata(1, "Page 1", 0, "BigNumber1")],
            Fields:
            [
                Row(1, "BigCount", NumberType, pageId: 1, ("bigint", 8)) with { Pattern = mask },
                Row(2, "FloatCount", NumberType, pageId: 1, FloatColumn) with { Pattern = mask },
            ]);

        var result = Validate(Validator(form), (field, FieldValue.FromInteger(raw)));

        if (accepted)
        {
            Assert.Equal(FieldValue.FromInteger(raw), AssertValid(result)[field]);
        }
        else
        {
            AssertOnlyFieldError(result, field, ValidationMessages.FormatPatternMismatch(mask));
        }
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

    /// <summary>
    /// <c>Choice</c> (page 1) and <c>CHOICE</c> (page 2) are Legal Values fields whose names differ only by letter
    /// case, each with its own code table. A value is checked against the submitted field's own options only, so
    /// <c>A-Alpha</c>, an option of <c>Choice</c> alone, is rejected for <c>CHOICE</c> whether or not the options map
    /// holds an entry for <c>CHOICE</c>.
    /// </summary>
    [Theory]
    [InlineData(true, "Choice", "A-Alpha", true)]
    [InlineData(true, "CHOICE", "A-Alpha", false)]
    [InlineData(true, "CHOICE", "Z-Zulu", true)]
    [InlineData(true, "Choice", "Z-Zulu", false)]
    [InlineData(false, "Choice", "A-Alpha", true)]
    [InlineData(false, "CHOICE", "A-Alpha", false)]
    public void ValidateInput_CaseVariantLegalValuesFields_EachAcceptsOnlyItsOwnOptions(
        bool upperHasEntry,
        string fieldName,
        string raw,
        bool accepted)
    {
        var form = new FormMetadata(
            ViewId: 7,
            Name: "LegalValuesCase",
            BaseTableName: "LegalValuesCase",
            Pages:
            [
                new PageMetadata(1, "Page 1", 0, "LegalValuesCase1"),
                new PageMetadata(2, "Page 2", 1, "LegalValuesCase2"),
            ],
            Fields:
            [
                Row(1, "Choice", LegalValuesType, pageId: 1, Nvarchar(255)),
                Row(2, "CHOICE", LegalValuesType, pageId: 2, Nvarchar(255)),
            ]);
        var legalValues = new Dictionary<string, IReadOnlyList<FieldOption>>(StringComparer.Ordinal)
        {
            ["Choice"] = [new FieldOption(FieldValue.FromText("A-Alpha"), "A-Alpha")],
        };
        if (upperHasEntry)
        {
            legalValues.Add("CHOICE", [new FieldOption(FieldValue.FromText("Z-Zulu"), "Z-Zulu")]);
        }

        var result = Validate(Validator(form, legalValues), (fieldName, FieldValue.FromText(raw)));

        if (accepted)
        {
            Assert.Equal(FieldValue.FromText(raw), AssertValid(result)[fieldName]);
        }
        else
        {
            AssertOnlyFieldError(result, fieldName, ValidationMessages.NotALegalValue);
        }
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
        Assert.Equal(new[] { "Zip", "Age", "Sex", RecordValidator.ValuesKey }, result.FieldErrors.Keys);
        Assert.Equal(new[] { ValidationMessages.FormatMaxLengthExceeded(5) }, result.FieldErrors["Zip"]);
        Assert.Equal(new[] { ValidationMessages.FormatRangeBetween(0d, 120d) }, result.FieldErrors["Age"]);
        Assert.Equal(new[] { ValidationMessages.NotALegalValue }, result.FieldErrors["Sex"]);
        Assert.Equal(new[] { ValidationMessages.UnknownValuesName }, result.FieldErrors[RecordValidator.ValuesKey]);
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

    /// <summary>
    /// C-3: the required flag does not make a read-only field writable. Presence alone rejects it under its canonical
    /// name, whatever the value, an explicit null included, and the required gate never runs on a rejected input.
    /// </summary>
    [Theory]
    [InlineData("AssignedCode", "A-1")]
    [InlineData("AssignedCode", null)]
    [InlineData("assignedcode", "A-1")]
    public void ValidateInput_RequiredReadOnlyFieldPresent_IsRejected(string submittedName, string? raw)
    {
        var result = Validate(Validator(RequiredReadOnlyForm), (submittedName, Wire(raw)));

        AssertOnlyFieldError(result, "AssignedCode", ValidationMessages.ReadOnlyField);
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
            ValidationMessages.FormatFieldIsRequired(PatientNamePrompt, "Page 1"),
            result.FieldErrors["PatientName"][0]);
        Assert.Equal(
            ValidationMessages.FormatFieldIsRequired(CollectionDatePrompt, "Page 2"),
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

    /// <summary>
    /// The required message takes the prompt exactly as stored, as legacy passes it to <c>string.Format</c>
    /// (<c>GuiMediator.Events.ViewExplorer.cs</c>, line 79): a null prompt is empty text, and an empty or whitespace
    /// prompt is inserted as it is, never replaced by the field name. <paramref name="expected"/> is typed out,
    /// independently of <see cref="ValidationMessages"/>.
    /// </summary>
    [Theory]
    [InlineData(null, IsRequiredOnPage1)]
    [InlineData("", IsRequiredOnPage1)]
    [InlineData("   ", "   " + IsRequiredOnPage1)]
    [InlineData("Case code:", "Case code:" + IsRequiredOnPage1)]
    public void CheckNewRecord_RequiredFieldPrompt_IsInsertedVerbatim(string? prompt, string expected)
    {
        var form = NullPromptForm with
        {
            Fields = [.. NullPromptForm.Fields.Select(row => row.Name == "CaseCode" ? row with { Prompt = prompt } : row)],
        };

        var result = Validator(form).CheckNewRecord(Normalised(("Other", FieldValue.FromText("x"))));

        AssertOnlyFieldError(result, "CaseCode", expected);
        Assert.Equal(expected, RequiredMessage(prompt, "Page 1"));
        Assert.Equal(expected, ValidationMessages.FormatFieldIsRequired(prompt, "Page 1"));
        Assert.DoesNotContain("CaseCode", expected, StringComparison.Ordinal);
    }

    /// <summary>
    /// A create can never supply a read-only field (C-3), yet the required gate takes every required data field,
    /// because legacy's required check exempts no read-only field. Each create here carries a non-null value
    /// (<c>Remarks</c> "x"), so the empty-record rule does not answer first, and a required read-only field is always
    /// listed as missing, with its prompt and page name, and the writable required field beside it is listed after
    /// it, in data-field order, when that one is missing too. A null <paramref name="submittedReporter"/> leaves
    /// <c>Reporter</c> out of the create.
    /// </summary>
    [Theory]
    [InlineData("Jones", new[] { "AssignedCode" })]
    [InlineData(null, new[] { "AssignedCode", "Reporter" })]
    public void CheckNewRecord_RequiredReadOnlyField_IsAlwaysMissing(
        string? submittedReporter,
        string[] expectedMissing)
    {
        var normalised = submittedReporter is null
            ? Normalised(("Remarks", FieldValue.FromText("x")))
            : Normalised(("Remarks", FieldValue.FromText("x")), ("Reporter", FieldValue.FromText(submittedReporter)));

        var result = Validator(RequiredReadOnlyForm).CheckNewRecord(normalised);

        AssertMissingOnRequiredReadOnlyForm(result, expectedMissing);
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

    /// <summary>
    /// Stored text is read back as it is, never normalised, so an empty string stored in a required field the
    /// <c>PUT</c> leaves untouched is missing, as legacy tests the value string for null or empty
    /// (<c>GuiMediator.Events.ViewExplorer.cs</c>, lines 74 and 114). Stored whitespace is a value, a submitted value
    /// replaces the stored empty string, and an empty string stored in the optional <c>Notes</c> blocks nothing. A null
    /// <paramref name="submittedPatientName"/> leaves <c>PatientName</c> untouched.
    /// </summary>
    [Theory]
    [InlineData("", "stored note", null, true)]
    [InlineData("   ", "stored note", null, false)]
    [InlineData("", "stored note", "Jones", false)]
    [InlineData("Smith", "", null, false)]
    public void CheckRequired_StoredEmptyText_IsMissingOnlyOnAnUntouchedRequiredField(
        string storedPatientName,
        string storedNotes,
        string? submittedPatientName,
        bool patientNameMissing)
    {
        var stored = Stored(Normalised(
            ("PatientName", FieldValue.FromText(storedPatientName)),
            ("Notes", FieldValue.FromText(storedNotes)),
            ("CollectionDate", IsoDate("2011-02-01")),
            ("Flag", FieldValue.FromBoolean(true))));
        var submitted = submittedPatientName is null
            ? Normalised(("Flag", FieldValue.FromBoolean(false)))
            : Normalised(
                ("Flag", FieldValue.FromBoolean(false)),
                ("PatientName", FieldValue.FromText(submittedPatientName)));

        var result = Validator(RequiredForm).CheckRequired(stored, submitted);

        Assert.Equal(FieldValue.FromText(storedPatientName), stored.Values["PatientName"]);
        if (patientNameMissing)
        {
            AssertOnlyFieldError(result, "PatientName", RequiredMessage(PatientNamePrompt, "Page 1"));
            return;
        }

        var values = AssertValid(result);
        Assert.Equal(submitted.Keys, values.Keys);
        Assert.All(submitted, entry => Assert.Equal(entry.Value, values[entry.Key]));
    }

    /// <summary>
    /// An update can never supply a read-only field (C-3), so the stored value of a required read-only field decides
    /// the gate: stored as null or as an empty string it is listed as missing, and stored with a value it passes. The
    /// writable required field beside it is gated on the effective record as before. A null
    /// <paramref name="storedAssignedCode"/> or <paramref name="storedReporter"/> stores null; a null
    /// <paramref name="submittedReporter"/> leaves <c>Reporter</c> untouched.
    /// </summary>
    [Theory]
    [InlineData(null, null, "Jones", new[] { "AssignedCode" })]
    [InlineData("", null, "Jones", new[] { "AssignedCode" })]
    [InlineData("", null, null, new[] { "AssignedCode", "Reporter" })]
    [InlineData("A-1", null, "Jones", new string[0])]
    [InlineData("A-1", "Smith", null, new string[0])]
    [InlineData("A-1", null, null, new[] { "Reporter" })]
    public void CheckRequired_RequiredReadOnlyField_IsDecidedByItsStoredValue(
        string? storedAssignedCode,
        string? storedReporter,
        string? submittedReporter,
        string[] expectedMissing)
    {
        var stored = Stored(Normalised(
            ("AssignedCode", storedAssignedCode is null ? FieldValue.Null : FieldValue.FromText(storedAssignedCode)),
            ("Reporter", storedReporter is null ? FieldValue.Null : FieldValue.FromText(storedReporter)),
            ("Remarks", FieldValue.FromText("stored remark"))));
        var submitted = submittedReporter is null
            ? Normalised(("Remarks", FieldValue.FromText("x")))
            : Normalised(("Remarks", FieldValue.FromText("x")), ("Reporter", FieldValue.FromText(submittedReporter)));

        var result = Validator(RequiredReadOnlyForm).CheckRequired(stored, submitted);

        if (expectedMissing.Length > 0)
        {
            AssertMissingOnRequiredReadOnlyForm(result, expectedMissing);
            return;
        }

        var values = AssertValid(result);
        Assert.Equal(submitted.Keys, values.Keys);
        Assert.All(submitted, entry => Assert.Equal(entry.Value, values[entry.Key]));
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

    // ---- Data fields whose names differ only by letter case ----

    /// <summary>
    /// <c>Age</c> and <c>AGE</c> are two fields, each with its own rule and its own values key.
    /// </summary>
    [Theory]
    [InlineData("Age", 42d, "Age")]
    [InlineData("AGE", "40-49", "AGE")]
    public void ValidateInput_CaseVariantField_IsKeyedByItsOwnName(string name, object raw, string expectedKey)
    {
        var values = AssertValid(Validate(Validator(CaseVariantForm), (name, Wire(raw))));

        var (key, value) = Assert.Single(values);
        Assert.Equal(expectedKey, key);
        Assert.Equal(Wire(raw), value);
    }

    /// <summary>
    /// A spelling equal to both <c>Age</c> and <c>AGE</c> ignoring case and to neither exactly is ambiguous: it names
    /// no field, so it is reported under the <c>values</c> parameter key with the ambiguous-name message, never under
    /// the submitted spelling or a canonical name it might mean. An exact spelling sent beside it is still accepted as
    /// its own field, and a name with no row beside it adds its own message under the same key.
    /// </summary>
    [Theory]
    [InlineData(new[] { "age" }, new[] { ValidationMessages.AmbiguousValuesName })]
    [InlineData(new[] { "aGE" }, new[] { ValidationMessages.AmbiguousValuesName })]
    [InlineData(new[] { "age", "AGE" }, new[] { ValidationMessages.AmbiguousValuesName })]
    [InlineData(new[] { "Age", "aGE" }, new[] { ValidationMessages.AmbiguousValuesName })]
    [InlineData(new[] { "age", "aGE" }, new[] { ValidationMessages.AmbiguousValuesName })]
    [InlineData(
        new[] { "Agee", "age" },
        new[] { ValidationMessages.UnknownValuesName, ValidationMessages.AmbiguousValuesName })]
    public void ValidateInput_AmbiguousCaseVariantSpelling_IsAmbiguousNameUnderValuesKey(
        string[] names,
        string[] expectedMessages)
    {
        var result = Validate(Validator(CaseVariantForm), [.. names.Select(name => (name, FieldValue.Null))]);

        AssertFieldFailure(result);
        var (key, messages) = Assert.Single(result.FieldErrors);
        Assert.Equal(RecordValidator.ValuesKey, key);
        Assert.Equal(expectedMessages, messages);
    }

    /// <summary>Each case-variant field is type-checked by its own rule, not by its namesake's.</summary>
    [Theory]
    [InlineData("AGE", 42d, ValidationMessages.TextExpected)]
    [InlineData("Age", "40-49", ValidationMessages.NumberExpected)]
    public void ValidateInput_CaseVariantFieldGivenItsNamesakesType_FailsItsOwnTypeCheck(
        string name,
        object raw,
        string expected)
    {
        var result = Validate(Validator(CaseVariantForm), (name, Wire(raw)));

        AssertOnlyFieldError(result, name, expected);
    }

    /// <summary>
    /// Two submitted keys that differ only by letter case are a repeat, even though they name two distinct fields; the
    /// error is keyed by the canonical name the second resolves to. A key that is ambiguous instead names no field
    /// (see <see cref="ValidateInput_AmbiguousCaseVariantSpelling_IsAmbiguousNameUnderValuesKey"/>).
    /// </summary>
    [Theory]
    [InlineData("Age", "AGE", "AGE")]
    [InlineData("AGE", "Age", "Age")]
    public void ValidateInput_CaseVariantFieldsBothSubmitted_IsDuplicateError(
        string first,
        string second,
        string expectedKey)
    {
        var result = Validate(Validator(CaseVariantForm), (first, FieldValue.Null), (second, FieldValue.Null));

        AssertOnlyFieldError(result, expectedKey, ValidationMessages.DuplicateField);
    }

    /// <summary>
    /// The required gate of a create holds each case-variant field apart: supplying one never satisfies the other.
    /// </summary>
    [Theory]
    [InlineData("Age", 42d, "AGE", AgeGroupPrompt, "Page 2")]
    [InlineData("AGE", "40-49", "Age", AgePrompt, "Page 1")]
    public void CheckNewRecord_OneCaseVariantRequiredFieldSupplied_ListsTheOtherAsMissing(
        string supplied,
        object raw,
        string missing,
        string missingPrompt,
        string missingPage)
    {
        var validator = Validator(CaseVariantForm);
        var input = AssertValid(Validate(validator, (supplied, Wire(raw))));

        var result = validator.CheckNewRecord(input);

        AssertOnlyFieldError(result, missing, RequiredMessage(missingPrompt, missingPage));
    }

    /// <summary>
    /// The overlay of a <c>PUT</c> keeps the stored and submitted values of <c>Age</c> and <c>AGE</c> apart, so each
    /// field's gate reads only its own effective value. A null stored value or a null
    /// <paramref name="submittedRaw"/> is an empty value.
    /// </summary>
    [Theory]
    [InlineData(42d, "40-49", "AGE", null, new[] { "AGE" })]
    [InlineData(42d, "40-49", "Age", null, new[] { "Age" })]
    [InlineData(42d, null, "Age", 43d, new[] { "AGE" })]
    [InlineData(null, "40-49", "AGE", "50-59", new[] { "Age" })]
    [InlineData(null, null, "AGE", "50-59", new[] { "Age" })]
    [InlineData(42d, "40-49", "Notes", "x", new string[0])]
    public void CheckRequired_CaseVariantFields_AreOverlaidAndGatedApart(
        double? storedAge,
        string? storedAgeGroup,
        string submittedName,
        object? submittedRaw,
        string[] expectedMissing)
    {
        var stored = Stored(Normalised(
            ("Age", storedAge is { } age ? FieldValue.FromNumber(age) : FieldValue.Null),
            ("Notes", FieldValue.Null),
            ("AGE", storedAgeGroup is null ? FieldValue.Null : FieldValue.FromText(storedAgeGroup))));
        var submitted = Normalised((submittedName, Wire(submittedRaw)));

        var result = Validator(CaseVariantForm).CheckRequired(stored, submitted);

        if (expectedMissing.Length == 0)
        {
            Assert.Equal(submitted.Keys, AssertValid(result).Keys);
            return;
        }

        AssertFieldFailure(result);
        Assert.Equal(expectedMissing, result.FieldErrors.Keys);
        foreach (var field in expectedMissing)
        {
            var expected = field == "Age"
                ? RequiredMessage(AgePrompt, "Page 1")
                : RequiredMessage(AgeGroupPrompt, "Page 2");
            Assert.Equal(new[] { expected }, result.FieldErrors[field]);
        }
    }

    /// <summary>
    /// A row repeating a data field's name exactly is no data field, so its own <c>IsRequired</c> never gates a save:
    /// <c>Age</c> binds the page-1 field alone, and a create without it lists that field once, with its own prompt and
    /// page.
    /// </summary>
    [Theory]
    [InlineData("Age", 30d, false)]
    [InlineData("Notes", "x", true)]
    public void CheckNewRecord_RowRepeatingAName_GatesOnlyTheDataField(string supplied, object raw, bool ageMissing)
    {
        var validator = Validator(RepeatedNameForm);
        var input = AssertValid(Validate(validator, (supplied, Wire(raw))));

        var result = validator.CheckNewRecord(input);

        if (ageMissing)
        {
            AssertOnlyFieldError(result, "Age", RequiredMessage(AgePrompt, "Page 1"));
        }
        else
        {
            Assert.Equal(new[] { "Age" }, AssertValid(result).Keys);
        }
    }

    /// <summary>
    /// Every canonical values map rejects two keys that differ only by letter case, which no valid input can hold,
    /// with a fixed message that carries no key.
    /// </summary>
    [Fact]
    public void CanonicalMaps_KeysDifferingOnlyByCase_AreRejected()
    {
        var validator = Validator(CaseVariantForm);
        var values = Normalised(("Age", FieldValue.FromNumber(42)), ("AGE", FieldValue.FromText("40-49")));

        var onCreate = Assert.Throws<ArgumentException>("normalised", () => validator.CheckNewRecord(values));
        var onUpdate = Assert.Throws<ArgumentException>(
            "submitted",
            () => validator.CheckRequired(Stored(values), values));
        var onResult = Assert.Throws<ArgumentException>("values", () => RecordValidationResult.Success(values));

        Assert.All(
            new[] { onCreate, onUpdate, onResult },
            exception => Assert.DoesNotContain("AGE", exception.Message, StringComparison.Ordinal));
    }

    // ---- Fixture and assertion helpers ----

    private static FormMetadata FixtureForm(string name) => name switch
    {
        "Mirror" => MirrorForm,
        "Required" => RequiredForm,
        "NullPrompt" => NullPromptForm,
        "Storage" => StorageForm,
        "OneSidedRange" => OneSidedRangeForm,
        "RequiredReadOnly" => RequiredReadOnlyForm,
        "BigBound" => BigBoundForm,
        "CaseVariant" => CaseVariantForm,
        "CaseVariantLabel" => CaseVariantLabelForm,
        "RepeatedName" => RepeatedNameForm,
        "FixedWidth" => FixedWidthForm,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "No fixture form has that name."),
    };

    private static IReadOnlyDictionary<string, IReadOnlyList<FieldOption>> LegalValuesOf(FormMetadata form) =>
        ReferenceEquals(form, MirrorForm) ? MirrorLegalValues
        : ReferenceEquals(form, FixedWidthForm) ? FixedWidthLegalValues
        : NoLegalValues;

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

    // A map of normalised values, keyed as a valid ValidateInput result or the store's read keys them: by canonical
    // name, compared ordinally.
    private static IReadOnlyDictionary<string, FieldValue> Normalised(params (string Name, FieldValue Value)[] entries)
    {
        var values = new Dictionary<string, FieldValue>(StringComparer.Ordinal);
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

    // A JSON number literal as the API's values reader parses it, its digits kept exact: a whole number within the
    // signed 64-bit range keeps its integer, and any other number its nearest double and, when its floor lies within
    // that range, the exact floor (FieldValue.TryGetFloor), but never an integer, even where the double is whole.
    // Values beyond 2^53 are built this way, because a double literal would already have rounded them.
    private static FieldValue ExactNumber(string literal)
    {
        Assert.True(FieldValueCoercion.TryParseNumber(literal, out var value));
        Assert.Equal(FieldValueKind.Number, value.Kind);
        return value;
    }

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

    // The required message as legacy formats FIELD_IS_REQUIRED_LONG: {0} the prompt, {1} the page name; a null prompt
    // is empty text, as string.Format renders a null argument.
    private static string RequiredMessage(string? prompt, string pageName) =>
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

    // A required-gate failure of RequiredReadOnlyForm listing exactly these fields, in this order, each with its own
    // required message only; both required fields are on page 1.
    private static void AssertMissingOnRequiredReadOnlyForm(RecordValidationResult result, string[] expectedMissing)
    {
        AssertFieldFailure(result);
        Assert.Equal(expectedMissing, result.FieldErrors.Keys);
        foreach (var field in expectedMissing)
        {
            var expected = field switch
            {
                "AssignedCode" => RequiredMessage(AssignedCodePrompt, "Page 1"),
                "Reporter" => RequiredMessage(ReporterPrompt, "Page 1"),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(expectedMissing),
                    field,
                    "Not a required field of RequiredReadOnlyForm."),
            };
            Assert.Equal(new[] { expected }, result.FieldErrors[field]);
        }
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

    private static (string SqlType, int MaxLengthBytes) Nchar(int characters) => ("nchar", 2 * characters);

    // A metaFields.List of count Option items, L0 to L(count - 1), so the item at index i reads Li.
    private static string OptionList(int count) => string.Join(
        ',',
        Enumerable.Range(0, count).Select(index => "L" + index.ToString(CultureInfo.InvariantCulture)));

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
