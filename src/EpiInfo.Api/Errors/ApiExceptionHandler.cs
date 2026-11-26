using System.Diagnostics;
using System.Text.Json;
using EpiInfo.Api.Hosting;
using EpiInfo.Data.Sql;
using EpiInfo.Domain.Storage;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Features;

namespace EpiInfo.Api.Errors;

/// <summary>
/// The one mapping from an exception that escapes an endpoint to the API's error response. <c>Program.cs</c> registers
/// it with <c>AddExceptionHandler&lt;ApiExceptionHandler&gt;()</c> behind <c>UseExceptionHandler()</c>, and
/// <c>RouteHandlerOptions.ThrowOnBadRequest</c> is on, so binder failures arrive here with their own status.
/// </summary>
/// <remarks>
/// <para>Classification, first match wins; the response is the same in every environment:</para>
/// <list type="number">
/// <item><description>
/// A client abort (an <see cref="OperationCanceledException"/> or <see cref="IOException"/> while
/// <see cref="HttpContext.RequestAborted"/> is cancelled): no body, one Information line, with no status or write
/// outcome but the same SQL error number and rollback arguments as a failure line. In the API pipeline
/// <see cref="ApiExceptionBoundaryMiddleware"/> answers it, because the exception-handler middleware settles a client
/// abort itself, without calling any handler.
/// </description></item>
/// <item><description>
/// A <see cref="BadHttpRequestException"/> keeps its status: 400 is "request body is required" when the headers say
/// the request has no body, or when a <see cref="JsonException"/> caused it and the body guard's
/// <see cref="IRequestBodyReadFeature"/> saw the body end before its first byte (an empty chunked body); "malformed
/// JSON" when a <see cref="JsonException"/> caused it otherwise; and "the request is invalid" otherwise. 413 and 415
/// get their fixed bodies; any other 4xx keeps its status with the fixed invalid-request text. A request-body JSON
/// failure arrives only in this form, because binding wraps it in a <see cref="BadHttpRequestException"/>.
/// </description></item>
/// <item><description>A <see cref="DataStoreUnavailableException"/> is a 503.</description></item>
/// <item><description>
/// Anything else, <see cref="DataStoreWriteException"/> and an internal cancellation included, is a 500 with the
/// fixed generic <c>detail</c>. So is a bare <see cref="JsonException"/>: it comes from server-side work such as
/// serialising the response, never from the request, and on a record write that can follow the commit, which is then
/// reported as <c>written</c>.
/// </description></item>
/// </list>
/// <para>
/// A 500 or 503 of a record write (an endpoint carrying <see cref="WriteEndpointMetadata"/>) adds
/// <c>writeOutcome</c>, chosen by <see cref="ResolveWriteOutcome"/>. The outcome never changes the status.
/// </para>
/// <para>
/// Redaction: bodies come only from the <see cref="ApiProblems"/> factories, and each failure gets exactly one log
/// line whose arguments are the status, the exception type, the store's SQL error number and operation, the write
/// outcome, a failed rollback's type and SQL error number, and the trace id. The exception object is never given to
/// the logger and its message is never read, because the inner exception of a store failure can hold SQL text,
/// values or a connection string. Each line is written best effort (<see cref="LogBestEffort"/>), so a failing log
/// provider never prevents the response. The handler is stateless and safe to share across requests.
/// </para>
/// <para>
/// A failure after the response has started never reaches a handler: the exception-handler middleware would log the
/// exception object and rethrow it. <see cref="ApiExceptionBoundaryMiddleware"/> answers it under the same redaction,
/// with one Error line and an aborted response whose sent status and headers stay as they are.
/// </para>
/// </remarks>
internal sealed partial class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    private const string DefaultOperation = "request";

    /// <inheritdoc />
    /// <returns>Always <see langword="true"/>: every exception is answered here, or deliberately left without a body.</returns>
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        var traceId = TraceId(httpContext);
        var endpoint = MatchedEndpoint(httpContext);
        var operation = ResolveOperation(exception, endpoint);
        var exceptionType = TypeName(exception);
        var sqlErrorNumber = StoreSqlErrorNumber(exception);
        var (rollbackExceptionType, rollbackSqlErrorNumber) = RollbackFailure(exception);

        // Defensive: in the API pipeline ApiExceptionBoundaryMiddleware answers a client abort first, because the
        // exception-handler middleware settles one itself, without calling any handler. This branch answers a handler
        // invoked outside that pipeline the same way.
        if (IsClientAbort(exception, httpContext))
        {
            LogBestEffort(() => LogClientAbort(
                logger,
                operation,
                exceptionType,
                sqlErrorNumber,
                rollbackExceptionType,
                rollbackSqlErrorNumber,
                traceId));

            // The status the exception-handler middleware itself gives an aborted request; nobody receives a body.
            if (!httpContext.Response.HasStarted)
            {
                httpContext.Response.StatusCode = StatusCodes.Status499ClientClosedRequest;
            }

            return true;
        }

        var failure = Classify(httpContext, exception);
        var body = failure.Body;
        string? writeOutcome = null;

        if (failure.StatusCode is StatusCodes.Status500InternalServerError or StatusCodes.Status503ServiceUnavailable
            && endpoint?.Metadata.GetMetadata<WriteEndpointMetadata>() is not null)
        {
            var outcome = ResolveWriteOutcome(WriteOutcomeFeature.Get(httpContext), exception);
            body = failure.StatusCode == StatusCodes.Status503ServiceUnavailable
                ? ApiProblems.UnavailableWrite(outcome)
                : ApiProblems.UnexpectedWrite(outcome);
            writeOutcome = outcome.ToWireValue();
        }

        LogBestEffort(() => LogRequestFailed(
            logger,
            failure.Level,
            failure.StatusCode,
            exceptionType,
            operation,
            sqlErrorNumber,
            writeOutcome,
            rollbackExceptionType,
            rollbackSqlErrorNumber,
            traceId));

        // Defensive: the exception-handler middleware never calls a handler once the response has started, and in the
        // API pipeline ApiExceptionBoundaryMiddleware answers that failure first. The sent status and headers cannot
        // change, so a handler invoked outside that pipeline writes no body.
        if (httpContext.Response.HasStarted)
        {
            return true;
        }

        try
        {
            await ApiProblems.WriteAsync(httpContext, body);
        }
        catch (Exception writeFailure)
        {
            // Not rethrown: the middleware would then rethrow the original failure, whose inner exception can hold SQL
            // text, into the server log. A client that disconnected mid-write needs no further line.
            if (!IsClientAbort(writeFailure, httpContext))
            {
                LogBestEffort(() => LogErrorBodyWriteFailed(logger, TypeName(writeFailure), traceId));
            }
        }

        return true;
    }

    /// <summary>
    /// The <c>writeOutcome</c> of a failed record write, by this precedence, the feature first:
    /// <see cref="WriteOutcome.Written"/> once the store reported a commit; otherwise
    /// <see cref="WriteOutcome.NotWritten"/> once the store reported a result that wrote nothing, or while no store
    /// write call was issued, such as when the metadata read fails, whatever outcome a store exception reports;
    /// otherwise, for a started write still outstanding, the store exception's own <c>Outcome</c>; otherwise
    /// <see cref="WriteOutcome.Unknown"/>.
    /// </summary>
    /// <param name="feature">The request's write record, or <see langword="null"/> when no write path created one.</param>
    /// <param name="exception">The failure being answered.</param>
    /// <exception cref="ArgumentNullException"><paramref name="exception"/> is <see langword="null"/>.</exception>
    internal static WriteOutcome ResolveWriteOutcome(WriteOutcomeFeature? feature, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (feature?.Written == true)
        {
            return WriteOutcome.Written;
        }

        // No store write call is outstanding: none was issued, or the store reported that its call wrote nothing. A
        // store exception's outcome then describes no write, so it is not consulted.
        if (feature is null || feature.NotWritten || !feature.WriteStarted)
        {
            return WriteOutcome.NotWritten;
        }

        return exception switch
        {
            DataStoreUnavailableException unavailable => unavailable.Outcome,
            DataStoreWriteException failed => failed.Outcome,
            _ => WriteOutcome.Unknown,
        };
    }

    private static HandledFailure Classify(HttpContext httpContext, Exception exception) => exception switch
    {
        BadHttpRequestException badRequest => ClassifyBadRequest(httpContext.Request, badRequest),
        DataStoreUnavailableException =>
            new(StatusCodes.Status503ServiceUnavailable, LogLevel.Error, ApiProblems.Unavailable()),
        _ => new(StatusCodes.Status500InternalServerError, LogLevel.Error, ApiProblems.Unexpected()),
    };

    private static HandledFailure ClassifyBadRequest(HttpRequest request, BadHttpRequestException exception)
    {
        var status = exception.StatusCode;
        var body = status switch
        {
            StatusCodes.Status400BadRequest when IsEmptyBody(request, exception) => ApiProblems.RequestBodyRequired(),
            StatusCodes.Status400BadRequest when HasJsonCause(exception) => ApiProblems.MalformedJson(),
            StatusCodes.Status413PayloadTooLarge => ApiProblems.RequestTooLarge(),
            StatusCodes.Status415UnsupportedMediaType => ApiProblems.UnsupportedMediaType(),
            > StatusCodes.Status400BadRequest and < StatusCodes.Status500InternalServerError =>
                new ApiProblems.ProblemResult(ApiProblems.CreateProblem(status, ApiProblems.InvalidRequestDetail)),
            _ => ApiProblems.InvalidRequest(),
        };

        // A status outside 4xx is not a client error, whatever the exception's type says; it is answered as the
        // plain request-level 400 rather than as a server error with client-error text.
        var written = status is >= StatusCodes.Status400BadRequest and < StatusCodes.Status500InternalServerError
            ? status
            : StatusCodes.Status400BadRequest;
        return new(written, LogLevel.Information, body);
    }

    private static bool HasJsonCause(Exception exception)
    {
        for (var cause = exception.InnerException; cause is not null; cause = cause.InnerException)
        {
            if (cause is JsonException)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasNoBody(HttpRequest request) =>
        request.HttpContext.Features.Get<IHttpRequestBodyDetectionFeature>() is { CanHaveBody: false }
        || request.ContentLength == 0;

    // Empty by its headers, or, for a JSON failure, by the body guard's read state: the reads binding made found the
    // end of the body before its first byte, as with an empty chunked body. Nothing here reads the body, and a host
    // without the guard is judged by its headers alone.
    private static bool IsEmptyBody(HttpRequest request, BadHttpRequestException exception) =>
        HasNoBody(request)
        || (HasJsonCause(exception)
            && request.HttpContext.Features.Get<IRequestBodyReadFeature>() is { EndOfBody: true, BytesRead: 0 });

    internal static bool IsClientAbort(Exception exception, HttpContext httpContext) =>
        (exception is OperationCanceledException or IOException) && httpContext.RequestAborted.IsCancellationRequested;

    // The trace id that correlates one request's failure lines, those of this handler and of
    // ApiExceptionBoundaryMiddleware, with the traceId member of the ProblemDetails bodies ApiProblems writes for it.
    // No other log line carries it, the health, write-audit and check-code diagnostic lines included, and neither
    // does the /health body.
    internal static string TraceId(HttpContext httpContext) => Activity.Current?.Id ?? httpContext.TraceIdentifier;

    // UseExceptionHandler clears the request's endpoint before it calls any handler and keeps the matched one on
    // IExceptionHandlerFeature, so GetEndpoint() alone would never see WriteEndpointMetadata there. It still answers
    // for a handler invoked outside that middleware.
    private static Endpoint? MatchedEndpoint(HttpContext httpContext) =>
        httpContext.Features.Get<IExceptionHandlerFeature>()?.Endpoint ?? httpContext.GetEndpoint();

    internal static string ResolveOperation(Exception exception, Endpoint? endpoint) =>
        (exception switch
        {
            DataStoreUnavailableException unavailable => unavailable.Operation,
            DataStoreWriteException failed => failed.Operation,
            _ => null,
        })
        ?? endpoint?.Metadata.GetMetadata<WriteEndpointMetadata>()?.Operation
        ?? endpoint?.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName
        ?? DefaultOperation;

    // A store exception's own SQL error number; for any other exception, such as the OperationCanceledException the
    // store raises for SqlClient's acknowledgement of a cancelled command, the number the store kept beside it. Only
    // an int stored under that key is read.
    internal static int? StoreSqlErrorNumber(Exception exception) => exception switch
    {
        DataStoreUnavailableException unavailable => unavailable.SqlErrorNumber,
        DataStoreWriteException failed => failed.SqlErrorNumber,
        _ => exception.Data.Contains(SqlErrorClassifier.SqlErrorNumberKey)
            && exception.Data[SqlErrorClassifier.SqlErrorNumberKey] is int number
                ? number
                : null,
    };

    // The repository records a failed rollback beside the original failure, as a type name and a SQL error number
    // only; anything else stored under these keys is ignored rather than logged.
    internal static (string? ExceptionType, int? SqlErrorNumber) RollbackFailure(Exception exception)
    {
        var data = exception.Data;
        var type = data.Contains(SqlErrorClassifier.RollbackExceptionTypeKey)
            ? data[SqlErrorClassifier.RollbackExceptionTypeKey] as string
            : null;
        int? number = data.Contains(SqlErrorClassifier.RollbackSqlErrorNumberKey)
            && data[SqlErrorClassifier.RollbackSqlErrorNumberKey] is int value
                ? value
                : null;
        return (type, number);
    }

    internal static string TypeName(Exception exception)
    {
        var type = exception.GetType();
        return type.FullName ?? type.Name;
    }

    // Writes one failure line of this handler or of ApiExceptionBoundaryMiddleware. The logger rethrows a provider's
    // failure only after every provider has had the line, so the working sinks still get it.
    internal static void LogBestEffort(Action log)
    {
        try
        {
            log();
        }
        catch (Exception)
        {
            // Deliberately swallowed, with nothing logged in its place: a failing log provider must never stop the
            // error body, the 499 status or the abort that follows the line, and a throw from here would let the
            // exception-handler middleware log and rethrow the original failure, whose inner exception can hold SQL
            // text or a connection string.
        }
    }

    [LoggerMessage(
        EventId = 1,
        EventName = "RequestFailed",
        Message = "Request failed with status {StatusCode}: {ExceptionType} during {Operation}; SQL error {SqlErrorNumber}; "
            + "write outcome {WriteOutcome}; rollback {RollbackExceptionType} {RollbackSqlErrorNumber}; TraceId {TraceId}")]
    private static partial void LogRequestFailed(
        ILogger logger,
        LogLevel level,
        int statusCode,
        string exceptionType,
        string operation,
        int? sqlErrorNumber,
        string? writeOutcome,
        string? rollbackExceptionType,
        int? rollbackSqlErrorNumber,
        string traceId);

    [LoggerMessage(
        EventId = 2,
        EventName = "RequestAborted",
        Level = LogLevel.Information,
        Message = "Request aborted by the client during {Operation}: {ExceptionType}; SQL error {SqlErrorNumber}; "
            + "rollback {RollbackExceptionType} {RollbackSqlErrorNumber}; TraceId {TraceId}")]
    internal static partial void LogClientAbort(
        ILogger logger,
        string operation,
        string exceptionType,
        int? sqlErrorNumber,
        string? rollbackExceptionType,
        int? rollbackSqlErrorNumber,
        string traceId);

    [LoggerMessage(
        EventId = 3,
        EventName = "ErrorBodyWriteFailed",
        Level = LogLevel.Error,
        Message = "Writing the error response failed with {ExceptionType}; TraceId {TraceId}")]
    private static partial void LogErrorBodyWriteFailed(ILogger logger, string exceptionType, string traceId);

    /// <summary>The status, log level and body chosen for one failure.</summary>
    private readonly record struct HandledFailure(int StatusCode, LogLevel Level, IResult Body);
}

/// <summary>
/// The API's exception boundary, just inside <c>UseExceptionHandler()</c>. It answers the two failures the
/// exception-handler middleware settles itself, before it calls any <see cref="IExceptionHandler"/>, so that neither
/// escapes the error contract and redaction of <see cref="ApiExceptionHandler"/>. Every other exception passes through
/// unchanged to that middleware and handler.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>
/// A client abort (an <see cref="OperationCanceledException"/> or <see cref="IOException"/> while
/// <see cref="HttpContext.RequestAborted"/> is cancelled), before or after the response started: one Information line,
/// status 499 when the response has not started, and no body. The middleware itself would log it only at Debug, which
/// the committed <c>Microsoft.AspNetCore</c> filter drops.
/// </description></item>
/// <item><description>
/// Any other failure after the response started: one Error line, then <see cref="HttpContext.Abort"/>, so the
/// incomplete response is never completed as if it were whole. The sent status, headers and body are not rewritten.
/// The middleware itself would log the exception object and rethrow it to the server, which would log it again.
/// </description></item>
/// </list>
/// <para>
/// Neither case is rethrown. The lines share the handler's log category and argument rules: the exception object is
/// never given to the logger and its message is never read. They are written best effort, so a failing log provider
/// never prevents the 499 status or the abort. The matched endpoint is still on the request here,
/// because the exception-handler middleware clears it only once it handles an exception itself.
/// </para>
/// </remarks>
internal sealed partial class ApiExceptionBoundaryMiddleware
{
    private readonly RequestDelegate _next;

    private readonly ILogger<ApiExceptionHandler> _logger;

    /// <summary>Creates the boundary in front of <paramref name="next"/>.</summary>
    /// <param name="next">The rest of the pipeline.</param>
    /// <param name="logger">The exception handler's logger, so every failure line of a request has one category.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="next"/> or <paramref name="logger"/> is <see langword="null"/>.
    /// </exception>
    public ApiExceptionBoundaryMiddleware(RequestDelegate next, ILogger<ApiExceptionHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(logger);
        _next = next;
        _logger = logger;
    }

    /// <summary>
    /// Calls the rest of the pipeline, and answers a client abort, or a failure after the response started, that
    /// escapes it.
    /// </summary>
    /// <param name="httpContext">The current request.</param>
    /// <exception cref="ArgumentNullException"><paramref name="httpContext"/> is <see langword="null"/>.</exception>
    public async Task InvokeAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        try
        {
            await _next(httpContext);
        }
        catch (Exception exception) when (ApiExceptionHandler.IsClientAbort(exception, httpContext))
        {
            var (rollbackExceptionType, rollbackSqlErrorNumber) = ApiExceptionHandler.RollbackFailure(exception);
            ApiExceptionHandler.LogBestEffort(() => ApiExceptionHandler.LogClientAbort(
                _logger,
                ApiExceptionHandler.ResolveOperation(exception, httpContext.GetEndpoint()),
                ApiExceptionHandler.TypeName(exception),
                ApiExceptionHandler.StoreSqlErrorNumber(exception),
                rollbackExceptionType,
                rollbackSqlErrorNumber,
                ApiExceptionHandler.TraceId(httpContext)));

            // The status the exception-handler middleware itself gives an aborted request; nobody receives a body.
            if (!httpContext.Response.HasStarted)
            {
                httpContext.Response.StatusCode = StatusCodes.Status499ClientClosedRequest;
            }
        }
        catch (Exception exception) when (httpContext.Response.HasStarted)
        {
            var endpoint = httpContext.GetEndpoint();
            var writeOutcome = endpoint?.Metadata.GetMetadata<WriteEndpointMetadata>() is null
                ? null
                : ApiExceptionHandler.ResolveWriteOutcome(WriteOutcomeFeature.Get(httpContext), exception).ToWireValue();
            var (rollbackExceptionType, rollbackSqlErrorNumber) = ApiExceptionHandler.RollbackFailure(exception);
            ApiExceptionHandler.LogBestEffort(() => LogResponseAborted(
                _logger,
                httpContext.Response.StatusCode,
                ApiExceptionHandler.TypeName(exception),
                ApiExceptionHandler.ResolveOperation(exception, endpoint),
                ApiExceptionHandler.StoreSqlErrorNumber(exception),
                writeOutcome,
                rollbackExceptionType,
                rollbackSqlErrorNumber,
                ApiExceptionHandler.TraceId(httpContext)));

            // The client already holds a status and part of a body. Aborting the connection, which withholds the final
            // chunk, keeps it from reading the incomplete response as a whole one.
            httpContext.Abort();
        }
    }

    // Event id 4 continues the numbering of ApiExceptionHandler's events, whose category this line shares.
    [LoggerMessage(
        EventId = 4,
        EventName = "ResponseAborted",
        Level = LogLevel.Error,
        Message = "Request failed after the response started, with status {StatusCode}: {ExceptionType} during "
            + "{Operation}; SQL error {SqlErrorNumber}; write outcome {WriteOutcome}; rollback {RollbackExceptionType} "
            + "{RollbackSqlErrorNumber}; TraceId {TraceId}")]
    private static partial void LogResponseAborted(
        ILogger logger,
        int statusCode,
        string exceptionType,
        string operation,
        int? sqlErrorNumber,
        string? writeOutcome,
        string? rollbackExceptionType,
        int? rollbackSqlErrorNumber,
        string traceId);
}

/// <summary>Pipeline registration for <see cref="ApiExceptionBoundaryMiddleware"/>.</summary>
internal static class ApiExceptionBoundaryExtensions
{
    /// <summary>
    /// Adds <see cref="ApiExceptionBoundaryMiddleware"/>. Call it right after <c>UseExceptionHandler()</c> and before
    /// <c>UseStatusCodePages()</c>: inside the exception-handler middleware, so that middleware never settles a client
    /// abort or a failure after the response started itself, and outside status-code pages, so they never write a body
    /// for an aborted request.
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> is <see langword="null"/>.</exception>
    public static IApplicationBuilder UseApiExceptionBoundary(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<ApiExceptionBoundaryMiddleware>();
    }
}
