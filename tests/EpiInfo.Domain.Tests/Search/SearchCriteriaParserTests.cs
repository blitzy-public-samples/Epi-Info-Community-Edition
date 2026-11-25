using System.Globalization;
using EpiInfo.Domain.Forms;
using EpiInfo.Domain.Metadata;
using EpiInfo.Domain.Records;
using EpiInfo.Domain.Search;
using EpiInfo.Domain.Values;

namespace EpiInfo.Domain.Tests.Search;

/// <summary>
/// Unit tests of <see cref="SearchCriteriaParser"/>: the legacy Find semantics the target keeps
/// (<c>Epi.Windows.Enter/Dialogs/FindRecordDialog.cs</c>, lines 103-120, 293-374, 404-770;
/// <c>Epi.Core/Data/Services/CollectedDataProvider.cs</c>, lines 1890-2380) and the target's deliberate departures
/// from them. Parsing only: the SQL the conditions become and the rows a search returns are covered by the API's
/// list-and-search integration tests.
/// </summary>
/// <remarks>
/// The fixture is one synthetic two-page form built in code. It holds one field of every searchable kind, a label,
/// a group, an unsupported <c>Yes/No</c> field and the three system rows, with the seeded <c>metaFieldTypes</c> ids
/// and names (<c>testdata/sql/02_reference_data.sql</c>) and <c>sys.columns</c> byte lengths. No database, file or
/// environment variable is used.
/// </remarks>
public sealed class SearchCriteriaParserTests
{
    private const string Sentinel = "SENTINEL-9b1e";

    private static readonly (SearchOperator Op, FieldValue? Value) IsNull = (SearchOperator.IsNull, null);

    private static readonly DataFieldSet Fields = FormDefinitionFactory.GetDataFields(BuildForm());

    private static readonly string[] ExpectedDataFieldNames =
    [
        "LastName", "FirstName", "City", "State", "PhoneNumber", "Notes", "Sex", "Age", "Choice", "DOB", "Asian",
    ];

    #region Fixture

    private static FormMetadata BuildForm()
    {
        PageMetadata page1 = new(1, "Page 1", 0, "SearchForm1");
        PageMetadata page2 = new(2, "Page 2", 1, "SearchForm2");

        FieldMetadata[] fields =
        [
            PageField(1, "LastName", 1, "Text", page1, "nvarchar", 8000),
            PageField(2, "FirstName", 1, "Text", page1, "nvarchar", 8000),
            PageField(3, "City", 1, "Text", page1, "nvarchar", 8000),
            PageField(4, "State", 1, "Text", page1, "nvarchar", 8000),
            PageField(5, "PhoneNumber", 6, "Phone Number", page1, "nvarchar", 40),
            PageField(6, "Notes", 4, "Multiline", page1, "ntext", 16),
            PageField(7, "Sex", 17, "Legal Values", page1, "nvarchar", 510) with
            {
                SourceTableName = "codeSex",
                TextColumnName = "sex",
            },
            PageField(8, "Age", 5, "Number", page1, "float", 8),
            PageField(9, "Heading", 2, "Label/Title", page1),
            PageField(10, "Demographics", 21, "Group", page1) with { List = "LastName,FirstName" },
            PageField(11, "Choice", 12, "Option", page2, "smallint", 2) with { List = "A,B,C" },
            PageField(12, "DOB", 7, "Date", page2, "datetime", 8),
            PageField(13, "Asian", 10, "Checkbox", page2, "bit", 1),
            PageField(14, "ILL", 11, "Yes/No", page2, "tinyint", 1),
            SystemField(15, "UniqueKey", 23),
            SystemField(16, "RecStatus", 22),
            SystemField(17, "GlobalRecordId", 26),
        ];

        return new FormMetadata(1, "SearchForm", "SearchForm", [page1, page2], fields);
    }

    // A page-bound row. Labels and groups own no page-table column, so they pass no SQL type.
    private static FieldMetadata PageField(
        int fieldId,
        string name,
        int fieldTypeId,
        string fieldTypeName,
        PageMetadata page,
        string? sqlTypeName = null,
        int maxLengthBytes = 0)
    {
        return new FieldMetadata
        {
            FieldId = fieldId,
            Name = name,
            FieldTypeId = fieldTypeId,
            FieldTypeName = fieldTypeName,
            PageId = page.PageId,
            TabIndex = fieldId,
            Column = sqlTypeName is null ? null : FieldTypeMapper.DescribeColumn(name, sqlTypeName, maxLengthBytes),
        };
    }

    // A system row as seeded: no page, no tab index and no page-table column.
    private static FieldMetadata SystemField(int fieldId, string name, int fieldTypeId) => new()
    {
        FieldId = fieldId,
        Name = name,
        FieldTypeId = fieldTypeId,
        FieldTypeName = name,
    };

    private static SearchParseResult Parse(params SearchCriterion[] criteria) =>
        SearchCriteriaParser.Parse(Fields, criteria);

    private static SearchCriterion Text(string field, string value) => new(field, FieldValue.FromText(value));

    private static SearchCriterion Value(string field, FieldValue value) => new(field, value);

    private static SearchCondition SingleCondition(SearchParseResult result)
    {
        Assert.True(
            result.IsSuccess,
            $"Expected success; request error: {result.RequestError ?? "none"}; " +
            $"field errors under: {string.Join(", ", result.FieldErrors.Keys)}.");
        Assert.Null(result.RequestError);
        Assert.Empty(result.FieldErrors);
        return Assert.Single(result.Conditions);
    }

    private static void AssertAlternatives(
        SearchCondition condition,
        params (SearchOperator Op, FieldValue? Value)[] expected)
    {
        Assert.Equal(expected.Length, condition.Alternatives.Count);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Op, condition.Alternatives[i].Operator);
            Assert.Equal(expected[i].Value, condition.Alternatives[i].Value);
        }
    }

    private static void AssertFieldError(SearchParseResult result, string key)
    {
        Assert.False(result.IsSuccess);
        Assert.Null(result.RequestError);
        Assert.Empty(result.Conditions);
        Assert.Contains(key, result.FieldErrors.Keys);
        string[] messages = result.FieldErrors[key];
        Assert.NotEmpty(messages);
        Assert.All(messages, message => Assert.False(string.IsNullOrWhiteSpace(message)));
    }

    private static IEnumerable<string> AllMessages(SearchParseResult result)
    {
        foreach (var message in result.FieldErrors.Values.SelectMany(messages => messages))
        {
            yield return message;
        }

        if (result.RequestError is { } requestError)
        {
            yield return requestError;
        }
    }

    private static (SearchOperator Op, FieldValue? Value) EqualText(string text) =>
        (SearchOperator.Equal, FieldValue.FromText(text));

    private static (SearchOperator Op, FieldValue? Value) EqualNumber(double number) =>
        (SearchOperator.Equal, FieldValue.FromNumber(number));

    private static (SearchOperator Op, FieldValue? Value) EqualDate(int year, int month, int day) =>
        (SearchOperator.Equal, FieldValue.FromDate(new DateOnly(year, month, day)));

    private static (SearchOperator Op, FieldValue? Value) EqualBoolean(bool value) =>
        (SearchOperator.Equal, FieldValue.FromBoolean(value));

    private static (SearchOperator Op, FieldValue? Value) LikeText(string pattern) =>
        (SearchOperator.Like, FieldValue.FromText(pattern));

    [Fact]
    public void Fixture_DataFieldSet_HoldsExactlyTheSearchableDataFields()
    {
        Assert.Equal(
            ExpectedDataFieldNames.Order(StringComparer.Ordinal).ToArray(),
            Fields.Select(field => field.Name).Order(StringComparer.Ordinal).ToArray());
    }

    [Theory]
    [InlineData("ILL")]
    [InlineData("Heading")]
    [InlineData("Demographics")]
    [InlineData("UniqueKey")]
    [InlineData("RecStatus")]
    [InlineData("GlobalRecordId")]
    public void Fixture_NonDataRows_AreAbsentFromTheDataFieldSet(string name)
    {
        Assert.False(Fields.TryGet(name, out _));
    }

    [Theory]
    [InlineData("LastName", FieldType.Text)]
    [InlineData("PhoneNumber", FieldType.Text)]
    [InlineData("Notes", FieldType.Multiline)]
    [InlineData("Sex", FieldType.LegalValues)]
    [InlineData("Age", FieldType.Number)]
    [InlineData("Choice", FieldType.Option)]
    [InlineData("DOB", FieldType.Date)]
    [InlineData("Asian", FieldType.Checkbox)]
    public void Fixture_DataFields_MapToTheExpectedTypes(string name, FieldType expected)
    {
        DataFieldMetadata field = Assert.Single(Fields, candidate => candidate.Name == name);
        Assert.Equal(expected, field.FieldType);
    }

    #endregion

    #region Text family

    // Text, multiline and legal values strip a leading <=, >=, <>, > or < and ignore it
    // (FindRecordDialog.cs:708-770; CollectedDataProvider.cs:2186-2231).
    [Theory]
    [InlineData(">Smith")]
    [InlineData("<Smith")]
    [InlineData(">=Smith")]
    [InlineData("<=Smith")]
    [InlineData("<>Smith")]
    public void Text_PrefixIsStrippedAndIgnored(string value)
    {
        AssertAlternatives(SingleCondition(Parse(Text("LastName", value))), EqualText("Smith"));
    }

    // A value left empty once its prefix is stripped matches a column holding no value
    // (CollectedDataProvider.cs:2186-2193).
    [Theory]
    [InlineData("LastName", ">")]
    [InlineData("LastName", "<>")]
    [InlineData("LastName", ">=")]
    [InlineData("LastName", "<=")]
    [InlineData("LastName", "<")]
    [InlineData("LastName", "  >  ")]
    [InlineData("Sex", ">")]
    [InlineData("Notes", ">")]
    public void Text_BarePrefix_MatchesNull(string field, string value)
    {
        AssertAlternatives(SingleCondition(Parse(Text(field, value))), IsNull);
    }

    // The legacy dialog rejects a blank text criterion (FindRecordDialog.cs:611-643), so the API answers 400.
    [Theory]
    [InlineData("LastName", "")]
    [InlineData("LastName", "   ")]
    [InlineData("Sex", "")]
    [InlineData("Notes", "   ")]
    public void Text_Blank_IsAFieldError(string field, string value)
    {
        AssertFieldError(Parse(Text(field, value)), field);
    }

    [Fact]
    public void Text_JsonNull_IsABlankFieldError()
    {
        AssertFieldError(Parse(Value("LastName", FieldValue.Null)), "LastName");
    }

    [Fact]
    public void Text_NonTextValue_IsAFieldError()
    {
        AssertFieldError(Parse(Value("LastName", FieldValue.FromNumber(5))), "LastName");
        AssertFieldError(Parse(Value("LastName", FieldValue.FromBoolean(true))), "LastName");
    }

    // The legacy dialog trims each control's text (FindRecordDialog.cs:355-374).
    [Fact]
    public void Text_SurroundingWhitespace_IsTrimmed()
    {
        AssertAlternatives(SingleCondition(Parse(Text("LastName", "  Smith  "))), EqualText("Smith"));
    }

    // Only %, * or ? switch to a pattern match; _ and [...] alone do not, and quotes are ordinary characters.
    [Theory]
    [InlineData("Sm_th")]
    [InlineData("Sm[i]th")]
    [InlineData("O'Brien")]
    public void Text_WithoutPercentStarOrQuestionMark_IsAnEqualityMatch(string value)
    {
        AssertAlternatives(SingleCondition(Parse(Text("LastName", value))), EqualText(value));
    }

    // Legal values follow the text path, and Find does not check option membership.
    [Fact]
    public void LegalValues_FollowTheTextPathWithoutAMembershipCheck()
    {
        AssertAlternatives(SingleCondition(Parse(Text("Sex", "F"))), EqualText("F"));
        AssertAlternatives(SingleCondition(Parse(Text("Sex", "F*"))), LikeText("F%"));
        AssertAlternatives(SingleCondition(Parse(Text("Sex", "Z"))), EqualText("Z"));
    }

    // Every page-field kind: the condition carries the canonical name, the page-field target and the mapped type.
    // Phone Number is searched on its own path but keeps the text type.
    [Theory]
    [InlineData("LastName", "Smith", FieldType.Text)]
    [InlineData("Sex", "F", FieldType.LegalValues)]
    [InlineData("PhoneNumber", "555", FieldType.Text)]
    [InlineData("Notes", "abc", FieldType.Multiline)]
    [InlineData("Age", "18", FieldType.Number)]
    [InlineData("Choice", "2", FieldType.Option)]
    [InlineData("DOB", "2011-01-15", FieldType.Date)]
    [InlineData("Asian", "Yes", FieldType.Checkbox)]
    public void PageField_Condition_CarriesCanonicalNameTargetAndMappedType(
        string field,
        string value,
        FieldType expectedType)
    {
        SearchCondition condition = SingleCondition(Parse(Text(field, value)));

        Assert.Equal(field, condition.FieldName);
        Assert.Equal(SearchTarget.PageField, condition.Target);
        Assert.Equal(expectedType, condition.FieldType);
    }

    #endregion

    #region Wildcard patterns

    // CollectedDataProvider.cs:2195-2206. Only * is translated to %; ? triggers the pattern path but stays a
    // literal character. The pattern is a parameter, so a quote stays in it where legacy removed it.
    [Theory]
    [InlineData("Sm*", "Sm%")]
    [InlineData("O'Br*", "O'Br%")]
    [InlineData("S%th", "S%th")]
    [InlineData("Sm?th", "Sm?th")]
    [InlineData(">Sm*", "Sm%")]
    [InlineData("*", "%")]
    public void Wildcard_TextWithPercentStarOrQuestionMark_IsALikePattern(string value, string expectedPattern)
    {
        AssertAlternatives(SingleCondition(Parse(Text("LastName", value))), LikeText(expectedPattern));
    }

    #endregion

    #region Multiline

    // The wildcard check precedes the multiline check (CollectedDataProvider.cs:2195-2222).
    [Fact]
    public void Multiline_PlainText_ComparesTheFirst255Characters()
    {
        SearchCondition condition = SingleCondition(Parse(Text("Notes", "abc")));

        Assert.Equal(FieldType.Multiline, condition.FieldType);
        AssertAlternatives(condition, (SearchOperator.PrefixEqual255, FieldValue.FromText("abc")));
    }

    // The 255-character truncation happens in SQL on both sides, so the parameter carries the full text.
    [Fact]
    public void Multiline_LongValue_CarriesTheFullText()
    {
        var value = new string('x', 300);

        AssertAlternatives(
            SingleCondition(Parse(Text("Notes", value))),
            (SearchOperator.PrefixEqual255, FieldValue.FromText(value)));
    }

    [Fact]
    public void Multiline_Wildcard_IsALikePatternNotAPrefixComparison()
    {
        AssertAlternatives(SingleCondition(Parse(Text("Notes", "Ab*"))), LikeText("Ab%"));
    }

    #endregion

    #region Phone Number

    // Phone Number is chosen by its metaFieldTypes name: the prefix is the operator, and there is no wildcard path
    // (CollectedDataProvider.cs:2118-2131).
    [Theory]
    [InlineData(">555", SearchOperator.Greater)]
    [InlineData(">=555", SearchOperator.GreaterOrEqual)]
    [InlineData("<=555", SearchOperator.LessOrEqual)]
    [InlineData("<>555", SearchOperator.NotEqual)]
    [InlineData("<555", SearchOperator.Less)]
    [InlineData("555", SearchOperator.Equal)]
    [InlineData("  >555  ", SearchOperator.Greater)]
    public void Phone_PrefixIsTheOperator(string value, SearchOperator expected)
    {
        AssertAlternatives(
            SingleCondition(Parse(Text("PhoneNumber", value))),
            (expected, FieldValue.FromText("555")));
    }

    [Theory]
    [InlineData("555*")]
    [InlineData("5%5")]
    [InlineData("55?")]
    public void Phone_WildcardCharacters_CompareAsThemselves(string value)
    {
        AssertAlternatives(SingleCondition(Parse(Text("PhoneNumber", value))), EqualText(value));
    }

    [Theory]
    [InlineData(">")]
    [InlineData("<>")]
    public void Phone_BarePrefix_MatchesNull(string value)
    {
        AssertAlternatives(SingleCondition(Parse(Text("PhoneNumber", value))), IsNull);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Phone_Blank_IsAFieldError(string value)
    {
        AssertFieldError(Parse(Text("PhoneNumber", value)), "PhoneNumber");
    }

    #endregion

    #region Alternatives

    // The trimmed value is split on exactly " or ", " OR ", " Or " and " oR ", dropping empty entries
    // (FindRecordDialog.cs:438).
    [Theory]
    [InlineData("a or b")]
    [InlineData("a OR b")]
    [InlineData("a Or b")]
    [InlineData("a oR b")]
    public void Alternatives_EachSeparatorSpelling_SplitsInOrder(string value)
    {
        AssertAlternatives(SingleCondition(Parse(Text("LastName", value))), EqualText("a"), EqualText("b"));
    }

    [Fact]
    public void Alternatives_WithoutSurroundingSpaces_DoNotSplit()
    {
        AssertAlternatives(SingleCondition(Parse(Text("LastName", "aorb"))), EqualText("aorb"));
    }

    [Fact]
    public void Alternatives_ThreeAlternatives_KeepTheirOrder()
    {
        AssertAlternatives(
            SingleCondition(Parse(Text("LastName", "a or b or c"))),
            EqualText("a"),
            EqualText("b"),
            EqualText("c"));
    }

    [Fact]
    public void Alternatives_EmptyEntry_IsDropped()
    {
        AssertAlternatives(SingleCondition(Parse(Text("LastName", "a or  or b"))), EqualText("a"), EqualText("b"));
    }

    // The whole value is trimmed before the split, so a trailing separator loses its last space and does not split.
    [Fact]
    public void Alternatives_TrailingSeparator_IsTrimmedBeforeTheSplit()
    {
        AssertAlternatives(SingleCondition(Parse(Text("LastName", "a or "))), EqualText("a or"));
    }

    // Each alternative keeps the characters the split leaves, as legacy reads them: they are not trimmed again.
    [Fact]
    public void Alternatives_AreNotTrimmedAgainAfterTheSplit()
    {
        AssertAlternatives(SingleCondition(Parse(Text("LastName", "a  or b"))), EqualText("a "), EqualText("b"));
    }

    [Fact]
    public void Alternatives_PrefixesApplyPerAlternative()
    {
        AssertAlternatives(
            SingleCondition(Parse(Text("Age", ">5 or <2"))),
            (SearchOperator.Greater, FieldValue.FromNumber(5)),
            (SearchOperator.Less, FieldValue.FromNumber(2)));

        AssertAlternatives(
            SingleCondition(Parse(Text("PhoneNumber", ">1 or <9"))),
            (SearchOperator.Greater, FieldValue.FromText("1")),
            (SearchOperator.Less, FieldValue.FromText("9")));
    }

    #endregion

    #region Number

    // FindRecordDialog.cs:418-463; CollectedDataProvider.cs:2046-2066. A blank number criterion matches a column
    // holding no value.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Number_Blank_MatchesNull(string value)
    {
        AssertAlternatives(SingleCondition(Parse(Text("Age", value))), IsNull);
    }

    [Fact]
    public void Number_JsonNull_MatchesNull()
    {
        AssertAlternatives(SingleCondition(Parse(Value("Age", FieldValue.Null))), IsNull);
    }

    // Runs assertions under de-DE, whose decimal separator is a comma, then restores both cultures. Legacy parsed
    // numbers with the current culture (FindRecordDialog.cs:418-463); the target reads them with the invariant
    // culture, which a dot-decimal runner alone cannot tell apart. Culture is per async flow, so setting it here
    // does not leak into tests running in parallel.
    private static void UnderCommaDecimalCulture(Action assertions)
    {
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo german = new("de-DE");
            CultureInfo.CurrentCulture = german;
            CultureInfo.CurrentUICulture = german;

            // Culture-sensitive parsing does change under this culture, reading "18,5" as 18.5 and "18.5" as 185,
            // so the assertions run under it are meaningful.
            Assert.Equal(18.5d, double.Parse("18,5", CultureInfo.CurrentCulture));
            Assert.Equal(185d, double.Parse("18.5", CultureInfo.CurrentCulture));

            assertions();
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    // Numbers are read with the invariant culture, and the prefix is the operator, even under a comma-decimal
    // current culture (CollectedDataProvider.cs:2046-2066).
    [Theory]
    [InlineData(">=18", SearchOperator.GreaterOrEqual, 18d)]
    [InlineData("18.5", SearchOperator.Equal, 18.5d)]
    [InlineData("-3", SearchOperator.Equal, -3d)]
    [InlineData("<>0", SearchOperator.NotEqual, 0d)]
    [InlineData("<=120", SearchOperator.LessOrEqual, 120d)]
    public void Number_PrefixIsTheOperatorOnAnInvariantNumber(string value, SearchOperator expected, double number)
    {
        UnderCommaDecimalCulture(() =>
        {
            SearchCondition condition = SingleCondition(Parse(Text("Age", value)));

            Assert.Equal(FieldType.Number, condition.FieldType);
            AssertAlternatives(condition, (expected, FieldValue.FromNumber(number)));
        });
    }

    // A comma is never the decimal separator, so the current culture's way of writing 18.5 is not a number.
    [Theory]
    [InlineData("18,5")]
    [InlineData(">=18,5")]
    public void Number_CommaDecimalUnderCommaDecimalCulture_IsAFieldError(string value)
    {
        UnderCommaDecimalCulture(() => AssertFieldError(Parse(Text("Age", value)), "Age"));
    }

    [Fact]
    public void Number_JsonNumber_IsAnEqualityMatch()
    {
        AssertAlternatives(SingleCondition(Parse(Value("Age", FieldValue.FromNumber(5)))), EqualNumber(5));
    }

    // What follows the prefix must parse as a number, so a bare prefix is rejected, and one bad alternative fails
    // the whole field.
    [Theory]
    [InlineData(">")]
    [InlineData(">=")]
    [InlineData("abc")]
    [InlineData("5 or abc")]
    [InlineData("1,000")]
    [InlineData("NaN")]
    public void Number_NonNumericOrBarePrefix_IsAFieldError(string value)
    {
        AssertFieldError(Parse(Text("Age", value)), "Age");
    }

    [Fact]
    public void Number_BooleanValue_IsAFieldError()
    {
        AssertFieldError(Parse(Value("Age", FieldValue.FromBoolean(true))), "Age");
    }

    #endregion

    #region Option

    // The legacy dialog treats an option as numeric (FindRecordDialog.cs:418), and a blank option made the legacy
    // query fail with a null dereference (CollectedDataProvider.cs:2186); the target rejects it cleanly.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Option_Blank_IsAFieldError(string value)
    {
        AssertFieldError(Parse(Text("Choice", value)), "Choice");
    }

    [Fact]
    public void Option_JsonNull_IsAFieldError()
    {
        AssertFieldError(Parse(Value("Choice", FieldValue.Null)), "Choice");
    }

    // An option is its 0-based index, compared for equality with any prefix ignored
    // (CollectedDataProvider.cs:2224-2231).
    [Theory]
    [InlineData("2", 2)]
    [InlineData(">2", 2)]
    [InlineData("<>0", 0)]
    public void Option_Index_IsAnEqualityMatchWithThePrefixIgnored(string value, int index)
    {
        SearchCondition condition = SingleCondition(Parse(Text("Choice", value)));

        Assert.Equal(FieldType.Option, condition.FieldType);
        AssertAlternatives(condition, (SearchOperator.Equal, FieldValue.FromIndex(index)));
    }

    [Fact]
    public void Option_Alternatives_AreIndexes()
    {
        AssertAlternatives(
            SingleCondition(Parse(Text("Choice", "0 or 1"))),
            (SearchOperator.Equal, FieldValue.FromIndex(0)),
            (SearchOperator.Equal, FieldValue.FromIndex(1)));
    }

    [Fact]
    public void Option_JsonInteger_IsAnIndex()
    {
        AssertAlternatives(
            SingleCondition(Parse(Value("Choice", FieldValue.FromNumber(2)))),
            (SearchOperator.Equal, FieldValue.FromIndex(2)));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("2.5")]
    [InlineData("1 or abc")]
    public void Option_NotAnIndex_IsAFieldError(string value)
    {
        AssertFieldError(Parse(Text("Choice", value)), "Choice");
    }

    [Theory]
    [InlineData(2.5d)]
    [InlineData(-1d)]
    public void Option_JsonNumberThatIsNotAnIndex_IsAFieldError(double number)
    {
        AssertFieldError(Parse(Value("Choice", FieldValue.FromNumber(number))), "Choice");
    }

    #endregion

    #region Date

    // Target departure: only YYYY-MM-DD within the datetime range is accepted, where legacy parsed with the current
    // culture (FindRecordDialog.cs:464-510; CollectedDataProvider.cs:2135-2169).
    [Fact]
    public void Date_Blank_MatchesNull()
    {
        AssertAlternatives(SingleCondition(Parse(Text("DOB", ""))), IsNull);
        AssertAlternatives(SingleCondition(Parse(Value("DOB", FieldValue.Null))), IsNull);
    }

    [Theory]
    [InlineData(">=2011-01-15", SearchOperator.GreaterOrEqual, 2011, 1, 15)]
    [InlineData("2011-01-15", SearchOperator.Equal, 2011, 1, 15)]
    [InlineData("<2011-01-15", SearchOperator.Less, 2011, 1, 15)]
    [InlineData("9999-12-31", SearchOperator.Equal, 9999, 12, 31)]
    [InlineData("1753-01-01", SearchOperator.Equal, 1753, 1, 1)]
    public void Date_IsoDate_PrefixIsTheOperator(string value, SearchOperator expected, int year, int month, int day)
    {
        SearchCondition condition = SingleCondition(Parse(Text("DOB", value)));

        Assert.Equal(FieldType.Date, condition.FieldType);
        AssertAlternatives(condition, (expected, FieldValue.FromDate(new DateOnly(year, month, day))));
    }

    [Theory]
    [InlineData("01/15/2011")]
    [InlineData("1752-12-31")]
    [InlineData("2011-02-30")]
    [InlineData("2011-1-5")]
    [InlineData("2011-01-15T00:00:00")]
    [InlineData(">")]
    [InlineData("2011-01-15 or abc")]
    public void Date_NotAnIsoDateInTheDatetimeRange_IsAFieldError(string value)
    {
        AssertFieldError(Parse(Text("DOB", value)), "DOB");
    }

    [Fact]
    public void Date_NonTextValue_IsAFieldError()
    {
        AssertFieldError(Parse(Value("DOB", FieldValue.FromNumber(20110115))), "DOB");
        AssertFieldError(Parse(Value("DOB", FieldValue.FromBoolean(true))), "DOB");
    }

    [Fact]
    public void Date_Alternatives_AreDates()
    {
        AssertAlternatives(
            SingleCondition(Parse(Text("DOB", "2011-01-15 or 2011-02-01"))),
            EqualDate(2011, 1, 15),
            EqualDate(2011, 2, 1));
    }

    #endregion

    #region Checkbox

    // Each alternative, prefix included, must equal the default Yes or No text or (+) or (-), case-sensitively
    // (FindRecordDialog.cs:511-554; CollectedDataProvider.cs:2022-2045).
    [Theory]
    [InlineData("Yes", true)]
    [InlineData("(+)", true)]
    [InlineData("No", false)]
    [InlineData("(-)", false)]
    public void Checkbox_LegacyRepresentation_MatchesItsState(string value, bool expected)
    {
        SearchCondition condition = SingleCondition(Parse(Text("Asian", value)));

        Assert.Equal(FieldType.Checkbox, condition.FieldType);
        AssertAlternatives(condition, EqualBoolean(expected));
    }

    // Target extension for API callers: a JSON boolean is accepted beside the legacy strings.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Checkbox_JsonBoolean_MatchesItsState(bool value)
    {
        AssertAlternatives(SingleCondition(Parse(Value("Asian", FieldValue.FromBoolean(value)))), EqualBoolean(value));
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("YES")]
    [InlineData("no")]
    [InlineData("true")]
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(">Yes")]
    [InlineData("Yes or maybe")]
    public void Checkbox_AnyOtherText_IsAFieldError(string value)
    {
        AssertFieldError(Parse(Text("Asian", value)), "Asian");
    }

    [Fact]
    public void Checkbox_NumberOrNull_IsAFieldError()
    {
        AssertFieldError(Parse(Value("Asian", FieldValue.FromNumber(1))), "Asian");
        AssertFieldError(Parse(Value("Asian", FieldValue.Null)), "Asian");
    }

    [Fact]
    public void Checkbox_Alternatives_KeepTheirOrder()
    {
        AssertAlternatives(SingleCondition(Parse(Text("Asian", "Yes or No"))), EqualBoolean(true), EqualBoolean(false));
    }

    #endregion

    #region System fields

    // GlobalRecordId takes the dialog's generic branch: the prefix is the operator and there is no wildcard path
    // (FindRecordDialog.cs:116-120; CollectedDataProvider.cs:2068-2082). It is a base-table condition.
    [Theory]
    [InlineData("abc", SearchOperator.Equal, "abc")]
    [InlineData(">abc", SearchOperator.Greater, "abc")]
    [InlineData("<>abc", SearchOperator.NotEqual, "abc")]
    [InlineData("ab*", SearchOperator.Equal, "ab*")]
    public void GlobalRecordId_PrefixIsTheOperatorOnTheBaseColumn(string value, SearchOperator expected, string text)
    {
        SearchCondition condition = SingleCondition(Parse(Text(SearchCriteriaParser.GlobalRecordIdFieldName, value)));

        Assert.Equal(SearchCriteriaParser.GlobalRecordIdFieldName, condition.FieldName);
        Assert.Equal(SearchTarget.BaseGlobalRecordId, condition.Target);
        Assert.Null(condition.FieldType);
        AssertAlternatives(condition, (expected, FieldValue.FromText(text)));
    }

    [Fact]
    public void GlobalRecordId_BarePrefix_MatchesNull()
    {
        AssertAlternatives(SingleCondition(Parse(Text(SearchCriteriaParser.GlobalRecordIdFieldName, ">"))), IsNull);
    }

    [Fact]
    public void GlobalRecordId_Blank_IsAFieldError()
    {
        AssertFieldError(
            Parse(Text(SearchCriteriaParser.GlobalRecordIdFieldName, "")),
            SearchCriteriaParser.GlobalRecordIdFieldName);
    }

    // RecStatus is an integral status compared for equality with any prefix ignored
    // (CollectedDataProvider.cs:2224-2231). It narrows the caller's selected set on the base table.
    [Theory]
    [InlineData("1", 1)]
    [InlineData(">1", 1)]
    [InlineData("0", 0)]
    public void RecStatus_IntegralStatus_IsAnEqualityMatchWithThePrefixIgnored(string value, int status)
    {
        SearchCondition condition = SingleCondition(Parse(Text(SearchCriteriaParser.RecStatusFieldName, value)));

        Assert.Equal(SearchCriteriaParser.RecStatusFieldName, condition.FieldName);
        Assert.Equal(SearchTarget.BaseRecStatus, condition.Target);
        Assert.Null(condition.FieldType);
        AssertAlternatives(condition, EqualNumber(status));
    }

    [Fact]
    public void RecStatus_JsonIntegerAndAlternatives_AreIntegralNumbers()
    {
        AssertAlternatives(
            SingleCondition(Parse(Value(SearchCriteriaParser.RecStatusFieldName, FieldValue.FromNumber(1)))),
            EqualNumber(1));

        AssertAlternatives(
            SingleCondition(Parse(Text(SearchCriteriaParser.RecStatusFieldName, "0 or 1"))),
            EqualNumber(0),
            EqualNumber(1));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("1.5")]
    public void RecStatus_NotAnInteger_IsAFieldError(string value)
    {
        AssertFieldError(
            Parse(Text(SearchCriteriaParser.RecStatusFieldName, value)),
            SearchCriteriaParser.RecStatusFieldName);
    }

    [Fact]
    public void RecStatus_JsonFraction_IsAFieldError()
    {
        AssertFieldError(
            Parse(Value(SearchCriteriaParser.RecStatusFieldName, FieldValue.FromNumber(1.5))),
            SearchCriteriaParser.RecStatusFieldName);
    }

    #endregion

    #region Field names

    [Theory]
    [InlineData("lastname", "LastName")]
    [InlineData("LASTNAME", "LastName")]
    [InlineData("globalrecordid", SearchCriteriaParser.GlobalRecordIdFieldName)]
    [InlineData("RECSTATUS", SearchCriteriaParser.RecStatusFieldName)]
    public void FieldName_MatchesCaseInsensitivelyAndIsReportedCanonically(string submitted, string canonical)
    {
        Assert.Equal(canonical, SingleCondition(Parse(Text(submitted, "1"))).FieldName);
    }

    // Labels, groups, unsupported fields (Yes/No included), UniqueKey and unknown names are not searchable. The
    // error is keyed by the criteria parameter, not by the submitted name: a name that resolves to no searchable
    // field has no canonical spelling to key a field error by, and the raw name is never echoed.
    [Theory]
    [InlineData("ILL")]
    [InlineData("Heading")]
    [InlineData("Demographics")]
    [InlineData("UniqueKey")]
    [InlineData("Bogus")]
    public void FieldName_NotSearchable_IsACriteriaError(string submitted)
    {
        SearchParseResult result = Parse(Text(submitted, "1"));

        AssertFieldError(result, SearchCriteriaParser.CriteriaKey);
        Assert.DoesNotContain(submitted, result.FieldErrors.Keys);
        Assert.All(AllMessages(result), message => Assert.DoesNotContain(submitted, message, StringComparison.Ordinal));
    }

    // The wire reader can yield a null entry or a null name, whatever the annotations say.
    [Fact]
    public void FieldName_NullEntryOrNullName_IsACriteriaError()
    {
        AssertFieldError(SearchCriteriaParser.Parse(Fields, [null!]), SearchCriteriaParser.CriteriaKey);
        AssertFieldError(Parse(new SearchCriterion(null!, FieldValue.FromText("Smith"))), SearchCriteriaParser.CriteriaKey);
    }

    #endregion

    #region Criteria-level rules

    // The legacy dialog cannot select a field twice, so a field named twice, in any letter case, is a 400.
    [Theory]
    [InlineData("LastName", "LastName")]
    [InlineData("LastName", "lastname")]
    [InlineData(SearchCriteriaParser.GlobalRecordIdFieldName, "globalrecordid")]
    public void Criteria_FieldNamedTwice_IsACriteriaError(string first, string second)
    {
        AssertFieldError(Parse(Text(first, "a"), Text(second, "b")), SearchCriteriaParser.CriteriaKey);
    }

    [Fact]
    public void Criteria_FieldLimit_IsSix()
    {
        Assert.Equal(6, RecordQueryRules.MaxSearchFields);
    }

    // The legacy dialog selects at most six fields (FindRecordDialog.cs:293-317).
    [Fact]
    public void Criteria_SevenDistinctFields_IsACriteriaError()
    {
        SearchCriterion[] criteria =
        [
            Text("LastName", "Smith"),
            Text("FirstName", "Ann"),
            Text("City", "Atlanta"),
            Text("State", "GA"),
            Text("PhoneNumber", "555"),
            Text("Age", "18"),
            Text("DOB", "2011-01-15"),
        ];
        Assert.Equal(RecordQueryRules.MaxSearchFields + 1, criteria.Length);

        AssertFieldError(Parse(criteria), SearchCriteriaParser.CriteriaKey);
    }

    // System fields count toward the limit like data fields.
    [Fact]
    public void Criteria_SixDistinctFieldsIncludingASystemField_Succeed()
    {
        SearchCriterion[] criteria =
        [
            Text("LastName", "Smith"),
            Text("FirstName", "Ann"),
            Text("City", "Atlanta"),
            Text("State", "GA"),
            Text("Age", "18"),
            Text(SearchCriteriaParser.GlobalRecordIdFieldName, "abc"),
        ];
        Assert.Equal(RecordQueryRules.MaxSearchFields, criteria.Length);

        SearchParseResult result = Parse(criteria);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            new[] { "LastName", "FirstName", "City", "State", "Age", SearchCriteriaParser.GlobalRecordIdFieldName },
            result.Conditions.Select(condition => condition.FieldName).ToArray());
    }

    // The limit is reported once, and criteria past it are not parsed, so their bad values add no error.
    [Fact]
    public void Criteria_LimitIsReportedOnceAndExcessCriteriaAreNotParsed()
    {
        SearchParseResult result = Parse(
            Text("LastName", "Smith"),
            Text("FirstName", "Ann"),
            Text("City", "Atlanta"),
            Text("State", "GA"),
            Text("PhoneNumber", "555"),
            Text("Notes", "abc"),
            Text("Age", "abc"),
            Text("DOB", "abc"));

        AssertFieldError(result, SearchCriteriaParser.CriteriaKey);
        Assert.Single(result.FieldErrors);
        Assert.Single(result.FieldErrors[SearchCriteriaParser.CriteriaKey]);
    }

    // Every error is found in one pass, each under its own key.
    [Fact]
    public void Criteria_EveryErrorIsReportedInOnePass()
    {
        SearchParseResult result = Parse(Text("Bogus", "x"), Text("LastName", ""), Text("Age", "abc"));

        AssertFieldError(result, SearchCriteriaParser.CriteriaKey);
        AssertFieldError(result, "LastName");
        AssertFieldError(result, "Age");
        Assert.Equal(3, result.FieldErrors.Count);
    }

    #endregion

    #region Request level

    [Fact]
    public void Request_NullOrEmptyCriteria_IsARequestLevelError()
    {
        foreach (SearchParseResult result in new[]
                 {
                     SearchCriteriaParser.Parse(Fields, null),
                     SearchCriteriaParser.Parse(Fields, Array.Empty<SearchCriterion>()),
                 })
        {
            Assert.False(result.IsSuccess);
            Assert.Equal(SearchCriteriaParser.EmptyCriteriaMessage, result.RequestError);
            Assert.Empty(result.FieldErrors);
            Assert.Empty(result.Conditions);
        }
    }

    [Fact]
    public void Request_NullDataFieldSet_IsAProgrammingError()
    {
        Assert.Throws<ArgumentNullException>(() => SearchCriteriaParser.Parse(null!, [Text("LastName", "Smith")]));
    }

    #endregion

    #region Combination

    // Conditions follow the criteria order and combine with AND; the alternatives of one field combine with OR.
    [Fact]
    public void Combination_ConditionsFollowCriteriaOrderWithAlternativesPerField()
    {
        SearchParseResult result = Parse(Text("LastName", "Smith or Jones"), Text("Age", ">18"));

        Assert.True(result.IsSuccess);
        Assert.Collection(
            result.Conditions,
            lastName =>
            {
                Assert.Equal("LastName", lastName.FieldName);
                AssertAlternatives(lastName, EqualText("Smith"), EqualText("Jones"));
            },
            age =>
            {
                Assert.Equal("Age", age.FieldName);
                AssertAlternatives(age, (SearchOperator.Greater, FieldValue.FromNumber(18)));
            });
    }

    [Fact]
    public void Combination_ReversedCriteria_ReverseTheConditions()
    {
        SearchParseResult result = Parse(Text("Age", ">18"), Text("LastName", "Smith or Jones"));

        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { "Age", "LastName" }, result.Conditions.Select(condition => condition.FieldName).ToArray());
    }

    #endregion

    #region Result immutability

    // A result is immutable: neither the arrays given to FieldFailure nor the arrays or map a read of FieldErrors
    // returns are the result's own storage, so changing them leaves every later read with the original messages, in
    // their original key and message order.
    [Fact]
    public void Result_ChangingAnInputOrAReturnedErrorArray_LeavesTheFieldErrorsUnchanged()
    {
        SearchParseResult parsed = Parse(Text("Bogus", "x"), Text("LastName", ""), Text("Age", "abc"));
        string[] parsedRead = parsed.FieldErrors["Age"];
        string ageMessage = Assert.Single(parsedRead);

        parsedRead[0] = Sentinel;

        string[] parsedReread = parsed.FieldErrors["Age"];
        Assert.NotSame(parsedRead, parsedReread);
        Assert.Equal(new[] { ageMessage }, parsedReread);
        Assert.Equal(new[] { SearchCriteriaParser.CriteriaKey, "LastName", "Age" }, parsed.FieldErrors.Keys);

        string[] input = ["First message.", "Second message."];
        SearchParseResult built = SearchParseResult.FieldFailure(
            new Dictionary<string, string[]>(StringComparer.Ordinal) { ["LastName"] = input });

        input[0] = Sentinel;
        string[] firstRead = built.FieldErrors["LastName"];
        firstRead[1] = Sentinel;
        var writable = Assert.IsAssignableFrom<IDictionary<string, string[]>>(built.FieldErrors);
        Assert.Throws<NotSupportedException>(() => writable.Remove("LastName"));

        string[] secondRead = built.FieldErrors["LastName"];
        Assert.NotSame(firstRead, secondRead);
        Assert.Equal(new[] { "First message.", "Second message." }, secondRead);
        Assert.False(built.IsSuccess);
        Assert.All(AllMessages(built), message => Assert.DoesNotContain(Sentinel, message, StringComparison.Ordinal));
    }

    #endregion

    #region Redaction

    // Error messages may name the canonical field but never carry a submitted value or an unresolved name.
    [Theory]
    [InlineData("Age", Sentinel)]
    [InlineData("Age", "5 or " + Sentinel)]
    [InlineData("DOB", Sentinel)]
    [InlineData("Asian", Sentinel)]
    [InlineData("Choice", Sentinel)]
    [InlineData(SearchCriteriaParser.RecStatusFieldName, Sentinel)]
    [InlineData(Sentinel, "Smith")]
    public void Redaction_ErrorsNeverCarryTheSubmittedValue(string field, string value)
    {
        SearchParseResult result = Parse(Text(field, value));

        Assert.False(result.IsSuccess);
        Assert.NotEmpty(AllMessages(result));
        Assert.All(AllMessages(result), message => Assert.DoesNotContain(Sentinel, message, StringComparison.Ordinal));
        Assert.All(result.FieldErrors.Keys, key => Assert.DoesNotContain(Sentinel, key, StringComparison.Ordinal));
    }

    #endregion

    #region Robustness

    // Bad input is reported in the result, never thrown.
    [Theory]
    [InlineData("LastName", "")]
    [InlineData("PhoneNumber", "   ")]
    [InlineData("Age", ">")]
    [InlineData("Age", "5 or abc")]
    [InlineData("Choice", "")]
    [InlineData("Choice", "-1")]
    [InlineData("DOB", "1752-12-31")]
    [InlineData("DOB", "01/15/2011")]
    [InlineData("Asian", "yes")]
    [InlineData(SearchCriteriaParser.RecStatusFieldName, "abc")]
    [InlineData(SearchCriteriaParser.GlobalRecordIdFieldName, "")]
    [InlineData("ILL", "1")]
    [InlineData("", "1")]
    public void Robustness_BadInput_IsReportedNotThrown(string field, string value)
    {
        SearchParseResult? result = null;

        Exception? exception = Record.Exception(() => result = Parse(Text(field, value)));

        Assert.Null(exception);
        Assert.NotNull(result);
        Assert.False(result.IsSuccess);
    }

    // Every value kind on every searchable field parses to a result, whatever the wire reader produced.
    [Fact]
    public void Robustness_EveryValueKindOnEverySearchableField_IsReportedNotThrown()
    {
        FieldValue[] values =
        [
            FieldValue.Null,
            FieldValue.FromText(""),
            FieldValue.FromText("x"),
            FieldValue.FromNumber(-1),
            FieldValue.FromNumber(1e300),
            FieldValue.FromBoolean(true),
            FieldValue.FromDate(new DateOnly(2011, 1, 15)),
            FieldValue.FromIndex(1),
        ];
        string[] names =
        [
            .. ExpectedDataFieldNames,
            SearchCriteriaParser.GlobalRecordIdFieldName,
            SearchCriteriaParser.RecStatusFieldName,
        ];

        foreach (var name in names)
        {
            foreach (var value in values)
            {
                SearchParseResult? result = null;

                Exception? exception = Record.Exception(() => result = Parse(Value(name, value)));

                Assert.Null(exception);
                Assert.NotNull(result);
                Assert.Null(result.RequestError);
                Assert.True(result.IsSuccess || result.FieldErrors.ContainsKey(name));
            }
        }
    }

    #endregion
}
