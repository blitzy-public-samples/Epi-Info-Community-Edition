// SC-8, client half: the form view renders a form from its metadata with an accessible
// structure. Each data field is one native control labelled by its prompt, an option
// field is a radio group named by its legend, label fields are headings, a group is a
// section headed by its prompt that holds its same-page members in `tabIndex` order,
// every page is one tab, and every field renders exactly once.
//
// FormView runs against the real client, hooks and field components. The client's fetch
// seam is an in-memory API that answers the read routes of one form and its stored active
// records: one record by default, two or none where a case asks. A PUT of a stored record
// or a POST of a new one is answered only with a reply the case queued, at once or held
// until the case releases it, and a saved reply updates the stored records. Any other
// request, and a write no reply was queued for, is answered 500 and recorded, and fails
// the test, so loading the form is also shown to send GETs only and raise no field event
// (DATA-01).
//
// The write cases drive the editor through FormView: a dirty draft is saved before a grid
// open or Back to forms continues, and a failed save keeps the draft and cancels the move;
// the first Save of a new record follows canInsert and Save of a stored record canUpdate,
// and a 403 keeps the draft; the validation summary lists each errored field's prompt
// with its message, from client validation or a field-level 400; and the toolbar
// announces Saving… while a save is under way. The connection-banner cases answer their
// own outage reads and failed writes, and check the banner each shows.
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { Profiler } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi, type Mock } from 'vitest';

import { createApiClient } from '../api/client';
import type {
  FieldDefinition,
  FieldEventResponse,
  FieldType,
  FormDefinition,
  HttpValidationProblemDetails,
  NavigationPosition,
  PageDefinition,
  ProblemDetails,
  RecordCountResponse,
  RecordDto,
  RecordPage,
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
import { formatRequiredMessage } from '../validation/clientValidation';
import { FormView } from './FormView';
import { fieldComponents, fieldControlId, fieldErrorId, type FieldComponentProps } from './fields/fieldComponents';

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
  // A request no route answers fails the case: a field event, which no case here raises,
  // or a write the case queued no reply for.
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

function jsonResponse(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
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

/** The answer to a field event in a case that moves focus into a field: no effects. */
const NO_EFFECTS: FieldEventResponse = {
  enabled: [],
  disabled: [],
  cleared: [],
  assigned: {},
  dialogs: [],
  diagnostics: [],
};

/**
 * A fetch that serves `definition`'s form and `records`, answers each write with the next
 * queued reply, answers field events with no effects when `answersFieldEvents` is set, and
 * records every request it receives.
 */
function fakeApi(definition: FormDefinition, records: RecordDto[], answersFieldEvents = false): typeof fetch {
  return async (input, init) => {
    const request = input instanceof Request ? input : new Request(input, init);
    const url = new URL(request.url);
    const text = await request.text();
    const call: Call = { method: request.method, url, body: text === '' ? null : text };
    calls.push(call);

    const body = readRouteBody(call.method, url.pathname, definition, records);
    if (body !== null) {
      return jsonResponse(200, body);
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

// Rendering

interface RenderedForm {
  readonly container: HTMLElement;
  readonly definition: FormDefinition;
  readonly onExit: Mock<() => void>;
  readonly onFormNotFound: Mock<() => void>;
}

interface RenderOptions {
  /** The form's stored active records in position order; by default the one RECORD. */
  readonly records?: readonly RecordDto[];
  /** Answer field events with no effects, for a case that moves focus into a field. */
  readonly answersFieldEvents?: boolean;
  /** Answers a GET of `path` before the read routes do, for a case that fails a read; `undefined` leaves it to them. */
  readonly answerRead?: (path: string) => Response | undefined;
  /** Called at the end of each commit of the form view, in its layout phase, after the form view's layout effects. */
  readonly onCommit?: () => void;
}

/**
 * Renders the form view and waits until the definition, the first record (New on an
 * empty form) and the grid have loaded.
 */
async function renderForm(
  definition: FormDefinition = createFormDefinitionFixture(),
  options: RenderOptions = {},
): Promise<RenderedForm> {
  // A copy, so the writes of one case never change the records another case stores.
  const records = [...(options.records ?? [RECORD])];
  const onExit = vi.fn<() => void>();
  const onFormNotFound = vi.fn<() => void>();
  const serve = fakeApi(definition, records, options.answersFieldEvents ?? false);
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

  const count = records.length;
  await screen.findByRole('heading', { name: definition.name });
  await within(screen.getByRole('toolbar', { name: 'Record navigation' })).findByText(
    count === 0 ? 'New record' : `1 of ${count}`,
  );
  await within(screen.getByRole('region', { name: 'Records' })).findByText(
    count === 1 ? '1 record' : `${count} records`,
  );
  return { container, definition, onExit, onFormNotFound };
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
  date: { tagName: 'INPUT', type: 'date' },
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
    const message = formatRequiredMessage(severity.prompt, pageOf(definition, severity.pageId).name);
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
  // any case; any other pattern, or none, shows the date in the user's locale.
  it.each([
    { name: 'DOB', stored: '1980-05-01', pattern: 'MM-DD-YYYY', shown: '05-01-1980' },
    { name: 'DOB', stored: '1980-05-01', pattern: 'DD-MM-YYYY', shown: '01-05-1980' },
    { name: 'DOB', stored: '1980-05-01', pattern: 'yyyy/mm/dd', shown: '1980/05/01' },
    { name: 'DOB', stored: '1980-05-01', pattern: null, shown: localeDate('1980-05-01') },
    { name: 'DOB', stored: '1980-05-01', pattern: 'MM-DD', shown: localeDate('1980-05-01') },
    { name: 'DOB', stored: '1980-05-01', pattern: 'MM-DD-YYYY HH:MM:SS AMPM', shown: localeDate('1980-05-01') },
    { name: 'OnsetDate', stored: null, pattern: 'MM-DD-YYYY', shown: '' },
  ])(
    'renders the read-only date $name, stored $stored with pattern $pattern, as a disabled read-only text box showing $shown, and writable dates as date inputs',
    async ({ name, stored, pattern, shown }) => {
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

      const writableDates = allFields(definition).filter(
        (field) => field.fieldType === 'date' && field.name !== name,
      );
      expect(writableDates).not.toHaveLength(0);
      for (const field of writableDates) {
        const input = present(wrapperOf(container, field.name).querySelector('input'), `control of ${field.name}`);
        const value = RECORD.values[field.name] ?? null;
        expect(input.getAttribute('type'), field.name).toBe('date');
        expect(isDisabled(input), field.name).toBe(false);
        expect(input.hasAttribute('readonly'), field.name).toBe(false);
        expect(controlValue(input), field.name).toBe(typeof value === 'string' ? value : '');
      }
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
  // these name members every object inherits.
  it.each(['constructor', 'toString', '__proto__', 'hasOwnProperty', 'valueOf'])(
    'renders a field of the malformed type %s as a protected placeholder',
    async (malformed) => {
      const fieldType: string = malformed;
      const { container } = await renderForm(withField('Zip', (field) => ({ ...field, fieldType: fieldType as FieldType })));
      const wrapper = wrapperOf(container, 'Zip');

      expect(within(wrapper).getByText('Unsupported field type: unknown')).toBeTruthy();
      expect(wrapper.classList.contains('field--protected')).toBe(true);
      expect(wrapper.querySelectorAll(`${NATIVE_CONTROLS}, button`)).toHaveLength(0);
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
  it('announces Saving… in the form toolbar while a save is under way, with Save disabled and still named Save', async () => {
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
    await waitFor(() => {
      expect(status.textContent).toBe('');
    });

    expect(isDisabled(save)).toBe(false);
    const writes = writeCalls();
    expect(writes.map(requestLine)).toEqual([`PUT ${FORM_PATH}/records/${RECORD.globalRecordId}`]);
    expect(writtenValues(writes[0])).toEqual({ LastName: EDITED_LAST_NAME });
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
      return jsonResponse(503, UNAVAILABLE_PROBLEM);
    }
    const body: WriteProblemDetails = { ...UNAVAILABLE_PROBLEM, writeOutcome };
    return jsonResponse(503, body);
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

  it.each<{
    label: string;
    create: boolean;
    failure: () => Answer;
    saveAgainName: string;
    duplicate: boolean;
  }>([
    {
      label: 'a create that got no response',
      create: true,
      failure: () => 'no response',
      saveAgainName: 'Save again (this may create a duplicate record)',
      duplicate: true,
    },
    {
      label: 'an update answered 503 with writeOutcome unknown',
      create: false,
      failure: () => unavailable('unknown'),
      saveAgainName: 'Save again (the earlier save may already have been applied)',
      duplicate: false,
    },
  ])('shows the risk before an explicit Save again, and no Retry, for $label', async ({
    create,
    failure,
    saveAgainName,
    duplicate,
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
    const dialog = await screen.findByRole('dialog');
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

    // The open lands on the nearest record with a notice that offers Dismiss, and moves no focus.
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
    expect(document.activeElement).toBe(document.body);

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
