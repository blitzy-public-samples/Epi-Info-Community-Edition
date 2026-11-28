using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using EpiInfo.CheckCode.Evaluation;
using EpiInfo.CheckCode.Lexing;
using EpiInfo.CheckCode.Parsing;
using EpiInfo.CheckCode.Syntax;
using EpiInfo.Domain.Diagnostics;
using EpiInfo.Domain.Events;
using EpiInfo.Domain.Metadata;
using EpiInfo.Domain.Storage;
using EpiInfo.Domain.Values;

namespace EpiInfo.CheckCode;

/// <summary>
/// The check-code engine the API calls. Each call lexes and parses a form's <c>metaViews.CheckCode</c> program with
/// <see cref="Lexer"/> and <see cref="Parser"/>; <see cref="Analyze"/> returns the program's static diagnostics and
/// <see cref="Run"/> hands the event block an event selects to the <see cref="Evaluator"/>. It takes the place of the
/// event dispatch of legacy <c>EnterCheckCodeEngine</c> and <c>RunTimeView</c>, as one stateless call per request.
/// </summary>
/// <remarks>
/// <para>
/// The class holds no state, instance or static: nothing is cached between calls, so every call parses its own
/// program text afresh, and concurrent calls on the single instance the API registers share no mutable data. It
/// performs no I/O, reads no clock and logs nothing; <c>FormService</c> and <c>FieldEventService</c> log the
/// diagnostics it returns. The implicit public parameterless constructor is its only constructor.
/// </para>
/// <para>
/// The lexer and parser accept any text, and the evaluator turns each runtime failure into an
/// <see cref="DiagnosticSeverity.Error"/> diagnostic for its statement, so neither member throws for any program
/// text or values. An exception from below therefore signals a defect, and it propagates rather than being hidden.
/// The one exception by design is <see cref="OperationCanceledException"/>: the lexer, the parser and the evaluator
/// poll the caller's <see cref="CancellationToken"/>, so an aborted request stops its work.
/// </para>
/// <para>
/// Every call is bounded by three budgets, because the stored program is unbounded text that each form definition and
/// each field event reads. A program longer than <see cref="MaxProgramLength"/> is refused before any other test of its
/// text, so one of white space only is refused too, and one with more than <see cref="MaxTokens"/> tokens as soon as
/// the lexer passes that count; either is reported as one
/// <see cref="DiagnosticSeverity.Warning"/> with the command <see cref="Parser.Commands.Limit"/> at line 1, and none
/// of it runs. The parser does work linear in the tokens, so the token budget bounds the parse. The evaluator's work
/// is not linear in the tokens, because a comparison reads whole texts and a group name stands for all its members,
/// so a run is bounded by <see cref="Evaluator.MaxWork"/> as well: a run that would pass it ends with one
/// <see cref="Parser.Commands.Limit"/> warning at the line of the statement that ran out, after the effects of the
/// statements before it. At most <see cref="MaxDiagnostics"/> diagnostics come back from a call, the last of them a
/// <see cref="Parser.Commands.Limit"/> warning that counts the ones left out when there are more. Each budget's
/// message is fixed text and a count, never program text.
/// </para>
/// </remarks>
public sealed class CheckCodeEngine : ICheckCodeEngine
{
    /// <summary>
    /// The longest program, in UTF-16 code units, the engine lexes and parses: 1,048,576, the
    /// <see cref="IDataStore.MaxCheckCodeLength"/> that the store serves whole.
    /// </summary>
    /// <remarks>
    /// A longer program is reported as over the limit and none of it is lexed. The store returns such a program as
    /// its first <see cref="IDataStore.MaxCheckCodeLength"/> + 1 code units, which is enough to tell. The seeded
    /// programs are 443 and 2,428 code units long.
    /// </remarks>
    public const int MaxProgramLength = IDataStore.MaxCheckCodeLength;

    /// <summary>
    /// The most tokens, the end of input aside, a program may produce and still be parsed: 262,144.
    /// </summary>
    /// <remarks>
    /// The lexer stops at the first token past it, and the parser does work linear in the tokens it is given, so this
    /// budget bounds the parse of every call, and with it how many nodes a run can evaluate; the work those nodes do
    /// is bounded by <see cref="Evaluator.MaxWork"/>. A program of <see cref="MaxProgramLength"/> code units
    /// averaging four or more code units a token stays within it; the seeded programs average 9.4 (<c>FoodHistory</c>)
    /// and 12.3 (<c>CaseReport</c>).
    /// </remarks>
    public const int MaxTokens = 262_144;

    /// <summary>
    /// The most diagnostics one <see cref="Analyze"/> or <see cref="Run"/> call returns: 100, each of which the API
    /// also writes to the log.
    /// </summary>
    /// <remarks>
    /// When there are more, the first <see cref="MaxDiagnostics"/> - 1 are kept in their order and the last entry is
    /// one <see cref="DiagnosticSeverity.Warning"/> with the command <see cref="Parser.Commands.Limit"/>, at the line
    /// of the first diagnostic left out, whose message counts those left out. Neither seeded program yields more than
    /// one diagnostic.
    /// </remarks>
    public const int MaxDiagnostics = 100;

    /// <inheritdoc />
    /// <remarks>
    /// The program is parsed and never evaluated, so the result is the parser's
    /// <see cref="ProgramNode.Diagnostics"/>: a <see cref="DiagnosticSeverity.Warning"/> for each unsupported
    /// construct and a <see cref="DiagnosticSeverity.Info"/> for each <c>GEOCODE</c>, in source order, and never an
    /// <see cref="DiagnosticSeverity.Error"/>, bounded to <see cref="MaxDiagnostics"/> entries. A program over
    /// <see cref="MaxProgramLength"/>, white space only included, or over <see cref="MaxTokens"/> yields its one
    /// <see cref="Parser.Commands.Limit"/> warning instead. A program within the length that holds only whitespace or
    /// comments yields an empty list.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="form"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public IReadOnlyList<Diagnostic> Analyze(
        string? program,
        FormMetadata form,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(form);

        // Parsing only: the evaluator is never reached here, which is what lets the read path call this method.
        return TryParse(program, form, cancellationToken, out ProgramNode? parsed, out Diagnostic? limit)
            ? Bound(parsed.Diagnostics, form)
            : [limit];
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// The field's block is the last <c>Field</c> block naming the same field as <paramref name="fieldName"/>, so
    /// <c>Field AGE</c> answers field <c>Age</c> when no row is spelled <c>AGE</c>, and an earlier block of the field
    /// is never consulted. Both names resolve by <see cref="EvaluationContext.TryResolveTarget"/>, the exact spelling
    /// first, and a block matches when its row has the trigger row's canonical name. A block whose name resolves to
    /// no field, such as one ambiguous between <c>Age</c> and <c>AGE</c>, answers no field; when
    /// <paramref name="fieldName"/> itself resolves to no field, the last block whose name equals it ignoring case is
    /// taken, as in legacy. Its one event block of the <paramref name="fieldEvent"/> kind runs through
    /// <see cref="Evaluator.Run"/>, over one fresh evaluation context and effect accumulator. The response holds that
    /// block's static diagnostics and its runtime errors, merged in source order; diagnostics elsewhere in the
    /// program, such as the warning for a section the parser skipped, are not included.
    /// </para>
    /// <para>
    /// With no such block the result is <see cref="FieldEventResponse.Empty"/>, every array empty, diagnostics
    /// included. <paramref name="values"/> is read, never changed, and no validation rule is applied to it.
    /// </para>
    /// <para>
    /// A program over <see cref="MaxProgramLength"/>, white space only included, or over <see cref="MaxTokens"/> is
    /// not parsed, so no block of it can be selected: the response has no effects and holds only the program's one
    /// <see cref="Parser.Commands.Limit"/> warning, whatever the field and the event. Otherwise the response's
    /// diagnostics are bounded to
    /// <see cref="MaxDiagnostics"/> entries, and its effects and dialogs are the evaluator's, unchanged.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="form"/>, <paramref name="fieldName"/> or <paramref name="values"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="fieldEvent"/> is not a defined <see cref="FieldEventKind"/> member.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public FieldEventResponse Run(
        string? program,
        FormMetadata form,
        FieldEventKind fieldEvent,
        string fieldName,
        IReadOnlyDictionary<string, FieldValue> values,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(fieldName);
        ArgumentNullException.ThrowIfNull(values);
        if (!Enum.IsDefined(fieldEvent))
        {
            throw new ArgumentOutOfRangeException(
                nameof(fieldEvent),
                fieldEvent,
                "The value is not a defined field-event kind.");
        }

        if (!TryParse(program, form, cancellationToken, out ProgramNode? parsed, out Diagnostic? limit))
        {
            return new FieldEventResponse([], [], [], ReadOnlyDictionary<string, FieldValue>.Empty, [], [limit]);
        }

        // A null, blank or comment-only program parses to no block, which selects nothing: the empty response.
        EventBlockNode[] blocks = SelectEventBlocks(parsed, form, fieldName, fieldEvent);
        return blocks.Length == 0
            ? FieldEventResponse.Empty
            : Bound(Evaluator.Run(form, blocks, values, cancellationToken), form);
    }

    /// <summary>
    /// Lexes and parses <paramref name="program"/> within the length and token budgets, the one order both
    /// <see cref="Analyze"/> and <see cref="Run"/> follow: a <see langword="null"/> program, then the length, then a
    /// blank program, then the tokens.
    /// </summary>
    /// <remarks>
    /// The length is checked before anything else reads the text, the blank test included: the store serves a program
    /// over <see cref="MaxProgramLength"/> as its first <see cref="MaxProgramLength"/> + 1 code units, which can all be
    /// white space, and such a program is over the budget, never empty. The lexer then gives up at the first token past
    /// <see cref="MaxTokens"/>, so a program over either budget costs no more than the budget. Nothing is cached.
    /// </remarks>
    /// <param name="program">The program text; <see langword="null"/> is an empty program.</param>
    /// <param name="form">The form whose name every diagnostic carries.</param>
    /// <param name="cancellationToken">The request's abort token, passed to the lexer and the parser.</param>
    /// <param name="parsed">
    /// The parsed program, when it is within both budgets; <see cref="ProgramNode.Empty"/>, with no block and no
    /// diagnostic, for a <see langword="null"/> or blank program.
    /// </param>
    /// <param name="limit">The program's one <see cref="Parser.Commands.Limit"/> warning, when it is over one.</param>
    /// <returns>
    /// <see langword="true"/> when the program was parsed or is empty; <see langword="false"/> when it is over a
    /// budget.
    /// </returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    private static bool TryParse(
        string? program,
        FormMetadata form,
        CancellationToken cancellationToken,
        [NotNullWhen(true)] out ProgramNode? parsed,
        [NotNullWhen(false)] out Diagnostic? limit)
    {
        if (program is not null && program.Length > MaxProgramLength)
        {
            parsed = null;
            limit = LimitDiagnostic(
                form,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Check code exceeds the {MaxProgramLength}-character limit; program skipped"));
            return false;
        }

        if (string.IsNullOrWhiteSpace(program))
        {
            parsed = ProgramNode.Empty;
            limit = null;
            return true;
        }

        if (!Lexer.TryTokenize(program, MaxTokens, cancellationToken, out IReadOnlyList<Token> tokens))
        {
            parsed = null;
            limit = LimitDiagnostic(
                form,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Check code exceeds the {MaxTokens}-token limit; program skipped"));
            return false;
        }

        parsed = Parser.Parse(tokens, form, cancellationToken);
        limit = null;
        return true;
    }

    /// <summary>
    /// The warning for a whole program over a budget: at line 1, because none of the program is read.
    /// </summary>
    private static Diagnostic LimitDiagnostic(FormMetadata form, string message) =>
        new(DiagnosticSeverity.Warning, form.Name, 1, Parser.Commands.Limit, message);

    /// <summary>
    /// <paramref name="response"/> with its diagnostics bounded to <see cref="MaxDiagnostics"/> entries, as the
    /// diagnostics overload bounds them, and every other member unchanged; the response itself when it is within the
    /// budget.
    /// </summary>
    private static FieldEventResponse Bound(FieldEventResponse response, FormMetadata form) =>
        response.Diagnostics.Count <= MaxDiagnostics
            ? response
            : new FieldEventResponse(
                response.Enabled,
                response.Disabled,
                response.Cleared,
                response.Assigned,
                response.Dialogs,
                Bound(response.Diagnostics, form));

    /// <summary>
    /// At most <see cref="MaxDiagnostics"/> of <paramref name="diagnostics"/>: all of them when they fit; otherwise the
    /// first <see cref="MaxDiagnostics"/> - 1 in their order, then one <see cref="Parser.Commands.Limit"/> warning at
    /// the line of the first one left out, counting those left out. Diagnostics arrive in source order, so the summary
    /// keeps that order, and nothing is left out unreported.
    /// </summary>
    private static IReadOnlyList<Diagnostic> Bound(IReadOnlyList<Diagnostic> diagnostics, FormMetadata form)
    {
        if (diagnostics.Count <= MaxDiagnostics)
        {
            return diagnostics;
        }

        const int Kept = MaxDiagnostics - 1;
        var bounded = new Diagnostic[MaxDiagnostics];
        for (int i = 0; i < Kept; i++)
        {
            bounded[i] = diagnostics[i];
        }

        int omitted = diagnostics.Count - Kept;
        bounded[Kept] = new Diagnostic(
            DiagnosticSeverity.Warning,
            form.Name,
            diagnostics[Kept].Line,
            Parser.Commands.Limit,
            string.Create(CultureInfo.InvariantCulture, $"{omitted} more check-code diagnostics are not listed"));
        return Array.AsReadOnly(bounded);
    }

    /// <summary>
    /// Returns the one event block of <paramref name="fieldEvent"/>'s kind inside the last <c>Field</c> block naming
    /// the field <paramref name="fieldName"/> names, by the rule <see cref="Run"/> describes; empty when there is no
    /// such field block or it has no block of that kind.
    /// </summary>
    /// <remarks>
    /// Only the last block of a field counts, as in legacy Epi Info, whose interpreter registered each <c>Field</c>
    /// block under its name and let a later block of the same name replace the earlier one's <c>Before</c>,
    /// <c>After</c> and <c>Click</c> sections, a section the later block lacks included: an event the last block has
    /// no section for selects nothing, even when an earlier block of the field has one. Running every block of the
    /// field would be a behaviour legacy never had, so the legacy rule is kept, among the blocks that name the same
    /// field: two fields whose names differ only by case each keep their own block. The grammar allows one section of
    /// each kind per block, in the order <c>Before</c>, <c>After</c>, <c>Click</c>; the parser reports any other
    /// section as a warning and skips it, and <see cref="FieldBlockNode"/> holds no second block of a kind, so at
    /// most one block is selected.
    /// </remarks>
    private static EventBlockNode[] SelectEventBlocks(
        ProgramNode program,
        FormMetadata form,
        string fieldName,
        FieldEventKind fieldEvent)
    {
        if (program.FieldBlocks.Count == 0)
        {
            return [];
        }

        // A scope with no values serves name resolution only; it lives for this call, so the engine stays stateless.
        var scope = new EvaluationContext(form, ReadOnlyDictionary<string, FieldValue>.Empty);
        FieldBlockNode? fieldBlock;
        if (scope.TryResolveTarget(fieldName, out FieldMetadata? trigger))
        {
            string triggerName = trigger.Name;
            fieldBlock = program.FieldBlocks.LastOrDefault(
                block => scope.TryResolveTarget(block.FieldName, out FieldMetadata? named)
                    && string.Equals(named.Name, triggerName, StringComparison.Ordinal));
        }
        else
        {
            fieldBlock = program.FieldBlocks.LastOrDefault(
                block => string.Equals(block.FieldName, fieldName, StringComparison.OrdinalIgnoreCase));
        }

        EventBlockNode? eventBlock = fieldBlock?.Events.SingleOrDefault(block => block.Event == fieldEvent);
        return eventBlock is null ? [] : [eventBlock];
    }
}
