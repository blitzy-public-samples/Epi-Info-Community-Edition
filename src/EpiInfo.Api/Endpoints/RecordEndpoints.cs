using EpiInfo.Api.Contracts;
using EpiInfo.Api.Errors;
using EpiInfo.Api.Hosting;
using EpiInfo.Api.Services;
using EpiInfo.Domain.Records;
using Microsoft.AspNetCore.Mvc;

namespace EpiInfo.Api.Endpoints;

/// <summary>
/// Maps every record route under <c>/api/forms/{viewId}/records</c>: the records list, the selected-set count, the
/// record at a position, the record by id and its navigation position, create, update, soft delete and the Find
/// search. Together they replace the legacy Enter navigation toolstrip, record-number box, save, mark-as-deleted and
/// Find menu, and the record status display.
/// </summary>
/// <remarks>
/// <para>
/// <b>Thin adapters.</b> Each handler coerces its string inputs through <see cref="RequestParameters"/>, calls
/// <see cref="RecordService"/> and returns its result. The set predicate, positions, validation, the save path and the
/// search rules all belong to the service and the Domain rules it calls. A coercion failure is returned at once, before
/// any service call, so a bad parameter is reported without a database read and ahead of the form lookup.
/// <c>includeDeleted</c>, <c>page</c> and <c>pageSize</c> bind as the array of every occurrence sent, because binding
/// a repeated key to one string joins only its non-empty occurrences: an empty or repeated occurrence therefore reaches
/// the coercion instead of being dropped or merged.
/// </para>
/// <para>
/// <b>Routes.</b> <c>{viewId}</c> carries the <c>int</c> constraint, so a non-integer form id matches no route and
/// status-code pages answer it with the 404 ProblemDetails. <c>{position}</c> deliberately has no constraint: it binds
/// as a string so <c>at/abc</c> reaches the coercion 400 under <c>errors["position"]</c> rather than a route miss, and
/// any integer outside 1..<c>count</c> is the service's 404. Legacy shortcuts such as <c>*</c> or <c>0</c> for the
/// last record are not carried; the client navigator owns "last". <c>{globalRecordId}</c> has no constraint either:
/// the literal segments <c>count</c>, <c>at</c>, <c>search</c> and the trailing <c>position</c> outrank it by routing
/// precedence, and an unknown or malformed id is the service's 404. No other verb is mapped, so <c>PATCH</c> on a
/// record stays the routing 405.
/// </para>
/// <para>
/// <b>Bodies.</b> Create, update and search carry <see cref="JsonBodyMetadata"/>, under which
/// <c>JsonContentTypeMiddleware</c> answers 415 for any media type other than <c>application/json</c> and the
/// request-level 400 for an empty body; the body limit gives 413. Each handler reads its body through
/// <see cref="JsonRequestBody"/> rather than by parameter binding, so the whole body is parsed before its kind is
/// judged: content that is not valid JSON surfaces as a <c>BadHttpRequestException</c> that the exception handler maps
/// to "malformed JSON", a valid value that is not an object is answered with "request body must be a JSON object"
/// before any service call, and a JSON <c>null</c> body reaches the service's request-level 400. Each body is declared
/// by its <c>Accepts</c> metadata alone, and the OpenAPI document publishes each body as required and non-null, and
/// each body route's 400 as either shape: the request-level ProblemDetails or the field-level
/// <see cref="HttpValidationProblemDetails"/>. The query routes' 400 is the field-level shape only.
/// </para>
/// <para>
/// <b>Write failures.</b> Create, update and delete declare the 403 ProblemDetails the service returns when the
/// database permission check denies the write. They carry <see cref="WriteEndpointMetadata"/>, from which the exception
/// handler adds <c>writeOutcome</c> to their 500 and 503 bodies, documented as <see cref="WriteProblemDetails"/>.
/// Search writes nothing and carries neither, so it has no 403 and its 500 and 503 are plain ProblemDetails.
/// </para>
/// <para>
/// The handlers are named static methods with explicit binding sources, so the build-time OpenAPI document is
/// deterministic and independent of the service collection, and the parameter names <c>q</c>,
/// <c>includeDeleted</c>, <c>page</c>, <c>pageSize</c> and <c>position</c> are the ones the parameter schema
/// transformer rewrites to their wire types. Nothing here logs, caches or keeps state, so no field value or query
/// string can reach a log from this file.
/// </para>
/// </remarks>
public static class RecordEndpoints
{
    private const string GroupPattern = "/api/forms/{viewId:int}/records";

    private const string JsonContentType = "application/json";

    /// <summary>Maps the record routes, tagged <c>Records</c> in the OpenAPI document.</summary>
    /// <param name="app">The route builder of the application.</param>
    /// <returns><paramref name="app"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> is <see langword="null"/>.</exception>
    public static IEndpointRouteBuilder MapRecordEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup(GroupPattern).WithTags("Records");

        group.MapGet(string.Empty, ListRecordsAsync)
            .WithName("ListRecords")
            .WithSummary("List one page of a form's records, optionally filtered by the grid columns.")
            .Produces<RecordPage>(StatusCodes.Status200OK, JsonContentType)
            .ProducesValidationProblem(StatusCodes.Status400BadRequest, ApiProblems.ProblemContentType)
            .ProducesReadErrors();

        group.MapGet("/count", CountRecordsAsync)
            .WithName("CountRecords")
            .WithSummary("Count the records of the selected set.")
            .Produces<RecordCountResponse>(StatusCodes.Status200OK, JsonContentType)
            .ProducesValidationProblem(StatusCodes.Status400BadRequest, ApiProblems.ProblemContentType)
            .ProducesReadErrors();

        group.MapGet("/at/{position}", GetRecordAtAsync)
            .WithName("GetRecordAt")
            .WithSummary("Open the record at a 1-based position of the selected set, in UniqueKey order.")
            .Produces<RecordDto>(StatusCodes.Status200OK, JsonContentType)
            .ProducesValidationProblem(StatusCodes.Status400BadRequest, ApiProblems.ProblemContentType)
            .ProducesReadErrors();

        group.MapGet("/{globalRecordId}", GetRecordAsync)
            .WithName("GetRecord")
            .WithSummary("Open a record by its GlobalRecordId; deleted records are returned.")
            .Produces<RecordDto>(StatusCodes.Status200OK, JsonContentType)
            .ProducesReadErrors();

        group.MapGet("/{globalRecordId}/position", GetRecordPositionAsync)
            .WithName("GetRecordPosition")
            .WithSummary("Place a record in the selected set; position is null when the record is outside it.")
            .Produces<NavigationPosition>(StatusCodes.Status200OK, JsonContentType)
            .ProducesValidationProblem(StatusCodes.Status400BadRequest, ApiProblems.ProblemContentType)
            .ProducesReadErrors();

        group.MapPost(string.Empty, CreateRecordAsync)
            .WithName("CreateRecord")
            .WithSummary("Create a record from the submitted field values.")
            .WithMetadata(WriteEndpointMetadata.Insert)
            .RequireJsonBody()
            .Accepts<RecordWriteRequest>(JsonContentType)
            .Produces<RecordDto>(StatusCodes.Status201Created, JsonContentType)
            .ProducesValidationProblem(StatusCodes.Status400BadRequest, ApiProblems.ProblemContentType)
            .ProducesProblem(StatusCodes.Status404NotFound, ApiProblems.ProblemContentType)
            .ProducesBodyErrors()
            .ProducesWriteFailures();

        group.MapPut("/{globalRecordId}", UpdateRecordAsync)
            .WithName("UpdateRecord")
            .WithSummary("Update a record with the submitted field values; absent fields are unchanged.")
            .WithMetadata(WriteEndpointMetadata.Update)
            .RequireJsonBody()
            .Accepts<RecordWriteRequest>(JsonContentType)
            .Produces<RecordDto>(StatusCodes.Status200OK, JsonContentType)
            .ProducesValidationProblem(StatusCodes.Status400BadRequest, ApiProblems.ProblemContentType)
            .ProducesProblem(StatusCodes.Status404NotFound, ApiProblems.ProblemContentType)
            .ProducesProblem(StatusCodes.Status409Conflict, ApiProblems.ProblemContentType)
            .ProducesBodyErrors()
            .ProducesWriteFailures();

        group.MapDelete("/{globalRecordId}", DeleteRecordAsync)
            .WithName("DeleteRecord")
            .WithSummary("Soft-delete a record: its base row's RECSTATUS becomes 0. Repeating it changes nothing.")
            .WithMetadata(WriteEndpointMetadata.SoftDelete)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound, ApiProblems.ProblemContentType)
            .ProducesWriteFailures();

        group.MapPost("/search", SearchRecordsAsync)
            .WithName("SearchRecords")
            .WithSummary("Find records with the legacy Find criteria syntax.")
            .RequireJsonBody()
            .Accepts<SearchRequest>(JsonContentType)
            .Produces<SearchResponse>(StatusCodes.Status200OK, JsonContentType)
            .ProducesValidationProblem(StatusCodes.Status400BadRequest, ApiProblems.ProblemContentType)
            .ProducesProblem(StatusCodes.Status404NotFound, ApiProblems.ProblemContentType)
            .ProducesBodyErrors()
            .ProducesProblem(StatusCodes.Status500InternalServerError, ApiProblems.ProblemContentType)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable, ApiProblems.ProblemContentType);

        return app;
    }

    /// <summary>Handles <c>GET /api/forms/{viewId}/records</c>.</summary>
    /// <param name="viewId">The form's <c>metaViews.ViewId</c>, already an integer by its route constraint.</param>
    /// <param name="q">
    /// The grid filter; absent or whitespace means no filter. Any other filter, whatever its length, is a literal,
    /// case-insensitive "contains" over the grid columns.
    /// </param>
    /// <param name="includeDeleted">
    /// Every sent occurrence of the deleted toggle, in order and as sent; none means <c>false</c>.
    /// </param>
    /// <param name="page">Every sent occurrence of the 1-based page number, in order and as sent; none means 1.</param>
    /// <param name="pageSize">Every sent occurrence of the page size, in order and as sent; none means 50.</param>
    /// <param name="service">The record service.</param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>200 with the page, the coercion 400, or the service's 404 for an unknown form.</returns>
    private static Task<IResult> ListRecordsAsync(
        [FromRoute] int viewId,
        [FromQuery(Name = RequestParameters.Query)] string? q,
        [FromQuery(Name = RequestParameters.IncludeDeleted)] string[]? includeDeleted,
        [FromQuery(Name = RequestParameters.Page)] string[]? page,
        [FromQuery(Name = RequestParameters.PageSize)] string[]? pageSize,
        [FromServices] RecordService service,
        CancellationToken cancellationToken)
    {
        if (!RequestParameters.TryParseListQuery(q, includeDeleted, page, pageSize, out var query, out var problem))
        {
            return Task.FromResult(problem);
        }

        return service.ListAsync(viewId, query, cancellationToken);
    }

    /// <summary>Handles <c>GET /api/forms/{viewId}/records/count</c>.</summary>
    /// <param name="viewId">The form's <c>metaViews.ViewId</c>, already an integer by its route constraint.</param>
    /// <param name="includeDeleted">
    /// Every sent occurrence of the deleted toggle, in order and as sent; none means <c>false</c>.
    /// </param>
    /// <param name="service">The record service.</param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>200 with the count, the coercion 400, or the service's 404 for an unknown form.</returns>
    private static Task<IResult> CountRecordsAsync(
        [FromRoute] int viewId,
        [FromQuery(Name = RequestParameters.IncludeDeleted)] string[]? includeDeleted,
        [FromServices] RecordService service,
        CancellationToken cancellationToken)
    {
        if (!RequestParameters.TryParseIncludeDeleted(includeDeleted, out var parsedIncludeDeleted, out var problem))
        {
            return Task.FromResult(problem);
        }

        return service.CountAsync(viewId, parsedIncludeDeleted, cancellationToken);
    }

    /// <summary>Handles <c>GET /api/forms/{viewId}/records/at/{position}</c>.</summary>
    /// <param name="viewId">The form's <c>metaViews.ViewId</c>, already an integer by its route constraint.</param>
    /// <param name="position">The 1-based position as sent, coerced here rather than by a route constraint.</param>
    /// <param name="includeDeleted">
    /// Every sent occurrence of the deleted toggle, in order and as sent; none means <c>false</c>.
    /// </param>
    /// <param name="service">The record service.</param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>
    /// 200 with the record, one coercion 400 naming every bad parameter, or the service's 404 for an unknown form or a
    /// position outside the selected set.
    /// </returns>
    private static Task<IResult> GetRecordAtAsync(
        [FromRoute] int viewId,
        [FromRoute(Name = RequestParameters.Position)] string position,
        [FromQuery(Name = RequestParameters.IncludeDeleted)] string[]? includeDeleted,
        [FromServices] RecordService service,
        CancellationToken cancellationToken)
    {
        if (!RequestParameters.TryParsePositionRequest(
                position,
                includeDeleted,
                out var parsedPosition,
                out var parsedIncludeDeleted,
                out var problem))
        {
            return Task.FromResult(problem);
        }

        return service.GetAtAsync(viewId, parsedPosition, parsedIncludeDeleted, cancellationToken);
    }

    /// <summary>Handles <c>GET /api/forms/{viewId}/records/{globalRecordId}</c>.</summary>
    /// <param name="viewId">The form's <c>metaViews.ViewId</c>, already an integer by its route constraint.</param>
    /// <param name="globalRecordId">The record's <c>GlobalRecordId</c>.</param>
    /// <param name="service">The record service.</param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>200 with the record, deleted or not, or the service's 404 for an unknown form or record.</returns>
    private static Task<IResult> GetRecordAsync(
        [FromRoute] int viewId,
        [FromRoute] string globalRecordId,
        [FromServices] RecordService service,
        CancellationToken cancellationToken) =>
        service.GetByIdAsync(viewId, globalRecordId, cancellationToken);

    /// <summary>Handles <c>GET /api/forms/{viewId}/records/{globalRecordId}/position</c>.</summary>
    /// <param name="viewId">The form's <c>metaViews.ViewId</c>, already an integer by its route constraint.</param>
    /// <param name="globalRecordId">The record's <c>GlobalRecordId</c>.</param>
    /// <param name="includeDeleted">
    /// Every sent occurrence of the deleted toggle, in order and as sent; none means <c>false</c>.
    /// </param>
    /// <param name="service">The record service.</param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>
    /// 200 with the selected-set position, the coercion 400, or the service's 404 for an unknown form or record.
    /// </returns>
    private static Task<IResult> GetRecordPositionAsync(
        [FromRoute] int viewId,
        [FromRoute] string globalRecordId,
        [FromQuery(Name = RequestParameters.IncludeDeleted)] string[]? includeDeleted,
        [FromServices] RecordService service,
        CancellationToken cancellationToken)
    {
        if (!RequestParameters.TryParseIncludeDeleted(includeDeleted, out var parsedIncludeDeleted, out var problem))
        {
            return Task.FromResult(problem);
        }

        return service.GetNavigationPositionAsync(viewId, globalRecordId, parsedIncludeDeleted, cancellationToken);
    }

    /// <summary>Handles <c>POST /api/forms/{viewId}/records</c>.</summary>
    /// <param name="httpContext">
    /// The current request, whose body is read as a <see cref="RecordWriteRequest"/> and on which the service records
    /// the write outcome.
    /// </param>
    /// <param name="viewId">The form's <c>metaViews.ViewId</c>, already an integer by its route constraint.</param>
    /// <param name="service">The record service.</param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>
    /// 201 with the new record and its <c>Location</c>, the request-level 400 for a body that is not a JSON object, or
    /// the service's 400, 403 or 404.
    /// </returns>
    private static async Task<IResult> CreateRecordAsync(
        HttpContext httpContext,
        [FromRoute] int viewId,
        [FromServices] RecordService service,
        CancellationToken cancellationToken)
    {
        var body = await JsonRequestBody.ReadObjectAsync<RecordWriteRequest>(httpContext, cancellationToken);
        return body.Problem ?? await service.CreateAsync(httpContext, viewId, body.Value, cancellationToken);
    }

    /// <summary>Handles <c>PUT /api/forms/{viewId}/records/{globalRecordId}</c>.</summary>
    /// <param name="httpContext">
    /// The current request, whose body is read as a <see cref="RecordWriteRequest"/> and on which the service records
    /// the write outcome.
    /// </param>
    /// <param name="viewId">The form's <c>metaViews.ViewId</c>, already an integer by its route constraint.</param>
    /// <param name="globalRecordId">The record's <c>GlobalRecordId</c>.</param>
    /// <param name="service">The record service.</param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>
    /// 200 with the updated record, the request-level 400 for a body that is not a JSON object, or the service's 400,
    /// 403, 404 or 409.
    /// </returns>
    private static async Task<IResult> UpdateRecordAsync(
        HttpContext httpContext,
        [FromRoute] int viewId,
        [FromRoute] string globalRecordId,
        [FromServices] RecordService service,
        CancellationToken cancellationToken)
    {
        var body = await JsonRequestBody.ReadObjectAsync<RecordWriteRequest>(httpContext, cancellationToken);
        return body.Problem
            ?? await service.UpdateAsync(httpContext, viewId, globalRecordId, body.Value, cancellationToken);
    }

    /// <summary>Handles <c>DELETE /api/forms/{viewId}/records/{globalRecordId}</c>.</summary>
    /// <param name="httpContext">The current request, on which the service records the write outcome.</param>
    /// <param name="viewId">The form's <c>metaViews.ViewId</c>, already an integer by its route constraint.</param>
    /// <param name="globalRecordId">The record's <c>GlobalRecordId</c>.</param>
    /// <param name="service">The record service.</param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>204, also for a record already deleted, or the service's 403 or 404.</returns>
    private static Task<IResult> DeleteRecordAsync(
        HttpContext httpContext,
        [FromRoute] int viewId,
        [FromRoute] string globalRecordId,
        [FromServices] RecordService service,
        CancellationToken cancellationToken) =>
        service.DeleteAsync(httpContext, viewId, globalRecordId, cancellationToken);

    /// <summary>Handles <c>POST /api/forms/{viewId}/records/search</c>.</summary>
    /// <param name="httpContext">The current request, whose body is read as a <see cref="SearchRequest"/>.</param>
    /// <param name="viewId">The form's <c>metaViews.ViewId</c>, already an integer by its route constraint.</param>
    /// <param name="service">The record service.</param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>
    /// 200 with the matches, the request-level 400 for a body that is not a JSON object, or the service's 400 or 404.
    /// </returns>
    private static async Task<IResult> SearchRecordsAsync(
        HttpContext httpContext,
        [FromRoute] int viewId,
        [FromServices] RecordService service,
        CancellationToken cancellationToken)
    {
        var body = await JsonRequestBody.ReadObjectAsync<SearchRequest>(httpContext, cancellationToken);
        return body.Problem ?? await service.SearchAsync(viewId, body.Value, cancellationToken);
    }

    // 404 for an unknown form, record or position, and the plain 500 and 503 of a read.
    private static RouteHandlerBuilder ProducesReadErrors(this RouteHandlerBuilder builder) =>
        builder
            .ProducesProblem(StatusCodes.Status404NotFound, ApiProblems.ProblemContentType)
            .ProducesProblem(StatusCodes.Status500InternalServerError, ApiProblems.ProblemContentType)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable, ApiProblems.ProblemContentType);

    // The body limit's 413 and the media-type guard's 415, shared by the three body endpoints.
    private static RouteHandlerBuilder ProducesBodyErrors(this RouteHandlerBuilder builder) =>
        builder
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge, ApiProblems.ProblemContentType)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType, ApiProblems.ProblemContentType);

    // The permission check's 403, and a record write's 500 and 503, shared by create, update and delete and by no other
    // route. The 500 and 503 carry writeOutcome; ScalarSchemaTransformer repoints the same two responses to the
    // WriteProblemDetails component for any endpoint with WriteEndpointMetadata, so both declare one schema.
    private static RouteHandlerBuilder ProducesWriteFailures(this RouteHandlerBuilder builder) =>
        builder
            .ProducesProblem(StatusCodes.Status403Forbidden, ApiProblems.ProblemContentType)
            .Produces<WriteProblemDetails>(StatusCodes.Status500InternalServerError, ApiProblems.ProblemContentType)
            .Produces<WriteProblemDetails>(StatusCodes.Status503ServiceUnavailable, ApiProblems.ProblemContentType);
}
