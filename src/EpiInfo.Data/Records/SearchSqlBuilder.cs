using System.Data;
using System.Globalization;
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
/// One page of the records list and the total of its filtered set, with the layout of its result: ordinals
/// <see cref="SearchSqlBuilder.IdOrdinal"/>, <see cref="SearchSqlBuilder.UniqueKeyOrdinal"/> and
/// <see cref="SearchSqlBuilder.RecStatusOrdinal"/> (0, 1, 2) are the base row's <c>GlobalRecordId</c>,
/// <c>UniqueKey</c> and <c>RecStatus</c>; <see cref="SearchSqlBuilder.InPageOrdinal"/> (3) is 1 for a row of the page
/// and 0 otherwise, an <c>int</c>; <see cref="SearchSqlBuilder.TotalOrdinal"/> (4) is the number of rows in the
/// filtered set, a <c>bigint</c>, the same on every row; then one <see cref="ProjectedColumn"/> per grid column, from
/// ordinal 5.
/// </summary>
/// <param name="Statement">
/// The statement to run. It returns the page's rows, flagged 1; for an empty page, a page past the end or an empty
/// filtered set included, it returns one row flagged 0 whose other columns are <c>NULL</c>, so the total always
/// arrives.
/// </param>
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
/// rule: trimming, the <c>" or "</c> split, the comparison prefixes, per-type coercion, the six-field limit, the
/// wildcard-pattern bound and the <c>*</c> to <c>%</c> rewrite. This class renders each alternative exactly as given,
/// however many a condition holds; it reads a value only to reject, as a programming error, a <c>LIKE</c> pattern over
/// that bound, and to pack the values of a large search into parameters ("Packed binding" under <c>Search</c>).
/// </para>
/// <para>
/// <b>Identifiers and values.</b> Every table and column name comes from the reader-built <see cref="FormSchema"/>
/// and reaches the text only through <see cref="SqlIdentifier"/>, by way of the <see cref="RecordSqlBuilder"/>
/// helpers. The CTE name <c>[n]</c>, Find's position column <c>[pos]</c>, the list's derived tables <c>[ct]</c> and
/// <c>[pg]</c> and its count column <c>[tot]</c>, the aliases <c>b</c>, <c>p{i}</c> and <c>[c{n}]</c>, the names a
/// packed list is decoded under (the derived tables <c>[d]</c>, <c>[a]</c> and <c>[e]</c>, the digit tables
/// <c>[r{k}]</c>, and their columns <c>[v]</c>, <c>[i]</c>, <c>[t]</c> and <c>[g]</c>), the collation name, the escape
/// character, the decoding expressions of packed values, a packed statement's plan hints,
/// <c>OPTION (USE HINT ('DISABLE_OPTIMIZER_ROWGOAL', 'FORCE_LEGACY_CARDINALITY_ESTIMATION'))</c>, and the list's,
/// <c>OPTION (FORCE ORDER, LOOP JOIN)</c>, are fixed literals.
/// Every value (criteria, packed or not, the grid filter, the excluded id, the cap, the offset and the page
/// size) is a positional parameter, so text that looks like SQL stays inert data.
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
    /// The ordinal of a list row's page flag in a <see cref="ListSelect"/>, an <c>int</c>: 1 for a row of the requested
    /// page, 0 for the one row an empty page returns, which carries only the total.
    /// </summary>
    internal const int InPageOrdinal = 3;

    /// <summary>
    /// The ordinal of the total in a <see cref="SearchSelect"/> or a <see cref="ListSelect"/>, a <c>bigint</c> that
    /// every row carries: the number of matches, counted before the cap, in a search; the number of rows in the
    /// filtered set, whatever the page, in the list.
    /// </summary>
    internal const int TotalOrdinal = 4;

    // The first grid-column ordinal of each result layout.
    private const int SearchFirstColumnOrdinal = 5;
    private const int ListFirstColumnOrdinal = 5;

    // The fixed names of the list's filtered set, of the derived tables that count it and page it, and of the count.
    private const string ListSetName = "[n]";
    private const string ListTotalName = "[ct]";
    private const string ListPageName = "[pg]";
    private const string ListTotalColumn = "[tot]";

    // SUBSTRING bounds of the multiline comparison, legacy's 255-character prefix.
    private const string PrefixLength = "255";

    // The collation of the grid filter: case-insensitive whatever the database collation is.
    private const string ContainsCollation = "Latin1_General_100_CI_AS";

    // CONVERT style 23 renders a datetime as yyyy-mm-dd, the wire form of a date field.
    private const string IsoDateStyle = "23";

    // The sys.types name of the one admitted character type that rejects comparison operators.
    private const string NTextTypeName = "ntext";

    // The most alternatives other than IS NULL a Find statement binds one parameter each; a search with more binds
    // its values packed, a few packed-list parameters per condition.
    private const int ScalarParameterBudget = 2000;

    // SQL Server accepts 2,100 parameters per command, and sp_executesql, which SqlClient sends a parameterised
    // statement through, takes two of them for the statement text and the parameter declarations.
    private const int MaxStatementParameters = 2098;

    // The name a criterion's scalar parameter is built under before its value is packed; it is never bound.
    private const string PackedValueName = "@v";

    // A packed list is ASCII text: its element count, then the cumulative offsets of its element tokens, 0 first and
    // one after each token, each a decimal number of PackedNumberWidth digits, then the tokens themselves. Ten digits
    // hold every int, so every count and offset a .NET string can have. ASCII text is one code unit per character
    // under every collation, so SUBSTRING reads the same positions whatever the database's collation is.
    private const int PackedNumberWidth = 10;
    private const string PackedNumberFormat = "D10";

    // The decoding of one element token, [e].[t], into the SQL type and value the scalar parameter would bind. Text is
    // the hexadecimal of its UTF-16LE code units, which CONVERT style 2 reads, so every code unit, a lone surrogate
    // included, round-trips.
    private const string PackedTextDecoder = "CAST(CONVERT(varbinary(max), [e].[t], 2) AS nvarchar(max))";

    // A LIKE pattern's text decoding. LIKE reads an nvarchar(max) pattern as nvarchar(4000), the conversion the
    // per-alternative statement applies to its parameter once; decoding a packed pattern to that type does it once per
    // element instead of once per comparison, which with patterns of a few dozen characters made a long list about
    // ten times slower to match. It holds every pattern, because none passes RecordQueryRules.MaxLikePatternLength.
    private const string PackedPatternDecoder = "CAST(CONVERT(varbinary(max), [e].[t], 2) AS nvarchar(4000))";
    private const string PackedByteDecoder = "CAST([e].[t] AS tinyint)";
    private const string PackedInt16Decoder = "CAST([e].[t] AS smallint)";
    private const string PackedInt32Decoder = "CAST([e].[t] AS int)";
    private const string PackedInt64Decoder = "CAST([e].[t] AS bigint)";
    private const string PackedBooleanDecoder = "CAST([e].[t] AS bit)";
    private const string PackedDateTimeDecoder = "CONVERT(datetime, [e].[t], 126)";

    // A float token is the fields of its IEEE 754 bits at fixed widths: the sign (one digit), the biased exponent
    // (four) and the fraction (sixteen). They are rebuilt by arithmetic that is exact at every step: the significand,
    // at most 2^53, converts to float exactly, and multiplying it by a power of two, the original value's own scale,
    // rounds nothing. SQL Server's text-to-float parse is not correctly rounded and it refuses binary-to-float, so
    // neither decimal text nor the raw bits would be exact.
    private const string PackedDoubleDecoder =
        "CASE WHEN CAST(SUBSTRING([e].[t], 1, 1) AS int) = 1 THEN -1e0 ELSE 1e0 END"
        + " * CAST(CAST(SUBSTRING([e].[t], 6, 16) AS bigint)"
        + " + CASE WHEN CAST(SUBSTRING([e].[t], 2, 4) AS int) = 0 THEN 0 ELSE 4503599627370496 END AS float)"
        + " * POWER(CAST(2 AS float), CASE WHEN CAST(SUBSTRING([e].[t], 2, 4) AS int) = 0 THEN -1074"
        + " ELSE CAST(SUBSTRING([e].[t], 2, 4) AS int) - 1075 END)";
    private const string DoubleExponentFormat = "D4";
    private const string DoubleFractionFormat = "D16";

    // The date and time form CONVERT style 126 reads exactly.
    private const string PackedDateTimeFormat = "yyyy-MM-ddTHH:mm:ss.fff";

    // The row limit of the derived table a packed list is decoded in, int.MaxValue, so it keeps every element, and
    // the name its decoded value takes there.
    private const string PackedFenceRows = "2147483647";
    private const string PackedFencedValue = "[d].[v]";

    // One digit table of the row source that numbers a packed list's elements: k of them, cross joined, give each
    // index from 0 to 10^k - 1 once.
    private const string PackedDigits = "(VALUES (0), (1), (2), (3), (4), (5), (6), (7), (8), (9))";
    private const int PackedDigitBase = 10;

    // The plan hints a packed statement ends with. The optimizer costs decoding a list as almost free, so with the
    // row goal TOP (@cap) sets, or under the cardinality estimator of compatibility level 130 and later, it can pick a
    // nested-loops semi-join that decodes and sorts the whole list again for every candidate row instead of spooling
    // it once: a few thousand LIKE alternatives then run past the 30-second default command timeout. The row goal buys
    // nothing, because COUNT_BIG(*) OVER () reads every match before TOP returns a row, and the estimator of levels 100
    // and 110 is applied at every level, so each level plans the decoding as those do: once per list.
    private const string PackedPlanHints =
        " OPTION (USE HINT ('DISABLE_OPTIMIZER_ROWGOAL', 'FORCE_LEGACY_CARDINALITY_ESTIMATION'))";

    // The plan hints the list statement ends with: the joins run in written order, each as nested loops, so the base
    // table is the outer input of every page join and each page row is read only after its base row is held (ListPage).
    private const string ListPlanHints = " OPTION (FORCE ORDER, LOOP JOIN)";

    // SQL Server's largest decimal precision, the precision a packed decimal element is read back with.
    private const int MaxDecimalPrecision = 38;

    // The IEEE 754 binary64 fields and the biased exponent of infinities and NaN, which no SQL float holds.
    private const int DoubleExponentShift = 52;
    private const long DoubleExponentMask = 0x7FF;
    private const long DoubleFractionMask = (1L << DoubleExponentShift) - 1;
    private const long NonFiniteExponent = 0x7FF;

    // Fixed messages: the redaction policy keeps identifiers, field names, values and ids out of exception text.
    private const string NullConditionMessage = "The search conditions must not hold a null entry.";
    private const string EmptyAlternativesMessage = "A search condition needs at least one alternative.";
    private const string UnknownFieldMessage = "A search condition names a field that is not a data field of the form.";
    private const string FieldTypeMismatchMessage = "A search condition's field type is not the type of its field.";
    private const string UnknownTargetMessage = "The search target is not a defined member.";
    private const string UnknownOperatorMessage = "The search operator is not a defined member.";
    private const string MissingValueMessage = "A search alternative other than IS NULL needs a value.";
    private const string NullGridColumnMessage = "The grid columns must not hold a null entry.";
    private const string TooManyParametersMessage =
        "The search conditions need more parameters than one SQL Server statement can bind.";
    private const string UnpackableTypeMessage = "A search parameter of this type has no packed form.";
    private const string UnpackableValueMessage = "A search parameter value has no exact packed form.";
    private const string PatternTooLongMessage = "A search pattern is longer than SQL Server's LIKE pattern limit.";
    private const string FilterTooLongMessage =
        "The grid filter's pattern is longer than SQL Server's LIKE pattern limit.";

    /// <summary>
    /// Builds the Find statement: the selected set numbered by <c>UniqueKey</c>, joined to every page table, filtered
    /// by the conditions and the excluded id, ordered by <c>UniqueKey</c> and capped at
    /// <see cref="RecordQueryRules.SearchCap"/> rows.
    /// </summary>
    /// <remarks>
    /// <para>The statement has this shape:</para>
    /// <code>
    /// WITH [n] AS (SELECT b.[GlobalRecordId], b.[UniqueKey], b.[RecStatus],
    ///                     ROW_NUMBER() OVER (ORDER BY b.[UniqueKey]) AS [pos]
    ///              FROM [base] b WITH (HOLDLOCK) [WHERE b.[RecStatus] = @p0])
    /// SELECT TOP (@p1) b.[GlobalRecordId] AS [c0], b.[UniqueKey] AS [c1], b.[RecStatus] AS [c2],
    ///        b.[pos] AS [c3], COUNT_BIG(*) OVER () AS [c4], p0.[col] AS [c5], ...
    /// FROM [n] b INNER JOIN [page] p0 ON p0.[GlobalRecordId] = b.[GlobalRecordId] ...
    /// WHERE (alt OR alt ...) AND (alt OR ...)
    ///   [AND NOT (b.[GlobalRecordId] = @pX AND DATALENGTH(b.[GlobalRecordId]) = DATALENGTH(@pX))]
    /// ORDER BY b.[UniqueKey]
    /// [OPTION (USE HINT ('DISABLE_OPTIMIZER_ROWGOAL', 'FORCE_LEGACY_CARDINALITY_ESTIMATION'))]  -- packed only
    /// </code>
    /// <para>
    /// <b>Positions.</b> The CTE reads the base table only and applies only the selected-set predicate, so
    /// <c>[pos]</c> is numbered before any criterion and equals the navigation position, the count of selected rows
    /// with <c>UniqueKey &lt;= k</c>.
    /// </para>
    /// <para>
    /// <b>One base read.</b> The CTE is the statement's only reference to the base table. It keeps the base system
    /// columns under their catalogue names, and the outer query reads it as <c>b</c>, so the projected id, key and
    /// status, the system-field criteria, the page joins and the exclusion all read the row that was numbered. The
    /// reference carries the base-table hint <c>WITH (HOLDLOCK)</c> that <see cref="RecordSqlBuilder"/>'s class remarks
    /// describe for its record reads: under the database's locking <c>READ COMMITTED</c>, every shared and key-range
    /// lock taken through it is held until the statement completes, so no base row read can change, and no row can be
    /// inserted among them, before it returns, and each result's membership in the selected set, its position and its
    /// status describe one committed state even under a concurrent soft delete. That holds too where the plan reads the
    /// reference more than once, as SQL Server may when it expands an <c>OR</c> into separate branches: every read
    /// holds its locks, so each sees the rows the others hold. Only the base table carries the hint, and every
    /// application write locks a record's base row before any of its page rows, so the page rows joined to a held base
    /// row cannot change either.
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
    /// from the result afterwards (<c>:2256-2266</c>) and had neither a total nor a cap. The exclusion is
    /// <see cref="RecordSqlBuilder.AppendGlobalRecordIdExclusion"/>, the negation of the exact match every lookup by id
    /// uses, so it leaves out exactly the record a lookup by that id finds: an id that differs from a stored id only by
    /// trailing spaces, or is longer than the column, excludes nothing, and letter case follows the collation.
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
    /// <para>
    /// <b>Packed binding.</b> A search binds one parameter per alternative other than <c>IS NULL</c> while it holds at
    /// most 2,000 of them, so the statement, at most 2,003 parameters with the selected-set status, the cap and the
    /// excluded id, stays under the 2,098 a <c>sp_executesql</c> command leaves of SQL Server's 2,100. A search with
    /// more renders every condition packed instead, so its parameter count no longer grows with its alternatives:
    /// </para>
    /// <code>
    /// (column IS NULL OR EXISTS (SELECT 1 FROM (SELECT TOP (2147483647) decoded AS [v]
    ///      FROM (SELECT [r0].[g] + 10 * [r1].[g] ... AS [i]
    ///            FROM (VALUES (0), ..., (9)) AS [r0]([g]) CROSS JOIN ...) AS [a]
    ///      CROSS APPLY (VALUES (CASE WHEN [a].[i] &lt; count THEN SUBSTRING(@p2, start, length) END)) AS [e]([t])
    ///      WHERE [a].[i] &lt; count ORDER BY [v]) AS [d] WHERE column LIKE [d].[v]) OR ...)
    /// </code>
    /// <para>
    /// The <c>IS NULL</c> alternatives give one <c>IS NULL</c> term. The other values of a condition are grouped by
    /// operator and by the type of the scalar parameter each would bind, one type per condition except that a number
    /// on an integer column binds <c>bigint</c> or <c>decimal</c>, and each group is one <c>nvarchar(max)</c>
    /// parameter holding a packed list, at most twelve per condition. A packed list is ASCII text: the element count
    /// and the cumulative offsets of the element tokens, starting with 0, each ten decimal digits, then the tokens.
    /// The derived table <c>[d]</c> decodes it into one row per element: cross-joined digit tables, as many as the
    /// count has digits, number the elements, and <c>SUBSTRING</c> reads each element's token at the offsets the
    /// header gives, so the count, the offsets and the values all stay in the parameter and the text depends on the
    /// count only through its number of digits. It uses only T-SQL that every database compatibility level and every
    /// session <c>SET</c> option evaluate the same way, so Find runs on any database SQL Server 2022 accepts; no
    /// <c>OPENJSON</c>, which needs level 130, <c>JSON_VALUE</c>, <c>STRING_SPLIT</c> or <c>xml</c> method. The
    /// <c>EXISTS</c> applies the operator's own predicate to each decoded element, the comparison on the comparable
    /// column, <c>LIKE</c> with no <c>ESCAPE</c> clause and the 255-character <c>SUBSTRING</c> equality, so an element
    /// matches exactly when its scalar parameter would. The <c>TOP (2147483647) ... ORDER BY [v]</c> keeps every
    /// element; ordering by the decoded value makes SQL Server decode each element once, before the sort, and no
    /// predicate can be pushed below the <c>TOP</c>, so the decoded set stays uncorrelated: SQL Server can match an
    /// equality with one merge or hash semi-join and scan the elements of any other operator from one spool, rather
    /// than decoding the whole list again for each row, which a list of thousands of values would make take many
    /// seconds. Because SQL Server costs that decoding as almost free, it may still choose to repeat it per row under
    /// the row goal of <c>TOP</c>, which buys nothing since <c>COUNT_BIG(*) OVER ()</c> reads every match first, or
    /// under the cardinality estimator of level 130 and later, so a packed statement alone ends with
    /// <c>OPTION (USE HINT ('DISABLE_OPTIMIZER_ROWGOAL', 'FORCE_LEGACY_CARDINALITY_ESTIMATION'))</c>, which makes every
    /// level plan it as levels 100 and 110 do without a row goal: once per list. Each value is first built as that
    /// scalar parameter by <see cref="ColumnValueConverter.ToSearchParameter"/>, which checks its kind and range, and
    /// then encoded by the parameter's type so that its decoding has that same SQL type and value: text as the
    /// hexadecimal of its UTF-16LE code units, decoded through <c>varbinary(max)</c> with <c>CONVERT</c> style 2 to
    /// <c>nvarchar(max)</c>, or for a <c>LIKE</c> pattern to the <c>nvarchar(4000)</c> that <c>LIKE</c> converts its
    /// <c>nvarchar(max)</c> parameter to, which holds every pattern within the length bound below and is then not
    /// converted again for each comparison; either keeps every code unit and, like a parameter, takes the database's
    /// coercible-default collation, so a column's collation governs the comparison as it does for a parameter;
    /// integers and bits as invariant decimal text, cast
    /// to their own type; a <c>decimal</c> as its invariant text, cast to <c>decimal(38, s)</c> with <c>s</c> the
    /// largest scale of its list; a date as <c>yyyy-MM-ddTHH:mm:ss.fff</c> read with <c>CONVERT</c> style 126, exact
    /// for the midnight values search binds; and a float as the sign, exponent and fraction of its IEEE 754 bits at
    /// fixed widths, rebuilt by exact arithmetic, because SQL Server's text-to-float parse is not correctly rounded.
    /// </para>
    /// <para>
    /// <b>Bounds.</b> A statement that would still bind more than 2,098 parameters, which only conditions beyond the
    /// parser's six fields can need, is rejected here before it is returned. Each <c>LIKE</c> pattern is at most
    /// <see cref="RecordQueryRules.MaxLikePatternLength"/> characters, SQL Server's 8,000-byte pattern limit; the
    /// search-criteria parser enforces it with a 400, and longer input is rejected here before any text is built.
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
    /// condition's, or has no alternative; the conditions hold a <c>LIKE</c> pattern longer than
    /// <see cref="RecordQueryRules.MaxLikePatternLength"/>; a field or grid column has no column in the form's page
    /// tables; a value does not fit its column; or the statement would bind more than 2,098 parameters.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">A target or an operator is not a defined member.</exception>
    /// <exception cref="InvalidOperationException">
    /// A table lacks a required system or key column, or a value to pack has a parameter type or value with no exact
    /// packed form.
    /// </exception>
    internal static SearchSelect Search(
        FormSchema schema,
        DataFieldSet dataFields,
        IReadOnlyList<DataFieldMetadata> gridColumns,
        IReadOnlyList<SearchCondition> conditions,
        bool includeDeleted,
        string? excludeGlobalRecordId) =>
        Search(
            schema, dataFields, gridColumns, conditions, includeDeleted, excludeGlobalRecordId, ScalarParameterBudget);

    /// <summary>
    /// Builds the Find statement as the six-argument overload does, with the number of alternatives other than
    /// <c>IS NULL</c> above which every condition is packed given explicitly, so tests can render the same search
    /// both ways.
    /// </summary>
    /// <param name="schema">The form's catalogue view.</param>
    /// <param name="dataFields">The form's data fields, to resolve each page-field condition by name.</param>
    /// <param name="gridColumns">The grid columns to project.</param>
    /// <param name="conditions">The parsed conditions; AND across conditions, OR across one condition's alternatives.</param>
    /// <param name="includeDeleted">Whether the selected set holds every row rather than the active set.</param>
    /// <param name="excludeGlobalRecordId">The open record's id to leave out of the result, or <see langword="null"/>.</param>
    /// <param name="scalarParameterBudget">
    /// The most alternatives other than <c>IS NULL</c> that bind one parameter each; 0 packs every search that binds a
    /// value.
    /// </param>
    /// <returns>The statement and its result layout.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="schema"/>, <paramref name="dataFields"/>, <paramref name="gridColumns"/> or
    /// <paramref name="conditions"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">As for the six-argument overload.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="scalarParameterBudget"/> is negative, or a target or an operator is not a defined member.
    /// </exception>
    /// <exception cref="InvalidOperationException">As for the six-argument overload.</exception>
    internal static SearchSelect Search(
        FormSchema schema,
        DataFieldSet dataFields,
        IReadOnlyList<DataFieldMetadata> gridColumns,
        IReadOnlyList<SearchCondition> conditions,
        bool includeDeleted,
        string? excludeGlobalRecordId,
        int scalarParameterBudget)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(dataFields);
        ArgumentNullException.ThrowIfNull(gridColumns);
        ArgumentNullException.ThrowIfNull(conditions);
        ArgumentOutOfRangeException.ThrowIfNegative(scalarParameterBudget);

        var uniqueKey = RecordSqlBuilder.Require(schema.UniqueKey);
        var recStatus = RecordSqlBuilder.Require(schema.RecStatus);
        var globalRecordId = RecordSqlBuilder.Require(schema.GlobalRecordId);

        // Every field and column is resolved before any text is written, so an unknown field fails with no SQL built.
        var resolved = ResolveConditions(schema, dataFields, conditions);
        var columns = ResolveGridColumns(schema, gridColumns, SearchFirstColumnOrdinal);

        // The numbered selected set is the statement's one base-table reference. It keeps the base system columns
        // under their catalogue names, so every b.[...] reference below reads the numbered row, and its HOLDLOCK keeps
        // that row's membership, position and status one committed state until the statement completes.
        var parameters = new PositionalParameters();
        var sql = new StringBuilder("WITH [n] AS (SELECT ");
        AppendBase(sql, globalRecordId).Append(", ");
        AppendBase(sql, uniqueKey).Append(", ");
        AppendBase(sql, recStatus).Append(", ROW_NUMBER() OVER (ORDER BY ");
        AppendBase(sql, uniqueKey).Append(") AS [pos] FROM ");
        AppendBaseTable(sql, schema);
        sql.Append(" WITH (HOLDLOCK)");
        RecordSqlBuilder.AppendSelectedSetPredicate(sql, schema, includeDeleted, parameters, SelectedSetClause.Where);

        sql.Append(") SELECT TOP (")
            .Append(parameters.Add(n => ColumnValueConverter.Int32(n, RecordQueryRules.SearchCap)))
            .Append(") ");
        AppendBase(sql, globalRecordId).Append(" AS ").Append(RecordSqlBuilder.ColumnAlias(IdOrdinal)).Append(", ");
        AppendBase(sql, uniqueKey).Append(" AS ").Append(RecordSqlBuilder.ColumnAlias(UniqueKeyOrdinal)).Append(", ");
        AppendBase(sql, recStatus).Append(" AS ").Append(RecordSqlBuilder.ColumnAlias(RecStatusOrdinal));
        sql.Append(", ").Append(RecordSqlBuilder.BaseAlias).Append(".[pos] AS ")
            .Append(RecordSqlBuilder.ColumnAlias(PositionOrdinal))
            .Append(", COUNT_BIG(*) OVER () AS ").Append(RecordSqlBuilder.ColumnAlias(TotalOrdinal));
        AppendProjections(sql, schema, columns);

        sql.Append(" FROM [n] ").Append(RecordSqlBuilder.BaseAlias);
        for (var index = 0; index < schema.Pages.Count; index++)
        {
            if (schema.Pages[index].Table is { } table)
            {
                AppendPageJoin(sql, schema, index, table, "INNER JOIN", holdLock: false);
            }
        }

        // Within the budget each alternative binds its own parameter; beyond it every condition is packed, so the
        // parameter count no longer grows with the alternatives.
        var packed = CountBindingAlternatives(resolved) > scalarParameterBudget;
        var clause = " WHERE ";
        foreach (var condition in resolved)
        {
            sql.Append(clause).Append('(');
            if (packed)
            {
                AppendPackedAlternatives(sql, condition, parameters);
            }
            else
            {
                for (var index = 0; index < condition.Condition.Alternatives.Count; index++)
                {
                    sql.Append(index == 0 ? string.Empty : " OR ");
                    AppendAlternative(sql, condition, condition.Condition.Alternatives[index], parameters);
                }
            }

            sql.Append(')');
            clause = " AND ";
        }

        if (excludeGlobalRecordId is not null)
        {
            // The negated exact match of every lookup by id, inside WHERE so the record is gone before the total and
            // the cap: a spelling that finds no row by id, padded or over-long, excludes nothing.
            sql.Append(clause);
            RecordSqlBuilder.AppendGlobalRecordIdExclusion(sql, schema, parameters, excludeGlobalRecordId);
        }

        sql.Append(" ORDER BY ");
        AppendBase(sql, uniqueKey);

        // Only a packed statement decodes lists; the per-alternative statement keeps the optimizer's own choices.
        if (packed)
        {
            sql.Append(PackedPlanHints);
        }

        if (parameters.Count > MaxStatementParameters)
        {
            throw new ArgumentException(TooManyParametersMessage, nameof(conditions));
        }

        return new SearchSelect(parameters.ToStatement(sql.ToString()), columns);
    }

    /// <summary>
    /// Builds one page of the records list and the total of its filtered set in one statement: the selected set,
    /// filtered by the grid filter, counted, and paged in <c>UniqueKey</c> order with <c>OFFSET ... FETCH</c>.
    /// </summary>
    /// <remarks>
    /// <para>The statement has this shape:</para>
    /// <code>
    /// WITH [n] AS (SELECT b.[GlobalRecordId] AS [c0], b.[UniqueKey] AS [c1], b.[RecStatus] AS [c2],
    ///                     p0.[col] AS [c5], ...
    ///              FROM [base] b WITH (HOLDLOCK)
    ///              LEFT JOIN [page] p0 WITH (HOLDLOCK) ON p0.[GlobalRecordId] = b.[GlobalRecordId] ...
    ///              [WHERE b.[RecStatus] = @p0] [AND (filter)])
    /// SELECT [pg].[c0], [pg].[c1], [pg].[c2], CASE WHEN [pg].[c1] IS NULL THEN 0 ELSE 1 END AS [c3],
    ///        [ct].[tot] AS [c4], [pg].[c5], ...
    /// FROM (SELECT COUNT_BIG(*) AS [tot] FROM [n]) AS [ct]
    /// OUTER APPLY (SELECT [c0], [c1], [c2], [c5], ... FROM [n]
    ///              ORDER BY [c1] OFFSET @p2 ROWS FETCH NEXT @p3 ROWS ONLY) AS [pg]
    /// ORDER BY [pg].[c1]
    /// OPTION (FORCE ORDER, LOOP JOIN)
    /// </code>
    /// <para>
    /// <b>Page and total.</b> <c>[n]</c> is the filtered set. <c>[ct]</c> counts it, and <c>[pg]</c> is the page,
    /// <c>OFFSET ... FETCH</c> in <c>UniqueKey</c> order, with a <c>bigint</c> offset and an <c>int</c> page size, so
    /// the largest admitted page cannot wrap. The count is one row, and applying the page to it returns each page row
    /// with the total, flagged 1 at <see cref="InPageOrdinal"/>, or, for an empty page, one row of <c>NULL</c> page
    /// columns flagged 0 that carries the total alone: a valid page past the end has no item and the correct total, and
    /// an empty filtered set a total of 0.
    /// </para>
    /// <para>
    /// <b>One consistent read.</b> The count and the page each read the filtered set, so this one statement evaluates
    /// <c>[n]</c> twice, and every table <c>[n]</c> reads, the base table and each joined page table, carries the hint
    /// <c>WITH (HOLDLOCK)</c>. Under the database's locking <c>READ COMMITTED</c>, every shared and key-range lock
    /// either evaluation takes is held until the statement completes, so no base or page row one of them read can
    /// change, and no row can be inserted among the base rows, before the other has read it too: the page and the
    /// total describe one committed state even under a concurrent soft delete, a change to a filtered field, or a
    /// change to a page row alone, which a writer outside the application, or an application update of a base table
    /// with neither last-save column, makes without updating the base row. This is the one read that holds its page
    /// tables; the record reads and Find read each row once and hold only the base table, as
    /// <see cref="RecordSqlBuilder"/>'s class remarks describe.
    /// </para>
    /// <para>
    /// <b>Lock order.</b> A held page-row lock could deadlock with a write that holds that record's exclusive page-row
    /// lock while it waits for a base row the list holds. The statement therefore ends with
    /// <c>OPTION (FORCE ORDER, LOOP JOIN)</c>: the joins run in their written order, each as nested loops, so the base
    /// table is the outer input of every page join, and each evaluation reads a page row only once it holds that
    /// row's base row. Page tables key on <c>GlobalRecordId</c>, so each inner read is a seek that locks the base row's
    /// own page row or, where it has none, the key range that row would sit in. Every application write locks a
    /// record's base row before any of its page rows: an update takes its update lock on the base row, and updates
    /// the base row's last-save columns where the table has them, before its page rows; a create inserts the base row
    /// first; and a soft delete writes the base row alone. Its later reads of other rows take only shared locks. A
    /// write that holds a page row the list waits for therefore waits on nothing the list holds, and a write that
    /// waits for a base row the list holds holds none of the page rows the list still has to read, so no application
    /// write forms a lock cycle with the list.
    /// </para>
    /// <para>
    /// <b>Residual deadlocks.</b> Two waits can still end in a deadlock (1205): a writer outside the application that
    /// changes a page row the list holds while it holds one the list waits for, and a create whose new page key falls
    /// in the key range a page seek of the list locked for a record with no row on that page, while the list waits for
    /// that create's base row. The list can also be chosen as the victim of a deadlock between writers whose base-row
    /// locks it waits behind, a wait its held base-table read alone already allows. SQL Server ends one statement as
    /// the victim, which the store reports as unavailable: the list answers 503, and an application create 503 with
    /// nothing written. None of them returns a page and a total from two different states.
    /// </para>
    /// <para>
    /// Only the pages holding a grid column are joined, with <c>LEFT JOIN</c>, so a record missing a page row is still
    /// listed with <c>NULL</c> grid values and still counted.
    /// </para>
    /// </remarks>
    /// <param name="schema">The form's catalogue view.</param>
    /// <param name="gridColumns">The grid columns to project, from <see cref="RecordQueryRules.GetGridColumns(IReadOnlyList{DataFieldMetadata})"/>.</param>
    /// <param name="query">The coerced list query.</param>
    /// <returns>The statement and its result layout.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The query's paging bounds were not validated.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="gridColumns"/> holds a null entry or a field with no column in the form's page tables; or the
    /// query's filter fails <see cref="RecordQueryRules.IsRepresentableFilter"/>, so its contains pattern would be
    /// longer than <see cref="RecordQueryRules.MaxLikePatternLength"/>.
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

        // The filtered set is the statement's one source of base and page rows, read by the count and by the page with
        // every table under HOLDLOCK and, through the plan hints at the end, the base table first.
        var parameters = new PositionalParameters();
        var sql = new StringBuilder("WITH ").Append(ListSetName).Append(" AS (SELECT ");
        AppendBase(sql, RecordSqlBuilder.Require(schema.GlobalRecordId))
            .Append(" AS ").Append(RecordSqlBuilder.ColumnAlias(IdOrdinal)).Append(", ");
        AppendBase(sql, uniqueKey).Append(" AS ").Append(RecordSqlBuilder.ColumnAlias(UniqueKeyOrdinal)).Append(", ");
        AppendBase(sql, RecordSqlBuilder.Require(schema.RecStatus))
            .Append(" AS ").Append(RecordSqlBuilder.ColumnAlias(RecStatusOrdinal));
        AppendProjections(sql, schema, columns);
        AppendListSource(sql, schema, columns, query, parameters);

        // The page's columns as the filtered set names them, and as the outer query reads them from the page.
        var setColumns = new StringBuilder();
        var pageColumns = new StringBuilder();
        foreach (var ordinal in new[] { IdOrdinal, UniqueKeyOrdinal, RecStatusOrdinal }
                     .Concat(columns.Select(column => column.Ordinal)))
        {
            var alias = RecordSqlBuilder.ColumnAlias(ordinal);
            setColumns.Append(setColumns.Length == 0 ? string.Empty : ", ").Append(alias);
            pageColumns.Append(", ").Append(ListPageName).Append('.').Append(alias);
        }

        var pageKey = string.Concat(ListPageName, ".", RecordSqlBuilder.ColumnAlias(UniqueKeyOrdinal));
        sql.Append(") SELECT ")
            .Append(ListPageName).Append('.').Append(RecordSqlBuilder.ColumnAlias(IdOrdinal)).Append(", ")
            .Append(pageKey).Append(", ")
            .Append(ListPageName).Append('.').Append(RecordSqlBuilder.ColumnAlias(RecStatusOrdinal))
            .Append(", CASE WHEN ").Append(pageKey).Append(" IS NULL THEN 0 ELSE 1 END AS ")
            .Append(RecordSqlBuilder.ColumnAlias(InPageOrdinal))
            .Append(", ").Append(ListTotalName).Append('.').Append(ListTotalColumn)
            .Append(" AS ").Append(RecordSqlBuilder.ColumnAlias(TotalOrdinal));
        foreach (var column in columns)
        {
            sql.Append(", ").Append(ListPageName).Append('.').Append(RecordSqlBuilder.ColumnAlias(column.Ordinal));
        }

        // The offset is a bigint and the page size an int, bound after the filter's parameters.
        sql.Append(" FROM (SELECT COUNT_BIG(*) AS ").Append(ListTotalColumn).Append(" FROM ").Append(ListSetName)
            .Append(") AS ").Append(ListTotalName)
            .Append(" OUTER APPLY (SELECT ").Append(setColumns).Append(" FROM ").Append(ListSetName)
            .Append(" ORDER BY ").Append(RecordSqlBuilder.ColumnAlias(UniqueKeyOrdinal))
            .Append(" OFFSET ").Append(parameters.Add(n => ColumnValueConverter.Int64(n, offset)))
            .Append(" ROWS FETCH NEXT ").Append(parameters.Add(n => ColumnValueConverter.Int32(n, query.PageSize)))
            .Append(" ROWS ONLY) AS ").Append(ListPageName)
            .Append(" ORDER BY ").Append(pageKey)
            .Append(ListPlanHints);

        return new ListSelect(parameters.ToStatement(sql.ToString()), columns);
    }

    // Resolves every condition to the column text it compares, before any statement text is written. A page field
    // resolves through the form's data fields and the reader-built schema, a system target to its base column. The
    // parser's LIKE pattern bound is checked again here, so a longer pattern fails with no statement built.
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

            foreach (var alternative in condition.Alternatives)
            {
                if (alternative is { Operator: SearchOperator.Like, Value: { } pattern }
                    && pattern.TryGetText(out var text)
                    && text.Length > RecordQueryRules.MaxLikePatternLength)
                {
                    throw new ArgumentException(PatternTooLongMessage, nameof(conditions));
                }
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

    // One alternative: IS NULL, or its operator's predicate on its own positional parameter.
    private static void AppendAlternative(
        StringBuilder sql,
        ResolvedCondition condition,
        SearchAlternative alternative,
        PositionalParameters parameters)
    {
        if (alternative.Operator == SearchOperator.IsNull)
        {
            sql.Append(condition.Reference).Append(" IS NULL");
            return;
        }

        AppendPredicate(sql, condition, alternative.Operator, Bind(condition, alternative, parameters));
    }

    // One condition in packed form: one IS NULL term for its IS NULL alternatives, then, for each operator and scalar
    // parameter type in the order the alternatives first use them, an EXISTS over the packed list of their values.
    private static void AppendPackedAlternatives(
        StringBuilder sql,
        ResolvedCondition condition,
        PositionalParameters parameters)
    {
        var matchesNull = false;
        var groups = new List<PackedGroup>();
        foreach (var alternative in condition.Condition.Alternatives)
        {
            if (alternative.Operator == SearchOperator.IsNull)
            {
                matchesNull = true;
                continue;
            }

            // Built exactly as the scalar path binds it, so the value is checked the same way and keeps its type.
            var scalar = ToScalarParameter(condition, alternative, PackedValueName);
            var group = groups.Find(
                candidate => candidate.Operator == alternative.Operator && candidate.DbType == scalar.DbType);
            if (group is null)
            {
                group = new PackedGroup(alternative.Operator, scalar.DbType, []);
                groups.Add(group);
            }

            group.Values.Add(scalar);
        }

        var separator = string.Empty;
        if (matchesNull)
        {
            sql.Append(condition.Reference).Append(" IS NULL");
            separator = " OR ";
        }

        foreach (var group in groups)
        {
            var list = PackValues(group.Values, out var decoder);
            if (group.Operator == SearchOperator.Like && group.DbType == DbType.String)
            {
                decoder = PackedPatternDecoder;
            }

            var name = parameters.Add(n => ColumnValueConverter.Text(n, list));
            sql.Append(separator).Append("EXISTS (SELECT 1 FROM ");
            AppendPackedElements(sql, name, group.Values.Count, decoder);
            sql.Append(" WHERE ");
            AppendPredicate(sql, condition, group.Operator, PackedFencedValue);
            sql.Append(')');
            separator = " OR ";
        }
    }

    // The derived table [d] of a packed list's decoded elements, one row each, the value named [v]:
    //   (SELECT TOP (2147483647) decoder AS [v]
    //    FROM (SELECT [r0].[g] + 10 * [r1].[g] + ... AS [i] FROM digits AS [r0]([g]) CROSS JOIN ...) AS [a]
    //    CROSS APPLY (VALUES (CASE WHEN [a].[i] < count THEN SUBSTRING(@p, token start, token length) END)) AS [e]([t])
    //    WHERE [a].[i] < count ORDER BY [v]) AS [d]
    // with count CAST(SUBSTRING(@p, 1, 10) AS int) and token i's start and end read from offsets i and i + 1. It uses
    // only T-SQL that every compatibility level and every SET option evaluates the same way: no OPENJSON, JSON
    // function, STRING_SPLIT or xml method. The digit tables, the fewest whose product reaches the element count,
    // number the elements, so the text depends on the count only through its number of digits; the count, the
    // offsets and the values are read from the parameter. The CASE keeps an index past the count from reading token
    // text as an offset, whatever order SQL Server evaluates the filter in. Ordering by the decoded value makes SQL
    // Server decode each element once, before the sort, instead of deferring the decoding into the predicate, where it
    // would read the parameter again for every row compared; and no predicate can be pushed below the TOP, so the
    // decoded set stays uncorrelated: SQL Server can match an equality with one merge or hash semi-join and scan the
    // elements of any other operator from one spool rather than decoding the whole list again for every row.
    private static void AppendPackedElements(StringBuilder sql, string name, int count, string decoder)
    {
        // 1-based positions: the count at 1, offset i at W + 1 + W * i, and the tokens from 2W + 1 + W * count, after
        // the count and the count + 1 offsets; the element index [a].[i] is 0-based.
        var width = PackedNumberWidth.ToString(CultureInfo.InvariantCulture);
        var firstOffset = (PackedNumberWidth + 1).ToString(CultureInfo.InvariantCulture);
        var afterHeader = ((2 * PackedNumberWidth) + 1).ToString(CultureInfo.InvariantCulture);
        var elementCount = string.Concat("CAST(SUBSTRING(", name, ", 1, ", width, ") AS int)");
        var tokenStart = string.Concat(
            "CAST(SUBSTRING(", name, ", ", firstOffset, " + ", width, " * [a].[i], ", width, ") AS int)");
        var tokenEnd = string.Concat(
            "CAST(SUBSTRING(", name, ", ", afterHeader, " + ", width, " * [a].[i], ", width, ") AS int)");

        // A .NET list's count needs at most ten digit tables, and a list long enough to need a tenth would have a
        // header longer than any string, so the index sum stays within int.
        var tables = 1;
        for (var reach = (long)PackedDigitBase; reach < count; reach *= PackedDigitBase)
        {
            tables++;
        }

        sql.Append("(SELECT TOP (").Append(PackedFenceRows).Append(") ").Append(decoder)
            .Append(" AS [v] FROM (SELECT ");
        var multiplier = 1L;
        for (var table = 0; table < tables; table++)
        {
            if (table > 0)
            {
                sql.Append(" + ").Append(multiplier.ToString(CultureInfo.InvariantCulture)).Append(" * ");
            }

            sql.Append("[r").Append(table.ToString(CultureInfo.InvariantCulture)).Append("].[g]");
            multiplier *= PackedDigitBase;
        }

        sql.Append(" AS [i] FROM ");
        for (var table = 0; table < tables; table++)
        {
            sql.Append(table == 0 ? string.Empty : " CROSS JOIN ").Append(PackedDigits)
                .Append(" AS [r").Append(table.ToString(CultureInfo.InvariantCulture)).Append("]([g])");
        }

        sql.Append(") AS [a] CROSS APPLY (VALUES (CASE WHEN [a].[i] < ").Append(elementCount)
            .Append(" THEN SUBSTRING(").Append(name).Append(", ").Append(afterHeader).Append(" + ").Append(width)
            .Append(" * ").Append(elementCount).Append(" + ").Append(tokenStart).Append(", ").Append(tokenEnd)
            .Append(" - ").Append(tokenStart).Append(") END)) AS [e]([t]) WHERE [a].[i] < ").Append(elementCount)
            .Append(" ORDER BY [v]) AS [d]");
    }

    // An operator's predicate on a value expression, a parameter name or a packed element's decoding: a comparison on
    // the comparable column text, LIKE with no ESCAPE clause, or the 255-character prefix equality.
    private static void AppendPredicate(
        StringBuilder sql,
        ResolvedCondition condition,
        SearchOperator comparisonOperator,
        string value)
    {
        switch (comparisonOperator)
        {
            case SearchOperator.Like:
                sql.Append(condition.Reference).Append(" LIKE ").Append(value);
                return;

            case SearchOperator.PrefixEqual255:
                sql.Append("SUBSTRING(").Append(condition.Reference).Append(", 1, ").Append(PrefixLength)
                    .Append(") = SUBSTRING(").Append(value).Append(", 1, ").Append(PrefixLength).Append(')');
                return;

            default:
                var comparison = comparisonOperator switch
                {
                    SearchOperator.Equal => "=",
                    SearchOperator.NotEqual => "<>",
                    SearchOperator.Less => "<",
                    SearchOperator.LessOrEqual => "<=",
                    SearchOperator.Greater => ">",
                    SearchOperator.GreaterOrEqual => ">=",
                    _ => throw new ArgumentOutOfRangeException(nameof(comparisonOperator), UnknownOperatorMessage),
                };
                sql.Append(condition.Comparable).Append(' ').Append(comparison).Append(' ').Append(value);
                return;
        }
    }

    // Binds an alternative's value as the next positional parameter, typed for the condition's column.
    private static string Bind(ResolvedCondition condition, SearchAlternative alternative, PositionalParameters parameters) =>
        parameters.Add(name => ToScalarParameter(condition, alternative, name));

    // The scalar parameter of an alternative's value, typed for the condition's column.
    private static SqlParameterValue ToScalarParameter(
        ResolvedCondition condition,
        SearchAlternative alternative,
        string name)
    {
        var value = alternative.Value ?? throw new ArgumentException(MissingValueMessage, nameof(alternative));
        return ColumnValueConverter.ToSearchParameter(
            name, condition.Condition.Target, condition.Condition.FieldType, condition.Column, value);
    }

    // The alternatives the scalar path binds a parameter for: every one but IS NULL.
    private static long CountBindingAlternatives(IReadOnlyList<ResolvedCondition> conditions)
    {
        var count = 0L;
        foreach (var condition in conditions)
        {
            foreach (var alternative in condition.Condition.Alternatives)
            {
                if (alternative.Operator != SearchOperator.IsNull)
                {
                    count++;
                }
            }
        }

        return count;
    }

    // One packed group's list, and the decoding of one of its elements, which all share the group's type: the element
    // count, the offset of each token's start and then of the end of the last, every one PackedNumberWidth digits,
    // and then the tokens.
    private static string PackValues(IReadOnlyList<SqlParameterValue> values, out string decoder)
    {
        decoder = string.Empty;
        var decimalScale = DecimalScale(values);
        var header = new StringBuilder((values.Count + 2) * PackedNumberWidth);
        var tokens = new StringBuilder();
        header.Append(values.Count.ToString(PackedNumberFormat, CultureInfo.InvariantCulture))
            .Append(0.ToString(PackedNumberFormat, CultureInfo.InvariantCulture));
        foreach (var value in values)
        {
            decoder = WritePackedElement(tokens, value, decimalScale);
            header.Append(tokens.Length.ToString(PackedNumberFormat, CultureInfo.InvariantCulture));
        }

        return header.Append(tokens).ToString();
    }

    // The largest scale among a list's decimal values, the scale each of them is read back with; 0 when it has none.
    private static int DecimalScale(IReadOnlyList<SqlParameterValue> values)
    {
        var scale = 0;
        foreach (var value in values)
        {
            if (value.Value is decimal number)
            {
                scale = Math.Max(scale, number.Scale);
            }
        }

        return scale;
    }

    // The number of digits in a decimal's integer part, at least one.
    private static int IntegerDigits(decimal number)
    {
        var integer = decimal.Truncate(Math.Abs(number));
        return integer == 0m ? 1 : integer.ToString(CultureInfo.InvariantCulture).Length;
    }

    // The one mapping from a scalar search parameter to its packed form: appends the parameter's value as the next
    // element token and returns the expression that decodes such a token to the parameter's SQL type and value.
    // decimalScale is the scale a decimal element is read back with, the largest of its list. Every token written is
    // ASCII.
    private static string WritePackedElement(StringBuilder tokens, SqlParameterValue scalar, int decimalScale)
    {
        switch (scalar.DbType, scalar.Value)
        {
            case (DbType.String, string text):
                tokens.Append(ToUtf16LittleEndianHex(text));
                return PackedTextDecoder;

            case (DbType.Byte, byte number):
                tokens.Append(number.ToString(CultureInfo.InvariantCulture));
                return PackedByteDecoder;

            case (DbType.Int16, short number):
                tokens.Append(number.ToString(CultureInfo.InvariantCulture));
                return PackedInt16Decoder;

            case (DbType.Int32, int number):
                tokens.Append(number.ToString(CultureInfo.InvariantCulture));
                return PackedInt32Decoder;

            case (DbType.Int64, long number):
                tokens.Append(number.ToString(CultureInfo.InvariantCulture));
                return PackedInt64Decoder;

            // A decimal, an integer column's criterion that holds no exact 64-bit integer, is its invariant text read
            // back as decimal(38, s), s the largest scale of its list, which keeps every digit while its integer part
            // fits the other 38 - s digits; the converter's criteria have scale 1 and at most 19 integer digits.
            case (DbType.Decimal, decimal number) when IntegerDigits(number) <= MaxDecimalPrecision - decimalScale:
                tokens.Append(number.ToString(CultureInfo.InvariantCulture));
                return string.Concat(
                    "CAST([e].[t] AS decimal(",
                    MaxDecimalPrecision.ToString(CultureInfo.InvariantCulture),
                    ", ",
                    decimalScale.ToString(CultureInfo.InvariantCulture),
                    "))");

            case (DbType.Boolean, bool flag):
                tokens.Append(flag ? '1' : '0');
                return PackedBooleanDecoder;

            // Search binds dates at midnight; another time of day could round differently as text than as a
            // parameter, so it is refused rather than packed inexactly.
            case (DbType.DateTime, DateTime time) when time.TimeOfDay == TimeSpan.Zero:
                tokens.Append(time.ToString(PackedDateTimeFormat, CultureInfo.InvariantCulture));
                return PackedDateTimeDecoder;

            case (DbType.Double, double number):
                var bits = BitConverter.DoubleToInt64Bits(number);
                var exponent = (bits >> DoubleExponentShift) & DoubleExponentMask;
                if (exponent == NonFiniteExponent)
                {
                    throw new InvalidOperationException(UnpackableValueMessage);
                }

                tokens.Append(bits < 0 ? '1' : '0')
                    .Append(exponent.ToString(DoubleExponentFormat, CultureInfo.InvariantCulture))
                    .Append((bits & DoubleFractionMask).ToString(DoubleFractionFormat, CultureInfo.InvariantCulture));
                return PackedDoubleDecoder;

            case (DbType.String or DbType.Byte or DbType.Int16 or DbType.Int32 or DbType.Int64 or DbType.Decimal
                or DbType.Boolean or DbType.DateTime or DbType.Double, _):
                throw new InvalidOperationException(UnpackableValueMessage);

            default:
                throw new InvalidOperationException(UnpackableTypeMessage);
        }
    }

    // The uppercase hexadecimal of the text's UTF-16 code units, low byte first and with no "0x" prefix, the form
    // CONVERT style 2 reads as varbinary. The code units are copied, not encoded, so a lone surrogate is kept as it is.
    private static string ToUtf16LittleEndianHex(string text)
    {
        var bytes = new byte[text.Length * sizeof(char)];
        for (var index = 0; index < text.Length; index++)
        {
            bytes[2 * index] = (byte)(text[index] & 0xFF);
            bytes[(2 * index) + 1] = (byte)(text[index] >> 8);
        }

        return Convert.ToHexString(bytes);
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

    // The FROM clause and predicate of ListPage's filtered set: the base table LEFT JOINed to each page holding a grid
    // column, every one of them under the HOLDLOCK hint ListPage describes, the selected set, and the grid filter. The
    // filter is a case-insensitive "contains" over the grid columns that does not depend on the database collation:
    // each column is converted to text (a datetime as yyyy-mm-dd), and both sides are compared under one
    // case-insensitive collation, with the filter's pattern characters escaped by RecordQueryRules.ToContainsPattern.
    // It has no legacy counterpart.
    private static void AppendListSource(
        StringBuilder sql,
        FormSchema schema,
        IReadOnlyList<ProjectedColumn> columns,
        RecordListQuery query,
        PositionalParameters parameters)
    {
        sql.Append(" FROM ");
        AppendBaseTable(sql, schema);
        sql.Append(" WITH (HOLDLOCK)");

        var joined = new bool[schema.Pages.Count];
        foreach (var column in columns)
        {
            joined[RecordSqlBuilder.PageIndexOf(schema, column.Field.Page.PageId)] = true;
        }

        for (var index = 0; index < joined.Length; index++)
        {
            if (joined[index] && schema.Pages[index].Table is { } table)
            {
                AppendPageJoin(sql, schema, index, table, "LEFT JOIN", holdLock: true);
            }
        }

        var filtered = RecordSqlBuilder.AppendSelectedSetPredicate(
            sql, schema, query.IncludeDeleted, parameters, SelectedSetClause.Where);
        if (query.Filter is not { } filter)
        {
            return;
        }

        // Checked whatever the columns, so a filter over SQL Server's LIKE pattern limit always fails the same way.
        var containsPattern = RecordQueryRules.ToContainsPattern(filter);
        if (containsPattern.Length > RecordQueryRules.MaxLikePatternLength)
        {
            throw new ArgumentException(FilterTooLongMessage, nameof(query));
        }

        sql.Append(filtered ? " AND " : " WHERE ");
        if (columns.Count == 0)
        {
            // A filter with no column to search matches nothing.
            sql.Append("1 = 0");
            return;
        }

        // One parameter serves every column's term.
        var pattern = parameters.Add(n => ColumnValueConverter.Text(n, containsPattern));
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
                .Append(" ESCAPE '").Append(RecordQueryRules.ContainsPatternEscape).Append('\'');
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

    // <join> [page] p{i} [WITH (HOLDLOCK)] ON p{i}.[GlobalRecordId] = b.[GlobalRecordId]; only ListPage holds its page
    // tables, for the reason it describes, so Find's joins carry no hint.
    private static void AppendPageJoin(
        StringBuilder sql,
        FormSchema schema,
        int pageIndex,
        TableSchema table,
        string join,
        bool holdLock)
    {
        var alias = RecordSqlBuilder.PageAlias(pageIndex);
        sql.Append(' ').Append(join).Append(' ').Append(SqlIdentifier.Quote(table)).Append(' ').Append(alias);
        if (holdLock)
        {
            sql.Append(" WITH (HOLDLOCK)");
        }

        sql.Append(" ON ").Append(alias).Append('.').Append(SqlIdentifier.Quote(RecordSqlBuilder.RequirePageKey(table)))
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

    // The scalar parameters of one packed list: a condition's values that share an operator and a parameter type.
    private sealed record PackedGroup(SearchOperator Operator, DbType DbType, List<SqlParameterValue> Values);
}
