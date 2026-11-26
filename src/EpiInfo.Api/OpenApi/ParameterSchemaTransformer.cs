using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using EpiInfo.Api.Endpoints;
using EpiInfo.Domain.Records;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace EpiInfo.Api.OpenApi;

/// <summary>
/// Publishes the wire types of the coerced query and route parameters, so the OpenAPI document describes what the
/// client sends and never the strings the endpoints bind. It owns query and route parameter schemas only; every body
/// and response schema belongs to <see cref="ScalarSchemaTransformer"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Rewrite.</b> The record endpoints bind <c>page</c>, <c>pageSize</c> and <c>includeDeleted</c> as the string array
/// of every occurrence sent, and <c>position</c> as a string, so that <see cref="RequestParameters"/> alone coerces
/// them. On every operation, each of those parameters gets a fresh schema of its wire type:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <c>page</c> (query): <c>integer</c>, <c>int32</c>, minimum <see cref="RecordQueryRules.MinPage"/>, default
/// <see cref="RecordQueryRules.DefaultPage"/>.
/// </description></item>
/// <item><description>
/// <c>pageSize</c> (query): <c>integer</c>, <c>int32</c>, minimum <see cref="RecordQueryRules.MinPageSize"/>, maximum
/// <see cref="RecordQueryRules.MaxPageSize"/>, default <see cref="RecordQueryRules.DefaultPageSize"/>.
/// </description></item>
/// <item><description>
/// <c>position</c> (path, required): <c>integer</c>, <c>int32</c>, minimum 1. A position above <c>count</c> is the
/// service's 404 at run time, not a schema bound.
/// </description></item>
/// <item><description><c>includeDeleted</c> (query): <c>boolean</c>, default <c>false</c>.</description></item>
/// </list>
/// <para>
/// A parameter is matched by its location and by name ignoring case, and a name that differs only by case is
/// normalised to the wire spelling. Its description is kept. <c>q</c>, <c>viewId</c>, <c>globalRecordId</c> and every
/// other parameter are left as generated, and so is a parameter reference, which has no schema of its own to replace.
/// </para>
/// <para>
/// <b>Backstop.</b> On the four record reads that take these parameters, any of them that binding no longer surfaces
/// is appended with the same wire schema, so the generated client never loses one: the records list takes <c>q</c>,
/// <c>includeDeleted</c>, <c>page</c> and <c>pageSize</c>; <c>count</c> and a record's <c>position</c> take
/// <c>includeDeleted</c>; <c>at/{position}</c> takes <c>position</c> and <c>includeDeleted</c>. A parameter already
/// present, matched by location and by name ignoring case, is never added again, and existing parameters keep their
/// order.
/// </para>
/// <para>
/// The committed document and the client generated from it are compared byte for byte by <c>npm run lint</c>, so the
/// output depends only on the operation: every list here is static and ordered, numbers are written with the
/// invariant culture, and nothing reads the clock or the environment. An unexpected shape, such as a missing name,
/// location or parameter list, is skipped, never thrown on. The class is stateless and safe to share across documents
/// and threads.
/// </para>
/// </remarks>
public sealed class ParameterSchemaTransformer : IOpenApiOperationTransformer
{
    private const string Int32Format = "int32";

    // The smallest 1-based position, the lower bound RecordQueryRules.IsValidPosition admits.
    private const int MinPosition = 1;

    private const string HttpGet = "GET";

    private static readonly WireParameter Filter =
        new(RequestParameters.Query, ParameterLocation.Query, Required: false, CreateFilterSchema);

    private static readonly WireParameter IncludeDeleted =
        new(RequestParameters.IncludeDeleted, ParameterLocation.Query, Required: false, CreateIncludeDeletedSchema);

    private static readonly WireParameter Page =
        new(RequestParameters.Page, ParameterLocation.Query, Required: false, CreatePageSchema);

    private static readonly WireParameter PageSize =
        new(RequestParameters.PageSize, ParameterLocation.Query, Required: false, CreatePageSizeSchema);

    private static readonly WireParameter Position =
        new(RequestParameters.Position, ParameterLocation.Path, Required: true, CreatePositionSchema);

    // The parameters whose binding type differs from their wire type. q binds as the string it is on the wire, so it
    // is only ever added by the backstop.
    private static readonly WireParameter[] CoercedParameters = [Page, PageSize, Position, IncludeDeleted];

    // The record reads and the parameters each takes, in the order they are appended when missing. Templates are the
    // record routes with every route parameter reduced to its name, as NormalizeTemplate reduces a relative path.
    private static readonly BackstopRoute[] BackstopRoutes =
    [
        new("api/forms/{viewId}/records", [Filter, IncludeDeleted, Page, PageSize]),
        new("api/forms/{viewId}/records/count", [IncludeDeleted]),
        new("api/forms/{viewId}/records/at/{position}", [Position, IncludeDeleted]),
        new("api/forms/{viewId}/records/{globalRecordId}/position", [IncludeDeleted]),
    ];

    /// <summary>
    /// Rewrites the coerced parameters of one operation to their wire schemas and, on the record reads, appends any of
    /// their parameters that binding did not surface.
    /// </summary>
    /// <param name="operation">The operation to rewrite in place.</param>
    /// <param name="context">The endpoint description, whose method and relative path select the backstop.</param>
    /// <param name="cancellationToken">Not observed; the work is synchronous.</param>
    /// <returns>A completed task.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="operation"/> or <paramref name="context"/> is <see langword="null"/>.
    /// </exception>
    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(context);

        if (operation.Parameters is { Count: > 0 } parameters)
        {
            RewriteCoercedParameters(parameters);
        }

        var description = context.Description;
        if (FindBackstopRoute(description.HttpMethod, description.RelativePath) is { } route)
        {
            AddMissingParameters(operation, route);
        }

        return Task.CompletedTask;
    }

    private static void RewriteCoercedParameters(IList<IOpenApiParameter> parameters)
    {
        foreach (var item in parameters)
        {
            if (item is not OpenApiParameter { Name: { } name, In: { } location } parameter
                || FindCoercedParameter(name, location) is not { } wire)
            {
                continue;
            }

            // A case-only variant beside a parameter that already holds the wire spelling is a separate parameter:
            // renaming it would publish the wire name twice, so it stays as generated.
            if (!string.Equals(name, wire.Name, StringComparison.Ordinal) && HasExactParameter(parameters, wire))
            {
                continue;
            }

            parameter.Name = wire.Name;

            // Set rather than left as bound: the query parameters are optional with a default on the wire whatever
            // their binding type, and a path parameter is always required.
            parameter.Required = wire.Required;
            parameter.Schema = wire.CreateSchema();
        }
    }

    private static WireParameter? FindCoercedParameter(string name, ParameterLocation location)
    {
        foreach (var wire in CoercedParameters)
        {
            if (wire.Location == location && string.Equals(name, wire.Name, StringComparison.OrdinalIgnoreCase))
            {
                return wire;
            }
        }

        return null;
    }

    private static bool HasExactParameter(IList<IOpenApiParameter> parameters, WireParameter wire)
    {
        foreach (var parameter in parameters)
        {
            if (parameter.In == wire.Location && string.Equals(parameter.Name, wire.Name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static BackstopRoute? FindBackstopRoute(string? httpMethod, string? relativePath)
    {
        if (!string.Equals(httpMethod, HttpGet, StringComparison.OrdinalIgnoreCase) || relativePath is null)
        {
            return null;
        }

        var template = NormalizeTemplate(relativePath);
        foreach (var route in BackstopRoutes)
        {
            if (string.Equals(route.Template, template, StringComparison.OrdinalIgnoreCase))
            {
                return route;
            }
        }

        return null;
    }

    private static void AddMissingParameters(OpenApiOperation operation, BackstopRoute route)
    {
        var parameters = operation.Parameters ??= new List<IOpenApiParameter>();
        foreach (var wire in route.Parameters)
        {
            if (IsPresent(parameters, wire))
            {
                continue;
            }

            parameters.Add(new OpenApiParameter
            {
                Name = wire.Name,
                In = wire.Location,
                Required = wire.Required,
                Schema = wire.CreateSchema(),
            });
        }
    }

    private static bool IsPresent(IList<IOpenApiParameter> parameters, WireParameter wire)
    {
        foreach (var parameter in parameters)
        {
            if (parameter.In == wire.Location
                && string.Equals(parameter.Name, wire.Name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // Reduces a relative path to a comparable template: leading slashes are trimmed and every route parameter becomes
    // {name}, dropping its constraints, optional marker, default value and catch-all stars, so "/api/forms/{viewId:int}"
    // and "api/forms/{viewId}" compare equal. Doubled braces are literal braces in a route template, outside a
    // parameter and inside a constraint alike. An unclosed parameter is kept as text, so it matches no template.
    private static string NormalizeTemplate(string relativePath)
    {
        var path = relativePath.AsSpan().TrimStart('/');
        var builder = new StringBuilder(path.Length);
        var index = 0;
        while (index < path.Length)
        {
            if (path[index] != '{')
            {
                builder.Append(path[index]);
                index++;
                continue;
            }

            if (index + 1 < path.Length && path[index + 1] == '{')
            {
                builder.Append(path.Slice(index, 2));
                index += 2;
                continue;
            }

            var end = FindParameterEnd(path, index + 1);
            if (end < 0)
            {
                builder.Append(path[index..]);
                break;
            }

            builder.Append('{').Append(ParameterName(path[(index + 1)..end])).Append('}');
            index = end + 1;
        }

        return builder.ToString();
    }

    // The index of the brace that closes the parameter whose text starts at start, or -1 when it is not closed.
    private static int FindParameterEnd(ReadOnlySpan<char> path, int start)
    {
        var index = start;
        while (index < path.Length)
        {
            var current = path[index];
            var doubled = index + 1 < path.Length && path[index + 1] == current;
            if (current == '}' && !doubled)
            {
                return index;
            }

            index += (current is '{' or '}') && doubled ? 2 : 1;
        }

        return -1;
    }

    // The name of a route parameter: its text before the first ':', '?' or '=', without a leading catch-all '*'.
    private static ReadOnlySpan<char> ParameterName(ReadOnlySpan<char> parameter)
    {
        var cut = parameter.IndexOfAny(':', '?', '=');
        var name = cut < 0 ? parameter : parameter[..cut];
        return name.TrimStart('*');
    }

    private static OpenApiSchema CreateFilterSchema() => new() { Type = JsonSchemaType.String };

    private static OpenApiSchema CreateIncludeDeletedSchema() => new()
    {
        Type = JsonSchemaType.Boolean,
        Default = JsonValue.Create(false),
    };

    private static OpenApiSchema CreatePageSchema() => new()
    {
        Type = JsonSchemaType.Integer,
        Format = Int32Format,
        Minimum = Invariant(RecordQueryRules.MinPage),
        Default = JsonValue.Create(RecordQueryRules.DefaultPage),
    };

    private static OpenApiSchema CreatePageSizeSchema() => new()
    {
        Type = JsonSchemaType.Integer,
        Format = Int32Format,
        Minimum = Invariant(RecordQueryRules.MinPageSize),
        Maximum = Invariant(RecordQueryRules.MaxPageSize),
        Default = JsonValue.Create(RecordQueryRules.DefaultPageSize),
    };

    private static OpenApiSchema CreatePositionSchema() => new()
    {
        Type = JsonSchemaType.Integer,
        Format = Int32Format,
        Minimum = Invariant(MinPosition),
    };

    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);

    // A parameter's wire name, location and required flag, and the factory of its schema. Each use takes a new schema,
    // so no schema instance is shared between parameters or operations.
    private sealed record WireParameter(
        string Name,
        ParameterLocation Location,
        bool Required,
        Func<OpenApiSchema> CreateSchema);

    private sealed record BackstopRoute(string Template, WireParameter[] Parameters);
}
