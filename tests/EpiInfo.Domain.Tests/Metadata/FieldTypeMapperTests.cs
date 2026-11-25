using System.Globalization;
using System.Text.Json;
using EpiInfo.Domain.Diagnostics;
using EpiInfo.Domain.Forms;
using EpiInfo.Domain.Metadata;
using EpiInfo.Domain.Values;

namespace EpiInfo.Domain.Tests.Metadata;

/// <summary>
/// Unit tests of <see cref="FieldTypeMapper"/>, the one owner of field-type mapping and column-family compatibility:
/// name matching, the seeded <c>metaFieldTypes</c> names, the character fallback, the always-unsupported types,
/// column admission and description, the type predicates, Option items, the Legal Values sort that
/// <see cref="FormDefinitionFactory"/> applies, and the mapping of every data field in <c>testdata/forms.json</c>.
/// </summary>
/// <remarks>
/// The tests need no database: columns are described from <c>sys.columns</c>-style sizes, and
/// <c>testdata/forms.json</c> is read by path and never written.
/// </remarks>
public sealed class FieldTypeMapperTests
{
    private const string DefaultFieldName = "F1";

    // The metaFieldTypes rows seeded by testdata/sql/02_reference_data.sql, by FieldTypeId.
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

    private static readonly string[] AlwaysUnsupportedTypeNames =
    [
        "Grid",
        "Image",
        "Mirror",
        "GUID",
        "Command Button",
        "Relate",
        "Date/Time",
        "Time",
    ];

    [Theory]
    [InlineData("Legal Values")]
    [InlineData("legalvalues")]
    [InlineData("LEGAL VALUES")]
    public void NormalizeTypeName_LegalValuesSpellings_AreLowerCaseWithSpacesRemovedAndMapAlike(string typeName)
    {
        string normalized = FieldTypeMapper.NormalizeTypeName(typeName);

        Assert.Equal("legalvalues", normalized);
        Assert.Equal(FieldTypeMapper.NormalizeTypeName("Legal Values"), normalized);
        AssertSupported(FieldType.LegalValues, FieldTypeMapper.Map(FieldOn(typeName, "nvarchar(255)")));
    }

    [Theory]
    [InlineData("Label/Title", "label/title")]
    [InlineData("Text (Uppercase)", "text(uppercase)")]
    [InlineData("Command Button", "commandbutton")]
    [InlineData(null, "")]
    [InlineData("", "")]
    public void NormalizeTypeName_OtherNames_OnlyLowerCasesAndRemovesSpaces(string? typeName, string expected)
    {
        Assert.Equal(expected, FieldTypeMapper.NormalizeTypeName(typeName));
    }

    [Theory]
    [InlineData("Label/Title", null, FieldType.Label)]
    [InlineData("Group", null, FieldType.Group)]
    [InlineData("Text", "nvarchar(4000)", FieldType.Text)]
    [InlineData("Multiline", "ntext", FieldType.Multiline)]
    [InlineData("Number", "float", FieldType.Number)]
    [InlineData("Date", "datetime", FieldType.Date)]
    [InlineData("Checkbox", "bit", FieldType.Checkbox)]
    [InlineData("Option", "smallint", FieldType.Option)]
    [InlineData("Legal Values", "nvarchar(255)", FieldType.LegalValues)]
    public void Map_SeededSupportedName_MapsToItsTypeAndIsSupported(
        string typeName,
        string? sqlType,
        FieldType expected)
    {
        AssertSupported(expected, FieldTypeMapper.Map(FieldOn(typeName, sqlType)));
    }

    [Theory]
    [InlineData("Phone Number", "nvarchar(20)")]
    [InlineData("Comment Legal", "nvarchar(255)")]
    [InlineData("Text (Uppercase)", "nvarchar(255)")]
    [InlineData("Codes", "nvarchar(255)")]
    public void Map_UnlistedNameOnCharacterColumn_FallsBackToText(string typeName, string sqlType)
    {
        AssertSupported(FieldType.Text, FieldTypeMapper.Map(FieldOn(typeName, sqlType)));
    }

    // Yes/No on tinyint is the seeded case: text written into an integer column would fail or corrupt the data.
    [Theory]
    [InlineData("Phone Number", "float")]
    [InlineData("Yes/No", "tinyint")]
    public void Map_UnlistedNameOnNonCharacterColumn_IsUnsupportedNamingItsStoredType(string typeName, string sqlType)
    {
        AssertUnsupported(typeName, FieldTypeMapper.Map(FieldOn(typeName, sqlType)));
    }

    public static TheoryData<string, string?> AlwaysUnsupportedCases
    {
        get
        {
            TheoryData<string, string?> data = [];
            foreach (string typeName in AlwaysUnsupportedTypeNames)
            {
                data.Add(typeName, null);

                // A character column must never let the text fallback capture these types.
                data.Add(typeName, "nvarchar(255)");
            }

            data.Add("Date/Time", "datetime");
            data.Add("Time", "datetime");
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(AlwaysUnsupportedCases))]
    public void Map_AlwaysUnsupportedType_IsUnsupportedOnAnyColumn(string typeName, string? sqlType)
    {
        AssertUnsupported(typeName, FieldTypeMapper.Map(FieldOn(typeName, sqlType)));
    }

    [Theory]
    [InlineData("Text", "nvarchar(4000)")]
    [InlineData("Number", "float")]
    public void Map_EncryptedFieldOnAdmittedColumn_IsUnsupported(string typeName, string sqlType)
    {
        AssertUnsupported(typeName, FieldTypeMapper.Map(FieldOn(typeName, sqlType, encrypted: true)));
    }

    public static TheoryData<string, string, FieldType> AdmittedColumnCases
    {
        get
        {
            TheoryData<string, string, FieldType> data = [];
            foreach ((string typeName, FieldType type, string[] admitted, _) in AdmissionMatrix)
            {
                foreach (string sqlType in admitted)
                {
                    data.Add(typeName, sqlType, type);
                }
            }

            return data;
        }
    }

    public static TheoryData<string, string> RejectedColumnCases
    {
        get
        {
            TheoryData<string, string> data = [];
            foreach ((string typeName, _, _, string[] rejected) in AdmissionMatrix)
            {
                foreach (string sqlType in rejected)
                {
                    data.Add(typeName, sqlType);
                }
            }

            return data;
        }
    }

    // Per data type: the column types it is admitted on, and those that leave it unsupported.
    private static (string TypeName, FieldType Type, string[] Admitted, string[] Rejected)[] AdmissionMatrix =>
    [
        ("Text", FieldType.Text, TextFamilyAdmitted, TextFamilyRejected),
        ("Multiline", FieldType.Multiline, TextFamilyAdmitted, TextFamilyRejected),
        ("Legal Values", FieldType.LegalValues, TextFamilyAdmitted, TextFamilyRejected),
        (
            "Number",
            FieldType.Number,
            ["tinyint", "smallint", "int", "bigint", "float"],
            ["decimal", "numeric", "money", "real", "bit", "nvarchar(10)"]),
        ("Date", FieldType.Date, ["datetime"], ["date", "datetime2", "smalldatetime", "nvarchar(10)"]),
        ("Checkbox", FieldType.Checkbox, ["bit"], ["tinyint", "int", "nvarchar(5)"]),
        ("Option", FieldType.Option, ["tinyint", "smallint", "int"], ["float", "bigint", "bit", "nvarchar(10)"]),
    ];

    private static string[] TextFamilyAdmitted => ["nvarchar(50)", "nchar(10)", "nvarchar(max)", "ntext"];

    private static string[] TextFamilyRejected =>
        ["varchar(50)", "char(10)", "text", "int", "float", "datetime", "bit"];

    [Theory]
    [MemberData(nameof(AdmittedColumnCases))]
    public void Map_DataTypeOnCompatibleColumn_IsSupported(string typeName, string sqlType, FieldType expected)
    {
        AssertSupported(expected, FieldTypeMapper.Map(FieldOn(typeName, sqlType)));
    }

    [Theory]
    [MemberData(nameof(RejectedColumnCases))]
    public void Map_DataTypeOnIncompatibleColumn_IsUnsupportedNamingItsStoredType(string typeName, string sqlType)
    {
        AssertUnsupported(typeName, FieldTypeMapper.Map(FieldOn(typeName, sqlType)));
    }

    // A data type with no page-table column has nowhere to write its value.
    [Theory]
    [InlineData("Text")]
    [InlineData("Number")]
    [InlineData("Date")]
    [InlineData("Checkbox")]
    [InlineData("Option")]
    [InlineData("Legal Values")]
    public void Map_DataTypeWithoutColumn_IsUnsupported(string typeName)
    {
        AssertUnsupported(typeName, FieldTypeMapper.Map(Field(typeName, column: null)));
    }

    [Theory]
    [InlineData("nvarchar", ColumnFamily.Character)]
    [InlineData("nchar", ColumnFamily.Character)]
    [InlineData("ntext", ColumnFamily.Character)]
    [InlineData("tinyint", ColumnFamily.Integer)]
    [InlineData("smallint", ColumnFamily.Integer)]
    [InlineData("int", ColumnFamily.Integer)]
    [InlineData("bigint", ColumnFamily.Integer)]
    [InlineData("float", ColumnFamily.Float)]
    [InlineData("datetime", ColumnFamily.DateTime)]
    [InlineData("bit", ColumnFamily.Bit)]
    [InlineData("varchar", ColumnFamily.Other)]
    [InlineData("char", ColumnFamily.Other)]
    [InlineData("text", ColumnFamily.Other)]
    [InlineData("decimal", ColumnFamily.Other)]
    [InlineData("numeric", ColumnFamily.Other)]
    [InlineData("money", ColumnFamily.Other)]
    [InlineData("real", ColumnFamily.Other)]
    [InlineData("date", ColumnFamily.Other)]
    [InlineData("datetime2", ColumnFamily.Other)]
    [InlineData("smalldatetime", ColumnFamily.Other)]
    [InlineData("uniqueidentifier", ColumnFamily.Other)]
    [InlineData("varbinary", ColumnFamily.Other)]
    public void ClassifyColumn_SysTypesName_GivesItsFamily(string sqlTypeName, ColumnFamily expected)
    {
        Assert.Equal(expected, FieldTypeMapper.ClassifyColumn(sqlTypeName));
    }

    [Theory]
    [InlineData("LastName", "nvarchar", 8000, 4000, ColumnFamily.Character)]
    [InlineData("StateCode", "nchar", 20, 10, ColumnFamily.Character)]
    [InlineData("Notes", "nvarchar", -1, null, ColumnFamily.Character)]
    [InlineData("BuyLastTenDays", "ntext", 16, null, ColumnFamily.Character)]
    [InlineData("CaseID", "float", 8, null, ColumnFamily.Float)]
    public void DescribeColumn_CatalogueValues_GiveCharacterLengthAndFamily(
        string name,
        string sqlTypeName,
        int maxLengthBytes,
        int? expectedMaxCharacters,
        ColumnFamily expectedFamily)
    {
        ColumnMetadata column = FieldTypeMapper.DescribeColumn(name, sqlTypeName, maxLengthBytes);

        Assert.Equal(new ColumnMetadata(name, sqlTypeName, expectedMaxCharacters, expectedFamily), column);
    }

    // The catalogue never reports these sizes for a Unicode character column.
    [Theory]
    [InlineData("nvarchar", 0)]
    [InlineData("nchar", -2)]
    public void DescribeColumn_ImpossibleCharacterSize_Throws(string sqlTypeName, int maxLengthBytes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => FieldTypeMapper.DescribeColumn("F1", sqlTypeName, maxLengthBytes));
    }

    [Theory]
    [InlineData(FieldType.Label, false, false)]
    [InlineData(FieldType.Group, false, false)]
    [InlineData(FieldType.Text, true, true)]
    [InlineData(FieldType.Multiline, true, true)]
    [InlineData(FieldType.Number, false, true)]
    [InlineData(FieldType.Date, false, true)]
    [InlineData(FieldType.Checkbox, false, true)]
    [InlineData(FieldType.Option, false, true)]
    [InlineData(FieldType.LegalValues, true, true)]
    [InlineData(FieldType.Unsupported, false, false)]
    public void Predicates_EachFieldType_MatchTheMappingTable(FieldType type, bool textFamily, bool dataField)
    {
        Assert.Equal(textFamily, FieldTypeMapper.IsTextFamily(type));
        Assert.Equal(dataField, FieldTypeMapper.IsDataFieldType(type));
    }

    [Theory]
    [InlineData("Phone Number", true)]
    [InlineData("phonenumber", true)]
    [InlineData("Text", false)]
    public void IsPhoneNumber_ByMetadataTypeName_RecognisesOnlyPhoneNumber(string typeName, bool expected)
    {
        Assert.Equal(expected, FieldTypeMapper.IsPhoneNumber(FieldOn(typeName, "nvarchar(20)")));
    }

    // Synthetic, because no seeded field is an Option: the labels are the items before "||", and each value is the
    // item's 0-based index, as legacy OptionField reads metaFields.List.
    [Theory]
    [InlineData("Red,Green,Blue||1,2,3")]
    [InlineData("Red,Green,Blue")]
    public void ParseOptionItems_StoredList_GivesIndexedLabelsBeforeTheSeparator(string list)
    {
        FieldOption[] expected =
        [
            new(FieldValue.FromIndex(0), "Red"),
            new(FieldValue.FromIndex(1), "Green"),
            new(FieldValue.FromIndex(2), "Blue"),
        ];

        Assert.Equal(expected, FieldTypeMapper.ParseOptionItems(list));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ParseOptionItems_NoList_GivesNoOptions(string? list)
    {
        Assert.Empty(FieldTypeMapper.ParseOptionItems(list));
    }

    // FormDefinitionFactory sorts with StringComparer.InvariantCulture, a culture-aware comparer that orders letters
    // before case, so "alpha" precedes "Mid". A plain ordinal sort would give Mid, Zeta, alpha.
    [Theory]
    [InlineData(true, new[] { "alpha", "Mid", "Zeta" })]
    [InlineData(false, new[] { "Zeta", "alpha", "Mid" })]
    public void Create_LegalValuesSortFlag_SortsOptionsByTextOrKeepsStoreOrder(bool sort, string[] expectedLabels)
    {
        FieldMetadata colour = new()
        {
            FieldId = 1,
            Name = "Colour",
            FieldTypeId = 17,
            FieldTypeName = "Legal Values",
            PageId = 1,
            TabIndex = 1,
            Column = Column("Colour", "nvarchar(255)"),
            SourceTableName = "codecolour",
            TextColumnName = "Colour",
            Sort = sort,
        };
        FormMetadata form = new(
            ViewId: 99,
            Name: "Synthetic",
            BaseTableName: "Synthetic",
            Pages: [new PageMetadata(1, "Page 1", 0, "Synthetic1")],
            Fields: [colour]);
        Dictionary<string, IReadOnlyList<FieldOption>> legalValueOptions = new()
        {
            ["Colour"] = new[] { "Zeta", "alpha", "Mid" }
                .Select(text => new FieldOption(FieldValue.FromText(text), text))
                .ToArray(),
        };

        FormDefinition definition = FormDefinitionFactory.Create(form, legalValueOptions, Array.Empty<Diagnostic>());

        FieldDefinition field = Assert.Single(Assert.Single(definition.Pages).Fields);
        AssertSupported(FieldType.LegalValues, (field.FieldType, field.Unsupported, field.UnsupportedType));
        IReadOnlyList<FieldOption>? options = field.Options;
        Assert.NotNull(options);
        Assert.Equal(expectedLabels, options.Select(option => option.Label));
        Assert.Equal(expectedLabels.Select(FieldValue.FromText), options.Select(option => option.Value));
    }


    [Fact]
    public void Map_EveryCaseReportFieldOfFormsJson_IsSupportedTextNumberOrDate()
    {
        FormSweep sweep = SweepForm("CaseReport");

        Assert.Equal(1, sweep.ViewId);
        AssertMappingInvariants(sweep.Fields);
        AssertCounts(new Dictionary<int, int> { [1] = 43 }, CountByPage(sweep.Fields));
        AssertCounts(
            new Dictionary<FieldType, int>
            {
                [FieldType.Text] = 37,
                [FieldType.Number] = 2,
                [FieldType.Date] = 4,
            },
            CountByFieldType(sweep.Fields));
        AssertCounts(
            new Dictionary<string, int>
            {
                ["Text (Text)"] = 16,
                ["Text (Comment Legal)"] = 18,
                ["Text (Phone Number)"] = 3,
                ["Number (Number)"] = 2,
                ["Date (Date)"] = 4,
            },
            CountByFieldTypeAndSeededName(sweep.Fields));
        Assert.DoesNotContain(sweep.Fields, field => field.Unsupported);
    }

    [Fact]
    public void Map_EveryFoodHistoryFieldOfFormsJson_LeavesOnlyTheSevenYesNoFieldsUnsupported()
    {
        FormSweep sweep = SweepForm("FoodHistory");

        Assert.Equal(2, sweep.ViewId);
        AssertMappingInvariants(sweep.Fields);
        AssertCounts(new Dictionary<int, int> { [2] = 45, [3] = 29 }, CountByPage(sweep.Fields));
        AssertCounts(
            new Dictionary<FieldType, int>
            {
                [FieldType.Unsupported] = 7,
                [FieldType.Text] = 9,
                [FieldType.Multiline] = 2,
                [FieldType.Number] = 6,
                [FieldType.Date] = 7,
                [FieldType.Checkbox] = 40,
                [FieldType.LegalValues] = 3,
            },
            CountByFieldType(sweep.Fields));
        AssertCounts(
            new Dictionary<string, int>
            {
                ["Unsupported (Yes/No)"] = 7,
                ["Text (Text)"] = 8,
                ["Text (Phone Number)"] = 1,
                ["Multiline (Multiline)"] = 2,
                ["Number (Number)"] = 6,
                ["Date (Date)"] = 7,
                ["Checkbox (Checkbox)"] = 40,
                ["LegalValues (Legal Values)"] = 3,
            },
            CountByFieldTypeAndSeededName(sweep.Fields));

        MappedField[] unsupported = sweep.Fields.Where(field => field.Unsupported).ToArray();
        Assert.All(unsupported, field => Assert.Equal("Yes/No", field.UnsupportedType));
        Assert.Equal(
            new[] { "Antibiotics", "Died", "DoctorVisit", "Hospitalized", "ILL", "O157H7isolated", "StoolSubmitted" },
            unsupported.Select(field => field.Name).Order(StringComparer.Ordinal));
        Assert.Equal(67, sweep.Fields.Count(field => !field.Unsupported));
    }

    private static void AssertSupported(
        FieldType expected,
        (FieldType FieldType, bool Unsupported, string? UnsupportedType) result)
    {
        Assert.Equal(expected, result.FieldType);
        Assert.False(result.Unsupported);
        Assert.Null(result.UnsupportedType);
    }

    // The placeholder names the type exactly as stored, never its normalised form.
    private static void AssertUnsupported(
        string expectedType,
        (FieldType FieldType, bool Unsupported, string? UnsupportedType) result)
    {
        Assert.Equal(FieldType.Unsupported, result.FieldType);
        Assert.True(result.Unsupported);
        Assert.Equal(expectedType, result.UnsupportedType);
    }

    private static void AssertMappingInvariants(IReadOnlyList<MappedField> fields)
    {
        Assert.All(fields, field =>
        {
            Assert.Equal(field.FieldType == FieldType.Unsupported, field.Unsupported);
            Assert.Equal(field.Unsupported ? field.SeededTypeName : null, field.UnsupportedType);
        });
    }

    // Compares two count dictionaries as their "key = count" entries in ordinal key order, so a failure points at the
    // first entry that differs and names its key, where a dictionary comparison would truncate the message.
    private static void AssertCounts<TKey>(
        IReadOnlyDictionary<TKey, int> expected,
        IReadOnlyDictionary<TKey, int> actual)
        where TKey : notnull
    {
        Assert.Equal(CountEntries(expected), CountEntries(actual));
    }

    private static string[] CountEntries<TKey>(IReadOnlyDictionary<TKey, int> counts)
        where TKey : notnull =>
        counts
            .Select(pair => string.Create(CultureInfo.InvariantCulture, $"{pair.Key} = {pair.Value}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static Dictionary<int, int> CountByPage(IEnumerable<MappedField> fields) =>
        fields.CountBy(field => field.PageId).ToDictionary();

    private static Dictionary<FieldType, int> CountByFieldType(IEnumerable<MappedField> fields) =>
        fields.CountBy(field => field.FieldType).ToDictionary();

    private static Dictionary<string, int> CountByFieldTypeAndSeededName(IEnumerable<MappedField> fields) =>
        fields.CountBy(field => $"{field.FieldType} ({field.SeededTypeName})").ToDictionary();

    // Maps every field of one form in testdata/forms.json, with the seeded type name of its fieldTypeId and the
    // column its sqlType describes.
    private static FormSweep SweepForm(string formName)
    {
        using JsonDocument document = LoadFormsJson();
        JsonElement form = document.RootElement
            .GetProperty("forms")
            .EnumerateArray()
            .Single(candidate => RequiredString(candidate, "name") == formName);

        List<MappedField> fields = [];
        foreach (JsonElement page in form.GetProperty("pages").EnumerateArray())
        {
            int pageId = page.GetProperty("pageId").GetInt32();
            foreach (JsonElement entry in page.GetProperty("fields").EnumerateArray())
            {
                string name = RequiredString(entry, "name");
                int fieldTypeId = entry.GetProperty("fieldTypeId").GetInt32();
                string seededTypeName = SeededTypeNames[fieldTypeId];
                FieldMetadata field = new()
                {
                    FieldId = fields.Count + 1,
                    Name = name,
                    FieldTypeId = fieldTypeId,
                    FieldTypeName = seededTypeName,
                    PageId = pageId,
                    TabIndex = fields.Count + 1,
                    Column = Column(name, RequiredString(entry, "sqlType")),
                };

                (FieldType fieldType, bool unsupported, string? unsupportedType) = FieldTypeMapper.Map(field);
                fields.Add(new MappedField(name, pageId, seededTypeName, fieldType, unsupported, unsupportedType));
            }
        }

        return new FormSweep(form.GetProperty("viewId").GetInt32(), fields);
    }

    private static JsonDocument LoadFormsJson() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(FindRepositoryRoot(), "testdata", "forms.json")));

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EpiInfo.Web.sln")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException(
            $"No directory at or above '{AppContext.BaseDirectory}' contains EpiInfo.Web.sln, "
            + "so testdata/forms.json cannot be located.");
    }

    private static string RequiredString(JsonElement element, string propertyName) =>
        element.GetProperty(propertyName).GetString()
        ?? throw new InvalidOperationException($"testdata/forms.json holds a null '{propertyName}'.");

    private static FieldMetadata FieldOn(string typeName, string? sqlType, bool encrypted = false) =>
        Field(typeName, sqlType is null ? null : Column(DefaultFieldName, sqlType), encrypted);

    private static FieldMetadata Field(
        string typeName,
        ColumnMetadata? column,
        bool encrypted = false,
        string name = DefaultFieldName) => new()
        {
            FieldId = 1,
            Name = name,
            FieldTypeId = SeededTypeId(typeName),
            FieldTypeName = typeName,
            PageId = 1,
            TabIndex = 1,
            IsEncrypted = encrypted,
            Column = column,
        };

    // The seeded id of a type name in any letter case or spacing, so the alternative spellings still carry the id
    // of the row they stand for. Map reads only the name.
    private static int SeededTypeId(string typeName)
    {
        string wanted = Compact(typeName);
        foreach ((int id, string seededName) in SeededTypeNames)
        {
            if (string.Equals(Compact(seededName), wanted, StringComparison.Ordinal))
            {
                return id;
            }
        }

        throw new ArgumentException($"'{typeName}' is not a seeded metaFieldTypes name.", nameof(typeName));
    }

    private static string Compact(string name) =>
        string.Concat(name.Where(character => !char.IsWhiteSpace(character))).ToUpperInvariant();

    // Describes a forms.json-style column type, such as nvarchar(4000), with the max_length sys.columns reports.
    private static ColumnMetadata Column(string name, string sqlType)
    {
        int open = sqlType.IndexOf('(');
        if (open < 0)
        {
            return FieldTypeMapper.DescribeColumn(name, sqlType, FixedSizeBytes(sqlType));
        }

        if (!sqlType.EndsWith(')'))
        {
            throw new ArgumentException($"'{sqlType}' is not a sized column type.", nameof(sqlType));
        }

        string typeName = sqlType[..open];
        string length = sqlType[(open + 1)..^1];
        int bytesPerCharacter = typeName switch
        {
            "nvarchar" or "nchar" => 2,
            "varchar" or "char" => 1,
            _ => throw new ArgumentException($"'{sqlType}' is not a sized character type.", nameof(sqlType)),
        };
        int maxLengthBytes = length == "max"
            ? -1
            : int.Parse(length, NumberStyles.None, CultureInfo.InvariantCulture) * bytesPerCharacter;
        return FieldTypeMapper.DescribeColumn(name, typeName, maxLengthBytes);
    }

    private static int FixedSizeBytes(string sqlType) => sqlType switch
    {
        "ntext" or "text" => 16,
        "float" or "datetime" or "datetime2" or "bigint" or "money" => 8,
        "smalldatetime" or "int" or "real" => 4,
        "date" => 3,
        "smallint" => 2,
        "tinyint" or "bit" => 1,
        "decimal" or "numeric" => 9,
        _ => throw new ArgumentException($"'{sqlType}' has no known fixed size.", nameof(sqlType)),
    };

    private sealed record MappedField(
        string Name,
        int PageId,
        string SeededTypeName,
        FieldType FieldType,
        bool Unsupported,
        string? UnsupportedType);

    private sealed record FormSweep(int ViewId, IReadOnlyList<MappedField> Fields);
}
