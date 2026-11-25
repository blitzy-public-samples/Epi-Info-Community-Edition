namespace EpiInfo.Domain.Diagnostics;

/// <summary>
/// One problem the check-code interpreter found in a form's program, reported instead of stopping the program.
/// On the wire it is <c>{severity, form, line, command, message}</c>.
/// </summary>
/// <remarks>
/// <para>
/// The interpreter recovers at statement and block boundaries, so one unsupported or failing construct yields one
/// diagnostic and the rest of the program still runs. Every diagnostic is also written to the log.
/// </para>
/// <para>
/// Diagnostics appear in two places:
/// </para>
/// <list type="bullet">
/// <item><description>
/// a form definition's <c>unsupportedCheckCode</c>, which holds only the <see cref="DiagnosticSeverity.Info"/> and
/// <see cref="DiagnosticSeverity.Warning"/> diagnostics that static analysis of the program text can know;
/// </description></item>
/// <item><description>
/// a field-event response's <c>diagnostics</c>, which holds the executed block's static diagnostics plus its runtime
/// <see cref="DiagnosticSeverity.Error"/> diagnostics, in source order.
/// </description></item>
/// </list>
/// <para>
/// The type is a plain value: two diagnostics are equal when all five members are equal. Property names and the
/// severity spellings <c>info</c>, <c>warning</c> and <c>error</c> are set by the API's JSON configuration, so the
/// type carries no serialisation attribute. The parameter order fixes the property order of the JSON and of the
/// OpenAPI schema.
/// </para>
/// </remarks>
/// <param name="Severity">
/// <see cref="DiagnosticSeverity.Info"/> for a skipped <c>GEOCODE</c> statement;
/// <see cref="DiagnosticSeverity.Warning"/> for an unsupported command, function, operator, literal, block,
/// <c>ELSE-IF</c> or syntax; <see cref="DiagnosticSeverity.Error"/> for a runtime error such as an unknown field,
/// a type mismatch or a missing <c>EPIWEEK</c> date.
/// </param>
/// <param name="Form">
/// The name of the form (view) whose program holds the construct, as stored in <c>metaViews.Name</c>, for example
/// <c>FoodHistory</c>.
/// </param>
/// <param name="Line">
/// The 1-based line number, in the form's <c>metaViews.CheckCode</c> program text, on which the construct begins.
/// </param>
/// <param name="Command">
/// The keyword of the command or construct the diagnostic concerns, for example <c>GEOCODE</c> or <c>GOTO</c>.
/// </param>
/// <param name="Message">
/// Fixed, human-readable text describing the problem. It is client diagnostic text, returned in the response: a form
/// definition's <c>unsupportedCheckCode</c> or a field-event response's <c>diagnostics</c>. It never contains a field
/// value, a <c>values</c> entry or any other request-supplied data; it may name an identifier taken from the program
/// text, such as a field name, because that is metadata. It is not a log argument and is never passed to a logger;
/// the log records each diagnostic's occurrence through a fixed message template whose arguments are limited to the
/// fields the redaction policy allows: the form, ids, operation, outcome, status, exception type, SQL error number
/// and trace id, never the message text.
/// </param>
public sealed record Diagnostic(DiagnosticSeverity Severity, string Form, int Line, string Command, string Message);
