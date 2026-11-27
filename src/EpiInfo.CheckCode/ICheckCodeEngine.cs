using EpiInfo.Domain.Diagnostics;
using EpiInfo.Domain.Events;
using EpiInfo.Domain.Metadata;
using EpiInfo.Domain.Values;

namespace EpiInfo.CheckCode;

/// <summary>
/// The boundary between the API and the check-code engine: static analysis of a form's program for the form
/// definition, and execution of one field-event block whose effects travel back to the browser as data.
/// </summary>
/// <remarks>
/// <para>
/// The API passes in everything an operation needs: the program text, which it reads from
/// <c>metaViews.CheckCode</c> through <c>IDataStore.GetCheckCodeAsync</c>, the form's metadata, and for
/// <see cref="Run"/> the event, the field and the record's values. The engine reaches no store and calls back into
/// no host; effects are returned, never applied.
/// </para>
/// <para>Both members follow the same rules, which every implementation and every wrapper must keep:</para>
/// <list type="bullet">
/// <item><description>
/// <b>Stateless.</b> No state survives a call and nothing is cached between calls, so each call sees only its own
/// arguments. Nothing is written and there is no I/O.
/// </description></item>
/// <item><description>
/// <b>Thread-safe.</b> Calls may run concurrently; the API registers one engine as a singleton.
/// </description></item>
/// <item><description>
/// <b>Never throws</b> for any program text or any values. A construct outside the supported subset, a skipped
/// <c>GEOCODE</c> statement and a runtime problem each become a <see cref="Diagnostic"/>, and a check-code error
/// never blocks a save. The one exception is cancellation: a call whose <see cref="CancellationToken"/> is cancelled
/// stops with <see cref="OperationCanceledException"/>, which the API answers as a client abort, with no response.
/// </description></item>
/// <item><description>
/// <b>Bounded.</b> A stored program costs each call at most a fixed budget, the bound the stored text itself does not
/// give: a program longer than <see cref="CheckCodeEngine.MaxProgramLength"/> UTF-16 code units, or with more than
/// <see cref="CheckCodeEngine.MaxTokens"/> tokens, is not parsed and runs nothing, and the call reports it as one
/// <see cref="DiagnosticSeverity.Warning"/> with the command <c>LIMIT</c> at line 1. A <c>Run</c> whose work would pass
/// <see cref="Evaluation.Evaluator.MaxWork"/> stops there with one <c>LIMIT</c> warning at the line of the statement
/// that ran out, keeping the effects of the statements before it. A call returns at most
/// <see cref="CheckCodeEngine.MaxDiagnostics"/> diagnostics: when there are more, the first ones are kept in their
/// order and the last entry is one <c>LIMIT</c> warning, at the line of the first one left out, that counts those left
/// out, so none is dropped unreported.
/// </description></item>
/// <item><description>
/// <b>Form metadata.</b> <c>form</c> supplies identifier resolution, case-insensitive to the canonical
/// <see cref="FieldMetadata.Name"/>: a value read or <c>ASSIGN</c> takes the data field first (as
/// <see cref="DataFieldSet.TryGet"/> resolves <c>AGE</c> to <c>Age</c>), while an <c>ENABLE</c>, <c>DISABLE</c> or
/// <c>CLEAR</c> field, a <c>Field</c> block's name and the event's field take the row spelled exactly so first, and a
/// name ambiguous among the data fields names no data field. It also supplies field types, and group members from
/// <see cref="FieldMetadata.List"/> (as <see cref="FormDefinitionFactory.GetGroupMembers"/> resolves them).
/// <see cref="FormMetadata.Name"/> is the <see cref="Diagnostic.Form"/> of every diagnostic returned.
/// </description></item>
/// </list>
/// <para>
/// Permanent contract, pinned by <c>CheckCodeEngineContractTests</c> for every implementation: <see cref="Run"/>
/// for a field with no block for the event returns the empty envelope, and <see cref="Analyze"/> and
/// <see cref="Run"/> on an empty program return no effects and no diagnostics.
/// </para>
/// </remarks>
public interface ICheckCodeEngine
{
    /// <summary>
    /// Reports what the program text alone reveals, without evaluating it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The result holds a <see cref="DiagnosticSeverity.Warning"/> for each construct outside the supported subset
    /// and a <see cref="DiagnosticSeverity.Info"/> for each <c>GEOCODE</c> statement, in source order. Nothing is
    /// evaluated, so the result never holds a <see cref="DiagnosticSeverity.Error"/>.
    /// </para>
    /// <para>
    /// <c>FormService</c> places the result in the form definition's <c>unsupportedCheckCode</c> and logs each
    /// diagnostic. Calling this method runs no check code, so it is allowed on the read path.
    /// </para>
    /// </remarks>
    /// <param name="program">
    /// The form's <c>metaViews.CheckCode</c> text. <see langword="null"/>, empty or whitespace means the form has no
    /// program.
    /// </param>
    /// <param name="form">The metadata of the form that owns <paramref name="program"/>.</param>
    /// <param name="cancellationToken">
    /// The request's abort token, polled while the program is lexed and parsed.
    /// </param>
    /// <returns>
    /// The static diagnostics in source order, at most <see cref="CheckCodeEngine.MaxDiagnostics"/> of them; the one
    /// <c>LIMIT</c> warning for a program over the engine's budget; an empty list when there is no program or nothing
    /// to report.
    /// </returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    IReadOnlyList<Diagnostic> Analyze(
        string? program,
        FormMetadata form,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes the block of <paramref name="fieldName"/> that <paramref name="fieldEvent"/> selects, against
    /// <paramref name="values"/>, and returns its effects.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="FieldEventKind.FieldBefore"/> selects the field's <c>Before</c> block,
    /// <see cref="FieldEventKind.FieldAfter"/> its <c>After</c> block and <see cref="FieldEventKind.FieldClick"/>
    /// its <c>Click</c> block. Statements run in order, and later statements read the results of earlier
    /// <c>ASSIGN</c> and <c>CLEAR</c> statements.
    /// </para>
    /// <para>
    /// The response holds the block's effects, its dialogs, and its diagnostics in source order: the block's static
    /// diagnostics plus a <see cref="DiagnosticSeverity.Error"/> for each runtime error. A runtime error keeps the
    /// effects of the statements before it.
    /// </para>
    /// <para>
    /// <c>FieldEventService</c> validates the request before calling: the event, the field, every key of
    /// <paramref name="values"/> and each value's wire type. The engine applies no read-only, range, length, mask,
    /// legal-value, required or empty-record rule, so a record whose stored values break current rules can still
    /// be evaluated.
    /// </para>
    /// </remarks>
    /// <param name="program">
    /// The form's <c>metaViews.CheckCode</c> text. <see langword="null"/>, empty or whitespace means the form has no
    /// program.
    /// </param>
    /// <param name="form">The metadata of the form that owns <paramref name="program"/>.</param>
    /// <param name="fieldEvent">The event the browser raised on the field.</param>
    /// <param name="fieldName">The canonical <c>metaFields.Name</c> of the field that raised the event.</param>
    /// <param name="values">
    /// The record's current values, keyed by canonical <c>metaFields.Name</c>. A field absent from the dictionary
    /// reads as <see cref="FieldValue.Null"/>.
    /// </param>
    /// <param name="cancellationToken">
    /// The request's abort token, polled while the program is lexed and parsed and before each statement runs.
    /// </param>
    /// <returns>
    /// The effects and diagnostics of the executed block, at most <see cref="CheckCodeEngine.MaxDiagnostics"/>
    /// diagnostics; for a program over the engine's budget, no effects and the one <c>LIMIT</c> warning;
    /// <see cref="FieldEventResponse.Empty"/>, with every array empty, diagnostics included, when there is no program
    /// or the field has no block for the event.
    /// </returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    FieldEventResponse Run(
        string? program,
        FormMetadata form,
        FieldEventKind fieldEvent,
        string fieldName,
        IReadOnlyDictionary<string, FieldValue> values,
        CancellationToken cancellationToken = default);
}
