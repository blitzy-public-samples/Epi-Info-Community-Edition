// SC-8, client half: the form view renders a form from its metadata with an accessible
// structure. Each data field is one native control labelled by its prompt, an option
// field is a radio group named by its legend, label fields are headings, a group is a
// section headed by its prompt that holds its same-page members in `tabIndex` order,
// every page is one tab, and every field renders exactly once.
//
// FormView runs against the real client, hooks and field components. The client's fetch
// seam is an in-memory API that answers the read routes of one form with one record; any
// other request is answered 500 and recorded, and fails the test, so loading the form is
// also shown to send GETs only and raise no field event (DATA-01).
import { fireEvent, render, screen, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi, type Mock } from 'vitest';

import { createApiClient } from '../api/client';
import type {
  FieldDefinition,
  FieldType,
  FormDefinition,
  NavigationPosition,
  PageDefinition,
  ProblemDetails,
  RecordCountResponse,
  RecordDto,
  RecordPage,
  RecordSummary,
} from '../api/types';
import {
  allFields,
  createFormDefinitionFixture,
  dataFieldNames,
  fieldByName,
  readOnlyFieldNames,
  recordFixture,
  withField,
} from '../test/formDefinitionFixture';
import { FormView } from './FormView';
import { fieldControlId } from './fields/fieldComponents';

// The in-memory API

interface Call {
  readonly method: string;
  readonly url: URL;
  /** The request body text; `null` when the request has none. */
  readonly body: string | null;
}

const calls: Call[] = [];
const unrouted: string[] = [];

beforeEach(() => {
  calls.length = 0;
  unrouted.length = 0;
});

afterEach(() => {
  // A request outside the read routes is a write or a field event, which no case here causes.
  expect(unrouted).toEqual([]);
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

const GRID_FIELD_TYPES: ReadonlySet<FieldType> = new Set<FieldType>(['text', 'number', 'date', 'legalValues']);

/** The record's grid row: the server's six grid columns, by page position and then `tabIndex`. */
function summaryOf(definition: FormDefinition, record: RecordDto): RecordSummary {
  const columns = allFields(definition)
    .filter((field) => GRID_FIELD_TYPES.has(field.fieldType))
    .slice(0, 6);
  return {
    globalRecordId: record.globalRecordId,
    uniqueKey: record.uniqueKey,
    recStatus: record.recStatus,
    values: Object.fromEntries(columns.map((field) => [field.name, record.values[field.name] ?? null])),
  };
}

const UNROUTED_PROBLEM: ProblemDetails = {
  type: 'https://tools.ietf.org/html/rfc9110#section-15.6.1',
  title: 'An error occurred while processing your request.',
  status: 500,
  detail: 'The test API has no route for this request.',
  traceId: '00-test-unrouted-00',
};

function jsonResponse(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

/** The body of a read route of `definition`'s form, or `null` when the request matches none. */
function readRouteBody(method: string, path: string, definition: FormDefinition): unknown {
  if (method !== 'GET') {
    return null;
  }
  const form = `/api/forms/${definition.viewId}`;
  const record = `${form}/records/${RECORD.globalRecordId}`;
  switch (path) {
    case form:
      return definition;
    case `${form}/records`: {
      const page: RecordPage = { items: [summaryOf(definition, RECORD)], total: 1, page: 1, pageSize: 50 };
      return page;
    }
    case `${form}/records/count`: {
      const count: RecordCountResponse = { count: 1 };
      return count;
    }
    case `${form}/records/at/1`:
    case record:
      return RECORD;
    case `${record}/position`: {
      const position: NavigationPosition = { position: 1, count: 1 };
      return position;
    }
    default:
      return null;
  }
}

/** A fetch that serves `definition`'s form and records every request it receives. */
function fakeApi(definition: FormDefinition): typeof fetch {
  return async (input, init) => {
    const request = input instanceof Request ? input : new Request(input, init);
    const url = new URL(request.url);
    const text = await request.text();
    const call: Call = { method: request.method, url, body: text === '' ? null : text };
    calls.push(call);

    const body = readRouteBody(call.method, url.pathname, definition);
    if (body === null) {
      unrouted.push(requestLine(call));
      return jsonResponse(500, UNROUTED_PROBLEM);
    }
    return jsonResponse(200, body);
  };
}

// Rendering

interface RenderedForm {
  readonly container: HTMLElement;
  readonly definition: FormDefinition;
  readonly onExit: Mock<() => void>;
  readonly onFormNotFound: Mock<() => void>;
}

/** Renders the form view and waits until the definition, the record and the grid have loaded. */
async function renderForm(definition: FormDefinition = createFormDefinitionFixture()): Promise<RenderedForm> {
  const onExit = vi.fn<() => void>();
  const onFormNotFound = vi.fn<() => void>();
  // Absolute: openapi-fetch builds a Request, and Node's Request rejects a relative URL.
  const client = createApiClient({ baseUrl: 'http://localhost', fetch: fakeApi(definition) });
  const { container } = render(
    <FormView viewId={definition.viewId} client={client} onExit={onExit} onFormNotFound={onFormNotFound} />,
  );

  await screen.findByRole('heading', { name: definition.name });
  await within(screen.getByRole('toolbar', { name: 'Record navigation' })).findByText('1 of 1');
  await within(screen.getByRole('region', { name: 'Records' })).findByText('1 record');
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
  it('renders one tab per page in position order, and the selected tab shows its panel', async () => {
    const { definition } = await renderForm();
    const pages = [...definition.pages].sort((a, b) => a.position - b.position);
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
    const { definition } = await renderForm();

    for (const page of definition.pages) {
      // The fixture's group members directly follow the group in tabIndex, so the group's
      // nesting keeps document order equal to tabIndex order; Black's tabIndex is fractional.
      expect(renderedFieldNames(panelOf(page))).toEqual([...page.fields].sort(byTabIndex).map((field) => field.name));
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
        expect(screen.getByRole('group', { name: promptOf(field) })).toBe(fieldset);

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
  });

  it('labels a control by the field name when the prompt is null', async () => {
    const definition = withField('PatientAddress', (field) => ({ ...field, prompt: null }));
    const { container } = await renderForm(definition);

    const control = screen.getByLabelText('PatientAddress');
    expect(control.tagName).toBe('TEXTAREA');
    expect(wrapperOf(container, 'PatientAddress').contains(control)).toBe(true);
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

    // The browser test matches button names by substring, so no other button may contain these words.
    for (const word of ['Save', 'Delete', 'Find', 'First', 'Previous', 'Next', 'Last', 'New']) {
      expect(
        screen.getAllByRole('button', { name: (accessibleName) => accessibleName.includes(word) }),
        word,
      ).toHaveLength(1);
    }
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
});
