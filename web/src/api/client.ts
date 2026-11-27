// The client's one way to reach the API. Hooks and components call an ApiClient from this
// module; nothing else in web/src builds an API URL, calls fetch or imports openapi-fetch.
// Paths, parameters and bodies are typed by the generated `paths` of ./schema, so a call
// that no longer matches the OpenAPI document fails `tsc`. Every call settles into an
// ApiResult here, and every failure is parsed by ./errors.
//
// A success body is checked against its contract shape before it is reported: callers
// receive only well-formed data, and an OK response whose body is malformed or is not
// JSON settles as a failure of its own status, as does a record create, update or delete
// answered by any status but its contracted 201, 200 or 204, and a health check answered
// by any but its 200. So does a response, OK or
// not, whose body could not be read to its end: its status arrived, so only a request
// that received no response at all settles as networkError(). For a POST or PUT such a
// failure states no outcome, so the save may have been applied and is never resent
// without the user's explicit consent.
//
// The client keeps no state, caching or retries; its one timer is each request's deadline
// (REQUEST_DEADLINE_MS), which ends a request that has not completed as a failure with no
// response and never resends it. Retry decisions belong to isSafeToResend and the hooks,
// and values are sent exactly as the caller gives them, because the editor alone decides
// which fields a save carries. Every request asks for JSON or ProblemDetails (Accept).
//
// Field values are exact in both directions: a field value that is a JSON integer beyond
// ±Number.MAX_SAFE_INTEGER that a `bigint` column can hold, such as 9007199254740993, is
// read as a bigint rather than rounded to the nearest double, and a bigint is written as
// that JSON integer, digit for digit. A field value that is not whole although its nearest
// double is, such as 9007199254740992.5 typed into a number field or an assigned
// 1.0000000000000001 the API writes, is an ExactDecimal, read and written as that JSON
// number token, never as the whole double. Every other number, such as a `tabIndex` or a
// `count`, is read as JSON.parse reads it. The wire keeps plain JSON numbers.
import createClient from 'openapi-fetch';

import { isAbortError, networkError, parseProblem, type ApiError } from './errors';
import type { components, paths } from './schema';
import type {
  Diagnostic,
  DiagnosticSeverity,
  ExactDecimal,
  FieldDefinition,
  FieldEventRequest,
  FieldEventResponse,
  FieldOption,
  FieldType,
  FieldValidation,
  FieldValue,
  FormDefinition,
  FormSummary,
  NavigationPosition,
  PageDefinition,
  RecordCountResponse,
  RecordDto,
  RecordPage,
  RecordSummary,
  RecordWriteRequest,
  SearchRequest,
  SearchResponse,
  SearchResult,
  Values,
} from './types';

/**
 * The settled outcome of one call: the response body and status on success, else the
 * parsed failure. Hooks branch on `ok` and never see a `Response`.
 */
export type ApiResult<T> = { ok: true; data: T; status: number } | { ok: false; error: ApiError };

export interface ApiClientOptions {
  /** Absolute origin the API paths are appended to; defaults to the page's own origin. */
  baseUrl?: string;
  /** Replaces the global fetch, as tests do; it receives the built `Request`. */
  fetch?: typeof fetch;
  /**
   * How long one request may take to complete, in milliseconds; REQUEST_DEADLINE_MS unless
   * this is a positive finite number, and at most the longest delay a timer honours.
   */
  deadlineMs?: number;
}

/** Query of the records list; an absent member takes the server default. */
export type RecordListQuery = NonNullable<paths['/api/forms/{viewId}/records']['get']['parameters']['query']>;

/** Selects the record set; without `includeDeleted` the server uses the active set. */
export type RecordSetOptions = NonNullable<paths['/api/forms/{viewId}/records/count']['get']['parameters']['query']>;

/** The body of a `GET /health` 200: the health answer whose status is `healthy`. */
export type HealthyResponse = paths['/health']['get']['responses'][200]['content']['application/json'];

/**
 * Typed calls of every API endpoint. The last parameter of each is an optional
 * `AbortSignal`; an aborted call rejects with an AbortError, which callers recognise with
 * `isAbortError`, and every other outcome resolves to an `ApiResult`.
 *
 * A call whose request and whole response have not completed within the client's deadline
 * (REQUEST_DEADLINE_MS) is cancelled and settles as a failure with no response, kind
 * `network`: a read offers Retry, and a create or update counts as possibly applied. The
 * deadline never resends a request.
 *
 * Members are function-typed properties rather than methods: they never use `this`, so a
 * hook may pass `client.getRecordAt` around unbound.
 */
export interface ApiClient {
  /** `GET /api/forms`: every form of the connected project. */
  readonly listForms: (signal?: AbortSignal) => Promise<ApiResult<FormSummary[]>>;

  /** `GET /api/forms/{viewId}`: the form's pages and fields; 404 for an unknown form. */
  readonly getForm: (viewId: number, signal?: AbortSignal) => Promise<ApiResult<FormDefinition>>;

  /**
   * `GET /api/forms/{viewId}/records`: one page of the grid. A blank `q`, as
   * {@link isBlankQuery} decides, is not sent, because the server treats it as no filter;
   * any other `q` is sent verbatim.
   */
  readonly listRecords: (
    viewId: number,
    query?: RecordListQuery,
    signal?: AbortSignal,
  ) => Promise<ApiResult<RecordPage>>;

  /** `GET /api/forms/{viewId}/records/count`: the size of the selected set. */
  readonly countRecords: (
    viewId: number,
    options?: RecordSetOptions,
    signal?: AbortSignal,
  ) => Promise<ApiResult<RecordCountResponse>>;

  /**
   * `GET /api/forms/{viewId}/records/at/{position}`: the record at a 1-based position of
   * the selected set; 404 outside 1..count.
   */
  readonly getRecordAt: (
    viewId: number,
    position: number,
    options?: RecordSetOptions,
    signal?: AbortSignal,
  ) => Promise<ApiResult<RecordDto>>;

  /** `GET /api/forms/{viewId}/records/{globalRecordId}`: a record by id, deleted records included. */
  readonly getRecord: (viewId: number, globalRecordId: string, signal?: AbortSignal) => Promise<ApiResult<RecordDto>>;

  /**
   * `GET /api/forms/{viewId}/records/{globalRecordId}/position`: the record's place in the
   * selected set, the navigator's "[n] of N"; `position` is `null` outside the set.
   */
  readonly getRecordPosition: (
    viewId: number,
    globalRecordId: string,
    options?: RecordSetOptions,
    signal?: AbortSignal,
  ) => Promise<ApiResult<NavigationPosition>>;

  /**
   * `POST /api/forms/{viewId}/records`: creates a record; succeeds with status 201 only.
   * Any other 2xx settles as a failure with no write outcome, so the create may have been applied.
   */
  readonly createRecord: (viewId: number, values: Values, signal?: AbortSignal) => Promise<ApiResult<RecordDto>>;

  /**
   * `PUT /api/forms/{viewId}/records/{globalRecordId}`: saves the submitted fields only; 409 for a
   * deleted record. Succeeds with status 200 only; any other 2xx settles as a failure with no
   * write outcome, so the save may have been applied.
   */
  readonly updateRecord: (
    viewId: number,
    globalRecordId: string,
    values: Values,
    signal?: AbortSignal,
  ) => Promise<ApiResult<RecordDto>>;

  /**
   * `DELETE /api/forms/{viewId}/records/{globalRecordId}`: soft-deletes the record;
   * succeeds with status 204 only, and `data: null`, also when it was already deleted.
   * Any other 2xx settles as a failure with no write outcome, so the delete is never
   * reported done without its acknowledgement.
   */
  readonly deleteRecord: (viewId: number, globalRecordId: string, signal?: AbortSignal) => Promise<ApiResult<null>>;

  /** `POST /api/forms/{viewId}/records/search`: Find with the legacy criteria syntax. */
  readonly searchRecords: (
    viewId: number,
    request: SearchRequest,
    signal?: AbortSignal,
  ) => Promise<ApiResult<SearchResponse>>;

  /** `POST /api/forms/{viewId}/checkcode/field-event`: the effects of one field event; nothing is written. */
  readonly raiseFieldEvent: (
    viewId: number,
    request: FieldEventRequest,
    signal?: AbortSignal,
  ) => Promise<ApiResult<FieldEventResponse>>;

  /**
   * `GET /health`: succeeds with status 200 only, and `{status: 'healthy'}`, when SQL Server
   * answers. A 503 settles as an `unavailable` error, a 200 whose body is not that contracted
   * `healthy` answer, `unhealthy` included, as a `server` error of status 200, and any other
   * 2xx, a 202 or 203 with a `healthy` body included, as a `server` error of its own status.
   */
  readonly getHealth: (signal?: AbortSignal) => Promise<ApiResult<HealthyResponse>>;
}

/** What an openapi-fetch call resolves to; `data` and `error` are never both set. */
interface Exchange<D> {
  readonly data?: D;
  readonly error?: unknown;
  readonly response: Response;
}

// The body-less copies readingJsonExactly returns for a response whose status arrived but
// whose body could not be read, or whose OK body is not JSON. openapi-fetch resolves the
// exchange with the very Response the fetch wrapper returned, so settle recognises them.
const unreadableResponses = new WeakSet<Response>();

// Settles one call. `read` turns an OK response's body into the result's data, returning
// `undefined` when the body is missing or is not the shape the endpoint's contract
// returns. Such a response settles as a failure of its own status, which parseProblem
// reads as `server` with no write outcome: a read offers Retry, and a POST or PUT counts
// as possibly applied, since the server may have committed it before the body went wrong.
// With `successStatus`, an OK response of any other status settles the same way: a write
// is acknowledged only by the status its contract gives, so a 202 or another 2xx from
// something other than the API proves neither that the write was applied nor that it was not.
// A response marked unreadable (readingJsonExactly), OK or not, also settles as a failure
// of its own status with no write outcome: a 2xx as `server`, an error status by its kind.
async function settle<D, T>(
  signal: AbortSignal | undefined,
  send: () => Promise<Exchange<D>>,
  read: (data: D | undefined) => T | undefined,
  successStatus?: number,
): Promise<ApiResult<T>> {
  let exchange: Exchange<D>;
  try {
    exchange = await send();
  } catch (e) {
    // An abort is the caller's own decision, not a failure, so it is not turned into one.
    if (signal?.aborted === true || isAbortError(e)) {
      throw e;
    }
    // No response arrived: a network failure, so nothing is known about a write. A
    // response whose body could not be read or parsed does not end here; it resolves
    // marked unreadable and settles below with the status it carried.
    return { ok: false, error: networkError() };
  }

  const { data, error, response } = exchange;
  // The status arrived but the body did not, or an OK body was not JSON: nothing in it can
  // be trusted, so it settles as a failure of that status that states no write outcome.
  if (unreadableResponses.has(response)) {
    return { ok: false, error: parseProblem(response.status, undefined) };
  }

  // openapi-fetch reports an empty error body as `error: undefined`, so only the status
  // decides between success and failure.
  if (!response.ok) {
    return { ok: false, error: parseProblem(response.status, error) };
  }

  if (successStatus !== undefined && response.status !== successStatus) {
    return { ok: false, error: parseProblem(response.status, undefined) };
  }

  const value = read(data);
  if (value === undefined) {
    return { ok: false, error: parseProblem(response.status, undefined) };
  }

  return { ok: true, data: value, status: response.status };
}

/**
 * Settles a call whose success carries a JSON body. The body is reported only when
 * `isBody` holds for it; any other OK body settles as a failure.
 * @param successStatus The one status that acknowledges the call, as a record create
 *   (201) and update (200) have; without it any OK status does.
 */
function withBody<D>(
  signal: AbortSignal | undefined,
  send: () => Promise<Exchange<D>>,
  isBody: (body: unknown) => body is D,
  successStatus?: number,
): Promise<ApiResult<D>> {
  return settle(signal, send, (data) => (isBody(data) ? data : undefined), successStatus);
}

/**
 * Settles a call whose one success is a bodyless 204, as a record delete has. Any other
 * OK status, a 205 or a 200 with a body included, acknowledges nothing and settles as a
 * failure of its own status.
 */
function withoutBody(signal: AbortSignal | undefined, send: () => Promise<Exchange<unknown>>): Promise<ApiResult<null>> {
  return settle(signal, send, () => null, 204);
}

// Success bodies. Each guard reads a parsed body as `unknown` and holds only when every
// member its schema in ./schema requires is present with the type the contract gives it,
// so no caller reads a member that a malformed body lacks. A member the contract types as
// a union holds only with one of its published arms, narrowed to the one arm its field's
// type takes where the form definition fixes it: an option value is a 0-based `int32`
// index on an `option` field and the label text on a `legalValues` field, and a bound is a
// number on a `number` field and a `YYYY-MM-DD` date on a `date` field. Likewise a field's
// `options` are a list on an `option` or `legalValues` field and `null` on a field of every
// other published type. Two parts are left to their one reader: a field's `fieldType` is
// checked as a string, because FieldRenderer shows a field of a type it does not know as a
// protected placeholder, and the field-event response is passed on as parsed (raiseFieldEvent).

function isObject(value: unknown): value is Readonly<Record<string, unknown>> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function isString(value: unknown): value is string {
  return typeof value === 'string';
}

function isStringOrNull(value: unknown): value is string | null {
  return value === null || isString(value);
}

function isBoolean(value: unknown): value is boolean {
  return typeof value === 'boolean';
}

function isBooleanOrNull(value: unknown): value is boolean | null {
  return value === null || isBoolean(value);
}

/** An `int32` member: JSON carries it as a number with no fraction. */
function isInteger(value: unknown): value is number {
  return typeof value === 'number' && Number.isInteger(value);
}

function isIntegerOrNull(value: unknown): value is number | null {
  return value === null || isInteger(value);
}

function isFiniteNumber(value: unknown): value is number {
  return typeof value === 'number' && Number.isFinite(value);
}

function isArrayOf<T>(value: unknown, isItem: (item: unknown) => item is T): value is T[] {
  if (!Array.isArray(value)) {
    return false;
  }
  const items: readonly unknown[] = value;
  return items.every(isItem);
}

/**
 * Whether `value` is a field value as this client reads and holds one: `null`, a string, a
 * boolean, a finite number, a bigint, which is a whole number beyond the safe range kept
 * exact (FIELD_VALUE_PATHS), or an {@link ExactDecimal} this module made. It is the one check
 * of a scalar field value in a parsed response, so a reader of a body that is passed on
 * unchecked, such as a field-event answer, accepts every value the client decodes.
 */
export function isFieldValue(value: unknown): value is FieldValue {
  return (
    value === null ||
    isString(value) ||
    isBoolean(value) ||
    isFiniteNumber(value) ||
    typeof value === 'bigint' ||
    isExactDecimal(value)
  );
}

/** A `values` map: an object whose own entries are all scalar field values. */
function isValues(value: unknown): value is Values {
  return isObject(value) && Object.values(value).every(isFieldValue);
}

function isRecordDto(body: unknown): body is RecordDto {
  return (
    isObject(body) &&
    isString(body.globalRecordId) &&
    isInteger(body.uniqueKey) &&
    isInteger(body.recStatus) &&
    isInteger(body.position) &&
    isInteger(body.count) &&
    isValues(body.values)
  );
}

function isRecordSummary(body: unknown): body is RecordSummary {
  return (
    isObject(body) &&
    isString(body.globalRecordId) &&
    isInteger(body.uniqueKey) &&
    isInteger(body.recStatus) &&
    isValues(body.values)
  );
}

function isRecordPage(body: unknown): body is RecordPage {
  return (
    isObject(body) &&
    isArrayOf(body.items, isRecordSummary) &&
    isInteger(body.total) &&
    isInteger(body.page) &&
    isInteger(body.pageSize)
  );
}

function isRecordCountResponse(body: unknown): body is RecordCountResponse {
  return isObject(body) && isInteger(body.count);
}

function isNavigationPosition(body: unknown): body is NavigationPosition {
  return isObject(body) && isIntegerOrNull(body.position) && isInteger(body.count);
}

function isSearchResult(body: unknown): body is SearchResult {
  return isObject(body) && isInteger(body.position) && isRecordSummary(body);
}

function isSearchResponse(body: unknown): body is SearchResponse {
  return (
    isObject(body) && isArrayOf(body.items, isSearchResult) && isInteger(body.total) && isBoolean(body.truncated)
  );
}

function isFormSummary(body: unknown): body is FormSummary {
  return isObject(body) && isInteger(body.viewId) && isString(body.name) && isInteger(body.pageCount);
}

function isFormSummaryList(body: unknown): body is FormSummary[] {
  return isArrayOf(body, isFormSummary);
}

const DIAGNOSTIC_SEVERITIES: Readonly<Record<DiagnosticSeverity, true>> = { info: true, warning: true, error: true };

function isDiagnosticSeverity(value: unknown): value is DiagnosticSeverity {
  return isString(value) && Object.hasOwn(DIAGNOSTIC_SEVERITIES, value);
}

// The field types whose option values or bounds the form definition narrows to one
// published arm. Typed by the contract, so a renamed field type fails tsc here.
const OPTION_FIELD = 'option' satisfies FieldType;
const LEGAL_VALUES_FIELD = 'legalValues' satisfies FieldType;
const NUMBER_FIELD = 'number' satisfies FieldType;
const DATE_FIELD = 'date' satisfies FieldType;

// The largest `int32`, the format of an option index.
const MAX_INT32 = 2_147_483_647;

/** An option index as the contract publishes one: an `int32` of at least 0. */
function isOptionIndex(value: unknown): value is number {
  return isInteger(value) && value >= 0 && value <= MAX_INT32;
}

// A bound's `date` format: YYYY-MM-DD with ASCII digits; without the m flag, `$` matches
// only at the end of the text.
const ISO_DATE = /^(\d{4})-(\d{2})-(\d{2})$/;

// A YYYY-MM-DD text naming a real day, by integer arithmetic, so 2011-02-30 and 2011-13-45
// are not dates. Year 0000 is not one either: the server writes a bound from a .NET
// DateOnly, whose years start at 0001.
function isCalendarDate(value: unknown): value is string {
  if (!isString(value)) {
    return false;
  }
  const match = ISO_DATE.exec(value);
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

/**
 * An option value of a field of `fieldType`: the index on an `option` field, the label
 * text on a `legalValues` field, and either published arm on a field of any other type.
 */
function isOptionValue(value: unknown, fieldType: string): value is FieldOption['value'] {
  switch (fieldType) {
    case OPTION_FIELD:
      return isOptionIndex(value);
    case LEGAL_VALUES_FIELD:
      return isString(value);
    default:
      return isOptionIndex(value) || isString(value);
  }
}

function isFieldOption(body: unknown, fieldType: string): body is FieldOption {
  return isObject(body) && isOptionValue(body.value, fieldType) && isString(body.label);
}

// Whether a field of each published type carries an options list: an `option` or
// `legalValues` field does, possibly empty, and every other type carries `null`. Keyed by
// the contract's FieldType, so a renamed, added or removed field type fails tsc here.
const OPTION_LISTS: Readonly<Record<FieldType, boolean>> = {
  label: false,
  group: false,
  text: false,
  multiline: false,
  number: false,
  date: false,
  checkbox: false,
  option: true,
  legalValues: true,
  unsupported: false,
};

/** A type the contract publishes; an own key only, so `constructor` or `__proto__` is none. */
function isPublishedFieldType(fieldType: string): fieldType is FieldType {
  return Object.hasOwn(OPTION_LISTS, fieldType);
}

/**
 * The `options` of a field of `fieldType`: a list, possibly empty, of that type's options on
 * an `option` or `legalValues` field, and `null` on a field of every other published type.
 * A field of a type the contract does not name takes `null` or a list of either published
 * arm, because FieldRenderer shows it as a protected placeholder that reads no option.
 */
function isFieldOptions(value: unknown, fieldType: string): value is FieldDefinition['options'] {
  const isOption = (option: unknown): option is FieldOption => isFieldOption(option, fieldType);
  if (!isPublishedFieldType(fieldType)) {
    return value === null || isArrayOf(value, isOption);
  }
  return OPTION_LISTS[fieldType] ? isArrayOf(value, isOption) : value === null;
}

/**
 * A `lower` or `upper` bound of a field of `fieldType`: `null` when unset, else a finite
 * number on a `number` field, a calendar date on a `date` field, and either on a field of
 * any other type. A bound is no field value, so parseJson reads it as JSON.parse does,
 * never as a bigint.
 */
function isBound(value: unknown, fieldType: string): value is FieldValidation['lower'] {
  if (value === null) {
    return true;
  }
  switch (fieldType) {
    case NUMBER_FIELD:
      return isFiniteNumber(value);
    case DATE_FIELD:
      return isCalendarDate(value);
    default:
      return isFiniteNumber(value) || isCalendarDate(value);
  }
}

function isFieldValidation(body: unknown, fieldType: string): body is FieldValidation {
  return (
    isObject(body) &&
    isBoolean(body.required) &&
    isBoolean(body.readOnly) &&
    isIntegerOrNull(body.maxLength) &&
    isBound(body.lower, fieldType) &&
    isBound(body.upper, fieldType) &&
    isStringOrNull(body.pattern)
  );
}

function isFieldDefinition(body: unknown): body is FieldDefinition {
  if (!isObject(body)) {
    return false;
  }
  // The field's type decides whether it carries options, and the arm its option values and
  // bounds take.
  const { fieldType } = body;
  return (
    isString(body.name) &&
    isString(fieldType) &&
    isStringOrNull(body.prompt) &&
    isFiniteNumber(body.tabIndex) &&
    isInteger(body.pageId) &&
    isBoolean(body.unsupported) &&
    isStringOrNull(body.unsupportedType) &&
    isFieldValidation(body.validation, fieldType) &&
    isFieldOptions(body.options, fieldType) &&
    (body.members === null || isArrayOf(body.members, isString))
  );
}

function isPageDefinition(body: unknown): body is PageDefinition {
  return (
    isObject(body) &&
    isInteger(body.pageId) &&
    isString(body.name) &&
    isInteger(body.position) &&
    isArrayOf(body.fields, isFieldDefinition)
  );
}

function isDiagnostic(body: unknown): body is Diagnostic {
  return (
    isObject(body) &&
    isDiagnosticSeverity(body.severity) &&
    isString(body.form) &&
    isInteger(body.line) &&
    isString(body.command) &&
    isString(body.message)
  );
}

function isFormDefinition(body: unknown): body is FormDefinition {
  return (
    isObject(body) &&
    isInteger(body.viewId) &&
    isString(body.name) &&
    isArrayOf(body.pages, isPageDefinition) &&
    isArrayOf(body.unsupportedCheckCode, isDiagnostic) &&
    isBooleanOrNull(body.canInsert) &&
    isBooleanOrNull(body.canUpdate) &&
    isBooleanOrNull(body.canDelete)
  );
}

// The one status a `GET /health` 200 carries; typed by the contract, so a renamed status fails tsc here.
const HEALTHY: HealthyResponse['status'] = 'healthy';

function isHealthyResponse(body: unknown): body is HealthyResponse {
  return isObject(body) && body.status === HEALTHY;
}

// `null` rather than an absent member, so the init also compiles under
// exactOptionalPropertyTypes; fetch treats both as "no signal".
function abortable(signal: AbortSignal | undefined): { signal: AbortSignal | null } {
  return { signal: signal ?? null };
}

// The API rejects every other media type with 415, `+json` types included.
const jsonHeaders: Record<string, string> = { 'Content-Type': 'application/json' };

// Sent with every request: the media types the API answers with, a JSON body on success and
// ProblemDetails on failure.
const acceptHeaders: Record<string, string> = { Accept: 'application/json, application/problem+json' };

/**
 * How long one request may take, in milliseconds: one bound per attempt, from sending the
 * request to receiving the whole response, headers and body. It is twice the API's default
 * `Database:CommandTimeoutSeconds` (30 s), so when one database command stalls, the
 * server's own 503 and its write outcome (a lock wait, for instance, answers `notWritten`,
 * which Retry may resend) arrive before the client gives up. A request that never
 * completes therefore cannot hold the editor's save or a record transition.
 */
export const REQUEST_DEADLINE_MS = 60_000;

// The largest delay a timer honours; setTimeout fires at once for a longer one.
const MAX_TIMER_DELAY_MS = 2_147_483_647;

// REQUEST_DEADLINE_MS unless `deadlineMs` is a positive finite number, and at most
// MAX_TIMER_DELAY_MS, the rule of the field-event queue's own deadline.
function deadlineOf(deadlineMs: number | undefined): number {
  if (deadlineMs === undefined || !Number.isFinite(deadlineMs) || deadlineMs <= 0) {
    return REQUEST_DEADLINE_MS;
  }
  return Math.min(deadlineMs, MAX_TIMER_DELAY_MS);
}

// What a request that outlived its deadline rejects with. It is named TimeoutError, never
// AbortError, so settle reports a failure with no response rather than the caller's abort.
function deadlineExceeded(): DOMException {
  return new DOMException('The request did not complete in time.', 'TimeoutError');
}

// What a call its caller aborted rejects with: the caller's signal's reason, as fetch rejects
// such a call, or an AbortError when the signal carries none.
function callerAbortReason(signal: AbortSignal): unknown {
  const reason: unknown = signal.reason;
  return reason === undefined ? new DOMException('The operation was aborted.', 'AbortError') : reason;
}

// What ended a call before its exchange completed: the caller's abort or the deadline.
interface Stopped {
  readonly stoppedBy: unknown;
}

/**
 * Sends `request` through `send` and reads its whole response within `deadlineMs`; the body
 * is read by readingJsonExactly, so nothing openapi-fetch reads after this returns can wait
 * on the network. The caller's signal is the one openapi-fetch put on `request`. When it is
 * already aborted, nothing is sent and the call rejects at once with the caller's reason, as
 * fetch rejects such a request. Otherwise the request sent is a copy whose signal the
 * caller's abort and the deadline both abort, and the call settles at the first of three
 * things: the whole response; the caller's abort, which
 * rejects it with the caller's reason; or the deadline, which rejects it with a TimeoutError.
 * An abort or the deadline settles the call at once, independently of the transport, and
 * only then cancels the transport with the same reason. So a transport that ignores its
 * signal or never settles cannot hold the call, and one that rejects as soon as it is
 * aborted cannot settle a timed-out call first with an AbortError, which settle would take
 * for the caller's own abort. Whatever the transport does later is ignored, and nothing is
 * resent.
 */
async function withinDeadline(
  request: Request,
  send: (request: Request) => Promise<Response>,
  deadlineMs: number,
): Promise<Response> {
  const caller = request.signal;
  if (caller.aborted) {
    throw callerAbortReason(caller);
  }
  const controller = new AbortController();
  let timer: ReturnType<typeof setTimeout> | undefined;
  let followCaller: (() => void) | undefined;
  // Resolves with the reason, which the call then throws, rather than rejecting with it: the
  // caller's reason may be any value, and the lint rules let a promise reject only with an Error.
  const stopped = new Promise<Stopped>((resolve) => {
    const stop = (reason: unknown): void => {
      clearTimeout(timer);
      resolve({ stoppedBy: reason });
      controller.abort(reason);
    };
    followCaller = () => {
      stop(callerAbortReason(caller));
    };
    caller.addEventListener('abort', followCaller, { once: true });
    timer = setTimeout(() => {
      stop(deadlineExceeded());
    }, deadlineMs);
  });
  const sent = new Request(request, { signal: controller.signal });
  const exchange = async (): Promise<Response> => readingJsonExactly(sent, await send(sent));

  try {
    const outcome = await Promise.race([exchange(), stopped]);
    if ('stoppedBy' in outcome) {
      throw outcome.stoppedBy;
    }
    return outcome;
  } finally {
    clearTimeout(timer);
    if (followCaller !== undefined) {
      caller.removeEventListener('abort', followCaller);
    }
  }
}

// Text made only of .NET whitespace (`char.IsWhiteSpace`), or empty.
const DOTNET_BLANK = /^[\t\n\v\f\r \u0085\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000]*$/;

/**
 * Whether a records-list `q` is blank, the text the server reads as no filter: empty, or
 * every UTF-16 code unit whitespace by .NET `char.IsWhiteSpace`, the test behind the
 * server's `string.IsNullOrWhiteSpace`. That set is U+0009–U+000D, U+0020, U+0085, U+00A0,
 * U+1680, U+2000–U+200A, U+2028, U+2029, U+202F, U+205F and U+3000. Unlike JavaScript's
 * `String.prototype.trim` and `\s`, it includes U+0085 and excludes U+FEFF, so a text the
 * server filters on is never dropped here, and a text it ignores is never sent.
 */
export function isBlankQuery(text: string): boolean {
  return DOTNET_BLANK.test(text);
}

// A blank `q` (isBlankQuery) is not sent; any other `q` is sent exactly as given, edge
// whitespace included, because the server matches it untrimmed.
function listQuery(query: RecordListQuery = {}): RecordListQuery {
  const { q, includeDeleted, page, pageSize } = query;
  return {
    ...(q !== undefined && !isBlankQuery(q) ? { q } : {}),
    ...(includeDeleted !== undefined ? { includeDeleted } : {}),
    ...(page !== undefined ? { page } : {}),
    ...(pageSize !== undefined ? { pageSize } : {}),
  };
}

// `includeDeleted` is sent only when the caller chose a set; absent means the active set.
function setQuery(options: RecordSetOptions = {}): RecordSetOptions {
  return options.includeDeleted !== undefined ? { includeDeleted: options.includeDeleted } : {};
}

function pageOrigin(): string {
  if (typeof window === 'undefined') {
    throw new Error('createApiClient needs a baseUrl outside a browser, where there is no page origin.');
  }
  return window.location.origin;
}

// Exact numbers

// A decimal literal as Number() reads one, such as 9.007199254740993e15: an optional sign,
// digits with an optional point, and an optional exponent. `Infinity` and the 0x, 0o and 0b
// forms do not match.
const DECIMAL_LITERAL = /^([+-]?)(\d*)(?:\.(\d*))?(?:[eE]([+-]?\d+))?$/;

// The 0x, 0o and 0b integers, which Number() reads unsigned and BigInt() reads alike.
const NON_DECIMAL_INTEGER = /^0[xXoObB]/;

// The signed 64-bit range, a `bigint` column's. Beyond it a number is outside every integer
// column's range and a `float` column stores its nearest double, so that double serves as
// well as the exact value would.
const MIN_INT64 = -(2n ** 63n);
const MAX_INT64 = 2n ** 63n - 1n;

// The exact integer a numeric literal stands for, or `null` when it stands for a fraction.
// It is only asked about a literal whose nearest double is a whole number beyond the safe
// range and below 2^64, so the digits it builds stay short.
function exactInteger(literal: string): bigint | null {
  if (NON_DECIMAL_INTEGER.test(literal)) {
    return BigInt(literal);
  }

  const match = DECIMAL_LITERAL.exec(literal);
  if (match === null) {
    return null;
  }

  // The literal is sign × digits × 10^shift; BigInt() ignores leading zeros.
  const [, sign = '', whole = '', fraction = '', exponent = '0'] = match;
  const digits = whole + fraction;
  const shift = Number(exponent) - fraction.length;
  if (shift >= 0) {
    return BigInt(sign + digits + '0'.repeat(shift));
  }

  const point = digits.length + shift;
  return point > 0 && /^0*$/.test(digits.slice(point)) ? BigInt(sign + digits.slice(0, point)) : null;
}

// A number as a field value holds it: the nearest double, except that a whole number beyond
// ±Number.MAX_SAFE_INTEGER within the signed 64-bit range is the exact bigint the literal
// stands for, so 9007199254740993 never becomes 9007199254740992. A fraction keeps its
// nearest double.
function exactNumber(literal: string): number | bigint {
  const nearest = Number(literal);
  // A double of 2^64 or more lies beyond the 64-bit range whatever value it was rounded from.
  if (!Number.isInteger(nearest) || Number.isSafeInteger(nearest) || Math.abs(nearest) >= 2 ** 64) {
    return nearest;
  }

  const exact = exactInteger(literal);
  return exact !== null && exact >= MIN_INT64 && exact <= MAX_INT64 ? exact : nearest;
}

// Typed fractions

// The number grammar of RFC 8259 over a whole text: the form an ExactDecimal's literal takes.
const JSON_NUMBER_TOKEN = /^-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?$/;

// Whether whole.fraction × 10^exponent is a whole number, decided from the digits alone so
// that no exponent, however large, builds a string: it is when every digit after the point
// the exponent moves is zero, as it is for zero itself. A huge exponent is ±Infinity here.
function isWholeDecimal(whole: string, fraction: string, exponent: number): boolean {
  const digits = whole + fraction;
  const point = whole.length + exponent;
  if (point >= digits.length) {
    return true;
  }
  return /^0*$/.test(digits.slice(Math.max(point, 0)));
}

// The JSON number token of a decimal literal that is not a whole number, else `null`: the
// sign without `+`, the integer part without leading zeros (`0` when it has no digits), the
// fraction digits as typed after a point only when there are any, and the exponent as typed.
function fractionLiteral(literal: string): string | null {
  const match = DECIMAL_LITERAL.exec(literal);
  if (match === null) {
    return null;
  }

  const [, sign = '', whole = '', fraction = '', exponent] = match;
  if ((whole === '' && fraction === '') || isWholeDecimal(whole, fraction, Number(exponent ?? '0'))) {
    return null;
  }

  const integerPart = whole.replace(/^0+/, '');
  return (
    (sign === '-' ? '-' : '') +
    (integerPart === '' ? '0' : integerPart) +
    (fraction === '' ? '' : `.${fraction}`) +
    (exponent === undefined ? '' : `e${exponent}`)
  );
}

/**
 * The number that typed text stands for, as a number field holds it: `null` when the
 * trimmed text is blank or is not a finite number (`abc`, `1e400`); otherwise the nearest
 * double, except that:
 * - an integer beyond ±`Number.MAX_SAFE_INTEGER` within the signed 64-bit range is the
 *   exact bigint (`9007199254740993`, `9.007199254740993e15`), as the client reads it
 *   from JSON;
 * - a number that is not whole but whose nearest double is (`9007199254740992.5`,
 *   `2.0000000000000001`, `1e-400`) is the frozen {@link ExactDecimal} of its literal, as
 *   the client reads such a field value from JSON, and the client writes it bare, so it
 *   never reaches the server as that whole double.
 *
 * Every other fraction, such as `34.5` or `0.1`, and every other whole number, such as
 * `1e20`, is its nearest double. The text is read as Number() reads it: surrounding
 * whitespace, a sign, a point, an exponent, and the 0x, 0o and 0b forms.
 */
export function numberFromText(text: string): number | bigint | ExactDecimal | null {
  const trimmed = text.trim();
  if (trimmed === '' || !Number.isFinite(Number(trimmed))) {
    return null;
  }

  return exactFieldNumber(trimmed);
}

// A number literal as a field value holds it, for typed text and for a field value in a
// response alike: exactNumber's value, except that a literal that is not whole although
// that value is becomes the ExactDecimal of its JSON number token, so 1.0000000000000001
// never becomes the whole number 1 on its way in or out.
function exactFieldNumber(literal: string): number | bigint | ExactDecimal {
  const value = exactNumber(literal);
  const fraction = typeof value === 'number' && Number.isInteger(value) ? fractionLiteral(literal) : null;
  return fraction === null ? value : exactDecimal(fraction);
}

// Every ExactDecimal this module made, and nothing else. Membership, not shape, makes one, so
// a `values` map whose fields are named `kind` and `literal` stays a map wherever it goes.
const EXACT_DECIMALS = new WeakSet<object>();

// The one maker of an ExactDecimal: frozen, so its literal can never change after it is
// checked. `literal` comes from fractionLiteral, which only ever builds a JSON number token.
function exactDecimal(literal: string): ExactDecimal {
  if (!JSON_NUMBER_TOKEN.test(literal)) {
    throw new RangeError('An exact decimal literal must be a JSON number token.');
  }
  // The brand is type-only, so the object made here is asserted to carry it.
  const decimal = Object.freeze({ kind: 'decimal', literal }) as ExactDecimal;
  EXACT_DECIMALS.add(decimal);
  return decimal;
}

/**
 * Whether `value` is an {@link ExactDecimal} this module made. An object that merely has its
 * shape, such as a `values` map with a `kind` of `decimal` and a `literal` field, is not one,
 * so only a literal checked as a JSON number token is ever written bare.
 */
export function isExactDecimal(value: unknown): value is ExactDecimal {
  return typeof value === 'object' && value !== null && EXACT_DECIMALS.has(value);
}

/**
 * Whether two field values are the same value: `Object.is`, so a bigint compares by value
 * and -0 differs from 0, except that two {@link ExactDecimal}s are the same when their
 * literals are equal, whichever objects hold them.
 */
export function sameFieldValue(a: FieldValue, b: FieldValue): boolean {
  return isExactDecimal(a) && isExactDecimal(b) ? a.literal === b.literal : Object.is(a, b);
}

// Exact JSON
//
// JSON.parse rounds every number to a double, and JSON.stringify throws on a bigint, so
// response bodies are read and request bodies written here. Both match the built-ins for
// every other value. A response is read exactly only at its field values; every other
// number, such as a `tabIndex`, a `uniqueKey` or a `count`, is the double JSON.parse reads,
// as its type says, so arithmetic on it never meets a bigint.

// The body an OK response of each ApiClient call carries.
type ResponseBody = {
  [K in keyof ApiClient]: Extract<Awaited<ReturnType<ApiClient[K]>>, { ok: true }>['data'];
}[keyof ApiClient];

type MemberPath<Path extends string, Key extends string> = Path extends '' ? Key : `${Path}.${Key}`;

// The paths of T's members, as FIELD_VALUE_PATHS writes them: for `Kind` 'fieldValue' its
// field values, the members whose client type admits a bigint; for `Kind` 'number' its
// other numbers.
type PathsOf<T, Kind extends 'fieldValue' | 'number', Path extends string = ''> = [bigint] extends [T]
  ? Kind extends 'fieldValue'
    ? Path
    : never
  : T extends readonly (infer Item)[]
    ? PathsOf<Item, Kind, `${Path}[]`>
    : T extends number
      ? Kind extends 'number'
        ? Path
        : never
      : T extends object
        ? {
            [K in keyof T & string]-?: PathsOf<T[K], Kind, MemberPath<Path, string extends K ? '*' : K>>;
          }[keyof T & string]
        : never;

// A path as the reader matches it: `*` matches every key.
type PathPattern<Path extends string> = Path extends `${infer Head}*${infer Tail}`
  ? `${Head}${string}${PathPattern<Tail>}`
  : Path;

// `FieldValues`, unless a field-value path matches one of `Numbers` or one of them matches
// it, which the reader would then read exactly: the result then names that path instead,
// so the path list below fails tsc.
type Unambiguous<FieldValues extends string, Numbers extends string> = [
  Extract<Numbers, PathPattern<FieldValues>>,
] extends [never]
  ? [Extract<FieldValues, PathPattern<Numbers>>] extends [never]
    ? FieldValues
    : `${Extract<FieldValues, PathPattern<Numbers>>} is both a field value and another number`
  : `${Extract<Numbers, PathPattern<FieldValues>>} is both a field value and another number`;

type FieldValuePath = Unambiguous<PathsOf<ResponseBody, 'fieldValue'>, PathsOf<ResponseBody, 'number'>>;

// Where a response body holds field values: members by name joined by `.`, an array's items
// as `[]` after it, and the entries of a map keyed by field name as `*`. Typed by the
// response types, so tsc fails when an entry is missing, extra, or matches another number.
const FIELD_VALUE_PATHS: Readonly<Record<FieldValuePath, true>> = {
  // RecordDto
  'values.*': true,
  // RecordPage and SearchResponse items
  'items[].values.*': true,
  // FieldEventResponse
  'assigned.*': true,
};

// One position of FIELD_VALUE_PATHS: where its named members, its entries and its items
// lead, and whether a number there is a field value.
interface JsonPath {
  readonly members: Map<string, JsonPath>;
  entries: JsonPath | undefined;
  items: JsonPath | undefined;
  fieldValue: boolean;
}

function jsonPath(): JsonPath {
  return { members: new Map(), entries: undefined, items: undefined, fieldValue: false };
}

// The positions of `paths`, from the body itself down.
function jsonPaths(paths: readonly string[]): JsonPath {
  const body = jsonPath();
  for (const path of paths) {
    let at = body;
    // 'items[].values.*' -> items, [], values, *
    for (const segment of path.split(/\.|(?=\[\])/)) {
      if (segment === '[]') {
        at = at.items ??= jsonPath();
      } else if (segment === '*') {
        at = at.entries ??= jsonPath();
      } else {
        const member = at.members.get(segment) ?? jsonPath();
        at.members.set(segment, member);
        at = member;
      }
    }
    at.fieldValue = true;
  }
  return body;
}

const RESPONSE_BODY: JsonPath = jsonPaths(Object.keys(FIELD_VALUE_PATHS));

interface JsonCursor {
  readonly text: string;
  at: number;
}

// The number grammar of RFC 8259, matched at the cursor.
const JSON_NUMBER = /-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?/y;

const JSON_WHITESPACE = ' \t\n\r';

function jsonSyntaxError(cursor: JsonCursor): SyntaxError {
  return new SyntaxError(`Unexpected JSON at position ${String(cursor.at)}`);
}

function skipJsonWhitespace(cursor: JsonCursor): void {
  while (cursor.at < cursor.text.length && JSON_WHITESPACE.includes(cursor.text[cursor.at])) {
    cursor.at++;
  }
}

// `path` is the value's position in FIELD_VALUE_PATHS, `undefined` outside every path.
function readJsonValue(cursor: JsonCursor, path: JsonPath | undefined): unknown {
  skipJsonWhitespace(cursor);
  switch (cursor.text[cursor.at]) {
    case '{':
      return readJsonObject(cursor, path);
    case '[':
      return readJsonArray(cursor, path?.items);
    case '"':
      return readJsonString(cursor);
    case 't':
      return readJsonLiteral(cursor, 'true', true);
    case 'f':
      return readJsonLiteral(cursor, 'false', false);
    case 'n':
      return readJsonLiteral(cursor, 'null', null);
    default:
      return readJsonNumber(cursor, path?.fieldValue === true);
  }
}

function readJsonLiteral<T>(cursor: JsonCursor, word: string, value: T): T {
  if (!cursor.text.startsWith(word, cursor.at)) {
    throw jsonSyntaxError(cursor);
  }
  cursor.at += word.length;
  return value;
}

// A field value as a number field holds it (exactFieldNumber), so the API's
// 1.0000000000000001 is an ExactDecimal; any other number as JSON.parse does, the nearest
// double, which Number() gives for every token of the grammar.
function readJsonNumber(cursor: JsonCursor, fieldValue: boolean): number | bigint | ExactDecimal {
  JSON_NUMBER.lastIndex = cursor.at;
  const match = JSON_NUMBER.exec(cursor.text);
  if (match === null) {
    throw jsonSyntaxError(cursor);
  }
  cursor.at = JSON_NUMBER.lastIndex;
  return fieldValue ? exactFieldNumber(match[0]) : Number(match[0]);
}

function readJsonString(cursor: JsonCursor): string {
  const { text } = cursor;
  const start = cursor.at;
  let at = start + 1;
  while (at < text.length && text[at] !== '"') {
    if (text.charCodeAt(at) < 0x20) {
      cursor.at = at;
      throw jsonSyntaxError(cursor);
    }
    // A backslash escapes the next character; JSON.parse checks the escape below.
    at += text[at] === '\\' ? 2 : 1;
  }
  cursor.at = at;
  if (at >= text.length) {
    throw jsonSyntaxError(cursor);
  }
  cursor.at = at + 1;

  // JSON.parse decodes the escapes of the token, and rejects a malformed one, as it would in place.
  const decoded: unknown = JSON.parse(text.slice(start, cursor.at));
  if (typeof decoded !== 'string') {
    throw jsonSyntaxError(cursor);
  }
  return decoded;
}

// `itemPath` is the position of every item in FIELD_VALUE_PATHS.
function readJsonArray(cursor: JsonCursor, itemPath: JsonPath | undefined): unknown[] {
  const items: unknown[] = [];
  cursor.at++;
  skipJsonWhitespace(cursor);
  if (cursor.text[cursor.at] === ']') {
    cursor.at++;
    return items;
  }

  for (;;) {
    items.push(readJsonValue(cursor, itemPath));
    skipJsonWhitespace(cursor);
    const next = cursor.text[cursor.at];
    if (next === ']') {
      cursor.at++;
      return items;
    }
    if (next !== ',') {
      throw jsonSyntaxError(cursor);
    }
    cursor.at++;
  }
}

function readJsonObject(cursor: JsonCursor, path: JsonPath | undefined): Record<string, unknown> {
  const object: Record<string, unknown> = {};
  cursor.at++;
  skipJsonWhitespace(cursor);
  if (cursor.text[cursor.at] === '}') {
    cursor.at++;
    return object;
  }

  for (;;) {
    skipJsonWhitespace(cursor);
    if (cursor.text[cursor.at] !== '"') {
      throw jsonSyntaxError(cursor);
    }
    const key = readJsonString(cursor);
    skipJsonWhitespace(cursor);
    if (cursor.text[cursor.at] !== ':') {
      throw jsonSyntaxError(cursor);
    }
    cursor.at++;
    // A named member before a map entry; a Map, so `__proto__` and `constructor` name no path.
    const memberPath = path === undefined ? undefined : (path.members.get(key) ?? path.entries);
    // As JSON.parse defines members: an own data property even for `__proto__`, and a
    // repeated key keeps its first place and takes its last value.
    Object.defineProperty(object, key, {
      value: readJsonValue(cursor, memberPath),
      writable: true,
      enumerable: true,
      configurable: true,
    });
    skipJsonWhitespace(cursor);
    const next = cursor.text[cursor.at];
    if (next === '}') {
      cursor.at++;
      return object;
    }
    if (next !== ',') {
      throw jsonSyntaxError(cursor);
    }
    cursor.at++;
  }
}

// JSON.parse of a response body with exact field values: the same value for the same text,
// except that a field value (FIELD_VALUE_PATHS) that is a whole number beyond the safe
// range and within the 64-bit range is the bigint it stands for, and one that is not whole
// although its nearest double is, the ExactDecimal of its token. Throws a SyntaxError for
// text that is not JSON. Nesting is recursive, which the API's shallow bodies never strain.
function parseJson(text: string): unknown {
  const cursor: JsonCursor = { text, at: 0 };
  const value = readJsonValue(cursor, RESPONSE_BODY);
  skipJsonWhitespace(cursor);
  if (cursor.at < text.length) {
    throw jsonSyntaxError(cursor);
  }
  return value;
}

// JSON.stringify of plain data (objects, arrays, strings, numbers, booleans, null), except
// that a bigint is written as the JSON integer it holds, where JSON.stringify throws, and an
// ExactDecimal as its literal, the JSON number typed, where JSON.stringify writes an object.
// Any other object is written as an object, whatever its members are named, so a `values`
// map holding text fields named `kind` and `literal` stays a map. As JSON.stringify does, an
// `undefined`, function or symbol member is left out, and such an array element is written as `null`.
function serializeJson(value: unknown): string | undefined {
  if (typeof value === 'bigint') {
    return value.toString();
  }
  if (isExactDecimal(value)) {
    return value.literal;
  }
  if (value === undefined || typeof value === 'function' || typeof value === 'symbol') {
    return undefined;
  }
  if (Array.isArray(value)) {
    return `[${value.map((item: unknown) => serializeJson(item) ?? 'null').join(',')}]`;
  }
  if (typeof value === 'object' && value !== null) {
    const members: string[] = [];
    for (const [key, member] of Object.entries(value as Record<string, unknown>)) {
      const written = serializeJson(member);
      if (written !== undefined) {
        members.push(`${JSON.stringify(key)}:${written}`);
      }
    }
    return `{${members.join(',')}}`;
  }
  // A string, number (NaN and ±Infinity as null), boolean or null.
  return JSON.stringify(value);
}

// The statuses whose response has no body by definition (the Fetch standard's null body
// statuses); a Response of one of them cannot be built with a body.
const NULL_BODY_STATUSES: ReadonlySet<number> = new Set([101, 103, 204, 205, 304]);

// A copy of `response` with `body` as its body, held whole, so it is neither encoded nor
// chunked and carries its Content-Length: decoded text, or the bytes received; with `null`,
// a copy with no body, whose Content-Length of 0 tells openapi-fetch to read none.
function copyWithBody(response: Response, body: string | ArrayBuffer | null): Response {
  let length = 0;
  if (typeof body === 'string') {
    length = new TextEncoder().encode(body).byteLength;
  } else if (body !== null) {
    length = body.byteLength;
  }
  const headers = new Headers(response.headers);
  headers.delete('Content-Encoding');
  headers.delete('Transfer-Encoding');
  headers.set('Content-Length', String(length));
  return new Response(body, { status: response.status, statusText: response.statusText, headers });
}

// The body-less copy of a response whose body cannot be used, marked for settle.
function unreadable(response: Response): Response {
  const copy = copyWithBody(response, null);
  unreadableResponses.add(copy);
  return copy;
}

// Reads every response body here, once its status has arrived, so a body that fails is
// told apart from a request that got no response; the latter alone rejects (settle).
// - A null body status has no body, whatever its headers advertise, so it gives a copy
//   with no body and a Content-Length of 0, which openapi-fetch does not read. Passed on
//   as it came, a 205 advertising a nonzero Content-Length would have openapi-fetch, which
//   skips the body only of a 204 or a Content-Length of 0, parse the missing body as JSON
//   and reject, the no-response case. The copy settles by its status alone: a 204
//   acknowledges a delete, and any other such status answers no call, so it settles as a
//   failure of that status.
// - A body whose stream fails, such as a connection cut mid-body, gives a copy marked
//   unreadable, so its status is kept, unless the request's signal is aborted: an abort
//   stays an abort. Only that signal, which follows the caller's and any deadline composed
//   into the request, tells a cancellation apart from a failed stream; the error's name
//   does not, since a stream can fail with an AbortError that no abort of the call caused.
// - An OK body is parsed by parseJson, so the field values of every OK body are read
//   exactly. Text that is not JSON, an empty body included, gives a copy marked
//   unreadable. Otherwise the copy's json() answers the parsed body: openapi-fetch reads an
//   OK body with json() when the response carries a Content-Length, which the copy does.
// - An error body holds no field values, so the copy carries the bytes received, undecoded,
//   and openapi-fetch decodes and parses it once, as it parses any error body, a non-JSON one
//   included. Decoding it here as well would drop a second leading byte order mark and could
//   turn a body that is not JSON, and so states no write outcome, into one that is.
async function readingJsonExactly(request: Request, response: Response): Promise<Response> {
  if (NULL_BODY_STATUSES.has(response.status)) {
    return copyWithBody(response, null);
  }

  let text: string | ArrayBuffer;
  try {
    text = response.ok ? await response.text() : await response.arrayBuffer();
  } catch (e) {
    if (request.signal.aborted) {
      throw e;
    }
    return unreadable(response);
  }

  if (typeof text !== 'string') {
    return copyWithBody(response, text);
  }

  let body: unknown;
  try {
    body = parseJson(text);
  } catch {
    return unreadable(response);
  }
  const copy = copyWithBody(response, text);
  Object.defineProperty(copy, 'json', { value: () => Promise.resolve(body) });
  return copy;
}

// openapi-fetch types each body by the wire schema, whose numbers are never bigints or
// ExactDecimals; serializeJson writes either as the JSON number that schema calls a
// number. Each body is first checked against its client type, which the schema generates,
// so contract drift still fails tsc.
type WireSchemas = components['schemas'];

/**
 * Builds a client for the API at `baseUrl`, by default the page's own origin: the API
 * serves the built client, and the development server proxies `/api` and `/health`, so
 * every request is same-origin.
 * @throws Error when no `baseUrl` is given outside a browser.
 */
export function createApiClient(options: ApiClientOptions = {}): ApiClient {
  // openapi-fetch captures globalThis.fetch when the client is built; resolving it per
  // call lets a fetch installed later, such as a test stub, reach the default client.
  const send = options.fetch ?? ((request: Request) => globalThis.fetch(request));
  const deadlineMs = deadlineOf(options.deadlineMs);
  const http = createClient<paths>({
    baseUrl: options.baseUrl ?? pageOrigin(),
    fetch: (request: Request) => withinDeadline(request, send, deadlineMs),
    // openapi-fetch merges these into every request; Content-Type stays on the body calls.
    headers: acceptHeaders,
    bodySerializer: serializeJson,
  });

  return {
    listForms: (signal) => withBody(signal, () => http.GET('/api/forms', abortable(signal)), isFormSummaryList),

    getForm: (viewId, signal) =>
      withBody(
        signal,
        () =>
          http.GET('/api/forms/{viewId}', {
            params: { path: { viewId } },
            ...abortable(signal),
          }),
        isFormDefinition,
      ),

    listRecords: (viewId, query, signal) =>
      withBody(
        signal,
        () =>
          http.GET('/api/forms/{viewId}/records', {
            params: { path: { viewId }, query: listQuery(query) },
            ...abortable(signal),
          }),
        isRecordPage,
      ),

    countRecords: (viewId, options, signal) =>
      withBody(
        signal,
        () =>
          http.GET('/api/forms/{viewId}/records/count', {
            params: { path: { viewId }, query: setQuery(options) },
            ...abortable(signal),
          }),
        isRecordCountResponse,
      ),

    getRecordAt: (viewId, position, options, signal) =>
      withBody(
        signal,
        () =>
          http.GET('/api/forms/{viewId}/records/at/{position}', {
            params: { path: { viewId, position }, query: setQuery(options) },
            ...abortable(signal),
          }),
        isRecordDto,
      ),

    getRecord: (viewId, globalRecordId, signal) =>
      withBody(
        signal,
        () =>
          http.GET('/api/forms/{viewId}/records/{globalRecordId}', {
            params: { path: { viewId, globalRecordId } },
            ...abortable(signal),
          }),
        isRecordDto,
      ),

    getRecordPosition: (viewId, globalRecordId, options, signal) =>
      withBody(
        signal,
        () =>
          http.GET('/api/forms/{viewId}/records/{globalRecordId}/position', {
            params: { path: { viewId, globalRecordId }, query: setQuery(options) },
            ...abortable(signal),
          }),
        isNavigationPosition,
      ),

    createRecord: (viewId, values, signal) =>
      withBody(
        signal,
        () =>
          http.POST('/api/forms/{viewId}/records', {
            params: { path: { viewId } },
            body: ({ values } satisfies RecordWriteRequest) as WireSchemas['RecordWriteRequest'],
            headers: jsonHeaders,
            ...abortable(signal),
          }),
        isRecordDto,
        201,
      ),

    updateRecord: (viewId, globalRecordId, values, signal) =>
      withBody(
        signal,
        () =>
          http.PUT('/api/forms/{viewId}/records/{globalRecordId}', {
            params: { path: { viewId, globalRecordId } },
            body: ({ values } satisfies RecordWriteRequest) as WireSchemas['RecordWriteRequest'],
            headers: jsonHeaders,
            ...abortable(signal),
          }),
        isRecordDto,
        200,
      ),

    deleteRecord: (viewId, globalRecordId, signal) =>
      withoutBody(signal, () =>
        http.DELETE('/api/forms/{viewId}/records/{globalRecordId}', {
          params: { path: { viewId, globalRecordId } },
          ...abortable(signal),
        }),
      ),

    searchRecords: (viewId, request, signal) =>
      withBody(
        signal,
        () =>
          http.POST('/api/forms/{viewId}/records/search', {
            params: { path: { viewId } },
            body: request as WireSchemas['SearchRequest'],
            headers: jsonHeaders,
            ...abortable(signal),
          }),
        isSearchResponse,
      ),

    // The body is passed on as parsed: the field-event queue reads it as `unknown` and
    // reports an answer it cannot read, so no shape is decided here.
    raiseFieldEvent: (viewId, request, signal) =>
      settle(
        signal,
        () =>
          http.POST('/api/forms/{viewId}/checkcode/field-event', {
            params: { path: { viewId } },
            body: request as WireSchemas['FieldEventRequest'],
            headers: jsonHeaders,
            ...abortable(signal),
          }),
        (data) => data,
      ),

    getHealth: (signal) => withBody(signal, () => http.GET('/health', abortable(signal)), isHealthyResponse, 200),
  };
}

/** The client of the page's own origin, used by the application shell. */
export const apiClient: ApiClient = createApiClient();
