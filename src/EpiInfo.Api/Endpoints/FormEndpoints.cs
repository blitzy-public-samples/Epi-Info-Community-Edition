using EpiInfo.Api.Services;
using EpiInfo.Domain.Forms;
using Microsoft.AspNetCore.Mvc;

namespace EpiInfo.Api.Endpoints;

/// <summary>
/// Maps the form routes <c>GET /api/forms</c> and <c>GET /api/forms/{viewId}</c>: the forms list and the form
/// definition the client renders every page from.
/// </summary>
/// <remarks>
/// <para>
/// The handlers only forward to <see cref="FormService"/>, which owns every rule: page order, field order, group
/// members, options, the check-code diagnostics of static analysis and the 404 for a form that is not in
/// <c>metaViews</c>. Neither route runs check code or writes anything.
/// </para>
/// <para>
/// <c>{viewId}</c> carries the <c>int</c> route constraint, so a non-integer or out-of-range form id matches no route
/// and status-code pages answer it with the 404 ProblemDetails; no coercion error can arise from it. Store failures
/// propagate to the API exception handler, which maps an unavailable database to 503 and anything else to 500.
/// </para>
/// <para>
/// The handlers are named static methods with explicit binding sources, so the OpenAPI document generated at build
/// time is deterministic and does not depend on what the service collection holds.
/// </para>
/// </remarks>
public static class FormEndpoints
{
    /// <summary>Maps the form routes under <c>/api/forms</c>, tagged <c>Forms</c> in the OpenAPI document.</summary>
    /// <param name="app">The route builder of the application.</param>
    /// <returns><paramref name="app"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> is <see langword="null"/>.</exception>
    public static IEndpointRouteBuilder MapFormEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/forms").WithTags("Forms");

        group.MapGet(string.Empty, ListFormsAsync)
            .WithName("ListForms")
            .WithSummary("List the forms of the connected project.")
            .Produces<IReadOnlyList<FormSummary>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/{viewId:int}", GetFormDefinitionAsync)
            .WithName("GetFormDefinition")
            .WithSummary("Get a form's definition: its pages, their fields and the unsupported check code.")
            .Produces<FormDefinition>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return app;
    }

    /// <summary>Handles <c>GET /api/forms</c>.</summary>
    /// <param name="service">The form service.</param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>200 with one summary per form.</returns>
    private static Task<IResult> ListFormsAsync(
        [FromServices] FormService service,
        CancellationToken cancellationToken) =>
        service.ListAsync(cancellationToken);

    /// <summary>Handles <c>GET /api/forms/{viewId}</c>.</summary>
    /// <param name="viewId">The form's <c>metaViews.ViewId</c>, already an integer by its route constraint.</param>
    /// <param name="service">The form service.</param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>200 with the form definition, or the 404 problem for a form that does not exist.</returns>
    private static Task<IResult> GetFormDefinitionAsync(
        [FromRoute] int viewId,
        [FromServices] FormService service,
        CancellationToken cancellationToken) =>
        service.GetDefinitionAsync(viewId, cancellationToken);
}
