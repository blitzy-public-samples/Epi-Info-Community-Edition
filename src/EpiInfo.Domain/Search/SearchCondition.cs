using EpiInfo.Domain.Forms;
using EpiInfo.Domain.Values;

namespace EpiInfo.Domain.Search;

/// <summary>
/// How a search alternative compares a column with its value. The six comparisons are the ones the legacy
/// Find dialog reads from a leading prefix of the typed value (<c>Epi.Windows.Enter/Dialogs/FindRecordDialog.cs</c>,
/// lines 708-771). The other three are the predicate shapes the legacy query picks by field kind and value
/// (<c>Epi.Core/Data/Services/CollectedDataProvider.cs</c>, lines 1973-2249).
/// </summary>
public enum SearchOperator
{
    /// <summary>
    /// The column equals the value. This is the comparison when the value carries no prefix, and the only one for
    /// the field kinds whose prefix legacy ignores or rejects: text family, option, checkbox and <c>RecStatus</c>.
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
    /// criterion, and for an alternative that is only a prefix, left empty once the prefix is stripped.
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
/// <param name="FieldName">The canonical column name; see <see cref="FieldName"/>.</param>
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
