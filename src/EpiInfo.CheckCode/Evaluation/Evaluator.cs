using System.Globalization;
using System.Runtime.CompilerServices;
using EpiInfo.CheckCode.Functions;
using EpiInfo.CheckCode.Syntax;
using EpiInfo.Domain.Diagnostics;
using EpiInfo.Domain.Events;
using EpiInfo.Domain.Forms;
using EpiInfo.Domain.Metadata;
using EpiInfo.Domain.Values;

namespace EpiInfo.CheckCode.Evaluation;

/// <summary>
/// The tree-walking evaluator of the check-code subset: it executes the statements of the selected event blocks
/// against one record's values and reports every effect to an <see cref="EffectAccumulator"/>, which alone builds
/// the <see cref="FieldEventResponse"/>.
/// </summary>
/// <remarks>
/// <para>
/// Check code runs only here; the client applies the returned effects. A run reads the request's values through an
/// <see cref="EvaluationContext"/> and writes nothing anywhere: <c>ASSIGN</c> and <c>CLEAR</c> change the context's
/// current values, so later statements, and later blocks of the same run, read their results.
/// </para>
/// <para>
/// Each statement runs inside its own error boundary. A runtime error (an unknown field, a type mismatch, an
/// assignment to a label, group or unsupported field, a failed <c>YEARS</c> or <c>EPIWEEK</c> call, an expression
/// nested deeper than the remaining stack allows) skips that statement with one
/// <see cref="DiagnosticSeverity.Error"/> diagnostic, keeps the effects of earlier statements and continues with the
/// next one, so a check-code error never blocks a save. Diagnostic messages name fields, functions and constructs
/// only, never a value.
/// </para>
/// <para>
/// The evaluator enforces no read-only, range, length, mask, legal-value or required rule: an <c>ASSIGN</c> to a
/// read-only field such as the seeded <c>OnsetWeek</c> is reported like any other, and the client shows it without
/// sending it. Validation belongs to the save path.
/// </para>
/// <para>
/// The class holds no state, performs no I/O and reads no clock or current culture, so concurrent calls are safe and
/// every run of the same blocks over the same values gives the same response.
/// </para>
/// </remarks>
public static class Evaluator
{
    private const string IfCommand = "IF";
    private const string EnableCommand = "ENABLE";
    private const string DisableCommand = "DISABLE";
    private const string ClearCommand = "CLEAR";
    private const string AssignCommand = "ASSIGN";
    private const string DialogCommand = "DIALOG";
    private const string GeocodeCommand = "GEOCODE";

    // Fixed text: the message of an ArgumentOutOfRangeException or OverflowException can embed a value.
    private const string OutOfRangeMessage = "Date arithmetic is out of range.";
    private const string UnsupportedStatementMessage = "Unsupported statement.";
    private const string UnsupportedExpressionMessage = "Unsupported expression.";
    private const string BooleanComparisonMismatchMessage =
        "Type mismatch: a boolean can be compared only with a yes or no value.";
    private const string ExpressionTooDeepMessage = "Expression is nested too deeply to evaluate.";

    /// <summary>
    /// Runs the selected event blocks of one field event over a record's values and returns the response.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The caller, <c>CheckCodeEngine.Run</c>, selects the block: the one event block of the event's kind, as the
    /// grammar allows one of each kind, inside the last <c>Field</c> block whose name matches the event's field
    /// without regard to case, because a later block of a name replaced the earlier one in legacy Epi Info.
    /// </para>
    /// <para>
    /// Each block's static diagnostics are added here, so the caller must not add them again. They are added at
    /// statement boundaries, each just before the first statement to run that starts after it in the source, or
    /// after the block when none does, as <see cref="EventBlockNode.DiagnosticAnchors"/> places them; a block built
    /// without anchors has them all added before its first statement. Static diagnostics and runtime errors
    /// therefore reach the response in source order, so a statement that fails before a skipped construct on the
    /// same line reports its error first. Statement positions are counted only while a block still has a diagnostic
    /// to add, so a block without diagnostics or anchors never walks a branch it skips, however deeply nested.
    /// </para>
    /// <para>
    /// The blocks run in the given order over one shared <see cref="EvaluationContext"/>, so a later block reads the
    /// values an earlier block assigned or cleared.
    /// </para>
    /// </remarks>
    /// <param name="form">The metadata of the form whose program runs.</param>
    /// <param name="blocks">The selected event blocks in source order.</param>
    /// <param name="values">The record's current values, keyed by field name in any letter case. Never changed.</param>
    /// <returns>
    /// The accumulated effects and diagnostics, or <see cref="FieldEventResponse.Empty"/> when
    /// <paramref name="blocks"/> is empty: with no matching block every array is empty, diagnostics included.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="form"/>, <paramref name="blocks"/> or <paramref name="values"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="blocks"/> contains a <see langword="null"/> element.
    /// </exception>
    public static FieldEventResponse Run(
        FormMetadata form,
        IReadOnlyList<EventBlockNode> blocks,
        IReadOnlyDictionary<string, FieldValue> values)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(values);

        if (blocks.Count == 0)
        {
            return FieldEventResponse.Empty;
        }

        // Checked on a snapshot before anything runs, so a rejected call has evaluated nothing.
        EventBlockNode[] selected = blocks.ToArray();
        if (Array.Exists(selected, block => block is null))
        {
            throw new ArgumentException("An event block must not be null.", nameof(blocks));
        }

        var context = new EvaluationContext(form, values);
        var effects = new EffectAccumulator();
        foreach (EventBlockNode block in selected)
        {
            var feed = new StaticDiagnosticFeed(block, effects);
            ExecuteList(block.Statements, 0, context, effects, feed);
            feed.AddRemaining();
        }

        return effects.ToResponse();
    }

    /// <summary>
    /// Executes statements in order, each inside its own error boundary.
    /// </summary>
    /// <remarks>
    /// A <see cref="CheckCodeRuntimeException"/> becomes an <see cref="DiagnosticSeverity.Error"/> diagnostic at the
    /// exception's line and command. An <see cref="ArgumentOutOfRangeException"/> or <see cref="OverflowException"/>
    /// becomes one at the statement's line and command, with a fixed message. Either way the failing statement is
    /// skipped, the effects of earlier statements stand, and the next statement runs. Any other exception is a
    /// defect and propagates.
    /// </remarks>
    /// <param name="statements">The statements in source order.</param>
    /// <param name="context">The run's field scope.</param>
    /// <param name="effects">The run's effect accumulator.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="statements"/>, <paramref name="context"/> or <paramref name="effects"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="statements"/> contains a <see langword="null"/> element.
    /// </exception>
    public static void Execute(
        IReadOnlyList<StatementNode> statements,
        EvaluationContext context,
        EffectAccumulator effects)
    {
        ArgumentNullException.ThrowIfNull(statements);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(effects);

        ExecuteList(statements, 0, context, effects, feed: null);
    }

    // Execute's loop. firstIndex is the pre-order index, within its event block, of the first statement (see
    // EventBlockNode.DiagnosticAnchors). Before each statement runs, the feed adds the block's static diagnostics
    // that precede it; after the statement, failed or not, the index moves past its whole subtree. Indexes exist only
    // for the feed, so they are computed only while it still has a diagnostic to add: once it has none it is dropped,
    // for the rest of this list and for every branch this list runs, and firstIndex and index are no longer read.
    // Execute has no feed, a block without diagnostics has nothing to add, and a block without covering anchors adds
    // everything before its first statement, so none of them sizes a subtree, and a skipped branch is never walked.
    private static void ExecuteList(
        IReadOnlyList<StatementNode> statements,
        int firstIndex,
        EvaluationContext context,
        EffectAccumulator effects,
        StaticDiagnosticFeed? feed)
    {
        int index = firstIndex;
        for (var i = 0; i < statements.Count; i++)
        {
            StatementNode statement = statements[i]
                ?? throw new ArgumentException("A statement must not be null.", nameof(statements));

            feed?.AddThrough(index);
            feed = StaticDiagnosticFeed.WhilePending(feed);
            try
            {
                ExecuteStatement(statement, index, context, effects, feed);
            }
            catch (CheckCodeRuntimeException ex)
            {
                // Reason, not Message: Message repeats the form, line and command the diagnostic already carries.
                effects.AddDiagnostic(
                    new Diagnostic(DiagnosticSeverity.Error, context.FormName, ex.Line, ex.Command, ex.Reason));
            }
            catch (Exception ex) when (ex is ArgumentOutOfRangeException or OverflowException)
            {
                effects.AddDiagnostic(
                    new Diagnostic(
                        DiagnosticSeverity.Error,
                        context.FormName,
                        statement.Line,
                        CommandOf(statement),
                        OutOfRangeMessage));
            }

            // A branch of the statement may have added the last diagnostic.
            feed = StaticDiagnosticFeed.WhilePending(feed);
            if (feed is not null)
            {
                index += EventBlockNode.PreOrderSize(statement);
            }
        }
    }

    /// <summary>
    /// Evaluates an expression to a value.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description>
    /// A literal gives <see cref="ValueComparer.LiteralToValue"/>: <c>(.)</c> is <see cref="FieldValue.Null"/> and
    /// <c>(-)</c> is <see langword="false"/>.
    /// </description></item>
    /// <item><description>An identifier gives the named field's current value.</description></item>
    /// <item><description>
    /// A function call evaluates its arguments left to right and calls the registered function.
    /// </description></item>
    /// <item><description>
    /// <c>NOT</c> is <see langword="false"/> only when its operand is true as legacy <c>Rule_NotExp</c> reads it:
    /// the boolean <see langword="true"/>, or text that equals <c>true</c>, untrimmed, in a linguistic comparison
    /// that ignores case, made with the invariant culture where legacy used the current culture. So <c>NOT</c> of a
    /// null value, a number, a date, <c>" true"</c> or other text is <see langword="true"/>, while <c>NOT</c> of
    /// <c>tr</c>, a soft hyphen and <c>ue</c> is <see langword="false"/>, because that comparison ignores the soft
    /// hyphen. The result is a boolean everywhere except as an operand of <c>=</c>, below.
    /// </description></item>
    /// <item><description>
    /// <c>AND</c> evaluates both operands, with no short circuit, so an error on the right still surfaces, and is
    /// <see langword="true"/> only when both are true as an <c>IF</c> condition is, as in legacy
    /// <c>Rule_AndExp</c>: the boolean <see langword="true"/>, or text that equals <c>true</c>, untrimmed, in an
    /// ordinal comparison that ignores case. That comparison, unlike <c>NOT</c>'s, ignores no character.
    /// </description></item>
    /// <item><description>
    /// <c>=</c> evaluates left, then right, and compares with <see cref="ValueComparer.TryAreEqual"/>. An operand
    /// that is a <c>NOT</c>, which only parentheses can place beside <c>=</c>, is first turned into the text
    /// <c>"true"</c> or <c>"false"</c>, because legacy <c>Rule_NotExp</c> gives that text and legacy
    /// <c>Rule_CompareExp</c> compares it as text. So <c>"false" = (NOT "true")</c> holds, and
    /// <c>(NOT LastName) = 1</c> compares the text with the number and does not hold. A pair it cannot compare, a
    /// boolean on the left against a value that is not a yes or no spelling, is a type-mismatch runtime error.
    /// </description></item>
    /// </list>
    /// Precedence is already encoded in the tree by the parser, which folds a flat <c>AND</c> or <c>=</c> chain to
    /// the right. Such a chain is evaluated along its right spine in a loop, operands left to right, so its length
    /// costs no stack and its result is that of the right-associated tree. Nesting that still recurses, such as
    /// parentheses, <c>NOT</c> or a hand-built tree, is evaluated while stack remains; deeper nesting throws a
    /// <see cref="CheckCodeRuntimeException"/> with a fixed message, which <see cref="Execute"/> reports as one
    /// <see cref="DiagnosticSeverity.Error"/> diagnostic for its statement.
    /// </remarks>
    /// <param name="expression">The expression.</param>
    /// <param name="context">The run's field scope.</param>
    /// <param name="command">
    /// The upper-case keyword of the enclosing statement, for example <c>ASSIGN</c>, which a runtime error reports.
    /// </param>
    /// <returns>The expression's value.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="expression"/>, <paramref name="context"/> or <paramref name="command"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="CheckCodeRuntimeException">
    /// The expression names an unknown field or an unsupported function, a function call failed, an <c>=</c>
    /// compares a boolean on the left with a value that is not a yes or no spelling, the expression is of a kind
    /// outside the subset, or it is nested too deeply for the remaining stack.
    /// </exception>
    public static FieldValue Evaluate(ExpressionNode expression, EvaluationContext context, string command)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(command);

        // Nesting that still recurses (parentheses, NOT, a hand-built tree) stops while the stack can still unwind,
        // so it fails its statement with a diagnostic instead of overflowing the stack and ending the process.
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            throw new CheckCodeRuntimeException(expression.Line, command, ExpressionTooDeepMessage);
        }

        switch (expression)
        {
            case LiteralNode literal:
                return ValueComparer.LiteralToValue(literal);

            case IdentifierNode identifier:
                return context.Read(context.Resolve(identifier.Name, identifier.Line, command));

            case FunctionCallNode call:
                return EvaluateCall(call, context, command);

            case NotNode not:
                return FieldValue.FromBoolean(!IsTrueForNot(Evaluate(not.Operand, context, command)));

            case AndNode and:
                return EvaluateAnd(and, context, command);

            case EqualsNode equals:
                return EvaluateEquals(equals, context, command);

            default:
                throw new CheckCodeRuntimeException(expression.Line, command, UnsupportedExpressionMessage);
        }
    }

    // The parser folds a flat chain a AND b AND c to the right, And(a, And(b, c)), so the chain is walked along its
    // right spine in a loop and its length costs no stack. Every operand is evaluated in source order with no short
    // circuit, as legacy Rule_AndExp evaluated both sides, so an error in any later operand is never hidden.
    private static FieldValue EvaluateAnd(AndNode node, EvaluationContext context, string command)
    {
        bool all = true;
        ExpressionNode current = node;
        while (current is AndNode and)
        {
            all &= IsTrue(Evaluate(and.Left, context, command));
            current = and.Right;
        }

        bool last = IsTrue(Evaluate(current, context, command));
        return FieldValue.FromBoolean(all && last);
    }

    // a = b = c is Eq(a, Eq(b, c)): the operands are evaluated left to right along the right spine, then compared
    // from the right, so the result is that of the right-associated tree and the chain's length costs no stack.
    private static FieldValue EvaluateEquals(EqualsNode node, EvaluationContext context, string command)
    {
        // Each comparison keeps its own operator line, which a type mismatch reports.
        var lefts = new List<(FieldValue Value, int Line)>();
        ExpressionNode current = node;
        while (current is EqualsNode equals)
        {
            lefts.Add((EvaluateComparisonOperand(equals.Left, context, command), equals.Line));
            current = equals.Right;
        }

        FieldValue result = EvaluateComparisonOperand(current, context, command);
        for (var i = lefts.Count - 1; i >= 0; i--)
        {
            if (!ValueComparer.TryAreEqual(lefts[i].Value, result, out bool equal))
            {
                // Legacy throws from its boolean conversion here (Rule_CompareExp.cs:182-185); the statement
                // boundary turns this into one error diagnostic and skips only the enclosing statement.
                throw new CheckCodeRuntimeException(lefts[i].Line, command, BooleanComparisonMismatchMessage);
            }

            result = FieldValue.FromBoolean(equal);
        }

        return result;
    }

    // An operand of =. Legacy NOT gives the text "true" or "false", not a boolean (Rule_NotExp.cs:36-47), and a
    // parenthesised NOT, the only way one can stand beside =, passes that text through (Rule_ExprList.cs:32-45).
    // Rule_CompareExp.cs then compares it as text: trimmed and ignoring case against text (224-227), as "the left side
    // is not empty" against a boolean on the right (119-121), converted to a boolean against a boolean on the left
    // (182-185), and as a string against a number, because neither word parses as one (191-217). So the NOT's boolean
    // becomes that text here. Elsewhere NOT keeps its boolean, which IF and AND read as they read the text, and which
    // ASSIGN can give a checkbox.
    private static FieldValue EvaluateComparisonOperand(
        ExpressionNode operand,
        EvaluationContext context,
        string command)
    {
        FieldValue value = Evaluate(operand, context, command);
        return operand is NotNode && value.TryGetBoolean(out bool isTrue)
            ? FieldValue.FromText(isTrue ? "true" : "false")
            : value;
    }

    private static void ExecuteStatement(
        StatementNode statement,
        int index,
        EvaluationContext context,
        EffectAccumulator effects,
        StaticDiagnosticFeed? feed)
    {
        switch (statement)
        {
            case IfStatementNode node:
                ExecuteIf(node, index, context, effects, feed);
                break;

            case EnableStatementNode node:
                ExecuteEnablement(node.Names, node.Line, EnableCommand, context, effects.Enable);
                break;

            case DisableStatementNode node:
                ExecuteEnablement(node.Names, node.Line, DisableCommand, context, effects.Disable);
                break;

            case ClearStatementNode node:
                ExecuteClear(node, context, effects);
                break;

            case AssignStatementNode node:
                ExecuteAssign(node, context, effects);
                break;

            case DialogStatementNode node:
                effects.AddDialog(new DialogMessage(node.Message, node.Title));
                break;

            case GeocodeStatementNode:
                // Skipped by design: its Info diagnostic is already among the block's static diagnostics.
                break;

            default:
                throw new CheckCodeRuntimeException(statement.Line, CommandOf(statement), UnsupportedStatementMessage);
        }
    }

    // Legacy Rule_If_Then_Else_End: a true condition, the boolean true or the text "true" in any letter case, runs
    // THEN; false, null, numbers, dates and other text run ELSE. A runtime error in the condition escapes to this
    // statement's boundary, so neither branch runs. Each branch statement has its own boundary through ExecuteList.
    // In pre-order the IF at index is followed by its THEN statements and then by its ELSE statements. ExecuteList
    // passes a feed only while it has a diagnostic to add; without one no index is read, so the skipped THEN is not
    // sized.
    private static void ExecuteIf(
        IfStatementNode node,
        int index,
        EvaluationContext context,
        EffectAccumulator effects,
        StaticDiagnosticFeed? feed)
    {
        FieldValue condition = Evaluate(node.Condition, context, IfCommand);
        if (IsTrue(condition))
        {
            ExecuteList(node.Then, index + 1, context, effects, feed);
        }
        else if (node.Else is { } elseBranch)
        {
            int elseIndex = feed is null ? index + 1 : index + 1 + EventBlockNode.PreOrderSize(node.Then);
            ExecuteList(elseBranch, elseIndex, context, effects, feed);
        }
    }

    // Legacy Enable/Disable: every name, then a group's members, de-duplicated keeping the first occurrence. Any
    // field kind is accepted, labels and groups included, as legacy ChangeIsEnabled is. All names are resolved
    // before the first effect, so an unknown name fails the statement whole.
    private static void ExecuteEnablement(
        IReadOnlyList<string> names,
        int line,
        string command,
        EvaluationContext context,
        Action<string> record)
    {
        FieldMetadata[] fields = ResolveAll(names, line, command, context);

        var targets = new List<FieldMetadata>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (FieldMetadata field in fields)
        {
            AddOnce(targets, seen, field);
            foreach (FieldMetadata member in context.GetGroupMembers(field))
            {
                AddOnce(targets, seen, member);
            }
        }

        foreach (FieldMetadata target in targets)
        {
            record(target.Name);
        }
    }

    // Legacy Clear: a group stands for its members only, and only input fields are cleared, so labels, groups,
    // command buttons and unsupported fields are skipped without a diagnostic.
    private static void ExecuteClear(ClearStatementNode node, EvaluationContext context, EffectAccumulator effects)
    {
        FieldMetadata[] fields = ResolveAll(node.Names, node.Line, ClearCommand, context);

        var targets = new List<FieldMetadata>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (FieldMetadata field in fields)
        {
            IReadOnlyList<FieldMetadata> expanded = context.IsGroup(field) ? context.GetGroupMembers(field) : [field];
            foreach (FieldMetadata candidate in expanded)
            {
                if (context.IsDataField(candidate))
                {
                    AddOnce(targets, seen, candidate);
                }
            }
        }

        foreach (FieldMetadata target in targets)
        {
            context.Write(target, FieldValue.Null);
            effects.Clear(target.Name);
        }
    }

    private static void ExecuteAssign(AssignStatementNode node, EvaluationContext context, EffectAccumulator effects)
    {
        FieldMetadata target = context.Resolve(node.Target, node.Line, AssignCommand);
        FieldValue value = Evaluate(node.Value, context, AssignCommand);
        FieldValue coerced = CoerceForAssignment(target, context.GetFieldType(target), value, node.Line);

        context.Write(target, coerced);
        effects.Assign(target.Name, coerced);
    }

    // Legacy Rule_Assign handed the raw result to the host, which converted it to the control's type. Here the
    // value is converted by the target's mapped type, and a combination that has no exact conversion is a runtime
    // error rather than a guess. No validation rule applies (see the type remarks).
    private static FieldValue CoerceForAssignment(FieldMetadata field, FieldType type, FieldValue value, int line)
    {
        string? nonDataKind = type switch
        {
            FieldType.Label => "label",
            FieldType.Group => "group",
            FieldType.Unsupported => "unsupported",
            _ => null,
        };
        if (nonDataKind is not null)
        {
            throw new CheckCodeRuntimeException(
                line,
                AssignCommand,
                $"Cannot assign to {nonDataKind} field '{field.Name}'.");
        }

        // (.) assigns no value to any data field.
        if (value.IsNull)
        {
            return FieldValue.Null;
        }

        FieldValue? coerced = type switch
        {
            FieldType.Number => CoerceToNumber(value),
            FieldType.Checkbox => value.Kind == FieldValueKind.Boolean ? value : null,
            FieldType.Date => value.Kind == FieldValueKind.Date ? value : null,
            FieldType.Text or FieldType.Multiline or FieldType.LegalValues => CoerceToText(value),
            FieldType.Option => CoerceToOption(field, value, line),
            _ => null,
        };

        return coerced
            ?? throw new CheckCodeRuntimeException(
                line,
                AssignCommand,
                $"Type mismatch assigning to field '{field.Name}'.");
    }

    private static FieldValue? CoerceToNumber(FieldValue value)
    {
        if (value.Kind == FieldValueKind.Number)
        {
            return value;
        }

        return value.TryGetIndex(out int index) ? FieldValue.FromNumber(index) : null;
    }

    private static FieldValue? CoerceToText(FieldValue value)
    {
        if (value.Kind == FieldValueKind.Text)
        {
            return value;
        }

        // ToInvariantString rather than double.ToString(InvariantCulture): the same invariant round-trip text for
        // fractions (37.5), and the exact digits for a whole number, which a double would round or print in
        // exponent form beyond 1E+15.
        if (value.Kind == FieldValueKind.Number && value.ToInvariantString() is { } number)
        {
            return FieldValue.FromText(number);
        }

        return value.TryGetDate(out DateOnly date) ? FieldValue.FromText(FieldValue.FormatIsoDate(date)) : null;
    }

    // An option holds a 0-based index into its metaFields.List items. Integrality and range are checked before
    // FieldValue.FromIndex, so no factory exception can stand in for the diagnostic.
    private static FieldValue? CoerceToOption(FieldMetadata field, FieldValue value, int line)
    {
        long index;
        if (value.TryGetIndex(out int optionIndex))
        {
            index = optionIndex;
        }
        else if (value.TryGetInteger(out long whole))
        {
            index = whole;
        }
        else
        {
            return null;
        }

        int count = FieldTypeMapper.ParseOptionItems(field.List).Count;
        if (index < 0 || index >= count)
        {
            throw new CheckCodeRuntimeException(
                line,
                AssignCommand,
                $"Option index is out of range assigning to field '{field.Name}'.");
        }

        return FieldValue.FromIndex((int)index);
    }

    private static FieldValue EvaluateCall(FunctionCallNode call, EvaluationContext context, string command)
    {
        var arguments = new FieldValue[call.Arguments.Count];
        for (var i = 0; i < arguments.Length; i++)
        {
            arguments[i] = Evaluate(call.Arguments[i], context, command);
        }

        // The parser reports an unregistered function and builds no node for it; this guards a hand-built tree.
        if (!FunctionRegistry.TryGet(call.Name, out CheckCodeFunction? function))
        {
            throw new CheckCodeRuntimeException(call.Line, command, $"Unsupported function '{call.Name}'.");
        }

        FunctionResult result = function(arguments);
        if (result.Error is { } error)
        {
            throw new CheckCodeRuntimeException(call.Line, command, FunctionFailureReason(call.Name, error));
        }

        return result.Value;
    }

    // The registered functions already name themselves ("EPIWEEK requires a date"); the name is prefixed only to a
    // reason that does not, so every reason names its function exactly once. Both parts are value-free.
    private static string FunctionFailureReason(string name, string error) =>
        error.StartsWith(name, StringComparison.OrdinalIgnoreCase) ? error : $"{name}: {error}";

    // The truth of an IF condition and of each AND operand. Rule_If_Then_Else_End.cs:170 and Rule_AndExp.cs:41-44
    // lower the value's ToString() with the invariant culture and compare it ordinally with "true". So the boolean
    // true ("True") and the text "true" in any letter case are true, untrimmed; every other value, numbers, dates,
    // option indexes and other text included, is not. OrdinalIgnoreCase matches those ASCII letters without reading a
    // culture. A null value, which legacy could not turn into text, is not true either and never throws. NOT reads
    // truth with a culture comparison instead (IsTrueForNot).
    private static bool IsTrue(FieldValue value) =>
        value.TryGetBoolean(out bool isTrue)
            ? isTrue
            : value.TryGetText(out string? text) && string.Equals(text, "true", StringComparison.OrdinalIgnoreCase);

    // The truth of a NOT operand. Rule_NotExp.cs:40 compares the value's ToString() with "true" using
    // StringComparison.CurrentCultureIgnoreCase, a linguistic comparison; the invariant culture keeps it, as the text
    // rule of ValueComparer does, without depending on the server's culture. It ignores characters without collation
    // weight, such as the soft hyphen, so "tr" + U+00AD + "ue" is true here and not for IF or AND. Only text can
    // differ from IsTrue: the boolean true is "True", and no number, date or option index spells "true". A null
    // value, which legacy could not turn into text, is not true either and never throws.
    private static bool IsTrueForNot(FieldValue value) =>
        value.TryGetBoolean(out bool isTrue)
            ? isTrue
            : value.TryGetText(out string? text)
                && CultureInfo.InvariantCulture.CompareInfo.Compare(text, "true", CompareOptions.IgnoreCase) == 0;

    private static FieldMetadata[] ResolveAll(
        IReadOnlyList<string> names,
        int line,
        string command,
        EvaluationContext context)
    {
        var fields = new FieldMetadata[names.Count];
        for (var i = 0; i < fields.Length; i++)
        {
            fields[i] = context.Resolve(names[i], line, command);
        }

        return fields;
    }

    private static void AddOnce(List<FieldMetadata> targets, HashSet<string> seen, FieldMetadata field)
    {
        if (seen.Add(field.Name))
        {
            targets.Add(field);
        }
    }

    // The keyword a statement's diagnostics carry. A statement type outside the subset, which the parser never
    // builds, is named after its node type, such as HIDE for a HideStatementNode.
    private static string CommandOf(StatementNode statement) => statement switch
    {
        IfStatementNode => IfCommand,
        EnableStatementNode => EnableCommand,
        DisableStatementNode => DisableCommand,
        ClearStatementNode => ClearCommand,
        AssignStatementNode => AssignCommand,
        DialogStatementNode => DialogCommand,
        GeocodeStatementNode => GeocodeCommand,
        _ => KeywordFromNodeType(statement.GetType().Name),
    };

    private static string KeywordFromNodeType(string typeName)
    {
        foreach (string suffix in (ReadOnlySpan<string>)["StatementNode", "Node"])
        {
            if (typeName.Length > suffix.Length && typeName.EndsWith(suffix, StringComparison.Ordinal))
            {
                return typeName[..^suffix.Length].ToUpperInvariant();
            }
        }

        return typeName.ToUpperInvariant();
    }

    /// <summary>
    /// Adds one event block's static diagnostics to the accumulator at statement boundaries, in their source order:
    /// each one before the first statement to run whose pre-order index is at least its
    /// <see cref="EventBlockNode.AnchorOf(int)"/>, and the rest once the block has run. Runtime errors added in
    /// between therefore stay in source order.
    /// </summary>
    private sealed class StaticDiagnosticFeed(EventBlockNode block, EffectAccumulator effects)
    {
        // The index in block.Diagnostics of the first diagnostic not yet added.
        private int next;

        /// <summary>Whether a diagnostic is still to be added, and so still needs statement indexes.</summary>
        public bool HasPending => next < block.Diagnostics.Count;

        /// <summary>
        /// <paramref name="feed"/> while it still has a diagnostic to add; otherwise <see langword="null"/>, so the
        /// caller stops computing statement indexes.
        /// </summary>
        public static StaticDiagnosticFeed? WhilePending(StaticDiagnosticFeed? feed) =>
            feed is { HasPending: true } ? feed : null;

        /// <summary>
        /// Adds, in order, the diagnostics not yet added up to the first whose anchor is past
        /// <paramref name="statementIndex"/>.
        /// </summary>
        public void AddThrough(int statementIndex)
        {
            IReadOnlyList<Diagnostic> diagnostics = block.Diagnostics;
            int first = next;
            while (next < diagnostics.Count && block.AnchorOf(next) <= statementIndex)
            {
                next++;
            }

            if (next > first)
            {
                effects.AddStaticDiagnostics(diagnostics.Skip(first).Take(next - first));
            }
        }

        /// <summary>Adds every diagnostic not yet added, once the block has run.</summary>
        public void AddRemaining() => AddThrough(int.MaxValue);
    }
}
