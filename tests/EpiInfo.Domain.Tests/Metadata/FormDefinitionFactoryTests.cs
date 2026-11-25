using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using EpiInfo.Domain.Diagnostics;
using EpiInfo.Domain.Forms;
using EpiInfo.Domain.Metadata;
using EpiInfo.Domain.Values;

namespace EpiInfo.Domain.Tests.Metadata;

/// <summary>
/// Unit tests of <see cref="FormDefinitionFactory"/>, the one owner of form-definition assembly, group members and
/// the data-field set: page and field order, which <c>metaFields</c> rows a page lists, the data-field counts of the
/// seeded forms, validation, options, members and pass-through values.
/// </summary>
/// <remarks>
/// The seeded forms are rebuilt from <c>testdata/forms.json</c>, read by path. That file holds only the page-table
/// columns, in an order that is not <c>TabIndex</c> order, so each field's <c>TabIndex</c> is its list position, and
/// every page gains one synthetic label and one synthetic group, as the seeded pages hold labels and groups. The
/// <c>IsReadOnly</c> and <c>MaxLength</c> values forms.json lacks are taken from <c>testdata/sql/03_forms.sql</c>.
/// No database is used.
/// </remarks>
public sealed class FormDefinitionFactoryTests
{
    private const int PageId = 1;

    // The API's wire settings: camelCase names and strict JSON numbers.
    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.Strict,
    };

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<FieldOption>> NoLegalValues =
        new Dictionary<string, IReadOnlyList<FieldOption>>(StringComparer.Ordinal);

    private static readonly Diagnostic[] NoDiagnostics = [];

    /// <summary><c>metaFieldTypes</c> as seeded by <c>testdata/sql/02_reference_data.sql</c>, lines 17-45.</summary>
    private static readonly IReadOnlyDictionary<int, string> SeededTypeNames = new Dictionary<int, string>
    {
        [1] = "Text",
        [2] = "Label/Title",
        [3] = "Text (Uppercase)",
        [4] = "Multiline",
        [5] = "Number",
        [6] = "Phone Number",
        [7] = "Date",
        [8] = "Time",
        [9] = "Date/Time",
        [10] = "Checkbox",
        [11] = "Yes/No",
        [12] = "Option",
        [13] = "Command Button",
        [14] = "Image",
        [15] = "Mirror",
        [16] = "Grid",
        [17] = "Legal Values",
        [18] = "Codes",
        [19] = "Comment Legal",
        [20] = "Relate",
        [21] = "Group",
        [22] = "RecStatus",
        [23] = "UniqueKey",
        [24] = "ForeignKey",
        [25] = "GUID",
        [26] = "GlobalRecordId",
        [27] = "List",
        [99] = "Unknown",
    };

    private static readonly IReadOnlyDictionary<string, int> SeededTypeIds =
        SeededTypeNames.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);

    /// <summary>The <c>IsReadOnly = 1</c> rows of <c>testdata/sql/03_forms.sql</c>.</summary>
    private static readonly HashSet<(string Form, string Field)> SeededReadOnlyFields =
    [
        ("CaseReport", "StateIDNumber"),
        ("CaseReport", "CDCIDNumber"),
        ("CaseReport", "ReportedToCDCDate"),
        ("FoodHistory", "OnsetWeek"),
    ];

    /// <summary>The rows of <c>testdata/sql/03_forms.sql</c> whose <c>MaxLength</c> is above 0.</summary>
    private static readonly IReadOnlyDictionary<(string Form, string Field), int> SeededMaxLengths =
        new Dictionary<(string Form, string Field), int> { [("CaseReport", "Zip")] = 5 };

    /// <summary>The seeded <c>Yes/No</c> fields, all on <c>tinyint</c> columns and so unsupported.</summary>
    private static readonly IReadOnlyDictionary<string, string[]> SeededYesNoFields = new Dictionary<string, string[]>
    {
        ["CaseReport"] = [],
        ["FoodHistory"] =
        [
            "ILL", "DoctorVisit", "Hospitalized", "Antibiotics", "Died", "StoolSubmitted", "O157H7isolated",
        ],
    };

    private static readonly string[] SystemFieldNames = ["UniqueKey", "RecStatus", "GlobalRecordId"];

    private static readonly Lazy<JsonElement> FormsJson = new(LoadFormsJson);

    /// <summary>
    /// Adjacent schema-valid tab indexes, lower first, as invariant text because attributes cannot hold decimal
    /// constants: 2^53 and 2^53 + 1, and the largest and the smallest adjacent pair a <c>decimal(18,0)</c> holds.
    /// One IEEE double holds both values of each pair.
    /// </summary>
    [Theory]
    [InlineData("9007199254740992", "9007199254740993")]
    [InlineData("999999999999999998", "999999999999999999")]
    [InlineData("-999999999999999999", "-999999999999999998")]
    public void TabIndex_AdjacentValuesWithReversedFieldIds_StayExactAndOrderAheadOfFieldId(
        string lowerText,
        string higherText)
    {
        decimal lower = decimal.Parse(lowerText, CultureInfo.InvariantCulture);
        decimal higher = decimal.Parse(higherText, CultureInfo.InvariantCulture);
        Assert.Equal((double)lower, (double)higher);

        // Listed in FieldId order, as the metadata query returns them; the lower tab index has the higher FieldId.
        FieldMetadata later = TextRow(fieldId: 1, name: "Later", tabIndex: higher);
        FieldMetadata earlier = TextRow(fieldId: 2, name: "Earlier", tabIndex: lower);

        Assert.Equal(lower, earlier.TabIndex);
        Assert.Equal(higher, later.TabIndex);
        Assert.NotEqual(earlier.TabIndex, later.TabIndex);

        FieldMetadata[] ordered = new[] { later, earlier }
            .OrderBy(row => row.TabIndex ?? 0)
            .ThenBy(row => row.FieldId)
            .ToArray();

        Assert.Equal(["Earlier", "Later"], ordered.Select(row => row.Name));
        Assert.Equal([2, 1], ordered.Select(row => row.FieldId));

        FieldDefinition earlierDefinition = Definition(earlier);
        FieldDefinition laterDefinition = Definition(later);

        Assert.Equal(lower, earlierDefinition.TabIndex);
        Assert.Equal(higher, laterDefinition.TabIndex);
        Assert.Contains(
            $"\"tabIndex\":{lowerText},",
            JsonSerializer.Serialize(earlierDefinition, WireOptions),
            StringComparison.Ordinal);
        Assert.Contains(
            $"\"tabIndex\":{higherText},",
            JsonSerializer.Serialize(laterDefinition, WireOptions),
            StringComparison.Ordinal);

        FormMetadata form = SinglePageForm(later, earlier);
        PageDefinition page = Assert.Single(Create(form).Pages);
        Assert.Equal(["Earlier", "Later"], page.Fields.Select(field => field.Name));
        Assert.Equal([lower, higher], page.Fields.Select(field => field.TabIndex));
        Assert.Equal(["Earlier", "Later"], FormDefinitionFactory.GetDataFields(form).Select(field => field.Name));
    }

    [Fact]
    public void Create_OrdersPagesByPositionAndFieldsByTabIndexThenFieldId()
    {
        FormMetadata form = new(
            7,
            "Synthetic",
            "Syn",
            [new PageMetadata(20, "Second", 1, "Syn20"), new PageMetadata(10, "First", 0, "Syn10")],
            [
                Field(1, "A", "Text", pageId: 10, tabIndex: 3, sqlType: "nvarchar(255)"),
                Field(2, "B", "Text", pageId: 10, tabIndex: 1, sqlType: "nvarchar(255)"),
                Field(3, "C", "Text", pageId: 10, tabIndex: 2, sqlType: "nvarchar(255)"),
                Field(9, "D", "Text", pageId: 20, tabIndex: 5, sqlType: "nvarchar(255)"),
                Field(8, "E", "Text", pageId: 20, tabIndex: 5, sqlType: "nvarchar(255)"),
            ]);

        FormDefinition definition = Create(form);

        Assert.Equal([10, 20], definition.Pages.Select(page => page.PageId));
        Assert.Equal(["First", "Second"], definition.Pages.Select(page => page.Name));
        Assert.Equal([0, 1], definition.Pages.Select(page => page.Position));

        PageDefinition first = definition.Pages[0];
        Assert.Equal(["B", "C", "A"], first.Fields.Select(field => field.Name));
        Assert.Equal([1m, 2m, 3m], first.Fields.Select(field => field.TabIndex));

        // Equal tab indexes fall back to FieldId, whatever order the rows arrive in.
        PageDefinition second = definition.Pages[1];
        Assert.Equal(["E", "D"], second.Fields.Select(field => field.Name));
        Assert.Equal([5m, 5m], second.Fields.Select(field => field.TabIndex));

        Assert.All(definition.Pages, page => Assert.All(page.Fields, field => Assert.Equal(page.PageId, field.PageId)));

        // The data fields keep the same storage order, which the grid columns are taken from.
        DataFieldSet dataFields = FormDefinitionFactory.GetDataFields(form);
        Assert.Equal(["B", "C", "A", "E", "D"], dataFields.Select(field => field.Name));
        Assert.Equal([10, 10, 10, 20, 20], dataFields.Select(field => field.Page.PageId));
    }

    /// <summary>
    /// A page lists every row with its <c>PageId</c>, labels, groups and unsupported fields included, and never a
    /// system row. The system rows carry their base-table columns here, so their exclusion is shown to rest on the
    /// null <c>PageId</c> alone.
    /// </summary>
    [Fact]
    public void Create_ListsEveryRowOfThePageAndNoSystemRow()
    {
        FormMetadata form = SinglePageForm(
        [
            .. SystemRows(firstFieldId: 1, withBaseTableColumns: true),
            Field(4, "Heading", "Label/Title", tabIndex: 1),
            Field(5, "Section", "Group", tabIndex: 2, list: "Name,Button,Flag"),
            Field(6, "Name", "Text", tabIndex: 3, sqlType: "nvarchar(255)"),
            Field(7, "Button", "Command Button", tabIndex: 4),
            Field(8, "Flag", "Yes/No", tabIndex: 5, sqlType: "tinyint"),
        ]);

        FormDefinition definition = Create(form);

        PageDefinition page = Assert.Single(definition.Pages);
        Assert.Equal(["Heading", "Section", "Name", "Button", "Flag"], page.Fields.Select(field => field.Name));
        Assert.Equal(
            [FieldType.Label, FieldType.Group, FieldType.Text, FieldType.Unsupported, FieldType.Unsupported],
            page.Fields.Select(field => field.FieldType));
        Assert.DoesNotContain(
            definition.Pages.SelectMany(listed => listed.Fields),
            field => SystemFieldNames.Contains(field.Name, StringComparer.OrdinalIgnoreCase));

        DataFieldMetadata dataField = Assert.Single(FormDefinitionFactory.GetDataFields(form));
        Assert.Equal("Name", dataField.Name);
    }

    /// <summary>
    /// The SC-8 reconciliation: the seeded page tables hold 43 and 74 data columns, and the seven <c>Yes/No</c>
    /// columns of <c>FoodHistory</c> are unsupported, leaving 43 and 67 data fields.
    /// </summary>
    [Theory]
    [InlineData("CaseReport", 43, 43)]
    [InlineData("FoodHistory", 74, 67)]
    public void GetDataFields_SeededForm_HoldsEveryPageTableColumnExceptTheUnsupportedYesNoFields(
        string formName,
        int pageTableColumns,
        int expectedDataFields)
    {
        FormMetadata form = SeededForm(formName);
        (string Name, int PageId)[] columns = SeededColumns(formName);
        Assert.Equal(pageTableColumns, columns.Length);

        DataFieldSet dataFields = FormDefinitionFactory.GetDataFields(form);

        Assert.Equal(expectedDataFields, dataFields.Count);
        string[] expectedNames = columns
            .Select(column => column.Name)
            .Except(SeededYesNoFields[formName], StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expectedNames, dataFields.Select(field => field.Name).Order(StringComparer.Ordinal));

        Dictionary<string, int> pageByName = columns.ToDictionary(column => column.Name, column => column.PageId);
        Assert.All(dataFields, field =>
        {
            Assert.NotNull(field.Column);
            Assert.Equal(field.Name, field.Column.Name);
            Assert.Equal(pageByName[field.Name], field.Page.PageId);
            Assert.Equal(field.Field.PageId, field.Page.PageId);
            Assert.NotEqual(FieldType.Label, field.FieldType);
            Assert.NotEqual(FieldType.Group, field.FieldType);
            Assert.NotEqual(FieldType.Unsupported, field.FieldType);
        });
        Assert.DoesNotContain(
            dataFields,
            field => field.Name.StartsWith("SynLabel", StringComparison.Ordinal)
                || field.Name.StartsWith("SynGroup", StringComparison.Ordinal)
                || SystemFieldNames.Contains(field.Name, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("ILL")]
    [InlineData("O157H7isolated")]
    [InlineData("SynLabel2")]
    [InlineData("SynGroup3")]
    [InlineData("UniqueKey")]
    [InlineData("RecStatus")]
    [InlineData("GlobalRecordId")]
    [InlineData("NoSuchField")]
    [InlineData("")]
    public void DataFieldSet_NameOfNoDataField_IsNotFound(string name)
    {
        DataFieldSet dataFields = FormDefinitionFactory.GetDataFields(SeededForm("FoodHistory"));

        Assert.False(dataFields.TryGet(name, out DataFieldMetadata? field));
        Assert.Null(field);
        Assert.Null(dataFields.Find(name));
    }

    [Fact]
    public void DataFieldSet_NameInAnyCase_ResolvesToTheCanonicalField()
    {
        DataFieldSet dataFields = FormDefinitionFactory.GetDataFields(SeededForm("FoodHistory"));

        Assert.True(dataFields.TryGet("lastname", out DataFieldMetadata? lastName));
        Assert.Equal("LastName", lastName.Name);
        Assert.Equal(FieldType.Text, lastName.FieldType);
        Assert.Equal(2, lastName.Page.PageId);

        DataFieldMetadata? found = dataFields.Find("LASTNAME");
        Assert.NotNull(found);
        Assert.Same(lastName, found);
    }

    /// <summary>
    /// Each seeded page lists its page-table columns in <c>TabIndex</c> order, then its synthetic label and group:
    /// 43 + 2 fields on <c>CaseReport</c>, and 45 + 2 and 29 + 2 on <c>FoodHistory</c>.
    /// </summary>
    [Theory]
    [InlineData("CaseReport", new[] { 45 }, 43, 0)]
    [InlineData("FoodHistory", new[] { 47, 31 }, 74, 7)]
    public void Create_SeededForm_ListsEveryPageRowAndFlagsOnlyTheYesNoFieldsUnsupported(
        string formName,
        int[] expectedPageSizes,
        int expectedColumnBearingFields,
        int expectedUnsupported)
    {
        FormDefinition definition = SeededDefinition(formName);
        (string Name, int PageId)[] columns = SeededColumns(formName);

        Assert.Equal(expectedPageSizes, definition.Pages.Select(page => page.Fields.Count));
        foreach (PageDefinition page in definition.Pages)
        {
            string[] expectedNames =
            [
                .. columns.Where(column => column.PageId == page.PageId).Select(column => column.Name),
                $"SynLabel{page.PageId}",
                $"SynGroup{page.PageId}",
            ];
            Assert.Equal(expectedNames, page.Fields.Select(field => field.Name));
        }

        FieldDefinition[] fields = definition.Pages.SelectMany(page => page.Fields).ToArray();
        Assert.Equal(
            expectedColumnBearingFields,
            fields.Count(field => field.FieldType is not (FieldType.Label or FieldType.Group)));

        FieldDefinition[] unsupported = fields.Where(field => field.Unsupported).ToArray();
        Assert.Equal(expectedUnsupported, unsupported.Length);
        Assert.Equal(SeededYesNoFields[formName], unsupported.Select(field => field.Name));
        Assert.All(unsupported, field =>
        {
            Assert.Equal(FieldType.Unsupported, field.FieldType);
            Assert.Equal("Yes/No", field.UnsupportedType);
        });
    }

    [Fact]
    public void Create_SeededPages_CarryIdNameAndPosition()
    {
        Assert.Equal(
            [(1, "Page 1", 0)],
            SeededDefinition("CaseReport").Pages.Select(page => (page.PageId, page.Name, page.Position)));
        Assert.Equal(
            [(2, "Page 1", 0), (3, "Page 2", 1)],
            SeededDefinition("FoodHistory").Pages.Select(page => (page.PageId, page.Name, page.Position)));
    }

    /// <summary>
    /// The seeded <c>DemographicInformation</c> group (<c>testdata/sql/03_forms.sql</c>, FieldId 20). Its members
    /// keep <c>List</c> order, which is not tab order: the client sorts them by <c>tabIndex</c>.
    /// </summary>
    [Fact]
    public void Create_Group_GetsItsListMembersInListOrder()
    {
        FormMetadata caseReport = SeededForm("CaseReport");
        FieldMetadata demographicInformation = Field(
            10_000,
            "DemographicInformation",
            "Group",
            tabIndex: 11,
            prompt: "Demographic Information",
            list: "Race,Ethnicity,Sex,Age,Zip,County,State");
        FormMetadata form = caseReport with { Fields = [.. caseReport.Fields, demographicInformation] };

        FormDefinition definition = Create(form, SeededLegalValueOptions("CaseReport"));

        FieldDefinition group = FieldNamed(definition, "DemographicInformation");
        Assert.Equal(FieldType.Group, group.FieldType);
        Assert.Equal(11m, group.TabIndex);
        Assert.Equal(["Race", "Ethnicity", "Sex", "Age", "Zip", "County", "State"], group.Members);
        Assert.Equal(["LastName", "FirstName"], FieldNamed(definition, "SynGroup1").Members);
    }

    /// <summary>
    /// Each <c>List</c> entry is trimmed and resolved, ignoring case, to a field the definition lists; unknown names,
    /// system rows, repeats and the group itself are dropped, and a group with no list has no members.
    /// </summary>
    [Fact]
    public void Create_GroupList_ResolvesListedFieldsOnly()
    {
        FormMetadata form = SinglePageForm(
        [
            .. SystemRows(firstFieldId: 1, withBaseTableColumns: false),
            Field(4, "Name", "Text", tabIndex: 1, sqlType: "nvarchar(255)"),
            Field(5, "Flag", "Yes/No", tabIndex: 2, sqlType: "tinyint"),
            Field(6, "Section", "Group", tabIndex: 3, list: " name ,NoSuchField,GlobalRecordId,Section,,NAME,Flag"),
            Field(7, "Empty", "Group", tabIndex: 4),
        ]);

        FormDefinition definition = Create(form);

        Assert.Equal(["Name", "Flag"], FieldNamed(definition, "Section").Members);
        IReadOnlyList<string>? empty = FieldNamed(definition, "Empty").Members;
        Assert.NotNull(empty);
        Assert.Empty(empty);
    }

    [Theory]
    [InlineData("CaseReport")]
    [InlineData("FoodHistory")]
    public void Create_FieldOtherThanAGroup_HasNoMembers(string formName)
    {
        FieldDefinition[] fields = SeededDefinition(formName).Pages.SelectMany(page => page.Fields).ToArray();

        Assert.Contains(fields, field => field.FieldType == FieldType.Text);
        Assert.Contains(fields, field => field.FieldType == FieldType.Label);
        Assert.All(fields.Where(field => field.FieldType != FieldType.Group), field => Assert.Null(field.Members));
        Assert.All(fields.Where(field => field.FieldType == FieldType.Group), field => Assert.NotNull(field.Members));
    }

    /// <summary>Date bounds are stored <c>MM-DD-YYYY</c> and leave the factory as dates.</summary>
    [Theory]
    [InlineData("FoodHistory", "DateofInterview", 2011, 1, 15, 2012, 12, 31)]
    [InlineData("CaseReport", "OnsetDate", 1900, 1, 1, 2050, 12, 31)]
    public void Create_DateBounds_BecomeDates(
        string formName,
        string fieldName,
        int lowerYear,
        int lowerMonth,
        int lowerDay,
        int upperYear,
        int upperMonth,
        int upperDay)
    {
        FieldDefinition field = FieldNamed(SeededDefinition(formName), fieldName);

        Assert.Equal(FieldType.Date, field.FieldType);
        Assert.Equal(FieldValue.FromDate(new DateOnly(lowerYear, lowerMonth, lowerDay)), field.Validation.Lower);
        Assert.Equal(FieldValue.FromDate(new DateOnly(upperYear, upperMonth, upperDay)), field.Validation.Upper);
    }

    [Theory]
    [InlineData("CaseReport", "Age", 0d, 120d)]
    [InlineData("FoodHistory", "FeverTemp", 80d, 110d)]
    public void Create_NumberBounds_BecomeNumbers(string formName, string fieldName, double lower, double upper)
    {
        FieldDefinition field = FieldNamed(SeededDefinition(formName), fieldName);

        Assert.Equal(FieldType.Number, field.FieldType);
        Assert.Equal(FieldValue.FromNumber(lower), field.Validation.Lower);
        Assert.Equal(FieldValue.FromNumber(upper), field.Validation.Upper);
    }

    /// <summary>The seeded unset bounds are empty strings, as on <c>AntibioticUseDays</c> and <c>DOB</c>.</summary>
    [Theory]
    [InlineData("CaseReport", "AntibioticUseDays")]
    [InlineData("CaseReport", "ReportedToCDCDate")]
    [InlineData("FoodHistory", "DOB")]
    [InlineData("FoodHistory", "OnsetWeek")]
    public void Create_SeededEmptyBounds_AreNoBounds(string formName, string fieldName)
    {
        FieldDefinition field = FieldNamed(SeededDefinition(formName), fieldName);

        Assert.Null(field.Validation.Lower);
        Assert.Null(field.Validation.Upper);
    }

    [Theory]
    [InlineData("Number", "float", "")]
    [InlineData("Number", "float", "   ")]
    [InlineData("Number", "float", null)]
    [InlineData("Date", "datetime", "")]
    [InlineData("Date", "datetime", "   ")]
    [InlineData("Date", "datetime", null)]
    public void Create_UnsetBound_IsNoBound(string typeName, string sqlType, string? bound)
    {
        FieldDefinition field = SingleDefinition(
            Field(1, "Bounded", typeName, tabIndex: 1, sqlType: sqlType, lower: bound, upper: bound));

        Assert.Null(field.Validation.Lower);
        Assert.Null(field.Validation.Upper);
    }

    [Theory]
    [InlineData(5, 5)]
    [InlineData(0, null)]
    [InlineData(null, null)]
    public void Create_MaxLength_IsCarriedWhenPositive(int? maxLength, int? expected)
    {
        FieldDefinition field = SingleDefinition(
            Field(1, "Code", "Text", tabIndex: 1, sqlType: "nvarchar(4000)", maxLength: maxLength));

        Assert.Equal(expected, field.Validation.MaxLength);
    }

    [Fact]
    public void Create_SeededMaxLength_IsSetOnZipOnly()
    {
        FieldDefinition[] fields = SeededDefinition("CaseReport").Pages.SelectMany(page => page.Fields).ToArray();

        Assert.Equal(5, FieldNamed(fields, "Zip").Validation.MaxLength);
        Assert.All(fields.Where(field => field.Name != "Zip"), field => Assert.Null(field.Validation.MaxLength));
    }

    [Theory]
    [InlineData("CaseReport", new[] { "StateIDNumber", "CDCIDNumber", "ReportedToCDCDate" })]
    [InlineData("FoodHistory", new[] { "OnsetWeek" })]
    public void Create_ReadOnly_MirrorsIsReadOnly(string formName, string[] readOnlyFields)
    {
        FieldDefinition[] fields = SeededDefinition(formName).Pages.SelectMany(page => page.Fields).ToArray();

        Assert.Equal(readOnlyFields, fields.Where(field => field.Validation.ReadOnly).Select(field => field.Name));
    }

    [Theory]
    [InlineData("CaseReport", "StateIDNumber", FieldType.Text)]
    [InlineData("FoodHistory", "OnsetWeek", FieldType.Number)]
    public void Create_SeededReadOnlyField_KeepsItsTypeAndIsReadOnly(
        string formName,
        string fieldName,
        FieldType expectedType)
    {
        FieldDefinition field = FieldNamed(SeededDefinition(formName), fieldName);

        Assert.Equal(expectedType, field.FieldType);
        Assert.True(field.Validation.ReadOnly);
    }

    [Fact]
    public void Create_Required_MirrorsIsRequired()
    {
        FormDefinition definition = Create(SinglePageForm(
            Field(1, "Mandatory", "Text", tabIndex: 1, sqlType: "nvarchar(255)", isRequired: true),
            Field(2, "Optional", "Text", tabIndex: 2, sqlType: "nvarchar(255)")));

        Assert.True(FieldNamed(definition, "Mandatory").Validation.Required);
        Assert.False(FieldNamed(definition, "Optional").Validation.Required);

        // No seeded field is required.
        Assert.All(
            new[] { "CaseReport", "FoodHistory" }.SelectMany(name => SeededDefinition(name).Pages)
                .SelectMany(page => page.Fields),
            field => Assert.False(field.Validation.Required));
    }

    [Theory]
    [InlineData("CaseReport", "PhoneNumber", "###-###-####")]
    [InlineData("CaseReport", "Age", "###")]
    [InlineData("FoodHistory", "FeverTemp", "###.#")]
    [InlineData("FoodHistory", "DateofInterview", "MM-DD-YYYY")]
    [InlineData("FoodHistory", "CaseID", null)]
    [InlineData("CaseReport", "LastName", null)]
    public void Create_SeededPattern_IsCarriedOrNull(string formName, string fieldName, string? expected)
    {
        Assert.Equal(expected, FieldNamed(SeededDefinition(formName), fieldName).Validation.Pattern);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Create_UnsetPattern_IsNull(string? pattern)
    {
        FieldDefinition field = SingleDefinition(
            Field(1, "Masked", "Text", tabIndex: 1, sqlType: "nvarchar(255)", pattern: pattern));

        Assert.Null(field.Validation.Pattern);
    }

    [Theory]
    [InlineData("CaseReport", "LastName", "Patient's Name")]
    [InlineData("CaseReport", "OnsetDate", "Date of onset of\r\nfirst symptoms")]
    [InlineData("FoodHistory", "ILL", "Was the patient ill?")]
    public void Create_Prompt_IsCarriedVerbatim(string formName, string fieldName, string prompt)
    {
        Assert.Equal(prompt, FieldNamed(SeededDefinition(formName), fieldName).Prompt);
    }

    [Fact]
    public void Create_MissingPrompt_StaysNull()
    {
        Assert.Null(SingleDefinition(Field(1, "Unprompted", "Text", tabIndex: 1, sqlType: "nvarchar(255)")).Prompt);
    }

    [Fact]
    public void Create_YesNoOnTinyint_IsUnsupportedAndNamesItsType()
    {
        FieldDefinition ill = FieldNamed(SeededDefinition("FoodHistory"), "ILL");

        Assert.Equal(FieldType.Unsupported, ill.FieldType);
        Assert.True(ill.Unsupported);
        Assert.Equal("Yes/No", ill.UnsupportedType);
        Assert.Equal(FieldValidation.None, ill.Validation);
        Assert.Null(ill.Options);
    }

    /// <summary>
    /// The seeded <c>GetCoordinates</c> button (<c>testdata/sql/03_forms.sql</c>, FieldId 1022), which owns no column.
    /// </summary>
    [Fact]
    public void Create_CommandButton_IsUnsupportedAndNamesItsType()
    {
        FormMetadata foodHistory = SeededForm("FoodHistory");
        FieldMetadata getCoordinates = Field(
            10_000,
            "GetCoordinates",
            "Command Button",
            pageId: 2,
            tabIndex: 25,
            prompt: "Get Coordinates");
        FormMetadata form = foodHistory with { Fields = [.. foodHistory.Fields, getCoordinates] };

        FieldDefinition button = FieldNamed(Create(form, SeededLegalValueOptions("FoodHistory")), "GetCoordinates");

        Assert.Equal(FieldType.Unsupported, button.FieldType);
        Assert.True(button.Unsupported);
        Assert.Equal("Command Button", button.UnsupportedType);
        Assert.Equal(2, button.PageId);
        Assert.DoesNotContain(FormDefinitionFactory.GetDataFields(form), field => field.Name == "GetCoordinates");
    }

    [Theory]
    [InlineData("CaseReport")]
    [InlineData("FoodHistory")]
    public void Create_SupportedField_IsNotFlaggedUnsupported(string formName)
    {
        FieldDefinition[] supported = SeededDefinition(formName).Pages
            .SelectMany(page => page.Fields)
            .Where(field => field.FieldType != FieldType.Unsupported)
            .ToArray();

        Assert.NotEmpty(supported);
        Assert.All(supported, field =>
        {
            Assert.False(field.Unsupported);
            Assert.Null(field.UnsupportedType);
        });
    }

    /// <summary>
    /// Only Option and Legal Values fields carry options. The <c>Comment Legal</c> field <c>State</c> names a code
    /// table but maps to text, so it carries none.
    /// </summary>
    [Theory]
    [InlineData("FoodHistory", "FirstName", FieldType.Text)]
    [InlineData("FoodHistory", "HomePhone", FieldType.Text)]
    [InlineData("FoodHistory", "Age", FieldType.Number)]
    [InlineData("FoodHistory", "DOB", FieldType.Date)]
    [InlineData("FoodHistory", "White", FieldType.Checkbox)]
    [InlineData("FoodHistory", "PatientAddress", FieldType.Multiline)]
    [InlineData("FoodHistory", "SynLabel2", FieldType.Label)]
    [InlineData("FoodHistory", "SynGroup3", FieldType.Group)]
    [InlineData("FoodHistory", "ILL", FieldType.Unsupported)]
    [InlineData("CaseReport", "State", FieldType.Text)]
    public void Create_FieldOtherThanOptionOrLegalValues_HasNoOptions(
        string formName,
        string fieldName,
        FieldType expectedType)
    {
        FieldDefinition field = FieldNamed(SeededDefinition(formName), fieldName);

        Assert.Equal(expectedType, field.FieldType);
        Assert.Null(field.Options);
    }

    [Fact]
    public void Create_OptionField_GetsIndexedItemsBeforeTheDoubleBar()
    {
        FieldDefinition field = SingleDefinition(
            Field(1, "Colour", "Option", tabIndex: 1, sqlType: "smallint", list: "Red,Green,Blue||1,2,3"));

        Assert.Equal(FieldType.Option, field.FieldType);
        Assert.Equal(
            [
                new FieldOption(FieldValue.FromIndex(0), "Red"),
                new FieldOption(FieldValue.FromIndex(1), "Green"),
                new FieldOption(FieldValue.FromIndex(2), "Blue"),
            ],
            field.Options);
    }

    /// <summary>
    /// Each seeded Legal Values field gets its own code-table rows, in the order given, because <c>Sort</c> is 0.
    /// </summary>
    [Theory]
    [InlineData("Sex")]
    [InlineData("EthnicityGroup")]
    [InlineData("LooseStoolsPerDay")]
    public void Create_LegalValuesField_GetsItsEntryInInputOrder(string fieldName)
    {
        IReadOnlyDictionary<string, IReadOnlyList<FieldOption>> options = SeededLegalValueOptions("FoodHistory");

        FieldDefinition field = FieldNamed(Create(SeededForm("FoodHistory"), options), fieldName);

        Assert.Equal(FieldType.LegalValues, field.FieldType);
        Assert.Equal(options[fieldName], field.Options);
    }

    [Fact]
    public void Create_SeededSex_GetsTheAdministrativeSexCodes()
    {
        FieldDefinition sex = FieldNamed(SeededDefinition("FoodHistory"), "Sex");

        Assert.Equal(
            [
                new FieldOption(FieldValue.FromText("F-Female"), "F-Female"),
                new FieldOption(FieldValue.FromText("M-Male"), "M-Male"),
                new FieldOption(FieldValue.FromText("U-Unknown"), "U-Unknown"),
            ],
            sex.Options);
    }

    [Fact]
    public void Create_SortedLegalValues_AreOrderedByText()
    {
        FieldMetadata sorted = Field(1, "Sex", "Legal Values", tabIndex: 1, sqlType: "nvarchar(255)", sort: true);
        Dictionary<string, IReadOnlyList<FieldOption>> options = new(StringComparer.Ordinal)
        {
            ["Sex"] = [TextOption("U-Unknown"), TextOption("F-Female"), TextOption("M-Male")],
        };

        FieldDefinition field = Assert.Single(Assert.Single(Create(SinglePageForm(sorted), options).Pages).Fields);

        Assert.Equal([TextOption("F-Female"), TextOption("M-Male"), TextOption("U-Unknown")], field.Options);
    }

    [Fact]
    public void Create_LegalValuesKey_MatchesTheFieldNameIgnoringCase()
    {
        Dictionary<string, IReadOnlyList<FieldOption>> options = new(StringComparer.Ordinal)
        {
            ["sex"] = [TextOption("F-Female")],
        };
        FormMetadata form = SinglePageForm(
            Field(1, "Sex", "Legal Values", tabIndex: 1, sqlType: "nvarchar(255)"),
            Field(2, "Uncoded", "Legal Values", tabIndex: 2, sqlType: "nvarchar(255)"));

        FormDefinition definition = Create(form, options);

        Assert.Equal([TextOption("F-Female")], FieldNamed(definition, "Sex").Options);
        IReadOnlyList<FieldOption>? uncoded = FieldNamed(definition, "Uncoded").Options;
        Assert.NotNull(uncoded);
        Assert.Empty(uncoded);
    }

    [Fact]
    public void Create_CheckCodeDiagnostics_PassThroughUnchanged()
    {
        FormMetadata form = SeededForm("FoodHistory");
        Diagnostic[] diagnostics =
        [
            new Diagnostic(DiagnosticSeverity.Warning, "FoodHistory", 3, "GOTO", "unsupported command"),
        ];

        Assert.Empty(FormDefinitionFactory.Create(form, NoLegalValues, NoDiagnostics).UnsupportedCheckCode);
        Assert.Equal(diagnostics, FormDefinitionFactory.Create(form, NoLegalValues, diagnostics).UnsupportedCheckCode);
    }

    [Theory]
    [InlineData("CaseReport", 1)]
    [InlineData("FoodHistory", 2)]
    public void Create_SeededForm_CarriesViewIdAndName(string formName, int viewId)
    {
        FormDefinition definition = SeededDefinition(formName);

        Assert.Equal(viewId, definition.ViewId);
        Assert.Equal(formName, definition.Name);
    }

    [Fact]
    public void Create_PermissionFlags_AreNullUnlessGivenAndCarriedWhenGiven()
    {
        FormMetadata form = SeededForm("CaseReport");

        FormDefinition notChecked = FormDefinitionFactory.Create(form, NoLegalValues, NoDiagnostics);
        Assert.Null(notChecked.CanInsert);
        Assert.Null(notChecked.CanUpdate);
        Assert.Null(notChecked.CanDelete);

        FormDefinition checkedFlags = FormDefinitionFactory.Create(
            form,
            NoLegalValues,
            NoDiagnostics,
            canInsert: true,
            canUpdate: false,
            canDelete: true);
        Assert.True(checkedFlags.CanInsert);
        Assert.False(checkedFlags.CanUpdate);
        Assert.True(checkedFlags.CanDelete);
    }

    [Fact]
    public void Create_NullArgumentOrDiagnostic_IsRejected()
    {
        FormMetadata form = SeededForm("CaseReport");

        Assert.Throws<ArgumentNullException>(
            "form",
            () => FormDefinitionFactory.Create(null!, NoLegalValues, NoDiagnostics));
        Assert.Throws<ArgumentNullException>(
            "legalValueOptions",
            () => FormDefinitionFactory.Create(form, null!, NoDiagnostics));
        Assert.Throws<ArgumentNullException>(
            "unsupportedCheckCode",
            () => FormDefinitionFactory.Create(form, NoLegalValues, null!));
        Assert.Throws<ArgumentException>(
            "unsupportedCheckCode",
            () => FormDefinitionFactory.Create(form, NoLegalValues, [null!]));
        Assert.Throws<ArgumentNullException>("form", () => FormDefinitionFactory.GetDataFields(null!));
    }

    private static FormDefinition Create(
        FormMetadata form,
        IReadOnlyDictionary<string, IReadOnlyList<FieldOption>>? legalValueOptions = null) =>
        FormDefinitionFactory.Create(form, legalValueOptions ?? NoLegalValues, NoDiagnostics);

    private static FieldDefinition SingleDefinition(FieldMetadata field) =>
        Assert.Single(Assert.Single(Create(SinglePageForm(field)).Pages).Fields);

    private static FieldDefinition FieldNamed(FormDefinition definition, string name) =>
        FieldNamed(definition.Pages.SelectMany(page => page.Fields), name);

    private static FieldDefinition FieldNamed(IEnumerable<FieldDefinition> fields, string name) =>
        Assert.Single(fields, field => field.Name == name);

    private static FieldOption TextOption(string text) => new(FieldValue.FromText(text), text);

    private static FormMetadata SinglePageForm(params FieldMetadata[] fields) =>
        new(9, "Synthetic", "Synthetic", [new PageMetadata(PageId, "Page 1", 0, $"Synthetic{PageId}")], fields);

    /// <summary>
    /// A <c>metaFields</c> row of a seeded type. <paramref name="sqlType"/> names its page-table column in the
    /// forms.json spelling, and a null type leaves the row without a column, as labels, groups and buttons are.
    /// </summary>
    private static FieldMetadata Field(
        int fieldId,
        string name,
        string typeName,
        int? pageId = PageId,
        decimal? tabIndex = null,
        string? sqlType = null,
        string? prompt = null,
        string? lower = null,
        string? upper = null,
        string? pattern = null,
        int? maxLength = null,
        bool isRequired = false,
        bool isReadOnly = false,
        string? list = null,
        bool sort = false) => new()
        {
            FieldId = fieldId,
            Name = name,
            FieldTypeId = SeededTypeIds[typeName],
            FieldTypeName = typeName,
            Prompt = prompt,
            TabIndex = tabIndex,
            PageId = pageId,
            IsRequired = isRequired,
            IsReadOnly = isReadOnly,
            MaxLength = maxLength,
            Lower = lower,
            Upper = upper,
            Pattern = pattern,
            List = list,
            Sort = sort,
            Column = sqlType is null ? null : Column(name, sqlType),
        };

    /// <summary>
    /// The three system rows every view holds, with a null <c>PageId</c> and <c>TabIndex</c>. With
    /// <paramref name="withBaseTableColumns"/> they carry the base-table columns of
    /// <c>testdata/sql/05_data_tables.sql</c>.
    /// </summary>
    private static FieldMetadata[] SystemRows(int firstFieldId, bool withBaseTableColumns) =>
    [
        Field(firstFieldId, "UniqueKey", "UniqueKey", pageId: null, sqlType: withBaseTableColumns ? "int" : null),
        Field(
            firstFieldId + 1,
            "RecStatus",
            "RecStatus",
            pageId: null,
            sqlType: withBaseTableColumns ? "smallint" : null),
        Field(
            firstFieldId + 2,
            "GlobalRecordId",
            "GlobalRecordId",
            pageId: null,
            sqlType: withBaseTableColumns ? "nvarchar(255)" : null),
    ];

    /// <summary>
    /// Describes a column from its forms.json <c>sqlType</c> with the sizes <c>sys.columns.max_length</c> reports:
    /// 2·n bytes for <c>nvarchar(n)</c>, 16 for <c>ntext</c>, and the fixed size of each other type.
    /// </summary>
    private static ColumnMetadata Column(string name, string sqlType)
    {
        const string Nvarchar = "nvarchar(";
        if (sqlType.StartsWith(Nvarchar, StringComparison.Ordinal) && sqlType.EndsWith(')'))
        {
            string length = sqlType[Nvarchar.Length..^1];
            int maxLengthBytes = length == "max" ? -1 : 2 * int.Parse(length, CultureInfo.InvariantCulture);
            return FieldTypeMapper.DescribeColumn(name, "nvarchar", maxLengthBytes);
        }

        int size = sqlType switch
        {
            "ntext" => 16,
            "float" or "datetime" => 8,
            "bit" or "tinyint" => 1,
            "smallint" => 2,
            "int" => 4,
            _ => throw new ArgumentException($"No catalogue size is known for SQL type '{sqlType}'.", nameof(sqlType)),
        };
        return FieldTypeMapper.DescribeColumn(name, sqlType, size);
    }

    private static FieldMetadata TextRow(int fieldId, string name, decimal tabIndex) => new()
    {
        FieldId = fieldId,
        Name = name,
        FieldTypeId = 1,
        FieldTypeName = "Text",
        Prompt = name,
        TabIndex = tabIndex,
        PageId = PageId,
        Column = Column(name, "nvarchar(255)"),
    };

    private static FieldDefinition Definition(FieldMetadata row) => new(
        row.Name,
        FieldType.Text,
        row.Prompt,
        row.TabIndex ?? 0,
        row.PageId ?? PageId,
        Unsupported: false,
        UnsupportedType: null,
        FieldValidation.None,
        Options: null,
        Members: null);

    private static FormMetadata SeededForm(string formName) => BuildSeededForm(SeededFormJson(formName));

    private static FormDefinition SeededDefinition(string formName) =>
        Create(SeededForm(formName), SeededLegalValueOptions(formName));

    private static IReadOnlyDictionary<string, IReadOnlyList<FieldOption>> SeededLegalValueOptions(string formName) =>
        SeededLegalValueOptions(FormsJson.Value, SeededFormJson(formName));

    /// <summary>The page-table columns of a seeded form, in forms.json order, with the page that holds each.</summary>
    private static (string Name, int PageId)[] SeededColumns(string formName) =>
        SeededFormJson(formName)
            .GetProperty("pages")
            .EnumerateArray()
            .SelectMany(page => page
                .GetProperty("fields")
                .EnumerateArray()
                .Select(field => (RequiredText(field, "name"), page.GetProperty("pageId").GetInt32())))
            .ToArray();

    private static JsonElement SeededFormJson(string formName) =>
        Assert.Single(
            FormsJson.Value.GetProperty("forms").EnumerateArray(),
            form => RequiredText(form, "name") == formName);

    /// <summary>
    /// Rebuilds a seeded form's metadata: per page, every forms.json field with its list position as
    /// <c>TabIndex</c>, then a synthetic label (<c>TabIndex</c> 1000) and a synthetic group (1001) whose list is
    /// the page's first two fields; then the three system rows.
    /// </summary>
    private static FormMetadata BuildSeededForm(JsonElement form)
    {
        string formName = RequiredText(form, "name");
        List<PageMetadata> pages = [];
        List<FieldMetadata> fields = [];
        int fieldId = 0;

        foreach (JsonElement page in form.GetProperty("pages").EnumerateArray())
        {
            int pageId = page.GetProperty("pageId").GetInt32();
            pages.Add(new PageMetadata(pageId, RequiredText(page, "name"), pages.Count, RequiredText(page, "table")));

            JsonElement[] pageFields = page.GetProperty("fields").EnumerateArray().ToArray();
            for (int index = 0; index < pageFields.Length; index++)
            {
                JsonElement json = pageFields[index];
                string name = RequiredText(json, "name");
                int typeId = json.GetProperty("fieldTypeId").GetInt32();
                fields.Add(new FieldMetadata
                {
                    FieldId = ++fieldId,
                    Name = name,
                    FieldTypeId = typeId,
                    FieldTypeName = SeededTypeNames[typeId],
                    Prompt = OptionalText(json, "prompt"),
                    TabIndex = index + 1,
                    PageId = pageId,
                    IsRequired = json.GetProperty("required").GetBoolean(),
                    IsReadOnly = SeededReadOnlyFields.Contains((formName, name)),
                    MaxLength = SeededMaxLengths.TryGetValue((formName, name), out int maxLength) ? maxLength : null,
                    Lower = OptionalText(json, "lower"),
                    Upper = OptionalText(json, "upper"),
                    Pattern = OptionalText(json, "pattern"),
                    SourceTableName = OptionalText(json, "codeTable"),
                    CodeColumnName = OptionalText(json, "codeColumn"),
                    Column = Column(name, RequiredText(json, "sqlType")),
                });
            }

            fields.Add(Field(++fieldId, $"SynLabel{pageId}", "Label/Title", pageId: pageId, tabIndex: 1000));
            fields.Add(Field(
                ++fieldId,
                $"SynGroup{pageId}",
                "Group",
                pageId: pageId,
                tabIndex: 1001,
                list: string.Join(',', pageFields.Take(2).Select(json => RequiredText(json, "name")))));
        }

        fields.AddRange(SystemRows(++fieldId, withBaseTableColumns: false));
        return new FormMetadata(form.GetProperty("viewId").GetInt32(), formName, formName, pages, fields);
    }

    /// <summary>
    /// The code-table rows of each Legal Values field of a seeded form, keyed by field name in forms.json row
    /// order, each row text being both the value and the label.
    /// </summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<FieldOption>> SeededLegalValueOptions(
        JsonElement root,
        JsonElement form)
    {
        JsonElement codeTables = root.GetProperty("codeTables");
        Dictionary<string, IReadOnlyList<FieldOption>> options = new(StringComparer.Ordinal);
        foreach (JsonElement field in form.GetProperty("pages").EnumerateArray()
            .SelectMany(page => page.GetProperty("fields").EnumerateArray())
            .Where(field => field.GetProperty("fieldTypeId").GetInt32() == SeededTypeIds["Legal Values"]))
        {
            options.Add(
                RequiredText(field, "name"),
                codeTables
                    .GetProperty(RequiredText(field, "codeTable"))
                    .EnumerateArray()
                    .Select(row => TextOption(row.GetString() ?? throw MissingValue("codeTables row")))
                    .ToArray());
        }

        return options;
    }

    private static JsonElement LoadFormsJson()
    {
        string path = Path.Combine(FindRepositoryRoot(), "testdata", "forms.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
            directory is not null;
            directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EpiInfo.Web.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            $"No directory at or above '{AppContext.BaseDirectory}' holds EpiInfo.Web.sln.");
    }

    private static string RequiredText(JsonElement element, string property) =>
        element.GetProperty(property).GetString() ?? throw MissingValue(property);

    private static string? OptionalText(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static InvalidDataException MissingValue(string what) =>
        new($"testdata/forms.json holds no text for {what}.");
}
