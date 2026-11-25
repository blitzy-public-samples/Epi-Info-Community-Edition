using EpiInfo.Api.Contracts;
using EpiInfo.Api.Errors;
using EpiInfo.Api.Hosting;
using EpiInfo.Api.Services;
using EpiInfo.Domain.Events;
using Microsoft.AspNetCore.Mvc;

namespace EpiInfo.Api.Endpoints;

/// <summary>
/// Maps <c>POST /api/forms/{viewId}/checkcode/field-event</c>, the stateless check-code route: the browser posts one
/// field event with the editor's current values and receives the effects of the matching block as data
/// (<c>enabled</c>, <c>disabled</c>, <c>cleared</c>, <c>assigned</c>, <c>dialogs</c>, <c>diagnostics</c>), which it
/// applies itself.
/// </summary>
/// <remarks>
/// <para>
/// The endpoint is a thin adapter. Event-name parsing, field checks, value typing, the program read and the engine
/// call all belong to <see cref="FieldEventService"/>, which also produces every outcome: 200 with a
/// <see cref="FieldEventResponse"/>, a field-level 400 under <c>errors["event"]</c>, <c>errors["fieldName"]</c> or a
/// <c>values</c> key, a request-level 400, and the 404 of an unknown form. A non-integer <c>viewId</c> matches no route
/// because of its <c>int</c> constraint, so status-code pages answer it with the 404 ProblemDetails.
/// </para>
/// <para>
/// <see cref="JsonBodyMetadata"/> places the route under <c>JsonContentTypeMiddleware</c>, which answers 415 for any
/// media type other than <c>application/json</c> with an optional <c>charset=utf-8</c>, <c>+json</c> types and a
/// missing <c>Content-Type</c> included, and the request-level 400 for an empty body. The 1,048,576-byte body limit
/// gives 413, and the exception handler gives 400 for malformed JSON, 503 for an unavailable store and 500 otherwise.
/// </para>
/// <para>
/// The route never writes, so it carries no <c>WriteEndpointMetadata</c> and its 500 and 503 bodies are plain
/// ProblemDetails with no <c>writeOutcome</c>. It keeps no state, caches nothing and logs nothing here, so no field
/// value can reach a log from this file.
/// </para>
/// </remarks>
public static class CheckCodeEndpoints
{
    private const string FieldEventPattern = "/api/forms/{viewId:int}/checkcode/field-event";

    private const string JsonContentType = "application/json";

    /// <summary>Maps the field-event route.</summary>
    /// <param name="app">The route builder of the application.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> is <see langword="null"/>.</exception>
    public static IEndpointRouteBuilder MapCheckCodeEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost(FieldEventPattern, RunFieldEventAsync)
            .WithTags("CheckCode")
            .WithName("RunFieldEvent")
            .WithSummary("Runs one check-code field event on the posted values. Stateless; nothing is written.")
            .RequireJsonBody()
            .Accepts<FieldEventRequest>(JsonContentType)
            .Produces<FieldEventResponse>(StatusCodes.Status200OK, JsonContentType)
            .ProducesValidationProblem(StatusCodes.Status400BadRequest, ApiProblems.ProblemContentType)
            .ProducesProblem(StatusCodes.Status404NotFound, ApiProblems.ProblemContentType)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge, ApiProblems.ProblemContentType)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType, ApiProblems.ProblemContentType)
            .ProducesProblem(StatusCodes.Status500InternalServerError, ApiProblems.ProblemContentType)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable, ApiProblems.ProblemContentType);

        return app;
    }

    // The body is nullable so a JSON null body reaches the service's request-level 400 rather than a binder message.
    // The service is marked [FromServices] so build-time OpenAPI generation never relies on service-collection
    // inference to tell it apart from a body parameter.
    private static Task<IResult> RunFieldEventAsync(
        [FromRoute] int viewId,
        [FromBody] FieldEventRequest? request,
        [FromServices] FieldEventService service,
        CancellationToken cancellationToken) =>
        service.RunAsync(viewId, request, cancellationToken);
}
