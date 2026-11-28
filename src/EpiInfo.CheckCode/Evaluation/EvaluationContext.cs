using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using EpiInfo.Domain.Forms;
using EpiInfo.Domain.Metadata;
using EpiInfo.Domain.Values;

namespace EpiInfo.CheckCode.Evaluation;

/// <summary>
/// The field scope of one evaluator run: it resolves check-code identifiers to the form's canonical fields, reports
/// each field's type and group members, and holds the current value of every data field, so later statements read
/// the results of earlier <c>ASSIGN</c> and <c>CLEAR</c> statements.
/// </summary>
/// <remarks>
/// <para>
/// It takes the role legacy <c>Rule_Context</c> and the host's <c>TryGetFieldInfo</c> played for field reads, with
/// no persistent scope: it is created for exactly one call, from the request's values for an <c>Evaluator.Run</c>,
/// or with no values for <c>CheckCodeEngine.Run</c> to choose the <c>Field</c> block, and is discarded with it.
/// Nothing it holds survives the call or is written anywhere.
/// </para>
/// <para>
/// A field is its <c>metaFields</c> row, never its spelling: rows are compared by reference, so two fields whose
/// names differ only by case, on different pages for example, are distinct fields, each with its own value and its
/// own canonical name on every effect. Which rows hold values, and their types, come from
/// <see cref="FormDefinitionFactory.GetDataFields"/>, the same data-field set the field-event service types the
/// request's values against, so a value is read as the field and type it was checked as. Group members come from
/// <see cref="FormDefinitionFactory.GetGroupMembers"/>, the rule the form definition's <c>members</c> follows.
/// </para>
/// <para>
/// Identifiers are trimmed and matched ignoring case, so the seeded program's <c>AGE</c> resolves to the field
/// <c>Age</c>. A blank identifier names no field. Two rules apply, and they differ only in whether another row spelled
/// exactly so outranks a data field equal to the name only ignoring case. "Other rows" below are the rows of
/// <see cref="FormMetadata.Fields"/> outside the data set: labels, groups, unsupported fields, command buttons and
/// system rows.
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Values</b> (<see cref="TryResolve"/> and <see cref="Resolve"/>), for an identifier an expression reads or an
/// <c>ASSIGN</c> writes, where only a data field holds a value: the data field <see cref="DataFieldSet.TryGet"/>
/// resolves it to, spelled exactly so or else the one distinct data spelling equal to it ignoring case; otherwise the
/// first other row in metadata order spelled exactly so; otherwise, when the name is ambiguous among the data fields
/// (<see cref="DataFieldSet.IsAmbiguous"/>), no field; otherwise the first other row in metadata order equal to it
/// ignoring case. A label, group or other row whose name differs only by case never shadows a data field here.
/// </description></item>
/// <item><description>
/// <b>Targets</b> (<see cref="TryResolveTarget"/> and <see cref="ResolveTarget"/>), for a name that designates a row:
/// the fields of <c>ENABLE</c>, <c>DISABLE</c> and <c>CLEAR</c>, a <c>Field</c> block's name and the field an event
/// is raised on. The row spelled exactly so comes first: the data field so spelled if there is one, otherwise the
/// first row of any kind in metadata order so spelled, so the group <c>SECTION</c> stays the group beside a data
/// field <c>Section</c>. Otherwise the data field <see cref="DataFieldSet.TryGet"/> finds ignoring case; otherwise,
/// for a name ambiguous among the data fields, no field; otherwise the first other row in metadata order equal to it
/// ignoring case.
/// </description></item>
/// </list>
/// <para>
/// It enforces no read-only, range, length, mask, legal-value, required or empty-record rule: the engine must
/// evaluate records whose stored values break the current rules, and validation belongs to the save path.
/// </para>
/// <para>
/// It also meters the run's work against <see cref="Evaluator.MaxWork"/>: the evaluator charges each step, and the
/// meter polls the cancellation token while the steps add up, so a single long expression or statement stops when the
/// request is aborted. Each group's members are resolved once per run and kept, so naming a group again costs only its
/// members.
/// </para>
/// <para>
/// An instance serves one run on one thread and is not thread-safe. It holds no static state, so concurrent runs
/// never share anything.
/// </para>
/// </remarks>
public sealed class EvaluationContext
{
    // Steps of work between two polls of the cancellation tokens. The first poll comes once the run has charged this
    // much, and a single large charge, such as a long text, polls at once.
    private const long CancellationPollInterval = 4_096;

    // The maps keyed by a metadata row compare it by reference: rows whose names differ only by case are distinct
    // fields, and a value-equality key would hash every property of the row on each lookup.

    // Current values per data field: the request's values, overlaid by the run's writes.
    private readonly Dictionary<FieldMetadata, FieldValue> _values = new(ReferenceEqualityComparer.Instance);

    // The form's data fields, the set the field-event service typed the request's values against.
    private readonly DataFieldSet _dataFields;

    // The data-field entry, with its mapped type, of each data row.
    private readonly Dictionary<FieldMetadata, DataFieldMetadata> _dataFieldsByRow =
        new(ReferenceEqualityComparer.Instance);

    // The rows outside the data set by exact name and by name ignoring case, the first in metadata order winning in
    // each.
    private readonly Dictionary<string, FieldMetadata> _otherRowsByExactName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FieldMetadata> _otherRowsByName = new(StringComparer.OrdinalIgnoreCase);

    // The types of rows outside the data set, computed on first use.
    private readonly Dictionary<FieldMetadata, FieldType> _otherFieldTypes = new(ReferenceEqualityComparer.Instance);

    // The members of each row whose group expansion was asked for, resolved on first use and kept for the run.
    private readonly Dictionary<FieldMetadata, IReadOnlyList<FieldMetadata>> _groupMembers =
        new(ReferenceEqualityComparer.Instance);

    // The token the context was created with, polled by the work meter for every evaluation in it.
    private readonly CancellationToken _cancellationToken;

    // The token of the Evaluator.Execute call in progress, if any, polled beside _cancellationToken.
    private CancellationToken _callCancellationToken;

    // The steps of work charged so far, never more than Evaluator.MaxWork, and the total at which the tokens are next
    // polled.
    private long _work;
    private long _nextCancellationPoll = CancellationPollInterval;

    // The line of the statement running, which a run out of work reports.
    private int _statementLine;

    /// <summary>
    /// Creates the scope of one run over <paramref name="form"/>, starting from <paramref name="values"/>.
    /// </summary>
    /// <param name="form">The metadata of the form whose program runs.</param>
    /// <param name="values">
    /// The record's current values keyed by data-field name. Each key binds to the data field
    /// <see cref="DataFieldSet.TryGet"/> resolves it to: the field spelled exactly so, or else the one distinct data
    /// spelling equal to it ignoring case. A key that resolves to no data field (a label, group, unsupported field,
    /// system row, unknown name or a name ambiguous among the data fields) is ignored; the field-event service has
    /// already rejected such keys. When several keys bind to one field, the key spelled exactly as its canonical name
    /// wins, and otherwise the first key enumerated. The dictionary is copied: the context keeps no reference to it
    /// and never changes it.
    /// </param>
    /// <param name="cancellationToken">
    /// The request's abort token, polled by the work meter as each evaluation in this context adds up its steps, every
    /// 4,096 steps and on any larger single step, so <see cref="Evaluator.Evaluate"/> of one long expression stops
    /// with <see cref="OperationCanceledException"/>.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="form"/> or <paramref name="values"/> is <see langword="null"/>.
    /// </exception>
    public EvaluationContext(
        FormMetadata form,
        IReadOnlyDictionary<string, FieldValue> values,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(values);

        Form = form;
        _cancellationToken = cancellationToken;
        _dataFields = FormDefinitionFactory.GetDataFields(form);
        foreach (DataFieldMetadata dataField in _dataFields)
        {
            _dataFieldsByRow.TryAdd(dataField.Field, dataField);
        }

        foreach (FieldMetadata field in form.Fields)
        {
            if (!_dataFieldsByRow.ContainsKey(field))
            {
                _otherRowsByExactName.TryAdd(field.Name, field);
                _otherRowsByName.TryAdd(field.Name, field);
            }
        }

        // An exact key wins over every other spelling of its field, whichever is enumerated first: it replaces a value
        // another spelling bound, and another spelling never replaces a value already bound.
        foreach (KeyValuePair<string, FieldValue> entry in values)
        {
            if (!_dataFields.TryGet(entry.Key, out DataFieldMetadata? dataField))
            {
                continue;
            }

            if (string.Equals(entry.Key, dataField.Name, StringComparison.Ordinal))
            {
                _values[dataField.Field] = entry.Value;
            }
            else
            {
                _values.TryAdd(dataField.Field, entry.Value);
            }
        }
    }

    /// <summary>The metadata of the form whose program runs.</summary>
    public FormMetadata Form { get; }

    /// <summary>
    /// The form's <c>metaViews.Name</c>, for example <c>FoodHistory</c>, which every diagnostic of the run carries
    /// as its form.
    /// </summary>
    public string FormName => Form.Name;

    /// <summary>
    /// Resolves an identifier that an expression reads or an <c>ASSIGN</c> writes to the form's field, ignoring case
    /// and surrounding whitespace, by the rule for values: the data field <see cref="DataFieldSet.TryGet"/> resolves
    /// it to; otherwise the first other row in metadata order spelled exactly so; otherwise, for a name ambiguous
    /// among the data fields, no field; otherwise the first other row in metadata order equal to it ignoring case.
    /// </summary>
    /// <param name="identifier">The identifier as written in the program, for example <c>AGE</c>.</param>
    /// <param name="field">
    /// The field's metadata row, with its canonical name such as <c>Age</c>; <see langword="null"/> when none
    /// matches.
    /// </param>
    /// <returns>
    /// Whether the identifier names a field of the form; <see langword="false"/> for a blank one, and for one
    /// ambiguous among the data fields that no other row spells exactly.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="identifier"/> is <see langword="null"/>.</exception>
    public bool TryResolve(string identifier, [NotNullWhen(true)] out FieldMetadata? field)
    {
        ArgumentNullException.ThrowIfNull(identifier);

        string name = identifier.Trim();
        if (_dataFields.TryGet(name, out DataFieldMetadata? dataField))
        {
            field = dataField.Field;
            return true;
        }

        return TryResolveOtherRow(name, out field);
    }

    /// <summary>
    /// Resolves an identifier that an expression reads or an <c>ASSIGN</c> writes, as <see cref="TryResolve"/> does,
    /// and fails the statement when it names no field of the form.
    /// </summary>
    /// <param name="identifier">The identifier as written in the program, for example <c>AGE</c>.</param>
    /// <param name="line">The 1-based line of the statement that names it.</param>
    /// <param name="command">The upper-case keyword of that statement, for example <c>ASSIGN</c>.</param>
    /// <returns>The field with its canonical name.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="identifier"/> or <paramref name="command"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="CheckCodeRuntimeException">
    /// The form has no field of that name, or the name is ambiguous among its data fields. The reason names the
    /// identifier, which is program text, never a value.
    /// </exception>
    public FieldMetadata Resolve(string identifier, int line, string command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (TryResolve(identifier, out FieldMetadata? field))
        {
            return field;
        }

        throw UnknownField(identifier, line, command);
    }

    /// <summary>
    /// Resolves a name that designates a row (a field of <c>ENABLE</c>, <c>DISABLE</c> or <c>CLEAR</c>, a
    /// <c>Field</c> block's name, or the field an event is raised on) to the form's field, ignoring case and
    /// surrounding whitespace, by the rule for targets: the row spelled exactly so, which is the data field so spelled
    /// when there is one and otherwise the first row in metadata order so spelled; otherwise the data field
    /// <see cref="DataFieldSet.TryGet"/> finds ignoring case; otherwise, for a name ambiguous among the data fields, no
    /// field; otherwise the first other row in metadata order equal to it ignoring case.
    /// </summary>
    /// <remarks>
    /// Only another row's exact spelling sets this rule apart from <see cref="TryResolve"/>: beside a data field
    /// <c>Section</c>, <c>ENABLE SECTION</c> names the group <c>SECTION</c> and expands to its members, while an
    /// expression reading <c>SECTION</c> reads the data field <c>Section</c>, because a group holds no value.
    /// </remarks>
    /// <param name="identifier">The name as written in the program, for example <c>SECTION</c>.</param>
    /// <param name="field">
    /// The field's metadata row, with its canonical name; <see langword="null"/> when none matches.
    /// </param>
    /// <returns>
    /// Whether the name designates a field of the form; <see langword="false"/> for a blank one, and for one
    /// ambiguous among the data fields that no other row spells exactly.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="identifier"/> is <see langword="null"/>.</exception>
    public bool TryResolveTarget(string identifier, [NotNullWhen(true)] out FieldMetadata? field)
    {
        ArgumentNullException.ThrowIfNull(identifier);

        string name = identifier.Trim();

        // TryGet answers an exact data spelling before any other, so a match spelled otherwise means no data field is
        // spelled exactly so, and another row spelled exactly so outranks it.
        if (_dataFields.TryGet(name, out DataFieldMetadata? dataField)
            && (string.Equals(dataField.Name, name, StringComparison.Ordinal)
                || !_otherRowsByExactName.ContainsKey(name)))
        {
            field = dataField.Field;
            return true;
        }

        return TryResolveOtherRow(name, out field);
    }

    /// <summary>
    /// Resolves a name that designates a row, as <see cref="TryResolveTarget"/> does, and fails the statement when
    /// it names no field of the form.
    /// </summary>
    /// <param name="identifier">The name as written in the program, for example <c>SECTION</c>.</param>
    /// <param name="line">The 1-based line of the statement that names it.</param>
    /// <param name="command">The upper-case keyword of that statement, for example <c>ENABLE</c>.</param>
    /// <returns>The field with its canonical name.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="identifier"/> or <paramref name="command"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="CheckCodeRuntimeException">
    /// The form has no field of that name, or the name is ambiguous among its data fields. The reason names the
    /// identifier, which is program text, never a value.
    /// </exception>
    public FieldMetadata ResolveTarget(string identifier, int line, string command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (TryResolveTarget(identifier, out FieldMetadata? field))
        {
            return field;
        }

        throw UnknownField(identifier, line, command);
    }

    /// <summary>
    /// The field's type: for a data field the type its <see cref="DataFieldMetadata"/> carries, the one its request
    /// value was typed as; for any other row the type <see cref="FieldTypeMapper.Map"/> gives it, computed once per
    /// row for the run.
    /// </summary>
    /// <param name="field">A row of <see cref="FormMetadata.Fields"/>, matched by reference.</param>
    /// <returns>
    /// The type; <see cref="FieldType.Unsupported"/> for a field the application does not edit. A row outside the
    /// data set holds no value in this form, so one the mapper would give a data type (a row with a column but no
    /// page of the form) is <see cref="FieldType.Unsupported"/> too, and is never assigned.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="field"/> is <see langword="null"/>.</exception>
    public FieldType GetFieldType(FieldMetadata field)
    {
        ArgumentNullException.ThrowIfNull(field);

        if (_dataFieldsByRow.TryGetValue(field, out DataFieldMetadata? dataField))
        {
            return dataField.FieldType;
        }

        if (!_otherFieldTypes.TryGetValue(field, out FieldType type))
        {
            type = FieldTypeMapper.Map(field).FieldType;
            if (FieldTypeMapper.IsDataFieldType(type))
            {
                type = FieldType.Unsupported;
            }

            _otherFieldTypes[field] = type;
        }

        return type;
    }

    /// <summary>
    /// Whether the field is one of the form's data fields, the set <see cref="FormDefinitionFactory.GetDataFields"/>
    /// selects: a page-bound text, multiline, number, date, checkbox, option or legal-values field with a page-table
    /// column. Labels, groups, unsupported fields and system rows are not.
    /// </summary>
    /// <param name="field">A row of <see cref="FormMetadata.Fields"/>, matched by reference.</param>
    /// <returns><see langword="true"/> for a data field.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="field"/> is <see langword="null"/>.</exception>
    public bool IsDataField(FieldMetadata field)
    {
        ArgumentNullException.ThrowIfNull(field);

        return _dataFieldsByRow.ContainsKey(field);
    }

    /// <summary>
    /// Whether the field is a group, whose name in <c>ENABLE</c>, <c>DISABLE</c> and <c>CLEAR</c> stands for its
    /// members.
    /// </summary>
    /// <param name="field">A row of <see cref="FormMetadata.Fields"/>, matched by reference.</param>
    /// <returns><see langword="true"/> for a group.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="field"/> is <see langword="null"/>.</exception>
    public bool IsGroup(FieldMetadata field) => GetFieldType(field) == FieldType.Group;

    /// <summary>
    /// The members of a group, as <see cref="FormDefinitionFactory.GetGroupMembers"/> resolves its
    /// <c>metaFields.List</c> (for example <c>Race,Ethnicity,Sex,Age,Zip,County,State</c> on the seeded
    /// <c>DemographicInformation</c>), so check code expands a group to exactly the members the form definition
    /// lists.
    /// </summary>
    /// <param name="group">A row of <see cref="FormMetadata.Fields"/>.</param>
    /// <returns>
    /// The member rows in list order. Each item is trimmed and resolves among the rows the form's pages show, to
    /// the first in metadata order spelled exactly so, or else, unless the item is ambiguous among the data fields
    /// (<see cref="DataFieldSet.IsAmbiguous"/>), to the first in metadata order equal to it ignoring case. Empty
    /// items, unknown and ambiguous names, system rows, rows on a page outside the form, a row an earlier item already
    /// resolved to and the group itself are dropped. A member that is itself a group is returned as it is, not
    /// expanded, as in legacy. The list is empty when <paramref name="group"/> is not a group.
    /// </returns>
    /// <remarks>
    /// The members are resolved on the first call for a row and kept for the rest of the run, so later calls for the
    /// same row return the same list without reading the form's rows again.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="group"/> is <see langword="null"/>.</exception>
    public IReadOnlyList<FieldMetadata> GetGroupMembers(FieldMetadata group)
    {
        ArgumentNullException.ThrowIfNull(group);

        return MembersOf(group, chargeWork: false);
    }

    /// <summary>
    /// The members of a group, as <see cref="GetGroupMembers"/> returns them, for a statement that expands the group:
    /// the run is charged one step per member, and, the first time a group with a <c>List</c> is expanded, the rows
    /// and pages its resolution reads.
    /// </summary>
    /// <exception cref="WorkLimitExceededException">
    /// The charge takes the run past <see cref="Evaluator.MaxWork"/>.
    /// </exception>
    /// <exception cref="OperationCanceledException">A cancellation token the meter polls was cancelled.</exception>
    internal IReadOnlyList<FieldMetadata> ExpandGroup(FieldMetadata group) => MembersOf(group, chargeWork: true);

    /// <summary>
    /// Charges one step for the statement that starts at <paramref name="line"/>, which becomes the line a run out of
    /// work reports until the next statement starts.
    /// </summary>
    /// <exception cref="WorkLimitExceededException">
    /// The step takes the run past <see cref="Evaluator.MaxWork"/>.
    /// </exception>
    /// <exception cref="OperationCanceledException">A cancellation token the meter polls was cancelled.</exception>
    internal void BeginStatement(int line)
    {
        _statementLine = line;
        ChargeWork(1);
    }

    /// <summary>
    /// Charges <paramref name="steps"/> steps of work to the statement running, before the work is done. The
    /// cancellation tokens are polled once the run has charged another 4,096 steps, so the steps of one long
    /// expression are cancellable, and before a charge that would pass the budget.
    /// </summary>
    /// <param name="steps">The steps, zero or more.</param>
    /// <exception cref="WorkLimitExceededException">
    /// The steps take the run past <see cref="Evaluator.MaxWork"/>; nothing is charged, and the exception carries the
    /// line of the statement running.
    /// </exception>
    /// <exception cref="OperationCanceledException">A cancellation token the meter polls was cancelled.</exception>
    internal void ChargeWork(long steps)
    {
        Debug.Assert(steps >= 0, "A charge is never negative.");

        long work = _work + steps;
        if (work >= _nextCancellationPoll || work > Evaluator.MaxWork)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            _callCancellationToken.ThrowIfCancellationRequested();
            _nextCancellationPoll = work + CancellationPollInterval;
        }

        if (work > Evaluator.MaxWork)
        {
            throw new WorkLimitExceededException(_statementLine);
        }

        _work = work;
    }

    /// <summary>
    /// Makes <paramref name="cancellationToken"/>, an <see cref="Evaluator.Execute"/> call's token, one the work meter
    /// polls, beside the context's own, until <see cref="ExitCall"/> restores the token this returns.
    /// </summary>
    internal CancellationToken EnterCall(CancellationToken cancellationToken)
    {
        CancellationToken previous = _callCancellationToken;
        _callCancellationToken = cancellationToken;
        return previous;
    }

    /// <summary>Restores the call token <see cref="EnterCall"/> replaced, once the call is over.</summary>
    internal void ExitCall(CancellationToken previous) => _callCancellationToken = previous;

    /// <summary>
    /// The field's current value: the last value written in this run, otherwise the request's value bound to it.
    /// </summary>
    /// <param name="field">A row of <see cref="FormMetadata.Fields"/>, matched by reference.</param>
    /// <returns>
    /// The current value, or <see cref="FieldValue.Null"/> when the request held none and the run wrote none, as for
    /// labels, groups, unsupported fields and system rows, to which no request value binds.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="field"/> is <see langword="null"/>.</exception>
    public FieldValue Read(FieldMetadata field)
    {
        ArgumentNullException.ThrowIfNull(field);

        return _values.TryGetValue(field, out FieldValue value) ? value : FieldValue.Null;
    }

    /// <summary>
    /// Resolves a check-code identifier with <see cref="Resolve"/> and returns that field's current value.
    /// </summary>
    /// <param name="identifier">The identifier as written in the program.</param>
    /// <param name="line">The 1-based line of the statement that reads it.</param>
    /// <param name="command">The upper-case keyword of that statement.</param>
    /// <returns>The current value, as <see cref="Read(FieldMetadata)"/> returns it.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="identifier"/> or <paramref name="command"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="CheckCodeRuntimeException">The form has no field of that name.</exception>
    public FieldValue Read(string identifier, int line, string command) => Read(Resolve(identifier, line, command));

    /// <summary>
    /// Sets the field's current value for the rest of the run: an <c>ASSIGN</c> writes its coerced value and a
    /// <c>CLEAR</c> writes <see cref="FieldValue.Null"/>. Nothing is persisted, and no rule is checked.
    /// </summary>
    /// <param name="field">A row of <see cref="FormMetadata.Fields"/>, matched by reference.</param>
    /// <param name="value">The new value.</param>
    /// <exception cref="ArgumentNullException"><paramref name="field"/> is <see langword="null"/>.</exception>
    public void Write(FieldMetadata field, FieldValue value)
    {
        ArgumentNullException.ThrowIfNull(field);

        _values[field] = value;
    }

    // The last steps of both rules, once neither has taken a data field: the first other row spelled exactly so, then,
    // unless the name is ambiguous among the data fields, the first other row equal to it ignoring case. An ambiguous
    // name names nothing, so a row equal to it only ignoring case never stands in for the field it might mean.
    private bool TryResolveOtherRow(string name, [NotNullWhen(true)] out FieldMetadata? field)
    {
        if (name.Length > 0
            && (_otherRowsByExactName.TryGetValue(name, out field)
                || (!_dataFields.IsAmbiguous(name) && _otherRowsByName.TryGetValue(name, out field))))
        {
            return true;
        }

        field = null;
        return false;
    }

    private static CheckCodeRuntimeException UnknownField(string identifier, int line, string command) =>
        new(line, command, $"Unknown field '{identifier.Trim()}'.");

    // The group's members, resolved on first use by FormDefinitionFactory, the rule the form definition's members
    // follows, and kept for the run. Charged, the first resolution of a group with a List pays for what it reads: each
    // row of the form once per page and once more, the way the data fields and the listed fields are found, and the
    // List text; every expansion pays one step per member.
    private IReadOnlyList<FieldMetadata> MembersOf(FieldMetadata group, bool chargeWork)
    {
        if (!_groupMembers.TryGetValue(group, out IReadOnlyList<FieldMetadata>? members))
        {
            if (chargeWork && IsGroup(group) && !string.IsNullOrEmpty(group.List))
            {
                ChargeWork(((long)Form.Fields.Count * (Form.Pages.Count + 1)) + group.List.Length);
            }

            members = FormDefinitionFactory.GetGroupMembers(Form, group);
            _groupMembers.Add(group, members);
        }

        if (chargeWork)
        {
            ChargeWork(members.Count);
        }

        return members;
    }

    /// <summary>
    /// Ends a run whose work would pass <see cref="Evaluator.MaxWork"/>. <c>Evaluator.Run</c> and
    /// <c>Evaluator.Execute</c> catch it once, around the whole run, and report one <c>LIMIT</c> warning at
    /// <see cref="Line"/>; the effects and diagnostics of the statements before stand, and the rest of the run is
    /// skipped.
    /// </summary>
    /// <remarks>
    /// It is neither a <see cref="CheckCodeRuntimeException"/> nor an <see cref="ArgumentOutOfRangeException"/> or
    /// <see cref="OverflowException"/>, the exceptions each statement's error boundary turns into a diagnostic, so no
    /// statement, an <c>IF</c> around the one that ran out included, can take it for its own error and carry on. Its
    /// message is fixed text.
    /// </remarks>
    /// <param name="line">The 1-based line of the statement that ran out of work.</param>
    internal sealed class WorkLimitExceededException(int line)
        : InvalidOperationException("The check-code work budget is spent.")
    {
        /// <summary>The 1-based line of the statement that ran out of work.</summary>
        public int Line { get; } = line;
    }
}
