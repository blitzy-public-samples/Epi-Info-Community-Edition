// The client's one way to reach the API. Hooks and components call an ApiClient from this
// module; nothing else in web/src builds an API URL, calls fetch or imports openapi-fetch.
// Paths, parameters and bodies are typed by the generated `paths` of ./schema, so a call
// that no longer matches the OpenAPI document fails `tsc`. Every call settles into an
// ApiResult here, and every failure is parsed by ./errors.
//
// A success body is checked against its contract shape before it is reported: callers
// receive only well-formed data, and an OK response whose body is malformed settles as a
// failure, as does a record create or update answered by any status but its contracted
// 201 or 200. For a POST or PUT that failure states no outcome, so the save may have been
// applied and is never resent without the user's explicit consent.
//
// The client is stateless: no retries, caching or timers. Retry decisions belong to
// isSafeToResend and the hooks, and values are sent exactly as the caller gives them,
// because the editor alone decides which fields a save carries.
//
// Field values are exact in both directions: a field value that is a JSON integer beyond
// ±Number.MAX_SAFE_INTEGER that a `bigint` column can hold, such as 9007199254740993, is
// read as a bigint rather than rounded to the nearest double, and a bigint is written as
// that JSON integer, digit for digit. Every other number, such as a `tabIndex` or a
// `count`, is read as JSON.parse reads it. A number typed as a fraction whose nearest
// double is whole, such as 9007199254740992.5, is an ExactDecimal and is written as the
// literal typed, never as that whole double. The wire keeps plain JSON numbers.
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
  FieldValidation,
  FieldValue,
  FormDefinition,
  FormSummary,
  HealthResponse,
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
}

/** Query of the records list; an absent member takes the server default. */
export type RecordListQuery = NonNullable<paths['/api/forms/{viewId}/records']['get']['parameters']['query']>;

/** Selects the record set; without `includeDeleted` the server uses the active set. */
export type RecordSetOptions = NonNullable<paths['/api/forms/{viewId}/records/count']['get']['parameters']['query']>;

/**
 * Typed calls of every API endpoint. The last parameter of each is an optional
 * `AbortSignal`; an aborted call rejects with an AbortError, which callers recognise with
 * `isAbortError`, and every other outcome resolves to an `ApiResult`.
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
   * `GET /api/forms/{viewId}/records`: one page of the grid. A blank `q` is not sent,
   * because the server treats it as no filter.
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
   * succeeds with status 204 and `data: null`, also when it was already deleted.
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

  /** `GET /health`: 200 when SQL Server answers; a 503 settles as an `unavailable` error. */
  readonly getHealth: (signal?: AbortSignal) => Promise<ApiResult<HealthResponse>>;
}

/** What an openapi-fetch call resolves to; `data` and `error` are never both set. */
interface Exchange<D> {
  readonly data?: D;
  readonly error?: unknown;
  readonly response: Response;
}

// Settles one call. `read` turns an OK response's body into the result's data, returning
// `undefined` when the body is missing or is not the shape the endpoint's contract
// returns. Such a response settles as a failure of its own status, which parseProblem
// reads as `server` with no write outcome: a read offers Retry, and a POST or PUT counts
// as possibly applied, since the server may have committed it before the body went wrong.
// With `successStatus`, an OK response of any other status settles the same way: a write
// is acknowledged only by the status its contract gives, so a 202 or another 2xx from
// something other than the API proves neither that the write was applied nor that it was not.
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
    // No response was confirmed: a network failure, or an OK response whose body could
    // not be parsed. Either way nothing is known about a write.
    return { ok: false, error: networkError() };
  }

  const { data, error, response } = exchange;
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

/** Settles a call whose success carries no body (204). */
function withoutBody(signal: AbortSignal | undefined, send: () => Promise<Exchange<unknown>>): Promise<ApiResult<null>> {
  return settle(signal, send, () => null);
}

// Success bodies. Each guard reads a parsed body as `unknown` and holds only when every
// member its schema in ./schema requires is present with the type the contract gives it,
// so no caller reads a member that a malformed body lacks. Two parts are left to their
// one reader: a field's `fieldType` is checked as a string, because FieldRenderer shows a
// field of a type it does not know as a protected placeholder, and the field-event
// response is passed on as parsed (raiseFieldEvent).

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

// A field value as parseJson reads one: a bigint is a whole number beyond the safe range
// that it kept exact (FIELD_VALUE_PATHS). No response carries an ExactDecimal, so an
// object is never a field value here.
function isFieldValue(value: unknown): value is FieldValue {
  return value === null || isString(value) || isBoolean(value) || isFiniteNumber(value) || typeof value === 'bigint';
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

function isFieldOption(body: unknown): body is FieldOption {
  return isObject(body) && isFieldValue(body.value) && isString(body.label);
}

function isFieldValidation(body: unknown): body is FieldValidation {
  return (
    isObject(body) &&
    isBoolean(body.required) &&
    isBoolean(body.readOnly) &&
    isIntegerOrNull(body.maxLength) &&
    isFieldValue(body.lower) &&
    isFieldValue(body.upper) &&
    isStringOrNull(body.pattern)
  );
}

function isFieldDefinition(body: unknown): body is FieldDefinition {
  return (
    isObject(body) &&
    isString(body.name) &&
    isString(body.fieldType) &&
    isStringOrNull(body.prompt) &&
    isFiniteNumber(body.tabIndex) &&
    isInteger(body.pageId) &&
    isBoolean(body.unsupported) &&
    isStringOrNull(body.unsupportedType) &&
    isFieldValidation(body.validation) &&
    (body.options === null || isArrayOf(body.options, isFieldOption)) &&
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

function isHealthResponse(body: unknown): body is HealthResponse {
  return isObject(body) && isString(body.status);
}

// `null` rather than an absent member, so the init also compiles under
// exactOptionalPropertyTypes; fetch treats both as "no signal".
function abortable(signal: AbortSignal | undefined): { signal: AbortSignal | null } {
  return { signal: signal ?? null };
}

// The API rejects every other media type with 415, `+json` types included.
const jsonHeaders: Record<string, string> = { 'Content-Type': 'application/json' };

function listQuery(query: RecordListQuery = {}): RecordListQuery {
  const { q, includeDeleted, page, pageSize } = query;
  return {
    ...(q !== undefined && q.trim() !== '' ? { q } : {}),
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
 *   `2.0000000000000001`, `1e-400`) is the frozen {@link ExactDecimal} of its literal,
 *   which the client writes bare, so it never reaches the server as that whole double.
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

  const value = exactNumber(trimmed);
  const literal = typeof value === 'number' && Number.isInteger(value) ? fractionLiteral(trimmed) : null;
  if (literal === null) {
    return value;
  }
  const decimal: ExactDecimal = Object.freeze({ kind: 'decimal', literal });
  return decimal;
}

/**
 * Whether `value` is an {@link ExactDecimal}: an object of kind `decimal` whose `literal` is
 * a JSON number token, so writing it bare can only ever write a number.
 */
export function isExactDecimal(value: unknown): value is ExactDecimal {
  return (
    typeof value === 'object' &&
    value !== null &&
    'kind' in value &&
    value.kind === 'decimal' &&
    'literal' in value &&
    typeof value.literal === 'string' &&
    JSON_NUMBER_TOKEN.test(value.literal)
  );
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

// A field value as exactNumber reads it; any other number as JSON.parse does, the nearest
// double, which Number() gives for every token of the grammar.
function readJsonNumber(cursor: JsonCursor, fieldValue: boolean): number | bigint {
  JSON_NUMBER.lastIndex = cursor.at;
  const match = JSON_NUMBER.exec(cursor.text);
  if (match === null) {
    throw jsonSyntaxError(cursor);
  }
  cursor.at = JSON_NUMBER.lastIndex;
  return fieldValue ? exactNumber(match[0]) : Number(match[0]);
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
// range and within the 64-bit range is the bigint it stands for. Throws a SyntaxError for
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
// As JSON.stringify does, an `undefined`, function or symbol member is left out, and such an
// array element is written as `null`.
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

// openapi-fetch reads an OK body with Response.json() when the response carries a
// Content-Length, and with text() and the global JSON.parse when it does not. The copy
// therefore always carries one, and its json() is parseJson, so the field values of every
// OK body are read exactly. Error bodies hold no field values and keep openapi-fetch's own
// parsing.
async function readingJsonExactly(response: Response): Promise<Response> {
  if (!response.ok || response.status === 204 || response.status === 205) {
    return response;
  }

  const text = await response.text();
  // The copy holds the decoded text whole, so it is neither encoded nor chunked.
  const headers = new Headers(response.headers);
  headers.delete('Content-Encoding');
  headers.delete('Transfer-Encoding');
  headers.set('Content-Length', String(new TextEncoder().encode(text).byteLength));
  const copy = new Response(text, { status: response.status, statusText: response.statusText, headers });
  Object.defineProperty(copy, 'json', { value: () => Promise.resolve(text).then(parseJson) });
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
  const http = createClient<paths>({
    baseUrl: options.baseUrl ?? pageOrigin(),
    fetch: async (request: Request) => readingJsonExactly(await send(request)),
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

    getHealth: (signal) => withBody(signal, () => http.GET('/health', abortable(signal)), isHealthResponse),
  };
}

/** The client of the page's own origin, used by the application shell. */
export const apiClient: ApiClient = createApiClient();
