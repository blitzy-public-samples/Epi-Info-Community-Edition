using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EpiInfo.Api.Contracts;
using EpiInfo.Api.Errors;
using EpiInfo.Api.Json;
using EpiInfo.Api.Services;
using EpiInfo.Api.Tests.Infrastructure;
using EpiInfo.CheckCode;
using EpiInfo.Domain.Events;
using EpiInfo.Domain.Forms;
using EpiInfo.Domain.Metadata;
using EpiInfo.Domain.Storage;
using EpiInfo.Domain.Validation;
using EpiInfo.Domain.Values;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EpiInfo.Api.Tests.CheckCode;

/// <summary>
/// The field-event route, <c>POST /api/forms/{viewId}/checkcode/field-event</c>, over HTTP: the 400, 404, 413 and 415
/// answers issued before evaluation and how each 400 is keyed, the exact empty envelope of an event that selects no
/// block, values breaking the write rules but carrying their wire type evaluated as sent, and the effects and
/// diagnostics of a block that runs.
/// </summary>
/// <remarks>
/// <para>
/// A field event checks only what evaluation needs: the event spelling, a field name on a page, and that every
/// <c>values</c> entry names a data field once and carries that field's wire type. Each request is sent as raw JSON
/// text, so missing members, numbers, case variants, repeats, <c>1e400</c> and escaped lone surrogates reach the API
/// exactly as written. Every rejection must be a ProblemDetails body carrying no submitted text, a 400 keyed by the
/// canonical <c>metaFields.Name</c>, by <see cref="RecordValidator.ValuesKey"/>, or by the <c>event</c> or
/// <c>fieldName</c> member, issued before the program is read: the store is never asked for the check code, the
/// engine never runs and nothing is written. An accepted event runs the engine exactly once and writes nothing.
/// </para>
/// <para>
/// Every request crosses the API's own pipeline, <c>Program.cs</c> on TestServer through <see cref="ApiFactory"/>,
/// body-limit and media-type guards included. The forms are <see cref="FakeForms"/> fixtures in a
/// <see cref="FakeDataStore"/>, extended with synthetic fields where no fixture has the type or rule a case needs, so
/// the class needs no database and carries no <c>Integration</c> trait.
/// </para>
/// </remarks>
public sealed class FieldEventEndpointTests
{
    private const string Sentinel = "SENTINEL-PII-4711";

    // The committed Kestrel:Limits:MaxRequestBodySize, which RequestBodyLimitMiddleware applies on TestServer.
    private const int RequestBodyLimit = 1_048_576;

    // FieldEventResponse.Empty on the wire: every list empty, diagnostics included.
    private const string EmptyEnvelope =
        """{"enabled":[],"disabled":[],"cleared":[],"assigned":{},"dialogs":[],"diagnostics":[]}""";

    // The one field of NoBlockProgram that has a block, and that block's only event is After.
    private const string BlockFieldName = "Age";

    // A valid program on the fields of FakeForms.WithCheckCode whose only block is After on Age: an event on another
    // field, or Before or Click on Age, selects no block. It holds only supported constructs naming fields of the form,
    // so it parses without a diagnostic and an event selecting no block gives the empty envelope, diagnostics included.
    private const string NoBlockProgram = """
        Field Age
            After
                CLEAR LastName
            End-After
        End-Field
        """;

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

    private const int NumberTypeId = 5;

    // A number field on an int column, which stores whole numbers only.
    private const string IntegerFieldName = "Visits";

    // Two blocks, each assigning Visits a number that is not whole although its nearest double is: the After block of
    // Age copies the float Age, and the After block of LastName a literal whose nearest double is 2.
    private const string ExactAssignmentProgram = """
        Field Age
            After
                ASSIGN Visits = Age
            End-After
        End-Field
        Field LastName
            After
                ASSIGN Visits = 2.0000000000000001
            End-After
        End-Field
        """;

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
    /// <c>event</c> members that name no event, as raw JSON, or <see langword="null"/> to leave the member out: other
    /// spellings of <c>FieldAfter</c>, the empty string, a number, a boolean and the JSON literal <c>null</c>.
    /// </summary>
    public static TheoryData<string?> InvalidEventMembers => new()
    {
        "\"fieldAfter\"",
        "\"After\"",
        "\"FIELDAFTER\"",
        "\"field-after\"",
        "\"\"",
        "1",
        "true",
        "null",
        null,
    };

    /// <summary>
    /// <c>fieldName</c> members that name no field on a page of the form, as raw JSON, or <see langword="null"/> to
    /// leave the member out: a name the form has no row for, a system row, blank text, a number and the JSON literal
    /// <c>null</c>.
    /// </summary>
    public static TheoryData<string?> InvalidFieldNameMembers => new()
    {
        "\"NoSuchField\"",
        "\"UniqueKey\"",
        "\"\"",
        "\"   \"",
        "5",
        "null",
        null,
    };

    /// <summary>
    /// Paths of a form the store does not hold, with the <c>detail</c> of each 404: a view id outside the fixtures'
    /// 9001 to 9004, and a non-integer view id, which the route's <c>int</c> constraint turns into a route miss.
    /// </summary>
    public static TheoryData<string, string> UnknownFormPaths => new()
    {
        { "/api/forms/424242/checkcode/field-event", ApiProblems.FormNotFoundDetail },
        { "/api/forms/abc/checkcode/field-event", ApiProblems.ResourceNotFoundDetail },
    };

    /// <summary>
    /// Events of <see cref="NoBlockProgram"/> that select no block, as (event, field name, <c>values</c> JSON): After
    /// on a field without a block, with values of each wire type and with the read-only <c>OnsetWeek</c>, which a field
    /// event accepts although a save refuses it; and Before and Click on <see cref="BlockFieldName"/>, whose only
    /// block is After.
    /// </summary>
    public static TheoryData<string, string, string> NoMatchingBlockEvents => new()
    {
        { "FieldAfter", "LastName", """{"LastName":"Smith","DOB":"2011-01-15","Age":37,"Asian":true}""" },
        { "FieldAfter", "DOB", """{"OnsetWeek":3,"DOB":null}""" },
        { "FieldBefore", BlockFieldName, "{}" },
        { "FieldClick", BlockFieldName, """{"Asian":false}""" },
    };

    /// <summary>
    /// An <c>event</c> other than exactly <c>FieldBefore</c>, <c>FieldAfter</c> or <c>FieldClick</c>, or none, is a
    /// field-level 400 under <c>errors["event"]</c> alone, and the event is not evaluated.
    /// </summary>
    [Theory]
    [MemberData(nameof(InvalidEventMembers))]
    public async Task EventOtherThanTheThreeExactSpellingsIsRejectedUnderEvent(string? eventMember)
    {
        await using var host = EndpointHost.ForProgram(NoBlockProgram);

        var problem = await SendRejectedEventAsync(host, RequestJson(eventMember, "\"LastName\"", "{}"));

        Assert.Equal(ApiProblems.ValidationFailedDetail, problem.GetProperty("detail").GetString());
        AssertErrors(problem, [FieldEventService.EventKey], [FieldEventService.InvalidEventMessage]);
    }

    /// <summary>
    /// A <c>fieldName</c> that names no field on a page of the form, or none, is a field-level 400 under
    /// <c>errors["fieldName"]</c> alone, and the event is not evaluated.
    /// </summary>
    [Theory]
    [MemberData(nameof(InvalidFieldNameMembers))]
    public async Task FieldNameOfNoFieldOnAPageIsRejectedUnderFieldName(string? fieldNameMember)
    {
        await using var host = EndpointHost.ForProgram(NoBlockProgram);

        var problem = await SendRejectedEventAsync(host, RequestJson("\"FieldAfter\"", fieldNameMember, "{}"));

        Assert.Equal(ApiProblems.ValidationFailedDetail, problem.GetProperty("detail").GetString());
        AssertErrors(problem, [FieldEventService.FieldNameKey], [FieldEventService.InvalidFieldNameMessage]);
    }

    /// <summary>
    /// A valid event posted to a form the store does not hold is the 404 ProblemDetails, and nothing is evaluated.
    /// </summary>
    [Theory]
    [MemberData(nameof(UnknownFormPaths))]
    public async Task UnknownFormIsA404Problem(string path, string expectedDetail)
    {
        await using var host = EndpointHost.ForProgram(NoBlockProgram);
        using var content = JsonBody(RequestJson("\"FieldAfter\"", "\"LastName\"", "{}"));

        using var response = await host.Client.PostAsync(path, content, host.Timeout);

        var problem = await ReadUnevaluatedProblemAsync(host, response, HttpStatusCode.NotFound);
        Assert.Equal(expectedDetail, problem.GetProperty("detail").GetString());
    }

    /// <summary>
    /// A valid JSON body one byte over the 1,048,576-byte limit, declared by <c>Content-Length</c>, is the 413
    /// ProblemDetails, and nothing is evaluated.
    /// </summary>
    [Fact]
    public async Task BodyOneByteOverTheLimitIsA413Problem()
    {
        await using var host = EndpointHost.ForProgram(NoBlockProgram);

        // A valid event padded inside its LastName value, so only the size can reject it.
        const string Prefix = "{\"event\":\"FieldAfter\",\"fieldName\":\"LastName\",\"values\":{\"LastName\":\"";
        const string Suffix = "\"}}";
        var padding = RequestBodyLimit + 1 - Encoding.UTF8.GetByteCount(Prefix + Suffix);
        var body = Encoding.UTF8.GetBytes(Prefix + new string('x', padding) + Suffix);
        Assert.Equal(RequestBodyLimit + 1, body.Length);
        JsonDocument.Parse(body).Dispose();

        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue(ApiTestClient.JsonMediaType);
        content.Headers.ContentLength = body.Length;
        using var response = await host.Client.PostAsync(EventPath(FakeForms.CheckCodeViewId), content, host.Timeout);

        var problem = await ReadUnevaluatedProblemAsync(host, response, HttpStatusCode.RequestEntityTooLarge);
        Assert.Equal(ApiProblems.RequestTooLargeDetail, problem.GetProperty("detail").GetString());
    }

    /// <summary>
    /// A valid JSON body sent as <c>text/plain</c> is the 415 ProblemDetails, and nothing is evaluated.
    /// </summary>
    [Fact]
    public async Task TextPlainBodyIsA415Problem()
    {
        await using var host = EndpointHost.ForProgram(NoBlockProgram);
        using var content = new StringContent(
            RequestJson("\"FieldAfter\"", "\"LastName\"", "{}"),
            Encoding.UTF8,
            "text/plain");

        using var response = await host.Client.PostAsync(EventPath(FakeForms.CheckCodeViewId), content, host.Timeout);

        var problem = await ReadUnevaluatedProblemAsync(host, response, HttpStatusCode.UnsupportedMediaType);
        Assert.Equal(ApiProblems.UnsupportedMediaTypeDetail, problem.GetProperty("detail").GetString());
    }

    /// <summary>
    /// An event that selects no block is evaluated once and answers 200 with exactly the empty envelope, diagnostics
    /// included, whatever wire-typed values it carries, a read-only field's included.
    /// </summary>
    [Theory]
    [MemberData(nameof(NoMatchingBlockEvents))]
    public async Task EventSelectingNoBlockGivesTheExactEmptyEnvelope(
        string eventName,
        string fieldName,
        string valuesJson)
    {
        await using var host = EndpointHost.ForProgram(NoBlockProgram);

        var body = await SendAcceptedEventAsync(
            host,
            FakeForms.CheckCodeViewId,
            RequestJson(JsonSerializer.Serialize(eventName), JsonSerializer.Serialize(fieldName), valuesJson));

        AssertJsonEqual(EmptyEnvelope, body);
    }

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
        await using var host = EndpointHost.Start(new FakeDataStore(FormWithOptionField()));

        var problem = await SendRejectedEventAsync(
            host,
            $$"""{"event":"FieldAfter","fieldName":"Age","values":{{valuesJson}}}""");

        Assert.Equal(ApiProblems.ValidationFailedDetail, problem.GetProperty("detail").GetString());
        AssertErrors(problem, [expectedKey], [expectedMessage]);
    }

    /// <summary>
    /// An object value is rejected with the full fixed sentence, written out here rather than taken from
    /// <see cref="ValuesReader.ScalarValueExpected"/>, so the message keeps the subject and full stop every other
    /// field-level message has.
    /// </summary>
    [Fact]
    public async Task ObjectValueIsRejectedWithTheFullFixedSentence()
    {
        await using var host = EndpointHost.Start(new FakeDataStore(FormWithOptionField()));

        var problem = await SendRejectedEventAsync(
            host,
            """{"event":"FieldAfter","fieldName":"Age","values":{"LastName":{"first":"Smith"}}}""");

        AssertErrors(problem, ["LastName"], ["A string, number, boolean or null is expected."]);
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
        await using var host = EndpointHost.Start(new FakeDataStore(FormWithOptionField()));

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
        await using var host = EndpointHost.Start(new FakeDataStore(FormWithOptionField()));

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
        await using var host = EndpointHost.Start(new FakeDataStore(FormWithOptionField()));

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
        await using var host = EndpointHost.Start(store);

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
        await using var host = EndpointHost.Start(store);
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

    // P1 effect and diagnostic cases (AAP 0.3.5, 0.7.2)

    // Each effect statement once, on the fields of FakeForms.WithCheckCode: ENABLE the date DOB, DISABLE the checkbox
    // Asian, CLEAR the read-only number OnsetWeek, ASSIGN the text LastName, and a dialog with and without TITLETEXT.
    private const string BasicEffectsProgram = """
        Field Age
            After
                ENABLE DOB
                DISABLE Asian
                CLEAR OnsetWeek
                ASSIGN LastName = "assigned-text"
                DIALOG "Check the value" TITLETEXT = "Notice"
                DIALOG "No title"
            End-After
        End-Field
        """;

    // LastName enters enabled before DOB, leaves it for disabled and comes back: the later statement decides that it
    // is enabled, and its first entry decides that it is listed first.
    private const string EnablementOrderProgram = """
        Field Age
            After
                ENABLE LastName
                ENABLE DOB
                DISABLE LastName
                ENABLE LastName
            End-After
        End-Field
        """;

    // Age is assigned before LastName, cleared and assigned again: the last ASSIGN gives the value and keeps it out of
    // cleared, and its first assignment keeps it the first key of assigned.
    private const string AssignmentOrderProgram = """
        Field Age
            After
                ASSIGN Age = 1
                ASSIGN LastName = "second"
                CLEAR Age
                ASSIGN Age = 3
            End-After
        End-Field
        """;

    // An unsupported command on line 3 followed by a supported statement.
    private const string UnsupportedCommandProgram = """
        Field Age
            After
                GOTO DOB
                ASSIGN LastName = "after-goto"
            End-After
        End-Field
        """;

    // EPIWEEK of the date DOB, sent empty, fails at run time on line 4, after line 3 has enabled LastName.
    private const string RuntimeErrorProgram = """
        Field Age
            After
                ENABLE LastName
                ASSIGN OnsetWeek = EPIWEEK(DOB)
            End-After
        End-Field
        """;

    // A warning for the GOTO on line 3, then the runtime error of RuntimeErrorProgram on line 5, after line 4 has
    // enabled LastName.
    private const string RuntimeErrorAfterGotoProgram = """
        Field Age
            After
                GOTO DOB
                ENABLE LastName
                ASSIGN OnsetWeek = EPIWEEK(DOB)
            End-After
        End-Field
        """;

    /// <summary>
    /// Programs whose After block on <c>Age</c> runs without a diagnostic, as (program, the expected response, the
    /// expected key order of <c>assigned</c>).
    /// </summary>
    public static TheoryData<string, string, string[]> EffectCases => new()
    {
        {
            BasicEffectsProgram,
            """
            {
              "enabled": ["DOB"], "disabled": ["Asian"], "cleared": ["OnsetWeek"],
              "assigned": {"LastName": "assigned-text"},
              "dialogs": [{"message": "Check the value", "title": "Notice"}, {"message": "No title", "title": null}],
              "diagnostics": []
            }
            """,
            ["LastName"]
        },
        {
            EnablementOrderProgram,
            """
            {
              "enabled": ["LastName", "DOB"], "disabled": [], "cleared": [], "assigned": {}, "dialogs": [],
              "diagnostics": []
            }
            """,
            []
        },
        {
            AssignmentOrderProgram,
            """
            {
              "enabled": [], "disabled": [], "cleared": [], "assigned": {"Age": 3, "LastName": "second"}, "dialogs": [],
              "diagnostics": []
            }
            """,
            ["Age", "LastName"]
        },
    };

    /// <summary>
    /// The effects of the matching block come back as data in the response lists, in the engine's order: dialogs in
    /// execution order, each list by the first time its field entered it, membership by the last statement, and the
    /// keys of <c>assigned</c> by first assignment. Nothing is written.
    /// </summary>
    [Theory]
    [MemberData(nameof(EffectCases))]
    public async Task EffectsOfTheMatchingBlockAreReturnedAsData(
        string program,
        string expectedResponse,
        string[] expectedAssignedKeys)
    {
        await using var host = EndpointHost.ForProgram(program);

        var body = await SendAcceptedEventAsync(
            host,
            FakeForms.CheckCodeViewId,
            """{"event":"FieldAfter","fieldName":"Age","values":{"Age":37,"LastName":"Smith","OnsetWeek":12}}""");

        AssertJsonEqual(expectedResponse, body);
        Assert.Equal(
            expectedAssignedKeys,
            body.GetProperty("assigned").EnumerateObject().Select(entry => entry.Name));
    }

    /// <summary>
    /// An unsupported command is a <c>warning</c> diagnostic naming the form, its line and the command, it is logged
    /// without any field value, and the statement after it still runs.
    /// </summary>
    [Fact]
    public async Task UnsupportedCommandIsALoggedWarningAndTheNextStatementStillRuns()
    {
        var form = FakeForms.WithCheckCode(UnsupportedCommandProgram);
        await using var host = EndpointHost.Start(new FakeDataStore(form));

        var body = await SendAcceptedEventAsync(
            host,
            FakeForms.CheckCodeViewId,
            """{"event":"FieldAfter","fieldName":"Age","values":{"Age":37,"LastName":"Smith"}}""");

        AssertDiagnostic(
            Assert.Single(body.GetProperty("diagnostics").EnumerateArray()),
            "warning",
            form.Metadata.Name,
            line: 3,
            command: "GOTO");
        Assert.Equal("after-goto", body.GetProperty("assigned").GetProperty("LastName").GetString());
        AssertDiagnosticLogged(host, LogLevel.Warning, form.Metadata.Name, line: 3, command: "GOTO");
        Assert.False(host.Logs.ContainsText("after-goto"), "A log entry carries an assigned field value.");
        Assert.False(host.Logs.ContainsText("Smith"), "A log entry carries a submitted field value.");
    }

    /// <summary>
    /// A runtime error is an <c>error</c> diagnostic at the failing statement in a 200 response: the effects of the
    /// statements before it are kept and the failed assignment is absent.
    /// </summary>
    [Fact]
    public async Task RuntimeErrorIsAnErrorDiagnosticAndEarlierEffectsAreKept()
    {
        var form = FakeForms.WithCheckCode(RuntimeErrorProgram);
        await using var host = EndpointHost.Start(new FakeDataStore(form));

        var body = await SendAcceptedEventAsync(
            host,
            FakeForms.CheckCodeViewId,
            """{"event":"FieldAfter","fieldName":"Age","values":{"Age":37,"DOB":null,"OnsetWeek":12}}""");

        AssertDiagnostic(
            Assert.Single(body.GetProperty("diagnostics").EnumerateArray()),
            "error",
            form.Metadata.Name,
            line: 4,
            command: "ASSIGN");
        Assert.Equal(
            new string?[] { "LastName" },
            body.GetProperty("enabled").EnumerateArray().Select(name => name.GetString()));
        Assert.Empty(body.GetProperty("assigned").EnumerateObject());
        Assert.Empty(body.GetProperty("disabled").EnumerateArray());
        Assert.Empty(body.GetProperty("cleared").EnumerateArray());
        Assert.Empty(body.GetProperty("dialogs").EnumerateArray());
        AssertDiagnosticLogged(host, LogLevel.Warning, form.Metadata.Name, line: 4, command: "ASSIGN");
    }

    /// <summary>
    /// An unsupported function's name is program text, and a bracketed one keeps its whitespace and line breaks: the
    /// response's diagnostics name each unsupported function as the engine reported it, while each one's log line
    /// names the fixed <see cref="FormService.UnregisteredFunctionCommand"/> whatever its name's spelling, and no
    /// captured line holds the name. <c>[GOTO]</c> and <c>FORMAT</c> are spelled like vocabulary, yet only the
    /// <c>GOTO</c> statement after them is logged as <c>GOTO</c>.
    /// </summary>
    [Fact]
    public async Task UnsupportedFunctionNameIsReturnedButLoggedAsTheFixedCode()
    {
        string sentinel = "SentinelFn" + Guid.NewGuid().ToString("N");
        string functionName = sentinel + "\r\n Secret";
        var form = FakeForms.WithCheckCode(UnsupportedFunctionProgram(functionName));
        await using var host = EndpointHost.Start(new FakeDataStore(form));

        var body = await SendAcceptedEventAsync(
            host,
            FakeForms.CheckCodeViewId,
            """{"event":"FieldAfter","fieldName":"Age","values":{"Age":37}}""");

        var diagnostics = body.GetProperty("diagnostics").EnumerateArray().ToArray();
        Assert.Equal(4, diagnostics.Length);
        string formName = form.Metadata.Name;
        AssertDiagnostic(diagnostics[0], "warning", formName, line: 3, command: functionName.ToUpperInvariant());
        AssertDiagnostic(diagnostics[1], "warning", formName, line: 5, command: "GOTO");
        AssertDiagnostic(diagnostics[2], "warning", formName, line: 6, command: "FORMAT");
        AssertDiagnostic(diagnostics[3], "warning", formName, line: 7, command: "GOTO");
        Assert.Equal("after-function", body.GetProperty("assigned").GetProperty("LastName").GetString());

        // One line per diagnostic, in order, each at Warning under its form, line and mapped command.
        string unregistered = FormService.UnregisteredFunctionCommand;
        AssertDiagnosticLogged(host, LogLevel.Warning, formName, line: 3, command: unregistered);
        AssertDiagnosticLogged(host, LogLevel.Warning, formName, line: 5, command: unregistered);
        AssertDiagnosticLogged(host, LogLevel.Warning, formName, line: 6, command: unregistered);
        AssertDiagnosticLogged(host, LogLevel.Warning, formName, line: 7, command: "GOTO");
        Assert.Equal(4, host.Logs.Find(entry => entry.Category == typeof(FieldEventService).FullName).Count);
        Assert.False(host.Logs.ContainsText(sentinel), "A log entry holds the function name.");
        Assert.False(host.Logs.ContainsText(sentinel.ToUpperInvariant()), "A log entry holds the function name.");
    }

    /// <summary>
    /// A log sink that throws for the field-event service's lines changes nothing the event answers: the response is
    /// the 200 with its effects and every diagnostic, and each diagnostic's line still reaches the other sinks.
    /// </summary>
    [Fact]
    public async Task ThrowingLogSinkLeavesTheFieldEventResponseUnchanged()
    {
        var form = FakeForms.WithCheckCode(RuntimeErrorAfterGotoProgram);
        using var throwing = new ThrowingLoggerProvider(typeof(FieldEventService));
        await using var host = EndpointHost.Start(new FakeDataStore(form), throwing);

        var body = await SendAcceptedEventAsync(
            host,
            FakeForms.CheckCodeViewId,
            """{"event":"FieldAfter","fieldName":"Age","values":{"Age":37,"DOB":null}}""");

        var diagnostics = body.GetProperty("diagnostics").EnumerateArray().ToArray();
        Assert.Equal(2, diagnostics.Length);
        AssertDiagnostic(diagnostics[0], "warning", form.Metadata.Name, line: 3, command: "GOTO");
        AssertDiagnostic(diagnostics[1], "error", form.Metadata.Name, line: 5, command: "ASSIGN");
        Assert.Equal(
            new string?[] { "LastName" },
            body.GetProperty("enabled").EnumerateArray().Select(name => name.GetString()));
        Assert.Equal(2, throwing.Throws);
        AssertDiagnosticLogged(host, LogLevel.Warning, form.Metadata.Name, line: 3, command: "GOTO");
        AssertDiagnosticLogged(host, LogLevel.Warning, form.Metadata.Name, line: 5, command: "ASSIGN");
        Assert.Empty(host.Logs.Find(entry => entry.ExceptionType is not null));
    }

    /// <summary>
    /// A stored program longer than the store serves whole is refused by the engine: the event is a 200 with no
    /// effects and one <c>LIMIT</c> warning at line 1, logged once, although the program's own <c>Age</c> block would
    /// have had effects. The store hands over no more than the first <see cref="IDataStore.MaxCheckCodeLength"/> + 1
    /// code units.
    /// </summary>
    [Fact]
    public async Task OversizedStoredProgramIsOneLimitWarningWithNoEffects()
    {
        string program = BasicEffectsProgram + "\n//" + new string('x', IDataStore.MaxCheckCodeLength);
        var form = FakeForms.WithCheckCode(program);
        await using var host = EndpointHost.Start(new FakeDataStore(form));

        var body = await SendAcceptedEventAsync(
            host,
            FakeForms.CheckCodeViewId,
            """{"event":"FieldAfter","fieldName":"Age","values":{"Age":37}}""");

        AssertJsonEqual(
            """
            {
              "enabled": [], "disabled": [], "cleared": [], "assigned": {}, "dialogs": [],
              "diagnostics": [
                {"severity": "warning", "form": "FakeCheckCode", "line": 1, "command": "LIMIT",
                 "message": "Check code exceeds the 1048576-character limit; program skipped"}
              ]
            }
            """,
            body);
        AssertDiagnosticLogged(host, LogLevel.Warning, form.Metadata.Name, line: 1, command: "LIMIT");
        Assert.Single(host.Logs.Find(entry => entry.Category == typeof(FieldEventService).FullName));
        Assert.Equal(1, host.Engine.RunCalls);

        string? served = await host.Store.GetCheckCodeAsync(FakeForms.CheckCodeViewId, host.Timeout);
        Assert.Equal(program[..(IDataStore.MaxCheckCodeLength + 1)], served);
    }

    /// <summary>
    /// A block with more diagnostics than one response lists returns exactly
    /// <see cref="CheckCodeEngine.MaxDiagnostics"/> of them, the last one <c>LIMIT</c> warning at the line of the first
    /// one left out, with the block's effects kept, and the service writes one log line per listed diagnostic.
    /// </summary>
    [Fact]
    public async Task DiagnosticsPastTheBudgetAreBoundedInTheResponseAndTheLog()
    {
        const int Gotos = 1_000;
        const int Listed = CheckCodeEngine.MaxDiagnostics - 1;
        string program = UnsupportedCommandProgram.ReplaceLineEndings("\n").Replace(
            "GOTO DOB",
            string.Join("\n", Enumerable.Repeat("GOTO DOB", Gotos)),
            StringComparison.Ordinal);
        var form = FakeForms.WithCheckCode(program);
        await using var host = EndpointHost.Start(new FakeDataStore(form));

        var body = await SendAcceptedEventAsync(
            host,
            FakeForms.CheckCodeViewId,
            """{"event":"FieldAfter","fieldName":"Age","values":{"Age":37}}""");

        var diagnostics = body.GetProperty("diagnostics").EnumerateArray().ToArray();
        Assert.Equal(CheckCodeEngine.MaxDiagnostics, diagnostics.Length);
        for (var i = 0; i < Listed; i++)
        {
            AssertDiagnostic(diagnostics[i], "warning", form.Metadata.Name, line: 3 + i, command: "GOTO");
        }

        AssertDiagnostic(diagnostics[Listed], "warning", form.Metadata.Name, line: 3 + Listed, command: "LIMIT");
        Assert.Contains(
            (Gotos - Listed).ToString(CultureInfo.InvariantCulture),
            diagnostics[Listed].GetProperty("message").GetString(),
            StringComparison.Ordinal);
        Assert.Equal("after-goto", body.GetProperty("assigned").GetProperty("LastName").GetString());

        Assert.Equal(
            CheckCodeEngine.MaxDiagnostics,
            host.Logs.Find(entry => entry.Category == typeof(FieldEventService).FullName).Count);
        AssertDiagnosticLogged(host, LogLevel.Warning, form.Metadata.Name, line: 3 + Listed, command: "LIMIT");
    }

    /// <summary>
    /// A block whose comparisons read a long request value past the evaluator's work budget, 100 comparisons of two
    /// 100,000-character texts against the 4,000,000 steps of
    /// <see cref="EpiInfo.CheckCode.Evaluation.Evaluator.MaxWork"/>, is a 200 that keeps the effect of the statement
    /// before the <c>IF</c> that ran out, runs nothing after it, and holds one <c>LIMIT</c> warning at that <c>IF</c>'s
    /// line with a fixed message, logged once with its form, severity, line and command.
    /// </summary>
    [Fact]
    public async Task WorkPastTheBudgetIsOneLimitWarningAfterTheEarlierEffects()
    {
        string comparisons = string.Join(" AND ", Enumerable.Repeat("LastName = LastName", 100));
        string program = $"""
            Field Age
                After
                    ENABLE DOB
                    IF {comparisons} THEN
                        DISABLE Asian
                    END-IF
                    ASSIGN LastName = "after"
                End-After
            End-Field
            """;
        var form = FakeForms.WithCheckCode(program);
        await using var host = EndpointHost.Start(new FakeDataStore(form));
        string lastName = new('x', 100_000);

        var body = await SendAcceptedEventAsync(
            host,
            FakeForms.CheckCodeViewId,
            $$$"""{"event":"FieldAfter","fieldName":"Age","values":{"LastName":"{{{lastName}}}"}}""");

        AssertJsonEqual(
            """
            {
              "enabled": ["DOB"], "disabled": [], "cleared": [], "assigned": {}, "dialogs": [],
              "diagnostics": [
                {"severity": "warning", "form": "FakeCheckCode", "line": 4, "command": "LIMIT",
                 "message": "Check code exceeds the 4000000-step work limit; remaining statements skipped"}
              ]
            }
            """,
            body);
        AssertDiagnosticLogged(host, LogLevel.Warning, form.Metadata.Name, line: 4, command: "LIMIT");
        Assert.Single(host.Logs.Find(entry => entry.Category == typeof(FieldEventService).FullName));
        Assert.Equal(1, host.Engine.RunCalls);
    }

    /// <summary>
    /// Events whose block assigns the integer-column number field <c>Visits</c> a number that is not whole although
    /// its nearest double is, as (the event's field, its <c>values</c> JSON, the JSON number expected in
    /// <c>assigned</c>): a copy of the <c>float</c> number <c>Age</c>, or a check-code literal.
    /// </summary>
    public static TheoryData<string, string, string> NonIntegralWholeDoubleAssignments => new()
    {
        { "Age", """{"Age":1.0000000000000001}""", "1.0000000000000001" },
        { "Age", """{"Age":9007199254740992.5}""", "9007199254740992.5" },
        { "Age", """{"Age":-1E-400}""", "-1e-400" },
        { "LastName", "{}", "2.0000000000000001" },
    };

    /// <summary>
    /// An assigned number that is not whole although its nearest double is, such as <c>1.0000000000000001</c>, is
    /// returned as that exact JSON number, never as the whole double 1, so the integer column of <c>Visits</c> refuses
    /// it when it is saved, exactly as it refuses the same number typed in: the save is a 400 keyed by
    /// <c>Visits</c> and nothing is written.
    /// </summary>
    [Theory]
    [MemberData(nameof(NonIntegralWholeDoubleAssignments))]
    public async Task AssignedNumberWhoseDoubleIsWholeKeepsItsExactNumberAndAnIntegerColumnRefusesIt(
        string fieldName,
        string valuesJson,
        string expectedNumber)
    {
        var store = new FakeDataStore(FormWithIntegerColumnNumber());
        await using var host = EndpointHost.Start(store);

        var body = await SendAcceptedEventAsync(
            host,
            FakeForms.CheckCodeViewId,
            $$"""{"event":"FieldAfter","fieldName":"{{fieldName}}","values":{{valuesJson}}}""");

        var assigned = body.GetProperty("assigned");
        Assert.Equal([IntegerFieldName], assigned.EnumerateObject().Select(entry => entry.Name));
        var assignedNumber = assigned.GetProperty(IntegerFieldName);
        Assert.Equal(JsonValueKind.Number, assignedNumber.ValueKind);
        Assert.Equal(expectedNumber, assignedNumber.GetRawText());
        Assert.Empty(body.GetProperty("diagnostics").EnumerateArray());

        // The assigned value saved as the client sends it back: the JSON number exactly as the event returned it.
        var savedMember = $$"""{"{{IntegerFieldName}}":{{assignedNumber.GetRawText()}}}""";
        using var content = JsonBody($$"""{"values":{{savedMember}}}""");
        using var save = await host.Client.PostAsync(
            $"/api/forms/{FakeForms.CheckCodeViewId}/records",
            content,
            host.Timeout);
        var saveText = await save.Content.ReadAsStringAsync(host.Timeout);
        Assert.True(
            save.StatusCode == HttpStatusCode.BadRequest,
            $"The save returned {(int)save.StatusCode}: {saveText}");
        Assert.DoesNotContain(expectedNumber, saveText, StringComparison.Ordinal);
        using var saveDocument = JsonDocument.Parse(saveText);
        AssertErrors(saveDocument.RootElement, [IntegerFieldName], [ValidationMessages.NumberNotStorable]);
        Assert.Empty(store.WriteCalls);
    }

    // The After block on Age: from line 3, a call of an unsupported function whose bracketed name is the argument,
    // then on lines 5 and 6 calls of the unsupported functions [GOTO] and FORMAT, a GOTO statement on line 7 and an
    // ASSIGN that still runs; those line numbers hold for a name with one line break. Line breaks are normalised
    // before the name is placed, so a line break in it is kept.
    private static string UnsupportedFunctionProgram(string functionName) => """
        Field Age
            After
                ASSIGN OnsetWeek = [{0}](Age)
                ASSIGN OnsetWeek = [GOTO](Age)
                ASSIGN OnsetWeek = FORMAT(Age)
                GOTO LastName
                ASSIGN LastName = "after-function"
            End-After
        End-Field
        """.ReplaceLineEndings("\n").Replace("{0}", functionName, StringComparison.Ordinal);

    // Asserts one diagnostic of the response: its severity, form, 1-based line and command, and a message.
    private static void AssertDiagnostic(JsonElement diagnostic, string severity, string form, int line, string command)
    {
        Assert.Equal(severity, diagnostic.GetProperty("severity").GetString());
        Assert.Equal(form, diagnostic.GetProperty("form").GetString());
        Assert.Equal(line, diagnostic.GetProperty("line").GetInt32());
        Assert.Equal(command, diagnostic.GetProperty("command").GetString());
        Assert.False(
            string.IsNullOrWhiteSpace(diagnostic.GetProperty("message").GetString()),
            "A diagnostic carries a message.");
    }

    // Asserts that the field-event service logged the diagnostic exactly once, at level, by its form, line and command.
    private static void AssertDiagnosticLogged(EndpointHost host, LogLevel level, string form, int line, string command)
    {
        var entry = Assert.Single(host.Logs.Find(
            candidate => candidate.Category == typeof(FieldEventService).FullName
                && HasStateValue(candidate, "Form", form)
                && HasStateValue(candidate, "Line", line)
                && HasStateValue(candidate, "Command", command)));
        Assert.Equal(level, entry.Level);
    }

    private static bool HasStateValue(CapturedLogEntry entry, string key, object expected) =>
        entry.State.Any(pair => string.Equals(pair.Key, key, StringComparison.Ordinal) && Equals(pair.Value, expected));


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

    // FakeForms.WithCheckCode carrying ExactAssignmentProgram, with the number field Visits on an int column added to
    // its page, since every fixture number field has a float column.
    private static FakeForm FormWithIntegerColumnNumber()
    {
        var form = FakeForms.WithCheckCode(ExactAssignmentProgram);
        var page = form.Metadata.Pages[0];
        var visits = new FieldMetadata
        {
            FieldId = (FakeForms.CheckCodeViewId * 1000) + 10,
            Name = IntegerFieldName,
            FieldTypeId = NumberTypeId,
            FieldTypeName = "Number",
            Prompt = IntegerFieldName,
            TabIndex = 6m,
            PageId = page.PageId,
            Column = FieldTypeMapper.DescribeColumn(IntegerFieldName, "int", 4),
        };

        return form with { Metadata = form.Metadata with { Fields = [.. form.Metadata.Fields, visits] } };
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
        return await ReadUnevaluatedProblemAsync(host, response, HttpStatusCode.BadRequest);
    }

    // Asserts the contract every answer issued before evaluation shares: the expected status as an
    // application/problem+json ProblemDetails with a traceId and no submitted text, the program never read, the engine
    // never run and nothing written. Returns the body.
    private static async Task<JsonElement> ReadUnevaluatedProblemAsync(
        EndpointHost host,
        HttpResponseMessage response,
        HttpStatusCode expected)
    {
        var text = await response.Content.ReadAsStringAsync(host.Timeout);

        Assert.True(
            response.StatusCode == expected,
            $"Expected {(int)expected}, got {(int)response.StatusCode}: {text}");
        Assert.Equal(ApiProblems.ProblemContentType, response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain(Sentinel, text, StringComparison.Ordinal);

        Assert.Equal(0, host.Engine.RunCalls);
        Assert.DoesNotContain(host.Store.Calls, call => call.Method == nameof(IDataStore.GetCheckCodeAsync));
        Assert.Empty(host.Store.WriteCalls);

        using var document = JsonDocument.Parse(text);
        return ApiTestClient.AssertProblemDetails(document.RootElement.Clone(), (int)expected);
    }

    // Posts requestJson to the field-event route of viewId and asserts a 200 application/json response from exactly one
    // evaluation of the program the store was asked for, with nothing written. Returns the field-event response body.
    private static async Task<JsonElement> SendAcceptedEventAsync(EndpointHost host, int viewId, string requestJson)
    {
        using var response = await PostEventAsync(host, viewId, requestJson);
        var text = await response.Content.ReadAsStringAsync(host.Timeout);

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"{requestJson} returned {(int)response.StatusCode}: {text}");
        Assert.Equal(ApiTestClient.JsonMediaType, response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(1, host.Engine.RunCalls);
        Assert.Single(host.Store.Calls, call => call.Method == nameof(IDataStore.GetCheckCodeAsync));
        Assert.Empty(host.Store.WriteCalls);

        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static async Task<HttpResponseMessage> PostEventAsync(EndpointHost host, int viewId, string requestJson)
    {
        using var content = JsonBody(requestJson);
        return await host.Client.PostAsync(EventPath(viewId), content, host.Timeout);
    }

    private static string EventPath(int viewId) =>
        string.Create(CultureInfo.InvariantCulture, $"/api/forms/{viewId}/checkcode/field-event");

    // The raw JSON text as an application/json; charset=utf-8 body, sent exactly as written.
    private static StringContent JsonBody(string rawJson) =>
        new(rawJson, Encoding.UTF8, ApiTestClient.JsonMediaType);

    // A field-event body from raw JSON member values; a null member is left out of the object altogether.
    private static string RequestJson(string? eventMember, string? fieldNameMember, string? valuesMember)
    {
        var members = new List<string>(3);
        if (eventMember is not null)
        {
            members.Add("\"event\":" + eventMember);
        }

        if (fieldNameMember is not null)
        {
            members.Add("\"fieldName\":" + fieldNameMember);
        }

        if (valuesMember is not null)
        {
            members.Add("\"values\":" + valuesMember);
        }

        return "{" + string.Join(",", members) + "}";
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
    /// One test's API host: <c>Program.cs</c> on TestServer through <see cref="ApiFactory"/>, over a
    /// <see cref="FakeDataStore"/>, with the factory's <see cref="CountingCheckCodeEngine"/> around the real check-code
    /// engine and its <see cref="CapturingLoggerProvider"/>. Every request crosses the production pipeline, its guards
    /// included; nothing binds a port and nothing connects to SQL Server.
    /// </summary>
    private sealed class EndpointHost : IAsyncDisposable
    {
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

        private readonly ApiFactory _factory;

        private readonly CancellationTokenSource _timeout = new(RequestTimeout);

        private EndpointHost(ApiFactory factory, FakeDataStore store, ILoggerProvider? extraLogs)
        {
            _factory = factory;
            Store = store;

            // The derived factory runs the factory's own configuration first, so its doubles stay the ones this host
            // exposes, and disposing the factory disposes it too.
            Client = extraLogs is { } provider
                ? factory
                    .WithWebHostBuilder(builder => builder.ConfigureTestServices(
                        services => services.AddSingleton(provider)))
                    .CreateClient()
                : factory.CreateClient();
        }

        /// <summary>The store the host reads and writes.</summary>
        public FakeDataStore Store { get; }

        /// <summary>The check-code engine the host uses, counting its calls.</summary>
        public CountingCheckCodeEngine Engine => _factory.Engine;

        /// <summary>Every log entry the committed logging filters let through.</summary>
        public CapturingLoggerProvider Logs => _factory.Logs;

        /// <summary>A client whose requests go straight to the test server.</summary>
        public HttpClient Client { get; }

        /// <summary>Cancels a request or read that has not finished within the host's time limit.</summary>
        public CancellationToken Timeout => _timeout.Token;

        /// <summary>Starts a host over <paramref name="store"/>.</summary>
        /// <param name="store">The in-memory store the endpoints use.</param>
        /// <param name="extraLogs">One more logger provider beside the factory's, or <see langword="null"/>.</param>
        /// <returns>The started host; the caller disposes it.</returns>
        public static EndpointHost Start(FakeDataStore store, ILoggerProvider? extraLogs = null)
        {
            var factory = new ApiFactory(ApiFactoryOptions.ForFake(store));
            try
            {
                return new EndpointHost(factory, store, extraLogs);
            }
            catch
            {
                factory.Dispose();
                throw;
            }
        }

        /// <summary>Starts a host whose store holds <see cref="FakeForms.WithCheckCode(string)"/> with
        /// <paramref name="program"/>.</summary>
        /// <param name="program">The form's check-code program.</param>
        /// <returns>The started host; the caller disposes it.</returns>
        public static EndpointHost ForProgram(string program) =>
            Start(new FakeDataStore(FakeForms.WithCheckCode(program)));

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _factory.DisposeAsync();
            _timeout.Dispose();
        }
    }
}

/// <summary>
/// The identity of the field-event <c>values</c> keys of <c>POST /api/forms/{viewId}/checkcode/field-event</c>
/// (AAP 0.3.5, "Stateless field-event flow"): every key must be a data field of the form and every value must have
/// its wire type, and nothing else is checked. A key names the data field <see cref="DataFieldSet.TryGet"/> resolves
/// it to, so two data fields whose canonical names differ only by letter case are two fields, each typed for its own
/// type, while two keys that resolve to one field name it twice. <c>fieldName</c> likewise names one field shown on a
/// page of the form, and the engine runs that field's block only.
/// </summary>
/// <remarks>
/// The class needs no database and no host: it runs <see cref="ValuesReader"/> and <see cref="FieldEventService"/>
/// directly, the service over <see cref="FakeDataStore"/> and the real <see cref="CheckCodeEngine"/>. It carries no
/// <c>Integration</c> trait and joins no database collection, so <c>--filter "Category!=Integration"</c> selects it.
/// The "two keys differing only by case are a 400" rule of AAP 0.3.4.5 belongs to record writes, not to field events.
/// </remarks>
public sealed class FieldEventValuesTests
{
    private const string CaseVariantFormName = "CaseVariant";

    private const string SingleAgeFormName = "SingleAge";

    private const int CaseVariantViewId = 9101;

    private const int SingleAgeViewId = 9102;

    private const int AgeLastViewId = 9103;

    private const int PagelessVariantViewId = 9104;

    // A page id no form of this class has.
    private const int OffFormPageId = 999999;

    private const int TextTypeId = 1;

    private const int NumberTypeId = 5;

    // The two canonical names of the case-variant fields, in either order.
    private const string AgeFirstBody = """{"Age":30,"AGE":"x"}""";

    private const string AgeLastBody = """{"AGE":"x","Age":30}""";

    // One block per case variant, each showing a dialog that names its block.
    private const string BothBlocksProgram = """
        Field Age
            After
                DIALOG "Age block"
            End-After
        End-Field
        Field AGE
            After
                DIALOG "AGE block"
            End-After
        End-Field
        """;

    private const string AgeBlockProgram = """
        Field Age
            After
                DIALOG "Age block"
            End-After
        End-Field
        """;

    /// <summary>
    /// Bodies whose keys are the canonical names of the two case-variant fields, with the key order the typed values
    /// must keep.
    /// </summary>
    public static TheoryData<string, string[]> CaseVariantBodies => new()
    {
        { AgeFirstBody, ["Age", "AGE"] },
        { AgeLastBody, ["AGE", "Age"] },
    };

    /// <summary>The same bodies as <see cref="CaseVariantBodies"/>, as field-event <c>values</c>.</summary>
    public static TheoryData<string> CaseVariantRequestBodies => new() { AgeFirstBody, AgeLastBody };

    /// <summary>
    /// Rejected bodies: the form, the body, and the expected error keys in order, each with its one message.
    /// </summary>
    public static TheoryData<string, string, string[], string[]> RejectedBodies => new()
    {
        // Another spelling of the one field Age resolves to it, so the field is named twice; the error carries the
        // canonical name whichever spelling repeats it.
        { SingleAgeFormName, """{"Age":1,"age":2}""", ["Age"], [ValidationMessages.DuplicateField] },
        { SingleAgeFormName, """{"age":1,"Age":2}""", ["Age"], [ValidationMessages.DuplicateField] },

        // An exact repeat, which the raw entries keep.
        { SingleAgeFormName, """{"Age":1,"Age":2}""", ["Age"], [ValidationMessages.DuplicateField] },
        { CaseVariantFormName, """{"Age":30,"AGE":"x","Age":31}""", ["Age"], [ValidationMessages.DuplicateField] },
        { CaseVariantFormName, """{"AGE":"x","Age":30,"AGE":"y"}""", ["AGE"], [ValidationMessages.DuplicateField] },

        // Each case variant is checked against its own field's wire type, never its namesake's.
        {
            CaseVariantFormName,
            """{"Age":"x","AGE":30}""",
            ["Age", "AGE"],
            [ValidationMessages.NumberExpected, ValidationMessages.TextExpected]
        },

        // A name the form has no metaFields row for is keyed by the values member, never as submitted.
        {
            CaseVariantFormName,
            """{"Age":30,"Agee":1}""",
            [RecordValidator.ValuesKey],
            [ValidationMessages.UnknownValuesName]
        },

        // aGE equals Age and AGE ignoring case and neither exactly, so it names no field and is keyed by the values
        // member, never as submitted nor as either field.
        {
            CaseVariantFormName,
            """{"Age":30,"aGE":"x"}""",
            [RecordValidator.ValuesKey],
            [ValidationMessages.AmbiguousValuesName]
        },
    };

    // AAP 0.3.5: Age (number, page 1) and AGE (text, page 2) are distinct data fields, so a body naming both is valid.
    [Theory]
    [MemberData(nameof(CaseVariantBodies))]
    public void ToTyped_CaseVariantDataFields_AreTypedAsDistinctFields(string body, string[] expectedKeys)
    {
        var raw = ReadRaw(body);

        var typed = ValuesReader.ToTyped(CaseVariantForm(), raw);

        Assert.True(typed.IsSuccess);
        Assert.Empty(typed.FieldErrors);
        Assert.Equal(expectedKeys, typed.Values.Keys);
        Assert.Equal(FieldValueKind.Number, typed.Values["Age"].Kind);
        Assert.Equal(30d, typed.Values["Age"].AsNumber());
        Assert.Equal(FieldValueKind.Text, typed.Values["AGE"].Kind);
        Assert.Equal("x", typed.Values["AGE"].AsText());

        // Lookups are ordinal on the canonical names: a third spelling is neither key.
        Assert.False(typed.Values.ContainsKey("age"));
    }

    [Theory]
    [MemberData(nameof(RejectedBodies))]
    public void ToTyped_RejectsRepeatsUnknownNamesAndWrongWireTypes(
        string formName,
        string body,
        string[] expectedKeys,
        string[] expectedMessages)
    {
        var form = formName == CaseVariantFormName ? CaseVariantForm() : SingleAgeForm();
        var raw = ReadRaw(body);

        var typed = ValuesReader.ToTyped(form, raw);

        Assert.False(typed.IsSuccess);
        Assert.Empty(typed.Values);
        Assert.Equal(expectedKeys, typed.FieldErrors.Keys);
        for (var i = 0; i < expectedKeys.Length; i++)
        {
            Assert.Equal(new[] { expectedMessages[i] }, typed.FieldErrors[expectedKeys[i]]);
        }
    }

    // AAP 0.3.5: the service validates only that every key is a data field and every value has its wire type, so a
    // body naming both case variants reaches the engine, and each value is read as its own field.
    [Theory]
    [MemberData(nameof(CaseVariantRequestBodies))]
    public async Task RunAsync_CaseVariantDataFields_ReachTheEngine(string body)
    {
        const string Program = """
            Field Age
                After
                    IF Age = 30 AND AGE = "x" THEN
                        ASSIGN AGE = "seen"
                    END-IF
                End-After
            End-Field
            """;
        var store = new FakeDataStore(new FakeForm(CaseVariantForm(), NoOptions(), Program));
        var service = new FieldEventService(store, new CheckCodeEngine(), NullLogger<FieldEventService>.Instance);
        var request = new FieldEventRequest(Json("\"FieldAfter\""), Json("\"Age\""), Json(body));

        var result = await service.RunAsync(CaseVariantViewId, request, CancellationToken.None);

        var response = Assert.IsType<Ok<FieldEventResponse>>(result).Value;
        Assert.NotNull(response);
        Assert.Equal(new[] { "AGE" }, response.Assigned.Keys);
        Assert.Equal("seen", response.Assigned["AGE"].AsText());
        Assert.Empty(response.Enabled);
        Assert.Empty(response.Disabled);
        Assert.Empty(response.Cleared);
        Assert.Empty(response.Dialogs);
        Assert.Empty(response.Diagnostics);
        Assert.Empty(store.WriteCalls);
    }

    // AAP 0.3.5: fieldName names one field of the form, its exact spelling first, so with AGE listed before Age each
    // case variant runs its own block, never the one first in metadata order or last in the program.
    [Theory]
    [InlineData("Age", "Age block")]
    [InlineData("AGE", "AGE block")]
    public async Task RunAsync_FieldNameSpelledExactly_RunsThatFieldsBlock(string fieldName, string expectedDialog)
    {
        var result = await RunEventAsync(AgeLastForm(), BothBlocksProgram, fieldName);

        var response = Assert.IsType<Ok<FieldEventResponse>>(result).Value;
        Assert.NotNull(response);
        Assert.Equal(new[] { expectedDialog }, response.Dialogs.Select(dialog => dialog.Message));
        Assert.Empty(response.Diagnostics);
    }

    // aGE equals Age and AGE ignoring case and neither exactly, so it names no field of the form.
    [Fact]
    public async Task RunAsync_FieldNameAmbiguousAmongDataFields_IsAFieldNameError()
    {
        var result = await RunEventAsync(AgeLastForm(), BothBlocksProgram, "aGE");

        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode;
        Assert.Equal(StatusCodes.Status400BadRequest, status);
        var problem = Assert.IsType<HttpValidationProblemDetails>(
            Assert.IsAssignableFrom<IValueHttpResult>(result).Value);
        Assert.Equal(new[] { FieldEventService.FieldNameKey }, problem.Errors.Keys);
        Assert.Equal(
            new[] { FieldEventService.InvalidFieldNameMessage },
            problem.Errors[FieldEventService.FieldNameKey]);
    }

    // AAP 0.3.5: "a field without a block gives the empty envelope". The block of Age is not AGE's.
    [Fact]
    public async Task RunAsync_FieldWhoseCaseVariantAloneHasABlock_GivesTheEmptyEnvelope()
    {
        var result = await RunEventAsync(AgeLastForm(), AgeBlockProgram, "AGE");

        var response = Assert.IsType<Ok<FieldEventResponse>>(result).Value;
        Assert.NotNull(response);
        Assert.Empty(response.Enabled);
        Assert.Empty(response.Disabled);
        Assert.Empty(response.Cleared);
        Assert.Empty(response.Assigned);
        Assert.Empty(response.Dialogs);
        Assert.Empty(response.Diagnostics);
    }

    // A row no page of the form shows, with no page or a page outside the form, is no field an event can name, so
    // listed before the page field Age and spelled AGE, it neither hides Age nor makes its name ambiguous.
    [Theory]
    [InlineData(null, "Age")]
    [InlineData(null, "age")]
    [InlineData(OffFormPageId, "Age")]
    [InlineData(OffFormPageId, "age")]
    public async Task RunAsync_PagelessRowSpelledLikeAPageField_LeavesTheFieldNameValid(
        int? pagelessRowPageId,
        string fieldName)
    {
        var result = await RunEventAsync(PagelessVariantForm(pagelessRowPageId), AgeBlockProgram, fieldName);

        var response = Assert.IsType<Ok<FieldEventResponse>>(result).Value;
        Assert.NotNull(response);
        Assert.Equal(new[] { "Age block" }, response.Dialogs.Select(dialog => dialog.Message));
        Assert.Empty(response.Diagnostics);
    }

    // Raises FieldAfter on fieldName with empty values, over the real engine and a store holding only form.
    private static Task<IResult> RunEventAsync(FormMetadata form, string program, string fieldName)
    {
        var store = new FakeDataStore(new FakeForm(form, NoOptions(), program));
        var service = new FieldEventService(store, new CheckCodeEngine(), NullLogger<FieldEventService>.Instance);
        var request = new FieldEventRequest(
            Json("\"FieldAfter\""),
            Json(JsonSerializer.Serialize(fieldName)),
            Json("{}"));
        return service.RunAsync(form.ViewId, request, CancellationToken.None);
    }

    private static IReadOnlyList<KeyValuePair<string, FieldValue>> ReadRaw(string body)
    {
        var raw = ValuesReader.ReadRaw(Json(body));
        Assert.True(raw.IsSuccess);
        return raw.Values;
    }

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static ReadOnlyDictionary<string, IReadOnlyList<FieldOption>> NoOptions() =>
        new(new Dictionary<string, IReadOnlyList<FieldOption>>(StringComparer.OrdinalIgnoreCase));

    // Synthetic: the number field Age on page 1 and the text field AGE on page 2, two data fields whose canonical names
    // differ only by letter case. Age comes first in metadata and in storage order.
    private static FormMetadata CaseVariantForm()
    {
        const int viewId = CaseVariantViewId;
        var first = Page(viewId, CaseVariantFormName, 1, "Page 1", 0);
        var second = Page(viewId, CaseVariantFormName, 2, "Page 2", 1);
        return new FormMetadata(
            viewId,
            CaseVariantFormName,
            CaseVariantFormName,
            [first, second],
            [
                Field(viewId, 1, first, "Age", NumberTypeId, "Number", "float", 8),
                Field(viewId, 2, second, "AGE", TextTypeId, "Text", "nvarchar", 8000),
            ]);
    }

    // Synthetic: one page with the one number field Age.
    private static FormMetadata SingleAgeForm()
    {
        const int viewId = SingleAgeViewId;
        var page = Page(viewId, SingleAgeFormName, 1, "Page 1", 0);
        return new FormMetadata(
            viewId,
            SingleAgeFormName,
            SingleAgeFormName,
            [page],
            [Field(viewId, 1, page, "Age", NumberTypeId, "Number", "float", 8)]);
    }

    // Synthetic: the case-variant fields of CaseVariantForm with the text field AGE (page 2) listed first in metadata
    // order, while the number field Age (page 1) stays first in storage order.
    private static FormMetadata AgeLastForm()
    {
        const int viewId = AgeLastViewId;
        var first = Page(viewId, CaseVariantFormName, 1, "Page 1", 0);
        var second = Page(viewId, CaseVariantFormName, 2, "Page 2", 1);
        return new FormMetadata(
            viewId,
            CaseVariantFormName,
            CaseVariantFormName,
            [first, second],
            [
                Field(viewId, 2, second, "AGE", TextTypeId, "Text", "nvarchar", 8000),
                Field(viewId, 1, first, "Age", NumberTypeId, "Number", "float", 8),
            ]);
    }

    // Synthetic: a text row AGE on no page of the form (pageId null, as a system row has, or a page the form lacks),
    // listed before the one page field Age.
    private static FormMetadata PagelessVariantForm(int? pagelessRowPageId)
    {
        const int viewId = PagelessVariantViewId;
        var page = Page(viewId, SingleAgeFormName, 1, "Page 1", 0);
        return new FormMetadata(
            viewId,
            SingleAgeFormName,
            SingleAgeFormName,
            [page],
            [
                Field(viewId, 2, page, "AGE", TextTypeId, "Text", "nvarchar", 8000) with { PageId = pagelessRowPageId },
                Field(viewId, 1, page, "Age", NumberTypeId, "Number", "float", 8),
            ]);
    }

    // Page ids are <viewId>01 and <viewId>02, and the page table is the base table name plus the page id.
    private static PageMetadata Page(int viewId, string baseTable, int ordinal, string name, int position)
    {
        var pageId = (viewId * 100) + ordinal;
        return new PageMetadata(pageId, name, position, baseTable + pageId.ToString(CultureInfo.InvariantCulture));
    }

    private static FieldMetadata Field(
        int viewId,
        int ordinal,
        PageMetadata page,
        string name,
        int fieldTypeId,
        string fieldTypeName,
        string sqlTypeName,
        int maxLengthBytes) => new()
        {
            FieldId = (viewId * 1000) + ordinal,
            Name = name,
            FieldTypeId = fieldTypeId,
            FieldTypeName = fieldTypeName,
            Prompt = name,
            TabIndex = 1m,
            PageId = page.PageId,
            Column = FieldTypeMapper.DescribeColumn(name, sqlTypeName, maxLengthBytes),
        };
}
