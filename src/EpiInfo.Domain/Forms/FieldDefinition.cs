namespace EpiInfo.Domain.Forms;

/// <summary>
/// One field of a page in the form definition returned by <c>GET /api/forms/{viewId}</c>: a <c>metaFields</c> row
/// with its kind, prompt, position, validation metadata, options and group members.
/// </summary>
/// <remarks>
/// <para>
/// A page lists every <c>metaFields</c> row whose <c>PageId</c> is that page, in <c>TabIndex</c> order. Rows with a
/// null <c>PageId</c> (<c>UniqueKey</c>, <c>RecStatus</c>, <c>GlobalRecordId</c>) are system fields and never appear
/// as a field definition.
/// </para>
/// <para>
/// The record is a plain carrier: the kind is decided only by the field-type mapper, and the record is assembled only
/// by the form-definition factory, which also guarantees the invariants stated on each property. Whether a field is a
/// data field, one with a page-table column and a <c>values</c> entry, is decided by those consumers: it is never
/// <see cref="FieldType.Label"/>, <see cref="FieldType.Group"/> or <see cref="FieldType.Unsupported"/>.
/// </para>
/// <para>
/// Every public property is a wire member and an OpenAPI schema property, written in camelCase in declaration order:
/// <c>{name, fieldType, prompt, tabIndex, pageId, unsupported, unsupportedType, validation, options, members}</c>.
/// A property with no value is C# <see langword="null"/>, written as JSON <c>null</c> rather than omitted. The record
/// therefore declares no serialisation attributes and no public members beyond its positional properties.
/// </para>
/// </remarks>
/// <param name="Name">
/// The canonical <c>metaFields.Name</c>, such as <c>Age</c> or <c>OnsetWeek</c>. It is the key of the field's entry in
/// a record's <c>values</c>, and the name used in a group's <paramref name="Members"/>, in check code and in search
/// criteria.
/// </param>
/// <param name="FieldType">
/// The field's kind, mapped from its <c>metaFieldTypes.Name</c> and page-table column. The seeded <c>Phone Number</c>
/// and <c>Comment Legal</c> types are <see cref="FieldType.Text"/>; the seeded <c>Yes/No</c> type, stored in a
/// <c>tinyint</c> column, is <see cref="FieldType.Unsupported"/>.
/// </param>
/// <param name="Prompt">
/// The <c>metaFields.PromptText</c>, or <see langword="null"/> when it is absent. The client labels the field's native
/// control with it, and it fills the field-name placeholder of the required-field message.
/// </param>
/// <param name="TabIndex">
/// The <c>metaFields.TabIndex</c>, a SQL <c>decimal(18,0)</c>, carried exactly and written as a JSON number such as
/// <c>9</c> or <c>9007199254740993</c>. A page lists its fields in exact tab-index order, then by
/// <c>metaFields.FieldId</c>, and a group's members are rendered sorted by their own tab index. A client that holds
/// JSON numbers as IEEE doubles cannot tell apart values above 2^53, so for values it sees as equal it keeps the order
/// in which the page lists the fields rather than re-sorting them into another order.
/// </param>
/// <param name="PageId">
/// The <c>metaFields.PageId</c> of the page the field sits on, the <c>metaPages.PageId</c> of the enclosing page.
/// </param>
/// <param name="Unsupported">
/// <see langword="true"/> exactly when <paramref name="FieldType"/> is <see cref="FieldType.Unsupported"/>. An
/// unsupported field is rendered as a disabled placeholder, has no <c>values</c> entry and is never written.
/// </param>
/// <param name="UnsupportedType">
/// For an unsupported field, its <c>metaFieldTypes.Name</c>, such as <c>Yes/No</c>, <c>Command Button</c> or
/// <c>Grid</c>, which the client's placeholder names; <see langword="null"/> for every other field.
/// </param>
/// <param name="Validation">
/// The field's validation metadata; never <see langword="null"/>. Label, group and unsupported fields carry
/// <see cref="FieldValidation.None"/>.
/// </param>
/// <param name="Options">
/// The selectable items of an <see cref="FieldType.Option"/> or <see cref="FieldType.LegalValues"/> field, possibly
/// empty when the code table has no rows; <see langword="null"/> for every other kind.
/// </param>
/// <param name="Members">
/// For a <see cref="FieldType.Group"/> field, the field names of its <c>metaFields.List</c>, split on commas with
/// empty entries removed as legacy Epi Info reads the list, in stored order, which is not tab-index order;
/// <see langword="null"/> for every other kind. The client renders the group as a section holding these fields.
/// </param>
public sealed record FieldDefinition(
    string Name,
    FieldType FieldType,
    string? Prompt,
    decimal TabIndex,
    int PageId,
    bool Unsupported,
    string? UnsupportedType,
    FieldValidation Validation,
    IReadOnlyList<FieldOption>? Options,
    IReadOnlyList<string>? Members);
