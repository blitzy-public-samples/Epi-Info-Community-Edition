using System.Diagnostics;
using System.Text.Json;
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
/// <see cref="HttpContext.RequestAborted"/> is cancelled): no body, one Information line.
/// </description></item>
/// <item><description>
/// A <see cref="BadHttpRequestException"/> keeps its status: 400 is "malformed JSON" when a
/// <see cref="JsonException"/> caused it, "request body is required" when the request has no body, and "the request
/// is invalid" otherwise; 413 and 415 get their fixed bodies; any other 4xx keeps its status with the fixed
/// invalid-request text.
/// </description></item>
/// <item><description>A bare <see cref="JsonException"/> is the 400 "malformed JSON".</description></item>
/// <item><description>A <see cref="DataStoreUnavailableException"/> is a 503.</description></item>
/// <item><description>
/// Anything else, <see cref="DataStoreWriteException"/> and an internal cancellation included, is a 500 with the
/// fixed generic <c>detail</c>.
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
/// values or a connection string. The handler is stateless and safe to share across requests.
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

        var traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;
        var endpoint = MatchedEndpoint(httpContext);
        var operation = ResolveOperation(exception, endpoint);
        var exceptionType = TypeName(exception);

        if (IsClientAbort(exception, httpContext))
        {
            LogClientAbort(logger, operation, exceptionType, traceId);

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

        var (rollbackExceptionType, rollbackSqlErrorNumber) = RollbackFailure(exception);
        LogRequestFailed(
            logger,
            failure.Level,
            failure.StatusCode,
            exceptionType,
            operation,
            StoreSqlErrorNumber(exception),
            writeOutcome,
            rollbackExceptionType,
            rollbackSqlErrorNumber,
            traceId);

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
                LogErrorBodyWriteFailed(logger, TypeName(writeFailure), traceId);
            }
        }

        return true;
    }

    /// <summary>
    /// The <c>writeOutcome</c> of a failed record write, by this precedence: <see cref="WriteOutcome.Written"/> once
    /// the store reported a commit; otherwise the store exception's own <c>Outcome</c>; otherwise
    /// <see cref="WriteOutcome.NotWritten"/> when no store write call is outstanding: none was issued, or the store
    /// reported that it wrote nothing; otherwise <see cref="WriteOutcome.Unknown"/>.
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

        return exception switch
        {
            DataStoreUnavailableException unavailable => unavailable.Outcome,
            DataStoreWriteException failed => failed.Outcome,
            _ when feature is null || !feature.WriteStarted => WriteOutcome.NotWritten,
            _ => WriteOutcome.Unknown,
        };
    }

    private static HandledFailure Classify(HttpContext httpContext, Exception exception) => exception switch
    {
        BadHttpRequestException badRequest => ClassifyBadRequest(httpContext.Request, badRequest),
        JsonException => new(StatusCodes.Status400BadRequest, LogLevel.Information, ApiProblems.MalformedJson()),
        DataStoreUnavailableException =>
            new(StatusCodes.Status503ServiceUnavailable, LogLevel.Warning, ApiProblems.Unavailable()),
        _ => new(StatusCodes.Status500InternalServerError, LogLevel.Error, ApiProblems.Unexpected()),
    };

    private static HandledFailure ClassifyBadRequest(HttpRequest request, BadHttpRequestException exception)
    {
        var status = exception.StatusCode;
        var body = status switch
        {
            StatusCodes.Status400BadRequest when HasJsonCause(exception) => ApiProblems.MalformedJson(),
            StatusCodes.Status400BadRequest when HasNoBody(request) => ApiProblems.RequestBodyRequired(),
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

    private static bool IsClientAbort(Exception exception, HttpContext httpContext) =>
        (exception is OperationCanceledException or IOException) && httpContext.RequestAborted.IsCancellationRequested;

    // UseExceptionHandler clears the request's endpoint before it calls any handler and keeps the matched one on
    // IExceptionHandlerFeature, so GetEndpoint() alone would never see WriteEndpointMetadata there. It still answers
    // for a handler invoked outside that middleware.
    private static Endpoint? MatchedEndpoint(HttpContext httpContext) =>
        httpContext.Features.Get<IExceptionHandlerFeature>()?.Endpoint ?? httpContext.GetEndpoint();

    private static string ResolveOperation(Exception exception, Endpoint? endpoint) =>
        (exception switch
        {
            DataStoreUnavailableException unavailable => unavailable.Operation,
            DataStoreWriteException failed => failed.Operation,
            _ => null,
        })
        ?? endpoint?.Metadata.GetMetadata<WriteEndpointMetadata>()?.Operation
        ?? endpoint?.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName
        ?? DefaultOperation;

    private static int? StoreSqlErrorNumber(Exception exception) => exception switch
    {
        DataStoreUnavailableException unavailable => unavailable.SqlErrorNumber,
        DataStoreWriteException failed => failed.SqlErrorNumber,
        _ => null,
    };

    // The repository records a failed rollback beside the original failure, as a type name and a SQL error number
    // only; anything else stored under these keys is ignored rather than logged.
    private static (string? ExceptionType, int? SqlErrorNumber) RollbackFailure(Exception exception)
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

    private static string TypeName(Exception exception)
    {
        var type = exception.GetType();
        return type.FullName ?? type.Name;
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
        Message = "Request aborted by the client during {Operation}: {ExceptionType}; TraceId {TraceId}")]
    private static partial void LogClientAbort(ILogger logger, string operation, string exceptionType, string traceId);

    [LoggerMessage(
        EventId = 3,
        EventName = "ErrorBodyWriteFailed",
        Level = LogLevel.Error,
        Message = "Writing the error response failed with {ExceptionType}; TraceId {TraceId}")]
    private static partial void LogErrorBodyWriteFailed(ILogger logger, string exceptionType, string traceId);

    /// <summary>The status, log level and body chosen for one failure.</summary>
    private readonly record struct HandledFailure(int StatusCode, LogLevel Level, IResult Body);
}
