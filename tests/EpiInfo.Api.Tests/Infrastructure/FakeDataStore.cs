using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Globalization;
using EpiInfo.Domain.Forms;
using EpiInfo.Domain.Metadata;
using EpiInfo.Domain.Records;
using EpiInfo.Domain.Search;
using EpiInfo.Domain.Storage;
using EpiInfo.Domain.Validation;
using EpiInfo.Domain.Values;

namespace EpiInfo.Api.Tests.Infrastructure;

/// <summary>
/// One form held by <see cref="FakeDataStore"/>: its metadata, the options of its Legal Values fields and its
/// check-code program.
/// </summary>
/// <param name="Metadata">The form's metadata, returned by <see cref="IDataStore.GetFormMetadataAsync"/>.</param>
/// <param name="Options">
/// The code-table options of the form's Legal Values fields, keyed by field name and matched ignoring case. A Legal
/// Values field without an entry gets an empty list, as a field without a usable code table does.
/// </param>
/// <param name="CheckCode">The <c>metaViews.CheckCode</c> text, or <see langword="null"/> for a NULL column.</param>
public sealed record FakeForm(
    FormMetadata Metadata,
    IReadOnlyDictionary<string, IReadOnlyList<FieldOption>> Options,
    string? CheckCode)
{
    /// <summary>Returns a copy of this form whose check-code program is <paramref name="program"/>.</summary>
    /// <param name="program">The program text, or <see langword="null"/> for a NULL column.</param>
    public FakeForm WithCheckCode(string? program) => this with { CheckCode = program };
}

/// <summary>One call the API made to <see cref="FakeDataStore"/>, in the order the calls arrived.</summary>
/// <param name="Method">The <see cref="IDataStore"/> member name, for example <c>InsertAsync</c>.</param>
/// <param name="IsWrite">
/// <see langword="true"/> for the data-changing stage of a write: <c>InsertAsync</c>, the DML stage of
/// <c>UpdateAsync</c>, and <c>SoftDeleteAsync</c> when it changes a record.
/// </param>
public sealed record FakeStoreCall(string Method, bool IsWrite);

/// <summary>
/// When an injected failure is raised, and so which <see cref="WriteOutcome"/> a store exception may report.
/// </summary>
/// <remarks>
/// A <see cref="DataStoreUnavailableException"/> or <see cref="DataStoreWriteException"/> must report an outcome that
/// the stage's state agrees with, because the API reports that outcome as <c>writeOutcome</c>:
/// <see cref="WriteOutcome.NotWritten"/> goes with <see cref="BeforeWrite"/>, <see cref="WriteOutcome.Written"/> with
/// <see cref="AfterWrite"/> on a write method, and <see cref="WriteOutcome.Unknown"/> with either, each stage modelling
/// one of the two states it covers. <see cref="FakeDataStore.ThrowOn"/> and <see cref="FakeDataStore.ThrowOnAny"/>
/// reject an incoherent pair with an <see cref="ArgumentException"/>. Any other exception carries no outcome and fits
/// either stage. A test asserts the resulting state (<see cref="FakeDataStore.GetRecord"/>,
/// <see cref="FakeDataStore.GetRecords"/>) and the call log (<see cref="FakeDataStore.WriteCalls"/>) as well as the
/// response.
/// </remarks>
public enum FakeFailureStage
{
    /// <summary>
    /// Before any state change. For a write, at the point its first data statement would be sent: nothing is stored
    /// and <see cref="FakeDataStore.WriteCalls"/> gains no entry, as when no DML was sent. A store exception raised
    /// here reports <see cref="WriteOutcome.NotWritten"/>, or <see cref="WriteOutcome.Unknown"/> for the unconfirmed
    /// state in which nothing was kept; never <see cref="WriteOutcome.Written"/>.
    /// </summary>
    BeforeWrite,

    /// <summary>
    /// For the three write methods, after the change has been applied and logged, as a failure after the commit: the
    /// change is stored and <see cref="FakeDataStore.WriteCalls"/> holds its entry. A store exception raised here
    /// reports <see cref="WriteOutcome.Written"/>, or <see cref="WriteOutcome.Unknown"/> for the unconfirmed state in
    /// which the change was kept; never <see cref="WriteOutcome.NotWritten"/>. Any other method treats it as
    /// <see cref="BeforeWrite"/>, with that stage's outcomes.
    /// </summary>
    AfterWrite,
}

/// <summary>An inspection snapshot of one record held by <see cref="FakeDataStore"/>.</summary>
/// <param name="GlobalRecordId">The base-table <c>GlobalRecordId</c>.</param>
/// <param name="UniqueKey">The base-table <c>UniqueKey</c>.</param>
/// <param name="RecStatus">The base-table <c>RecStatus</c>.</param>
/// <param name="Values">
/// Every data field by canonical name, in storage order, with <see cref="FieldValue.Null"/> for each field of a page
/// whose row is missing, as a <c>LEFT JOIN</c> reads it.
/// </param>
/// <param name="PresentPageIds">The ids of the pages whose page row exists.</param>
/// <param name="FirstSave">The stamp in <c>FirstSaveLogonName</c>/<c>FirstSaveTime</c>, or <see langword="null"/>.</param>
/// <param name="LastSave">The stamp in <c>LastSaveLogonName</c>/<c>LastSaveTime</c>, or <see langword="null"/>.</param>
public sealed record FakeRecordState(
    string GlobalRecordId,
    int UniqueKey,
    int RecStatus,
    IReadOnlyDictionary<string, FieldValue> Values,
    IReadOnlySet<int> PresentPageIds,
    SaveStamp? FirstSave,
    SaveStamp? LastSave);

/// <summary>
/// Synthetic forms for API tests that run without SQL Server, built from the same Domain metadata records the SQL
/// metadata reader returns. Every member returns a fresh instance.
/// </summary>
/// <remarks>
/// <para>
/// View ids 9001 to 9004 and page ids <c>&lt;viewId&gt;01</c> and <c>&lt;viewId&gt;02</c> never collide with the
/// seeded views 1 and 2 or pages 1 to 3. Field-type ids and names are the seeded <c>metaFieldTypes</c> rows. Each page
/// table is named after the base table plus the page id, as legacy names it, and each form carries the three system
/// rows <c>UniqueKey</c>, <c>RecStatus</c> and <c>GlobalRecordId</c> with a null page, as the seeded forms do.
/// </para>
/// <para>
/// Column shapes: text and legal values on <c>nvarchar(4000)</c> (8000 bytes), number on <c>float</c>, date on
/// <c>datetime</c> and checkbox on <c>bit</c>. Labels own no column. Every page-bound field's prompt is its name.
/// </para>
/// </remarks>
public static class FakeForms
{
    /// <summary>The view id of <see cref="Empty"/>.</summary>
    public const int EmptyViewId = 9001;

    /// <summary>The view id of <see cref="WithRequiredField"/>.</summary>
    public const int RequiredViewId = 9002;

    /// <summary>The view id of <see cref="WithCheckCode(string)"/>.</summary>
    public const int CheckCodeViewId = 9003;

    /// <summary>The view id of <see cref="TwoPage"/>.</summary>
    public const int TwoPageViewId = 9004;

    /// <summary>The required field of <see cref="WithRequiredField"/>.</summary>
    public const string RequiredFieldName = "Surname";

    /// <summary>The prompt of <see cref="RequiredFieldName"/>, the <c>{0}</c> of the required-field message.</summary>
    public const string RequiredFieldPrompt = "Surname";

    /// <summary>The page of <see cref="RequiredFieldName"/>, the <c>{1}</c> of the required-field message.</summary>
    public const string RequiredFieldPageName = "Page 1";

    /// <summary>The fake code table of the <c>Sex</c> Legal Values field of <see cref="TwoPage"/>.</summary>
    public const string SexCodeTableName = "codeFakeSex";

    private const int TextTypeId = 1;
    private const int LabelTypeId = 2;
    private const int NumberTypeId = 5;
    private const int DateTypeId = 7;
    private const int CheckboxTypeId = 10;
    private const int LegalValuesTypeId = 17;
    private const int RecStatusTypeId = 22;
    private const int UniqueKeyTypeId = 23;
    private const int GlobalRecordIdTypeId = 26;

    private const string FirstPageName = "Page 1";
    private const string SecondPageName = "Page 2";

    /// <summary>
    /// View 9001, base table <c>FakeEmpty</c>: page <c>Page 1</c> with text <c>LastName</c> and number <c>Age</c>.
    /// The empty-form cases hold no record for it.
    /// </summary>
    public static FakeForm Empty
    {
        get
        {
            const int viewId = EmptyViewId;
            const string baseTable = "FakeEmpty";
            var page = Page(viewId, baseTable, 1, FirstPageName, 0);
            return Create(
                viewId,
                baseTable,
                [page],
                [
                    .. SystemRows(viewId),
                    Text(viewId, 4, page, 1, "LastName"),
                    Number(viewId, 5, page, 2, "Age"),
                ]);
        }
    }

    /// <summary>
    /// View 9002, base table <c>FakeRequired</c>: page <c>Page 1</c> with the required text
    /// <see cref="RequiredFieldName"/> and the optional text <c>Comment</c>. No seeded field is required, so this is
    /// the form on which the required gate and its <c>FIELD_IS_REQUIRED_LONG</c> message are proved.
    /// </summary>
    public static FakeForm WithRequiredField
    {
        get
        {
            const int viewId = RequiredViewId;
            const string baseTable = "FakeRequired";
            var page = Page(viewId, baseTable, 1, RequiredFieldPageName, 0);
            return Create(
                viewId,
                baseTable,
                [page],
                [
                    .. SystemRows(viewId),
                    Text(viewId, 4, page, 1, RequiredFieldName) with
                    {
                        Prompt = RequiredFieldPrompt,
                        IsRequired = true,
                    },
                    Text(viewId, 5, page, 2, "Comment"),
                ]);
        }
    }

    /// <summary>
    /// View 9003, base table <c>FakeCheckCode</c>: page <c>Page 1</c> with text <c>LastName</c>, date <c>DOB</c>,
    /// number <c>Age</c>, checkbox <c>Asian</c> and the read-only number <c>OnsetWeek</c>, carrying
    /// <paramref name="program"/> as its check code.
    /// </summary>
    /// <param name="program">The check-code program the store returns for the form.</param>
    /// <exception cref="ArgumentNullException"><paramref name="program"/> is <see langword="null"/>.</exception>
    public static FakeForm WithCheckCode(string program)
    {
        ArgumentNullException.ThrowIfNull(program);

        const int viewId = CheckCodeViewId;
        const string baseTable = "FakeCheckCode";
        var page = Page(viewId, baseTable, 1, FirstPageName, 0);
        return Create(
            viewId,
            baseTable,
            [page],
            [
                .. SystemRows(viewId),
                Text(viewId, 4, page, 1, "LastName"),
                Date(viewId, 5, page, 2, "DOB"),
                Number(viewId, 6, page, 3, "Age"),
                Checkbox(viewId, 7, page, 4, "Asian"),
                Number(viewId, 8, page, 5, "OnsetWeek") with { IsReadOnly = true },
            ],
            checkCode: program);
    }

    /// <summary>
    /// View 9004, base table <c>FakeTwoPage</c>. <c>Page 1</c> (position 0) holds the label <c>Title1</c>, text
    /// <c>LastName</c>, number <c>Age</c> bounded 0 to 120, and the Legal Values field <c>Sex</c>, whose options
    /// <c>Male</c> and <c>Female</c> come from <see cref="SexCodeTableName"/>. <c>Page 2</c> (position 1) holds
    /// checkbox <c>Ill</c> and text <c>Notes</c>.
    /// </summary>
    public static FakeForm TwoPage
    {
        get
        {
            const int viewId = TwoPageViewId;
            const string baseTable = "FakeTwoPage";
            var first = Page(viewId, baseTable, 1, FirstPageName, 0);
            var second = Page(viewId, baseTable, 2, SecondPageName, 1);

            var options = new Dictionary<string, IReadOnlyList<FieldOption>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Sex"] = Array.AsReadOnly(new[]
                {
                    new FieldOption(FieldValue.FromText("Male"), "Male"),
                    new FieldOption(FieldValue.FromText("Female"), "Female"),
                }),
            };

            return Create(
                viewId,
                baseTable,
                [first, second],
                [
                    .. SystemRows(viewId),
                    Label(viewId, 4, first, 1, "Title1"),
                    Text(viewId, 5, first, 2, "LastName"),
                    Number(viewId, 6, first, 3, "Age") with { Lower = "0", Upper = "120" },
                    Text(viewId, 7, first, 4, "Sex") with
                    {
                        FieldTypeId = LegalValuesTypeId,
                        FieldTypeName = "Legal Values",
                        SourceTableName = SexCodeTableName,
                        CodeColumnName = "Sex",
                        TextColumnName = "Sex",
                    },
                    Checkbox(viewId, 8, second, 1, "Ill"),
                    Text(viewId, 9, second, 2, "Notes"),
                ],
                options);
        }
    }

    private static FakeForm Create(
        int viewId,
        string baseTable,
        PageMetadata[] pages,
        FieldMetadata[] fields,
        IDictionary<string, IReadOnlyList<FieldOption>>? options = null,
        string? checkCode = null)
    {
        var metadata = new FormMetadata(viewId, baseTable, baseTable, Array.AsReadOnly(pages), Array.AsReadOnly(fields));
        var optionMap = new ReadOnlyDictionary<string, IReadOnlyList<FieldOption>>(
            options ?? new Dictionary<string, IReadOnlyList<FieldOption>>(StringComparer.OrdinalIgnoreCase));
        return new FakeForm(metadata, optionMap, checkCode);
    }

    // Page ids are <viewId>01 and <viewId>02, and the page table is the base table name plus the page id.
    private static PageMetadata Page(int viewId, string baseTable, int ordinal, string name, int position)
    {
        var pageId = (viewId * 100) + ordinal;
        return new PageMetadata(pageId, name, position, baseTable + pageId.ToString(CultureInfo.InvariantCulture));
    }

    private static int FieldId(int viewId, int ordinal) => (viewId * 1000) + ordinal;

    // The seeded system rows have no page, no prompt and no page-table column, and TabIndex 0.
    private static FieldMetadata[] SystemRows(int viewId) =>
    [
        SystemRow(FieldId(viewId, 1), "UniqueKey", UniqueKeyTypeId),
        SystemRow(FieldId(viewId, 2), "RecStatus", RecStatusTypeId),
        SystemRow(FieldId(viewId, 3), "GlobalRecordId", GlobalRecordIdTypeId),
    ];

    private static FieldMetadata SystemRow(int fieldId, string name, int fieldTypeId) => new()
    {
        FieldId = fieldId,
        Name = name,
        FieldTypeId = fieldTypeId,
        FieldTypeName = name,
        TabIndex = 0m,
    };

    private static FieldMetadata Text(int viewId, int ordinal, PageMetadata page, decimal tabIndex, string name) =>
        PageField(viewId, ordinal, page, tabIndex, name, TextTypeId, "Text", "nvarchar", 8000);

    private static FieldMetadata Number(int viewId, int ordinal, PageMetadata page, decimal tabIndex, string name) =>
        PageField(viewId, ordinal, page, tabIndex, name, NumberTypeId, "Number", "float", 8);

    private static FieldMetadata Date(int viewId, int ordinal, PageMetadata page, decimal tabIndex, string name) =>
        PageField(viewId, ordinal, page, tabIndex, name, DateTypeId, "Date", "datetime", 8);

    private static FieldMetadata Checkbox(int viewId, int ordinal, PageMetadata page, decimal tabIndex, string name) =>
        PageField(viewId, ordinal, page, tabIndex, name, CheckboxTypeId, "Checkbox", "bit", 1);

    private static FieldMetadata Label(int viewId, int ordinal, PageMetadata page, decimal tabIndex, string name) =>
        new()
        {
            FieldId = FieldId(viewId, ordinal),
            Name = name,
            FieldTypeId = LabelTypeId,
            FieldTypeName = "Label/Title",
            Prompt = name,
            TabIndex = tabIndex,
            PageId = page.PageId,
        };

    private static FieldMetadata PageField(
        int viewId,
        int ordinal,
        PageMetadata page,
        decimal tabIndex,
        string name,
        int fieldTypeId,
        string fieldTypeName,
        string sqlTypeName,
        int maxLengthBytes) => new()
        {
            FieldId = FieldId(viewId, ordinal),
            Name = name,
            FieldTypeId = fieldTypeId,
            FieldTypeName = fieldTypeName,
            Prompt = name,
            TabIndex = tabIndex,
            PageId = page.PageId,
            Column = FieldTypeMapper.DescribeColumn(name, sqlTypeName, maxLengthBytes),
        };
}

/// <summary>
/// The in-memory <see cref="IDataStore"/> test double: API tests host the real endpoints and services over it, with no
/// SQL Server, and assert on its records, its call log and its injected failures.
/// </summary>
/// <remarks>
/// <para>
/// It follows the record-set, read and write contract of <see cref="IDataStore"/> as the SQL Server store implements
/// it: the selected set and <c>UniqueKey</c> order of <see cref="RecordQueryRules"/>, page rows read as a
/// <c>LEFT JOIN</c> (missing pages give <see cref="FieldValue.Null"/>), search over records that have every page row,
/// base-only soft delete, delta updates, and the gates of <see cref="UpdateAsync"/> in the interface's order. A
/// <c>values</c> key that is not a data field, a value not of its field's kind, or a value its column cannot store,
/// is an <see cref="ArgumentException"/> raised before any change, as the SQL builders raise it. Text comparisons,
/// record ids and the grid filter ignore case ordinally, the common case-insensitive collation; the fake claims no
/// collation parity beyond that.
/// </para>
/// <para>
/// One lock guards all state, and every write runs entirely under it, so concurrent updates serialise and the last
/// writer wins per field, as the SQL store's <c>UPDLOCK</c> makes them. Rows are immutable and each write publishes
/// replacements, so <see cref="ListAsync"/> and <see cref="SearchAsync"/> take the form's current rows under the lock
/// and filter outside it, seeing one consistent state of the form. Every member completes synchronously and reports
/// failures through the returned task, as an asynchronous store does.
/// </para>
/// </remarks>
public sealed class FakeDataStore : IDataStore
{
    private static readonly HashSet<string> MemberNames = new(StringComparer.Ordinal)
    {
        nameof(IDataStore.ListFormsAsync),
        nameof(IDataStore.GetFormMetadataAsync),
        nameof(IDataStore.GetOptionsAsync),
        nameof(IDataStore.GetCheckCodeAsync),
        nameof(IDataStore.CountAsync),
        nameof(IDataStore.GetAtPositionAsync),
        nameof(IDataStore.GetByIdAsync),
        nameof(IDataStore.GetSetPositionAsync),
        nameof(IDataStore.ListAsync),
        nameof(IDataStore.SearchAsync),
        nameof(IDataStore.PingAsync),
        nameof(IDataStore.InsertAsync),
        nameof(IDataStore.UpdateAsync),
        nameof(IDataStore.SoftDeleteAsync),
        nameof(IDataStore.GetPermissionsAsync),
    };

    private const int PrefixCompareLength = 255;

    private readonly object _gate = new();
    private readonly SortedDictionary<int, FormState> _forms = new();
    private readonly List<FakeStoreCall> _calls = new();
    private readonly Dictionary<string, (Exception Exception, FakeFailureStage Stage)> _failures =
        new(StringComparer.Ordinal);

    private Exception? _failureForAll;
    private bool _pingAnswered = true;

    /// <summary>Creates a store that holds no form.</summary>
    public FakeDataStore()
    {
    }

    /// <summary>Creates a store holding <paramref name="forms"/>.</summary>
    /// <param name="forms">The forms to add, each with a distinct view id.</param>
    /// <exception cref="ArgumentNullException"><paramref name="forms"/> or one of its entries is null.</exception>
    /// <exception cref="ArgumentException">Two forms share a view id.</exception>
    public FakeDataStore(params FakeForm[] forms)
    {
        ArgumentNullException.ThrowIfNull(forms);
        foreach (var form in forms)
        {
            AddForm(form);
        }
    }

    /// <summary>The calls made so far, in order.</summary>
    public IReadOnlyList<FakeStoreCall> Calls
    {
        get
        {
            lock (_gate)
            {
                return Array.AsReadOnly(_calls.ToArray());
            }
        }
    }

    /// <summary>The data-changing calls made so far, in order: the entries of <see cref="Calls"/> that are writes.</summary>
    public IReadOnlyList<FakeStoreCall> WriteCalls
    {
        get
        {
            lock (_gate)
            {
                return Array.AsReadOnly(_calls.Where(call => call.IsWrite).ToArray());
            }
        }
    }

    /// <summary>
    /// Creates a store holding <see cref="FakeForms.Empty"/>, <see cref="FakeForms.WithRequiredField"/>,
    /// <see cref="FakeForms.WithCheckCode(string)"/> with an empty program, and <see cref="FakeForms.TwoPage"/>.
    /// </summary>
    public static FakeDataStore WithStandardForms() => new(
        FakeForms.Empty,
        FakeForms.WithRequiredField,
        FakeForms.WithCheckCode(string.Empty),
        FakeForms.TwoPage);

    /// <summary>
    /// A failure that is not a store exception and whose message holds SQL text and <paramref name="sentinel"/>, as a
    /// database driver's message would. The API maps it to 500, and redaction tests assert that neither the response
    /// nor the logs carry it.
    /// </summary>
    /// <param name="sentinel">A unique marker to search for in bodies and logs.</param>
    public static Exception SqlTextException(string sentinel)
    {
        ArgumentNullException.ThrowIfNull(sentinel);
        return new InvalidOperationException(
            $"SELECT * FROM [FakeTwoPage] WHERE [LastName] = N'{sentinel}'");
    }

    /// <summary>An availability failure, which the API maps to 503.</summary>
    /// <remarks>
    /// Its outcome fixes the stage it is injected at, as <see cref="FakeFailureStage"/> sets out:
    /// <see cref="WriteOutcome.NotWritten"/> at <see cref="FakeFailureStage.BeforeWrite"/>,
    /// <see cref="WriteOutcome.Written"/> at <see cref="FakeFailureStage.AfterWrite"/> on a write method, and
    /// <see cref="WriteOutcome.Unknown"/> at either, each stage modelling one of the two states it covers.
    /// <see cref="ThrowOn"/> and <see cref="ThrowOnAny"/> reject any other pairing with an
    /// <see cref="ArgumentException"/>. A test asserts the resulting state (<see cref="GetRecord"/>,
    /// <see cref="GetRecords"/>) and the call log (<see cref="WriteCalls"/>) as well as the response.
    /// </remarks>
    /// <param name="outcome">What the failure reports about the stored data.</param>
    /// <param name="operation">The failed operation's fixed identifier.</param>
    /// <param name="sqlErrorNumber">The SQL error number it reports; -2 is a command timeout.</param>
    public static DataStoreUnavailableException Unavailable(
        WriteOutcome outcome,
        string operation = "fake",
        int? sqlErrorNumber = -2) => new(operation, outcome, sqlErrorNumber);

    /// <summary>An unexpected store failure, which the API maps to 500.</summary>
    /// <remarks>
    /// Its outcome fixes the stage it is injected at, as <see cref="FakeFailureStage"/> sets out:
    /// <see cref="WriteOutcome.NotWritten"/> at <see cref="FakeFailureStage.BeforeWrite"/>,
    /// <see cref="WriteOutcome.Written"/> at <see cref="FakeFailureStage.AfterWrite"/> on a write method, and
    /// <see cref="WriteOutcome.Unknown"/> at either, each stage modelling one of the two states it covers.
    /// <see cref="ThrowOn"/> and <see cref="ThrowOnAny"/> reject any other pairing with an
    /// <see cref="ArgumentException"/>. A test asserts the resulting state (<see cref="GetRecord"/>,
    /// <see cref="GetRecords"/>) and the call log (<see cref="WriteCalls"/>) as well as the response.
    /// </remarks>
    /// <param name="outcome">What the failure reports about the stored data.</param>
    /// <param name="operation">The failed operation's fixed identifier.</param>
    /// <param name="sqlErrorNumber">The SQL error number it reports; 547 is a constraint violation.</param>
    public static DataStoreWriteException WriteFailure(
        WriteOutcome outcome,
        string operation = "fake",
        int? sqlErrorNumber = 547) => new(operation, outcome, sqlErrorNumber);

    /// <summary>Adds a form, with no record.</summary>
    /// <param name="form">The form.</param>
    /// <exception cref="ArgumentNullException"><paramref name="form"/> or its metadata or options are null.</exception>
    /// <exception cref="ArgumentException">The store already holds a form with that view id.</exception>
    public void AddForm(FakeForm form)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(form.Metadata, nameof(form));
        ArgumentNullException.ThrowIfNull(form.Options, nameof(form));

        var state = new FormState(form);
        lock (_gate)
        {
            if (!_forms.TryAdd(form.Metadata.ViewId, state))
            {
                throw new ArgumentException("The store already holds a form with this view id.", nameof(form));
            }
        }
    }

    /// <summary>
    /// Adds a record directly, as if it had been seeded: no call is logged and no stamp is written.
    /// </summary>
    /// <param name="viewId">The form's view id.</param>
    /// <param name="values">
    /// Values of data fields keyed by name, in any letter case; every other data field is <see cref="FieldValue.Null"/>.
    /// A value for a field of a missing page is discarded, so it reads as <see cref="FieldValue.Null"/>.
    /// </param>
    /// <param name="recStatus">The record's <c>RecStatus</c>; 0 seeds a deleted record.</param>
    /// <param name="missingPageIds">The pages whose page row the record lacks.</param>
    /// <param name="globalRecordId">The record's id, or <see langword="null"/> for a new GUID.</param>
    /// <returns>The record's <c>GlobalRecordId</c>.</returns>
    /// <exception cref="ArgumentException">
    /// The form is unknown; a key is not a data field, a value is not of its field's kind or a value does not fit its
    /// column; a page id is not a page of the form; or the id is blank or already used.
    /// </exception>
    public string SeedRecord(
        int viewId,
        IReadOnlyDictionary<string, FieldValue>? values = null,
        int recStatus = RecordQueryRules.ActiveRecStatus,
        IEnumerable<int>? missingPageIds = null,
        string? globalRecordId = null)
    {
        lock (_gate)
        {
            var state = Resolve(viewId);
            var normalised = NormaliseValues(state, values ?? new Dictionary<string, FieldValue>(), nameof(values));

            var present = new HashSet<int>(state.Form.Metadata.Pages.Select(page => page.PageId));
            foreach (var pageId in missingPageIds ?? Enumerable.Empty<int>())
            {
                if (!present.Remove(pageId) && state.Form.Metadata.FindPage(pageId) is null)
                {
                    throw new ArgumentException("A missing page id is not a page of the form.", nameof(missingPageIds));
                }
            }

            var id = globalRecordId ?? NewGlobalRecordId();
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException("A record id must not be blank.", nameof(globalRecordId));
            }

            if (state.Find(id) is not null)
            {
                throw new ArgumentException("The form already holds a record with this id.", nameof(globalRecordId));
            }

            var stored = new Dictionary<string, FieldValue>(state.DataFields.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var field in state.DataFields)
            {
                stored[field.Name] = present.Contains(field.Page.PageId)
                    && normalised.TryGetValue(field.Name, out var value)
                        ? value
                        : FieldValue.Null;
            }

            state.Add(new StoredRow(
                id,
                state.NextUniqueKey(),
                recStatus,
                new ReadOnlyDictionary<string, FieldValue>(stored),
                new ReadOnlySet<int>(present),
                null,
                null));
            return id;
        }
    }

    /// <summary>Takes an inspection snapshot of one record.</summary>
    /// <param name="viewId">The form's view id.</param>
    /// <param name="globalRecordId">The record's id, matched ignoring case.</param>
    /// <returns>The snapshot, or <see langword="null"/> when the form holds no such record.</returns>
    /// <exception cref="ArgumentException">The form is unknown.</exception>
    public FakeRecordState? GetRecord(int viewId, string globalRecordId)
    {
        ArgumentNullException.ThrowIfNull(globalRecordId);
        lock (_gate)
        {
            var state = Resolve(viewId);
            return state.Find(globalRecordId) is { } row ? State(state, row) : null;
        }
    }

    /// <summary>Takes inspection snapshots of every record of a form, in <c>UniqueKey</c> order.</summary>
    /// <param name="viewId">The form's view id.</param>
    /// <exception cref="ArgumentException">The form is unknown.</exception>
    public IReadOnlyList<FakeRecordState> GetRecords(int viewId)
    {
        lock (_gate)
        {
            var state = Resolve(viewId);
            return Array.AsReadOnly(state.Rows.Select(row => State(state, row)).ToArray());
        }
    }

    /// <summary>
    /// Sets the per-table permissions <see cref="GetPermissionsAsync"/> reports for a form; <see langword="null"/>
    /// reports none, so no permission check runs.
    /// </summary>
    /// <param name="viewId">The form's view id.</param>
    /// <param name="permissions">The base-table and page-table entries, or <see langword="null"/>.</param>
    /// <exception cref="ArgumentException">The form is unknown.</exception>
    public void SetPermissions(int viewId, FormPermissions? permissions)
    {
        lock (_gate)
        {
            Resolve(viewId).Permissions = permissions;
        }
    }

    /// <summary>
    /// Sets the answer <see cref="PingAsync"/> returns once its call is logged and no injected failure or cancellation
    /// applies: <see langword="true"/>, the default, for a database that answered, or <see langword="false"/>, the
    /// negative answer the <see cref="IDataStore.PingAsync"/> contract permits a store to report instead of throwing.
    /// </summary>
    /// <param name="answered">Whether the database answered.</param>
    public void SetPingResult(bool answered)
    {
        lock (_gate)
        {
            _pingAnswered = answered;
        }
    }

    /// <summary>Forgets every logged call.</summary>
    public void ClearCalls()
    {
        lock (_gate)
        {
            _calls.Clear();
        }
    }

    /// <summary>
    /// Makes every later call to <paramref name="method"/> throw <paramref name="exception"/> at
    /// <paramref name="stage"/>, replacing any failure set for that method. A read throws once its call is logged;
    /// a write throws at its first data statement (<see cref="FakeFailureStage.BeforeWrite"/>) or once its change is
    /// applied and logged (<see cref="FakeFailureStage.AfterWrite"/>). <c>UpdateAsync</c> and <c>SoftDeleteAsync</c>
    /// reach that stage only for an active record that passes the gates.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <see cref="DataStoreUnavailableException"/> or <see cref="DataStoreWriteException"/> must report the outcome
    /// that the state its stage leaves agrees with, because the API reports that outcome as <c>writeOutcome</c>:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <see cref="WriteOutcome.NotWritten"/> at <see cref="FakeFailureStage.BeforeWrite"/>, where nothing has changed;
    /// </description></item>
    /// <item><description>
    /// <see cref="WriteOutcome.Written"/> only at <see cref="FakeFailureStage.AfterWrite"/> of <c>InsertAsync</c>,
    /// <c>UpdateAsync</c> or <c>SoftDeleteAsync</c>, where the change is applied; every other member treats that stage
    /// as <see cref="FakeFailureStage.BeforeWrite"/>;
    /// </description></item>
    /// <item><description>
    /// <see cref="WriteOutcome.Unknown"/> at either stage, each modelling one of the two states it covers: the change
    /// lost at <see cref="FakeFailureStage.BeforeWrite"/>, the change kept at
    /// <see cref="FakeFailureStage.AfterWrite"/>.
    /// </description></item>
    /// </list>
    /// <para>
    /// Any other exception carries no outcome, because the API derives one from whether the write call started, so it
    /// is accepted at either stage. A test asserts the resulting state (<see cref="GetRecord"/> or
    /// <see cref="GetRecords"/>) and the call log (<see cref="WriteCalls"/>) as well as the response, so the outcome
    /// the response claims is checked against the data.
    /// </para>
    /// </remarks>
    /// <param name="method">An <see cref="IDataStore"/> member name, for example <c>nameof(IDataStore.InsertAsync)</c>.</param>
    /// <param name="exception">The exception to throw.</param>
    /// <param name="stage">When to throw it.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="method"/> names no <see cref="IDataStore"/> member, or <paramref name="exception"/> is a store
    /// exception whose outcome contradicts the state <paramref name="stage"/> leaves. No failure is set then, and any
    /// failure set earlier for <paramref name="method"/> stays.
    /// </exception>
    public void ThrowOn(string method, Exception exception, FakeFailureStage stage = FakeFailureStage.BeforeWrite)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(exception);
        if (!MemberNames.Contains(method))
        {
            throw new ArgumentException("The method is not an IDataStore member.", nameof(method));
        }

        if (!Enum.IsDefined(stage))
        {
            throw new ArgumentOutOfRangeException(nameof(stage));
        }

        // Only a write method's AfterWrite follows an applied change; every other member, and every BeforeWrite, raises
        // before any change.
        var changeApplied = stage == FakeFailureStage.AfterWrite
            && method is nameof(IDataStore.InsertAsync) or nameof(IDataStore.UpdateAsync)
                or nameof(IDataStore.SoftDeleteAsync);
        ThrowIfOutcomeContradicts(exception, changeApplied);

        lock (_gate)
        {
            _failures[method] = (exception, stage);
        }
    }

    /// <summary>
    /// Makes every member throw <paramref name="exception"/> before doing anything, its call log entry included.
    /// </summary>
    /// <remarks>
    /// Nothing is ever changed, so a <see cref="DataStoreUnavailableException"/> or
    /// <see cref="DataStoreWriteException"/> reports <see cref="WriteOutcome.NotWritten"/>, or
    /// <see cref="WriteOutcome.Unknown"/> for the unconfirmed state in which nothing was kept; any other exception is
    /// accepted. A test asserts the unchanged state (<see cref="GetRecord"/> or <see cref="GetRecords"/>) and the empty
    /// call log (<see cref="WriteCalls"/>) as well as the response.
    /// </remarks>
    /// <param name="exception">The exception to throw.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="exception"/> is a store exception that reports <see cref="WriteOutcome.Written"/>. No failure is
    /// set then, and any failure set earlier stays.
    /// </exception>
    public void ThrowOnAny(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ThrowIfOutcomeContradicts(exception, changeApplied: false);
        lock (_gate)
        {
            _failureForAll = exception;
        }
    }

    // A store exception's outcome must agree with the state its injection point leaves: written needs the change
    // applied, notWritten needs it absent. Unknown covers both states, so it fits either, as does an outcome outside
    // the enum, which the API's error body reports as unknown. Any other exception carries no outcome. The message is
    // fixed text that names no value.
    private static void ThrowIfOutcomeContradicts(Exception exception, bool changeApplied)
    {
        WriteOutcome? outcome = exception switch
        {
            DataStoreUnavailableException unavailable => unavailable.Outcome,
            DataStoreWriteException failed => failed.Outcome,
            _ => null,
        };

        var contradicts = outcome switch
        {
            WriteOutcome.Written => !changeApplied,
            WriteOutcome.NotWritten => changeApplied,
            _ => false,
        };

        if (contradicts)
        {
            throw new ArgumentException(
                "The exception's write outcome contradicts the state this stage leaves: written needs AfterWrite on a "
                + "write method, notWritten needs BeforeWrite.",
                nameof(exception));
        }
    }

    /// <summary>Removes every injected failure.</summary>
    public void ClearFailures()
    {
        lock (_gate)
        {
            _failures.Clear();
            _failureForAll = null;
        }
    }


    /// <inheritdoc />
    public Task<IReadOnlyList<FormSummary>> ListFormsAsync(CancellationToken cancellationToken) =>
        Complete<IReadOnlyList<FormSummary>>(() =>
        {
            lock (_gate)
            {
                BeginRead(nameof(ListFormsAsync), cancellationToken);
                return Array.AsReadOnly(_forms.Values
                    .Select(state => new FormSummary(
                        state.Form.Metadata.ViewId,
                        state.Form.Metadata.Name,
                        state.Form.Metadata.Pages.Count))
                    .ToArray());
            }
        });

    /// <inheritdoc />
    public Task<FormMetadata?> GetFormMetadataAsync(int viewId, CancellationToken cancellationToken) =>
        Complete<FormMetadata?>(() =>
        {
            lock (_gate)
            {
                BeginRead(nameof(GetFormMetadataAsync), cancellationToken);
                return _forms.TryGetValue(viewId, out var state) ? state.Form.Metadata : null;
            }
        });

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, IReadOnlyList<FieldOption>>> GetOptionsAsync(
        FormMetadata form,
        CancellationToken cancellationToken) =>
        Complete<IReadOnlyDictionary<string, IReadOnlyList<FieldOption>>>(() =>
        {
            ArgumentNullException.ThrowIfNull(form);
            lock (_gate)
            {
                BeginRead(nameof(GetOptionsAsync), cancellationToken);
                var state = Resolve(form);
                var options = new Dictionary<string, IReadOnlyList<FieldOption>>(StringComparer.OrdinalIgnoreCase);
                foreach (var field in state.DataFields.Where(field => field.FieldType == FieldType.LegalValues))
                {
                    IEnumerable<FieldOption> configured = FindOptions(state.Form, field.Name);
                    // Mirrors SqlMetadataReader, the one owner of the Sort = 1 ordering: the current culture read at
                    // the call, with StringSort, case-sensitive, then ordinal for texts the culture ranks equal.
                    if (field.Field.Sort)
                    {
                        StringComparer comparer =
                            CultureInfo.CurrentCulture.CompareInfo.GetStringComparer(CompareOptions.StringSort);
                        configured = configured
                            .OrderBy(option => option.Label, comparer)
                            .ThenBy(option => option.Label, StringComparer.Ordinal);
                    }

                    options.TryAdd(field.Name, Array.AsReadOnly(configured.ToArray()));
                }

                return new ReadOnlyDictionary<string, IReadOnlyList<FieldOption>>(options);
            }
        });

    /// <inheritdoc />
    public Task<string?> GetCheckCodeAsync(int viewId, CancellationToken cancellationToken) =>
        Complete<string?>(() =>
        {
            lock (_gate)
            {
                BeginRead(nameof(GetCheckCodeAsync), cancellationToken);
                return _forms.TryGetValue(viewId, out var state) ? state.Form.CheckCode : null;
            }
        });

    /// <inheritdoc />
    public Task<int> CountAsync(FormMetadata form, bool includeDeleted, CancellationToken cancellationToken) =>
        Complete(() =>
        {
            ArgumentNullException.ThrowIfNull(form);
            lock (_gate)
            {
                BeginRead(nameof(CountAsync), cancellationToken);
                return Resolve(form).Selected(includeDeleted).Count;
            }
        });

    /// <inheritdoc />
    public Task<RecordDto?> GetAtPositionAsync(
        FormMetadata form,
        int position,
        bool includeDeleted,
        CancellationToken cancellationToken) =>
        Complete<RecordDto?>(() =>
        {
            ArgumentNullException.ThrowIfNull(form);
            lock (_gate)
            {
                BeginRead(nameof(GetAtPositionAsync), cancellationToken);
                var state = Resolve(form);
                if (!RecordQueryRules.IsValidPosition(position))
                {
                    return null;
                }

                // The row and its intrinsic coordinates are read under one hold of the lock, the fake's one
                // consistent read, as the SQL store reads them in one statement.
                var selected = state.Selected(includeDeleted);
                var offset = RecordQueryRules.PositionToOffset(position);
                return offset < selected.Count ? Dto(state, selected[(int)offset]) : null;
            }
        });

    /// <inheritdoc />
    public Task<RecordDto?> GetByIdAsync(
        FormMetadata form,
        string globalRecordId,
        CancellationToken cancellationToken) =>
        Complete<RecordDto?>(() =>
        {
            ArgumentNullException.ThrowIfNull(form);
            ArgumentNullException.ThrowIfNull(globalRecordId);
            lock (_gate)
            {
                BeginRead(nameof(GetByIdAsync), cancellationToken);
                var state = Resolve(form);
                return state.Find(globalRecordId) is { } row ? Dto(state, row) : null;
            }
        });

    /// <inheritdoc />
    public Task<NavigationPosition?> GetSetPositionAsync(
        FormMetadata form,
        string globalRecordId,
        bool includeDeleted,
        CancellationToken cancellationToken) =>
        Complete<NavigationPosition?>(() =>
        {
            ArgumentNullException.ThrowIfNull(form);
            ArgumentNullException.ThrowIfNull(globalRecordId);
            lock (_gate)
            {
                BeginRead(nameof(GetSetPositionAsync), cancellationToken);
                var state = Resolve(form);
                if (state.Find(globalRecordId) is not { } row)
                {
                    return null;
                }

                // Base rows only: page-row presence plays no part, so a record missing a page row is still placed.
                var selected = state.Selected(includeDeleted);
                int? position = RecordQueryRules.IsInSelectedSet(row.RecStatus, includeDeleted)
                    ? PositionOf(selected, row.UniqueKey)
                    : null;
                return new NavigationPosition(position, selected.Count);
            }
        });

    /// <inheritdoc />
    public Task<RecordPage> ListAsync(FormMetadata form, RecordListQuery query, CancellationToken cancellationToken) =>
        Complete(() =>
        {
            ArgumentNullException.ThrowIfNull(form);
            ArgumentNullException.ThrowIfNull(query);
            FormState state;
            ImmutableList<StoredRow> rows;
            lock (_gate)
            {
                BeginRead(nameof(ListAsync), cancellationToken);
                state = Resolve(form);
                rows = state.Rows;
            }

            // One pass over the rows taken under the lock, outside it: the filter reads only the grid columns, every
            // match is counted, and only the page's rows are projected. The offset is 64-bit, so a valid page far past
            // the end gives no item rather than overflowing.
            var columns = GridColumns(state);
            var filter = query.Filter;
            var offset = query.Offset;
            var items = new List<RecordSummary>(Math.Min(query.PageSize, rows.Count));
            var total = 0;
            foreach (var row in rows)
            {
                if (!RecordQueryRules.IsInSelectedSet(row.RecStatus, query.IncludeDeleted)
                    || (filter is not null && !columns.Any(column => Contains(ReadField(row, column.Field), filter))))
                {
                    continue;
                }

                if (total >= offset && items.Count < query.PageSize)
                {
                    items.Add(new RecordSummary(
                        row.GlobalRecordId,
                        row.UniqueKey,
                        row.RecStatus,
                        GridValues(row, columns)));
                }

                total++;
            }

            return new RecordPage(items.AsReadOnly(), total, query.Page, query.PageSize);
        });

    /// <inheritdoc />
    public Task<SearchResponse> SearchAsync(
        FormMetadata form,
        IReadOnlyList<SearchCondition> conditions,
        bool includeDeleted,
        string? excludeGlobalRecordId,
        CancellationToken cancellationToken) =>
        Complete(() =>
        {
            ArgumentNullException.ThrowIfNull(form);
            ArgumentNullException.ThrowIfNull(conditions);
            FormState state;
            ImmutableList<StoredRow> rows;
            lock (_gate)
            {
                BeginRead(nameof(SearchAsync), cancellationToken);
                state = Resolve(form);
                rows = state.Rows;
            }

            var resolved = ResolveConditions(state, conditions);
            var columns = GridColumns(state);
            var pageIds = state.PageIds.ToArray();

            // One pass over the rows taken under the lock, outside it. Positions are numbered over the selected set
            // before any criterion, so they equal navigation positions; the page-row join, the excluded id and the
            // criteria, each reading only its own field, then filter before the total and the cap, and only the
            // capped items are projected.
            var items = new List<SearchResult>();
            var position = 0;
            var total = 0;
            foreach (var row in rows)
            {
                if (!RecordQueryRules.IsInSelectedSet(row.RecStatus, includeDeleted))
                {
                    continue;
                }

                position++;
                if (!pageIds.All(row.PresentPageIds.Contains)
                    || (excludeGlobalRecordId is not null
                        && string.Equals(row.GlobalRecordId, excludeGlobalRecordId, StringComparison.OrdinalIgnoreCase))
                    || !resolved.All(condition => condition.Matches(row)))
                {
                    continue;
                }

                total++;
                if (items.Count < RecordQueryRules.SearchCap)
                {
                    items.Add(new SearchResult(
                        row.GlobalRecordId,
                        row.UniqueKey,
                        row.RecStatus,
                        position,
                        GridValues(row, columns)));
                }
            }

            return SearchResponse.Create(items.AsReadOnly(), total);
        });

    /// <inheritdoc />
    public Task<bool> PingAsync(CancellationToken cancellationToken) =>
        Complete(() =>
        {
            lock (_gate)
            {
                BeginRead(nameof(PingAsync), cancellationToken);
                return _pingAnswered;
            }
        });

    /// <inheritdoc />
    public Task<FormPermissions?> GetPermissionsAsync(FormMetadata form, CancellationToken cancellationToken) =>
        Complete<FormPermissions?>(() =>
        {
            ArgumentNullException.ThrowIfNull(form);
            lock (_gate)
            {
                BeginRead(nameof(GetPermissionsAsync), cancellationToken);
                return Resolve(form).Permissions;
            }
        });


    /// <inheritdoc />
    public Task<RecordDto> InsertAsync(
        FormMetadata form,
        IReadOnlyDictionary<string, FieldValue> values,
        SaveStamp stamp,
        CancellationToken cancellationToken) =>
        Complete(() =>
        {
            ArgumentNullException.ThrowIfNull(form);
            ArgumentNullException.ThrowIfNull(values);
            ArgumentNullException.ThrowIfNull(stamp);
            lock (_gate)
            {
                ThrowIfEveryMemberFails();
                var state = Resolve(form);
                var normalised = NormaliseValues(state, values, nameof(values));

                // The first data statement: the base-row insert.
                ThrowInjected(nameof(InsertAsync), FakeFailureStage.BeforeWrite);
                cancellationToken.ThrowIfCancellationRequested();
                _calls.Add(new FakeStoreCall(nameof(InsertAsync), IsWrite: true));

                var stored = new Dictionary<string, FieldValue>(state.DataFields.Count, StringComparer.OrdinalIgnoreCase);
                foreach (var field in state.DataFields)
                {
                    stored[field.Name] = normalised.TryGetValue(field.Name, out var value) ? value : FieldValue.Null;
                }

                var row = new StoredRow(
                    NewGlobalRecordId(),
                    state.NextUniqueKey(),
                    RecordQueryRules.ActiveRecStatus,
                    new ReadOnlyDictionary<string, FieldValue>(stored),
                    new ReadOnlySet<int>(new HashSet<int>(state.PageIds)),
                    stamp,
                    stamp);
                state.Add(row);
                var snapshot = Dto(state, row);

                ThrowInjected(nameof(InsertAsync), FakeFailureStage.AfterWrite);
                return snapshot;
            }
        });

    /// <inheritdoc />
    public Task<RecordWriteResult> UpdateAsync(
        FormMetadata form,
        string globalRecordId,
        IReadOnlyDictionary<string, FieldValue> submitted,
        SaveStamp stamp,
        Func<StoredRecord, RecordValidationResult> requiredGate,
        FormPermissions? permissions,
        CancellationToken cancellationToken) =>
        Complete(() =>
        {
            ArgumentNullException.ThrowIfNull(form);
            ArgumentNullException.ThrowIfNull(globalRecordId);
            ArgumentNullException.ThrowIfNull(submitted);
            ArgumentNullException.ThrowIfNull(stamp);
            ArgumentNullException.ThrowIfNull(requiredGate);
            lock (_gate)
            {
                ThrowIfEveryMemberFails();
                var state = Resolve(form);
                var normalised = NormaliseValues(state, submitted, nameof(submitted));

                // The locked read of the base row and every page row.
                _calls.Add(new FakeStoreCall(nameof(UpdateAsync), IsWrite: false));
                cancellationToken.ThrowIfCancellationRequested();
                if (state.Find(globalRecordId) is not { } row)
                {
                    return RecordWriteResult.NotFound();
                }

                if (row.RecStatus == RecordQueryRules.DeletedRecStatus)
                {
                    return RecordWriteResult.Deleted();
                }

                // Every page whose row the locked read found missing needs INSERT, whether or not a submitted field
                // sits on it: the interface's contract, which this follows over a narrower per-submitted-page reading.
                if (permissions is not null
                    && state.Form.Metadata.Pages.Any(page =>
                        !row.PresentPageIds.Contains(page.PageId) && !permissions.CanInsertInto(page.TableName)))
                {
                    return RecordWriteResult.Forbidden();
                }

                RecordValidationResult gate;
                try
                {
                    gate = requiredGate(Stored(state, row))
                        ?? throw new InvalidOperationException("The required gate returned no result.");
                }
                catch (Exception exception)
                {
                    throw new DataStoreWriteException(nameof(UpdateAsync), WriteOutcome.NotWritten, null, exception);
                }

                if (!gate.IsValid)
                {
                    return RecordWriteResult.ValidationFailed(gate);
                }

                // The first data statement: the base-row stamp update.
                ThrowInjected(nameof(UpdateAsync), FakeFailureStage.BeforeWrite);
                cancellationToken.ThrowIfCancellationRequested();
                _calls.Add(new FakeStoreCall(nameof(UpdateAsync), IsWrite: true));

                // The published row is never changed: the update builds its replacement from copies.
                var stored = new Dictionary<string, FieldValue>(row.Values, StringComparer.OrdinalIgnoreCase);
                var present = new HashSet<int>(row.PresentPageIds);
                foreach (var field in state.DataFields)
                {
                    if (!normalised.TryGetValue(field.Name, out var value))
                    {
                        continue;
                    }

                    // A missing page row is inserted holding only its key, then updated, so its other fields stay null.
                    if (present.Add(field.Page.PageId))
                    {
                        foreach (var pageField in state.DataFields.Where(other => other.Page.PageId == field.Page.PageId))
                        {
                            stored[pageField.Name] = FieldValue.Null;
                        }
                    }

                    stored[field.Name] = value;
                }

                var updated = row with
                {
                    Values = new ReadOnlyDictionary<string, FieldValue>(stored),
                    PresentPageIds = new ReadOnlySet<int>(present),
                    LastSave = stamp,
                };
                state.Replace(row, updated);
                var snapshot = Dto(state, updated);

                ThrowInjected(nameof(UpdateAsync), FakeFailureStage.AfterWrite);
                return RecordWriteResult.Success(snapshot);
            }
        });

    /// <inheritdoc />
    public Task<SoftDeleteResult> SoftDeleteAsync(
        FormMetadata form,
        string globalRecordId,
        SaveStamp stamp,
        CancellationToken cancellationToken) =>
        Complete(() =>
        {
            ArgumentNullException.ThrowIfNull(form);
            ArgumentNullException.ThrowIfNull(globalRecordId);
            ArgumentNullException.ThrowIfNull(stamp);
            lock (_gate)
            {
                ThrowIfEveryMemberFails();
                var state = Resolve(form);

                // The locked read of the base row, logged as a read like UpdateAsync's, so Calls shows that it ran.
                _calls.Add(new FakeStoreCall(nameof(SoftDeleteAsync), IsWrite: false));
                cancellationToken.ThrowIfCancellationRequested();
                if (state.Find(globalRecordId) is not { } row)
                {
                    return SoftDeleteResult.NotFound;
                }

                if (row.RecStatus == RecordQueryRules.DeletedRecStatus)
                {
                    return SoftDeleteResult.AlreadyDeleted;
                }

                ThrowInjected(nameof(SoftDeleteAsync), FakeFailureStage.BeforeWrite);
                cancellationToken.ThrowIfCancellationRequested();
                _calls.Add(new FakeStoreCall(nameof(SoftDeleteAsync), IsWrite: true));

                // Base row only: values and page rows are untouched and nothing cascades.
                state.Replace(row, row with { RecStatus = RecordQueryRules.DeletedRecStatus, LastSave = stamp });

                ThrowInjected(nameof(SoftDeleteAsync), FakeFailureStage.AfterWrite);
                return SoftDeleteResult.Deleted;
            }
        });

    // Completes synchronously, delivering a failure through the task as an asynchronous store would, so a caller
    // that starts several calls before awaiting them sees the same behaviour as with the SQL store.
    private static Task<T> Complete<T>(Func<T> body)
    {
        try
        {
            return Task.FromResult(body());
        }
        catch (OperationCanceledException exception) when (exception.CancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<T>(exception.CancellationToken);
        }
        catch (Exception exception)
        {
            return Task.FromException<T>(exception);
        }
    }

    // The format the SQL store writes: a lower-case hyphenated GUID, as legacy Guid.NewGuid().ToString() gives it.
    private static string NewGlobalRecordId() => Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture);

    private static int PositionOf(IReadOnlyList<StoredRow> selected, int uniqueKey) =>
        selected.Count(row => row.UniqueKey <= uniqueKey);

    private static IReadOnlyList<FieldOption> FindOptions(FakeForm form, string fieldName)
    {
        if (form.Options.TryGetValue(fieldName, out var exact))
        {
            return exact;
        }

        foreach (var (name, options) in form.Options)
        {
            if (string.Equals(name, fieldName, StringComparison.OrdinalIgnoreCase))
            {
                return options;
            }
        }

        return [];
    }

    // Values as the record methods read them: every data field in storage order, null for a page whose row is
    // missing, as the LEFT JOIN of the SQL store reads it.
    private static IReadOnlyDictionary<string, FieldValue> Snapshot(FormState state, StoredRow row)
    {
        var values = RecordQueryRules.CreateValuesMap(state.DataFields.Count);
        foreach (var field in state.DataFields)
        {
            values.TryAdd(field.Name, ReadField(row, field));
        }

        return new ReadOnlyDictionary<string, FieldValue>(values);
    }

    // One data field of a row as Snapshot reads it: null when the field's page row is missing.
    private static FieldValue ReadField(StoredRow row, DataFieldMetadata field) =>
        row.PresentPageIds.Contains(field.Page.PageId) && row.Values.TryGetValue(field.Name, out var value)
            ? value
            : FieldValue.Null;

    // The grid columns, each with the data field its name resolves to. Snapshot keys a name by that field, so reading
    // it gives exactly what RecordQueryRules.ProjectValues takes from a full snapshot.
    private static (string Name, DataFieldMetadata Field)[] GridColumns(FormState state) =>
        RecordQueryRules.GetGridColumns(state.DataFields)
            .Select(column => (column.Name, state.DataFields.Find(column.Name) ?? column))
            .ToArray();

    // The grid-only values of a returned row, in column order, keyed as RecordQueryRules.ProjectValues keys them: a
    // name that differs only by case keeps the first column's entry.
    private static IReadOnlyDictionary<string, FieldValue> GridValues(
        StoredRow row,
        (string Name, DataFieldMetadata Field)[] columns)
    {
        var values = RecordQueryRules.CreateValuesMap(columns.Length);
        foreach (var (name, field) in columns)
        {
            values.TryAdd(name, ReadField(row, field));
        }

        return values;
    }

    private static StoredRecord Stored(FormState state, StoredRow row) =>
        new(row.GlobalRecordId, row.UniqueKey, row.RecStatus, Snapshot(state, row));

    // A record with its intrinsic coordinates, for the record reads and the response snapshot of a write. Callers hold
    // the lock, so the row and its coordinates come from one state.
    private static RecordDto Dto(FormState state, StoredRow row)
    {
        var selected = state.Selected(RecordQueryRules.IntrinsicIncludesDeleted(row.RecStatus));
        return RecordDto.From(Stored(state, row), PositionOf(selected, row.UniqueKey), selected.Count);
    }

    private static FakeRecordState State(FormState state, StoredRow row) => new(
        row.GlobalRecordId,
        row.UniqueKey,
        row.RecStatus,
        Snapshot(state, row),
        new ReadOnlySet<int>(new HashSet<int>(row.PresentPageIds)),
        row.FirstSave,
        row.LastSave);

    // Resolves values to canonical data-field names, rejecting what the SQL builders reject before any statement.
    private static Dictionary<string, FieldValue> NormaliseValues(
        FormState state,
        IReadOnlyDictionary<string, FieldValue> values,
        string paramName)
    {
        var normalised = new Dictionary<string, FieldValue>(values.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in values)
        {
            if (name is null || !state.DataFields.TryGet(name, out var field))
            {
                throw new ArgumentException("A value names no data field of the form.", paramName);
            }

            // The kind is checked before the column, in the SQL builders' order: an integer column admits a number
            // and an option index alike, yet an option field holds only an index and a number field only a number.
            if (!value.IsNull && value.Kind != FieldTypeMapper.ValueKind(field.FieldType))
            {
                throw new ArgumentException("A value is not of its field's kind.", paramName);
            }

            if (!FieldTypeMapper.IsRepresentable(field.Column, value))
            {
                throw new ArgumentException("A value does not fit its column.", paramName);
            }

            if (!normalised.TryAdd(field.Name, value))
            {
                throw new ArgumentException("Two values name the same field ignoring case.", paramName);
            }
        }

        return normalised;
    }

    private static bool Contains(FieldValue value, string filter) =>
        value.ToInvariantString() is { } text && text.Contains(filter, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<ResolvedCondition> ResolveConditions(
        FormState state,
        IReadOnlyList<SearchCondition> conditions)
    {
        var resolved = new List<ResolvedCondition>(conditions.Count);
        foreach (var condition in conditions)
        {
            if (condition is null)
            {
                throw new ArgumentException("A search condition must not be null.", nameof(conditions));
            }

            if (condition.Alternatives.Count == 0)
            {
                throw new ArgumentException("A search condition needs an alternative.", nameof(conditions));
            }

            switch (condition.Target)
            {
                case SearchTarget.PageField:
                    if (!state.DataFields.TryGet(condition.FieldName, out var field))
                    {
                        throw new ArgumentException("A search condition names no data field of the form.", nameof(conditions));
                    }

                    if (field.FieldType != condition.FieldType)
                    {
                        throw new ArgumentException(
                            "A search condition's field type is not the type of its field.", nameof(conditions));
                    }

                    // A float column compares binary64 doubles, as SQL float comparison does; an integer column
                    // compares exactly.
                    var numbers = field.Column.Family == ColumnFamily.Float
                        ? NumericComparison.Binary64
                        : NumericComparison.Exact;
                    resolved.Add(new ResolvedCondition(condition, row => ReadField(row, field), numbers));
                    break;

                case SearchTarget.BaseGlobalRecordId:
                    resolved.Add(new ResolvedCondition(
                        condition, row => FieldValue.FromText(row.GlobalRecordId), NumericComparison.Exact));
                    break;

                // RecStatus is an int column, so it compares exactly.
                case SearchTarget.BaseRecStatus:
                    resolved.Add(new ResolvedCondition(
                        condition, row => FieldValue.FromInteger(row.RecStatus), NumericComparison.Exact));
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(conditions), "The search target is not a defined member.");
            }
        }

        return resolved;
    }

    // A null stored value satisfies only IS NULL, as SQL's three-valued comparison gives. A LIKE alternative matches
    // through its prepared pattern.
    private static bool Satisfies(
        SearchAlternative alternative,
        LikePattern? pattern,
        FieldValue stored,
        NumericComparison numbers)
    {
        if (alternative.Operator == SearchOperator.IsNull)
        {
            return stored.IsNull;
        }

        if (stored.IsNull || alternative.Value is not { } criterion)
        {
            return false;
        }

        return alternative.Operator switch
        {
            SearchOperator.Like => stored.TryGetText(out var text) && pattern is not null && pattern.IsMatch(text),
            SearchOperator.PrefixEqual255 => stored.TryGetText(out var text)
                && criterion.TryGetText(out var other)
                && string.Equals(Prefix255(text), Prefix255(other), StringComparison.OrdinalIgnoreCase),
            SearchOperator.Equal => Compare(stored, criterion, numbers) is 0,
            SearchOperator.NotEqual => Compare(stored, criterion, numbers) is { } order && order != 0,
            SearchOperator.Less => Compare(stored, criterion, numbers) is < 0,
            SearchOperator.LessOrEqual => Compare(stored, criterion, numbers) is <= 0,
            SearchOperator.Greater => Compare(stored, criterion, numbers) is > 0,
            SearchOperator.GreaterOrEqual => Compare(stored, criterion, numbers) is >= 0,
            _ => throw new ArgumentOutOfRangeException(nameof(alternative), "The search operator is not a defined member."),
        };
    }

    private static string Prefix255(string text) =>
        text.Length <= PrefixCompareLength ? text : text[..PrefixCompareLength];

    // The order of two values of one comparable kind, or null when the kinds differ, so that no operator matches.
    // Numbers compare as their column's family stores them, and option indexes, held in integer columns, as integers.
    private static int? Compare(FieldValue stored, FieldValue criterion, NumericComparison numbers)
    {
        if (stored.TryGetText(out var left) && criterion.TryGetText(out var right))
        {
            return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
        }

        if (stored.TryGetNumber(out var leftNumber) && criterion.TryGetNumber(out var rightNumber))
        {
            return numbers == NumericComparison.Binary64
                ? leftNumber.CompareTo(rightNumber)
                : CompareExactly(stored, criterion);
        }

        if (stored.TryGetIndex(out var leftIndex) && criterion.TryGetIndex(out var rightIndex))
        {
            return leftIndex.CompareTo(rightIndex);
        }

        if (stored.TryGetDate(out var leftDate) && criterion.TryGetDate(out var rightDate))
        {
            return leftDate.CompareTo(rightDate);
        }

        if (stored.TryGetBoolean(out var leftFlag) && criterion.TryGetBoolean(out var rightFlag))
        {
            return leftFlag.CompareTo(rightFlag);
        }

        return null;
    }

    // The exact order of two numbers: as integers when both hold one, an integer against the other's double without
    // rounding either, and as doubles when neither holds an integer.
    private static int CompareExactly(FieldValue left, FieldValue right)
    {
        var leftIsInteger = left.TryGetInteger(out var leftInteger);
        var rightIsInteger = right.TryGetInteger(out var rightInteger);
        return (leftIsInteger, rightIsInteger) switch
        {
            (true, true) => leftInteger.CompareTo(rightInteger),
            (true, false) => CompareWithDouble(leftInteger, right.AsNumber()),
            (false, true) => -CompareWithDouble(rightInteger, left.AsNumber()),
            _ => left.AsNumber().CompareTo(right.AsNumber()),
        };
    }

    // The mathematically exact order, -1, 0 or 1, of an integer and a finite double, where converting either to the
    // other's type could round. -2^63 and 2^63 are exact doubles, and between them the double's floor fits a long.
    private static int CompareWithDouble(long integer, double number)
    {
        if (number >= 9223372036854775808d)
        {
            return -1;
        }

        if (number < -9223372036854775808d)
        {
            return 1;
        }

        var floor = Math.Floor(number);
        var whole = (long)floor;
        if (integer != whole)
        {
            return integer < whole ? -1 : 1;
        }

        // The integer equals the floor, so it is smaller exactly when the double has a fractional part.
        return floor == number ? 0 : -1;
    }

    private FormState Resolve(FormMetadata form) => Resolve(form.ViewId);

    private FormState Resolve(int viewId) =>
        _forms.TryGetValue(viewId, out var state)
            ? state
            : throw new ArgumentException("The store holds no form with this view id.", nameof(viewId));

    // The read pattern: the every-member failure, then the call log, then the method's own failure, then cancellation.
    private void BeginRead(string method, CancellationToken cancellationToken)
    {
        ThrowIfEveryMemberFails();
        _calls.Add(new FakeStoreCall(method, IsWrite: false));
        ThrowInjected(method, stage: null);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private void ThrowIfEveryMemberFails()
    {
        if (_failureForAll is { } exception)
        {
            throw exception;
        }
    }

    // A null stage matches either stage: a read treats AfterWrite as BeforeWrite.
    private void ThrowInjected(string method, FakeFailureStage? stage)
    {
        if (_failures.TryGetValue(method, out var failure) && (stage is null || failure.Stage == stage))
        {
            throw failure.Exception;
        }
    }

    private sealed class FormState(FakeForm form)
    {
        private int _nextUniqueKey = 1;

        public FakeForm Form { get; } = form;

        public DataFieldSet DataFields { get; } = FormDefinitionFactory.GetDataFields(form.Metadata);

        // Kept in UniqueKey order: keys only grow, and rows are only appended. Each write publishes a new list under
        // the store's lock, so a list a reader has taken never changes.
        public ImmutableList<StoredRow> Rows { get; private set; } = ImmutableList<StoredRow>.Empty;

        public FormPermissions? Permissions { get; set; }

        public IEnumerable<int> PageIds => Form.Metadata.Pages.Select(page => page.PageId);

        public StoredRow? Find(string globalRecordId) =>
            Rows.Find(row => string.Equals(row.GlobalRecordId, globalRecordId, StringComparison.OrdinalIgnoreCase));

        public IReadOnlyList<StoredRow> Selected(bool includeDeleted) =>
            Rows.Where(row => RecordQueryRules.IsInSelectedSet(row.RecStatus, includeDeleted)).ToArray();

        public int NextUniqueKey() => _nextUniqueKey++;

        public void Add(StoredRow row) => Rows = Rows.Add(row);

        // The row is matched by reference, so only the exact row the write read is replaced.
        public void Replace(StoredRow current, StoredRow updated) =>
            Rows = Rows.Replace(current, updated, ReferenceEqualityComparer.Instance);
    }

    // One record, immutable once published: a write replaces the row rather than changing it. Values is keyed ignoring
    // case and holds every data field; PresentPageIds holds the pages whose page row exists.
    private sealed record StoredRow(
        string GlobalRecordId,
        int UniqueKey,
        int RecStatus,
        IReadOnlyDictionary<string, FieldValue> Values,
        IReadOnlySet<int> PresentPageIds,
        SaveStamp? FirstSave,
        SaveStamp? LastSave);

    // How a number condition compares its column: as binary64 doubles for a float column, as SQL float comparison
    // does, and exactly for an integer column, so adjacent bigint values beyond 2^53 stay distinct.
    private enum NumericComparison
    {
        Exact,
        Binary64,
    }

    // Read takes only the condition's own column from a row. Each LIKE pattern is prepared once, when
    // ResolveConditions builds the condition, rather than once per row.
    private sealed record ResolvedCondition(
        SearchCondition Condition,
        Func<StoredRow, FieldValue> Read,
        NumericComparison Numbers)
    {
        private readonly LikePattern?[] _patterns = [.. Condition.Alternatives.Select(LikePattern.For)];

        public bool Matches(StoredRow row)
        {
            var stored = Read(row);
            for (var index = 0; index < _patterns.Length; index++)
            {
                if (Satisfies(Condition.Alternatives[index], _patterns[index], stored, Numbers))
                {
                    return true;
                }
            }

            return false;
        }
    }

    // SQL LIKE with no ESCAPE clause, prepared once per alternative: % matches any run of characters, _ any one
    // character and [...] one character of a class, where a leading ^ negates the class and x-y is a range. Every [
    // opens a class, which the first ] after it closes or, with no such ], the end of the pattern; inside a class %, _
    // and [ are members, and outside one a ] is a literal. A class with no member, such as [] or a final [, matches no
    // character, so the pattern cannot match there; negated, as [^] or a final [^, it matches any one character. The
    // match must cover the whole text, so a trailing line feed is a character that _ or % has to cover. Each UTF-16
    // code unit compares ignoring case: a literal by its invariant upper case, a class member by the character or
    // either of its invariant case forms. That is ordinal case-insensitivity only, with no collation parity, so ranges
    // and members such as ^ and - follow code-unit order rather than a collation's weights. Matching keeps a single
    // backtrack point, the last %, so any pattern needs no memory beyond its tokens and at most text length x token
    // count steps.
    private sealed class LikePattern
    {
        private readonly Token[] _tokens;

        private LikePattern(Token[] tokens) => _tokens = tokens;

        private enum TokenKind
        {
            Literal,
            AnyCharacter,
            AnyRun,
            Class,
        }

        // The prepared pattern of a LIKE alternative, or null for any other operator.
        public static LikePattern? For(SearchAlternative alternative) =>
            alternative.Operator == SearchOperator.Like
            && alternative.Value is { } value
            && value.TryGetText(out var pattern)
                ? Parse(pattern)
                : null;

        public bool IsMatch(string text)
        {
            var token = 0;
            var position = 0;

            // The last % reached, and the text position after the run it has absorbed so far.
            var run = -1;
            var resume = 0;
            while (position < text.Length)
            {
                if (token < _tokens.Length && _tokens[token].Kind == TokenKind.AnyRun)
                {
                    run = token++;
                    resume = position;
                }
                else if (token < _tokens.Length && _tokens[token].Matches(text[position]))
                {
                    token++;
                    position++;
                }
                else if (run >= 0)
                {
                    // The last % absorbs one more character, and the tokens after it start again from there.
                    token = run + 1;
                    position = ++resume;
                }
                else
                {
                    return false;
                }
            }

            // The whole text is consumed, so only % may remain, each matching an empty run.
            while (token < _tokens.Length && _tokens[token].Kind == TokenKind.AnyRun)
            {
                token++;
            }

            return token == _tokens.Length;
        }

        private static LikePattern Parse(string pattern)
        {
            var tokens = new List<Token>(pattern.Length);
            for (var index = 0; index < pattern.Length; index++)
            {
                var character = pattern[index];
                switch (character)
                {
                    // Consecutive runs match what one run matches, so they collapse into one.
                    case '%':
                        if (tokens.Count == 0 || tokens[^1].Kind != TokenKind.AnyRun)
                        {
                            tokens.Add(new Token(TokenKind.AnyRun, default, false, null));
                        }

                        break;

                    case '_':
                        tokens.Add(new Token(TokenKind.AnyCharacter, default, false, null));
                        break;

                    // The first ] after the [ closes the class, or else the end of the pattern does.
                    case '[':
                        var close = pattern.IndexOf(']', index + 1);
                        var end = close >= 0 ? close : pattern.Length;
                        tokens.Add(ParseClass(pattern.AsSpan(index + 1, end - index - 1)));
                        index = end;
                        break;

                    default:
                        tokens.Add(new Token(TokenKind.Literal, char.ToUpperInvariant(character), false, null));
                        break;
                }
            }

            return new LikePattern([.. tokens]);
        }

        // [abc], [a-c] and [^a-c]: after an optional leading ^, a hyphen with a member on each side makes a range, and
        // every other character, %, _ and [ included, is a member.
        private static Token ParseClass(ReadOnlySpan<char> content)
        {
            var negated = content.Length > 0 && content[0] == '^';
            var members = negated ? content[1..] : content;
            var ranges = new List<(char Low, char High)>(members.Length);
            for (var index = 0; index < members.Length; index++)
            {
                if (index + 2 < members.Length && members[index + 1] == '-')
                {
                    ranges.Add((members[index], members[index + 2]));
                    index += 2;
                }
                else
                {
                    ranges.Add((members[index], members[index]));
                }
            }

            return new Token(TokenKind.Class, default, negated, [.. ranges]);
        }

        // One pattern element. Literal is a literal's invariant upper case; Ranges are a class's members, a single
        // character being the range from itself to itself. A reversed range such as z-a holds no character, as in SQL
        // Server, while the class's other members still match. A class with no member holds no character, so it
        // matches none and, negated, matches any one.
        private readonly record struct Token(TokenKind Kind, char Literal, bool Negated, (char Low, char High)[]? Ranges)
        {
            // A run is matched by IsMatch, never one character at a time.
            public bool Matches(char character) => Kind switch
            {
                TokenKind.AnyCharacter => true,
                TokenKind.Literal => char.ToUpperInvariant(character) == Literal,
                TokenKind.Class => InClass(character) != Negated,
                _ => false,
            };

            private bool InClass(char character)
            {
                var upper = char.ToUpperInvariant(character);
                var lower = char.ToLowerInvariant(character);
                foreach (var (low, high) in Ranges ?? [])
                {
                    if ((character >= low && character <= high)
                        || (upper >= low && upper <= high)
                        || (lower >= low && lower <= high))
                    {
                        return true;
                    }
                }

                return false;
            }
        }
    }
}
