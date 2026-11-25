using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using EpiInfo.Domain.Forms;
using EpiInfo.Domain.Metadata;

namespace EpiInfo.Domain.Tests.Metadata;

/// <summary>
/// Unit tests of the field order of a form definition. A page lists its fields by the exact
/// <c>metaFields.TabIndex</c>, a SQL <c>decimal(18,0)</c>, then by <c>FieldId</c>, so <see cref="FieldMetadata"/>
/// and <see cref="FieldDefinition"/> must carry every stored value without loss.
/// </summary>
public sealed class FormDefinitionFactoryTests
{
    private const int PageId = 1;

    // The API's wire settings: camelCase names and strict JSON numbers.
    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.Strict,
    };

    /// <summary>
    /// Adjacent schema-valid tab indexes, lower first, as invariant text because attributes cannot hold decimal
    /// constants: 2^53 and 2^53 + 1, and the largest and the smallest adjacent pair a <c>decimal(18,0)</c> holds.
    /// One IEEE double holds both values of each pair.
    /// </summary>
    [Theory]
    [InlineData("9007199254740992", "9007199254740993")]
    [InlineData("999999999999999998", "999999999999999999")]
    [InlineData("-999999999999999999", "-999999999999999998")]
    public void TabIndex_AdjacentValuesWithReversedFieldIds_StayExactAndOrderAheadOfFieldId(
        string lowerText,
        string higherText)
    {
        decimal lower = decimal.Parse(lowerText, CultureInfo.InvariantCulture);
        decimal higher = decimal.Parse(higherText, CultureInfo.InvariantCulture);
        Assert.Equal((double)lower, (double)higher);

        // Listed in FieldId order, as the metadata query returns them; the lower tab index has the higher FieldId.
        FieldMetadata later = TextRow(fieldId: 1, name: "Later", tabIndex: higher);
        FieldMetadata earlier = TextRow(fieldId: 2, name: "Earlier", tabIndex: lower);

        Assert.Equal(lower, earlier.TabIndex);
        Assert.Equal(higher, later.TabIndex);
        Assert.NotEqual(earlier.TabIndex, later.TabIndex);

        FieldMetadata[] ordered = new[] { later, earlier }
            .OrderBy(row => row.TabIndex ?? 0)
            .ThenBy(row => row.FieldId)
            .ToArray();

        Assert.Equal(["Earlier", "Later"], ordered.Select(row => row.Name));
        Assert.Equal([2, 1], ordered.Select(row => row.FieldId));

        FieldDefinition earlierDefinition = Definition(earlier);
        FieldDefinition laterDefinition = Definition(later);

        Assert.Equal(lower, earlierDefinition.TabIndex);
        Assert.Equal(higher, laterDefinition.TabIndex);
        Assert.Contains(
            $"\"tabIndex\":{lowerText},",
            JsonSerializer.Serialize(earlierDefinition, WireOptions),
            StringComparison.Ordinal);
        Assert.Contains(
            $"\"tabIndex\":{higherText},",
            JsonSerializer.Serialize(laterDefinition, WireOptions),
            StringComparison.Ordinal);
    }

    private static FieldMetadata TextRow(int fieldId, string name, decimal tabIndex) => new()
    {
        FieldId = fieldId,
        Name = name,
        FieldTypeId = 1,
        FieldTypeName = "Text",
        Prompt = name,
        TabIndex = tabIndex,
        PageId = PageId,
    };

    private static FieldDefinition Definition(FieldMetadata row) => new(
        row.Name,
        FieldType.Text,
        row.Prompt,
        row.TabIndex ?? 0,
        row.PageId ?? PageId,
        Unsupported: false,
        UnsupportedType: null,
        FieldValidation.None,
        Options: null,
        Members: null);
}
