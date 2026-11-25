using EpiInfo.CheckCode.Evaluation;
using EpiInfo.CheckCode.Lexing;
using EpiInfo.CheckCode.Parsing;
using EpiInfo.CheckCode.Syntax;
using EpiInfo.Domain.Diagnostics;
using EpiInfo.Domain.Events;
using EpiInfo.Domain.Metadata;
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
/// </para>
/// </remarks>
public sealed class CheckCodeEngine : ICheckCodeEngine
{
    /// <inheritdoc />
    /// <remarks>
    /// The program is parsed and never evaluated, so the result is the parser's
    /// <see cref="ProgramNode.Diagnostics"/>: a <see cref="DiagnosticSeverity.Warning"/> for each unsupported
    /// construct and a <see cref="DiagnosticSeverity.Info"/> for each <c>GEOCODE</c>, in source order, and never an
    /// <see cref="DiagnosticSeverity.Error"/>. A program holding only whitespace or comments yields an empty list.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="form"/> is <see langword="null"/>.</exception>
    public IReadOnlyList<Diagnostic> Analyze(string? program, FormMetadata form)
    {
        ArgumentNullException.ThrowIfNull(form);

        if (string.IsNullOrWhiteSpace(program))
        {
            return Array.Empty<Diagnostic>();
        }

        // Parsing only: the evaluator is never reached here, which is what lets the read path call this method.
        return Parse(program, form).Diagnostics;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// The field's block is the last <c>Field</c> block whose name equals <paramref name="fieldName"/> without
    /// regard to case, so <c>Field AGE</c> answers field <c>Age</c>, and an earlier block of the name is never
    /// consulted. Its one event block of the <paramref name="fieldEvent"/> kind runs through
    /// <see cref="Evaluator.Run"/>, over one fresh evaluation context and effect accumulator. The response holds that
    /// block's static diagnostics and its runtime errors, merged in source order; diagnostics elsewhere in the
    /// program, such as the warning for a section the parser skipped, are not included.
    /// </para>
    /// <para>
    /// With no such block the result is <see cref="FieldEventResponse.Empty"/>, every array empty, diagnostics
    /// included. <paramref name="values"/> is read, never changed, and no validation rule is applied to it.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="form"/>, <paramref name="fieldName"/> or <paramref name="values"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="fieldEvent"/> is not a defined <see cref="FieldEventKind"/> member.
    /// </exception>
    public FieldEventResponse Run(
        string? program,
        FormMetadata form,
        FieldEventKind fieldEvent,
        string fieldName,
        IReadOnlyDictionary<string, FieldValue> values)
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

        if (string.IsNullOrWhiteSpace(program))
        {
            return FieldEventResponse.Empty;
        }

        EventBlockNode[] blocks = SelectEventBlocks(Parse(program, form), fieldName, fieldEvent);
        return blocks.Length == 0 ? FieldEventResponse.Empty : Evaluator.Run(form, blocks, values);
    }

    private static ProgramNode Parse(string program, FormMetadata form) =>
        Parser.Parse(Lexer.Tokenize(program), form);

    /// <summary>
    /// Returns the one event block of <paramref name="fieldEvent"/>'s kind inside the last <c>Field</c> block named
    /// <paramref name="fieldName"/>; empty when there is no such field block or it has no block of that kind.
    /// </summary>
    /// <remarks>
    /// Only the last block of a name counts, as in legacy Epi Info, whose interpreter registered each <c>Field</c>
    /// block under its name and let a later block of the same name replace the earlier one's <c>Before</c>,
    /// <c>After</c> and <c>Click</c> sections, a section the later block lacks included: an event the last block has
    /// no section for selects nothing, even when an earlier block of the name has one. Running every block of the
    /// name would be a behaviour legacy never had, so the legacy rule is kept. The grammar allows one section of each
    /// kind per block, in the order <c>Before</c>, <c>After</c>, <c>Click</c>; the parser reports any other section as
    /// a warning and skips it, and <see cref="FieldBlockNode"/> holds no second block of a kind, so at most one block
    /// is selected.
    /// </remarks>
    private static EventBlockNode[] SelectEventBlocks(ProgramNode program, string fieldName, FieldEventKind fieldEvent)
    {
        FieldBlockNode? fieldBlock = program.FieldBlocks.LastOrDefault(
            block => string.Equals(block.FieldName, fieldName, StringComparison.OrdinalIgnoreCase));

        EventBlockNode? eventBlock = fieldBlock?.Events.SingleOrDefault(block => block.Event == fieldEvent);
        return eventBlock is null ? [] : [eventBlock];
    }
}
