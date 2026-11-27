using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using EpiInfo.Api.Errors;
using Microsoft.Extensions.Options;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

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
/// OpenAPI document. Each endpoint declares its own <c>Accepts</c> metadata and reads its body through
/// <see cref="JsonRequestBody"/>.
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

/// <summary>
/// The one reader of the request body of the four endpoints that carry <see cref="JsonBodyMetadata"/>. It reads the
/// whole body as one JSON value before it looks at the value's kind, so content that is not valid JSON is told apart
/// from a valid JSON value that is not an object, whatever the first token is, and it judges the object's members
/// before it reads the request type, so an ambiguous or wrongly typed member is reported as such.
/// </summary>
/// <remarks>
/// <para>Outcomes, in order:</para>
/// <list type="number">
/// <item><description>
/// A <c>Content-Type</c> that is not a JSON media type throws a <see cref="BadHttpRequestException"/> with status
/// 415. In the API pipeline <c>JsonContentTypeMiddleware</c> and routing answer it first; this keeps a host without
/// that guard on 415.
/// </description></item>
/// <item><description>
/// Content that is not exactly one valid JSON value, under the HTTP JSON options that <c>JsonSetup.AddApiJson</c>
/// configures, throws a <see cref="BadHttpRequestException"/> with status 400 whose inner exception is the
/// <see cref="JsonException"/>: a syntax error anywhere, such as <c>[</c>, <c>[1,]</c> or a bare word, content after
/// the value, such as <c>5 x</c> or <c>true false</c>, and an empty or whitespace-only body. <c>ApiExceptionHandler</c>
/// answers it with "request body is required" when the body was empty and "malformed JSON" otherwise.
/// </description></item>
/// <item><description>
/// The JSON literal <c>null</c> gives neither a value nor a problem, so the service answers it with its
/// request-level 400 "request body is required".
/// </description></item>
/// <item><description>
/// A valid value that is not an object, that is an array, a string, a number or a boolean, gives the request-level
/// 400 of <see cref="ApiProblems.RequestBodyMustBeObject"/>.
/// </description></item>
/// <item><description>
/// An object that names a member more than once, exactly or differing only by letter case, gives the request-level
/// 400 of <see cref="ApiProblems.RequestBodyRepeatsMember"/>: binding matches member names ignoring case, so one
/// occurrence would silently replace the other. Every member name is decoded first, and one that cannot be decoded,
/// such as an escaped lone surrogate, throws the same 400 <see cref="BadHttpRequestException"/> with a
/// <see cref="JsonException"/> as its inner exception, so it is "malformed JSON". Only the object's own member names
/// are compared here: the keys of a <c>values</c> object are field names, which the values reader and the record
/// validator judge, so a repeated one keeps its field-level 400.
/// </description></item>
/// <item><description>
/// The endpoint's member check, when it passes one, runs on the object next, and a problem it returns is the result.
/// The search endpoint's check, <c>SearchRequestShape.Check</c>, reports each member of the wrong JSON kind in one
/// field-level 400, and a criterion object that names a member more than once with the request-level 400 above.
/// </description></item>
/// <item><description>
/// The object is then deserialised into the request type with the same options. A member that still does not
/// convert, such as a search criterion string that cannot be decoded, throws the same 400
/// <see cref="BadHttpRequestException"/> with its <see cref="JsonException"/> as the inner exception, so it is
/// "malformed JSON" as well.
/// </description></item>
/// </list>
/// <para>
/// A <see cref="BadHttpRequestException"/> from the body stream, such as the 413 of the body limit or of Kestrel, an
/// <see cref="IOException"/> and an <see cref="OperationCanceledException"/> propagate unchanged, so the exception
/// handler and the exception boundary answer them by their own rules. The exceptions thrown here carry fixed messages
/// only: neither the body nor a serializer message, which can quote the body, is ever copied into them. The body is
/// read once, through <see cref="HttpRequest.Body"/>, so the body limit's counting stream sees every byte and its
/// <see cref="IRequestBodyReadFeature"/> describes this read. Nothing is logged or kept.
/// </para>
/// </remarks>
public static class JsonRequestBody
{
    private const string UnsupportedMediaTypeMessage = "The request body is not JSON.";

    private const string InvalidJsonMessage = "The request body is not valid JSON for this endpoint.";

    private const string UndecodableNameMessage = "A member name of the request body cannot be decoded.";

    /// <summary>
    /// Reads the request body as one JSON value and, when it is an object that names each member once, as a
    /// <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">The request type the endpoint takes, such as <c>RecordWriteRequest</c>.</typeparam>
    /// <param name="context">The current request.</param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>
    /// The request for an object body; a <see cref="JsonRequestBodyResult{T}"/> with neither a value nor a problem for
    /// the JSON literal <c>null</c>; or the request-level 400 problem for any other valid JSON value and for an object
    /// that names a member more than once.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="BadHttpRequestException">
    /// With status 415 for a <c>Content-Type</c> that is not JSON; with status 400, and a <see cref="JsonException"/>
    /// as its inner exception, for content that is not one valid JSON value, an object member name that cannot be
    /// decoded, or an object that does not convert to <typeparamref name="T"/>; or as the body stream raised it, such
    /// as a 413.
    /// </exception>
    public static Task<JsonRequestBodyResult<T>> ReadObjectAsync<T>(
        HttpContext context,
        CancellationToken cancellationToken)
        where T : class =>
        ReadObjectAsync<T>(context, static _ => null, cancellationToken);

    /// <summary>
    /// Reads the request body as <see cref="ReadObjectAsync{T}(HttpContext, CancellationToken)"/> does, and runs
    /// <paramref name="checkMembers"/> on the body object after the repeated-member check and before the object is
    /// read as a <typeparamref name="T"/>, so a member of the wrong JSON kind can be reported by name rather than as a
    /// conversion failure.
    /// </summary>
    /// <typeparam name="T">The request type the endpoint takes, such as <c>SearchRequest</c>.</typeparam>
    /// <param name="context">The current request.</param>
    /// <param name="checkMembers">
    /// The endpoint's member check. It receives the body object, whose member names are decoded and each named once,
    /// and returns the 400 to answer instead of reading the request, or <see langword="null"/> to read it. A
    /// <see cref="BadHttpRequestException"/> it throws propagates unchanged.
    /// </param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>
    /// The outcomes of <see cref="ReadObjectAsync{T}(HttpContext, CancellationToken)"/>, plus the problem
    /// <paramref name="checkMembers"/> returned.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/> or <paramref name="checkMembers"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="BadHttpRequestException">
    /// As <see cref="ReadObjectAsync{T}(HttpContext, CancellationToken)"/> throws it, or as
    /// <paramref name="checkMembers"/> threw it.
    /// </exception>
    public static async Task<JsonRequestBodyResult<T>> ReadObjectAsync<T>(
        HttpContext context,
        Func<JsonElement, IResult?> checkMembers,
        CancellationToken cancellationToken)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(checkMembers);

        var request = context.Request;
        if (!request.HasJsonContentType())
        {
            throw new BadHttpRequestException(UnsupportedMediaTypeMessage, StatusCodes.Status415UnsupportedMediaType);
        }

        // The options minimal-API binding, typed results and the ProblemDetails writer share, so the body is read
        // under exactly the wire rules of JsonSetup.
        var options = context.RequestServices.GetRequiredService<IOptions<HttpJsonOptions>>().Value.SerializerOptions;

        JsonElement? root;
        try
        {
            root = await request.ReadFromJsonAsync(GetTypeInfo<JsonElement?>(options), cancellationToken);
        }
        catch (JsonException exception)
        {
            throw InvalidJson(exception);
        }

        if (root is not { } element)
        {
            return default;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return new(null, ApiProblems.RequestBodyMustBeObject());
        }

        if (NamesAMemberMoreThanOnce(element))
        {
            return new(null, ApiProblems.RequestBodyRepeatsMember());
        }

        if (checkMembers(element) is { } problem)
        {
            return new(null, problem);
        }

        try
        {
            return new(element.Deserialize(GetTypeInfo<T>(options)), null);
        }
        catch (JsonException exception)
        {
            throw InvalidJson(exception);
        }
    }

    /// <summary>
    /// Whether the JSON object <paramref name="element"/> names a member more than once, exactly or differing only by
    /// letter case. Binding matches member names ignoring case, so either repeat would let one occurrence silently
    /// replace another. Every name is decoded before the answer is given, so an undecodable name is reported as such
    /// wherever it stands.
    /// </summary>
    /// <param name="element">A JSON object: a request body or an object member of one.</param>
    /// <returns>
    /// <see langword="true"/> when two member names are equal under ordinal, case-insensitive comparison.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="element"/> is not a JSON object.</exception>
    /// <exception cref="BadHttpRequestException">
    /// With status 400 and a <see cref="JsonException"/> as its inner exception, which the exception handler answers
    /// with "malformed JSON", when a member name cannot be decoded, such as an escaped lone surrogate.
    /// </exception>
    internal static bool NamesAMemberMoreThanOnce(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("A member check needs a JSON object.", nameof(element));
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var repeated = false;
        foreach (var property in element.EnumerateObject())
        {
            repeated |= !names.Add(MemberName(property));
        }

        return repeated;
    }

    /// <summary>The decoded name of a member of the request body.</summary>
    /// <param name="property">A member of the request body or of an object inside it.</param>
    /// <returns>The member's name.</returns>
    /// <exception cref="BadHttpRequestException">
    /// With status 400 and a <see cref="JsonException"/> as its inner exception, which the exception handler answers
    /// with "malformed JSON", when the name cannot be decoded, such as an escaped lone surrogate. The exception's
    /// message is fixed and quotes nothing of the name.
    /// </exception>
    internal static string MemberName(JsonProperty property)
    {
        try
        {
            return property.Name;
        }
        catch (InvalidOperationException)
        {
            throw InvalidJson(new JsonException(UndecodableNameMessage));
        }
    }

    // The contract the options' own resolver builds, as binding looks it up, rather than the reflection-based generic
    // overloads of the serializer.
    private static JsonTypeInfo<TValue> GetTypeInfo<TValue>(JsonSerializerOptions options) =>
        (JsonTypeInfo<TValue>)options.GetTypeInfo(typeof(TValue));

    private static BadHttpRequestException InvalidJson(JsonException exception) =>
        new(InvalidJsonMessage, StatusCodes.Status400BadRequest, exception);
}

/// <summary>
/// The outcome of <see cref="JsonRequestBody.ReadObjectAsync{T}(HttpContext, CancellationToken)"/> and of
/// <see cref="JsonRequestBody.ReadObjectAsync{T}(HttpContext, Func{JsonElement, IResult}, CancellationToken)"/>.
/// </summary>
/// <typeparam name="T">The request type the endpoint takes.</typeparam>
/// <param name="Value">
/// The request read from an object body, or <see langword="null"/> for the JSON literal <c>null</c> or when
/// <paramref name="Problem"/> is set.
/// </param>
/// <param name="Problem">
/// The 400 to return instead of calling the service: the reader's request-level 400, or the problem the endpoint's
/// member check returned. <see langword="null"/> when there is none.
/// </param>
public readonly record struct JsonRequestBodyResult<T>(T? Value, IResult? Problem)
    where T : class
{
    /// <summary>
    /// Returns the type name only. The compiler-generated record <c>ToString</c> would print the request, whose
    /// members are field data that must never reach a log line or an error body.
    /// </summary>
    /// <returns><c>JsonRequestBodyResult</c>.</returns>
    public override string ToString() => nameof(JsonRequestBodyResult<>);
}
