// SC-10, effects reach the screen: a check-code field event raised from the form view
// comes back as effects that the rendered controls show. Fields are enabled, disabled,
// cleared and assigned, and DIALOG text is shown, one message at a time. Events are
// raised as legacy Enter raised them: FieldBefore on focus, FieldAfter on blur, and
// FieldClick after a checkbox or radio toggled. Opening a record raises none (DATA-01).
//
// FormView runs against the real client, editor, field-event queue and effect
// application; createApiClient's fetch seam is an in-memory API that serves the fixture
// form and one record, and answers each field event with the response a test configured
// for its `${event}:${fieldName}`.
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';

import { createApiClient } from '../api/client';
import type {
  FieldEventKind,
  FieldEventResponse,
  FieldValue,
  FormDefinition,
  ProblemDetails,
  RecordCountResponse,
  RecordDto,
  RecordPage,
  Values,
} from '../api/types';
import {
  createFormDefinitionFixture,
  fieldByName,
  recordFixture,
  recordSummaryFixture,
} from '../test/formDefinitionFixture';
import { FormView } from './FormView';

const definition = createFormDefinitionFixture();
const VIEW_ID = definition.viewId;
const FORM_PATH = `/api/forms/${VIEW_ID}`;
const COUNT_PATH = `${FORM_PATH}/records/count`;
const FIRST_RECORD_PATH = `${FORM_PATH}/records/at/1`;
const LIST_PATH = `${FORM_PATH}/records`;
const FIELD_EVENT_PATH = `${FORM_PATH}/checkcode/field-event`;

/**
 * The one stored record, the only record of the set and so intrinsically 1 of 1: values
 * a test can watch being cleared or overwritten, and EC-AGE-03's interview date.
 */
const RECORD: RecordDto = recordFixture({
  position: 1,
  count: 1,
  values: { LastName: 'Smith', DateofInterview: '2011-02-28', Black: true, Asian: false },
});

/** EC-AGE-03: YEARS(DOB, DateofInterview) assigns 11 for this leap-day birth date. */
const DOB_ENTERED = '2000-02-29';
const ASSIGNED_AGE = 11;

// Wire bodies

type EventKey = `${FieldEventKind}:${string}`;

/** Field-event responses by `${event}:${fieldName}`; an unlisted event gets the empty envelope. */
type ConfiguredResponses = Partial<Record<EventKey, Partial<FieldEventResponse>>>;

function envelope(effects: Partial<FieldEventResponse> = {}): FieldEventResponse {
  return { enabled: [], disabled: [], cleared: [], assigned: {}, dialogs: [], diagnostics: [], ...effects };
}

function json(body: FormDefinition | RecordCountResponse | RecordDto | RecordPage | FieldEventResponse): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'content-type': 'application/json' } });
}

function notFound(): Response {
  const body: ProblemDetails = {
    type: 'about:blank',
    title: 'Not Found',
    status: 404,
    detail: 'The resource was not found.',
    traceId: '00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01',
  };
  return new Response(JSON.stringify(body), { status: 404, headers: { 'content-type': 'application/problem+json' } });
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
  if (!isRecord(values)) {
    throw new Error('the field-event `values` is not an object');
  }
  const entries: [string, FieldValue][] = [];
  for (const [name, entry] of Object.entries(values)) {
    if (!isFieldValue(entry)) {
      throw new Error(`values.${name} is not a scalar field value`);
    }
    entries.push([name, entry]);
  }
  return { event, fieldName, values: Object.fromEntries(entries) };
}

// The in-memory API

interface FakeApi {
  readonly fetch: (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>;
  /** Request lines (`GET /api/forms/2`) in arrival order. */
  readonly requests: readonly string[];
  /** Field-event requests in arrival order. */
  readonly events: readonly SentEvent[];
  /** Requests the fake could not route or parse, and any write: a field event writes nothing. */
  readonly faults: readonly string[];
}

/** Every fake API of the running test, checked after it. */
const apisOfTest: FakeApi[] = [];

afterEach(() => {
  for (const api of apisOfTest.splice(0)) {
    expect(api.faults).toEqual([]);
  }
});

function createFakeApi(responses: ConfiguredResponses): FakeApi {
  const requests: string[] = [];
  const events: SentEvent[] = [];
  const faults: string[] = [];

  // The list row carries the grid columns only, as the API returns it.
  const page: RecordPage = {
    items: [recordSummaryFixture(RECORD, definition)],
    total: 1,
    page: 1,
    pageSize: 50,
  };

  const routeGet = (path: string): Response | null => {
    switch (path) {
      case FORM_PATH:
        return json(definition);
      case COUNT_PATH:
        return json({ count: 1 });
      case FIRST_RECORD_PATH:
        return json(RECORD);
      case LIST_PATH:
        return json(page);
      default:
        return null;
    }
  };

  const fetchFromFake = async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
    // openapi-fetch hands over the Request it built; anything else is normalised to one.
    const request = input instanceof Request ? input : new Request(input, init);
    const path = new URL(request.url).pathname;
    const line = `${request.method} ${path}`;
    requests.push(line);

    try {
      if (request.method === 'GET') {
        const response = routeGet(path);
        if (response !== null) {
          return response;
        }
      } else if (request.method === 'POST' && path === FIELD_EVENT_PATH) {
        const text = await request.text();
        const sent = readFieldEvent(JSON.parse(text));
        events.push(sent);
        return json(envelope(responses[`${sent.event}:${sent.fieldName}`]));
      }
      faults.push(`unrouted ${line}`);
    } catch (e) {
      faults.push(`${line}: ${e instanceof Error ? e.message : String(e)}`);
    }
    return notFound();
  };

  const api: FakeApi = { fetch: fetchFromFake, requests, events, faults };
  apisOfTest.push(api);
  return api;
}

// Rendering and interaction

/** Lets every pending promise callback and zero-delay timer run. */
async function flush(): Promise<void> {
  await act(async () => {
    await new Promise<void>((resolve) => {
      setTimeout(resolve, 0);
    });
  });
}

/**
 * Renders the form view and waits until the record and the grid are on screen and the
 * editor has finished installing the record: the focus and blur that installing causes
 * raise no event, so a test's own focus must come after it.
 */
async function openForm(responses: ConfiguredResponses = {}): Promise<FakeApi> {
  const api = createFakeApi(responses);
  render(
    <FormView
      viewId={VIEW_ID}
      client={createApiClient({ baseUrl: 'http://localhost', fetch: api.fetch })}
      onExit={() => undefined}
      onFormNotFound={() => undefined}
    />,
  );
  await waitFor(() => {
    expect(fieldControl('LastName').value).toBe('Smith');
  });
  await waitFor(() => {
    expect(screen.getByRole('table', { name: `${definition.name} records` }).getAttribute('aria-busy')).toBe('false');
  });
  await flush();
  return api;
}

/** The input labelled `label`: a field prompt, or an option's label for a radio. */
function control(label: string): HTMLInputElement {
  const element = screen.getByLabelText(label);
  if (!(element instanceof HTMLInputElement)) {
    throw new Error(`the control labelled ${label} is not an input`);
  }
  return element;
}

/** The control of a fixture field, found by its prompt as a user finds it. */
function fieldControl(name: string): HTMLInputElement {
  const prompt = fieldByName(name, definition).prompt;
  return control(prompt ?? name);
}

// Real focus moves, which jsdom reports with focusin and focusout, the events React's
// onFocus and onBlur listen to.
function focus(element: HTMLElement): void {
  act(() => {
    element.focus();
  });
}

function blur(element: HTMLElement): void {
  act(() => {
    element.blur();
  });
}

/** Enters `value` in a field: focus (FieldBefore), the change, then blur (FieldAfter). */
function enter(name: string, value: string): void {
  const element = fieldControl(name);
  focus(element);
  fireEvent.change(element, { target: { value } });
  blur(element);
}

function eventKeys(api: FakeApi): string[] {
  return api.events.map((sent) => `${sent.event}:${sent.fieldName}`);
}

function sentEvent(api: FakeApi, key: EventKey): SentEvent {
  const found = api.events.find((sent) => `${sent.event}:${sent.fieldName}` === key);
  if (found === undefined) {
    throw new Error(`no ${key} event was sent`);
  }
  return found;
}

describe('FieldEffects: check-code effects reach the screen (SC-10)', () => {
  it('raises no field event while the form and its record load (DATA-01)', async () => {
    const api = await openForm();

    expect(api.requests).toContain(`GET ${FIRST_RECORD_PATH}`);
    expect(api.requests.filter((line) => !line.startsWith('GET '))).toEqual([]);
    expect(api.events).toEqual([]);
    // Stored values are shown as loaded: nothing was assigned or cleared.
    expect(fieldControl('Age').value).toBe('');
    expect(fieldControl('Black').checked).toBe(true);
  });

  it('shows an assigned value after FieldAfter, which carries the edited value', async () => {
    const api = await openForm({ 'FieldAfter:DOB': { assigned: { Age: ASSIGNED_AGE } } });

    enter('DOB', DOB_ENTERED);

    await waitFor(() => {
      expect(fieldControl('Age').value).toBe(String(ASSIGNED_AGE));
    });
    expect(eventKeys(api)).toEqual(['FieldBefore:DOB', 'FieldAfter:DOB']);
    expect(sentEvent(api, 'FieldAfter:DOB').values).toMatchObject({
      DOB: DOB_ENTERED,
      DateofInterview: '2011-02-28',
      Age: null,
    });
    expect(fieldControl('Age').disabled).toBe(false);
  });

  it('disables a field, and a later event enables it again', async () => {
    const api = await openForm({
      'FieldAfter:DOB': { disabled: ['LastName'] },
      'FieldBefore:FirstName': { enabled: ['LastName'] },
    });
    expect(fieldControl('LastName').disabled).toBe(false);

    const dob = fieldControl('DOB');
    focus(dob);
    blur(dob);
    await waitFor(() => {
      expect(fieldControl('LastName').disabled).toBe(true);
    });

    focus(fieldControl('FirstName'));
    await waitFor(() => {
      expect(fieldControl('LastName').disabled).toBe(false);
    });
    expect(eventKeys(api)).toEqual(['FieldBefore:DOB', 'FieldAfter:DOB', 'FieldBefore:FirstName']);
  });

  it('never unlocks a protected field: ENABLE of a read-only field leaves it disabled', async () => {
    const api = await openForm({ 'FieldAfter:DOB': { enabled: ['Headache', 'OnsetWeek'], assigned: { Age: 30 } } });
    expect(fieldControl('Headache').disabled).toBe(true);

    const dob = fieldControl('DOB');
    focus(dob);
    blur(dob);
    // The assigned Age shows that the response, ENABLE included, has been applied.
    await waitFor(() => {
      expect(fieldControl('Age').value).toBe('30');
    });

    const headache = fieldControl('Headache');
    expect(headache.disabled).toBe(true);
    expect(fieldControl('OnsetWeek').disabled).toBe(true);
    fireEvent.click(headache);
    await flush();
    expect(headache.checked).toBe(false);
    expect(eventKeys(api)).toEqual(['FieldBefore:DOB', 'FieldAfter:DOB']);
  });

  it('empties a cleared text field and unchecks a cleared checkbox', async () => {
    await openForm({ 'FieldAfter:DOB': { cleared: ['LastName', 'Black'] } });
    expect(fieldControl('LastName').value).toBe('Smith');
    expect(fieldControl('Black').checked).toBe(true);

    const dob = fieldControl('DOB');
    focus(dob);
    blur(dob);

    await waitFor(() => {
      expect(fieldControl('LastName').value).toBe('');
    });
    expect(fieldControl('Black').checked).toBe(false);
  });

  it('shows each DIALOG in order, titled "Message" without TITLETEXT, until the last OK', async () => {
    const api = await openForm({
      'FieldAfter:DOB': {
        dialogs: [
          { message: 'Check the onset date', title: null },
          { message: 'Second', title: 'Notice' },
        ],
      },
    });

    const dob = fieldControl('DOB');
    focus(dob);
    blur(dob);

    const first = await screen.findByRole('dialog', { name: 'Message' });
    expect(within(first).queryByText('Check the onset date')).not.toBeNull();
    fireEvent.click(within(first).getByRole('button', { name: 'OK' }));

    const second = await screen.findByRole('dialog', { name: 'Notice' });
    expect(within(second).queryByText('Second')).not.toBeNull();
    fireEvent.click(within(second).getByRole('button', { name: 'OK' }));

    await waitFor(() => {
      expect(screen.queryByRole('dialog')).toBeNull();
    });
    // Opening and closing the dialogs raised no event of its own.
    expect(eventKeys(api)).toEqual(['FieldBefore:DOB', 'FieldAfter:DOB']);
  });

  it('shows a value assigned to a read-only field and keeps the field disabled', async () => {
    const api = await openForm({ 'FieldAfter:OnsetDate': { assigned: { OnsetWeek: 7 } } });

    enter('OnsetDate', '2011-02-15');

    await waitFor(() => {
      expect(fieldControl('OnsetWeek').value).toBe('7');
    });
    expect(fieldControl('OnsetWeek').disabled).toBe(true);
    expect(sentEvent(api, 'FieldAfter:OnsetDate').values).toMatchObject({ OnsetDate: '2011-02-15', OnsetWeek: null });
  });

  // A click sets the value before FieldClick is raised, so the event carries the post-click
  // value (EC-RACE-01, EC-RACE-02), and its effect, as Asian's ASSIGN of (-) to the other
  // race checkboxes, reaches the screen.
  it.each([
    { label: 'Asian', fieldName: 'Asian', posted: true },
    { label: 'Severe', fieldName: 'IllnessSeverity', posted: 2 },
  ])('sends FieldClick on $fieldName with its post-toggle value $posted', async ({ label, fieldName, posted }) => {
    const key: EventKey = `FieldClick:${fieldName}`;
    const api = await openForm({ [key]: { assigned: { Black: false } } });

    fireEvent.click(control(label));

    await waitFor(() => {
      expect(fieldControl('Black').checked).toBe(false);
    });
    expect(control(label).checked).toBe(true);
    expect(eventKeys(api)).toEqual([key]);
    expect(sentEvent(api, key).values).toMatchObject({ [fieldName]: posted, Black: true });
  });
});
