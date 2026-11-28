using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using EpiInfo.Api.Contracts;
using EpiInfo.Api.Endpoints;
using EpiInfo.Api.Errors;
using EpiInfo.Api.Hosting;
using EpiInfo.Domain.Diagnostics;
using EpiInfo.Domain.Events;
using EpiInfo.Domain.Forms;
using EpiInfo.Domain.Records;
using EpiInfo.Domain.Storage;
using EpiInfo.Domain.Values;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
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
/// <c>values</c> map, <c>assigned</c> and a criterion <c>value</c> reference it, and no member of the tagged
/// <see cref="FieldValue"/> struct (<see cref="FieldValueKind"/>, <c>kind</c>, <c>isNull</c>) appears.
/// </description></item>
/// <item><description>
/// The form definition's two narrower <see cref="FieldValue"/> members, inline and without the union. A validation
/// <c>lower</c>/<c>upper</c> bound is <c>oneOf</c> <c>null</c>, a number and a <c>date</c>-format string, because it is
/// <c>null</c> when unset, a number on a number field and <c>YYYY-MM-DD</c> on a date field, and never a boolean. An
/// option <c>value</c> is <c>oneOf</c> an <c>int32</c> of at least 0 and a string, because an Option item's value is
/// its 0-based index and a Legal Values item's its stored text, and neither is ever <c>null</c>. The branches of each
/// are disjoint by type, so a value matches exactly one, as <c>oneOf</c> requires.
/// </description></item>
/// <item><description>
/// The request bodies whose members bind as raw <see cref="JsonElement"/>: <c>values</c> is an object of
/// <c>FieldValue</c>, <c>event</c> the string enum <c>FieldBefore</c>, <c>FieldAfter</c>, <c>FieldClick</c>, and
/// <c>fieldName</c> a string, each required, so no <see cref="JsonElement"/> component exists. The search body's
/// <c>criteria</c> is a required, non-null array of 1 to <see cref="RecordQueryRules.MaxSearchFields"/> criteria,
/// because an empty list, a seventh field and a field named twice are each a 400; its <c>includeDeleted</c> and
/// <c>excludeGlobalRecordId</c> stay optional. The four operations carrying <see cref="JsonBodyMetadata"/> publish
/// their body as required and as a plain <c>$ref</c> to its component, without the <c>null</c> branch a nullable
/// body parameter would publish, because a missing or <c>null</c> body is a 400.
/// </description></item>
/// <item><description>
/// The search response's <c>items</c> as an array of at most <see cref="RecordQueryRules.SearchCap"/> results, the
/// cap after which <c>total</c> goes on counting and <c>truncated</c> is set.
/// </description></item>
/// <item><description>
/// <c>HealthResponse</c> with <c>status</c> the required string enum <c>healthy</c>, <c>unhealthy</c>. The handler
/// writes one fixed body per status, so the 200 and the 503 of <c>GET /health</c> each publish <c>allOf</c> the
/// component and that status's own literal, <c>healthy</c> and <c>unhealthy</c>.
/// </description></item>
/// <item><description>
/// The camelCase string enums <c>FieldType</c> and <c>DiagnosticSeverity</c>, and the <c>writeOutcome</c> enum
/// <c>notWritten</c>, <c>unknown</c>, <c>written</c> of <c>WriteProblemDetails</c>, the schema of the 500 and 503
/// responses of the three record writes and of no other response.
/// </description></item>
/// <item><description>
/// <c>ProblemDetails</c>, <c>HttpValidationProblemDetails</c> and <c>WriteProblemDetails</c> with <c>type</c>,
/// <c>title</c>, <c>status</c>, <c>detail</c> and the <c>traceId</c> extension as required, non-null members, and
/// <c>errors</c> required on the validation variant; <c>instance</c> stays optional. The plain variant never carries
/// <c>errors</c>, so it publishes <c>not: {required: ["errors"]}</c> while every other extension stays open. The 400
/// of each JSON-body operation is <c>anyOf</c> the plain and the validation variant, because it answers with either;
/// every other 400 keeps the validation variant its endpoint declares.
/// </description></item>
/// <item><description>
/// The required <c>Location</c> header, a <c>uri-reference</c> string, on the 201 of the record create and of no other
/// response.
/// </description></item>
/// <item><description>
/// <c>NavigationPosition</c> as <c>{position: integer | null, count: integer}</c>, and every Domain DTO member the API
/// always writes as required, so the generated TypeScript members are non-optional; a member that may be
/// <c>null</c> keeps <c>null</c> in its type.
/// </description></item>
/// <item><description>
/// <c>info.description</c>, the precondition of every request on every path: its <c>Host</c> names
/// <see cref="LoopbackBindingGuard.AllowedHost"/> or <see cref="LoopbackBindingGuard.LocalhostName"/>, in any case
/// and with any port, and any other is answered before routing with the request-level 400
/// <see cref="ApiProblems.HostNotAllowedDetail"/>. That 400 is stated once for the document and not under each
/// operation, so every operation keeps the statuses its endpoint declares.
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

    // The component ids the framework gives the plain and the field-level ProblemDetails: their type names.
    private const string ProblemDetailsSchemaId = nameof(ProblemDetails);
    private const string ValidationProblemDetailsSchemaId = nameof(HttpValidationProblemDetails);

    // The component id the framework gives the /health body: its type name.
    private const string HealthResponseSchemaId = nameof(HealthResponse);

    // Metadata keys of Microsoft.AspNetCore.OpenApi: a node holding the first becomes the component of that id, and a
    // property node holding the second is wrapped as oneOf [null, $ref] when it is resolved.
    private const string SchemaIdMetadataKey = "x-schema-id";
    private const string NullablePropertyMetadataKey = "x-is-nullable-property";

    // The component ids of CLR helpers that must never be published; the schema role keeps both unreferenced.
    private const string JsonElementSchemaId = nameof(JsonElement);
    private const string FieldValueKindSchemaId = nameof(FieldValueKind);

    private const string Int32Format = "int32";
    private const string DateFormat = "date";
    private const string UriReferenceFormat = "uri-reference";

    // The first Option index: an option's value is its 0-based position in metaFields.List.
    private const int MinOptionIndex = 0;

    // The fewest criteria a search may carry; RecordService answers an empty list with a 400.
    private const int MinSearchCriteria = 1;

    private const string CreatedLocationDescription =
        "The relative URI of the created record, /api/forms/{viewId}/records/{globalRecordId}, with the id "
        + "percent-encoded.";

    // The Host precondition that LoopbackBindingGuard.UseLoopbackHostGuard enforces ahead of routing, outside every
    // operation's metadata, so the document states it once instead of as a 400 under each operation.
    private const string HostPreconditionDescription =
        "The API is for local single-user use. Every request on every path, the /api operations, /health, this "
        + $"document and the client included, must carry a Host naming {LoopbackBindingGuard.AllowedHost} or "
        + $"{LoopbackBindingGuard.LocalhostName}, in any letter case and with any port. A request with any other Host "
        + $"is answered before routing with the request-level 400, an {ApiProblems.ProblemContentType} ProblemDetails "
        + $"whose detail is \"{ApiProblems.HostNotAllowedDetail}\". That response applies to every operation and is "
        + "not repeated under each one.";

    // The JSON name WriteProblemDetails declares for its outcome member.
    private const string WriteOutcomeProperty = "writeOutcome";

    // The JSON name HttpValidationProblemDetails declares for its field-error map.
    private const string ErrorsProperty = "errors";

    private const JsonSchemaType FieldValueTypes =
        JsonSchemaType.String | JsonSchemaType.Number | JsonSchemaType.Boolean | JsonSchemaType.Null;

    // Wire names follow the camelCase property policy that JsonSetup applies to the shared HTTP JSON options.
    private static readonly string ValuesProperty = CamelCase(nameof(RecordWriteRequest.Values));
    private static readonly string EventProperty = CamelCase(nameof(FieldEventRequest.Event));
    private static readonly string FieldNameProperty = CamelCase(nameof(FieldEventRequest.FieldName));
    private static readonly string CriteriaProperty = CamelCase(nameof(SearchRequest.Criteria));
    private static readonly string SearchItemsProperty = CamelCase(nameof(SearchResponse.Items));
    private static readonly string PositionProperty = CamelCase(nameof(NavigationPosition.Position));
    private static readonly string CountProperty = CamelCase(nameof(NavigationPosition.Count));
    private static readonly string LowerProperty = CamelCase(nameof(FieldValidation.Lower));
    private static readonly string UpperProperty = CamelCase(nameof(FieldValidation.Upper));
    private static readonly string OptionValueProperty = CamelCase(nameof(FieldOption.Value));
    private static readonly string HealthStatusProperty = CamelCase(nameof(HealthResponse.Status));

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

    private static readonly string[] HealthStatusWireNames =
    [
        HealthResponse.HealthyStatus,
        HealthResponse.UnhealthyStatus,
    ];

    // The JSON names ProblemDetails declares for the members ApiProblems always writes, in declaration order.
    private static readonly string[] ProblemNonNullProperties = ["type", "title", "status", "detail"];

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
    /// The schema role does the essential work; the operation role corrects the JSON request bodies and their 400, adds
    /// the create's <c>Location</c> header, repoints the write 500 and 503 responses and narrows each <c>/health</c>
    /// response to its status literal, and the document role drops helper components and states the <c>Host</c>
    /// precondition as <c>info.description</c>. Each role is correct on its own, so none depends on the order in which
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
    /// member's node; on a <see cref="FieldValue"/> member they run instead of the type rule, which would otherwise
    /// make the member a reference to the union. Query and route parameter schemas are left to
    /// <c>ParameterSchemaTransformer</c>.
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
            // A member whose values are narrower than the union is published in its own inline shape before the type
            // rule could make it a reference to the union; every other occurrence is the one FieldValue component.
            if (context.JsonPropertyInfo is not { } valueMember || !ApplyFieldValueMemberRules(schema, valueMember))
            {
                ApplyFieldValue(schema);
            }

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

    // The form definition carries some values as FieldValue in the Domain although the factory only ever writes a
    // narrower set into them. Returns whether a rule matched the member.
    private static bool ApplyFieldValueMemberRules(OpenApiSchema schema, JsonPropertyInfo property)
    {
        var name = property.Name;

        if (property.DeclaringType == typeof(FieldValidation)
            && (string.Equals(name, LowerProperty, StringComparison.Ordinal)
                || string.Equals(name, UpperProperty, StringComparison.Ordinal)))
        {
            // An unset bound is null, a number field's bound a number and a date field's a YYYY-MM-DD string, never a
            // boolean. The branches are disjoint by type, so every bound matches exactly one, as oneOf requires; a null
            // branch beside the union, which admits null itself, made an unset bound match two.
            PublishOneOf(
                schema,
                [
                    new OpenApiSchema { Type = JsonSchemaType.Null },
                    new OpenApiSchema { Type = JsonSchemaType.Number },
                    new OpenApiSchema { Type = JsonSchemaType.String, Format = DateFormat },
                ]);
            return true;
        }

        if (property.DeclaringType == typeof(FieldOption)
            && string.Equals(name, OptionValueProperty, StringComparison.Ordinal))
        {
            // An Option item's value is its 0-based index and a Legal Values item's the stored text; neither is ever
            // null, a boolean or a fraction.
            PublishOneOf(
                schema,
                [
                    new OpenApiSchema
                    {
                        Type = JsonSchemaType.Integer,
                        Format = Int32Format,
                        Minimum = MinOptionIndex.ToString(CultureInfo.InvariantCulture),
                    },
                    new OpenApiSchema { Type = JsonSchemaType.String },
                ]);
            return true;
        }

        return false;
    }

    // Publishes the member as an inline oneOf of the branches, with no type of its own and no component behind it.
    private static void PublishOneOf(OpenApiSchema schema, IList<IOpenApiSchema> branches)
    {
        Inline(schema);
        schema.Type = null;
        schema.AdditionalProperties = null;
        schema.Required = null;
        schema.OneOf = branches;
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
        else if (typeof(ProblemDetails).IsAssignableFrom(type))
        {
            ApplyProblemDetails(schema, type);
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
        else if (type == typeof(HealthResponse))
        {
            // The handler writes one of two fixed bodies; the operation role narrows each response to its own status.
            schema.Properties = new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal)
            {
                [HealthStatusProperty] = CreateStatusSchema(HealthStatusWireNames),
            };
            AddRequired(schema, [HealthStatusProperty]);
        }

        if (IsAlwaysSerialisedDomainObject(type, typeInfo))
        {
            AddRequired(schema, AlwaysSerialisedProperties(schema, typeInfo));
        }
    }

    // ApiProblems writes every /api error body with a type, title, status and detail and adds the traceId extension, so
    // each ProblemDetails variant publishes those five as required, non-null members. traceId lives in the extension
    // data, which the exporter does not describe, so it is declared here. instance is never written and stays optional.
    // The plain variant never carries errors, because CreateProblem adds none and Customize strips every extension but
    // traceId; it excludes the member, so only the validation variant admits a field-error map.
    private static void ApplyProblemDetails(OpenApiSchema schema, Type type)
    {
        if (schema.Properties is not { } properties)
        {
            return;
        }

        foreach (var name in ProblemNonNullProperties)
        {
            if (properties.TryGetValue(name, out var member) && member is OpenApiSchema { Type: { } memberType } node)
            {
                node.Type = memberType & ~JsonSchemaType.Null;
            }
        }

        properties[ApiProblems.TraceIdKey] = new OpenApiSchema { Type = JsonSchemaType.String };
        AddRequired(schema, [.. ProblemNonNullProperties, ApiProblems.TraceIdKey]);

        if (typeof(HttpValidationProblemDetails).IsAssignableFrom(type))
        {
            AddRequired(schema, [ErrorsProperty]);
        }
        else if (typeof(WriteProblemDetails).IsAssignableFrom(type))
        {
            AddRequired(schema, [WriteOutcomeProperty]);
        }
        else if (type == typeof(ProblemDetails))
        {
            // not, rather than a closed schema, so traceId and any other extension stay allowed while a body carrying
            // errors, whatever its value, can only match the validation variant.
            schema.Not = new OpenApiSchema
            {
                Required = new SortedSet<string>([ErrorsProperty], StringComparer.Ordinal),
            };
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

            // An empty list is a request-level 400, and a seventh criterion names either a seventh field or one field
            // twice, which are 400s as well.
            schema.MinItems = MinSearchCriteria;
            schema.MaxItems = RecordQueryRules.MaxSearchFields;
        }
        else if (declaringType == typeof(SearchResponse)
            && string.Equals(name, SearchItemsProperty, StringComparison.Ordinal))
        {
            // total counts every match, while the items stop at the cap.
            schema.MaxItems = RecordQueryRules.SearchCap;
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
    /// Rewrites the parts of an operation that endpoint metadata cannot express. An operation carrying
    /// <see cref="JsonBodyMetadata"/> gets a required, non-null request body and a 400 that is <c>anyOf</c>
    /// <c>ProblemDetails</c> and <c>HttpValidationProblemDetails</c>. An operation carrying
    /// <see cref="WriteEndpointMetadata"/> gets its 500 and 503 pointed at <c>WriteProblemDetails</c>, and the create,
    /// <see cref="WriteEndpointMetadata.Insert"/>, the required <c>Location</c> header on its 201. A 200 or 503
    /// declared with <c>HealthResponse</c> gets the component narrowed to the status literal of its status. Every
    /// problem body is <c>application/problem+json</c>, and every other response keeps the schema its endpoint
    /// declares.
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

        await NarrowHealthResponsesAsync(operation, context, cancellationToken).ConfigureAwait(false);

        var endpointMetadata = context.Description.ActionDescriptor.EndpointMetadata;
        if (HasMetadata<JsonBodyMetadata>(endpointMetadata))
        {
            RequireJsonBody(operation);

            // A body endpoint answers 400 with the request-level ProblemDetails or the field-level
            // HttpValidationProblemDetails. The plain variant excludes errors and the validation variant requires it,
            // so any body matches at most one branch.
            var plain = await ComponentReferenceAsync(
                    typeof(ProblemDetails),
                    ProblemDetailsSchemaId,
                    context,
                    cancellationToken)
                .ConfigureAwait(false);
            var validation = await ComponentReferenceAsync(
                    typeof(HttpValidationProblemDetails),
                    ValidationProblemDetailsSchemaId,
                    context,
                    cancellationToken)
                .ConfigureAwait(false);
            SetProblemResponse(
                operation,
                StatusCodes.Status400BadRequest,
                new OpenApiSchema { AnyOf = [plain, validation] });
        }

        if (!HasMetadata<WriteEndpointMetadata>(endpointMetadata))
        {
            return;
        }

        if (HasInstance(endpointMetadata, WriteEndpointMetadata.Insert))
        {
            AddCreatedLocation(operation);
        }

        // The lookup applies the schema role, so the body carries the writeOutcome enum and its required entry.
        var problemSchema = await ComponentReferenceAsync(
                typeof(WriteProblemDetails),
                WriteProblemDetailsSchemaId,
                context,
                cancellationToken)
            .ConfigureAwait(false);
        foreach (var status in WriteFailureStatuses)
        {
            SetProblemResponse(operation, status, problemSchema);
        }
    }

    /// <summary>
    /// Removes the <c>JsonElement</c> and <c>FieldValueKind</c> components when present, and states the <c>Host</c>
    /// precondition of every request as <c>info.description</c>. The schema role leaves nothing referencing the two
    /// components, so a reference that still did would surface as a dangling <c>$ref</c> that
    /// <c>openapi-typescript</c> rejects, rather than as a helper type in the client. The precondition is answered by
    /// <see cref="LoopbackBindingGuard.UseLoopbackHostGuard"/> before routing, so no operation's metadata can carry it.
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

        // The framework fills info before any document transformer runs; a document without one is left as generated.
        if (document.Info is { } info)
        {
            info.Description = HostPreconditionDescription;
        }

        return Task.CompletedTask;
    }

    private static bool HasMetadata<TMetadata>(IList<object> endpointMetadata)
        where TMetadata : class
    {
        foreach (var item in endpointMetadata)
        {
            if (item is TMetadata)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasInstance(IList<object> endpointMetadata, object instance)
    {
        foreach (var item in endpointMetadata)
        {
            if (ReferenceEquals(item, instance))
            {
                return true;
            }
        }

        return false;
    }

    // RecordService answers a create with 201 and the Location of the new record, so the declared 201 publishes it as a
    // required header. A missing 201 is left absent: the body schema belongs to the endpoint's own metadata.
    private static void AddCreatedLocation(OpenApiOperation operation)
    {
        var key = StatusCodes.Status201Created.ToString(CultureInfo.InvariantCulture);
        if (operation.Responses is null
            || !operation.Responses.TryGetValue(key, out var existing)
            || existing is not OpenApiResponse response)
        {
            return;
        }

        response.Headers ??= new Dictionary<string, IOpenApiHeader>(StringComparer.Ordinal);
        response.Headers[HeaderNames.Location] = new OpenApiHeader
        {
            Description = CreatedLocationDescription,
            Required = true,
            Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = UriReferenceFormat },
        };
    }

    // GET /health writes one fixed body per status, so each response declared with HealthResponse publishes the
    // component narrowed to the literal of its status, and a swapped or unknown status fails validation. A response
    // the endpoint does not declare, or a status with no fixed body, keeps its schema.
    private static async Task NarrowHealthResponsesAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        if (operation.Responses is not { } responses)
        {
            return;
        }

        IOpenApiSchema? component = null;
        foreach (var responseType in context.Description.SupportedResponseTypes)
        {
            if (responseType.Type != typeof(HealthResponse)
                || HealthStatusOf(responseType.StatusCode) is not { } status
                || !responses.TryGetValue(
                    responseType.StatusCode.ToString(CultureInfo.InvariantCulture),
                    out var existing)
                || existing is not OpenApiResponse { Content: { } content })
            {
                continue;
            }

            component ??= await ComponentReferenceAsync(
                    typeof(HealthResponse),
                    HealthResponseSchemaId,
                    context,
                    cancellationToken)
                .ConfigureAwait(false);
            foreach (var mediaType in content.Values)
            {
                mediaType.Schema = new OpenApiSchema
                {
                    AllOf =
                    [
                        component,
                        new OpenApiSchema
                        {
                            Type = JsonSchemaType.Object,
                            Properties = new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal)
                            {
                                [HealthStatusProperty] = CreateStatusSchema([status]),
                            },
                            Required = new SortedSet<string>([HealthStatusProperty], StringComparer.Ordinal),
                        },
                    ],
                };
            }
        }
    }

    private static string? HealthStatusOf(int statusCode) => statusCode switch
    {
        StatusCodes.Status200OK => HealthResponse.HealthyStatus,
        StatusCodes.Status503ServiceUnavailable => HealthResponse.UnhealthyStatus,
        _ => null,
    };

    private static OpenApiSchema CreateStatusSchema(string[] statuses) => new()
    {
        Type = JsonSchemaType.String,
        Enum = CreateEnum(statuses),
    };

    // The four JSON-body endpoints read their body themselves, so that a JSON null reaches the service's request-level
    // 400, and declare it through Accepts metadata. A nullable body parameter would instead be published as an
    // optional body whose schema is oneOf [null, $ref]. On the wire the body is a required, non-null object, so it is
    // marked required and any such null branch is dropped.
    private static void RequireJsonBody(OpenApiOperation operation)
    {
        if (operation.RequestBody is not OpenApiRequestBody body)
        {
            return;
        }

        body.Required = true;
        if (body.Content is not { } content)
        {
            return;
        }

        foreach (var mediaType in content.Values)
        {
            if (mediaType.Schema is OpenApiSchema schema && NonNullMember(schema) is { } member)
            {
                mediaType.Schema = member;
            }
        }
    }

    // The other member of a oneOf [null, member] wrapper with no shape of its own; null for any other node, which then
    // stays as generated.
    private static IOpenApiSchema? NonNullMember(OpenApiSchema schema)
    {
        if (schema.Type is not null || schema.OneOf is not { Count: 2 } members)
        {
            return null;
        }

        var firstIsNull = IsNullOnly(members[0]);
        var secondIsNull = IsNullOnly(members[1]);
        if (firstIsNull == secondIsNull)
        {
            return null;
        }

        return firstIsNull ? members[1] : members[0];
    }

    private static bool IsNullOnly(IOpenApiSchema? schema) => schema is OpenApiSchema { Type: JsonSchemaType.Null };

    // A $ref to the component of the type, which is published from the type's schema when no response has published it
    // yet. The lookup applies the schema role, so the component carries every rule of its type.
    private static async Task<IOpenApiSchema> ComponentReferenceAsync(
        Type type,
        string schemaId,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        var schema = await context.GetOrCreateSchemaAsync(type, null, cancellationToken).ConfigureAwait(false);
        var document = context.Document;
        if (document is null)
        {
            return schema;
        }

        if (document.Components?.Schemas?.ContainsKey(schemaId) != true)
        {
            document.AddComponent(schemaId, schema);
        }

        return new OpenApiSchemaReference(schemaId, document);
    }

    // Makes the one application/problem+json schema the content of the status's response, adding the response with its
    // reason phrase when the endpoint declares none.
    private static void SetProblemResponse(OpenApiOperation operation, int status, IOpenApiSchema schema)
    {
        operation.Responses ??= new OpenApiResponses();
        var key = status.ToString(CultureInfo.InvariantCulture);
        operation.Responses.TryGetValue(key, out var existing);
        var response = existing as OpenApiResponse ?? new OpenApiResponse
        {
            Description = existing?.Description ?? ReasonPhrases.GetReasonPhrase(status),
        };

        response.Content = new Dictionary<string, OpenApiMediaType>(StringComparer.Ordinal)
        {
            [ApiProblems.ProblemContentType] = new OpenApiMediaType { Schema = schema },
        };
        operation.Responses[key] = response;
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
