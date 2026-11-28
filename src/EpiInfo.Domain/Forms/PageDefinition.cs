namespace EpiInfo.Domain.Forms;

/// <summary>
/// One page of the form definition returned by <c>GET /api/forms/{viewId}</c>: a <c>metaPages</c> row with the fields
/// placed on it.
/// </summary>
/// <remarks>
/// <para>
/// The record is a plain carrier with no logic of its own. The form-definition factory assembles it from the metadata
/// read on each request and owns every ordering and invariant stated on its properties; nothing about any particular
/// form is fixed here.
/// </para>
/// <para>
/// Every public property is a wire member and an OpenAPI schema property, written in camelCase in declaration order:
/// <c>{pageId, name, position, fields}</c>. The record therefore declares no serialisation attributes and no public
/// members beyond its four positional properties. The page's data table name is not one of them: it is resolved in the
/// data layer and never reaches the client.
/// </para>
/// </remarks>
/// <param name="PageId">
/// The <c>metaPages.PageId</c>, which each of the page's fields repeats as its own <c>pageId</c>. It is also the suffix
/// of the page's data table name, the form's base table name followed by this id as legacy Epi Info names it (seeded
/// <c>CaseReport1</c>, <c>FoodHistory2</c>, <c>FoodHistory3</c>); that name is resolved in <c>EpiInfo.Data</c>, not
/// here.
/// </param>
/// <param name="Name">
/// The <c>metaPages.Name</c>, for example <c>Page 1</c>. The client shows it as the page's tab label, and it fills the
/// page-name placeholder <c>{1}</c> of the <c>FIELD_IS_REQUIRED_LONG</c> required-field message.
/// </param>
/// <param name="Position">
/// The <c>metaPages.Position</c>, a SQL <c>smallint</c>, 0-based in the seeded forms. The form definition lists its
/// pages ordered by it, and the client renders one tab per page in that order.
/// </param>
/// <param name="Fields">
/// Every page-bound <c>metaFields</c> row whose <c>PageId</c> is <paramref name="PageId"/>, in <c>TabIndex</c> order
/// with <c>metaFields.FieldId</c> breaking ties, including label, group and unsupported fields; the seeded totals are
/// 61 for the <c>CaseReport</c> page and 55 and 33 for the two <c>FoodHistory</c> pages. Rows with a null
/// <c>PageId</c> (<c>UniqueKey</c>, <c>RecStatus</c>, <c>GlobalRecordId</c>) are system fields and are never listed.
/// Never <see langword="null"/>: a page with no fields carries an empty list.
/// </param>
public sealed record PageDefinition(int PageId, string Name, int Position, IReadOnlyList<FieldDefinition> Fields);
