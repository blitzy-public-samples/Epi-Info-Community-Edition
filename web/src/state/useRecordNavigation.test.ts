// Client half of SC-5 navigation: moves from the start, middle and end of a set, the
// edges that send nothing, go-to bounds, New, the empty form, the C-17 sources of
// "[n] of N", the 404 recovery on open, and DATA-01 (opening records sends GETs only).
//
// The hook runs against the real client and error parsing: createApiClient's fetch seam
// is an in-memory API whose routes, record sets and positions follow the server contract.
// The selected set is RecStatus = 1, or every row with includeDeleted (C-16); positions
// count the selected set's rows with UniqueKey <= k; a Record DTO's own position and
// count are intrinsic, over the active set for an active record and over every row for a
// deleted one.
import { act, renderHook, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi, type Mock } from 'vitest';

import { createApiClient } from '../api/client';
import type {
  FieldDefinition,
  NavigationPosition,
  ProblemDetails,
  RecordCountResponse,
  RecordDto,
  Values,
} from '../api/types';
import { formDefinitionFixture, isDataField, recordFixture } from '../test/formDefinitionFixture';
import { useRecordNavigation, type RecordNavigation } from './useRecordNavigation';

const VIEW_ID = formDefinitionFixture.viewId;
const RECORDS_PATH = `/api/forms/${VIEW_ID}/records`;
const ACTIVE = 1;
const DELETED = 0;

/**
 * The notice of an open that met a 404, in the words the 0.3.4.2 error table gives it,
 * matched independently of the client's own text.
 */
const RECORD_GONE = /this record no longer exists/i;

/** Page-2 data fields: a row without its page-2 row reads them as `null`. */
const PAGE_2_FIELDS: readonly FieldDefinition[] = formDefinitionFixture.pages
  .filter((page) => page.position === 2)
  .flatMap((page) => page.fields)
  .filter(isDataField);

// Test data

interface Row {
  readonly globalRecordId: string;
  readonly uniqueKey: number;
  readonly recStatus: number;
  /** The base row has no page-2 row, as a record whose page insert never happened. */
  readonly missingPage: boolean;
}

function idOf(uniqueKey: number): string {
  return `00000000-0000-4000-8000-${String(uniqueKey).padStart(12, '0')}`;
}

function row(uniqueKey: number, init: { readonly recStatus?: number; readonly missingPage?: boolean } = {}): Row {
  return {
    globalRecordId: idOf(uniqueKey),
    uniqueKey,
    recStatus: init.recStatus ?? ACTIVE,
    missingPage: init.missingPage ?? false,
  };
}

/** `count` active rows with UniqueKey 1..count, so position k holds `idOf(k)`. */
function activeRows(count: number): Row[] {
  return Array.from({ length: count }, (_, index) => row(index + 1));
}

function valuesOf(target: Row): Values {
  const values: Values = { CaseID: target.uniqueKey, LastName: `Last ${target.uniqueKey}` };
  for (const field of PAGE_2_FIELDS) {
    values[field.name] = target.missingPage
      ? null
      : field.fieldType === 'checkbox'
        ? true
        : `${field.name} ${target.uniqueKey}`;
  }
  return values;
}

// Expected request lines, as the fake records them.

function countRequest(includeDeleted = false): string {
  return `GET ${RECORDS_PATH}/count?includeDeleted=${String(includeDeleted)}`;
}

function atRequest(position: number, includeDeleted = false): string {
  return `GET ${RECORDS_PATH}/at/${position}?includeDeleted=${String(includeDeleted)}`;
}

function byIdRequest(id: string): string {
  return `GET ${RECORDS_PATH}/${id}`;
}

function positionRequest(id: string, includeDeleted = false): string {
  return `GET ${RECORDS_PATH}/${id}/position?includeDeleted=${String(includeDeleted)}`;
}

/** Requests sent in parallel arrive in no promised order, so they are compared sorted. */
function sorted(lines: readonly string[]): string[] {
  return [...lines].sort();
}

// The in-memory API

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

interface Call {
  readonly method: string;
  readonly url: URL;
}

/** A request held at the fake until the test releases it. */
interface Gate {
  /** Resolves once the held request has reached the fake. */
  readonly reached: Promise<void>;
  /** Lets the held request answer, from the rows as they are then. */
  readonly release: () => void;
}

interface FakeApi {
  readonly fetch: (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>;
  /** Every request received, in arrival order. */
  readonly calls: readonly Call[];
  /** Request lines that matched no route of the four record reads. */
  readonly unrouted: readonly string[];
  /** Request lines (`GET <path><query>`) in arrival order. */
  readonly requests: () => string[];
  /** A point in the log, for requestsSince and logSince. */
  readonly mark: () => number;
  readonly requestsSince: (mark: number) => string[];
  /** Request lines and notes, such as `beforeLeave`, since `mark`. */
  readonly logSince: (mark: number) => string[];
  readonly note: (entry: string) => void;
  /** The Record DTO the API returns for `id` now. */
  readonly dto: (id: string) => RecordDto;
  /** Removes the row, as a record that no longer exists: every read of it answers 404. */
  readonly remove: (id: string) => void;
  readonly setStatus: (id: string, recStatus: number) => void;
  readonly add: (added: Row) => void;
  readonly hold: (pathname: string) => Gate;
  /** From now on every at/{position} answers 404, although count still counts the rows. */
  readonly failEveryAt: () => void;
}

/** Every fake API of the running test, checked for DATA-01 after it. */
const apisOfTest: FakeApi[] = [];

function jsonResponse(body: RecordDto | RecordCountResponse | NavigationPosition): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'content-type': 'application/json' } });
}

function problemResponse(status: number, title: string): Response {
  const body: ProblemDetails = {
    type: 'about:blank',
    title,
    status,
    detail: title,
    traceId: '00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-00',
  };
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/problem+json' },
  });
}

const COUNT_ROUTE = /^\/api\/forms\/(\d+)\/records\/count$/;
const AT_ROUTE = /^\/api\/forms\/(\d+)\/records\/at\/([^/]+)$/;
const POSITION_ROUTE = /^\/api\/forms\/(\d+)\/records\/([^/]+)\/position$/;
const BY_ID_ROUTE = /^\/api\/forms\/(\d+)\/records\/([^/]+)$/;

function createFakeApi(initialRows: readonly Row[]): FakeApi {
  let rows: Row[] = [...initialRows].sort((a, b) => a.uniqueKey - b.uniqueKey);
  let everyAtMissing = false;
  const calls: Call[] = [];
  const unrouted: string[] = [];
  const log: { readonly request: boolean; readonly text: string }[] = [];
  const holds = new Map<string, { readonly arrive: () => void; readonly released: Promise<void> }>();

  const selected = (includeDeleted: boolean): Row[] =>
    includeDeleted ? rows : rows.filter((candidate) => candidate.recStatus === ACTIVE);
  const placeIn = (set: readonly Row[], target: Row): number =>
    set.filter((candidate) => candidate.uniqueKey <= target.uniqueKey).length;
  const find = (id: string): Row | undefined => rows.find((candidate) => candidate.globalRecordId === id);

  const dtoOf = (target: Row): RecordDto => {
    const intrinsic = selected(target.recStatus !== ACTIVE);
    return recordFixture({
      globalRecordId: target.globalRecordId,
      uniqueKey: target.uniqueKey,
      recStatus: target.recStatus,
      position: placeIn(intrinsic, target),
      count: intrinsic.length,
      values: valuesOf(target),
    });
  };

  const notFound = (): Response => problemResponse(404, 'Not Found');

  const route = (method: string, url: URL, line: string): Response => {
    if (method !== 'GET') {
      unrouted.push(line);
      return problemResponse(405, 'Method Not Allowed');
    }
    // Absent means false; anything but true or false is a 400, as RequestParameters coerces it.
    const flag = url.searchParams.get('includeDeleted')?.toLowerCase() ?? 'false';
    if (flag !== 'true' && flag !== 'false') {
      return problemResponse(400, 'One or more validation errors occurred.');
    }
    const includeDeleted = flag === 'true';
    const path = url.pathname;
    const match = COUNT_ROUTE.exec(path) ?? AT_ROUTE.exec(path) ?? POSITION_ROUTE.exec(path) ?? BY_ID_ROUTE.exec(path);
    if (match === null) {
      unrouted.push(line);
      return notFound();
    }
    if (Number(match[1]) !== VIEW_ID) {
      return notFound();
    }
    const set = selected(includeDeleted);

    if (COUNT_ROUTE.test(path)) {
      return jsonResponse({ count: set.length });
    }
    if (AT_ROUTE.test(path)) {
      if (!/^\d+$/.test(match[2])) {
        return problemResponse(400, 'One or more validation errors occurred.');
      }
      const position = Number(match[2]);
      const target = set[position - 1];
      return everyAtMissing || position < 1 || target === undefined ? notFound() : jsonResponse(dtoOf(target));
    }
    const target = find(match[2]);
    if (target === undefined) {
      return notFound();
    }
    if (POSITION_ROUTE.test(path)) {
      const inSet = includeDeleted || target.recStatus === ACTIVE;
      return jsonResponse({ position: inSet ? placeIn(set, target) : null, count: set.length });
    }
    return jsonResponse(dtoOf(target));
  };

  const fetchFromFake = async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
    // openapi-fetch hands over the Request it built; anything else is normalised to one.
    const request = input instanceof Request ? input : new Request(input, init);
    const url = new URL(request.url);
    const line = `${request.method} ${url.pathname}${url.search}`;
    calls.push({ method: request.method, url });
    log.push({ request: true, text: line });
    const held = holds.get(url.pathname);
    if (held !== undefined) {
      holds.delete(url.pathname);
      held.arrive();
      await held.released;
    }
    return route(request.method, url, line);
  };

  const requestLines = (from: number): string[] =>
    log.slice(from).flatMap((entry) => (entry.request ? [entry.text] : []));

  const requireRow = (id: string): Row => {
    const target = find(id);
    if (target === undefined) {
      throw new Error(`fake API has no record ${id}`);
    }
    return target;
  };

  const api: FakeApi = {
    fetch: fetchFromFake,
    calls,
    unrouted,
    requests: () => requestLines(0),
    mark: () => log.length,
    requestsSince: requestLines,
    logSince: (from) => log.slice(from).map((entry) => entry.text),
    note: (entry) => {
      log.push({ request: false, text: entry });
    },
    dto: (id) => dtoOf(requireRow(id)),
    remove: (id) => {
      const target = requireRow(id);
      rows = rows.filter((candidate) => candidate !== target);
    },
    setStatus: (id, recStatus) => {
      const target = requireRow(id);
      rows = rows.map((candidate) => (candidate === target ? { ...target, recStatus } : candidate));
    },
    add: (added) => {
      rows = [...rows, added].sort((a, b) => a.uniqueKey - b.uniqueKey);
    },
    hold: (pathname) => {
      const arrival = deferred<undefined>();
      const release = deferred<undefined>();
      holds.set(pathname, {
        arrive: () => {
          arrival.resolve(undefined);
        },
        released: release.promise,
      });
      return {
        reached: arrival.promise,
        release: () => {
          release.resolve(undefined);
        },
      };
    },
    failEveryAt: () => {
      everyAtMissing = true;
    },
  };
  apisOfTest.push(api);
  return api;
}

// Mounting the hook

interface Current {
  readonly current: RecordNavigation;
}

interface Mounted {
  readonly api: FakeApi;
  readonly result: Current;
  readonly beforeLeave: Mock<() => Promise<boolean>>;
}

interface MountOptions {
  readonly initialPosition?: number;
  /** Replaces the default leave(), which notes `beforeLeave` in the log and resolves `true`. */
  readonly beforeLeave?: () => Promise<boolean>;
}

/** Waits until no load is in flight. */
async function settled(result: Current): Promise<void> {
  await waitFor(() => {
    expect(result.current.loading).toBe(false);
  });
}

/** Mounts the hook on the fixture form over `rows` and waits for the initial load. */
async function mount(rows: readonly Row[], options: MountOptions = {}): Promise<Mounted> {
  const api = createFakeApi(rows);
  const beforeLeave = vi.fn(
    options.beforeLeave ??
      ((): Promise<boolean> => {
        api.note('beforeLeave');
        return Promise.resolve(true);
      }),
  );
  const client = createApiClient({ baseUrl: 'http://localhost', fetch: api.fetch });
  const { result } = renderHook(() =>
    useRecordNavigation({ client, viewId: VIEW_ID, initialPosition: options.initialPosition, beforeLeave }),
  );
  await settled(result);
  return { api, result, beforeLeave };
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

/** Lets every pending promise callback and timer of the fake run. */
function flush(): Promise<void> {
  return new Promise((resolve) => {
    setTimeout(resolve, 0);
  });
}

type Action = (navigation: RecordNavigation) => Promise<unknown>;

interface NamedAction {
  readonly name: string;
  readonly run: Action;
}

const FIRST: NamedAction = { name: 'first', run: (navigation) => navigation.first() };
const PREVIOUS: NamedAction = { name: 'previous', run: (navigation) => navigation.previous() };
const NEXT: NamedAction = { name: 'next', run: (navigation) => navigation.next() };
const LAST: NamedAction = { name: 'last', run: (navigation) => navigation.last() };

function goTo(k: number): NamedAction {
  return { name: `goTo(${k})`, run: (navigation) => navigation.goTo(k) };
}

const READ_ROUTES: readonly RegExp[] = [COUNT_ROUTE, AT_ROUTE, POSITION_ROUTE, BY_ID_ROUTE];

// DATA-01 for every case: whatever a test does, the navigator only reads.
afterEach(() => {
  for (const api of apisOfTest.splice(0)) {
    for (const call of api.calls) {
      expect(call.method).toBe('GET');
      expect(call.url.pathname).not.toContain('/checkcode/field-event');
    }
    expect(api.unrouted).toEqual([]);
  }
});


describe('useRecordNavigation', () => {
  describe('loading and moving through the active set', () => {
    it.each([
      { initialPosition: undefined, opened: 1 },
      { initialPosition: 3, opened: 3 },
      { initialPosition: 0, opened: 1 },
      { initialPosition: 2.5, opened: 1 },
    ])(
      'with initialPosition $initialPosition the load reads N, opens at/$opened and takes n from that request',
      async ({ initialPosition, opened }) => {
        const { api, result, beforeLeave } = await mount(activeRows(5), { initialPosition });

        expect(api.requests()).toEqual([countRequest(), atRequest(opened)]);
        expect(result.current.n).toBe(opened);
        expect(result.current.count).toBe(5);
        expect(result.current.includeDeleted).toBe(false);
        expect(result.current.record?.globalRecordId).toBe(idOf(opened));
        expect(result.current.isNew).toBe(false);
        expect(result.current.statusText).toBe(`${opened} of 5`);
        expect(result.current.recordGeneration).toBe(1);
        expect(result.current.loadError).toBeNull();
        expect(beforeLeave).not.toHaveBeenCalled();
      },
    );

    it.each([
      { from: 1, ...FIRST, to: 1 },
      { from: 1, ...NEXT, to: 2 },
      { from: 1, ...LAST, to: 5 },
      { from: 1, ...goTo(4), to: 4 },
      { from: 3, ...FIRST, to: 1 },
      { from: 3, ...PREVIOUS, to: 2 },
      { from: 3, ...NEXT, to: 4 },
      { from: 3, ...LAST, to: 5 },
      { from: 3, ...goTo(1), to: 1 },
      { from: 5, ...FIRST, to: 1 },
      { from: 5, ...PREVIOUS, to: 4 },
      { from: 5, ...LAST, to: 5 },
      { from: 5, ...goTo(2), to: 2 },
    ])('$name from position $from leaves the record once, then opens at/$to', async ({ from, run, to }) => {
      const { api, result, beforeLeave } = await mount(activeRows(5), { initialPosition: from });
      const generation = result.current.recordGeneration;
      const mark = api.mark();

      await perform(() => run(result.current));

      // The leave is awaited before the request, and N is reused rather than re-read.
      expect(api.logSince(mark)).toEqual(['beforeLeave', atRequest(to)]);
      expect(beforeLeave).toHaveBeenCalledTimes(1);
      expect(result.current.n).toBe(to);
      expect(result.current.count).toBe(5);
      expect(result.current.record?.globalRecordId).toBe(idOf(to));
      expect(result.current.statusText).toBe(`${to} of 5`);
      expect(result.current.recordGeneration).toBe(generation + 1);
    });

    const edgeMoves: readonly {
      readonly name: string;
      readonly from: number;
      readonly run: Action;
      readonly enabled: (navigation: RecordNavigation) => boolean;
    }[] = [
      { name: 'previous at 1', from: 1, run: PREVIOUS.run, enabled: (navigation) => navigation.canPrevious },
      { name: 'next at N', from: 5, run: NEXT.run, enabled: (navigation) => navigation.canNext },
    ];

    it.each(edgeMoves)(
      '$name is disabled and sends no request, leaving n unchanged',
      async ({ from, run, enabled }) => {
        const { api, result, beforeLeave } = await mount(activeRows(5), { initialPosition: from });
        const opened = result.current.record;
        const callsBefore = api.calls.length;

        expect(enabled(result.current)).toBe(false);
        await perform(() => run(result.current));

        expect(api.calls.length).toBe(callsBefore);
        expect(beforeLeave).not.toHaveBeenCalled();
        expect(result.current.n).toBe(from);
        expect(result.current.record).toBe(opened);
      },
    );

    it.each([0, 6, 2.5, -1, Number.NaN])(
      'goTo(%s) outside 1..N is rejected in the box: false, goToError, no leave and no request',
      async (k) => {
        const { api, result, beforeLeave } = await mount(activeRows(5));
        const mark = api.mark();

        const accepted = await perform(() => result.current.goTo(k));

        expect(accepted).toBe(false);
        expect(result.current.goToError).toBe('Enter a record number from 1 to 5.');
        expect(api.logSince(mark)).toEqual([]);
        expect(beforeLeave).not.toHaveBeenCalled();
        expect(result.current.n).toBe(1);

        // The next transition clears the message.
        expect(await perform(() => result.current.goTo(2))).toBe(true);
        expect(result.current.goToError).toBeNull();
        expect(result.current.n).toBe(2);
      },
    );

    it('New shows "New record" with no request, and previous from it opens at/{N}', async () => {
      const { api, result, beforeLeave } = await mount(activeRows(5), { initialPosition: 2 });
      const generation = result.current.recordGeneration;
      const mark = api.mark();

      await perform(() => result.current.newRecord());

      expect(beforeLeave).toHaveBeenCalledTimes(1);
      expect(api.requestsSince(mark)).toEqual([]);
      expect(result.current.isNew).toBe(true);
      expect(result.current.n).toBeNull();
      expect(result.current.record).toBeNull();
      expect(result.current.count).toBe(5);
      expect(result.current.statusText).toBe('New record');
      expect(result.current.recordGeneration).toBe(generation + 1);
      expect(result.current.canPrevious).toBe(true);
      expect(result.current.canNext).toBe(false);

      // Next from New does nothing; previous opens the last record of the N shown with New.
      await perform(() => result.current.next());
      expect(api.requestsSince(mark)).toEqual([]);
      await perform(() => result.current.previous());

      expect(api.requestsSince(mark)).toEqual([atRequest(5)]);
      expect(result.current.isNew).toBe(false);
      expect(result.current.n).toBe(5);
      expect(result.current.record?.globalRecordId).toBe(idOf(5));
      expect(result.current.statusText).toBe('5 of 5');
      expect(result.current.recordGeneration).toBe(generation + 2);
    });

    it('a move waits for beforeLeave and sends nothing until it resolves', async () => {
      const leaving = deferred<boolean>();
      const { api, result, beforeLeave } = await mount(activeRows(5), { beforeLeave: () => leaving.promise });
      const mark = api.mark();

      await act(async () => {
        const moving = result.current.next();
        await flush();
        expect(beforeLeave).toHaveBeenCalledTimes(1);
        expect(api.requestsSince(mark)).toEqual([]);
        expect(result.current.n).toBe(1);

        leaving.resolve(true);
        await moving;
      });

      expect(api.requestsSince(mark)).toEqual([atRequest(2)]);
      expect(result.current.n).toBe(2);
    });

    const leavingTransitions: readonly NamedAction[] = [
      FIRST,
      PREVIOUS,
      NEXT,
      LAST,
      goTo(5),
      { name: 'newRecord', run: (navigation) => navigation.newRecord() },
      { name: 'openById', run: (navigation) => navigation.openById(idOf(4)) },
    ];

    it.each(leavingTransitions)(
      '$name is cancelled with no request when beforeLeave resolves false',
      async ({ run }) => {
        const { api, result, beforeLeave } = await mount(activeRows(5), { initialPosition: 3 });
        beforeLeave.mockImplementation(() => Promise.resolve(false));
        const before = result.current;
        const mark = api.mark();

        await perform(() => run(result.current));

        expect(beforeLeave).toHaveBeenCalledTimes(1);
        expect(api.requestsSince(mark)).toEqual([]);
        expect(result.current.n).toBe(3);
        expect(result.current.isNew).toBe(false);
        expect(result.current.record).toBe(before.record);
        expect(result.current.recordGeneration).toBe(before.recordGeneration);
        expect(result.current.listVersion).toBe(before.listVersion);
      },
    );

    it('an empty form shows New, disables every move and never requests at/*', async () => {
      const { api, result, beforeLeave } = await mount([]);

      expect(api.requests()).toEqual([countRequest()]);
      expect(result.current.isNew).toBe(true);
      expect(result.current.n).toBeNull();
      expect(result.current.count).toBe(0);
      expect(result.current.record).toBeNull();
      expect(result.current.statusText).toBe('New record');
      expect(result.current.canFirst).toBe(false);
      expect(result.current.canPrevious).toBe(false);
      expect(result.current.canNext).toBe(false);
      expect(result.current.canLast).toBe(false);
      expect(result.current.canGoTo).toBe(false);

      const mark = api.mark();
      for (const { run } of [FIRST, PREVIOUS, NEXT, LAST]) {
        await perform(() => run(result.current));
      }
      expect(await perform(() => result.current.goTo(1))).toBe(false);
      expect(result.current.goToError).toBe('There are no records to go to.');
      expect(api.requestsSince(mark)).toEqual([]);
      expect(beforeLeave).not.toHaveBeenCalled();

      const generation = result.current.recordGeneration;
      await perform(() => result.current.newRecord());

      expect(beforeLeave).toHaveBeenCalledTimes(1);
      expect(result.current.isNew).toBe(true);
      expect(result.current.goToError).toBeNull();
      expect(result.current.recordGeneration).toBe(generation + 1);
      expect(api.requestsSince(mark)).toEqual([]);
      expect(api.calls.some((call) => call.url.pathname.includes('/records/at/'))).toBe(false);
    });
  });


  // UniqueKey order A(1), D(0), B(1), C(1): B is record 2 of the active set and record
  // 3 of every row, and D exists only in the set with deleted records.
  const A = row(1);
  const D = row(2, { recStatus: DELETED });
  const B = row(3);
  const C = row(4);
  const mixedRows = (): Row[] => [A, D, B, C];

  describe('position sources (C-17)', () => {
    it('n follows the toggle through the position endpoint; the DTO keeps its intrinsic position and count', async () => {
      const { api, result, beforeLeave } = await mount(mixedRows());
      let mark = api.mark();

      await perform(() => result.current.openById(B.globalRecordId));

      expect(sorted(api.requestsSince(mark))).toEqual(
        sorted([byIdRequest(B.globalRecordId), positionRequest(B.globalRecordId), countRequest()]),
      );
      expect(result.current.n).toBe(2);
      expect(result.current.count).toBe(3);
      expect(result.current.statusText).toBe('2 of 3');
      const record = result.current.record;
      const generation = result.current.recordGeneration;
      expect(record?.globalRecordId).toBe(B.globalRecordId);
      expect(record?.position).toBe(2);
      expect(record?.count).toBe(3);

      mark = api.mark();
      await perform(() => result.current.setIncludeDeleted(true));

      expect(sorted(api.requestsSince(mark))).toEqual(
        sorted([countRequest(true), positionRequest(B.globalRecordId, true)]),
      );
      expect(result.current.includeDeleted).toBe(true);
      expect(result.current.n).toBe(3);
      expect(result.current.count).toBe(4);
      expect(result.current.statusText).toBe('3 of 4');
      // The same record stays open: neither the DTO nor the generation changes.
      expect(result.current.record).toBe(record);
      expect(result.current.record?.position).toBe(2);
      expect(result.current.record?.count).toBe(3);
      expect(result.current.recordGeneration).toBe(generation);

      // Moves use the toggle's set: record 3 of every row is B.
      mark = api.mark();
      await perform(() => result.current.goTo(3));
      expect(api.requestsSince(mark)).toEqual([atRequest(3, true)]);
      expect(result.current.record?.globalRecordId).toBe(B.globalRecordId);
      expect(result.current.record?.position).toBe(2);

      mark = api.mark();
      await perform(() => result.current.setIncludeDeleted(false));

      expect(sorted(api.requestsSince(mark))).toEqual(sorted([countRequest(), positionRequest(B.globalRecordId)]));
      expect(result.current.includeDeleted).toBe(false);
      expect(result.current.n).toBe(2);
      expect(result.current.count).toBe(3);
      expect(result.current.record?.position).toBe(2);
      expect(result.current.record?.count).toBe(3);
      // Switching sets with an active record open does not leave it; only the goTo did.
      expect(beforeLeave).toHaveBeenCalledTimes(2);
    });

    it('after openById, n is the position endpoint answer for the toggle, not the DTO position', async () => {
      const { api, result } = await mount(mixedRows());
      await perform(() => result.current.setIncludeDeleted(true));
      expect(result.current.n).toBe(1);
      expect(result.current.count).toBe(4);
      const mark = api.mark();

      await perform(() => result.current.openById(C.globalRecordId));

      expect(sorted(api.requestsSince(mark))).toEqual(
        sorted([byIdRequest(C.globalRecordId), positionRequest(C.globalRecordId, true), countRequest(true)]),
      );
      expect(result.current.n).toBe(4);
      expect(result.current.count).toBe(4);
      expect(result.current.statusText).toBe('4 of 4');
      expect(result.current.record?.position).toBe(3);
      expect(result.current.record?.count).toBe(3);
    });

    it('a record missing its page-2 row is placed by the position endpoint and returned by at/{n}', async () => {
      const gap = row(2, { missingPage: true });
      const { api, result } = await mount([row(1), gap, row(3)]);
      for (const field of PAGE_2_FIELDS) {
        expect(result.current.record?.values[field.name]).not.toBeNull();
      }
      let mark = api.mark();

      await perform(() => result.current.openById(gap.globalRecordId));

      expect(api.requestsSince(mark)).toContain(positionRequest(gap.globalRecordId));
      expect(result.current.n).toBe(2);
      expect(result.current.statusText).toBe('2 of 3');
      expect(result.current.record?.values.LastName).toBe('Last 2');
      for (const field of PAGE_2_FIELDS) {
        expect(result.current.record?.values[field.name]).toBeNull();
      }

      await perform(() => result.current.first());
      mark = api.mark();
      await perform(() => result.current.goTo(2));

      expect(api.requestsSince(mark)).toEqual([atRequest(2)]);
      expect(result.current.n).toBe(2);
      expect(result.current.record?.globalRecordId).toBe(gap.globalRecordId);
      for (const field of PAGE_2_FIELDS) {
        expect(result.current.record?.values[field.name]).toBeNull();
      }
    });

    it('afterSave of a created record re-reads N and its position and keeps the generation', async () => {
      const { api, result } = await mount(activeRows(3));
      await perform(() => result.current.newRecord());
      const { recordGeneration, listVersion } = result.current;
      const created = row(4);
      api.add(created);
      const saved = api.dto(created.globalRecordId);
      const mark = api.mark();

      act(() => {
        result.current.afterSave(saved);
      });
      // The saved record shows at once; n is unknown until the position endpoint answers.
      expect(result.current.record).toBe(saved);
      expect(result.current.isNew).toBe(false);
      expect(result.current.n).toBeNull();
      await settled(result);

      expect(sorted(api.requestsSince(mark))).toEqual(
        sorted([countRequest(), positionRequest(created.globalRecordId)]),
      );
      expect(result.current.n).toBe(4);
      expect(result.current.count).toBe(4);
      expect(result.current.statusText).toBe('4 of 4');
      expect(result.current.record).toBe(saved);
      expect(result.current.recordGeneration).toBe(recordGeneration);
      expect(result.current.listVersion).toBe(listVersion + 1);

      // N is fresh again, so the next move reuses it rather than re-reading it.
      const moveMark = api.mark();
      await perform(() => result.current.previous());
      expect(api.requestsSince(moveMark)).toEqual([atRequest(3)]);
      expect(result.current.n).toBe(3);
    });

    it('afterSave of an open record takes n and N from fresh reads, not from the move that opened it', async () => {
      const { api, result } = await mount(activeRows(3), { initialPosition: 2 });
      const { recordGeneration } = result.current;
      // Record 1 was deleted in another session while record 2 was being edited.
      api.setStatus(idOf(1), DELETED);
      const saved = api.dto(idOf(2));
      const mark = api.mark();

      act(() => {
        result.current.afterSave(saved);
      });
      await settled(result);

      expect(sorted(api.requestsSince(mark))).toEqual(sorted([countRequest(), positionRequest(idOf(2))]));
      expect(result.current.n).toBe(1);
      expect(result.current.count).toBe(2);
      expect(result.current.statusText).toBe('1 of 2');
      expect(result.current.record).toBe(saved);
      expect(result.current.recordGeneration).toBe(recordGeneration);
    });
  });

  describe('the deleted toggle with a deleted record open', () => {
    it('switching it off leaves the record and opens at/{min(n, count)} of the active set', async () => {
      const { api, result, beforeLeave } = await mount(mixedRows());
      await perform(() => result.current.setIncludeDeleted(true));
      await perform(() => result.current.openById(D.globalRecordId));
      expect(result.current.n).toBe(2);
      expect(result.current.count).toBe(4);
      // A deleted record's own coordinates are over every row.
      expect(result.current.record?.recStatus).toBe(DELETED);
      expect(result.current.record?.position).toBe(2);
      expect(result.current.record?.count).toBe(4);
      const leaves = beforeLeave.mock.calls.length;
      const { recordGeneration } = result.current;
      const mark = api.mark();

      await perform(() => result.current.setIncludeDeleted(false));

      const requests = api.requestsSince(mark);
      expect(sorted(requests.slice(0, 2))).toEqual(sorted([countRequest(), positionRequest(D.globalRecordId)]));
      expect(requests.slice(2)).toEqual([atRequest(2)]);
      expect(beforeLeave).toHaveBeenCalledTimes(leaves + 1);
      expect(result.current.includeDeleted).toBe(false);
      expect(result.current.n).toBe(2);
      expect(result.current.count).toBe(3);
      expect(result.current.record?.globalRecordId).toBe(B.globalRecordId);
      expect(result.current.recordGeneration).toBe(recordGeneration + 1);
    });

    it('switching it off shows New when every record is deleted', async () => {
      const { api, result } = await mount([row(1, { recStatus: DELETED }), row(2, { recStatus: DELETED })]);
      expect(result.current.isNew).toBe(true);
      expect(result.current.count).toBe(0);

      // From New only N changes.
      await perform(() => result.current.setIncludeDeleted(true));
      expect(result.current.isNew).toBe(true);
      expect(result.current.count).toBe(2);
      expect(result.current.statusText).toBe('New record');

      await perform(() => result.current.openById(idOf(1)));
      expect(result.current.n).toBe(1);
      expect(result.current.count).toBe(2);
      const mark = api.mark();

      await perform(() => result.current.setIncludeDeleted(false));

      expect(sorted(api.requestsSince(mark))).toEqual(sorted([countRequest(), positionRequest(idOf(1))]));
      expect(result.current.isNew).toBe(true);
      expect(result.current.record).toBeNull();
      expect(result.current.n).toBeNull();
      expect(result.current.count).toBe(0);
      expect(result.current.statusText).toBe('New record');
    });
  });


  describe('a record that no longer exists (404 on open)', () => {
    it('openById shows the notice, reloads the list and N, and opens at/{min(n, count)}', async () => {
      const { api, result } = await mount(activeRows(5), { initialPosition: 3 });
      const { listVersion } = result.current;
      const vanished = idOf(2);
      api.remove(vanished);
      const mark = api.mark();

      await perform(() => result.current.openById(vanished));

      const requests = api.requestsSince(mark);
      expect(sorted(requests.slice(0, 3))).toEqual(
        sorted([byIdRequest(vanished), positionRequest(vanished), countRequest()]),
      );
      expect(requests.slice(3)).toEqual([countRequest(), atRequest(3)]);
      expect(result.current.notice).toMatch(RECORD_GONE);
      expect(result.current.listVersion).toBe(listVersion + 1);
      expect(result.current.n).toBe(3);
      expect(result.current.count).toBe(4);
      // Position 3 of the remaining 1, 3, 4, 5.
      expect(result.current.record?.globalRecordId).toBe(idOf(4));
      expect(result.current.isNew).toBe(false);
      expect(result.current.loadError).toBeNull();
    });

    it('goTo of a position that vanished since N was read opens the nearest record', async () => {
      const { api, result } = await mount(activeRows(5));
      const { listVersion } = result.current;
      api.remove(idOf(4));
      api.remove(idOf(5));
      const mark = api.mark();

      const accepted = await perform(() => result.current.goTo(5));

      expect(accepted).toBe(false);
      expect(api.requestsSince(mark)).toEqual([atRequest(5), countRequest(), atRequest(3)]);
      expect(result.current.notice).toMatch(RECORD_GONE);
      expect(result.current.listVersion).toBe(listVersion + 1);
      expect(result.current.n).toBe(3);
      expect(result.current.count).toBe(3);
      expect(result.current.record?.globalRecordId).toBe(idOf(3));
      expect(result.current.statusText).toBe('3 of 3');
    });

    it('shows the empty state when no record is left', async () => {
      const { api, result } = await mount(activeRows(2));
      api.remove(idOf(1));
      api.remove(idOf(2));
      const mark = api.mark();

      await perform(() => result.current.goTo(2));

      expect(api.requestsSince(mark)).toEqual([atRequest(2), countRequest()]);
      expect(result.current.notice).toMatch(RECORD_GONE);
      expect(result.current.isNew).toBe(true);
      expect(result.current.record).toBeNull();
      expect(result.current.n).toBeNull();
      expect(result.current.count).toBe(0);
      expect(result.current.statusText).toBe('New record');
    });

    it('recovers once: a second 404 after goTo installs New instead of looping', async () => {
      const { api, result } = await mount(activeRows(3));
      const { listVersion } = result.current;
      api.failEveryAt();
      const mark = api.mark();

      await perform(() => result.current.goTo(2));
      await act(flush);

      expect(api.requestsSince(mark)).toEqual([atRequest(2), countRequest(), atRequest(2)]);
      expect(result.current.notice).toMatch(RECORD_GONE);
      expect(result.current.isNew).toBe(true);
      expect(result.current.count).toBe(3);
      expect(result.current.listVersion).toBe(listVersion + 1);
    });

    it('recovers once: a second 404 after openById installs New instead of looping', async () => {
      const { api, result } = await mount(activeRows(3));
      const { listVersion } = result.current;
      const vanished = idOf(2);
      api.remove(vanished);
      api.failEveryAt();
      const mark = api.mark();

      await perform(() => result.current.openById(vanished));
      await act(flush);

      const requests = api.requestsSince(mark);
      expect(sorted(requests.slice(0, 3))).toEqual(
        sorted([byIdRequest(vanished), positionRequest(vanished), countRequest()]),
      );
      expect(requests.slice(3)).toEqual([countRequest(), atRequest(1)]);
      expect(result.current.notice).toMatch(RECORD_GONE);
      expect(result.current.isNew).toBe(true);
      expect(result.current.count).toBe(2);
      expect(result.current.listVersion).toBe(listVersion + 1);
    });
  });

  describe('afterDelete', () => {
    it.each([
      { name: 'the last record', open: 3, n: 2, opened: idOf(2) },
      { name: 'a middle record', open: 2, n: 2, opened: idOf(3) },
    ])(
      'after deleting $name it re-reads N and opens at/{min(n, count)} without leaving',
      async ({ open, n, opened }) => {
        const { api, result, beforeLeave } = await mount(activeRows(3), { initialPosition: open });
        const { listVersion, recordGeneration } = result.current;
        // The editor's DELETE soft-deleted the open record.
        api.setStatus(idOf(open), DELETED);
        const mark = api.mark();

        act(() => {
          result.current.afterDelete();
        });
        await settled(result);

        expect(api.requestsSince(mark)).toEqual([countRequest(), atRequest(n)]);
        expect(beforeLeave).not.toHaveBeenCalled();
        expect(result.current.n).toBe(n);
        expect(result.current.count).toBe(2);
        expect(result.current.record?.globalRecordId).toBe(opened);
        expect(result.current.listVersion).toBe(listVersion + 1);
        expect(result.current.recordGeneration).toBe(recordGeneration + 1);
        expect(result.current.notice).toBeNull();
      },
    );

    it('after deleting the only record it shows New', async () => {
      const { api, result, beforeLeave } = await mount(activeRows(1));
      api.setStatus(idOf(1), DELETED);
      const mark = api.mark();

      act(() => {
        result.current.afterDelete();
      });
      await settled(result);

      expect(api.requestsSince(mark)).toEqual([countRequest()]);
      expect(beforeLeave).not.toHaveBeenCalled();
      expect(result.current.isNew).toBe(true);
      expect(result.current.count).toBe(0);
      expect(result.current.statusText).toBe('New record');
    });
  });

  describe('superseded loads', () => {
    it('ignores a response that a later move overtook', async () => {
      const { api, result } = await mount(activeRows(5));
      const gate = api.hold(`${RECORDS_PATH}/at/2`);
      let overtaken: Promise<void> = Promise.resolve();

      await act(async () => {
        overtaken = result.current.next();
        await gate.reached;
      });
      expect(result.current.loading).toBe(true);
      expect(result.current.n).toBe(1);

      expect(await perform(() => result.current.goTo(4))).toBe(true);
      expect(result.current.n).toBe(4);

      await act(async () => {
        gate.release();
        await overtaken;
      });

      expect(api.requests().slice(-2)).toEqual([atRequest(2), atRequest(4)]);
      expect(result.current.loading).toBe(false);
      expect(result.current.n).toBe(4);
      expect(result.current.record?.globalRecordId).toBe(idOf(4));
      expect(result.current.statusText).toBe('4 of 5');
    });
  });

  describe('a success body outside the contract', () => {
    it('shows a count answered 200 without its count as a server load error', async () => {
      const requests: string[] = [];
      // Every read answers 200 with an empty object, which no read's contract allows.
      const answerEmptyObject = (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
        const request = input instanceof Request ? input : new Request(input, init);
        const url = new URL(request.url);
        requests.push(`${request.method} ${url.pathname}${url.search}`);
        return Promise.resolve(new Response('{}', { status: 200, headers: { 'content-type': 'application/json' } }));
      };
      const client = createApiClient({ baseUrl: 'http://localhost', fetch: answerEmptyObject });
      const beforeLeave = vi.fn((): Promise<boolean> => Promise.resolve(true));

      const { result } = renderHook(() => useRecordNavigation({ client, viewId: VIEW_ID, beforeLeave }));
      await settled(result);

      // The load stops at the count, shown with Retry, and opens nothing from a count it lacks.
      expect(requests).toEqual([countRequest()]);
      expect(result.current.loadError?.kind).toBe('server');
      expect(result.current.loadError?.status).toBe(200);
      expect(result.current.record).toBeNull();
      expect(beforeLeave).not.toHaveBeenCalled();
    });
  });

  describe('reads never write (DATA-01)', () => {
    it('a whole navigation session sends only GETs to the four record-read routes', async () => {
      const { api, result } = await mount(mixedRows());
      const steps: readonly Action[] = [
        NEXT.run,
        LAST.run,
        goTo(1).run,
        (navigation) => navigation.newRecord(),
        PREVIOUS.run,
        (navigation) => navigation.openById(B.globalRecordId),
        (navigation) => navigation.setIncludeDeleted(true),
        (navigation) => navigation.openById(D.globalRecordId),
        (navigation) => navigation.setIncludeDeleted(false),
      ];
      for (const step of steps) {
        await perform(() => step(result.current));
      }
      act(() => {
        result.current.afterSave(api.dto(B.globalRecordId));
      });
      await settled(result);
      api.setStatus(B.globalRecordId, DELETED);
      act(() => {
        result.current.afterDelete();
      });
      await settled(result);

      expect(api.calls.length).toBeGreaterThan(steps.length);
      expect(new Set(api.calls.map((call) => call.method))).toEqual(new Set(['GET']));
      for (const call of api.calls) {
        expect(READ_ROUTES.some((readRoute) => readRoute.test(call.url.pathname))).toBe(true);
        expect(call.url.pathname).not.toContain('/checkcode/field-event');
      }
    });
  });
});
