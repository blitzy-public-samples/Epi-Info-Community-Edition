using System.Collections.ObjectModel;

namespace EpiInfo.Domain.Metadata;

/// <summary>
/// The metadata of one form: its <c>metaViews</c> row with the view's <c>metaPages</c> and
/// <c>metaFields</c> rows, as the metadata reader returns it, uncached, on each request.
/// </summary>
/// <remarks>
/// The record carries rows as read and applies no rule: the form-definition factory, the record
/// validator and the record repository filter and order them. It is immutable, so one instance can
/// be shared across threads: <see cref="Pages"/> and <see cref="Fields"/> are read-only copies of
/// the lists the record is given, taken by the constructor, an object initializer and a
/// <see langword="with"/> expression alike, so a caller that later changes its own list changes
/// neither this record nor any lookup or form definition built from it.
/// </remarks>
/// <param name="ViewId"><c>metaViews.ViewId</c>, the form id used in every API route.</param>
/// <param name="Name"><c>metaViews.Name</c>, for example <c>CaseReport</c>.</param>
/// <param name="BaseTableName">
/// The form's base data table: the <c>metaFields.DataTableName</c> of the view, as legacy Epi Info
/// reads it (seeded <c>CaseReport</c> and <c>FoodHistory</c>). Page tables are named from it.
/// </param>
/// <param name="Pages">
/// The view's <c>metaPages</c> rows in no particular order; consumers order them by
/// <see cref="PageMetadata.Position"/>.
/// </param>
/// <param name="Fields">
/// Every <c>metaFields</c> row of the view, including the system rows <c>UniqueKey</c>,
/// <c>RecStatus</c> and <c>GlobalRecordId</c>, whose <see cref="FieldMetadata.PageId"/> is
/// <see langword="null"/>. Consumers select the rows they need.
/// </param>
/// <exception cref="ArgumentNullException">
/// <paramref name="Pages"/> or <paramref name="Fields"/> is null.
/// </exception>
/// <exception cref="ArgumentException">
/// <paramref name="Pages"/> or <paramref name="Fields"/> holds a null row.
/// </exception>
public sealed record FormMetadata(
    int ViewId,
    string Name,
    string BaseTableName,
    IReadOnlyList<PageMetadata> Pages,
    IReadOnlyList<FieldMetadata> Fields)
{
    private readonly ReadOnlyCollection<PageMetadata> _pages = Snapshot(Pages, nameof(Pages));
    private readonly ReadOnlyCollection<FieldMetadata> _fields = Snapshot(Fields, nameof(Fields));

    /// <summary>
    /// The view's <c>metaPages</c> rows in the order given: a read-only copy of the list the
    /// record was built or initialised with, which no caller holds.
    /// </summary>
    /// <exception cref="ArgumentNullException">The list set on init is null.</exception>
    /// <exception cref="ArgumentException">The list set on init holds a null row.</exception>
    public IReadOnlyList<PageMetadata> Pages
    {
        get => _pages;
        init => _pages = Snapshot(value, nameof(Pages));
    }

    /// <summary>
    /// Every <c>metaFields</c> row of the view in the order given: a read-only copy of the list
    /// the record was built or initialised with, which no caller holds.
    /// </summary>
    /// <exception cref="ArgumentNullException">The list set on init is null.</exception>
    /// <exception cref="ArgumentException">The list set on init holds a null row.</exception>
    public IReadOnlyList<FieldMetadata> Fields
    {
        get => _fields;
        init => _fields = Snapshot(value, nameof(Fields));
    }

    /// <summary>
    /// Finds the page whose <see cref="PageMetadata.PageId"/> is <paramref name="pageId"/>.
    /// </summary>
    /// <param name="pageId">A <c>metaPages.PageId</c>.</param>
    /// <returns>The first page with that id, or <see langword="null"/> when the form has none.</returns>
    public PageMetadata? FindPage(int pageId)
    {
        foreach (PageMetadata page in Pages)
        {
            if (page.PageId == pageId)
            {
                return page;
            }
        }

        return null;
    }

    /// <summary>
    /// Finds the field whose canonical <see cref="FieldMetadata.Name"/> equals <paramref name="name"/>,
    /// ignoring case, so <c>AGE</c> in check code or <c>age</c> in a request resolves to <c>Age</c>.
    /// </summary>
    /// <param name="name">A field name in any letter case.</param>
    /// <returns>
    /// The first matching field, or <see langword="null"/> when none matches or
    /// <paramref name="name"/> is null or empty.
    /// </returns>
    public FieldMetadata? FindField(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        foreach (FieldMetadata field in Fields)
        {
            if (string.Equals(field.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return field;
            }
        }

        return null;
    }

    // Copies the rows into an array only this record references and wraps it read-only, so a
    // later change to the caller's list cannot reach the record, and the exposed list cannot be
    // cast back to a writable one.
    private static ReadOnlyCollection<T> Snapshot<T>(IReadOnlyList<T> rows, string member)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(rows, member);

        T[] copy = [.. rows];
        foreach (T row in copy)
        {
            if (row is null)
            {
                throw new ArgumentException("A form's metadata must not hold a null row.", member);
            }
        }

        return Array.AsReadOnly(copy);
    }
}
