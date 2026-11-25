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
/// A second fixture transcribes every page-bound <c>metaFields</c> row of that SQL file with its stored
/// <c>TabIndex</c>, so the 61, 55 and 33 rows of the three seeded pages are checked as the database holds them.
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
    /// The seeded <c>metaViews</c> rows by <c>ViewId</c> and <c>Name</c>, with the <c>FieldId</c> of the first of
    /// each view's three system rows, as <c>testdata/sql/03_forms.sql</c> inserts them.
    /// </summary>
    private static readonly (int ViewId, string Name, int FirstSystemFieldId)[] SeededMetaViews =
    [
        (1, "CaseReport", 1),
        (2, "FoodHistory", 1001),
    ];

    /// <summary>
    /// The <c>metaPages</c> rows of <c>testdata/sql/03_forms.sql</c>, each with its <c>ViewId</c>. Each page table
    /// is named from the view's base table and the <c>PageId</c>.
    /// </summary>
    private static readonly (int ViewId, PageMetadata Page)[] SeededMetaPages =
    [
        (1, new PageMetadata(1, "Page 1", 0, "CaseReport1")),
        (2, new PageMetadata(2, "Page 1", 0, "FoodHistory2")),
        (2, new PageMetadata(3, "Page 2", 1, "FoodHistory3")),
    ];

    /// <summary>
    /// Every page-bound row of the <c>metaFields</c> INSERT of <c>testdata/sql/03_forms.sql</c>, transcribed in the
    /// file's <c>FieldId</c> order, which is not <c>TabIndex</c> order on any page: <c>FieldId</c>, <c>Name</c>,
    /// <c>FieldTypeId</c>, <c>TabIndex</c>, <c>PageId</c> and the group <c>List</c>.
    /// </summary>
    /// <remarks>
    /// The seeded views hold 64 and 91 <c>metaFields</c> rows. Three of each are the system rows (<c>FieldId</c> 1-3
    /// and 1001-1003), whose <c>PageId</c> is null, so 61 rows sit on <c>CaseReport</c> page 1 and 88 on
    /// <c>FoodHistory</c>: 55 on page 2 and 33 on page 3. Of those, 43 and 74 own a page-table column, the fields
    /// <c>testdata/forms.json</c> lists; the rest are labels, groups and a command button. The 43 and 74 columns give
    /// 43 and 67 data fields, because the seven <c>Yes/No</c> columns of <c>FoodHistory</c> are unsupported.
    /// </remarks>
    private static readonly (int FieldId, string Name, int FieldTypeId, decimal TabIndex, int PageId, string? List)[]
        SeededMetaFieldsRows =
        [
            (4, "DottedLine", 2, 6m, 1, null),
            (5, "Detach", 2, 7m, 1, null),
            (6, "LastName", 1, 2m, 1, null),
            (7, "FirstName", 1, 3m, 1, null),
            (8, "PhoneNumber", 6, 1m, 1, null),
            (9, "Address", 1, 4m, 1, null),
            (10, "City", 1, 5m, 1, null),
            (11, "LymeDiseaseCaseReport", 2, 8m, 1, null),
            (12, "Underline", 2, 9m, 1, null),
            (13, "State", 19, 12m, 1, null),
            (14, "County", 1, 13m, 1, null),
            (15, "Zip", 1, 14m, 1, null),
            (16, "Age", 5, 15m, 1, null),
            (17, "Sex", 19, 16m, 1, null),
            (18, "Ethnicity", 19, 17m, 1, null),
            (19, "Race", 19, 18m, 1, null),
            (20, "DemographicInformation", 21, 11m, 1, "Race,Ethnicity,Sex,Age,Zip,County,State"),
            (21, "Symptoms", 21, 19m, 1,
                "DiagnosisDate,ReportDate,OnsetDate,OtherClinical,AtrloventricularBlock,LabelCardiologic,Antibody,"
                + "CSF,Encephalitis,LymphocyticMeningitis,Radiculoneuropathy,BellsPalsy,LabelNeurologic,Arthritis,"
                + "LabelRheumatologic,ErythemaMigrans,LabelDermatologic"),
            (22, "LabelDermatologic", 2, 20m, 1, null),
            (23, "LabelRheumatologic", 2, 22m, 1, null),
            (24, "LabelNeurologic", 2, 24m, 1, null),
            (25, "LabelCardiologic", 2, 31m, 1, null),
            (26, "ErythemaMigrans", 19, 21m, 1, null),
            (27, "Arthritis", 19, 23m, 1, null),
            (28, "BellsPalsy", 19, 25m, 1, null),
            (29, "Radiculoneuropathy", 19, 26m, 1, null),
            (30, "LymphocyticMeningitis", 19, 27m, 1, null),
            (31, "Encephalitis", 19, 28m, 1, null),
            (32, "CSF", 19, 29m, 1, null),
            (33, "Antibody", 19, 30m, 1, null),
            (34, "AtrloventricularBlock", 19, 32m, 1, null),
            (35, "OtherClinical", 1, 33m, 1, null),
            (36, "OnsetDate", 7, 34m, 1, null),
            (37, "DiagnosisDate", 7, 36m, 1, null),
            (38, "ReportDate", 7, 35m, 1, null),
            (40, "Hospitalized", 19, 38m, 1, null),
            (41, "Antibiotics", 1, 39m, 1, null),
            (42, "AntibioticUseDays", 5, 40m, 1, null),
            (43, "Pregnant", 19, 41m, 1, null),
            (44, "Wherewasthepatientmostlikelyexposed", 2, 42m, 1, null),
            (45, "ExposedCounty", 1, 43m, 1, null),
            (46, "ExposedState", 19, 44m, 1, null),
            (47, "OtherHistory", 21, 37m, 1,
                "ExposedState,ExposedCounty,Wherewasthepatientmostlikelyexposed,Pregnant,AntibioticUseDays,"
                + "Antibiotics,Hospitalized"),
            (48, "SerologicResults", 19, 46m, 1, null),
            (49, "CultureResults", 19, 47m, 1, null),
            (50, "OtherLabResult", 1, 48m, 1, null),
            (51, "PhysicianName", 1, 49m, 1, null),
            (52, "PhysicianPhone", 6, 50m, 1, null),
            (53, "PhysicianAddress", 1, 51m, 1, null),
            (54, "PersonCompletingForm", 1, 52m, 1, null),
            (55, "PersonCompletingFormPhone", 6, 53m, 1, null),
            (56, "PersonCompletingFormAddress", 1, 54m, 1, null),
            (57, "LaboratoryResults", 21, 45m, 1,
                "PersonCompletingFormAddress,PersonCompletingFormPhone,PersonCompletingForm,PhysicianAddress,"
                + "PhysicianPhone,PhysicianName,OtherLabResult,CultureResults,SerologicResults"),
            (58, "StateIDNumber", 1, 55m, 1, null),
            (59, "CDCIDNumber", 1, 56m, 1, null),
            (60, "ReportedToCDCDate", 7, 57m, 1, null),
            (61, "FooterLabel", 2, 58m, 1, null),
            (62, "FormBasedonCDC5260Rev052004", 2, 59m, 1, null),
            (63, "CenterFooter", 2, 60m, 1, null),
            (64, "Page1", 2, 61m, 1, null),
            (65, "Alldatacontainedinthisformisfictional", 2, 10m, 1, null),
            (1004, "CaseID", 5, 6m, 2, null),
            (1005, "DateofInterview", 7, 7m, 2, null),
            (1006, "DemographicInformation", 21, 9m, 2,
                "HomePhone,EmailAddress,Occupation,Longitude,Latitude,State,GetCoordinates,PatientAddress,"
                + "Multiracial,Asian,AmericanIndianAlaskanNative,Black,UnknownOther,"
                + "NativeHawaiianOtherPacificIslander,White,Race,EthnicityGroup,Age,DOB,Sex,LastName,FirstName"),
            (1007, "FirstName", 1, 10m, 2, null),
            (1008, "LastName", 1, 11m, 2, null),
            (1009, "Sex", 17, 12m, 2, null),
            (1010, "DOB", 7, 13m, 2, null),
            (1011, "Age", 5, 14m, 2, null),
            (1012, "EthnicityGroup", 17, 15m, 2, null),
            (1013, "Race", 2, 16m, 2, null),
            (1014, "White", 10, 17m, 2, null),
            (1015, "NativeHawaiianOtherPacificIslander", 10, 18m, 2, null),
            (1016, "UnknownOther", 10, 19m, 2, null),
            (1017, "Black", 10, 20m, 2, null),
            (1018, "AmericanIndianAlaskanNative", 10, 21m, 2, null),
            (1019, "Asian", 10, 22m, 2, null),
            (1020, "Multiracial", 10, 23m, 2, null),
            (1021, "PatientAddress", 4, 24m, 2, null),
            (1022, "GetCoordinates", 13, 25m, 2, null),
            (1023, "State", 1, 26m, 2, null),
            (1024, "Latitude", 5, 27m, 2, null),
            (1025, "Longitude", 5, 28m, 2, null),
            (1026, "Occupation", 1, 29m, 2, null),
            (1027, "EmailAddress", 1, 30m, 2, null),
            (1028, "HomePhone", 6, 31m, 2, null),
            (1036, "OnsetDate", 7, 34m, 2, null),
            (1038, "Symptoms", 21, 32m, 2,
                "DateOfDeath,Died,Antibiotics,HospitalAdmissionDate,Hospitalized,DoctorVisitDate,DoctorVisit,"
                + "LooseStoolsPerDay,FeverTemp,Abdominalcramps,Nausea,Irritable,Chills,PoorFeeding,NonBloodyDiarrhea,"
                + "BloodyDiarrhea,Vomiting,Fever,Headache,SymptomDuration,OnsetWeek,OnsetDate,ILL"),
            (1039, "Headache", 10, 37m, 2, null),
            (1040, "Fever", 10, 38m, 2, null),
            (1041, "Vomiting", 10, 39m, 2, null),
            (1042, "BloodyDiarrhea", 10, 40m, 2, null),
            (1043, "NonBloodyDiarrhea", 10, 41m, 2, null),
            (1062, "FoodHistory", 2, 2m, 2, null),
            (1063, "EColi", 2, 1m, 2, null),
            (1064, "ILL", 11, 33m, 2, null),
            (1065, "SymptomDuration", 1, 36m, 2, null),
            (1066, "PoorFeeding", 10, 42m, 2, null),
            (1067, "Chills", 10, 43m, 2, null),
            (1068, "Irritable", 10, 44m, 2, null),
            (1069, "Nausea", 10, 45m, 2, null),
            (1070, "Abdominalcramps", 10, 46m, 2, null),
            (1071, "LooseStoolsPerDay", 17, 48m, 2, null),
            (1073, "FoodsEaten", 21, 3m, 3,
                "Cookedbacon,Butter,Beansprouts,Rawcarrots,Wholemilk,Turkey,Icecream,N2milk,Viennasausages,Sourcream,"
                + "Beefjerkey,Freshtomatoes,Applejuice,Orangejuice,Strawberries,Americancheese,Peaches,Cheddarcheese,"
                + "Skimmilk,Breastmilk,Blueberries,Grapes,Freshcelery"),
            (1108, "DoctorVisit", 11, 49m, 2, null),
            (1109, "Hospitalized", 11, 51m, 2, null),
            (1110, "Antibiotics", 11, 53m, 2, null),
            (1111, "DoctorVisitDate", 7, 50m, 2, null),
            (1112, "HospitalAdmissionDate", 7, 52m, 2, null),
            (1113, "EColi1", 2, 1m, 3, null),
            (1114, "FoodHistory1", 2, 2m, 3, null),
            (1115, "BuyLastTenDays", 4, 27m, 3, null),
            (1116, "Breastmilk", 10, 7m, 3, null),
            (1120, "Beefjerkey", 10, 16m, 3, null),
            (1121, "Viennasausages", 10, 18m, 3, null),
            (1122, "Americancheese", 10, 11m, 3, null),
            (1123, "Cheddarcheese", 10, 9m, 3, null),
            (1124, "Sourcream", 10, 17m, 3, null),
            (1125, "Butter", 10, 25m, 3, null),
            (1126, "Icecream", 10, 20m, 3, null),
            (1127, "Cookedbacon", 10, 26m, 3, null),
            (1128, "Turkey", 10, 21m, 3, null),
            (1129, "Strawberries", 10, 12m, 3, null),
            (1130, "Grapes", 10, 5m, 3, null),
            (1131, "Blueberries", 10, 6m, 3, null),
            (1132, "Peaches", 10, 10m, 3, null),
            (1133, "Beansprouts", 10, 24m, 3, null),
            (1134, "Applejuice", 10, 14m, 3, null),
            (1135, "N2milk", 10, 19m, 3, null),
            (1136, "Skimmilk", 10, 8m, 3, null),
            (1137, "Wholemilk", 10, 22m, 3, null),
            (1138, "Orangejuice", 10, 13m, 3, null),
            (1139, "Freshtomatoes", 10, 15m, 3, null),
            (1140, "Freshcelery", 10, 4m, 3, null),
            (1141, "Rawcarrots", 10, 23m, 3, null),
            (1142, "Alldatacontainedhereinisfictional", 2, 8m, 2, null),
            (1143, "Died", 11, 54m, 2, null),
            (1144, "DateOfDeath", 7, 55m, 2, null),
            (1145, "FeverTemp", 5, 47m, 2, null),
            (1146, "StoolSubmitted", 11, 29m, 3, null),
            (1147, "O157H7isolated", 11, 31m, 3, null),
            (1148, "StoolCollectionDate", 7, 30m, 3, null),
            (1149, "Lab", 1, 32m, 3, null),
            (1150, "LabResults", 1, 33m, 3, null),
            (1151, "LabTestingandResults", 21, 28m, 3,
                "LabResults,Lab,O157H7isolated,StoolCollectionDate,StoolSubmitted"),
            (1152, "N_____", 2, 5m, 2, null),
            (1153, "USDEPARTMENTOFHEALTHHUMANSERVICES", 2, 3m, 2, null),
            (1154, "CentersforDiseaseControlandPrevention", 2, 4m, 2, null),
            (1157, "OnsetWeek", 5, 35m, 2, null),
        ];

    /// <summary>
    /// The names of each seeded page's <c>metaFields</c> rows by <c>PageId</c>, in <c>TabIndex</c> order, as
    /// <c>testdata/sql/03_forms.sql</c> holds them: 61 on page 1, 55 on page 2 and 33 on page 3.
    /// </summary>
    private static readonly IReadOnlyDictionary<int, string[]> SeededPageFieldNames = new Dictionary<int, string[]>
    {
        [1] =
        [
            "PhoneNumber", "LastName", "FirstName", "Address", "City", "DottedLine", "Detach", "LymeDiseaseCaseReport",
            "Underline", "Alldatacontainedinthisformisfictional", "DemographicInformation", "State", "County", "Zip",
            "Age", "Sex", "Ethnicity", "Race", "Symptoms", "LabelDermatologic", "ErythemaMigrans", "LabelRheumatologic",
            "Arthritis", "LabelNeurologic", "BellsPalsy", "Radiculoneuropathy", "LymphocyticMeningitis", "Encephalitis",
            "CSF", "Antibody", "LabelCardiologic", "AtrloventricularBlock", "OtherClinical", "OnsetDate", "ReportDate",
            "DiagnosisDate", "OtherHistory", "Hospitalized", "Antibiotics", "AntibioticUseDays", "Pregnant",
            "Wherewasthepatientmostlikelyexposed", "ExposedCounty", "ExposedState", "LaboratoryResults",
            "SerologicResults", "CultureResults", "OtherLabResult", "PhysicianName", "PhysicianPhone",
            "PhysicianAddress", "PersonCompletingForm", "PersonCompletingFormPhone", "PersonCompletingFormAddress",
            "StateIDNumber", "CDCIDNumber", "ReportedToCDCDate", "FooterLabel", "FormBasedonCDC5260Rev052004",
            "CenterFooter", "Page1",
        ],
        [2] =
        [
            "EColi", "FoodHistory", "USDEPARTMENTOFHEALTHHUMANSERVICES", "CentersforDiseaseControlandPrevention",
            "N_____", "CaseID", "DateofInterview", "Alldatacontainedhereinisfictional", "DemographicInformation",
            "FirstName", "LastName", "Sex", "DOB", "Age", "EthnicityGroup", "Race", "White",
            "NativeHawaiianOtherPacificIslander", "UnknownOther", "Black", "AmericanIndianAlaskanNative", "Asian",
            "Multiracial", "PatientAddress", "GetCoordinates", "State", "Latitude", "Longitude", "Occupation",
            "EmailAddress", "HomePhone", "Symptoms", "ILL", "OnsetDate", "OnsetWeek", "SymptomDuration", "Headache",
            "Fever", "Vomiting", "BloodyDiarrhea", "NonBloodyDiarrhea", "PoorFeeding", "Chills", "Irritable", "Nausea",
            "Abdominalcramps", "FeverTemp", "LooseStoolsPerDay", "DoctorVisit", "DoctorVisitDate", "Hospitalized",
            "HospitalAdmissionDate", "Antibiotics", "Died", "DateOfDeath",
        ],
        [3] =
        [
            "EColi1", "FoodHistory1", "FoodsEaten", "Freshcelery", "Grapes", "Blueberries", "Breastmilk", "Skimmilk",
            "Cheddarcheese", "Peaches", "Americancheese", "Strawberries", "Orangejuice", "Applejuice", "Freshtomatoes",
            "Beefjerkey", "Sourcream", "Viennasausages", "N2milk", "Icecream", "Turkey", "Wholemilk", "Rawcarrots",
            "Beansprouts", "Butter", "Cookedbacon", "BuyLastTenDays", "LabTestingandResults", "StoolSubmitted",
            "StoolCollectionDate", "O157H7isolated", "Lab", "LabResults",
        ],
    };

    /// <summary>
    /// Adjacent schema-valid tab indexes, lower first, as invariant text because attributes cannot hold decimal
    /// constants: 2^53 and 2^53 + 1, and the largest and the smallest adjacent pair a <c>decimal(18,0)</c> holds.
    /// One IEEE double holds both values of each pair. The rows reach <see cref="FormDefinitionFactory"/> in
    /// <c>FieldId</c> order with the lower tab index on the higher <c>FieldId</c>; the page it builds must list them
    /// by exact tab index, and each field it produces must carry its tab index exactly, in the DTO and on the wire.
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

        FormMetadata form = SinglePageForm(later, earlier);
        PageDefinition page = Assert.Single(Create(form).Pages);

        // Ordering by FieldId, or by the tab indexes as doubles, would list Later first.
        Assert.Equal(["Earlier", "Later"], page.Fields.Select(field => field.Name));
        Assert.Equal([lower, higher], page.Fields.Select(field => field.TabIndex));

        // The factory's own fields, serialized with the API's number settings, keep every digit of each tab index.
        Assert.Contains(
            $"\"tabIndex\":{lowerText},",
            JsonSerializer.Serialize(page.Fields[0], WireOptions),
            StringComparison.Ordinal);
        Assert.Contains(
            $"\"tabIndex\":{higherText},",
            JsonSerializer.Serialize(page.Fields[1], WireOptions),
            StringComparison.Ordinal);

        Assert.Equal(["Earlier", "Later"], FormDefinitionFactory.GetDataFields(form).Select(field => field.Name));
    }

    [Fact]
    public void Create_OrdersPagesByPositionAndFieldsByTabIndexThenFieldId()
    {
        // PageId runs against Position: the Position 0 page has the higher PageId and is listed second, so neither
        // the input order nor a PageId sort yields Position order. The rows interleave the two pages, so membership
        // rests on the PageId filter alone, and the Position 1 page's tab indexes sit below every Position 0 tab
        // index, so a field order that ignores the page's Position would list D and E first.
        FormMetadata form = new(
            7,
            "Synthetic",
            "Syn",
            [new PageMetadata(10, "Second", 1, "Syn10"), new PageMetadata(20, "First", 0, "Syn20")],
            [
                Field(9, "D", "Text", pageId: 10, tabIndex: 0, sqlType: "nvarchar(255)"),
                Field(1, "A", "Text", pageId: 20, tabIndex: 3, sqlType: "nvarchar(255)"),
                Field(8, "E", "Text", pageId: 10, tabIndex: 0, sqlType: "nvarchar(255)"),
                Field(2, "B", "Text", pageId: 20, tabIndex: 1, sqlType: "nvarchar(255)"),
                Field(3, "C", "Text", pageId: 20, tabIndex: 2, sqlType: "nvarchar(255)"),
            ]);

        FormDefinition definition = Create(form);

        Assert.Equal(
            [(20, "First", 0), (10, "Second", 1)],
            definition.Pages.Select(page => (page.PageId, page.Name, page.Position)));

        PageDefinition first = definition.Pages[0];
        Assert.Equal(["B", "C", "A"], first.Fields.Select(field => field.Name));
        Assert.Equal([1m, 2m, 3m], first.Fields.Select(field => field.TabIndex));
        Assert.Equal([20, 20, 20], first.Fields.Select(field => field.PageId));

        // Equal tab indexes fall back to FieldId: D is listed before E, and E's lower FieldId still puts it first.
        PageDefinition second = definition.Pages[1];
        Assert.Equal(["E", "D"], second.Fields.Select(field => field.Name));
        Assert.Equal([0m, 0m], second.Fields.Select(field => field.TabIndex));
        Assert.Equal([10, 10], second.Fields.Select(field => field.PageId));

        // The data fields keep the same storage order, page Position first. The grid columns re-sort them by page
        // Position, then TabIndex.
        DataFieldSet dataFields = FormDefinitionFactory.GetDataFields(form);
        Assert.Equal(["B", "C", "A", "E", "D"], dataFields.Select(field => field.Name));
        Assert.Equal([20, 20, 20, 10, 10], dataFields.Select(field => field.Page.PageId));
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
    /// Each seeded page lists exactly its <c>metaFields</c> rows of <c>testdata/sql/03_forms.sql</c>, in
    /// <c>TabIndex</c> order: 61 on <c>CaseReport</c> page 1, and 55 and 33 on <c>FoodHistory</c> pages 2 and 3. The
    /// rows arrive in <c>FieldId</c> order, which differs from <c>TabIndex</c> order on every page, so the order
    /// listed is the factory's own.
    /// </summary>
    [Theory]
    [InlineData("CaseReport", 1, "Page 1", 0, 61)]
    [InlineData("FoodHistory", 2, "Page 1", 0, 55)]
    [InlineData("FoodHistory", 3, "Page 2", 1, 33)]
    public void Create_SeededMetaFieldsRows_ListEachPageInTabIndexOrder(
        string formName,
        int pageId,
        string pageName,
        int position,
        int expectedFieldCount)
    {
        string[] expectedNames = SeededPageFieldNames[pageId];
        Assert.Equal(expectedFieldCount, expectedNames.Length);

        FormMetadata form = MetaFieldsForm(formName);
        string[] inputNames = form.Fields.Where(field => field.PageId == pageId).Select(field => field.Name).ToArray();
        Assert.Equal(expectedNames.Order(StringComparer.Ordinal), inputNames.Order(StringComparer.Ordinal));
        Assert.NotEqual(expectedNames, inputNames);

        PageDefinition page = Assert.Single(Create(form).Pages, listed => listed.PageId == pageId);

        Assert.Equal(pageName, page.Name);
        Assert.Equal(position, page.Position);
        Assert.Equal(expectedFieldCount, page.Fields.Count);
        Assert.Equal(expectedNames, page.Fields.Select(field => field.Name));
        Assert.Equal(
            Enumerable.Range(1, expectedFieldCount).Select(tabIndex => (decimal)tabIndex),
            page.Fields.Select(field => field.TabIndex));
        Assert.All(page.Fields, field => Assert.Equal(pageId, field.PageId));
    }

    /// <summary>
    /// The C-7 reconciliation on the transcribed rows: the views hold 64 and 91 <c>metaFields</c> rows; without the
    /// three system rows of each, 61 and 88 sit on pages and are all listed; 43 and 74 own a page-table column; and 43
    /// and 67 are data fields, the seven <c>Yes/No</c> columns of <c>FoodHistory</c> being unsupported.
    /// </summary>
    [Theory]
    [InlineData("CaseReport", 64, 61, new[] { 1 }, new[] { 61 }, 43, 43)]
    [InlineData("FoodHistory", 91, 88, new[] { 2, 3 }, new[] { 55, 33 }, 74, 67)]
    public void Create_SeededMetaFieldsRows_ReconcileRowPageColumnAndDataFieldTotals(
        string formName,
        int expectedRows,
        int expectedPageBoundRows,
        int[] expectedPageIds,
        int[] expectedPageSizes,
        int expectedColumnRows,
        int expectedDataFields)
    {
        FormMetadata form = MetaFieldsForm(formName);

        Assert.Equal(expectedRows, form.Fields.Count);
        Assert.Equal(SystemFieldNames, form.Fields.Where(field => field.PageId is null).Select(field => field.Name));
        Assert.Equal(expectedPageBoundRows, form.Fields.Count(field => field.PageId is not null));
        Assert.Equal(expectedColumnRows, form.Fields.Count(field => field.Column is not null));

        FormDefinition definition = Create(form);
        FieldDefinition[] listed = definition.Pages.SelectMany(page => page.Fields).ToArray();

        Assert.Equal(expectedPageIds, definition.Pages.Select(page => page.PageId));
        Assert.Equal(expectedPageSizes, definition.Pages.Select(page => page.Fields.Count));
        Assert.Equal(expectedPageBoundRows, listed.Length);
        Assert.DoesNotContain(listed, field => SystemFieldNames.Contains(field.Name, StringComparer.OrdinalIgnoreCase));

        DataFieldSet dataFields = FormDefinitionFactory.GetDataFields(form);
        string[] expectedDataFieldNames = form.Fields
            .Where(field => field.Column is not null)
            .Select(field => field.Name)
            .Except(SeededYesNoFields[formName], StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expectedDataFields, dataFields.Count);
        Assert.Equal(expectedDataFieldNames, dataFields.Select(field => field.Name).Order(StringComparer.Ordinal));
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

    /// <summary>
    /// A <c>bigint</c> bound beyond 2^53 fits <c>metaFields.Lower</c>/<c>Upper</c> (<c>nvarchar(20)</c>) and keeps its
    /// exact integer: a double would round <c>9007199254740993</c> to <c>9007199254740992</c>, so an upper bound would
    /// reject its own endpoint and a lower bound would admit the integer below it.
    /// </summary>
    [Theory]
    [InlineData("9007199254740993", 9007199254740993L)]
    [InlineData("-9007199254740993", -9007199254740993L)]
    [InlineData("9223372036854775807", long.MaxValue)]
    [InlineData("-9223372036854775808", long.MinValue)]
    public void Create_BigintNumberBound_KeepsItsExactInteger(string stored, long expected)
    {
        FieldDefinition field = SingleDefinition(
            Field(1, "Big", "Number", tabIndex: 1, sqlType: "bigint", lower: stored, upper: stored));

        Assert.Equal(FieldType.Number, field.FieldType);
        foreach (FieldValue? bound in new[] { field.Validation.Lower, field.Validation.Upper })
        {
            Assert.Equal(FieldValue.FromInteger(expected), bound);
            Assert.True(Assert.NotNull(bound).TryGetInteger(out long integer));
            Assert.Equal(expected, integer);
        }
    }

    /// <summary>
    /// Number bounds keep the invariant lexical rules (surrounding whitespace, a sign, a point, an exponent): a whole
    /// number is its exact integer, and any other number its nearest double, never rounded into a whole number.
    /// </summary>
    [Theory]
    [InlineData(" 2e3 ", true, 2000L, 2000d)]
    [InlineData("+120", true, 120L, 120d)]
    [InlineData("2.0", true, 2L, 2d)]
    [InlineData("-0", true, 0L, 0d)]
    [InlineData("37.5", false, 0L, 37.5d)]
    [InlineData("-1.5E1", true, -15L, -15d)]
    [InlineData("2.0000000000000001", false, 0L, 2d)]
    [InlineData("9223372036854775808", false, 0L, 9223372036854775808d)]
    public void Create_NumberBoundText_KeepsAnExactIntegerOrItsNearestDouble(
        string stored,
        bool isInteger,
        long integer,
        double nearest)
    {
        FieldDefinition field = SingleDefinition(
            Field(1, "Bounded", "Number", tabIndex: 1, sqlType: "float", lower: stored, upper: stored));

        foreach (FieldValue? bound in new[] { field.Validation.Lower, field.Validation.Upper })
        {
            FieldValue value = Assert.NotNull(bound);
            Assert.Equal(FieldValueKind.Number, value.Kind);
            Assert.Equal(isInteger, value.TryGetInteger(out long actualInteger));
            Assert.Equal(integer, actualInteger);
            Assert.True(value.TryGetNumber(out double actualNearest));
            Assert.Equal(nearest, actualNearest);
            if (isInteger)
            {
                Assert.Equal(FieldValue.FromInteger(integer), value);
            }
            else if (Math.Truncate(nearest) != nearest)
            {
                Assert.Equal(FieldValue.FromNumber(nearest), value);
            }
        }
    }

    /// <summary>Text that is no finite invariant number is no bound, as before.</summary>
    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1e400")]
    [InlineData("1,000")]
    [InlineData("37,5")]
    [InlineData("abc")]
    public void Create_UnparseableNumberBound_IsNoBound(string stored)
    {
        FieldDefinition field = SingleDefinition(
            Field(1, "Bounded", "Number", tabIndex: 1, sqlType: "bigint", lower: stored, upper: stored));

        Assert.Null(field.Validation.Lower);
        Assert.Null(field.Validation.Upper);
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
    /// An option is stored as its 0-based index. 256 items on <c>tinyint</c> are offered with indexes 0 to 255, its
    /// largest value; 257 would offer index 256, which no write can store, so that field is unsupported, carries no
    /// options and is not a data field.
    /// </summary>
    [Theory]
    [InlineData(256, FieldType.Option, null, true)]
    [InlineData(257, FieldType.Unsupported, "Option", false)]
    public void Create_OptionOnTinyint_OffersEveryItemOnlyWhileTheLastIndexFits(
        int itemCount,
        FieldType expectedType,
        string? expectedUnsupportedType,
        bool expectedDataField)
    {
        string[] labels = Enumerable.Range(0, itemCount)
            .Select(index => "Item" + index.ToString(CultureInfo.InvariantCulture))
            .ToArray();
        FormMetadata form = SinglePageForm(
            Field(1, "Colour", "Option", tabIndex: 1, sqlType: "tinyint", list: string.Join(',', labels)));
        FieldOption[]? expectedOptions = expectedDataField
            ? labels.Select((label, index) => new FieldOption(FieldValue.FromIndex(index), label)).ToArray()
            : null;

        FieldDefinition field = Assert.Single(Assert.Single(Create(form).Pages).Fields);

        Assert.Equal(expectedType, field.FieldType);
        Assert.Equal(expectedUnsupportedType is not null, field.Unsupported);
        Assert.Equal(expectedUnsupportedType, field.UnsupportedType);
        Assert.Equal(expectedOptions, field.Options);
        Assert.Equal(
            expectedDataField,
            FormDefinitionFactory.GetDataFields(form).Any(dataField => dataField.Name == "Colour"));
    }

    /// <summary>
    /// Each seeded Legal Values field gets its own code-table rows, in the order given.
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

    /// <summary>
    /// The store owns the one sort of a <c>Sort</c> = 1 list, so the factory keeps the store's order exactly, even
    /// when that order is not by text.
    /// </summary>
    [Fact]
    public void Create_SortedLegalValues_KeepTheStoreOrder()
    {
        FieldMetadata sorted = Field(1, "Sex", "Legal Values", tabIndex: 1, sqlType: "nvarchar(255)", sort: true);
        Dictionary<string, IReadOnlyList<FieldOption>> options = new(StringComparer.Ordinal)
        {
            ["Sex"] = [TextOption("U-Unknown"), TextOption("F-Female"), TextOption("M-Male")],
        };

        FieldDefinition field = Assert.Single(Assert.Single(Create(SinglePageForm(sorted), options).Pages).Fields);

        Assert.Equal([TextOption("U-Unknown"), TextOption("F-Female"), TextOption("M-Male")], field.Options);
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
            "float" or "datetime" or "bigint" => 8,
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

    /// <summary>
    /// Builds a seeded form from the transcribed <c>metaViews</c>, <c>metaPages</c> and <c>metaFields</c> rows in
    /// <c>FieldId</c> order: the view's three system rows, then every row of its pages. A row that forms.json lists on
    /// its page owns that page-table column, and every other row owns none. Each forms.json field must match one
    /// transcribed row of the same page and <c>FieldTypeId</c>, which guards the transcription.
    /// </summary>
    private static FormMetadata MetaFieldsForm(string formName)
    {
        (int viewId, _, int firstSystemFieldId) = Assert.Single(SeededMetaViews, view => view.Name == formName);
        PageMetadata[] pages = SeededMetaPages
            .Where(entry => entry.ViewId == viewId)
            .Select(entry => entry.Page)
            .ToArray();

        JsonElement formJson = SeededFormJson(formName);
        Assert.Equal(viewId, formJson.GetProperty("viewId").GetInt32());
        Dictionary<(int PageId, string Name), JsonElement> columnFields = new();
        foreach (JsonElement page in formJson.GetProperty("pages").EnumerateArray())
        {
            int pageId = page.GetProperty("pageId").GetInt32();
            foreach (JsonElement field in page.GetProperty("fields").EnumerateArray())
            {
                columnFields.Add((pageId, RequiredText(field, "name")), field);
            }
        }

        List<FieldMetadata> fields = [.. SystemRows(firstSystemFieldId, withBaseTableColumns: false)];
        IEnumerable<(int FieldId, string Name, int FieldTypeId, decimal TabIndex, int PageId, string? List)> rows =
            SeededMetaFieldsRows.Where(row => Array.Exists(pages, page => page.PageId == row.PageId));
        foreach ((int fieldId, string name, int fieldTypeId, decimal tabIndex, int pageId, string? list) in rows)
        {
            ColumnMetadata? column = null;
            if (columnFields.Remove((pageId, name), out JsonElement json))
            {
                Assert.Equal(json.GetProperty("fieldTypeId").GetInt32(), fieldTypeId);
                column = Column(name, RequiredText(json, "sqlType"));
            }

            fields.Add(new FieldMetadata
            {
                FieldId = fieldId,
                Name = name,
                FieldTypeId = fieldTypeId,
                FieldTypeName = SeededTypeNames[fieldTypeId],
                TabIndex = tabIndex,
                PageId = pageId,
                List = list,
                Column = column,
            });
        }

        Assert.Empty(columnFields.Keys);
        return new FormMetadata(viewId, formName, formName, pages, fields);
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
