// SC-8, client half: the form view renders a form from its metadata with an accessible
// structure. Each data field is one native control labelled by its prompt, an option
// field is a radio group named by its legend, label fields are headings, a group is a
// section headed by its prompt that holds its same-page members in `tabIndex` order,
// every page is one tab, and every field renders exactly once.
//
// FormView runs against the real client, hooks and field components. The client's fetch
// seam is an in-memory API that answers the read routes of one form and its stored active
// records: one record by default, two or none where a case asks. A PUT of a stored record
// or a POST of a new one is answered with the next reply the case queued, at once or held
// until the case releases it, and a saved reply updates the stored records. A case may
// answer more through two options: its own `route`, tried before every other route, and
// `answersFieldEvents`, which answers field events with no effects. A request nothing
// answers, and a write no reply was queued for, is answered 500 and recorded, and fails the
// case in afterEach. DATA-01, that loading sends GETs only and raises no field event, is
// asserted by the clean-load case, not implied by the 500 default.
//
// The write cases drive the editor through FormView: a dirty draft is saved before a grid
// open or Back to forms continues, and a failed save keeps the draft and cancels the move;
// a save that finds the record deleted (409) keeps it read-only and reloads the grid, and
// Next then re-reads N and opens the record that followed it; the first Save of a new
// record follows canInsert and Save of a stored record canUpdate, and a 403 keeps the draft; the validation summary lists each errored field's prompt
// with its message, from client validation or a field-level 400; and the toolbar
// announces Saving… while a save is under way, a leave's from the start of its field-event
// drain, then Changes saved. until the next edit. The connection-banner cases answer their
// own outage reads and failed writes, and check the banner each shows.
//
// A number field holds an integer beyond ±Number.MAX_SAFE_INTEGER, which a `bigint`
// column admits, exactly: read from JSON, shown and entered digit for digit. A number typed
// as a fraction whose nearest double is whole is sent as typed, in its field event and its
// save. A number field shows the number its text stands for once it loses focus, so Age
// typed as `1e2` shows the 100 it saves, text with a sign or decimal separator and no digit
// is cleared as legacy clears it, and other text that is not a number stays as typed
// beside its error. A date field with a date mask is a text box in that mask, described by
// a "Format:" hint naming it: text that follows the mask is saved as its wire date, and
// other text stays as typed beside a message in the mask's terms. A tabIndex beyond the
// safe range, read from the form route's own body text, still orders the page and Find.
//
// The navigator cases hold a navigation read: a pending load disables no control, marks the
// toolbar and its status busy and announces itself outside them, a press supersedes it, and
// the draft it replaces is frozen, every field disabled and none marked protected.
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { Profiler, useState } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi, type Mock } from 'vitest';

import { createApiClient } from '../api/client';
import type {
  FieldDefinition,
  FieldEventResponse,
  FieldType,
  FieldValue,
  FormDefinition,
  HttpValidationProblemDetails,
  NavigationPosition,
  PageDefinition,
  ProblemDetails,
  RecordCountResponse,
  RecordDto,
  RecordPage,
  RecordSummary,
  SearchResponse,
  SearchResult,
  Values,
  WriteOutcome,
  WriteProblemDetails,
} from '../api/types';
import {
  allFields,
  createFormDefinitionFixture,
  dataFieldNames,
  fieldByName,
  isDataField,
  readOnlyFieldNames,
  recordFixture,
  recordSummaryFixture,
  withField,
  writableCheckboxNames,
} from '../test/formDefinitionFixture';
import { clientMessages } from '../validation/clientValidation';
import { FormView } from './FormView';
import { RecordNavigator, type RecordNavigatorProps } from './RecordNavigator';
import type { RecordGridProps } from './RecordGrid';
import { fieldComponents, fieldControlId, fieldErrorId, type FieldComponentProps } from './fields/fieldComponents';
import { NumberInput } from './fields/NumberInput';

// The record grid FormView renders is the real one inside a memo probe that counts the
// renders getting past React.memo's comparison of its props, the comparison the real grid
// makes itself. Every case here renders the real grid through it.
const gridRenders = vi.hoisted(() => ({ count: 0 }));
vi.mock('./RecordGrid', async (importOriginal) => {
  const actual = await importOriginal<typeof import('./RecordGrid')>();
  const { createElement, memo } = await import('react');
  return {
    ...actual,
    RecordGrid: memo(function CountingRecordGrid(props: RecordGridProps) {
      gridRenders.count += 1;
      return createElement(actual.RecordGrid, props);
    }),
  };
});

// The in-memory API

interface Call {
  readonly method: string;
  readonly url: URL;
  /** The request body text; `null` when the request has none. */
  readonly body: string | null;
}

const calls: Call[] = [];
const unrouted: string[] = [];
/** Answers to the next PUT or POST requests, in arrival order; see replyToWrite and holdWrite. */
const writeAnswers: Promise<WriteReply>[] = [];

beforeEach(() => {
  calls.length = 0;
  unrouted.length = 0;
  writeAnswers.length = 0;
});

afterEach(() => {
  // Unmounted first: a failing check below skips the setup file's cleanup, and a form left
  // mounted would fail the next case too.
  cleanup();
  // A request no route answers fails the case, such as a field event the case did not opt
  // to answer or a write it queued no reply for.
  expect(unrouted).toEqual([]);
  // A reply left over answers a write the case expected and FormView never sent.
  expect(writeAnswers, 'write replies queued but never requested').toHaveLength(0);
});

function requestLine(call: Call): string {
  return `${call.method} ${call.url.pathname}${call.url.search}`;
}

/** The one stored record: active, the only record of the set. */
const RECORD: RecordDto = recordFixture({
  count: 1,
  values: {
    CaseID: 1042,
    FirstName: 'Ada',
    LastName: 'Lovelace',
    Sex: 'F-Female',
    DOB: '1980-05-01',
    Age: 31,
    Asian: false,
    Black: true,
    Headache: false,
    IllnessSeverity: 1,
    OnsetWeek: 3,
    Lab: 'State lab',
  },
});

/** A of the two-record set A, B: the stored record's values at position 1 of 2. */
const RECORD_A: RecordDto = recordFixture({
  globalRecordId: RECORD.globalRecordId,
  uniqueKey: 1,
  position: 1,
  count: 2,
  values: RECORD.values,
});

const B_LAST_NAME = 'Hopper';

/** The edit a case makes to a stored record's LastName before it saves or leaves. */
const EDITED_LAST_NAME = 'Byron';

/** B of the two-record set A, B: active, at position 2 of 2. */
const RECORD_B: RecordDto = recordFixture({
  globalRecordId: '3f2b8c1e-5d4a-4c1b-9e7f-000000000002',
  uniqueKey: 2,
  position: 2,
  count: 2,
  values: {
    CaseID: 2087,
    FirstName: 'Grace',
    LastName: B_LAST_NAME,
    Sex: 'F-Female',
    DOB: '1906-12-09',
    Age: 85,
  },
});

/** `record` as the server returns it after storing `changes`. */
function savedWith(record: RecordDto, changes: Values): RecordDto {
  return { ...record, values: { ...record.values, ...changes } };
}

/** The form's path, for every case that renders the fixture form. */
const FORM_PATH = `/api/forms/${createFormDefinitionFixture().viewId}`;

const UNROUTED_PROBLEM: ProblemDetails = {
  type: 'https://tools.ietf.org/html/rfc9110#section-15.6.1',
  title: 'An error occurred while processing your request.',
  status: 500,
  detail: 'The test API has no route for this request.',
  traceId: '00-test-unrouted-00',
};

/**
 * The 404 of an at/{position} outside 1..count, as the server answers it: an empty form's
 * load reads at/1 beside its count.
 */
const POSITION_NOT_FOUND: ProblemDetails = {
  type: 'https://tools.ietf.org/html/rfc9110#section-15.5.5',
  title: 'Not Found',
  status: 404,
  detail: 'There is no record at that position.',
  traceId: '00-test-position-00',
};

/** A 403 of the permission check. Its text never speaks of permission, so only the client's own banner can. */
const FORBIDDEN_PROBLEM: ProblemDetails = {
  type: 'https://tools.ietf.org/html/rfc9110#section-15.5.4',
  title: 'Forbidden',
  status: 403,
  detail: 'The test server refused this write.',
  traceId: '00-test-forbidden-00',
};

/** A 500 of a write that rolled back, so nothing was written. */
const NOT_WRITTEN_PROBLEM: WriteProblemDetails = {
  type: 'https://tools.ietf.org/html/rfc9110#section-15.6.1',
  title: 'An error occurred while processing your request.',
  status: 500,
  detail: 'The test server failed this write.',
  traceId: '00-test-not-written-00',
  writeOutcome: 'notWritten',
};

/** A field-level 400 carrying `errors`, keyed by canonical field name. */
function fieldLevelProblem(errors: Record<string, string[]>): HttpValidationProblemDetails {
  return {
    type: 'https://tools.ietf.org/html/rfc9110#section-15.5.1',
    title: 'One or more validation errors occurred.',
    status: 400,
    detail: 'The test server rejected these values.',
    errors,
    traceId: '00-test-validation-00',
  };
}

/**
 * The test's own stand-in for the server's required-field message of a field: like the server's
 * required-gate message, it opens with the prompt and names the page.
 */
function serverRequiredMessage(prompt: string, pageName: string): string {
  return `${prompt} must be filled in (page ${pageName}).`;
}

// Marks a bigint inside JSON.stringify's output so it can be written as a bare integer; no
// fixture string holds the mark.
const BIGINT_MARK = '\u0000bigint:';
const MARKED_BIGINT = /"\\u0000bigint:(-?\d+)"/g;

/** JSON as the API writes it: a bigint is the JSON integer it holds, digit for digit. */
function jsonResponse(status: number, body: unknown): Response {
  const marked = JSON.stringify(body, (_key: string, value: unknown) =>
    typeof value === 'bigint' ? BIGINT_MARK + value.toString() : value,
  );
  return new Response(marked.replace(MARKED_BIGINT, '$1'), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

function problemResponse(problem: ProblemDetails): Response {
  return new Response(JSON.stringify(problem), {
    status: problem.status,
    headers: { 'Content-Type': 'application/problem+json' },
  });
}

/**
 * The body of a read route of `definition`'s form over `records`, its active records in
 * position order, or `null` when the request matches none.
 */
function readRouteBody(
  method: string,
  path: string,
  definition: FormDefinition,
  records: readonly RecordDto[],
): unknown {
  if (method !== 'GET') {
    return null;
  }
  const form = `/api/forms/${definition.viewId}`;
  switch (path) {
    case form:
      return definition;
    case `${form}/records`: {
      // Each row carries the six grid columns only, as the API returns it.
      const page: RecordPage = {
        items: records.map((record) => recordSummaryFixture(record, definition)),
        total: records.length,
        page: 1,
        pageSize: 50,
      };
      return page;
    }
    case `${form}/records/count`: {
      const count: RecordCountResponse = { count: records.length };
      return count;
    }
    default:
      break;
  }
  for (const [index, record] of records.entries()) {
    const byId = `${form}/records/${record.globalRecordId}`;
    if (path === byId || path === `${form}/records/at/${index + 1}`) {
      return record;
    }
    if (path === `${byId}/position`) {
      const position: NavigationPosition = { position: index + 1, count: records.length };
      return position;
    }
  }
  return null;
}

/** A write's answer: the record the server stored, or the problem it refused the write with. */
type WriteReply =
  | { readonly kind: 'saved'; readonly record: RecordDto }
  | { readonly kind: 'problem'; readonly problem: ProblemDetails | HttpValidationProblemDetails | WriteProblemDetails };

/** Queues the answer to the next PUT or POST, given as soon as it arrives. */
function replyToWrite(reply: WriteReply): void {
  writeAnswers.push(Promise.resolve(reply));
}

/** A write the fake holds open until the case releases it. */
interface HeldWrite {
  /** Answers the held write; before the write arrives, it is answered on arrival. */
  readonly release: (reply: WriteReply) => void;
}

/** Queues a held answer to the next PUT or POST. */
function holdWrite(): HeldWrite {
  let settle: ((reply: WriteReply) => void) | null = null;
  writeAnswers.push(
    new Promise<WriteReply>((resolve) => {
      settle = resolve;
    }),
  );
  return {
    release: (reply) => {
      if (settle === null) {
        throw new Error('the held write has no resolver');
      }
      settle(reply);
    },
  };
}

/**
 * What a write route addresses: `create` for `POST .../records`, the stored record for
 * `PUT .../records/{id}`, or `null` when the request is no write of `definition`'s form.
 */
function writeTargetOf(
  method: string,
  path: string,
  definition: FormDefinition,
  records: readonly RecordDto[],
): 'create' | RecordDto | null {
  const list = `/api/forms/${definition.viewId}/records`;
  if (method === 'POST' && path === list) {
    return 'create';
  }
  if (method === 'PUT') {
    return records.find((record) => path === `${list}/${record.globalRecordId}`) ?? null;
  }
  return null;
}

/** Stores a saved write: a created record joins the set, an updated one replaces the record it addressed. */
function storeSaved(records: RecordDto[], target: 'create' | RecordDto, saved: RecordDto): void {
  if (target === 'create') {
    records.push(saved);
  } else {
    records[records.indexOf(target)] = saved;
  }
}

/**
 * A case's own answer to a request, tried before every other route: a response, a
 * rejection (an aborted or failed fetch), or `null` to fall through to the other routes.
 */
type Route = (call: Call) => Response | Promise<Response> | null;

/** The answer to a field event in a case that moves focus into a field: no effects. */
const NO_EFFECTS: FieldEventResponse = {
  enabled: [],
  disabled: [],
  cleared: [],
  assigned: {},
  dialogs: [],
  diagnostics: [],
};

interface FakeApiOptions {
  /** Answer field events with no effects, for a case that moves focus into a field. */
  readonly answersFieldEvents?: boolean;
  /** The form route's body text as sent, in place of the definition as JSON. */
  readonly definitionText?: string;
  /** The case's own answers, tried first. */
  readonly route?: Route;
}

/**
 * A fetch that serves `definition`'s form and `records`, answers each write with the next
 * queued reply, answers field events with no effects when `answersFieldEvents` is set, and
 * records every request it receives. A case's `route` answers before all of these.
 */
function fakeApi(definition: FormDefinition, records: RecordDto[], options: FakeApiOptions = {}): typeof fetch {
  const { answersFieldEvents = false, definitionText, route } = options;
  const atPath = new RegExp(`^/api/forms/${definition.viewId}/records/at/\\d+$`);
  return async (input, init) => {
    const request = input instanceof Request ? input : new Request(input, init);
    const url = new URL(request.url);
    const text = await request.text();
    const call: Call = { method: request.method, url, body: text === '' ? null : text };
    calls.push(call);

    const own = route === undefined ? null : await route(call);
    if (own !== null) {
      return own;
    }

    const body = readRouteBody(call.method, url.pathname, definition, records);
    if (body === definition && definitionText !== undefined) {
      return new Response(definitionText, { status: 200, headers: { 'Content-Type': 'application/json' } });
    }
    if (body !== null) {
      return jsonResponse(200, body);
    }
    if (call.method === 'GET' && atPath.test(url.pathname)) {
      return problemResponse(POSITION_NOT_FOUND);
    }
    if (answersFieldEvents && call.method === 'POST' && url.pathname === `/api/forms/${definition.viewId}/checkcode/field-event`) {
      return jsonResponse(200, NO_EFFECTS);
    }
    const target = writeTargetOf(call.method, url.pathname, definition, records);
    const answer = target === null ? undefined : writeAnswers.shift();
    if (target === null || answer === undefined) {
      unrouted.push(requestLine(call));
      return jsonResponse(500, UNROUTED_PROBLEM);
    }
    const reply = await answer;
    if (reply.kind === 'problem') {
      return problemResponse(reply.problem);
    }
    storeSaved(records, target, reply.record);
    return jsonResponse(target === 'create' ? 201 : 200, reply.record);
  };
}

/** Whether `call` is a Find search of the form. */
function isSearch(call: Call): boolean {
  return call.method === 'POST' && call.url.pathname.endsWith('/records/search');
}

/** A response the test releases when it chooses, for a request held open. */
function deferred(): { readonly response: Promise<Response>; readonly release: (response: Response) => void } {
  let release: (response: Response) => void = () => {
    throw new Error('released before the request was held');
  };
  const response = new Promise<Response>((resolve) => {
    release = resolve;
  });
  return { response, release };
}

// Rendering

interface RenderedForm {
  readonly container: HTMLElement;
  readonly definition: FormDefinition;
  readonly onExit: Mock<(saved: boolean) => void>;
  readonly onFormNotFound: Mock<() => void>;
}

interface RenderOptions extends FakeApiOptions {
  /** The form's stored active records in position order; by default the one RECORD. */
  readonly records?: readonly RecordDto[];
  /** Answers a GET of `path` before the read routes do, for a case that fails a read; `undefined` leaves it to them. */
  readonly answerRead?: (path: string) => Response | undefined;
  /** Called at the end of each commit of the form view, in its layout phase, after the form view's layout effects. */
  readonly onCommit?: () => void;
}

/** Renders the form view without waiting for anything. */
function mountForm(
  definition: FormDefinition = createFormDefinitionFixture(),
  options: RenderOptions = {},
): RenderedForm & { readonly records: readonly RecordDto[] } {
  // A copy, so the writes of one case never change the records another case stores.
  const records = [...(options.records ?? [RECORD])];
  const onExit = vi.fn<(saved: boolean) => void>();
  const onFormNotFound = vi.fn<() => void>();
  const serve = fakeApi(definition, records, options);
  const { answerRead, onCommit } = options;
  const fetch: typeof globalThis.fetch =
    answerRead === undefined
      ? serve
      : (input, init) => {
          const request = input instanceof Request ? input : new Request(input, init);
          const url = new URL(request.url);
          const answered = request.method === 'GET' ? answerRead(url.pathname) : undefined;
          if (answered === undefined) {
            return serve(request);
          }
          calls.push({ method: request.method, url, body: null });
          return Promise.resolve(answered);
        };
  // Absolute: openapi-fetch builds a Request, and Node's Request rejects a relative URL.
  const client = createApiClient({ baseUrl: 'http://localhost', fetch });
  const formView = (
    <FormView viewId={definition.viewId} client={client} onExit={onExit} onFormNotFound={onFormNotFound} />
  );
  // A Profiler's onRender runs in the commit's layout phase, after the layout effects of the tree it wraps.
  const { container } = render(
    onCommit === undefined ? (
      formView
    ) : (
      <Profiler id="form-view" onRender={() => onCommit()}>
        {formView}
      </Profiler>
    ),
  );
  return { container, definition, onExit, onFormNotFound, records };
}

/**
 * Renders the form view and waits until the definition, the first record (New on an
 * empty form) and the grid have loaded.
 */
async function renderForm(
  definition: FormDefinition = createFormDefinitionFixture(),
  options: RenderOptions = {},
): Promise<RenderedForm> {
  const { records, ...rendered } = mountForm(definition, options);
  const count = records.length;
  await screen.findByRole('heading', { name: definition.name });
  await within(screen.getByRole('toolbar', { name: 'Record navigation' })).findByText(
    count === 0 ? 'New record' : `1 of ${count}`,
  );
  // An empty list's status is its empty text, not a count.
  await within(screen.getByRole('region', { name: 'Records' })).findByText(
    count === 0 ? 'No records found.' : count === 1 ? '1 record' : `${count} records`,
  );
  return rendered;
}

// DOM helpers

const NATIVE_CONTROLS = 'input, textarea, select';

/** Every element that renders the field `name`, matched on the attribute value, so no name needs escaping. */
function wrappersOf(container: HTMLElement, name: string): HTMLElement[] {
  return Array.from(container.querySelectorAll<HTMLElement>('[data-field-name]')).filter(
    (element) => element.dataset.fieldName === name,
  );
}

function wrapperOf(container: HTMLElement, name: string): HTMLElement {
  const wrappers = wrappersOf(container, name);
  expect(wrappers, `elements rendering ${name}`).toHaveLength(1);
  return wrappers[0];
}

/** Field names rendered inside `element`, in document order. */
function renderedFieldNames(element: HTMLElement): (string | undefined)[] {
  return Array.from(element.querySelectorAll<HTMLElement>('[data-field-name]')).map(
    (rendered) => rendered.dataset.fieldName,
  );
}

function present<T>(value: T | null | undefined, what: string): T {
  if (value === null || value === undefined) {
    throw new Error(`${what} is missing`);
  }
  return value;
}

/** The number `record` holds for `name`, as the form and the grid show it. */
function recordNumberText(record: RecordDto, name: string): string {
  const value = record.values[name];
  if (typeof value !== 'number') {
    throw new Error(`${name} holds no number in the record`);
  }
  return String(value);
}

/** The prompt of a fixture field that has one; every fixture field does. */
function promptOf(field: FieldDefinition): string {
  return present(field.prompt, `prompt of ${field.name}`);
}

function pageOf(definition: FormDefinition, pageId: number): PageDefinition {
  return present(
    definition.pages.find((page) => page.pageId === pageId),
    `page ${pageId}`,
  );
}

function byTabIndex(a: FieldDefinition, b: FieldDefinition): number {
  return a.tabIndex - b.tabIndex;
}

/**
 * A page's panel, through its tab's `aria-controls`: an inactive panel is hidden, and a
 * hidden element has no accessible name to find it by.
 */
function panelOf(page: PageDefinition): HTMLElement {
  const tab = screen.getByRole('tab', { name: page.name });
  const panelId = present(tab.getAttribute('aria-controls'), `aria-controls of tab ${page.name}`);
  const panel = present(document.getElementById(panelId), `panel of ${page.name}`);
  expect(panel.getAttribute('role')).toBe('tabpanel');
  expect(panel.getAttribute('aria-labelledby')).toBe(tab.id);
  return panel;
}

function showPage(page: PageDefinition): void {
  fireEvent.click(screen.getByRole('tab', { name: page.name }));
}

function isDisabled(element: Element): boolean {
  return element.matches(':disabled');
}

function controlValue(element: HTMLElement): string {
  if (
    element instanceof HTMLInputElement ||
    element instanceof HTMLTextAreaElement ||
    element instanceof HTMLSelectElement
  ) {
    return element.value;
  }
  throw new Error(`${element.tagName} is not a native control`);
}

/** A wire `YYYY-MM-DD` date as the user's locale writes it: the text a disabled date control with no date mask shows. */
function localeDate(iso: string): string {
  const [year, month, day] = iso.split('-').map(Number);
  return new Intl.DateTimeFormat(undefined, {
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    timeZone: 'UTC',
    calendar: 'gregory',
  }).format(Date.UTC(year, month - 1, day));
}

/** A wire `YYYY-MM-DD` date as the fixture's date mask `MM-DD-YYYY` writes it, `''` for none: the text a date control shows. */
function inDateMask(iso: FieldValue | undefined): string {
  if (typeof iso !== 'string') {
    return '';
  }
  const [year, month, day] = iso.split('-');
  return `${month}-${day}-${year}`;
}

// Write-flow helpers

/** Lets every pending promise callback and zero-delay timer run. */
async function flush(): Promise<void> {
  await act(async () => {
    await new Promise<void>((resolve) => {
      setTimeout(resolve, 0);
    });
  });
}

/** Every request other than a GET, in arrival order. */
function writeCalls(): Call[] {
  return calls.filter((call) => call.method !== 'GET');
}

function isPlainObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/** The `values` a PUT or POST sent; throws unless its body is `{ values: { … } }`. */
function writtenValues(call: Call): Record<string, unknown> {
  const parsed: unknown = JSON.parse(present(call.body, `body of ${requestLine(call)}`));
  if (!isPlainObject(parsed) || !isPlainObject(parsed.values)) {
    throw new Error(`${requestLine(call)} sent no values object`);
  }
  return parsed.values;
}

/** The control of a fixture field, found by its prompt as a user finds it. */
function controlOf(definition: FormDefinition, name: string): HTMLElement {
  return screen.getByLabelText(promptOf(fieldByName(name, definition)));
}

/** A control's accessible description: the text of the elements its `aria-describedby` names. */
function descriptionOf(control: HTMLElement): string {
  const ids = (control.getAttribute('aria-describedby') ?? '').split(/\s+/).filter((id) => id !== '');
  return ids.map((id) => document.getElementById(id)?.textContent ?? '').join(' ');
}

/**
 * Asserts the format hint of a date control whose field shows no error: `hint` is the text
 * of the `small` right after the control, which alone describes it; `null` is no hint and no
 * description.
 */
function expectDateHint(control: HTMLElement, hint: string | null): void {
  const hintId = `${control.id}-hint`;
  const element = document.getElementById(hintId);
  if (hint === null) {
    expect(element, `hint of ${control.id}`).toBeNull();
    expect(control.parentElement?.querySelector('small') ?? null, `hint of ${control.id}`).toBeNull();
    expect(control.getAttribute('aria-describedby'), `description of ${control.id}`).toBeNull();
    return;
  }
  const shownHint = present(element, `hint of ${control.id}`);
  expect(shownHint.tagName, `hint of ${control.id}`).toBe('SMALL');
  expect(shownHint.textContent, `hint of ${control.id}`).toBe(hint);
  expect(control.nextElementSibling, `hint of ${control.id}`).toBe(shownHint);
  expect(control.getAttribute('aria-describedby'), `description of ${control.id}`).toBe(hintId);
}

/** Changes a field's value as typing does; no focus moves, so no field event is raised. */
function edit(definition: FormDefinition, name: string, value: string): void {
  fireEvent.change(controlOf(definition, name), { target: { value } });
}

function formActionButton(name: string): HTMLElement {
  return within(screen.getByRole('toolbar', { name: 'Form actions' })).getByRole('button', { name });
}

function navigatorButton(name: string): HTMLElement {
  return within(screen.getByRole('toolbar', { name: 'Record navigation' })).getByRole('button', { name });
}

/** The navigator's position text: "[n] of N", or "New record". */
function navigatorStatus(): string {
  return within(screen.getByRole('toolbar', { name: 'Record navigation' })).getByRole('status').textContent ?? '';
}

/** Waits for an alert whose text contains `text`, ignoring case. */
function findAlertContaining(text: string): Promise<HTMLElement> {
  const wanted = text.toLowerCase();
  return waitFor(() =>
    present(
      screen.queryAllByRole('alert').find((alert) => (alert.textContent ?? '').toLowerCase().includes(wanted)),
      `an alert containing "${text}"`,
    ),
  );
}

/** Waits for the validation summary: the alert headed "Please correct the following". */
async function findValidationSummary(): Promise<HTMLElement> {
  const heading = await screen.findByRole('heading', { name: 'Please correct the following' });
  return present(heading.closest<HTMLElement>('[role="alert"]'), 'the alert holding the validation summary');
}

/** The summary's items listed under `prompt`: each starts with the prompt and goes on with a message. */
function summaryItemsFor(summary: HTMLElement, prompt: string): HTMLElement[] {
  return within(summary)
    .getAllByRole('listitem')
    .filter((item) => (item.textContent ?? '').startsWith(prompt));
}

/** Field types rendered as one native control; an option field is a radio group instead. */
type SingleControlFieldType = Exclude<FieldType, 'label' | 'group' | 'unsupported' | 'option'>;

const expectedControls: Readonly<Record<SingleControlFieldType, { readonly tagName: string; readonly type?: string }>> = {
  text: { tagName: 'INPUT', type: 'text' },
  multiline: { tagName: 'TEXTAREA' },
  number: { tagName: 'INPUT' },
  // Every fixture date field has the date mask MM-DD-YYYY, its entry format, so it is a text input.
  date: { tagName: 'INPUT', type: 'text' },
  checkbox: { tagName: 'INPUT', type: 'checkbox' },
  legalValues: { tagName: 'SELECT' },
};

describe('FormView accessible structure (SC-8)', () => {
  /** The control labelled by `field`'s prompt, as a user finds it, narrowed to the element `kind`. */
  function controlLabelledBy<T extends HTMLElement>(field: FieldDefinition, kind: new () => T): T {
    const control = screen.getByLabelText(promptOf(field));
    if (!(control instanceof kind)) {
      throw new Error(`the control of ${field.name} is a ${control.tagName}`);
    }
    return control;
  }

  /**
   * A fixture copy listing its pages, and each page's fields, in reverse, with every
   * `position` and `tabIndex` unchanged. The fixture is already in `position` and `tabIndex`
   * order, so only this copy shows that the order rendered is the renderer's own.
   */
  function reversedDefinition(): FormDefinition {
    const definition = createFormDefinitionFixture();
    return {
      ...definition,
      pages: [...definition.pages].reverse().map((page) => ({ ...page, fields: [...page.fields].reverse() })),
    };
  }

  it('renders one tab per page in position order, and the selected tab shows its panel', async () => {
    const { definition } = await renderForm(reversedDefinition());
    const pages = [...definition.pages].sort((a, b) => a.position - b.position);
    expect(definition.pages.map((page) => page.name), 'pages as listed').not.toEqual(pages.map((page) => page.name));
    const [firstPage, secondPage] = pages;
    const tabs = screen.getAllByRole('tab');

    expect(tabs).toHaveLength(pages.length);
    pages.forEach((page, index) => {
      expect(screen.getByRole('tab', { name: page.name })).toBe(tabs[index]);
    });
    expect(tabs.map((tab) => tab.getAttribute('aria-selected'))).toEqual(['true', 'false']);
    // Only the selected panel is visible, and it is named by its tab.
    expect(screen.getByRole('tabpanel')).toBe(panelOf(firstPage));
    expect(screen.getByRole('tabpanel', { name: firstPage.name })).toBe(panelOf(firstPage));
    expect(panelOf(secondPage).hidden).toBe(true);
    const secondPageLabel = present(
      secondPage.fields.find((field) => field.fieldType === 'label'),
      'a label on the second page',
    );
    expect(screen.queryByRole('heading', { name: promptOf(secondPageLabel) })).toBeNull();

    showPage(secondPage);

    expect(tabs.map((tab) => tab.getAttribute('aria-selected'))).toEqual(['false', 'true']);
    expect(screen.getByRole('tabpanel')).toBe(panelOf(secondPage));
    expect(panelOf(firstPage).hidden).toBe(true);
    expect(screen.getByRole('heading', { name: promptOf(secondPageLabel) })).toBeTruthy();
  });

  it("renders each page's fields inside its own panel in tabIndex order", async () => {
    const { definition } = await renderForm(reversedDefinition());

    for (const page of definition.pages) {
      const expectedOrder = [...page.fields].sort(byTabIndex).map((field) => field.name);
      expect(
        page.fields.map((field) => field.name),
        `fields of ${page.name} as listed`,
      ).not.toEqual(expectedOrder);
      // The fixture's group members directly follow the group in tabIndex, so the group's
      // nesting keeps document order equal to tabIndex order; Black's tabIndex is fractional.
      expect(renderedFieldNames(panelOf(page)), page.name).toEqual(expectedOrder);
    }
  });

  it('renders every data field as one native control labelled by its prompt', async () => {
    const { container, definition } = await renderForm();

    for (const name of dataFieldNames) {
      const field = fieldByName(name, definition);
      const wrapper = wrapperOf(container, name);
      const fieldType = field.fieldType;

      if (fieldType === 'option') {
        const fieldset = present(wrapper.querySelector('fieldset'), `fieldset of ${name}`);
        expect(fieldset.id).toBe(fieldControlId(name));
        expect(fieldset.querySelector('legend')?.textContent).toBe(promptOf(field));
        expect(screen.getByRole('radiogroup', { name: promptOf(field) })).toBe(fieldset);
        // Neither required nor in error: the group carries no state and describes nothing.
        expect(fieldset.hasAttribute('aria-required')).toBe(false);
        expect(fieldset.hasAttribute('aria-invalid')).toBe(false);
        expect(fieldset.hasAttribute('aria-describedby')).toBe(false);

        // The group's only controls are its radios, one per option, each labelled by the option.
        const options = present(field.options, `options of ${name}`);
        const radios = Array.from(fieldset.querySelectorAll<HTMLInputElement>('input[type="radio"]'));
        const controls = Array.from(wrapper.querySelectorAll(NATIVE_CONTROLS));
        expect(controls.map((control) => control.id)).toEqual(radios.map((radio) => radio.id));
        expect(radios.map((radio) => Array.from(radio.labels ?? [], (label) => label.textContent))).toEqual(
          options.map((option) => [option.label]),
        );
        expect(options.map((option) => screen.getByRole('radio', { name: option.label }).id)).toEqual(
          radios.map((radio) => radio.id),
        );
        continue;
      }
      if (fieldType === 'label' || fieldType === 'group' || fieldType === 'unsupported') {
        throw new Error(`${name} is listed as a data field but is a ${fieldType}`);
      }

      const controls = wrapper.querySelectorAll(NATIVE_CONTROLS);
      expect(controls, `controls of ${name}`).toHaveLength(1);
      const control = controls[0];
      const expected = expectedControls[fieldType];
      expect(control.tagName, `control of ${name}`).toBe(expected.tagName);
      if (expected.type !== undefined) {
        expect(control.getAttribute('type'), `type of ${name}`).toBe(expected.type);
      }
      expect(control.id).toBe(fieldControlId(name));

      const labels = Array.from(container.querySelectorAll('label')).filter((label) => label.htmlFor === control.id);
      expect(
        labels.map((label) => label.textContent),
        `label of ${name}`,
      ).toEqual([field.prompt ?? field.name]);
    }

    // Two visible fields, found by their prompts as a user finds them, showing the stored values.
    expect(controlValue(screen.getByLabelText('Last Name'))).toBe('Lovelace');
    expect(controlValue(screen.getByLabelText('Sex'))).toBe('F-Female');

    // A text field's metadata maxLength is the input's native length limit.
    const zip = fieldByName('Zip', definition);
    expect(controlLabelledBy(zip, HTMLInputElement).maxLength, `maxLength of ${zip.name}`).toBe(
      present(zip.validation.maxLength, `metadata maxLength of ${zip.name}`),
    );

    // A pattern is a format hint that describes the input. The record has no errors, so the
    // description is the hint alone.
    const phone = fieldByName('PhoneNumber', definition);
    const describedBy = present(
      controlLabelledBy(phone, HTMLInputElement).getAttribute('aria-describedby'),
      `aria-describedby of ${phone.name}`,
    );
    expect(
      describedBy
        .split(/\s+/)
        .filter((id) => id !== '')
        .map((id) => present(document.getElementById(id), `description ${id} of ${phone.name}`).textContent),
      `description of ${phone.name}`,
    ).toEqual([`Format: ${present(phone.validation.pattern, `pattern of ${phone.name}`)}`]);

    // Legal values follow the empty "No selection" choice in the order the server sends them. The
    // fixture's order is not alphabetical, so a renderer that sorted them fails here; the stored
    // F-Female is listed, so no extra option for an unlisted stored value appears.
    const sex = fieldByName('Sex', definition);
    const sexOptions = present(sex.options, `options of ${sex.name}`);
    const sexLabels = sexOptions.map((option) => option.label);
    expect(sexLabels, `fixture options of ${sex.name}`).not.toEqual([...sexLabels].sort());
    expect(
      Array.from(controlLabelledBy(sex, HTMLSelectElement).options, (option) => [option.value, option.text]),
      `options of ${sex.name}`,
    ).toEqual([['', 'No selection'], ...sexOptions.map((option) => [String(option.value), option.label])]);
  });

  // A metadata maxLength stays the control's native limit, as legacy's MaxLength is, so the
  // browser cuts an entry that passes it, and the cut is told beside the field. jsdom applies
  // no edit for a beforeinput event, so each step sets the value and selection the browser
  // holds, then dispatches the native beforeinput Chromium fires for the entry: a typed
  // character's `data`, a paste's whole clipboard text, `null` for a line break.
  describe('the length-limit notice of a text control', () => {
    interface Entry {
      readonly inputType: string;
      readonly data: string | null;
      /** The selection the entry replaces; by default collapsed at the end of the value. */
      readonly selection?: readonly [number, number];
    }

    function textControlOf(definition: FormDefinition, name: string): HTMLInputElement | HTMLTextAreaElement {
      const control = controlOf(definition, name);
      if (!(control instanceof HTMLInputElement || control instanceof HTMLTextAreaElement)) {
        throw new Error(`the control of ${name} is a ${control.tagName}`);
      }
      return control;
    }

    /** Gives `control` the value `value` as typing does, selects, and dispatches `entry`'s beforeinput. */
    function enter(control: HTMLInputElement | HTMLTextAreaElement, value: string, entry: Entry): void {
      if (control.value !== value) {
        fireEvent.change(control, { target: { value } });
      }
      const [start, end] = entry.selection ?? [value.length, value.length];
      control.setSelectionRange(start, end);
      act(() => {
        control.dispatchEvent(
          new InputEvent('beforeinput', { inputType: entry.inputType, data: entry.data, bubbles: true, cancelable: true }),
        );
      });
    }

    function withValidation(name: string, validation: Partial<FieldDefinition['validation']>): FormDefinition {
      return withField(name, (field) => ({ ...field, validation: { ...field.validation, ...validation } }));
    }

    /** Requests other than reads and the field events focus and blur raise. */
    function recordWrites(): string[] {
      return writeCalls()
        .filter((call) => !call.url.pathname.endsWith('/checkcode/field-event'))
        .map(requestLine);
    }

    const TYPED_AT_LIMIT: Entry = { inputType: 'insertText', data: '9' };

    it.each<{ what: string; name: string; maxLength?: number; value: string; entry: Entry; cutTo: string }>([
      {
        what: 'a sixth character typed into a full Zip',
        name: 'Zip',
        value: '12345',
        entry: { inputType: 'insertText', data: '6' },
        cutTo: '12345',
      },
      {
        what: 'a paste longer than Zip',
        name: 'Zip',
        value: '',
        entry: { inputType: 'insertFromPaste', data: '123456' },
        cutTo: '12345',
      },
      {
        what: 'a paste over a selection that leaves Zip too long',
        name: 'Zip',
        value: '12345',
        entry: { inputType: 'insertFromPaste', data: '678', selection: [1, 3] },
        // The browser keeps as much of the paste as fits in place of the selection.
        cutTo: '16745',
      },
      {
        what: 'a character typed into a full phone number beside its format hint',
        name: 'PhoneNumber',
        maxLength: 12,
        value: '555-014-8636',
        entry: { inputType: 'insertText', data: '0' },
        cutTo: '555-014-8636',
      },
      {
        what: 'a line break typed into a full Address',
        name: 'PatientAddress',
        maxLength: 8,
        value: '12 Elm S',
        entry: { inputType: 'insertLineBreak', data: null },
        cutTo: '12 Elm S',
      },
      {
        what: 'a paste into Address still too long once its CRLF is one line break',
        name: 'PatientAddress',
        maxLength: 8,
        value: '',
        entry: { inputType: 'insertFromPaste', data: '12 Elm\r\nSt.' },
        cutTo: '12 Elm\nS',
      },
    ])(
      'announces $what beside the field as a notice, not an error, until a fitting entry, a deletion or blur',
      async ({ name, maxLength, value, entry, cutTo }) => {
        const definition = maxLength === undefined ? createFormDefinitionFixture() : withValidation(name, { maxLength });
        const field = fieldByName(name, definition);
        const limit = present(field.validation.maxLength, `maxLength of ${name}`);
        expect(cutTo, 'the value the browser keeps').toHaveLength(limit);
        const pattern = field.validation.pattern;
        const hintIds = typeof pattern === 'string' && pattern !== '' ? [`${fieldControlId(name)}-hint`] : [];
        const limitId = `${fieldControlId(name)}-limit`;
        const { container } = await renderForm(definition, { answersFieldEvents: true });
        // Installing the record moves focus without raising an event; the case's own focus comes after it.
        await flush();
        const control = textControlOf(definition, name);
        act(() => {
          control.focus();
        });

        const expectShown = (step: string): void => {
          const notice = within(wrapperOf(container, name)).getByRole('status');
          expect(notice.id, step).toBe(limitId);
          expect(notice.tagName, step).toBe('SMALL');
          expect(notice.textContent, step).toBe(`Enter at most ${String(limit)} characters.`);
          expect(control.getAttribute('aria-describedby'), step).toBe([...hintIds, limitId].join(' '));
          expect(control.hasAttribute('aria-invalid'), step).toBe(false);
          expect(wrapperOf(container, name).querySelector('.field-error'), step).toBeNull();
        };
        const expectHidden = (step: string): void => {
          expect(within(wrapperOf(container, name)).queryByRole('status'), step).toBeNull();
          expect(document.getElementById(limitId), step).toBeNull();
          expect(control.getAttribute('aria-describedby'), step).toBe(hintIds.length > 0 ? hintIds.join(' ') : null);
        };

        expectHidden('before the entry');
        enter(control, value, entry);
        expectShown('after the entry');
        // The browser then keeps the cut value and fires input, which the draft takes.
        fireEvent.change(control, { target: { value: cutTo } });
        expectShown('once the draft holds the cut value');
        expect(controlValue(control)).toBe(cutTo);

        enter(control, cutTo, { inputType: 'insertText', data: '1', selection: [limit - 1, limit] });
        expectHidden('after a character typed over the last one');

        enter(control, cutTo, TYPED_AT_LIMIT);
        expectShown('after a character typed at the limit');
        enter(control, cutTo, { inputType: 'deleteContentBackward', data: null });
        expectHidden('after a deletion');

        enter(control, cutTo, TYPED_AT_LIMIT);
        expectShown('after another character typed at the limit');
        act(() => {
          control.blur();
        });
        expectHidden('after blur');

        // The blur raised its one FieldAfter, and no step wrote the record.
        await waitFor(() => {
          expect(calls.filter((call) => call.body?.includes('"event":"FieldAfter"') === true)).toHaveLength(1);
        });
        await flush();
        expect(calls.filter((call) => call.body?.includes('"event":"FieldAfter"') === true)).toHaveLength(1);
        expect(recordWrites()).toEqual([]);
      },
    );

    it.each<{ what: string; name: string; validation?: Partial<FieldDefinition['validation']>; value: string; entry: Entry }>([
      {
        what: 'a paste that fills Zip once its trailing CRLF is dropped',
        name: 'Zip',
        value: '',
        entry: { inputType: 'insertFromPaste', data: '12345\r\n' },
      },
      {
        what: 'a paste that fills Address once its CRLF is one line break',
        name: 'PatientAddress',
        validation: { maxLength: 5 },
        value: '',
        entry: { inputType: 'insertFromPaste', data: 'ab\r\ncd' },
      },
      {
        what: 'a long paste into a field with no maxLength',
        name: 'LastName',
        value: 'Lovelace',
        entry: { inputType: 'insertFromPaste', data: 'x'.repeat(100) },
      },
      {
        what: 'a paste longer than a read-only, so disabled, Zip',
        name: 'Zip',
        validation: { readOnly: true },
        value: '',
        entry: { inputType: 'insertFromPaste', data: '123456' },
      },
    ])('shows no length-limit notice for $what', async ({ name, validation, value, entry }) => {
      const definition = validation === undefined ? createFormDefinitionFixture() : withValidation(name, validation);
      const { container } = await renderForm(definition);
      const control = textControlOf(definition, name);

      enter(control, value, entry);

      expect(within(wrapperOf(container, name)).queryByRole('status')).toBeNull();
      expect(document.getElementById(`${fieldControlId(name)}-limit`)).toBeNull();
      expect(control.hasAttribute('aria-describedby')).toBe(false);
      await flush();
      expect(writeCalls().map(requestLine)).toEqual([]);
    });
  });

  it('labels a control by the field name when the prompt is null', async () => {
    const definition = withField('PatientAddress', (field) => ({ ...field, prompt: null }));
    const { container } = await renderForm(definition);

    const control = screen.getByLabelText('PatientAddress');
    expect(control.tagName).toBe('TEXTAREA');
    expect(wrapperOf(container, 'PatientAddress').contains(control)).toBe(true);
  });

  it('exposes a required option field as a required radiogroup, invalid and described by its errors once Save reports them', async () => {
    const definition = withField('IllnessSeverity', (field) => ({
      ...field,
      validation: { ...field.validation, required: true },
    }));
    const severity = fieldByName('IllnessSeverity', definition);
    const prompt = promptOf(severity);
    const message = serverRequiredMessage(prompt, pageOf(definition, severity.pageId).name);
    const recordsPath = `/api/forms/${definition.viewId}/records`;
    // The one write this case makes: a POST the server's required gate rejects. The client
    // leaves option fields to that gate, as legacy data entry never required them.
    replyToWrite({ kind: 'problem', problem: fieldLevelProblem({ [severity.name]: [message] }) });
    await renderForm(definition);
    const group = screen.getByRole('radiogroup', { name: prompt });

    // The stored record holds an option, so the group is required but not invalid.
    expect(group.getAttribute('aria-required')).toBe('true');
    expect(group.hasAttribute('aria-invalid')).toBe(false);
    expect(group.hasAttribute('aria-describedby')).toBe(false);

    // A new record with another field entered and no option chosen: the server rejects the
    // save, and its message shows beside the group.
    const navigator = screen.getByRole('toolbar', { name: 'Record navigation' });
    fireEvent.click(within(navigator).getByRole('button', { name: 'New' }));
    await within(navigator).findByText('New record');
    fireEvent.change(screen.getByLabelText('Last Name'), { target: { value: 'Byron' } });
    fireEvent.click(within(screen.getByRole('toolbar', { name: 'Form actions' })).getByRole('button', { name: 'Save' }));

    await waitFor(() => {
      expect(screen.getByRole('radiogroup', { name: prompt }).getAttribute('aria-invalid')).toBe('true');
    });
    expect(calls.filter((call) => call.method !== 'GET').map(requestLine)).toEqual([`POST ${recordsPath}`]);
    const invalid = screen.getByRole('radiogroup', { name: prompt });
    const errorId = fieldErrorId(severity.name);
    expect(invalid.getAttribute('aria-required')).toBe('true');
    expect(invalid.getAttribute('aria-describedby')).toBe(errorId);
    const errorList = present(document.getElementById(errorId), `error list of ${severity.name}`);
    expect(errorList.textContent).toBe(message);
    // The radios support neither state, so only the group carries it.
    for (const radio of within(invalid).getAllByRole('radio')) {
      expect(radio.hasAttribute('aria-required'), radio.id).toBe(false);
      expect(radio.hasAttribute('aria-invalid'), radio.id).toBe(false);
    }
  });

  it('names the empty legal-values option "No selection", lists the server\'s options after it, and saves it as null', async () => {
    const definition = createFormDefinitionFixture();
    // The one write this case makes: the PUT of the open record.
    replyToWrite({ kind: 'saved', record: savedWith(RECORD, { Sex: null }) });
    await renderForm(definition);
    const sex = fieldByName('Sex', definition);
    const select = screen.getByRole('combobox', { name: promptOf(sex) });
    const noSelection = within(select).getByRole('option', { name: 'No selection' });
    const options = within(select).getAllByRole('option');

    // "No selection" comes first and carries '' for null; the server's options follow in its order.
    expect(options[0]).toBe(noSelection);
    expect(noSelection.getAttribute('value')).toBe('');
    expect(
      options.slice(1).map((option) => ({ value: option.getAttribute('value'), label: option.textContent })),
    ).toEqual(present(sex.options, 'options of Sex'));
    expect(controlValue(select)).toBe('F-Female');

    fireEvent.change(select, { target: { value: '' } });

    expect(controlValue(select)).toBe('');
    expect(within(select).getByRole('option', { selected: true })).toBe(noSelection);

    fireEvent.click(within(screen.getByRole('toolbar', { name: 'Form actions' })).getByRole('button', { name: 'Save' }));

    await waitFor(() => {
      expect(calls.filter((call) => call.method === 'PUT').map((call) => call.body)).toEqual([
        JSON.stringify({ values: { Sex: null } }),
      ]);
    });
    await within(screen.getByRole('toolbar', { name: 'Record navigation' })).findByText('1 of 1');
    expect(within(select).getByRole('option', { selected: true })).toBe(noSelection);
  });

  it('renders label fields as headings without a control', async () => {
    const { container, definition } = await renderForm();
    const labels = allFields(definition).filter((field) => field.fieldType === 'label');
    expect(new Set(labels.map((field) => field.pageId)).size).toBe(definition.pages.length);

    for (const label of labels) {
      // Inactive panels are hidden, and role queries skip hidden content.
      showPage(pageOf(definition, label.pageId));
      const wrapper = wrapperOf(container, label.name);
      const heading = screen.getByRole('heading', { name: promptOf(label) });

      expect(wrapper.contains(heading)).toBe(true);
      expect(wrapper.querySelector(NATIVE_CONTROLS)).toBeNull();
    }
  });

  it('renders a group as a section headed by its prompt, holding its same-page members in tabIndex order', async () => {
    const { container, definition } = await renderForm();
    const group = present(
      allFields(definition).find((field) => field.fieldType === 'group'),
      'a group field',
    );
    const members = present(group.members, `members of ${group.name}`).map((name) => fieldByName(name, definition));
    const samePage = members.filter((member) => member.pageId === group.pageId);
    const otherPage = members.filter((member) => member.pageId !== group.pageId);
    const expectedOrder = [...samePage].sort(byTabIndex).map((member) => member.name);
    // The fixture lists the members out of tabIndex order, so the order below is the renderer's.
    expect(samePage.map((member) => member.name)).not.toEqual(expectedOrder);
    expect(otherPage).not.toHaveLength(0);

    const section = wrapperOf(container, group.name);
    expect(section.tagName).toBe('SECTION');
    expect(screen.getByRole('region', { name: promptOf(group) })).toBe(section);
    const heading = within(section).getByRole('heading', { name: promptOf(group) });
    expect(renderedFieldNames(section)).toEqual(expectedOrder);
    for (const name of expectedOrder) {
      const member = wrapperOf(container, name);
      expect(heading.compareDocumentPosition(member) & Node.DOCUMENT_POSITION_FOLLOWING, name).not.toBe(0);
    }

    // The section sits at the group's own tabIndex among the page's items.
    const page = pageOf(definition, group.pageId);
    const before = [...page.fields].sort(byTabIndex).filter((field) => field.tabIndex < group.tabIndex);
    for (const field of before) {
      const earlier = wrapperOf(container, field.name);
      expect(section.compareDocumentPosition(earlier) & Node.DOCUMENT_POSITION_PRECEDING, field.name).not.toBe(0);
    }

    // A member on another page renders there, outside the section.
    for (const member of otherPage) {
      const rendered = wrapperOf(container, member.name);
      expect(section.contains(rendered)).toBe(false);
      expect(panelOf(pageOf(definition, member.pageId)).contains(rendered)).toBe(true);
    }
  });

  it('renders every field exactly once, and every data field control once', async () => {
    const { container, definition } = await renderForm();
    const fields = allFields(definition);

    for (const field of fields) {
      expect(wrappersOf(container, field.name), field.name).toHaveLength(1);
    }
    expect(container.querySelectorAll('[data-field-name]')).toHaveLength(fields.length);
    for (const name of dataFieldNames) {
      expect(document.querySelectorAll(`[id="${fieldControlId(name)}"]`), name).toHaveLength(1);
    }
  });

  it('keeps every id unique when field names differ only in characters an id cannot hold, and when two groups share a name', async () => {
    // Each fixture field is renamed so that an id kept DOM-safe by folding characters, or by
    // keeping `-`, would merge it with another field's control id or with a derived id: the
    // `-hint` of `Q-1` (it keeps PhoneNumber's pattern), the first radio of IllnessSeverity,
    // or the `-error` list of `Q-1`. Every field renamed holds no value in RECORD.
    const renames: readonly { readonly from: string; readonly to: string }[] = [
      { from: 'PhoneNumber', to: 'Q-1' },
      { from: 'Zip', to: 'Q-1-hint' },
      { from: 'DateofInterview', to: 'Q.1' },
      { from: 'PatientAddress', to: 'Q 1' },
      { from: 'FeverTemp', to: 'Q_1' },
      { from: 'OnsetDate', to: 'IllnessSeverity-option-0' },
      { from: 'BuyLastTenDays', to: 'Q-1-error' },
    ];
    const definition = createFormDefinitionFixture();
    for (const page of definition.pages) {
      page.fields = page.fields.map((field) => {
        const rename = renames.find(({ from }) => from === field.name);
        return rename === undefined ? field : { ...field, name: rename.to };
      });
    }
    // A second group with the first group's name, on the other page, holding Lab.
    const [firstPage, secondPage] = [...definition.pages].sort((a, b) => a.position - b.position);
    const group = fieldByName('DemographicInformation', definition);
    const secondGroup: FieldDefinition = {
      ...group,
      prompt: 'Demographic Information, continued',
      tabIndex: 3.5,
      pageId: secondPage.pageId,
      members: ['Lab'],
    };
    secondPage.fields.push(secondGroup);

    const { container } = await renderForm(definition);

    const ids = Array.from(document.querySelectorAll('[id]'), (element) => element.id);
    expect(ids.filter((id, index) => ids.indexOf(id) !== index)).toEqual([]);

    // Each renamed field's prompt labels its own control, which carries that field's id.
    for (const { to } of renames) {
      const control = screen.getByLabelText(promptOf(fieldByName(to, definition)));
      expect(wrapperOf(container, to).contains(control), to).toBe(true);
      expect(control.id, to).toBe(fieldControlId(to));
    }
    // Every label of every data field names a control inside that field.
    for (const field of allFields(definition).filter(isDataField)) {
      const wrapper = wrapperOf(container, field.name);
      const labels = Array.from(wrapper.querySelectorAll('label'));
      expect(labels, `labels of ${field.name}`).not.toHaveLength(0);
      for (const label of labels) {
        const target = document.getElementById(label.htmlFor);
        expect(target !== null && wrapper.contains(target), `${field.name}: ${label.textContent ?? ''}`).toBe(true);
      }
    }

    // Each group's section is named by its own heading.
    const sections = wrappersOf(container, group.name);
    expect(sections).toHaveLength(2);
    expect(screen.getByRole('region', { name: promptOf(group) })).toBe(sections[0]);
    expect(panelOf(firstPage).contains(sections[0])).toBe(true);
    showPage(secondPage);
    expect(screen.getByRole('region', { name: promptOf(secondGroup) })).toBe(sections[1]);
    expect(within(sections[1]).getByLabelText(promptOf(fieldByName('Lab', definition)))).toBeTruthy();

    // Distinct names give distinct control ids, none equals a derived id, and every id is
    // usable unescaped in a CSS selector. The name `Q_002d1` spells the encoding of `Q-1`, so
    // it keeps its own id only because `_` is encoded as well.
    const names = [...allFields(definition).map((field) => field.name), 'Q_002d1'];
    const controlIds = names.map(fieldControlId);
    expect(new Set(controlIds).size).toBe(new Set(names).size);
    const derivedIds = names.flatMap((name) => [fieldErrorId(name), `${fieldControlId(name)}-hint`]);
    expect(derivedIds.filter((id) => controlIds.includes(id))).toEqual([]);
    for (const id of controlIds) {
      expect(id).toMatch(/^field-[A-Za-z0-9_]+$/);
    }
  });

  it("lists a save error at the data field that owns its name, and its jump shows that field's page, when a group on an earlier page shares the name", async () => {
    // Metadata may give a label, group or placeholder a data field's exact name, but the
    // `values` and `errors` key belongs to the data field: here a group `Lab` on page 1,
    // ahead of CaseID, and the text field `Lab` on page 2.
    const definition = createFormDefinitionFixture();
    const [firstPage, secondPage] = [...definition.pages].sort((a, b) => a.position - b.position);
    const lab = fieldByName('Lab', definition);
    const caseId = fieldByName('CaseID', definition);
    const labGroup: FieldDefinition = {
      ...fieldByName('DemographicInformation', definition),
      name: lab.name,
      prompt: 'Laboratory testing',
      tabIndex: 1.5,
      members: [],
    };
    firstPage.fields.push(labGroup);
    expect(lab.pageId).toBe(secondPage.pageId);
    expect(caseId.pageId).toBe(firstPage.pageId);
    expect(labGroup.tabIndex).toBeLessThan(caseId.tabIndex);

    const labMessage = 'Name the laboratory that tested the specimen.';
    const caseIdMessage = 'Case ID must be a whole number.';
    const recordPath = `/api/forms/${definition.viewId}/records/${RECORD.globalRecordId}`;
    const fieldEventPath = `/api/forms/${definition.viewId}/checkcode/field-event`;
    // This case's writes: the PUT of the open record, which the server rejects with a message
    // for each name, and the field event that focusing a control raises.
    replyToWrite({ kind: 'problem', problem: fieldLevelProblem({ [lab.name]: [labMessage], [caseId.name]: [caseIdMessage] }) });
    await renderForm(definition, { answersFieldEvents: true });
    expect(screen.getByRole('region', { name: promptOf(labGroup) })).toBeTruthy();

    fireEvent.change(screen.getByLabelText('Last Name'), { target: { value: 'Byron' } });
    fireEvent.click(within(screen.getByRole('toolbar', { name: 'Form actions' })).getByRole('button', { name: 'Save' }));

    await screen.findByRole('heading', { name: 'Please correct the following' });
    expect(calls.filter((call) => call.method !== 'GET').map(requestLine)).toEqual([`PUT ${recordPath}`]);
    const summary = present(
      screen
        .getAllByRole('alert')
        .find((alert) => within(alert).queryByRole('heading', { name: 'Please correct the following' }) !== null),
      'validation summary',
    );
    // Lab's message follows CaseID's, at the data field's place on page 2, under its prompt
    // ("Lab:" already ends with a colon); the group's prompt labels nothing.
    expect(within(summary).getAllByRole('listitem').map((item) => item.textContent)).toEqual([
      `${promptOf(caseId)}: ${caseIdMessage}`,
      `${promptOf(lab)} ${labMessage}`,
    ]);
    expect(within(summary).queryByRole('button', { name: promptOf(labGroup) })).toBeNull();

    fireEvent.click(within(summary).getByRole('button', { name: promptOf(lab) }));

    await waitFor(() => {
      expect(document.activeElement?.id).toBe(fieldControlId(lab.name));
    });
    expect(screen.getByRole('tab', { name: secondPage.name }).getAttribute('aria-selected')).toBe('true');
    expect(screen.getByRole('tab', { name: firstPage.name }).getAttribute('aria-selected')).toBe('false');
    const panel = panelOf(secondPage);
    expect(panel.hidden).toBe(false);
    expect(panel.contains(document.activeElement)).toBe(true);
    // A role query skips hidden content, so this also shows the focused control is on screen.
    expect(screen.getByRole('textbox', { name: promptOf(lab) })).toBe(document.activeElement);
    // Focusing the control raises its FieldBefore, answered with no effects.
    await waitFor(() => {
      expect(calls.filter((call) => call.method !== 'GET').map(requestLine)).toEqual([
        `PUT ${recordPath}`,
        `POST ${fieldEventPath}`,
      ]);
    });
  });

  it('disables the controls of read-only fields and leaves writable ones enabled', async () => {
    const { container } = await renderForm();
    expect(readOnlyFieldNames).not.toHaveLength(0);

    for (const name of dataFieldNames) {
      const controls = Array.from(wrapperOf(container, name).querySelectorAll(NATIVE_CONTROLS));
      const readOnly = readOnlyFieldNames.includes(name);
      expect(
        controls.map((control) => isDisabled(control)),
        name,
      ).toEqual(controls.map(() => readOnly));
    }
  });

  // Chromium's accessibility snapshots drop a disabled date input and announce its
  // spinbuttons without the field's label, so a protected date is a read-only text box.
  // Its text follows the field's date mask, which holds each of YYYY, MM and DD once in
  // any case; any other pattern, or none, shows the date in the user's locale. A date
  // mask is also stated in a "Format:" hint that describes the box, as a patterned
  // text field's is; a field with no date mask has none.
  it.each<{ name: string; stored: string | null; pattern: string | null; shown: string; hint: string | null }>([
    { name: 'DOB', stored: '1980-05-01', pattern: 'MM-DD-YYYY', shown: '05-01-1980', hint: 'Format: MM-DD-YYYY' },
    { name: 'DOB', stored: '1980-05-01', pattern: 'DD-MM-YYYY', shown: '01-05-1980', hint: 'Format: DD-MM-YYYY' },
    { name: 'DOB', stored: '1980-05-01', pattern: 'yyyy/mm/dd', shown: '1980/05/01', hint: 'Format: yyyy/mm/dd' },
    { name: 'DOB', stored: '1980-05-01', pattern: null, shown: localeDate('1980-05-01'), hint: null },
    { name: 'DOB', stored: '1980-05-01', pattern: 'MM-DD', shown: localeDate('1980-05-01'), hint: null },
    {
      name: 'DOB',
      stored: '1980-05-01',
      pattern: 'MM-DD-YYYY HH:MM:SS AMPM',
      shown: localeDate('1980-05-01'),
      hint: null,
    },
    { name: 'OnsetDate', stored: null, pattern: 'MM-DD-YYYY', shown: '', hint: 'Format: MM-DD-YYYY' },
  ])(
    'renders the read-only date $name, stored $stored with pattern $pattern, as a disabled read-only text box showing $shown, and writable dates as text boxes in their date mask',
    async ({ name, stored, pattern, shown, hint }) => {
      const definition = withField(name, (field) => ({
        ...field,
        validation: { ...field.validation, readOnly: true, pattern },
      }));
      const { container } = await renderForm(definition);
      expect(RECORD.values[name] ?? null).toBe(stored);

      const control = screen.getByRole('textbox', { name: promptOf(fieldByName(name, definition)) });
      expect(control.id).toBe(fieldControlId(name));
      expect(wrapperOf(container, name).contains(control)).toBe(true);
      expect(control.getAttribute('type')).toBe('text');
      expect(isDisabled(control)).toBe(true);
      expect(control.hasAttribute('readonly')).toBe(true);
      expect(control.hasAttribute('min') || control.hasAttribute('max')).toBe(false);
      expect(controlValue(control)).toBe(shown);
      expectDateHint(control, hint);

      const writableDates = allFields(definition).filter(
        (field) => field.fieldType === 'date' && field.name !== name,
      );
      expect(writableDates).not.toHaveLength(0);
      // Each writable date keeps the fixture's date mask, its entry format, its placeholder
      // and its hint.
      for (const field of writableDates) {
        const input = present(wrapperOf(container, field.name).querySelector('input'), `control of ${field.name}`);
        expect(field.validation.pattern, field.name).toBe('MM-DD-YYYY');
        expect(input.getAttribute('type'), field.name).toBe('text');
        expect(input.getAttribute('placeholder'), field.name).toBe('MM-DD-YYYY');
        expect(isDisabled(input), field.name).toBe(false);
        expect(input.hasAttribute('readonly'), field.name).toBe(false);
        expect(controlValue(input), field.name).toBe(inDateMask(RECORD.values[field.name]));
        expectDateHint(input, 'Format: MM-DD-YYYY');
      }
    },
  );

  // A writable date's date mask is its display and entry format, so the field is a text box in
  // that mask, with the mask as its placeholder, a "Format:" hint that describes it, and no
  // picker bounds. A pattern that is no date mask keeps the browser's date input, whose value
  // is the wire date and whose bounds are min and max, with no hint.
  it.each<{
    pattern: string | null;
    type: string;
    shown: string;
    placeholder: string | null;
    hint: string | null;
    bounds: boolean;
  }>([
    {
      pattern: 'MM-DD-YYYY',
      type: 'text',
      shown: '05-01-1980',
      placeholder: 'MM-DD-YYYY',
      hint: 'Format: MM-DD-YYYY',
      bounds: false,
    },
    {
      pattern: 'DD-MM-YYYY',
      type: 'text',
      shown: '01-05-1980',
      placeholder: 'DD-MM-YYYY',
      hint: 'Format: DD-MM-YYYY',
      bounds: false,
    },
    {
      pattern: 'yyyy/mm/dd',
      type: 'text',
      shown: '1980/05/01',
      placeholder: 'yyyy/mm/dd',
      hint: 'Format: yyyy/mm/dd',
      bounds: false,
    },
    { pattern: null, type: 'date', shown: '1980-05-01', placeholder: null, hint: null, bounds: true },
    { pattern: 'MM-DD', type: 'date', shown: '1980-05-01', placeholder: null, hint: null, bounds: true },
    {
      pattern: 'MM-DD-YYYY HH:MM:SS AMPM',
      type: 'date',
      shown: '1980-05-01',
      placeholder: null,
      hint: null,
      bounds: true,
    },
  ])(
    'renders the writable date DOB with pattern $pattern as a $type input showing $shown',
    async ({ pattern, type, shown, placeholder, hint, bounds }) => {
      const lower = '1900-01-01';
      const upper = '2012-12-31';
      const definition = withField('DOB', (field) => ({
        ...field,
        validation: { ...field.validation, pattern, lower, upper },
      }));
      await renderForm(definition);
      expect(RECORD.values.DOB).toBe('1980-05-01');

      const control = controlOf(definition, 'DOB');
      expect(control.id).toBe(fieldControlId('DOB'));
      expect(control.getAttribute('type')).toBe(type);
      expect(controlValue(control)).toBe(shown);
      expect(control.getAttribute('placeholder')).toBe(placeholder);
      expect([control.getAttribute('min'), control.getAttribute('max')]).toEqual(bounds ? [lower, upper] : [null, null]);
      expect(isDisabled(control)).toBe(false);
      expect(control.hasAttribute('readonly')).toBe(false);
      expectDateHint(control, hint);
    },
  );

  it('renders an unsupported field as a disabled placeholder naming its type, with no control', async () => {
    const { container } = await renderForm();
    const wrapper = wrapperOf(container, 'ILL');

    const text = within(wrapper).getByText('Unsupported field type: Yes/No');
    expect(text.closest('[aria-disabled="true"]')).not.toBeNull();
    expect(wrapper.querySelectorAll(`${NATIVE_CONTROLS}, button`)).toHaveLength(0);
    expect(document.getElementById(fieldControlId('ILL'))).toBeNull();
  });

  it('sends only GET requests while loading and switching pages, and raises no field event (DATA-01)', async () => {
    const { definition, onExit, onFormNotFound } = await renderForm();
    const form = `/api/forms/${definition.viewId}`;
    const paths = new Set(calls.map((call) => call.url.pathname));
    for (const path of [form, `${form}/records/count`, `${form}/records/at/1`, `${form}/records`]) {
      expect(paths).toContain(path);
    }

    for (const page of [...definition.pages].reverse()) {
      showPage(page);
    }

    expect(calls.filter((call) => call.method !== 'GET').map(requestLine)).toEqual([]);
    expect(calls.filter((call) => call.url.pathname.endsWith('/checkcode/field-event')).map(requestLine)).toEqual([]);
    expect(calls.filter((call) => call.body !== null).map(requestLine)).toEqual([]);
    expect(onFormNotFound).not.toHaveBeenCalled();
    expect(onExit).not.toHaveBeenCalled();
  });

  it('names its toolbar, navigator and grid controls as the smoke test finds them', async () => {
    await renderForm();
    const formActions = screen.getByRole('toolbar', { name: 'Form actions' });
    const navigator = screen.getByRole('toolbar', { name: 'Record navigation' });
    const records = screen.getByRole('region', { name: 'Records' });

    // String names match the whole accessible name; getByRole also fails on a second match.
    for (const name of ['Back to forms', 'Save', 'Delete', 'Find']) {
      expect(within(formActions).getByRole('button', { name })).toBeTruthy();
    }
    for (const name of ['First', 'Previous', 'Next', 'Last', 'New']) {
      expect(within(navigator).getByRole('button', { name })).toBeTruthy();
    }
    expect(within(navigator).getByRole('textbox', { name: 'Go to record' })).toBeTruthy();
    expect(within(records).getByRole('searchbox', { name: 'Search records' })).toBeTruthy();
    expect(within(records).getByRole('checkbox', { name: 'Show deleted' })).toBeTruthy();
  });
});

describe('FormView field rendering: isolation, row identity, registry lookup and heading depth', () => {
  /**
   * Routes every registry entry through a wrapper that records the name of each field it
   * renders, then renders the real component. Returns the live list of rendered names.
   */
  function recordFieldRenders(): string[] {
    const rendered: string[] = [];
    for (const type of Object.keys(fieldComponents) as FieldType[]) {
      const Real = fieldComponents[type];
      const Counted = (props: FieldComponentProps) => {
        rendered.push(props.field.name);
        return <Real {...props} />;
      };
      vi.spyOn(fieldComponents, type, 'get').mockReturnValue(Counted);
    }
    return rendered;
  }

  // Every panel stays mounted, so an edit re-renders the form view and every page; only
  // the edited control, and the group holding it, may render again.
  it.each([
    { label: 'Zip', value: '30301', rerendered: ['Zip'] },
    { label: 'Lab:', value: 'County lab', rerendered: ['Lab'] },
    { label: 'Last Name', value: 'Byron', rerendered: ['DemographicInformation', 'LastName'] },
  ])('re-renders only the edited field and the group holding it: $label', async ({ label, value, rerendered }) => {
    const rendered = recordFieldRenders();
    await renderForm();
    rendered.length = 0;

    fireEvent.change(screen.getByLabelText(label), { target: { value } });

    expect(controlValue(screen.getByLabelText(label))).toBe(value);
    expect([...new Set(rendered)].sort()).toEqual(rerendered);
  });

  type AddedField = Pick<FieldDefinition, 'name' | 'fieldType' | 'prompt' | 'tabIndex' | 'pageId'> &
    Partial<Pick<FieldDefinition, 'unsupportedType' | 'members'>>;

  /** A complete Field DTO with no validation and no options; `unsupported` follows `fieldType`. */
  function addedField(init: AddedField): FieldDefinition {
    return {
      ...init,
      unsupported: init.fieldType === 'unsupported',
      unsupportedType: init.unsupportedType ?? null,
      validation: { required: false, readOnly: false, maxLength: null, lower: null, upper: null, pattern: null },
      options: null,
      members: init.members ?? null,
    };
  }

  /** Adds each field to its page after the fields with a lower or equal `tabIndex`, as the server orders them. */
  function withAddedFields(definition: FormDefinition, ...fields: FieldDefinition[]): FormDefinition {
    for (const added of fields) {
      const page = pageOf(definition, added.pageId);
      const after = page.fields.findIndex((field) => field.tabIndex > added.tabIndex);
      page.fields.splice(after < 0 ? page.fields.length : after, 0, added);
    }
    return definition;
  }

  function pagesInPositionOrder(definition: FormDefinition): PageDefinition[] {
    return [...definition.pages].sort((a, b) => a.position - b.position);
  }

  /** Names of the items laid out directly in a page panel's field list, in document order. */
  function topLevelFieldNames(panel: HTMLElement): (string | undefined)[] {
    const list = present(panel.querySelector<HTMLElement>('.page-fields'), 'field list of the panel');
    return Array.from(list.children, (child) => (child instanceof HTMLElement ? child.dataset.fieldName : undefined));
  }

  // FormDefinitionFactory lists a row repeating a data field's exact name as an unsupported
  // placeholder after the data field, and never resolves a group member to it.
  it('keeps a repeated row and its data field apart on one page, across an edit', async () => {
    const base = createFormDefinitionFixture();
    const [firstPage] = pagesInPositionOrder(base);
    const definition = withAddedFields(
      base,
      addedField({
        name: 'Zip',
        fieldType: 'unsupported',
        prompt: 'Zip (repeated row)',
        tabIndex: 11.5,
        pageId: firstPage.pageId,
        unsupportedType: 'Text',
      }),
    );
    const consoleError = vi.spyOn(console, 'error');
    const { container } = await renderForm(definition);
    const control = screen.getByLabelText('Zip');

    fireEvent.change(control, { target: { value: '30301' } });

    expect(screen.getByLabelText('Zip')).toBe(control);
    expect(controlValue(control)).toBe('30301');
    const [canonical, repeat] = wrappersOf(container, 'Zip');
    expect(wrappersOf(container, 'Zip')).toHaveLength(2);
    expect(canonical.contains(control)).toBe(true);
    expect(within(repeat).getByText('Unsupported field type: Text')).toBeTruthy();
    expect(screen.getAllByText('Unsupported field type: Text')).toHaveLength(1);
    const keyWarnings = consoleError.mock.calls.filter((args: unknown[]) =>
      args.some((arg: unknown) => String(arg).includes('same key')),
    );
    expect(keyWarnings).toEqual([]);
  });

  it("never claims a repeated row for a group when the name's data field is on another page", async () => {
    const base = createFormDefinitionFixture();
    const [firstPage, secondPage] = pagesInPositionOrder(base);
    const definition = withAddedFields(
      base,
      addedField({
        name: 'LabGroup',
        fieldType: 'group',
        prompt: 'Laboratory',
        tabIndex: 3.5,
        pageId: secondPage.pageId,
        members: ['Lab', 'LastName'],
      }),
      addedField({
        name: 'LastName',
        fieldType: 'unsupported',
        prompt: 'Last Name (repeated row)',
        tabIndex: 5,
        pageId: secondPage.pageId,
        unsupportedType: 'Text',
      }),
    );
    const { container } = await renderForm(definition);

    const section = wrapperOf(container, 'LabGroup');
    expect(renderedFieldNames(section)).toEqual(['Lab']);
    expect(topLevelFieldNames(panelOf(secondPage))).toEqual([
      'FoodsLabel',
      'BuyLastTenDays',
      'Breastmilk',
      'LabGroup',
      'LastName',
    ]);

    const [canonical, repeat] = wrappersOf(container, 'LastName');
    expect(wrappersOf(container, 'LastName')).toHaveLength(2);
    expect(canonical.contains(screen.getByLabelText('Last Name'))).toBe(true);
    expect(wrapperOf(container, 'DemographicInformation').contains(canonical)).toBe(true);
    expect(panelOf(firstPage).contains(canonical)).toBe(true);
    expect(within(repeat).getByText('Unsupported field type: Text')).toBeTruthy();
    expect(section.contains(repeat)).toBe(false);
    expect(panelOf(secondPage).contains(repeat)).toBe(true);
  });

  // A genuinely unsupported row listed before the data field of its exact name repeats no
  // data field, so the server keeps it as a member candidate and, being first, resolves
  // the group's entry to it.
  it('claims a genuine unsupported row listed before the data field of its name for a group', async () => {
    const base = withField('DemographicInformation', (group) => ({
      ...group,
      members: [...(group.members ?? []), 'Shared'],
    }));
    const [firstPage] = pagesInPositionOrder(base);
    const definition = withAddedFields(
      base,
      addedField({
        name: 'Shared',
        fieldType: 'unsupported',
        prompt: 'Shared grid',
        tabIndex: 3.2,
        pageId: firstPage.pageId,
        unsupportedType: 'Grid',
      }),
      addedField({ name: 'Shared', fieldType: 'text', prompt: 'Shared text', tabIndex: 11.7, pageId: firstPage.pageId }),
    );
    const { container } = await renderForm(definition);

    const section = wrapperOf(container, 'DemographicInformation');
    expect(renderedFieldNames(section)).toEqual(['Shared', 'FirstName', 'LastName', 'Sex']);
    expect(within(section).getByText('Unsupported field type: Grid')).toBeTruthy();
    expect(screen.getAllByText('Unsupported field type: Grid')).toHaveLength(1);

    const control = screen.getByLabelText('Shared text');
    expect(screen.getAllByLabelText('Shared text')).toHaveLength(1);
    expect(section.contains(control)).toBe(false);
    expect(panelOf(firstPage).contains(control)).toBe(true);
    expect(wrappersOf(container, 'Shared')).toHaveLength(2);
    expect(topLevelFieldNames(panelOf(firstPage))).toEqual([
      'Title',
      'CaseID',
      'DemographicInformation',
      'DOB',
      'Age',
      'DateofInterview',
      'PhoneNumber',
      'Zip',
      'Shared',
      'PatientAddress',
      'Asian',
      'Black',
      'Headache',
      'IllnessSeverity',
      'ILL',
      'FeverTemp',
      'OnsetDate',
      'OnsetWeek',
    ]);
  });

  // The contract closes `fieldType`, but a malformed response can carry any string, and
  // these name members every object inherits. Such a field is no data field, so Find offers
  // no criterion for it either, while every published data field stays searchable.
  it.each(['constructor', 'toString', '__proto__', 'hasOwnProperty', 'valueOf'])(
    'renders a field of the malformed type %s as a protected placeholder that Find does not offer',
    async (malformed) => {
      const fieldType: string = malformed;
      const { container } = await renderForm(withField('Zip', (field) => ({ ...field, fieldType: fieldType as FieldType })));
      const wrapper = wrapperOf(container, 'Zip');

      expect(within(wrapper).getByText('Unsupported field type: unknown')).toBeTruthy();
      expect(wrapper.classList.contains('field--protected')).toBe(true);
      expect(wrapper.querySelectorAll(`${NATIVE_CONTROLS}, button`)).toHaveLength(0);

      fireEvent.click(within(screen.getByRole('toolbar', { name: 'Form actions' })).getByRole('button', { name: 'Find' }));
      const picker = within(await screen.findByRole('dialog', { name: 'Find record' })).getByRole('group', {
        name: 'Fields to search',
      });
      expect(within(picker).queryByRole('checkbox', { name: 'Zip' })).toBeNull();
      expect(within(picker).getByRole('checkbox', { name: 'PhoneNumber' })).toBeTruthy();
    },
  );

  // The form's name is the h2, so a page's own labels and groups are h3, and a field a
  // group holds is one level below the group's heading.
  it.each([
    {
      held: 'a label',
      heading: 'Food History Interview',
      definition: () =>
        withField('DemographicInformation', (group) => ({ ...group, members: [...(group.members ?? []), 'Title'] })),
    },
    {
      held: 'a nested group',
      heading: 'Nested group',
      definition: () => {
        const base = withField('DemographicInformation', (group) => ({
          ...group,
          members: [...(group.members ?? []), 'NestedGroup'],
        }));
        const [firstPage] = pagesInPositionOrder(base);
        return withAddedFields(
          base,
          addedField({
            name: 'NestedGroup',
            fieldType: 'group',
            prompt: 'Nested group',
            tabIndex: 4.5,
            pageId: firstPage.pageId,
            members: ['Zip'],
          }),
        );
      },
    },
  ])('renders $held that a group holds one heading level below the group', async ({ heading, definition }) => {
    const { container, definition: rendered } = await renderForm(definition());
    const section = wrapperOf(container, 'DemographicInformation');

    expect(within(section).getByRole('heading', { level: 3, name: 'Demographic Information' })).toBeTruthy();
    expect(within(section).getByRole('heading', { level: 4, name: heading })).toBeTruthy();
    expect(screen.queryByRole('heading', { level: 3, name: heading })).toBeNull();

    // A label at the top level of another page keeps level 3.
    showPage(pagesInPositionOrder(rendered)[1]);
    expect(screen.getByRole('heading', { level: 3, name: 'Foods eaten in the 10 days before illness' })).toBeTruthy();
  });
});

describe('FormView save progress', () => {
  it('announces Saving… in the form toolbar while a save is under way, with Save disabled and still named Save, then Changes saved. until the next edit', async () => {
    const { definition } = await renderForm();
    const formActions = screen.getByRole('toolbar', { name: 'Form actions' });
    const status = within(formActions).getByRole('status');
    const save = within(formActions).getByRole('button', { name: 'Save' });
    expect(status.textContent).toBe('');
    expect(isDisabled(save)).toBe(false);

    edit(definition, 'LastName', EDITED_LAST_NAME);
    const held = holdWrite();
    fireEvent.click(save);
    await waitFor(() => {
      expect(writeCalls()).toHaveLength(1);
    });

    expect(within(formActions).getByRole('status')).toBe(status);
    expect(status.textContent).toBe('Saving…');
    expect(within(formActions).getByRole('button', { name: 'Save' })).toBe(save);
    expect(isDisabled(save)).toBe(true);

    held.release({ kind: 'saved', record: savedWith(RECORD, { LastName: EDITED_LAST_NAME }) });
    // The same status confirms the stored edit, visibly, until the draft changes again.
    await waitFor(() => {
      expect(status.textContent).toBe('Changes saved.');
    });
    expect(status.classList.contains('visually-hidden')).toBe(false);

    expect(isDisabled(save)).toBe(false);
    const writes = writeCalls();
    expect(writes.map(requestLine)).toEqual([`PUT ${FORM_PATH}/records/${RECORD.globalRecordId}`]);
    expect(writtenValues(writes[0])).toEqual({ LastName: EDITED_LAST_NAME });

    edit(definition, 'FirstName', 'Augusta');
    expect(within(formActions).getByRole('status')).toBe(status);
    expect(status.textContent).toBe('');
    expect(status.classList.contains('visually-hidden')).toBe(true);
  });

  it('announces Saving… and holds Save, Delete, the navigator and the grid from the start of the drain a move of a dirty record awaits', async () => {
    const held = deferred();
    const events: Call[] = [];
    const { definition } = await renderForm(createFormDefinitionFixture(), {
      records: [RECORD_A, RECORD_B],
      route: (call) => {
        if (call.method !== 'POST' || !call.url.pathname.endsWith('/checkcode/field-event')) {
          return null;
        }
        events.push(call);
        return held.response;
      },
    });
    const formActions = screen.getByRole('toolbar', { name: 'Form actions' });
    const navigator = screen.getByRole('toolbar', { name: 'Record navigation' });
    const status = within(formActions).getByRole('status');
    edit(definition, 'LastName', EDITED_LAST_NAME);
    // Leaving the field raises FieldAfter, which the fake holds.
    fireEvent.blur(controlOf(definition, 'LastName'));
    await waitFor(() => {
      expect(events).toHaveLength(1);
    });

    fireEvent.click(navigatorButton('Next'));

    // Still draining: nothing is written yet, and the save the leave makes is already shown.
    await waitFor(() => {
      expect(status.textContent).toBe('Saving…');
    });
    expect(writeCalls().map(requestLine)).toEqual([`POST ${FORM_PATH}/checkcode/field-event`]);
    expect(isDisabled(formActionButton('Save'))).toBe(true);
    expect(isDisabled(formActionButton('Delete'))).toBe(true);
    for (const name of ['First', 'Previous', 'Next', 'Last', 'Go', 'New']) {
      expect(isDisabled(within(navigator).getByRole('button', { name })), name).toBe(true);
    }
    expect(navigator.getAttribute('aria-busy')).toBe('true');
    const grid = screen.getByRole('region', { name: 'Records' });
    expect(within(grid).getByRole('row', { name: new RegExp(B_LAST_NAME) }).getAttribute('aria-disabled')).toBe('true');

    replyToWrite({ kind: 'saved', record: savedWith(RECORD_A, { LastName: EDITED_LAST_NAME }) });
    held.release(jsonResponse(200, NO_EFFECTS));

    await waitFor(() => {
      expect(navigatorStatus()).toBe('2 of 2');
    });
    expect(writeCalls().map(requestLine)).toEqual([
      `POST ${FORM_PATH}/checkcode/field-event`,
      `PUT ${FORM_PATH}/records/${RECORD_A.globalRecordId}`,
    ]);
    expect(isDisabled(navigatorButton('Previous'))).toBe(false);
    // The save the move made is confirmed on the record it made way for, until a later move.
    expect(status.textContent).toBe('Changes saved.');
    fireEvent.click(navigatorButton('Previous'));
    await waitFor(() => {
      expect(navigatorStatus()).toBe('1 of 2');
    });
    expect(status.textContent).toBe('');
  });
});

describe('FormView validation summary jumps', () => {
  // A rejected save lists each message under its field's label, a button. The label of a field
  // whose control can take focus shows the field's page and focuses the control. A protected,
  // check-code-disabled or unrendered control, or a radio group with no radio, cannot take
  // focus: its label stays the same button, marked aria-disabled and described by a note that
  // the field cannot be edited, and activating it does nothing.
  const WRITABLE = 'Lab';
  const WRITABLE_MESSAGE = 'Lab must name a laboratory the state accepts.';
  const BLOCKED_MESSAGE = 'The server rejected this value.';
  const NOT_EDITABLE = '(This field cannot be edited right now.)';

  /**
   * `fakeApi`, plus a field-event route whose nth reply, counted from 1, disables the fields
   * `disabledFor(n)` names once it settles, and a save route that rejects the open record's
   * `PUT` with `errors`.
   */
  function rejectingSaveApi(
    definition: FormDefinition,
    disabledFor: (event: number) => readonly string[] | Promise<readonly string[]>,
    errors: Readonly<Record<string, string[]>>,
  ): typeof fetch {
    const serve = fakeApi(definition, [RECORD]);
    const form = `/api/forms/${definition.viewId}`;
    let events = 0;
    return async (input, init) => {
      const request = input instanceof Request ? input : new Request(input, init);
      const url = new URL(request.url);
      const isFieldEvent = request.method === 'POST' && url.pathname === `${form}/checkcode/field-event`;
      const isSave = request.method === 'PUT' && url.pathname === `${form}/records/${RECORD.globalRecordId}`;
      if (!isFieldEvent && !isSave) {
        return serve(request);
      }
      const text = await request.text();
      calls.push({ method: request.method, url, body: text === '' ? null : text });
      if (isFieldEvent) {
        events += 1;
        const disabled = await disabledFor(events);
        const effects: FieldEventResponse = {
          enabled: [],
          disabled: [...disabled],
          cleared: [],
          assigned: {},
          dialogs: [],
          diagnostics: [],
        };
        return jsonResponse(200, effects);
      }
      const problem: HttpValidationProblemDetails = {
        type: 'https://tools.ietf.org/html/rfc9110#section-15.5.1',
        title: 'One or more validation errors occurred.',
        status: 400,
        detail: 'See the errors property for details.',
        errors: { ...errors },
        traceId: '00-test-validation-00',
      };
      return jsonResponse(400, problem);
    };
  }

  /** Lets pending promise callbacks and zero-delay timers run inside act. */
  async function settle(): Promise<void> {
    await act(async () => {
      await new Promise<void>((resolve) => {
        setTimeout(resolve, 0);
      });
    });
  }

  function fieldEventCalls(): Call[] {
    return calls.filter((call) => call.url.pathname.endsWith('/checkcode/field-event'));
  }

  /**
   * The element of field `name` that can take focus: its enabled control, or for a radio
   * group an enabled radio; `null` when it is not rendered, disabled or holds no radio.
   */
  function focusTargetOf(name: string): HTMLElement | null {
    const control = document.getElementById(fieldControlId(name));
    if (control === null || isDisabled(control)) {
      return null;
    }
    return control instanceof HTMLFieldSetElement
      ? control.querySelector<HTMLElement>('input:not(:disabled)')
      : control;
  }

  /** A promise the test settles itself, to hold a reply until the test releases it. */
  function deferred<T>(): { readonly promise: Promise<T>; readonly resolve: (value: T) => void } {
    let resolve: (value: T) => void = () => undefined;
    const promise = new Promise<T>((release) => {
      resolve = release;
    });
    return { promise, resolve };
  }

  /** Asserts that a summary label button is marked unavailable and described by the note. */
  function expectUnavailable(button: HTMLElement): void {
    expect(button.getAttribute('aria-disabled')).toBe('true');
    const noteId = present(button.getAttribute('aria-describedby'), 'aria-describedby of the label');
    expect(document.getElementById(noteId)?.textContent).toBe(NOT_EDITABLE);
  }

  function isSelected(page: PageDefinition): boolean {
    return screen.getByRole('tab', { name: page.name }).getAttribute('aria-selected') === 'true';
  }

  /**
   * Opens the record of `definition`, edits Last Name, which raises FieldBefore, and saves.
   * The save drains that event, then its `PUT` is rejected with `errors`. Returns the summary.
   */
  async function rejectedSave(
    definition: FormDefinition,
    disabledFor: (event: number) => readonly string[] | Promise<readonly string[]>,
    errors: Readonly<Record<string, string[]>>,
  ): Promise<HTMLElement> {
    const client = createApiClient({
      baseUrl: 'http://localhost',
      fetch: rejectingSaveApi(definition, disabledFor, errors),
    });
    render(
      <FormView
        viewId={definition.viewId}
        client={client}
        onExit={() => undefined}
        onFormNotFound={() => undefined}
      />,
    );
    await screen.findByRole('heading', { name: definition.name });
    await within(screen.getByRole('toolbar', { name: 'Record navigation' })).findByText('1 of 1');
    await within(screen.getByRole('region', { name: 'Records' })).findByText('1 record');
    // Past the tick in which installing the record suppresses field events.
    await settle();

    const lastName = screen.getByLabelText('Last Name');
    act(() => {
      lastName.focus();
    });
    fireEvent.change(lastName, { target: { value: 'Byron' } });
    const formActions = screen.getByRole('toolbar', { name: 'Form actions' });
    fireEvent.click(within(formActions).getByRole('button', { name: 'Save' }));

    const summary = await screen.findByRole('alert');
    expect(calls.filter((call) => call.method === 'PUT')).toHaveLength(1);
    return summary;
  }

  it.each<{
    reason: string;
    blocked: string;
    disabledByCheckCode: readonly string[];
    form?: () => FormDefinition;
  }>([
    { reason: 'read-only in metadata', blocked: 'OnsetWeek', disabledByCheckCode: [] },
    { reason: 'disabled by check code', blocked: 'FirstName', disabledByCheckCode: ['FirstName'] },
    { reason: 'unsupported, so it has no control', blocked: 'ILL', disabledByCheckCode: [] },
    {
      // Valid metadata: the enabled fieldset renders, but holds no radio to focus.
      reason: 'an option field with no option',
      blocked: 'IllnessSeverity',
      disabledByCheckCode: [],
      form: () => withField('IllnessSeverity', (field) => ({ ...field, options: [] })),
    },
  ])(
    'jumps to a focusable field, and marks the label of $blocked unavailable: $reason',
    async ({ blocked, disabledByCheckCode, form = createFormDefinitionFixture }) => {
      const definition = form();
      const errors = { [blocked]: [BLOCKED_MESSAGE], [WRITABLE]: [WRITABLE_MESSAGE] };
      // Every field-event reply applies the check-code disabling, the first before the save's PUT.
      const summary = await rejectedSave(definition, () => disabledByCheckCode, errors);
      const blockedPrompt = promptOf(fieldByName(blocked, definition));
      const writablePrompt = promptOf(fieldByName(WRITABLE, definition));
      expect(within(summary).getAllByRole('listitem').map((item) => item.textContent)).toEqual([
        `${blockedPrompt}: ${BLOCKED_MESSAGE} ${NOT_EDITABLE}`,
        `${writablePrompt} ${WRITABLE_MESSAGE}`,
      ]);

      // The blocked field: nothing of its control can take focus, so its label is unavailable.
      expect(focusTargetOf(blocked), `focusable control of ${blocked}`).toBeNull();
      const blockedLabel = within(summary).getByRole('button', { name: blockedPrompt });
      expectUnavailable(blockedLabel);
      const writableLabel = within(summary).getByRole('button', { name: writablePrompt });
      expect(writableLabel.hasAttribute('aria-disabled')).toBe(false);
      expect(writableLabel.hasAttribute('aria-describedby')).toBe(false);

      // The writable field on page 2: its label shows that page and focuses its control.
      const writablePage = pageOf(definition, fieldByName(WRITABLE, definition).pageId);
      expect(isSelected(writablePage)).toBe(false);
      fireEvent.click(writableLabel);
      expect(isSelected(writablePage)).toBe(true);
      expect(document.activeElement).toBe(screen.getByLabelText(writablePrompt));

      // Activating the blocked label (Enter and Space on a button arrive as this click) shows
      // its page, page 1, no more than it focuses a control: focus stays on the label.
      expect(fieldByName(blocked, definition).pageId).not.toBe(writablePage.pageId);
      act(() => {
        blockedLabel.focus();
      });
      fireEvent.click(blockedLabel);
      expect(isSelected(writablePage)).toBe(true);
      expect(document.activeElement).toBe(blockedLabel);

      // Moving focus raised FieldAfter on Last Name, then FieldBefore and FieldAfter on Lab;
      // let their replies land.
      await waitFor(() => {
        expect(fieldEventCalls()).toHaveLength(4);
      });
      await settle();
    },
  );

  it('keeps focus on a jump label whose field a later field-event reply disables', async () => {
    const definition = createFormDefinitionFixture();
    const writablePrompt = promptOf(fieldByName(WRITABLE, definition));
    const writablePage = pageOf(definition, fieldByName(WRITABLE, definition).pageId);
    // FieldBefore on Last Name disables nothing. The reply to the FieldAfter that focusing the
    // jump raises is held until the test releases it, and then disables Lab.
    const heldReply = deferred<readonly string[]>();
    const summary = await rejectedSave(definition, (event) => (event === 1 ? [] : heldReply.promise), {
      [WRITABLE]: [WRITABLE_MESSAGE],
    });

    const jump = within(summary).getByRole('button', { name: writablePrompt });
    expect(jump.hasAttribute('aria-disabled')).toBe(false);
    act(() => {
      jump.focus();
    });
    await waitFor(() => {
      expect(fieldEventCalls()).toHaveLength(2);
    });
    expect(document.activeElement).toBe(jump);

    heldReply.resolve([WRITABLE]);
    await waitFor(() => {
      expect(focusTargetOf(WRITABLE), 'focusable control of Lab').toBeNull();
    });
    await settle();

    // The same element, now unavailable, still holds focus.
    expect(within(screen.getByRole('alert')).getByRole('button', { name: writablePrompt })).toBe(jump);
    expectUnavailable(jump);
    expect(document.activeElement).toBe(jump);
    expect(within(summary).getAllByRole('listitem').map((item) => item.textContent)).toEqual([
      `${writablePrompt} ${WRITABLE_MESSAGE} ${NOT_EDITABLE}`,
    ]);

    // Activating it now shows no page and moves no focus.
    fireEvent.click(jump);
    expect(isSelected(writablePage)).toBe(false);
    expect(document.activeElement).toBe(jump);
  });
});

describe('FormView permission flags', () => {
  // The server reports these flags from SQL Server permissions and enforces them on every
  // write; the client only disables what they deny. `null` means nothing was reported and
  // restricts nothing; only `false` disables.
  type Flags = Pick<FormDefinition, 'canInsert' | 'canUpdate' | 'canDelete'>;

  it.each<{ label: string; flags: Flags; save: boolean; remove: boolean; create: boolean }>([
    {
      label: 'none reported',
      flags: { canInsert: null, canUpdate: null, canDelete: null },
      save: true,
      remove: true,
      create: true,
    },
    {
      label: 'canDelete true, canUpdate false',
      flags: { canInsert: true, canUpdate: false, canDelete: true },
      save: false,
      remove: true,
      create: true,
    },
    {
      label: 'canInsert false, canDelete false',
      flags: { canInsert: false, canUpdate: true, canDelete: false },
      save: true,
      remove: false,
      create: false,
    },
  ])('enables Save, Delete and New by the flags: $label', async ({ flags, save, remove, create }) => {
    await renderForm({ ...createFormDefinitionFixture(), ...flags });
    const formActions = screen.getByRole('toolbar', { name: 'Form actions' });
    const navigator = screen.getByRole('toolbar', { name: 'Record navigation' });

    expect(isDisabled(within(formActions).getByRole('button', { name: 'Save' }))).toBe(!save);
    expect(isDisabled(within(formActions).getByRole('button', { name: 'Delete' }))).toBe(!remove);
    expect(isDisabled(within(navigator).getByRole('button', { name: 'New' }))).toBe(!create);
  });

  // On an empty form the navigator opens New, whose first Save is a create: the flags
  // below oppose each other, so Save follows canInsert there and never canUpdate.
  it.each<{ label: string; flags: Flags; save: boolean; create: boolean }>([
    {
      label: 'canInsert true, canUpdate false',
      flags: { canInsert: true, canUpdate: false, canDelete: null },
      save: true,
      create: true,
    },
    {
      label: 'canInsert false, canUpdate true',
      flags: { canInsert: false, canUpdate: true, canDelete: null },
      save: false,
      create: false,
    },
  ])('enables the first Save of a new record and New by canInsert alone: $label', async ({ flags, save, create }) => {
    await renderForm({ ...createFormDefinitionFixture(), ...flags }, { records: [] });

    expect(navigatorStatus()).toBe('New record');
    expect(isDisabled(formActionButton('Save'))).toBe(!save);
    expect(isDisabled(navigatorButton('New'))).toBe(!create);
  });

  it('POSTs the first Save of a new record under canInsert, then holds the stored record to canUpdate', async () => {
    const { definition } = await renderForm(
      { ...createFormDefinitionFixture(), canInsert: true, canUpdate: false, canDelete: null },
      { records: [] },
    );
    edit(definition, 'LastName', B_LAST_NAME);
    // A create sends every entered value and false for each unchecked writable checkbox.
    const sent: Values = {
      LastName: B_LAST_NAME,
      ...Object.fromEntries(writableCheckboxNames.map((name) => [name, false])),
    };
    replyToWrite({
      kind: 'saved',
      record: recordFixture({
        globalRecordId: '3f2b8c1e-5d4a-4c1b-9e7f-000000000003',
        uniqueKey: 3,
        position: 1,
        count: 1,
        values: sent,
      }),
    });

    fireEvent.click(formActionButton('Save'));

    await waitFor(() => {
      expect(navigatorStatus()).toBe('1 of 1');
    });
    await within(screen.getByRole('region', { name: 'Records' })).findByText('1 record');
    const writes = writeCalls();
    expect(writes.map(requestLine)).toEqual([`POST ${FORM_PATH}/records`]);
    expect(writtenValues(writes[0])).toEqual(sent);
    expect(controlValue(controlOf(definition, 'LastName'))).toBe(B_LAST_NAME);
    // The record is stored now, so its next Save is an update, which canUpdate denies.
    expect(isDisabled(formActionButton('Save'))).toBe(true);
    expect(isDisabled(navigatorButton('New'))).toBe(false);
  });

  // With nothing reported nothing is disabled, so the server's 403 is the only refusal.
  it.each<{ label: string; records: readonly RecordDto[]; value: string; write: string; status: string }>([
    {
      label: 'PUT of a stored record',
      records: [RECORD],
      value: EDITED_LAST_NAME,
      write: `PUT ${FORM_PATH}/records/${RECORD.globalRecordId}`,
      status: '1 of 1',
    },
    {
      label: 'POST of a new record',
      records: [],
      value: B_LAST_NAME,
      write: `POST ${FORM_PATH}/records`,
      status: 'New record',
    },
  ])(
    'keeps the draft and shows a permission banner when the server answers the $label with 403',
    async ({ records, value, write, status }) => {
      const { definition } = await renderForm(createFormDefinitionFixture(), { records });
      expect(isDisabled(formActionButton('Save'))).toBe(false);
      edit(definition, 'LastName', value);
      replyToWrite({ kind: 'problem', problem: FORBIDDEN_PROBLEM });

      fireEvent.click(formActionButton('Save'));

      await findAlertContaining('permission');
      await flush();
      const writes = writeCalls();
      expect(writes.map(requestLine)).toEqual([write]);
      // Nothing follows the refused write: no refresh and no move.
      expect(calls.slice(calls.indexOf(writes[0]) + 1).map(requestLine)).toEqual([]);
      expect(controlValue(controlOf(definition, 'LastName'))).toBe(value);
      expect(navigatorStatus()).toBe(status);
    },
  );
});

describe('FormView leaving a dirty record', () => {
  // Opening a grid row and Back to forms each save a dirty draft first, wait for that
  // save, and continue only once it succeeds; a failed save keeps the draft on screen
  // and cancels the transition.
  const REJECTED_NAME = 'The test server rejects this name.';

  interface Rejection {
    readonly label: string;
    readonly problem: ProblemDetails;
    /** Text, ignoring case, of the alert that shows the failure. */
    readonly shown: string;
  }

  // Each failure shows text only the test chose (its trace id or message), or for the
  // 403, the meaning of the client's permission banner.
  const REJECTIONS: readonly Rejection[] = [
    { label: '500 notWritten', problem: NOT_WRITTEN_PROBLEM, shown: NOT_WRITTEN_PROBLEM.traceId },
    { label: 'field-level 400', problem: fieldLevelProblem({ LastName: [REJECTED_NAME] }), shown: REJECTED_NAME },
    { label: '403', problem: FORBIDDEN_PROBLEM, shown: 'permission' },
  ];

  /** Every request that reads B: by id, its position, or at its place in the set. */
  function readsOfB(): string[] {
    const byId = `${FORM_PATH}/records/${RECORD_B.globalRecordId}`;
    // Every record of the set A, B is active, so B's place in it is B's own position.
    const atPlace = `${FORM_PATH}/records/at/${RECORD_B.position}`;
    return calls
      .filter(
        (call) =>
          call.url.pathname === byId || call.url.pathname.startsWith(`${byId}/`) || call.url.pathname === atPlace,
      )
      .map(requestLine);
  }

  /** B's grid row, found by the last name it shows. */
  function rowOfB(): HTMLElement {
    const records = screen.getByRole('region', { name: 'Records' });
    return within(records).getByRole('row', { name: new RegExp(B_LAST_NAME) });
  }

  it('saves the dirty draft before a grid open, and opens the row only once the held save succeeds', async () => {
    const { definition } = await renderForm(createFormDefinitionFixture(), { records: [RECORD_A, RECORD_B] });
    edit(definition, 'LastName', EDITED_LAST_NAME);
    const held = holdWrite();

    // The grid is disabled while a save runs, so the click comes first and triggers the save.
    fireEvent.click(rowOfB());

    await waitFor(() => {
      expect(writeCalls()).toHaveLength(1);
    });
    await flush();
    const [put] = writeCalls();
    expect(requestLine(put)).toBe(`PUT ${FORM_PATH}/records/${RECORD_A.globalRecordId}`);
    expect(writtenValues(put)).toEqual({ LastName: EDITED_LAST_NAME });
    // While the save is held, B is not read and A's draft stays on screen.
    expect(readsOfB()).toEqual([]);
    expect(controlValue(controlOf(definition, 'LastName'))).toBe(EDITED_LAST_NAME);
    expect(navigatorStatus()).toBe('1 of 2');

    held.release({ kind: 'saved', record: savedWith(RECORD_A, { LastName: EDITED_LAST_NAME }) });

    await waitFor(() => {
      expect(navigatorStatus()).toBe('2 of 2');
      expect(controlValue(controlOf(definition, 'LastName'))).toBe(B_LAST_NAME);
    });
    await flush();
    expect(writeCalls()).toEqual([put]);
    const readOfB = calls.findIndex(
      (call) => call.method === 'GET' && call.url.pathname === `${FORM_PATH}/records/${RECORD_B.globalRecordId}`,
    );
    expect(readOfB).toBeGreaterThan(calls.indexOf(put));
  });

  it.each(REJECTIONS)(
    'keeps the draft and the open record when the save before a grid open fails: $label',
    async ({ problem, shown }) => {
      const { definition } = await renderForm(createFormDefinitionFixture(), { records: [RECORD_A, RECORD_B] });
      edit(definition, 'LastName', EDITED_LAST_NAME);
      replyToWrite({ kind: 'problem', problem });

      fireEvent.click(rowOfB());

      await findAlertContaining(shown);
      await flush();
      expect(writeCalls().map(requestLine)).toEqual([`PUT ${FORM_PATH}/records/${RECORD_A.globalRecordId}`]);
      expect(readsOfB()).toEqual([]);
      expect(controlValue(controlOf(definition, 'LastName'))).toBe(EDITED_LAST_NAME);
      expect(screen.queryByDisplayValue(B_LAST_NAME)).toBeNull();
      expect(navigatorStatus()).toBe('1 of 2');
    },
  );

  it('saves the dirty draft before Back to forms, and exits only once the held save succeeds', async () => {
    const { definition, onExit } = await renderForm();
    edit(definition, 'LastName', EDITED_LAST_NAME);
    const held = holdWrite();

    fireEvent.click(formActionButton('Back to forms'));

    await waitFor(() => {
      expect(writeCalls()).toHaveLength(1);
    });
    await flush();
    const [put] = writeCalls();
    expect(requestLine(put)).toBe(`PUT ${FORM_PATH}/records/${RECORD.globalRecordId}`);
    expect(writtenValues(put)).toEqual({ LastName: EDITED_LAST_NAME });
    expect(onExit).not.toHaveBeenCalled();
    expect(controlValue(controlOf(definition, 'LastName'))).toBe(EDITED_LAST_NAME);

    held.release({ kind: 'saved', record: savedWith(RECORD, { LastName: EDITED_LAST_NAME }) });

    await waitFor(() => {
      expect(onExit).toHaveBeenCalledTimes(1);
    });
    await flush();
    expect(onExit).toHaveBeenCalledTimes(1);
    expect(writeCalls()).toEqual([put]);
  });

  it.each<{ label: string; edited: boolean }>([
    { label: 'an edited record, whose save Back to forms made', edited: true },
    { label: 'a clean record, which Back to forms left without a save', edited: false },
  ])('tells onExit whether Back to forms saved, for $label', async ({ edited }) => {
    const { definition, onExit } = await renderForm();
    if (edited) {
      edit(definition, 'LastName', EDITED_LAST_NAME);
      replyToWrite({ kind: 'saved', record: savedWith(RECORD, { LastName: EDITED_LAST_NAME }) });
    }

    fireEvent.click(formActionButton('Back to forms'));

    await waitFor(() => {
      expect(onExit).toHaveBeenCalledTimes(1);
    });
    expect(onExit).toHaveBeenCalledWith(edited);
    expect(writeCalls().map(requestLine)).toEqual(edited ? [`PUT ${FORM_PATH}/records/${RECORD.globalRecordId}`] : []);
  });

  it.each(REJECTIONS)(
    'keeps the draft and does not exit when the save before Back to forms fails: $label',
    async ({ problem, shown }) => {
      const { definition, onExit } = await renderForm();
      edit(definition, 'LastName', EDITED_LAST_NAME);
      replyToWrite({ kind: 'problem', problem });

      fireEvent.click(formActionButton('Back to forms'));

      await findAlertContaining(shown);
      await flush();
      expect(writeCalls().map(requestLine)).toEqual([`PUT ${FORM_PATH}/records/${RECORD.globalRecordId}`]);
      expect(onExit).not.toHaveBeenCalled();
      expect(controlValue(controlOf(definition, 'LastName'))).toBe(EDITED_LAST_NAME);
      expect(navigatorStatus()).toBe('1 of 1');
    },
  );
});

describe('FormView a save that finds the record deleted (409)', () => {
  const CONFLICT_PROBLEM: ProblemDetails = {
    type: 'https://tools.ietf.org/html/rfc9110#section-15.5.10',
    title: 'Conflict',
    status: 409,
    detail: 'The test server found this record deleted.',
    traceId: '00-test-conflict-00',
  };

  it('keeps the record read-only and reloads the grid, then Next re-reads N and opens the record that followed it', async () => {
    const definition = createFormDefinitionFixture();
    // B as the server returns it once A is deleted: the only record of the active set.
    const bAlone: RecordDto = { ...RECORD_B, position: 1, count: 1 };
    let aDeleted = false;
    await renderForm(definition, {
      records: [RECORD_A, RECORD_B],
      answerRead: (path) => {
        if (!aDeleted) {
          return undefined;
        }
        switch (path) {
          case `${FORM_PATH}/records`: {
            const page: RecordPage = {
              items: [recordSummaryFixture(bAlone, definition)],
              total: 1,
              page: 1,
              pageSize: 50,
            };
            return jsonResponse(200, page);
          }
          case `${FORM_PATH}/records/count`:
            return jsonResponse(200, { count: 1 });
          case `${FORM_PATH}/records/at/1`:
            return jsonResponse(200, bAlone);
          default:
            return undefined;
        }
      },
    });
    edit(definition, 'LastName', EDITED_LAST_NAME);
    // A is deleted elsewhere before the save reaches the server.
    aDeleted = true;
    replyToWrite({ kind: 'problem', problem: CONFLICT_PROBLEM });

    fireEvent.click(formActionButton('Save'));

    // The save's conflict banner stands in for the deleted status while it is shown.
    await screen.findByText('This record has been deleted and is read-only. Your unsaved changes were not saved.');
    // The grid reloads and no longer lists A, while A stays open at the n and N shown before.
    await within(screen.getByRole('region', { name: 'Records' })).findByText('1 record');
    await flush();
    const put = present(writeCalls()[0], 'the PUT of A');
    expect(requestLine(put)).toBe(`PUT ${FORM_PATH}/records/${RECORD_A.globalRecordId}`);
    expect(navigatorStatus()).toBe('1 of 2');
    expect(isDisabled(navigatorButton('Next'))).toBe(false);

    fireEvent.click(navigatorButton('Next'));

    await waitFor(() => {
      expect(navigatorStatus()).toBe('1 of 1');
      expect(controlValue(controlOf(definition, 'LastName'))).toBe(B_LAST_NAME);
    });
    await flush();
    const navigationReads = calls
      .slice(calls.indexOf(put))
      .filter((call) => /\/records\/(count|at\/\d+)$/.test(call.url.pathname))
      .map(requestLine);
    expect(navigationReads).toEqual([
      `GET ${FORM_PATH}/records/count?includeDeleted=false`,
      `GET ${FORM_PATH}/records/at/1?includeDeleted=false`,
    ]);
    expect(writeCalls()).toEqual([put]);
    expect(isDisabled(navigatorButton('Next'))).toBe(true);
  });
});

describe('FormView validation summary', () => {
  it('lists each field of a field-level 400 under its prompt with the server message; the draft stays', async () => {
    const { definition } = await renderForm();
    // Values the client accepts, so only the server rejects them. Each prompt differs from
    // its field name, and each message is the test's own and names neither prompt, so an
    // item is matched to its field by the prompt alone.
    const rejected = [
      {
        name: 'LastName',
        entered: EDITED_LAST_NAME,
        sent: EDITED_LAST_NAME,
        message: 'The test server rejects this surname.',
      },
      { name: 'FeverTemp', entered: '101.5', sent: 101.5, message: 'The test server rejects this reading.' },
    ];
    for (const { name, entered } of rejected) {
      edit(definition, name, entered);
    }
    replyToWrite({
      kind: 'problem',
      problem: fieldLevelProblem(Object.fromEntries(rejected.map(({ name, message }) => [name, [message]]))),
    });

    fireEvent.click(formActionButton('Save'));

    const summary = await findValidationSummary();
    await flush();
    const writes = writeCalls();
    expect(writes.map(requestLine)).toEqual([`PUT ${FORM_PATH}/records/${RECORD.globalRecordId}`]);
    expect(writtenValues(writes[0])).toEqual(Object.fromEntries(rejected.map(({ name, sent }) => [name, sent])));
    for (const { name, message } of rejected) {
      const items = summaryItemsFor(summary, promptOf(fieldByName(name, definition)));
      expect(
        items.map((item) => item.textContent?.includes(message)),
        name,
      ).toEqual([true]);
    }
    expect(within(summary).getAllByRole('listitem')).toHaveLength(rejected.length);
    // Each message is also shown beside its field, as the control's description.
    for (const { name, message } of rejected) {
      expect(descriptionOf(controlOf(definition, name)), name).toContain(message);
    }
    // The draft stays, and nothing follows the rejected write.
    for (const { name, entered } of rejected) {
      expect(controlValue(controlOf(definition, name)), name).toBe(entered);
    }
    expect(calls.slice(calls.indexOf(writes[0]) + 1).map(requestLine)).toEqual([]);
    expect(navigatorStatus()).toBe('1 of 1');
  });

  it('lists every field client validation rejects under its prompt with a message, and sends no write', async () => {
    const { definition } = await renderForm();
    const zip = fieldByName('Zip', definition);
    // Its prompt differs from its name, so the summary must show the prompt itself.
    const feverTemp = fieldByName('FeverTemp', definition);
    const maxLength = present(zip.validation.maxLength, 'maxLength of Zip');
    const upper = feverTemp.validation.upper;
    if (typeof upper !== 'number') {
      throw new Error('FeverTemp has no numeric upper bound');
    }
    // Past the limits the fixture sets. The input's maxLength stops typing in a browser;
    // fireEvent.change sets the value directly, standing for any entry that gets past it.
    const entries = [
      { field: zip, entered: '9'.repeat(maxLength + 1) },
      { field: feverTemp, entered: String(upper + 1) },
    ];
    for (const { field, entered } of entries) {
      edit(definition, field.name, entered);
    }

    fireEvent.click(formActionButton('Save'));

    const summary = await findValidationSummary();
    await flush();
    expect(writeCalls().map(requestLine)).toEqual([]);
    const prompts = entries.map(({ field }) => promptOf(field));
    for (const prompt of prompts) {
      const items = summaryItemsFor(summary, prompt);
      expect(items, prompt).not.toHaveLength(0);
      for (const item of items) {
        // A message follows the prompt.
        expect((item.textContent ?? '').slice(prompt.length).replace(/[\s:]/g, ''), prompt).not.toBe('');
      }
    }
    // Every item is one of the two fields.
    for (const item of within(summary).getAllByRole('listitem')) {
      expect(
        prompts.some((prompt) => (item.textContent ?? '').startsWith(prompt)),
        item.textContent ?? '',
      ).toBe(true);
    }
    for (const { field, entered } of entries) {
      expect(controlValue(controlOf(definition, field.name)), field.name).toBe(entered);
    }
    expect(navigatorStatus()).toBe('1 of 1');
  });

  // Last Name sits on page 1; Lab sits on page 2 and its prompt ends with a colon. No seeded
  // field is required, so each case makes the fixture field required.
  const REQUIRED_FIELDS = [{ name: 'LastName' }, { name: 'Lab' }];

  /** The fixture form with the field `name` required. */
  function requiring(name: string): FormDefinition {
    return withField(name, (field) => ({ ...field, validation: { ...field.validation, required: true } }));
  }

  /** An item's text for a message shown after its label: a label that ends with a colon is followed by a space only. */
  function afterLabel(label: string, message: string): string {
    return `${label}${label.endsWith(':') ? ' ' : ': '}${message}`;
  }

  /**
   * Checks that `item`'s one button is the summary's jump labelled by `field`'s prompt, clicks it,
   * and waits until it shows `field`'s page and focuses its control. The focus raises a field
   * event, answered with no effects, after the `earlier` writes.
   */
  async function expectJumpToField(
    summary: HTMLElement,
    item: HTMLElement,
    definition: FormDefinition,
    field: FieldDefinition,
    earlier: readonly string[],
  ): Promise<void> {
    const prompt = promptOf(field);
    const jump = within(item).getByRole('button');
    expect(jump).toBe(within(summary).getByRole('button', { name: prompt }));
    expect(jump.textContent).toBe(prompt);
    fireEvent.click(jump);
    await waitFor(() => {
      expect(document.activeElement?.id).toBe(fieldControlId(field.name));
    });
    const page = pageOf(definition, field.pageId);
    expect(screen.getByRole('tab', { name: page.name }).getAttribute('aria-selected')).toBe('true');
    expect(controlOf(definition, field.name)).toBe(document.activeElement);
    await waitFor(() => {
      expect(writeCalls().map(requestLine)).toEqual([...earlier, `POST ${FORM_PATH}/checkcode/field-event`]);
    });
  }

  it.each(REQUIRED_FIELDS)(
    "lists the client's required message of $name after its prompt, whose jump focuses the field, in form order with other messages",
    async ({ name }) => {
      const definition = requiring(name);
      const required = fieldByName(name, definition);
      const feverTemp = fieldByName('FeverTemp', definition);
      const upper = feverTemp.validation.upper;
      if (typeof upper !== 'number') {
        throw new Error('FeverTemp has no numeric upper bound');
      }
      await renderForm(definition, { answersFieldEvents: true });

      // A new record with the required field blank and one other value entered, so it is not
      // empty (an empty new record is never posted). Client validation rejects the save.
      fireEvent.click(navigatorButton('New'));
      await within(screen.getByRole('toolbar', { name: 'Record navigation' })).findByText('New record');
      edit(definition, feverTemp.name, String(upper + 1));
      fireEvent.click(formActionButton('Save'));

      const summary = await findValidationSummary();
      await flush();
      expect(writeCalls().map(requestLine)).toEqual([]);
      // FeverTemp's one message, as shown beside the field.
      const feverErrors = present(document.getElementById(fieldErrorId(feverTemp.name)), 'error list of FeverTemp');
      expect(feverErrors.querySelectorAll('p')).toHaveLength(1);
      const feverMessage = feverErrors.textContent ?? '';
      // In form order, each message after its prompt and the separator.
      const requiredText = afterLabel(promptOf(required), clientMessages.required);
      const expected = new Map([
        [required.name, requiredText],
        [feverTemp.name, afterLabel(promptOf(feverTemp), feverMessage)],
      ]);
      const items = within(summary).getAllByRole('listitem');
      expect(items.map((item) => item.textContent)).toEqual(
        allFields(definition).flatMap((field) => expected.get(field.name) ?? []),
      );

      const requiredItem = present(
        items.find((item) => item.textContent === requiredText),
        'summary item of the required field',
      );
      await expectJumpToField(summary, requiredItem, definition, required, []);
    },
  );

  it.each(REQUIRED_FIELDS)(
    "shows the server's required message of $name once, verbatim, its opening prompt as the jump label",
    async ({ name }) => {
      const definition = requiring(name);
      const required = fieldByName(name, definition);
      const prompt = promptOf(required);
      const message = serverRequiredMessage(prompt, pageOf(definition, required.pageId).name);
      // The stored record holds no value for the required field. A PUT that does not touch it
      // leaves it to the server, whose required gate rejects the save.
      const stored = savedWith(RECORD, { [required.name]: null });
      const put = `PUT ${FORM_PATH}/records/${stored.globalRecordId}`;
      replyToWrite({ kind: 'problem', problem: fieldLevelProblem({ [required.name]: [message] }) });
      await renderForm(definition, { records: [stored], answersFieldEvents: true });

      // A value the client accepts, in another field.
      edit(definition, 'FeverTemp', '101.5');
      fireEvent.click(formActionButton('Save'));

      const summary = await findValidationSummary();
      await flush();
      const writes = writeCalls();
      expect(writes.map(requestLine)).toEqual([put]);
      expect(writtenValues(writes[0])).toEqual({ FeverTemp: 101.5 });
      // The message verbatim, its prompt shown once: the jump label is its opening words.
      const items = within(summary).getAllByRole('listitem');
      expect(items.map((item) => item.textContent)).toEqual([message]);
      const [requiredItem] = items;
      expect(requiredItem.firstChild).toBe(within(requiredItem).getByRole('button', { name: prompt }));
      await expectJumpToField(summary, requiredItem, definition, required, [put]);
    },
  );

  it("shows the server's required-field message of a required field, which opens with its prompt and names its page, once, and every other message after its label", async () => {
    // Five of the rows' fields are made required; Address has no prompt, so its label is its
    // name; and the page Lab sits on has a blank name.
    const required = new Set(['CaseID', 'FirstName', 'Age', 'PatientAddress', 'Lab']);
    const fixture = createFormDefinitionFixture();
    const blankPageId = fieldByName('Lab', fixture).pageId;
    const definition: FormDefinition = {
      ...fixture,
      pages: fixture.pages.map((page) => ({
        ...page,
        name: page.pageId === blankPageId ? '  ' : page.name,
        fields: page.fields.map((field) => ({
          ...field,
          prompt: field.name === 'PatientAddress' ? null : field.prompt,
          validation: { ...field.validation, required: field.validation.required || required.has(field.name) },
        })),
      })),
    };
    const prompt = (name: string): string => promptOf(fieldByName(name, definition));
    const pageName = (name: string): string => pageOf(definition, fieldByName(name, definition).pageId).name;
    // The server's messages, in form order and then a key that names no field. Only the
    // FirstName row meets every condition and is shown once; each other row misses one and is
    // shown after its label.
    const rows = [
      // Required and opens with its prompt, but names no page.
      {
        key: 'CaseID',
        label: prompt('CaseID'),
        message: `${prompt('CaseID')} must be a whole number.`,
        once: false,
      },
      // Required, opens with its prompt and names its page.
      {
        key: 'FirstName',
        label: prompt('FirstName'),
        message: serverRequiredMessage(prompt('FirstName'), pageName('FirstName')),
        once: true,
      },
      // Opens with its prompt and names its page, but the field is not required.
      {
        key: 'LastName',
        label: prompt('LastName'),
        message: serverRequiredMessage(prompt('LastName'), pageName('LastName')),
        once: false,
      },
      // Required and names its page, but the prompt's letters only open a longer word.
      {
        key: 'Age',
        label: prompt('Age'),
        message: serverRequiredMessage(`${prompt('Age')}s`, pageName('Age')),
        once: false,
      },
      // Required and names its page, but opens with the blank prompt, not the name that labels it.
      {
        key: 'PatientAddress',
        label: 'PatientAddress',
        message: serverRequiredMessage('', pageName('PatientAddress')),
        once: false,
      },
      // Required and opens with its prompt, but its page's name is blank, which names no page.
      {
        key: 'Lab',
        label: prompt('Lab'),
        message: serverRequiredMessage(prompt('Lab'), pageName('Lab')),
        once: false,
      },
      // A key that names no field.
      {
        key: 'values',
        label: 'values',
        message: 'values holds a name that is not a field of the form.',
        once: false,
      },
    ];
    replyToWrite({
      kind: 'problem',
      problem: fieldLevelProblem(Object.fromEntries(rows.map(({ key, message }) => [key, [message]]))),
    });
    // The stored record holds a value for every required field, so only the server rejects the save.
    await renderForm(definition, { records: [savedWith(RECORD, { PatientAddress: '12 Elm Street' })] });

    edit(definition, 'FeverTemp', '101.5');
    fireEvent.click(formActionButton('Save'));

    const summary = await findValidationSummary();
    await flush();
    expect(writeCalls().map(requestLine)).toEqual([`PUT ${FORM_PATH}/records/${RECORD.globalRecordId}`]);
    const items = within(summary).getAllByRole('listitem');
    expect(items.map((item) => item.textContent)).toEqual(
      rows.map(({ label, message, once }) => (once ? message : afterLabel(label, message))),
    );
    // Each field's item opens with its jump button, named by its label; the key that names no
    // field, the last row, has none.
    rows.slice(0, -1).forEach(({ key, label }, index) => {
      expect(items[index].firstChild, key).toBe(within(items[index]).getByRole('button', { name: label }));
    });
    expect(within(items[rows.length - 1]).queryByRole('button')).toBeNull();
  });

  // Age's prompt is made the first word of the client's number message. Client feedback names no
  // page, so it follows the prompt whether or not the field is required.
  it.each([{ required: false }, { required: true }])(
    "shows the client's message after the prompt when the prompt is the message's first word (required: $required)",
    async ({ required }) => {
      const definition = withField('Age', (field) => ({
        ...field,
        prompt: 'Enter',
        validation: { ...field.validation, required },
      }));
      await renderForm(definition);

      edit(definition, 'Age', 'abc');
      fireEvent.click(formActionButton('Save'));

      const summary = await findValidationSummary();
      await flush();
      expect(writeCalls().map(requestLine)).toEqual([]);
      expect(clientMessages.numberExpected.startsWith('Enter ')).toBe(true);
      const items = within(summary).getAllByRole('listitem');
      expect(items.map((item) => item.textContent)).toEqual([`Enter: ${clientMessages.numberExpected}`]);
      // The button holds the prompt alone, and the separator and the whole message follow it.
      const jump = within(items[0]).getByRole('button', { name: 'Enter' });
      expect(items[0].firstChild).toBe(jump);
      expect(jump.textContent).toBe('Enter');
    },
  );
});

describe('FormView with no pages', () => {
  it('shows a no-pages status instead of an empty tablist and keeps the rest of the workspace', async () => {
    await renderForm({ ...createFormDefinitionFixture(), pages: [] });

    expect(screen.queryByRole('tablist')).toBeNull();
    expect(screen.queryAllByRole('tab', { hidden: true })).toEqual([]);
    expect(screen.queryAllByRole('tabpanel', { hidden: true })).toEqual([]);
    const noPages = screen.getAllByRole('status').filter((status) => status.textContent === 'This form has no pages.');
    expect(noPages).toHaveLength(1);

    expect(screen.getByRole('toolbar', { name: 'Form actions' })).toBeTruthy();
    expect(screen.getByRole('toolbar', { name: 'Record navigation' })).toBeTruthy();
    expect(screen.getByRole('region', { name: 'Records' })).toBeTruthy();
  });
});

describe('FormView sticky header and page tabs', () => {
  // jsdom lays nothing out and loads no style sheet, so these cases give the header, the tab
  // strip, the panels and a control their boxes through getBoundingClientRect, and the styles
  // FormView reads through getComputedStyle, and observe what FormView does with them: the
  // sizes it publishes for styles.css, the scroll a focused control needs and the scroll a
  // tab's selection requests. That the header and the strip stick is a browser check.
  const HEADER_HEIGHT = 140;
  const NOTICE_HEIGHT = 58;
  const TABS_HEIGHT = 44;
  const TABS_BOTTOM = HEADER_HEIGHT + TABS_HEIGHT;

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  /**
   * Gives the workspace header, `headerHeight()` tall, the check-code notice, `noticeHeight()`
   * tall, and the tab strip their boxes; other elements keep jsdom's empty box.
   */
  function layOutStickyParts(headerHeight: () => number, noticeHeight: () => number = () => NOTICE_HEIGHT): void {
    vi.spyOn(Element.prototype, 'getBoundingClientRect').mockImplementation(function (this: Element) {
      if (this.classList.contains('form-view__header')) {
        return new DOMRect(0, 0, 1000, headerHeight());
      }
      if (this.classList.contains('banner--sticky')) {
        return new DOMRect(0, headerHeight(), 1000, noticeHeight());
      }
      return this.getAttribute('role') === 'tablist' ? new DOMRect(0, HEADER_HEIGHT, 1000, TABS_HEIGHT) : new DOMRect();
    });
  }

  /** A stand-in ResizeObserver that records what it observes and runs its callback on demand. */
  class RecordingResizeObserver {
    static readonly instances: RecordingResizeObserver[] = [];
    readonly observed: Element[] = [];
    disconnected = false;

    constructor(private readonly callback: ResizeObserverCallback) {
      RecordingResizeObserver.instances.push(this);
    }

    observe(target: Element): void {
      this.observed.push(target);
    }

    unobserve(target: Element): void {
      this.observed.splice(this.observed.indexOf(target), 1);
    }

    disconnect(): void {
      this.disconnected = true;
    }

    /** Reports a resize, as the browser does after layout. */
    resize(): void {
      this.callback([], this);
    }
  }

  it('holds the form actions and the record navigator in one header, ahead of the page tabs', async () => {
    await renderForm();
    const formActions = screen.getByRole('toolbar', { name: 'Form actions' });
    const navigator = screen.getByRole('toolbar', { name: 'Record navigation' });
    const tablist = screen.getByRole('tablist', { name: 'Pages' });

    const header = present(formActions.parentElement, 'the container of the form actions');
    expect(header.classList.contains('form-view__header')).toBe(true);
    // The two toolbars, in reading order; the navigator's live announcement sits beside it.
    const toolbars = Array.from(header.children).filter((child) => child.getAttribute('role') === 'toolbar');
    expect(toolbars).toHaveLength(2);
    expect(toolbars[0]).toBe(formActions);
    expect(toolbars[1]).toBe(navigator);
    // Notices, the validation summary and the tabs scroll with the page, below the header.
    expect(header.contains(tablist)).toBe(false);
    expect(header.compareDocumentPosition(tablist) & Node.DOCUMENT_POSITION_FOLLOWING).not.toBe(0);
  });

  it('publishes the header and tab strip block sizes on the form view, again on each resize, and stops observing on unmount', async () => {
    RecordingResizeObserver.instances.length = 0;
    vi.stubGlobal('ResizeObserver', RecordingResizeObserver);
    let headerHeight = HEADER_HEIGHT;
    layOutStickyParts(() => headerHeight);
    await renderForm();
    const header = present(
      screen.getByRole('toolbar', { name: 'Form actions' }).parentElement,
      'the container of the form actions',
    );
    const formView = present(header.parentElement, 'the form view');
    const publishedSize = (name: string): string => formView.style.getPropertyValue(name);

    expect(publishedSize('--form-view-header-block-size')).toBe(`${HEADER_HEIGHT}px`);
    expect(publishedSize('--form-view-tabs-block-size')).toBe(`${TABS_HEIGHT}px`);
    // The record grid observes its own box too, so the header's observer is found by what it observes.
    const headerObservers = RecordingResizeObserver.instances.filter((instance) => instance.observed.includes(header));
    expect(headerObservers).toHaveLength(1);
    const [observer] = headerObservers;
    expect(observer.observed).toHaveLength(2);
    expect(observer.observed[0]).toBe(header);
    expect(observer.observed[1]).toBe(screen.getByRole('tablist', { name: 'Pages' }));

    // The navigator wraps onto a second row.
    headerHeight = 192;
    act(() => {
      observer.resize();
    });
    expect(publishedSize('--form-view-header-block-size')).toBe('192px');

    expect(observer.disconnected).toBe(false);
    cleanup();
    expect(observer.disconnected).toBe(true);
  });

  // The check-code notice sticks below the header, and the strip and the scroll margins count
  // it, so its size is published while it shows and 0px otherwise. Where there is no
  // ResizeObserver (jsdom) the size measured as it appears stays.
  it.each<{ environment: string; observes: boolean }>([
    { environment: 'with a ResizeObserver, again on each resize', observes: true },
    { environment: 'without a ResizeObserver, as measured when it appears', observes: false },
  ])('publishes the check-code notice block size while it shows $environment, and 0px once it is gone', async ({
    observes,
  }) => {
    RecordingResizeObserver.instances.length = 0;
    if (observes) {
      vi.stubGlobal('ResizeObserver', RecordingResizeObserver);
    }
    let noticeHeight = NOTICE_HEIGHT;
    layOutStickyParts(() => HEADER_HEIGHT, () => noticeHeight);
    // Last Name's first FieldBefore fails, which shows the notice; every later event is
    // applied, and Last Name's next FieldBefore resolves the failure, which removes it.
    let fieldEvents = 0;
    const { definition } = await renderForm(createFormDefinitionFixture(), {
      route: (call) => {
        if (call.method !== 'POST' || !call.url.pathname.endsWith('/checkcode/field-event')) {
          return null;
        }
        fieldEvents += 1;
        return fieldEvents === 1 ? jsonResponse(500, UNROUTED_PROBLEM) : jsonResponse(200, NO_EFFECTS);
      },
    });
    const header = present(
      screen.getByRole('toolbar', { name: 'Form actions' }).parentElement,
      'the container of the form actions',
    );
    const formView = present(header.parentElement, 'the form view');
    const noticeSize = (): string => formView.style.getPropertyValue('--form-view-notice-block-size');
    const shownNotice = (): HTMLElement | undefined =>
      screen.queryAllByRole('status').find((status) => (status.textContent ?? '').startsWith('Check code could not run'));
    expect(noticeSize()).toBe('0px');

    const lastName = controlOf(definition, 'LastName');
    act(() => {
      lastName.focus();
    });
    const notice = await waitFor(() => present(shownNotice(), 'the check-code notice'));
    expect(notice.parentElement).toBe(formView);
    expect(noticeSize()).toBe(`${NOTICE_HEIGHT}px`);

    const noticeObservers = RecordingResizeObserver.instances.filter((instance) => instance.observed.includes(notice));
    expect(noticeObservers).toHaveLength(observes ? 1 : 0);
    if (observes) {
      const [observer] = noticeObservers;
      expect(observer.observed).toEqual([notice]);
      // Its text wraps onto a second line.
      noticeHeight = 2 * NOTICE_HEIGHT;
      act(() => {
        observer.resize();
      });
      expect(noticeSize()).toBe(`${2 * NOTICE_HEIGHT}px`);
    }

    act(() => {
      lastName.blur();
    });
    act(() => {
      lastName.focus();
    });
    await waitFor(() => {
      expect(shownNotice()).toBeUndefined();
    });
    expect(noticeSize()).toBe('0px');
    expect(noticeObservers.every((observer) => observer.disconnected)).toBe(true);
    // The notice took no focus on its way in or out.
    expect(document.activeElement).toBe(lastName);
    await flush();
  });

  // The notice of a field's own failed event appears after focus has scrolled the field into
  // view, and pushes the strip down over it. styles.css makes the field's margin the header,
  // the notice, the strip and the ring room, which here Last Name gets from the published
  // notice size: 188px before the notice and 246px with it. A field left inside the deeper
  // margin is scrolled back below it as the notice's size is taken, on the observer's report
  // where there is one; a field clear of it, or one the user has scrolled above the viewport,
  // stays.
  it.each<{ environment: string; observes: boolean; top: number; scrolledBy: number | null }>([
    { environment: 'without a ResizeObserver', observes: false, top: 200, scrolledBy: 200 - 246 },
    { environment: 'with a ResizeObserver', observes: true, top: 200, scrolledBy: 200 - 246 },
    { environment: 'without a ResizeObserver', observes: false, top: 246, scrolledBy: null },
    { environment: 'without a ResizeObserver', observes: false, top: -400, scrolledBy: null },
  ])('scrolls the focused field at $top px by $scrolledBy px (null: not at all) when a check-code notice appears $environment', async ({
    observes,
    top,
    scrolledBy,
  }) => {
    RecordingResizeObserver.instances.length = 0;
    if (observes) {
      vi.stubGlobal('ResizeObserver', RecordingResizeObserver);
    }
    const { definition } = await renderForm(createFormDefinitionFixture(), {
      route: (call) =>
        call.method === 'POST' && call.url.pathname.endsWith('/checkcode/field-event')
          ? jsonResponse(500, UNROUTED_PROBLEM)
          : null,
    });
    const lastName = controlOf(definition, 'LastName');
    const formView = present(lastName.closest<HTMLElement>('.form-view'), 'the form view');
    const realComputedStyle = window.getComputedStyle.bind(window);
    vi.spyOn(window, 'getComputedStyle').mockImplementation((element, pseudoElement) => {
      const style = realComputedStyle(element, pseudoElement);
      if (element === lastName) {
        const notice = Number.parseFloat(formView.style.getPropertyValue('--form-view-notice-block-size') || '0');
        Object.defineProperty(style, 'scrollMarginBlockStart', { value: `${TABS_BOTTOM + notice + 4}px` });
      }
      return style;
    });
    // Focus finds Last Name clear of its 188px margin; then it stands at `top`.
    let lastNameTop = 200;
    vi.spyOn(Element.prototype, 'getBoundingClientRect').mockImplementation(function (this: Element) {
      if (this === lastName) {
        return new DOMRect(0, lastNameTop, 600, 44);
      }
      return this.classList.contains('banner--sticky') ? new DOMRect(0, HEADER_HEIGHT, 1000, NOTICE_HEIGHT) : new DOMRect();
    });
    vi.spyOn(Element.prototype, 'clientHeight', 'get').mockReturnValue(800);
    const scrollBy = vi.spyOn(window, 'scrollBy').mockImplementation(() => undefined);

    act(() => {
      lastName.focus();
    });
    expect(scrollBy).not.toHaveBeenCalled();
    lastNameTop = top;
    const notice = await waitFor(() =>
      present(
        screen.queryAllByRole('status').find((status) => (status.textContent ?? '').startsWith('Check code could not run')),
        'the check-code notice',
      ),
    );
    expect(formView.style.getPropertyValue('--form-view-notice-block-size')).toBe(`${NOTICE_HEIGHT}px`);
    if (observes) {
      // Published at once, but focus waits for the observer's report, after layout.
      expect(scrollBy).not.toHaveBeenCalled();
      const [observer] = RecordingResizeObserver.instances.filter((instance) => instance.observed.includes(notice));
      act(() => {
        present(observer, 'the observer of the notice').resize();
      });
    }

    expect(scrollBy.mock.calls).toEqual(scrolledBy === null ? [] : [[0, scrolledBy]]);
    expect(document.activeElement).toBe(lastName);
    await flush();
  });

  // styles.css gives the controls below the sticky parts a scroll margin; here Last Name gets
  // `margin`. Focus that leaves the control starting inside it, as a date input's inner fields
  // can, scrolls the control to just below it. A control clear of its margin, one with none
  // (a header control) and one taller than the 800px viewport's room below it stay.
  it.each<{ label: string; margin: string; top: number; height: number; scrolledBy: number | null }>([
    { label: 'starting inside its margin', margin: '188px', top: 50, height: 44, scrolledBy: 50 - 188 },
    { label: 'starting above the viewport', margin: '188px', top: -400, height: 44, scrolledBy: -400 - 188 },
    { label: 'clear of its margin', margin: '188px', top: 188, height: 44, scrolledBy: null },
    { label: 'with no margin', margin: '0px', top: -20, height: 44, scrolledBy: null },
    { label: 'taller than the room below its margin', margin: '188px', top: 50, height: 700, scrolledBy: null },
  ])('scrolls a focused control $label by $scrolledBy px (null: not at all)', async ({
    margin,
    top,
    height,
    scrolledBy,
  }) => {
    // Focus raises FieldBefore, which the fake API answers with no effects.
    const { definition } = await renderForm(createFormDefinitionFixture(), { answersFieldEvents: true });
    const control = controlOf(definition, 'LastName');
    const realComputedStyle = window.getComputedStyle.bind(window);
    vi.spyOn(window, 'getComputedStyle').mockImplementation((element, pseudoElement) => {
      const style = realComputedStyle(element, pseudoElement);
      if (element === control) {
        Object.defineProperty(style, 'scrollMarginBlockStart', { value: margin });
      }
      return style;
    });
    vi.spyOn(Element.prototype, 'getBoundingClientRect').mockImplementation(function (this: Element) {
      return this === control ? new DOMRect(0, top, 600, height) : new DOMRect();
    });
    vi.spyOn(Element.prototype, 'clientHeight', 'get').mockReturnValue(800);
    const scrollBy = vi.spyOn(window, 'scrollBy').mockImplementation(() => undefined);

    act(() => {
      control.focus();
    });

    expect(document.activeElement).toBe(control);
    expect(scrollBy.mock.calls).toEqual(scrolledBy === null ? [] : [[0, scrolledBy]]);
    await flush();
  });

  // A sticky strip sticks 140px down, so its stuck bottom edge is at 184px. A panel starting
  // above that edge is scrolled back to start right at it: under the stuck strip, or with the
  // strip carried off-screen by a newly shown panel shorter than the old one, whose rect then
  // says nothing about where the strip sticks. A panel starting at the edge, as at the top
  // of the page, stays, and so does any panel where the strip is not sticky.
  it.each<{
    how: 'click' | 'ArrowRight';
    position: 'sticky' | 'static';
    tablistTop: number;
    panelTop: number;
    scrolledBy: number | null;
  }>([
    { how: 'click', position: 'sticky', tablistTop: HEADER_HEIGHT, panelTop: -300, scrolledBy: -300 - TABS_BOTTOM },
    { how: 'ArrowRight', position: 'sticky', tablistTop: HEADER_HEIGHT, panelTop: -300, scrolledBy: -300 - TABS_BOTTOM },
    { how: 'click', position: 'sticky', tablistTop: -1376, panelTop: -3146, scrolledBy: -3146 - TABS_BOTTOM },
    { how: 'click', position: 'sticky', tablistTop: 282, panelTop: 326, scrolledBy: null },
    { how: 'click', position: 'sticky', tablistTop: HEADER_HEIGHT, panelTop: TABS_BOTTOM, scrolledBy: null },
    { how: 'click', position: 'static', tablistTop: -344, panelTop: -300, scrolledBy: null },
  ])(
    'selecting a page by $how with a $position strip at $tablistTop px and the panel at $panelTop px scrolls by $scrolledBy px',
    async ({ how, position, tablistTop, panelTop, scrolledBy }) => {
      vi.spyOn(Element.prototype, 'getBoundingClientRect').mockImplementation(function (this: Element) {
        switch (this.getAttribute('role')) {
          case 'tablist':
            return new DOMRect(0, tablistTop, 1000, TABS_HEIGHT);
          case 'tabpanel':
            return new DOMRect(0, panelTop, 1000, 1800);
          default:
            return new DOMRect();
        }
      });
      // styles.css, which jsdom does not load, sets the strip's sticky offset to the header's height.
      const realComputedStyle = window.getComputedStyle.bind(window);
      vi.spyOn(window, 'getComputedStyle').mockImplementation((element, pseudoElement) => {
        const style = realComputedStyle(element, pseudoElement);
        if (element.getAttribute('role') === 'tablist') {
          Object.defineProperty(style, 'position', { value: position });
          Object.defineProperty(style, 'top', { value: position === 'sticky' ? `${HEADER_HEIGHT}px` : 'auto' });
        }
        return style;
      });
      const scrollBy = vi.spyOn(window, 'scrollBy').mockImplementation(() => undefined);
      const { definition } = await renderForm();
      const [firstPage, secondPage] = [...definition.pages].sort((a, b) => a.position - b.position);
      expect(scrollBy).not.toHaveBeenCalled();

      if (how === 'click') {
        showPage(secondPage);
      } else {
        fireEvent.keyDown(screen.getByRole('tab', { name: firstPage.name }), { key: 'ArrowRight' });
      }

      expect(screen.getByRole('tab', { name: secondPage.name }).getAttribute('aria-selected')).toBe('true');
      expect(scrollBy.mock.calls).toEqual(scrolledBy === null ? [] : [[0, scrolledBy]]);
    },
  );
});

describe('FormView malformed form metadata', () => {
  // An option value and a bound hold only with the published arm their field's type takes: a
  // 0-based int32 index on an option field and the label text on a legal-values field, a finite
  // number on a number field and a YYYY-MM-DD calendar date on a date field, `null` when unset.
  // Options are a list, possibly empty, on an option or legal-values field and `null` on a
  // field of every other published type. A check-code diagnostic's line is 1-based in the
  // program text. A definition departing from that is a failed load, never a form whose radio
  // group or list silently substitutes a choice or offers none, or whose range check drops a
  // bound.

  type OptionValue = NonNullable<FieldDefinition['options']>[number]['value'];
  type Bound = FieldDefinition['validation']['lower'];

  /** The fixture with the `options` of `name` replaced by `options`. */
  function withOptions(name: string, options: FieldDefinition['options']): FormDefinition {
    return withField(name, (field) => ({ ...field, options }));
  }

  /** The fixture with the first option value of `name` replaced by `value`, as a response may carry it. */
  function withFirstOptionValue(name: string, value: unknown): FormDefinition {
    return withField(name, (field) => ({
      ...field,
      options: present(field.options, `options of ${name}`).map((option, index) =>
        index === 0 ? { ...option, value: value as OptionValue } : option,
      ),
    }));
  }

  /** The fixture with the `bound` of `name` replaced by `value`, as a response may carry it. */
  function withBound(name: string, bound: 'lower' | 'upper', value: unknown): FormDefinition {
    return withField(name, (field) => ({ ...field, validation: { ...field.validation, [bound]: value as Bound } }));
  }

  /** The fixture with one unsupported-command diagnostic on `line`, as a response may carry it. */
  function withDiagnosticOn(line: number): FormDefinition {
    const fixture = createFormDefinitionFixture();
    return {
      ...fixture,
      unsupportedCheckCode: [{ severity: 'warning', form: fixture.name, line, command: 'GOTO', message: 'Not supported.' }],
    };
  }

  it.each<{ label: string; definition: () => FormDefinition }>([
    { label: 'a boolean option value', definition: () => withFirstOptionValue('IllnessSeverity', true) },
    { label: 'a null option value', definition: () => withFirstOptionValue('IllnessSeverity', null) },
    { label: 'a negative option index', definition: () => withFirstOptionValue('IllnessSeverity', -1) },
    { label: 'a fractional option index', definition: () => withFirstOptionValue('IllnessSeverity', 1.5) },
    { label: 'an option index beyond int32', definition: () => withFirstOptionValue('IllnessSeverity', 2 ** 31) },
    { label: 'a text value on an option field', definition: () => withFirstOptionValue('IllnessSeverity', 'Mild') },
    { label: 'an index on a legal-values field', definition: () => withFirstOptionValue('Sex', 0) },
    { label: 'an options list on a text field', definition: () => withOptions('LastName', [{ value: 'M-Male', label: 'M-Male' }]) },
    { label: 'an empty options list on a text field', definition: () => withOptions('LastName', []) },
    { label: 'null options on an option field', definition: () => withOptions('IllnessSeverity', null) },
    { label: 'null options on a legal-values field', definition: () => withOptions('Sex', null) },
    { label: 'a boolean number bound', definition: () => withBound('Age', 'lower', true) },
    { label: 'a date as a number bound', definition: () => withBound('Age', 'upper', '2011-01-15') },
    { label: 'a number as a date bound', definition: () => withBound('DateofInterview', 'lower', 20110115) },
    { label: 'an impossible date bound', definition: () => withBound('DateofInterview', 'upper', '2011-13-45') },
    { label: '29 February of a common year as a date bound', definition: () => withBound('OnsetDate', 'lower', '2011-02-29') },
    ...[0, -1, 1.5].map((line) => ({ label: `a diagnostic on line ${line}`, definition: () => withDiagnosticOn(line) })),
  ])('reports a definition with $label as a failed load and renders none of its fields', async ({ definition }) => {
    const malformed = definition();
    const { container } = mountForm(malformed);

    const banner = await screen.findByRole('alert');
    expect(within(banner).getByText('An unexpected error occurred.')).toBeTruthy();
    expect(within(banner).getByRole('button', { name: 'Retry' })).toBeTruthy();
    expect(screen.getByRole('status').textContent).toBe('The form could not be loaded.');
    expect(screen.queryByRole('heading', { name: malformed.name })).toBeNull();
    expect(screen.queryByRole('tablist')).toBeNull();
    expect(container.querySelectorAll(NATIVE_CONTROLS)).toHaveLength(0);
    // The definition, and the reads sent beside it: N, the first record and the grid's first page.
    expect(calls.every((call) => call.method === 'GET')).toBe(true);
    expect(calls.map((call) => call.url.pathname).sort()).toEqual(
      [FORM_PATH, `${FORM_PATH}/records`, `${FORM_PATH}/records/at/1`, `${FORM_PATH}/records/count`].sort(),
    );
  });

  it.each<{ label: string; field: string; definition: () => FormDefinition }>([
    { label: 'the unmodified fixture', field: 'IllnessSeverity', definition: createFormDefinitionFixture },
    { label: 'an empty options list on a legal-values field', field: 'Sex', definition: () => withOptions('Sex', []) },
    { label: 'a fractional negative number bound', field: 'Age', definition: () => withBound('Age', 'lower', -0.5) },
    { label: 'a leap day as a date bound', field: 'OnsetDate', definition: () => withBound('OnsetDate', 'upper', '2012-02-29') },
    { label: 'a diagnostic on line 1', field: 'LastName', definition: () => withDiagnosticOn(1) },
  ])('loads $label with every field rendered', async ({ field, definition }) => {
    const { container, definition: rendered } = await renderForm(definition());

    expect(container.querySelectorAll('[data-field-name]')).toHaveLength(allFields(rendered).length);
    expect(wrapperOf(container, field).querySelectorAll(NATIVE_CONTROLS).length).toBeGreaterThan(0);
  });
});

describe('FormView connection banners', () => {
  // These cases answer the writes they make, so they route their own requests. Reads fall
  // through to the read routes above, and any other request still fails the test.

  /** The answer a case gives one request instead of the read routes; `undefined` leaves it to them. */
  type Answer = Response | 'no response' | undefined;

  const RECORD_PATH = `${FORM_PATH}/records/${RECORD.globalRecordId}`;
  const TRACE_ID = '00-test-unavailable-00';
  const UNAVAILABLE_PROBLEM = {
    type: 'https://tools.ietf.org/html/rfc9110#section-15.6.4',
    title: 'Service Unavailable',
    status: 503,
    detail: 'The database is unavailable.',
    traceId: TRACE_ID,
  } as const satisfies ProblemDetails;

  /** A 503, carrying `writeOutcome` as a record write's does when one is given. */
  function unavailable(writeOutcome?: WriteOutcome): Response {
    if (writeOutcome === undefined) {
      return problemResponse(UNAVAILABLE_PROBLEM);
    }
    const body: WriteProblemDetails = { ...UNAVAILABLE_PROBLEM, writeOutcome };
    return problemResponse(body);
  }

  /** A fetch that answers through `answer` first, then the read routes, recording every request. */
  function answeringApi(definition: FormDefinition, answer: (call: Call) => Answer): typeof fetch {
    return async (input, init) => {
      const request = input instanceof Request ? input : new Request(input, init);
      const url = new URL(request.url);
      const text = await request.text();
      const call: Call = { method: request.method, url, body: text === '' ? null : text };
      calls.push(call);

      const answered = answer(call);
      if (answered === 'no response') {
        // What fetch does when no response arrives.
        throw new TypeError('Failed to fetch');
      }
      if (answered !== undefined) {
        return answered;
      }
      const body = readRouteBody(call.method, url.pathname, definition, [RECORD]);
      if (body === null) {
        unrouted.push(requestLine(call));
        return jsonResponse(500, UNROUTED_PROBLEM);
      }
      return jsonResponse(200, body);
    };
  }

  function renderWith(answer: (call: Call) => Answer): void {
    const definition = createFormDefinitionFixture();
    const client = createApiClient({ baseUrl: 'http://localhost', fetch: answeringApi(definition, answer) });
    render(<FormView viewId={definition.viewId} client={client} onExit={vi.fn()} onFormNotFound={vi.fn()} />);
  }

  async function loaded(): Promise<void> {
    await within(await screen.findByRole('toolbar', { name: 'Record navigation' })).findByText('1 of 1');
  }

  function linesOf(method: string): string[] {
    return calls.filter((call) => call.method === method).map(requestLine);
  }

  function formAction(name: string): HTMLElement {
    return within(screen.getByRole('toolbar', { name: 'Form actions' })).getByRole('button', { name });
  }

  it.each<{ label: string; failure: () => Answer; text: string }>([
    { label: 'a 503', failure: () => unavailable(), text: 'The database cannot be reached.' },
    { label: 'no response', failure: () => 'no response', text: 'The server could not be reached.' },
  ])('says only what cannot be reached, with Retry, when the form definition gets $label', async ({
    failure,
    text,
  }) => {
    let failed = false;
    renderWith((call) => {
      if (!failed && call.method === 'GET' && call.url.pathname === FORM_PATH) {
        failed = true;
        return failure();
      }
      return undefined;
    });

    const alert = await screen.findByRole('alert');
    // Nothing is open yet, so there is no draft to speak of.
    expect(within(alert).getByText(text)).toBeTruthy();
    expect(alert.textContent).not.toContain('still on screen');

    fireEvent.click(within(alert).getByRole('button', { name: 'Retry' }));

    await loaded();
    expect(linesOf('GET').filter((line) => line === `GET ${FORM_PATH}`)).toHaveLength(2);
    expect(screen.queryByRole('alert')).toBeNull();
  });

  // The navigator's count and the grid's list page are reads, so their connection banner
  // holds no draft and its Retry repeats the read.
  it.each<{ label: string; read: string; path: string; failure: () => Answer; text: string }>([
    {
      label: 'a 503',
      read: 'the record count',
      path: `${FORM_PATH}/records/count`,
      failure: () => unavailable(),
      text: 'The database cannot be reached.',
    },
    {
      label: 'no response',
      read: 'the record count',
      path: `${FORM_PATH}/records/count`,
      failure: () => 'no response',
      text: 'The server could not be reached.',
    },
    {
      label: 'a 503',
      read: 'the record list',
      path: `${FORM_PATH}/records`,
      failure: () => unavailable(),
      text: 'The database cannot be reached.',
    },
    {
      label: 'no response',
      read: 'the record list',
      path: `${FORM_PATH}/records`,
      failure: () => 'no response',
      text: 'The server could not be reached.',
    },
  ])('says only what cannot be reached, with Retry, when $read gets $label', async ({ path, failure, text }) => {
    let failed = false;
    renderWith((call) => {
      if (!failed && call.method === 'GET' && call.url.pathname === path) {
        failed = true;
        return failure();
      }
      return undefined;
    });

    const alert = await screen.findByRole('alert');
    expect(within(alert).getByText(text)).toBeTruthy();
    expect(alert.textContent).not.toContain('still on screen');

    fireEvent.click(within(alert).getByRole('button', { name: 'Retry' }));

    await loaded();
    await within(screen.getByRole('region', { name: 'Records' })).findByText('1 record');
    expect(calls.filter((call) => call.method === 'GET' && call.url.pathname === path)).toHaveLength(2);
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it.each<{
    label: string;
    create: boolean;
    failure: () => Answer;
    saveAgainName: string;
    duplicate: boolean;
    /** What failed, which the banner names first. */
    cause: string;
  }>([
    {
      label: 'a create that got no response',
      create: true,
      failure: () => 'no response',
      saveAgainName: 'Save again (this may create a duplicate record)',
      duplicate: true,
      cause: 'The server could not be reached.',
    },
    {
      label: 'an update answered 503 with writeOutcome unknown',
      create: false,
      failure: () => unavailable('unknown'),
      saveAgainName: 'Save again (the earlier save may already have been applied)',
      duplicate: false,
      cause: 'The database cannot be reached.',
    },
  ])('shows the risk before an explicit Save again, and no Retry, for $label', async ({
    create,
    failure,
    saveAgainName,
    duplicate,
    cause,
  }) => {
    const method = create ? 'POST' : 'PUT';
    const path = create ? `${FORM_PATH}/records` : RECORD_PATH;
    renderWith((call) => (call.method === method && call.url.pathname === path ? failure() : undefined));
    await loaded();
    const navigator = screen.getByRole('toolbar', { name: 'Record navigation' });
    if (create) {
      fireEvent.click(within(navigator).getByRole('button', { name: 'New' }));
      await within(navigator).findByText('New record');
    }

    fireEvent.change(screen.getByLabelText('Last Name'), { target: { value: 'Byron' } });
    fireEvent.click(formAction('Save'));

    const alert = await screen.findByRole('alert');
    // Visible text, not only an accessible name: the banner says what the save may have done.
    const text = alert.textContent ?? '';
    expect(text.startsWith(`${cause} `)).toBe(true);
    expect(text).toContain('may already have been applied');
    expect(text).toContain('cannot prove');
    expect(text).toContain('Your changes are still on screen.');
    if (duplicate) {
      expect(text).toContain('Saving again may create a duplicate record.');
    } else {
      expect(text).not.toContain('duplicate');
    }
    const saveAgain = within(alert).getByRole('button', { name: saveAgainName });
    expect(saveAgain.textContent).toBe('Save again');
    expect(within(alert).queryByRole('button', { name: 'Retry' })).toBeNull();
    expect(within(alert).getByRole('button', { name: 'Dismiss' })).toBeTruthy();
    // Nothing was resent without the user's explicit action.
    expect(linesOf(method)).toEqual([`${method} ${path}`]);
  });

  it('shows the latest trace id, and still only Save again, when Save again of an uncertain update gets a 503 notWritten', async () => {
    const firstTrace = '00-test-written-00';
    const written: WriteProblemDetails = {
      type: 'https://tools.ietf.org/html/rfc9110#section-15.6.1',
      title: 'An error occurred while processing your request.',
      status: 500,
      detail: 'An unexpected error occurred.',
      traceId: firstTrace,
      writeOutcome: 'written',
    };
    const updates = [problemResponse(written), unavailable('notWritten')];
    renderWith((call) => (call.method === 'PUT' && call.url.pathname === RECORD_PATH ? updates.shift() : undefined));
    await loaded();
    const saveAgainName = 'Save again (the earlier save may already have been applied)';

    fireEvent.change(screen.getByLabelText('Last Name'), { target: { value: 'Byron' } });
    fireEvent.click(formAction('Save'));
    const first = await findAlertContaining(`Trace id: ${firstTrace}`);
    fireEvent.click(within(first).getByRole('button', { name: saveAgainName }));

    // The 503 replaced the 500's banner: its own cause and trace id, the first save still in doubt.
    const latest = await findAlertContaining(`Trace id: ${TRACE_ID}`);
    expect(latest.textContent?.startsWith('The database cannot be reached. ')).toBe(true);
    expect(latest.textContent).toContain('may already have been applied');
    expect(latest.textContent).not.toContain(firstTrace);
    expect(within(latest).getByRole('button', { name: saveAgainName })).toBeTruthy();
    expect(within(latest).queryByRole('button', { name: 'Retry' })).toBeNull();
    expect(linesOf('PUT')).toEqual([`PUT ${RECORD_PATH}`, `PUT ${RECORD_PATH}`]);
    expect(updates).toEqual([]);
  });

  it.each<{ label: string; failure: () => Answer; text: string; traced: boolean }>([
    {
      label: 'a 503 with writeOutcome notWritten',
      failure: () => unavailable('notWritten'),
      text: 'The delete could not be confirmed: the database cannot be reached.',
      traced: true,
    },
    {
      label: 'a 503 without writeOutcome',
      failure: () => unavailable(),
      text: 'The delete could not be confirmed: the database cannot be reached.',
      traced: true,
    },
    {
      label: 'no response',
      failure: () => 'no response',
      text: 'The delete could not be confirmed: the server could not be reached.',
      traced: false,
    },
  ])('shows the connection banner with Retry when a confirmed delete gets $label', async ({
    failure,
    text,
    traced,
  }) => {
    let deletes = 0;
    renderWith((call) => {
      if (call.method !== 'DELETE' || call.url.pathname !== RECORD_PATH) {
        return undefined;
      }
      deletes += 1;
      return deletes === 1 ? failure() : new Response(null, { status: 204 });
    });
    await loaded();

    fireEvent.click(formAction('Delete'));
    const dialog = await screen.findByRole('dialog', { name: 'Delete record' });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Delete record' }));

    const alert = await screen.findByRole('alert');
    // The confirmation discarded the draft, so the banner does not speak of one.
    expect(within(alert).getByText(text)).toBeTruthy();
    expect(alert.textContent).not.toContain('still on screen');
    // The server's trace id, on a line of its own, whenever a response carried one.
    expect(within(alert).queryByText(`Trace id: ${TRACE_ID}`) !== null).toBe(traced);
    expect(within(alert).getByRole('button', { name: 'Dismiss' })).toBeTruthy();
    expect(within(alert).queryByRole('button', { name: /Save again/ })).toBeNull();
    expect(linesOf('DELETE')).toEqual([`DELETE ${RECORD_PATH}`]);

    fireEvent.click(within(alert).getByRole('button', { name: 'Retry' }));

    // The resent DELETE succeeds, and the navigator opens the nearest record, the first again.
    await waitFor(() => {
      expect(linesOf('GET').filter((line) => line.startsWith(`GET ${FORM_PATH}/records/at/1`))).toHaveLength(2);
    });
    await loaded();
    expect(screen.queryByRole('alert')).toBeNull();
    expect(linesOf('DELETE')).toEqual([`DELETE ${RECORD_PATH}`, `DELETE ${RECORD_PATH}`]);
    expect(linesOf('PUT')).toEqual([]);
  });
});

describe('FormView Delete of a new record', () => {
  // A new draft with no entry holds nothing to delete or discard, so Delete is disabled until
  // something is entered. A draft with an entry asks first, and Confirm discards it with no
  // request (0.3.6). Confirm leaves Delete disabled on the empty draft, so the heading takes
  // the focus; Cancel keeps the draft, and the focus returns to Delete.
  it.each<{ label: string; choice: string; lastName: string; focus: 'heading' | 'Delete' }>([
    { label: 'Confirm discards it and focuses the heading', choice: 'Delete record', lastName: '', focus: 'heading' },
    { label: 'Cancel keeps it and focuses Delete', choice: 'Cancel', lastName: EDITED_LAST_NAME, focus: 'Delete' },
  ])('disables Delete on an empty new record; on a typed one, $label', async ({ choice, lastName, focus }) => {
    const { definition } = await renderForm();
    fireEvent.click(navigatorButton('New'));
    await waitFor(() => {
      expect(navigatorStatus()).toBe('New record');
    });
    const remove = formActionButton('Delete');
    expect(isDisabled(remove)).toBe(true);

    edit(definition, 'LastName', EDITED_LAST_NAME);
    expect(isDisabled(remove)).toBe(false);

    remove.focus();
    fireEvent.click(remove);
    const dialog = await screen.findByRole('dialog', { name: 'Delete record' });
    expect(dialog.textContent).toContain('never been saved');
    fireEvent.click(within(dialog).getByRole('button', { name: choice }));

    await waitFor(() => {
      expect(screen.queryByRole('dialog')).toBeNull();
    });
    expect(controlValue(controlOf(definition, 'LastName'))).toBe(lastName);
    expect(isDisabled(remove)).toBe(lastName === '');
    expect(document.activeElement).toBe(
      focus === 'heading' ? screen.getByRole('heading', { level: 2, name: definition.name }) : remove,
    );
    expect(navigatorStatus()).toBe('New record');
    expect(writeCalls()).toEqual([]);
  });
});

describe('FormView while a save may already have been applied', () => {
  // A 500 or 503 whose writeOutcome is unknown leaves the save in doubt, and only Save again
  // may send the draft (0.3.4.2). Save, Back to forms and the navigator's moves, each of which
  // would first save the dirty draft, stay operable but are marked unavailable, and a press
  // shows the banner again as a new alert, announced again. Save again takes the warning tone.
  const SAVE_AGAIN_NAME = 'Save again (the earlier save may already have been applied)';

  function unknownOutcome(status: 500 | 503): WriteProblemDetails {
    return {
      type: `https://tools.ietf.org/html/rfc9110#section-15.6.${status === 503 ? 4 : 1}`,
      title: status === 503 ? 'Service Unavailable' : 'An error occurred while processing your request.',
      status,
      detail: 'The test server lost track of this write.',
      traceId: '00-test-unknown-outcome-00',
      writeOutcome: 'unknown',
    };
  }

  /** Back to forms, Save, and the navigator's moves enabled at record 1 of 2: all but Previous. */
  function refusingButtons(): HTMLElement[] {
    return [
      formActionButton('Back to forms'),
      formActionButton('Save'),
      ...['First', 'Next', 'Last', 'Go', 'New'].map(navigatorButton),
    ];
  }

  function bannerShown(): HTMLElement | undefined {
    return screen
      .queryAllByRole('alert')
      .find((alert) => (alert.textContent ?? '').includes('may already have been applied'));
  }

  it.each<{ label: string; status: 500 | 503 }>([
    { label: 'a 503', status: 503 },
    { label: 'a 500', status: 500 },
  ])('marks the refused controls, and shows the banner again on each press, after $label with writeOutcome unknown', async ({
    status,
  }) => {
    const { definition, onExit } = await renderForm(createFormDefinitionFixture(), { records: [RECORD_A, RECORD_B] });
    edit(definition, 'LastName', EDITED_LAST_NAME);
    replyToWrite({ kind: 'problem', problem: unknownOutcome(status) });
    fireEvent.click(formActionButton('Save'));
    let banner = await waitFor(() => present(bannerShown(), 'the banner of the uncertain save'));
    await flush();

    // The risky resend is told apart from Dismiss beside it.
    expect(within(banner).getByRole('button', { name: SAVE_AGAIN_NAME }).classList.contains('button--warning')).toBe(true);
    expect(within(banner).getByRole('button', { name: 'Dismiss' }).classList.contains('button--warning')).toBe(false);
    for (const button of refusingButtons()) {
      expect([button.textContent, button.getAttribute('aria-disabled'), button.className]).toEqual([
        button.textContent,
        'true',
        'button--blocked',
      ]);
      expect(isDisabled(button)).toBe(false);
    }
    // A disabled control already says so, and the actions that save nothing are not refused.
    expect(isDisabled(navigatorButton('Previous'))).toBe(true);
    for (const button of [navigatorButton('Previous'), formActionButton('Delete'), formActionButton('Find')]) {
      expect(button.hasAttribute('aria-disabled')).toBe(false);
    }

    // Go needs a record number to try the move at all.
    fireEvent.change(screen.getByRole('textbox', { name: 'Go to record' }), { target: { value: '2' } });
    for (const button of refusingButtons()) {
      const before = banner;
      fireEvent.click(button);
      banner = await waitFor(() => {
        const shown = present(bannerShown(), `the banner shown again by ${button.textContent ?? ''}`);
        expect(shown).not.toBe(before);
        return shown;
      });
      expect(before.isConnected).toBe(false);
    }
    await flush();
    expect(writeCalls().map(requestLine)).toEqual([`PUT ${FORM_PATH}/records/${RECORD_A.globalRecordId}`]);
    expect(onExit).not.toHaveBeenCalled();
    expect(navigatorStatus()).toBe('1 of 2');
    expect(controlValue(controlOf(definition, 'LastName'))).toBe(EDITED_LAST_NAME);

    // Save again sends the draft, and once it is stored nothing is refused.
    replyToWrite({ kind: 'saved', record: savedWith(RECORD_A, { LastName: EDITED_LAST_NAME }) });
    fireEvent.click(within(banner).getByRole('button', { name: SAVE_AGAIN_NAME }));
    await waitFor(() => {
      expect(bannerShown()).toBeUndefined();
      expect(formActionButton('Save').hasAttribute('aria-disabled')).toBe(false);
    });
    for (const button of refusingButtons()) {
      expect([button.textContent, button.getAttribute('aria-disabled'), button.className]).toEqual([
        button.textContent,
        null,
        '',
      ]);
    }
    expect(writeCalls().map(requestLine)).toEqual([
      `PUT ${FORM_PATH}/records/${RECORD_A.globalRecordId}`,
      `PUT ${FORM_PATH}/records/${RECORD_A.globalRecordId}`,
    ]);
  });
});

describe('FormView definition reload focus', () => {
  // Retry replaces the failure's toolbar and banner with the loading status, so it first
  // moves focus to that status line, which stays one element through loading and failure.
  // A retried load that succeeds hands focus on to the form's heading; nothing else moves it.

  /** How the test answers a definition GET: an HTTP status, or no response at all. */
  type DefinitionAnswer = 200 | 500 | 503 | 'network';

  interface HeldApi {
    readonly fetch: typeof fetch;
    /** Answers the definition GET in flight, or else the next one when it arrives. */
    readonly answer: (answer: DefinitionAnswer) => void;
  }

  /** `fakeApi`, except that each definition GET waits until the test answers it. */
  function heldDefinitionApi(definition: FormDefinition): HeldApi {
    const serve = fakeApi(definition, [RECORD]);
    const formPath = `/api/forms/${String(definition.viewId)}`;
    let held: ((response: Promise<Response>) => void) | null = null;
    let queued: DefinitionAnswer | null = null;

    const respond = (answer: DefinitionAnswer): Promise<Response> => {
      switch (answer) {
        case 200:
          return Promise.resolve(jsonResponse(200, definition));
        case 'network':
          return Promise.reject(new TypeError('fetch failed'));
        default: {
          const problem: ProblemDetails = {
            ...UNROUTED_PROBLEM,
            status: answer,
            detail: 'The test API failed the definition request.',
            traceId: `00-test-${String(answer)}-00`,
          };
          return Promise.resolve(jsonResponse(answer, problem));
        }
      }
    };

    return {
      fetch: (input, init) => {
        const request = input instanceof Request ? input : new Request(input, init);
        const url = new URL(request.url);
        if (request.method !== 'GET' || url.pathname !== formPath) {
          return serve(request);
        }
        calls.push({ method: request.method, url, body: null });
        if (queued !== null) {
          const answer = queued;
          queued = null;
          return respond(answer);
        }
        return new Promise<Response>((resolve) => {
          held = resolve;
        });
      },
      answer: (answer) => {
        if (held === null) {
          queued = answer;
          return;
        }
        const release = held;
        held = null;
        release(respond(answer));
      },
    };
  }

  /** The form view of `definition`, served by `api`. */
  function heldFormView(definition: FormDefinition, api: HeldApi) {
    return (
      <FormView
        viewId={definition.viewId}
        client={createApiClient({ baseUrl: 'http://localhost', fetch: api.fetch })}
        onExit={() => undefined}
        onFormNotFound={() => undefined}
      />
    );
  }

  /** Waits until the workspace has loaded its record and grid, so no request is left in flight. */
  async function workspaceLoaded(): Promise<void> {
    await within(screen.getByRole('toolbar', { name: 'Record navigation' })).findByText('1 of 1');
    await within(screen.getByRole('region', { name: 'Records' })).findByText('1 record');
  }

  it.each<{ label: string; failure: Exclude<DefinitionAnswer, 200> }>([
    { label: 'a 500, under the error banner', failure: 500 },
    { label: 'a 503, under the connection banner', failure: 503 },
    { label: 'no response, under the connection banner', failure: 'network' },
  ])('keeps focus on the status line through Retry after $label, then gives it to the heading', async ({ failure }) => {
    const definition = createFormDefinitionFixture();
    const api = heldDefinitionApi(definition);
    // While `recordCommits` is set, the focused element at the end of each commit. A Profiler's
    // onRender runs in the commit's layout phase, after the form view's layout effects and
    // before any passive effect, which may run only after the browser has painted.
    const focusAtCommits: (Element | null)[] = [];
    let recordCommits = false;
    const recordFocus = (): void => {
      if (recordCommits) {
        focusAtCommits.push(document.activeElement);
      }
    };
    render(
      <Profiler id="form-view" onRender={recordFocus}>
        {heldFormView(definition, api)}
      </Profiler>,
    );
    const status = screen.getByRole('status');
    expect(status.textContent).toBe('Loading form…');

    /** Answers the pending definition GET with the failure, and returns the banner's Retry. */
    const fail = async (): Promise<HTMLElement> => {
      api.answer(failure);
      const banner = await screen.findByRole('alert');
      return within(banner).getByRole('button', { name: 'Retry' });
    };

    // The first load moves no focus, even when it fails.
    let retry = await fail();
    expect(document.activeElement).toBe(document.body);
    expect(screen.getByRole('status')).toBe(status);
    expect(status.textContent).toBe('The form could not be loaded.');
    const formActions = screen.getByRole('toolbar', { name: 'Form actions' });
    expect(within(formActions).getByRole('button', { name: 'Back to forms' })).toBeTruthy();

    // Retry focuses the status line before its button and the toolbar leave the page.
    retry.focus();
    fireEvent.click(retry);
    expect(document.activeElement).toBe(status);
    expect(status.textContent).toBe('Loading form…');
    expect(screen.queryAllByRole('button')).toEqual([]);

    // A repeated failure leaves focus there, one Tab before the new Retry.
    retry = await fail();
    expect(document.activeElement).toBe(status);
    expect(screen.getByRole('status')).toBe(status);
    expect(status.textContent).toBe('The form could not be loaded.');
    const following = screen
      .getAllByRole('button')
      .filter((button) => (status.compareDocumentPosition(button) & Node.DOCUMENT_POSITION_FOLLOWING) !== 0);
    expect(following).toEqual([retry]);

    // A retried load that succeeds moves focus on to the form's heading, where it stays.
    retry.focus();
    fireEvent.click(retry);
    expect(document.activeElement).toBe(status);
    // The heading takes focus in the commit that removes the status line, before the browser
    // could paint it, so focus never rests on the page body: every commit ends with it there.
    recordCommits = true;
    api.answer(200);
    const heading = await screen.findByRole('heading', { level: 2, name: definition.name });
    expect(document.activeElement).toBe(heading);
    await workspaceLoaded();
    expect(document.activeElement).toBe(heading);
    expect(focusAtCommits.length).toBeGreaterThan(0);
    const elsewhere = focusAtCommits.filter((element) => element !== heading);
    expect(elsewhere.map((element) => element?.tagName ?? 'none')).toEqual([]);

    // Each Retry repeated the same GET, and nothing but GETs was sent.
    const formPath = `/api/forms/${String(definition.viewId)}`;
    const definitionGets = calls.filter((call) => call.url.pathname === formPath).map(requestLine);
    expect(definitionGets).toEqual([`GET ${formPath}`, `GET ${formPath}`, `GET ${formPath}`]);
    expect(calls.filter((call) => call.method !== 'GET').map(requestLine)).toEqual([]);
  });

  it('moves no focus when the first load succeeds', async () => {
    await renderForm();

    expect(document.activeElement).toBe(document.body);
  });

  it('leaves focus where the user moved it while a retried load ran', async () => {
    const definition = createFormDefinitionFixture();
    const api = heldDefinitionApi(definition);
    render(
      <>
        <button type="button">Elsewhere</button>
        {heldFormView(definition, api)}
      </>,
    );
    api.answer(500);
    const retry = within(await screen.findByRole('alert')).getByRole('button', { name: 'Retry' });

    retry.focus();
    fireEvent.click(retry);
    const elsewhere = screen.getByRole('button', { name: 'Elsewhere' });
    elsewhere.focus();
    api.answer(200);
    await screen.findByRole('heading', { level: 2, name: definition.name });
    await workspaceLoaded();

    expect(document.activeElement).toBe(elsewhere);
  });
});

describe('FormView form open', () => {
  // Navigation needs no definition, so a form open sends the definition, N, the first record
  // and the grid's first page together, and the grid takes the page sent beside the
  // definition rather than asking again.

  /** The four reads of a form open, by path. */
  function formOpenPaths(definition: FormDefinition): string[] {
    const form = `/api/forms/${String(definition.viewId)}`;
    return [form, `${form}/records`, `${form}/records/at/1`, `${form}/records/count`].sort();
  }

  function sentPaths(): string[] {
    return calls.map((call) => call.url.pathname).sort();
  }

  async function workspaceLoaded(definition: FormDefinition): Promise<void> {
    await screen.findByRole('heading', { level: 2, name: definition.name });
    await within(screen.getByRole('toolbar', { name: 'Record navigation' })).findByText('1 of 1');
    await within(screen.getByRole('region', { name: 'Records' })).findByText('1 record');
  }

  it('sends N, the first record and the grid page while the definition loads, and the grid takes that page', async () => {
    const definition = createFormDefinitionFixture();
    const formPath = `/api/forms/${String(definition.viewId)}`;
    const answer = deferred();
    mountForm(definition, {
      route: (call) => (call.method === 'GET' && call.url.pathname === formPath ? answer.response : null),
    });

    // The definition is unanswered, and the other three reads have been sent.
    await vi.waitFor(() => {
      expect(sentPaths()).toEqual(formOpenPaths(definition));
    });
    expect(screen.getByRole('status').textContent).toBe('Loading form…');

    answer.release(jsonResponse(200, definition));
    await workspaceLoaded(definition);

    // Each read went out once, the grid's first page included, and every one is a GET.
    expect(sentPaths()).toEqual(formOpenPaths(definition));
    expect(calls.filter((call) => call.method !== 'GET').map(requestLine)).toEqual([]);
  });

  it('sends N, the first record and the grid page again with the definition on Retry', async () => {
    const definition = createFormDefinitionFixture();
    const formPath = `/api/forms/${String(definition.viewId)}`;
    const unavailable: ProblemDetails = {
      ...UNROUTED_PROBLEM,
      status: 503,
      detail: 'The test API failed the definition request.',
      traceId: '00-test-503-00',
    };
    let definitionGets = 0;
    mountForm(definition, {
      route: (call) => {
        if (call.method !== 'GET' || call.url.pathname !== formPath) {
          return null;
        }
        definitionGets += 1;
        return definitionGets === 1 ? jsonResponse(503, unavailable) : null;
      },
    });
    const retry = within(await screen.findByRole('alert')).getByRole('button', { name: 'Retry' });
    await vi.waitFor(() => {
      expect(sentPaths()).toEqual(formOpenPaths(definition));
    });

    fireEvent.click(retry);
    await workspaceLoaded(definition);

    expect(sentPaths()).toEqual([...formOpenPaths(definition), ...formOpenPaths(definition)].sort());
    expect(calls.filter((call) => call.method !== 'GET').map(requestLine)).toEqual([]);
  });
});

describe('FormView dialog chunks', () => {
  // Find, the delete confirmation and check-code messages are each a chunk of its own, which
  // the form view fetches as it opens. Each chunk is tracked on its own: one that cannot be
  // fetched holds up no other dialog, is fetched again when its dialog is asked for, and only
  // its failure while its dialog is asked for is shown, with Retry. While a dialog asked for
  // waits for its chunk, a placeholder modal says "Opening…" and holds the page as the dialog
  // will, so a second modal cannot open beside it; Escape or Cancel cancels the open.
  const DIALOG_FAILED = 'The dialog could not be opened. The server could not be reached.';
  const OPENING = 'Opening…';

  type DialogModule = './ConfirmDialog' | './MessageDialog' | './FindRecordDialog';
  const DIALOG_MODULES: readonly DialogModule[] = ['./ConfirmDialog', './MessageDialog', './FindRecordDialog'];

  /** How a case serves one dialog's chunk: each fetch waits for `held`, then fails while `fails` is set. */
  interface ChunkServing {
    held: Promise<void>;
    fails: boolean;
  }

  /** A wait the case ends when it chooses. */
  function hold(): { readonly held: Promise<void>; readonly release: () => void } {
    let release: () => void = () => undefined;
    const held = new Promise<void>((resolve) => {
      release = resolve;
    });
    return { held, release };
  }

  /** Every chunk served at once. */
  function servedAtOnce(): Record<DialogModule, ChunkServing> {
    return {
      './ConfirmDialog': { held: Promise.resolve(), fails: false },
      './MessageDialog': { held: Promise.resolve(), fails: false },
      './FindRecordDialog': { held: Promise.resolve(), fails: false },
    };
  }

  afterEach(() => {
    for (const path of DIALOG_MODULES) {
      vi.doUnmock(path);
    }
    vi.resetModules();
  });

  /**
   * Renders a fresh form view, whose dialogs have not been fetched yet, over the one RECORD,
   * with each dialog's chunk served as `serving` says at the time of each fetch; resolves once
   * the workspace has loaded. `route` answers before the read routes, as in mountForm.
   */
  async function renderFreshForm(
    serving: Readonly<Record<DialogModule, ChunkServing>>,
    route?: Route,
  ): Promise<FormDefinition> {
    vi.resetModules();
    for (const path of DIALOG_MODULES) {
      vi.doMock(path, async (importOriginal) => {
        await serving[path].held;
        if (serving[path].fails) {
          throw new TypeError('Failed to fetch dynamically imported module');
        }
        return importOriginal();
      });
    }
    const { FormView: FreshFormView } = await import('./FormView');
    const definition = createFormDefinitionFixture();
    const client = createApiClient({ baseUrl: 'http://localhost', fetch: fakeApi(definition, [RECORD], { route }) });
    render(<FreshFormView viewId={definition.viewId} client={client} onExit={vi.fn()} onFormNotFound={vi.fn()} />);
    await within(await screen.findByRole('toolbar', { name: 'Record navigation' })).findByText('1 of 1');
    await within(screen.getByRole('region', { name: 'Records' })).findByText('1 record');
    return definition;
  }

  /** The one modal on screen; fails when there is none or more than one. */
  function onlyDialog(): HTMLElement {
    const dialogs = screen.queryAllByRole('dialog');
    expect(dialogs, 'the modals on screen').toHaveLength(1);
    return dialogs[0];
  }

  function nonGetRequests(): string[] {
    return calls.filter((call) => call.method !== 'GET').map(requestLine);
  }

  const DIALOG_OF = { Delete: 'Delete record', Find: 'Find record' } as const;
  // The control each dialog closes with, sending nothing.
  const CLOSE_OF = { Delete: 'Cancel', Find: 'Close' } as const;
  const OTHER_OF = { Delete: 'Find', Find: 'Delete' } as const;

  it.each<{ first: 'Delete' | 'Find'; close: 'Escape while it opens' | 'Cancel while it opens' | 'the dialog once open' }>([
    { first: 'Delete', close: 'Escape while it opens' },
    { first: 'Delete', close: 'Cancel while it opens' },
    { first: 'Delete', close: 'the dialog once open' },
    { first: 'Find', close: 'Escape while it opens' },
    { first: 'Find', close: 'Cancel while it opens' },
    { first: 'Find', close: 'the dialog once open' },
  ])('opens only $first while the chunks are fetched, whatever else is pressed, and closes it with $close', async ({ first, close }) => {
    const chunks = hold();
    const serving = servedAtOnce();
    for (const path of DIALOG_MODULES) {
      serving[path].held = chunks.held;
    }
    await renderFreshForm(serving);
    const opener = formActionButton(first);
    const other = formActionButton(OTHER_OF[first]);

    opener.focus();
    fireEvent.click(opener);

    // The placeholder holds the page at once, with focus on its Cancel.
    const placeholder = onlyDialog();
    expect(placeholder.getAttribute('aria-modal')).toBe('true');
    expect(placeholder.getAttribute('aria-busy')).toBe('true');
    expect(placeholder.textContent).toContain(OPENING);
    const cancel = within(placeholder).getByRole('button', { name: 'Cancel' });
    expect(document.activeElement).toBe(cancel);
    expect(present(opener.closest('[inert]'), 'the inert page behind the placeholder')).toBeTruthy();

    // The other toolbar dialog opens nothing beside it, as the inert page would refuse the press.
    fireEvent.click(other);
    expect(onlyDialog()).toBe(placeholder);

    if (close === 'the dialog once open') {
      chunks.release();
      const dialog = await screen.findByRole('dialog', { name: DIALOG_OF[first] });
      expect(onlyDialog()).toBe(dialog);
      expect(dialog.contains(document.activeElement)).toBe(true);
      fireEvent.click(within(dialog).getByRole('button', { name: CLOSE_OF[first] }));
    } else {
      if (close === 'Escape while it opens') {
        fireEvent.keyDown(cancel, { key: 'Escape' });
      } else {
        fireEvent.click(cancel);
      }
      expect(screen.queryByRole('dialog')).toBeNull();
      chunks.release();
    }
    await flush();

    // Closed, with focus back on the button that opened it, and neither dialog opens later.
    expect(screen.queryAllByRole('dialog')).toEqual([]);
    expect(document.activeElement).toBe(opener);
    expect(document.body.querySelector('[inert]')).toBeNull();
    expect(screen.queryByText(DIALOG_FAILED)).toBeNull();

    // The other dialog's press was refused, not kept: pressed now, it opens, alone.
    fireEvent.click(other);
    const otherDialog = await screen.findByRole('dialog', { name: DIALOG_OF[OTHER_OF[first]] });
    expect(onlyDialog()).toBe(otherDialog);
    fireEvent.click(within(otherDialog).getByRole('button', { name: CLOSE_OF[OTHER_OF[first]] }));
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).toBeNull();
    });
    // Each dialog opened and closed with no request.
    expect(nonGetRequests()).toEqual([]);
  });

  it('keeps check-code messages queued while their chunk is fetched, refusing Escape and Delete, and shows them once it arrives', async () => {
    const messageChunk = hold();
    const serving = servedAtOnce();
    serving['./MessageDialog'].held = messageChunk.held;
    const fieldEventPath = `/api/forms/${String(createFormDefinitionFixture().viewId)}/checkcode/field-event`;
    let events = 0;
    const definition = await renderFreshForm(serving, (call) => {
      if (call.method !== 'POST' || call.url.pathname !== fieldEventPath) {
        return null;
      }
      events += 1;
      return jsonResponse(200, {
        ...NO_EFFECTS,
        dialogs: events === 1 ? [{ message: 'Check the onset date.', title: null }] : [],
      });
    });
    await flush();

    // Entering a field raises FieldBefore, whose answer holds a DIALOG.
    const field = controlOf(definition, 'LastName');
    field.focus();
    const placeholder = await screen.findByRole('dialog', { name: OPENING });
    expect(placeholder.getAttribute('aria-busy')).toBe('true');
    // Nothing on it dismisses the message unseen: it has no Cancel, and Escape does nothing.
    expect(within(placeholder).queryByRole('button')).toBeNull();
    expect(document.activeElement).toBe(placeholder);
    fireEvent.keyDown(placeholder, { key: 'Escape' });
    fireEvent.click(formActionButton('Delete'));
    expect(onlyDialog()).toBe(placeholder);

    messageChunk.release();
    const message = await screen.findByRole('dialog', { name: 'Message' });
    expect(onlyDialog()).toBe(message);
    expect(message.textContent).toContain('Check the onset date.');
    fireEvent.click(within(message).getByRole('button', { name: 'OK' }));

    await waitFor(() => {
      expect(screen.queryByRole('dialog')).toBeNull();
    });
    // Focus is back in the field, and moving it through the placeholder and the message
    // raised no event of its own: the one FieldBefore is the only request.
    expect(document.activeElement).toBe(field);
    await flush();
    expect(nonGetRequests()).toEqual([`POST ${fieldEventPath}`]);
  });

  it.each<{ failing: 'Find' | 'Delete'; module: DialogModule }>([
    { failing: 'Find', module: './FindRecordDialog' },
    { failing: 'Delete', module: './ConfirmDialog' },
  ])(
    'opens the other dialog at once while $failing cannot be fetched, says so only while $failing is asked for, and Retry opens it',
    async ({ failing, module }) => {
      const working = OTHER_OF[failing];
      const serving = servedAtOnce();
      serving[module].fails = true;
      await renderFreshForm(serving);
      await flush();
      // The fetch as the form opened failed while no dialog was asked for: nothing says so.
      expect(screen.queryByText(DIALOG_FAILED)).toBeNull();

      // The other dialog's chunk arrived, so it opens at once, with no failure shown.
      const workingButton = formActionButton(working);
      workingButton.focus();
      fireEvent.click(workingButton);
      const workingDialog = onlyDialog();
      expect(screen.getByRole('dialog', { name: DIALOG_OF[working] })).toBe(workingDialog);
      expect(screen.queryByText(DIALOG_FAILED)).toBeNull();
      fireEvent.click(within(workingDialog).getByRole('button', { name: CLOSE_OF[working] }));
      await waitFor(() => {
        expect(screen.queryByRole('dialog')).toBeNull();
      });
      expect(document.activeElement).toBe(workingButton);

      // Asking for the failing dialog fetched its chunk again, which failed too.
      fireEvent.click(formActionButton(failing));
      await findAlertContaining(DIALOG_FAILED);
      expect(screen.queryByRole('dialog')).toBeNull();

      // It blocks nothing: the other dialog opens, and the failure, no longer asked for, goes.
      fireEvent.click(workingButton);
      expect(await screen.findByRole('dialog', { name: DIALOG_OF[working] })).toBe(onlyDialog());
      expect(screen.queryByText(DIALOG_FAILED)).toBeNull();
      fireEvent.click(within(onlyDialog()).getByRole('button', { name: CLOSE_OF[working] }));
      await waitFor(() => {
        expect(screen.queryByRole('dialog')).toBeNull();
      });

      fireEvent.click(formActionButton(failing));
      const alert = await findAlertContaining(DIALOG_FAILED);
      serving[module].fails = false;
      fireEvent.click(within(alert).getByRole('button', { name: 'Retry' }));

      expect(await screen.findByRole('dialog', { name: DIALOG_OF[failing] })).toBe(onlyDialog());
      expect(screen.queryByText(DIALOG_FAILED)).toBeNull();
      fireEvent.click(within(onlyDialog()).getByRole('button', { name: CLOSE_OF[failing] }));
      await waitFor(() => {
        expect(screen.queryByRole('dialog')).toBeNull();
      });
      // Retry has gone with its banner, so focus returns to the toolbar button of the dialog it opened.
      expect(document.activeElement).toBe(formActionButton(failing));
      expect(nonGetRequests()).toEqual([]);
    },
  );
});


describe('FormView banner action focus', () => {
  // Retry, Save again, Discard edits and Dismiss each remove the banner whose button ran
  // them, at once or when the resent write starts. The form's heading then takes focus in
  // the commit that removes the button, before the browser could paint, so focus never
  // rests on the page body. A banner that appears moves no focus, and focus the user moved
  // elsewhere stays there.

  const AT_1_PATH = `${FORM_PATH}/records/at/1`;
  const AT_2_PATH = `${FORM_PATH}/records/at/2`;
  const B_PATH = `${FORM_PATH}/records/${RECORD_B.globalRecordId}`;
  const PUT_LINE = `PUT ${FORM_PATH}/records/${RECORD.globalRecordId}`;
  /** RECORD's stored LastName, which Discard edits puts back on screen. */
  const STORED_LAST_NAME = 'Lovelace';

  const NOT_FOUND_PROBLEM: ProblemDetails = {
    type: 'https://tools.ietf.org/html/rfc9110#section-15.5.5',
    title: 'Not Found',
    status: 404,
    detail: 'The test server has no such record.',
    traceId: '00-test-not-found-00',
  };

  /** A 503, carrying `writeOutcome` as a record write's does when one is given. */
  function unavailableProblem(writeOutcome?: WriteOutcome): ProblemDetails | WriteProblemDetails {
    const problem: ProblemDetails = {
      type: 'https://tools.ietf.org/html/rfc9110#section-15.6.4',
      title: 'Service Unavailable',
      status: 503,
      detail: 'The test database is unavailable.',
      traceId: '00-test-banner-focus-00',
    };
    return writeOutcome === undefined ? problem : { ...problem, writeOutcome };
  }

  /** The form's heading, which takes the focus of a banner button its own action removed. */
  function headingOf(definition: FormDefinition): HTMLElement {
    return screen.getByRole('heading', { level: 2, name: definition.name });
  }

  /** Presses `button` as a keyboard user does: it holds focus when it is activated. */
  function press(button: HTMLElement): void {
    button.focus();
    expect(document.activeElement).toBe(button);
    fireEvent.click(button);
  }

  function noAlert(): Promise<void> {
    return waitFor(() => {
      expect(screen.queryByRole('alert')).toBeNull();
    });
  }

  function readsOf(path: string): string[] {
    return calls.filter((call) => call.method === 'GET' && call.url.pathname === path).map(requestLine);
  }

  /**
   * Edits LastName and saves it with a PUT the fake answers with `problem`, and returns the
   * banner that shows the failure. The click does not focus Save, so focus is on the page
   * body when the banner appears, and stays there: a banner that appears moves no focus.
   */
  async function failedSave(
    definition: FormDefinition,
    problem: ProblemDetails | WriteProblemDetails,
  ): Promise<HTMLElement> {
    edit(definition, 'LastName', EDITED_LAST_NAME);
    replyToWrite({ kind: 'problem', problem });
    fireEvent.click(formActionButton('Save'));
    const banner = await screen.findByRole('alert');
    await flush();
    expect(document.activeElement).toBe(document.body);
    return banner;
  }

  /** Answers a held resend with the stored record, and waits until the save has ended. */
  async function saveHeld(held: HeldWrite): Promise<void> {
    await waitFor(() => {
      expect(writeCalls()).toHaveLength(2);
    });
    held.release({ kind: 'saved', record: savedWith(RECORD, { LastName: EDITED_LAST_NAME }) });
    await waitFor(() => {
      expect(isDisabled(formActionButton('Save'))).toBe(false);
    });
  }

  it.each<{
    label: string;
    problem: ProblemDetails | WriteProblemDetails;
    /** The accessible name of the banner button the case presses. */
    button: string;
    /** The action resends the draft's PUT, which the fake holds until the case answers it. */
    resends: boolean;
    /** The action opens the nearest record again, the one at position 1. */
    reopens: boolean;
    /** LastName on screen once the action has run its course. */
    lastName: string;
  }>([
    {
      label: 'Retry of a 503 notWritten',
      problem: unavailableProblem('notWritten'),
      button: 'Retry',
      resends: true,
      reopens: false,
      lastName: EDITED_LAST_NAME,
    },
    {
      label: 'Retry of a 500 notWritten',
      problem: NOT_WRITTEN_PROBLEM,
      button: 'Retry',
      resends: true,
      reopens: false,
      lastName: EDITED_LAST_NAME,
    },
    {
      label: 'Save again after a 503 unknown',
      problem: unavailableProblem('unknown'),
      button: 'Save again (the earlier save may already have been applied)',
      resends: true,
      reopens: false,
      lastName: EDITED_LAST_NAME,
    },
    {
      label: 'Dismiss of a 503 notWritten',
      problem: unavailableProblem('notWritten'),
      button: 'Dismiss',
      resends: false,
      reopens: false,
      lastName: EDITED_LAST_NAME,
    },
    {
      label: 'Dismiss of a 500 notWritten',
      problem: NOT_WRITTEN_PROBLEM,
      button: 'Dismiss',
      resends: false,
      reopens: false,
      lastName: EDITED_LAST_NAME,
    },
    {
      label: 'Discard edits after a 404',
      problem: NOT_FOUND_PROBLEM,
      button: 'Discard edits',
      resends: false,
      reopens: true,
      lastName: STORED_LAST_NAME,
    },
  ])('gives the heading the focus that $label removes with its banner', async ({
    problem,
    button,
    resends,
    reopens,
    lastName,
  }) => {
    // While `recordCommits` is set, the focused element at the end of each commit.
    const focusAtCommits: (Element | null)[] = [];
    let recordCommits = false;
    const { definition } = await renderForm(createFormDefinitionFixture(), {
      onCommit: () => {
        if (recordCommits) {
          focusAtCommits.push(document.activeElement);
        }
      },
    });
    const banner = await failedSave(definition, problem);
    const held = resends ? holdWrite() : null;

    recordCommits = true;
    press(within(banner).getByRole('button', { name: button }));
    await noAlert();
    const heading = headingOf(definition);
    expect(document.activeElement).toBe(heading);

    if (held !== null) {
      await saveHeld(held);
    }
    if (reopens) {
      await waitFor(() => {
        expect(readsOf(AT_1_PATH)).toHaveLength(2);
      });
    }
    await waitFor(() => {
      expect(controlValue(controlOf(definition, 'LastName'))).toBe(lastName);
    });
    await flush();
    expect(document.activeElement).toBe(heading);
    // Every commit from the press on ended with focus on the pressed button, still on the
    // page, or on the heading; none left it on the page body.
    expect(focusAtCommits.length).toBeGreaterThan(0);
    const unfocused = focusAtCommits.filter((element) => element === null || element === document.body);
    expect(unfocused.map((element) => element?.tagName ?? 'none')).toEqual([]);
    // The action sent what it always sends: the failed PUT, and the resend when it makes one.
    const writes = writeCalls();
    expect(writes.map(requestLine)).toEqual(resends ? [PUT_LINE, PUT_LINE] : [PUT_LINE]);
    for (const write of writes) {
      expect(writtenValues(write)).toEqual({ LastName: EDITED_LAST_NAME });
    }
  });

  it('keeps focus on the heading when the retried save fails again and its banner returns', async () => {
    const { definition } = await renderForm();
    const banner = await failedSave(definition, unavailableProblem('notWritten'));
    const held = holdWrite();

    press(within(banner).getByRole('button', { name: 'Retry' }));
    await noAlert();
    const heading = headingOf(definition);
    expect(document.activeElement).toBe(heading);

    await waitFor(() => {
      expect(writeCalls()).toHaveLength(2);
    });
    held.release({ kind: 'problem', problem: unavailableProblem('notWritten') });
    const returned = await screen.findByRole('alert');
    await flush();

    expect(within(returned).getByRole('button', { name: 'Retry' })).toBeTruthy();
    expect(document.activeElement).toBe(heading);
    expect(writeCalls().map(requestLine)).toEqual([PUT_LINE, PUT_LINE]);
  });

  it('leaves focus where the user moved it before the retried save removed the banner', async () => {
    const { definition } = await renderForm();
    const banner = await failedSave(definition, unavailableProblem('notWritten'));
    const held = holdWrite();

    press(within(banner).getByRole('button', { name: 'Retry' }));
    // The retried write starts, and removes the banner, only after the click has returned.
    expect(screen.getByRole('alert')).toBe(banner);
    const find = formActionButton('Find');
    find.focus();
    await noAlert();
    expect(document.activeElement).toBe(find);

    await saveHeld(held);
    await flush();
    expect(document.activeElement).toBe(find);
    expect(writeCalls().map(requestLine)).toEqual([PUT_LINE, PUT_LINE]);
  });

  it('gives the heading the focus that Dismiss removes with the navigation notice', async () => {
    const { definition } = await renderForm(createFormDefinitionFixture(), {
      records: [RECORD_A, RECORD_B],
      // B is gone by the time its grid row is opened.
      answerRead: (path) => (path === B_PATH ? problemResponse(NOT_FOUND_PROBLEM) : undefined),
    });
    const records = screen.getByRole('region', { name: 'Records' });
    fireEvent.click(within(records).getByRole('row', { name: new RegExp(B_LAST_NAME) }));

    // The open lands on the nearest record with a notice that offers Dismiss. The grid sits far
    // below it, so the open ends on the heading, which the notice sits under.
    const notice = await waitFor(() =>
      present(
        screen
          .queryAllByRole('status')
          .find((status) => within(status).queryByRole('button', { name: 'Dismiss' }) !== null),
        'the navigation notice',
      ),
    );
    await flush();
    expect(navigatorStatus()).toBe('1 of 2');
    expect(document.activeElement).toBe(headingOf(definition));

    press(within(notice).getByRole('button', { name: 'Dismiss' }));

    expect(notice.isConnected).toBe(false);
    expect(document.activeElement).toBe(headingOf(definition));
    expect(readsOf(B_PATH)).toEqual([`GET ${B_PATH}`]);
    expect(writeCalls()).toEqual([]);
  });

  it('gives the heading the focus that Retry removes with a failed navigation read', async () => {
    let failedReads = 0;
    const { definition } = await renderForm(createFormDefinitionFixture(), {
      records: [RECORD_A, RECORD_B],
      // The first read of position 2 finds the database unreachable; its retry is served.
      answerRead: (path) => {
        if (path !== AT_2_PATH || failedReads > 0) {
          return undefined;
        }
        failedReads += 1;
        return problemResponse(unavailableProblem());
      },
    });
    fireEvent.click(navigatorButton('Next'));
    const banner = await screen.findByRole('alert');
    await flush();
    expect(document.activeElement).toBe(document.body);

    press(within(banner).getByRole('button', { name: 'Retry' }));

    expect(banner.isConnected).toBe(false);
    const heading = headingOf(definition);
    expect(document.activeElement).toBe(heading);
    await waitFor(() => {
      expect(navigatorStatus()).toBe('2 of 2');
    });
    await flush();
    expect(document.activeElement).toBe(heading);
    expect(readsOf(AT_2_PATH)).toHaveLength(2);
    expect(writeCalls()).toEqual([]);
  });
});

describe('FormView focus and feedback after actions', () => {
  // An action's outcome is brought to the user: a confirmed delete lands on the next record
  // with no deleted banner in between, says "Record deleted." and focuses the heading.

  const A_PATH = `${FORM_PATH}/records/${RECORD_A.globalRecordId}`;
  const DELETED_BANNER_TEXT = 'This record is deleted and is read-only.';
  const RECORD_DELETED = 'Record deleted.';

  function headingOf(definition: FormDefinition): HTMLElement {
    return screen.getByRole('heading', { level: 2, name: definition.name });
  }

  function toolbarStatus(): HTMLElement {
    return within(screen.getByRole('toolbar', { name: 'Form actions' })).getByRole('status');
  }

  /** Answers the DELETE of A with 204; from then on the active set holds B alone, at position 1. */
  function deletingA(): Route {
    let deleted = false;
    return (call) => {
      if (call.method === 'DELETE' && call.url.pathname === A_PATH) {
        deleted = true;
        return new Response(null, { status: 204 });
      }
      if (!deleted || call.method !== 'GET') {
        return null;
      }
      if (call.url.pathname === `${FORM_PATH}/records/count`) {
        return jsonResponse(200, { count: 1 });
      }
      if (call.url.pathname === `${FORM_PATH}/records/at/1`) {
        return jsonResponse(200, { ...RECORD_B, position: 1, count: 1 });
      }
      return null;
    };
  }

  /** Deletes the open record A through the toolbar and its confirmation, and waits for B to open. */
  async function deleteA(definition: FormDefinition): Promise<void> {
    fireEvent.click(formActionButton('Delete'));
    fireEvent.click(
      within(await screen.findByRole('dialog', { name: 'Delete record' })).getByRole('button', { name: 'Delete record' }),
    );
    await waitFor(() => {
      expect(navigatorStatus()).toBe('1 of 1');
      expect(controlValue(controlOf(definition, 'LastName'))).toBe(B_LAST_NAME);
    });
    await flush();
  }

  it('lands on the next record after a confirmed delete with no deleted banner, focuses the heading and says "Record deleted."', async () => {
    /** The DOM at the end of each commit from the confirmation on. */
    const commits: { readonly banner: boolean; readonly protectedLastName: boolean }[] = [];
    let recording = false;
    const { definition } = await renderForm(createFormDefinitionFixture(), {
      records: [RECORD_A, RECORD_B],
      route: deletingA(),
      onCommit: () => {
        if (recording) {
          commits.push({
            banner: (document.body.textContent ?? '').includes(DELETED_BANNER_TEXT),
            protectedLastName: Array.from(
              document.querySelectorAll<HTMLElement>('[data-field-name].field--protected'),
            ).some((wrapper) => wrapper.dataset.fieldName === 'LastName'),
          });
        }
      },
    });
    const deleteButton = formActionButton('Delete');
    expect(toolbarStatus().textContent).toBe('');

    recording = true;
    await deleteA(definition);

    expect(writeCalls().map(requestLine)).toEqual([`DELETE ${A_PATH}`]);
    // No commit showed the record just deleted as deleted, read-only or protected.
    expect(commits.length).toBeGreaterThan(0);
    expect(commits.filter((commit) => commit.banner || commit.protectedLastName)).toEqual([]);
    expect(document.activeElement).toBe(headingOf(definition));
    expect(toolbarStatus().textContent).toBe(RECORD_DELETED);
    expect(toolbarStatus().className).not.toContain('visually-hidden');
    expect(isDisabled(formActionButton('Save'))).toBe(false);
    expect(isDisabled(deleteButton)).toBe(false);

    // Asking to delete again ends the report, and Cancel returns focus to Delete.
    fireEvent.click(deleteButton);
    const again = await screen.findByRole('dialog', { name: 'Delete record' });
    expect(toolbarStatus().textContent).toBe('');
    fireEvent.click(within(again).getByRole('button', { name: 'Cancel' }));
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).toBeNull();
    });
    expect(document.activeElement).toBe(deleteButton);
    expect(writeCalls().map(requestLine)).toEqual([`DELETE ${A_PATH}`]);
  });

  it('keeps "Record deleted." on the record navigation landed on, and ends it with the next move', async () => {
    const { definition } = await renderForm(createFormDefinitionFixture(), {
      records: [RECORD_A, RECORD_B],
      route: deletingA(),
    });
    await deleteA(definition);
    expect(toolbarStatus().textContent).toBe(RECORD_DELETED);

    fireEvent.click(navigatorButton('First'));

    await waitFor(() => {
      expect(toolbarStatus().textContent).toBe('');
    });
    expect(toolbarStatus().className).toContain('visually-hidden');
  });

  it.each<{ label: string; startAtLast: boolean; press: string; lands: string; focus: string }>([
    { label: 'Next reaching the last record', startAtLast: false, press: 'Next', lands: '2 of 2', focus: 'Previous' },
    { label: 'Previous reaching the first record', startAtLast: true, press: 'Previous', lands: '1 of 2', focus: 'Next' },
  ])('moves the focus of $label, which disables it, to $focus', async ({ startAtLast, press, lands, focus }) => {
    await renderForm(createFormDefinitionFixture(), { records: [RECORD_A, RECORD_B] });
    if (startAtLast) {
      fireEvent.click(navigatorButton('Last'));
      await waitFor(() => {
        expect(navigatorStatus()).toBe('2 of 2');
      });
    }
    const pressed = navigatorButton(press);
    // Pressed from the keyboard, so the button holds focus as it is activated.
    pressed.focus();
    fireEvent.click(pressed);

    await waitFor(() => {
      expect(navigatorStatus()).toBe(lands);
    });
    await flush();
    expect(isDisabled(pressed)).toBe(true);
    expect(document.activeElement).toBe(navigatorButton(focus));
    expect(writeCalls()).toEqual([]);
  });

  it.each<{ label: string; press: string; lands: string; focus: string }>([
    { label: '"Earlier records" reaching the first page', press: 'Earlier records', lands: 'Page 1 of 3', focus: 'Later records' },
    { label: '"Later records" reaching the last page', press: 'Later records', lands: 'Page 3 of 3', focus: 'Earlier records' },
  ])('moves the focus of $label, which disables it, to "$focus"', async ({ press, lands, focus }) => {
    const definition = createFormDefinitionFixture();
    // A list of three pages of 50, answering the page asked for with one row.
    mountForm(definition, {
      route: (call) => {
        if (call.method !== 'GET' || call.url.pathname !== `${FORM_PATH}/records`) {
          return null;
        }
        const page = Number(call.url.searchParams.get('page') ?? '1');
        const body: RecordPage = { items: [recordSummaryFixture(RECORD, definition)], total: 150, page, pageSize: 50 };
        return jsonResponse(200, body);
      },
    });
    const records = await screen.findByRole('region', { name: 'Records' });
    await within(records).findByText('Page 1 of 3');
    fireEvent.click(within(records).getByRole('button', { name: 'Later records' }));
    await within(records).findByText('Page 2 of 3');

    const pressed = within(records).getByRole('button', { name: press });
    pressed.focus();
    fireEvent.click(pressed);

    await within(records).findByText(lands);
    await flush();
    expect(isDisabled(pressed)).toBe(true);
    expect(document.activeElement).toBe(within(records).getByRole('button', { name: focus }));
  });

  // A save or move stopped by field errors focuses the summary, which scrolls it into view,
  // and its first line says what did not happen; a repeated attempt is a new line.

  /** Age's upper bound is 120, so client validation stops every save of this value. */
  const AGE_TOO_HIGH = '121';
  const NOT_FOUND: ProblemDetails = {
    type: 'https://tools.ietf.org/html/rfc9110#section-15.5.5',
    title: 'Not Found',
    status: 404,
    detail: 'The test server has no such resource.',
    traceId: '00-test-outcome-not-found-00',
  };

  /** Presses `button` as a keyboard user does: it holds focus as it is activated. */
  function press(button: HTMLElement): void {
    button.focus();
    fireEvent.click(button);
  }

  /** The open action of B's grid row, the row's one button. */
  function openButtonOfB(): HTMLElement {
    const records = screen.getByRole('region', { name: 'Records' });
    return within(within(records).getByRole('row', { name: new RegExp(B_LAST_NAME) })).getByRole('button');
  }

  it.each<{ label: string; start: () => void; line: string }>([
    {
      label: 'Save',
      start: () => press(formActionButton('Save')),
      line: 'The record was not saved: correct the fields below.',
    },
    {
      label: 'Next',
      start: () => press(navigatorButton('Next')),
      line: 'Could not move to the next record: correct the fields below.',
    },
    {
      label: 'Last',
      start: () => press(navigatorButton('Last')),
      line: 'Could not move to the last record: correct the fields below.',
    },
    {
      label: 'Go to record 2',
      start: () => {
        fireEvent.change(screen.getByLabelText('Go to record'), { target: { value: '2' } });
        press(navigatorButton('Go'));
      },
      line: 'Could not go to record 2: correct the fields below.',
    },
    {
      label: 'New',
      start: () => press(navigatorButton('New')),
      line: 'Could not start a new record: correct the fields below.',
    },
    {
      label: 'Back to forms',
      start: () => press(formActionButton('Back to forms')),
      line: 'Could not return to the forms list: correct the fields below.',
    },
    {
      label: 'a grid open',
      start: () => press(openButtonOfB()),
      line: 'Could not open the record: correct the fields below.',
    },
  ])('focuses the summary and says what $label did not do, anew on every attempt', async ({ start, line }) => {
    const { definition, onExit } = await renderForm(createFormDefinitionFixture(), { records: [RECORD_A, RECORD_B] });
    edit(definition, 'Age', AGE_TOO_HIGH);

    start();
    const summary = await findValidationSummary();
    const first = await within(summary).findByText(line);
    await flush();
    expect(document.activeElement).toBe(summary);
    expect(summary.getAttribute('tabindex')).toBe('-1');
    // The line heads the summary, above its heading and the field's message.
    expect(summary.firstElementChild?.textContent).toBe(line);
    expect(summaryItemsFor(summary, 'Age')).toHaveLength(1);

    start();
    await waitFor(() => {
      expect(first.isConnected).toBe(false);
    });
    await flush();
    // A new line with the same text, which the alert announces again, and the focus back on the summary.
    expect(within(summary).getByText(line)).not.toBe(first);
    expect(document.activeElement).toBe(summary);
    // The user stayed on the record, which nothing was sent for.
    expect(writeCalls()).toEqual([]);
    expect(navigatorStatus()).toBe('1 of 2');
    expect(controlValue(controlOf(definition, 'Age'))).toBe(AGE_TOO_HIGH);
    expect(onExit).not.toHaveBeenCalled();
  });

  it('leaves focus in the field it rests in when Save is blocked, so no FieldAfter is raised', async () => {
    const { definition } = await renderForm(createFormDefinitionFixture(), { answersFieldEvents: true });
    // Past the tick in which installing the record suppresses field events.
    await flush();
    const age = controlOf(definition, 'Age');
    act(() => {
      age.focus();
    });
    edit(definition, 'Age', AGE_TOO_HIGH);

    fireEvent.click(formActionButton('Save'));

    const summary = await findValidationSummary();
    await within(summary).findByText('The record was not saved: correct the fields below.');
    await flush();
    expect(document.activeElement).toBe(age);
    // The FieldBefore of the focus only: focus never left the field.
    expect(calls.filter((call) => call.url.pathname.endsWith('/checkcode/field-event'))).toHaveLength(1);
    expect(writeCalls().filter((call) => !call.url.pathname.endsWith('/checkcode/field-event'))).toEqual([]);
  });

  it('keeps the Find results across a refused open, and drops them once Close is pressed or a result opens', async () => {
    const match: SearchResult = {
      globalRecordId: RECORD_B.globalRecordId,
      uniqueKey: RECORD_B.uniqueKey,
      recStatus: RECORD_B.recStatus,
      position: 2,
      values: { LastName: B_LAST_NAME },
    };
    const found: SearchResponse = { items: [match], total: 1, truncated: false };
    const { definition } = await renderForm(createFormDefinitionFixture(), {
      records: [RECORD_A, RECORD_B],
      route: (call) => (isSearch(call) ? jsonResponse(200, found) : null),
    });
    const findButton = formActionButton('Find');
    const openName = 'Open record at position 2';
    /** Opens Find and returns its dialog. */
    async function openFind(): Promise<HTMLElement> {
      fireEvent.click(findButton);
      return screen.findByRole('dialog', { name: 'Find record' });
    }
    /** Searches LastName for B in the dialog and waits for the one match. */
    async function searchForB(dialog: HTMLElement): Promise<void> {
      const picker = within(dialog).getByRole('checkbox', { name: 'LastName' });
      if (!(picker instanceof HTMLInputElement) || !picker.checked) {
        fireEvent.click(picker);
      }
      fireEvent.change(within(dialog).getByRole('textbox', { name: 'Last Name (LastName)' }), {
        target: { value: B_LAST_NAME },
      });
      fireEvent.click(within(dialog).getByRole('button', { name: 'Search' }));
      await within(dialog).findByText('1 record matches.');
    }

    edit(definition, 'Age', AGE_TOO_HIGH);
    let dialog = await openFind();
    await searchForB(dialog);
    fireEvent.click(within(dialog).getByRole('button', { name: openName }));
    expect(screen.queryByRole('dialog', { name: 'Find record' })).toBeNull();

    // The open is refused: the summary says so and takes focus from the Find button.
    const summary = await findValidationSummary();
    await within(summary).findByText('Could not open the record: correct the fields below.');
    await flush();
    expect(document.activeElement).toBe(summary);
    expect(navigatorStatus()).toBe('1 of 2');

    // Find shows the same results again, ready to open, with no new search.
    dialog = await openFind();
    expect(within(dialog).getByText('1 record matches.')).toBeTruthy();
    expect(within(dialog).getByRole('button', { name: openName }).getAttribute('aria-disabled')).toBeNull();
    expect(calls.filter(isSearch)).toHaveLength(1);

    // Close drops them.
    fireEvent.click(within(dialog).getByRole('button', { name: 'Close' }));
    await flush();
    dialog = await openFind();
    expect(within(dialog).queryByRole('table', { name: 'Matching records' })).toBeNull();

    // With Age corrected, the chosen result opens once the draft is saved, and its results go with the open.
    fireEvent.click(within(dialog).getByRole('button', { name: 'Close' }));
    // 32, not A's stored 31: an edit back to the stored value would leave the draft clean.
    edit(definition, 'Age', '32');
    replyToWrite({ kind: 'saved', record: savedWith(RECORD_A, { Age: 32 }) });
    dialog = await openFind();
    await searchForB(dialog);
    fireEvent.click(within(dialog).getByRole('button', { name: openName }));
    await waitFor(() => {
      expect(navigatorStatus()).toBe('2 of 2');
    });
    await flush();
    expect(writeCalls().filter((call) => !isSearch(call)).map(requestLine)).toEqual([`PUT ${A_PATH}`]);
    dialog = await openFind();
    expect(within(dialog).queryByRole('table', { name: 'Matching records' })).toBeNull();
    fireEvent.click(within(dialog).getByRole('button', { name: 'Close' }));
  });

  const READ_FAILED: ProblemDetails = {
    type: 'https://tools.ietf.org/html/rfc9110#section-15.6.1',
    title: 'An error occurred while processing your request.',
    status: 500,
    detail: 'The test server failed this read.',
    traceId: '00-test-outcome-read-failed-00',
  };

  it.each<{
    label: string;
    record: RecordDto;
    /** The answer to the read of B by id; `null` leaves it to the read routes. */
    answer: (() => Response) | null;
    /** Text on screen once the open has run its course. */
    shown: string;
    saveEnabled: boolean;
  }>([
    { label: 'an active record', record: RECORD_B, answer: null, shown: B_LAST_NAME, saveEnabled: true },
    {
      label: 'a deleted record, under its deleted banner',
      record: { ...RECORD_B, recStatus: 0 },
      answer: null,
      shown: DELETED_BANNER_TEXT,
      saveEnabled: false,
    },
    {
      label: 'a record whose read fails, under its banner',
      record: RECORD_B,
      answer: () => problemResponse(READ_FAILED),
      shown: READ_FAILED.traceId,
      saveEnabled: true,
    },
  ])('ends an open from the grid of $label on the heading', async ({ record, answer, shown, saveEnabled }) => {
    const { definition } = await renderForm(createFormDefinitionFixture(), {
      records: [RECORD_A, record],
      answerRead: (path) =>
        answer !== null && path === `${FORM_PATH}/records/${RECORD_B.globalRecordId}` ? answer() : undefined,
    });

    press(openButtonOfB());

    await waitFor(() => {
      expect(document.body.textContent).toContain(shown);
    });
    await flush();
    expect(document.activeElement).toBe(headingOf(definition));
    expect(isDisabled(formActionButton('Save'))).toBe(!saveEnabled);
    expect(writeCalls()).toEqual([]);
  });

  // A Save refused with a banner, Save enabled again, leaves focus on Save: the stopped-action
  // outcome moves focus only to a validation summary. A 409, which disables Save, is
  // 'FormView record found deleted by its save (409)'.
  it.each<{
    label: string;
    /** The PUT's answer: a problem the server refuses it with, or no response at all. */
    reply: WriteReply | 'no response';
    /** Text on screen once the refusal is shown. */
    shown: string;
  }>([
    {
      label: 'a 503, under the connection banner',
      reply: {
        kind: 'problem',
        problem: {
          type: 'https://tools.ietf.org/html/rfc9110#section-15.6.4',
          title: 'Service Unavailable',
          status: 503,
          detail: 'The test database is unavailable.',
          traceId: '00-test-outcome-unavailable-00',
          writeOutcome: 'notWritten',
        },
      },
      shown: 'The database cannot be reached. Your changes are still on screen.',
    },
    {
      label: 'no response, under the banner offering Save again',
      reply: 'no response',
      shown: 'The save may already have been applied',
    },
  ])('keeps focus on Save when a Save is refused with $label', async ({ reply, shown }) => {
    const { definition } = await renderForm(createFormDefinitionFixture(), {
      records: [RECORD_A, RECORD_B],
      route: (call) =>
        reply === 'no response' && call.method === 'PUT' ? Promise.reject(new TypeError('Failed to fetch')) : null,
    });
    if (reply !== 'no response') {
      replyToWrite(reply);
    }
    edit(definition, 'FirstName', 'Adah');
    const save = formActionButton('Save');

    press(save);

    await waitFor(() => {
      expect(document.body.textContent).toContain(shown);
    });
    await flush();
    expect(isDisabled(save)).toBe(false);
    expect(isDisabled(formActionButton('Delete'))).toBe(false);
    expect(document.activeElement).toBe(save);
    expect(writeCalls().map(requestLine)).toEqual([`PUT ${A_PATH}`]);
    expect(navigatorStatus()).toBe('1 of 2');
  });

  // Focusing the element that already holds focus does nothing, scrolling included. An attempt
  // made while its outcome still holds focus, after a script's click or a mouse click that
  // focuses no button, scrolls the outcome into view instead.
  it.each<{
    label: string;
    target: string;
    /** Whether Age holds a value client validation stops every save of. */
    invalid: boolean;
    /** The control each of the two attempts clicks. */
    controls: readonly [() => HTMLElement, () => HTMLElement];
    /** The element the attempts' outcome is brought to, once the first attempt shows it. */
    outcome: (definition: FormDefinition) => Promise<HTMLElement>;
  }>([
    {
      label: 'a Save stopped by field errors',
      target: 'the summary',
      invalid: true,
      controls: [() => formActionButton('Save'), () => formActionButton('Save')],
      outcome: () => findValidationSummary(),
    },
    {
      // B opens first, then A, the record whose LastName is Lovelace.
      label: 'an open from the grid',
      target: 'the heading',
      invalid: false,
      controls: [
        openButtonOfB,
        () =>
          within(
            within(screen.getByRole('region', { name: 'Records' })).getByRole('row', { name: /Lovelace/ }),
          ).getByRole('button'),
      ],
      outcome: (definition) => Promise.resolve(headingOf(definition)),
    },
  ])(
    'scrolls $target into view when $label is repeated while it holds focus, and focuses it the first time',
    async ({ invalid, controls, outcome }) => {
      const { definition } = await renderForm(createFormDefinitionFixture(), { records: [RECORD_A, RECORD_B] });
      if (invalid) {
        edit(definition, 'Age', AGE_TOO_HIGH);
      }
      const focus = vi.spyOn(HTMLElement.prototype, 'focus');
      const scroll = vi.spyOn(Element.prototype, 'scrollIntoView');
      const [first, second] = controls;

      // The first attempt, pressed from the keyboard, moves focus to the outcome.
      press(first());
      const target = await outcome(definition);
      await waitFor(() => {
        expect(document.activeElement).toBe(target);
      });
      await flush();
      expect(focus.mock.contexts).toContain(target);
      expect(scroll).not.toHaveBeenCalled();

      // The second leaves focus on the outcome as it starts, so focusing it again would not scroll.
      focus.mockClear();
      fireEvent.click(second());
      await waitFor(() => {
        expect(scroll.mock.contexts).toEqual([target]);
      });
      await flush();
      expect(scroll.mock.contexts).toEqual([target]);
      expect(scroll).toHaveBeenCalledWith({ block: 'center' });
      expect(focus.mock.contexts).not.toContain(target);
      expect(document.activeElement).toBe(target);
      // The stopped Save stays on A, and the second open opens A.
      expect(navigatorStatus()).toBe('1 of 2');
      expect(writeCalls()).toEqual([]);
    },
  );

  it('brings the form-gone banner and its "Discard edits" to a dirty draft whose grid search finds the form gone', async () => {
    const { definition, onFormNotFound } = await renderForm(createFormDefinitionFixture(), {
      route: (call) =>
        call.method === 'GET' && call.url.pathname === `${FORM_PATH}/records` && call.url.searchParams.has('q')
          ? jsonResponse(404, NOT_FOUND)
          : null,
    });
    edit(definition, 'FirstName', 'Adah');
    const search = within(screen.getByRole('region', { name: 'Records' })).getByRole('searchbox', {
      name: 'Search records',
    });
    act(() => {
      search.focus();
    });
    fireEvent.change(search, { target: { value: 'Ada' } });

    const banner = await findAlertContaining('This form no longer exists.');
    await waitFor(() => {
      expect(document.activeElement).toBe(banner);
    });
    expect(banner.getAttribute('tabindex')).toBe('-1');
    expect(within(banner).getByRole('button', { name: 'Discard edits' })).toBeTruthy();
    expect(onFormNotFound).not.toHaveBeenCalled();
    expect(controlValue(controlOf(definition, 'FirstName'))).toBe('Adah');
  });

  it('shows a check-code failure as a sticky notice that takes no focus', async () => {
    const { definition } = await renderForm(createFormDefinitionFixture(), {
      route: (call) =>
        call.method === 'POST' && call.url.pathname.endsWith('/checkcode/field-event')
          ? jsonResponse(500, UNROUTED_PROBLEM)
          : null,
    });
    await flush();
    const lastName = controlOf(definition, 'LastName');
    act(() => {
      lastName.focus();
    });

    const notice = await waitFor(() =>
      present(
        screen
          .queryAllByRole('status')
          .find((status) => (status.textContent ?? '').startsWith('Check code could not run')),
        'the check-code notice',
      ),
    );
    await flush();
    expect(notice.classList.contains('banner--sticky')).toBe(true);
    expect(document.activeElement).toBe(lastName);
  });
});

describe('FormView record found deleted by its save (409)', () => {
  // The 409's banner says the record is deleted and read-only and that the edits on screen
  // were not saved; while it is up it stands in for the deleted status, which returns once
  // it is dismissed. Save stays disabled, so the heading takes the focus Save lost; a
  // navigator button whose leave the 409 cancelled keeps the focus returned to it.
  const CONFLICT_PROBLEM: ProblemDetails = {
    type: 'https://tools.ietf.org/html/rfc9110#section-15.5.10',
    title: 'Conflict',
    status: 409,
    detail: 'The record has been deleted.',
    traceId: '00-test-conflict-00',
  };
  const DELETED_STATUS = 'This record is deleted and is read-only.';
  const CONFLICT_TEXT = 'This record has been deleted and is read-only. Your unsaved changes were not saved.';

  function deletedStatus(): HTMLElement | undefined {
    return screen.queryAllByRole('status').find((status) => status.textContent === DELETED_STATUS);
  }

  it.each<{ label: string; focusFind: boolean }>([
    { label: 'gives the heading the focus that Save lost', focusFind: false },
    { label: 'leaves focus where the user moved it', focusFind: true },
  ])('says the edits were not saved in place of the deleted status, and $label', async ({ focusFind }) => {
    const { definition } = await renderForm();
    edit(definition, 'LastName', EDITED_LAST_NAME);
    const held = holdWrite();
    // The click does not focus Save, so focus is on the page body, as when Save is disabled while it saves.
    fireEvent.click(formActionButton('Save'));
    await waitFor(() => {
      expect(writeCalls()).toHaveLength(1);
    });
    const find = formActionButton('Find');
    if (focusFind) {
      find.focus();
    }
    held.release({ kind: 'problem', problem: CONFLICT_PROBLEM });

    const alert = await findAlertContaining('not saved');
    await flush();
    expect(within(alert).getByText(CONFLICT_TEXT)).toBeTruthy();
    expect(deletedStatus()).toBeUndefined();
    expect(isDisabled(formActionButton('Save'))).toBe(true);
    expect(isDisabled(formActionButton('Delete'))).toBe(true);
    expect(controlValue(controlOf(definition, 'LastName'))).toBe(EDITED_LAST_NAME);
    expect(isDisabled(controlOf(definition, 'LastName'))).toBe(true);
    expect(document.activeElement).toBe(
      focusFind ? find : screen.getByRole('heading', { level: 2, name: definition.name }),
    );

    fireEvent.click(within(alert).getByRole('button', { name: 'Dismiss' }));

    expect(alert.isConnected).toBe(false);
    expect(deletedStatus()).toBeDefined();
    expect(writeCalls().map(requestLine)).toEqual([`PUT ${FORM_PATH}/records/${RECORD.globalRecordId}`]);
  });

  it('leaves the focus returned to the navigator button whose leave the 409 cancelled', async () => {
    const { definition } = await renderForm(createFormDefinitionFixture(), { records: [RECORD_A, RECORD_B] });
    edit(definition, 'LastName', EDITED_LAST_NAME);
    const next = navigatorButton('Next');
    const held = holdWrite();
    // The click does not focus Next, so focus is on the page body, where the browser drops it
    // when the pressed Next is disabled while the leave saves.
    fireEvent.click(next);
    await waitFor(() => {
      expect(writeCalls()).toHaveLength(1);
    });
    expect(isDisabled(next)).toBe(true);
    expect(document.activeElement).toBe(document.body);
    // As the shell does for a button that lost focus by being disabled: once it is enabled
    // again, a MutationObserver microtask after that commit returns focus to it, unless focus
    // has gone elsewhere meanwhile.
    const focusReturn = new MutationObserver(() => {
      const active = document.activeElement;
      if (!isDisabled(next) && (active === null || active === document.body)) {
        next.focus();
      }
    });
    focusReturn.observe(next, { attributes: true, attributeFilter: ['disabled'] });
    try {
      held.release({ kind: 'problem', problem: CONFLICT_PROBLEM });

      await findAlertContaining('not saved');
      await flush();
      expect(isDisabled(next)).toBe(false);
      expect(document.activeElement).toBe(next);
      expect(navigatorStatus()).toBe('1 of 2');
      expect(writeCalls().map(requestLine)).toEqual([`PUT ${FORM_PATH}/records/${RECORD_A.globalRecordId}`]);
    } finally {
      focusReturn.disconnect();
    }
  });
});

describe('number fields hold integers beyond the safe range exactly', () => {
  const CASE_ID = fieldByName('CaseID');

  it.each<[bigint]>([[9007199254740993n], [-9007199254740993n], [9223372036854775807n]])(
    'shows the stored %s digit for digit in its control and its grid cell',
    async (stored) => {
      const record: RecordDto = { ...RECORD, values: { ...RECORD.values, [CASE_ID.name]: stored } };
      await renderForm(createFormDefinitionFixture(), { records: [record] });

      const digits = stored.toString();
      expect(screen.getByLabelText(CASE_ID.prompt ?? CASE_ID.name)).toHaveProperty('value', digits);
      expect(within(screen.getByRole('region', { name: 'Records' })).getByText(digits)).toBeTruthy();
    },
  );

  // An expected ExactDecimal is written as its plain shape, which the call is compared with
  // member by member: only the client can make a real one.
  it.each<[string, FieldValue | { readonly kind: 'decimal'; readonly literal: string }]>([
    ['9007199254740993', 9007199254740993n],
    ['-9007199254740993', -9007199254740993n],
    ['9223372036854775807', 9223372036854775807n],
    [' 9007199254740994 ', 9007199254740994n],
    ['9.007199254740993e15', 9007199254740993n],
    ['9007199254740993.000', 9007199254740993n],
    ['0x20000000000001', 9007199254740993n],
    ['9223372036854775808', 2 ** 63],
    ['9007199254740991', 9007199254740991],
    ['-9007199254740991', -9007199254740991],
    ['37.5', 37.5],
    // A fraction whose nearest double is whole keeps its literal, as a JSON number token.
    ['9007199254740992.5', { kind: 'decimal', literal: '9007199254740992.5' }],
    ['-9223372036854775808.5', { kind: 'decimal', literal: '-9223372036854775808.5' }],
    ['2.0000000000000001', { kind: 'decimal', literal: '2.0000000000000001' }],
    ['1e-400', { kind: 'decimal', literal: '1e-400' }],
    [' +00.5e-400 ', { kind: 'decimal', literal: '0.5e-400' }],
    ['34.5', 34.5],
    ['0.1', 0.1],
    ['1e20', 1e20],
    ['1e400', '1e400'],
    ['abc', 'abc'],
  ])('enters the text %j as %s', (text, expected) => {
    const onChange = vi.fn<(value: FieldValue) => void>();
    render(
      <NumberInput
        field={CASE_ID}
        value={null}
        disabled={false}
        isProtected={false}
        errors={[]}
        onChange={onChange}
      />,
    );

    const input = screen.getByLabelText(CASE_ID.prompt ?? CASE_ID.name);
    fireEvent.change(input, { target: { value: text } });

    expect(onChange).toHaveBeenCalledTimes(1);
    expect(onChange).toHaveBeenLastCalledWith(expected);
    expect(input).toHaveProperty('value', text);
  });
});

describe('a number typed as a fraction whose nearest double is whole is sent as typed', () => {
  const CASE_ID = fieldByName('CaseID');
  // The JSON text of CaseID's member in a request body, up to the next member or the end of its object.
  const CASE_ID_MEMBER = new RegExp(`${JSON.stringify(CASE_ID.name)}:([^,}]*)`);

  /** The JSON token a request body sends for CaseID. */
  function sentToken(call: Call | undefined, what: string): string {
    const match = CASE_ID_MEMBER.exec(present(present(call, what).body, `the body of ${what}`));
    return present(match, `CaseID in ${what}`)[1];
  }

  // Integer columns reject these as not whole and a `float` column stores their nearest double,
  // which the server decides only if it receives the number typed rather than that double.
  it.each<[string, string]>([
    ['9007199254740992.5', '9007199254740992.5'],
    ['-9223372036854775808.5', '-9223372036854775808.5'],
    ['2.0000000000000001', '2.0000000000000001'],
    [' +00.5e-400 ', '0.5e-400'],
    ['34.5', '34.5'],
    ['0.1', '0.1'],
  ])('sends the text %j as the JSON number %s in its FieldAfter event and its save', async (text, token) => {
    await renderForm(createFormDefinitionFixture(), { answersFieldEvents: true });
    // The server stores RECORD unchanged, as an integer column would reject or a `float` column round it.
    replyToWrite({ kind: 'saved', record: RECORD });
    // Installing the record moves focus without raising an event; the test's own focus comes after it.
    await act(async () => {
      await new Promise<void>((resolve) => {
        setTimeout(resolve, 0);
      });
    });
    const input = screen.getByLabelText(promptOf(CASE_ID));

    act(() => {
      input.focus();
    });
    fireEvent.change(input, { target: { value: text } });
    act(() => {
      input.blur();
    });
    // Leaving the control shows the number the draft stands for, the token a save sends.
    expect(input).toHaveProperty('value', token);
    const formActions = screen.getByRole('toolbar', { name: 'Form actions' });
    fireEvent.click(within(formActions).getByRole('button', { name: 'Save' }));

    const update = await waitFor(() => present(calls.find((call) => call.method === 'PUT'), 'the update'));
    const fieldAfter = calls.find(
      (call) => call.method === 'POST' && call.body?.includes('"event":"FieldAfter"') === true,
    );
    expect(sentToken(fieldAfter, 'the FieldAfter event')).toBe(token);
    expect(update.body).toBe(`{"values":{${JSON.stringify(CASE_ID.name)}:${token}}}`);
    // The saved record, RECORD with its CaseID 1042, replaces the draft once the update settles.
    await waitFor(() => {
      expect(input).toHaveProperty('value', '1042');
    });
  });
});

describe('a number field shows the number its text stands for once it loses focus', () => {
  /** Focuses Age, types `text` and leaves it, as a user does; FieldBefore and FieldAfter are raised. */
  async function enterAge(definition: FormDefinition, text: string): Promise<HTMLElement> {
    // Installing the record moves focus without raising an event; the case's own focus comes after it.
    await flush();
    const age = controlOf(definition, 'Age');
    act(() => {
      age.focus();
    });
    fireEvent.change(age, { target: { value: text } });
    act(() => {
      age.blur();
    });
    return age;
  }

  // The server accepts each for the mask ###, so the client accepts it too and shows what is saved.
  it.each<[string, number]>([
    ['1e2', 100],
    ['045', 45],
    ['45.', 45],
    [' 45 ', 45],
  ])(
    'saves the text %j in Age as %s and shows that number from leaving the field to after the save',
    async (text, stored) => {
      const { definition } = await renderForm(createFormDefinitionFixture(), { answersFieldEvents: true });
      replyToWrite({ kind: 'saved', record: savedWith(RECORD, { Age: stored }) });

      const age = await enterAge(definition, text);

      expect(age).toHaveProperty('value', String(stored));
      fireEvent.click(formActionButton('Save'));
      const update = await waitFor(() => present(calls.find((call) => call.method === 'PUT'), 'the update'));
      expect(update.body).toBe(`{"values":{"Age":${String(stored)}}}`);
      const status = within(screen.getByRole('toolbar', { name: 'Form actions' })).getByRole('status');
      // The save has settled once the toolbar confirms it.
      await waitFor(() => {
        expect(status.textContent).toBe('Changes saved.');
      });
      await flush();
      expect(age).toHaveProperty('value', String(stored));
      // Leaving the field still raised its one FieldAfter event.
      const fieldAfters = calls.filter(
        (call) => call.method === 'POST' && call.body?.includes('"event":"FieldAfter"') === true,
      );
      expect(fieldAfters.map((call) => call.body?.includes('"fieldName":"Age"'))).toEqual([true]);
    },
  );

  it.each<[string]>([['abc'], ['1e400']])(
    'keeps the text %j in Age as typed once it loses focus, beside its error, and sends no write',
    async (text) => {
      const { definition } = await renderForm(createFormDefinitionFixture(), { answersFieldEvents: true });

      const age = await enterAge(definition, text);

      expect(age).toHaveProperty('value', text);
      fireEvent.click(formActionButton('Save'));
      const summary = await findValidationSummary();
      await flush();
      expect(calls.filter((call) => call.method === 'PUT')).toEqual([]);
      const items = summaryItemsFor(summary, promptOf(fieldByName('Age', definition)));
      expect(items.map((item) => item.textContent?.includes(clientMessages.numberExpected))).toEqual([true]);
      expect(within(summary).getAllByRole('listitem')).toHaveLength(1);
      expect(descriptionOf(age)).toContain(clientMessages.numberExpected);
      expect(age).toHaveProperty('value', text);
    },
  );

  // Legacy's control_Leave clears a number box holding a negative sign or decimal separator
  // and no digit, so the field is left empty, with no error, before its FieldAfter.
  it.each<[string]>([['-'], ['.'], ['-.'], ['-abc']])(
    'clears the text %j in Age once it loses focus, raises FieldAfter with Age empty, and saves Age as null',
    async (text) => {
      const { definition } = await renderForm(createFormDefinitionFixture(), { answersFieldEvents: true });
      expect(RECORD.values.Age).toBe(31);
      replyToWrite({ kind: 'saved', record: savedWith(RECORD, { Age: null }) });

      const age = await enterAge(definition, text);

      expect(age).toHaveProperty('value', '');
      const fieldAfter = await waitFor(() =>
        present(
          calls.find(
            (call) =>
              call.method === 'POST' &&
              call.body?.includes('"event":"FieldAfter"') === true &&
              call.body.includes('"fieldName":"Age"'),
          ),
          'the FieldAfter event of Age',
        ),
      );
      expect(writtenValues(fieldAfter).Age).toBeNull();
      fireEvent.click(formActionButton('Save'));
      const update = await waitFor(() => present(calls.find((call) => call.method === 'PUT'), 'the update'));
      expect(update.body).toBe('{"values":{"Age":null}}');
      const status = within(screen.getByRole('toolbar', { name: 'Form actions' })).getByRole('status');
      await waitFor(() => {
        expect(status.textContent).toBe('Changes saved.');
      });
      expect(screen.queryByRole('heading', { name: 'Please correct the following' })).toBeNull();
      expect(age).toHaveProperty('value', '');
    },
  );

  // What leaving the box emits and raises, in order, with NumberInput holding its own value as
  // the editor does: only text with a sign or separator and no digit is cleared, to null, and
  // only before FieldAfter is raised.
  it.each<[string, string, FieldValue[]]>([
    ['-', '', ['-', null]],
    ['.', '', ['.', null]],
    ['-.', '', ['-.', null]],
    ['-abc', '', ['-abc', null]],
    ['-5', '-5', [-5]],
    ['37.', '37', [37]],
    ['abc', 'abc', ['abc']],
  ])('leaves the text %j showing %j, having emitted %j before FieldAfter', (text, shown, emitted) => {
    const age = fieldByName('Age');
    const events: unknown[] = [];
    function HeldNumber() {
      const [value, setValue] = useState<FieldValue>(null);
      return (
        <NumberInput
          field={age}
          value={value}
          disabled={false}
          isProtected={false}
          errors={[]}
          onChange={(next) => {
            events.push({ change: next });
            setValue(next);
          }}
          onBlur={() => {
            events.push('FieldAfter');
          }}
        />
      );
    }
    render(<HeldNumber />);
    const input = screen.getByLabelText(promptOf(age));

    act(() => {
      input.focus();
    });
    fireEvent.change(input, { target: { value: text } });
    act(() => {
      input.blur();
    });

    expect(input).toHaveProperty('value', shown);
    expect(events).toEqual([...emitted.map((value) => ({ change: value })), 'FieldAfter']);
  });
});

describe('a date field with a date mask is entered and shown in its mask', () => {
  /** Focuses the date `name`, types `text` and leaves it, as a user does; FieldBefore and FieldAfter are raised. */
  async function enterDate(definition: FormDefinition, name: string, text: string): Promise<HTMLElement> {
    // Installing the record moves focus without raising an event; the case's own focus comes after it.
    await flush();
    const control = controlOf(definition, name);
    act(() => {
      control.focus();
    });
    fireEvent.change(control, { target: { value: text } });
    act(() => {
      control.blur();
    });
    return control;
  }

  /** The values the FieldAfter event of `name` carried, once it is sent. */
  async function fieldAfterValues(name: string): Promise<Record<string, unknown>> {
    const fieldAfter = await waitFor(() =>
      present(
        calls.find(
          (call) =>
            call.method === 'POST' &&
            call.body?.includes('"event":"FieldAfter"') === true &&
            call.body.includes(`"fieldName":${JSON.stringify(name)}`),
        ),
        `the FieldAfter event of ${name}`,
      ),
    );
    return writtenValues(fieldAfter);
  }

  // RECORD's DOB is 1980-05-01, so each entry is an edit. Text that follows the mask MM-DD-YYYY
  // and names a real day is that wire date; blank text is no value; text typed in the wire form
  // is that date too, and leaving the field shows it in the mask.
  it.each<{ typed: string; shown: string; sent: string | null }>([
    { typed: '05-11-2011', shown: '05-11-2011', sent: '2011-05-11' },
    { typed: '02-29-2000', shown: '02-29-2000', sent: '2000-02-29' },
    { typed: '2011-05-11', shown: '05-11-2011', sent: '2011-05-11' },
    { typed: '', shown: '', sent: null },
    { typed: '   ', shown: '', sent: null },
  ])(
    'saves DOB typed as $typed as $sent, raises FieldAfter with it, and shows $shown once it is left',
    async ({ typed, shown, sent }) => {
      const { definition } = await renderForm(createFormDefinitionFixture(), { answersFieldEvents: true });
      replyToWrite({ kind: 'saved', record: savedWith(RECORD, { DOB: sent }) });

      const dob = await enterDate(definition, 'DOB', typed);

      expect(controlValue(dob)).toBe(shown);
      expect((await fieldAfterValues('DOB')).DOB).toBe(sent);
      fireEvent.click(formActionButton('Save'));
      const update = await waitFor(() => present(calls.find((call) => call.method === 'PUT'), 'the update'));
      expect(update.body).toBe(`{"values":{"DOB":${JSON.stringify(sent)}}}`);
      const status = within(screen.getByRole('toolbar', { name: 'Form actions' })).getByRole('status');
      await waitFor(() => {
        expect(status.textContent).toBe('Changes saved.');
      });
      await flush();
      // The saved record holds the wire date, shown in the mask.
      expect(controlValue(dob)).toBe(shown);
    },
  );

  // Partial or impossible text is kept as typed and reported in the mask's terms; a real day
  // outside the field's bounds or the datetime range is reported with its bounds in the mask.
  it.each<{ name: string; typed: string; message: string }>([
    { name: 'DOB', typed: '05-1', message: 'Enter a valid date (MM-DD-YYYY).' },
    { name: 'DOB', typed: '13-45-2011', message: 'Enter a valid date (MM-DD-YYYY).' },
    { name: 'DOB', typed: '02-30-2011', message: 'Enter a valid date (MM-DD-YYYY).' },
    { name: 'DOB', typed: '2011/05/11', message: 'Enter a valid date (MM-DD-YYYY).' },
    { name: 'DOB', typed: '5-11-2011', message: 'Enter a valid date (MM-DD-YYYY).' },
    { name: 'DOB', typed: '12-31-1752', message: 'Enter a date between 01-01-1753 and 12-31-9999.' },
    { name: 'DateofInterview', typed: '01-14-2011', message: 'Enter a value from 01-15-2011 to 12-31-2012.' },
    { name: 'OnsetDate', typed: '01-01-2013', message: 'Enter a value from 01-15-2011 to 12-31-2012.' },
  ])(
    'keeps $name typed as $typed once it is left, and Save shows $message and sends no write',
    async ({ name, typed, message }) => {
      const { definition } = await renderForm(createFormDefinitionFixture(), { answersFieldEvents: true });

      const control = await enterDate(definition, name, typed);

      expect(controlValue(control)).toBe(typed);
      fireEvent.click(formActionButton('Save'));
      const summary = await findValidationSummary();
      await flush();
      expect(calls.filter((call) => call.method === 'PUT')).toEqual([]);
      const items = summaryItemsFor(summary, promptOf(fieldByName(name, definition)));
      expect(items.map((item) => item.textContent?.includes(message))).toEqual([true]);
      expect(within(summary).getAllByRole('listitem')).toHaveLength(1);
      // The format hint and the error both describe the box, the hint first.
      expect(control.getAttribute('aria-describedby')).toBe(`${fieldControlId(name)}-hint ${fieldErrorId(name)}`);
      expect(descriptionOf(control)).toBe(`Format: MM-DD-YYYY ${message}`);
      expect(controlValue(control)).toBe(typed);
    },
  );
});

describe('a tabIndex beyond the safe range', () => {
  // metaFields.TabIndex is decimal(18,0), so a field can sit past Number.MAX_SAFE_INTEGER.
  // The client reads a tabIndex as JSON.parse does, its nearest double, so the page and Find
  // order it among ordinary indexes as a number. Title's 9007199254740993 reads as 2^53.
  const LARGE_TAB_INDEXES: ReadonlyMap<string, string> = new Map([
    ['Title', '9007199254740993'],
    ['CaseID', '9007199254741000'],
  ]);

  /** The fixture with those page-1 tabIndexes as the client holds them, and its body text as the API writes it. */
  function withLargeTabIndexes(): { definition: FormDefinition; text: string } {
    const definition = createFormDefinitionFixture();
    for (const page of definition.pages) {
      page.fields = page.fields.map((field) => {
        const token = LARGE_TAB_INDEXES.get(field.name);
        return token === undefined ? field : { ...field, tabIndex: Number(token) };
      });
    }
    let text = JSON.stringify(definition);
    for (const token of LARGE_TAB_INDEXES.values()) {
      text = text.replace(`"tabIndex":${String(Number(token))}`, `"tabIndex":${token}`);
      expect(text).toContain(`"tabIndex":${token}`);
    }
    return { definition, text };
  }

  it('renders the page in tabIndex order and lists the fields in Find in that order', async () => {
    const { definition, text } = withLargeTabIndexes();
    const [firstPage] = definition.pages;
    await renderForm(definition, { definitionText: text });

    const rendered = renderedFieldNames(panelOf(firstPage));
    expect(rendered).toEqual([...firstPage.fields].sort(byTabIndex).map((field) => field.name));
    expect(rendered.slice(-2)).toEqual(['Title', 'CaseID']);

    fireEvent.click(within(screen.getByRole('toolbar', { name: 'Form actions' })).getByRole('button', { name: 'Find' }));

    const picker = within(await screen.findByRole('dialog', { name: 'Find record' })).getByRole('group', {
      name: 'Fields to search',
    });
    const picked = within(picker)
      .getAllByRole('checkbox')
      .map((box) => {
        const nameId = present(box.getAttribute('aria-labelledby'), 'aria-labelledby of a Find checkbox');
        return present(document.getElementById(nameId), `element ${nameId}`).textContent;
      });
    expect(picked).toEqual([
      ...allFields(definition)
        .filter(isDataField)
        .map((field) => field.name),
      'GlobalRecordId',
      'RecStatus',
    ]);
  });
});

describe('RecordNavigator pending and busy states', () => {
  // A pending navigation load disables no control: useRecordNavigation drops the responses
  // of a load a newer action overtakes, so a press mid-load supersedes it. The toolbar and
  // its status are marked busy and the load is announced instead. A control is disabled
  // only while its operation is unavailable: an edge, an empty set, or a save in progress.
  type NavigatorState = RecordNavigatorProps['navigation'];

  const LOADING = 'Loading…';
  const BUTTONS = ['First', 'Previous', 'Next', 'Last', 'Go', 'New'] as const;

  // Three records, so record 1 has a Next, and Last can overtake a pending Next. Each holds
  // a different Sex, so the legal-values select changes its selection with the record.
  const LAST_NAMES = ['Lovelace', 'Hopper', 'Noether'];
  const SEXES = ['M-Male', 'F-Female', 'U-Unknown'];
  const RECORDS: readonly RecordDto[] = LAST_NAMES.map((lastName, index) =>
    recordFixture({
      globalRecordId: `3f2b8c1e-5d4a-4c1b-9e7f-${String(101 + index).padStart(12, '0')}`,
      uniqueKey: index + 1,
      position: index + 1,
      count: LAST_NAMES.length,
      values: { LastName: lastName, Sex: SEXES[index] },
    }),
  );

  interface HoldingApi {
    readonly fetch: typeof fetch;
    /** Holds the next GET of `at/{position}` until `release`. */
    readonly holdAt: (position: number) => void;
    /** Requests held so far and not yet released. */
    readonly heldCount: () => number;
    /** Answers every held request. */
    readonly release: () => void;
  }

  /**
   * The in-memory API over the three records. The definition and every other request go
   * to fakeApi, which records an unrouted one. A held request still rejects with an
   * AbortError when its signal aborts, as fetch does.
   */
  function holdingApi(definition: FormDefinition): HoldingApi {
    const base = fakeApi(definition, [...RECORDS]);
    const records = `/api/forms/${definition.viewId}/records`;
    const heldPaths = new Set<string>();
    const waiting: (() => void)[] = [];

    function bodyOf(path: string): unknown {
      if (path === records) {
        const page: RecordPage = {
          items: RECORDS.map((record) => recordSummaryFixture(record, definition)),
          total: RECORDS.length,
          page: 1,
          pageSize: 50,
        };
        return page;
      }
      if (path === `${records}/count`) {
        const count: RecordCountResponse = { count: RECORDS.length };
        return count;
      }
      for (const [index, record] of RECORDS.entries()) {
        const byId = `${records}/${record.globalRecordId}`;
        if (path === `${records}/at/${index + 1}` || path === byId) {
          return record;
        }
        if (path === `${byId}/position`) {
          const position: NavigationPosition = { position: index + 1, count: RECORDS.length };
          return position;
        }
      }
      return null;
    }

    function hold(signal: AbortSignal): Promise<void> {
      return new Promise((resolve, reject) => {
        const abort = (): void => {
          reject(new DOMException('The operation was aborted.', 'AbortError'));
        };
        if (signal.aborted) {
          abort();
          return;
        }
        signal.addEventListener('abort', abort, { once: true });
        waiting.push(resolve);
      });
    }

    return {
      fetch: async (input, init) => {
        const request = input instanceof Request ? input : new Request(input, init);
        const url = new URL(request.url);
        const body = request.method === 'GET' ? bodyOf(url.pathname) : null;
        if (body === null) {
          return base(request);
        }
        calls.push({ method: request.method, url, body: null });
        if (heldPaths.delete(url.pathname)) {
          await hold(request.signal);
        }
        return jsonResponse(200, body);
      },
      holdAt: (position) => {
        heldPaths.add(`${records}/at/${position}`);
      },
      heldCount: () => waiting.length,
      release: () => {
        for (const resolve of waiting.splice(0)) {
          resolve();
        }
      },
    };
  }

  /** Renders the form over the three records; resolves once record 1 of 3 and the grid have loaded. */
  async function renderThreeRecords(api: HoldingApi, definition: FormDefinition): Promise<HTMLElement> {
    const client = createApiClient({ baseUrl: 'http://localhost', fetch: api.fetch });
    render(
      <FormView
        viewId={definition.viewId}
        client={client}
        onExit={vi.fn<() => void>()}
        onFormNotFound={vi.fn<() => void>()}
      />,
    );
    const navigator = await screen.findByRole('toolbar', { name: 'Record navigation' });
    await within(navigator).findByText('1 of 3');
    await within(screen.getByRole('region', { name: 'Records' })).findByText('3 records');
    return navigator;
  }

  /** Answers the held requests and lets their responses run through to the render. */
  async function releaseAndSettle(api: HoldingApi): Promise<void> {
    await act(async () => {
      api.release();
      await new Promise((resolve) => setTimeout(resolve, 0));
    });
  }

  /** Whether each navigator control is enabled, by accessible name. */
  function enabledControls(navigator: HTMLElement): Record<string, boolean> {
    const states: Record<string, boolean> = {};
    for (const name of BUTTONS) {
      states[name] = !isDisabled(within(navigator).getByRole('button', { name }));
    }
    states['Go to record'] = !isDisabled(within(navigator).getByRole('textbox', { name: 'Go to record' }));
    return states;
  }

  function allControls(enabled: boolean): Record<string, boolean> {
    return Object.fromEntries([...BUTTONS, 'Go to record'].map((name) => [name, enabled]));
  }

  function lastNameValue(): string {
    return controlValue(screen.getByLabelText('Last Name'));
  }

  /**
   * The pending-load announcer, found by role: the one status that directly follows the
   * navigator toolbar. Other statuses, such as the navigator's "[n] of N", the form actions'
   * Saving… and the record grid's, sit elsewhere.
   */
  function loadingAnnouncer(navigator: HTMLElement): HTMLElement {
    const following = screen.getAllByRole('status').filter((element) => element.previousElementSibling === navigator);
    expect(following, 'statuses following the navigator toolbar').toHaveLength(1);
    expect(navigator.contains(following[0])).toBe(false);
    return following[0];
  }

  /**
   * Asserts the announcer reads `text` as a polite status of its own, beside the toolbar and
   * outside every aria-busy subtree, while the toolbar's one status stays `status`, the
   * "[n] of N" text. Returns the announcer.
   */
  function expectAnnouncer(navigator: HTMLElement, status: HTMLElement, text: string): HTMLElement {
    const announcer = loadingAnnouncer(navigator);
    expect(announcer.textContent).toBe(text);
    expect(announcer.getAttribute('role')).toBe('status');
    expect(announcer.getAttribute('aria-live')).toBe('polite');
    expect(announcer.closest('[aria-busy="true"]')).toBeNull();
    expect(navigator.nextElementSibling).toBe(announcer);
    expect(within(navigator).getAllByRole('status')).toEqual([status]);
    return announcer;
  }

  it('keeps every available control enabled during a pending load, and marks and announces the load', async () => {
    const definition = createFormDefinitionFixture();
    const api = holdingApi(definition);
    const navigator = await renderThreeRecords(api, definition);
    const status = within(navigator).getByRole('status');
    expect(navigator.getAttribute('aria-busy')).toBeNull();
    expect(status.getAttribute('aria-busy')).toBeNull();

    api.holdAt(2);
    fireEvent.click(within(navigator).getByRole('button', { name: 'Next' }));
    await waitFor(() => {
      expect(api.heldCount()).toBe(1);
    });

    expect(navigator.getAttribute('aria-busy')).toBe('true');
    // The status still shows the open record's coordinates, so it is busy rather than current.
    expect(status.getAttribute('aria-busy')).toBe('true');
    expect(status.textContent).toBe('1 of 3');
    // The selector styles.css mutes the pending status with (jsdom loads no stylesheet).
    expect(status.matches('.record-navigator [role="status"][aria-busy="true"]')).toBe(true);
    // The announcement follows the toolbar, outside every aria-busy subtree, since assistive
    // technology may hold back a busy subtree's updates until the load has ended.
    const announcement = expectAnnouncer(navigator, status, LOADING);
    // Every control is enabled: Previous counts from the pending target, record 2, as a press
    // of it does, although the status still shows record 1.
    expect(enabledControls(navigator)).toEqual(allControls(true));

    await releaseAndSettle(api);

    expect(status.textContent).toBe('2 of 3');
    expect(lastNameValue()).toBe('Hopper');
    expect(navigator.getAttribute('aria-busy')).toBeNull();
    expect(status.getAttribute('aria-busy')).toBeNull();
    // The same live region stays mounted beside the toolbar, emptied, for the next load to
    // announce into.
    expect(expectAnnouncer(navigator, status, '')).toBe(announcement);
    expect(enabledControls(navigator)).toEqual(allControls(true));
  });

  // Previous counts from the pending record 2, so it goes back to record 1.
  it.each([
    { press: 'Last', status: '3 of 3', lastName: 'Noether' },
    { press: 'Previous', status: '1 of 3', lastName: 'Lovelace' },
  ])(
    'lets $press pressed during a pending Next from record 1 supersede it, and ignores the overtaken response',
    async ({ press, status: landed, lastName }) => {
      const definition = createFormDefinitionFixture();
      const api = holdingApi(definition);
      const navigator = await renderThreeRecords(api, definition);
      const status = within(navigator).getByRole('status');

      api.holdAt(2);
      fireEvent.click(within(navigator).getByRole('button', { name: 'Next' }));
      await waitFor(() => {
        expect(api.heldCount()).toBe(1);
      });
      fireEvent.click(within(navigator).getByRole('button', { name: press }));

      await waitFor(() => {
        expect(status.textContent).toBe(landed);
        expect(navigator.getAttribute('aria-busy')).toBeNull();
      });
      expect(lastNameValue()).toBe(lastName);

      // The read of record 2 answers last and installs nothing.
      await releaseAndSettle(api);

      expect(status.textContent).toBe(landed);
      expect(lastNameValue()).toBe(lastName);
      expect(navigator.getAttribute('aria-busy')).toBeNull();
      expectAnnouncer(navigator, status, '');
    },
  );

  it('disables every data field while a pending load replaces the record, without marking any protected (0.3.6)', async () => {
    const definition = createFormDefinitionFixture();
    const api = holdingApi(definition);
    const navigator = await renderThreeRecords(api, definition);
    const body = document.body;
    const writable = dataFieldNames.filter((name) => !readOnlyFieldNames.includes(name));
    expect(writable).not.toHaveLength(0);

    /** The field's control, found by its prompt within the field's own wrapper. */
    function labelledControl(name: string): HTMLElement {
      const field = fieldByName(name, definition);
      const wrapper = within(wrapperOf(body, name));
      // A radio group is named by its legend; a panel other than the selected one is hidden.
      return field.fieldType === 'option'
        ? wrapper.getByRole('radiogroup', { name: promptOf(field), hidden: true })
        : wrapper.getByLabelText(promptOf(field));
    }

    /** Whether the labelled control and each native control the field renders is disabled. */
    function disabledStates(name: string): boolean[] {
      const controls = Array.from(wrapperOf(body, name).querySelectorAll(NATIVE_CONTROLS));
      return [labelledControl(name), ...controls].map((control) => isDisabled(control));
    }

    function protectedWrappers(): (string | undefined)[] {
      return Array.from(body.querySelectorAll<HTMLElement>('[data-field-name].field--protected')).map(
        (wrapper) => wrapper.dataset.fieldName,
      );
    }

    // Before the load only read-only metadata and the unsupported field are protected.
    const protectedBefore = protectedWrappers();
    expect(new Set(protectedBefore)).toEqual(
      new Set(
        allFields(definition)
          .filter((field) => field.fieldType === 'unsupported' || readOnlyFieldNames.includes(field.name))
          .map((field) => field.name),
      ),
    );
    expect(body.querySelectorAll('.field--disabled')).toHaveLength(0);

    api.holdAt(2);
    fireEvent.click(within(navigator).getByRole('button', { name: 'Next' }));
    await waitFor(() => {
      expect(api.heldCount()).toBe(1);
    });

    // The record being left is frozen: every control refuses input, yet nothing becomes
    // protected, so no wrapper takes the protected band meanwhile.
    expect(lastNameValue()).toBe('Lovelace');
    for (const name of dataFieldNames) {
      expect(disabledStates(name), name).not.toContain(false);
    }
    expect(protectedWrappers()).toEqual(protectedBefore);
    for (const name of writable) {
      expect(wrapperOf(body, name).className, name).toBe('field field--disabled');
    }

    await releaseAndSettle(api);

    expect(lastNameValue()).toBe('Hopper');
    for (const name of writable) {
      expect(disabledStates(name), name).not.toContain(true);
      expect(wrapperOf(body, name).className, name).toBe('field');
    }
    expect(protectedWrappers()).toEqual(protectedBefore);
    expect(body.querySelectorAll('.field--disabled')).toHaveLength(0);
  });

  it('replaces the options of a legal-values select a pending load disables and re-enables, keeping the select, its label and its selection, with no field event', async () => {
    const definition = createFormDefinitionFixture();
    const api = holdingApi(definition);
    const navigator = await renderThreeRecords(api, definition);
    const select = screen.getByRole<HTMLSelectElement>('combobox', { name: 'Sex' });
    const optionsOf = (): HTMLOptionElement[] => Array.from(select.options);
    const enabledOptions = optionsOf();
    expect(controlValue(select)).toBe('M-Male');

    api.holdAt(2);
    fireEvent.click(within(navigator).getByRole('button', { name: 'Next' }));
    await waitFor(() => {
      expect(api.heldCount()).toBe(1);
    });

    // Frozen: the same select, disabled and still showing record 1, over new options.
    expect(screen.getByRole('combobox', { name: 'Sex' })).toBe(select);
    expect(isDisabled(select)).toBe(true);
    expect(controlValue(select)).toBe('M-Male');
    const frozenOptions = optionsOf();
    for (const option of frozenOptions) {
      expect(enabledOptions).not.toContain(option);
    }

    await releaseAndSettle(api);

    // Re-enabled on record 2: the select stays and is still labelled by its prompt, and no
    // option from before either change of its disabled state survives, so none can carry
    // an accessibility state from then. The new options are enabled and the record's value
    // is the one selected.
    expect(lastNameValue()).toBe('Hopper');
    expect(screen.getByRole('combobox', { name: 'Sex' })).toBe(select);
    expect(isDisabled(select)).toBe(false);
    const options = optionsOf();
    expect(options.map((option) => option.textContent)).toEqual(['No selection', 'M-Male', 'F-Female', 'U-Unknown']);
    for (const option of options) {
      expect(enabledOptions).not.toContain(option);
      expect(frozenOptions).not.toContain(option);
      expect(isDisabled(option)).toBe(false);
    }
    expect(controlValue(select)).toBe('F-Female');
    expect(within(select).getByRole('option', { selected: true })).toBe(options[2]);
    expect(calls.filter((call) => call.url.pathname.endsWith('/checkcode/field-event')).map(requestLine)).toEqual([]);
  });

  it('disables every control while the editor saves, and marks the toolbar busy without announcing a load', () => {
    const navigation: NavigatorState = {
      count: 3,
      statusText: '2 of 3',
      goToError: null,
      loading: false,
      transitioning: false,
      recordGeneration: 1,
      canFirst: true,
      canPrevious: true,
      canNext: true,
      canLast: true,
      canGoTo: true,
      first: vi.fn<NavigatorState['first']>(),
      previous: vi.fn<NavigatorState['previous']>(),
      next: vi.fn<NavigatorState['next']>(),
      last: vi.fn<NavigatorState['last']>(),
      goTo: vi.fn<NavigatorState['goTo']>(),
      newRecord: vi.fn<NavigatorState['newRecord']>(),
    };
    render(<RecordNavigator navigation={navigation} busy />);
    const navigator = screen.getByRole('toolbar', { name: 'Record navigation' });
    const status = within(navigator).getByRole('status');

    expect(enabledControls(navigator)).toEqual(allControls(false));
    expect(navigator.getAttribute('aria-busy')).toBe('true');
    expect(status.getAttribute('aria-busy')).toBeNull();
    expect(status.textContent).toBe('2 of 3');
    expectAnnouncer(navigator, status, '');
  });

  it.each<{ label: string; text: string }>([
    { label: 'a number outside 1..N, which useRecordNavigation rejects as goToError', text: '999999' },
    { label: 'text that is no record number, which the navigator rejects itself', text: 'abc' },
  ])('shows the go-to rejection of $label only while the box holds the text submitted', async ({ text }) => {
    const definition = createFormDefinitionFixture();
    const api = holdingApi(definition);
    const navigator = await renderThreeRecords(api, definition);
    const box = within(navigator).getByRole('textbox', { name: 'Go to record' });

    /** Asserts the rejection and the box's invalid state are both shown, or both absent. */
    function expectRejection(shown: boolean): void {
      const alert = within(navigator).queryByRole('alert');
      if (shown) {
        expect(alert?.textContent).toBe('Enter a record number from 1 to 3.');
        expect(box.getAttribute('aria-invalid')).toBe('true');
        expect(box.getAttribute('aria-describedby')).toBe(alert?.id);
      } else {
        expect(alert).toBeNull();
        expect(box.getAttribute('aria-invalid')).toBeNull();
        expect(box.getAttribute('aria-describedby')).toBeNull();
      }
    }

    fireEvent.change(box, { target: { value: text } });
    expectRejection(false);
    fireEvent.keyDown(box, { key: 'Enter' });
    expectRejection(true);

    // Any edit leaves a message that describes other text, so it goes.
    fireEvent.change(box, { target: { value: `${text}1` } });
    expectRejection(false);
    fireEvent.keyDown(box, { key: 'Enter' });
    expectRejection(true);
    fireEvent.change(box, { target: { value: '' } });
    expectRejection(false);

    // Submitting the same text again shows its rejection again; the record never changed.
    fireEvent.change(box, { target: { value: text } });
    fireEvent.keyDown(box, { key: 'Enter' });
    expectRejection(true);
    expect(within(navigator).getByRole('status').textContent).toBe('1 of 3');
  });

  it('shows no go-to rejection for a valid number whose save before leaving client validation refuses', async () => {
    const definition = createFormDefinitionFixture();
    const api = holdingApi(definition);
    const navigator = await renderThreeRecords(api, definition);
    const box = within(navigator).getByRole('textbox', { name: 'Go to record' });
    // Past the fixture's Age upper bound of 120, so the save before leaving stops at client validation.
    edit(definition, 'Age', '121');

    fireEvent.change(box, { target: { value: '999999' } });
    fireEvent.keyDown(box, { key: 'Enter' });
    expect(within(navigator).getByRole('alert').textContent).toBe('Enter a record number from 1 to 3.');

    fireEvent.change(box, { target: { value: '2' } });
    fireEvent.keyDown(box, { key: 'Enter' });
    await findValidationSummary();
    await flush();

    // The refused leave is no rejection of 2, and the earlier one describes other text.
    expect(within(navigator).queryByRole('alert')).toBeNull();
    expect(box.getAttribute('aria-invalid')).toBeNull();
    expect(controlValue(box)).toBe('2');
    expect(writeCalls().map(requestLine)).toEqual([]);
    expect(controlValue(controlOf(definition, 'Age'))).toBe('121');
    expect(within(navigator).getByRole('status').textContent).toBe('1 of 3');
  });
});

describe('FormView Find record', () => {
  // The dialog renders into <body>, so its controls are found through the screen.
  async function openFind(): Promise<HTMLElement> {
    const formActions = screen.getByRole('toolbar', { name: 'Form actions' });
    fireEvent.click(within(formActions).getByRole('button', { name: 'Find' }));
    return screen.findByRole('dialog', { name: 'Find record' });
  }

  /**
   * Picks `name` in the field picker and returns its criterion's value box, labelled by
   * the field's prompt with `(name)` after it, or by the name alone when that is the prompt.
   */
  function pick(dialog: HTMLElement, name: string): HTMLInputElement {
    fireEvent.click(within(dialog).getByRole('checkbox', { name }));
    const box = within(dialog).getByRole('textbox', {
      name: (label) => label === name || label.endsWith(` (${name})`),
    });
    if (!(box instanceof HTMLInputElement)) {
      throw new Error(`the value box of ${name} is not an input`);
    }
    return box;
  }

  /** Text of each element the control's aria-describedby names, in order. */
  function descriptionsOf(control: HTMLElement): string[] {
    const ids = (control.getAttribute('aria-describedby') ?? '').split(' ').filter((id) => id !== '');
    return ids.map((id) => present(document.getElementById(id), `element #${id}`).textContent ?? '');
  }

  // Text kinds other than a Phone Number ignore a comparison prefix on the server, so their
  // hints offer none. A Phone Number arrives as text, with or without a mask, and has no
  // wildcard path: a masked text hint offers no wildcard, and an unmasked one offers it only
  // with the Phone Number exception. Legal values are never a Phone Number, so theirs is plain.
  const COMPARISONS = ['>=', '<=', '<>', '<', 'compare'];
  const PLAIN_WILDCARD = '* as a wildcard (e.g. Sm*)';
  const PHONE_EXCEPTION = '* is a wildcard (e.g. Sm*), except in a phone number, where it is matched as typed.';
  /** The fixture with `PhoneNumber` unmasked, as a Phone Number whose metadata mask is blank arrives. */
  const unmaskedPhone = (): FormDefinition =>
    withField('PhoneNumber', (field) => ({ ...field, validation: { ...field.validation, pattern: null } }));
  const unlistedLegalValues = (): FormDefinition => withField('Sex', (field) => ({ ...field, options: [] }));
  it.each<{
    kind: string;
    name: string;
    definition?: () => FormDefinition;
    includes: readonly string[];
    /** Fragments the hint must not hold outside the Phone Number exception sentence. */
    excludes: readonly string[];
  }>([
    {
      kind: 'unmasked text',
      name: 'LastName',
      includes: ['whole value', 'a or b', '> alone finds empty values', PHONE_EXCEPTION],
      excludes: [...COMPARISONS, '*', 'wildcard'],
    },
    {
      kind: 'unmasked phone number',
      name: 'PhoneNumber',
      definition: unmaskedPhone,
      includes: ['whole value', 'a or b', '> alone finds empty values', PHONE_EXCEPTION],
      excludes: [...COMPARISONS, '*', 'wildcard'],
    },
    {
      kind: 'masked text',
      name: 'PhoneNumber',
      includes: ['whole value', 'a or b', '> alone finds empty values'],
      excludes: [...COMPARISONS, '*', 'wildcard', 'phone number'],
    },
    {
      kind: 'multiline',
      name: 'PatientAddress',
      includes: ['whole text', 'first 255 characters', 'a or b', PLAIN_WILDCARD, '> alone finds empty values'],
      excludes: [...COMPARISONS, 'phone number'],
    },
    {
      kind: 'legal values',
      name: 'Sex',
      includes: [
        'whole value',
        'a or b',
        PLAIN_WILDCARD,
        '> alone finds empty values',
        'Allowed values: M-Male, F-Female, U-Unknown.',
      ],
      excludes: [...COMPARISONS, 'phone number'],
    },
    {
      kind: 'unlisted legal values',
      name: 'Sex',
      definition: unlistedLegalValues,
      includes: ['whole value', 'a or b', PLAIN_WILDCARD, '> alone finds empty values'],
      excludes: [...COMPARISONS, 'phone number', 'Allowed values'],
    },
    { kind: 'number', name: 'CaseID', includes: ['>=5', 'Leave empty to find missing values'], excludes: [] },
    { kind: 'date', name: 'DOB', includes: ['YYYY-MM-DD', '>=2011-01-15'], excludes: [] },
  ])(
    'describes the $kind criterion $name by the syntax the server applies',
    async ({ name, definition, includes, excludes }) => {
      await renderForm(definition?.());
      const box = pick(await openFind(), name);

      const [hint] = descriptionsOf(box);
      for (const fragment of includes) {
        expect(hint, name).toContain(fragment);
      }
      const outsideException = hint.replace(PHONE_EXCEPTION, '');
      for (const fragment of excludes) {
        expect(outsideException, name).not.toContain(fragment);
      }
    },
  );

  // A criterion is labelled by the field's prompt on one line, without a closing colon,
  // then the field name, or by the name alone when the prompt says only that; Remove
  // still names the field.
  it.each<{ name: string; definition?: () => FormDefinition; label: string }>([
    { name: 'LastName', label: 'Last Name (LastName)' },
    { name: 'Age', label: 'Age' },
    { name: 'Lab', label: 'Lab' },
    {
      name: 'OnsetDate',
      definition: () => withField('OnsetDate', (field) => ({ ...field, prompt: 'Date of onset of\r\nfirst  symptoms: ' })),
      label: 'Date of onset of first symptoms (OnsetDate)',
    },
    {
      name: 'PatientAddress',
      definition: () => withField('PatientAddress', (field) => ({ ...field, prompt: null })),
      label: 'PatientAddress',
    },
    { name: 'GlobalRecordId', label: 'Global record ID (GlobalRecordId)' },
    { name: 'RecStatus', label: 'Record status (RecStatus)' },
  ])('labels the criterion of $name $label', async ({ name, definition, label }) => {
    await renderForm(definition?.());
    const dialog = await openFind();
    fireEvent.click(within(dialog).getByRole('checkbox', { name }));

    const box = within(dialog).getByRole('textbox', { name: label });
    const row = present(box.parentElement, `criterion row of ${name}`);
    expect(within(row).getByRole('button', { name: `Remove ${name}` })).toBeTruthy();
  });

  // A field the server flags unsupported holds no searchable value whatever its wire type,
  // so the picker leaves it out, as the editor does, and still offers every other data
  // field, then the system fields, in order.
  it.each<{ name: string; fieldType: FieldType }>([
    { name: 'LastName', fieldType: 'text' },
    { name: 'PatientAddress', fieldType: 'multiline' },
    { name: 'Sex', fieldType: 'legalValues' },
    { name: 'CaseID', fieldType: 'number' },
    { name: 'DOB', fieldType: 'date' },
    { name: 'Asian', fieldType: 'checkbox' },
    { name: 'IllnessSeverity', fieldType: 'option' },
  ])('offers no criterion for the $fieldType field $name flagged unsupported', async ({ name, fieldType }) => {
    const definition = withField(name, (field) => ({ ...field, unsupported: true, unsupportedType: 'Grid' }));
    expect(fieldByName(name, definition).fieldType).toBe(fieldType);
    await renderForm(definition);

    const picker = within(await openFind()).getByRole('group', { name: 'Fields to search' });
    expect(within(picker).queryByRole('checkbox', { name })).toBeNull();
    const offered = within(picker)
      .getAllByRole('checkbox')
      .map((box) => {
        const nameId = present(box.getAttribute('aria-labelledby'), 'aria-labelledby of a Find checkbox');
        return present(document.getElementById(nameId), `element ${nameId}`).textContent;
      });
    expect(offered).toEqual([
      ...allFields(definition)
        .filter((field) => isDataField(field) && field.name !== name)
        .map((field) => field.name),
      'GlobalRecordId',
      'RecStatus',
    ]);
  });

  /** The words of a picker name or prompt: its text split at each <wbr> inside the element. */
  function nameWords(nameElement: HTMLElement): string[] {
    const words = [''];
    for (const node of nameElement.childNodes) {
      if (node instanceof HTMLElement && node.tagName === 'WBR') {
        words.push('');
      } else if (node.nodeType === Node.TEXT_NODE) {
        words[words.length - 1] += node.textContent ?? '';
      } else {
        throw new Error(`unexpected ${node.nodeName} in ${nameElement.textContent ?? ''}`);
      }
    }
    return words;
  }

  // A long name wraps between its words, not inside one. Chrome reads a <wbr> as a space
  // in an accessible name, so the checkbox is named by the plain name and the copy shown,
  // which carries the <wbr> elements, is hidden from assistive technology.
  it.each<{ name: string; words: readonly string[] }>([
    { name: 'EatenLastTenDays', words: ['Eaten', 'Last', 'Ten', 'Days'] },
    { name: 'CDCIDNumber', words: ['CDCID', 'Number'] },
    { name: 'O157H7isolated', words: ['O', '157', 'H', '7', 'isolated'] },
    { name: 'first_name-2', words: ['first_', 'name-', '2'] },
    { name: 'Radiculoneuropathy', words: ['Radiculoneuropathy'] },
  ])('marks the word boundaries of the picker name $name', async ({ name, words }) => {
    await renderForm(withField('FeverTemp', (field) => ({ ...field, name })));
    const checkbox = within(await openFind()).getByRole('checkbox', { name });

    const nameId = present(checkbox.getAttribute('aria-labelledby'), 'aria-labelledby of the checkbox');
    const nameElement = present(document.getElementById(nameId), `element #${nameId}`);
    expect(nameWords(nameElement)).toEqual([name]);
    const shown = present(
      checkbox.closest('label')?.querySelector<HTMLElement>('[aria-hidden="true"]'),
      `shown name of ${name}`,
    );
    expect(shown.textContent).toBe(name);
    expect(nameWords(shown)).toEqual(words);
  });

  // A prompt wraps at its spaces and after a slash, backslash or underscore joining two
  // words, never inside a word; numbers keep their slashes. Chrome reads a <wbr> as a
  // space in an accessible description too, so the checkbox is described by the plain
  // prompt and the copy shown, which carries the <wbr> elements, is hidden from it.
  it.each<{ prompt: string; words: readonly string[] }>([
    { prompt: 'Encephalitis/Encephalomyelitis', words: ['Encephalitis/', 'Encephalomyelitis'] },
    { prompt: 'Native Hawaiian/Other Pacific Islander', words: ['Native Hawaiian/', 'Other Pacific Islander'] },
    { prompt: 'Lab\\Clinic_Name', words: ['Lab\\', 'Clinic_', 'Name'] },
    { prompt: 'Dose 1/2 on 01/15/2011, and/ or later', words: ['Dose 1/2 on 01/15/2011, and/ or later'] },
    { prompt: 'Highest fever temp', words: ['Highest fever temp'] },
  ])('marks the word breaks of the picker prompt $prompt', async ({ prompt, words }) => {
    await renderForm(withField('FeverTemp', (field) => ({ ...field, prompt })));
    const checkbox = within(await openFind()).getByRole('checkbox', { name: 'FeverTemp', description: prompt });

    const descriptionId = present(checkbox.getAttribute('aria-describedby'), 'aria-describedby of the checkbox');
    const description = present(document.getElementById(descriptionId), `element #${descriptionId}`);
    expect(description.hidden).toBe(true);
    expect(nameWords(description)).toEqual([prompt]);
    const shown = present(
      checkbox.closest('label')?.querySelector<HTMLElement>('small[aria-hidden="true"]'),
      `shown prompt of ${prompt}`,
    );
    expect(shown.textContent).toBe(prompt);
    expect(nameWords(shown)).toEqual(words);
  });

  /** Answers the searches in order, one answer each; a search past the last is left unrouted. */
  function searchRoute(...answers: readonly (() => Response | Promise<Response>)[]): Route {
    let next = 0;
    return (call) => {
      if (!isSearch(call) || next >= answers.length) {
        return null;
      }
      const answer = answers[next];
      next += 1;
      return answer();
    };
  }

  // The open record as a match: opening it needs only the read routes.
  const MATCH: SearchResult = {
    globalRecordId: RECORD.globalRecordId,
    uniqueKey: RECORD.uniqueKey,
    recStatus: RECORD.recStatus,
    position: 1,
    values: { LastName: 'Lovelace' },
  };

  function matches(...items: SearchResult[]): Response {
    const body: SearchResponse = { items, total: items.length, truncated: false };
    return jsonResponse(200, body);
  }

  function sentCriteria(call: Call): unknown {
    const body: unknown = JSON.parse(present(call.body, 'search body'));
    return typeof body === 'object' && body !== null && 'criteria' in body ? body.criteria : undefined;
  }

  /** The search's `excludeGlobalRecordId`; `undefined` when the body carries none. */
  function sentExclusion(call: Call): unknown {
    const body: unknown = JSON.parse(present(call.body, 'search body'));
    return typeof body === 'object' && body !== null && 'excludeGlobalRecordId' in body
      ? body.excludeGlobalRecordId
      : undefined;
  }

  // The status line's sentence for a search that left the open record out.
  const ON_SCREEN_NOT_LISTED = 'The record on screen is not listed.';
  const EARLIER_NOT_LISTED = 'The record that was on screen when you searched is not listed.';

  /** How the one result row offers itself: actionable rows carry no aria-disabled. */
  function resultState(dialog: HTMLElement) {
    const table = within(dialog).getByRole('table', { name: 'Matching records' });
    const open = within(table).getByRole('button', { name: 'Open record at position 1' });
    return {
      open: open.getAttribute('aria-disabled'),
      row: present(open.closest('tr'), 'result row').getAttribute('aria-disabled'),
      busy: table.getAttribute('aria-busy'),
      stale: present(table.parentElement, 'results wrapper').classList.contains('find-dialog__results--stale'),
    };
  }

  const ACTIONABLE = { open: null, row: null, busy: 'false', stale: false };
  const OUT_OF_DATE = { open: 'true', row: 'true', busy: 'false', stale: true };
  const PENDING = { open: 'true', row: 'true', busy: 'true', stale: true };

  /** Clicks the result's Open button and its row; the dialog stays open when they are inactive. */
  function tryToOpen(dialog: HTMLElement): void {
    const open = within(dialog).getByRole('button', { name: 'Open record at position 1' });
    fireEvent.click(open);
    fireEvent.click(present(open.closest('tr'), 'result row'));
  }

  it('opens results only while no search runs and the criteria are the ones sent', async () => {
    const second = deferred();
    const { definition } = await renderForm(undefined, {
      route: searchRoute(
        () => matches(MATCH),
        () => second.response,
      ),
    });
    const openByIdPath = `/api/forms/${definition.viewId}/records/${RECORD.globalRecordId}`;
    const opened = () => calls.filter((call) => call.method === 'GET' && call.url.pathname === openByIdPath);
    const dialog = await openFind();
    const box = pick(dialog, 'LastName');
    const search = within(dialog).getByRole('button', { name: 'Search' });

    fireEvent.change(box, { target: { value: 'Lovelace' } });
    fireEvent.click(search);
    await within(dialog).findByText('1 record matches.');
    expect(resultState(dialog)).toEqual(ACTIONABLE);

    // An edit keeps the rows on screen, marked out of date, and they no longer open.
    fireEvent.change(box, { target: { value: 'Love' } });
    const stale = '1 record matches. These results are for the previous criteria; Search updates them.';
    expect(resultState(dialog)).toEqual(OUT_OF_DATE);
    expect(within(dialog).getByText(stale)).toBeTruthy();
    expect(descriptionsOf(within(dialog).getByRole('button', { name: 'Open record at position 1' }))).toEqual([
      `${stale} ${ON_SCREEN_NOT_LISTED}`,
    ]);
    tryToOpen(dialog);
    expect(screen.getByRole('dialog', { name: 'Find record' })).toBe(dialog);

    // The criteria sent again describe the rows.
    fireEvent.change(box, { target: { value: 'Lovelace' } });
    expect(resultState(dialog)).toEqual(ACTIONABLE);

    // A newer search, held open: the old rows stay inactive while the criteria stay editable.
    fireEvent.change(box, { target: { value: 'Lovelace or Smith' } });
    fireEvent.click(search);
    expect(resultState(dialog)).toEqual(PENDING);
    expect(within(dialog).getByText('Searching… The results below are from the previous search.')).toBeTruthy();
    expect(isDisabled(search)).toBe(true);
    expect(isDisabled(box)).toBe(false);
    expect(isDisabled(within(dialog).getByRole('button', { name: 'Remove LastName' }))).toBe(false);
    expect(isDisabled(within(dialog).getByRole('button', { name: 'Close' }))).toBe(false);
    tryToOpen(dialog);
    expect(screen.getByRole('dialog', { name: 'Find record' })).toBe(dialog);
    expect(opened()).toEqual([]);

    second.release(matches(MATCH));
    await within(dialog).findByText('1 record matches.');
    expect(resultState(dialog)).toEqual(ACTIONABLE);
    // Each search sent its criterion exactly as typed.
    expect(calls.filter(isSearch).map(sentCriteria)).toEqual([
      [{ field: 'LastName', value: 'Lovelace' }],
      [{ field: 'LastName', value: 'Lovelace or Smith' }],
    ]);

    fireEvent.click(within(dialog).getByRole('button', { name: 'Open record at position 1' }));
    expect(screen.queryByRole('dialog', { name: 'Find record' })).toBeNull();
    await vi.waitFor(() => {
      expect(opened()).toHaveLength(1);
    });
    await within(screen.getByRole('toolbar', { name: 'Record navigation' })).findByText('1 of 1');
  });

  /** A search result for a stored record of the two-record set, matched on its LastName. */
  function matchOf(record: RecordDto): SearchResult {
    const lastName = record.values.LastName;
    return {
      globalRecordId: record.globalRecordId,
      uniqueKey: record.uniqueKey,
      recStatus: record.recStatus,
      position: record.position,
      values: { LastName: typeof lastName === 'string' ? lastName : null },
    };
  }

  // The server leaves the record sent as excludeGlobalRecordId out before it counts, so the
  // status says so; a new record's search excludes nothing and the status adds nothing.
  it.each<{
    on: string;
    startNew: boolean;
    answer: readonly RecordDto[];
    summary: string;
    excluded: string | undefined;
  }>([
    { on: 'a stored record', startNew: false, answer: [], summary: 'No records match.', excluded: RECORD_A.globalRecordId },
    {
      on: 'a stored record',
      startNew: false,
      answer: [RECORD_B],
      summary: '1 record matches.',
      excluded: RECORD_A.globalRecordId,
    },
    { on: 'a new record', startNew: true, answer: [RECORD_A], summary: '1 record matches.', excluded: undefined },
  ])(
    'tells whether the record on screen was left out when Find on $on answers $summary',
    async ({ startNew, answer, summary, excluded }) => {
      await renderForm(undefined, {
        records: [RECORD_A, RECORD_B],
        route: searchRoute(() => matches(...answer.map(matchOf))),
      });
      if (startNew) {
        const navigator = screen.getByRole('toolbar', { name: 'Record navigation' });
        fireEvent.click(within(navigator).getByRole('button', { name: 'New' }));
        await within(navigator).findByText('New record');
      }
      const dialog = await openFind();
      fireEvent.change(pick(dialog, 'LastName'), { target: { value: 'Lovelace or Hopper' } });
      fireEvent.click(within(dialog).getByRole('button', { name: 'Search' }));

      // The count keeps a text of its own, and the note follows it on the same status line.
      const status = await within(dialog).findByText(summary);
      expect(status.getAttribute('role')).toBe('status');
      expect(status.textContent).toBe(excluded === undefined ? summary : `${summary} ${ON_SCREEN_NOT_LISTED}`);
      expect(calls.filter(isSearch).map(sentExclusion)).toEqual([excluded]);
    },
  );

  it('says the record that was on screen is not listed once another record opens behind the results', async () => {
    const next = deferred();
    const atTwo = `${FORM_PATH}/records/at/2`;
    const answerSearch = searchRoute(() => matches(matchOf(RECORD_B)));
    await renderForm(undefined, {
      records: [RECORD_A, RECORD_B],
      route: (call) => (call.method === 'GET' && call.url.pathname === atTwo ? next.response : answerSearch(call)),
    });
    // Next is still loading B when Find opens, so the search leaves A out.
    const navigator = screen.getByRole('toolbar', { name: 'Record navigation' });
    fireEvent.click(within(navigator).getByRole('button', { name: 'Next' }));
    await vi.waitFor(() => {
      expect(calls.some((call) => call.method === 'GET' && call.url.pathname === atTwo)).toBe(true);
    });
    const dialog = await openFind();
    fireEvent.change(pick(dialog, 'LastName'), { target: { value: B_LAST_NAME } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Search' }));
    const status = await within(dialog).findByText('1 record matches.');
    expect(status.textContent).toBe(`1 record matches. ${ON_SCREEN_NOT_LISTED}`);

    // B, which the results list, is now on screen; A is the record they leave out.
    next.release(jsonResponse(200, RECORD_B));
    await within(navigator).findByText('2 of 2');
    const stale = '1 record matches. These results are for the previous criteria; Search updates them.';
    expect(within(dialog).getByText(stale).textContent).toBe(`${stale} ${EARLIER_NOT_LISTED}`);
    expect(calls.filter(isSearch).map(sentExclusion)).toEqual([RECORD_A.globalRecordId]);
  });

  it("keeps a field's message while its criterion holds the text sent, and the banner's summary while such a message shows", async () => {
    const problem = {
      type: 'https://tools.ietf.org/html/rfc9110#section-15.5.1',
      title: 'One or more validation errors occurred.',
      status: 400,
      traceId: '00-test-search-00',
      errors: {
        LastName: ['LastName needs a value.'],
        CaseID: ['CaseID must be a number.'],
        criteria: ['A search names at most six fields.'],
      },
    };
    await renderForm(undefined, { route: searchRoute(() => jsonResponse(400, problem)) });
    const dialog = await openFind();
    const lastName = pick(dialog, 'LastName');
    const caseId = pick(dialog, 'CaseID');
    const [lastNameHint] = descriptionsOf(lastName);
    const [caseIdHint] = descriptionsOf(caseId);

    fireEvent.change(caseId, { target: { value: 'abc' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Search' }));
    await within(dialog).findByText('CaseID must be a number.');
    const shown = () => ({
      lastName: { invalid: lastName.getAttribute('aria-invalid'), described: descriptionsOf(lastName) },
      caseId: { invalid: caseId.getAttribute('aria-invalid'), described: descriptionsOf(caseId) },
      banner: within(dialog).queryByRole('alert')?.textContent ?? null,
    });
    const lastNameInvalid = { invalid: 'true', described: [lastNameHint, 'LastName needs a value.'] };
    const sentState = {
      lastName: lastNameInvalid,
      caseId: { invalid: 'true', described: [caseIdHint, 'CaseID must be a number.'] },
      banner: 'Some values need attention. See the messages beside each field.A search names at most six fields.',
    };
    expect(shown()).toEqual(sentState);

    // Editing CaseID drops its message and the banner's line about the criteria as a whole;
    // the untouched LastName keeps its own, and the summary that points at it stays.
    const summary = 'Some values need attention. See the messages beside each field.';
    fireEvent.change(caseId, { target: { value: 'abcd' } });
    const caseIdEdited = {
      lastName: lastNameInvalid,
      caseId: { invalid: null, described: [caseIdHint] },
      banner: summary,
    };
    expect(shown()).toEqual(caseIdEdited);

    // The text sent once more: the messages describe the criteria on screen again.
    fireEvent.change(caseId, { target: { value: 'abc' } });
    expect(shown()).toEqual(sentState);

    // The summary stays while either criterion still shows its message, and goes once
    // neither does.
    fireEvent.change(lastName, { target: { value: 'Lovelace' } });
    expect(shown()).toEqual({
      lastName: { invalid: null, described: [lastNameHint] },
      caseId: { invalid: 'true', described: [caseIdHint, 'CaseID must be a number.'] },
      banner: summary,
    });
    fireEvent.change(caseId, { target: { value: 'abcd' } });
    expect(shown()).toEqual({
      lastName: { invalid: null, described: [lastNameHint] },
      caseId: { invalid: null, described: [caseIdHint] },
      banner: null,
    });

    // Restoring one criterion's text brings back its message and the summary with it.
    fireEvent.change(lastName, { target: { value: '' } });
    expect(shown()).toEqual(caseIdEdited);
  });

  it('titles each nonblank result cell with its full value', async () => {
    const long = 'L'.repeat(4000);
    await renderForm(undefined, {
      route: searchRoute(() => matches({ ...MATCH, values: { LastName: long, FirstName: null } })),
    });
    const dialog = await openFind();
    fireEvent.change(pick(dialog, 'LastName'), { target: { value: 'L*' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Search' }));
    await within(dialog).findByText('1 record matches.');

    const open = within(dialog).getByRole('button', { name: 'Open record at position 1' });
    const cells = within(present(open.closest('tr'), 'result row')).getAllByRole('cell');
    // The position cell holds the Open button, named by the position; the others hold values.
    expect(cells.map((cell) => cell.getAttribute('title'))).toEqual([null, long, null]);
  });

  it('wraps the results in a named region that takes focus, so a keyboard can scroll it', async () => {
    await renderForm(undefined, { route: searchRoute(() => matches(MATCH)) });
    const dialog = await openFind();
    fireEvent.change(pick(dialog, 'LastName'), { target: { value: 'Lovelace' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Search' }));
    await within(dialog).findByText('1 record matches.');

    const region = within(dialog).getByRole('region', { name: 'Search results' });
    expect(within(region).getByRole('table', { name: 'Matching records' })).toBeTruthy();
    expect(region.tabIndex).toBe(0);
    region.focus();
    expect(document.activeElement).toBe(region);
  });

  /** The fixture plus `LASTNAME`, just before `LastName`, whose name differs from it only in letter case. */
  function caseDistinctDefinition(): FormDefinition {
    const definition = createFormDefinitionFixture();
    const lastName = fieldByName('LastName', definition);
    const page = pageOf(definition, lastName.pageId);
    const twin: FieldDefinition = {
      ...lastName,
      name: 'LASTNAME',
      prompt: 'Surname as registered',
      tabIndex: lastName.tabIndex - 0.5,
    };
    page.fields.splice(page.fields.indexOf(lastName), 0, twin);
    return definition;
  }

  it('heads each grid column by the field of its exact name, and ignores case only for a unique match', async () => {
    const definition = caseDistinctDefinition();
    const listPath = `/api/forms/${definition.viewId}/records`;
    // Keys as exact names, a unique and an ambiguous spelling in another letter case.
    const summary: RecordSummary = {
      globalRecordId: RECORD.globalRecordId,
      uniqueKey: RECORD.uniqueKey,
      recStatus: RECORD.recStatus,
      values: { caseid: 1042, LASTNAME: null, LastName: 'Lovelace', lastname: null },
    };
    const list: RecordPage = { items: [summary], total: 1, page: 1, pageSize: 50 };
    await renderForm(definition, {
      route: (call) =>
        call.method === 'GET' && call.url.pathname === listPath ? jsonResponse(200, list) : null,
    });

    const headers = within(screen.getByRole('region', { name: 'Records' })).getAllByRole('columnheader');
    expect(headers.map((header) => header.textContent)).toEqual([
      'Case ID',
      'Surname as registered',
      'Last Name',
      'lastname',
    ]);
  });

  it.each<{ key: string; target: string | null }>([
    { key: 'LASTNAME', target: 'LASTNAME' },
    { key: 'LastName', target: 'LastName' },
    { key: 'firstname', target: 'FirstName' },
    // Two field names equal it ignoring case, so it names neither criterion.
    { key: 'lastname', target: null },
  ])('shows a search error keyed $key beside the criterion of that exact or only name', async ({ key, target }) => {
    const message = `Message for ${key}.`;
    const problem = {
      type: 'https://tools.ietf.org/html/rfc9110#section-15.5.1',
      title: 'One or more validation errors occurred.',
      status: 400,
      traceId: '00-test-search-01',
      errors: { [key]: [message] },
    };
    await renderForm(caseDistinctDefinition(), {
      route: searchRoute(() => jsonResponse(400, problem)),
    });
    const dialog = await openFind();
    const boxes = new Map(['FirstName', 'LASTNAME', 'LastName'].map((name) => [name, pick(dialog, name)]));

    fireEvent.click(within(dialog).getByRole('button', { name: 'Search' }));
    await within(dialog).findByText(message);

    const invalid = [...boxes].filter(([, box]) => box.getAttribute('aria-invalid') === 'true').map(([name]) => name);
    expect(invalid).toEqual(target === null ? [] : [target]);
    if (target !== null) {
      expect(descriptionsOf(present(boxes.get(target), `value box of ${target}`))).toContain(message);
    }
    expect(within(dialog).getByRole('alert').textContent).toBe(
      target === null ? message : 'Some values need attention. See the messages beside each field.',
    );
  });

  /** A search 400 with `errors` keyed as the server keys them. */
  function searchProblem(errors: Readonly<Record<string, readonly string[]>>): Response {
    return jsonResponse(400, {
      type: 'https://tools.ietf.org/html/rfc9110#section-15.5.1',
      title: 'One or more validation errors occurred.',
      status: 400,
      traceId: '00-test-search-02',
      errors,
    });
  }

  /** The bar pinned to the dialog's top or bottom edge while its place is out of view, which holds Search. */
  function footerOf(dialog: HTMLElement): HTMLElement {
    const search = within(dialog).getByRole('button', { name: 'Search' });
    return present(search.closest<HTMLElement>('.find-dialog__footer'), 'footer around Search');
  }

  /** Picks LastName, enters `Lovelace`, focuses Search and presses it, as a pointer or Space does. */
  function searchFromButton(dialog: HTMLElement): HTMLElement {
    fireEvent.change(pick(dialog, 'LastName'), { target: { value: 'Lovelace' } });
    const search = within(dialog).getByRole('button', { name: 'Search' });
    search.focus();
    fireEvent.click(search);
    return search;
  }

  it('keeps the status line, Search and Close in one footer between the criteria and the results', async () => {
    await renderForm(undefined, { route: searchRoute(() => matches(MATCH)) });
    const dialog = await openFind();
    const footer = footerOf(dialog);
    const status = within(footer).getByRole('status');
    expect(status.className).toBe('visually-hidden');

    const search = searchFromButton(dialog);
    await within(dialog).findByText('1 record matches.');
    const close = within(dialog).getByRole('button', { name: 'Close' });
    expect(footer.parentElement).toBe(dialog);
    // The status line first, at the bar's start, then the buttons as one group; Search
    // still submits the form.
    const [first, actions, ...rest] = Array.from(footer.children);
    expect(first).toBe(status);
    expect(rest).toEqual([]);
    expect(Array.from(actions.children)).toEqual([search, close]);
    expect(status.className).toBe('');
    expect(search.getAttribute('type')).toBe('submit');
    expect(search.getAttribute('form')).toBe(present(dialog.querySelector('form'), 'Find form').id);

    // Document and Tab order: picker, criteria, Search, Close, then the results.
    const table = within(dialog).getByRole('table', { name: 'Matching records' });
    const ordered = [
      within(dialog).getByRole('group', { name: 'Fields to search' }),
      within(dialog).getByRole('button', { name: 'Remove LastName' }),
      search,
      close,
      within(table).getByRole('button', { name: 'Open record at position 1' }),
    ];
    for (const [index, element] of ordered.slice(1).entries()) {
      expect(ordered[index].compareDocumentPosition(element) & Node.DOCUMENT_POSITION_FOLLOWING).not.toBe(0);
    }
    expect(footer.contains(table)).toBe(false);
  });

  it('shows the limit note under the picker legend, outside the scrolling options', async () => {
    await renderForm();
    const dialog = await openFind();
    const picker = within(dialog).getByRole('group', { name: 'Fields to search' });
    const options = present(picker.querySelector<HTMLElement>('.find-dialog__options'), 'picker options');
    const boxes = within(options).getAllByRole('checkbox');
    for (const box of boxes.slice(0, 6)) {
      fireEvent.click(box);
    }

    const note = within(picker).getByText('At most six fields can be searched.');
    expect(note.getAttribute('role')).toBe('status');
    expect(note.className).toBe('');
    expect(options.contains(note)).toBe(false);
    expect(present(picker.querySelector('legend'), 'picker legend').nextElementSibling).toBe(note);
    // Every option the limit disables still names the note as its reason.
    const disabled = boxes.filter((box) => isDisabled(box));
    expect(disabled).toHaveLength(boxes.length - 6);
    for (const box of disabled) {
      expect(descriptionsOf(box)).toContain('At most six fields can be searched.');
    }
  });

  // The first field, one in the middle and the last, a system field.
  it.each<{ name: string }>([{ name: 'CaseID' }, { name: 'PhoneNumber' }, { name: 'RecStatus' }])(
    'moves focus from the checked $name checkbox to its new value box',
    async ({ name }) => {
      await renderForm();
      const dialog = await openFind();
      within(dialog).getByRole('checkbox', { name }).focus();

      const box = pick(dialog, name);
      expect(document.activeElement).toBe(box);
    },
  );

  it('keeps focus in the picker on open, on unchecking and after Remove, and says checking moves it', async () => {
    await renderForm();
    const dialog = await openFind();
    const picker = within(dialog).getByRole('group', { name: 'Fields to search' });
    const guidance = 'Checking a field moves focus to its value box.';
    expect(document.activeElement).toBe(within(picker).getAllByRole('checkbox')[0]);
    expect(within(picker).getByText(guidance)).toBeTruthy();
    expect(descriptionsOf(picker)).toEqual([guidance]);

    pick(dialog, 'LastName');
    pick(dialog, 'Age');
    const age = within(dialog).getByRole('checkbox', { name: 'Age' });
    age.focus();
    fireEvent.click(age);
    expect(within(dialog).queryByRole('button', { name: 'Remove Age' })).toBeNull();
    expect(document.activeElement).toBe(age);

    fireEvent.click(within(dialog).getByRole('button', { name: 'Remove LastName' }));
    expect(document.activeElement).toBe(within(dialog).getByRole('checkbox', { name: 'LastName' }));
  });

  // The value boxes after LastName, then CaseID, were picked: CaseID comes first in picker order.
  it.each<{ marks: string; errors: Readonly<Record<string, readonly string[]>>; focused: string }>([
    {
      marks: 'two criteria',
      errors: { LastName: ['LastName needs a value.'], CaseID: ['CaseID must be a number.'] },
      focused: 'CaseID',
    },
    {
      marks: 'one criterion',
      errors: { LastName: ['LastName needs a value.'], criteria: ['A search names at most six fields.'] },
      focused: 'LastName',
    },
    { marks: 'no criterion', errors: { criteria: ['A search names at most six fields.'] }, focused: 'Search' },
  ])('after a 400 that marks $marks, scrolls the banner into view, then focuses $focused: the first marked value box in picker order, else Search', async ({
    errors,
    focused,
  }) => {
    // jsdom has no scrollIntoView; with one, the banner scrolls into view first, and the
    // box is focused without a scroll of its own and then scrolled only as far as it
    // needs, so the banner stays in view whenever both fit.
    type Step =
      | { readonly step: 'scroll'; readonly element: Element; readonly options: unknown }
      | { readonly step: 'focus'; readonly element: EventTarget | null };
    const steps: Step[] = [];
    Object.defineProperty(Element.prototype, 'scrollIntoView', {
      configurable: true,
      writable: true,
      value(this: Element, options?: boolean | ScrollIntoViewOptions) {
        steps.push({ step: 'scroll', element: this, options });
      },
    });
    const recordFocus = (event: FocusEvent) => {
      steps.push({ step: 'focus', element: event.target });
    };
    try {
      await renderForm(undefined, { route: searchRoute(() => searchProblem(errors)) });
      const dialog = await openFind();
      const boxes = new Map(['LastName', 'CaseID'].map((name) => [name, pick(dialog, name)]));
      const search = within(dialog).getByRole('button', { name: 'Search' });
      search.focus();
      document.addEventListener('focusin', recordFocus);
      fireEvent.click(search);

      const banner = await within(dialog).findByRole('alert');
      const box = focused === 'Search' ? undefined : present(boxes.get(focused), `value box of ${focused}`);
      expect(document.activeElement).toBe(box ?? search);
      const nearest = { block: 'nearest', inline: 'nearest' };
      expect(steps).toEqual([
        { step: 'scroll', element: banner, options: nearest },
        ...(box === undefined
          ? []
          : [
              { step: 'focus', element: box },
              { step: 'scroll', element: box, options: nearest },
            ]),
      ]);
    } finally {
      document.removeEventListener('focusin', recordFocus);
      Reflect.deleteProperty(Element.prototype, 'scrollIntoView');
    }
  });

  it('leaves focus where the user moved it while the search ran', async () => {
    const answer = deferred();
    await renderForm(undefined, { route: searchRoute(() => answer.response) });
    const dialog = await openFind();
    searchFromButton(dialog);
    const caseId = pick(dialog, 'CaseID');
    caseId.focus();

    answer.release(searchProblem({ LastName: ['LastName needs a value.'] }));
    await within(dialog).findByText('LastName needs a value.');
    expect(document.activeElement).toBe(caseId);
  });

  it.each<{
    outcome: string;
    answer: () => Response;
    scrolled: (dialog: HTMLElement) => readonly Element[];
  }>([
    {
      outcome: 'matches',
      answer: () => matches(MATCH),
      scrolled: (dialog) => [
        present(within(dialog).getByRole('table', { name: 'Matching records' }).parentElement, 'results wrapper'),
        footerOf(dialog),
      ],
    },
    { outcome: 'no match', answer: () => matches(), scrolled: () => [] },
    {
      outcome: 'a request-level 400',
      answer: () => searchProblem({ criteria: ['A search names at most six fields.'] }),
      scrolled: (dialog) => [within(dialog).getByRole('alert')],
    },
    {
      outcome: 'a 503',
      answer: () =>
        jsonResponse(503, {
          type: 'https://tools.ietf.org/html/rfc9110#section-15.6.4',
          title: 'Service Unavailable',
          status: 503,
          detail: 'The database is unavailable.',
          traceId: '00-test-search-03',
        }),
      scrolled: (dialog) => [within(dialog).getByRole('alert')],
    },
  ])('after $outcome, scrolls into view what the footer does not show, keeping focus on Search', async ({
    answer,
    scrolled,
  }) => {
    // jsdom has no scrollIntoView; the dialog calls it only where the browser has it.
    const requests: { readonly element: Element; readonly options: unknown }[] = [];
    Object.defineProperty(Element.prototype, 'scrollIntoView', {
      configurable: true,
      writable: true,
      value(this: Element, options?: boolean | ScrollIntoViewOptions) {
        requests.push({ element: this, options });
      },
    });
    try {
      await renderForm(undefined, { route: searchRoute(answer) });
      const dialog = await openFind();
      const search = searchFromButton(dialog);
      await vi.waitFor(() => {
        expect(isDisabled(search)).toBe(false);
      });

      expect(requests).toEqual(
        scrolled(dialog).map((element) => ({ element, options: { block: 'nearest', inline: 'nearest' } })),
      );
      expect(document.activeElement).toBe(search);
    } finally {
      Reflect.deleteProperty(Element.prototype, 'scrollIntoView');
    }
  });

  // A search is a read, so its connection banner holds no draft and its Retry repeats it.
  it.each<{ label: string; failure: () => Response | Promise<Response>; text: string }>([
    {
      label: 'a 503',
      failure: () =>
        jsonResponse(503, {
          type: 'https://tools.ietf.org/html/rfc9110#section-15.6.4',
          title: 'Service Unavailable',
          status: 503,
          detail: 'The database is unavailable.',
          traceId: '00-test-search-03',
        }),
      text: 'The database cannot be reached.',
    },
    {
      label: 'no response',
      failure: () => Promise.reject(new TypeError('Failed to fetch')),
      text: 'The server could not be reached.',
    },
  ])('says only what cannot be reached, with Retry, when a search gets $label', async ({ failure, text }) => {
    await renderForm(undefined, { route: searchRoute(failure, () => matches(MATCH)) });
    const dialog = await openFind();
    searchFromButton(dialog);

    const alert = await within(dialog).findByRole('alert');
    expect(within(alert).getByText(text)).toBeTruthy();
    expect(alert.textContent).not.toContain('still on screen');

    fireEvent.click(within(alert).getByRole('button', { name: 'Retry' }));

    expect(await within(dialog).findByRole('table', { name: 'Matching records' })).toBeTruthy();
    expect(within(dialog).queryByRole('alert')).toBeNull();
    expect(calls.filter(isSearch)).toHaveLength(2);
  });
});

describe('FormView record grid', () => {
  const LIST_PATH = `/api/forms/${createFormDefinitionFixture().viewId}/records`;
  const COUNT_PATH = `${LIST_PATH}/count`;
  const OPEN_PATH = `${LIST_PATH}/${RECORD.globalRecordId}`;
  // A row's first cell is its first grid column, the fixture's CaseID.
  const OPEN_NAME = `Open record ${recordNumberText(RECORD, 'CaseID')}`;
  const FORM_GONE = 'This form no longer exists.';
  const NOT_FOUND: ProblemDetails = {
    type: 'https://tools.ietf.org/html/rfc9110#section-15.5.5',
    title: 'Not Found',
    status: 404,
    detail: 'The form does not exist.',
    traceId: '00-test-gone-00',
  };

  function isGet(call: Call, path: string): boolean {
    return call.method === 'GET' && call.url.pathname === path;
  }

  function listCalls(): Call[] {
    return calls.filter((call) => isGet(call, LIST_PATH));
  }

  function openCalls(): Call[] {
    return calls.filter((call) => isGet(call, OPEN_PATH));
  }

  function records(): HTMLElement {
    return screen.getByRole('region', { name: 'Records' });
  }

  function searchBox(): HTMLElement {
    return within(records()).getByRole('searchbox', { name: 'Search records' });
  }

  /** The text of the grid's one live region. */
  function gridStatus(): string | null {
    return within(records()).getByRole('status').textContent;
  }

  // The workspace, and with it the navigator and the grid, renders once the form definition has loaded.
  async function recordOpened(): Promise<void> {
    const navigator = await screen.findByRole('toolbar', { name: 'Record navigation' });
    await within(navigator).findByText('1 of 1');
  }

  async function findInGrid(text: string): Promise<HTMLElement> {
    const region = await screen.findByRole('region', { name: 'Records' });
    return within(region).findByText(text);
  }

  /** One page of the list holding the stored record, as `recStatus` and with `values` replaced, of `total` records. */
  function listPage(
    total: number,
    page: number,
    recStatus: number = RECORD.recStatus,
    values: RecordSummary['values'] = {},
  ): Response {
    const summary = recordSummaryFixture(RECORD, createFormDefinitionFixture());
    const body: RecordPage = {
      items: [{ ...summary, recStatus, values: { ...summary.values, ...values } }],
      total,
      page,
      pageSize: 50,
    };
    return jsonResponse(200, body);
  }

  function abortError(): DOMException {
    return new DOMException('The operation was aborted.', 'AbortError');
  }

  // Blank is the server's .NET whitespace test, not JavaScript's trim(): the two disagree on
  // U+FEFF, a filter to the server, and U+0085, blank to it.
  it.each<{ label: string; typed: readonly string[]; sent: readonly (string | null)[] }>([
    { label: 'sends a nonblank filter verbatim, edge spaces included', typed: [' Ada '], sent: [' Ada '] },
    { label: 'sends a U+FEFF filter verbatim, not whitespace to .NET', typed: ['\uFEFF'], sent: ['\uFEFF'] },
    { label: 'sends no q for a blank filter', typed: ['Ada', '   '], sent: ['Ada', null] },
    { label: 'sends no q for a U+0085 filter, whitespace to .NET', typed: ['Ada', '\u0085'], sent: ['Ada', null] },
    { label: 'sends no q for a U+3000, space and tab filter', typed: ['Ada', '\u3000 \t'], sent: ['Ada', null] },
  ])('$label', async ({ typed, sent }) => {
    // A filtered list matches nothing here, so the status tells which list answered.
    const noMatch: RecordPage = { items: [], total: 0, page: 1, pageSize: 50 };
    await renderForm(undefined, {
      route: (call) =>
        isGet(call, LIST_PATH) && call.url.searchParams.has('q') ? jsonResponse(200, noMatch) : null,
    });

    for (const [index, value] of typed.entries()) {
      const before = listCalls().length;
      fireEvent.change(searchBox(), { target: { value } });
      // One request per filter, once the search box's pause has passed.
      await vi.waitFor(() => {
        expect(listCalls()).toHaveLength(before + 1);
      });
      const { searchParams } = present(listCalls().at(-1), 'list request').url;
      expect(searchParams.has('q'), value).toBe(sent[index] !== null);
      expect(searchParams.get('q'), value).toBe(sent[index]);
      // No q shows the unfiltered list, the one stored record.
      const status = sent[index] === null ? '1 record' : 'No records found.';
      await vi.waitFor(() => {
        expect(gridStatus(), value).toBe(status);
      });
    }
  });

  it('shows a 400 about q beside the search box and other parameters in the banner, and drops the stale message on an edit', async () => {
    // The server sends no q error today; this case guards the grid's routing of one should it come.
    const searchMessage = 'q is not accepted here.';
    const rejected = {
      type: 'https://tools.ietf.org/html/rfc9110#section-15.5.1',
      title: 'One or more validation errors occurred.',
      status: 400,
      traceId: '00-test-list-00',
      errors: { q: [searchMessage], pageSize: ['The value must be an integer.'] },
    };
    await renderForm(undefined, {
      route: (call) =>
        isGet(call, LIST_PATH) && call.url.searchParams.has('q') ? jsonResponse(400, rejected) : null,
    });
    const search = searchBox();

    fireEvent.change(search, { target: { value: 'Ada' } });
    await findInGrid(searchMessage);
    expect(search.getAttribute('aria-invalid')).toBe('true');
    const describedBy = present(search.getAttribute('aria-describedby'), 'aria-describedby of the search box');
    const message = present(document.getElementById(describedBy), 'the search box message');
    expect(message.getAttribute('role')).toBe('alert');
    expect(message.textContent).toBe(searchMessage);
    // The other parameter's message names it in the banner, which offers no Retry for a 400.
    expect(within(records()).getAllByRole('alert').map((alert) => alert.textContent)).toEqual([
      searchMessage,
      'The record list could not be loaded.pageSize: The value must be an integer.Trace id: 00-test-list-00',
    ]);
    expect(within(records()).queryByRole('button', { name: 'Retry' })).toBeNull();

    // Another text: the message no longer describes the box, so it goes at once.
    fireEvent.change(search, { target: { value: 'Adam' } });
    expect(search.getAttribute('aria-invalid')).toBeNull();
    expect(search.getAttribute('aria-describedby')).toBeNull();
    expect(within(records()).queryByText(searchMessage)).toBeNull();

    // The new text gets its own answer, shown beside the box in turn.
    await findInGrid(searchMessage);
    expect(listCalls().map((call) => call.url.searchParams.get('q'))).toEqual([null, 'Ada', 'Adam']);
  });

  it.each<{ source: string; path: string; grid: string }>([
    { source: 'records list', path: LIST_PATH, grid: FORM_GONE },
    { source: 'record count', path: COUNT_PATH, grid: '1 record' },
  ])('hands a $source 404 over to the forms list once while the draft is clean', async ({ path, grid }) => {
    const { onFormNotFound } = mountForm(undefined, {
      route: (call) =>
        isGet(call, path) ? jsonResponse(404, NOT_FOUND) : null,
    });

    await findInGrid(grid);
    await vi.waitFor(() => {
      expect(onFormNotFound).toHaveBeenCalledTimes(1);
    });
    // One alert, the workspace's; the grid states the loss without an alert or a Retry.
    expect(screen.getAllByRole('alert').map((alert) => alert.textContent)).toEqual([FORM_GONE]);
    expect(gridStatus()).toBe(grid);
    expect(screen.queryByRole('button', { name: 'Retry' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Discard edits' })).toBeNull();
  });

  it('hands a record count 404 answered before the definition over to the forms list once', async () => {
    const definition = createFormDefinitionFixture();
    const answer = deferred();
    const { onFormNotFound } = mountForm(definition, {
      route: (call) => {
        if (isGet(call, FORM_PATH)) {
          return answer.response;
        }
        return isGet(call, COUNT_PATH) ? jsonResponse(404, NOT_FOUND) : null;
      },
    });
    await vi.waitFor(() => {
      expect(calls.some((call) => isGet(call, COUNT_PATH))).toBe(true);
    });
    await act(flush);
    // Nothing is on screen yet to hand over: the definition is still loading.
    expect(onFormNotFound).not.toHaveBeenCalled();
    expect(screen.getByRole('status').textContent).toBe('Loading form…');

    answer.release(jsonResponse(200, definition));

    await findInGrid('1 record');
    await vi.waitFor(() => {
      expect(onFormNotFound).toHaveBeenCalledTimes(1);
    });
    expect(screen.getAllByRole('alert').map((alert) => alert.textContent)).toEqual([FORM_GONE]);
  });

  it('keeps a dirty draft on a list 404 until "Discard edits" is chosen', async () => {
    const list = deferred();
    let held = false;
    const { definition, onFormNotFound } = mountForm(undefined, {
      route: (call) => {
        if (held || !isGet(call, LIST_PATH)) {
          return null;
        }
        held = true;
        return list.response;
      },
    });
    await recordOpened();
    const firstName = screen.getByLabelText(promptOf(fieldByName('FirstName', definition)));
    fireEvent.change(firstName, { target: { value: 'Adah' } });

    list.release(jsonResponse(404, NOT_FOUND));
    const banner = await screen.findByRole('alert');
    expect(banner.textContent).toBe(`${FORM_GONE}Discard edits`);
    expect(gridStatus()).toBe(FORM_GONE);
    expect(onFormNotFound).not.toHaveBeenCalled();
    expect(controlValue(firstName)).toBe('Adah');

    fireEvent.click(within(banner).getByRole('button', { name: 'Discard edits' }));
    await vi.waitFor(() => {
      expect(onFormNotFound).toHaveBeenCalledTimes(1);
    });
    expect(within(banner).queryByRole('button', { name: 'Discard edits' })).toBeNull();
  });

  const FIELD_EVENT_PATH = `/api/forms/${createFormDefinitionFixture().viewId}/checkcode/field-event`;
  const EVENT_FAILED: ProblemDetails = {
    type: 'https://tools.ietf.org/html/rfc9110#section-15.6.1',
    title: 'An error occurred while processing your request.',
    status: 500,
    detail: 'An unexpected error occurred.',
    traceId: '00-test-event-00',
  };

  /** The `event` of a field-event request; `null` for any other request. */
  function fieldEventOf(call: Call): string | null {
    if (call.method !== 'POST' || call.url.pathname !== FIELD_EVENT_PATH || call.body === null) {
      return null;
    }
    const body: unknown = JSON.parse(call.body);
    return typeof body === 'object' && body !== null && 'event' in body && typeof body.event === 'string'
      ? body.event
      : null;
  }

  function fieldEventResponse(assigned: FieldEventResponse['assigned']): Response {
    const body: FieldEventResponse = { enabled: [], disabled: [], cleared: [], assigned, dialogs: [], diagnostics: [] };
    return jsonResponse(200, body);
  }

  it.each<{
    outcome: string;
    answer: () => Promise<Response>;
    age: string;
    notice: string | null;
    dirty: boolean;
  }>([
    {
      outcome: 'assigns nothing',
      answer: () => Promise.resolve(fieldEventResponse({})),
      age: recordNumberText(RECORD, 'Age'),
      notice: null,
      dirty: false,
    },
    {
      outcome: 'assigns Age',
      answer: () => Promise.resolve(fieldEventResponse({ Age: 11 })),
      age: '11',
      notice: null,
      dirty: true,
    },
    {
      outcome: 'fails with a 500',
      answer: () => Promise.resolve(jsonResponse(500, EVENT_FAILED)),
      age: recordNumberText(RECORD, 'Age'),
      notice: `Check code could not run for DOB. An unexpected error occurred. Trace id: ${EVENT_FAILED.traceId}.`,
      dirty: false,
    },
    {
      outcome: 'gets no response',
      answer: () => Promise.reject(new TypeError('Failed to fetch')),
      age: recordNumberText(RECORD, 'Age'),
      notice: 'Check code could not run for DOB. The server could not be reached. Your changes are still on screen.',
      dirty: false,
    },
  ])('decides on a list 404 only after a held FieldAfter that $outcome has settled', async (example) => {
    // The gate's own response is not used: the example answers the event once it opens.
    const gate = deferred();
    const { definition, onFormNotFound } = await renderForm(undefined, {
      route: (call) => {
        if (isGet(call, LIST_PATH) && call.url.searchParams.get('q') === 'gone') {
          return jsonResponse(404, NOT_FOUND);
        }
        switch (fieldEventOf(call)) {
          case 'FieldBefore':
            return fieldEventResponse({});
          case 'FieldAfter':
            return gate.response.then(example.answer);
          default:
            return null;
        }
      },
    });
    const dob = screen.getByLabelText(promptOf(fieldByName('DOB', definition)));
    const age = screen.getByLabelText(promptOf(fieldByName('Age', definition)));

    // Entering and leaving DOB, unedited: FieldBefore is answered, FieldAfter is held.
    fireEvent.focus(dob);
    fireEvent.blur(dob);
    await vi.waitFor(() => {
      expect(calls.map(fieldEventOf).filter((event) => event !== null)).toEqual(['FieldBefore', 'FieldAfter']);
    });
    fireEvent.change(searchBox(), { target: { value: 'gone' } });

    // The form is gone, but the draft is not judged clean while its event is pending.
    const banner = await screen.findByRole('alert');
    expect(banner.textContent).toBe(FORM_GONE);
    expect(gridStatus()).toBe(FORM_GONE);
    expect(onFormNotFound).not.toHaveBeenCalled();

    gate.release(new Response(null));
    if (example.dirty) {
      // The assignment is applied and dirties the draft, which stays until it is discarded.
      await within(banner).findByRole('button', { name: 'Discard edits' });
      expect(banner.textContent).toBe(`${FORM_GONE}Discard edits`);
      expect(controlValue(age)).toBe(example.age);
      expect(onFormNotFound).not.toHaveBeenCalled();
      fireEvent.click(within(banner).getByRole('button', { name: 'Discard edits' }));
    }
    await vi.waitFor(() => {
      expect(onFormNotFound).toHaveBeenCalledTimes(1);
    });
    expect(controlValue(age)).toBe(example.age);
    // A failed event shows its non-blocking notice and still counts as drained.
    const notices = screen
      .getAllByRole('status')
      .map((status) => status.textContent ?? '')
      .filter((text) => text.startsWith('Check code could not run'));
    expect(notices).toEqual(example.notice === null ? [] : [example.notice]);
    // Leaving the gone form writes nothing.
    expect(calls.filter((call) => call.url.pathname.startsWith(LIST_PATH) && call.method !== 'GET')).toEqual([]);
  });

  /** `<event> <fieldName>` of a field-event request; `null` for any other request. */
  function fieldEventTargetOf(call: Call): string | null {
    const event = fieldEventOf(call);
    if (event === null || call.body === null) {
      return null;
    }
    const body: unknown = JSON.parse(call.body);
    return typeof body === 'object' && body !== null && 'fieldName' in body && typeof body.fieldName === 'string'
      ? `${event} ${body.fieldName}`
      : null;
  }

  /** The paragraphs of each check-code notice banner shown; empty when there is none. */
  function checkCodeNoticeBanners(): string[][] {
    return screen
      .queryAllByRole('status')
      .filter((status) => (status.textContent ?? '').startsWith('Check code could not run'))
      .map((status) => within(status).queryAllByRole('paragraph').map((paragraph) => paragraph.textContent ?? ''));
  }

  const DOB_EVENT_FAILED = `Check code could not run for DOB. An unexpected error occurred. Trace id: ${EVENT_FAILED.traceId}.`;
  const FIRST_NAME_EVENT_FAILED = `Check code could not run for First Name. An unexpected error occurred. Trace id: ${EVENT_FAILED.traceId}.`;

  /** `<event> <fieldName>` of every field-event request, in arrival order. */
  function sentFieldEvents(): string[] {
    return calls.map(fieldEventTargetOf).filter((target) => target !== null);
  }

  /**
   * Waits until `count` field events were sent. The queue sends an event only once the one
   * before it was processed, so every earlier answer has then reached the editor; act
   * commits the render it caused.
   */
  async function processedBefore(count: number): Promise<void> {
    await vi.waitFor(() => {
      expect(sentFieldEvents()).toHaveLength(count);
    });
    await act(async () => {
      await Promise.resolve();
    });
  }

  /** Presses `target` while `from` has focus: the press comes first, then focus moves. */
  function pressMovingFocus(from: HTMLElement, target: HTMLElement): void {
    fireEvent.pointerDown(target);
    fireEvent.blur(from);
    fireEvent.focus(target);
  }

  it('lists the notice of each field whose event failed in one status banner, a paragraph each in order of failure', async () => {
    const { definition } = await renderForm(undefined, {
      route: (call) => {
        switch (fieldEventTargetOf(call)) {
          case null:
            return null;
          case 'FieldAfter DOB':
          case 'FieldAfter FirstName':
            return jsonResponse(500, EVENT_FAILED);
          default:
            return fieldEventResponse({});
        }
      },
    });
    const dob = screen.getByLabelText(promptOf(fieldByName('DOB', definition)));
    const firstName = screen.getByLabelText(promptOf(fieldByName('FirstName', definition)));

    fireEvent.focus(dob);
    fireEvent.blur(dob);
    fireEvent.focus(firstName);
    fireEvent.blur(firstName);

    await vi.waitFor(() => {
      expect(checkCodeNoticeBanners()).toEqual([[DOB_EVENT_FAILED, FIRST_NAME_EVENT_FAILED]]);
    });
    // The paragraphs sit in one block, the banner's only child that is not a button, so
    // each takes its own line instead of sharing one with another as a flex item.
    const banner = present(
      screen.getAllByRole('status').find((status) => (status.textContent ?? '').startsWith('Check code could not run')),
      'check-code notice banner',
    );
    const blocks = Array.from(banner.children).filter((child) => !(child instanceof HTMLButtonElement));
    expect(blocks).toHaveLength(1);
    const paragraphs = within(banner).getAllByRole('paragraph');
    expect(paragraphs).toHaveLength(2);
    for (const paragraph of paragraphs) {
      expect(paragraph.parentElement).toBe(blocks[0]);
    }
  });

  it.each<{ end: string; finish: (target: HTMLElement) => void }>([
    { end: 'a pointerup', finish: (target) => fireEvent.pointerUp(target) },
    { end: 'a pointercancel', finish: (target) => fireEvent.pointerCancel(target) },
    { end: 'the window losing focus', finish: () => fireEvent.blur(window) },
  ])('shows a notice that arrived during a press a task after $end ends the press', async ({ finish }) => {
    const { definition } = await renderForm(undefined, {
      route: (call) => {
        switch (fieldEventTargetOf(call)) {
          case null:
            return null;
          case 'FieldAfter DOB':
            return jsonResponse(500, EVENT_FAILED);
          default:
            return fieldEventResponse({});
        }
      },
    });
    const dob = screen.getByLabelText(promptOf(fieldByName('DOB', definition)));
    const firstName = screen.getByLabelText(promptOf(fieldByName('FirstName', definition)));

    fireEvent.focus(dob);
    pressMovingFocus(dob, firstName);
    await processedBefore(3);
    expect(checkCodeNoticeBanners()).toEqual([]);

    finish(firstName);
    // Applied a task later, so the press's click is dispatched over the same layout.
    expect(checkCodeNoticeBanners()).toEqual([]);
    await vi.waitFor(() => {
      expect(checkCodeNoticeBanners()).toEqual([[DOB_EVENT_FAILED]]);
    });
  });

  // QAFE-F03: a press moves focus, which raises events whose notices may appear, change or
  // go; the banner holds still until the release, so the press and the release land on the
  // same control.
  it('holds the check-code notices still while a pointer is pressed and shows their change after the release', async () => {
    const firstDobAfter = deferred();
    const afters = { DOB: 0, FirstName: 0 };
    const { definition } = await renderForm(undefined, {
      route: (call) => {
        switch (fieldEventTargetOf(call)) {
          case null:
            return null;
          case 'FieldAfter DOB':
            // The first is held and then fails; the second is applied, which resolves it.
            afters.DOB += 1;
            return afters.DOB === 1 ? firstDobAfter.response : fieldEventResponse({});
          case 'FieldAfter FirstName':
            afters.FirstName += 1;
            return afters.FirstName === 1 ? jsonResponse(500, EVENT_FAILED) : fieldEventResponse({});
          default:
            return fieldEventResponse({});
        }
      },
    });
    const dob = screen.getByLabelText(promptOf(fieldByName('DOB', definition)));
    const firstName = screen.getByLabelText(promptOf(fieldByName('FirstName', definition)));
    const lastName = screen.getByLabelText(promptOf(fieldByName('LastName', definition)));
    const release = (target: HTMLElement): void => {
      fireEvent.pointerUp(target);
      fireEvent.click(target);
    };

    fireEvent.focus(dob);

    // Appearing: DOB's FieldAfter fails while the press on First Name is held.
    pressMovingFocus(dob, firstName);
    firstDobAfter.release(jsonResponse(500, EVENT_FAILED));
    await processedBefore(3);
    expect(checkCodeNoticeBanners()).toEqual([]);
    release(firstName);
    expect(checkCodeNoticeBanners()).toEqual([]);
    await vi.waitFor(() => {
      expect(checkCodeNoticeBanners()).toEqual([[DOB_EVENT_FAILED]]);
    });

    // Growing: First Name's FieldAfter fails; DOB's FieldBefore succeeds and resolves nothing.
    pressMovingFocus(firstName, dob);
    await processedBefore(5);
    expect(checkCodeNoticeBanners()).toEqual([[DOB_EVENT_FAILED]]);
    release(dob);
    expect(checkCodeNoticeBanners()).toEqual([[DOB_EVENT_FAILED]]);
    await vi.waitFor(() => {
      expect(checkCodeNoticeBanners()).toEqual([[DOB_EVENT_FAILED, FIRST_NAME_EVENT_FAILED]]);
    });

    // Shrinking: DOB's own FieldAfter is applied.
    pressMovingFocus(dob, firstName);
    await processedBefore(7);
    expect(checkCodeNoticeBanners()).toEqual([[DOB_EVENT_FAILED, FIRST_NAME_EVENT_FAILED]]);
    release(firstName);
    expect(checkCodeNoticeBanners()).toEqual([[DOB_EVENT_FAILED, FIRST_NAME_EVENT_FAILED]]);
    await vi.waitFor(() => {
      expect(checkCodeNoticeBanners()).toEqual([[FIRST_NAME_EVENT_FAILED]]);
    });

    // Going: First Name's own FieldAfter is applied.
    pressMovingFocus(firstName, lastName);
    await processedBefore(9);
    expect(checkCodeNoticeBanners()).toEqual([[FIRST_NAME_EVENT_FAILED]]);
    release(lastName);
    expect(checkCodeNoticeBanners()).toEqual([[FIRST_NAME_EVENT_FAILED]]);
    await vi.waitFor(() => {
      expect(checkCodeNoticeBanners()).toEqual([]);
    });

    expect(sentFieldEvents()).toEqual([
      'FieldBefore DOB',
      'FieldAfter DOB',
      'FieldBefore FirstName',
      'FieldAfter FirstName',
      'FieldBefore DOB',
      'FieldAfter DOB',
      'FieldBefore FirstName',
      'FieldAfter FirstName',
      'FieldBefore LastName',
    ]);
  });

  it('decides on a list 404 only after a fresh drain when a dirty draft turns clean with a FieldAfter held', async () => {
    const gate = deferred();
    const { definition, onFormNotFound } = await renderForm(undefined, {
      route: (call) => {
        if (isGet(call, LIST_PATH) && call.url.searchParams.get('q') === 'gone') {
          return jsonResponse(404, NOT_FOUND);
        }
        switch (fieldEventOf(call)) {
          case 'FieldBefore':
            return fieldEventResponse({});
          case 'FieldAfter':
            return gate.response.then(() => fieldEventResponse({}));
          default:
            return null;
        }
      },
    });
    // A new draft is clean while it is empty, so clearing its one entry restores it; an
    // edited field of a stored record stays dirty whatever value it is given back.
    const navigator = screen.getByRole('toolbar', { name: 'Record navigation' });
    fireEvent.click(within(navigator).getByRole('button', { name: 'New' }));
    await within(navigator).findByText('New record');
    const lastName = screen.getByLabelText(promptOf(fieldByName('LastName', definition)));
    const dob = screen.getByLabelText(promptOf(fieldByName('DOB', definition)));
    fireEvent.change(lastName, { target: { value: 'Byron' } });

    // The form is gone while the draft is dirty, with no event pending: it stays until discarded.
    fireEvent.change(searchBox(), { target: { value: 'gone' } });
    const banner = await screen.findByRole('alert');
    await within(banner).findByRole('button', { name: 'Discard edits' });
    expect(onFormNotFound).not.toHaveBeenCalled();

    // Entering and leaving DOB holds its FieldAfter; then the user restores the empty LastName.
    fireEvent.focus(dob);
    fireEvent.blur(dob);
    await vi.waitFor(() => {
      expect(calls.map(fieldEventOf).filter((event) => event !== null)).toEqual(['FieldBefore', 'FieldAfter']);
    });
    fireEvent.change(lastName, { target: { value: '' } });
    expect(banner.textContent).toBe(FORM_GONE);
    // The drain that ended while the draft was dirty does not judge the clean draft, and
    // the fresh drain waits for the held event however long the rest takes to settle.
    expect(onFormNotFound).not.toHaveBeenCalled();
    await new Promise<void>((resolve) => {
      setTimeout(resolve, 0);
    });
    expect(onFormNotFound).not.toHaveBeenCalled();

    gate.release(new Response(null));
    await vi.waitFor(() => {
      expect(onFormNotFound).toHaveBeenCalledTimes(1);
    });
    expect(controlValue(lastName)).toBe('');
    expect(controlValue(dob)).toBe('');
    // Leaving the gone form writes nothing.
    expect(calls.filter((call) => call.url.pathname.startsWith(LIST_PATH) && call.method !== 'GET')).toEqual([]);
  });

  /** Opens Find, picks LastName, enters a value and presses Search; returns the dialog and its Search button. */
  async function searchLastName(): Promise<{ readonly dialog: HTMLElement; readonly search: HTMLElement }> {
    const formActions = screen.getByRole('toolbar', { name: 'Form actions' });
    fireEvent.click(within(formActions).getByRole('button', { name: 'Find' }));
    const dialog = await screen.findByRole('dialog', { name: 'Find record' });
    fireEvent.click(within(dialog).getByRole('checkbox', { name: 'LastName' }));
    fireEvent.change(within(dialog).getByRole('textbox', { name: 'Last Name (LastName)' }), {
      target: { value: 'Lovelace' },
    });
    const search = within(dialog).getByRole('button', { name: 'Search' });
    fireEvent.click(search);
    return { dialog, search };
  }

  it('closes Find and hands a search 404 over to the forms list', async () => {
    const { onFormNotFound } = await renderForm(undefined, {
      route: (call) =>
        isSearch(call) ? jsonResponse(404, NOT_FOUND) : null,
    });

    await searchLastName();
    await vi.waitFor(() => {
      expect(onFormNotFound).toHaveBeenCalledTimes(1);
    });
    expect(screen.queryByRole('dialog', { name: 'Find record' })).toBeNull();
    expect(screen.getByRole('alert').textContent).toBe(FORM_GONE);
  });

  it('settles a list request aborted by something else to a line offering Retry, which loads the list', async () => {
    let aborted = false;
    mountForm(undefined, {
      route: (call) => {
        if (aborted || !isGet(call, LIST_PATH)) {
          return null;
        }
        aborted = true;
        return Promise.reject(abortError());
      },
    });

    await findInGrid('The record list was not loaded.');
    expect(gridStatus()).toBe('The record list was not loaded.');
    expect(within(records()).queryByText('Loading records…')).toBeNull();
    expect(within(records()).queryByRole('alert')).toBeNull();

    fireEvent.click(within(records()).getByRole('button', { name: 'Retry' }));
    // The button leaves with the line, so focus moves to the live region, which stays.
    expect(document.activeElement).toBe(within(records()).getByRole('status'));
    await findInGrid('1 record');
    expect(document.activeElement).toBe(within(records()).getByRole('status'));
    expect(listCalls()).toHaveLength(2);
  });

  it('enables Search again after a search aborted by something else, with no error', async () => {
    let aborted = false;
    await renderForm(undefined, {
      route: (call) => {
        if (aborted || !isSearch(call)) {
          return null;
        }
        aborted = true;
        return Promise.reject(abortError());
      },
    });

    const { dialog, search } = await searchLastName();
    expect(isDisabled(search)).toBe(true);
    await vi.waitFor(() => {
      expect(isDisabled(search)).toBe(false);
    });
    expect(within(dialog).queryByRole('alert')).toBeNull();
    expect(calls.filter(isSearch)).toHaveLength(1);
  });

  it.each<{
    change: string;
    first: () => Response;
    trigger: () => void;
    isHeld: (call: Call) => boolean;
    then: () => Response;
    pages: readonly [before: string, pending: string, after: string];
  }>([
    {
      change: 'a new filter',
      first: () => listPage(1, 1),
      trigger: () => fireEvent.change(searchBox(), { target: { value: 'Ada' } }),
      isHeld: (call) => call.url.searchParams.get('q') === 'Ada',
      then: () => listPage(1, 1),
      pages: ['Page 1 of 1', 'Page 1', 'Page 1 of 1'],
    },
    {
      change: 'another page',
      first: () => listPage(51, 1),
      trigger: () => fireEvent.click(within(records()).getByRole('button', { name: 'Later records' })),
      isHeld: (call) => call.url.searchParams.get('page') === '2',
      then: () => listPage(51, 2),
      pages: ['Page 1 of 2', 'Page 2', 'Page 2 of 2'],
    },
  ])('keeps rows on screen but inert while $change loads, and opens a row once it arrives', async (example) => {
    const held = deferred();
    const { definition } = mountForm(undefined, {
      route: (call) => {
        if (!isGet(call, LIST_PATH)) {
          return null;
        }
        return example.isHeld(call) ? held.response : example.first();
      },
    });
    const state = () => {
      const table = within(records()).getByRole('table', { name: `${definition.name} records` });
      const open = within(table).getByRole('button', { name: OPEN_NAME });
      const paging = within(records()).getByRole('navigation', { name: 'Record list pages' });
      return {
        busy: table.getAttribute('aria-busy'),
        pendingStyle: table.classList.contains('record-grid__table--pending'),
        row: present(open.closest('tr'), 'grid row').getAttribute('aria-disabled'),
        open: open.getAttribute('aria-disabled'),
        page: within(paging).getByText(/^Page /).textContent,
        paging: within(paging)
          .getAllByRole('button')
          .map((button) => button.getAttribute('aria-disabled')),
      };
    };
    const clickRow = () => {
      const open = within(records()).getByRole('button', { name: OPEN_NAME });
      fireEvent.click(open);
      fireEvent.click(present(open.closest('tr'), 'grid row'));
    };
    const [before, pending, after] = example.pages;
    await recordOpened();
    await vi.waitFor(() => {
      expect(state().busy).toBe('false');
    });
    const settled = { busy: 'false', pendingStyle: false, row: null, open: null, paging: [null, null] };
    expect(state()).toEqual({ ...settled, page: before });

    example.trigger();
    await vi.waitFor(() => {
      expect(listCalls().some(example.isHeld)).toBe(true);
    });
    expect(state()).toEqual({
      busy: 'true',
      pendingStyle: true,
      row: 'true',
      open: 'true',
      page: pending,
      paging: ['true', 'true'],
    });
    expect(gridStatus()).toBe('Loading records…');
    clickRow();
    expect(openCalls()).toEqual([]);

    held.release(example.then());
    await vi.waitFor(() => {
      expect(state().busy).toBe('false');
    });
    expect(state()).toEqual({ ...settled, page: after });
    fireEvent.click(within(records()).getByRole('button', { name: OPEN_NAME }));
    await vi.waitFor(() => {
      expect(openCalls()).toHaveLength(1);
    });
    await recordOpened();
  });

  const CASE_ID_TEXT = recordNumberText(RECORD, 'CaseID');
  it.each<{ row: string; recStatus: number; values: RecordSummary['values']; name: string; shown: string }>([
    { row: 'an active row', recStatus: 1, values: {}, name: OPEN_NAME, shown: CASE_ID_TEXT },
    { row: 'a deleted row', recStatus: 0, values: {}, name: `${OPEN_NAME} (deleted)`, shown: CASE_ID_TEXT },
    // The next grid column, FirstName, is the first nonblank cell; the blank cell's button shows "Open".
    {
      row: 'a row with a blank first cell',
      recStatus: 1,
      values: { CaseID: null },
      name: 'Open record Ada',
      shown: 'Open',
    },
  ])('names and visibly labels the open action of $row, and opens that record by id', async (example) => {
    const { recStatus, values, name, shown } = example;
    mountForm(undefined, { route: (call) => (isGet(call, LIST_PATH) ? listPage(1, 1, recStatus, values) : null) });
    await recordOpened();
    const open = await within(records()).findByRole('button', { name });
    // The text drawn on screen: the button's content outside its visually hidden parts.
    const drawn = Array.from(open.querySelectorAll(':scope > :not(.visually-hidden)'), (part) => part.textContent);
    expect(drawn.join('')).toBe(shown);
    const row = present(open.closest('tr'), 'grid row');
    // The button is the row's only control and its one tab stop; the row takes no focus.
    expect(row.hasAttribute('tabindex')).toBe(false);
    expect(within(row).getAllByRole('button')).toEqual([open]);
    expect(open.getAttribute('type')).toBe('button');

    fireEvent.click(open);
    await vi.waitFor(() => {
      expect(openCalls()).toHaveLength(1);
    });
    await recordOpened();
  });

  // 150 characters, 149 of them outside the BMP, then 3850 more: a cut by UTF-16 units
  // would split a surrogate pair or keep too few characters.
  const LONG_START = `A${'𝒜'.repeat(149)}`;
  const LONG_TEXT = `${LONG_START}${'b'.repeat(3850)}`;
  const EXACT_TEXT = 'c'.repeat(150);
  it.each<{ row: string; values: RecordSummary['values']; name: string }>([
    { row: 'a first cell past 150 characters', values: { CaseID: LONG_TEXT }, name: `Open record ${LONG_START}…` },
    {
      row: 'a first cell of exactly 150 characters',
      values: { CaseID: EXACT_TEXT },
      name: `Open record ${EXACT_TEXT}`,
    },
    {
      row: 'a blank first cell before a cell past 150 characters',
      values: { CaseID: null, FirstName: LONG_TEXT },
      name: `Open record ${LONG_START}…`,
    },
  ])('names the open action of $row by at most 150 of its characters', async ({ values, name }) => {
    mountForm(undefined, {
      route: (call) => (isGet(call, LIST_PATH) ? listPage(1, 1, RECORD.recStatus, values) : null),
    });
    await recordOpened();
    expect(await within(records()).findByRole('button', { name })).toBeTruthy();
  });

  it('wraps the table in a named region that takes focus, so a keyboard can scroll it', async () => {
    const { definition } = await renderForm();
    const region = within(records()).getByRole('region', { name: 'Record list' });
    expect(within(region).getByRole('table', { name: `${definition.name} records` })).toBeTruthy();
    expect(region.tabIndex).toBe(0);
    region.focus();
    expect(document.activeElement).toBe(region);
  });

  it('titles each nonblank grid cell with its full value', async () => {
    mountForm(undefined, {
      route: (call) =>
        isGet(call, LIST_PATH) ? listPage(1, 1, RECORD.recStatus, { CaseID: LONG_TEXT, Sex: null }) : null,
    });
    await recordOpened();
    const open = await within(records()).findByRole('button', { name: `Open record ${LONG_START}…` });
    const cells = within(present(open.closest('tr'), 'grid row')).getAllByRole('cell');
    expect(cells.map((cell) => cell.getAttribute('title'))).toEqual([
      LONG_TEXT,
      'Ada',
      'Lovelace',
      null,
      '1980-05-01',
      '31',
    ]);
  });

  it('shows a "Show deleted" change as pending, ignoring further changes, until navigation applies it', async () => {
    // The toggle's one read: the open record's position in the set with deleted records.
    const position = deferred();
    const isDeletedPosition = (call: Call) =>
      isGet(call, `${OPEN_PATH}/position`) && call.url.searchParams.get('includeDeleted') === 'true';
    await renderForm(undefined, { route: (call) => (isDeletedPosition(call) ? position.response : null) });
    const toggle = within(records()).getByRole('checkbox', { name: 'Show deleted' });
    const state = () => ({
      checked: toggle.matches(':checked'),
      busy: toggle.getAttribute('aria-busy'),
      pendingStyle: present(toggle.closest('label'), 'toggle label').classList.contains('record-grid__toggle--pending'),
      rows: within(records()).getByRole('button', { name: OPEN_NAME }).getAttribute('aria-disabled'),
    });
    expect(state()).toEqual({ checked: false, busy: null, pendingStyle: false, rows: null });

    fireEvent.click(toggle);
    await vi.waitFor(() => {
      expect(calls.some(isDeletedPosition)).toBe(true);
    });
    const pending = { checked: true, busy: 'true', pendingStyle: true, rows: 'true' };
    expect(state()).toEqual(pending);
    // A second press while the first is pending changes nothing.
    fireEvent.click(toggle);
    expect(state()).toEqual(pending);

    position.release(jsonResponse(200, { position: 1, count: 1 } satisfies NavigationPosition));
    await vi.waitFor(() => {
      expect(listCalls().at(-1)?.url.searchParams.get('includeDeleted')).toBe('true');
    });
    await findInGrid('1 record');
    expect(state()).toEqual({ checked: true, busy: null, pendingStyle: false, rows: null });
  });

  it.each<{ list: string; answer: () => Response | Promise<Response>; status: string; retry: boolean; rows: boolean }>([
    { list: 'a page of records', answer: () => listPage(1, 1), status: '1 record', retry: false, rows: true },
    {
      list: 'no match',
      answer: () => jsonResponse(200, { items: [], total: 0, page: 1, pageSize: 50 } satisfies RecordPage),
      status: 'No records found.',
      retry: false,
      rows: false,
    },
    {
      list: 'a load aborted by something else',
      answer: () => Promise.reject(abortError()),
      status: 'The record list was not loaded.',
      retry: true,
      rows: false,
    },
  ])('reports $list in the one status, beside the search controls and before the table', async (example) => {
    mountForm(undefined, { route: (call) => (isGet(call, LIST_PATH) ? example.answer() : null) });
    await vi.waitFor(() => {
      expect(gridStatus()).toBe(example.status);
    });
    const region = records();
    const statuses = within(region).getAllByRole('status');
    expect(statuses).toHaveLength(1);
    const [status] = statuses;
    // In the controls' row: directly after the toggle, then its Retry when one is offered.
    const toggle = within(region).getByRole('checkbox', { name: 'Show deleted' });
    expect(present(toggle.closest('label'), 'toggle label').nextElementSibling).toBe(status);
    const retry = within(region).queryByRole('button', { name: 'Retry' });
    expect(retry !== null).toBe(example.retry);
    if (retry !== null) {
      expect(status.nextElementSibling).toBe(retry);
    }
    // The count is stated once, by the status, and the rows and the paging follow it.
    expect(within(region).getAllByText(example.status)).toEqual([status]);
    const table = within(region).queryByRole('table');
    expect(table !== null).toBe(example.rows);
    if (table !== null) {
      expect(status.compareDocumentPosition(table) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
      // The footer holds the paging alone.
      const paging = within(region).getByRole('navigation', { name: 'Record list pages' });
      const footer = present(paging.parentElement, 'paging footer');
      expect(footer.tagName).toBe('FOOTER');
      expect(Array.from(footer.children)).toEqual([paging]);
      expect(table.compareDocumentPosition(footer) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
      // Earlier and Later share one group, so they wrap as a pair, in reading order.
      const buttons = within(paging).getAllByRole('button');
      expect(buttons.map((button) => button.textContent)).toEqual(['Earlier records', 'Later records']);
      const group = present(buttons[0].parentElement, 'paging button group');
      expect(group).not.toBe(paging);
      expect(Array.from(group.children)).toEqual(buttons);
    } else {
      // No rows, no paging and no empty footer.
      expect(within(region).queryByRole('navigation')).toBeNull();
      expect(region.querySelector('footer')).toBeNull();
    }
  });

  it.each<{
    outcome: 'resets' | 'keeps';
    change: string;
    total: number;
    trigger: (definition: FormDefinition) => void;
  }>([
    {
      outcome: 'resets',
      change: 'a new search',
      total: 1,
      trigger: () => fireEvent.change(searchBox(), { target: { value: 'Ada' } }),
    },
    {
      outcome: 'resets',
      change: 'another page',
      total: 51,
      trigger: () => fireEvent.click(within(records()).getByRole('button', { name: 'Later records' })),
    },
    {
      outcome: 'resets',
      change: 'a "Show deleted" change',
      total: 1,
      trigger: () => fireEvent.click(within(records()).getByRole('checkbox', { name: 'Show deleted' })),
    },
    {
      outcome: 'keeps',
      change: 'a reload of the same page after a save',
      total: 1,
      trigger: (definition) => {
        replyToWrite({ kind: 'saved', record: savedWith(RECORD, { FirstName: 'Adah' }) });
        fireEvent.change(screen.getByLabelText(promptOf(fieldByName('FirstName', definition))), {
          target: { value: 'Adah' },
        });
        fireEvent.click(
          within(screen.getByRole('toolbar', { name: 'Form actions' })).getByRole('button', { name: 'Save' }),
        );
      },
    },
  ])('$outcome the sideways scroll of the rows on $change', async (example) => {
    // The first list request after the change is held, so the rows it replaces are seen pending.
    const next = deferred();
    let holding = false;
    const { definition } = mountForm(undefined, {
      route: (call) => {
        if (!isGet(call, LIST_PATH)) {
          return null;
        }
        const answer = listPage(example.total, Number(call.url.searchParams.get('page') ?? '1'));
        if (!holding) {
          return answer;
        }
        holding = false;
        return next.response.then(() => answer);
      },
    });
    const table = () => within(records()).getByRole('table', { name: `${definition.name} records` });
    await recordOpened();
    await vi.waitFor(() => {
      expect(table().getAttribute('aria-busy')).toBe('false');
    });
    const box = present(table().parentElement, 'scroll box');
    box.scrollLeft = 120;
    const before = listCalls().length;

    holding = true;
    example.trigger(definition);
    await vi.waitFor(() => {
      expect(listCalls().length).toBeGreaterThan(before);
    });
    // The old rows stay, pending, in the same scroll box at the user's offset.
    expect(table().getAttribute('aria-busy')).toBe('true');
    expect(box.scrollLeft).toBe(120);

    next.release(new Response(null));
    await vi.waitFor(() => {
      expect(table().getAttribute('aria-busy')).toBe('false');
    });
    // Another listing starts at the inline start; the same listing keeps the user's offset.
    expect(table().parentElement).toBe(box);
    expect(box.scrollLeft).toBe(example.outcome === 'keeps' ? 120 : 0);
  });

  it.each<{
    outcome: 'restores' | 'leaves';
    control: string;
    focused: () => HTMLElement | null;
    change: () => void;
    /** The control's viewport top before the change. */
    top: number;
  }>([
    {
      outcome: 'restores',
      control: 'the search box in view',
      focused: () => searchBox(),
      change: () => fireEvent.change(searchBox(), { target: { value: 'Ada' } }),
      top: 80,
    },
    {
      outcome: 'restores',
      control: '"Show deleted" in view',
      focused: () => within(records()).getByRole('checkbox', { name: 'Show deleted' }),
      change: () => fireEvent.click(within(records()).getByRole('checkbox', { name: 'Show deleted' })),
      top: 80,
    },
    {
      outcome: 'leaves',
      control: 'the search box scrolled out of view',
      focused: () => searchBox(),
      change: () => fireEvent.change(searchBox(), { target: { value: 'Ada' } }),
      top: -100,
    },
    {
      outcome: 'leaves',
      control: 'no grid control',
      focused: () => null,
      change: () => fireEvent.change(searchBox(), { target: { value: 'Ada' } }),
      top: 80,
    },
  ])('$outcome a window offset a shorter list clamps while $control has focus', async (example) => {
    // jsdom lays nothing out and never scrolls: the window's offset is a value the case
    // sets, scrollTo only records its calls, and the case gives the boxes it reads.
    let offset = 0;
    const ownScrollY = Object.getOwnPropertyDescriptor(window, 'scrollY');
    Object.defineProperty(window, 'scrollY', { configurable: true, get: () => offset });
    try {
      const scrollTo = vi.spyOn(window, 'scrollTo').mockImplementation(() => undefined);
      let hold: ReturnType<typeof deferred> | null = null;
      await renderForm(undefined, {
        route: (call) => {
          if (hold === null || !isGet(call, LIST_PATH)) {
            return null;
          }
          const { response } = hold;
          hold = null;
          return response.then(() => listPage(1, 1));
        },
      });
      const region = records();
      const busy = () => within(region).getByRole('table').getAttribute('aria-busy');
      /** Runs `change` with the list request it makes held, then answers it once `offset` is `after`. */
      const changeList = async (change: () => void, after: number) => {
        const answer = deferred();
        hold = answer;
        change();
        await vi.waitFor(() => {
          expect(busy()).toBe('true');
        });
        offset = after;
        answer.release(new Response(null));
        await vi.waitFor(() => {
          expect(busy()).toBe('false');
        });
      };

      // The user has scrolled the page 600 px; the grid's controls sit at example.top.
      offset = 600;
      window.dispatchEvent(new Event('scroll'));
      example.focused()?.focus();
      // The shorter list's clamp takes 200 px off the offset and moves the controls down by as much.
      const shortfall = 200;
      for (const control of [searchBox(), within(region).getByRole('checkbox', { name: 'Show deleted' })]) {
        vi.spyOn(control, 'getBoundingClientRect').mockReturnValue(new DOMRect(0, example.top + shortfall, 240, 44));
      }
      vi.spyOn(region, 'getBoundingClientRect').mockReturnValue(new DOMRect(0, -400, 1000, 1000));

      await changeList(example.change, offset - shortfall);
      if (example.outcome === 'restores') {
        // The section keeps the height the old offset needs, and the window returns to it.
        expect(region.style.minBlockSize).toBe(`${1000 + shortfall}px`);
        expect(scrollTo.mock.calls).toEqual([[{ left: 0, top: 600, behavior: 'instant' }]]);
        offset = 600;
      } else {
        expect(region.style.minBlockSize).toBe('');
        expect(scrollTo).not.toHaveBeenCalled();
      }
      window.dispatchEvent(new Event('scroll'));

      // The next change of the list releases the hold; with no clamp, nothing scrolls.
      await changeList(() => fireEvent.change(searchBox(), { target: { value: 'Adam' } }), offset);
      expect(region.style.minBlockSize).toBe('');
      expect(scrollTo).toHaveBeenCalledTimes(example.outcome === 'restores' ? 1 : 0);
    } finally {
      if (ownScrollY === undefined) {
        Reflect.deleteProperty(window, 'scrollY');
      } else {
        Object.defineProperty(window, 'scrollY', ownScrollY);
      }
    }
  });

  it('re-renders no grid row and sends no list request while a field is edited', async () => {
    const { definition } = await renderForm();
    const renders = gridRenders.count;
    const lists = listCalls().length;
    expect(renders).toBeGreaterThan(0);

    const firstName = screen.getByLabelText(promptOf(fieldByName('FirstName', definition)));
    for (const value of ['A', 'Ad', 'Ada', 'Adah']) {
      fireEvent.change(firstName, { target: { value } });
    }
    expect(controlValue(firstName)).toBe('Adah');
    expect(gridRenders.count).toBe(renders);
    expect(listCalls()).toHaveLength(lists);

    // The probe stands for the real export, which is memoised itself.
    const actual = await vi.importActual<typeof import('./RecordGrid')>('./RecordGrid');
    expect(actual.RecordGrid.$$typeof).toBe(Symbol.for('react.memo'));
  });
});
