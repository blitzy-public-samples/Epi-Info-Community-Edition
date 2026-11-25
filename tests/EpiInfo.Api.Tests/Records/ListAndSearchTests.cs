using System.Text.Json;
using EpiInfo.Api.Contracts;
using EpiInfo.Api.Json;
using EpiInfo.Domain.Records;
using EpiInfo.Domain.Values;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
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
