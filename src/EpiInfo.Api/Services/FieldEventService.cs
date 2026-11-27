using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using EpiInfo.Api.Contracts;
using EpiInfo.Api.Errors;
using EpiInfo.Api.Json;
using EpiInfo.CheckCode;
using EpiInfo.Domain.Diagnostics;
using EpiInfo.Domain.Events;
using EpiInfo.Domain.Metadata;
using EpiInfo.Domain.Storage;

namespace EpiInfo.Api.Services;

/// <summary>
/// Runs one check-code field event for <c>POST /api/forms/{viewId}/checkcode/field-event</c>: it validates what
/// evaluation needs, reads the form's program, hands the event to <see cref="ICheckCodeEngine.Run"/> and returns the
/// effects as data for the browser to apply.
/// </summary>
/// <remarks>
/// <para>
/// Each request is evaluated on its own. No view stack, record or interpreter state survives the call, nothing is
/// cached and nothing is written: the store is only read, through <see cref="IDataStore.GetFormMetadataAsync"/> and
/// <see cref="IDataStore.GetCheckCodeAsync"/>. The service holds no check-code logic and never parses the program;
/// the response's lists and their order are the engine's and are returned unchanged.
/// </para>
/// <para>Validation, in this order, before the engine is called:</para>
/// <list type="number">
/// <item><description>An unknown form is the 404 of <see cref="ApiProblems.FormNotFound"/>.</description></item>
/// <item><description>A missing body is the request-level 400 of <see cref="ApiProblems.RequestBodyRequired"/>.</description></item>
/// <item><description>
/// <c>event</c> must be a JSON string that <see cref="FieldEventKindNames.TryParseExact"/> accepts, and
/// <c>fieldName</c> a JSON string naming a field on a page of the form: the one spelled exactly so, otherwise the data
/// field <see cref="DataFieldSet.TryGet"/> finds ignoring case, otherwise the first in metadata order equal to it
/// ignoring case; a name ambiguous among the data fields (<see cref="DataFieldSet.IsAmbiguous"/>) names none. Data
/// fields, labels, groups and command buttons all qualify. A failure of either is a field-level 400 under
/// <c>errors["event"]</c> or <c>errors["fieldName"]</c> with a fixed message that never echoes the submitted text,
/// together with every per-key error <see cref="ValuesReader"/> finds in <c>values</c>.
/// </description></item>
/// <item><description>
/// With both valid, a <c>values</c> that is not an object is request-level, and a key that is not a data field of the
/// form or a value without its wire type is field-level, as <see cref="ValuesReader.ReadRaw"/> and
/// <see cref="ValuesReader.ToTyped"/> report them. Read-only fields are accepted, because the editor sends the whole
/// record state.
/// </description></item>
/// </list>
/// <para>
/// No read-only, range, length, mask, legal-value, required or empty-record rule applies, so a record whose stored
/// values break current rules can still be evaluated. A check-code runtime error is an <c>error</c> diagnostic in a
/// 200 response, never an HTTP error, and never blocks a save. Store exceptions propagate to the exception handler,
/// which answers 503 or 500; the endpoint writes nothing, so those bodies carry no <c>writeOutcome</c>. A client abort
/// stops the store read or the engine with <see cref="OperationCanceledException"/>, which the handler answers with
/// no response.
/// </para>
/// <para>
/// Each returned diagnostic is logged once through a fixed template holding the form name, severity, line and
/// command only. The logged command is the one <see cref="FormService.LoggedCommand"/> maps it to: the diagnostic's
/// own when it is fixed language vocabulary and the diagnostic is not an unsupported function's, otherwise
/// <see cref="FormService.UnregisteredFunctionCommand"/>. Every unsupported function's diagnostic is therefore logged
/// under the fixed code, whatever its name's spelling, <c>[GOTO](1)</c> and <c>FORMAT(x)</c> included, so a name
/// taken from the program text is never logged; the response keeps the diagnostic as the engine reported it. A
/// diagnostic's message, the program text and every field value stay out of the log.
/// </para>
/// <para>The type keeps no mutable state; the API registers it as a scoped service.</para>
/// </remarks>
public sealed partial class FieldEventService(
    IDataStore store,
    ICheckCodeEngine checkCode,
    ILogger<FieldEventService> logger)
{
    /// <summary>The wire name of the <c>event</c> member, the key of its entry in a field-level 400.</summary>
    public const string EventKey = "event";

    /// <summary>The wire name of the <c>fieldName</c> member, the key of its entry in a field-level 400.</summary>
    public const string FieldNameKey = "fieldName";

    /// <summary>
    /// The fixed message under <c>errors["fieldName"]</c> for a missing, non-string, blank or unknown field name.
    /// </summary>
    public const string InvalidFieldNameMessage = "The fieldName must name a field on a page of the form.";

    private readonly IDataStore store = store ?? throw new ArgumentNullException(nameof(store));

    private readonly ICheckCodeEngine checkCode = checkCode ?? throw new ArgumentNullException(nameof(checkCode));

    private readonly ILogger<FieldEventService> logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// The fixed message under <c>errors["event"]</c> for a missing, non-string or unrecognised event, naming the
    /// three accepted spellings.
    /// </summary>
    public static string InvalidEventMessage { get; } =
        "The event must be exactly one of " + string.Join(", ", FieldEventKindNames.WireNames) + ".";

    /// <summary>Validates and evaluates one field event.</summary>
    /// <param name="viewId">The form's <c>metaViews.ViewId</c> from the route.</param>
    /// <param name="request">The bound body, or <see langword="null"/> when the body is the JSON literal <c>null</c>.</param>
    /// <param name="cancellationToken">
    /// The request's abort token, passed to every store call and to <see cref="ICheckCodeEngine.Run"/>, so an aborted
    /// request stops the evaluation with <see cref="OperationCanceledException"/>.
    /// </param>
    /// <returns>
    /// 200 with the engine's <see cref="FieldEventResponse"/>; 404 for an unknown form; or a request-level or
    /// field-level 400 as the remarks describe.
    /// </returns>
    public async Task<IResult> RunAsync(int viewId, FieldEventRequest? request, CancellationToken cancellationToken)
    {
        var metadata = await store.GetFormMetadataAsync(viewId, cancellationToken).ConfigureAwait(false);
        if (metadata is null)
        {
            return ApiProblems.FormNotFound();
        }

        if (request is null)
        {
            return ApiProblems.RequestBodyRequired();
        }

        var errors = new OrderedDictionary<string, List<string>>(StringComparer.Ordinal);

        if (!TryReadEvent(request.Event, out var fieldEvent))
        {
            AddError(errors, EventKey, InvalidEventMessage);
        }

        // The field name resolves against the data fields ValuesReader.ToTyped selects from the same metadata for the
        // values keys, so both name fields by one rule.
        var dataFields = FormDefinitionFactory.GetDataFields(metadata);
        var fieldName = ResolveFieldName(metadata, dataFields, request.FieldName);
        if (fieldName is null)
        {
            AddError(errors, FieldNameKey, InvalidFieldNameMessage);
        }

        var raw = ValuesReader.ReadRaw(request.Values);
        var typed = raw.IsSuccess
            ? ValuesReader.ToTyped(metadata, raw.Values)
            : null;

        if (fieldName is null || errors.Count > 0)
        {
            // One field-level 400 lists the event and field-name entries with every per-key values error found, so
            // the caller sees all of them at once. A values member that is not an object adds nothing here: its
            // request-level 400 is reserved for a request whose event and field name are both valid.
            AddErrors(errors, raw.FieldEventErrorsFor(metadata));
            if (typed is not null)
            {
                AddErrors(errors, typed.FieldErrors);
            }

            return ApiProblems.ValidationFailed(Freeze(errors));
        }

        if (raw.RequestError is { } requestError)
        {
            // A fixed ValuesReader text: ValidationMessages.ValuesMustBeObject, or "malformed JSON" for a name or
            // string that cannot be decoded, which must not be reported as a values shape problem.
            return ApiProblems.RequestLevel(requestError);
        }

        if (typed is null)
        {
            return ApiProblems.ValidationFailed(raw.FieldEventErrorsFor(metadata));
        }

        if (!typed.IsSuccess)
        {
            return ApiProblems.ValidationFailed(typed.FieldErrors);
        }

        var program = await store.GetCheckCodeAsync(viewId, cancellationToken).ConfigureAwait(false);
        var response = checkCode.Run(program, metadata, fieldEvent, fieldName, typed.Values, cancellationToken);

        foreach (var diagnostic in response.Diagnostics)
        {
            try
            {
                LogDiagnostic(
                    logger,
                    LevelFor(diagnostic.Severity),
                    metadata.Name,
                    diagnostic.Severity,
                    diagnostic.Line,
                    FormService.LoggedCommand(diagnostic));
            }
            catch (Exception)
            {
                // Deliberately swallowed: a failing log provider must never turn the evaluated event into an error,
                // nor keep the remaining diagnostics from the providers that work.
            }
        }

        return TypedResults.Ok(response);
    }

    private static bool TryReadEvent(JsonElement? element, out FieldEventKind fieldEvent)
    {
        if (TryReadString(element, out var text) && FieldEventKindNames.TryParseExact(text, out fieldEvent))
        {
            return true;
        }

        fieldEvent = default;
        return false;
    }

    // The canonical metaFields.Name of the field shown on one of the form's pages that the name designates, or null.
    // Only page-bound rows count: system rows (UniqueKey, RecStatus, GlobalRecordId) and rows on a page outside the
    // form are never shown, so the browser can never raise an event on them, and they neither hide nor shadow a page
    // field whose name differs from theirs only by case. The rule is the one the engine resolves the name by again:
    // the exact spelling; otherwise the data field DataFieldSet.TryGet finds ignoring case; otherwise nothing for a
    // name ambiguous among the data fields, such as aGE beside Age and AGE; otherwise the first page-bound row in
    // metadata order equal to it ignoring case, which is then a non-data row, since no data field equals it.
    private static string? ResolveFieldName(FormMetadata form, DataFieldSet dataFields, JsonElement? element)
    {
        if (!TryReadString(element, out var name) || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        FieldMetadata? firstEqualIgnoringCase = null;
        foreach (var field in form.Fields)
        {
            if (field.PageId is not { } pageId || form.FindPage(pageId) is null)
            {
                continue;
            }

            // Every row spelled exactly so, the data field included, has this one canonical name.
            if (string.Equals(field.Name, name, StringComparison.Ordinal))
            {
                return field.Name;
            }

            if (firstEqualIgnoringCase is null && string.Equals(field.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                firstEqualIgnoringCase = field;
            }
        }

        if (dataFields.TryGet(name, out var dataField))
        {
            return dataField.Name;
        }

        return dataFields.IsAmbiguous(name) ? null : firstEqualIgnoringCase?.Name;
    }

    // JsonElement.GetString throws InvalidOperationException for text that does not decode, such as an escaped lone
    // surrogate, which JSON parsing itself accepts. Such text is never a valid event or field name, so it is reported
    // as one rather than surfacing as a 500.
    private static bool TryReadString(JsonElement? element, [NotNullWhen(true)] out string? text)
    {
        text = null;
        if (element is not { ValueKind: JsonValueKind.String } value)
        {
            return false;
        }

        try
        {
            text = value.GetString();
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        return text is not null;
    }

    private static void AddError(OrderedDictionary<string, List<string>> errors, string key, string message)
    {
        if (!errors.TryGetValue(key, out var messages))
        {
            messages = [];
            errors.Add(key, messages);
        }

        if (!messages.Contains(message, StringComparer.Ordinal))
        {
            messages.Add(message);
        }
    }

    // A values key may coincide with "event" or "fieldName"; its messages then join that entry instead of clashing.
    private static void AddErrors(
        OrderedDictionary<string, List<string>> errors,
        IReadOnlyDictionary<string, string[]> source)
    {
        foreach (var (key, messages) in source)
        {
            foreach (var message in messages)
            {
                AddError(errors, key, message);
            }
        }
    }

    private static OrderedDictionary<string, string[]> Freeze(OrderedDictionary<string, List<string>> errors)
    {
        var copy = new OrderedDictionary<string, string[]>(errors.Count, StringComparer.Ordinal);
        foreach (var (key, messages) in errors)
        {
            copy.Add(key, [.. messages]);
        }

        return copy;
    }

    // Information for a skipped GEOCODE, so the committed Default=Information filter keeps the entry; Warning for an
    // unsupported construct and for a runtime error, neither of which fails the request.
    private static LogLevel LevelFor(DiagnosticSeverity severity) =>
        severity == DiagnosticSeverity.Info ? LogLevel.Information : LogLevel.Warning;

    [LoggerMessage(
        EventId = 1,
        EventName = "CheckCodeDiagnostic",
        Message = "Check-code diagnostic for form {Form}: {Severity} at line {Line}, command {Command}")]
    private static partial void LogDiagnostic(
        ILogger logger,
        LogLevel level,
        string form,
        DiagnosticSeverity severity,
        int line,
        string command);
}
