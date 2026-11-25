using System.Diagnostics.CodeAnalysis;
using EpiInfo.Domain.Forms;
using EpiInfo.Domain.Metadata;
using EpiInfo.Domain.Values;

namespace EpiInfo.CheckCode.Evaluation;

/// <summary>
/// The field scope of one evaluator run: it resolves check-code identifiers to the form's canonical fields, reports
/// each field's type and group members, and holds the current value of every field, so later statements read the
/// results of earlier <c>ASSIGN</c> and <c>CLEAR</c> statements.
/// </summary>
/// <remarks>
/// <para>
/// It takes the role legacy <c>Rule_Context</c> and the host's <c>TryGetFieldInfo</c> played for field reads, with
/// no persistent scope: it is created from the request's values for exactly one <c>Evaluator.Run</c> call and is
/// discarded with it. Nothing it holds survives the call or is written anywhere.
/// </para>
/// <para>
/// Identifiers are matched without regard to case and surrounding whitespace, so the seeded program's <c>AGE</c>
/// resolves to the field <c>Age</c>. Field types come only from <see cref="FieldTypeMapper"/>, the single owner of
/// that mapping.
/// </para>
/// <para>
/// It enforces no read-only, range, length, mask, legal-value, required or empty-record rule: the engine must
/// evaluate records whose stored values break the current rules, and validation belongs to the save path.
/// </para>
/// <para>
/// An instance serves one run on one thread and is not thread-safe. It holds no static state, so concurrent runs
/// never share anything.
/// </para>
/// </remarks>
public sealed class EvaluationContext
{
    // Current values keyed by canonical metaFields.Name: the request's values, overlaid by the run's writes.
    private readonly Dictionary<string, FieldValue> _values = new(StringComparer.OrdinalIgnoreCase);

    // Mapped types per metadata row. Keyed by reference, because the rows come from this form's metadata and a
    // value-equality key would hash every property of the row on each lookup.
    private readonly Dictionary<FieldMetadata, FieldType> _fieldTypes = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Creates the scope of one run over <paramref name="form"/>, starting from <paramref name="values"/>.
    /// </summary>
    /// <param name="form">The metadata of the form whose program runs.</param>
    /// <param name="values">
    /// The record's current values keyed by field name in any letter case. Each key is stored under the canonical
    /// name of the field it resolves to, and a key that resolves to no field of the form is ignored; the field-event
    /// service has already rejected such keys, and admits data fields only. The dictionary is copied: the context
    /// keeps no reference to it and never changes it.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="form"/> or <paramref name="values"/> is <see langword="null"/>.
    /// </exception>
    public EvaluationContext(FormMetadata form, IReadOnlyDictionary<string, FieldValue> values)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(values);

        Form = form;
        foreach (KeyValuePair<string, FieldValue> entry in values)
        {
            if (Form.FindField(entry.Key) is { } field)
            {
                _values[field.Name] = entry.Value;
            }
        }
    }

    /// <summary>The metadata of the form whose program runs.</summary>
    public FormMetadata Form { get; }

    /// <summary>
    /// The form's <c>metaViews.Name</c>, for example <c>FoodHistory</c>, which every diagnostic of the run carries
    /// as its form.
    /// </summary>
    public string FormName => Form.Name;

    /// <summary>
    /// Resolves a check-code identifier to the form's field, ignoring case and surrounding whitespace.
    /// </summary>
    /// <param name="identifier">The identifier as written in the program, for example <c>AGE</c>.</param>
    /// <param name="field">
    /// The field with its canonical name, such as <c>Age</c>; <see langword="null"/> when none matches.
    /// </param>
    /// <returns>Whether the identifier names a field of the form.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="identifier"/> is <see langword="null"/>.</exception>
    public bool TryResolve(string identifier, [NotNullWhen(true)] out FieldMetadata? field)
    {
        ArgumentNullException.ThrowIfNull(identifier);

        field = Form.FindField(identifier.Trim());
        return field is not null;
    }

    /// <summary>
    /// Resolves a check-code identifier to the form's field, ignoring case and surrounding whitespace, and fails the
    /// statement when the form has no such field.
    /// </summary>
    /// <param name="identifier">The identifier as written in the program, for example <c>AGE</c>.</param>
    /// <param name="line">The 1-based line of the statement that names it.</param>
    /// <param name="command">The upper-case keyword of that statement, for example <c>ASSIGN</c>.</param>
    /// <returns>The field with its canonical name.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="identifier"/> or <paramref name="command"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="CheckCodeRuntimeException">
    /// The form has no field of that name. The reason names the identifier, which is program text, never a value.
    /// </exception>
    public FieldMetadata Resolve(string identifier, int line, string command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (TryResolve(identifier, out FieldMetadata? field))
        {
            return field;
        }

        throw new CheckCodeRuntimeException(line, command, $"Unknown field '{identifier.Trim()}'.");
    }

    /// <summary>
    /// The field's type as <see cref="FieldTypeMapper.Map"/> decides it, computed once per field for the run.
    /// </summary>
    /// <param name="field">A field of the form.</param>
    /// <returns>
    /// The mapped type; <see cref="FieldType.Unsupported"/> for a field the application does not edit.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="field"/> is <see langword="null"/>.</exception>
    public FieldType GetFieldType(FieldMetadata field)
    {
        ArgumentNullException.ThrowIfNull(field);

        if (!_fieldTypes.TryGetValue(field, out FieldType type))
        {
            type = FieldTypeMapper.Map(field).FieldType;
            _fieldTypes[field] = type;
        }

        return type;
    }

    /// <summary>
    /// Whether the field holds a value: text, multiline, number, date, checkbox, option or legal values. Labels,
    /// groups and unsupported fields do not.
    /// </summary>
    /// <param name="field">A field of the form.</param>
    /// <returns><see langword="true"/> for a data field.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="field"/> is <see langword="null"/>.</exception>
    public bool IsDataField(FieldMetadata field)
    {
        FieldType type = GetFieldType(field);
        return type != FieldType.Unsupported && FieldTypeMapper.IsDataFieldType(type);
    }

    /// <summary>
    /// Whether the field is a group, whose name in <c>ENABLE</c>, <c>DISABLE</c> and <c>CLEAR</c> stands for its
    /// members.
    /// </summary>
    /// <param name="field">A field of the form.</param>
    /// <returns><see langword="true"/> for a group.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="field"/> is <see langword="null"/>.</exception>
    public bool IsGroup(FieldMetadata field) => GetFieldType(field) == FieldType.Group;

    /// <summary>
    /// The members of a group, read from its <c>metaFields.List</c> as legacy <c>GroupField.ChildFieldNameArray</c>
    /// splits it on commas, for example <c>Race,Ethnicity,Sex,Age,Zip,County,State</c> on the seeded
    /// <c>DemographicInformation</c>.
    /// </summary>
    /// <param name="group">A field of the form.</param>
    /// <returns>
    /// The canonical fields the list names, in list order. Each item is trimmed, empty items and names the form does
    /// not have are dropped, and a field named twice is kept at its first place. A member that is itself a group is
    /// returned as it is, not expanded, as in legacy. The list is empty when <paramref name="group"/> is not a group.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="group"/> is <see langword="null"/>.</exception>
    public IReadOnlyList<FieldMetadata> GetGroupMembers(FieldMetadata group)
    {
        if (!IsGroup(group) || string.IsNullOrEmpty(group.List))
        {
            return [];
        }

        var members = new List<FieldMetadata>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        const StringSplitOptions ItemOptions = StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries;
        foreach (string item in group.List.Split(',', ItemOptions))
        {
            if (Form.FindField(item) is { } member && seen.Add(member.Name))
            {
                members.Add(member);
            }
        }

        return members;
    }

    /// <summary>
    /// The field's current value: the last value written in this run, otherwise the request's value.
    /// </summary>
    /// <param name="field">A field of the form.</param>
    /// <returns>
    /// The current value, or <see cref="FieldValue.Null"/> when the request held none and the run wrote none, as for
    /// labels, groups and unsupported fields.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="field"/> is <see langword="null"/>.</exception>
    public FieldValue Read(FieldMetadata field)
    {
        ArgumentNullException.ThrowIfNull(field);

        return _values.TryGetValue(field.Name, out FieldValue value) ? value : FieldValue.Null;
    }

    /// <summary>
    /// Resolves a check-code identifier with <see cref="Resolve"/> and returns that field's current value.
    /// </summary>
    /// <param name="identifier">The identifier as written in the program.</param>
    /// <param name="line">The 1-based line of the statement that reads it.</param>
    /// <param name="command">The upper-case keyword of that statement.</param>
    /// <returns>The current value, as <see cref="Read(FieldMetadata)"/> returns it.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="identifier"/> or <paramref name="command"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="CheckCodeRuntimeException">The form has no field of that name.</exception>
    public FieldValue Read(string identifier, int line, string command) => Read(Resolve(identifier, line, command));

    /// <summary>
    /// Sets the field's current value for the rest of the run: an <c>ASSIGN</c> writes its coerced value and a
    /// <c>CLEAR</c> writes <see cref="FieldValue.Null"/>. Nothing is persisted, and no rule is checked.
    /// </summary>
    /// <param name="field">A field of the form.</param>
    /// <param name="value">The new value.</param>
    /// <exception cref="ArgumentNullException"><paramref name="field"/> is <see langword="null"/>.</exception>
    public void Write(FieldMetadata field, FieldValue value)
    {
        ArgumentNullException.ThrowIfNull(field);

        _values[field.Name] = value;
    }
}
