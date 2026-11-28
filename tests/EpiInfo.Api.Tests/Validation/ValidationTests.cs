using System.Data;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
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
using EpiInfo.Domain.Values;
using EpiInfo.Tests.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EpiInfo.Api.Tests.Validation;

/// <summary>
/// SC-6 on the seeded forms: the two-phase validation of <c>POST /api/forms/{viewId}/records</c> and
/// <c>PUT /api/forms/{viewId}/records/{globalRecordId}</c> over HTTP against <c>EpiRehearsal</c>. Every field-level
/// rejection is a 400 keyed by the canonical <c>metaFields.Name</c>, every request-level rejection a 400 with a
/// <c>detail</c> and no <c>errors</c>, and no rejection writes anything; the accepted bounds and shapes are stored
/// exactly as submitted.
/// </summary>
/// <remarks>
/// <para>
/// The cases use seeded metadata only: <c>Zip</c> (<c>MaxLength</c> 5), <c>Age</c> (0 to 120, <c>###</c>),
/// <c>PhoneNumber</c> (<c>###-###-####</c>), <c>LastName</c> (<c>nvarchar(4000)</c>) and the read-only
/// <c>StateIDNumber</c> on <c>CaseReport</c>; <c>FeverTemp</c> (80 to 110, <c>###.#</c>), <c>DateofInterview</c>
/// (2011-01-15 to 2012-12-31), the read-only <c>OnsetWeek</c>, the Legal Values field <c>Sex</c>, the checkboxes
/// <c>White</c> and <c>Asian</c> and the unsupported Yes/No field <c>ILL</c> on <c>FoodHistory</c>. No seeded field
/// is required, so the required gate is proved by <see cref="RequiredGateTests"/>.
/// </para>
/// <para>
/// Each request runs under the exclusive <see cref="DatabaseWriteLock"/> between two <see cref="SqlSnapshot"/>
/// captures: a rejection must leave the five data tables unchanged, and an acceptance must add exactly the new
/// record's base and page rows. A rejection must also send no write command and run no command inside a
/// transaction, as <see cref="SqlCommandRecorder"/> records them, because a write rolled back leaves the snapshots
/// equal. Each body carries a fresh <see cref="TestDatabase.NewMarker"/> in
/// <see cref="TestDatabase.MarkerField"/>, so the tested field alone decides the outcome and a record a defect
/// accepted can be found; the one exception is the body of unchecked checkboxes only, identified by the id its
/// response returns. Created records stay in the database, and no count or view id is hard-coded.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(DatabaseCollections.Database)]
public sealed class ValidationTests : IAsyncLifetime
{
    private const string CaseReport = "CaseReport";
    private const string CaseReportPage = "CaseReport1";
    private const string FoodHistory = "FoodHistory";
    private const string FoodHistoryPageOne = "FoodHistory2";
    private const string FoodHistoryPageTwo = "FoodHistory3";

    private const string GlobalRecordIdColumn = "GlobalRecordId";
    private const string RecStatusColumn = "RecStatus";
    private const string MarkerField = TestDatabase.MarkerField;

    // The character length of the seeded LastName columns, nvarchar(4000) on CaseReport1 and FoodHistory2.
    private const int LastNameColumnLength = 4000;

    private const string IsoDateFormat = "yyyy-MM-dd";

    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(5);

    // The data tables of each seeded form in SqlSnapshot.Tables order, base table first: the rows a create adds.
    private static readonly IReadOnlyDictionary<string, string[]> FormTables =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [CaseReport] = [CaseReport, CaseReportPage],
            [FoodHistory] = [FoodHistory, FoodHistoryPageOne, FoodHistoryPageTwo],
        };

    private readonly Dictionary<string, int> _viewIds = new(StringComparer.Ordinal);

    private ApiFactory? _factory;
    private HttpClient? _client;

    /// <summary>
    /// Field-level rejections of a <c>POST</c> as (form, submitted name, raw JSON value, the one expected
    /// <c>errors</c> key, the exact messages that key holds, in any order). The body also carries a marker in
    /// <c>LastName</c>.
    /// </summary>
    public static TheoryData<string, string, string, string, string[]> FieldLevelPostCases
    {
        get
        {
            string ageRange = ValidationMessages.FormatRangeBetween(0L, 120L);
            string agePattern = ValidationMessages.FormatPatternMismatch("###");
            string feverRange = ValidationMessages.FormatRangeBetween(80L, 110L);
            string feverPattern = ValidationMessages.FormatPatternMismatch("###.#");
            string interviewRange = ValidationMessages.FormatRangeBetween(
                new DateOnly(2011, 1, 15),
                new DateOnly(2012, 12, 31));
            string valuesKey = RecordValidator.ValuesKey;
            string notAField = ValidationMessages.UnknownValuesName;
            string notADataField = ValidationMessages.UnknownField;
            string phoneMask = ValidationMessages.FormatPatternMismatch("###-###-####");
            string zipLength = ValidationMessages.FormatMaxLengthExceeded(5);

            return new TheoryData<string, string, string, string, string[]>
            {
                // MaxLength 5, also under a lower-case key, which the error names by its canonical spelling.
                { CaseReport, "Zip", "\"123456\"", "Zip", [zipLength] },
                { CaseReport, "zip", "\"123456\"", "Zip", [zipLength] },

                // Inclusive range 0 to 120; 1000 is outside it and also has more integer digits than ###, so both
                // messages are required.
                { CaseReport, "Age", "121", "Age", [ageRange] },
                { CaseReport, "Age", "1000", "Age", [ageRange, agePattern] },

                // The phone mask; the last is the shape seeded rows hold, still rejected when it is submitted.
                { CaseReport, "PhoneNumber", "\"5550148636\"", "PhoneNumber", [phoneMask] },
                { CaseReport, "PhoneNumber", "\"555-014-863\"", "PhoneNumber", [phoneMask] },
                { CaseReport, "PhoneNumber", "\"(555) 0148-6367\"", "PhoneNumber", [phoneMask] },

                // A read-only field is rejected by its presence, whatever its value (C-3).
                { CaseReport, "StateIDNumber", "\"X\"", "StateIDNumber", [ValidationMessages.ReadOnlyField] },
                { CaseReport, "StateIDNumber", "null", "StateIDNumber", [ValidationMessages.ReadOnlyField] },
                { FoodHistory, "OnsetWeek", "5", "OnsetWeek", [ValidationMessages.ReadOnlyField] },

                // Inclusive range 80 to 110 (112 is EC-VAL-02). 37.25 is below it and has two decimals, so both
                // messages are required.
                { FoodHistory, "FeverTemp", "112", "FeverTemp", [feverRange] },
                { FoodHistory, "FeverTemp", "79", "FeverTemp", [feverRange] },
                { FoodHistory, "FeverTemp", "37.25", "FeverTemp", [feverRange, feverPattern] },

                // In range with two decimals: the ###.# pattern alone rejects it.
                { FoodHistory, "FeverTemp", "101.25", "FeverTemp", [feverPattern] },

                // 37.5 is below the inclusive seeded range 80 to 110, and the range is its only error: the ###.# mask
                // accepts it, as MaskPatternTests pins on the bare rule.
                { FoodHistory, "FeverTemp", "37.5", "FeverTemp", [feverRange] },

                // Inclusive range 2011-01-15 to 2012-12-31 (2011-01-01 is EC-VAL-01), and a month that does not exist.
                { FoodHistory, "DateofInterview", "\"2011-01-01\"", "DateofInterview", [interviewRange] },
                { FoodHistory, "DateofInterview", "\"2013-01-01\"", "DateofInterview", [interviewRange] },
                {
                    FoodHistory, "DateofInterview", "\"2011-13-01\"", "DateofInterview",
                    [ValidationMessages.DateExpected]
                },

                // Not one of the code table's values (EC-VAL-03).
                { FoodHistory, "Sex", "\"X-Other\"", "Sex", [ValidationMessages.NotALegalValue] },

                // Wire types: a string for a number, and a string or a number for a checkbox.
                { CaseReport, "Age", "\"120\"", "Age", [ValidationMessages.NumberExpected] },
                { FoodHistory, "White", "\"yes\"", "White", [ValidationMessages.BooleanExpected] },
                { FoodHistory, "White", "1", "White", [ValidationMessages.BooleanExpected] },

                // A name with no metaFields row is reported under the fixed values key, so the submitted name never
                // reaches the body; a system column or an unsupported field is a row that holds no value, reported
                // under its canonical name.
                { CaseReport, "NoSuchField", "\"unknown-value\"", valuesKey, [notAField] },
                { CaseReport, "RecStatus", "1", "RecStatus", [notADataField] },
                { CaseReport, "GlobalRecordId", "\"unknown-value\"", "GlobalRecordId", [notADataField] },
                { CaseReport, "UniqueKey", "1", "UniqueKey", [notADataField] },
                { FoodHistory, "ILL", "1", "ILL", [notADataField] },
            };
        }
    }

    /// <summary>
    /// Request-level rejections of a <c>POST</c> as (form, raw JSON body, the expected <c>detail</c>), none of which
    /// may carry <c>errors</c>.
    /// </summary>
    public static TheoryData<string, string, string> RequestLevelPostCases => new()
    {
        // Well-formed JSON that is not a request object: an array, an array holding a request object, a number, a
        // string and a boolean.
        { CaseReport, "[]", ApiProblems.RequestBodyMustBeObjectDetail },
        { CaseReport, """[{"values":{}}]""", ApiProblems.RequestBodyMustBeObjectDetail },
        { CaseReport, "5", ApiProblems.RequestBodyMustBeObjectDetail },
        { CaseReport, "\"x\"", ApiProblems.RequestBodyMustBeObjectDetail },
        { CaseReport, "true", ApiProblems.RequestBodyMustBeObjectDetail },

        // An array that is not valid JSON, whose first token alone would look like a non-object value.
        { CaseReport, "[1,]", ApiProblems.MalformedJsonDetail },

        // values missing, null or not an object.
        { CaseReport, "{}", ValidationMessages.ValuesMustBeObject },
        { CaseReport, """{"values":null}""", ValidationMessages.ValuesMustBeObject },
        { CaseReport, """{"values":[]}""", ValidationMessages.ValuesMustBeObject },
        { CaseReport, """{"values":5}""", ValidationMessages.ValuesMustBeObject },

        // Every value null once empty text is null, absent fields counting as null (C-10).
        { CaseReport, """{"values":{"LastName":null}}""", ValidationMessages.RecordHasNoValues },
        { CaseReport, """{"values":{"LastName":""}}""", ValidationMessages.RecordHasNoValues },
        { CaseReport, """{"values":{}}""", ValidationMessages.RecordHasNoValues },
        { FoodHistory, """{"values":{"White":null,"LastName":null}}""", ValidationMessages.RecordHasNoValues },
    };

    /// <summary>
    /// Accepted numbers as (form, field, value): the inclusive bounds and a value that fits its pattern.
    /// </summary>
    public static TheoryData<string, string, double> AcceptedNumberCases => new()
    {
        { CaseReport, "Age", 120 },
        { CaseReport, "Age", 0 },

        // In range with one decimal: ###.# accepts it on the seeded metadata.
        { FoodHistory, "FeverTemp", 101.5 },
        { FoodHistory, "FeverTemp", 110 },
    };

    /// <summary>Accepted texts as (form, field, value): the phone mask, <c>MaxLength</c> and a legal value.</summary>
    public static TheoryData<string, string, string> AcceptedTextCases => new()
    {
        { CaseReport, "PhoneNumber", "555-014-8636" },
        { CaseReport, "Zip", "12345" },
        { FoodHistory, "Sex", "F-Female" },
    };

    private HttpClient Client =>
        _client ?? throw new InvalidOperationException("The API client is created in InitializeAsync.");

    // Every SQL command the host's data store sends, the proof that a rejection reached no write.
    private SqlCommandRecorder Recorder =>
        _factory?.Recorder ?? throw new InvalidOperationException("The API host is created in InitializeAsync.");

    /// <summary>
    /// Starts the API host on the rehearsal database and resolves both form ids by name. A database that is not
    /// configured or not reachable fails every test with the <see cref="TestDatabase"/> message; nothing is skipped.
    /// </summary>
    public async Task InitializeAsync()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        _factory = await ApiFactory.ForDatabaseAsync();
        _client = _factory.CreateClient();
        foreach (string form in FormTables.Keys)
        {
            _viewIds[form] = await ApiTestClient.GetViewIdAsync(_client, form, timeout.Token);
        }
    }

    /// <summary>Disposes the client and stops the host.</summary>
    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
    }

    /// <summary>
    /// A value that fails one input check is a field-level 400 under the canonical name alone, whose messages echo
    /// neither the value nor the marker beside it, and nothing is written.
    /// </summary>
    [Theory]
    [MemberData(nameof(FieldLevelPostCases))]
    public async Task FieldLevelRejectionOfAPostIsKeyedByCanonicalNameAndWritesNothing(
        string form,
        string submittedName,
        string rawValue,
        string expectedKey,
        string[] expectedMessages)
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        string marker = TestDatabase.NewMarker();
        string body = $$$"""{"values":{"{{{submittedName}}}":{{{rawValue}}},"{{{MarkerField}}}":"{{{marker}}}"}}""";

        var (problem, text) = await AssertRejectedWithoutWriteAsync(form, Post(form, JsonBody(body)), timeout.Token);

        AssertFieldLevel(problem, expectedKey, expectedMessages, SubmittedText(rawValue));
        Assert.DoesNotContain(marker, text, StringComparison.Ordinal);
        if (!string.Equals(submittedName, expectedKey, StringComparison.Ordinal))
        {
            Assert.DoesNotContain(submittedName, text, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Text longer than the <c>nvarchar(4000)</c> column holds is rejected by the storage-range check, never by a
    /// SQL exception.
    /// </summary>
    [Fact]
    public async Task TextLongerThanItsColumnIsAFieldLevelRejection()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        string marker = TestDatabase.NewMarker();
        string value = Pad(marker, LastNameColumnLength + 1);
        string body = $$$"""{"values":{"{{{MarkerField}}}":"{{{value}}}"}}""";

        var (problem, text) = await AssertRejectedWithoutWriteAsync(
            CaseReport,
            Post(CaseReport, JsonBody(body)),
            timeout.Token);

        AssertFieldLevel(
            problem,
            MarkerField,
            [ValidationMessages.FormatTextTooLongForColumn(LastNameColumnLength)],
            value);
        Assert.DoesNotContain(marker, text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two keys that differ only by letter case name one field twice: one error under its canonical name.
    /// </summary>
    [Fact]
    public async Task CaseVariantRepeatIsRejectedUnderTheCanonicalName()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        string marker = TestDatabase.NewMarker();
        string body = $$$"""{"values":{"LastName":"{{{marker}}}","lastname":"{{{marker}}}"}}""";

        var (problem, text) = await AssertRejectedWithoutWriteAsync(
            CaseReport,
            Post(CaseReport, JsonBody(body)),
            timeout.Token);

        AssertFieldLevel(problem, MarkerField, [ValidationMessages.DuplicateField], marker);
        Assert.DoesNotContain(marker, text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A rejected <c>PUT</c> writes nothing, base-row stamps included: an out-of-range value, a read-only field and a
    /// <c>values</c> that is not an object, each against a record this test created.
    /// </summary>
    [Fact]
    public async Task RejectedPutsLeaveTheRecordAndItsStampsUnchanged()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        CancellationToken ct = timeout.Token;
        await using DatabaseWriteLock dbLock = await DatabaseWriteLock.AcquireExclusiveAsync(ct);

        string marker = TestDatabase.NewMarker();
        CreatedRecord created = await ApiTestClient.CreateRecordAsync(
            Client,
            ViewId(CaseReport),
            new Dictionary<string, object?> { [MarkerField] = marker },
            ct);

        var (outOfRange, _) = await SendRejectedWithoutWriteAsync(
            CaseReport,
            Put(CaseReport, created.GlobalRecordId, JsonBody("""{"values":{"Age":121}}""")),
            ct);
        AssertFieldLevel(outOfRange, "Age", [ValidationMessages.FormatRangeBetween(0L, 120L)], "121");

        var (readOnly, _) = await SendRejectedWithoutWriteAsync(
            CaseReport,
            Put(CaseReport, created.GlobalRecordId, JsonBody("""{"values":{"StateIDNumber":"X"}}""")),
            ct);
        AssertFieldLevel(readOnly, "StateIDNumber", [ValidationMessages.ReadOnlyField], "X");

        var (notAnObject, _) = await SendRejectedWithoutWriteAsync(
            CaseReport,
            Put(CaseReport, created.GlobalRecordId, JsonBody("""{"values":null}""")),
            ct);
        AssertRequestLevel(notAnObject, ValidationMessages.ValuesMustBeObject);
    }

    /// <summary>
    /// A body that is not a request object with a <c>values</c> object, or whose values are all <c>null</c>, is a
    /// request-level 400 that names the problem in <c>detail</c> and carries no <c>errors</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(RequestLevelPostCases))]
    public async Task RequestLevelRejectionOfAPostHasADetailAndNoErrors(string form, string body, string expectedDetail)
    {
        using var timeout = new CancellationTokenSource(TestTimeout);

        var (problem, _) = await AssertRejectedWithoutWriteAsync(form, Post(form, JsonBody(body)), timeout.Token);

        AssertRequestLevel(problem, expectedDetail);
    }

    /// <summary>
    /// An empty <c>application/json</c> body is the request-level "request body is required". A request with no
    /// content carries no <c>Content-Type</c> and is a 415, which <c>MediaTypeTests</c> owns.
    /// </summary>
    [Fact]
    public async Task EmptyJsonBodyIsRequestBodyRequired()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        var content = new ByteArrayContent([]);
        content.Headers.ContentType = new MediaTypeHeaderValue(ApiTestClient.JsonMediaType);

        var (problem, _) = await AssertRejectedWithoutWriteAsync(CaseReport, Post(CaseReport, content), timeout.Token);

        AssertRequestLevel(problem, ApiProblems.RequestBodyRequiredDetail);
    }

    /// <summary>An inclusive bound and a value that fits its pattern are stored exactly, as <c>float</c>.</summary>
    [Theory]
    [MemberData(nameof(AcceptedNumberCases))]
    public async Task AcceptedNumberIsStoredAsSubmitted(string form, string field, double value)
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        CancellationToken ct = timeout.Token;
        await using DatabaseWriteLock dbLock = await DatabaseWriteLock.AcquireExclusiveAsync(ct);
        string marker = TestDatabase.NewMarker();

        CreatedRecord created = await CreateAddingOnlyTheRecordAsync(
            form,
            new Dictionary<string, object?> { [field] = value, [MarkerField] = marker },
            ct);

        Assert.Equal(value, ResponseValue(created, field).GetDouble());
        var stored = await ReadPageOneAsync(form, created.GlobalRecordId, [field, MarkerField], ct);
        Assert.Equal(value, Assert.IsType<double>(stored[field]));
        Assert.Equal(marker, stored[MarkerField]);
    }

    /// <summary>Text that fits its mask, <c>MaxLength</c> or legal values is stored exactly.</summary>
    [Theory]
    [MemberData(nameof(AcceptedTextCases))]
    public async Task AcceptedTextIsStoredAsSubmitted(string form, string field, string value)
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        CancellationToken ct = timeout.Token;
        await using DatabaseWriteLock dbLock = await DatabaseWriteLock.AcquireExclusiveAsync(ct);
        string marker = TestDatabase.NewMarker();

        CreatedRecord created = await CreateAddingOnlyTheRecordAsync(
            form,
            new Dictionary<string, object?> { [field] = value, [MarkerField] = marker },
            ct);

        Assert.Equal(value, ResponseValue(created, field).GetString());
        var stored = await ReadPageOneAsync(form, created.GlobalRecordId, [field, MarkerField], ct);
        Assert.Equal(value, stored[field]);
        Assert.Equal(marker, stored[MarkerField]);
    }

    /// <summary>
    /// The inclusive lower bound of <c>DateofInterview</c> is returned as <c>YYYY-MM-DD</c> and stored at midnight
    /// (C-15).
    /// </summary>
    [Fact]
    public async Task AcceptedDateIsStoredAtMidnight()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        CancellationToken ct = timeout.Token;
        await using DatabaseWriteLock dbLock = await DatabaseWriteLock.AcquireExclusiveAsync(ct);
        string marker = TestDatabase.NewMarker();
        var lowerBound = new DateOnly(2011, 1, 15);
        const string Field = "DateofInterview";

        CreatedRecord created = await CreateAddingOnlyTheRecordAsync(
            FoodHistory,
            new Dictionary<string, object?>
            {
                [Field] = lowerBound.ToString(IsoDateFormat, CultureInfo.InvariantCulture),
                [MarkerField] = marker,
            },
            ct);

        Assert.Equal(
            lowerBound.ToString(IsoDateFormat, CultureInfo.InvariantCulture),
            ResponseValue(created, Field).GetString());
        var stored = await ReadPageOneAsync(FoodHistory, created.GlobalRecordId, [Field, MarkerField], ct);
        Assert.Equal(lowerBound.ToDateTime(TimeOnly.MinValue), Assert.IsType<DateTime>(stored[Field]));
        Assert.Equal(marker, stored[MarkerField]);
    }

    /// <summary>Text of exactly the column's length is accepted and stored verbatim.</summary>
    [Fact]
    public async Task TextOfExactlyItsColumnLengthIsStoredVerbatim()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        CancellationToken ct = timeout.Token;
        await using DatabaseWriteLock dbLock = await DatabaseWriteLock.AcquireExclusiveAsync(ct);
        string value = Pad(TestDatabase.NewMarker(), LastNameColumnLength);

        CreatedRecord created = await CreateAddingOnlyTheRecordAsync(
            CaseReport,
            new Dictionary<string, object?> { [MarkerField] = value },
            ct);

        Assert.Equal(value, ResponseValue(created, MarkerField).GetString());
        var stored = await ReadPageOneAsync(CaseReport, created.GlobalRecordId, [MarkerField], ct);
        string storedText = Assert.IsType<string>(stored[MarkerField]);
        Assert.Equal(LastNameColumnLength, storedText.Length);
        Assert.Equal(value, storedText);
    }

    /// <summary>A key matched without regard to letter case is stored and returned under the canonical name.</summary>
    [Fact]
    public async Task CaseInsensitiveKeyIsAcceptedUnderTheCanonicalName()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        CancellationToken ct = timeout.Token;
        await using DatabaseWriteLock dbLock = await DatabaseWriteLock.AcquireExclusiveAsync(ct);
        string marker = TestDatabase.NewMarker();

        CreatedRecord created = await CreateAddingOnlyTheRecordAsync(
            CaseReport,
            new Dictionary<string, object?> { ["lastname"] = marker },
            ct);

        Assert.Equal(marker, ResponseValue(created, MarkerField).GetString());
        Assert.DoesNotContain(
            created.Record.GetProperty("values").EnumerateObject(),
            property => string.Equals(property.Name, "lastname", StringComparison.Ordinal));
        var stored = await ReadPageOneAsync(CaseReport, created.GlobalRecordId, [MarkerField], ct);
        Assert.Equal(marker, stored[MarkerField]);
    }

    /// <summary>
    /// An explicit <c>false</c> is a value, so a body of unchecked checkboxes only creates a record whose
    /// <c>bit</c> columns hold 0 (C-10). It holds no marker, so it is identified by the id its response returns.
    /// </summary>
    [Fact]
    public async Task OnlyUncheckedCheckboxesCreateARecord()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        CancellationToken ct = timeout.Token;
        await using DatabaseWriteLock dbLock = await DatabaseWriteLock.AcquireExclusiveAsync(ct);

        CreatedRecord created = await CreateAddingOnlyTheRecordAsync(
            FoodHistory,
            new Dictionary<string, object?> { ["White"] = false, ["Asian"] = false },
            ct);

        Assert.False(ResponseValue(created, "White").GetBoolean());
        Assert.False(ResponseValue(created, "Asian").GetBoolean());
        var stored = await ReadPageOneAsync(FoodHistory, created.GlobalRecordId, ["White", "Asian"], ct);
        Assert.False(Assert.IsType<bool>(stored["White"]));
        Assert.False(Assert.IsType<bool>(stored["Asian"]));
        var baseRow = await ReadStoredRowAsync(FoodHistory, created.GlobalRecordId, [RecStatusColumn], ct);
        Assert.Equal(1, Convert.ToInt32(baseRow[RecStatusColumn], CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// An unchecked checkbox beside an entered value creates a record holding both, and a checkbox the body leaves
    /// out is stored as <c>NULL</c> (C-10).
    /// </summary>
    [Fact]
    public async Task UncheckedCheckboxBesideAnEnteredValueCreatesARecord()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        CancellationToken ct = timeout.Token;
        await using DatabaseWriteLock dbLock = await DatabaseWriteLock.AcquireExclusiveAsync(ct);
        string marker = TestDatabase.NewMarker();

        CreatedRecord created = await CreateAddingOnlyTheRecordAsync(
            FoodHistory,
            new Dictionary<string, object?> { ["White"] = false, [MarkerField] = marker },
            ct);

        var stored = await ReadPageOneAsync(FoodHistory, created.GlobalRecordId, ["White", "Asian", MarkerField], ct);
        Assert.False(Assert.IsType<bool>(stored["White"]));
        Assert.Null(stored["Asian"]);
        Assert.Equal(marker, stored[MarkerField]);
    }

    // The text a raw JSON value submits, for checking that no error message echoes it: a string's content or a
    // number's literal; null when the value is the JSON null and so submits no text.
    private static string? SubmittedText(string rawValue)
    {
        using JsonDocument document = JsonDocument.Parse(rawValue);
        return document.RootElement.ValueKind switch
        {
            JsonValueKind.String => document.RootElement.GetString(),
            JsonValueKind.Null => null,
            _ => document.RootElement.GetRawText(),
        };
    }

    // The marker followed by enough 'x' characters to make the text exactly length characters long.
    private static string Pad(string marker, int length) => marker + new string('x', length - marker.Length);

    private static StringContent JsonBody(string json) => new(json, Encoding.UTF8, ApiTestClient.JsonMediaType);

    // The form's page-one table, which holds every field these cases read back.
    private static string PageOneTable(string form) => FormTables[form][1];

    // Asserts a field-level 400: the fixed detail, an errors object whose only key is expectedKey (ordinal), exactly
    // expectedMessages under it in any order, so no further message passes unseen, and no message echoing the
    // submitted text.
    private static void AssertFieldLevel(
        JsonElement problem,
        string expectedKey,
        string[] expectedMessages,
        string? submittedText)
    {
        Assert.NotEmpty(expectedMessages);
        Assert.Equal(ApiProblems.ValidationFailedDetail, problem.GetProperty("detail").GetString());
        Assert.True(
            problem.TryGetProperty("errors", out JsonElement errors) && errors.ValueKind == JsonValueKind.Object,
            "A field-level 400 carries an errors object.");
        Assert.Equal(new[] { expectedKey }, errors.EnumerateObject().Select(entry => entry.Name).ToArray());

        string?[] messages =
            [.. errors.GetProperty(expectedKey).EnumerateArray().Select(message => message.GetString())];
        Assert.Equal<string?>(
            expectedMessages.Order(StringComparer.Ordinal),
            messages.Order(StringComparer.Ordinal));
        if (!string.IsNullOrEmpty(submittedText))
        {
            Assert.All(messages, message => Assert.DoesNotContain(submittedText, message!, StringComparison.Ordinal));
        }
    }

    // Asserts a request-level 400: the expected detail and no errors member at all.
    private static void AssertRequestLevel(JsonElement problem, string expectedDetail)
    {
        Assert.Equal(expectedDetail, problem.GetProperty("detail").GetString());
        Assert.False(problem.TryGetProperty("errors", out _), "A request-level 400 carries no errors member.");
    }

    // The value the 201 Record DTO returns under the canonical name, after checking the record is active.
    private static JsonElement ResponseValue(CreatedRecord created, string canonicalName)
    {
        Assert.Equal(1, created.Record.GetProperty("recStatus").GetInt32());
        Assert.True(
            created.Record.GetProperty("values").TryGetProperty(canonicalName, out JsonElement value),
            $"The response values hold no entry named {canonicalName}.");
        return value;
    }

    // Requires that a create added exactly one row with the record's id to each data table of its form, and that no
    // other row or column changed.
    private static void AssertOnlyTheRecordWasAdded(string form, string globalRecordId, SnapshotDiff diff)
    {
        Assert.True(
            diff.RemovedRows.Count == 0 && diff.ChangedCells.Count == 0 && diff.SchemaChanges.Count == 0,
            diff.ToString());
        Assert.All(diff.AddedRows, row => Assert.Equal(globalRecordId, row.GlobalRecordId));
        Assert.Equal(FormTables[form], diff.AddedRows.Select(row => row.Table));
    }

    // Reads one row of a data table with a parameterised SELECT. The table and column names are this class's own
    // literals, bracket-quoted; the record id is the only value and is a parameter.
    private static async Task<IReadOnlyDictionary<string, object?>> ReadStoredRowAsync(
        string table,
        string globalRecordId,
        string[] columns,
        CancellationToken ct)
    {
        await using SqlConnection connection = await TestDatabase.OpenConnectionAsync(ct);
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = string.Create(
            CultureInfo.InvariantCulture,
            $"SELECT {string.Join(", ", columns.Select(Quote))} FROM [dbo].{Quote(table)} " +
            $"WHERE {Quote(GlobalRecordIdColumn)} = @id;");
        command.Parameters.Add(new SqlParameter("@id", SqlDbType.NVarChar, 255) { Value = globalRecordId });

        await using SqlDataReader reader = await command.ExecuteReaderAsync(ct);
        Assert.True(await reader.ReadAsync(ct), $"{table} holds no row for the created record.");
        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (int ordinal = 0; ordinal < reader.FieldCount; ordinal++)
        {
            row[reader.GetName(ordinal)] = await reader.IsDBNullAsync(ordinal, ct) ? null : reader.GetValue(ordinal);
        }

        Assert.False(await reader.ReadAsync(ct), $"{table} holds more than one row for the created record.");
        return row;
    }

    private static Task<IReadOnlyDictionary<string, object?>> ReadPageOneAsync(
        string form,
        string globalRecordId,
        string[] columns,
        CancellationToken ct) =>
        ReadStoredRowAsync(PageOneTable(form), globalRecordId, columns, ct);

    private static string Quote(string identifier) =>
        "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";

    private int ViewId(string form) => _viewIds[form];

    private string RecordsPath(string form) =>
        string.Create(CultureInfo.InvariantCulture, $"/api/forms/{ViewId(form)}/records");

    private HttpRequestMessage Post(string form, HttpContent content) =>
        new(HttpMethod.Post, RecordsPath(form)) { Content = content };

    private HttpRequestMessage Put(string form, string globalRecordId, HttpContent content) =>
        new(HttpMethod.Put, RecordsPath(form) + "/" + Uri.EscapeDataString(globalRecordId)) { Content = content };

    // Takes the exclusive lock and sends one rejected request between two snapshots, with the SQL recorder cleared
    // just before it, as SendRejectedWithoutWriteAsync.
    private async Task<(JsonElement Problem, string Text)> AssertRejectedWithoutWriteAsync(
        string form,
        HttpRequestMessage request,
        CancellationToken ct)
    {
        using HttpRequestMessage owned = request;
        await using DatabaseWriteLock dbLock = await DatabaseWriteLock.AcquireExclusiveAsync(ct);
        return await SendRejectedWithoutWriteAsync(form, owned, ct);
    }

    // Under the caller's exclusive lock: captures a snapshot, clears the host's SQL recorder, sends the request and
    // captures again. Requires a 400 problem body (application/problem+json, status 400, title, traceId), no recorded
    // INSERT, UPDATE, DELETE or MERGE and no recorded command inside a transaction, an empty snapshot diff and an
    // unchanged row count of the form's base table. A write that ran and was rolled back leaves the snapshot unchanged,
    // so the recorder is what shows the rejection came before the store's write transaction. Returns the parsed body
    // and its text. Disposes the request.
    private async Task<(JsonElement Problem, string Text)> SendRejectedWithoutWriteAsync(
        string form,
        HttpRequestMessage request,
        CancellationToken ct)
    {
        using HttpRequestMessage sent = request;
        SqlSnapshot before = await SqlSnapshot.CaptureAsync(ct);

        Recorder.Clear();
        using HttpResponseMessage response = await Client.SendAsync(sent, ct);
        string text = await response.Content.ReadAsStringAsync(ct);

        SqlSnapshot after = await SqlSnapshot.CaptureAsync(ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiProblems.ProblemContentType, response.Content.Headers.ContentType?.MediaType);

        // ToString lists each command without its parameter values, so no submitted value reaches the test output.
        IReadOnlyList<RecordedCommand> writes = Recorder.Writes();
        Assert.True(
            writes.Count == 0,
            $"The rejected request sent {writes.Count} write command(s): {string.Join("; ", writes)}");
        IReadOnlyList<RecordedCommand> inTransaction = [.. Recorder.Commands.Where(command => command.InTransaction)];
        Assert.True(
            inTransaction.Count == 0,
            $"The rejected request ran {inTransaction.Count} command(s) inside a transaction: " +
            string.Join("; ", inTransaction));

        SnapshotDiff diff = SqlSnapshot.Compare(before, after);
        Assert.True(diff.IsEmpty, diff.ToString());
        string baseTable = FormTables[form][0];
        Assert.Equal(before.RowCount(baseTable), after.RowCount(baseTable));

        using JsonDocument document = JsonDocument.Parse(text);
        JsonElement problem = ApiTestClient.AssertProblemDetails(
            document.RootElement.Clone(),
            StatusCodes.Status400BadRequest);
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("title").GetString()), "The problem has no title.");
        return (problem, text);
    }

    // Under the caller's exclusive lock: creates a record through the API between two snapshots, which must differ
    // only by the record's added base and page rows. CreateRecordAsync checks the 201 and its Location header.
    private async Task<CreatedRecord> CreateAddingOnlyTheRecordAsync(
        string form,
        IDictionary<string, object?> values,
        CancellationToken ct)
    {
        SqlSnapshot before = await SqlSnapshot.CaptureAsync(ct);
        CreatedRecord created = await ApiTestClient.CreateRecordAsync(Client, ViewId(form), values, ct);
        SqlSnapshot after = await SqlSnapshot.CaptureAsync(ct);

        AssertOnlyTheRecordWasAdded(form, created.GlobalRecordId, SqlSnapshot.Compare(before, after));
        return created;
    }
}

/// <summary>
/// The validation cases that need no database, over <see cref="FakeDataStore"/>: the required gate of
/// <c>POST</c> and <c>PUT</c> with its legacy <c>FIELD_IS_REQUIRED_LONG</c> wording (C-12), and the values reader's
/// rejection of entries that are not scalar values of data fields.
/// </summary>
/// <remarks>
/// <para>
/// No seeded field has <c>IsRequired = 1</c>, so the gate runs on <see cref="FakeForms.WithRequiredField"/> with a
/// second required field added on a second page, through the API pipeline of <see cref="ApiFactory"/>. A
/// <c>POST</c> is gated before any write; a <c>PUT</c> is gated inside the store's update, after its locked read, on
/// the stored values overlaid with the submitted ones, and a failed gate leaves no data-changing call in
/// <see cref="FakeDataStore.WriteCalls"/>.
/// </para>
/// <para>
/// The values-reader cases run on <see cref="FakeForms.TwoPage"/> in an endpoint host of their own. They share this
/// class rather than <see cref="ValidationTests"/>, whose cases need the rehearsal database, so they keep running
/// without one; the class therefore carries no <c>Integration</c> trait.
/// </para>
/// </remarks>
public sealed class RequiredGateTests : IAsyncLifetime
{
    // FIELD_IS_REQUIRED_LONG of Epi.Core/SharedStrings.resx line 638, typed out here rather than taken from
    // ValidationMessages so the wording is pinned independently of production code; note the trailing space.
    private const string RequiredMessageFormat =
        "{0} is a required field (page {1}). Please fill in this field before attempting to leave the record, save data, or close the form. ";

    // The second required field this class adds; its prompt differs from its name, so {0} is shown to be the prompt.
    private const string SecondRequiredName = "Town";
    private const string SecondRequiredPrompt = "Town of residence";
    private const string SecondPageName = "Page 2";

    private const string OptionalValue = "note";
    private const string FirstRequiredValue = "Smith";
    private const string SecondRequiredValue = "Springfield";

    // The values-reader cases: a text no error body may echo, the two write methods, and the two-page form they use.
    private const string Sentinel = "SENTINEL-PII-4711";

    private const string Post = "POST";

    private const string Put = "PUT";

    private const int TwoPageViewId = FakeForms.TwoPageViewId;

    private static readonly TimeSpan RequiredGateTimeout = TimeSpan.FromSeconds(60);

    private readonly FakeForm _requiredForm;
    private readonly FakeDataStore _requiredStore;
    private readonly ApiFactory _factory;
    private HttpClient? _client;

    /// <summary>
    /// Creates a fresh store holding the two-required-field form and a host over it, per test. The host starts on the
    /// first request of a required-gate case.
    /// </summary>
    public RequiredGateTests()
    {
        _requiredForm = CreateTwoRequiredFieldForm();
        _requiredStore = new FakeDataStore();
        _requiredStore.AddForm(_requiredForm);
        _factory = new ApiFactory(ApiFactoryOptions.ForFake(_requiredStore));
    }

    private FormMetadata RequiredForm => _requiredForm.Metadata;

    private int RequiredViewId => RequiredForm.ViewId;

    // The required fields in metadata order: the one WithRequiredField declares, then the one this class adds.
    private FieldMetadata FirstRequired => RequiredForm.Fields.First(row => row.IsRequired);

    private FieldMetadata SecondRequired => RequiredForm.Fields.Last(row => row.IsRequired);

    // The one page-bound field that is not required.
    private FieldMetadata Optional =>
        Assert.Single(RequiredForm.Fields, row => row.PageId is not null && !row.IsRequired);

    private string RecordsPath => string.Create(CultureInfo.InvariantCulture, $"/api/forms/{RequiredViewId}/records");

    private HttpClient Client => _client ??= _factory.CreateClient();

    /// <summary>Nothing to start: the required-gate host starts on its first request.</summary>
    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>Disposes the client and stops the host.</summary>
    public async Task DisposeAsync()
    {
        _client?.Dispose();
        await _factory.DisposeAsync();
    }

    /// <summary>
    /// The message literal of this class is the legacy text the validator formats, so a drift in either is caught.
    /// </summary>
    [Fact]
    public void RequiredMessageLiteralIsTheValidatorsFormat()
    {
        Assert.Equal(RequiredMessageFormat, ValidationMessages.FieldIsRequiredLong);
        Assert.Equal(2, RequiredForm.Fields.Count(field => field.IsRequired));
        Assert.NotEqual(FirstRequired.PageId, SecondRequired.PageId);
    }

    /// <summary>
    /// A create without the required field, beside a value for a field that is not required, is a 400 under the
    /// required field's name with the legacy message, and nothing reaches the store's write methods.
    /// </summary>
    [Fact]
    public async Task PostWithoutARequiredFieldIsRejectedWithTheLegacyMessage()
    {
        using var timeout = new CancellationTokenSource(RequiredGateTimeout);

        var values = new Dictionary<string, object?>
        {
            [Optional.Name] = OptionalValue,
            [SecondRequired.Name] = SecondRequiredValue,
        };

        await AssertPostRejectedAsync(values, [FirstRequired], timeout.Token);
    }

    /// <summary>
    /// A required field sent as <c>null</c>, or as empty text that is normalised to <c>null</c>, is missing as well.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task PostWithARequiredFieldNullOrEmptyIsRejected(string? submitted)
    {
        using var timeout = new CancellationTokenSource(RequiredGateTimeout);

        var values = new Dictionary<string, object?>
        {
            [Optional.Name] = OptionalValue,
            [FirstRequired.Name] = submitted,
            [SecondRequired.Name] = SecondRequiredValue,
        };

        await AssertPostRejectedAsync(values, [FirstRequired], timeout.Token);
    }

    /// <summary>
    /// Every missing required field is listed, each with its own prompt and page name; legacy stopped at the first.
    /// </summary>
    [Fact]
    public async Task PostMissingEveryRequiredFieldListsEachOne()
    {
        using var timeout = new CancellationTokenSource(RequiredGateTimeout);

        var values = new Dictionary<string, object?> { [Optional.Name] = OptionalValue };

        await AssertPostRejectedAsync(values, [FirstRequired, SecondRequired], timeout.Token);
    }

    /// <summary>A create that supplies every required field passes the gate and is inserted once.</summary>
    [Fact]
    public async Task PostWithEveryRequiredFieldIsCreated()
    {
        using var timeout = new CancellationTokenSource(RequiredGateTimeout);

        CreatedRecord created = await ApiTestClient.CreateRecordAsync(
            Client,
            RequiredViewId,
            new Dictionary<string, object?>
            {
                [FirstRequired.Name] = FirstRequiredValue,
                [SecondRequired.Name] = SecondRequiredValue,
            },
            timeout.Token);

        Assert.Equal(
            new[] { new FakeStoreCall(nameof(IDataStore.InsertAsync), IsWrite: true) },
            _requiredStore.WriteCalls);
        FakeRecordState? stored = _requiredStore.GetRecord(RequiredViewId, created.GlobalRecordId);
        Assert.NotNull(stored);
        Assert.Equal(FieldValue.FromText(FirstRequiredValue), stored.Values[FirstRequired.Name]);
        Assert.Equal(FieldValue.FromText(SecondRequiredValue), stored.Values[SecondRequired.Name]);
    }

    /// <summary>
    /// A <c>PUT</c> that leaves a required field empty in the stored record fails the gate on the overlay, inside the
    /// store's update: a 400 with no data-changing call, and the record keeps its values and stamps.
    /// </summary>
    [Fact]
    public async Task PutWhoseOverlayLacksAStoredRequiredValueIsRejected()
    {
        using var timeout = new CancellationTokenSource(RequiredGateTimeout);
        string id = _requiredStore.SeedRecord(
            RequiredViewId,
            new Dictionary<string, FieldValue> { [SecondRequired.Name] = FieldValue.FromText(SecondRequiredValue) });

        await AssertPutRejectedAsync(
            id,
            new Dictionary<string, object?> { [Optional.Name] = OptionalValue },
            FirstRequired,
            timeout.Token);
    }

    /// <summary>A <c>PUT</c> that clears a stored required value fails the gate the same way.</summary>
    [Fact]
    public async Task PutClearingAStoredRequiredValueIsRejected()
    {
        using var timeout = new CancellationTokenSource(RequiredGateTimeout);
        string id = SeedCompleteRecord();

        await AssertPutRejectedAsync(
            id,
            new Dictionary<string, object?> { [FirstRequired.Name] = null },
            FirstRequired,
            timeout.Token);
    }

    /// <summary>
    /// The gate runs on the effective record: a delta touching only a field that is not required passes on a record
    /// whose required values are stored, and a delta supplying the one missing required value passes too.
    /// </summary>
    [Fact]
    public async Task PutWhoseOverlaySatisfiesTheGateIsSaved()
    {
        using var timeout = new CancellationTokenSource(RequiredGateTimeout);
        CancellationToken ct = timeout.Token;
        string complete = SeedCompleteRecord();
        string incomplete = _requiredStore.SeedRecord(
            RequiredViewId,
            new Dictionary<string, FieldValue> { [SecondRequired.Name] = FieldValue.FromText(SecondRequiredValue) });

        await AssertPutSavedAsync(complete, Optional.Name, OptionalValue, ct);
        await AssertPutSavedAsync(incomplete, FirstRequired.Name, FirstRequiredValue, ct);

        Assert.Equal(
            new[]
            {
                new FakeStoreCall(nameof(IDataStore.UpdateAsync), IsWrite: true),
                new FakeStoreCall(nameof(IDataStore.UpdateAsync), IsWrite: true),
            },
            _requiredStore.WriteCalls);
    }

    // WithRequiredField declares one required field, and the "every missing field" rule needs two on different pages,
    // so a second required text field is added on a second page, built from the first one's rows. The page follows
    // FakeForms' naming: page id <viewId>02, page table the base table plus that id.
    private static FakeForm CreateTwoRequiredFieldForm()
    {
        FakeForm source = FakeForms.WithRequiredField;
        FormMetadata metadata = source.Metadata;
        PageMetadata firstPage = Assert.Single(metadata.Pages);
        FieldMetadata firstRequired = Assert.Single(metadata.Fields, field => field.IsRequired);
        ColumnMetadata? firstColumn = firstRequired.Column;
        Assert.NotNull(firstColumn);

        int pageId = firstPage.PageId + 1;
        var secondPage = new PageMetadata(
            pageId,
            SecondPageName,
            firstPage.Position + 1,
            metadata.BaseTableName + pageId.ToString(CultureInfo.InvariantCulture));
        FieldMetadata secondRequired = firstRequired with
        {
            FieldId = metadata.Fields.Max(field => field.FieldId) + 1,
            Name = SecondRequiredName,
            Prompt = SecondRequiredPrompt,
            PageId = pageId,
            TabIndex = 1m,
            Column = firstColumn with { Name = SecondRequiredName },
        };

        return source with
        {
            Metadata = metadata with
            {
                Pages = [firstPage, secondPage],
                Fields = [.. metadata.Fields, secondRequired],
            },
        };
    }

    // The legacy message for one missing field: its prompt and the name of its page.
    private string ExpectedRequiredMessage(FieldMetadata field)
    {
        PageMetadata page = Assert.Single(RequiredForm.Pages, candidate => candidate.PageId == field.PageId);
        return string.Format(CultureInfo.InvariantCulture, RequiredMessageFormat, field.Prompt, page.Name);
    }

    private string SeedCompleteRecord() => _requiredStore.SeedRecord(
        RequiredViewId,
        new Dictionary<string, FieldValue>
        {
            [FirstRequired.Name] = FieldValue.FromText(FirstRequiredValue),
            [SecondRequired.Name] = FieldValue.FromText(SecondRequiredValue),
        });

    // Requires a 400 whose errors hold exactly the missing fields, each with exactly its legacy message (ordinal).
    private async Task AssertRequiredErrorsAsync(
        HttpResponseMessage response,
        FieldMetadata[] missing,
        CancellationToken ct)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        JsonElement problem = ApiTestClient.AssertProblemDetails(
            await ApiTestClient.ReadJsonElementAsync(response, ct),
            StatusCodes.Status400BadRequest);
        Assert.Equal(ApiProblems.ValidationFailedDetail, problem.GetProperty("detail").GetString());
        Assert.True(
            problem.TryGetProperty("errors", out JsonElement errors) && errors.ValueKind == JsonValueKind.Object,
            "A field-level 400 carries an errors object.");

        Assert.Equal(
            missing.Select(field => field.Name).Order(StringComparer.Ordinal),
            errors.EnumerateObject().Select(entry => entry.Name).Order(StringComparer.Ordinal));
        foreach (FieldMetadata field in missing)
        {
            Assert.Equal(
                new string?[] { ExpectedRequiredMessage(field) },
                errors.GetProperty(field.Name).EnumerateArray().Select(message => message.GetString()));
        }
    }

    private async Task AssertPostRejectedAsync(
        Dictionary<string, object?> values,
        FieldMetadata[] missing,
        CancellationToken ct)
    {
        using HttpResponseMessage response = await ApiTestClient.PostJsonAsync(Client, RecordsPath, new { values }, ct);

        await AssertRequiredErrorsAsync(response, missing, ct);
        Assert.Empty(_requiredStore.WriteCalls);
        Assert.Empty(_requiredStore.GetRecords(RequiredViewId));
    }

    private async Task AssertPutRejectedAsync(
        string globalRecordId,
        Dictionary<string, object?> values,
        FieldMetadata missing,
        CancellationToken ct)
    {
        FakeRecordState? before = _requiredStore.GetRecord(RequiredViewId, globalRecordId);
        Assert.NotNull(before);

        using HttpResponseMessage response = await ApiTestClient.PutJsonAsync(
            Client,
            RecordsPath + "/" + globalRecordId,
            new { values },
            ct);

        await AssertRequiredErrorsAsync(response, [missing], ct);

        // The gate ran inside the update, after its locked read, and no data-changing call followed it.
        Assert.Contains(new FakeStoreCall(nameof(IDataStore.UpdateAsync), IsWrite: false), _requiredStore.Calls);
        Assert.Empty(_requiredStore.WriteCalls);

        FakeRecordState? after = _requiredStore.GetRecord(RequiredViewId, globalRecordId);
        Assert.NotNull(after);
        Assert.Equal(before.RecStatus, after.RecStatus);
        Assert.Equal(before.Values, after.Values);
        Assert.Equal(before.FirstSave, after.FirstSave);
        Assert.Equal(before.LastSave, after.LastSave);
    }

    private async Task AssertPutSavedAsync(string globalRecordId, string field, string value, CancellationToken ct)
    {
        using HttpResponseMessage response = await ApiTestClient.PutJsonAsync(
            Client,
            RecordsPath + "/" + globalRecordId,
            new { values = new Dictionary<string, object?> { [field] = value } },
            ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        FakeRecordState? after = _requiredStore.GetRecord(RequiredViewId, globalRecordId);
        Assert.NotNull(after);
        Assert.Equal(FieldValue.FromText(value), after.Values[field]);
        Assert.NotNull(after.LastSave);
    }

    /// <summary>
    /// Field-level rejections as (method, <c>values</c> JSON, the one expected <c>errors</c> key, its one message),
    /// each sent as a <c>POST</c> and as a <c>PUT</c> of a seeded record.
    /// </summary>
    public static TheoryData<string, string, string, string> FieldLevelCases
    {
        get
        {
            const string ValuesKey = RecordValidator.ValuesKey;
            const string Unknown = ValidationMessages.UnknownValuesName;
            const string NotData = ValidationMessages.UnknownField;
            const string Duplicate = ValidationMessages.DuplicateField;
            const string Shape = ValuesReader.ScalarValueExpected;
            const string NotFinite = ValidationMessages.NumberExpected;

            var data = new TheoryData<string, string, string, string>();
            foreach (var method in new[] { Post, Put })
            {
                // An object or array under a case variant of a data field: the canonical name, the shape message.
                data.Add(method, $$$"""{"lastname":{"first":"{{{Sentinel}}}"}}""", "LastName", Shape);
                data.Add(method, $$"""{"LASTNAME":["{{Sentinel}}"]}""", "LastName", Shape);

                // A number JSON parsing accepts but no double holds, under a case variant of a number field.
                data.Add(method, """{"age":1e400}""", "Age", NotFinite);
                data.Add(method, """{"AGE":-1e400}""", "Age", NotFinite);

                // A name the form has no metaFields row for, whatever its value: only the fixed values key.
                data.Add(method, $$$"""{"{{{Sentinel}}}":{"nested":"{{{Sentinel}}}"}}""", ValuesKey, Unknown);
                data.Add(method, $$"""{"{{Sentinel}}":["{{Sentinel}}"]}""", ValuesKey, Unknown);
                data.Add(method, $$"""{"{{Sentinel}}":1e400}""", ValuesKey, Unknown);
                data.Add(method, $$"""{"{{Sentinel}}":"{{Sentinel}}"}""", ValuesKey, Unknown);

                // A label or system row holds no value: its canonical name, whatever the value's shape.
                data.Add(method, $$"""{"title1":"{{Sentinel}}"}""", "Title1", NotData);
                data.Add(method, $$$"""{"TITLE1":{"nested":"{{{Sentinel}}}"}}""", "Title1", NotData);
                data.Add(method, """{"recstatus":1}""", "RecStatus", NotData);
                data.Add(method, $$"""{"globalRecordID":"{{Sentinel}}"}""", "GlobalRecordId", NotData);

                // A field named twice, exactly or differing only by case: one error under the canonical name.
                data.Add(method, $$"""{"LastName":"{{Sentinel}}","LastName":"{{Sentinel}}"}""", "LastName", Duplicate);
                data.Add(method, $$"""{"LastName":"{{Sentinel}}","lastNAME":"{{Sentinel}}"}""", "LastName", Duplicate);
            }

            return data;
        }
    }

    /// <summary>
    /// Bodies where a raw fault sits beside other entries, as (method, <c>values</c> JSON, the expected <c>errors</c>
    /// keys, one message each in the same order), each sent as a <c>POST</c> and as a <c>PUT</c> of a seeded record.
    /// A key listed twice expects both messages under it, in that order.
    /// </summary>
    public static TheoryData<string, string, string[], string[]> FaultBesideOtherEntriesCases
    {
        get
        {
            const string ValuesKey = RecordValidator.ValuesKey;
            const string Unknown = ValidationMessages.UnknownValuesName;
            const string NotData = ValidationMessages.UnknownField;
            const string Duplicate = ValidationMessages.DuplicateField;
            const string Shape = ValuesReader.ScalarValueExpected;
            const string NotFinite = ValidationMessages.NumberExpected;

            var data = new TheoryData<string, string, string[], string[]>();
            foreach (var method in new[] { Post, Put })
            {
                // A data field named again, whichever occurrence is faulted: the repeat is a duplicate under the
                // canonical name, and a faulted first occurrence keeps its own fault ahead of it.
                data.Add(method, $$"""{"Age":1,"AGE":["{{Sentinel}}"]}""", ["Age"], [Duplicate]);
                data.Add(method, $$"""{"AGE":["{{Sentinel}}"],"Age":1}""", ["Age", "Age"], [Shape, Duplicate]);
                data.Add(method, """{"age":1e400,"AGE":30}""", ["Age", "Age"], [NotFinite, Duplicate]);

                // A name the form has no row for beside a faulted data field: each under its own key.
                data.Add(
                    method,
                    $$$"""{"lastname":{"first":"{{{Sentinel}}}"},"{{{Sentinel}}}":"{{{Sentinel}}}"}""",
                    ["LastName", ValuesKey],
                    [Shape, Unknown]);

                // A scalar system row beside a faulted label: neither holds a value, so both read the same message.
                data.Add(
                    method,
                    $$$"""{"title1":{"nested":"{{{Sentinel}}}"},"recstatus":1}""",
                    ["Title1", "RecStatus"],
                    [NotData, NotData]);

                // A data field read without fault and named once adds nothing beside the fault.
                data.Add(method, $$"""{"LastName":"{{Sentinel}}","age":[]}""", ["Age"], [Shape]);
            }

            return data;
        }
    }

    /// <summary>
    /// <c>values</c> objects holding a property name or a string value whose escaped lone surrogate decodes to no
    /// text, as (method, <c>values</c> JSON), each sent as a <c>POST</c> and as a <c>PUT</c> of a seeded record.
    /// </summary>
    public static TheoryData<string, string> UndecodableCases
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var method in new[] { Post, Put })
            {
                data.Add(method, $$"""{"\uD800":"{{Sentinel}}"}""");
                data.Add(method, $$"""{"{{Sentinel}}\uDC00":1}""");
                data.Add(method, $$"""{"LastName":"{{Sentinel}}\uD800"}""");
                data.Add(method, $$"""{"{{Sentinel}}":"\uDFFF"}""");
            }

            return data;
        }
    }

    /// <summary>
    /// A rejected entry yields exactly one error, under the canonical field name or the fixed values key, with the
    /// reader's or validator's fixed message, and nothing reaches the store's write methods.
    /// </summary>
    [Theory]
    [MemberData(nameof(FieldLevelCases))]
    public async Task RejectedEntryIsKeyedByCanonicalNameOrValuesKey(
        string method,
        string valuesJson,
        string expectedKey,
        string expectedMessage)
    {
        await using var host = await EndpointHost.StartAsync(new FakeDataStore(FakeForms.TwoPage));

        var problem = await SendRejectedWriteAsync(host, method, valuesJson);

        Assert.Equal(ApiProblems.ValidationFailedDetail, problem.GetProperty("detail").GetString());
        AssertErrors(problem, (expectedKey, expectedMessage));
    }

    /// <summary>
    /// Every raw fault of one body is reported under its own key, so an unknown name next to faulted data fields
    /// neither hides them nor is echoed, and a case variant of a faulted field is also reported as a duplicate.
    /// </summary>
    [Theory]
    [InlineData(Post)]
    [InlineData(Put)]
    public async Task EveryRawFaultOfOneBodyIsReportedUnderItsOwnKey(string method)
    {
        await using var host = await EndpointHost.StartAsync(new FakeDataStore(FakeForms.TwoPage));

        var problem = await SendRejectedWriteAsync(
            host,
            method,
            $$"""{"lastname":{},"{{Sentinel}}":["{{Sentinel}}"],"age":1e400,"title1":{},"AGE":1e400}""");

        AssertErrors(
            problem,
            ("LastName", ValuesReader.ScalarValueExpected),
            (RecordValidator.ValuesKey, ValidationMessages.UnknownValuesName),
            ("Age", ValidationMessages.NumberExpected),
            ("Title1", ValidationMessages.UnknownField),
            ("Age", ValidationMessages.DuplicateField));
    }

    /// <summary>
    /// A raw fault does not hide the entries beside it: every property is resolved in document order by the
    /// precedence the validator applies, so a name that is not a data field reads its own error, a data field named
    /// again is a duplicate under its canonical name, a faulted first occurrence keeps its fault, and nothing is
    /// written.
    /// </summary>
    [Theory]
    [MemberData(nameof(FaultBesideOtherEntriesCases))]
    public async Task FaultBesideOtherEntriesIsResolvedPerPropertyInDocumentOrder(
        string method,
        string valuesJson,
        string[] expectedKeys,
        string[] expectedMessages)
    {
        Assert.Equal(expectedKeys.Length, expectedMessages.Length);
        await using var host = await EndpointHost.StartAsync(new FakeDataStore(FakeForms.TwoPage));

        var problem = await SendRejectedWriteAsync(host, method, valuesJson);

        Assert.Equal(ApiProblems.ValidationFailedDetail, problem.GetProperty("detail").GetString());
        AssertErrors(problem, [.. expectedKeys.Zip(expectedMessages)]);
    }

    /// <summary>
    /// A name or string that cannot be decoded is the request-level "malformed JSON" 400 with no <c>errors</c>
    /// member, not a field error and not a 500.
    /// </summary>
    [Theory]
    [MemberData(nameof(UndecodableCases))]
    public async Task UndecodableNameOrStringIsRequestLevelMalformedJson(string method, string valuesJson)
    {
        await using var host = await EndpointHost.StartAsync(new FakeDataStore(FakeForms.TwoPage));

        var problem = await SendRejectedWriteAsync(host, method, valuesJson);

        Assert.Equal(ApiProblems.MalformedJsonDetail, problem.GetProperty("detail").GetString());
        Assert.False(
            problem.TryGetProperty("errors", out _),
            $"A request-level 400 has no errors member: {problem.GetRawText()}");
    }

    // Sends {"values": <valuesJson>} as raw JSON with the given method: a POST to the form, or a PUT of a record seeded
    // for this request. Asserts the shared rejection contract and returns the ProblemDetails body.
    private static async Task<JsonElement> SendRejectedWriteAsync(EndpointHost host, string method, string valuesJson)
    {
        Assert.True(method is Post or Put, $"Unexpected method {method}.");

        var path = $"/api/forms/{TwoPageViewId}/records";
        string? seededId = null;
        FakeRecordState? before = null;
        if (method == Put)
        {
            seededId = host.Store.SeedRecord(
                TwoPageViewId,
                new Dictionary<string, FieldValue>
                {
                    ["LastName"] = FieldValue.FromText("Seeded"),
                    ["Age"] = FieldValue.FromNumber(30),
                });
            before = host.Store.GetRecord(TwoPageViewId, seededId);
            path += "/" + seededId;
        }

        var json = $$"""{"values":{{valuesJson}}}""";
        using var request = new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = new StringContent(json, Encoding.UTF8, ApiTestClient.JsonMediaType),
        };
        using var response = await host.Client.SendAsync(request, host.Timeout);
        var text = await response.Content.ReadAsStringAsync(host.Timeout);

        Assert.True(
            response.StatusCode == HttpStatusCode.BadRequest,
            $"{method} {json} returned {(int)response.StatusCode}: {text}");
        Assert.Equal(ApiProblems.ProblemContentType, response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain(Sentinel, text, StringComparison.Ordinal);

        // Rejected before any write call and without check code: nothing written, nothing evaluated, and a seeded
        // record keeps every value and stamp.
        Assert.Equal(0, host.Engine.RunCalls);
        Assert.Empty(host.Store.WriteCalls);
        Assert.DoesNotContain(host.Store.Calls, call => call.Method is nameof(IDataStore.InsertAsync)
            or nameof(IDataStore.UpdateAsync)
            or nameof(IDataStore.SoftDeleteAsync));
        if (seededId is not null)
        {
            var after = host.Store.GetRecord(TwoPageViewId, seededId);
            Assert.NotNull(before);
            Assert.NotNull(after);
            Assert.Equal(before.RecStatus, after.RecStatus);
            Assert.Equal(before.Values, after.Values);
            Assert.Equal(before.FirstSave, after.FirstSave);
            Assert.Equal(before.LastSave, after.LastSave);
        }

        using var document = JsonDocument.Parse(text);
        return ApiTestClient.AssertProblemDetails(document.RootElement.Clone(), StatusCodes.Status400BadRequest);
    }

    // Asserts the errors map holds exactly the expected keys, compared ordinally, each with exactly the messages of its
    // expected entries in their order: a key listed once expects one message, a key listed twice expects two.
    private static void AssertErrors(JsonElement problem, params (string Key, string Message)[] expected)
    {
        Assert.True(
            problem.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object,
            $"A field-level 400 carries an errors object: {problem.GetRawText()}");

        var expectedByKey = expected.GroupBy(entry => entry.Key, StringComparer.Ordinal).ToArray();
        Assert.Equal(
            expectedByKey.Select(group => group.Key).Order(StringComparer.Ordinal),
            errors.EnumerateObject().Select(entry => entry.Name).Order(StringComparer.Ordinal));

        foreach (var group in expectedByKey)
        {
            Assert.Equal(
                group.Select(entry => (string?)entry.Message),
                errors.GetProperty(group.Key).EnumerateArray().Select(item => item.GetString()));
        }
    }

    /// <summary>
    /// An in-process TestServer host over the real record endpoints and the services behind them, with the API's JSON
    /// settings, <see cref="FakeDataStore"/> as the store and the real check-code engine behind a
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

            var app = builder.Build();
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
