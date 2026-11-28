using System.Diagnostics.CodeAnalysis;
using EpiInfo.Domain.Forms;
using EpiInfo.Domain.Metadata;
using EpiInfo.Domain.Records;
using EpiInfo.Domain.Search;
using EpiInfo.Domain.Validation;
using EpiInfo.Domain.Values;

namespace EpiInfo.Domain.Storage;

/// <summary>
/// The persistence port of the web data-entry server: every metadata read, record read and record write the API
/// performs, as one operation-level method each. The API reaches data only through this interface and never handles
/// a SQL client type, a connection or a transaction. <c>EpiInfo.Data.SqlServerDataStore</c> implements it against
/// SQL Server, and the API tests' <c>FakeDataStore</c> implements it in memory.
/// </summary>
/// <remarks>
/// <para>
/// <b>Failures.</b> A store-specific failure surfaces only as <see cref="DataStoreUnavailableException"/>, which the
/// API maps to 503, or as <see cref="DataStoreWriteException"/>, which it maps to 500. Each carries the failed
/// <c>Operation</c>, its <see cref="WriteOutcome"/> as <c>Outcome</c>, and the SQL error number when there is one as
/// <c>SqlErrorNumber</c>. No SQL client exception escapes an implementation. When
/// <c>cancellationToken</c> is cancelled because the client aborted the request, the failure is an
/// <see cref="OperationCanceledException"/>, which is never wrapped. An argument that breaks a method's documented
/// contract, such as a <c>values</c> key that is not a data field of the form, is a programming error raised as
/// <see cref="ArgumentException"/> before any base- or page-table command is sent, and is never wrapped either.
/// </para>
/// <para>
/// <b>Classification</b> of SQL failures into those two exceptions is owned by the data layer's
/// <c>SqlErrorClassifier</c>. Exception messages are fixed text and never carry data: no SQL text, parameter value,
/// field value, identifier or connection string.
/// </para>
/// <para>
/// <b>Threading.</b> Implementations are stateless and thread-safe, so one instance serves every request. Each call
/// uses its own connection, and nothing is kept for a later request, metadata included: a form's metadata, code tables
/// and column types are read again by every request that needs them.
/// </para>
/// <para>
/// <b>Identifiers.</b> Field names are canonical <c>metaFields.Name</c> values, and results key them ordinally, so
/// fields whose names differ only by letter case keep their own entries. A field name in an argument names the field
/// <see cref="DataFieldSet.TryGet"/> resolves it to: the exact spelling, otherwise the one field equal to it ignoring
/// case; an ambiguous name names none. Every table and column name a store uses comes from its own catalogue read,
/// never from a caller's string; in <c>EpiInfo.Data</c> that is the reader-built <c>TableSchema</c>. The
/// <see cref="FormMetadata"/> a record method receives identifies the form. A record read takes its tables and columns
/// from the catalogue read that produced that very instance in the same request, or reads them again for any other
/// instance; a write confirms inside its transaction that the metadata and catalogue rows that read returned still
/// stand, and reads them again there when they do not or for any other instance.
/// </para>
/// <para>
/// <b>Cancellation.</b> Every method takes a cancellation token as its last, required parameter, so no caller can omit
/// it. Callers pass the request's abort token, except the API's health check, which passes
/// <see cref="CancellationToken.None"/> to <see cref="PingAsync"/> so that one caller's abort cannot end a ping that
/// other runs of the check share.
/// </para>
/// <para>
/// <b>Reads.</b> <see cref="CountAsync"/>, <see cref="GetAtPositionAsync"/>, <see cref="GetByIdAsync"/>,
/// <see cref="GetSetPositionAsync"/>, <see cref="ListAsync"/>, <see cref="SearchAsync"/> and the metadata methods
/// issue only <c>SELECT</c> statements and never run check code, so loading a record changes nothing in the database
/// (scenario <c>DATA-01</c>). They share the record-set rules of <see cref="RecordQueryRules"/>:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Selected set:</b> <c>RecStatus = 1</c>, or every row when <c>includeDeleted</c> is <see langword="true"/>
/// (<see cref="RecordQueryRules.IsInSelectedSet"/>).
/// </description></item>
/// <item><description><b>Ordering:</b> <c>UniqueKey</c> ascending.</description></item>
/// <item><description>
/// <b>Intrinsic coordinates:</b> the <see cref="RecordDto"/> that <see cref="GetAtPositionAsync"/> and
/// <see cref="GetByIdAsync"/> return carries the record's own <see cref="RecordDto.Position"/> and
/// <see cref="RecordDto.Count"/>. Their set is the one <see cref="RecordQueryRules.IntrinsicIncludesDeleted"/>
/// chooses for the record's <c>RecStatus</c>: the active set for 1, every row otherwise. The position is the number
/// of rows of that set whose <c>UniqueKey</c> is at most the record's, the legacy record-number formula, and the count
/// is the number of rows of that set. The caller's deleted toggle plays no part in them. The <c>RecStatus</c>, the
/// position, the count and the page values come from one consistent read: in the SQL store, one statement whose
/// base-table read holds its shared locks until the statement completes (<c>HOLDLOCK</c>), so no base row it read can
/// change before it returns, and the three values describe one committed state even under a concurrent soft delete.
/// </description></item>
/// <item><description>
/// <b>Page rows</b> of record hydration, that is <see cref="GetAtPositionAsync"/>, <see cref="GetByIdAsync"/> and
/// the response snapshots of the writes: each page table is <c>LEFT JOIN</c>ed to the base table on
/// <c>GlobalRecordId</c>, so a base row without a row on some page is still read, with <see cref="FieldValue.Null"/>
/// for every field of that page, as the legacy per-page load nulls them. The list projection of
/// <see cref="ListAsync"/> <c>LEFT JOIN</c>s only the pages holding a grid column, so such a record is still listed,
/// with <see cref="FieldValue.Null"/> for the grid columns of the missing page.
/// </description></item>
/// <item><description>
/// <b>Exempt from the page-row rule:</b> <see cref="SearchAsync"/>, whose one statement <c>INNER JOIN</c>s the base
/// table to every page table, so only a base row with a row on every page can match; and the base-only reads
/// <see cref="CountAsync"/> and <see cref="GetSetPositionAsync"/>, which read the base table alone, so a record
/// missing a page row is still counted and placed.
/// </description></item>
/// </list>
/// <para>
/// <b>Writes.</b> <see cref="InsertAsync"/>, <see cref="UpdateAsync"/> and <see cref="SoftDeleteAsync"/> each run in
/// one transaction at <c>READ COMMITTED</c>, so a failure before commit leaves no partial record. They share this
/// contract:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Values</b> have already passed the API's <c>RecordValidator</c>: they are keyed by canonical name and limited to
/// writable data fields. Read-only, unsupported and system columns never appear in them, and callers can never
/// supply <c>GlobalRecordId</c>, <c>UniqueKey</c>, <c>RecStatus</c> or a stamp column.
/// </description></item>
/// <item><description>
/// <b>Inside the transaction</b> run the locked read and the gates, every write, and the read of the response
/// snapshot (base and page rows with the intrinsic position and count, which follow the same intrinsic rule as the
/// record reads and come from one consistent read with the rows, as theirs do) together with the construction of its
/// <see cref="RecordDto"/> through <see cref="RecordDto.From"/>. A failure in any of them rolls the write back.
/// </description></item>
/// <item><description>
/// <b>Return value:</b> a <see cref="RecordWriteStatus.Success"/> from <see cref="InsertAsync"/> or
/// <see cref="UpdateAsync"/>, or <see cref="SoftDeleteStatus.Deleted"/> from <see cref="SoftDeleteAsync"/>, means the
/// transaction committed, and the API's <c>RecordService</c> then records <see cref="WriteOutcome.Written"/> for the
/// request. Every other result means nothing was written: the transaction rolled back, or no data statement was
/// sent.
/// </description></item>
/// <item><description>
/// <b>Permissions:</b> a write given non-null permissions decides them again inside its transaction, from the store's
/// own permission read for the tables that transaction resolved, after the locks that order it and before its first
/// data statement, and answers <see cref="RecordWriteStatus.Forbidden"/> or <see cref="SoftDeleteStatus.Forbidden"/>
/// with nothing written when they deny it. The caller's earlier <see cref="GetPermissionsAsync"/> result only turns
/// that check on, so a grant revoked between the two reads is a refusal, never a failed data statement.
/// </description></item>
/// <item><description>
/// <b>Failures</b> carry the <see cref="WriteOutcome"/> the store can prove:
/// <see cref="WriteOutcome.NotWritten"/> for a failure before any data statement was sent, for a rollback that
/// completed without error, and for a deadlock the server aborted (1205); <see cref="WriteOutcome.Unknown"/> when a
/// data statement was sent and neither a commit nor a completed rollback can be confirmed, because the commit failed
/// or did not return, or the rollback itself failed; <see cref="WriteOutcome.Written"/> when the commit is confirmed
/// and a later step inside the store still fails, such as releasing the transaction or the connection. A store must
/// report such a post-commit failure as <see cref="WriteOutcome.Written"/>, never as
/// <see cref="WriteOutcome.Unknown"/> or <see cref="WriteOutcome.NotWritten"/>.
/// </description></item>
/// <item><description>
/// <b>After the commit</b> the store keeps its work to a minimum: it makes no database call, computes nothing from
/// data and logs no value, so the only failure left to it there is one of those release steps, which it reports as
/// <see cref="WriteOutcome.Written"/>.
/// </description></item>
/// </list>
/// </remarks>
public interface IDataStore
{
    /// <summary>
    /// The longest check-code program, in UTF-16 code units, that <see cref="GetCheckCodeAsync"/> serves whole and the
    /// check-code engine parses: 1,048,576, the same bound as the 1 MiB request body cap.
    /// </summary>
    /// <remarks>
    /// <c>metaViews.CheckCode</c> is an unbounded <c>ntext</c> column, and both the form definition and every field
    /// event read and parse it, so this bound keeps a stored program from costing each of those requests more than a
    /// request body can. The seeded programs, 443 and 2,428 code units long, are far inside it.
    /// </remarks>
    public const int MaxCheckCodeLength = 1_048_576;

    /// <summary>
    /// Lists every form: one entry per <c>metaViews</c> row, ordered by <c>ViewId</c> ascending, with
    /// <see cref="FormSummary.PageCount"/> the number of that view's <c>metaPages</c> rows.
    /// </summary>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>Every form, possibly none.</returns>
    Task<IReadOnlyList<FormSummary>> ListFormsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Reads the metadata of one form, fresh on every call: its <c>metaViews</c> row, its <c>metaPages</c> rows
    /// (ordered by <c>Position</c>) and every <c>metaFields</c> row of the view joined to its
    /// <c>metaFieldTypes.Name</c> and to its page-table column, whose type and length come from the database
    /// catalogue.
    /// </summary>
    /// <param name="viewId">A <c>metaViews.ViewId</c>.</param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>
    /// The form's metadata, or <see langword="null"/> when <paramref name="viewId"/> is not in <c>metaViews</c>, which
    /// the API reports as 404.
    /// </returns>
    Task<FormMetadata?> GetFormMetadataAsync(int viewId, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the options of every Legal Values field of <paramref name="form"/>: one entry per data field that
    /// <see cref="FieldTypeMapper"/> maps to <see cref="FieldType.LegalValues"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A field's options are the rows of its code table <see cref="FieldMetadata.SourceTableName"/>, column
    /// <see cref="FieldMetadata.TextColumnName"/>, each as <c>new FieldOption(FieldValue.FromText(text), text)</c>.
    /// They keep the order in which the unsorted read returns them, as legacy reads code tables, so no order is
    /// promised beyond the database's own; they are sorted by text only when <see cref="FieldMetadata.Sort"/> is set,
    /// as legacy sorts the list. A usable code table is a table of the catalogue with the shape legacy gives code
    /// tables: its name starts with <c>code</c>, in any letter case, and its columns are all character columns, none
    /// of them an identity column. It has no <c>GlobalRecordId</c> column, so it is no form's base or page table, and
    /// it holds a character column of the field's <see cref="FieldMetadata.TextColumnName"/>. A field without a usable
    /// code table, because either name is missing, the table is not in the catalogue or is no code table, or the
    /// column is not a character column of it, gets an empty list, so no other table's rows are published as options.
    /// </para>
    /// <para>
    /// Option fields are not served here: their labels come from <c>metaFields.List</c>, which
    /// <see cref="FieldTypeMapper.ParseOptionItems"/> parses.
    /// </para>
    /// </remarks>
    /// <param name="form">The form's metadata, as <see cref="GetFormMetadataAsync"/> returned it.</param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>
    /// The options keyed by canonical field name, with keys compared ordinally, so fields whose names differ only by
    /// letter case each have their own entry; the shape <see cref="FormDefinitionFactory.Create"/> takes.
    /// </returns>
    Task<IReadOnlyDictionary<string, IReadOnlyList<FieldOption>>> GetOptionsAsync(
        FormMetadata form,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads the check-code program of a form: the <c>metaViews.CheckCode</c> text, unchanged when it is at most
    /// <see cref="MaxCheckCodeLength"/> UTF-16 code units long.
    /// </summary>
    /// <remarks>
    /// A longer stored program is returned as its first <see cref="MaxCheckCodeLength"/> + 1 code units, so the caller
    /// can tell that it is over the limit while the rest of it never reaches the process. The check-code engine
    /// reports such a program as over its budget and runs none of it.
    /// </remarks>
    /// <param name="viewId">A <c>metaViews.ViewId</c>.</param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>
    /// The program text, at most <see cref="MaxCheckCodeLength"/> + 1 code units long, or <see langword="null"/> when
    /// the column is <c>NULL</c> or the view is unknown. Callers treat <see langword="null"/> as an empty program and
    /// resolve an unknown form to 404 through <see cref="GetFormMetadataAsync"/> first.
    /// </returns>
    Task<string?> GetCheckCodeAsync(int viewId, CancellationToken cancellationToken);

    /// <summary>Counts the records of the selected set.</summary>
    /// <param name="form">The form's metadata.</param>
    /// <param name="includeDeleted">
    /// <see langword="false"/> for the active set (<c>RecStatus = 1</c>); <see langword="true"/> for every row.
    /// </param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>The number of base-table rows in the selected set; 0 for a form with none.</returns>
    Task<int> CountAsync(FormMetadata form, bool includeDeleted, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the record at a 1-based position of the selected set, ordered by <c>UniqueKey</c>, with every page row
    /// and its intrinsic position and count.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The row is the one at offset <see cref="RecordQueryRules.PositionToOffset"/> of that order, a 64-bit value the
    /// SQL store binds as <c>bigint</c>, fetching one row.
    /// </para>
    /// <para>
    /// <paramref name="includeDeleted"/> selects the row only. Its position and count follow the intrinsic rule of
    /// the interface remarks and come from the same consistent read as the row, so an active record at position q of
    /// the set with every row carries its active-set position, which is smaller when deleted rows precede it.
    /// </para>
    /// </remarks>
    /// <param name="form">The form's metadata.</param>
    /// <param name="position">The 1-based position.</param>
    /// <param name="includeDeleted">Whether the selected set holds every row rather than the active set.</param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>
    /// The record with its intrinsic position and count, built with <see cref="RecordDto.From"/>; or
    /// <see langword="null"/> when <paramref name="position"/> is outside 1 to the selected-set count, which the API
    /// reports as 404.
    /// </returns>
    Task<RecordDto?> GetAtPositionAsync(
        FormMetadata form,
        int position,
        bool includeDeleted,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads one record by its <c>GlobalRecordId</c>, with every page row and its intrinsic position and count.
    /// Deleted records are included.
    /// </summary>
    /// <remarks>
    /// The position and count follow the intrinsic rule of the interface remarks and come from the same consistent
    /// read as the row.
    /// </remarks>
    /// <param name="form">The form's metadata.</param>
    /// <param name="globalRecordId">The record's base-table <c>GlobalRecordId</c>.</param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>
    /// The record with its intrinsic position and count, built with <see cref="RecordDto.From"/>; or
    /// <see langword="null"/> when the id is not in the base table, which the API reports as 404.
    /// </returns>
    Task<RecordDto?> GetByIdAsync(FormMetadata form, string globalRecordId, CancellationToken cancellationToken);

    /// <summary>
    /// Places a record in the caller's selected set, for the navigator's "[n] of N" display.
    /// </summary>
    /// <remarks>
    /// Only the base table is read, so a record missing a page row is still placed. These coordinates differ from the
    /// intrinsic ones a <see cref="RecordDto"/> carries whenever the toggle selects a set other than the record's own.
    /// The record's <c>RecStatus</c>, which decides its membership, its position and the count come from one
    /// consistent read, as the intrinsic coordinates do, so a concurrent soft delete can never yield a position for a
    /// record outside the set.
    /// </remarks>
    /// <param name="form">The form's metadata.</param>
    /// <param name="globalRecordId">The record's base-table <c>GlobalRecordId</c>.</param>
    /// <param name="includeDeleted">Whether the selected set holds every row rather than the active set.</param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>
    /// <see langword="null"/> when the id is not in the base table, which the API reports as 404. Otherwise
    /// <see cref="NavigationPosition.Count"/> is the selected-set count and <see cref="NavigationPosition.Position"/>
    /// is the record's position in that set, or <see langword="null"/> when the record is outside it.
    /// </returns>
    Task<NavigationPosition?> GetSetPositionAsync(
        FormMetadata form,
        string globalRecordId,
        bool includeDeleted,
        CancellationToken cancellationToken);

    // ListAsync and SearchAsync deliberately take no grid-column argument: the store derives the columns from the
    // form with RecordQueryRules, the one owner of that rule, so no column name a store queries comes from a caller.
    /// <summary>
    /// Reads one page of the records list: the selected set, filtered by the grid filter, ordered by
    /// <c>UniqueKey</c> and paged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The grid columns are the ones <see cref="RecordQueryRules.GetGridColumns(FormMetadata)"/> selects for
    /// <paramref name="form"/>; the store derives them itself, so every column it names comes from its own catalogue
    /// read.
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <see cref="RecordListQuery.Filter"/>, when not <see langword="null"/>, keeps the rows in which any grid column
    /// contains it, ignoring case and independent of the database collation, with the filter text passed as a
    /// parameter and its pattern characters matched literally, whatever its length: the SQL Server store matches a
    /// filter whose contains pattern exceeds SQL Server's <c>LIKE</c> pattern limit through a long-pattern program in
    /// the same statement, with the same meaning. <see langword="null"/> applies no filter.
    /// </description></item>
    /// <item><description>
    /// Rows come from the selected set of <see cref="RecordListQuery.IncludeDeleted"/> and are paged at the 64-bit
    /// <see cref="RecordListQuery.Offset"/> for <see cref="RecordListQuery.PageSize"/> rows.
    /// </description></item>
    /// <item><description>
    /// <b>Consistency:</b> the items and the total come from one consistent read of the filtered set, so they always
    /// describe the same set: in the SQL store, one statement that counts and pages the set and holds the shared locks
    /// of its base-table and page-table reads until it completes (<c>HOLDLOCK</c>), reading each page row only after
    /// its base row, so no row either read can change before the other has read it, even under a concurrent soft
    /// delete or a change to a filtered field. A deadlock that chooses the read as its victim fails it as unavailable,
    /// never with an inconsistent page.
    /// </description></item>
    /// </list>
    /// </remarks>
    /// <param name="form">The form's metadata.</param>
    /// <param name="query">
    /// The coerced list parameters, whose paging bounds the API has already checked.
    /// </param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>
    /// A page whose <see cref="RecordPage.Items"/> are <see cref="RecordSummary"/> values holding exactly the grid
    /// columns, in grid order; whose <see cref="RecordPage.Total"/> counts the whole filtered set; and whose
    /// <see cref="RecordPage.Page"/> and <see cref="RecordPage.PageSize"/> echo <paramref name="query"/>. A valid page
    /// past the end has no items and the correct total.
    /// </returns>
    Task<RecordPage> ListAsync(FormMetadata form, RecordListQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Finds the records matching the parsed Find criteria, following the legacy Find semantics as the target
    /// reconciles them.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description>
    /// One statement <c>INNER JOIN</c>s the base table to every page table, so only base rows with a row on every page
    /// can match, which is the result set legacy Find produced after it hydrated its matches.
    /// </description></item>
    /// <item><description>
    /// <paramref name="conditions"/> combine with AND, and the alternatives of one condition with OR. Every value is a
    /// parameter.
    /// </description></item>
    /// <item><description>
    /// Rows are limited to the selected set of <paramref name="includeDeleted"/>, and
    /// <paramref name="excludeGlobalRecordId"/> is removed before <see cref="SearchResponse.Total"/> is counted and
    /// before the cap applies.
    /// </description></item>
    /// <item><description>
    /// Results are ordered by <c>UniqueKey</c> and capped at <see cref="RecordQueryRules.SearchCap"/>. Each result's
    /// position is its row number over the selected set by <c>UniqueKey</c>, numbered before the criteria filter, so it
    /// equals the record's navigation position.
    /// </description></item>
    /// <item><description>
    /// Each result's membership in the selected set, its position and its <c>RecStatus</c> come from one consistent
    /// read: in the SQL store, one statement whose base-table read holds its shared locks until the statement completes
    /// (<c>HOLDLOCK</c>), so no base row it read can change before it returns, and the three describe one committed
    /// state even under a concurrent soft delete.
    /// </description></item>
    /// <item><description>
    /// Each result holds the grid columns of <see cref="RecordQueryRules.GetGridColumns(FormMetadata)"/>, which the
    /// store derives itself, and the response is built with <see cref="SearchResponse.Create"/>.
    /// </description></item>
    /// </list>
    /// </remarks>
    /// <param name="form">The form's metadata.</param>
    /// <param name="conditions">
    /// The parsed criteria, at most one per column; never empty, because the search criteria parser rejects an empty
    /// list before the store is called. They may hold any number of alternatives: the SQL Server store binds a large
    /// set in packed form, a few array parameters per condition, so the statement stays within SQL Server's parameter
    /// limit with the same matches. A <see cref="SearchOperator.Like"/> pattern may have any length: the SQL Server
    /// store evaluates one longer than <see cref="RecordQueryRules.MaxLikePatternLength"/> code units, SQL Server's
    /// <c>LIKE</c> pattern limit, window by window with the same meaning.
    /// </param>
    /// <param name="includeDeleted">Whether the selected set holds every row rather than the active set.</param>
    /// <param name="excludeGlobalRecordId">
    /// The <c>GlobalRecordId</c> of the record open in the client, which is left out of the results, or
    /// <see langword="null"/> to leave nothing out.
    /// </param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>
    /// The capped results with the uncapped match count; no match gives an empty list and a total of 0.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="conditions"/> hold a <see cref="SearchOperator.Like"/> pattern with a <c>[...]</c> set longer
    /// than <see cref="RecordQueryRules.MaxLikePatternLength"/> code units, which no SQL Server construct can
    /// evaluate and the search criteria parser rejects with a 400; raised before any base- or page-table command is
    /// sent.
    /// </exception>
    Task<SearchResponse> SearchAsync(
        FormMetadata form,
        IReadOnlyList<SearchCondition> conditions,
        bool includeDeleted,
        string? excludeGlobalRecordId,
        CancellationToken cancellationToken);

    // A failed ping is deliberately allowed to be either false or a classified exception, not false alone: the
    // health check catches store failures itself, and the SQL Server store classifies every failure the same way
    // for every member, so callers handle both forms and no exception text can reach the health result.
    /// <summary>
    /// Checks that the database answers a trivial query, <c>SELECT 1</c>, for <c>GET /health</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A store may report a failure either by returning <see langword="false"/> or by throwing the exceptions of the
    /// interface's failure rule, which is what the SQL Server store does for every member, a missing connection string
    /// included. Cancellation of <paramref name="cancellationToken"/> propagates as
    /// <see cref="OperationCanceledException"/>.
    /// </para>
    /// <para>
    /// The caller, the API's SQL Server health check, therefore reports Unhealthy for <see langword="false"/>, for any
    /// exception the ping ends with, a cancellation included, and for a ping that has not answered within the check's
    /// bound. It never attaches the exception or its message to its result, so no exception text reaches the
    /// health-check log.
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">
    /// The token that cancels the ping. The API's health check passes <see cref="CancellationToken.None"/>, so one
    /// caller's abort cannot end a ping that other runs of the check share.
    /// </param>
    /// <returns><see langword="true"/> when the database answered.</returns>
    Task<bool> PingAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Inserts a new active record: one base row and one row on every page, then reads the response snapshot and
    /// commits, all in one transaction.
    /// </summary>
    /// <remarks>
    /// <list type="number">
    /// <item><description>
    /// When <paramref name="permissions"/> is not <see langword="null"/>, the record must pass
    /// <see cref="FormPermissions.CanInsert"/>, INSERT on the base table and every page table; otherwise roll back with
    /// <see cref="RecordWriteStatus.Forbidden"/> before any data statement. The store decides it from its own
    /// permission read inside the transaction, for the base and page tables the transaction resolved, once the
    /// transaction holds the lock that orders inserts of the form, so the decision follows the grants in force inside
    /// the transaction, not those the caller read earlier.
    /// </description></item>
    /// <item><description>
    /// The base row gets a new GUID string as <c>GlobalRecordId</c> and <c>RecStatus</c> 1. <paramref name="stamp"/>
    /// fills <c>FirstSaveLogonName</c>, <c>FirstSaveTime</c>, <c>LastSaveLogonName</c> and <c>LastSaveTime</c>, each
    /// written only when that column exists. The statement returns the new key through
    /// <c>OUTPUT INSERTED.UniqueKey</c>. <c>FKEY</c> is not named, so it keeps its default <c>NULL</c>, as legacy
    /// leaves it for a form without a foreign-key field.
    /// </description></item>
    /// <item><description>
    /// One page row per page, in <see cref="PageMetadata.Position"/> order, keyed by the same <c>GlobalRecordId</c>
    /// and holding every writable data column, <c>NULL</c> for each field absent from <paramref name="values"/>.
    /// Unsupported columns are not named, so they stay <c>NULL</c>.
    /// </description></item>
    /// <item><description>The response snapshot is read, then the transaction commits.</description></item>
    /// </list>
    /// </remarks>
    /// <param name="form">The form's metadata, as <see cref="GetFormMetadataAsync"/> returned it.</param>
    /// <param name="values">
    /// The validated, normalised values of writable data fields, keyed by canonical name; a field may be absent.
    /// </param>
    /// <param name="stamp">The save stamp of this write.</param>
    /// <param name="permissions">
    /// Non-null turns on the insert check: the caller's own earlier <see cref="GetPermissionsAsync"/> result, which
    /// never decides it, because the store reads the permissions again inside the transaction.
    /// <see langword="null"/> means no check and no permission statement, which is what callers pass while permission
    /// awareness is not delivered.
    /// </param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>
    /// <see cref="RecordWriteStatus.Success"/> with the new record as committed, with its intrinsic position and count;
    /// or <see cref="RecordWriteStatus.Forbidden"/> when the store's own permission read denied the insert and the
    /// transaction rolled back with nothing written. No other status is returned: every failure throws.
    /// </returns>
    Task<RecordWriteResult> InsertAsync(
        FormMetadata form,
        IReadOnlyDictionary<string, FieldValue> values,
        SaveStamp stamp,
        FormPermissions? permissions,
        CancellationToken cancellationToken);

    /// <summary>
    /// Updates the submitted fields of an existing record, as a delta, inside one transaction that first locks the
    /// base row and re-checks the required fields against the stored state.
    /// </summary>
    /// <remarks>
    /// <para>Steps, in this order, inside the transaction:</para>
    /// <list type="number">
    /// <item><description>
    /// Read the base row's <c>RecStatus</c> <c>WITH (UPDLOCK, ROWLOCK)</c>. A missing row rolls back with
    /// <see cref="RecordWriteStatus.NotFound"/>; <c>RecStatus</c> 0 rolls back with
    /// <see cref="RecordWriteStatus.Deleted"/>.
    /// </description></item>
    /// <item><description>Read every page row into a <see cref="StoredRecord"/>.</description></item>
    /// <item><description>
    /// Call <paramref name="requiredGate"/> with the stored record. A result that is not valid rolls back before any
    /// write, with <see cref="RecordWriteStatus.ValidationFailed"/> carrying that result. Running the gate after the
    /// locked read means two concurrent saves can never both pass it on stale state.
    /// </description></item>
    /// <item><description>
    /// When <paramref name="permissions"/> is not <see langword="null"/>, the record must pass
    /// <see cref="FormPermissions.CanUpdate"/>, UPDATE on the base table and every page table, and every page whose
    /// row the locked read found missing must pass <see cref="FormPermissions.CanInsertInto"/> for its
    /// <see cref="PageMetadata.TableName"/>; otherwise roll back with <see cref="RecordWriteStatus.Forbidden"/> before
    /// any data statement. The store decides both from its own permission read inside the transaction, for the base
    /// and page tables the transaction resolved, so the decision follows the grants in force inside the transaction,
    /// not those the caller read earlier.
    /// </description></item>
    /// <item><description>
    /// Call <paramref name="createStamp"/>, then set the base <c>LastSaveLogonName</c> and <c>LastSaveTime</c> from
    /// the stamp it returns.
    /// </description></item>
    /// <item><description>
    /// For each page holding a submitted field, in <see cref="PageMetadata.Position"/> order, update only the
    /// submitted columns. When no row is affected, insert the page row with its <c>GlobalRecordId</c> and repeat the
    /// update, as legacy does.
    /// </description></item>
    /// <item><description>Read the response snapshot, then commit.</description></item>
    /// </list>
    /// <para>
    /// An exception thrown by <paramref name="requiredGate"/> or <paramref name="createStamp"/>, and a
    /// <see langword="null"/> returned by either, rolls back and surfaces as a <see cref="DataStoreWriteException"/>
    /// with <see cref="WriteOutcome.NotWritten"/>, no SQL error number and, for an exception, that exception as its
    /// inner exception. Concurrency is last-writer-wins under the lock: for a field two saves both submitted the later
    /// commit wins, a field only one save submitted keeps that save's value, and there is no row-version check.
    /// </para>
    /// </remarks>
    /// <param name="form">The form's metadata, as <see cref="GetFormMetadataAsync"/> returned it.</param>
    /// <param name="globalRecordId">The record's base-table <c>GlobalRecordId</c>.</param>
    /// <param name="submitted">
    /// The validated, normalised values of the writable data fields the request submitted, keyed by canonical name.
    /// It may be empty, which updates only the stamps.
    /// </param>
    /// <param name="createStamp">
    /// Creates the save stamp of this write. It is invoked at most once, inside the transaction, after the locked
    /// read, the required gate and the permission check have decided that the update writes, and before its first data
    /// statement. It is never invoked when the update writes nothing: <see cref="RecordWriteStatus.NotFound"/>,
    /// <see cref="RecordWriteStatus.Deleted"/>, <see cref="RecordWriteStatus.ValidationFailed"/> or
    /// <see cref="RecordWriteStatus.Forbidden"/>. An exception from it ends the call with nothing written.
    /// </param>
    /// <param name="requiredGate">
    /// The required-field check of the effective record. The API passes
    /// <c>stored =&gt; validator.CheckRequired(stored, submitted)</c>, which overlays the submitted values on the
    /// stored ones itself.
    /// </param>
    /// <param name="permissions">
    /// Non-null turns on the update and missing-page insert check: the caller's own earlier
    /// <see cref="GetPermissionsAsync"/> result, which never decides it, because the store reads the permissions again
    /// inside the transaction. <see langword="null"/> means no check, which is what callers pass while permission
    /// awareness is not delivered.
    /// </param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>
    /// <see cref="RecordWriteStatus.Success"/> with the updated record and its intrinsic position and count when the
    /// transaction committed; any other status when it rolled back with nothing written.
    /// </returns>
    Task<RecordWriteResult> UpdateAsync(
        FormMetadata form,
        string globalRecordId,
        IReadOnlyDictionary<string, FieldValue> submitted,
        Func<SaveStamp> createStamp,
        Func<StoredRecord, RecordValidationResult> requiredGate,
        FormPermissions? permissions,
        CancellationToken cancellationToken);

    /// <summary>
    /// Soft-deletes a record: sets the base row's <c>RecStatus</c> to 0 with the last-save stamp, in one transaction.
    /// </summary>
    /// <remarks>
    /// <list type="number">
    /// <item><description>
    /// Read the base row's <c>RecStatus</c> and <c>GlobalRecordId</c> <c>WITH (UPDLOCK, ROWLOCK)</c>. A missing row
    /// gives <see cref="SoftDeleteStatus.NotFound"/>; <c>RecStatus</c> 0 gives
    /// <see cref="SoftDeleteStatus.AlreadyDeleted"/> with nothing written, stamps included. Neither reads permissions.
    /// </description></item>
    /// <item><description>
    /// For an active record, when <paramref name="permissions"/> is not <see langword="null"/>, the record must pass
    /// <see cref="FormPermissions.CanDelete"/>, UPDATE on the base table; otherwise roll back with
    /// <see cref="SoftDeleteStatus.Forbidden"/> before any data statement. The store decides it from its own
    /// permission read inside the transaction, after the locked read, for the base table the transaction resolved, so
    /// the decision follows the grants in force inside the transaction, not those the caller read earlier.
    /// </description></item>
    /// <item><description>
    /// Otherwise call <paramref name="createStamp"/>, update the base row's <c>RecStatus</c> to 0 and its
    /// <c>LastSaveLogonName</c> and <c>LastSaveTime</c> from the stamp it returns, then commit.
    /// </description></item>
    /// </list>
    /// <para>Page rows are never touched and nothing cascades to other forms.</para>
    /// <para>
    /// A <see cref="SoftDeleteStatus.Deleted"/> or <see cref="SoftDeleteStatus.AlreadyDeleted"/> result carries the
    /// record's <c>GlobalRecordId</c> as stored, taken from the locked read, so the caller can name the record as
    /// stored once the transaction ends, without another database call. A <see cref="SoftDeleteStatus.Forbidden"/>
    /// result carries none, although the locked read found the record, and neither does
    /// <see cref="SoftDeleteStatus.NotFound"/>.
    /// </para>
    /// <para>
    /// An exception thrown by <paramref name="createStamp"/>, and a <see langword="null"/> it returns, rolls back and
    /// surfaces as a <see cref="DataStoreWriteException"/> with <see cref="WriteOutcome.NotWritten"/>, no SQL error
    /// number and, for an exception, that exception as its inner exception.
    /// </para>
    /// </remarks>
    /// <param name="form">The form's metadata, as <see cref="GetFormMetadataAsync"/> returned it.</param>
    /// <param name="globalRecordId">The record's base-table <c>GlobalRecordId</c>.</param>
    /// <param name="createStamp">
    /// Creates the save stamp of this soft delete. It is invoked at most once, inside the transaction, after the
    /// locked read has found an active record and the permission check has passed, and before the first data
    /// statement. It is never invoked when the soft delete writes nothing: <see cref="SoftDeleteStatus.NotFound"/>,
    /// <see cref="SoftDeleteStatus.AlreadyDeleted"/> and <see cref="SoftDeleteStatus.Forbidden"/>. An exception from
    /// it ends the call with nothing written.
    /// </param>
    /// <param name="permissions">
    /// Non-null turns on the delete check: the caller's own earlier <see cref="GetPermissionsAsync"/> result, which
    /// never decides it, because the store reads the base table's permissions again inside the transaction.
    /// <see langword="null"/> means no check and no permission statement, which is what callers pass while permission
    /// awareness is not delivered.
    /// </param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>
    /// What happened to the record, with the stored <c>GlobalRecordId</c> for
    /// <see cref="SoftDeleteStatus.Deleted"/> and <see cref="SoftDeleteStatus.AlreadyDeleted"/>.
    /// </returns>
    Task<SoftDeleteResult> SoftDeleteAsync(
        FormMetadata form,
        string globalRecordId,
        Func<SaveStamp> createStamp,
        FormPermissions? permissions,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads the login's INSERT and UPDATE permissions on the base table and every page table of
    /// <paramref name="form"/>, for permission awareness.
    /// </summary>
    /// <remarks>
    /// The permissions are read on every call and never cached, so a grant or revoke applies to the next request.
    /// <see cref="FormPermissions"/> derives the form-level insert, update and delete decisions from them.
    /// </remarks>
    /// <param name="form">The form's metadata.</param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>
    /// One entry per table, or <see langword="null"/> when the store does not report permissions. Then no permission
    /// check runs, the form-definition permission flags stay <see langword="null"/>, and a SQL permission failure on a
    /// write surfaces as <see cref="DataStoreWriteException"/>.
    /// </returns>
    Task<FormPermissions?> GetPermissionsAsync(FormMetadata form, CancellationToken cancellationToken);
}

/// <summary>
/// How <see cref="IDataStore.InsertAsync"/> or <see cref="IDataStore.UpdateAsync"/> ended. Only
/// <see cref="Success"/> means the transaction committed; every other status means it rolled back with nothing
/// written. An insert ends only in <see cref="Success"/> or <see cref="Forbidden"/>.
/// </summary>
public enum RecordWriteStatus
{
    /// <summary>
    /// The write committed; the API answers 201 with the new record for an insert and 200 with the updated record for
    /// an update.
    /// </summary>
    Success,

    /// <summary>The <c>GlobalRecordId</c> is not in the base table; the API answers 404.</summary>
    NotFound,

    /// <summary>
    /// The record has <c>RecStatus</c> 0, and a <c>PUT</c> on a deleted record is refused; the API answers 409.
    /// </summary>
    Deleted,

    /// <summary>
    /// The required gate failed on the stored record overlaid with the submitted values; the API answers 400 with
    /// <see cref="RecordWriteResult.Validation"/>.
    /// </summary>
    ValidationFailed,

    /// <summary>
    /// The store's permission read inside the transaction found that the login may not apply the write: for an
    /// insert, may not insert into the base table or a page table; for an update, may not update the base table or a
    /// page table, or may not insert into the table of a page whose row is missing. The API answers 403. Only a store
    /// given permissions, under permission awareness, returns it.
    /// </summary>
    Forbidden,
}

/// <summary>
/// The result of <see cref="IDataStore.InsertAsync"/> or <see cref="IDataStore.UpdateAsync"/>: its
/// <see cref="Status"/>, the written record for <see cref="RecordWriteStatus.Success"/>, and the failed gate result
/// for <see cref="RecordWriteStatus.ValidationFailed"/>.
/// </summary>
/// <remarks>
/// The factories are the only way to build one, so <see cref="Record"/> is set exactly for
/// <see cref="RecordWriteStatus.Success"/> and <see cref="Validation"/> exactly for
/// <see cref="RecordWriteStatus.ValidationFailed"/>. Instances are immutable and safe to share across threads.
/// </remarks>
public sealed class RecordWriteResult
{
    private static readonly RecordWriteResult NotFoundResult = new(RecordWriteStatus.NotFound, null, null);
    private static readonly RecordWriteResult DeletedResult = new(RecordWriteStatus.Deleted, null, null);
    private static readonly RecordWriteResult ForbiddenResult = new(RecordWriteStatus.Forbidden, null, null);

    private RecordWriteResult(RecordWriteStatus status, RecordDto? record, RecordValidationResult? validation)
    {
        Status = status;
        Record = record;
        Validation = validation;
    }

    /// <summary>How the write ended.</summary>
    public RecordWriteStatus Status { get; }

    /// <summary>
    /// The new or updated record with its intrinsic position and count, read inside the committed transaction; set
    /// only when <see cref="Status"/> is <see cref="RecordWriteStatus.Success"/>.
    /// </summary>
    public RecordDto? Record { get; }

    /// <summary>
    /// The required gate's failed result; set only when <see cref="Status"/> is
    /// <see cref="RecordWriteStatus.ValidationFailed"/>.
    /// </summary>
    public RecordValidationResult? Validation { get; }

    /// <summary>Whether the write committed, in which case <see cref="Record"/> is set.</summary>
    [MemberNotNullWhen(true, nameof(Record))]
    public bool IsSuccess => Status == RecordWriteStatus.Success;

    /// <summary>A committed insert or update.</summary>
    /// <param name="record">The written record, as the snapshot read inside the transaction built it.</param>
    /// <returns>A <see cref="RecordWriteStatus.Success"/> result carrying <paramref name="record"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="record"/> is <see langword="null"/>.</exception>
    public static RecordWriteResult Success(RecordDto record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new RecordWriteResult(RecordWriteStatus.Success, record, null);
    }

    /// <summary>The record is not in the base table.</summary>
    /// <returns>A <see cref="RecordWriteStatus.NotFound"/> result.</returns>
    public static RecordWriteResult NotFound() => NotFoundResult;

    /// <summary>The record is soft-deleted, so it cannot be updated.</summary>
    /// <returns>A <see cref="RecordWriteStatus.Deleted"/> result.</returns>
    public static RecordWriteResult Deleted() => DeletedResult;

    /// <summary>The required gate failed, so nothing was written.</summary>
    /// <param name="validation">The gate's result, which must not be valid.</param>
    /// <returns>
    /// A <see cref="RecordWriteStatus.ValidationFailed"/> result carrying <paramref name="validation"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="validation"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="validation"/> is valid.</exception>
    public static RecordWriteResult ValidationFailed(RecordValidationResult validation)
    {
        ArgumentNullException.ThrowIfNull(validation);

        if (validation.IsValid)
        {
            throw new ArgumentException("A validation failure needs a result that is not valid.", nameof(validation));
        }

        return new RecordWriteResult(RecordWriteStatus.ValidationFailed, null, validation);
    }

    /// <summary>
    /// The login may not insert the record into the form's tables, or may not update the record's tables or insert a
    /// missing page row of it, so nothing was written.
    /// </summary>
    /// <returns>A <see cref="RecordWriteStatus.Forbidden"/> result.</returns>
    public static RecordWriteResult Forbidden() => ForbiddenResult;
}

/// <summary>How <see cref="IDataStore.SoftDeleteAsync"/> ended.</summary>
public enum SoftDeleteStatus
{
    /// <summary>The record was active and is now deleted; the transaction committed and the API answers 204.</summary>
    Deleted,

    /// <summary>
    /// The record was already deleted, so nothing was written, stamps included; the API still answers 204, which makes
    /// a repeated <c>DELETE</c> safe.
    /// </summary>
    AlreadyDeleted,

    /// <summary>The <c>GlobalRecordId</c> is not in the base table; the API answers 404.</summary>
    NotFound,

    /// <summary>
    /// The record was active, and the store's permission read inside the transaction found that the login may not
    /// update the base table, so the transaction rolled back with nothing written, stamps included; the API answers
    /// 403. Only a store given permissions, under permission awareness, returns it.
    /// </summary>
    Forbidden,
}

/// <summary>
/// The result of <see cref="IDataStore.SoftDeleteAsync"/>: its <see cref="Status"/> and, for a record the locked read
/// found and the soft delete did not refuse, the base row's <c>GlobalRecordId</c> as stored.
/// </summary>
/// <remarks>
/// The id is the spelling the locked read returned, which can differ in letter case from the one the caller passed
/// wherever the column's collation ignores letter case. It lets the caller name the record as stored, as an insert or
/// an update names it, without another database call. The id is carried exactly as the matched row holds it: the
/// column admits any non-null text, so an empty or whitespace id is carried too, and only a missing one is refused.
/// The factories are the only way to build one, so <see cref="GlobalRecordId"/> is set exactly for
/// <see cref="SoftDeleteStatus.Deleted"/> and <see cref="SoftDeleteStatus.AlreadyDeleted"/>, and neither
/// <see cref="SoftDeleteStatus.NotFound"/> nor <see cref="SoftDeleteStatus.Forbidden"/> carries an id. Instances are
/// immutable and safe to share across threads.
/// </remarks>
public sealed class SoftDeleteResult
{
    private static readonly SoftDeleteResult NotFoundResult = new(SoftDeleteStatus.NotFound, null);

    private static readonly SoftDeleteResult ForbiddenResult = new(SoftDeleteStatus.Forbidden, null);

    private SoftDeleteResult(SoftDeleteStatus status, string? globalRecordId)
    {
        Status = status;
        GlobalRecordId = globalRecordId;
    }

    /// <summary>How the soft delete ended.</summary>
    public SoftDeleteStatus Status { get; }

    /// <summary>
    /// The base row's <c>GlobalRecordId</c> as stored, taken from the locked read; set only when <see cref="Status"/>
    /// is <see cref="SoftDeleteStatus.Deleted"/> or <see cref="SoftDeleteStatus.AlreadyDeleted"/>.
    /// </summary>
    public string? GlobalRecordId { get; }

    /// <summary>
    /// Whether the locked read found the record and the soft delete either committed or found it already deleted, in
    /// which case <see cref="GlobalRecordId"/> is set. A <see cref="SoftDeleteStatus.Forbidden"/> result is not found:
    /// it carries no id.
    /// </summary>
    [MemberNotNullWhen(true, nameof(GlobalRecordId))]
    public bool IsFound => Status is SoftDeleteStatus.Deleted or SoftDeleteStatus.AlreadyDeleted;

    /// <summary>A committed soft delete of an active record.</summary>
    /// <param name="globalRecordId">
    /// The base row's <c>GlobalRecordId</c>, as the locked read returned it, carried unchanged.
    /// </param>
    /// <returns>A <see cref="SoftDeleteStatus.Deleted"/> result carrying <paramref name="globalRecordId"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="globalRecordId"/> is <see langword="null"/>.</exception>
    public static SoftDeleteResult Deleted(string globalRecordId) =>
        new(SoftDeleteStatus.Deleted, RequireStoredId(globalRecordId));

    /// <summary>The record was already deleted, so nothing was written, stamps included.</summary>
    /// <param name="globalRecordId">
    /// The base row's <c>GlobalRecordId</c>, as the locked read returned it, carried unchanged.
    /// </param>
    /// <returns>
    /// An <see cref="SoftDeleteStatus.AlreadyDeleted"/> result carrying <paramref name="globalRecordId"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="globalRecordId"/> is <see langword="null"/>.</exception>
    public static SoftDeleteResult AlreadyDeleted(string globalRecordId) =>
        new(SoftDeleteStatus.AlreadyDeleted, RequireStoredId(globalRecordId));

    /// <summary>The record is not in the base table.</summary>
    /// <returns>A <see cref="SoftDeleteStatus.NotFound"/> result, which carries no id.</returns>
    public static SoftDeleteResult NotFound() => NotFoundResult;

    /// <summary>The login may not update the record's base table, so nothing was written, stamps included.</summary>
    /// <returns>A <see cref="SoftDeleteStatus.Forbidden"/> result, which carries no id.</returns>
    public static SoftDeleteResult Forbidden() => ForbiddenResult;

    // A found record always has a stored id, since the column is NOT NULL. Any other value is the matched row's own and
    // is kept as it is: refusing an empty or whitespace id would turn an already-deleted 204 into a failure, or fail a
    // delete after its UPDATE had run.
    private static string RequireStoredId(string globalRecordId)
    {
        ArgumentNullException.ThrowIfNull(globalRecordId);
        return globalRecordId;
    }
}
