using EpiInfo.Api.Errors;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace EpiInfo.Api.Hosting;

/// <summary>
/// The media-type guard of the four JSON body endpoints. On an endpoint carrying <see cref="JsonBodyMetadata"/> it
/// answers 415 for any <c>Content-Type</c> other than <c>application/json</c> (case-insensitive, with an optional
/// <c>charset=utf-8</c>), with or without a body, and then the request-level 400 "request body is required" for an
/// allowed media type with an empty or missing body. Every other request passes through unchanged.
/// </summary>
/// <remarks>
/// <para>
/// Minimal-API JSON binding accepts any <c>+json</c> media type on its own, so this guard is what makes
/// <c>application/merge-patch+json</c>, <c>application/vnd.x+json</c> and <c>application/problem+json</c> a 415.
/// </para>
/// <para>
/// <c>Program.cs</c> adds it after <c>UseRouting</c> and <c>RequestBodyLimitMiddleware</c>, so the endpoint is
/// already matched and nothing has been bound. A request that matches no route, or the 405 endpoint of a method
/// mismatch, carries no marker and keeps its 404 or 405. Routing may itself reject a <c>text/plain</c> request with an
/// empty 415 before this guard runs; status-code pages turn that into the same 415 ProblemDetails, so that path is
/// left alone.
/// </para>
/// <para>
/// Error bodies come only from <see cref="ApiProblems"/>, which writes fixed text plus <c>traceId</c>; the received
/// <c>Content-Type</c> is never echoed, and nothing is logged here.
/// </para>
/// </remarks>
public sealed class JsonContentTypeMiddleware
{
    private const string JsonMediaType = "application/json";

    private const string CharsetParameter = "charset";

    private const string Utf8Charset = "utf-8";

    private readonly RequestDelegate _next;

    /// <summary>Creates the guard in front of <paramref name="next"/>.</summary>
    /// <param name="next">The rest of the pipeline.</param>
    /// <exception cref="ArgumentNullException"><paramref name="next"/> is <see langword="null"/>.</exception>
    public JsonContentTypeMiddleware(RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(next);
        _next = next;
    }

    /// <summary>
    /// Checks the media type, then the presence of a body, on an endpoint marked with <see cref="JsonBodyMetadata"/>,
    /// and otherwise calls the rest of the pipeline.
    /// </summary>
    /// <param name="context">The current request.</param>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.GetEndpoint()?.Metadata.GetMetadata<JsonBodyMetadata>() is null)
        {
            await _next(context);
            return;
        }

        // The media type is checked first, so a wrong type is a 415 whether or not a body was sent.
        if (!IsAllowedJsonContentType(context.Request.ContentType))
        {
            await ApiProblems.WriteAsync(context, ApiProblems.UnsupportedMediaType());
            return;
        }

        if (!CanHaveBody(context))
        {
            await ApiProblems.WriteAsync(context, ApiProblems.RequestBodyRequired());
            return;
        }

        await _next(context);
    }

    /// <summary>
    /// Whether <paramref name="contentType"/> is <c>application/json</c>, compared case-insensitively, with no
    /// parameter or with exactly one <c>charset</c> parameter whose unquoted value is <c>utf-8</c>.
    /// </summary>
    /// <param name="contentType">The request's <c>Content-Type</c> header value, or <see langword="null"/>.</param>
    /// <returns>
    /// <see langword="false"/> for a missing, blank or unparseable value, any other media type (<c>+json</c> types
    /// included), any other parameter, another charset, or a repeated parameter.
    /// </returns>
    public static bool IsAllowedJsonContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType) || !MediaTypeHeaderValue.TryParse(contentType, out var mediaType))
        {
            return false;
        }

        if (!StringSegment.Equals(mediaType.MediaType, JsonMediaType, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var parameters = mediaType.Parameters;
        if (parameters.Count == 0)
        {
            return true;
        }

        if (parameters.Count > 1)
        {
            return false;
        }

        var parameter = parameters[0];
        return StringSegment.Equals(parameter.Name, CharsetParameter, StringComparison.OrdinalIgnoreCase)
            && StringSegment.Equals(
                HeaderUtilities.RemoveQuotes(parameter.Value),
                Utf8Charset,
                StringComparison.OrdinalIgnoreCase);
    }

    // Decided from headers only; the body is never read, peeked or buffered. Mixing BodyReader peeks with later Body
    // stream reads can lose data, and RequestBodyLimitMiddleware's counting stream must see every byte. A chunked
    // body that turns out to be empty therefore reaches binding, which reports the same request-level 400.
    private static bool CanHaveBody(HttpContext context)
    {
        if (context.Request.ContentLength == 0)
        {
            return false;
        }

        // On Kestrel an HTTP/1.1 request with neither Content-Length nor Transfer-Encoding cannot have a body. A host
        // that does not expose the feature is left to binding.
        return context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody != false;
    }
}

/// <summary>Pipeline registration for <see cref="JsonContentTypeMiddleware"/>.</summary>
public static class JsonContentTypeMiddlewareExtensions
{
    /// <summary>
    /// Adds <see cref="JsonContentTypeMiddleware"/>. Call it after <c>UseRouting</c> and the request body limit, so the
    /// endpoint is matched before the guard reads its metadata and before any body is bound.
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> is <see langword="null"/>.</exception>
    public static IApplicationBuilder UseJsonContentTypeGuard(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<JsonContentTypeMiddleware>();
    }
}
