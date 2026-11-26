// P2 command-line switch mapping, client half. Legacy Enter read `/view:<name>` and
// `/record:<id>` from its command line (Epi.Windows.Enter/EntryPoint.cs); the web shell
// reads `?view=<form name>&record=<1-based position>` once on load, opens that form at
// that record, and keeps the query string in step while the user moves between records.
// `title` and `project` are ignored: the page title is fixed and the API's configured
// connection string is the project.
//
// The shell runs over the real client, FormView, hooks and field components. The client's
// fetch seam is an in-memory API serving one form, `CaseReport` (view 1), with three
// active records; every request is logged, and one it does not serve is answered 404 and
// fails the test, so opening a form from the URL is also shown to send GETs only and to
// raise no field event (DATA-01).
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

/** `LastName` of the record at each position, so the open record is told apart on screen. */
const LAST_NAMES = ['First', 'Second', 'Third'] as const;

/** The form's records in `UniqueKey` order; each Record DTO carries its own active-set position. */
const RECORDS: readonly RecordDto[] = LAST_NAMES.map((lastName, index) =>
  recordFixture({
    globalRecordId: `3f2b8c1e-5d4a-4c1b-9e7f-00000000000${String(index + 1)}`,
    uniqueKey: index + 1,
    position: index + 1,
    count: LAST_NAMES.length,
    values: { LastName: lastName },
  }),
);

function summaryOf(record: RecordDto): RecordSummary {
  return {
    globalRecordId: record.globalRecordId,
    uniqueKey: record.uniqueKey,
    recStatus: record.recStatus,
    values: { LastName: record.values.LastName ?? null },
  };
}

const RECORD_PAGE: RecordPage = { items: RECORDS.map(summaryOf), total: RECORDS.length, page: 1, pageSize: 50 };

const AT_PATTERN = /^\/records\/at\/(\d+)$/;
const POSITION_PATTERN = /^\/records\/([^/]+)\/position$/;

/** One request the API received. */
interface ApiRequest {
  readonly method: string;
  readonly origin: string;
  readonly path: string;
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

/** The answer of a read route, or `null` when no route serves the request. */
function route(method: string, path: string): Answer | null {
  if (method !== 'GET') {
    return null;
  }
  if (path === '/api/forms') {
    // `'noResponse'` never reaches a route: the fetch rejects first.
    return formsFailure === null || formsFailure === 'noResponse' ? ok(FORMS) : formsFailure;
  }
  if (path === FORM_PATH) {
    return ok(FORM);
  }
  if (!path.startsWith(`${FORM_PATH}/`)) {
    return null;
  }

  const rest = path.slice(FORM_PATH.length);
  if (rest === '/records') {
    return ok(RECORD_PAGE);
  }
  if (rest === '/records/count') {
    const count: RecordCountResponse = { count: RECORDS.length };
    return ok(count);
  }

  const at = AT_PATTERN.exec(rest);
  if (at !== null) {
    // A position outside 1..count is the API's documented 404, not an unexpected request.
    const record = RECORDS[Number(at[1]) - 1];
    return record === undefined ? notFound('The record does not exist.') : ok(record);
  }

  const position = POSITION_PATTERN.exec(rest);
  if (position !== null) {
    const index = RECORDS.findIndex((record) => record.globalRecordId === position[1]);
    if (index < 0) {
      return notFound('The record does not exist.');
    }
    const answer: NavigationPosition = { position: index + 1, count: RECORDS.length };
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
    line: `${request.method} ${url.pathname}${url.search}`,
  };
  requests.push(entry);

  if (formsFailure === 'noResponse' && entry.method === 'GET' && entry.path === '/api/forms') {
    // A lost response: fetch rejects, as it does when the server cannot be reached.
    return Promise.reject(new TypeError('Failed to fetch'));
  }
  const answer = route(entry.method, entry.path);
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
  window.history.replaceState(null, '', '/?view=CaseReport&record=2');
});

afterEach(() => {
  expect(unexpected, 'requests no route serves').toEqual([]);
});

// Rendering and DOM helpers

function renderApp(): void {
  const client: ApiClient = createApiClient({ baseUrl: API_ORIGIN, fetch: fakeFetch });
  render(<App client={client} />);
}

function lastNameInput(): HTMLInputElement {
  const element = screen.getByLabelText('Last Name', { selector: 'input' });
  if (!(element instanceof HTMLInputElement)) {
    throw new Error('the Last Name control is not an input');
  }
  return element;
}

/** Waits until the navigator reads "<position> of 3" and the form shows that record. */
async function expectOpenRecord(position: number): Promise<void> {
  const navigator = await screen.findByRole('toolbar', { name: 'Record navigation' });
  await waitFor(() => {
    expect(within(navigator).getByRole('status').textContent).toBe(`${String(position)} of ${String(RECORDS.length)}`);
  });
  await waitFor(() => {
    expect(lastNameInput().value).toBe(LAST_NAMES[position - 1]);
  });
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

  it.each(['abc', '0', '-2', '2.5', ''])(
    'opens the first record for record=%j and rewrites the URL to record=1',
    async (record) => {
      window.history.replaceState(null, '', `/?view=CaseReport&record=${encodeURIComponent(record)}`);
      renderApp();

      await expectOpenRecord(1);
      await expectUrlSearch('?view=CaseReport&record=1');

      const opened = requestedPaths().filter((path) => path.startsWith(`GET ${FORM_PATH}/records/at/`));
      expect(opened).toEqual([`GET ${FORM_PATH}/records/at/1`]);
      expectReadsOnly();
    },
  );

  it('opens the nearest record when record is beyond the count', async () => {
    window.history.replaceState(null, '', '/?view=CaseReport&record=7');
    renderApp();

    await expectOpenRecord(3);
    await expectUrlSearch('?view=CaseReport&record=3');

    expect(requestedPaths()).toContain(`GET ${FORM_PATH}/records/at/7`);
    expect(screen.getByText('This record no longer exists.')).toBeDefined();
    expectReadsOnly();
  });

  it('stays on the forms list with a notice when no form has the view name', async () => {
    window.history.replaceState(null, '', '/?view=Nope');
    renderApp();

    const alert = await screen.findByRole('alert');
    expect(alert.textContent).toBe("Form 'Nope' was not found.");
    expect(await screen.findByRole('button', { name: 'CaseReport — 2 pages' })).toBeDefined();
    expect(screen.getByRole('button', { name: 'FoodHistory — 2 pages' })).toBeDefined();
    await expectUrlSearch('');

    // No form was opened: nothing but the forms list was requested.
    const formReads = requests.filter((request) => /^\/api\/forms\/[^/]+/.test(request.path));
    expect(formReads.map((request) => request.line)).toEqual([]);
    expect(screen.queryByRole('toolbar', { name: 'Record navigation' })).toBeNull();
    expectReadsOnly();
  });

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
      expect(screen.getByRole('status').textContent).toBe('Opening form CaseReport…');
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

  it.each([
    { outcome: 'two forms', count: 2, text: '2 forms', hidden: true },
    { outcome: 'one form', count: 1, text: '1 form', hidden: true },
    { outcome: 'no forms', count: 0, text: 'No forms found.', hidden: false },
  ])(
    "updates the one status region from 'Loading forms…' to $text for $outcome",
    async ({ count, text, hidden }) => {
      renderList({ status: 200, body: LIST_FORMS.slice(0, count) });

      const status = screen.getByRole('status');
      expect(status.textContent).toBe('Loading forms…');
      expect(status.classList.contains('visually-hidden')).toBe(false);

      await waitFor(() => {
        expect(status.textContent).toBe(text);
      });
      // The same element, and still the only status on the screen.
      expect(screen.getByRole('status')).toBe(status);
      // The count is for assistive technology only, since the buttons show the forms; an empty result stays visible.
      expect(status.classList.contains('visually-hidden')).toBe(hidden);
      expect(screen.queryAllByRole('button').map((button) => button.textContent)).toEqual(
        LIST_BUTTONS.slice(0, count),
      );
    },
  );

  it('keeps the status region, empty, beside the failure alert, and through Retry', async () => {
    renderList({ status: 500, body: SERVER_ERROR }, { status: 200, body: LIST_FORMS });

    const status = screen.getByRole('status');
    expect(status.textContent).toBe('Loading forms…');

    // The alert alone reports the failure.
    const alert = await screen.findByRole('alert');
    expect(screen.getByRole('status')).toBe(status);
    expect(status.textContent).toBe('');
    expect(status.classList.contains('visually-hidden')).toBe(true);

    fireEvent.click(within(alert).getByRole('button', { name: 'Retry' }));

    expect(screen.getByRole('status')).toBe(status);
    expect(status.textContent).toBe('Loading forms…');
    expect(status.classList.contains('visually-hidden')).toBe(false);
    // Retry's own button is gone while the list reloads, so the list heading holds focus.
    expect(document.activeElement).toBe(screen.getByRole('heading', { level: 2, name: 'Forms' }));

    await waitFor(() => {
      expect(status.textContent).toBe('2 forms');
    });
    expect(screen.getByRole('status')).toBe(status);
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
