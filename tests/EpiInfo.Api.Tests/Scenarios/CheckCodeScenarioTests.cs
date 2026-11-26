using System.Data;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using EpiInfo.Api.Tests.Infrastructure;
using EpiInfo.Api.Tests.Reads;
using EpiInfo.Tests.Shared;
using Microsoft.Data.SqlClient;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace EpiInfo.Api.Tests.Scenarios;

/// <summary>
/// The data-driven check-code scenarios of <c>testdata/scenarios/check_code_scenarios.json</c> (AAP 0.7.4), run
/// against the seeded rehearsal database through the API, tiered as AAP 0.7.2 sets out: the save cases
/// <c>EC-VAL-01</c>..<c>03</c> and the data cases <c>DATA-01</c> and <c>DATA-02</c> are P0, and the 19 in-scope field
/// events plus <c>EC-GEO-01</c> are the P1 theory.
/// </summary>
/// <remarks>
/// <para>
/// Test code never supplies a program: the API reads <c>metaViews.CheckCode</c> from the database, so a seeded program
/// that differs from the file's expectations fails its cases.
/// </para>
/// <para>
/// Direct SQL here is read-only: <c>SELECT</c> statements over a fixed whitelist of bracket-quoted table names, with
/// every value a <see cref="SqlParameter"/>. The only write the class attempts is the save cases' <c>POST</c>, which
/// must be rejected, and each case proves it wrote nothing. The class never skips: without a reachable database
/// <see cref="ApiFactory.ForDatabaseAsync"/> fails with the message naming <c>SQL_HOST</c> and
/// <c>MSSQL_SA_PASSWORD</c>.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(DatabaseCollections.Database)]
public sealed class CheckCodeScenarioTests
{
    private const string CaseReport = "CaseReport";
    private const string CaseReport1 = "CaseReport1";
    private const string FoodHistory = "FoodHistory";
    private const string FoodHistory2 = "FoodHistory2";
    private const string FoodHistory3 = "FoodHistory3";
    private const string MetaViews = "metaViews";

    private const string SaveEvent = "save";
    private const string LoadId = "DATA-01";
    private const string LoadEvent = "load";
    private const string NavigateId = "DATA-02";
    private const string NavigateEvent = "navigate";

    private const string OnsetWeekField = "OnsetWeek";
    private const string WeekScenarioPrefix = "EC-WEEK-";
    private const string GeocodeCommand = "GEOCODE";
    private const string DialogExpectation = "dialog";
    private const string CheckboxFieldType = "checkbox";
    private const string FieldClickEvent = "FieldClick";
    private const string ProblemMediaType = "application/problem+json";
    private const string JsonMediaType = "application/json";

    // The status the seeded inserts give a soft-deleted base row.
    private const int DeletedRecStatus = 0;

    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(5);

    // The only tables this class reads; each name is a fixed literal that needs no escaping inside brackets.
    private static readonly string[] ReadableTables =
        [CaseReport, CaseReport1, FoodHistory, FoodHistory2, FoodHistory3, MetaViews];

    // The base table, then every page table, of each seeded form (testdata/sql/05_data_tables.sql).
    private static readonly Dictionary<string, string[]> FormTables = new(StringComparer.Ordinal)
    {
        [CaseReport] = [CaseReport, CaseReport1],
        [FoodHistory] = [FoodHistory, FoodHistory2, FoodHistory3],
    };

    private static readonly string[] DeletedRecordForms = [CaseReport, FoodHistory];

    // The wire spelling of each event kind, keyed by the suffix of a scenario event such as "DOB.After".
    private static readonly Dictionary<string, string> EventKinds = new(StringComparer.Ordinal)
    {
        ["Before"] = "FieldBefore",
        ["After"] = "FieldAfter",
        ["Click"] = FieldClickEvent,
    };

    // The field-event response's list members, each a JSON array.
    private static readonly string[] ResponseListMembers = ["enabled", "disabled", "cleared", "dialogs", "diagnostics"];

    // Every expectation the start state already meets, which the final-state comparison alone cannot tell apart from
    // the program's own work, classified by the seeded programs (testdata/sql/03_forms.sql): as an effect the response
    // must name (Enabled, Cleared, Assigned) or as a sent value the program must leave alone (Retained).
    private static readonly Dictionary<string, RequiredEffect[]> EffectsBehindStartState = new(StringComparer.Ordinal)
    {
        // Sex "1" runs ENABLE Pregnant, and every field starts enabled.
        ["LY-01"] = [new RequiredEffect("Pregnant", EffectKind.Enabled)],

        // A missing Sex takes the ELSE branch's CLEAR Pregnant, and the unsent Pregnant starts null.
        ["LY-04"] = [new RequiredEffect("Pregnant", EffectKind.Cleared)],

        // YEARS with no interview date is null, which ASSIGN AGE returns in assigned as JSON null.
        ["EC-AGE-05"] = [new RequiredEffect("Age", EffectKind.Assigned)],

        // The FeverTemp After block only shows a DIALOG, so the sent FeverTemp is neither cleared nor assigned.
        ["EC-FEVER-01"] = [new RequiredEffect("FeverTemp", EffectKind.Retained)],

        // The client's click toggle sets Asian, and Asian's Click block assigns only the six other boxes.
        ["EC-RACE-01"] = [new RequiredEffect("Asian", EffectKind.Retained)],

        // Asian's Click block runs ASSIGN Black = (-) among the six other boxes and never assigns Asian itself.
        ["EC-RACE-02"] =
        [
            new RequiredEffect("Asian", EffectKind.Retained),
            new RequiredEffect("Black", EffectKind.Assigned),
        ],
    };

    private readonly ITestOutputHelper _output;

    /// <summary>Creates the test class with xUnit's output sink, which receives each request and response.</summary>
    /// <param name="output">The test output.</param>
    public CheckCodeScenarioTests(ITestOutputHelper output)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
    }

    /// <summary>The in-scope save scenarios (<c>EC-VAL-01</c>..<c>03</c>), in file order.</summary>
    public static IEnumerable<object[]> SaveIds =>
        ScenarioCatalog.All
            .Where(c => c.InScope && string.Equals(c.Event, SaveEvent, StringComparison.Ordinal))
            .Select(c => new object[] { c.Id })
            .ToArray();

    [Theory]
    [MemberData(nameof(SaveIds))]
    public async Task SaveScenarioIsRejected(string id)
    {
        ScenarioCase scenario = ScenarioCatalog.Get(id);
        Assert.Equal(SaveEvent, scenario.Event);
        Assert.True(
            Member(scenario.Expect, "rejected", id).ValueKind == JsonValueKind.True,
            $"{id}: a save scenario must expect rejected: true.");
        string field = RequireString(scenario.Expect, "field", id);
        string[] tables = TablesOf(scenario.Form, id);

        using var cts = new CancellationTokenSource(TestTimeout);
        CancellationToken ct = cts.Token;
        await using var factory = await ApiFactory.ForDatabaseAsync();
        using var client = factory.CreateClient();
        int viewId = await ApiTestClient.GetViewIdAsync(client, scenario.Form, ct);
        string path = string.Create(CultureInfo.InvariantCulture, $"/api/forms/{viewId}/records");

        // Held from the first count to the last, so no other test process can move a count in between.
        await using var dbLock = await DatabaseWriteLock.AcquireExclusiveAsync(ct);
        long[] before = await CountRowsAsync(tables, ct);

        factory.Recorder.Clear();
        factory.Engine.Reset();

        // The scenario's given object is sent unchanged as the record's values.
        using HttpResponseMessage response =
            await ApiTestClient.PostJsonAsync(client, path, new { values = scenario.Given }, ct);
        JsonElement body = await ApiTestClient.ReadJsonElementAsync(response, ct);
        _output.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{id}: POST {path} values {JsonSerializer.Serialize(scenario.Given, ApiTestClient.Json)} -> " +
                $"{(int)response.StatusCode}"));

        Assert.True(
            response.StatusCode == HttpStatusCode.BadRequest,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{id}: POST {path} returned {(int)response.StatusCode}, expected 400."));
        Assert.Equal(ProblemMediaType, response.Content.Headers.ContentType?.MediaType);
        ApiTestClient.AssertProblemDetails(body, (int)HttpStatusCode.BadRequest);

        JsonElement errors = Member(body, "errors", id);
        Assert.True(
            errors.ValueKind == JsonValueKind.Object,
            $"{id}: the 400 must carry a field-level errors object; errors is {errors.ValueKind}.");
        string[] keys = errors.EnumerateObject().Select(p => p.Name).ToArray();
        _output.WriteLine($"{id}: errors keys [{string.Join(", ", keys)}]");

        // TryGetProperty matches ordinally, so the key must be the canonical metaFields.Name exactly.
        Assert.True(
            errors.TryGetProperty(field, out JsonElement messages),
            $"{id}: errors has no entry named exactly '{field}'; it has [{string.Join(", ", keys)}].");
        Assert.True(
            messages.ValueKind == JsonValueKind.Array && messages.GetArrayLength() > 0,
            $"{id}: errors['{field}'] must be a non-empty array; it is {messages.ValueKind}.");
        Assert.All(
            messages.EnumerateArray(),
            message => Assert.False(
                message.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(message.GetString()),
                $"{id}: every errors['{field}'] message must be a non-empty string."));

        IReadOnlyList<RecordedCommand> writes = factory.Recorder.Writes();
        Assert.True(
            writes.Count == 0,
            $"{id}: the rejected save sent {writes.Count} write command(s): " +
            string.Join("; ", writes.Select(command => command.Describe(includeParameterValues: false))));

        long[] after = await CountRowsAsync(tables, ct);
        for (int index = 0; index < tables.Length; index++)
        {
            Assert.True(
                before[index] == after[index],
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{id}: [{tables[index]}] held {before[index]} rows before the rejected save and " +
                    $"{after[index]} after."));
        }
    }

    [Fact]
    public async Task Data01_LoadingNeverWritesOrRecomputes()
    {
        ScenarioCase scenario = ScenarioCatalog.Get(LoadId);
        Assert.Equal(LoadEvent, scenario.Event);
        Assert.True(
            Member(scenario.Expect, "storedValuesUnchanged", LoadId).ValueKind == JsonValueKind.True,
            $"{LoadId} must expect storedValuesUnchanged: true.");

        // The oracle must reproduce the catalogue's own EPIWEEK expectations before it judges stored weeks.
        AssertOracleMatchesWeekScenarios();

        using var cts = new CancellationTokenSource(TestTimeout);
        CancellationToken ct = cts.Token;
        await using var factory = await ApiFactory.ForDatabaseAsync();

        // The procedure takes the exclusive test lock itself; holding one here would make it wait on this test's own
        // lock session until the 300-second lock timeout.
        await ReadEquivalenceProcedure.RunAsync(factory, _output, ct);

        using var client = factory.CreateClient();
        await using var dbLock = await DatabaseWriteLock.AcquireSharedAsync(ct);
        int viewId = await ApiTestClient.GetViewIdAsync(client, FoodHistory, ct);

        factory.Recorder.Clear();
        factory.Engine.Reset();

        IReadOnlyList<StoredOnsetWeek> stored = await ReadStoredOnsetWeeksAsync(ct);
        Assert.True(stored.Count > 0, $"[{FoodHistory2}] holds no record with both OnsetDate and OnsetWeek.");

        StoredOnsetWeek[] differing = stored.Where(row => row.OnsetWeek != MmwrWeek(row.OnsetDate)).ToArray();
        _output.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{LoadId}: {stored.Count - differing.Length} of {stored.Count} stored OnsetWeek values match " +
                $"EPIWEEK(OnsetDate); {differing.Length} differ and must be returned as stored."));

        // testdata/README.md reports only 40 of 326 sample weeks matching, so a database without a differing week
        // cannot show that a load returns the stored value rather than a recomputed one.
        Assert.True(
            differing.Length > 0,
            $"Every stored {OnsetWeekField} matches EPIWEEK(OnsetDate); the seeded historical weeks are missing.");

        foreach (StoredOnsetWeek row in differing)
        {
            string path = string.Create(
                CultureInfo.InvariantCulture,
                $"/api/forms/{viewId}/records/{Uri.EscapeDataString(row.GlobalRecordId)}");
            JsonElement record = await GetJsonAsync(client, path, HttpStatusCode.OK, LoadId, ct);
            JsonElement values = Member(record, "values", LoadId);
            Assert.True(
                values.ValueKind == JsonValueKind.Object,
                $"{LoadId}: GET {path} returned values of kind {values.ValueKind}.");
            JsonElement week = Member(values, OnsetWeekField, LoadId);
            Assert.True(
                week.ValueKind == JsonValueKind.Number,
                $"{LoadId}: GET {path} returned {OnsetWeekField} of kind {week.ValueKind}, expected a number.");
            Assert.True(
                week.GetDouble() == row.OnsetWeek,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{LoadId}: GET {path} returned {OnsetWeekField} {week.GetRawText()}, but SQL stores " +
                    $"{row.OnsetWeek} (EPIWEEK of the stored date is {MmwrWeek(row.OnsetDate)})."));
        }

        Assert.Equal(0, factory.Engine.RunCalls);
        IReadOnlyList<RecordedCommand> writes = factory.Recorder.Writes();
        Assert.True(
            writes.Count == 0,
            $"{LoadId}: loading records sent {writes.Count} write command(s): " +
            string.Join("; ", writes.Select(command => command.Describe(includeParameterValues: false))));
    }

    [Fact]
    public async Task Data02_SoftDeletedRecordIsRetainedAndMarkedDeleted()
    {
        ScenarioCase scenario = ScenarioCatalog.Get(NavigateId);
        Assert.Equal(NavigateEvent, scenario.Event);
        JsonElement recStatus = Member(scenario.Given, "RecStatus", NavigateId);
        Assert.True(
            recStatus.ValueKind == JsonValueKind.Number &&
            recStatus.TryGetInt32(out int givenStatus) &&
            givenStatus == DeletedRecStatus,
            $"{NavigateId} must give RecStatus {DeletedRecStatus}.");
        Assert.True(
            Member(scenario.Expect, "recordRetained", NavigateId).ValueKind == JsonValueKind.True,
            $"{NavigateId} must expect recordRetained: true.");
        Assert.True(
            Member(scenario.Expect, "markedDeleted", NavigateId).ValueKind == JsonValueKind.True,
            $"{NavigateId} must expect markedDeleted: true.");

        using var cts = new CancellationTokenSource(TestTimeout);
        CancellationToken ct = cts.Token;
        await using var factory = await ApiFactory.ForDatabaseAsync();
        using var client = factory.CreateClient();

        // The expected positions are SQL counts, which must not move while the test compares them.
        await using var dbLock = await DatabaseWriteLock.AcquireSharedAsync(ct);
        factory.Recorder.Clear();
        factory.Engine.Reset();

        foreach (string form in DeletedRecordForms)
        {
            int viewId = await ApiTestClient.GetViewIdAsync(client, form, ct);
            string records = string.Create(CultureInfo.InvariantCulture, $"/api/forms/{viewId}/records");

            if (await ReadFirstDeletedRecordAsync(form, ct) is not { } deleted)
            {
                throw new XunitException(
                    $"{NavigateId}: [{form}] holds no RecStatus = {DeletedRecStatus} row; the seed has soft-deleted " +
                    "records.");
            }

            string recordPath = records + "/" + Uri.EscapeDataString(deleted.GlobalRecordId);
            string context = $"{NavigateId} {form}";

            // Retained and marked deleted when opened by id.
            JsonElement byId = await GetJsonAsync(client, recordPath, HttpStatusCode.OK, context, ct);
            AssertRecord(byId, deleted.GlobalRecordId, DeletedRecStatus, $"{context}: GET {recordPath}");

            // Placed in the set including deleted records, at its SQL position over every row.
            string withDeleted = recordPath + "/position?includeDeleted=true";
            JsonElement placed = await GetJsonAsync(client, withDeleted, HttpStatusCode.OK, context, ct);
            JsonElement position = Member(placed, "position", context);
            Assert.True(
                position.ValueKind == JsonValueKind.Number && position.TryGetInt32(out _),
                $"{context}: GET {withDeleted} returned position of kind {position.ValueKind}, expected an integer.");
            int n = position.GetInt32();
            long expectedPosition = await CountRowsUpToAsync(form, deleted.UniqueKey, ct);
            Assert.True(
                n == expectedPosition,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{context}: GET {withDeleted} placed the record at {n}; SQL counts {expectedPosition} rows " +
                    $"with UniqueKey <= {deleted.UniqueKey}."));
            long allRows = (await CountRowsAsync([form], ct))[0];
            Assert.True(
                RequireInt(placed, "count", context) == allRows,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{context}: GET {withDeleted} returned count {RequireInt(placed, "count", context)}; SQL holds " +
                    $"{allRows} rows."));

            string atWithDeleted = string.Create(CultureInfo.InvariantCulture, $"{records}/at/{n}?includeDeleted=true");
            JsonElement atAll = await GetJsonAsync(client, atWithDeleted, HttpStatusCode.OK, context, ct);
            AssertRecord(atAll, deleted.GlobalRecordId, DeletedRecStatus, $"{context}: GET {atWithDeleted}");

            // Outside the active set: no position there, and the active index n holds another record or none.
            string activePosition = recordPath + "/position";
            JsonElement unplaced = await GetJsonAsync(client, activePosition, HttpStatusCode.OK, context, ct);
            Assert.True(
                Member(unplaced, "position", context).ValueKind == JsonValueKind.Null,
                $"{context}: GET {activePosition} must return position null for a deleted record.");

            string atActive = string.Create(CultureInfo.InvariantCulture, $"{records}/at/{n}");
            using HttpResponseMessage response = await client.GetAsync(atActive, ct);
            JsonElement body = await ApiTestClient.ReadJsonElementAsync(response, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                ApiTestClient.AssertProblemDetails(body, (int)HttpStatusCode.NotFound);
            }
            else
            {
                Assert.True(
                    response.StatusCode == HttpStatusCode.OK,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{context}: GET {atActive} returned {(int)response.StatusCode}, expected 200 or 404."));
                Assert.False(
                    string.Equals(
                        RequireString(body, "globalRecordId", context),
                        deleted.GlobalRecordId,
                        StringComparison.Ordinal),
                    $"{context}: GET {atActive} returned the deleted record from the active set.");
            }

            _output.WriteLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{context}: deleted UniqueKey {deleted.UniqueKey} is position {n} of {allRows} with deleted " +
                    $"records and absent from the active set."));
        }

        Assert.Equal(0, factory.Engine.RunCalls);
        Assert.Empty(factory.Recorder.Writes());
    }

    // One method takes both sources: in xUnit v2 a theory whose only source is empty reports "No data found", so a
    // method per source would fail whenever P1 is complete or wholly pending, while the union is always the 20 P1 ids.
    [Theory]
    [MemberData(nameof(ScenarioCatalog.RunnableIds), MemberType = typeof(ScenarioCatalog))]
    [MemberData(
        nameof(ScenarioCatalog.PendingIds),
        MemberType = typeof(ScenarioCatalog),
        Skip = "P1 not delivered: see docs/parity-notes.md, Undelivered items")]
    public async Task FieldEventScenario(string id)
    {
        ScenarioCase scenario = ScenarioCatalog.Get(id);
        Assert.True(
            ScenarioCatalog.IsFieldEvent(scenario),
            $"{id}: event '{scenario.Event}' is not <Field>.<Before|After|Click>.");
        (string fieldName, string eventKind) = SplitEvent(scenario.Event, id);
        bool geocode = string.Equals(id, ScenarioCatalog.GeocodeId, StringComparison.Ordinal);
        if (geocode)
        {
            Assert.True(
                Member(scenario.Expect, "skipped", id).ValueKind == JsonValueKind.True,
                $"{id} must expect skipped: true.");
            Assert.True(
                Member(scenario.Expect, "logged", id).ValueKind == JsonValueKind.True,
                $"{id} must expect logged: true.");
        }
        else
        {
            Assert.True(
                scenario.Expect.EnumerateObject().Any(),
                $"{id}: the scenario expects nothing, so it could pass without checking any effect.");
        }

        using var cts = new CancellationTokenSource(TestTimeout);
        CancellationToken ct = cts.Token;
        await using var factory = await ApiFactory.ForDatabaseAsync();
        using var client = factory.CreateClient();
        int viewId = await ApiTestClient.GetViewIdAsync(client, scenario.Form, ct);
        string formPath = string.Create(CultureInfo.InvariantCulture, $"/api/forms/{viewId}");
        JsonElement definition = await GetJsonAsync(client, formPath, HttpStatusCode.OK, id, ct);
        IReadOnlyDictionary<string, FormField> fields = ReadFields(definition, id);
        if (!fields.TryGetValue(fieldName, out FormField? clicked))
        {
            throw new XunitException($"{id}: '{fieldName}' is not a field of {scenario.Form} (matched ordinally).");
        }

        // The given values are already typed as the wire has them: ISO date strings, numbers, strings, booleans, null.
        JsonObject values = JsonNode.Parse(scenario.Given.GetRawText())?.AsObject()
            ?? throw new XunitException($"{id}: given is not a JSON object.");
        if (string.Equals(eventKind, FieldClickEvent, StringComparison.Ordinal) &&
            string.Equals(clicked.FieldType, CheckboxFieldType, StringComparison.Ordinal))
        {
            // The client raises a checkbox click after the value toggles, so the program sees the post-click value.
            ToggleCheckbox(values, fieldName, id);
        }

        Dictionary<string, FieldState> start = StartState(fields, values);
        var state = new Dictionary<string, FieldState>(start, StringComparer.Ordinal);
        var request = new JsonObject
        {
            ["event"] = eventKind,
            ["fieldName"] = fieldName,
            ["values"] = values.DeepClone(),
        };
        string path = formPath + "/checkcode/field-event";

        factory.Logs.Clear();
        factory.Recorder.Clear();
        factory.Engine.Reset();

        _output.WriteLine($"{id}: POST {path} {request.ToJsonString(ApiTestClient.Json)}");
        using HttpResponseMessage response = await ApiTestClient.PostJsonAsync(client, path, request, ct);
        JsonElement result = await ApiTestClient.ReadJsonElementAsync(response, ct);
        _output.WriteLine(
            string.Create(CultureInfo.InvariantCulture, $"{id}: {(int)response.StatusCode} {result.GetRawText()}"));

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{id}: POST {path} returned {(int)response.StatusCode}, expected 200."));
        Assert.Equal(JsonMediaType, response.Content.Headers.ContentType?.MediaType);
        AssertResponseShape(result, id);
        AssertNoWarningsOrErrors(result, id);
        Assert.True(
            factory.Engine.RunCalls >= 1,
            $"{id}: the field event did not reach the check-code engine (Run calls: {factory.Engine.RunCalls}).");
        IReadOnlyList<RecordedCommand> writes = factory.Recorder.Writes();
        Assert.True(
            writes.Count == 0,
            $"{id}: the field event sent {writes.Count} write command(s): " +
            string.Join("; ", writes.Select(command => command.Describe(includeParameterValues: false))));

        if (geocode)
        {
            await AssertGeocodeSkippedAndLoggedAsync(factory, scenario, result, ct);
            return;
        }

        ApplyResponse(state, fields, result, id);
        AssertExpectations(scenario, state, result);
        AssertEffectsBehindStartState(scenario, start, result);
    }

    // The final state of every field after the response is applied, as web/src/checkcode/applyFieldEvent.ts computes
    // it: enabled then disabled set the check-code flag of any field of the form, and cleared then assigned set the
    // value of a data field. Every name the response lists must be a canonical field name of the form, matched
    // ordinally, which also proves that a program identifier such as AGE resolved to the canonical Age.
    private static void ApplyResponse(
        Dictionary<string, FieldState> state,
        IReadOnlyDictionary<string, FormField> fields,
        JsonElement result,
        string id)
    {
        foreach (string name in StringList(result, "enabled", id))
        {
            _ = RequireKnownField(fields, name, "enabled", id);
            state[name] = state[name] with { Enabled = true };
        }

        foreach (string name in StringList(result, "disabled", id))
        {
            _ = RequireKnownField(fields, name, "disabled", id);
            state[name] = state[name] with { Enabled = false };
        }

        foreach (string name in StringList(result, "cleared", id))
        {
            if (RequireKnownField(fields, name, "cleared", id).IsData)
            {
                state[name] = state[name] with { Value = null };
            }
        }

        foreach (JsonProperty assigned in Member(result, "assigned", id).EnumerateObject())
        {
            if (RequireKnownField(fields, assigned.Name, "assigned", id).IsData)
            {
                JsonNode? value = JsonNode.Parse(assigned.Value.GetRawText());
                state[assigned.Name] = state[assigned.Name] with { Value = value };
            }
        }
    }

    // Every expect property against the final state: "dialog" against the dialogs list, an object expectation by
    // the properties it declares, and a scalar expectation against the final value.
    private static void AssertExpectations(
        ScenarioCase scenario,
        Dictionary<string, FieldState> state,
        JsonElement result)
    {
        string id = scenario.Id;
        foreach (JsonProperty expectation in scenario.Expect.EnumerateObject())
        {
            if (expectation.NameEquals(DialogExpectation))
            {
                AssertDialog(result, expectation.Value, id);
                continue;
            }

            string name = expectation.Name;
            if (!state.TryGetValue(name, out FieldState final))
            {
                throw new XunitException($"{id}: expects '{name}', which is not a field of {scenario.Form}.");
            }

            if (expectation.Value.ValueKind != JsonValueKind.Object)
            {
                AssertValue(id, name, expectation.Value, final.Value);
                continue;
            }

            foreach (JsonProperty property in expectation.Value.EnumerateObject())
            {
                if (property.NameEquals("enabled"))
                {
                    Assert.True(
                        property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                        $"{id}: '{name}.enabled' must be a boolean expectation; it is {property.Value.ValueKind}.");
                    bool expected = property.Value.GetBoolean();
                    Assert.True(
                        final.Enabled == expected,
                        $"{id}: '{name}' is {(final.Enabled ? "enabled" : "disabled")} after the event, expected " +
                        $"{(expected ? "enabled" : "disabled")}.");
                }
                else if (property.NameEquals("value"))
                {
                    AssertValue(id, name, property.Value, final.Value);
                }
                else
                {
                    throw new XunitException(
                        $"{id}: the expectation for '{name}' holds '{property.Name}'; only 'enabled' and 'value' are " +
                        "known, so the scenario file's shape changed.");
                }
            }
        }
    }

    // The effect table names only in-scope field-event scenarios, the expectations the start state already meets must
    // be exactly those the table classifies for the scenario, and the response must bear out each classification:
    // ENABLE in enabled, CLEAR in cleared, an ASSIGN as a present member of assigned holding the expected value, and a
    // retained sent value in neither cleared nor assigned.
    private static void AssertEffectsBehindStartState(
        ScenarioCase scenario,
        Dictionary<string, FieldState> start,
        JsonElement result)
    {
        string id = scenario.Id;
        IReadOnlyList<string> fieldEventIds = ScenarioCatalog.FieldEventIds;
        string[] stale = EffectsBehindStartState.Keys
            .Where(key => !fieldEventIds.Contains(key, StringComparer.Ordinal))
            .ToArray();
        Assert.True(
            stale.Length == 0,
            $"{id}: the effect table names [{string.Join(", ", stale)}], which are not in-scope field-event ids.");

        RequiredEffect[] effects = EffectsBehindStartState.TryGetValue(id, out RequiredEffect[]? tabled) ? tabled : [];
        string[] met = StartMetExpectations(scenario, start)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] covered = effects
            .Select(effect => effect.Field + (effect.Kind == EffectKind.Enabled ? ".enabled" : ".value"))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.True(
            met.SequenceEqual(covered, StringComparer.Ordinal),
            $"{id}: the start state already meets the expectations [{string.Join(", ", met)}], but the effect table " +
            $"classifies [{string.Join(", ", covered)}]; the table must classify exactly the expectations the start " +
            "state meets, each as an effect the response must name or a sent value the program must leave alone.");

        foreach (RequiredEffect effect in effects)
        {
            switch (effect.Kind)
            {
                case EffectKind.Enabled:
                    AssertNamedIn(result, "enabled", effect.Field, "ENABLE", id);
                    break;
                case EffectKind.Cleared:
                    AssertNamedIn(result, "cleared", effect.Field, "CLEAR", id);
                    break;
                case EffectKind.Assigned:
                    AssertAssigned(scenario, result, effect.Field);
                    break;
                case EffectKind.Retained:
                    AssertRetained(result, effect.Field, id);
                    break;
                default:
                    throw new XunitException($"{id}: the effect table holds the unknown effect kind {effect.Kind}.");
            }
        }
    }

    // Each expectation the start state already meets, as "<field>.enabled" or "<field>.value": an enabled expectation
    // equal to the starting flag, or a value expectation the starting value already matches, compared as ValueMatches
    // compares a final value. The starting value is the one the request sends, after the click toggle, or null for a
    // field the request does not send. "dialog" is checked against the response alone.
    private static IEnumerable<string> StartMetExpectations(ScenarioCase scenario, Dictionary<string, FieldState> start)
    {
        string id = scenario.Id;
        foreach (JsonProperty expectation in scenario.Expect.EnumerateObject())
        {
            if (expectation.NameEquals(DialogExpectation))
            {
                continue;
            }

            string name = expectation.Name;
            if (!start.TryGetValue(name, out FieldState initial))
            {
                throw new XunitException($"{id}: expects '{name}', which is not a field of {scenario.Form}.");
            }

            JsonElement expected = expectation.Value;
            if (expected.ValueKind != JsonValueKind.Object)
            {
                if (ValueMatches(id, name, expected, initial.Value))
                {
                    yield return name + ".value";
                }

                continue;
            }

            if (expected.TryGetProperty("enabled", out JsonElement enabled) &&
                (enabled.ValueKind is JsonValueKind.True or JsonValueKind.False) &&
                enabled.GetBoolean() == initial.Enabled)
            {
                yield return name + ".enabled";
            }

            if (expected.TryGetProperty("value", out JsonElement value) && ValueMatches(id, name, value, initial.Value))
            {
                yield return name + ".value";
            }
        }
    }

    // assigned must hold the field as a present member whose value matches the scenario's expected value, compared as
    // ValueMatches compares a final value, so an absent member fails even where the expectation is null.
    private static void AssertAssigned(ScenarioCase scenario, JsonElement result, string field)
    {
        string id = scenario.Id;
        JsonElement expected = ExpectedValue(scenario, field);
        JsonElement assigned = Member(result, "assigned", id);
        Assert.True(
            assigned.TryGetProperty(field, out JsonElement assignedValue) &&
            ValueMatches(id, field, expected, JsonNode.Parse(assignedValue.GetRawText())),
            $"{id}: 'assigned' must hold '{field}' as {expected.GetRawText()}, because the start state already meets " +
            $"the expectation and only the response shows the ASSIGN; assigned is {assigned.GetRawText()}.");
    }

    // A sent value the program leaves alone is named in neither cleared nor assigned, so the start value that meets
    // the expectation is the one the program kept, not one it wrote back.
    private static void AssertRetained(JsonElement result, string field, string id)
    {
        JsonElement cleared = Member(result, "cleared", id);
        JsonElement assigned = Member(result, "assigned", id);
        Assert.True(
            !StringList(result, "cleared", id).Contains(field, StringComparer.Ordinal) &&
            !assigned.TryGetProperty(field, out _),
            $"{id}: neither 'cleared' nor 'assigned' may name '{field}', because the program leaves its sent value " +
            $"alone; cleared is {cleared.GetRawText()} and assigned is {assigned.GetRawText()}.");
    }

    // The value a scenario expects for a field: a scalar expectation itself, or an object expectation's value.
    private static JsonElement ExpectedValue(ScenarioCase scenario, string field)
    {
        if (!scenario.Expect.TryGetProperty(field, out JsonElement expectation))
        {
            throw new XunitException(
                $"{scenario.Id}: the effect table names '{field}', which the scenario does not expect.");
        }

        if (expectation.ValueKind != JsonValueKind.Object)
        {
            return expectation;
        }

        return expectation.TryGetProperty("value", out JsonElement value)
            ? value
            : throw new XunitException(
                $"{scenario.Id}: the effect table asserts the value of '{field}', whose expectation declares none.");
    }

    // A list member of the response must name the field ordinally; the command is the one the effect stands for.
    private static void AssertNamedIn(JsonElement result, string member, string field, string command, string id) =>
        Assert.True(
            StringList(result, member, id).Contains(field, StringComparer.Ordinal),
            $"{id}: '{member}' must name '{field}', because the start state already meets the expectation and only " +
            $"the response shows the {command}; {member} is {Member(result, member, id).GetRawText()}.");

    // The final value against one expectation, compared as ValueMatches compares them.
    private static void AssertValue(string id, string name, JsonElement expected, JsonNode? actual)
    {
        string actualText = actual?.ToJsonString() ?? "null";
        string message = $"{id}: '{name}' is {actualText} after the event, expected {expected.GetRawText()}.";
        Assert.True(ValueMatches(id, name, expected, actual), message);
    }

    // A value against one expectation: numbers numerically, booleans and strings exactly, and null as a null or absent
    // value. The JSON kinds must agree, so "25" never equals 25. Any other expected kind fails the case, because the
    // scenario file's shape changed.
    private static bool ValueMatches(string id, string name, JsonElement expected, JsonNode? actual)
    {
        JsonValueKind actualKind = actual?.GetValueKind() ?? JsonValueKind.Null;
        switch (expected.ValueKind)
        {
            case JsonValueKind.Null:
                return actualKind == JsonValueKind.Null;
            case JsonValueKind.Number:
                return actual is not null &&
                    actualKind == JsonValueKind.Number &&
                    actual.GetValue<double>() == expected.GetDouble();
            case JsonValueKind.True:
            case JsonValueKind.False:
                return actualKind == expected.ValueKind;
            case JsonValueKind.String:
                return actual is not null &&
                    actualKind == JsonValueKind.String &&
                    string.Equals(actual.GetValue<string>(), expected.GetString(), StringComparison.Ordinal);
            default:
                throw new XunitException(
                    $"{id}: the expectation for '{name}' is a JSON {expected.ValueKind}, which the harness does not " +
                    "compare, so the scenario file's shape changed.");
        }
    }

    // dialog: null requires no dialog; a string must equal the first dialog's message ordinally.
    private static void AssertDialog(JsonElement result, JsonElement expected, string id)
    {
        JsonElement dialogs = Member(result, "dialogs", id);
        switch (expected.ValueKind)
        {
            case JsonValueKind.Null:
                Assert.True(
                    dialogs.GetArrayLength() == 0,
                    $"{id}: expected no dialog; the response holds {dialogs.GetArrayLength()}: " +
                    $"{dialogs.GetRawText()}.");
                break;
            case JsonValueKind.String:
                Assert.True(dialogs.GetArrayLength() > 0, $"{id}: expected a dialog; the response holds none.");
                string message = RequireString(dialogs[0], "message", id);
                Assert.True(
                    string.Equals(message, expected.GetString(), StringComparison.Ordinal),
                    $"{id}: the first dialog reads '{message}', expected '{expected.GetString()}'.");
                break;
            default:
                throw new XunitException(
                    $"{id}: the dialog expectation is a JSON {expected.ValueKind}; only a string or null is known, " +
                    "so the scenario file's shape changed.");
        }
    }


    // EC-GEO-01: GEOCODE is skipped, so the response holds no effect and exactly one info diagnostic at the line the
    // seeded program puts it on, and the API logged it.
    private static async Task AssertGeocodeSkippedAndLoggedAsync(
        ApiFactory factory,
        ScenarioCase scenario,
        JsonElement result,
        CancellationToken ct)
    {
        string id = scenario.Id;
        foreach (string member in new[] { "enabled", "disabled", "cleared", "dialogs" })
        {
            JsonElement list = Member(result, member, id);
            Assert.True(
                list.GetArrayLength() == 0,
                $"{id}: a skipped GEOCODE must leave {member} empty: {list.GetRawText()}.");
        }

        JsonElement assigned = Member(result, "assigned", id);
        Assert.True(
            !assigned.EnumerateObject().Any(),
            $"{id}: a skipped GEOCODE must assign nothing: {assigned.GetRawText()}.");

        int expectedLine = await ReadGeocodeLineAsync(scenario.Form, ct);
        JsonElement diagnostics = Member(result, "diagnostics", id);
        Assert.True(
            diagnostics.GetArrayLength() == 1,
            $"{id}: expected exactly one diagnostic; the response holds {diagnostics.GetArrayLength()}: " +
            diagnostics.GetRawText());
        JsonElement diagnostic = diagnostics[0];
        Assert.Equal("info", RequireString(diagnostic, "severity", id));
        Assert.Equal(GeocodeCommand, RequireString(diagnostic, "command", id));
        Assert.Equal(scenario.Form, RequireString(diagnostic, "form", id));
        Assert.Equal(expectedLine, RequireInt(diagnostic, "line", id));

        IReadOnlyList<CapturedLogEntry> entries = factory.Logs.Entries;
        string captured = string.Join(
            " | ",
            entries.Select(entry => $"[{entry.Level}] {entry.Category}: {entry.Message}"));
        IReadOnlyList<CapturedLogEntry> matches =
            factory.Logs.Find(entry => MentionsGeocode(entry) && MentionsLine(entry, expectedLine));
        Assert.True(
            matches.Count > 0,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{id}: no log entry names {GeocodeCommand} and line {expectedLine}; {entries.Count} entries were " +
                $"captured: {captured}"));
    }

    // Every list member is an array, assigned is an object, each dialog carries a string message and each diagnostic
    // has the five members of the wire shape.
    private static void AssertResponseShape(JsonElement result, string id)
    {
        Assert.True(result.ValueKind == JsonValueKind.Object, $"{id}: the response is a JSON {result.ValueKind}.");
        foreach (string member in ResponseListMembers)
        {
            JsonElement list = Member(result, member, id);
            Assert.True(list.ValueKind == JsonValueKind.Array, $"{id}: '{member}' is a JSON {list.ValueKind}.");
        }

        JsonElement assigned = Member(result, "assigned", id);
        Assert.True(assigned.ValueKind == JsonValueKind.Object, $"{id}: 'assigned' is a JSON {assigned.ValueKind}.");

        foreach (JsonElement dialog in Member(result, "dialogs", id).EnumerateArray())
        {
            _ = RequireString(dialog, "message", id);
        }

        foreach (JsonElement diagnostic in Member(result, "diagnostics", id).EnumerateArray())
        {
            _ = RequireString(diagnostic, "severity", id);
            _ = RequireString(diagnostic, "form", id);
            _ = RequireInt(diagnostic, "line", id);
            _ = RequireString(diagnostic, "command", id);
            _ = RequireString(diagnostic, "message", id);
        }
    }

    // A warning (unsupported construct) or an error (runtime failure) means the seeded program did not run as the
    // scenario expects, so any of them fails the case with every offending diagnostic listed.
    private static void AssertNoWarningsOrErrors(JsonElement result, string id)
    {
        string[] offending = Member(result, "diagnostics", id).EnumerateArray()
            .Where(diagnostic => RequireString(diagnostic, "severity", id) is "warning" or "error")
            .Select(diagnostic => string.Create(
                CultureInfo.InvariantCulture,
                $"{RequireString(diagnostic, "severity", id)} at line {RequireInt(diagnostic, "line", id)}, command " +
                $"{RequireString(diagnostic, "command", id)}: {RequireString(diagnostic, "message", id)}"))
            .ToArray();
        Assert.True(
            offending.Length == 0,
            $"{id}: the field event returned {offending.Length} warning or error diagnostic(s): " +
            string.Join("; ", offending));
    }

    private static bool MentionsGeocode(CapturedLogEntry entry) =>
        entry.Message.Contains(GeocodeCommand, StringComparison.OrdinalIgnoreCase) ||
        entry.State.Any(
            pair => ValueText(pair.Value)?.Contains(GeocodeCommand, StringComparison.OrdinalIgnoreCase) == true);

    // The line as a structured state value, or as "line <n>" in the rendered message.
    private static bool MentionsLine(CapturedLogEntry entry, int line)
    {
        string lineText = line.ToString(CultureInfo.InvariantCulture);
        if (entry.State.Any(pair => pair.Value switch
            {
                int number => number == line,
                long number => number == line,
                string text => string.Equals(text, lineText, StringComparison.Ordinal),
                _ => false,
            }))
        {
            return true;
        }

        string phrase = "line " + lineText;
        int start = 0;
        while ((start = entry.Message.IndexOf(phrase, start, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            int end = start + phrase.Length;
            bool wordBefore = start > 0 && char.IsLetterOrDigit(entry.Message[start - 1]);
            bool digitAfter = end < entry.Message.Length && char.IsDigit(entry.Message[end]);
            if (!wordBefore && !digitAfter)
            {
                return true;
            }

            start = end;
        }

        return false;
    }

    private static string? ValueText(object? value) => value switch
    {
        null => null,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    // "DOB.After" becomes the field name DOB and the wire event FieldAfter; the split is at the last dot.
    private static (string FieldName, string EventKind) SplitEvent(string scenarioEvent, string id)
    {
        int dot = scenarioEvent.LastIndexOf('.');
        if (dot <= 0 || dot == scenarioEvent.Length - 1)
        {
            throw new XunitException($"{id}: event '{scenarioEvent}' has no '<Field>.<Kind>' form.");
        }

        string suffix = scenarioEvent[(dot + 1)..];
        if (!EventKinds.TryGetValue(suffix, out string? eventKind))
        {
            throw new XunitException($"{id}: event kind '{suffix}' is not Before, After or Click.");
        }

        return (scenarioEvent[..dot], eventKind);
    }

    // true becomes false; false, null or absent becomes true, as an unchecked box becomes checked.
    private static void ToggleCheckbox(JsonObject values, string fieldName, string id)
    {
        values.TryGetPropertyValue(fieldName, out JsonNode? current);
        bool toggled = (current?.GetValueKind() ?? JsonValueKind.Null) switch
        {
            JsonValueKind.True => false,
            JsonValueKind.False or JsonValueKind.Null => true,
            JsonValueKind kind => throw new XunitException(
                $"{id}: the clicked checkbox '{fieldName}' is given a JSON {kind}, not a boolean."),
        };
        values[fieldName] = toggled;
    }

    // Every field of the form starts enabled, with the value the request sends for it or null.
    private static Dictionary<string, FieldState> StartState(
        IReadOnlyDictionary<string, FormField> fields,
        JsonObject values)
    {
        var state = new Dictionary<string, FieldState>(StringComparer.Ordinal);
        foreach (string name in fields.Keys)
        {
            JsonNode? sent = values.TryGetPropertyValue(name, out JsonNode? value) ? value?.DeepClone() : null;
            state[name] = new FieldState(sent, Enabled: true);
        }

        return state;
    }

    // Every field on a page of the definition, by canonical name; a data field is one that is not unsupported and is
    // neither a label nor a group, as applyFieldEvent.ts decides it.
    private static IReadOnlyDictionary<string, FormField> ReadFields(JsonElement definition, string id)
    {
        var fields = new Dictionary<string, FormField>(StringComparer.Ordinal);
        foreach (JsonElement page in Member(definition, "pages", id).EnumerateArray())
        {
            foreach (JsonElement field in Member(page, "fields", id).EnumerateArray())
            {
                string name = RequireString(field, "name", id);
                string fieldType = RequireString(field, "fieldType", id);
                bool unsupported = Member(field, "unsupported", id).ValueKind == JsonValueKind.True;
                bool isData = !unsupported && fieldType is not ("label" or "group" or "unsupported");
                fields.TryAdd(name, new FormField(name, fieldType, isData));
            }
        }

        Assert.True(fields.Count > 0, $"{id}: the form definition lists no field.");
        return fields;
    }

    private static FormField RequireKnownField(
        IReadOnlyDictionary<string, FormField> fields,
        string name,
        string member,
        string id) =>
        fields.TryGetValue(name, out FormField? field)
            ? field
            : throw new XunitException(
                $"{id}: the response's {member} names '{name}', which is not the canonical name of a field of " +
                "the form.");

    private static string[] StringList(JsonElement result, string member, string id) =>
        Member(result, member, id).EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } name
                ? name
                : throw new XunitException($"{id}: '{member}' holds a JSON {item.ValueKind}, not a field name."))
            .ToArray();

    private static void AssertRecord(JsonElement record, string globalRecordId, int recStatus, string context)
    {
        Assert.True(
            string.Equals(RequireString(record, "globalRecordId", context), globalRecordId, StringComparison.Ordinal),
            $"{context} returned another record than {globalRecordId}.");
        Assert.True(
            RequireInt(record, "recStatus", context) == recStatus,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{context} returned recStatus {RequireInt(record, "recStatus", context)}, expected {recStatus}."));
    }

    private static async Task<JsonElement> GetJsonAsync(
        HttpClient client,
        string path,
        HttpStatusCode expected,
        string context,
        CancellationToken ct)
    {
        using HttpResponseMessage response = await client.GetAsync(path, ct);
        JsonElement body = await ApiTestClient.ReadJsonElementAsync(response, ct);
        Assert.True(
            response.StatusCode == expected,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{context}: GET {path} returned {(int)response.StatusCode}, expected {(int)expected}."));
        return body;
    }

    private static JsonElement Member(JsonElement element, string name, string context)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new XunitException(
                $"{context}: expected a JSON object holding '{name}'; found a JSON {element.ValueKind}.");
        }

        return element.TryGetProperty(name, out JsonElement value)
            ? value
            : throw new XunitException($"{context}: the JSON object has no member '{name}'.");
    }

    private static string RequireString(JsonElement element, string name, string context)
    {
        JsonElement value = Member(element, name, context);
        return value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text
            : throw new XunitException(
                $"{context}: '{name}' must be a non-empty string; it is a JSON {value.ValueKind}.");
    }

    private static int RequireInt(JsonElement element, string name, string context)
    {
        JsonElement value = Member(element, name, context);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number)
            ? number
            : throw new XunitException(
                $"{context}: '{name}' must be a 32-bit integer; it is a JSON {value.ValueKind}.");
    }


    // The oracle reproduces each EC-WEEK-* scenario's expected OnsetWeek from its given OnsetDate.
    private static void AssertOracleMatchesWeekScenarios()
    {
        ScenarioCase[] weeks = ScenarioCatalog.All
            .Where(c => c.Id.StartsWith(WeekScenarioPrefix, StringComparison.Ordinal))
            .ToArray();
        Assert.True(
            weeks.Length == 5,
            string.Create(
                CultureInfo.InvariantCulture,
                $"The catalogue holds {weeks.Length} {WeekScenarioPrefix}* scenarios, expected 5."));

        foreach (ScenarioCase week in weeks)
        {
            string text = RequireString(week.Given, "OnsetDate", week.Id);
            DateTime onsetDate =
                DateTime.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None);
            int expected = RequireInt(week.Expect, OnsetWeekField, week.Id);
            int actual = MmwrWeek(onsetDate);
            Assert.True(
                actual == expected,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{week.Id}: the test oracle gives week {actual} for {text}, the scenario expects {expected}."));
        }
    }

    // EPIWEEK with the default first day of the week (Sunday), ported from epiweek(d, fdow=0) in
    // testdata/tools/epi_functions.py: an independent reference, so the check does not trust the code under test.
    private static int MmwrWeek(DateTime value)
    {
        DateTime date = value.Date;
        return (date - MmwrStart(date)).Days / 7 + 1;
    }

    // _mmwr_start: the start of the MMWR year that holds the date, chosen from the year before, the year and the
    // year after.
    private static DateTime MmwrStart(DateTime date)
    {
        DateTime previous = MmwrYearStart(date.Year - 1);
        DateTime current = MmwrYearStart(date.Year);
        DateTime next = MmwrYearStart(date.Year + 1);
        if ((current - date).Days > 0)
        {
            return previous;
        }

        return (next - date).Days > 0 ? current : next;
    }

    // _mmwr_start_year with fdow = 0: the Sunday on or before 1 January when 1 January falls on Sunday to Wednesday,
    // otherwise the Sunday after it. .NET DayOfWeek already numbers Sunday 0.
    private static DateTime MmwrYearStart(int year)
    {
        var januaryFirst = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        int dayOfWeek = (int)januaryFirst.DayOfWeek;
        return dayOfWeek <= 3 ? januaryFirst.AddDays(-dayOfWeek) : januaryFirst.AddDays(7 - dayOfWeek);
    }

    private static string[] TablesOf(string form, string id) =>
        FormTables.TryGetValue(form, out string[]? tables)
            ? tables
            : throw new XunitException($"{id}: '{form}' is not a seeded form whose tables the harness counts.");

    // Brackets one of the fixed table names this class reads; any other name is refused before SQL is built.
    private static string Quote(string table) =>
        ReadableTables.Contains(table, StringComparer.Ordinal)
            ? "[" + table + "]"
            : throw new ArgumentException($"'{table}' is not one of the tables this class reads.", nameof(table));

    private static async Task<long[]> CountRowsAsync(IReadOnlyList<string> tables, CancellationToken ct)
    {
        await using SqlConnection connection = await TestDatabase.OpenConnectionAsync(ct);
        var counts = new long[tables.Count];
        for (int index = 0; index < tables.Count; index++)
        {
            await using var command = new SqlCommand($"SELECT COUNT_BIG(*) FROM {Quote(tables[index])};", connection);
            counts[index] = ToCount(await command.ExecuteScalarAsync(ct), tables[index]);
        }

        return counts;
    }

    // The 1-based position of a record over every row of the base table: the legacy COUNT of UniqueKey <= k.
    private static async Task<long> CountRowsUpToAsync(string form, int uniqueKey, CancellationToken ct)
    {
        await using SqlConnection connection = await TestDatabase.OpenConnectionAsync(ct);
        await using var command = new SqlCommand(
            $"SELECT COUNT_BIG(*) FROM {Quote(form)} WHERE [UniqueKey] <= @uniqueKey;",
            connection);
        command.Parameters.Add("@uniqueKey", SqlDbType.Int).Value = uniqueKey;
        return ToCount(await command.ExecuteScalarAsync(ct), form);
    }

    private static async Task<DeletedRecord?> ReadFirstDeletedRecordAsync(string form, CancellationToken ct)
    {
        await using SqlConnection connection = await TestDatabase.OpenConnectionAsync(ct);
        await using var command = new SqlCommand(
            $"SELECT TOP (1) [GlobalRecordId], [UniqueKey] FROM {Quote(form)} WHERE [RecStatus] = @recStatus " +
            "ORDER BY [UniqueKey];",
            connection);
        command.Parameters.Add("@recStatus", SqlDbType.SmallInt).Value = (short)DeletedRecStatus;
        await using SqlDataReader reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new DeletedRecord(reader.GetString(0), reader.GetInt32(1))
            : null;
    }

    // FoodHistory2 rows with both an onset date and a stored week, limited to records the base table holds, which are
    // the records GET by id can open, in UniqueKey order.
    private static async Task<IReadOnlyList<StoredOnsetWeek>> ReadStoredOnsetWeeksAsync(CancellationToken ct)
    {
        await using SqlConnection connection = await TestDatabase.OpenConnectionAsync(ct);
        await using var command = new SqlCommand(
            $"SELECT p.[GlobalRecordId], p.[OnsetDate], p.[OnsetWeek] FROM {Quote(FoodHistory2)} AS p " +
            $"INNER JOIN {Quote(FoodHistory)} AS b ON b.[GlobalRecordId] = p.[GlobalRecordId] " +
            "WHERE p.[OnsetDate] IS NOT NULL AND p.[OnsetWeek] IS NOT NULL ORDER BY b.[UniqueKey];",
            connection);
        await using SqlDataReader reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<StoredOnsetWeek>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new StoredOnsetWeek(reader.GetString(0), reader.GetDateTime(1), reader.GetDouble(2)));
        }

        return rows;
    }

    // The 1-based line of the first GEOCODE statement in the form's stored program, computed rather than hard-coded
    // so a reformatted program moves the expectation with it.
    private static async Task<int> ReadGeocodeLineAsync(string form, CancellationToken ct)
    {
        await using SqlConnection connection = await TestDatabase.OpenConnectionAsync(ct);
        await using var command = new SqlCommand(
            $"SELECT [CheckCode] FROM {Quote(MetaViews)} WHERE [Name] = @name;",
            connection);
        command.Parameters.Add("@name", SqlDbType.NVarChar, 64).Value = form;
        var programs = new List<string>();
        await using (SqlDataReader reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                programs.Add(reader.IsDBNull(0) ? string.Empty : reader.GetString(0));
            }
        }

        Assert.True(
            programs.Count == 1,
            string.Create(CultureInfo.InvariantCulture, $"[{MetaViews}] holds {programs.Count} rows named {form}."));
        string[] lines = programs[0].Split('\n');
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index].TrimEnd('\r').Trim();
            if (line.StartsWith(GeocodeCommand, StringComparison.OrdinalIgnoreCase) &&
                (line.Length == GeocodeCommand.Length || char.IsWhiteSpace(line[GeocodeCommand.Length])))
            {
                return index + 1;
            }
        }

        throw new XunitException($"The stored {form} program holds no {GeocodeCommand} statement.");
    }

    private static long ToCount(object? scalar, string table) =>
        scalar is long count
            ? count
            : throw new InvalidOperationException($"COUNT_BIG(*) over [{table}] returned no bigint.");

    /// <summary>
    /// A field of the form definition: its canonical name, its wire field type and whether it holds data.
    /// </summary>
    private sealed record FormField(string Name, string FieldType, bool IsData);

    /// <summary>One field's value and check-code flag in the computed final state.</summary>
    private readonly record struct FieldState(JsonNode? Value, bool Enabled);

    /// <summary>An expectation the start state already meets, with what the response must show for its field.</summary>
    private readonly record struct RequiredEffect(string Field, EffectKind Kind);

    /// <summary>
    /// How the response accounts for an expectation the start state already meets: an effect it must name, or a sent
    /// value the program must leave alone.
    /// </summary>
    private enum EffectKind
    {
        /// <summary>The field is named in <c>enabled</c>.</summary>
        Enabled,

        /// <summary>The field is named in <c>cleared</c>.</summary>
        Cleared,

        /// <summary>The field is a present member of <c>assigned</c> whose value matches the expected value.</summary>
        Assigned,

        /// <summary>The field is named in neither <c>cleared</c> nor <c>assigned</c>; its sent value stays.</summary>
        Retained,
    }

    /// <summary>The first soft-deleted base row of a form.</summary>
    private readonly record struct DeletedRecord(string GlobalRecordId, int UniqueKey);

    /// <summary>A stored onset date and week of one FoodHistory record.</summary>
    private sealed record StoredOnsetWeek(string GlobalRecordId, DateTime OnsetDate, double OnsetWeek);
}

/// <summary>
/// The parity ledger's record of every legacy defect a scenario keeps (AAP 0.7.4 "Legacy defects", 0.9.4 "Legacy
/// defects kept"). It reads only the scenario file and <c>docs/parity-notes.md</c>, so it carries no
/// <c>Integration</c> trait and runs with no database.
/// </summary>
public sealed class LegacyDefectLedgerTests
{
    private const string SectionHeading = "## Legacy defects kept";
    private const string BehaviourLine = "- **Legacy behaviour:**";
    private const string DefectLine = "- **Why it is a defect:**";

    private static readonly string[] ExpectedLegacyDefectIds = ["LY-02", "LY-03", "EC-AGE-03"];

    [Fact]
    public void LegacyDefectsListed()
    {
        string[] ids = ScenarioCatalog.All.Where(c => c.LegacyDefect).Select(c => c.Id).ToArray();
        Assert.True(
            ids.Order(StringComparer.Ordinal)
                .SequenceEqual(ExpectedLegacyDefectIds.Order(StringComparer.Ordinal), StringComparer.Ordinal),
            $"The scenarios with legacyDefect: true are [{string.Join(", ", ids)}], expected " +
            $"[{string.Join(", ", ExpectedLegacyDefectIds)}].");

        string[] lines = ScenarioCatalog.ParityNotesSection(SectionHeading).Split('\n');
        var failures = new List<string>();
        foreach (string id in ids)
        {
            int[] headings = Enumerable.Range(0, lines.Length).Where(i => IsHeadingFor(lines[i], id)).ToArray();
            if (headings.Length != 1)
            {
                failures.Add(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{id}: '{SectionHeading}' holds {headings.Length} heading lines '### {id}', expected 1."));
                continue;
            }

            int end = headings[0] + 1;
            while (end < lines.Length &&
                !lines[end].StartsWith("### ", StringComparison.Ordinal) &&
                !lines[end].StartsWith("## ", StringComparison.Ordinal))
            {
                end++;
            }

            string[] body = lines[(headings[0] + 1)..end];
            foreach (string required in new[] { BehaviourLine, DefectLine })
            {
                if (!body.Any(line => HasText(line, required)))
                {
                    failures.Add($"{id}: the '### {id}' subsection has no line '{required} <text>'.");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    // "### <id>" after trimming trailing whitespace, optionally followed by text after a space, an em dash or a colon.
    private static bool IsHeadingFor(string line, string id)
    {
        string heading = "### " + id;
        string text = line.TrimEnd();
        return string.Equals(text, heading, StringComparison.Ordinal) ||
            (text.StartsWith(heading, StringComparison.Ordinal) && text[heading.Length] is ' ' or '\u2014' or ':');
    }

    private static bool HasText(string line, string prefix) =>
        line.StartsWith(prefix, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(line[prefix.Length..]);
}
