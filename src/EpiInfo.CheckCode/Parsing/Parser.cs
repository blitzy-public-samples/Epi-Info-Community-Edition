using System.Collections.Frozen;
using System.Globalization;
using EpiInfo.CheckCode.Lexing;
using EpiInfo.CheckCode.Syntax;
using EpiInfo.Domain.Diagnostics;
using EpiInfo.Domain.Events;
using EpiInfo.Domain.Metadata;

namespace EpiInfo.CheckCode.Parsing;

/// <summary>
/// The recursive-descent parser of the check-code language: it turns the <see cref="Lexer"/>'s tokens into the
/// syntax tree the evaluator runs, and turns every construct outside the supported subset into a
/// <see cref="Diagnostic"/>. Line numbers in the comments of this file refer to <c>EpiInfo.Enter.Grammar.grm</c>,
/// the language specification.
/// </summary>
/// <remarks>
/// <para>
/// The subset is the <c>Field</c> block with its <c>Before</c>, <c>After</c> and <c>Click</c> event blocks;
/// <c>IF … THEN … [ELSE …] END-IF | END</c>; <c>ASSIGN</c>, <c>ENABLE</c>, <c>DISABLE</c>, <c>CLEAR</c>,
/// <c>DIALOG "text" [TITLETEXT = "title"]</c> and <c>GEOCODE</c>; the operators <c>=</c>, <c>NOT</c> and
/// <c>AND</c>; string literals, unsuffixed decimal and real literals, <c>(.)</c> and <c>(-)</c>; and calls of the
/// functions <see cref="FunctionNames"/> supports. Everything else yields a <see cref="DiagnosticSeverity.Warning"/>
/// and is left out of the tree; each <c>GEOCODE</c> statement yields one <see cref="DiagnosticSeverity.Info"/>. The
/// parser never reports a <see cref="DiagnosticSeverity.Error"/>.
/// </para>
/// <para>
/// Recovery works at grammar boundaries: an unsupported statement is skipped up to the next command, block
/// terminator or implicit assignment (<c>a = 1</c>, line 285), an <c>ASSIGN</c> whose expression does not end the
/// statement is dropped whole, an <c>IF</c> with an unsupported condition or an <c>ELSE-IF</c> is dropped up to its
/// own <c>END-IF</c> or <c>END</c>, an unsupported block (<c>View</c>, <c>Form</c>, <c>Record</c>, <c>Page</c>,
/// <c>DefineVariables</c>, <c>Sub</c>) is skipped up to its <c>End-*</c> keyword, an event block that repeats a
/// kind or comes after a later kind in its <c>Field</c> block is skipped through its terminator, and the rest of the
/// program still parses. A <c>/*</c> without its <c>*/</c> (lines 50-51) hides the rest of the program: it is
/// reported once, at its own line, in place of the terminators it hides, and the statements before it still parse.
/// </para>
/// <para>
/// A <see cref="TokenKind.File"/> or <see cref="TokenKind.Url"/> operand (lines 25-26) is one opaque token that no
/// supported statement takes: it is skipped with the unsupported statement or block it belongs to, such as
/// <c>EXECUTE</c>, <c>WAITFOR</c> or <c>COMMANDLINE</c>, so a reserved word inside a path delimits nothing; at a
/// statement start it is reported as <see cref="Commands.Syntax"/>, and as a value as a malformed expression. The
/// parser performs no I/O and runs nothing.
/// </para>
/// <para>
/// A keyword position takes only a <see cref="TokenKind.Keyword"/> token: a statement's command, <c>THEN</c>,
/// <c>TITLETEXT</c> and the built-in values written without parentheses (line 254). A bracketed name is always an
/// identifier (line 30), so <c>[ASSIGN]</c> starts no statement, <c>[THEN]</c> and <c>[TITLETEXT]</c> delimit
/// nothing, and <c>[SYSTEMDATE]</c> is the field named <c>SYSTEMDATE</c>. A name slot (the <c>ASSIGN</c> target,
/// each <c>GEOCODE</c> argument, each name of <c>ENABLE</c>, <c>DISABLE</c> and <c>CLEAR</c>, a name in an
/// expression) takes any identifier, bracketed keyword names included, or a keyword the parser does not reserve,
/// so a missing name leaves the keyword of the next statement, event or block unconsumed.
/// </para>
/// <para>
/// Diagnostics carry the form name and the 1-based line of the token where the construct starts. Their
/// <see cref="Diagnostic.Command"/> and <see cref="Diagnostic.Message"/> come only from fixed text, keyword and
/// operator spellings and function names, never from string, character or number literals, paths or URLs. A function
/// name is fixed text only for a supported function: an unsupported function's command is its name as written in
/// the program, in upper case, which can be any identifier, a field's name or a bracketed name holding whitespace and
/// line breaks, and its message repeats it. Such a name can also be spelled like vocabulary, as <c>[GOTO]</c> and
/// <c>FORMAT</c> are, so only <see cref="ReportsUnsupportedFunction"/> tells its diagnostic apart. That is why the API
/// logs a diagnostic's command only when <see cref="ReportsUnsupportedFunction"/> rejects the diagnostic and
/// <see cref="IsVocabularyCommand"/> accepts the command, logs every unsupported function's diagnostic under one fixed
/// code, whatever its name's spelling, and never logs a message. A literal the subset does not run, such as
/// <c>12L</c>, <c>#1a</c> or <c>'x'</c> (lines 38-41), is reported once under its kind's pseudo-command and drops its
/// statement.
/// </para>
/// <para>
/// Each call works on its own state and the keyword tables are immutable, so the parser is safe to call
/// concurrently. It never throws for any token list; the one exception is the
/// <see cref="OperationCanceledException"/> of a cancelled call, which it polls for as it consumes tokens.
/// </para>
/// </remarks>
public static class Parser
{
    /// <summary>
    /// The deepest nesting the parser follows, counting nested <c>IF</c> statements, parenthesised
    /// sub-expressions and function calls together. Going deeper yields one <see cref="Commands.Nesting"/>
    /// warning and skips the rest of the enclosing event block; the statements parsed before it are kept.
    /// </summary>
    public const int MaxNestingDepth = 64;

    private const string StatementSkipped = "statement skipped";
    private const string IfSkipped = "IF skipped";

    // The opening of the message of every unsupported function's diagnostic, and of no other diagnostic's message;
    // ReportsUnsupportedFunction recognises that diagnostic by it.
    private const string UnsupportedFunctionText = "Unsupported function ";

    private static readonly FrozenSet<string> SupportedCommands = Set(
        Commands.If, Commands.Assign, Commands.Enable, Commands.Disable, Commands.Clear, Commands.Dialog,
        Commands.Geocode);

    // Lines 76-142: every other statement keyword of the grammar.
    private static readonly FrozenSet<string> UnsupportedCommands = Set(
        "ALWAYS", "LET", "AUTOSEARCH", "BEEP", "CALL", "COMMANDLINE", "NEWRECORD", "EXECUTE", "EXIT", "FILEDIALOG",
        "IOCODE", "GETPATH", "GOTO", "GOTOPAGE", "GOTOFORM", "HELP", "HIDE", "HIGHLIGHT", "QUIT", "UNDEFINE",
        "UNHIDE", "UNHIGHLIGHT", "WAITFOR", "WAITFOREXIT", "WAITFORFILEEXISTS", "SET-REQUIRED", "SET-NOT-REQUIRED",
        "SAVE-RECORD", "DEFINE");

    // Lines 297, 404, 452, 609, 645: the statements that are their command keyword alone, so each is complete
    // without an operand.
    private static readonly FrozenSet<string> ZeroOperandCommands = Set(
        "BEEP", "EXIT", "NEWRECORD", "QUIT", "SAVE-RECORD");

    private static readonly FrozenSet<string> CommandKeywords = Set([.. SupportedCommands, .. UnsupportedCommands]);

    private static readonly FrozenSet<string> IfTerminators = Set(
        Commands.Else, Commands.ElseIf, Commands.EndIf, Commands.End);

    // Lines 620-622.
    private static readonly FrozenDictionary<string, EventSyntax> EventStarts =
        new Dictionary<string, EventSyntax>(StringComparer.Ordinal)
        {
            ["BEFORE"] = new(FieldEventKind.FieldBefore, "END-BEFORE"),
            ["AFTER"] = new(FieldEventKind.FieldAfter, "END-AFTER"),
            ["CLICK"] = new(FieldEventKind.FieldClick, "END-CLICK"),
        }.ToFrozenDictionary(StringComparer.Ordinal);

    // Lines 624-640: each top-level block keyword and its terminator.
    private static readonly FrozenDictionary<string, string> BlockStarts =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Commands.Field] = Commands.EndField,
            [Commands.View] = "END-VIEW",
            [Commands.Form] = "END-FORM",
            [Commands.Record] = "END-RECORD",
            [Commands.Page] = "END-PAGE",
            [Commands.Sub] = "END-SUB",
            [Commands.DefineVariables] = "END-DEFINEVARIABLES",
        }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly FrozenSet<string> EventTerminators = Set([.. EventStarts.Values.Select(e => e.Terminator)]);

    private static readonly FrozenSet<string> BlockTerminators = Set([.. BlockStarts.Values]);

    private static readonly FrozenSet<string> ExpressionKeywords = Set(
        Commands.And, Commands.Or, Commands.Xor, Commands.Not, Commands.Mod, Commands.Like, Commands.Then,
        Commands.TitleText, "EXCEPT", "DISPLAYLIST");

    // Line 254: built-in values written without parentheses.
    private static readonly FrozenSet<string> ZeroArgFunctions = Set(
        "SYSBARCODE", "SYSLATITUDE", "SYSLONGITUDE", "SYSALTITUDE", "SYSTEMTIME", "SYSTEMDATE", "RECORDCOUNT");

    // Keywords that end an event block's statement list without being consumed by it.
    private static readonly FrozenSet<string> EventStopKeywords = Set(
        [.. EventTerminators, .. BlockTerminators, .. EventStarts.Keys, .. BlockStarts.Keys]);

    // Inside an IF branch the IF terminators also end the list.
    private static readonly FrozenSet<string> IfStopKeywords = Set([.. EventStopKeywords, .. IfTerminators]);

    // The keywords a statement ends before: the next command, or any keyword that ends a statement list.
    // ParseRun.AtStatementBoundary adds the start of an implicit assignment, which has no keyword.
    private static readonly FrozenSet<string> BoundaryKeywords = Set([.. IfStopKeywords, .. CommandKeywords]);

    // Words that cannot stand as a field name outside brackets in a name list or an expression.
    private static readonly FrozenSet<string> Reserved = Set(
        [.. BoundaryKeywords, .. ExpressionKeywords, .. ZeroArgFunctions]);

    // Every Diagnostic.Command spelling that is language vocabulary rather than program text: the keyword tables
    // above, the lexer's reserved words, the supported function names and the Commands constants the tables do not
    // already hold. Declared after the sets it is built from, because static initialisers run in textual order.
    private static readonly FrozenSet<string> VocabularyCommands = Set(
        [
            .. CommandKeywords, .. IfTerminators, .. EventStarts.Keys, .. EventTerminators, .. BlockStarts.Keys,
            .. BlockTerminators, .. ExpressionKeywords, .. ZeroArgFunctions, .. Lexer.Keywords,
            .. Lexer.HyphenatedKeywords, .. FunctionNames.Supported,
            Commands.Syntax, Commands.Nesting, Commands.ImplicitAssign, Commands.QualifiedName, Commands.GridField,
            Commands.ExpressionList, Commands.NumberLiteral, Commands.CharLiteral, Commands.DateLiteral,
            Commands.TimeLiteral, Commands.CheckedLiteral, Commands.EnableExcept, Commands.DisableExcept,
            Commands.NotEqual, Commands.Less, Commands.Greater, Commands.LessOrEqual, Commands.GreaterOrEqual,
            Commands.Concatenate, Commands.Plus, Commands.Minus, Commands.Multiply, Commands.Divide, Commands.Power,
            Commands.Percent, Commands.LibraryCall, Commands.Limit,
        ]);

    /// <summary>Parses the tokens of one form's check-code program.</summary>
    /// <param name="tokens">
    /// The tokens as <see cref="Lexer.Tokenize(string?)"/> returns them. <see langword="null"/> is treated as an
    /// empty list, and a list without a trailing <see cref="TokenKind.EndOfInput"/> as if it had one; parsing stops
    /// at the first <see cref="TokenKind.EndOfInput"/>.
    /// </param>
    /// <param name="formName">
    /// The <see cref="Diagnostic.Form"/> of every diagnostic; <see langword="null"/> is treated as empty.
    /// </param>
    /// <param name="cancellationToken">
    /// The request's abort token, polled as the parse consumes tokens: at its first step and every 1,024th after it.
    /// </param>
    /// <returns>
    /// Every <c>Field</c> block in source order and every static diagnostic in source order, by line and then by
    /// column, or <see cref="ProgramNode.Empty"/> when there is neither. Never <see langword="null"/>; for any token
    /// list the method throws only when <paramref name="cancellationToken"/> is cancelled.
    /// </returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static ProgramNode Parse(
        IReadOnlyList<Token>? tokens,
        string? formName,
        CancellationToken cancellationToken = default) =>
        new ParseRun(tokens, formName ?? string.Empty, cancellationToken).Execute();

    /// <summary>Parses the tokens of a form's check-code program, naming the form in each diagnostic.</summary>
    /// <param name="tokens">
    /// The tokens, as for <see cref="Parse(IReadOnlyList{Token}, string, CancellationToken)"/>.
    /// </param>
    /// <param name="form">
    /// The form whose <see cref="FormMetadata.Name"/> becomes the <see cref="Diagnostic.Form"/> of every diagnostic;
    /// <see langword="null"/> gives an empty form name.
    /// </param>
    /// <param name="cancellationToken">
    /// The request's abort token, as for <see cref="Parse(IReadOnlyList{Token}, string, CancellationToken)"/>.
    /// </param>
    /// <returns>
    /// The parsed program, as for <see cref="Parse(IReadOnlyList{Token}, string, CancellationToken)"/>.
    /// </returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static ProgramNode Parse(
        IReadOnlyList<Token>? tokens,
        FormMetadata? form,
        CancellationToken cancellationToken = default) =>
        Parse(tokens, form?.Name, cancellationToken);

    /// <summary>
    /// Whether a <see cref="Diagnostic.Command"/> is fixed language vocabulary: a keyword or reserved word of the
    /// lexer, a supported function name, or one of the <see cref="Commands"/> spellings, compared ordinally as the
    /// parser and the evaluator report them, in upper case.
    /// </summary>
    /// <remarks>
    /// Every command the parser and the evaluator report is such a spelling, except an unsupported function's name:
    /// that is taken from the program as written, in upper case, such as <c>DAYS</c> or a bracketed name like
    /// <c>[Any Text]</c> with its inner whitespace and line breaks, and it passes this check whenever it is spelled
    /// like vocabulary, as <c>FORMAT</c> and the bracketed <c>[GOTO]</c> are. The spelling alone therefore cannot tell
    /// such a name from a statement's keyword. A caller that must keep program text out of what it writes, such as a
    /// log, first sets aside every diagnostic <see cref="ReportsUnsupportedFunction"/> recognises and only then accepts
    /// a command this check accepts, so every unsupported function's diagnostic gets the caller's fixed code, whatever
    /// its name's spelling.
    /// </remarks>
    /// <param name="command">A diagnostic's command; <see langword="null"/> is accepted.</param>
    /// <returns>
    /// <see langword="true"/> for a vocabulary spelling; <see langword="false"/> for <see langword="null"/> and for any
    /// other text, which is the name of an unsupported function taken from the program text.
    /// </returns>
    public static bool IsVocabularyCommand(string? command) =>
        command is not null && VocabularyCommands.Contains(command);

    /// <summary>
    /// Whether a diagnostic is the parser's report of an unsupported function: a call of a function the subset does
    /// not support, such as <c>DAYS(a, b)</c>, <c>FORMAT(x)</c> or <c>[GOTO](1)</c>, or a built-in value written
    /// without parentheses, such as <c>SYSTEMDATE</c> (line 254).
    /// </summary>
    /// <remarks>
    /// Such a diagnostic's <see cref="Diagnostic.Command"/> is the function's name as the program writes it, in upper
    /// case, which is program text even where it is spelled like vocabulary, so <see cref="IsVocabularyCommand"/>
    /// cannot tell it from a statement's keyword. Its <see cref="Diagnostic.Message"/> starts, compared ordinally,
    /// with <c>Unsupported function </c>, the command and <c>;</c>, which the message of no other diagnostic the
    /// parser or the evaluator reports does: an unsupported statement's message starts <c>Unsupported command</c>, and
    /// the evaluator's guard against an unregistered function in a hand-built tree names the function in quotes under
    /// the enclosing statement's command, so neither is such a report.
    /// </remarks>
    /// <param name="diagnostic">A diagnostic the parser or the evaluator reported.</param>
    /// <returns>
    /// <see langword="true"/> for an unsupported function's diagnostic; <see langword="false"/> for every other
    /// diagnostic, and for one without a command or a message.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="diagnostic"/> is <see langword="null"/>.</exception>
    public static bool ReportsUnsupportedFunction(Diagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);

        // Members a deserialiser or a caller left null hold no report.
        string? message = diagnostic.Message;
        string? command = diagnostic.Command;
        return message is not null
            && command is not null
            && message.StartsWith(string.Concat(UnsupportedFunctionText, command, ";"), StringComparison.Ordinal);
    }

    private static FrozenSet<string> Set(params string[] entries) => entries.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// The <see cref="Diagnostic.Command"/> spellings the parser reports under its own names: pseudo-commands for
    /// constructs that have no keyword, the unsupported operators and literals, the unsupported block keywords,
    /// and the statement keywords whose malformed forms it reports.
    /// </summary>
    /// <remarks>
    /// An unsupported statement is reported under its own keyword in upper case (<c>GOTO</c>, <c>HIDE</c>,
    /// <c>SET-REQUIRED</c>), and a call of an unsupported function under its name in upper case (<c>DAYS</c>,
    /// <c>FORMAT</c>), so those spellings have no constant here.
    /// </remarks>
    public static class Commands
    {
        /// <summary>Text that is no recognisable construct, or a token that is not a keyword.</summary>
        public const string Syntax = "SYNTAX";

        /// <summary>Nesting deeper than <see cref="MaxNestingDepth"/>.</summary>
        public const string Nesting = "NESTING";

        /// <summary>An assignment written without <c>ASSIGN</c>, such as <c>a = 1</c> (line 285).</summary>
        public const string ImplicitAssign = "IMPLICIT-ASSIGN";

        /// <summary>A qualified name such as <c>Form.Field</c> (line 181).</summary>
        public const string QualifiedName = "QUALIFIED-NAME";

        /// <summary>A grid cell reference such as <c>Grid[1, 2]</c> (line 177).</summary>
        public const string GridField = "GRID-FIELD";

        /// <summary>A parenthesised expression list such as <c>(a, b)</c> (line 528).</summary>
        public const string ExpressionList = "EXPRESSION-LIST";

        /// <summary>
        /// A number literal with no finite invariant-culture value: a <c>DecLiteral</c> with its <c>U</c>/<c>L</c>
        /// suffix such as <c>12L</c>, a <c>HexLiteral</c> such as <c>#1a</c> (lines 38-39), or an overflow such as
        /// <c>1E999</c>.
        /// </summary>
        public const string NumberLiteral = "NUMBER-LITERAL";

        /// <summary>A character literal such as <c>'x'</c> (line 41).</summary>
        public const string CharLiteral = "CHAR-LITERAL";

        /// <summary>A date literal (line 44).</summary>
        public const string DateLiteral = "DATE-LITERAL";

        /// <summary>A time literal (line 45).</summary>
        public const string TimeLiteral = "TIME-LITERAL";

        /// <summary>The checked literal <c>(+)</c> (line 42).</summary>
        public const string CheckedLiteral = "(+)";

        /// <summary>The <c>ENABLE * EXCEPT</c> form (line 424).</summary>
        public const string EnableExcept = "ENABLE * EXCEPT";

        /// <summary>The <c>DISABLE * EXCEPT</c> form (line 426).</summary>
        public const string DisableExcept = "DISABLE * EXCEPT";

        /// <summary>The <c>IF</c> statement (lines 434-440).</summary>
        public const string If = "IF";

        /// <summary>The <c>THEN</c> keyword of an <c>IF</c>.</summary>
        public const string Then = "THEN";

        /// <summary>The <c>ELSE</c> branch of an <c>IF</c>.</summary>
        public const string Else = "ELSE";

        /// <summary>The unsupported <c>ELSE-IF</c> branch (line 442).</summary>
        public const string ElseIf = "ELSE-IF";

        /// <summary>The <c>END-IF</c> terminator.</summary>
        public const string EndIf = "END-IF";

        /// <summary>The <c>END</c> terminator, which closes an <c>IF</c> or an <c>ALWAYS</c>.</summary>
        public const string End = "END";

        /// <summary>The <c>ASSIGN</c> statement (line 281).</summary>
        public const string Assign = "ASSIGN";

        /// <summary>The <c>ENABLE</c> statement (line 423).</summary>
        public const string Enable = "ENABLE";

        /// <summary>The <c>DISABLE</c> statement (line 425).</summary>
        public const string Disable = "DISABLE";

        /// <summary>The <c>CLEAR</c> statement (line 301).</summary>
        public const string Clear = "CLEAR";

        /// <summary>The <c>DIALOG</c> statement (line 364).</summary>
        public const string Dialog = "DIALOG";

        /// <summary>The <c>TITLETEXT</c> option of <c>DIALOG</c> (line 385).</summary>
        public const string TitleText = "TITLETEXT";

        /// <summary>The <c>GEOCODE</c> statement (line 636), reported as skipped.</summary>
        public const string Geocode = "GEOCODE";

        /// <summary>The <c>Field</c> block (line 627).</summary>
        public const string Field = "FIELD";

        /// <summary>The <c>End-Field</c> terminator.</summary>
        public const string EndField = "END-FIELD";

        /// <summary>The unsupported <c>View</c> block (line 629).</summary>
        public const string View = "VIEW";

        /// <summary>The unsupported <c>Form</c> block, the <c>View</c> synonym (line 629).</summary>
        public const string Form = "FORM";

        /// <summary>The unsupported <c>Record</c> block (line 630).</summary>
        public const string Record = "RECORD";

        /// <summary>The unsupported <c>Page</c> block (line 632).</summary>
        public const string Page = "PAGE";

        /// <summary>The unsupported <c>Sub</c> block (line 640).</summary>
        public const string Sub = "SUB";

        /// <summary>The unsupported <c>DefineVariables</c> block (line 624).</summary>
        public const string DefineVariables = "DEFINEVARIABLES";

        /// <summary>The supported conjunction <c>AND</c> (line 490).</summary>
        public const string And = "AND";

        /// <summary>The supported negation <c>NOT</c> (line 493).</summary>
        public const string Not = "NOT";

        /// <summary>The unsupported operator <c>OR</c> (line 486).</summary>
        public const string Or = "OR";

        /// <summary>The unsupported operator <c>XOR</c> (line 487).</summary>
        public const string Xor = "XOR";

        /// <summary>The unsupported operator <c>MOD</c> (line 514).</summary>
        public const string Mod = "MOD";

        /// <summary>The unsupported operator <c>LIKE</c> (line 496).</summary>
        public const string Like = "LIKE";

        /// <summary>The unsupported operator <c>&lt;&gt;</c> (line 498).</summary>
        public const string NotEqual = "<>";

        /// <summary>The unsupported operator <c>&lt;</c> (line 501).</summary>
        public const string Less = "<";

        /// <summary>The unsupported operator <c>&gt;</c> (line 499).</summary>
        public const string Greater = ">";

        /// <summary>The unsupported operator <c>&lt;=</c> (line 502).</summary>
        public const string LessOrEqual = "<=";

        /// <summary>The unsupported operator <c>&gt;=</c> (line 500).</summary>
        public const string GreaterOrEqual = ">=";

        /// <summary>The unsupported concatenation operator <c>&amp;</c> (line 505).</summary>
        public const string Concatenate = "&";

        /// <summary>The unsupported addition and unary plus operator <c>+</c> (lines 508, 522).</summary>
        public const string Plus = "+";

        /// <summary>The unsupported subtraction and negation operator <c>-</c> (lines 509, 521).</summary>
        public const string Minus = "-";

        /// <summary>The unsupported multiplication operator <c>*</c> (line 512).</summary>
        public const string Multiply = "*";

        /// <summary>The unsupported division operator <c>/</c> (line 513).</summary>
        public const string Divide = "/";

        /// <summary>The unsupported power operator <c>^</c> (line 518).</summary>
        public const string Power = "^";

        /// <summary>The unsupported modulus operator <c>%</c> (line 515).</summary>
        public const string Percent = "%";

        /// <summary>The library-call separator <c>!</c> of the DLL form of <c>ASSIGN</c> (line 282).</summary>
        public const string LibraryCall = "!";

        /// <summary>
        /// A program or a response over one of the check-code engine's budgets: a program too long or with too many
        /// tokens to parse, which runs nothing, or more diagnostics than one response lists, the rest summarised. The
        /// engine reports it; the parser does not.
        /// </summary>
        public const string Limit = "LIMIT";
    }

    /// <summary>The event an event-block keyword selects and the keyword that closes the block.</summary>
    private sealed record EventSyntax(FieldEventKind Kind, string Terminator);

    /// <summary>
    /// What an expression's diagnostics name: the enclosing statement's command and keyword token, at which a
    /// malformed expression is reported, and the suffix saying what is skipped.
    /// </summary>
    private readonly record struct ExpressionContext(string Command, Token Keyword, string Suffix);

    /// <summary>
    /// A reported diagnostic with the column of the token it was reported at. <see cref="Diagnostic"/> carries only
    /// the line, and a diagnostic can be found after a later one on the same line, as the missing terminator of a
    /// one-line <c>IF</c> is found after the statements of its branch, so the sinks keep the column to sort by.
    /// </summary>
    private readonly record struct PositionedDiagnostic(Diagnostic Diagnostic, int Column);

    /// <summary>
    /// Unwinds the parse to the enclosing event block's statement loop once nesting exceeds
    /// <see cref="MaxNestingDepth"/>; its <see cref="Commands.Nesting"/> warning is already reported.
    /// </summary>
    private sealed class NestingLimitException : Exception
    {
    }

    /// <summary>
    /// The state of one <see cref="Parse(IReadOnlyList{Token}, string, CancellationToken)"/> call: the token cursor,
    /// the nesting depth and the diagnostic sinks. A new instance per call is what makes the parser thread-safe.
    /// </summary>
    private sealed class ParseRun(IReadOnlyList<Token>? source, string form, CancellationToken cancellationToken)
    {
        // Calls of Advance between two polls of the cancellation token; the first call polls.
        private const int CancellationPollInterval = 1_024;

        private readonly List<FieldBlockNode> fieldBlocks = [];
        private readonly List<PositionedDiagnostic> programDiagnostics = [];

        // The open event block's diagnostics; null outside an event block, so those go to the program list only.
        private List<PositionedDiagnostic>? eventDiagnostics;

        // Where each statement node of the open event block starts. Keyed by reference, because statement records
        // compare by value and two equal statements can stand on one line.
        private readonly Dictionary<StatementNode, Token> statementStarts = new(ReferenceEqualityComparer.Instance);

        private Token[] tokens = [];
        private Token end = new(TokenKind.EndOfInput, string.Empty, 1, 1);
        private int position;
        private int depth;

        // The calls of Advance so far, which pace the polls of the cancellation token.
        private long advances;

        // The lookahead of AlwaysOpensBlock, built by AnalyseClosers for the tokens from closerStart up to the
        // statement-list stop at closerStop: entry i - closerStart is the first END or END-IF (nextCloser), or the
        // first END (nextEndCloser), at or after token i that closes a construct opened before token i; -1 for none.
        private int closerStart = -1;
        private int closerStop = -1;
        private int[] nextCloser = [];
        private int[] nextEndCloser = [];

        // A '/*' without its '*/' (lines 50-51), taken off the token list; null when the program has none.
        private Token? danglingComment;
        private bool danglingCommentReported;

        private Token Current => Peek(0);

        private bool AtEnd => position >= tokens.Length;

        public ProgramNode Execute()
        {
            try
            {
                Buffer();
                DetachDanglingComment();
                ParseProgram();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
            {
                // Last-resort net for a defect in this parser; every input it is designed for is handled above.
                // The blocks completed so far are kept, and the open event block, if any, is abandoned. A cancelled
                // call is no defect: its exception reaches the caller, which no longer wants the result.
                eventDiagnostics = null;
                Warn(Current, Commands.Syntax, "Check code could not be parsed; rest of the program skipped");
            }

            return fieldBlocks.Count == 0 && programDiagnostics.Count == 0
                ? ProgramNode.Empty
                : new ProgramNode(fieldBlocks, DiagnosticsOf(InSourceOrder(programDiagnostics)));
        }

        /// <summary>
        /// Copies the tokens up to the first <see cref="TokenKind.EndOfInput"/>, which becomes <see cref="end"/>;
        /// without one, a synthetic end token takes the last token's position. A token whose text is
        /// <see langword="null"/>, as <c>default(Token)</c> has, is read as empty text, and a line or column below 1,
        /// as <c>default(Token)</c> or a hand-built list can hold, is read as 1, the end token included, so every
        /// diagnostic carries a 1-based line.
        /// </summary>
        private void Buffer()
        {
            if (source is null)
            {
                return;
            }

            int count = source.Count;
            var buffered = new List<Token>(count);
            for (int i = 0; i < count; i++)
            {
                Token token = source[i];
                if (token.Text is null || token.Line < 1 || token.Column < 1)
                {
                    token = token with
                    {
                        Text = token.Text ?? string.Empty,
                        Line = Math.Max(1, token.Line),
                        Column = Math.Max(1, token.Column),
                    };
                }

                if (token.Kind == TokenKind.EndOfInput)
                {
                    end = token;
                    tokens = [.. buffered];
                    return;
                }

                buffered.Add(token);
            }

            tokens = [.. buffered];
            if (tokens.Length > 0)
            {
                Token last = tokens[^1];
                end = new Token(TokenKind.EndOfInput, string.Empty, last.Line, last.Column);
            }
        }

        /// <summary>
        /// Takes off the token list the <see cref="TokenKind.Unknown"/> token the lexer makes of a <c>/*</c> without
        /// its <c>*/</c> (lines 50-51). That comment runs to the end of the text, so its token is always the last, and
        /// no other token starts with <c>/*</c>, because <c>/</c> followed by <c>*</c> always opens a comment. Without
        /// it, the statement before the comment ends at <see cref="end"/> and still parses, and each construct the
        /// hidden text leaves open reaches <see cref="end"/>, where <see cref="ReportDanglingCommentAt"/> reports the
        /// comment instead of a missing terminator.
        /// </summary>
        private void DetachDanglingComment()
        {
            if (tokens.Length > 0
                && tokens[^1].Kind == TokenKind.Unknown
                && tokens[^1].Text.StartsWith("/*", StringComparison.Ordinal))
            {
                danglingComment = tokens[^1];
                tokens = tokens[..^1];
            }
        }

        /// <summary>
        /// Reports the unterminated comment (lines 50-51) at its own line when the parse reaches the end of input it
        /// hid, once for the whole program: the terminators it hid are not reported as missing.
        /// </summary>
        /// <param name="token">The token where a terminator was expected.</param>
        /// <returns>
        /// <see langword="true"/> when <paramref name="token"/> is the end of input and the program ends in an
        /// unterminated comment, whether this call or an earlier one reported it; otherwise <see langword="false"/>.
        /// </returns>
        private bool ReportDanglingCommentAt(Token token)
        {
            if (danglingComment is not { } comment || token.Kind != TokenKind.EndOfInput)
            {
                return false;
            }

            if (!danglingCommentReported)
            {
                danglingCommentReported = true;
                Warn(comment, Commands.Syntax, "Unterminated comment; rest of the program skipped");
            }

            return true;
        }

        private Token Peek(int offset)
        {
            int index = position + offset;
            return index < tokens.Length ? tokens[index] : end;
        }

        /// <summary>
        /// Consumes the current token and returns it; at the end it returns the end token and stays. The first call
        /// and every <see cref="CancellationPollInterval"/>th call after it poll the cancellation token first.
        /// </summary>
        /// <exception cref="OperationCanceledException">The call's cancellation token was cancelled.</exception>
        private Token Advance()
        {
            if (advances++ % CancellationPollInterval == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            Token token = Current;
            if (position < tokens.Length)
            {
                position++;
            }

            return token;
        }

        /// <summary>
        /// Reports a diagnostic at the position of <paramref name="at"/>, the token where the construct starts, to
        /// the program's list and to the open event block's list, if any.
        /// </summary>
        private void Report(DiagnosticSeverity severity, Token at, string command, string message)
        {
            var entry = new PositionedDiagnostic(new Diagnostic(severity, form, at.Line, command, message), at.Column);
            programDiagnostics.Add(entry);
            eventDiagnostics?.Add(entry);
        }

        private void Warn(Token at, string command, string message) =>
            Report(DiagnosticSeverity.Warning, at, command, message);

        // ---------------------------------------------------------------------------------------------------------
        // Blocks (lines 620-640)
        // ---------------------------------------------------------------------------------------------------------

        private void ParseProgram()
        {
            while (!AtEnd)
            {
                Token token = Current;
                if (token.IsKeyword(Commands.Field))
                {
                    ParseFieldBlock();
                }
                else if (IsBlockStart(token))
                {
                    SkipUnsupportedBlock();
                }
                else
                {
                    Warn(token, CommandOrSyntax(token), "Check code outside a check-code block; skipped");
                    Advance();
                    while (!AtEnd && !IsBlockStart(Current))
                    {
                        Advance();
                    }
                }
            }

            // An unterminated comment (lines 50-51) opened outside any event block, at the top level or in a skipped
            // block, has not been reported yet.
            ReportDanglingCommentAt(Current);
        }

        /// <summary>
        /// Skips a <c>View</c>, <c>Form</c>, <c>Record</c>, <c>Page</c>, <c>Sub</c> or <c>DefineVariables</c> block
        /// with one warning. The scan stops before another block keyword, so a missing terminator never swallows
        /// the <c>Field</c> blocks that follow.
        /// </summary>
        private void SkipUnsupportedBlock()
        {
            Token blockToken = Advance();
            string keyword = blockToken.Keyword!;
            string terminator = BlockStarts[keyword];
            Warn(blockToken, keyword, $"Unsupported {keyword} block; block skipped");

            while (!AtEnd)
            {
                Token token = Current;
                if (token.IsKeyword(terminator))
                {
                    Advance();
                    return;
                }

                if (IsBlockStart(token))
                {
                    return;
                }

                Advance();
            }
        }

        /// <summary>
        /// Skips a <c>Before</c>, <c>After</c> or <c>Click</c> section that the <c>Field</c> production does not allow
        /// where it stands (lines 620-627), with one warning at its keyword. Its statements are not parsed, so they
        /// yield no node and no diagnostic of their own.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The scan counts each nested <c>IF</c> against its <c>END-IF</c> or <c>END</c> (lines 434-440), never
        /// below zero, so an event keyword inside one belongs to the skipped section instead of ending it and
        /// becoming executable. <c>ALWAYS</c> is not counted, because after <c>AUTOSEARCH</c> it is a modifier that
        /// no <c>END</c> closes (lines 290-293).
        /// </para>
        /// <para>
        /// Outside a nested <c>IF</c>, the section ends where <see cref="ParseEventBlock"/> would end it: before an
        /// event start, or through another event terminator, which stands for its own. At any depth, its own terminator
        /// ends it and is consumed, while <c>End-Field</c>, a block start or terminator and the end of input end it
        /// and are left to the enclosing field block. An unterminated nested <c>IF</c> therefore swallows neither the
        /// section's own terminator nor anything after the field block.
        /// </para>
        /// </remarks>
        private void SkipMisplacedEventBlock(string message)
        {
            Token eventToken = Advance();
            string keyword = eventToken.Keyword!;
            string terminator = EventStarts[keyword].Terminator;
            Warn(eventToken, keyword, message);

            int ifDepth = 0;
            while (true)
            {
                Token token = Current;
                if (token.IsKeyword(terminator))
                {
                    Advance();
                    return;
                }

                if (token.Kind == TokenKind.EndOfInput || IsBlockStart(token) || IsKeywordIn(token, BlockTerminators))
                {
                    return;
                }

                if (ifDepth == 0 && IsEventStart(token))
                {
                    return;
                }

                if (ifDepth == 0 && IsKeywordIn(token, EventTerminators))
                {
                    Advance();
                    return;
                }

                if (token.IsKeyword(Commands.If))
                {
                    ifDepth++;
                }
                else if (ifDepth > 0 && (token.IsKeyword(Commands.EndIf) || token.IsKeyword(Commands.End)))
                {
                    ifDepth--;
                }

                Advance();
            }
        }

        /// <summary>
        /// The slot of an event block's kind in a <c>Field</c> block, in the order the grammar sets (lines 620-627):
        /// <c>Before</c>, then <c>After</c>, then <c>Click</c>.
        /// </summary>
        private static int EventSlot(FieldEventKind kind) => kind switch
        {
            FieldEventKind.FieldBefore => 0,
            FieldEventKind.FieldAfter => 1,
            FieldEventKind.FieldClick => 2,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "The value is not a field-event kind."),
        };

        private void ParseFieldBlock()
        {
            Token fieldToken = Advance();
            if (!IsFieldBlockName(Current))
            {
                Warn(fieldToken, Commands.Field, "FIELD without a field name; block skipped");
                while (!AtEnd && !IsBlockStart(Current))
                {
                    if (Advance().IsKeyword(Commands.EndField))
                    {
                        return;
                    }
                }

                return;
            }

            Token nameToken = Advance();
            var events = new List<EventBlockNode>();

            // The grammar allows one Before, then one After, then one Click (lines 620-627). A section whose slot is
            // not past the last kept one is a repeat or out of order, and is skipped rather than kept.
            int lastSlot = -1;
            while (true)
            {
                Token token = Current;
                string? keyword = token.Keyword;
                if (keyword is not null && EventStarts.TryGetValue(keyword, out EventSyntax? eventSyntax))
                {
                    int slot = EventSlot(eventSyntax.Kind);
                    if (slot > lastSlot)
                    {
                        events.Add(ParseEventBlock());
                        lastSlot = slot;
                    }
                    else
                    {
                        SkipMisplacedEventBlock(
                            slot == lastSlot
                                ? $"Second {keyword} block in FIELD; block skipped"
                                : $"{keyword} block after a later event block in FIELD; block skipped");
                    }
                }
                else if (keyword == Commands.EndField)
                {
                    Advance();
                    break;
                }
                else if (token.Kind == TokenKind.EndOfInput || IsBlockStart(token))
                {
                    // An END-FIELD hidden in an unterminated comment (lines 50-51) is reported as that comment.
                    if (!ReportDanglingCommentAt(token))
                    {
                        Warn(token, Commands.EndField, "Missing END-FIELD; field block closed");
                    }

                    break;
                }
                else if (keyword is not null && EventTerminators.Contains(keyword))
                {
                    Warn(token, keyword, $"Unexpected {keyword}; skipped");
                    Advance();
                }
                else
                {
                    Warn(token, CommandOrSyntax(token), "Check code outside a Before, After or Click block; skipped");
                    Advance();
                    while (!AtEnd && !IsEventStart(Current) && !Current.IsKeyword(Commands.EndField)
                        && !IsBlockStart(Current))
                    {
                        Advance();
                    }
                }
            }

            fieldBlocks.Add(new FieldBlockNode(nameToken.Text, fieldToken.Line, events));
        }

        private EventBlockNode ParseEventBlock()
        {
            Token eventToken = Advance();
            string eventKeyword = eventToken.Keyword!;
            EventSyntax syntax = EventStarts[eventKeyword];

            var diagnostics = new List<PositionedDiagnostic>();
            eventDiagnostics = diagnostics;
            statementStarts.Clear();
            List<StatementNode> statements;
            try
            {
                statements = ParseEventStatements();

                Token token = Current;
                if (token.IsKeyword(syntax.Terminator))
                {
                    Advance();
                }
                else if (!ReportDanglingCommentAt(token))
                {
                    // Reached when the terminator is missing from the text itself; one hidden in an unterminated
                    // comment (lines 50-51) is reported as that comment by the condition above.
                    Warn(token, syntax.Terminator, $"Missing {syntax.Terminator}; {eventKeyword} block closed");

                    // A different event terminator stands for this one; END-FIELD and block or event starts are
                    // left to the enclosing field block.
                    if (token.Keyword is { } keyword && EventTerminators.Contains(keyword))
                    {
                        Advance();
                    }
                }
            }
            finally
            {
                eventDiagnostics = null;
            }

            PositionedDiagnostic[] sorted = InSourceOrder(diagnostics);
            return new EventBlockNode(syntax.Kind, eventToken.Line, statements, DiagnosticsOf(sorted))
            {
                DiagnosticAnchors = AnchorsOf(statements, sorted),
            };
        }

        /// <summary>
        /// The <see cref="EventBlockNode.DiagnosticAnchors"/> of a block: for each of its diagnostics, in source
        /// order, how many of its statements start before it. Statements are counted in the block's pre-order
        /// (each statement, then its <c>THEN</c> branch, then its <c>ELSE</c> branch), which is their source order;
        /// the statements of a dropped <c>IF</c> are not in the tree, so they are not counted.
        /// </summary>
        private int[] AnchorsOf(IReadOnlyList<StatementNode> statements, PositionedDiagnostic[] sorted)
        {
            var starts = new List<(int Line, int Column)>();
            CollectStatementStarts(statements, starts);

            var anchors = new int[sorted.Length];
            int before = 0;
            for (int i = 0; i < sorted.Length; i++)
            {
                int line = sorted[i].Diagnostic.Line;
                int column = sorted[i].Column;
                while (before < starts.Count
                    && (starts[before].Line < line || (starts[before].Line == line && starts[before].Column < column)))
                {
                    before++;
                }

                anchors[i] = before;
            }

            return anchors;
        }

        /// <summary>Appends the start of each statement of the tree, in pre-order.</summary>
        private void CollectStatementStarts(
            IReadOnlyList<StatementNode> statements,
            List<(int Line, int Column)> starts)
        {
            foreach (StatementNode statement in statements)
            {
                // Every node of the tree came from ParseStatement, which recorded its start token; the node's own
                // line, at column 1, would stand in for one that had none.
                starts.Add(
                    statementStarts.TryGetValue(statement, out Token start)
                        ? (start.Line, start.Column)
                        : (statement.Line, 1));

                if (statement is IfStatementNode ifStatement)
                {
                    CollectStatementStarts(ifStatement.Then, starts);
                    if (ifStatement.Else is { } elseBranch)
                    {
                        CollectStatementStarts(elseBranch, starts);
                    }
                }
            }
        }

        // ---------------------------------------------------------------------------------------------------------
        // Statement lists and recovery
        // ---------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Parses the statements of an event block up to a token that ends it. A stray <c>ELSE</c>,
        /// <c>ELSE-IF</c>, <c>END-IF</c> or <c>END</c> is reported and skipped. Nesting past
        /// <see cref="MaxNestingDepth"/> keeps the statements parsed so far and skips flat to the block's end.
        /// </summary>
        private List<StatementNode> ParseEventStatements()
        {
            var statements = new List<StatementNode>();
            try
            {
                while (!IsStop(Current, insideIf: false))
                {
                    Token token = Current;
                    if (token.Keyword is { } keyword && IfTerminators.Contains(keyword))
                    {
                        Warn(token, keyword, $"Unexpected {keyword}; skipped");
                        Advance();
                        continue;
                    }

                    if (ParseStatement() is { } statement)
                    {
                        statements.Add(statement);
                    }
                }
            }
            catch (NestingLimitException)
            {
                depth = 0;
                while (!IsStop(Current, insideIf: false))
                {
                    Advance();
                }
            }

            return statements;
        }

        /// <summary>Parses an <c>IF</c> branch up to its <c>ELSE</c>, its terminator or the block end.</summary>
        private List<StatementNode> ParseIfBranch()
        {
            var statements = new List<StatementNode>();
            while (!IsStop(Current, insideIf: true))
            {
                if (ParseStatement() is { } statement)
                {
                    statements.Add(statement);
                }
            }

            return statements;
        }

        /// <summary>
        /// Parses one statement, or reports and skips it. It always consumes at least one token, and returns
        /// <see langword="null"/> for a statement that is unsupported or malformed. The start token of each node it
        /// returns, nested ones included, is recorded in <see cref="statementStarts"/>.
        /// </summary>
        private StatementNode? ParseStatement()
        {
            Token start = Current;
            StatementNode? statement = ParseStatementAt(start);
            if (statement is not null)
            {
                statementStarts[statement] = start;
            }

            return statement;
        }

        /// <summary>The body of <see cref="ParseStatement"/>; <paramref name="token"/> is the current token.</summary>
        private StatementNode? ParseStatementAt(Token token)
        {
            string? command = StatementCommand(token);
            switch (command)
            {
                case Commands.If:
                    return ParseIf();
                case Commands.Assign:
                    return ParseAssign();
                case Commands.Enable:
                case Commands.Disable:
                case Commands.Clear:
                    return ParseNameList(command);
                case Commands.Dialog:
                    return ParseDialog();
                case Commands.Geocode:
                    return ParseGeocode();
                case { } unsupported:
                    Warn(token, unsupported, $"Unsupported command {unsupported}; {StatementSkipped}");
                    SkipUnsupportedStatement(unsupported);
                    return null;
            }

            int targetLength = ImplicitAssignStartLength();
            if (targetLength > 0)
            {
                Warn(token, Commands.ImplicitAssign, $"Assignment without ASSIGN is not supported; {StatementSkipped}");
                for (int i = 0; i < targetLength; i++)
                {
                    Advance();
                }

                SkipToBoundary();
                return null;
            }

            // Junk: one warning, then everything up to the next statement or terminator.
            Advance();
            SkipToBoundary();
            Warn(
                token,
                CommandOrSyntax(token),
                IsCommandKeyword(Current) || ImplicitAssignStartLength() > 0
                    ? "Unrecognised check code; skipped to the next statement"
                    : "Unrecognised check code; rest of the block skipped");
            return null;
        }

        /// <summary>
        /// Skips an unsupported statement after its command keyword. <c>ALWAYS … END</c> is skipped whole
        /// (<see cref="SkipAlwaysBlock"/>); a trailing <c>ALWAYS</c> of <c>AUTOSEARCH</c> belongs to it when it is the
        /// modifier rather than a block of its own (<see cref="SkipAutoSearchOperands"/>), and so does the
        /// <c>WAITFOREXIT</c> or <c>NOWAITFOREXIT</c> written directly after <c>EXECUTE</c> (lines 392-400); any other
        /// statement ends at the next statement boundary.
        /// </summary>
        private void SkipUnsupportedStatement(string command)
        {
            Advance();
            if (command == "ALWAYS")
            {
                SkipAlwaysBlock();
            }
            else if (command == "AUTOSEARCH")
            {
                SkipAutoSearchOperands([]);
            }
            else
            {
                // WAITFOREXIT is also a command of its own (line 581), so the skip would otherwise end before it.
                if (command == "EXECUTE" && (Current.IsKeyword("WAITFOREXIT") || Current.IsKeyword("NOWAITFOREXIT")))
                {
                    Advance();
                }

                SkipToBoundary();
            }
        }

        /// <summary>
        /// Skips the body of <c>ALWAYS &lt;statements&gt; END</c> (line 275) through its matching <c>END</c>, matching
        /// each construct nested in it against its own production: an <c>IF</c> closes with <c>END-IF</c> or
        /// <c>END</c> (lines 434-440), a nested <c>ALWAYS</c> only with <c>END</c>, so an <c>END-IF</c> never closes
        /// an <c>ALWAYS</c>, and the trailing <c>ALWAYS</c> of an <c>AUTOSEARCH</c> (lines 290-293) opens a nested
        /// block only when <see cref="AlwaysOpensBlock"/> finds that it is not the modifier. The scan stops before an
        /// event or block boundary, so an unterminated <c>ALWAYS</c> cannot leave its block.
        /// </summary>
        private void SkipAlwaysBlock()
        {
            // The keyword of each construct still open, the innermost on top.
            var open = new Stack<string>();
            open.Push("ALWAYS");
            while (!IsStop(Current, insideIf: false))
            {
                string? keyword = Advance().Keyword;
                if (keyword == "AUTOSEARCH")
                {
                    // An ALWAYS left unconsumed opens a block: the next pass pushes it.
                    SkipAutoSearchOperands(open);
                }
                else if (keyword is Commands.If or "ALWAYS")
                {
                    open.Push(keyword);
                }
                else if (keyword == Commands.End || (keyword == Commands.EndIf && open.Peek() == Commands.If))
                {
                    open.Pop();
                    if (open.Count == 0)
                    {
                        return;
                    }
                }
            }
        }

        /// <summary>
        /// Skips the operands of <c>AUTOSEARCH &lt;names&gt; [DISPLAYLIST &lt;names&gt;] [ALWAYS]</c> (lines 290-293)
        /// after its keyword, and the <c>ALWAYS</c> after them when it is the statement's modifier. The word is also
        /// the command <c>ALWAYS &lt;statements&gt; END</c> (line 275); when it opens such a block
        /// (<see cref="AlwaysOpensBlock"/>), it is left as the current token, a statement of its own at statement
        /// level and a nested block inside an <c>ALWAYS</c> being skipped.
        /// </summary>
        /// <param name="open">
        /// The keywords of the constructs open in the <c>ALWAYS</c> being skipped, innermost first; empty at statement
        /// level.
        /// </param>
        private void SkipAutoSearchOperands(IEnumerable<string> open)
        {
            SkipToBoundary();
            if (Current.IsKeyword("ALWAYS") && !AlwaysOpensBlock(open))
            {
                Advance();
            }
        }

        /// <summary>
        /// Whether the <c>ALWAYS</c> at the cursor, written after the operands of an <c>AUTOSEARCH</c>, opens
        /// <c>ALWAYS &lt;statements&gt; END</c> (line 275) instead of being the modifier of lines 290-293. The grammar
        /// gives the word both roles and line breaks carry no meaning, so the structure that follows decides. The word
        /// opens a block only when its body starts with a statement, since <c>&lt;Statements&gt;</c> is never empty
        /// (line 74), and when, with the block open, the terminators before the statement-list stop
        /// (<see cref="IsStop"/>) close the block, every construct in <paramref name="open"/> and every enclosing
        /// <c>IF</c>, innermost first, with <see cref="SkipAlwaysBlock"/>'s matching: <c>END</c> closes any construct
        /// and <c>END-IF</c> only an <c>IF</c>. Otherwise it is the modifier, so a terminator the program needs for an
        /// outer construct is never taken for a block the program does not close.
        /// </summary>
        /// <param name="open">The constructs open in the <c>ALWAYS</c> being skipped, innermost first.</param>
        /// <remarks>
        /// A later <c>AUTOSEARCH</c>'s <c>ALWAYS</c> counts as its modifier in the lookahead. Deciding it by this rule
        /// instead gives the same answer: a block there only adds a construct to close, so it never lets the
        /// constructs close where the modifier does not, and it is a block only when they all close with it open.
        /// At statement level <see cref="depth"/> counts the enclosing <c>IF</c> statements alone, as parentheses and
        /// calls raise it only inside an expression. The lookahead is built once per statement list
        /// (<see cref="AnalyseClosers"/>), and each decision costs one step per construct it closes.
        /// </remarks>
        private bool AlwaysOpensBlock(IEnumerable<string> open)
        {
            if (IsKeywordIn(Peek(1), IfTerminators))
            {
                return false;
            }

            int from = position + 1;
            if (from < closerStart || from > closerStop)
            {
                AnalyseClosers(from);
            }

            // The block itself closes only with END.
            int closing = NextCloser(from, endOnly: true);
            foreach (string keyword in open)
            {
                if (closing < 0)
                {
                    return false;
                }

                closing = NextCloser(closing + 1, endOnly: keyword != Commands.If);
            }

            for (int i = 0; i < depth && closing >= 0; i++)
            {
                closing = NextCloser(closing + 1, endOnly: false);
            }

            return closing >= 0;
        }

        /// <summary>
        /// The first <c>END</c>, or with <paramref name="endOnly"/> false the first <c>END</c> or <c>END-IF</c>, at or
        /// after token <paramref name="index"/> that closes a construct opened before it, or -1 when the stop comes
        /// first. <paramref name="index"/> lies between <see cref="closerStart"/> and <see cref="closerStop"/>.
        /// </summary>
        private int NextCloser(int index, bool endOnly) =>
            (endOnly ? nextEndCloser : nextCloser)[index - closerStart];

        /// <summary>
        /// Builds the lookahead of <see cref="AlwaysOpensBlock"/> for the tokens from <paramref name="start"/> up to
        /// the statement-list stop. A forward pass pairs each <c>IF</c> and <c>ALWAYS</c> with the terminator that
        /// closes it, matched as <see cref="SkipAlwaysBlock"/> matches them, and skips each <c>AUTOSEARCH</c>'s
        /// operands as <see cref="SkipAutoSearchOperands"/> does, its trailing <c>ALWAYS</c> counted as the modifier.
        /// A backward pass then links each token to the first terminator at or after it outside every construct opened
        /// from it on, jumping over each closed construct; a construct left open hides every terminator after it.
        /// The cursor is restored, so nothing is consumed.
        /// </summary>
        private void AnalyseClosers(int start)
        {
            const int NoConstruct = -2;
            const int Unclosed = -1;

            int stop = start;
            while (stop < tokens.Length && !IsStop(tokens[stop], insideIf: false))
            {
                stop++;
            }

            int length = stop - start;

            // For each token that opens a construct, the index of its terminator or Unclosed; NoConstruct for the rest.
            var closedAt = new int[length];
            Array.Fill(closedAt, NoConstruct);
            var opened = new Stack<int>();
            int resume = position;
            try
            {
                position = start;
                while (position < stop)
                {
                    int index = position;
                    string? keyword = Advance().Keyword;
                    if (keyword == "AUTOSEARCH")
                    {
                        // The operands end at a boundary keyword, so the skip never passes the stop.
                        SkipToBoundary();
                        if (Current.IsKeyword("ALWAYS"))
                        {
                            Advance();
                        }
                    }
                    else if (keyword is Commands.If or "ALWAYS")
                    {
                        closedAt[index - start] = Unclosed;
                        opened.Push(index);
                    }
                    else if (opened.Count > 0
                        && (keyword == Commands.End
                            || (keyword == Commands.EndIf && tokens[opened.Peek()].IsKeyword(Commands.If))))
                    {
                        closedAt[opened.Pop() - start] = index;
                    }
                }
            }
            finally
            {
                position = resume;
            }

            var next = new int[length + 1];
            var nextEnd = new int[length + 1];
            next[length] = -1;
            nextEnd[length] = -1;
            for (int offset = length - 1; offset >= 0; offset--)
            {
                int index = start + offset;
                int terminator = closedAt[offset];
                if (terminator == Unclosed)
                {
                    next[offset] = -1;
                    nextEnd[offset] = -1;
                }
                else if (terminator != NoConstruct)
                {
                    next[offset] = next[terminator + 1 - start];
                    nextEnd[offset] = nextEnd[terminator + 1 - start];
                }
                else if (tokens[index].IsKeyword(Commands.End))
                {
                    next[offset] = index;
                    nextEnd[offset] = index;
                }
                else
                {
                    next[offset] = tokens[index].IsKeyword(Commands.EndIf) ? index : next[offset + 1];
                    nextEnd[offset] = nextEnd[offset + 1];
                }
            }

            closerStart = start;
            closerStop = stop;
            nextCloser = next;
            nextEndCloser = nextEnd;
        }

        /// <summary>
        /// Skips to the next statement boundary as <see cref="AtStatementBoundary"/> defines it: a command, a
        /// terminator, a block boundary or an implicit assignment, which is not consumed.
        /// </summary>
        private void SkipToBoundary()
        {
            while (!AtStatementBoundary())
            {
                Advance();
            }
        }

        /// <summary>
        /// Whether the statement before the cursor ends at the current token, which a statement or a skip then leaves
        /// unconsumed: the end of the input; a command or a keyword that ends a statement list
        /// (<see cref="BoundaryKeywords"/>); or a complete implicit-assignment start (line 285) whose target begins
        /// with an identifier, plain or bracketed, after a token that can end a statement, as <c>B = 1</c> starts
        /// after <c>ENABLE A</c> and after <c>BEEP</c>. Following an operator, punctuation or a keyword that takes an
        /// operand, the same name is that operand instead, as <c>B</c> is in <c>LET B = 1</c> and in
        /// <c>A &lt;&gt; "1" AND B = "2"</c>. A keyword followed by <c>=</c> is never a target here: the grammar
        /// builds the target from the <c>Identifier</c> terminal (lines 173-181), and inside a statement such a
        /// keyword is that statement's option, as <c>VIEWNAME</c> is in
        /// <c>COMMANDLINE PGMNAME = 'C:\a.prj' VIEWNAME = 'C:\b.prj' : "v"</c> (lines 308-326).
        /// </summary>
        private bool AtStatementBoundary()
        {
            Token token = Current;
            return token.Kind == TokenKind.EndOfInput
                || IsKeywordIn(token, BoundaryKeywords)
                || (token.Kind == TokenKind.Identifier
                    && CanEndStatement(position - 1)
                    && ImplicitAssignStartLength() > 0);
        }

        /// <summary>
        /// The number of tokens in the <c>target =</c> start of an implicit assignment at the cursor (line 285), or 0
        /// when the cursor starts none. The target is a <c>Qualified ID</c> (lines 173-181): a name, any number of
        /// <c>.name</c> parts, then at most one grid reference <c>[Number, name | Number | String]</c>. Each name is
        /// one <see cref="IsStrictName"/> accepts, so where a statement starts, a keyword no statement reserves, such
        /// as <c>NUMERIC</c> in <c>NUMERIC = 1</c>, is the target, as it is a name in every other name slot: no
        /// statement is in progress there for it to be an option of. Inside a statement,
        /// <see cref="AtStatementBoundary"/> takes only an identifier as the first name.
        /// </summary>
        private int ImplicitAssignStartLength()
        {
            if (!IsStrictName(Current))
            {
                return 0;
            }

            int offset = 1;
            while (Peek(offset).Kind == TokenKind.Dot && IsStrictName(Peek(offset + 1)))
            {
                offset += 2;
            }

            if (Peek(offset).Kind == TokenKind.LeftBracket
                && Peek(offset + 1).Kind == TokenKind.Number
                && Peek(offset + 2).Kind == TokenKind.Comma
                && (IsStrictName(Peek(offset + 3)) || Peek(offset + 3).Kind is TokenKind.Number or TokenKind.String)
                && Peek(offset + 4).Kind == TokenKind.RightBracket)
            {
                offset += 5;
            }

            return Peek(offset).Kind == TokenKind.Equal ? offset + 1 : 0;
        }

        /// <summary>
        /// Whether the token at <paramref name="index"/> can be the last token of a statement: a name, including a
        /// keyword no statement reserves such as <c>NUMERIC</c>; a literal, character literals included; a
        /// <c>File</c> or <c>Url</c> operand, as in <c>EXECUTE http://x</c> (lines 25-26, 392-400); a built-in value
        /// such as <c>SYSTEMDATE</c> (line 254); a command that is a whole statement on its own, as <c>BEEP</c>,
        /// <c>EXIT</c>, <c>NEWRECORD</c>, <c>QUIT</c> and <c>SAVE-RECORD</c> are (lines 297, 404, 452, 609, 645);
        /// <c>)</c> or <c>]</c>; or a <c>*</c> written directly after its command, as in <c>UNHIDE *</c> (lines
        /// 418-428, 468, 476). Operators, other punctuation, the commands that take an operand (<c>LET</c>,
        /// <c>GOTO</c> and the like) and the keywords that take an operand (<c>AND</c>, <c>THEN</c>, <c>EXCEPT</c>
        /// and the like) cannot; nor can a missing token.
        /// </summary>
        private bool CanEndStatement(int index)
        {
            if (index < 0 || index >= tokens.Length)
            {
                return false;
            }

            Token token = tokens[index];
            return token.Kind switch
            {
                TokenKind.Identifier or TokenKind.String or TokenKind.Number or TokenKind.Date or TokenKind.Time
                    or TokenKind.Character or TokenKind.File or TokenKind.Url
                    or TokenKind.Plus or TokenKind.Minus or TokenKind.Missing
                    or TokenKind.RightParen or TokenKind.RightBracket => true,
                TokenKind.Keyword => !Reserved.Contains(token.Keyword!)
                    || ZeroArgFunctions.Contains(token.Keyword!)
                    || ZeroOperandCommands.Contains(token.Keyword!),
                TokenKind.Asterisk => index > 0 && IsCommandKeyword(tokens[index - 1]),
                _ => false,
            };
        }

        /// <summary>Raises the nesting depth for one more <c>IF</c>, parenthesis or function call.</summary>
        /// <exception cref="NestingLimitException">The depth exceeds <see cref="MaxNestingDepth"/>.</exception>
        private void EnterNesting(Token at)
        {
            depth++;
            if (depth > MaxNestingDepth)
            {
                Warn(at, Commands.Nesting, "Check code nested too deeply; rest of the block skipped");
                throw new NestingLimitException();
            }
        }

        // ---------------------------------------------------------------------------------------------------------
        // Statements (lines 281-444, 636)
        // ---------------------------------------------------------------------------------------------------------

        /// <summary>
        /// <c>IF &lt;condition&gt; THEN &lt;statements&gt; [ELSE &lt;statements&gt;] END-IF | END</c> (lines
        /// 434-440). An unsupported condition, a missing <c>THEN</c>, an <c>ELSE-IF</c>, a second <c>ELSE</c> or a
        /// missing terminator drops the whole statement, so neither branch runs; the branches are still parsed, so
        /// nested statements are matched with their own terminators and their diagnostics stay reported.
        /// </summary>
        private IfStatementNode? ParseIf()
        {
            Token ifToken = Advance();
            EnterNesting(ifToken);
            try
            {
                var context = new ExpressionContext(Commands.If, ifToken, IfSkipped);
                ExpressionNode? condition = ParseExpression(context);
                bool poisoned = condition is null;
                if (condition is not null && !Current.IsKeyword(Commands.Then))
                {
                    Warn(ifToken, Commands.If, $"Malformed IF condition or missing THEN; {IfSkipped}");
                    poisoned = true;
                }

                SkipToThen();
                List<StatementNode> thenBranch = ParseIfBranch();
                List<StatementNode>? elseBranch = null;

                while (true)
                {
                    Token token = Current;
                    string? keyword = token.Keyword;
                    if (keyword is Commands.EndIf or Commands.End)
                    {
                        Advance();
                        break;
                    }

                    if (keyword == Commands.ElseIf)
                    {
                        Warn(token, Commands.ElseIf, $"ELSE-IF is not supported; {IfSkipped}");
                        poisoned = true;
                        Advance();
                        SkipToThen();
                        ParseIfBranch();
                    }
                    else if (keyword == Commands.Else)
                    {
                        if (elseBranch is not null)
                        {
                            Warn(token, Commands.Else, $"Second ELSE in IF; {IfSkipped}");
                            poisoned = true;
                        }

                        Advance();
                        List<StatementNode> branch = ParseIfBranch();
                        elseBranch ??= branch;
                    }
                    else
                    {
                        // An END-IF or END hidden in an unterminated comment (lines 50-51) is reported as that
                        // comment; the IF never runs either way, because its terminator is not in the program.
                        if (!ReportDanglingCommentAt(token))
                        {
                            Warn(ifToken, Commands.If, $"IF without END-IF or END; {IfSkipped}");
                        }

                        poisoned = true;
                        break;
                    }
                }

                return poisoned ? null : new IfStatementNode(condition!, thenBranch, elseBranch, ifToken.Line);
            }
            finally
            {
                depth--;
            }
        }

        /// <summary>
        /// Consumes tokens through the next <c>THEN</c>; without one, stops before the next statement boundary
        /// (<see cref="AtStatementBoundary"/>), so the statements that follow still parse.
        /// </summary>
        private void SkipToThen()
        {
            while (!AtStatementBoundary())
            {
                if (Advance().IsKeyword(Commands.Then))
                {
                    return;
                }
            }
        }

        /// <summary>
        /// <c>ASSIGN &lt;field&gt; = &lt;expression&gt;</c> (line 281). The statement must end where its expression
        /// does (<see cref="AtStatementBoundary"/>): anything left before the boundary, as the <c>C</c> of
        /// <c>ASSIGN A = B C</c>, drops the whole statement with one warning, so a valid prefix of a longer,
        /// malformed assignment never runs.
        /// </summary>
        private AssignStatementNode? ParseAssign()
        {
            Token assignToken = Advance();

            // A reserved word is never the target, so a bare ASSIGN leaves the next statement's keyword unconsumed.
            if (!IsStrictName(Current))
            {
                return MalformedAssign(assignToken);
            }

            Token target = Advance();
            if (ReportQualifiedReference(target, StatementSkipped))
            {
                SkipToBoundary();
                return null;
            }

            if (Current.Kind != TokenKind.Equal)
            {
                return MalformedAssign(assignToken);
            }

            Advance();
            var context = new ExpressionContext(Commands.Assign, assignToken, StatementSkipped);
            ExpressionNode? value = ParseExpression(context);
            if (value is null)
            {
                SkipToBoundary();
                return null;
            }

            // A following implicit assignment is a boundary, so ASSIGN A = B C = 1 keeps A = B and C = 1 is next.
            if (!AtStatementBoundary())
            {
                return MalformedAssign(assignToken);
            }

            return new AssignStatementNode(target.Text, value, assignToken.Line);
        }

        private AssignStatementNode? MalformedAssign(Token assignToken)
        {
            Warn(assignToken, Commands.Assign, $"Malformed ASSIGN statement; {StatementSkipped}");
            SkipToBoundary();
            return null;
        }

        /// <summary>
        /// <c>ENABLE</c>, <c>DISABLE</c> or <c>CLEAR</c> followed by one or more space-separated names (lines 168-171,
        /// 301, 423-426); the grammar has no commas between them.
        /// </summary>
        private StatementNode? ParseNameList(string command)
        {
            Token commandToken = Advance();
            if (Current.Kind == TokenKind.Asterisk)
            {
                if (command == Commands.Clear)
                {
                    Warn(commandToken, command, $"Malformed {command} field list; {StatementSkipped}");
                }
                else
                {
                    string except = command == Commands.Enable ? Commands.EnableExcept : Commands.DisableExcept;
                    Warn(commandToken, except, $"{except} is not supported; {StatementSkipped}");
                }

                SkipToBoundary();
                return null;
            }

            // A name that starts an implicit assignment begins the next statement, so the list ends before it.
            var names = new List<string>();
            while (IsStrictName(Current) && !AtStatementBoundary())
            {
                Token name = Advance();
                if (ReportQualifiedReference(name, StatementSkipped))
                {
                    SkipToBoundary();
                    return null;
                }

                names.Add(name.Text);
            }

            if (names.Count == 0 || !AtStatementBoundary())
            {
                Warn(commandToken, command, $"Malformed {command} field list; {StatementSkipped}");
                SkipToBoundary();
                return null;
            }

            return command switch
            {
                Commands.Enable => new EnableStatementNode(names, commandToken.Line),
                Commands.Disable => new DisableStatementNode(names, commandToken.Line),
                _ => new ClearStatementNode(names, commandToken.Line),
            };
        }

        /// <summary>
        /// <c>DIALOG "message" [TITLETEXT = "title"]</c> (lines 364, 383-385). Each text keeps its trailing
        /// whitespace and loses its leading whitespace, as the legacy prompt and title extraction did.
        /// </summary>
        private DialogStatementNode? ParseDialog()
        {
            Token dialogToken = Advance();
            if (Current.Kind != TokenKind.String)
            {
                return MalformedDialog(dialogToken);
            }

            string message = Advance().Text.TrimStart();
            string? title = null;
            if (Current.IsKeyword(Commands.TitleText))
            {
                Advance();
                if (Current.Kind != TokenKind.Equal || Peek(1).Kind != TokenKind.String)
                {
                    return MalformedDialog(dialogToken);
                }

                Advance();
                title = Advance().Text.TrimStart();
                if (!AtStatementBoundary())
                {
                    return MalformedDialog(dialogToken);
                }
            }
            else if (!AtStatementBoundary())
            {
                // The forms that read a value into a variable (lines 365-381).
                Warn(dialogToken, Commands.Dialog, $"Unsupported DIALOG form; {StatementSkipped}");
                SkipToBoundary();
                return null;
            }

            // Legacy showed an empty title without TITLETEXT; null lets the client show its default caption.
            return new DialogStatementNode(message, title, dialogToken.Line);
        }

        private DialogStatementNode? MalformedDialog(Token dialogToken)
        {
            Warn(dialogToken, Commands.Dialog, $"Malformed DIALOG statement; {StatementSkipped}");
            SkipToBoundary();
            return null;
        }

        /// <summary>
        /// <c>GEOCODE address, latitude, longitude</c> (line 636). The node is kept so the evaluator can skip it at
        /// its line, and its one <see cref="DiagnosticSeverity.Info"/> is reported here and nowhere else.
        /// </summary>
        private GeocodeStatementNode? ParseGeocode()
        {
            Token geocodeToken = Advance();
            var arguments = new List<string>(3);
            for (int i = 0; i < 3; i++)
            {
                if (i > 0)
                {
                    if (Current.Kind != TokenKind.Comma)
                    {
                        return MalformedGeocode(geocodeToken);
                    }

                    Advance();
                }

                // A missing operand leaves a statement, event or block keyword where it stands, unconsumed.
                if (!IsStrictName(Current))
                {
                    return MalformedGeocode(geocodeToken);
                }

                arguments.Add(Advance().Text);
            }

            if (!AtStatementBoundary())
            {
                return MalformedGeocode(geocodeToken);
            }

            Report(
                DiagnosticSeverity.Info,
                geocodeToken,
                Commands.Geocode,
                $"GEOCODE is not executed; {StatementSkipped}");
            return new GeocodeStatementNode(arguments, geocodeToken.Line);
        }

        private GeocodeStatementNode? MalformedGeocode(Token geocodeToken)
        {
            Warn(geocodeToken, Commands.Geocode, $"Malformed GEOCODE statement; {StatementSkipped}");
            SkipToBoundary();
            return null;
        }

        /// <summary>
        /// Reports a qualified name (<c>a.b</c>) or a grid reference (<c>a[1, 2]</c>) when one follows
        /// <paramref name="name"/>, which is already consumed.
        /// </summary>
        /// <returns>Whether one was reported; the cursor is then on the <c>.</c> or <c>[</c>.</returns>
        private bool ReportQualifiedReference(Token name, string suffix)
        {
            switch (Current.Kind)
            {
                case TokenKind.Dot:
                    Warn(name, Commands.QualifiedName, $"Qualified field names are not supported; {suffix}");
                    return true;
                case TokenKind.LeftBracket:
                    Warn(name, Commands.GridField, $"Grid field references are not supported; {suffix}");
                    return true;
                default:
                    return false;
            }
        }

        // ---------------------------------------------------------------------------------------------------------
        // Expressions (lines 486-529, subset only)
        // ---------------------------------------------------------------------------------------------------------

        /// <summary>
        /// <c>Expression ::= NotExp {AND NotExp}</c>, folded to the right as the grammar's right recursion nests it:
        /// <c>a AND b AND c</c> is <c>And(a, And(b, c))</c>. The chain is collected in a loop, so its length costs
        /// no stack.
        /// </summary>
        /// <returns>
        /// The expression, or <see langword="null"/> once one diagnostic is reported, with the cursor on the
        /// offending token.
        /// </returns>
        private ExpressionNode? ParseExpression(ExpressionContext context)
        {
            var operands = new List<ExpressionNode>();
            var operatorLines = new List<int>();
            while (true)
            {
                ExpressionNode? operand = ParseNot(context);
                if (operand is null)
                {
                    return null;
                }

                operands.Add(operand);
                if (!Current.IsKeyword(Commands.And))
                {
                    break;
                }

                operatorLines.Add(Advance().Line);
            }

            ExpressionNode result = operands[^1];
            for (int i = operands.Count - 2; i >= 0; i--)
            {
                result = new AndNode(operands[i], result, operatorLines[i]);
            }

            return result;
        }

        /// <summary><c>NotExp ::= [NOT] CompareExp</c>: <c>NOT</c> binds one comparison (line 493).</summary>
        private ExpressionNode? ParseNot(ExpressionContext context)
        {
            if (!Current.IsKeyword(Commands.Not))
            {
                return ParseCompare(context);
            }

            Token notToken = Advance();
            ExpressionNode? operand = ParseCompare(context);
            return operand is null ? null : new NotNode(operand, notToken.Line);
        }

        /// <summary>
        /// <c>CompareExp ::= Value {= Value}</c>, folded to the right: <c>a = b = c</c> is <c>Eq(a, Eq(b, c))</c>.
        /// Every value is followed by the check for an unsupported operator, which covers each place one can stand.
        /// </summary>
        private ExpressionNode? ParseCompare(ExpressionContext context)
        {
            var operands = new List<ExpressionNode>();
            var operatorLines = new List<int>();
            while (true)
            {
                ExpressionNode? operand = ParseValue(context);
                if (operand is null)
                {
                    return null;
                }

                if (UnsupportedOperator(Current) is { } spelling)
                {
                    Warn(Current, spelling, $"Unsupported operator {spelling}; {context.Suffix}");
                    return null;
                }

                operands.Add(operand);
                if (Current.Kind != TokenKind.Equal)
                {
                    break;
                }

                operatorLines.Add(Advance().Line);
            }

            ExpressionNode result = operands[^1];
            for (int i = operands.Count - 2; i >= 0; i--)
            {
                result = new EqualsNode(operands[i], result, operatorLines[i]);
            }

            return result;
        }

        /// <summary>
        /// <c>Value ::= name | String | Number | (.) | (-) | '(' Expression ')' | function '(' [arguments] ')'</c>.
        /// </summary>
        private ExpressionNode? ParseValue(ExpressionContext context)
        {
            Token token = Current;
            switch (token.Kind)
            {
                case TokenKind.String:
                    Advance();
                    return LiteralNode.FromString(token.Text, token.Line);
                case TokenKind.Number:
                    // An overflowing literal such as 1E999 parses to an infinity, which no literal node can hold.
                    if (double.TryParse(token.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
                        && double.IsFinite(number))
                    {
                        Advance();
                        return LiteralNode.FromNumber(token.Text, number, token.Line);
                    }

                    return UnsupportedLiteral(token, Commands.NumberLiteral, context);
                case TokenKind.Missing:
                    Advance();
                    return LiteralNode.Missing(token.Line);
                case TokenKind.Minus:
                    Advance();
                    return LiteralNode.Unchecked(token.Line);
                case TokenKind.Plus:
                    return UnsupportedLiteral(token, Commands.CheckedLiteral, context);
                case TokenKind.Date:
                    return UnsupportedLiteral(token, Commands.DateLiteral, context);
                case TokenKind.Time:
                    return UnsupportedLiteral(token, Commands.TimeLiteral, context);
                case TokenKind.Character:
                    return UnsupportedLiteral(token, Commands.CharLiteral, context);
                case TokenKind.LeftParen:
                    return ParseParenthesised(context);
                case TokenKind.PlusSign:
                case TokenKind.MinusSign:
                    // Unary sign (lines 521-522).
                    string sign = UnsupportedOperator(token)!;
                    Warn(token, sign, $"Unsupported operator {sign}; {context.Suffix}");
                    return null;
            }

            // Only the keyword is the built-in (line 254); a bracketed [SYSTEMDATE] is a field name (line 30).
            if (IsKeywordIn(token, ZeroArgFunctions))
            {
                string name = token.Keyword!;
                Warn(token, name, $"{UnsupportedFunctionText}{name}; {context.Suffix}");
                return null;
            }

            if (IsStrictName(token))
            {
                if (Peek(1).Kind == TokenKind.LeftParen)
                {
                    return ParseFunctionCall(context);
                }

                Advance();
                if (ReportQualifiedReference(token, context.Suffix))
                {
                    return null;
                }

                return new IdentifierNode(token.Text, token.Line);
            }

            return MalformedExpression(context);
        }

        private ExpressionNode? UnsupportedLiteral(Token token, string literal, ExpressionContext context)
        {
            Warn(token, literal, $"Unsupported literal {literal}; {context.Suffix}");
            return null;
        }

        private ExpressionNode? MalformedExpression(ExpressionContext context)
        {
            Warn(context.Keyword, context.Command, $"Malformed expression in {context.Command}; {context.Suffix}");
            return null;
        }

        /// <summary>
        /// <c>'(' Expression ')'</c>, unwrapped to the inner node because the tree has no grouping node. A comma
        /// inside makes it the unsupported expression list of line 528.
        /// </summary>
        private ExpressionNode? ParseParenthesised(ExpressionContext context)
        {
            Token open = Current;
            EnterNesting(open);
            try
            {
                Advance();
                ExpressionNode? inner = ParseExpression(context);
                if (inner is null)
                {
                    return null;
                }

                if (Current.Kind == TokenKind.Comma)
                {
                    Warn(
                        open,
                        Commands.ExpressionList,
                        $"Parenthesised expression lists are not supported; {context.Suffix}");
                    return null;
                }

                if (Current.Kind != TokenKind.RightParen)
                {
                    return MalformedExpression(context);
                }

                Advance();
                return inner;
            }
            finally
            {
                depth--;
            }
        }

        /// <summary>
        /// <c>name '(' [Expression {, Expression}] ')'</c> (line 259) for a supported function; any other name is
        /// reported with the cursor still on it. The argument count is the function's own check, at run time.
        /// </summary>
        private FunctionCallNode? ParseFunctionCall(ExpressionContext context)
        {
            Token nameToken = Current;
            string name = nameToken.Text.ToUpperInvariant();
            if (!FunctionNames.IsSupported(name))
            {
                Warn(nameToken, name, $"{UnsupportedFunctionText}{name}; {context.Suffix}");
                return null;
            }

            EnterNesting(nameToken);
            try
            {
                Advance();
                Advance();
                var arguments = new List<ExpressionNode>();
                if (Current.Kind != TokenKind.RightParen)
                {
                    while (true)
                    {
                        ExpressionNode? argument = ParseExpression(context);
                        if (argument is null)
                        {
                            return null;
                        }

                        arguments.Add(argument);
                        if (Current.Kind != TokenKind.Comma)
                        {
                            break;
                        }

                        Advance();
                    }
                }

                if (Current.Kind != TokenKind.RightParen)
                {
                    Warn(nameToken, name, $"Malformed {name} call; {context.Suffix}");
                    return null;
                }

                Advance();
                return new FunctionCallNode(name, arguments, nameToken.Line);
            }
            finally
            {
                depth--;
            }
        }
    }

    // -------------------------------------------------------------------------------------------------------------
    // Token classification
    // -------------------------------------------------------------------------------------------------------------

    private static bool IsKeywordIn(Token token, FrozenSet<string> keywords) =>
        token.Kind == TokenKind.Keyword && keywords.Contains(token.Keyword!);

    /// <summary>
    /// Whether a statement list ends at <paramref name="token"/>: the end, an event or block terminator, an event
    /// or block start, and inside an <c>IF</c> also <c>ELSE</c>, <c>ELSE-IF</c>, <c>END-IF</c> and <c>END</c>.
    /// </summary>
    private static bool IsStop(Token token, bool insideIf) =>
        token.Kind == TokenKind.EndOfInput || IsKeywordIn(token, insideIf ? IfStopKeywords : EventStopKeywords);

    private static bool IsCommandKeyword(Token token) => IsKeywordIn(token, CommandKeywords);

    private static bool IsBlockStart(Token token) => token.Keyword is { } keyword && BlockStarts.ContainsKey(keyword);

    private static bool IsEventStart(Token token) => token.Keyword is { } keyword && EventStarts.ContainsKey(keyword);

    /// <summary>
    /// A name in an <c>ASSIGN</c> target, a <c>GEOCODE</c> argument, an identifier list or an expression: an
    /// identifier, or a keyword that is none of the parser's reserved words, so a field named like a statement
    /// keyword needs brackets, as in the grammar.
    /// </summary>
    private static bool IsStrictName(Token token) =>
        token.CanBeName && (token.Kind == TokenKind.Identifier || !Reserved.Contains(token.Keyword!));

    /// <summary>
    /// The name after <c>FIELD</c>: any name, except a block or event keyword. Those cannot name a field without
    /// brackets in a program legacy Epi Info accepted, and reading one as the name would merge a nameless block
    /// with the block or event that follows it.
    /// </summary>
    private static bool IsFieldBlockName(Token token) =>
        token.CanBeName && !IsBlockStart(token) && !IsEventStart(token);

    /// <summary>
    /// The command a statement starting with <paramref name="token"/> runs, or <see langword="null"/>. Only a
    /// <see cref="TokenKind.Keyword"/> starts a command: an identifier spelled like one, such as the bracketed
    /// <c>[ASSIGN]</c>, is a name (line 30), so it starts no statement.
    /// </summary>
    private static string? StatementCommand(Token token) =>
        IsKeywordIn(token, CommandKeywords) ? token.Keyword : null;

    /// <summary>
    /// The token's keyword when it belongs to the check-code vocabulary, else <see cref="Commands.Syntax"/>, so a
    /// diagnostic never repeats arbitrary token text.
    /// </summary>
    private static string CommandOrSyntax(Token token)
    {
        if (token.Kind != TokenKind.Keyword)
        {
            return Commands.Syntax;
        }

        string keyword = token.Keyword!;
        return Reserved.Contains(keyword)
            || Lexer.Keywords.Contains(keyword)
            || Lexer.HyphenatedKeywords.Contains(keyword)
            ? keyword
            : Commands.Syntax;
    }

    /// <summary>
    /// The spelling of the unsupported operator <paramref name="token"/> is, or <see langword="null"/> when it is
    /// none (lines 282, 486-518).
    /// </summary>
    private static string? UnsupportedOperator(Token token) => token.Kind switch
    {
        TokenKind.NotEqual => Commands.NotEqual,
        TokenKind.Less => Commands.Less,
        TokenKind.Greater => Commands.Greater,
        TokenKind.LessOrEqual => Commands.LessOrEqual,
        TokenKind.GreaterOrEqual => Commands.GreaterOrEqual,
        TokenKind.Ampersand => Commands.Concatenate,
        TokenKind.PlusSign => Commands.Plus,
        TokenKind.MinusSign => Commands.Minus,
        TokenKind.Asterisk => Commands.Multiply,
        TokenKind.Slash => Commands.Divide,
        TokenKind.Caret => Commands.Power,
        TokenKind.Percent => Commands.Percent,
        TokenKind.Bang => Commands.LibraryCall,
        TokenKind.Keyword => token.Keyword is Commands.Or or Commands.Xor or Commands.Mod or Commands.Like
            ? token.Keyword
            : null,
        _ => null,
    };

    // Source order is line, then column. OrderBy and ThenBy are stable, so diagnostics reported at one position keep
    // the order they were reported in.
    private static PositionedDiagnostic[] InSourceOrder(List<PositionedDiagnostic> entries) =>
        [.. entries.OrderBy(entry => entry.Diagnostic.Line).ThenBy(entry => entry.Column)];

    private static Diagnostic[] DiagnosticsOf(PositionedDiagnostic[] entries) =>
        [.. entries.Select(entry => entry.Diagnostic)];
}
