// Ordering and races of check-code field events: the queue sends one event at a time in
// the order raised; each event carries the values the user had when it was raised, plus
// the effects of earlier responses; a user edit made after an event was raised beats that
// event's `cleared`/`assigned`; a response for a replaced record is ignored; and a failed
// event shows a notice without blocking a save.
//
// createFieldEventQueue is tested directly against a mutable editor snapshot, and through
// useRecordEditor, which drains the queue before saving and suppresses the focus and blur
// that replacing the rendered record causes. Both run against the real client and error
// parsing: createApiClient's fetch seam is an in-memory API that holds every field-event
// request until the test answers it.
import { act, renderHook, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi, type Mock } from 'vitest';

import { createApiClient, type ApiClient } from '../api/client';
import { errorMessages } from '../api/errors';
import type {
  DialogMessage,
  FieldEventKind,
  FieldEventResponse,
  FieldValue,
  ProblemDetails,
  RecordDto,
  Values,
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

// Wire bodies

function json(status: number, body: FieldEventResponse | RecordDto): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } });
}

function problem(status: number): Response {
  const body: ProblemDetails = {
    type: 'about:blank',
    title: 'An error occurred while processing your request.',
    status,
    detail: 'An unexpected error occurred.',
    traceId: TRACE_ID,
  };
  return new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/problem+json' } });
}

/** A field-event response: the empty envelope with `effects` laid over it. */
function envelope(effects: Partial<FieldEventResponse> = {}): FieldEventResponse {
  return { enabled: [], disabled: [], cleared: [], assigned: {}, dialogs: [], diagnostics: [], ...effects };
}

/** A full `values` object of the fixture form; recordFixture rejects a name that is not a data field. */
function valuesWith(overrides: Values): Values {
  return recordFixture({ values: overrides }).values;
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
}

/** A field-event request held at the fake until the test answers it. */
interface HeldEvent extends SentEvent {
  readonly respond: (response: Response) => void;
}

interface FakeApi {
  readonly fetch: (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>;
  /** Every request received, in arrival order. */
  readonly calls: readonly RecordedCall[];
  /** Field-event requests in arrival order. */
  readonly events: readonly HeldEvent[];
  /** The `values` of every PUT, in arrival order. */
  readonly puts: () => Values[];
  /** The most field-event requests that were ever unanswered at once. */
  readonly maxOpenEvents: () => number;
  /** Requests the fake could not route or parse. */
  readonly faults: readonly string[];
}

/** Every fake API of the running test, checked after it. */
const apisOfTest: FakeApi[] = [];

/**
 * Field events are held until answered. A PUT of a `stored` record answers at once with
 * the record and the submitted values laid over it, as the server's saved DTO.
 */
function createFakeApi(stored: readonly RecordDto[]): FakeApi {
  const calls: RecordedCall[] = [];
  const events: HeldEvent[] = [];
  const faults: string[] = [];
  let open = 0;
  let maxOpen = 0;

  const holdFieldEvent = (sent: SentEvent): Promise<Response> =>
    new Promise<Response>((resolve) => {
      const index = events.length;
      let answered = false;
      open += 1;
      maxOpen = Math.max(maxOpen, open);
      events.push({
        ...sent,
        respond: (response) => {
          if (answered) {
            throw new Error(`field event ${index} was already answered`);
          }
          answered = true;
          open -= 1;
          resolve(response);
        },
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

  const fetchFromFake = async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
    // openapi-fetch hands over the Request it built; anything else is normalised to one.
    const request = input instanceof Request ? input : new Request(input, init);
    const path = new URL(request.url).pathname;
    const text = await request.text();

    try {
      const body: unknown = text === '' ? null : JSON.parse(text);
      calls.push({ method: request.method, path, body });
      if (request.method === 'POST' && path === FIELD_EVENT_PATH) {
        return await holdFieldEvent(readFieldEvent(body));
      }
      const record = RECORD_ROUTE.exec(path);
      if (request.method === 'PUT' && record !== null && Number(record[1]) === VIEW_ID) {
        return saveRecord(record[2], readWriteValues(body));
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
  /** The real client over the fake, which also keeps every raiseFieldEvent result. */
  readonly client: ApiClient;
  /** The client's settled result of the `index`-th field event. */
  readonly eventResult: (index: number) => Promise<unknown>;
}

function createHarness(stored: readonly RecordDto[] = []): Harness {
  const api = createFakeApi(stored);
  const base = createApiClient({ baseUrl: 'http://localhost', fetch: api.fetch });
  const results: Promise<unknown>[] = [];
  const client: ApiClient = {
    ...base,
    raiseFieldEvent: (viewId, request, signal) => {
      const result = base.raiseFieldEvent(viewId, request, signal);
      results.push(result);
      return result;
    },
  };
  return {
    api,
    client,
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

/** Answers the `index`-th field event and waits until the queue has processed the answer. */
async function answer(harness: Harness, index: number, response: Response): Promise<void> {
  const held = await eventAt(harness.api, index);
  await act(async () => {
    held.respond(response);
    await harness.eventResult(index);
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
  readonly onNotice: Mock<(message: string) => void>;
}

function createHost(values: Values): TestHost {
  const host: TestHost = {
    snapshot: { values, checkCodeDisabled: new Set(), dirty: new Set() },
    getSnapshot: () => host.snapshot,
    setSnapshot: vi.fn((next: EditorSnapshot) => {
      host.snapshot = next;
    }),
    onDialogs: vi.fn(),
    onNotice: vi.fn(),
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

function createQueue(values: Values = valuesWith({ DateofInterview: INTERVIEW_DATE })): QueueUnderTest {
  const harness = createHarness();
  const host = createHost(values);
  const queue = createFieldEventQueue({ client: harness.client, viewId: VIEW_ID, definition, host });
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
    { outcome: 'effects', response: staleEffects },
    { outcome: 'a 500', response: (): Response => problem(500) },
  ])('ignores $outcome arriving for a record generation that reset() ended', async ({ response }) => {
    const { harness, host, queue } = createQueue(valuesWith({ LastName: 'Smith' }));
    const { api } = harness;
    let drained = false;

    queue.raise('FieldAfter', 'DOB');
    await eventAt(api, 0);
    void queue.drain().then(() => {
      drained = true;
    });
    await flush();
    expect(drained).toBe(false);

    queue.reset();
    await flush();
    // The waiter is released by the reset itself, not by the stale answer.
    expect(drained).toBe(true);
    expect(queue.size).toBe(0);
    expect(queue.generation).toBe(1);

    // The new generation's event is sent at once; it does not wait for the stale one.
    queue.raise('FieldBefore', 'LastName');
    const fresh = await eventAt(api, 1);
    expect(fresh).toMatchObject({ event: 'FieldBefore', fieldName: 'LastName' });
    expect(queue.size).toBe(1);

    const before = host.snapshot;
    await answer(harness, 0, response());

    expect(host.snapshot).toBe(before);
    expect(host.setSnapshot).not.toHaveBeenCalled();
    expect(host.onDialogs).not.toHaveBeenCalled();
    expect(host.onNotice).not.toHaveBeenCalled();
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

    expect(host.onNotice.mock.calls).toEqual([
      [`Check code could not run for Last Name. ${errorMessages.server}${errorMessages.traceIdSuffix(TRACE_ID)}`],
    ]);
    expect(host.setSnapshot).not.toHaveBeenCalled();

    const next = await eventAt(harness.api, 1);
    expect(next).toMatchObject({ event: 'FieldBefore', fieldName: 'FirstName' });
    await answer(harness, 1, json(200, envelope()));
    expect(drained).toBe(true);
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

  it('saves after a field event that failed with 500, showing a non-blocking notice', async () => {
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
    expect(api.puts()).toEqual([]);

    const saved = await perform(() => {
      blur.respond(problem(500));
      return saving;
    });

    expect(saved).toBe(true);
    expect(api.puts()).toEqual([{ LastName: 'Jones' }]);
    expect(result.current.eventNotice).toBe(
      `Check code could not run for Last Name. ${errorMessages.server}${errorMessages.traceIdSuffix(TRACE_ID)}`,
    );
    expect(result.current.banner).toBeNull();
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
    expect(result.current.eventNotice).toBeNull();
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
