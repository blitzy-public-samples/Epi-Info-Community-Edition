using EpiInfo.CheckCode;
using EpiInfo.Domain.Diagnostics;
using EpiInfo.Domain.Events;
using EpiInfo.Domain.Metadata;
using EpiInfo.Domain.Values;

namespace EpiInfo.Api.Tests.Infrastructure;

/// <summary>
/// An <see cref="ICheckCodeEngine"/> that counts the calls the API makes and forwards each one, unchanged, to the
/// engine it wraps. It lets a test prove that loading records runs no check code: after a read-only load
/// <see cref="RunCalls"/> must still be 0, while <see cref="AnalyzeCalls"/> may grow, because the form definition only
/// parses the program and evaluates nothing.
/// </summary>
/// <remarks>
/// <para>
/// <c>ApiFactory</c> registers one instance, wrapping the real <see cref="CheckCodeEngine"/>, as the
/// <see cref="ICheckCodeEngine"/> singleton. That one instance serves every concurrent request of the host, so the
/// counters change only through <see cref="Interlocked"/> and are read through <see cref="Volatile"/>.
/// </para>
/// <para>
/// The counters are the only state. The wrapped engine is stateless, and this class caches, alters and reorders
/// nothing: every argument, the cancellation token included, reaches the inner engine as given and its result, or its
/// exception, reaches the caller as the inner engine produced it. Responses through the API are therefore the real
/// engine's.
/// </para>
/// <para>
/// A call is counted before it is forwarded, so a call that throws is still counted.
/// </para>
/// </remarks>
/// <param name="inner">The engine every call is forwarded to.</param>
/// <exception cref="ArgumentNullException"><paramref name="inner"/> is <see langword="null"/>.</exception>
public sealed class CountingCheckCodeEngine(ICheckCodeEngine inner) : ICheckCodeEngine
{
    private readonly ICheckCodeEngine _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    private int _analyzeCalls;
    private int _runCalls;

    /// <summary>The number of <see cref="Analyze"/> calls since construction or the last <see cref="Reset"/>.</summary>
    public int AnalyzeCalls => Volatile.Read(ref _analyzeCalls);

    /// <summary>The number of <see cref="Run"/> calls since construction or the last <see cref="Reset"/>.</summary>
    public int RunCalls => Volatile.Read(ref _runCalls);

    /// <summary>The wrapped engine, exposed for diagnostics only; calls made on it directly are not counted.</summary>
    public ICheckCodeEngine Inner => _inner;

    /// <inheritdoc />
    public IReadOnlyList<Diagnostic> Analyze(
        string? program,
        FormMetadata form,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _analyzeCalls);
        return _inner.Analyze(program, form, cancellationToken);
    }

    /// <inheritdoc />
    public FieldEventResponse Run(
        string? program,
        FormMetadata form,
        FieldEventKind fieldEvent,
        string fieldName,
        IReadOnlyDictionary<string, FieldValue> values,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _runCalls);
        return _inner.Run(program, form, fieldEvent, fieldName, values, cancellationToken);
    }

    /// <summary>
    /// Sets both counters to 0, so a test can discard warm-up calls before the calls it measures. Each counter is
    /// reset atomically; a call that runs concurrently with the reset may be counted on either side of it.
    /// </summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _analyzeCalls, 0);
        Interlocked.Exchange(ref _runCalls, 0);
    }
}
