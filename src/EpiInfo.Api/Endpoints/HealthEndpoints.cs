using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace EpiInfo.Api.Endpoints;

/// <summary>
/// The body of every <c>GET /health</c> response: <c>{"status":"healthy"}</c> with 200 or
/// <c>{"status":"unhealthy"}</c> with 503.
/// </summary>
/// <param name="Status"><see cref="HealthyStatus"/> or <see cref="UnhealthyStatus"/>.</param>
public sealed record HealthResponse(string Status)
{
    /// <summary>The status of a 200 response: the database answered a trivial query.</summary>
    public const string HealthyStatus = "healthy";

    /// <summary>The status of a 503 response: the database did not answer, or no connection string is configured.</summary>
    public const string UnhealthyStatus = "unhealthy";
}

/// <summary>
/// Maps <c>GET /health</c>, outside <c>/api</c>: 200 <c>{"status":"healthy"}</c> when SQL Server answers a trivial
/// query and 503 <c>{"status":"unhealthy"}</c> otherwise.
/// </summary>
/// <remarks>
/// <para>
/// The answer comes from the registered health checks, of which the database check
/// (<see cref="Health.SqlServerHealthCheck"/>) is the one that matters. Only a Healthy report is 200; Degraded counts
/// as unhealthy. The default <c>MapHealthChecks</c> writer is not used, because it writes plain-text
/// <c>Healthy</c>/<c>Unhealthy</c> and answers Degraded with 200.
/// </para>
/// <para>
/// <b>Redaction.</b> The body is one of two fixed texts and carries nothing from the report: no check name,
/// description, duration, exception or data. Nothing is logged here. This 503 is the one error response outside the
/// ProblemDetails contract; its JSON content type keeps status-code pages from replacing it.
/// </para>
/// <para>
/// A client abort is not caught: the cancelled check propagates <see cref="OperationCanceledException"/>, for which
/// the exception handler writes no response.
/// </para>
/// </remarks>
public static class HealthEndpoints
{
    /// <summary>Maps <c>GET /health</c>, with no <c>/api</c> prefix and no alias, and its OpenAPI metadata.</summary>
    /// <param name="app">The application's endpoint route builder.</param>
    /// <returns><paramref name="app"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> is <see langword="null"/>.</exception>
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/health", GetHealthAsync)
            .WithTags("Health")
            .WithName("GetHealth")
            .Produces<HealthResponse>(StatusCodes.Status200OK)
            .Produces<HealthResponse>(StatusCodes.Status503ServiceUnavailable);

        return app;
    }

    // HealthCheckService is taken explicitly from services, so mapping and describing the endpoint, as build-time
    // OpenAPI generation does, needs neither a connection string nor a database.
    private static async Task<IResult> GetHealthAsync(
        [FromServices] HealthCheckService healthChecks,
        CancellationToken cancellationToken)
    {
        var report = await healthChecks.CheckHealthAsync(cancellationToken).ConfigureAwait(false);

        // No serializer options are passed, so the configured HTTP JSON options (camelCase) write "status".
        return report.Status == HealthStatus.Healthy
            ? Results.Json(new HealthResponse(HealthResponse.HealthyStatus), statusCode: StatusCodes.Status200OK)
            : Results.Json(
                new HealthResponse(HealthResponse.UnhealthyStatus),
                statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}
