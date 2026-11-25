using System.Globalization;
using EpiInfo.Domain.Values;

namespace EpiInfo.Domain.Validation;

/// <summary>
/// The fixed texts the server-side record validator returns, as plain constants rather than localised
/// resources. Field-level messages become <c>errors[&lt;canonical field name&gt;]</c> entries of a 400; the
/// request-level ones become the <c>detail</c> of a 400 that carries no <c>errors</c> map.
/// </summary>
/// <remarks>
/// Error bodies must never echo what the caller submitted, so every placeholder is filled from form
/// metadata only (column length, <c>MaxLength</c>, range bounds, pattern, option count), never from a
/// submitted value.
/// </remarks>
public static class ValidationMessages
{
    /// <summary>
    /// Required gate: the legacy <c>FIELD_IS_REQUIRED_LONG</c> wording, copied verbatim from
    /// <c>Epi.Core/SharedStrings.resx</c> line 638. <c>{0}</c> is the field prompt and <c>{1}</c> the page
    /// name, as legacy formats it. Do not edit this text, including its trailing space: tests pin it
    /// byte for byte against the legacy resource.
    /// </summary>
    public const string FieldIsRequiredLong =
        "{0} is a required field (page {1}). Please fill in this field before attempting to leave the record, save data, or close the form. ";

    /// <summary>Request-level body check: <c>values</c> is missing, <c>null</c> or not a JSON object.</summary>
    public const string ValuesMustBeObject = "values must be an object";

    /// <summary>Empty-record rule: every value of a create is <c>null</c> after normalisation.</summary>
    public const string RecordHasNoValues = "the record has no values";

    /// <summary>Unknown-field check: the key names no data field of the form.</summary>
    public const string UnknownField = "This is not a data field of the form.";

    /// <summary>
    /// Unknown-field check: the message under <see cref="RecordValidator.ValuesKey"/> for a submitted name the form
    /// has no <c>metaFields</c> row for. The text is fixed and names nothing that was submitted.
    /// </summary>
    public const string UnknownValuesName = "values holds a name that is not a field of the form.";

    /// <summary>
    /// Unknown-field check: the message under <see cref="RecordValidator.ValuesKey"/> for a submitted name that spells
    /// no <c>metaFields</c> row exactly, data field or not, and equals two or more distinct data-field names when
    /// letter case is ignored, as <c>age</c> equals both <c>Age</c> and <c>AGE</c>. The text is fixed and names
    /// nothing that was submitted.
    /// </summary>
    public const string AmbiguousValuesName =
        "values holds a name that matches more than one field when letter case is ignored.";

    /// <summary>Unknown-field check: two keys name the same field when letter case is ignored.</summary>
    public const string DuplicateField =
        "This field is given more than once; field names are matched without regard to letter case.";

    /// <summary>Read-only check: a field whose metadata marks it read-only is present in <c>values</c>.</summary>
    public const string ReadOnlyField = "This field is read-only and cannot be written.";

    /// <summary>Type check: a text, multiline or legal-values field received a non-string value.</summary>
    public const string TextExpected = "A text value is expected.";

    /// <summary>Type check: a number field received something other than a finite JSON number.</summary>
    public const string NumberExpected = "A finite number is expected.";

    /// <summary>Type check: a date field received something other than a valid <c>YYYY-MM-DD</c> string.</summary>
    public const string DateExpected = "A date in the form YYYY-MM-DD is expected.";

    /// <summary>Type check: a checkbox field received something other than a JSON boolean.</summary>
    public const string BooleanExpected = "true or false is expected.";

    /// <summary>Type check: an option field received something other than a whole-number index.</summary>
    public const string OptionIndexExpected = "A whole-number option index is expected.";

    /// <summary>Type check: an option index outside the option list; <c>{0}</c> is the option count minus 1.</summary>
    public const string OptionIndexOutOfRange = "The option index must be from 0 to {0}.";

    /// <summary>Type check: a value was given for an option field whose metadata lists no options.</summary>
    public const string NoOptionsAvailable = "This field has no options to choose from.";

    /// <summary>Storage-range check: text longer than the column holds; <c>{0}</c> is its character length.</summary>
    public const string TextTooLongForColumn = "The value is longer than the {0} characters this field can store.";

    /// <summary>Storage-range check: a number that is fractional or outside the range of an integer column.</summary>
    public const string NumberNotStorable = "The number is not a whole number within the range this field can store.";

    /// <summary>Storage-range check: a date outside the range of a <c>datetime</c> column.</summary>
    public const string DateNotStorable = "The date must be between 1753-01-01 and 9999-12-31.";

    /// <summary>Storage-range check: a value its admitted column type cannot represent exactly, for any other reason.</summary>
    public const string ValueNotStorable = "The value cannot be stored in this field.";

    /// <summary>Maximum-length check: text longer than metadata <c>MaxLength</c>, given as <c>{0}</c>.</summary>
    public const string MaxLengthExceeded = "The value must be at most {0} characters long.";

    /// <summary>Range check with both bounds set; <c>{0}</c> and <c>{1}</c> are the inclusive bounds.</summary>
    public const string RangeBetween = "The value must be between {0} and {1}.";

    /// <summary>Range check with only a lower bound set; <c>{0}</c> is the inclusive bound.</summary>
    public const string RangeAtLeast = "The value must be at least {0}.";

    /// <summary>Range check with only an upper bound set; <c>{0}</c> is the inclusive bound.</summary>
    public const string RangeAtMost = "The value must be at most {0}.";

    /// <summary>Pattern check: text that does not fit its metadata pattern, given as <c>{0}</c>.</summary>
    public const string PatternMismatch = "The value must match the pattern {0}.";

    /// <summary>Legal-values check: text that equals none of the field's options.</summary>
    public const string NotALegalValue = "The value is not one of the field's legal values.";

    /// <summary>
    /// Formats <see cref="FieldIsRequiredLong"/> for one missing field. The prompt is used exactly as stored,
    /// line breaks included; a field without a prompt is named by its field name instead.
    /// </summary>
    /// <param name="prompt">The field's <c>metaFields.PromptText</c>, or <c>null</c> when it has none.</param>
    /// <param name="fieldName">The canonical <c>metaFields.Name</c>, used when the prompt is blank.</param>
    /// <param name="pageName">The name of the page that holds the field.</param>
    /// <exception cref="ArgumentNullException"><paramref name="fieldName"/> or <paramref name="pageName"/> is <c>null</c>.</exception>
    public static string FormatFieldIsRequired(string? prompt, string fieldName, string pageName)
    {
        ArgumentNullException.ThrowIfNull(fieldName);
        ArgumentNullException.ThrowIfNull(pageName);

        return string.Format(
            CultureInfo.InvariantCulture,
            FieldIsRequiredLong,
            string.IsNullOrWhiteSpace(prompt) ? fieldName : prompt,
            pageName);
    }

    /// <summary>Formats <see cref="OptionIndexOutOfRange"/> from the number of options the field offers.</summary>
    /// <param name="optionCount">The option count, at least 1; a field with none uses <see cref="NoOptionsAvailable"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="optionCount"/> is less than 1.</exception>
    public static string FormatOptionIndexOutOfRange(int optionCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(optionCount, 1);
        return string.Format(CultureInfo.InvariantCulture, OptionIndexOutOfRange, optionCount - 1);
    }

    /// <summary>Formats <see cref="TextTooLongForColumn"/> from the column's length in characters.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="characterLength"/> is not positive.</exception>
    public static string FormatTextTooLongForColumn(int characterLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(characterLength);
        return string.Format(CultureInfo.InvariantCulture, TextTooLongForColumn, characterLength);
    }

    /// <summary>Formats <see cref="MaxLengthExceeded"/> from the metadata <c>MaxLength</c>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxLength"/> is not positive.</exception>
    public static string FormatMaxLengthExceeded(int maxLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLength);
        return string.Format(CultureInfo.InvariantCulture, MaxLengthExceeded, maxLength);
    }

    /// <summary>Formats <see cref="RangeBetween"/> for a number field's inclusive bounds.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A bound is not a finite number.</exception>
    public static string FormatRangeBetween(double lower, double upper) =>
        string.Format(
            CultureInfo.InvariantCulture,
            RangeBetween,
            FormatBound(lower, nameof(lower)),
            FormatBound(upper, nameof(upper)));

    /// <summary>
    /// Formats <see cref="RangeBetween"/> for a number field's inclusive bounds when both are exact integers, written
    /// with every digit, so a <c>bigint</c> bound such as <c>9007199254740993</c> is not rounded as a double would be.
    /// </summary>
    public static string FormatRangeBetween(long lower, long upper) =>
        string.Format(CultureInfo.InvariantCulture, RangeBetween, FormatBound(lower), FormatBound(upper));

    /// <summary>
    /// Formats <see cref="RangeBetween"/> for a number field's inclusive bounds when only the lower one is an exact
    /// integer: the lower bound keeps every digit and the upper one takes its shortest round-trip form.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="upper"/> is not a finite number.</exception>
    public static string FormatRangeBetween(long lower, double upper) =>
        string.Format(
            CultureInfo.InvariantCulture,
            RangeBetween,
            FormatBound(lower),
            FormatBound(upper, nameof(upper)));

    /// <summary>
    /// Formats <see cref="RangeBetween"/> for a number field's inclusive bounds when only the upper one is an exact
    /// integer: the lower bound takes its shortest round-trip form and the upper one keeps every digit.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lower"/> is not a finite number.</exception>
    public static string FormatRangeBetween(double lower, long upper) =>
        string.Format(
            CultureInfo.InvariantCulture,
            RangeBetween,
            FormatBound(lower, nameof(lower)),
            FormatBound(upper));

    /// <summary>Formats <see cref="RangeBetween"/> for a date field's inclusive bounds, as <c>yyyy-MM-dd</c>.</summary>
    public static string FormatRangeBetween(DateOnly lower, DateOnly upper) =>
        string.Format(CultureInfo.InvariantCulture, RangeBetween, FormatBound(lower), FormatBound(upper));

    /// <summary>Formats <see cref="RangeAtLeast"/> for a number field's inclusive lower bound.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lower"/> is not a finite number.</exception>
    public static string FormatRangeAtLeast(double lower) =>
        string.Format(CultureInfo.InvariantCulture, RangeAtLeast, FormatBound(lower, nameof(lower)));

    /// <summary>
    /// Formats <see cref="RangeAtLeast"/> for a number field's inclusive lower bound when it is an exact integer,
    /// written with every digit, such as <c>9007199254740993</c>.
    /// </summary>
    public static string FormatRangeAtLeast(long lower) =>
        string.Format(CultureInfo.InvariantCulture, RangeAtLeast, FormatBound(lower));

    /// <summary>Formats <see cref="RangeAtLeast"/> for a date field's inclusive lower bound, as <c>yyyy-MM-dd</c>.</summary>
    public static string FormatRangeAtLeast(DateOnly lower) =>
        string.Format(CultureInfo.InvariantCulture, RangeAtLeast, FormatBound(lower));

    /// <summary>Formats <see cref="RangeAtMost"/> for a number field's inclusive upper bound.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="upper"/> is not a finite number.</exception>
    public static string FormatRangeAtMost(double upper) =>
        string.Format(CultureInfo.InvariantCulture, RangeAtMost, FormatBound(upper, nameof(upper)));

    /// <summary>
    /// Formats <see cref="RangeAtMost"/> for a number field's inclusive upper bound when it is an exact integer,
    /// written with every digit, such as <c>9007199254740993</c>.
    /// </summary>
    public static string FormatRangeAtMost(long upper) =>
        string.Format(CultureInfo.InvariantCulture, RangeAtMost, FormatBound(upper));

    /// <summary>Formats <see cref="RangeAtMost"/> for a date field's inclusive upper bound, as <c>yyyy-MM-dd</c>.</summary>
    public static string FormatRangeAtMost(DateOnly upper) =>
        string.Format(CultureInfo.InvariantCulture, RangeAtMost, FormatBound(upper));

    /// <summary>Formats <see cref="PatternMismatch"/> with the field's metadata pattern, such as <c>###-###-####</c>.</summary>
    /// <exception cref="ArgumentException"><paramref name="pattern"/> is <c>null</c> or empty.</exception>
    public static string FormatPatternMismatch(string pattern)
    {
        ArgumentException.ThrowIfNullOrEmpty(pattern);
        return string.Format(CultureInfo.InvariantCulture, PatternMismatch, pattern);
    }

    // The shortest round-trip invariant form, so the bounds read as the metadata states them (0, 120, 37.5).
    private static string FormatBound(double bound, string paramName)
    {
        if (!double.IsFinite(bound))
        {
            throw new ArgumentOutOfRangeException(paramName, bound, "A range bound must be a finite number.");
        }

        return bound.ToString("R", CultureInfo.InvariantCulture);
    }

    // Every digit of the exact integer, invariant, so a bound beyond 2^53 reads as the metadata states it.
    private static string FormatBound(long bound) => bound.ToString(CultureInfo.InvariantCulture);

    private static string FormatBound(DateOnly bound) => FieldValue.FormatIsoDate(bound);
}
