// P2 command-line switch mapping, client half. Legacy Enter read `/view:<name>` and
// `/record:<id>` from its command line (Epi.Windows.Enter/EntryPoint.cs); the web shell
// reads `?view=<form name>&record=<1-based position>` once on load, opens that form at
// that record, and keeps the query string in step while the user moves between records.
// `title` and `project` are ignored: the page title is fixed and the API's configured
// connection string is the project.
//
// The shell runs over the real client, FormView, hooks and field components. The client's
// fetch seam is an in-memory API serving one form, `CaseReport` (view 1), with three
// active records and, between the first two in UniqueKey order, one deleted record, which
// only `includeDeleted=true` selects; every request is logged, and one it does not serve
// is answered 404 and fails the test, so opening a form from the URL is also shown to send
// GETs only and to raise no field event (DATA-01).
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import App from './App';
import { createApiClient, type ApiClient } from './api/client';
import type {
  FormDefinition,
  FormSummary,
  NavigationPosition,
  ProblemDetails,
  RecordCountResponse,
  RecordDto,
  RecordPage,
  RecordSummary,
} from './api/types';
import { createFormDefinitionFixture, recordFixture } from './test/formDefinitionFixture';

// The in-memory API

// Absolute: openapi-fetch builds a Request, and Node's Request rejects a relative URL.
const API_ORIGIN = 'http://localhost';

/** The fixture form under the seeded `CaseReport` identity, which the URL names. */
const FORM: FormDefinition = { ...createFormDefinitionFixture(), viewId: 1, name: 'CaseReport' };
const FORM_PATH = `/api/forms/${String(FORM.viewId)}`;

const FORMS: FormSummary[] = [
  { viewId: FORM.viewId, name: FORM.name, pageCount: FORM.pages.length },
  { viewId: 2, name: 'FoodHistory', pageCount: 2 },
];

/** `LastName` of the active record at each position, so the open record is told apart on screen. */
const LAST_NAMES = ['First', 'Second', 'Third'] as const;

/** UniqueKey of each active record: key 2 is the deleted record's. */
const ACTIVE_KEYS = [1, 3, 4] as const;

/** The active set in `UniqueKey` order; each Record DTO carries its own active-set position. */
const RECORDS: readonly RecordDto[] = LAST_NAMES.map((lastName, index) =>
  recordFixture({
    globalRecordId: `3f2b8c1e-5d4a-4c1b-9e7f-00000000000${String(ACTIVE_KEYS[index])}`,
    uniqueKey: ACTIVE_KEYS[index],
    position: index + 1,
    count: LAST_NAMES.length,
    values: { LastName: lastName },
  }),
);

/** The deleted record, at position 2 of every record; its own coordinates are over every record. */
const DELETED_RECORD: RecordDto = recordFixture({
  globalRecordId: '3f2b8c1e-5d4a-4c1b-9e7f-000000000002',
  uniqueKey: 2,
  recStatus: 0,
  position: 2,
  count: RECORDS.length + 1,
  values: { LastName: 'Gone' },
});

/** Every record in `UniqueKey` order: the set `includeDeleted=true` selects. */
const ALL_RECORDS: readonly RecordDto[] = [RECORDS[0], DELETED_RECORD, ...RECORDS.slice(1)];

function summaryOf(record: RecordDto): RecordSummary {
  return {
    globalRecordId: record.globalRecordId,
    uniqueKey: record.uniqueKey,
    recStatus: record.recStatus,
    values: { LastName: record.values.LastName ?? null },
  };
}

function pageOf(set: readonly RecordDto[]): RecordPage {
  return { items: set.map(summaryOf), total: set.length, page: 1, pageSize: 50 };
}

const AT_PATTERN = /^\/records\/at\/(\d+)$/;
const POSITION_PATTERN = /^\/records\/([^/]+)\/position$/;

/** One request the API received. */
interface ApiRequest {
  readonly method: string;
  readonly origin: string;
  readonly path: string;
  readonly query: URLSearchParams;
  /** Method, path and query string, as a request line reads. */
  readonly line: string;
}

/** The API's answer to one request. */
interface Answer {
  readonly status: 200 | 404 | 500 | 503;
  readonly body: unknown;
}

/**
 * A failed answer to `GET /api/forms`: an error response, an OK response whose body is not a
 * forms list, or `'noResponse'` for a fetch that rejects.
 */
type FormsFailure = Answer | 'noResponse';

const requests: ApiRequest[] = [];
/** Request lines no route serves: a write, a field event or a read this shell must not send. */
const unexpected: string[] = [];
/** Replaces the answer to `GET /api/forms` while a case sets it; `null` serves `FORMS`. Reset before each case. */
let formsFailure: FormsFailure | null = null;
/** Set by a case whose form definition answers 404, as a form deleted since the list was read. Reset before each case. */
let formMissing = false;

function notFound(detail: string): Answer {
  const problem: ProblemDetails = {
    type: 'https://tools.ietf.org/html/rfc9110#section-15.5.5',
    title: 'Not Found',
    status: 404,
    detail,
    traceId: '00-test-not-found-00',
  };
  return { status: 404, body: problem };
}

/** The API's 503: the database cannot be reached. */
function unavailable(): Answer {
  const problem: ProblemDetails = {
    type: 'https://tools.ietf.org/html/rfc9110#section-15.6.4',
    title: 'Service Unavailable',
    status: 503,
    detail: 'the database is unavailable',
    traceId: '00-test-unavailable-00',
  };
  return { status: 503, body: problem };
}

/** The API's 500: an unexpected failure, reported with its trace id. */
function serverError(): Answer {
  const problem: ProblemDetails = {
    type: 'https://tools.ietf.org/html/rfc9110#section-15.6.1',
    title: 'Internal Server Error',
    status: 500,
    detail: 'An unexpected error occurred.',
    traceId: '00-test-server-error-00',
  };
  return { status: 500, body: problem };
}

function ok(body: unknown): Answer {
  return { status: 200, body };
}

/**
 * The answer of a read route, or `null` when no route serves the request. `includeDeleted`
 * selects the set, as the API's toggle does: `true` every record, absent or `false` the
 * active set.
 */
function route(method: string, path: string, query: URLSearchParams): Answer | null {
  if (method !== 'GET') {
    return null;
  }
  const set = query.get('includeDeleted') === 'true' ? ALL_RECORDS : RECORDS;
  if (path === '/api/forms') {
    // `'noResponse'` never reaches a route: the fetch rejects first.
    return formsFailure === null || formsFailure === 'noResponse' ? ok(FORMS) : formsFailure;
  }
  if (path === FORM_PATH) {
    return formMissing ? notFound('The form does not exist.') : ok(FORM);
  }
  if (!path.startsWith(`${FORM_PATH}/`)) {
    return null;
  }

  const rest = path.slice(FORM_PATH.length);
  if (rest === '/records') {
    return ok(pageOf(set));
  }
  if (rest === '/records/count') {
    const count: RecordCountResponse = { count: set.length };
    return ok(count);
  }

  const at = AT_PATTERN.exec(rest);
  if (at !== null) {
    // A position outside 1..count is the API's documented 404, not an unexpected request.
    const record = set[Number(at[1]) - 1];
    return record === undefined ? notFound('The record does not exist.') : ok(record);
  }

  const position = POSITION_PATTERN.exec(rest);
  if (position !== null) {
    if (!ALL_RECORDS.some((record) => record.globalRecordId === position[1])) {
      return notFound('The record does not exist.');
    }
    // A record outside the selected set has no position in it.
    const index = set.findIndex((record) => record.globalRecordId === position[1]);
    const answer: NavigationPosition = { position: index < 0 ? null : index + 1, count: set.length };
    return ok(answer);
  }

  return null;
}

function jsonResponse({ status, body }: Answer): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

/** The client's fetch: logs every request and answers it from the read routes. */
const fakeFetch: typeof fetch = (input, init) => {
  const request = input instanceof Request ? input : new Request(input, init);
  const url = new URL(request.url);
  const entry: ApiRequest = {
    method: request.method,
    origin: url.origin,
    path: url.pathname,
    query: url.searchParams,
    line: `${request.method} ${url.pathname}${url.search}`,
  };
  requests.push(entry);

  if (formsFailure === 'noResponse' && entry.method === 'GET' && entry.path === '/api/forms') {
    // A lost response: fetch rejects, as it does when the server cannot be reached.
    return Promise.reject(new TypeError('Failed to fetch'));
  }
  const answer = route(entry.method, entry.path, entry.query);
  if (answer === null) {
    unexpected.push(entry.line);
    return Promise.resolve(jsonResponse(notFound('The test API has no route for this request.')));
  }
  return Promise.resolve(jsonResponse(answer));
};

beforeEach(() => {
  requests.length = 0;
  unexpected.length = 0;
  formsFailure = null;
  formMissing = false;
  window.history.replaceState(null, '', '/?view=CaseReport&record=2');
});

afterEach(() => {
  expect(unexpected, 'requests no route serves').toEqual([]);
});

// Rendering and DOM helpers

/** Renders the shell at the current URL; the returned function unmounts it, as closing the tab does. */
function renderApp(): () => void {
  const client: ApiClient = createApiClient({ baseUrl: API_ORIGIN, fetch: fakeFetch });
  return render(<App client={client} />).unmount;
}

function lastNameInput(): HTMLInputElement {
  const element = screen.getByLabelText('Last Name', { selector: 'input' });
  if (!(element instanceof HTMLInputElement)) {
    throw new Error('the Last Name control is not an input');
  }
  return element;
}

/**
 * Waits until the navigator reads "<position> of <N>" over `set`, the active set unless
 * given, and the form shows that set's record at `position`.
 */
async function expectOpenRecord(position: number, set: readonly RecordDto[] = RECORDS): Promise<void> {
  const navigator = await screen.findByRole('toolbar', { name: 'Record navigation' });
  await waitFor(() => {
    expect(within(navigator).getByRole('status').textContent).toBe(`${String(position)} of ${String(set.length)}`);
  });
  await waitFor(() => {
    expect(lastNameInput().value).toBe(set[position - 1].values.LastName);
  });
}

function showDeletedCheckbox(): HTMLInputElement {
  const element = screen.getByRole('checkbox', { name: 'Show deleted' });
  if (!(element instanceof HTMLInputElement)) {
    throw new Error('the Show deleted control is not an input');
  }
  return element;
}

/** Waits for the status banner whose message reads `text`, as FormView renders a navigation notice. */
function findNavigationNotice(text: string): Promise<HTMLElement> {
  return waitFor(() => {
    const banner = screen.getAllByRole('status').find((element) => element.firstElementChild?.textContent === text);
    if (banner === undefined) {
      throw new Error(`no status banner reads "${text}"`);
    }
    return banner;
  });
}

const SET_READ_PATTERN = /^\/api\/forms\/\d+\/records(\/count|\/at\/\d+)?$/;

/** `includeDeleted` of every count, `at` and list request, in order: each names the set it reads. */
function setsRead(): (string | null)[] {
  return requests
    .filter((request) => SET_READ_PATTERN.test(request.path))
    .map((request) => request.query.get('includeDeleted'));
}

async function expectUrlSearch(search: string): Promise<void> {
  await waitFor(() => {
    expect(window.location.search).toBe(search);
  });
  expect(window.location.pathname).toBe('/');
}

/** Method and path of every request, without the query string. */
function requestedPaths(): string[] {
  return requests.map((request) => `${request.method} ${request.path}`);
}

/** DATA-01: opening a form and its records sends GETs only and raises no field event. */
function expectReadsOnly(): void {
  expect(requests.filter((request) => request.method !== 'GET').map((request) => request.line)).toEqual([]);
  expect(requests.filter((request) => request.path.endsWith('/checkcode/field-event'))).toEqual([]);
}

// Cases

describe('App URL parameters', () => {
  it.each([
    { name: 'the user example', url: '/?view=CaseReport&record=2' },
    { name: 'a view name in another case', url: '/?view=casereport&record=2' },
    { name: 'title and project, which are ignored', url: '/?view=CaseReport&record=2&title=X&project=Y' },
  ])('opens record 2 of CaseReport for $name ($url)', async ({ url }) => {
    window.history.replaceState(null, '', url);
    renderApp();

    await expectOpenRecord(2);
    await expectUrlSearch('?view=CaseReport&record=2');
    // A record the form has needs no notice.
    expect(screen.queryByRole('button', { name: 'Dismiss' })).toBeNull();

    expect(screen.getByRole('heading', { level: 1 }).textContent).toBe('Epi Info Data Entry');
    expect(screen.getByRole('heading', { level: 2, name: 'CaseReport' })).toBeDefined();
    expect(requestedPaths()).toContain(`GET ${FORM_PATH}`);
    expect(requestedPaths()).toContain(`GET ${FORM_PATH}/records/at/2`);
    // `project` names no other API: every request goes to the client's own origin.
    expect(new Set(requests.map((request) => request.origin))).toEqual(new Set([API_ORIGIN]));
    expectReadsOnly();
  });

  it('keeps the URL in step when Next moves to the following record', async () => {
    renderApp();
    await expectOpenRecord(2);
    await expectUrlSearch('?view=CaseReport&record=2');
    const historyLength = window.history.length;

    fireEvent.click(
      within(screen.getByRole('toolbar', { name: 'Record navigation' })).getByRole('button', { name: 'Next' }),
    );

    await expectOpenRecord(3);
    await expectUrlSearch('?view=CaseReport&record=3');
    // The address bar is rewritten in place: moving between records adds no history entry.
    expect(window.history.length).toBe(historyLength);
    expect(requestedPaths()).toContain(`GET ${FORM_PATH}/records/at/3`);
    expectReadsOnly();
  });

  // Record 3 of every record is `Second`, record 3 of the active set is `Third`.
  it.each([
    { deleted: '1', on: true },
    { deleted: 'true', on: true },
    { deleted: ' TRUE ', on: true },
    { deleted: '0', on: false },
    { deleted: 'false', on: false },
    { deleted: 'yes', on: false },
    { deleted: '', on: false },
  ])('reads deleted=$deleted as Show deleted $on, so record=3 counts the set it selects', async ({ deleted, on }) => {
    window.history.replaceState(null, '', `/?view=CaseReport&record=3&deleted=${encodeURIComponent(deleted)}`);
    renderApp();

    await expectOpenRecord(3, on ? ALL_RECORDS : RECORDS);
    // The URL carries the toggle only while it is on, so any other value is written back without it.
    await expectUrlSearch(on ? '?view=CaseReport&record=3&deleted=1' : '?view=CaseReport&record=3');
    await waitFor(() => {
      expect(showDeletedCheckbox().checked).toBe(on);
    });
    // The first count, record and list read the set the URL asked for, and none reads the other.
    await waitFor(() => {
      expect(requestedPaths()).toContain(`GET ${FORM_PATH}/records`);
    });
    expect(requestedPaths()).toContain(`GET ${FORM_PATH}/records/at/3`);
    expect(new Set(setsRead())).toEqual(new Set([String(on)]));
    expectReadsOnly();
  });

  it('writes the toggle with the position, so the written URL reopens the same record', async () => {
    const unmount = renderApp();
    await expectOpenRecord(2);
    expect(lastNameInput().value).toBe('Second');

    fireEvent.click(showDeletedCheckbox());

    // `Second` is record 3 of every record: the deleted record comes before it.
    await expectOpenRecord(3, ALL_RECORDS);
    await expectUrlSearch('?view=CaseReport&record=3&deleted=1');
    expect(showDeletedCheckbox().checked).toBe(true);

    // Loading the URL the shell wrote opens the same record, in the same set.
    unmount();
    renderApp();

    await expectOpenRecord(3, ALL_RECORDS);
    expect(lastNameInput().value).toBe('Second');
    await expectUrlSearch('?view=CaseReport&record=3&deleted=1');
    await waitFor(() => {
      expect(showDeletedCheckbox().checked).toBe(true);
    });

    // Switching it off writes the record's active-set position and drops the toggle.
    fireEvent.click(showDeletedCheckbox());

    await expectOpenRecord(2);
    expect(lastNameInput().value).toBe('Second');
    await expectUrlSearch('?view=CaseReport&record=2');
    expect(showDeletedCheckbox().checked).toBe(false);
    expectReadsOnly();
  });

  it('writes a toggle change that leaves the position as it was, as on a new record', async () => {
    renderApp();
    await expectOpenRecord(2);
    const navigator = screen.getByRole('toolbar', { name: 'Record navigation' });

    fireEvent.click(within(navigator).getByRole('button', { name: 'New' }));
    await waitFor(() => {
      expect(within(navigator).getByRole('status').textContent).toBe('New record');
    });
    await expectUrlSearch('?view=CaseReport');

    fireEvent.click(showDeletedCheckbox());
    await expectUrlSearch('?view=CaseReport&deleted=1');
    await waitFor(() => {
      expect(showDeletedCheckbox().checked).toBe(true);
    });
    expect(within(navigator).getByRole('status').textContent).toBe('New record');

    fireEvent.click(showDeletedCheckbox());
    await expectUrlSearch('?view=CaseReport');
    await waitFor(() => {
      expect(showDeletedCheckbox().checked).toBe(false);
    });
    expectReadsOnly();
  });

  it.each([
    { record: 'abc', notice: "'abc' is not a record number, so the first record is shown." },
    { record: '0', notice: "'0' is not a record number, so the first record is shown." },
    { record: '-2', notice: "'-2' is not a record number, so the first record is shown." },
    { record: '2.5', notice: "'2.5' is not a record number, so the first record is shown." },
    { record: '2abc', notice: "'2abc' is not a record number, so the first record is shown." },
    { record: '1e3', notice: "'1e3' is not a record number, so the first record is shown." },
    { record: '', notice: 'The record number is blank, so the first record is shown.' },
    // The URL's value is shown as text, never as markup.
    { record: '<b>2</b>', notice: "'<b>2</b>' is not a record number, so the first record is shown." },
  ])(
    'opens the first record for record=$record with a notice saying so, and rewrites the URL to record=1',
    async ({ record, notice }) => {
      window.history.replaceState(null, '', `/?view=CaseReport&record=${encodeURIComponent(record)}`);
      renderApp();

      await expectOpenRecord(1);
      await expectUrlSearch('?view=CaseReport&record=1');

      const opened = requestedPaths().filter((path) => path.startsWith(`GET ${FORM_PATH}/records/at/`));
      expect(opened).toEqual([`GET ${FORM_PATH}/records/at/1`]);
      const banner = await findNavigationNotice(notice);
      expect(banner.querySelector('b')).toBeNull();

      // The navigation notice's own Dismiss takes it away.
      fireEvent.click(within(banner).getByRole('button', { name: 'Dismiss' }));
      expect(screen.queryByText(notice)).toBeNull();
      expectReadsOnly();
    },
  );

  it.each([
    { record: '7', at: 7 },
    // Capped at the API's 32-bit limit, while the notice names the value as written.
    { record: '99999999999', at: 2_147_483_647 },
  ])(
    'opens the last record for record=$record, beyond the count, with a notice naming both',
    async ({ record, at }) => {
      window.history.replaceState(null, '', `/?view=CaseReport&record=${record}`);
      renderApp();

      await expectOpenRecord(3);
      await expectUrlSearch('?view=CaseReport&record=3');

      expect(requestedPaths()).toContain(`GET ${FORM_PATH}/records/at/${String(at)}`);
      await findNavigationNotice(
        `This record no longer exists. There is no record ${record}; showing the last record, 3.`,
      );
      expectReadsOnly();
    },
  );

  it('stays on the forms list with a notice when no form has the view name', async () => {
    window.history.replaceState(null, '', '/?view=Nope');
    renderApp();

    const alert = await screen.findByRole('alert');
    expect(within(alert).getByText("Form 'Nope' was not found.")).toBeDefined();
    expect(await screen.findByRole('button', { name: 'CaseReport — 2 pages' })).toBeDefined();
    expect(screen.getByRole('button', { name: 'FoodHistory — 2 pages' })).toBeDefined();
    await expectUrlSearch('');

    // No form was opened: nothing but the forms list was requested.
    const formReads = requests.filter((request) => /^\/api\/forms\/[^/]+/.test(request.path));
    expect(formReads.map((request) => request.line)).toEqual([]);
    expect(screen.queryByRole('toolbar', { name: 'Record navigation' })).toBeNull();
    expectReadsOnly();
  });

  it.each([
    { name: 'an unknown view name', url: '/?view=Nope', missing: false, text: "Form 'Nope' was not found." },
    // The forms list names the form, and its definition then answers 404.
    { name: 'a form gone since the lookup', url: '/?view=CaseReport', missing: true, text: 'This form no longer exists.' },
  ])('dismisses the forms-list notice after $name, and the title takes focus', async ({ url, missing, text }) => {
    window.history.replaceState(null, '', url);
    formMissing = missing;
    renderApp();

    // The list is back once the shell has settled the request, its notice above it.
    expect(await screen.findByRole('button', { name: 'CaseReport — 2 pages' })).toBeDefined();
    const alert = screen.getByRole('alert');
    expect(within(alert).getByText(text)).toBeDefined();
    await expectUrlSearch('');

    fireEvent.click(within(alert).getByRole('button', { name: 'Dismiss' }));

    expect(screen.queryByRole('alert')).toBeNull();
    expect(screen.queryByText(text)).toBeNull();
    // The button pressed is gone, so focus is on the title rather than the page body.
    expect(document.activeElement).toBe(screen.getByRole('heading', { level: 1 }));
    // Only the notice went: the forms list stays.
    expect(screen.getByRole('button', { name: 'CaseReport — 2 pages' })).toBeDefined();
    await expectUrlSearch('');
    expectReadsOnly();
  });

  it.each(['/?record=3', '/?view=&record=2', '/?view=%20&record=2', '/?deleted=1&title=X&project=Y'])(
    'shows the forms list at the bare path / for %s, which names no form',
    async (url) => {
      window.history.replaceState(null, '', url);
      const historyLength = window.history.length;
      renderApp();

      expect(await screen.findByRole('button', { name: 'CaseReport — 2 pages' })).toBeDefined();
      await expectUrlSearch('');
      // Rewritten in place: the stale address leaves no history entry behind.
      expect(window.history.length).toBe(historyLength);
      expect(screen.queryByRole('alert')).toBeNull();
      expect(requestedPaths()).toEqual(['GET /api/forms']);
      expectReadsOnly();
    },
  );

  // A failed lookup leaves the form unknown, so the URL's request is kept until it is settled.
  const lookupFailures: readonly {
    name: string;
    failure: () => FormsFailure;
    text: string;
    traceId: string | null;
  }[] = [
    { name: 'a 503', failure: unavailable, text: 'The database cannot be reached.', traceId: '00-test-unavailable-00' },
    { name: 'a 500', failure: serverError, text: 'An unexpected error occurred.', traceId: '00-test-server-error-00' },
    { name: 'no response', failure: () => 'noResponse', text: 'The server could not be reached.', traceId: null },
    // An OK body the lookup cannot read is the unexpected failure, not "not found".
    {
      name: 'a 200 whose body is not a list',
      failure: () => ok({}),
      text: 'An unexpected error occurred.',
      traceId: null,
    },
    {
      name: 'a 200 whose entry has no name',
      failure: () => ok([{ viewId: FORM.viewId, pageCount: 2 }]),
      text: 'An unexpected error occurred.',
      traceId: null,
    },
  ];

  it.each(lookupFailures)(
    'keeps the request and its URL after $name to the form lookup, and Retry opens record 2',
    async ({ failure, text, traceId }) => {
      formsFailure = failure();
      renderApp();

      const alert = await screen.findByRole('alert');
      expect(screen.getByText("Form 'CaseReport' could not be opened.")).toBeDefined();
      expect(alert.textContent).toContain(text);
      if (traceId !== null) {
        expect(alert.textContent).toContain(traceId);
      }
      const retry = within(alert).getByRole('button', { name: 'Retry' });
      expect(screen.getByRole('button', { name: 'Show forms list' })).toBeDefined();
      // Neither the URL nor the screen gave the request up: only the lookup was sent.
      await expectUrlSearch('?view=CaseReport&record=2');
      expect(requestedPaths()).toEqual(['GET /api/forms']);
      expect(screen.queryByRole('heading', { level: 2, name: 'Forms' })).toBeNull();

      formsFailure = null;
      fireEvent.click(retry);

      // Retry's own button is gone while the lookup repeats, so the title holds focus.
      expect(document.activeElement).toBe(screen.getByRole('heading', { level: 1 }));
      // The opening line is the one status with text; the shell's saved confirmation stays empty.
      expect(
        screen
          .getAllByRole('status')
          .map((status) => status.textContent)
          .filter((text) => text !== ''),
      ).toEqual(['Opening form CaseReport…']);
      await expectOpenRecord(2);
      await expectUrlSearch('?view=CaseReport&record=2');
      expect(screen.queryByRole('alert')).toBeNull();
      expect(requestedPaths().filter((path) => path === 'GET /api/forms')).toHaveLength(2);
      expect(requestedPaths()).toContain(`GET ${FORM_PATH}/records/at/2`);
      expectReadsOnly();
    },
  );

  it.each(lookupFailures)(
    'gives the request up when Show forms list is pressed after $name to the form lookup',
    async ({ failure }) => {
      formsFailure = failure();
      renderApp();
      await screen.findByRole('alert');

      formsFailure = null;
      fireEvent.click(screen.getByRole('button', { name: 'Show forms list' }));

      expect(document.activeElement).toBe(screen.getByRole('heading', { level: 1 }));
      expect(await screen.findByRole('button', { name: 'CaseReport — 2 pages' })).toBeDefined();
      await expectUrlSearch('');
      expect(screen.queryByRole('alert')).toBeNull();
      expect(screen.queryByText("Form 'CaseReport' could not be opened.")).toBeNull();
      // The lookup, then the list's own load; no form was opened.
      expect(requestedPaths()).toEqual(['GET /api/forms', 'GET /api/forms']);
      expectReadsOnly();
    },
  );
});

// Back to forms leaves the form like any move, saving a dirty draft first. The form, and the
// toolbar's confirmation with it, then unmounts, so the forms list confirms the save in a status
// of the shell's own, which stands outside the list's region and empties on the next screen.

describe('App return to the forms list', () => {
  const SAVED_TEXT = 'Changes to the CaseReport record were saved.';

  /** fakeFetch, also answering a PUT of a stored record with that record holding the values sent. */
  const savingFetch: typeof fetch = async (input, init) => {
    const request = input instanceof Request ? input : new Request(input, init);
    const url = new URL(request.url);
    const record = RECORDS.find((candidate) => url.pathname === `${FORM_PATH}/records/${candidate.globalRecordId}`);
    if (request.method !== 'PUT' || record === undefined) {
      return fakeFetch(request);
    }
    const body: unknown = await request.json();
    const values = typeof body === 'object' && body !== null && 'values' in body ? body.values : null;
    if (typeof values !== 'object' || values === null) {
      throw new Error('the PUT sent no values object');
    }
    requests.push({
      method: 'PUT',
      origin: url.origin,
      path: url.pathname,
      query: url.searchParams,
      line: `PUT ${url.pathname}${url.search}`,
    });
    return jsonResponse(ok({ ...record, values: { ...record.values, ...values } }));
  };

  /**
   * The shell's own status, the saved confirmation: the one status directly inside the main
   * landmark once the URL lookup has settled. It has no name by design, so it is told apart
   * from each screen's statuses, which sit inside that screen, by where it stands.
   */
  function savedStatus(): HTMLElement {
    const main = screen.getByRole('main');
    const statuses = within(main)
      .getAllByRole('status')
      .filter((status) => status.parentElement === main);
    expect(statuses, "the shell's own statuses").toHaveLength(1);
    return statuses[0];
  }

  it.each<{ label: string; edited: boolean; shown: string }>([
    { label: 'a draft Back to forms saved', edited: true, shown: SAVED_TEXT },
    { label: 'nothing for a clean record', edited: false, shown: '' },
  ])('confirms on the forms list $label, until a form opens again', async ({ edited, shown }) => {
    render(<App client={createApiClient({ baseUrl: API_ORIGIN, fetch: savingFetch })} />);
    await expectOpenRecord(2);
    const region = savedStatus();
    expect(region.textContent).toBe('');
    if (edited) {
      fireEvent.change(lastNameInput(), { target: { value: 'Edited' } });
    }

    fireEvent.click(
      within(screen.getByRole('toolbar', { name: 'Form actions' })).getByRole('button', { name: 'Back to forms' }),
    );

    const listed = await screen.findByRole('button', { name: 'CaseReport — 2 pages' });
    await expectUrlSearch('');
    expect(savedStatus()).toBe(region);
    expect(region.textContent).toBe(shown);
    expect(region.className).toBe(edited ? 'banner banner--info' : 'visually-hidden');
    // Informational: no alert, and the forms list keeps its one status beside the shell's confirmation.
    expect(screen.queryByRole('alert')).toBeNull();
    expect(within(screen.getByRole('region', { name: 'Forms' })).getAllByRole('status')).toHaveLength(1);
    expect(screen.getAllByRole('status')).toHaveLength(2);
    const puts = requests.filter((request) => request.method === 'PUT').map((request) => request.line);
    expect(puts).toEqual(edited ? [`PUT ${FORM_PATH}/records/${RECORDS[1].globalRecordId}`] : []);

    fireEvent.click(listed);

    await expectOpenRecord(1);
    expect(region.textContent).toBe('');
    expect(region.className).toBe('visually-hidden');
  });
});

// Forms list status. Self-contained: the shell opens at "/" over its own fetch, which answers
// only `GET /api/forms`, so the list's one status region is read while the list loads and
// after each outcome. Updating that region in place, never replacing it, is what makes the
// loaded count and the empty result announced.

describe('Forms list status', () => {
  /** One answer to `GET /api/forms`. */
  interface ListAnswer {
    readonly status: 200 | 500;
    readonly body: unknown;
  }

  const LIST_FORMS: readonly FormSummary[] = [
    { viewId: 1, name: 'CaseReport', pageCount: 2 },
    { viewId: 2, name: 'FoodHistory', pageCount: 2 },
  ];

  /** The forms' button names, in list order. */
  const LIST_BUTTONS = ['CaseReport — 2 pages', 'FoodHistory — 2 pages'] as const;

  const SERVER_ERROR: ProblemDetails = {
    type: 'https://tools.ietf.org/html/rfc9110#section-15.6.1',
    title: 'Internal Server Error',
    status: 500,
    detail: 'An unexpected error occurred.',
    traceId: '00-test-server-error-00',
  };

  /** Requests the list's fetch does not serve, or beyond the answers it was given: none is expected. */
  const strays: string[] = [];

  afterEach(() => {
    expect(strays, 'requests the forms-list fetch does not serve').toEqual([]);
  });

  /** Renders the shell at "/" over a fetch that answers successive `GET /api/forms` requests with `answers`. */
  function renderList(...answers: ListAnswer[]): void {
    strays.length = 0;
    window.history.replaceState(null, '', '/');
    const queue = [...answers];
    const listFetch: typeof fetch = (input, init) => {
      const request = input instanceof Request ? input : new Request(input, init);
      const url = new URL(request.url);
      const answer = request.method === 'GET' && url.pathname === '/api/forms' ? queue.shift() : undefined;
      if (answer === undefined) {
        strays.push(`${request.method} ${url.pathname}${url.search}`);
        return Promise.reject(new TypeError('The forms-list fetch has no answer for this request.'));
      }
      return Promise.resolve(
        new Response(JSON.stringify(answer.body), {
          status: answer.status,
          headers: { 'Content-Type': 'application/json' },
        }),
      );
    };
    render(<App client={createApiClient({ baseUrl: 'http://localhost', fetch: listFetch })} />);
  }

  /** The list's only status, inside its "Forms" region; the shell's own status stands outside it. */
  function listStatus(): HTMLElement {
    const statuses = within(screen.getByRole('region', { name: 'Forms' })).getAllByRole('status');
    expect(statuses, "the list's statuses").toHaveLength(1);
    return statuses[0];
  }

  it.each([
    { outcome: 'two forms', count: 2, text: '2 forms', hidden: true },
    { outcome: 'one form', count: 1, text: '1 form', hidden: true },
    { outcome: 'no forms', count: 0, text: 'No forms found.', hidden: false },
  ])(
    "updates the one status region from 'Loading forms…' to $text for $outcome",
    async ({ count, text, hidden }) => {
      renderList({ status: 200, body: LIST_FORMS.slice(0, count) });

      const status = listStatus();
      expect(status.textContent).toBe('Loading forms…');
      expect(status.classList.contains('visually-hidden')).toBe(false);

      await waitFor(() => {
        expect(status.textContent).toBe(text);
      });
      // The same element, and still the list's only status.
      expect(listStatus()).toBe(status);
      // The count is for assistive technology only, since the buttons show the forms; an empty result stays visible.
      expect(status.classList.contains('visually-hidden')).toBe(hidden);
      expect(screen.queryAllByRole('button').map((button) => button.textContent)).toEqual(
        LIST_BUTTONS.slice(0, count),
      );
    },
  );

  it('keeps the status region, empty, beside the failure alert, and through Retry', async () => {
    renderList({ status: 500, body: SERVER_ERROR }, { status: 200, body: LIST_FORMS });

    const status = listStatus();
    expect(status.textContent).toBe('Loading forms…');

    // The alert alone reports the failure.
    const alert = await screen.findByRole('alert');
    expect(listStatus()).toBe(status);
    expect(status.textContent).toBe('');
    expect(status.classList.contains('visually-hidden')).toBe(true);

    fireEvent.click(within(alert).getByRole('button', { name: 'Retry' }));

    expect(listStatus()).toBe(status);
    expect(status.textContent).toBe('Loading forms…');
    expect(status.classList.contains('visually-hidden')).toBe(false);
    // Retry's own button is gone while the list reloads, so the list heading holds focus.
    expect(document.activeElement).toBe(screen.getByRole('heading', { level: 2, name: 'Forms' }));

    await waitFor(() => {
      expect(status.textContent).toBe('2 forms');
    });
    expect(listStatus()).toBe(status);
    expect(screen.queryByRole('alert')).toBeNull();
    expect(screen.getAllByRole('button').map((button) => button.textContent)).toEqual(LIST_BUTTONS);
  });
});

// Forms list connection failure. Self-contained: the shell opens at "/" over its own fetch,
// which answers only `GET /api/forms`, first with the failure and then with the list. No
// draft exists on the forms list, so the banner says only what cannot be reached.

describe('Forms list connection failure', () => {
  const ORIGIN = 'http://localhost';

  const LISTED: readonly FormSummary[] = [{ viewId: 1, name: 'CaseReport', pageCount: 2 }];

  const UNAVAILABLE: ProblemDetails = {
    type: 'https://tools.ietf.org/html/rfc9110#section-15.6.4',
    title: 'Service Unavailable',
    status: 503,
    detail: 'The database is unavailable.',
    traceId: '00-test-forms-unavailable-00',
  };

  /** The failure the first `GET /api/forms` gets: a 503, or no response at all. */
  type ListFailure = 'unavailable' | 'noResponse';

  it.each<{ name: string; failure: ListFailure; text: string }>([
    { name: 'a 503', failure: 'unavailable', text: 'The database cannot be reached.' },
    { name: 'no response', failure: 'noResponse', text: 'The server could not be reached.' },
  ])('says only what cannot be reached, with Retry, after $name', async ({ failure, text }) => {
    window.history.replaceState(null, '', '/');
    const lines: string[] = [];
    const strayLines: string[] = [];
    const listFetch: typeof fetch = (input, init) => {
      const request = input instanceof Request ? input : new Request(input, init);
      const url = new URL(request.url);
      const line = `${request.method} ${url.pathname}${url.search}`;
      lines.push(line);
      const headers = { 'Content-Type': 'application/json' };
      if (line !== 'GET /api/forms') {
        strayLines.push(line);
        return Promise.resolve(new Response(JSON.stringify(UNAVAILABLE), { status: 503, headers }));
      }
      if (lines.filter((sent) => sent === line).length > 1) {
        return Promise.resolve(new Response(JSON.stringify(LISTED), { status: 200, headers }));
      }
      if (failure === 'noResponse') {
        // What fetch does when no response arrives.
        return Promise.reject(new TypeError('Failed to fetch'));
      }
      return Promise.resolve(new Response(JSON.stringify(UNAVAILABLE), { status: 503, headers }));
    };
    render(<App client={createApiClient({ baseUrl: ORIGIN, fetch: listFetch })} />);

    const alert = await screen.findByRole('alert');
    expect(within(alert).getByText(text)).toBeDefined();
    expect(alert.textContent).not.toContain('still on screen');

    fireEvent.click(within(alert).getByRole('button', { name: 'Retry' }));

    expect(await screen.findByRole('button', { name: 'CaseReport — 2 pages' })).toBeDefined();
    expect(screen.queryByRole('alert')).toBeNull();
    expect(lines).toEqual(['GET /api/forms', 'GET /api/forms']);
    expect(strayLines).toEqual([]);
  });
});
