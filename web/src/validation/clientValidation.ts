// Feedback-only client copy of the server's record validation: the phase-1 input checks and the
// required gate of src/EpiInfo.Domain/Validation/RecordValidator.cs, with the pattern rule of
// MaskPattern.cs. The server stays authoritative. Every check here is one the server makes, or a
// weaker form of it, so the client never blocks a save the server would accept; what the client
// cannot decide (column lengths, integer-column storage, the range of a bigint or exact decimal
// value or against a whole bound beyond the safe range, the number mask of an exact decimal,
// unknown, repeated or ambiguous names) is left to the server's 400 `errors` map. No message ever contains a submitted
// value, only metadata.
import { isExactDecimal } from '../api/client';
import type { FieldDefinition, FieldType, FieldValue, FormDefinition, PageDefinition, Values } from '../api/types';

// A submitted value that is not empty.
type PresentValue = NonNullable<FieldValue>;

/**
 * Messages keyed by canonical field name; only fields with at least one message appear. The maps
 * this module returns have no prototype, so every field name, `__proto__` included, is an own entry.
 */
export type FieldErrors = Record<string, string[]>;

// Null prototype: a field named like an Object member, such as `__proto__` or `constructor`,
// becomes an own entry, never a prototype change or an inherited member.
function emptyFieldErrors(): FieldErrors {
  return Object.create(null) as FieldErrors;
}

/**
 * The legacy `FIELD_IS_REQUIRED_LONG` text (Epi.Core/SharedStrings.resx), byte for byte, trailing
 * space included, as the server's `ValidationMessages.FieldIsRequiredLong` holds it. `{0}` is the
 * field prompt and `{1}` the page name.
 */
export const FIELD_IS_REQUIRED_LONG =
  '{0} is a required field (page {1}). Please fill in this field before attempting to leave the record, save data, or close the form. ';

// The placeholders of FIELD_IS_REQUIRED_LONG, filled in one pass: a prompt that itself holds "{1}"
// or "$&" is inserted literally, as .NET string.Format inserts it.
const REQUIRED_PLACEHOLDERS = /\{([01])\}/g;

/**
 * Formats {@link FIELD_IS_REQUIRED_LONG} for one missing field with its prompt and page name, as
 * legacy and the server's `FormatFieldIsRequired` do. The prompt is inserted exactly as given, an
 * empty or whitespace-only prompt included; a missing prompt (`null` or `undefined`) is inserted as
 * empty text.
 */
export function formatRequiredMessage(prompt: string | null | undefined, pageName: string): string {
  const subject = prompt ?? '';
  return FIELD_IS_REQUIRED_LONG.replace(REQUIRED_PLACEHOLDERS, (_placeholder: string, index: string) =>
    index === '0' ? subject : pageName,
  );
}

/** The client's own feedback texts. Builders take metadata only, never a submitted value. */
export const clientMessages = Object.freeze({
  textExpected: 'Enter text.',
  numberExpected: 'Enter a number.',
  dateExpected: 'Enter a valid date (YYYY-MM-DD).',
  dateNotStorable: 'Enter a date between 1753-01-01 and 9999-12-31.',
  booleanExpected: 'Check or clear this box.',
  optionExpected: 'Choose one of the listed options.',
  noOptions: 'No options are available for this field.',
  maxLength: (maxLength: number): string => `Enter at most ${String(maxLength)} characters.`,
  rangeBetween: (lower: string, upper: string): string => `Enter a value from ${lower} to ${upper}.`,
  rangeAtLeast: (lower: string): string => `Enter a value of at least ${lower}.`,
  rangeAtMost: (upper: string): string => `Enter a value of at most ${upper}.`,
  patternMismatch: (pattern: string): string => `Use the format ${pattern}.`,
  notLegalValue: 'Choose one of the allowed values.',
});

/** Whether values of the type are text: `text`, `multiline` and `legalValues`. */
export function isTextFamily(fieldType: FieldType): boolean {
  return fieldType === 'text' || fieldType === 'multiline' || fieldType === 'legalValues';
}

/** Whether the field has a page-table column that `values` carries: not a label, group or unsupported field. */
export function isDataField(field: FieldDefinition): boolean {
  if (field.unsupported === true) {
    return false;
  }

  switch (field.fieldType) {
    case 'label':
    case 'group':
    case 'unsupported':
      return false;
    default:
      return true;
  }
}

/** Whether the client may send the field: a data field that is not read-only. */
export function isWritableDataField(field: FieldDefinition): boolean {
  return isDataField(field) && !field.validation.readOnly;
}

/**
 * The value as the server normalises it: `undefined` becomes `null`, and empty text on a text-family
 * field becomes `null`, as legacy writes NULL for an empty string. Nothing is trimmed and nothing is
 * coerced; a value of the wrong type is reported by {@link validateField}.
 */
export function normalizeValue(field: FieldDefinition, raw: FieldValue | undefined): FieldValue {
  if (raw === undefined || raw === null) {
    return null;
  }

  return raw === '' && isTextFamily(field.fieldType) ? null : raw;
}

// Exactly YYYY-MM-DD with ASCII digits; without the m flag, `$` matches only at the end of the text.
const ISO_DATE = /^(\d{4})-(\d{2})-(\d{2})$/;

// The range of a datetime column, the only column a date field is admitted on.
const MIN_STORABLE_DATE = '1753-01-01';
const MAX_STORABLE_DATE = '9999-12-31';

// Integer arithmetic only: Date.parse and new Date(y, …) shift years 0-99 and roll overflowing days
// into the next month. Year 0000 is rejected, as the server's DateOnly parse rejects it.
function isCalendarDate(text: string): boolean {
  const match = ISO_DATE.exec(text);
  if (match === null) {
    return false;
  }

  const year = Number(match[1]);
  const month = Number(match[2]);
  const day = Number(match[3]);
  if (year < 1 || month < 1 || month > 12 || day < 1) {
    return false;
  }

  const leapYear = year % 4 === 0 && (year % 100 !== 0 || year % 400 === 0);
  const daysInMonth = month === 2 ? (leapYear ? 29 : 28) : [4, 6, 9, 11].includes(month) ? 30 : 31;
  return day <= daysInMonth;
}

function isIsoDateBound(bound: FieldValue | undefined): bound is string {
  return typeof bound === 'string' && ISO_DATE.test(bound);
}

// A number bound the client checks: a finite number that is not a whole number beyond the safe range.
// Such a bound may be the nearest double of a different metadata bound, which a message would then
// misstate; see checkRange.
function isNumberBound(bound: FieldValue | undefined): bound is number {
  return (
    typeof bound === 'number' && Number.isFinite(bound) && (!Number.isInteger(bound) || Number.isSafeInteger(bound))
  );
}

/**
 * The phase-1 checks on one submitted, normalised value. `null` (emptiness belongs to the required
 * gate) and fields that are not data fields give `[]`. A value of the wrong type gives only the type
 * message; otherwise every failing check among maximum length, range, pattern and legal values adds
 * its message, in that order.
 */
export function validateField(field: FieldDefinition, value: FieldValue | undefined): string[] {
  if (value === undefined || value === null || !isDataField(field)) {
    return [];
  }

  const typeError = checkType(field, value);
  if (typeError !== null) {
    return [typeError];
  }

  const messages: string[] = [];
  const { maxLength, pattern } = field.validation;

  if (
    isTextFamily(field.fieldType) &&
    typeof value === 'string' &&
    typeof maxLength === 'number' &&
    maxLength > 0 &&
    value.length > maxLength
  ) {
    messages.push(clientMessages.maxLength(maxLength));
  }

  const rangeError = checkRange(field, value);
  if (rangeError !== null) {
    messages.push(rangeError);
  }

  if (typeof pattern === 'string' && !matchesPattern(field, value)) {
    messages.push(clientMessages.patternMismatch(pattern));
  }

  if (field.fieldType === 'legalValues' && typeof value === 'string') {
    const legalValues = legalValueTexts(field);
    if (legalValues.length > 0 && !legalValues.includes(value)) {
      messages.push(clientMessages.notLegalValue);
    }
  }

  return messages;
}

// The wire type of each field type, then the datetime storage range for dates. A bigint is a
// number: an exact integer beyond the safe range; so is an exact decimal, a typed fraction that
// is written as the JSON number typed.
function checkType(field: FieldDefinition, value: PresentValue): string | null {
  switch (field.fieldType) {
    case 'text':
    case 'multiline':
    case 'legalValues':
      return typeof value === 'string' ? null : clientMessages.textExpected;
    case 'number':
      return typeof value === 'bigint' ||
        isExactDecimal(value) ||
        (typeof value === 'number' && Number.isFinite(value))
        ? null
        : clientMessages.numberExpected;
    case 'date':
      if (typeof value !== 'string' || !isCalendarDate(value)) {
        return clientMessages.dateExpected;
      }
      return value < MIN_STORABLE_DATE || value > MAX_STORABLE_DATE ? clientMessages.dateNotStorable : null;
    case 'checkbox':
      return typeof value === 'boolean' ? null : clientMessages.booleanExpected;
    case 'option':
      return checkOptionIndex(field, value);
    default:
      return null;
  }
}

// In the server's order: a value that is not a whole number is the wrong type whatever the options;
// only then does an empty option list, and then the index bounds, decide. A bigint lies beyond
// every index, and an exact decimal is not a whole number.
function checkOptionIndex(field: FieldDefinition, value: PresentValue): string | null {
  if (typeof value !== 'number' || !Number.isInteger(value)) {
    return clientMessages.optionExpected;
  }

  const optionCount = field.options?.length ?? 0;
  if (optionCount === 0) {
    return clientMessages.noOptions;
  }

  return value >= 0 && value < optionCount ? null : clientMessages.optionExpected;
}

// Inclusive bounds of the field's own kind: numbers on a number field, ISO dates (compared as text,
// which orders them by date) on a date field. A bound of any other kind is ignored, as on the server.
//
// The server compares a number by what its column stores: with exact integer limits read from the
// metadata text on an integer column, and as nearest doubles on a `float` column. The client knows
// neither the column nor that text; a bound arrives as its nearest double, so 18014398509481985.25
// arrives as 18014398509481984. So only a number value is checked, and only against number bounds:
// rounding to a double is monotonic, so there the client never rejects a value the server accepts on
// either column. A bigint or exact decimal value, and a whole bound beyond the safe range, are left to
// the server's 400, and a message names only the bounds checked here.
function checkRange(field: FieldDefinition, value: PresentValue): string | null {
  const { lower, upper } = field.validation;

  if (field.fieldType === 'number' && typeof value === 'number') {
    const lowerBound = isNumberBound(lower) ? lower : null;
    const upperBound = isNumberBound(upper) ? upper : null;
    const outOfRange = (lowerBound !== null && value < lowerBound) || (upperBound !== null && value > upperBound);
    return outOfRange
      ? describeRange(
          lowerBound === null ? null : String(lowerBound),
          upperBound === null ? null : String(upperBound),
        )
      : null;
  }

  if (field.fieldType === 'date' && typeof value === 'string') {
    const lowerBound = isIsoDateBound(lower) ? lower : null;
    const upperBound = isIsoDateBound(upper) ? upper : null;
    const outOfRange = (lowerBound !== null && value < lowerBound) || (upperBound !== null && value > upperBound);
    return outOfRange ? describeRange(lowerBound, upperBound) : null;
  }

  return null;
}

function describeRange(lower: string | null, upper: string | null): string | null {
  if (lower !== null && upper !== null) {
    return clientMessages.rangeBetween(lower, upper);
  }

  if (lower !== null) {
    return clientMessages.rangeAtLeast(lower);
  }

  return upper !== null ? clientMessages.rangeAtMost(upper) : null;
}

// The texts a legal-values field accepts, compared case-sensitively; options that are not text are
// ignored, as the server ignores them.
function legalValueTexts(field: FieldDefinition): string[] {
  const texts: string[] = [];
  for (const option of field.options ?? []) {
    if (typeof option.value === 'string') {
      texts.push(option.value);
    }
  }

  return texts;
}

// How the server's MaskPattern.Classify treats a field's pattern, reduced to the kinds that check a
// value; every other pattern, date patterns included, checks nothing.
type PatternRule =
  | { readonly kind: 'none' }
  | { readonly kind: 'textDigitMask'; readonly mask: string }
  | { readonly kind: 'numberMask'; readonly maxIntegerDigits: number; readonly maxDecimalDigits: number };

const NO_PATTERN_CHECK: PatternRule = { kind: 'none' };

// One or more '#', optionally a point and one or more '#', over the whole pattern.
const NUMBER_MASK = /^(#+)(?:\.(#+))?$/;

// A letter in the sense of .NET char.IsLetter (Unicode L*).
const LETTER = /\p{L}/u;

// Tested per UTF-16 code unit, as .NET tests each char: a surrogate half of an astral letter is no
// letter there, so it is none here either.
function hasLetter(pattern: string): boolean {
  for (let i = 0; i < pattern.length; i++) {
    if (LETTER.test(pattern.charAt(i))) {
      return true;
    }
  }

  return false;
}

function interpretPattern(pattern: string | null | undefined, fieldType: FieldType): PatternRule {
  if (typeof pattern !== 'string' || pattern.trim().length === 0) {
    return NO_PATTERN_CHECK;
  }

  // The server checks a text mask only when it holds a '#' and no letter, every other position, a
  // digit such as the `2` of `##-2` included, being a literal; a pattern such as `A##` is unchecked
  // there, so it must be unchecked here too.
  if (isTextFamily(fieldType)) {
    return pattern.includes('#') && !hasLetter(pattern)
      ? { kind: 'textDigitMask', mask: pattern }
      : NO_PATTERN_CHECK;
  }

  if (fieldType === 'number') {
    const match = NUMBER_MASK.exec(pattern);
    if (match !== null) {
      const integerPositions = match[1];
      // The decimal group is absent from the match when the mask has no point.
      const decimalPositions: string | undefined = match[2];
      return {
        kind: 'numberMask',
        maxIntegerDigits: integerPositions.length,
        maxDecimalDigits: decimalPositions === undefined ? 0 : decimalPositions.length,
      };
    }
  }

  return NO_PATTERN_CHECK;
}

/**
 * Whether a submitted value satisfies the field's pattern under the server's target rule (it is not
 * legacy parity, where the pattern was only an entry mask):
 * - a text-family `#` mask such as `###-###-####` needs exactly the mask's length, an ASCII digit at
 *   each `#` and the identical character everywhere else;
 * - a number mask such as `###` or `###.#` bounds the integer and decimal digits of the number's
 *   plain positional form, after one leading sign; an exact decimal is left to the server;
 * - a blank pattern, a date pattern and any other pattern check nothing.
 * A `null` value, or one not of the kind the mask checks, passes: type errors are reported apart.
 */
export function matchesPattern(field: FieldDefinition, value: FieldValue | undefined): boolean {
  if (value === undefined || value === null) {
    return true;
  }

  const rule = interpretPattern(field.validation.pattern, field.fieldType);
  switch (rule.kind) {
    case 'textDigitMask':
      return typeof value !== 'string' || matchesTextMask(rule.mask, value);
    case 'numberMask':
      // A bigint's decimal digits are already its plain positional form. A `float` column's double
      // of it has at least as many integer digits, so this is never stricter than the server.
      if (typeof value === 'bigint') {
        return fitsDigitBudget(value.toString(), rule.maxIntegerDigits, rule.maxDecimalDigits);
      }
      // An exact decimal is left to the server: an integer column rejects it as not whole, and a
      // `float` column counts the digits of its nearest double, which the typed digits do not give.
      if (isExactDecimal(value) || typeof value !== 'number') {
        return true;
      }
      return (
        Number.isFinite(value) &&
        fitsDigitBudget(formatPlain(value), rule.maxIntegerDigits, rule.maxDecimalDigits)
      );
    case 'none':
      return true;
  }
}

const DIGIT_ZERO = 48;
const DIGIT_NINE = 57;

// Compared per UTF-16 code unit, as .NET compares chars; nothing is trimmed.
function matchesTextMask(mask: string, text: string): boolean {
  if (text.length !== mask.length) {
    return false;
  }

  for (let i = 0; i < mask.length; i++) {
    if (mask[i] === '#') {
      const code = text.charCodeAt(i);
      if (code < DIGIT_ZERO || code > DIGIT_NINE) {
        return false;
      }
    } else if (text[i] !== mask[i]) {
      return false;
    }
  }

  return true;
}

function fitsDigitBudget(plain: string, maxIntegerDigits: number, maxDecimalDigits: number): boolean {
  const unsigned = plain.startsWith('-') || plain.startsWith('+') ? plain.slice(1) : plain;
  const point = unsigned.indexOf('.');
  const integerDigits = point < 0 ? unsigned.length : point;
  const decimalDigits = point < 0 ? 0 : unsigned.length - point - 1;
  return integerDigits <= maxIntegerDigits && decimalDigits <= maxDecimalDigits;
}

// The server's MaskPattern.FormatPlain: the shortest round-trip digits in positional notation, never
// an exponent, with -0 as "0". String(n) gives the same shortest digits as .NET's "R" format but
// switches to an exponent for |n| >= 1e21 and 0 < |n| < 1e-6; those are expanded as text, moving the
// point over the mantissa digits so no digit is added or lost. The result is field data, so it is
// only ever counted, never shown.
function formatPlain(n: number): string {
  if (n === 0) {
    return '0';
  }

  const roundTrip = String(n);
  const exponentAt = roundTrip.indexOf('e');
  return exponentAt < 0 ? roundTrip : expandExponent(roundTrip, exponentAt);
}

function expandExponent(roundTrip: string, exponentAt: number): string {
  const exponent = Number.parseInt(roundTrip.slice(exponentAt + 1), 10);
  const signed = roundTrip.slice(0, exponentAt);
  const negative = signed.startsWith('-');
  const mantissa = negative ? signed.slice(1) : signed;
  const point = mantissa.indexOf('.');
  const digits = point < 0 ? mantissa : mantissa.slice(0, point) + mantissa.slice(point + 1);
  const integerDigitCount = (point < 0 ? mantissa.length : point) + exponent;
  const sign = negative ? '-' : '';

  if (integerDigitCount <= 0) {
    return `${sign}0.${'0'.repeat(-integerDigitCount)}${digits}`;
  }

  if (integerDigitCount >= digits.length) {
    return sign + digits + '0'.repeat(integerDigitCount - digits.length);
  }

  return `${sign}${digits.slice(0, integerDigitCount)}.${digits.slice(integerDigitCount)}`;
}

// Resolves a submitted name to a data field: its exact spelling first, then ignoring letter case
// when exactly one data field matches. An ambiguous or unknown name resolves to nothing and is left
// to the server, which rejects it.
function createDataFieldLookup(pages: readonly PageDefinition[]): (name: string) => FieldDefinition | null {
  const exact = new Map<string, FieldDefinition>();
  const folded = new Map<string, FieldDefinition[]>();

  for (const page of pages) {
    for (const field of page.fields) {
      if (!isDataField(field) || exact.has(field.name)) {
        continue;
      }

      exact.set(field.name, field);
      const key = field.name.toLowerCase();
      const sameKey = folded.get(key);
      if (sameKey === undefined) {
        folded.set(key, [field]);
      } else {
        sameKey.push(field);
      }
    }
  }

  return (name: string): FieldDefinition | null => {
    const exactField = exact.get(name);
    if (exactField !== undefined) {
      return exactField;
    }

    const candidates = folded.get(name.toLowerCase());
    return candidates !== undefined && candidates.length === 1 ? candidates[0] : null;
  };
}

/**
 * Runs {@link validateField} on each submitted value of a create or update, keyed by the canonical
 * field name; names are matched without regard to letter case, as on the server. Unknown names,
 * read-only and non-data fields, and a repeat of a field already given are skipped: the editor never
 * sends them, and the server rejects them itself.
 */
export function validateValues(definition: FormDefinition, values: Values): FieldErrors {
  const findField = createDataFieldLookup(definition.pages);
  const seen = new Set<string>();
  const errors = emptyFieldErrors();

  for (const [name, raw] of Object.entries(values)) {
    const field = findField(name);
    if (field === null || field.validation.readOnly) {
      continue;
    }

    const seenKey = field.name.toLowerCase();
    if (seen.has(seenKey)) {
      continue;
    }

    seen.add(seenKey);
    const messages = validateField(field, normalizeValue(field, raw));
    if (messages.length > 0) {
      errors[field.name] = messages;
    }
  }

  return errors;
}

// The legacy gate checks only fields with a separate prompt; checkbox and option fields have none
// (CheckBoxField and OptionField derive from InputFieldWithoutSeparatePrompt), so the client never
// requires them and cannot block a save over them. An explicit false is a value in any case.
// A required read-only field is left to the server, whose gate reports it when its value is
// missing: the client can never fill it, so skipping it here is the weaker form of that check.
function isRequiredGated(field: FieldDefinition): boolean {
  if (!field.validation.required || !isWritableDataField(field)) {
    return false;
  }

  switch (field.fieldType) {
    case 'text':
    case 'multiline':
    case 'legalValues':
    case 'number':
    case 'date':
      return true;
    default:
      return false;
  }
}

/**
 * The required gate over the effective record (the draft laid over the loaded record; the draft
 * alone for a new record). Every missing required field is listed, in page then tab order, with the
 * legacy `FIELD_IS_REQUIRED_LONG` wording. A field is missing when its value is absent, `null` or
 * empty text, as on the server; whitespace is a value. Only writable fields are checked here: the
 * client can never fill a read-only field, so a required read-only one is reported by the server's
 * gate instead.
 */
export function checkRequired(definition: FormDefinition, effective: Values): FieldErrors {
  const errors = emptyFieldErrors();

  for (const page of definition.pages) {
    for (const field of page.fields) {
      if (!isRequiredGated(field) || Object.hasOwn(errors, field.name)) {
        continue;
      }

      const raw = Object.hasOwn(effective, field.name) ? effective[field.name] : undefined;
      const value = normalizeValue(field, raw);
      if (value === null || value === '') {
        errors[field.name] = [formatRequiredMessage(field.prompt, page.name)];
      }
    }
  }

  return errors;
}

/** Combines error sets per field, keeping first-seen field order and dropping repeated messages. */
export function mergeFieldErrors(...sets: FieldErrors[]): FieldErrors {
  const merged = emptyFieldErrors();

  for (const set of sets) {
    for (const [name, messages] of Object.entries(set)) {
      const target = Object.hasOwn(merged, name) ? merged[name] : [];
      for (const message of messages) {
        if (!target.includes(message)) {
          target.push(message);
        }
      }

      if (target.length > 0) {
        merged[name] = target;
      }
    }
  }

  return merged;
}

/** Whether any field holds at least one message. */
export function hasFieldErrors(errors: FieldErrors): boolean {
  return Object.values(errors).some((messages) => messages.length > 0);
}
