using System.Collections.ObjectModel;
using EpiInfo.Domain.Metadata;

namespace EpiInfo.Domain.Storage;

/// <summary>
/// The SQL permissions the configured login holds on one data table of a form, as the permission reader
/// reads them with <c>HAS_PERMS_BY_NAME(&lt;table&gt;, 'OBJECT', 'INSERT')</c> and <c>'UPDATE'</c>.
/// </summary>
/// <param name="TableName">
/// The name of the table the entry describes; the SQL Server store writes the catalogue spelling of the table it
/// confirmed. Lookups match it ordinally, so entries whose names differ only by letter case describe different
/// tables. Never <see langword="null"/>, empty or whitespace.
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
/// <item><description>
/// <c>POST</c> needs <see cref="CanInsert"/>; then, once the insert holds the form's lock and before any statement
/// that changes data, the store re-checks <see cref="CanInsert"/> from its own permission read inside the
/// transaction.
/// </description></item>
/// <item><description>
/// <c>PUT</c> needs <see cref="CanUpdate"/>; then, after the locked read and the required gate and before any
/// statement that changes data, the store re-checks <see cref="CanUpdate"/> and requires <see cref="CanInsertInto"/>
/// for each page whose row is missing, from its own permission read inside the transaction.
/// </description></item>
/// <item><description>
/// <c>DELETE</c> needs <see cref="CanDelete"/>; then, after the locked read finds an active record and before any
/// statement that changes data, the store re-checks <see cref="CanDelete"/> from its own permission read of the base
/// table inside the transaction.
/// </description></item>
/// <item><description>A denied write is a 403 and nothing is written.</description></item>
/// </list>
/// <para>
/// Table names compare ordinally, never ignoring case. A form's table name, as <see cref="FormMetadata.BaseTableName"/>
/// or <see cref="PageMetadata.TableName"/> spells it, reaches an entry only when the entry carries exactly that name,
/// or through <see cref="ResolvedTableNames"/>, where the store records the entry of the table it confirmed for that
/// name. A decision is therefore made only for the table the entry describes, and a spelling the store did not
/// confirm reaches no entry.
/// </para>
/// <para>
/// Every derivation fails closed: a table with no entry denies, and so does a name that reaches two entries or one
/// that <see cref="ResolvedTableNames"/> maps away from an entry carrying that very name, so an incomplete or
/// conflicting result can never grant a write. The methods are pure and perform no I/O. The instance is immutable,
/// so it can be shared across threads.
/// </para>
/// </remarks>
/// <param name="Tables">
/// One entry per data table of the form; never <see langword="null"/> and holding no <see langword="null"/>
/// entry. The entries are copied, so later changes to the supplied list cannot change a decision.
/// </param>
public sealed record FormPermissions(IReadOnlyList<TablePermission> Tables)
{
    private readonly IReadOnlyList<TablePermission> tables = ValidateTables(Tables);

    private readonly IReadOnlyDictionary<string, string> resolvedTableNames =
        ReadOnlyDictionary<string, string>.Empty;

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
    /// For each form table name the store was asked about, spelled exactly as <see cref="FormMetadata.BaseTableName"/>
    /// or <see cref="PageMetadata.TableName"/> gives it, the <see cref="TablePermission.TableName"/> of the entry for
    /// the table the store confirmed for that name. Empty by default, so every name then has to match an entry
    /// exactly. Keys compare ordinally; a name the store could not confirm, because the table is missing or the name
    /// is ambiguous, has no key.
    /// </summary>
    /// <remarks>
    /// The SQL Server store names each entry with the catalogue spelling of the table it confirmed, which can differ
    /// in letter case from the metadata name the form uses; this map is how that metadata name reaches the entry.
    /// The pairs are copied, so later changes to the supplied dictionary cannot change a decision.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// A key or value is <see langword="null"/>, empty or whitespace, or two keys are the same name.
    /// </exception>
    public IReadOnlyDictionary<string, string> ResolvedTableNames
    {
        get => resolvedTableNames;
        // Validated here, so neither an object initializer nor a `with` expression can bypass the rule.
        init => resolvedTableNames = ValidateResolvedTableNames(value);
    }

    /// <summary>
    /// Finds the one entry for <paramref name="tableName"/> by exact identity: the name
    /// <see cref="ResolvedTableNames"/> maps it to, or else the name itself, compared ordinally with each entry's
    /// <see cref="TablePermission.TableName"/>. Letter case is never ignored, so a spelling the store did not confirm
    /// finds nothing.
    /// </summary>
    /// <param name="tableName">
    /// A base or page table name, exactly as <see cref="FormMetadata.BaseTableName"/> or
    /// <see cref="PageMetadata.TableName"/> gives it.
    /// </param>
    /// <returns>
    /// The single matching entry; or <see langword="null"/> when <paramref name="tableName"/> is
    /// <see langword="null"/>, when no entry matches, when several do, or when <see cref="ResolvedTableNames"/> maps
    /// the name to another entry while an entry carries the name itself. Each <see langword="null"/> denies.
    /// </returns>
    public TablePermission? Find(string tableName)
    {
        if (tableName is null)
        {
            return null;
        }

        string identity = resolvedTableNames.TryGetValue(tableName, out string? resolved) ? resolved : tableName;
        bool remapped = !string.Equals(identity, tableName, StringComparison.Ordinal);

        TablePermission? match = null;
        foreach (TablePermission table in tables)
        {
            // An entry that carries the name itself while the store resolved that name to another table: the two
            // disagree about which table the name means, so neither decides.
            if (remapped && string.Equals(table.TableName, tableName, StringComparison.Ordinal))
            {
                return null;
            }

            if (string.Equals(table.TableName, identity, StringComparison.Ordinal))
            {
                // Two entries for one table conflict, so neither decides.
                if (match is not null)
                {
                    return null;
                }

                match = table;
            }
        }

        return match;
    }

    /// <summary>
    /// Whether the login may insert a row into <paramref name="tableName"/>. The store checks it inside an update,
    /// from its own permission read in the transaction, for each page whose row the locked read found missing, before
    /// any statement that changes data.
    /// </summary>
    /// <param name="tableName">
    /// A page table name, exactly as <see cref="PageMetadata.TableName"/> gives it; <see cref="Find"/> resolves it.
    /// </param>
    /// <returns><see langword="true"/> only when <see cref="Find"/> returns an entry that allows INSERT.</returns>
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

    // A table with no entry, or with conflicting entries, denies, so the rule fails closed.
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

    // Copies the pairs into an ordinal dictionary. Exception messages are fixed text and never echo a name.
    private static IReadOnlyDictionary<string, string> ValidateResolvedTableNames(
        IReadOnlyDictionary<string, string> value)
    {
        ArgumentNullException.ThrowIfNull(value, nameof(ResolvedTableNames));

        var copy = new Dictionary<string, string>(value.Count, StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> pair in value)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
            {
                throw new ArgumentException(
                    "A resolved table name is null, empty or whitespace.",
                    nameof(ResolvedTableNames));
            }

            if (!copy.TryAdd(pair.Key, pair.Value))
            {
                throw new ArgumentException(
                    "Two resolved table names have the same key.",
                    nameof(ResolvedTableNames));
            }
        }

        return new ReadOnlyDictionary<string, string>(copy);
    }
}
