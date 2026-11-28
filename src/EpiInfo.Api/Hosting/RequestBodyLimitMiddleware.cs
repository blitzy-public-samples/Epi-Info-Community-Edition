using System.Runtime.ExceptionServices;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;

namespace EpiInfo.Api.Hosting;

/// <summary>
/// The request body guard of the four JSON body endpoints. On an endpoint carrying <see cref="JsonBodyMetadata"/>
/// whose <c>Content-Type</c> the JSON media-type guard accepts, a body whose payload exceeds
/// <see cref="ApiOptions.MaxRequestBodySize"/> fails with a <see cref="BadHttpRequestException"/> whose status is 413,
/// whether the size is declared by <c>Content-Length</c> or only found while the body is streamed. A body of exactly
/// the limit passes on TestServer, and with <c>Content-Length</c> on every host; on Kestrel a chunked body of exactly
/// the limit passes when its framing fits the budget stated in the remarks. When the server's body stream fails a read
/// of a chunked body with an <see cref="IOException"/> that is not a <see cref="BadHttpRequestException"/> and whose
/// inner exception is an <see cref="OverflowException"/>, as Kestrel does for a chunk-size line whose value overflows,
/// the read fails with a <see cref="BadHttpRequestException"/> whose status is 400 instead, unless the client has
/// aborted the request; every other failure of that stream leaves the read unchanged. Every other request passes
/// through unchanged.
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
/// No response is written and nothing is logged here: each failure reaches the API's exception boundary and handler,
/// which keep its status, write the fixed ProblemDetails and log the one failure line, so a 413 is answered and
/// correlated the same way however the body is framed. A declared length over the limit throws before the endpoint
/// runs. A read of the counting stream past the limit fails with the guard's own 413, and a
/// <see cref="BadHttpRequestException"/> the server's body stream raises, such as Kestrel's own 413 or its 400 for a
/// chunk size that is not hexadecimal, leaves the read as it is. Kestrel raises one client framing error of a chunked
/// body as a plain <see cref="IOException"/> instead: a chunk-size line whose value overflows, with the
/// <see cref="OverflowException"/> as its inner exception. A body is chunked when the request has a
/// <c>Transfer-Encoding</c> and no <c>Content-Length</c>, the same test that decides its framing allowance, and on
/// such a body an <see cref="IOException"/> of that stream that is not a <see cref="BadHttpRequestException"/> and
/// whose inner exception is an <see cref="OverflowException"/> fails the read instead with the guard's 400, whose
/// inner exception is the original. On a request the client has aborted, that exception leaves the read as it is, and
/// the exception boundary answers it as the abort. Every other exception of that stream, including any
/// <see cref="IOException"/> on a body with <c>Content-Length</c> and any with another inner exception, leaves the
/// read as it is, the same instance with its own stack trace, so the API's exception handler answers it as the server
/// fault or the abort it is.
/// </para>
/// <para>
/// JSON binding, however, catches a <see cref="BadHttpRequestException"/> from its body read and ends the request with
/// that exception's status and no body. The counting stream therefore records, without catching it, the first such
/// exception to leave any of its reads, whether the guard created it or the server's body stream raised it. When the
/// rest of the pipeline returns without starting the response and with that exception's status, the guard rethrows
/// the same instance with its original stack trace; a response the endpoint started, or a status it chose itself, is
/// left as it is. The exceptions the guard creates, its 413 and its 400, carry fixed messages that never state a size
/// or quote the body. An exception the server's body stream raised and the guard leaves as it is, a
/// <see cref="BadHttpRequestException"/> it records and rethrows included, keeps its own message; the API's exception
/// boundary and handler never read that message, and the only body they write for either source is their fixed
/// ProblemDetails.
/// </para>
/// </remarks>
public sealed class RequestBodyLimitMiddleware
{
    private const string TooLargeMessage = "Request body too large.";

    private const string UnreadableBodyMessage = "The request body could not be read.";

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
    /// limit of a chunked body by its framing allowance; then calls the rest of the pipeline, and rethrows a failed
    /// body read that it turned into an empty response of the failure's status. Every other request passes through
    /// unchanged.
    /// </summary>
    /// <param name="context">The current request.</param>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="BadHttpRequestException">
    /// With status 413, when <c>Content-Length</c> exceeds the limit; the counting stream throws the same exception
    /// from a read that takes the body past the limit. With status 400 from a counting-stream read of a chunked body,
    /// one with a <c>Transfer-Encoding</c> and no <c>Content-Length</c>, that the server's body stream failed with an
    /// <see cref="IOException"/> that is not a <see cref="BadHttpRequestException"/> and whose inner exception is an
    /// <see cref="OverflowException"/>, on a request the client has not aborted, the original as its inner exception;
    /// every other failure of the server's body stream leaves the read unchanged. When a body read failed with a
    /// <see cref="BadHttpRequestException"/> that the rest of the pipeline caught, and it returned without starting
    /// the response and with that exception's status, as JSON binding does, the first such exception is rethrown, the
    /// same instance with its original stack trace.
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
            // Decided once from the headers: the framing allowance and the counting stream's chunk-size failure rule
            // both apply to exactly the chunked bodies.
            bool chunked = IsChunked(request);
            if (chunked)
            {
                AllowChunkFraming(context);
            }

            // Wrapped even under a declared length within the limit: in-process hosts do not hold a body to its
            // Content-Length, and the counter costs one addition per read.
            var stream = new LimitedReadStream(request.Body, _limit, chunked, context.RequestAborted);
            request.Body = stream;

            // The endpoint's body reads leave their state here, so the exception handler can tell a body that ended
            // before its first byte from content that is not JSON without reading the body again.
            context.Features.Set<IRequestBodyReadFeature>(stream);
            return InvokeCountedAsync(context, stream);
        }

        return _next(context);
    }

    // JSON binding catches a BadHttpRequestException from its body read, sets the response status to the exception's
    // and returns with no body, so the exception handler would never see the failure and log its line. The stream
    // recorded that exception; while the response is still unstarted and carries exactly its status, it is rethrown
    // so it reaches the handler the way a throw before the endpoint does. A status the endpoint chose, or a response
    // it started, stands.
    private async Task InvokeCountedAsync(HttpContext context, LimitedReadStream stream)
    {
        await _next(context);

        if (stream.ReadFailure is { } failure
            && !context.Response.HasStarted
            && context.Response.StatusCode == failure.StatusCode)
        {
            ExceptionDispatchInfo.Throw(failure);
        }
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

    // Only a body with a Transfer-Encoding and no Content-Length is chunked; HTTP/2 and HTTP/3 forbid
    // Transfer-Encoding and frame their bodies themselves.
    private static bool IsChunked(HttpRequest request) =>
        request.ContentLength is null && request.Headers.ContainsKey(HeaderNames.TransferEncoding);

    // Kestrel counts the framing of an HTTP/1.1 chunked body against its own limit, so the server limit of a request
    // whose body is chunked is raised to the framing allowance while the counting stream holds the decoded bytes to
    // the limit. The server limit is raised before the first read, while the feature is writable, and never lowered or
    // removed.
    private void AllowChunkFraming(HttpContext context)
    {
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

    // The 400 that replaces the server's body-stream failure for a chunk-size line whose value overflows, a client
    // framing error rather than a server fault, with that failure as its inner exception; its message never copies
    // the failure's.
    private static BadHttpRequestException UnreadableBody(IOException failure) =>
        new(UnreadableBodyMessage, StatusCodes.Status400BadRequest, failure);

    // Decided from headers and server features only, like the media-type guard. A host that does not expose the
    // body-detection feature gets the counting stream, which is harmless on an empty body.
    private static bool CanHaveBody(HttpContext context) =>
        context.Request.ContentLength != 0
        && context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody != false;

    /// <summary>
    /// A read-only, forward-only view of the request body that counts the bytes read and fails every read once the
    /// count exceeds the limit, never taking more than one byte past the limit from the inner stream, and the
    /// request's <see cref="IRequestBodyReadFeature"/>. A body of exactly the limit ends with an ordinary zero-byte
    /// read. On a chunked body, an <see cref="IOException"/> from the inner stream's read that is not a
    /// <see cref="BadHttpRequestException"/> and whose inner exception is an <see cref="OverflowException"/>, the
    /// server's failure for a chunk-size line whose value overflows, fails that read with the guard's 400 instead, the
    /// original as its inner exception, unless the request is aborted; every other exception of the inner stream
    /// leaves the read unchanged. It also records, without catching it, the first
    /// <see cref="BadHttpRequestException"/> that leaves a read. It is used by one request at a time, as the body
    /// stream it wraps is.
    /// </summary>
    private sealed class LimitedReadStream : Stream, IRequestBodyReadFeature
    {
        private readonly Stream _inner;

        private readonly long _limit;

        private readonly bool _chunked;

        private readonly CancellationToken _requestAborted;

        private long _total;

        public LimitedReadStream(Stream inner, long limit, bool chunked, CancellationToken requestAborted)
        {
            _inner = inner;
            _limit = limit;
            _chunked = chunked;
            _requestAborted = requestAborted;
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

        // The first BadHttpRequestException that left any read, or null: this stream's own 413 or 400, or one the
        // server's body stream raised, such as Kestrel's own limit. The guard rethrows it when binding swallowed it.
        internal BadHttpRequestException? ReadFailure { get; private set; }

        // Synchronous reads forward to the inner stream's synchronous read, so the server's AllowSynchronousIO policy
        // still applies to it. Each read records a BadHttpRequestException leaving it through a filter that never
        // matches, so its catch block is never entered and the exception unwinds unchanged, stack trace included. A
        // BadHttpRequestException the first filter declines is offered to the second clause, so that clause's filter
        // excludes it; any other IOException here can come only from the inner stream's read, and the second filter
        // matches only the chunk-size overflow, so every other exception unwinds unchanged as well.
        public override int Read(byte[] buffer, int offset, int count)
        {
            ValidateBufferArguments(buffer, offset, count);
            try
            {
                ThrowIfOverLimit();
                int requested = Bound(count);
                return Count(_inner.Read(buffer, offset, requested), requested);
            }
            catch (BadHttpRequestException failure) when (Remember(failure))
            {
                throw;
            }
            catch (IOException failure) when (IsChunkSizeOverflow(failure))
            {
                throw Unreadable(failure);
            }
        }

        public override int Read(Span<byte> buffer)
        {
            try
            {
                ThrowIfOverLimit();
                int requested = Bound(buffer.Length);
                return Count(_inner.Read(buffer[..requested]), requested);
            }
            catch (BadHttpRequestException failure) when (Remember(failure))
            {
                throw;
            }
            catch (IOException failure) when (IsChunkSizeOverflow(failure))
            {
                throw Unreadable(failure);
            }
        }

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            ValidateBufferArguments(buffer, offset, count);
            try
            {
                ThrowIfOverLimit();
                int requested = Bound(count);
                return Count(
                    await _inner.ReadAsync(buffer, offset, requested, cancellationToken).ConfigureAwait(false),
                    requested);
            }
            catch (BadHttpRequestException failure) when (Remember(failure))
            {
                throw;
            }
            catch (IOException failure) when (IsChunkSizeOverflow(failure))
            {
                throw Unreadable(failure);
            }
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            try
            {
                ThrowIfOverLimit();
                int requested = Bound(buffer.Length);
                return Count(
                    await _inner.ReadAsync(buffer[..requested], cancellationToken).ConfigureAwait(false),
                    requested);
            }
            catch (BadHttpRequestException failure) when (Remember(failure))
            {
                throw;
            }
            catch (IOException failure) when (IsChunkSizeOverflow(failure))
            {
                throw Unreadable(failure);
            }
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

        // The filter of every read: keeps the first failure, the one that ended the body for its reader, and never
        // matches, so nothing is caught or swallowed.
        private bool Remember(BadHttpRequestException failure)
        {
            ReadFailure ??= failure;
            return false;
        }

        // The one client framing error the server's body stream raises as a plain IOException rather than as a
        // BadHttpRequestException: on a chunked body, Kestrel's failure for a chunk-size line whose value overflows,
        // which carries the OverflowException as its inner exception. On an aborted request it is the abort, and it
        // leaves the read unchanged, so the exception boundary logs the abort with the server's own exception type.
        // Every other IOException, on a body with Content-Length or with another cause, is not the client's framing:
        // it leaves the read unchanged, so the exception handler answers it as the server fault or the abort it is.
        private bool IsChunkSizeOverflow(IOException failure) =>
            _chunked
            && failure is not BadHttpRequestException
            && failure.InnerException is OverflowException
            && !_requestAborted.IsCancellationRequested;

        // Replaces such a failure with the guard's 400, recorded like every other failure that leaves a read, so the
        // guard also rethrows it when binding swallowed it.
        private BadHttpRequestException Unreadable(IOException failure)
        {
            BadHttpRequestException unreadable = UnreadableBody(failure);
            Remember(unreadable);
            return unreadable;
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
/// from reads the endpoint already made, so the body is never read, peeked or buffered twice. Both values only grow,
/// and they describe the decoded body bytes, never the transfer framing.
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
