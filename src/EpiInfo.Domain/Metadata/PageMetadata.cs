namespace EpiInfo.Domain.Metadata;

/// <summary>
/// One <c>metaPages</c> row of a form, as the metadata reader returns it on each request.
/// </summary>
/// <param name="PageId">
/// <c>metaPages.PageId</c>. Fields name their page through <c>metaFields.PageId</c>, and the page's
/// data table name ends with this id.
/// </param>
/// <param name="Name">
/// <c>metaPages.Name</c>, for example <c>Page 1</c>. It is the page name shown on the page tab and
/// placed as <c>{1}</c> in the required-field message.
/// </param>
/// <param name="Position">
/// <c>metaPages.Position</c>, 0-based in the seeded forms. Pages are ordered by it; the rows
/// themselves may arrive in any order.
/// </param>
/// <param name="TableName">
/// The page's data table: the form's base table name followed by <see cref="PageId"/>, as legacy
/// Epi Info names it (seeded <c>CaseReport1</c>, <c>FoodHistory2</c>, <c>FoodHistory3</c>). The
/// reader checks the name against the database catalogue and supplies the catalogue's spelling when
/// the table exists; this record only carries the name.
/// </param>
public sealed record PageMetadata(int PageId, string Name, int Position, string TableName);
