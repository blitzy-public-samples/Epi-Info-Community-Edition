using Microsoft.Extensions.Logging;

namespace EpiInfo.Api.Tests.Infrastructure;

/// <summary>
/// One log call as <see cref="CapturingLoggerProvider"/> received it, after the logger factory's filters.
/// </summary>
/// <param name="Category">The logger category, for example <c>EpiInfo.Api.Errors.ApiExceptionHandler</c>.</param>
/// <param name="Level">The level the call was made at.</param>
/// <param name="EventId">The event id the call carried.</param>
/// <param name="Message">
/// The formatter's output; empty when it returned nothing, and <c>&lt;formatter failed&gt;</c> when it threw.
/// </param>
/// <param name="State">
/// The structured key/value pairs of the state, <c>{OriginalFormat}</c> included, copied at capture time because
/// state objects can be pooled. A state that is not a key/value sequence becomes the single pair
/// <c>("State", state.ToString())</c>.
/// </param>
/// <param name="Scopes">
/// The text of each scope active at the call, outermost first; a scope that is a key/value sequence is followed by
/// one <c>key=value</c> item per pair.
/// </param>
/// <param name="ExceptionType">The full type name of the logged exception, or <see langword="null"/>.</param>
/// <param name="ExceptionMessage">
/// The logged exception's full <see cref="Exception.ToString"/> text, so its message, every inner exception and the
/// stack trace are searchable; <see langword="null"/> without an exception.
/// </param>
public sealed record CapturedLogEntry(
    string Category,
    LogLevel Level,
    EventId EventId,
    string Message,
    IReadOnlyList<KeyValuePair<string, object?>> State,
    IReadOnlyList<string> Scopes,
    string? ExceptionType,
    string? ExceptionMessage);

/// <summary>
/// An in-memory logger provider that records everything the API's logging pipeline lets through, so tests can
/// assert on exactly what production emits: the redaction checks search every captured line for sentinels, and the
/// check-code tests look for the entry a skipped or unsupported statement leaves.
/// </summary>
/// <remarks>
/// <para>
/// Registration: inside <c>ConfigureTestServices</c>, add the instance with
/// <c>services.AddSingleton&lt;ILoggerProvider&gt;(provider)</c>, or <c>TryAddEnumerable</c> with the same
/// instance. That runs after <c>Program.cs</c> has called <c>ClearProviders()</c> and <c>AddJsonConsole()</c>, so the
/// JSON console stays registered and this provider is one more sink under the same filters. Never call
/// <c>RemoveAll&lt;ILoggerProvider&gt;()</c>: the test would then no longer observe the production configuration.
/// </para>
/// <para>
/// The provider applies no filtering of its own: its loggers are enabled for every level except
/// <see cref="LogLevel.None"/>. It deliberately has no <c>ProviderAlias</c>, so no provider-specific configuration
/// section can single it out, and the generic <c>Logging:LogLevel</c> rules of the committed
/// <c>appsettings*.json</c> govern it exactly as they govern the console. Raising or lowering a level here would make
/// the captured lines differ from what production writes.
/// </para>
/// <para>
/// The provider is thread-safe. <see cref="Dispose"/> only marks it disposed: entries stay readable, and loggers
/// created earlier keep capturing, so a test can assert after the host has shut down and one instance can outlive the
/// host it was registered in.
/// </para>
/// </remarks>
public sealed class CapturingLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private const string FormatterFailed = "<formatter failed>";
    private const string StateCaptureFailed = "<state capture failed>";
    private const string ScopeCaptureFailed = "<scope capture failed>";
    private const string ExceptionTextFailed = "<exception text unavailable>";

    private readonly object _gate = new();
    private readonly List<CapturedLogEntry> _entries = [];
    private volatile IExternalScopeProvider _scopeProvider = new LoggerExternalScopeProvider();
    private volatile bool _disposed;

    /// <summary>A snapshot copy of the captured entries, in arrival order.</summary>
    public IReadOnlyList<CapturedLogEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToArray();
            }
        }
    }

    /// <summary>Whether <see cref="Dispose"/> has been called. Entries remain readable either way.</summary>
    public bool IsDisposed => _disposed;

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName)
    {
        ArgumentNullException.ThrowIfNull(categoryName);
        return new CapturingLogger(categoryName, this);
    }

    /// <summary>
    /// Uses the logger factory's scope provider, so scopes the application begins appear in <see cref="Entries"/>.
    /// Until the factory calls this, a private <see cref="LoggerExternalScopeProvider"/> holds the scopes.
    /// </summary>
    public void SetScopeProvider(IExternalScopeProvider scopeProvider)
    {
        ArgumentNullException.ThrowIfNull(scopeProvider);
        _scopeProvider = scopeProvider;
    }

    /// <summary>Discards every captured entry.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }

    /// <summary>
    /// Whether any captured entry holds <paramref name="text"/>, compared ordinally, in its message, the text of any
    /// state value, any scope item, its exception type or its full exception text.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="text"/> is null or empty.</exception>
    public bool ContainsText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            throw new ArgumentException("The text to search for must be a non-empty string.", nameof(text));
        }

        foreach (CapturedLogEntry entry in Entries)
        {
            if (EntryContains(entry, text))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The captured entries that satisfy <paramref name="predicate"/>, in arrival order.</summary>
    public IReadOnlyList<CapturedLogEntry> Find(Func<CapturedLogEntry, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return Entries.Where(predicate).ToArray();
    }

    /// <summary>Marks the provider disposed; captured entries are kept.</summary>
    public void Dispose() => _disposed = true;

    private void Append(CapturedLogEntry entry)
    {
        lock (_gate)
        {
            _entries.Add(entry);
        }
    }

    private static bool EntryContains(CapturedLogEntry entry, string text)
    {
        if (entry.Message.Contains(text, StringComparison.Ordinal))
        {
            return true;
        }

        foreach (KeyValuePair<string, object?> pair in entry.State)
        {
            if (pair.Value?.ToString()?.Contains(text, StringComparison.Ordinal) == true)
            {
                return true;
            }
        }

        foreach (string scope in entry.Scopes)
        {
            if (scope.Contains(text, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return entry.ExceptionType?.Contains(text, StringComparison.Ordinal) == true
            || entry.ExceptionMessage?.Contains(text, StringComparison.Ordinal) == true;
    }

    private sealed class CapturingLogger(string category, CapturingLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => provider._scopeProvider.Push(state);

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        // Every capture step falls back to a marker instead of throwing: a logger that throws would change the
        // behaviour of the code under test.
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            provider.Append(new CapturedLogEntry(
                category,
                logLevel,
                eventId,
                FormatMessage(state, exception, formatter),
                CaptureState(state),
                CaptureScopes(provider._scopeProvider),
                exception?.GetType().FullName,
                exception is null ? null : DescribeException(exception)));
        }

        private static string FormatMessage<TState>(
            TState state,
            Exception? exception,
            Func<TState, Exception?, string>? formatter)
        {
            try
            {
                return (formatter is null ? state?.ToString() : formatter(state, exception)) ?? string.Empty;
            }
            catch (Exception)
            {
                return FormatterFailed;
            }
        }

        private static KeyValuePair<string, object?>[] CaptureState<TState>(TState state)
        {
            try
            {
                return state is IEnumerable<KeyValuePair<string, object?>> pairs
                    ? pairs.ToArray()
                    : [new KeyValuePair<string, object?>("State", state?.ToString())];
            }
            catch (Exception)
            {
                return [new KeyValuePair<string, object?>("State", StateCaptureFailed)];
            }
        }

        private static string[] CaptureScopes(IExternalScopeProvider scopeProvider)
        {
            var scopes = new List<string>();
            try
            {
                scopeProvider.ForEachScope(
                    static (scope, items) =>
                    {
                        if (scope is null)
                        {
                            return;
                        }

                        items.Add(scope.ToString() ?? string.Empty);
                        if (scope is IEnumerable<KeyValuePair<string, object?>> pairs)
                        {
                            foreach (KeyValuePair<string, object?> pair in pairs)
                            {
                                items.Add($"{pair.Key}={pair.Value}");
                            }
                        }
                    },
                    scopes);
            }
            catch (Exception)
            {
                scopes.Add(ScopeCaptureFailed);
            }

            return scopes.ToArray();
        }

        private static string DescribeException(Exception exception)
        {
            try
            {
                return exception.ToString();
            }
            catch (Exception)
            {
                return ExceptionTextFailed;
            }
        }
    }
}
