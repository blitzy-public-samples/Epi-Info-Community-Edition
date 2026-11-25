using System.Globalization;
using System.Net;
using System.Text.Json;
using EpiInfo.Api.Endpoints;
using EpiInfo.Api.Json;
using EpiInfo.Api.Services;
using EpiInfo.Api.Tests.Infrastructure;
using EpiInfo.CheckCode;
using EpiInfo.Domain.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EpiInfo.Api.Tests.Reads;

/// <summary>
/// The P1 proof for the form-definition route <c>GET /api/forms/{viewId}</c>, mapped by <see cref="FormEndpoints"/>
/// and served by <see cref="FormService.GetDefinitionAsync"/>: with the interpreter delivered, the response's
/// <c>unsupportedCheckCode</c> holds the static <c>GEOCODE</c> diagnostic, the diagnostic is logged once without its
/// message or any program value, and the program is analysed but never run.
/// </summary>
/// <remarks>
/// <para>
/// It runs over <see cref="FakeDataStore"/> because the property it proves needs a program the seeded database does
/// not hold: runnable statements carrying a sentinel beside exactly one <c>GEOCODE</c>, on a form whose name and view
/// id are known. The fake also records every store call, so the test needs no SQL Server: the class carries no
/// <c>Integration</c> trait and joins no database collection, which keeps it independent of the database-backed P0
/// definition cases and selected by <c>--filter "Category!=Integration"</c>.
/// </para>
/// <para>
/// The request goes through the real <see cref="FormEndpoints"/> route, on an in-memory TestServer host the test
/// composes from the API's own pieces: the committed <c>appsettings.json</c> and <c>appsettings.{environment}.json</c>
/// of the API project as its configuration; the API's JSON settings (<see cref="JsonSetup.AddApiJson"/>);
/// <see cref="FormService"/> over the fake store; the real interpreter behind <see cref="CountingCheckCodeEngine"/>;
/// and the JSON console, with <see cref="CapturingLoggerProvider"/> added as one more provider under the committed
/// <c>Logging</c> filters. The response is therefore the product of routing, parameter binding, the service and the
/// API's JSON writer, and the captured entries are exactly what that logging pipeline writes. Each environment whose
/// logging configuration is committed is checked.
/// </para>
/// </remarks>
public sealed class FormDefinitionCheckCodeTests
{
    private const string FormName = "FakeCheckCode";

    private const string GeocodeCommand = "GEOCODE";

    private const string DiagnosticEventName = "CheckCodeDiagnostic";

    private const int DiagnosticEventId = 1;

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public async Task DefinitionReportsAndLogsGeocodeWithoutRunningTheProgram(string environment)
    {
        // The sentinel stands for program text the redaction policy keeps out of the log: a string the program would
        // assign and show only if it ran.
        string sentinel = "Sentinel-Geo-" + Guid.NewGuid().ToString("N");
        string program = SyntheticProgram(sentinel);
        int geocodeLine = SingleLineNumber(program, GeocodeCommand + " ");

        var store = new FakeDataStore(FakeForms.WithCheckCode(program));
        var engine = new CountingCheckCodeEngine(new CheckCodeEngine());
        using var logs = new CapturingLoggerProvider();
        await using WebApplication app = await StartHostAsync(environment, store, engine, logs);
        using HttpClient client = app.GetTestClient();

        // Starting the host is not part of the read, so only the request below is counted.
        engine.Reset();
        store.ClearCalls();

        using HttpResponseMessage response = await client.GetAsync(
            string.Create(CultureInfo.InvariantCulture, $"/api/forms/{FakeForms.CheckCodeViewId}"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ApiTestClient.JsonMediaType, response.Content.Headers.ContentType?.MediaType);
        JsonElement definition = await ApiTestClient.ReadJsonElementAsync(response);
        Assert.Equal(JsonValueKind.Object, definition.ValueKind);
        Assert.Equal(FakeForms.CheckCodeViewId, definition.GetProperty("viewId").GetInt32());
        Assert.Equal(FormName, definition.GetProperty("name").GetString());

        // The static analysis knows the GEOCODE from the text alone; the program holds nothing unsupported, and a GET
        // never yields a runtime error. The members and the severity are spelt as the API's JSON settings write them.
        JsonElement unsupported = definition.GetProperty("unsupportedCheckCode");
        Assert.Equal(JsonValueKind.Array, unsupported.ValueKind);
        JsonElement diagnostic = Assert.Single(unsupported.EnumerateArray());
        Assert.Equal("info", diagnostic.GetProperty("severity").GetString());
        Assert.Equal(FormName, diagnostic.GetProperty("form").GetString());
        JsonElement line = diagnostic.GetProperty("line");
        Assert.Equal(JsonValueKind.Number, line.ValueKind);
        Assert.Equal(geocodeLine, line.GetInt32());
        Assert.Equal(GeocodeCommand, diagnostic.GetProperty("command").GetString());
        JsonElement messageElement = diagnostic.GetProperty("message");
        Assert.Equal(JsonValueKind.String, messageElement.ValueKind);
        string message = messageElement.GetString() ?? string.Empty;
        Assert.False(string.IsNullOrWhiteSpace(message), "The GEOCODE diagnostic has no message.");

        // Analysed once and never evaluated, and the read wrote nothing.
        Assert.Equal(1, engine.AnalyzeCalls);
        Assert.Equal(0, engine.RunCalls);
        Assert.Contains(store.Calls, call => call.Method == nameof(IDataStore.GetCheckCodeAsync));
        Assert.Empty(store.WriteCalls);

        // One entry for the one diagnostic, through the fixed template: form, view id, line and command only.
        CapturedLogEntry entry = Assert.Single(
            logs.Find(captured => captured.Category == typeof(FormService).FullName));
        Assert.Equal(DiagnosticEventId, entry.EventId.Id);
        Assert.Equal(DiagnosticEventName, entry.EventId.Name);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal(FormName, StateValue(entry, "Form"));
        Assert.Equal(FakeForms.CheckCodeViewId, StateValue(entry, "ViewId"));
        Assert.Equal(geocodeLine, StateValue(entry, "Line"));
        Assert.Equal(GeocodeCommand, StateValue(entry, "Command"));
        Assert.Null(entry.ExceptionType);
        Assert.Null(entry.ExceptionMessage);

        // Neither the program's values nor the diagnostic's message reach any captured line, whatever its category.
        Assert.False(logs.ContainsText(sentinel), "A captured log entry holds the program sentinel.");
        Assert.False(logs.ContainsText(message), "A captured log entry holds the diagnostic's message.");
    }

    // The API's pieces the route needs, on TestServer: the committed configuration of the environment, the API's JSON
    // settings, the form routes and service, and the JSON console with the capturing provider beside it.
    private static async Task<WebApplication> StartHostAsync(
        string environment,
        FakeDataStore store,
        CountingCheckCodeEngine engine,
        CapturingLoggerProvider logs)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = RepositoryPaths.ApiProjectDirectory,
            EnvironmentName = environment,
            ApplicationName = typeof(FormService).Assembly.GetName().Name,
        });
        builder.WebHost.UseTestServer();

        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole();
        builder.Logging.AddProvider(logs);

        builder.Services.AddApiJson();
        builder.Services.AddSingleton<IDataStore>(store);
        builder.Services.AddSingleton<ICheckCodeEngine>(engine);
        builder.Services.AddScoped<FormService>();

        WebApplication app = builder.Build();
        app.MapFormEndpoints();

        try
        {
            await app.StartAsync();
        }
        catch
        {
            // A host that failed to start is still disposed, so its file watchers and services are released.
            await app.DisposeAsync();
            throw;
        }

        return app;
    }

    // Three Field blocks over the fake form's fields. The DOB and Asian blocks hold runnable statements of the
    // supported subset, the Asian block's carrying the sentinel; the LastName Click block holds the program's one
    // GEOCODE. Line breaks are normalised so the 1-based line numbers do not depend on the source file's endings.
    private static string SyntheticProgram(string sentinel) => $"""
        Field DOB
            After
                ASSIGN Age = YEARS(DOB, DOB)
            End-After
        End-Field

        Field Asian
            Click
                IF Asian = (-) THEN
                    CLEAR Age
                ELSE
                    ASSIGN LastName = "{sentinel}"
                    DIALOG "{sentinel}" TITLETEXT = "{sentinel}"
                END-IF
            End-Click
        End-Field

        Field LastName
            Click
                GEOCODE LastName, Age, OnsetWeek
            End-Click
        End-Field
        """.ReplaceLineEndings("\n");

    // The 1-based line of the program's only line that starts, after indentation, with the prefix.
    private static int SingleLineNumber(string program, string prefix)
    {
        string[] lines = program.Split('\n');
        int[] matches = Enumerable.Range(0, lines.Length)
            .Where(index => lines[index].TrimStart().StartsWith(prefix, StringComparison.Ordinal))
            .ToArray();
        return Assert.Single(matches) + 1;
    }

    private static object? StateValue(CapturedLogEntry entry, string key) =>
        Assert.Single(entry.State, pair => pair.Key == key).Value;
}
