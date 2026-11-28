using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using EpiInfo.Data.Sql;

namespace EpiInfo.Tests.Shared;

/// <summary>
/// One SQL command as <see cref="SqlCommandRecorder"/> saw it, with its outcome once it completed.
/// </summary>
/// <remarks>
/// <see cref="ToString"/> lists parameter names and types but never their values, so a command can appear in
/// test output without leaking field values. <see cref="Describe"/> adds the values for an assertion message
/// that needs them.
/// </remarks>
/// <param name="Sequence">Execution order across the recorder, assigned when the command started.</param>
/// <param name="Operation">The fixed operation name <c>EpiInfo.Data</c> gave the command.</param>
/// <param name="CommandText">The exact SQL text sent.</param>
/// <param name="Parameters">The parameters bound to the command, as captured when it started.</param>
/// <param name="ClientConnectionId">The connection the command ran on.</param>
/// <param name="InTransaction">Whether the command ran inside a transaction.</param>
/// <param name="Completed">Whether the command reported completion or failure.</param>
/// <param name="Succeeded">Whether it completed with neither an exception nor a SQL error number.</param>
/// <param name="SqlErrorNumber">The SQL Server error number of a failure, or <see langword="null"/>.</param>
/// <param name="ExceptionType">The full type name of the exception that ended it, or <see langword="null"/>.</param>
public sealed record RecordedCommand(
    long Sequence,
    string Operation,
    string CommandText,
    IReadOnlyList<SqlCommandParameterInfo> Parameters,
    Guid ClientConnectionId,
    bool InTransaction,
    bool Completed,
    bool Succeeded,
    int? SqlErrorNumber,
    string? ExceptionType)
{
    /// <summary>
    /// Whether the text holds an <c>INSERT</c>, <c>UPDATE</c>, <c>DELETE</c> or <c>MERGE</c> statement anywhere
    /// in the batch. Keywords inside quoted identifiers, string literals and comments do not count, so lock
    /// hints, <c>OUTPUT INSERTED.*</c> and a column named <c>[Deleted]</c> are not writes.
    /// </summary>
    public bool IsWrite => SqlCommandRecorder.ContainsWriteStatement(CommandText);

    /// <summary>
    /// Describes the command without parameter values.
    /// </summary>
    public override string ToString() => Describe(includeParameterValues: false);

    /// <summary>
    /// Describes the command, adding parameter values only when <paramref name="includeParameterValues"/> is
    /// <see langword="true"/>. Use the values only in an assertion message that needs them.
    /// </summary>
    public string Describe(bool includeParameterValues)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"#{Sequence} {Operation} on connection {ClientConnectionId:D}");
        text.Append(InTransaction ? " in transaction" : " outside a transaction");
        text.Append(", ").Append(DescribeOutcome());
        text.Append(", SQL error ")
            .Append(SqlErrorNumber is int number ? number.ToString(CultureInfo.InvariantCulture) : "none");
        text.Append(": ").Append(CommandText);
        text.Append(" | parameters: ");

        if (Parameters.Count == 0)
        {
            text.Append("none");
            return text.ToString();
        }

        for (var index = 0; index < Parameters.Count; index++)
        {
            var parameter = Parameters[index];
            if (index > 0)
            {
                text.Append(", ");
            }

            text.Append(parameter.Name).Append(' ').Append(parameter.DbType.ToString());
            if (includeParameterValues)
            {
                text.Append(" = ").Append(FormatValue(parameter.Value));
            }
        }

        return text.ToString();
    }

    private string DescribeOutcome()
    {
        if (!Completed)
        {
            return "pending";
        }

        if (Succeeded)
        {
            return "succeeded";
        }

        return ExceptionType is null ? "failed" : $"failed ({ExceptionType})";
    }

    private static string FormatValue(object? value) => value switch
    {
        null => "NULL",
        string text => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'",
        bool flag => flag ? "true" : "false",
        DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
        byte[] bytes => "0x" + Convert.ToHexString(bytes),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };
}

/// <summary>
/// Test implementation of <see cref="ISqlCommandObserver"/> that records every command <c>EpiInfo.Data</c> sends,
/// with its parameters, connection, transaction flag and outcome.
/// </summary>
/// <remarks>
/// One instance can serve every request of a test host, so the callbacks are safe to call concurrently. They
/// never throw: a failure to record is kept and raised by the next query instead, so the code under test runs
/// unchanged and a test never asserts on an incomplete record. The recorder writes nothing to any log or console.
/// </remarks>
public sealed partial class SqlCommandRecorder : ISqlCommandObserver
{
    // SQL Server ends a "--" comment at a carriage return or a line feed, and at no other character.
    private static readonly char[] LineCommentTerminators = ['\r', '\n'];

    private readonly Lock _gate = new();
    private readonly List<Entry> _entries = [];

    // SqlCommandInfo compares by value and two identical commands can be in flight at once, so each
    // OnExecuting call is paired with its OnExecuted call by instance.
    private readonly Dictionary<SqlCommandInfo, Entry> _pending = new(ReferenceEqualityComparer.Instance);
    private readonly List<Exception> _faults = [];
    private long _lastSequence;

    /// <summary>
    /// Every recorded command, ordered by <see cref="RecordedCommand.Sequence"/>. Each read is a new, immutable
    /// snapshot.
    /// </summary>
    /// <exception cref="InvalidOperationException">A callback failed to record a command.</exception>
    public IReadOnlyList<RecordedCommand> Commands => Snapshot(static _ => true);

    /// <inheritdoc />
    public void OnExecuting(SqlCommandInfo command)
    {
        var sequence = Interlocked.Increment(ref _lastSequence);
        try
        {
            var entry = new Entry(sequence, command);
            lock (_gate)
            {
                _entries.Add(entry);
                _pending[command] = entry;
            }
        }
        catch (Exception fault)
        {
            RecordFault(fault);
        }
    }

    /// <inheritdoc />
    public void OnExecuted(SqlCommandInfo command, int? sqlErrorNumber, Exception? error)
    {
        try
        {
            lock (_gate)
            {
                if (_pending.Remove(command, out var entry))
                {
                    entry.Complete(sqlErrorNumber, error);
                    return;
                }
            }

            // No OnExecuting was seen for this instance, for example because Clear ran while the command was
            // in flight: record the completed command rather than lose it.
            var unpaired = new Entry(Interlocked.Increment(ref _lastSequence), command);
            unpaired.Complete(sqlErrorNumber, error);
            lock (_gate)
            {
                _entries.Add(unpaired);
            }
        }
        catch (Exception fault)
        {
            RecordFault(fault);
        }
    }

    /// <summary>
    /// Drops every recorded command, every pending pair and every recording failure. Call it after setup so a
    /// test asserts only on the operation under test.
    /// </summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _pending.Clear();
            _faults.Clear();
        }
    }

    /// <summary>
    /// The commands that ran on the connection with <paramref name="clientConnectionId"/>, in execution order.
    /// </summary>
    public IReadOnlyList<RecordedCommand> ForConnection(Guid clientConnectionId) =>
        Snapshot(command => command.ClientConnectionId == clientConnectionId);

    /// <summary>
    /// The commands whose <see cref="RecordedCommand.Operation"/> equals <paramref name="operation"/> (ordinal).
    /// </summary>
    public IReadOnlyList<RecordedCommand> ForOperation(string operation) =>
        Snapshot(command => string.Equals(command.Operation, operation, StringComparison.Ordinal));

    /// <summary>
    /// The commands whose text holds a write statement (<see cref="RecordedCommand.IsWrite"/>).
    /// </summary>
    public IReadOnlyList<RecordedCommand> Writes() => Snapshot(static command => command.IsWrite);

    /// <summary>
    /// The completed commands that did not succeed.
    /// </summary>
    public IReadOnlyList<RecordedCommand> Failed() =>
        Snapshot(static command => command.Completed && !command.Succeeded);

    /// <summary>
    /// Whether any recorded command text contains <paramref name="text"/> (ordinal).
    /// </summary>
    public bool AnyCommandTextContains(string text) =>
        Commands.Any(command => command.CommandText.Contains(text, StringComparison.Ordinal));

    /// <summary>
    /// Whether any recorded command bound a string parameter equal to <paramref name="value"/> (ordinal).
    /// </summary>
    public bool AnyParameterValueEquals(string value) =>
        Commands.Any(command => command.Parameters.Any(
            parameter => parameter.Value is string bound && string.Equals(bound, value, StringComparison.Ordinal)));

    /// <summary>
    /// Every <c>[bracketed]</c> identifier in <paramref name="commandText"/>, in order, with <c>]]</c> unescaped
    /// to <c>]</c>. Brackets inside string literals, double-quoted identifiers and comments are not identifiers
    /// and are skipped.
    /// </summary>
    public static IReadOnlyList<string> BracketedIdentifiers(string commandText)
    {
        var identifiers = new List<string>();
        Scan(commandText, code: null, identifiers);
        return identifiers.AsReadOnly();
    }

    internal static bool ContainsWriteStatement(string commandText)
    {
        var code = new StringBuilder(commandText.Length);
        Scan(commandText, code, identifiers: null);
        return WriteKeyword().IsMatch(code.ToString());
    }

    // A keyword preceded by '@', '#', '$' or '.' is a variable, temporary table or qualified name, not a statement.
    [GeneratedRegex(@"(?<![@#$.])\b(?:INSERT|UPDATE|DELETE|MERGE)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WriteKeyword();

    /// <summary>
    /// Splits T-SQL text into code and the spans that cannot hold a statement keyword: bracketed and
    /// double-quoted identifiers, string literals, <c>--</c> comments and nested <c>/* */</c> comments. A
    /// <c>--</c> comment ends at the first carriage return or line feed, which stays in the code. Each span
    /// becomes one space in <paramref name="code"/>; bracketed names are added, unescaped, to
    /// <paramref name="identifiers"/>. An unterminated span runs to the end of the text, as SQL Server reads it.
    /// </summary>
    /// <remarks>
    /// String literals and double-quoted identifiers are stripped as well as brackets and comments, because a
    /// fixed literal such as <c>HAS_PERMS_BY_NAME(..., 'OBJECT', 'INSERT')</c> is a read, not a write.
    /// </remarks>
    private static void Scan(string text, StringBuilder? code, List<string>? identifiers)
    {
        var index = 0;
        while (index < text.Length)
        {
            var current = text[index];
            var next = index + 1 < text.Length ? text[index + 1] : '\0';

            if (current == '[')
            {
                var name = identifiers is null ? null : new StringBuilder();
                index = ReadDelimited(text, index, ']', name);
                if (name is not null)
                {
                    identifiers?.Add(name.ToString());
                }

                code?.Append(' ');
            }
            else if (current is '\'' or '"')
            {
                index = ReadDelimited(text, index, current, content: null);
                code?.Append(' ');
            }
            else if (current == '-' && next == '-')
            {
                var lineEnd = text.IndexOfAny(LineCommentTerminators, index + 2);
                index = lineEnd < 0 ? text.Length : lineEnd;
                code?.Append(' ');
            }
            else if (current == '/' && next == '*')
            {
                index = SkipBlockComment(text, index);
                code?.Append(' ');
            }
            else
            {
                code?.Append(current);
                index++;
            }
        }
    }

    // Reads a span opened at text[start] and closed by 'close', where a doubled 'close' is an escaped character.
    // Returns the index after the span, appending its unescaped content to 'content' when one is supplied.
    private static int ReadDelimited(string text, int start, char close, StringBuilder? content)
    {
        var index = start + 1;
        while (index < text.Length)
        {
            if (text[index] == close)
            {
                if (index + 1 < text.Length && text[index + 1] == close)
                {
                    content?.Append(close);
                    index += 2;
                    continue;
                }

                return index + 1;
            }

            content?.Append(text[index]);
            index++;
        }

        return index;
    }

    // T-SQL block comments nest, so the comment ends only when every opened "/*" is closed.
    private static int SkipBlockComment(string text, int start)
    {
        var depth = 0;
        var index = start;
        while (index < text.Length)
        {
            if (text[index] == '/' && index + 1 < text.Length && text[index + 1] == '*')
            {
                depth++;
                index += 2;
            }
            else if (text[index] == '*' && index + 1 < text.Length && text[index + 1] == '/')
            {
                depth--;
                index += 2;
                if (depth == 0)
                {
                    return index;
                }
            }
            else
            {
                index++;
            }
        }

        return index;
    }

    private ReadOnlyCollection<RecordedCommand> Snapshot(Func<RecordedCommand, bool> predicate)
    {
        RecordedCommand[] all;
        lock (_gate)
        {
            if (_faults.Count > 0)
            {
                throw new InvalidOperationException(
                    $"{nameof(SqlCommandRecorder)} failed to record {_faults.Count} observer call(s); its record is incomplete.",
                    new AggregateException(_faults));
            }

            all = new RecordedCommand[_entries.Count];
            for (var index = 0; index < _entries.Count; index++)
            {
                all[index] = _entries[index].ToRecordedCommand();
            }
        }

        // Sequence numbers are taken before the lock, so list order can differ from execution order.
        Array.Sort(all, static (left, right) => left.Sequence.CompareTo(right.Sequence));
        return Array.AsReadOnly(Array.FindAll(all, command => predicate(command)));
    }

    private void RecordFault(Exception fault)
    {
        lock (_gate)
        {
            _faults.Add(fault);
        }
    }

    private sealed class Entry(long sequence, SqlCommandInfo command)
    {
        // Copied when the command starts, so a caller that reuses its parameter list cannot alter the record.
        private readonly IReadOnlyList<SqlCommandParameterInfo> _parameters = Array.AsReadOnly(command.Parameters.ToArray());
        private bool _completed;
        private int? _sqlErrorNumber;
        private string? _exceptionType;
        private bool _succeeded;

        public long Sequence { get; } = sequence;

        public void Complete(int? sqlErrorNumber, Exception? error)
        {
            _completed = true;
            _sqlErrorNumber = sqlErrorNumber;
            _exceptionType = error?.GetType().FullName;
            _succeeded = error is null && sqlErrorNumber is null;
        }

        public RecordedCommand ToRecordedCommand() => new(
            Sequence,
            command.Operation,
            command.CommandText,
            _parameters,
            command.ClientConnectionId,
            command.InTransaction,
            _completed,
            _succeeded,
            _sqlErrorNumber,
            _exceptionType);
    }
}
