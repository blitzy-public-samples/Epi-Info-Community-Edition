// The client's only import surface for API types. Each alias names a component
// schema of the generated ./schema.ts (npm run api:generate), so a contract change
// arrives here without edits; components, hooks, fixtures and tests import from this
// file, never from ./schema.ts. Type declarations only: consumers use `import type`.
import type { components } from './schema';

type Schemas = components['schemas'];

/**
 * A generated schema type as the client holds it: every member the wire types as
 * `FieldValue` (record and grid `values`, `assigned` and criterion values) is the
 * client's {@link FieldValue}, which may also be a bigint or, in what the client sends,
 * an {@link ExactDecimal}; every other member, the narrower bound and option value types
 * included, is unchanged.
 */
type ClientValued<T> =
  Equals<T, Schemas['FieldValue']> extends true
    ? FieldValue
    : T extends readonly (infer Item)[]
      ? ClientValued<Item>[]
      : T extends object
        ? { [K in keyof T]: ClientValued<T[K]> }
        : T;

// Forms

/** One entry of the forms list. */
export type FormSummary = Schemas['FormSummary'];

/**
 * A form's pages in `position` order and the check-code diagnostics known from its
 * program text. `canInsert`, `canUpdate` and `canDelete` are `null` until permission
 * awareness is delivered.
 */
export type FormDefinition = ClientValued<Schemas['FormDefinition']>;

/** One page; `fields` lists every field on the page in `tabIndex` order. */
export type PageDefinition = ClientValued<Schemas['PageDefinition']>;

/**
 * One field of a page.
 * - `tabIndex` may be fractional.
 * - `unsupportedType` is the `metaFieldTypes` name of an unsupported field, else `null`.
 * - `options` is set for `option` (value = 0-based index) and `legalValues`
 *   (value = the label text), else `null`.
 * - `members` is a group's field names, else `null`.
 */
export type FieldDefinition = ClientValued<Schemas['FieldDefinition']>;

/** Rendering and value type of a field; `unsupported` fields are never written. */
export type FieldType = Schemas['FieldType'];

/**
 * Metadata validation of a field. `lower`/`upper` are numbers on number fields and
 * `YYYY-MM-DD` strings on date fields; `maxLength`, `lower`, `upper` and `pattern`
 * are `null` when unset.
 */
export type FieldValidation = ClientValued<Schemas['FieldValidation']>;

/** One choice of an `option` or `legalValues` field. */
export type FieldOption = ClientValued<Schemas['FieldOption']>;

// Values

/**
 * Scalar value of one field: on the wire a JSON string, number, boolean or `null`. A
 * whole number beyond ±`Number.MAX_SAFE_INTEGER` within the signed 64-bit range of a
 * `bigint` column, which a JavaScript number cannot hold exactly, is a `bigint`, such as
 * 9007199254740993n; every other number is a `number`. `./client` reads and writes a
 * bigint as the JSON integer it holds, digit for digit, and reads and types such an
 * integer only as a bigint, so `===` compares it exactly. A number typed as a fraction
 * whose nearest double is a whole number is an {@link ExactDecimal}; it never comes from
 * the wire.
 */
export type FieldValue = string | number | bigint | boolean | null | ExactDecimal;

/**
 * A number typed into a number field that is not a whole number although its nearest double
 * is one, such as `9007199254740992.5`, `-9223372036854775808.5`, `2.0000000000000001` or
 * `1e-400`. Sent as that double, it would reach the server as a different, whole number,
 * which an integer column may accept; `literal` keeps the exact JSON number token instead
 * (no `+`, no leading zero in the integer part, a digit on each side of any point), and
 * `./client` writes it bare, so the server decides: an integer column rejects it as not a
 * whole number and a `float` column stores its nearest double. Only `numberFromText` in
 * `./client` makes one, frozen, and two are the same value when their literals are equal.
 * Every other number stays a `number` or a `bigint`; no response carries one.
 */
export type ExactDecimal = { readonly kind: 'decimal'; readonly literal: string };

/**
 * Field values keyed by canonical field name: `YYYY-MM-DD` dates, boolean
 * checkboxes, 0-based integer option indexes, `null` for empty.
 */
export type Values = Record<string, FieldValue>;

// Records

/**
 * A full record. `position` and `count` are intrinsic to the record (over the active
 * set when `recStatus` is 1, over every row when it is 0); they are not the
 * navigator's selected-set coordinates, which come from {@link NavigationPosition}.
 */
export type RecordDto = ClientValued<Schemas['RecordDto']>;

/** A records-list row; `values` holds the grid columns only. */
export type RecordSummary = ClientValued<Schemas['RecordSummary']>;

/** One page of the records list; `total` counts every record matching the filter. */
export type RecordPage = ClientValued<Schemas['RecordPage']>;

/** Number of records in the selected set. */
export type RecordCountResponse = Schemas['RecordCountResponse'];

/** A record's place in the selected set; `position` is `null` when it is outside the set. */
export type NavigationPosition = Schemas['NavigationPosition'];

/**
 * One Find criterion: a searchable field, or `GlobalRecordId`/`RecStatus`. A string
 * `value` uses the legacy Find syntax (`a or b`, `>=5`, `Sm*`).
 */
export type SearchCriterion = ClientValued<Schemas['SearchCriterion']>;

/** A Find match; `values` holds the grid columns only and `position` is its selected-set position. */
export type SearchResult = ClientValued<Schemas['SearchResult']>;

/** At most 200 matches in `UniqueKey` order; `total` counts all matches and `truncated` is `total > 200`. */
export type SearchResponse = ClientValued<Schemas['SearchResponse']>;

// Check code

/** Exact wire spellings of the field events. */
export type FieldEventKind = 'FieldBefore' | 'FieldAfter' | 'FieldClick';

/**
 * Effects of one field event. A field is in at most one of `enabled`/`disabled` and
 * in at most one of `cleared`/`assigned`; `dialogs` are in execution order.
 */
export type FieldEventResponse = ClientValued<Schemas['FieldEventResponse']>;

/** A check-code `DIALOG`; `title` is `null` without `TITLETEXT`. */
export type DialogMessage = Schemas['DialogMessage'];

/** A check-code diagnostic; `line` is 1-based in the form's check-code text. */
export type Diagnostic = Schemas['Diagnostic'];

/** `info` for a skipped `GEOCODE`, `warning` for unsupported check code, `error` at run time. */
export type DiagnosticSeverity = Schemas['DiagnosticSeverity'];

// Requests

/** Body of a record create or update; on update an absent field is unchanged. */
export type RecordWriteRequest = ClientValued<Schemas['RecordWriteRequest']>;

/** Body of a Find; `includeDeleted` and `excludeGlobalRecordId` are optional. */
export type SearchRequest = ClientValued<Schemas['SearchRequest']>;

/** Body of a field event: the event, the field it is raised on and the record's current values. */
export type FieldEventRequest = ClientValued<Schemas['FieldEventRequest']>;

// Other responses

/** `status` is `healthy` with 200 and `unhealthy` with 503. */
export type HealthResponse = Schemas['HealthResponse'];

/** RFC 7807 body of every `/api` error; `instance` is never written. */
export type ProblemDetails = Schemas['ProblemDetails'];

/** Field-level 400: `errors` is keyed by canonical field name, or by parameter name for route and query input. */
export type HttpValidationProblemDetails = Schemas['HttpValidationProblemDetails'];

/** 500 or 503 body of a record create, update or delete, stating what is known about the data. */
export type WriteProblemDetails = Schemas['WriteProblemDetails'];

/** What a failed write is known to have done; only `notWritten` is safe to resend automatically. */
export type WriteOutcome = 'notWritten' | 'unknown' | 'written';

// Contract drift checks

// Exact type identity, so a widened, narrowed or optional member fails the check.
type Equals<A, B> = (<T>() => T extends A ? 1 : 2) extends (<T>() => T extends B ? 1 : 2) ? true : false;
type Assert<T extends true> = T;

/**
 * Compile-time checks that the generated schema still matches the wire contract:
 * `tsc --noEmit` fails when an entry is not `true`.
 */
export type ContractChecks = [
  Assert<Equals<Schemas['FieldValue'], string | number | boolean | null>>,
  Assert<Equals<FieldValue, Schemas['FieldValue'] | bigint | ExactDecimal>>,
  Assert<Equals<Schemas['RecordWriteRequest']['values'], Record<string, Schemas['FieldValue']>>>,
  // ClientValued reaches every FieldValue member the client reads or sends.
  Assert<Equals<RecordWriteRequest['values'], Values>>,
  Assert<Equals<RecordDto['values'], Values>>,
  Assert<Equals<FieldEventResponse['assigned'], Values>>,
  // A bound is no FieldValue on the wire, so the client reads it as JSON.parse does, never as a bigint.
  Assert<Equals<FieldValidation['lower'], Schemas['FieldValidation']['lower']>>,
  Assert<Equals<WriteOutcome, NonNullable<Schemas['WriteProblemDetails']['writeOutcome']>>>,
  Assert<Equals<FieldEventKind, Schemas['FieldEventRequest']['event']>>,
  Assert<
    Equals<
      FieldType,
      'label' | 'group' | 'text' | 'multiline' | 'number' | 'date' | 'checkbox' | 'option' | 'legalValues' | 'unsupported'
    >
  >,
  Assert<Equals<DiagnosticSeverity, 'info' | 'warning' | 'error'>>,
  Assert<Equals<NavigationPosition['position'], number | null>>,
];
