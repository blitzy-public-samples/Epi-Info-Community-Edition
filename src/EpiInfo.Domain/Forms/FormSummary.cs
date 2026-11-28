namespace EpiInfo.Domain.Forms;

/// <summary>
/// One entry of the forms list returned by <c>GET /api/forms</c>: a form's identity and how many pages it has.
/// </summary>
/// <remarks>
/// Every value is read from the <c>metaViews</c> and <c>metaPages</c> tables at request time; nothing about any
/// particular form is fixed in code.
/// </remarks>
/// <param name="ViewId">
/// The form's <c>metaViews.ViewId</c>. It is the <c>{viewId}</c> route value, under an <c>int</c> route constraint,
/// of every form endpoint.
/// </param>
/// <param name="Name">
/// The form's <c>metaViews.Name</c>, for example <c>CaseReport</c> or <c>FoodHistory</c> in the seeded database. The
/// client's <c>?view=&lt;form name&gt;</c> URL parameter is matched against it.
/// </param>
/// <param name="PageCount">
/// The number of <c>metaPages</c> rows for the form; the seeded <c>CaseReport</c> has 1 and <c>FoodHistory</c> 2.
/// </param>
public sealed record FormSummary(int ViewId, string Name, int PageCount);
