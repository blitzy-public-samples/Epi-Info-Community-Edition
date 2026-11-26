// The client's only import surface for API types. Each alias names a component
// schema of the generated ./schema.ts (npm run api:generate), so a contract change
// arrives here without edits; components, hooks, fixtures and tests import from this
// file, never from ./schema.ts. Type declarations only: consumers use `import type`.
import type { components } from './schema';

type Schemas = components['schemas'];

// Forms

/** One entry of the forms list. */
export type FormSummary = Schemas['FormSummary'];

/**
 * A form's pages in `position` order and the check-code diagnostics known from its
 * program text. `canInsert`, `canUpdate` and `canDelete` are `null` until permission
 * awareness is delivered.
 */
export type FormDefinition = Schemas['FormDefinition'];

/** One page; `fields` lists every field on the page in `tabIndex` order. */
export type PageDefinition = Schemas['PageDefinition'];

/**
 * One field of a page.
 * - `tabIndex` may be fractional.
 * - `unsupportedType` is the `metaFieldTypes` name of an unsupported field, else `null`.
 * - `options` is set for `option` (value = 0-based index) and `legalValues`
 *   (value = the label text), else `null`.
 * - `members` is a group's field names, else `null`.
 */
export type FieldDefinition = Schemas['FieldDefinition'];

/** Rendering and value type of a field; `unsupported` fields are never written. */
export type FieldType = Schemas['FieldType'];

/**
 * Metadata validation of a field. `lower`/`upper` are numbers on number fields and
 * `YYYY-MM-DD` strings on date fields; `maxLength`, `lower`, `upper` and `pattern`
 * are `null` when unset.
 */
export type FieldValidation = Schemas['FieldValidation'];

/** One choice of an `option` or `legalValues` field. */
export type FieldOption = Schemas['FieldOption'];

// Values

/** Scalar value of one field on the wire. */
export type FieldValue = string | number | boolean | null;

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
export type RecordDto = Schemas['RecordDto'];

/** A records-list row; `values` holds the grid columns only. */
export type RecordSummary = Schemas['RecordSummary'];

/** One page of the records list; `total` counts every record matching the filter. */
export type RecordPage = Schemas['RecordPage'];

/** Number of records in the selected set. */
export type RecordCountResponse = Schemas['RecordCountResponse'];

/** A record's place in the selected set; `position` is `null` when it is outside the set. */
export type NavigationPosition = Schemas['NavigationPosition'];

/**
 * One Find criterion: a searchable field, or `GlobalRecordId`/`RecStatus`. A string
 * `value` uses the legacy Find syntax (`a or b`, `>=5`, `Sm*`).
 */
export type SearchCriterion = Schemas['SearchCriterion'];

/** A Find match; `values` holds the grid columns only and `position` is its selected-set position. */
export type SearchResult = Schemas['SearchResult'];

/** At most 200 matches in `UniqueKey` order; `total` counts all matches and `truncated` is `total > 200`. */
export type SearchResponse = Schemas['SearchResponse'];

// Check code

/** Exact wire spellings of the field events. */
export type FieldEventKind = 'FieldBefore' | 'FieldAfter' | 'FieldClick';

/**
 * Effects of one field event. A field is in at most one of `enabled`/`disabled` and
 * in at most one of `cleared`/`assigned`; `dialogs` are in execution order.
 */
export type FieldEventResponse = Schemas['FieldEventResponse'];

/** A check-code `DIALOG`; `title` is `null` without `TITLETEXT`. */
export type DialogMessage = Schemas['DialogMessage'];

/** A check-code diagnostic; `line` is 1-based in the form's check-code text. */
export type Diagnostic = Schemas['Diagnostic'];

/** `info` for a skipped `GEOCODE`, `warning` for unsupported check code, `error` at run time. */
export type DiagnosticSeverity = Schemas['DiagnosticSeverity'];

// Requests

/** Body of a record create or update; on update an absent field is unchanged. */
export type RecordWriteRequest = Schemas['RecordWriteRequest'];

/** Body of a Find; `includeDeleted` and `excludeGlobalRecordId` are optional. */
export type SearchRequest = Schemas['SearchRequest'];

/** Body of a field event: the event, the field it is raised on and the record's current values. */
export type FieldEventRequest = Schemas['FieldEventRequest'];

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
  Assert<Equals<FieldValue, Schemas['FieldValue']>>,
  Assert<Equals<Values, Schemas['RecordWriteRequest']['values']>>,
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
