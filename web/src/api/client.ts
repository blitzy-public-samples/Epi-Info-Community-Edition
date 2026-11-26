// The client's one way to reach the API. Hooks and components call an ApiClient from this
// module; nothing else in web/src builds an API URL, calls fetch or imports openapi-fetch.
// Paths, parameters and bodies are typed by the generated `paths` of ./schema, so a call
// that no longer matches the OpenAPI document fails `tsc`. Every call settles into an
// ApiResult here, and every failure is parsed by ./errors.
//
// The client is stateless: no retries, caching or timers. Retry decisions belong to
// isSafeToResend and the hooks, and values are sent exactly as the caller gives them,
// because the editor alone decides which fields a save carries.
import createClient from 'openapi-fetch';

import { isAbortError, networkError, parseProblem, type ApiError } from './errors';
import type { paths } from './schema';
import type {
  FieldEventRequest,
  FieldEventResponse,
  FormDefinition,
  FormSummary,
  HealthResponse,
  NavigationPosition,
  RecordCountResponse,
  RecordDto,
  RecordPage,
  SearchRequest,
  SearchResponse,
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

  /** `POST /api/forms/{viewId}/records`: creates a record; succeeds with status 201. */
  readonly createRecord: (viewId: number, values: Values, signal?: AbortSignal) => Promise<ApiResult<RecordDto>>;

  /** `PUT /api/forms/{viewId}/records/{globalRecordId}`: saves the submitted fields only; 409 for a deleted record. */
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
// `undefined` when the body the endpoint must return is missing.
async function settle<D, T>(
  signal: AbortSignal | undefined,
  send: () => Promise<Exchange<D>>,
  read: (data: D | undefined) => T | undefined,
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

  const value = read(data);
  if (value === undefined) {
    return { ok: false, error: parseProblem(response.status, undefined) };
  }

  return { ok: true, data: value, status: response.status };
}

/** Settles a call whose success carries a JSON body. */
function withBody<D>(signal: AbortSignal | undefined, send: () => Promise<Exchange<D>>): Promise<ApiResult<D>> {
  return settle(signal, send, (data) => data);
}

/** Settles a call whose success carries no body (204). */
function withoutBody(signal: AbortSignal | undefined, send: () => Promise<Exchange<unknown>>): Promise<ApiResult<null>> {
  return settle(signal, send, () => null);
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

/**
 * Builds a client for the API at `baseUrl`, by default the page's own origin: the API
 * serves the built client, and the development server proxies `/api` and `/health`, so
 * every request is same-origin.
 * @throws Error when no `baseUrl` is given outside a browser.
 */
export function createApiClient(options: ApiClientOptions = {}): ApiClient {
  const http = createClient<paths>({
    baseUrl: options.baseUrl ?? pageOrigin(),
    // openapi-fetch captures globalThis.fetch when the client is built; resolving it per
    // call lets a fetch installed later, such as a test stub, reach the default client.
    fetch: options.fetch ?? ((request: Request) => globalThis.fetch(request)),
  });

  return {
    listForms: (signal) => withBody(signal, () => http.GET('/api/forms', abortable(signal))),

    getForm: (viewId, signal) =>
      withBody(signal, () =>
        http.GET('/api/forms/{viewId}', {
          params: { path: { viewId } },
          ...abortable(signal),
        }),
      ),

    listRecords: (viewId, query, signal) =>
      withBody(signal, () =>
        http.GET('/api/forms/{viewId}/records', {
          params: { path: { viewId }, query: listQuery(query) },
          ...abortable(signal),
        }),
      ),

    countRecords: (viewId, options, signal) =>
      withBody(signal, () =>
        http.GET('/api/forms/{viewId}/records/count', {
          params: { path: { viewId }, query: setQuery(options) },
          ...abortable(signal),
        }),
      ),

    getRecordAt: (viewId, position, options, signal) =>
      withBody(signal, () =>
        http.GET('/api/forms/{viewId}/records/at/{position}', {
          params: { path: { viewId, position }, query: setQuery(options) },
          ...abortable(signal),
        }),
      ),

    getRecord: (viewId, globalRecordId, signal) =>
      withBody(signal, () =>
        http.GET('/api/forms/{viewId}/records/{globalRecordId}', {
          params: { path: { viewId, globalRecordId } },
          ...abortable(signal),
        }),
      ),

    getRecordPosition: (viewId, globalRecordId, options, signal) =>
      withBody(signal, () =>
        http.GET('/api/forms/{viewId}/records/{globalRecordId}/position', {
          params: { path: { viewId, globalRecordId }, query: setQuery(options) },
          ...abortable(signal),
        }),
      ),

    createRecord: (viewId, values, signal) =>
      withBody(signal, () =>
        http.POST('/api/forms/{viewId}/records', {
          params: { path: { viewId } },
          body: { values },
          headers: jsonHeaders,
          ...abortable(signal),
        }),
      ),

    updateRecord: (viewId, globalRecordId, values, signal) =>
      withBody(signal, () =>
        http.PUT('/api/forms/{viewId}/records/{globalRecordId}', {
          params: { path: { viewId, globalRecordId } },
          body: { values },
          headers: jsonHeaders,
          ...abortable(signal),
        }),
      ),

    deleteRecord: (viewId, globalRecordId, signal) =>
      withoutBody(signal, () =>
        http.DELETE('/api/forms/{viewId}/records/{globalRecordId}', {
          params: { path: { viewId, globalRecordId } },
          ...abortable(signal),
        }),
      ),

    searchRecords: (viewId, request, signal) =>
      withBody(signal, () =>
        http.POST('/api/forms/{viewId}/records/search', {
          params: { path: { viewId } },
          body: request,
          headers: jsonHeaders,
          ...abortable(signal),
        }),
      ),

    raiseFieldEvent: (viewId, request, signal) =>
      withBody(signal, () =>
        http.POST('/api/forms/{viewId}/checkcode/field-event', {
          params: { path: { viewId } },
          body: request,
          headers: jsonHeaders,
          ...abortable(signal),
        }),
      ),

    getHealth: (signal) => withBody(signal, () => http.GET('/health', abortable(signal))),
  };
}

/** The client of the page's own origin, used by the application shell. */
export const apiClient: ApiClient = createApiClient();
