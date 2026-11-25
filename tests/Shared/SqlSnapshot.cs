using System.Collections;
using System.Collections.ObjectModel;
using System.Data;
using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;

namespace EpiInfo.Tests.Shared;

/// <summary>
/// One row of a <see cref="SqlSnapshot"/> table: the typed value of every column, keyed by the row's
/// <c>GlobalRecordId</c>.
/// </summary>
/// <remarks>
/// Values keep the CLR type SqlClient returns: <see cref="int"/> for <c>int</c>, <see cref="short"/> for
/// <c>smallint</c>, <see cref="byte"/> for <c>tinyint</c>, <see cref="double"/> for <c>float</c>,
/// <see cref="DateTime"/> for <c>datetime</c>, <see cref="bool"/> for <c>bit</c> and <see cref="string"/> for
/// <c>nvarchar</c> and <c>ntext</c>. Any other type is kept as returned. A database null is
/// <see langword="null"/>, never <see cref="DBNull"/>.
/// </remarks>
public sealed class SnapshotRow
{
    private readonly SnapshotColumns _columns;
    private readonly object?[] _values;

    internal SnapshotRow(SnapshotColumns columns, string globalRecordId, object?[] values)
    {
        _columns = columns;
        _values = values;
        GlobalRecordId = globalRecordId;
        Values = new SnapshotRowValues(columns, values);
    }

    /// <summary>The table the row was read from, for example <c>FoodHistory2</c>.</summary>
    public string Table => _columns.Table;

    /// <summary>The row's <c>GlobalRecordId</c>, the key of the row within its table.</summary>
    public string GlobalRecordId { get; }

    /// <summary>
    /// Column name to typed value, enumerated in the order the reader returned the columns. Names match
    /// ordinally (case-sensitive), as the table defines them.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Values { get; }

    /// <summary>The typed value of <paramref name="column"/>; <see langword="null"/> for a database null.</summary>
    /// <param name="column">The column name, matched ordinally.</param>
    /// <exception cref="KeyNotFoundException">The table has no such column; the message names both.</exception>
    public object? this[string column] => _values[_columns.OrdinalOf(column)];

    /// <summary>Names the row by table and <c>GlobalRecordId</c>, without its values.</summary>
    public override string ToString() => Table + " " + GlobalRecordId;

    internal object? ValueAt(int ordinal) => _values[ordinal];
}

/// <summary>Whether a <see cref="SnapshotRowChange"/> is a row that appeared or one that disappeared.</summary>
public enum SnapshotChangeKind
{
    /// <summary>The row exists only in the later snapshot.</summary>
    Added,

    /// <summary>The row exists only in the earlier snapshot.</summary>
    Removed,
}

/// <summary>A row present in only one of two compared snapshots.</summary>
/// <param name="Table">The table holding the row.</param>
/// <param name="GlobalRecordId">The row's key.</param>
/// <param name="Kind">Whether the row was added or removed.</param>
/// <param name="Row">The row as the snapshot that holds it read it.</param>
public sealed record SnapshotRowChange(string Table, string GlobalRecordId, SnapshotChangeKind Kind, SnapshotRow Row);

/// <summary>One column of a row present in both compared snapshots whose value differs between them.</summary>
/// <param name="Table">The table holding the row.</param>
/// <param name="GlobalRecordId">The row's key.</param>
/// <param name="Column">The column whose value changed.</param>
/// <param name="OldValue">The typed value in the earlier snapshot.</param>
/// <param name="NewValue">The typed value in the later snapshot.</param>
public sealed record SnapshotCellChange(
    string Table,
    string GlobalRecordId,
    string Column,
    object? OldValue,
    object? NewValue);

/// <summary>
/// A difference between the column sets two snapshots read for one table. The schema of
/// <c>EpiRehearsal</c> is unchanged by contract, so any entry is a failure signal.
/// </summary>
/// <param name="Table">The table whose columns differ.</param>
/// <param name="ColumnsAdded">Columns only the later snapshot read, in its column order.</param>
/// <param name="ColumnsRemoved">Columns only the earlier snapshot read, in its column order.</param>
public sealed record SnapshotSchemaChange(
    string Table,
    IReadOnlyList<string> ColumnsAdded,
    IReadOnlyList<string> ColumnsRemoved);

/// <summary>
/// Every difference <see cref="SqlSnapshot.Compare"/> found between two snapshots. Each list is ordered by
/// the table order of <see cref="SqlSnapshot.Tables"/>, then <c>GlobalRecordId</c> (ordinal), then column order.
/// </summary>
/// <remarks>
/// No tolerance is built in: callers hold the exclusive <c>DatabaseWriteLock</c>, so the diff holds exactly
/// the changes the test made and an assertion can require every other change to be absent.
/// </remarks>
public sealed class SnapshotDiff
{
    private const int ListingLimit = 100;

    internal SnapshotDiff(
        IReadOnlyList<SnapshotRowChange> addedRows,
        IReadOnlyList<SnapshotRowChange> removedRows,
        IReadOnlyList<SnapshotCellChange> changedCells,
        IReadOnlyList<SnapshotSchemaChange> schemaChanges)
    {
        AddedRows = addedRows;
        RemovedRows = removedRows;
        ChangedCells = changedCells;
        SchemaChanges = schemaChanges;
    }

    /// <summary>Rows only the later snapshot holds.</summary>
    public IReadOnlyList<SnapshotRowChange> AddedRows { get; }

    /// <summary>Rows only the earlier snapshot holds.</summary>
    public IReadOnlyList<SnapshotRowChange> RemovedRows { get; }

    /// <summary>Columns, present in both snapshots, of rows present in both, whose values differ.</summary>
    public IReadOnlyList<SnapshotCellChange> ChangedCells { get; }

    /// <summary>Tables whose column sets differ; empty whenever the schema is unchanged.</summary>
    public IReadOnlyList<SnapshotSchemaChange> SchemaChanges { get; }

    /// <summary>Whether no row was added or removed, no cell changed and no column set changed.</summary>
    public bool IsEmpty =>
        AddedRows.Count == 0 && RemovedRows.Count == 0 && ChangedCells.Count == 0 && SchemaChanges.Count == 0;

    /// <summary>The changes of one table.</summary>
    /// <param name="table">One of <see cref="SqlSnapshot.Tables"/>, matched ordinally.</param>
    /// <exception cref="ArgumentException"><paramref name="table"/> is not a snapshot table.</exception>
    public SnapshotDiff Changes(string table)
    {
        SqlSnapshot.RequireKnownTable(table, nameof(table));
        return new SnapshotDiff(
            Filter(AddedRows, change => Same(change.Table, table)),
            Filter(RemovedRows, change => Same(change.Table, table)),
            Filter(ChangedCells, change => Same(change.Table, table)),
            Filter(SchemaChanges, change => Same(change.Table, table)));
    }

    /// <summary>
    /// The changes of one record across every table. Schema changes are table-wide rather than per record, so
    /// they are all kept: a filtered diff never hides one.
    /// </summary>
    /// <param name="globalRecordId">The record's <c>GlobalRecordId</c>, matched ordinally.</param>
    public SnapshotDiff ChangesFor(string globalRecordId)
    {
        ArgumentNullException.ThrowIfNull(globalRecordId);
        return new SnapshotDiff(
            Filter(AddedRows, change => Same(change.GlobalRecordId, globalRecordId)),
            Filter(RemovedRows, change => Same(change.GlobalRecordId, globalRecordId)),
            Filter(ChangedCells, change => Same(change.GlobalRecordId, globalRecordId)),
            SchemaChanges);
    }

    /// <summary>
    /// A multi-line listing for assertion messages: a summary line, then schema changes (<c>!</c>), added rows
    /// (<c>+</c>), removed rows (<c>-</c>) and changed cells (<c>~</c>, old value then new), capped at the first
    /// 100 entries. Values are test data and appear in full; no connection detail ever does.
    /// </summary>
    public override string ToString()
    {
        int total = SchemaChanges.Count + AddedRows.Count + RemovedRows.Count + ChangedCells.Count;
        if (total == 0)
        {
            return "No changes.";
        }

        var text = new StringBuilder();
        text.Append(
            CultureInfo.InvariantCulture,
            $"{total} change(s): {AddedRows.Count} row(s) added, {RemovedRows.Count} row(s) removed, " +
            $"{ChangedCells.Count} cell(s) changed, {SchemaChanges.Count} schema change(s).");

        int listed = 0;
        foreach (string entry in Entries())
        {
            if (listed == ListingLimit)
            {
                break;
            }

            text.AppendLine().Append(entry);
            listed++;
        }

        if (total > listed)
        {
            text.AppendLine().Append(CultureInfo.InvariantCulture, $"… and {total - listed} more");
        }

        return text.ToString();
    }

    private IEnumerable<string> Entries()
    {
        foreach (SnapshotSchemaChange change in SchemaChanges)
        {
            yield return $"! {change.Table} schema: columns added [{string.Join(", ", change.ColumnsAdded)}], " +
                $"columns removed [{string.Join(", ", change.ColumnsRemoved)}]";
        }

        foreach (SnapshotRowChange change in AddedRows)
        {
            yield return $"+ {change.Table} {change.GlobalRecordId}";
        }

        foreach (SnapshotRowChange change in RemovedRows)
        {
            yield return $"- {change.Table} {change.GlobalRecordId}";
        }

        foreach (SnapshotCellChange change in ChangedCells)
        {
            string oldValue = SqlSnapshot.FormatValue(change.OldValue);
            string newValue = SqlSnapshot.FormatValue(change.NewValue);

            // Values of different CLR types can print alike (1 as int and as short), so the types are named then.
            if (change.OldValue is { } oldObject && change.NewValue is { } newObject &&
                oldObject.GetType() != newObject.GetType())
            {
                oldValue += $" ({oldObject.GetType().Name})";
                newValue += $" ({newObject.GetType().Name})";
            }

            yield return $"~ {change.Table} {change.GlobalRecordId} {change.Column}: {oldValue} -> {newValue}";
        }
    }

    private static bool Same(string left, string right) => string.Equals(left, right, StringComparison.Ordinal);

    private static ReadOnlyCollection<T> Filter<T>(IReadOnlyList<T> source, Func<T, bool> keep) =>
        source.Where(keep).ToList().AsReadOnly();
}

/// <summary>
/// A read-only copy of every column of every row of the five data tables of the seeded forms
/// (<see cref="Tables"/>), keyed by <c>GlobalRecordId</c>, and the one comparison procedure for stored-data
/// equivalence (SC-3) and read equivalence (SC-4, <c>DATA-01</c>).
/// </summary>
/// <remarks>
/// <para>
/// A capture runs one fixed <c>SELECT *</c> per table. No value or name from outside this class enters the
/// SQL, so no parameter is needed; nothing is written, no schema object is touched and nothing is cached
/// between captures. A snapshot is immutable once captured and safe to share across threads.
/// </para>
/// <para>
/// Callers hold the exclusive <c>DatabaseWriteLock</c> from the first capture to the last, so
/// <see cref="Compare"/> reports exactly the changes the test made.
/// </para>
/// </remarks>
public sealed class SqlSnapshot
{
    private const int CommandTimeoutSeconds = 120;
    private const string KeyColumn = "GlobalRecordId";

    // One fixed statement per table, in table order. The names are literals of this file, never input.
    private static readonly (string Table, string Query)[] Queries =
    [
        ("CaseReport", "SELECT * FROM [dbo].[CaseReport];"),
        ("CaseReport1", "SELECT * FROM [dbo].[CaseReport1];"),
        ("FoodHistory", "SELECT * FROM [dbo].[FoodHistory];"),
        ("FoodHistory2", "SELECT * FROM [dbo].[FoodHistory2];"),
        ("FoodHistory3", "SELECT * FROM [dbo].[FoodHistory3];"),
    ];

    private readonly Dictionary<string, SnapshotTable> _tables;

    private SqlSnapshot(Dictionary<string, SnapshotTable> tables)
    {
        _tables = tables;
    }

    /// <summary>
    /// The captured tables in comparison order: <c>CaseReport</c>, <c>CaseReport1</c>, <c>FoodHistory</c>,
    /// <c>FoodHistory2</c>, <c>FoodHistory3</c>.
    /// </summary>
    public static IReadOnlyList<string> Tables { get; } = Array.AsReadOnly(Queries.Select(query => query.Table).ToArray());

    /// <summary>
    /// Captures every table on a new connection to the test database, which it closes afterwards.
    /// </summary>
    /// <param name="cancellationToken">Cancels the open and the reads.</param>
    /// <exception cref="TestDatabaseUnavailableException">The test database is not configured or not reachable.</exception>
    /// <exception cref="InvalidOperationException">A table holds a <c>NULL</c> or duplicate <c>GlobalRecordId</c>.</exception>
    /// <remarks>
    /// Reads as <see cref="CaptureAsync(SqlConnection, CancellationToken)"/> does, so the same lock rule applies.
    /// </remarks>
    public static async Task<SqlSnapshot> CaptureAsync(CancellationToken cancellationToken = default)
    {
        await using SqlConnection connection = await TestDatabase.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await CaptureCoreAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Captures every table on <paramref name="connection"/>, which stays open and owned by the caller.
    /// </summary>
    /// <param name="connection">An open connection with no pending local transaction.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <exception cref="ArgumentException"><paramref name="connection"/> is not open.</exception>
    /// <exception cref="InvalidOperationException">A table holds a <c>NULL</c> or duplicate <c>GlobalRecordId</c>.</exception>
    /// <remarks>
    /// Each table is read with a plain <c>SELECT *</c> at the session's isolation level, <c>READ COMMITTED</c>
    /// by default, with a 120-second command timeout. A capture therefore blocks while another session holds a
    /// lock that conflicts with a shared lock, for example the <c>TABLOCKX</c> that <c>TransactionalSaveTests</c>
    /// holds on <c>FoodHistory3</c>. Never capture while a second connection of the same test holds such a lock:
    /// the capture would wait for it until the timeout.
    /// </remarks>
    public static Task<SqlSnapshot> CaptureAsync(SqlConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.State != ConnectionState.Open)
        {
            throw new ArgumentException(
                $"A snapshot needs an open connection; this one is {connection.State}.", nameof(connection));
        }

        return CaptureCoreAsync(connection, cancellationToken);
    }

    /// <summary>
    /// Compares two snapshots table by table: rows only in <paramref name="after"/> are added, rows only in
    /// <paramref name="before"/> are removed, and for rows in both every column present in both whose values
    /// differ is a changed cell. Column-set differences are schema changes.
    /// </summary>
    /// <remarks>
    /// Equality is exact, with no tolerance: the same CLR type and <see cref="object.Equals(object?)"/>, so
    /// <see cref="string"/> is ordinal and case-sensitive and <see cref="DateTime"/> compares ticks; a
    /// <see cref="double"/> or <see cref="float"/> compares bit patterns; a <see cref="byte"/> array compares
    /// content; <see langword="null"/> equals only <see langword="null"/>.
    /// </remarks>
    /// <param name="before">The earlier snapshot.</param>
    /// <param name="after">The later snapshot.</param>
    public static SnapshotDiff Compare(SqlSnapshot before, SqlSnapshot after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var addedRows = new List<SnapshotRowChange>();
        var removedRows = new List<SnapshotRowChange>();
        var changedCells = new List<SnapshotCellChange>();
        var schemaChanges = new List<SnapshotSchemaChange>();

        foreach (string table in Tables)
        {
            SnapshotTable earlier = before._tables[table];
            SnapshotTable later = after._tables[table];
            IReadOnlyList<SharedColumn> shared = CompareColumns(table, earlier.Columns, later.Columns, schemaChanges);

            // Both row lists are sorted by GlobalRecordId (ordinal), so one merge pass pairs them in key order.
            ReadOnlyCollection<SnapshotRow> earlierRows = earlier.Rows;
            ReadOnlyCollection<SnapshotRow> laterRows = later.Rows;
            int earlierIndex = 0;
            int laterIndex = 0;
            while (earlierIndex < earlierRows.Count || laterIndex < laterRows.Count)
            {
                int order = earlierIndex == earlierRows.Count ? 1
                    : laterIndex == laterRows.Count ? -1
                    : string.CompareOrdinal(earlierRows[earlierIndex].GlobalRecordId, laterRows[laterIndex].GlobalRecordId);

                if (order < 0)
                {
                    SnapshotRow removed = earlierRows[earlierIndex++];
                    removedRows.Add(new SnapshotRowChange(table, removed.GlobalRecordId, SnapshotChangeKind.Removed, removed));
                }
                else if (order > 0)
                {
                    SnapshotRow added = laterRows[laterIndex++];
                    addedRows.Add(new SnapshotRowChange(table, added.GlobalRecordId, SnapshotChangeKind.Added, added));
                }
                else
                {
                    SnapshotRow oldRow = earlierRows[earlierIndex++];
                    SnapshotRow newRow = laterRows[laterIndex++];
                    foreach (SharedColumn column in shared)
                    {
                        object? oldValue = oldRow.ValueAt(column.EarlierOrdinal);
                        object? newValue = newRow.ValueAt(column.LaterOrdinal);
                        if (!ValuesEqual(oldValue, newValue))
                        {
                            changedCells.Add(new SnapshotCellChange(table, oldRow.GlobalRecordId, column.Name, oldValue, newValue));
                        }
                    }
                }
            }
        }

        return new SnapshotDiff(
            addedRows.AsReadOnly(),
            removedRows.AsReadOnly(),
            changedCells.AsReadOnly(),
            schemaChanges.AsReadOnly());
    }

    /// <summary>The number of rows captured for <paramref name="table"/>.</summary>
    /// <param name="table">One of <see cref="Tables"/>, matched ordinally.</param>
    /// <exception cref="ArgumentException"><paramref name="table"/> is not a snapshot table.</exception>
    public int RowCount(string table) => Table(table).Rows.Count;

    /// <summary>The captured row of <paramref name="table"/> with <paramref name="globalRecordId"/>, or <see langword="null"/>.</summary>
    /// <param name="table">One of <see cref="Tables"/>, matched ordinally.</param>
    /// <param name="globalRecordId">The row's key, matched ordinally.</param>
    /// <exception cref="ArgumentException"><paramref name="table"/> is not a snapshot table.</exception>
    public SnapshotRow? Row(string table, string globalRecordId)
    {
        SnapshotTable captured = Table(table);
        ArgumentNullException.ThrowIfNull(globalRecordId);
        return captured.ById.GetValueOrDefault(globalRecordId);
    }

    /// <summary>Every captured row of <paramref name="table"/>, ordered by <c>GlobalRecordId</c> (ordinal).</summary>
    /// <param name="table">One of <see cref="Tables"/>, matched ordinally.</param>
    /// <exception cref="ArgumentException"><paramref name="table"/> is not a snapshot table.</exception>
    public IReadOnlyCollection<SnapshotRow> Rows(string table) => Table(table).Rows;

    internal static void RequireKnownTable(string table, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(table, parameterName);
        if (!Tables.Contains(table, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                $"'{table}' is not a snapshot table; the tables are {string.Join(", ", Tables)}.", parameterName);
        }
    }

    internal static bool ValuesEqual(object? left, object? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        if (left.GetType() != right.GetType())
        {
            return false;
        }

        return left switch
        {
            // double.Equals treats 0.0 and -0.0 as equal; a stored value must match bit for bit.
            double number => BitConverter.DoubleToInt64Bits(number) == BitConverter.DoubleToInt64Bits((double)right),
            float number => BitConverter.SingleToInt32Bits(number) == BitConverter.SingleToInt32Bits((float)right),
            byte[] bytes => bytes.AsSpan().SequenceEqual((byte[])right),
            _ => left.Equals(right),
        };
    }

    internal static string FormatValue(object? value) => value switch
    {
        null => "NULL",
        string text => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'",
        bool flag => flag ? "true" : "false",
        DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        float number => number.ToString("R", CultureInfo.InvariantCulture),
        byte[] bytes => "0x" + Convert.ToHexString(bytes),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private SnapshotTable Table(string table)
    {
        RequireKnownTable(table, nameof(table));
        return _tables[table];
    }

    private static async Task<SqlSnapshot> CaptureCoreAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        var tables = new Dictionary<string, SnapshotTable>(StringComparer.Ordinal);
        foreach ((string table, string query) in Queries)
        {
            tables.Add(table, await ReadTableAsync(connection, table, query, cancellationToken).ConfigureAwait(false));
        }

        return new SqlSnapshot(tables);
    }

    private static async Task<SnapshotTable> ReadTableAsync(
        SqlConnection connection,
        string table,
        string query,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(query, connection)
        {
            CommandTimeout = CommandTimeoutSeconds,
        };
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var names = new string[reader.FieldCount];
        for (int ordinal = 0; ordinal < names.Length; ordinal++)
        {
            names[ordinal] = reader.GetName(ordinal);
        }

        var columns = new SnapshotColumns(table, names);
        if (!columns.TryGetOrdinal(KeyColumn, out int keyOrdinal))
        {
            throw new InvalidOperationException($"Table {table} has no {KeyColumn} column, so its rows cannot be keyed.");
        }

        var rows = new List<SnapshotRow>();
        var byId = new Dictionary<string, SnapshotRow>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var values = new object?[names.Length];
            for (int ordinal = 0; ordinal < values.Length; ordinal++)
            {
                object value = reader.GetValue(ordinal);
                values[ordinal] = value is DBNull ? null : value;
            }

            string globalRecordId = values[keyOrdinal] switch
            {
                string id => id,
                null => throw new InvalidOperationException(
                    $"Table {table} holds a row whose {KeyColumn} is NULL, so the row cannot be keyed."),
                object other => throw new InvalidOperationException(
                    $"Table {table} returned {KeyColumn} {FormatValue(other)} as {other.GetType().Name}, not as a string."),
            };

            var row = new SnapshotRow(columns, globalRecordId, values);
            if (!byId.TryAdd(globalRecordId, row))
            {
                // Each record has one row per table; a repeated id breaks the stored-data contract itself.
                throw new InvalidOperationException(
                    $"Table {table} holds {KeyColumn} '{globalRecordId}' more than once.");
            }

            rows.Add(row);
        }

        rows.Sort(static (left, right) => string.CompareOrdinal(left.GlobalRecordId, right.GlobalRecordId));
        return new SnapshotTable(columns, rows.AsReadOnly(), byId);
    }

    private static IReadOnlyList<SharedColumn> CompareColumns(
        string table,
        SnapshotColumns earlier,
        SnapshotColumns later,
        List<SnapshotSchemaChange> schemaChanges)
    {
        var shared = new List<SharedColumn>(earlier.Names.Count);
        var removed = new List<string>();
        for (int ordinal = 0; ordinal < earlier.Names.Count; ordinal++)
        {
            string name = earlier.Names[ordinal];
            if (later.TryGetOrdinal(name, out int laterOrdinal))
            {
                shared.Add(new SharedColumn(name, ordinal, laterOrdinal));
            }
            else
            {
                removed.Add(name);
            }
        }

        var added = later.Names.Where(name => !earlier.TryGetOrdinal(name, out _)).ToList();
        if (added.Count > 0 || removed.Count > 0)
        {
            schemaChanges.Add(new SnapshotSchemaChange(table, added.AsReadOnly(), removed.AsReadOnly()));
        }

        return shared;
    }

    private readonly record struct SharedColumn(string Name, int EarlierOrdinal, int LaterOrdinal);

    private sealed class SnapshotTable(
        SnapshotColumns columns,
        ReadOnlyCollection<SnapshotRow> rows,
        Dictionary<string, SnapshotRow> byId)
    {
        public SnapshotColumns Columns { get; } = columns;

        // Ordered by GlobalRecordId (ordinal), which Compare's merge pass relies on.
        public ReadOnlyCollection<SnapshotRow> Rows { get; } = rows;

        public IReadOnlyDictionary<string, SnapshotRow> ById { get; } = byId;
    }
}

/// <summary>
/// The column names one capture of a table read, in reader order, shared by every row of that capture.
/// </summary>
internal sealed class SnapshotColumns
{
    private readonly Dictionary<string, int> _ordinals;

    public SnapshotColumns(string table, string[] names)
    {
        Table = table;
        Names = Array.AsReadOnly(names);
        _ordinals = new Dictionary<string, int>(names.Length, StringComparer.Ordinal);
        for (int ordinal = 0; ordinal < names.Length; ordinal++)
        {
            if (!_ordinals.TryAdd(names[ordinal], ordinal))
            {
                throw new InvalidOperationException($"Table {table} returned column {names[ordinal]} more than once.");
            }
        }
    }

    public string Table { get; }

    public ReadOnlyCollection<string> Names { get; }

    public bool TryGetOrdinal(string column, out int ordinal) => _ordinals.TryGetValue(column, out ordinal);

    public int OrdinalOf(string column)
    {
        ArgumentNullException.ThrowIfNull(column);
        if (_ordinals.TryGetValue(column, out int ordinal))
        {
            return ordinal;
        }

        throw new KeyNotFoundException(
            $"Snapshot table {Table} has no column {column}; column names match ordinally (case-sensitive).");
    }
}

/// <summary>
/// The read-only column-to-value view of one <see cref="SnapshotRow"/>, enumerated in reader order.
/// </summary>
internal sealed class SnapshotRowValues(SnapshotColumns columns, object?[] values) : IReadOnlyDictionary<string, object?>
{
    public int Count => values.Length;

    public IEnumerable<string> Keys => columns.Names;

    // A read-only wrapper, so a caller cannot cast back to the array and change a captured value.
    public IEnumerable<object?> Values => Array.AsReadOnly(values);

    public object? this[string key] => values[columns.OrdinalOf(key)];

    public bool ContainsKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return columns.TryGetOrdinal(key, out _);
    }

    public bool TryGetValue(string key, out object? value)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (columns.TryGetOrdinal(key, out int ordinal))
        {
            value = values[ordinal];
            return true;
        }

        value = null;
        return false;
    }

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
    {
        for (int ordinal = 0; ordinal < values.Length; ordinal++)
        {
            yield return new KeyValuePair<string, object?>(columns.Names[ordinal], values[ordinal]);
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

