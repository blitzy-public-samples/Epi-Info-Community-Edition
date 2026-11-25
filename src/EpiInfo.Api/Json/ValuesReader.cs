using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Text.Json;
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
///     holds every entry, <see cref="RequestError"/> is <see langword="null"/> and <see cref="FieldErrors"/> is empty;
///   </description></item>
///   <item><description>
///     request-level failure (<see cref="RequestFailure"/>): <see cref="RequestError"/> is the <c>detail</c> of a 400
///     with no <c>errors</c> map, and <see cref="Values"/> and <see cref="FieldErrors"/> are empty;
///   </description></item>
///   <item><description>
///     field-level failure (<see cref="FieldFailure"/>): <see cref="FieldErrors"/> is the non-empty <c>errors</c> map
///     of a validation 400, <see cref="RequestError"/> is <see langword="null"/> and <see cref="Values"/> is empty.
///   </description></item>
/// </list>
/// <para>
/// Every collection passed to a factory is copied, so instances are immutable and safe to share across threads.
/// Error texts are fixed, so a failure never carries a submitted value.
/// </para>
/// </remarks>
public sealed class RawValuesResult
{
    private RawValuesResult(
        IReadOnlyList<KeyValuePair<string, FieldValue>> values,
        string? requestError,
        IReadOnlyDictionary<string, string[]> fieldErrors)
    {
        Values = values;
        RequestError = requestError;
        FieldErrors = fieldErrors;
    }

    /// <summary>Whether the object was read: no request-level error and no field error.</summary>
    public bool IsSuccess => RequestError is null && FieldErrors.Count == 0;

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
    /// <see cref="ValuesReader.MalformedJson"/>; <see langword="null"/> unless this is a request-level failure.
    /// </summary>
    public string? RequestError { get; }

    /// <summary>
    /// The field-level errors keyed by the name as submitted, compared ordinally and in the order they were found;
    /// never <see langword="null"/>, and empty unless this is a field-level failure.
    /// </summary>
    public IReadOnlyDictionary<string, string[]> FieldErrors { get; }

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

        return new RawValuesResult(Array.AsReadOnly(copy), requestError: null, FieldErrorSet.None);
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

        return new RawValuesResult(ReadOnlyCollection<KeyValuePair<string, FieldValue>>.Empty, detail, FieldErrorSet.None);
    }

    /// <summary>Returns a field-level failure holding a copy of <paramref name="errors"/>, in its order.</summary>
    /// <param name="errors">The fixed messages per submitted name.</param>
    /// <returns>The failed result.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="errors"/> is <see langword="null"/> or empty, or an entry has a <see langword="null"/> key, a
    /// <see langword="null"/> or empty message array, or a <see langword="null"/> or blank message.
    /// </exception>
    public static RawValuesResult FieldFailure(IReadOnlyDictionary<string, string[]> errors) => new(
        ReadOnlyCollection<KeyValuePair<string, FieldValue>>.Empty,
        requestError: null,
        FieldErrorSet.Copy(errors, nameof(errors)));
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
    /// The field-level errors, compared ordinally and in the order they were found: keyed by canonical field name,
    /// or by the name as submitted when it names no data field. Never <see langword="null"/>, and empty unless this
    /// is a failure.
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
    /// <param name="errors">The fixed messages per canonical or submitted name.</param>
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
/// Every message is fixed text. A submitted value never appears in a result; the only submitted text a result can
/// hold is a field name used as an error key.
/// </para>
/// </remarks>
public static class ValuesReader
{
    /// <summary>The field-level message for a <c>values</c> member that is a JSON object or array.</summary>
    public const string ScalarValueExpected = "must be a string, number, boolean or null";

    /// <summary>
    /// The request-level <c>detail</c> for a <c>values</c> object holding a name or string the JSON text cannot
    /// decode, such as an escaped lone surrogate <c>"\uD800"</c>: the same "malformed JSON" 400 that binding gives that
    /// text when it is read as a typed value.
    /// </summary>
    public const string MalformedJson = "malformed JSON";

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
    ///     a request-level failure with <see cref="MalformedJson"/> when a property name or string value cannot be
    ///     decoded;
    ///   </description></item>
    ///   <item><description>
    ///     a field-level failure, keyed by the submitted name, with <see cref="ScalarValueExpected"/> for an object or
    ///     array value and <see cref="ValidationMessages.NumberExpected"/> for a number that is not finite, such as
    ///     <c>1e400</c>; every such property is reported;
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
        var errors = new FieldErrorCollector();

        // EnumerateObject yields every property occurrence in document order; nothing here collapses repeats, so
        // RecordValidator can reject a field named twice, exactly or differing only by case.
        foreach (var property in element.EnumerateObject())
        {
            if (!TryDecodeName(property, out var name))
            {
                return RawValuesResult.RequestFailure(MalformedJson);
            }

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
                        return RawValuesResult.RequestFailure(MalformedJson);
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
                        errors.Add(name, ValidationMessages.NumberExpected);
                    }

                    break;

                case JsonValueKind.True:
                    entries.Add(new(name, FieldValue.FromBoolean(true)));
                    break;

                case JsonValueKind.False:
                    entries.Add(new(name, FieldValue.FromBoolean(false)));
                    break;

                default:
                    errors.Add(name, ScalarValueExpected);
                    break;
            }
        }

        return errors.HasErrors
            ? RawValuesResult.FieldFailure(errors.ToDictionary())
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
    /// <param name="dataFields">The data fields of the form.</param>
    /// <param name="raw">The entries <see cref="ReadRaw"/> returned, in document order.</param>
    /// <returns>
    /// A success keyed by canonical name in submitted order, or a field-level failure listing every error:
    /// <list type="bullet">
    ///   <item><description>
    ///     <see cref="ValidationMessages.UnknownField"/>, keyed by the submitted name, for a name that is not a data
    ///     field (unknown, a system column such as <c>GlobalRecordId</c>, a label, a group or an unsupported field);
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
    public static TypedValuesResult ToTyped(DataFieldSet dataFields, IReadOnlyList<KeyValuePair<string, FieldValue>> raw)
    {
        ArgumentNullException.ThrowIfNull(dataFields);
        ArgumentNullException.ThrowIfNull(raw);

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
                errors.Add(name, ValidationMessages.UnknownField);
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
            // An empty key stays allowed: JSON permits "" as a property name, and an unknown one is keyed as submitted.
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
