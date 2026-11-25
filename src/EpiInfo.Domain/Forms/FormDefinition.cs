using EpiInfo.Domain.Diagnostics;

namespace EpiInfo.Domain.Forms;

/// <summary>
/// The form definition returned by <c>GET /api/forms/{viewId}</c>: a <c>metaViews</c> row with its pages, the check-code
/// diagnostics static analysis found in its program, and the write-permission flags.
/// </summary>
/// <remarks>
/// <para>
/// The record is a plain carrier with no logic of its own. The form-definition factory assembles it from the metadata
/// read on each request and owns every ordering and invariant stated on its properties; nothing about any particular
/// form is fixed here. A name-to-field lookup, where a consumer needs one, belongs to that factory or to the consumer.
/// </para>
/// <para>
/// Every public property is a wire member and an OpenAPI schema property, written in camelCase in declaration order:
/// <c>{viewId, name, pages, unsupportedCheckCode, canInsert, canUpdate, canDelete}</c>. The three permission flags are
/// additive optional members beyond the request's <c>{viewId, name, pages, unsupportedCheckCode}</c>, so that shape
/// still holds. A flag with no value is C# <see langword="null"/>, written as JSON <c>null</c> rather than omitted. The
/// record therefore declares no serialisation attributes and no public members beyond its positional properties.
/// </para>
/// <para>
/// The permission flags default to <see langword="null"/>, so a form definition built without a permission check is
/// constructed from its first four members alone. The client leaves the actions of an unchecked flag enabled; a write
/// the database refuses still fails on the server.
/// </para>
/// </remarks>
/// <param name="ViewId">
/// The form's <c>metaViews.ViewId</c>, the <c>{viewId}</c> route value of every form endpoint.
/// </param>
/// <param name="Name">
/// The form's <c>metaViews.Name</c>, for example <c>CaseReport</c> or <c>FoodHistory</c> in the seeded database.
/// </param>
/// <param name="Pages">
/// One entry per <c>metaPages</c> row of the form, ordered by <c>metaPages.Position</c>; the client renders one tab per
/// page in that order. Never <see langword="null"/>.
/// </param>
/// <param name="UnsupportedCheckCode">
/// The diagnostics the check-code engine's static analysis knows from the program text alone: a
/// <see cref="DiagnosticSeverity.Warning"/> for each construct outside the supported subset and a
/// <see cref="DiagnosticSeverity.Info"/> for each skipped <c>GEOCODE</c> statement. Reading a form definition never
/// runs the program, so runtime <see cref="DiagnosticSeverity.Error"/> diagnostics never appear here. Never
/// <see langword="null"/>: a program with nothing to report, and every program while the engine is the facade that
/// analyses nothing, gives an empty list.
/// </param>
/// <param name="CanInsert">
/// Whether the connected login holds INSERT permission on the form's base table and on every page table, which a new
/// record needs. <see langword="null"/> means not checked.
/// </param>
/// <param name="CanUpdate">
/// Whether the connected login holds UPDATE permission on the form's base table and on every page table, which saving
/// an existing record needs. Page-table UPDATE is checked whatever <paramref name="CanInsert"/> is, unlike legacy Epi
/// Info, which checked it only when inserts were allowed. <see langword="null"/> means not checked.
/// </param>
/// <param name="CanDelete">
/// Whether the connected login holds UPDATE permission on the form's base table, which is all a soft delete writes.
/// <see langword="null"/> means not checked.
/// </param>
public sealed record FormDefinition(
    int ViewId,
    string Name,
    IReadOnlyList<PageDefinition> Pages,
    IReadOnlyList<Diagnostic> UnsupportedCheckCode,
    bool? CanInsert = null,
    bool? CanUpdate = null,
    bool? CanDelete = null);
