using System.Globalization;

namespace EpiInfo.CheckCode.Evaluation;

/// <summary>
/// A runtime error in one check-code statement, carrying the statement's location and its value-free reason until
/// <c>Evaluator</c> turns it into a diagnostic.
/// </summary>
/// <remarks>
/// <para>
/// It is thrown while a statement executes for an unknown field, a type mismatch, an assignment to a label, group or
/// unsupported field, and a failed function (a <c>FunctionResult.Fail</c>, such as <c>EPIWEEK</c> without a date).
/// <c>Evaluator</c> catches it at each statement boundary and reports it as the
/// <see cref="EpiInfo.Domain.Diagnostics.DiagnosticSeverity.Error"/> diagnostic
/// <c>Diagnostic(Error, form, Line, Command, Reason)</c>, where <c>form</c> is the name of the form whose program
/// runs. The failing statement is skipped, the effects of earlier statements are kept and execution continues, so
/// the exception never escapes <c>Evaluator.Run</c>.
/// </para>
/// <para>
/// <see cref="Exception.Message"/> is the contextual message, composed with the invariant culture from the form name
/// when the throw site supplied one, the line, the command and the reason, for example
/// <c>Check-code runtime error in form 'FoodHistory' at line 18, command ASSIGN: EPIWEEK requires a date</c>.
/// <c>Diagnostic.Message</c> is built from <see cref="Reason"/>, never from <see cref="Exception.Message"/>, which
/// repeats the form, line and command the diagnostic already carries.
/// </para>
/// <para>
/// Redaction: <see cref="Reason"/> is client diagnostic text, returned in the field-event response. It must name
/// fields, functions and constructs only, as written in the program or the form metadata, and must never include a
/// field value, a literal value or any other user-entered text. <see cref="Exception.Message"/> therefore holds only
/// the form name, the line, the command keyword and that reason; the inner exception's message is never copied into
/// it, because a BCL message can embed a value. Neither <see cref="Exception.Message"/>, <see cref="Reason"/>,
/// <see cref="Exception.ToString"/> nor the inner exception's text is ever passed to a logger, and the exception is
/// never passed to a logger as its exception argument, because <see cref="Exception.ToString"/> includes the inner
/// exception's message. <c>FieldEventService</c> records the diagnostic's occurrence through a fixed message
/// template whose arguments are limited to the fields the redaction policy allows: the form, ids, operation, outcome,
/// status, exception type, SQL error number and trace id, never the message text. The diagnostic's full context
/// (severity, line, command and reason) travels to the client in the field-event response only.
/// </para>
/// </remarks>
public sealed class CheckCodeRuntimeException : Exception
{
    /// <summary>
    /// Creates the exception for a runtime error at <paramref name="line"/> whose throw site does not know the form.
    /// </summary>
    /// <remarks>
    /// <see cref="Form"/> is <see langword="null"/>, and <see cref="Exception.Message"/> reads, for example,
    /// <c>Check-code runtime error at line 18, command ASSIGN: EPIWEEK requires a date</c>.
    /// </remarks>
    /// <param name="line">The 1-based line of the failing statement in the form's check-code text.</param>
    /// <param name="command">The upper-case keyword of the enclosing statement, for example <c>ASSIGN</c>.</param>
    /// <param name="message">
    /// The value-free reason, which becomes <see cref="Reason"/> and <c>Diagnostic.Message</c>. It names fields,
    /// functions and constructs only, never a value.
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
    /// <paramref name="innerException"/>, whose throw site does not know the form.
    /// </summary>
    /// <remarks>
    /// <see cref="Form"/> is <see langword="null"/>, and <see cref="Exception.Message"/> reads, for example,
    /// <c>Check-code runtime error at line 18, command ASSIGN: EPIWEEK requires a date</c>.
    /// </remarks>
    /// <param name="line">The 1-based line of the failing statement in the form's check-code text.</param>
    /// <param name="command">The upper-case keyword of the enclosing statement, for example <c>ASSIGN</c>.</param>
    /// <param name="message">
    /// The value-free reason, which becomes <see cref="Reason"/> and <c>Diagnostic.Message</c>. It names fields,
    /// functions and constructs only, never a value; the inner exception's own message is not copied into it,
    /// because a BCL message can embed a value.
    /// </param>
    /// <param name="innerException">The exception that caused the error, or <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="command"/> or <paramref name="message"/> is <see langword="null"/>.
    /// </exception>
    public CheckCodeRuntimeException(int line, string command, string message, Exception? innerException)
        : this(line, command, message, innerException, form: null)
    {
    }

    /// <summary>
    /// Creates the exception for a runtime error at <paramref name="line"/> in the program of
    /// <paramref name="form"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="Exception.Message"/> reads, for example,
    /// <c>Check-code runtime error in form 'FoodHistory' at line 18, command ASSIGN: EPIWEEK requires a date</c>.
    /// </remarks>
    /// <param name="form">
    /// The name of the form (view) whose program runs, as stored in <c>metaViews.Name</c>, for example
    /// <c>FoodHistory</c>. It comes from the form metadata, never from request text.
    /// </param>
    /// <param name="line">The 1-based line of the failing statement in the form's check-code text.</param>
    /// <param name="command">The upper-case keyword of the enclosing statement, for example <c>ASSIGN</c>.</param>
    /// <param name="message">
    /// The value-free reason, which becomes <see cref="Reason"/> and <c>Diagnostic.Message</c>. It names fields,
    /// functions and constructs only, never a value.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="form"/>, <paramref name="command"/> or <paramref name="message"/> is
    /// <see langword="null"/>.
    /// </exception>
    public CheckCodeRuntimeException(string form, int line, string command, string message)
        : this(form, line, command, message, innerException: null)
    {
    }

    /// <summary>
    /// Creates the exception for a runtime error at <paramref name="line"/> in the program of
    /// <paramref name="form"/>, caused by <paramref name="innerException"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="Exception.Message"/> reads, for example,
    /// <c>Check-code runtime error in form 'FoodHistory' at line 18, command ASSIGN: EPIWEEK requires a date</c>.
    /// </remarks>
    /// <param name="form">
    /// The name of the form (view) whose program runs, as stored in <c>metaViews.Name</c>, for example
    /// <c>FoodHistory</c>. It comes from the form metadata, never from request text.
    /// </param>
    /// <param name="line">The 1-based line of the failing statement in the form's check-code text.</param>
    /// <param name="command">The upper-case keyword of the enclosing statement, for example <c>ASSIGN</c>.</param>
    /// <param name="message">
    /// The value-free reason, which becomes <see cref="Reason"/> and <c>Diagnostic.Message</c>. It names fields,
    /// functions and constructs only, never a value; the inner exception's own message is not copied into it,
    /// because a BCL message can embed a value.
    /// </param>
    /// <param name="innerException">The exception that caused the error, or <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="form"/>, <paramref name="command"/> or <paramref name="message"/> is
    /// <see langword="null"/>.
    /// </exception>
    public CheckCodeRuntimeException(string form, int line, string command, string message, Exception? innerException)
        : this(line, command, message, innerException, form ?? throw new ArgumentNullException(nameof(form)))
    {
    }

    // The one constructor that composes the message and sets the properties. The form comes last so that this
    // signature differs from every public one.
    private CheckCodeRuntimeException(int line, string command, string message, Exception? innerException, string? form)
        : base(ComposeMessage(form, line, command, message), innerException)
    {
        Form = form;
        Line = line;
        Command = command;
        Reason = message;
    }

    /// <summary>
    /// The name of the form (view) whose program raised the error, as stored in <c>metaViews.Name</c>, for example
    /// <c>FoodHistory</c>, or <see langword="null"/> when the throw site did not supply it. It comes from the form
    /// metadata, never from request text.
    /// </summary>
    public string? Form { get; }

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

    /// <summary>
    /// The value-free reason exactly as the throw site passed it, for example <c>EPIWEEK requires a date</c>. It
    /// becomes <c>Diagnostic.Message</c>, so it is client diagnostic text: it names fields, functions and constructs
    /// only, never a value, and it is never passed to a logger.
    /// </summary>
    public string Reason { get; }

    private static string ComposeMessage(string? form, int line, string command, string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(command);

        // The invariant culture keeps the line's digits and sign identical whatever the thread's culture.
        return form is null
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"Check-code runtime error at line {line}, command {command}: {message}")
            : string.Create(
                CultureInfo.InvariantCulture,
                $"Check-code runtime error in form '{form}' at line {line}, command {command}: {message}");
    }
}
