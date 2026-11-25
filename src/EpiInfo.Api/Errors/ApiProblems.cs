using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using EpiInfo.Domain.Records;
using EpiInfo.Domain.Search;
using EpiInfo.Domain.Storage;
using EpiInfo.Domain.Validation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

namespace EpiInfo.Api.Errors;

/// <summary>
/// The single builder and writer of the API's error bodies. Every error response under <c>/api</c> is an RFC 7807
/// ProblemDetails with <c>type</c>, <c>title</c>, <c>status</c>, <c>detail</c> and <c>traceId</c>; a field-level 400
/// is an <see cref="HttpValidationProblemDetails"/> whose <c>errors</c> map is keyed by canonical field or parameter
/// name, and the 500 and 503 of a record write are a <see cref="WriteProblemDetails"/>. The <c>/health</c> 503 body
/// is written by the health endpoint and never by this class.
/// </summary>
/// <remarks>
/// <para>
/// Redaction: every <c>detail</c> is one of the fixed texts below, except that a 500's is
/// <see cref="UnexpectedDetail"/> followed by the request's trace id (<see cref="UnexpectedDetailFor"/>), the same
/// value the body's <c>traceId</c> member carries. Every <c>errors</c> message is a fixed Domain text from
/// <c>ValidationMessages</c>, <c>SearchCriteriaParser</c> or <c>RecordQueryRules</c>. Nothing here formats a submitted
/// value, a query-string value, SQL text, a connection string, or an exception's message or type into a body.
/// </para>
/// <para>
/// Endpoints and services return the <see cref="IResult"/> factories; middleware and the exception handler write
/// them through <see cref="WriteAsync(HttpContext, IResult)"/>. Empty error responses produced inside the pipeline
/// (route miss, 405, routing 415) are written by <see cref="WriteStatusCodePageAsync"/> whatever the request's
/// <c>Accept</c> header, and bodies other framework writers produce through <c>IProblemDetailsService</c> pass
/// through <see cref="Customize"/>. All members are stateless and safe to call from any thread; each factory call
/// returns a new body.
/// </para>
/// </remarks>
public static class ApiProblems
{
    /// <summary>The media type of every error body.</summary>
    public const string ProblemContentType = "application/problem+json";

    /// <summary>The name of the ProblemDetails extension member that carries the request's trace id.</summary>
    public const string TraceIdKey = "traceId";

    /// <summary>The <c>detail</c> of a field-level 400.</summary>
    public const string ValidationFailedDetail = "one or more values are invalid";

    /// <summary>The <c>detail</c> of the request-level 400 for a body that is not valid JSON.</summary>
    public const string MalformedJsonDetail = "malformed JSON";

    /// <summary>The <c>detail</c> of the request-level 400 for a missing, empty or JSON <c>null</c> body.</summary>
    public const string RequestBodyRequiredDetail = "request body is required";

    /// <summary>The <c>detail</c> of the request-level 400 for a missing, <c>null</c> or non-object <c>values</c>.</summary>
    public const string ValuesMustBeObjectDetail = ValidationMessages.ValuesMustBeObject;

    /// <summary>The <c>detail</c> of the request-level 400 for a <c>POST</c> whose values are all <c>null</c>.</summary>
    public const string RecordHasNoValuesDetail = ValidationMessages.RecordHasNoValues;

    /// <summary>The <c>detail</c> of the request-level 400 for a search with no criteria.</summary>
    public const string CriteriaMustNotBeEmptyDetail = SearchCriteriaParser.EmptyCriteriaMessage;

    /// <summary>The <c>detail</c> of a request-level 400 for any other request the binder rejects.</summary>
    public const string InvalidRequestDetail = "the request is invalid";

    /// <summary>The <c>detail</c> of the request-level 400 for a <c>Host</c> not 127.0.0.1 or localhost.</summary>
    public const string HostNotAllowedDetail = "the request host is not allowed";

    /// <summary>The <c>detail</c> of the 404 for a <c>viewId</c> that is not in <c>metaViews</c>.</summary>
    public const string FormNotFoundDetail = "the form does not exist";

    /// <summary>The <c>detail</c> of the 404 for a <c>globalRecordId</c> that is not in the base table.</summary>
    public const string RecordNotFoundDetail = "the record does not exist";

    /// <summary>The <c>detail</c> of the 404 for a position outside 1..<c>count</c>.</summary>
    public const string PositionNotFoundDetail = "no record exists at this position";

    /// <summary>The <c>detail</c> of the 404 the framework writes for a request that matches no route.</summary>
    public const string ResourceNotFoundDetail = "the resource does not exist";

    /// <summary>The <c>detail</c> of the 405 the framework writes for a route that does not allow the method.</summary>
    public const string MethodNotAllowedDetail = "method not allowed";

    /// <summary>The <c>detail</c> of the 409 for a <c>PUT</c> on a record whose <c>RECSTATUS</c> is 0.</summary>
    public const string RecordDeletedDetail = "the record is deleted";

    /// <summary>The <c>detail</c> of the 413 for a body over the configured limit.</summary>
    public const string RequestTooLargeDetail = "request too large";

    /// <summary>The <c>detail</c> of the 415 for a media type other than <c>application/json</c>.</summary>
    public const string UnsupportedMediaTypeDetail = "unsupported media type; use application/json";

    /// <summary>
    /// The one generic sentence that opens the <c>detail</c> of every 500, identical in every environment;
    /// <see cref="UnexpectedDetailFor"/> appends the request's trace id to it.
    /// </summary>
    /// <remarks>
    /// One sentence by design: a 500's <c>detail</c> is exactly this fixed sentence followed by the trace id, and
    /// nothing else.
    /// </remarks>
    public const string UnexpectedDetail = "An unexpected error occurred.";

    // Joins UnexpectedDetail and the trace id in a 500's detail.
    private const string TraceIdLabel = " Trace id: ";

    /// <summary>The <c>detail</c> of every 503.</summary>
    public const string UnavailableDetail = "the database is unavailable";

    /// <summary>The <c>detail</c> of the 403 for a denied create.</summary>
    public const string ForbiddenInsertDetail = "You do not have permission to create records.";

    /// <summary>The <c>detail</c> of the 403 for a denied update.</summary>
    public const string ForbiddenUpdateDetail = "You do not have permission to update records.";

    /// <summary>The <c>detail</c> of the 403 for a denied delete.</summary>
    public const string ForbiddenDeleteDetail = "You do not have permission to delete records.";

    // Deliberately independent of the application's JSON options: a null DictionaryKeyPolicy keeps every errors key
    // exactly as the canonical metaFields.Name or parameter name (LastName, pageSize), never camel-cased.
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DictionaryKeyPolicy = null,
    };

    /// <summary>
    /// The <c>detail</c> of a 500: <see cref="UnexpectedDetail"/> followed by <paramref name="traceId"/>, the value the
    /// same body carries in its <c>traceId</c> member. Every 500 the API writes uses it, with the trace id taken when
    /// the body is written.
    /// </summary>
    /// <param name="traceId">
    /// The request's trace id: the current activity's id, or else the request's trace identifier.
    /// </param>
    /// <returns>The generic sentence and the trace id, joined with no culture-dependent formatting.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="traceId"/> is <see langword="null"/>, empty or whitespace.
    /// </exception>
    public static string UnexpectedDetailFor(string traceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(traceId);
        return string.Concat(UnexpectedDetail, TraceIdLabel, traceId);
    }

    /// <summary>
    /// Writes <paramref name="problem"/> to the response: the path middleware and the exception handler use, so every
    /// error body is written the same way. Nothing is written once the response has started.
    /// </summary>
    /// <param name="context">The current request.</param>
    /// <param name="problem">A result returned by one of this class's factories.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static Task WriteAsync(HttpContext context, IResult problem)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(problem);
        return problem.ExecuteAsync(context);
    }

    /// <summary>
    /// Writes a body built by <see cref="CreateProblem"/> or <see cref="CreateWriteProblem"/>, such as a request-level
    /// error for an uncommon <see cref="BadHttpRequestException"/> status, through the same path as
    /// <see cref="WriteAsync(HttpContext, IResult)"/>. A 5xx other than 503 is written with the generic 500
    /// <c>detail</c> of <see cref="UnexpectedDetailFor"/>, whatever detail it was built with.
    /// </summary>
    /// <param name="context">The current request.</param>
    /// <param name="problem">The body; its <see cref="ProblemDetails.Status"/> must be a 4xx or 5xx status.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="problem"/> has no 4xx or 5xx status.</exception>
    public static Task WriteAsync(HttpContext context, ProblemDetails problem)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(problem);
        var correlate = problem.Status is int status && IsUnexpectedStatus(status);
        return new ProblemResult(problem, correlate).ExecuteAsync(context);
    }

    /// <summary>
    /// A field-level 400: an <see cref="HttpValidationProblemDetails"/> holding an ordinal copy of
    /// <paramref name="errors"/>, in its key and message order.
    /// </summary>
    /// <param name="errors">
    /// Fixed messages keyed by canonical field name or parameter name; at least one key, each with at least one
    /// non-blank message.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="errors"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="errors"/> is empty, or a key has a <see langword="null"/> name, no message or a blank message.
    /// </exception>
    public static IResult ValidationFailed(IReadOnlyDictionary<string, string[]> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);

        var copy = new Dictionary<string, string[]>(errors.Count, StringComparer.Ordinal);
        foreach (var (key, messages) in errors)
        {
            // An empty key stays allowed: JSON permits "" as a property name, and the validator keys an unknown
            // field by the name as submitted.
            if (key is null)
            {
                throw new ArgumentException("A field-level error must be keyed by a name.", nameof(errors));
            }

            if (messages is not { Length: > 0 } || Array.Exists(messages, string.IsNullOrWhiteSpace))
            {
                throw new ArgumentException("Each error key needs at least one non-blank message.", nameof(errors));
            }

            if (!copy.TryAdd(key, [.. messages]))
            {
                throw new ArgumentException("Each error key may appear only once.", nameof(errors));
            }
        }

        if (copy.Count == 0)
        {
            throw new ArgumentException("A field-level 400 needs at least one error.", nameof(errors));
        }

        return new ProblemResult(CreateValidationProblem(copy));
    }

    /// <summary>
    /// A field-level 400 for one route or query parameter that failed coercion, keyed by its wire name, such as
    /// <c>page</c>, <c>pageSize</c>, <c>position</c>, <c>includeDeleted</c>, <c>event</c>, <c>fieldName</c> or
    /// <c>criteria</c>.
    /// </summary>
    /// <param name="parameter">The parameter's wire name.</param>
    /// <param name="message">A fixed message that does not echo the submitted value.</param>
    /// <exception cref="ArgumentException">An argument is <see langword="null"/>, empty or whitespace.</exception>
    public static IResult InvalidParameter(string parameter, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parameter);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        var errors = new Dictionary<string, string[]>(1, StringComparer.Ordinal) { [parameter] = [message] };
        return new ProblemResult(CreateValidationProblem(errors));
    }

    /// <summary>
    /// A field-level 400 for the paging errors <see cref="RecordQueryRules.ValidatePaging"/> reported, with the
    /// messages of each parameter merged under its name in the order they were reported.
    /// </summary>
    /// <param name="errors">At least one error, each with a non-blank parameter and message.</param>
    /// <exception cref="ArgumentNullException"><paramref name="errors"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="errors"/> is empty, or an entry has a blank parameter or message.
    /// </exception>
    public static IResult FromPaging(IReadOnlyList<PagingError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        if (errors.Count == 0)
        {
            throw new ArgumentException("A paging 400 needs at least one error.", nameof(errors));
        }

        var merged = new Dictionary<string, List<string>>(errors.Count, StringComparer.Ordinal);
        foreach (var error in errors)
        {
            if (string.IsNullOrWhiteSpace(error.Parameter) || string.IsNullOrWhiteSpace(error.Message))
            {
                throw new ArgumentException("A paging error needs a parameter and a message.", nameof(errors));
            }

            if (!merged.TryGetValue(error.Parameter, out var messages))
            {
                messages = [];
                merged.Add(error.Parameter, messages);
            }

            if (!messages.Contains(error.Message, StringComparer.Ordinal))
            {
                messages.Add(error.Message);
            }
        }

        var copy = new Dictionary<string, string[]>(merged.Count, StringComparer.Ordinal);
        foreach (var (parameter, messages) in merged)
        {
            copy.Add(parameter, [.. messages]);
        }

        return new ProblemResult(CreateValidationProblem(copy));
    }

    /// <summary>
    /// The 400 for a failed <c>RecordValidator</c> step: a request-level ProblemDetails with no <c>errors</c> when
    /// <see cref="RecordValidationResult.RequestError"/> is set, otherwise a field-level 400 from
    /// <see cref="RecordValidationResult.ToErrorDictionary"/>.
    /// </summary>
    /// <param name="result">A failed validation result.</param>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="result"/> is valid, so there is no error to report.</exception>
    public static IResult FromValidation(RecordValidationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.IsValid)
        {
            throw new InvalidOperationException("A valid record has no validation problem to report.");
        }

        return result.RequestError is { } detail ? RequestLevel(detail) : ValidationFailed(result.ToErrorDictionary());
    }

    /// <summary>
    /// The 400 for search criteria that did not parse: a request-level ProblemDetails with no <c>errors</c> when
    /// <see cref="SearchParseResult.RequestError"/> is set, otherwise a field-level 400 from
    /// <see cref="SearchParseResult.FieldErrors"/>.
    /// </summary>
    /// <param name="result">A failed parse result.</param>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="result"/> succeeded, so there is no error to report.</exception>
    public static IResult FromSearch(SearchParseResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.IsSuccess)
        {
            throw new InvalidOperationException("Parsed search criteria have no problem to report.");
        }

        return result.RequestError is { } detail ? RequestLevel(detail) : ValidationFailed(result.FieldErrors);
    }

    /// <summary>
    /// A request-level 400: a plain ProblemDetails whose <c>detail</c> names the problem, with no <c>errors</c> member.
    /// </summary>
    /// <param name="detail">A fixed text, such as a Domain constant; never a submitted value.</param>
    /// <exception cref="ArgumentException"><paramref name="detail"/> is <see langword="null"/>, empty or whitespace.</exception>
    public static IResult RequestLevel(string detail) =>
        new ProblemResult(CreateProblem(StatusCodes.Status400BadRequest, detail));

    /// <summary>The request-level 400 <see cref="MalformedJsonDetail"/>.</summary>
    public static IResult MalformedJson() => RequestLevel(MalformedJsonDetail);

    /// <summary>The request-level 400 <see cref="RequestBodyRequiredDetail"/>.</summary>
    public static IResult RequestBodyRequired() => RequestLevel(RequestBodyRequiredDetail);

    /// <summary>The request-level 400 <see cref="ValuesMustBeObjectDetail"/>.</summary>
    public static IResult ValuesMustBeObject() => RequestLevel(ValuesMustBeObjectDetail);

    /// <summary>The request-level 400 <see cref="RecordHasNoValuesDetail"/>.</summary>
    public static IResult RecordHasNoValues() => RequestLevel(RecordHasNoValuesDetail);

    /// <summary>The request-level 400 <see cref="CriteriaMustNotBeEmptyDetail"/>.</summary>
    public static IResult CriteriaMustNotBeEmpty() => RequestLevel(CriteriaMustNotBeEmptyDetail);

    /// <summary>The request-level 400 <see cref="InvalidRequestDetail"/>.</summary>
    public static IResult InvalidRequest() => RequestLevel(InvalidRequestDetail);

    /// <summary>The request-level 400 <see cref="HostNotAllowedDetail"/>.</summary>
    public static IResult HostNotAllowed() => RequestLevel(HostNotAllowedDetail);

    /// <summary>The 404 <see cref="FormNotFoundDetail"/>.</summary>
    public static IResult FormNotFound() => Create(StatusCodes.Status404NotFound, FormNotFoundDetail);

    /// <summary>The 404 <see cref="RecordNotFoundDetail"/>.</summary>
    public static IResult RecordNotFound() => Create(StatusCodes.Status404NotFound, RecordNotFoundDetail);

    /// <summary>The 404 <see cref="PositionNotFoundDetail"/>.</summary>
    public static IResult PositionNotFound() => Create(StatusCodes.Status404NotFound, PositionNotFoundDetail);

    /// <summary>The 409 <see cref="RecordDeletedDetail"/>.</summary>
    public static IResult RecordDeleted() => Create(StatusCodes.Status409Conflict, RecordDeletedDetail);

    /// <summary>The 413 <see cref="RequestTooLargeDetail"/>.</summary>
    public static IResult RequestTooLarge() => Create(StatusCodes.Status413PayloadTooLarge, RequestTooLargeDetail);

    /// <summary>The 415 <see cref="UnsupportedMediaTypeDetail"/>.</summary>
    public static IResult UnsupportedMediaType() =>
        Create(StatusCodes.Status415UnsupportedMediaType, UnsupportedMediaTypeDetail);

    /// <summary>The 403 for a write the permission check denies, with the fixed text for <paramref name="action"/>.</summary>
    /// <param name="action">The denied operation.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="action"/> is not a defined value.</exception>
    public static IResult Forbidden(ForbiddenAction action)
    {
        var detail = action switch
        {
            ForbiddenAction.Insert => ForbiddenInsertDetail,
            ForbiddenAction.Update => ForbiddenUpdateDetail,
            ForbiddenAction.Delete => ForbiddenDeleteDetail,
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };

        return Create(StatusCodes.Status403Forbidden, detail);
    }

    /// <summary>
    /// The 500 of every endpoint other than a record write. Each time the result is written, its <c>detail</c> is
    /// rebuilt as <see cref="UnexpectedDetailFor"/> of the trace id written to the same body's <c>traceId</c>.
    /// </summary>
    public static IResult Unexpected() => new ProblemResult(
        CreateProblem(StatusCodes.Status500InternalServerError, UnexpectedDetail),
        correlateUnexpectedDetail: true);

    /// <summary>The 503 <see cref="UnavailableDetail"/> of every endpoint other than a record write.</summary>
    public static IResult Unavailable() => Create(StatusCodes.Status503ServiceUnavailable, UnavailableDetail);

    /// <summary>
    /// The 500 of a failed record write: the generic 500 <c>detail</c> plus the <c>writeOutcome</c> member. Each time
    /// the result is written, its <c>detail</c> is rebuilt as <see cref="UnexpectedDetailFor"/> of the trace id
    /// written to the same body's <c>traceId</c>. The status comes from the failure's classification;
    /// <paramref name="outcome"/> only reports what is known about the data.
    /// </summary>
    /// <param name="outcome">What is known about the stored data.</param>
    public static IResult UnexpectedWrite(WriteOutcome outcome) => new ProblemResult(
        CreateWriteProblem(StatusCodes.Status500InternalServerError, UnexpectedDetail, outcome),
        correlateUnexpectedDetail: true);

    /// <summary>
    /// The 503 of a failed record write: <see cref="UnavailableDetail"/> plus the <c>writeOutcome</c> member. The
    /// status comes from the failure's classification; <paramref name="outcome"/> only reports what is known about
    /// the data.
    /// </summary>
    /// <param name="outcome">What is known about the stored data.</param>
    public static IResult UnavailableWrite(WriteOutcome outcome) =>
        new ProblemResult(CreateWriteProblem(StatusCodes.Status503ServiceUnavailable, UnavailableDetail, outcome));

    /// <summary>
    /// Writes the status-code page of an empty error response produced inside the pipeline, such as a route miss 404,
    /// a 405 or a routing 415: the fixed ProblemDetails of the response's status, with the status's default
    /// <c>type</c> and <c>title</c>, its fixed <c>detail</c> and <c>traceId</c>, as <c>application/problem+json</c>
    /// whatever the request's <c>Accept</c> header. <c>Program.cs</c> passes it as
    /// <c>app.UseStatusCodePages(ApiProblems.WriteStatusCodePageAsync)</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It writes through the same result as every other error body and never through <c>IProblemDetailsService</c>:
    /// the default status-code page goes through that service, whose default writer declines an <c>Accept</c> header
    /// that admits neither <c>application/json</c> nor <c>application/problem+json</c>, and then falls back to a
    /// <c>text/plain</c> body without <c>traceId</c>.
    /// </para>
    /// <para>
    /// Only the response status is read, never an exception. No header is cleared or rewritten, so the status and the
    /// <c>Allow</c> header of a 405 reach the client unchanged. Status-code pages act only on a 4xx or 5xx response
    /// with no body, so the <c>/health</c> 503, which has its <c>{"status":"unhealthy"}</c> body, is never rewritten.
    /// Nothing is written for a status outside 400 to 599, once the response has started, or after the client
    /// aborted the request, which receives no body.
    /// </para>
    /// </remarks>
    /// <param name="context">The status-code pages context of the current request.</param>
    /// <returns>A task that completes when the body is written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public static Task WriteStatusCodePageAsync(StatusCodeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var httpContext = context.HttpContext;
        var status = httpContext.Response.StatusCode;
        if (status is < 400 or > 599
            || httpContext.Response.HasStarted
            || httpContext.RequestAborted.IsCancellationRequested)
        {
            return Task.CompletedTask;
        }

        // FixedDetail and the flag together are DefaultDetail: a generic 500 gets its trace id when it is written.
        var result = new ProblemResult(
            CreateProblem(status, FixedDetail(status)),
            correlateUnexpectedDetail: IsUnexpectedStatus(status));
        return result.ExecuteAsync(httpContext);
    }

    /// <summary>
    /// Shapes a body another framework writer produces through the ProblemDetails service, such as the Development
    /// exception page. Status-code pages do not come here: <see cref="WriteStatusCodePageAsync"/> writes them.
    /// <c>Program.cs</c> passes it as <c>AddProblemDetails(o =&gt; o.CustomizeProblemDetails = ApiProblems.Customize)</c>.
    /// </summary>
    /// <remarks>
    /// The body keeps only fixed texts and <c>traceId</c>: <c>type</c> and <c>title</c> become the status defaults,
    /// <c>detail</c> the fixed text for the status, which for a 5xx other than 503 is <see cref="UnexpectedDetailFor"/>
    /// of the <c>traceId</c> written, and every extension other than <c>traceId</c> (such as <c>exception</c> or
    /// <c>errors</c>) is removed. <see cref="ProblemDetailsContext.Exception"/> is never read.
    /// </remarks>
    /// <param name="context">The framework's ProblemDetails context.</param>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public static void Customize(ProblemDetailsContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var problem = context.ProblemDetails;
        var status = problem.Status ?? context.HttpContext.Response.StatusCode;
        problem.Status = status;

        // One read, so a 500's detail quotes exactly the trace id the body carries.
        var traceId = CurrentTraceId(context.HttpContext);

        // Overwritten rather than filled only when null: a framework writer such as the Development exception page
        // puts the exception type in title and its message in detail, and the redaction policy allows neither in any
        // error body.
        problem.Type = DefaultType(status);
        problem.Title = DefaultTitle(status);
        problem.Detail = DefaultDetail(status, traceId);

        foreach (var key in problem.Extensions.Keys.ToArray())
        {
            if (!string.Equals(key, TraceIdKey, StringComparison.Ordinal))
            {
                problem.Extensions.Remove(key);
            }
        }

        problem.Extensions[TraceIdKey] = traceId;
    }

    /// <summary>
    /// Creates a plain ProblemDetails with <paramref name="status"/>, <paramref name="detail"/> and the status's
    /// default <c>type</c> and <c>title</c>; <c>traceId</c> is added when it is written.
    /// </summary>
    /// <param name="status">A 4xx or 5xx status.</param>
    /// <param name="detail">A fixed text; never a submitted value or an exception message.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="status"/> is not from 400 to 599.</exception>
    /// <exception cref="ArgumentException"><paramref name="detail"/> is <see langword="null"/>, empty or whitespace.</exception>
    internal static ProblemDetails CreateProblem(int status, string detail)
    {
        ThrowIfNotErrorStatus(status);
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);

        return new ProblemDetails
        {
            Type = DefaultType(status),
            Title = DefaultTitle(status),
            Status = status,
            Detail = detail,
        };
    }

    /// <summary>
    /// Creates the <see cref="WriteProblemDetails"/> of a failed record write, with the status's default <c>type</c>
    /// and <c>title</c>; <c>traceId</c> is added when it is written.
    /// </summary>
    /// <param name="status">500 or 503, the only statuses whose schema is <see cref="WriteProblemDetails"/>.</param>
    /// <param name="detail">A fixed text; never a submitted value or an exception message.</param>
    /// <param name="outcome">What is known about the stored data.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="status"/> is neither 500 nor 503.</exception>
    /// <exception cref="ArgumentException"><paramref name="detail"/> is <see langword="null"/>, empty or whitespace.</exception>
    internal static WriteProblemDetails CreateWriteProblem(int status, string detail, WriteOutcome outcome)
    {
        if (status is not (StatusCodes.Status500InternalServerError or StatusCodes.Status503ServiceUnavailable))
        {
            throw new ArgumentOutOfRangeException(nameof(status), "A write problem is a 500 or a 503.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(detail);

        return new WriteProblemDetails(outcome)
        {
            Type = DefaultType(status),
            Title = DefaultTitle(status),
            Status = status,
            Detail = detail,
        };
    }

    private static ProblemResult Create(int status, string detail) => new(CreateProblem(status, detail));

    private static HttpValidationProblemDetails CreateValidationProblem(Dictionary<string, string[]> errors) =>
        new(errors)
        {
            Type = DefaultType(StatusCodes.Status400BadRequest),
            Status = StatusCodes.Status400BadRequest,
            Detail = ValidationFailedDetail,
        };

    private static void ThrowIfNotErrorStatus(int status)
    {
        if (status is < 400 or > 599)
        {
            throw new ArgumentOutOfRangeException(nameof(status), "An error body needs a 4xx or 5xx status.");
        }
    }

    private static string CurrentTraceId(HttpContext context) => Activity.Current?.Id ?? context.TraceIdentifier;

    // The values ASP.NET Core's own ProblemDetails defaults give each status, so bodies written here and bodies the
    // framework writes link the same RFC section; any other status links the client- or server-error section.
    private static string DefaultType(int status) => status switch
    {
        400 => "https://tools.ietf.org/html/rfc9110#section-15.5.1",
        401 => "https://tools.ietf.org/html/rfc9110#section-15.5.2",
        403 => "https://tools.ietf.org/html/rfc9110#section-15.5.4",
        404 => "https://tools.ietf.org/html/rfc9110#section-15.5.5",
        405 => "https://tools.ietf.org/html/rfc9110#section-15.5.6",
        406 => "https://tools.ietf.org/html/rfc9110#section-15.5.7",
        407 => "https://tools.ietf.org/html/rfc9110#section-15.5.8",
        408 => "https://tools.ietf.org/html/rfc9110#section-15.5.9",
        409 => "https://tools.ietf.org/html/rfc9110#section-15.5.10",
        410 => "https://tools.ietf.org/html/rfc9110#section-15.5.11",
        411 => "https://tools.ietf.org/html/rfc9110#section-15.5.12",
        412 => "https://tools.ietf.org/html/rfc9110#section-15.5.13",
        413 => "https://tools.ietf.org/html/rfc9110#section-15.5.14",
        414 => "https://tools.ietf.org/html/rfc9110#section-15.5.15",
        415 => "https://tools.ietf.org/html/rfc9110#section-15.5.16",
        416 => "https://tools.ietf.org/html/rfc9110#section-15.5.17",
        417 => "https://tools.ietf.org/html/rfc9110#section-15.5.18",
        421 => "https://tools.ietf.org/html/rfc9110#section-15.5.20",
        422 => "https://tools.ietf.org/html/rfc4918#section-11.2",
        426 => "https://tools.ietf.org/html/rfc9110#section-15.5.22",
        500 => "https://tools.ietf.org/html/rfc9110#section-15.6.1",
        501 => "https://tools.ietf.org/html/rfc9110#section-15.6.2",
        502 => "https://tools.ietf.org/html/rfc9110#section-15.6.3",
        503 => "https://tools.ietf.org/html/rfc9110#section-15.6.4",
        504 => "https://tools.ietf.org/html/rfc9110#section-15.6.5",
        505 => "https://tools.ietf.org/html/rfc9110#section-15.6.6",
        < 500 => "https://tools.ietf.org/html/rfc9110#section-15.5",
        _ => "https://tools.ietf.org/html/rfc9110#section-15.6",
    };

    private static string DefaultTitle(int status)
    {
        var phrase = ReasonPhrases.GetReasonPhrase(status);
        if (phrase.Length > 0)
        {
            return phrase;
        }

        return status < 500 ? "Client Error" : "Server Error";
    }

    // Whether a status is answered as the generic 500: every 5xx except 503, whose detail is the unavailable text.
    private static bool IsUnexpectedStatus(int status) => status is >= 500 and <= 599 and not 503;

    // The one detail mapping of a body built from its status alone: status-code pages and framework bodies shaped by
    // Customize. The generic 500 detail ends with the trace id the body carries; every other status has fixed text.
    private static string DefaultDetail(int status, string traceId) =>
        IsUnexpectedStatus(status) ? UnexpectedDetailFor(traceId) : FixedDetail(status);

    // The fixed text of each status; for the generic 500 it is the sentence the trace id is appended to.
    private static string FixedDetail(int status) => status switch
    {
        400 => InvalidRequestDetail,
        404 => ResourceNotFoundDetail,
        405 => MethodNotAllowedDetail,
        413 => RequestTooLargeDetail,
        415 => UnsupportedMediaTypeDetail,
        503 => UnavailableDetail,
        < 500 => InvalidRequestDetail,
        _ => UnexpectedDetail,
    };

    /// <summary>
    /// The one <see cref="IResult"/> every factory returns: writes its ProblemDetails as
    /// <c>application/problem+json</c>, serialised by runtime type so the <c>errors</c> of an
    /// <see cref="HttpValidationProblemDetails"/> and the <c>writeOutcome</c> of a <see cref="WriteProblemDetails"/>
    /// are written.
    /// </summary>
    /// <remarks>
    /// It deliberately bypasses <c>IProblemDetailsService</c>, whose default writer can decline on an <c>Accept</c>
    /// header and serialises by the declared type. Each execution reads the trace id once and writes it to
    /// <c>traceId</c>; a generic 500 also rebuilds its <c>detail</c> from <see cref="UnexpectedDetail"/> and that
    /// same trace id, so a result written again quotes the trace id of the request it is written to.
    /// </remarks>
    internal sealed class ProblemResult :
        IResult,
        IStatusCodeHttpResult,
        IValueHttpResult,
        IValueHttpResult<ProblemDetails>,
        IContentTypeHttpResult
    {
        private readonly bool _correlateUnexpectedDetail;

        /// <summary>
        /// Wraps <paramref name="problem"/>, whose status must be a 4xx or 5xx status, and writes its
        /// <c>detail</c> as it stands.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="problem"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="problem"/> has no 4xx or 5xx status.</exception>
        public ProblemResult(ProblemDetails problem)
            : this(problem, correlateUnexpectedDetail: false)
        {
        }

        /// <summary>Wraps <paramref name="problem"/>, whose status must be a 4xx or 5xx status.</summary>
        /// <param name="problem">The body.</param>
        /// <param name="correlateUnexpectedDetail">
        /// <see langword="true"/> for a generic 500, a 5xx other than 503: every execution sets its <c>detail</c> to
        /// <see cref="UnexpectedDetailFor"/> of the trace id it writes to <c>traceId</c>.
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="problem"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="problem"/> has no 4xx or 5xx status, or <paramref name="correlateUnexpectedDetail"/> is set
        /// for a status that is not a generic 500.
        /// </exception>
        public ProblemResult(ProblemDetails problem, bool correlateUnexpectedDetail)
        {
            ArgumentNullException.ThrowIfNull(problem);
            if (problem.Status is not (>= 400 and <= 599))
            {
                throw new ArgumentException("An error body needs a 4xx or 5xx status.", nameof(problem));
            }

            if (correlateUnexpectedDetail && !IsUnexpectedStatus(problem.Status.Value))
            {
                throw new ArgumentException(
                    "Only a 5xx other than 503 carries the generic 500 detail.",
                    nameof(correlateUnexpectedDetail));
            }

            Value = problem;
            StatusCode = problem.Status.Value;
            _correlateUnexpectedDetail = correlateUnexpectedDetail;
        }

        /// <summary>The body this result writes.</summary>
        public ProblemDetails Value { get; }

        /// <summary>The response status, equal to the body's <see cref="ProblemDetails.Status"/>.</summary>
        public int StatusCode { get; }

        /// <summary>Always <see cref="ProblemContentType"/>.</summary>
        public string ContentType => ProblemContentType;

        int? IStatusCodeHttpResult.StatusCode => StatusCode;

        object? IValueHttpResult.Value => Value;

        /// <inheritdoc />
        public Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);

            // Headers and status are already sent, so no body can be added.
            if (httpContext.Response.HasStarted)
            {
                return Task.CompletedTask;
            }

            // One read, so a 500's detail quotes exactly the trace id the body carries.
            var traceId = CurrentTraceId(httpContext);

            var problem = Value;
            problem.Status = StatusCode;
            problem.Type ??= DefaultType(StatusCode);
            problem.Title ??= DefaultTitle(StatusCode);
            if (_correlateUnexpectedDetail)
            {
                problem.Detail = UnexpectedDetailFor(traceId);
            }

            problem.Extensions[TraceIdKey] = traceId;

            httpContext.Response.StatusCode = StatusCode;
            return httpContext.Response.WriteAsJsonAsync(
                problem,
                problem.GetType(),
                SerializerOptions,
                ProblemContentType,
                httpContext.RequestAborted);
        }
    }
}

/// <summary>The write a 403 reports as denied by the database permission check.</summary>
public enum ForbiddenAction
{
    /// <summary>Creating a record: <c>POST</c> on records.</summary>
    Insert,

    /// <summary>Updating a record: <c>PUT</c> on a record.</summary>
    Update,

    /// <summary>Soft-deleting a record: <c>DELETE</c> on a record.</summary>
    Delete,
}

/// <summary>
/// The ProblemDetails of a failed record write: a <c>POST</c>, <c>PUT</c> or <c>DELETE</c> on records that ends in
/// 500 or 503. It adds <c>writeOutcome</c>, what is known about the stored data, so the client can tell whether an
/// automatic resend is safe (only <c>notWritten</c>).
/// </summary>
/// <remarks>
/// <para>OpenAPI contract:</para>
/// <list type="bullet">
/// <item><description>
/// It is the schema of the 500 and 503 responses of the three record writes only. <c>RecordEndpoints</c> declares
/// <c>.Produces&lt;WriteProblemDetails&gt;(StatusCodes.Status500InternalServerError, "application/problem+json")</c>
/// and the same for 503, so the generated schema is named <c>WriteProblemDetails</c>.
/// </description></item>
/// <item><description>
/// <c>OpenApi/ScalarSchemaTransformer.cs</c> gives its <c>writeOutcome</c> property the string enum
/// <c>notWritten</c>, <c>unknown</c>, <c>written</c>.
/// </description></item>
/// <item><description>
/// Every other error response is <see cref="ProblemDetails"/>, or <see cref="HttpValidationProblemDetails"/> for a
/// field-level 400.
/// </description></item>
/// </list>
/// <para>
/// <c>writeOutcome</c> is a declared property, never an entry in <see cref="ProblemDetails.Extensions"/>, so the JSON
/// holds it once.
/// </para>
/// </remarks>
public sealed class WriteProblemDetails : ProblemDetails
{
    /// <summary>Creates a body whose outcome is <c>unknown</c>; used when deserialising.</summary>
    public WriteProblemDetails()
    {
    }

    /// <summary>Creates a body reporting <paramref name="outcome"/>.</summary>
    /// <param name="outcome">
    /// What is known about the stored data. A value outside the enum is reported as <c>unknown</c>, never thrown,
    /// because this runs while an error response is being written and <c>unknown</c> is never resent automatically.
    /// </param>
    public WriteProblemDetails(WriteOutcome outcome)
    {
        Outcome = Enum.IsDefined(outcome) ? outcome.ToWireValue() : WriteOutcomeExtensions.WireUnknown;
    }

    /// <summary>
    /// The <c>writeOutcome</c> member: <see cref="WriteOutcomeExtensions.WireNotWritten"/>,
    /// <see cref="WriteOutcomeExtensions.WireUnknown"/> or <see cref="WriteOutcomeExtensions.WireWritten"/>.
    /// </summary>
    /// <remarks>Named <c>Outcome</c> so it does not clash with the <see cref="WriteOutcome"/> enum.</remarks>
    [JsonPropertyName("writeOutcome")]
    public string Outcome { get; set; } = WriteOutcomeExtensions.WireUnknown;
}
