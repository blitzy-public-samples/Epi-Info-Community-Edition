using EpiInfo.Domain.Values;

namespace EpiInfo.Domain.Forms;

/// <summary>
/// The validation metadata of one field in a form definition, exposed on the wire as the field's
/// <c>validation</c> object: <c>{required, readOnly, maxLength, lower, upper, pattern}</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is a plain carrier of metadata already normalised by the form-definition factory; it checks
/// nothing itself. The server rules that use it belong to the record validator, the only validator of
/// writes, and the client mirrors them from these properties for feedback only.
/// </para>
/// <para>
/// Every public property is a wire member, in declaration order. A property with no value is C#
/// <see langword="null"/>, written as JSON <c>null</c>.
/// </para>
/// </remarks>
/// <param name="Required">
/// <see langword="true"/> when <c>metaFields.IsRequired = 1</c>. A required field left empty fails the save
/// gate, which lists every missing field with the legacy required-field wording. No seeded field is
/// required.
/// </param>
/// <param name="ReadOnly">
/// <see langword="true"/> when <c>metaFields.IsReadOnly = 1</c>, as on the seeded <c>StateIDNumber</c> and
/// <c>OnsetWeek</c>. A read-only field present in the <c>values</c> of a write is a 400, and the client never
/// sends it; a value check code assigns to it is shown but not saved.
/// </param>
/// <param name="MaxLength">
/// <c>metaFields.MaxLength</c> when it is greater than 0 (the seeded <c>Zip</c> has 5), otherwise
/// <see langword="null"/>. This is the metadata limit only: the column capacity, such as the 4000 characters
/// of an <c>nvarchar(4000)</c> column, is the validator's storage-range rule and is not carried here.
/// </param>
/// <param name="Lower">
/// The inclusive lower bound. On a number field it is a number value
/// (<see cref="FieldValue.FromNumber(double)"/>), such as 0 for the seeded <c>Age</c> or 80 for
/// <c>FeverTemp</c>. On a date field it is a date value (<see cref="FieldValue.FromDate(DateOnly)"/>) that the
/// factory converts from the stored <c>MM-DD-YYYY</c> text, so <c>01-15-2011</c> is written as
/// <c>2011-01-15</c>. It is <see langword="null"/> when the stored text is null, empty or whitespace, and on
/// every other field type. An unset bound is always <see langword="null"/>, never
/// <see cref="FieldValue.Null"/>.
/// </param>
/// <param name="Upper">
/// The inclusive upper bound, under the same rules as <paramref name="Lower"/>: for example 120 for the seeded
/// <c>Age</c>, 110 for <c>FeverTemp</c>, and a date such as <c>2012-12-31</c> from the stored
/// <c>12-31-2012</c>. An unset bound is always <see langword="null"/>, never <see cref="FieldValue.Null"/>.
/// </param>
/// <param name="Pattern">
/// The <c>metaFields.Pattern</c> expression as stored, such as the seeded <c>###-###-####</c>, <c>###</c>,
/// <c>###.#</c> or <c>MM-DD-YYYY</c>, or <see langword="null"/> when it is null or empty. The server applies
/// the mask-pattern rule to text and number masks; a date mask is a display and entry format for the client
/// only and is never checked on the ISO value.
/// </param>
public sealed record FieldValidation(
    bool Required,
    bool ReadOnly,
    int? MaxLength,
    FieldValue? Lower,
    FieldValue? Upper,
    string? Pattern)
{
    /// <summary>
    /// No validation: not required, not read-only, and no length, bounds or pattern. Used for fields that
    /// carry no value, such as labels, groups and unsupported fields.
    /// </summary>
    public static FieldValidation None { get; } = new(false, false, null, null, null, null);
}
