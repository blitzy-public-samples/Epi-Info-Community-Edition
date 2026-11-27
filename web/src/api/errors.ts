// The client's one interpreter of API error responses. client.ts turns every failed
// response into an ApiError with parseProblem, and a request that received no response
// with networkError; hooks and components read only ApiError, describeError and
// isSafeToResend, never a raw body or status code.
//
// Every /api error body is an RFC 7807 ProblemDetails (type, title, status, detail,
// traceId). A field-level 400 adds `errors`, keyed by canonical field or parameter name;
// the 500 and 503 of a record create, update or delete add `writeOutcome`, which is read
// only from such a reply received directly, never through a redirect, and from no other
// response. Parsing reads the body as `unknown` through type guards only, so a non-JSON
// or unexpected body (the /health 503, a proxy's HTML page) degrades to nulls rather than
// to a wrong type.
//
// Banner text is fixed: it is never built from a submitted value or anything else the
// client sent, only from the fixed texts below, the server's own fixed request-level
// `detail` and the server's trace id. The one addition is a save too large to send, whose
// text may name a field by its prompt: form metadata, never the field's value.
import type { WriteOutcome } from './types';

/**
 * What a failed request means to the user, decided by its HTTP status, `network` when no
 * response arrived. A 400 alone also reads its body: it is `validation` when the body's
 * field-level `errors` map holds a message, and `badRequest` otherwise.
 */
export type ApiErrorKind =
  | 'validation'
  | 'badRequest'
  | 'forbidden'
  | 'notFound'
  | 'conflict'
  | 'tooLarge'
  | 'unsupportedMediaType'
  | 'server'
  | 'unavailable'
  | 'network';

/** The HTTP methods the client sends; the Retry rule depends on the method. */
export type ApiMethod = 'GET' | 'POST' | 'PUT' | 'DELETE';

/** The write a 403 denied, which completes the permission banner. */
export type ErrorAction = 'create' | 'update' | 'delete';

/** A failed request, parsed once by this module and read everywhere else. */
export interface ApiError {
  readonly kind: ApiErrorKind;
  /** The response's HTTP status; `null` when no response was received. */
  readonly status: number | null;
  /**
   * The ProblemDetails `type` URI reference, or `null` when absent, blank or not a string,
   * and when the body is no ProblemDetails: one without a string `title` and a `status`
   * equal to the HTTP status, such as a proxy's own JSON error envelope, the /health body
   * or no response at all. `kind` never depends on it, so a body without one is
   * classified like any other.
   */
  readonly type: string | null;
  /** The ProblemDetails `title`, or `null` when absent, blank or not a string. */
  readonly title: string | null;
  /** The ProblemDetails `detail`, or `null` when absent, blank or not a string. */
  readonly detail: string | null;
  /** The server's trace id for the failed request, or `null` when absent. */
  readonly traceId: string | null;
  /**
   * The string messages of the body's `errors` map, read from any object body and keyed
   * exactly as the server sent them (canonical field name, or parameter name for route and
   * query input); empty when the body has no such map or none of its entries holds a
   * string message. Only a 400 with a non-empty map is `validation`. The map has no
   * prototype, so a field named like an Object member (`constructor`) reads only its own
   * messages.
   */
  readonly fieldErrors: Record<string, string[]>;
  /**
   * What a failed record write is known to have done; `null`, which is treated as unsafe
   * to resend, unless the response is the direct reply to a record create, update or
   * delete, not one reached through a redirect, and is a WriteProblemDetails carrying a
   * recognised `writeOutcome`: a 500 or 503 served as `application/problem+json` whose body
   * is a ProblemDetails for that status with string `type`, `detail` and `traceId`.
   */
  readonly writeOutcome: WriteOutcome | null;
}

/** The one source of banner wording; tests compare against these texts. */
export const errorMessages = {
  validation: 'Some values need attention. See the messages beside each field.',
  badRequest: 'The request could not be processed.',
  forbidden: {
    create: 'You do not have permission to create records.',
    update: 'You do not have permission to save changes to this record.',
    delete: 'You do not have permission to delete this record.',
    default: 'You do not have permission to make this change.',
  },
  notFound: 'This record no longer exists.',
  conflict: 'This record has been deleted and is read-only.',
  /** A save refused with 409: the record was deleted meanwhile, so the edits on screen were never stored. */
  saveConflict: 'This record has been deleted and is read-only. Your unsaved changes were not saved.',
  /** Completes a failed save's text when the save was leaving the record: the user stays on it. */
  leaveCancelled: 'Leaving this record was cancelled.',
  tooLarge: 'Request too large.',
  /**
   * A save refused with 413: its body exceeded the API's 1,048,576-byte limit, and the draft
   * stays on screen. `fieldLabel` names the field holding the most text in the refused body
   * by its prompt (its name when the prompt is blank), or is `null` when it holds no text.
   */
  saveTooLarge: (fieldLabel: string | null): string =>
    'Request too large: a save can send at most 1 MB, and this one is larger. ' +
    (fieldLabel === null
      ? 'Shorten long text, then save again.'
      : `Shorten long text, starting with ${fieldLabel}, then save again.`) +
    ' Your changes are still on screen.',
  unsupportedMediaType: 'The request format is not supported.',
  server: 'An unexpected error occurred.',
  /** Appended to the `server` text when the response carried a trace id. */
  traceIdSuffix: (traceId: string): string => ` Trace id: ${traceId}.`,
  /** Completes a failed save's text that does not already say the draft is kept. */
  draftKept: 'Your changes are still on screen.',
  /**
   * A save the server failed unexpectedly and reported `notWritten`: the draft stays for
   * Retry. The text holds no trace id, which the banner shows on a line of its own.
   */
  serverWithDraft: 'An unexpected error occurred. Your changes are still on screen.',
  /**
   * A POST or PUT not proven `notWritten`: the save may have been applied. describeUncertainWrite
   * puts the cause before it.
   */
  uncertainWrite:
    'The save may already have been applied, and the record list cannot prove whether it was. ' +
    'Your changes are still on screen.',
  /** The risk of resending a create whose outcome is unknown; it follows `uncertainWrite`. */
  duplicateRisk: 'Saving again may create a duplicate record.',
  /** A 503 where a draft exists: a failed save keeps it on screen. */
  unavailable: 'The database cannot be reached. Your changes are still on screen.',
  /** No response where a draft exists: a failed save keeps it on screen. */
  network: 'The server could not be reached. Your changes are still on screen.',
  /**
   * A 503 where no draft exists: a read, or a delete whose confirmation discarded the draft.
   * Also the cause describeUncertainWrite names first.
   */
  unavailableWithoutDraft: 'The database cannot be reached.',
  /**
   * No response where no draft exists: a read, or a delete whose confirmation discarded the
   * draft. Also the cause describeUncertainWrite names first.
   */
  networkWithoutDraft: 'The server could not be reached.',
} as const;

// A writeOutcome outside this list (a newer server's value, a typo) parses as null, which
// isSafeToResend treats as unsafe, so an unrecognised outcome can never trigger a resend.
const WRITE_OUTCOMES = ['notWritten', 'unknown', 'written'] as const satisfies readonly WriteOutcome[];

function isWriteOutcome(value: unknown): value is WriteOutcome {
  return WRITE_OUTCOMES.some((outcome) => outcome === value);
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function isString(value: unknown): value is string {
  return typeof value === 'string';
}

function readText(value: unknown): string | null {
  return isString(value) && value.trim() !== '' ? value : null;
}

// Whether `problem` is a ProblemDetails answering the response with `status`, so that its
// `type` names the problem rather than a foreign error envelope's own code (a proxy's
// {"type":"gateway_error", ...}). Every /api error body the server writes carries a string
// `title` and an integer `status` beside `type`, and its `status` is the HTTP status, as
// RFC 9457 §3.1.2 asks; a body lacking either is not one of them.
function isProblemDetailsFor(problem: Record<string, unknown>, status: number): boolean {
  return isString(problem.title) && Number.isInteger(problem.status) && problem.status === status;
}

// The statuses and media type of the one response that states a write outcome: the
// WriteProblemDetails of a record create, update or delete that failed.
const WRITE_PROBLEM_STATUSES: ReadonlySet<number> = new Set([500, 503]);
const PROBLEM_MEDIA_TYPE = 'application/problem+json';

// The media type of a Content-Type value: the text before its parameters, lower-cased.
function mediaTypeOf(contentType: string): string {
  const parameters = contentType.indexOf(';');
  return (parameters === -1 ? contentType : contentType.slice(0, parameters)).trim().toLowerCase();
}

// Whether `problem` is a WriteProblemDetails answering the response with `status`: a 500 or
// 503 served as `application/problem+json` whose body is a ProblemDetails for that status
// with every member the schema requires. Only such a body's `writeOutcome` is trusted. An
// intermediary's 502 or 504, a 500 or 503 another server wrote, or a body missing a member
// states nothing about the write, whatever `writeOutcome` it carries, since `notWritten`
// read from it could offer Retry for a save the API committed.
function isWriteProblemFor(problem: Record<string, unknown>, status: number, contentType: string | null): boolean {
  return (
    WRITE_PROBLEM_STATUSES.has(status) &&
    isString(contentType) &&
    mediaTypeOf(contentType) === PROBLEM_MEDIA_TYPE &&
    isProblemDetailsFor(problem, status) &&
    isString(problem.type) &&
    isString(problem.detail) &&
    isString(problem.traceId)
  );
}

// Null prototype: a parsed key such as "__proto__" or "constructor" becomes an own
// entry, never an inherited member or a prototype change.
function emptyFieldErrors(): Record<string, string[]> {
  return Object.create(null) as Record<string, string[]>;
}

function readFieldErrors(errors: unknown): Record<string, string[]> {
  const result = emptyFieldErrors();
  if (!isRecord(errors)) {
    return result;
  }

  for (const [name, value] of Object.entries(errors)) {
    let messages: string[] = [];
    if (isString(value)) {
      messages = [value];
    } else if (Array.isArray(value)) {
      const items: readonly unknown[] = value;
      messages = items.filter(isString);
    }

    if (messages.length > 0) {
      result[name] = messages;
    }
  }

  return result;
}

function kindOf(status: number, hasFieldErrors: boolean): ApiErrorKind {
  switch (status) {
    case 400:
      return hasFieldErrors ? 'validation' : 'badRequest';
    case 403:
      return 'forbidden';
    case 404:
      return 'notFound';
    case 409:
      return 'conflict';
    case 413:
      return 'tooLarge';
    case 415:
      return 'unsupportedMediaType';
    // 502 and 504 come from the development proxy when the API is not answering.
    case 502:
    case 503:
    case 504:
      return 'unavailable';
    default:
      break;
  }

  if (status >= 400 && status <= 499) {
    return 'badRequest';
  }

  // Any other 5xx, and a non-error status reported as a failure (an OK response whose
  // JSON body is missing), is unexpected.
  return 'server';
}

/** What parseProblem needs of the direct reply to a record create, update or delete. */
export interface WriteReply {
  /**
   * The response's Content-Type header, `null` when it has none. Its parameters, such as
   * `charset`, are ignored.
   */
  readonly contentType: string | null;
}

/**
 * Parses a failed response. The HTTP status decides `kind`, with one branch on the body:
 * a 400 is `validation` when the body's `errors` map holds a message, and `badRequest`
 * otherwise. The body's own `status` member serves only to recognise a ProblemDetails:
 * `type` is kept only from a body that has a string `title` and whose integer `status` is
 * the HTTP status. `title`, `detail`, `traceId` and `errors` are read from any object
 * body. `writeOutcome` is read only when `writeReply` is given and the response is a
 * WriteProblemDetails, a 500 or 503 served as `application/problem+json` whose body is a
 * ProblemDetails for that status with string `type`, `detail` and `traceId`; otherwise it
 * is `null`.
 * @param status The response's HTTP status.
 * @param body The parsed JSON body, the raw text of a non-JSON body, or `undefined` for
 *   an empty one.
 * @param writeReply Given only for the direct reply to a record create, update or delete;
 *   omitted for every other response, a reply reached through a redirect included, since
 *   such a response states nothing about a write.
 */
export function parseProblem(status: number, body: unknown, writeReply?: WriteReply): ApiError {
  const problem: Record<string, unknown> = isRecord(body) ? body : {};
  const fieldErrors = readFieldErrors(problem.errors);

  return {
    kind: kindOf(status, Object.keys(fieldErrors).length > 0),
    status,
    type: isProblemDetailsFor(problem, status) ? readText(problem.type) : null,
    title: readText(problem.title),
    detail: readText(problem.detail),
    traceId: readText(problem.traceId),
    fieldErrors,
    writeOutcome:
      writeReply !== undefined &&
      isWriteProblemFor(problem, status, writeReply.contentType) &&
      isWriteOutcome(problem.writeOutcome)
        ? problem.writeOutcome
        : null,
  };
}

/**
 * The error of a request that received no response (a network failure, or a create
 * whose acknowledgement never arrived), so the outcome of a write is unknown.
 */
export function networkError(): ApiError {
  return {
    kind: 'network',
    status: null,
    type: null,
    title: null,
    detail: null,
    traceId: null,
    fieldErrors: emptyFieldErrors(),
    writeOutcome: null,
  };
}

/**
 * Whether `e` is the rejection of an aborted request. Matched by name, because Node's
 * and jsdom's `DOMException` are different classes under the test environment.
 */
export function isAbortError(e: unknown): boolean {
  return typeof e === 'object' && e !== null && 'name' in e && e.name === 'AbortError';
}

/**
 * Whether the API or its database could not be reached: a 503 (or the development
 * proxy's 502 or 504), or no response at all. Consumers show such a failure with the
 * ConnectionError banner, whose Retry follows isSafeToResend.
 */
export function isConnectionFailure(error: ApiError): boolean {
  return error.kind === 'unavailable' || error.kind === 'network';
}

/**
 * Whether Retry may resend the failed request without asking the user. `GET` and
 * `DELETE` repeat safely: deleting an already-deleted record returns 204 and writes
 * nothing. A `POST` or `PUT` is resent only when the server states that nothing was
 * written; `unknown`, `written`, a missing or unrecognised outcome, and no response at
 * all may mean the save was applied, so the editor offers "Save again" only as an
 * explicit action instead.
 */
export function isSafeToResend(error: ApiError, method: ApiMethod): boolean {
  switch (method) {
    case 'GET':
    case 'DELETE':
      return true;
    case 'POST':
    case 'PUT':
      return error.writeOutcome === 'notWritten';
  }
}

/**
 * The text of a POST or PUT that may have been applied: what failed, when that is known,
 * then that the save may have been applied while the draft stays on screen, and for a
 * create the risk of a duplicate record. The trace id is not included; the banner shows it
 * on a line of its own.
 * @param isCreate The failed write was the first POST of a new record.
 */
export function describeUncertainWrite(error: ApiError, isCreate: boolean): string {
  const parts: string[] = [];
  switch (error.kind) {
    case 'unavailable':
      parts.push(errorMessages.unavailableWithoutDraft);
      break;
    case 'network':
      parts.push(errorMessages.networkWithoutDraft);
      break;
    case 'server':
      parts.push(errorMessages.server);
      break;
    default:
      break;
  }
  parts.push(errorMessages.uncertainWrite);
  if (isCreate) {
    parts.push(errorMessages.duplicateRisk);
  }
  return parts.join(' ');
}

/**
 * The banner text for `error`. Field-level messages are not included: the field
 * components and the validation summary show `fieldErrors` themselves.
 * @param action The write a 403 denied; without it the permission banner is generic.
 */
export function describeError(error: ApiError, action?: ErrorAction): string {
  switch (error.kind) {
    case 'validation':
      return errorMessages.validation;
    case 'badRequest':
      // A request-level detail is fixed server text such as "the record has no values".
      return error.detail ?? errorMessages.badRequest;
    case 'forbidden':
      return errorMessages.forbidden[action ?? 'default'];
    case 'notFound':
      return errorMessages.notFound;
    case 'conflict':
      return errorMessages.conflict;
    case 'tooLarge':
      return errorMessages.tooLarge;
    case 'unsupportedMediaType':
      return errorMessages.unsupportedMediaType;
    case 'server':
      return error.traceId === null
        ? errorMessages.server
        : errorMessages.server + errorMessages.traceIdSuffix(error.traceId);
    case 'unavailable':
      return errorMessages.unavailable;
    case 'network':
      return errorMessages.network;
  }
}
