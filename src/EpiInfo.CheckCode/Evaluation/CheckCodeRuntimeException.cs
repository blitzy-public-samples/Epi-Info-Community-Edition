namespace EpiInfo.CheckCode.Evaluation;

/// <summary>
/// A runtime error in one check-code statement, carrying the statement's location until <c>Evaluator</c> turns it
/// into a diagnostic.
/// </summary>
/// <remarks>
/// <para>
/// It is thrown while a statement executes for an unknown field, a type mismatch, an assignment to a label, group or
/// unsupported field, and a failed function (a <c>FunctionResult.Fail</c>, such as <c>EPIWEEK</c> without a date).
/// <c>Evaluator</c> catches it at each statement boundary and reports it as a
/// <see cref="EpiInfo.Domain.Diagnostics.DiagnosticSeverity.Error"/> diagnostic built from <see cref="Line"/>,
/// <see cref="Command"/> and <see cref="Exception.Message"/>. The failing statement is skipped, the effects of earlier
/// statements are kept and execution continues, so the exception never escapes <c>Evaluator.Run</c>.
/// </para>
/// <para>
/// Redaction: the message becomes <c>Diagnostic.Message</c>, which is returned to the client in the field-event
/// response and logged by <c>FieldEventService</c>. It must name fields, functions and constructs only, as written in
/// the program or the form metadata, and must never include a field value, a literal value or any other user-entered
/// text.
/// </para>
/// </remarks>
public sealed class CheckCodeRuntimeException : Exception
{
    /// <summary>Creates the exception for a runtime error at <paramref name="line"/>.</summary>
    /// <param name="line">The 1-based line of the failing statement in the form's check-code text.</param>
    /// <param name="command">The upper-case keyword of the enclosing statement, for example <c>ASSIGN</c>.</param>
    /// <param name="message">
    /// The diagnostic message. It names fields, functions and constructs only, never a value.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="command"/> or <paramref name="message"/> is <see langword="null"/>.
    /// </exception>
    public CheckCodeRuntimeException(int line, string command, string message)
        : this(line, command, message, innerException: null)
    {
    }

    /// <summary>
    /// Creates the exception for a runtime error at <paramref name="line"/> caused by
    /// <paramref name="innerException"/>.
    /// </summary>
    /// <param name="line">The 1-based line of the failing statement in the form's check-code text.</param>
    /// <param name="command">The upper-case keyword of the enclosing statement, for example <c>ASSIGN</c>.</param>
    /// <param name="message">
    /// The diagnostic message. It names fields, functions and constructs only, never a value; the inner exception's
    /// own message is not copied into it, because a BCL message can embed a value.
    /// </param>
    /// <param name="innerException">The exception that caused the error, or <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="command"/> or <paramref name="message"/> is <see langword="null"/>.
    /// </exception>
    public CheckCodeRuntimeException(int line, string command, string message, Exception? innerException)
        : base(message ?? throw new ArgumentNullException(nameof(message)), innerException)
    {
        ArgumentNullException.ThrowIfNull(command);

        Line = line;
        Command = command;
    }

    /// <summary>
    /// The 1-based line of the failing statement in the <c>metaViews.CheckCode</c> text, as carried by the syntax
    /// node's <c>Line</c>. It becomes <c>Diagnostic.Line</c>.
    /// </summary>
    public int Line { get; }

    /// <summary>
    /// The upper-case keyword of the enclosing statement: <c>ASSIGN</c>, <c>IF</c>, <c>ENABLE</c>, <c>DISABLE</c>,
    /// <c>CLEAR</c> or <c>DIALOG</c>. It becomes <c>Diagnostic.Command</c>.
    /// </summary>
    public string Command { get; }
}
