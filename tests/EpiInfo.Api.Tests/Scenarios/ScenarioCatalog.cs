using System.Text.Json;
using System.Text.RegularExpressions;
using EpiInfo.Api.Tests.Infrastructure;

namespace EpiInfo.Api.Tests.Scenarios;

/// <summary>
/// One entry of <c>testdata/scenarios/check_code_scenarios.json</c>, read where the file lies.
/// </summary>
/// <param name="Id">The scenario id, for example <c>EC-AGE-01</c>; unique in the file.</param>
/// <param name="InScope">Whether the scenario counts toward the 24 in-scope cases of SC-9.</param>
/// <param name="Form">The form name, or <c>*</c> for a data case that covers both forms.</param>
/// <param name="Event">
/// <c>&lt;Field&gt;.&lt;Before|After|Click&gt;</c> for a field event, or <c>save</c>, <c>load</c> or
/// <c>navigate</c>.
/// </param>
/// <param name="Given">The scenario's input values, a JSON object that outlives the parsed document.</param>
/// <param name="Expect">The scenario's expected outcome, a JSON object that outlives the parsed document.</param>
/// <param name="LegacyDefect">Whether the expected value keeps a legacy defect the parity notes list.</param>
/// <param name="Source">The legacy source the expected value is derived from.</param>
public sealed record ScenarioCase(
    string Id,
    bool InScope,
    string Form,
    string Event,
    JsonElement Given,
    JsonElement Expect,
    bool LegacyDefect,
    string Source);

/// <summary>
/// The check-code scenario catalogue (AAP 0.7.4) and the P1 tiering of its field-event cases (AAP 0.7.2). The file is
/// located only through <see cref="RepositoryPaths.ScenarioFile"/>, read once, and never copied, embedded or written.
/// </summary>
/// <remarks>
/// Loading is strict: a missing property, a JSON kind other than the one the file uses, an unknown or repeated
/// property and a repeated id each throw <see cref="InvalidDataException"/>, so a change in the file's shape fails
/// every test that reads the catalogue instead of silently dropping or misreading a case.
/// </remarks>
public static class ScenarioCatalog
{
    /// <summary>
    /// The out-of-scope <c>GEOCODE</c> scenario, which the P1 theory runs as its "logged and skipped" check.
    /// </summary>
    public const string GeocodeId = "EC-GEO-01";

    /// <summary>
    /// The P1 scenario ids whose feature is not delivered; the P1 field-event theory reports each one as its own
    /// skipped case.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ScenarioCatalogTests.PendingIdsAreP1Only"/> asserts that the array repeats no id, names no P0
    /// scenario (<c>EC-VAL-*</c>, <c>DATA-*</c>) and holds only ids from <see cref="P1Ids"/>; that the
    /// <see cref="RunnableIds"/> and <see cref="PendingIds"/> rows are disjoint and together are exactly
    /// <see cref="P1Ids"/>; and that every id in it appears literally under <c>## Undelivered items</c> in
    /// <c>docs/parity-notes.md</c>.
    /// </para>
    /// <para>
    /// That test cannot tell which features are delivered, so keeping the array complete is the maintainers' duty: it
    /// names every P1 id whose feature is not delivered, each id is removed once its feature passes, and the array is
    /// empty once P1 is complete.
    /// </para>
    /// </remarks>
    public static readonly string[] PendingP1Ids = [];

    private const string ScenariosProperty = "scenarios";
    private const string NoteProperty = "note";

    private static readonly string[] EntryProperties =
        ["id", "form", "event", "given", "expect", "source", "legacyDefect", "inScope", NoteProperty];

    // \z rather than $, which would also accept a trailing newline after the event kind.
    private static readonly Regex FieldEventPattern = new(
        @"^[A-Za-z_][A-Za-z0-9_]*\.(Before|After|Click)\z",
        RegexOptions.CultureInvariant);

    private static readonly Lazy<IReadOnlyList<ScenarioCase>> Cases =
        new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Every entry of the scenario file, in file order.</summary>
    /// <exception cref="FileNotFoundException">The scenario file does not exist.</exception>
    /// <exception cref="InvalidDataException">The file's shape differs from the catalogue's.</exception>
    public static IReadOnlyList<ScenarioCase> All => Cases.Value;

    /// <summary>
    /// The in-scope field-event ids, those whose <see cref="ScenarioCase.Event"/> is
    /// <c>&lt;Field&gt;.&lt;Before|After|Click&gt;</c>, in file order.
    /// </summary>
    public static IReadOnlyList<string> FieldEventIds =>
        All.Where(c => c.InScope && IsFieldEvent(c)).Select(c => c.Id).ToArray();

    /// <summary>
    /// The ids the P1 field-event theory runs, in file order: <see cref="FieldEventIds"/> plus
    /// <see cref="GeocodeId"/>.
    /// </summary>
    public static IReadOnlyList<string> P1Ids =>
        All.Where(c => (c.InScope && IsFieldEvent(c)) || string.Equals(c.Id, GeocodeId, StringComparison.Ordinal))
            .Select(c => c.Id)
            .ToArray();

    /// <summary>
    /// The delivered P1 cases: <see cref="P1Ids"/> minus <see cref="PendingP1Ids"/>, in file order, one
    /// <see cref="string"/> id per row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rows are materialised and hold only strings, which xUnit v2 can serialise, so discovery pre-enumerates
    /// them and reports each id as its own test case.
    /// </para>
    /// <para>
    /// <c>CheckCodeScenarioTests</c> attaches this source and <see cref="PendingIds"/> to one theory method, the
    /// second with a <c>Skip</c> reason. In xUnit v2 a theory whose only data source yields no rows reports a
    /// "No data found" failure, so a method per source would fail whenever P1 is complete (no pending rows) or wholly
    /// pending (no runnable rows). On one method the union is always the 20 <see cref="P1Ids"/>, whatever the
    /// delivery state, as AAP 0.7.2 has the field-event theory take its rows from two xUnit v2 data sources.
    /// </para>
    /// </remarks>
    public static IEnumerable<object[]> RunnableIds =>
        P1Ids.Where(id => !PendingP1Ids.Contains(id, StringComparer.Ordinal))
            .Select(id => new object[] { id })
            .ToArray();

    /// <summary>
    /// The pending P1 cases: <see cref="PendingP1Ids"/> in array order, one <see cref="string"/> id per row, reported
    /// as skipped by the theory that also takes <see cref="RunnableIds"/>.
    /// </summary>
    public static IEnumerable<object[]> PendingIds =>
        PendingP1Ids.Select(id => new object[] { id }).ToArray();

    /// <summary>The scenario with the given id, matched ordinally.</summary>
    /// <param name="id">The scenario id, for example <c>DATA-01</c>.</param>
    /// <returns>The catalogue entry.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="id"/> is <see langword="null"/>.</exception>
    /// <exception cref="KeyNotFoundException">The file holds no scenario with that id.</exception>
    public static ScenarioCase Get(string id)
    {
        ArgumentNullException.ThrowIfNull(id);

        foreach (ScenarioCase scenario in All)
        {
            if (string.Equals(scenario.Id, id, StringComparison.Ordinal))
            {
                return scenario;
            }
        }

        throw new KeyNotFoundException($"The scenario '{id}' is not in '{RepositoryPaths.ScenarioFile}'.");
    }

    /// <summary>
    /// Whether the scenario is a field event: its <see cref="ScenarioCase.Event"/> is a field name followed by
    /// <c>.Before</c>, <c>.After</c> or <c>.Click</c>, matched case-sensitively.
    /// </summary>
    /// <param name="scenario">The scenario.</param>
    /// <returns><see langword="true"/> for a field event.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="scenario"/> is <see langword="null"/>.</exception>
    public static bool IsFieldEvent(ScenarioCase scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        return FieldEventPattern.IsMatch(scenario.Event);
    }

    /// <summary>
    /// The body of one <c>## </c> section of <c>docs/parity-notes.md</c>: the text after the heading line, up to the
    /// next line starting with <c>## </c> or the end of the file. Deeper headings such as <c>### </c> stay inside it.
    /// </summary>
    /// <param name="heading">The heading line, for example <c>## Undelivered items</c>.</param>
    /// <returns>The section body with <c>\n</c> line endings, without the heading line.</returns>
    /// <exception cref="ArgumentException"><paramref name="heading"/> is null or blank.</exception>
    /// <exception cref="FileNotFoundException">The parity notes do not exist.</exception>
    /// <exception cref="InvalidDataException">No line, or more than one, equals the heading.</exception>
    public static string ParityNotesSection(string heading)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(heading);

        string path = RepositoryPaths.ParityNotes;
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"The parity notes '{path}' do not exist.", path);
        }

        string wanted = heading.TrimEnd();
        string[] lines = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int[] matches = Enumerable.Range(0, lines.Length)
            .Where(i => string.Equals(lines[i].TrimEnd(), wanted, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidDataException(matches.Length == 0
                ? $"'{path}' has no heading line '{wanted}'."
                : $"'{path}' has {matches.Length} heading lines '{wanted}', so the section is ambiguous.");
        }

        int start = matches[0] + 1;
        int end = start;
        while (end < lines.Length && !lines[end].StartsWith("## ", StringComparison.Ordinal))
        {
            end++;
        }

        return string.Join('\n', lines, start, end - start);
    }

    private static IReadOnlyList<ScenarioCase> Load()
    {
        string path = RepositoryPaths.ScenarioFile;
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"The check-code scenario catalogue '{path}' does not exist.", path);
        }

        string text = File.ReadAllText(path);
        JsonDocument document;
        try
        {
            // A repeated property would otherwise resolve silently to one of its values.
            document = JsonDocument.Parse(text, new JsonDocumentOptions { AllowDuplicateProperties = false });
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"'{path}' is not well-formed JSON.", exception);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"The root of '{path}' must be a JSON object, found {root.ValueKind}.");
            }

            foreach (JsonProperty property in root.EnumerateObject())
            {
                bool known = property.NameEquals(ScenariosProperty) ||
                    (property.NameEquals(NoteProperty) && property.Value.ValueKind == JsonValueKind.String);
                if (!known)
                {
                    throw new InvalidDataException(
                        $"The root of '{path}' holds '{property.Name}' as {property.Value.ValueKind}; " +
                        $"only the array '{ScenariosProperty}' and the string '{NoteProperty}' are expected.");
                }
            }

            if (!root.TryGetProperty(ScenariosProperty, out JsonElement scenarios) ||
                scenarios.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException($"The root of '{path}' must hold an array '{ScenariosProperty}'.");
            }

            var cases = new List<ScenarioCase>(scenarios.GetArrayLength());
            var ids = new HashSet<string>(StringComparer.Ordinal);
            int index = 0;
            foreach (JsonElement entry in scenarios.EnumerateArray())
            {
                ScenarioCase scenario = ReadEntry(entry, index, path);
                if (!ids.Add(scenario.Id))
                {
                    throw new InvalidDataException(
                        $"Entry {index} of '{path}' repeats the scenario id '{scenario.Id}'.");
                }

                cases.Add(scenario);
                index++;
            }

            return cases.AsReadOnly();
        }
    }

    private static ScenarioCase ReadEntry(JsonElement entry, int index, string path)
    {
        if (entry.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                $"Entry {index} of '{path}' must be a JSON object, found {entry.ValueKind}.");
        }

        foreach (JsonProperty property in entry.EnumerateObject())
        {
            if (!EntryProperties.Contains(property.Name, StringComparer.Ordinal))
            {
                throw new InvalidDataException(
                    $"Entry {index} of '{path}' holds the unknown property '{property.Name}'.");
            }
        }

        if (entry.TryGetProperty(NoteProperty, out JsonElement note) && note.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException(
                $"Entry {index} of '{path}': property '{NoteProperty}' must be a JSON string, found {note.ValueKind}.");
        }

        return new ScenarioCase(
            Id: RequiredString(entry, "id", index, path),
            InScope: RequiredBoolean(entry, "inScope", index, path),
            Form: RequiredString(entry, "form", index, path),
            Event: RequiredString(entry, "event", index, path),
            Given: Required(entry, "given", JsonValueKind.Object, index, path).Clone(),
            Expect: Required(entry, "expect", JsonValueKind.Object, index, path).Clone(),
            LegacyDefect: RequiredBoolean(entry, "legacyDefect", index, path),
            Source: RequiredString(entry, "source", index, path));
    }

    private static JsonElement Required(JsonElement entry, string name, JsonValueKind kind, int index, string path)
    {
        if (!entry.TryGetProperty(name, out JsonElement value))
        {
            throw new InvalidDataException($"Entry {index} of '{path}' has no property '{name}'.");
        }

        if (value.ValueKind != kind)
        {
            throw new InvalidDataException(
                $"Entry {index} of '{path}': property '{name}' must be a JSON {kind}, found {value.ValueKind}.");
        }

        return value;
    }

    private static string RequiredString(JsonElement entry, string name, int index, string path)
    {
        string text = Required(entry, name, JsonValueKind.String, index, path).GetString()!;
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidDataException($"Entry {index} of '{path}': property '{name}' must not be blank.");
        }

        return text;
    }

    private static bool RequiredBoolean(JsonElement entry, string name, int index, string path)
    {
        if (!entry.TryGetProperty(name, out JsonElement value))
        {
            throw new InvalidDataException($"Entry {index} of '{path}' has no property '{name}'.");
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException(
                $"Entry {index} of '{path}': property '{name}' must be a JSON boolean, found {value.ValueKind}."),
        };
    }
}

/// <summary>
/// The catalogue's own checks: the file's counts as AAP 0.7.4 reconciles them, and the P1 tiering rules of
/// AAP 0.7.2.
/// </summary>
/// <remarks>
/// The class reads only the scenario file and the parity notes. It carries no <c>Integration</c> trait and joins no
/// database collection, so <c>--filter "Category!=Integration"</c> selects it and it runs with no database.
/// </remarks>
public sealed class ScenarioCatalogTests
{
    private const string LoadId = "DATA-01";
    private const string NavigateId = "DATA-02";

    private static readonly string[] ExpectedSaveIds = ["EC-VAL-01", "EC-VAL-02", "EC-VAL-03"];

    [Fact]
    public void CatalogCounts()
    {
        IReadOnlyList<ScenarioCase> all = ScenarioCatalog.All;
        Assert.True(all.Count == 25, $"The scenario file holds {all.Count} entries, expected 25: {List(all)}.");

        string[] repeated = Repeated(all.Select(c => c.Id));
        Assert.True(repeated.Length == 0, $"Repeated scenario ids: {List(repeated)}.");

        ScenarioCase[] inScope = all.Where(c => c.InScope).ToArray();
        Assert.True(
            inScope.Length == 24,
            $"{inScope.Length} scenarios are in scope, expected 24; out of scope: {List(all.Where(c => !c.InScope))}.");

        string[] fieldEvents = inScope.Where(ScenarioCatalog.IsFieldEvent).Select(c => c.Id).ToArray();
        Assert.True(
            fieldEvents.Length == 19,
            $"{fieldEvents.Length} in-scope field events, expected 19: {List(fieldEvents)}.");
        Assert.Equal(fieldEvents, ScenarioCatalog.FieldEventIds);

        string[] saves = inScope.Where(c => c.Event == "save").Select(c => c.Id).ToArray();
        Assert.True(
            saves.Order(StringComparer.Ordinal).SequenceEqual(ExpectedSaveIds, StringComparer.Ordinal),
            $"In-scope save scenarios are {List(saves)}, expected {List(ExpectedSaveIds)}.");

        string[] dataCases = inScope.Where(c => c.Event is "load" or "navigate").Select(c => c.Id).ToArray();
        Assert.True(
            dataCases.Order(StringComparer.Ordinal).SequenceEqual([LoadId, NavigateId], StringComparer.Ordinal),
            $"In-scope data scenarios are {List(dataCases)}, expected {LoadId} and {NavigateId}.");
        Assert.Equal("load", ScenarioCatalog.Get(LoadId).Event);
        Assert.Equal("navigate", ScenarioCatalog.Get(NavigateId).Event);

        string[] grouped = [.. fieldEvents, .. saves, .. dataCases];
        string[] ungrouped = inScope.Select(c => c.Id).Except(grouped, StringComparer.Ordinal).ToArray();
        Assert.True(
            ungrouped.Length == 0,
            $"In-scope scenarios outside the field-event, save and data groups: {List(ungrouped)}.");
        Assert.True(
            grouped.Length == inScope.Length,
            $"The groups hold {fieldEvents.Length} + {saves.Length} + {dataCases.Length} ids for {inScope.Length} " +
            "in-scope scenarios.");

        ScenarioCase geocode = ScenarioCatalog.Get(ScenarioCatalog.GeocodeId);
        Assert.False(geocode.InScope, $"{ScenarioCatalog.GeocodeId} must be out of scope.");
        Assert.Equal("GetCoordinates.Click", geocode.Event);

        IReadOnlyList<string> p1 = ScenarioCatalog.P1Ids;
        Assert.True(p1.Count == 20, $"{p1.Count} P1 ids, expected 20: {List(p1)}.");
        Assert.True(
            p1.Order(StringComparer.Ordinal)
                .SequenceEqual(fieldEvents.Append(ScenarioCatalog.GeocodeId).Order(StringComparer.Ordinal),
                    StringComparer.Ordinal),
            $"P1 ids {List(p1)} are not the in-scope field events plus {ScenarioCatalog.GeocodeId}.");
    }

    [Fact]
    public void PendingIdsAreP1Only()
    {
        string[] pending = ScenarioCatalog.PendingP1Ids;

        string[] repeated = Repeated(pending);
        Assert.True(repeated.Length == 0, $"{nameof(ScenarioCatalog.PendingP1Ids)} repeats {List(repeated)}.");

        string[] p0 = pending
            .Where(id => id.StartsWith("EC-VAL-", StringComparison.Ordinal) ||
                id.StartsWith("DATA-", StringComparison.Ordinal))
            .ToArray();
        Assert.True(p0.Length == 0, $"{nameof(ScenarioCatalog.PendingP1Ids)} names P0 scenarios: {List(p0)}.");

        IReadOnlyList<string> p1 = ScenarioCatalog.P1Ids;
        string[] notP1 = pending.Where(id => !p1.Contains(id, StringComparer.Ordinal)).ToArray();
        Assert.True(notP1.Length == 0, $"{nameof(ScenarioCatalog.PendingP1Ids)} names non-P1 ids: {List(notP1)}.");

        string[] runnable = Rows(ScenarioCatalog.RunnableIds);
        string[] pendingRows = Rows(ScenarioCatalog.PendingIds);
        string[] both = runnable.Intersect(pendingRows, StringComparer.Ordinal).ToArray();
        Assert.True(both.Length == 0, $"Ids both runnable and pending: {List(both)}.");

        string[] union = [.. runnable, .. pendingRows];
        string[] missing = p1.Except(union, StringComparer.Ordinal).ToArray();
        string[] extra = union.Except(p1, StringComparer.Ordinal).ToArray();
        Assert.True(
            missing.Length == 0 && extra.Length == 0 && union.Length == p1.Count,
            $"Runnable and pending rows ({union.Length}) differ from the {p1.Count} P1 ids; " +
            $"missing: {List(missing)}; extra: {List(extra)}; repeated: {List(Repeated(union))}.");

        string undelivered = ScenarioCatalog.ParityNotesSection("## Undelivered items");
        string[] unlisted = pending.Where(id => !undelivered.Contains(id, StringComparison.Ordinal)).ToArray();
        Assert.True(
            unlisted.Length == 0,
            $"Pending ids absent from the parity notes 'Undelivered items' section: {List(unlisted)}.");
    }

    private static string[] Rows(IEnumerable<object[]> rows) =>
        rows.Select(row => Assert.IsType<string>(Assert.Single(row))).ToArray();

    private static string[] Repeated(IEnumerable<string> ids) =>
        ids.GroupBy(id => id, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();

    private static string List(IEnumerable<ScenarioCase> cases) => List(cases.Select(c => c.Id));

    private static string List(IEnumerable<string> ids)
    {
        string joined = string.Join(", ", ids);
        return joined.Length == 0 ? "none" : joined;
    }
}
