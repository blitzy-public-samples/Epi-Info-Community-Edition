using EpiInfo.Domain.Metadata;

namespace EpiInfo.Domain.Storage;

/// <summary>
/// The SQL permissions the configured login holds on one data table of a form, as the permission reader
/// reads them with <c>HAS_PERMS_BY_NAME(&lt;table&gt;, 'OBJECT', 'INSERT')</c> and <c>'UPDATE'</c>.
/// </summary>
/// <param name="TableName">
/// The metadata-derived table name exactly as <see cref="FormMetadata.BaseTableName"/> or
/// <see cref="PageMetadata.TableName"/> gives it; never <see langword="null"/>, empty or whitespace.
/// </param>
/// <param name="CanInsert">Whether the login may insert rows into the table.</param>
/// <param name="CanUpdate">Whether the login may update rows of the table.</param>
public sealed record TablePermission(string TableName, bool CanInsert, bool CanUpdate)
{
    private readonly string tableName = ValidateTableName(TableName);

    /// <summary>The table name; never <see langword="null"/>, empty or whitespace.</summary>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The value is empty or whitespace.</exception>
    public string TableName
    {
        get => tableName;
        // Validated here as well as in the constructor, so a `with` expression cannot bypass the rule.
        init => tableName = ValidateTableName(value);
    }

    private static string ValidateTableName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, nameof(TableName));
        return value;
    }
}

/// <summary>
/// The per-table permissions of one form, and the single rule that derives from them whether a record of the
/// form may be inserted, updated or soft-deleted.
/// </summary>
/// <remarks>
/// <para>
/// The permission reader supplies one entry for the base table, then one per page table in
/// <c>metaPages.Position</c> order. Permissions are read on every write and never cached, so an instance
/// describes the login's rights at the moment it was read.
/// </para>
/// <para>
/// <see cref="CanInsert"/>, <see cref="CanUpdate"/> and <see cref="CanDelete"/> are the only place the rule is
/// written. <c>FormService</c> maps them to the form-definition flags <c>canInsert</c>, <c>canUpdate</c> and
/// <c>canDelete</c> it passes to <c>FormDefinitionFactory.Create</c>, and <c>RecordService</c> applies the same
/// methods to each write, with no copy of the rule:
/// </para>
/// <list type="bullet">
/// <item><description><c>POST</c> needs <see cref="CanInsert"/>.</description></item>
/// <item><description>
/// <c>PUT</c> needs <see cref="CanUpdate"/>; then, after the locked read, the store requires
/// <see cref="CanInsertInto"/> for each page whose row is missing, before any statement that changes data.
/// </description></item>
/// <item><description><c>DELETE</c> needs <see cref="CanDelete"/>.</description></item>
/// <item><description>A denied write is a 403 and nothing is written.</description></item>
/// </list>
/// <para>
/// Every derivation fails closed: a table with no entry denies, so an incomplete reader result can never grant
/// a write. The methods are pure and perform no I/O. The instance is immutable, so it can be shared across
/// threads.
/// </para>
/// </remarks>
/// <param name="Tables">
/// One entry per data table of the form; never <see langword="null"/> and holding no <see langword="null"/>
/// entry. The entries are copied, so later changes to the supplied list cannot change a decision.
/// </param>
public sealed record FormPermissions(IReadOnlyList<TablePermission> Tables)
{
    private readonly IReadOnlyList<TablePermission> tables = ValidateTables(Tables);

    /// <summary>The per-table entries, in the order the reader supplied them.</summary>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The value holds a <see langword="null"/> entry.</exception>
    public IReadOnlyList<TablePermission> Tables
    {
        get => tables;
        // Validated here as well as in the constructor, so a `with` expression cannot bypass the rule.
        init => tables = ValidateTables(value);
    }

    /// <summary>
    /// Finds the entry for <paramref name="tableName"/>, ignoring case, so <c>foodhistory3</c> resolves to the
    /// entry <c>FoodHistory3</c>.
    /// </summary>
    /// <param name="tableName">A base or page table name.</param>
    /// <returns>The first matching entry, or <see langword="null"/> when there is none.</returns>
    public TablePermission? Find(string tableName)
    {
        foreach (TablePermission table in tables)
        {
            if (string.Equals(table.TableName, tableName, StringComparison.OrdinalIgnoreCase))
            {
                return table;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether the login may insert a row into <paramref name="tableName"/>. The store checks it inside an update
    /// for each page whose row the locked read found missing, before any statement that changes data.
    /// </summary>
    /// <param name="tableName">A page table name.</param>
    /// <returns><see langword="true"/> only when the table has an entry that allows INSERT.</returns>
    public bool CanInsertInto(string tableName) => Find(tableName)?.CanInsert == true;

    /// <summary>
    /// Whether a record of <paramref name="form"/> may be inserted: INSERT on the base table and on every page
    /// table, as legacy Epi Info computes <c>canInsertRecords</c>.
    /// </summary>
    /// <param name="form">The form's metadata, which names its base and page tables.</param>
    /// <returns><see langword="true"/> only when every one of those tables has an entry that allows INSERT.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="form"/> is <see langword="null"/>.</exception>
    public bool CanInsert(FormMetadata form)
    {
        ArgumentNullException.ThrowIfNull(form);
        return AllTablesAllow(form, static table => table.CanInsert);
    }

    /// <summary>
    /// Whether a record of <paramref name="form"/> may be updated: UPDATE on the base table and on every page
    /// table.
    /// </summary>
    /// <param name="form">The form's metadata, which names its base and page tables.</param>
    /// <returns><see langword="true"/> only when every one of those tables has an entry that allows UPDATE.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="form"/> is <see langword="null"/>.</exception>
    public bool CanUpdate(FormMetadata form)
    {
        ArgumentNullException.ThrowIfNull(form);

        // Page UPDATE is always checked. Legacy checked it only when INSERT was granted everywhere, so a login
        // without INSERT could be offered a save the page tables then refused; that is a recorded reconciliation.
        return AllTablesAllow(form, static table => table.CanUpdate);
    }

    /// <summary>
    /// Whether a record of <paramref name="form"/> may be soft-deleted: UPDATE on the base table, because a soft
    /// delete changes only the base row.
    /// </summary>
    /// <param name="form">The form's metadata, which names its base table.</param>
    /// <returns><see langword="true"/> only when the base table has an entry that allows UPDATE.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="form"/> is <see langword="null"/>.</exception>
    public bool CanDelete(FormMetadata form)
    {
        ArgumentNullException.ThrowIfNull(form);
        return Find(form.BaseTableName)?.CanUpdate == true;
    }

    private bool AllTablesAllow(FormMetadata form, Func<TablePermission, bool> allows)
    {
        if (!Allows(form.BaseTableName, allows))
        {
            return false;
        }

        foreach (PageMetadata page in form.Pages)
        {
            if (!Allows(page.TableName, allows))
            {
                return false;
            }
        }

        return true;
    }

    // A table with no entry denies, so the rule fails closed.
    private bool Allows(string tableName, Func<TablePermission, bool> allows) =>
        Find(tableName) is { } table && allows(table);

    // Exception messages are fixed text and never echo the supplied value.
    private static IReadOnlyList<TablePermission> ValidateTables(IReadOnlyList<TablePermission> value)
    {
        ArgumentNullException.ThrowIfNull(value, nameof(Tables));

        var copy = new TablePermission[value.Count];
        for (int i = 0; i < copy.Length; i++)
        {
            copy[i] = value[i] ?? throw new ArgumentException("A table permission entry is null.", nameof(Tables));
        }

        return Array.AsReadOnly(copy);
    }
}
