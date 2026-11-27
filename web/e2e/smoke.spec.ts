/**
 * The browser smoke test of the web data-entry client, and the only end-to-end test (SC-1, SC-2 and SC-10).
 *
 * It drives the built client that the API serves, the API Playwright's webServer starts or the one E2E_BASE_URL
 * names, and checks what each action stored through read-only SQL (`support/sql.ts`). Through the UI it creates,
 * edits and deletes only its own CaseReport record, marked in LastName; seeded records are opened and stepped
 * through without touching a field, so no save can reach them, and its last step leaves a FoodHistory draft unsaved.
 * The exclusive test lock is held for the whole workflow, so no .NET integration test writes while it counts and places
 * records, and the positions it expects come from SQL counts at run time. Before it waits for that lock, it checks,
 * without a database, that `support/connection.ts` reads connection settings as SqlClient does.
 *
 * Locators use roles, accessible names and labels as `web/src` renders them. A name is matched exactly wherever it
 * is a prefix of another ("Save" and "Save again", "Delete" and "Delete record", "New" and "New record").
 */
import { randomUUID } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

import {
  expect,
  test,
  type APIRequestContext,
  type Locator,
  type Page,
  type Response as PlaywrightResponse,
} from '@playwright/test';
import type { config as MssqlConfig } from 'mssql';

import {
  CONNECTION_STRING_VARIABLE,
  PASSWORD_VARIABLE,
  SQL_HOST_VARIABLE,
  TestDatabaseConfigurationError,
  hasDatabaseSettings,
  resolveConnectionString,
  toMssqlConfig,
} from './support/connection';
import {
  acquireExclusiveTestLock,
  openSqlVerifier,
  type ExclusiveTestLock,
  type PageTable,
  type SeededForm,
  type SqlVerifier,
} from './support/sql';

/** The tab names of each seeded form's pages, in page order; their number is the form's `pageCount`. */
const SEEDED_PAGES: Readonly<Record<SeededForm, readonly string[]>> = {
  CaseReport: ['Page 1'],
  FoodHistory: ['Page 1', 'Page 2'],
};

const SEEDED_FORMS: readonly SeededForm[] = ['CaseReport', 'FoodHistory'];

// CaseReport's only page, which holds LastName and City.
const CASE_REPORT_PAGE: PageTable = 'CaseReport1';

// Prompts unique on CaseReport. Address, State, County and Phone repeat there, so no locator uses them.
const LAST_NAME_LABEL = "Patient's Name";
const CITY_LABEL = 'City';
const CITY_TEXT = 'Smoke Test City';

// FoodHistory prompts of the fields scenario EC-AGE-01 enters and checks.
const DOB_LABEL = 'DOB';
const INTERVIEW_LABEL = 'Date of Interview';
const AGE_LABEL = 'Age';
const AGE_SCENARIO_ID = 'EC-AGE-01';
const SCENARIO_FILE = fileURLToPath(new URL('../../testdata/scenarios/check_code_scenarios.json', import.meta.url));

const RECORDS_PATH = /^\/api\/forms\/\d+\/records$/;
const RECORD_PATH = /^\/api\/forms\/\d+\/records\/([^/]+)$/;
const RECORD_AT_PATH = /^\/api\/forms\/\d+\/records\/at\/\d+$/;
const SEARCH_PATH = /^\/api\/forms\/\d+\/records\/search$/;
const FIELD_EVENT_PATH_SUFFIX = '/checkcode/field-event';

type ActionName = 'Back to forms' | 'Save' | 'Delete' | 'Find';
type NavigatorButtonName = 'First' | 'Previous' | 'Next' | 'Last' | 'New';

/** A form as `GET /api/forms` lists it. */
interface FormSummary {
  readonly viewId: number;
  readonly name: string;
  readonly pageCount: number;
}

/** A request the page sent: its method and path, never its query string. */
interface SentRequest {
  readonly method: string;
  readonly path: string;
}

/** What the page sends, recorded from before the first navigation. */
interface Traffic {
  /** Every request sent until {@link Traffic.endLoadWindow} is called. */
  readonly loadWindow: readonly SentRequest[];
  /** Every record create, update and delete, as "METHOD path", for the whole test. */
  readonly recordWrites: readonly string[];
  /** Ends the clean-load window; later requests are no longer added to `loadWindow`. */
  endLoadWindow(): void;
}

/**
 * A setting the parity step appends to the configured connection string, and how SqlClient 5.2.3, which the API and
 * the .NET tests connect with, reads it: the verifier options it changes, where `undefined` unsets one and `{}`
 * changes none, or a refusal whose message must not repeat `withheld`, the setting's value or unknown keyword.
 */
interface ConnectionSettingCase {
  readonly setting: string;
  readonly reads: { readonly changes: Partial<NonNullable<MssqlConfig['options']>> } | { readonly withheld: string };
}

// A DNS alias the server certificate would have to name; it is never resolved.
const CERTIFICATE_ALIAS = 'sql-alias.example.test';

// SqlClient 5.2.3's readings, recorded from SqlConnectionStringBuilder and SqlConnection, never from toMssqlConfig.
// No value here is a credential, and the certificate path is never opened.
const CONNECTION_SETTING_CASES: readonly ConnectionSettingCase[] = [
  { setting: `HostNameInCertificate=${CERTIFICATE_ALIAS}`, reads: { changes: { serverName: CERTIFICATE_ALIAS } } },
  { setting: `Host Name In Certificate=${CERTIFICATE_ALIAS}`, reads: { changes: { serverName: CERTIFICATE_ALIAS } } },
  {
    setting: `HostNameInCertificate=${CERTIFICATE_ALIAS};HostNameInCertificate=`,
    reads: { changes: { serverName: undefined } },
  },
  { setting: 'MultiSubnetFailover=True', reads: { changes: { multiSubnetFailover: true } } },
  { setting: 'Integrated Security=False', reads: { changes: {} } },
  { setting: 'Authentication=SqlPassword', reads: { changes: {} } },
  { setting: 'ApplicationIntent=ReadWrite', reads: { changes: {} } },
  { setting: 'IP Address Preference=IPv4First', reads: { changes: {} } },
  { setting: 'Application Name=smoke', reads: { changes: {} } },
  { setting: 'Integrated Security=SSPI', reads: { withheld: 'SSPI' } },
  { setting: 'ApplicationIntent=ReadOnly', reads: { withheld: 'ReadOnly' } },
  { setting: 'Server Certificate=/certs/sql-alias.cer', reads: { withheld: '/certs/sql-alias.cer' } },
  { setting: 'IP Address Preference=IPv6First', reads: { withheld: 'IPv6First' } },
  { setting: 'Network Library=dbmssocn', reads: { withheld: 'dbmssocn' } },
  { setting: 'SmokeUnknownKeyword=1', reads: { withheld: 'SmokeUnknownKeyword' } },
];

/** The parts of scenario EC-AGE-01 the browser step enters and checks. */
interface AgeScenario {
  readonly given: { readonly DOB: string; readonly DateofInterview: string };
  readonly expected: { readonly Age: number };
}

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function isArray(value: unknown): value is readonly unknown[] {
  return Array.isArray(value);
}

function isCount(value: unknown): value is number {
  return typeof value === 'number' && Number.isInteger(value) && value >= 0;
}

/** The navigator's text for record `n` of `count`, as `useRecordNavigation` formats it. */
function statusText(n: number, count: number): string {
  return `${n} of ${count}`;
}

function pathOf(url: string): string {
  return new URL(url).pathname;
}

/** The record id a `/api/forms/{viewId}/records/{id}` path names, or `null` for any other path. */
function recordIdOf(path: string): string | null {
  const match = RECORD_PATH.exec(path);
  return match === null ? null : decodeURIComponent(match[1]);
}

/** A create (`POST .../records`), update (`PUT .../records/{id}`) or delete (`DELETE .../records/{id}`). */
function isRecordWrite(method: string, path: string): boolean {
  if (method === 'POST') {
    return RECORDS_PATH.test(path);
  }
  return (method === 'PUT' || method === 'DELETE') && RECORD_PATH.test(path);
}

function isRecordsPost(response: PlaywrightResponse): boolean {
  return response.request().method() === 'POST' && RECORDS_PATH.test(pathOf(response.url()));
}

function isSearchPost(response: PlaywrightResponse): boolean {
  return response.request().method() === 'POST' && SEARCH_PATH.test(pathOf(response.url()));
}

/** A `POST .../checkcode/field-event` whose body raises `event` for `fieldName`. */
function isFieldEventPost(response: PlaywrightResponse, event: string, fieldName: string): boolean {
  const sent = response.request();
  if (sent.method() !== 'POST' || !pathOf(response.url()).endsWith(FIELD_EVENT_PATH_SUFFIX)) {
    return false;
  }
  const body: unknown = JSON.parse(sent.postData() ?? 'null');
  return isObject(body) && body.event === event && body.fieldName === fieldName;
}

function isRecordRequest(response: PlaywrightResponse, method: string, id: string): boolean {
  return response.request().method() === method && recordIdOf(pathOf(response.url())) === id;
}

function isRecordGet(response: PlaywrightResponse, id: string): boolean {
  return isRecordRequest(response, 'GET', id);
}

function isRecordPut(response: PlaywrightResponse, id: string): boolean {
  return isRecordRequest(response, 'PUT', id);
}

function isRecordDelete(response: PlaywrightResponse, id: string): boolean {
  return isRecordRequest(response, 'DELETE', id);
}

function toFormSummary(value: unknown): FormSummary | null {
  if (!isObject(value)) {
    return null;
  }
  const { viewId, name, pageCount } = value;
  return isCount(viewId) && typeof name === 'string' && isCount(pageCount) ? { viewId, name, pageCount } : null;
}

/** `GET /api/forms`, validated. */
async function readForms(request: APIRequestContext): Promise<FormSummary[]> {
  const response = await request.get('/api/forms');
  expect(response.status(), 'GET /api/forms').toBe(200);
  const body: unknown = await response.json();
  if (!isArray(body)) {
    throw new Error('GET /api/forms did not return a JSON array.');
  }
  const forms: FormSummary[] = [];
  for (const item of body) {
    const form = toFormSummary(item);
    if (form === null) {
      throw new Error('GET /api/forms returned an entry that is not {viewId, name, pageCount}.');
    }
    forms.push(form);
  }
  return forms;
}

function formNamed(forms: readonly FormSummary[], name: SeededForm): FormSummary {
  const form = forms.find((candidate) => candidate.name === name);
  if (form === undefined) {
    throw new Error(`GET /api/forms does not list ${name}.`);
  }
  return form;
}

/** `GET /api/forms/{viewId}/records/count` over the active set, or every record with `includeDeleted`. */
async function apiCount(request: APIRequestContext, viewId: number, includeDeleted: boolean): Promise<number> {
  const path = `/api/forms/${viewId}/records/count${includeDeleted ? '?includeDeleted=true' : ''}`;
  const response = await request.get(path);
  expect(response.status(), `GET ${path}`).toBe(200);
  const body: unknown = await response.json();
  const count = isObject(body) ? body.count : undefined;
  if (!isCount(count)) {
    throw new Error(`GET ${path} did not return {count} as a non-negative integer.`);
  }
  return count;
}

/** EC-AGE-01 read by path from the scenario file, so the values the step enters are never copied here. */
function readAgeScenario(): AgeScenario {
  const parsed: unknown = JSON.parse(readFileSync(SCENARIO_FILE, 'utf8'));
  const scenarios = isObject(parsed) ? parsed.scenarios : undefined;
  if (!isArray(scenarios)) {
    throw new Error('testdata/scenarios/check_code_scenarios.json holds no scenarios array.');
  }
  const scenario = scenarios.find((candidate) => isObject(candidate) && candidate.id === AGE_SCENARIO_ID);
  if (!isObject(scenario) || scenario.form !== 'FoodHistory' || scenario.event !== 'DOB.After') {
    throw new Error(`${AGE_SCENARIO_ID} is missing, or is not a FoodHistory DOB.After scenario.`);
  }
  const given = isObject(scenario.given) ? scenario.given : {};
  const expected = isObject(scenario.expect) ? scenario.expect : {};
  const { DOB: dob, DateofInterview: interview } = given;
  const { Age: age } = expected;
  if (typeof dob !== 'string' || typeof interview !== 'string' || typeof age !== 'number') {
    throw new Error(`${AGE_SCENARIO_ID} does not give DOB and DateofInterview as strings and expect Age as a number.`);
  }
  return { given: { DOB: dob, DateofInterview: interview }, expected: { Age: age } };
}

/** Starts recording what `page` sends; call it before the first navigation. */
function recordTraffic(page: Page): Traffic {
  const loadWindow: SentRequest[] = [];
  const recordWrites: string[] = [];
  let inLoadWindow = true;
  page.on('request', (sent) => {
    const method = sent.method();
    const path = pathOf(sent.url());
    if (inLoadWindow) {
      loadWindow.push({ method, path });
    }
    if (isRecordWrite(method, path)) {
      recordWrites.push(`${method} ${path}`);
    }
  });
  return {
    loadWindow,
    recordWrites,
    endLoadWindow: () => {
      inLoadWindow = false;
    },
  };
}

/**
 * DATA-01 in the browser: ends the clean-load window and checks every request it recorded, from the first
 * navigation through the seeded records' loads and steps, the second CaseReport load and New. Each must be a GET
 * and none may raise a field event. Call it immediately before the first deliberate edit.
 */
function assertCleanLoad(traffic: Traffic): void {
  traffic.endLoadWindow();
  const offending = traffic.loadWindow
    .filter((sent) => sent.method !== 'GET' || sent.path.endsWith(FIELD_EVENT_PATH_SUFFIX))
    .map((sent) => `${sent.method} ${sent.path}`);
  expect(offending, 'requests other than a GET, or raising a field event, before the first edit').toEqual([]);
  const positionReads = traffic.loadWindow.filter((sent) => sent.method === 'GET' && RECORD_AT_PATH.test(sent.path));
  expect(positionReads.length, 'records read by position before the first edit').toBeGreaterThanOrEqual(4);
}

function formButton(page: Page, form: SeededForm): Locator {
  // The button text starts with the form name, then its page count: "FoodHistory — 2 pages".
  return page.getByRole('button', { name: new RegExp(`^${form}\\b`) });
}

function actionButton(page: Page, name: ActionName): Locator {
  return page.getByRole('toolbar', { name: 'Form actions', exact: true }).getByRole('button', { name, exact: true });
}

function navigatorButton(page: Page, name: NavigatorButtonName): Locator {
  return page
    .getByRole('toolbar', { name: 'Record navigation', exact: true })
    .getByRole('button', { name, exact: true });
}

/** The navigator's "n of N" or "New record" text. */
function navigatorStatus(page: Page): Locator {
  return page.getByRole('toolbar', { name: 'Record navigation', exact: true }).getByRole('status');
}

function recordsRegion(page: Page): Locator {
  return page.getByRole('region', { name: 'Records', exact: true });
}

/** The data rows of `table`: every row holding a data cell, so the header row is left out. */
function dataRows(page: Page, table: Locator): Locator {
  return table.getByRole('row').filter({ has: page.getByRole('cell') });
}

/** The text of the forms-list button for `form`, ending with its page count. */
function pageCountPattern(form: SeededForm): RegExp {
  const pages = SEEDED_PAGES[form].length;
  return new RegExp(`^${form}\\b.*\\b${pages} ${pages === 1 ? 'page' : 'pages'}$`);
}

/** Opens `form` from the forms list and waits until its first record, of `count` active records, is shown. */
async function openForm(page: Page, form: SeededForm, count: number): Promise<void> {
  await formButton(page, form).click();
  await expect(page.getByRole('heading', { level: 2, name: form, exact: true })).toBeVisible();
  const tabNames = SEEDED_PAGES[form];
  await expect(page.getByRole('tab')).toHaveCount(tabNames.length);
  for (const name of tabNames) {
    await expect(page.getByRole('tab', { name, exact: true })).toBeVisible();
  }
  await expect(navigatorStatus(page)).toHaveText(statusText(1, count));
}

/** Leaves the open form, whose record is clean, for the forms list. */
async function backToForms(page: Page): Promise<void> {
  await actionButton(page, 'Back to forms').click();
  for (const form of SEEDED_FORMS) {
    await expect(formButton(page, form)).toBeVisible();
  }
}

/**
 * With E2E_BASE_URL naming an API this run did not start, its counts must equal the SQL counts of the database the
 * settings name, before anything is written; otherwise every SQL check would read another database.
 */
async function checkExternalApiDatabase(request: APIRequestContext, db: SqlVerifier): Promise<void> {
  const forms = await readForms(request);
  for (const form of SEEDED_FORMS) {
    const { viewId } = formNamed(forms, form);
    for (const includeDeleted of [false, true]) {
      const fromApi = await apiCount(request, viewId, includeDeleted);
      const fromSql = await db.countRecords(form, includeDeleted);
      const set = includeDeleted ? 'all records' : 'active records';
      expect(
        fromApi,
        `${form}, ${set}: the API at E2E_BASE_URL counts ${fromApi} and SQL counts ${fromSql}, so that API is ` +
          `not using the rehearsal database that ${CONNECTION_STRING_VARIABLE}, or ${SQL_HOST_VARIABLE} and ` +
          `${PASSWORD_VARIABLE}, name.`,
      ).toBe(fromSql);
    }
  }
}

/** SC-1 over HTTP: the API is healthy and lists both seeded forms with their page counts. */
async function checkHealthAndForms(request: APIRequestContext): Promise<void> {
  const health = await request.get('/health');
  expect(health.status(), 'GET /health').toBe(200);
  const healthBody: unknown = await health.json();
  expect(healthBody).toEqual({ status: 'healthy' });

  const forms = await readForms(request);
  for (const form of SEEDED_FORMS) {
    expect(formNamed(forms, form).pageCount, `pageCount of ${form}`).toBe(SEEDED_PAGES[form].length);
  }
}

/** The paths at which two plain values differ, so that comparing configurations never prints one. */
function differingPaths(actual: unknown, expected: unknown, path: string): string[] {
  if (isObject(actual) && isObject(expected)) {
    const keys = new Set([...Object.keys(actual), ...Object.keys(expected)]);
    return [...keys].flatMap((key) => differingPaths(actual[key], expected[key], `${path}.${key}`));
  }
  return Object.is(actual, expected) ? [] : [path];
}

/** {@link toMssqlConfig} of `base` with `setting` appended, or the configuration error it throws. */
function configWith(base: string, setting: string): MssqlConfig | TestDatabaseConfigurationError {
  try {
    return toMssqlConfig(`${base};${setting}`);
  } catch (thrown: unknown) {
    if (thrown instanceof TestDatabaseConfigurationError) {
      return thrown;
    }
    throw thrown;
  }
}

/**
 * The SQL verifier reaches what the API reaches: each {@link CONNECTION_SETTING_CASES} setting, appended to the
 * configured connection string, changes the verifier's configuration as SqlClient's reading changes the API's
 * connection, or is refused. It needs no database. The string and every configuration hold the password, so neither
 * is printed: a failure names the setting, and the configuration entries that differ.
 */
function checkConnectionSettingParity(): void {
  const base = resolveConnectionString();
  const baseConfig = toMssqlConfig(base);
  for (const { setting, reads } of CONNECTION_SETTING_CASES) {
    const outcome = configWith(base, setting);
    if ('withheld' in reads) {
      const message = outcome instanceof TestDatabaseConfigurationError ? outcome.message : undefined;
      expect(message, `${setting} is refused`).toBeDefined();
      expect(message, `the refusal of ${setting} repeats no value or unknown keyword`).not.toContain(reads.withheld);
    } else {
      const expected = { ...baseConfig, options: { ...baseConfig.options, ...reads.changes } };
      const differing =
        outcome instanceof TestDatabaseConfigurationError
          ? [`refused: ${outcome.message}`]
          : differingPaths(outcome, expected, 'config');
      expect(differing, `${setting} changes the configuration as SqlClient's reading does`).toEqual([]);
    }
  }
}

/** The message of a thrown value, which need not be an `Error`. */
function messageOf(thrown: unknown): string {
  return thrown instanceof Error ? thrown.message : String(thrown);
}

/**
 * Closes the verifier, when one was opened, then releases the test lock. Each is attempted even when the other
 * fails, so a failed close still releases the lock. Resolves to one line per failure, naming what failed and the
 * message `support/sql.ts` gave, which is fixed text that holds no connection setting or value.
 */
async function closeAndRelease(db: SqlVerifier | undefined, lock: ExclusiveTestLock): Promise<string[]> {
  const failures: string[] = [];
  if (db !== undefined) {
    try {
      await db.close();
    } catch (thrown: unknown) {
      failures.push(`Closing the SQL verifier failed: ${messageOf(thrown)}`);
    }
  }
  try {
    await lock.release();
  } catch (thrown: unknown) {
    failures.push(`Releasing the test lock failed: ${messageOf(thrown)}`);
  }
  return failures;
}

/**
 * Reports cleanup failures beside the workflow's failure without ever throwing, so that failure stays the test's
 * error. Each failure is first added as a 'cleanup failure' annotation, which cannot fail; then all are attached as
 * text, and an attachment that fails, as one can while Playwright writes it, is noted as an annotation instead.
 */
async function reportCleanupFailures(failures: readonly string[]): Promise<void> {
  const { annotations } = test.info();
  for (const description of failures) {
    annotations.push({ type: 'cleanup failure', description });
  }
  try {
    await test.info().attach('cleanup failures', { body: failures.join('\n'), contentType: 'text/plain' });
  } catch (thrown: unknown) {
    annotations.push({
      type: 'cleanup report failure',
      description: `Attaching the cleanup failures failed: ${messageOf(thrown)}`,
    });
  }
}

test('data entry smoke: forms, create, edit, navigate, find, delete', async ({ page, request }) => {
  // The lock may wait up to 300 s for the .NET integration tests to finish; the workflow needs the rest.
  test.setTimeout(600_000);

  const externalApi = (process.env.E2E_BASE_URL ?? '').trim() !== '';
  if (externalApi && !hasDatabaseSettings()) {
    throw new Error(
      `E2E_BASE_URL names an external API, so the test must verify the database that API uses: set ` +
        `${CONNECTION_STRING_VARIABLE}, or ${SQL_HOST_VARIABLE} and ${PASSWORD_VARIABLE}, to that rehearsal ` +
        'database (see testdata/README.md).',
    );
  }

  // It needs no database, so it runs before the lock is waited for.
  await test.step('the SQL verifier reads connection settings as SqlClient does', checkConnectionSettingParity);

  const lock = await acquireExclusiveTestLock();
  let db: SqlVerifier | undefined;
  // Boxed, so that a thrown value of any kind, `undefined` included, still counts as the workflow's failure.
  let workflowFailure: { readonly thrown: unknown } | undefined;
  try {
    db = await openSqlVerifier();
    await runWorkflow(page, request, db, externalApi);
  } catch (thrown: unknown) {
    workflowFailure = { thrown };
  }
  const cleanupFailures = await closeAndRelease(db, lock);
  if (workflowFailure !== undefined) {
    // The workflow's failure stays the test's error; a cleanup failure after it is reported beside it.
    if (cleanupFailures.length > 0) {
      await reportCleanupFailures(cleanupFailures);
    }
    throw workflowFailure.thrown;
  }
  if (cleanupFailures.length > 0) {
    throw new Error(`The workflow passed, but its cleanup failed:\n${cleanupFailures.join('\n')}`);
  }
});

/** Every step after the lock is held, in order. `db` reads the database the API writes. */
async function runWorkflow(
  page: Page,
  request: APIRequestContext,
  db: SqlVerifier,
  externalApi: boolean,
): Promise<void> {
  if (externalApi) {
    await test.step('the external API uses the rehearsal database', () => checkExternalApiDatabase(request, db));
  }
  await test.step('health and forms over HTTP', () => checkHealthAndForms(request));

  const traffic = recordTraffic(page);
  const status = navigatorStatus(page);
  const lastName = page.getByLabel(LAST_NAME_LABEL, { exact: true });
  const city = page.getByLabel(CITY_LABEL, { exact: true });

  await test.step('the served client lists the forms', async () => {
    const [formsResponse, main] = await Promise.all([
      page.waitForResponse(
        (response) => pathOf(response.url()) === '/api/forms' && response.request().method() === 'GET',
      ),
      page.goto('/'),
    ]);
    expect(main, 'the response to GET /').not.toBeNull();
    expect(main?.status(), 'GET /').toBe(200);
    expect(main?.headers()['content-type'] ?? '', 'content type of GET /').toMatch(/^text\/html\b/i);
    expect(formsResponse.status(), 'GET /api/forms from the client').toBe(200);
    for (const form of SEEDED_FORMS) {
      await expect(formButton(page, form)).toHaveText(pageCountPattern(form));
    }
  });

  const nCase = await db.countRecords('CaseReport', false);
  const nFood = await db.countRecords('FoodHistory', false);
  const nFoodAll = await db.countRecords('FoodHistory', true);

  await test.step('seeded records open and step inside the clean-load window', async () => {
    expect(nCase, 'active CaseReport records').toBeGreaterThanOrEqual(2);
    expect(nFood, 'active FoodHistory records').toBeGreaterThanOrEqual(2);

    // No field is focused, clicked or typed into here: only form buttons, tabs and the navigator. The window stays
    // open through the create step's CaseReport load and New, and assertCleanLoad checks it just before LastName.
    await openForm(page, 'CaseReport', nCase);
    await expect(lastName).toBeVisible();
    await navigatorButton(page, 'Next').click();
    await expect(status).toHaveText(statusText(2, nCase));
    await backToForms(page);

    await openForm(page, 'FoodHistory', nFood);
    const secondPage = page.getByRole('tab', { name: SEEDED_PAGES.FoodHistory[1], exact: true });
    await secondPage.click();
    await expect(secondPage).toHaveAttribute('aria-selected', 'true');
    await navigatorButton(page, 'Next').click();
    await expect(status).toHaveText(statusText(2, nFood));
    await backToForms(page);
  });

  const marker = `T-${randomUUID().replaceAll('-', '')}`;

  const { id, nAfterCreate } = await test.step('create a CaseReport record', async () => {
    expect(marker).toHaveLength(34);
    expect(await db.baseRowsByMarker('CaseReport', marker), 'records already holding the marker').toEqual([]);

    await openForm(page, 'CaseReport', nCase);
    await navigatorButton(page, 'New').click();
    await expect(status).toHaveText('New record');
    await expect(lastName).toHaveValue('');
    // Every request so far loaded or stepped a record or opened the draft; LastName is the first deliberate edit.
    assertCleanLoad(traffic);
    await lastName.fill(marker);

    const [created] = await Promise.all([page.waitForResponse(isRecordsPost), actionButton(page, 'Save').click()]);
    expect(created.status(), 'POST of the new record').toBe(201);
    const body: unknown = await created.json();
    const createdId = isObject(body) ? body.globalRecordId : undefined;
    if (typeof createdId !== 'string' || createdId === '') {
      throw new Error('The 201 response to the create carries no globalRecordId.');
    }
    expect(body).toMatchObject({ recStatus: 1, values: { LastName: marker } });
    const locationSuffix = `/records/${createdId}`;
    const location = created.headers()['location'] ?? '';
    expect(location.slice(-locationSuffix.length), 'Location of the created record').toBe(locationSuffix);

    await expect
      .poll(async () =>
        (await db.baseRowsByMarker('CaseReport', marker)).map((row) => ({
          globalRecordId: row.globalRecordId,
          recStatus: row.recStatus,
        })),
      )
      .toEqual([{ globalRecordId: createdId, recStatus: 1 }]);
    await expect.poll(async () => (await db.pageRow(CASE_REPORT_PAGE, createdId))?.LastName).toBe(marker);
    await expect.poll(async () => (await db.baseRowById('CaseReport', createdId))?.fkey).toBeNull();

    const count = await db.countRecords('CaseReport', false);
    expect(count, 'active CaseReport records after the create').toBe(nCase + 1);
    // A created record follows every earlier one in UniqueKey order, so it is the last of the active set.
    await expect(status).toHaveText(statusText(count, count));
    return { id: createdId, nAfterCreate: count };
  });

  await test.step('search the grid, open the record, edit, save and step', async () => {
    // The created record is open after its save, so a different one is opened first: the grid row then has to
    // install the created record for the position and LastName to change back. The saved record is clean, so
    // leaving it writes nothing.
    const writesBeforeLeaving = traffic.recordWrites.length;
    await navigatorButton(page, 'Previous').click();
    await expect(status).toHaveText(statusText(nAfterCreate - 1, nAfterCreate));
    await expect(lastName).not.toHaveValue(marker);
    expect(traffic.recordWrites.slice(writesBeforeLeaving), 'record writes while stepping away').toEqual([]);

    const records = recordsRegion(page);
    const search = records.getByLabel('Search records', { exact: true });
    const rows = dataRows(page, records.getByRole('table'));
    await search.fill(marker);
    await search.press('Enter');
    await expect(rows).toHaveCount(1);
    await expect(rows.first()).toContainText(marker);

    const [opened] = await Promise.all([
      page.waitForResponse((response) => isRecordGet(response, id)),
      rows.first().click(),
    ]);
    expect(opened.status(), 'GET of the record opened from the grid').toBe(200);
    await expect(status).toHaveText(statusText(nAfterCreate, nAfterCreate));
    await expect(lastName).toHaveValue(marker);

    await city.fill(CITY_TEXT);
    const [updated] = await Promise.all([
      page.waitForResponse((response) => isRecordPut(response, id)),
      actionButton(page, 'Save').click(),
    ]);
    expect(updated.status(), 'PUT of the edited record').toBe(200);
    await expect
      .poll(async () => {
        const row = await db.pageRow(CASE_REPORT_PAGE, id);
        return row === null ? null : { City: row.City, LastName: row.LastName };
      })
      .toEqual({ City: CITY_TEXT, LastName: marker });
    await expect.poll(async () => (await db.baseRowById('CaseReport', id))?.recStatus).toBe(1);
    await expect(status).toHaveText(statusText(nAfterCreate, nAfterCreate));

    // The record is clean once saved, so stepping away and back writes nothing.
    const writesBefore = traffic.recordWrites.length;
    await navigatorButton(page, 'Previous').click();
    await expect(status).toHaveText(statusText(nAfterCreate - 1, nAfterCreate));
    await navigatorButton(page, 'Next').click();
    await expect(status).toHaveText(statusText(nAfterCreate, nAfterCreate));
    await expect(lastName).toHaveValue(marker);
    await expect(city).toHaveValue(CITY_TEXT);
    expect(traffic.recordWrites.slice(writesBefore), 'record writes while stepping').toEqual([]);
  });

  const pageBefore = await test.step('Find the record from another record', async () => {
    // Find leaves the open record out of its results, so a different record is opened first.
    await navigatorButton(page, 'First').click();
    await expect(status).toHaveText(statusText(1, nAfterCreate));

    await actionButton(page, 'Find').click();
    const find = page.getByRole('dialog', { name: 'Find record', exact: true });
    await expect(find).toBeVisible();
    await find.getByRole('checkbox', { name: 'LastName', exact: true }).check();
    // A criterion is labelled by the field's prompt, then its name in parentheses.
    await find.getByLabel(`${LAST_NAME_LABEL} (LastName)`, { exact: true }).fill(marker);
    const [searched] = await Promise.all([
      page.waitForResponse(isSearchPost),
      find.getByRole('button', { name: 'Search', exact: true }).click(),
    ]);
    expect(searched.status(), 'POST of the search').toBe(200);
    const body: unknown = await searched.json();
    expect(body).toMatchObject({ total: 1, truncated: false, items: [{ globalRecordId: id, position: nAfterCreate }] });

    const results = dataRows(page, find.getByRole('table', { name: 'Matching records', exact: true }));
    await expect(results).toHaveCount(1);
    await expect(results.first()).toContainText(marker);
    const openResult = results
      .first()
      .getByRole('button', { name: `Open record at position ${nAfterCreate}`, exact: true });
    await Promise.all([page.waitForResponse((response) => isRecordGet(response, id)), openResult.click()]);
    await expect(find).toBeHidden();
    await expect(status).toHaveText(statusText(nAfterCreate, nAfterCreate));
    await expect(lastName).toHaveValue(marker);

    const row = await db.pageRow(CASE_REPORT_PAGE, id);
    expect(row, 'the page row of the record before its delete').not.toBeNull();
    return row;
  });

  await test.step('delete the record with confirmation, then show deleted records', async () => {
    const writesBefore = traffic.recordWrites.length;
    await actionButton(page, 'Delete').click();
    const confirm = page.getByRole('dialog', { name: 'Delete record', exact: true });
    await expect(confirm).toBeVisible();
    const [deleted] = await Promise.all([
      page.waitForResponse((response) => isRecordDelete(response, id)),
      confirm.getByRole('button', { name: 'Delete record', exact: true }).click(),
    ]);
    expect(deleted.status(), 'DELETE of the record').toBe(204);
    await expect(confirm).toBeHidden();
    // The confirmed DELETE is the only write: no save of the draft precedes it.
    expect(traffic.recordWrites.slice(writesBefore), 'record writes of the delete').toEqual([
      `DELETE ${pathOf(deleted.url())}`,
    ]);

    await expect.poll(async () => (await db.baseRowById('CaseReport', id))?.recStatus).toBe(0);
    // A soft delete updates the base row only, so the page row is unchanged.
    expect(await db.pageRow(CASE_REPORT_PAGE, id), 'the page row after the delete').toEqual(pageBefore);
    expect(await db.countRecords('CaseReport', false), 'active CaseReport records after the delete').toBe(
      nAfterCreate - 1,
    );
    await expect(status).toHaveText(statusText(nAfterCreate - 1, nAfterCreate - 1));

    const records = recordsRegion(page);
    const search = records.getByLabel('Search records', { exact: true });
    if ((await search.inputValue()) !== marker) {
      await search.fill(marker);
      await search.press('Enter');
    }
    const markedRows = dataRows(page, records.getByRole('table')).filter({ hasText: marker });
    await expect(records.getByText('No records found.', { exact: true })).toBeVisible();
    await expect(markedRows).toHaveCount(0);

    const showDeleted = records.getByRole('checkbox', { name: 'Show deleted', exact: true });
    // The toggle follows navigation state, which changes only once the set is re-read, so it is clicked and its
    // state awaited rather than set by check(), which expects the change at once.
    await showDeleted.click();
    await expect(showDeleted).toBeChecked();
    await expect(markedRows).toHaveCount(1);
    await expect(markedRows.first()).toContainText('(deleted)');
  });

  await test.step('SC-10: check code assigns Age when DOB is left', async () => {
    const scenario = readAgeScenario();
    const writesBefore = traffic.recordWrites.length;
    await backToForms(page);
    await openForm(page, 'FoodHistory', nFood);
    await navigatorButton(page, 'New').click();
    await expect(status).toHaveText('New record');

    await page.getByLabel(INTERVIEW_LABEL, { exact: true }).fill(scenario.given.DateofInterview);
    const dob = page.getByLabel(DOB_LABEL, { exact: true });
    await dob.fill(scenario.given.DOB);
    // DOB is left by blur, not Tab: in Chromium, Tab inside a native date input moves between its month, day and
    // year parts, so focus would stay on DOB and FieldAfter would never be raised.
    const [leftDob] = await Promise.all([
      page.waitForResponse((response) => isFieldEventPost(response, 'FieldAfter', 'DOB')),
      dob.blur(),
    ]);
    expect(leftDob.status(), 'POST of the DOB FieldAfter event').toBe(200);
    const effects: unknown = await leftDob.json();
    expect(effects, 'effects of the DOB FieldAfter event').toMatchObject({
      assigned: { Age: scenario.expected.Age },
    });
    await expect(page.getByLabel(AGE_LABEL, { exact: true })).toHaveValue(String(scenario.expected.Age));

    // The draft holds no marker and is never saved: nothing is written, and closing the page discards it.
    expect(traffic.recordWrites.slice(writesBefore), 'record writes during the check-code step').toEqual([]);
    expect(await db.countRecords('FoodHistory', true), 'FoodHistory records after the check-code step').toBe(
      nFoodAll,
    );
  });
}
