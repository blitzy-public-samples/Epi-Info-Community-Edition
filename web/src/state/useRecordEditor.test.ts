// The record editor's write rules:
// - C-10: a new record whose only entries are unchecked checkboxes is empty and never
//   posted; a POST sends `false` for every unchecked writable checkbox.
// - C-3: a PUT carries the dirty writable fields only; read-only and unsupported fields
//   are never sent and cannot be edited.
// - Leaving a dirty record saves it first; a failed save, a 404 included, cancels the
//   move and keeps the draft, and Discard edits alone replaces it (0.3.6, 0.3.4.2).
// - The write-outcome table of 0.3.4.2: Retry resends a POST or PUT only on
//   `notWritten` from the API's WriteProblemDetails, a 500 or 503 served as
//   `application/problem+json` with every member its schema requires, received as the
//   direct reply to that write; `unknown`, `written`, a missing or unrecognised outcome, a
//   `notWritten` in any other response, one reached through a redirect included, and no
//   response offer Save again only, for a create and an update alike. A DELETE is always
//   safe to resend. A reply to a call that writes no record states no write outcome.
// - A write is acknowledged only by its contracted status (201 for POST, 200 for PUT)
//   with its complete Record DTO; any other 2xx offers Save again only, as no response does.
//   A DELETE is acknowledged by its bodyless 204 alone; any other 2xx offers Retry.
// - A response whose body cannot be read, cut off or not JSON, is a failure of the status
//   it carried, never the no-response case, and states no write outcome.
// - Every request is sent same-origin with redirect 'error', and a redirect, followed or
//   not, is the no-response case, whatever it leads to (0.3.7).
// - C-6: Delete asks first; Cancel sends nothing, Confirm sends exactly one DELETE.
// - P2 permissions (0.2.4): a save the definition's flags deny sends no request; a known
//   denial or a 403 keeps the draft and offers Discard edits, which reverts it in place.
// - Exact numbers: a field value keeps what a double would lose. An integer beyond
//   ±Number.MAX_SAFE_INTEGER that a `bigint` column admits is its bigint, saved digit for
//   digit; a number that is not whole although its nearest double is, such as
//   9007199254740992.5 or 2.0000000000000001, typed or read, is an ExactDecimal, saved as its
//   JSON number token. Everything else, every other number included, such as a `tabIndex`,
//   `uniqueKey` or `count`, is read as JSON.parse reads it. A `values` map is written as a
//   map whatever its fields are named, `kind` and `literal` included. Client feedback compares a
//   number with its bounds only where the server compares alike on every column, and
//   leaves the rest to its 400.
//
// The hook runs against the real client and error parsing; createApiClient's fetch seam
// is a fake that records every request and answers from a per-test reply queue.
import { act, renderHook } from '@testing-library/react';
import { useState } from 'react';
import { afterEach, describe, expect, it, vi, type Mock } from 'vitest';

import { createApiClient, isExactDecimal, numberFromText, type ApiClient, type ApiResult } from '../api/client';
import { errorMessages, isAbortError, isSafeToResend, type ApiError, type ApiErrorKind } from '../api/errors';
import type {
  FieldDefinition,
  FieldEventResponse,
  FieldValidation,
  FieldValue,
  FormDefinition,
  RecordDto,
  Values,
  WriteOutcome,
} from '../api/types';
import {
  allFields,
  createFormDefinitionFixture,
  emptyValues,
  fieldByName,
  formDefinitionFixture,
  isDataField,
  readOnlyFieldNames,
  recordFixture,
  withField,
  writableCheckboxNames,
} from '../test/formDefinitionFixture';
import { clientMessages, validateField } from '../validation/clientValidation';
import { useRecordEditor, type EditorBanner, type RecordEditor, type UseRecordEditorOptions } from './useRecordEditor';

const VIEW_ID = formDefinitionFixture.viewId;
const RECORDS_PATH = `/api/forms/${VIEW_ID}/records`;
const NEW_RECORD_ID = '3f2b8c1e-5d4a-4c1b-9e7f-000000000004';
const TRACE_ID = '00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-00';

// Banner wording fixed by the error table of 0.3.4.2, matched independently of the client's
// own texts. Wording the plan leaves open is asserted by what it must say, not verbatim.
const RECORD_GONE = /this record no longer exists/i;
const MAY_ALREADY_BE_APPLIED = /may already have been applied/i;
const LIST_CANNOT_PROVE = /the record list cannot prove whether it was/i;
const DUPLICATE_RECORD = /duplicate record/i;

// Fields, chosen by their metadata so the cases hold whatever the fixture names them.

/** The fixture field's prompt, which a 413 banner names it by. */
function promptOf(name: string): string {
  const { prompt } = fieldByName(name);
  if (prompt === null || prompt.trim() === '') {
    throw new Error(`the fixture field ${name} needs a prompt`);
  }
  return prompt;
}

function isWritableDataField(field: FieldDefinition): boolean {
  return isDataField(field) && !field.validation.readOnly;
}

/** Writable text fields that no rule of the client or the server can reject. */
const PLAIN_TEXT_FIELDS: readonly string[] = allFields()
  .filter(
    (field) =>
      field.fieldType === 'text' &&
      isWritableDataField(field) &&
      !field.validation.required &&
      field.validation.maxLength === null &&
      field.validation.pattern === null,
  )
  .map((field) => field.name);

const UNSUPPORTED_FIELD_NAMES: readonly string[] = allFields()
  .filter((field) => field.fieldType === 'unsupported')
  .map((field) => field.name);

/** Fields the editor must never send or let the user change. */
const PROTECTED_FIELD_NAMES: readonly string[] = [...readOnlyFieldNames, ...UNSUPPORTED_FIELD_NAMES];

function requireNames(what: string, names: readonly string[], count: number): readonly string[] {
  if (names.length < count) {
    throw new Error(`the fixture needs at least ${count} ${what}`);
  }
  return names;
}

/** Writable number fields with no required, range or pattern rule. */
const PLAIN_NUMBER_FIELDS: readonly string[] = allFields()
  .filter(
    (field) =>
      field.fieldType === 'number' &&
      isWritableDataField(field) &&
      !field.validation.required &&
      field.validation.lower === null &&
      field.validation.upper === null &&
      field.validation.pattern === null,
  )
  .map((field) => field.name);

const [TEXT, OTHER_TEXT] = requireNames('plain writable text fields', PLAIN_TEXT_FIELDS, 2);
const [NUMBER] = requireNames('plain writable number fields', PLAIN_NUMBER_FIELDS, 1);
const [CHECKBOX] = requireNames('writable checkboxes', writableCheckboxNames, 1);
requireNames('read-only data fields', readOnlyFieldNames, 1);
requireNames('unsupported fields', UNSUPPORTED_FIELD_NAMES, 1);

const ZIP = fieldByName('Zip');
const ZIP_MAX_LENGTH = ((): number => {
  const { maxLength } = ZIP.validation;
  if (typeof maxLength !== 'number') {
    throw new Error('the fixture Zip field needs a maxLength');
  }
  return maxLength;
})();

/** `false` for every writable checkbox, as a new-record POST sends them unchecked. */
function uncheckedCheckboxes(): Values {
  return Object.fromEntries(writableCheckboxNames.map((name): [string, FieldValue] => [name, false]));
}

/** A non-null value of the field's wire type. */
function sampleValue(field: FieldDefinition): FieldValue {
  switch (field.fieldType) {
    case 'checkbox':
      return true;
    case 'number':
    case 'option':
      return 1;
    case 'date':
      return '2011-06-01';
    default:
      return 'sample';
  }
}

// Records

/** The open record of the update cases: active, with both plain text fields filled. */
function storedRecord(): RecordDto {
  return recordFixture({ values: { [TEXT]: 'Smith', [OTHER_TEXT]: 'Other' } });
}

/** The server's copy of `record` after a save of `values`. */
function savedCopy(record: RecordDto, values: Values): RecordDto {
  return { ...record, values: { ...record.values, ...values } };
}

/** The 201 DTO of a created record holding `values`. */
function createdRecord(values: Values): RecordDto {
  return recordFixture({ globalRecordId: NEW_RECORD_ID, uniqueKey: 4, position: 4, count: 4, values });
}

function recordPath(record: RecordDto): string {
  return `${RECORDS_PATH}/${record.globalRecordId}`;
}

// The fake API

interface RecordedCall {
  readonly method: string;
  readonly path: string;
  /** The parsed JSON body; `undefined` when the request carried none. */
  readonly body: unknown;
  /** The body text as sent; `null` when the request carried none. */
  readonly text: string | null;
}

/** The answer to one request: a response, or a function of the request that settles it later or rejects. */
type Reply = Response | ((request: Request) => Promise<Response>);

/** A reply held at the fake until the test releases it. */
interface Gate {
  /** Resolves once the held request has reached the fake. */
  readonly reached: Promise<void>;
  readonly release: (response: Response) => void;
}

interface FakeApi {
  readonly fetch: (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>;
  /** Every request received, in arrival order. */
  readonly calls: readonly RecordedCall[];
  /** `METHOD path` of every request, in arrival order. */
  readonly lines: () => string[];
  /** Queues the replies of the next requests, in order. */
  readonly reply: (...replies: Reply[]) => void;
  /** Queues a reply that answers only when the test releases it. */
  readonly hold: () => Gate;
  /** Requests that arrived with no reply queued. */
  readonly unanswered: readonly string[];
  /** Replies queued but never used. */
  readonly unusedReplies: () => number;
}

interface Deferred<T> {
  readonly promise: Promise<T>;
  readonly resolve: (value: T) => void;
}

function deferred<T>(): Deferred<T> {
  let resolve: ((value: T) => void) | null = null;
  const promise = new Promise<T>((settle) => {
    resolve = settle;
  });
  const settleWith = (value: T): void => {
    if (resolve === null) {
      throw new Error('deferred promise has no resolver');
    }
    resolve(value);
  };
  return { promise, resolve: settleWith };
}

function lineOf(call: RecordedCall): string {
  return `${call.method} ${call.path}`;
}

/** Every fake API of the running test, checked after it. */
const apisOfTest: FakeApi[] = [];

/** Fake APIs of tests that raise field events themselves; every other one must receive none. */
const apisRaisingEvents = new WeakSet<FakeApi>();

function createFakeApi(): FakeApi {
  const calls: RecordedCall[] = [];
  const replies: Reply[] = [];
  const unanswered: string[] = [];

  const fetchFromFake = async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
    // openapi-fetch hands over the Request it built; anything else is normalised to one.
    const request = input instanceof Request ? input : new Request(input, init);
    const text = await request.clone().text();
    const body: unknown = text === '' ? undefined : JSON.parse(text);
    const call: RecordedCall = {
      method: request.method,
      path: new URL(request.url).pathname,
      body,
      text: text === '' ? null : text,
    };
    calls.push(call);
    const next = replies.shift();
    if (next === undefined) {
      unanswered.push(lineOf(call));
      throw new Error(`no reply queued for ${lineOf(call)}`);
    }
    return next instanceof Response ? next : next(request);
  };

  const api: FakeApi = {
    fetch: fetchFromFake,
    calls,
    lines: () => calls.map(lineOf),
    reply: (...queued) => {
      replies.push(...queued);
    },
    hold: () => {
      const arrival = deferred<undefined>();
      const answer = deferred<Response>();
      replies.push(() => {
        arrival.resolve(undefined);
        return answer.promise;
      });
      return { reached: arrival.promise, release: answer.resolve };
    },
    unanswered,
    unusedReplies: () => replies.length,
  };
  apisOfTest.push(api);
  return api;
}

// Marks a bigint inside JSON.stringify's output so it can be written as a bare integer; no
// fixture string holds the mark.
const BIGINT_MARK = '\u0000bigint:';
const MARKED_BIGINT = /"\\u0000bigint:(-?\d+)"/g;

/** A record as the API writes it: a bigint is the JSON integer it holds, digit for digit. */
function jsonResponse(status: number, body: RecordDto): Response {
  const marked = JSON.stringify(body, (_key: string, value: unknown) =>
    typeof value === 'bigint' ? BIGINT_MARK + value.toString() : value,
  );
  return new Response(marked.replace(MARKED_BIGINT, '$1'), {
    status,
    headers: { 'content-type': 'application/json' },
  });
}

type ProblemStatus = 400 | 403 | 404 | 409 | 413 | 500 | 503;

const PROBLEM_TITLES: Readonly<Record<ProblemStatus, string>> = {
  400: 'Bad Request',
  403: 'Forbidden',
  404: 'Not Found',
  409: 'Conflict',
  413: 'Payload Too Large',
  500: 'Internal Server Error',
  503: 'Service Unavailable',
};

interface ProblemExtra {
  readonly detail?: string;
  readonly errors?: Readonly<Record<string, readonly string[]>>;
  /** The wire value as sent: any string, so a value outside the contract's outcomes can be served. */
  readonly writeOutcome?: string;
  /** The failed request's trace id; TRACE_ID when absent. */
  readonly traceId?: string;
  /** The response's Content-Type; `application/problem+json`, as the API serves it, when absent. */
  readonly contentType?: string;
}

/** A ProblemDetails response, with a field-level `errors` map or a `writeOutcome` when given. */
function problem(status: ProblemStatus, extra: ProblemExtra = {}): Response {
  const body = {
    type: 'about:blank',
    title: PROBLEM_TITLES[status],
    status,
    detail: extra.detail ?? 'A fixed server sentence.',
    traceId: extra.traceId ?? TRACE_ID,
    ...(extra.errors === undefined ? {} : { errors: extra.errors }),
    ...(extra.writeOutcome === undefined ? {} : { writeOutcome: extra.writeOutcome }),
  };
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': extra.contentType ?? 'application/problem+json' },
  });
}

function noContent(): Response {
  return new Response(null, { status: 204 });
}

/** A response of a null body status, which has no body, whose headers advertise a 5-byte one. */
function nullBodyAdvertisingFive(status: 204 | 205 | 304): Response {
  return new Response(null, { status, headers: { 'Content-Length': '5' } });
}

/** A request that never received a response. */
function networkFailure(): Promise<Response> {
  return Promise.reject(new TypeError('Failed to fetch'));
}

// Redirects in place of the API's answer. Each answer below would read as a proven outcome or
// a success were it taken for the API's own, so only its refusal as no response passes.

/** Where a redirect from something between the client and the API sends a request: another origin. */
const REDIRECT_TARGET = 'http://elsewhere.example/api/forms';

/** `response` as fetch reports it after following a redirect to it. */
function asRedirected(response: Response): Response {
  Object.defineProperty(response, 'redirected', { value: true });
  return response;
}

/** What fetch returns for a redirect under redirect 'manual': an opaque redirect, status 0, no body. */
function opaqueRedirect(): Response {
  const response = new Response(null, { status: 200 });
  Object.defineProperties(response, {
    type: { value: 'opaqueredirect' },
    status: { value: 0 },
    ok: { value: false },
  });
  return response;
}

/** A 307 to REDIRECT_TARGET, as a transport that follows no redirect returns it, whose body claims `notWritten`. */
function temporaryRedirect(): Response {
  return new Response(JSON.stringify({ title: 'Temporary Redirect', status: 307, writeOutcome: 'notWritten' }), {
    status: 307,
    headers: { location: REDIRECT_TARGET, 'content-type': 'application/problem+json' },
  });
}

/**
 * Fetch's handling of a 307 to REDIRECT_TARGET, by the request's redirect mode: 'error'
 * rejects with a TypeError, 'manual' gives an opaque redirect, and 'follow' gives the
 * target's notWritten 503.
 */
function fetchRedirecting(request: Request): Promise<Response> {
  switch (request.redirect) {
    case 'error':
      return Promise.reject(new TypeError('Failed to fetch'));
    case 'manual':
      return Promise.resolve(opaqueRedirect());
    case 'follow':
      return Promise.resolve(asRedirected(problem(503, { writeOutcome: 'notWritten' })));
  }
}

// Request bodies, read as `unknown` and narrowed

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function isFieldValue(value: unknown): value is FieldValue {
  return value === null || typeof value === 'string' || typeof value === 'number' || typeof value === 'boolean';
}

/** The `values` of a write body; throws unless the body is exactly `{ values: { name: scalar } }`. */
function sentValues(call: RecordedCall | undefined): Values {
  if (call === undefined) {
    throw new Error('no such request');
  }
  const { body } = call;
  if (!isObject(body) || Object.keys(body).join() !== 'values' || !isObject(body.values)) {
    throw new Error(`${lineOf(call)} carries no { values } object`);
  }
  const values: Values = {};
  for (const [name, value] of Object.entries(body.values)) {
    if (!isFieldValue(value)) {
      throw new Error(`${lineOf(call)} sends a non-scalar value for ${name}`);
    }
    values[name] = value;
  }
  return values;
}

// Mounting the hook

interface Current {
  readonly current: RecordEditor;
}

interface Mounted {
  readonly api: FakeApi;
  readonly result: Current;
  readonly props: UseRecordEditorOptions;
  readonly rerender: (props: UseRecordEditorOptions) => void;
  readonly onSaved: Mock<(record: RecordDto) => void>;
  readonly onDeleted: Mock<() => void>;
  readonly onConflict: Mock<() => void>;
  readonly onDiscardToNearest: Mock<(notice?: string) => void>;
}

interface MountedRecord extends Mounted {
  readonly record: RecordDto;
}

// The definition is fresh per test, and the same object across rerenders: a new definition starts a
// new form. Without `deadlineMs` the client has its default request deadline.
function mount(
  record: RecordDto | null,
  isNew: boolean,
  definition: FormDefinition = createFormDefinitionFixture(),
  deadlineMs?: number,
): Mounted {
  const api = createFakeApi();
  const onSaved = vi.fn<(saved: RecordDto) => void>();
  const onDeleted = vi.fn<() => void>();
  const onConflict = vi.fn<() => void>();
  const onDiscardToNearest = vi.fn<(notice?: string) => void>();
  const props: UseRecordEditorOptions = {
    client: createApiClient({ baseUrl: 'http://localhost', fetch: api.fetch, deadlineMs }),
    viewId: definition.viewId,
    definition,
    record,
    isNew,
    recordGeneration: 1,
    onSaved,
    onDeleted,
    onConflict,
    onDiscardToNearest,
  };
  const { result, rerender } = renderHook((options: UseRecordEditorOptions) => useRecordEditor(options), {
    initialProps: props,
  });
  return { api, result, props, rerender, onSaved, onDeleted, onConflict, onDiscardToNearest };
}

function mountNew(definition?: FormDefinition, deadlineMs?: number): Mounted {
  return mount(null, true, definition, deadlineMs);
}

function mountExisting(record: RecordDto = storedRecord(), definition?: FormDefinition, deadlineMs?: number): MountedRecord {
  return { ...mount(record, false, definition, deadlineMs), record };
}

/** Runs an action inside act and returns what it resolved to. */
async function perform<T>(action: () => Promise<T>): Promise<T> {
  const outcomes: T[] = [];
  await act(async () => {
    outcomes.push(await action());
  });
  if (outcomes.length !== 1) {
    throw new Error('the action did not settle inside act');
  }
  return outcomes[0];
}

/** Runs a synchronous action, such as setValue or requestDelete, inside act. */
function run(action: () => void): void {
  act(() => {
    action();
  });
}

function edit(result: Current, name: string, value: FieldValue): void {
  run(() => {
    result.current.setValue(name, value);
  });
}

interface Started<T> {
  readonly promise: Promise<T>;
  /** What the action resolved to; `undefined` while it is still pending. */
  readonly outcome: () => T | undefined;
}

/** Starts an action whose request the fake holds, and returns once the request has arrived. */
async function startHeld<T>(action: () => Promise<T>, gate: Gate): Promise<Started<T>> {
  const started: Started<T>[] = [];
  await act(async () => {
    let outcome: T | undefined;
    const promise = action().then((value) => {
      outcome = value;
      return value;
    });
    started.push({ promise, outcome: () => outcome });
    await gate.reached;
  });
  if (started.length !== 1) {
    throw new Error('the action did not start inside act');
  }
  return started[0];
}

function bannerOf(editor: RecordEditor): EditorBanner {
  if (editor.banner === null) {
    throw new Error('the editor shows no banner');
  }
  return editor.banner;
}

afterEach(() => {
  for (const api of apisOfTest.splice(0)) {
    expect(api.unanswered).toEqual([]);
    expect(api.unusedReplies()).toBe(0);
    // A test that raises no field event must not see the editor send one of its own.
    if (!apisRaisingEvents.has(api)) {
      expect(api.lines().filter((line) => line.includes('/checkcode/'))).toEqual([]);
    }
  }
});

describe('create bodies (C-10)', () => {
  it('leaves and saves an untouched new record without a request', async () => {
    const { api, result } = mountNew();

    expect(result.current.isDirty).toBe(false);
    expect(await perform(() => result.current.leave())).toBe(true);
    expect(await perform(() => result.current.save())).toBe(true);
    expect(api.calls).toEqual([]);
  });

  it('treats a new record holding only unchecked checkboxes and erased text as empty', async () => {
    const { api, result } = mountNew();
    for (const name of writableCheckboxNames) {
      edit(result, name, true);
      edit(result, name, false);
    }
    edit(result, TEXT, 'typed');
    edit(result, TEXT, '');

    expect(result.current.values[TEXT]).toBeNull();
    expect(result.current.isDirty).toBe(false);
    expect(await perform(() => result.current.save())).toBe(true);
    expect(await perform(() => result.current.leave())).toBe(true);
    expect(api.calls).toEqual([]);
    // Leaving discarded the empty draft.
    expect(result.current.values).toEqual(emptyValues());
  });

  it('posts an entered value with false for every unchecked writable checkbox and no protected field', async () => {
    const { api, result, onSaved } = mountNew();
    edit(result, TEXT, 'Smith');
    const created = createdRecord({ [TEXT]: 'Smith', ...uncheckedCheckboxes() });
    api.reply(jsonResponse(201, created));

    expect(await perform(() => result.current.save())).toBe(true);

    expect(api.lines()).toEqual([`POST ${RECORDS_PATH}`]);
    const sent = sentValues(api.calls[0]);
    expect(sent).toEqual({ [TEXT]: 'Smith', ...uncheckedCheckboxes() });
    for (const name of PROTECTED_FIELD_NAMES) {
      expect(Object.hasOwn(sent, name)).toBe(false);
    }
    expect(onSaved).toHaveBeenCalledTimes(1);
    expect(onSaved).toHaveBeenCalledWith(created);
    expect(result.current.isDirty).toBe(false);
  });

  it('posts a new record whose only entry is a checked writable checkbox', async () => {
    const { api, result, onSaved } = mountNew();
    edit(result, CHECKBOX, true);
    const created = createdRecord({ ...uncheckedCheckboxes(), [CHECKBOX]: true });
    api.reply(jsonResponse(201, created));

    expect(result.current.isDirty).toBe(true);
    expect(await perform(() => result.current.leave())).toBe(true);

    expect(api.lines()).toEqual([`POST ${RECORDS_PATH}`]);
    expect(sentValues(api.calls[0])).toEqual({ ...uncheckedCheckboxes(), [CHECKBOX]: true });
    expect(onSaved).toHaveBeenCalledWith(created);
  });
});

describe('update bodies (C-3)', () => {
  it.each<[string, string, FieldValue]>([
    ['an edited text field', 'Jones', 'Jones'],
    ['a cleared text field, as an explicit null', '', null],
  ])('puts only %s', async (_change, value, sent) => {
    const { api, result, record, onSaved } = mountExisting();
    edit(result, TEXT, value);
    const saved = savedCopy(record, { [TEXT]: sent });
    api.reply(jsonResponse(200, saved));

    expect(await perform(() => result.current.save())).toBe(true);

    expect(api.lines()).toEqual([`PUT ${recordPath(record)}`]);
    expect(sentValues(api.calls[0])).toEqual({ [TEXT]: sent });
    expect(onSaved).toHaveBeenCalledWith(saved);
    expect(result.current.isDirty).toBe(false);
  });

  it('ignores edits to read-only and unsupported fields and never sends them', async () => {
    const { api, result, record } = mountExisting();
    for (const name of PROTECTED_FIELD_NAMES) {
      edit(result, name, sampleValue(fieldByName(name)));
    }

    expect(result.current.values).toEqual(record.values);
    expect(result.current.dirty.size).toBe(0);
    expect(result.current.isDirty).toBe(false);
    expect(await perform(() => result.current.save())).toBe(true);
    expect(api.calls).toEqual([]);

    edit(result, OTHER_TEXT, 'Jones');
    api.reply(jsonResponse(200, savedCopy(record, { [OTHER_TEXT]: 'Jones' })));
    expect(await perform(() => result.current.save())).toBe(true);

    const sent = sentValues(api.calls[0]);
    expect(sent).toEqual({ [OTHER_TEXT]: 'Jones' });
    for (const name of PROTECTED_FIELD_NAMES) {
      expect(Object.hasOwn(sent, name)).toBe(false);
    }
  });

  it('sends nothing for a clean record', async () => {
    const { api, result } = mountExisting();

    expect(result.current.isDirty).toBe(false);
    expect(await perform(() => result.current.save())).toBe(true);
    expect(await perform(() => result.current.leave())).toBe(true);
    expect(api.calls).toEqual([]);
  });
});

// A field the user edits back to the record's value is clean again: it leaves the dirty set,
// so no PUT rewrites the stored row and no leave prompt holds the page. While a write that may
// still reach the server, or may already have been applied, carries the field, it stays dirty,
// so the next save sends the value on screen (0.3.6 dirty tracking).
describe('an edit back to the record value', () => {
  /** Whether the browser's leave prompt would hold the page: a beforeunload listener cancels the event. */
  function promptsOnUnload(): boolean {
    const event = new Event('beforeunload', { cancelable: true });
    window.dispatchEvent(event);
    return event.defaultPrevented;
  }

  it.each<[string, string, FieldValue, FieldValue, FieldValue]>([
    ['a text field edited and typed back', TEXT, 'Smith', 'Jones', 'Smith'],
    ['a text field typed into and cleared', TEXT, null, '121', ''],
    ['a number field edited and typed back', NUMBER, 31, 32, 31],
    ['a number field typed into and cleared', NUMBER, null, 121, null],
    // An unchecked box is no value, so a box checked and unchecked again is back at its stored null.
    ['a checkbox stored as null, checked and unchecked', CHECKBOX, null, true, false],
  ])('leaves %s clean, with no leave prompt and no request on leaving', async (_case, name, stored, edited, back) => {
    const record = recordFixture({ values: { [name]: stored } });
    const { api, result } = mountExisting(record);
    edit(result, name, edited);
    expect([...result.current.dirty]).toEqual([name]);
    expect(result.current.isDirty).toBe(true);
    expect(promptsOnUnload()).toBe(true);

    edit(result, name, back);

    expect(result.current.dirty.size).toBe(0);
    expect(result.current.isDirty).toBe(false);
    expect(promptsOnUnload()).toBe(false);
    expect(await perform(() => result.current.leave())).toBe(true);
    expect(await perform(() => result.current.save())).toBe(true);
    expect(api.calls).toEqual([]);
  });

  it('keeps a field typed back during a PUT that carries it dirty for the next PUT, and drops one the PUT does not carry', async () => {
    const { api, result, record } = mountExisting();
    edit(result, TEXT, 'Jones');
    const put = api.hold();
    const saving = await startHeld(() => result.current.save(), put);

    // The PUT may store Jones, so Smith on screen still has to be saved; OTHER_TEXT is not in it.
    edit(result, TEXT, 'Smith');
    edit(result, OTHER_TEXT, 'Brown');
    edit(result, OTHER_TEXT, 'Other');
    expect([...result.current.dirty]).toEqual([TEXT]);
    await act(async () => {
      put.release(jsonResponse(200, savedCopy(record, { [TEXT]: 'Jones' })));
      await saving.promise;
    });

    expect(saving.outcome()).toBe(true);
    expect(result.current.values[TEXT]).toBe('Smith');
    expect([...result.current.dirty]).toEqual([TEXT]);
    expect(result.current.isDirty).toBe(true);

    api.reply(jsonResponse(200, savedCopy(record, { [TEXT]: 'Smith' })));
    expect(await perform(() => result.current.leave())).toBe(true);

    expect(api.calls.map(sentValues)).toEqual([{ [TEXT]: 'Jones' }, { [TEXT]: 'Smith' }]);
    expect(result.current.isDirty).toBe(false);
  });

  it('keeps a field typed back after a notWritten PUT dirty, and saves it after the PUT Retry resends', async () => {
    const { api, result, record } = mountExisting();
    edit(result, TEXT, 'Jones');
    api.reply(problem(503, { writeOutcome: 'notWritten' }));
    expect(await perform(() => result.current.save())).toBe(false);

    edit(result, TEXT, 'Smith');
    expect([...result.current.dirty]).toEqual([TEXT]);

    // Retry resends the failed PUT as it was sent; the value typed back since stays to be saved.
    api.reply(jsonResponse(200, savedCopy(record, { [TEXT]: 'Jones' })));
    await perform(() => result.current.retry());
    expect(result.current.values[TEXT]).toBe('Smith');
    expect([...result.current.dirty]).toEqual([TEXT]);

    api.reply(jsonResponse(200, savedCopy(record, { [TEXT]: 'Smith' })));
    expect(await perform(() => result.current.save())).toBe(true);

    expect(api.calls.map(sentValues)).toEqual([{ [TEXT]: 'Jones' }, { [TEXT]: 'Jones' }, { [TEXT]: 'Smith' }]);
    expect(result.current.isDirty).toBe(false);
  });

  it('keeps a field typed back after a PUT of unknown outcome dirty and blocked until Save again sends it', async () => {
    const { api, result, record } = mountExisting();
    edit(result, TEXT, 'Jones');
    api.reply(problem(503, { writeOutcome: 'unknown' }));
    expect(await perform(() => result.current.save())).toBe(false);

    // The server may hold Jones, so Smith on screen is not known to be stored.
    edit(result, TEXT, 'Smith');
    expect([...result.current.dirty]).toEqual([TEXT]);
    expect(result.current.isDirty).toBe(true);
    expect(result.current.saveBlocked).toBe(true);
    expect(await perform(() => result.current.leave())).toBe(false);
    expect(api.lines()).toEqual([`PUT ${recordPath(record)}`]);

    api.reply(jsonResponse(200, savedCopy(record, { [TEXT]: 'Smith' })));
    expect(await perform(() => result.current.saveAgain())).toBe(true);

    expect(api.calls.map(sentValues)).toEqual([{ [TEXT]: 'Jones' }, { [TEXT]: 'Smith' }]);
    expect(result.current.isDirty).toBe(false);
  });
});

// A check-code CLEAR answered before or while a write is held: the saved record is adopted
// except for a field changed after the write was sent, which stays on screen and dirty, so
// the next save sends it (0.3.6 dirty tracking). A PUT sends `null` and `false` as distinct
// values; a POST sends an unchecked checkbox, `null` included, as `false` (C-10).
describe('adopting the saved record', () => {
  const FIELD_EVENT_PATH = `/api/forms/${VIEW_ID}/checkcode/field-event`;

  /** A 200 field-event response that clears `cleared` and has no other effect. */
  function clearing(cleared: readonly string[]): Response {
    const body: FieldEventResponse = {
      enabled: [],
      disabled: [],
      cleared: [...cleared],
      assigned: {},
      dialogs: [],
      diagnostics: [],
    };
    return new Response(JSON.stringify(body), { status: 200, headers: { 'content-type': 'application/json' } });
  }

  /** Lets every pending promise callback and zero-delay timer run. */
  function tick(): Promise<void> {
    return new Promise((resolve) => {
      setTimeout(resolve, 0);
    });
  }

  /**
   * Lets the mounted editor's fake receive field events and puts the editor on a client whose
   * field-event results the returned function awaits. Called before any edit, because a new
   * client starts a new field-event queue.
   */
  async function raisingEvents(mounted: Mounted): Promise<() => Promise<void>> {
    const base = mounted.props.client;
    const results: Promise<unknown>[] = [];
    const client: ApiClient = {
      ...base,
      raiseFieldEvent: (viewId, request, signal) => {
        const result = base.raiseFieldEvent(viewId, request, signal);
        results.push(result);
        return result;
      },
    };
    apisRaisingEvents.add(mounted.api);
    mounted.rerender({ ...mounted.props, client });
    // The record installed at mount raises no event until a tick after it is committed.
    await act(tick);
    // A response is applied once its result settles; the tick lets the queue finish applying it.
    return async () => {
      await Promise.all(results);
      await tick();
    };
  }

  /** Raises FieldAfter on TEXT, whose check code clears `cleared`, and waits until the editor has applied it. */
  async function blurClearing(
    mounted: Mounted,
    settled: () => Promise<void>,
    cleared: readonly string[],
  ): Promise<void> {
    mounted.api.reply(clearing(cleared));
    await act(async () => {
      mounted.result.current.onFieldBlur(TEXT);
      await settled();
    });
    expect(mounted.api.lines().at(-1)).toBe(`POST ${FIELD_EVENT_PATH}`);
  }

  it.each<[string, readonly string[], FieldValue, readonly Values[]]>([
    [
      'keeps a checkbox cleared to null during a PUT that sent false, for the next PUT',
      [CHECKBOX],
      null,
      [{ [CHECKBOX]: null }],
    ],
    ['adopts the saved false when nothing changed during the PUT', [], false, []],
  ])('%s', async (_case, cleared, shown, laterPuts) => {
    const mounted = mountExisting(recordFixture({ values: { [CHECKBOX]: true } }));
    const { api, result, record } = mounted;
    const settled = await raisingEvents(mounted);
    edit(result, CHECKBOX, false);
    const put = api.hold();
    const saving = await startHeld(() => result.current.save(), put);

    await blurClearing(mounted, settled, cleared);
    expect(result.current.values[CHECKBOX]).toBe(shown);
    await act(async () => {
      put.release(jsonResponse(200, savedCopy(record, { [CHECKBOX]: false })));
      await saving.promise;
    });

    expect(saving.outcome()).toBe(true);
    expect(result.current.values[CHECKBOX]).toBe(shown);
    expect([...result.current.dirty]).toEqual(cleared);
    expect(result.current.isDirty).toBe(cleared.length > 0);

    api.reply(...laterPuts.map((values) => jsonResponse(200, savedCopy(record, values))));
    expect(await perform(() => result.current.save())).toBe(true);

    const putLine = `PUT ${recordPath(record)}`;
    expect(api.lines()).toEqual([putLine, `POST ${FIELD_EVENT_PATH}`, ...laterPuts.map(() => putLine)]);
    const puts = api.calls.filter((call) => call.method === 'PUT').map(sentValues);
    expect(puts).toEqual([{ [CHECKBOX]: false }, ...laterPuts]);
    expect(result.current.isDirty).toBe(false);
  });

  it('keeps a field the user edits during a PUT on screen and dirty, for the next PUT', async () => {
    const { api, result, record } = mountExisting();
    edit(result, TEXT, 'Jones');
    const put = api.hold();
    const saving = await startHeld(() => result.current.save(), put);

    edit(result, TEXT, 'Brown');
    await act(async () => {
      put.release(jsonResponse(200, savedCopy(record, { [TEXT]: 'Jones' })));
      await saving.promise;
    });

    expect(saving.outcome()).toBe(true);
    expect(result.current.values[TEXT]).toBe('Brown');
    expect([...result.current.dirty]).toEqual([TEXT]);

    api.reply(jsonResponse(200, savedCopy(record, { [TEXT]: 'Brown' })));
    expect(await perform(() => result.current.save())).toBe(true);

    const puts = api.calls.filter((call) => call.method === 'PUT').map(sentValues);
    expect(puts).toEqual([{ [TEXT]: 'Jones' }, { [TEXT]: 'Brown' }]);
    expect(result.current.values[TEXT]).toBe('Brown');
    expect(result.current.isDirty).toBe(false);
  });

  it.each<[string, 'before' | 'during', FieldValue, readonly string[], readonly Values[]]>([
    [
      'keeps a checkbox cleared to null during a POST that sent false, for the first PUT',
      'during',
      null,
      [CHECKBOX],
      [{ [CHECKBOX]: null }],
    ],
    [
      'adopts the saved false for a checkbox cleared to null before the POST, which sent it as false',
      'before',
      false,
      [],
      [],
    ],
  ])('%s', async (_case, clearedWhen, shown, dirtyAfter, laterPuts) => {
    const mounted = mountNew();
    const { api, result } = mounted;
    const settled = await raisingEvents(mounted);
    edit(result, TEXT, 'Smith');
    const created = createdRecord({ [TEXT]: 'Smith', ...uncheckedCheckboxes() });
    if (clearedWhen === 'before') {
      await blurClearing(mounted, settled, [CHECKBOX]);
    }
    const post = api.hold();
    const saving = await startHeld(() => result.current.save(), post);
    if (clearedWhen === 'during') {
      await blurClearing(mounted, settled, [CHECKBOX]);
    }

    expect(result.current.values[CHECKBOX]).toBeNull();
    expect(result.current.dirty.has(CHECKBOX)).toBe(true);
    await act(async () => {
      post.release(jsonResponse(201, created));
      await saving.promise;
    });

    expect(saving.outcome()).toBe(true);
    expect(result.current.values[CHECKBOX]).toBe(shown);
    expect([...result.current.dirty]).toEqual(dirtyAfter);
    expect(result.current.isDirty).toBe(dirtyAfter.length > 0);

    api.reply(...laterPuts.map((values) => jsonResponse(200, savedCopy(created, values))));
    expect(await perform(() => result.current.save())).toBe(true);

    const createLine = `POST ${RECORDS_PATH}`;
    const eventLine = `POST ${FIELD_EVENT_PATH}`;
    const putLine = `PUT ${recordPath(created)}`;
    expect(api.lines()).toEqual([
      ...(clearedWhen === 'before' ? [eventLine, createLine] : [createLine, eventLine]),
      ...laterPuts.map(() => putLine),
    ]);
    // The POST sent the cleared checkbox as `false`, so the stored row holds 0 (C-10).
    const posts = api.calls.filter((call) => lineOf(call) === createLine).map(sentValues);
    expect(posts).toEqual([{ [TEXT]: 'Smith', ...uncheckedCheckboxes() }]);
    const puts = api.calls.filter((call) => call.method === 'PUT').map(sentValues);
    expect(puts).toEqual(laterPuts);
    expect(result.current.values[CHECKBOX]).toBe(shown);
    expect(result.current.isDirty).toBe(false);
  });
});

describe('validation', () => {
  it('stops a save that fails client validation without a request', async () => {
    const { api, result } = mountExisting();
    edit(result, ZIP.name, '1'.repeat(ZIP_MAX_LENGTH + 1));

    expect(await perform(() => result.current.save())).toBe(false);

    expect(api.calls).toEqual([]);
    // One message, beside Zip alone, that states the limit; the plan leaves its wording open.
    expect(Object.keys(result.current.fieldErrors)).toEqual([ZIP.name]);
    const messages = result.current.fieldErrors[ZIP.name];
    expect(messages).toHaveLength(1);
    expect(messages[0]).toMatch(new RegExp(`\\b${String(ZIP_MAX_LENGTH)}\\b`));
    expect(result.current.banner).toBeNull();
  });

  /** The fixture form with OTHER_TEXT required; no seeded or fixture field is. */
  function requiringOtherText(): FormDefinition {
    return withField(OTHER_TEXT, (field) => ({ ...field, validation: { ...field.validation, required: true } }));
  }

  it.each<[string, RecordDto | null, string, FieldValue]>([
    ['a new record missing a required field', null, TEXT, 'Smith'],
    [
      'a PUT clearing a required field',
      recordFixture({ values: { [TEXT]: 'Smith', [OTHER_TEXT]: 'Other' } }),
      OTHER_TEXT,
      '',
    ],
  ])('stops %s without a request', async (_case, record, name, value) => {
    const { api, result } = mount(record, record === null, requiringOtherText());
    edit(result, name, value);

    expect(await perform(() => result.current.save())).toBe(false);

    expect(api.calls).toEqual([]);
    expect(Object.keys(result.current.fieldErrors)).toEqual([OTHER_TEXT]);
    const messages = result.current.fieldErrors[OTHER_TEXT];
    expect(messages).toEqual([clientMessages.required]);
    expect(result.current.banner).toBeNull();
  });

  it('leaves an untouched required field to the server, which judges a PUT on the stored row', async () => {
    // The draft holds the required field empty; another save may have filled it since.
    const record = recordFixture({ values: { [TEXT]: 'Smith' } });
    const { api, result, onSaved } = mount(record, false, requiringOtherText());
    edit(result, TEXT, 'Jones');
    const saved = savedCopy(record, { [TEXT]: 'Jones', [OTHER_TEXT]: 'Filled elsewhere' });
    api.reply(jsonResponse(200, saved));

    expect(result.current.values[OTHER_TEXT]).toBeNull();
    expect(await perform(() => result.current.save())).toBe(true);

    expect(api.lines()).toEqual([`PUT ${recordPath(record)}`]);
    expect(sentValues(api.calls[0])).toEqual({ [TEXT]: 'Jones' });
    expect(result.current.fieldErrors).toEqual({});
    expect(onSaved).toHaveBeenCalledWith(saved);
  });

  // A date value is first a wire date, YYYY-MM-DD naming a real day of the years 0001-9999, as
  // every response's dates are; only a wire date then meets the datetime column's storage range,
  // a save rule alone, which no field event applies. A field without a date mask speaks the wire
  // format; one with a date mask, which is its entry and display format, speaks that mask, here
  // the fixture DOB's MM-DD-YYYY.
  const ISO_DATE_EXPECTED = 'Enter a valid date (YYYY-MM-DD).';
  const ISO_DATE_NOT_STORABLE = 'Enter a date between 1753-01-01 and 9999-12-31.';
  const MASKED_DATE_EXPECTED = 'Enter a valid date (MM-DD-YYYY).';
  const MASKED_DATE_NOT_STORABLE = 'Enter a date between 01-01-1753 and 12-31-9999.';
  it.each<[string, FieldValue, string[], string[]]>([
    ['a leap day', '2000-02-29', [], []],
    ['the first day a datetime column stores', '1753-01-01', [], []],
    ['the last day a datetime column stores', '9999-12-31', [], []],
    ['a day the calendar lacks', '2011-02-29', [ISO_DATE_EXPECTED], [MASKED_DATE_EXPECTED]],
    ['year 0000', '0000-01-01', [ISO_DATE_EXPECTED], [MASKED_DATE_EXPECTED]],
    // DateInput turns text that follows the mask into its wire date, so text in the mask is
    // never itself a value; only text that matches no date reaches the editor raw.
    ['the legacy MM-DD-YYYY text', '01-15-2011', [ISO_DATE_EXPECTED], [MASKED_DATE_EXPECTED]],
    ['a partial entry', '05-1', [ISO_DATE_EXPECTED], [MASKED_DATE_EXPECTED]],
    ['a number', 20110115, [ISO_DATE_EXPECTED], [MASKED_DATE_EXPECTED]],
    ['the day before the datetime range', '1752-12-31', [ISO_DATE_NOT_STORABLE], [MASKED_DATE_NOT_STORABLE]],
    ['the first wire date', '0001-01-01', [ISO_DATE_NOT_STORABLE], [MASKED_DATE_NOT_STORABLE]],
  ])('gives feedback on %s in a date field, in the wire format without a date mask and in the mask with one', (_case, value, wireMessages, maskedMessages) => {
    const dob = fieldByName('DOB');
    expect(dob.validation.pattern).toBe('MM-DD-YYYY');
    expect(validateField({ ...dob, validation: { ...dob.validation, pattern: null } }, value)).toEqual(wireMessages);
    expect(validateField(dob, value)).toEqual(maskedMessages);
  });

  // The bounds of a range message are shown as the field shows a date: as wire dates without a
  // date mask, through the mask with one. A pattern that is no date mask speaks the wire format.
  it.each<[string | null, string | null, string | null, string, string[]]>([
    [null, '2011-01-15', '2012-12-31', '2011-01-14', ['Enter a value from 2011-01-15 to 2012-12-31.']],
    ['MM-DD', '2011-01-15', '2012-12-31', '2013-01-01', ['Enter a value from 2011-01-15 to 2012-12-31.']],
    ['MM-DD-YYYY', '2011-01-15', '2012-12-31', '2011-01-14', ['Enter a value from 01-15-2011 to 12-31-2012.']],
    ['MM-DD-YYYY', '2011-01-15', null, '2011-01-14', ['Enter a value of at least 01-15-2011.']],
    ['MM-DD-YYYY', null, '2012-12-31', '2013-01-01', ['Enter a value of at most 12-31-2012.']],
    ['dd.mm.yyyy', '2011-01-15', '2012-12-31', '2013-01-01', ['Enter a value from 15.01.2011 to 31.12.2012.']],
    ['MM-DD-YYYY', '2011-01-15', '2012-12-31', '2011-01-15', []],
  ])('shows the range of a date field with pattern %s and bounds %s to %s, given %s', (pattern, lower, upper, value, messages) => {
    const field = fieldByName('DateofInterview');
    expect(validateField({ ...field, validation: { ...field.validation, pattern, lower, upper } }, value)).toEqual(messages);
  });

  it.each<[string, string]>([
    ['dd.mm.yyyy', 'Enter a valid date (dd.mm.yyyy).'],
    ['YYYY/MM/DD', 'Enter a valid date (YYYY/MM/DD).'],
    ['MM-DD-YYYY HH:MM', ISO_DATE_EXPECTED],
  ])('names the date mask %s, when the pattern is one, in the message for text that is no date', (pattern, message) => {
    const dob = fieldByName('DOB');
    expect(validateField({ ...dob, validation: { ...dob.validation, pattern } }, '31.12.2011x')).toEqual([message]);
  });

  // A digit in a text '#' mask is a literal, as on the server: '##-2' needs two digits, a hyphen and a 2.
  it.each<[string, string, boolean]>([
    ['stops a save without a request', '12-3', false],
    ['sends a save', '12-2', true],
  ])('%s when a text mask with a literal digit is given %s', async (_outcome, value, fits) => {
    const mask = '##-2';
    const definition = withField(TEXT, (field) => ({ ...field, validation: { ...field.validation, pattern: mask } }));
    const { api, result, record } = mountExisting(storedRecord(), definition);
    edit(result, TEXT, value);
    if (fits) {
      api.reply(jsonResponse(200, savedCopy(record, { [TEXT]: value })));
    }

    expect(await perform(() => result.current.save())).toBe(fits);

    expect(api.lines()).toEqual(fits ? [`PUT ${recordPath(record)}`] : []);
    expect(api.calls.map((call) => sentValues(call))).toEqual(fits ? [{ [TEXT]: value }] : []);
    // Nothing when the value fits; otherwise one message, beside the field alone, that shows
    // the mask. The plan leaves the wording open.
    const shown = Object.entries(result.current.fieldErrors).map(([name, messages]) => ({
      name,
      showsMask: messages.map((message) => message.includes(mask)),
    }));
    expect(shown).toEqual(fits ? [] : [{ name: TEXT, showsMask: [true] }]);
    expect(result.current.banner).toBeNull();
  });

  it('shows a field-level 400 by canonical field name and keeps the user on the record', async () => {
    const { api, result, onSaved } = mountExisting();
    edit(result, TEXT, 'Jones');
    const message = 'Last Name is not accepted.';
    api.reply(problem(400, { detail: 'One or more validation errors occurred.', errors: { [TEXT]: [message] } }));

    expect(await perform(() => result.current.leave())).toBe(false);

    expect(api.lines()).toHaveLength(1);
    expect(result.current.fieldErrors).toEqual({ [TEXT]: [message] });
    expect(result.current.banner).toBeNull();
    expect(result.current.values[TEXT]).toBe('Jones');
    expect(result.current.isDirty).toBe(true);
    expect(onSaved).not.toHaveBeenCalled();
  });

  it('shows a request-level 400 detail in a badRequest banner', async () => {
    const { api, result } = mountExisting();
    edit(result, TEXT, 'Jones');
    api.reply(problem(400, { detail: 'malformed JSON' }));

    expect(await perform(() => result.current.save())).toBe(false);

    const banner = bannerOf(result.current);
    expect(banner.kind).toBe('badRequest');
    expect(banner.message).toBe('malformed JSON');
    expect(banner.actions).toEqual([]);
    expect(result.current.fieldErrors).toEqual({});
    expect(result.current.values[TEXT]).toBe('Jones');
    expect(api.lines()).toHaveLength(1);
  });

  // Synthetic metadata: a plain writable text field renamed `__proto__`, the name of Object's
  // prototype accessor. Its message must stay an own entry of the editor's error map, so the
  // invalid new record is stopped before any request. The required message is the client's own
  // required text; the format message, whose wording the plan leaves open, is matched by the
  // limit it states.
  it.each<{
    readonly feedback: string;
    readonly update: (field: FieldDefinition) => FieldDefinition;
    readonly enter: readonly (readonly [string, FieldValue])[];
    readonly expected: readonly (string | RegExp)[];
  }>([
    {
      feedback: 'a format message',
      update: (field) => ({ ...field, validation: { ...field.validation, maxLength: 3 } }),
      enter: [['__proto__', 'abcd']],
      expected: [/\b3\b/],
    },
    {
      feedback: 'the required message',
      update: (field) => ({ ...field, validation: { ...field.validation, required: true } }),
      enter: [[OTHER_TEXT, 'Smith']],
      expected: [clientMessages.required],
    },
  ])('keeps $feedback for a field named __proto__ and sends nothing', async ({ update, enter, expected }) => {
    const definition = withField(TEXT, (field) => update({ ...field, name: '__proto__' }));
    const { api, result } = mountNew(definition);
    for (const [name, value] of enter) {
      edit(result, name, value);
    }

    expect(await perform(() => result.current.save())).toBe(false);

    expect(api.calls).toEqual([]);
    // Own enumerable entries only: an inherited `__proto__` would read as the messages too.
    // A message its pattern matches stands as that pattern, so a text must equal its message.
    const entries = Object.entries(result.current.fieldErrors).map(([name, messages]) => [
      name,
      messages.map((message, index) => {
        const want = expected[index];
        return want instanceof RegExp && want.test(message) ? want : message;
      }),
    ]);
    expect(entries).toEqual([['__proto__', expected]]);
    expect(result.current.banner).toBeNull();
  });
});

describe('leaving a record (0.3.6)', () => {
  it('saves a dirty record first and lets it go only after the 200', async () => {
    const { api, result, record, onSaved } = mountExisting();
    edit(result, TEXT, 'Jones');
    const gate = api.hold();

    const leaving = await startHeld(() => result.current.leave(), gate);

    expect(api.lines()).toEqual([`PUT ${recordPath(record)}`]);
    expect(leaving.outcome()).toBeUndefined();
    expect(result.current.saving).toBe(true);

    const saved = savedCopy(record, { [TEXT]: 'Jones' });
    await act(async () => {
      gate.release(jsonResponse(200, saved));
      await leaving.promise;
    });

    expect(leaving.outcome()).toBe(true);
    expect(onSaved).toHaveBeenCalledWith(saved);
    expect(result.current.saving).toBe(false);
    expect(result.current.isDirty).toBe(false);
  });

  it('cancels the move on a 404, keeps the draft and offers Discard edits', async () => {
    const { api, result, record, onDiscardToNearest } = mountExisting();
    edit(result, TEXT, 'Jones');
    api.reply(problem(404));

    expect(await perform(() => result.current.leave())).toBe(false);

    const banner = bannerOf(result.current);
    expect(banner.kind).toBe('notFound');
    expect(banner.message).toMatch(RECORD_GONE);
    expect(banner.actions).toEqual(['discardEdits']);
    expect(result.current.values[TEXT]).toBe('Jones');
    expect(result.current.isDirty).toBe(true);
    // The draft is never replaced without the user's action.
    expect(onDiscardToNearest).not.toHaveBeenCalled();

    run(() => {
      result.current.discardEdits();
    });

    expect(onDiscardToNearest).toHaveBeenCalledTimes(1);
    expect(result.current.values).toEqual(record.values);
    expect(result.current.banner).toBeNull();
    expect(api.lines()).toEqual([`PUT ${recordPath(record)}`]);
  });

  it.each([
    { name: 'save', write: (editor: RecordEditor) => editor.save(), says: errorMessages.saveConflict },
    // The move the leave was for is cancelled, and the text says so.
    {
      name: 'leave',
      write: (editor: RecordEditor) => editor.leave(),
      says: `${errorMessages.saveConflict} ${errorMessages.leaveCancelled}`,
    },
  ])('shows a record its $name found deleted (409) read-only, with no restore, and tells navigation once', async ({ write, says }) => {
    const { api, result, onSaved, onDeleted, onConflict, onDiscardToNearest } = mountExisting();
    edit(result, TEXT, 'Jones');
    api.reply(problem(409));

    expect(await perform(() => write(result.current))).toBe(false);

    // Navigation learns that the selected set changed; the record was neither saved nor left.
    expect(onConflict).toHaveBeenCalledTimes(1);
    expect(onSaved).not.toHaveBeenCalled();
    expect(onDeleted).not.toHaveBeenCalled();
    expect(onDiscardToNearest).not.toHaveBeenCalled();
    expect(result.current.deletedReadOnly).toBe(true);
    const banner = bannerOf(result.current);
    expect(banner.kind).toBe('deleted');
    expect(banner.message).toMatch(/deleted/i);
    // The edits stay on screen, read-only, and the text says they were not saved.
    expect(banner.message).toBe(says);
    expect(result.current.values[TEXT]).toBe('Jones');
    expect(banner.actions).toEqual([]);
    for (const field of allFields()) {
      expect(result.current.isProtected(field.name)).toBe(true);
    }
    // The edits can no longer be saved, so they no longer hold the user on the record.
    expect(result.current.isDirty).toBe(false);
    expect(await perform(() => result.current.leave())).toBe(true);
    expect(api.lines()).toHaveLength(1);
    expect(onConflict).toHaveBeenCalledTimes(1);
  });

  it.each([
    {
      // The 413 names the field holding the most text in the refused body, the one edit.
      status: 413 as const,
      writeOutcome: undefined,
      kind: 'tooLarge',
      says: errorMessages.saveTooLarge(promptOf(TEXT)),
      actions: [],
    },
    {
      // The text says the draft is kept; the trace id is the banner's own, shown on its own line.
      status: 500 as const,
      writeOutcome: 'notWritten' as const,
      kind: 'server',
      says: errorMessages.serverWithDraft,
      actions: ['retry'],
    },
  ])('keeps the draft on a $status, in a $kind banner with the trace id', async (row) => {
    const { api, result, onSaved } = mountExisting();
    edit(result, TEXT, 'Jones');
    api.reply(problem(row.status, row.writeOutcome === undefined ? {} : { writeOutcome: row.writeOutcome }));

    expect(await perform(() => result.current.leave())).toBe(false);

    const banner = bannerOf(result.current);
    expect(banner.kind).toBe(row.kind);
    // The save was leaving the record, so the text also says that leaving was cancelled.
    expect(banner.message).toBe(`${row.says} ${errorMessages.leaveCancelled}`);
    expect(banner.traceId).toBe(TRACE_ID);
    expect(banner.actions).toEqual(row.actions);
    expect(result.current.values[TEXT]).toBe('Jones');
    expect(result.current.isDirty).toBe(true);
    expect(onSaved).not.toHaveBeenCalled();
    expect(api.lines()).toHaveLength(1);
  });
});

// What a failed save's banner tells the user to do next.
describe('save failure banners', () => {
  it.each<{ readonly name: string; readonly edits: Values; readonly label: string | null }>([
    {
      name: 'the field holding the most text',
      edits: { [TEXT]: 'Jones', [OTHER_TEXT]: 'x'.repeat(64) },
      label: promptOf(OTHER_TEXT),
    },
    {
      // The body is sent as UTF-8, so text is measured in bytes: 40 two-byte letters outweigh 60 one-byte ones.
      name: 'the field whose text takes the most bytes',
      edits: { [TEXT]: 'é'.repeat(40), [OTHER_TEXT]: 'x'.repeat(60) },
      label: promptOf(TEXT),
    },
    { name: 'no field when the body holds no text', edits: { [NUMBER]: 7 }, label: null },
  ])('explains the size limit of a 413 and names $name', async ({ edits, label }) => {
    const { api, result } = mountExisting();
    for (const [name, value] of Object.entries(edits)) {
      edit(result, name, value);
    }
    api.reply(problem(413));

    expect(await perform(() => result.current.save())).toBe(false);

    const banner = bannerOf(result.current);
    expect(banner.kind).toBe('tooLarge');
    // No leave was running, so the text says nothing of one.
    expect(banner.message).toBe(errorMessages.saveTooLarge(label));
    expect(banner.message).toMatch(/request too large/i);
    expect(banner.message).toContain('1 MB');
    expect(banner.traceId).toBe(TRACE_ID);
    expect(banner.actions).toEqual([]);
    expect(sentValues(api.calls[0])).toEqual(edits);
    expect(result.current.isDirty).toBe(true);
  });

  const LEAVE_CANCELLED = ` ${errorMessages.leaveCancelled}`;
  const LEAVE_CANCELLED_DRAFT_KEPT = ` ${errorMessages.leaveCancelled} ${errorMessages.draftKept}`;

  it.each<{ readonly failure: string; readonly reply: () => Response; readonly text: string; readonly suffix: string }>([
    // The 409's edits were not saved and no longer can be, so it says nothing of a kept draft.
    { failure: 'a 409', reply: () => problem(409), text: errorMessages.saveConflict, suffix: LEAVE_CANCELLED },
    {
      failure: 'a 503 reporting notWritten',
      reply: () => problem(503, { writeOutcome: 'notWritten' }),
      text: errorMessages.unavailable,
      suffix: LEAVE_CANCELLED,
    },
    { failure: 'a 404', reply: () => problem(404), text: errorMessages.notFound, suffix: LEAVE_CANCELLED_DRAFT_KEPT },
    {
      failure: 'a 403',
      reply: () => problem(403),
      text: errorMessages.forbidden.update,
      suffix: LEAVE_CANCELLED_DRAFT_KEPT,
    },
    {
      failure: 'a request-level 400',
      reply: () => problem(400, { detail: 'malformed JSON' }),
      text: 'malformed JSON',
      suffix: LEAVE_CANCELLED_DRAFT_KEPT,
    },
  ])('says that leaving was cancelled when $failure stops the save of a leave, and only then', async (row) => {
    const leaving = mountExisting();
    edit(leaving.result, TEXT, 'Jones');
    leaving.api.reply(row.reply());

    expect(await perform(() => leaving.result.current.leave())).toBe(false);
    expect(bannerOf(leaving.result.current).message).toBe(row.text + row.suffix);

    // The same failure of a plain save, with no leave running, says nothing of leaving.
    const saving = mountExisting();
    edit(saving.result, TEXT, 'Jones');
    saving.api.reply(row.reply());

    expect(await perform(() => saving.result.current.save())).toBe(false);
    expect(bannerOf(saving.result.current).message).toBe(row.text);
  });
});

// savePending covers a whole save, from its start to its settled write; saving covers only
// a request in flight, a DELETE's included. The drain part is pinned in fieldEventQueue.test.ts.
describe('save progress', () => {
  it('is pending from save() until its held PUT settles', async () => {
    const { api, result, record } = mountExisting();
    edit(result, TEXT, 'Jones');
    expect(result.current.savePending).toBe(false);
    const gate = api.hold();

    const saving = await startHeld(() => result.current.save(), gate);

    expect(api.lines()).toEqual([`PUT ${recordPath(record)}`]);
    expect(result.current.savePending).toBe(true);
    expect(result.current.saving).toBe(true);

    await act(async () => {
      gate.release(jsonResponse(200, savedCopy(record, { [TEXT]: 'Jones' })));
      await saving.promise;
    });

    expect(saving.outcome()).toBe(true);
    expect(result.current.savePending).toBe(false);
    expect(result.current.saving).toBe(false);
  });

  it('is pending while a Retry of a notWritten PUT is held, and ends with the failed save before it', async () => {
    const { api, result, record } = mountExisting();
    edit(result, TEXT, 'Jones');
    api.reply(problem(503, { writeOutcome: 'notWritten' }));
    expect(await perform(() => result.current.save())).toBe(false);
    expect(result.current.savePending).toBe(false);
    const gate = api.hold();

    const retrying = await startHeld(() => result.current.retry(), gate);

    expect(result.current.savePending).toBe(true);
    await act(async () => {
      gate.release(jsonResponse(200, savedCopy(record, { [TEXT]: 'Jones' })));
      await retrying.promise;
    });
    expect(api.lines()).toEqual([`PUT ${recordPath(record)}`, `PUT ${recordPath(record)}`]);
    expect(result.current.savePending).toBe(false);
  });

  it.each<[string, boolean]>([
    ['is pending from the start of the field-event drain that leave() of a dirty record awaits', true],
    ['stays false through the field-event drain that leave() of a clean record awaits', false],
  ])('%s', async (_case, edited) => {
    const { api, result, record } = mountExisting();
    const eventLine = `POST /api/forms/${VIEW_ID}/checkcode/field-event`;
    apisRaisingEvents.add(api);
    // The record installed at mount raises no event until a tick after it is committed.
    await act(
      () =>
        new Promise<void>((resolve) => {
          setTimeout(resolve, 0);
        }),
    );
    if (edited) {
      edit(result, TEXT, 'Jones');
    }
    const event = api.hold();
    await act(async () => {
      result.current.onFieldBlur(TEXT);
      await event.reached;
    });

    const leaves: Promise<boolean>[] = [];
    run(() => {
      leaves.push(result.current.leave());
    });

    // The leave waits on the held event; a dirty draft's save is under way from here on.
    expect(result.current.frozen).toBe(true);
    expect(result.current.savePending).toBe(edited);
    expect(result.current.saving).toBe(false);
    expect(api.lines()).toEqual([eventLine]);

    if (edited) {
      api.reply(jsonResponse(200, savedCopy(record, { [TEXT]: 'Jones' })));
    }
    const noEffects: FieldEventResponse = {
      enabled: [],
      disabled: [],
      cleared: [],
      assigned: {},
      dialogs: [],
      diagnostics: [],
    };
    expect(
      await perform(() => {
        event.release(
          new Response(JSON.stringify(noEffects), { status: 200, headers: { 'content-type': 'application/json' } }),
        );
        return Promise.all(leaves);
      }),
    ).toEqual([true]);

    expect(result.current.savePending).toBe(false);
    expect(api.lines()).toEqual(edited ? [eventLine, `PUT ${recordPath(record)}`] : [eventLine]);
  });

  it('ends a save that client validation stops before any request', async () => {
    const { api, result } = mountExisting();
    edit(result, ZIP.name, '1'.repeat(ZIP_MAX_LENGTH + 1));

    expect(await perform(() => result.current.save())).toBe(false);

    expect(api.calls).toEqual([]);
    expect(Object.keys(result.current.fieldErrors)).toEqual([ZIP.name]);
    expect(result.current.savePending).toBe(false);
  });

  it('stays false while a confirmed DELETE is held, which saving reports', async () => {
    const { api, result, record } = mountExisting();
    run(() => {
      result.current.requestDelete();
    });
    const gate = api.hold();

    const deleting = await startHeld(() => result.current.confirmDelete(), gate);

    expect(api.lines()).toEqual([`DELETE ${recordPath(record)}`]);
    expect(result.current.saving).toBe(true);
    expect(result.current.savePending).toBe(false);

    await act(async () => {
      gate.release(noContent());
      await deleting.promise;
    });

    expect(result.current.saving).toBe(false);
    expect(result.current.savePending).toBe(false);
  });
});

// `saved` confirms the last successful save of the draft until the next edit, write, delete or
// discard. A save leave() made is confirmed on the record its move installs; the record a later
// move installs without a save, or after a move that failed, confirms nothing.
describe('save confirmation', () => {
  /** The record a move installs. */
  function nextRecord(): RecordDto {
    return recordFixture({
      globalRecordId: '3f2b8c1e-5d4a-4c1b-9e7f-000000000002',
      uniqueKey: 2,
      position: 2,
      values: { [TEXT]: 'Next' },
    });
  }

  it.each<[string, (mounted: MountedRecord) => Promise<void>]>([
    [
      'Save',
      async ({ api, result, record }) => {
        api.reply(jsonResponse(200, savedCopy(record, { [TEXT]: 'Jones' })));
        expect(await perform(() => result.current.save())).toBe(true);
      },
    ],
    [
      'Retry of a notWritten PUT',
      async ({ api, result, record }) => {
        api.reply(problem(503, { writeOutcome: 'notWritten' }));
        expect(await perform(() => result.current.save())).toBe(false);
        expect(result.current.saved).toBe(false);
        api.reply(jsonResponse(200, savedCopy(record, { [TEXT]: 'Jones' })));
        await perform(() => result.current.retry());
      },
    ],
    [
      'Save again after a PUT of unknown outcome',
      async ({ api, result, record }) => {
        api.reply(problem(503, { writeOutcome: 'unknown' }));
        expect(await perform(() => result.current.save())).toBe(false);
        expect(result.current.saved).toBe(false);
        api.reply(jsonResponse(200, savedCopy(record, { [TEXT]: 'Jones' })));
        expect(await perform(() => result.current.saveAgain())).toBe(true);
      },
    ],
  ])('confirms a save made by %s until the next edit', async (_case, succeed) => {
    const mounted = mountExisting();
    const { result } = mounted;
    edit(result, TEXT, 'Jones');
    expect(result.current.saved).toBe(false);

    await succeed(mounted);

    expect(result.current.saved).toBe(true);
    expect(result.current.isDirty).toBe(false);
    edit(result, OTHER_TEXT, 'Brown');
    expect(result.current.saved).toBe(false);
  });

  it('confirms the save leave() made on the record its move installs, and nothing on the record after a move that saved nothing', async () => {
    const { api, result, record, props, rerender } = mountExisting();
    edit(result, TEXT, 'Jones');
    // Navigation freezes the draft before it awaits leave(), and installs the next record after.
    rerender({ ...props, transitioning: true });
    api.reply(jsonResponse(200, savedCopy(record, { [TEXT]: 'Jones' })));
    expect(await perform(() => result.current.leave())).toBe(true);
    expect(result.current.saved).toBe(true);

    const next = nextRecord();
    rerender({ ...props, record: next, recordGeneration: props.recordGeneration + 1, transitioning: false });
    expect(result.current.values[TEXT]).toBe('Next');
    expect(result.current.saved).toBe(true);

    rerender({ ...props, record: next, recordGeneration: props.recordGeneration + 1, transitioning: true });
    expect(await perform(() => result.current.leave())).toBe(true);
    rerender({ ...props, recordGeneration: props.recordGeneration + 2, transitioning: false });
    expect(result.current.values[TEXT]).toBe('Smith');
    expect(result.current.saved).toBe(false);
    expect(api.lines()).toEqual([`PUT ${recordPath(record)}`]);
  });

  it('confirms nothing on a later move once the move a leave() save made way for has failed', async () => {
    const { api, result, record, props, rerender } = mountExisting();
    edit(result, TEXT, 'Jones');
    rerender({ ...props, transitioning: true });
    api.reply(jsonResponse(200, savedCopy(record, { [TEXT]: 'Jones' })));
    expect(await perform(() => result.current.leave())).toBe(true);
    // The move's read failed: navigation ends its transition with the saved record still open.
    rerender({ ...props, transitioning: false });
    expect(result.current.saved).toBe(true);

    rerender({ ...props, transitioning: true });
    expect(await perform(() => result.current.leave())).toBe(true);
    rerender({ ...props, record: nextRecord(), recordGeneration: props.recordGeneration + 1, transitioning: false });
    expect(result.current.values[TEXT]).toBe('Next');
    expect(result.current.saved).toBe(false);
    expect(api.lines()).toEqual([`PUT ${recordPath(record)}`]);
  });
});

// The write-outcome table of 0.3.4.2, for a create and an update

type RecordWriteMethod = 'POST' | 'PUT';

const RECORD_WRITE_METHODS: readonly RecordWriteMethod[] = ['POST', 'PUT'];

/** The statuses whose ProblemDetails carries `writeOutcome`. */
const OUTCOME_STATUSES = [500, 503] as const;

/** An editor holding a dirty draft whose save is a POST or a PUT, and what that save must send. */
interface DirtyDraft extends Mounted {
  /** `METHOD path` of the save. */
  readonly line: string;
  /** The exact `values` of the save: the create body, or the one dirty field. */
  readonly body: Values;
  /** What the user typed into TEXT, which the draft keeps until a save succeeds. */
  readonly typed: string;
  /** The server's DTO once the save succeeds. */
  readonly saved: RecordDto;
  /** The 2xx that answers a successful save. */
  readonly success: () => Response;
}

/** POST: a new record with TEXT entered. PUT: the stored record with TEXT edited. */
function mountDirtyDraft(method: RecordWriteMethod, deadlineMs?: number): DirtyDraft {
  if (method === 'POST') {
    const mounted = mountNew(undefined, deadlineMs);
    edit(mounted.result, TEXT, 'Smith');
    // C-10: every entered value, and `false` for each unchecked writable checkbox.
    const body: Values = { [TEXT]: 'Smith', ...uncheckedCheckboxes() };
    const saved = createdRecord(body);
    return {
      ...mounted,
      line: `POST ${RECORDS_PATH}`,
      body,
      typed: 'Smith',
      saved,
      success: () => jsonResponse(201, saved),
    };
  }
  const mounted = mountExisting(undefined, undefined, deadlineMs);
  edit(mounted.result, TEXT, 'Jones');
  // C-3: the dirty field only.
  const body: Values = { [TEXT]: 'Jones' };
  const saved = savedCopy(mounted.record, body);
  return {
    ...mounted,
    line: `PUT ${recordPath(mounted.record)}`,
    body,
    typed: 'Jones',
    saved,
    success: () => jsonResponse(200, saved),
  };
}

/** The Content-Type values the API's WriteProblemDetails is recognised by; a parameter is ignored. */
const WRITE_PROBLEM_CONTENT_TYPES = ['application/problem+json', 'application/problem+json; charset=utf-8'] as const;

/** notWritten, the one outcome Retry resends; the banner follows the status, and both say the draft is kept. */
const NOT_WRITTEN_CASES = RECORD_WRITE_METHODS.flatMap((method) =>
  WRITE_PROBLEM_CONTENT_TYPES.flatMap((contentType) => [
    // The generic error banner; its trace id is the banner's own.
    { method, contentType, status: 500 as const, kind: 'server', says: errorMessages.serverWithDraft },
    // The connection banner.
    { method, contentType, status: 503 as const, kind: 'unavailable', says: errorMessages.unavailable },
  ]),
);

/** The 500 and 503 bodies that do not prove the save was not applied, and the outcome each parses as. */
const UNPROVEN_OUTCOMES: readonly {
  readonly name: string;
  readonly extra: ProblemExtra;
  readonly outcome: WriteOutcome | null;
}[] = [
  { name: 'writeOutcome unknown', extra: { writeOutcome: 'unknown' }, outcome: 'unknown' },
  { name: 'writeOutcome written', extra: { writeOutcome: 'written' }, outcome: 'written' },
  { name: 'no writeOutcome', extra: {}, outcome: null },
  // A value outside the contract, such as a newer server's, proves nothing either.
  { name: 'an unrecognised outcome', extra: { writeOutcome: 'partiallyWritten' }, outcome: null },
];

/**
 * Responses whose JSON body says `notWritten` but that are not the API's WriteProblemDetails
 * (a 500 or 503 served as `application/problem+json`, a ProblemDetails of that status with
 * every member the schema requires) received as the direct reply to the save, so they prove
 * nothing: the save may have been applied.
 */
const UNTRUSTED_NOT_WRITTEN: readonly {
  readonly name: string;
  readonly status: 500 | 502 | 503 | 504;
  readonly contentType: string;
  /** Laid over the conforming body; an `undefined` member is left out. */
  readonly members: Readonly<Record<string, unknown>>;
  readonly traceId: string | null;
}[] = [
  // An intermediary's answer, such as the development proxy's, when the API did not answer.
  { name: "an intermediary's 502", status: 502, contentType: 'application/problem+json', members: {}, traceId: TRACE_ID },
  { name: "an intermediary's 504", status: 504, contentType: 'application/problem+json', members: {}, traceId: TRACE_ID },
  { name: 'a 500 served as application/json', status: 500, contentType: 'application/json', members: {}, traceId: TRACE_ID },
  {
    name: 'a 503 whose body status is 500',
    status: 503,
    contentType: 'application/problem+json',
    members: { status: 500 },
    traceId: TRACE_ID,
  },
  {
    name: 'a 503 without traceId',
    status: 503,
    contentType: 'application/problem+json',
    members: { traceId: undefined },
    traceId: null,
  },
  {
    name: 'a 503 without type',
    status: 503,
    contentType: 'application/problem+json',
    members: { type: undefined },
    traceId: TRACE_ID,
  },
  {
    name: 'a 503 without detail',
    status: 503,
    contentType: 'application/problem+json',
    members: { detail: undefined },
    traceId: TRACE_ID,
  },
  // A conforming body that answers the request a redirect led to, not the save, is refused
  // before it is read, as no response: REDIRECT_REPLIES.
];

/** A `status` response served as `contentType` whose body is a notWritten write problem with `members` laid over it. */
function notWrittenAnswer(status: number, contentType: string, members: Readonly<Record<string, unknown>>): Response {
  const body = {
    type: 'about:blank',
    title: 'A fixed title.',
    status,
    detail: 'A fixed server sentence.',
    traceId: TRACE_ID,
    writeOutcome: 'notWritten',
    ...members,
  };
  return new Response(JSON.stringify(body), { status, headers: { 'content-type': contentType } });
}

interface UnsafeCase {
  readonly method: RecordWriteMethod;
  /** What answered the save, for the test name. */
  readonly failure: string;
  readonly reply: () => Reply;
  /** The banner's trace id: the server's, or `null` when no response arrived. */
  readonly traceId: string | null;
  /** What failed, which the banner names first. */
  readonly cause: string;
  /** The write outcome the failure is parsed as; never `notWritten`. */
  readonly outcome: WriteOutcome | null;
}

/**
 * A redirect in place of the save's answer, in each form a transport may give it: fetch
 * refuses it, and a replacement transport may follow it to a conforming notWritten 500 or
 * 503, return the opaque redirect, or return the redirect itself. None reached the API's
 * answer, so each is no response (0.3.7).
 */
const REDIRECT_REPLIES: readonly { readonly failure: string; readonly reply: Reply }[] = [
  { failure: 'a redirect, which fetch refuses', reply: fetchRedirecting },
  ...OUTCOME_STATUSES.map((status) => ({
    failure: `a redirect its transport followed to a notWritten ${String(status)}`,
    reply: () => Promise.resolve(asRedirected(problem(status, { writeOutcome: 'notWritten' }))),
  })),
  { failure: 'an opaque redirect', reply: () => Promise.resolve(opaqueRedirect()) },
  { failure: 'a 307 redirect claiming notWritten', reply: () => Promise.resolve(temporaryRedirect()) },
];

/**
 * Every failed save that Retry must not resend: each unproven 500 and 503, each untrusted
 * notWritten, no response, and a redirect.
 */
const UNSAFE_CASES: readonly UnsafeCase[] = RECORD_WRITE_METHODS.flatMap((method): UnsafeCase[] => [
  ...OUTCOME_STATUSES.flatMap((status) =>
    UNPROVEN_OUTCOMES.map(
      ({ name, extra, outcome }): UnsafeCase => ({
        method,
        failure: `a ${String(status)} with ${name}`,
        reply: () => problem(status, extra),
        traceId: TRACE_ID,
        cause: status === 500 ? errorMessages.server : errorMessages.unavailableWithoutDraft,
        outcome,
      }),
    ),
  ),
  ...UNTRUSTED_NOT_WRITTEN.map(
    ({ name, status, contentType, members, traceId }): UnsafeCase => ({
      method,
      failure: `${name} saying notWritten`,
      reply: () => notWrittenAnswer(status, contentType, members),
      traceId,
      cause: status === 500 ? errorMessages.server : errorMessages.unavailableWithoutDraft,
      outcome: null,
    }),
  ),
  {
    method,
    failure: 'no response',
    reply: () => networkFailure,
    traceId: null,
    cause: errorMessages.networkWithoutDraft,
    outcome: null,
  },
  ...REDIRECT_REPLIES.map(
    ({ failure, reply }): UnsafeCase => ({
      method,
      failure,
      reply: () => reply,
      traceId: null,
      cause: errorMessages.networkWithoutDraft,
      outcome: null,
    }),
  ),
]);

describe('write outcomes and Retry (0.3.4.2)', () => {
  it.each(NOT_WRITTEN_CASES)('retries a $method that a $status served as $contentType reports notWritten', async (row) => {
    const draft = mountDirtyDraft(row.method);
    const { api, result, onSaved } = draft;
    api.reply(problem(row.status, { writeOutcome: 'notWritten', contentType: row.contentType }));

    expect(await perform(() => result.current.save())).toBe(false);

    const banner = bannerOf(result.current);
    expect(banner.error.writeOutcome).toBe('notWritten');
    expect(banner.kind).toBe(row.kind);
    expect(banner.method).toBe(row.method);
    expect(banner.actions).toEqual(['retry']);
    expect(banner.message).toBe(row.says);
    expect(banner.traceId).toBe(TRACE_ID);
    expect(result.current.values[TEXT]).toBe(draft.typed);
    expect(onSaved).not.toHaveBeenCalled();

    api.reply(draft.success());
    await perform(() => result.current.retry());

    expect(api.lines()).toEqual([draft.line, draft.line]);
    expect(sentValues(api.calls[0])).toEqual(draft.body);
    expect(sentValues(api.calls[1])).toEqual(draft.body);
    expect(onSaved).toHaveBeenCalledTimes(1);
    expect(onSaved).toHaveBeenCalledWith(draft.saved);
    expect(result.current.banner).toBeNull();
  });

  it.each(UNSAFE_CASES)('offers only Save again on a $method after $failure', async ({ method, reply, traceId, cause, outcome }) => {
    const draft = mountDirtyDraft(method);
    const { api, result, onSaved } = draft;
    api.reply(reply());

    expect(await perform(() => result.current.save())).toBe(false);

    const banner = bannerOf(result.current);
    expect(banner.error.writeOutcome).toBe(outcome);
    expect(isSafeToResend(banner.error, method)).toBe(false);
    expect(banner.kind).toBe('uncertainWrite');
    expect(banner.method).toBe(method);
    expect(banner.isCreate).toBe(method === 'POST');
    expect(banner.traceId).toBe(traceId);
    expect(banner.actions).toEqual(['saveAgain']);
    expect(banner.message).toMatch(MAY_ALREADY_BE_APPLIED);
    expect(banner.message).toMatch(LIST_CANNOT_PROVE);
    // Save again names the risk; for a create it is a possible duplicate record.
    if (method === 'POST') {
      expect(banner.message).toMatch(DUPLICATE_RECORD);
    } else {
      expect(banner.message).not.toMatch(/duplicate/i);
    }
    // The cause comes first, then what the save may have done and that the draft is kept.
    const risk = method === 'POST' ? ` ${errorMessages.duplicateRisk}` : '';
    const uncertainText = `${cause} ${errorMessages.uncertainWrite}${risk}`;
    expect(banner.message).toBe(uncertainText);

    // Neither Retry nor an ordinary save or leave resends it, and the draft stays.
    await perform(() => result.current.retry());
    expect(await perform(() => result.current.save())).toBe(false);
    expect(bannerOf(result.current).message).toBe(uncertainText);
    expect(await perform(() => result.current.leave())).toBe(false);
    // The refused leave's banner says that leaving was cancelled.
    expect(bannerOf(result.current).message).toBe(`${uncertainText} ${errorMessages.leaveCancelled}`);
    expect(api.lines()).toEqual([draft.line]);
    expect(bannerOf(result.current).actions).toEqual(['saveAgain']);
    expect(result.current.values[TEXT]).toBe(draft.typed);
    expect(result.current.isDirty).toBe(true);
    expect(onSaved).not.toHaveBeenCalled();

    // Save again, the explicit action, sends the same request exactly once more.
    api.reply(draft.success());
    expect(await perform(() => result.current.saveAgain())).toBe(true);

    expect(api.lines()).toEqual([draft.line, draft.line]);
    expect(sentValues(api.calls[0])).toEqual(draft.body);
    expect(sentValues(api.calls[1])).toEqual(draft.body);
    expect(onSaved).toHaveBeenCalledTimes(1);
    expect(onSaved).toHaveBeenCalledWith(draft.saved);
    expect(result.current.banner).toBeNull();
  });

  it('retries a DELETE that got no response', async () => {
    const { api, result, record, onDeleted } = mountExisting();
    run(() => {
      result.current.requestDelete();
    });
    api.reply(networkFailure);

    await perform(() => result.current.confirmDelete());

    const banner = bannerOf(result.current);
    expect(banner.kind).toBe('unavailable');
    expect(banner.method).toBe('DELETE');
    expect(banner.actions).toEqual(['retry']);
    expect(onDeleted).not.toHaveBeenCalled();

    api.reply(noContent());
    await perform(() => result.current.retry());

    expect(api.lines()).toEqual([`DELETE ${recordPath(record)}`, `DELETE ${recordPath(record)}`]);
    expect(onDeleted).toHaveBeenCalledTimes(1);
    expect(result.current.banner).toBeNull();
  });

  // Only the direct reply to a record create, update or delete is a WriteProblemDetails
  // (0.3.4.1): the same conforming notWritten reply to any other call states no write
  // outcome, and one reached through a redirect is refused as no response (0.3.7), so it
  // states none either. Keyed by every member of ApiClient, so an operation
  // the client gains fails tsc until it is listed.
  const OUTCOME_OF_DIRECT_REPLY: Readonly<
    Record<
      keyof ApiClient,
      { readonly call: (client: ApiClient) => Promise<ApiResult<unknown>>; readonly outcome: WriteOutcome | null }
    >
  > = {
    listForms: { call: (client) => client.listForms(), outcome: null },
    getForm: { call: (client) => client.getForm(VIEW_ID), outcome: null },
    listRecords: { call: (client) => client.listRecords(VIEW_ID), outcome: null },
    countRecords: { call: (client) => client.countRecords(VIEW_ID), outcome: null },
    getRecordAt: { call: (client) => client.getRecordAt(VIEW_ID, 1), outcome: null },
    getRecord: { call: (client) => client.getRecord(VIEW_ID, NEW_RECORD_ID), outcome: null },
    getRecordPosition: { call: (client) => client.getRecordPosition(VIEW_ID, NEW_RECORD_ID), outcome: null },
    createRecord: { call: (client) => client.createRecord(VIEW_ID, { [TEXT]: 'Smith' }), outcome: 'notWritten' },
    updateRecord: {
      call: (client) => client.updateRecord(VIEW_ID, NEW_RECORD_ID, { [TEXT]: 'Jones' }),
      outcome: 'notWritten',
    },
    deleteRecord: { call: (client) => client.deleteRecord(VIEW_ID, NEW_RECORD_ID), outcome: 'notWritten' },
    searchRecords: { call: (client) => client.searchRecords(VIEW_ID, { criteria: [] }), outcome: null },
    raiseFieldEvent: {
      call: (client) => client.raiseFieldEvent(VIEW_ID, { event: 'FieldAfter', fieldName: TEXT, values: {} }),
      outcome: null,
    },
    getHealth: { call: (client) => client.getHealth(), outcome: null },
  };

  it.each(
    Object.entries(OUTCOME_OF_DIRECT_REPLY).flatMap(([name, { call, outcome }]) =>
      OUTCOME_STATUSES.flatMap((status) => [
        {
          name,
          call,
          status,
          via: 'directly',
          redirected: false,
          settled: { status, traceId: TRACE_ID, writeOutcome: outcome },
        },
        // A reply that would state an outcome directly is refused through a redirect, before
        // it is read, so the call settles as no response and states none.
        ...(outcome === null
          ? []
          : [
              {
                name,
                call,
                status,
                via: 'through a redirect',
                redirected: true,
                settled: { kind: 'network', status: null, traceId: null, writeOutcome: null },
              },
            ]),
      ]),
    ),
  )(
    'settles a conforming $status notWritten answering $name $via',
    async ({ call, status, redirected, settled }) => {
      const api = createFakeApi();
      // The raiseFieldEvent row sends the one field event itself.
      apisRaisingEvents.add(api);
      const answer = problem(status, { writeOutcome: 'notWritten' });
      api.reply(redirected ? asRedirected(answer) : answer);

      const result = await call(createApiClient({ baseUrl: 'http://localhost', fetch: api.fetch }));

      expect(api.calls).toHaveLength(1);
      expect(result.ok ? null : result.error).toMatchObject(settled);
    },
  );
});

// The client's request deadline: a request whose whole response has not arrived by then is
// cancelled and settles as no response, which 0.3.4.2 lets Retry resend for a DELETE or a
// read and never for a POST or PUT. Nothing resends it by itself. Real timers, short deadline.
describe('the request deadline (0.3.4.2)', () => {
  const DEADLINE_MS = 100;
  // A timer fires no earlier than its delay, up to the millisecond the event loop rounds to.
  const TIMER_SLACK_MS = 5;

  function sleep(ms: number): Promise<void> {
    return new Promise((resolve) => {
      setTimeout(resolve, ms);
    });
  }

  function abortError(): DOMException {
    return new DOMException('The operation was aborted.', 'AbortError');
  }

  /** The requests a transport received, in arrival order. */
  interface Received {
    readonly requests: readonly Request[];
  }

  /** The request received, which the deadline cancelled. */
  function expectCancelledByDeadline({ requests }: Received): void {
    expect(requests).toHaveLength(1);
    const { signal } = requests[0];
    expect(signal.aborted).toBe(true);
    const reason: unknown = signal.reason;
    expect(reason).toMatchObject({ name: 'TimeoutError' });
  }

  /** A fake API reply that never answers, as a stalled connection. */
  interface Stall extends Received {
    readonly reply: Reply;
  }

  function stall(): Stall {
    const requests: Request[] = [];
    return {
      reply: (request) => {
        requests.push(request);
        return new Promise<Response>(() => undefined);
      },
      requests,
    };
  }

  const STALLED_WRITES = RECORD_WRITE_METHODS.flatMap((method) =>
    (['save', 'leave'] as const).map((action) => ({ method, action })),
  );

  it.each(STALLED_WRITES)(
    'ends the $action of a $method that never answers at the deadline as a write that may have been applied, and never resends it',
    async ({ method, action }) => {
      const draft = mountDirtyDraft(method, DEADLINE_MS);
      const { api, result, onSaved } = draft;
      const held = stall();
      api.reply(held.reply);
      const { values } = result.current;
      const dirty = new Set(result.current.dirty);
      const started = performance.now();

      expect(await perform(() => (action === 'save' ? result.current.save() : result.current.leave()))).toBe(false);

      expect(performance.now() - started).toBeGreaterThanOrEqual(DEADLINE_MS - TIMER_SLACK_MS);
      expectCancelledByDeadline(held);
      const banner = bannerOf(result.current);
      expect(banner.kind).toBe('uncertainWrite');
      expect(banner.method).toBe(method);
      expect(banner.isCreate).toBe(method === 'POST');
      expect(banner.traceId).toBeNull();
      expect(banner.actions).toEqual(['saveAgain']);
      expect(banner.message).toMatch(MAY_ALREADY_BE_APPLIED);
      expect(banner.message).toMatch(LIST_CANNOT_PROVE);
      // The draft stays as it was, and the editor is no longer saving.
      expect(result.current.values).toEqual(values);
      expect(result.current.dirty).toEqual(dirty);
      expect(result.current.saving).toBe(false);
      expect(result.current.savePending).toBe(false);
      expect(onSaved).not.toHaveBeenCalled();

      // Several deadlines later the write has still been sent once.
      await act(async () => {
        await sleep(DEADLINE_MS * 3);
      });
      expect(api.lines()).toEqual([draft.line]);

      // Save again, the explicit action, sends it once more, and its answer completes the save.
      api.reply(draft.success());
      expect(await perform(() => result.current.saveAgain())).toBe(true);

      expect(api.lines()).toEqual([draft.line, draft.line]);
      expect(sentValues(api.calls[1])).toEqual(draft.body);
      expect(onSaved).toHaveBeenCalledTimes(1);
      expect(onSaved).toHaveBeenCalledWith(draft.saved);
      expect(result.current.banner).toBeNull();
    },
  );

  it('ends a DELETE that never answers at the deadline with Retry, and sends it again only on Retry', async () => {
    const { api, result, record, onDeleted } = mountExisting(undefined, undefined, DEADLINE_MS);
    const line = `DELETE ${recordPath(record)}`;
    run(() => {
      result.current.requestDelete();
    });
    const held = stall();
    api.reply(held.reply);
    const started = performance.now();

    await perform(() => result.current.confirmDelete());

    expect(performance.now() - started).toBeGreaterThanOrEqual(DEADLINE_MS - TIMER_SLACK_MS);
    expectCancelledByDeadline(held);
    const banner = bannerOf(result.current);
    expect(banner.kind).toBe('unavailable');
    expect(banner.method).toBe('DELETE');
    expect(banner.traceId).toBeNull();
    expect(banner.actions).toEqual(['retry']);
    expect(result.current.deleteDialogOpen).toBe(false);
    expect(onDeleted).not.toHaveBeenCalled();

    await act(async () => {
      await sleep(DEADLINE_MS * 3);
    });
    expect(api.lines()).toEqual([line]);

    api.reply(noContent());
    await perform(() => result.current.retry());

    expect(api.lines()).toEqual([line, line]);
    expect(onDeleted).toHaveBeenCalledTimes(1);
    expect(result.current.banner).toBeNull();
  });

  /** A fetch that keeps every request it receives. */
  interface Transport extends Received {
    readonly fetch: (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>;
  }

  /**
   * A transport whose exchange never completes: with `status` null it never answers; with a
   * status it answers at once, and the body's first bytes arrive but its end never does. As
   * fetch does, it fails with an AbortError, at once, when the request's signal aborts.
   */
  function neverCompleting(status: number | null): Transport {
    const requests: Request[] = [];
    const fetchNever = (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
      const request = input instanceof Request ? input : new Request(input, init);
      requests.push(request);
      const { signal } = request;
      if (status === null) {
        return new Promise<Response>((_resolve, reject) => {
          signal.addEventListener(
            'abort',
            () => {
              reject(abortError());
            },
            { once: true },
          );
        });
      }
      const body = new ReadableStream<Uint8Array>({
        start(stream) {
          stream.enqueue(new TextEncoder().encode('{"'));
          signal.addEventListener(
            'abort',
            () => {
              stream.error(abortError());
            },
            { once: true },
          );
        },
      });
      return Promise.resolve(
        new Response(body, {
          status,
          headers: { 'content-type': status < 400 ? 'application/json' : 'application/problem+json' },
        }),
      );
    };
    return { fetch: fetchNever, requests };
  }

  const NEVER_COMPLETING: readonly { readonly name: string; readonly status: number | null }[] = [
    { name: 'never answers', status: null },
    { name: 'answers 200 with a body that never ends', status: 200 },
    { name: 'answers 503 with a body that never ends', status: 503 },
  ];

  it.each(NEVER_COMPLETING)('settles a request that $name as no response at the deadline, and cancels it', async ({ status }) => {
    const transport = neverCompleting(status);
    const client = createApiClient({ baseUrl: 'http://localhost', fetch: transport.fetch, deadlineMs: DEADLINE_MS });
    const started = performance.now();

    const read = await client.countRecords(VIEW_ID);

    expect(performance.now() - started).toBeGreaterThanOrEqual(DEADLINE_MS - TIMER_SLACK_MS);
    const error = read.ok ? null : read.error;
    expect(error?.kind).toBe('network');
    expect(error?.status).toBeNull();
    expect(error?.traceId).toBeNull();
    expect(error?.writeOutcome).toBeNull();
    expectCancelledByDeadline(transport);
  });

  it.each(NEVER_COMPLETING)(
    "rejects a request that $name with an AbortError when the caller aborts it before the deadline",
    async ({ status }) => {
      const transport = neverCompleting(status);
      const client = createApiClient({ baseUrl: 'http://localhost', fetch: transport.fetch, deadlineMs: DEADLINE_MS });
      const caller = new AbortController();
      const read = client.countRecords(VIEW_ID, undefined, caller.signal);

      await sleep(DEADLINE_MS / 4);
      caller.abort();

      await expect(read).rejects.toSatisfy(isAbortError);
      expect(transport.requests).toHaveLength(1);
      const { signal } = transport.requests[0];
      expect(signal.aborted).toBe(true);
      const reason: unknown = signal.reason;
      expect(reason).toMatchObject({ name: 'AbortError' });
    },
  );

  /**
   * A transport that keeps every request and never answers it, deaf to its signal, as a
   * replacement fetch may be: only the client can end the call.
   */
  function deafAndSilent(): Transport {
    const requests: Request[] = [];
    const fetchDeaf = (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
      requests.push(input instanceof Request ? input : new Request(input, init));
      return new Promise<Response>(() => undefined);
    };
    return { fetch: fetchDeaf, requests };
  }

  // A caller's abort ends the call at once, with the caller's own reason, however the
  // transport behaves. The deadline is long, so a call it ended could not be mistaken for one
  // the abort ended.
  it.each(
    [
      {
        operation: 'a read',
        call: (client: ApiClient, signal: AbortSignal) => client.countRecords(VIEW_ID, undefined, signal),
      },
      {
        operation: 'a create',
        call: (client: ApiClient, signal: AbortSignal) => client.createRecord(VIEW_ID, { [TEXT]: 'Smith' }, signal),
      },
    ].flatMap(({ operation, call }) => [
      // fetch rejects a request whose signal is already aborted without sending it.
      { operation, call, when: 'before it starts', abortAfterMs: null, sent: 0 },
      { operation, call, when: 'while its request is in flight', abortAfterMs: DEADLINE_MS / 4, sent: 1 },
    ]),
  )(
    'rejects $operation aborted $when at once with the caller’s AbortError, though the transport ignores it and never answers',
    async ({ call, abortAfterMs, sent }) => {
      const deadlineMs = DEADLINE_MS * 10;
      const transport = deafAndSilent();
      const client = createApiClient({ baseUrl: 'http://localhost', fetch: transport.fetch, deadlineMs });
      const caller = new AbortController();
      if (abortAfterMs === null) {
        caller.abort();
      }
      const pending = call(client, caller.signal);
      if (abortAfterMs !== null) {
        await sleep(abortAfterMs);
        caller.abort();
      }
      const aborted = performance.now();
      const reason: unknown = caller.signal.reason;

      await expect(pending).rejects.toBe(reason);

      expect(reason).toSatisfy(isAbortError);
      // Settled by the abort itself, long before the deadline could end it.
      expect(performance.now() - aborted).toBeLessThan(deadlineMs / 10);
      expect(transport.requests).toHaveLength(sent);
      // A request that was sent is cancelled with the caller's reason, never the deadline's.
      for (const { signal } of transport.requests) {
        expect(signal.aborted).toBe(true);
        expect(signal.reason).toBe(reason);
      }
    },
  );

  /** A transport that answers `status` with `text` in two parts, the second, which ends the body, after `delayMs`. */
  function answeringInParts(status: number, text: string, delayMs: number): Transport {
    const requests: Request[] = [];
    const fetchInParts = (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
      requests.push(input instanceof Request ? input : new Request(input, init));
      const encoder = new TextEncoder();
      const middle = Math.floor(text.length / 2);
      const body = new ReadableStream<Uint8Array>({
        start(stream) {
          stream.enqueue(encoder.encode(text.slice(0, middle)));
          setTimeout(() => {
            stream.enqueue(encoder.encode(text.slice(middle)));
            stream.close();
          }, delayMs);
        },
      });
      return Promise.resolve(
        new Response(body, {
          status,
          headers: { 'content-type': status < 400 ? 'application/json' : 'application/problem+json' },
        }),
      );
    };
    return { fetch: fetchInParts, requests };
  }

  const PROBLEM_503 = JSON.stringify({
    type: 'about:blank',
    title: 'Service Unavailable',
    status: 503,
    detail: 'A fixed server sentence.',
    traceId: TRACE_ID,
    writeOutcome: 'notWritten',
  });

  it.each<[string, (client: ApiClient) => Promise<ApiResult<unknown>>, number, string, ApiResult<unknown>]>([
    ['an OK read', (client) => client.countRecords(VIEW_ID), 200, '{"count":7}', { ok: true, data: { count: 7 }, status: 200 }],
    [
      'a 503 of a create',
      (client) => client.createRecord(VIEW_ID, { [TEXT]: 'Smith' }),
      503,
      PROBLEM_503,
      {
        ok: false,
        error: {
          kind: 'unavailable',
          status: 503,
          type: 'about:blank',
          title: 'Service Unavailable',
          detail: 'A fixed server sentence.',
          traceId: TRACE_ID,
          fieldErrors: {},
          writeOutcome: 'notWritten',
        },
      },
    ],
  ])('reads %s whose body ends within the deadline as it was sent', async (_case, call, status, text, expected) => {
    const transport = answeringInParts(status, text, DEADLINE_MS / 4);
    const client = createApiClient({ baseUrl: 'http://localhost', fetch: transport.fetch, deadlineMs: DEADLINE_MS });

    expect(await call(client)).toEqual(expected);

    expect(transport.requests).toHaveLength(1);
    expect(transport.requests[0].signal.aborted).toBe(false);
  });

  // An error body reaches the parser exactly as it was received and is decoded once, as fetch
  // decodes a body: as UTF-8, dropping leading byte order marks. A mark that one decode leaves
  // in the text makes the body not JSON, so a writeOutcome it seems to carry proves nothing
  // (0.3.4.2); a second decode, which would drop that mark as well, must not happen.
  const UTF8_BOM: readonly number[] = [0xef, 0xbb, 0xbf];

  /** `marks` UTF-8 byte order marks followed by the UTF-8 bytes of `text`. */
  function afterMarks(marks: number, text: string): Uint8Array<ArrayBuffer> {
    const encoded = new TextEncoder().encode(text);
    const bytes = new Uint8Array(UTF8_BOM.length * marks + encoded.length);
    for (let mark = 0; mark < marks; mark += 1) {
      bytes.set(UTF8_BOM, UTF8_BOM.length * mark);
    }
    bytes.set(encoded, UTF8_BOM.length * marks);
    return bytes;
  }

  /**
   * How many leading byte order marks one decode of a body by this runtime's fetch drops:
   * one under the Encoding standard, as a browser decodes; Node's fetch has been seen to drop
   * two. Measured, so each case below holds wherever the tests run.
   */
  async function marksOneDecodeDrops(): Promise<number> {
    const marks = 4;
    const text = await new Response(afterMarks(marks, '{}')).text();
    return marks - text.indexOf('{');
  }

  /** A 503 whose body is `marks` byte order marks followed by the bytes of PROBLEM_503. */
  function problemAfterMarks(marks: number): Response {
    return new Response(afterMarks(marks, PROBLEM_503), {
      status: 503,
      headers: { 'content-type': 'application/problem+json' },
    });
  }

  /** A transport that keeps every request and answers it with `response`. */
  function answeringWith(response: Response): Transport {
    const requests: Request[] = [];
    const fetchAnswering = (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
      requests.push(input instanceof Request ? input : new Request(input, init));
      return Promise.resolve(response);
    };
    return { fetch: fetchAnswering, requests };
  }

  const READ_AS_SENT: ApiError = {
    kind: 'unavailable',
    status: 503,
    type: 'about:blank',
    title: 'Service Unavailable',
    detail: 'A fixed server sentence.',
    traceId: TRACE_ID,
    fieldErrors: {},
    writeOutcome: 'notWritten',
  };
  const NOT_JSON: ApiError = {
    kind: 'unavailable',
    status: 503,
    type: null,
    title: null,
    detail: null,
    traceId: null,
    fieldErrors: {},
    writeOutcome: null,
  };

  const MARKED_BODIES: readonly {
    readonly body: string;
    /** The marks before the JSON, given how many one decode drops. */
    readonly marks: (dropped: number) => number;
    readonly error: ApiError;
    readonly resendable: boolean;
  }[] = [
    { body: 'no byte order mark', marks: () => 0, error: READ_AS_SENT, resendable: true },
    {
      body: 'the byte order marks one decode drops',
      marks: (dropped) => dropped,
      error: READ_AS_SENT,
      resendable: true,
    },
    {
      body: 'one byte order mark more than one decode drops',
      marks: (dropped) => dropped + 1,
      error: NOT_JSON,
      resendable: false,
    },
  ];

  it.each(MARKED_BODIES)(
    'reads the notWritten 503 of a create whose body has $body as fetch decodes it, once',
    async ({ marks, error, resendable }) => {
      const dropped = await marksOneDecodeDrops();
      expect(dropped).toBeGreaterThanOrEqual(1);
      const transport = answeringWith(problemAfterMarks(marks(dropped)));
      const client = createApiClient({ baseUrl: 'http://localhost', fetch: transport.fetch, deadlineMs: DEADLINE_MS });

      const created = await client.createRecord(VIEW_ID, { [TEXT]: 'Smith' });

      expect(created).toEqual({ ok: false, error });
      expect(!created.ok && isSafeToResend(created.error, 'POST')).toBe(resendable);
      expect(transport.requests).toHaveLength(1);
    },
  );

  it.each(RECORD_WRITE_METHODS)(
    'offers only Save again on a %s answered 503 notWritten with one byte order mark more than one decode drops',
    async (method) => {
      const marks = (await marksOneDecodeDrops()) + 1;
      const draft = mountDirtyDraft(method, DEADLINE_MS);
      const { api, result, onSaved } = draft;
      api.reply(problemAfterMarks(marks));

      expect(await perform(() => result.current.save())).toBe(false);

      const banner = bannerOf(result.current);
      expect(banner.kind).toBe('uncertainWrite');
      expect(banner.method).toBe(method);
      expect(banner.isCreate).toBe(method === 'POST');
      expect(banner.traceId).toBeNull();
      expect(banner.actions).toEqual(['saveAgain']);
      expect(banner.message).toMatch(MAY_ALREADY_BE_APPLIED);
      expect(banner.message).toMatch(LIST_CANNOT_PROVE);
      // Retry does not resend it, and the draft stays.
      await perform(() => result.current.retry());
      expect(api.lines()).toEqual([draft.line]);
      expect(result.current.values[TEXT]).toBe(draft.typed);
      expect(result.current.isDirty).toBe(true);
      expect(onSaved).not.toHaveBeenCalled();
    },
  );

  // Each of these, as a timer delay, would fire within a millisecond or two: zero, a negative
  // number and NaN count as no delay, and Infinity and 2^32 overflow the longest delay.
  it.each([0, -1, Number.NaN, Number.POSITIVE_INFINITY, 2 ** 32])(
    'gives a request more than a few milliseconds when deadlineMs is %s',
    async (deadlineMs) => {
      const transport = answeringInParts(200, '{"count":7}', 30);
      const client = createApiClient({ baseUrl: 'http://localhost', fetch: transport.fetch, deadlineMs });

      expect(await client.countRecords(VIEW_ID)).toEqual({ ok: true, data: { count: 7 }, status: 200 });
    },
  );
});

// Every request asks for a JSON body or ProblemDetails; only the four calls that send a body
// declare it as JSON, the one media type the API accepts on them. Every request is sent in
// mode 'same-origin' with redirect 'error', so fetch keeps it on the API's origin and paths,
// refusing a redirect rather than following it (0.3.7).
describe('request headers and destination', () => {
  const ACCEPT = 'application/json, application/problem+json';
  const JSON_BODY = 'application/json';

  interface Operation {
    readonly call: (client: ApiClient) => Promise<unknown>;
    /** The request's Content-Type; `null` for a call that sends no body. */
    readonly contentType: string | null;
  }

  // Keyed by every member of ApiClient, so an operation the client gains fails tsc until it is listed.
  const OPERATIONS: Readonly<Record<keyof ApiClient, Operation>> = {
    listForms: { call: (client) => client.listForms(), contentType: null },
    getForm: { call: (client) => client.getForm(VIEW_ID), contentType: null },
    listRecords: { call: (client) => client.listRecords(VIEW_ID, { q: 'Smith' }), contentType: null },
    countRecords: { call: (client) => client.countRecords(VIEW_ID), contentType: null },
    getRecordAt: { call: (client) => client.getRecordAt(VIEW_ID, 1), contentType: null },
    getRecord: { call: (client) => client.getRecord(VIEW_ID, NEW_RECORD_ID), contentType: null },
    getRecordPosition: { call: (client) => client.getRecordPosition(VIEW_ID, NEW_RECORD_ID), contentType: null },
    createRecord: { call: (client) => client.createRecord(VIEW_ID, { [TEXT]: 'Smith' }), contentType: JSON_BODY },
    updateRecord: {
      call: (client) => client.updateRecord(VIEW_ID, NEW_RECORD_ID, { [TEXT]: 'Jones' }),
      contentType: JSON_BODY,
    },
    deleteRecord: { call: (client) => client.deleteRecord(VIEW_ID, NEW_RECORD_ID), contentType: null },
    searchRecords: { call: (client) => client.searchRecords(VIEW_ID, { criteria: [] }), contentType: JSON_BODY },
    raiseFieldEvent: {
      call: (client) => client.raiseFieldEvent(VIEW_ID, { event: 'FieldAfter', fieldName: TEXT, values: {} }),
      contentType: JSON_BODY,
    },
    getHealth: { call: (client) => client.getHealth(), contentType: null },
  };

  it.each(Object.entries(OPERATIONS))(
    '%s asks for JSON or ProblemDetails, declares a JSON body only when it sends one, and is sent same-origin with redirects refused',
    async (_name, { call, contentType }) => {
      const requests: Request[] = [];
      const fetchRecording = (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
        requests.push(input instanceof Request ? input : new Request(input, init));
        return Promise.resolve(new Response(null, { status: 204 }));
      };

      await call(createApiClient({ baseUrl: 'http://localhost', fetch: fetchRecording }));

      expect(requests).toHaveLength(1);
      const [request] = requests;
      expect(request.headers.get('Accept')).toBe(ACCEPT);
      expect(request.headers.get('Content-Type')).toBe(contentType);
      expect(request.body === null).toBe(contentType === null);
      expect(request.mode).toBe('same-origin');
      expect(request.redirect).toBe('error');
    },
  );
});

// A save whose outcome is unknown stays unknown until a save of the draft succeeds: the
// consent Save again gives covers the one request it sends (0.3.4.2).
describe('an uncertain save blocks every save but Save again (0.3.4.2)', () => {
  /**
   * A 2xx JSON response that does not acknowledge the write: a body that is not its complete
   * Record DTO, or a status other than the write's contracted one.
   */
  function malformedSuccess(status: number, body: unknown): Response {
    return new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } });
  }

  it.each(RECORD_WRITE_METHODS)(
    'reports a %s of unknown outcome as saveBlocked until Save again succeeds',
    async (method) => {
      const draft = mountDirtyDraft(method);
      const { api, result } = draft;
      expect(result.current.saveBlocked).toBe(false);
      api.reply(problem(503, { writeOutcome: 'unknown' }));
      expect(await perform(() => result.current.save())).toBe(false);

      expect(result.current.saveBlocked).toBe(true);
      // A Save again the server also leaves in doubt keeps it blocked.
      api.reply(networkFailure);
      expect(await perform(() => result.current.saveAgain())).toBe(false);
      expect(result.current.saveBlocked).toBe(true);

      api.reply(draft.success());
      expect(await perform(() => result.current.saveAgain())).toBe(true);
      expect(result.current.saveBlocked).toBe(false);
      expect(api.lines()).toEqual([draft.line, draft.line, draft.line]);
    },
  );

  it('refuses a blocked save at once: the banner is shown again as a new banner, and no save is pending', async () => {
    const { api, result, record } = mountExisting();
    edit(result, TEXT, 'Jones');
    api.reply(problem(503, { writeOutcome: 'unknown' }));
    expect(await perform(() => result.current.save())).toBe(false);
    const shown = bannerOf(result.current);
    run(() => {
      result.current.dismissBanner();
    });
    expect(result.current.banner).toBeNull();

    const refusals: Promise<boolean>[] = [];
    run(() => {
      refusals.push(result.current.save());
    });

    // Before any drain or request: no save started, so nothing reports Saving… meanwhile.
    expect(result.current.savePending).toBe(false);
    expect(result.current.saving).toBe(false);
    const again = bannerOf(result.current);
    expect(again).not.toBe(shown);
    expect(again).toEqual(shown);
    expect(await perform(() => Promise.all(refusals))).toEqual([false]);
    expect(result.current.savePending).toBe(false);
    expect(api.lines()).toEqual([`PUT ${recordPath(record)}`]);
  });

  it('keeps a PUT uncertain when Save again stops at client validation', async () => {
    const { api, result, record, onSaved } = mountExisting();
    const line = `PUT ${recordPath(record)}`;
    edit(result, TEXT, 'Jones');
    api.reply(problem(503, { writeOutcome: 'unknown' }));
    expect(await perform(() => result.current.save())).toBe(false);
    expect(bannerOf(result.current).kind).toBe('uncertainWrite');

    // Longer than the field allows, so Save again stops before sending anything.
    edit(result, ZIP.name, '1'.repeat(ZIP_MAX_LENGTH + 1));
    expect(await perform(() => result.current.saveAgain())).toBe(false);

    expect(api.lines()).toEqual([line]);
    expect(Object.keys(result.current.fieldErrors)).toEqual([ZIP.name]);
    const stopped = bannerOf(result.current);
    expect(stopped.kind).toBe('uncertainWrite');
    expect(stopped.actions).toEqual(['saveAgain']);
    expect(stopped.message).toMatch(MAY_ALREADY_BE_APPLIED);
    expect(stopped.message).toMatch(LIST_CANNOT_PROVE);

    // Correcting the field gives neither an ordinary save nor leaving the right to resend.
    const zip = '1'.repeat(ZIP_MAX_LENGTH);
    edit(result, ZIP.name, zip);
    expect(result.current.fieldErrors).toEqual({});
    expect(await perform(() => result.current.save())).toBe(false);
    expect(await perform(() => result.current.leave())).toBe(false);
    expect(api.lines()).toEqual([line]);
    expect(bannerOf(result.current).actions).toEqual(['saveAgain']);
    expect(result.current.isDirty).toBe(true);
    expect(onSaved).not.toHaveBeenCalled();

    const body: Values = { [TEXT]: 'Jones', [ZIP.name]: zip };
    const saved = savedCopy(record, body);
    api.reply(jsonResponse(200, saved));
    expect(await perform(() => result.current.saveAgain())).toBe(true);

    expect(api.lines()).toEqual([line, line]);
    expect(sentValues(api.calls[1])).toEqual(body);
    expect(onSaved).toHaveBeenCalledTimes(1);
    expect(onSaved).toHaveBeenCalledWith(saved);
    expect(result.current.banner).toBeNull();

    // Once a save of the draft succeeded, an ordinary save sends again.
    edit(result, OTHER_TEXT, 'Brown');
    api.reply(jsonResponse(200, savedCopy(saved, { [OTHER_TEXT]: 'Brown' })));
    expect(await perform(() => result.current.save())).toBe(true);
    expect(api.lines()).toEqual([line, line, line]);
    expect(sentValues(api.calls[2])).toEqual({ [OTHER_TEXT]: 'Brown' });
  });

  it('keeps a create uncertain when the server refuses Save again with field messages', async () => {
    const { api, result, onSaved } = mountNew();
    const line = `POST ${RECORDS_PATH}`;
    edit(result, TEXT, 'Smith');
    api.reply(networkFailure);
    expect(await perform(() => result.current.save())).toBe(false);
    expect(bannerOf(result.current).kind).toBe('uncertainWrite');

    const message = 'Last Name is not accepted.';
    api.reply(problem(400, { detail: 'One or more validation errors occurred.', errors: { [TEXT]: [message] } }));
    expect(await perform(() => result.current.saveAgain())).toBe(false);

    expect(api.lines()).toEqual([line, line]);
    expect(result.current.fieldErrors).toEqual({ [TEXT]: [message] });
    const refused = bannerOf(result.current);
    expect(refused.kind).toBe('uncertainWrite');
    expect(refused.isCreate).toBe(true);
    expect(refused.actions).toEqual(['saveAgain']);
    expect(refused.message).toMatch(MAY_ALREADY_BE_APPLIED);
    expect(refused.message).toMatch(DUPLICATE_RECORD);

    // The first create may still have been applied, so a corrected draft is not resent either.
    edit(result, TEXT, 'Smyth');
    expect(await perform(() => result.current.save())).toBe(false);
    expect(await perform(() => result.current.leave())).toBe(false);
    expect(api.lines()).toEqual([line, line]);
    expect(bannerOf(result.current).actions).toEqual(['saveAgain']);
    expect(result.current.values[TEXT]).toBe('Smyth');
    expect(onSaved).not.toHaveBeenCalled();

    const body: Values = { [TEXT]: 'Smyth', ...uncheckedCheckboxes() };
    const created = createdRecord(body);
    api.reply(jsonResponse(201, created));
    expect(await perform(() => result.current.saveAgain())).toBe(true);

    expect(api.lines()).toEqual([line, line, line]);
    expect(sentValues(api.calls[2])).toEqual(body);
    expect(onSaved).toHaveBeenCalledTimes(1);
    expect(onSaved).toHaveBeenCalledWith(created);
  });

  /** A Save again that the server answers with a 500 or 503 reporting notWritten, per method. */
  const NOT_WRITTEN_REPLAYS = RECORD_WRITE_METHODS.flatMap((method) =>
    OUTCOME_STATUSES.map((status) => ({ method, status })),
  );

  it.each(NOT_WRITTEN_REPLAYS)(
    'offers no Retry when Save again of an uncertain $method gets a $status reporting notWritten',
    async ({ method, status }) => {
      const draft = mountDirtyDraft(method);
      const { api, result, onSaved } = draft;
      api.reply(problem(503, { writeOutcome: 'unknown' }));
      expect(await perform(() => result.current.save())).toBe(false);
      expect(bannerOf(result.current).kind).toBe('uncertainWrite');

      // The replay wrote nothing, but the first save may still have been applied.
      api.reply(problem(status, { writeOutcome: 'notWritten' }));
      expect(await perform(() => result.current.saveAgain())).toBe(false);

      expect(api.lines()).toEqual([draft.line, draft.line]);
      const banner = bannerOf(result.current);
      expect(banner.kind).toBe('uncertainWrite');
      expect(banner.method).toBe(method);
      expect(banner.isCreate).toBe(method === 'POST');
      expect(banner.actions).toEqual(['saveAgain']);
      expect(banner.message).toMatch(MAY_ALREADY_BE_APPLIED);
      expect(banner.message).toMatch(LIST_CANNOT_PROVE);
      if (method === 'POST') {
        expect(banner.message).toMatch(DUPLICATE_RECORD);
      } else {
        expect(banner.message).not.toMatch(/duplicate/i);
      }

      // Neither Retry nor an ordinary save or leave sends it a third time, and the draft stays.
      await perform(() => result.current.retry());
      expect(await perform(() => result.current.save())).toBe(false);
      expect(await perform(() => result.current.leave())).toBe(false);
      expect(api.lines()).toEqual([draft.line, draft.line]);
      expect(bannerOf(result.current).actions).toEqual(['saveAgain']);
      expect(result.current.values[TEXT]).toBe(draft.typed);
      expect(result.current.isDirty).toBe(true);
      expect(onSaved).not.toHaveBeenCalled();

      // A further Save again sends the same request exactly once more.
      api.reply(draft.success());
      expect(await perform(() => result.current.saveAgain())).toBe(true);

      expect(api.lines()).toEqual([draft.line, draft.line, draft.line]);
      for (const call of api.calls) {
        expect(sentValues(call)).toEqual(draft.body);
      }
      expect(onSaved).toHaveBeenCalledTimes(1);
      expect(onSaved).toHaveBeenCalledWith(draft.saved);
      expect(result.current.banner).toBeNull();
    },
  );

  it.each(RECORD_WRITE_METHODS)(
    'reports the latest failure when Save again of an uncertain %s gets a 503 reporting notWritten',
    async (method) => {
      const firstTrace = '00-1111111111111111111111111111aaaa-111111111111aaaa-00';
      const latestTrace = '00-2222222222222222222222222222bbbb-222222222222bbbb-00';
      const draft = mountDirtyDraft(method);
      const { api, result } = draft;
      api.reply(problem(500, { writeOutcome: 'written', traceId: firstTrace }));
      expect(await perform(() => result.current.save())).toBe(false);
      expect(bannerOf(result.current).traceId).toBe(firstTrace);

      api.reply(problem(503, { writeOutcome: 'notWritten', traceId: latestTrace }));
      expect(await perform(() => result.current.saveAgain())).toBe(false);

      // The latest request's trace id and cause, while the first save may still have been applied.
      const banner = bannerOf(result.current);
      expect(banner.kind).toBe('uncertainWrite');
      expect(banner.actions).toEqual(['saveAgain']);
      expect(banner.traceId).toBe(latestTrace);
      expect(banner.error.status).toBe(503);
      expect(banner.error.writeOutcome).toBe('written');
      expect(banner.message.startsWith(`${errorMessages.unavailableWithoutDraft} `)).toBe(true);
      expect(banner.message).toMatch(MAY_ALREADY_BE_APPLIED);

      // A refused save shows the latest failure again, and sends nothing.
      run(() => {
        result.current.dismissBanner();
      });
      expect(await perform(() => result.current.save())).toBe(false);
      const refused = bannerOf(result.current);
      expect(refused.kind).toBe('uncertainWrite');
      expect(refused.actions).toEqual(['saveAgain']);
      expect(refused.traceId).toBe(latestTrace);
      expect(api.lines()).toEqual([draft.line, draft.line]);
    },
  );

  it.each([
    { status: 201, name: 'null', body: null },
    {
      status: 201,
      name: 'a record without values',
      body: { globalRecordId: NEW_RECORD_ID, uniqueKey: 4, recStatus: 1, position: 4, count: 4 },
    },
    // The Record DTO carries every data field, so a record holding none of them is not the created one.
    { status: 201, name: 'a record whose values hold no data field', body: { ...createdRecord({}), values: {} } },
    // POST is acknowledged by 201 alone; another 2xx proves nothing, whatever its body.
    {
      status: 202,
      name: 'the complete created record',
      body: createdRecord({ [TEXT]: 'Smith', ...uncheckedCheckboxes() }),
    },
  ])('treats a $status whose body is $name as a create that may have been applied', async ({ status, body }) => {
    const { api, result, onSaved } = mountNew();
    const line = `POST ${RECORDS_PATH}`;
    edit(result, TEXT, 'Smith');
    api.reply(malformedSuccess(status, body));

    expect(await perform(() => result.current.save())).toBe(false);

    const banner = bannerOf(result.current);
    expect(banner.kind).toBe('uncertainWrite');
    expect(banner.method).toBe('POST');
    expect(banner.isCreate).toBe(true);
    expect(banner.actions).toEqual(['saveAgain']);
    expect(banner.message).toMatch(MAY_ALREADY_BE_APPLIED);
    expect(banner.message).toMatch(LIST_CANNOT_PROVE);
    expect(banner.message).toMatch(DUPLICATE_RECORD);
    expect(onSaved).not.toHaveBeenCalled();
    expect(result.current.values[TEXT]).toBe('Smith');
    expect(result.current.isDirty).toBe(true);

    // Neither Retry nor an ordinary save or leave repeats the create.
    await perform(() => result.current.retry());
    expect(await perform(() => result.current.save())).toBe(false);
    expect(await perform(() => result.current.leave())).toBe(false);
    expect(api.lines()).toEqual([line]);

    const created = createdRecord({ [TEXT]: 'Smith', ...uncheckedCheckboxes() });
    api.reply(jsonResponse(201, created));
    expect(await perform(() => result.current.saveAgain())).toBe(true);

    expect(api.lines()).toEqual([line, line]);
    expect(sentValues(api.calls[1])).toEqual(sentValues(api.calls[0]));
    expect(onSaved).toHaveBeenCalledTimes(1);
    expect(onSaved).toHaveBeenCalledWith(created);
  });

  it.each([
    // The Record DTO carries every data field, so a record holding only the sent one is not the saved row.
    {
      status: 200,
      name: 'a record whose values lack the other data fields',
      body: (record: RecordDto): RecordDto => ({ ...record, values: { [TEXT]: 'Jones' } }),
    },
    {
      status: 200,
      name: 'another record',
      body: (record: RecordDto): RecordDto => ({
        ...savedCopy(record, { [TEXT]: 'Jones' }),
        globalRecordId: '3f2b8c1e-5d4a-4c1b-9e7f-000000000002',
      }),
    },
    // PUT is acknowledged by 200 alone; another 2xx proves nothing, whatever its body.
    {
      status: 201,
      name: 'the complete saved record',
      body: (record: RecordDto): RecordDto => savedCopy(record, { [TEXT]: 'Jones' }),
    },
  ])('treats a $status whose body is $name as an update that may have been applied', async ({ status, body }) => {
    const { api, result, record, onSaved } = mountExisting();
    const line = `PUT ${recordPath(record)}`;
    edit(result, TEXT, 'Jones');
    api.reply(malformedSuccess(status, body(record)));

    expect(await perform(() => result.current.save())).toBe(false);

    const banner = bannerOf(result.current);
    expect(banner.kind).toBe('uncertainWrite');
    expect(banner.method).toBe('PUT');
    expect(banner.isCreate).toBe(false);
    expect(banner.actions).toEqual(['saveAgain']);
    expect(banner.message).toMatch(MAY_ALREADY_BE_APPLIED);
    expect(banner.message).toMatch(LIST_CANNOT_PROVE);
    expect(banner.message).not.toMatch(/duplicate/i);
    expect(onSaved).not.toHaveBeenCalled();
    // Nothing of the body is adopted: the draft keeps the edit and every value it held.
    expect(result.current.values[TEXT]).toBe('Jones');
    expect(result.current.values[OTHER_TEXT]).toBe('Other');
    expect(result.current.isDirty).toBe(true);

    // Neither Retry nor an ordinary save or leave repeats the update.
    await perform(() => result.current.retry());
    expect(await perform(() => result.current.save())).toBe(false);
    expect(await perform(() => result.current.leave())).toBe(false);
    expect(api.lines()).toEqual([line]);

    const saved = savedCopy(record, { [TEXT]: 'Jones' });
    api.reply(jsonResponse(200, saved));
    expect(await perform(() => result.current.saveAgain())).toBe(true);

    expect(api.lines()).toEqual([line, line]);
    expect(sentValues(api.calls[0])).toEqual({ [TEXT]: 'Jones' });
    expect(sentValues(api.calls[1])).toEqual({ [TEXT]: 'Jones' });
    expect(onSaved).toHaveBeenCalledTimes(1);
    expect(onSaved).toHaveBeenCalledWith(saved);
    expect(result.current.banner).toBeNull();
  });
});

// A response whose status arrived but whose body cannot be used is a failure of that
// status, never the no-response case, and its body states no write outcome (0.3.4.2).
describe('a response whose body cannot be read (0.3.4.2)', () => {
  /** A response whose status and first body bytes arrive before its body stream fails with `failure`. */
  function interruptedBody(status: number, failure: Error = new TypeError('terminated')): Response {
    let started = false;
    const body = new ReadableStream<Uint8Array>({
      pull(stream) {
        if (!started) {
          started = true;
          stream.enqueue(new TextEncoder().encode('{"globalRecordId":"'));
          return;
        }
        stream.error(failure);
      },
    });
    return new Response(body, { status, headers: { 'content-type': 'application/json' } });
  }

  function textResponse(status: number, contentType: string, text: string): Response {
    return new Response(text, { status, headers: { 'content-type': contentType } });
  }

  const HTML_PAGE = '<html><body>proxy page</body></html>';

  /** A body stream failure named AbortError that no abort of the caller caused. */
  function strayAbortError(): DOMException {
    return new DOMException('The body stream was aborted.', 'AbortError');
  }

  /**
   * Per method: its contracted 2xx with a body that is not JSON or is cut off, and a cut-off
   * 503. A cut-off body is cut off with a network error, or with an AbortError the caller did
   * not cause, which only the caller's own abort makes an abort.
   */
  const UNREADABLE_SAVES = RECORD_WRITE_METHODS.flatMap((method) => {
    const success = method === 'POST' ? 201 : 200;
    return [
      { method, status: success, kind: 'server', failure: 'a text/plain body', reply: () => textResponse(success, 'text/plain', 'OK') },
      { method, status: success, kind: 'server', failure: 'an HTML page', reply: () => textResponse(success, 'text/html', HTML_PAGE) },
      { method, status: success, kind: 'server', failure: 'a body cut off mid-stream', reply: () => interruptedBody(success) },
      { method, status: 503, kind: 'unavailable', failure: 'a body cut off mid-stream', reply: () => interruptedBody(503) },
      {
        method,
        status: success,
        kind: 'server',
        failure: 'a body cut off by a stray AbortError',
        reply: () => interruptedBody(success, strayAbortError()),
      },
      {
        method,
        status: 503,
        kind: 'unavailable',
        failure: 'a body cut off by a stray AbortError',
        reply: () => interruptedBody(503, strayAbortError()),
      },
    ];
  });

  it.each(UNREADABLE_SAVES)(
    'keeps the $status of a $method answered with $failure, and offers only Save again',
    async ({ method, status, kind, reply }) => {
      const draft = mountDirtyDraft(method);
      const { api, result, onSaved } = draft;
      api.reply(reply());

      expect(await perform(() => result.current.save())).toBe(false);

      const banner = bannerOf(result.current);
      expect(banner.error).toMatchObject({ kind, status, traceId: null, writeOutcome: null });
      expect(banner.kind).toBe('uncertainWrite');
      expect(banner.method).toBe(method);
      expect(banner.actions).toEqual(['saveAgain']);
      expect(banner.message).toMatch(MAY_ALREADY_BE_APPLIED);
      expect(result.current.values[TEXT]).toBe(draft.typed);
      expect(result.current.isDirty).toBe(true);
      expect(onSaved).not.toHaveBeenCalled();

      // The save may have been applied, so Retry does not resend it; Save again sends it once more.
      await perform(() => result.current.retry());
      expect(api.lines()).toEqual([draft.line]);
      api.reply(draft.success());
      expect(await perform(() => result.current.saveAgain())).toBe(true);

      expect(api.lines()).toEqual([draft.line, draft.line]);
      expect(sentValues(api.calls[1])).toEqual(draft.body);
      expect(onSaved).toHaveBeenCalledTimes(1);
      expect(onSaved).toHaveBeenCalledWith(draft.saved);
    },
  );

  it.each<[string, () => Promise<Response>, Record<string, unknown>]>([
    ['a 200 whose body is cut off', () => Promise.resolve(interruptedBody(200)), { kind: 'server', status: 200, traceId: null }],
    ['a 503 whose body is cut off', () => Promise.resolve(interruptedBody(503)), { kind: 'unavailable', status: 503, traceId: null }],
    [
      'a 503 whose body is cut off by an AbortError the caller did not cause',
      () => Promise.resolve(interruptedBody(503, strayAbortError())),
      { kind: 'unavailable', status: 503, traceId: null },
    ],
    ['a 200 HTML page', () => Promise.resolve(textResponse(200, 'text/html', HTML_PAGE)), { kind: 'server', status: 200, traceId: null }],
    // A null body status has no body whatever its headers advertise, so its status still decides.
    ['a 205 with a Content-Length of 5', () => Promise.resolve(nullBodyAdvertisingFive(205)), { kind: 'server', status: 205, traceId: null }],
    ['a 304 with a Content-Length of 5', () => Promise.resolve(nullBodyAdvertisingFive(304)), { kind: 'server', status: 304, traceId: null }],
    // A readable error body is still parsed as ProblemDetails.
    ['a 404 ProblemDetails', () => Promise.resolve(problem(404)), { kind: 'notFound', status: 404, traceId: TRACE_ID }],
    // Only a request that got no response at all is a network failure.
    ['no response', networkFailure, { kind: 'network', status: null, traceId: null }],
    // A redirect is no response from the API, whatever it leads to, a well-formed record included.
    [
      'a record its transport reached by following a redirect',
      () => Promise.resolve(asRedirected(jsonResponse(200, recordFixture()))),
      { kind: 'network', status: null, traceId: null },
    ],
    ['an opaque redirect', () => Promise.resolve(opaqueRedirect()), { kind: 'network', status: null, traceId: null }],
    ['a 307 its transport did not follow', () => Promise.resolve(temporaryRedirect()), { kind: 'network', status: null, traceId: null }],
  ])('settles a read answered with %s as that failure', async (_case, answer, expected) => {
    const client = createApiClient({ baseUrl: 'http://localhost', fetch: answer });

    const read = await client.getRecord(VIEW_ID, 'g');

    expect(read.ok).toBe(false);
    expect(read.ok ? null : read.error).toMatchObject({ ...expected, writeOutcome: null });
  });

  it.each<[string, DOMException | undefined, string]>([
    ['without a reason', undefined, 'AbortError'],
    ['with a deadline reason', new DOMException('The deadline passed.', 'TimeoutError'), 'TimeoutError'],
  ])('rejects a call aborted %s while its body is read with the abort reason', async (_case, reason, name) => {
    const controller = new AbortController();
    const client = createApiClient({
      baseUrl: 'http://localhost',
      // As fetch does, an abort during the body fails the body stream with the abort's reason.
      fetch: (input: RequestInfo | URL, init?: RequestInit) => {
        const request = input instanceof Request ? input : new Request(input, init);
        let started = false;
        const body = new ReadableStream<Uint8Array>({
          pull(stream) {
            if (!started) {
              started = true;
              stream.enqueue(new TextEncoder().encode('{"globalRecordId":"'));
              return;
            }
            request.signal.addEventListener('abort', () => {
              stream.error(request.signal.reason);
            });
            controller.abort(reason);
          },
        });
        return Promise.resolve(new Response(body, { status: 200, headers: { 'content-type': 'application/json' } }));
      },
    });

    const failure = await client.getRecord(VIEW_ID, 'g', controller.signal).then(
      () => null,
      (e: unknown) => e,
    );

    expect(failure).not.toBeNull();
    expect(failure).toBe(controller.signal.reason);
    expect(failure).toMatchObject({ name });
  });
});

describe('delete (C-6)', () => {
  it('cancel closes the dialog without a PUT or a DELETE and keeps the draft', () => {
    const { api, result } = mountExisting();
    edit(result, TEXT, 'Jones');
    run(() => {
      result.current.requestDelete();
    });
    expect(result.current.deleteDialogOpen).toBe(true);

    run(() => {
      result.current.cancelDelete();
    });

    expect(result.current.deleteDialogOpen).toBe(false);
    expect(api.calls).toEqual([]);
    expect(result.current.values[TEXT]).toBe('Jones');
    expect(result.current.isDirty).toBe(true);
  });

  it.each([
    { name: 'a 204', reply: noContent },
    // A 204 has no body whatever its headers advertise, so a stray Content-Length changes nothing.
    { name: 'a 204 with a Content-Length of 5', reply: (): Response => nullBodyAdvertisingFive(204) },
  ])('confirm discards the edits and sends exactly one DELETE, with no PUT before it, completed by $name', async ({ reply }) => {
    const { api, result, record, onDeleted, onSaved } = mountExisting();
    edit(result, TEXT, 'Jones');
    run(() => {
      result.current.requestDelete();
    });
    api.reply(reply());

    await perform(() => result.current.confirmDelete());

    expect(api.lines()).toEqual([`DELETE ${recordPath(record)}`]);
    expect(api.calls[0]?.body).toBeUndefined();
    expect(onDeleted).toHaveBeenCalledTimes(1);
    expect(onSaved).not.toHaveBeenCalled();
    expect(result.current.values).toEqual(record.values);
    expect(result.current.isDirty).toBe(false);
    expect(result.current.deleteDialogOpen).toBe(false);
    expect(result.current.deletedReadOnly).toBe(true);
  });

  it('never marks a record just deleted read-only while navigation replaces it, and does once a failed landing leaves it on screen', async () => {
    const definition = createFormDefinitionFixture();
    const api = createFakeApi();
    const client = createApiClient({ baseUrl: 'http://localhost', fetch: api.fetch });
    const record = storedRecord();
    const next = recordFixture({
      globalRecordId: '3f2b8c1e-5d4a-4c1b-9e7f-000000000002',
      uniqueKey: 2,
      position: 1,
      values: { [TEXT]: 'Next' },
    });
    /** `deletedReadOnly` of every render from the confirmation on. */
    const shownDeleted: boolean[] = [];
    let recording = false;
    const { result } = renderHook(() => {
      // Navigation's part, as FormView wires it: afterDelete starts the landing on the next
      // record in the same update as the 204, and that landing installs a new generation.
      const [navigation, setNavigation] = useState({ record, recordGeneration: 1, transitioning: false });
      const editor = useRecordEditor({
        client,
        viewId: definition.viewId,
        definition,
        record: navigation.record,
        isNew: false,
        recordGeneration: navigation.recordGeneration,
        transitioning: navigation.transitioning,
        onSaved: () => undefined,
        onDeleted: () => {
          setNavigation((current) => ({ ...current, transitioning: true }));
        },
        onConflict: () => undefined,
        onDiscardToNearest: () => undefined,
      });
      if (recording) {
        shownDeleted.push(editor.deletedReadOnly);
      }
      return { editor, setNavigation };
    });
    /** Every guarded action of the record just deleted: none sends a request or changes the draft. */
    async function expectClosed(): Promise<void> {
      expect(await perform(() => result.current.editor.save())).toBe(true);
      run(() => {
        result.current.editor.setValue(TEXT, 'Brown');
        result.current.editor.requestDelete();
      });
      expect(result.current.editor.values).toEqual(record.values);
      expect(result.current.editor.isDirty).toBe(false);
      expect(result.current.editor.deleteDialogOpen).toBe(false);
    }

    run(() => {
      result.current.editor.setValue(TEXT, 'Jones');
      result.current.editor.requestDelete();
    });
    api.reply(noContent());
    recording = true;
    await perform(() => result.current.editor.confirmDelete());

    // During the landing the draft is frozen, never protected, and never shown as deleted.
    expect(shownDeleted.length).toBeGreaterThan(0);
    expect(shownDeleted).not.toContain(true);
    expect(result.current.editor.frozen).toBe(true);
    for (const field of allFields().filter(isWritableDataField)) {
      expect(result.current.editor.isProtected(field.name), field.name).toBe(false);
      expect(result.current.editor.isDisabled(field.name), field.name).toBe(true);
    }
    await expectClosed();

    // The landing failed and the record stays on screen: it is deleted and read-only.
    run(() => {
      result.current.setNavigation((current) => ({ ...current, transitioning: false }));
    });
    expect(result.current.editor.deletedReadOnly).toBe(true);
    for (const field of allFields()) {
      expect(result.current.editor.isProtected(field.name), field.name).toBe(true);
    }
    await expectClosed();

    // The next record installed by navigation is a clean draft that can be edited and deleted.
    run(() => {
      result.current.setNavigation({ record: next, recordGeneration: 2, transitioning: false });
    });
    expect(result.current.editor.deletedReadOnly).toBe(false);
    expect(result.current.editor.values).toEqual(next.values);
    for (const field of allFields().filter(isWritableDataField)) {
      expect(result.current.editor.isProtected(field.name), field.name).toBe(false);
    }
    run(() => {
      result.current.editor.requestDelete();
    });
    expect(result.current.editor.deleteDialogOpen).toBe(true);
    expect(api.lines()).toEqual([`DELETE ${recordPath(record)}`]);
  });

  it.each<[string, readonly (readonly [string, FieldValue])[]]>([
    ['an untouched new record', []],
    [
      'a new record whose one entry is a checkbox checked and unchecked again',
      [
        [CHECKBOX, true],
        [CHECKBOX, false],
      ],
    ],
  ])('asks nothing on Delete of %s, which holds nothing to discard', (_case, entries) => {
    const { api, result } = mountNew();
    for (const [name, value] of entries) {
      edit(result, name, value);
    }

    run(() => {
      result.current.requestDelete();
    });

    expect(result.current.deleteDialogOpen).toBe(false);
    expect(result.current.isDirty).toBe(false);
    expect(api.calls).toEqual([]);
  });

  // DELETE is acknowledged by its bodyless 204 alone; another 2xx proves nothing, whatever its body.
  it.each([
    { status: 205, name: 'no body', reply: (): Response => new Response(null, { status: 205 }) },
    // A null body status has no body whatever its headers advertise, so its status still decides.
    { status: 205, name: 'no body but a Content-Length of 5', reply: (): Response => nullBodyAdvertisingFive(205) },
    {
      status: 200,
      name: 'a JSON body',
      reply: (): Response =>
        new Response(JSON.stringify({ deleted: true }), { status: 200, headers: { 'content-type': 'application/json' } }),
    },
  ])('reports a DELETE answered $status with $name as not done, and offers Retry', async ({ status, reply }) => {
    const { api, result, record, onDeleted } = mountExisting();
    const line = `DELETE ${recordPath(record)}`;
    run(() => {
      result.current.requestDelete();
    });
    api.reply(reply());

    await perform(() => result.current.confirmDelete());

    expect(api.lines()).toEqual([line]);
    expect(onDeleted).not.toHaveBeenCalled();
    expect(result.current.deletedReadOnly).toBe(false);
    const banner = bannerOf(result.current);
    expect(banner.kind).toBe('server');
    expect(banner.method).toBe('DELETE');
    expect(banner.actions).toEqual(['retry']);
    expect(banner.error).toMatchObject({ kind: 'server', status, writeOutcome: null });

    // Repeating a DELETE is safe, and its 204 completes the delete exactly once.
    api.reply(noContent());
    await perform(() => result.current.retry());

    expect(api.lines()).toEqual([line, line]);
    expect(onDeleted).toHaveBeenCalledTimes(1);
    expect(result.current.deletedReadOnly).toBe(true);
    expect(result.current.banner).toBeNull();
  });

  it('discards a never-saved new record without a request', async () => {
    const { api, result } = mountNew();
    edit(result, TEXT, 'Smith');
    run(() => {
      result.current.requestDelete();
    });
    expect(result.current.deleteDialogOpen).toBe(true);

    await perform(() => result.current.confirmDelete());

    expect(api.calls).toEqual([]);
    expect(result.current.values).toEqual(emptyValues());
    expect(result.current.isDirty).toBe(false);
    expect(result.current.deleteDialogOpen).toBe(false);
  });
});

describe('permission flags (P2)', () => {
  // Flags are null until the server reports them, and only `false` denies. The write's own
  // flag decides: canInsert for the POST of a new record, canUpdate for a PUT. A denied save
  // sends nothing, whether Save or leaving started it, and a 403 from the server keeps the
  // draft as well. Either banner offers Discard edits, which reverts the record in place:
  // it still exists, so no other record is opened. Permissions protect no field (0.3.6).
  type Flags = Pick<FormDefinition, 'canInsert' | 'canUpdate'>;

  /** The write of each case, and the values Discard edits restores. */
  const UPDATE = {
    write: 'an update',
    isNew: false,
    line: `PUT ${recordPath(storedRecord())}`,
    stored: storedRecord().values,
    action: 'update',
  } as const;
  const CREATE = {
    write: 'a create',
    isNew: true,
    line: `POST ${RECORDS_PATH}`,
    stored: emptyValues(),
    action: 'create',
  } as const;

  /** Mounts the editor on a definition carrying `flags`, kept as one object across rerenders. */
  function mountWithFlags(isNew: boolean, flags: Flags): Mounted {
    const definition: FormDefinition = { ...createFormDefinitionFixture(), ...flags };
    return isNew ? mount(null, true, definition) : mount(storedRecord(), false, definition);
  }

  it.each([
    { ...UPDATE, flags: { canInsert: true, canUpdate: false } },
    { ...CREATE, flags: { canInsert: false, canUpdate: true } },
  ])('refuses $write the flags deny without a request, and discards it in place', async (row) => {
    const { api, result, onSaved, onDiscardToNearest } = mountWithFlags(row.isNew, row.flags);
    edit(result, TEXT, 'Jones');

    expect(await perform(() => result.current.leave())).toBe(false);

    const banner = bannerOf(result.current);
    expect(banner.kind).toBe('forbidden');
    expect(banner.message).toBe(
      `${errorMessages.forbidden[row.action]} ${errorMessages.leaveCancelled} ${errorMessages.draftKept}`,
    );
    expect(banner.actions).toEqual(['discardEdits']);
    expect(banner.traceId).toBeNull();
    expect(banner.isCreate).toBe(row.isNew);
    expect(result.current.values[TEXT]).toBe('Jones');
    expect(result.current.isDirty).toBe(true);
    expect(result.current.isProtected(TEXT)).toBe(false);
    expect(result.current.saving).toBe(false);

    // An explicit save is refused the same way, with no leave to speak of.
    run(() => {
      result.current.dismissBanner();
    });
    expect(await perform(() => result.current.save())).toBe(false);
    expect(result.current.banner).toEqual({ ...banner, message: errorMessages.forbidden[row.action] });
    expect(api.calls).toEqual([]);

    run(() => {
      result.current.discardEdits();
    });

    expect(result.current.values).toEqual(row.stored);
    expect(result.current.isDirty).toBe(false);
    expect(result.current.banner).toBeNull();
    expect(onDiscardToNearest).not.toHaveBeenCalled();
    expect(await perform(() => result.current.leave())).toBe(true);
    expect(api.calls).toEqual([]);
    expect(onSaved).not.toHaveBeenCalled();
  });

  // Null flags restrict nothing either: the fixture reports none, so 'posts an entered value
  // with false for every unchecked writable checkbox and no protected field' and 'puts only %s'
  // above save with null flags.
  it.each([
    {
      ...UPDATE,
      flags: { canInsert: false, canUpdate: true },
      status: 200,
      saved: savedCopy(storedRecord(), { [TEXT]: 'Jones' }),
    },
    {
      ...CREATE,
      flags: { canInsert: true, canUpdate: false },
      status: 201,
      saved: createdRecord({ [TEXT]: 'Jones', ...uncheckedCheckboxes() }),
    },
  ])('sends $write its own flag allows, whatever the other flag says', async (row) => {
    const { api, result, onSaved } = mountWithFlags(row.isNew, row.flags);
    edit(result, TEXT, 'Jones');
    api.reply(jsonResponse(row.status, row.saved));

    expect(await perform(() => result.current.leave())).toBe(true);

    expect(api.lines()).toEqual([row.line]);
    expect(onSaved).toHaveBeenCalledWith(row.saved);
    expect(result.current.banner).toBeNull();
    expect(result.current.isDirty).toBe(false);
  });

  it.each([
    { ...UPDATE, flags: { canInsert: null, canUpdate: null } },
    { ...CREATE, flags: { canInsert: true, canUpdate: true } },
  ])('keeps the draft on a 403 to $write, and discards it in place', async (row) => {
    const { api, result, onSaved, onDiscardToNearest } = mountWithFlags(row.isNew, row.flags);
    edit(result, TEXT, 'Jones');
    api.reply(problem(403));

    expect(await perform(() => result.current.leave())).toBe(false);

    expect(api.lines()).toEqual([row.line]);
    const banner = bannerOf(result.current);
    expect(banner.kind).toBe('forbidden');
    expect(banner.message).toBe(
      `${errorMessages.forbidden[row.action]} ${errorMessages.leaveCancelled} ${errorMessages.draftKept}`,
    );
    expect(banner.actions).toEqual(['discardEdits']);
    expect(banner.traceId).toBe(TRACE_ID);
    expect(result.current.values[TEXT]).toBe('Jones');
    expect(result.current.isDirty).toBe(true);

    run(() => {
      result.current.discardEdits();
    });

    expect(result.current.values).toEqual(row.stored);
    expect(result.current.isDirty).toBe(false);
    expect(result.current.banner).toBeNull();
    expect(onDiscardToNearest).not.toHaveBeenCalled();
    expect(await perform(() => result.current.leave())).toBe(true);
    expect(api.lines()).toEqual([row.line]);
    expect(onSaved).not.toHaveBeenCalled();
  });
});

describe('protected state', () => {
  it('protects read-only and unsupported fields of an active record, and no writable one', () => {
    const { result } = mountExisting();

    for (const name of PROTECTED_FIELD_NAMES) {
      expect(result.current.isProtected(name)).toBe(true);
      expect(result.current.isDisabled(name)).toBe(true);
    }
    for (const field of allFields().filter(isWritableDataField)) {
      expect(result.current.isProtected(field.name)).toBe(false);
      expect(result.current.isDisabled(field.name)).toBe(false);
    }
  });

  it('protects every field of a deleted record (recStatus 0)', () => {
    const record = recordFixture({ recStatus: 0, values: { [TEXT]: 'Smith' } });
    const { api, result } = mountExisting(record);

    for (const field of allFields()) {
      expect(result.current.isProtected(field.name)).toBe(true);
      expect(result.current.isDisabled(field.name)).toBe(true);
    }
    // Protection is not a check-code flag, so ENABLE can never lift it.
    expect(result.current.checkCodeDisabled.size).toBe(0);

    edit(result, TEXT, 'Jones');
    run(() => {
      result.current.requestDelete();
    });

    expect(result.current.values[TEXT]).toBe('Smith');
    expect(result.current.isDirty).toBe(false);
    expect(result.current.deleteDialogOpen).toBe(false);
    expect(api.calls).toEqual([]);
  });

  it('replacing the record clears the dirty fields, the errors and the banner', async () => {
    const { api, result, props, rerender } = mountExisting();
    edit(result, TEXT, 'Jones');
    api.reply(problem(400, { errors: { [TEXT]: ['Last Name is not accepted.'] } }));
    expect(await perform(() => result.current.save())).toBe(false);
    edit(result, OTHER_TEXT, 'Brown');
    api.reply(problem(413));
    expect(await perform(() => result.current.save())).toBe(false);

    expect(result.current.dirty).toEqual(new Set([TEXT, OTHER_TEXT]));
    expect(Object.keys(result.current.fieldErrors)).toEqual([TEXT]);
    expect(bannerOf(result.current).kind).toBe('tooLarge');

    const next = recordFixture({
      globalRecordId: '3f2b8c1e-5d4a-4c1b-9e7f-000000000002',
      uniqueKey: 2,
      position: 2,
      values: { [TEXT]: 'Next' },
    });
    rerender({ ...props, record: next, recordGeneration: props.recordGeneration + 1 });

    expect(result.current.values).toEqual(next.values);
    expect(result.current.dirty.size).toBe(0);
    expect(result.current.isDirty).toBe(false);
    expect(result.current.fieldErrors).toEqual({});
    expect(result.current.banner).toBeNull();
    expect(api.lines()).toHaveLength(2);
  });
});

describe('leaving a record: the frozen draft (0.3.6)', () => {
  /**
   * Lets the tick pass after which the record installed at mount may raise field events,
   * so a focus, blur or click that raises none does so because the draft is frozen.
   */
  async function pastMountTick(): Promise<void> {
    await act(async () => {
      await new Promise<void>((resolve) => {
        setTimeout(resolve, 0);
      });
    });
  }

  /** Edits and field events a frozen draft must refuse; an event sent would be an unanswered request. */
  function tryToChange(result: Current): void {
    edit(result, TEXT, 'Brown');
    edit(result, OTHER_TEXT, 'Brown');
    edit(result, CHECKBOX, true);
    run(() => {
      result.current.onFieldFocus(TEXT);
      result.current.onFieldBlur(TEXT);
      result.current.onFieldClick(CHECKBOX);
    });
  }

  /**
   * A frozen draft disables every field and protects none that is not protected anyway:
   * a record being left or replaced is not read-only, so protection keeps its own state.
   */
  function expectEveryFieldFrozen(editor: RecordEditor, frozen: boolean): void {
    expect(editor.frozen).toBe(frozen);
    for (const field of allFields().filter(isWritableDataField)) {
      expect(editor.isProtected(field.name), field.name).toBe(false);
      expect(editor.isDisabled(field.name), field.name).toBe(frozen);
    }
    for (const name of PROTECTED_FIELD_NAMES) {
      expect(editor.isProtected(name), name).toBe(true);
      expect(editor.isDisabled(name), name).toBe(true);
    }
    if (frozen) {
      for (const field of allFields()) {
        expect(editor.isDisabled(field.name), field.name).toBe(true);
      }
    }
  }

  it('freezes the draft during the save leave() starts, then resolves true after exactly one PUT', async () => {
    const { api, result, record, onSaved } = mountExisting();
    await pastMountTick();
    edit(result, TEXT, 'Jones');
    const gate = api.hold();

    // Back to forms calls leave() directly, with no transition around it.
    const leaving = await startHeld(() => result.current.leave(), gate);

    expect(api.lines()).toEqual([`PUT ${recordPath(record)}`]);
    expectEveryFieldFrozen(result.current, true);
    tryToChange(result);
    expect(result.current.values).toEqual({ ...record.values, [TEXT]: 'Jones' });
    expect(result.current.dirty).toEqual(new Set([TEXT]));
    expect(leaving.outcome()).toBeUndefined();

    const saved = savedCopy(record, { [TEXT]: 'Jones' });
    await act(async () => {
      gate.release(jsonResponse(200, saved));
      await leaving.promise;
    });

    expect(leaving.outcome()).toBe(true);
    // One PUT, carrying the edit made before the leave, and no field event.
    expect(api.lines()).toEqual([`PUT ${recordPath(record)}`]);
    expect(sentValues(api.calls[0])).toEqual({ [TEXT]: 'Jones' });
    expect(onSaved).toHaveBeenCalledTimes(1);
    expect(result.current.isDirty).toBe(false);
    expect(result.current.dirty.size).toBe(0);
    expect(result.current.values).toEqual(saved.values);

    // Once leave() has resolved, the draft takes edits again.
    expectEveryFieldFrozen(result.current, false);
    edit(result, OTHER_TEXT, 'Green');
    expect(result.current.values[OTHER_TEXT]).toBe('Green');
  });

  it('saves an edit made while an explicit save was in flight before leave() resolves true', async () => {
    const { api, result, record, onSaved } = mountExisting();
    edit(result, TEXT, 'Jones');
    const gate = api.hold();
    const saving = await startHeld(() => result.current.save(), gate);

    // An explicit save does not freeze the draft, so the user keeps typing.
    expectEveryFieldFrozen(result.current, false);
    edit(result, OTHER_TEXT, 'Brown');
    expect(result.current.values[OTHER_TEXT]).toBe('Brown');

    const second = savedCopy(record, { [TEXT]: 'Jones', [OTHER_TEXT]: 'Brown' });
    api.reply(jsonResponse(200, second));
    const left = await perform(() => {
      const leaving = result.current.leave();
      gate.release(jsonResponse(200, savedCopy(record, { [TEXT]: 'Jones' })));
      return leaving;
    });

    expect(await saving.promise).toBe(true);
    expect(left).toBe(true);
    expect(api.lines()).toEqual([`PUT ${recordPath(record)}`, `PUT ${recordPath(record)}`]);
    expect(sentValues(api.calls[0])).toEqual({ [TEXT]: 'Jones' });
    expect(sentValues(api.calls[1])).toEqual({ [OTHER_TEXT]: 'Brown' });
    expect(onSaved).toHaveBeenCalledTimes(2);
    expect(onSaved).toHaveBeenLastCalledWith(second);
    expect(result.current.isDirty).toBe(false);
    expect(result.current.values).toEqual(second.values);
  });

  it('freezes the draft while navigation is transitioning, still saves it on leave, and unfreezes it after', async () => {
    const { api, result, record, props, rerender } = mountExisting();
    await pastMountTick();
    edit(result, TEXT, 'Jones');

    rerender({ ...props, transitioning: true });

    expectEveryFieldFrozen(result.current, true);
    tryToChange(result);
    expect(result.current.values).toEqual({ ...record.values, [TEXT]: 'Jones' });
    expect(result.current.dirty).toEqual(new Set([TEXT]));
    expect(api.calls).toEqual([]);
    // Frozen is not protected: the edit made before the freeze still holds the user, and
    // the leave that navigation awaits saves it.
    expect(result.current.isDirty).toBe(true);
    const saved = savedCopy(record, { [TEXT]: 'Jones' });
    api.reply(jsonResponse(200, saved));
    expect(await perform(() => result.current.leave())).toBe(true);
    expect(api.lines()).toEqual([`PUT ${recordPath(record)}`]);
    expect(sentValues(api.calls[0])).toEqual({ [TEXT]: 'Jones' });
    expectEveryFieldFrozen(result.current, true);

    rerender({ ...props, transitioning: false });

    expectEveryFieldFrozen(result.current, false);
    edit(result, OTHER_TEXT, 'Brown');
    expect(result.current.values[OTHER_TEXT]).toBe('Brown');
    expect(result.current.dirty).toEqual(new Set([OTHER_TEXT]));
    expect(api.lines()).toHaveLength(1);
  });
});

describe('health', () => {
  // GET /health publishes its 200 only as {status: 'healthy'} and its 503 as {status: 'unhealthy'}.
  // Any other 200 body, and any other OK status whatever its body, acknowledges no health, so the
  // client settles it as a server failure of that status, through the same path as every other
  // malformed OK body.

  /** A client whose every request is recorded and answered with `status` and the JSON `body`. */
  function clientAnsweringHealth(status: number, body: unknown, requests: string[]): ApiClient {
    return createApiClient({
      baseUrl: 'http://localhost',
      fetch: (input) => {
        const request = input instanceof Request ? input : new Request(input);
        requests.push(`${request.method} ${new URL(request.url).pathname}`);
        return Promise.resolve(
          new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } }),
        );
      },
    });
  }

  it('reports a 200 {status: "healthy"} as healthy', async () => {
    const requests: string[] = [];

    const result = await clientAnsweringHealth(200, { status: 'healthy' }, requests).getHealth();

    expect(result).toEqual({ ok: true, data: { status: 'healthy' }, status: 200 });
    expect(requests).toEqual(['GET /health']);
  });

  it.each<{ label: string; status: number; body: unknown; kind: string }>([
    { label: 'a 200 {status: "ok"}', status: 200, body: { status: 'ok' }, kind: 'server' },
    { label: 'a 200 {status: "unhealthy"}', status: 200, body: { status: 'unhealthy' }, kind: 'server' },
    { label: 'a 202 {status: "healthy"}', status: 202, body: { status: 'healthy' }, kind: 'server' },
    { label: 'a 203 {status: "healthy"}', status: 203, body: { status: 'healthy' }, kind: 'server' },
    { label: 'a 503 {status: "unhealthy"}', status: 503, body: { status: 'unhealthy' }, kind: 'unavailable' },
  ])('reports $label as a $kind failure of its status', async ({ status, body, kind }) => {
    const requests: string[] = [];

    const result = await clientAnsweringHealth(status, body, requests).getHealth();

    expect(result.ok).toBe(false);
    expect(result.ok ? null : { kind: result.error.kind, status: result.error.status }).toEqual({ kind, status });
    expect(requests).toEqual(['GET /health']);
  });
});

describe('exact numbers', () => {
  /** A client whose every request is answered 200 with the JSON body text `text`. */
  function clientAnswering(text: string): ApiClient {
    return createApiClient({
      baseUrl: 'http://localhost',
      fetch: () => Promise.resolve(new Response(text, { status: 200, headers: { 'content-type': 'application/json' } })),
    });
  }

  it.each<[bigint, bigint]>([
    [9007199254740993n, 9007199254740994n],
    [-9007199254740993n, -9007199254740994n],
    [9223372036854775806n, 9223372036854775807n],
  ])('reads the stored %s and saves the adjacent %s digit for digit', async (stored, edited) => {
    const record = recordFixture({ values: { [NUMBER]: stored } });
    const read = await createApiClient({
      baseUrl: 'http://localhost',
      fetch: () => Promise.resolve(jsonResponse(200, record)),
    }).getRecord(VIEW_ID, record.globalRecordId);
    if (!read.ok) {
      throw new Error('the stored record was not read');
    }
    expect(read.data.values[NUMBER]).toBe(stored);

    const { api, result, onSaved } = mountExisting(read.data);
    expect(result.current.values[NUMBER]).toBe(stored);
    edit(result, NUMBER, edited);
    api.reply(jsonResponse(200, savedCopy(read.data, { [NUMBER]: edited })));

    expect(await perform(() => result.current.save())).toBe(true);

    expect(api.lines()).toEqual([`PUT ${recordPath(record)}`]);
    expect(api.calls[0].text).toBe(`{"values":{${JSON.stringify(NUMBER)}:${edited.toString()}}}`);
    expect(onSaved).toHaveBeenCalledTimes(1);
    expect(onSaved.mock.calls[0][0].values[NUMBER]).toBe(edited);
    expect(result.current.isDirty).toBe(false);
  });

  // The server compares a number with its bounds by what the column stores: exact integer limits
  // read from the metadata text on an integer column, nearest doubles on a `float` column. The
  // client sees neither and reads every bound as its nearest double, so its feedback checks a number
  // against number bounds only and leaves a bigint or exact decimal value, and a whole bound beyond
  // the safe range, to the server's 400.

  type Bound = FieldValidation['lower'];

  /** The plain number field of the fixture with the bounds given. */
  function boundedNumber(lower: Bound, upper: Bound): FieldDefinition {
    const field = fieldByName(NUMBER);
    return { ...field, validation: { ...field.validation, lower, upper } };
  }

  /** The value typed text stands for, as the number control enters it. */
  function typed(text: string): FieldValue {
    const value = numberFromText(text);
    if (value === null) {
      throw new Error(`${text} is not a number`);
    }
    return value;
  }

  const AGE = fieldByName('Age');

  // The metadata bound 9007199254740993 as the client reads it: its nearest double, 2^53.
  const BEYOND_SAFE = 2 ** 53;

  // RecordValidatorTests reject ±9007199254740994 against the bounds ±9007199254740993 on `bigint`;
  // the client, which cannot see the column or the exact bound, leaves such comparisons to the server.
  const UNSAFE_BOUNDED = boundedNumber(-BEYOND_SAFE, BEYOND_SAFE);

  it.each<[string, FieldDefinition, FieldValue, string[]]>([
    ['a bigint above an upper bound beyond the safe range', UNSAFE_BOUNDED, 9007199254740994n, []],
    ['a bigint below a lower bound beyond the safe range', UNSAFE_BOUNDED, -9007199254740994n, []],
    ['a number below a lower bound beyond the safe range', boundedNumber(BEYOND_SAFE, null), 5, []],
    // The number mask still counts a bigint's digits, which a `float` column's double never has fewer of.
    ['a bigint beyond a number range', AGE, 9007199254740993n, [clientMessages.patternMismatch('###')]],
    ['an exact decimal beyond a number range and a number mask', AGE, typed('9007199254740992.5'), []],
    [
      'a number below a number lower bound beside an upper bound beyond the safe range',
      boundedNumber(10, BEYOND_SAFE),
      5,
      [clientMessages.rangeAtLeast('10')],
    ],
  ])('gives feedback on %s only where the server compares alike on every column', (_case, field, value, messages) => {
    expect(validateField(field, value)).toEqual(messages);
  });

  it.each<[string, Bound, Bound, number | bigint, string[] | null]>([
    // A `float` column compares 2^53 with the double of 9007199254740993, which is 2^53, and accepts it.
    ['a bigint below a float lower bound beyond the safe range', BEYOND_SAFE, null, 9007199254740992n, null],
    // A `bigint` column's upper limit is 18014398509481985, the floor of the metadata bound
    // 18014398509481985.25, whose nearest double, 18014398509481984, is the bound the client reads.
    [
      'a bigint above the nearest double of a fractional upper bound',
      null,
      2 ** 54,
      18014398509481985n,
      null,
    ],
    ['a number above an upper bound of 120', 0, 120, 121, [clientMessages.rangeBetween('0', '120')]],
  ])('saves or stops %s as the server would', async (_case, lower, upper, value, errors) => {
    const definition = withField(NUMBER, (field) => ({ ...field, validation: { ...field.validation, lower, upper } }));
    const { api, result, record } = mountExisting(storedRecord(), definition);
    edit(result, NUMBER, value);
    if (errors === null) {
      api.reply(jsonResponse(200, savedCopy(record, { [NUMBER]: value })));
    }

    expect(await perform(() => result.current.save())).toBe(errors === null);

    expect(api.calls.map((call) => call.text)).toEqual(
      errors === null ? [`{"values":{${JSON.stringify(NUMBER)}:${String(value)}}}`] : [],
    );
    expect(result.current.fieldErrors).toEqual(errors === null ? {} : { [NUMBER]: errors });
  });

  it('reads every integer beyond the safe range that a bigint column holds as its bigint, whatever its form, and every other number as a double', async () => {
    // A whole record, so the body passes the client's check of the record's contract shape.
    const text =
      '{"globalRecordId":"g","uniqueKey":1,"recStatus":1,"position":1,"count":1,' +
      '"values":{"a":9007199254740992,"b":9007199254740993,"c":-9223372036854775808,' +
      '"d":9223372036854775807,"e":1E+16,"f":9.007199254740993e15,"g":9223372036854775808,' +
      '"h":-9223372036854775809,"i":9007199254740991,"j":-9007199254740991,"k":1.5,"l":-0}}';
    const read = await clientAnswering(text).getRecord(VIEW_ID, 'any');

    expect(read.ok && read.data.values).toEqual({
      a: 9007199254740992n,
      b: 9007199254740993n,
      c: -9223372036854775808n,
      d: 9223372036854775807n,
      e: 10000000000000000n,
      f: 9007199254740993n,
      // Beyond the 64-bit range no integer column holds the value, so its double serves.
      g: 2 ** 63,
      h: -(2 ** 63),
      i: 9007199254740991,
      j: -9007199254740991,
      k: 1.5,
      l: -0,
    });
  });

  // ±9007199254740993 at every number of each response body: a field value, wherever its
  // map entry's name, is its bigint; every other number, such as a `tabIndex`, `uniqueKey`,
  // `count` or bound, is the double JSON.parse reads, ±2^53, so arithmetic on it never meets
  // a bigint. An option value is the one exception: the contract holds it to an `int32`
  // index, so the definition's option carries 0, and a larger one fails the load.
  const UNSAFE = '9007199254740993';
  const EXACT = 9007199254740993n;
  const DOUBLE = 2 ** 53;

  it.each<[string, (client: ApiClient) => Promise<ApiResult<unknown>>, string, unknown]>([
    [
      'a record',
      (client) => client.getRecord(VIEW_ID, 'g'),
      `{"globalRecordId":"g","uniqueKey":${UNSAFE},"recStatus":1,"position":${UNSAFE},"count":${UNSAFE},` +
        `"values":{"a":${UNSAFE},"count":-${UNSAFE},"tabIndex":${UNSAFE}}}`,
      {
        globalRecordId: 'g',
        uniqueKey: DOUBLE,
        recStatus: 1,
        position: DOUBLE,
        count: DOUBLE,
        values: { a: EXACT, count: -EXACT, tabIndex: EXACT },
      },
    ],
    [
      'a form definition',
      (client) => client.getForm(VIEW_ID),
      `{"viewId":${UNSAFE},"name":"F","pages":[{"pageId":${UNSAFE},"name":"P","position":${UNSAFE},"fields":[` +
        `{"name":"n","fieldType":"number","prompt":null,"tabIndex":${UNSAFE},"pageId":${UNSAFE},"unsupported":false,` +
        `"unsupportedType":null,"validation":{"required":false,"readOnly":false,"maxLength":${UNSAFE},` +
        `"lower":-${UNSAFE},"upper":${UNSAFE},"pattern":null},"options":null,"members":null},` +
        `{"name":"o","fieldType":"option","prompt":null,"tabIndex":-${UNSAFE},"pageId":${UNSAFE},"unsupported":false,` +
        `"unsupportedType":null,"validation":{"required":false,"readOnly":false,"maxLength":null,"lower":null,` +
        `"upper":null,"pattern":null},"options":[{"value":0,"label":"l"}],"members":null}]}],` +
        `"unsupportedCheckCode":[{"severity":"warning","form":"F","line":${UNSAFE},"command":"c","message":"m"}],` +
        `"canInsert":null,"canUpdate":null,"canDelete":null}`,
      {
        viewId: DOUBLE,
        name: 'F',
        pages: [
          {
            pageId: DOUBLE,
            name: 'P',
            position: DOUBLE,
            fields: [
              {
                name: 'n',
                fieldType: 'number',
                prompt: null,
                tabIndex: DOUBLE,
                pageId: DOUBLE,
                unsupported: false,
                unsupportedType: null,
                validation: { required: false, readOnly: false, maxLength: DOUBLE, lower: -DOUBLE, upper: DOUBLE, pattern: null },
                options: null,
                members: null,
              },
              {
                name: 'o',
                fieldType: 'option',
                prompt: null,
                tabIndex: -DOUBLE,
                pageId: DOUBLE,
                unsupported: false,
                unsupportedType: null,
                validation: { required: false, readOnly: false, maxLength: null, lower: null, upper: null, pattern: null },
                options: [{ value: 0, label: 'l' }],
                members: null,
              },
            ],
          },
        ],
        unsupportedCheckCode: [{ severity: 'warning', form: 'F', line: DOUBLE, command: 'c', message: 'm' }],
        canInsert: null,
        canUpdate: null,
        canDelete: null,
      },
    ],
    [
      'a records page',
      (client) => client.listRecords(VIEW_ID),
      `{"items":[{"globalRecordId":"g","uniqueKey":${UNSAFE},"recStatus":1,"values":{"a":${UNSAFE}}}],` +
        `"total":${UNSAFE},"page":${UNSAFE},"pageSize":${UNSAFE}}`,
      {
        items: [{ globalRecordId: 'g', uniqueKey: DOUBLE, recStatus: 1, values: { a: EXACT } }],
        total: DOUBLE,
        page: DOUBLE,
        pageSize: DOUBLE,
      },
    ],
    [
      'a search response',
      (client) => client.searchRecords(VIEW_ID, { criteria: [] }),
      `{"items":[{"globalRecordId":"g","uniqueKey":${UNSAFE},"recStatus":1,"position":${UNSAFE},` +
        `"values":{"a":${UNSAFE}}}],"total":${UNSAFE},"truncated":true}`,
      {
        items: [{ globalRecordId: 'g', uniqueKey: DOUBLE, recStatus: 1, position: DOUBLE, values: { a: EXACT } }],
        total: DOUBLE,
        truncated: true,
      },
    ],
    [
      'a field-event response',
      (client) => client.raiseFieldEvent(VIEW_ID, { event: 'FieldAfter', fieldName: 'a', values: {} }),
      `{"enabled":[],"disabled":[],"cleared":[],"assigned":{"a":${UNSAFE},"uniqueKey":-${UNSAFE}},"dialogs":[],` +
        `"diagnostics":[{"severity":"error","form":"F","line":${UNSAFE},"command":"c","message":"m"}]}`,
      {
        enabled: [],
        disabled: [],
        cleared: [],
        assigned: { a: EXACT, uniqueKey: -EXACT },
        dialogs: [],
        diagnostics: [{ severity: 'error', form: 'F', line: DOUBLE, command: 'c', message: 'm' }],
      },
    ],
    ['a record count', (client) => client.countRecords(VIEW_ID), `{"count":${UNSAFE}}`, { count: DOUBLE }],
    [
      'a navigation position',
      (client) => client.getRecordPosition(VIEW_ID, 'g'),
      `{"position":${UNSAFE},"count":${UNSAFE}}`,
      { position: DOUBLE, count: DOUBLE },
    ],
    [
      'the forms list',
      (client) => client.listForms(),
      `[{"viewId":${UNSAFE},"name":"F","pageCount":${UNSAFE}}]`,
      [{ viewId: DOUBLE, name: 'F', pageCount: DOUBLE }],
    ],
  ])('reads %s with its field values exact and every other number as JSON.parse does', async (_case, read, text, expected) => {
    const result = await read(clientAnswering(text));

    expect(result.ok && result.data).toEqual(expected);
  });

  // The six members of a field-event answer with no effects, which the client checks before it
  // reports the answer. A member beside them is no part of the contract and is passed on as read,
  // so a `values` member there shows the reader: its entries sit where a record's field values do.
  const NO_EFFECTS = '"enabled":[],"disabled":[],"cleared":[],"assigned":{},"dialogs":[],"diagnostics":[]';

  it.each<[string, string]>([
    ['escapes, unicode and a repeated key', `{${NO_EFFECTS},"values":{"a":"q\\"b\\\\s\\/\\u00e9\\ud83d\\ude00\\n\\t","b":"x","a":"last"}}`],
    ['members named __proto__ and constructor', `{${NO_EFFECTS},"values":{"__proto__":"own","constructor":{"__proto__":[1]}}}`],
    ['whitespace, nesting and every literal', ` \n{ ${NO_EFFECTS} , "values" : { "a" : [ 1 , -0 , 1.5e-7 , 1E+20 , -2.5E-3 , true , false , null , { } , [ ] ] } }\r\n\t`],
    ['the numbers a double holds exactly', `{${NO_EFFECTS},"values":{"a":9007199254740991,"b":-9007199254740991,"c":0,"d":1e308,"e":5e-324}}`],
  ])('reads a body with %s as JSON.parse does', async (_case, text) => {
    const read = await clientAnswering(text).raiseFieldEvent(VIEW_ID, { event: 'FieldAfter', fieldName: 'a', values: {} });
    const expected: unknown = JSON.parse(text);

    expect(read.ok && read.data).toEqual(expected);
  });

  it.each<string>([
    '{"values":{"a":01}}',
    '{"values":}',
    '{"values":[1,]}',
    '{"values":{"a":1,}}',
    '{"values":{"a" 1}}',
    '{"values":{"a":"\\x"}}',
    '{"values":{"a":"\t"}}',
    '{"values":{"a":"unterminated}}',
    '{"values":{}} trailing',
    '{"values":{"a":tru}}',
  ])('reports the malformed OK body %j as a failure of its 200, as JSON.parse rejects it', async (text) => {
    expect((): unknown => JSON.parse(text)).toThrow(SyntaxError);

    const read = await clientAnswering(text).getRecord(VIEW_ID, 'any');

    // The status arrived, so this is no lost response, and nothing of the body is used.
    expect(read.ok).toBe(false);
    expect(read.ok ? null : read.error).toMatchObject({ kind: 'server', status: 200, traceId: null, writeOutcome: null });
  });

  it.each<[string, unknown, boolean]>([
    ['the exact decimal numberFromText makes', numberFromText('2.0000000000000001'), true],
    ['an object of its shape', { kind: 'decimal', literal: '1' }, false],
    ['a frozen object of its shape', Object.freeze({ kind: 'decimal', literal: '1' }), false],
  ])('takes %s for an exact decimal: %s', (_case, value, expected) => {
    expect(isExactDecimal(value)).toBe(expected);
  });

  // A `values` map is data whatever its fields are named: text fields named `kind` and `literal`
  // that hold `decimal` and `42` are written as those two strings, never as the decimal 42 they
  // resemble, beside a real exact decimal, which is written bare as the number typed.
  const RECORD_TEXT = '{"globalRecordId":"g","uniqueKey":1,"recStatus":1,"position":1,"count":1,"values":{}}';
  const ENVELOPE_TEXT = `{${NO_EFFECTS}}`;

  interface ValuesWrite {
    readonly write: string;
    readonly send: (client: ApiClient, values: Values) => Promise<ApiResult<unknown>>;
    /** The status and JSON text the API answers the write with. */
    readonly status: number;
    readonly reply: string;
    /** The request line and body text expected for the JSON text of the values. */
    readonly request: (valuesText: string) => string;
  }

  it.each<ValuesWrite>([
    {
      write: 'a create',
      send: (client, values) => client.createRecord(VIEW_ID, values),
      status: 201,
      reply: RECORD_TEXT,
      request: (valuesText) => `POST {"values":${valuesText}}`,
    },
    {
      write: 'an update',
      send: (client, values) => client.updateRecord(VIEW_ID, 'g', values),
      status: 200,
      reply: RECORD_TEXT,
      request: (valuesText) => `PUT {"values":${valuesText}}`,
    },
    {
      write: 'a field event',
      send: (client, values) => client.raiseFieldEvent(VIEW_ID, { event: 'FieldAfter', fieldName: NUMBER, values }),
      status: 200,
      reply: ENVELOPE_TEXT,
      request: (valuesText) => `POST {"event":"FieldAfter","fieldName":${JSON.stringify(NUMBER)},"values":${valuesText}}`,
    },
  ])('writes the values of $write as a map, its fields named kind and literal as text', async (row) => {
    const { send, status, reply, request } = row;
    const sent: string[] = [];
    const client = createApiClient({
      baseUrl: 'http://localhost',
      fetch: async (input: RequestInfo | URL, init?: RequestInit) => {
        const received = input instanceof Request ? input : new Request(input, init);
        sent.push(`${received.method} ${await received.text()}`);
        return new Response(reply, { status, headers: { 'content-type': 'application/json' } });
      },
    });
    const values: Values = { kind: 'decimal', literal: '42', [NUMBER]: typed('2.0000000000000001') };

    const result = await send(client, values);

    expect(result.ok).toBe(true);
    expect(sent).toEqual([request(`{"kind":"decimal","literal":"42",${JSON.stringify(NUMBER)}:2.0000000000000001}`)]);
  });
});

// Every /api error body is a ProblemDetails whose `type` is the problem's URI reference
// (0.3.4.2), and the client keeps it as text on the ApiError. The HTTP status alone picks
// the kind, so a `type` naming another status changes nothing, and a body with no usable
// `type`, the /health 503 body, a non-JSON body and no response at all give `null`. So does
// a JSON body that is no ProblemDetails, one without a string `title` and a `status` equal
// to the HTTP status, such as a proxy's own error envelope, whatever `type` it carries.
describe('the ProblemDetails type (0.3.4.2)', () => {
  const BAD_REQUEST_TYPE = 'https://tools.ietf.org/html/rfc9110#section-15.5.1';
  const UNAVAILABLE_TYPE = 'https://tools.ietf.org/html/rfc9110#section-15.6.4';

  /** An error response carrying `body` as JSON, or as the text itself when it is a string. */
  function answer(status: number, body: unknown, contentType = 'application/problem+json'): Response {
    return new Response(typeof body === 'string' ? body : JSON.stringify(body), {
      status,
      headers: { 'content-type': contentType },
    });
  }

  /** A ProblemDetails response whose `type` member is `type`, or which has none when `undefined`. */
  function typedProblem(status: number, type: unknown, extra: Readonly<Record<string, unknown>> = {}): Response {
    return answer(status, {
      ...(type === undefined ? {} : { type }),
      title: 'A fixed title.',
      status,
      detail: 'A fixed server sentence.',
      traceId: TRACE_ID,
      ...extra,
    });
  }

  interface TypeCase {
    readonly name: string;
    readonly reply: () => Reply;
    readonly read: (client: ApiClient) => Promise<ApiResult<unknown>>;
    readonly type: string | null;
    readonly kind: ApiErrorKind;
    readonly status: number | null;
  }

  const record = storedRecord();
  const getRecord = (client: ApiClient): Promise<ApiResult<unknown>> => client.getRecord(VIEW_ID, record.globalRecordId);
  const updateRecord = (client: ApiClient): Promise<ApiResult<unknown>> =>
    client.updateRecord(VIEW_ID, record.globalRecordId, { [TEXT]: 'Jones' });

  it.each<TypeCase>([
    {
      name: 'the about:blank type of a write 503',
      reply: () => problem(503, { writeOutcome: 'notWritten' }),
      read: updateRecord,
      type: 'about:blank',
      kind: 'unavailable',
      status: 503,
    },
    {
      name: 'the RFC 9110 type of a field-level 400',
      reply: () => typedProblem(400, BAD_REQUEST_TYPE, { errors: { includeDeleted: ['A fixed message.'] } }),
      read: (client) => client.countRecords(VIEW_ID),
      type: BAD_REQUEST_TYPE,
      kind: 'validation',
      status: 400,
    },
    {
      name: 'a 404 whose type names a 503',
      reply: () => typedProblem(404, UNAVAILABLE_TYPE),
      read: getRecord,
      type: UNAVAILABLE_TYPE,
      kind: 'notFound',
      status: 404,
    },
    {
      name: 'a ProblemDetails without type',
      reply: () => typedProblem(409, undefined),
      read: updateRecord,
      type: null,
      kind: 'conflict',
      status: 409,
    },
    { name: 'a blank type', reply: () => typedProblem(404, '  '), read: getRecord, type: null, kind: 'notFound', status: 404 },
    { name: 'a numeric type', reply: () => typedProblem(500, 42), read: getRecord, type: null, kind: 'server', status: 500 },
    { name: 'a null type', reply: () => typedProblem(403, null), read: updateRecord, type: null, kind: 'forbidden', status: 403 },
    {
      name: "a proxy's JSON envelope with a type but no title or status",
      reply: () => answer(502, { type: 'gateway_error', message: 'The upstream did not answer.' }, 'application/json'),
      read: getRecord,
      type: null,
      kind: 'unavailable',
      status: 502,
    },
    {
      name: 'a typed body whose status differs from the HTTP status',
      reply: () => typedProblem(404, UNAVAILABLE_TYPE, { status: 503 }),
      read: getRecord,
      type: null,
      kind: 'notFound',
      status: 404,
    },
    {
      name: 'a typed body whose status is not an integer',
      reply: () => typedProblem(400, BAD_REQUEST_TYPE, { status: '400' }),
      read: (client) => client.countRecords(VIEW_ID),
      type: null,
      kind: 'badRequest',
      status: 400,
    },
    {
      name: 'a typed body without a title',
      reply: () => typedProblem(409, BAD_REQUEST_TYPE, { title: undefined }),
      read: updateRecord,
      type: null,
      kind: 'conflict',
      status: 409,
    },
    {
      name: 'a non-JSON error text',
      reply: () => answer(502, 'Bad Gateway', 'text/plain'),
      read: getRecord,
      type: null,
      kind: 'unavailable',
      status: 502,
    },
    {
      name: 'an empty error body',
      reply: () => new Response(null, { status: 500 }),
      read: getRecord,
      type: null,
      kind: 'server',
      status: 500,
    },
    {
      name: 'the /health 503 body',
      reply: () => answer(503, { status: 'unhealthy' }, 'application/json'),
      read: (client) => client.getHealth(),
      type: null,
      kind: 'unavailable',
      status: 503,
    },
    { name: 'no response', reply: () => networkFailure, read: getRecord, type: null, kind: 'network', status: null },
  ])('reads $name as type $type, with the kind of its status', async (row) => {
    const api = createFakeApi();
    api.reply(row.reply());

    const result = await row.read(createApiClient({ baseUrl: 'http://localhost', fetch: api.fetch }));

    expect(api.calls).toHaveLength(1);
    if (result.ok) {
      throw new Error(`${row.name} settled as a success`);
    }
    const { type, kind, status } = result.error;
    expect({ type, kind, status }).toEqual({ type: row.type, kind: row.kind, status: row.status });
  });
});
