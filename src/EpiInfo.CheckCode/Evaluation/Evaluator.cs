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
/// assignment to a label, group or unsupported field, a failed <c>YEARS</c> or <c>EPIWEEK</c> call) skips that
/// statement with one <see cref="DiagnosticSeverity.Error"/> diagnostic, keeps the effects of earlier statements and
/// continues with the next one, so a check-code error never blocks a save. Diagnostic messages name fields,
/// functions and constructs only, never a value.
/// </para>
/// <para>
/// The evaluator enforces no read-only, range, length, mask, legal-value or required rule: an <c>ASSIGN</c> to a
/// read-only field such as the seeded <c>OnsetWeek</c> is reported like any other, and the client shows it without
/// sending it. Validation belongs to the save path.
/// </para>
/// <para>
/// The class holds no state, performs no I/O and reads no clock or culture, so concurrent calls are safe and every
/// run of the same blocks over the same values gives the same response.
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

    /// <summary>
    /// Runs the selected event blocks of one field event over a record's values and returns the response.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The caller, <c>CheckCodeEngine.Run</c>, selects the blocks: every <c>Field</c> block whose name matches the
    /// event's field without regard to case, holding an event block of the event's kind, in source order. Each
    /// selected block's static diagnostics are added here, ahead of its runtime errors, so the caller must not add
    /// them again.
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
            effects.AddStaticDiagnostics(block.Diagnostics);
            Execute(block.Statements, context, effects);
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

        for (var i = 0; i < statements.Count; i++)
        {
            StatementNode statement = statements[i]
                ?? throw new ArgumentException("A statement must not be null.", nameof(statements));

            try
            {
                ExecuteStatement(statement, context, effects);
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
    /// <c>NOT</c> is <see langword="false"/> only when its operand is the boolean <see langword="true"/>, so
    /// <c>NOT</c> of a null value is <see langword="true"/>, as in legacy <c>Rule_NotExp</c>.
    /// </description></item>
    /// <item><description>
    /// <c>AND</c> evaluates both operands, with no short circuit, so an error on the right still surfaces, and is
    /// <see langword="true"/> only when both are the boolean <see langword="true"/>, as in legacy
    /// <c>Rule_AndExp</c>.
    /// </description></item>
    /// <item><description>
    /// <c>=</c> evaluates left, then right, and compares with <see cref="ValueComparer.AreEqual"/>.
    /// </description></item>
    /// </list>
    /// Precedence is already encoded in the tree by the parser.
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
    /// The expression names an unknown field or an unsupported function, a function call failed, or the expression
    /// is of a kind outside the subset.
    /// </exception>
    public static FieldValue Evaluate(ExpressionNode expression, EvaluationContext context, string command)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(command);

        switch (expression)
        {
            case LiteralNode literal:
                return ValueComparer.LiteralToValue(literal);

            case IdentifierNode identifier:
                return context.Read(context.Resolve(identifier.Name, identifier.Line, command));

            case FunctionCallNode call:
                return EvaluateCall(call, context, command);

            case NotNode not:
                return FieldValue.FromBoolean(!IsTrue(Evaluate(not.Operand, context, command)));

            case AndNode and:
                return EvaluateAnd(and, context, command);

            case EqualsNode equals:
                return EvaluateEquals(equals, context, command);

            default:
                throw new CheckCodeRuntimeException(expression.Line, command, UnsupportedExpressionMessage);
        }
    }

    private static FieldValue EvaluateAnd(AndNode node, EvaluationContext context, string command)
    {
        // Both sides first: legacy evaluated both operands, so an error on the right is never hidden.
        bool left = IsTrue(Evaluate(node.Left, context, command));
        bool right = IsTrue(Evaluate(node.Right, context, command));
        return FieldValue.FromBoolean(left && right);
    }

    private static FieldValue EvaluateEquals(EqualsNode node, EvaluationContext context, string command)
    {
        FieldValue left = Evaluate(node.Left, context, command);
        FieldValue right = Evaluate(node.Right, context, command);
        return FieldValue.FromBoolean(ValueComparer.AreEqual(left, right));
    }

    private static void ExecuteStatement(StatementNode statement, EvaluationContext context, EffectAccumulator effects)
    {
        switch (statement)
        {
            case IfStatementNode node:
                ExecuteIf(node, context, effects);
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

    // Legacy Rule_If_Then_Else_End: only a true condition runs THEN; false, null, text and numbers run ELSE. A
    // runtime error in the condition escapes to this statement's boundary, so neither branch runs. Each branch
    // statement has its own boundary through Execute.
    private static void ExecuteIf(IfStatementNode node, EvaluationContext context, EffectAccumulator effects)
    {
        FieldValue condition = Evaluate(node.Condition, context, IfCommand);
        if (IsTrue(condition))
        {
            Execute(node.Then, context, effects);
        }
        else if (node.Else is { } elseBranch)
        {
            Execute(elseBranch, context, effects);
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

    private static bool IsTrue(FieldValue value) => value.TryGetBoolean(out bool isTrue) && isTrue;

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
}
