namespace EpiInfo.Domain.Forms;

/// <summary>
/// The kind of a field in a form definition, exposed on the wire as <c>fieldType</c>.
/// The kind decides the JSON type of the field's entry in a record's <c>values</c> and the
/// component the client renders for it.
/// </summary>
/// <remarks>
/// Member names and their order are a wire contract: the API serialises each member as its
/// camelCase name (<c>label</c>, <c>group</c>, <c>text</c>, <c>multiline</c>, <c>number</c>,
/// <c>date</c>, <c>checkbox</c>, <c>option</c>, <c>legalValues</c>, <c>unsupported</c>), and the
/// generated OpenAPI document and client types depend on those spellings.
/// </remarks>
public enum FieldType
{
    /// <summary>
    /// Seeded <c>metaFieldTypes</c> name <c>Label/Title</c>: a heading only, with no value and no
    /// page-table column.
    /// </summary>
    Label,

    /// <summary>
    /// Seeded <c>Group</c>: a heading wrapping the fields named in its <c>members</c>
    /// (<c>metaFields.List</c>); it has no value and no column.
    /// </summary>
    Group,

    /// <summary>
    /// Seeded <c>Text</c>, and the fallback for a metadata type that is neither another supported
    /// seeded type (<c>Label/Title</c>, <c>Group</c>, <c>Multiline</c>, <c>Number</c>,
    /// <c>Date</c>, <c>Checkbox</c>, <c>Option</c>, <c>Legal Values</c>) nor one always mapped
    /// <c>Unsupported</c>, on a field that is not encrypted and whose column is an admitted
    /// character column (<c>nvarchar(n)</c>, <c>nchar(n)</c>, <c>nvarchar(max)</c> or
    /// <c>ntext</c>), such as the seeded <c>Phone Number</c> and <c>Comment Legal</c>. The same
    /// unlisted types on any other column, such as the seeded <c>Yes/No</c> on <c>tinyint</c>, are
    /// <c>Unsupported</c>. The value is a JSON string.
    /// </summary>
    Text,

    /// <summary>
    /// Seeded <c>Multiline</c>: free text in a character column. The value is a JSON string.
    /// </summary>
    Multiline,

    /// <summary>
    /// Seeded <c>Number</c>: a numeric column. The value is a JSON number.
    /// </summary>
    Number,

    /// <summary>
    /// Seeded <c>Date</c>: the value is a JSON string <c>YYYY-MM-DD</c>, stored at 00:00:00 in a
    /// <c>datetime</c> column.
    /// </summary>
    Date,

    /// <summary>
    /// Seeded <c>Checkbox</c>: a <c>bit</c> column. The value is a JSON boolean.
    /// </summary>
    Checkbox,

    /// <summary>
    /// <c>Option</c>: a radio group whose labels come from <c>metaFields.List</c>. The value is a
    /// JSON number, the 0-based index of the chosen item, held in an integer column as legacy
    /// Epi Info stores it (a 16-bit integer in the legacy option field).
    /// </summary>
    Option,

    /// <summary>
    /// Seeded <c>Legal Values</c>: a dropdown over the rows of the field's code table. The value
    /// is a JSON string equal to one of its options.
    /// </summary>
    LegalValues,

    /// <summary>
    /// A field the application does not edit: grid, image, mirror, GUID, GPS or geocode, command
    /// button, relate, <c>Date/Time</c>, <c>Time</c>, any field with <c>IsEncrypted = 1</c>,
    /// <c>Yes/No</c> (a <c>tinyint</c> column), and any field whose column type does not match its
    /// kind. It is rendered as a disabled placeholder naming its metadata type and is never written.
    /// </summary>
    Unsupported,
}
