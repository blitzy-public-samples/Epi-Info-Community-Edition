using System.Text;
using EpiInfo.Data.Metadata;
using EpiInfo.Data.Sql;
using EpiInfo.Domain.Metadata;
using EpiInfo.Domain.Records;
using EpiInfo.Domain.Search;

namespace EpiInfo.Data.Records;

/// <summary>
/// A Find statement with the layout of its result: ordinals <see cref="SearchSqlBuilder.IdOrdinal"/> to
/// <see cref="SearchSqlBuilder.TotalOrdinal"/> (0 to 4) are the base row's <c>GlobalRecordId</c>, <c>UniqueKey</c>
/// and <c>RecStatus</c>, the record's navigation position (<c>bigint</c>) and the total number of matches
/// (<c>bigint</c>, the same on every row); then one <see cref="ProjectedColumn"/> per grid column, from ordinal 5.
/// </summary>
/// <param name="Statement">The statement to run; it returns no row when nothing matches.</param>
/// <param name="Columns">The grid columns in the order they were passed to the builder.</param>
internal sealed record SearchSelect(SqlStatement Statement, IReadOnlyList<ProjectedColumn> Columns);

/// <summary>
/// One page of the records list with the layout of its result: ordinals <see cref="SearchSqlBuilder.IdOrdinal"/>,
/// <see cref="SearchSqlBuilder.UniqueKeyOrdinal"/> and <see cref="SearchSqlBuilder.RecStatusOrdinal"/> (0, 1, 2) are
/// the base row's <c>GlobalRecordId</c>, <c>UniqueKey</c> and <c>RecStatus</c>; then one
/// <see cref="ProjectedColumn"/> per grid column, from ordinal 3.
/// </summary>
/// <param name="Statement">The statement to run; it returns no row for a page past the end.</param>
/// <param name="Columns">The grid columns in the order they were passed to the builder.</param>
internal sealed record ListSelect(SqlStatement Statement, IReadOnlyList<ProjectedColumn> Columns);

/// <summary>
/// Renders Find (<c>POST .../records/search</c>) and the records list (<c>GET .../records</c>) as parameterised SQL
/// over a form's base and page tables. It is a pure text-and-parameter builder: no connection, no I/O, no logging
/// and no cache.
/// </summary>
/// <remarks>
/// <para>
/// <b>Input.</b> Search conditions arrive already parsed by <c>SearchCriteriaParser</c>, which owns every criterion
/// rule: trimming, the <c>" or "</c> split, the comparison prefixes, per-type coercion, the six-field limit and the
/// <c>*</c> to <c>%</c> rewrite. This class renders each alternative exactly as given and never reads a value.
/// </para>
/// <para>
/// <b>Identifiers and values.</b> Every table and column name comes from the reader-built <see cref="FormSchema"/>
/// and reaches the text only through <see cref="SqlIdentifier"/>, by way of the <see cref="RecordSqlBuilder"/>
/// helpers. The CTE name <c>[n]</c>, its columns <c>[k]</c> and <c>[pos]</c>, the aliases <c>b</c>, <c>n</c>,
/// <c>p{i}</c> and <c>[c{n}]</c>, the collation name and the escape character are fixed literals. Every value
/// (criteria, the grid filter, the excluded id, the cap, the offset and the page size) is a positional parameter,
/// so text that looks like SQL stays inert data.
/// </para>
/// <para>
/// Every exception message is fixed text naming no table, column, field, value or id, as the redaction policy
/// requires. The class holds no state and is safe to call from any thread.
/// </para>
/// </remarks>
internal static class SearchSqlBuilder
{
    /// <summary>The ordinal of the base row's <c>GlobalRecordId</c> in a <see cref="SearchSelect"/> or <see cref="ListSelect"/>.</summary>
    internal const int IdOrdinal = 0;

    /// <summary>The ordinal of the base row's <c>UniqueKey</c> in a <see cref="SearchSelect"/> or <see cref="ListSelect"/>.</summary>
    internal const int UniqueKeyOrdinal = 1;

    /// <summary>The ordinal of the base row's <c>RecStatus</c> in a <see cref="SearchSelect"/> or <see cref="ListSelect"/>.</summary>
    internal const int RecStatusOrdinal = 2;

    /// <summary>
    /// The ordinal of a search result's navigation position in a <see cref="SearchSelect"/>: its <c>ROW_NUMBER()</c>
    /// over the selected set by <c>UniqueKey</c>, a <c>bigint</c>.
    /// </summary>
    internal const int PositionOrdinal = 3;

    /// <summary>
    /// The ordinal of the total number of matches in a <see cref="SearchSelect"/>, a <c>bigint</c> counted before the
    /// cap, so every row carries the same total.
    /// </summary>
    internal const int TotalOrdinal = 4;

    // The first grid-column ordinal of each result layout.
    private const int SearchFirstColumnOrdinal = 5;
    private const int ListFirstColumnOrdinal = 3;

    // SUBSTRING bounds of the multiline comparison, legacy's 255-character prefix.
    private const string PrefixLength = "255";

    // The collation of the grid filter: case-insensitive whatever the database collation is.
    private const string ContainsCollation = "Latin1_General_100_CI_AS";

    // CONVERT style 23 renders a datetime as yyyy-mm-dd, the wire form of a date field.
    private const string IsoDateStyle = "23";

    // The escape character of the grid filter pattern, written into the statement as a fixed literal.
    private const char ContainsEscape = '\\';

    // The sys.types name of the one admitted character type that rejects comparison operators.
    private const string NTextTypeName = "ntext";

    // Fixed messages: the redaction policy keeps identifiers, field names, values and ids out of exception text.
    private const string NullConditionMessage = "The search conditions must not hold a null entry.";
    private const string EmptyAlternativesMessage = "A search condition needs at least one alternative.";
    private const string UnknownFieldMessage = "A search condition names a field that is not a data field of the form.";
    private const string FieldTypeMismatchMessage = "A search condition's field type is not the type of its field.";
    private const string UnknownTargetMessage = "The search target is not a defined member.";
    private const string UnknownOperatorMessage = "The search operator is not a defined member.";
    private const string MissingValueMessage = "A search alternative other than IS NULL needs a value.";
    private const string NullGridColumnMessage = "The grid columns must not hold a null entry.";

    /// <summary>
    /// Builds the Find statement: the selected set numbered by <c>UniqueKey</c>, joined to every page table, filtered
    /// by the conditions and the excluded id, ordered by <c>UniqueKey</c> and capped at
    /// <see cref="RecordQueryRules.SearchCap"/> rows.
    /// </summary>
    /// <remarks>
    /// <para>The statement has this shape:</para>
    /// <code>
    /// WITH [n] AS (SELECT b.[UniqueKey] AS [k], ROW_NUMBER() OVER (ORDER BY b.[UniqueKey]) AS [pos]
    ///              FROM [base] b [WHERE b.[RecStatus] = @p0])
    /// SELECT TOP (@p1) b.[GlobalRecordId] AS [c0], b.[UniqueKey] AS [c1], b.[RecStatus] AS [c2],
    ///        n.[pos] AS [c3], COUNT_BIG(*) OVER () AS [c4], p0.[col] AS [c5], ...
    /// FROM [base] b INNER JOIN [n] n ON n.[k] = b.[UniqueKey]
    ///      INNER JOIN [page] p0 ON p0.[GlobalRecordId] = b.[GlobalRecordId] ...
    /// WHERE (alt OR alt ...) AND (alt OR ...) [AND b.[GlobalRecordId] &lt;&gt; @pX]
    /// ORDER BY b.[UniqueKey]
    /// </code>
    /// <para>
    /// <b>Positions.</b> The CTE reads the base table only and applies only the selected-set predicate, so
    /// <c>[pos]</c> is numbered before any criterion and equals the navigation position, the count of selected rows
    /// with <c>UniqueKey &lt;= k</c>.
    /// </para>
    /// <para>
    /// <b>Result set.</b> The base table is joined with <c>INNER JOIN</c> to every page that has a table, so a record
    /// missing a row on any page is not returned. That reproduces legacy's final, hydrated result, which dropped such
    /// base rows when it joined each page in memory
    /// (<c>Epi.Core/Data/Services/CollectedDataProvider.cs:2289-2298, 2357-2375</c>), though not its two-step query,
    /// which first joined only the pages holding a criterion field (<c>:1940-1967</c>).
    /// </para>
    /// <para>
    /// <b>Open record, total and cap.</b> The excluded id is removed in the <c>WHERE</c> clause, so it is gone before
    /// <c>COUNT_BIG(*) OVER ()</c> counts the matches and before <c>TOP</c> caps them; legacy removed the open record
    /// from the result afterwards (<c>:2256-2266</c>) and had neither a total nor a cap.
    /// </para>
    /// <para>
    /// <b>Collation.</b> No <c>COLLATE</c> clause is written: equality and <c>LIKE</c> compare under the database
    /// collation, exactly as legacy Find does.
    /// </para>
    /// <para>
    /// <b>Alternatives.</b> A comparison renders as <c>column op @p</c>; <see cref="SearchOperator.IsNull"/> as
    /// <c>column IS NULL</c> with no parameter; <see cref="SearchOperator.Like"/> as <c>column LIKE @p</c> with no
    /// <c>ESCAPE</c> clause, so <c>%</c>, <c>_</c> and <c>[...]</c> keep their SQL meaning and quotes stay in the
    /// pattern; and <see cref="SearchOperator.PrefixEqual255"/> as
    /// <c>SUBSTRING(column, 1, 255) = SUBSTRING(@p, 1, 255)</c>. That last parameter is <c>nvarchar(max)</c>, where
    /// legacy bound <c>DbType.AnsiString</c> (<c>:2212-2222</c>), which would have replaced characters outside the
    /// code page; the difference is recorded in the parity notes. Values are bound by
    /// <see cref="ColumnValueConverter.ToSearchParameter"/>, each typed by the resolved column. On an integer column a
    /// number criterion is compared exactly, never through <c>float</c>, which rounds a <c>bigint</c> beyond 2^53: an
    /// exact 64-bit integer is bound as that <c>bigint</c>, and any other number as the exact <c>decimal</c> half-way
    /// inside the integer interval that holds it, or just beyond the <c>bigint</c> range on its side. On a
    /// <c>float</c> column the number is bound as <c>float</c>.
    /// </para>
    /// <para>
    /// <b>ntext.</b> An <c>ntext</c> column admits <c>LIKE</c>, <c>IS NULL</c> and <c>SUBSTRING</c> but no comparison
    /// operator. The parser gives multiline fields only those three, but a text field may also sit on an
    /// <c>ntext</c> column, so a comparison against one is written on <c>CONVERT(nvarchar(max), column)</c>, which
    /// keeps the column's collation and compares the whole text.
    /// </para>
    /// </remarks>
    /// <param name="schema">The form's catalogue view.</param>
    /// <param name="dataFields">The form's data fields, to resolve each page-field condition by name.</param>
    /// <param name="gridColumns">The grid columns to project, from <see cref="RecordQueryRules.GetGridColumns(IReadOnlyList{DataFieldMetadata})"/>.</param>
    /// <param name="conditions">The parsed conditions; AND across conditions, OR across one condition's alternatives.</param>
    /// <param name="includeDeleted">Whether the selected set holds every row rather than the active set.</param>
    /// <param name="excludeGlobalRecordId">The open record's id to leave out of the result, or <see langword="null"/>.</param>
    /// <returns>The statement and its result layout.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="schema"/>, <paramref name="dataFields"/>, <paramref name="gridColumns"/> or
    /// <paramref name="conditions"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// A list holds a null entry; a condition names no data field of the form, names one whose type differs from the
    /// condition's, or has no alternative; a field or grid column has no column in the form's page tables; or a value
    /// does not fit its column.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">A target or an operator is not a defined member.</exception>
    /// <exception cref="InvalidOperationException">A table lacks a required system or key column.</exception>
    internal static SearchSelect Search(
        FormSchema schema,
        DataFieldSet dataFields,
        IReadOnlyList<DataFieldMetadata> gridColumns,
        IReadOnlyList<SearchCondition> conditions,
        bool includeDeleted,
        string? excludeGlobalRecordId)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(dataFields);
        ArgumentNullException.ThrowIfNull(gridColumns);
        ArgumentNullException.ThrowIfNull(conditions);

        var uniqueKey = RecordSqlBuilder.Require(schema.UniqueKey);
        var recStatus = RecordSqlBuilder.Require(schema.RecStatus);
        var globalRecordId = RecordSqlBuilder.Require(schema.GlobalRecordId);

        // Every field and column is resolved before any text is written, so an unknown field fails with no SQL built.
        var resolved = ResolveConditions(schema, dataFields, conditions);
        var columns = ResolveGridColumns(schema, gridColumns, SearchFirstColumnOrdinal);

        var parameters = new PositionalParameters();
        var sql = new StringBuilder("WITH [n] AS (SELECT ");
        AppendBase(sql, uniqueKey).Append(" AS [k], ROW_NUMBER() OVER (ORDER BY ");
        AppendBase(sql, uniqueKey).Append(") AS [pos] FROM ");
        AppendBaseTable(sql, schema);
        RecordSqlBuilder.AppendSelectedSetPredicate(sql, schema, includeDeleted, parameters, SelectedSetClause.Where);

        sql.Append(") SELECT TOP (")
            .Append(parameters.Add(n => ColumnValueConverter.Int32(n, RecordQueryRules.SearchCap)))
            .Append(") ");
        AppendBase(sql, globalRecordId).Append(" AS ").Append(RecordSqlBuilder.ColumnAlias(IdOrdinal)).Append(", ");
        AppendBase(sql, uniqueKey).Append(" AS ").Append(RecordSqlBuilder.ColumnAlias(UniqueKeyOrdinal)).Append(", ");
        AppendBase(sql, recStatus).Append(" AS ").Append(RecordSqlBuilder.ColumnAlias(RecStatusOrdinal));
        sql.Append(", n.[pos] AS ").Append(RecordSqlBuilder.ColumnAlias(PositionOrdinal))
            .Append(", COUNT_BIG(*) OVER () AS ").Append(RecordSqlBuilder.ColumnAlias(TotalOrdinal));
        AppendProjections(sql, schema, columns);

        sql.Append(" FROM ");
        AppendBaseTable(sql, schema);
        sql.Append(" INNER JOIN [n] n ON n.[k] = ");
        AppendBase(sql, uniqueKey);
        for (var index = 0; index < schema.Pages.Count; index++)
        {
            if (schema.Pages[index].Table is { } table)
            {
                AppendPageJoin(sql, schema, index, table, "INNER JOIN");
            }
        }

        var clause = " WHERE ";
        foreach (var condition in resolved)
        {
            sql.Append(clause).Append('(');
            for (var index = 0; index < condition.Condition.Alternatives.Count; index++)
            {
                sql.Append(index == 0 ? string.Empty : " OR ");
                AppendAlternative(sql, condition, condition.Condition.Alternatives[index], parameters);
            }

            sql.Append(')');
            clause = " AND ";
        }

        if (excludeGlobalRecordId is not null)
        {
            // Bound at full length like every request-supplied id, so an over-long id excludes nothing.
            sql.Append(clause);
            AppendBase(sql, globalRecordId).Append(" <> ")
                .Append(parameters.Add(n => ColumnValueConverter.Text(n, excludeGlobalRecordId)));
        }

        sql.Append(" ORDER BY ");
        AppendBase(sql, uniqueKey);

        return new SearchSelect(parameters.ToStatement(sql.ToString()), columns);
    }

    /// <summary>
    /// Builds one page of the records list: the selected set, filtered by the grid filter, ordered by
    /// <c>UniqueKey</c> and paged with <c>OFFSET ... FETCH</c>.
    /// </summary>
    /// <remarks>
    /// <para>The statement has this shape:</para>
    /// <code>
    /// SELECT b.[GlobalRecordId] AS [c0], b.[UniqueKey] AS [c1], b.[RecStatus] AS [c2], p0.[col] AS [c3], ...
    /// FROM [base] b LEFT JOIN [page] p0 ON p0.[GlobalRecordId] = b.[GlobalRecordId] ...
    /// [WHERE b.[RecStatus] = @p0] [AND (filter)]
    /// ORDER BY b.[UniqueKey] OFFSET @p2 ROWS FETCH NEXT @p3 ROWS ONLY
    /// </code>
    /// <para>
    /// Only the pages holding a grid column are joined, with <c>LEFT JOIN</c>, so a record missing a page row is
    /// still listed with <c>NULL</c> grid values. The offset is a <c>bigint</c>, so the largest admitted page cannot
    /// wrap, and a valid page past the end returns no row. The filter is the one <see cref="ListCount"/> applies.
    /// </para>
    /// </remarks>
    /// <param name="schema">The form's catalogue view.</param>
    /// <param name="gridColumns">The grid columns to project, from <see cref="RecordQueryRules.GetGridColumns(IReadOnlyList{DataFieldMetadata})"/>.</param>
    /// <param name="query">The coerced list query.</param>
    /// <returns>The statement and its result layout.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The query's paging bounds were not validated.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="gridColumns"/> holds a null entry or a field with no column in the form's page tables.
    /// </exception>
    /// <exception cref="InvalidOperationException">A table lacks a required system or key column.</exception>
    internal static ListSelect ListPage(
        FormSchema schema,
        IReadOnlyList<DataFieldMetadata> gridColumns,
        RecordListQuery query)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(gridColumns);
        ArgumentNullException.ThrowIfNull(query);

        var offset = query.Offset;
        var uniqueKey = RecordSqlBuilder.Require(schema.UniqueKey);
        var columns = ResolveGridColumns(schema, gridColumns, ListFirstColumnOrdinal);

        var parameters = new PositionalParameters();
        var sql = new StringBuilder("SELECT ");
        AppendBase(sql, RecordSqlBuilder.Require(schema.GlobalRecordId))
            .Append(" AS ").Append(RecordSqlBuilder.ColumnAlias(IdOrdinal)).Append(", ");
        AppendBase(sql, uniqueKey).Append(" AS ").Append(RecordSqlBuilder.ColumnAlias(UniqueKeyOrdinal)).Append(", ");
        AppendBase(sql, RecordSqlBuilder.Require(schema.RecStatus))
            .Append(" AS ").Append(RecordSqlBuilder.ColumnAlias(RecStatusOrdinal));
        AppendProjections(sql, schema, columns);
        AppendListSource(sql, schema, columns, query, parameters);

        sql.Append(" ORDER BY ");
        AppendBase(sql, uniqueKey)
            .Append(" OFFSET ").Append(parameters.Add(n => ColumnValueConverter.Int64(n, offset)))
            .Append(" ROWS FETCH NEXT ").Append(parameters.Add(n => ColumnValueConverter.Int32(n, query.PageSize)))
            .Append(" ROWS ONLY");

        return new ListSelect(parameters.ToStatement(sql.ToString()), columns);
    }

    /// <summary>
    /// Counts the records the list filters, for its <c>total</c>: <c>SELECT COUNT_BIG(*)</c> over the same joins and
    /// the same predicate as <see cref="ListPage"/>, built by the same private code (<see cref="AppendListSource"/>),
    /// so for one database state the total counts exactly the rows the pages are drawn from.
    /// </summary>
    /// <remarks>
    /// The page and the count are two statements, so this builder alone gives them no common snapshot: a write
    /// committed between them can make the total and a page disagree. A caller that needs them to agree runs both
    /// under one consistent read, for example one snapshot transaction where the database allows snapshot isolation,
    /// or one serializable transaction.
    /// </remarks>
    /// <param name="schema">The form's catalogue view.</param>
    /// <param name="gridColumns">The grid columns the filter searches, the ones passed to <see cref="ListPage"/>.</param>
    /// <param name="query">The coerced list query; its paging is not used.</param>
    /// <returns>A statement returning one <c>bigint</c>.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="gridColumns"/> holds a null entry or a field with no column in the form's page tables.
    /// </exception>
    /// <exception cref="InvalidOperationException">A table lacks a required system or key column.</exception>
    internal static SqlStatement ListCount(
        FormSchema schema,
        IReadOnlyList<DataFieldMetadata> gridColumns,
        RecordListQuery query)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(gridColumns);
        ArgumentNullException.ThrowIfNull(query);

        // The ordinals match ListPage's so the resolved columns are the same; this statement projects none of them.
        var columns = ResolveGridColumns(schema, gridColumns, ListFirstColumnOrdinal);
        var parameters = new PositionalParameters();
        var sql = new StringBuilder("SELECT COUNT_BIG(*)");
        AppendListSource(sql, schema, columns, query, parameters);

        return parameters.ToStatement(sql.ToString());
    }

    /// <summary>
    /// Turns the grid filter into the pattern of a case-insensitive "contains": each <c>\</c>, <c>%</c>, <c>_</c> and
    /// <c>[</c> gets a leading <c>\</c>, the escape character the list statement declares, and the result is wrapped
    /// in <c>%</c>. The backslash is escaped too, so a backslash in the filter matches itself.
    /// </summary>
    /// <param name="filter">The filter text, unchanged and untrimmed.</param>
    /// <returns>For example <c>%a\\b\%c\_d\[e%</c> for <c>a\b%c_d[e</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="filter"/> is null.</exception>
    internal static string EscapeContainsPattern(string filter)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var pattern = new StringBuilder(filter.Length + 2);
        pattern.Append('%');
        foreach (var character in filter)
        {
            if (character is ContainsEscape or '%' or '_' or '[')
            {
                pattern.Append(ContainsEscape);
            }

            pattern.Append(character);
        }

        return pattern.Append('%').ToString();
    }

    // Resolves every condition to the column text it compares, before any statement text is written. A page field
    // resolves through the form's data fields and the reader-built schema, a system target to its base column.
    private static IReadOnlyList<ResolvedCondition> ResolveConditions(
        FormSchema schema,
        DataFieldSet dataFields,
        IReadOnlyList<SearchCondition> conditions)
    {
        var resolved = new List<ResolvedCondition>(conditions.Count);
        foreach (var condition in conditions)
        {
            if (condition is null)
            {
                throw new ArgumentException(NullConditionMessage, nameof(conditions));
            }

            if (condition.Alternatives.Count == 0)
            {
                throw new ArgumentException(EmptyAlternativesMessage, nameof(conditions));
            }

            switch (condition.Target)
            {
                case SearchTarget.PageField:
                    if (!dataFields.TryGet(condition.FieldName, out var field))
                    {
                        throw new ArgumentException(UnknownFieldMessage, nameof(conditions));
                    }

                    // The parameter is typed by the condition's field type, so it must be the column's own.
                    if (field.FieldType != condition.FieldType)
                    {
                        throw new ArgumentException(FieldTypeMismatchMessage, nameof(conditions));
                    }

                    // The ordinal is unused: a criterion column is compared, never projected.
                    var column = RecordSqlBuilder.ResolveDataColumn(schema, field, ordinal: 0);
                    var reference = RecordSqlBuilder.ColumnReference(schema, column);
                    var comparable = string.Equals(
                        column.Column.Metadata.SqlTypeName, NTextTypeName, StringComparison.OrdinalIgnoreCase)
                        ? string.Concat("CONVERT(nvarchar(max), ", reference, ")")
                        : reference;
                    resolved.Add(new ResolvedCondition(condition, reference, comparable, column.Column.Metadata));
                    break;

                case SearchTarget.BaseGlobalRecordId:
                    var idColumn = RecordSqlBuilder.Require(schema.GlobalRecordId);
                    var idReference = BaseReference(idColumn);
                    resolved.Add(new ResolvedCondition(condition, idReference, idReference, idColumn.Metadata));
                    break;

                case SearchTarget.BaseRecStatus:
                    var statusColumn = RecordSqlBuilder.Require(schema.RecStatus);
                    var statusReference = BaseReference(statusColumn);
                    resolved.Add(
                        new ResolvedCondition(condition, statusReference, statusReference, statusColumn.Metadata));
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(conditions), UnknownTargetMessage);
            }
        }

        return resolved;
    }

    // One alternative: a comparison, IS NULL, LIKE with no ESCAPE clause, or the 255-character prefix equality.
    private static void AppendAlternative(
        StringBuilder sql,
        ResolvedCondition condition,
        SearchAlternative alternative,
        PositionalParameters parameters)
    {
        switch (alternative.Operator)
        {
            case SearchOperator.IsNull:
                sql.Append(condition.Reference).Append(" IS NULL");
                return;

            case SearchOperator.Like:
                sql.Append(condition.Reference).Append(" LIKE ").Append(Bind(condition, alternative, parameters));
                return;

            case SearchOperator.PrefixEqual255:
                sql.Append("SUBSTRING(").Append(condition.Reference).Append(", 1, ").Append(PrefixLength)
                    .Append(") = SUBSTRING(").Append(Bind(condition, alternative, parameters))
                    .Append(", 1, ").Append(PrefixLength).Append(')');
                return;

            default:
                var comparison = alternative.Operator switch
                {
                    SearchOperator.Equal => "=",
                    SearchOperator.NotEqual => "<>",
                    SearchOperator.Less => "<",
                    SearchOperator.LessOrEqual => "<=",
                    SearchOperator.Greater => ">",
                    SearchOperator.GreaterOrEqual => ">=",
                    _ => throw new ArgumentOutOfRangeException(nameof(alternative), UnknownOperatorMessage),
                };
                sql.Append(condition.Comparable).Append(' ').Append(comparison).Append(' ')
                    .Append(Bind(condition, alternative, parameters));
                return;
        }
    }

    // Binds an alternative's value as the next positional parameter, typed for the condition's column.
    private static string Bind(ResolvedCondition condition, SearchAlternative alternative, PositionalParameters parameters)
    {
        var value = alternative.Value ?? throw new ArgumentException(MissingValueMessage, nameof(alternative));
        var target = condition.Condition.Target;
        var fieldType = condition.Condition.FieldType;
        var column = condition.Column;

        return parameters.Add(n => ColumnValueConverter.ToSearchParameter(n, target, fieldType, column, value));
    }

    // Resolves the grid columns to reader-built columns with consecutive ordinals from firstOrdinal.
    private static IReadOnlyList<ProjectedColumn> ResolveGridColumns(
        FormSchema schema,
        IReadOnlyList<DataFieldMetadata> gridColumns,
        int firstOrdinal)
    {
        var columns = new List<ProjectedColumn>(gridColumns.Count);
        foreach (var field in gridColumns)
        {
            if (field is null)
            {
                throw new ArgumentException(NullGridColumnMessage, nameof(gridColumns));
            }

            columns.Add(RecordSqlBuilder.ResolveDataColumn(schema, field, firstOrdinal + columns.Count));
        }

        return columns.AsReadOnly();
    }

    // The list's FROM clause and predicate, shared by ListPage and ListCount: the base table LEFT JOINed to each page
    // holding a grid column, the selected set, and the grid filter. The filter is a case-insensitive "contains" over
    // the grid columns that does not depend on the database collation: each column is converted to text (a datetime
    // as yyyy-mm-dd), and both sides are compared under one case-insensitive collation, with the filter's pattern
    // characters escaped. It has no legacy counterpart.
    private static void AppendListSource(
        StringBuilder sql,
        FormSchema schema,
        IReadOnlyList<ProjectedColumn> columns,
        RecordListQuery query,
        PositionalParameters parameters)
    {
        sql.Append(" FROM ");
        AppendBaseTable(sql, schema);

        var joined = new bool[schema.Pages.Count];
        foreach (var column in columns)
        {
            joined[RecordSqlBuilder.PageIndexOf(schema, column.Field.Page.PageId)] = true;
        }

        for (var index = 0; index < joined.Length; index++)
        {
            if (joined[index] && schema.Pages[index].Table is { } table)
            {
                AppendPageJoin(sql, schema, index, table, "LEFT JOIN");
            }
        }

        var filtered = RecordSqlBuilder.AppendSelectedSetPredicate(
            sql, schema, query.IncludeDeleted, parameters, SelectedSetClause.Where);
        if (query.Filter is not { } filter)
        {
            return;
        }

        sql.Append(filtered ? " AND " : " WHERE ");
        if (columns.Count == 0)
        {
            // A filter with no column to search matches nothing.
            sql.Append("1 = 0");
            return;
        }

        // One parameter serves every column's term.
        var pattern = parameters.Add(n => ColumnValueConverter.Text(n, EscapeContainsPattern(filter)));
        sql.Append('(');
        for (var index = 0; index < columns.Count; index++)
        {
            var column = columns[index];
            sql.Append(index == 0 ? string.Empty : " OR ")
                .Append("CONVERT(nvarchar(max), ").Append(RecordSqlBuilder.ColumnReference(schema, column));
            if (column.Column.Metadata.Family == ColumnFamily.DateTime)
            {
                sql.Append(", ").Append(IsoDateStyle);
            }

            sql.Append(") COLLATE ").Append(ContainsCollation)
                .Append(" LIKE ").Append(pattern).Append(" COLLATE ").Append(ContainsCollation)
                .Append(" ESCAPE '").Append(ContainsEscape).Append('\'');
        }

        sql.Append(')');
    }

    // , p{i}.[col] AS [cN] for each projected column.
    private static void AppendProjections(StringBuilder sql, FormSchema schema, IReadOnlyList<ProjectedColumn> columns)
    {
        foreach (var column in columns)
        {
            sql.Append(", ").Append(RecordSqlBuilder.ColumnReference(schema, column))
                .Append(" AS ").Append(RecordSqlBuilder.ColumnAlias(column.Ordinal));
        }
    }

    // <join> [page] p{i} ON p{i}.[GlobalRecordId] = b.[GlobalRecordId]
    private static void AppendPageJoin(StringBuilder sql, FormSchema schema, int pageIndex, TableSchema table, string join)
    {
        var alias = RecordSqlBuilder.PageAlias(pageIndex);
        sql.Append(' ').Append(join).Append(' ').Append(SqlIdentifier.Quote(table)).Append(' ').Append(alias)
            .Append(" ON ").Append(alias).Append('.').Append(SqlIdentifier.Quote(RecordSqlBuilder.RequirePageKey(table)))
            .Append(" = ");
        AppendBase(sql, RecordSqlBuilder.Require(schema.GlobalRecordId));
    }

    // [base] b
    private static void AppendBaseTable(StringBuilder sql, FormSchema schema) =>
        sql.Append(SqlIdentifier.Quote(schema.BaseTable)).Append(' ').Append(RecordSqlBuilder.BaseAlias);

    // b.[column]
    private static StringBuilder AppendBase(StringBuilder sql, ColumnSchema column) => sql.Append(BaseReference(column));

    private static string BaseReference(ColumnSchema column) =>
        string.Concat(RecordSqlBuilder.BaseAlias, ".", SqlIdentifier.Quote(column));

    // A condition with the column text it is rendered on: Reference for IS NULL, LIKE and SUBSTRING, and Comparable
    // for the comparison operators, which differs from Reference only for an ntext column. Column is the reader-built
    // column it reads, whose type the converter binds each value for, so a number on an integer column stays exact.
    private sealed record ResolvedCondition(
        SearchCondition Condition,
        string Reference,
        string Comparable,
        ColumnMetadata Column);
}
