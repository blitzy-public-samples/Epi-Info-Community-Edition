import { useEffect, useId, useRef, type KeyboardEvent, type MouseEvent } from 'react';

export interface ConfirmDialogProps {
  open: boolean;
  title?: string;
  message?: string;
  confirmLabel?: string;
  cancelLabel?: string;
  /** Disables both buttons, and Escape, while the confirmed request is in flight. */
  busy?: boolean;
  onConfirm: () => void;
  onCancel: () => void;
}

const DEFAULT_TITLE = 'Delete record';
const DEFAULT_MESSAGE =
  'Delete this record? Unsaved changes to this record will be discarded. ' +
  'The record will be marked deleted and hidden unless Show deleted is on.';
// The confirm button's accessible name differs from the toolbar's exact "Delete",
// so the two never match the same exact-name query.
const DEFAULT_CONFIRM_LABEL = 'Delete record';
const DEFAULT_CANCEL_LABEL = 'Cancel';

/**
 * Asks before a destructive action and reports the user's choice; it sends no
 * request itself. A plain role="dialog" element rather than <dialog>.showModal(),
 * which jsdom does not implement.
 */
export function ConfirmDialog({
  open,
  title = DEFAULT_TITLE,
  message = DEFAULT_MESSAGE,
  confirmLabel = DEFAULT_CONFIRM_LABEL,
  cancelLabel = DEFAULT_CANCEL_LABEL,
  busy = false,
  onConfirm,
  onCancel,
}: ConfirmDialogProps) {
  const headingId = useId();
  const messageId = useId();
  const dialogRef = useRef<HTMLDivElement>(null);
  const cancelRef = useRef<HTMLButtonElement>(null);
  const confirmRef = useRef<HTMLButtonElement>(null);

  // Declared before the placement effect so it captures the opener before
  // focus moves into the dialog.
  useEffect(() => {
    if (!open) {
      return undefined;
    }
    const opener = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    return () => {
      if (opener !== null && opener.isConnected) {
        opener.focus();
      }
    };
  }, [open]);

  // Cancel is the safe default. While busy both buttons are disabled and cannot
  // hold focus, so the dialog itself takes it and keyboard input stays inside.
  useEffect(() => {
    const dialog = dialogRef.current;
    if (!open || dialog === null) {
      return;
    }
    const active = document.activeElement;
    if (active instanceof HTMLButtonElement && !active.disabled && dialog.contains(active)) {
      return;
    }
    const cancel = cancelRef.current;
    if (cancel !== null && !cancel.disabled) {
      cancel.focus();
    } else {
      dialog.focus();
    }
  }, [open, busy]);

  if (!open) {
    return null;
  }

  const handleKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    if (event.key === 'Escape') {
      event.preventDefault();
      event.stopPropagation();
      if (!busy) {
        onCancel();
      }
      return;
    }
    if (event.key !== 'Tab') {
      return;
    }
    // Focus cycles between the enabled buttons and never leaves the dialog.
    event.preventDefault();
    const buttons = [cancelRef.current, confirmRef.current].filter(
      (button): button is HTMLButtonElement => button !== null && !button.disabled,
    );
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

  return (
    <div className="dialog-backdrop" onMouseDown={handleBackdropMouseDown}>
      <div
        ref={dialogRef}
        className="dialog"
        role="dialog"
        aria-modal="true"
        aria-labelledby={headingId}
        aria-describedby={messageId}
        aria-busy={busy}
        tabIndex={-1}
        onKeyDown={handleKeyDown}
      >
        <h2 id={headingId}>{title}</h2>
        <p id={messageId}>{message}</p>
        <button ref={cancelRef} type="button" disabled={busy} onClick={() => onCancel()}>
          {cancelLabel}
        </button>
        <button ref={confirmRef} type="button" disabled={busy} onClick={() => onConfirm()}>
          {confirmLabel}
        </button>
      </div>
    </div>
  );
}
