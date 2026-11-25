namespace EpiInfo.Api.Hosting;

/// <summary>
/// Endpoint metadata marking an endpoint whose request body must be <c>application/json</c>.
/// <c>JsonContentTypeMiddleware</c> enforces the media type and the non-empty body only on endpoints that
/// carry this marker, so a route miss stays 404 and a method mismatch stays 405.
/// </summary>
/// <remarks>
/// <para>
/// Exactly four endpoints carry it, attached through
/// <see cref="JsonBodyEndpointExtensions.RequireJsonBody{TBuilder}(TBuilder)"/>:
/// <c>POST /api/forms/{viewId}/records</c>, <c>PUT /api/forms/{viewId}/records/{globalRecordId}</c> and
/// <c>POST /api/forms/{viewId}/records/search</c> in <c>Endpoints/RecordEndpoints.cs</c>, and
/// <c>POST /api/forms/{viewId}/checkcode/field-event</c> in <c>Endpoints/CheckCodeEndpoints.cs</c>.
/// <c>DELETE</c> and every <c>GET</c> take no body and never carry it.
/// </para>
/// <para>
/// The type itself is the signal, so it has no members. It deliberately implements no framework metadata
/// interface such as <c>IAcceptsMetadata</c>: that would change routing's content-type matching and the generated
/// OpenAPI document. Each endpoint declares its own <c>Accepts</c> metadata.
/// </para>
/// </remarks>
public sealed class JsonBodyMetadata
{
    private JsonBodyMetadata()
    {
    }

    /// <summary>The single shared instance; the marker carries no state.</summary>
    public static JsonBodyMetadata Instance { get; } = new();
}

/// <summary>Endpoint convention for attaching <see cref="JsonBodyMetadata"/>.</summary>
public static class JsonBodyEndpointExtensions
{
    /// <summary>
    /// Marks the endpoint as taking a JSON request body, so <c>JsonContentTypeMiddleware</c> rejects any other
    /// media type with 415 and an empty body with the request-level 400.
    /// </summary>
    /// <typeparam name="TBuilder">The endpoint convention builder, for example the <c>RouteHandlerBuilder</c> that
    /// <c>MapPost</c> and <c>MapPut</c> return.</typeparam>
    /// <param name="builder">The builder of the endpoint to mark.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static TBuilder RequireJsonBody<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.WithMetadata(JsonBodyMetadata.Instance);
        return builder;
    }
}
