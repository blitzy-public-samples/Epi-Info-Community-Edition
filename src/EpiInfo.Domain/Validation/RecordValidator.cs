using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using EpiInfo.Domain.Forms;
using EpiInfo.Domain.Metadata;
using EpiInfo.Domain.Records;
using EpiInfo.Domain.Values;

namespace EpiInfo.Domain.Validation;

/// <summary>
/// The server's one authoritative write gate: it validates the <c>values</c> of every record <c>POST</c> and
/// <c>PUT</c> against the form's metadata. The client repeats the input checks for feedback only.
/// </summary>
/// <remarks>
/// <para>Validation runs in two phases.</para>
/// <list type="number">
///   <item><description>
///     <see cref="ValidateInput"/> checks the submitted values only, before any write transaction opens: unknown
///     and repeated names, read-only fields, empty-text normalisation, the wire type, the column's storage range,
///     <c>MaxLength</c>, the inclusive range, the pattern and the legal values. Stored values the request does not
///     touch are never re-validated, so historical data that breaks a current rule stays readable and editable.
///   </description></item>
///   <item><description>
///     The effective-record checks run on the normalised values: <see cref="CheckNewRecord"/> for a <c>POST</c>,
///     before the transaction, and <see cref="CheckRequired"/> for a <c>PUT</c>, inside the transaction after the
///     locked read of the stored record.
///   </description></item>
/// </list>
/// <para>
/// Only data fields are accepted, as <see cref="FormDefinitionFactory.GetDataFields"/> defines them, so a caller can
/// never supply a save stamp, <c>RecStatus</c>, <c>UniqueKey</c>, <c>GlobalRecordId</c> or any other system column,
/// nor a label, group or unsupported field. Error keys are canonical <c>metaFields.Name</c> values; a name that
/// matches no <c>metaFields</c> row is reported under <see cref="ValuesKey"/>, so a submitted name never becomes an
/// error key (<see cref="NotADataFieldError"/>).
/// </para>
/// <para>
/// Every message is a fixed text of <see cref="ValidationMessages"/>, formatted only from metadata (prompt, page
/// name, column length, <c>MaxLength</c>, bounds, pattern, option count), so no submitted value ever reaches an
/// error body.
/// </para>
/// <para>
/// The class is pure: no I/O, no locking, no logging. All metadata is interpreted once, in the constructor, and the
/// instance is immutable afterwards, so it can be cached and shared across threads.
/// </para>
/// </remarks>
public sealed class RecordValidator
{
    /// <summary>
    /// The <c>errors</c> key for a submitted name that matches no <c>metaFields</c> row: the name of the request's
    /// <c>values</c> member, a parameter-name key, so a submitted name never reaches an error body.
    /// </summary>
    public const string ValuesKey = "values";

    private readonly FormMetadata _form;
    private readonly DataFieldSet _dataFields;
    private readonly FieldRule[] _rules;
    private readonly Dictionary<DataFieldMetadata, FieldRule> _rulesByField;

    /// <summary>
    /// Builds the validator for one form, interpreting its metadata once.
    /// </summary>
    /// <param name="form">The form's metadata, as read on this request.</param>
    /// <param name="definition">
    /// The form definition built from <paramref name="form"/> by <see cref="FormDefinitionFactory.Create"/>, which
    /// supplies the prompts, validation metadata and options of each field.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="form"/> or <paramref name="definition"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="definition"/> describes another form, lacks a data field of <paramref name="form"/>, gives a
    /// data field another type than the metadata does, or gives a number field on an integer column a bound that is
    /// not the one its metadata text states.
    /// </exception>
    /// <exception cref="InvalidOperationException">A data field's type holds no value kind.</exception>
    public RecordValidator(FormMetadata form, FormDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(definition);

        if (definition.ViewId != form.ViewId)
        {
            throw new ArgumentException(
                "The form definition describes a different form than the metadata.",
                nameof(definition));
        }

        _form = form;
        _dataFields = FormDefinitionFactory.GetDataFields(form);

        var exactDefinitions = new Dictionary<string, FieldDefinition>(StringComparer.Ordinal);
        var looseDefinitions = new Dictionary<string, FieldDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var fieldDefinition in definition.Pages.SelectMany(page => page.Fields))
        {
            exactDefinitions.TryAdd(fieldDefinition.Name, fieldDefinition);
            looseDefinitions.TryAdd(fieldDefinition.Name, fieldDefinition);
        }

        var rules = new List<FieldRule>(_dataFields.Count);
        _rulesByField = new Dictionary<DataFieldMetadata, FieldRule>(_dataFields.Count, ReferenceEqualityComparer.Instance);
        foreach (var dataField in _dataFields)
        {
            if (!exactDefinitions.TryGetValue(dataField.Name, out var fieldDefinition)
                && !looseDefinitions.TryGetValue(dataField.Name, out fieldDefinition))
            {
                throw new ArgumentException(
                    "The form definition does not list every data field of the form metadata.",
                    nameof(definition));
            }

            if (fieldDefinition.FieldType != dataField.FieldType)
            {
                throw new ArgumentException(
                    "The form definition gives a data field another type than the form metadata.",
                    nameof(definition));
            }

            var kind = FieldTypeMapper.ValueKind(dataField.FieldType)
                ?? throw new InvalidOperationException("A data field's type must hold a value kind.");

            // A field whose name differs from an earlier one only by letter case is shadowed: name lookups and value
            // maps resolve that name to the earlier field, so the shadowed one can never be submitted or carry a
            // value of its own. It gets no rule, so it can neither be written nor block a save as a required field.
            if (!_dataFields.TryGet(dataField.Name, out var resolved) || !ReferenceEquals(resolved, dataField))
            {
                continue;
            }

            var rule = new FieldRule(dataField, fieldDefinition, kind);
            rules.Add(rule);
            _rulesByField.Add(dataField, rule);
        }

        _rules = [.. rules];
    }

    /// <summary>
    /// The field-level error, key and message together, for a submitted name that is not a data field of the form.
    /// It is the one rule for reporting such a name: <see cref="ValidateInput"/> and the API's values reader both
    /// report every name that is not a data field through it.
    /// </summary>
    /// <remarks>
    /// A name that matches a <c>metaFields</c> row without regard to letter case, a system column, a label, a group or
    /// an unsupported field, gives <see cref="ValidationMessages.UnknownField"/> under the row's canonical
    /// <c>metaFields.Name</c>. Any other name, the empty name included, gives
    /// <see cref="ValidationMessages.UnknownValuesName"/> under <see cref="ValuesKey"/>. The caller has already found
    /// that the name is not a data field; this method does not check that again.
    /// </remarks>
    /// <param name="form">The form's metadata.</param>
    /// <param name="submittedName">A name as the request spelled it; an empty name matches no row.</param>
    /// <returns>The (key, fixed message) pair; neither is ever the submitted text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="form"/> or <paramref name="submittedName"/> is null.</exception>
    public static KeyValuePair<string, string> NotADataFieldError(FormMetadata form, string submittedName)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(submittedName);

        return form.FindField(submittedName) is { } row
            ? new(row.Name, ValidationMessages.UnknownField)
            : new(ValuesKey, ValidationMessages.UnknownValuesName);
    }

    /// <summary>
    /// Runs the phase-1 input checks on the submitted values of a <c>POST</c> or <c>PUT</c>, in submission order.
    /// </summary>
    /// <param name="submitted">
    /// The entries of the request's <c>values</c> object as (name, raw value) pairs in document order, exact and
    /// case-variant repeats included, as the API's values reader produces them. Each raw value is a JSON scalar:
    /// <see cref="FieldValue.Null"/>, text, a number or a boolean. <see langword="null"/> means <c>values</c> was
    /// missing, <c>null</c> or not an object.
    /// </param>
    /// <returns>
    /// <list type="bullet">
    ///   <item><description>
    ///     A request-level failure with <see cref="ValidationMessages.ValuesMustBeObject"/> when
    ///     <paramref name="submitted"/> is <see langword="null"/>.
    ///   </description></item>
    ///   <item><description>
    ///     A field-level failure holding every error found, keys in first-seen order. Per entry, the first failing
    ///     check among name, repeat, read-only, type and storage range is the field's only message; otherwise each
    ///     failing check among <c>MaxLength</c>, range, pattern and legal values adds its own.
    ///   </description></item>
    ///   <item><description>
    ///     Otherwise a valid result whose <see cref="RecordValidationResult.Values"/> holds every submitted data field
    ///     under its canonical name, in submission order, with its normalised value: empty text on a text, multiline
    ///     or legal-values field becomes <see cref="FieldValue.Null"/>, an ISO date string becomes a date value and an
    ///     option number becomes an index. Explicit nulls are kept; absent fields are not added, so a <c>PUT</c>
    ///     leaves them unchanged and a <c>POST</c> stores <c>NULL</c>.
    ///   </description></item>
    /// </list>
    /// </returns>
    /// <exception cref="ArgumentException">An entry has a <see langword="null"/> name.</exception>
    public RecordValidationResult ValidateInput(IReadOnlyList<KeyValuePair<string, FieldValue>>? submitted)
    {
        if (submitted is null)
        {
            return RecordValidationResult.RequestFailure(ValidationMessages.ValuesMustBeObject);
        }

        var errors = new FieldErrorCollector();
        var normalised = new OrderedDictionary<string, FieldValue>(submitted.Count, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, raw) in submitted)
        {
            if (name is null)
            {
                throw new ArgumentException("A submitted value must be keyed by a field name.", nameof(submitted));
            }

            if (!TryGetRule(name, out var rule))
            {
                // Keyed and worded by the one rule for such a name, so the submitted text is never a key.
                var (key, message) = NotADataFieldError(_form, name);
                errors.Add(key, message);
                continue;
            }

            if (!seen.Add(rule.Name))
            {
                errors.Add(rule.Name, ValidationMessages.DuplicateField);
                continue;
            }

            // Presence alone is the error, whatever the value, null included: the client never sends a read-only
            // field, and a value check code assigned to one is shown but not saved.
            if (rule.IsReadOnly)
            {
                errors.Add(rule.Name, ValidationMessages.ReadOnlyField);
                continue;
            }

            if (CheckValue(rule, raw, errors) is { } value)
            {
                normalised.Add(rule.Name, value);
            }
        }

        return errors.HasErrors ? errors.ToFailure() : RecordValidationResult.Success(normalised);
    }

    /// <summary>
    /// Runs the phase-2 checks of a <c>POST</c> on the values a successful <see cref="ValidateInput"/> returned,
    /// before any write transaction opens: the empty-record rule, then the required gate.
    /// </summary>
    /// <param name="normalised">
    /// The <see cref="RecordValidationResult.Values"/> of a valid <see cref="ValidateInput"/> result. Keys are matched
    /// to the data fields without regard to letter case; a field the map does not hold counts as
    /// <see cref="FieldValue.Null"/>.
    /// </param>
    /// <returns>
    /// <list type="bullet">
    ///   <item><description>
    ///     A request-level failure with <see cref="ValidationMessages.RecordHasNoValues"/> when every data field is
    ///     <see cref="FieldValue.Null"/>, an empty map included. An explicit <see langword="false"/> on a checkbox is a
    ///     value, so a record holding only unchecked checkboxes is not empty here. Unlike legacy data entry, which
    ///     ignores unchecked checkboxes when it decides whether a record is empty, the API applies the literal
    ///     all-null rule; the legacy rule belongs to the client editor, which then sends no request at all. This rule
    ///     is checked first, so an all-null create is reported as empty rather than as missing required fields.
    ///   </description></item>
    ///   <item><description>
    ///     A field-level failure listing every missing required field (see <see cref="CheckRequired"/> for the gate).
    ///   </description></item>
    ///   <item><description>
    ///     Otherwise a valid result holding the same values under their canonical names, for the store to insert.
    ///   </description></item>
    /// </list>
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="normalised"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="normalised"/> holds a null key, a key that names no data field of the form, or two keys that
    /// name the same field; none of these can come from a valid <see cref="ValidateInput"/> result.
    /// </exception>
    public RecordValidationResult CheckNewRecord(IReadOnlyDictionary<string, FieldValue> normalised)
    {
        ArgumentNullException.ThrowIfNull(normalised);

        var values = Canonicalise(normalised, nameof(normalised));
        if (values.Values.All(value => value.IsNull))
        {
            return RecordValidationResult.RequestFailure(ValidationMessages.RecordHasNoValues);
        }

        var errors = new FieldErrorCollector();
        AddMissingRequiredFields(values, errors);
        return errors.HasErrors ? errors.ToFailure() : RecordValidationResult.Success(values);
    }

    /// <summary>
    /// Runs the required gate of a <c>PUT</c> on the effective record: the stored values with the submitted ones laid
    /// over them. The store calls it inside the write transaction, after the locked read of the base row and every
    /// page row, so two concurrent saves of one record are serialised and neither passes the gate on stale state.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The gate takes, in data-field order, every field whose metadata marks it required and not read-only, and
    /// reports it missing when its effective value is absent, <see cref="FieldValue.Null"/> or empty text, as in
    /// legacy data entry, which tests the value string for null or empty. Submitted empty text was already normalised
    /// to null by <see cref="ValidateInput"/>; stored text is not normalised, so a stored empty string that the
    /// request leaves untouched is missing too. Whitespace counts as present and nothing is trimmed, and no other
    /// input check is applied to stored values. Every missing field is listed, where legacy stopped at the first, each
    /// with the legacy <c>FIELD_IS_REQUIRED_LONG</c> wording formatted with the field's prompt (its name when the
    /// prompt is blank) and its page name.
    /// </para>
    /// <para>
    /// A read-only field is never treated as required. Read-only fields are rejected whenever they are submitted and
    /// the client never sends them, so a required read-only field that is empty could never be filled and would block
    /// every save of the form; skipping it is the reconciliation that follows from that read-only rule.
    /// </para>
    /// <para>
    /// The empty-record rule of <see cref="CheckNewRecord"/> does not apply here: a <c>PUT</c> may clear fields. The
    /// method has no I/O and takes no lock, so it can be passed to the store as
    /// <c>stored =&gt; validator.CheckRequired(stored, values)</c>.
    /// </para>
    /// </remarks>
    /// <param name="stored">The record as the locked read returned it; a data field it does not hold counts as null.</param>
    /// <param name="submitted">
    /// The <see cref="RecordValidationResult.Values"/> of a valid <see cref="ValidateInput"/> result. Every entry,
    /// explicit nulls included, replaces the stored value of its field; keys are matched without regard to letter case.
    /// </param>
    /// <returns>
    /// A field-level failure listing every missing required field, after which the store rolls back before any write;
    /// otherwise a valid result holding the submitted values under their canonical names.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="stored"/> or <paramref name="submitted"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="submitted"/> holds a null key, a key that names no data field of the form, or two keys that
    /// name the same field; none of these can come from a valid <see cref="ValidateInput"/> result.
    /// </exception>
    public RecordValidationResult CheckRequired(StoredRecord stored, IReadOnlyDictionary<string, FieldValue> submitted)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(submitted);

        var values = Canonicalise(submitted, nameof(submitted));

        var effective = new Dictionary<string, FieldValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in stored.Values)
        {
            if (name is not null)
            {
                effective.TryAdd(name, value);
            }
        }

        foreach (var (name, value) in values)
        {
            effective[name] = value;
        }

        var errors = new FieldErrorCollector();
        AddMissingRequiredFields(effective, errors);
        return errors.HasErrors ? errors.ToFailure() : RecordValidationResult.Success(values);
    }

    // Normalisation, type, storage range, MaxLength, range, pattern and legal values for one submitted entry. Returns
    // the normalised value, or null after recording at least one message for the field.
    private static FieldValue? CheckValue(FieldRule rule, FieldValue raw, FieldErrorCollector errors)
    {
        // Legacy data entry writes NULL for an empty string, so empty text on a text-family field is no value. Only
        // the empty string is normalised: whitespace is data and nothing is trimmed. Empty text on any other field is
        // left to the type check, which rejects it.
        if (raw.IsNull || (rule.IsTextFamily && raw.TryGetText(out var rawText) && rawText.Length == 0))
        {
            return FieldValue.Null;
        }

        if (!TryCoerce(rule, raw, out var value, out var typeError))
        {
            errors.Add(rule.Name, typeError);
            return null;
        }

        // Out-of-domain input must be a validation error, never a storage exception, and the later checks assume a
        // value the column can hold.
        if (!FieldTypeMapper.IsRepresentable(rule.Column, value))
        {
            errors.Add(rule.Name, StorageMessage(rule, value));
            return null;
        }

        var valid = true;

        if (rule.MaxLengthMessage is { } maxLengthMessage
            && rule.MaxLength is { } maxLength
            && value.TryGetText(out var text)
            && text.Length > maxLength)
        {
            errors.Add(rule.Name, maxLengthMessage);
            valid = false;
        }

        if (rule.RangeMessage is { } rangeMessage && IsOutOfRange(rule, value))
        {
            errors.Add(rule.Name, rangeMessage);
            valid = false;
        }

        if (rule.PatternMessage is { } patternMessage && !rule.Mask.IsSatisfiedBy(value))
        {
            errors.Add(rule.Name, patternMessage);
            valid = false;
        }

        if (rule.LegalValues is { } legalValues && value.TryGetText(out var choice) && !legalValues.Contains(choice))
        {
            errors.Add(rule.Name, ValidationMessages.NotALegalValue);
            valid = false;
        }

        return valid ? value : null;
    }

    // Only the JSON scalar kinds the values reader produces are wire values. A value that is already a date or an
    // option index did not come from the wire, so it fails the type check like any other wrong kind: a date must
    // arrive as YYYY-MM-DD text and an option as a whole number, whoever the caller is. The FieldValue overload of
    // Coerce keeps a number exact, so 2.0000000000000001 can never become option index 2.
    private static bool TryCoerce(
        FieldRule rule,
        FieldValue raw,
        out FieldValue value,
        [NotNullWhen(false)] out string? error)
    {
        var isWireKind = raw.Kind is FieldValueKind.Text or FieldValueKind.Number or FieldValueKind.Boolean;
        var coercion = isWireKind
            ? FieldValueCoercion.Coerce(raw, rule.Kind)
            : FieldValueCoercionResult.Failure(FieldValueCoercionError.WrongType);

        if (rule.Kind == FieldValueKind.Index)
        {
            return TryOptionIndex(rule, raw, coercion, out value, out error);
        }

        if (coercion.Succeeded)
        {
            value = coercion.Value;
            error = null;
            return true;
        }

        value = FieldValue.Null;
        error = rule.Kind switch
        {
            FieldValueKind.Text => ValidationMessages.TextExpected,
            FieldValueKind.Number => ValidationMessages.NumberExpected,
            FieldValueKind.Date => ValidationMessages.DateExpected,
            _ => ValidationMessages.BooleanExpected,
        };
        return false;
    }

    // An option value must be a whole JSON number; only then do the option count and the index bounds apply. A whole
    // number below 0 or beyond the int range is a whole number outside the list, not a wrong type.
    private static bool TryOptionIndex(
        FieldRule rule,
        FieldValue raw,
        FieldValueCoercionResult coercion,
        out FieldValue value,
        [NotNullWhen(false)] out string? error)
    {
        value = FieldValue.Null;

        var isWholeNumber = raw.Kind == FieldValueKind.Number
            && (coercion.Succeeded
                || coercion.Error is FieldValueCoercionError.Negative or FieldValueCoercionError.OutOfRange);
        if (!isWholeNumber)
        {
            error = ValidationMessages.OptionIndexExpected;
            return false;
        }

        if (rule.OptionOutOfRangeMessage is not { } outOfRangeMessage)
        {
            error = ValidationMessages.NoOptionsAvailable;
            return false;
        }

        if (!coercion.Succeeded || !coercion.Value.TryGetIndex(out var index) || index >= rule.OptionCount)
        {
            error = outOfRangeMessage;
            return false;
        }

        value = coercion.Value;
        error = null;
        return true;
    }

    // One message per kind of value the column could not hold. Text is only ever too long for a character column,
    // so that case names the column's length.
    private static string StorageMessage(FieldRule rule, FieldValue value) => value.Kind switch
    {
        FieldValueKind.Text when rule.TextTooLongMessage is { } textTooLong => textTooLong,
        FieldValueKind.Number => ValidationMessages.NumberNotStorable,
        FieldValueKind.Date => ValidationMessages.DateNotStorable,
        _ => ValidationMessages.ValueNotStorable,
    };

    // Inclusive bounds, compared by what the column stores. The rule keeps only bounds of the field's own kind, a
    // number on a number field and a date on a date field, so a bound of another kind never applies. A number on an
    // integer column is the exact integer its storage range check admitted, and it is compared with the rule's exact
    // integer limits, so a bound beyond 2^53 or with a fraction a double rounds away neither admits nor rejects an
    // integer it should not. Every other value is compared with the bound itself.
    private static bool IsOutOfRange(FieldRule rule, FieldValue value)
    {
        if (rule.UsesIntegerLimits)
        {
            if (!value.TryGetInteger(out var integer))
            {
                throw new InvalidOperationException(
                    "A number on an integer column must hold an exact integer once its storage range is checked.");
            }

            return (rule.LowerLimit is { } lowerLimit && integer < lowerLimit)
                || (rule.UpperLimit is { } upperLimit && integer > upperLimit);
        }

        return (rule.Lower is { } lower && Compare(value, lower) < 0)
            || (rule.Upper is { } upper && Compare(value, upper) > 0);
    }

    // Dates compare as calendar dates. A number reaching here is on a float column, which stores a binary64, so value
    // and bound compare as their nearest doubles, even when both hold exact integers: two numbers the column stores
    // as one double are on the same side of every bound.
    private static int Compare(FieldValue value, FieldValue bound)
    {
        if (value.TryGetDate(out var date) && bound.TryGetDate(out var boundDate))
        {
            return date.CompareTo(boundDate);
        }

        if (value.TryGetNumber(out var number) && bound.TryGetNumber(out var boundNumber))
        {
            return number.CompareTo(boundNumber);
        }

        return 0;
    }

    private bool TryGetRule(string name, [NotNullWhen(true)] out FieldRule? rule)
    {
        if (_dataFields.TryGet(name, out var dataField) && _rulesByField.TryGetValue(dataField, out rule))
        {
            return true;
        }

        rule = null;
        return false;
    }

    // Re-keys a map of normalised values by canonical name, in its own order, rejecting what a valid ValidateInput
    // result can never hold. The messages are fixed, so no key or value reaches an exception message.
    private OrderedDictionary<string, FieldValue> Canonicalise(
        IReadOnlyDictionary<string, FieldValue> values,
        string paramName)
    {
        var canonical = new OrderedDictionary<string, FieldValue>(values.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in values)
        {
            if (name is null)
            {
                throw new ArgumentException("A value must be keyed by a field name.", paramName);
            }

            if (!TryGetRule(name, out var rule))
            {
                throw new ArgumentException("A value is keyed by a name that is not a data field of the form.", paramName);
            }

            if (!canonical.TryAdd(rule.Name, value))
            {
                throw new ArgumentException(
                    "Each field may be given only once; names are compared without regard to letter case.",
                    paramName);
            }
        }

        return canonical;
    }

    // A required field is missing when its effective value is absent, null or empty text, as legacy data entry tests
    // the value string for null or empty. Submitted empty text is already null, but stored text is read back as it is,
    // so an empty string kept in a page row must count as missing too. Whitespace is a value and nothing is trimmed;
    // no other rule is applied, so a stored value the request does not touch is never re-validated.
    private void AddMissingRequiredFields(IReadOnlyDictionary<string, FieldValue> effective, FieldErrorCollector errors)
    {
        foreach (var rule in _rules)
        {
            if (rule.RequiredMessage is { } requiredMessage
                && (!effective.TryGetValue(rule.Name, out var value)
                    || value.IsNull
                    || (value.TryGetText(out var text) && text.Length == 0)))
            {
                errors.Add(rule.Name, requiredMessage);
            }
        }
    }


    // Everything the checks need about one data field, interpreted once from the metadata and the form definition,
    // with every metadata-only message formatted in advance. Immutable after construction.
    private sealed class FieldRule
    {
        public FieldRule(DataFieldMetadata dataField, FieldDefinition definition, FieldValueKind kind)
        {
            var validation = definition.Validation;

            Name = dataField.Name;
            FieldType = dataField.FieldType;
            Kind = kind;
            IsTextFamily = FieldTypeMapper.IsTextFamily(dataField.FieldType);
            Column = dataField.Column;

            // The factory copies the metadata flag, so the two sources agree; taking either keeps a disagreement from
            // ever letting a protected field be written.
            IsReadOnly = validation.ReadOnly || dataField.Field.IsReadOnly;

            // A required read-only field is left out of the gate: it could never be supplied (see CheckRequired).
            if (validation.Required && !IsReadOnly)
            {
                RequiredMessage = ValidationMessages.FormatFieldIsRequired(definition.Prompt, Name, dataField.Page.Name);
            }

            if (Column is { Family: ColumnFamily.Character, MaxCharacters: { } maxCharacters } && maxCharacters > 0)
            {
                TextTooLongMessage = ValidationMessages.FormatTextTooLongForColumn(maxCharacters);
            }

            if (IsTextFamily && validation.MaxLength is { } maxLength && maxLength > 0)
            {
                MaxLength = maxLength;
                MaxLengthMessage = ValidationMessages.FormatMaxLengthExceeded(maxLength);
            }

            if (kind is FieldValueKind.Number or FieldValueKind.Date)
            {
                Lower = validation.Lower is { } lower && lower.Kind == kind ? lower : null;
                Upper = validation.Upper is { } upper && upper.Kind == kind ? upper : null;
            }

            // An integer column stores whole numbers only, so its inclusive limits are the least integer at or above
            // Lower and the greatest at or below Upper, read exactly from the metadata text: the bound's nearest
            // double can cross an integer, as 9007199254740992.5 rounds down to 9007199254740992 and
            // 2.0000000000000001 to 2. Its range message names those limits; any other field's names its bounds.
            if (kind == FieldValueKind.Number && Column.Family == ColumnFamily.Integer)
            {
                UsesIntegerLimits = true;
                if (Lower is { } lowerBound)
                {
                    LowerLimit = ReadIntegerLimits(dataField.Field.Lower, lowerBound, nameof(definition)).Ceiling;
                }

                if (Upper is { } upperBound)
                {
                    UpperLimit = ReadIntegerLimits(dataField.Field.Upper, upperBound, nameof(definition)).Floor;
                }

                RangeMessage = FormatIntegerRange(Lower, LowerLimit, Upper, UpperLimit);
            }
            else
            {
                RangeMessage = FormatRange(Lower, Upper);
            }

            Mask = MaskPattern.Parse(validation.Pattern, FieldType, Column.Family);
            if (Mask.IsChecked)
            {
                PatternMessage = ValidationMessages.FormatPatternMismatch(Mask.Pattern);
            }

            if (FieldType == FieldType.LegalValues)
            {
                // Compared ordinally and case-sensitively: the stored value must be exactly one code-table text.
                LegalValues = (definition.Options ?? [])
                    .Select(option => option.Value.TryGetText(out var optionText) ? optionText : null)
                    .OfType<string>()
                    .ToFrozenSet(StringComparer.Ordinal);
            }

            if (kind == FieldValueKind.Index)
            {
                OptionCount = definition.Options?.Count ?? 0;
                if (OptionCount > 0)
                {
                    OptionOutOfRangeMessage = ValidationMessages.FormatOptionIndexOutOfRange(OptionCount);
                }
            }
        }

        /// <summary>The canonical <c>metaFields.Name</c>, the key of the field's value and of its errors.</summary>
        public string Name { get; }

        public FieldType FieldType { get; }

        /// <summary>The kind of value the field holds, from <see cref="FieldTypeMapper.ValueKind"/>.</summary>
        public FieldValueKind Kind { get; }

        public bool IsTextFamily { get; }

        public ColumnMetadata Column { get; }

        public bool IsReadOnly { get; }

        /// <summary>The required-gate message; <see langword="null"/> when the gate does not apply.</summary>
        public string? RequiredMessage { get; }

        /// <summary>The storage message for text longer than a bounded character column.</summary>
        public string? TextTooLongMessage { get; }

        /// <summary>The metadata <c>MaxLength</c>, set only when it is above 0 on a text-family field.</summary>
        public int? MaxLength { get; }

        public string? MaxLengthMessage { get; }

        /// <summary>
        /// The inclusive lower bound, kept only when it has the field's own kind. A date or a number on a float column
        /// is compared with it and the range message names it; an integer column compares with, and names,
        /// <see cref="LowerLimit"/> instead.
        /// </summary>
        public FieldValue? Lower { get; }

        /// <summary>
        /// The inclusive upper bound, kept only when it has the field's own kind. A date or a number on a float column
        /// is compared with it and the range message names it; an integer column compares with, and names,
        /// <see cref="UpperLimit"/> instead.
        /// </summary>
        public FieldValue? Upper { get; }

        /// <summary>
        /// Whether the range compares the value's exact integer with <see cref="LowerLimit"/> and
        /// <see cref="UpperLimit"/>, and the range message names them: a number field on an integer column.
        /// </summary>
        public bool UsesIntegerLimits { get; }

        /// <summary>
        /// The least integer at or above the metadata <c>Lower</c> text, set only on an integer column with a
        /// <see cref="Lower"/> bound. The range message names it, so <c>2.0000000000000001</c> reads "at least 3".
        /// </summary>
        public BigInteger? LowerLimit { get; }

        /// <summary>
        /// The greatest integer at or below the metadata <c>Upper</c> text, set only on an integer column with an
        /// <see cref="Upper"/> bound. The range message names it, so <c>9007199254740993.5</c> reads
        /// "at most 9007199254740993".
        /// </summary>
        public BigInteger? UpperLimit { get; }

        /// <summary>
        /// The range message, naming the values the range check enforces: <see cref="LowerLimit"/> and
        /// <see cref="UpperLimit"/> on an integer column, else <see cref="Lower"/> and <see cref="Upper"/>;
        /// <see langword="null"/> when the field has no applicable bound.
        /// </summary>
        public string? RangeMessage { get; }

        /// <summary>
        /// The metadata pattern, interpreted once here for the field type and column, so a save-time check never
        /// parses it again.
        /// </summary>
        public ParsedMask Mask { get; }

        /// <summary>The pattern message; <see langword="null"/> when the pattern is one the server does not check.</summary>
        public string? PatternMessage { get; }

        /// <summary>The option texts of a legal-values field, possibly empty; <see langword="null"/> for other types.</summary>
        public FrozenSet<string>? LegalValues { get; }

        /// <summary>The number of options of an option field; 0 for other types.</summary>
        public int OptionCount { get; }

        /// <summary>The out-of-range message of an option field with at least one option.</summary>
        public string? OptionOutOfRangeMessage { get; }

        // The exact integer limits of one bound, read from the metadata text the factory parses it from. That text
        // must parse to the very bound the definition holds; anything else means the definition was not built from
        // this metadata, and the limits would enforce, and the range message name, a bound the definition does not
        // carry.
        private static (BigInteger Floor, BigInteger Ceiling) ReadIntegerLimits(
            string? text,
            FieldValue bound,
            string paramName)
        {
            if (!FieldValueCoercion.TryParseNumber(text, out var parsed)
                || parsed != bound
                || !FieldValueCoercion.TryParseIntegerLimits(text, out var floor, out var ceiling))
            {
                throw new ArgumentException(
                    "The form definition gives a number field a bound that the form metadata does not state.",
                    paramName);
            }

            return (floor, ceiling);
        }

        // The range message of a date or of a number on a float column, which names the bounds the range check
        // compares with. The ValidationMessages helpers own bound formatting (invariant; dates as yyyy-MM-dd, an exact
        // integer with every digit, and any other number in shortest round-trip form), so a range message reads the
        // same wherever it is built. A bound that holds an exact integer is passed as that integer, never as its
        // double, so a bound beyond 2^53 such as 9007199254740993 is named as the metadata states it, not rounded, and
        // a fractional bound such as 10.5 is named as its shortest round-trip double.
        private static string? FormatRange(FieldValue? lower, FieldValue? upper)
        {
            if (lower is { } lowerBound && upper is { } upperBound)
            {
                if (lowerBound.TryGetDate(out var lowerDate) && upperBound.TryGetDate(out var upperDate))
                {
                    return ValidationMessages.FormatRangeBetween(lowerDate, upperDate);
                }

                var lowerIsInteger = lowerBound.TryGetInteger(out var lowerInteger);
                var upperIsInteger = upperBound.TryGetInteger(out var upperInteger);
                return (lowerIsInteger, upperIsInteger) switch
                {
                    (true, true) => ValidationMessages.FormatRangeBetween(lowerInteger, upperInteger),
                    (true, false) => ValidationMessages.FormatRangeBetween(lowerInteger, upperBound.AsNumber()),
                    (false, true) => ValidationMessages.FormatRangeBetween(lowerBound.AsNumber(), upperInteger),
                    (false, false) =>
                        ValidationMessages.FormatRangeBetween(lowerBound.AsNumber(), upperBound.AsNumber()),
                };
            }

            if (lower is { } onlyLower)
            {
                if (onlyLower.TryGetDate(out var date))
                {
                    return ValidationMessages.FormatRangeAtLeast(date);
                }

                return onlyLower.TryGetInteger(out var integer)
                    ? ValidationMessages.FormatRangeAtLeast(integer)
                    : ValidationMessages.FormatRangeAtLeast(onlyLower.AsNumber());
            }

            if (upper is { } onlyUpper)
            {
                if (onlyUpper.TryGetDate(out var date))
                {
                    return ValidationMessages.FormatRangeAtMost(date);
                }

                return onlyUpper.TryGetInteger(out var integer)
                    ? ValidationMessages.FormatRangeAtMost(integer)
                    : ValidationMessages.FormatRangeAtMost(onlyUpper.AsNumber());
            }

            return null;
        }

        // The range message of a number on an integer column, which names the integer limits the range check
        // enforces rather than the bounds they come from, whose nearest doubles can cross an integer: Upper
        // 9007199254740993.5 reads "at most 9007199254740993", Lower 2.0000000000000001 "at least 3" and Lower 0.5
        // with Upper 99.5 "between 1 and 99". A limit within the signed 64-bit range is passed as that integer, so it
        // keeps every digit. Only a bound beyond the bigint range, such as 1e30, gives a limit outside it; every value
        // the column stores is then on one side of it, and it is named by its bound's shortest round-trip double,
        // 1E+30. Which bounds are set chooses the message, as in FormatRange.
        private static string? FormatIntegerRange(
            FieldValue? lower,
            BigInteger? lowerLimit,
            FieldValue? upper,
            BigInteger? upperLimit)
        {
            if (lower is { } lowerBound && lowerLimit is { } lowerInteger
                && upper is { } upperBound && upperLimit is { } upperInteger)
            {
                var lowerFits = TryGetInt64(lowerInteger, out var lowerLong);
                var upperFits = TryGetInt64(upperInteger, out var upperLong);
                return (lowerFits, upperFits) switch
                {
                    (true, true) => ValidationMessages.FormatRangeBetween(lowerLong, upperLong),
                    (true, false) => ValidationMessages.FormatRangeBetween(lowerLong, upperBound.AsNumber()),
                    (false, true) => ValidationMessages.FormatRangeBetween(lowerBound.AsNumber(), upperLong),
                    (false, false) =>
                        ValidationMessages.FormatRangeBetween(lowerBound.AsNumber(), upperBound.AsNumber()),
                };
            }

            if (lower is { } onlyLower && lowerLimit is { } onlyLowerInteger)
            {
                return TryGetInt64(onlyLowerInteger, out var integer)
                    ? ValidationMessages.FormatRangeAtLeast(integer)
                    : ValidationMessages.FormatRangeAtLeast(onlyLower.AsNumber());
            }

            if (upper is { } onlyUpper && upperLimit is { } onlyUpperInteger)
            {
                return TryGetInt64(onlyUpperInteger, out var integer)
                    ? ValidationMessages.FormatRangeAtMost(integer)
                    : ValidationMessages.FormatRangeAtMost(onlyUpper.AsNumber());
            }

            return null;
        }

        private static bool TryGetInt64(BigInteger integer, out long value)
        {
            var fits = integer >= long.MinValue && integer <= long.MaxValue;
            value = fits ? (long)integer : 0L;
            return fits;
        }
    }

    // Collects field-level messages in first-seen key order. Keys compare ordinally, as in RecordValidationResult; a
    // message already recorded for a key is not repeated, so a name submitted three times reads one duplicate error.
    private sealed class FieldErrorCollector
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

            if (!messages.Contains(message))
            {
                messages.Add(message);
            }
        }

        public RecordValidationResult ToFailure()
        {
            var errors = new OrderedDictionary<string, IReadOnlyList<string>>(_errors.Count, StringComparer.Ordinal);
            foreach (var (key, messages) in _errors)
            {
                errors.Add(key, messages);
            }

            return RecordValidationResult.FieldFailure(errors);
        }
    }
}
