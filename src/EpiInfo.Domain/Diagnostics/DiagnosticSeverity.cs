namespace EpiInfo.Domain.Diagnostics;

/// <summary>
/// How serious a check-code diagnostic is. Every diagnostic has exactly one severity.
/// </summary>
/// <remarks>
/// <para>
/// The check-code interpreter recovers from what it cannot execute and reports it as a diagnostic, so one
/// unsupported or failing statement never stops the rest of the program.
/// </para>
/// <para>
/// On the wire the members are the camelCase strings <c>info</c>, <c>warning</c> and <c>error</c>. The API's
/// per-type converter in <c>src/EpiInfo.Api/Json/JsonSetup.cs</c> produces them; this type carries no
/// serialisation attribute and its numeric values are not part of any contract.
/// </para>
/// <para>
/// A form definition's <c>unsupportedCheckCode</c> comes from static analysis of the program text, which yields
/// only <see cref="Info"/> and <see cref="Warning"/>. <see cref="Error"/> diagnostics arise only while a field
/// event runs, so they appear only in a field-event response.
/// </para>
/// </remarks>
public enum DiagnosticSeverity
{
    /// <summary>
    /// A statement that is recognised but deliberately skipped. The one case is <c>GEOCODE</c>, which needs an
    /// external geocoding service: it is logged and skipped (scenario <c>EC-GEO-01</c>).
    /// </summary>
    Info,

    /// <summary>
    /// A construct outside the supported subset: a command such as <c>GOTO</c>, <c>HIDE</c> or
    /// <c>SET-REQUIRED</c>; a function; an operator such as <c>OR</c>, <c>&lt;&gt;</c> or arithmetic; a literal
    /// such as <c>(+)</c> or a date literal; a <c>View</c>, <c>Record</c>, <c>Page</c>, <c>DefineVariables</c> or
    /// <c>Sub</c> block; <c>ELSE-IF</c>; or syntax that cannot be parsed. The construct is skipped and the program
    /// keeps running past it.
    /// </summary>
    Warning,

    /// <summary>
    /// A runtime evaluation error, such as an unknown field, a type mismatch or a missing <c>EPIWEEK</c> date. The
    /// failing statement is skipped, the effects of earlier statements are kept, and a check-code error never
    /// blocks a save.
    /// </summary>
    Error,
}
