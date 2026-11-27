// Ordering and races of check-code field events: the queue sends one event at a time in
// the order raised; each event carries the values the user had when it was raised, plus
// the effects of earlier responses; a user edit made after an event was raised beats that
// event's `cleared`/`assigned`; a response for a replaced record is ignored; a failed event
// shows a notice without blocking a save, which lasts until an event of the same kind on the
// same field succeeds or the record changes; and a number the client decodes exactly in
// `assigned` is applied with the rest of its answer and saved as the same JSON token.
//
// createFieldEventQueue is tested directly against a mutable editor snapshot, and through
// useRecordEditor, which drains the queue before saving and suppresses the focus and blur
// that replacing the rendered record causes. Both run against the real client and error
// parsing: createApiClient's fetch seam is an in-memory API that holds every field-event
// request until the test answers it.
import { act, renderHook, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi, type Mock } from 'vitest';

import { createApiClient, isExactDecimal, numberFromText, sameFieldValue, type ApiClient } from '../api/client';
import { isAbortError } from '../api/errors';
import type {
  Diagnostic,
  DialogMessage,
  ExactDecimal,
  FieldEventKind,
  FieldEventRequest,
  FieldEventResponse,
  FieldValue,
  ProblemDetails,
  RecordDto,
  Values,
  WriteOutcome,
  WriteProblemDetails,
} from '../api/types';
import type { EditorSnapshot } from '../checkcode/applyFieldEvent';
import {
  createFormDefinitionFixture,
  dataFieldNames,
  readOnlyFieldNames,
  recordFixture,
  writableCheckboxNames,
} from '../test/formDefinitionFixture';
import { createFieldEventQueue, type FieldEventQueue, type FieldEventQueueHost } from './fieldEventQueue';
import { useRecordEditor } from './useRecordEditor';

// One instance for the whole file: the editor starts a new queue whenever the definition's
// identity changes.
const definition = createFormDefinitionFixture();
const VIEW_ID = definition.viewId;
const FIELD_EVENT_PATH = `/api/forms/${VIEW_ID}/checkcode/field-event`;
const RECORD_ROUTE = /^\/api\/forms\/(\d+)\/records\/([^/]+)$/;
const TRACE_ID = '00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01';

/** A writable checkbox, which a click event carries after its toggle. */
const CHECKBOX = 'Asian';
/** A read-only field: check code may assign it, the editor never sends it. */
const READ_ONLY = 'OnsetWeek';
/** Every checkbox of the fixture form, read-only ones included: each is sent in every event. */
const CHECKBOX_NAMES = definition.pages
  .flatMap((page) => page.fields)
  .filter((field) => field.fieldType === 'checkbox')
  .map((field) => field.name);
/** A writable number field with no range or mask, so any number assigned to it can be saved. */
const PLAIN_NUMBER = 'CaseID';

const RECORD_A_ID = '3f2b8c1e-5d4a-4c1b-9e7f-00000000000a';
const RECORD_B_ID = '3f2b8c1e-5d4a-4c1b-9e7f-00000000000b';

/** EC-AGE-03: YEARS(DOB, DateofInterview) assigns 11 for this leap-day birth date. */
const DOB_ENTERED = '2000-02-29';
const DOB_LATER = '1999-12-31';
const INTERVIEW_DATE = '2011-02-28';
const ASSIGNED_AGE = 11;

const FEVER_DIALOG: DialogMessage = {
  message: "Warning: A fever temperature has been entered, but 'fever' was not selected as a symptom.",
  title: null,
};
const ONSET_DIALOG: DialogMessage = { message: 'Check the onset date.', title: 'Onset' };
const RACE_DIALOG: DialogMessage = { message: 'Only one race may be selected.', title: 'Race' };
/** The diagnostic of a skipped GEOCODE (EC-GEO-01), as the server reports it. */
const GEOCODE_DIAGNOSTIC: Diagnostic = {
  severity: 'info',
  form: 'FoodHistory',
  line: 12,
  command: 'GEOCODE',
  message: 'GEOCODE is not executed; statement skipped',
};

// Wire bodies

function json(status: number, body: FieldEventResponse | RecordDto): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } });
}

/** A 200 whose JSON text is given as is, for answers no typed body can express. */
function rawJson(text: string): Response {
  return new Response(text, { status: 200, headers: { 'content-type': 'application/json' } });
}

/** A ProblemDetails body; given `writeOutcome`, the WriteProblemDetails of a failed record write. */
function problem(status: number, writeOutcome?: WriteOutcome): Response {
  const details: ProblemDetails = {
    type: 'about:blank',
    title: 'An error occurred while processing your request.',
    status,
    detail: 'An unexpected error occurred.',
    traceId: TRACE_ID,
  };
  const body: ProblemDetails | WriteProblemDetails =
    writeOutcome === undefined ? details : { ...details, writeOutcome };
  return new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/problem+json' } });
}

/** A field-event response: the empty envelope with `effects` laid over it. */
function envelope(effects: Partial<FieldEventResponse> = {}): FieldEventResponse {
  return { enabled: [], disabled: [], cleared: [], assigned: {}, dialogs: [], diagnostics: [], ...effects };
}

/** The six members the OpenAPI document requires of every field-event response. */
const ENVELOPE_MEMBERS = [
  'enabled',
  'disabled',
  'cleared',
  'assigned',
  'dialogs',
  'diagnostics',
] as const satisfies readonly (keyof FieldEventResponse)[];

/** The JSON text of `response` without its `member`. */
function omitting(response: FieldEventResponse, member: keyof FieldEventResponse): string {
  return JSON.stringify(Object.fromEntries(Object.entries(response).filter(([name]) => name !== member)));
}

/** A full `values` object of the fixture form; recordFixture rejects a name that is not a data field. */
function valuesWith(overrides: Values): Values {
  return recordFixture({ values: overrides }).values;
}

/** The ExactDecimal a number field holds for `text`; throws when the text makes none. */
function exactDecimal(text: string): ExactDecimal {
  const value = numberFromText(text);
  if (!isExactDecimal(value)) {
    throw new Error(`${text} is not an exact decimal`);
  }
  return value;
}

// Request bodies arrive as `unknown` and are narrowed here; a body of the wrong shape is
// recorded as a fault, which fails the test after it.

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function isFieldValue(value: unknown): value is FieldValue {
  return value === null || typeof value === 'string' || typeof value === 'number' || typeof value === 'boolean';
}

const EVENT_KINDS: readonly FieldEventKind[] = ['FieldBefore', 'FieldAfter', 'FieldClick'];

function isEventKind(value: unknown): value is FieldEventKind {
  return EVENT_KINDS.some((kind) => kind === value);
}

function readValues(value: unknown): Values {
  if (!isRecord(value)) {
    throw new Error('`values` is not an object');
  }
  const entries: [string, FieldValue][] = [];
  for (const [name, entry] of Object.entries(value)) {
    if (!isFieldValue(entry)) {
      throw new Error(`values.${name} is not a scalar field value`);
    }
    entries.push([name, entry]);
  }
  return Object.fromEntries(entries);
}

/** A field-event request as the fake received it. */
interface SentEvent {
  readonly event: FieldEventKind;
  readonly fieldName: string;
  readonly values: Values;
}

function readFieldEvent(body: unknown): SentEvent {
  if (!isRecord(body)) {
    throw new Error('the field-event body is not an object');
  }
  const { event, fieldName, values } = body;
  if (!isEventKind(event)) {
    throw new Error('the field-event body has no valid `event`');
  }
  if (typeof fieldName !== 'string') {
    throw new Error('the field-event body has no `fieldName`');
  }
  return { event, fieldName, values: readValues(values) };
}

function readWriteValues(body: unknown): Values {
  if (!isRecord(body)) {
    throw new Error('the record write body is not an object');
  }
  return readValues(body.values);
}

// The in-memory API

interface RecordedCall {
  readonly method: string;
  readonly path: string;
  readonly body: unknown;
  /** The body text exactly as sent, whose numbers JSON.parse may have rounded in `body`. */
  readonly text: string;
}

/** A field-event request held at the fake until the test answers it. */
interface HeldEvent extends SentEvent {
  readonly respond: (response: Response) => void;
  /**
   * The signal the request arrived with. The fake stops counting an aborted request as
   * open but still delivers a later answer, as a transport that ignores the abort would.
   */
  readonly signal: AbortSignal;
}

interface FakeApi {
  readonly fetch: (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>;
  /** Every request received, in arrival order. */
  readonly calls: readonly RecordedCall[];
  /** Field-event requests in arrival order. */
  readonly events: readonly HeldEvent[];
  /** The `values` of every PUT, in arrival order. */
  readonly puts: () => Values[];
  /** The most field-event requests that were ever unanswered and not aborted at once. */
  readonly maxOpenEvents: () => number;
  /** From now on each PUT is held until the test answers it; until then a PUT is answered at once. */
  readonly holdPuts: () => void;
  /** PUTs held since holdPuts(), in arrival order. */
  readonly heldPuts: readonly HeldPut[];
  /** Requests the fake could not route or parse. */
  readonly faults: readonly string[];
}

/** A PUT held at the fake until the test answers it. */
interface HeldPut {
  /** The `values` the PUT carried. */
  readonly values: Values;
  /** Answers as an unheld PUT is answered, with the saved DTO. */
  readonly save: () => void;
  readonly respond: (response: Response) => void;
}

/** Every fake API of the running test, checked after it. */
const apisOfTest: FakeApi[] = [];

/**
 * Field events are held until answered. A PUT of a `stored` record answers at once with
 * the record and the submitted values laid over it, as the server's saved DTO; after
 * holdPuts() each PUT is held until the test saves it or answers it otherwise.
 */
function createFakeApi(stored: readonly RecordDto[]): FakeApi {
  const calls: RecordedCall[] = [];
  const events: HeldEvent[] = [];
  const faults: string[] = [];
  let open = 0;
  let maxOpen = 0;

  const holdFieldEvent = (sent: SentEvent, signal: AbortSignal): Promise<Response> =>
    new Promise<Response>((resolve) => {
      const index = events.length;
      let answered = false;
      let closed = false;
      const close = (): void => {
        if (!closed) {
          closed = true;
          open -= 1;
        }
      };
      open += 1;
      maxOpen = Math.max(maxOpen, open);
      if (signal.aborted) {
        close();
      } else {
        signal.addEventListener('abort', close, { once: true });
      }
      events.push({
        ...sent,
        respond: (response) => {
          if (answered) {
            throw new Error(`field event ${index} was already answered`);
          }
          answered = true;
          close();
          resolve(response);
        },
        signal,
      });
    });

  const saveRecord = (id: string, values: Values): Response => {
    const record = stored.find((candidate) => candidate.globalRecordId === id);
    if (record === undefined) {
      faults.push(`PUT of unknown record ${id}`);
      return problem(404);
    }
    return json(200, { ...record, values: { ...record.values, ...values } });
  };

  const heldPuts: HeldPut[] = [];
  let holdingPuts = false;

  const holdPut = (id: string, values: Values): Promise<Response> =>
    new Promise<Response>((resolve) => {
      const index = heldPuts.length;
      let answered = false;
      const respond = (response: Response): void => {
        if (answered) {
          throw new Error(`PUT ${index} was already answered`);
        }
        answered = true;
        resolve(response);
      };
      heldPuts.push({
        values,
        save: () => {
          respond(saveRecord(id, values));
        },
        respond,
      });
    });

  const fetchFromFake = async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
    // openapi-fetch hands over the Request it built; anything else is normalised to one.
    const request = input instanceof Request ? input : new Request(input, init);
    const path = new URL(request.url).pathname;
    const text = await request.text();

    try {
      const body: unknown = text === '' ? null : JSON.parse(text);
      calls.push({ method: request.method, path, body, text });
      if (request.method === 'POST' && path === FIELD_EVENT_PATH) {
        return await holdFieldEvent(readFieldEvent(body), request.signal);
      }
      const record = RECORD_ROUTE.exec(path);
      if (request.method === 'PUT' && record !== null && Number(record[1]) === VIEW_ID) {
        const values = readWriteValues(body);
        return holdingPuts ? await holdPut(record[2], values) : saveRecord(record[2], values);
      }
      faults.push(`unrouted ${request.method} ${path}`);
    } catch (e) {
      faults.push(`${request.method} ${path}: ${e instanceof Error ? e.message : String(e)}`);
    }
    return problem(400);
  };

  const api: FakeApi = {
    fetch: fetchFromFake,
    calls,
    events,
    puts: () => calls.filter((call) => call.method === 'PUT').map((call) => readWriteValues(call.body)),
    maxOpenEvents: () => maxOpen,
    holdPuts: () => {
      holdingPuts = true;
    },
    heldPuts,
    faults,
  };
  apisOfTest.push(api);
  return api;
}

afterEach(() => {
  for (const api of apisOfTest.splice(0)) {
    expect(api.faults).toEqual([]);
  }
});

interface Harness {
  readonly api: FakeApi;
  /** The real client over the fake, which also keeps every raiseFieldEvent request and result. */
  readonly client: ApiClient;
  /**
   * Every field-event request as the client was given it, in order: values exactly as the
   * client holds them, before JSON turns a bigint or an ExactDecimal into a plain number.
   */
  readonly requests: readonly FieldEventRequest[];
  /** The client's settled result of the `index`-th field event. */
  readonly eventResult: (index: number) => Promise<unknown>;
}

function createHarness(stored: readonly RecordDto[] = []): Harness {
  const api = createFakeApi(stored);
  const base = createApiClient({ baseUrl: 'http://localhost', fetch: api.fetch });
  const requests: FieldEventRequest[] = [];
  const results: Promise<unknown>[] = [];
  const client: ApiClient = {
    ...base,
    raiseFieldEvent: (viewId, request, signal) => {
      requests.push(request);
      const result = base.raiseFieldEvent(viewId, request, signal);
      results.push(result);
      return result;
    },
  };
  return {
    api,
    client,
    requests,
    eventResult: (index) => {
      const result = results.at(index);
      if (result === undefined) {
        throw new Error(`field event ${index} was never raised`);
      }
      return result;
    },
  };
}

/** Lets every pending promise callback and zero-delay timer run. */
function flush(): Promise<void> {
  return new Promise((resolve) => {
    setTimeout(resolve, 0);
  });
}

/** Waits until the `index`-th field-event request has reached the fake. */
async function eventAt(api: FakeApi, index: number): Promise<HeldEvent> {
  await waitFor(() => {
    expect(api.events.length).toBeGreaterThan(index);
  });
  return api.events[index];
}

/** Waits until the `index`-th PUT has reached the fake after holdPuts(). */
async function putAt(api: FakeApi, index: number): Promise<HeldPut> {
  await waitFor(() => {
    expect(api.heldPuts.length).toBeGreaterThan(index);
  });
  return api.heldPuts[index];
}

/**
 * Answers the `index`-th field event and waits until the queue has processed the answer. A
 * request whose signal was aborted reached its end at the abort: the client rejected it with
 * an AbortError then, without waiting for the fake, which ignores the abort, so the answer
 * that still arrives reaches no one.
 */
async function answer(harness: Harness, index: number, response: Response): Promise<void> {
  const held = await eventAt(harness.api, index);
  await act(async () => {
    held.respond(response);
    if (held.signal.aborted) {
      await expect(harness.eventResult(index)).rejects.toSatisfy(isAbortError);
    } else {
      await harness.eventResult(index);
    }
    await flush();
  });
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

/** The fields of each field-event request, in arrival order. */
function sentEvents(api: FakeApi): { event: FieldEventKind; fieldName: string }[] {
  return api.events.map(({ event, fieldName }) => ({ event, fieldName }));
}

// The queue on its own, over a mutable editor snapshot

/** The editor as the queue sees it; callbacks are mocks the test inspects. */
interface TestHost extends FieldEventQueueHost {
  snapshot: EditorSnapshot;
  readonly getSnapshot: () => EditorSnapshot;
  readonly setSnapshot: Mock<(next: EditorSnapshot) => void>;
  readonly onDialogs: Mock<(dialogs: readonly DialogMessage[]) => void>;
  readonly onNotices: Mock<(messages: readonly string[]) => void>;
}

function createHost(values: Values): TestHost {
  const host: TestHost = {
    snapshot: { values, checkCodeDisabled: new Set(), dirty: new Set() },
    getSnapshot: () => host.snapshot,
    setSnapshot: vi.fn((next: EditorSnapshot) => {
      host.snapshot = next;
    }),
    onDialogs: vi.fn(),
    onNotices: vi.fn(),
  };
  return host;
}

interface QueueUnderTest {
  readonly harness: Harness;
  readonly host: TestHost;
  readonly queue: FieldEventQueue;
  /** A user edit, as the editor makes it: the value and the dirty set change, then the queue is told. */
  readonly edit: (name: string, value: FieldValue) => void;
}

function createQueue(
  values: Values = valuesWith({ DateofInterview: INTERVIEW_DATE }),
  deadlineMs?: number,
): QueueUnderTest {
  const harness = createHarness();
  const host = createHost(values);
  const queue = createFieldEventQueue({ client: harness.client, viewId: VIEW_ID, definition, host, deadlineMs });
  return {
    harness,
    host,
    queue,
    edit: (name, value) => {
      host.snapshot = {
        ...host.snapshot,
        values: { ...host.snapshot.values, [name]: value },
        dirty: new Set([...host.snapshot.dirty, name]),
      };
      queue.noteUserEdit(name);
    },
  };
}

/** A response with every kind of effect, which must change nothing once its record is gone. */
function staleEffects(): Response {
  return json(
    200,
    envelope({
      enabled: ['LastName'],
      disabled: ['Age'],
      cleared: ['FirstName'],
      assigned: { Age: ASSIGNED_AGE, [READ_ONLY]: 5 },
      dialogs: [FEVER_DIALOG],
    }),
  );
}

describe('createFieldEventQueue', () => {
  it('sends one event at a time in the order raised, each after the effects of the one before', async () => {
    const { harness, host, queue } = createQueue();
    const { api } = harness;
    let drained = false;

    queue.raise('FieldBefore', 'DOB');
    queue.raise('FieldAfter', 'DOB');
    queue.raise('FieldClick', CHECKBOX);
    void queue.drain().then(() => {
      drained = true;
    });

    await eventAt(api, 0);
    await flush();
    expect(api.events).toHaveLength(1);
    expect(queue.size).toBe(3);

    await answer(harness, 0, json(200, envelope({ assigned: { Age: ASSIGNED_AGE } })));
    await eventAt(api, 1);
    await flush();
    expect(api.events).toHaveLength(2);
    expect(queue.size).toBe(2);
    // The second event was sent after the first response was applied, so it sees its effect.
    expect(api.events[1].values.Age).toBe(ASSIGNED_AGE);

    await answer(harness, 1, json(200, envelope()));
    await answer(harness, 2, json(200, envelope()));

    expect(sentEvents(api)).toEqual([
      { event: 'FieldBefore', fieldName: 'DOB' },
      { event: 'FieldAfter', fieldName: 'DOB' },
      { event: 'FieldClick', fieldName: CHECKBOX },
    ]);
    expect(api.maxOpenEvents()).toBe(1);
    // Every request carries the whole record: each data field once, read-only ones included.
    for (const sent of api.events) {
      expect(Object.keys(sent.values).sort()).toEqual([...dataFieldNames].sort());
    }
    expect(queue.size).toBe(0);
    expect(drained).toBe(true);
    expect(host.snapshot.values.Age).toBe(ASSIGNED_AGE);
  });

  it('gives each queued event the values the user had when it was raised, never a later edit', async () => {
    // A loaded record holds an unchecked box as `false`, as legacy stores it as 0.
    const { harness, host, queue, edit } = createQueue(
      valuesWith({ DateofInterview: INTERVIEW_DATE, [CHECKBOX]: false }),
    );
    const { api } = harness;
    expect(writableCheckboxNames).toContain(CHECKBOX);

    queue.raise('FieldBefore', 'DOB');
    const focus = await eventAt(api, 0);

    edit('DOB', DOB_ENTERED);
    queue.raise('FieldAfter', 'DOB');
    // A click is raised after its toggle, so it captures the post-click value.
    edit(CHECKBOX, true);
    queue.raise('FieldClick', CHECKBOX);
    edit('DOB', DOB_LATER);

    await answer(harness, 0, json(200, envelope()));
    const blur = await eventAt(api, 1);
    await answer(harness, 1, json(200, envelope()));
    const click = await eventAt(api, 2);
    await answer(harness, 2, json(200, envelope()));

    expect(focus.values).toMatchObject({ DOB: null, [CHECKBOX]: false, DateofInterview: INTERVIEW_DATE });
    // The toggle came after the blur was raised, so the blur still carries the unchecked box.
    expect(blur).toMatchObject({ event: 'FieldAfter', fieldName: 'DOB' });
    expect(blur.values).toMatchObject({ DOB: DOB_ENTERED, [CHECKBOX]: false, DateofInterview: INTERVIEW_DATE });
    expect(click).toMatchObject({ event: 'FieldClick', fieldName: CHECKBOX });
    expect(click.values).toMatchObject({ DOB: DOB_ENTERED, [CHECKBOX]: true, DateofInterview: INTERVIEW_DATE });
    expect([blur.values.DOB, click.values.DOB]).not.toContain(DOB_LATER);

    // The later edit is on screen, and an event raised after it carries it.
    expect(host.snapshot.values.DOB).toBe(DOB_LATER);
    queue.raise('FieldAfter', 'DOB');
    const next = await eventAt(api, 3);
    expect(next.values.DOB).toBe(DOB_LATER);
    await answer(harness, 3, json(200, envelope()));
  });

  // The server fails a whole field-event request when one value lacks its field type's wire
  // shape, so such a draft value would fail every later event; it is sent as null. Any value
  // with the shape is sent unchanged: an event applies none of the storage-range or
  // option-count rules a save does.
  it.each<{ value: string; fieldName: string; draft: FieldValue; outcome: 'as null' | 'unchanged' }>([
    { value: 'text in a number field', fieldName: 'Age', draft: 'abc', outcome: 'as null' },
    { value: 'a lone minus sign in a number field', fieldName: 'Age', draft: '-', outcome: 'as null' },
    { value: 'a date that is not on the calendar', fieldName: 'DOB', draft: '2011-02-29', outcome: 'as null' },
    { value: 'a fractional option index', fieldName: 'IllnessSeverity', draft: 2.5, outcome: 'as null' },
    { value: 'a negative option index', fieldName: 'IllnessSeverity', draft: -1, outcome: 'as null' },
    { value: 'a bigint number', fieldName: 'Age', draft: 9007199254740993n, outcome: 'unchanged' },
    { value: 'an ExactDecimal number', fieldName: 'FeverTemp', draft: exactDecimal('9007199254740992.5'), outcome: 'unchanged' },
    { value: 'a date before the datetime range', fieldName: 'DOB', draft: '0001-01-01', outcome: 'unchanged' },
    { value: 'an option index beyond the option count', fieldName: 'IllnessSeverity', draft: 7, outcome: 'unchanged' },
  ])('sends $value $outcome, as the current value and as the value captured before an edit', async (row) => {
    const { fieldName, draft, outcome } = row;
    const { harness, queue, edit } = createQueue(valuesWith({ DateofInterview: INTERVIEW_DATE, [fieldName]: draft }));
    const sent = outcome === 'unchanged' ? draft : null;

    // The first event is sent at once, with the current value.
    queue.raise('FieldBefore', 'LastName');
    await eventAt(harness.api, 0);
    // The second waits; the user then changes the field, so it carries the captured value.
    queue.raise('FieldAfter', 'LastName');
    edit(fieldName, null);
    await answer(harness, 0, json(200, envelope()));
    await answer(harness, 1, json(200, envelope()));

    expect(harness.requests.map((request) => request.values[fieldName])).toEqual([sent, sent]);
    for (const request of harness.requests) {
      expect(request.values.DateofInterview).toBe(INTERVIEW_DATE);
    }
  });

  it.each([
    { when: 'after the event was raised', winner: 'the edit', editAfterRaise: true, expectedAge: 30 },
    { when: 'before the event was raised', winner: 'check code', editAfterRaise: false, expectedAge: ASSIGNED_AGE },
  ])('resolves an assigned Age against a user edit made $when: $winner wins', async (row) => {
    const { editAfterRaise, expectedAge } = row;
    const values = valuesWith({ DOB: DOB_ENTERED, DateofInterview: INTERVIEW_DATE });
    const { harness, host, queue, edit } = createQueue(values);

    if (!editAfterRaise) {
      edit('Age', 30);
    }
    queue.raise('FieldAfter', 'DOB');
    await eventAt(harness.api, 0);
    if (editAfterRaise) {
      edit('Age', 30);
    }
    await answer(harness, 0, json(200, envelope({ assigned: { Age: ASSIGNED_AGE, FeverTemp: 101.2 } })));

    expect(host.snapshot.values.Age).toBe(expectedAge);
    // A field the user did not touch takes the assigned value and is saved with the record.
    expect(host.snapshot.values.FeverTemp).toBe(101.2);
    expect([...host.snapshot.dirty].sort()).toEqual(['Age', 'FeverTemp']);
  });

  it.each([
    { when: 'after the event was raised', winner: 'the edit', editAfterRaise: true, sent: 'Ann', expected: 'Anna' },
    { when: 'before the event was raised', winner: 'check code', editAfterRaise: false, sent: 'Anna', expected: null },
  ])('resolves a cleared FirstName against a user edit made $when: $winner wins', async (row) => {
    const { editAfterRaise, sent, expected } = row;
    const { harness, host, queue, edit } = createQueue(valuesWith({ FirstName: 'Ann', LastName: 'Smith' }));

    if (!editAfterRaise) {
      edit('FirstName', 'Anna');
    }
    queue.raise('FieldAfter', 'LastName');
    const blur = await eventAt(harness.api, 0);
    if (editAfterRaise) {
      edit('FirstName', 'Anna');
    }
    await answer(harness, 0, json(200, envelope({ cleared: ['FirstName', 'LastName'] })));

    // The event carries the value the user had when it was raised.
    expect(blur.values.FirstName).toBe(sent);
    expect(host.snapshot.values.FirstName).toBe(expected);
    // A field the user did not touch is cleared and saved with the record.
    expect(host.snapshot.values.LastName).toBeNull();
    expect([...host.snapshot.dirty].sort()).toEqual(['FirstName', 'LastName']);
  });

  it.each([
    { outcome: 'effects', response: staleEffects },
    { outcome: 'a 500', response: (): Response => problem(500) },
    { outcome: 'an unreadable answer', response: (): Response => rawJson('{"enabled":5}') },
  ])('cancels the request of a record generation that reset() ended, and ignores $outcome arriving for it', async ({
    response,
  }) => {
    const { harness, host, queue } = createQueue(valuesWith({ LastName: 'Smith' }));
    const { api } = harness;
    let drained = false;

    queue.raise('FieldAfter', 'DOB');
    const stale = await eventAt(api, 0);
    void queue.drain().then(() => {
      drained = true;
    });
    await flush();
    expect(drained).toBe(false);

    queue.reset();
    expect(stale.signal.aborted).toBe(true);
    await flush();
    // The waiter is released by the reset itself, not by the stale answer.
    expect(drained).toBe(true);
    expect(queue.size).toBe(0);
    expect(queue.generation).toBe(1);

    // The new generation's event is sent at once; it does not wait for the stale one.
    queue.raise('FieldBefore', 'LastName');
    const fresh = await eventAt(api, 1);
    expect(fresh).toMatchObject({ event: 'FieldBefore', fieldName: 'LastName' });
    expect(fresh.signal.aborted).toBe(false);
    expect(queue.size).toBe(1);

    const before = host.snapshot;
    await answer(harness, 0, response());

    expect(host.snapshot).toBe(before);
    expect(host.setSnapshot).not.toHaveBeenCalled();
    expect(host.onDialogs).not.toHaveBeenCalled();
    expect(host.onNotices).not.toHaveBeenCalled();
    expect(queue.size).toBe(1);

    await answer(harness, 1, json(200, envelope()));
    expect(queue.size).toBe(0);
  });

  it('passes the dialogs of each response to onDialogs in order, and only when there are some', async () => {
    const { harness, host, queue } = createQueue(valuesWith({ FeverTemp: 101.2 }));

    queue.raise('FieldAfter', 'FeverTemp');
    queue.raise('FieldAfter', 'OnsetDate');
    queue.raise('FieldClick', CHECKBOX);
    await answer(harness, 0, json(200, envelope({ dialogs: [FEVER_DIALOG, ONSET_DIALOG] })));
    await answer(harness, 1, json(200, envelope()));
    await answer(harness, 2, json(200, envelope({ dialogs: [RACE_DIALOG] })));

    expect(host.onDialogs.mock.calls).toEqual([[[FEVER_DIALOG, ONSET_DIALOG]], [[RACE_DIALOG]]]);
  });

  it('turns a failed event into a notice, counts it as processed and sends the next event', async () => {
    const { harness, host, queue } = createQueue();
    let drained = false;

    queue.raise('FieldAfter', 'LastName');
    queue.raise('FieldBefore', 'FirstName');
    void queue.drain().then(() => {
      drained = true;
    });
    await answer(harness, 0, problem(500));

    // One notice, naming the field by its prompt and carrying the server's trace id.
    expect(host.onNotices).toHaveBeenCalledTimes(1);
    const [notices] = host.onNotices.mock.calls[0];
    expect(notices).toHaveLength(1);
    expect(notices[0]).toContain('Last Name');
    expect(notices[0]).toContain(TRACE_ID);
    expect(host.setSnapshot).not.toHaveBeenCalled();

    const next = await eventAt(harness.api, 1);
    expect(next).toMatchObject({ event: 'FieldBefore', fieldName: 'FirstName' });
    await answer(harness, 1, json(200, envelope()));
    expect(drained).toBe(true);
    // Another field's success leaves the failed field's notice as it was.
    expect(host.onNotices).toHaveBeenCalledTimes(1);
  });

  const DOB_FAILED = `Check code could not run for DOB. An unexpected error occurred. Trace id: ${TRACE_ID}.`;
  const DOB_UNREADABLE = "Check code could not run for DOB. The server's answer could not be read.";
  const FIRST_NAME_FAILED = `Check code could not run for First Name. An unexpected error occurred. Trace id: ${TRACE_ID}.`;
  const failed = (): Response => problem(500);
  const unreadable = (): Response => rawJson('{"enabled":5}');
  const applied = (): Response => json(200, envelope());

  // UXFB-F13: a field's failure stands until an event of the same kind on that field is
  // applied; the host is given the whole list on every change and only then.
  it.each<{
    readonly through: string;
    readonly steps: readonly (readonly [FieldEventKind, string, () => Response])[];
    readonly lists: readonly (readonly string[])[];
  }>([
    {
      through: 'a later failure of another field, listed after it',
      steps: [
        ['FieldAfter', 'DOB', failed],
        ['FieldAfter', 'FirstName', failed],
      ],
      lists: [[DOB_FAILED], [DOB_FAILED, FIRST_NAME_FAILED]],
    },
    {
      through: "another field's successful events",
      steps: [
        ['FieldAfter', 'DOB', failed],
        ['FieldBefore', 'FirstName', applied],
        ['FieldAfter', 'FirstName', applied],
      ],
      lists: [[DOB_FAILED]],
    },
    {
      through: 'a successful FieldBefore of the same field',
      steps: [
        ['FieldAfter', 'DOB', failed],
        ['FieldBefore', 'DOB', applied],
      ],
      lists: [[DOB_FAILED]],
    },
    {
      through: 'an applied FieldAfter of the same field, which removes it',
      steps: [
        ['FieldAfter', 'DOB', failed],
        ['FieldAfter', 'FirstName', failed],
        ['FieldAfter', 'DOB', applied],
      ],
      lists: [[DOB_FAILED], [DOB_FAILED, FIRST_NAME_FAILED], [FIRST_NAME_FAILED]],
    },
    {
      through: 'a failure of another kind on the same field, whose reason it shows until that kind is applied',
      steps: [
        ['FieldAfter', 'DOB', failed],
        ['FieldBefore', 'DOB', unreadable],
        ['FieldBefore', 'DOB', applied],
        ['FieldAfter', 'DOB', applied],
      ],
      lists: [[DOB_FAILED], [DOB_UNREADABLE], [DOB_FAILED], []],
    },
    {
      through: 'repeated failures of the same field, which keeps its place and shows the latest reason',
      steps: [
        ['FieldAfter', 'DOB', failed],
        ['FieldAfter', 'FirstName', failed],
        ['FieldAfter', 'DOB', unreadable],
        ['FieldAfter', 'DOB', unreadable],
      ],
      lists: [[DOB_FAILED], [DOB_FAILED, FIRST_NAME_FAILED], [DOB_UNREADABLE, FIRST_NAME_FAILED]],
    },
  ])('lists a FieldAfter DOB failure through $through', async ({ steps, lists }) => {
    const { harness, host, queue } = createQueue();

    for (const [index, [kind, fieldName, response]] of steps.entries()) {
      queue.raise(kind, fieldName);
      await answer(harness, index, response());
    }

    expect(sentEvents(harness.api)).toEqual(steps.map(([event, fieldName]) => ({ event, fieldName })));
    expect(host.onNotices.mock.calls.map(([messages]) => messages)).toEqual(lists);
  });

  it('clears every failure at reset(), reports the empty list once, and keeps the new generation silent', async () => {
    const { harness, host, queue } = createQueue();

    queue.raise('FieldAfter', 'DOB');
    await answer(harness, 0, problem(500));
    queue.reset();
    queue.reset();
    expect(host.onNotices.mock.calls.map(([messages]) => messages)).toEqual([[DOB_FAILED], []]);

    // The record now open has no failure for its own FieldAfter DOB to resolve.
    queue.raise('FieldAfter', 'DOB');
    await answer(harness, 1, json(200, envelope()));
    expect(host.onNotices).toHaveBeenCalledTimes(2);
  });

  // A request that never answers, an answer of the wrong shape and a host callback that
  // throws each end as a notice; none of them stops the queue or holds drain().

  /**
   * The deadline of the cases that let a request stall. The event queued behind the stalled
   * one is answered as soon as it arrives, so the deadline only has to outlast that, with
   * room for a loaded machine.
   */
  const STALL_DEADLINE_MS = 300;

  /** Waits `ms` on the real clock. */
  const elapse = (ms: number): Promise<void> =>
    new Promise((resolve) => {
      setTimeout(resolve, ms);
    });

  it('cancels a request unanswered past the deadline, shows one notice and sends the next event', async () => {
    const { harness, host, queue, edit } = createQueue(undefined, STALL_DEADLINE_MS);
    const { api } = harness;
    let drained = false;

    queue.raise('FieldAfter', 'LastName');
    const stalled = await eventAt(api, 0);
    edit('DOB', DOB_ENTERED);
    queue.raise('FieldAfter', 'DOB');
    // Made after the blur was raised, so the blur must not carry it.
    edit('DOB', DOB_LATER);
    void queue.drain().then(() => {
      drained = true;
    });

    const next = await eventAt(api, 1);
    expect(stalled.signal.aborted).toBe(true);
    expect(host.onNotices.mock.calls).toEqual([
      [['Check code could not run for Last Name. The server did not answer in time.']],
    ]);
    expect(next).toMatchObject({ event: 'FieldAfter', fieldName: 'DOB' });
    expect(next.values).toMatchObject({ DOB: DOB_ENTERED, DateofInterview: INTERVIEW_DATE });
    expect(next.signal.aborted).toBe(false);
    expect(drained).toBe(false);

    await answer(harness, 1, json(200, envelope({ assigned: { Age: ASSIGNED_AGE } })));
    expect(drained).toBe(true);
    expect(queue.size).toBe(0);
    expect(host.snapshot.values.Age).toBe(ASSIGNED_AGE);

    // An answer that still arrives for the cancelled request changes nothing.
    await answer(harness, 0, json(200, envelope({ assigned: { Age: 30 }, dialogs: [FEVER_DIALOG] })));
    expect(host.snapshot.values.Age).toBe(ASSIGNED_AGE);
    expect(host.onDialogs).not.toHaveBeenCalled();
    expect(host.onNotices).toHaveBeenCalledTimes(1);
    // The stalled request stopped counting as open when it was cancelled.
    expect(api.maxOpenEvents()).toBe(1);
  });

  it('reports nothing when the deadline of a request that reset() cancelled passes', async () => {
    const { harness, host, queue } = createQueue(undefined, STALL_DEADLINE_MS);

    queue.raise('FieldAfter', 'LastName');
    const stale = await eventAt(harness.api, 0);
    queue.reset();
    expect(stale.signal.aborted).toBe(true);
    await elapse(STALL_DEADLINE_MS * 2);

    expect(host.onNotices).not.toHaveBeenCalled();
    expect(host.setSnapshot).not.toHaveBeenCalled();
    expect(queue.size).toBe(0);
    await expect(queue.drain()).resolves.toBeUndefined();
  });

  /**
   * A complete answer that assigns a value and opens a dialog, so applying any part of it
   * would call setSnapshot or onDialogs. Each malformed answer below breaks it in one place.
   */
  const effectful = envelope({ assigned: { Age: ASSIGNED_AGE }, dialogs: [FEVER_DIALOG] });

  it.each([
    { shape: 'an empty body', text: '' },
    { shape: 'a body that is not JSON', text: '<html>Bad gateway</html>' },
    { shape: 'a null body', text: 'null' },
    { shape: 'an array body', text: '[]' },
    { shape: 'none of the members', text: '{"message":"Not available"}' },
    ...ENVELOPE_MEMBERS.flatMap((member) => [
      { shape: `no \`${member}\``, text: omitting(effectful, member) },
      { shape: `a null \`${member}\``, text: JSON.stringify({ ...effectful, [member]: null }) },
    ]),
    { shape: 'a list that is not an array', text: JSON.stringify({ ...effectful, enabled: 5 }) },
    {
      shape: 'a dialog whose message is not text',
      text: JSON.stringify({ ...effectful, dialogs: [{ message: 5, title: null }] }),
    },
    {
      shape: 'a dialog without its title',
      text: JSON.stringify({ ...effectful, dialogs: [{ message: FEVER_DIALOG.message }] }),
    },
    { shape: '`assigned` as an array', text: JSON.stringify({ ...effectful, assigned: [ASSIGNED_AGE] }) },
    { shape: '`assigned` as text', text: JSON.stringify({ ...effectful, assigned: 'Age' }) },
    {
      shape: 'a diagnostic without its line',
      text: JSON.stringify({
        ...effectful,
        diagnostics: [{ severity: 'warning', form: 'FoodHistory', command: 'ASSIGN Age', message: 'Skipped.' }],
      }),
    },
    // Lines are 1-based in the form's program text.
    ...[0, -1, 1.5].map((line) => ({
      shape: `a diagnostic on line ${line}`,
      text: JSON.stringify({ ...effectful, diagnostics: [{ ...GEOCODE_DIAGNOSTIC, line }] }),
    })),
  ])('applies none of an answer with $shape, shows a notice and sends the next event', async ({ text }) => {
    const { harness, host, queue } = createQueue();
    let drained = false;

    queue.raise('FieldAfter', 'LastName');
    queue.raise('FieldBefore', 'FirstName');
    void queue.drain().then(() => {
      drained = true;
    });
    await answer(harness, 0, rawJson(text));

    expect(host.onNotices.mock.calls).toEqual([
      [["Check code could not run for Last Name. The server's answer could not be read."]],
    ]);
    expect(host.setSnapshot).not.toHaveBeenCalled();
    expect(host.onDialogs).not.toHaveBeenCalled();

    const next = await eventAt(harness.api, 1);
    expect(next).toMatchObject({ event: 'FieldBefore', fieldName: 'FirstName' });
    await answer(harness, 1, json(200, envelope()));
    expect(drained).toBe(true);
  });

  it('applies an answer whose diagnostics are on line 1 or later and passes them on in source order', async () => {
    const { harness, host, queue } = createQueue();
    const onDiagnostics = vi.fn<(diagnostics: readonly Diagnostic[]) => void>();
    host.onDiagnostics = onDiagnostics;
    const firstLine: Diagnostic = { ...GEOCODE_DIAGNOSTIC, line: 1 };

    queue.raise('FieldAfter', 'DOB');
    await answer(
      harness,
      0,
      json(200, envelope({ assigned: { Age: ASSIGNED_AGE }, diagnostics: [firstLine, GEOCODE_DIAGNOSTIC] })),
    );

    expect(host.onNotices).not.toHaveBeenCalled();
    expect(host.snapshot.values.Age).toBe(ASSIGNED_AGE);
    expect(onDiagnostics.mock.calls).toEqual([[[firstLine, GEOCODE_DIAGNOSTIC]]]);
  });

  it.each([
    {
      callback: 'setSnapshot',
      response: (): Response => json(200, envelope({ assigned: { Age: ASSIGNED_AGE } })),
      notice: 'Check code could not run for Last Name. Its effects could not be applied.',
    },
    {
      callback: 'onDialogs',
      response: (): Response => json(200, envelope({ dialogs: [FEVER_DIALOG] })),
      notice: 'Check code could not run for Last Name. Its effects could not be applied.',
    },
    {
      callback: 'onNotices',
      response: (): Response => rawJson('null'),
      notice: "Check code could not run for Last Name. The server's answer could not be read.",
    },
  ] as const)('keeps sending events and drains when host.$callback throws', async ({ callback, response, notice }) => {
    const { harness, host, queue } = createQueue();
    let drained = false;
    host[callback].mockImplementationOnce(() => {
      throw new Error(`${callback} failed`);
    });

    queue.raise('FieldAfter', 'LastName');
    queue.raise('FieldBefore', 'FirstName');
    void queue.drain().then(() => {
      drained = true;
    });
    await answer(harness, 0, response());

    expect(host.onNotices.mock.calls).toEqual([[[notice]]]);
    const next = await eventAt(harness.api, 1);
    expect(next).toMatchObject({ event: 'FieldBefore', fieldName: 'FirstName' });
    await answer(harness, 1, json(200, envelope({ assigned: { Age: ASSIGNED_AGE } })));
    expect(drained).toBe(true);
    expect(queue.size).toBe(0);
    expect(host.snapshot.values.Age).toBe(ASSIGNED_AGE);
  });
});

// The queue inside the record editor

const RECORD_A = recordFixture({
  globalRecordId: RECORD_A_ID,
  uniqueKey: 1,
  position: 1,
  values: { LastName: 'Smith', FirstName: 'Ann', DateofInterview: INTERVIEW_DATE },
});
const RECORD_B = recordFixture({
  globalRecordId: RECORD_B_ID,
  uniqueKey: 2,
  position: 2,
  values: { LastName: 'Jones', FirstName: 'Bo', DateofInterview: '2011-06-15' },
});

interface EditorProps {
  readonly record: RecordDto;
  readonly recordGeneration: number;
}

/** Mounts the editor on record A, generation 1, and lets the mount's replacement window close. */
async function mountEditor(harness: Harness) {
  const onSaved = vi.fn<(record: RecordDto) => void>();
  const onDeleted = vi.fn<() => void>();
  const onConflict = vi.fn<() => void>();
  const onDiscardToNearest = vi.fn<(notice?: string) => void>();
  const view = renderHook(
    ({ record, recordGeneration }: EditorProps) =>
      useRecordEditor({
        client: harness.client,
        viewId: VIEW_ID,
        definition,
        record,
        isNew: false,
        recordGeneration,
        onSaved,
        onDeleted,
        onConflict,
        onDiscardToNearest,
      }),
    { initialProps: { record: RECORD_A, recordGeneration: 1 } },
  );
  // The record installed at mount raises no event until a tick after it is committed.
  await act(flush);
  return { ...view, onSaved };
}

describe('useRecordEditor field events', () => {
  it('saves after a DOB blur once its event is answered, with the assigned Age and without OnsetWeek', async () => {
    const harness = createHarness([RECORD_A]);
    const { api } = harness;
    const { result, onSaved } = await mountEditor(harness);
    expect(readOnlyFieldNames).toContain(READ_ONLY);

    act(() => {
      result.current.onFieldFocus('DOB');
    });
    await answer(harness, 0, json(200, envelope()));
    act(() => {
      result.current.setValue('DOB', DOB_ENTERED);
      result.current.onFieldBlur('DOB');
    });
    const blur = await eventAt(api, 1);
    expect(blur).toMatchObject({ event: 'FieldAfter', fieldName: 'DOB' });
    expect(blur.values).toMatchObject({ DOB: DOB_ENTERED, DateofInterview: INTERVIEW_DATE, Age: null });

    // save() drains the queue first: while the blur is unanswered nothing is written.
    const saving = result.current.save();
    await act(flush);
    expect(api.puts()).toEqual([]);

    const saved = await perform(() => {
      blur.respond(json(200, envelope({ assigned: { Age: ASSIGNED_AGE, [READ_ONLY]: 5 } })));
      return saving;
    });

    expect(saved).toBe(true);
    expect(api.calls.map((call) => call.method)).toEqual(['POST', 'POST', 'PUT']);
    expect(api.puts()).toEqual([{ DOB: DOB_ENTERED, Age: ASSIGNED_AGE }]);
    expect(onSaved).toHaveBeenCalledTimes(1);
    // The assigned read-only value is shown, and stays on screen after the save.
    expect(result.current.values).toMatchObject({ DOB: DOB_ENTERED, Age: ASSIGNED_AGE, [READ_ONLY]: 5 });
    expect(result.current.dirty.size).toBe(0);
  });

  // EC-FEVER-01's `Fever = (-)` matches only `false`. A box the user never touched is `null`
  // in the draft, on a new record and on a stored record whose bit column is NULL; it is
  // shown unchecked and a new record's POST saves it as false, so every event sends false.
  it.each<{ record: string; open: RecordDto | null }>([
    { record: 'a new record', open: null },
    { record: 'a stored record whose checkbox columns are NULL', open: RECORD_A },
  ])('sends each untouched checkbox of $record as false, read-only ones included', async ({ open }) => {
    const harness = createHarness(open === null ? [] : [open]);
    const { result } = renderHook(() =>
      useRecordEditor({
        client: harness.client,
        viewId: VIEW_ID,
        definition,
        record: open,
        isNew: open === null,
        recordGeneration: 1,
        onSaved: vi.fn<(record: RecordDto) => void>(),
        onDeleted: vi.fn<() => void>(),
        onConflict: vi.fn<() => void>(),
        onDiscardToNearest: vi.fn<(notice?: string) => void>(),
      }),
    );
    await act(flush);
    expect(CHECKBOX_NAMES.filter((name) => readOnlyFieldNames.includes(name))).toEqual(['Headache']);
    expect(CHECKBOX_NAMES.map((name) => result.current.values[name])).toEqual(CHECKBOX_NAMES.map(() => null));

    act(() => {
      result.current.setValue('FeverTemp', 101);
      result.current.onFieldBlur('FeverTemp');
    });
    const blur = await eventAt(harness.api, 0);
    await answer(harness, 0, json(200, envelope()));

    expect(blur).toMatchObject({ event: 'FieldAfter', fieldName: 'FeverTemp' });
    expect(blur.values).toMatchObject({
      FeverTemp: 101,
      ...Object.fromEntries(CHECKBOX_NAMES.map((name) => [name, false])),
    });
    // Only the event's copy changes: the draft still holds null for each box.
    expect(CHECKBOX_NAMES.map((name) => result.current.values[name])).toEqual(CHECKBOX_NAMES.map(() => null));
  });

  // An answer the API writes with an exact number in `assigned`, beside every other kind of
  // effect: the client decodes the number exactly, the queue applies the whole answer, and
  // the save sends the number back as the same JSON token. 9007199254740993 is a bigint;
  // 1.0000000000000001, whose nearest double is the whole number 1, is an exact decimal, so
  // the server, not a rounded 1, decides whether the field's column can hold it.
  it.each<{ token: string }>([{ token: '9007199254740993' }, { token: '1.0000000000000001' }])(
    'applies in full an answer assigning $token to a number field and saves that token digit for digit',
    async ({ token }) => {
      const harness = createHarness([RECORD_A]);
      const { api } = harness;
      const { result, onSaved } = await mountEditor(harness);
      const answerText = JSON.stringify(
        envelope({ disabled: ['LastName'], cleared: ['FirstName'], dialogs: [FEVER_DIALOG] }),
      ).replace('"assigned":{}', `"assigned":{"${PLAIN_NUMBER}":${token}}`);

      act(() => {
        result.current.onFieldBlur('LastName');
      });
      await answer(harness, 0, rawJson(answerText));

      const expected = numberFromText(token);
      expect(sameFieldValue(result.current.values[PLAIN_NUMBER], expected)).toBe(true);
      expect(result.current.values.FirstName).toBeNull();
      expect([...result.current.checkCodeDisabled]).toEqual(['LastName']);
      expect(result.current.dialogs).toEqual([FEVER_DIALOG]);
      expect(result.current.eventNotices).toEqual([]);
      expect([...result.current.dirty].sort()).toEqual([PLAIN_NUMBER, 'FirstName'].sort());

      expect(await perform(() => result.current.save())).toBe(true);

      const writes = api.calls.filter((call) => call.method === 'PUT');
      expect(writes.map((call) => call.text)).toEqual([`{"values":{"${PLAIN_NUMBER}":${token},"FirstName":null}}`]);
      expect(onSaved).toHaveBeenCalledTimes(1);
    },
  );

  it.each<{ effect: string; response: FieldEventResponse; put: Values | null }>([
    { effect: 'an assigned Age', response: envelope({ assigned: { Age: ASSIGNED_AGE } }), put: { Age: ASSIGNED_AGE } },
    { effect: 'a cleared FirstName', response: envelope({ cleared: ['FirstName'] }), put: { FirstName: null } },
    { effect: 'no effect', response: envelope(), put: null },
  ])('decides that an unedited record is clean only after its pending event answers with $effect', async (row) => {
    const { response, put } = row;
    const harness = createHarness([RECORD_A]);
    const { api } = harness;
    api.holdPuts();
    const { result, onSaved } = await mountEditor(harness);

    // A blur without an edit: the draft is clean while the event is pending.
    act(() => {
      result.current.onFieldBlur('DOB');
    });
    await eventAt(api, 0);
    expect(result.current.isDirty).toBe(false);
    let left: boolean | undefined;
    const leaving = result.current.leave().then((value) => {
      left = value;
      return value;
    });
    await act(flush);
    expect(left).toBeUndefined();
    expect(api.calls.map((call) => call.method)).toEqual(['POST']);
    expect(onSaved).not.toHaveBeenCalled();

    await answer(harness, 0, json(200, response));
    if (put !== null) {
      // The effect made the draft dirty, so leaving saves it first and waits for the PUT.
      const held = await putAt(api, 0);
      await act(flush);
      expect(left).toBeUndefined();
      expect(onSaved).not.toHaveBeenCalled();
      held.save();
    }

    expect(await perform(() => leaving)).toBe(true);
    expect(api.puts()).toStrictEqual(put === null ? [] : [put]);
    expect(onSaved).toHaveBeenCalledTimes(put === null ? 0 : 1);
    expect(result.current.isDirty).toBe(false);
  });

  it.each<{ edit: string; before: string | null; after: string | null; saved: string | null }>([
    { edit: 'no edit', before: null, after: null, saved: null },
    { edit: 'an edit before the event', before: 'Anna', after: null, saved: null },
    { edit: 'an edit after the event', before: null, after: 'Anna', saved: 'Anna' },
  ])('saves FirstName as $saved after check code cleared it, with $edit', async (row) => {
    const { before, after, saved } = row;
    const harness = createHarness([RECORD_A]);
    const { api } = harness;
    const { result, onSaved } = await mountEditor(harness);
    expect(result.current.values.FirstName).toBe('Ann');

    act(() => {
      if (before !== null) {
        result.current.setValue('FirstName', before);
      }
      result.current.onFieldBlur('FirstName');
    });
    await eventAt(api, 0);
    if (after !== null) {
      act(() => {
        result.current.setValue('FirstName', after);
      });
    }
    await answer(harness, 0, json(200, envelope({ cleared: ['FirstName'] })));
    expect(result.current.values.FirstName).toBe(saved);
    expect([...result.current.dirty]).toEqual(['FirstName']);

    expect(await perform(() => result.current.save())).toBe(true);

    // A cleared value goes out as an explicit null, never as a missing key.
    const writes = api.calls.filter((call) => call.method === 'PUT');
    expect(writes.map((call) => call.body)).toStrictEqual([{ values: { FirstName: saved } }]);
    expect(Object.hasOwn(api.puts()[0], 'FirstName')).toBe(true);
    expect(onSaved).toHaveBeenCalledTimes(1);
    expect(result.current.values.FirstName).toBe(saved);
    expect(result.current.dirty.size).toBe(0);
  });

  it('saves after a FieldAfter DOB that the save drained failed with 500, and keeps its notice after the save', async () => {
    const harness = createHarness([RECORD_A]);
    const { api } = harness;
    api.holdPuts();
    const { result, onSaved } = await mountEditor(harness);
    const notice = `Check code could not run for DOB. An unexpected error occurred. Trace id: ${TRACE_ID}.`;

    act(() => {
      result.current.setValue('DOB', DOB_ENTERED);
      result.current.onFieldBlur('DOB');
    });
    await eventAt(api, 0);
    const saving = result.current.save();
    await act(flush);
    expect(api.puts()).toEqual([]);

    // The failed event counts as drained, so the save goes ahead while the notice is shown.
    await answer(harness, 0, problem(500));
    const put = await putAt(api, 0);
    expect(put.values).toEqual({ DOB: DOB_ENTERED });
    expect(result.current.saving).toBe(true);
    expect(result.current.eventNotices).toEqual([notice]);
    expect(result.current.banner).toBeNull();

    const saved = await perform(() => {
      put.save();
      return saving;
    });

    expect(saved).toBe(true);
    expect(api.puts()).toEqual([{ DOB: DOB_ENTERED }]);
    expect(onSaved).toHaveBeenCalledTimes(1);
    // A save runs no check code: Age was not recalculated, and the notice still says so.
    expect(result.current.eventNotices).toEqual([notice]);
    expect(result.current.banner).toBeNull();
  });

  it('keeps the notice of an event that failed with 503 through a failed save and the Retry that saves the record', async () => {
    const harness = createHarness([RECORD_A]);
    const { api } = harness;
    api.holdPuts();
    const { result, onSaved } = await mountEditor(harness);
    const notice =
      'Check code could not run for First Name. The database cannot be reached. Your changes are still on screen.';

    act(() => {
      result.current.setValue('FirstName', 'Anna');
      result.current.onFieldBlur('FirstName');
    });
    await answer(harness, 0, problem(503));
    expect(result.current.eventNotices).toEqual([notice]);

    // The database is still unreachable: the PUT writes nothing, so Retry is offered.
    const saving = result.current.save();
    const failed = await putAt(api, 0);
    const saved = await perform(() => {
      failed.respond(problem(503, 'notWritten'));
      return saving;
    });
    expect(saved).toBe(false);
    expect(result.current.banner).toMatchObject({
      kind: 'unavailable',
      message: 'The database cannot be reached. Your changes are still on screen.',
      actions: ['retry'],
    });
    expect(result.current.eventNotices).toEqual([notice]);
    expect(onSaved).not.toHaveBeenCalled();

    // The database is back: Retry resends the same PUT, which succeeds.
    const retrying = result.current.retry();
    const resent = await putAt(api, 1);
    await perform(() => {
      resent.save();
      return retrying;
    });

    expect(api.puts()).toEqual([{ FirstName: 'Anna' }, { FirstName: 'Anna' }]);
    expect(onSaved).toHaveBeenCalledTimes(1);
    expect(result.current.banner).toBeNull();
    // The record is saved, but First Name's check code still has not run.
    expect(result.current.eventNotices).toEqual([notice]);
    expect(result.current.values.FirstName).toBe('Anna');
    expect(result.current.dirty.size).toBe(0);
  });

  it("keeps a failure notice through raising and answering other events, until the field's own FieldAfter succeeds", async () => {
    const harness = createHarness();
    const { result } = await mountEditor(harness);
    const notice = `Check code could not run for Last Name. An unexpected error occurred. Trace id: ${TRACE_ID}.`;

    act(() => {
      result.current.onFieldBlur('LastName');
    });
    await answer(harness, 0, problem(500));
    const shown = result.current.eventNotices;
    expect(shown).toEqual([notice]);

    // QAFE-F03: raising FieldBefore on another field leaves the notice as it is, so the
    // layout does not move while focus does.
    act(() => {
      result.current.onFieldFocus('FirstName');
    });
    expect(result.current.eventNotices).toBe(shown);
    // UXFB-F13: neither another field's successful events nor the failed field's own
    // FieldBefore resolve a FieldAfter failure.
    await answer(harness, 1, json(200, envelope()));
    act(() => {
      result.current.onFieldBlur('FirstName');
      result.current.onFieldFocus('LastName');
    });
    await answer(harness, 2, json(200, envelope()));
    await answer(harness, 3, json(200, envelope()));
    expect(result.current.eventNotices).toBe(shown);

    act(() => {
      result.current.onFieldBlur('LastName');
    });
    await answer(harness, 4, json(200, envelope()));
    expect(sentEvents(harness.api)).toEqual([
      { event: 'FieldAfter', fieldName: 'LastName' },
      { event: 'FieldBefore', fieldName: 'FirstName' },
      { event: 'FieldAfter', fieldName: 'FirstName' },
      { event: 'FieldBefore', fieldName: 'LastName' },
      { event: 'FieldAfter', fieldName: 'LastName' },
    ]);
    expect(result.current.eventNotices).toEqual([]);
  });

  it('lists both failures, in order, when the next event is answered 200 without the response envelope', async () => {
    const harness = createHarness();
    const { result } = await mountEditor(harness);
    const lastName =
      'Check code could not run for Last Name. The database cannot be reached. Your changes are still on screen.';

    act(() => {
      result.current.onFieldBlur('LastName');
      result.current.onFieldFocus('FirstName');
    });
    await answer(harness, 0, problem(503));
    expect(result.current.eventNotices).toEqual([lastName]);

    // An OK status alone is no success: the answer is unreadable, so it reports its own failure.
    await answer(harness, 1, rawJson('{"message":"Not available"}'));
    expect(result.current.eventNotices).toEqual([
      lastName,
      "Check code could not run for First Name. The server's answer could not be read.",
    ]);
    expect(result.current.values).toEqual(RECORD_A.values);
    expect(result.current.dirty.size).toBe(0);
  });

  it('drops the notices of a record that was replaced', async () => {
    const harness = createHarness();
    const { result, rerender } = await mountEditor(harness);

    act(() => {
      result.current.onFieldBlur('LastName');
    });
    await answer(harness, 0, problem(500));
    expect(result.current.eventNotices).toHaveLength(1);

    rerender({ record: RECORD_B, recordGeneration: 2 });
    await act(flush);
    expect(result.current.eventNotices).toEqual([]);
  });

  it('keeps the notice of an event that failed while a save was in flight after that save succeeds', async () => {
    const harness = createHarness([RECORD_A]);
    const { api } = harness;
    api.holdPuts();
    const { result, onSaved } = await mountEditor(harness);
    const notice = `Check code could not run for First Name. An unexpected error occurred. Trace id: ${TRACE_ID}.`;

    act(() => {
      result.current.setValue('LastName', 'Jones');
    });
    const saving = result.current.save();
    const put = await putAt(api, 0);

    act(() => {
      result.current.onFieldFocus('FirstName');
    });
    await answer(harness, 0, problem(500));
    expect(result.current.eventNotices).toEqual([notice]);

    const saved = await perform(() => {
      put.save();
      return saving;
    });

    expect(saved).toBe(true);
    expect(api.puts()).toEqual([{ LastName: 'Jones' }]);
    expect(onSaved).toHaveBeenCalledTimes(1);
    // The event failed after the PUT was sent, so the save's success says nothing about it.
    expect(result.current.eventNotices).toEqual([notice]);
  });

  it('reports the save pending, with no request in flight, while it waits for an unanswered field event', async () => {
    const harness = createHarness([RECORD_A]);
    const { api } = harness;
    const { result } = await mountEditor(harness);

    act(() => {
      result.current.setValue('LastName', 'Jones');
      result.current.onFieldBlur('LastName');
    });
    const blur = await eventAt(api, 0);
    const saving = result.current.save();
    await act(flush);

    // The drain holds the write back; only savePending tells that the save is under way.
    expect(api.puts()).toEqual([]);
    expect(result.current.savePending).toBe(true);
    expect(result.current.saving).toBe(false);

    const saved = await perform(() => {
      blur.respond(json(200, envelope()));
      return saving;
    });

    expect(saved).toBe(true);
    expect(api.puts()).toEqual([{ LastName: 'Jones' }]);
    expect(result.current.savePending).toBe(false);
    expect(result.current.saving).toBe(false);
  });

  it('raises no event for the blur and focus that replacing the rendered record causes', async () => {
    const harness = createHarness();
    const { api } = harness;
    const { result, rerender } = await mountEditor(harness);

    act(() => {
      result.current.onFieldFocus('LastName');
    });
    expect(sentEvents(api)).toEqual([]);
    await answer(harness, 0, json(200, envelope()));
    expect(sentEvents(api)).toEqual([{ event: 'FieldBefore', fieldName: 'LastName' }]);

    rerender({ record: RECORD_B, recordGeneration: 2 });
    // Delivered synchronously after the commit, before the timer that ends the replacement.
    act(() => {
      result.current.onFieldBlur('LastName');
      result.current.onFieldFocus('FirstName');
    });
    await act(flush);
    expect(api.events).toHaveLength(1);

    // Once the replacement is over, a focus is the user's own and carries record B's values.
    act(() => {
      result.current.onFieldFocus('FirstName');
    });
    const focus = await eventAt(api, 1);
    expect(focus).toMatchObject({ event: 'FieldBefore', fieldName: 'FirstName' });
    expect(focus.values).toMatchObject({ LastName: 'Jones', FirstName: 'Bo' });
    await answer(harness, 1, json(200, envelope()));
  });

  it('ignores a response that arrives after the record was replaced', async () => {
    const harness = createHarness();
    const { api } = harness;
    const { result, rerender } = await mountEditor(harness);

    act(() => {
      result.current.setValue('DOB', DOB_ENTERED);
      result.current.onFieldBlur('DOB');
    });
    await eventAt(api, 0);
    rerender({ record: RECORD_B, recordGeneration: 2 });
    await act(flush);

    await answer(harness, 0, staleEffects());

    expect(result.current.values).toEqual(RECORD_B.values);
    expect(result.current.dirty.size).toBe(0);
    expect(result.current.checkCodeDisabled.size).toBe(0);
    expect(result.current.dialogs).toEqual([]);
    expect(result.current.eventNotices).toEqual([]);
  });

  it('appends the dialogs of each response, in order, for the form to show one at a time', async () => {
    const harness = createHarness();
    const { api } = harness;
    const { result } = await mountEditor(harness);

    act(() => {
      result.current.setValue('FeverTemp', 101.2);
      result.current.onFieldBlur('FeverTemp');
      result.current.setValue(CHECKBOX, true);
      result.current.onFieldClick(CHECKBOX);
    });
    await answer(harness, 0, json(200, envelope({ dialogs: [FEVER_DIALOG, ONSET_DIALOG] })));
    expect(result.current.dialogs).toEqual([FEVER_DIALOG, ONSET_DIALOG]);

    const click = await eventAt(api, 1);
    expect(click).toMatchObject({ event: 'FieldClick', fieldName: CHECKBOX });
    expect(click.values).toMatchObject({ FeverTemp: 101.2, [CHECKBOX]: true });
    await answer(harness, 1, json(200, envelope({ dialogs: [RACE_DIALOG] })));
    expect(result.current.dialogs).toEqual([FEVER_DIALOG, ONSET_DIALOG, RACE_DIALOG]);

    act(() => {
      result.current.dismissDialog();
    });
    expect(result.current.dialogs).toEqual([ONSET_DIALOG, RACE_DIALOG]);
  });
});
