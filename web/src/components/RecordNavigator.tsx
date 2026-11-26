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
   * Form-definition insert permission. Only `false` disables New; `null` or absent means
   * no restriction, which holds until permission awareness is delivered.
   */
  canInsert?: boolean | null;
}

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
export function RecordNavigator({ navigation, busy = false, canInsert = null }: RecordNavigatorProps) {
  const inputId = useId();
  const errorId = `${inputId}-error`;
  const [entry, setEntry] = useState('');
  const [entryError, setEntryError] = useState<EntryError | null>(null);

  const moveBlocked = busy || navigation.loading;
  const goToDisabled = !navigation.canGoTo || busy;
  // Legacy GotoNewRecord runs only with canInsertRecords; New stays enabled on an empty form.
  const newDisabled = busy || canInsert === false;

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
    <div className="record-navigator" role="toolbar" aria-label="Record navigation">
      <button
        type="button"
        disabled={!navigation.canFirst || moveBlocked}
        onClick={() => {
          void navigation.first();
        }}
      >
        First
      </button>
      <button
        type="button"
        disabled={!navigation.canPrevious || moveBlocked}
        onClick={() => {
          void navigation.previous();
        }}
      >
        Previous
      </button>
      <span role="status" aria-live="polite">
        {navigation.statusText}
      </span>
      <button
        type="button"
        disabled={!navigation.canNext || moveBlocked}
        onClick={() => {
          void navigation.next();
        }}
      >
        Next
      </button>
      <button
        type="button"
        disabled={!navigation.canLast || moveBlocked}
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
      <button type="button" disabled={goToDisabled} onClick={submitGoTo}>
        Go
      </button>
      <button
        type="button"
        disabled={newDisabled}
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
  );
}
