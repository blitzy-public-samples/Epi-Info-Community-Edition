// Navigator state and record transitions of one form: which record is open, its place
// "[n] of N" in the set the deleted toggle selects, and every move between records. It
// replaces legacy GoToRecordHandler and the toolstrip's record number: a dirty record is
// saved before it is left (the editor's leave(), awaited as `beforeLeave`), a blank
// record number opens the first record, and positions are the API's count of the
// selected set with UniqueKey <= k.
//
// Sources of n and N (C-17), never a search result and never the Record DTO, whose own
// `position` and `count` are intrinsic to the record:
// - N is the size of the toggle's set. GET .../records/count reads it on load, delete and
//   404 recovery, and in the next move or New after a save, after a save found the open
//   record deleted (409), and while a 404, delete or discard landing runs. An open by id,
//   a save and a toggle change take it from the `count` the position endpoint answers with
//   n, so they send no count read; when that answer places the open record outside the
//   set, its `count` is the N the next move or New uses (knownSize), while the n and N
//   shown stay. Other moves reuse the known N;
// - n is the requested position after GET .../records/at/{n}, and the position
//   endpoint's answer after an open by id, a save or a toggle change. A record that has
//   left the set keeps the n and N shown, which still count it, and coordinates read
//   before a 409 never replace them;
// - Previous and Next count from the move still running, once its load has read N in
//   that N, then from the slot a record left the active set by, then from n. They are
//   available exactly when a press would move.
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
  /** previous() would move: counted from where it counts, a pending move's target included. */
  readonly canPrevious: boolean;
  /** next() would move: counted from where it counts, a pending move's target included. */
  readonly canNext: boolean;
  /**
   * previous() would move from the record shown, were no move pending: canPrevious before
   * the pending move started. A save the pending move waits on keeps that record open, so
   * the navigator shows its edges meanwhile.
   */
  readonly canPreviousFromShown: boolean;
  /** next() would move from the record shown, were no move pending, as canPreviousFromShown. */
  readonly canNextFromShown: boolean;
  readonly canLast: boolean;
  readonly canGoTo: boolean;
  readonly first: () => Promise<void>;
  /**
   * From New, opens the last record; at position 1 it does nothing. While a move is
   * pending, it counts from that move's target, in the N its load or 404 recovery has
   * read once one has, so it never repeats a destination a 404 replaced.
   */
  readonly previous: () => Promise<void>;
  /**
   * At position N, and from New, it does nothing. While a move is pending, it counts from
   * that move's target, as previous() does.
   */
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
  /**
   * The editor's onConflict: a save found the open record deleted (409), and it stays open
   * read-only at the n and N shown. Nothing is sent: the grid reloads, and the next move or
   * New re-reads N. With the active set selected the record has left it, so Next opens the
   * record that followed it, which now stands at n. Coordinates read before the conflict
   * never replace that: a refresh still in flight is superseded.
   */
  readonly afterConflict: () => void;
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
  | 'afterConflict'
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
  /**
   * The latest intent while it is a running move; `null` otherwise. Internal, as `vacated`
   * is: Previous and Next count from both (originOf), while RecordNavigation shows the
   * installed record until a move settles.
   */
  readonly pendingMove: PendingMove | null;
  /** The slot the open record left the active set by; it counts only while kept (originOf). */
  readonly vacated: VacatedSlot | null;
  /** N of the set the open record left, as a refresh read it; it counts only while kept (knownSizeOf). */
  readonly knownSize: KnownSize | null;
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
  pendingMove: null,
  vacated: null,
  knownSize: null,
};

/** The state a form starts in: nothing open, with the deleted toggle as `options` asks. */
function initialStateOf(options: UseRecordNavigationOptions): NavigationState {
  return { ...INITIAL_STATE, includeDeleted: options.initialIncludeDeleted === true };
}

/** Legacy SharedStrings.NEW_RECORD. */
const NEW_RECORD_TEXT = 'New record';
const NO_RECORDS_TO_GO_TO = 'There are no records to go to.';
/** Shown with the nearest record, or New, that replaces a deleted record the active set hides. */
const DELETED_HIDDEN_TEXT = 'That record is deleted, and deleted records are hidden while Show deleted is off.';

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

/** The move still running: where it lands unless a newer operation supersedes it. */
interface PendingMove {
  readonly target: MoveTarget;
  /**
   * N of the set `target` lies in, once the load serving the move has read it, whether the
   * move's own or the 404 recovery that took over from it; `null` while the N shown holds.
   */
  readonly count: number | null;
}

/**
 * The slot of an open record that left the active set while shown, a save having found it
 * deleted (409) or a refresh having placed it outside the set, and the record generation
 * it holds for.
 */
interface VacatedSlot {
  readonly generation: number;
  readonly position: number;
}

/**
 * N of the selected set as the position endpoint counted it while placing the open record
 * outside that set, and the record generation and toggle value it holds for. The N shown
 * still counts the record; this one does not.
 */
interface KnownSize {
  readonly generation: number;
  readonly includeDeleted: boolean;
  readonly count: number;
}

/**
 * What Previous and Next count from instead of the record shown, first to last in
 * precedence. `pending` is the move still running. `vacated` is the slot an open record
 * left when it left the active set while shown: N still counts that record, and the record
 * that followed it now stands at the slot. `size` is the N a refresh read once the record
 * had left, the set those moves go through; `null` when none is kept.
 */
interface MoveOrigin {
  readonly pending: PendingMove | null;
  readonly vacated: number | null;
  readonly size: number | null;
}

/** The installed record, or New, and its place in the selected set: what originOf and resolveMove read. */
type ShownPlace = Pick<NavigationState, 'n' | 'count' | 'isNew' | 'includeDeleted' | 'recordGeneration'>;

/**
 * The N `knownSize` holds while it is kept, the record it was read for still shown with
 * the same toggle value; `null` otherwise. Every load, install, save and conflict
 * retires it, so the next move or New reads the set as before.
 */
function knownSizeOf(knownSize: KnownSize | null, shown: ShownPlace): number | null {
  return knownSize !== null &&
    knownSize.generation === shown.recordGeneration &&
    knownSize.includeDeleted === shown.includeDeleted
    ? knownSize.count
    : null;
}

/**
 * What Previous and Next count from: the pending move, and the vacated slot only while it
 * is kept, its record still shown at that position with the active set selected, so any
 * install, toggle change or position refresh retires it; the known size likewise.
 */
function originOf(
  pendingMove: PendingMove | null,
  vacated: VacatedSlot | null,
  knownSize: KnownSize | null,
  shown: ShownPlace,
): MoveOrigin {
  const kept =
    vacated !== null &&
    vacated.generation === shown.recordGeneration &&
    vacated.position === shown.n &&
    !shown.includeDeleted;
  return { pending: pendingMove, vacated: kept ? vacated.position : null, size: knownSizeOf(knownSize, shown) };
}

/** The position one step from `from` in a set of `size` records; `null` past either end. */
function stepFrom(move: 'previous' | 'next', from: number, size: number): MoveTarget | null {
  const position = move === 'next' ? from + 1 : from - 1;
  return position >= 1 && position <= size ? { kind: 'position', position } : null;
}

/** The target of a move from `state` and `origin`, or `null` for a move that does nothing. */
function resolveMove(move: Move, state: ShownPlace, origin: MoveOrigin): MoveTarget | null {
  const { n, count, isNew } = state;
  if (count === 0) {
    return null;
  }
  if (move === 'previous' || move === 'next') {
    const { pending, vacated } = origin;
    // While the slot is kept, the set moved through no longer holds the deleted record N
    // counts; a size read since the record left is that set's own.
    const size = origin.size ?? (vacated === null ? count : count - 1);
    if (pending !== null) {
      // Once a load has read N for the pending move, its target lies in that set.
      const within = pending.count ?? size;
      const { target } = pending;
      return stepFrom(move, target.kind === 'last' ? within : target.position, within);
    }
    if (vacated !== null) {
      // The slot lies between vacated - 1 and vacated, so Next opens vacated and Previous
      // the one before; in a set that has shrunk below the slot, it follows the last record.
      const slot = Math.min(vacated, size + 1);
      return move === 'next' ? stepFrom(move, slot - 1, size) : stepFrom(move, slot, size);
    }
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
  /**
   * The move state.pendingMove belongs to, until that move ends: its intent, and the
   * generation of the load serving it, whether the move's own or the 404 recovery that took
   * over from it; only that load resolves where the move lands (landMove). Any newer intent
   * supersedes it (nextIntent).
   */
  let moving: { readonly token: number; readonly gen: number | null } | null = null;
  let active = false;
  let activeViewId: number | null = null;
  /**
   * Set by afterSave, since a create grows the set; by leftSet, since the open record has
   * left the set N counts; and while a 404, delete or discard landing runs, since N may
   * have changed: the next move or New re-reads N, unless a known size stands for it.
   */
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
  /**
   * The coordinates refresh in flight, refreshAfterSave's or refreshCoordinates': its load
   * generation, and whether it shows `loading`. Its reads predate any conflict reported
   * meanwhile, so afterConflict supersedes it while it is the latest load.
   */
  let coordinatesRefresh: { readonly gen: number; readonly showsLoading: boolean } | null = null;
  /**
   * +1 on every afterConflict. A toggle to the active set whose reads were sent before a
   * conflict cannot trust a position that still places the open record in that set.
   */
  let conflicts = 0;
  /**
   * +1 on every afterSave. A toggle whose leave or reads overlap a save reads the set
   * again, since the save may have moved the open record into or out of it.
   */
  let saves = 0;

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

  /**
   * Starts a load that supersedes every load in flight and aborts its requests; Retry runs
   * `repeat`. The load may change the set, so a known size no longer stands for N.
   */
  function begin(repeat: () => Promise<unknown>): Load {
    const load = nextLoad();
    lastLoad = repeat;
    retrying = null;
    host.update({ loading: true, loadError: null, knownSize: null });
    return load;
  }

  /** The N a refresh read once the open record left the set, while it is kept (knownSizeOf). */
  function knownSize(): number | null {
    const state = host.state();
    return knownSizeOf(state.knownSize, state);
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

  /** Takes the next intent: a pending move is superseded, so Previous and Next no longer count from it. */
  function nextIntent(): number {
    intent += 1;
    moving = null;
    if (host.state().pendingMove !== null) {
      host.update({ pendingMove: null });
    }
    return intent;
  }

  /**
   * Starts a record-changing operation as the latest intent. `transitioning` is set before
   * anything is awaited, beforeLeave included, so the editor freezes the draft at once.
   */
  function startTransition(): number {
    const token = nextIntent();
    claimTransition(token);
    return token;
  }

  /**
   * Ends the operation `token`. Only the latest intent clears `transitioning`, since a
   * newer one owns it, and only it settles a deferred after-save refresh, which runs when
   * the operation installed nothing and left N stale. When the operation failed, or is a
   * Retry that stopped at its leave, only the saved record's coordinates are re-read, so
   * the failure and its Retry stay shown.
   */
  function endTransition(token: number): void {
    if (moving?.token === token) {
      moving = null;
      host.update({ pendingMove: null });
    }
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
   * beforeLeave has resolved `true` while this is still the latest intent. A move passes
   * its `target`, which Previous and Next count from until the move ends.
   */
  async function guarded(body: () => Promise<boolean>, target?: MoveTarget): Promise<boolean> {
    const token = startTransition();
    if (target !== undefined) {
      moving = { token, gen: null };
      host.update({ pendingMove: { target, count: null } });
    }
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

  function installRecord(record: RecordDto, n: number, set: SetView, notice?: string): void {
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
      ...(notice === undefined ? {} : { notice }),
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
   * Opens the record at `position`; n is the requested position, and `notice` shows with
   * the record it installs. A 404 shows "This record no longer exists." and opens the
   * record nearest to `position` once: when `recovering` is set this already is that
   * attempt, and a second 404 installs New instead of looping. `landed`, when given,
   * replaces that notice once the recovery knows where it lands.
   */
  async function openAt(
    load: Load,
    position: number,
    set: SetView,
    recovering: boolean,
    notice?: string,
    landed?: LandingNotice,
  ): Promise<boolean> {
    const result = await settle(readAt(load, position, set.includeDeleted));
    return installAt(load, result, position, set, recovering, notice, landed);
  }

  /** Installs the answer `result` of `load`'s at/{position} read, or recovers from its 404, as openAt does. */
  async function installAt(
    load: Load,
    result: Settled<RecordDto>,
    position: number,
    set: SetView,
    recovering: boolean,
    notice?: string,
    landed?: LandingNotice,
  ): Promise<boolean> {
    if (!isCurrent(load)) {
      return false;
    }
    if (result === null) {
      settleIdle();
      return false;
    }
    if (result.ok) {
      installRecord(result.data, position, set, notice);
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

  /**
   * Opens `position`, or installs New when the set is empty; `notice` shows with either,
   * and `landed` is as for openAt.
   */
  function openOrNew(
    load: Load,
    position: number,
    set: SetView,
    recovering: boolean,
    notice?: string,
    landed?: LandingNotice,
  ): Promise<boolean> {
    if (set.count === 0) {
      installNew(set, notice);
      return Promise.resolve(false);
    }
    return openAt(load, position, set, recovering, notice, landed);
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
    // A 404 on the load serving a move: this landing takes over from that load, so where it
    // lands is where the move now lands.
    const served = moving !== null && moving.gen === loadGen ? moving.token : null;
    // N may have changed, so a move that supersedes this landing re-reads it; the landing's
    // own install marks it fresh again.
    countStale = true;
    const listVersion = host.state().listVersion + 1;
    host.update(notice === null ? { listVersion } : { listVersion, notice });
    const run = async (): Promise<boolean> => {
      // The record still shown may have been edited since a failure, so Retry leaves it first.
      const load = begin(() => guarded(run));
      if (served !== null) {
        serveMove(served, load);
      }
      const set = await readSet(load, includeDeleted);
      if (set === null) {
        return false;
      }
      const landing = nearest(anchor, set.count);
      landMove(load, landing, set.count);
      if (landed !== undefined) {
        host.update({ notice: landed(set.count === 0 ? null : landing) });
      }
      return openOrNew(load, landing, set, recovering);
    };
    return run();
  }

  /**
   * Mount and form change: N and the initial position, read together, or New on an empty
   * form. No leave. A position that cannot be opened as asked gets a notice naming it as
   * written (`initialPositionText`) and saying what opened instead. On a non-empty set,
   * every 404 of the `at` read, a position past the end included, runs the 404 recovery an
   * open runs: the grid reloads, N is read again and the record nearest to the position
   * opens, or New on a set found empty or after a second 404. Its notice names the last
   * record when that landing falls short of the position asked for. A record found at a
   * position N does not count, on an empty set too, means the set grew meanwhile: N is
   * read again and the record shows.
   */
  function loadInitial(includeDeleted: boolean): Promise<boolean> {
    const { initialPosition: requested, initialPositionText } = host.options();
    const valid = requested !== undefined && Number.isInteger(requested) && requested >= 1;
    const position = valid ? requested : 1;
    // What a notice names: the position as written, else as given; with neither, there is no notice.
    const asked = initialPositionText ?? (requested === undefined ? undefined : String(requested));
    const run = async (): Promise<boolean> => {
      const load = begin(run);
      // Only the meaning of an at/{position} 404 depends on N, so neither read waits for the other.
      const results = await settleAll(load, [readCount(load, includeDeleted), readAt(load, position, includeDeleted)]);
      if (!isCurrent(load)) {
        return false;
      }
      if (results === null) {
        settleIdle();
        return false;
      }
      const [counted, opened] = results;
      if (!counted.ok) {
        fail(counted.error);
        return false;
      }
      if (opened.ok) {
        // The record found shows at its position, whatever N the count beside it gave.
        const notice = asked !== undefined && !valid ? notARecordNumber(asked, false) : undefined;
        if (position <= counted.data.count) {
          installRecord(opened.data, position, { includeDeleted, count: counted.data.count, fresh: true }, notice);
          return true;
        }
        // N does not count the record found, so the set grew between the two reads: N is
        // read again for it.
        const grown = await readSet(load, includeDeleted);
        if (grown === null) {
          return false;
        }
        if (grown.count >= position) {
          installRecord(opened.data, position, grown, notice);
          return true;
        }
        // N is still behind: the record's position stands for N until the next move or New
        // reads N again.
        countStale = true;
        installRecord(opened.data, position, { includeDeleted, count: position, fresh: false }, notice);
        return true;
      }
      const set: SetView = { includeDeleted, count: counted.data.count, fresh: true };
      if (set.count === 0) {
        // Nothing to open, whatever at/{position} answered.
        let notice: string | undefined;
        if (asked !== undefined) {
          notice = valid ? noSuchRecord(asked, null) : notARecordNumber(asked, true);
        }
        installNew(set, notice);
        return false;
      }
      if (asked !== undefined && !valid) {
        host.update({ notice: notARecordNumber(asked, false) });
      }
      // A landing short of the position asked for is the last record. One at it means the
      // record there went away after N was read, which the 404 notice alone describes.
      const landed: LandingNotice | undefined =
        asked === undefined || !valid
          ? undefined
          : (landing) =>
              landing !== null && landing >= position
                ? errorMessages.notFound
                : `${errorMessages.notFound} ${noSuchRecord(asked, landing)}`;
      return installAt(load, opened, position, set, false, undefined, landed);
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
   * A user move: leave the open record, then open the target; N is reused unless it is
   * stale (countStale), and then a known size stands for it before N is read again. Retry
   * repeats the whole move, so an edit made since a failure is saved before the target is
   * read.
   */
  function transition(target: MoveTarget): Promise<boolean> {
    return guarded(async () => {
      host.update({ goToError: null, notice: null });
      const { includeDeleted } = host.state();
      // Taken before this load begins, which retires it.
      const known = knownSize();
      const positionIn = (count: number): number => (target.kind === 'last' ? count : target.position);
      const load = begin(() => transition(target));
      // beforeLeave has just resolved while this move is the latest intent.
      serveMove(intent, load);
      const { count } = host.state();
      if (!countStale && count > 0) {
        return openAt(load, positionIn(count), { includeDeleted, count, fresh: false }, false);
      }
      const set: SetView | null =
        known === null ? await readSet(load, includeDeleted) : { includeDeleted, count: known, fresh: true };
      if (set === null) {
        return false;
      }
      const position = positionIn(set.count);
      landMove(load, position, set.count);
      return openOrNew(load, position, set, false);
    }, target);
  }

  /** `load` now serves the pending move of intent `token`, while that move is still pending. */
  function serveMove(token: number, load: Load): void {
    if (moving !== null && moving.token === token) {
      moving = { token, gen: load.gen };
    }
  }

  /**
   * The load serving the pending move has read N: the move lands at `position` of a set of
   * `count` records, or at its last record when `position` lies past it, as the 404
   * recovery then lands. Previous and Next count from there until the move ends.
   */
  function landMove(load: Load, position: number, count: number): void {
    if (moving !== null && moving.gen === load.gen) {
      host.update({ pendingMove: { target: { kind: 'position', position: nearest(position, count) }, count } });
    }
  }

  function move(kind: Move): Promise<boolean> {
    const state = host.state();
    const target = resolveMove(kind, state, originOf(state.pendingMove, state.vacated, state.knownSize, state));
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
    // k is accepted, so an earlier rejection no longer describes the last goTo, even when
    // the leave below is refused and the transition never runs.
    if (host.state().goToError !== null) {
      host.update({ goToError: null });
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
      // A known size is the N New shows, so N is not read again.
      const known = knownSize();
      host.update({ goToError: null, notice: null });
      installNew(
        known === null ? { includeDeleted, count, fresh: false } : { includeDeleted, count: known, fresh: true },
      );
      // New is open, so the draft is the user's again while N is re-read.
      endTransition(token);
      if (stale && known === null) {
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
      const results = await settleAll(load, [readRecord(load, id), readPosition(load, id, includeDeleted)]);
      if (!isCurrent(load)) {
        return false;
      }
      if (results === null) {
        settleIdle();
        return false;
      }
      // A 404 of either read recovers through openNearestTo, whose count read tells a record
      // that is gone from a form that is gone.
      const [found, placed] = results;
      if (!found.ok) {
        await failOpen(found.error, anchor, includeDeleted);
        return false;
      }
      if (!placed.ok) {
        await failOpen(placed.error, anchor, includeDeleted);
        return false;
      }
      // The position endpoint counts the selected set with its answer, so it gives N as well.
      const set: SetView = { includeDeleted, count: placed.data.count, fresh: true };
      const { position } = placed.data;
      if (position !== null) {
        installRecord(found.data, position, set);
        return true;
      }
      // Outside the selected set, so deleted since the grid or Find listed it: the list has
      // changed, and the grid reloads even when the open below fails. The set's record
      // nearest to the one that was open replaces it, with the notice.
      host.update({ listVersion: host.state().listVersion + 1 });
      return openOrNew(load, nearest(anchor, set.count), set, false, DELETED_HIDDEN_TEXT);
    });
  }

  async function setIncludeDeleted(value: boolean): Promise<void> {
    const before = host.state();
    if (value === before.includeDeleted) {
      return;
    }
    const token = nextIntent();
    // Ends the toggle whichever path it took: it is the latest intent until a newer one starts.
    try {
      const { record } = before;
      if (record === null) {
        host.update({ goToError: null, notice: null });
        // New stays New and only N changes; with nothing open yet the form loads afresh.
        await (before.isNew ? recount(value) : loadInitial(value));
        return;
      }

      // The record is left only once the position endpoint places it outside the selected
      // set, the server's answer to whether the toggle hides it.
      let left = false;
      host.update({ goToError: null, notice: null });
      const anchor = before.n ?? 1;
      const run = async (): Promise<boolean> => {
        // Results apply only on success, so includeDeleted is still the old value when
        // Retry repeats the whole toggle, whose reads again decide whether the record is left.
        const load = begin(() => setIncludeDeleted(value));
        const conflictsBefore = conflicts;
        const savesBefore = saves;
        const placed = await settle(readPosition(load, record.globalRecordId, value));
        if (!isCurrent(load)) {
          return false;
        }
        if (placed === null) {
          settleIdle();
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
        const set: SetView = { includeDeleted: value, count: placed.data.count, fresh: true };
        // A save found the open record deleted after these reads were sent, so a position in
        // the active set may predate the delete: the record is left and the set read again,
        // as when the position is null. With deleted records shown every row stays in the
        // set, so those reads still hold.
        const predatesConflict = !value && conflicts !== conflictsBefore;
        const position = predatesConflict ? null : placed.data.position;
        if (position !== null) {
          // The open record stays: only n and N change, so the generation does not.
          countStale = false;
          host.update({ n: position, count: set.count, includeDeleted: value, vacated: null, loading: false });
          return true;
        }
        if (!left) {
          // Outside the selected set: the nearest record replaces it, so it is left like any
          // other transition, its draft drained and saved.
          host.update({ loading: false });
          claimTransition(token);
          if (!(await leave(token))) {
            return false;
          }
          left = true;
          if (saves !== savesBefore || conflicts !== conflictsBefore) {
            // A save or a conflict since these reads were sent may have moved the record, so
            // the set is read again.
            return run();
          }
          host.update({ loading: true });
        }
        // The deleted record is hidden now: the nearest active record, or New, replaces it, with the notice.
        return openOrNew(load, nearest(anchor, set.count), set, false, DELETED_HIDDEN_TEXT);
      };
      await run();
    } finally {
      endTransition(token);
    }
  }

  function afterSave(record: RecordDto): void {
    saves += 1;
    const before = host.state();
    countStale = true;
    // The same on-screen record, so the generation stays and the editor keeps its state.
    // The save may have moved it into the set, so a known size no longer stands for N.
    host.update({
      record,
      isNew: false,
      n: before.isNew ? null : before.n,
      listVersion: before.listVersion + 1,
      knownSize: null,
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

  /**
   * Re-reads N and the saved record's n; the record stays, so the generation does too. A
   * record the position endpoint places outside the set stays open at the n and N shown,
   * and the N it answers with is kept for the next move or New (leftSet).
   */
  function refreshAfterSave(record: RecordDto, includeDeleted: boolean): Promise<boolean> {
    const run = async (): Promise<boolean> => {
      const load = begin(run);
      coordinatesRefresh = { gen: load.gen, showsLoading: true };
      const placed = await settle(readPosition(load, record.globalRecordId, includeDeleted));
      endCoordinatesRefresh(load);
      if (!isCurrent(load)) {
        return false;
      }
      if (placed === null) {
        settleIdle();
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
      if (placed.data.position === null) {
        host.update({ loading: false });
        leftSet(placed.data.count);
        return false;
      }
      countStale = false;
      host.update({ n: placed.data.position, count: placed.data.count, vacated: null, loading: false });
      return true;
    };
    return run();
  }

  /**
   * Re-reads N and the saved record's n while a failed load is shown: the transition whose
   * leave saved the record failed afterwards, so the record stays open with stale
   * coordinates. The failure, its Retry and `loading` stay as they are. An abort, a failure
   * or a 404 changes nothing, so Retry still re-reads N; any later load supersedes this one
   * and aborts its requests. A record placed outside the set keeps the n and N shown, and
   * the N the answer gives is kept for the next move or New (leftSet).
   */
  async function refreshCoordinates(record: RecordDto, includeDeleted: boolean): Promise<void> {
    const load = nextLoad();
    coordinatesRefresh = { gen: load.gen, showsLoading: false };
    const placed = await settle(readPosition(load, record.globalRecordId, includeDeleted));
    endCoordinatesRefresh(load);
    if (!isCurrent(load) || placed === null || !placed.ok) {
      return;
    }
    if (placed.data.position === null) {
      leftSet(placed.data.count);
      return;
    }
    countStale = false;
    host.update({ n: placed.data.position, count: placed.data.count, vacated: null, knownSize: null });
  }

  /** The coordinates refresh `load` has its answers, so a conflict no longer supersedes it. */
  function endCoordinatesRefresh(load: Load): void {
    if (coordinatesRefresh?.gen === load.gen) {
      coordinatesRefresh = null;
    }
  }

  /**
   * Supersedes the coordinates refresh in flight while it is the latest load, aborting its
   * requests: its reads were sent before a conflict, so they must not install n and N over
   * it. A newer load, such as a pending toggle, is not touched.
   */
  function supersedeCoordinatesRefresh(): void {
    const refresh = coordinatesRefresh;
    coordinatesRefresh = null;
    if (refresh === null || refresh.gen !== loadGen) {
      return;
    }
    loadGen += 1;
    abortLoad();
    if (refresh.showsLoading) {
      settleIdle();
    }
  }

  /**
   * The open record has left the selected set, a save having found it deleted (409) or a
   * refresh having placed it outside the set, and it stays open at the n and N shown, which
   * still count it. The grid reloads. `count` is the set's N the refresh read with that
   * answer, which the next move or New uses (knownSize); after a 409 it is `null`, and they
   * re-read N. With the active set selected the record that followed it now stands at n,
   * so its slot is kept for Previous and Next (resolveMove).
   */
  function leftSet(count: number | null): void {
    const { n, includeDeleted, recordGeneration, listVersion } = host.state();
    countStale = true;
    host.update({
      listVersion: listVersion + 1,
      vacated: !includeDeleted && n !== null ? { generation: recordGeneration, position: n } : null,
      knownSize: count === null ? null : { generation: recordGeneration, includeDeleted, count },
    });
  }

  /** Does not call beforeLeave: the delete already discarded the draft. */
  function afterDelete(): void {
    const { n, includeDeleted } = host.state();
    host.update({ goToError: null, notice: null });
    void replaceWithoutLeave(() => openNearestTo(n ?? 1, includeDeleted, null, false));
  }

  /**
   * Sends nothing: the record stays open, read-only. The next move or New re-reads N, and
   * the grid reloads, since the record has left the active set or now shows as deleted
   * (leftSet). Coordinates read before the conflict never replace that: a refresh in
   * flight is superseded, a deferred one dropped, and a toggle to the active set in flight
   * reads the set again once the record is left.
   */
  function afterConflict(): void {
    const { record, isNew } = host.state();
    if (record === null || isNew) {
      return;
    }
    conflicts += 1;
    supersedeCoordinatesRefresh();
    deferredRefresh = null;
    leftSet(null);
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
      afterConflict,
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
      nextIntent();
      leavingToken = null;
      deferredRefresh = null;
    },
  };
}

function statusTextOf({ isNew, n, count }: ShownPlace): string {
  if (isNew) {
    return NEW_RECORD_TEXT;
  }
  return n === null ? '' : `${n} of ${count}`;
}

function present(state: NavigationState, actions: NavigationActions): RecordNavigation {
  // The move origin stays internal, and the status shows the installed record until a move
  // settles, while Previous and Next are available exactly when a press would move. Their
  // FromShown forms leave the pending move out and count from the record shown.
  const { pendingMove, vacated, knownSize, ...shown } = state;
  const origin = originOf(pendingMove, vacated, knownSize, shown);
  const here = originOf(null, vacated, knownSize, shown);
  const hasRecords = shown.count > 0;
  return {
    ...shown,
    statusText: statusTextOf(shown),
    // First and Last stay enabled at either end, where they reopen that record.
    canFirst: hasRecords,
    canPrevious: resolveMove('previous', shown, origin) !== null,
    canNext: resolveMove('next', shown, origin) !== null,
    canPreviousFromShown: resolveMove('previous', shown, here) !== null,
    canNextFromShown: resolveMove('next', shown, here) !== null,
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
 *   onSaved: navigation.afterSave, onDeleted: navigation.afterDelete, onConflict: navigation.afterConflict,
 *   onDiscardToNearest: navigation.openNearest, transitioning: navigation.transitioning,
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
