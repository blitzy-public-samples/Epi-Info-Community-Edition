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
import { StrictMode, useLayoutEffect, useRef } from 'react';
import { afterEach, describe, expect, it, vi, type Mock } from 'vitest';

import { createApiClient } from '../api/client';
import { isSafeToResend } from '../api/errors';
import type {
  FieldDefinition,
  NavigationPosition,
  ProblemDetails,
  RecordCountResponse,
  RecordDto,
  Values,
} from '../api/types';
import { formDefinitionFixture, isDataField, recordFixture } from '../test/formDefinitionFixture';
import { useRecordEditor, type RecordEditor } from './useRecordEditor';
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

/**
 * The notice of an open that lands on another record because the one it meant is deleted
 * and the toggle hides deleted records: it names the deletion and the "Show deleted"
 * toggle, matched independently of the client's own text.
 */
const DELETED_HIDDEN = /\bdeleted\b.*\bshow deleted\b/i;

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
  /**
   * From now on the position route answers 404 for `id`, while the read by id still
   * answers 200 with its DTO and count still counts the row.
   */
  readonly failPosition: (id: string) => void;
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
  const positionMissing = new Set<string>();
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
      if (positionMissing.has(target.globalRecordId)) {
        return notFound();
      }
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
    failPosition: (id) => {
      positionMissing.add(requireRow(id).globalRecordId);
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
  readonly initialPositionText?: string;
  readonly initialIncludeDeleted?: boolean;
  /** Replaces the default leave(), which notes `beforeLeave` in the log and resolves `true`. */
  readonly beforeLeave?: () => Promise<boolean>;
  /** The client's request deadline; without it, the default one. */
  readonly deadlineMs?: number;
  /** Mounts under StrictMode, which runs the mount effect, its cleanup and the effect again. */
  readonly strict?: boolean;
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
  const client = createApiClient({ baseUrl: 'http://localhost', fetch: api.fetch, deadlineMs: options.deadlineMs });
  const { result } = renderHook(
    () =>
      useRecordNavigation({
        client,
        viewId: VIEW_ID,
        initialPosition: options.initialPosition,
        initialPositionText: options.initialPositionText,
        initialIncludeDeleted: options.initialIncludeDeleted,
        beforeLeave,
      }),
    options.strict === true ? { wrapper: StrictMode } : {},
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

    it('an accepted goTo clears an earlier rejection even when beforeLeave resolves false', async () => {
      const { api, result, beforeLeave } = await mount(activeRows(5));
      expect(await perform(() => result.current.goTo(999999))).toBe(false);
      expect(result.current.goToError).toBe('Enter a record number from 1 to 5.');
      beforeLeave.mockImplementation(() => Promise.resolve(false));
      const before = result.current;
      const mark = api.mark();

      const accepted = await perform(() => result.current.goTo(2));

      // goToError describes the last goTo, which was a valid number, not the earlier one.
      expect(accepted).toBe(false);
      expect(result.current.goToError).toBeNull();
      expect(beforeLeave).toHaveBeenCalledTimes(1);
      expect(api.requestsSince(mark)).toEqual([]);
      expect(result.current.n).toBe(1);
      expect(result.current.record).toBe(before.record);
      expect(result.current.recordGeneration).toBe(before.recordGeneration);
    });

    it.each([
      { name: 'start', from: 1 },
      { name: 'middle', from: 3 },
      { name: 'end', from: 5 },
    ])(
      'New from the $name (position $from) shows "New record" with no request, and previous from it opens at/{N}',
      async ({ from }) => {
        const { api, result, beforeLeave } = await mount(activeRows(5), { initialPosition: from });
        // The mount's own read of at/{from} precedes the mark, so the at/5 below is a new request.
        expect(api.requests()).toEqual([countRequest(), atRequest(from)]);
        // The record New leaves is the one at this position.
        expect(result.current.n).toBe(from);
        expect(result.current.record?.globalRecordId).toBe(idOf(from));
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
      },
    );

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

      // Moves use the toggle's set: record 3 of every row is B. n is the requested
      // position, while the DTO at/3 returns keeps its active-set 2 of 3.
      mark = api.mark();
      await perform(() => result.current.goTo(3));
      expect(api.requestsSince(mark)).toEqual([atRequest(3, true)]);
      expect(result.current.record?.globalRecordId).toBe(B.globalRecordId);
      expect(result.current.n).toBe(3);
      expect(result.current.count).toBe(4);
      expect(result.current.statusText).toBe('3 of 4');
      expect(result.current.record?.position).toBe(2);
      expect(result.current.record?.count).toBe(3);

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
      expect(result.current.notice).toBeNull();
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

    // B's saved DTO is 2 of 3 over the active set in both cases, while the refresh places
    // it elsewhere, so only the count and position reads can supply n and N.
    const savedElsewhere: readonly {
      readonly name: string;
      readonly includeDeleted: boolean;
      readonly opened: { readonly n: number; readonly count: number };
      /** Rows another session deletes after the save answered and before the refresh. */
      readonly deletedAfterSave: readonly string[];
      readonly refreshed: { readonly n: number; readonly count: number };
      readonly statusText: string;
    }[] = [
      {
        name: 'with deleted records shown, the deleted record before it counts',
        includeDeleted: true,
        opened: { n: 3, count: 4 },
        deletedAfterSave: [],
        refreshed: { n: 3, count: 4 },
        statusText: '3 of 4',
      },
      {
        name: 'with the active set shown, the record before it was deleted after the save',
        includeDeleted: false,
        opened: { n: 2, count: 3 },
        deletedAfterSave: [A.globalRecordId],
        refreshed: { n: 1, count: 2 },
        statusText: '1 of 2',
      },
    ];

    it.each(savedElsewhere)(
      'afterSave takes n and N from the refresh, not the saved DTO, $name',
      async ({ includeDeleted, opened, deletedAfterSave, refreshed, statusText }) => {
        const { api, result } = await mount(mixedRows());
        await perform(() => result.current.setIncludeDeleted(includeDeleted));
        await perform(() => result.current.openById(B.globalRecordId));
        expect(result.current.n).toBe(opened.n);
        expect(result.current.count).toBe(opened.count);
        const { recordGeneration } = result.current;
        const saved = api.dto(B.globalRecordId);
        expect(saved.recStatus).toBe(ACTIVE);
        expect(saved.position).toBe(2);
        expect(saved.count).toBe(3);
        expect({ n: saved.position, count: saved.count }).not.toEqual(refreshed);
        for (const id of deletedAfterSave) {
          api.setStatus(id, DELETED);
        }
        const mark = api.mark();

        act(() => {
          result.current.afterSave(saved);
        });
        await settled(result);

        expect(sorted(api.requestsSince(mark))).toEqual(
          sorted([countRequest(includeDeleted), positionRequest(B.globalRecordId, includeDeleted)]),
        );
        expect(result.current.includeDeleted).toBe(includeDeleted);
        expect(result.current.n).toBe(refreshed.n);
        expect(result.current.count).toBe(refreshed.count);
        expect(result.current.statusText).toBe(statusText);
        // The saved DTO is installed as the server returned it, its own coordinates untouched.
        expect(result.current.record).toBe(saved);
        expect(result.current.record?.position).toBe(2);
        expect(result.current.record?.count).toBe(3);
        expect(result.current.recordGeneration).toBe(recordGeneration);
      },
    );
  });

  describe('the deleted toggle with a deleted record open', () => {
    // The open record's DTO and the server disagree once another session deletes or
    // restores it, so only the position endpoint's answer decides whether it is left.
    const switchedOff: readonly {
      readonly name: string;
      readonly outcome: string;
      /** The record opened with deleted records shown. */
      readonly open: Row;
      /** Its n and N with deleted records shown. */
      readonly opened: { readonly n: number; readonly count: number };
      /** The opened DTO: its recStatus and its intrinsic coordinates. */
      readonly dto: { readonly recStatus: number; readonly position: number; readonly count: number };
      /** The open record's status on the server when the toggle switches off. */
      readonly serverStatus: number;
      /** The log after the toggle's two parallel reads. */
      readonly after: readonly string[];
      /** The record open afterwards. */
      readonly landed: Row;
      readonly n: number;
      readonly count: number;
      readonly statusText: string;
      /** The record is replaced: a new generation, with the notice. */
      readonly replaced: boolean;
    }[] = [
      {
        name: 'the DTO and the server deleted',
        outcome: 'left, then at/{min(n, count)}',
        open: D,
        opened: { n: 2, count: 4 },
        // A deleted record's own coordinates are over every row.
        dto: { recStatus: DELETED, position: 2, count: 4 },
        serverStatus: DELETED,
        after: ['beforeLeave', atRequest(2)],
        landed: B,
        n: 2,
        count: 3,
        statusText: '2 of 3',
        replaced: true,
      },
      {
        name: 'an active DTO the server deleted',
        outcome: 'left, then at/{min(n, count)}',
        open: B,
        opened: { n: 3, count: 4 },
        dto: { recStatus: ACTIVE, position: 2, count: 3 },
        // Another session soft-deletes B after it was opened.
        serverStatus: DELETED,
        // Nothing was saved, so the reads in hand place the landing: the set is not read
        // again. The active set is A, C: position min(3, 2).
        after: ['beforeLeave', atRequest(2)],
        landed: C,
        n: 2,
        count: 2,
        statusText: '2 of 2',
        replaced: true,
      },
      {
        name: 'a deleted DTO the server restored',
        outcome: 'stays at its active-set n, never left',
        open: D,
        opened: { n: 2, count: 4 },
        dto: { recStatus: DELETED, position: 2, count: 4 },
        // Another session restores D after it was opened; its DTO still says deleted.
        serverStatus: ACTIVE,
        after: [],
        landed: D,
        n: 2,
        count: 4,
        statusText: '2 of 4',
        replaced: false,
      },
    ];

    it.each(switchedOff)(
      'switching it off with $name decides from the position endpoint: $outcome',
      async ({ open, opened, dto, serverStatus, after, landed, n, count, statusText, replaced }) => {
        const { api, result, beforeLeave } = await mount(mixedRows());
        await perform(() => result.current.setIncludeDeleted(true));
        await perform(() => result.current.openById(open.globalRecordId));
        expect(result.current.n).toBe(opened.n);
        expect(result.current.count).toBe(opened.count);
        expect(result.current.record?.recStatus).toBe(dto.recStatus);
        expect(result.current.record?.position).toBe(dto.position);
        expect(result.current.record?.count).toBe(dto.count);
        api.setStatus(open.globalRecordId, serverStatus);
        const leaves = beforeLeave.mock.calls.length;
        const { record, recordGeneration, listVersion } = result.current;
        const mark = api.mark();

        await perform(() => result.current.setIncludeDeleted(false));

        // The reads come first: a leave, where there is one, waits for the position response.
        const log = api.logSince(mark);
        expect(sorted(log.slice(0, 2))).toEqual(sorted([countRequest(), positionRequest(open.globalRecordId)]));
        expect(log.slice(2)).toEqual(after);
        expect(beforeLeave).toHaveBeenCalledTimes(leaves + (replaced ? 1 : 0));
        expect(result.current.includeDeleted).toBe(false);
        expect(result.current.n).toBe(n);
        expect(result.current.count).toBe(count);
        expect(result.current.statusText).toBe(statusText);
        expect(result.current.record?.globalRecordId).toBe(landed.globalRecordId);
        if (replaced) {
          expect(result.current.recordGeneration).toBe(recordGeneration + 1);
          expect(result.current.notice).toMatch(DELETED_HIDDEN);
        } else {
          // The same record stays: neither the DTO nor the generation changes.
          expect(result.current.record).toBe(record);
          expect(result.current.recordGeneration).toBe(recordGeneration);
          expect(result.current.notice).toBeNull();
        }
        // The grid reloads for the toggle change itself, so the list version stays.
        expect(result.current.listVersion).toBe(listVersion);
        expect(result.current.loadError).toBeNull();
      },
    );

    it('switching it off reads the set again when its leave saves, and keeps a record the second reads place in the active set', async () => {
      const { api, result, beforeLeave } = await mount(mixedRows());
      await perform(() => result.current.setIncludeDeleted(true));
      await perform(() => result.current.openById(B.globalRecordId));
      expect(result.current.statusText).toBe('3 of 4');
      // Another session soft-deletes B, so the toggle's reads place it outside the active set.
      api.setStatus(B.globalRecordId, DELETED);
      // The leave saves the draft; B was restored elsewhere meanwhile, so the save succeeds.
      beforeLeave.mockImplementation(() => {
        api.note('beforeLeave');
        api.setStatus(B.globalRecordId, ACTIVE);
        result.current.afterSave(api.dto(B.globalRecordId));
        return Promise.resolve(true);
      });
      const leaves = beforeLeave.mock.calls.length;
      const { recordGeneration } = result.current;
      const mark = api.mark();

      await perform(() => result.current.setIncludeDeleted(false));

      // The save may have moved B, so the set is read again, which places B in it: nothing follows.
      const log = api.logSince(mark);
      const toggleReads = sorted([countRequest(), positionRequest(B.globalRecordId)]);
      expect(sorted(log.slice(0, 2))).toEqual(toggleReads);
      expect(log[2]).toBe('beforeLeave');
      expect(sorted(log.slice(3))).toEqual(toggleReads);
      expect(beforeLeave).toHaveBeenCalledTimes(leaves + 1);
      expect(result.current.record?.globalRecordId).toBe(B.globalRecordId);
      expect(result.current.recordGeneration).toBe(recordGeneration);
      expect(result.current.includeDeleted).toBe(false);
      expect(result.current.n).toBe(2);
      expect(result.current.count).toBe(3);
      expect(result.current.statusText).toBe('2 of 3');
      expect(result.current.notice).toBeNull();
      expect(result.current.loadError).toBeNull();
      expect(result.current.loading).toBe(false);
      expect(result.current.transitioning).toBe(false);
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
      expect(result.current.notice).toMatch(DELETED_HIDDEN);
    });
  });

  describe('an open by id of a record deleted since it was listed', () => {
    // A grid row or Find result still lists a record another session has soft-deleted: its
    // read by id answers 200, and its position in the active set is null.
    const deletedSinceListed: readonly {
      readonly name: string;
      readonly rows: readonly Row[];
      readonly initialPosition: number;
      /** Rows soft-deleted after the load, the opened one among them. */
      readonly deleted: readonly string[];
      readonly opening: string;
      /** The requests after the open's three parallel reads. */
      readonly landing: readonly string[];
      readonly n: number | null;
      readonly count: number;
      readonly opened: string | null;
      readonly isNew: boolean;
      readonly statusText: string;
    }[] = [
      {
        name: 'opens at/{min(n, count)} of the active set',
        rows: activeRows(5),
        initialPosition: 5,
        deleted: [idOf(2)],
        opening: idOf(2),
        landing: [atRequest(4)],
        n: 4,
        count: 4,
        // Position 4 of the remaining 1, 3, 4, 5: the record that was open.
        opened: idOf(5),
        isNew: false,
        statusText: '4 of 4',
      },
      {
        name: 'shows New when no active record is left',
        rows: activeRows(2),
        initialPosition: 1,
        deleted: [idOf(1), idOf(2)],
        opening: idOf(2),
        landing: [],
        n: null,
        count: 0,
        opened: null,
        isNew: true,
        statusText: 'New record',
      },
    ];

    it.each(deletedSinceListed)(
      'shows the notice, reloads the list, reads N only once and $name',
      async ({ rows, initialPosition, deleted, opening, landing, n, count, opened, isNew, statusText }) => {
        const { api, result } = await mount(rows, { initialPosition });
        const { listVersion, recordGeneration } = result.current;
        for (const id of deleted) {
          api.setStatus(id, DELETED);
        }
        const mark = api.mark();

        await perform(() => result.current.openById(opening));

        const requests = api.requestsSince(mark);
        expect(sorted(requests.slice(0, 3))).toEqual(
          sorted([byIdRequest(opening), positionRequest(opening), countRequest()]),
        );
        // N comes from the open's own count, so the landing reads no count of its own.
        expect(requests.slice(3)).toEqual(landing);
        expect(result.current.notice).toMatch(DELETED_HIDDEN);
        expect(result.current.listVersion).toBe(listVersion + 1);
        expect(result.current.recordGeneration).toBe(recordGeneration + 1);
        expect(result.current.n).toBe(n);
        expect(result.current.count).toBe(count);
        expect(result.current.record?.globalRecordId ?? null).toBe(opened);
        expect(result.current.isNew).toBe(isNew);
        expect(result.current.statusText).toBe(statusText);
        expect(result.current.loadError).toBeNull();
      },
    );
  });

  describe('the deleted toggle on as the form loads', () => {
    // Position 2 of every record is the deleted D; of the active set, B.
    it.each([{ strict: false }, { strict: true }])(
      'with initialIncludeDeleted the first count and at/2 read every record and open D (StrictMode $strict)',
      async ({ strict }) => {
        const { api, result, beforeLeave } = await mount(mixedRows(), {
          initialPosition: 2,
          initialIncludeDeleted: true,
          strict,
        });

        // StrictMode's repeated effect may send the count twice; no request reads the active set.
        const requests = api.requests();
        expect(requests.slice(-2)).toEqual([countRequest(true), atRequest(2, true)]);
        expect(requests.filter((line) => line !== countRequest(true) && line !== atRequest(2, true))).toEqual([]);
        expect(result.current.includeDeleted).toBe(true);
        expect(result.current.n).toBe(2);
        expect(result.current.count).toBe(4);
        expect(result.current.record?.globalRecordId).toBe(D.globalRecordId);
        expect(result.current.statusText).toBe('2 of 4');
        expect(beforeLeave).not.toHaveBeenCalled();

        // A later toggle change replaces the initial one, as any toggle does.
        const mark = api.mark();
        await perform(() => result.current.setIncludeDeleted(false));

        const toggled = api.requestsSince(mark);
        expect(sorted(toggled.slice(0, 2))).toEqual(sorted([countRequest(), positionRequest(D.globalRecordId)]));
        expect(toggled.slice(2)).toEqual([atRequest(2)]);
        expect(result.current.includeDeleted).toBe(false);
        expect(result.current.record?.globalRecordId).toBe(B.globalRecordId);
        expect(result.current.statusText).toBe('2 of 3');
      },
    );

    it.each([undefined, false])(
      'with initialIncludeDeleted %s the first load reads the active set and opens B',
      async (initialIncludeDeleted) => {
        const { api, result } = await mount(mixedRows(), { initialPosition: 2, initialIncludeDeleted });

        expect(api.requests()).toEqual([countRequest(), atRequest(2)]);
        expect(result.current.includeDeleted).toBe(false);
        expect(result.current.record?.globalRecordId).toBe(B.globalRecordId);
        expect(result.current.statusText).toBe('2 of 3');
      },
    );
  });

  describe('the notice of an initial position that cannot be opened as asked', () => {
    const PAST_THE_END = [countRequest(), atRequest(7), countRequest(), atRequest(3)];

    it.each<{
      readonly name: string;
      readonly rows: readonly Row[];
      readonly initialPosition?: number;
      readonly initialPositionText?: string;
      readonly requests: readonly string[];
      readonly opened: string | null;
      readonly statusText: string;
      readonly notice: string | null;
    }>([
      {
        name: 'text that is not a record number opens the first record',
        rows: activeRows(3),
        initialPositionText: 'abc',
        requests: [countRequest(), atRequest(1)],
        opened: idOf(1),
        statusText: '1 of 3',
        notice: "'abc' is not a record number, so the first record is shown.",
      },
      {
        name: 'blank text opens the first record',
        rows: activeRows(3),
        initialPositionText: ' ',
        requests: [countRequest(), atRequest(1)],
        opened: idOf(1),
        statusText: '1 of 3',
        notice: 'The record number is blank, so the first record is shown.',
      },
      {
        name: 'a position that is not one, with no text, is named as given',
        rows: activeRows(3),
        initialPosition: 0,
        requests: [countRequest(), atRequest(1)],
        opened: idOf(1),
        statusText: '1 of 3',
        notice: "'0' is not a record number, so the first record is shown.",
      },
      {
        name: 'text that is not a record number opens New on an empty form',
        rows: [],
        initialPositionText: 'abc',
        requests: [countRequest()],
        opened: null,
        statusText: 'New record',
        notice: "'abc' is not a record number, and there are no records to show, so a new record is shown.",
      },
      {
        name: 'a position past the end opens the last record after the 404',
        rows: activeRows(3),
        initialPosition: 7,
        initialPositionText: '7',
        requests: PAST_THE_END,
        opened: idOf(3),
        statusText: '3 of 3',
        notice: 'This record no longer exists. There is no record 7; showing the last record, 3.',
      },
      {
        name: 'a position past the end, with no text, is named as given',
        rows: activeRows(3),
        initialPosition: 7,
        requests: PAST_THE_END,
        opened: idOf(3),
        statusText: '3 of 3',
        notice: 'This record no longer exists. There is no record 7; showing the last record, 3.',
      },
      {
        name: 'a capped position is named as written',
        rows: activeRows(3),
        initialPosition: 2_147_483_647,
        initialPositionText: '99999999999',
        requests: [countRequest(), atRequest(2_147_483_647), countRequest(), atRequest(3)],
        opened: idOf(3),
        statusText: '3 of 3',
        notice: 'This record no longer exists. There is no record 99999999999; showing the last record, 3.',
      },
      {
        name: 'a position on an empty form opens New, with no at read',
        rows: [],
        initialPosition: 3,
        initialPositionText: '3',
        requests: [countRequest()],
        opened: null,
        statusText: 'New record',
        notice: 'There is no record 3; there are no records to show, so a new record is shown.',
      },
      {
        name: 'a position the set holds opens with no notice',
        rows: activeRows(3),
        initialPosition: 2,
        initialPositionText: '2',
        requests: [countRequest(), atRequest(2)],
        opened: idOf(2),
        statusText: '2 of 3',
        notice: null,
      },
      {
        name: 'neither position nor text opens the first record with no notice',
        rows: activeRows(3),
        requests: [countRequest(), atRequest(1)],
        opened: idOf(1),
        statusText: '1 of 3',
        notice: null,
      },
    ])('$name', async ({ rows, initialPosition, initialPositionText, requests, opened, statusText, notice }) => {
      const { api, result, beforeLeave } = await mount(rows, { initialPosition, initialPositionText });

      expect(api.requests()).toEqual(requests);
      expect(result.current.notice).toBe(notice);
      expect(result.current.record?.globalRecordId ?? null).toBe(opened);
      expect(result.current.isNew).toBe(opened === null);
      expect(result.current.statusText).toBe(statusText);
      expect(result.current.loadError).toBeNull();
      expect(beforeLeave).not.toHaveBeenCalled();
    });

    // The count said record 2 existed, so a 404 on at/2 means it went away after N was read.
    it.each<{
      readonly name: string;
      readonly rows: readonly Row[];
      /** Runs while at/2 is held, before it answers. */
      readonly whileAt: (api: FakeApi) => void;
      /** Runs while the recovery's count is held, before it answers. */
      readonly whileCount: (api: FakeApi) => void;
      readonly requests: readonly string[];
      readonly opened: string | null;
      readonly statusText: string;
      readonly notice: string;
    }>([
      {
        name: 'the set is empty by the recovery, so New opens',
        rows: activeRows(2),
        whileAt: (api) => {
          api.remove(idOf(1));
          api.remove(idOf(2));
        },
        whileCount: () => undefined,
        requests: [countRequest(), atRequest(2), countRequest()],
        opened: null,
        statusText: 'New record',
        notice: 'This record no longer exists. There is no record 2; there are no records to show, so a new record is shown.',
      },
      {
        name: 'the set has a record 2 again by the recovery, which opens with the 404 notice alone',
        rows: activeRows(3),
        whileAt: (api) => {
          api.remove(idOf(2));
          api.remove(idOf(3));
        },
        whileCount: (api) => {
          api.add(row(4));
          api.add(row(5));
        },
        requests: [countRequest(), atRequest(2), countRequest(), atRequest(2)],
        opened: idOf(4),
        statusText: '2 of 3',
        notice: 'This record no longer exists.',
      },
    ])(
      'a 404 on the initial at/2 that N counted says no "last record" when $name',
      async ({ rows, whileAt, whileCount, requests, opened, statusText, notice }) => {
        const api = createFakeApi(rows);
        const atGate = api.hold(`${RECORDS_PATH}/at/2`);
        const client = createApiClient({ baseUrl: 'http://localhost', fetch: api.fetch });
        const { result } = renderHook(() =>
          useRecordNavigation({
            client,
            viewId: VIEW_ID,
            initialPosition: 2,
            initialPositionText: '2',
            beforeLeave: () => Promise.resolve(true),
          }),
        );
        await act(async () => {
          await atGate.reached;
        });
        const countGate = api.hold(`${RECORDS_PATH}/count`);
        whileAt(api);

        await act(async () => {
          atGate.release();
          await countGate.reached;
        });
        whileCount(api);
        act(() => {
          countGate.release();
        });
        await settled(result);

        expect(api.requests()).toEqual(requests);
        expect(result.current.notice).toBe(notice);
        expect(result.current.record?.globalRecordId ?? null).toBe(opened);
        expect(result.current.statusText).toBe(statusText);
        expect(result.current.loadError).toBeNull();
      },
    );
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

    // The read by id answers 200 while the position endpoint answers 404, so only the
    // position 404 can start the recovery; it is not read as a `null` position either.
    const positionOnlyMisses: readonly {
      readonly name: string;
      readonly rows: readonly Row[];
      readonly initialPosition: number;
      /** Rows soft-deleted after the load, so they leave the active set. */
      readonly deleted: readonly string[];
      /** The requests after the open's three parallel reads. */
      readonly recovery: readonly string[];
      readonly n: number | null;
      readonly count: number;
      readonly opened: string | null;
      readonly isNew: boolean;
      readonly statusText: string;
    }[] = [
      {
        name: 'opens at/{min(n, count)} of a set that still counts the record',
        rows: activeRows(5),
        initialPosition: 3,
        deleted: [],
        recovery: [countRequest(), atRequest(3)],
        n: 3,
        count: 5,
        opened: idOf(3),
        isNew: false,
        statusText: '3 of 5',
      },
      {
        name: 'shows the empty state when the count is 0',
        rows: activeRows(2),
        initialPosition: 1,
        deleted: [idOf(1), idOf(2)],
        recovery: [countRequest()],
        n: null,
        count: 0,
        opened: null,
        isNew: true,
        statusText: 'New record',
      },
    ];

    it.each(positionOnlyMisses)(
      'openById with a 404 from the position endpoint alone shows the notice, reloads the list and N, and $name',
      async ({ rows, initialPosition, deleted, recovery, n, count, opened, isNew, statusText }) => {
        const { api, result } = await mount(rows, { initialPosition });
        const { listVersion, recordGeneration } = result.current;
        const target = idOf(2);
        for (const id of deleted) {
          api.setStatus(id, DELETED);
        }
        api.failPosition(target);
        const mark = api.mark();

        await perform(() => result.current.openById(target));

        const requests = api.requestsSince(mark);
        expect(sorted(requests.slice(0, 3))).toEqual(
          sorted([byIdRequest(target), positionRequest(target), countRequest()]),
        );
        expect(requests.slice(3)).toEqual(recovery);
        expect(result.current.notice).toMatch(RECORD_GONE);
        expect(result.current.listVersion).toBe(listVersion + 1);
        expect(result.current.recordGeneration).toBe(recordGeneration + 1);
        expect(result.current.n).toBe(n);
        expect(result.current.count).toBe(count);
        expect(result.current.record?.globalRecordId ?? null).toBe(opened);
        expect(result.current.isNew).toBe(isNew);
        expect(result.current.statusText).toBe(statusText);
        expect(result.current.loadError).toBeNull();
      },
    );

    // Record 3 of 5 is open and active, so switching the toggle on keeps it open unless it
    // is gone: its position read answers 404, and the nearest record would replace it.
    it.each([
      {
        name: 'refused',
        left: false,
        recovery: [] as string[],
        opened: idOf(3),
        count: 5,
        includeDeleted: false,
        installed: 0,
        notice: null as RegExp | null,
        listChanged: 0,
        statusText: '3 of 5',
      },
      {
        name: 'succeeds',
        left: true,
        recovery: [countRequest(true), atRequest(3, true)],
        // Position 3 of the remaining 1, 2, 4, 5.
        opened: idOf(4),
        count: 4,
        includeDeleted: true,
        installed: 1,
        notice: RECORD_GONE,
        listChanged: 1,
        statusText: '3 of 4',
      },
    ])(
      'a toggle change that finds the open record gone leaves it before opening the nearest record (leave $name)',
      async ({ left, recovery, opened, count, includeDeleted, installed, notice, listChanged, statusText }) => {
        const { api, result, beforeLeave } = await mount(activeRows(5), { initialPosition: 3 });
        const { recordGeneration, listVersion } = result.current;
        api.remove(idOf(3));
        beforeLeave.mockImplementation(() => {
          api.note('beforeLeave');
          return Promise.resolve(left);
        });
        const mark = api.mark();

        await perform(() => result.current.setIncludeDeleted(true));

        // The toggle's own reads, then the leave, and only after a successful leave the recovery.
        const log = api.logSince(mark);
        expect(sorted(log.slice(0, 2))).toEqual(sorted([countRequest(true), positionRequest(idOf(3), true)]));
        expect(log.slice(2)).toEqual(['beforeLeave', ...recovery]);
        expect(beforeLeave).toHaveBeenCalledTimes(1);
        expect(result.current.record?.globalRecordId).toBe(opened);
        expect(result.current.n).toBe(3);
        expect(result.current.count).toBe(count);
        expect(result.current.includeDeleted).toBe(includeDeleted);
        expect(result.current.recordGeneration).toBe(recordGeneration + installed);
        expect(result.current.listVersion).toBe(listVersion + listChanged);
        if (notice === null) {
          expect(result.current.notice).toBeNull();
        } else {
          expect(result.current.notice).toMatch(notice);
        }
        expect(result.current.statusText).toBe(statusText);
        expect(result.current.loadError).toBeNull();
        expect(result.current.loading).toBe(false);
        expect(result.current.transitioning).toBe(false);
      },
    );

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

  // The editor's onConflict: a save found the open record deleted elsewhere (409), and the
  // record stays open, read-only, at the n and N shown before.
  describe('afterConflict', () => {
    const conflicts: readonly {
      readonly name: string;
      readonly open: number;
      readonly includeDeleted: boolean;
      /** Presses that send nothing before `press`. */
      readonly idle: readonly NamedAction[];
      readonly press: NamedAction;
      readonly reads: readonly string[];
      readonly opened: number;
      readonly status: string;
      readonly canNext: boolean;
    }[] = [
      {
        // Next opens the record that followed the deleted one, which now stands at n.
        name: 'middle record, active set',
        open: 3,
        includeDeleted: false,
        idle: [],
        press: NEXT,
        reads: [countRequest(), atRequest(3)],
        opened: 4,
        status: '3 of 4',
        canNext: true,
      },
      {
        // Next has no record to go to, and Previous opens the new last record.
        name: 'last record, active set',
        open: 5,
        includeDeleted: false,
        idle: [NEXT],
        press: PREVIOUS,
        reads: [countRequest(), atRequest(4)],
        opened: 4,
        status: '4 of 4',
        canNext: false,
      },
      {
        // The deleted record keeps its place in the set, so Next opens n + 1.
        name: 'middle record, deleted shown',
        open: 3,
        includeDeleted: true,
        idle: [],
        press: NEXT,
        reads: [countRequest(true), atRequest(4, true)],
        opened: 4,
        status: '4 of 5',
        canNext: true,
      },
    ];

    it.each(conflicts)(
      '$name: sends nothing and reloads the grid, then $press.name re-reads N and shows $status',
      async ({ open, includeDeleted, idle, press, reads, opened, status, canNext }) => {
        const { api, result } = await mount(activeRows(5), { initialPosition: open });
        await perform(() => result.current.setIncludeDeleted(includeDeleted));
        const { listVersion, recordGeneration } = result.current;
        api.setStatus(idOf(open), DELETED);
        const mark = api.mark();

        act(() => {
          result.current.afterConflict();
        });

        expect(api.requestsSince(mark)).toEqual([]);
        expect(result.current.listVersion).toBe(listVersion + 1);
        expect(result.current.recordGeneration).toBe(recordGeneration);
        expect(result.current.record?.globalRecordId).toBe(idOf(open));
        expect(result.current.statusText).toBe(`${open} of 5`);
        expect(result.current.canNext).toBe(open < 5);
        for (const action of idle) {
          await perform(() => action.run(result.current));
        }
        expect(api.requestsSince(mark)).toEqual([]);

        await perform(() => press.run(result.current));

        expect(api.logSince(mark)).toEqual(['beforeLeave', ...reads]);
        expect(result.current.record?.globalRecordId).toBe(idOf(opened));
        expect(result.current.statusText).toBe(status);
        expect(result.current.canNext).toBe(canNext);
        expect(result.current.notice).toBeNull();
        expect(result.current.loadError).toBeNull();
      },
    );

    it('makes New re-read N', async () => {
      const { api, result } = await mount(activeRows(5), { initialPosition: 3 });
      api.setStatus(idOf(3), DELETED);
      act(() => {
        result.current.afterConflict();
      });
      const mark = api.mark();

      await perform(() => result.current.newRecord());
      await settled(result);

      expect(api.logSince(mark)).toEqual(['beforeLeave', countRequest()]);
      expect(result.current.isNew).toBe(true);
      expect(result.current.count).toBe(4);
      expect(result.current.statusText).toBe('New record');
    });

    it.each([
      { open: 3, log: ['beforeLeave', countRequest(), atRequest(3), 'beforeLeave', countRequest(), atRequest(4)] },
      // The slot holds the last record of the set without the deleted one, so the second Next has nowhere to go.
      { open: 4, log: ['beforeLeave', countRequest(), atRequest(4)] },
    ])(
      'a second Next while the first from slot $open is pending counts in the set without the deleted record',
      async ({ open, log }) => {
        const { api, result } = await mount(activeRows(5), { initialPosition: open });
        api.setStatus(idOf(open), DELETED);
        act(() => {
          result.current.afterConflict();
        });
        const mark = api.mark();
        const gate = api.hold(`${RECORDS_PATH}/at/${open}`);
        let pending: Promise<void> = Promise.resolve();

        await act(async () => {
          pending = result.current.next();
          await gate.reached;
        });
        await perform(() => result.current.next());
        await act(async () => {
          gate.release();
          await pending;
        });

        // Both land on the last record, with no false "no longer exists" from an at/5 past the end.
        expect(api.logSince(mark)).toEqual(log);
        expect(result.current.record?.globalRecordId).toBe(idOf(5));
        expect(result.current.statusText).toBe('4 of 4');
        expect(result.current.notice).toBeNull();
      },
    );
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

  // The client's request deadline ends a read that never completes as a failure with no
  // response, which Retry may resend (0.3.4.2); nothing resends it by itself. Real timers.
  describe('a read that never completes', () => {
    const DEADLINE_MS = 100;
    // A timer fires no earlier than its delay, up to the millisecond the event loop rounds to.
    const TIMER_SLACK_MS = 5;

    const stalledReads: readonly {
      readonly name: string;
      readonly pathname: string;
      readonly start: Action;
      /** The position the Retry opens. */
      readonly opened: number;
    }[] = [
      { name: 'the at/{n} read of a move', pathname: `${RECORDS_PATH}/at/2`, start: NEXT.run, opened: 2 },
      {
        name: 'the record read of an open by id',
        pathname: `${RECORDS_PATH}/${idOf(4)}`,
        start: (navigation) => navigation.openById(idOf(4)),
        opened: 4,
      },
    ];

    it.each(stalledReads)(
      'shows $name unanswered at the deadline as a connection failure with Retry, and Retry alone sends it again and opens it',
      async ({ pathname, start, opened }) => {
        const { api, result } = await mount(activeRows(5), { deadlineMs: DEADLINE_MS });
        const { recordGeneration } = result.current;
        // Never released, so the read is never answered.
        api.hold(pathname);
        const sentTimes = (): number => api.calls.filter((call) => call.url.pathname === pathname).length;
        const started = performance.now();

        await perform(() => start(result.current));

        expect(performance.now() - started).toBeGreaterThanOrEqual(DEADLINE_MS - TIMER_SLACK_MS);
        const failure = result.current.loadError;
        expect(failure?.kind).toBe('network');
        expect(failure?.status).toBeNull();
        expect(failure !== null && isSafeToResend(failure, 'GET')).toBe(true);
        expect(result.current.loading).toBe(false);
        expect(result.current.transitioning).toBe(false);
        expect(result.current.record?.globalRecordId).toBe(idOf(1));
        expect(result.current.recordGeneration).toBe(recordGeneration);
        expect(result.current.n).toBe(1);

        // Several deadlines later the read has still been sent once.
        await act(async () => {
          await new Promise((resolve) => {
            setTimeout(resolve, DEADLINE_MS * 3);
          });
        });
        expect(sentTimes()).toBe(1);

        await perform(() => result.current.retryLoad());

        expect(sentTimes()).toBe(2);
        expect(result.current.loadError).toBeNull();
        expect(result.current.record?.globalRecordId).toBe(idOf(opened));
        expect(result.current.n).toBe(opened);
        expect(result.current.statusText).toBe(`${opened} of 5`);
      },
    );
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

// Record transitions: the `transitioning` flag that freezes the editor's draft from the
// start of every record-changing operation until it ends, Retry through the leave guard,
// and reads that are cancelled when superseded or unmounted.
describe('record transitions (0.3.6, 0.3.4.2)', () => {
  /** What fetch rejects with once its request was aborted. */
  function abortError(): DOMException {
    return new DOMException('The operation was aborted.', 'AbortError');
  }

  interface Sent {
    readonly line: string;
    readonly signal: AbortSignal;
  }

  /** The in-memory API behind a fetch that keeps each request's signal and can fail or cancel one request. */
  interface Wire {
    readonly api: FakeApi;
    readonly client: ReturnType<typeof createApiClient>;
    /** Every request with its signal, in arrival order. */
    readonly sent: readonly Sent[];
    /** The next request to `pathname` reaches the fake, then answers 503. */
    readonly failNext: (pathname: string) => void;
    /** The next request to `pathname` reaches the fake, then rejects with an AbortError, as a cancelled fetch does. */
    readonly abortNext: (pathname: string) => void;
    /** Lines of the requests that rejected with an AbortError of their own while their signal was still live. */
    readonly selfAborted: readonly string[];
    /**
     * The next request to `pathname` keeps waiting for its answer after its signal aborts,
     * as a fetch that does not honour its signal, so only a load that stops waiting for it
     * settles before it answers.
     */
    readonly ignoreAbort: (pathname: string) => void;
  }

  function wire(rows: readonly Row[]): Wire {
    const api = createFakeApi(rows);
    const sent: Sent[] = [];
    const selfAborted: string[] = [];
    const outcomes = new Map<string, 'fail' | 'abort'>();
    const deaf = new Set<string>();
    const fetchThrough = (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
      const request = input instanceof Request ? input : new Request(input, init);
      const url = new URL(request.url);
      const line = `${request.method} ${url.pathname}${url.search}`;
      sent.push({ line, signal: request.signal });
      const outcome = outcomes.get(url.pathname);
      outcomes.delete(url.pathname);
      const honoursAbort = !deaf.delete(url.pathname);
      return new Promise<Response>((resolve, reject) => {
        // A real fetch rejects as soon as its request is aborted, answered or still held.
        if (honoursAbort) {
          request.signal.addEventListener(
            'abort',
            () => {
              reject(abortError());
            },
            { once: true },
          );
        }
        api.fetch(request).then((response) => {
          if (outcome === 'abort') {
            if (!request.signal.aborted) {
              selfAborted.push(line);
            }
            reject(abortError());
          } else {
            resolve(outcome === 'fail' ? problemResponse(503, 'Service Unavailable') : response);
          }
        }, reject);
      });
    };
    return {
      api,
      client: createApiClient({ baseUrl: 'http://localhost', fetch: fetchThrough }),
      sent,
      failNext: (pathname) => {
        outcomes.set(pathname, 'fail');
      },
      abortNext: (pathname) => {
        outcomes.set(pathname, 'abort');
      },
      selfAborted,
      ignoreAbort: (pathname) => {
        deaf.add(pathname);
      },
    };
  }

  interface MountedWire extends Wire {
    readonly result: Current;
    readonly beforeLeave: Mock<() => Promise<boolean>>;
    readonly unmount: () => void;
  }

  /** Notes `beforeLeave` in the log and resolves `left`. */
  function leaving(api: FakeApi, left: boolean): () => Promise<boolean> {
    return () => {
      api.note('beforeLeave');
      return Promise.resolve(left);
    };
  }

  async function mountWire(rows: readonly Row[], initialPosition?: number): Promise<MountedWire> {
    const connection = wire(rows);
    const beforeLeave = vi.fn(leaving(connection.api, true));
    const { result, unmount } = renderHook(() =>
      useRecordNavigation({ client: connection.client, viewId: VIEW_ID, initialPosition, beforeLeave }),
    );
    await settled(result);
    return { ...connection, result, beforeLeave, unmount };
  }

  /**
   * The latest request sent as `line`. A load's signal is aborted whenever a newer load
   * supersedes it, its finished requests included, so only the latest one tells.
   */
  function sentTo(connection: Wire, line: string): Sent {
    const found = [...connection.sent].reverse().find((entry) => entry.line === line);
    if (found === undefined) {
      throw new Error(`no request ${line} was sent`);
    }
    return found;
  }

  it('Retry of a failed initial load keeps initialIncludeDeleted, so the repeated count and at/2 read every record', async () => {
    // Record 2 is deleted: only the set of every record has a position 2.
    const connection = wire([row(1), row(2, { recStatus: DELETED })]);
    connection.failNext(`${RECORDS_PATH}/count`);
    const beforeLeave = vi.fn(leaving(connection.api, true));
    const { result } = renderHook(() =>
      useRecordNavigation({
        client: connection.client,
        viewId: VIEW_ID,
        initialPosition: 2,
        initialIncludeDeleted: true,
        beforeLeave,
      }),
    );
    await settled(result);
    expect(connection.api.requests()).toEqual([countRequest(true)]);
    expect(result.current.loadError?.kind).toBe('unavailable');
    expect(result.current.includeDeleted).toBe(true);
    const mark = connection.api.mark();

    await perform(() => result.current.retryLoad());

    expect(connection.api.requestsSince(mark)).toEqual([countRequest(true), atRequest(2, true)]);
    expect(result.current.loadError).toBeNull();
    expect(result.current.includeDeleted).toBe(true);
    expect(result.current.record?.globalRecordId).toBe(idOf(2));
    expect(result.current.statusText).toBe('2 of 2');
    expect(beforeLeave).not.toHaveBeenCalled();
  });

  const RECORD_CHANGES: readonly NamedAction[] = [
    FIRST,
    PREVIOUS,
    NEXT,
    LAST,
    goTo(5),
    { name: 'newRecord', run: (navigation) => navigation.newRecord() },
    { name: 'openById', run: (navigation) => navigation.openById(idOf(4)) },
  ];

  describe('transitioning', () => {
    it.each(RECORD_CHANGES)(
      '$name sets it before beforeLeave resolves, and clears it with no request when beforeLeave cancels',
      async ({ run }) => {
        const leave = deferred<boolean>();
        const { api, result, beforeLeave } = await mount(activeRows(5), {
          initialPosition: 3,
          beforeLeave: () => leave.promise,
        });
        const { record, recordGeneration } = result.current;
        expect(result.current.transitioning).toBe(false);
        const mark = api.mark();
        let changing: Promise<unknown> = Promise.resolve();

        act(() => {
          changing = run(result.current);
        });

        expect(beforeLeave).toHaveBeenCalledTimes(1);
        expect(result.current.transitioning).toBe(true);
        expect(result.current.loading).toBe(false);

        await act(async () => {
          leave.resolve(false);
          await changing;
        });

        expect(result.current.transitioning).toBe(false);
        expect(result.current.loading).toBe(false);
        expect(api.requestsSince(mark)).toEqual([]);
        expect(result.current.record).toBe(record);
        expect(result.current.recordGeneration).toBe(recordGeneration);
      },
    );

    it.each([
      { name: 'next', run: NEXT.run, held: `${RECORDS_PATH}/at/4`, n: 4 },
      {
        name: 'openById',
        run: (navigation: RecordNavigation) => navigation.openById(idOf(5)),
        held: `${RECORDS_PATH}/${idOf(5)}/position`,
        n: 5,
      },
    ])('$name stays transitioning through beforeLeave and a held GET until the record is installed', async ({ run, held, n }) => {
      const leave = deferred<boolean>();
      const { api, result } = await mount(activeRows(5), { initialPosition: 3, beforeLeave: () => leave.promise });
      const gate = api.hold(held);
      let changing: Promise<unknown> = Promise.resolve();

      act(() => {
        changing = run(result.current);
      });
      expect(result.current.transitioning).toBe(true);

      await act(async () => {
        leave.resolve(true);
        await gate.reached;
      });
      expect(result.current.transitioning).toBe(true);
      expect(result.current.loading).toBe(true);
      expect(result.current.n).toBe(3);

      await act(async () => {
        gate.release();
        await changing;
      });
      expect(result.current.transitioning).toBe(false);
      expect(result.current.loading).toBe(false);
      expect(result.current.n).toBe(n);
      expect(result.current.record?.globalRecordId).toBe(idOf(n));
    });

    it('afterDelete sets it without calling beforeLeave, and clears it once the nearest record is open', async () => {
      const { api, result, beforeLeave } = await mount(activeRows(3), { initialPosition: 2 });
      api.setStatus(idOf(2), DELETED);
      const gate = api.hold(`${RECORDS_PATH}/at/2`);

      await act(async () => {
        result.current.afterDelete();
        await gate.reached;
      });
      expect(result.current.transitioning).toBe(true);
      expect(beforeLeave).not.toHaveBeenCalled();

      await act(async () => {
        gate.release();
        await flush();
      });
      await settled(result);
      expect(result.current.transitioning).toBe(false);
      expect(result.current.record?.globalRecordId).toBe(idOf(3));
    });
  });

  // Previous and Next count from the target of the move still running, not from the
  // record still shown, so two quick presses move two records. They are available exactly
  // when a press would move, counted the same way, while the status stays on the record
  // shown until the move settles.
  describe('a relative move while another is pending', () => {
    const BOTH = { previous: true, next: true };
    it.each([
      {
        from: 2,
        first: NEXT,
        second: NEXT,
        held: 3,
        available: BOTH,
        log: ['beforeLeave', atRequest(3), 'beforeLeave', atRequest(4)],
        to: 4,
      },
      {
        from: 4,
        first: PREVIOUS,
        second: PREVIOUS,
        held: 3,
        available: BOTH,
        log: ['beforeLeave', atRequest(3), 'beforeLeave', atRequest(2)],
        to: 2,
      },
      {
        from: 2,
        first: NEXT,
        second: PREVIOUS,
        held: 3,
        available: BOTH,
        log: ['beforeLeave', atRequest(3), 'beforeLeave', atRequest(2)],
        to: 2,
      },
      // Record 1 has no previous record, but the pending record 2 does.
      {
        from: 1,
        first: NEXT,
        second: PREVIOUS,
        held: 2,
        available: BOTH,
        log: ['beforeLeave', atRequest(2), 'beforeLeave', atRequest(1)],
        to: 1,
      },
      {
        from: 1,
        first: goTo(3),
        second: NEXT,
        held: 3,
        available: BOTH,
        log: ['beforeLeave', atRequest(3), 'beforeLeave', atRequest(4)],
        to: 4,
      },
      // Record 4 has a next record, but the pending record 5 is the last, so Next does nothing.
      {
        from: 4,
        first: NEXT,
        second: NEXT,
        held: 5,
        available: { previous: true, next: false },
        log: ['beforeLeave', atRequest(5)],
        to: 5,
      },
      // Last lands on the end of the set, so Next from it does nothing and Last goes on.
      {
        from: 2,
        first: LAST,
        second: NEXT,
        held: 5,
        available: { previous: true, next: false },
        log: ['beforeLeave', atRequest(5)],
        to: 5,
      },
    ])(
      '$second.name pressed while $first.name from $from waits for at/$held counts from there: the view ends at $to',
      async ({ from, first, second, held, available, log, to }) => {
        const { api, result } = await mount(activeRows(5), { initialPosition: from });
        const mark = api.mark();
        const gate = api.hold(`${RECORDS_PATH}/at/${held}`);
        let pending: Promise<unknown> = Promise.resolve();

        await act(async () => {
          pending = first.run(result.current);
          await gate.reached;
        });
        expect(result.current.n).toBe(from);
        expect(result.current.statusText).toBe(`${from} of 5`);
        expect(result.current.canPrevious).toBe(available.previous);
        expect(result.current.canNext).toBe(available.next);

        await perform(() => second.run(result.current));
        // The first move's answer arrives last; it is shown only while that move is the latest.
        await act(async () => {
          gate.release();
          await pending;
        });

        expect(api.logSince(mark)).toEqual(log);
        expect(result.current.n).toBe(to);
        expect(result.current.record?.globalRecordId).toBe(idOf(to));
        expect(result.current.statusText).toBe(`${to} of 5`);
        expect(result.current.loading).toBe(false);
        expect(result.current.transitioning).toBe(false);
      },
    );

    // Records 4 and 5 are deleted elsewhere while "2 of 5" is shown, so Last finds at/5 gone,
    // and its recovery reads N (3) and opens at/3, which the case holds.
    it.each([
      { second: PREVIOUS, log: ['beforeLeave', countRequest(), atRequest(2)], to: 2, gone: false },
      // 3 is the last of the 3 records left, so Next has nowhere to go and sends nothing.
      { second: NEXT, log: [], to: 3, gone: true },
    ])(
      '$second.name pressed while the 404 recovery of Last waits for at/3 counts from that landing, in the N it read',
      async ({ second, log, to, gone }) => {
        const { api, result } = await mount(activeRows(5), { initialPosition: 2 });
        api.setStatus(idOf(4), DELETED);
        api.setStatus(idOf(5), DELETED);
        const gate = api.hold(`${RECORDS_PATH}/at/3`);
        const mark = api.mark();
        let pending: Promise<unknown> = Promise.resolve();

        await act(async () => {
          pending = LAST.run(result.current);
          await gate.reached;
        });
        expect(api.logSince(mark)).toEqual(['beforeLeave', atRequest(5), countRequest(), atRequest(3)]);
        expect(result.current.statusText).toBe('2 of 5');
        // Counted from record 3 of 3, where the recovery lands.
        expect(result.current.canPrevious).toBe(true);
        expect(result.current.canNext).toBe(false);
        const pressMark = api.mark();

        await perform(() => second.run(result.current));
        await act(async () => {
          gate.release();
          await pending;
        });

        // Never at/4 of the N the failed at/5 assumed, so no second "no longer exists".
        expect(api.logSince(pressMark)).toEqual(log);
        expect(result.current.record?.globalRecordId).toBe(idOf(to));
        expect(result.current.statusText).toBe(`${to} of 3`);
        expect(RECORD_GONE.test(result.current.notice ?? '')).toBe(gone);
        expect(result.current.loading).toBe(false);
        expect(result.current.transitioning).toBe(false);
      },
    );

    it.each([
      {
        name: 'its read failed (503)',
        end: (connection: MountedWire): void => {
          connection.failNext(`${RECORDS_PATH}/at/3`);
        },
        log: ['beforeLeave', atRequest(3), 'beforeLeave', atRequest(3)],
      },
      {
        name: 'its leave was cancelled',
        end: (connection: MountedWire): void => {
          connection.beforeLeave.mockImplementationOnce(leaving(connection.api, false));
        },
        log: ['beforeLeave', 'beforeLeave', atRequest(3)],
      },
    ])('a Next that ended because $name leaves the next press counting from the record shown', async ({ end, log }) => {
      const connection = await mountWire(activeRows(5), 2);
      const { api, result } = connection;
      end(connection);
      const mark = api.mark();

      await perform(() => result.current.next());
      expect(result.current.n).toBe(2);

      await perform(() => result.current.next());

      expect(api.logSince(mark)).toEqual(log);
      expect(result.current.n).toBe(3);
      expect(result.current.record?.globalRecordId).toBe(idOf(3));
      expect(result.current.loadError).toBeNull();
    });
  });

  describe('Retry of a load that replaces the record', () => {
    const failedLoads: readonly {
      readonly name: string;
      /** The request that answers 503 once. */
      readonly failed: string;
      readonly prepare: (api: FakeApi) => void;
      readonly start: (navigation: RecordNavigation) => Promise<unknown>;
      /** The reads the successful Retry sends after beforeLeave; parallel ones are compared sorted. */
      readonly reads: readonly string[];
      readonly parallel: boolean;
      readonly opened: number;
    }[] = [
      {
        name: 'a move',
        failed: `${RECORDS_PATH}/at/2`,
        prepare: () => undefined,
        start: NEXT.run,
        reads: [atRequest(2)],
        parallel: false,
        opened: 2,
      },
      {
        name: 'an open by id',
        failed: `${RECORDS_PATH}/${idOf(4)}`,
        prepare: () => undefined,
        start: (navigation) => navigation.openById(idOf(4)),
        reads: sorted([byIdRequest(idOf(4)), positionRequest(idOf(4)), countRequest()]),
        parallel: true,
        opened: 4,
      },
      {
        name: 'the landing after a delete',
        failed: `${RECORDS_PATH}/count`,
        // The editor's DELETE soft-deleted the open record.
        prepare: (api) => {
          api.setStatus(idOf(1), DELETED);
        },
        start: (navigation) => {
          navigation.afterDelete();
          return Promise.resolve();
        },
        reads: [countRequest(), atRequest(1)],
        parallel: false,
        opened: 2,
      },
    ];

    /** The next leave notes `beforeLeave` in the log and stays pending until the test resolves it. */
    function holdLeave(api: FakeApi, beforeLeave: Mock<() => Promise<boolean>>): Deferred<boolean> {
      const leave = deferred<boolean>();
      beforeLeave.mockImplementationOnce(() => {
        api.note('beforeLeave');
        return leave.promise;
      });
      return leave;
    }

    /** Presses Retry and returns its promise, still pending, once the press has rendered. */
    function pressRetry(result: Current): Promise<void> {
      let retrying: Promise<void> = Promise.resolve();
      act(() => {
        retrying = result.current.retryLoad();
      });
      return retrying;
    }

    /** Mounts 5 rows and fails Next, the read of record 2: the failure and its Retry are shown. */
    async function mountFailedNext(): Promise<MountedWire> {
      const connection = await mountWire(activeRows(5));
      connection.failNext(`${RECORDS_PATH}/at/2`);
      await perform(() => NEXT.run(connection.result.current));
      await settled(connection.result);
      expect(connection.result.current.loadError?.kind).toBe('unavailable');
      return connection;
    }

    it.each(failedLoads)(
      'after $name failed, Retry takes the failure off screen at once and calls beforeLeave before any read; a refused leave sends nothing and shows the failure again',
      async ({ failed, prepare, start, reads, parallel, opened }) => {
        const connection = await mountWire(activeRows(5));
        const { api, result, beforeLeave } = connection;
        prepare(api);
        connection.failNext(failed);

        await perform(() => start(result.current));
        await settled(result);

        expect(result.current.loadError?.kind).toBe('unavailable');
        expect(result.current.loading).toBe(false);
        expect(result.current.transitioning).toBe(false);
        const { record, recordGeneration } = result.current;

        // The user may have edited the record still shown since the failure, so Retry leaves
        // it first, and a leave that cannot save the edit sends nothing. The banner goes as
        // Retry is pressed, in the commit that freezes the draft, before the leave resolves.
        let leave = holdLeave(api, beforeLeave);
        let mark = api.mark();
        let retrying = pressRetry(result);

        expect(result.current.loadError).toBeNull();
        expect(result.current.transitioning).toBe(true);
        await act(async () => {
          leave.resolve(false);
          await retrying;
        });

        expect(api.logSince(mark)).toEqual(['beforeLeave']);
        expect(result.current.loadError?.kind).toBe('unavailable');
        expect(result.current.loading).toBe(false);
        expect(result.current.transitioning).toBe(false);
        expect(result.current.record).toBe(record);
        expect(result.current.recordGeneration).toBe(recordGeneration);

        leave = holdLeave(api, beforeLeave);
        mark = api.mark();
        retrying = pressRetry(result);

        expect(result.current.loadError).toBeNull();
        expect(result.current.transitioning).toBe(true);
        await act(async () => {
          leave.resolve(true);
          await retrying;
        });
        await settled(result);

        const log = api.logSince(mark);
        expect(log[0]).toBe('beforeLeave');
        expect(parallel ? sorted(log.slice(1)) : log.slice(1)).toEqual(reads);
        expect(result.current.loadError).toBeNull();
        expect(result.current.transitioning).toBe(false);
        expect(result.current.record?.globalRecordId).toBe(idOf(opened));
      },
    );

    it.each([{ left: false }, { left: true }])(
      'a New pressed while the leave of a Retry is pending is installed, and the failure does not come back (Retry leave resolves $left)',
      async ({ left }) => {
        const { api, result, beforeLeave } = await mountFailedNext();
        const { recordGeneration } = result.current;
        const leave = holdLeave(api, beforeLeave);
        const mark = api.mark();
        const retrying = pressRetry(result);
        expect(result.current.loadError).toBeNull();

        // New's own leave resolves true at once, so New overtakes the Retry.
        await perform(() => result.current.newRecord());
        expect(result.current.isNew).toBe(true);

        await act(async () => {
          leave.resolve(left);
          await retrying;
        });
        await act(flush);

        expect(api.logSince(mark)).toEqual(['beforeLeave', 'beforeLeave']);
        expect(result.current.loadError).toBeNull();
        expect(result.current.isNew).toBe(true);
        expect(result.current.record).toBeNull();
        expect(result.current.statusText).toBe('New record');
        expect(result.current.recordGeneration).toBe(recordGeneration + 1);
        expect(result.current.transitioning).toBe(false);
        expect(result.current.loading).toBe(false);
      },
    );

    it("a Retry whose leave saves the record and then resolves false keeps the failure, re-reading only the saved record's n and N", async () => {
      const { api, result, beforeLeave } = await mountFailedNext();
      const { recordGeneration } = result.current;
      // Another session adds a record while record 1 is edited, so N grows.
      api.add(row(6));
      // The editor's leave() saves the edit and reports it, then cannot save a later edit.
      beforeLeave.mockImplementationOnce(() => {
        api.note('beforeLeave');
        result.current.afterSave(api.dto(idOf(1)));
        return Promise.resolve(false);
      });
      const mark = api.mark();

      await perform(() => result.current.retryLoad());
      await act(flush);

      const log = api.logSince(mark);
      expect(log[0]).toBe('beforeLeave');
      expect(sorted(log.slice(1))).toEqual(sorted([countRequest(), positionRequest(idOf(1))]));
      expect(result.current.loadError?.kind).toBe('unavailable');
      expect(result.current.loading).toBe(false);
      expect(result.current.transitioning).toBe(false);
      expect(result.current.record?.globalRecordId).toBe(idOf(1));
      expect(result.current.recordGeneration).toBe(recordGeneration);
      expect(result.current.statusText).toBe('1 of 6');

      // Retry still repeats the move; N is fresh from the refresh, so only its target is read.
      const retryMark = api.mark();
      await perform(() => result.current.retryLoad());
      await settled(result);

      expect(api.logSince(retryMark)).toEqual(['beforeLeave', atRequest(2)]);
      expect(result.current.loadError).toBeNull();
      expect(result.current.record?.globalRecordId).toBe(idOf(2));
      expect(result.current.statusText).toBe('2 of 6');
    });

    it.each([
      { left: false, expected: [] as string[] },
      { left: true, expected: [countRequest(), atRequest(2)] },
    ])(
      'the after-save refresh that finds the record gone opens the nearest one only after beforeLeave (left: $left)',
      async ({ left, expected }) => {
        const { api, result, beforeLeave } = await mount(activeRows(3), { initialPosition: 2 });
        const saved = api.dto(idOf(2));
        api.remove(idOf(2));
        beforeLeave.mockImplementation(leaving(api, left));
        const mark = api.mark();

        act(() => {
          result.current.afterSave(saved);
        });
        await settled(result);
        await act(flush);

        const log = api.logSince(mark);
        expect(sorted(log.slice(0, 2))).toEqual(sorted([countRequest(), positionRequest(idOf(2))]));
        expect(log.slice(2)).toEqual(['beforeLeave', ...expected]);
        expect(result.current.transitioning).toBe(false);
        expect(result.current.loading).toBe(false);
        if (left) {
          expect(result.current.notice).toMatch(RECORD_GONE);
        } else {
          expect(result.current.notice).toBeNull();
        }
        expect(result.current.record?.globalRecordId).toBe(left ? idOf(3) : idOf(2));
      },
    );
  });

  describe('a failed landing in place of a deleted record the active set hides', () => {
    const failedLandings: readonly {
      readonly name: string;
      readonly rows: readonly Row[];
      readonly initialPosition?: number;
      /** Opens the deleted record, or deletes the one a grid row still lists. */
      readonly prepare: (connection: MountedWire) => Promise<void>;
      readonly start: (navigation: RecordNavigation) => Promise<unknown>;
      /** The active-set position the landing reads, which answers 503 once. */
      readonly position: number;
      /** The list version change before the landing: an open by id finds the list changed. */
      readonly listChanged: number;
      /** The record the successful Retry opens. */
      readonly landed: string;
    }[] = [
      {
        name: 'an open by id of a record deleted since it was listed',
        rows: activeRows(5),
        initialPosition: 3,
        prepare: ({ api }) => {
          api.setStatus(idOf(2), DELETED);
          return Promise.resolve();
        },
        start: (navigation) => navigation.openById(idOf(2)),
        position: 3,
        listChanged: 1,
        // Position 3 of the remaining 1, 3, 4, 5.
        landed: idOf(4),
      },
      {
        name: 'switching the toggle off with a deleted record open',
        rows: [row(1), row(2, { recStatus: DELETED }), row(3)],
        prepare: async ({ result }) => {
          await perform(() => result.current.setIncludeDeleted(true));
          await perform(() => result.current.openById(idOf(2)));
        },
        start: (navigation) => navigation.setIncludeDeleted(false),
        position: 2,
        listChanged: 0,
        // Position 2 of the active 1, 3.
        landed: idOf(3),
      },
    ];

    it.each(failedLandings)(
      'when the landing after $name fails, the record stays shown with the failure and no notice, and Retry lands with the notice',
      async ({ rows, initialPosition, prepare, start, position, listChanged, landed }) => {
        const connection = await mountWire(rows, initialPosition);
        const { api, result } = connection;
        await prepare(connection);
        const { record, n, count, includeDeleted, recordGeneration, listVersion } = result.current;
        connection.failNext(`${RECORDS_PATH}/at/${position}`);
        const mark = api.mark();

        await perform(() => start(result.current));
        await settled(result);

        expect(api.requestsSince(mark).at(-1)).toBe(atRequest(position));
        expect(result.current.loadError?.kind).toBe('unavailable');
        expect(result.current.notice).toBeNull();
        expect(result.current.record).toBe(record);
        expect(result.current.n).toBe(n);
        expect(result.current.count).toBe(count);
        expect(result.current.includeDeleted).toBe(includeDeleted);
        expect(result.current.recordGeneration).toBe(recordGeneration);
        expect(result.current.listVersion).toBe(listVersion + listChanged);
        expect(result.current.transitioning).toBe(false);

        await perform(() => result.current.retryLoad());
        await settled(result);

        expect(result.current.loadError).toBeNull();
        expect(result.current.record?.globalRecordId).toBe(landed);
        expect(result.current.n).toBe(position);
        expect(result.current.includeDeleted).toBe(false);
        expect(result.current.notice).toMatch(DELETED_HIDDEN);
      },
    );
  });

  describe('cancelled reads', () => {
    const abortedReads: readonly {
      readonly name: string;
      /** The request whose fetch rejects with an AbortError of its own. */
      readonly aborted: string;
      readonly line: string;
      readonly prepare: (api: FakeApi) => void;
      readonly start: (navigation: RecordNavigation, api: FakeApi) => Promise<unknown>;
      /** Every read the load sends; parallel ones are compared sorted. */
      readonly reads: readonly string[];
      /** The load reads in parallel, so the abort of one read cancels the load's other requests. */
      readonly parallel: boolean;
    }[] = [
      {
        name: 'the at/{n} read of a move',
        aborted: `${RECORDS_PATH}/at/2`,
        line: atRequest(2),
        prepare: () => undefined,
        start: NEXT.run,
        reads: [atRequest(2)],
        parallel: false,
      },
      {
        name: 'the record read of an open by id',
        aborted: `${RECORDS_PATH}/${idOf(4)}`,
        line: byIdRequest(idOf(4)),
        prepare: () => undefined,
        start: (navigation) => navigation.openById(idOf(4)),
        reads: sorted([byIdRequest(idOf(4)), positionRequest(idOf(4)), countRequest()]),
        parallel: true,
      },
      {
        name: 'the count read of a toggle change',
        aborted: `${RECORDS_PATH}/count`,
        line: countRequest(true),
        prepare: () => undefined,
        start: (navigation) => navigation.setIncludeDeleted(true),
        reads: sorted([countRequest(true), positionRequest(idOf(1), true)]),
        parallel: true,
      },
      {
        name: 'the count read of the landing after a delete',
        aborted: `${RECORDS_PATH}/count`,
        line: countRequest(),
        prepare: (api) => {
          api.setStatus(idOf(1), DELETED);
        },
        start: (navigation) => {
          navigation.afterDelete();
          return Promise.resolve();
        },
        reads: [countRequest()],
        parallel: false,
      },
      {
        name: 'the position read of the after-save refresh',
        aborted: `${RECORDS_PATH}/${idOf(1)}/position`,
        line: positionRequest(idOf(1)),
        prepare: () => undefined,
        start: (navigation, api) => {
          navigation.afterSave(api.dto(idOf(1)));
          return Promise.resolve();
        },
        reads: sorted([countRequest(), positionRequest(idOf(1))]),
        parallel: true,
      },
    ];

    it.each(abortedReads)(
      'an abort of $name settles the current load idle: no error, no banner, not loading',
      async ({ aborted, line, prepare, start, reads, parallel }) => {
        const connection = await mountWire(activeRows(5));
        const { api, result } = connection;
        prepare(api);
        connection.abortNext(aborted);
        const { recordGeneration, n, count } = result.current;
        const sentBefore = connection.sent.length;

        await perform(() => start(result.current, api));
        await act(flush);

        // The fetch rejected by itself while its signal was live, and nothing but this load's
        // own reads was sent: no newer load superseded it, so this load was current.
        expect(connection.selfAborted).toEqual([line]);
        const lines = connection.sent.slice(sentBefore).map((entry) => entry.line);
        expect(parallel ? sorted(lines) : lines).toEqual(reads);
        // The read is left as it was: a request's signal follows the load's only while the request
        // is in flight, so a parallel load's cancelling of its other reads, once one is aborted, is
        // seen on a read still held (the parallelLoads cases below).
        expect(sentTo(connection, line).signal.aborted).toBe(false);
        expect(result.current.loading).toBe(false);
        expect(result.current.loadError).toBeNull();
        expect(result.current.notice).toBeNull();
        expect(result.current.transitioning).toBe(false);
        expect(result.current.record?.globalRecordId).toBe(idOf(1));
        expect(result.current.recordGeneration).toBe(recordGeneration);
        expect(result.current.n).toBe(n);
        expect(result.current.count).toBe(count);
        expect(result.current.includeDeleted).toBe(false);
      },
    );

    // The loads that read in parallel: one read of each aborts by itself, or is the one
    // held at the fake while the load is superseded, and a sibling read stays held.
    const parallelLoads: readonly {
      readonly name: string;
      readonly start: (navigation: RecordNavigation, api: FakeApi) => Promise<unknown>;
      readonly aborted: { readonly pathname: string; readonly line: string };
      readonly held: { readonly pathname: string; readonly line: string };
    }[] = [
      {
        name: 'an open by id',
        start: (navigation) => navigation.openById(idOf(4)),
        aborted: { pathname: `${RECORDS_PATH}/${idOf(4)}`, line: byIdRequest(idOf(4)) },
        held: { pathname: `${RECORDS_PATH}/${idOf(4)}/position`, line: positionRequest(idOf(4)) },
      },
      {
        name: 'a toggle change',
        start: (navigation) => navigation.setIncludeDeleted(true),
        aborted: { pathname: `${RECORDS_PATH}/count`, line: countRequest(true) },
        held: { pathname: `${RECORDS_PATH}/${idOf(1)}/position`, line: positionRequest(idOf(1), true) },
      },
      {
        name: 'the after-save refresh',
        start: (navigation, api) => {
          navigation.afterSave(api.dto(idOf(1)));
          return Promise.resolve();
        },
        aborted: { pathname: `${RECORDS_PATH}/${idOf(1)}/position`, line: positionRequest(idOf(1)) },
        held: { pathname: `${RECORDS_PATH}/count`, line: countRequest() },
      },
    ];

    it.each(parallelLoads)(
      'an abort of one read of $name settles it idle at once and cancels the held read it no longer awaits',
      async ({ start, aborted, held }) => {
        const connection = await mountWire(activeRows(5));
        const { api, result } = connection;
        const { recordGeneration, n, count } = result.current;
        const gate = api.hold(held.pathname);
        connection.ignoreAbort(held.pathname);
        connection.abortNext(aborted.pathname);
        let running: Promise<unknown> = Promise.resolve();
        const unchanged = (): void => {
          expect(result.current.loading).toBe(false);
          expect(result.current.transitioning).toBe(false);
          expect(result.current.loadError).toBeNull();
          expect(result.current.notice).toBeNull();
          expect(result.current.record?.globalRecordId).toBe(idOf(1));
          expect(result.current.recordGeneration).toBe(recordGeneration);
          expect(result.current.n).toBe(n);
          expect(result.current.count).toBe(count);
          expect(result.current.includeDeleted).toBe(false);
        };

        await act(async () => {
          running = start(result.current, api);
          await gate.reached;
          await flush();
        });

        // The held read has not answered and ignores its signal, so the load settled without
        // waiting for it, and cancelled it rather than leaving it to run on.
        expect(connection.selfAborted).toEqual([aborted.line]);
        expect(sentTo(connection, held.line).signal.aborted).toBe(true);
        unchanged();

        // Its late answer changes nothing either.
        await act(async () => {
          gate.release();
          await running;
          await flush();
        });
        unchanged();
      },
    );

    it.each(parallelLoads)(
      'a superseded $name whose reads are aborted changes nothing while the move that superseded it runs',
      async ({ start, held }) => {
        const connection = await mountWire(activeRows(5));
        const { api, result } = connection;
        const obsoleteGate = api.hold(held.pathname);
        let superseded: Promise<unknown> = Promise.resolve();
        await act(async () => {
          superseded = start(result.current, api);
          await obsoleteGate.reached;
        });
        const obsolete = sentTo(connection, held.line);
        expect(obsolete.signal.aborted).toBe(false);
        const moveGate = api.hold(`${RECORDS_PATH}/at/2`);
        let moving: Promise<boolean> = Promise.resolve(false);

        await act(async () => {
          moving = result.current.goTo(2);
          await moveGate.reached;
          await flush();
        });

        // The move aborted the older load's reads. That load is no longer current, so its
        // aborted reads neither settle the move idle nor show anything.
        expect(obsolete.signal.aborted).toBe(true);
        expect(connection.selfAborted).toEqual([]);
        expect(result.current.loading).toBe(true);
        expect(result.current.transitioning).toBe(true);
        expect(result.current.loadError).toBeNull();
        expect(result.current.notice).toBeNull();
        expect(result.current.n).toBe(1);
        expect(result.current.record?.globalRecordId).toBe(idOf(1));
        expect(result.current.includeDeleted).toBe(false);

        await act(async () => {
          moveGate.release();
          expect(await moving).toBe(true);
        });
        const opened = result.current;
        expect(opened.n).toBe(2);
        expect(opened.record?.globalRecordId).toBe(idOf(2));
        expect(opened.loading).toBe(false);
        expect(opened.transitioning).toBe(false);

        // The superseded load's late answer changes nothing.
        await act(async () => {
          obsoleteGate.release();
          await superseded;
          await flush();
        });
        expect(result.current).toBe(opened);
      },
    );

    it.each([
      {
        name: 'a later move',
        overtake: async ({ result }: MountedWire) => {
          await perform(() => result.current.goTo(4));
        },
        n: 4,
      },
      {
        name: 'New',
        overtake: async ({ result }: MountedWire) => {
          await perform(() => result.current.newRecord());
        },
        n: null,
      },
    ])('a load that $name supersedes has its request aborted', async ({ overtake, n }) => {
      const connection = await mountWire(activeRows(5));
      const { api, result } = connection;
      const gate = api.hold(`${RECORDS_PATH}/at/2`);
      let moving: Promise<void> = Promise.resolve();
      await act(async () => {
        moving = result.current.next();
        await gate.reached;
      });
      const held = sentTo(connection, atRequest(2));
      expect(held.signal.aborted).toBe(false);
      const sentBefore = connection.sent.length;

      await overtake(connection);

      expect(held.signal.aborted).toBe(true);
      // The load that overtook it is the latest, so its own reads run on.
      for (const entry of connection.sent.slice(sentBefore)) {
        expect(entry.signal.aborted).toBe(false);
      }
      await act(async () => {
        gate.release();
        await moving;
      });
      // The aborted read changed nothing; whatever overtook it decided what is open.
      expect(result.current.n).toBe(n);
      expect(result.current.loading).toBe(false);
      expect(result.current.loadError).toBeNull();
      expect(result.current.transitioning).toBe(false);
    });

    it('an unmount aborts the read in flight, and its late answer changes nothing', async () => {
      const connection = await mountWire(activeRows(5));
      const { api, result, unmount } = connection;
      const gate = api.hold(`${RECORDS_PATH}/at/2`);
      let moving: Promise<void> = Promise.resolve();
      await act(async () => {
        moving = result.current.next();
        await gate.reached;
      });
      const held = sentTo(connection, atRequest(2));
      expect(held.signal.aborted).toBe(false);
      const shown = result.current;

      unmount();

      expect(held.signal.aborted).toBe(true);
      gate.release();
      await moving;
      await flush();
      expect(result.current).toBe(shown);
      expect(api.requests().slice(-1)).toEqual([atRequest(2)]);
    });
  });

  describe('the save a transition leave makes', () => {
    it.each([
      {
        name: 'next, once the leave has saved the record,',
        run: NEXT.run,
        left: true,
        reads: [countRequest(), atRequest(2)],
        parallel: false,
        n: 2,
      },
      {
        name: 'New, once the leave has saved the record,',
        run: (navigation: RecordNavigation) => navigation.newRecord(),
        left: true,
        reads: [countRequest()],
        parallel: false,
        n: null,
      },
      {
        name: 'next, cancelled after the leave saved the record,',
        run: NEXT.run,
        left: false,
        reads: sorted([countRequest(), positionRequest(idOf(1))]),
        parallel: true,
        n: 1,
      },
    ])('$name sends no after-save refresh of its own before its reads', async ({ run, left, reads, parallel, n }) => {
      const { api, result, beforeLeave, sent } = await mountWire(activeRows(3));
      const saved = api.dto(idOf(1));
      // The editor's leave() saves the dirty record and reports the save before it resolves.
      beforeLeave.mockImplementation(() => {
        api.note('beforeLeave');
        result.current.afterSave(saved);
        return Promise.resolve(left);
      });
      const mark = api.mark();
      const sentBefore = sent.length;

      await perform(() => run(result.current));
      await settled(result);

      const log = api.logSince(mark);
      expect(log[0]).toBe('beforeLeave');
      expect(parallel ? sorted(log.slice(1)) : log.slice(1)).toEqual(reads);
      // Nothing was sent only to be superseded.
      expect(sent.slice(sentBefore).filter((entry) => entry.signal.aborted)).toEqual([]);
      expect(result.current.n).toBe(n);
      expect(result.current.count).toBe(3);
      expect(result.current.transitioning).toBe(false);
      expect(result.current.loadError).toBeNull();
    });

    // Record 4 joins the 3-row set before the leave: the record the leave creates, or one
    // another session created while record 2 was edited, so N grows in both cases.
    it.each([
      {
        name: 'a create on leave (New, then First)',
        initialPosition: 1,
        prepare: (navigation: RecordNavigation) => navigation.newRecord(),
        saved: idOf(4),
        run: FIRST.run,
        failed: `${RECORDS_PATH}/at/1`,
        move: [countRequest(), atRequest(1)],
        coordinates: { n: 4, count: 4, statusText: '4 of 4' },
        landed: { id: idOf(1), statusText: '1 of 4' },
      },
      {
        name: 'an update on leave (record 2 edited, then Next)',
        initialPosition: 2,
        prepare: () => Promise.resolve(),
        saved: idOf(2),
        run: NEXT.run,
        failed: `${RECORDS_PATH}/at/3`,
        move: [countRequest(), atRequest(3)],
        coordinates: { n: 2, count: 4, statusText: '2 of 4' },
        landed: { id: idOf(3), statusText: '3 of 4' },
      },
    ])(
      "after $name fails, the saved record's n and N are refreshed while the failure and its Retry stay",
      async ({ initialPosition, prepare, saved, run, failed, move, coordinates, landed }) => {
        const connection = await mountWire(activeRows(3), initialPosition);
        const { api, result, beforeLeave } = connection;
        await perform(() => prepare(result.current));
        api.add(row(4));
        // The editor's leave() saves the dirty draft and reports the save before it resolves;
        // later leaves find the draft clean.
        beforeLeave.mockImplementationOnce(() => {
          api.note('beforeLeave');
          result.current.afterSave(api.dto(saved));
          return Promise.resolve(true);
        });
        connection.failNext(failed);
        const mark = api.mark();

        await perform(() => run(result.current));
        await act(flush);

        // The move's reads, the failing one last, then the coordinates of the saved record.
        const log = api.logSince(mark);
        expect(log.slice(0, 3)).toEqual(['beforeLeave', ...move]);
        expect(sorted(log.slice(3))).toEqual(sorted([countRequest(), positionRequest(saved)]));
        expect(result.current.loadError?.kind).toBe('unavailable');
        expect(result.current.record?.globalRecordId).toBe(saved);
        expect(result.current.isNew).toBe(false);
        expect(result.current.n).toBe(coordinates.n);
        expect(result.current.count).toBe(coordinates.count);
        expect(result.current.statusText).toBe(coordinates.statusText);
        expect(result.current.loading).toBe(false);
        expect(result.current.transitioning).toBe(false);
        const { recordGeneration } = result.current;

        // Retry repeats the move; N is fresh from the refresh, so only its target is read.
        const retryMark = api.mark();
        await perform(() => result.current.retryLoad());
        await settled(result);

        expect(api.logSince(retryMark)).toEqual(['beforeLeave', move[1]]);
        expect(result.current.loadError).toBeNull();
        expect(result.current.record?.globalRecordId).toBe(landed.id);
        expect(result.current.statusText).toBe(landed.statusText);
        expect(result.current.recordGeneration).toBe(recordGeneration + 1);
        expect(result.current.transitioning).toBe(false);
      },
    );

    /**
     * From New, First whose leave saves the created record 4, which the test has added to
     * the fake's rows, and whose at/1 then answers 503. Resolves the log mark taken just
     * before First.
     */
    async function failFirstAfterCreate(connection: MountedWire): Promise<number> {
      const { api, result, beforeLeave } = connection;
      await perform(() => result.current.newRecord());
      beforeLeave.mockImplementationOnce(() => {
        api.note('beforeLeave');
        result.current.afterSave(api.dto(idOf(4)));
        return Promise.resolve(true);
      });
      connection.failNext(`${RECORDS_PATH}/at/1`);
      const mark = api.mark();
      await perform(() => result.current.first());
      return mark;
    }

    it('a coordinates refresh answering 404 changes nothing: the failure stays and Retry re-reads N', async () => {
      const connection = await mountWire(activeRows(3));
      const { api, result } = connection;
      api.add(row(4));
      api.failPosition(idOf(4));

      const mark = await failFirstAfterCreate(connection);
      await act(flush);

      // The refresh's 404 starts no recovery and shows nothing of its own.
      const log = api.logSince(mark);
      expect(log.slice(0, 3)).toEqual(['beforeLeave', countRequest(), atRequest(1)]);
      expect(sorted(log.slice(3))).toEqual(sorted([countRequest(), positionRequest(idOf(4))]));
      expect(result.current.loadError?.kind).toBe('unavailable');
      expect(result.current.notice).toBeNull();
      expect(result.current.record?.globalRecordId).toBe(idOf(4));
      expect(result.current.n).toBeNull();
      expect(result.current.count).toBe(3);
      expect(result.current.statusText).toBe('');

      // N is still stale, so the repeated move reads it before its target.
      const retryMark = api.mark();
      await perform(() => result.current.retryLoad());
      await settled(result);

      expect(api.logSince(retryMark)).toEqual(['beforeLeave', countRequest(), atRequest(1)]);
      expect(result.current.loadError).toBeNull();
      expect(result.current.record?.globalRecordId).toBe(idOf(1));
      expect(result.current.statusText).toBe('1 of 4');
    });

    it('Retry supersedes a coordinates refresh in flight and aborts it; its late answer changes nothing', async () => {
      const connection = await mountWire(activeRows(3));
      const { api, result } = connection;
      api.add(row(4));
      const gate = api.hold(`${RECORDS_PATH}/${idOf(4)}/position`);

      await failFirstAfterCreate(connection);
      await act(async () => {
        await gate.reached;
      });

      const refresh = sentTo(connection, positionRequest(idOf(4)));
      expect(refresh.signal.aborted).toBe(false);
      expect(result.current.loadError?.kind).toBe('unavailable');
      expect(result.current.loading).toBe(false);
      expect(result.current.n).toBeNull();
      const retryMark = api.mark();

      await perform(() => result.current.retryLoad());
      await settled(result);

      // The refresh never answered, so N is still stale and the move re-reads it.
      expect(refresh.signal.aborted).toBe(true);
      expect(api.logSince(retryMark)).toEqual(['beforeLeave', countRequest(), atRequest(1)]);
      const opened = result.current;
      expect(opened.loadError).toBeNull();
      expect(opened.record?.globalRecordId).toBe(idOf(1));
      expect(opened.statusText).toBe('1 of 4');

      await act(async () => {
        gate.release();
        await flush();
      });
      expect(result.current).toBe(opened);
    });
  });

  // Another session deletes the open record while its coordinates are being read, and a
  // save then finds it deleted (409). Reads sent before that conflict never install n and
  // N over it: the record stays at the n and N shown, which still count it, and the next
  // move re-reads N and opens the record that followed it.
  describe('coordinates read before a conflict', () => {
    it.each([
      { held: 'count', path: `${RECORDS_PATH}/count`, line: countRequest() },
      { held: 'position', path: `${RECORDS_PATH}/${idOf(4)}/position`, line: positionRequest(idOf(4)) },
    ])(
      'an after-save refresh whose $held read is held across the conflict is superseded, and Next re-reads N',
      async ({ path, line }) => {
        const connection = await mountWire(activeRows(5), 4);
        const { api, result } = connection;
        const gate = api.hold(path);
        await act(async () => {
          result.current.afterSave(api.dto(idOf(4)));
          await gate.reached;
        });
        expect(result.current.loading).toBe(true);
        const refresh = sentTo(connection, line);
        api.setStatus(idOf(4), DELETED);

        act(() => {
          result.current.afterConflict();
        });

        expect(refresh.signal.aborted).toBe(true);
        expect(result.current.loading).toBe(false);
        // The held read answers after the conflict and installs nothing.
        await act(async () => {
          gate.release();
          await flush();
        });
        expect(result.current.n).toBe(4);
        expect(result.current.count).toBe(5);
        expect(result.current.statusText).toBe('4 of 5');
        expect(result.current.loading).toBe(false);
        expect(result.current.loadError).toBeNull();
        const mark = api.mark();

        await perform(() => result.current.next());

        expect(api.logSince(mark)).toEqual(['beforeLeave', countRequest(), atRequest(4)]);
        expect(result.current.record?.globalRecordId).toBe(idOf(5));
        expect(result.current.statusText).toBe('4 of 4');
        expect(result.current.canNext).toBe(false);
        expect(result.current.notice).toBeNull();
      },
    );

    // Record 3 of 5 is deleted elsewhere once its save has answered, so the refresh finds it
    // outside the active set.
    it.each([
      {
        name: 'an after-save refresh',
        refresh: async ({ api, result }: MountedWire): Promise<void> => {
          const saved = api.dto(idOf(3));
          api.setStatus(idOf(3), DELETED);
          act(() => {
            result.current.afterSave(saved);
          });
          await settled(result);
        },
        failure: null,
      },
      {
        name: 'the coordinates refresh after a Next whose leave saved it fails',
        refresh: async (connection: MountedWire): Promise<void> => {
          const { api, result, beforeLeave } = connection;
          beforeLeave.mockImplementationOnce(() => {
            api.note('beforeLeave');
            result.current.afterSave(api.dto(idOf(3)));
            api.setStatus(idOf(3), DELETED);
            return Promise.resolve(true);
          });
          connection.failNext(`${RECORDS_PATH}/at/4`);
          await perform(() => result.current.next());
          await act(flush);
        },
        failure: 'unavailable',
      },
    ])(
      '$name placing the saved record outside the set keeps n and N, and Next opens the record that followed',
      async ({ refresh, failure }) => {
        const connection = await mountWire(activeRows(5), 3);
        const { api, result } = connection;
        const { listVersion } = result.current;
        const mark = api.mark();

        await refresh(connection);

        expect(api.requestsSince(mark)).toContain(positionRequest(idOf(3)));
        expect(result.current.record?.globalRecordId).toBe(idOf(3));
        expect(result.current.n).toBe(3);
        expect(result.current.count).toBe(5);
        expect(result.current.statusText).toBe('3 of 5');
        expect(result.current.canPrevious).toBe(true);
        expect(result.current.canNext).toBe(true);
        expect(result.current.loading).toBe(false);
        expect(result.current.loadError?.kind ?? null).toBe(failure);
        // The save and the record leaving the set each reload the grid.
        expect(result.current.listVersion).toBe(listVersion + 2);
        const moveMark = api.mark();

        await perform(() => result.current.next());

        expect(api.logSince(moveMark)).toEqual(['beforeLeave', countRequest(), atRequest(3)]);
        expect(result.current.record?.globalRecordId).toBe(idOf(4));
        expect(result.current.statusText).toBe('3 of 4');
        expect(result.current.loadError).toBeNull();
        expect(result.current.notice).toBeNull();
      },
    );

    it('a toggle to the active set whose reads predate the conflict leaves the record and reads the set again', async () => {
      const { api, result, beforeLeave } = await mount(activeRows(5), { initialPosition: 3 });
      await perform(() => result.current.setIncludeDeleted(true));
      expect(result.current.statusText).toBe('3 of 5');
      const gate = api.hold(`${RECORDS_PATH}/count`);
      const mark = api.mark();
      const toggleReads = sorted([countRequest(), positionRequest(idOf(3))]);
      let toggling: Promise<void> = Promise.resolve();
      await act(async () => {
        toggling = result.current.setIncludeDeleted(false);
        await gate.reached;
      });
      // The position read has answered from before the delete: record 3 is 3 of the active set.
      await waitFor(() => {
        expect(sorted(api.requestsSince(mark))).toEqual(toggleReads);
      });
      api.setStatus(idOf(3), DELETED);
      act(() => {
        result.current.afterConflict();
      });
      const leaves = beforeLeave.mock.calls.length;

      await act(async () => {
        gate.release();
        await toggling;
      });

      const log = api.logSince(mark);
      expect(sorted(log.slice(0, 2))).toEqual(toggleReads);
      expect(log[2]).toBe('beforeLeave');
      expect(sorted(log.slice(3, 5))).toEqual(toggleReads);
      expect(log.slice(5)).toEqual([atRequest(3)]);
      expect(beforeLeave).toHaveBeenCalledTimes(leaves + 1);
      expect(result.current.includeDeleted).toBe(false);
      expect(result.current.record?.globalRecordId).toBe(idOf(4));
      expect(result.current.statusText).toBe('3 of 4');
      expect(result.current.notice).toMatch(DELETED_HIDDEN);
      expect(result.current.loadError).toBeNull();
    });
  });

  describe('the editor wired to navigation as FormView wires them', () => {
    /** A writable text field the fake's records leave empty. */
    const EDITED = 'FirstName';

    interface Workspace {
      readonly navigation: RecordNavigation;
      readonly editor: RecordEditor;
    }

    function useWorkspace(client: ReturnType<typeof createApiClient>): Workspace {
      const leaveRef = useRef<() => Promise<boolean>>(() => Promise.resolve(true));
      const navigation = useRecordNavigation({ client, viewId: VIEW_ID, beforeLeave: () => leaveRef.current() });
      const editor = useRecordEditor({
        client,
        viewId: VIEW_ID,
        definition: formDefinitionFixture,
        record: navigation.record,
        isNew: navigation.isNew,
        recordGeneration: navigation.recordGeneration,
        onSaved: navigation.afterSave,
        onDeleted: navigation.afterDelete,
        onConflict: navigation.afterConflict,
        onDiscardToNearest: navigation.openNearest,
        transitioning: navigation.transitioning,
      });
      const { leave } = editor;
      useLayoutEffect(() => {
        leaveRef.current = leave;
      }, [leave]);
      return { navigation, editor };
    }

    it('refuses an edit while an open by id waits for its position and count, then edits the opened record', async () => {
      const api = createFakeApi(activeRows(5));
      const client = createApiClient({ baseUrl: 'http://localhost', fetch: api.fetch });
      const { result } = renderHook(() => useWorkspace(client));
      await waitFor(() => {
        expect(result.current.navigation.loading).toBe(false);
      });
      expect(result.current.editor.values.LastName).toBe('Last 1');
      expect(result.current.editor.frozen).toBe(false);
      expect(result.current.editor.isDisabled(EDITED)).toBe(false);
      expect(result.current.editor.isProtected(EDITED)).toBe(false);
      const positionGate = api.hold(`${RECORDS_PATH}/${idOf(4)}/position`);
      const countGate = api.hold(`${RECORDS_PATH}/count`);
      let opening: Promise<void> = Promise.resolve();

      await act(async () => {
        opening = result.current.navigation.openById(idOf(4));
        await Promise.all([positionGate.reached, countGate.reached]);
      });

      // The record read has answered and the old record is still shown, frozen: the edit
      // that the record about to be installed would silently replace is refused instead.
      // Frozen is not protected: the field is disabled, and its protection is unchanged.
      expect(result.current.navigation.transitioning).toBe(true);
      expect(result.current.navigation.record?.globalRecordId).toBe(idOf(1));
      expect(result.current.editor.frozen).toBe(true);
      expect(result.current.editor.isDisabled(EDITED)).toBe(true);
      expect(result.current.editor.isProtected(EDITED)).toBe(false);
      act(() => {
        result.current.editor.setValue(EDITED, 'Typed while opening');
      });
      expect(result.current.editor.values[EDITED]).toBeNull();
      expect(result.current.editor.isDirty).toBe(false);

      await act(async () => {
        positionGate.release();
        countGate.release();
        await opening;
      });

      expect(result.current.navigation.transitioning).toBe(false);
      expect(result.current.navigation.record?.globalRecordId).toBe(idOf(4));
      expect(result.current.navigation.n).toBe(4);
      expect(result.current.editor.values.LastName).toBe('Last 4');
      expect(result.current.editor.frozen).toBe(false);
      expect(result.current.editor.isDisabled(EDITED)).toBe(false);
      expect(result.current.editor.isProtected(EDITED)).toBe(false);
      act(() => {
        result.current.editor.setValue(EDITED, 'Typed after opening');
      });
      expect(result.current.editor.values[EDITED]).toBe('Typed after opening');
      expect(result.current.editor.isDirty).toBe(true);
    });
  });
});
