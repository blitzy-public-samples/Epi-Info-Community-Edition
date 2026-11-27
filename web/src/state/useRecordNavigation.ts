// Navigator state and record transitions of one form: which record is open, its place
// "[n] of N" in the set the deleted toggle selects, and every move between records. It
// replaces legacy GoToRecordHandler and the toolstrip's record number: a dirty record is
// saved before it is left (the editor's leave(), awaited as `beforeLeave`), a blank
// record number opens the first record, and positions are the API's count of the
// selected set with UniqueKey <= k.
//
// Sources of n and N (C-17), never a search result and never the Record DTO, whose own
// `position` and `count` are intrinsic to the record:
// - N is GET .../records/count for the toggle's set, re-read on load, open by id, save,
//   delete, toggle change and 404 recovery; moves reuse the known N;
// - n is the requested position after GET .../records/at/{n}, and the position
//   endpoint's answer after an open by id, a save or a toggle change.
//
// Only GET requests are sent and no field event is raised: opening a record writes
// nothing and runs no check code (DATA-01). Legacy's re-save after opening is not kept.
import { useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';

import type { ApiClient, ApiResult } from '../api/client';
import { errorMessages, isAbortError, networkError, type ApiError } from '../api/errors';
import type { NavigationPosition, RecordCountResponse, RecordDto } from '../api/types';

export interface UseRecordNavigationOptions {
  readonly client: ApiClient;
  /** The open form; a change resets the navigator and loads the new form. */
  readonly viewId: number;
  /** 1-based position opened on load; absent, or not a positive integer, opens the first record. */
  readonly initialPosition?: number;
  /**
   * The initial position as the user wrote it, such as the `record` URL parameter. When the
   * load cannot open it as asked, its notice names it and says what opened instead: the
   * first record for text that is not a record number, the last for a position past the
   * end, New on an empty set. Absent, the notice names `initialPosition`, and with neither
   * the first record opens with no notice.
   */
  readonly initialPositionText?: string;
  /**
   * The deleted toggle as the form loads: `true` starts it on, so the first count, record
   * and list read every record; absent starts it off. A later toggle change replaces it.
   */
  readonly initialIncludeDeleted?: boolean;
  /**
   * The editor's leave(): drains pending field events and saves a dirty draft. A user
   * transition awaits it first and is cancelled, with no request, when it resolves `false`.
   */
  readonly beforeLeave: () => Promise<boolean>;
}

/**
 * Navigator state and actions. Actions are function-typed properties, never methods,
 * and keep their identity for the life of the hook, so they can be passed unbound
 * (`onSaved: navigation.afterSave`).
 */
export interface RecordNavigation {
  /** Position of the open record in the selected set; `null` for New or when unknown. */
  readonly n: number | null;
  /** Size of the selected set. */
  readonly count: number;
  /** The deleted toggle: `true` selects every record, `false` the active set. */
  readonly includeDeleted: boolean;
  readonly record: RecordDto | null;
  readonly isNew: boolean;
  /** +1 whenever a record or New is installed, a reload included; unchanged by afterSave. */
  readonly recordGeneration: number;
  /** +1 whenever the record list may have changed; the grid's reload key. */
  readonly listVersion: number;
  readonly loading: boolean;
  /**
   * A record-changing operation is running: set before its beforeLeave is called, and
   * cleared once it has installed a record or New, was cancelled, failed or settled idle.
   * FormView passes it to useRecordEditor, which freezes the draft meanwhile, so no edit
   * made during a move is lost to the record that replaces it.
   */
  readonly transitioning: boolean;
  /** Informational banner text, such as "This record no longer exists." after a 404. */
  readonly notice: string | null;
  /** Why the last goTo was rejected; cleared by the next transition. */
  readonly goToError: string | null;
  /** The failed load, shown with Retry; `kind` `notFound` from a count means the form is gone. */
  readonly loadError: ApiError | null;
  /** "New record", "<n> of <N>", or empty while no position is known. */
  readonly statusText: string;
  readonly canFirst: boolean;
  readonly canPrevious: boolean;
  readonly canNext: boolean;
  readonly canLast: boolean;
  readonly canGoTo: boolean;
  readonly first: () => Promise<void>;
  /** From New, opens the last record; at position 1 it does nothing. */
  readonly previous: () => Promise<void>;
  /** At position N, and from New, it does nothing. */
  readonly next: () => Promise<void>;
  readonly last: () => Promise<void>;
  /** Resolves `true` once record `k` is open; `false` when rejected, cancelled or failed. */
  readonly goTo: (k: number) => Promise<boolean>;
  readonly newRecord: () => Promise<void>;
  /** Opens a record from the grid or Find. */
  readonly openById: (id: string) => Promise<void>;
  readonly setIncludeDeleted: (value: boolean) => Promise<void>;
  /** The editor's onSaved: shows the saved record and re-reads N and n, keeping the generation. */
  readonly afterSave: (record: RecordDto) => void;
  /** The editor's onDeleted: re-reads N and opens the nearest record. */
  readonly afterDelete: () => void;
  /** Re-reads N and opens the record nearest to n, or New on an empty set. */
  readonly openNearest: (notice?: string) => void;
  /**
   * Repeats the last load; every load is GETs only, so it is always safe to repeat. A load
   * that replaces the record repeats its whole operation, beforeLeave first, so an edit
   * made since the failure is saved before anything is read; it sends nothing when
   * beforeLeave resolves `false`. `loadError` clears as Retry is pressed and is set again
   * when the repeated operation stops at its leave.
   */
  readonly retryLoad: () => Promise<void>;
  readonly dismissNotice: () => void;
}

type NavigationActions = Pick<
  RecordNavigation,
  | 'first'
  | 'previous'
  | 'next'
  | 'last'
  | 'goTo'
  | 'newRecord'
  | 'openById'
  | 'setIncludeDeleted'
  | 'afterSave'
  | 'afterDelete'
  | 'openNearest'
  | 'retryLoad'
  | 'dismissNotice'
>;

interface NavigationState {
  readonly n: number | null;
  readonly count: number;
  readonly includeDeleted: boolean;
  readonly record: RecordDto | null;
  readonly isNew: boolean;
  readonly recordGeneration: number;
  readonly listVersion: number;
  readonly loading: boolean;
  readonly transitioning: boolean;
  readonly notice: string | null;
  readonly goToError: string | null;
  readonly loadError: ApiError | null;
}

const INITIAL_STATE: NavigationState = {
  n: null,
  count: 0,
  includeDeleted: false,
  record: null,
  isNew: false,
  recordGeneration: 0,
  listVersion: 0,
  loading: true,
  transitioning: false,
  notice: null,
  goToError: null,
  loadError: null,
};

/** The state a form starts in: nothing open, with the deleted toggle as `options` asks. */
function initialStateOf(options: UseRecordNavigationOptions): NavigationState {
  return { ...INITIAL_STATE, includeDeleted: options.initialIncludeDeleted === true };
}

/** Legacy SharedStrings.NEW_RECORD. */
const NEW_RECORD_TEXT = 'New record';
const NO_RECORDS_TO_GO_TO = 'There are no records to go to.';

function goToRange(count: number): string {
  return `Enter a record number from 1 to ${count}.`;
}

/**
 * The notice of an initial position that is not a record number, `asked` as written: the
 * first record opens instead, or New on an empty set.
 */
function notARecordNumber(asked: string, empty: boolean): string {
  const problem = asked.trim() === '' ? 'The record number is blank' : `'${asked}' is not a record number`;
  return empty
    ? `${problem}, and there are no records to show, so a new record is shown.`
    : `${problem}, so the first record is shown.`;
}

/**
 * The notice of an initial position past the end of the set, `asked` as written: the last
 * record, `landing`, opens instead, or New (`null`) on an empty set.
 */
function noSuchRecord(asked: string, landing: number | null): string {
  return landing === null
    ? `There is no record ${asked}; there are no records to show, so a new record is shown.`
    : `There is no record ${asked}; showing the last record, ${landing}.`;
}

/** A recovery's notice naming where it landed: a position of the set, or `null` for New on an empty set. */
type LandingNotice = (landing: number | null) => string;

type Move = 'first' | 'previous' | 'next' | 'last';

/** Where a move lands: a fixed position, or the last record of the set when the move runs. */
type MoveTarget = { readonly kind: 'position'; readonly position: number } | { readonly kind: 'last' };

/** The target of a move from `state`, or `null` for a move that does nothing. */
function resolveMove(move: Move, state: NavigationState): MoveTarget | null {
  const { n, count, isNew } = state;
  if (count === 0) {
    return null;
  }
  switch (move) {
    case 'first':
      return { kind: 'position', position: 1 };
    case 'last':
      return { kind: 'last' };
    case 'previous':
      // Previous from New is the record before it, the last of the N shown with New,
      // even when leaving New saves it as record N + 1.
      if (isNew) {
        return { kind: 'position', position: count };
      }
      return n !== null && n > 1 ? { kind: 'position', position: n - 1 } : null;
    case 'next':
      return !isNew && n !== null && n < count ? { kind: 'position', position: n + 1 } : null;
  }
}

/** A settled call, or `null` for an aborted one, which is never an error to show. */
type Settled<T> = ApiResult<T> | null;

/** A call's result. An aborted call rejects with its AbortError, which its caller settles. */
async function attempt<T>(call: () => Promise<ApiResult<T>>): Promise<ApiResult<T>> {
  try {
    return await call();
  } catch (e) {
    if (isAbortError(e)) {
      throw e;
    }
    // The client settles every response itself; any other rejection got no response.
    return { ok: false, error: networkError() };
  }
}

/** A single read's result, or `null` once it was aborted. */
async function settle<T>(read: Promise<ApiResult<T>>): Promise<Settled<T>> {
  try {
    return await read;
  } catch (e) {
    if (isAbortError(e)) {
      return null;
    }
    throw e;
  }
}

interface ControllerHost {
  readonly options: () => UseRecordNavigationOptions;
  readonly state: () => NavigationState;
  readonly update: (patch: Partial<NavigationState>) => void;
}

interface Controller {
  readonly actions: NavigationActions;
  /** Starts the form's initial load; resets first when the form changed. */
  readonly activate: (viewId: number) => void;
  /** Drops every load and transition in flight. */
  readonly deactivate: () => void;
}

/** The selected set a load works in: its toggle value, its N, and whether this load read N. */
interface SetView {
  readonly includeDeleted: boolean;
  readonly count: number;
  readonly fresh: boolean;
}

/** RecStatus of an active record; the active set is RecStatus = 1 (C-16). */
const ACTIVE_STATUS = 1;

/** The 1-based position nearest to `anchor` in a set of `count` records. */
function nearest(anchor: number, count: number): number {
  return Math.min(Math.max(anchor, 1), count);
}

/** One load: its generation, and the signal that cancels its requests once it is superseded. */
interface Load {
  readonly gen: number;
  readonly signal: AbortSignal;
  /** Aborts this load's own requests: one of its parallel reads was aborted, so the rest are not awaited. */
  readonly cancel: () => void;
}

/**
 * The results of `load`'s parallel reads, in order, once every one has answered; `null` as
 * soon as one of them is aborted. That abort cancels the whole load, so its other requests
 * are aborted as well, and nothing waits for them, whether or not they honour the signal.
 */
async function settleAll<T extends readonly Promise<ApiResult<unknown>>[] | []>(
  load: Load,
  reads: T,
): Promise<{ -readonly [K in keyof T]: Awaited<T[K]> } | null> {
  try {
    return await Promise.all(reads);
  } catch (e) {
    if (isAbortError(e)) {
      load.cancel();
      return null;
    }
    throw e;
  }
}

// Every load takes the next generation and applies its results only while it is still
// the latest, and its requests are aborted once a newer load, New or an unmount
// supersedes it. That covers StrictMode's repeated effects and fast clicks. A read the
// current load itself finds aborted settles that load idle, with nothing to show.
function createController(host: ControllerHost): Controller {
  /** Generation of the latest load. */
  let loadGen = 0;
  /** Cancels the requests of the latest load. */
  let loadAbort: AbortController | null = null;
  /** The latest user transition; an older one stops once its beforeLeave resolves. */
  let intent = 0;
  let active = false;
  let activeViewId: number | null = null;
  /** Set by afterSave, since a create grows the set: the next move or New re-reads N. */
  let countStale = false;
  /** What retryLoad repeats. */
  let lastLoad: (() => Promise<unknown>) | null = null;
  /**
   * The failure a pending Retry took off screen. It is still the latest failure until a
   * load begins or New is installed, either of which drops it, and it is shown again when
   * the repeated operation stops at its leave.
   */
  let retrying: ApiError | null = null;
  /** The intent whose beforeLeave is pending: a save its leave makes defers the after-save refresh to it. */
  let leavingToken: number | null = null;
  /**
   * The record generation of an after-save refresh deferred to the transition that is
   * leaving the saved record; `null` when none is deferred.
   */
  let deferredRefresh: number | null = null;

  const api = (): ApiClient => host.options().client;
  const viewId = (): number => host.options().viewId;

  // Each read rejects once aborted: settle() resolves a single read, settleAll() parallel ones.
  const readCount = (load: Load, includeDeleted: boolean): Promise<ApiResult<RecordCountResponse>> =>
    attempt(() => api().countRecords(viewId(), { includeDeleted }, load.signal));
  const readAt = (load: Load, position: number, includeDeleted: boolean): Promise<ApiResult<RecordDto>> =>
    attempt(() => api().getRecordAt(viewId(), position, { includeDeleted }, load.signal));
  const readRecord = (load: Load, id: string): Promise<ApiResult<RecordDto>> =>
    attempt(() => api().getRecord(viewId(), id, load.signal));
  const readPosition = (load: Load, id: string, includeDeleted: boolean): Promise<ApiResult<NavigationPosition>> =>
    attempt(() => api().getRecordPosition(viewId(), id, { includeDeleted }, load.signal));

  const isCurrent = (load: Load): boolean => active && load.gen === loadGen;

  /** Cancels the latest load's requests; its results are ignored by generation as well. */
  function abortLoad(): void {
    loadAbort?.abort();
    loadAbort = null;
  }

  /** Takes the next generation: every load in flight is superseded and its requests aborted. */
  function nextLoad(): Load {
    loadGen += 1;
    abortLoad();
    const abort = new AbortController();
    loadAbort = abort;
    return {
      gen: loadGen,
      signal: abort.signal,
      cancel: () => {
        abort.abort();
      },
    };
  }

  /** Starts a load that supersedes every load in flight and aborts its requests; Retry runs `repeat`. */
  function begin(repeat: () => Promise<unknown>): Load {
    const load = nextLoad();
    lastLoad = repeat;
    retrying = null;
    host.update({ loading: true, loadError: null });
    return load;
  }

  /** Ends the current load with `error`, keeping the previous display. */
  function fail(error: ApiError): void {
    host.update({ loading: false, loadError: error });
  }

  /** Ends the current load whose read was aborted: idle, with no error to show or retry. */
  function settleIdle(): void {
    host.update({ loading: false });
  }

  /** The operation `token` is about to replace the record: while it is the latest intent, it owns `transitioning`. */
  function claimTransition(token: number): void {
    if (token === intent && !host.state().transitioning) {
      host.update({ transitioning: true });
    }
  }

  /**
   * Starts a record-changing operation as the latest intent. `transitioning` is set before
   * anything is awaited, beforeLeave included, so the editor freezes the draft at once.
   */
  function startTransition(): number {
    intent += 1;
    claimTransition(intent);
    return intent;
  }

  /**
   * Ends the operation `token`. Only the latest intent clears `transitioning`, since a
   * newer one owns it, and only it settles a deferred after-save refresh, which runs when
   * the operation installed nothing and left N stale. When the operation failed, or is a
   * Retry that stopped at its leave, only the saved record's coordinates are re-read, so
   * the failure and its Retry stay shown.
   */
  function endTransition(token: number): void {
    if (token !== intent) {
      return;
    }
    if (host.state().transitioning) {
      host.update({ transitioning: false });
    }
    const deferredIn = deferredRefresh;
    deferredRefresh = null;
    const { record, recordGeneration, includeDeleted, loadError } = host.state();
    if (deferredIn === null || deferredIn !== recordGeneration || !active || !countStale || record === null) {
      return;
    }
    if (loadError === null && retrying === null) {
      void refreshAfterSave(record, includeDeleted);
    } else {
      void refreshCoordinates(record, includeDeleted);
    }
  }

  /**
   * A record-changing operation that leaves the open record first: `body` runs only once
   * beforeLeave has resolved `true` while this is still the latest intent.
   */
  async function guarded(body: () => Promise<boolean>): Promise<boolean> {
    const token = startTransition();
    try {
      return (await leave(token)) && (await body());
    } finally {
      endTransition(token);
    }
  }

  /** A record-changing operation that does not call beforeLeave: the draft was deleted or discarded. */
  async function replaceWithoutLeave(body: () => Promise<boolean>): Promise<void> {
    const token = startTransition();
    try {
      await body();
    } finally {
      endTransition(token);
    }
  }

  function installRecord(record: RecordDto, n: number, set: SetView): void {
    if (set.fresh) {
      countStale = false;
    }
    host.update({
      record,
      n,
      count: set.count,
      includeDeleted: set.includeDeleted,
      isNew: false,
      recordGeneration: host.state().recordGeneration + 1,
      loading: false,
      loadError: null,
    });
  }

  function installNew(set: SetView, notice?: string): void {
    if (set.fresh) {
      countStale = false;
    }
    host.update({
      record: null,
      n: null,
      count: set.count,
      includeDeleted: set.includeDeleted,
      isNew: true,
      recordGeneration: host.state().recordGeneration + 1,
      loading: false,
      loadError: null,
      ...(notice === undefined ? {} : { notice }),
    });
  }

  /** Awaits the editor's leave(); `false` also when a newer transition or an unmount overtook it. */
  async function leave(token: number): Promise<boolean> {
    leavingToken = token;
    try {
      const left = await host.options().beforeLeave();
      return left && active && token === intent;
    } finally {
      if (leavingToken === token) {
        leavingToken = null;
      }
    }
  }

  /**
   * Reads N for `load`; `null` when the load was overtaken, aborted (settled idle) or
   * failed, the failure shown.
   */
  async function readSet(load: Load, includeDeleted: boolean): Promise<SetView | null> {
    const counted = await settle(readCount(load, includeDeleted));
    if (!isCurrent(load)) {
      return null;
    }
    if (counted === null) {
      settleIdle();
      return null;
    }
    if (!counted.ok) {
      fail(counted.error);
      return null;
    }
    return { includeDeleted, count: counted.data.count, fresh: true };
  }

  /**
   * Opens the record at `position`; n is the requested position. A 404 shows "This
   * record no longer exists." and opens the record nearest to `position` once: when
   * `recovering` is set this already is that attempt, and a second 404 installs New
   * instead of looping. `landed`, when given, replaces that notice once the recovery
   * knows where it lands.
   */
  async function openAt(
    load: Load,
    position: number,
    set: SetView,
    recovering: boolean,
    landed?: LandingNotice,
  ): Promise<boolean> {
    const result = await settle(readAt(load, position, set.includeDeleted));
    if (!isCurrent(load)) {
      return false;
    }
    if (result === null) {
      settleIdle();
      return false;
    }
    if (result.ok) {
      installRecord(result.data, position, set);
      return true;
    }
    if (result.error.kind !== 'notFound') {
      fail(result.error);
      return false;
    }
    if (recovering) {
      installNew(set, errorMessages.notFound);
      return false;
    }
    await openNearestTo(position, set.includeDeleted, errorMessages.notFound, true, landed);
    return false;
  }

  /** Opens `position`, or installs New when the set is empty; `landed` as for openAt. */
  function openOrNew(
    load: Load,
    position: number,
    set: SetView,
    recovering: boolean,
    landed?: LandingNotice,
  ): Promise<boolean> {
    if (set.count === 0) {
      installNew(set);
      return Promise.resolve(false);
    }
    return openAt(load, position, set, recovering, landed);
  }

  /** Handles a failed read of a record by id or of its position: a 404 recovers once. */
  async function failOpen(error: ApiError, anchor: number, includeDeleted: boolean): Promise<void> {
    if (error.kind === 'notFound') {
      await openNearestTo(anchor, includeDeleted, errorMessages.notFound, true);
    } else {
      fail(error);
    }
  }

  /**
   * Re-reads N and opens the record nearest to `anchor`, or New on an empty set: the
   * landing after a 404 on open, a delete or discarded edits. The list may have changed,
   * so the grid reloads. `notice` shows at once; `landed`, when given, replaces it once N
   * is read, naming the position the recovery lands on.
   */
  function openNearestTo(
    anchor: number,
    includeDeleted: boolean,
    notice: string | null,
    recovering: boolean,
    landed?: LandingNotice,
  ): Promise<boolean> {
    const listVersion = host.state().listVersion + 1;
    host.update(notice === null ? { listVersion } : { listVersion, notice });
    const run = async (): Promise<boolean> => {
      // The record still shown may have been edited since a failure, so Retry leaves it first.
      const load = begin(() => guarded(run));
      const set = await readSet(load, includeDeleted);
      if (set === null) {
        return false;
      }
      const landing = nearest(anchor, set.count);
      if (landed !== undefined) {
        host.update({ notice: landed(set.count === 0 ? null : landing) });
      }
      return openOrNew(load, landing, set, recovering);
    };
    return run();
  }

  /**
   * Mount and form change: N, then the initial position, or New on an empty form. No leave.
   * A position that cannot be opened as asked gets a notice naming it as written
   * (`initialPositionText`) and saying what opened instead; a position past the end says
   * so once the 404 of its `at` read has been recovered from, which tells where it landed.
   */
  function loadInitial(includeDeleted: boolean): Promise<boolean> {
    const { initialPosition: requested, initialPositionText } = host.options();
    const valid = requested !== undefined && Number.isInteger(requested) && requested >= 1;
    const position = valid ? requested : 1;
    // What a notice names: the position as written, else as given; with neither, there is no notice.
    const asked = initialPositionText ?? (requested === undefined ? undefined : String(requested));
    const run = async (): Promise<boolean> => {
      const load = begin(run);
      const set = await readSet(load, includeDeleted);
      if (set === null) {
        return false;
      }
      if (asked === undefined) {
        return openOrNew(load, position, set, false);
      }
      if (!valid) {
        host.update({ notice: notARecordNumber(asked, set.count === 0) });
        return openOrNew(load, position, set, false);
      }
      if (set.count === 0) {
        host.update({ notice: noSuchRecord(asked, null) });
      }
      // A landing short of the position asked for is the last record. One at it means the
      // record there went away after N was read, which the 404 notice alone describes.
      return openOrNew(load, position, set, false, (landing) =>
        landing !== null && landing >= position
          ? errorMessages.notFound
          : `${errorMessages.notFound} ${noSuchRecord(asked, landing)}`,
      );
    };
    return run();
  }

  /** Re-reads N only; the open record, or New, stays. */
  function recount(includeDeleted: boolean): Promise<boolean> {
    const run = async (): Promise<boolean> => {
      const load = begin(run);
      const set = await readSet(load, includeDeleted);
      if (set === null) {
        return false;
      }
      countStale = false;
      host.update({ count: set.count, includeDeleted, loading: false });
      return true;
    };
    return run();
  }

  /**
   * A user move: leave the open record, then open the target; N is reused unless a save
   * made it stale. Retry repeats the whole move, so an edit made since a failure is saved
   * before the target is read.
   */
  function transition(target: MoveTarget): Promise<boolean> {
    return guarded(async () => {
      host.update({ goToError: null, notice: null });
      const { includeDeleted } = host.state();
      const positionIn = (count: number): number => (target.kind === 'last' ? count : target.position);
      const load = begin(() => transition(target));
      const { count } = host.state();
      if (!countStale && count > 0) {
        return openAt(load, positionIn(count), { includeDeleted, count, fresh: false }, false);
      }
      const set = await readSet(load, includeDeleted);
      return set === null ? false : openOrNew(load, positionIn(set.count), set, false);
    });
  }

  function move(kind: Move): Promise<boolean> {
    const target = resolveMove(kind, host.state());
    return target === null ? Promise.resolve(false) : transition(target);
  }

  function goTo(k: number): Promise<boolean> {
    const { count } = host.state();
    if (count === 0) {
      host.update({ goToError: NO_RECORDS_TO_GO_TO });
      return Promise.resolve(false);
    }
    if (!Number.isInteger(k) || k < 1 || k > count) {
      host.update({ goToError: goToRange(count) });
      return Promise.resolve(false);
    }
    return transition({ kind: 'position', position: k });
  }

  async function newRecord(): Promise<void> {
    const token = startTransition();
    try {
      if (!(await leave(token))) {
        return;
      }
      // New overtakes every load in flight, an after-save refresh among them.
      loadGen += 1;
      abortLoad();
      lastLoad = null;
      retrying = null;
      const stale = countStale;
      const { count, includeDeleted } = host.state();
      host.update({ goToError: null, notice: null });
      installNew({ includeDeleted, count, fresh: false });
      // New is open, so the draft is the user's again while N is re-read.
      endTransition(token);
      if (stale) {
        await recount(includeDeleted);
      }
    } finally {
      endTransition(token);
    }
  }

  /** Opens a record by id; Retry repeats the whole open, beforeLeave first. */
  async function openById(id: string): Promise<void> {
    await guarded(async () => {
      host.update({ goToError: null, notice: null });
      const { n, includeDeleted } = host.state();
      const anchor = n ?? 1;
      const load = begin(() => openById(id));
      const results = await settleAll(load, [
        readRecord(load, id),
        readPosition(load, id, includeDeleted),
        readCount(load, includeDeleted),
      ]);
      if (!isCurrent(load)) {
        return false;
      }
      if (results === null) {
        settleIdle();
        return false;
      }
      const [found, placed, counted] = results;
      if (!counted.ok) {
        fail(counted.error);
        return false;
      }
      if (!found.ok) {
        await failOpen(found.error, anchor, includeDeleted);
        return false;
      }
      if (!placed.ok) {
        await failOpen(placed.error, anchor, includeDeleted);
        return false;
      }
      const set: SetView = { includeDeleted, count: counted.data.count, fresh: true };
      const { position } = placed.data;
      if (position !== null) {
        installRecord(found.data, position, set);
        return true;
      }
      // Outside the selected set: open the set's record nearest to the one that was open.
      return openOrNew(load, nearest(anchor, set.count), set, false);
    });
  }

  async function setIncludeDeleted(value: boolean): Promise<void> {
    const before = host.state();
    if (value === before.includeDeleted) {
      return;
    }
    const token = ++intent;
    // Ends the toggle whichever path it took: it is the latest intent until a newer one starts.
    try {
      const { record } = before;
      if (record === null) {
        host.update({ goToError: null, notice: null });
        // New stays New and only N changes; with nothing open yet the form loads afresh.
        await (before.isNew ? recount(value) : loadInitial(value));
        return;
      }

      // Switching to the active set while a deleted record is open leaves that record.
      let left = !value && record.recStatus !== ACTIVE_STATUS;
      if (left) {
        claimTransition(token);
        if (!(await leave(token))) {
          return;
        }
      }
      host.update({ goToError: null, notice: null });
      const anchor = before.n ?? 1;
      const run = async (): Promise<boolean> => {
        // Results apply only on success, so includeDeleted is still the old value when
        // Retry repeats the whole toggle, with its leave where the record must be left.
        const load = begin(() => setIncludeDeleted(value));
        const results = await settleAll(load, [
          readCount(load, value),
          readPosition(load, record.globalRecordId, value),
        ]);
        if (!isCurrent(load)) {
          return false;
        }
        if (results === null) {
          settleIdle();
          return false;
        }
        const [counted, placed] = results;
        if (!counted.ok) {
          fail(counted.error);
          return false;
        }
        if (!placed.ok) {
          if (placed.error.kind !== 'notFound') {
            fail(placed.error);
            return false;
          }
          if (!left) {
            // The open record no longer exists. Opening the nearest one replaces it, and the
            // user may have edited it, so it is left first like any other transition.
            host.update({ loading: false });
            claimTransition(token);
            if (!(await leave(token))) {
              return false;
            }
            left = true;
          }
          await openNearestTo(anchor, value, errorMessages.notFound, true);
          return false;
        }
        const set: SetView = { includeDeleted: value, count: counted.data.count, fresh: true };
        const { position } = placed.data;
        if (position !== null) {
          // The open record stays: only n and N change, so the generation does not.
          countStale = false;
          host.update({ n: position, count: set.count, includeDeleted: value, loading: false });
          return true;
        }
        if (!left) {
          // Deleted elsewhere since it was opened: leave it like any other transition, then
          // read the set again, since the save may have run its own refresh.
          host.update({ loading: false });
          claimTransition(token);
          if (!(await leave(token))) {
            return false;
          }
          left = true;
          return run();
        }
        return openOrNew(load, nearest(anchor, set.count), set, false);
      };
      await run();
    } finally {
      endTransition(token);
    }
  }

  function afterSave(record: RecordDto): void {
    const before = host.state();
    countStale = true;
    // The same on-screen record, so the generation stays and the editor keeps its state.
    host.update({
      record,
      isNew: false,
      n: before.isNew ? null : before.n,
      listVersion: before.listVersion + 1,
    });
    if (leavingToken !== null && leavingToken === intent) {
      // The save is the one the pending transition's leave made. That transition re-reads
      // N, since countStale is set, so a refresh now would only be superseded; it runs
      // when the transition ends without replacing the record (endTransition).
      deferredRefresh = host.state().recordGeneration;
      return;
    }
    void refreshAfterSave(record, before.includeDeleted);
  }

  /** Re-reads N and the saved record's n; the record stays, so the generation does too. */
  function refreshAfterSave(record: RecordDto, includeDeleted: boolean): Promise<boolean> {
    const run = async (): Promise<boolean> => {
      const load = begin(run);
      const results = await settleAll(load, [
        readCount(load, includeDeleted),
        readPosition(load, record.globalRecordId, includeDeleted),
      ]);
      if (!isCurrent(load)) {
        return false;
      }
      if (results === null) {
        settleIdle();
        return false;
      }
      const [counted, placed] = results;
      if (!counted.ok) {
        fail(counted.error);
        return false;
      }
      if (!placed.ok) {
        if (placed.error.kind !== 'notFound') {
          fail(placed.error);
          return false;
        }
        // The saved record is gone. Opening the nearest one replaces it, and the user may
        // have edited it since the save, so it is left first like any other transition.
        const anchor = host.state().n ?? 1;
        host.update({ loading: false });
        await guarded(() => openNearestTo(anchor, includeDeleted, errorMessages.notFound, true));
        return false;
      }
      countStale = false;
      host.update({ n: placed.data.position, count: counted.data.count, loading: false });
      return true;
    };
    return run();
  }

  /**
   * Re-reads N and the saved record's n while a failed load is shown: the transition whose
   * leave saved the record failed afterwards, so the record stays open with stale
   * coordinates. The failure, its Retry and `loading` stay as they are. An abort, a failure
   * or a 404 changes nothing, so Retry still re-reads N; any later load supersedes this one
   * and aborts its requests.
   */
  async function refreshCoordinates(record: RecordDto, includeDeleted: boolean): Promise<void> {
    const load = nextLoad();
    const results = await settleAll(load, [
      readCount(load, includeDeleted),
      readPosition(load, record.globalRecordId, includeDeleted),
    ]);
    if (!isCurrent(load) || results === null) {
      return;
    }
    const [counted, placed] = results;
    if (!counted.ok || !placed.ok) {
      return;
    }
    countStale = false;
    host.update({ n: placed.data.position, count: counted.data.count });
  }

  /** Does not call beforeLeave: the delete already discarded the draft. */
  function afterDelete(): void {
    const { n, includeDeleted } = host.state();
    host.update({ goToError: null, notice: null });
    void replaceWithoutLeave(() => openNearestTo(n ?? 1, includeDeleted, null, false));
  }

  /** Does not call beforeLeave: the draft is gone (404) or its edits were discarded. */
  function openNearest(notice?: string): void {
    const { n, includeDeleted } = host.state();
    void replaceWithoutLeave(() => openNearestTo(n ?? 1, includeDeleted, notice ?? null, true));
  }

  async function retryLoad(): Promise<void> {
    const repeat = lastLoad;
    if (repeat === null) {
      return;
    }
    const failure = host.state().loadError;
    if (failure !== null) {
      // The failure leaves the screen in the commit of the press, before the repeated
      // operation's leave resolves; an operation that replaces the record freezes the draft
      // in that same commit.
      retrying = failure;
      host.update({ loadError: null });
    }
    try {
      await repeat();
    } finally {
      // Still `retrying`: no load began and no New replaced the operation, which stopped
      // at its leave, so its failure and the Retry that repeats it are shown again.
      if (failure !== null && retrying === failure) {
        retrying = null;
        if (active) {
          host.update({ loadError: failure });
        }
      }
    }
  }

  return {
    actions: {
      first: async () => {
        await move('first');
      },
      previous: async () => {
        await move('previous');
      },
      next: async () => {
        await move('next');
      },
      last: async () => {
        await move('last');
      },
      goTo,
      newRecord,
      openById,
      setIncludeDeleted,
      afterSave,
      afterDelete,
      openNearest,
      retryLoad,
      dismissNotice: () => {
        host.update({ notice: null });
      },
    },

    activate: (id) => {
      active = true;
      if (activeViewId !== null && activeViewId !== id) {
        const { recordGeneration, listVersion } = host.state();
        countStale = false;
        host.update({
          ...initialStateOf(host.options()),
          recordGeneration: recordGeneration + 1,
          listVersion: listVersion + 1,
        });
      } else if (host.state().transitioning) {
        // deactivate dropped every operation in flight, the one that owned the flag among them.
        host.update({ transitioning: false });
      }
      activeViewId = id;
      void loadInitial(host.state().includeDeleted);
    },

    deactivate: () => {
      active = false;
      loadGen += 1;
      abortLoad();
      intent += 1;
      leavingToken = null;
      deferredRefresh = null;
    },
  };
}

function statusTextOf({ isNew, n, count }: NavigationState): string {
  if (isNew) {
    return NEW_RECORD_TEXT;
  }
  return n === null ? '' : `${n} of ${count}`;
}

function present(state: NavigationState, actions: NavigationActions): RecordNavigation {
  const { n, count, isNew } = state;
  const hasRecords = count > 0;
  return {
    ...state,
    statusText: statusTextOf(state),
    // First and Last stay enabled at either end, where they reopen that record.
    canFirst: hasRecords,
    canPrevious: hasRecords && (isNew || (n !== null && n > 1)),
    canNext: !isNew && n !== null && n < count,
    canLast: hasRecords,
    canGoTo: hasRecords,
    ...actions,
  };
}

/**
 * Navigator state and record transitions of the form `viewId`, sending GET requests only.
 *
 * The editor's `leave()` is this hook's `beforeLeave`, while the editor needs this hook's
 * record and callbacks, so FormView breaks the cycle with a ref:
 *
 * ```tsx
 * const leaveRef = useRef<() => Promise<boolean>>(() => Promise.resolve(true));
 * const navigation = useRecordNavigation({ client, viewId, beforeLeave: () => leaveRef.current() });
 * const editor = useRecordEditor({
 *   client, viewId, definition,
 *   record: navigation.record, isNew: navigation.isNew, recordGeneration: navigation.recordGeneration,
 *   onSaved: navigation.afterSave, onDeleted: navigation.afterDelete, onDiscardToNearest: navigation.openNearest,
 *   transitioning: navigation.transitioning,
 * });
 * leaveRef.current = editor.leave;
 * ```
 *
 * Options are read when an action runs, so a new `beforeLeave` closure on every render
 * never changes an action's identity.
 */
export function useRecordNavigation(options: UseRecordNavigationOptions): RecordNavigation {
  const optionsRef = useRef(options);
  useLayoutEffect(() => {
    optionsRef.current = options;
  });

  const [state, setState] = useState<NavigationState>(() => initialStateOf(options));
  // Async transitions read the latest state between renders, so every update lands in
  // the ref synchronously and is rendered from there.
  const stateRef = useRef(state);
  const [controller] = useState(() =>
    createController({
      options: () => optionsRef.current,
      state: () => stateRef.current,
      update: (patch) => {
        stateRef.current = { ...stateRef.current, ...patch };
        setState(stateRef.current);
      },
    }),
  );

  const { viewId } = options;
  useEffect(() => {
    controller.activate(viewId);
    return () => {
      controller.deactivate();
    };
  }, [controller, viewId]);

  return useMemo(() => present(state, controller.actions), [state, controller]);
}
