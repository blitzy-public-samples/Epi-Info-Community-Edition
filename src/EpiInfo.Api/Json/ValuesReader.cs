using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using EpiInfo.Api.Errors;
using EpiInfo.Domain.Metadata;
using EpiInfo.Domain.Validation;
using EpiInfo.Domain.Values;

namespace EpiInfo.Api.Json;

/// <summary>
/// The outcome of <see cref="ValuesReader.ReadRaw"/>: the entries of a <c>values</c> object as field-type blind
/// scalars, or why the object cannot be read.
/// </summary>
/// <remarks>
/// <para>Exactly one of three states holds, and the factories are the only way to reach one:</para>
/// <list type="bullet">
///   <item><description>
///     success (<see cref="Success"/>): <see cref="IsSuccess"/> is <see langword="true"/>, <see cref="Values"/>
///     holds every entry, <see cref="RequestError"/> is <see langword="null"/> and <see cref="FieldErrorsFor"/>
///     returns an empty map;
///   </description></item>
///   <item><description>
///     request-level failure (<see cref="RequestFailure"/>): <see cref="RequestError"/> is the <c>detail</c> of a 400
///     with no <c>errors</c> map, <see cref="Values"/> is empty and <see cref="FieldErrorsFor"/> returns an empty map;
///   </description></item>
///   <item><description>
///     field-level failure (<see cref="FieldFailure"/>): the result holds every property of the object, each with its
///     fault or none, at least one faulted, and <see cref="FieldErrorsFor"/> resolves them into the non-empty
///     <c>errors</c> map of a validation 400; <see cref="RequestError"/> is <see langword="null"/> and
///     <see cref="Values"/> is empty.
///   </description></item>
/// </list>
/// <para>
/// Every collection passed to a factory is copied, so instances are immutable and safe to share across threads.
/// Error texts are fixed, so a failure never carries a submitted value. The submitted names of a field-level failure
/// are kept privately, only to be resolved against the form's metadata, and are never exposed:
/// <see cref="FieldErrorsFor"/> keys every error by a canonical <c>metaFields.Name</c> or by
/// <see cref="RecordValidator.ValuesKey"/> (<see cref="RecordValidator.NotADataFieldError"/>).
/// </para>
/// </remarks>
public sealed class RawValuesResult
{
    private static readonly IReadOnlyList<KeyValuePair<string, string?>> NoProperties =
        ReadOnlyCollection<KeyValuePair<string, string?>>.Empty;

    // Every property of a field-level failure as (submitted name, fixed fault message, or null for a property read
    // without fault), in document order, repeats included; empty unless a field-level failure.
    private readonly IReadOnlyList<KeyValuePair<string, string?>> _properties;

    private RawValuesResult(
        IReadOnlyList<KeyValuePair<string, FieldValue>> values,
        string? requestError,
        IReadOnlyList<KeyValuePair<string, string?>> properties)
    {
        Values = values;
        RequestError = requestError;
        _properties = properties;
    }

    /// <summary>Whether the object was read: no request-level error and no field fault.</summary>
    public bool IsSuccess => RequestError is null && _properties.Count == 0;

    /// <summary>
    /// Every property of the object as (submitted name, raw value), in document order, with exact repeats and
    /// names that differ only by letter case kept as separate entries; empty unless <see cref="IsSuccess"/>.
    /// Each value is <see cref="FieldValueKind.Null"/>, <see cref="FieldValueKind.Text"/> (verbatim, never trimmed
    /// or turned into <see langword="null"/>), <see cref="FieldValueKind.Number"/> or
    /// <see cref="FieldValueKind.Boolean"/>.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, FieldValue>> Values { get; }

    /// <summary>
    /// The <c>detail</c> of a request-level 400, <see cref="ValidationMessages.ValuesMustBeObject"/> or
    /// <see cref="ApiProblems.MalformedJsonDetail"/>; <see langword="null"/> unless this is a request-level failure.
    /// </summary>
    public string? RequestError { get; }

    /// <summary>
    /// Resolves every property of a field-level failure against the form's metadata into the <c>errors</c> map of a
    /// validation 400, with the precedence the record validator and <see cref="ValuesReader.ToTyped"/> apply, so the
    /// record-write and field-event paths key and word an error alike.
    /// </summary>
    /// <param name="form">The metadata of the form the request names.</param>
    /// <returns>
    /// The map, never <see langword="null"/>, and empty unless this is a field-level failure. Each property, in
    /// document order, gives the first of:
    /// <list type="number">
    ///   <item><description>
    ///     for a name that is not a data field, whatever its fault, the error
    ///     <see cref="RecordValidator.NotADataFieldError"/> gives:
    ///     <see cref="ValidationMessages.UnknownValuesName"/> under <see cref="RecordValidator.ValuesKey"/> when the
    ///     form has no <c>metaFields</c> row of that name, and <see cref="ValidationMessages.UnknownField"/> under the
    ///     row's canonical name for a system column, a label, a group or an unsupported field;
    ///   </description></item>
    ///   <item><description>
    ///     for a data field named again, exactly or differing only by letter case, whatever its fault,
    ///     <see cref="ValidationMessages.DuplicateField"/> under the canonical name;
    ///   </description></item>
    ///   <item><description>
    ///     for the first occurrence of a faulted data field, the fault's own message under the canonical name;
    ///   </description></item>
    ///   <item><description>
    ///     otherwise nothing: the first occurrence of a data field read without fault adds no error.
    ///   </description></item>
    /// </list>
    /// Keys compare ordinally and keep the order they were first found in, and each holds each distinct message once,
    /// so <c>{"Age":1,"AGE":[]}</c> gives <c>Age</c> the duplicate message alone and <c>{"AGE":[],"Age":1}</c> gives it
    /// the shape message, then the duplicate message. A submitted name is never a key.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="form"/> is <see langword="null"/>.</exception>
    public IReadOnlyDictionary<string, string[]> FieldErrorsFor(FormMetadata form)
    {
        ArgumentNullException.ThrowIfNull(form);

        if (_properties.Count == 0)
        {
            return FieldErrorSet.None;
        }

        var dataFields = FormDefinitionFactory.GetDataFields(form);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var errors = new FieldErrorCollector();
        foreach (var (name, fault) in _properties)
        {
            if (!dataFields.TryGet(name, out var field))
            {
                errors.AddNotADataField(form, name);
            }
            else if (!seen.Add(field.Name))
            {
                errors.Add(field.Name, ValidationMessages.DuplicateField);
            }
            else if (fault is not null)
            {
                errors.Add(field.Name, fault);
            }
        }

        return errors.ToDictionary();
    }

    /// <summary>Returns a success holding a copy of <paramref name="values"/>, in its order.</summary>
    /// <param name="values">The entries in document order; an empty list is a valid <c>values: {}</c>.</param>
    /// <returns>The successful result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="values"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An entry has a <see langword="null"/> name.</exception>
    public static RawValuesResult Success(IReadOnlyList<KeyValuePair<string, FieldValue>> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var copy = new KeyValuePair<string, FieldValue>[values.Count];
        for (var i = 0; i < copy.Length; i++)
        {
            if (values[i].Key is null)
            {
                throw new ArgumentException("A value must be keyed by a field name.", nameof(values));
            }

            copy[i] = values[i];
        }

        return new RawValuesResult(Array.AsReadOnly(copy), requestError: null, NoProperties);
    }

    /// <summary>Returns a request-level failure whose <c>detail</c> is <paramref name="detail"/>.</summary>
    /// <param name="detail">A fixed message such as <see cref="ValidationMessages.ValuesMustBeObject"/>.</param>
    /// <returns>The failed result.</returns>
    /// <exception cref="ArgumentException"><paramref name="detail"/> is <see langword="null"/>, empty or whitespace.</exception>
    public static RawValuesResult RequestFailure(string detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            throw new ArgumentException("A request-level failure needs a non-blank detail.", nameof(detail));
        }

        return new RawValuesResult(ReadOnlyCollection<KeyValuePair<string, FieldValue>>.Empty, detail, NoProperties);
    }

    /// <summary>Returns a field-level failure holding a copy of <paramref name="properties"/>, in its order.</summary>
    /// <param name="properties">
    /// Every property of the object as (submitted name, fixed fault message), in document order, with
    /// <see langword="null"/> as the message of a property read without fault; a name may recur, exactly or differing
    /// only by letter case. At least one entry carries a fault. The names are kept only for
    /// <see cref="FieldErrorsFor"/> to resolve.
    /// </param>
    /// <returns>The failed result.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="properties"/> is <see langword="null"/> or holds no faulted entry, or an entry has a
    /// <see langword="null"/> name or an empty or whitespace message.
    /// </exception>
    public static RawValuesResult FieldFailure(IReadOnlyList<KeyValuePair<string, string?>> properties)
    {
        // Null, empty and fault-free are one misuse, so all three raise one exception type with one message.
        const string NoFaultMessage = "A field-level failure needs at least one faulted property.";
        if (properties is null)
        {
            throw new ArgumentException(NoFaultMessage, nameof(properties));
        }

        var copy = new KeyValuePair<string, string?>[properties.Count];
        var faulted = false;
        for (var i = 0; i < copy.Length; i++)
        {
            var (name, fault) = properties[i];

            // An empty name stays allowed: JSON permits "" as a property name, and it resolves to ValuesKey.
            if (name is null)
            {
                throw new ArgumentException("A property must be keyed by its submitted name.", nameof(properties));
            }

            if (fault is not null)
            {
                if (string.IsNullOrWhiteSpace(fault))
                {
                    throw new ArgumentException("A fault needs a non-blank message.", nameof(properties));
                }

                faulted = true;
            }

            copy[i] = properties[i];
        }

        if (!faulted)
        {
            throw new ArgumentException(NoFaultMessage, nameof(properties));
        }

        return new RawValuesResult(
            ReadOnlyCollection<KeyValuePair<string, FieldValue>>.Empty,
            requestError: null,
            Array.AsReadOnly(copy));
    }
}

/// <summary>
/// The outcome of <see cref="ValuesReader.ToTyped"/>: the field-event values typed for their fields and keyed by
/// canonical field name, or the field-level errors that reject them.
/// </summary>
/// <remarks>
/// Exactly one of two states holds: success (<see cref="Success"/>), with <see cref="FieldErrors"/> empty, or
/// field-level failure (<see cref="FieldFailure"/>), with <see cref="FieldErrors"/> non-empty and
/// <see cref="Values"/> empty. There is no request-level state: a <c>values</c> that is not an object is answered by
/// <see cref="ValuesReader.ReadRaw"/> before typing starts. Every collection passed to a factory is copied, so
/// instances are immutable and safe to share across threads.
/// </remarks>
public sealed class TypedValuesResult
{
    private TypedValuesResult(
        IReadOnlyDictionary<string, FieldValue> values,
        IReadOnlyDictionary<string, string[]> fieldErrors)
    {
        Values = values;
        FieldErrors = fieldErrors;
    }

    /// <summary>Whether every entry named a data field once and had its field's wire type.</summary>
    public bool IsSuccess => FieldErrors.Count == 0;

    /// <summary>
    /// The typed values keyed by canonical <c>metaFields.Name</c>, in submitted order, with lookups that ignore
    /// letter case; empty unless <see cref="IsSuccess"/>. Data fields the request did not name are absent.
    /// </summary>
    public IReadOnlyDictionary<string, FieldValue> Values { get; }

    /// <summary>
    /// The field-level errors, compared ordinally and in the order they were found: keyed by canonical
    /// <c>metaFields.Name</c>, or by <see cref="RecordValidator.ValuesKey"/> for a name the form has no
    /// <c>metaFields</c> row for (<see cref="RecordValidator.NotADataFieldError"/>), so a submitted name is never a
    /// key. Never <see langword="null"/>, and empty unless this is a failure.
    /// </summary>
    public IReadOnlyDictionary<string, string[]> FieldErrors { get; }

    /// <summary>Returns a success holding a copy of <paramref name="values"/>, in its order.</summary>
    /// <param name="values">The typed values keyed by canonical field name; an empty dictionary is allowed.</param>
    /// <returns>The successful result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="values"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="values"/> holds a <see langword="null"/> key, or two keys that differ only by letter case.
    /// </exception>
    public static TypedValuesResult Success(IReadOnlyDictionary<string, FieldValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var copy = new OrderedDictionary<string, FieldValue>(values.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in values)
        {
            if (name is null)
            {
                throw new ArgumentException("A value must be keyed by a field name.", nameof(values));
            }

            if (!copy.TryAdd(name, value))
            {
                throw new ArgumentException(
                    "Each field may be given only once; names are compared without regard to letter case.",
                    nameof(values));
            }
        }

        return new TypedValuesResult(new ReadOnlyDictionary<string, FieldValue>(copy), FieldErrorSet.None);
    }

    /// <summary>Returns a field-level failure holding a copy of <paramref name="errors"/>, in its order.</summary>
    /// <param name="errors">
    /// The fixed messages per canonical field name or <see cref="RecordValidator.ValuesKey"/>.
    /// </param>
    /// <returns>The failed result.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="errors"/> is <see langword="null"/> or empty, or an entry has a <see langword="null"/> key, a
    /// <see langword="null"/> or empty message array, or a <see langword="null"/> or blank message.
    /// </exception>
    public static TypedValuesResult FieldFailure(IReadOnlyDictionary<string, string[]> errors) => new(
        ReadOnlyDictionary<string, FieldValue>.Empty,
        FieldErrorSet.Copy(errors, nameof(errors)));
}

/// <summary>
/// Reads the <c>values</c> object of the record write bodies (<c>POST</c> and <c>PUT</c> on records) and of the
/// field-event body, and types field-event values for their fields. It is pure and stateless: no I/O, no logging.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ReadRaw"/> is the shape check both paths share. It keeps every property, exact repeats and case
/// variants included, in document order, and converts each scalar field-type blind with the mapping of
/// <see cref="FieldValueJsonConverter"/>. Field-specific rules then belong to <c>RecordValidator</c> for writes and to
/// <see cref="ToTyped"/> for field events.
/// </para>
/// <para>
/// Every message is fixed text, and no error carries submitted text: a value never appears in one, and every error
/// is keyed by a canonical <c>metaFields.Name</c> or, for a name the form has no row for, by
/// <see cref="RecordValidator.ValuesKey"/> (<see cref="RecordValidator.NotADataFieldError"/>), never by a name as
/// submitted.
/// </para>
/// </remarks>
public static class ValuesReader
{
    /// <summary>The field-level message for a <c>values</c> member that is a JSON object or array.</summary>
    public const string ScalarValueExpected = "must be a string, number, boolean or null";

    /// <summary>Reads the raw entries of a <c>values</c> member.</summary>
    /// <param name="values">
    /// The member as bound: <see langword="null"/> when it is missing or JSON <c>null</c>, otherwise the element sent.
    /// </param>
    /// <returns>
    /// <list type="bullet">
    ///   <item><description>
    ///     a request-level failure with <see cref="ValidationMessages.ValuesMustBeObject"/> when
    ///     <paramref name="values"/> is <see langword="null"/>, undefined, JSON <c>null</c>, an array, a string, a
    ///     number or a boolean;
    ///   </description></item>
    ///   <item><description>
    ///     a request-level failure with <see cref="ApiProblems.MalformedJsonDetail"/> when a property name or string
    ///     value cannot be decoded, such as an escaped lone surrogate <c>"\uD800"</c>: the same "malformed JSON" 400
    ///     that binding gives that text when it is read as a typed value;
    ///   </description></item>
    ///   <item><description>
    ///     a field-level failure when any property is faulted: <see cref="ScalarValueExpected"/> for an object or array
    ///     value and <see cref="ValidationMessages.NumberExpected"/> for a number that is not finite, such as
    ///     <c>1e400</c>. It holds every property, repeats and the properties read without fault included, in document
    ///     order, so that <see cref="RawValuesResult.FieldErrorsFor"/>, once the form's metadata is known, applies the
    ///     validator's precedence: a name that is not a data field first, then a repeat of a data field, then the
    ///     fault itself;
    ///   </description></item>
    ///   <item><description>
    ///     otherwise a success: <c>null</c> as <see cref="FieldValue.Null"/>, a string as verbatim text, a number as an
    ///     exact number value and <c>true</c>/<c>false</c> as booleans, in document order.
    ///   </description></item>
    /// </list>
    /// </returns>
    public static RawValuesResult ReadRaw(JsonElement? values)
    {
        if (values is not { ValueKind: JsonValueKind.Object } element)
        {
            return RawValuesResult.RequestFailure(ValidationMessages.ValuesMustBeObject);
        }

        var entries = new List<KeyValuePair<string, FieldValue>>();

        // Every property under its name as sent, with its fault or null, so a field-level failure keeps the properties
        // read without fault as well: RawValuesResult.FieldErrorsFor needs them to find the names that are not data
        // fields and the repeats beside a fault once the form's metadata is known.
        var properties = new List<KeyValuePair<string, string?>>();
        var faulted = false;

        // EnumerateObject yields every property occurrence in document order; nothing here collapses repeats, so
        // RecordValidator can reject a field named twice, exactly or differing only by case.
        foreach (var property in element.EnumerateObject())
        {
            if (!TryDecodeName(property, out var name))
            {
                return RawValuesResult.RequestFailure(ApiProblems.MalformedJsonDetail);
            }

            string? fault = null;
            var value = property.Value;
            switch (value.ValueKind)
            {
                case JsonValueKind.Null:
                    entries.Add(new(name, FieldValue.Null));
                    break;

                case JsonValueKind.String:
                    // Kept verbatim: trimming and empty-to-null normalisation are validation rules.
                    if (!TryDecodeString(value, out var text))
                    {
                        return RawValuesResult.RequestFailure(ApiProblems.MalformedJsonDetail);
                    }

                    entries.Add(new(name, FieldValue.FromText(text)));
                    break;

                case JsonValueKind.Number:
                    // Parsed from the literal exactly as FieldValueJsonConverter parses it, rather than through
                    // JsonElement.TryGetDouble, which rounds to a double and gives infinity for 1e400: a whole number
                    // keeps its exact integer, so 2.0000000000000001 can never become option index 2.
                    if (FieldValueCoercion.TryParseNumber(JsonMarshal.GetRawUtf8Value(value), out var number))
                    {
                        entries.Add(new(name, number));
                    }
                    else
                    {
                        fault = ValidationMessages.NumberExpected;
                    }

                    break;

                case JsonValueKind.True:
                    entries.Add(new(name, FieldValue.FromBoolean(true)));
                    break;

                case JsonValueKind.False:
                    entries.Add(new(name, FieldValue.FromBoolean(false)));
                    break;

                default:
                    fault = ScalarValueExpected;
                    break;
            }

            properties.Add(new(name, fault));
            faulted |= fault is not null;
        }

        return faulted
            ? RawValuesResult.FieldFailure(properties)
            : RawValuesResult.Success(entries);
    }

    /// <summary>
    /// Types the raw entries of a field-event body for the form's data fields. Only the two rules evaluation needs
    /// apply: every name is a data field of the form, and every value has its field's wire type.
    /// </summary>
    /// <remarks>
    /// Read-only, required, length, range, mask, legal-value, storage-range and empty-record rules are deliberately
    /// not applied: the editor sends the whole record state, read-only fields included, and must be able to evaluate
    /// stored values that break current rules, such as a phone number that does not match its mask.
    /// </remarks>
    /// <param name="form">
    /// The form's metadata; its data fields are those <see cref="FormDefinitionFactory.GetDataFields"/> selects.
    /// </param>
    /// <param name="raw">The entries <see cref="ReadRaw"/> returned, in document order.</param>
    /// <returns>
    /// A success keyed by canonical name in submitted order, or a field-level failure listing every error, never
    /// keyed by a name as submitted:
    /// <list type="bullet">
    ///   <item><description>
    ///     for a name that is not a data field, <see cref="ValidationMessages.UnknownValuesName"/> under
    ///     <see cref="RecordValidator.ValuesKey"/> when the form has no <c>metaFields</c> row of that name, and
    ///     <see cref="ValidationMessages.UnknownField"/> under the row's canonical name for a system column such as
    ///     <c>GlobalRecordId</c>, a label, a group or an unsupported field, as
    ///     <see cref="RecordValidator.NotADataFieldError"/> gives them;
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="ValidationMessages.DuplicateField"/>, keyed by the canonical name, for a field named again,
    ///     exactly or differing only by case;
    ///   </description></item>
    ///   <item><description>
    ///     keyed by the canonical name, <see cref="ValidationMessages.TextExpected"/> for a text, multiline or
    ///     legal-values field whose value is not a string; <see cref="ValidationMessages.NumberExpected"/> for a number
    ///     field whose value is not a number; <see cref="ValidationMessages.DateExpected"/> for a date field whose
    ///     value is not a string of exactly <c>YYYY-MM-DD</c>; <see cref="ValidationMessages.BooleanExpected"/> for a
    ///     checkbox whose value is not a boolean; and <see cref="ValidationMessages.OptionIndexExpected"/> for an
    ///     option whose value is not a whole number from 0 to <see cref="int.MaxValue"/>.
    ///   </description></item>
    /// </list>
    /// <c>null</c> is accepted for every field, and an empty string stays text.
    /// </returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An entry of <paramref name="raw"/> has a <see langword="null"/> name.</exception>
    public static TypedValuesResult ToTyped(FormMetadata form, IReadOnlyList<KeyValuePair<string, FieldValue>> raw)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(raw);

        var dataFields = FormDefinitionFactory.GetDataFields(form);
        var typed = new OrderedDictionary<string, FieldValue>(raw.Count, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var errors = new FieldErrorCollector();

        foreach (var (name, value) in raw)
        {
            if (name is null)
            {
                throw new ArgumentException("A value must be keyed by a field name.", nameof(raw));
            }

            if (!dataFields.TryGet(name, out var field))
            {
                errors.AddNotADataField(form, name);
                continue;
            }

            if (!seen.Add(field.Name))
            {
                errors.Add(field.Name, ValidationMessages.DuplicateField);
                continue;
            }

            if (FieldTypeMapper.ValueKind(field.FieldType) is not { } target
                || TypeExpectedMessage(target) is not { } typeExpected)
            {
                // A data field always holds a value kind; one that does not cannot take a value.
                errors.Add(field.Name, ValidationMessages.UnknownField);
                continue;
            }

            if (value.IsNull)
            {
                typed.Add(field.Name, FieldValue.Null);
            }
            else if (TryConvert(value, target, out var converted))
            {
                typed.Add(field.Name, converted);
            }
            else
            {
                errors.Add(field.Name, typeExpected);
            }
        }

        return errors.HasErrors
            ? TypedValuesResult.FieldFailure(errors.ToDictionary())
            : TypedValuesResult.Success(typed);
    }

    // The fixed message for a value of the wrong wire type, or null for a kind that holds no value.
    private static string? TypeExpectedMessage(FieldValueKind kind) => kind switch
    {
        FieldValueKind.Text => ValidationMessages.TextExpected,
        FieldValueKind.Number => ValidationMessages.NumberExpected,
        FieldValueKind.Date => ValidationMessages.DateExpected,
        FieldValueKind.Boolean => ValidationMessages.BooleanExpected,
        FieldValueKind.Index => ValidationMessages.OptionIndexExpected,
        _ => null,
    };

    // Accepts only the JSON scalar the wire contract gives the kind: a date travels as a string and an option index as
    // a number. The coercion then checks the shape (an exact yyyy-MM-dd date, a whole index from 0 to int.MaxValue)
    // and keeps an empty string as text.
    private static bool TryConvert(FieldValue raw, FieldValueKind target, out FieldValue value)
    {
        var wireKind = target switch
        {
            FieldValueKind.Date => FieldValueKind.Text,
            FieldValueKind.Index => FieldValueKind.Number,
            _ => target,
        };

        if (raw.Kind != wireKind)
        {
            value = FieldValue.Null;
            return false;
        }

        var result = FieldValueCoercion.Coerce(raw, target, normalizeEmptyText: false);
        value = result.Value;
        return result.Succeeded;
    }

    // JsonProperty.Name and JsonElement.GetString throw InvalidOperationException for text that does not decode, such
    // as an escaped lone surrogate, which JSON parsing itself accepts. Caught here so such a body is a 400, not a 500.
    private static bool TryDecodeName(JsonProperty property, out string name)
    {
        try
        {
            name = property.Name;
            return true;
        }
        catch (InvalidOperationException)
        {
            name = string.Empty;
            return false;
        }
    }

    private static bool TryDecodeString(JsonElement element, out string text)
    {
        try
        {
            text = element.GetString() ?? string.Empty;
            return true;
        }
        catch (InvalidOperationException)
        {
            text = string.Empty;
            return false;
        }
    }
}

/// <summary>Validating copies of a field-errors map, shared by both result types.</summary>
file static class FieldErrorSet
{
    public static readonly IReadOnlyDictionary<string, string[]> None = ReadOnlyDictionary<string, string[]>.Empty;

    public static IReadOnlyDictionary<string, string[]> Copy(IReadOnlyDictionary<string, string[]>? errors, string paramName)
    {
        // Null and empty are one misuse, so both raise one exception type.
        if (errors is null || errors.Count == 0)
        {
            throw new ArgumentException("A field-level failure needs at least one field error.", paramName);
        }

        var copy = new OrderedDictionary<string, string[]>(errors.Count, StringComparer.Ordinal);
        foreach (var (name, messages) in errors)
        {
            // An empty key stays allowed, as JSON permits "" as a property name. The reader never produces one: a name
            // with no metaFields row is keyed by RecordValidator.ValuesKey.
            if (name is null)
            {
                throw new ArgumentException("A field error must be keyed by a field name.", paramName);
            }

            if (messages is null || messages.Length == 0 || Array.Exists(messages, string.IsNullOrWhiteSpace))
            {
                throw new ArgumentException("A field error needs one or more non-blank messages.", paramName);
            }

            if (!copy.TryAdd(name, [.. messages]))
            {
                throw new ArgumentException("Each field may carry only one list of errors.", paramName);
            }
        }

        return new ReadOnlyDictionary<string, string[]>(copy);
    }
}

/// <summary>
/// Collects field errors in the order they are found, keyed ordinally, holding each distinct message once per key so a
/// name repeated with the same fault is reported once.
/// </summary>
file sealed class FieldErrorCollector
{
    private readonly OrderedDictionary<string, List<string>> _errors = new(StringComparer.Ordinal);

    public bool HasErrors => _errors.Count > 0;

    public void Add(string key, string message)
    {
        if (!_errors.TryGetValue(key, out var messages))
        {
            messages = [];
            _errors.Add(key, messages);
        }

        if (!messages.Contains(message, StringComparer.Ordinal))
        {
            messages.Add(message);
        }
    }

    // A name that is not a data field, keyed and worded by RecordValidator.NotADataFieldError, the one rule the
    // validator also reports such a name by; the name check replaces any fault of the value.
    public void AddNotADataField(FormMetadata form, string name)
    {
        var (key, message) = RecordValidator.NotADataFieldError(form, name);
        Add(key, message);
    }

    public IReadOnlyDictionary<string, string[]> ToDictionary()
    {
        var result = new OrderedDictionary<string, string[]>(_errors.Count, StringComparer.Ordinal);
        foreach (var (key, messages) in _errors)
        {
            result.Add(key, [.. messages]);
        }

        return result;
    }
}
