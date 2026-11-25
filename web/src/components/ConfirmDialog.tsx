import {
  useEffect,
  useId,
  useRef,
  useState,
  type KeyboardEvent,
  type MouseEvent,
  type RefObject,
} from 'react';
import { createPortal } from 'react-dom';

export interface ConfirmDialogProps {
  open: boolean;
  title?: string;
  message?: string;
  confirmLabel?: string;
  cancelLabel?: string;
  /** Disables both buttons, and Escape, while the confirmed request is in flight. */
  busy?: boolean;
  /**
   * The record has never been saved. Without an explicit `message`, the text
   * then says the draft is discarded rather than marked deleted.
   */
  isNew?: boolean;
  /**
   * Optional: a control to focus when the dialog closes, tried before the
   * control whose click opened it and the element focused at open. The
   * clicked control is recorded even when the press did not focus it and
   * when the click is the one that mounts the dialog, so this is needed only
   * where no click opens the dialog.
   */
  returnFocusRef?: RefObject<HTMLElement>;
  /**
   * Called once per confirmation. Return the operation's promise; the dialog
   * stays pending until it settles, so a second activation cannot start a
   * second operation. The controller owns failure reporting: a rejection
   * releases the dialog for a retry and is not reported again by the dialog.
   */
  onConfirm: () => void | Promise<void>;
  onCancel: () => void;
}

const DEFAULT_TITLE = 'Delete record';
const DEFAULT_MESSAGE =
  'Delete this record? Unsaved changes to this record will be discarded. ' +
  'The record will be marked deleted and hidden unless Show deleted is on.';
const DRAFT_MESSAGE =
  'Delete this new record? Unsaved changes to this record will be discarded. ' +
  'It has never been saved, so nothing is kept, even with Show deleted on.';
// The confirm button's accessible name differs from the toolbar's exact "Delete",
// so the two never match the same exact-name query.
const DEFAULT_CONFIRM_LABEL = 'Delete record';
const DEFAULT_CANCEL_LABEL = 'Cancel';
const PENDING_STATUS = 'Deleting record…';

function isPromiseLike(value: unknown): value is PromiseLike<unknown> {
  return (
    ((typeof value === 'object' && value !== null) || typeof value === 'function') &&
    'then' in value &&
    typeof value.then === 'function'
  );
}

// Controls that can take focus: the recorded invoker of a click, and the
// candidates for the focus-return fallback, matched in document order.
const FOCUSABLE_SELECTOR =
  'button, [href], input, select, textarea, summary, [tabindex]:not([tabindex="-1"])';

const MODAL_SELECTOR = '[aria-modal="true"]';

/** Whether the element is, or contains, a modal dialog. */
function holdsModal(element: Element): boolean {
  return element.matches(MODAL_SELECTOR) || element.querySelector(MODAL_SELECTOR) !== null;
}

/** Cancel is the safe default; the dialog itself holds focus while Cancel is disabled. */
function focusSafeDefault(dialog: HTMLElement, cancel: HTMLButtonElement | null): void {
  if (cancel !== null && !cancel.disabled) {
    cancel.focus();
  } else {
    dialog.focus();
  }
}

function enabledButtons(...buttons: (HTMLButtonElement | null)[]): HTMLButtonElement[] {
  return buttons.filter(
    (button): button is HTMLButtonElement => button !== null && !button.disabled,
  );
}

/** Whether focus() really landed: a disabled, hidden, inert or detached element refuses it. */
function takesFocus(element: HTMLElement): boolean {
  if (!element.isConnected) {
    return false;
  }
  element.focus();
  return document.activeElement === element;
}

/** The element's ancestors below <body>, nearest first. */
function ancestorsBelowBody(element: HTMLElement | null): HTMLElement[] {
  const chain: HTMLElement[] = [];
  let current = element?.parentElement ?? null;
  while (current !== null && current !== document.body && current !== document.documentElement) {
    chain.push(current);
    current = current.parentElement;
  }
  return chain;
}

/** The control a click is being dispatched to, with its ancestors as they stood at the click. */
interface ClickRecord {
  control: HTMLElement | null;
  ancestors: HTMLElement[];
}

let currentClick: ClickRecord | null = null;

// One recorder for the whole page, listening from the moment this module loads,
// so a dialog knows its invoker even when the click is the one that mounts it,
// when the press did not focus the control, and when the opening render removes
// the control. A keyboard activation of a button dispatches a click as well.
// The capture phase on document runs before the control's own click handlers,
// and the record is cleared on the next task, so it never describes an earlier click.
if (typeof document !== 'undefined') {
  let clearTimer: number | undefined;
  document.addEventListener(
    'click',
    (event) => {
      const target = event.target;
      const found = target instanceof Element ? target.closest(FOCUSABLE_SELECTOR) : null;
      const control = found instanceof HTMLElement ? found : null;
      currentClick = { control, ancestors: ancestorsBelowBody(control) };
      window.clearTimeout(clearTimer);
      clearTimer = window.setTimeout(() => {
        clearTimer = undefined;
        currentClick = null;
      }, 0);
    },
    true,
  );
}

/** The click being dispatched in the current task, if any. */
function recordedClick(): ClickRecord | null {
  return currentClick;
}

/** Controls to try in order, then the ancestors whose controls stand in for them, nearest first. */
interface FocusReturnTier {
  controls: (HTMLElement | null)[];
  ancestors: HTMLElement[];
}

/**
 * Returns focus after the dialog closes, tier by tier: first the caller's
 * returnFocusRef and the control whose click opened the dialog, then the
 * element focused at open. A control counts only if it really takes focus,
 * which a disabled or removed trigger does not. When a tier's controls all
 * fail, focus goes to the first control, in document order, that takes it
 * inside that tier's ancestors, nearest first, skipping any no longer
 * connected: another toolbar button, for example, rather than <body>.
 */
function restoreFocus(tiers: FocusReturnTier[]): void {
  const tried = new Set<HTMLElement>();
  const focuses = (element: HTMLElement): boolean => {
    if (tried.has(element)) {
      return false;
    }
    tried.add(element);
    return takesFocus(element);
  };
  for (const { controls, ancestors } of tiers) {
    for (const control of controls) {
      if (control !== null && focuses(control)) {
        return;
      }
    }
    for (const ancestor of ancestors) {
      if (!ancestor.isConnected) {
        continue;
      }
      for (const element of ancestor.querySelectorAll(FOCUSABLE_SELECTOR)) {
        if (element instanceof HTMLElement && focuses(element)) {
          return;
        }
      }
    }
  }
}

/**
 * Asks before a destructive action and reports the user's choice; it sends no
 * request itself. A plain role="dialog" element rather than <dialog>.showModal(),
 * which jsdom does not implement. It is rendered into <body> so that, while it
 * is open, every other child of <body> can be made inert.
 */
export function ConfirmDialog({
  open,
  title = DEFAULT_TITLE,
  message,
  confirmLabel = DEFAULT_CONFIRM_LABEL,
  cancelLabel = DEFAULT_CANCEL_LABEL,
  busy = false,
  isNew = false,
  returnFocusRef,
  onConfirm,
  onCancel,
}: ConfirmDialogProps) {
  const headingId = useId();
  const messageId = useId();
  const backdropRef = useRef<HTMLDivElement>(null);
  const dialogRef = useRef<HTMLDivElement>(null);
  const cancelRef = useRef<HTMLButtonElement>(null);
  const confirmRef = useRef<HTMLButtonElement>(null);

  // Single-flight claim on the confirmation. This is UI interaction state only:
  // the dialog still just reports the choice and owns no workflow state. The
  // ref is claimed synchronously, so a second activation before React commits
  // `claimed` (or before the parent commits `busy`) is ignored; `claimed` drives
  // the rendered pending state. `sessionRef` counts closes, so a promise that
  // settles after the dialog closed cannot release a later session's claim.
  const claimRef = useRef(false);
  const awaitingRef = useRef(false);
  const sessionRef = useRef(0);
  const [claimed, setClaimed] = useState(false);
  const pending = busy || claimed;

  // A closed dialog holds no claim, so a reopen starts clean.
  useEffect(() => {
    if (!open) {
      return undefined;
    }
    return () => {
      sessionRef.current += 1;
      claimRef.current = false;
      awaitingRef.current = false;
      setClaimed(false);
    };
  }, [open]);

  // A handler that returned no promise (or threw) releases the claim once the
  // claim is committed and `busy` is down; one that raised `busy` keeps the
  // dialog pending until `busy` falls.
  useEffect(() => {
    if (claimed && !busy && !awaitingRef.current) {
      claimRef.current = false;
      setClaimed(false);
    }
  }, [busy, claimed]);

  // The document listeners live for a whole open session and read the latest props here.
  const latestRef = useRef({ pending, onCancel, returnFocusRef });
  useEffect(() => {
    latestRef.current = { pending, onCancel, returnFocusRef };
  });

  // Modal isolation for one open session. Declared before the placement effect
  // so it reads the invoker before focus moves into the dialog: the control
  // whose click opened it, with the ancestors it had at the click, and the
  // element focused at open, which alone identifies an invoker when no click
  // opened it. The rest of the page, content added while open included, is
  // made inert; focus that still lands outside (before added content is made
  // inert, or in a browser without inert) is pulled back; Tab and Escape
  // pressed outside the dialog still act on it.
  useEffect(() => {
    const backdrop = backdropRef.current;
    if (!open || backdrop === null) {
      return undefined;
    }

    const click = recordedClick();
    const invoker = click !== null && !backdrop.contains(click.control) ? click : null;
    const activator = invoker?.control ?? null;
    const invokerAncestors = [
      ...new Set([
        ...(invoker?.ancestors ?? []),
        ...ancestorsBelowBody(latestRef.current.returnFocusRef?.current ?? null),
      ]),
    ];
    const active = document.activeElement;
    const opener =
      active instanceof HTMLElement &&
      active !== document.body &&
      active !== document.documentElement &&
      !backdrop.contains(active)
        ? active
        : null;
    const openerAncestors = ancestorsBelowBody(opener);

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

    const isOutsideDialog = (target: EventTarget | null) =>
      !(target instanceof Node && dialogRef.current?.contains(target) === true);

    // A newer modal dialog owns focus and keys while it is open: while it has
    // made this dialog inert, and for events inside it.
    const yieldsTo = (target: EventTarget | null) => {
      if (backdrop.closest('[inert]') !== null) {
        return true;
      }
      const modal = target instanceof Element ? target.closest(MODAL_SELECTOR) : null;
      return modal !== null && modal !== dialogRef.current;
    };

    const handleFocusIn = (event: FocusEvent) => {
      const dialog = dialogRef.current;
      if (dialog !== null && isOutsideDialog(event.target) && !yieldsTo(event.target)) {
        focusSafeDefault(dialog, cancelRef.current);
      }
    };

    // Keys pressed inside the dialog are handled by its own onKeyDown.
    const handleDocumentKeyDown = (event: DocumentEventMap['keydown']) => {
      const dialog = dialogRef.current;
      if (dialog === null || !isOutsideDialog(event.target) || yieldsTo(event.target)) {
        return;
      }
      if (event.key === 'Escape') {
        event.preventDefault();
        if (!latestRef.current.pending && !claimRef.current) {
          latestRef.current.onCancel();
        }
        return;
      }
      if (event.key === 'Tab') {
        event.preventDefault();
        const buttons = enabledButtons(cancelRef.current, confirmRef.current);
        if (buttons.length === 0) {
          dialog.focus();
        } else {
          buttons[event.shiftKey ? buttons.length - 1 : 0].focus();
        }
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
      restoreFocus([
        {
          controls: [latestRef.current.returnFocusRef?.current ?? null, activator],
          ancestors: invokerAncestors,
        },
        { controls: [opener], ancestors: openerAncestors },
      ]);
    };
  }, [open]);

  // Cancel is the safe default. While pending both buttons are disabled and
  // cannot hold focus, so the dialog itself takes it and keyboard input stays inside.
  useEffect(() => {
    const dialog = dialogRef.current;
    if (!open || dialog === null) {
      return;
    }
    const active = document.activeElement;
    if (active instanceof HTMLButtonElement && !active.disabled && dialog.contains(active)) {
      return;
    }
    focusSafeDefault(dialog, cancelRef.current);
  }, [open, pending]);

  if (!open) {
    return null;
  }

  const handleConfirm = () => {
    if (busy || claimRef.current) {
      return;
    }
    claimRef.current = true;
    setClaimed(true);
    const result: unknown = onConfirm();
    if (!isPromiseLike(result)) {
      return;
    }
    awaitingRef.current = true;
    const session = sessionRef.current;
    const release = () => {
      if (sessionRef.current !== session) {
        return;
      }
      awaitingRef.current = false;
      claimRef.current = false;
      setClaimed(false);
    };
    // Either outcome releases the claim, and the promise derived here always
    // fulfils, so a rejection the controller has handled is not raised again.
    void Promise.resolve(result).then(release, release);
  };

  // Once the confirmation is claimed, Cancel and Escape no longer report a choice.
  const handleCancel = () => {
    if (busy || claimRef.current) {
      return;
    }
    onCancel();
  };

  const handleKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    if (event.key === 'Escape') {
      event.preventDefault();
      event.stopPropagation();
      handleCancel();
      return;
    }
    if (event.key !== 'Tab') {
      return;
    }
    // Focus cycles between the enabled buttons and never leaves the dialog.
    event.preventDefault();
    const buttons = enabledButtons(cancelRef.current, confirmRef.current);
    if (buttons.length === 0) {
      dialogRef.current?.focus();
      return;
    }
    const current = buttons.findIndex((button) => button === document.activeElement);
    const step = event.shiftKey ? -1 : 1;
    const next =
      current === -1
        ? event.shiftKey
          ? buttons.length - 1
          : 0
        : (current + step + buttons.length) % buttons.length;
    buttons[next].focus();
  };

  // A press on the backdrop neither confirms nor cancels; preventing its default
  // also keeps focus inside the dialog instead of moving it to the page.
  const handleBackdropMouseDown = (event: MouseEvent<HTMLDivElement>) => {
    if (event.target === event.currentTarget) {
      event.preventDefault();
    }
  };

  return createPortal(
    <div ref={backdropRef} className="dialog-backdrop" onMouseDown={handleBackdropMouseDown}>
      <div
        ref={dialogRef}
        className="dialog"
        role="dialog"
        aria-modal="true"
        aria-labelledby={headingId}
        aria-describedby={messageId}
        tabIndex={-1}
        onKeyDown={handleKeyDown}
      >
        <h2 id={headingId}>{title}</h2>
        <p id={messageId}>{message ?? (isNew ? DRAFT_MESSAGE : DEFAULT_MESSAGE)}</p>
        {/* Always rendered, so the status text is announced when it appears. No
            aria-busy on the dialog: it would defer that announcement. */}
        <p role="status" className={pending ? undefined : 'visually-hidden'}>
          {pending ? PENDING_STATUS : ''}
        </p>
        <button ref={cancelRef} type="button" disabled={pending} onClick={handleCancel}>
          {cancelLabel}
        </button>
        <button
          ref={confirmRef}
          type="button"
          className="button--danger"
          disabled={pending}
          onClick={handleConfirm}
        >
          {confirmLabel}
        </button>
      </div>
    </div>,
    document.body,
  );
}
