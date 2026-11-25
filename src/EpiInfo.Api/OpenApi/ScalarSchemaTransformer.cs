using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using EpiInfo.Api.Contracts;
using EpiInfo.Api.Errors;
using EpiInfo.Domain.Diagnostics;
using EpiInfo.Domain.Events;
using EpiInfo.Domain.Forms;
using EpiInfo.Domain.Records;
using EpiInfo.Domain.Storage;
using EpiInfo.Domain.Values;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.OpenApi;

namespace EpiInfo.Api.OpenApi;

/// <summary>
/// Publishes the wire schemas of the API's bodies, so the OpenAPI document describes what travels on the wire and
/// never the CLR types that bind or carry it. It owns every schema except query and route parameters, which
/// <c>ParameterSchemaTransformer</c> owns.
/// </summary>
/// <remarks>
/// <para>What it publishes:</para>
/// <list type="bullet">
/// <item><description>
/// <c>FieldValue</c>, one component holding the scalar union <c>string | number | boolean | null</c>. Every record
/// <c>values</c> map, <c>assigned</c>, a criterion <c>value</c>, an option <c>value</c> and the
/// <c>lower</c>/<c>upper</c> bounds reference it, and no member of the tagged <see cref="FieldValue"/> struct
/// (<see cref="FieldValueKind"/>, <c>kind</c>, <c>isNull</c>) appears.
/// </description></item>
/// <item><description>
/// The request bodies whose members bind as raw <see cref="JsonElement"/>: <c>values</c> is an object of
/// <c>FieldValue</c>, <c>event</c> the string enum <c>FieldBefore</c>, <c>FieldAfter</c>, <c>FieldClick</c>, and
/// <c>fieldName</c> a string, each required, so no <see cref="JsonElement"/> component exists. The search body's
/// <c>criteria</c> is a required, non-null array; its <c>includeDeleted</c> and <c>excludeGlobalRecordId</c> stay
/// optional.
/// </description></item>
/// <item><description>
/// The camelCase string enums <c>FieldType</c> and <c>DiagnosticSeverity</c>, and the <c>writeOutcome</c> enum
/// <c>notWritten</c>, <c>unknown</c>, <c>written</c> of <c>WriteProblemDetails</c>, the schema of the 500 and 503
/// responses of the three record writes and of no other response.
/// </description></item>
/// <item><description>
/// <c>NavigationPosition</c> as <c>{position: integer | null, count: integer}</c>, and every Domain DTO member the API
/// always writes as required, so the generated TypeScript members are non-optional; a member that may be
/// <c>null</c> keeps <c>null</c> in its type.
/// </description></item>
/// </list>
/// <para>
/// The committed <c>openapi/EpiInfo.Api.json</c> and the client types generated from it are compared byte for byte by
/// <c>npm run lint</c>, so the output is deterministic: every list is static and ordered, every added
/// <c>required</c> set is sorted ordinally, and nothing reads the clock, the environment or the culture. An
/// unexpected schema shape is left as generated, never thrown on.
/// </para>
/// <para>The class is stateless and safe to share across documents and threads.</para>
/// </remarks>
public sealed class ScalarSchemaTransformer :
    IOpenApiSchemaTransformer,
    IOpenApiOperationTransformer,
    IOpenApiDocumentTransformer
{
    /// <summary>The component id of the field-value scalar union.</summary>
    public const string FieldValueSchemaId = "FieldValue";

    /// <summary>The component id of the ProblemDetails of a failed record write.</summary>
    public const string WriteProblemDetailsSchemaId = "WriteProblemDetails";

    // Metadata keys of Microsoft.AspNetCore.OpenApi: a node holding the first becomes the component of that id, and a
    // property node holding the second is wrapped as oneOf [null, $ref] when it is resolved.
    private const string SchemaIdMetadataKey = "x-schema-id";
    private const string NullablePropertyMetadataKey = "x-is-nullable-property";

    // The component ids of CLR helpers that must never be published; the schema role keeps both unreferenced.
    private const string JsonElementSchemaId = nameof(JsonElement);
    private const string FieldValueKindSchemaId = nameof(FieldValueKind);

    private const string Int32Format = "int32";

    // The JSON name WriteProblemDetails declares for its outcome member.
    private const string WriteOutcomeProperty = "writeOutcome";

    private const JsonSchemaType FieldValueTypes =
        JsonSchemaType.String | JsonSchemaType.Number | JsonSchemaType.Boolean | JsonSchemaType.Null;

    // Wire names follow the camelCase property policy that JsonSetup applies to the shared HTTP JSON options.
    private static readonly string ValuesProperty = CamelCase(nameof(RecordWriteRequest.Values));
    private static readonly string EventProperty = CamelCase(nameof(FieldEventRequest.Event));
    private static readonly string FieldNameProperty = CamelCase(nameof(FieldEventRequest.FieldName));
    private static readonly string CriteriaProperty = CamelCase(nameof(SearchRequest.Criteria));
    private static readonly string PositionProperty = CamelCase(nameof(NavigationPosition.Position));
    private static readonly string CountProperty = CamelCase(nameof(NavigationPosition.Count));

    private static readonly string[] FieldTypeWireNames = CamelCaseNames<FieldType>();
    private static readonly string[] DiagnosticSeverityWireNames = CamelCaseNames<DiagnosticSeverity>();

    private static readonly string[] FieldEventWireNames =
    [
        FieldEventKind.FieldBefore.ToWireName(),
        FieldEventKind.FieldAfter.ToWireName(),
        FieldEventKind.FieldClick.ToWireName(),
    ];

    private static readonly string[] WriteOutcomeWireNames =
    [
        WriteOutcomeExtensions.WireNotWritten,
        WriteOutcomeExtensions.WireUnknown,
        WriteOutcomeExtensions.WireWritten,
    ];

    private static readonly int[] WriteFailureStatuses =
    [
        StatusCodes.Status500InternalServerError,
        StatusCodes.Status503ServiceUnavailable,
    ];

    /// <summary>
    /// Registers all three roles of this transformer on <paramref name="options"/>. <c>Program.cs</c> calls it inside
    /// <c>AddOpenApi</c>, alongside <c>options.AddOperationTransformer&lt;ParameterSchemaTransformer&gt;()</c>.
    /// </summary>
    /// <remarks>
    /// The schema role does the essential work; the operation role repoints the write 500 and 503 responses and the
    /// document role drops helper components. Each role is correct on its own, so none depends on the order in which
    /// the framework runs them.
    /// </remarks>
    /// <param name="options">The options of the default <c>v1</c> document.</param>
    /// <returns><paramref name="options"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public static OpenApiOptions Register(OpenApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.AddSchemaTransformer<ScalarSchemaTransformer>();
        options.AddOperationTransformer<ScalarSchemaTransformer>();
        options.AddDocumentTransformer<ScalarSchemaTransformer>();
        return options;
    }

    /// <summary>
    /// Rewrites one schema node. The framework calls it for every node of a body schema, the root and each nested
    /// property, item and dictionary value, before the nodes are resolved into components.
    /// </summary>
    /// <remarks>
    /// Rules keyed by type run on every node of that type, because a nested occurrence becomes the component just as
    /// a root one does, and every occurrence must produce the same component. Rules keyed by a member run only on that
    /// member's node. Query and route parameter schemas are left to <c>ParameterSchemaTransformer</c>.
    /// </remarks>
    /// <param name="schema">The node to rewrite in place.</param>
    /// <param name="context">The JSON contract of the node and the document being built.</param>
    /// <param name="cancellationToken">Not observed; the work is synchronous.</param>
    /// <returns>A completed task.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="schema"/> or <paramref name="context"/> is <see langword="null"/>.
    /// </exception>
    public Task TransformAsync(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(context);

        if (context.ParameterDescription is { Source: { } source } && source != BindingSource.Body)
        {
            return Task.CompletedTask;
        }

        var typeInfo = context.JsonTypeInfo;
        var type = Nullable.GetUnderlyingType(typeInfo.Type) ?? typeInfo.Type;

        if (type == typeof(FieldValue))
        {
            ApplyFieldValue(schema);
            return Task.CompletedTask;
        }

        ApplyTypeRules(schema, type, typeInfo);

        if (context.JsonPropertyInfo is { } property)
        {
            ApplyMemberRules(schema, property);
        }

        return Task.CompletedTask;
    }

    private static void ApplyFieldValue(OpenApiSchema schema)
    {
        ResetShape(schema);
        schema.Type = FieldValueTypes;
        schema.AdditionalProperties = null;
        schema.Required = null;

        // Every occurrence, FieldValue? included, carries the one id, so all of them resolve to the single component.
        schema.Metadata ??= new Dictionary<string, object>(StringComparer.Ordinal);
        schema.Metadata[SchemaIdMetadataKey] = FieldValueSchemaId;
    }

    private static void ApplyTypeRules(OpenApiSchema schema, Type type, JsonTypeInfo typeInfo)
    {
        if (typeInfo.Kind == JsonTypeInfoKind.Dictionary
            && typeInfo.ElementType is { } elementType
            && (Nullable.GetUnderlyingType(elementType) ?? elementType) == typeof(FieldValue))
        {
            // The exporter describes a converter-written value as the empty schema, which reaches this node as no
            // additionalProperties at all, so the values map would otherwise accept anything and name no FieldValue.
            schema.Type = JsonSchemaType.Object | (schema.Type.GetValueOrDefault() & JsonSchemaType.Null);
            schema.AdditionalProperties = CreateFieldValueSchema();
        }
        else if (type == typeof(FieldType))
        {
            ApplyStringEnum(schema, FieldTypeWireNames);
        }
        else if (type == typeof(DiagnosticSeverity))
        {
            ApplyStringEnum(schema, DiagnosticSeverityWireNames);
        }
        else if (type == typeof(WriteOutcome))
        {
            ApplyStringEnum(schema, WriteOutcomeWireNames);
        }
        else if (type == typeof(RecordWriteRequest))
        {
            AddRequired(schema, [ValuesProperty]);
        }
        else if (type == typeof(FieldEventRequest))
        {
            AddRequired(schema, [EventProperty, FieldNameProperty, ValuesProperty]);
        }
        else if (type == typeof(SearchRequest))
        {
            AddRequired(schema, [CriteriaProperty]);
        }
        else if (typeof(WriteProblemDetails).IsAssignableFrom(type))
        {
            AddRequired(schema, [WriteOutcomeProperty]);
        }
        else if (type == typeof(NavigationPosition))
        {
            schema.Properties = new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal)
            {
                [PositionProperty] = new OpenApiSchema
                {
                    Type = JsonSchemaType.Integer | JsonSchemaType.Null,
                    Format = Int32Format,
                },
                [CountProperty] = new OpenApiSchema { Type = JsonSchemaType.Integer, Format = Int32Format },
            };
            AddRequired(schema, [PositionProperty, CountProperty]);
        }

        if (IsAlwaysSerialisedDomainObject(type, typeInfo))
        {
            AddRequired(schema, AlwaysSerialisedProperties(schema, typeInfo));
        }
    }

    private static void ApplyMemberRules(OpenApiSchema schema, JsonPropertyInfo property)
    {
        var declaringType = property.DeclaringType;
        var name = property.Name;

        if (declaringType == typeof(RecordWriteRequest) || declaringType == typeof(FieldEventRequest))
        {
            if (string.Equals(name, ValuesProperty, StringComparison.Ordinal))
            {
                // A null values is a request-level 400, so the map itself does not admit null; its entries do.
                Inline(schema);
                schema.Type = JsonSchemaType.Object;
                schema.AdditionalProperties = CreateFieldValueSchema();
            }
            else if (declaringType == typeof(FieldEventRequest)
                && string.Equals(name, EventProperty, StringComparison.Ordinal))
            {
                Inline(schema);
                schema.Type = JsonSchemaType.String;
                schema.Enum = CreateEnum(FieldEventWireNames);
            }
            else if (declaringType == typeof(FieldEventRequest)
                && string.Equals(name, FieldNameProperty, StringComparison.Ordinal))
            {
                Inline(schema);
                schema.Type = JsonSchemaType.String;
            }
        }
        else if (declaringType == typeof(SearchRequest)
            && string.Equals(name, CriteriaProperty, StringComparison.Ordinal))
        {
            // includeDeleted and excludeGlobalRecordId stay optional and nullable as generated.
            if (schema.Type is { } criteriaType)
            {
                schema.Type = criteriaType & ~JsonSchemaType.Null;
            }
        }
        else if (typeof(WriteProblemDetails).IsAssignableFrom(declaringType)
            && string.Equals(name, WriteOutcomeProperty, StringComparison.Ordinal))
        {
            // Inline whatever the CLR member is, a string or the WriteOutcome enum, so no WriteOutcome component
            // exists.
            Inline(schema);
            schema.Type = JsonSchemaType.String;
            schema.Enum = CreateEnum(WriteOutcomeWireNames);
        }
    }

    /// <summary>
    /// Points the 500 and 503 responses of a record write at <c>WriteProblemDetails</c>, as
    /// <c>application/problem+json</c>. Only operations carrying <see cref="WriteEndpointMetadata"/> are touched, and
    /// no other status, so every other error response keeps the ProblemDetails schema its endpoint declares.
    /// </summary>
    /// <param name="operation">The operation to rewrite in place.</param>
    /// <param name="context">The endpoint description and the document being built.</param>
    /// <param name="cancellationToken">Cancels the schema lookup.</param>
    /// <returns>A task that completes when the responses are rewritten.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="operation"/> or <paramref name="context"/> is <see langword="null"/>.
    /// </exception>
    public async Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(context);

        if (!IsRecordWrite(context.Description.ActionDescriptor.EndpointMetadata))
        {
            return;
        }

        // The lookup applies the schema role, so the body carries the writeOutcome enum and its required entry.
        var schema = await context.GetOrCreateSchemaAsync(typeof(WriteProblemDetails), null, cancellationToken)
            .ConfigureAwait(false);
        var problemSchema = WriteProblemDetailsReference(schema, context.Document);

        operation.Responses ??= new OpenApiResponses();
        foreach (var status in WriteFailureStatuses)
        {
            var key = status.ToString(CultureInfo.InvariantCulture);
            operation.Responses.TryGetValue(key, out var existing);
            var response = existing as OpenApiResponse ?? new OpenApiResponse
            {
                Description = existing?.Description ?? ReasonPhrases.GetReasonPhrase(status),
            };

            response.Content = new Dictionary<string, OpenApiMediaType>(StringComparer.Ordinal)
            {
                [ApiProblems.ProblemContentType] = new OpenApiMediaType { Schema = problemSchema },
            };
            operation.Responses[key] = response;
        }
    }

    /// <summary>
    /// Removes the <c>JsonElement</c> and <c>FieldValueKind</c> components when present. The schema role leaves
    /// nothing referencing them, so a reference that still did would surface as a dangling <c>$ref</c> that
    /// <c>openapi-typescript</c> rejects, rather than as a helper type in the client.
    /// </summary>
    /// <param name="document">The document to rewrite in place.</param>
    /// <param name="context">Not used.</param>
    /// <param name="cancellationToken">Not observed; the work is synchronous.</param>
    /// <returns>A completed task.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);

        // The framework sorts the components after the document transformers run, so only removal happens here.
        if (document.Components?.Schemas is { } schemas)
        {
            schemas.Remove(JsonElementSchemaId);
            schemas.Remove(FieldValueKindSchemaId);
        }

        return Task.CompletedTask;
    }

    private static bool IsRecordWrite(IList<object> endpointMetadata)
    {
        foreach (var item in endpointMetadata)
        {
            if (item is WriteEndpointMetadata)
            {
                return true;
            }
        }

        return false;
    }

    private static IOpenApiSchema WriteProblemDetailsReference(OpenApiSchema schema, OpenApiDocument? document)
    {
        if (document is null)
        {
            return schema;
        }

        if (document.Components?.Schemas?.ContainsKey(WriteProblemDetailsSchemaId) != true)
        {
            document.AddComponent(WriteProblemDetailsSchemaId, schema);
        }

        return new OpenApiSchemaReference(WriteProblemDetailsSchemaId, document);
    }

    // A new FieldValue node for a place the generator left without one. It carries the component id rather than being
    // an OpenApiSchemaReference: the framework resolves a reference placed in an unresolved schema into its target and
    // inlines it, while a node with the id is published as the one component and referenced by $ref.
    private static OpenApiSchema CreateFieldValueSchema() => new()
    {
        Type = FieldValueTypes,
        Metadata = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [SchemaIdMetadataKey] = FieldValueSchemaId,
        },
    };

    private static void ApplyStringEnum(OpenApiSchema schema, string[] wireNames)
    {
        // A null the generator placed on this node stays: only the value set changes.
        var nullable = schema.Type.GetValueOrDefault() & JsonSchemaType.Null;
        ResetShape(schema);
        schema.Type = JsonSchemaType.String | nullable;
        schema.Enum = CreateEnum(wireNames);
    }

    // Detaches a node from any component: without the id it stays inline, and without the nullable marker it is not
    // wrapped in oneOf [null, ...] when it is resolved.
    private static void Inline(OpenApiSchema schema)
    {
        if (schema.Metadata is { } metadata)
        {
            metadata.Remove(SchemaIdMetadataKey);
            metadata.Remove(NullablePropertyMetadataKey);
        }

        ResetShape(schema);
    }

    private static void ResetShape(OpenApiSchema schema)
    {
        schema.Properties = null;
        schema.Items = null;
        schema.Enum = null;
        schema.Format = null;
        schema.OneOf = null;
        schema.AnyOf = null;
        schema.AllOf = null;
    }

    private static void AddRequired(OpenApiSchema schema, IEnumerable<string> names)
    {
        if (schema.Properties is not { Count: > 0 } properties)
        {
            return;
        }

        var required = new SortedSet<string>(schema.Required ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
        var added = false;
        foreach (var name in names)
        {
            // Only a member the schema publishes is listed; required never names a property that does not exist.
            if (properties.ContainsKey(name))
            {
                added |= required.Add(name);
            }
        }

        if (added || schema.Required is not null)
        {
            schema.Required = required;
        }
    }

    // The EpiInfo.Domain assembly holds exactly the EpiInfo.Domain namespaces, so the assembly identifies its DTOs.
    private static bool IsAlwaysSerialisedDomainObject(Type type, JsonTypeInfo typeInfo) =>
        typeInfo.Kind == JsonTypeInfoKind.Object
        && typeInfo.Options.DefaultIgnoreCondition == JsonIgnoreCondition.Never
        && type.Assembly == typeof(FieldValue).Assembly;

    // The members the serializer writes on every instance, nulls included: readable and never conditionally skipped.
    private static IEnumerable<string> AlwaysSerialisedProperties(OpenApiSchema schema, JsonTypeInfo typeInfo)
    {
        foreach (var property in typeInfo.Properties)
        {
            if (property.Get is not null
                && property.ShouldSerialize is null
                && schema.Properties?.ContainsKey(property.Name) == true)
            {
                yield return property.Name;
            }
        }
    }

    // A fresh list of fresh nodes per schema: a JSON node can belong to one parent only.
    private static List<JsonNode> CreateEnum(string[] wireNames)
    {
        var values = new List<JsonNode>(wireNames.Length);
        foreach (var name in wireNames)
        {
            values.Add(JsonValue.Create(name));
        }

        return values;
    }

    private static string CamelCase(string memberName) => JsonNamingPolicy.CamelCase.ConvertName(memberName);

    private static string[] CamelCaseNames<TEnum>()
        where TEnum : struct, Enum
    {
        var values = Enum.GetValues<TEnum>();
        var names = new string[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            names[i] = CamelCase(values[i].ToString());
        }

        return names;
    }
}
