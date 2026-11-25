using System.Text.Json;
using System.Text.Json.Serialization;
using EpiInfo.Domain.Diagnostics;
using EpiInfo.Domain.Forms;
using EpiInfo.Domain.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace EpiInfo.Api.Json;

/// <summary>
/// The single definition of the API's wire JSON settings. <see cref="AddApiJson"/> applies them to the options that
/// minimal-API binding, typed results, the ProblemDetails writer and OpenAPI schema generation share, and tests use
/// <see cref="CreateSerializerOptions"/> to read responses exactly as the server writes them.
/// </summary>
/// <remarks>
/// <para>The settings, on top of <see cref="JsonSerializerDefaults.Web"/>:</para>
/// <list type="bullet">
/// <item><description>
/// Property names are camelCase. Dictionary keys keep their spelling, because <c>values</c>, <c>assigned</c> and the
/// ProblemDetails <c>errors</c> map are keyed by the canonical <c>metaFields.Name</c>, such as <c>LastName</c>.
/// </description></item>
/// <item><description>
/// Numbers are strict: a string such as <c>"2"</c> is never read as a number, NaN and Infinity are neither read nor
/// written, and the OpenAPI document describes integers as <c>integer</c> rather than <c>["integer","string"]</c>.
/// </description></item>
/// <item><description>
/// Enum spellings are set per type and integers are refused: <see cref="FieldType"/> and
/// <see cref="DiagnosticSeverity"/> are camelCase strings, and <see cref="WriteOutcome"/> uses the spellings of
/// <see cref="WriteOutcomeExtensions.ToWireValue"/>. There is no global enum policy, and the field-event kind has no
/// converter: the <c>event</c> member binds as raw JSON and is parsed ordinally by the field-event service, so a
/// number or another spelling becomes <c>errors["event"]</c> rather than a binding failure.
/// </description></item>
/// <item><description>
/// A field value is the plain scalar <c>string | number | boolean | null</c> written by
/// <see cref="FieldValueJsonConverter"/>; a nullable field value such as a bound is handled by the serializer's
/// nullable wrapper around it.
/// </description></item>
/// </list>
/// <para>
/// The web defaults left in place are part of the contract: <c>null</c> members are written, not omitted; duplicate
/// properties are allowed, so keys of <c>values</c> that differ only by case reach the record validator; unknown
/// members are skipped; and <c>RespectRequiredConstructorParameters</c> and <c>RespectNullableAnnotations</c> stay off,
/// so a missing or <c>null</c> request member reaches its documented 400 instead of failing binding.
/// </para>
/// </remarks>
public static class JsonSetup
{
    /// <summary>
    /// Applies <see cref="Apply"/> to the HTTP JSON options of minimal APIs
    /// (<c>Microsoft.AspNetCore.Http.Json.JsonOptions</c>). MVC options are not configured, because MVC is not used.
    /// </summary>
    /// <param name="services">The application's service collection.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddApiJson(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.ConfigureHttpJsonOptions(options => Apply(options.SerializerOptions));
        return services;
    }

    /// <summary>
    /// Applies the API's wire settings to <paramref name="options"/>. Calling it again on the same options changes
    /// nothing, because each converter is added only when no converter of its type is present.
    /// </summary>
    /// <param name="options">
    /// Options created from <see cref="JsonSerializerDefaults.Web"/> that have not been used yet: options become
    /// read-only on first use, so this must run before the first serialisation or deserialisation.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="options"/> is already read-only.</exception>
    public static void Apply(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.DictionaryKeyPolicy = null;
        options.NumberHandling = JsonNumberHandling.Strict;

        var converters = options.Converters;
        AddOnce(
            converters,
            static () => new JsonStringEnumConverter<FieldType>(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        AddOnce(
            converters,
            static () => new JsonStringEnumConverter<DiagnosticSeverity>(
                JsonNamingPolicy.CamelCase,
                allowIntegerValues: false));
        AddOnce(converters, static () => new WriteOutcomeJsonConverter());
        AddOnce(converters, static () => new FieldValueJsonConverter());
    }

    /// <summary>
    /// Creates new options holding the API's wire settings: <see cref="JsonSerializerDefaults.Web"/> with
    /// <see cref="Apply"/> applied.
    /// </summary>
    /// <returns>Options not yet used, so a caller may still adjust them before the first serialisation.</returns>
    public static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Apply(options);
        return options;
    }

    private static void AddOnce<TConverter>(IList<JsonConverter> converters, Func<TConverter> create)
        where TConverter : JsonConverter
    {
        foreach (var converter in converters)
        {
            if (converter is TConverter)
            {
                return;
            }
        }

        converters.Add(create());
    }

    /// <summary>
    /// Writes and reads <see cref="WriteOutcome"/> as its wire spelling only. <c>ApiProblems</c> writes the string
    /// itself; this converter keeps an enum value placed in a ProblemDetails extension on the same spelling.
    /// </summary>
    /// <remarks>
    /// Reading accepts exactly <c>notWritten</c>, <c>unknown</c> and <c>written</c>, compared ordinally. Any other
    /// token, an integer included, fails with a fixed message that does not echo the input.
    /// </remarks>
    private sealed class WriteOutcomeJsonConverter : JsonConverter<WriteOutcome>
    {
        private const string InvalidValueMessage =
            "A write outcome must be one of the strings notWritten, unknown or written.";

        public override WriteOutcome Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException(InvalidValueMessage);
            }

            return reader.GetString() switch
            {
                WriteOutcomeExtensions.WireNotWritten => WriteOutcome.NotWritten,
                WriteOutcomeExtensions.WireUnknown => WriteOutcome.Unknown,
                WriteOutcomeExtensions.WireWritten => WriteOutcome.Written,
                _ => throw new JsonException(InvalidValueMessage),
            };
        }

        public override void Write(Utf8JsonWriter writer, WriteOutcome value, JsonSerializerOptions options)
        {
            ArgumentNullException.ThrowIfNull(writer);

            writer.WriteStringValue(value.ToWireValue());
        }
    }
}
