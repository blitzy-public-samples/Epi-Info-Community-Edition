// The client's one interpreter of API error responses. client.ts turns every failed
// response into an ApiError with parseProblem, and a request that received no response
// with networkError; hooks and components read only ApiError, describeError and
// isSafeToResend, never a raw body or status code.
//
// Every /api error body is an RFC 7807 ProblemDetails (type, title, status, detail,
// traceId). A field-level 400 adds `errors`, keyed by canonical field or parameter name;
// the 500 and 503 of a record create, update or delete add `writeOutcome`. Parsing reads
// the body as `unknown` through type guards only, so a non-JSON or unexpected body (the
// /health 503, a proxy's HTML page) degrades to nulls rather than to a wrong type.
//
// Banner text is fixed: it is never built from a submitted value or anything else the
// client sent, only from the fixed texts below, the server's own fixed request-level
// `detail` and the server's trace id.
import type { WriteOutcome } from './types';

/** What a failed request means to the user, decided by its HTTP status. */
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
  /** The ProblemDetails `title`, or `null` when absent, blank or not a string. */
  readonly title: string | null;
  /** The ProblemDetails `detail`, or `null` when absent, blank or not a string. */
  readonly detail: string | null;
  /** The server's trace id for the failed request, or `null` when absent. */
  readonly traceId: string | null;
  /**
   * Field-level 400 messages keyed exactly as the server sent them (canonical field
   * name, or parameter name for route and query input); empty otherwise. The map has
   * no prototype, so a field named like an Object member (`constructor`) reads only
   * its own messages.
   */
  readonly fieldErrors: Record<string, string[]>;
  /**
   * What a failed record write is known to have done; `null` when the body carries no
   * recognised `writeOutcome`, which is treated as unsafe to resend.
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
  tooLarge: 'Request too large.',
  unsupportedMediaType: 'The request format is not supported.',
  server: 'An unexpected error occurred.',
  /** Appended to the `server` text when the response carried a trace id. */
  traceIdSuffix: (traceId: string): string => ` Trace id: ${traceId}.`,
  /** A 503 where a draft exists: a failed save keeps it on screen. */
  unavailable: 'The database cannot be reached. Your changes are still on screen.',
  /** No response where a draft exists: a failed save keeps it on screen. */
  network: 'The server could not be reached. Your changes are still on screen.',
  /** A 503 where no draft exists: a read, or a delete whose confirmation discarded the draft. */
  unavailableWithoutDraft: 'The database cannot be reached.',
  /** No response where no draft exists: a read, or a delete whose confirmation discarded the draft. */
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

/**
 * Parses a failed response.
 * @param status The response's HTTP status; the body's own `status` member is ignored.
 * @param body The parsed JSON body, the raw text of a non-JSON body, or `undefined` for
 *   an empty one.
 */
export function parseProblem(status: number, body: unknown): ApiError {
  const problem: Record<string, unknown> = isRecord(body) ? body : {};
  const fieldErrors = readFieldErrors(problem.errors);

  return {
    kind: kindOf(status, Object.keys(fieldErrors).length > 0),
    status,
    title: readText(problem.title),
    detail: readText(problem.detail),
    traceId: readText(problem.traceId),
    fieldErrors,
    writeOutcome: isWriteOutcome(problem.writeOutcome) ? problem.writeOutcome : null,
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
