// The record editor's write rules:
// - C-10: a new record whose only entries are unchecked checkboxes is empty and never
//   posted; a POST sends `false` for every unchecked writable checkbox.
// - C-3: a PUT carries the dirty writable fields only; read-only and unsupported fields
//   are never sent and cannot be edited.
// - Leaving a dirty record saves it first; a failed save, a 404 included, cancels the
//   move and keeps the draft, and Discard edits alone replaces it (0.3.6, 0.3.4.2).
// - The write-outcome table of 0.3.4.2: Retry resends a POST or PUT only on
//   `notWritten`; `unknown`, `written`, a missing or unrecognised outcome and no
//   response offer Save again only, for a create and an update alike. A DELETE is
//   always safe to resend.
// - A write is acknowledged only by its contracted status (201 for POST, 200 for PUT)
//   with its complete Record DTO; any other 2xx offers Save again only, as no response does.
// - C-6: Delete asks first; Cancel sends nothing, Confirm sends exactly one DELETE.
//
// The hook runs against the real client and error parsing; createApiClient's fetch seam
// is a fake that records every request and answers from a per-test reply queue.
import { act, renderHook } from '@testing-library/react';
import { afterEach, describe, expect, it, vi, type Mock } from 'vitest';

import { createApiClient, type ApiClient } from '../api/client';
import type {
  FieldDefinition,
  FieldEventResponse,
  FieldValue,
  FormDefinition,
  RecordDto,
  Values,
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
  /** The wire value as sent: any string, so a value outside the contract's outcomes can be served. */
  readonly writeOutcome?: string;
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

// The definition is fresh per test, and the same object across rerenders: a new definition starts a
// new form.
function mount(
  record: RecordDto | null,
  isNew: boolean,
  definition: FormDefinition = createFormDefinitionFixture(),
): Mounted {
  const api = createFakeApi();
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

function mountNew(definition?: FormDefinition): Mounted {
  return mount(null, true, definition);
}

function mountExisting(record: RecordDto = storedRecord(), definition?: FormDefinition): MountedRecord {
  return { ...mount(record, false, definition), record };
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
    expect(messages).toHaveLength(1);
    expect(messages[0]).toContain(fieldByName(OTHER_TEXT).prompt ?? OTHER_TEXT);
    expect(messages[0]).toContain('is a required field');
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
  // invalid new record is stopped before any request. The required messages are the legacy text
  // typed out, the prompt inserted verbatim, a blank one included; the format message, whose
  // wording the plan leaves open, is matched by the limit it states.
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
      feedback: 'the required message with its prompt',
      update: (field) => ({ ...field, prompt: 'Nickname', validation: { ...field.validation, required: true } }),
      enter: [[OTHER_TEXT, 'Smith']],
      expected: [
        'Nickname is a required field (page Page 1). Please fill in this field before attempting to leave the record, save data, or close the form. ',
      ],
    },
    {
      feedback: 'the required message with a whitespace prompt',
      update: (field) => ({ ...field, prompt: '   ', validation: { ...field.validation, required: true } }),
      enter: [[OTHER_TEXT, 'Smith']],
      expected: [
        '    is a required field (page Page 1). Please fill in this field before attempting to leave the record, save data, or close the form. ',
      ],
    },
    {
      feedback: 'the required message with an empty prompt',
      update: (field) => ({ ...field, prompt: '', validation: { ...field.validation, required: true } }),
      enter: [[OTHER_TEXT, 'Smith']],
      expected: [
        ' is a required field (page Page 1). Please fill in this field before attempting to leave the record, save data, or close the form. ',
      ],
    },
  ])('keeps $feedback for a field named __proto__ and sends nothing', async ({ update, enter, expected }) => {
    const definition = withField(TEXT, (field) => update({ ...field, name: '__proto__' }));
    const page = definition.pages.find((candidate) => candidate.fields.some((field) => field.name === '__proto__'));
    expect(page?.name).toBe('Page 1');
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

  it('shows a record found deleted (409) read-only, with no restore', async () => {
    const { api, result } = mountExisting();
    edit(result, TEXT, 'Jones');
    api.reply(problem(409));

    expect(await perform(() => result.current.save())).toBe(false);

    expect(result.current.deletedReadOnly).toBe(true);
    const banner = bannerOf(result.current);
    expect(banner.kind).toBe('deleted');
    expect(banner.message).toMatch(/deleted/i);
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
      says: /request too large/i,
      actions: [],
    },
    {
      // The generic error banner names the trace id in its text.
      status: 500 as const,
      writeOutcome: 'notWritten' as const,
      kind: 'server',
      says: TRACE_ID,
      actions: ['retry'],
    },
  ])('keeps the draft on a $status, in a $kind banner with the trace id', async (row) => {
    const { api, result, onSaved } = mountExisting();
    edit(result, TEXT, 'Jones');
    api.reply(problem(row.status, row.writeOutcome === undefined ? {} : { writeOutcome: row.writeOutcome }));

    expect(await perform(() => result.current.leave())).toBe(false);

    const banner = bannerOf(result.current);
    expect(banner.kind).toBe(row.kind);
    expect(banner.message).toMatch(row.says);
    expect(banner.traceId).toBe(TRACE_ID);
    expect(banner.actions).toEqual(row.actions);
    expect(result.current.values[TEXT]).toBe('Jones');
    expect(result.current.isDirty).toBe(true);
    expect(onSaved).not.toHaveBeenCalled();
    expect(api.lines()).toHaveLength(1);
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
function mountDirtyDraft(method: RecordWriteMethod): DirtyDraft {
  if (method === 'POST') {
    const mounted = mountNew();
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
  const mounted = mountExisting();
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

/** notWritten, the one outcome Retry resends; the banner follows the status. */
const NOT_WRITTEN_CASES = RECORD_WRITE_METHODS.flatMap((method) => [
  // The generic error banner with the trace id.
  { method, status: 500 as const, kind: 'server', says: TRACE_ID as string | RegExp },
  // The connection banner, whose wording the plan leaves open: it only has to say something.
  { method, status: 503 as const, kind: 'unavailable', says: /\S/ },
]);

/** The 500 and 503 bodies that do not prove the save was not applied. */
const UNPROVEN_OUTCOMES: readonly { readonly name: string; readonly extra: ProblemExtra }[] = [
  { name: 'writeOutcome unknown', extra: { writeOutcome: 'unknown' } },
  { name: 'writeOutcome written', extra: { writeOutcome: 'written' } },
  { name: 'no writeOutcome', extra: {} },
  // A value outside the contract, such as a newer server's, proves nothing either.
  { name: 'an unrecognised outcome', extra: { writeOutcome: 'partiallyWritten' } },
];

interface UnsafeCase {
  readonly method: RecordWriteMethod;
  /** What answered the save, for the test name. */
  readonly failure: string;
  readonly reply: () => Reply;
  /** The banner's trace id: the server's, or `null` when no response arrived. */
  readonly traceId: string | null;
}

/** Every failed save that Retry must not resend: each unproven 500 and 503, and no response. */
const UNSAFE_CASES: readonly UnsafeCase[] = RECORD_WRITE_METHODS.flatMap((method): UnsafeCase[] => [
  ...OUTCOME_STATUSES.flatMap((status) =>
    UNPROVEN_OUTCOMES.map(
      ({ name, extra }): UnsafeCase => ({
        method,
        failure: `a ${String(status)} with ${name}`,
        reply: () => problem(status, extra),
        traceId: TRACE_ID,
      }),
    ),
  ),
  { method, failure: 'no response', reply: () => networkFailure, traceId: null },
]);

describe('write outcomes and Retry (0.3.4.2)', () => {
  it.each(NOT_WRITTEN_CASES)('retries a $method that a $status reports notWritten', async (row) => {
    const draft = mountDirtyDraft(row.method);
    const { api, result, onSaved } = draft;
    api.reply(problem(row.status, { writeOutcome: 'notWritten' }));

    expect(await perform(() => result.current.save())).toBe(false);

    const banner = bannerOf(result.current);
    expect(banner.kind).toBe(row.kind);
    expect(banner.method).toBe(row.method);
    expect(banner.actions).toEqual(['retry']);
    expect(banner.message).toMatch(row.says);
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

  it.each(UNSAFE_CASES)('offers only Save again on a $method after $failure', async ({ method, reply, traceId }) => {
    const draft = mountDirtyDraft(method);
    const { api, result, onSaved } = draft;
    api.reply(reply());

    expect(await perform(() => result.current.save())).toBe(false);

    const banner = bannerOf(result.current);
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

    // Neither Retry nor an ordinary save or leave resends it, and the draft stays.
    await perform(() => result.current.retry());
    expect(await perform(() => result.current.save())).toBe(false);
    expect(await perform(() => result.current.leave())).toBe(false);
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
