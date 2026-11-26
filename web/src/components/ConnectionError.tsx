import { describeError, isSafeToResend, type ApiError, type ApiMethod } from '../api/errors';

/** The method of the failed request; whether Retry may resend it depends on it. */
export type ResendMethod = ApiMethod;

export interface ConnectionErrorProps {
  /** The failed request: a 503, or a request that received no response. */
  error: ApiError;
  /** Method of the failed request. Defaults to `GET`. */
  method?: ResendMethod;
  /** The failed request was the first `POST` of a new record, so a resend may duplicate it. */
  isCreate?: boolean;
  /** Resends the failed request, or reloads. Offered only when resending is safe. */
  onRetry?: () => void;
  /** Resends the draft at the user's explicit request although the earlier save may have been applied. */
  onSaveAgain?: () => void;
  onDismiss?: () => void;
}

const UNREACHABLE = 'Cannot reach the database server.';
const UNREACHABLE_WITH_DRAFT = 'Cannot reach the database server. Your changes are still on screen.';
const UNCERTAIN_WRITE =
  'The save may already have been applied, and the record list cannot prove whether it was. ' +
  'Your changes are still on screen.';

// The visible text stays "Save again", never the toolbar's exact "Save", and the
// accessible name starts with it and adds the risk of resending.
const SAVE_AGAIN_TEXT = 'Save again';
const SAVE_AGAIN_CREATE_LABEL = 'Save again (this may create a duplicate record)';
const SAVE_AGAIN_UPDATE_LABEL = 'Save again (the earlier save may already have been applied)';

function safeMessage(error: ApiError, method: ResendMethod): string {
  const described = describeError(error);
  if (described.trim() !== '') {
    return described;
  }
  return method === 'POST' || method === 'PUT' ? UNREACHABLE_WITH_DRAFT : UNREACHABLE;
}

/**
 * The banner for an unreachable database or server. It decides nothing about the
 * request itself: `isSafeToResend` decides whether Retry may resend it, and the editor
 * keeps the draft and performs the resend. A `POST` or `PUT` not proven `notWritten`
 * offers no Retry, only an explicit "Save again" that names its risk.
 *
 * The banner shows fixed text and the server's trace id only, never a field value, a
 * URL or exception text, which the API also keeps out of its error bodies.
 */
export function ConnectionError({
  error,
  method = 'GET',
  isCreate = false,
  onRetry,
  onSaveAgain,
  onDismiss,
}: ConnectionErrorProps) {
  const safe = isSafeToResend(error, method);
  const message = safe ? safeMessage(error, method) : UNCERTAIN_WRITE;

  // Shown in both variants, because an uncertain write is the case support needs it
  // for. describeError already ends a 500's text with the trace id, so it is never
  // repeated on a line of its own.
  const traceId = error.traceId;
  const traceLine = traceId !== null && !message.includes(traceId) ? `Trace id: ${traceId}` : null;

  const showRetry = safe && onRetry !== undefined;
  const showSaveAgain = !safe && onSaveAgain !== undefined;
  const showDismiss = onDismiss !== undefined;

  return (
    <div className="banner banner--error" role="alert">
      <p>{message}</p>
      {traceLine !== null && (
        <p>
          <small>{traceLine}</small>
        </p>
      )}
      {/* One group, so the buttons wrap together at the inline end instead of
          a later button landing alone at the start of the next line. */}
      {(showRetry || showSaveAgain || showDismiss) && (
        <div className="banner__actions">
          {showRetry && (
            <button type="button" onClick={() => onRetry()}>
              Retry
            </button>
          )}
          {showSaveAgain && (
            <button
              type="button"
              aria-label={isCreate ? SAVE_AGAIN_CREATE_LABEL : SAVE_AGAIN_UPDATE_LABEL}
              onClick={() => onSaveAgain()}
            >
              {SAVE_AGAIN_TEXT}
            </button>
          )}
          {showDismiss && (
            <button type="button" onClick={() => onDismiss()}>
              Dismiss
            </button>
          )}
        </div>
      )}
    </div>
  );
}
