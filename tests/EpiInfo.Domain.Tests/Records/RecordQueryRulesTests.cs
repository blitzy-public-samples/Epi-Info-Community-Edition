using System.Globalization;
using EpiInfo.Domain.Forms;
using EpiInfo.Domain.Metadata;
using EpiInfo.Domain.Records;
using EpiInfo.Domain.Values;

namespace EpiInfo.Domain.Tests.Records;

/// <summary>
/// Unit tests of <see cref="RecordQueryRules"/>, the one owner of the record-set rules: the selected-set predicate,
/// the intrinsic coordinates of a Record DTO, the paging, position and search bounds, the 64-bit offsets, the grid
/// columns and the grid filter's contains pattern. The API integration tests <c>NavigationTests</c> and
/// <c>ListAndSearchTests</c> prove the SQL those rules drive; these tests prove the rules themselves.
/// </summary>
/// <remarks>
/// No database, environment variable or file is used. The seeded forms are rebuilt in code from
/// <c>testdata/sql/03_forms.sql</c> (field ids, names, types, pages, <c>TabIndex</c>) and
/// <c>testdata/sql/05_data_tables.sql</c> (column types), limited to the rows up to the first fields past the sixth
/// grid column. <c>testdata/forms.json</c> is not used, because it holds no <c>TabIndex</c> and lists fields in
/// another order. The helpers only build metadata rows: the grid rule, the data-field set and the type mapping are
/// always those of <see cref="RecordQueryRules"/>, <see cref="FormDefinitionFactory"/> and
/// <see cref="FieldTypeMapper"/>.
/// </remarks>
public sealed class RecordQueryRulesTests
{
    // metaFieldTypes rows as seeded by testdata/sql/02_reference_data.sql.
    private static readonly SeededType TextType = new(1, "Text");
    private static readonly SeededType LabelType = new(2, "Label/Title");
    private static readonly SeededType MultilineType = new(4, "Multiline");
    private static readonly SeededType NumberType = new(5, "Number");
    private static readonly SeededType PhoneNumberType = new(6, "Phone Number");
    private static readonly SeededType DateType = new(7, "Date");
    private static readonly SeededType CheckboxType = new(10, "Checkbox");
    private static readonly SeededType YesNoType = new(11, "Yes/No");
    private static readonly SeededType OptionType = new(12, "Option");
    private static readonly SeededType CommandButtonType = new(13, "Command Button");
    private static readonly SeededType LegalValuesType = new(17, "Legal Values");
    private static readonly SeededType CommentLegalType = new(19, "Comment Legal");
    private static readonly SeededType GroupType = new(21, "Group");
    private static readonly SeededType RecStatusType = new(22, "RecStatus");
    private static readonly SeededType UniqueKeyType = new(23, "UniqueKey");
    private static readonly SeededType GlobalRecordIdType = new(26, "GlobalRecordId");

    // The grid columns the contract states for the seeded forms.
    private static readonly IReadOnlyList<string> CaseReportGrid =
        ["PhoneNumber", "LastName", "FirstName", "Address", "City", "State"];

    private static readonly IReadOnlyList<string> FoodHistoryGrid =
        ["CaseID", "DateofInterview", "FirstName", "LastName", "Sex", "DOB"];

    private static readonly IReadOnlyList<string> IneligibleBeforeEligibleGrid = ["T", "N", "D", "L"];

    private static readonly IReadOnlyList<string> FewerThanSixGrid = ["First", "Second", "Third"];

    private static readonly IReadOnlyList<string> PagePositionGrid = ["A1", "A2", "A3", "B1", "B2", "B3"];

    private static readonly IReadOnlyList<string> TiedPagePositionGrid = ["H1", "H2", "H3", "H4", "L5", "L6"];

    [Fact]
    public void Constants_HoldTheContractValues()
    {
        Assert.Equal(1, RecordQueryRules.ActiveRecStatus);
        Assert.Equal(0, RecordQueryRules.DeletedRecStatus);
        Assert.Equal(1, RecordQueryRules.DefaultPage);
        Assert.Equal(1, RecordQueryRules.MinPage);
        Assert.Equal(50, RecordQueryRules.DefaultPageSize);
        Assert.Equal(1, RecordQueryRules.MinPageSize);
        Assert.Equal(200, RecordQueryRules.MaxPageSize);
        Assert.Equal(200, RecordQueryRules.SearchCap);
        Assert.Equal(6, RecordQueryRules.MaxSearchFields);
        Assert.Equal(4000, RecordQueryRules.MaxLikePatternLength);
        Assert.Equal('\\', RecordQueryRules.ContainsPatternEscape);
        Assert.Equal(6, RecordQueryRules.GridColumnCount);
        Assert.Equal("page", RecordQueryRules.PageParameter);
        Assert.Equal("pageSize", RecordQueryRules.PageSizeParameter);
    }

    [Fact]
    public void RequiredRecStatus_ActiveSetRequiresExactlyOne_AndAllRowsRequireNoStatus()
    {
        Assert.Equal(RecordQueryRules.ActiveRecStatus, RecordQueryRules.RequiredRecStatus(includeDeleted: false));
        Assert.Null(RecordQueryRules.RequiredRecStatus(includeDeleted: true));
    }

    /// <summary>
    /// One predicate, <c>RecStatus = 1</c>, selects the active set; a status other than 0 and 1 is outside it, unlike
    /// legacy Find's <c>RecStatus &gt; 0</c>. The in-memory predicate also agrees with the status the data layer is
    /// given for its SQL.
    /// </summary>
    [Theory]
    [InlineData(1, false, true)]
    [InlineData(0, false, false)]
    [InlineData(2, false, false)]
    [InlineData(-1, false, false)]
    [InlineData(0, true, true)]
    [InlineData(1, true, true)]
    [InlineData(2, true, true)]
    public void IsInSelectedSet_AdmitsExactlyTheActiveStatusUnlessDeletedAreIncluded(
        int recStatus,
        bool includeDeleted,
        bool expected)
    {
        bool selected = RecordQueryRules.IsInSelectedSet(recStatus, includeDeleted);

        Assert.Equal(expected, selected);
        int? required = RecordQueryRules.RequiredRecStatus(includeDeleted);
        Assert.Equal(required is null || required == recStatus, selected);
    }

    /// <summary>
    /// A Record DTO's own position and count are over the active set for an active record and over every row
    /// otherwise, whatever the caller's toggle, and that set always contains the record itself.
    /// </summary>
    [Theory]
    [InlineData(1, false)]
    [InlineData(0, true)]
    [InlineData(2, true)]
    public void IntrinsicIncludesDeleted_FollowsTheRecordsOwnStatus(int recStatus, bool expected)
    {
        bool includeDeleted = RecordQueryRules.IntrinsicIncludesDeleted(recStatus);

        Assert.Equal(expected, includeDeleted);
        Assert.True(RecordQueryRules.IsInSelectedSet(recStatus, includeDeleted));
    }

    [Theory]
    [InlineData(int.MinValue, false)]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(int.MaxValue, true)]
    public void IsValidPage_AdmitsEveryPageFromOne(int page, bool expected)
    {
        Assert.Equal(expected, RecordQueryRules.IsValidPage(page));
    }

    [Theory]
    [InlineData(int.MinValue, false)]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(50, true)]
    [InlineData(200, true)]
    [InlineData(201, false)]
    [InlineData(int.MaxValue, false)]
    public void IsValidPageSize_AdmitsOneToTwoHundred(int pageSize, bool expected)
    {
        Assert.Equal(expected, RecordQueryRules.IsValidPageSize(pageSize));
    }

    [Theory]
    [InlineData(1, 50)]
    [InlineData(1, 1)]
    [InlineData(1, 200)]
    [InlineData(int.MaxValue, 200)]
    public void ValidatePaging_AdmittedValues_ReportNoError(int page, int pageSize)
    {
        Assert.Empty(RecordQueryRules.ValidatePaging(page, pageSize));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void ValidatePaging_PageBelowOne_ReportsOnlyPage(int page)
    {
        PagingError error = Assert.Single(RecordQueryRules.ValidatePaging(page, RecordQueryRules.DefaultPageSize));

        Assert.Equal(RecordQueryRules.PageParameter, error.Parameter);
        Assert.Equal(RecordQueryRules.PageOutOfRangeMessage, error.Message);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(201)]
    [InlineData(int.MaxValue)]
    public void ValidatePaging_PageSizeOutOfRange_ReportsOnlyPageSize(int pageSize)
    {
        PagingError error = Assert.Single(RecordQueryRules.ValidatePaging(RecordQueryRules.DefaultPage, pageSize));

        Assert.Equal(RecordQueryRules.PageSizeParameter, error.Parameter);
        Assert.Equal(RecordQueryRules.PageSizeOutOfRangeMessage, error.Message);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    /// <summary>The rule documents its order: <c>page</c> before <c>pageSize</c>.</summary>
    [Fact]
    public void ValidatePaging_BothOutOfRange_ReportsPageThenPageSize()
    {
        IReadOnlyList<PagingError> errors = RecordQueryRules.ValidatePaging(0, 0);

        Assert.Collection(
            errors,
            error =>
            {
                Assert.Equal(RecordQueryRules.PageParameter, error.Parameter);
                Assert.Equal(RecordQueryRules.PageOutOfRangeMessage, error.Message);
            },
            error =>
            {
                Assert.Equal(RecordQueryRules.PageSizeParameter, error.Parameter);
                Assert.Equal(RecordQueryRules.PageSizeOutOfRangeMessage, error.Message);
            });
        Assert.All(errors, error => Assert.False(string.IsNullOrWhiteSpace(error.Message)));
    }

    /// <summary>
    /// The rule checks only the lower bound. A position above the selected set's <c>count</c>, every position on an
    /// empty set included, is the 404 the store and the record service decide against that count, which
    /// <c>NavigationTests</c> pins.
    /// </summary>
    [Theory]
    [InlineData(int.MinValue, false)]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(int.MaxValue, true)]
    public void IsValidPosition_ChecksTheLowerBoundOnly(int position, bool expected)
    {
        Assert.Equal(expected, RecordQueryRules.IsValidPosition(position));
    }

    [Theory]
    [InlineData(1, 50, 0L)]
    [InlineData(2, 50, 50L)]
    [InlineData(3, 200, 400L)]
    [InlineData(1, 1, 0L)]
    [InlineData(10, 1, 9L)]
    public void ComputeOffset_IsPageMinusOneTimesPageSize(int page, int pageSize, long expected)
    {
        Assert.Equal(expected, RecordQueryRules.ComputeOffset(page, pageSize));
    }

    /// <summary>
    /// <c>page=2147483647</c> is admitted, so its offset must not wrap: a 32-bit product would. The <c>checked</c>
    /// block is lexical and guards only the arithmetic written inside it, not the arithmetic inside
    /// <see cref="RecordQueryRules.ComputeOffset"/>, so the proof is the exact 64-bit value.
    /// </summary>
    [Fact]
    public void ComputeOffset_LargestAdmittedPage_IsExactIn64Bits()
    {
        long expected = checked((2147483647L - 1L) * 200L);
        long offset;

        checked
        {
            offset = RecordQueryRules.ComputeOffset(int.MaxValue, RecordQueryRules.MaxPageSize);
        }

        Assert.Equal(429496729200L, expected);
        Assert.Equal(expected, offset);
        Assert.True(offset > int.MaxValue);
        Assert.True(offset >= 0L);
    }

    [Theory]
    [InlineData(0, 50, "page")]
    [InlineData(-1, 50, "page")]
    [InlineData(1, 0, "pageSize")]
    [InlineData(1, 201, "pageSize")]
    public void ComputeOffset_OutsideThePagingBounds_Throws(int page, int pageSize, string parameter)
    {
        Assert.Throws<ArgumentOutOfRangeException>(parameter, () => RecordQueryRules.ComputeOffset(page, pageSize));
    }

    [Theory]
    [InlineData(1, 0L)]
    [InlineData(2, 1L)]
    [InlineData(int.MaxValue, 2147483646L)]
    public void PositionToOffset_IsPositionMinusOneIn64Bits(int position, long expected)
    {
        Assert.Equal(expected, RecordQueryRules.PositionToOffset(position));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void PositionToOffset_BelowOne_Throws(int position)
    {
        Assert.Throws<ArgumentOutOfRangeException>("position", () => RecordQueryRules.PositionToOffset(position));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(199, false)]
    [InlineData(200, false)]
    [InlineData(201, true)]
    [InlineData(5000, true)]
    public void IsTruncated_IsTotalAboveTheSearchCap(int total, bool expected)
    {
        Assert.Equal(expected, RecordQueryRules.IsTruncated(total));
    }

    /// <summary>
    /// The factory and the public constructor, the two ways to build a response, both keep the total and derive
    /// <c>truncated</c> as <c>total &gt; 200</c>.
    /// </summary>
    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(200, 200, false)]
    [InlineData(200, 201, true)]
    [InlineData(200, 5000, true)]
    public void SearchResponse_KeepsTheTotalAndDerivesTruncated(int itemCount, int total, bool truncated)
    {
        IReadOnlyList<SearchResult> items = Results(itemCount);

        SearchResponse[] responses = [SearchResponse.Create(items, total), new SearchResponse(items, total)];

        Assert.All(responses, response =>
        {
            Assert.Equal(itemCount, response.Items.Count);
            Assert.Equal(total, response.Total);
            Assert.Equal(truncated, response.Truncated);
        });
    }

    /// <summary>
    /// More items than the cap, more items than the total counts, and a negative total are each rejected by the
    /// factory and by the public constructor alike.
    /// </summary>
    [Theory]
    [InlineData(RecordQueryRules.SearchCap + 1, 5000, "items")]
    [InlineData(3, 2, "items")]
    [InlineData(0, -1, "total")]
    public void SearchResponse_ItemsOrTotalOutsideTheLimits_Throws(int itemCount, int total, string parameter)
    {
        IReadOnlyList<SearchResult> items = Results(itemCount);

        Assert.Throws<ArgumentOutOfRangeException>(parameter, () => SearchResponse.Create(items, total));
        Assert.Throws<ArgumentOutOfRangeException>(parameter, () => new SearchResponse(items, total));
    }

    /// <summary>
    /// A list whose count is already outside the limits is rejected from that count alone, before any item is read,
    /// so it is never copied: even a list reporting <see cref="int.MaxValue"/> items gets this exception rather than
    /// exhausting memory.
    /// </summary>
    [Theory]
    [InlineData(RecordQueryRules.SearchCap + 1, 5000)]
    [InlineData(int.MaxValue, 5000)]
    [InlineData(3, 2)]
    public void SearchResponse_ItemCountOutsideTheLimits_ThrowsBeforeReadingAnyItem(int itemCount, int total)
    {
        CountingResults items = new(itemCount);

        Assert.Throws<ArgumentOutOfRangeException>("items", () => SearchResponse.Create(items, total));
        Assert.Throws<ArgumentOutOfRangeException>("items", () => new SearchResponse(items, total));
        Assert.Equal(0, items.Reads);
    }

    [Fact]
    public void SearchResponse_NullItems_Throws()
    {
        Assert.Throws<ArgumentNullException>("items", () => SearchResponse.Create(null!, 0));
        Assert.Throws<ArgumentNullException>("items", () => new SearchResponse(null!, 0));
    }

    /// <summary>
    /// No member has a setter, so neither an initializer nor a <c>with</c> expression can assign <c>total</c> or
    /// <c>truncated</c>, and a <c>with</c> copy keeps the original's items, total and derived <c>truncated</c>.
    /// </summary>
    [Fact]
    public void SearchResponseWithCopy_KeepsTheTotalAndTruncatedConsistent()
    {
        SearchResponse response = new(Results(RecordQueryRules.SearchCap), 201);

        SearchResponse copy = response with { };

        Assert.All(typeof(SearchResponse).GetProperties(), property => Assert.Null(property.SetMethod));
        Assert.Equal(response, copy);
        Assert.Same(response.Items, copy.Items);
        Assert.Equal(201, copy.Total);
        Assert.True(copy.Truncated);
    }

    /// <summary>
    /// The response keeps a read-only copy of the items, so growing the caller's list afterwards cannot push it past
    /// the cap or past its total.
    /// </summary>
    [Fact]
    public void SearchResponse_KeepsAReadOnlyCopyOfTheItems()
    {
        List<SearchResult> items = [.. Results(RecordQueryRules.SearchCap)];
        SearchResponse response = new(items, RecordQueryRules.SearchCap);

        items.AddRange(Results(5));

        Assert.Equal(RecordQueryRules.SearchCap, response.Items.Count);
        Assert.True(Assert.IsAssignableFrom<ICollection<SearchResult>>(response.Items).IsReadOnly);
    }

    /// <summary>
    /// The grid filter's contains pattern escapes <c>\</c>, <c>%</c>, <c>_</c> and <c>[</c> with the declared escape
    /// character and wraps the result in <c>%</c>; every other character, spaces included, is kept as written.
    /// </summary>
    [Theory]
    [InlineData("a\\b%c_d[e", "%a\\\\b\\%c\\_d\\[e%")]
    [InlineData(" Smith ", "% Smith %")]
    [InlineData("]^-*?'", "%]^-*?'%")]
    [InlineData("", "%%")]
    public void ToContainsPattern_EscapesTheLikeCharactersAndWrapsInPercent(string filter, string expected)
    {
        Assert.Equal(expected, RecordQueryRules.ToContainsPattern(filter));
    }

    /// <summary>
    /// SQL Server's <c>LIKE</c> pattern is at most 4,000 UTF-16 code units, so a plain filter may hold 3,998
    /// characters and a filter of escaped characters half as many; the count agrees with the pattern itself.
    /// </summary>
    [Theory]
    [InlineData("a", 3998, true)]
    [InlineData("a", 3999, false)]
    [InlineData("%", 1999, true)]
    [InlineData("%", 2000, false)]
    [InlineData("_", 1999, true)]
    [InlineData("[", 2000, false)]
    [InlineData("\\", 1999, true)]
    [InlineData("\\", 2000, false)]
    [InlineData("a%", 1332, true)]
    [InlineData("a%", 1333, false)]
    public void IsRepresentableFilter_CountsTheEscapedAndWrappedPattern(string unit, int repeat, bool expected)
    {
        var filter = string.Concat(Enumerable.Repeat(unit, repeat));

        Assert.Equal(expected, RecordQueryRules.IsRepresentableFilter(filter));
        Assert.Equal(
            expected,
            RecordQueryRules.ToContainsPattern(filter).Length <= RecordQueryRules.MaxLikePatternLength);
    }

    [Fact]
    public void FilterTooLongMessage_StatesTheLongestPlainFilter()
    {
        var longest = RecordQueryRules.MaxLikePatternLength - 2;

        Assert.Contains(
            string.Create(CultureInfo.InvariantCulture, $" {longest} "),
            RecordQueryRules.FilterTooLongMessage,
            StringComparison.Ordinal);
        Assert.True(RecordQueryRules.IsRepresentableFilter(new string('a', longest)));
        Assert.False(RecordQueryRules.IsRepresentableFilter(new string('a', longest + 1)));
    }

    [Fact]
    public void ContainsPatternMembers_NullFilter_Throw()
    {
        Assert.Throws<ArgumentNullException>("filter", () => RecordQueryRules.ToContainsPattern(null!));
        Assert.Throws<ArgumentNullException>("filter", () => RecordQueryRules.IsRepresentableFilter(null!));
    }

    [Theory]
    [InlineData(FieldType.Text, true)]
    [InlineData(FieldType.Number, true)]
    [InlineData(FieldType.Date, true)]
    [InlineData(FieldType.LegalValues, true)]
    [InlineData(FieldType.Label, false)]
    [InlineData(FieldType.Group, false)]
    [InlineData(FieldType.Multiline, false)]
    [InlineData(FieldType.Checkbox, false)]
    [InlineData(FieldType.Option, false)]
    [InlineData(FieldType.Unsupported, false)]
    public void IsGridFieldType_AdmitsTextNumberDateAndLegalValuesOnly(FieldType type, bool expected)
    {
        Assert.Equal(expected, RecordQueryRules.IsGridFieldType(type));
    }

    /// <summary>
    /// The seeded forms give the grid columns the contract states, whether their rows and pages arrive in storage
    /// order or in a shuffled order, so the order is page <c>Position</c> then <c>TabIndex</c> and never input
    /// order. On <c>CaseReport</c> the eligible <c>County</c>, <c>Zip</c>, <c>Age</c> and <c>Sex</c> follow
    /// <c>State</c>, which proves the cut at six.
    /// </summary>
    [Theory]
    [InlineData("CaseReport", false)]
    [InlineData("CaseReport", true)]
    [InlineData("FoodHistory", false)]
    [InlineData("FoodHistory", true)]
    public void GetGridColumns_SeededForm_GivesTheContractColumnsInOrder(string formName, bool shuffled)
    {
        FormMetadata form = formName == "CaseReport" ? CaseReport(shuffled) : FoodHistory(shuffled);
        IReadOnlyList<string> expected = formName == "CaseReport" ? CaseReportGrid : FoodHistoryGrid;

        IReadOnlyList<DataFieldMetadata> columns = RecordQueryRules.GetGridColumns(form);

        Assert.Equal(expected, columns.Select(column => column.Name));
        Assert.Equal(RecordQueryRules.GridColumnCount, columns.Count);
    }

    /// <summary>
    /// Data fields of a non-grid type, and fields that are not data fields at all, take no grid column even when
    /// their <c>TabIndex</c> is lower than every eligible field's.
    /// </summary>
    [Fact]
    public void GetGridColumns_IneligibleFieldsWithLowerTabIndex_AreSkipped()
    {
        FormMetadata form = IneligibleBeforeEligible();

        IReadOnlyList<DataFieldMetadata> columns = RecordQueryRules.GetGridColumns(form);

        Assert.Equal(IneligibleBeforeEligibleGrid, columns.Select(column => column.Name));

        // The checkbox, multiline and option rows are data fields, so the grid rule, not the data-field set,
        // keeps them out.
        IEnumerable<string> dataFieldNames = FormDefinitionFactory.GetDataFields(form).Select(field => field.Name);
        Assert.Contains("Ticked", dataFieldNames);
        Assert.Contains("Notes", dataFieldNames);
        Assert.Contains("Colour", dataFieldNames);
    }

    [Fact]
    public void GetGridColumns_FewerThanSixEligibleFields_ReturnsExactlyThoseInOrder()
    {
        IReadOnlyList<DataFieldMetadata> columns = RecordQueryRules.GetGridColumns(FewerThanSix());

        Assert.Equal(FewerThanSixGrid, columns.Select(column => column.Name));
    }

    [Fact]
    public void GetGridColumns_NoEligibleField_ReturnsAnEmptyList()
    {
        Assert.Empty(RecordQueryRules.GetGridColumns(NoGridField()));
    }

    /// <summary>
    /// The page at <c>Position</c> 0 fills the grid first even though its <c>PageId</c>, its fields'
    /// <c>FieldId</c>s and its fields' <c>TabIndex</c>es are all higher than the later page's, and the pages are
    /// listed later page first.
    /// </summary>
    [Fact]
    public void GetGridColumns_FieldsOfAnEarlierPagePosition_ComeFirst()
    {
        IReadOnlyList<DataFieldMetadata> columns = RecordQueryRules.GetGridColumns(PagePositionDecides());

        Assert.Equal(PagePositionGrid, columns.Select(column => column.Name));
    }

    /// <summary>
    /// When two pages share a <c>Position</c>, <c>TabIndex</c> orders their fields before <c>PageId</c> does: the
    /// page with the higher <c>PageId</c> holds the lower <c>TabIndex</c>es, so its four fields come first and the
    /// lower page's <c>L7</c> and <c>L8</c> fall past the sixth column. <c>PageId</c> order would instead have kept
    /// <c>L7</c> and <c>L8</c> and cut <c>H3</c> and <c>H4</c>.
    /// </summary>
    [Fact]
    public void GetGridColumns_PagesSharingAPosition_TabIndexDecidesBeforePageId()
    {
        IReadOnlyList<DataFieldMetadata> columns = RecordQueryRules.GetGridColumns(TabIndexBreaksPagePositionTie());

        Assert.Equal(TiedPagePositionGrid, columns.Select(column => column.Name));
    }

    /// <summary>
    /// The form overload, the data-field-set overload and the list overload given the data fields in reverse give
    /// the same columns: the list overload orders its input itself.
    /// </summary>
    [Theory]
    [InlineData("CaseReport")]
    [InlineData("FoodHistory")]
    [InlineData("IneligibleBeforeEligible")]
    [InlineData("FewerThanSix")]
    [InlineData("NoGridField")]
    [InlineData("PagePositionDecides")]
    [InlineData("TabIndexBreaksPagePositionTie")]
    public void GetGridColumns_Overloads_AgreeWithEachOther(string formName)
    {
        FormMetadata form = BuildForm(formName);
        IReadOnlyList<DataFieldMetadata> dataFields = FormDefinitionFactory.GetDataFields(form);
        IReadOnlyList<DataFieldMetadata> reversedDataFields = Enumerable.Reverse(dataFields).ToArray();

        string[] fromForm = RecordQueryRules.GetGridColumns(form).Select(column => column.Name).ToArray();
        string[] fromDataFields = RecordQueryRules.GetGridColumns(dataFields).Select(column => column.Name).ToArray();
        string[] fromReversed = RecordQueryRules.GetGridColumns(reversedDataFields)
            .Select(column => column.Name)
            .ToArray();

        Assert.Equal(fromForm, fromDataFields);
        Assert.Equal(fromForm, fromReversed);
    }

    [Fact]
    public void GetGridColumns_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>("form", () => RecordQueryRules.GetGridColumns((FormMetadata)null!));
        Assert.Throws<ArgumentNullException>(
            "dataFields",
            () => RecordQueryRules.GetGridColumns((IReadOnlyList<DataFieldMetadata>)null!));

        DataFieldMetadata first = FormDefinitionFactory.GetDataFields(CaseReport())[0];
        IReadOnlyList<DataFieldMetadata> withNullEntry = [first, null!];
        Assert.Throws<ArgumentException>("dataFields", () => RecordQueryRules.GetGridColumns(withNullEntry));
    }

    /// <summary>
    /// Keys are canonical names compared ordinally, one entry per data field, so a name differing only by letter case
    /// is another key, and only an exact repeat collides.
    /// </summary>
    [Fact]
    public void CreateValuesMap_KeepsInsertionOrder_AndComparesKeysOrdinally()
    {
        OrderedDictionary<string, FieldValue> values = RecordQueryRules.CreateValuesMap();

        values.Add("Zip", FieldValue.FromText("30333"));
        values.Add("Age", FieldValue.FromNumber(42));
        values.Add("AGE", FieldValue.FromText("forty-two"));

        Assert.Equal(["Zip", "Age", "AGE"], values.Keys);
        Assert.Equal(FieldValue.FromNumber(42), values["Age"]);
        Assert.Equal(FieldValue.FromText("forty-two"), values["AGE"]);
        Assert.False(values.ContainsKey("age"));
        Assert.Throws<ArgumentException>(() => values.Add("Zip", FieldValue.Null));
        Assert.Throws<ArgumentOutOfRangeException>(() => RecordQueryRules.CreateValuesMap(-1));
    }

    /// <summary>
    /// Two data fields on different pages whose canonical names differ only by letter case keep a key and a value
    /// each, both in a record's full values map and in its grid projection.
    /// </summary>
    [Fact]
    public void CaseVariantFields_KeepTheirOwnKeysInFullAndGridValues()
    {
        FormMetadata form = CaseVariantPages();
        DataFieldSet dataFields = FormDefinitionFactory.GetDataFields(form);
        OrderedDictionary<string, FieldValue> full = RecordQueryRules.CreateValuesMap(dataFields.Count);
        foreach (DataFieldMetadata field in dataFields)
        {
            full.Add(field.Name, field.Name == "Age" ? FieldValue.FromNumber(42) : FieldValue.FromText("forty-two"));
        }

        IReadOnlyList<DataFieldMetadata> columns = RecordQueryRules.GetGridColumns(form);
        IReadOnlyDictionary<string, FieldValue> projected = RecordQueryRules.ProjectValues(full, columns);

        Assert.Equal(["Age", "AGE"], dataFields.Select(field => field.Name));
        Assert.Equal(["Age", "AGE"], full.Keys);
        Assert.Equal(["Age", "AGE"], columns.Select(column => column.Name));
        Assert.Equal(["Age", "AGE"], projected.Keys);
        Assert.Equal(FieldValue.FromNumber(42), projected["Age"]);
        Assert.Equal(FieldValue.FromText("forty-two"), projected["AGE"]);
    }

    /// <summary>
    /// A record summary's values are exactly the grid columns, in grid order and under canonical names; a column
    /// the record does not hold is null, and a value outside the grid is dropped.
    /// </summary>
    [Fact]
    public void ProjectValues_KeepsOnlyTheGridColumnsInColumnOrder()
    {
        IReadOnlyList<DataFieldMetadata> columns = RecordQueryRules.GetGridColumns(CaseReport());
        OrderedDictionary<string, FieldValue> values = RecordQueryRules.CreateValuesMap();
        values.Add("Zip", FieldValue.FromText("30333"));
        values.Add("City", FieldValue.FromText("Atlanta"));
        values.Add("LastName", FieldValue.FromText("Smith"));
        values.Add("PhoneNumber", FieldValue.FromText("555-014-8636"));

        IReadOnlyDictionary<string, FieldValue> projected = RecordQueryRules.ProjectValues(values, columns);

        Assert.Equal(CaseReportGrid, projected.Keys);
        Assert.Equal(FieldValue.FromText("555-014-8636"), projected["PhoneNumber"]);
        Assert.Equal(FieldValue.FromText("Smith"), projected["LastName"]);
        Assert.Equal(FieldValue.FromText("Atlanta"), projected["City"]);
        Assert.Equal(FieldValue.Null, projected["FirstName"]);
        Assert.Equal(FieldValue.Null, projected["Address"]);
        Assert.Equal(FieldValue.Null, projected["State"]);
        Assert.False(projected.ContainsKey("Zip"));
    }

    private static FormMetadata BuildForm(string formName) => formName switch
    {
        "CaseReport" => CaseReport(),
        "FoodHistory" => FoodHistory(),
        "IneligibleBeforeEligible" => IneligibleBeforeEligible(),
        "FewerThanSix" => FewerThanSix(),
        "NoGridField" => NoGridField(),
        "PagePositionDecides" => PagePositionDecides(),
        "TabIndexBreaksPagePositionTie" => TabIndexBreaksPagePositionTie(),
        _ => throw new ArgumentOutOfRangeException(nameof(formName), formName, "No such test form."),
    };

    /// <summary>
    /// Seeded <c>CaseReport</c> (view 1, one page): its system rows and its page-one rows through <c>TabIndex</c> 16.
    /// </summary>
    private static FormMetadata CaseReport(bool shuffled = false)
    {
        const int page = 1;
        FieldMetadata[] fields =
        [
            .. SystemRows(firstFieldId: 1),
            Row(8, "PhoneNumber", PhoneNumberType, page, 1, Nvarchar("PhoneNumber", 20)),
            Row(6, "LastName", TextType, page, 2, Nvarchar("LastName", 4000)),
            Row(7, "FirstName", TextType, page, 3, Nvarchar("FirstName", 4000)),
            Row(9, "Address", TextType, page, 4, Nvarchar("Address", 4000)),
            Row(10, "City", TextType, page, 5, Nvarchar("City", 4000)),
            Row(4, "DottedLine", LabelType, page, 6, null),
            Row(5, "Detach", LabelType, page, 7, null),
            Row(11, "LymeDiseaseCaseReport", LabelType, page, 8, null),
            Row(12, "Underline", LabelType, page, 9, null),
            Row(65, "Alldatacontainedinthisformisfictional", LabelType, page, 10, null),
            Row(
                20,
                "DemographicInformation",
                GroupType,
                page,
                11,
                null,
                list: "Race,Ethnicity,Sex,Age,Zip,County,State"),
            Row(13, "State", CommentLegalType, page, 12, Nvarchar("State", 255), sourceTable: "codeState"),
            Row(14, "County", TextType, page, 13, Nvarchar("County", 4000)),
            Row(15, "Zip", TextType, page, 14, Nvarchar("Zip", 4000)),
            Row(16, "Age", NumberType, page, 15, Float("Age")),
            Row(17, "Sex", CommentLegalType, page, 16, Nvarchar("Sex", 255), sourceTable: "codeSex"),
        ];

        return new FormMetadata(
            1,
            "CaseReport",
            "CaseReport",
            [new PageMetadata(page, "Page 1", 0, "CaseReport1")],
            Arrange(fields, shuffled));
    }

    /// <summary>
    /// Seeded <c>FoodHistory</c> (view 2, pages 2 and 3): its system rows, page-two rows through <c>TabIndex</c> 33
    /// with the checkbox run after <c>White</c> left out, and a sample of page-three rows. Shuffled, the pages are
    /// listed later page first.
    /// </summary>
    private static FormMetadata FoodHistory(bool shuffled = false)
    {
        const int pageOne = 2;
        const int pageTwo = 3;
        FieldMetadata[] fields =
        [
            .. SystemRows(firstFieldId: 1001),
            Row(1063, "EColi", LabelType, pageOne, 1, null),
            Row(1062, "FoodHistory", LabelType, pageOne, 2, null),
            Row(1153, "USDEPARTMENTOFHEALTHHUMANSERVICES", LabelType, pageOne, 3, null),
            Row(1154, "CentersforDiseaseControlandPrevention", LabelType, pageOne, 4, null),
            Row(1152, "N_____", LabelType, pageOne, 5, null),
            Row(1004, "CaseID", NumberType, pageOne, 6, Float("CaseID")),
            Row(1005, "DateofInterview", DateType, pageOne, 7, DateTimeColumn("DateofInterview")),
            Row(1142, "Alldatacontainedhereinisfictional", LabelType, pageOne, 8, null),
            Row(1006, "DemographicInformation", GroupType, pageOne, 9, null),
            Row(1007, "FirstName", TextType, pageOne, 10, Nvarchar("FirstName", 4000)),
            Row(1008, "LastName", TextType, pageOne, 11, Nvarchar("LastName", 4000)),
            Row(
                1009,
                "Sex",
                LegalValuesType,
                pageOne,
                12,
                Nvarchar("Sex", 255),
                sourceTable: "codephvs_administrativesex_hl7_2x"),
            Row(1010, "DOB", DateType, pageOne, 13, DateTimeColumn("DOB")),
            Row(1011, "Age", NumberType, pageOne, 14, Float("Age")),
            Row(
                1012,
                "EthnicityGroup",
                LegalValuesType,
                pageOne,
                15,
                Nvarchar("EthnicityGroup", 255),
                sourceTable: "codephvs_ethnicitygroup_cdc"),
            Row(1014, "White", CheckboxType, pageOne, 17, Bit("White")),
            Row(1021, "PatientAddress", MultilineType, pageOne, 24, Ntext("PatientAddress")),
            Row(1022, "GetCoordinates", CommandButtonType, pageOne, 25, null),
            Row(1023, "State", TextType, pageOne, 26, Nvarchar("State", 4000)),
            Row(1064, "ILL", YesNoType, pageOne, 33, TinyInt("ILL")),
            Row(1113, "EColi1", LabelType, pageTwo, 1, null),
            Row(1114, "FoodHistory1", LabelType, pageTwo, 2, null),
            Row(1073, "FoodsEaten", GroupType, pageTwo, 3, null),
            Row(1140, "Freshcelery", CheckboxType, pageTwo, 4, Bit("Freshcelery")),
            Row(1130, "Grapes", CheckboxType, pageTwo, 5, Bit("Grapes")),
            Row(1115, "BuyLastTenDays", MultilineType, pageTwo, 27, Ntext("BuyLastTenDays")),
            Row(1149, "Lab", TextType, pageTwo, 32, Nvarchar("Lab", 4000)),
        ];

        PageMetadata first = new(pageOne, "Page 1", 0, "FoodHistory2");
        PageMetadata second = new(pageTwo, "Page 2", 1, "FoodHistory3");
        IReadOnlyList<PageMetadata> pages = shuffled ? [second, first] : [first, second];

        return new FormMetadata(2, "FoodHistory", "FoodHistory", pages, Arrange(fields, shuffled));
    }

    /// <summary>
    /// One page whose lowest <c>TabIndex</c>es hold fields the grid must skip: data fields of a non-grid type
    /// (checkbox, multiline, option), unsupported fields (<c>Yes/No</c> on <c>tinyint</c>, an encrypted text field,
    /// a text field on a non-Unicode <c>varchar</c> column, a command button), a label and a group. Four eligible
    /// fields follow.
    /// </summary>
    private static FormMetadata IneligibleBeforeEligible()
    {
        const int page = 7;
        FieldMetadata[] fields =
        [
            .. SystemRows(firstFieldId: 100),
            Row(103, "Ticked", CheckboxType, page, 1, Bit("Ticked")),
            Row(104, "Notes", MultilineType, page, 2, Ntext("Notes")),
            Row(105, "Colour", OptionType, page, 3, SmallInt("Colour"), list: "Red,Green"),
            Row(106, "Ill", YesNoType, page, 4, TinyInt("Ill")),
            Row(107, "Secret", TextType, page, 5, Nvarchar("Secret", 50), isEncrypted: true),
            Row(108, "Locate", CommandButtonType, page, 6, null),
            Row(109, "Heading", LabelType, page, 7, null),
            Row(110, "Section", GroupType, page, 8, null, list: "T,N,D,L"),
            Row(111, "Narrow", TextType, page, 9, FieldTypeMapper.DescribeColumn("Narrow", "varchar", 50)),
            Row(112, "T", TextType, page, 10, Nvarchar("T", 50)),
            Row(113, "N", NumberType, page, 11, Float("N")),
            Row(114, "D", DateType, page, 12, DateTimeColumn("D")),
            Row(115, "L", LegalValuesType, page, 13, Nvarchar("L", 255), sourceTable: "codeL"),
        ];

        return new FormMetadata(
            10,
            "IneligibleBeforeEligible",
            "IneligibleBeforeEligible",
            [new PageMetadata(page, "Page 1", 0, "IneligibleBeforeEligible7")],
            fields);
    }

    /// <summary>Three eligible fields, then a checkbox and a label: fewer than six grid columns.</summary>
    private static FormMetadata FewerThanSix()
    {
        const int page = 11;
        FieldMetadata[] fields =
        [
            .. SystemRows(firstFieldId: 200),
            Row(203, "First", TextType, page, 1, Nvarchar("First", 100)),
            Row(204, "Second", NumberType, page, 2, Float("Second")),
            Row(205, "Third", DateType, page, 3, DateTimeColumn("Third")),
            Row(206, "Fourth", CheckboxType, page, 4, Bit("Fourth")),
            Row(207, "Caption", LabelType, page, 5, null),
        ];

        return new FormMetadata(
            11,
            "FewerThanSix",
            "FewerThanSix",
            [new PageMetadata(page, "Page 1", 0, "FewerThanSix11")],
            fields);
    }

    /// <summary>Only labels and checkboxes: data fields exist, but none of a grid type.</summary>
    private static FormMetadata NoGridField()
    {
        const int page = 12;
        FieldMetadata[] fields =
        [
            .. SystemRows(firstFieldId: 300),
            Row(303, "Title", LabelType, page, 1, null),
            Row(304, "Fever", CheckboxType, page, 2, Bit("Fever")),
            Row(305, "Chills", CheckboxType, page, 3, Bit("Chills")),
            Row(306, "Footer", LabelType, page, 4, null),
        ];

        return new FormMetadata(
            12,
            "NoGridField",
            "NoGridField",
            [new PageMetadata(page, "Page 1", 0, "NoGridField12")],
            fields);
    }

    /// <summary>
    /// Two pages, listed later page first: page <c>B</c> (<c>PageId</c> 5, <c>Position</c> 1) holds four text
    /// fields at <c>TabIndex</c> 1 to 4 with the lower field ids, and page <c>A</c> (<c>PageId</c> 9,
    /// <c>Position</c> 0) three text fields at <c>TabIndex</c> 10, 20 and 30.
    /// </summary>
    private static FormMetadata PagePositionDecides()
    {
        const int pageA = 9;
        const int pageB = 5;
        FieldMetadata[] fields =
        [
            .. SystemRows(firstFieldId: 400),
            Row(403, "B1", TextType, pageB, 1, Nvarchar("B1", 50)),
            Row(404, "B2", TextType, pageB, 2, Nvarchar("B2", 50)),
            Row(405, "B3", TextType, pageB, 3, Nvarchar("B3", 50)),
            Row(406, "B4", TextType, pageB, 4, Nvarchar("B4", 50)),
            Row(407, "A1", TextType, pageA, 10, Nvarchar("A1", 50)),
            Row(408, "A2", TextType, pageA, 20, Nvarchar("A2", 50)),
            Row(409, "A3", TextType, pageA, 30, Nvarchar("A3", 50)),
        ];

        return new FormMetadata(
            13,
            "PagePositionDecides",
            "X",
            [new PageMetadata(pageB, "B", 1, "X5"), new PageMetadata(pageA, "A", 0, "X9")],
            fields);
    }

    /// <summary>
    /// Two pages that share <c>Position</c> 0, listed and numbered lower page first: page <c>L</c> (<c>PageId</c>
    /// 14) holds four text fields at <c>TabIndex</c> 5 to 8 with the lower field ids, and page <c>H</c>
    /// (<c>PageId</c> 15) four text fields at <c>TabIndex</c> 1 to 4. Eight fields are eligible, so the sixth
    /// column's cut depends on which key follows <c>Position</c>.
    /// </summary>
    private static FormMetadata TabIndexBreaksPagePositionTie()
    {
        const int pageL = 14;
        const int pageH = 15;
        FieldMetadata[] fields =
        [
            .. SystemRows(firstFieldId: 500),
            Row(503, "L5", TextType, pageL, 5, Nvarchar("L5", 50)),
            Row(504, "L6", TextType, pageL, 6, Nvarchar("L6", 50)),
            Row(505, "L7", TextType, pageL, 7, Nvarchar("L7", 50)),
            Row(506, "L8", TextType, pageL, 8, Nvarchar("L8", 50)),
            Row(507, "H1", TextType, pageH, 1, Nvarchar("H1", 50)),
            Row(508, "H2", TextType, pageH, 2, Nvarchar("H2", 50)),
            Row(509, "H3", TextType, pageH, 3, Nvarchar("H3", 50)),
            Row(510, "H4", TextType, pageH, 4, Nvarchar("H4", 50)),
        ];

        return new FormMetadata(
            14,
            "TabIndexBreaksPagePositionTie",
            "Y",
            [new PageMetadata(pageL, "L", 0, "Y14"), new PageMetadata(pageH, "H", 0, "Y15")],
            fields);
    }

    /// <summary>
    /// Two pages, each holding one grid-eligible data field, whose canonical names differ only by letter case:
    /// <c>Age</c>, a number on page 1, and <c>AGE</c>, a text field on page 2.
    /// </summary>
    private static FormMetadata CaseVariantPages()
    {
        const int pageOne = 20;
        const int pageTwo = 21;
        FieldMetadata[] fields =
        [
            .. SystemRows(firstFieldId: 600),
            Row(603, "Age", NumberType, pageOne, 1, Float("Age")),
            Row(604, "AGE", TextType, pageTwo, 1, Nvarchar("AGE", 50)),
        ];

        return new FormMetadata(
            15,
            "CaseVariantPages",
            "CaseVariantPages",
            [
                new PageMetadata(pageOne, "Page 1", 0, "CaseVariantPages20"),
                new PageMetadata(pageTwo, "Page 2", 1, "CaseVariantPages21"),
            ],
            fields);
    }

    /// <summary>
    /// The system rows <c>UniqueKey</c>, <c>RecStatus</c> and <c>GlobalRecordId</c>, which belong to no page and own
    /// no page-table column, with consecutive field ids as seeded.
    /// </summary>
    private static FieldMetadata[] SystemRows(int firstFieldId) =>
    [
        Row(firstFieldId, "UniqueKey", UniqueKeyType, null, null, null),
        Row(firstFieldId + 1, "RecStatus", RecStatusType, null, null, null),
        Row(firstFieldId + 2, "GlobalRecordId", GlobalRecordIdType, null, null, null),
    ];

    private static FieldMetadata Row(
        int fieldId,
        string name,
        SeededType type,
        int? pageId,
        decimal? tabIndex,
        ColumnMetadata? column,
        string? list = null,
        string? sourceTable = null,
        bool isEncrypted = false) => new()
        {
            FieldId = fieldId,
            Name = name,
            FieldTypeId = type.Id,
            FieldTypeName = type.Name,
            PageId = pageId,
            TabIndex = tabIndex,
            Column = column,
            List = list,
            SourceTableName = sourceTable,
            IsEncrypted = isEncrypted,
        };

    // Columns are described by the mapper from their catalogue values (sys.columns.max_length in bytes), so the
    // family and length rules stay the mapper's.
    private static ColumnMetadata Nvarchar(string name, int length) =>
        FieldTypeMapper.DescribeColumn(name, "nvarchar", length * 2);

    private static ColumnMetadata Ntext(string name) => FieldTypeMapper.DescribeColumn(name, "ntext", 16);

    private static ColumnMetadata Float(string name) => FieldTypeMapper.DescribeColumn(name, "float", 8);

    private static ColumnMetadata DateTimeColumn(string name) => FieldTypeMapper.DescribeColumn(name, "datetime", 8);

    private static ColumnMetadata Bit(string name) => FieldTypeMapper.DescribeColumn(name, "bit", 1);

    private static ColumnMetadata TinyInt(string name) => FieldTypeMapper.DescribeColumn(name, "tinyint", 1);

    private static ColumnMetadata SmallInt(string name) => FieldTypeMapper.DescribeColumn(name, "smallint", 2);

    /// <summary>
    /// Returns the rows as listed, or in a fixed permutation that follows no sort key: reversed, then rotated by
    /// half, so neither <c>TabIndex</c> nor page order survives (<c>LastName</c> precedes <c>PhoneNumber</c>) and the
    /// system rows sit mid-list.
    /// </summary>
    private static FieldMetadata[] Arrange(FieldMetadata[] rows, bool shuffled)
    {
        if (!shuffled)
        {
            return rows;
        }

        FieldMetadata[] reversed = Enumerable.Reverse(rows).ToArray();
        int half = reversed.Length / 2;
        return [.. reversed.Skip(half), .. reversed.Take(half)];
    }

    private static SearchResult[] Results(int count) =>
        Enumerable.Range(1, count)
            .Select(i => new SearchResult(
                $"id-{i}",
                i,
                RecordQueryRules.ActiveRecStatus,
                i,
                new Dictionary<string, FieldValue>()))
            .ToArray();

    /// <summary>
    /// A list that reports a count without holding that many items, yields one fixed result for each index it
    /// reports, and counts every item read through its indexer or its enumerator.
    /// </summary>
    private sealed class CountingResults(int count) : IReadOnlyList<SearchResult>
    {
        private static readonly SearchResult Result = Results(1)[0];

        public int Reads { get; private set; }

        public int Count => count;

        public SearchResult this[int index]
        {
            get
            {
                ArgumentOutOfRangeException.ThrowIfNegative(index);
                ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, count);
                Reads++;
                return Result;
            }
        }

        public IEnumerator<SearchResult> GetEnumerator()
        {
            for (int i = 0; i < count; i++)
            {
                Reads++;
                yield return Result;
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>A seeded <c>metaFieldTypes</c> row: its id and its name exactly as stored.</summary>
    private readonly record struct SeededType(int Id, string Name);
}
