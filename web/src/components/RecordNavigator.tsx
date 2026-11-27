// Record navigation toolbar of the open form: First, Previous, "[n] of N", Next, Last, the
// record number box and New, replacing the navigation toolstrip of legacy EnterMainForm.
// Positions, the set the deleted toggle selects, the edge rules and every request belong
// to useRecordNavigation, whose transitions first await the editor's leave() (save before
// leaving). This component renders that state and calls its actions, and keeps focus in
// the toolbar when an edge disables the move that held it.
import { useId, useLayoutEffect, useRef, useState, type FocusEvent, type KeyboardEvent } from 'react';

import type { RecordNavigation } from '../state/useRecordNavigation';

/**
 * Keeps focus inside a control group when its focused `button` has just been disabled by an
 * edge of its range, such as Next on the last record or "Earlier records" on the first page.
 * The browser would drop that focus to the page body, where the next key press does nothing,
 * and App's focus return gives focus back only to a button that is enabled again, which an
 * edge never does. Focus moves to the first of `candidates` that can take it, but only while
 * it is still on the button, or fell to the page body from it: `lastFocused` is the group's
 * element that last held focus. Focus the user moved anywhere else stays there. RecordGrid's
 * paging buttons use it as well.
 */
export function keepFocusOffDisabled(
  button: HTMLButtonElement | null,
  lastFocused: Element | null,
  candidates: readonly (HTMLElement | null)[],
): void {
  if (button === null || !button.disabled) {
    return;
  }
  const active = document.activeElement;
  const held = active === button || ((active === null || active === document.body) && lastFocused === button);
  if (!held) {
    return;
  }
  for (const candidate of candidates) {
    if (candidate !== null && candidate.isConnected && !(candidate instanceof HTMLButtonElement && candidate.disabled)) {
      candidate.focus();
      return;
    }
  }
}

/**
 * The blur handler of a group whose focus handler records, in `lastFocused`, the element that
 * last held focus. Focus moved elsewhere, or put down while the element is enabled, clears
 * it; a button that loses focus because it was disabled stays named, so
 * {@link keepFocusOffDisabled} can tell that the focus on the page body came from it.
 */
export function forgetGroupFocus(lastFocused: { current: Element | null }, event: FocusEvent<HTMLElement>): void {
  if (event.relatedTarget !== null || !(event.target instanceof HTMLButtonElement && event.target.disabled)) {
    lastFocused.current = null;
  }
}

/** The navigation members the toolbar reads and the actions it calls. */
type NavigatorState = Pick<
  RecordNavigation,
  | 'count'
  | 'statusText'
  | 'goToError'
  | 'loading'
  | 'transitioning'
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

/** The box text a Go or Enter last submitted, and the record open when it was submitted. */
interface Submission {
  readonly text: string;
  readonly recordGeneration: number;
  /** This component's rejection of text that is no record number; null once the number went to the hook. */
  readonly message: string | null;
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
  const [submission, setSubmission] = useState<Submission | null>(null);

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

  // A rejection describes the text that was submitted, on the record open at the time, as
  // Find's criterion messages do. It shows, with aria-invalid and aria-describedby, only while
  // the box holds exactly that text and that record stays open: any edit, clearing included,
  // hides it, and so does a move, New or a reload, which installs another record. Submitting
  // again shows the new rejection, whether this component's own or the hook's goToError.
  const standing =
    submission !== null && submission.text === entry && submission.recordGeneration === navigation.recordGeneration
      ? submission
      : null;
  const error = standing === null ? null : (standing.message ?? navigation.goToError);

  // Next reaching the last record, or Previous the first, is disabled for good while it holds
  // focus: focus goes to the move the other way, else to Last or First, which stay enabled
  // while the set holds a record. Moved in the layout phase of the commit that disables the
  // button, before the browser paints. A save disables every control only while it runs, and
  // App's focus return gives that focus back, so nothing moves while busy. Nor while a move is
  // in flight: Previous and Next count from its target, so a move its leave then refuses would
  // look like an edge for a render. Once it has landed, `lastFocusedRef` still names the
  // button whose focus fell to the page body, so a move that did reach the edge is handled then.
  const firstRef = useRef<HTMLButtonElement>(null);
  const previousRef = useRef<HTMLButtonElement>(null);
  const nextRef = useRef<HTMLButtonElement>(null);
  const lastRef = useRef<HTMLButtonElement>(null);
  const lastFocusedRef = useRef<Element | null>(null);
  const { canNext, canPrevious, transitioning } = navigation;
  useLayoutEffect(() => {
    if (busy || transitioning) {
      return;
    }
    const lastFocused = lastFocusedRef.current;
    if (!canNext) {
      keepFocusOffDisabled(nextRef.current, lastFocused, [previousRef.current, lastRef.current]);
    }
    if (!canPrevious) {
      keepFocusOffDisabled(previousRef.current, lastFocused, [nextRef.current, firstRef.current]);
    }
  });

  function submitGoTo(): void {
    if (goToDisabled) {
      return;
    }
    const submitted = entry;
    const trimmed = submitted.trim();
    const { recordGeneration } = navigation;
    if (!RECORD_NUMBER_PATTERN.test(trimmed)) {
      setSubmission({ text: submitted, recordGeneration, message: rangeMessage(navigation.count) });
      return;
    }
    setSubmission({ text: submitted, recordGeneration, message: null });
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
        onFocus={(event) => {
          lastFocusedRef.current = event.target;
        }}
        onBlur={(event) => {
          forgetGroupFocus(lastFocusedRef, event);
        }}
      >
        {/* The toolbar wraps only between these groups, in reading order: First and
            Previous, the status, Next and Last, then the go-to entry with New after it. The
            wrappers are unnamed generic elements, so every accessible name stays the
            control's own. */}
        <div className="record-navigator__moves">
          <button
            ref={firstRef}
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
            ref={previousRef}
            type="button"
            disabled={previousDisabled}
            {...blockedMarking(previousDisabled)}
            onClick={() => {
              void navigation.previous();
            }}
          >
            Previous
          </button>
        </div>
        {/* Busy while a load is pending, so the coordinates it still shows are not announced
            as current; the text itself stays the hook's statusText. */}
        <span role="status" aria-live="polite" aria-busy={pending ? true : undefined}>
          {navigation.statusText}
        </span>
        <div className="record-navigator__moves">
          <button
            ref={nextRef}
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
            ref={lastRef}
            type="button"
            disabled={lastDisabled}
            {...blockedMarking(lastDisabled)}
            onClick={() => {
              void navigation.last();
            }}
          >
            Last
          </button>
        </div>
        {/* New wraps below Go only when the two cannot share a row; the label, its box and Go
            never part. */}
        <div className="record-navigator__entry">
          <div className="record-navigator__go-to">
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
          </div>
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
        </div>
        {/* Last child: .record-navigator .field-error spans the full width on its own row,
            which placed earlier would push the groups after it onto a row of their own. */}
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
