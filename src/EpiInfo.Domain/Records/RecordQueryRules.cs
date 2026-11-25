using System.Text;
using EpiInfo.Domain.Forms;
using EpiInfo.Domain.Metadata;
using EpiInfo.Domain.Values;

namespace EpiInfo.Domain.Records;

/// <summary>
/// The one owner of the record-set rules: which rows a request selects, how they are ordered and numbered, the
/// paging and search bounds, the grid columns, the grid filter's "contains" pattern, and the shape of every
/// <c>values</c> map. The record service, the SQL Server store, its SQL builders and the in-memory test store all
/// call these members instead of deriving the rules again. Nothing here is SQL: the data layer renders the predicate
/// and the offsets as parameterised SQL, and passes the contains pattern as a parameter value, never as SQL text.
/// </summary>
/// <remarks>
/// <para>
/// <b>Selected set.</b> One predicate serves every query that selects records: <c>count</c>, <c>at</c>, positions,
/// the records list, search and the <c>position</c> endpoint (<see cref="RequiredRecStatus"/>). It selects the
/// target active set, <c>RecStatus = 1</c> exactly, unless <c>includeDeleted=true</c> selects every row, so
/// deleted records are hidden by default. Two legacy predicates differ from it. Legacy navigation applied no status
/// filter (<c>Epi.Core/Data/Services/CollectedDataProvider.cs</c>, lines 1422-1878; <c>RunTimeView.cs</c>, lines
/// 673-694), so <c>includeDeleted=true</c> reproduces the legacy navigation set. Legacy Find appended
/// <c>RecStatus &gt; 0</c> (<c>CollectedDataProvider.cs</c>, line 2249). The target active set and legacy Find
/// differ only for status values other than 0 and 1, which the seeded data does not hold; legacy navigation and
/// legacy Find differ for status 0, which navigation kept and Find left out. Search uses this selected-set
/// predicate in place of legacy Find's, the same one navigation uses, so a search position always equals the
/// navigation position.
/// </para>
/// <para>
/// <b>Ordering and positions.</b> Records are ordered by <c>UniqueKey</c> ascending. The position of the record
/// with key k is the number of selected-set rows whose <c>UniqueKey</c> is at most k, the legacy record-number
/// formula (<c>Epi.Enter.CheckCodeEngine/RunTimeView.cs</c>, lines 673-694). <c>at/{position}</c> reads the row
/// at offset <c>position - 1</c> of that order, fetching one row (<see cref="PositionToOffset"/>). A search
/// result's position is its row number over the selected set by <c>UniqueKey</c>, numbered before the criteria
/// filter, so it equals the navigation position.
/// </para>
/// <para>
/// <b>Intrinsic coordinates.</b> A Record DTO's own <c>position</c> and <c>count</c> do not follow the caller's
/// toggle: they are over the active set for an active record and over every row for a deleted one
/// (<see cref="IntrinsicIncludesDeleted"/>). The navigator's "[n] of N" is taken over the set its deleted toggle
/// selects: N is the count from <c>GET /api/forms/{viewId}/records/count</c> with the toggle's
/// <c>includeDeleted</c>, and n is the requested position after <c>at/{n}</c>; after an open by id, a save of a new
/// or an existing record or a toggle change, it is the <see cref="NavigationPosition.Position"/> from
/// <c>GET /api/forms/{viewId}/records/{globalRecordId}/position</c> with the same <c>includeDeleted</c>. Neither n
/// nor N is derived from search, and the Record DTO's own coordinates are left as they are.
/// </para>
/// <para>
/// Every member is a pure function of its arguments with no shared state, so the class is safe to call from any
/// thread. Exception messages name the parameter only and never carry the value, as the redaction policy requires.
/// </para>
/// </remarks>
public static class RecordQueryRules
{
    /// <summary>The <c>RecStatus</c> of an active record, the only value the active set admits.</summary>
    public const int ActiveRecStatus = 1;

    /// <summary>The <c>RecStatus</c> a soft delete writes.</summary>
    public const int DeletedRecStatus = 0;

    /// <summary>The page the records list returns when <c>page</c> is absent.</summary>
    public const int DefaultPage = 1;

    /// <summary>The smallest admitted <c>page</c>.</summary>
    public const int MinPage = 1;

    /// <summary>The page size the records list uses when <c>pageSize</c> is absent.</summary>
    public const int DefaultPageSize = 50;

    /// <summary>The smallest admitted <c>pageSize</c>.</summary>
    public const int MinPageSize = 1;

    /// <summary>The largest admitted <c>pageSize</c>.</summary>
    public const int MaxPageSize = 200;

    /// <summary>The query parameter name of the page number, the key of its entry in a 400's <c>errors</c>.</summary>
    public const string PageParameter = "page";

    /// <summary>The query parameter name of the page size, the key of its entry in a 400's <c>errors</c>.</summary>
    public const string PageSizeParameter = "pageSize";

    /// <summary>
    /// The fixed message for a <c>page</c> that is not an integer of at least 1. It never echoes the submitted value.
    /// </summary>
    public const string PageOutOfRangeMessage = "page must be an integer greater than or equal to 1.";

    /// <summary>
    /// The fixed message for a <c>pageSize</c> that is not an integer from 1 to 200. It never echoes the submitted
    /// value.
    /// </summary>
    public const string PageSizeOutOfRangeMessage = "pageSize must be an integer from 1 to 200.";

    /// <summary>
    /// The most items a search response holds. <c>total</c> still counts every match, and <c>truncated</c> is
    /// <c>total &gt; SearchCap</c>. Legacy Find had no cap.
    /// </summary>
    public const int SearchCap = 200;

    /// <summary>
    /// The most fields one search may name, the legacy Find dialog's limit
    /// (<c>Epi.Windows.Enter/Dialogs/FindRecordDialog.cs</c>, lines 293-317). The search-criteria parser rejects a
    /// seventh field with a 400.
    /// </summary>
    public const int MaxSearchFields = 6;

    /// <summary>
    /// The longest <c>LIKE</c> pattern, SQL Server's 8,000-byte pattern limit counted in UTF-16 code units of an
    /// <c>nvarchar</c> pattern. It applies to Find's wildcard patterns, after the search-criteria parser has collapsed
    /// each run of <c>%</c> in a pattern without <c>[</c>, and to the grid filter's contains pattern
    /// (<see cref="ToContainsPattern"/>); Find's equality and its 255-character multiline comparison are not limited.
    /// </summary>
    public const int MaxLikePatternLength = 4000;

    /// <summary>
    /// The escape character of the grid filter's contains pattern, which the list statement declares in its
    /// <c>ESCAPE</c> clause.
    /// </summary>
    public const char ContainsPatternEscape = '\\';

    /// <summary>
    /// The fixed message for a <c>q</c> whose contains pattern would exceed <see cref="MaxLikePatternLength"/>. It
    /// never echoes the submitted value.
    /// </summary>
    public const string FilterTooLongMessage =
        "q must be at most 3998 characters long, counting each %, _, [ and \\ as two.";

    /// <summary>The number of grid columns a record summary and a search result carry.</summary>
    public const int GridColumnCount = 6;

    /// <summary>
    /// The <c>RecStatus</c> a row must hold to be in the selected set.
    /// </summary>
    /// <param name="includeDeleted">Whether the caller selected every row.</param>
    /// <returns>
    /// <see langword="null"/> when <paramref name="includeDeleted"/> is <see langword="true"/>, which the data
    /// layer renders as no status filter; otherwise <see cref="ActiveRecStatus"/>, rendered as an equality on
    /// <c>RecStatus</c> with a parameter.
    /// </returns>
    public static int? RequiredRecStatus(bool includeDeleted) => includeDeleted ? null : ActiveRecStatus;

    /// <summary>
    /// Whether a row with <paramref name="recStatus"/> is in the selected set, the in-memory form of the predicate
    /// <see cref="RequiredRecStatus"/> gives the data layer.
    /// </summary>
    /// <param name="recStatus">The row's <c>RecStatus</c>.</param>
    /// <param name="includeDeleted">Whether the caller selected every row.</param>
    /// <returns>
    /// <see langword="true"/> when every row is selected, or when <paramref name="recStatus"/> is exactly
    /// <see cref="ActiveRecStatus"/>.
    /// </returns>
    public static bool IsInSelectedSet(int recStatus, bool includeDeleted) =>
        includeDeleted || recStatus == ActiveRecStatus;

    /// <summary>
    /// The set a record's own <c>position</c> and <c>count</c> are taken over: the active set for an active
    /// record, every row otherwise.
    /// </summary>
    /// <param name="recStatus">The record's <c>RecStatus</c>.</param>
    /// <returns>
    /// The <c>includeDeleted</c> value to compute the record's intrinsic coordinates with: <see langword="false"/>
    /// for <see cref="ActiveRecStatus"/>, <see langword="true"/> for any other status.
    /// </returns>
    public static bool IntrinsicIncludesDeleted(int recStatus) => recStatus != ActiveRecStatus;

    /// <summary>
    /// Whether <paramref name="position"/> can name a record. Only the lower bound is checked here: a position
    /// above the selected set's <c>count</c>, every position on an empty set included, is a 404 the store and the
    /// record service decide.
    /// </summary>
    /// <param name="position">A 1-based position.</param>
    /// <returns>Whether <paramref name="position"/> is at least 1.</returns>
    public static bool IsValidPosition(int position) => position >= 1;

    /// <summary>
    /// The 0-based row offset of a 1-based position in <c>UniqueKey</c> order, computed in 64 bits for the data
    /// layer's <c>bigint</c> offset parameter.
    /// </summary>
    /// <param name="position">A 1-based position of at least 1.</param>
    /// <returns><c>position - 1</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="position"/> is less than 1.</exception>
    public static long PositionToOffset(int position)
    {
        if (!IsValidPosition(position))
        {
            throw new ArgumentOutOfRangeException(nameof(position), "A position must be at least 1.");
        }

        return (long)position - 1L;
    }

    /// <summary>Whether <paramref name="page"/> is an admitted page number.</summary>
    /// <param name="page">A 1-based page number.</param>
    /// <returns>Whether <paramref name="page"/> is at least <see cref="MinPage"/>.</returns>
    public static bool IsValidPage(int page) => page >= MinPage;

    /// <summary>Whether <paramref name="pageSize"/> is an admitted page size.</summary>
    /// <param name="pageSize">A page size.</param>
    /// <returns>
    /// Whether <paramref name="pageSize"/> lies from <see cref="MinPageSize"/> to <see cref="MaxPageSize"/>,
    /// inclusive.
    /// </returns>
    public static bool IsValidPageSize(int pageSize) => pageSize is >= MinPageSize and <= MaxPageSize;

    /// <summary>
    /// Checks the paging bounds of the records list.
    /// </summary>
    /// <param name="page">The parsed page number.</param>
    /// <param name="pageSize">The parsed page size.</param>
    /// <returns>
    /// An empty list when both are admitted; otherwise one error per out-of-range parameter, <c>page</c> before
    /// <c>pageSize</c>, each keyed by <see cref="PageParameter"/> or <see cref="PageSizeParameter"/> and carrying
    /// <see cref="PageOutOfRangeMessage"/> or <see cref="PageSizeOutOfRangeMessage"/>.
    /// </returns>
    public static IReadOnlyList<PagingError> ValidatePaging(int page, int pageSize)
    {
        var pageValid = IsValidPage(page);
        var pageSizeValid = IsValidPageSize(pageSize);
        if (pageValid && pageSizeValid)
        {
            return Array.Empty<PagingError>();
        }

        var errors = new List<PagingError>(2);
        if (!pageValid)
        {
            errors.Add(new PagingError(PageParameter, PageOutOfRangeMessage));
        }

        if (!pageSizeValid)
        {
            errors.Add(new PagingError(PageSizeParameter, PageSizeOutOfRangeMessage));
        }

        return errors.AsReadOnly();
    }

    /// <summary>
    /// The 0-based row offset of the first record on a page, <c>(page - 1) * pageSize</c>, computed entirely in
    /// 64 bits. The largest admitted page, 2,147,483,647, with a page size of 200 gives 429,496,729,200, which
    /// cannot wrap; the data layer passes it as a <c>bigint</c> offset, so a valid page past the end returns no
    /// items and the correct total.
    /// </summary>
    /// <param name="page">An admitted page number.</param>
    /// <param name="pageSize">An admitted page size.</param>
    /// <returns>The offset of the page's first row.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="page"/> is less than <see cref="MinPage"/>, or <paramref name="pageSize"/> is outside
    /// <see cref="MinPageSize"/> to <see cref="MaxPageSize"/>.
    /// </exception>
    public static long ComputeOffset(int page, int pageSize)
    {
        if (!IsValidPage(page))
        {
            throw new ArgumentOutOfRangeException(nameof(page), PageOutOfRangeMessage);
        }

        if (!IsValidPageSize(pageSize))
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), PageSizeOutOfRangeMessage);
        }

        return ((long)page - 1L) * pageSize;
    }

    /// <summary>Whether a search with <paramref name="total"/> matches returns fewer items than it found.</summary>
    /// <param name="total">The number of matches in the selected set.</param>
    /// <returns>Whether <paramref name="total"/> exceeds <see cref="SearchCap"/>.</returns>
    public static bool IsTruncated(int total) => total > SearchCap;

    /// <summary>
    /// Turns the grid filter into the pattern of a case-insensitive "contains": each <c>\</c>, <c>%</c>, <c>_</c> and
    /// <c>[</c> gets a leading <see cref="ContainsPatternEscape"/>, the escape character the list statement declares,
    /// and the result is wrapped in <c>%</c>. The backslash is escaped too, so a backslash in the filter matches
    /// itself.
    /// </summary>
    /// <param name="filter">The filter text, unchanged and untrimmed.</param>
    /// <returns>For example <c>%a\\b\%c\_d\[e%</c> for <c>a\b%c_d[e</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="filter"/> is null.</exception>
    public static string ToContainsPattern(string filter)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var pattern = new StringBuilder(filter.Length + 2);
        pattern.Append('%');
        foreach (var character in filter)
        {
            if (IsContainsPatternCharacter(character))
            {
                pattern.Append(ContainsPatternEscape);
            }

            pattern.Append(character);
        }

        return pattern.Append('%').ToString();
    }

    /// <summary>
    /// Whether the contains pattern of <paramref name="filter"/>, as <see cref="ToContainsPattern"/> builds it, fits
    /// <see cref="MaxLikePatternLength"/>: the filter's length, plus one per <c>\</c>, <c>%</c>, <c>_</c> and
    /// <c>[</c>, plus the two wrapping <c>%</c>. It counts without building the pattern.
    /// </summary>
    /// <param name="filter">The filter text, unchanged and untrimmed.</param>
    /// <returns>Whether the pattern is at most <see cref="MaxLikePatternLength"/> characters long.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="filter"/> is null.</exception>
    public static bool IsRepresentableFilter(string filter)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var length = filter.Length + 2;
        if (length > MaxLikePatternLength)
        {
            return false;
        }

        foreach (var character in filter)
        {
            if (IsContainsPatternCharacter(character) && ++length > MaxLikePatternLength)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether a data field of <paramref name="type"/> can be a grid column.
    /// </summary>
    /// <param name="type">A mapped field type.</param>
    /// <returns>
    /// <see langword="true"/> only for <see cref="FieldType.Text"/>, <see cref="FieldType.Number"/>,
    /// <see cref="FieldType.Date"/> and <see cref="FieldType.LegalValues"/>.
    /// </returns>
    public static bool IsGridFieldType(FieldType type) =>
        type is FieldType.Text or FieldType.Number or FieldType.Date or FieldType.LegalValues;

    /// <summary>
    /// Selects the grid columns of a form: the first <see cref="GridColumnCount"/> data fields whose type passes
    /// <see cref="IsGridFieldType"/>, in page <c>Position</c> order, then <c>TabIndex</c> order. Seeded
    /// <c>CaseReport</c> gives <c>PhoneNumber, LastName, FirstName, Address, City, State</c>, and seeded
    /// <c>FoodHistory</c> gives <c>CaseID, DateofInterview, FirstName, LastName, Sex, DOB</c>.
    /// </summary>
    /// <param name="dataFields">
    /// The form's data fields, from <see cref="FormDefinitionFactory.GetDataFields"/>, which has already left out
    /// labels, groups and unsupported fields.
    /// </param>
    /// <returns>At most <see cref="GridColumnCount"/> columns, in grid order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="dataFields"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="dataFields"/> holds a null entry.</exception>
    public static IReadOnlyList<DataFieldMetadata> GetGridColumns(IReadOnlyList<DataFieldMetadata> dataFields)
    {
        ArgumentNullException.ThrowIfNull(dataFields);

        if (dataFields.Any(field => field is null))
        {
            throw new ArgumentException("A data field must not be null.", nameof(dataFields));
        }

        // Page Position, then TabIndex, order the grid. Position is not unique, so the fields of pages that share
        // one interleave by TabIndex; PageId, then FieldId, only break ties between fields equal on both keys. A
        // missing TabIndex reads as 0, as the tabIndex the form definition reports does, so the grid never places
        // such a field last while the form shows it first. OrderBy is stable, so rows equal on every key keep their
        // input order.
        return dataFields
            .Where(field => IsGridFieldType(field.FieldType))
            .OrderBy(field => field.Page.Position)
            .ThenBy(field => field.Field.TabIndex ?? 0m)
            .ThenBy(field => field.Page.PageId)
            .ThenBy(field => field.Field.FieldId)
            .Take(GridColumnCount)
            .ToArray();
    }

    /// <summary>
    /// Selects the grid columns of <paramref name="form"/> from its data fields, as
    /// <see cref="GetGridColumns(IReadOnlyList{DataFieldMetadata})"/> does.
    /// </summary>
    /// <param name="form">The form's metadata.</param>
    /// <returns>At most <see cref="GridColumnCount"/> columns, in grid order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="form"/> is null.</exception>
    public static IReadOnlyList<DataFieldMetadata> GetGridColumns(FormMetadata form)
    {
        ArgumentNullException.ThrowIfNull(form);
        return GetGridColumns(FormDefinitionFactory.GetDataFields(form));
    }

    /// <summary>
    /// Creates an empty <c>values</c> map. Every producer of a record's or a DTO's values uses it, so each map
    /// keeps its entries in insertion order, which producers make data-field order (page <c>Position</c>, then
    /// <c>TabIndex</c>), and finds a key whatever its letter case. Keys are the canonical <c>metaFields.Name</c>.
    /// </summary>
    /// <param name="capacity">The number of entries to reserve room for; 0 or more.</param>
    /// <returns>An empty, insertion-ordered map whose keys compare ordinally, ignoring case.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is negative.</exception>
    public static OrderedDictionary<string, FieldValue> CreateValuesMap(int capacity = 0) =>
        new(capacity, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Projects a record's values onto <paramref name="columns"/>, for the grid-only values of a record summary
    /// or a search result.
    /// </summary>
    /// <param name="values">The record's values, keyed by field name.</param>
    /// <param name="columns">The columns to keep, usually from <see cref="GetGridColumns(FormMetadata)"/>.</param>
    /// <returns>
    /// A new map from <see cref="CreateValuesMap"/> with one entry per column, in column order, keyed by the
    /// column's canonical name; a column <paramref name="values"/> does not hold maps to
    /// <see cref="FieldValue.Null"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="values"/> or <paramref name="columns"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="columns"/> holds a null entry.</exception>
    public static IReadOnlyDictionary<string, FieldValue> ProjectValues(
        IReadOnlyDictionary<string, FieldValue> values,
        IReadOnlyList<DataFieldMetadata> columns)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(columns);

        var projected = CreateValuesMap(columns.Count);
        foreach (var column in columns)
        {
            if (column is null)
            {
                throw new ArgumentException("A column must not be null.", nameof(columns));
            }

            // Keys ignore case, so two columns whose names differ only by case share one entry; the first in column
            // order keeps it, as DataFieldSet resolves such names to the first in storage order.
            projected.TryAdd(column.Name, values.TryGetValue(column.Name, out var value) ? value : FieldValue.Null);
        }

        return projected;
    }

    // The characters a contains pattern escapes: the escape character itself and LIKE's %, _ and [.
    private static bool IsContainsPatternCharacter(char character) =>
        character is ContainsPatternEscape or '%' or '_' or '[';
}

/// <summary>
/// One out-of-range paging parameter of the records list, as <see cref="RecordQueryRules.ValidatePaging"/> reports
/// it.
/// </summary>
/// <param name="Parameter">
/// The query parameter's name, <see cref="RecordQueryRules.PageParameter"/> or
/// <see cref="RecordQueryRules.PageSizeParameter"/>: the key of its entry in the 400's <c>errors</c>.
/// </param>
/// <param name="Message">A fixed message that never echoes the submitted value.</param>
public readonly record struct PagingError(string Parameter, string Message);
