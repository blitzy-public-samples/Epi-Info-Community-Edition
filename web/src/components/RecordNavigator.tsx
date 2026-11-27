// Record navigation toolbar of the open form: First, Previous, "[n] of N", Next, Last, the
// record number box and New, replacing the navigation toolstrip of legacy EnterMainForm.
// Positions, the set the deleted toggle selects, the edge rules and every request belong
// to useRecordNavigation, whose transitions first await the editor's leave() (save before
// leaving). This component renders that state and calls its actions, nothing more.
import { useId, useState, type KeyboardEvent } from 'react';

import type { RecordNavigation } from '../state/useRecordNavigation';

/** The navigation members the toolbar reads and the actions it calls. */
type NavigatorState = Pick<
  RecordNavigation,
  | 'count'
  | 'statusText'
  | 'goToError'
  | 'loading'
  | 'recordGeneration'
  | 'canFirst'
  | 'canPrevious'
  | 'canNext'
  | 'canLast'
  | 'canGoTo'
  | 'first'
  | 'previous'
  | 'next'
  | 'last'
  | 'goTo'
  | 'newRecord'
>;

export interface RecordNavigatorProps {
  /** State and actions of the open form, as `useRecordNavigation` returns them. */
  navigation: NavigatorState;
  /** The editor is saving: every control is disabled until it settles. */
  busy?: boolean;
  /**
   * Every move would first save a dirty draft that a write of unknown outcome blocks, so it
   * would refuse. Each button not already disabled is marked unavailable (aria-disabled and
   * the blocked styling), yet stays operable: its press runs the move, whose refused save
   * shows the editor's banner again.
   */
  blocked?: boolean;
  /**
   * Form-definition insert permission. Only `false` disables New; `null` or absent means
   * no restriction, which holds until permission awareness is delivered.
   */
  canInsert?: boolean | null;
}

/** How a move button is marked while `blocked` holds and the button is not disabled. */
const BLOCKED_BUTTON_PROPS = { 'aria-disabled': true, className: 'button--blocked' } as const;

/** An optional sign, then ASCII digits only: "1e3", "2.0" and "0x10" are not record numbers. */
const RECORD_NUMBER_PATTERN = /^[+-]?\d+$/;

/** The text useRecordNavigation reports for a number outside 1..N, so both read alike. */
function rangeMessage(count: number): string {
  return `Enter a record number from 1 to ${count}.`;
}

/** A rejected entry, kept only while the record it was typed on stays open. */
interface EntryError {
  readonly message: string;
  readonly recordGeneration: number;
}

/**
 * The record navigation toolbar. Its accessible names (First, Previous, Next, Last, Go,
 * New, "Go to record") are matched by the browser smoke test, and no other control here
 * may contain First, Previous, Next, Last, New, Save, Delete or Find: Playwright matches
 * names by substring.
 */
export function RecordNavigator({ navigation, busy = false, blocked = false, canInsert = null }: RecordNavigatorProps) {
  const inputId = useId();
  const errorId = `${inputId}-error`;
  const [entry, setEntry] = useState('');
  const [entryError, setEntryError] = useState<EntryError | null>(null);

  // A pending load disables nothing: useRecordNavigation drops the responses of a load a
  // newer action overtakes, so a move, Go or New pressed mid-load supersedes it, and the
  // load is announced instead. A control is disabled only while its operation is
  // unavailable: an edge or an empty set (canX false), a save in progress (busy), or, for
  // New, a denied insert permission.
  const pending = navigation.loading;
  const firstDisabled = !navigation.canFirst || busy;
  const previousDisabled = !navigation.canPrevious || busy;
  const nextDisabled = !navigation.canNext || busy;
  const lastDisabled = !navigation.canLast || busy;
  const goToDisabled = !navigation.canGoTo || busy;
  // Legacy GotoNewRecord runs only with canInsertRecords; New stays enabled on an empty form.
  const newDisabled = busy || canInsert === false;
  // A disabled button already says it is unavailable; only an enabled one takes the marking.
  const blockedMarking = (disabled: boolean) => (blocked && !disabled ? BLOCKED_BUTTON_PROPS : {});

  // A move, New or a reload installs another record and leaves an earlier entry's
  // message stale; the hook clears its own goToError on the same transitions.
  const localError =
    entryError !== null && entryError.recordGeneration === navigation.recordGeneration ? entryError.message : null;
  const error = localError ?? navigation.goToError;

  function submitGoTo(): void {
    if (goToDisabled) {
      return;
    }
    const submitted = entry;
    const trimmed = submitted.trim();
    if (!RECORD_NUMBER_PATTERN.test(trimmed)) {
      setEntryError({ message: rangeMessage(navigation.count), recordGeneration: navigation.recordGeneration });
      return;
    }
    setEntryError(null);
    // The hook rejects a number outside 1..N without a request and reports goToError.
    void navigation.goTo(Number.parseInt(trimmed, 10)).then((opened) => {
      if (opened) {
        // Text typed while the move ran is the user's next entry and stays.
        setEntry((current) => (current === submitted ? '' : current));
      }
    });
  }

  function handleEntryKeyDown(event: KeyboardEvent<HTMLInputElement>): void {
    if (event.key !== 'Enter' || event.nativeEvent.isComposing) {
      return;
    }
    // Enter goes to the record and never submits a surrounding form.
    event.preventDefault();
    submitGoTo();
  }

  return (
    <>
      <div
        className="record-navigator"
        role="toolbar"
        aria-label="Record navigation"
        aria-busy={pending || busy ? true : undefined}
      >
        <button
          type="button"
          disabled={firstDisabled}
          {...blockedMarking(firstDisabled)}
          onClick={() => {
            void navigation.first();
          }}
        >
          First
        </button>
        <button
          type="button"
          disabled={previousDisabled}
          {...blockedMarking(previousDisabled)}
          onClick={() => {
            void navigation.previous();
          }}
        >
          Previous
        </button>
        {/* Busy while a load is pending, so the coordinates it still shows are not announced
            as current; the text itself stays the hook's statusText. */}
        <span role="status" aria-live="polite" aria-busy={pending ? true : undefined}>
          {navigation.statusText}
        </span>
        <button
          type="button"
          disabled={nextDisabled}
          {...blockedMarking(nextDisabled)}
          onClick={() => {
            void navigation.next();
          }}
        >
          Next
        </button>
        <button
          type="button"
          disabled={lastDisabled}
          {...blockedMarking(lastDisabled)}
          onClick={() => {
            void navigation.last();
          }}
        >
          Last
        </button>
        <label htmlFor={inputId}>Go to record</label>
        <input
          id={inputId}
          type="text"
          inputMode="numeric"
          enterKeyHint="go"
          autoComplete="off"
          spellCheck={false}
          value={entry}
          disabled={goToDisabled}
          aria-invalid={error !== null ? true : undefined}
          aria-describedby={error !== null ? errorId : undefined}
          onChange={(event) => {
            setEntry(event.target.value);
          }}
          onKeyDown={handleEntryKeyDown}
        />
        <button type="button" disabled={goToDisabled} {...blockedMarking(goToDisabled)} onClick={submitGoTo}>
          Go
        </button>
        <button
          type="button"
          disabled={newDisabled}
          {...blockedMarking(newDisabled)}
          onClick={() => {
            void navigation.newRecord();
          }}
        >
          New
        </button>
        {/* Last child: .record-navigator .field-error spans the full width on its own row,
            which placed earlier would push New onto a row of its own. */}
        {error !== null && (
          <div className="field-error" id={errorId} role="alert">
            <p>{error}</p>
          </div>
        )}
      </div>
      {/* The pending announcement sits beside the toolbar, not in it: assistive technology may
          hold back updates inside an aria-busy subtree until it clears, and the render that
          clears the toolbar's aria-busy also empties this text, so inside it the load would
          never be announced. It is a status message of its own, so it carries role="status";
          the toolbar's one status stays the "[n] of N" text. Always mounted, so the region
          exists before its text changes, and unnamed, since a name on a live region can be
          read in place of its content. */}
      <span className="visually-hidden" role="status" aria-live="polite">
        {pending ? 'Loading…' : ''}
      </span>
    </>
  );
}
