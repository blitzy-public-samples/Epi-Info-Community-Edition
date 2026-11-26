// The record editor's write rules:
// - C-10: a new record whose only entries are unchecked checkboxes is empty and never
//   posted; a POST sends `false` for every unchecked writable checkbox.
// - C-3: a PUT carries the dirty writable fields only; read-only and unsupported fields
//   are never sent and cannot be edited.
// - Leaving a dirty record saves it first; a failed save, a 404 included, cancels the
//   move and keeps the draft, and Discard edits alone replaces it (0.3.6, 0.3.4.2).
// - The write-outcome table of 0.3.4.2: Retry resends a POST or PUT only on
//   `notWritten`; `unknown`, `written`, a missing outcome and no response offer Save
//   again only. A DELETE is always safe to resend.
// - C-6: Delete asks first; Cancel sends nothing, Confirm sends exactly one DELETE.
//
// The hook runs against the real client and error parsing; createApiClient's fetch seam
// is a fake that records every request and answers from a per-test reply queue.
import { act, renderHook } from '@testing-library/react';
import { afterEach, describe, expect, it, vi, type Mock } from 'vitest';

import { createApiClient } from '../api/client';
import { errorMessages } from '../api/errors';
import type { FieldDefinition, FieldValue, RecordDto, Values, WriteOutcome } from '../api/types';
import {
  allFields,
  createFormDefinitionFixture,
  emptyValues,
  fieldByName,
  formDefinitionFixture,
  isDataField,
  readOnlyFieldNames,
  recordFixture,
  writableCheckboxNames,
} from '../test/formDefinitionFixture';
import { clientMessages } from '../validation/clientValidation';
import { useRecordEditor, type EditorBanner, type RecordEditor, type UseRecordEditorOptions } from './useRecordEditor';

const VIEW_ID = formDefinitionFixture.viewId;
const RECORDS_PATH = `/api/forms/${VIEW_ID}/records`;
const NEW_RECORD_ID = '3f2b8c1e-5d4a-4c1b-9e7f-000000000004';
const TRACE_ID = '00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-00';

// Fields, chosen by their metadata so the cases hold whatever the fixture names them.

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

const [TEXT, OTHER_TEXT] = requireNames('plain writable text fields', PLAIN_TEXT_FIELDS, 2);
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
}

/** The answer to one request: a response, or a function that settles it later or rejects. */
type Reply = Response | (() => Promise<Response>);

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

function createFakeApi(): FakeApi {
  const calls: RecordedCall[] = [];
  const replies: Reply[] = [];
  const unanswered: string[] = [];

  const fetchFromFake = async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
    // openapi-fetch hands over the Request it built; anything else is normalised to one.
    const request = input instanceof Request ? input : new Request(input, init);
    const text = await request.clone().text();
    const body: unknown = text === '' ? undefined : JSON.parse(text);
    const call: RecordedCall = { method: request.method, path: new URL(request.url).pathname, body };
    calls.push(call);
    const next = replies.shift();
    if (next === undefined) {
      unanswered.push(lineOf(call));
      throw new Error(`no reply queued for ${lineOf(call)}`);
    }
    return next instanceof Response ? next : next();
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

function jsonResponse(status: number, body: RecordDto): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } });
}

type ProblemStatus = 400 | 404 | 409 | 413 | 500 | 503;

const PROBLEM_TITLES: Readonly<Record<ProblemStatus, string>> = {
  400: 'Bad Request',
  404: 'Not Found',
  409: 'Conflict',
  413: 'Payload Too Large',
  500: 'Internal Server Error',
  503: 'Service Unavailable',
};

interface ProblemExtra {
  readonly detail?: string;
  readonly errors?: Readonly<Record<string, readonly string[]>>;
  readonly writeOutcome?: WriteOutcome;
}

/** A ProblemDetails response, with a field-level `errors` map or a `writeOutcome` when given. */
function problem(status: ProblemStatus, extra: ProblemExtra = {}): Response {
  const body = {
    type: 'about:blank',
    title: PROBLEM_TITLES[status],
    status,
    detail: extra.detail ?? 'A fixed server sentence.',
    traceId: TRACE_ID,
    ...(extra.errors === undefined ? {} : { errors: extra.errors }),
    ...(extra.writeOutcome === undefined ? {} : { writeOutcome: extra.writeOutcome }),
  };
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/problem+json' },
  });
}

function noContent(): Response {
  return new Response(null, { status: 204 });
}

/** A request that never received a response. */
function networkFailure(): Promise<Response> {
  return Promise.reject(new TypeError('Failed to fetch'));
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
  readonly onDiscardToNearest: Mock<(notice?: string) => void>;
}

interface MountedRecord extends Mounted {
  readonly record: RecordDto;
}

function mount(record: RecordDto | null, isNew: boolean): Mounted {
  const api = createFakeApi();
  // Fresh per test, and the same object across rerenders: a new definition starts a new form.
  const definition = createFormDefinitionFixture();
  const onSaved = vi.fn<(saved: RecordDto) => void>();
  const onDeleted = vi.fn<() => void>();
  const onDiscardToNearest = vi.fn<(notice?: string) => void>();
  const props: UseRecordEditorOptions = {
    client: createApiClient({ baseUrl: 'http://localhost', fetch: api.fetch }),
    viewId: definition.viewId,
    definition,
    record,
    isNew,
    recordGeneration: 1,
    onSaved,
    onDeleted,
    onDiscardToNearest,
  };
  const { result, rerender } = renderHook((options: UseRecordEditorOptions) => useRecordEditor(options), {
    initialProps: props,
  });
  return { api, result, props, rerender, onSaved, onDeleted, onDiscardToNearest };
}

function mountNew(): Mounted {
  return mount(null, true);
}

function mountExisting(record: RecordDto = storedRecord()): MountedRecord {
  return { ...mount(record, false), record };
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
    // Nothing here raises a field event, so the editor must not send one of its own.
    expect(api.lines().filter((line) => line.includes('/checkcode/'))).toEqual([]);
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

describe('validation', () => {
  it('stops a save that fails client validation without a request', async () => {
    const { api, result } = mountExisting();
    edit(result, ZIP.name, '1'.repeat(ZIP_MAX_LENGTH + 1));

    expect(await perform(() => result.current.save())).toBe(false);

    expect(api.calls).toEqual([]);
    expect(result.current.fieldErrors).toEqual({ [ZIP.name]: [clientMessages.maxLength(ZIP_MAX_LENGTH)] });
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
    expect(banner.message).toBe(errorMessages.notFound);
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

  it('shows a record found deleted (409) read-only, with no restore', async () => {
    const { api, result } = mountExisting();
    edit(result, TEXT, 'Jones');
    api.reply(problem(409));

    expect(await perform(() => result.current.save())).toBe(false);

    expect(result.current.deletedReadOnly).toBe(true);
    const banner = bannerOf(result.current);
    expect(banner.kind).toBe('deleted');
    expect(banner.message).toBe(errorMessages.conflict);
    expect(banner.actions).toEqual([]);
    for (const field of allFields()) {
      expect(result.current.isProtected(field.name)).toBe(true);
    }
    // The edits can no longer be saved, so they no longer hold the user on the record.
    expect(result.current.isDirty).toBe(false);
    expect(await perform(() => result.current.leave())).toBe(true);
    expect(api.lines()).toHaveLength(1);
  });

  it.each([
    {
      status: 413 as const,
      writeOutcome: undefined,
      kind: 'tooLarge',
      message: errorMessages.tooLarge,
      actions: [],
    },
    {
      status: 500 as const,
      writeOutcome: 'notWritten' as const,
      kind: 'server',
      message: errorMessages.server + errorMessages.traceIdSuffix(TRACE_ID),
      actions: ['retry'],
    },
  ])('keeps the draft on a $status, in a $kind banner with the trace id', async (row) => {
    const { api, result, onSaved } = mountExisting();
    edit(result, TEXT, 'Jones');
    api.reply(problem(row.status, row.writeOutcome === undefined ? {} : { writeOutcome: row.writeOutcome }));

    expect(await perform(() => result.current.leave())).toBe(false);

    const banner = bannerOf(result.current);
    expect(banner.kind).toBe(row.kind);
    expect(banner.message).toBe(row.message);
    expect(banner.traceId).toBe(TRACE_ID);
    expect(banner.actions).toEqual(row.actions);
    expect(result.current.values[TEXT]).toBe('Jones');
    expect(result.current.isDirty).toBe(true);
    expect(onSaved).not.toHaveBeenCalled();
    expect(api.lines()).toHaveLength(1);
  });
});


describe('write outcomes and Retry (0.3.4.2)', () => {
  it('retries a PUT the server reports notWritten', async () => {
    const { api, result, record, onSaved } = mountExisting();
    edit(result, TEXT, 'Jones');
    api.reply(problem(503, { writeOutcome: 'notWritten' }));

    expect(await perform(() => result.current.save())).toBe(false);

    const banner = bannerOf(result.current);
    expect(banner.kind).toBe('unavailable');
    expect(banner.message).toBe(errorMessages.unavailable);
    expect(banner.actions).toEqual(['retry']);

    const saved = savedCopy(record, { [TEXT]: 'Jones' });
    api.reply(jsonResponse(200, saved));
    await perform(() => result.current.retry());

    expect(api.lines()).toEqual([`PUT ${recordPath(record)}`, `PUT ${recordPath(record)}`]);
    expect(sentValues(api.calls[1])).toEqual(sentValues(api.calls[0]));
    expect(onSaved).toHaveBeenCalledTimes(1);
    expect(onSaved).toHaveBeenCalledWith(saved);
    expect(result.current.banner).toBeNull();
  });

  // Every outcome but notWritten may mean the save was applied; a missing one is no proof either.
  it.each([
    { status: 503 as const, outcome: 'unknown' as const },
    { status: 500 as const, outcome: 'unknown' as const },
    { status: 503 as const, outcome: 'written' as const },
    { status: 500 as const, outcome: 'written' as const },
    { status: 503 as const, outcome: 'absent' as const },
    { status: 500 as const, outcome: 'absent' as const },
  ])('offers only Save again after a $status with writeOutcome $outcome', async ({ status, outcome }) => {
    const { api, result, record, onSaved } = mountExisting();
    edit(result, TEXT, 'Jones');
    api.reply(problem(status, outcome === 'absent' ? {} : { writeOutcome: outcome }));

    expect(await perform(() => result.current.save())).toBe(false);

    const banner = bannerOf(result.current);
    expect(banner.kind).toBe('uncertainWrite');
    expect(banner.actions).toEqual(['saveAgain']);
    expect(banner.message).toContain('may already have been applied');
    expect(banner.message).toContain('the record list cannot prove whether it was');
    expect(banner.isCreate).toBe(false);
    expect(banner.message).not.toContain('duplicate');

    // Neither Retry nor an ordinary save or leave resends it.
    await perform(() => result.current.retry());
    expect(await perform(() => result.current.save())).toBe(false);
    expect(await perform(() => result.current.leave())).toBe(false);
    expect(api.lines()).toEqual([`PUT ${recordPath(record)}`]);
    expect(result.current.values[TEXT]).toBe('Jones');

    const saved = savedCopy(record, { [TEXT]: 'Jones' });
    api.reply(jsonResponse(200, saved));
    expect(await perform(() => result.current.saveAgain())).toBe(true);

    expect(api.lines()).toEqual([`PUT ${recordPath(record)}`, `PUT ${recordPath(record)}`]);
    expect(sentValues(api.calls[1])).toEqual({ [TEXT]: 'Jones' });
    expect(onSaved).toHaveBeenCalledTimes(1);
    expect(result.current.banner).toBeNull();
  });

  it('treats a create that got no response as possibly applied and names the duplicate risk', async () => {
    const { api, result, onSaved } = mountNew();
    edit(result, TEXT, 'Smith');
    api.reply(networkFailure);

    expect(await perform(() => result.current.save())).toBe(false);

    const banner = bannerOf(result.current);
    expect(banner.kind).toBe('uncertainWrite');
    expect(banner.method).toBe('POST');
    expect(banner.isCreate).toBe(true);
    expect(banner.traceId).toBeNull();
    expect(banner.actions).toEqual(['saveAgain']);
    expect(banner.message).toContain('may already have been applied');
    expect(banner.message).toContain('duplicate record');

    await perform(() => result.current.retry());
    expect(api.lines()).toEqual([`POST ${RECORDS_PATH}`]);
    expect(result.current.values[TEXT]).toBe('Smith');

    const created = createdRecord({ [TEXT]: 'Smith', ...uncheckedCheckboxes() });
    api.reply(jsonResponse(201, created));
    expect(await perform(() => result.current.saveAgain())).toBe(true);

    expect(api.lines()).toEqual([`POST ${RECORDS_PATH}`, `POST ${RECORDS_PATH}`]);
    expect(sentValues(api.calls[1])).toEqual(sentValues(api.calls[0]));
    expect(onSaved).toHaveBeenCalledWith(created);
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

  it('confirm discards the edits and sends exactly one DELETE, with no PUT before it', async () => {
    const { api, result, record, onDeleted, onSaved } = mountExisting();
    edit(result, TEXT, 'Jones');
    run(() => {
      result.current.requestDelete();
    });
    api.reply(noContent());

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

