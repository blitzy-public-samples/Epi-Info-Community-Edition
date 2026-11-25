using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using EpiInfo.Domain.Values;

namespace EpiInfo.Api.Json;

/// <summary>
/// Converts a <see cref="FieldValue"/> to and from the plain JSON scalar the wire contract describes as
/// <c>FieldValue</c>: <c>string | number | boolean | null</c>. It serves <c>values</c>, <c>assigned</c>,
/// criterion values, option values and the <c>lower</c>/<c>upper</c> bounds.
/// </summary>
/// <remarks>
/// <para>
/// Writing: null is <c>null</c>; text and legal values are strings; a number is a JSON number, written digit for
/// digit when it holds an exact integer and in round-trip form otherwise; a checkbox is a boolean; a date is the
/// string <c>YYYY-MM-DD</c> with no time of day; an option is its 0-based index as a JSON integer.
/// </para>
/// <para>
/// Reading is raw and field-type blind: a string becomes text, even when it holds an ISO date; a number becomes a
/// finite number value kept exact, so a whole number within the signed 64-bit range keeps its exact integer, even
/// above 2^53, and any other number keeps its nearest double without being rounded into a whole number; a boolean
/// becomes a boolean. Interpreting a value for its field type belongs to <c>RecordValidator</c>,
/// <c>SearchCriteriaParser</c> and <c>ValuesReader.ToTyped</c>.
/// </para>
/// <para>
/// Exception messages are fixed text and never echo the value read or written, because field data is kept out of
/// error bodies and logs. The class holds no state, so one instance can be shared across threads.
/// </para>
/// </remarks>
public sealed class FieldValueJsonConverter : JsonConverter<FieldValue>
{
    /// <summary>The message for an object, an array or any other non-scalar token.</summary>
    internal const string NonScalarMessage = "A field value must be a string, number, boolean or null.";

    /// <summary>The message for a number that is not a finite double, such as <c>1e400</c>.</summary>
    internal const string NonFiniteMessage = "A field value number must be a finite number.";

    /// <summary>The message for a value whose kind this converter does not know.</summary>
    internal const string UnknownKindMessage = "A field value has a kind that cannot be written as JSON.";

    /// <summary>
    /// Always <see langword="true"/>, so a JSON <c>null</c> reaches <see cref="Read"/> and becomes
    /// <see cref="FieldValue.Null"/> instead of a serializer error on non-nullable members.
    /// </summary>
    public override bool HandleNull => true;

    /// <inheritdoc />
    public override FieldValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return FieldValue.Null;

            case JsonTokenType.String:
                // GetString() throws for text it cannot decode, such as malformed UTF-8 or an escaped lone
                // surrogate: the reader tags that InvalidOperationException as rethrowable, and the serializer
                // rethrows it as a JsonException during binding, so the body fails as malformed JSON. Any other
                // string is kept verbatim: trimming and empty-to-null normalisation are validation rules.
                return FieldValue.FromText(reader.GetString()!);

            case JsonTokenType.Number:
                // Exact and field-type blind: a whole number within the signed 64-bit range keeps its exact
                // integer, and any other number keeps its nearest double without being rounded into a whole
                // number; the per-type owners decide what the field accepts. The reader has already checked the
                // JSON number grammar. TryGetDouble would succeed with infinity for 1e400, so it is not used: the
                // parse rejects a number that is not finite by its own double.IsFinite check.
                ReadOnlySpan<byte> literal = reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan;
                if (FieldValueCoercion.TryParseNumber(literal, out var number))
                {
                    return number;
                }

                throw new JsonException(NonFiniteMessage);

            case JsonTokenType.True:
                return FieldValue.FromBoolean(true);

            case JsonTokenType.False:
                return FieldValue.FromBoolean(false);

            default:
                // The token is neither skipped nor quoted, so a nested object or array fails the whole body.
                throw new JsonException(NonScalarMessage);
        }
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, FieldValue value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        switch (value.Kind)
        {
            case FieldValueKind.Null:
                writer.WriteNullValue();
                break;

            case FieldValueKind.Text:
                writer.WriteStringValue(value.AsText());
                break;

            case FieldValueKind.Number:
                if (value.TryGetInteger(out var integer))
                {
                    // An exact integer is written digit for digit, so a bigint beyond 2^53 is not rounded.
                    writer.WriteNumberValue(integer);
                }
                else
                {
                    var number = value.AsNumber();
                    if (!double.IsFinite(number))
                    {
                        // The factories admit finite values only; this keeps NaN and Infinity off the wire
                        // regardless.
                        throw new JsonException(NonFiniteMessage);
                    }

                    writer.WriteNumberValue(number);
                }

                break;

            case FieldValueKind.Boolean:
                writer.WriteBooleanValue(value.AsBoolean());
                break;

            case FieldValueKind.Date:
                // Date fields are date-only on the wire: never a time of day, whatever the column type stores.
                writer.WriteStringValue(FieldValue.FormatIsoDate(value.AsDate()));
                break;

            case FieldValueKind.Index:
                writer.WriteNumberValue(value.AsIndex());
                break;

            default:
                throw new JsonException(UnknownKindMessage);
        }
    }
}
