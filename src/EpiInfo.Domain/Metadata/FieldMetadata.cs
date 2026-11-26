namespace EpiInfo.Domain.Metadata;

/// <summary>
/// One <c>metaFields</c> row of a form, joined to its <c>metaFieldTypes</c> name and to the
/// page-table column it owns, as the metadata reader returns it on each request. A form's metadata
/// holds every row of the view, including the system rows <c>UniqueKey</c>, <c>RecStatus</c> and
/// <c>GlobalRecordId</c>, whose <see cref="PageId"/> is <see langword="null"/>.
/// </summary>
/// <remarks>
/// The record carries stored values as they are, with no interpretation: the field-type mapper,
/// the form-definition factory and the record validator read these raw values and own every rule
/// built on them. Properties are init-only so tests can build synthetic rows (Option fields,
/// encrypted fields, mismatched columns, sorted lists) by naming only what they need.
/// </remarks>
public sealed record FieldMetadata
{
    /// <summary>
    /// <c>metaFields.FieldId</c>. It breaks ties when fields share a <see cref="TabIndex"/>.
    /// </summary>
    public required int FieldId { get; init; }

    /// <summary>
    /// The canonical <c>metaFields.Name</c>, for example <c>LastName</c>. Responses and the
    /// field's <c>values</c> key use this spelling verbatim. The page-table column that stores a
    /// data field's value is <see cref="Column"/>, whose catalogue spelling
    /// (<see cref="ColumnMetadata.Name"/>) can differ from this name in letter case.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// <c>metaFields.FieldTypeId</c>, the key of the field's <c>metaFieldTypes</c> row.
    /// </summary>
    public required int FieldTypeId { get; init; }

    /// <summary>
    /// <c>metaFieldTypes.Name</c> exactly as stored, for example <c>Text</c>, <c>Label/Title</c>,
    /// <c>Phone Number</c>, <c>Comment Legal</c>, <c>Yes/No</c>, <c>Legal Values</c> or
    /// <c>Command Button</c>. The field-type mapper normalises it before matching.
    /// </summary>
    public required string FieldTypeName { get; init; }

    /// <summary>
    /// <c>metaFields.PromptText</c>, the label shown beside the control and placed as <c>{0}</c> in
    /// the required-field message; <see langword="null"/> when the column is NULL.
    /// </summary>
    public string? Prompt { get; init; }

    /// <summary>
    /// <c>metaFields.TabIndex</c>, a SQL <c>decimal(18,0)</c> carried exactly as stored, with no
    /// conversion. It orders the fields of a page, and <see cref="FieldId"/> breaks ties between
    /// equal values; <see langword="null"/> for the system rows.
    /// </summary>
    public decimal? TabIndex { get; init; }

    /// <summary>
    /// <c>metaFields.PageId</c>, the page that shows the field; <see langword="null"/> for the
    /// system rows, which belong to no page.
    /// </summary>
    public int? PageId { get; init; }

    /// <summary>
    /// <c>metaFields.IsRequired</c>; a NULL bit reads as <see langword="false"/>.
    /// </summary>
    public bool IsRequired { get; init; }

    /// <summary>
    /// <c>metaFields.IsReadOnly</c>; a NULL bit reads as <see langword="false"/>. Seeded
    /// <c>OnsetWeek</c> and <c>StateIDNumber</c> are read-only.
    /// </summary>
    public bool IsReadOnly { get; init; }

    /// <summary>
    /// <c>metaFields.IsEncrypted</c>; a NULL bit reads as <see langword="false"/>. No seeded field
    /// sets it.
    /// </summary>
    public bool IsEncrypted { get; init; }

    /// <summary>
    /// <c>metaFields.MaxLength</c> as stored: usually NULL or 0, which both mean no limit (seeded
    /// <c>Zip</c> is 5).
    /// </summary>
    public int? MaxLength { get; init; }

    /// <summary>
    /// <c>metaFields.Lower</c> exactly as stored, which may be an empty string. Number fields hold a
    /// number (<c>0</c>) and date fields an <c>MM-DD-YYYY</c> date (<c>01-15-2011</c>).
    /// </summary>
    public string? Lower { get; init; }

    /// <summary>
    /// <c>metaFields.Upper</c> exactly as stored, which may be an empty string, in the same forms
    /// as <see cref="Lower"/> (<c>120</c>, <c>12-31-2012</c>).
    /// </summary>
    public string? Upper { get; init; }

    /// <summary>
    /// <c>metaFields.Pattern</c> exactly as stored, which may be an empty string, for example
    /// <c>###</c>, <c>###.#</c>, <c>MM-DD-YYYY</c> or <c>###-###-####</c>.
    /// </summary>
    public string? Pattern { get; init; }

    /// <summary>
    /// <c>metaFields.SourceTableName</c>, the code table of a Legal Values field, for example
    /// <c>codephvs_administrativesex_hl7_2x</c>.
    /// </summary>
    public string? SourceTableName { get; init; }

    /// <summary>
    /// <c>metaFields.CodeColumnName</c>, the code column of <see cref="SourceTableName"/>.
    /// </summary>
    public string? CodeColumnName { get; init; }

    /// <summary>
    /// <c>metaFields.TextColumnName</c>, the column of <see cref="SourceTableName"/> whose text is
    /// both the value and the label of each Legal Values option.
    /// </summary>
    public string? TextColumnName { get; init; }

    /// <summary>
    /// <c>metaFields.List</c> as stored: a group's comma-separated member names, or an Option
    /// field's comma-separated items, which end at the first <c>||</c>.
    /// </summary>
    /// <remarks>
    /// The name mirrors the column on purpose. Inside this record it hides the generic list type
    /// that implicit usings import, so this file uses no generic list.
    /// </remarks>
    public string? List { get; init; }

    /// <summary>
    /// <c>metaFields.Sort</c>; a NULL bit reads as <see langword="false"/>. When set, Legal Values
    /// options are sorted by their text. It is 0 on every seeded field.
    /// </summary>
    public bool Sort { get; init; }

    /// <summary>
    /// The column of the page table of <see cref="PageId"/> that the metadata reader attaches to the
    /// field: the column spelled exactly as <see cref="Name"/>, otherwise the first in
    /// <c>column_id</c> order equal to it ignoring case, so its catalogue spelling
    /// (<see cref="ColumnMetadata.Name"/>) can differ from <see cref="Name"/> in letter case;
    /// <see langword="null"/> when that table has no such column, as for labels, groups, command
    /// buttons and the system rows.
    /// </summary>
    public ColumnMetadata? Column { get; init; }
}
