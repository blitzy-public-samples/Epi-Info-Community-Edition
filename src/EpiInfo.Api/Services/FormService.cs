using System.Collections.ObjectModel;
using EpiInfo.Api.Errors;
using EpiInfo.CheckCode;
using EpiInfo.Domain.Diagnostics;
using EpiInfo.Domain.Forms;
using EpiInfo.Domain.Metadata;
using EpiInfo.Domain.Storage;

namespace EpiInfo.Api.Services;

/// <summary>
/// A form's metadata as read on this request, together with the form definition built from it.
/// </summary>
/// <remarks>
/// <see cref="FormService.LoadFormAsync"/> is its only producer. The definition carries the Legal Values options, so a
/// record validator built from it can check that a submitted value is one of them (scenario <c>EC-VAL-03</c>); it
/// carries no check-code diagnostics and no permission flags.
/// </remarks>
/// <param name="Metadata">The form's metadata, read fresh on this request.</param>
/// <param name="Definition">The form definition assembled from <paramref name="Metadata"/>.</param>
public sealed record LoadedForm(FormMetadata Metadata, FormDefinition Definition);

/// <summary>
/// Serves the forms list and the form definitions, and loads a form for the services that read or write its records.
/// It takes over the form loading and render preparation of the legacy Enter canvas, view explorer and control
/// factory, as stateless reads.
/// </summary>
/// <remarks>
/// <para>
/// Every member reads the metadata, the code tables, the check-code text and the permissions through
/// <see cref="IDataStore"/> on each call and caches nothing, so a metadata change applies to the next request. Every
/// read is a <c>SELECT</c>: nothing is written, and check code is analysed but never run, so loading a form definition
/// changes nothing in the database (scenario <c>DATA-01</c>).
/// </para>
/// <para>
/// Page order, field order, group members, option lists, bounds and field types are assembled only by
/// <see cref="FormDefinitionFactory"/>; this service supplies its inputs and applies no rule of its own.
/// </para>
/// <para>
/// HTTP-facing members return an <see cref="IResult"/> built from <see cref="TypedResults"/> or
/// <see cref="ApiProblems"/>, so an endpoint only forwards the call. A store failure propagates as
/// <see cref="DataStoreUnavailableException"/> or <see cref="DataStoreWriteException"/>, and a client abort as
/// <see cref="OperationCanceledException"/>, for the API exception handler to map. The class holds no mutable state.
/// </para>
/// </remarks>
/// <param name="store">The persistence port every metadata read goes through.</param>
/// <param name="checkCode">The check-code engine; only its static analysis is used here.</param>
/// <param name="logger">The logger each check-code diagnostic is written to.</param>
/// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
public sealed partial class FormService(IDataStore store, ICheckCodeEngine checkCode, ILogger<FormService> logger)
{
    private readonly IDataStore store = store ?? throw new ArgumentNullException(nameof(store));

    private readonly ICheckCodeEngine checkCode = checkCode ?? throw new ArgumentNullException(nameof(checkCode));

    private readonly ILogger<FormService> logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>Lists every form for <c>GET /api/forms</c>.</summary>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>200 with one <see cref="FormSummary"/> per form, possibly none.</returns>
    public async Task<IResult> ListAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<FormSummary> forms = await store.ListFormsAsync(cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(forms);
    }

    /// <summary>
    /// Loads a form's metadata and builds its definition with the Legal Values options, for the record and field-event
    /// services.
    /// </summary>
    /// <remarks>
    /// This is the single place a service outside this class obtains a form definition, so the record services never
    /// assemble one themselves. It neither reads nor analyses the check-code program and reads no permissions: record
    /// reads and writes need neither, and skipping them keeps their reads to the metadata and code tables.
    /// </remarks>
    /// <param name="viewId">A <c>metaViews.ViewId</c>.</param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>
    /// The metadata with its definition, or <see langword="null"/> when <paramref name="viewId"/> is not a form, which
    /// the caller reports as 404.
    /// </returns>
    public async Task<LoadedForm?> LoadFormAsync(int viewId, CancellationToken cancellationToken)
    {
        FormMetadata? metadata = await store.GetFormMetadataAsync(viewId, cancellationToken).ConfigureAwait(false);
        if (metadata is null)
        {
            return null;
        }

        IReadOnlyDictionary<string, IReadOnlyList<FieldOption>> options =
            await ReadLegalValueOptionsAsync(metadata, cancellationToken).ConfigureAwait(false);

        FormDefinition definition = FormDefinitionFactory.Create(metadata, options, Array.Empty<Diagnostic>());
        return new LoadedForm(metadata, definition);
    }

    /// <summary>Builds the form definition for <c>GET /api/forms/{viewId}</c>.</summary>
    /// <remarks>
    /// The check-code program is analysed and never run, so <c>unsupportedCheckCode</c> holds only what the program
    /// text reveals, and each of those diagnostics is logged once. The permission flags are <see langword="null"/>
    /// when the store reports no permissions; otherwise <see cref="FormPermissions"/> derives them.
    /// </remarks>
    /// <param name="viewId">A <c>metaViews.ViewId</c>.</param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>200 with the <see cref="FormDefinition"/>, or the 404 problem when the form does not exist.</returns>
    public async Task<IResult> GetDefinitionAsync(int viewId, CancellationToken cancellationToken)
    {
        FormMetadata? metadata = await store.GetFormMetadataAsync(viewId, cancellationToken).ConfigureAwait(false);
        if (metadata is null)
        {
            return ApiProblems.FormNotFound();
        }

        IReadOnlyDictionary<string, IReadOnlyList<FieldOption>> options =
            await ReadLegalValueOptionsAsync(metadata, cancellationToken).ConfigureAwait(false);

        string? program = await store.GetCheckCodeAsync(metadata.ViewId, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<Diagnostic> diagnostics = checkCode.Analyze(program, metadata);

        FormPermissions? permissions =
            await store.GetPermissionsAsync(metadata, cancellationToken).ConfigureAwait(false);

        FormDefinition definition = FormDefinitionFactory.Create(
            metadata,
            options,
            diagnostics,
            permissions?.CanInsert(metadata),
            permissions?.CanUpdate(metadata),
            permissions?.CanDelete(metadata));

        // Logged from the definition's validated copy, and only once the definition is built, so each diagnostic of a
        // served definition is logged exactly once.
        LogDiagnostics(metadata, definition.UnsupportedCheckCode);

        return TypedResults.Ok(definition);
    }

    /// <summary>
    /// Reads the options of the form's Legal Values fields, skipping the store when the form has none.
    /// </summary>
    /// <remarks>
    /// The store reads every Legal Values field's code table in one call and owns the code-table rules: an empty list
    /// for a field without a usable code table, the database's own row order, and sorting only when
    /// <c>metaFields.Sort</c> is set. The result is passed on unchanged, with no sorting, de-duplication or filtering.
    /// </remarks>
    private async Task<IReadOnlyDictionary<string, IReadOnlyList<FieldOption>>> ReadLegalValueOptionsAsync(
        FormMetadata form,
        CancellationToken cancellationToken)
    {
        if (!FormDefinitionFactory.GetDataFields(form).Any(field => field.FieldType == FieldType.LegalValues))
        {
            return ReadOnlyDictionary<string, IReadOnlyList<FieldOption>>.Empty;
        }

        return await store.GetOptionsAsync(form, cancellationToken).ConfigureAwait(false);
    }

    private void LogDiagnostics(FormMetadata form, IReadOnlyList<Diagnostic> diagnostics)
    {
        foreach (Diagnostic diagnostic in diagnostics)
        {
            LogLevel level = diagnostic.Severity == DiagnosticSeverity.Info ? LogLevel.Information : LogLevel.Warning;

            // The diagnostic's message is never logged, only its position and the command keyword from the program
            // text, which is metadata rather than a value.
            LogCheckCodeDiagnostic(
                logger,
                level,
                form.Name,
                form.ViewId,
                diagnostic.Severity,
                diagnostic.Line,
                diagnostic.Command);
        }
    }

    [LoggerMessage(
        EventId = 1,
        EventName = "CheckCodeDiagnostic",
        Message = "Check-code diagnostic for form {Form} (view {ViewId}): {Severity} at line {Line}, "
            + "command {Command}")]
    private static partial void LogCheckCodeDiagnostic(
        ILogger logger,
        LogLevel level,
        string form,
        int viewId,
        DiagnosticSeverity severity,
        int line,
        string command);
}
