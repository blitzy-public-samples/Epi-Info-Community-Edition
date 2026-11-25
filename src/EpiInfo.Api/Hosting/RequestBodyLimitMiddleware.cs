using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;

namespace EpiInfo.Api.Hosting;

/// <summary>
/// The request body guard of the four JSON body endpoints. On an endpoint carrying <see cref="JsonBodyMetadata"/>
/// whose <c>Content-Type</c> the JSON media-type guard accepts, a body whose payload exceeds
/// <see cref="ApiOptions.MaxRequestBodySize"/> fails with a <see cref="BadHttpRequestException"/> whose status is 413,
/// whether the size is declared by <c>Content-Length</c> or only found while the body is streamed. A body of exactly
/// the limit passes on TestServer, and with <c>Content-Length</c> on every host; on Kestrel a chunked body of exactly
/// the limit passes when its framing fits the budget stated in the remarks. Every other request passes through
/// unchanged.
/// </summary>
/// <remarks>
/// <para>
/// The guard reads the matched endpoint, so it runs after routing. A request that matches no route, the 405 endpoint
/// of a method mismatch and routing's own 415 endpoint carry no marker and keep their 404, 405 or 415. A media type
/// that <see cref="JsonContentTypeMiddleware.IsAllowedJsonContentType(string?)"/> rejects is left to that guard's 415
/// whatever its declared length, so the two guards give the same statuses in either order.
/// </para>
/// <para>
/// Real Kestrel enforces its own limit (<c>Kestrel:Limits:MaxRequestBodySize</c>) on every request, but TestServer
/// applies no Kestrel limits, so this guard is what makes in-process hosts reject the same bodies. On Kestrel
/// whichever check fires first yields the same 413. A body of exactly the limit passes on TestServer, framed either
/// way, and with <c>Content-Length</c> on Kestrel as well, where a chunked body is held as the next paragraph states.
/// </para>
/// <para>
/// Kestrel counts an HTTP/1.1 chunked body together with its framing: the chunk-size lines, any chunk extensions, the
/// line break after each chunk's data and the line ending the trailer. For a chunked body without
/// <c>Content-Length</c> on a request this guard accepts, the server limit of that one request
/// (<see cref="IHttpMaxRequestBodySizeFeature"/>) is raised before its first read to 6 × the limit + 5 bytes, a
/// framing budget of 5 × the limit + 5 bytes on top of the decoded limit: the framing of one-byte chunks, the most an
/// extension-free, canonically sized chunking of a body of the limit carries, plus the last chunk. A chunked body of
/// exactly the limit therefore passes whenever its framing fits that budget, which every extension-free, canonically
/// sized chunking does at any chunk size, as does any chunking whose extensions and zero-padded sizes stay within it;
/// within the budget the decoded bytes this guard counts decide the 413. Framing beyond the budget earns Kestrel's own
/// 413, which bounds the bytes the server reads for one request. That bound must be finite because chunk extensions
/// never reach the application and Kestrel holds them to this limit alone, so a server limit without one would let a
/// client stream extension bytes without end. The server limit is never lowered or removed, a host without a writable
/// feature is left as it is, and a body with <c>Content-Length</c>, an HTTP/2 or HTTP/3 body and every other request
/// keep the configured limit.
/// </para>
/// <para>
/// The body is never read, peeked or buffered here: an over-long declared length fails before any read, and any
/// other accepted request that can have a body gets its <see cref="HttpRequest.Body"/> replaced by a counting
/// stream. The framework's <see cref="HttpRequest.BodyReader"/> adapts to the replaced stream, so binding through
/// either sees every byte counted.
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

    // A one-byte chunk costs five framing bytes: its "1\r\n" size line and the "\r\n" after its data.
    private const long OneByteChunkFramingBytes = 5;

    // The "0\r\n" last chunk and the "\r\n" ending an empty trailer.
    private const long LastChunkFramingBytes = 5;

    private readonly RequestDelegate _next;

    private readonly long _limit;

    private readonly long _chunkedServerLimit;

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
        _chunkedServerLimit = ChunkedServerLimit(_limit);
    }

    /// <summary>
    /// On an endpoint marked with <see cref="JsonBodyMetadata"/> whose media type the JSON media-type guard accepts,
    /// rejects a declared length over the limit, otherwise wraps a request body that may exist in the counting
    /// stream, which it also sets as the request's <see cref="IRequestBodyReadFeature"/>, first raising the server
    /// limit of a chunked body by its framing allowance; then calls the rest of the pipeline. Every other request
    /// passes through unchanged.
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

        // A route miss, the 405 endpoint, routing's own 415 endpoint and every endpoint that takes no JSON body keep
        // their status; a media type the JSON guard rejects is left to its 415, whichever guard runs first.
        if (context.GetEndpoint()?.Metadata.GetMetadata<JsonBodyMetadata>() is null
            || !JsonContentTypeMiddleware.IsAllowedJsonContentType(request.ContentType))
        {
            return _next(context);
        }

        if (request.ContentLength is long length && length > _limit)
        {
            throw TooLarge();
        }

        if (CanHaveBody(context))
        {
            AllowChunkFraming(context);

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

    // The most a chunked body of exactly the limit makes Kestrel count when it carries no chunk extensions and no
    // zero-padded sizes: one-byte chunks, the most framing per payload byte, then the last chunk. A limit set in code
    // beyond the configuration cap saturates instead of overflowing.
    private static long ChunkedServerLimit(long limit)
    {
        const long bytesPerPayloadByte = 1 + OneByteChunkFramingBytes;
        return limit > (long.MaxValue - LastChunkFramingBytes) / bytesPerPayloadByte
            ? long.MaxValue
            : (limit * bytesPerPayloadByte) + LastChunkFramingBytes;
    }

    // Kestrel counts the framing of an HTTP/1.1 chunked body against its own limit, so this request's server limit is
    // raised to the framing allowance while the counting stream holds the decoded bytes to the limit. Only a body with
    // a Transfer-Encoding and no Content-Length is chunked; HTTP/2 and HTTP/3 forbid Transfer-Encoding and frame their
    // bodies themselves. The server limit is raised before the first read, while the feature is writable, and never
    // lowered or removed.
    private void AllowChunkFraming(HttpContext context)
    {
        var request = context.Request;
        if (request.ContentLength is not null || !request.Headers.ContainsKey(HeaderNames.TransferEncoding))
        {
            return;
        }

        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature
            && feature.MaxRequestBodySize is long serverLimit
            && serverLimit < _chunkedServerLimit)
        {
            feature.MaxRequestBodySize = _chunkedServerLimit;
        }
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
    /// count exceeds the limit, never taking more than one byte past the limit from the inner stream, and the
    /// request's <see cref="IRequestBodyReadFeature"/>. A body of exactly the limit ends with an ordinary zero-byte
    /// read. It is used by one request at a time, as the body stream it wraps is.
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

        // Synchronous reads forward to the inner stream's synchronous read, so the server's AllowSynchronousIO policy
        // still applies to it.
        public override int Read(byte[] buffer, int offset, int count)
        {
            ValidateBufferArguments(buffer, offset, count);
            ThrowIfOverLimit();
            int requested = Bound(count);
            return Count(_inner.Read(buffer, offset, requested), requested);
        }

        public override int Read(Span<byte> buffer)
        {
            ThrowIfOverLimit();
            int requested = Bound(buffer.Length);
            return Count(_inner.Read(buffer[..requested]), requested);
        }

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            ValidateBufferArguments(buffer, offset, count);
            ThrowIfOverLimit();
            int requested = Bound(count);
            return Count(
                await _inner.ReadAsync(buffer, offset, requested, cancellationToken).ConfigureAwait(false),
                requested);
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ThrowIfOverLimit();
            int requested = Bound(buffer.Length);
            return Count(
                await _inner.ReadAsync(buffer[..requested], cancellationToken).ConfigureAwait(false),
                requested);
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

        // A non-empty read asks the inner stream for no more than the bytes the limit still allows plus the one that
        // proves the body too large; a zero-length read is forwarded as it is. ThrowIfOverLimit runs first, so the
        // total is within the limit here and the allowance is never negative.
        private int Bound(int count)
        {
            long allowed = _limit - _total;
            return allowed < count ? (int)allowed + 1 : count;
        }

        // A read that asked the inner stream for at least one byte and got none found the end of the body. A zero-byte
        // read, which a pipe reader issues only to wait for data, proves nothing.
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
    /// <see cref="ApiOptions"/>. Call it after <c>UseRouting</c>, so the endpoint is matched before the guard reads its
    /// metadata, and before the endpoints, so the counting stream is in place before any body is bound. Either side of
    /// the JSON media-type guard gives the same statuses.
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
