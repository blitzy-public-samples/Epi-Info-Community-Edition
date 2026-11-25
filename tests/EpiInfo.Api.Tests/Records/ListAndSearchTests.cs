using System.Globalization;
using System.Net;
using System.Text.Json;
using EpiInfo.Api.Contracts;
using EpiInfo.Api.Endpoints;
using EpiInfo.Api.Errors;
using EpiInfo.Api.Json;
using EpiInfo.Api.Services;
using EpiInfo.Api.Tests.Infrastructure;
using EpiInfo.CheckCode;
using EpiInfo.Domain.Forms;
using EpiInfo.Domain.Metadata;
using EpiInfo.Domain.Records;
using EpiInfo.Domain.Search;
using EpiInfo.Domain.Storage;
using EpiInfo.Domain.Validation;
using EpiInfo.Domain.Values;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EpiInfo.Api.Tests.Records;

/// <summary>
/// The binding contract of the Find search body, <c>POST /api/forms/{viewId}/records/search</c> (AAP 0.3.4.1),
/// under the API's own JSON settings.
/// </summary>
/// <remarks>
/// The class needs no database: it carries no <c>Integration</c> trait and joins no database collection, so
/// <c>--filter "Category!=Integration"</c> selects it. Database-backed list and search cases belong in a separate
/// <c>Integration</c> class.
/// </remarks>
public sealed class SearchRequestBindingTests
{
    private const string TestHelperOptions = "JsonSetup.CreateSerializerOptions";

    private const string HttpJsonOptions = "Http.Json.JsonOptions after AddApiJson";

    /// <summary>The two ways the API's JSON settings are built: the test helper and the minimal-API options.</summary>
    public static TheoryData<string> SerializerOptionSources => new() { TestHelperOptions, HttpJsonOptions };

    // AAP 0.3.4.1: a criterion is {field, value}, and value is the FieldValue union, null included. An omitted value
    // must fail binding instead of reading as the blank criterion that "value": null is. Deserialisation only, so the
    // test needs no database and no DatabaseWriteLock.
    [Theory]
    [MemberData(nameof(SerializerOptionSources))]
    public void CriterionValueIsRequiredButMayBeNull(string source)
    {
        var options = CreateOptions(source);

        // Applying the settings a second time changes nothing and stacks nothing.
        var resolver = options.TypeInfoResolver;
        var converterCount = options.Converters.Count;
        JsonSetup.Apply(options);
        Assert.Same(resolver, options.TypeInfoResolver);
        Assert.Equal(converterCount, options.Converters.Count);

        Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize<SearchRequest>("""{"criteria":[{"field":"Age"}]}""", options));

        var blank = SingleCriterion("""{"criteria":[{"field":"Age","value":null}]}""", options);
        Assert.Equal("Age", blank.Field);
        Assert.Equal(FieldValueKind.Null, blank.Value.Kind);

        // Only value is required: an omitted field or criteria still binds as null and reaches its documented 400.
        var nameless = SingleCriterion("""{"criteria":[{"value":"Sm*"}]}""", options);
        Assert.Null(nameless.Field);
        Assert.Equal("Sm*", nameless.Value.AsText());
        Assert.Null(Deserialize("{}", options).Criteria);
    }

    private static JsonSerializerOptions CreateOptions(string source)
    {
        switch (source)
        {
            case TestHelperOptions:
                return JsonSetup.CreateSerializerOptions();

            case HttpJsonOptions:
                using (var provider = new ServiceCollection().AddApiJson().BuildServiceProvider())
                {
                    return provider.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(source), source, "Unknown serializer options source.");
        }
    }

    private static SearchRequest Deserialize(string json, JsonSerializerOptions options) =>
        Assert.IsType<SearchRequest>(JsonSerializer.Deserialize<SearchRequest>(json, options));

    private static SearchCriterion SingleCriterion(string json, JsonSerializerOptions options)
    {
        var criteria = Deserialize(json, options).Criteria;
        Assert.NotNull(criteria);
        return Assert.Single(criteria);
    }
}

/// <summary>
/// The SQL Server bounds of the records-list filter and of Find (AAP 0.3.4.1, 0.3.4.2, 0.3.4.4): a filter or wildcard
/// pattern over SQL Server's <c>LIKE</c> pattern limit is a field-level 400 before any store call, a request within it
/// reaches the store whatever its number of alternatives, and the in-memory store keeps the SQL builders' argument
/// contract for input over the bounds.
/// </summary>
/// <remarks>
/// The class needs no database: it carries no <c>Integration</c> trait and joins no database collection, so
/// <c>--filter "Category!=Integration"</c> selects it. It calls <see cref="RequestParameters"/> and
/// <see cref="RecordService"/> directly, the service over <see cref="FakeDataStore"/>.
/// </remarks>
public sealed class ListAndSearchBoundsTests
{
    private const string Sentinel = "SENTINEL-5c2d";

    private const string AlternativesKind = "alternatives";

    private const string PatternKind = "pattern";

    private const string RunKind = "run";

    private const string BracketKind = "bracket";

    // A q whose contains pattern fits 4,000 characters: 3,998 plain characters, 1,999 escaped ones, or any blank text,
    // which is no filter however long.
    [Theory]
    [InlineData("a", 3998)]
    [InlineData("%", 1999)]
    [InlineData(" ", 5000)]
    public void ListQuery_FilterWithinTheBound_Coerces(string unit, int repeat)
    {
        var q = string.Concat(Enumerable.Repeat(unit, repeat));

        Assert.True(RequestParameters.TryParseListQuery(q, null, null, null, out var query, out var problem));

        Assert.Null(problem);
        Assert.Equal(string.IsNullOrWhiteSpace(q) ? null : q, query.Filter);
    }

    // A longer q is a 400 on q, in the same single 400 as any other bad parameter.
    [Theory]
    [InlineData("a", 3999, null)]
    [InlineData("%", 2000, null)]
    [InlineData("a", 3999, "0")]
    public void ListQuery_FilterOverTheBound_IsA400OnQ(string unit, int repeat, string? page)
    {
        var q = string.Concat(Enumerable.Repeat(unit, repeat));

        string[]? pageOccurrences = page is null ? null : [page];

        Assert.False(
            RequestParameters.TryParseListQuery(q, null, pageOccurrences, null, out var query, out var problem));

        Assert.Null(query);
        var errors = ValidationErrors(problem);
        Assert.Equal(new[] { RecordQueryRules.FilterTooLongMessage }, errors[RequestParameters.Query]);
        string[] expectedKeys =
            page is null ? [RequestParameters.Query] : [RequestParameters.Page, RequestParameters.Query];
        Assert.Equal(expectedKeys, errors.Keys.Order(StringComparer.Ordinal).ToArray());
    }

    // Any number of alternatives reaches the store, which binds a large set packed; so do a 4,000-character pattern,
    // the longest SQL Server takes, and a longer one whose run of '*' collapses within it.
    [Theory]
    [InlineData(AlternativesKind, 5000)]
    [InlineData(PatternKind, 4000)]
    [InlineData(RunKind, 4001)]
    public async Task Search_WithinTheBounds_ReachesTheStore(string kind, int size)
    {
        var store = FakeDataStore.WithStandardForms();

        var result = await CreateService(store).SearchAsync(
            FakeForms.TwoPageViewId, LastNameSearch(kind, size), CancellationToken.None);

        var ok = Assert.IsType<Ok<SearchResponse>>(result);
        Assert.NotNull(ok.Value);
        Assert.Contains(store.Calls, call => call.Method == nameof(IDataStore.SearchAsync));
    }

    // A longer pattern that does not collapse within the bound, with no run of '*' or holding '[', is a fixed
    // field-level 400 on the field, and the store is never asked to search.
    [Theory]
    [InlineData(PatternKind, 4001)]
    [InlineData(BracketKind, 4001)]
    public async Task Search_PatternOverTheBound_IsA400BeforeTheStore(string kind, int size)
    {
        var store = FakeDataStore.WithStandardForms();

        var result = await CreateService(store).SearchAsync(
            FakeForms.TwoPageViewId, LastNameSearch(kind, size), CancellationToken.None);

        var errors = ValidationErrors(result);
        Assert.Equal(new[] { "LastName" }, errors.Keys.ToArray());
        Assert.All(
            errors.Values.SelectMany(messages => messages),
            message => Assert.DoesNotContain(Sentinel, message, StringComparison.Ordinal));
        Assert.DoesNotContain(store.Calls, call => call.Method == nameof(IDataStore.SearchAsync));
    }

    // The fake keeps the search builder's contract: a LIKE pattern over the bound, in any condition, is an
    // ArgumentException.
    [Fact]
    public async Task FakeStoreSearch_PatternOverTheBound_ThrowsArgumentException()
    {
        var store = FakeDataStore.WithStandardForms();
        var form = await TwoPageFormAsync(store);
        SearchCondition[] conditions =
        [
            LikeCondition("LastName", 1, 1),
            LikeCondition("Notes", 1, RecordQueryRules.MaxLikePatternLength + 1),
        ];

        await Assert.ThrowsAsync<ArgumentException>(
            "conditions",
            () => store.SearchAsync(form, conditions, includeDeleted: false, null, CancellationToken.None));
    }

    // The fake keeps the list builder's contract: a filter whose contains pattern is over the bound is an
    // ArgumentException, and one at the bound lists.
    [Fact]
    public async Task FakeStoreList_FilterOverTheBound_ThrowsArgumentException()
    {
        var store = FakeDataStore.WithStandardForms();
        var form = await TwoPageFormAsync(store);

        await Assert.ThrowsAsync<ArgumentException>(
            "query",
            () => store.ListAsync(form, ListQuery(new string('%', 2000)), CancellationToken.None));

        var page = await store.ListAsync(form, ListQuery(new string('%', 1999)), CancellationToken.None);
        Assert.Empty(page.Items);
    }

    private static RecordService CreateService(FakeDataStore store) => new(
        store,
        new FormService(store, new CheckCodeEngine(), NullLogger<FormService>.Instance),
        new SaveStampProvider(TimeProvider.System),
        NullLogger<RecordService>.Instance);

    // A LastName criterion of size alternatives, or a single wildcard pattern of size characters: one '*', a run of '*',
    // or a set followed by a run of '*'. Each carries the sentinel, so an error message that echoed it would be caught.
    private static SearchRequest LastNameSearch(string kind, int size)
    {
        var value = kind switch
        {
            AlternativesKind => string.Join(
                " or ",
                Enumerable.Range(0, size).Select(index => Sentinel + index.ToString(CultureInfo.InvariantCulture))),
            PatternKind => Sentinel + "*" + new string('x', size - Sentinel.Length - 1),
            RunKind => Sentinel + new string('*', size - Sentinel.Length),
            BracketKind => Sentinel + "[a-z]" + new string('*', size - Sentinel.Length - 5),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown search kind."),
        };

        return new SearchRequest([new SearchCriterion("LastName", FieldValue.FromText(value))]);
    }

    private static SearchCondition LikeCondition(string field, int count, int patternLength) => new(
        field,
        SearchTarget.PageField,
        FieldType.Text,
        [.. Enumerable.Range(0, count).Select(_ =>
            new SearchAlternative(SearchOperator.Like, FieldValue.FromText(new string('%', patternLength))))]);

    private static RecordListQuery ListQuery(string q) =>
        new(q, IncludeDeleted: false, RecordQueryRules.DefaultPage, RecordQueryRules.DefaultPageSize);

    private static async Task<FormMetadata> TwoPageFormAsync(FakeDataStore store)
    {
        var form = await store.GetFormMetadataAsync(FakeForms.TwoPageViewId, CancellationToken.None);
        Assert.NotNull(form);
        return form;
    }

    // The errors map of a field-level 400 as ApiProblems builds it.
    private static IDictionary<string, string[]> ValidationErrors(IResult? result)
    {
        var problem = Assert.IsType<ApiProblems.ProblemResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
        return Assert.IsType<HttpValidationProblemDetails>(problem.Value).Errors;
    }
}

/// <summary>
/// Pins the Find and list semantics of the in-memory <see cref="FakeDataStore"/> against the
/// <see cref="IDataStore"/> Find and list contract that AAP 0.3.4.3 and 0.3.4.4 set out: exact comparison on integer
/// columns and binary64 comparison on float columns, SQL <c>LIKE</c> over the whole text, and one scan that numbers,
/// filters, counts and pages records as the SQL statements do.
/// </summary>
/// <remarks>
/// The class calls the store's <see cref="IDataStore"/> members directly and needs no database: it carries no
/// <c>Integration</c> trait, joins no database collection and takes no lock. Its form is synthetic: view 9100 on the
/// unseeded base table <c>FakeFind</c>, with <c>Count</c> on a <c>bigint</c> column, <c>Weight</c> on a
/// <c>float</c> column, the text <c>Name</c>, and the Option <c>Choice</c> with items <c>a,b,c</c> on a
/// <c>smallint</c> column on page 1, and the text <c>Notes</c> on page 2.
/// </remarks>
public sealed class FakeDataStoreFindTests
{
    private const int ViewId = 9100;
    private const int FirstPageId = 910001;
    private const int SecondPageId = 910002;
    private const string BaseTable = "FakeFind";
    private const string CountField = "Count";
    private const string WeightField = "Weight";
    private const string NameField = "Name";
    private const string NotesField = "Notes";
    private const string ChoiceField = "Choice";
    private const long TwoToThe53 = 9007199254740992;

    private static readonly FormMetadata Form = CreateForm();

    private static readonly string[] GridColumnNames = [CountField, WeightField, NameField, NotesField];

    /// <summary>Text and SQL <c>LIKE</c> patterns, with whether the pattern matches the whole text.</summary>
    public static TheoryData<string, string, bool> LikeCases => new()
    {
        // Patterns long enough for a regular-expression automaton to exceed its size limit, at the store's 4,000
        // character LIKE pattern bound.
        { new string('a', 4000), new string('a', 3999) + "%", true },
        { new string('a', 4000), new string('a', 3998) + "_", false },
        { new string('a', 1000), string.Concat(Enumerable.Repeat("%a", 1000)), true },
        { new string('a', 999), string.Concat(Enumerable.Repeat("%a", 1000)), false },

        // A trailing line feed is a character the pattern must cover.
        { "abc\n", "ab_", false },
        { "abc\n", "abc_", true },
        { "abc\n", "abc%", true },

        // A reversed range matches no character, while the class's other members still match.
        { "m", "[z-a]", false },
        { "m", "[z-am]", true },

        // Negation and letter case.
        { "d", "[^a-c]", true },
        { "B", "[^a-c]", false },
        { "aBcDef", "AbC%", true },
        { "bX", "[A-C]x", true },

        // Every [ opens a class, which the first ] after it closes or, with no such ], the end of the pattern. The
        // unclosed class of a[b% holds b and %, and [[] holds [.
        { "ab", "a[b", true },
        { "a[b", "a[b", false },
        { "a[b", "a[b%", false },
        { "a%", "a[%", true },
        { "ac", "a[a-c", true },
        { "ad", "a[a-c", false },
        { "ac", "a[^b", true },
        { "ab", "a[^b", false },
        { "abc", "%[c", true },
        { "[", "[[]", true },

        // A class with no member matches no character.
        { "[", "[", false },
        { "a[", "a[", false },
        { "[]", "[]", false },
        { "x", "[]", false },
        { "a", "a[]%", false },
        { "x", "[]x]", false },

        // A negated class with no member matches any one character.
        { "a", "[^]", true },
        { "", "[^]", false },
        { "a", "[^", true },
        { "ab", "a[^]", true },
        { "a]", "[^]]", true },
        { "]", "[^]]", false },
    };

    // The parser keeps 9007199254740993 exact, and the three counts from 2^53 share two nearest doubles, so only an
    // exact comparison keeps them apart.
    [Theory]
    [InlineData("9007199254740993", new long[] { TwoToThe53 + 1 })]
    [InlineData("<>9007199254740993", new long[] { long.MinValue, 2, 3, TwoToThe53, TwoToThe53 + 2, long.MaxValue })]
    [InlineData("<9007199254740993", new long[] { long.MinValue, 2, 3, TwoToThe53 })]
    [InlineData("<=9007199254740993", new long[] { long.MinValue, 2, 3, TwoToThe53, TwoToThe53 + 1 })]
    [InlineData(">9007199254740993", new long[] { TwoToThe53 + 2, long.MaxValue })]
    [InlineData(">=9007199254740993", new long[] { TwoToThe53 + 1, TwoToThe53 + 2, long.MaxValue })]
    public async Task IntegerColumnComparesExactly(string criterion, long[] expected)
    {
        await AssertCountsFoundAsync(Parse(CountField, criterion), expected);
    }

    // A criterion holding no exact integer compares by its exact floor, never by its nearest double, as the SQL store
    // binds it: 2.5, 9007199254740992.5, 2.0000000000000001 and 1.9999999999999999 each lie strictly between their
    // floor and the next integer, though the last three have the whole nearest doubles 2^53, 2 and 2. A number beyond
    // the Int64 range lies beyond every value on its side, even where its nearest double is a value's own:
    // long.MaxValue rounds to 2^63, the nearest double of 9223372036854775808, yet is smaller, and long.MinValue is
    // -2^63, the nearest double of -9223372036854775809, yet is larger. The conditions are built directly, because the
    // store's contract for such criteria does not depend on whether the parser admits them on an integer column.
    [Theory]
    [InlineData(SearchOperator.Equal, "2.5", new long[0])]
    [InlineData(SearchOperator.Less, "2.5", new long[] { long.MinValue, 2 })]
    [InlineData(SearchOperator.Greater, "2.5", new long[] { 3, TwoToThe53, TwoToThe53 + 1, TwoToThe53 + 2, long.MaxValue })]
    [InlineData(SearchOperator.Equal, "9007199254740992.5", new long[0])]
    [InlineData(SearchOperator.Equal, "2.0000000000000001", new long[0])]
    [InlineData(SearchOperator.LessOrEqual, "1.9999999999999999", new long[] { long.MinValue })]
    [InlineData(SearchOperator.Less, "9223372036854775808", new long[] { long.MinValue, 2, 3, TwoToThe53, TwoToThe53 + 1, TwoToThe53 + 2, long.MaxValue })]
    [InlineData(SearchOperator.GreaterOrEqual, "9223372036854775808", new long[0])]
    [InlineData(SearchOperator.Equal, "-9223372036854775809", new long[0])]
    [InlineData(SearchOperator.Greater, "-9223372036854775809", new long[] { long.MinValue, 2, 3, TwoToThe53, TwoToThe53 + 1, TwoToThe53 + 2, long.MaxValue })]
    [InlineData(SearchOperator.Greater, "-1e19", new long[] { long.MinValue, 2, 3, TwoToThe53, TwoToThe53 + 1, TwoToThe53 + 2, long.MaxValue })]
    [InlineData(SearchOperator.Less, "-1e19", new long[0])]
    public async Task IntegerColumnComparesFractionsAndNumbersBeyondInt64(
        SearchOperator comparison,
        string numberText,
        long[] expected)
    {
        Assert.True(FieldValueCoercion.TryParseNumber(numberText, out var number));
        SearchCondition[] conditions =
        [
            new(CountField, SearchTarget.PageField, FieldType.Number, [new SearchAlternative(comparison, number)]),
        ];

        await AssertCountsFoundAsync(conditions, expected);
    }

    // A float column compares binary64 doubles, as SQL float comparison does: 2^53 + 1 rounds to 2^53.
    [Theory]
    [InlineData("9007199254740993", true)]
    [InlineData("<9007199254740993", false)]
    public async Task FloatColumnComparesDoubles(string criterion, bool matches)
    {
        var store = CreateStore();
        Seed(store, WeightField, FieldValue.FromNumber(TwoToThe53));

        var response = await SearchAsync(store, Parse(WeightField, criterion));

        Assert.Equal(matches ? 1 : 0, response.Total);
    }

    // The conditions are built directly: the parser trims criteria and chooses LIKE only for a value holding a
    // wildcard, so it cannot express every pattern here.
    [Theory]
    [MemberData(nameof(LikeCases))]
    public async Task LikeMatchesTheWholeTextIgnoringCase(string text, string pattern, bool matches)
    {
        var store = CreateStore();
        Seed(store, NameField, FieldValue.FromText(text));
        SearchCondition[] conditions =
        [
            new(NameField, SearchTarget.PageField, FieldType.Text,
                [new SearchAlternative(SearchOperator.Like, FieldValue.FromText(pattern))]),
        ];

        var response = await SearchAsync(store, conditions);

        Assert.Equal(matches ? 1 : 0, response.Total);
    }

    // Records r1, r2, the deleted rd, r3, r4 and r5, in UniqueKey order.
    [Theory]
    [InlineData(null, false, 1, 2, new[] { "r1", "r2" }, 5)]
    [InlineData(null, false, 3, 2, new[] { "r5" }, 5)]
    [InlineData(null, true, 2, 2, new[] { "rd", "r3" }, 6)]
    [InlineData(null, false, int.MaxValue, 200, new string[0], 5)]
    [InlineData("R2", false, 1, 50, new[] { "r2" }, 1)]
    [InlineData("D", true, 1, 50, new[] { "rd" }, 1)]
    [InlineData("D", false, 1, 50, new string[0], 0)]
    public async Task ListCountsEveryMatchAndProjectsOnlyThePage(
        string? q,
        bool includeDeleted,
        int page,
        int pageSize,
        string[] expectedNames,
        int expectedTotal)
    {
        var store = CreateStore();
        foreach (var name in new[] { "r1", "r2", "rd", "r3", "r4", "r5" })
        {
            Seed(
                store,
                NameField,
                FieldValue.FromText(name),
                name == "rd" ? RecordQueryRules.DeletedRecStatus : RecordQueryRules.ActiveRecStatus);
        }

        var result = await store.ListAsync(
            Form, new RecordListQuery(q, includeDeleted, page, pageSize), CancellationToken.None);

        Assert.Equal(expectedTotal, result.Total);
        Assert.Equal(expectedNames, result.Items.Select(item => item.Values[NameField].AsText()));
        Assert.All(result.Items, item => Assert.Equal(GridColumnNames, item.Values.Keys));
    }

    // Records: 0 match, 1 other, 2 match deleted, 3 match missing page 2, 4 match, 5 match. The excluded id is passed
    // in upper case, since ids match ignoring case.
    [Theory]
    [InlineData(false, null, new[] { 0, 4, 5 }, new[] { 1, 4, 5 })]
    [InlineData(true, null, new[] { 0, 2, 4, 5 }, new[] { 1, 3, 5, 6 })]
    [InlineData(false, 4, new[] { 0, 5 }, new[] { 1, 5 })]
    public async Task SearchNumbersPositionsBeforeCriteriaAndExcludesBeforeTheTotal(
        bool includeDeleted,
        int? excluded,
        int[] expectedRecords,
        int[] expectedPositions)
    {
        var store = CreateStore();
        string[] ids =
        [
            Seed(store, NameField, FieldValue.FromText("match")),
            Seed(store, NameField, FieldValue.FromText("other")),
            Seed(store, NameField, FieldValue.FromText("match"), RecordQueryRules.DeletedRecStatus),
            Seed(store, NameField, FieldValue.FromText("match"), missingPageIds: [SecondPageId]),
            Seed(store, NameField, FieldValue.FromText("match")),
            Seed(store, NameField, FieldValue.FromText("match")),
        ];
        var exclude = excluded is { } index ? ids[index].ToUpperInvariant() : null;

        var response = await store.SearchAsync(
            Form, Parse(NameField, "match"), includeDeleted, exclude, CancellationToken.None);

        Assert.Equal(expectedRecords.Select(record => ids[record]), response.Items.Select(item => item.GlobalRecordId));
        Assert.Equal(expectedPositions, response.Items.Select(item => item.Position));
        Assert.Equal(expectedRecords.Length, response.Total);
        Assert.False(response.Truncated);
        Assert.All(response.Items, item => Assert.Equal(GridColumnNames, item.Values.Keys));
    }

    [Fact]
    public async Task SearchCapsItemsAndCountsEveryMatch()
    {
        var store = CreateStore();
        const int matches = RecordQueryRules.SearchCap + 5;
        for (var record = 0; record < matches; record++)
        {
            Seed(store, NameField, FieldValue.FromText("match"));
        }

        var response = await SearchAsync(store, Parse(NameField, "match"));

        Assert.Equal(Enumerable.Range(1, RecordQueryRules.SearchCap), response.Items.Select(item => item.Position));
        Assert.Equal(matches, response.Total);
        Assert.True(response.Truncated);
    }

    // Writes replace rows rather than change them, and later reads see the replacements: an update inserts the
    // missing page row, so search, which needs every page row, then finds the record, and a soft delete leaves the
    // active set.
    [Fact]
    public async Task WritesPublishRowsThatLaterReadsSee()
    {
        var store = CreateStore();
        var stamp = new SaveStamp("tester", new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        var partial = Seed(store, NameField, FieldValue.FromText("partial"), missingPageIds: [SecondPageId]);
        var inserted = await store.InsertAsync(
            Form,
            new Dictionary<string, FieldValue> { [NameField] = FieldValue.FromText("inserted") },
            stamp,
            CancellationToken.None);
        var criteria = Parse(NameField, "partial or inserted");

        Assert.Equal(new[] { inserted.GlobalRecordId }, await SearchIdsAsync(store, criteria));

        var updated = await store.UpdateAsync(
            Form,
            partial,
            new Dictionary<string, FieldValue> { [NotesField] = FieldValue.FromText("filled") },
            stamp,
            _ => RecordValidationResult.Success(new Dictionary<string, FieldValue>()),
            permissions: null,
            CancellationToken.None);

        Assert.True(updated.IsSuccess);
        var state = Assert.IsType<FakeRecordState>(store.GetRecord(ViewId, partial));
        Assert.Equal(new[] { FirstPageId, SecondPageId }, state.PresentPageIds.Order());
        Assert.Equal("filled", state.Values[NotesField].AsText());
        Assert.Equal(stamp, state.LastSave);
        Assert.Equal(new[] { partial, inserted.GlobalRecordId }, await SearchIdsAsync(store, criteria));

        Assert.Equal(
            SoftDeleteResult.Deleted,
            await store.SoftDeleteAsync(Form, inserted.GlobalRecordId, stamp, CancellationToken.None));
        Assert.Equal(new[] { partial }, await SearchIdsAsync(store, criteria));
        var all = await store.ListAsync(
            Form, RecordListQuery.Default with { IncludeDeleted = true }, CancellationToken.None);
        Assert.Equal(
            new[] { RecordQueryRules.ActiveRecStatus, RecordQueryRules.DeletedRecStatus },
            all.Items.Select(item => item.RecStatus));
    }

    // An option field holds only an option index, as the SQL builders bind it, though its smallint column could also
    // store the number 1. The parsed criterion "1" is an index, so it finds the stored index 1 and not the index 0,
    // while the number 1 is rejected before any change, by a seed and by an insert alike.
    [Theory]
    [InlineData(FieldValueKind.Index, true)]
    [InlineData(FieldValueKind.Number, false)]
    public async Task OptionFieldAdmitsOnlyAnIndex(FieldValueKind kind, bool admitted)
    {
        var store = CreateStore();
        var other = Seed(store, ChoiceField, FieldValue.FromIndex(0));
        var value = kind == FieldValueKind.Index ? FieldValue.FromIndex(1) : FieldValue.FromInteger(1);
        var criteria = Parse(ChoiceField, "1");
        Assert.Equal(FieldValueKind.Index, Assert.Single(Assert.Single(criteria).Alternatives).Value?.Kind);

        var seedFailure = Record.Exception(() => Seed(store, ChoiceField, value));
        var insertFailure = await Record.ExceptionAsync(() => store.InsertAsync(
            Form,
            new Dictionary<string, FieldValue> { [ChoiceField] = value },
            new SaveStamp("tester", new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc)),
            CancellationToken.None));

        if (admitted)
        {
            Assert.Null(seedFailure);
            Assert.Null(insertFailure);
        }
        else
        {
            Assert.Equal("values", Assert.IsType<ArgumentException>(seedFailure).ParamName);
            Assert.Equal("values", Assert.IsType<ArgumentException>(insertFailure).ParamName);
        }

        var records = store.GetRecords(ViewId).Select(record => record.GlobalRecordId).ToArray();
        Assert.Equal(admitted ? 3 : 1, records.Length);
        Assert.Equal(other, records[0]);
        Assert.Equal(admitted ? 1 : 0, store.WriteCalls.Count);
        Assert.Equal(records[1..], await SearchIdsAsync(store, criteria));
    }

    private static FakeDataStore CreateStore() =>
        new(new FakeForm(Form, new Dictionary<string, IReadOnlyList<FieldOption>>(StringComparer.OrdinalIgnoreCase), null));

    private static string Seed(
        FakeDataStore store,
        string field,
        FieldValue value,
        int recStatus = RecordQueryRules.ActiveRecStatus,
        IEnumerable<int>? missingPageIds = null) =>
        store.SeedRecord(ViewId, new Dictionary<string, FieldValue> { [field] = value }, recStatus, missingPageIds);

    private static IReadOnlyList<SearchCondition> Parse(string field, string criterion)
    {
        var result = SearchCriteriaParser.Parse(
            FormDefinitionFactory.GetDataFields(Form),
            [new SearchCriterion(field, FieldValue.FromText(criterion))]);
        Assert.True(result.IsSuccess);
        return result.Conditions;
    }

    private static Task<SearchResponse> SearchAsync(FakeDataStore store, IReadOnlyList<SearchCondition> conditions) =>
        store.SearchAsync(Form, conditions, includeDeleted: false, excludeGlobalRecordId: null, CancellationToken.None);

    private static async Task<string[]> SearchIdsAsync(FakeDataStore store, IReadOnlyList<SearchCondition> conditions) =>
        [.. (await SearchAsync(store, conditions)).Items.Select(item => item.GlobalRecordId)];

    // Seeds the counts long.MinValue, 2, 3, 2^53, 2^53 + 1, 2^53 + 2 and long.MaxValue, in UniqueKey order, and
    // asserts that the conditions find exactly the expected counts, in that order.
    private static async Task AssertCountsFoundAsync(IReadOnlyList<SearchCondition> conditions, long[] expected)
    {
        var store = CreateStore();
        foreach (var count in new[] { long.MinValue, 2, 3, TwoToThe53, TwoToThe53 + 1, TwoToThe53 + 2, long.MaxValue })
        {
            Seed(store, CountField, FieldValue.FromInteger(count));
        }

        var response = await SearchAsync(store, conditions);

        Assert.Equal(expected, response.Items.Select(item => Integer(item.Values[CountField])));
        Assert.Equal(expected.Length, response.Total);
    }

    private static long Integer(FieldValue value)
    {
        Assert.True(value.TryGetInteger(out var integer));
        return integer;
    }

    // The system rows and field-type ids are the seeded metaFields and metaFieldTypes rows, as FakeForms builds them.
    private static FormMetadata CreateForm()
    {
        PageMetadata[] pages =
        [
            new(FirstPageId, "Page 1", 0, BaseTable + FirstPageId.ToString(CultureInfo.InvariantCulture)),
            new(SecondPageId, "Page 2", 1, BaseTable + SecondPageId.ToString(CultureInfo.InvariantCulture)),
        ];
        FieldMetadata[] fields =
        [
            SystemRow(1, "UniqueKey", 23),
            SystemRow(2, "RecStatus", 22),
            SystemRow(3, "GlobalRecordId", 26),
            PageField(4, FirstPageId, 1m, CountField, 5, "Number", "bigint", 8),
            PageField(5, FirstPageId, 2m, WeightField, 5, "Number", "float", 8),
            PageField(6, FirstPageId, 3m, NameField, 1, "Text", "nvarchar", 8000),
            PageField(7, SecondPageId, 1m, NotesField, 1, "Text", "nvarchar", 8000),
            PageField(8, FirstPageId, 4m, ChoiceField, 12, "Option", "smallint", 2) with { List = "a,b,c" },
        ];
        return new FormMetadata(ViewId, BaseTable, BaseTable, Array.AsReadOnly(pages), Array.AsReadOnly(fields));
    }

    private static FieldMetadata SystemRow(int ordinal, string name, int fieldTypeId) => new()
    {
        FieldId = (ViewId * 1000) + ordinal,
        Name = name,
        FieldTypeId = fieldTypeId,
        FieldTypeName = name,
        TabIndex = 0m,
    };

    private static FieldMetadata PageField(
        int ordinal,
        int pageId,
        decimal tabIndex,
        string name,
        int fieldTypeId,
        string fieldTypeName,
        string sqlTypeName,
        int maxLengthBytes) => new()
        {
            FieldId = (ViewId * 1000) + ordinal,
            Name = name,
            FieldTypeId = fieldTypeId,
            FieldTypeName = fieldTypeName,
            Prompt = name,
            TabIndex = tabIndex,
            PageId = pageId,
            Column = FieldTypeMapper.DescribeColumn(name, sqlTypeName, maxLengthBytes),
        };
}

/// <summary>
/// The route and query coercion of the record routes (AAP 0.3.4.1), owned by <see cref="RequestParameters"/>, over
/// HTTP: only a parameter that was not sent takes its default, a value sent once coerces only when it is a complete
/// integer token or <c>true</c>/<c>false</c>, and every other value, a key sent more than once included, is one
/// field-level 400, keyed by parameter, that reaches no store call and echoes no submitted value.
/// </summary>
/// <remarks>
/// <para>
/// The host is composed here rather than through <see cref="ApiFactory"/>, which hosts the composition root in
/// <c>Program.cs</c>. It maps exactly <see cref="RecordEndpoints.MapRecordEndpoints"/> over the real
/// <see cref="FormService"/>, <see cref="RecordService"/> and <see cref="CheckCodeEngine"/> on a test server, with a
/// <see cref="FakeDataStore"/> as the store, so each case observes the framework's string binding, the coercion and
/// the record service, and nothing else the composition root maps or registers. The endpoint API explorer, which the
/// OpenAPI document's parameters are generated from, is registered too, so the binding's parameter descriptions can
/// be read.
/// </para>
/// <para>
/// The class needs no database: it carries no <c>Integration</c> trait, joins no database collection and takes no
/// <c>DatabaseWriteLock</c>. Each request it asserts on starts from an empty store call log, so a rejected request is
/// shown to reach no store member, the form lookup included. In a query string <c>+</c> decodes to a space, so a
/// leading plus sign is sent as <c>%2B</c>.
/// </para>
/// </remarks>
public sealed class RequestParameterCoercionTests : IAsyncLifetime
{
    // The store seeds FakeForms.TwoPage with three active records and, between the second and the third, one deleted
    // record; FakeForms.Empty holds none.
    private const int ActiveRecordCount = 3;
    private const int AllRecordCount = ActiveRecordCount + 1;

    // No form of this store has this view id.
    private const int UnknownViewId = 9999;

    private const string FirstActiveId = "0b7a6c9e-2f1d-4e57-9c3a-5d8e1f20a001";
    private const string SecondActiveId = "0b7a6c9e-2f1d-4e57-9c3a-5d8e1f20a002";
    private const string DeletedId = "0b7a6c9e-2f1d-4e57-9c3a-5d8e1f20a003";
    private const string ThirdActiveId = "0b7a6c9e-2f1d-4e57-9c3a-5d8e1f20a004";

    // The LastName of the second active record and of no other, so q can be shown to filter.
    private const string FilterMatch = "Bravo";

    // The query text that sends one parameter, up to its value.
    private const string PageQuery = "?" + RequestParameters.Page + "=";
    private const string PageSizeQuery = "?" + RequestParameters.PageSize + "=";
    private const string IncludeDeletedQuery = "?" + RequestParameters.IncludeDeleted + "=";
    private const string FilterQuery = "?" + RequestParameters.Query + "=";

    // Sent integer values that are not one complete token of an optional single sign and ASCII digits, or that leave
    // Int32, as the query text after the key: "=" sends an empty value and "" sends the bare key.
    private static readonly string[] MalformedIntegerQueryValues =
    [
        "=",
        "",
        "=%201",
        "=1%20",
        "=%091",
        "=1%00",
        "=%001",
        "=1.0",
        "=1e3",
        "=0x10",
        "=1,000",
        "=abc",
        "=%2B%2B1",
        "=--1",
        "=%2B-1",
        "=1-",
        "=%2B",
        "=-",
        "=%EF%BC%91",
        "=%D9%A1",
        "=%E2%88%921",
        "=" + AboveInt32,
        "=" + BelowInt32,
        "=99999999999999999999",
    ];

    // Sent includeDeleted values that are neither true nor false ignoring case, in the same form.
    private static readonly string[] InvalidBooleanQueryValues =
    [
        "=",
        "",
        "=%20true",
        "=true%20",
        "=true%00",
        "=%00false",
        "=1",
        "=0",
        "=yes",
        "=no",
        "=tru",
        "=truefalse",
    ];

    // Sent values that repeat the key, in the same form, where {0} is the key and {1} and {2} are two values the
    // parameter accepts when sent once: an empty or bare occurrence beside an accepted one, the same accepted value
    // twice, and two different accepted values.
    private static readonly string[] RepeatedQueryValueTemplates =
    [
        "=&{0}={1}",
        "={1}&{0}",
        "={1}&{0}={1}",
        "={1}&{0}={2}",
    ];

    // The occurrence-bound query parameters, by the endpoint name of each route that takes them.
    private static readonly string[] OccurrenceBoundParameters =
    [
        "ListRecords/" + RequestParameters.IncludeDeleted,
        "ListRecords/" + RequestParameters.Page,
        "ListRecords/" + RequestParameters.PageSize,
        "CountRecords/" + RequestParameters.IncludeDeleted,
        "GetRecordAt/" + RequestParameters.IncludeDeleted,
        "GetRecordPosition/" + RequestParameters.IncludeDeleted,
    ];

    private readonly FakeDataStore store = new(FakeForms.TwoPage, FakeForms.Empty);

    private WebApplication? app;

    private HttpClient? client;

    /// <summary>
    /// Every sent page or pageSize value that is not one integer, a repeated key included, per parameter.
    /// </summary>
    public static TheoryData<string, string> MalformedPagingValues
    {
        get
        {
            var data = new TheoryData<string, string>();
            (string Parameter, long Accepted, long OtherAccepted)[] parameters =
            [
                (RequestParameters.Page, RecordQueryRules.MinPage, RecordQueryRules.MinPage + 1),
                (RequestParameters.PageSize, RecordQueryRules.DefaultPageSize, RecordQueryRules.MaxPageSize),
            ];
            foreach (var (parameter, accepted, otherAccepted) in parameters)
            {
                foreach (var value in MalformedIntegerQueryValues)
                {
                    data.Add(parameter, value);
                }

                foreach (var value in RepeatedQueryValues(parameter, Text(accepted), Text(otherAccepted)))
                {
                    data.Add(parameter, value);
                }
            }

            return data;
        }
    }

    /// <summary>
    /// Every invalid includeDeleted value, a repeated key included, on each of the four routes that take the flag.
    /// </summary>
    public static TheoryData<string, string> InvalidIncludeDeletedRequests
    {
        get
        {
            var data = new TheoryData<string, string>();
            string[] values =
            [
                .. InvalidBooleanQueryValues,
                .. RepeatedQueryValues(RequestParameters.IncludeDeleted, "true", "false"),
            ];
            foreach (var path in IncludeDeletedRoutes)
            {
                foreach (var value in values)
                {
                    data.Add(path, value);
                }
            }

            return data;
        }
    }

    /// <summary>An absent includeDeleted and accepted spellings, on each route that takes the flag.</summary>
    public static TheoryData<string, string> AcceptedIncludeDeletedRequests
    {
        get
        {
            var data = new TheoryData<string, string>();
            string[] queries = [string.Empty, IncludeDeletedQuery + "TRUE", IncludeDeletedQuery + "False"];
            foreach (var path in IncludeDeletedRoutes)
            {
                foreach (var query in queries)
                {
                    data.Add(path, query);
                }
            }

            return data;
        }
    }

    /// <summary>The includeDeleted query of a count request and the count it selects.</summary>
    public static TheoryData<string, int> CountSelections => new()
    {
        { string.Empty, ActiveRecordCount },
        { IncludeDeletedQuery + "TRUE", AllRecordCount },
        { IncludeDeletedQuery + "tRuE", AllRecordCount },
        { IncludeDeletedQuery + "False", ActiveRecordCount },
        { IncludeDeletedQuery + "FALSE", ActiveRecordCount },
    };

    /// <summary>
    /// Route positions that are not a 32-bit integer token. A NUL is not among them: the host's URL decoder refuses
    /// <c>%00</c> in a path before routing, so the NUL cases are sent as query values, which the same integer rule
    /// coerces.
    /// </summary>
    public static TheoryData<string> MalformedPositions => new()
    {
        "abc",
        "1.5",
        "1e3",
        "0x1",
        "1,0",
        AboveInt32,
        BelowInt32,
        "%201",
        "1%20",
        "1%09",
        "%E2%88%921",
        "%EF%BC%91",
        "--1",
        "+",
        "-",
    };

    /// <summary>Accepted route positions with their query and the record each opens.</summary>
    public static TheoryData<string, string> OpenedPositions => new()
    {
        { "1", FirstActiveId },
        { "+1", FirstActiveId },
        { "003", ThirdActiveId },
        { "3" + IncludeDeletedQuery + "False", ThirdActiveId },
        { "3" + IncludeDeletedQuery + "TRUE", DeletedId },
        { "+4" + IncludeDeletedQuery + "true", ThirdActiveId },
    };

    /// <summary>32-bit integer positions outside 1..count, which coerce and reach the service's 404.</summary>
    public static TheoryData<string> PositionsOutsideTheSet => new()
    {
        "0",
        "-0",
        "-1",
        Int32Min,
        Int32Max,
        Text(ActiveRecordCount + 1),
        Text(AllRecordCount + 1) + IncludeDeletedQuery + "true",
    };

    /// <summary>Integer page and pageSize values outside their bounds, with the range message each gets.</summary>
    public static TheoryData<string, string, string> OutOfRangePagingValues => new()
    {
        { RequestParameters.Page, Text(RecordQueryRules.MinPage - 1), RecordQueryRules.PageOutOfRangeMessage },
        { RequestParameters.Page, "-0", RecordQueryRules.PageOutOfRangeMessage },
        { RequestParameters.Page, "-1", RecordQueryRules.PageOutOfRangeMessage },
        { RequestParameters.Page, Int32Min, RecordQueryRules.PageOutOfRangeMessage },
        {
            RequestParameters.PageSize,
            Text(RecordQueryRules.MinPageSize - 1),
            RecordQueryRules.PageSizeOutOfRangeMessage
        },
        { RequestParameters.PageSize, "-1", RecordQueryRules.PageSizeOutOfRangeMessage },
        {
            RequestParameters.PageSize,
            Text(RecordQueryRules.MaxPageSize + 1),
            RecordQueryRules.PageSizeOutOfRangeMessage
        },
        { RequestParameters.PageSize, Int32Max, RecordQueryRules.PageSizeOutOfRangeMessage },
        { RequestParameters.PageSize, Int32Min, RecordQueryRules.PageSizeOutOfRangeMessage },
    };

    /// <summary>Accepted list requests with the page, page size and total each returns.</summary>
    public static TheoryData<string, int, int, int> AcceptedListPages => new()
    {
        { RecordsPath, RecordQueryRules.DefaultPage, RecordQueryRules.DefaultPageSize, ActiveRecordCount },
        {
            RecordsPath + PageSizeQuery + Text(RecordQueryRules.MinPageSize),
            RecordQueryRules.DefaultPage,
            RecordQueryRules.MinPageSize,
            ActiveRecordCount
        },
        {
            RecordsPath + PageSizeQuery + Text(RecordQueryRules.MaxPageSize),
            RecordQueryRules.DefaultPage,
            RecordQueryRules.MaxPageSize,
            ActiveRecordCount
        },
        {
            WithQuery(RecordsPath, (RequestParameters.Page, "%2B2"), (RequestParameters.PageSize, "%2B1")),
            2,
            1,
            ActiveRecordCount
        },
        {
            WithQuery(RecordsPath, (RequestParameters.Page, "0002"), (RequestParameters.PageSize, "002")),
            2,
            2,
            ActiveRecordCount
        },
        { RecordsPath + PageQuery + "2", 2, RecordQueryRules.DefaultPageSize, ActiveRecordCount },
        { RecordsPath + PageQuery + Int32Max, int.MaxValue, RecordQueryRules.DefaultPageSize, ActiveRecordCount },
        {
            WithQuery(
                RecordsPath,
                (RequestParameters.Page, Int32Max),
                (RequestParameters.PageSize, Text(RecordQueryRules.MaxPageSize))),
            int.MaxValue,
            RecordQueryRules.MaxPageSize,
            ActiveRecordCount
        },
        {
            WithQuery(RecordsPath, (RequestParameters.Page, Int32Max), (RequestParameters.IncludeDeleted, "true")),
            int.MaxValue,
            RecordQueryRules.DefaultPageSize,
            AllRecordCount
        },
        { EmptyRecordsPath, RecordQueryRules.DefaultPage, RecordQueryRules.DefaultPageSize, 0 },
        { EmptyRecordsPath + PageQuery + Int32Max, int.MaxValue, RecordQueryRules.DefaultPageSize, 0 },
    };

    /// <summary>The q part of a list request and the total it selects.</summary>
    public static TheoryData<string, int> ListFilters => new()
    {
        { string.Empty, ActiveRecordCount },
        { "?" + RequestParameters.Query, ActiveRecordCount },
        { FilterQuery, ActiveRecordCount },
        { FilterQuery + "%20%20", ActiveRecordCount },
        { FilterQuery + "%09", ActiveRecordCount },
        { FilterQuery + FilterMatch, 1 },
    };

    /// <summary>
    /// Requests with more than one bad parameter, or a bad parameter on an unknown form, with the errors keys and the
    /// one message each key carries, in the same order.
    /// </summary>
    public static TheoryData<string, string[], string[]> MergedRejections => new()
    {
        {
            WithQuery(
                RecordsPath,
                (RequestParameters.Page, "x"),
                (RequestParameters.PageSize, Text(RecordQueryRules.MinPageSize - 1)),
                (RequestParameters.IncludeDeleted, "maybe")),
            [RequestParameters.Page, RequestParameters.PageSize, RequestParameters.IncludeDeleted],
            [
                RequestParameters.IntegerExpectedMessage,
                RecordQueryRules.PageSizeOutOfRangeMessage,
                RequestParameters.BooleanExpectedMessage,
            ]
        },
        {
            WithQuery(
                RecordsPath,
                (RequestParameters.Page, string.Empty),
                (RequestParameters.PageSize, string.Empty),
                (RequestParameters.IncludeDeleted, string.Empty)),
            [RequestParameters.Page, RequestParameters.PageSize, RequestParameters.IncludeDeleted],
            [
                RequestParameters.IntegerExpectedMessage,
                RequestParameters.IntegerExpectedMessage,
                RequestParameters.BooleanExpectedMessage,
            ]
        },
        {
            WithQuery(
                RecordsPath,
                (RequestParameters.Page, null),
                (RequestParameters.PageSize, null),
                (RequestParameters.IncludeDeleted, null)),
            [RequestParameters.Page, RequestParameters.PageSize, RequestParameters.IncludeDeleted],
            [
                RequestParameters.IntegerExpectedMessage,
                RequestParameters.IntegerExpectedMessage,
                RequestParameters.BooleanExpectedMessage,
            ]
        },
        {
            WithQuery(
                RecordsPath,
                (RequestParameters.Page, Text(RecordQueryRules.MinPage - 1)),
                (RequestParameters.PageSize, Text(RecordQueryRules.MaxPageSize + 1))),
            [RequestParameters.Page, RequestParameters.PageSize],
            [RecordQueryRules.PageOutOfRangeMessage, RecordQueryRules.PageSizeOutOfRangeMessage]
        },
        {
            WithQuery(RecordsPath, (RequestParameters.Page, "-1"), (RequestParameters.PageSize, "x")),
            [RequestParameters.Page, RequestParameters.PageSize],
            [RecordQueryRules.PageOutOfRangeMessage, RequestParameters.IntegerExpectedMessage]
        },
        {
            WithQuery(
                RecordsPath,
                (RequestParameters.Page, string.Empty),
                (RequestParameters.Page, Text(RecordQueryRules.MinPage)),
                (RequestParameters.PageSize, Text(RecordQueryRules.DefaultPageSize)),
                (RequestParameters.IncludeDeleted, "true"),
                (RequestParameters.IncludeDeleted, null)),
            [RequestParameters.Page, RequestParameters.IncludeDeleted],
            [RequestParameters.IntegerExpectedMessage, RequestParameters.BooleanExpectedMessage]
        },
        {
            AtPath("abc") + IncludeDeletedQuery + "maybe",
            [RequestParameters.Position, RequestParameters.IncludeDeleted],
            [RequestParameters.IntegerExpectedMessage, RequestParameters.BooleanExpectedMessage]
        },
        {
            AtPath("1%20") + IncludeDeletedQuery,
            [RequestParameters.Position, RequestParameters.IncludeDeleted],
            [RequestParameters.IntegerExpectedMessage, RequestParameters.BooleanExpectedMessage]
        },
        {
            RecordsPathOf(UnknownViewId) + PageQuery + "x",
            [RequestParameters.Page],
            [RequestParameters.IntegerExpectedMessage]
        },
        {
            RecordsPathOf(UnknownViewId) + "/count" + IncludeDeletedQuery,
            [RequestParameters.IncludeDeleted],
            [RequestParameters.BooleanExpectedMessage]
        },
    };

    /// <summary>
    /// Rejected requests carrying sentinels: <c>{0}</c> is a text sentinel and <c>{1}</c> a nine-digit sentinel.
    /// </summary>
    public static TheoryData<string> SentinelRequests => new()
    {
        RecordsPath + PageQuery + "{0}",
        RecordsPath + PageQuery + "-{1}",
        RecordsPath + PageSizeQuery + "{0}",
        RecordsPath + PageSizeQuery + "{1}",
        RecordsPath + IncludeDeletedQuery + "{0}",
        WithQuery(
            RecordsPath,
            (RequestParameters.Page, "{0}"),
            (RequestParameters.PageSize, "{1}"),
            (RequestParameters.IncludeDeleted, "{0}")),
        CountPath + IncludeDeletedQuery + "{0}",
        AtPath("{0}"),
        AtPath("{1}{0}") + IncludeDeletedQuery + "{0}",
        AtPath("1") + IncludeDeletedQuery + "{0}",
        PositionPath + IncludeDeletedQuery + "{0}",
    };

    // The Int32 bounds and the integers just outside them, as sent.
    private static string Int32Max => Text(int.MaxValue);

    private static string Int32Min => Text(int.MinValue);

    private static string AboveInt32 => Text((long)int.MaxValue + 1L);

    private static string BelowInt32 => Text((long)int.MinValue - 1L);

    private static string RecordsPath => RecordsPathOf(FakeForms.TwoPageViewId);

    private static string EmptyRecordsPath => RecordsPathOf(FakeForms.EmptyViewId);

    private static string CountPath => RecordsPath + "/count";

    private static string PositionPath => $"{RecordsPath}/{FirstActiveId}/position";

    // The four routes that take includeDeleted: the list, count, at/{position} and the position endpoint.
    private static string[] IncludeDeletedRoutes => [RecordsPath, CountPath, AtPath("1"), PositionPath];

    private HttpClient Client => client ?? throw new InvalidOperationException("The test host is not started.");

    /// <summary>Seeds the store and starts the test server.</summary>
    public async Task InitializeAsync()
    {
        store.SeedRecord(FakeForms.TwoPageViewId, LastName("Alpha"), globalRecordId: FirstActiveId);
        store.SeedRecord(FakeForms.TwoPageViewId, LastName(FilterMatch), globalRecordId: SecondActiveId);
        store.SeedRecord(
            FakeForms.TwoPageViewId,
            LastName("Charlie"),
            recStatus: RecordQueryRules.DeletedRecStatus,
            globalRecordId: DeletedId);
        store.SeedRecord(FakeForms.TwoPageViewId, LastName("Delta"), globalRecordId: ThirdActiveId);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddApiJson();
        builder.Services.AddSingleton<IDataStore>(store);
        builder.Services.AddSingleton<ICheckCodeEngine, CheckCodeEngine>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<SaveStampProvider>();
        builder.Services.AddScoped<FormService>();
        builder.Services.AddScoped<RecordService>();
        builder.Services.AddEndpointsApiExplorer();

        app = builder.Build();
        app.MapRecordEndpoints();
        await app.StartAsync();
        client = app.GetTestClient();
    }

    /// <summary>Stops the test server.</summary>
    public async Task DisposeAsync()
    {
        client?.Dispose();
        if (app is not null)
        {
            await app.DisposeAsync();
        }
    }

    // AAP 0.3.4.1: a missing page is 1 and a missing pageSize is 50; any sent value that is not a 32-bit integer,
    // the empty value, the bare key and a key sent more than once included, is a 400 under errors[<parameter>] and
    // nothing else.
    [Theory]
    [MemberData(nameof(MalformedPagingValues))]
    public async Task MalformedPagingValueIsRejectedBeforeAnyStoreCall(string parameter, string value)
    {
        var errors = await GetRejectedAsync($"{RecordsPath}?{parameter}{value}");

        AssertErrors(errors, [parameter], [RequestParameters.IntegerExpectedMessage]);
    }

    // AAP 0.3.4.1 and C-19: only an absent includeDeleted is false; any other value but true or false ignoring case,
    // the empty value, the bare key and a key sent more than once included, is a 400 on every route that takes the
    // flag, count included.
    [Theory]
    [MemberData(nameof(InvalidIncludeDeletedRequests))]
    public async Task InvalidIncludeDeletedIsRejectedBeforeAnyStoreCall(string path, string value)
    {
        var errors = await GetRejectedAsync($"{path}?{RequestParameters.IncludeDeleted}{value}");

        AssertErrors(errors, [RequestParameters.IncludeDeleted], [RequestParameters.BooleanExpectedMessage]);
    }

    [Theory]
    [MemberData(nameof(AcceptedIncludeDeletedRequests))]
    public async Task AcceptedIncludeDeletedReachesTheService(string path, string query) =>
        await GetOkAsync(path + query);

    [Theory]
    [MemberData(nameof(CountSelections))]
    public async Task IncludeDeletedSelectsTheCountedSet(string query, int expectedCount)
    {
        var body = await GetOkAsync(CountPath + query);

        Assert.Equal(expectedCount, body.GetProperty("count").GetInt32());
    }

    // AAP 0.3.4.1: {position} is parsed as an invariant 32-bit integer, and anything else is a 400 under
    // errors["position"] before the form lookup.
    [Theory]
    [MemberData(nameof(MalformedPositions))]
    public async Task MalformedPositionIsRejectedBeforeAnyStoreCall(string position)
    {
        var errors = await GetRejectedAsync(AtPath(position));

        AssertErrors(errors, [RequestParameters.Position], [RequestParameters.IntegerExpectedMessage]);
    }

    [Theory]
    [MemberData(nameof(OpenedPositions))]
    public async Task IntegerPositionInTheSetOpensItsRecord(string positionAndQuery, string expectedId)
    {
        var body = await GetOkAsync(AtPath(positionAndQuery));

        Assert.Equal(expectedId, body.GetProperty("globalRecordId").GetString());
    }

    // AAP 0.3.4.1: an integer position outside 1..count coerces, and the record service answers 404.
    [Theory]
    [MemberData(nameof(PositionsOutsideTheSet))]
    public async Task IntegerPositionOutsideTheSetIsTheServiceNotFound(string positionAndQuery)
    {
        store.ClearCalls();
        using var response = await Client.GetAsync(AtPath(positionAndQuery));
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ApiProblems.ProblemContentType, response.Content.Headers.ContentType?.MediaType);
        var body = ParseProblem(text, StatusCodes.Status404NotFound);
        Assert.Equal(ApiProblems.PositionNotFoundDetail, body.GetProperty("detail").GetString());
        Assert.False(body.TryGetProperty("errors", out _), text);
        Assert.Contains(store.Calls, call => call.Method == nameof(IDataStore.GetFormMetadataAsync));
    }

    // AAP 0.3.4.1 and 0.7.1: page < 1 and pageSize outside 1-200 are 400s with the range message, not the
    // integer message.
    [Theory]
    [MemberData(nameof(OutOfRangePagingValues))]
    public async Task OutOfRangePagingValueGetsItsRangeMessage(string parameter, string value, string message)
    {
        var errors = await GetRejectedAsync($"{RecordsPath}?{parameter}={value}");

        AssertErrors(errors, [parameter], [message]);
    }

    // AAP 0.3.4.1: the defaults apply to omitted parameters, and the largest admitted page, 2147483647, returns
    // items: [] with the correct total.
    [Theory]
    [MemberData(nameof(AcceptedListPages))]
    public async Task AcceptedPagingReturnsThePage(string uri, int page, int pageSize, int total)
    {
        var body = await GetOkAsync(uri);

        Assert.Equal(page, body.GetProperty("page").GetInt32());
        Assert.Equal(pageSize, body.GetProperty("pageSize").GetInt32());
        Assert.Equal(total, body.GetProperty("total").GetInt32());

        var offset = ((long)page - 1L) * pageSize;
        var expectedItems = offset >= total ? 0 : (int)Math.Min(pageSize, total - offset);
        Assert.Equal(expectedItems, body.GetProperty("items").GetArrayLength());
    }

    // AAP 0.3.4.1: an absent, empty or whitespace q means no filter.
    [Theory]
    [MemberData(nameof(ListFilters))]
    public async Task BlankFilterMeansNoFilter(string query, int total)
    {
        var body = await GetOkAsync(RecordsPath + query);

        Assert.Equal(total, body.GetProperty("total").GetInt32());
    }

    // AAP 0.3.4.2: every bad parameter of one request is reported in one field-level 400, each key with exactly its
    // own fixed message, ahead of the form lookup.
    [Theory]
    [MemberData(nameof(MergedRejections))]
    public async Task EveryBadParameterIsReportedInOneRejection(string uri, string[] keys, string[] messages)
    {
        var errors = await GetRejectedAsync(uri);

        AssertErrors(errors, keys, messages);
    }

    // AAP 0.9.3: an error body never carries a query-string or route value.
    [Theory]
    [MemberData(nameof(SentinelRequests))]
    public async Task RejectionNeverEchoesTheSubmittedValue(string template)
    {
        var text = "T" + Guid.NewGuid().ToString("N");
        var digits = Random.Shared.Next(100_000_000, 1_000_000_000).ToString(CultureInfo.InvariantCulture);

        store.ClearCalls();
        using var response = await Client.GetAsync(string.Format(CultureInfo.InvariantCulture, template, text, digits));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(store.Calls);
        Assert.DoesNotContain(text, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(digits, body, StringComparison.Ordinal);
    }

    // AAP 0.3.4.1: includeDeleted, page and pageSize stay optional query parameters in the API description the OpenAPI
    // document is generated from, whatever type binds their occurrences.
    [Fact]
    public void OccurrenceBoundParametersAreOptionalQueryParameters()
    {
        var host = app ?? throw new InvalidOperationException("The test host is not started.");
        var described = host.Services.GetRequiredService<IApiDescriptionGroupCollectionProvider>()
            .ApiDescriptionGroups.Items
            .SelectMany(group => group.Items)
            .SelectMany(description => description.ParameterDescriptions.Select(parameter =>
                (Key: EndpointName(description) + "/" + parameter.Name, Parameter: parameter)))
            .Where(entry => OccurrenceBoundParameters.Contains(entry.Key, StringComparer.Ordinal))
            .ToList();

        Assert.Equal(
            OccurrenceBoundParameters.Order(StringComparer.Ordinal),
            described.Select(entry => entry.Key).Order(StringComparer.Ordinal));
        Assert.All(described, entry =>
        {
            Assert.Equal(BindingSource.Query, entry.Parameter.Source);
            Assert.False(entry.Parameter.IsRequired, entry.Key);
        });
    }

    private static string EndpointName(ApiDescription description) =>
        description.ActionDescriptor.EndpointMetadata.OfType<IEndpointNameMetadata>().Single().EndpointName;

    // The query values, after the key, that send parameter more than once, from two values it accepts when sent once.
    private static IEnumerable<string> RepeatedQueryValues(string parameter, string accepted, string otherAccepted) =>
        RepeatedQueryValueTemplates.Select(template =>
            string.Format(CultureInfo.InvariantCulture, template, parameter, accepted, otherAccepted));

    private static string RecordsPathOf(int viewId) => Invariant($"/api/forms/{viewId}/records");

    private static string AtPath(string positionAndQuery) => $"{RecordsPath}/at/{positionAndQuery}";

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);

    // path?name=value&..., where a null value sends the bare key.
    private static string WithQuery(string path, params (string Name, string? Value)[] parameters) =>
        path + "?" + string.Join(
            '&',
            parameters.Select(parameter =>
                parameter.Value is null ? parameter.Name : parameter.Name + "=" + parameter.Value));

    private static IReadOnlyDictionary<string, FieldValue> LastName(string value) =>
        new Dictionary<string, FieldValue>(StringComparer.Ordinal) { ["LastName"] = FieldValue.FromText(value) };

    private static JsonElement ParseProblem(string text, int status)
    {
        using var document = JsonDocument.Parse(text);
        return ApiTestClient.AssertProblemDetails(document.RootElement.Clone(), status);
    }

    // Asserts that errors holds exactly the keys given, each with exactly its one message.
    private static void AssertErrors(JsonElement errors, string[] keys, string[] messages)
    {
        Assert.Equal(keys.Length, messages.Length);
        Assert.Equal(JsonValueKind.Object, errors.ValueKind);

        var actual = errors.EnumerateObject().ToDictionary(
            entry => entry.Name,
            entry => entry.Value.EnumerateArray().Select(message => message.GetString()).ToArray(),
            StringComparer.Ordinal);
        Assert.Equal(keys.Order(StringComparer.Ordinal), actual.Keys.Order(StringComparer.Ordinal));
        for (var index = 0; index < keys.Length; index++)
        {
            Assert.Equal(new string?[] { messages[index] }, actual[keys[index]]);
        }
    }

    // Sends a GET that coercion must reject: a field-level 400 ProblemDetails, and no store call at all.
    private async Task<JsonElement> GetRejectedAsync(string uri)
    {
        store.ClearCalls();
        using var response = await Client.GetAsync(uri);
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiProblems.ProblemContentType, response.Content.Headers.ContentType?.MediaType);
        Assert.Empty(store.Calls);

        var body = ParseProblem(text, StatusCodes.Status400BadRequest);
        Assert.Equal(ApiProblems.ValidationFailedDetail, body.GetProperty("detail").GetString());
        return body.GetProperty("errors").Clone();
    }

    // Sends a GET that must succeed after reaching the store, and returns its body.
    private async Task<JsonElement> GetOkAsync(string uri)
    {
        store.ClearCalls();
        using var response = await Client.GetAsync(uri);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEmpty(store.Calls);
        return await ApiTestClient.ReadJsonElementAsync(response);
    }
}
