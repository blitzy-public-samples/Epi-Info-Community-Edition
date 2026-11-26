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
  /** Repeats the last load; every load is GETs only, so it is always safe to repeat. */
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
  notice: null,
  goToError: null,
  loadError: null,
};

/** Legacy SharedStrings.NEW_RECORD. */
const NEW_RECORD_TEXT = 'New record';
const NO_RECORDS_TO_GO_TO = 'There are no records to go to.';

function goToRange(count: number): string {
  return `Enter a record number from 1 to ${count}.`;
}

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

async function settle<T>(call: () => Promise<ApiResult<T>>): Promise<Settled<T>> {
  try {
    return await call();
  } catch (e) {
    if (isAbortError(e)) {
      return null;
    }
    // The client settles every response itself; any other rejection got no response.
    return { ok: false, error: networkError() };
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

// Superseded responses are dropped by generation rather than aborted: every load takes
// the next generation and applies its results only while it is still the latest. That
// covers StrictMode's repeated effects, fast clicks, and the after-save refresh that the
// transition whose leave() saved the record overtakes.
function createController(host: ControllerHost): Controller {
  /** Generation of the latest load. */
  let loadGen = 0;
  /** The latest user transition; an older one stops once its beforeLeave resolves. */
  let intent = 0;
  let active = false;
  let activeViewId: number | null = null;
  /** Set by afterSave, since a create grows the set: the next move or New re-reads N. */
  let countStale = false;
  /** What retryLoad repeats. */
  let lastLoad: (() => Promise<unknown>) | null = null;

  const api = (): ApiClient => host.options().client;
  const viewId = (): number => host.options().viewId;

  const readCount = (includeDeleted: boolean): Promise<Settled<RecordCountResponse>> =>
    settle(() => api().countRecords(viewId(), { includeDeleted }));
  const readAt = (position: number, includeDeleted: boolean): Promise<Settled<RecordDto>> =>
    settle(() => api().getRecordAt(viewId(), position, { includeDeleted }));
  const readRecord = (id: string): Promise<Settled<RecordDto>> => settle(() => api().getRecord(viewId(), id));
  const readPosition = (id: string, includeDeleted: boolean): Promise<Settled<NavigationPosition>> =>
    settle(() => api().getRecordPosition(viewId(), id, { includeDeleted }));

  const isCurrent = (gen: number): boolean => active && gen === loadGen;

  /** Starts a load that supersedes every load in flight; Retry runs `repeat`. */
  function begin(repeat: () => Promise<unknown>): number {
    loadGen += 1;
    lastLoad = repeat;
    host.update({ loading: true, loadError: null });
    return loadGen;
  }

  /** Ends the current load with `error`, keeping the previous display. */
  function fail(error: ApiError): void {
    host.update({ loading: false, loadError: error });
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
    const left = await host.options().beforeLeave();
    return left && active && token === intent;
  }

  /** Reads N for the load `gen`; `null` when the load was overtaken or failed, the failure shown. */
  async function readSet(gen: number, includeDeleted: boolean): Promise<SetView | null> {
    const counted = await readCount(includeDeleted);
    if (counted === null || !isCurrent(gen)) {
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
   * instead of looping.
   */
  async function openAt(gen: number, position: number, set: SetView, recovering: boolean): Promise<boolean> {
    const result = await readAt(position, set.includeDeleted);
    if (result === null || !isCurrent(gen)) {
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
    await openNearestTo(position, set.includeDeleted, errorMessages.notFound, true);
    return false;
  }

  /** Opens `position`, or installs New when the set is empty. */
  function openOrNew(gen: number, position: number, set: SetView, recovering: boolean): Promise<boolean> {
    if (set.count === 0) {
      installNew(set);
      return Promise.resolve(false);
    }
    return openAt(gen, position, set, recovering);
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
   * so the grid reloads.
   */
  function openNearestTo(
    anchor: number,
    includeDeleted: boolean,
    notice: string | null,
    recovering: boolean,
  ): Promise<boolean> {
    const listVersion = host.state().listVersion + 1;
    host.update(notice === null ? { listVersion } : { listVersion, notice });
    const run = async (): Promise<boolean> => {
      const gen = begin(run);
      const set = await readSet(gen, includeDeleted);
      return set === null ? false : openOrNew(gen, nearest(anchor, set.count), set, recovering);
    };
    return run();
  }

  /** Mount and form change: N, then the initial position, or New on an empty form. No leave. */
  function loadInitial(includeDeleted: boolean): Promise<boolean> {
    const requested = host.options().initialPosition;
    const position = requested !== undefined && Number.isInteger(requested) && requested >= 1 ? requested : 1;
    const run = async (): Promise<boolean> => {
      const gen = begin(run);
      const set = await readSet(gen, includeDeleted);
      return set === null ? false : openOrNew(gen, position, set, false);
    };
    return run();
  }

  /** Re-reads N only; the open record, or New, stays. */
  function recount(includeDeleted: boolean): Promise<boolean> {
    const run = async (): Promise<boolean> => {
      const gen = begin(run);
      const set = await readSet(gen, includeDeleted);
      if (set === null) {
        return false;
      }
      countStale = false;
      host.update({ count: set.count, includeDeleted, loading: false });
      return true;
    };
    return run();
  }

  /** A user move: leave the open record, then open the target; N is reused unless a save made it stale. */
  async function transition(target: MoveTarget): Promise<boolean> {
    const token = ++intent;
    if (!(await leave(token))) {
      return false;
    }
    host.update({ goToError: null, notice: null });
    const { includeDeleted } = host.state();
    const positionIn = (count: number): number => (target.kind === 'last' ? count : target.position);
    const run = async (): Promise<boolean> => {
      const gen = begin(run);
      const { count } = host.state();
      if (!countStale && count > 0) {
        return openAt(gen, positionIn(count), { includeDeleted, count, fresh: false }, false);
      }
      const set = await readSet(gen, includeDeleted);
      return set === null ? false : openOrNew(gen, positionIn(set.count), set, false);
    };
    return run();
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
    const token = ++intent;
    if (!(await leave(token))) {
      return;
    }
    // New overtakes every load in flight, an after-save refresh among them.
    loadGen += 1;
    lastLoad = null;
    const stale = countStale;
    const { count, includeDeleted } = host.state();
    host.update({ goToError: null, notice: null });
    installNew({ includeDeleted, count, fresh: false });
    if (stale) {
      await recount(includeDeleted);
    }
  }

  async function openById(id: string): Promise<void> {
    const token = ++intent;
    if (!(await leave(token))) {
      return;
    }
    host.update({ goToError: null, notice: null });
    const { n, includeDeleted } = host.state();
    const anchor = n ?? 1;
    const run = async (): Promise<boolean> => {
      const gen = begin(run);
      const [found, placed, counted] = await Promise.all([
        readRecord(id),
        readPosition(id, includeDeleted),
        readCount(includeDeleted),
      ]);
      if (found === null || placed === null || counted === null || !isCurrent(gen)) {
        return false;
      }
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
      return openOrNew(gen, nearest(anchor, set.count), set, false);
    };
    await run();
  }

  async function setIncludeDeleted(value: boolean): Promise<void> {
    const before = host.state();
    if (value === before.includeDeleted) {
      return;
    }
    const token = ++intent;
    const { record } = before;
    if (record === null) {
      host.update({ goToError: null, notice: null });
      // New stays New and only N changes; with nothing open yet the form loads afresh.
      await (before.isNew ? recount(value) : loadInitial(value));
      return;
    }

    // Switching to the active set while a deleted record is open leaves that record.
    let left = !value && record.recStatus !== ACTIVE_STATUS;
    if (left && !(await leave(token))) {
      return;
    }
    host.update({ goToError: null, notice: null });
    const anchor = before.n ?? 1;
    const run = async (): Promise<boolean> => {
      const gen = begin(run);
      const [counted, placed] = await Promise.all([
        readCount(value),
        readPosition(record.globalRecordId, value),
      ]);
      if (counted === null || placed === null || !isCurrent(gen)) {
        return false;
      }
      if (!counted.ok) {
        fail(counted.error);
        return false;
      }
      if (!placed.ok) {
        await failOpen(placed.error, anchor, value);
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
        if (!(await leave(token))) {
          return false;
        }
        left = true;
        return run();
      }
      return openOrNew(gen, nearest(anchor, set.count), set, false);
    };
    await run();
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
    const { includeDeleted } = before;
    const run = async (): Promise<boolean> => {
      const gen = begin(run);
      const [counted, placed] = await Promise.all([
        readCount(includeDeleted),
        readPosition(record.globalRecordId, includeDeleted),
      ]);
      if (counted === null || placed === null || !isCurrent(gen)) {
        return false;
      }
      if (!counted.ok) {
        fail(counted.error);
        return false;
      }
      if (!placed.ok) {
        await failOpen(placed.error, host.state().n ?? 1, includeDeleted);
        return false;
      }
      countStale = false;
      host.update({ n: placed.data.position, count: counted.data.count, loading: false });
      return true;
    };
    void run();
  }

  function afterDelete(): void {
    intent += 1;
    const { n, includeDeleted } = host.state();
    host.update({ goToError: null, notice: null });
    void openNearestTo(n ?? 1, includeDeleted, null, false);
  }

  function openNearest(notice?: string): void {
    intent += 1;
    const { n, includeDeleted } = host.state();
    void openNearestTo(n ?? 1, includeDeleted, notice ?? null, true);
  }

  async function retryLoad(): Promise<void> {
    const repeat = lastLoad;
    if (repeat !== null) {
      await repeat();
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
        host.update({ ...INITIAL_STATE, recordGeneration: recordGeneration + 1, listVersion: listVersion + 1 });
      }
      activeViewId = id;
      void loadInitial(host.state().includeDeleted);
    },

    deactivate: () => {
      active = false;
      loadGen += 1;
      intent += 1;
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

  const [state, setState] = useState<NavigationState>(INITIAL_STATE);
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
