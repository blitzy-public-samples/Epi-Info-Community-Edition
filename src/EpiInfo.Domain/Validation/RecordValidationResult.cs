using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using EpiInfo.Domain.Values;

namespace EpiInfo.Domain.Validation;

/// <summary>
/// The immutable outcome of one <c>RecordValidator</c> step: <c>ValidateInput</c>,
/// <c>CheckNewRecord</c> or <c>CheckRequired</c>. It separates the two 400 shapes of the API: a
/// request-level failure, sent as plain ProblemDetails whose <c>detail</c> is <see cref="RequestError"/>
/// with no <c>errors</c> map, and a field-level failure, sent as validation ProblemDetails whose
/// <c>errors</c> map is <see cref="FieldErrors"/>.
/// </summary>
/// <remarks>
/// <para>
/// Exactly one of three states holds, and the factories are the only way to reach one:
/// </para>
/// <list type="bullet">
///   <item><description>
///     valid (<see cref="Success"/>): <see cref="IsValid"/> is <see langword="true"/>,
///     <see cref="Values"/> is set, <see cref="RequestError"/> is <see langword="null"/> and
///     <see cref="FieldErrors"/> is empty;
///   </description></item>
///   <item><description>
///     request-level failure (<see cref="RequestFailure"/>): <see cref="RequestError"/> is set,
///     <see cref="FieldErrors"/> is empty and <see cref="Values"/> is <see langword="null"/>;
///   </description></item>
///   <item><description>
///     field-level failure (<see cref="FieldFailure"/>): <see cref="FieldErrors"/> is non-empty,
///     <see cref="RequestError"/> is <see langword="null"/> and <see cref="Values"/> is
///     <see langword="null"/>.
///   </description></item>
/// </list>
/// <para>
/// Error texts are the fixed messages of <see cref="ValidationMessages"/>, formatted from metadata only,
/// so a failure never carries a submitted value. Submitted values are held only by a valid result, in
/// normalised form, for the store to write; they never reach an error body.
/// </para>
/// <para>
/// Every collection passed to a factory is copied, so a caller that changes its own dictionary or list
/// afterwards cannot change the result. Instances are immutable and safe to share across threads.
/// </para>
/// </remarks>
public sealed class RecordValidationResult
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> NoFieldErrors =
        ReadOnlyDictionary<string, IReadOnlyList<string>>.Empty;

    private RecordValidationResult(
        IReadOnlyDictionary<string, FieldValue>? values,
        string? requestError,
        IReadOnlyDictionary<string, IReadOnlyList<string>> fieldErrors)
    {
        Values = values;
        RequestError = requestError;
        FieldErrors = fieldErrors;
    }

    /// <summary>
    /// Whether validation passed. When <see langword="true"/>, <see cref="Values"/> is set; when
    /// <see langword="false"/>, either <see cref="RequestError"/> is set or <see cref="FieldErrors"/> is
    /// non-empty, never both.
    /// </summary>
    [MemberNotNullWhen(true, nameof(Values))]
    public bool IsValid => Values is not null;

    /// <summary>
    /// The <c>detail</c> of a request-level 400, such as <see cref="ValidationMessages.ValuesMustBeObject"/>
    /// or <see cref="ValidationMessages.RecordHasNoValues"/>. <see langword="null"/> unless this is a
    /// request-level failure.
    /// </summary>
    public string? RequestError { get; }

    /// <summary>
    /// The field-level errors, never <see langword="null"/> and empty unless this is a field-level failure.
    /// </summary>
    /// <remarks>
    /// Keys are canonical <c>metaFields.Name</c> values, or <c>RecordValidator.ValuesKey</c> for a name that
    /// matches no <c>metaFields</c> row, and are compared ordinally. Keys keep the order the validator
    /// reported them in, and each list holds one or more messages in the order the checks ran.
    /// </remarks>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> FieldErrors { get; }

    /// <summary>
    /// The normalised values keyed by canonical field name, in the order they were given; set only when
    /// <see cref="IsValid"/> is <see langword="true"/>. Lookups ignore letter case, which only makes them
    /// forgiving, because the keys are canonical and unique without regard to case.
    /// </summary>
    public IReadOnlyDictionary<string, FieldValue>? Values { get; }

    /// <summary>Returns a valid result holding a copy of <paramref name="values"/>.</summary>
    /// <param name="values">
    /// The normalised values keyed by canonical field name. An empty dictionary is allowed: a <c>PUT</c>
    /// with <c>values: {}</c> passes the input checks.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="values"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="values"/> holds a <see langword="null"/> key, or two keys that differ only by letter case.
    /// </exception>
    public static RecordValidationResult Success(IReadOnlyDictionary<string, FieldValue> values)
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

        return new RecordValidationResult(
            new ReadOnlyDictionary<string, FieldValue>(copy),
            requestError: null,
            NoFieldErrors);
    }

    /// <summary>Returns a request-level failure whose <c>detail</c> is <paramref name="detail"/>.</summary>
    /// <param name="detail">
    /// A fixed message such as <see cref="ValidationMessages.ValuesMustBeObject"/> or
    /// <see cref="ValidationMessages.RecordHasNoValues"/>.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="detail"/> is <see langword="null"/>, empty or whitespace.</exception>
    public static RecordValidationResult RequestFailure(string detail)
    {
        // Null is reported as a plain ArgumentException, the same type as a blank detail, rather than
        // ArgumentNullException, so both misuses surface as one exception type.
        if (string.IsNullOrWhiteSpace(detail))
        {
            throw new ArgumentException("A request-level failure needs a non-blank detail.", nameof(detail));
        }

        return new RecordValidationResult(values: null, detail, NoFieldErrors);
    }

    /// <summary>Returns a field-level failure holding a copy of <paramref name="errors"/>, in its order.</summary>
    /// <param name="errors">
    /// The messages per field, keyed by canonical field name (or <c>RecordValidator.ValuesKey</c> for a name
    /// that matches no <c>metaFields</c> row), in the order the validator reported them.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="errors"/> is <see langword="null"/> or empty; or an entry has a <see langword="null"/>
    /// key, a <see langword="null"/> or empty message list, or a <see langword="null"/> or blank message.
    /// </exception>
    public static RecordValidationResult FieldFailure(IReadOnlyDictionary<string, IReadOnlyList<string>> errors)
    {
        // As in RequestFailure, null and empty are one misuse and raise one exception type.
        if (errors is null || errors.Count == 0)
        {
            throw new ArgumentException("A field-level failure needs at least one field error.", nameof(errors));
        }

        var copy = new OrderedDictionary<string, IReadOnlyList<string>>(errors.Count, StringComparer.Ordinal);
        foreach (var (name, messages) in errors)
        {
            // An empty key stays allowed, as JSON permits "" as a property name. The validator never
            // produces one: a name with no metaFields row is keyed by RecordValidator.ValuesKey.
            if (name is null)
            {
                throw new ArgumentException("A field error must be keyed by a field name.", nameof(errors));
            }

            if (!copy.TryAdd(name, CopyMessages(messages, nameof(errors))))
            {
                throw new ArgumentException("Each field may carry only one list of errors.", nameof(errors));
            }
        }

        return new RecordValidationResult(
            values: null,
            requestError: null,
            new ReadOnlyDictionary<string, IReadOnlyList<string>>(copy));
    }

    /// <summary>
    /// Copies <see cref="FieldErrors"/> into the <c>IDictionary&lt;string, string[]&gt;</c> shape that the
    /// API's validation ProblemDetails takes, keeping key order, ordinal key comparison and message order.
    /// </summary>
    /// <returns>A new dictionary the caller owns; empty when there are no field errors.</returns>
    public Dictionary<string, string[]> ToErrorDictionary()
    {
        var result = new Dictionary<string, string[]>(FieldErrors.Count, StringComparer.Ordinal);
        foreach (var (name, messages) in FieldErrors)
        {
            result.Add(name, [.. messages]);
        }

        return result;
    }

    private static ReadOnlyCollection<string> CopyMessages(IReadOnlyList<string>? messages, string paramName)
    {
        if (messages is null || messages.Count == 0)
        {
            throw new ArgumentException("A field error needs at least one message.", paramName);
        }

        var copy = new string[messages.Count];
        for (var i = 0; i < copy.Length; i++)
        {
            var message = messages[i];
            if (string.IsNullOrWhiteSpace(message))
            {
                throw new ArgumentException("A field error message must not be blank.", paramName);
            }

            copy[i] = message;
        }

        return Array.AsReadOnly(copy);
    }
}
