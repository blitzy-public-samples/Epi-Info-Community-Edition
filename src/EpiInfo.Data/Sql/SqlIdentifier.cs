using EpiInfo.Data.Metadata;

namespace EpiInfo.Data.Sql;

/// <summary>
/// Quotes SQL Server identifiers for record, search and permission SQL. Every table and column name that reaches
/// SQL text passes through here, and only as a <see cref="TableSchema"/> or <see cref="ColumnSchema"/>, so a name
/// the metadata reader did not return cannot be expressed.
/// </summary>
/// <remarks>
/// <para>
/// A name is quoted as <c>[</c>, the name with every <c>]</c> doubled, then <c>]</c>; nothing else changes.
/// Spaces, quotes, a leading <c>[</c> and reserved words such as <c>Order</c>, <c>Select</c> or <c>Group</c> are
/// all safe once bracketed, so <c>a]b</c> becomes <c>[a]]b]</c> and <c>[x]</c> becomes <c>[[x]]]</c>.
/// </para>
/// <para>
/// Legacy Epi Info's <c>InsertInEscape</c> (<c>Epi.Data.SqlServer/SqlDatabase.cs:2094-2107</c>) adds <c>[</c> only
/// when the text does not already start with it and <c>]</c> only when it does not already end with it, and never
/// doubles an embedded <c>]</c>. This type always wraps and always doubles, so an embedded <c>]</c> can never end
/// the identifier early. The two agree on every seeded name; the difference is recorded in
/// <c>docs/parity-notes.md</c>.
/// </para>
/// <para>
/// Values are never identifiers: every value is a command parameter. Schema qualification is not offered here; a
/// builder that needs <c>dbo.</c> writes that fixed literal itself. The type holds no state and is thread-safe.
/// </para>
/// </remarks>
public static class SqlIdentifier
{
    // The longest identifier SQL Server accepts: sysname is nvarchar(128).
    private const int MaxIdentifierLength = 128;

    // Fixed messages: the redaction policy keeps identifiers out of exception text.
    private const string EmptyNameMessage = "An identifier must not be empty.";
    private const string NameTooLongMessage =
        "An identifier must not be longer than 128 characters, the SQL Server sysname limit.";

    /// <summary>Quotes the name of a table the metadata reader returned.</summary>
    /// <param name="table">The catalogue table, such as a form's base table or a page table.</param>
    /// <returns>The bracket-quoted table name, for example <c>[CaseReport]</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="table"/> is null.</exception>
    /// <exception cref="ArgumentException">The name is empty or longer than 128 characters.</exception>
    public static string Quote(TableSchema table)
    {
        ArgumentNullException.ThrowIfNull(table);

        return QuoteName(table.Name);
    }

    /// <summary>Quotes the name of a column the metadata reader returned.</summary>
    /// <param name="column">The catalogue column of a base, page or code table.</param>
    /// <returns>The bracket-quoted column name, for example <c>[LastName]</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="column"/> is null.</exception>
    /// <exception cref="ArgumentException">The name is empty or longer than 128 characters.</exception>
    public static string Quote(ColumnSchema column)
    {
        ArgumentNullException.ThrowIfNull(column);

        return QuoteName(column.Name);
    }

    /// <summary>Quotes a raw name: <c>[</c>, the name with every <c>]</c> doubled, then <c>]</c>.</summary>
    /// <param name="name">The identifier text.</param>
    /// <returns>The bracket-quoted name.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> is empty or longer than 128 characters. The message never contains the name.
    /// </exception>
    /// <remarks>
    /// Internal so that <c>SqlIdentifierTests</c> can reach it through <c>InternalsVisibleTo</c>, and for no other
    /// caller. SQL builders never call it: every dynamic identifier enters SQL through <see cref="Quote(TableSchema)"/>
    /// or <see cref="Quote(ColumnSchema)"/>, which <c>IdentifierProvenanceTests</c> proves by reflection and
    /// <c>RecordCommandIdentifierProvenanceTests</c> by the recorded commands of saves made through the API. A public
    /// overload taking a raw string would let a name the reader never returned reach SQL.
    /// </remarks>
    internal static string QuoteName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (name.Length == 0)
        {
            throw new ArgumentException(EmptyNameMessage, nameof(name));
        }

        if (name.Length > MaxIdentifierLength)
        {
            throw new ArgumentException(NameTooLongMessage, nameof(name));
        }

        return string.Concat("[", name.Replace("]", "]]", StringComparison.Ordinal), "]");
    }
}
