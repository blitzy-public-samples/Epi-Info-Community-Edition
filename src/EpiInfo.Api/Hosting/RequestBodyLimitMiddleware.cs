using Microsoft.AspNetCore.Http.Features;

namespace EpiInfo.Api.Hosting;

/// <summary>
/// The request body guard: a request whose body exceeds <see cref="ApiOptions.MaxRequestBodySize"/> fails with a
/// <see cref="BadHttpRequestException"/> whose status is 413, whether the size is declared by <c>Content-Length</c>
/// or only found while the body is streamed. A body of exactly the limit passes.
/// </summary>
/// <remarks>
/// <para>
/// Real Kestrel enforces the same limit itself (<c>Kestrel:Limits:MaxRequestBodySize</c>), but TestServer applies no
/// Kestrel limits, so this guard is what makes in-process hosts reject the same bodies. On Kestrel whichever check
/// fires first yields the same 413, and the server's own <c>IHttpMaxRequestBodySizeFeature</c> is left untouched.
/// Kestrel's count of a chunked body includes the chunk framing (observed on 10.0.15), so there a chunked body of
/// exactly the limit already gets Kestrel's 413; this guard counts body bytes only.
/// </para>
/// <para>
/// The body is never read, peeked or buffered here: an over-long declared length fails before any read, and any
/// other request that can have a body gets its <see cref="HttpRequest.Body"/> replaced by a counting stream. The
/// framework's <see cref="HttpRequest.BodyReader"/> adapts to the replaced stream, so binding through either sees
/// every byte counted.
/// </para>
/// <para>
/// No response is written and nothing is logged here. A throw before the endpoint reaches the API exception handler,
/// which keeps the 413 and writes the fixed ProblemDetails; a throw the framework catches during JSON binding leaves
/// an empty 413 that status-code pages turn into the same ProblemDetails. The exception message is fixed and never
/// states a size.
/// </para>
/// </remarks>
public sealed class RequestBodyLimitMiddleware
{
    private const string TooLargeMessage = "Request body too large.";

    private readonly RequestDelegate _next;

    private readonly long _limit;

    /// <summary>
    /// Creates the guard in front of <paramref name="next"/> with the limit of <paramref name="options"/>.
    /// </summary>
    /// <param name="next">The rest of the pipeline.</param>
    /// <param name="options">The API settings; <see cref="ApiOptions.MaxRequestBodySize"/> is read once, here.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The configured limit is not a positive number of bytes.
    /// </exception>
    public RequestBodyLimitMiddleware(RequestDelegate next, ApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxRequestBodySize, nameof(options));

        _next = next;
        _limit = options.MaxRequestBodySize;
    }

    /// <summary>
    /// Rejects a declared length over the limit, otherwise wraps a request body that may exist in the counting
    /// stream, which it also sets as the request's <see cref="IRequestBodyReadFeature"/>, and calls the rest of the
    /// pipeline.
    /// </summary>
    /// <param name="context">The current request.</param>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="BadHttpRequestException">
    /// With status 413, when <c>Content-Length</c> exceeds the limit; the counting stream throws the same exception
    /// from a read that takes the body past the limit.
    /// </exception>
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var request = context.Request;
        if (request.ContentLength is long length && length > _limit)
        {
            throw TooLarge();
        }

        if (CanHaveBody(context))
        {
            // Wrapped even under a declared length within the limit: in-process hosts do not hold a body to its
            // Content-Length, and the counter costs one addition per read.
            var stream = new LimitedReadStream(request.Body, _limit);
            request.Body = stream;

            // The reads binding makes leave their state here, so the exception handler can tell a body that ended
            // before its first byte from content that is not JSON without reading the body again.
            context.Features.Set<IRequestBodyReadFeature>(stream);
        }

        return _next(context);
    }

    // The 413 every path of this guard raises: the HTTP abstractions' type, which the exception handler and the
    // framework's body binding both recognise by status, never Kestrel's obsolete one.
    private static BadHttpRequestException TooLarge() =>
        new(TooLargeMessage, StatusCodes.Status413PayloadTooLarge);

    // Decided from headers and server features only, like the media-type guard. A host that does not expose the
    // body-detection feature gets the counting stream, which is harmless on an empty body.
    private static bool CanHaveBody(HttpContext context) =>
        context.Request.ContentLength != 0
        && context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody != false;

    /// <summary>
    /// A read-only, forward-only view of the request body that counts the bytes read and fails every read once the
    /// count exceeds the limit, and the request's <see cref="IRequestBodyReadFeature"/>. It is used by one request at
    /// a time, as the body stream it wraps is.
    /// </summary>
    private sealed class LimitedReadStream : Stream, IRequestBodyReadFeature
    {
        private readonly Stream _inner;

        private readonly long _limit;

        private long _total;

        public LimitedReadStream(Stream inner, long limit)
        {
            _inner = inner;
            _limit = limit;
        }

        public long BytesRead => _total;

        public bool EndOfBody { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        // Synchronous reads forward as they are, so the server's AllowSynchronousIO policy still applies to the inner
        // stream.
        public override int Read(byte[] buffer, int offset, int count)
        {
            ThrowIfOverLimit();
            return Count(_inner.Read(buffer, offset, count), count);
        }

        public override int Read(Span<byte> buffer)
        {
            ThrowIfOverLimit();
            return Count(_inner.Read(buffer), buffer.Length);
        }

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            ThrowIfOverLimit();
            return Count(
                await _inner.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false),
                count);
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ThrowIfOverLimit();
            return Count(await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false), buffer.Length);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        // The inner stream belongs to the server, which ends it with the request; the pipe reader the framework builds
        // over this wrapper disposes the wrapper only, so it must not pass the disposal on. Stream.DisposeAsync
        // routes here as well.
        protected override void Dispose(bool disposing) => base.Dispose(disposing);

        // A read that asked for at least one byte and got none found the end of the body. A zero-byte read, which a
        // pipe reader issues only to wait for data, proves nothing.
        private int Count(int read, int requested)
        {
            _total += read;
            if (read == 0 && requested > 0)
            {
                EndOfBody = true;
            }

            ThrowIfOverLimit();
            return read;
        }

        // Also checked before each read, so a caller that ignores the first failure never reads past it.
        private void ThrowIfOverLimit()
        {
            if (_total > _limit)
            {
                throw TooLarge();
            }
        }
    }
}

/// <summary>
/// What the request's own consumers have read of its body so far, as <see cref="RequestBodyLimitMiddleware"/>'s
/// counting stream saw it. The exception handler reads it to tell a body that ended before its first byte, such as an
/// empty chunked body, from content that is not JSON.
/// </summary>
/// <remarks>
/// The feature is set only on a request whose body the guard wraps, and it is never a reason to read: its values come
/// from reads binding already made, so the body is never read, peeked or buffered twice. Both values only grow, and
/// they describe the decoded body bytes, never the transfer framing.
/// </remarks>
public interface IRequestBodyReadFeature
{
    /// <summary>The number of body bytes read so far.</summary>
    long BytesRead { get; }

    /// <summary>
    /// <see langword="true"/> once a read that asked for at least one byte returned none, so the body is known to
    /// have ended. A zero-byte read never sets it.
    /// </summary>
    bool EndOfBody { get; }
}

/// <summary>Pipeline registration for <see cref="RequestBodyLimitMiddleware"/>.</summary>
public static class RequestBodyLimitMiddlewareExtensions
{
    /// <summary>
    /// Adds <see cref="RequestBodyLimitMiddleware"/>, which takes its limit from the registered
    /// <see cref="ApiOptions"/>. Call it after <c>UseRouting</c> and before the JSON media-type guard, so the counting
    /// stream is in place before any body is bound.
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> is <see langword="null"/>.</exception>
    public static IApplicationBuilder UseRequestBodyLimit(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<RequestBodyLimitMiddleware>();
    }
}
