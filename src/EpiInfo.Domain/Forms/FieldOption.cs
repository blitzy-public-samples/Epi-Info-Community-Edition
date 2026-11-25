using EpiInfo.Domain.Values;

namespace EpiInfo.Domain.Forms;

/// <summary>
/// One selectable item of an <see cref="FieldType.Option"/> or <see cref="FieldType.LegalValues"/> field, listed in the
/// field's <c>options</c> in the form definition.
/// </summary>
/// <remarks>
/// <para>
/// The record carries exactly what the form-definition factory and the metadata reader built and holds no logic of its
/// own: neither the parsing of <c>metaFields.List</c> nor the code-table read lives here. It does not validate its
/// label, because an option item can legitimately be empty after <c>metaFields.List</c> is split.
/// </para>
/// <para>
/// On the wire an option is <c>{"value": ..., "label": ...}</c>, in that order. The API writes <see cref="Value"/> as a
/// plain JSON scalar, a number for an option index and a string for a legal value, so the record declares no
/// serialisation attributes and no public members beyond its two positional properties, each of which is a wire member.
/// </para>
/// </remarks>
/// <param name="Value">
/// The value a record stores when this item is chosen; never <see cref="FieldValue.Null"/>. For an
/// <see cref="FieldType.Option"/> field it is <see cref="FieldValue.FromIndex(int)"/> of the item's 0-based position in
/// the list, the index legacy Epi Info stores in the field's 16-bit integer column. For a
/// <see cref="FieldType.LegalValues"/> field it is <see cref="FieldValue.FromText(string)"/> of the code-table text, the
/// same text as <paramref name="Label"/>.
/// </param>
/// <param name="Label">
/// The text shown for the item. For an <see cref="FieldType.Option"/> field it is the item at that position among the
/// comma-separated items of <c>metaFields.List</c>, taken before any <c>||</c>, as legacy <c>OptionField</c> reads the
/// list; no seeded form has an Option field. For a <see cref="FieldType.LegalValues"/> field it is one row of the code
/// table named by <c>metaFields.SourceTableName</c>, column <c>metaFields.TextColumnName</c> (seeded example:
/// <c>codephvs_administrativesex_hl7_2x</c>); the options keep the order in which SQL Server returns the rows and are
/// sorted by text only when <c>metaFields.Sort</c> is 1.
/// </param>
public sealed record FieldOption(FieldValue Value, string Label);
