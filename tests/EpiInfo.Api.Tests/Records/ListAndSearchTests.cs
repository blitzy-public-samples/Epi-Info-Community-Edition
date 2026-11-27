using System.Data;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
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
using EpiInfo.Tests.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace EpiInfo.Api.Tests.Records;

/// <summary>
/// The binding contract of the Find search body, <c>POST /api/forms/{viewId}/records/search</c> (AAP 0.3.4.1),
/// under the API's own JSON settings, and the member check <see cref="SearchRequestShape"/> that judges the body's
/// members before binding.
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

    // The placeholder a body holds where its case puts a fresh sentinel.
    private const string SentinelToken = "%SENTINEL%";

    // The detail of a request-level 400 for a repeated member, written out rather than read from the product.
    private const string ExpectedRepeatsMemberDetail = "request body names a member more than once";

    // The one criteria message of an array of more than six items of which any has a problem, written out the same way.
    private const string ExpectedOverLongCriteriaEntry =
        "criteria: criteria must be an array of at most 6 {field, value} objects.";

    // The most bytes the serialized field-level 400 for an over-long criteria array may take, whatever the body's size.
    private const int MaxOverLongCriteriaProblemBytes = 512;

    // The largest body the API reads, Kestrel:Limits:MaxRequestBodySize.
    private const int BodyLimitBytes = 1_048_576;

    /// <summary>
    /// Search bodies whose every member has a JSON kind the request binds, as a caller may send them: absent,
    /// <c>null</c> and empty members, criteria left to the parser, case variants of every member name, and unknown
    /// members of any kind.
    /// </summary>
    public static TheoryData<string> BodiesWithBindableMembers => new()
    {
        "{}",
        """{"criteria":null,"includeDeleted":null,"excludeGlobalRecordId":null}""",
        """{"criteria":[],"includeDeleted":true,"excludeGlobalRecordId":""}""",
        """{"criteria":[null,{"value":null},{"field":null,"value":"x"}],"includeDeleted":false}""",
        """{"criteria":[{"field":"Age","value":-1.5e3},{"field":"Ill","value":true},{"field":"LastName","value":""}]}""",
        """{"Criteria":[{"FIELD":"LastName","Value":"Sm*"}],"INCLUDEDELETED":true,"ExcludeGlobalRecordID":"id"}""",
        """{"other":{"a":[1e400]},"criteria":[{"field":"x","value":"y","extra":{"z":[]},"EXTRA2":1e400}]}""",

        // More than six items, all of a bindable kind, a repeated field included: the field limit is the parser's.
        """{"criteria":[{"field":"A","value":1},{"field":"B","value":"x"},{"field":"C","value":true},{"field":"D","value":null},null,{"value":"y"},{"field":null,"value":-2},{"field":"A","value":"again"}]}""",
    };

    /// <summary>
    /// Search bodies with members of the wrong JSON kind, each with every <c>errors</c> entry it must get, in order,
    /// as <c>key: message</c>. Where a body holds <c>%SENTINEL%</c>, each case puts a fresh sentinel there.
    /// </summary>
    public static TheoryData<string, string[]> BodiesWithWronglyTypedMembers => new()
    {
        {
            """{"criteria":[{"field":"RecStatus","value":"1"}],"includeDeleted":"true"}""",
            ["includeDeleted: includeDeleted must be true, false or null."]
        },
        {
            """{"includeDeleted":1}""",
            ["includeDeleted: includeDeleted must be true, false or null."]
        },
        {
            """{"IncludeDeleted":"' OR 1=1-- %SENTINEL%"}""",
            ["includeDeleted: includeDeleted must be true, false or null."]
        },
        {
            """{"includeDeleted":{"%SENTINEL%":true}}""",
            ["includeDeleted: includeDeleted must be true, false or null."]
        },
        {
            """{"excludeGlobalRecordId":1}""",
            ["excludeGlobalRecordId: excludeGlobalRecordId must be a string or null."]
        },
        {
            """{"EXCLUDEGLOBALRECORDID":["%SENTINEL%"]}""",
            ["excludeGlobalRecordId: excludeGlobalRecordId must be a string or null."]
        },
        {
            """{"excludeGlobalRecordId":false}""",
            ["excludeGlobalRecordId: excludeGlobalRecordId must be a string or null."]
        },
        {
            """{"criteria":{"field":"LastName","value":"%SENTINEL%"}}""",
            ["criteria: criteria must be an array of {field, value} objects."]
        },
        {
            """{"criteria":"%SENTINEL%"}""",
            ["criteria: criteria must be an array of {field, value} objects."]
        },
        {
            """{"criteria":[{"field":5,"value":"%SENTINEL%"}]}""",
            ["criteria: criteria[0] field must be a string."]
        },
        {
            """{"criteria":[{"Field":["%SENTINEL%"],"value":"x"}]}""",
            ["criteria: criteria[0] field must be a string."]
        },
        {
            """{"criteria":[{"field":"LastName","value":{"%SENTINEL%":1}}]}""",
            ["criteria: criteria[0] value must be a string, number, boolean or null."]
        },
        {
            """{"criteria":[{"field":"LastName","VALUE":["%SENTINEL%"]}]}""",
            ["criteria: criteria[0] value must be a string, number, boolean or null."]
        },
        {
            """{"criteria":[{"field":"Age","value":1e400}]}""",
            ["criteria: criteria[0] value must be a finite number."]
        },
        {
            """{"criteria":[{"field":"Age","value":-1e400}]}""",
            ["criteria: criteria[0] value must be a finite number."]
        },
        {
            """{"criteria":[{"field":"LastName"}]}""",
            ["criteria: criteria[0] needs a value, which may be null."]
        },
        {
            """{"criteria":[{"field":"LastName","value":"x"},"%SENTINEL%",7]}""",
            [
                "criteria: criteria[1] must be an object with field and value.",
                "criteria: criteria[2] must be an object with field and value.",
            ]
        },

        // Every error of one body, members in body order and criteria in criterion order; a null criterion and a
        // null or absent field are the parser's, so they add nothing here.
        {
            """{"excludeGlobalRecordId":1,"criteria":[[1],{"value":[2],"field":3},null,{"field":null},{"field":"Age","value":1e999}],"includeDeleted":"%SENTINEL%"}""",
            [
                "excludeGlobalRecordId: excludeGlobalRecordId must be a string or null.",
                "criteria: criteria[0] must be an object with field and value.",
                "criteria: criteria[1] value must be a string, number, boolean or null.",
                "criteria: criteria[1] field must be a string.",
                "criteria: criteria[3] needs a value, which may be null.",
                "criteria: criteria[4] value must be a finite number.",
                "includeDeleted: includeDeleted must be true, false or null.",
            ]
        },

        // Six items, the field limit, keep a message per problem by its item's index.
        {
            """{"criteria":[{"field":"A","value":1},"%SENTINEL%",{"field":"B"},{"field":"C","value":"x"},{"field":[],"value":{}},{"field":"D","value":1e400}]}""",
            [
                "criteria: criteria[1] must be an object with field and value.",
                "criteria: criteria[2] needs a value, which may be null.",
                "criteria: criteria[4] field must be a string.",
                "criteria: criteria[4] value must be a string, number, boolean or null.",
                "criteria: criteria[5] value must be a finite number.",
            ]
        },

        // Past six items every item is still checked, and any problem among them is the one fixed message, in the
        // criteria member's place among the body's errors.
        {
            """{"criteria":[{"field":"A","value":1},{"field":"B","value":2},{"field":"C","value":3},{"field":"D","value":4},{"field":"E","value":5},{"field":"F","value":6},{"field":"G","value":["%SENTINEL%"]}]}""",
            [ExpectedOverLongCriteriaEntry]
        },
        {
            """{"criteria":["%SENTINEL%",null,null,null,null,null,null]}""",
            [ExpectedOverLongCriteriaEntry]
        },
        {
            """{"criteria":[null,null,null,null,null,null,{"field":"LastName"}]}""",
            [ExpectedOverLongCriteriaEntry]
        },
        {
            """{"includeDeleted":0,"criteria":[1,{"field":2},[3],{"value":{}},5,6,7,{"field":"x","value":-1e400}],"excludeGlobalRecordId":{"%SENTINEL%":1}}""",
            [
                "includeDeleted: includeDeleted must be true, false or null.",
                ExpectedOverLongCriteriaEntry,
                "excludeGlobalRecordId: excludeGlobalRecordId must be a string or null.",
            ]
        },
    };

    /// <summary>
    /// Search bodies in which a criterion object names a member more than once, exactly or differing only by letter
    /// case, with errors found earlier in the body or none.
    /// </summary>
    public static TheoryData<string> BodiesWithARepeatedCriterionMember => new()
    {
        """{"criteria":[{"field":"LastName","field":"Age","value":"%SENTINEL%"}]}""",
        """{"criteria":[{"field":"LastName","value":"%SENTINEL%","Value":"b"}]}""",
        """{"criteria":[{"field":"Age","value":1},{"other":"%SENTINEL%","OTHER":2,"field":"LastName","value":null}]}""",
        """{"includeDeleted":"x","criteria":[{"field":5,"value":[1]},{"FIELD":"a","field":"%SENTINEL%","value":1}]}""",

        // Past six items, with problems found before the repeat or none.
        """{"criteria":[1,2,3,4,5,6,7,{"field":"a","FIELD":"%SENTINEL%","value":1},8]}""",
        """{"criteria":[{"field":"A","value":1},{"field":"B","value":2},{"field":"C","value":3},{"field":"D","value":4},{"field":"E","value":5},{"field":"F","value":6},{"value":"%SENTINEL%","Value":7,"field":"G"}]}""",
    };

    // The member check accepts every body binding can read, and the body the API's own options write for a request,
    // so the wire names it matches are the ones binding uses.
    [Theory]
    [MemberData(nameof(BodiesWithBindableMembers))]
    public void MemberCheckAcceptsEveryBindableBody(string json)
    {
        using var document = JsonDocument.Parse(json);

        Assert.Null(SearchRequestShape.Check(document.RootElement));
    }

    [Theory]
    [MemberData(nameof(SerializerOptionSources))]
    public void MemberCheckAcceptsTheBodyTheApiOptionsWrite(string source)
    {
        var options = CreateOptions(source);
        var request = new SearchRequest(
            [new SearchCriterion("LastName", FieldValue.FromText("Sm*")), new SearchCriterion("Age", FieldValue.Null)],
            IncludeDeleted: true,
            ExcludeGlobalRecordId: "id");
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(request, options));

        Assert.Null(SearchRequestShape.Check(document.RootElement));
    }

    // A well-formed body whose members have the wrong JSON kind is one field-level 400 that names each problem under
    // its member's wire name, in body order, with fixed messages that quote nothing submitted.
    [Theory]
    [MemberData(nameof(BodiesWithWronglyTypedMembers))]
    public void MemberCheckReportsEveryWronglyTypedMember(string template, string[] expected)
    {
        var sentinel = "SENTINEL-" + Guid.NewGuid().ToString("N");
        using var document = JsonDocument.Parse(template.Replace(SentinelToken, sentinel, StringComparison.Ordinal));

        var problem = ProblemOf(SearchRequestShape.Check(document.RootElement));

        var validation = Assert.IsType<HttpValidationProblemDetails>(problem);
        Assert.Equal(StatusCodes.Status400BadRequest, validation.Status);
        Assert.Equal("one or more values are invalid", validation.Detail);
        var entries = validation.Errors
            .SelectMany(entry => entry.Value.Select(message => $"{entry.Key}: {message}"))
            .ToArray();
        Assert.Equal(expected, entries);
        Assert.False(
            entries.Any(entry => entry.Contains(sentinel, StringComparison.Ordinal)),
            "A member message quotes the submitted body.");
    }

    // A criteria array of more than six items, each item the one given, up to a body just under the body limit: the
    // check judges every item and answers the one fixed criteria message, so the serialized 400 stays within a fixed
    // size however many items have a problem.
    [Theory]
    [InlineData("1", 7)]
    [InlineData("1", 10_000)]
    [InlineData("1", 524_000)]
    [InlineData("""{"field":5,"value":[1]}""", 43_000)]
    [InlineData("""{"field":"LastName"}""", 49_000)]
    public void OverLongCriteriaWithProblemsAreOneBoundedMessage(string item, int count)
    {
        var json = "{\"criteria\":[" + string.Join(',', Enumerable.Repeat(item, count)) + "]}";
        Assert.InRange(json.Length, 1, BodyLimitBytes);
        using var document = JsonDocument.Parse(json);

        var problem = ProblemOf(SearchRequestShape.Check(document.RootElement));

        var validation = Assert.IsType<HttpValidationProblemDetails>(problem);
        Assert.Equal(StatusCodes.Status400BadRequest, validation.Status);
        var entries = validation.Errors
            .SelectMany(entry => entry.Value.Select(message => $"{entry.Key}: {message}"))
            .ToArray();
        Assert.Equal(new[] { ExpectedOverLongCriteriaEntry }, entries);
        var serialized = JsonSerializer.SerializeToUtf8Bytes(validation, JsonSetup.CreateSerializerOptions());
        Assert.InRange(serialized.Length, 1, MaxOverLongCriteriaProblemBytes);
    }

    // A criterion object that names a member more than once is the request-level 400 of a repeated member at once,
    // whatever errors the check found before it, with no errors map and nothing quoted.
    [Theory]
    [MemberData(nameof(BodiesWithARepeatedCriterionMember))]
    public void RepeatedCriterionMemberIsTheRequestLevelRepeatProblem(string template)
    {
        var sentinel = "SENTINEL-" + Guid.NewGuid().ToString("N");
        using var document = JsonDocument.Parse(template.Replace(SentinelToken, sentinel, StringComparison.Ordinal));

        var problem = ProblemOf(SearchRequestShape.Check(document.RootElement));

        Assert.IsNotType<HttpValidationProblemDetails>(problem);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.Status);
        Assert.Equal(ExpectedRepeatsMemberDetail, problem.Detail);
    }

    // A criterion member name that cannot be decoded is "malformed JSON": the 400 BadHttpRequestException whose inner
    // exception is a JsonException, with a fixed message.
    [Theory]
    [InlineData("""{"criteria":[{"field":"LastName","value":"x","\uD800":1}]}""")]
    [InlineData("""{"criteria":[{"\uDC00":1,"value":"x"}]}""")]
    [InlineData("""{"criteria":[1,1,1,1,1,1,1,{"value":"x","\uD800":1}]}""")]
    public void UndecodableCriterionMemberNameIsMalformedJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var exception = Assert.Throws<BadHttpRequestException>(() => SearchRequestShape.Check(root));

        Assert.Equal(StatusCodes.Status400BadRequest, exception.StatusCode);
        var inner = Assert.IsType<JsonException>(exception.InnerException);
        Assert.DoesNotContain(@"\u", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(@"\u", inner.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"x\"")]
    public void MemberCheckRefusesABodyThatIsNotAnObject(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Throws<ArgumentException>(() => SearchRequestShape.Check(root));
    }

    // The body a problem result writes. ProblemDetails is named in full: the MVC namespace also holds a JsonOptions.
    private static Microsoft.AspNetCore.Mvc.ProblemDetails ProblemOf(IResult? result)
    {
        Assert.NotNull(result);
        var problem = Assert.IsAssignableFrom<IValueHttpResult<Microsoft.AspNetCore.Mvc.ProblemDetails>>(result).Value;
        Assert.NotNull(problem);
        return problem;
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
/// The bounds of the records-list filter and of Find (AAP 0.3.4.1, 0.3.4.2, 0.3.4.4). The filter has no length bound.
/// Find has four, each one fixed message before the store is asked to search, whatever the request holds: at most six
/// criteria and, over the whole search, at most 6,554 alternatives other than equality and <c>IS NULL</c> and at most
/// 32,767 UTF-16 code units of wildcard pattern text, each under <c>criteria</c>; and at most 32,767 code units in each
/// string value, one legacy Find text box, under the field. Within them, a filter or a wildcard pattern past SQL
/// Server's 4,000-code-unit <c>LIKE</c> pattern limit coerces and reaches the store, which matches it as the SQL
/// builders do, a <c>[...]</c> set longer than that limit included, and so does every alternative, equality text of
/// several values together included. The one other 400 is a pattern holding such a set that is unclosed or that no cut
/// keeping its members and negation divides, which no SQL Server construct can evaluate, and the in-memory store keeps
/// the SQL builders' argument contract for it.
/// </summary>
/// <remarks>
/// The class needs no database: it carries no <c>Integration</c> trait and joins no database collection, so
/// <c>--filter "Category!=Integration"</c> selects it. It calls <see cref="RequestParameters"/> and
/// <see cref="RecordService"/> directly, the service over <see cref="FakeDataStore"/>, and the store itself, and reads
/// the published Find array bounds from the OpenAPI document of an <see cref="ApiFactory"/> host with no connection
/// string.
/// </remarks>
public sealed class ListAndSearchBoundsTests
{
    private const string Sentinel = "SENTINEL-5c2d";

    private const string AlternativesKind = "alternatives";

    private const string PatternKind = "pattern";

    private const string RunKind = "run";

    private const string BracketKind = "bracket";

    private const string SetKind = "set";

    private const string UnclosedSetKind = "unclosed set";

    private const string RangeChainSetKind = "range chain set";

    // The view id of TwoMultilineForm, which collides with no seeded or other synthetic form.
    private const int TwoMultilineViewId = 9120;

    // A LastName value of 4,000 characters, the most its nvarchar(4000) column stores, for the filter cases.
    private static readonly string LongLastName = "Q" + new string('x', 3998) + "Z";

    /// <summary>
    /// Grid filters past SQL Server's <c>LIKE</c> pattern limit, whose contains patterns run to 4,002 units and more,
    /// with whether each finds the record whose LastName is <see cref="LongLastName"/>: the whole value in another
    /// case, an inner part of it, and filters that differ from it in one character or hold only pattern characters.
    /// </summary>
    public static TheoryData<string, bool> LongFilterCases => new()
    {
        { LongLastName.ToLowerInvariant(), true },
        { new string('X', 3998) + "z", true },
        { LongLastName[..^1] + "Y", false },
        { new string('x', 3999), false },
        { new string('%', 2000), false },
        { new string('_', 3999), false },
    };

    // Any nonblank q coerces untrimmed whatever its length, with its contains pattern within 4,000 units (3,998 plain
    // characters, 1,999 escaped ones) or past them (3,999 plain, 2,000 escaped and more); a blank q, however long, is
    // no filter.
    [Theory]
    [InlineData("a", 3998)]
    [InlineData("a", 3999)]
    [InlineData("%", 1999)]
    [InlineData("%", 2000)]
    [InlineData("\\", 4001)]
    [InlineData("_", 9000)]
    [InlineData(" ", 5000)]
    public void ListQuery_FilterOfAnyLength_Coerces(string unit, int repeat)
    {
        var q = string.Concat(Enumerable.Repeat(unit, repeat));

        Assert.True(RequestParameters.TryParseListQuery(q, null, null, null, out var query, out var problem));

        Assert.Null(problem);
        Assert.Equal(string.IsNullOrWhiteSpace(q) ? null : q, query.Filter);
    }

    // A long q beside a bad page: the single 400 reports the page alone.
    [Fact]
    public void ListQuery_LongFilterBesideABadPage_IsA400OnThePageOnly()
    {
        Assert.False(RequestParameters.TryParseListQuery(
            new string('a', 3999), null, ["0"], null, out var query, out var problem));

        Assert.Null(query);
        Assert.Equal("page", Assert.Single(ValidationErrors(problem).Keys));
    }

    // Every alternative within the bounds reaches the store, which binds a large set packed, 1,500 sentinel
    // alternatives (30,386 code units) among them, and so does a wildcard pattern of any length the bounds admit:
    // 4,000 characters, the longest SQL Server matches in one LIKE, and longer ones up to the whole bound of one value
    // with no run of '*', with a run of '*', with a set before a run of '*', which the parser leaves as written, or
    // with a set of 4,000 characters or more, which the search builder cuts into shorter sets.
    [Theory]
    [InlineData(AlternativesKind, 1500)]
    [InlineData(PatternKind, 4000)]
    [InlineData(PatternKind, 4001)]
    [InlineData(PatternKind, 9000)]
    [InlineData(PatternKind, RecordQueryRules.MaxSearchValueLength)]
    [InlineData(RunKind, 4001)]
    [InlineData(BracketKind, 4001)]
    [InlineData(SetKind, 4000)]
    [InlineData(SetKind, 4001)]
    [InlineData(SetKind, 9000)]
    public async Task Search_WithinTheBounds_ReachesTheStore(string kind, int size)
    {
        var store = FakeDataStore.WithStandardForms();

        var result = await CreateService(store).SearchAsync(
            FakeForms.TwoPageViewId, LastNameSearch(kind, size), CancellationToken.None);

        var ok = Assert.IsType<Ok<SearchResponse>>(result);
        Assert.NotNull(ok.Value);
        Assert.Contains(store.Calls, call => call.Method == nameof(IDataStore.SearchAsync));
    }

    // A pattern holding a set longer than 4,000 characters that is unclosed, or that no cut keeping its members divides,
    // which no SQL Server construct evaluates, is the one fixed field-level 400 on the field, echoing nothing, and the
    // store is never asked to search.
    [Theory]
    [InlineData(UnclosedSetKind)]
    [InlineData(RangeChainSetKind)]
    public async Task Search_UnsearchableSet_IsA400BeforeTheStore(string kind)
    {
        var store = FakeDataStore.WithStandardForms();

        var result = await CreateService(store).SearchAsync(
            FakeForms.TwoPageViewId, LastNameSearch(kind, 4001), CancellationToken.None);

        var errors = ValidationErrors(result);
        Assert.Equal(new[] { "LastName" }, errors.Keys.ToArray());
        Assert.Equal(
            "LastName search patterns cannot hold a [...] set longer than 4000 characters that has no closing ] or"
            + " that cannot be cut into sets of at most 3998 characters that keep its members and negation.",
            Assert.Single(errors["LastName"]));
        Assert.DoesNotContain(store.Calls, call => call.Method == nameof(IDataStore.SearchAsync));
    }

    // Over HTTP, each bound of Find is one fixed message, whatever the request holds, before the store is asked to
    // search: a criteria list longer than six, of 7 or 100,000 null entries or of 20,000 unknown names (a body of about
    // 900 KB, within the 1 MiB cap), under criteria; a string value one code unit past the 32,767 of one value, under
    // the field; and, under criteria, 6,555 patterns over two fields, one past the 6,554 alternatives other than
    // equality and IS NULL, and two patterns of 32,768 code units together, one past the bound of pattern text. The
    // body stays under 512 bytes, it echoes nothing, and the store is never asked to search.
    [Theory]
    [InlineData("null entries", 7)]
    [InlineData("null entries", 100_000)]
    [InlineData("unknown names", 20_000)]
    [InlineData("value past its bound", RecordQueryRules.MaxSearchValueLength + 1)]
    [InlineData("alternatives past the bound", RecordQueryRules.MaxRowComparedAlternatives + 1)]
    [InlineData("patterns past the bound", RecordQueryRules.MaxSearchPatternTextLength + 1)]
    public async Task Search_PastABound_IsOneFixedMessageBeforeTheStore(string shape, int count)
    {
        const int MaxProblemBodyBytes = 512;
        var store = FakeDataStore.WithStandardForms();
        await using var factory = new ApiFactory(ApiFactoryOptions.ForFake(store));
        using HttpClient client = factory.CreateClient();

        // The share of count the first of two fields takes, the second taking the rest.
        var first = (count / 2) + (count % 2);
        var json = shape switch
        {
            "null entries" => "{\"criteria\":[" + string.Join(",", Enumerable.Repeat("null", count)) + "]}",
            "unknown names" => JsonSerializer.Serialize(new
            {
                criteria = Enumerable.Range(0, count).Select(index => new
                {
                    field = Sentinel + index.ToString(CultureInfo.InvariantCulture),
                    value = "x",
                }),
            }),
            "value past its bound" => JsonSerializer.Serialize(new
            {
                criteria = new[]
                {
                    new { field = "LastName", value = Sentinel + new string('x', count - Sentinel.Length) },
                },
            }),
            "alternatives past the bound" => JsonSerializer.Serialize(new
            {
                criteria = new[]
                {
                    new { field = "LastName", value = string.Join(" or ", Enumerable.Repeat("%", first)) },
                    new { field = "Notes", value = string.Join(" or ", Enumerable.Repeat("%", count - first)) },
                },
            }),
            "patterns past the bound" => JsonSerializer.Serialize(new
            {
                criteria = new[]
                {
                    new { field = "LastName", value = Sentinel + "*" + new string('x', first - Sentinel.Length - 1) },
                    new
                    {
                        field = "Notes",
                        value = Sentinel + "*" + new string('x', count - first - Sentinel.Length - 1),
                    },
                },
            }),
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "Unknown request shape."),
        };
        var (expectedKey, expectedMessage) = shape switch
        {
            "value past its bound" => ("LastName", "LastName search values can hold at most 32767 characters."),
            "alternatives past the bound" => (
                SearchCriteriaParser.CriteriaKey,
                "One search can hold at most 6554 wildcard, comparison and multiline alternatives in all."),
            "patterns past the bound" => (
                SearchCriteriaParser.CriteriaKey,
                "The wildcard patterns of one search can hold at most 32767 characters in all."),
            _ => (SearchCriteriaParser.CriteriaKey, "At most 6 fields can be searched at once."),
        };
        using var body = JsonDocument.Parse(json);

        using HttpResponseMessage response = await ApiTestClient.PostJsonAsync(
            client,
            string.Create(CultureInfo.InvariantCulture, $"/api/forms/{FakeForms.TwoPageViewId}/records/search"),
            body.RootElement);
        byte[] answer = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.InRange(answer.Length, 1, MaxProblemBodyBytes);
        Assert.DoesNotContain(Sentinel, System.Text.Encoding.UTF8.GetString(answer), StringComparison.Ordinal);
        using var parsed = JsonDocument.Parse(answer);
        var problem = ApiTestClient.AssertProblemDetails(parsed.RootElement, StatusCodes.Status400BadRequest);
        var errors = problem.GetProperty("errors");
        var error = Assert.Single(errors.EnumerateObject());
        Assert.Equal(expectedKey, error.Name);
        Assert.Equal(expectedMessage, Assert.Single(error.Value.EnumerateArray()).GetString());
        Assert.DoesNotContain(store.Calls, call => call.Method == nameof(IDataStore.SearchAsync));
    }

    // Over HTTP, the verifier's search reaches the store and answers 200: two plain multiline values of 17,000 code
    // units each, on two pages, each within one legacy text box and both more than one together, each compared on its
    // first 255 characters. A record whose two values share those characters with them is found.
    [Fact]
    public async Task Search_TwoLongPlainMultilineValues_ReachTheStore()
    {
        var form = TwoMultilineForm();
        var store = new FakeDataStore(new FakeForm(
            form, new Dictionary<string, IReadOnlyList<FieldOption>>(StringComparer.OrdinalIgnoreCase), null));
        var address = Sentinel + new string('a', 17_000 - Sentinel.Length);
        var purchases = Sentinel + new string('b', 17_000 - Sentinel.Length);
        var id = store.SeedRecord(
            TwoMultilineViewId,
            new Dictionary<string, FieldValue>(StringComparer.Ordinal)
            {
                ["PatientAddress"] = FieldValue.FromText(address[..300]),
                ["BuyLastTenDays"] = FieldValue.FromText(purchases[..255]),
            });
        await using var factory = new ApiFactory(ApiFactoryOptions.ForFake(store));
        using HttpClient client = factory.CreateClient();
        using var body = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            criteria = new[]
            {
                new { field = "PatientAddress", value = address },
                new { field = "BuyLastTenDays", value = purchases },
            },
        }));

        using HttpResponseMessage response = await ApiTestClient.PostJsonAsync(
            client,
            string.Create(CultureInfo.InvariantCulture, $"/api/forms/{TwoMultilineViewId}/records/search"),
            body.RootElement);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement result = await ApiTestClient.ReadJsonElementAsync(response);
        Assert.Equal(1, result.GetProperty("total").GetInt64());
        Assert.Equal(
            id,
            Assert.Single(result.GetProperty("items").EnumerateArray()).GetProperty("globalRecordId").GetString());
        Assert.Contains(store.Calls, call => call.Method == nameof(IDataStore.SearchAsync));
    }

    // The fake keeps the search builder's contract: a LIKE pattern holding a set longer than 4,000 characters that is
    // unclosed or that no cut keeping its members divides, in any condition, is an ArgumentException, while one the
    // builder cuts is matched.
    [Theory]
    [InlineData("%[", "", true)]
    [InlineData("%[", "]%", false)]
    [InlineData("%[^", "]%", false)]
    public async Task FakeStoreSearch_SetLongerThanTheLikeLimit_ThrowsOnlyWhereNoCutKeepsIt(
        string open,
        string close,
        bool throws)
    {
        var store = FakeDataStore.WithStandardForms();
        var form = await TwoPageFormAsync(store);
        SearchCondition[] conditions =
        [
            LikeCondition("LastName", "%"),
            LikeCondition("Notes", open + new string('a', 4001) + close),
        ];

        var failure = await Record.ExceptionAsync(
            () => store.SearchAsync(form, conditions, includeDeleted: false, null, CancellationToken.None));

        if (throws)
        {
            Assert.Equal("conditions", Assert.IsType<ArgumentException>(failure).ParamName);
        }
        else
        {
            Assert.Null(failure);
        }
    }

    // The fake lists a filter of any length, matching it literally and ignoring case as the list builder's contains
    // predicate does past SQL Server's LIKE pattern limit.
    [Theory]
    [MemberData(nameof(LongFilterCases))]
    public async Task FakeStoreList_FilterPastTheLikeLimit_ListsItsMatches(string q, bool matches)
    {
        var store = FakeDataStore.WithStandardForms();
        var form = await TwoPageFormAsync(store);
        var id = store.SeedRecord(
            FakeForms.TwoPageViewId,
            new Dictionary<string, FieldValue> { ["LastName"] = FieldValue.FromText(LongLastName) });

        var page = await store.ListAsync(form, ListQuery(q), CancellationToken.None);

        Assert.Equal(matches, page.Items.Any(item => item.GlobalRecordId == id));
    }

    // The published contract carries the same bounds: 1 to 6 criteria, since an empty list, a seventh field and a field
    // named twice are each a 400, and at most 200 result items, however many match.
    [Theory]
    [InlineData("SearchRequest", "criteria", 1, RecordQueryRules.MaxSearchFields)]
    [InlineData("SearchResponse", "items", null, RecordQueryRules.SearchCap)]
    public async Task SearchArrays_PublishTheirBounds(string component, string member, int? minItems, int maxItems)
    {
        JsonElement array = (await PublishedSchemasAsync())
            .GetProperty(component)
            .GetProperty("properties")
            .GetProperty(member);

        Assert.Equal("array", array.GetProperty("type").GetString());
        Assert.Equal(maxItems, array.GetProperty("maxItems").GetInt32());
        if (minItems is null)
        {
            Assert.False(array.TryGetProperty("minItems", out _), $"{component}.{member} publishes a minItems.");
        }
        else
        {
            Assert.Equal(minItems, array.GetProperty("minItems").GetInt32());
        }
    }

    private static RecordService CreateService(FakeDataStore store) => new(
        store,
        new FormService(store, new CheckCodeEngine(), NullLogger<FormService>.Instance),
        new SaveStampProvider(TimeProvider.System),
        NullLogger<RecordService>.Instance);

    // A LastName criterion of size alternatives, or a single wildcard pattern of size characters: one '*', a run of '*',
    // or a set followed by a run of '*'; or the sentinel, '*' and a set of size characters: closed, unclosed, or a
    // chain of ranges, odd-sized, no cut between whose members keeps them. Each carries the sentinel, so an error
    // message that echoed it would be caught.
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
            SetKind => Sentinel + "*[" + new string('a', size - 2) + "]",
            UnclosedSetKind => Sentinel + "*[" + new string('a', size - 1),
            RangeChainSetKind => Sentinel + "*[" + string.Concat(Enumerable.Repeat("a-", (size - 3) / 2)) + "a]",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown search kind."),
        };

        return new SearchRequest([new SearchCriterion("LastName", FieldValue.FromText(value))]);
    }

    private static SearchCondition LikeCondition(string field, string pattern) => new(
        field,
        SearchTarget.PageField,
        FieldType.Text,
        [new SearchAlternative(SearchOperator.Like, FieldValue.FromText(pattern))]);

    private static RecordListQuery ListQuery(string q) => new(q, IncludeDeleted: false, 1, 50);

    private static async Task<FormMetadata> TwoPageFormAsync(FakeDataStore store)
    {
        var form = await store.GetFormMetadataAsync(FakeForms.TwoPageViewId, CancellationToken.None);
        Assert.NotNull(form);
        return form;
    }

    // A synthetic form, view 9120 on the unseeded base table FakeTwoMultiline, holding the Multiline PatientAddress on
    // its first page and the Multiline BuyLastTenDays on its second, each on ntext as FoodHistory seeds them, with the
    // seeded system rows and field-type ids.
    private static FormMetadata TwoMultilineForm()
    {
        const string baseTable = "FakeTwoMultiline";
        PageMetadata[] pages = [Page(1, "Page 1", 0), Page(2, "Page 2", 1)];
        FieldMetadata[] fields =
        [
            SystemRow(1, "UniqueKey", 23),
            SystemRow(2, "RecStatus", 22),
            SystemRow(3, "GlobalRecordId", 26),
            MultilineField(4, pages[0], "PatientAddress"),
            MultilineField(5, pages[1], "BuyLastTenDays"),
        ];
        return new FormMetadata(
            TwoMultilineViewId, baseTable, baseTable, Array.AsReadOnly(pages), Array.AsReadOnly(fields));

        // Page ids are <viewId>01 and <viewId>02, and each page table is the base table name plus its page id.
        static PageMetadata Page(int ordinal, string name, int position)
        {
            var pageId = (TwoMultilineViewId * 100) + ordinal;
            return new PageMetadata(pageId, name, position, baseTable + pageId.ToString(CultureInfo.InvariantCulture));
        }

        static FieldMetadata SystemRow(int ordinal, string name, int fieldTypeId) => new()
        {
            FieldId = (TwoMultilineViewId * 1000) + ordinal,
            Name = name,
            FieldTypeId = fieldTypeId,
            FieldTypeName = name,
            TabIndex = 0m,
        };

        static FieldMetadata MultilineField(int ordinal, PageMetadata page, string name) => new()
        {
            FieldId = (TwoMultilineViewId * 1000) + ordinal,
            Name = name,
            FieldTypeId = 4,
            FieldTypeName = "Multiline",
            Prompt = name,
            TabIndex = 1m,
            PageId = page.PageId,
            Column = FieldTypeMapper.DescribeColumn(name, "ntext", 16),
        };
    }

    // The errors map of a field-level 400 as ApiProblems builds it.
    private static IDictionary<string, string[]> ValidationErrors(IResult? result)
    {
        var problem = Assert.IsType<ApiProblems.ProblemResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
        return Assert.IsType<HttpValidationProblemDetails>(problem.Value).Errors;
    }

    // The component schemas of the document the API serves at /openapi/v1.json, from a host with no connection string.
    private static async Task<JsonElement> PublishedSchemasAsync()
    {
        await using var factory = new ApiFactory(new ApiFactoryOptions { ConnectionMode = ApiConnectionMode.None });
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement document = await ApiTestClient.ReadJsonElementAsync(response);
        return document.GetProperty("components").GetProperty("schemas");
    }
}

/// <summary>
/// Pins the Find and list semantics of the in-memory <see cref="FakeDataStore"/> against the
/// <see cref="IDataStore"/> Find and list contract that AAP 0.3.4.3 and 0.3.4.4 set out: exact comparison on integer
/// columns and binary64 comparison on float columns, text comparison padded with spaces as SQL Server pads it, SQL
/// <c>LIKE</c> over the whole text, and one scan that numbers, filters, counts and pages records as the SQL statements
/// do.
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
        // Patterns long enough for a regular-expression automaton to exceed its size limit, at and past SQL Server's
        // 4,000-character LIKE pattern limit, which bounds no pattern.
        { new string('a', 4000), new string('a', 3999) + "%", true },
        { new string('a', 4000), new string('a', 3998) + "_", false },
        { new string('a', 4000), new string('A', 4000) + "%", true },
        { new string('a', 4000), new string('a', 3999) + "b%", false },
        { new string('a', 4000), new string('%', 4001) + "a", true },
        { new string('a', 4000), new string('_', 4001), false },
        { new string('a', 4000), "a" + new string('%', 2000) + "_a" + new string('%', 2000) + "a", true },
        { new string('a', 1000), string.Concat(Enumerable.Repeat("%a", 1000)), true },
        { new string('a', 999), string.Concat(Enumerable.Repeat("%a", 1000)), false },

        // A set past the limit, which the search builder cuts into shorter sets, holds every member it lists.
        { "a", "[" + new string('b', 4096) + "a]", true },
        { "c", "[" + new string('b', 4096) + "a]", false },
        { "c", "[^" + new string('b', 4096) + "a]", true },

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

    /// <summary>
    /// A stored text, a Find operator and its criterion, with whether SQL Server finds the stored value, as
    /// <see cref="FindPaddingSqlTests"/> confirms on <c>nvarchar</c> parameters: the comparisons and the 255-character
    /// prefix equality pad the shorter text with spaces, and <c>LIKE</c> keeps trailing blanks significant. The texts
    /// hold only letters and spaces, so each outcome turns on the padding, never on how a collation orders other
    /// characters.
    /// </summary>
    public static TheoryData<string, SearchOperator, string, bool> PaddingCases => new()
    {
        // Equality ignores trailing blanks on either side, never leading ones.
        { "Smith ", SearchOperator.Equal, "Smith", true },
        { "Smith", SearchOperator.Equal, "Smith  ", true },
        { " Smith", SearchOperator.Equal, "Smith", false },
        { "Smith ", SearchOperator.Equal, "Smit", false },
        { "Smith ", SearchOperator.NotEqual, "Smith", false },
        { "Smith ", SearchOperator.NotEqual, "Smyth", true },

        // Ordering compares the padded texts, so trailing blanks order as the padding does.
        { "Smith ", SearchOperator.Greater, "Smith", false },
        { "Smith ", SearchOperator.LessOrEqual, "Smith", true },
        { "Smith", SearchOperator.Less, "Smith ", false },
        { "Smith", SearchOperator.GreaterOrEqual, "Smith  ", true },
        { "Smith a", SearchOperator.Greater, "Smith  ", true },

        // 300-character texts whose 255-character prefix ends in blanks, followed by letters or by more blanks, equal
        // the shorter criterion; a prefix that differs before its blanks does not.
        {
            new string('a', 250) + new string(' ', 5) + new string('b', 45),
            SearchOperator.PrefixEqual255,
            new string('a', 250),
            true
        },
        { new string('a', 254) + new string(' ', 46), SearchOperator.PrefixEqual255, new string('a', 254), true },
        {
            new string('a', 250),
            SearchOperator.PrefixEqual255,
            new string('a', 250) + new string(' ', 10) + new string('b', 40),
            true
        },
        {
            new string('a', 250) + new string(' ', 5) + new string('b', 45),
            SearchOperator.PrefixEqual255,
            new string('a', 251),
            false
        },

        // LIKE pads neither side on Unicode text, so a trailing blank must be matched.
        { "Smith ", SearchOperator.Like, "Smith", false },
        { "Smith", SearchOperator.Like, "Smith ", false },
        { "Smith ", SearchOperator.Like, "Smith_", true },
        { "Smith ", SearchOperator.Like, "Smith%", true },
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

    // Stored values and criteria with trailing blanks, compared as SQL Server compares them. The conditions are built
    // directly: the parser trims every criterion and takes the prefix equality only for a multiline field.
    [Theory]
    [MemberData(nameof(PaddingCases))]
    public async Task TextComparisonsPadTrailingBlanksButLikeDoesNot(
        string stored,
        SearchOperator comparison,
        string criterion,
        bool matches)
    {
        Assert.Equal(matches, await FindsAsync(stored, comparison, criterion));
    }

    // A GlobalRecordId criterion compares padded too, as the SQL store's plain comparison on the nvarchar id column
    // does: trailing blanks still find the record, a leading blank does not.
    [Theory]
    [InlineData(SearchOperator.Equal, "", "  ", true)]
    [InlineData(SearchOperator.Equal, " ", "", false)]
    [InlineData(SearchOperator.Less, "", " ", false)]
    [InlineData(SearchOperator.GreaterOrEqual, "", " ", true)]
    public async Task GlobalRecordIdCriterionPadsTrailingBlanks(
        SearchOperator comparison,
        string leading,
        string trailing,
        bool matches)
    {
        var store = CreateStore();
        var id = Seed(store, NameField, FieldValue.FromText("match"));
        SearchCondition[] conditions =
        [
            new("GlobalRecordId", SearchTarget.BaseGlobalRecordId, null,
                [new SearchAlternative(comparison, FieldValue.FromText(leading + id + trailing))]),
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

    // The grid filter searches a float as SQL Server's style-0 text, which keeps at most six significant digits, so q
    // matches only that text. Records: 0 weighs 1234567.891 (1.23457e+006), 1 weighs 0.00001 (1e-005), 2 weighs
    // 12345.65 (12345.7, from its 17 significant digits, though its double lies below the tie), 3 weighs 2^53 + 1,
    // which a float column stores as 2^53 (9.0072e+015), and 4 weighs 37.5. Round-trip texts match nothing.
    [Theory]
    [InlineData("1.23457e+006", new[] { 0 })]
    [InlineData("E+006", new[] { 0 })]
    [InlineData("1234567", new int[0])]
    [InlineData("e-005", new[] { 1 })]
    [InlineData("1E-05", new int[0])]
    [InlineData("12345.7", new[] { 2 })]
    [InlineData("12345.65", new int[0])]
    [InlineData("9.0072e+015", new[] { 3 })]
    [InlineData("9007199254740993", new int[0])]
    [InlineData("e+0", new[] { 0, 3 })]
    [InlineData("37.5", new[] { 4 })]
    public async Task ListMatchesAFloatAsSqlServerConvertsItToText(string q, int[] expectedRecords)
    {
        var store = CreateStore();
        string[] ids =
        [
            Seed(store, WeightField, FieldValue.FromNumber(1234567.891)),
            Seed(store, WeightField, FieldValue.FromNumber(0.00001)),
            Seed(store, WeightField, FieldValue.FromNumber(12345.65)),
            Seed(store, WeightField, FieldValue.FromInteger(TwoToThe53 + 1)),
            Seed(store, WeightField, FieldValue.FromNumber(37.5)),
        ];

        var result = await store.ListAsync(Form, RecordListQuery.Default with { Q = q }, CancellationToken.None);

        Assert.Equal(expectedRecords.Select(record => ids[record]), result.Items.Select(item => item.GlobalRecordId));
        Assert.Equal(expectedRecords.Length, result.Total);
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
        var insertResult = await store.InsertAsync(
            Form,
            new Dictionary<string, FieldValue> { [NameField] = FieldValue.FromText("inserted") },
            stamp,
            permissions: null,
            CancellationToken.None);
        Assert.True(insertResult.IsSuccess);
        var inserted = insertResult.Record;
        var criteria = Parse(NameField, "partial or inserted");

        Assert.Equal(new[] { inserted.GlobalRecordId }, await SearchIdsAsync(store, criteria));

        var updated = await store.UpdateAsync(
            Form,
            partial,
            new Dictionary<string, FieldValue> { [NotesField] = FieldValue.FromText("filled") },
            () => stamp,
            _ => RecordValidationResult.Success(new Dictionary<string, FieldValue>()),
            permissions: null,
            CancellationToken.None);

        Assert.True(updated.IsSuccess);
        var state = Assert.IsType<FakeRecordState>(store.GetRecord(ViewId, partial));
        Assert.Equal(new[] { FirstPageId, SecondPageId }, state.PresentPageIds.Order());
        Assert.Equal("filled", state.Values[NotesField].AsText());
        Assert.Equal(stamp, state.LastSave);
        Assert.Equal(new[] { partial, inserted.GlobalRecordId }, await SearchIdsAsync(store, criteria));

        var deleted = await store.SoftDeleteAsync(
            Form,
            inserted.GlobalRecordId,
            () => stamp,
            permissions: null,
            CancellationToken.None);
        Assert.Equal(
            (SoftDeleteStatus.Deleted, inserted.GlobalRecordId),
            (deleted.Status, deleted.GlobalRecordId));
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
            permissions: null,
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

    // A write refuses a read-only field's key, even with a null value, as the SQL builders do: before the locked read
    // or any write is logged, and before anything changes, though the writable LastName beside it is valid. A seed
    // admits it, since a seeded record stands for a stored row, and the stored value reads back unchanged. This
    // class's own form has no read-only field, so the case uses the read-only number OnsetWeek of FakeForms.
    [Theory]
    [InlineData(nameof(IDataStore.InsertAsync), "values", false)]
    [InlineData(nameof(IDataStore.InsertAsync), "values", true)]
    [InlineData(nameof(IDataStore.UpdateAsync), "submitted", false)]
    [InlineData(nameof(IDataStore.UpdateAsync), "submitted", true)]
    public async Task WritesRefuseAReadOnlyFieldThatASeedAdmits(string method, string paramName, bool explicitNull)
    {
        const string readOnlyField = "OnsetWeek";
        const string writableField = "LastName";
        var form = FakeForms.WithCheckCode(string.Empty);
        var store = new FakeDataStore(form);
        var storedWeek = FieldValue.FromInteger(7);
        var id = store.SeedRecord(
            FakeForms.CheckCodeViewId,
            new Dictionary<string, FieldValue>
            {
                [writableField] = FieldValue.FromText("seeded"),
                [readOnlyField] = storedWeek,
            });
        var read = await store.GetByIdAsync(form.Metadata, id, CancellationToken.None);
        Assert.Equal(storedWeek, Assert.IsType<RecordDto>(read).Values[readOnlyField]);
        store.ClearCalls();

        var values = new Dictionary<string, FieldValue>
        {
            [writableField] = FieldValue.FromText("changed"),
            [readOnlyField] = explicitNull ? FieldValue.Null : FieldValue.FromInteger(8),
        };
        var stamp = new SaveStamp("tester", new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        var failure = await Record.ExceptionAsync(() => method == nameof(IDataStore.UpdateAsync)
            ? store.UpdateAsync(
                form.Metadata,
                id,
                values,
                () => stamp,
                _ => RecordValidationResult.Success(new Dictionary<string, FieldValue>()),
                permissions: null,
                CancellationToken.None)
            : store.InsertAsync(form.Metadata, values, stamp, permissions: null, CancellationToken.None));

        var argument = Assert.IsType<ArgumentException>(failure);
        Assert.Equal(paramName, argument.ParamName);
        Assert.StartsWith("A read-only field is never written.", argument.Message, StringComparison.Ordinal);
        var record = Assert.Single(store.GetRecords(FakeForms.CheckCodeViewId));
        Assert.Equal(id, record.GlobalRecordId);
        Assert.Equal("seeded", record.Values[writableField].AsText());
        Assert.Equal(storedWeek, record.Values[readOnlyField]);
        Assert.Null(record.LastSave);
        Assert.Empty(store.Calls);
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

    /// <summary>
    /// Whether the store's Find returns the one record seeded with <paramref name="stored"/> in the text
    /// <c>Name</c>, searched by a condition built directly with one <paramref name="comparison"/> alternative on
    /// <paramref name="criterion"/>, so the criterion keeps the blanks the parser would trim.
    /// </summary>
    internal static async Task<bool> FindsAsync(string stored, SearchOperator comparison, string criterion)
    {
        var store = CreateStore();
        Seed(store, NameField, FieldValue.FromText(stored));
        SearchCondition[] conditions =
        [
            new(NameField, SearchTarget.PageField, FieldType.Text,
                [new SearchAlternative(comparison, FieldValue.FromText(criterion))]),
        ];

        var response = await SearchAsync(store, conditions);

        Assert.InRange(response.Total, 0, 1);
        return response.Total == 1;
    }

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
/// Holds the <see cref="FakeDataStore"/> Find to SQL Server's trailing-blank rules through genuine Text, Multiline and
/// Phone Number metadata: each criterion goes through <see cref="SearchCriteriaParser"/>, which chooses its operator,
/// and the store must return exactly the rows, and the total, that SQL Server returns for the same stored values.
/// </summary>
/// <remarks>
/// <c>ReservedWordIdentifierTests.StoredValues_FindPadsTrailingBlanksButLikeDoesNot</c> in <c>EpiInfo.Data.Tests</c>
/// pins the same rows and cases on SQL Server, over session temporary tables; the two tables must stay identical. This
/// class needs no database: it carries no <c>Integration</c> trait, joins no database collection and takes no lock.
/// Its form is synthetic: view 9110 on the unseeded base table <c>FakeTrailingBlankFind</c>, with the Text
/// <c>Surname</c> on <c>nvarchar(4000)</c>, the Multiline <c>Remarks</c> on <c>ntext</c> and the Phone Number
/// <c>Phone</c> on <c>nvarchar(20)</c>, the column types their seeded fields use, on one page.
/// </remarks>
public sealed class FakeDataStoreTrailingBlankFindTests
{
    private const int ViewId = 9110;
    private const int PageId = 911001;
    private const string BaseTable = "FakeTrailingBlankFind";
    private const string Surname = "Surname";
    private const string Remarks = "Remarks";
    private const string Phone = "Phone";

    private static readonly FormMetadata Form = CreateForm();

    /// <summary>
    /// Criteria on the text <c>Surname</c>, the multiline <c>Remarks</c> and the Phone Number <c>Phone</c>, each with
    /// the operator the parser gives it and the labels of the rows Find must return, in <c>UniqueKey</c> order, over
    /// the stored rows of <see cref="StoredValues_FindPadsTrailingBlanksButLikeDoesNot"/>.
    /// </summary>
    public static TheoryData<string, string, SearchOperator, string> TrailingBlankCases => new()
    {
        // Equality pads the shorter text, so the stored "Smith ", "Smith   " and "Smith" are all found.
        { Surname, "Smith", SearchOperator.Equal, "ABC" },

        // LIKE keeps trailing blanks significant: %Smith_ matches only the one-blank "Smith ", and %mith only "Smith".
        // The parser takes LIKE only for a value holding *, % or ?, so * leads each pattern.
        { Surname, "*Smith_", SearchOperator.Like, "B" },
        { Surname, "*mith", SearchOperator.Like, "A" },

        // The multiline prefix equality pads too: "Notes" finds the stored "Notes   ", and a 300-character criterion
        // whose 255-character prefix ends in five blanks finds a stored prefix ending in the same blanks, and the
        // stored 250 letters alone, but not 251 letters.
        { Remarks, "Notes", SearchOperator.PrefixEqual255, "AB" },
        {
            Remarks,
            new string('a', 250) + new string(' ', 5) + new string('c', 45),
            SearchOperator.PrefixEqual255,
            "CD"
        },
        { Remarks, "*Notes", SearchOperator.Like, "A" },

        // Phone Number keeps its prefix as the operator, and the stored "555-0148 " orders as "555-0148" does. A NULL
        // phone satisfies no comparison.
        { Phone, ">555-0148", SearchOperator.Greater, "C" },
        { Phone, ">=555-0148", SearchOperator.GreaterOrEqual, "ABC" },
        { Phone, "<>555-0148", SearchOperator.NotEqual, "CE" },
        { Phone, "555-0148", SearchOperator.Equal, "AB" },
        { Phone, "<=555-0148", SearchOperator.LessOrEqual, "ABE" },
        { Phone, "<555-0148", SearchOperator.Less, "E" },
    };

    /// <summary>
    /// The store's Find over stored values with trailing blanks returns the rows and the total SQL Server returns:
    /// text and Phone Number comparisons and the multiline 255-character prefix equality pad the shorter operand with
    /// spaces, while <c>LIKE</c> keeps trailing blanks significant.
    /// </summary>
    /// <param name="field">The searched field.</param>
    /// <param name="criterion">The criterion text, as the API receives it.</param>
    /// <param name="expectedOperator">The operator the parser must give the criterion's one alternative.</param>
    /// <param name="expectedRows">The labels of the rows it must return, in <c>UniqueKey</c> order.</param>
    [Theory]
    [MemberData(nameof(TrailingBlankCases))]
    public async Task StoredValues_FindPadsTrailingBlanksButLikeDoesNot(
        string field,
        string criterion,
        SearchOperator expectedOperator,
        string expectedRows)
    {
        var store = new FakeDataStore(new FakeForm(
            Form, new Dictionary<string, IReadOnlyList<FieldOption>>(StringComparer.OrdinalIgnoreCase), null));

        // Every row is active and has its page row. B holds A's three texts with trailing blanks; C and D hold
        // multiline values whose 255-character prefixes equal the long criterion's once padded, and E one whose
        // prefix differs before the blanks; a field a row omits is NULL.
        var rows = new (string Label, (string Name, FieldValue Value)[] Values)[]
        {
            ("A", [
                (Surname, FieldValue.FromText("Smith")),
                (Remarks, FieldValue.FromText("Notes")),
                (Phone, FieldValue.FromText("555-0148")),
            ]),
            ("B", [
                (Surname, FieldValue.FromText("Smith ")),
                (Remarks, FieldValue.FromText("Notes   ")),
                (Phone, FieldValue.FromText("555-0148 ")),
            ]),
            ("C", [
                (Surname, FieldValue.FromText("Smith   ")),
                (Remarks, FieldValue.FromText(new string('a', 250) + new string(' ', 5) + new string('b', 45))),
                (Phone, FieldValue.FromText("555-0149")),
            ]),
            ("D", [
                (Surname, FieldValue.FromText("Smyth")),
                (Remarks, FieldValue.FromText(new string('a', 250))),
            ]),
            ("E", [
                (Remarks, FieldValue.FromText(new string('a', 251) + new string('b', 49))),
                (Phone, FieldValue.FromText("555-0147")),
            ]),
        };

        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (label, values) in rows)
        {
            var globalRecordId = store.SeedRecord(
                ViewId, values.ToDictionary(entry => entry.Name, entry => entry.Value, StringComparer.Ordinal));
            labels.Add(globalRecordId, label);
        }

        var parsed = SearchCriteriaParser.Parse(
            FormDefinitionFactory.GetDataFields(Form), [new SearchCriterion(field, FieldValue.FromText(criterion))]);
        Assert.True(parsed.IsSuccess, "The criterion must parse.");
        Assert.Equal(expectedOperator, Assert.Single(Assert.Single(parsed.Conditions).Alternatives).Operator);

        var response = await store.SearchAsync(
            Form, parsed.Conditions, includeDeleted: false, excludeGlobalRecordId: null, CancellationToken.None);

        Assert.Equal(expectedRows, string.Concat(response.Items.Select(item => labels[item.GlobalRecordId])));
        Assert.Equal(expectedRows.Length, response.Total);
        Assert.False(response.Truncated);
    }

    // The system rows and field-type ids are the seeded metaFields and metaFieldTypes rows, as FakeForms builds them;
    // each data field's column has the type and catalogue size of its seeded counterparts.
    private static FormMetadata CreateForm()
    {
        PageMetadata[] pages = [new(PageId, "Page 1", 0, BaseTable + PageId.ToString(CultureInfo.InvariantCulture))];
        FieldMetadata[] fields =
        [
            SystemRow(1, "UniqueKey", 23),
            SystemRow(2, "RecStatus", 22),
            SystemRow(3, "GlobalRecordId", 26),
            PageField(4, 1m, Surname, 1, "Text", "nvarchar", 8000),
            PageField(5, 2m, Remarks, 4, "Multiline", "ntext", 16),
            PageField(6, 3m, Phone, 6, "Phone Number", "nvarchar", 40),
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
            PageId = PageId,
            Column = FieldTypeMapper.DescribeColumn(name, sqlTypeName, maxLengthBytes),
        };
}

/// <summary>
/// Holds <see cref="FakeDataStore.GridFilterText"/>, the text the double's grid filter searches, to SQL Server: each
/// value is bound as the type of the column that stores it and converted as the list's <c>q</c> predicate converts a
/// grid column (AAP 0.3.4.4), <c>CONVERT(nvarchar(max), @v)</c> with style 23 for <c>datetime</c>, and SQL Server's
/// text must equal the double's exactly.
/// </summary>
/// <remarks>
/// Each parameter takes the SQL Server type of its column, a number on a <c>float</c> column its double as
/// <see cref="DbType.Double"/>, as the SQL store binds it, and an <c>nchar(n)</c> value its fixed length, as the
/// column holds it. The statement reads no table and writes nothing, so the class takes no <c>DatabaseWriteLock</c>
/// (AAP 0.7.5); it still joins the database collection and probes the database first, as every integration test
/// class does.
/// </remarks>
[Trait("Category", "Integration")]
[Collection(DatabaseCollections.Database)]
public sealed class GridFilterTextSqlTests : IAsyncLifetime
{
    // Numbers as a request or a criterion carries them, each stored in a float column.
    private static readonly string[] FloatNumbers =
    [
        // Integers below, at and above six digits, and values that round up to seven.
        "123456", "999999", "1234567", "9999995", "999999.5",

        // Exactly representable ties at the seventh significant digit round half up, never to even.
        "1234565", "1234575", "1234585", "100000.5", "100001.5", "100002.5", "123456.5", "1.828125", "0.01953125",

        // Values just below a tie, which their 17 significant digits decide.
        "12345.65", "99999.95", "9.999995",

        // Zero, negative zero, which a number value holds as zero, and negatives.
        "0", "-0", "-1234567.891", "-0.00001",

        // Either side of the exponents where fixed notation stops.
        "0.0001", "0.00001", "0.000123456", "100000", "1000000",

        // Large and small magnitudes: the largest double, the smallest normal one and the smallest subnormal one last.
        "1e15", "1e100", "1e-300", "1.7976931348623157e308", "2.2250738585072014e-308", "4.9406564584124654e-324",

        // 2^53 + 1, which a float column stores as 2^53, and short decimals.
        "9007199254740993", "0.1", "0.125", "37.5", "10001.0", "1234567.891",
    ];

    /// <summary>
    /// The column's <c>sys.types</c> name and <c>max_length</c>, the value's kind and its invariant text: every
    /// family a grid column can be stored in, and the <c>bit</c> family besides.
    /// </summary>
    public static TheoryData<string, int, FieldValueKind, string> Cases
    {
        get
        {
            var cases = new TheoryData<string, int, FieldValueKind, string>();
            foreach (var number in FloatNumbers)
            {
                cases.Add("float", 8, FieldValueKind.Number, number);
            }

            cases.Add("tinyint", 1, FieldValueKind.Number, "255");
            cases.Add("smallint", 2, FieldValueKind.Number, "-32768");
            cases.Add("smallint", 2, FieldValueKind.Index, "2");
            cases.Add("int", 4, FieldValueKind.Number, "-2147483648");
            cases.Add("bigint", 8, FieldValueKind.Number, "9223372036854775807");
            cases.Add("bigint", 8, FieldValueKind.Number, "-9223372036854775808");
            cases.Add("datetime", 8, FieldValueKind.Date, "2011-01-15");
            cases.Add("datetime", 8, FieldValueKind.Date, "1753-01-01");
            cases.Add("datetime", 8, FieldValueKind.Date, "9999-12-31");
            cases.Add("bit", 1, FieldValueKind.Boolean, "true");
            cases.Add("bit", 1, FieldValueKind.Boolean, "false");
            cases.Add("nvarchar", 8000, FieldValueKind.Text, "Smith ");
            cases.Add("nvarchar", -1, FieldValueKind.Text, "O'Brien");
            cases.Add("nchar", 10, FieldValueKind.Text, "abc");
            cases.Add("ntext", 16, FieldValueKind.Text, "Notes ");
            return cases;
        }
    }

    public Task InitializeAsync() => TestDatabase.EnsureReachableAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task GridFilterTextIsSqlServersText(
        string sqlTypeName,
        int maxLengthBytes,
        FieldValueKind kind,
        string text)
    {
        var column = FieldTypeMapper.DescribeColumn("Value", sqlTypeName, maxLengthBytes);
        var value = Value(kind, text);

        string sqlText;
        await using (var connection = await TestDatabase.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = column.Family == ColumnFamily.DateTime
                ? "SELECT CONVERT(nvarchar(max), @v, 23);"
                : "SELECT CONVERT(nvarchar(max), @v);";
            command.Parameters.Add(Parameter(column, value));
            sqlText = Assert.IsType<string>(await command.ExecuteScalarAsync());
        }

        Assert.Equal(sqlText, FakeDataStore.GridFilterText(column, value));
    }

    // The value a case's text stands for; a number parses as a request's number does, so 9007199254740993 stays exact.
    private static FieldValue Value(FieldValueKind kind, string text)
    {
        switch (kind)
        {
            case FieldValueKind.Number:
                Assert.True(FieldValueCoercion.TryParseNumber(text, out var number));
                return number;
            case FieldValueKind.Index:
                return FieldValue.FromIndex(int.Parse(text, CultureInfo.InvariantCulture));
            case FieldValueKind.Date:
                return FieldValue.FromDate(
                    DateOnly.ParseExact(text, FieldValue.IsoDateFormat, CultureInfo.InvariantCulture));
            case FieldValueKind.Boolean:
                return FieldValue.FromBoolean(bool.Parse(text));
            case FieldValueKind.Text:
                return FieldValue.FromText(text);
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "No case uses this kind.");
        }
    }

    // The parameter holding the value as its column stores it.
    private static SqlParameter Parameter(ColumnMetadata column, FieldValue value) => column.SqlTypeName switch
    {
        "float" => new SqlParameter { ParameterName = "@v", DbType = DbType.Double, Value = value.AsNumber() },
        "tinyint" => new SqlParameter("@v", SqlDbType.TinyInt) { Value = checked((byte)Whole(value)) },
        "smallint" => new SqlParameter("@v", SqlDbType.SmallInt) { Value = checked((short)Whole(value)) },
        "int" => new SqlParameter("@v", SqlDbType.Int) { Value = checked((int)Whole(value)) },
        "bigint" => new SqlParameter("@v", SqlDbType.BigInt) { Value = Whole(value) },
        "datetime" => new SqlParameter("@v", SqlDbType.DateTime)
        {
            Value = value.AsDate().ToDateTime(TimeOnly.MinValue),
        },
        "bit" => new SqlParameter("@v", SqlDbType.Bit) { Value = value.AsBoolean() },
        "nvarchar" => new SqlParameter("@v", SqlDbType.NVarChar, column.MaxCharacters ?? -1) { Value = value.AsText() },
        "nchar" => new SqlParameter("@v", SqlDbType.NChar, Assert.NotNull(column.MaxCharacters))
        {
            Value = value.AsText(),
        },
        "ntext" => new SqlParameter("@v", SqlDbType.NText) { Value = value.AsText() },
        _ => throw new ArgumentOutOfRangeException(nameof(column), column.SqlTypeName, "No case uses this type."),
    };

    // An integer column's value: an option index or an exact integer.
    private static long Whole(FieldValue value)
    {
        if (value.TryGetIndex(out var index))
        {
            return index;
        }

        Assert.True(value.TryGetInteger(out var integer));
        return integer;
    }
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

/// <summary>
/// Holds <see cref="FakeDataStoreFindTests.PaddingCases"/> to SQL Server: each case's predicate, in the form the SQL
/// store's Find renders it, is evaluated on <c>nvarchar</c> parameters, a comparison as <c>@stored op @criterion</c>,
/// the multiline prefix equality as <c>SUBSTRING(@stored, 1, 255) = SUBSTRING(@criterion, 1, 255)</c> and a pattern
/// as <c>@stored LIKE @criterion</c>. SQL Server's answer must be the case's expected one, and the
/// <see cref="FakeDataStore"/> Find must give the same answer, so the double's trailing-blank padding is pinned to
/// the database rather than to its own reading.
/// </summary>
/// <remarks>
/// Both parameters take the database's default collation, as a page column and a Find parameter do; the stored value
/// is bound as <c>nvarchar(4000)</c>, the type of the seeded <c>LastName</c> columns, and the criterion as
/// <c>nvarchar(max)</c>, as the SQL store binds it. The statement reads no table and writes nothing, so the class
/// takes no <c>DatabaseWriteLock</c>; it still joins the database collection and probes the database first, as every
/// integration test class does.
/// </remarks>
[Trait("Category", "Integration")]
[Collection(DatabaseCollections.Database)]
public sealed class FindPaddingSqlTests : IAsyncLifetime
{
    // The seeded LastName columns are nvarchar(4000); -1 is nvarchar(max), the SQL store's text criterion type.
    private const int StoredSize = 4000;
    private const int CriterionSize = -1;

    public Task InitializeAsync() => TestDatabase.EnsureReachableAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [MemberData(nameof(FakeDataStoreFindTests.PaddingCases), MemberType = typeof(FakeDataStoreFindTests))]
    public async Task SqlServerAndTheFakeAgreeOnTrailingBlanks(
        string stored,
        SearchOperator comparison,
        string criterion,
        bool matches)
    {
        bool sqlMatches;
        await using (var connection = await TestDatabase.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT CASE WHEN " + Predicate(comparison) + " THEN 1 ELSE 0 END;";
            command.Parameters.Add(new SqlParameter("@stored", SqlDbType.NVarChar, StoredSize) { Value = stored });
            command.Parameters.Add(
                new SqlParameter("@criterion", SqlDbType.NVarChar, CriterionSize) { Value = criterion });
            sqlMatches = Assert.IsType<int>(await command.ExecuteScalarAsync()) == 1;
        }

        Assert.Equal(matches, sqlMatches);
        Assert.Equal(sqlMatches, await FakeDataStoreFindTests.FindsAsync(stored, comparison, criterion));
    }

    // The predicate the SQL store's Find renders for each operator of the cases, on the two parameters.
    private static string Predicate(SearchOperator comparison) => comparison switch
    {
        SearchOperator.Equal => "@stored = @criterion",
        SearchOperator.NotEqual => "@stored <> @criterion",
        SearchOperator.Less => "@stored < @criterion",
        SearchOperator.LessOrEqual => "@stored <= @criterion",
        SearchOperator.Greater => "@stored > @criterion",
        SearchOperator.GreaterOrEqual => "@stored >= @criterion",
        SearchOperator.PrefixEqual255 => "SUBSTRING(@stored, 1, 255) = SUBSTRING(@criterion, 1, 255)",
        SearchOperator.Like => "@stored LIKE @criterion",
        _ => throw new ArgumentOutOfRangeException(
            nameof(comparison), comparison, "No padding case uses this operator."),
    };
}

/// <summary>
/// The records list and the Find search over HTTP against the seeded rehearsal database (AAP 0.3.4.4 and the SC-5
/// "Listing and Find" cases of AAP 0.7.1): paging, the deleted toggle and the grid <c>q</c> of
/// <c>GET /api/forms/{viewId}/records</c>, and the legacy-semantics criteria, set selection, exclusion, positions,
/// order and cap of <c>POST /api/forms/{viewId}/records/search</c>.
/// </summary>
/// <remarks>
/// <para>
/// Every expected record, count and position is computed by a direct SQL query read under the test lock, never
/// hard-coded, so each case holds on a freshly loaded database and after any number of earlier runs. The contract's
/// parameter names, paging defaults and bounds, search cap and row states are the class's own literals, never the
/// production constants, so the code under test cannot move an expectation. A test that creates records holds the
/// exclusive <see cref="DatabaseWriteLock"/> from before its first SQL read to its last assertion; a read-only case
/// whose expectations are SQL counts or positions holds it shared; a coercion 400 takes none (AAP 0.7.5).
/// </para>
/// <para>
/// Records are created only through <c>POST /api/forms/{viewId}/records</c>, each carrying a fresh
/// <see cref="TestDatabase.NewMarker"/> in <see cref="TestDatabase.MarkerField"/>, and they stay in the database. The
/// one direct write is the missing-page fixture, which deletes the <c>FoodHistory3</c> row of a record the test itself
/// created (AAP 0.3.7, "Data reach of tests"). No seeded row, metadata row or schema object is changed.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(DatabaseCollections.Database)]
public sealed partial class ListAndSearchTests
{
    private const string CaseReport = "CaseReport";

    private const string FoodHistory = "FoodHistory";

    // Data fields of the seeded forms (testdata/sql/03_forms.sql): FirstName is nvarchar(4000) text on both forms,
    // PhoneNumber is CaseReport's "Phone Number" field with the mask ###-###-####, and PatientAddress is FoodHistory's
    // ntext Multiline field (testdata/sql/05_data_tables.sql).
    private const string FirstNameField = "FirstName";

    private const string PhoneNumberField = "PhoneNumber";

    private const string PatientAddressField = "PatientAddress";

    // A value that matches the seeded ###-###-#### mask, so the create passes validation.
    private const string PhoneNumber = "555-014-8636";

    // A FirstName holding letters only, so it can never read as a prefix, a wildcard or an alternative.
    private const string FixedFirstName = "Avery";

    // Legacy Find compares a multiline field on its first 255 characters (CollectedDataProvider.cs:2212-2222); the
    // stored text and the criteria are longer, so the characters after the prefix are shown not to count.
    private const int MultilinePrefixLength = 255;

    private const int MultilineTextLength = 300;

    // The inner run of a marker that the grid q case upper-cases: characters 5 to 16, all hexadecimal digits.
    private const int InnerStart = 4;

    private const int InnerLength = 12;

    // The list and search contract (AAP 0.3.4.1, 0.3.4.3, 0.3.4.4), written out here rather than read from
    // RequestParameters or RecordQueryRules, so a changed wire name, default, bound or cap in the code under test fails
    // these cases instead of moving their expected values with it.
    private const string PageParameter = "page";

    private const string PageSizeParameter = "pageSize";

    private const string QueryParameter = "q";

    private const string IncludeDeletedParameter = "includeDeleted";

    // A missing page is the first page, and a missing pageSize is 50.
    private const int DefaultPage = 1;

    private const int DefaultPageSize = 50;

    // The largest pageSize the list admits, and the most items one search returns.
    private const int MaxPageSize = 200;

    private const int SearchCap = 200;

    // The base-table system field a RecStatus criterion names (testdata/sql/03_forms.sql).
    private const string RecStatusField = "RecStatus";

    // The row states of C-16, typed as the seeded smallint RecStatus column holds them
    // (testdata/sql/05_data_tables.sql).
    private const short ActiveRecStatus = 1;

    private const short DeletedRecStatus = 0;

    // The selected set on the base table b (AAP C-16): every row when @includeDeleted is 1, otherwise RecStatus = 1.
    private const string SelectedSetFilter = "(@includeDeleted = 1 OR b.[RecStatus] = @active)";

    // Longer than DatabaseWriteLock's 300-second wait, so a lock held by another process fails the lock call rather
    // than this token; xUnit v2 gives a test no cancellation token of its own.
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(10);

    private readonly ITestOutputHelper _output;

    /// <summary>Creates the test class with the output the collation case writes to.</summary>
    /// <param name="output">The test output.</param>
    public ListAndSearchTests(ITestOutputHelper output)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
    }

    /// <summary>
    /// A paging parameter and a value it rejects: a <c>pageSize</c> just below and just above 1 to 200, a <c>page</c>
    /// just below 1, and a <c>page</c> that is not an integer (AAP 0.7.1, SC-5).
    /// </summary>
    public static TheoryData<string, string> InvalidPagingCases => new()
    {
        { PageSizeParameter, "0" },
        { PageSizeParameter, "201" },
        { PageParameter, "0" },
        { PageParameter, "abc" },
    };

    // AAP 0.3.4.1 and 0.3.4.4: the list is the active set (RecStatus = 1) in UniqueKey order, paged with OFFSET/FETCH
    // on a 64-bit offset. No query string is page 1 of 50, page 2 is SQL's OFFSET 50, and the largest admitted page
    // lies past the end, so it is a 200 with no items and the full total, never a 400 or a 500.
    [Theory]
    [InlineData(CaseReport, null)]
    [InlineData(CaseReport, 2)]
    [InlineData(CaseReport, int.MaxValue)]
    [InlineData(FoodHistory, null)]
    [InlineData(FoodHistory, 2)]
    [InlineData(FoodHistory, int.MaxValue)]
    public async Task ListPage_IsTheSqlPageOfTheActiveSetInUniqueKeyOrder(string form, int? page)
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        var ct = timeout.Token;
        await using var factory = await ApiFactory.ForDatabaseAsync();
        using var client = factory.CreateClient();
        await using var dbLock = await DatabaseWriteLock.AcquireSharedAsync(ct);

        var viewId = await ApiTestClient.GetViewIdAsync(client, form, ct);
        var expectedPage = page ?? DefaultPage;

        // (page - 1) x pageSize in 64-bit arithmetic, so the largest admitted page cannot wrap (AAP 0.3.4.1).
        var offset = (expectedPage - 1L) * DefaultPageSize;
        var activeCount = await CountBaseRowsAsync(form, includeDeleted: false, ct);
        var expected = await ReadActivePageAsync(form, offset, DefaultPageSize, ct);

        // The SQL page is full before the end and empty past it, so no case passes on two empty lists by accident.
        var expectedItems = (int)Math.Clamp(activeCount - offset, 0L, DefaultPageSize);
        Assert.Equal(expectedItems, expected.Length);

        var query = page is null ? string.Empty : "?" + PageParameter + "=" + Text(expectedPage);
        var list = await GetListAsync(client, viewId, query, ct);

        Assert.Equal(expectedPage, Property(list, "page").GetInt32());
        Assert.Equal(DefaultPageSize, Property(list, "pageSize").GetInt32());
        Assert.Equal(activeCount, Property(list, "total").GetInt32());
        var items = Items(list);
        Assert.Equal(expected, items.Select(item => new BaseRow(Id(item), UniqueKey(item))).ToArray());
        Assert.All(items, item => Assert.Equal(ActiveRecStatus, RecStatus(item)));

        // Page 2,147,483,647 lies past the end of every seeded form: no items, and still the full total.
        if (expectedPage == int.MaxValue)
        {
            Assert.Empty(items);
            Assert.True(activeCount > 0, "SQL shows no active record, so the empty last page proves nothing.");
        }
    }

    // AAP 0.3.4.1 (RequestParameters): a pageSize outside 1 to 200, a page below 1 and a page that is not an integer
    // are each a field-level 400 whose errors map holds exactly that parameter. Coercion runs before any store call,
    // so the case takes no lock.
    [Theory]
    [MemberData(nameof(InvalidPagingCases))]
    public async Task InvalidPaging_IsAFieldLevel400OnThatParameterOnly(string parameter, string value)
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        var ct = timeout.Token;
        await using var factory = await ApiFactory.ForDatabaseAsync();
        using var client = factory.CreateClient();

        var viewId = await ApiTestClient.GetViewIdAsync(client, CaseReport, ct);
        var path = RecordsPath(viewId, "?" + parameter + "=" + Uri.EscapeDataString(value));
        using var response = await client.GetAsync(path, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = ApiTestClient.AssertProblemDetails(
            await ApiTestClient.ReadJsonElementAsync(response, ct),
            StatusCodes.Status400BadRequest);
        var errors = Property(body, "errors");
        Assert.Equal(JsonValueKind.Object, errors.ValueKind);
        var entry = Assert.Single(errors.EnumerateObject());
        Assert.Equal(parameter, entry.Name);
        Assert.Equal(JsonValueKind.Array, entry.Value.ValueKind);
        Assert.NotEmpty(entry.Value.EnumerateArray());
    }

    // AAP 0.3.4.4 (C-5, C-16): seeded deleted records (RecStatus = 0) are listed only with includeDeleted=true. Every
    // page of both sets is walked at the largest page size, and the seeded deleted rows make the case non-vacuous.
    [Theory]
    [InlineData(CaseReport)]
    [InlineData(FoodHistory)]
    public async Task DeletedRecords_AreListedOnlyWithIncludeDeleted(string form)
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        var ct = timeout.Token;
        await using var factory = await ApiFactory.ForDatabaseAsync();
        using var client = factory.CreateClient();
        await using var dbLock = await DatabaseWriteLock.AcquireSharedAsync(ct);

        var viewId = await ApiTestClient.GetViewIdAsync(client, form, ct);
        var activeCount = await CountBaseRowsAsync(form, includeDeleted: false, ct);
        var allCount = await CountBaseRowsAsync(form, includeDeleted: true, ct);
        var deletedIds = (await ReadDeletedIdsAsync(form, ct)).ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(deletedIds);

        var (activeTotal, active) = await ListEveryPageAsync(client, viewId, includeDeleted: false, ct);
        Assert.Equal(activeCount, activeTotal);
        Assert.Equal(activeCount, active.Count);
        Assert.All(active, record => Assert.Equal(ActiveRecStatus, record.RecStatus));
        var activeIds = active.Select(record => record.GlobalRecordId).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(activeCount, activeIds.Count);
        Assert.False(activeIds.Overlaps(deletedIds), "The default list holds a record SQL shows as deleted.");

        var (allTotal, all) = await ListEveryPageAsync(client, viewId, includeDeleted: true, ct);
        Assert.Equal(allCount, allTotal);
        Assert.Equal(allCount, all.Count);
        var allIds = all.Select(record => record.GlobalRecordId).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(allCount, allIds.Count);
        Assert.Subset(allIds, deletedIds);
        Assert.All(
            all.Where(record => deletedIds.Contains(record.GlobalRecordId)),
            record => Assert.Equal(DeletedRecStatus, record.RecStatus));

        // Both walks keep UniqueKey order across page boundaries.
        foreach (var listed in new[] { active, all })
        {
            var keys = listed.Select(record => record.UniqueKey).ToArray();
            Assert.Equal(keys.Order().ToArray(), keys);
        }
    }

    // AAP 0.3.4.4, grid q: a case-insensitive "contains" over the grid columns that does not depend on the database
    // collation. Upper-case, lower-case and inner-substring variants of a stored marker each find exactly its record,
    // and a whitespace-only q applies no filter. A case-insensitive database collation alone would produce those
    // matches, so the one recorded statement of every request, which pages and counts the filtered set, is checked
    // too: it compares every grid column under Latin1_General_100_CI_AS on both sides of LIKE with ESCAPE '\', holds no
    // other LIKE, and carries the q text only as a parameter value; the blank q's statement holds no LIKE.
    [Fact]
    public async Task GridQ_MatchesTheMarkerInAnyCaseAndAsAnInnerSubstring()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        var ct = timeout.Token;
        await using var factory = await ApiFactory.ForDatabaseAsync();
        using var client = factory.CreateClient();
        await using var dbLock = await DatabaseWriteLock.AcquireExclusiveAsync(ct);

        var viewId = await ApiTestClient.GetViewIdAsync(client, CaseReport, ct);
        var marker = NewMarkerWithHexLetter();
        var created = await CreateAsync(client, viewId, new() { [TestDatabase.MarkerField] = marker }, ct);

        string[] filters =
        [
            marker.ToUpperInvariant(),
            marker.ToLowerInvariant(),
            marker.Substring(InnerStart, InnerLength).ToUpperInvariant(),
        ];
        foreach (var q in filters)
        {
            // Each filter differs from the stored text in letter case, so only a case-insensitive contains finds it.
            Assert.DoesNotContain(q, marker, StringComparison.Ordinal);
            Assert.Contains(q, marker, StringComparison.OrdinalIgnoreCase);

            factory.Recorder.Clear();
            var list = await GetListAsync(client, viewId, "?" + QueryParameter + "=" + Uri.EscapeDataString(q), ct);

            Assert.Equal(1, Property(list, "total").GetInt32());
            var item = Assert.Single(Items(list));
            Assert.Equal(created.GlobalRecordId, Id(item));
            Assert.Equal(marker, StringValue(Property(item, "values"), TestDatabase.MarkerField));
            AssertGridFilterStatements(factory.Recorder, q);
        }

        var activeCount = await CountBaseRowsAsync(CaseReport, includeDeleted: false, ct);
        factory.Recorder.Clear();
        var blank = await GetListAsync(client, viewId, "?" + QueryParameter + "=" + Uri.EscapeDataString("   "), ct);
        Assert.Equal(activeCount, Property(blank, "total").GetInt32());
        AssertGridFilterStatements(factory.Recorder, filter: null);
    }

    // The one statement one GET .../records sent under the list operation, which both pages (ORDER BY ... OFFSET ...
    // FETCH NEXT) and totals (SELECT COUNT) the filtered set. For a filter, it must hold the AAP 0.3.4.4 grid predicate
    // once per CaseReport grid column of AAP 0.3.4.3, with the explicit collation on both sides of LIKE and the escape
    // clause, no other LIKE, and one parameter bound to the filter's contains pattern, while the filter's text appears
    // in no command text. A null filter stands for a blank q, whose statement holds no LIKE. The operation name, the
    // collation and the predicate shape are written out here rather than read from the SQL builders, so a builder that
    // stops forcing the collation fails this check whatever the database collation is.
    private static void AssertGridFilterStatements(SqlCommandRecorder recorder, string? filter)
    {
        const string ListOperation = "records.list";
        string[] gridColumns = ["PhoneNumber", "LastName", "FirstName", "Address", "City", "State"];

        var statement = Assert.Single(recorder.ForOperation(ListOperation));
        Assert.True(statement.Succeeded, statement.ToString());
        var text = statement.CommandText;
        Assert.Matches(PagedSelect(), text);
        Assert.Matches(CountSelect(), text);
        if (filter is null)
        {
            Assert.Equal(0, LikeKeyword().Count(text));
            return;
        }

        var predicates = GridContains().Matches(text);
        Assert.Equal(
            gridColumns.Order(StringComparer.Ordinal),
            predicates.Select(match => match.Groups["column"].Value).Order(StringComparer.Ordinal));
        Assert.Equal(predicates.Count, LikeKeyword().Count(text));

        // The filter holds none of %, _, [ and \, so its contains pattern is the filter wrapped in %.
        Assert.False(filter.AsSpan().ContainsAny(@"%_[\"), "The filter holds a LIKE pattern character.");
        var name = Assert.Single(predicates.Select(match => match.Groups["parameter"].Value).Distinct());
        var bound = Assert.Single(
            statement.Parameters,
            parameter => string.Equals(parameter.Name, name, StringComparison.Ordinal));
        Assert.Equal("%" + filter + "%", Assert.IsType<string>(bound.Value));
        Assert.DoesNotContain(filter, text, StringComparison.OrdinalIgnoreCase);
    }

    // AAP 0.3.4.4, multiline: legacy Find compares a multiline field as
    // SUBSTRING(col, 1, 255) = SUBSTRING(@p, 1, 255) (CollectedDataProvider.cs:2212-2222). A 300-character criterion
    // that keeps the stored first 255 characters and changes the rest finds the record; one that differs within the
    // first 255 does not. The texts hold only hexadecimal digits and 'z', so no space (and so no " or "), wildcard or
    // comparison prefix can give them another meaning.
    [Fact]
    public async Task Multiline_IsComparedOnItsFirst255CharactersOnly()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        var ct = timeout.Token;
        await using var factory = await ApiFactory.ForDatabaseAsync();
        using var client = factory.CreateClient();
        await using var dbLock = await DatabaseWriteLock.AcquireExclusiveAsync(ct);

        var viewId = await ApiTestClient.GetViewIdAsync(client, FoodHistory, ct);
        var marker = TestDatabase.NewMarker();
        var address = LettersAndDigits(MultilineTextLength);
        var created = await CreateAsync(
            client,
            viewId,
            new() { [TestDatabase.MarkerField] = marker, [PatientAddressField] = address },
            ct);

        var sameStart = address[..MultilinePrefixLength] +
            new string('z', MultilineTextLength - MultilinePrefixLength);
        var differentStart = address[..100] + "z" + address[101..];
        Assert.Equal(MultilineTextLength, sameStart.Length);
        Assert.Equal(MultilineTextLength, differentStart.Length);
        Assert.NotEqual(address, sameStart);

        var found = await SearchAsync(
            client,
            viewId,
            [new(TestDatabase.MarkerField, marker), new(PatientAddressField, sameStart)],
            ct: ct);
        AssertFound(found, created.GlobalRecordId);

        var missed = await SearchAsync(
            client,
            viewId,
            [new(TestDatabase.MarkerField, marker), new(PatientAddressField, differentStart)],
            ct: ct);
        AssertFound(missed);
    }

    // AAP 0.3.4.4, Phone Number: the legacy PhoneNumber path compares the column as a string, uses the written prefix
    // as the operator and has no wildcard path (CollectedDataProvider.cs:2118-2131), so "555-014-86*" is an equality
    // on that literal text.
    [Theory]
    [InlineData(">555-014-8635", true)]
    [InlineData("<555-014-8636", false)]
    [InlineData("<>555-014-8636", false)]
    [InlineData("555-014-86*", false)]
    public async Task PhoneNumber_ComparesAsAStringWithThePrefixAsTheOperator(string criterion, bool finds)
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        var ct = timeout.Token;
        await using var factory = await ApiFactory.ForDatabaseAsync();
        using var client = factory.CreateClient();
        await using var dbLock = await DatabaseWriteLock.AcquireExclusiveAsync(ct);

        var viewId = await ApiTestClient.GetViewIdAsync(client, CaseReport, ct);
        var marker = TestDatabase.NewMarker();
        var created = await CreateAsync(
            client,
            viewId,
            new() { [TestDatabase.MarkerField] = marker, [PhoneNumberField] = PhoneNumber },
            ct);

        var response = await SearchAsync(
            client,
            viewId,
            [new(TestDatabase.MarkerField, marker), new(PhoneNumberField, criterion)],
            ct: ct);

        AssertFound(response, finds ? [created.GlobalRecordId] : []);
    }

    // AAP 0.3.4.4, text: a bare comparison prefix leaves no value, which legacy Find turns into IS NULL
    // (CollectedDataProvider.cs:2186-2193), and a prefix before a text value is stripped and ignored
    // (FindRecordDialog.cs:708-770), so ">" + marker and "<>" + marker are both equality on the marker.
    [Fact]
    public async Task TextPrefix_BareIsNullAndBeforeAValueIsIgnored()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        var ct = timeout.Token;
        await using var factory = await ApiFactory.ForDatabaseAsync();
        using var client = factory.CreateClient();
        await using var dbLock = await DatabaseWriteLock.AcquireExclusiveAsync(ct);

        var viewId = await ApiTestClient.GetViewIdAsync(client, CaseReport, ct);
        var marker = TestDatabase.NewMarker();
        var withoutFirstName = await CreateAsync(client, viewId, new() { [TestDatabase.MarkerField] = marker }, ct);
        var withFirstName = await CreateAsync(
            client,
            viewId,
            new() { [TestDatabase.MarkerField] = marker, [FirstNameField] = FixedFirstName },
            ct);
        Assert.IsType<DBNull>(await ReadFirstNameAsync(withoutFirstName.GlobalRecordId, ct));
        Assert.Equal(
            FixedFirstName,
            Assert.IsType<string>(await ReadFirstNameAsync(withFirstName.GlobalRecordId, ct)));

        var bare = await SearchAsync(
            client,
            viewId,
            [new(TestDatabase.MarkerField, marker), new(FirstNameField, ">")],
            ct: ct);
        AssertFound(bare, withoutFirstName.GlobalRecordId);

        foreach (var prefix in new[] { ">", "<>" })
        {
            var prefixed = await SearchAsync(client, viewId, [new(TestDatabase.MarkerField, prefix + marker)], ct: ct);
            AssertFound(prefixed, withoutFirstName.GlobalRecordId, withFirstName.GlobalRecordId);
        }
    }

    // AAP 0.3.4.4: Find INNER JOINs the base table to every page table, which reproduces legacy hydration dropping a
    // base row that lacks a page row (CollectedDataProvider.cs:2289-2298, 2357-2375). The position endpoint reads the
    // base table only, so the same record is still placed in both sets.
    [Fact]
    public async Task MissingPageRow_HidesTheRecordFromFindButNotFromThePositionEndpoint()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        var ct = timeout.Token;
        await using var factory = await ApiFactory.ForDatabaseAsync();
        using var client = factory.CreateClient();
        await using var dbLock = await DatabaseWriteLock.AcquireExclusiveAsync(ct);

        var viewId = await ApiTestClient.GetViewIdAsync(client, FoodHistory, ct);
        var marker = TestDatabase.NewMarker();
        var created = await CreateAsync(client, viewId, new() { [TestDatabase.MarkerField] = marker }, ct);
        var id = created.GlobalRecordId;
        AssertFound(await SearchAsync(client, viewId, [new(TestDatabase.MarkerField, marker)], ct: ct), id);

        // The create wrote a FoodHistory3 row; removing it is the one direct write the tests make on a record of
        // their own.
        Assert.Equal(1, await CountPageTwoRowsAsync(id, ct));
        Assert.Equal(1, await DeletePageTwoRowAsync(id, ct));
        Assert.Equal(0, await CountPageTwoRowsAsync(id, ct));

        AssertFound(await SearchAsync(client, viewId, [new(TestDatabase.MarkerField, marker)], ct: ct));

        var uniqueKey = await ReadUniqueKeyAsync(FoodHistory, id, ct);
        foreach (var includeDeleted in new[] { false, true })
        {
            var (position, count) = await GetPositionAsync(client, viewId, id, includeDeleted, ct);
            Assert.Equal(await CountUpToAsync(FoodHistory, uniqueKey, includeDeleted, ct), position);
            Assert.Equal(await CountBaseRowsAsync(FoodHistory, includeDeleted, ct), count);
        }
    }

    // AAP 0.3.4.4 (C-19): excludeGlobalRecordId removes the open record on the server before total is counted. The
    // alternatives of one field, split on " or " (FindRecordDialog.cs:708-770), combine with OR, and criteria on
    // different fields combine with AND. Each record's marker stays in LastName, the one marker field (AAP 0.7.5). The
    // FirstName both records share, and the FirstName neither holds, are fresh lowercase hexadecimal text instead:
    // unique to this run, so no earlier run's record matches, and never read as a prefix, a wildcard or an alternative.
    [Fact]
    public async Task Exclusion_Alternatives_AndCriteria_SelectTheExpectedRecords()
    {
        const int firstNameLength = 32;
        using var timeout = new CancellationTokenSource(TestTimeout);
        var ct = timeout.Token;
        await using var factory = await ApiFactory.ForDatabaseAsync();
        using var client = factory.CreateClient();
        await using var dbLock = await DatabaseWriteLock.AcquireExclusiveAsync(ct);

        var viewId = await ApiTestClient.GetViewIdAsync(client, CaseReport, ct);
        var markerA = TestDatabase.NewMarker();
        var markerB = TestDatabase.NewMarker();
        var sharedFirstName = LettersAndDigits(firstNameLength);
        var absentFirstName = LettersAndDigits(firstNameLength);
        var a = (await CreateAsync(
            client, viewId, new() { [TestDatabase.MarkerField] = markerA, [FirstNameField] = sharedFirstName }, ct))
            .GlobalRecordId;
        var b = (await CreateAsync(
            client, viewId, new() { [TestDatabase.MarkerField] = markerB, [FirstNameField] = sharedFirstName }, ct))
            .GlobalRecordId;

        AssertFound(await SearchAsync(client, viewId, [new(FirstNameField, sharedFirstName)], ct: ct), a, b);
        AssertFound(await SearchAsync(client, viewId, [new(FirstNameField, sharedFirstName)], exclude: a, ct: ct), b);

        var either = markerA + " or " + markerB;
        AssertFound(await SearchAsync(client, viewId, [new(TestDatabase.MarkerField, either)], ct: ct), a, b);
        AssertFound(
            await SearchAsync(
                client,
                viewId,
                [new(TestDatabase.MarkerField, either), new(FirstNameField, sharedFirstName)],
                ct: ct),
            a,
            b);
        AssertFound(
            await SearchAsync(
                client,
                viewId,
                [new(TestDatabase.MarkerField, markerA), new(FirstNameField, sharedFirstName)],
                ct: ct),
            a);
        AssertFound(
            await SearchAsync(
                client,
                viewId,
                [new(TestDatabase.MarkerField, markerA), new(FirstNameField, absentFirstName)],
                ct: ct));
    }


    // AAP 0.3.4.4 (C-16, C-17): a result's position is its ROW_NUMBER over the selected set by UniqueKey, taken before
    // the criteria apply, so it equals the position endpoint over the same set and the legacy record number, the
    // count of rows with UniqueKey <= k (RunTimeView.cs:673-694). A RecStatus criterion stays inside the selected set.
    [Fact]
    public async Task SearchPositions_FollowTheSelectedSet()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        var ct = timeout.Token;
        await using var factory = await ApiFactory.ForDatabaseAsync();
        using var client = factory.CreateClient();
        await using var dbLock = await DatabaseWriteLock.AcquireExclusiveAsync(ct);

        var viewId = await ApiTestClient.GetViewIdAsync(client, CaseReport, ct);
        var marker = TestDatabase.NewMarker();
        var created = await CreateAsync(client, viewId, new() { [TestDatabase.MarkerField] = marker }, ct);
        var uniqueKey = await ReadUniqueKeyAsync(CaseReport, created.GlobalRecordId, ct);
        var activePosition = await CountUpToAsync(CaseReport, uniqueKey, includeDeleted: false, ct);
        var allPosition = await CountUpToAsync(CaseReport, uniqueKey, includeDeleted: true, ct);

        // Seeded deleted rows precede every record a test creates, so the two sets number this record differently.
        Assert.True(
            allPosition > activePosition,
            "SQL shows no deleted row before the created record, so the two sets cannot be told apart.");

        foreach (var (includeDeleted, expected) in new[] { (false, activePosition), (true, allPosition) })
        {
            var response = await SearchAsync(
                client, viewId, [new(TestDatabase.MarkerField, marker)], includeDeleted, ct: ct);
            var item = Assert.Single(Items(response));
            Assert.Equal(created.GlobalRecordId, Id(item));
            Assert.Equal(expected, Position(item));
            var (position, _) = await GetPositionAsync(client, viewId, created.GlobalRecordId, includeDeleted, ct);
            Assert.Equal(expected, position);
        }

        // The seeded deleted records, searched in the set that holds them.
        var deletedTotal = await CountSearchMatchesAsync(CaseReport, includeDeleted: true, DeletedRecStatus, ct);
        var expectedDeleted = await ReadSearchMatchesAsync(
            CaseReport, includeDeleted: true, DeletedRecStatus, SearchCap, ct);
        Assert.NotEmpty(expectedDeleted);

        var deletedCriterion = new CriterionBody(RecStatusField, Text(DeletedRecStatus));
        var deleted = await SearchAsync(client, viewId, [deletedCriterion], includeDeleted: true, ct: ct);
        Assert.Equal(deletedTotal, Property(deleted, "total").GetInt32());
        var deletedItems = Items(deleted);
        Assert.Equal(
            expectedDeleted,
            deletedItems.Select(item => new SearchMatch(Id(item), Position(item))).ToArray());
        Assert.All(deletedItems, item => Assert.Equal(DeletedRecStatus, RecStatus(item)));
        await AssertEndpointPositionsAsync(client, viewId, deletedItems, includeDeleted: true, ct);

        AssertFound(await SearchAsync(client, viewId, [deletedCriterion], ct: ct));
    }

    // SQL Server's LIKE and PATINDEX take a pattern of at most 4,000 UTF-16 code units (error 8152 beyond it), a limit
    // the q and Find semantics do not have; the two cases below straddle it with this literal of their own.
    private const int LikePatternLimit = 4000;

    // AAP 0.3.4.4, grid q: every nonblank q is a case-insensitive literal "contains", whatever its length. Four records
    // hold the marker followed, to 4,000 characters, by fresh hexadecimal text, by '_' only, by '%' only and by 'x'
    // only. A q of 3,998 characters, whose contains pattern is exactly 4,000 units and runs as the native LIKE, and
    // longer ones, in either case and as an inner part, find the hexadecimal record; one character changed finds
    // nothing; and a q of the marker and '_' or '%' finds only the record holding those characters, so neither acts as
    // a wildcard. The expected records are the marked rows SQL reads whose LastName contains q ignoring case. Every
    // statement carries q only as a parameter, and only a contains pattern past the limit runs the long-pattern
    // recursion.
    [Fact]
    public async Task GridQ_PastTheLikePatternLimit_IsALiteralCaseInsensitiveContains()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        var ct = timeout.Token;
        await using var factory = await ApiFactory.ForDatabaseAsync();
        using var client = factory.CreateClient();
        await using var dbLock = await DatabaseWriteLock.AcquireExclusiveAsync(ct);

        var viewId = await ApiTestClient.GetViewIdAsync(client, CaseReport, ct);
        var marker = TestDatabase.NewMarker();
        var fill = LikePatternLimit - marker.Length;
        var hex = marker + LettersAndDigits(fill);
        var underscores = marker + new string('_', fill);
        var percents = marker + new string('%', fill);
        foreach (var lastName in new[] { hex, underscores, percents, marker + new string('x', fill) })
        {
            await CreateAsync(client, viewId, new() { [TestDatabase.MarkerField] = lastName }, ct);
        }

        var marked = await ReadLastNamesLikeAsync(marker + "%", ct);
        Assert.Equal(4, marked.Length);

        // Each q with the number of marked records whose LastName contains it; the hexadecimal text holds no 'z'.
        (string Q, int Matches)[] filters =
        [
            (hex[..(LikePatternLimit - 2)], 1),
            (hex[..(LikePatternLimit - 2)].ToUpperInvariant(), 1),
            (hex[..(LikePatternLimit - 1)], 1),
            (hex.ToUpperInvariant(), 1),
            (hex[1..].ToUpperInvariant(), 1),
            (hex[..^1] + "z", 0),
            (underscores, 1),
            (percents, 1),
        ];
        foreach (var (q, matches) in filters)
        {
            var expected = marked
                .Where(row => row.LastName.Contains(q, StringComparison.OrdinalIgnoreCase))
                .Select(row => row.Id)
                .ToArray();
            Assert.Equal(matches, expected.Length);

            factory.Recorder.Clear();
            var list = await GetListAsync(client, viewId, "?" + QueryParameter + "=" + Uri.EscapeDataString(q), ct);

            Assert.Equal(expected.Length, Property(list, "total").GetInt32());
            Assert.Equal(expected, Items(list).Select(Id).ToArray());

            // The contains pattern escapes each %, _, [ and \ with one more unit and wraps q in two '%'.
            var patternLength = q.Length + 2 + q.Count(character => character is '%' or '_' or '[' or '\\');
            AssertLikeStatements(
                factory.Recorder, "records.list", 1, marker[^32..], longPattern: patternLength > LikePatternLimit);
        }
    }

    // AAP 0.3.4.4, grid q past the pattern limit keeps the contains of Latin1_General_100_CI_AS, under which an
    // expansion matches across the comparison, whatever the database collation. Two records hold the marker and fresh
    // hexadecimal text with "ae" in it, 4,000 characters each, the first storing it as "æ" and the second as written.
    // The first text with "ae", 4,001 characters, in either case, finds the first record, and so does its
    // 3,998-character prefix, whose contains pattern runs as the native LIKE, as SQL Server's own LIKE under that
    // collation finds it; the second text with "æ", 3,999 characters, in either case, finds the second record; and the
    // first with its last character changed finds nothing.
    [Fact]
    public async Task GridQ_PastTheLikePatternLimit_KeepsTheCollationsExpansions()
    {
        const int split = 1000;
        const string ligature = "\u00E6";
        using var timeout = new CancellationTokenSource(TestTimeout);
        var ct = timeout.Token;
        await using var factory = await ApiFactory.ForDatabaseAsync();
        using var client = factory.CreateClient();
        await using var dbLock = await DatabaseWriteLock.AcquireExclusiveAsync(ct);

        var viewId = await ApiTestClient.GetViewIdAsync(client, CaseReport, ct);
        var marker = TestDatabase.NewMarker();
        var firstHex = LettersAndDigits(LikePatternLimit - marker.Length - 1);
        var secondHex = LettersAndDigits(LikePatternLimit - marker.Length - 2);
        var firstWithLigature = marker + firstHex[..split] + ligature + firstHex[split..];
        var firstWithAe = marker + firstHex[..split] + "ae" + firstHex[split..];
        var secondWithAe = marker + secondHex[..split] + "ae" + secondHex[split..];
        var secondWithLigature = marker + secondHex[..split] + ligature + secondHex[split..];
        Assert.Equal(LikePatternLimit, firstWithLigature.Length);
        Assert.Equal(LikePatternLimit, secondWithAe.Length);
        var first = await CreateAsync(client, viewId, new() { [TestDatabase.MarkerField] = firstWithLigature }, ct);
        var second = await CreateAsync(client, viewId, new() { [TestDatabase.MarkerField] = secondWithAe }, ct);

        var nativePrefix = firstWithAe[..(LikePatternLimit - 2)];
        Assert.Equal(new[] { first.GlobalRecordId }, await ReadGridContainsAsync(nativePrefix, ct));

        (string Q, string[] Expected)[] filters =
        [
            (firstWithAe, new[] { first.GlobalRecordId }),
            (firstWithAe.ToUpperInvariant(), new[] { first.GlobalRecordId }),
            (nativePrefix, new[] { first.GlobalRecordId }),
            (secondWithLigature, new[] { second.GlobalRecordId }),
            (secondWithLigature.ToUpperInvariant(), new[] { second.GlobalRecordId }),
            (firstWithAe[..^1] + "z", Array.Empty<string>()),
        ];
        foreach (var (q, expected) in filters)
        {
            factory.Recorder.Clear();
            var list = await GetListAsync(client, viewId, "?" + QueryParameter + "=" + Uri.EscapeDataString(q), ct);

            Assert.Equal(expected.Length, Property(list, "total").GetInt32());
            Assert.Equal(expected, Items(list).Select(Id).ToArray());
            AssertLikeStatements(
                factory.Recorder, "records.list", 1, marker[^32..], longPattern: q.Length + 2 > LikePatternLimit);
        }
    }

    // The ids of the active CaseReport records, in UniqueKey order, whose LastName contains the filter, of at most
    // 3,998 characters, as SQL Server's own LIKE finds it under Latin1_General_100_CI_AS with the filter's LIKE
    // characters escaped.
    private static Task<string[]> ReadGridContainsAsync(string filter, CancellationToken ct) =>
        ReadRowsAsync(
            $"SELECT b.[GlobalRecordId] FROM {Tables(CaseReport).Base} AS b{PageJoins(CaseReport, "b")}" +
            " WHERE b.[RecStatus] = @active AND p0.[LastName] COLLATE Latin1_General_100_CI_AS" +
            " LIKE @pattern COLLATE Latin1_General_100_CI_AS ESCAPE '\\' ORDER BY b.[UniqueKey];",
            reader => reader.GetString(0),
            ct,
            new SqlParameter("@active", SqlDbType.SmallInt) { Value = ActiveRecStatus },
            new SqlParameter("@pattern", SqlDbType.NVarChar, LikePatternLimit)
            {
                Value = "%" + filter.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[")
                    + "%",
            });

    // AAP 0.3.4.4, Find: a criterion holding '*' is col LIKE @p with each '*' as '%' and '_' and [...] keeping their
    // SQL meaning, under the database collation, whatever its length. Two records hold the marker followed, to 4,000
    // characters, by fresh hexadecimal text, the second with its last character changed to 'z'; a third holds the
    // marker and other text with a supplementary character in it; and a fourth the marker and other text with "æ" in
    // it, between two digits, so no letter of the text sits beside it or its expansion. Each pattern finds exactly the
    // records SQL Server's own LIKE finds with an equivalent pattern within the limit: a 3,999-character prefix and
    // '*', 4,000 units and the native LIKE, finds the first two; each whole text and '*', 4,001 units, finds its own
    // record, since a LastName holds at most 4,000 characters; a text with one character changed and '*' finds none;
    // a 4,700-unit pattern of a prefix, '*', a middle written as one-member sets with one '_', '*' and a suffix finds
    // the records its suffix selects; the marker's first 33 characters before a set of its 34th repeated 4,096 times
    // and '*' find the four records, and with a set of a character no marker holds none; and the fourth text with "ae"
    // for "æ" and '*', 4,002 units, as stored and upper-cased, finds what its first 3,999 characters and '%' find: the
    // fourth record where the collation matches "æ" with that "ae", as a shorter native LIKE shows first, and nothing
    // otherwise; with one more character changed it finds none. Only a pattern past the limit runs the long-pattern
    // recursion, every value is a parameter, and a pattern holding a set longer than 4,000 units that is unclosed or
    // that no cut keeping its members divides, which no SQL Server construct evaluates, is the one field-level 400.
    [Fact]
    public async Task Find_PatternsPastTheLikePatternLimit_FindWhatLikeFinds()
    {
        const string supplementary = "\uD83D\uDE00";
        const string ligature = "\u00E6";
        const int ligatureAt = 1000;
        using var timeout = new CancellationTokenSource(TestTimeout);
        var ct = timeout.Token;
        await using var factory = await ApiFactory.ForDatabaseAsync();
        using var client = factory.CreateClient();
        await using var dbLock = await DatabaseWriteLock.AcquireExclusiveAsync(ct);

        var viewId = await ApiTestClient.GetViewIdAsync(client, CaseReport, ct);
        var marker = TestDatabase.NewMarker();
        var first = marker + LettersAndDigits(LikePatternLimit - marker.Length);
        var second = first[..^1] + "z";
        var thirdHex = LettersAndDigits(LikePatternLimit - marker.Length - supplementary.Length);
        var third = marker + thirdHex[..1500] + supplementary + thirdHex[1500..];

        // "æ" after 1,000 hexadecimal characters, with a '0' on either side.
        var fourthHex = LettersAndDigits(LikePatternLimit - marker.Length - 3);
        var fourth = marker + fourthHex[..ligatureAt] + "0" + ligature + "0" + fourthHex[ligatureAt..];
        Assert.Equal(LikePatternLimit, fourth.Length);
        var ids = new List<string>();
        foreach (var lastName in new[] { first, second, third, fourth })
        {
            var created = await CreateAsync(client, viewId, new() { [TestDatabase.MarkerField] = lastName }, ct);
            ids.Add(created.GlobalRecordId);
        }

        // Whether the LastName collation matches the fourth record's "æ" with "ae", as stored and upper-cased, as SQL
        // Server's own LIKE of the text with "ae" for "æ" up to 100 characters past it and '%' shows.
        var withAe = fourth.Replace(ligature, "ae", StringComparison.Ordinal);
        var upperWithAe = withAe.ToUpperInvariant();
        var throughLigature = marker.Length + ligatureAt + 100;
        var expands = (await ReadLastNamesLikeAsync(withAe[..throughLigature] + "%", ct)).Any(row => row.Id == ids[3]);
        var expandsUpper = (await ReadLastNamesLikeAsync(upperWithAe[..throughLigature] + "%", ct))
            .Any(row => row.Id == ids[3]);
        _output.WriteLine(
            $"The LastName collation {(expands ? "matches" : "does not match")} U+00E6 with \"ae\" and "
            + $"{(expandsUpper ? "matches" : "does not match")} it with \"AE\" in upper-cased text.");
        var changedAt = withAe.Length - 500;
        var changed = withAe[..changedAt] + "z" + withAe[(changedAt + 1)..];

        // Characters 1,500 to 2,499 of the first text with the tenth as '_', plain and with every other one as a set.
        var head = first[..1500];
        var plainMiddle = first[1500..1509] + "_" + first[1510..2500];
        var setMiddle = string.Concat(plainMiddle.Select(character => character == '_' ? "_" : "[" + character + "]"));
        (string Criterion, string Equivalent, int Matches)[] cases =
        [
            (first[..^1] + "*", first[..^1] + "%", 2),
            (first + "*", first, 1),
            (second + "*", second, 1),
            (third + "*", third, 1),
            (fourth + "*", fourth, 1),
            (first[..^2] + "y" + first[^1] + "*", first[..^2] + "y" + first[^1], 0),
            (head + "*" + setMiddle + "*" + first[3800..], head + "%" + plainMiddle + "%" + first[3800..], 1),
            (
                head + "*" + setMiddle + "*" + first[3800..^1] + "_",
                head + "%" + plainMiddle + "%" + first[3800..^1] + "_",
                2),
            (marker[..33] + "[" + new string(marker[33], 4096) + "]*", marker[..33] + "[" + marker[33] + "]%", 4),
            (marker[..33] + "[" + new string('z', 4096) + "]*", marker[..33] + "[z]%", 0),
            (withAe + "*", withAe[..(LikePatternLimit - 1)] + "%", expands ? 1 : 0),
            (upperWithAe + "*", upperWithAe[..(LikePatternLimit - 1)] + "%", expandsUpper ? 1 : 0),
            (changed + "*", changed[..(LikePatternLimit - 1)] + "%", 0),
        ];
        foreach (var (criterion, equivalent, matches) in cases)
        {
            Assert.True(equivalent.Length <= LikePatternLimit, "The equivalent pattern is past the LIKE limit.");
            var expected = (await ReadLastNamesLikeAsync(equivalent, ct)).Select(row => row.Id).ToArray();
            Assert.Equal(matches, expected.Length);

            factory.Recorder.Clear();
            var response = await SearchAsync(client, viewId, [new(TestDatabase.MarkerField, criterion)], ct: ct);

            AssertFound(response, expected);
            AssertLikeStatements(
                factory.Recorder, "records.search", 1, marker[^32..], longPattern: criterion.Length > LikePatternLimit);
        }

        // A literal run repeating the third record's supplementary character 1,300 times matches that record only
        // where the column's collation ignores the character, as SQL_Latin1_General_CP1_CI_AS does, which SQL Server's
        // own LIKE shows when the text around the character, without it, finds the record. Then the 4,235-unit
        // pattern finds what native LIKE of the 1,637-unit pattern holding the character once finds; otherwise it
        // finds nothing, since no value repeats the character.
        var before = third[..(marker.Length + 1500)];
        var after = third[(marker.Length + 1502)..(marker.Length + 1602)];
        var ignored = (await ReadLastNamesLikeAsync(before + after + "%", ct)).Any(row => row.Id == ids[2]);
        _output.WriteLine("The LastName collation " + (ignored ? "ignores" : "matches") + " U+1F600.");
        var repeated = before + string.Concat(Enumerable.Repeat(supplementary, 1300)) + after + "*";
        var expectedRepeated = ignored
            ? (await ReadLastNamesLikeAsync(before + supplementary + after + "%", ct)).Select(row => row.Id).ToArray()
            : Array.Empty<string>();
        Assert.Equal(ignored ? new[] { ids[2] } : Array.Empty<string>(), expectedRepeated);
        factory.Recorder.Clear();
        var found = await SearchAsync(client, viewId, [new(TestDatabase.MarkerField, repeated)], ct: ct);
        AssertFound(found, expectedRepeated);
        AssertLikeStatements(factory.Recorder, "records.search", 1, marker[^32..], longPattern: true);

        // The residual sets: unclosed, and a chain of ranges with no cut that keeps them.
        foreach (var unsearchable in new[]
                 {
                     marker + "*[" + new string('a', LikePatternLimit + 1),
                     marker + "*[" + string.Concat(Enumerable.Repeat("a-", 2001)) + "a]",
                 })
        {
            var body = new Dictionary<string, object?>
            {
                ["criteria"] = new[] { new CriterionBody(TestDatabase.MarkerField, unsearchable) },
            };
            using var rejected = await ApiTestClient.PostJsonAsync(client, RecordsPath(viewId, "/search"), body, ct);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            var errors = Property(await ApiTestClient.ReadJsonElementAsync(rejected, ct), "errors");
            Assert.Equal(
                UnsearchableSetMessage,
                Assert.Single(Property(errors, TestDatabase.MarkerField).EnumerateArray()).GetString());
        }
    }

    // The fixed 400 text of a LastName pattern holding a set no SQL Server construct evaluates.
    private const string UnsearchableSetMessage =
        "LastName search patterns cannot hold a [...] set longer than 4000 characters that has no closing ] or that"
        + " cannot be cut into sets of at most 3998 characters that keep its members and negation.";

    // The statements one list or Find request sent under its operation, as many as given: each succeeded, holds no part
    // of the submitted text, which travels only as a parameter value, and carries OPTION (MAXRECURSION 0), which only
    // the long-pattern recursion needs, exactly when the pattern is past the LIKE pattern limit.
    private static void AssertLikeStatements(
        SqlCommandRecorder recorder,
        string operation,
        int count,
        string submitted,
        bool longPattern)
    {
        var statements = recorder.ForOperation(operation);
        Assert.Equal(count, statements.Count);
        foreach (var statement in statements)
        {
            Assert.True(statement.Succeeded, statement.ToString());
            Assert.DoesNotContain(submitted, statement.CommandText, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(longPattern, statement.CommandText.Contains("MAXRECURSION 0", StringComparison.Ordinal));
        }
    }

    // The active CaseReport records whose LastName is LIKE the pattern, of at most 4,000 units, in UniqueKey order, as
    // SQL Server's own LIKE under the database collation selects them, with that LastName.
    private static Task<(string Id, string LastName)[]> ReadLastNamesLikeAsync(string pattern, CancellationToken ct) =>
        ReadRowsAsync<(string Id, string LastName)>(
            $"SELECT b.[GlobalRecordId], p0.[LastName] FROM {Tables(CaseReport).Base} AS b{PageJoins(CaseReport, "b")}" +
            " WHERE b.[RecStatus] = @active AND p0.[LastName] LIKE @pattern ORDER BY b.[UniqueKey];",
            reader => (reader.GetString(0), reader.GetString(1)),
            ct,
            new SqlParameter("@active", SqlDbType.SmallInt) { Value = ActiveRecStatus },
            new SqlParameter("@pattern", SqlDbType.NVarChar, LikePatternLimit) { Value = pattern });

    // AAP 0.3.4.4 "Order and cap" (C-19): results are ordered by UniqueKey and capped at 200, total counts every match
    // and truncated is total > 200; the open record is excluded before total and the cap, so the 201st match moves up
    // into the page. Read-only, so the case holds the lock shared.
    [Fact]
    public async Task Cap_OrdersByUniqueKey_CountsEveryMatch_AndExcludesBeforeCapping()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        var ct = timeout.Token;
        await using var factory = await ApiFactory.ForDatabaseAsync();
        using var client = factory.CreateClient();
        await using var dbLock = await DatabaseWriteLock.AcquireSharedAsync(ct);

        var viewId = await ApiTestClient.GetViewIdAsync(client, CaseReport, ct);
        var expectedTotal = await CountSearchMatchesAsync(CaseReport, includeDeleted: false, ActiveRecStatus, ct);

        // Every CaseReport base row has its page row (the 99_verify orphan check), so the matches are the active set.
        Assert.Equal(await CountBaseRowsAsync(CaseReport, includeDeleted: false, ct), expectedTotal);
        Assert.True(
            expectedTotal > SearchCap + 1,
            "SQL shows too few active CaseReport records for the cap to apply after an exclusion.");
        var expected = await ReadSearchMatchesAsync(
            CaseReport, includeDeleted: false, ActiveRecStatus, SearchCap + 1, ct);
        var criterion = new CriterionBody(RecStatusField, Text(ActiveRecStatus));

        var capped = await SearchAsync(client, viewId, [criterion], ct: ct);
        Assert.Equal(expectedTotal, Property(capped, "total").GetInt32());
        Assert.True(Property(capped, "truncated").GetBoolean());
        var cappedItems = Items(capped);
        Assert.Equal(SearchCap, cappedItems.Count);
        Assert.Equal(
            expected.Take(SearchCap).ToArray(),
            cappedItems.Select(item => new SearchMatch(Id(item), Position(item))).ToArray());
        await AssertEndpointPositionsAsync(client, viewId, cappedItems, includeDeleted: false, ct);

        var excludedId = expected[0].GlobalRecordId;
        var excluded = await SearchAsync(client, viewId, [criterion], exclude: excludedId, ct: ct);
        Assert.Equal(expectedTotal - 1, Property(excluded, "total").GetInt32());
        Assert.True(Property(excluded, "truncated").GetBoolean());
        var excludedItems = Items(excluded);
        Assert.Equal(SearchCap, excludedItems.Count);
        Assert.Equal(
            expected.Skip(1).ToArray(),
            excludedItems.Select(item => new SearchMatch(Id(item), Position(item))).ToArray());
        Assert.DoesNotContain(excludedItems, item => Id(item) == excludedId);
        Assert.Equal(expected[SearchCap].GlobalRecordId, Id(excludedItems[^1]));
    }

    // AAP 0.3.4.4 and SC-5: a criterion that matches nothing is a 200 with no items, a total of 0 and truncated false,
    // never a 404. The marker is fresh, so no record holds it.
    [Theory]
    [InlineData(CaseReport)]
    [InlineData(FoodHistory)]
    public async Task NoMatch_IsAnEmpty200(string form)
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        var ct = timeout.Token;
        await using var factory = await ApiFactory.ForDatabaseAsync();
        using var client = factory.CreateClient();
        await using var dbLock = await DatabaseWriteLock.AcquireSharedAsync(ct);

        var viewId = await ApiTestClient.GetViewIdAsync(client, form, ct);
        var response = await SearchAsync(
            client, viewId, [new(TestDatabase.MarkerField, TestDatabase.NewMarker())], ct: ct);

        AssertFound(response);
    }

    // AAP 0.3.4.4 "Collation": Find's equality and LIKE compare under the database collation exactly as legacy did, and
    // testdata/sql/00_create_database.sql pins none, so the case assumes no collation and reads no meaning into its
    // name. The collation found is written to the test output for the "Search semantics" section of
    // docs/parity-notes.md, and LastName must carry it. Each expected result is direct SQL shaped as Find selects it,
    // comparing on that column with no COLLATE clause: an equality on the marker, a LIKE through '*' on its first
    // characters, and one through '_' in place of its hyphen. The marker's own case must find the created record, so no
    // comparison passes on two empty results; the upper-cased marker differs from it inside the LIKE prefix too, so the
    // collation alone decides whether it matches, and case-insensitive, case-sensitive and binary collations all pass.
    [Fact]
    public async Task Find_FollowsTheDatabaseCollation()
    {
        // The marker's leading characters a LIKE criterion keeps: they hold the inner run NewMarkerWithHexLetter
        // gives a hexadecimal letter.
        const int likePrefixLength = InnerStart + InnerLength;
        using var timeout = new CancellationTokenSource(TestTimeout);
        var ct = timeout.Token;
        await using var factory = await ApiFactory.ForDatabaseAsync();
        using var client = factory.CreateClient();
        await using var dbLock = await DatabaseWriteLock.AcquireExclusiveAsync(ct);

        var collation = await ReadDatabaseCollationAsync(ct);
        _output.WriteLine("Database collation: " + collation);
        Assert.Equal(collation, await ReadLastNameCollationAsync(ct));

        var viewId = await ApiTestClient.GetViewIdAsync(client, CaseReport, ct);
        var marker = NewMarkerWithHexLetter();
        var created = await CreateAsync(client, viewId, new() { [TestDatabase.MarkerField] = marker }, ct);
        var upper = marker.ToUpperInvariant();
        Assert.NotEqual(marker[..likePrefixLength], upper[..likePrefixLength]);

        foreach (var (text, storedCase) in new[] { (marker, true), (upper, false) })
        {
            var prefix = text[..likePrefixLength];
            var underscored = prefix[..1] + "_" + prefix[2..];
            (string Criterion, bool Like, string SqlValue)[] comparisons =
            [
                (text, false, text),
                (prefix + "*", true, prefix + "%"),
                (underscored + "*", true, underscored + "%"),
            ];
            foreach (var (criterion, like, sqlValue) in comparisons)
            {
                var expected = await ReadLastNameMatchesAsync(like, sqlValue, ct);
                var matched = expected.Contains(created.GlobalRecordId, StringComparer.Ordinal);
                _output.WriteLine(
                    $"Criterion {criterion}: LastName {(like ? "LIKE" : "=")} {sqlValue} " +
                    $"{(matched ? "matches" : "does not match")} the created record.");
                if (storedCase)
                {
                    Assert.True(matched, $"Direct SQL does not find the created record for '{criterion}'.");
                }

                var response = await SearchAsync(
                    client, viewId, [new(TestDatabase.MarkerField, criterion)], ct: ct);
                AssertFound(response, expected);
            }
        }
    }

    // An integer as the query string and the criteria write it.
    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);

    // A marker whose inner run, and so the whole marker, holds a hexadecimal letter, so each case variant differs from
    // the lowercase text NewMarker stores.
    private static string NewMarkerWithHexLetter()
    {
        string marker;
        do
        {
            marker = TestDatabase.NewMarker();
        }
        while (!marker.AsSpan(InnerStart, InnerLength).ContainsAnyInRange('a', 'f'));

        return marker;
    }

    // Fresh lowercase hexadecimal text of the given length: letters a to f and digits only.
    private static string LettersAndDigits(int length) =>
        string.Concat(Enumerable.Range(0, (length / 32) + 1).Select(_ => Guid.NewGuid().ToString("N")))[..length];

    private static string RecordsPath(int viewId, string suffix) =>
        string.Create(CultureInfo.InvariantCulture, $"/api/forms/{viewId}/records{suffix}");

    // ApiTestClient.CreateRecordAsync with a concrete dictionary, so a case can write its values as new() { ... }.
    private static Task<CreatedRecord> CreateAsync(
        HttpClient client,
        int viewId,
        Dictionary<string, object?> values,
        CancellationToken ct) =>
        ApiTestClient.CreateRecordAsync(client, viewId, values, ct);

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string path, CancellationToken ct)
    {
        using var response = await client.GetAsync(path, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ApiTestClient.ReadJsonElementAsync(response, ct);
    }

    private static Task<JsonElement> GetListAsync(HttpClient client, int viewId, string query, CancellationToken ct) =>
        GetJsonAsync(client, RecordsPath(viewId, query), ct);

    // Every page of one set at the largest page size, until the items reach the total or a page comes back empty.
    private static async Task<(int Total, List<ListedRecord> Records)> ListEveryPageAsync(
        HttpClient client,
        int viewId,
        bool includeDeleted,
        CancellationToken ct)
    {
        var records = new List<ListedRecord>();
        var set = includeDeleted ? "&" + IncludeDeletedParameter + "=true" : string.Empty;
        for (var page = DefaultPage; ; page++)
        {
            var query = "?" + PageSizeParameter + "=" + Text(MaxPageSize) +
                "&" + PageParameter + "=" + Text(page) + set;
            var list = await GetListAsync(client, viewId, query, ct);
            Assert.Equal(page, Property(list, "page").GetInt32());
            Assert.Equal(MaxPageSize, Property(list, "pageSize").GetInt32());

            var total = Property(list, "total").GetInt32();
            var items = Items(list);
            records.AddRange(items.Select(item => new ListedRecord(Id(item), UniqueKey(item), RecStatus(item))));
            if (items.Count == 0 || records.Count >= total)
            {
                return (total, records);
            }
        }
    }

    // POST .../records/search with only the members a case sets, so an absent includeDeleted or excludeGlobalRecordId
    // takes its default; the response must be a 200.
    private static async Task<JsonElement> SearchAsync(
        HttpClient client,
        int viewId,
        IReadOnlyList<CriterionBody> criteria,
        bool? includeDeleted = null,
        string? exclude = null,
        CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?> { ["criteria"] = criteria.ToArray() };
        if (includeDeleted is { } flag)
        {
            body["includeDeleted"] = flag;
        }

        if (exclude is not null)
        {
            body["excludeGlobalRecordId"] = exclude;
        }

        using var response = await ApiTestClient.PostJsonAsync(client, RecordsPath(viewId, "/search"), body, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ApiTestClient.ReadJsonElementAsync(response, ct);
    }

    // GET .../records/{globalRecordId}/position over the chosen set.
    private static async Task<(int? Position, int Count)> GetPositionAsync(
        HttpClient client,
        int viewId,
        string globalRecordId,
        bool includeDeleted,
        CancellationToken ct)
    {
        var path = RecordsPath(
            viewId,
            "/" + Uri.EscapeDataString(globalRecordId) + "/position?" + IncludeDeletedParameter + "=" +
            (includeDeleted ? "true" : "false"));
        var body = await GetJsonAsync(client, path, ct);
        var position = Property(body, "position");
        int? placed = position.ValueKind == JsonValueKind.Null ? null : position.GetInt32();
        return (placed, Property(body, "count").GetInt32());
    }

    // The first, middle and last results carry the position the position endpoint gives them in the same set.
    private static async Task AssertEndpointPositionsAsync(
        HttpClient client,
        int viewId,
        IReadOnlyList<JsonElement> items,
        bool includeDeleted,
        CancellationToken ct)
    {
        Assert.NotEmpty(items);
        foreach (var index in new[] { 0, items.Count / 2, items.Count - 1 }.Distinct())
        {
            var item = items[index];
            var (position, _) = await GetPositionAsync(client, viewId, Id(item), includeDeleted, ct);
            Assert.Equal(Position(item), position);
        }
    }

    // A search response that found exactly ids, in UniqueKey order, within the cap.
    private static void AssertFound(JsonElement response, params string[] ids)
    {
        Assert.Equal(ids.Length, Property(response, "total").GetInt32());
        Assert.False(Property(response, "truncated").GetBoolean());
        Assert.Equal(ids, Items(response).Select(Id).ToArray());
    }

    private static JsonElement Property(JsonElement element, string name)
    {
        Assert.Equal(JsonValueKind.Object, element.ValueKind);
        Assert.True(element.TryGetProperty(name, out var value), $"The response has no '{name}' member.");
        return value;
    }

    private static IReadOnlyList<JsonElement> Items(JsonElement response)
    {
        var items = Property(response, "items");
        Assert.Equal(JsonValueKind.Array, items.ValueKind);
        return items.EnumerateArray().ToArray();
    }

    private static string StringValue(JsonElement element, string name)
    {
        var value = Property(element, name);
        Assert.Equal(JsonValueKind.String, value.ValueKind);
        return Assert.IsType<string>(value.GetString());
    }

    private static string Id(JsonElement item) => StringValue(item, "globalRecordId");

    private static int UniqueKey(JsonElement item) => Property(item, "uniqueKey").GetInt32();

    private static int RecStatus(JsonElement item) => Property(item, "recStatus").GetInt32();

    private static int Position(JsonElement item) => Property(item, "position").GetInt32();


    // The fixed, bracket-quoted tables of each seeded form (testdata/sql/05_data_tables.sql): its base table and its
    // page tables in page order. No identifier in the statements below is built from input.
    private static (string Base, string[] Pages) Tables(string form) => form switch
    {
        CaseReport => ("[dbo].[CaseReport]", new[] { "[dbo].[CaseReport1]" }),
        FoodHistory => ("[dbo].[FoodHistory]", new[] { "[dbo].[FoodHistory2]", "[dbo].[FoodHistory3]" }),
        _ => throw new ArgumentOutOfRangeException(nameof(form), form, "Not a seeded form."),
    };

    private static SqlParameter[] SelectedSetParameters(bool includeDeleted) =>
    [
        new("@includeDeleted", SqlDbType.Bit) { Value = includeDeleted },
        new("@active", SqlDbType.SmallInt) { Value = ActiveRecStatus },
    ];

    private static SqlParameter IdParameter(string globalRecordId) =>
        new("@id", SqlDbType.NVarChar, 255) { Value = globalRecordId };

    // An INNER JOIN of every page table of the form to the row alias, as Find joins them.
    private static string PageJoins(string form, string alias) => string.Concat(
        Tables(form).Pages.Select((table, index) => string.Create(
            CultureInfo.InvariantCulture,
            $" INNER JOIN {table} AS p{index} ON p{index}.[GlobalRecordId] = {alias}.[GlobalRecordId]")));

    private static Task<int> CountBaseRowsAsync(string form, bool includeDeleted, CancellationToken ct) =>
        ScalarIntAsync(
            $"SELECT COUNT(*) FROM {Tables(form).Base} AS b WHERE {SelectedSetFilter};",
            ct,
            SelectedSetParameters(includeDeleted));

    // The legacy record number over the selected set: the count of its rows with UniqueKey <= k.
    private static Task<int> CountUpToAsync(string form, int uniqueKey, bool includeDeleted, CancellationToken ct) =>
        ScalarIntAsync(
            $"SELECT COUNT(*) FROM {Tables(form).Base} AS b WHERE b.[UniqueKey] <= @uniqueKey AND {SelectedSetFilter};",
            ct,
            [
                new SqlParameter("@uniqueKey", SqlDbType.Int) { Value = uniqueKey },
                .. SelectedSetParameters(includeDeleted),
            ]);

    private static Task<int> ReadUniqueKeyAsync(string form, string globalRecordId, CancellationToken ct) =>
        ScalarIntAsync(
            $"SELECT b.[UniqueKey] FROM {Tables(form).Base} AS b WHERE b.[GlobalRecordId] = @id;",
            ct,
            IdParameter(globalRecordId));

    private static Task<BaseRow[]> ReadActivePageAsync(string form, long offset, int pageSize, CancellationToken ct) =>
        ReadRowsAsync(
            $"SELECT b.[GlobalRecordId], b.[UniqueKey] FROM {Tables(form).Base} AS b WHERE b.[RecStatus] = @active " +
            "ORDER BY b.[UniqueKey] OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY;",
            reader => new BaseRow(reader.GetString(0), reader.GetInt32(1)),
            ct,
            new SqlParameter("@active", SqlDbType.SmallInt) { Value = ActiveRecStatus },
            new SqlParameter("@offset", SqlDbType.BigInt) { Value = offset },
            new SqlParameter("@pageSize", SqlDbType.Int) { Value = pageSize });

    private static Task<string[]> ReadDeletedIdsAsync(string form, CancellationToken ct) =>
        ReadRowsAsync(
            $"SELECT b.[GlobalRecordId] FROM {Tables(form).Base} AS b WHERE b.[RecStatus] = @deleted " +
            "ORDER BY b.[UniqueKey];",
            reader => reader.GetString(0),
            ct,
            new SqlParameter("@deleted", SqlDbType.SmallInt) { Value = DeletedRecStatus });

    // The number of Find matches of a RecStatus criterion: base rows of the selected set with that status and a row
    // on every page.
    private static Task<int> CountSearchMatchesAsync(
        string form,
        bool includeDeleted,
        int recStatus,
        CancellationToken ct) =>
        ScalarIntAsync(
            $"SELECT COUNT(*) FROM {Tables(form).Base} AS b{PageJoins(form, "b")} " +
            $"WHERE b.[RecStatus] = @status AND {SelectedSetFilter};",
            ct,
            [
                new SqlParameter("@status", SqlDbType.SmallInt) { Value = (short)recStatus },
                .. SelectedSetParameters(includeDeleted),
            ]);

    // The first Find matches of a RecStatus criterion in UniqueKey order, each with its ROW_NUMBER over the selected
    // set, numbered before the criterion and the page joins apply.
    private static Task<SearchMatch[]> ReadSearchMatchesAsync(
        string form,
        bool includeDeleted,
        int recStatus,
        int take,
        CancellationToken ct) =>
        ReadRowsAsync(
            "SELECT TOP (@take) n.[GlobalRecordId], n.[pos] FROM (" +
            "SELECT b.[GlobalRecordId], b.[UniqueKey], b.[RecStatus], " +
            "CAST(ROW_NUMBER() OVER (ORDER BY b.[UniqueKey]) AS int) AS [pos] " +
            $"FROM {Tables(form).Base} AS b WHERE {SelectedSetFilter}) AS n{PageJoins(form, "n")} " +
            "WHERE n.[RecStatus] = @status ORDER BY n.[UniqueKey];",
            reader => new SearchMatch(reader.GetString(0), reader.GetInt32(1)),
            ct,
            [
                new SqlParameter("@take", SqlDbType.Int) { Value = take },
                new SqlParameter("@status", SqlDbType.SmallInt) { Value = (short)recStatus },
                .. SelectedSetParameters(includeDeleted),
            ]);

    private static Task<int> CountPageTwoRowsAsync(string globalRecordId, CancellationToken ct) =>
        ScalarIntAsync(
            "SELECT COUNT(*) FROM [dbo].[FoodHistory3] WHERE [GlobalRecordId] = @id;",
            ct,
            IdParameter(globalRecordId));

    // The missing-page fixture: the only direct write of these tests, on a FoodHistory record the test created.
    private static async Task<int> DeletePageTwoRowAsync(string globalRecordId, CancellationToken ct)
    {
        await using var connection = await TestDatabase.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM [dbo].[FoodHistory3] WHERE [GlobalRecordId] = @id;";
        command.Parameters.Add(IdParameter(globalRecordId));
        return await command.ExecuteNonQueryAsync(ct);
    }

    // The stored FirstName of a CaseReport record: DBNull for NULL, null when the record has no page row.
    private static Task<object?> ReadFirstNameAsync(string globalRecordId, CancellationToken ct) =>
        ScalarAsync(
            "SELECT [FirstName] FROM [dbo].[CaseReport1] WHERE [GlobalRecordId] = @id;",
            ct,
            IdParameter(globalRecordId));

    // The CaseReport records Find must return for one LastName comparison, as it selects them: active base rows with a
    // row on every page table, in UniqueKey order, whose LastName equals the value or, with like, matches it as a LIKE
    // pattern with no ESCAPE clause. No COLLATE clause is written, so the comparison runs under the column's
    // collation, which a parameter's coercible default never overrides.
    private static Task<string[]> ReadLastNameMatchesAsync(bool like, string value, CancellationToken ct) =>
        ReadRowsAsync(
            $"SELECT b.[GlobalRecordId] FROM {Tables(CaseReport).Base} AS b{PageJoins(CaseReport, "b")} " +
            $"WHERE b.[RecStatus] = @active AND p0.[LastName] {(like ? "LIKE" : "=")} @value " +
            "ORDER BY b.[UniqueKey];",
            reader => reader.GetString(0),
            ct,
            new SqlParameter("@active", SqlDbType.SmallInt) { Value = ActiveRecStatus },
            new SqlParameter("@value", SqlDbType.NVarChar, 4000) { Value = value });

    private static async Task<string> ReadDatabaseCollationAsync(CancellationToken ct) =>
        Assert.IsType<string>(await ScalarAsync(
            "SELECT CONVERT(nvarchar(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation'));",
            ct));

    // The collation of the CaseReport1.LastName column, the one Find's LastName comparisons run under.
    private static async Task<string> ReadLastNameCollationAsync(CancellationToken ct) =>
        Assert.IsType<string>(await ScalarAsync(
            "SELECT c.[collation_name] FROM sys.columns AS c " +
            "WHERE c.[object_id] = OBJECT_ID(N'[dbo].[CaseReport1]') AND c.[name] = N'LastName';",
            ct));

    private static async Task<object?> ScalarAsync(string sql, CancellationToken ct, params SqlParameter[] parameters)
    {
        await using var connection = await TestDatabase.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);
        return await command.ExecuteScalarAsync(ct);
    }

    private static async Task<int> ScalarIntAsync(string sql, CancellationToken ct, params SqlParameter[] parameters) =>
        Assert.IsType<int>(await ScalarAsync(sql, ct, parameters));

    private static async Task<T[]> ReadRowsAsync<T>(
        string sql,
        Func<SqlDataReader, T> map,
        CancellationToken ct,
        params SqlParameter[] parameters)
    {
        await using var connection = await TestDatabase.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<T>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(map(reader));
        }

        return rows.ToArray();
    }

    // A list statement's page: ORDER BY, then OFFSET ... ROWS FETCH NEXT; the keywords in any case.
    [GeneratedRegex(
        @"\bORDER\s+BY\b[\s\S]*\bOFFSET\b[\s\S]*\bROWS\s+FETCH\s+NEXT\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PagedSelect();

    // A list statement's total: SELECT COUNT(*) or COUNT_BIG(*); the keywords in any case.
    [GeneratedRegex(
        @"\bSELECT\s+COUNT(?:_BIG)?\s*\(\s*\*\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CountSelect();

    // The LIKE keyword, in any case.
    [GeneratedRegex(@"\bLIKE\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LikeKeyword();

    // CONVERT(nvarchar(max), alias.[column]) COLLATE Latin1_General_100_CI_AS LIKE @p COLLATE
    // Latin1_General_100_CI_AS ESCAPE '\'. Every CaseReport grid column is nvarchar, so none takes style 23.
    [GeneratedRegex(
        @"CONVERT\(\s*nvarchar\(\s*max\s*\)\s*,\s*\w+\.\[(?<column>[^\]]+)\]\s*\)" +
        @"\s+COLLATE\s+Latin1_General_100_CI_AS\s+LIKE\s+(?<parameter>@\w+)" +
        @"\s+COLLATE\s+Latin1_General_100_CI_AS\s+ESCAPE\s+'\\'",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GridContains();

    // One search criterion on the wire: {"field": ..., "value": ...}.
    private sealed record CriterionBody(string Field, string Value);

    // A base row's identity and order key, as the list items carry them.
    private readonly record struct BaseRow(string GlobalRecordId, int UniqueKey);

    // One listed record of a walk over every page.
    private readonly record struct ListedRecord(string GlobalRecordId, int UniqueKey, int RecStatus);

    // One Find result's identity and its position over the selected set.
    private readonly record struct SearchMatch(string GlobalRecordId, int Position);
}
