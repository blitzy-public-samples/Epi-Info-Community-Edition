import { useEffect, useId, useRef, type KeyboardEvent as ReactKeyboardEvent, type MouseEvent } from 'react';
import { createPortal } from 'react-dom';
import type { DialogMessage } from '../api/types';

export interface MessageDialogProps {
  /** Check-code `DIALOG` messages in execution order; only the first is shown. */
  dialogs: readonly DialogMessage[];
  /** Removes the first message, so the next one, if any, is shown. */
  onDismiss: () => void;
}

const DEFAULT_TITLE = 'Message';
const MODAL_SELECTOR = '[aria-modal="true"]';

/** A `DIALOG` without `TITLETEXT`, or with a blank one, is titled "Message". */
function headingText(title: string | null): string {
  return title === null || title.trim() === '' ? DEFAULT_TITLE : title;
}

// Each message gets its own dialog element, keyed by the message object's
// identity. A screen reader announces a dialog when focus enters it, so a
// dialog reused for the next message, with focus already on its OK button,
// would show the new text without announcing it.
const messageKeys = new WeakMap<DialogMessage, number>();
let nextMessageKey = 0;

function messageKey(message: DialogMessage): number {
  let key = messageKeys.get(message);
  if (key === undefined) {
    nextMessageKey += 1;
    key = nextMessageKey;
    messageKeys.set(message, key);
  }
  return key;
}

/** Whether the element is, or contains, a modal dialog. */
function holdsModal(element: Element): boolean {
  return element.matches(MODAL_SELECTOR) || element.querySelector(MODAL_SELECTOR) !== null;
}

/**
 * The dialog's keys: Enter and Escape dismiss the message, and Tab and
 * Shift+Tab keep focus on OK, the only control. Returns whether the key was
 * handled. Preventing Enter's default also stops a focused OK button from
 * turning the key into a click, which would dismiss a second message. A held
 * key's auto-repeats are swallowed, so they cannot dismiss the messages queued
 * behind this one before they are read.
 */
function handleDialogKey(
  event: KeyboardEvent,
  ok: HTMLButtonElement | null,
  dismiss: () => void,
): boolean {
  if (event.key === 'Enter' || event.key === 'Escape') {
    event.preventDefault();
    if (!event.repeat) {
      dismiss();
    }
    return true;
  }
  if (event.key === 'Tab') {
    event.preventDefault();
    ok?.focus();
    return true;
  }
  return false;
}

/**
 * Shows the check-code `DIALOG` messages one at a time, in order, as a modal
 * message box with a single OK button, as legacy `MsgBox.Show(text, title)` did.
 * It holds no check-code logic and no state: OK, Enter and Escape call
 * `onDismiss`, which removes the first message so the next one appears.
 *
 * Like ConfirmDialog it is a plain role="dialog" element (jsdom has no
 * <dialog>.showModal()) rendered into <body>, so that, while a message is
 * shown, every other child of <body> can be made inert. Focus moves to OK when
 * the first message appears, and returns to the element focused before it once
 * the last one is dismissed.
 */
export function MessageDialog({ dialogs, onDismiss }: MessageDialogProps) {
  const headingId = useId();
  const textId = useId();
  const backdropRef = useRef<HTMLDivElement>(null);
  const okRef = useRef<HTMLButtonElement>(null);

  const current = dialogs.length > 0 ? dialogs[0] : null;
  const open = current !== null;

  // The document listeners live for a whole open session and read the latest callback here.
  const onDismissRef = useRef(onDismiss);
  useEffect(() => {
    onDismissRef.current = onDismiss;
  });

  // Modal isolation for one open session, from the first message to the last.
  // The element focused at open is recorded before focus moves to OK, and OK
  // takes focus before the page is made inert, so the page's blur names OK as
  // its related target. The rest of the page, content added while open
  // included, is made inert; focus that still lands outside (in a browser
  // without inert) is pulled back; Enter, Escape and Tab pressed outside the
  // dialog still act on it.
  useEffect(() => {
    const backdrop = backdropRef.current;
    if (!open || backdrop === null) {
      return undefined;
    }

    const active = document.activeElement;
    const opener =
      active instanceof HTMLElement &&
      active !== document.body &&
      active !== document.documentElement &&
      !backdrop.contains(active)
        ? active
        : null;

    okRef.current?.focus();

    // Only the elements made inert here are released on close, so one that was
    // already inert stays inert. A child added to <body> while open is made
    // inert as well, unless it holds a newer modal dialog: that dialog
    // isolates the page itself, this dialog included.
    const madeInert: Element[] = [];
    const makeInert = (element: Element) => {
      element.setAttribute('inert', '');
      madeInert.push(element);
    };
    for (const child of document.body.children) {
      if (child !== backdrop && !child.hasAttribute('inert')) {
        makeInert(child);
      }
    }
    const observer = new MutationObserver((records) => {
      for (const record of records) {
        for (const node of record.addedNodes) {
          if (
            node instanceof Element &&
            node.parentNode === document.body &&
            node !== backdrop &&
            !node.hasAttribute('inert') &&
            !holdsModal(node)
          ) {
            makeInert(node);
          }
        }
      }
    });
    observer.observe(document.body, { childList: true });

    const isOutside = (target: EventTarget | null) =>
      !(target instanceof Node && backdrop.contains(target));

    // A newer modal dialog owns focus and keys while it is open: while it has
    // made this dialog inert, and for events inside it.
    const yieldsTo = (target: EventTarget | null) => {
      if (backdrop.closest('[inert]') !== null) {
        return true;
      }
      const modal = target instanceof Element ? target.closest(MODAL_SELECTOR) : null;
      return modal !== null && !backdrop.contains(modal);
    };

    const handleFocusIn = (event: FocusEvent) => {
      if (isOutside(event.target) && !yieldsTo(event.target)) {
        okRef.current?.focus();
      }
    };

    // Keys pressed inside the dialog are handled by its own onKeyDown.
    const handleDocumentKeyDown = (event: KeyboardEvent) => {
      if (isOutside(event.target) && !yieldsTo(event.target)) {
        handleDialogKey(event, okRef.current, () => onDismissRef.current());
      }
    };

    document.addEventListener('focusin', handleFocusIn);
    document.addEventListener('keydown', handleDocumentKeyDown);

    return () => {
      observer.disconnect();
      document.removeEventListener('focusin', handleFocusIn);
      document.removeEventListener('keydown', handleDocumentKeyDown);
      // Inert elements cannot take focus, so they are released before focus returns.
      for (const element of madeInert) {
        element.removeAttribute('inert');
      }
      // Focus returns only when it has nowhere else to be: the dismissed OK
      // button left it on <body>. Focus another control or a newer dialog took
      // meanwhile stays where it is.
      const focused = document.activeElement;
      if (
        opener !== null &&
        opener.isConnected &&
        (focused === null || focused === document.body || focused === document.documentElement)
      ) {
        opener.focus();
      }
    };
  }, [open]);

  // Each new first message takes focus on its OK button, which also makes a
  // screen reader announce it. On open the isolation effect has focused it already.
  useEffect(() => {
    if (current !== null) {
      okRef.current?.focus();
    }
  }, [current]);

  if (current === null) {
    return null;
  }

  const handleKeyDown = (event: ReactKeyboardEvent<HTMLDivElement>) => {
    if (handleDialogKey(event.nativeEvent, okRef.current, onDismiss)) {
      event.stopPropagation();
    }
  };

  // A press on the backdrop does not dismiss the message; preventing its
  // default also keeps focus on OK instead of moving it to the page.
  const handleBackdropMouseDown = (event: MouseEvent<HTMLDivElement>) => {
    if (event.target === event.currentTarget) {
      event.preventDefault();
    }
  };

  return createPortal(
    <div ref={backdropRef} className="dialog-backdrop" onMouseDown={handleBackdropMouseDown}>
      <div
        key={messageKey(current)}
        className="dialog"
        role="dialog"
        aria-modal="true"
        aria-labelledby={headingId}
        aria-describedby={textId}
        tabIndex={-1}
        onKeyDown={handleKeyDown}
      >
        <h2 id={headingId}>{headingText(current.title)}</h2>
        <p id={textId}>{current.message}</p>
        {/* Prefixed with "Message", never a bare "1 of 3", which would read like
            the record navigator's "[n] of N" position. */}
        {dialogs.length > 1 && (
          <p>
            <small>{`Message 1 of ${dialogs.length}`}</small>
          </p>
        )}
        <button ref={okRef} type="button" onClick={() => onDismiss()}>
          OK
        </button>
      </div>
    </div>,
    document.body,
  );
}
