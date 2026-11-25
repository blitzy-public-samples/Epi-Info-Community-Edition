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
/// The set is immutable after construction, so one instance can be read from any number of threads. Name lookups
/// ignore case and return the canonical entry, so <c>age</c> in a request resolves to <c>Age</c>.
/// </remarks>
public sealed class DataFieldSet : IReadOnlyList<DataFieldMetadata>
{
    private readonly DataFieldMetadata[] _fields;
    private readonly Dictionary<string, DataFieldMetadata> _byName;

    internal DataFieldSet(IEnumerable<DataFieldMetadata> orderedFields)
    {
        _fields = orderedFields.ToArray();
        _byName = new Dictionary<string, DataFieldMetadata>(_fields.Length, StringComparer.OrdinalIgnoreCase);
        foreach (var field in _fields)
        {
            // Two names that differ only by case resolve to the first in storage order.
            _byName.TryAdd(field.Name, field);
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
    /// Finds the data field whose canonical name equals <paramref name="name"/>, ignoring case.
    /// </summary>
    /// <param name="name">A field name in any letter case.</param>
    /// <param name="field">The canonical entry, or <see langword="null"/> when none matches.</param>
    /// <returns>
    /// Whether a data field matches; <see langword="false"/> for a null or empty name, and for labels, groups,
    /// unsupported fields, system fields and names the form does not have.
    /// </returns>
    public bool TryGet(string name, [NotNullWhen(true)] out DataFieldMetadata? field)
    {
        if (string.IsNullOrEmpty(name))
        {
            field = null;
            return false;
        }

        return _byName.TryGetValue(name, out field);
    }

    /// <summary>
    /// Finds the data field whose canonical name equals <paramref name="name"/>, ignoring case.
    /// </summary>
    /// <param name="name">A field name in any letter case.</param>
    /// <returns>The canonical entry, or <see langword="null"/> when none matches.</returns>
    public DataFieldMetadata? Find(string name) => TryGet(name, out var field) ? field : null;

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
    /// The options of each Legal Values field, keyed by field name, in the order the store returned the code-table
    /// rows. A field with no entry gets an empty list.
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

    private static IEnumerable<PageMetadata> OrderedPages(FormMetadata form) =>
        form.Pages.OrderBy(page => page.Position).ThenBy(page => page.PageId);

    // TabIndex is compared as the exact stored decimal, so adjacent values above 2^53 keep their order.
    private static IEnumerable<FieldMetadata> FieldsOnPage(FormMetadata form, PageMetadata page) =>
        form.Fields
            .Where(field => field.PageId == page.PageId)
            .OrderBy(field => field.TabIndex ?? 0m)
            .ThenBy(field => field.FieldId);

    // The fields the definition lists, by name ignoring case, the first in metadata order winning. Group members
    // resolve against this map only: a name that no page shows, such as a system row, gives the client nothing to
    // place in the group, so it is dropped like an unknown name.
    private static Dictionary<string, FieldMetadata> ListedFieldsByName(FormMetadata form)
    {
        var byName = new Dictionary<string, FieldMetadata>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in form.Fields)
        {
            if (field.PageId is { } pageId && form.FindPage(pageId) is not null)
            {
                byName.TryAdd(field.Name, field);
            }
        }

        return byName;
    }

    private static FieldDefinition Definition(
        FieldMetadata field,
        PageMetadata page,
        IReadOnlyDictionary<string, IReadOnlyList<FieldOption>> legalValueOptions,
        IReadOnlyDictionary<string, FieldMetadata> listedFieldsByName)
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

    // The store's row order is kept, as legacy reads the code table unsorted; Sort = 1 sorts by text, as the legacy
    // dropdown's Sorted property does. OrderBy is stable, so equal labels keep the store's order.
    private static IReadOnlyList<FieldOption> LegalValueOptions(
        FieldMetadata field,
        IReadOnlyDictionary<string, IReadOnlyList<FieldOption>> legalValueOptions)
    {
        if (FindLegalValueOptions(field.Name, legalValueOptions) is not { Count: > 0 } options)
        {
            return ReadOnlyCollection<FieldOption>.Empty;
        }

        var snapshot = field.Sort
            ? options.OrderBy(option => option.Label, StringComparer.InvariantCulture).ToArray()
            : options.ToArray();
        return Array.AsReadOnly(snapshot);
    }

    private static IReadOnlyList<FieldOption>? FindLegalValueOptions(
        string name,
        IReadOnlyDictionary<string, IReadOnlyList<FieldOption>> legalValueOptions)
    {
        if (legalValueOptions.TryGetValue(name, out var exact))
        {
            return exact;
        }

        foreach (var (key, options) in legalValueOptions)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                return options;
            }
        }

        return null;
    }

    // Legacy splits the group's List on commas and drops empty entries. Each entry is trimmed and resolved to the
    // canonical name of a listed field, keeping List order (the client sorts members by tab index); unknown names,
    // repeats and the group itself are left out.
    private static IReadOnlyList<string> Members(
        FieldMetadata group,
        IReadOnlyDictionary<string, FieldMetadata> listedFieldsByName)
    {
        if (string.IsNullOrEmpty(group.List))
        {
            return ReadOnlyCollection<string>.Empty;
        }

        var members = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in group.List.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (listedFieldsByName.TryGetValue(entry, out var member)
                && member.FieldId != group.FieldId
                && seen.Add(member.Name))
            {
                members.Add(member.Name);
            }
        }

        return members.Count == 0 ? ReadOnlyCollection<string>.Empty : Array.AsReadOnly(members.ToArray());
    }
}
