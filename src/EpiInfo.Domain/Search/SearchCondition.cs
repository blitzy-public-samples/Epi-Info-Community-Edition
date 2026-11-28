using System.Globalization;
using EpiInfo.Domain.Forms;
using EpiInfo.Domain.Records;
using EpiInfo.Domain.Values;

namespace EpiInfo.Domain.Search;

/// <summary>
/// How a search alternative compares a column with its value. The first six comparisons are the ones the legacy
/// Find dialog reads from the typed value: a leading <c>&lt;=</c>, <c>&gt;=</c>, <c>&lt;&gt;</c>, <c>&gt;</c> or
/// <c>&lt;</c>, tested in that order, selects one of the five prefix comparisons, and <see cref="Equal"/> is the
/// default when none leads (<c>Epi.Windows.Enter/Dialogs/FindRecordDialog.cs</c>, lines 708-771). The prefix is the
/// operator only for number, date, Phone Number and <c>GlobalRecordId</c> alternatives; the others ignore it or,
/// for a checkbox, reject it. The other three are the predicate shapes the legacy query picks by field kind and
/// value (<c>Epi.Core/Data/Services/CollectedDataProvider.cs</c>, lines 1973-2249).
/// </summary>
public enum SearchOperator
{
    /// <summary>
    /// The column equals the value. This is the comparison when no prefix leads the value, and the only one of the
    /// first six where legacy strips and ignores the prefix: option, <c>RecStatus</c>, and text, multiline and
    /// legal-values fields other than Phone Number, whose values with <c>%</c>, <c>*</c> or <c>?</c> take
    /// <see cref="Like"/> and whose other multiline values take <see cref="PrefixEqual255"/> instead. It is also
    /// the only one for a checkbox, whose alternative legacy rejects when it carries a prefix. Phone Number takes
    /// its operator from the prefix, as number, date and <c>GlobalRecordId</c> do.
    /// </summary>
    Equal,

    /// <summary>The column differs from the value; the prefix <c>&lt;&gt;</c>.</summary>
    NotEqual,

    /// <summary>The column is less than the value; the prefix <c>&lt;</c>.</summary>
    Less,

    /// <summary>The column is less than or equal to the value; the prefix <c>&lt;=</c>.</summary>
    LessOrEqual,

    /// <summary>The column is greater than the value; the prefix <c>&gt;</c>.</summary>
    Greater,

    /// <summary>The column is greater than or equal to the value; the prefix <c>&gt;=</c>.</summary>
    GreaterOrEqual,

    /// <summary>
    /// The column holds no value, and the alternative carries none. Legacy chooses it for a blank number or date
    /// criterion; a number or date alternative that is only a prefix is rejected instead, because what follows the
    /// prefix must parse as a number or a date (the dialog's lines 418-510). On a text, multiline, legal-values,
    /// Phone Number or <c>GlobalRecordId</c> criterion, legacy also chooses it for an alternative that is only a
    /// prefix, left empty once the prefix is stripped (the query's lines 2068-2082, 2118-2131 and 2186-2193).
    /// </summary>
    IsNull,

    /// <summary>
    /// The column matches a text pattern the parser has already converted, each <c>*</c> replaced by <c>%</c>.
    /// The pattern is bound as a parameter with no escape character, so <c>%</c> matches any run of characters,
    /// <c>_</c> any single character and <c>[...]</c> any character of the set, as in legacy, while <c>?</c>
    /// matches itself. Single quotes stay in the pattern, where legacy removed them from the literal it inlined
    /// (lines 2195-2206).
    /// </summary>
    Like,

    /// <summary>
    /// For multiline fields: the first 255 characters of the column equal the first 255 characters of the value.
    /// Legacy compares only that prefix because a multiline column holds unbounded text that cannot be compared
    /// whole (lines 2212-2222).
    /// </summary>
    PrefixEqual255,
}

/// <summary>
/// The column a search condition reads.
/// </summary>
public enum SearchTarget
{
    /// <summary>
    /// A data field of the form, stored in a column of one of its page tables; the condition's
    /// <see cref="SearchCondition.FieldType"/> gives its kind.
    /// </summary>
    PageField,

    /// <summary>The base table's <c>GlobalRecordId</c> system column. Its alternatives carry text values.</summary>
    BaseGlobalRecordId,

    /// <summary>
    /// The base table's <c>RecStatus</c> system column. Its alternatives carry integral number values. The
    /// condition narrows the caller's selected set, the active records or every record with
    /// <c>includeDeleted</c>, and never replaces that set's predicate, so <c>RecStatus</c> 0 matches nothing
    /// unless deleted records are selected.
    /// </summary>
    BaseRecStatus,
}

/// <summary>
/// One alternative of a <see cref="SearchCondition"/>: an operator and the value it compares the column with.
/// </summary>
/// <remarks>
/// <para>
/// The value's kind follows the column the condition reads:
/// <list type="bullet">
/// <item><description>Text: text, multiline and legal-values fields, Phone Number fields, <c>GlobalRecordId</c>,
/// and every <see cref="SearchOperator.Like"/> and <see cref="SearchOperator.PrefixEqual255"/> value.</description></item>
/// <item><description>Number: number fields, and <c>RecStatus</c> as an integral number.</description></item>
/// <item><description>Date: date fields.</description></item>
/// <item><description>Boolean: checkbox fields.</description></item>
/// <item><description>Index: option fields.</description></item>
/// </list>
/// </para>
/// <para>
/// Only the search criteria parser constructs alternatives, so the constructor's
/// <see cref="ArgumentException"/> signals a programming error, never a user's input. The synthesized
/// <see cref="object.ToString"/> includes the value, which is field data: never pass an alternative or its text
/// to a logger or an error message.
/// </para>
/// </remarks>
/// <param name="Operator">The comparison; see <see cref="Operator"/>.</param>
/// <param name="Value">The parameter value; see <see cref="Value"/>.</param>
public sealed record SearchAlternative(SearchOperator Operator, FieldValue? Value)
{
    /// <summary>The alternative that matches a column holding no value.</summary>
    public static SearchAlternative Null { get; } = new(SearchOperator.IsNull, null);

    /// <summary>The comparison, a defined <see cref="SearchOperator"/> member.</summary>
    public SearchOperator Operator { get; } = SearchModelGuard.CheckOperator(Operator);

    /// <summary>
    /// The parameter value: <see langword="null"/> exactly when <see cref="Operator"/> is
    /// <see cref="SearchOperator.IsNull"/>, and otherwise a value whose kind is not
    /// <see cref="FieldValueKind.Null"/>.
    /// </summary>
    public FieldValue? Value { get; } = SearchModelGuard.CheckValue(Operator, Value);
}

/// <summary>
/// One parsed criterion of <c>POST /api/forms/{viewId}/records/search</c>: a column and the alternatives the user
/// wrote for it. It is the SQL-free output of the search criteria parser and the input of
/// <c>IDataStore.SearchAsync</c> and the data layer's search builder.
/// </summary>
/// <remarks>
/// <para>
/// Alternatives within one condition combine with OR, and separate conditions combine with AND, as the legacy
/// query groups them (<c>Epi.Core/Data/Services/CollectedDataProvider.cs</c>, lines 1973-1993). A search holds at
/// most one condition per column.
/// </para>
/// <para>
/// Record equality compares <see cref="Alternatives"/> by reference, and each condition holds its own copy, so
/// compare alternatives element-wise.
/// </para>
/// </remarks>
/// <param name="FieldName">The canonical name; see <see cref="FieldName"/>.</param>
/// <param name="Target">The column kind; see <see cref="Target"/>.</param>
/// <param name="FieldType">The data field's kind; see <see cref="FieldType"/>.</param>
/// <param name="Alternatives">The alternatives; see <see cref="Alternatives"/>.</param>
public sealed record SearchCondition(
    string FieldName,
    SearchTarget Target,
    FieldType? FieldType,
    IReadOnlyList<SearchAlternative> Alternatives)
{
    /// <summary>
    /// The canonical name: <c>metaFields.Name</c> for a page field, and exactly <c>GlobalRecordId</c> or
    /// <c>RecStatus</c> for the system targets.
    /// </summary>
    public string FieldName { get; } = SearchModelGuard.CheckFieldName(FieldName, Target);

    /// <summary>The column the condition reads.</summary>
    public SearchTarget Target { get; } = SearchModelGuard.CheckTarget(Target);

    /// <summary>
    /// The data field's kind for <see cref="SearchTarget.PageField"/>, never a label, group or unsupported kind;
    /// <see langword="null"/> for both system targets.
    /// </summary>
    public FieldType? FieldType { get; } = SearchModelGuard.CheckFieldType(Target, FieldType);

    /// <summary>At least one alternative, in the order the user wrote them.</summary>
    public IReadOnlyList<SearchAlternative> Alternatives { get; } = SearchModelGuard.CheckAlternatives(Alternatives);
}

/// <summary>The kind of one element of a <see cref="SearchOperator.Like"/> pattern.</summary>
public enum LikePatternElementKind
{
    /// <summary>
    /// A character that matches itself under the comparison's collation: one UTF-16 code unit, or a high surrogate
    /// followed by a low one, which a supplementary-character (<c>_SC</c>) collation reads as one character and any
    /// other collation as two.
    /// </summary>
    Literal,

    /// <summary><c>_</c>: any one character.</summary>
    AnyCharacter,

    /// <summary>
    /// <c>[...]</c>: one character of a set, a range or, after a leading <c>^</c>, their complement. It runs from
    /// <c>[</c> to the first <c>]</c> after it, so <c>[]</c> is a set with no member, or to the end of the pattern
    /// when no <c>]</c> follows. Inside it <c>%</c>, <c>_</c> and <c>[</c> are members.
    /// </summary>
    Set,

    /// <summary><c>%</c>: any run of characters, the empty run included.</summary>
    AnyRun,
}

/// <summary>One element of a <see cref="SearchOperator.Like"/> pattern, as <see cref="LikePatternSyntax.Tokenize"/> reads it.</summary>
/// <param name="Kind">What the element matches.</param>
/// <param name="Start">The element's first UTF-16 code unit in the pattern, 0-based.</param>
/// <param name="Length">
/// The element's length in UTF-16 code units: 1, except 2 for a surrogate-pair <see cref="LikePatternElementKind.Literal"/>
/// and the whole text of a <see cref="LikePatternElementKind.Set"/>, brackets included.
/// </param>
public readonly record struct LikePatternElement(LikePatternElementKind Kind, int Start, int Length)
{
    /// <summary>
    /// Whether the element is a literal surrogate pair, which matches one character under a supplementary-character
    /// collation and two under any other.
    /// </summary>
    public bool IsSurrogatePair => Kind == LikePatternElementKind.Literal && Length == 2;
}

/// <summary>
/// The one reading of SQL Server's <c>LIKE</c> pattern syntax with no <c>ESCAPE</c> clause, the form every Find
/// pattern is bound in. The search criteria parser reads a pattern's sets with it, and the data layer's search builder
/// reads a pattern longer than SQL Server's pattern limit with it, so that it can evaluate the pattern window by
/// window with the same meaning.
/// </summary>
/// <remarks>
/// SQL Server reads a pattern left to right: <c>%</c> is any run of characters, <c>_</c> any one character, and
/// <c>[</c> opens a set that the first <c>]</c> after it closes, or the end of the pattern when none follows, so a set
/// is never nested and <c>[]</c> holds no member. Every other code unit, <c>]</c>, <c>^</c> and <c>-</c> outside a set
/// included, matches itself, and a high surrogate followed by a low one is one literal. Every element other than
/// <c>%</c> matches exactly one character. The class holds no state and is safe to call from any thread.
/// </remarks>
public static class LikePatternSyntax
{
    // The zero-width joiner, which joins the characters on either side of it into one.
    private const char ZeroWidthJoiner = '\u200D';

    /// <summary>Splits a pattern into its elements, in pattern order, each run of <c>%</c> kept as written.</summary>
    /// <param name="pattern">The pattern, as it is bound with no <c>ESCAPE</c> clause.</param>
    /// <returns>The elements, which together cover every code unit of <paramref name="pattern"/> once.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pattern"/> is null.</exception>
    public static IReadOnlyList<LikePatternElement> Tokenize(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        var elements = new List<LikePatternElement>(pattern.Length);
        var index = 0;
        while (index < pattern.Length)
        {
            var element = pattern[index] switch
            {
                '%' => new LikePatternElement(LikePatternElementKind.AnyRun, index, 1),
                '_' => new LikePatternElement(LikePatternElementKind.AnyCharacter, index, 1),
                '[' => new LikePatternElement(LikePatternElementKind.Set, index, SetLength(pattern, index)),
                _ when char.IsSurrogatePair(pattern, index) =>
                    new LikePatternElement(LikePatternElementKind.Literal, index, 2),
                _ => new LikePatternElement(LikePatternElementKind.Literal, index, 1),
            };

            elements.Add(element);
            index += element.Length;
        }

        return elements.AsReadOnly();
    }

    /// <summary>
    /// Whether the pattern holds a <c>[...]</c> set that no SQL Server construct can evaluate: one longer than
    /// <see cref="RecordQueryRules.MaxLikePatternLength"/> code units, too long for any <c>LIKE</c> or
    /// <c>PATINDEX</c>, that is unclosed, whose meaning depends on the collation (<c>[a</c> holds <c>a</c> under
    /// <c>Latin1_General_100_CI_AS</c> and nothing under <c>Latin1_General_100_BIN2</c>), or that
    /// <see cref="TrySplitSet"/> cannot cut into sets of at most <see cref="RecordQueryRules.MaxLikePatternLength"/>
    /// - 2 code units. The search-criteria parser refuses such a pattern with a 400, and the search builder and the
    /// in-memory test store with an <see cref="ArgumentException"/>; every other pattern, of any length, is searched.
    /// A pattern within the limit never holds one.
    /// </summary>
    /// <param name="pattern">The pattern, as it is bound with no <c>ESCAPE</c> clause.</param>
    /// <returns>Whether the pattern holds such a set.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pattern"/> is null.</exception>
    public static bool HoldsUnsearchableSet(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        if (pattern.Length <= RecordQueryRules.MaxLikePatternLength || !pattern.Contains('['))
        {
            return false;
        }

        foreach (var element in Tokenize(pattern))
        {
            if (element.Kind == LikePatternElementKind.Set
                && element.Length > RecordQueryRules.MaxLikePatternLength
                && !TrySplitSet(pattern, element, RecordQueryRules.MaxLikePatternLength - 2, out _, out _))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Cuts a closed <c>[...]</c> set into consecutive pieces of its members, each written as a set of its own of at
    /// most <paramref name="maxPieceLength"/> code units, brackets included, so that a character the set's one
    /// character matches is in the set exactly when it is in one piece, or, for a negated set, in none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// How SQL Server reads a <c>-</c> and a <c>^</c> depends on the members around them and on the collation
    /// (<c>[a-c-e]</c> holds <c>-</c> under <c>Latin1_General_100_BIN2</c> but not under
    /// <c>Latin1_General_100_CI_AS</c>, and <c>[a-]</c> holds <c>-</c> and <c>a</c> under the second and nothing under
    /// the first), and a combining sequence is one character. So a set is cut only between two members where neither is
    /// <c>-</c>, the second is not <c>^</c>, which would negate the piece it starts, the two are not one surrogate pair,
    /// and the second is not a combining mark (<see cref="UnicodeCategory.NonSpacingMark"/>,
    /// <see cref="UnicodeCategory.SpacingCombiningMark"/>, <see cref="UnicodeCategory.EnclosingMark"/>, variation
    /// selectors included) and neither is the zero-width joiner U+200D. Each piece then holds the same members, ranges
    /// and literal <c>-</c> as it did inside the whole set, and no piece but the first starts where the set's members
    /// start. The cuts are placed as late as each piece's length allows.
    /// </para>
    /// <para>
    /// It fails for an unclosed set, whose meaning depends on the collation, for a set with no member, for a negated set
    /// whose first member is <c>^</c>, which a piece would read as a second negation, and for a set with no cut that
    /// leaves every piece within the length.
    /// </para>
    /// </remarks>
    /// <param name="pattern">The pattern holding the set.</param>
    /// <param name="set">The set, a <see cref="LikePatternElementKind.Set"/> element of <paramref name="pattern"/>.</param>
    /// <param name="maxPieceLength">The longest piece, in UTF-16 code units, brackets included.</param>
    /// <param name="negated">On success, whether the set is negated, so a character matches when no piece holds it.</param>
    /// <param name="pieces">On success, the pieces in member order, each a closed set without <c>^</c>; otherwise empty.</param>
    /// <returns>Whether the set was cut.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pattern"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="set"/> is not a set element of <paramref name="pattern"/>.</exception>
    public static bool TrySplitSet(
        string pattern,
        LikePatternElement set,
        int maxPieceLength,
        out bool negated,
        out IReadOnlyList<string> pieces)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        if (set.Kind != LikePatternElementKind.Set
            || set.Start < 0
            || set.Length < 1
            || set.Start + set.Length > pattern.Length
            || pattern[set.Start] != '[')
        {
            throw new ArgumentException("The element is not a set of the pattern.", nameof(set));
        }

        negated = false;
        pieces = [];
        var close = set.Start + set.Length - 1;
        if (set.Length < 2 || pattern[close] != ']')
        {
            return false;
        }

        var start = set.Start + 1;
        var isNegated = pattern[start] == '^';
        if (isNegated)
        {
            start++;
        }

        // The members run from start to the closing ']'; none, or a negated set whose members start with '^', fails.
        var room = maxPieceLength - 2;
        if (start >= close || room < 1 || (isNegated && pattern[start] == '^'))
        {
            return false;
        }

        var cut = new List<string>();
        while (close - start > room)
        {
            var end = start + room;
            while (end > start && !IsSetCut(pattern, end))
            {
                end--;
            }

            if (end == start)
            {
                return false;
            }

            cut.Add(string.Concat("[", pattern.AsSpan(start, end - start), "]"));
            start = end;
        }

        cut.Add(string.Concat("[", pattern.AsSpan(start, close - start), "]"));
        negated = isNegated;
        pieces = cut.AsReadOnly();
        return true;
    }

    // Whether a set's members may be cut between the code units at index - 1 and index: neither is '-', the second is
    // not '^', they are not one surrogate pair, the second does not combine with the first, and neither is the
    // zero-width joiner.
    private static bool IsSetCut(string pattern, int index)
    {
        var left = pattern[index - 1];
        var right = pattern[index];
        if (left is '-' or ZeroWidthJoiner || right is '-' or '^' or ZeroWidthJoiner
            || (char.IsHighSurrogate(left) && char.IsLowSurrogate(right)))
        {
            return false;
        }

        return CharUnicodeInfo.GetUnicodeCategory(pattern, index) is not (UnicodeCategory.NonSpacingMark
            or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark);
    }

    // The length of the set opened at start: through the first ']' after the '[', or to the end of the pattern.
    private static int SetLength(string pattern, int start)
    {
        var close = pattern.IndexOf(']', start + 1);
        return (close >= 0 ? close + 1 : pattern.Length) - start;
    }
}

/// <summary>
/// The invariants behind the search records' constructors. They sit outside the records because inside
/// <see cref="SearchCondition"/> the simple name <c>FieldType</c> binds to its nullable property, not to the enum.
/// The checks are structural only: which value kind each field kind takes is the search criteria parser's rule,
/// over the field-type mapper it uses, and is not repeated here.
/// </summary>
file static class SearchModelGuard
{
    private const string GlobalRecordIdColumn = "GlobalRecordId";
    private const string RecStatusColumn = "RecStatus";

    internal static SearchOperator CheckOperator(SearchOperator @operator)
    {
        if (!Enum.IsDefined(@operator))
        {
            throw new ArgumentOutOfRangeException(
                nameof(SearchAlternative.Operator), "The search operator is not a defined member.");
        }

        return @operator;
    }

    internal static FieldValue? CheckValue(SearchOperator @operator, FieldValue? value)
    {
        // Messages name the operator only: the value is field data and must not reach an error message.
        if (@operator == SearchOperator.IsNull)
        {
            if (value.HasValue)
            {
                throw new ArgumentException(
                    "An IsNull alternative carries no value.", nameof(SearchAlternative.Value));
            }

            return null;
        }

        if (value is not { IsNull: false } present)
        {
            throw new ArgumentException(
                $"A {@operator} alternative needs a value.", nameof(SearchAlternative.Value));
        }

        if (@operator is SearchOperator.Like or SearchOperator.PrefixEqual255 && present.Kind != FieldValueKind.Text)
        {
            throw new ArgumentException(
                $"A {@operator} alternative needs a text value.", nameof(SearchAlternative.Value));
        }

        return present;
    }

    internal static string CheckFieldName(string fieldName, SearchTarget target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName, nameof(SearchCondition.FieldName));

        var systemColumn = target switch
        {
            SearchTarget.BaseGlobalRecordId => GlobalRecordIdColumn,
            SearchTarget.BaseRecStatus => RecStatusColumn,
            _ => null,
        };

        if (systemColumn is not null && !string.Equals(fieldName, systemColumn, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"A {target} condition must name the {systemColumn} column exactly.",
                nameof(SearchCondition.FieldName));
        }

        return fieldName;
    }

    internal static SearchTarget CheckTarget(SearchTarget target)
    {
        if (!Enum.IsDefined(target))
        {
            throw new ArgumentOutOfRangeException(
                nameof(SearchCondition.Target), "The search target is not a defined member.");
        }

        return target;
    }

    internal static FieldType? CheckFieldType(SearchTarget target, FieldType? fieldType)
    {
        if (target != SearchTarget.PageField)
        {
            if (fieldType.HasValue)
            {
                throw new ArgumentException(
                    $"A {target} condition reads a system column and has no field type.",
                    nameof(SearchCondition.FieldType));
            }

            return null;
        }

        // Labels, groups and unsupported fields have no column the search could read.
        if (fieldType is not { } dataFieldType
            || !Enum.IsDefined(dataFieldType)
            || dataFieldType is FieldType.Label or FieldType.Group or FieldType.Unsupported)
        {
            throw new ArgumentException(
                "A page-field condition needs the field type of a data field.", nameof(SearchCondition.FieldType));
        }

        return dataFieldType;
    }

    internal static IReadOnlyList<SearchAlternative> CheckAlternatives(IReadOnlyList<SearchAlternative> alternatives)
    {
        ArgumentNullException.ThrowIfNull(alternatives, nameof(SearchCondition.Alternatives));

        if (alternatives.Count == 0)
        {
            throw new ArgumentException(
                "A search condition needs at least one alternative.", nameof(SearchCondition.Alternatives));
        }

        foreach (var alternative in alternatives)
        {
            if (alternative is null)
            {
                throw new ArgumentException(
                    "A search condition's alternatives must not contain null.", nameof(SearchCondition.Alternatives));
            }
        }

        // A copy, so a caller that keeps the list it passed cannot change the condition afterwards.
        return [.. alternatives];
    }
}
