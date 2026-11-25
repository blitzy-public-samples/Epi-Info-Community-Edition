using EpiInfo.Domain.Values;

namespace EpiInfo.Domain.Records;

/// <summary>
/// One criterion of a record search, as received: an item of the <c>criteria</c> array in the body of
/// <c>POST /api/forms/{viewId}/records/search</c>, for example <c>{"field": "LastName", "value": "Sm*"}</c>.
/// </summary>
/// <remarks>
/// <para>
/// This type only carries what the caller sent. It does not trim, split, validate or normalise anything. The legacy
/// Find semantics are applied by <c>SearchCriteriaParser</c> alone: trimming, the <c>" or "</c> alternatives, the
/// <c>&lt;=</c>, <c>&gt;=</c>, <c>&lt;&gt;</c>, <c>&gt;</c> and <c>&lt;</c> prefixes, wildcards, the <c>Yes</c>/<c>No</c>
/// checkbox strings, the six-field limit and the rejection of a field named twice.
/// </para>
/// <para>
/// <see cref="Value"/> is field data, which the redaction policy keeps out of logs and error bodies. Never pass a
/// criterion or its <see cref="Value"/> to a logger or an error message. <see cref="ToString"/> returns the type name
/// only, so a criterion formatted by mistake carries no field data.
/// </para>
/// </remarks>
/// <param name="Field">
/// The field name exactly as the caller supplied it. The parser resolves it case-insensitively to the canonical
/// <c>metaFields.Name</c> of a data field of the form, or to the system fields <c>GlobalRecordId</c> and
/// <c>RecStatus</c>, and rejects every other name. The wire reader can leave it <see langword="null"/> when the caller
/// omits <c>field</c>, so the parser treats a null name as an unknown one rather than trusting the annotation.
/// </param>
/// <param name="Value">
/// The JSON scalar the caller sent, as the API's field-value wire converter read it. Its kind is
/// <see cref="FieldValueKind.Null"/> (JSON <c>null</c>, the blank criterion), <see cref="FieldValueKind.Text"/>,
/// <see cref="FieldValueKind.Number"/> or <see cref="FieldValueKind.Boolean"/>. The API's JSON settings reject a
/// criterion that omits <c>value</c> before the parser runs, so a null value always means the caller sent JSON
/// <c>null</c>. A JSON boolean is meaningful only for a checkbox criterion, a target extension beside the legacy
/// <c>Yes</c>, <c>No</c>, <c>(+)</c> and <c>(-)</c> strings. The kinds <see cref="FieldValueKind.Date"/> and
/// <see cref="FieldValueKind.Index"/> never arrive here: the parser produces them from the text or number it reads.
/// </param>
public sealed record SearchCriterion(string Field, FieldValue Value)
{
    /// <summary>
    /// Returns the type name only. The compiler-generated record <c>ToString</c> would print <see cref="Value"/>,
    /// which is field data that must never reach a log line or an error body.
    /// </summary>
    /// <returns><c>SearchCriterion</c>.</returns>
    public override string ToString() => nameof(SearchCriterion);
}
