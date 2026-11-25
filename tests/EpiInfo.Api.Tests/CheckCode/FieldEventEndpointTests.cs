using System.Net;
using System.Text;
using System.Text.Json;
using EpiInfo.Api.Endpoints;
using EpiInfo.Api.Errors;
using EpiInfo.Api.Json;
using EpiInfo.Api.Services;
using EpiInfo.Api.Tests.Infrastructure;
using EpiInfo.CheckCode;
using EpiInfo.Domain.Metadata;
using EpiInfo.Domain.Storage;
using EpiInfo.Domain.Validation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EpiInfo.Api.Tests.CheckCode;

/// <summary>
/// The field-event route, <c>POST /api/forms/{viewId}/checkcode/field-event</c>: which <c>values</c> it rejects
/// before evaluation and how each rejection is keyed, and that values breaking the write rules but carrying their
/// wire type are evaluated as sent.
/// </summary>
/// <remarks>
/// <para>
/// A field event checks only what evaluation needs: the event spelling, a field name on a page, and that every
/// <c>values</c> entry names a data field once and carries that field's wire type. Each request is sent as raw JSON
/// text, so case variants, repeats, <c>1e400</c> and escaped lone surrogates reach the API exactly as written. Every
/// rejection must be a 400 <c>application/problem+json</c> body keyed by the canonical <c>metaFields.Name</c>, by
/// <see cref="RecordValidator.ValuesKey"/>, or by the <c>event</c> or <c>fieldName</c> member, carrying no submitted
/// text, and issued before the program is read: the store is never asked for the check code, the engine never runs
/// and nothing is written.
/// </para>
/// <para>
/// The forms are <see cref="FakeForms"/> fixtures in a <see cref="FakeDataStore"/>, extended with synthetic fields
/// where no fixture has the type or rule a case needs, so the class needs no database and carries no
/// <c>Integration</c> trait.
/// </para>
/// </remarks>
public sealed class FieldEventEndpointTests
{
    private const string Sentinel = "SENTINEL-PII-4711";

    private const string OptionFieldName = "Colour";

    private const string PhoneFieldName = "Phone";

    private const string ReadOnlyFieldName = "StateIDNumber";

    private const string PhoneMask = "###-###-####";

    private const string ZipFieldName = "Zip";

    private const int ZipMaxLength = 5;

    // metaFieldTypes rows as seeded by testdata/sql/02_reference_data.sql.
    private const int PhoneNumberTypeId = 6;

    private const int TextTypeId = 1;

    private const int OptionTypeId = 12;

    // The After block of Age assigns LastName only when the Option field arrives as index 2 under its canonical name.
    private const string TypedValuesProgram = """
        Field Age
            After
                IF Colour = 2 THEN
                    ASSIGN LastName = "evaluated"
                END-IF
            End-After
        End-Field
        """;

    // Each effect of the After block of Age depends on one value that breaks a write rule: the range of Age, the legal
    // values of Sex, the mask of Phone, the read-only StateIDNumber and the maximum length of Zip.
    private const string HistoricValuesProgram = """
        Field Age
            After
                IF Age = 150 THEN
                    ASSIGN Notes = "range"
                END-IF
                IF Sex = "X-Other" THEN
                    ENABLE Ill
                END-IF
                IF Phone = "(555) 0148-6367" THEN
                    DISABLE LastName
                END-IF
                IF StateIDNumber = "RO-7" THEN
                    DIALOG "read-only"
                END-IF
                IF Zip = "303330" THEN
                    DIALOG "length"
                END-IF
            End-After
        End-Field
        """;

    /// <summary>
    /// Field-level rejections of a valid <c>FieldAfter</c> event on <c>Age</c>, as (<c>values</c> JSON, the one
    /// expected <c>errors</c> key, its one message), on <see cref="FakeForms.WithCheckCode(string)"/> extended with
    /// the Option field <c>Colour</c>.
    /// </summary>
    public static TheoryData<string, string, string> ValueRejectionCases
    {
        get
        {
            const string ValuesKey = RecordValidator.ValuesKey;
            const string Text = ValidationMessages.TextExpected;
            const string Number = ValidationMessages.NumberExpected;
            const string Date = ValidationMessages.DateExpected;
            const string Boolean = ValidationMessages.BooleanExpected;
            const string Index = ValidationMessages.OptionIndexExpected;

            return new TheoryData<string, string, string>
            {
                // The wire type of each field type, under case variants of the field's name.
                { """{"lastname":5}""", "LastName", Text },
                { """{"LASTNAME":true}""", "LastName", Text },
                { """{"age":"37"}""", "Age", Number },
                { $$"""{"Age":"{{Sentinel}}"}""", "Age", Number },
                { """{"AGE":true}""", "Age", Number },
                { """{"aGe":1e400}""", "Age", Number },
                { """{"dob":"01/15/2011"}""", "DOB", Date },
                { $$"""{"Dob":"{{Sentinel}}"}""", "DOB", Date },
                { """{"DOB":5}""", "DOB", Date },
                { """{"asian":"true"}""", "Asian", Boolean },
                { """{"ASIAN":1}""", "Asian", Boolean },
                { """{"colour":1.5}""", OptionFieldName, Index },
                { """{"COLOUR":-1}""", OptionFieldName, Index },
                { """{"Colour":"1"}""", OptionFieldName, Index },
                { """{"colOUR":true}""", OptionFieldName, Index },
                { """{"colour":2147483648}""", OptionFieldName, Index },

                // A number no double holds fails the shape check before the option rule applies.
                { """{"colour":1e400}""", OptionFieldName, Number },

                // An object or array under a case variant of a data field.
                { $$$"""{"lastname":{"first":"{{{Sentinel}}}"}}""", "LastName", ValuesReader.ScalarValueExpected },
                { $$"""{"dob":["{{Sentinel}}"]}""", "DOB", ValuesReader.ScalarValueExpected },

                // A field named again, by a case variant or exactly.
                { """{"Age":1,"AGE":2}""", "Age", ValidationMessages.DuplicateField },
                { """{"Age":1,"Age":1}""", "Age", ValidationMessages.DuplicateField },

                // A name the form has no row for, whatever its value, and a system row, which holds no value.
                { $$"""{"{{Sentinel}}":1}""", ValuesKey, ValidationMessages.UnknownValuesName },
                { $$"""{"{{Sentinel}}":"{{Sentinel}}"}""", ValuesKey, ValidationMessages.UnknownValuesName },
                { $$$"""{"{{{Sentinel}}}":{"{{{Sentinel}}}":1}}""", ValuesKey, ValidationMessages.UnknownValuesName },
                { $$"""{"globalrecordid":"{{Sentinel}}"}""", "GlobalRecordId", ValidationMessages.UnknownField },
            };
        }
    }

    /// <summary>
    /// Bodies of a valid <c>FieldAfter</c> event on <c>Age</c> whose <c>values</c> hold a raw fault beside other
    /// entries, as (<c>values</c> JSON, the expected <c>errors</c> keys, one message each in the same order), on
    /// <see cref="FakeForms.WithCheckCode(string)"/> extended with the Option field <c>Colour</c>. A key listed twice
    /// expects both messages under it, in that order.
    /// </summary>
    public static TheoryData<string, string[], string[]> FaultBesideOtherEntriesCases
    {
        get
        {
            const string Duplicate = ValidationMessages.DuplicateField;
            const string Shape = ValuesReader.ScalarValueExpected;

            return new TheoryData<string, string[], string[]>
            {
                // A data field named again, whichever occurrence is faulted: the repeat is a duplicate under the
                // canonical name, and a faulted first occurrence keeps its own fault ahead of it.
                { $$"""{"Age":1,"AGE":["{{Sentinel}}"]}""", ["Age"], [Duplicate] },
                { $$"""{"AGE":["{{Sentinel}}"],"Age":1}""", ["Age", "Age"], [Shape, Duplicate] },
                { """{"age":1e400,"AGE":2}""", ["Age", "Age"], [ValidationMessages.NumberExpected, Duplicate] },

                // A name the form has no row for, or a system row, beside a faulted data field: each under its own key.
                {
                    $$$"""{"lastname":{"first":"{{{Sentinel}}}"},"{{{Sentinel}}}":"{{{Sentinel}}}"}""",
                    ["LastName", RecordValidator.ValuesKey],
                    [Shape, ValidationMessages.UnknownValuesName]
                },
                {
                    $$"""{"dob":[],"globalrecordid":"{{Sentinel}}"}""",
                    ["DOB", "GlobalRecordId"],
                    [Shape, ValidationMessages.UnknownField]
                },

                // A data field read without fault and named once adds nothing beside the fault.
                { $$$"""{"LastName":"{{{Sentinel}}}","asian":{}}""", ["Asian"], [Shape] },
            };
        }
    }

    /// <summary>
    /// <c>values</c> objects with a valid event and field name whose property name or string value holds an escaped
    /// lone surrogate, which decodes to no text.
    /// </summary>
    public static TheoryData<string> UndecodableValuesCases => new()
    {
        $$"""{"\uD800":"{{Sentinel}}"}""",
        $$"""{"{{Sentinel}}\uDC00":1}""",
        $$"""{"LastName":"{{Sentinel}}\uD800"}""",
        $$"""{"{{Sentinel}}":"\uDFFF"}""",
    };

    /// <summary>
    /// Bodies whose envelope and <c>values</c> are both invalid, as (request JSON, the expected <c>errors</c> keys,
    /// one message each in the same order); a key listed twice expects both messages under it, in that order.
    /// </summary>
    public static TheoryData<string, string[], string[]> CombinedRejectionCases => new()
    {
        {
            $$$$"""{"event":"fieldAfter","fieldName":"Age","values":{"lastname":{"first":"{{{{Sentinel}}}}"}}}""",
            [FieldEventService.EventKey, "LastName"],
            [FieldEventService.InvalidEventMessage, ValuesReader.ScalarValueExpected]
        },
        {
            $$$"""{"event":"FieldAfter","fieldName":"{{{Sentinel}}}","values":{"AGE":"37","{{{Sentinel}}}":1}}""",
            [FieldEventService.FieldNameKey, "Age", RecordValidator.ValuesKey],
            [
                FieldEventService.InvalidFieldNameMessage,
                ValidationMessages.NumberExpected,
                ValidationMessages.UnknownValuesName,
            ]
        },
        {
            $$$"""{"event":1,"values":{"Age":1,"age":2,"asian":"{{{Sentinel}}}"}}""",
            [FieldEventService.EventKey, FieldEventService.FieldNameKey, "Age", "Asian"],
            [
                FieldEventService.InvalidEventMessage,
                FieldEventService.InvalidFieldNameMessage,
                ValidationMessages.DuplicateField,
                ValidationMessages.BooleanExpected,
            ]
        },
        {
            $$$"""{"event":"fieldAfter","fieldName":"Age","values":{"AGE":["{{{Sentinel}}}"],"Age":1}}""",
            [FieldEventService.EventKey, "Age", "Age"],
            [FieldEventService.InvalidEventMessage, ValuesReader.ScalarValueExpected, ValidationMessages.DuplicateField]
        },
    };

    /// <summary>
    /// A value without its field's wire type, a repeated field or a name that is not a data field is one error under
    /// the canonical name or the fixed values key, and the event is not evaluated.
    /// </summary>
    [Theory]
    [MemberData(nameof(ValueRejectionCases))]
    public async Task RejectedValueIsKeyedCanonicallyAndBlocksEvaluation(
        string valuesJson,
        string expectedKey,
        string expectedMessage)
    {
        await using var host = await EndpointHost.StartAsync(new FakeDataStore(FormWithOptionField()));

        var problem = await SendRejectedEventAsync(
            host,
            $$"""{"event":"FieldAfter","fieldName":"Age","values":{{valuesJson}}}""");

        Assert.Equal(ApiProblems.ValidationFailedDetail, problem.GetProperty("detail").GetString());
        AssertErrors(problem, [expectedKey], [expectedMessage]);
    }

    /// <summary>
    /// A raw fault does not hide the entries beside it: every property is resolved in document order by the
    /// precedence typing applies, so a name that is not a data field reads its own error, a data field named again is
    /// a duplicate under its canonical name, a faulted first occurrence keeps its fault, and the event is not
    /// evaluated.
    /// </summary>
    [Theory]
    [MemberData(nameof(FaultBesideOtherEntriesCases))]
    public async Task FaultBesideOtherEntriesIsResolvedPerPropertyAndBlocksEvaluation(
        string valuesJson,
        string[] expectedKeys,
        string[] expectedMessages)
    {
        await using var host = await EndpointHost.StartAsync(new FakeDataStore(FormWithOptionField()));

        var problem = await SendRejectedEventAsync(
            host,
            $$"""{"event":"FieldAfter","fieldName":"Age","values":{{valuesJson}}}""");

        Assert.Equal(ApiProblems.ValidationFailedDetail, problem.GetProperty("detail").GetString());
        AssertErrors(problem, expectedKeys, expectedMessages);
    }

    /// <summary>
    /// With a valid event and field name, a name or string that cannot be decoded is the request-level "malformed
    /// JSON" 400 with no <c>errors</c> member, and the event is not evaluated.
    /// </summary>
    [Theory]
    [MemberData(nameof(UndecodableValuesCases))]
    public async Task UndecodableNameOrStringIsRequestLevelMalformedJson(string valuesJson)
    {
        await using var host = await EndpointHost.StartAsync(new FakeDataStore(FormWithOptionField()));

        var problem = await SendRejectedEventAsync(
            host,
            $$"""{"event":"FieldAfter","fieldName":"Age","values":{{valuesJson}}}""");

        Assert.Equal(ApiProblems.MalformedJsonDetail, problem.GetProperty("detail").GetString());
        Assert.False(
            problem.TryGetProperty("errors", out _),
            $"A request-level 400 has no errors member: {problem.GetRawText()}");
    }

    /// <summary>
    /// An invalid <c>event</c> or <c>fieldName</c> and invalid <c>values</c> are reported together in one 400, every
    /// value error under its canonical name or the fixed values key, and nothing is evaluated.
    /// </summary>
    [Theory]
    [MemberData(nameof(CombinedRejectionCases))]
    public async Task EnvelopeAndValueErrorsAreReportedTogetherAndBlockEvaluation(
        string requestJson,
        string[] expectedKeys,
        string[] expectedMessages)
    {
        await using var host = await EndpointHost.StartAsync(new FakeDataStore(FormWithOptionField()));

        var problem = await SendRejectedEventAsync(host, requestJson);

        Assert.Equal(ApiProblems.ValidationFailedDetail, problem.GetProperty("detail").GetString());
        AssertErrors(problem, expectedKeys, expectedMessages);
    }

    /// <summary>
    /// The control for the rejection cases: case variants carrying each field type's wire type, a read-only field
    /// included, are accepted and handed to the engine under their canonical names and types.
    /// </summary>
    [Fact]
    public async Task CaseVariantsWithTheirWireTypesAreEvaluatedUnderCanonicalNames()
    {
        var store = new FakeDataStore(FormWithOptionField());
        await using var host = await EndpointHost.StartAsync(store);

        var body = await SendAcceptedEventAsync(
            host,
            FakeForms.CheckCodeViewId,
            """
            {"event":"FieldAfter","fieldName":"age","values":{"lastname":"Smith","AGE":37,"dob":"2011-01-15",
            "ASIAN":false,"colour":2,"onsetweek":3}}
            """);

        AssertJsonEqual(
            """
            {
              "enabled": [], "disabled": [], "cleared": [], "assigned": {"LastName": "evaluated"},
              "dialogs": [], "diagnostics": []
            }
            """,
            body);
    }

    /// <summary>
    /// Values a save would reject but that carry their wire type, an out-of-range number, a text that is no legal
    /// value, a text that breaks its mask, a read-only field and a text over its maximum length, are evaluated as
    /// sent: each drives one effect of the program, while the same values posted as a record are refused field by
    /// field.
    /// </summary>
    [Fact]
    public async Task HistoricallyInvalidValuesWithTheirWireTypesReachEvaluation()
    {
        var store = new FakeDataStore(FormWithHistoricRules());
        await using var host = await EndpointHost.StartAsync(store);
        var values = $$"""
            {"Age":150,"Sex":"X-Other","{{PhoneFieldName}}":"(555) 0148-6367","{{ReadOnlyFieldName}}":"RO-7",
            "{{ZipFieldName}}":"303330"}
            """;

        var body = await SendAcceptedEventAsync(
            host,
            FakeForms.TwoPageViewId,
            $$"""{"event":"FieldAfter","fieldName":"Age","values":{{values}}}""");

        AssertJsonEqual(
            """
            {
              "enabled": ["Ill"], "disabled": ["LastName"], "cleared": [], "assigned": {"Notes": "range"},
              "dialogs": [{"message": "read-only", "title": null}, {"message": "length", "title": null}],
              "diagnostics": []
            }
            """,
            body);

        // The same values are invalid for a save, which proves each one is a value the write rules reject.
        using var content = new StringContent(
            $$"""{"values":{{values}}}""",
            Encoding.UTF8,
            ApiTestClient.JsonMediaType);
        using var save = await host.Client.PostAsync(
            $"/api/forms/{FakeForms.TwoPageViewId}/records",
            content,
            host.Timeout);
        var saveText = await save.Content.ReadAsStringAsync(host.Timeout);
        Assert.True(
            save.StatusCode == HttpStatusCode.BadRequest,
            $"The save returned {(int)save.StatusCode}: {saveText}");
        using var saveDocument = JsonDocument.Parse(saveText);
        AssertErrors(
            saveDocument.RootElement,
            ["Age", "Sex", PhoneFieldName, ReadOnlyFieldName, ZipFieldName],
            [
                ValidationMessages.FormatRangeBetween(0, 120),
                ValidationMessages.NotALegalValue,
                ValidationMessages.FormatPatternMismatch(PhoneMask),
                ValidationMessages.ReadOnlyField,
                ValidationMessages.FormatMaxLengthExceeded(ZipMaxLength),
            ]);

        Assert.Equal(1, host.Engine.RunCalls);
        Assert.Empty(store.WriteCalls);
    }

    // FakeForms.WithCheckCode carrying TypedValuesProgram, with the Option field Colour (items Red, Green, Blue on a
    // smallint column) added to its page, since no fixture has an Option field.
    private static FakeForm FormWithOptionField()
    {
        var form = FakeForms.WithCheckCode(TypedValuesProgram);
        var page = form.Metadata.Pages[0];
        var option = new FieldMetadata
        {
            FieldId = (FakeForms.CheckCodeViewId * 1000) + 9,
            Name = OptionFieldName,
            FieldTypeId = OptionTypeId,
            FieldTypeName = "Option",
            Prompt = OptionFieldName,
            TabIndex = 6m,
            PageId = page.PageId,
            List = "Red,Green,Blue",
            Column = FieldTypeMapper.DescribeColumn(OptionFieldName, "smallint", 2),
        };

        return form with { Metadata = form.Metadata with { Fields = [.. form.Metadata.Fields, option] } };
    }

    // FakeForms.TwoPage (Age bounded 0 to 120, Sex with the legal values Male and Female) carrying
    // HistoricValuesProgram, with a Phone Number field masked ###-###-####, the read-only text StateIDNumber and the
    // text Zip of maximum length 5 added to its first page.
    private static FakeForm FormWithHistoricRules()
    {
        var form = FakeForms.TwoPage.WithCheckCode(HistoricValuesProgram);
        var page = form.Metadata.Pages[0];
        var phone = new FieldMetadata
        {
            FieldId = (FakeForms.TwoPageViewId * 1000) + 10,
            Name = PhoneFieldName,
            FieldTypeId = PhoneNumberTypeId,
            FieldTypeName = "Phone Number",
            Prompt = PhoneFieldName,
            TabIndex = 5m,
            PageId = page.PageId,
            Pattern = PhoneMask,
            Column = FieldTypeMapper.DescribeColumn(PhoneFieldName, "nvarchar", 40),
        };
        var readOnly = new FieldMetadata
        {
            FieldId = (FakeForms.TwoPageViewId * 1000) + 11,
            Name = ReadOnlyFieldName,
            FieldTypeId = TextTypeId,
            FieldTypeName = "Text",
            Prompt = ReadOnlyFieldName,
            TabIndex = 6m,
            PageId = page.PageId,
            IsReadOnly = true,
            Column = FieldTypeMapper.DescribeColumn(ReadOnlyFieldName, "nvarchar", 8000),
        };
        var zip = new FieldMetadata
        {
            FieldId = (FakeForms.TwoPageViewId * 1000) + 12,
            Name = ZipFieldName,
            FieldTypeId = TextTypeId,
            FieldTypeName = "Text",
            Prompt = ZipFieldName,
            TabIndex = 7m,
            PageId = page.PageId,
            MaxLength = ZipMaxLength,
            Column = FieldTypeMapper.DescribeColumn(ZipFieldName, "nvarchar", 8000),
        };

        return form with
        {
            Metadata = form.Metadata with { Fields = [.. form.Metadata.Fields, phone, readOnly, zip] },
        };
    }

    // Posts requestJson to the field-event route of the check-code form and asserts the shared rejection contract:
    // a 400 ProblemDetails with no submitted text, issued before the program is read, so nothing is evaluated or
    // written. Returns the body.
    private static async Task<JsonElement> SendRejectedEventAsync(EndpointHost host, string requestJson)
    {
        using var response = await PostEventAsync(host, FakeForms.CheckCodeViewId, requestJson);
        var text = await response.Content.ReadAsStringAsync(host.Timeout);

        Assert.True(
            response.StatusCode == HttpStatusCode.BadRequest,
            $"{requestJson} returned {(int)response.StatusCode}: {text}");
        Assert.Equal(ApiProblems.ProblemContentType, response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain(Sentinel, text, StringComparison.Ordinal);

        Assert.Equal(0, host.Engine.RunCalls);
        Assert.DoesNotContain(host.Store.Calls, call => call.Method == nameof(IDataStore.GetCheckCodeAsync));
        Assert.Empty(host.Store.WriteCalls);

        using var document = JsonDocument.Parse(text);
        return ApiTestClient.AssertProblemDetails(document.RootElement.Clone(), StatusCodes.Status400BadRequest);
    }

    // Posts requestJson to the field-event route of viewId and asserts a 200 from exactly one evaluation of the
    // program the store was asked for, with nothing written. Returns the field-event response body.
    private static async Task<JsonElement> SendAcceptedEventAsync(EndpointHost host, int viewId, string requestJson)
    {
        using var response = await PostEventAsync(host, viewId, requestJson);
        var text = await response.Content.ReadAsStringAsync(host.Timeout);

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"{requestJson} returned {(int)response.StatusCode}: {text}");
        Assert.Equal(1, host.Engine.RunCalls);
        Assert.Single(host.Store.Calls, call => call.Method == nameof(IDataStore.GetCheckCodeAsync));
        Assert.Empty(host.Store.WriteCalls);

        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static async Task<HttpResponseMessage> PostEventAsync(EndpointHost host, int viewId, string requestJson)
    {
        using var content = new StringContent(requestJson, Encoding.UTF8, ApiTestClient.JsonMediaType);
        return await host.Client.PostAsync($"/api/forms/{viewId}/checkcode/field-event", content, host.Timeout);
    }

    // Asserts that actual is the JSON value expected describes: the same members with equal values, arrays in order.
    private static void AssertJsonEqual(string expected, JsonElement actual)
    {
        using var document = JsonDocument.Parse(expected);
        Assert.True(
            JsonElement.DeepEquals(document.RootElement, actual),
            $"Expected {document.RootElement.GetRawText()}, got {actual.GetRawText()}");
    }

    // Asserts the errors map holds exactly the expected keys, compared ordinally, each with exactly the messages paired
    // with it in their order: a key listed once expects one message, a key listed twice expects two.
    private static void AssertErrors(JsonElement problem, string[] expectedKeys, string[] expectedMessages)
    {
        Assert.Equal(expectedKeys.Length, expectedMessages.Length);
        Assert.True(
            problem.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object,
            $"A field-level 400 carries an errors object: {problem.GetRawText()}");

        var expectedByKey = expectedKeys
            .Zip(expectedMessages)
            .GroupBy(entry => entry.First, entry => entry.Second, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            expectedByKey.Select(group => group.Key).Order(StringComparer.Ordinal),
            errors.EnumerateObject().Select(entry => entry.Name).Order(StringComparer.Ordinal));

        foreach (var group in expectedByKey)
        {
            Assert.Equal(
                group.Select(message => (string?)message),
                errors.GetProperty(group.Key).EnumerateArray().Select(item => item.GetString()));
        }
    }

    /// <summary>
    /// An in-process TestServer host over the real field-event and record endpoints and the services behind them, with
    /// the API's JSON settings, <see cref="FakeDataStore"/> as the store and the real check-code engine behind a
    /// <see cref="CountingCheckCodeEngine"/>. The test server replaces Kestrel, so nothing binds a port, and logging
    /// has no provider.
    /// </summary>
    private sealed class EndpointHost : IAsyncDisposable
    {
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

        private readonly WebApplication _app;

        private readonly CancellationTokenSource _timeout = new(RequestTimeout);

        private EndpointHost(WebApplication app, FakeDataStore store, CountingCheckCodeEngine engine)
        {
            _app = app;
            Store = store;
            Engine = engine;
            Client = app.GetTestClient();
        }

        /// <summary>The store the host reads and writes.</summary>
        public FakeDataStore Store { get; }

        /// <summary>The check-code engine the host uses, counting its calls.</summary>
        public CountingCheckCodeEngine Engine { get; }

        /// <summary>A client whose requests go straight to the test server.</summary>
        public HttpClient Client { get; }

        /// <summary>Cancels a request or read that has not finished within the host's time limit.</summary>
        public CancellationToken Timeout => _timeout.Token;

        /// <summary>Builds and starts a host over <paramref name="store"/>.</summary>
        /// <param name="store">The in-memory store the endpoints use.</param>
        /// <returns>The started host; the caller disposes it.</returns>
        public static async Task<EndpointHost> StartAsync(FakeDataStore store)
        {
            var builder = WebApplication.CreateSlimBuilder(
                new WebApplicationOptions { EnvironmentName = Environments.Production });
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();

            var engine = new CountingCheckCodeEngine(new CheckCodeEngine());
            builder.Services.AddApiJson();
            builder.Services.AddSingleton<IDataStore>(store);
            builder.Services.AddSingleton<ICheckCodeEngine>(engine);
            builder.Services.AddSingleton<TimeProvider>(new ControlledTimeProvider());
            builder.Services.AddSingleton<SaveStampProvider>();
            builder.Services.AddScoped<FormService>();
            builder.Services.AddScoped<RecordService>();
            builder.Services.AddScoped<FieldEventService>();

            var app = builder.Build();
            app.MapCheckCodeEndpoints();
            app.MapRecordEndpoints();

            using var startTimeout = new CancellationTokenSource(RequestTimeout);
            await app.StartAsync(startTimeout.Token);
            return new EndpointHost(app, store, engine);
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
            _timeout.Dispose();
        }
    }
}
