using System.Collections;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using EpiInfo.Domain.Diagnostics;
using EpiInfo.Domain.Forms;
using EpiInfo.Domain.Values;

namespace EpiInfo.Domain.Metadata;

/// <summary>
/// One data field of a form: a page-bound <c>metaFields</c> row whose mapped type holds a value, with the page and
/// the page-table column that store it.
/// </summary>
/// <param name="Field">The field's metadata row.</param>
/// <param name="FieldType">
/// The type <see cref="FieldTypeMapper.Map"/> gives the row; never <see cref="FieldType.Label"/>,
/// <see cref="FieldType.Group"/> or <see cref="FieldType.Unsupported"/>.
/// </param>
/// <param name="Page">The page the field sits on, whose table holds <paramref name="Column"/>.</param>
/// <param name="Column">The page-table column that stores the field's value.</param>
public sealed record DataFieldMetadata(FieldMetadata Field, FieldType FieldType, PageMetadata Page, ColumnMetadata Column)
{
    /// <summary>
    /// The canonical <c>metaFields.Name</c>: the key of the field's <c>values</c> entry and its column name.
    /// </summary>
    public string Name => Field.Name;
}

/// <summary>
/// The data fields of a form in storage order: page <c>Position</c>, then <c>PageId</c>, then <c>TabIndex</c>, then
/// <c>FieldId</c>. It is the one answer to which fields hold values, built only by
/// <see cref="FormDefinitionFactory.GetDataFields"/>.
/// </summary>
/// <remarks>
/// <para>
/// The set is immutable after construction, so one instance can be read from any number of threads.
/// </para>
/// <para>
/// A data field's identity is its exact canonical <c>metaFields.Name</c>: two fields whose names differ only by
/// letter case, on different pages for example, are distinct fields with distinct <c>values</c> keys. Every name
/// lookup follows one rule:
/// </para>
/// <list type="number">
/// <item><description>
/// A name spelled exactly as a canonical name resolves to the first field in storage order so spelled.
/// </description></item>
/// <item><description>
/// Otherwise, when exactly one distinct canonical spelling equals the name ignoring case, the name resolves to the
/// first field so spelled, so <c>age</c> in a request resolves to <c>Age</c>. Fields that repeat one spelling
/// exactly count as that one name.
/// </description></item>
/// <item><description>
/// Otherwise, when two or more distinct canonical spellings equal the name ignoring case, as <c>Age</c> and
/// <c>AGE</c> both equal <c>age</c>, the name is ambiguous and resolves to no field, because nothing in it says
/// which field is meant. <see cref="IsAmbiguous"/> tells such a name apart from one the form does not have.
/// </description></item>
/// </list>
/// </remarks>
public sealed class DataFieldSet : IReadOnlyList<DataFieldMetadata>
{
    private readonly DataFieldMetadata[] _fields;
    private readonly Dictionary<string, DataFieldMetadata> _byExactName;
    private readonly Dictionary<string, DataFieldMetadata> _byUnambiguousName;
    private readonly HashSet<string> _ambiguousNames;

    internal DataFieldSet(IEnumerable<DataFieldMetadata> orderedFields)
    {
        _fields = orderedFields.ToArray();
        _byExactName = new Dictionary<string, DataFieldMetadata>(_fields.Length, StringComparer.Ordinal);
        _byUnambiguousName =
            new Dictionary<string, DataFieldMetadata>(_fields.Length, StringComparer.OrdinalIgnoreCase);
        _ambiguousNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in _fields)
        {
            // The first field in storage order keeps each exact spelling, so an exact repeat resolves to it and adds
            // no spelling. A new spelling of a name already held ignoring case is a second distinct spelling, which
            // makes the name ambiguous.
            if (_byExactName.TryAdd(field.Name, field) && !_byUnambiguousName.TryAdd(field.Name, field))
            {
                _ambiguousNames.Add(field.Name);
            }
        }

        // An ambiguous name resolves only through its exact spellings, never ignoring case.
        foreach (var name in _ambiguousNames)
        {
            _byUnambiguousName.Remove(name);
        }
    }

    /// <summary>Gets the data field at <paramref name="index"/> in storage order.</summary>
    /// <param name="index">The 0-based position.</param>
    /// <returns>The data field.</returns>
    /// <exception cref="IndexOutOfRangeException"><paramref name="index"/> is outside the set.</exception>
    public DataFieldMetadata this[int index] => _fields[index];

    /// <summary>Gets the number of data fields.</summary>
    public int Count => _fields.Length;

    /// <summary>
    /// Finds the data field named <paramref name="name"/>: the first field in storage order whose canonical name
    /// equals it exactly; or else, when exactly one distinct canonical spelling equals it ignoring case, the first
    /// field so spelled.
    /// </summary>
    /// <param name="name">A field name in any letter case.</param>
    /// <param name="field">The canonical entry, or <see langword="null"/> when none matches.</param>
    /// <returns>
    /// Whether a data field matches; <see langword="false"/> for a null or empty name, for labels, groups,
    /// unsupported fields, system fields and names the form does not have, and for an ambiguous name, one that
    /// equals no canonical name exactly and two or more distinct canonical spellings ignoring case (see
    /// <see cref="IsAmbiguous"/>).
    /// </returns>
    public bool TryGet(string name, [NotNullWhen(true)] out DataFieldMetadata? field)
    {
        if (string.IsNullOrEmpty(name))
        {
            field = null;
            return false;
        }

        return _byExactName.TryGetValue(name, out field) || _byUnambiguousName.TryGetValue(name, out field);
    }

    /// <summary>
    /// Finds the data field named <paramref name="name"/>, as <see cref="TryGet"/> resolves it: the exact canonical
    /// spelling first, then the one distinct canonical spelling equal to it ignoring case. An ambiguous name finds
    /// no field.
    /// </summary>
    /// <param name="name">A field name in any letter case.</param>
    /// <returns>
    /// The canonical entry, or <see langword="null"/> when none matches or <paramref name="name"/> is ambiguous.
    /// </returns>
    public DataFieldMetadata? Find(string name) => TryGet(name, out var field) ? field : null;

    /// <summary>
    /// Whether <paramref name="name"/> is ambiguous: it equals no canonical name exactly, and two or more distinct
    /// canonical spellings equal it ignoring case, as <c>Age</c> and <c>AGE</c> both equal <c>age</c>. Such a name
    /// resolves to no field, so no caller treats it as a field it might mean: in the <c>values</c> of a record write
    /// or a field event it is reported under <see cref="Validation.RecordValidator.ValuesKey"/> with
    /// <see cref="Validation.ValidationMessages.AmbiguousValuesName"/>, unless a row that holds no value is spelled
    /// exactly so (<see cref="Validation.RecordValidator.NotADataFieldError"/>).
    /// </summary>
    /// <param name="name">A field name in any letter case.</param>
    /// <returns>
    /// <see langword="true"/> only for an ambiguous name; <see langword="false"/> for a null or empty name, a name
    /// spelled exactly as a data field, a name equal ignoring case to one distinct canonical spelling only (however
    /// many fields repeat it exactly), and a name no data field has.
    /// </returns>
    public bool IsAmbiguous(string name) =>
        !string.IsNullOrEmpty(name) && !_byExactName.ContainsKey(name) && _ambiguousNames.Contains(name);

    /// <inheritdoc />
    public IEnumerator<DataFieldMetadata> GetEnumerator() => ((IEnumerable<DataFieldMetadata>)_fields).GetEnumerator();

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// The one owner of form-definition assembly, group membership and the data-field set. The form service builds the
/// form definition here, and every consumer that needs to know which fields hold values asks
/// <see cref="GetDataFields"/> rather than deriving it again.
/// </summary>
/// <remarks>
/// Every member is a pure function of its arguments: no state, no I/O, ordinal or invariant comparisons only, and
/// the same output for the same input, so the class is safe to call from any thread. Nothing about any particular
/// form is fixed here; everything comes from the metadata passed in.
/// </remarks>
public static class FormDefinitionFactory
{
    /// <summary>
    /// Builds the form definition returned by <c>GET /api/forms/{viewId}</c>.
    /// </summary>
    /// <param name="form">The form's metadata, as read on this request.</param>
    /// <param name="legalValueOptions">
    /// The options of each Legal Values field, keyed by canonical field name, in the order the store returned them:
    /// the code-table row order, already sorted by the store when <c>Sort</c> is set. That order is kept as given; the
    /// factory never reorders it. Each field takes only the entry its exact name finds under the map's own comparer,
    /// so fields whose names differ only by letter case never share a list in an ordinal map. A field with no entry
    /// gets an empty list.
    /// </param>
    /// <param name="unsupportedCheckCode">The static check-code diagnostics of the form's program.</param>
    /// <param name="canInsert">The insert-permission flag, or <see langword="null"/> when not checked.</param>
    /// <param name="canUpdate">The update-permission flag, or <see langword="null"/> when not checked.</param>
    /// <param name="canDelete">The delete-permission flag, or <see langword="null"/> when not checked.</param>
    /// <returns>
    /// The definition: pages in <c>Position</c> order, then <c>PageId</c>; on each page every <c>metaFields</c> row
    /// with that <c>PageId</c>, labels, groups and unsupported fields included, in <c>TabIndex</c> order, then
    /// <c>FieldId</c>. System rows, whose <c>PageId</c> is null, are never listed.
    /// </returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="unsupportedCheckCode"/> holds a null entry.</exception>
    public static FormDefinition Create(
        FormMetadata form,
        IReadOnlyDictionary<string, IReadOnlyList<FieldOption>> legalValueOptions,
        IReadOnlyList<Diagnostic> unsupportedCheckCode,
        bool? canInsert = null,
        bool? canUpdate = null,
        bool? canDelete = null)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(legalValueOptions);
        ArgumentNullException.ThrowIfNull(unsupportedCheckCode);

        var diagnostics = unsupportedCheckCode.ToArray();
        if (Array.Exists(diagnostics, diagnostic => diagnostic is null))
        {
            throw new ArgumentException("A check-code diagnostic must not be null.", nameof(unsupportedCheckCode));
        }

        var listedFieldsByName = ListedFieldsByName(form);
        var pages = OrderedPages(form)
            .Select(page => new PageDefinition(
                page.PageId,
                page.Name,
                page.Position,
                Array.AsReadOnly(FieldsOnPage(form, page)
                    .Select(field => Definition(field, page, legalValueOptions, listedFieldsByName))
                    .ToArray())))
            .ToArray();

        return new FormDefinition(
            form.ViewId,
            form.Name,
            Array.AsReadOnly(pages),
            Array.AsReadOnly(diagnostics),
            canInsert,
            canUpdate,
            canDelete);
    }

    /// <summary>
    /// Selects the data fields of a form: every page-bound field whose page belongs to the form, which owns a
    /// page-table column, and whose mapped type holds a value (not a label, group or unsupported field).
    /// </summary>
    /// <param name="form">The form's metadata.</param>
    /// <returns>
    /// The data fields in page <c>Position</c>, <c>PageId</c>, <c>TabIndex</c> and <c>FieldId</c> order.
    /// <see cref="EpiInfo.Domain.Records.RecordQueryRules.GetGridColumns(IReadOnlyList{DataFieldMetadata})"/>
    /// re-sorts them by page <c>Position</c>, then <c>TabIndex</c>, to choose the grid columns, so the grid order
    /// can differ from this one only for fields on pages that share a <c>Position</c>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="form"/> is null.</exception>
    public static DataFieldSet GetDataFields(FormMetadata form)
    {
        ArgumentNullException.ThrowIfNull(form);

        var dataFields = new List<DataFieldMetadata>();
        foreach (var page in OrderedPages(form))
        {
            foreach (var field in FieldsOnPage(form, page))
            {
                if (field.Column is not { } column)
                {
                    continue;
                }

                var (fieldType, _, _) = FieldTypeMapper.Map(field);
                if (FieldTypeMapper.IsDataFieldType(fieldType))
                {
                    dataFields.Add(new DataFieldMetadata(field, fieldType, page, column));
                }
            }
        }

        return new DataFieldSet(dataFields);
    }

    /// <summary>
    /// Resolves the members of a group from its <c>metaFields.List</c>, by the one rule the form definition's
    /// <c>members</c> also follows. The list is split on commas, each entry is trimmed and empty entries are
    /// dropped. Each entry resolves among the fields the definition lists (page-bound rows whose page belongs to the
    /// form) to the first in metadata order spelled exactly so. Otherwise an entry equal ignoring case to the names of
    /// two or more data fields with distinct spellings is ambiguous, as <see cref="DataFieldSet.IsAmbiguous"/> decides,
    /// and is left out; any other entry resolves to the first listed field in metadata order equal to it ignoring
    /// case. An entry that resolves to no listed field, to a row an earlier entry already resolved to, or to the group
    /// itself is left out.
    /// </summary>
    /// <param name="form">The form's metadata.</param>
    /// <param name="group">A <c>metaFields</c> row of <paramref name="form"/>.</param>
    /// <returns>
    /// The member rows in <c>List</c> order (the client sorts them by tab index); empty when
    /// <paramref name="group"/> does not map to <see cref="FieldType.Group"/> or its <c>List</c> is null or empty.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="form"/> or <paramref name="group"/> is null.</exception>
    public static IReadOnlyList<FieldMetadata> GetGroupMembers(FormMetadata form, FieldMetadata group)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(group);

        if (FieldTypeMapper.Map(group).FieldType != FieldType.Group || string.IsNullOrEmpty(group.List))
        {
            return ReadOnlyCollection<FieldMetadata>.Empty;
        }

        var members = ResolveMembers(group, ListedFieldsByName(form));
        return members.Length == 0 ? ReadOnlyCollection<FieldMetadata>.Empty : Array.AsReadOnly(members);
    }

    private static IEnumerable<PageMetadata> OrderedPages(FormMetadata form) =>
        form.Pages.OrderBy(page => page.Position).ThenBy(page => page.PageId);

    // TabIndex is compared as the exact stored decimal, so adjacent values above 2^53 keep their order.
    private static IEnumerable<FieldMetadata> FieldsOnPage(FormMetadata form, PageMetadata page) =>
        form.Fields
            .Where(field => field.PageId == page.PageId)
            .OrderBy(field => field.TabIndex ?? 0m)
            .ThenBy(field => field.FieldId);

    // The fields the definition lists, by exact name and by name ignoring case, the first in metadata order winning in
    // each, with the form's data fields to tell which names are ambiguous. Group members resolve against these only:
    // a name that no page shows, such as a system row, gives the client nothing to place in the group, so it is
    // dropped like an unknown name.
    private static ListedFields ListedFieldsByName(FormMetadata form)
    {
        var byExactName = new Dictionary<string, FieldMetadata>(StringComparer.Ordinal);
        var byName = new Dictionary<string, FieldMetadata>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in form.Fields)
        {
            if (field.PageId is { } pageId && form.FindPage(pageId) is not null)
            {
                byExactName.TryAdd(field.Name, field);
                byName.TryAdd(field.Name, field);
            }
        }

        return new ListedFields(byExactName, byName, GetDataFields(form));
    }

    private static FieldDefinition Definition(
        FieldMetadata field,
        PageMetadata page,
        IReadOnlyDictionary<string, IReadOnlyList<FieldOption>> legalValueOptions,
        ListedFields listedFieldsByName)
    {
        var (fieldType, unsupported, unsupportedType) = FieldTypeMapper.Map(field);
        return new FieldDefinition(
            field.Name,
            fieldType,
            field.Prompt,
            field.TabIndex ?? 0m,
            page.PageId,
            unsupported,
            unsupportedType,
            Validation(field, fieldType),
            Options(field, fieldType, legalValueOptions),
            fieldType == FieldType.Group ? Members(field, listedFieldsByName) : null);
    }

    // Labels, groups and unsupported fields hold no value, so they carry no validation, as the field DTO states.
    private static FieldValidation Validation(FieldMetadata field, FieldType fieldType)
    {
        if (!FieldTypeMapper.IsDataFieldType(fieldType))
        {
            return FieldValidation.None;
        }

        return new FieldValidation(
            field.IsRequired,
            field.IsReadOnly,
            field.MaxLength is > 0 ? field.MaxLength : null,
            Bound(field.Lower, fieldType),
            Bound(field.Upper, fieldType),
            string.IsNullOrWhiteSpace(field.Pattern) ? null : field.Pattern);
    }

    // Number bounds are invariant numbers, parsed exactly: a whole number within the signed 64-bit range keeps its
    // exact integer, so a bigint bound such as 9007199254740993 is not rounded through double, and any other number
    // keeps its nearest double without being rounded into a whole one. Date bounds are stored MM-DD-YYYY and leave
    // here as dates, written YYYY-MM-DD on the wire. An unset or unparseable bound, or a bound on any other type, is
    // no bound.
    private static FieldValue? Bound(string? text, FieldType fieldType)
    {
        if (FieldValueCoercion.IsUnsetBound(text))
        {
            return null;
        }

        if (fieldType == FieldType.Number && FieldValueCoercion.TryParseNumber(text, out var number))
        {
            return number;
        }

        if (fieldType == FieldType.Date && FieldValueCoercion.TryParseMetadataDate(text, out var date))
        {
            return FieldValue.FromDate(date);
        }

        return null;
    }

    private static IReadOnlyList<FieldOption>? Options(
        FieldMetadata field,
        FieldType fieldType,
        IReadOnlyDictionary<string, IReadOnlyList<FieldOption>> legalValueOptions)
    {
        return fieldType switch
        {
            FieldType.LegalValues => LegalValueOptions(field, legalValueOptions),
            FieldType.Option => FieldTypeMapper.ParseOptionItems(field.List),
            _ => null,
        };
    }

    // The store's order is kept exactly as given: it is the code table's row order, already sorted by the store when
    // Sort = 1. The store owns that one sort, so the factory never reorders the list; it only takes a read-only copy.
    private static IReadOnlyList<FieldOption> LegalValueOptions(
        FieldMetadata field,
        IReadOnlyDictionary<string, IReadOnlyList<FieldOption>> legalValueOptions)
    {
        if (FindLegalValueOptions(field.Name, legalValueOptions) is not { Count: > 0 } options)
        {
            return ReadOnlyCollection<FieldOption>.Empty;
        }

        return Array.AsReadOnly(options.ToArray());
    }

    // A field's options are the entry keyed by its own canonical name, looked up with the map's own comparer. No other
    // spelling is tried: a key that differs only by letter case belongs to another field, whose code table this field
    // must never be validated against.
    private static IReadOnlyList<FieldOption>? FindLegalValueOptions(
        string name,
        IReadOnlyDictionary<string, IReadOnlyList<FieldOption>> legalValueOptions) =>
        legalValueOptions.TryGetValue(name, out var options) ? options : null;

    // The canonical names of the rows ResolveMembers gives, the one membership rule GetGroupMembers also returns.
    private static IReadOnlyList<string> Members(FieldMetadata group, ListedFields listedFieldsByName)
    {
        var members = ResolveMembers(group, listedFieldsByName);
        return members.Length == 0
            ? ReadOnlyCollection<string>.Empty
            : Array.AsReadOnly(Array.ConvertAll(members, member => member.Name));
    }

    // Legacy splits the group's List on commas and drops empty entries. Each entry is trimmed and resolved to a listed
    // field, the exact spelling first, keeping List order (the client sorts members by tab index); unknown and
    // ambiguous names, repeats of a row already resolved and the group itself are left out.
    private static FieldMetadata[] ResolveMembers(FieldMetadata group, ListedFields listedFieldsByName)
    {
        if (string.IsNullOrEmpty(group.List))
        {
            return [];
        }

        var members = new List<FieldMetadata>();
        var seen = new HashSet<FieldMetadata>(ReferenceEqualityComparer.Instance);
        foreach (var entry in group.List.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (listedFieldsByName.TryResolve(entry, out var member)
                && member.FieldId != group.FieldId
                && seen.Add(member))
            {
                members.Add(member);
            }
        }

        return [.. members];
    }

    // The listed fields indexed by exact name and by name ignoring case. A name resolves to the field spelled exactly
    // so. Otherwise a name the data fields leave ambiguous, equal ignoring case to two or more of their distinct
    // spellings, resolves to nothing, as it does in DataFieldSet; any other name resolves to the first listed field in
    // metadata order equal to it ignoring case. The exact spelling wins, so a label "age" earlier in the metadata
    // never shadows the field named exactly "Age".
    private sealed class ListedFields(
        IReadOnlyDictionary<string, FieldMetadata> byExactName,
        IReadOnlyDictionary<string, FieldMetadata> byName,
        DataFieldSet dataFields)
    {
        public bool TryResolve(string name, [NotNullWhen(true)] out FieldMetadata? field)
        {
            if (byExactName.TryGetValue(name, out field))
            {
                return true;
            }

            if (dataFields.IsAmbiguous(name))
            {
                field = null;
                return false;
            }

            return byName.TryGetValue(name, out field);
        }
    }
}
