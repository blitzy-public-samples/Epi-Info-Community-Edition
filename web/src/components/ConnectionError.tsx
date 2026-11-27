import {
  describeError,
  describeUncertainWrite,
  errorMessages,
  isSafeToResend,
  type ApiError,
  type ApiMethod,
} from '../api/errors';

/** The method of the failed request; whether Retry may resend it depends on it. */
export type ResendMethod = ApiMethod;

export interface ConnectionErrorProps {
  /** The failed request: a 503, or a request that received no response. */
  error: ApiError;
  /** Method of the failed request. Defaults to `GET`. */
  method?: ResendMethod;
  /** The failed request was the first `POST` of a new record, so a resend may duplicate it. */
  isCreate?: boolean;
  /**
   * The text the owner of the failed request decided, such as the editor's save or delete
   * message, shown as is. Absent or blank, the text follows from `error` and `method`.
   */
  message?: string;
  /** Resends the failed request, or reloads. Offered only when resending is safe. */
  onRetry?: () => void;
  /** Resends the draft at the user's explicit request although the earlier save may have been applied. */
  onSaveAgain?: () => void;
  onDismiss?: () => void;
}

// The visible text stays "Save again", never the toolbar's exact "Save", and the
// accessible name starts with it and adds the risk of resending.
const SAVE_AGAIN_TEXT = 'Save again';
const SAVE_AGAIN_CREATE_LABEL = 'Save again (this may create a duplicate record)';
const SAVE_AGAIN_UPDATE_LABEL = 'Save again (the earlier save may already have been applied)';

/**
 * The text when the caller decided none. A `POST` or `PUT` failed with the draft on
 * screen: when it is safe to resend, describeError's text says the draft is kept;
 * otherwise describeUncertainWrite's text says what could not be reached and that the save
 * may have been applied, which for a create risks a duplicate, the risk the create's "Save
 * again" also names. A `GET` or `DELETE` holds no draft, since a read loads nothing the
 * user typed and a delete's confirmation discarded it, so its text says only what cannot
 * be reached.
 */
function defaultMessage(error: ApiError, method: ResendMethod, safe: boolean, isCreate: boolean): string {
  if (!safe) {
    return describeUncertainWrite(error, isCreate);
  }
  if (method === 'POST' || method === 'PUT') {
    return describeError(error);
  }
  switch (error.kind) {
    case 'unavailable':
      return errorMessages.unavailableWithoutDraft;
    case 'network':
      return errorMessages.networkWithoutDraft;
    default:
      return describeError(error);
  }
}

/**
 * The banner for an unreachable database or server. It decides nothing about the
 * request itself: `isSafeToResend` decides whether Retry may resend it, and the editor
 * keeps the draft and performs the resend. A `POST` or `PUT` not proven `notWritten`
 * offers no Retry, only an explicit "Save again" that names its risk.
 *
 * The text is the caller's `message` when it has one, so the request's owner states what
 * happened to the draft; otherwise it follows from the method, as `defaultMessage` says.
 *
 * The banner shows fixed text and the server's trace id only, never a field value, a
 * URL or exception text, which the API also keeps out of its error bodies.
 */
export function ConnectionError({
  error,
  method = 'GET',
  isCreate = false,
  message: callerMessage,
  onRetry,
  onSaveAgain,
  onDismiss,
}: ConnectionErrorProps) {
  const safe = isSafeToResend(error, method);
  const message =
    callerMessage !== undefined && callerMessage.trim() !== ''
      ? callerMessage
      : defaultMessage(error, method, safe, isCreate);

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
          {/* The warning tone sets the resend that may duplicate apart from Retry and Dismiss. */}
          {showSaveAgain && (
            <button
              type="button"
              className="button--warning"
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
