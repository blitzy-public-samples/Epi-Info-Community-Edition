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
/// rejection of a <c>[...]</c> set no SQL Server construct evaluates and the <c>*</c> to <c>%</c> rewrite. This class
/// renders each alternative exactly as given, however many a condition holds and however long its pattern; it reads a
/// value only to pack the values of a large search into parameters ("Packed binding" under <c>Search</c>), to compile
/// a <c>LIKE</c> pattern longer than SQL Server's pattern limit into a program ("Long patterns" under
/// <c>Search</c>), and to reject, as a programming error, a set longer than that limit that is unclosed or that no cut
/// keeping its members divides.
/// </para>
/// <para>
/// <b>Identifiers and values.</b> Every table and column name comes from the reader-built <see cref="FormSchema"/>
/// and reaches the text only through <see cref="SqlIdentifier"/>, by way of the <see cref="RecordSqlBuilder"/>
/// helpers. The CTE name <c>[n]</c>, Find's position column <c>[pos]</c>, the list's derived tables <c>[ct]</c> and
/// <c>[pg]</c> and its count column <c>[tot]</c>, the aliases <c>b</c>, <c>p{i}</c> and <c>[c{n}]</c>, the names a
/// packed list is decoded under (the derived tables <c>[d]</c>, <c>[a]</c> and <c>[e]</c>, the digit tables
/// <c>[r{k}]</c>, and their columns <c>[v]</c>, <c>[i]</c>, <c>[t]</c> and <c>[g]</c>), the names a long-pattern
/// program runs under (the recursive CTEs <c>[x{n}]</c>, their columns <c>[k]</c>, <c>[v]</c>, <c>[l]</c>,
/// <c>[z]</c> and <c>[st]</c>, and the aliases <c>[u]</c>, <c>[h]</c>, <c>[x]</c>, <c>[q]</c>, <c>[f]</c>,
/// <c>[g]</c>, <c>[o]</c> and <c>[y]</c> with their columns), the collation name, the escape character, the decoding
/// expressions of packed values and of programs, and the query hints, a program's <c>MAXRECURSION 0, MAXDOP 1</c>,
/// a packed statement's <c>USE HINT ('DISABLE_OPTIMIZER_ROWGOAL', 'FORCE_LEGACY_CARDINALITY_ESTIMATION')</c> and the
/// list's <c>FORCE ORDER, LOOP JOIN</c>, are fixed literals. Every value (criteria, packed, compiled or not, the grid
/// filter, the excluded id, the cap, the offset and the page size) is a positional parameter, so text that looks like
/// SQL stays inert data.
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
    /// The native type of the navigation position at <see cref="PositionOrdinal"/>: <c>bigint</c>, the type of
    /// <c>ROW_NUMBER()</c>.
    /// </summary>
    internal const DbType PositionType = DbType.Int64;

    /// <summary>
    /// The ordinal of a list row's page flag in a <see cref="ListSelect"/>, an <c>int</c>: 1 for a row of the requested
    /// page, 0 for the one row an empty page returns, which carries only the total.
    /// </summary>
    internal const int InPageOrdinal = 3;

    /// <summary>
    /// The native type of the page flag at <see cref="InPageOrdinal"/>: <c>int</c>, the type of a <c>CASE</c> whose
    /// results are the literals 0 and 1.
    /// </summary>
    internal const DbType InPageType = DbType.Int32;

    /// <summary>
    /// The ordinal of the total in a <see cref="SearchSelect"/> or a <see cref="ListSelect"/>, a <c>bigint</c> that
    /// every row carries: the number of matches, counted before the cap, in a search; the number of rows in the
    /// filtered set, whatever the page, in the list.
    /// </summary>
    internal const int TotalOrdinal = 4;

    /// <summary>
    /// The native type of the total at <see cref="TotalOrdinal"/>: <c>bigint</c>, the type of <c>COUNT_BIG(*)</c>.
    /// </summary>
    internal const DbType TotalType = DbType.Int64;

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
    // ten times slower to match. It holds every packed pattern, because a pattern longer than the native LIKE limit,
    // at most RecordQueryRules.MaxLikePatternLength, is never packed: its long-pattern program evaluates it instead.
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
        "USE HINT ('DISABLE_OPTIMIZER_ROWGOAL', 'FORCE_LEGACY_CARDINALITY_ESTIMATION')";

    // The query hints of a statement that runs a long-pattern program. A recursive CTE takes one level per instruction,
    // bisection, extension or seek a value runs, more than the default limit of 100 levels for a long value or a long
    // program, so MAXRECURSION 0 lifts the limit. A parallel plan re-runs the program across its workers, many times
    // slower than one worker ("Long filters" in ListPage's remarks gives the measurement), so MAXDOP 1 keeps the
    // program on one worker whatever the server's max degree of parallelism.
    private const string LongPatternHint = "MAXRECURSION 0, MAXDOP 1";

    // A long-pattern program is ASCII text, as a packed list is: the number of patterns, the number of records, one
    // record of LongPatternFieldCount fields, each PackedNumberWidth decimal digits, per entry, instruction and data
    // record, and then the texts of the records, one after another, as the hexadecimal of their UTF-16LE code units.
    // The fields are: the kind; the record to run after a success, plus one, 0 meaning the pattern matched (for an
    // entry, its pattern's first instruction); the record to retry from after a failure, plus one, 0 meaning the value
    // fails; the step's character count, first in UTF-16 code units, as a collation without supplementary-character
    // support counts, and then with each surrogate pair counted once, as an _SC collation counts; the number of data
    // records after the instruction, the pieces of its set or the cuts of its run; whether its set is negated; a cut's
    // length in code units; and the end of the record's text, in code units from the start of the text area.
    private const int LongPatternFieldCount = 9;
    private const int LongPatternHeaderWidth = 2 * PackedNumberWidth;
    private const int LongPatternRecordWidth = LongPatternFieldCount * PackedNumberWidth;
    private const int KindField = 0;
    private const int NextField = 1;
    private const int FailField = 2;
    private const int CountField = 3;
    private const int ItemsField = 5;
    private const int NegatedField = 6;
    private const int CutField = 7;
    private const int TextEndField = 8;

    // The record kinds of a long-pattern program, the [f].[t] its statement branches on: an entry, one per pattern,
    // which only names the pattern's first instruction; the nine instructions; and a data record, a piece of the set
    // or a cut of the run of the instruction before it, which is never run.
    private const int EntryRecord = 0;
    private const int WindowStep = 1;
    private const int RunStep = 2;
    private const int SeekStep = 3;
    private const int SeekRunStep = 4;
    private const int ScanStep = 5;
    private const int TailStep = 6;
    private const int EndStep = 7;
    private const int DataRecord = 8;
    private const int EndAnchorStep = 9;
    private const int BackWindowStep = 10;

    // A window is wrapped in a '%' on each side for PATINDEX, and followed by one for the bisection's LIKE, so it is
    // two less than the native limit; a tail window is preceded by one '%' only.
    private const int SeekWrapping = 2;
    private const int TailWrapping = 1;

    // The prefixes of a long literal run that a seek looks for: the longest that fits a seek window and the 32 before
    // it, each one text element shorter. A value character that a collation expands into several pattern characters,
    // such as Æ for AE, can straddle the end of one prefix, which then misses the match, but not the end of every one
    // of 33 consecutive prefixes unless it expands into more than 33 text elements. Measured, of the 11,557 BMP
    // characters whose compatibility decomposition has three or more code units, the 314 that
    // Latin1_General_100_CI_AS ranks equal to it span at most three text elements (U+FB03 for ffi).
    private const int RunCuts = 33;

    /// <summary>
    /// The code point ranges, each inclusive, whose weighted characters (<see cref="IsWeightedCharacter(char)"/>) a
    /// long grid filter's minimum length counts, in Latin to Latin Extended-B, Greek, Cyrillic, Hebrew, Arabic, Thai,
    /// Latin Extended Additional, hiragana and katakana, the CJK Unified Ideographs and the Hangul syllables: their
    /// letters, numbers, punctuation, symbols and space separators. Measured on SQL Server 2022, none of the 33,528
    /// such characters in them is ignorable under <c>Latin1_General_100_CI_AS</c>
    /// (<c>(N'a' + NCHAR(c) + N'b') = N'ab'</c> is false for each). The collation ignores 13 code points of the ranges,
    /// all of other categories: NUL (a control), the soft hyphen U+00AD (a format character), the marks U+0487 and
    /// U+0E4C, and 9 unassigned code points. Outside the ranges it ignores 3,003 BMP characters of the counted
    /// categories, such as U+0370 to U+0373, U+9FBC to U+9FFF, the rupee sign U+20B9 and the dashes U+2E3A and U+2E3B,
    /// which Unicode assigned after the collation's weights, and the Arabic tatweel U+0640. So the ranges are an
    /// allow-list of measured ones, never every character of those categories.
    /// </summary>
    internal static readonly IReadOnlyList<(char First, char Last)> WeightedCharacterRanges =
        Array.AsReadOnly<(char First, char Last)>(
        [
            ('\u0000', '\u024F'),
            ('\u0386', '\u03CE'),
            ('\u0400', '\u0513'),
            ('\u05D0', '\u05EA'),
            ('\u0621', '\u063A'),
            ('\u0641', '\u064A'),
            ('\u0E01', '\u0E5B'),
            ('\u1E00', '\u1E9B'),
            ('\u1EA0', '\u1EF9'),
            ('\u3041', '\u30FF'),
            ('\u4E00', '\u9FBB'),
            ('\uAC00', '\uD7A3'),
        ]);

    // The phases of a step, the [q].[p] of a row: run the step afresh, bisect for the first end its pattern fits,
    // extend that end to a character boundary or scan a run's equal ends, and apply a seek's hit.
    private const int FreshPhase = 0;
    private const int BisectPhase = 1;
    private const int ExtendPhase = 2;
    private const int HitPhase = 3;

    // In a long-pattern step: the rest of the value after the end [e] of the text matched so far, and the midpoint of
    // a bisection's bounds [t] and [h].
    private const string Rest = "SUBSTRING([x].[v], [q].[e] + 1, [f].[r])";
    private const string Midpoint = "([q].[t] + [q].[h]) / 2";

    // The plan hints the list statement ends with: the joins run in written order, each as nested loops, so the held
    // base rows, of the page or of the filtered set, are the outer input of every page join and each page row is read
    // only after its base row is held (ListPage).
    private const string ListPlanHints = "FORCE ORDER, LOOP JOIN";

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
    private const string SetTooLongMessage =
        "A search pattern holds a [...] set longer than SQL Server's LIKE pattern limit that is unclosed or cannot be"
        + " cut into shorter sets.";
    private const string LikeLimitMessage =
        "The native LIKE limit must be from 0 to SQL Server's LIKE pattern limit.";

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
    ///      [, [x{n}] ([k], [v], [l], [z], [st]) AS (anchor UNION ALL step) ...]  -- long patterns only
    /// SELECT TOP (@p1) b.[GlobalRecordId] AS [c0], b.[UniqueKey] AS [c1], b.[RecStatus] AS [c2],
    ///        b.[pos] AS [c3], COUNT_BIG(*) OVER () AS [c4], p0.[col] AS [c5], ...
    /// FROM [n] b INNER JOIN [page] p0 ON p0.[GlobalRecordId] = b.[GlobalRecordId] ...
    /// WHERE (alt OR alt ... [OR EXISTS (SELECT 1 FROM [x{n}] AS [y] WHERE [y].[k] = b.[GlobalRecordId]
    ///                                     AND CAST(SUBSTRING([y].[st], 1, 8) AS bigint) = -1)]) AND (alt OR ...)
    ///   [AND NOT (b.[GlobalRecordId] = @pX AND DATALENGTH(b.[GlobalRecordId]) = DATALENGTH(@pX))]
    /// ORDER BY b.[UniqueKey]
    /// [OPTION ([MAXRECURSION 0, MAXDOP 1][, ]
    ///          [USE HINT ('DISABLE_OPTIMIZER_ROWGOAL', 'FORCE_LEGACY_CARDINALITY_ESTIMATION')])]
    /// </code>
    /// <para>
    /// <c>MAXRECURSION 0, MAXDOP 1</c> is written only with a long-pattern program, which it lets recurse without a
    /// level limit on one worker ("Long patterns" below), and the <c>USE HINT</c> only in a packed statement.
    /// </para>
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
    /// holds its locks, so each sees the rows the others hold. Only the base table carries the hint, so each page
    /// row's shared lock is released once the row is read. While the statement holds a base row, its page rows cannot
    /// change through a write that locks the base row exclusively before any page row: an update of a base table with
    /// a last-save column, which stamps the base row first, a create, which inserts the base row first, or a soft
    /// delete, which writes only the base row. A page-only change can alter a joined page row during the statement: an
    /// update of a base table with neither last-save column, whose update lock on the base row is compatible with the
    /// held shared lock, or a writer outside the application. The statement fixes no join order, so a page row the plan
    /// reads before it holds that row's base row can also change before the base row is held.
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
    /// <b>Packed binding.</b> A search binds one parameter per alternative other than <c>IS NULL</c> and other than a
    /// long pattern, plus one per condition holding long patterns (below), while those are at most 2,000, so the
    /// statement, at most 2,003 parameters with the selected-set status, the cap and the excluded id, stays under the
    /// 2,098 a <c>sp_executesql</c> command leaves of SQL Server's 2,100. A search with more renders every condition
    /// packed instead, so its parameter count no longer grows with its alternatives; long patterns are never packed:
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
    /// <c>nvarchar(max)</c> parameter to, which holds every packed pattern, since none is a long pattern, and is then
    /// not converted again for each comparison; either keeps every code unit and, like a parameter, takes the database's
    /// coercible-default collation, so a column's collation governs the comparison as it does for a parameter;
    /// integers and bits as invariant decimal text, cast
    /// to their own type; a <c>decimal</c> as its invariant text, cast to <c>decimal(38, s)</c> with <c>s</c> the
    /// largest scale of its list; a date as <c>yyyy-MM-ddTHH:mm:ss.fff</c> read with <c>CONVERT</c> style 126, exact
    /// for the midnight values search binds; and a float as the sign, exponent and fraction of its IEEE 754 bits at
    /// fixed widths, rebuilt by exact arithmetic, because SQL Server's text-to-float parse is not correctly rounded.
    /// </para>
    /// <para>
    /// <b>Long patterns.</b> SQL Server refuses a <c>LIKE</c> or <c>PATINDEX</c> pattern of more than
    /// <see cref="RecordQueryRules.MaxLikePatternLength"/> UTF-16 code units with error 8152. A <c>LIKE</c> pattern
    /// within that native limit is rendered as above; a longer one, a long pattern, is compiled into a program that
    /// gives the same answer with no pattern over the limit. The pattern is read as atoms, from the elements of
    /// <see cref="LikePatternSyntax"/>: maximal literal runs, each <c>_</c>, each <c>[...]</c> set, and <c>%</c> runs,
    /// which split it into segments. A literal run is never cut between two steps, because SQL Server matches it by
    /// the collation's equality, under which one character can equal several (<c>Æ</c> and <c>AE</c>) or none (an
    /// ignorable character), while <c>_</c> and a set match one character and an expansion never straddles a literal
    /// and a wildcard. A step is a window of whole atoms of at most the native limit less two units, compared with
    /// <c>LIKE</c>; a literal run longer than that, compared with <c>=</c>, which has no limit, and an <c>x</c>
    /// appended to both sides, since <c>=</c> ignores trailing blanks; or a set longer than that, one character
    /// <c>LIKE</c> one of the pieces <see cref="LikePatternSyntax.TrySplitSet"/> cuts it into, or none for a negated
    /// set. Each step tracks where the text matched so far ends: it tries its own character count, else bisects for
    /// the shortest prefix of the rest that starts a match, extends it to a character boundary of the collation and
    /// checks it. The first segment is matched from the start of the value; each middle one from the leftmost
    /// candidate <c>PATINDEX</c> finds for its first atoms, a long literal run by the leftmost hit of 33 of its
    /// prefixes, and again after a candidate whose steps fail; the last by <c>LIKE '%' + window</c> when it fits,
    /// else backwards from the end of the value, or as a middle one followed by an end that only ignorable characters
    /// fill, as a pattern with no <c>%</c> ends. A set longer than the limit that is unclosed or that no cut keeping
    /// its members divides, which the search-criteria parser rejects with a 400, is rejected here.
    /// </para>
    /// <para>
    /// The patterns of one condition share one program, the ASCII parameter that <c>LongPatternFieldCount</c>
    /// describes, and one recursive CTE <c>[x{n}]</c>, <c>n</c> the condition's index. Its anchor reads every
    /// non-<c>NULL</c> value of the condition's column in the selected set, as <c>nvarchar(max)</c> under the column's
    /// collation, with its length in characters, <c>LEN(v + N'.') - 1</c>, and whether the collation counts a surrogate
    /// pair as one character; each recursion step runs one instruction for one value, decoding it from the parameter
    /// by its index, so neither the text nor the parameter count grows with the pattern. A value matches when one of
    /// its rows reaches state -1, which the condition's <c>EXISTS</c> term tests, and the statement carries
    /// <c>MAXRECURSION 0, MAXDOP 1</c>: the recursion can take more than the default 100 levels, and a parallel plan
    /// re-runs the program across its workers, many times slower than one worker ("Long filters" under
    /// <c>ListPage</c> gives the measurement), so the hint keeps the program on one worker whatever the server's max
    /// degree of parallelism. Every comparison, <c>LIKE</c>, <c>PATINDEX</c>, <c>=</c> or <c>&lt;</c>, runs under the
    /// column's collation, as the native predicate compares, so the program returns the rows of native <c>LIKE</c>,
    /// expansions and ignorable characters included. The seek of a long literal run finds a match while no character
    /// of the collation equals more than 33 text elements of the run, a bound recorded under "Parity gaps" in the
    /// parity notes.
    /// </para>
    /// <para>
    /// <b>Bounds.</b> A statement that would still bind more than 2,098 parameters, which only conditions beyond the
    /// parser's six fields can need, is rejected here before it is returned.
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
    /// condition's, or has no alternative; the conditions hold a <c>LIKE</c> pattern with a <c>[...]</c> set longer
    /// than <see cref="RecordQueryRules.MaxLikePatternLength"/>; a field or grid column has no column in the form's
    /// page tables; a value does not fit its column; or the statement would bind more than 2,098 parameters.
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
        int scalarParameterBudget) =>
        Search(
            schema,
            dataFields,
            gridColumns,
            conditions,
            includeDeleted,
            excludeGlobalRecordId,
            scalarParameterBudget,
            RecordQueryRules.MaxLikePatternLength);

    /// <summary>
    /// Builds the Find statement as the seven-argument overload does, with the length above which a <c>LIKE</c>
    /// pattern is a long pattern given explicitly, so tests can evaluate short patterns through the long-pattern
    /// program and compare it with native <c>LIKE</c>.
    /// </summary>
    /// <param name="schema">The form's catalogue view.</param>
    /// <param name="dataFields">The form's data fields, to resolve each page-field condition by name.</param>
    /// <param name="gridColumns">The grid columns to project.</param>
    /// <param name="conditions">The parsed conditions; AND across conditions, OR across one condition's alternatives.</param>
    /// <param name="includeDeleted">Whether the selected set holds every row rather than the active set.</param>
    /// <param name="excludeGlobalRecordId">The open record's id to leave out of the result, or <see langword="null"/>.</param>
    /// <param name="scalarParameterBudget">
    /// The most bound values, alternatives other than <c>IS NULL</c> or a long pattern plus one per condition holding
    /// long patterns, that bind one parameter each; 0 packs every search that binds a value.
    /// </param>
    /// <param name="nativeLikeLimit">
    /// The longest pattern, in UTF-16 code units, rendered as one <c>LIKE</c>, which also bounds a long pattern's
    /// windows and set pieces, two units less, and its last window, one less; from 0 to
    /// <see cref="RecordQueryRules.MaxLikePatternLength"/>, which the other overloads pass.
    /// </param>
    /// <returns>The statement and its result layout.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="schema"/>, <paramref name="dataFields"/>, <paramref name="gridColumns"/> or
    /// <paramref name="conditions"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">As for the six-argument overload.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="scalarParameterBudget"/> is negative, <paramref name="nativeLikeLimit"/> is outside 0 to
    /// <see cref="RecordQueryRules.MaxLikePatternLength"/>, or a target or an operator is not a defined member.
    /// </exception>
    /// <exception cref="InvalidOperationException">As for the six-argument overload.</exception>
    internal static SearchSelect Search(
        FormSchema schema,
        DataFieldSet dataFields,
        IReadOnlyList<DataFieldMetadata> gridColumns,
        IReadOnlyList<SearchCondition> conditions,
        bool includeDeleted,
        string? excludeGlobalRecordId,
        int scalarParameterBudget,
        int nativeLikeLimit)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(dataFields);
        ArgumentNullException.ThrowIfNull(gridColumns);
        ArgumentNullException.ThrowIfNull(conditions);
        ArgumentOutOfRangeException.ThrowIfNegative(scalarParameterBudget);
        CheckNativeLikeLimit(nativeLikeLimit);

        var uniqueKey = RecordSqlBuilder.Require(schema.UniqueKey);
        var recStatus = RecordSqlBuilder.Require(schema.RecStatus);
        var globalRecordId = RecordSqlBuilder.Require(schema.GlobalRecordId);

        // Every field and column is resolved before any text is written, so an unknown field fails with no SQL built,
        // and every long pattern is compiled before then too, so a set no cut keeping its members divides fails the
        // same way.
        var resolved = ResolveConditions(schema, dataFields, conditions);
        var columns = ResolveGridColumns(schema, gridColumns, SearchFirstColumnOrdinal);
        var programs = new LongPatternProgram?[resolved.Count];
        for (var index = 0; index < resolved.Count; index++)
        {
            programs[index] = CompileConditionPatterns(resolved[index], nativeLikeLimit);
        }

        // The numbered selected set is the statement's one base-table reference. It keeps the base system columns
        // under their catalogue names, so every b.[...] reference below reads the numbered row, and its HOLDLOCK keeps
        // that row's membership, position and status one committed state until the statement completes. A long-pattern
        // CTE reads the values it evaluates through [n] as well.
        var parameters = new PositionalParameters();
        var sql = new StringBuilder("WITH [n] AS (SELECT ");
        AppendBase(sql, globalRecordId).Append(", ");
        AppendBase(sql, uniqueKey).Append(", ");
        AppendBase(sql, recStatus).Append(", ROW_NUMBER() OVER (ORDER BY ");
        AppendBase(sql, uniqueKey).Append(") AS [pos] FROM ");
        AppendBaseTable(sql, schema);
        sql.Append(" WITH (HOLDLOCK)");
        RecordSqlBuilder.AppendSelectedSetPredicate(sql, schema, includeDeleted, parameters, SelectedSetClause.Where);
        sql.Append(')');

        var hasLongPatterns = false;
        for (var index = 0; index < resolved.Count; index++)
        {
            if (programs[index] is not { } program)
            {
                continue;
            }

            var condition = resolved[index];
            var source = new StringBuilder("[n] " + RecordSqlBuilder.BaseAlias);
            if (condition.PageIndex is { } pageIndex && schema.Pages[pageIndex].Table is { } pageTable)
            {
                AppendPageJoin(
                    source, pageIndex, pageTable, "INNER JOIN", holdLock: false, BaseReference(globalRecordId));
            }

            sql.Append(", ");
            AppendLongPatternProgram(
                sql,
                LongPatternName(index),
                BaseReference(globalRecordId),
                source.ToString(),
                [string.Concat("CONVERT(nvarchar(max), ", condition.Reference, ")")],
                parameters.Add(n => ColumnValueConverter.Text(n, program.Text)),
                program);
            hasLongPatterns = true;
        }

        sql.Append(" SELECT TOP (")
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
                AppendPageJoin(sql, index, table, "INNER JOIN", holdLock: false, BaseReference(globalRecordId));
            }
        }

        // Within the budget each alternative binds its own parameter; beyond it every condition is packed, so the
        // parameter count no longer grows with the alternatives. A condition's long patterns are one EXISTS on its
        // program's CTE, whichever way its other alternatives bind.
        var packed = CountBindingAlternatives(resolved, nativeLikeLimit) > scalarParameterBudget;
        var clause = " WHERE ";
        for (var conditionIndex = 0; conditionIndex < resolved.Count; conditionIndex++)
        {
            var condition = resolved[conditionIndex];
            sql.Append(clause).Append('(');
            var separator = string.Empty;
            if (packed)
            {
                if (AppendPackedAlternatives(sql, condition, parameters, nativeLikeLimit))
                {
                    separator = " OR ";
                }
            }
            else
            {
                foreach (var alternative in condition.Condition.Alternatives)
                {
                    if (IsLongPattern(alternative, nativeLikeLimit))
                    {
                        continue;
                    }

                    sql.Append(separator);
                    AppendAlternative(sql, condition, alternative, parameters);
                    separator = " OR ";
                }
            }

            if (programs[conditionIndex] is not null)
            {
                sql.Append(separator);
                AppendLongPatternMatch(sql, LongPatternName(conditionIndex), BaseReference(globalRecordId));
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
        AppendQueryHints(sql, hasLongPatterns, packed ? PackedPlanHints : null);

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
    /// <para>Without a grid filter the statement has this shape:</para>
    /// <code>
    /// WITH [n] AS (SELECT b.[GlobalRecordId] AS [c0], b.[UniqueKey] AS [c1], b.[RecStatus] AS [c2]
    ///              FROM [base] b WITH (HOLDLOCK) [WHERE b.[RecStatus] = @p0])
    /// SELECT [pg].[c0], [pg].[c1], [pg].[c2], CASE WHEN [pg].[c1] IS NULL THEN 0 ELSE 1 END AS [c3],
    ///        [ct].[tot] AS [c4], p0.[col] AS [c5], ...
    /// FROM (SELECT COUNT_BIG(*) AS [tot] FROM [n]) AS [ct]
    /// OUTER APPLY (SELECT [c0], [c1], [c2] FROM [n]
    ///              ORDER BY [c1] OFFSET @p1 ROWS FETCH NEXT @p2 ROWS ONLY) AS [pg]
    /// LEFT JOIN [page] p0 WITH (HOLDLOCK) ON p0.[GlobalRecordId] = [pg].[c0] ...
    /// ORDER BY [pg].[c1]
    /// OPTION (FORCE ORDER, LOOP JOIN)
    /// </code>
    /// <para>With one, it has this shape:</para>
    /// <code>
    /// WITH [[x0] ([k], [v], [l], [z], [st]) AS (anchor UNION ALL step), ]   -- long filter only
    ///      [n] AS (SELECT b.[GlobalRecordId] AS [c0], b.[UniqueKey] AS [c1], b.[RecStatus] AS [c2],
    ///                     p0.[col] AS [c5], ...
    ///              FROM [base] b WITH (HOLDLOCK)
    ///              LEFT JOIN [page] p0 WITH (HOLDLOCK) ON p0.[GlobalRecordId] = b.[GlobalRecordId] ...
    ///              WHERE [b.[RecStatus] = @p0 AND ](filter))
    /// SELECT [pg].[c0], [pg].[c1], [pg].[c2], CASE WHEN [pg].[c1] IS NULL THEN 0 ELSE 1 END AS [c3],
    ///        [ct].[tot] AS [c4], [pg].[c5], ...
    /// FROM (SELECT COUNT_BIG(*) AS [tot] FROM [n]) AS [ct]
    /// OUTER APPLY (SELECT [c0], [c1], [c2], [c5], ... FROM [n]
    ///              ORDER BY [c1] OFFSET @p2 ROWS FETCH NEXT @p3 ROWS ONLY) AS [pg]
    /// ORDER BY [pg].[c1]
    /// OPTION ([MAXRECURSION 0, MAXDOP 1, ]FORCE ORDER, LOOP JOIN)
    /// </code>
    /// <para>
    /// The parameters are bound in this order: the selected set's status, for the active set; the filter's, one
    /// contains pattern, or a long filter's program and the length below which a value cannot contain the filter; the
    /// offset; and the page size.
    /// </para>
    /// <para>
    /// <b>Page and total.</b> <c>[n]</c> is the filtered set. <c>[ct]</c> counts it, and <c>[pg]</c> is the page,
    /// <c>OFFSET ... FETCH</c> in <c>UniqueKey</c> order, with a <c>bigint</c> offset and an <c>int</c> page size, so
    /// the largest admitted page cannot wrap. The count is one row, and applying the page to it returns each page row
    /// with the total, flagged 1 at <see cref="InPageOrdinal"/>, or, for an empty page, one row of <c>NULL</c> page
    /// columns flagged 0 that carries the total alone: a valid page past the end has no item and the correct total, and
    /// an empty filtered set a total of 0. Without a filter, the pages are joined to the page's rows after
    /// <c>OFFSET ... FETCH</c>, so a page row is read only for a row the page lists, and a deep page costs what the
    /// first one costs in page-table reads; the <c>NULL</c> id of an empty page's one row joins no page row.
    /// </para>
    /// <para>
    /// <b>One consistent read.</b> The count and the page each read the filtered set, so this one statement evaluates
    /// <c>[n]</c> twice, and every table the statement reads carries the hint <c>WITH (HOLDLOCK)</c>. Under the
    /// database's locking <c>READ COMMITTED</c>, every shared and key-range lock either evaluation takes is held until
    /// the statement completes, so no row one of them read can change, and no row can be inserted among the base rows,
    /// before the other has read it too. Without a filter, <c>[n]</c> reads the base table alone: both evaluations read
    /// held base rows, so the page and the total describe one committed state of the selected set even under a
    /// concurrent soft delete, and each page row is read once, for a fetched row only, and held until the statement
    /// completes. With a filter, <c>[n]</c> also reads each page joined for a grid column, and both evaluations read
    /// held base and page rows: the page and the total describe one committed state even under a concurrent soft
    /// delete, a change to a filtered field, or a change to a page row alone, which a writer outside the application,
    /// or an application update of a base table with neither last-save column, makes without updating the base row.
    /// This is the one read that holds its page tables; the record reads and Find read each row once and hold only the
    /// base table, as <see cref="RecordSqlBuilder"/>'s class remarks describe.
    /// </para>
    /// <para>
    /// <b>Lock order.</b> A held page-row lock could deadlock with a write that holds that record's exclusive page-row
    /// lock while it waits for a base row the list holds. The statement therefore ends with
    /// <c>OPTION (FORCE ORDER, LOOP JOIN)</c>: the joins run in their written order, each as nested loops, so held
    /// base rows are the outer input of every page join, and the statement reads a page row only once it holds that
    /// row's base row. Without a filter, those are the rows of <c>[pg]</c>, which the set read from the held base
    /// table and whose ids drive the page seeks; with one, the base table of <c>[n]</c>, before each of its joins. Page
    /// tables key on <c>GlobalRecordId</c>, so each inner read is a seek that locks the base row's own page row or,
    /// where it has none, the key range that row would sit in. Every application write locks a record's base row
    /// before any of its page rows: an update takes its update lock on the base row, and updates the base row's
    /// last-save columns where the table has them, before its page rows; a create inserts the base row first; and a
    /// soft delete writes the base row alone. Its later reads of other rows take only shared locks. A write that waits
    /// for a base row the list holds therefore holds none of that record's page rows, and the hints remove the
    /// lock-order inversion between a held page row and its base row: the list and an application write never wait for
    /// each other through the rows of one record. An application write's page-row insert can still wait for a key range
    /// the list locked for another record, the residual case described next.
    /// </para>
    /// <para>
    /// <b>Residual deadlocks.</b> Two waits can still end in a deadlock (1205): a writer outside the application that
    /// changes a page row the list holds while it holds one the list waits for, and an application write that inserts
    /// a page row (a create, or an update that repairs a record's missing page row) whose key falls in the key range
    /// a page seek of the list locked for a record with no row on that page, while the list waits for a row that
    /// write holds. The list can also be chosen as the victim of a deadlock between writers whose base-row locks it
    /// waits behind, a wait its held base-table read alone already allows. SQL Server ends one statement as the victim,
    /// which the store reports as unavailable: the list answers 503, and an application write 503 with nothing
    /// written. None of them returns a page and a total from two different states.
    /// </para>
    /// <para>
    /// <b>Long filters.</b> A grid filter whose contains pattern is longer than SQL Server's <c>LIKE</c> pattern limit
    /// is matched by the long-pattern program <c>[x0]</c>, which leads the statement. Its anchor reads the base table
    /// and the same page tables as <c>[n]</c>, with the same joins and the same <c>HOLDLOCK</c> hints, under the same
    /// plan hints, so its reads belong to the one consistent read and follow the same lock order; <c>[n]</c> keeps a
    /// row whose value the program matched (<c>EXISTS</c> on <c>[x0]</c>), and the statement's hints start with
    /// <c>MAXRECURSION 0, MAXDOP 1</c>. The anchor admits only a value of at least <c>ceil(n / 33)</c> characters,
    /// <c>n</c> the number of the filter's text elements that hold a weighted character
    /// (<see cref="IsWeightedCharacter(char)"/>): a letter, number, punctuation mark, symbol or space separator of
    /// <see cref="WeightedCharacterRanges"/>, a <c>bigint</c> parameter bound after the program. Measured, none of
    /// those characters is ignorable under <c>Latin1_General_100_CI_AS</c>, while characters of the same categories
    /// outside the ranges can be, and no character of the collation ranks equal to more than 33 text elements of a
    /// literal run, the bound the program's seek already relies on, so a shorter value cannot contain the filter and is
    /// never run through the program. A space counts too: the program compares a run with <c>=</c> after appending
    /// <c>'x'</c> to both sides, and the native <c>LIKE</c> keeps a pattern's trailing blanks, so a value containing
    /// the filter holds its spaces. A filter with no such element, such as one of supplementary characters only, admits
    /// every value. The filter's text reaches the statement only through those two parameters. A parallel plan re-runs
    /// the program across its workers: measured on SQL Server 2022 at its default parallelism settings, a list
    /// statement whose long filter ran the program at 56 workers took 9 to 25 seconds and hundreds of CPU-seconds, or
    /// ran past the 30-second default command timeout, where the serial plan took about 3 seconds at most, so
    /// <c>MAXDOP 1</c> keeps the program on one worker whatever the server's max degree of parallelism.
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
    /// <paramref name="gridColumns"/> holds a null entry or a field with no column in the form's page tables.
    /// </exception>
    /// <exception cref="InvalidOperationException">A table lacks a required system or key column.</exception>
    internal static ListSelect ListPage(
        FormSchema schema,
        IReadOnlyList<DataFieldMetadata> gridColumns,
        RecordListQuery query) =>
        ListPage(schema, gridColumns, query, RecordQueryRules.MaxLikePatternLength);

    /// <summary>
    /// Builds one page of the records list as the three-argument overload does, with the length above which the grid
    /// filter's contains pattern is a long pattern given explicitly, so tests can evaluate a short filter through the
    /// long-pattern program.
    /// </summary>
    /// <param name="schema">The form's catalogue view.</param>
    /// <param name="gridColumns">The grid columns to project.</param>
    /// <param name="query">The coerced list query.</param>
    /// <param name="nativeLikeLimit">
    /// The longest contains pattern, in UTF-16 code units, rendered as one <c>LIKE</c>; a longer one runs the
    /// long-pattern program; from 0 to <see cref="RecordQueryRules.MaxLikePatternLength"/>, which the other overload
    /// passes.
    /// </param>
    /// <returns>The statement and its result layout.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The query's paging bounds were not validated, or <paramref name="nativeLikeLimit"/> is outside 0 to
    /// <see cref="RecordQueryRules.MaxLikePatternLength"/>.
    /// </exception>
    /// <exception cref="ArgumentException">As for the three-argument overload.</exception>
    /// <exception cref="InvalidOperationException">As for the three-argument overload.</exception>
    internal static ListSelect ListPage(
        FormSchema schema,
        IReadOnlyList<DataFieldMetadata> gridColumns,
        RecordListQuery query,
        int nativeLikeLimit)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(gridColumns);
        ArgumentNullException.ThrowIfNull(query);
        CheckNativeLikeLimit(nativeLikeLimit);

        var offset = query.Offset;
        var uniqueKey = RecordSqlBuilder.Require(schema.UniqueKey);
        var columns = ResolveGridColumns(schema, gridColumns, ListFirstColumnOrdinal);

        // The filtered set is the statement's one source of base rows, read by the count and by the page under HOLDLOCK
        // and, through the plan hints at the end, before any page row. Its source is built first, so a long filter's
        // CTE, which reads the same tables under the same hints, can lead the statement; it allocates the parameters it
        // binds before the paging ones, in the same order whether or not the filter is long.
        var parameters = new PositionalParameters();
        var source = new StringBuilder();
        var program = AppendListSource(source, schema, columns, query, parameters, nativeLikeLimit);

        // A filter reads the grid columns, so its set joins their pages and carries them to the page. Without one the
        // set reads the base table alone, and the pages are joined after OFFSET ... FETCH, to the fetched rows only.
        var joinsAfterPage = query.Filter is null;
        IReadOnlyList<ProjectedColumn> setProjections = joinsAfterPage ? [] : columns;

        var sql = new StringBuilder("WITH ");
        if (program is not null)
        {
            sql.Append(program).Append(", ");
        }

        sql.Append(ListSetName).Append(" AS (SELECT ");
        AppendBase(sql, RecordSqlBuilder.Require(schema.GlobalRecordId))
            .Append(" AS ").Append(RecordSqlBuilder.ColumnAlias(IdOrdinal)).Append(", ");
        AppendBase(sql, uniqueKey).Append(" AS ").Append(RecordSqlBuilder.ColumnAlias(UniqueKeyOrdinal)).Append(", ");
        AppendBase(sql, RecordSqlBuilder.Require(schema.RecStatus))
            .Append(" AS ").Append(RecordSqlBuilder.ColumnAlias(RecStatusOrdinal));
        AppendProjections(sql, schema, setProjections);
        sql.Append(source);

        // The page's columns as the filtered set names them.
        var setColumns = new StringBuilder();
        foreach (var ordinal in new[] { IdOrdinal, UniqueKeyOrdinal, RecStatusOrdinal }
                     .Concat(setProjections.Select(column => column.Ordinal)))
        {
            setColumns.Append(setColumns.Length == 0 ? string.Empty : ", ")
                .Append(RecordSqlBuilder.ColumnAlias(ordinal));
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
        if (joinsAfterPage)
        {
            // Read from the pages joined below, under the aliases the filtered set would give them.
            AppendProjections(sql, schema, columns);
        }
        else
        {
            foreach (var column in columns)
            {
                sql.Append(", ").Append(ListPageName).Append('.').Append(RecordSqlBuilder.ColumnAlias(column.Ordinal));
            }
        }

        // The offset is a bigint and the page size an int, bound after the filter's parameters.
        sql.Append(" FROM (SELECT COUNT_BIG(*) AS ").Append(ListTotalColumn).Append(" FROM ").Append(ListSetName)
            .Append(") AS ").Append(ListTotalName)
            .Append(" OUTER APPLY (SELECT ").Append(setColumns).Append(" FROM ").Append(ListSetName)
            .Append(" ORDER BY ").Append(RecordSqlBuilder.ColumnAlias(UniqueKeyOrdinal))
            .Append(" OFFSET ").Append(parameters.Add(n => ColumnValueConverter.Int64(n, offset)))
            .Append(" ROWS FETCH NEXT ").Append(parameters.Add(n => ColumnValueConverter.Int32(n, query.PageSize)))
            .Append(" ROWS ONLY) AS ").Append(ListPageName);
        if (joinsAfterPage)
        {
            // Each page row is sought by the id of a fetched row, a base row the set holds; the NULL id of an empty
            // page's one row finds none.
            AppendGridPageJoins(
                sql,
                schema,
                columns,
                string.Concat(ListPageName, ".", RecordSqlBuilder.ColumnAlias(IdOrdinal)));
        }

        sql.Append(" ORDER BY ").Append(pageKey);
        AppendQueryHints(sql, program is not null, ListPlanHints);

        return new ListSelect(parameters.ToStatement(sql.ToString()), columns);
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
                    resolved.Add(new ResolvedCondition(
                        condition,
                        reference,
                        comparable,
                        column.Column.Metadata,
                        RecordSqlBuilder.PageIndexOf(schema, field.Page.PageId)));
                    break;

                case SearchTarget.BaseGlobalRecordId:
                    var idColumn = RecordSqlBuilder.Require(schema.GlobalRecordId);
                    var idReference = BaseReference(idColumn);
                    resolved.Add(new ResolvedCondition(condition, idReference, idReference, idColumn.Metadata, null));
                    break;

                case SearchTarget.BaseRecStatus:
                    var statusColumn = RecordSqlBuilder.Require(schema.RecStatus);
                    var statusReference = BaseReference(statusColumn);
                    resolved.Add(new ResolvedCondition(
                        condition, statusReference, statusReference, statusColumn.Metadata, null));
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
    // parameter type in the order the alternatives first use them, an EXISTS over the packed list of their values. Its
    // long patterns are left to its program. Returns whether it wrote a term.
    private static bool AppendPackedAlternatives(
        StringBuilder sql,
        ResolvedCondition condition,
        PositionalParameters parameters,
        int nativeLikeLimit)
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

            if (IsLongPattern(alternative, nativeLikeLimit))
            {
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

        return separator.Length > 0;
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

        sql.Append("(SELECT TOP (").Append(PackedFenceRows).Append(") ").Append(decoder)
            .Append(" AS [v] FROM ");
        AppendDigitIndex(sql, count);
        sql.Append(" CROSS APPLY (VALUES (CASE WHEN [a].[i] < ").Append(elementCount)
            .Append(" THEN SUBSTRING(").Append(name).Append(", ").Append(afterHeader).Append(" + ").Append(width)
            .Append(" * ").Append(elementCount).Append(" + ").Append(tokenStart).Append(", ").Append(tokenEnd)
            .Append(" - ").Append(tokenStart).Append(") END)) AS [e]([t]) WHERE [a].[i] < ").Append(elementCount)
            .Append(" ORDER BY [v]) AS [d]");
    }

    // The derived table [a] that numbers the indexes 0 to 10^k - 1 in [a].[i], with the fewest k digit tables whose
    // product reaches count:
    //   (SELECT [r0].[g] + 10 * [r1].[g] + ... AS [i] FROM digits AS [r0]([g]) CROSS JOIN ...) AS [a]
    // The caller keeps the indexes below its count, which it reads from its parameter, so the text depends on the
    // count only through its number of digits.
    private static void AppendDigitIndex(StringBuilder sql, long count)
    {
        // A .NET list's count needs at most ten digit tables, and a list long enough to need a tenth would have a
        // header longer than any string, so the index sum stays within int.
        var tables = 1;
        for (var reach = (long)PackedDigitBase; reach < count; reach *= PackedDigitBase)
        {
            tables++;
        }

        sql.Append("(SELECT ");
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

        sql.Append(") AS [a]");
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

    // The parameters the scalar path binds for the conditions' values: one per alternative but IS NULL and the long
    // patterns, and one program per condition that holds long patterns.
    private static long CountBindingAlternatives(IReadOnlyList<ResolvedCondition> conditions, int nativeLikeLimit)
    {
        var count = 0L;
        foreach (var condition in conditions)
        {
            var hasLongPatterns = false;
            foreach (var alternative in condition.Condition.Alternatives)
            {
                if (IsLongPattern(alternative, nativeLikeLimit))
                {
                    hasLongPatterns = true;
                }
                else if (alternative.Operator != SearchOperator.IsNull)
                {
                    count++;
                }
            }

            if (hasLongPatterns)
            {
                count++;
            }
        }

        return count;
    }

    // Whether an alternative is a long pattern: a LIKE whose text is longer than the native limit, which its
    // condition's program evaluates instead of LIKE.
    private static bool IsLongPattern(SearchAlternative alternative, int nativeLikeLimit) =>
        alternative is { Operator: SearchOperator.Like, Value: { } pattern }
        && pattern.TryGetText(out var text)
        && text.Length > nativeLikeLimit;

    private static void CheckNativeLikeLimit(int nativeLikeLimit)
    {
        if (nativeLikeLimit < 0 || nativeLikeLimit > RecordQueryRules.MaxLikePatternLength)
        {
            throw new ArgumentOutOfRangeException(nameof(nativeLikeLimit), LikeLimitMessage);
        }
    }

    // The fixed name of the program CTE of the condition, or of the grid filter, at index.
    private static string LongPatternName(int index) =>
        string.Concat("[x", index.ToString(CultureInfo.InvariantCulture), "]");

    // The program of a condition's long patterns, in their order, or null when it holds none. Each value is first built
    // as the scalar parameter the native path would bind, so it is checked the same way.
    private static LongPatternProgram? CompileConditionPatterns(ResolvedCondition condition, int nativeLikeLimit)
    {
        List<List<LikeRecord>>? patterns = null;
        foreach (var alternative in condition.Condition.Alternatives)
        {
            if (IsLongPattern(alternative, nativeLikeLimit))
            {
                var scalar = ToScalarParameter(condition, alternative, PackedValueName);
                (patterns ??= []).Add(CompileLongPattern((string)scalar.Value, nativeLikeLimit));
            }
        }

        return patterns is null ? null : AssembleProgram(patterns);
    }

    // The program of the grid filter past the native limit: the filter as one literal run between two runs of '%',
    // a middle segment of one run step, found by a seek for its prefixes, or by a scan where none fits PATINDEX.
    // The run is compared with '=', which reads no pattern character, and its prefixes are written with %, _ and [ as
    // the one-member sets [%], [_] and [[], since PATINDEX takes no ESCAPE clause, so it matches what the contains
    // pattern with its ESCAPE clause matches. The list runs it only on values at least ContainsMinimumLength(filter)
    // characters long.
    private static LongPatternProgram CompileContainsProgram(string filter)
    {
        var records = new List<LikeRecord>();
        var seek = AppendRunSeek(records, string.Empty, filter);
        records.Add(new LikeRecord(RunStep, 0, seek, LikeMeasure.Of(filter), 0, false, 0, filter));
        LinkInstructions(records);
        return AssembleProgram([records]);
    }

    // The fewest characters a value must have to contain the grid filter under Latin1_General_100_CI_AS:
    // ceil(n / RunCuts), n the number of the filter's text elements that hold a weighted character
    // (IsWeightedCharacter), a letter, number, punctuation mark, symbol or space separator of WeightedCharacterRanges.
    // An element counts once however many of them it holds, as a Thai consonant does with the SARA AM (U+0E33), a
    // letter that joins its element. None of those characters is ignorable under that collation, so the text a value
    // matches the filter with covers each of those n elements with at least one of its characters: a space too, since
    // the run is compared with '=' after an 'x' is appended to both sides, so trailing blanks are not padded away, as
    // the native LIKE keeps a pattern's trailing blanks. No character of the collation ranks equal to more than
    // RunCuts text elements of a literal run, the bound the run's seek relies on (RunCuts), so that text, and the
    // value, hold at least ceil(n / RunCuts) characters. Any other text element, which the collation may ignore, such
    // as NUL, the soft hyphen, a lone combining mark or a supplementary character, counts for nothing, so a filter of
    // none gives 0.
    private static long ContainsMinimumLength(string filter)
    {
        var elements = 0L;
        var index = 0;
        while (index < filter.Length)
        {
            var end = index + StringInfo.GetNextTextElementLength(filter, index);
            for (var at = index; at < end; at++)
            {
                if (IsWeightedCharacter(filter[at]))
                {
                    elements++;
                    break;
                }
            }

            index = end;
        }

        return (elements + RunCuts - 1) / RunCuts;
    }

    /// <summary>
    /// Whether a long grid filter's minimum length counts the character: one in <see cref="WeightedCharacterRanges"/>
    /// whose Unicode category (<see cref="char.GetUnicodeCategory(char)"/>) is a letter (Lu, Ll, Lt, Lm, Lo), a number
    /// (Nd, Nl, No), punctuation (Pc, Pd, Ps, Pe, Pi, Pf, Po), a symbol (Sm, Sc, Sk, So) or a space separator (Zs).
    /// Controls (NUL among them), format characters (the soft hyphen among them), marks, surrogates, private-use and
    /// unassigned code points are never counted, nor is any character outside the ranges: the ignorable code points
    /// the ranges' summary lists are of those categories or outside the ranges.
    /// </summary>
    /// <param name="character">The UTF-16 code unit.</param>
    /// <returns><see langword="true"/> when the character is counted.</returns>
    internal static bool IsWeightedCharacter(char character)
    {
        if (char.GetUnicodeCategory(character) is not (UnicodeCategory.UppercaseLetter
            or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter
            or UnicodeCategory.OtherLetter or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber
            or UnicodeCategory.OtherNumber or UnicodeCategory.ConnectorPunctuation or UnicodeCategory.DashPunctuation
            or UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation
            or UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation
            or UnicodeCategory.OtherPunctuation or UnicodeCategory.MathSymbol or UnicodeCategory.CurrencySymbol
            or UnicodeCategory.ModifierSymbol or UnicodeCategory.OtherSymbol or UnicodeCategory.SpaceSeparator))
        {
            return false;
        }

        for (var index = 0; index < WeightedCharacterRanges.Count; index++)
        {
            var (first, last) = WeightedCharacterRanges[index];
            if (character >= first && character <= last)
            {
                return true;
            }
        }

        return false;
    }

    // The program of the patterns: one entry record per pattern, at the pattern's index, naming its first instruction,
    // then each pattern's records in order, every Next and Fail made an index into the whole program. As ASCII text: the
    // pattern count and the record count, then one record of LongPatternFieldCount fields per record, each
    // PackedNumberWidth digits, then the hexadecimal of the records' texts.
    private static LongPatternProgram AssembleProgram(List<List<LikeRecord>> patterns)
    {
        var entries = new List<LikeRecord>(patterns.Count);
        var records = new List<LikeRecord>();
        foreach (var pattern in patterns)
        {
            var first = patterns.Count + records.Count;
            entries.Add(new LikeRecord(EntryRecord, pattern.Count > 0 ? first : -1, -1, default, 0, false, 0, string.Empty));
            foreach (var record in pattern)
            {
                records.Add(record with
                {
                    Next = record.Next < 0 ? record.Next : record.Next + first,
                    Fail = record.Fail < 0 ? record.Fail : record.Fail + first,
                });
            }
        }

        var all = entries.Concat(records).ToArray();
        var text = new StringBuilder(LongPatternHeaderWidth + (all.Length * LongPatternRecordWidth));
        var texts = new StringBuilder();
        AppendNumber(text, patterns.Count);
        AppendNumber(text, all.Length);
        var maxItems = 0;
        foreach (var record in all)
        {
            texts.Append(record.Text);
            maxItems = Math.Max(maxItems, record.Items);
            AppendNumber(text, record.Kind);
            AppendNumber(text, record.Next + 1L);
            AppendNumber(text, record.Fail + 1L);
            AppendNumber(text, record.Count.CodeUnits);
            AppendNumber(text, record.Count.Characters);
            AppendNumber(text, record.Items);
            AppendNumber(text, record.Negated ? 1 : 0);
            AppendNumber(text, record.Cut);
            AppendNumber(text, texts.Length);
        }

        text.Append(ToUtf16LittleEndianHex(texts.ToString()));
        return new LongPatternProgram(text.ToString(), patterns.Count, maxItems);
    }

    private static void AppendNumber(StringBuilder text, long number) =>
        text.Append(number.ToString(PackedNumberFormat, CultureInfo.InvariantCulture));

    // One pattern's records, whose Next and Fail are indexes into them; Next -1 means the pattern matched and Fail -1
    // that the value fails. The pattern's atoms, each a whole literal run, '_' or set, split at each run of '%' into
    // segments, a run of '%' giving empty segments that are dropped between the first and the last:
    //   - with no '%', the one segment runs from the start of the value, and the rest of the value must be empty or
    //     ignorable (an end step);
    //   - otherwise the first segment runs from the start; each middle segment, in order, is placed at its leftmost
    //     match after the previous one, a seek for its candidates followed by its steps, a failed step retrying the
    //     seek after the candidate; and the last is one tail window matched against the whole rest with LIKE '%' +
    //     window where it fits; or else, when its every literal run fits a window, its steps matched backwards from
    //     the end of the value, each at its latest start, within the rest; or else it is placed as a middle one and
    //     followed by an end step.
    // A literal run is never cut between two steps, because a collation expansion such as Æ for AE never straddles a
    // literal and a wildcard but can straddle a cut in a literal run. Leftmost placement is exact, because the matches
    // a step can make at one position differ only by characters the collation ignores, and latest-start placement
    // backwards from the end is exact for the same reason; a failed middle step retries its seek, while a failed
    // backward step fails the value, since a later placement of the segments before it only shortens the rest.
    private static List<LikeRecord> CompileLongPattern(string pattern, int nativeLikeLimit)
    {
        var segments = Segments(pattern);
        var records = new List<LikeRecord>();
        if (segments.Count == 1)
        {
            AppendChain(records, pattern, segments[0], -1, nativeLikeLimit);
            records.Add(new LikeRecord(EndStep, 0, -1, default, 0, false, 0, string.Empty));
            LinkInstructions(records);
            return records;
        }

        AppendChain(records, pattern, segments[0], -1, nativeLikeLimit);
        foreach (var segment in segments.Skip(1).Take(segments.Count - 2).Where(segment => segment.Count > 0))
        {
            var seek = AppendSeek(records, pattern, segment);
            AppendChain(records, pattern, segment, seek, nativeLikeLimit);
        }

        var tail = segments[^1];
        if (tail.Count > 0)
        {
            var tailText = Text(pattern, tail[0], tail[^1]);
            var limit = nativeLikeLimit - SeekWrapping;
            if (tailText.Length <= nativeLikeLimit - TailWrapping)
            {
                records.Add(new LikeRecord(TailStep, 0, -1, default, 0, false, 0, tailText));
            }
            else if (tail.All(atom => atom.Kind != LikeAtomKind.Run || FitsWindow(pattern, atom, limit)))
            {
                records.Add(new LikeRecord(EndAnchorStep, 0, -1, default, 0, false, 0, string.Empty));
                AppendBackChain(records, pattern, tail, nativeLikeLimit);
            }
            else
            {
                var seek = AppendSeek(records, pattern, tail);
                AppendChain(records, pattern, tail, seek, nativeLikeLimit);
                records.Add(new LikeRecord(EndStep, 0, seek, default, 0, false, 0, string.Empty));
            }
        }

        LinkInstructions(records);
        return records;
    }

    // Sets each instruction's Next to the instruction after it, past the data records that follow it, and the last
    // one's to -1, the pattern matched.
    private static void LinkInstructions(List<LikeRecord> records)
    {
        var next = -1;
        for (var index = records.Count - 1; index >= 0; index--)
        {
            if (records[index].Kind != DataRecord)
            {
                records[index] = records[index] with { Next = next };
                next = index;
            }
        }
    }

    // The pattern's atoms, split at each run of '%' into segments: consecutive literal characters make one run, and a
    // '_' or a set is an atom of its own that matches one character.
    private static List<List<LikeAtom>> Segments(string pattern)
    {
        var segments = new List<List<LikeAtom>> { new() };
        foreach (var element in LikePatternSyntax.Tokenize(pattern))
        {
            var segment = segments[^1];
            switch (element.Kind)
            {
                case LikePatternElementKind.AnyRun:
                    segments.Add([]);
                    break;

                case LikePatternElementKind.Literal when segment.Count > 0 && segment[^1].Kind == LikeAtomKind.Run:
                    var run = segment[^1];
                    segment[^1] = run with
                    {
                        Length = run.Length + element.Length,
                        Count = run.Count + LikeMeasure.Of(element),
                    };
                    break;

                case LikePatternElementKind.Literal:
                    segment.Add(new LikeAtom(LikeAtomKind.Run, element.Start, element.Length, LikeMeasure.Of(element)));
                    break;

                case LikePatternElementKind.AnyCharacter:
                    segment.Add(new LikeAtom(LikeAtomKind.AnyCharacter, element.Start, element.Length, LikeMeasure.One));
                    break;

                default:
                    segment.Add(new LikeAtom(LikeAtomKind.Set, element.Start, element.Length, LikeMeasure.One));
                    break;
            }
        }

        return segments;
    }

    // The steps of a segment, appended in order, each failing to fail: windows of consecutive whole atoms whose text
    // fits the window limit, two less than the native limit; a literal run longer than that, as a run step compared
    // with '='; and a set longer than that, or unclosed, whose meaning a '%' appended to it would change, as a
    // one-character window followed by its pieces. A '_' always makes a window, alone where none fits.
    private static void AppendChain(
        List<LikeRecord> records,
        string pattern,
        List<LikeAtom> atoms,
        int fail,
        int nativeLikeLimit)
    {
        var limit = nativeLikeLimit - SeekWrapping;
        var index = 0;
        while (index < atoms.Count)
        {
            var atom = atoms[index];
            if (!FitsWindow(pattern, atom, limit))
            {
                switch (atom.Kind)
                {
                    case LikeAtomKind.Run:
                        records.Add(new LikeRecord(
                            RunStep, 0, fail, atom.Count, 0, false, 0, Text(pattern, atom, atom)));
                        break;

                    case LikeAtomKind.Set:
                        AppendCharacterSet(records, pattern, atom, WindowStep, fail, nativeLikeLimit);
                        break;

                    default:
                        records.Add(new LikeRecord(WindowStep, 0, fail, atom.Count, 0, false, 0, "_"));
                        break;
                }

                index++;
                continue;
            }

            var end = WindowEnd(pattern, atoms, index, limit);
            records.Add(new LikeRecord(
                WindowStep,
                0,
                fail,
                Measure(atoms, index, end),
                0,
                false,
                0,
                Text(pattern, atoms[index], atoms[end - 1])));
            index = end;
        }
    }

    // The steps of a tail whose every literal run fits a window, appended in the order they run, from its last atom
    // back to its first, each failing the value: windows of consecutive whole atoms that fit the window limit, taken
    // from the right, and a set too long for a window, or unclosed, or a '_' where no window fits, as a one-character
    // back window, a set's followed by its pieces.
    private static void AppendBackChain(
        List<LikeRecord> records,
        string pattern,
        List<LikeAtom> atoms,
        int nativeLikeLimit)
    {
        var limit = nativeLikeLimit - SeekWrapping;
        var end = atoms.Count;
        while (end > 0)
        {
            var atom = atoms[end - 1];
            if (!FitsWindow(pattern, atom, limit))
            {
                if (atom.Kind == LikeAtomKind.Set)
                {
                    AppendCharacterSet(records, pattern, atom, BackWindowStep, -1, nativeLikeLimit);
                }
                else
                {
                    records.Add(new LikeRecord(BackWindowStep, 0, -1, atom.Count, 0, false, 0, "_"));
                }

                end--;
                continue;
            }

            var start = end - 1;
            var length = atom.Length;
            while (start > 0
                   && FitsWindow(pattern, atoms[start - 1], limit)
                   && length + atoms[start - 1].Length <= limit)
            {
                start--;
                length += atoms[start].Length;
            }

            records.Add(new LikeRecord(
                BackWindowStep,
                0,
                -1,
                Measure(atoms, start, end),
                0,
                false,
                0,
                Text(pattern, atoms[start], atoms[end - 1])));
            end = start;
        }
    }

    // The character count of the atoms from first to end, exclusive.
    private static LikeMeasure Measure(List<LikeAtom> atoms, int first, int end) =>
        atoms.Skip(first).Take(end - first).Aggregate(default(LikeMeasure), (sum, item) => sum + item.Count);

    // The end, exclusive, of the window that starts at the atom at first, which fits the limit: it takes each next
    // atom that fits a window while the window's text stays within the limit.
    private static int WindowEnd(string pattern, List<LikeAtom> atoms, int first, int limit)
    {
        var end = first + 1;
        var length = atoms[first].Length;
        while (end < atoms.Count && FitsWindow(pattern, atoms[end], limit) && length + atoms[end].Length <= limit)
        {
            length += atoms[end].Length;
            end++;
        }

        return end;
    }

    // Whether the atom can be part of a window of the limit: its text fits, and it is not an unclosed set, which runs
    // to the end of the pattern and whose meaning a '%' after it would change.
    private static bool FitsWindow(string pattern, LikeAtom atom, int limit) =>
        atom.Length <= limit && !(atom.Kind == LikeAtomKind.Set && !IsClosedSet(pattern, atom));

    private static bool IsClosedSet(string pattern, LikeAtom atom) =>
        atom.Length >= 2 && pattern[atom.Start + atom.Length - 1] == ']';

    // A set too long for a window, or unclosed: a window '_' of the kind given, forward or back, which finds the one
    // character, followed by data records holding the pieces it must be in, or for a negated set be in none of. A
    // closed set is cut into pieces of at most the window limit by LikePatternSyntax.TrySplitSet, or where it has no
    // such cut, of at most SQL Server's pattern limit less two, the rule the search-criteria parser applies; one it
    // cannot cut, or an unclosed one, is one piece written as it is, so that SQL Server reads it as it reads the whole
    // pattern, while it fits SQL Server's pattern limit; a longer one, which the parser rejects with a 400, is rejected
    // here.
    private static void AppendCharacterSet(
        List<LikeRecord> records,
        string pattern,
        LikeAtom atom,
        int kind,
        int fail,
        int nativeLikeLimit)
    {
        var element = new LikePatternElement(LikePatternElementKind.Set, atom.Start, atom.Length);
        var windowLimit = nativeLikeLimit - SeekWrapping;
        if (!LikePatternSyntax.TrySplitSet(pattern, element, windowLimit, out var negated, out var pieces)
            && !LikePatternSyntax.TrySplitSet(
                pattern, element, RecordQueryRules.MaxLikePatternLength - SeekWrapping, out negated, out pieces))
        {
            if (atom.Length > RecordQueryRules.MaxLikePatternLength)
            {
                throw new ArgumentException(SetTooLongMessage, nameof(pattern));
            }

            negated = false;
            pieces = [Text(pattern, atom, atom)];
        }

        records.Add(new LikeRecord(kind, 0, fail, LikeMeasure.One, pieces.Count, negated, 0, "_"));
        foreach (var piece in pieces)
        {
            records.Add(new LikeRecord(DataRecord, 0, -1, default, 0, false, 0, piece));
        }
    }

    // The seek of a segment, appended, and its index. Any position the segment can match at is a hit of the seek, so
    // the seek finds every candidate in order. The segment's leading whole atoms whose text fits PATINDEX('%' + text +
    // '%') within SQL Server's pattern limit, the lead, are sought with that PATINDEX, since whatever the segment
    // matches starts with what they match; a literal run after the lead, too long to join it, by the leftmost hit of
    // the lead followed by each of the run's prefixes (AppendRunSeek); a first literal run too long for PATINDEX, by
    // its prefixes alone; and a first set too long for PATINDEX, or an unclosed set, whose meaning a '%' after it would
    // change, by a scan of every position. A seek's patterns are bounded by SQL Server's pattern limit rather than by
    // the native limit, which only decides the steps, so a forced low limit still seeks.
    private static int AppendSeek(List<LikeRecord> records, string pattern, List<LikeAtom> atoms)
    {
        var limit = RecordQueryRules.MaxLikePatternLength - SeekWrapping;
        var first = atoms[0];
        if (FitsWindow(pattern, first, limit))
        {
            var end = WindowEnd(pattern, atoms, 0, limit);
            var lead = Text(pattern, first, atoms[end - 1]);
            if (end < atoms.Count && atoms[end].Kind == LikeAtomKind.Run)
            {
                return AppendRunSeek(records, lead, Text(pattern, atoms[end], atoms[end]));
            }

            records.Add(new LikeRecord(SeekStep, 0, -1, default, 0, false, 0, lead));
            return records.Count - 1;
        }

        if (first.Kind == LikeAtomKind.Run)
        {
            return AppendRunSeek(records, string.Empty, Text(pattern, first, first));
        }

        records.Add(new LikeRecord(ScanStep, 0, -1, default, 0, false, 0, string.Empty));
        return records.Count - 1;
    }

    // The seek of a lead, written as LIKE text, followed by a literal run, appended, and its index: the lead followed
    // by each of the run's prefixes ending at text-element boundaries, the longest whose LIKE form fits PATINDEX('%' +
    // text + '%') within SQL Server's pattern limit and up to RunCuts - 1 before it, each one text element shorter, as
    // one seek run step holding the longest and one data record per prefix holding its length with the lead's; the
    // step's candidate is the leftmost hit of any of them. The run's LIKE form writes %, _ and [ as the one-member sets
    // [%], [_] and [[], which only the grid filter holds, since a pattern's literal run has none. With no prefix that
    // fits, the seek is the lead's alone, or with no lead a scan.
    private static int AppendRunSeek(List<LikeRecord> records, string lead, string run)
    {
        var limit = RecordQueryRules.MaxLikePatternLength - SeekWrapping;
        var cuts = new List<long>();
        var likeLength = (long)lead.Length;
        var index = 0;
        while (index < run.Length)
        {
            var end = index + StringInfo.GetNextTextElementLength(run, index);
            for (; index < end; index++)
            {
                likeLength += run[index] is '%' or '_' or '[' ? 3 : 1;
            }

            if (likeLength > limit)
            {
                break;
            }

            cuts.Add(likeLength);
        }

        var seek = records.Count;
        if (cuts.Count == 0)
        {
            records.Add(lead.Length > 0
                ? new LikeRecord(SeekStep, 0, -1, default, 0, false, 0, lead)
                : new LikeRecord(ScanStep, 0, -1, default, 0, false, 0, string.Empty));
            return seek;
        }

        // A whole run that fits, which the loop measured to its end, is its one cut: a match of the run ends where the
        // run ends, so no character can straddle its end, and a shorter prefix would only add candidates.
        cuts.Reverse();
        cuts = likeLength <= limit ? [cuts[0]] : cuts.Take(RunCuts).ToList();
        var longest = new StringBuilder(lead, (int)cuts[0]);
        foreach (var character in run)
        {
            if (longest.Length >= cuts[0])
            {
                break;
            }

            longest.Append(character is '%' or '_' or '['
                ? string.Concat("[", character.ToString(), "]")
                : character.ToString());
        }

        records.Add(new LikeRecord(SeekRunStep, 0, -1, default, cuts.Count, false, 0, longest.ToString()));
        foreach (var cut in cuts)
        {
            records.Add(new LikeRecord(DataRecord, 0, -1, default, 0, false, cut, string.Empty));
        }

        return seek;
    }

    // The pattern's text from the first atom through the last.
    private static string Text(string pattern, LikeAtom first, LikeAtom last) =>
        pattern[first.Start..(last.Start + last.Length)];

    // [name] ([k], [v], [l], [z], [st]) AS (anchor UNION ALL step): the recursive CTE that runs a long-pattern program
    // over the values the anchor reads from source, one value expression per row and column. A row is one value at one
    // state: its key [k], the base row's GlobalRecordId; the value [v]; its length in characters [l]; [z] 1 when its
    // collation counts a surrogate pair as one character; and the state [st], six bigints packed as binary(48): the
    // record [s] to run, -1 once the pattern matched and -2 once the value failed; the step's phase [p]; the end [e] of
    // the text matched so far; the candidate [c] where the current segment started, or in a tail matched backwards
    // the start of the text it matched so far; and the phase's two numbers [t] and [h]. The anchor starts each
    // non-NULL value at the first instruction of every pattern; given minimumLength, the name of a bigint parameter,
    // only each value of at least that many characters ([h].[l]), the length below which the caller knows no value
    // matches. Each level unpacks a
    // row's state as [q], decodes its record from the parameter by index as [f], so neither the text nor the parameter
    // count grows with the program, and computes the next state as one expression, [o], so that no comparison is
    // evaluated twice; the record texts are read from the text area, decoded from hexadecimal once, an expression of
    // the parameter alone. Every step but a run step reads its text as [g].[w], an nvarchar(4000), which SQL Server's
    // pattern limit bounds it to, so its LIKE and PATINDEX patterns, a seek run's cuts included, are no large-object
    // values. A row whose state is -1 or -2 is not run again. All comparisons are under the value's
    // collation, which the decoded texts, coercible-default, never override. With boundary(j) meaning that a character
    // of the collation ends before position j (j beyond either end, the two code units at j - 1 and j not one '_',
    // either of them ignorable), a step at end e on the rest of r characters:
    //   1 window W of n characters, a '_' when its pieces follow: when SUBSTRING(v, e + 1, n) LIKE W and boundary(e + n
    //     + 1), it matches n; else when the rest LIKE W + '%', it bisects for the smallest k with SUBSTRING(v, e + 1, k)
    //     LIKE W + '%', which is monotone in k, extends k to a boundary, and matches k when SUBSTRING(v, e + 1, k) LIKE
    //     W; a set also needs its one character in a piece, or for a negated set in none;
    //   2 run R of n characters: it matches n when SUBSTRING(v, e + 1, n) + 'x' = R + 'x' and boundary(e + n + 1); else
    //     it bisects for the smallest k with SUBSTRING(v, e + 1, k) not below R, monotone too, then scans the k that
    //     compare equal, '=' ignoring trailing blanks, for the first equal with the 'x' sentinel at a boundary;
    //   3 seek W and 4 seek run: from e + 1, once it is a boundary, the candidate c is e + PATINDEX('%' + W + '%') of
    //     the rest, or for a run the leftmost such hit of its prefixes; a candidate at a boundary starts the segment's
    //     steps at c - 1, another one, or the next boundary, is sought again;
    //   5 scan: the candidate is e + 1, once it is a boundary;
    //   6 tail W: the rest LIKE '%' + W;
    //   7 end: the rest LIKE N'', empty or ignorable;
    //   9 end anchor: c becomes the value's length, so the tail's back windows start at its end;
    //   10 back window W of n characters, a '_' when its pieces follow, on the a = c - e characters between the text
    //     matched forwards and the text matched backwards: when n <= a, SUBSTRING(v, c - n + 1, n) LIKE W and
    //     boundary(c - n + 1), it matches n; else when SUBSTRING(v, e + 1, a) LIKE '%' + W, it bisects for the
    //     smallest k with SUBSTRING(v, c - k + 1, k) LIKE '%' + W, monotone in k as well, extends k leftwards to a
    //     boundary, and matches k when SUBSTRING(v, c - k + 1, k) LIKE W, moving c to c - k; a set's character must be
    //     in a piece or for a negated set in none. The text between e and c is the tail's leading '%'.
    // A match moves e, or c for a back window, and runs the next instruction; a failure in a middle segment or a last
    // one matched forwards returns to its seek with e at the candidate, and elsewhere fails the value. Every row
    // advances its state, its e or its c, so each row ends. Every SUBSTRING length is a decoded count, a bisection
    // bound, a remaining length clamped at 0, c - e, 1 or 2, so none raises error 537.
    private static void AppendLongPatternProgram(
        StringBuilder sql,
        string name,
        string key,
        string source,
        List<string> values,
        string program,
        LongPatternProgram compiled,
        string? minimumLength = null)
    {
        var width = PackedNumberWidth.ToString(CultureInfo.InvariantCulture);
        var record = LongPatternRecordWidth.ToString(CultureInfo.InvariantCulture);

        // 1-based positions: the pattern count at 1, the record count at W + 1, field f of record r at
        // 2W + 1 + R * r + W * f, and the text area after the records.
        string Field(int field, string index) => string.Concat(
            "CAST(SUBSTRING(", program, ", ",
            (LongPatternHeaderWidth + 1 + (field * PackedNumberWidth)).ToString(CultureInfo.InvariantCulture),
            " + ", record, " * (", index, "), ", width, ") AS bigint)");
        var area = string.Concat(
            "CONVERT(varbinary(max), SUBSTRING(", program, ", ",
            (LongPatternHeaderWidth + 1).ToString(CultureInfo.InvariantCulture), " + ", record, " * CAST(SUBSTRING(",
            program, ", ", (PackedNumberWidth + 1).ToString(CultureInfo.InvariantCulture), ", ", width,
            ") AS bigint), 2147483647), 2)");
        string TextStart(string index) =>
            string.Concat("CASE WHEN ", index, " > 0 THEN ", Field(TextEndField, index + " - 1"), " ELSE 0 END");
        string TextAt(string index, string length) => string.Concat(
            "CAST(SUBSTRING(", area, ", 2 * (", TextStart(index), ") + 1, 2 * (", length, ")) AS nvarchar(max))");
        string RecordText(string index) =>
            TextAt(index, string.Concat(Field(TextEndField, index), " - (", TextStart(index), ")"));

        sql.Append(name).Append(" ([k], [v], [l], [z], [st]) AS (SELECT ")
            .Append(key).Append(", [u].[v], [h].[l], [h].[z], ")
            .Append(Pack(Field(NextField, "[a].[i]") + " - 1", "0", "0", "0", "0", "0")).Append(" FROM ")
            .Append(source).Append(" CROSS APPLY (VALUES ");
        for (var index = 0; index < values.Count; index++)
        {
            sql.Append(index == 0 ? "(" : ", (").Append(values[index]).Append(')');
        }

        sql.Append(") AS [u]([v]) CROSS APPLY (VALUES (CAST(LEN([u].[v] + N'.') - 1 AS bigint), ")
            .Append("CASE WHEN LEN(SUBSTRING([u].[v], 1, 0) + NCHAR(55357) + NCHAR(56832)) = 1 THEN 1 ELSE 0 END))")
            .Append(" AS [h]([l], [z]) CROSS JOIN ");
        AppendDigitIndex(sql, compiled.PatternCount);
        sql.Append(" WHERE [u].[v] IS NOT NULL");
        if (minimumLength is not null)
        {
            sql.Append(" AND [h].[l] >= ").Append(minimumLength);
        }

        sql.Append(" AND [a].[i] < CAST(SUBSTRING(").Append(program).Append(", 1, ").Append(width).Append(") AS int)");

        sql.Append(" UNION ALL SELECT [x].[k], [x].[v], [x].[l], [x].[z], [o].[st] FROM ").Append(name)
            .Append(" AS [x] CROSS APPLY (VALUES (");
        for (var part = 0; part < 6; part++)
        {
            sql.Append(part == 0 ? string.Empty : ", ").Append("CAST(SUBSTRING([x].[st], ")
                .Append((1 + (8 * part)).ToString(CultureInfo.InvariantCulture)).Append(", 8) AS bigint)");
        }

        sql.Append(")) AS [q]([s], [p], [e], [c], [t], [h]) CROSS APPLY (VALUES (")
            .Append(Field(KindField, "[q].[s]")).Append(", ")
            .Append(Field(NextField, "[q].[s]")).Append(" - 1, ")
            .Append(Field(FailField, "[q].[s]")).Append(" - 1, CASE WHEN [x].[z] = 1 THEN ")
            .Append(Field(CountField + 1, "[q].[s]")).Append(" ELSE ").Append(Field(CountField, "[q].[s]"))
            .Append(" END, ").Append(Field(ItemsField, "[q].[s]")).Append(", ")
            .Append(Field(NegatedField, "[q].[s]")).Append(", ").Append(RecordText("[q].[s]"))
            .Append(", CASE WHEN [x].[l] > [q].[e] THEN [x].[l] - [q].[e] ELSE 0 END))")
            .Append(" AS [f]([t], [nx], [fl], [n], [ic], [ng], [pt], [r]) CROSS APPLY (VALUES (CAST(SUBSTRING([f].[pt], 1, ")
            .Append(RecordQueryRules.MaxLikePatternLength.ToString(CultureInfo.InvariantCulture))
            .Append(") AS nvarchar(4000)))) AS [g]([w]) CROSS APPLY (VALUES (CASE [f].[t] WHEN ")
            .Append(WindowStep.ToString(CultureInfo.InvariantCulture)).Append(" THEN ")
            .Append(WindowOutcome(compiled.MaxItems, RecordText)).Append(" WHEN ")
            .Append(RunStep.ToString(CultureInfo.InvariantCulture)).Append(" THEN ").Append(RunOutcome()).Append(" WHEN ")
            .Append(SeekStep.ToString(CultureInfo.InvariantCulture)).Append(" THEN ")
            .Append(SeekOutcome(string.Concat("PATINDEX(N'%' + [g].[w] + N'%', ", Rest, ")"))).Append(" WHEN ")
            .Append(SeekRunStep.ToString(CultureInfo.InvariantCulture)).Append(" THEN ")
            .Append(SeekOutcome(LeftmostCutHit(Field))).Append(" WHEN ")
            .Append(ScanStep.ToString(CultureInfo.InvariantCulture)).Append(" THEN CASE WHEN [q].[e] > [x].[l] THEN ")
            .Append(Ended).Append(" WHEN NOT ").Append(Boundary("[q].[e] + 1")).Append(" THEN ")
            .Append(Stay("[q].[e] + 1")).Append(" ELSE ").Append(Found("[q].[e] + 1")).Append(" END WHEN ")
            .Append(TailStep.ToString(CultureInfo.InvariantCulture)).Append(" THEN CASE WHEN ").Append(Rest)
            .Append(" LIKE N'%' + [g].[w] THEN ").Append(Matched("[f].[r]")).Append(" ELSE ").Append(Failed)
            .Append(" END WHEN ").Append(EndAnchorStep.ToString(CultureInfo.InvariantCulture)).Append(" THEN ")
            .Append(Pack("[f].[nx]", "0", "[q].[e]", "[x].[l]", "0", "0")).Append(" WHEN ")
            .Append(BackWindowStep.ToString(CultureInfo.InvariantCulture)).Append(" THEN ")
            .Append(BackWindowOutcome(compiled.MaxItems, RecordText))
            .Append(" ELSE CASE WHEN ").Append(Rest).Append(" LIKE N'' THEN ").Append(Matched("[f].[r]"))
            .Append(" ELSE ").Append(Failed).Append(" END END)) AS [o]([st]) WHERE [q].[s] >= 0)");
    }

    // The next states a long-pattern step can give: failed, retrying the segment's seek from its candidate or failing
    // the value; failed the value; matched count more characters, forwards or backwards, running the next instruction;
    // bisecting; extending; staying at a seek or a scan with a new end; and a found candidate, which starts the
    // segment's steps.
    private static string Failed => Pack(
        "CASE WHEN [f].[fl] >= 0 THEN [f].[fl] ELSE -2 END",
        "0",
        "CASE WHEN [f].[fl] >= 0 THEN [q].[c] ELSE [q].[e] END",
        "[q].[c]",
        "0",
        "0");

    private static string Ended => Pack("-2", "0", "[q].[e]", "[q].[c]", "0", "0");

    private static string Matched(string count) =>
        Pack("[f].[nx]", "0", string.Concat("[q].[e] + ", count), "[q].[c]", "0", "0");

    private static string MatchedBack(string count) =>
        Pack("[f].[nx]", "0", "[q].[e]", string.Concat("[q].[c] - ", count), "0", "0");

    private static string Bisect(string low, string high) =>
        Pack("[q].[s]", BisectPhase.ToString(CultureInfo.InvariantCulture), "[q].[e]", "[q].[c]", low, high);

    private static string Extend(string count) =>
        Pack("[q].[s]", ExtendPhase.ToString(CultureInfo.InvariantCulture), "[q].[e]", "[q].[c]", count, "0");

    private static string Stay(string end) =>
        Pack("[q].[s]", FreshPhase.ToString(CultureInfo.InvariantCulture), end, "[q].[c]", "0", "0");

    private static string Found(string candidate) =>
        Pack("[f].[nx]", "0", string.Concat("(", candidate, ") - 1"), candidate, "0", "0");

    // The state the six numbers give, each a bigint of eight bytes.
    private static string Pack(string record, string phase, string end, string candidate, string first, string second) =>
        string.Concat(
            "CAST(",
            string.Join(
                " + ",
                new[] { record, phase, end, candidate, first, second }
                    .Select(part => string.Concat("CAST(CAST(", part, " AS bigint) AS binary(8))"))),
            " AS binary(48))");

    // Whether a character of the value's collation ends before position j: beyond the value, at its start, where the
    // code units at j - 1 and j are not one '_', or where either is ignorable, matching N''.
    private static string Boundary(string position)
    {
        var j = string.Concat("(", position, ")");
        return string.Concat(
            "(", j, " > [x].[l] OR ", j, " <= 1 OR NOT (SUBSTRING([x].[v], ", j, " - 1, 2) LIKE N'_') OR SUBSTRING([x].[v], ",
            j, ", 1) LIKE N'' OR SUBSTRING([x].[v], ", j, " - 1, 1) LIKE N'')");
    }

    // Whether the count characters of the value from start are in a piece of the step's set, or for a negated set in
    // none; always true for a step with no pieces.
    private static string Membership(int maxItems, Func<string, string> recordText, string start, string count)
    {
        if (maxItems == 0)
        {
            return "1 = 1";
        }

        var digits = new StringBuilder();
        AppendDigitIndex(digits, maxItems);
        return string.Concat(
            "CASE WHEN [f].[ic] = 0 THEN 1 WHEN EXISTS (SELECT 1 FROM ", digits.ToString(),
            " WHERE [a].[i] < [f].[ic] AND SUBSTRING([x].[v], ", start, ", ", count, ") LIKE ",
            recordText("[q].[s] + 1 + [a].[i]"), ") THEN 1 - [f].[ng] ELSE [f].[ng] END = 1");
    }

    // The next state of a back window step, a '_' followed by its pieces for a set, on the text between the end [e] of
    // the text matched forwards and the start [c] of the text matched backwards: the fast path of its own character
    // count, then the bisection for the shortest end of that text that ends with what the window matches, the
    // extension leftwards to a boundary and the final LIKE, with the set's membership.
    private static string BackWindowOutcome(int maxItems, Func<string, string> recordText)
    {
        const string Span = "([q].[c] - [q].[e])";
        static string Start(string count) => string.Concat("[q].[c] - ", count, " + 1");
        return string.Concat(
            "CASE WHEN [q].[p] = ", FreshPhase.ToString(CultureInfo.InvariantCulture), " THEN CASE WHEN [f].[n] <= ",
            Span, " AND SUBSTRING([x].[v], ", Start("[f].[n]"), ", [f].[n]) LIKE [g].[w] AND ",
            Boundary(Start("[f].[n]")), " THEN CASE WHEN ", Membership(maxItems, recordText, Start("[f].[n]"), "[f].[n]"),
            " THEN ", MatchedBack("[f].[n]"), " ELSE ", Failed, " END WHEN SUBSTRING([x].[v], [q].[e] + 1, ", Span,
            ") LIKE N'%' + [g].[w] THEN ", Bisect("0", Span), " ELSE ", Failed, " END WHEN [q].[p] = ",
            BisectPhase.ToString(CultureInfo.InvariantCulture), " AND [q].[t] < [q].[h] THEN CASE WHEN SUBSTRING(",
            "[x].[v], ", Start(Midpoint), ", ", Midpoint, ") LIKE N'%' + [g].[w] THEN ", Bisect("[q].[t]", Midpoint),
            " ELSE ", Bisect(Midpoint + " + 1", "[q].[h]"), " END WHEN NOT ", Boundary(Start("[q].[t]")), " THEN ",
            Extend("[q].[t] + 1"), " WHEN SUBSTRING([x].[v], ", Start("[q].[t]"), ", [q].[t]) LIKE [g].[w] THEN ",
            "CASE WHEN ", Membership(maxItems, recordText, Start("[q].[t]"), "[q].[t]"), " THEN ",
            MatchedBack("[q].[t]"), " ELSE ", Failed, " END ELSE ", Failed, " END");
    }

    // The next state of a window step, a '_' followed by its pieces for a set: the fast path of its own character
    // count, then the bisection, the extension to a boundary and the final LIKE, with the set's membership.
    private static string WindowOutcome(int maxItems, Func<string, string> recordText)
    {
        string Membership(string count) => SearchSqlBuilder.Membership(maxItems, recordText, "[q].[e] + 1", count);

        return string.Concat(
            "CASE WHEN [q].[p] = ", FreshPhase.ToString(CultureInfo.InvariantCulture), " THEN CASE WHEN [f].[n] <= [f].[r]",
            " AND SUBSTRING([x].[v], [q].[e] + 1, [f].[n]) LIKE [g].[w] AND ", Boundary("[q].[e] + [f].[n] + 1"),
            " THEN CASE WHEN ", Membership("[f].[n]"), " THEN ", Matched("[f].[n]"), " ELSE ", Failed, " END WHEN ", Rest,
            " LIKE [g].[w] + N'%' THEN ", Bisect("0", "[f].[r]"), " ELSE ", Failed, " END WHEN [q].[p] = ",
            BisectPhase.ToString(CultureInfo.InvariantCulture), " AND [q].[t] < [q].[h] THEN CASE WHEN SUBSTRING([x].[v],",
            " [q].[e] + 1, ", Midpoint, ") LIKE [g].[w] + N'%' THEN ", Bisect("[q].[t]", Midpoint), " ELSE ",
            Bisect(Midpoint + " + 1", "[q].[h]"), " END WHEN NOT ", Boundary("[q].[e] + [q].[t] + 1"), " THEN ",
            Extend("[q].[t] + 1"), " WHEN SUBSTRING([x].[v], [q].[e] + 1, [q].[t]) LIKE [g].[w] THEN CASE WHEN ",
            Membership("[q].[t]"), " THEN ", Matched("[q].[t]"), " ELSE ", Failed, " END ELSE ", Failed, " END");
    }

    // The next state of a run step: the fast path of its own character count, then the bisection for the first end
    // not below the run, and the scan of the ends that compare equal.
    private static string RunOutcome() => string.Concat(
        "CASE WHEN [q].[p] = ", FreshPhase.ToString(CultureInfo.InvariantCulture), " THEN CASE WHEN [f].[n] <= [f].[r]",
        " AND SUBSTRING([x].[v], [q].[e] + 1, [f].[n]) + N'x' = [f].[pt] + N'x' AND ", Boundary("[q].[e] + [f].[n] + 1"),
        " THEN ", Matched("[f].[n]"), " WHEN ", Rest, " < [f].[pt] THEN ", Failed, " ELSE ", Bisect("0", "[f].[r]"),
        " END WHEN [q].[p] = ", BisectPhase.ToString(CultureInfo.InvariantCulture), " AND [q].[t] < [q].[h] THEN CASE",
        " WHEN SUBSTRING([x].[v], [q].[e] + 1, ", Midpoint, ") < [f].[pt] THEN ", Bisect(Midpoint + " + 1", "[q].[h]"),
        " ELSE ", Bisect("[q].[t]", Midpoint), " END WHEN SUBSTRING([x].[v], [q].[e] + 1, [q].[t]) + N'x' = [f].[pt]",
        " + N'x' AND ", Boundary("[q].[e] + [q].[t] + 1"), " THEN ", Matched("[q].[t]"),
        " WHEN [q].[t] < [f].[r] AND NOT (SUBSTRING([x].[v], [q].[e] + 1, [q].[t] + 1) > [f].[pt]) THEN ",
        Extend("[q].[t] + 1"), " ELSE ", Failed, " END");

    // The next state of a seek whose hit, relative to the rest, is hit: past the value, fail it; not at a boundary,
    // move to the next position; else hold the hit, which the next level applies, so the hit is computed once.
    private static string SeekOutcome(string hit) => string.Concat(
        "CASE WHEN [q].[p] = ", HitPhase.ToString(CultureInfo.InvariantCulture), " THEN CASE WHEN [q].[t] = 0 THEN ",
        Ended, " WHEN ", Boundary("[q].[e] + [q].[t]"), " THEN ", Found("[q].[e] + [q].[t]"), " ELSE ",
        Stay("[q].[e] + [q].[t]"), " END WHEN [q].[e] > [x].[l] THEN ", Ended, " WHEN NOT ", Boundary("[q].[e] + 1"),
        " THEN ", Stay("[q].[e] + 1"), " ELSE ",
        Pack("[q].[s]", HitPhase.ToString(CultureInfo.InvariantCulture), "[q].[e]", "[q].[c]", hit, "0"), " END");

    // The leftmost PATINDEX hit in the rest of any of a seek run's cuts, the prefixes of its text of the lengths its
    // data records hold, or 0: LEAST ignores the NULL of a cut that is not there or has no hit.
    private static string LeftmostCutHit(Func<int, string, string> field)
    {
        var hits = new StringBuilder("ISNULL(LEAST(");
        for (var cut = 0; cut < RunCuts; cut++)
        {
            var index = cut.ToString(CultureInfo.InvariantCulture);
            hits.Append(cut == 0 ? string.Empty : ", ").Append("CASE WHEN [f].[ic] > ").Append(index)
                .Append(" THEN NULLIF(PATINDEX(N'%' + SUBSTRING([g].[w], 1, ")
                .Append(field(CutField, string.Concat("[q].[s] + 1 + ", index)))
                .Append(") + N'%', ").Append(Rest).Append("), 0) END");
        }

        return hits.Append("), 0)").ToString();
    }

    // EXISTS (SELECT 1 FROM [name] AS [y] WHERE [y].[k] = b.[GlobalRecordId] AND the state of [y] is -1): the base row,
    // whose key column reference is key, has a value the program matched.
    private static void AppendLongPatternMatch(StringBuilder sql, string name, string key) =>
        sql.Append("EXISTS (SELECT 1 FROM ").Append(name).Append(" AS [y] WHERE [y].[k] = ").Append(key)
            .Append(" AND CAST(SUBSTRING([y].[st], 1, 8) AS bigint) = -1)");

    // OPTION ([MAXRECURSION 0, MAXDOP 1][, ][plan hints]): the hints of a statement that runs a program, which lift the
    // recursion limit and keep the program on one worker (LongPatternHint), then the plan hints of a packed statement
    // or of the list; nothing when the statement has neither.
    private static void AppendQueryHints(StringBuilder sql, bool hasLongPatterns, string? planHints)
    {
        if (!hasLongPatterns && planHints is null)
        {
            return;
        }

        sql.Append(" OPTION (");
        if (hasLongPatterns)
        {
            sql.Append(LongPatternHint).Append(planHints is null ? string.Empty : ", ");
        }

        sql.Append(planHints).Append(')');
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

    // The FROM clause and predicate of ListPage's filtered set: the base table under the HOLDLOCK hint ListPage
    // describes and the selected set, and with a grid filter the LEFT JOIN to each page holding a grid column, held
    // too, and the filter. Without a filter the set reads the base table alone, and ListPage joins those pages to the
    // fetched rows of the page instead, so a page row is read only for a row it lists. The filter reads the grid
    // columns, so its set joins them. It is a case-insensitive "contains" over the grid columns that does not depend on
    // the database collation: each column is converted to text (a datetime as yyyy-mm-dd), and both sides are compared
    // under one case-insensitive collation, with the filter's pattern characters escaped by
    // RecordQueryRules.ToContainsPattern. It has no legacy counterpart. A filter whose contains pattern is longer than
    // the native limit is matched by the long-pattern program instead (CompileContainsProgram), over the same converted
    // text of the same held joins, which carries the same collation into every comparison: the filter is one literal
    // run between two runs of '%', compared with '=' under Latin1_General_100_CI_AS, so it keeps that collation's
    // case-insensitive literal "contains", its expansions and ignorable characters included, and is found by PATINDEX
    // of its prefixes with %, _ and [ written as one-member sets, since PATINDEX takes no ESCAPE clause. Its anchor
    // skips a value shorter than ContainsMinimumLength(filter), one character per RunCuts text elements holding a
    // weighted character, a letter, number, punctuation mark, symbol or space separator of WeightedCharacterRanges
    // (IsWeightedCharacter), none of which that collation ignores (measured), bound as a bigint right after the
    // program: such a value cannot contain the filter. The program's CTE, [x0], reads the base table and those page
    // tables under HOLDLOCK and in the same order, so its reads belong to the list's one consistent read; it is
    // returned for the caller to lead the statement with, and null when there is none.
    private static string? AppendListSource(
        StringBuilder sql,
        FormSchema schema,
        IReadOnlyList<ProjectedColumn> columns,
        RecordListQuery query,
        PositionalParameters parameters,
        int nativeLikeLimit)
    {
        var filter = query.Filter;
        var from = new StringBuilder();
        AppendBaseTable(from, schema);
        from.Append(" WITH (HOLDLOCK)");
        if (filter is not null)
        {
            AppendGridPageJoins(from, schema, columns, BaseReference(RecordSqlBuilder.Require(schema.GlobalRecordId)));
        }

        sql.Append(" FROM ").Append(from);
        var filtered = RecordSqlBuilder.AppendSelectedSetPredicate(
            sql, schema, query.IncludeDeleted, parameters, SelectedSetClause.Where);
        if (filter is null)
        {
            return null;
        }

        var containsPattern = RecordQueryRules.ToContainsPattern(filter);
        sql.Append(filtered ? " AND " : " WHERE ");
        if (columns.Count == 0)
        {
            // A filter with no column to search matches nothing.
            sql.Append("1 = 0");
            return null;
        }

        var texts = new List<string>(columns.Count);
        foreach (var column in columns)
        {
            var text = new StringBuilder();
            AppendGridFilterText(text, schema, column);
            texts.Add(text.ToString());
        }

        if (containsPattern.Length > nativeLikeLimit)
        {
            var name = LongPatternName(0);
            var key = BaseReference(RecordSqlBuilder.Require(schema.GlobalRecordId));
            var compiled = CompileContainsProgram(filter);
            var minimumLength = ContainsMinimumLength(filter);
            var programText = parameters.Add(n => ColumnValueConverter.Text(n, compiled.Text));
            var program = new StringBuilder();
            AppendLongPatternProgram(
                program,
                name,
                key,
                from.ToString(),
                texts,
                programText,
                compiled,
                parameters.Add(n => ColumnValueConverter.Int64(n, minimumLength)));
            AppendLongPatternMatch(sql, name, key);
            return program.ToString();
        }

        // One parameter serves every column's term.
        var pattern = parameters.Add(n => ColumnValueConverter.Text(n, containsPattern));
        sql.Append('(');
        for (var index = 0; index < texts.Count; index++)
        {
            sql.Append(index == 0 ? string.Empty : " OR ").Append(texts[index])
                .Append(" LIKE ").Append(pattern).Append(" COLLATE ").Append(ContainsCollation)
                .Append(" ESCAPE '").Append(RecordQueryRules.ContainsPatternEscape).Append('\'');
        }

        sql.Append(')');
        return null;
    }

    // CONVERT(nvarchar(max), p{i}.[col][, 23]) COLLATE Latin1_General_100_CI_AS: the text the grid filter searches in
    // one grid column.
    private static void AppendGridFilterText(StringBuilder sql, FormSchema schema, ProjectedColumn column)
    {
        sql.Append("CONVERT(nvarchar(max), ").Append(RecordSqlBuilder.ColumnReference(schema, column));
        if (column.Column.Metadata.Family == ColumnFamily.DateTime)
        {
            sql.Append(", ").Append(IsoDateStyle);
        }

        sql.Append(") COLLATE ").Append(ContainsCollation);
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

    // LEFT JOIN [page] p{i} WITH (HOLDLOCK) ON p{i}.[GlobalRecordId] = key for each page with a table that holds a grid
    // column, in page order: the list's joins, keyed on the GlobalRecordId reference key, b.[GlobalRecordId] in the
    // filtered set and [pg].[c0] after the page.
    private static void AppendGridPageJoins(
        StringBuilder sql,
        FormSchema schema,
        IReadOnlyList<ProjectedColumn> columns,
        string key)
    {
        var joined = new bool[schema.Pages.Count];
        foreach (var column in columns)
        {
            joined[RecordSqlBuilder.PageIndexOf(schema, column.Field.Page.PageId)] = true;
        }

        for (var index = 0; index < joined.Length; index++)
        {
            if (joined[index] && schema.Pages[index].Table is { } table)
            {
                AppendPageJoin(sql, index, table, "LEFT JOIN", holdLock: true, key);
            }
        }
    }

    // <join> [page] p{i} [WITH (HOLDLOCK)] ON p{i}.[GlobalRecordId] = key, key the reference to the base row's
    // GlobalRecordId the page row is joined to; only ListPage holds its page tables, for the reason it describes, so
    // Find's joins carry no hint.
    private static void AppendPageJoin(
        StringBuilder sql,
        int pageIndex,
        TableSchema table,
        string join,
        bool holdLock,
        string key)
    {
        var alias = RecordSqlBuilder.PageAlias(pageIndex);
        sql.Append(' ').Append(join).Append(' ').Append(SqlIdentifier.Quote(table)).Append(' ').Append(alias);
        if (holdLock)
        {
            sql.Append(" WITH (HOLDLOCK)");
        }

        sql.Append(" ON ").Append(alias).Append('.').Append(SqlIdentifier.Quote(RecordSqlBuilder.RequirePageKey(table)))
            .Append(" = ").Append(key);
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
    // PageIndex is the index in FormSchema.Pages of a page field's page, which a long-pattern program joins to read
    // the column, and null for a base-table column.
    private sealed record ResolvedCondition(
        SearchCondition Condition,
        string Reference,
        string Comparable,
        ColumnMetadata Column,
        int? PageIndex);

    // The scalar parameters of one packed list: a condition's values that share an operator and a parameter type.
    private sealed record PackedGroup(SearchOperator Operator, DbType DbType, List<SqlParameterValue> Values);

    // A compiled long-pattern program: its ASCII text, the number of patterns it holds, and the most data records any
    // of its instructions has, which sizes the row source a set's pieces are read through.
    private sealed record LongPatternProgram(string Text, int PatternCount, int MaxItems);

    // One record of a long-pattern program: its kind; the record to run after it succeeds, -1 when the pattern has
    // matched; the record to retry from after it fails, -1 when the value fails; its character count; the number of
    // data records after it; whether its set is negated; a cut's length; and its text, a window, a run, a piece or a
    // seek run's longest prefix, or empty.
    private sealed record LikeRecord(
        int Kind,
        int Next,
        int Fail,
        LikeMeasure Count,
        int Items,
        bool Negated,
        long Cut,
        string Text);

    // The kinds of atom a long pattern is compiled from.
    private enum LikeAtomKind
    {
        // A run of literal characters, never cut between two steps.
        Run,

        // A '_'.
        AnyCharacter,

        // A [...] set, closed or not.
        Set,
    }

    // One atom of a long pattern: its kind, its text's start and length in the pattern, and its character count.
    private readonly record struct LikeAtom(LikeAtomKind Kind, int Start, int Length, LikeMeasure Count);

    // A number of characters as both kinds of collation count them: in UTF-16 code units, as a collation without
    // supplementary-character support counts, and with a surrogate pair as one character, as an _SC collation counts.
    // Only a literal surrogate pair differs; a set or a '_' matches one character under both.
    private readonly record struct LikeMeasure(long CodeUnits, long Characters)
    {
        public static LikeMeasure One => new(1, 1);

        public static LikeMeasure operator +(LikeMeasure left, LikeMeasure right) =>
            new(left.CodeUnits + right.CodeUnits, left.Characters + right.Characters);

        public static LikeMeasure Of(LikePatternElement element) => element.IsSurrogatePair ? new(2, 1) : One;

        // The measure of literal text: each surrogate pair one character, every other code unit one.
        public static LikeMeasure Of(string text)
        {
            var pairs = 0L;
            for (var index = 0; index + 1 < text.Length; index++)
            {
                if (char.IsSurrogatePair(text[index], text[index + 1]))
                {
                    pairs++;
                    index++;
                }
            }

            return new(text.Length, text.Length - pairs);
        }
    }
}
