// The forms list: the first screen, where a form is chosen for data entry. It stands in for
// legacy Enter's view-selection dialog, except that the connected database is the project,
// so every form `GET /api/forms` returns is listed, in the order the API returns them.
// Render-only: the page count is the server's `pageCount`, and opening a form is the
// caller's decision through `onOpen`.
import { useCallback, useEffect, useId, useRef, useState } from 'react';

import { apiClient, type ApiClient } from '../api/client';
import { describeError, isAbortError, isConnectionFailure, networkError, type ApiError } from '../api/errors';
import type { FormSummary } from '../api/types';
import { ConnectionError } from './ConnectionError';

export interface FormsListProps {
  /** The API client; defaults to the page-origin client. Tests inject `createApiClient({ baseUrl, fetch })`. */
  client?: ApiClient;
  /** Called with the chosen form when its button is pressed. */
  onOpen: (form: FormSummary) => void;
  /**
   * Called with a form whose button the pointer enters, that takes focus or that a pointer
   * presses, before it is chosen, so the caller can start loading what opening it needs.
   */
  onPreview?: (form: FormSummary) => void;
}

type FormsListState =
  | { readonly status: 'loading' }
  | { readonly status: 'loaded'; readonly forms: readonly FormSummary[] }
  | { readonly status: 'failed'; readonly error: ApiError };

/** The button text, which starts with the form name so the accessible name does too: "FoodHistory — 2 pages". */
function formButtonText(form: FormSummary): string {
  const unit = form.pageCount === 1 ? 'page' : 'pages';
  return `${form.name} — ${String(form.pageCount)} ${unit}`;
}

/**
 * The status line's text, and whether it is shown. Loading and an empty result are shown;
 * the loaded count is visually hidden, because the buttons already show the forms. A
 * failure leaves it empty: the error banner is an alert of its own.
 */
function statusOf(state: FormsListState): { readonly text: string; readonly shown: boolean } {
  switch (state.status) {
    case 'loading':
      return { text: 'Loading forms…', shown: true };

    case 'loaded': {
      const count = state.forms.length;
      if (count === 0) {
        return { text: 'No forms found.', shown: true };
      }
      return { text: `${String(count)} ${count === 1 ? 'form' : 'forms'}`, shown: false };
    }

    case 'failed':
      return { text: '', shown: false };
  }
}

/** Lists the project's forms, one button per form, with loading, empty and error states. */
export function FormsList({ client = apiClient, onOpen, onPreview }: FormsListProps) {
  const headingId = useId();
  const [state, setState] = useState<FormsListState>({ status: 'loading' });
  // The in-flight load; a new load or unmounting aborts it, so a late answer never
  // overwrites a newer state or updates an unmounted component.
  const loadRef = useRef<AbortController | null>(null);

  const load = useCallback(() => {
    loadRef.current?.abort();
    const controller = new AbortController();
    loadRef.current = controller;
    setState({ status: 'loading' });

    void (async () => {
      try {
        const result = await client.listForms(controller.signal);
        if (!controller.signal.aborted) {
          setState(result.ok ? { status: 'loaded', forms: result.data } : { status: 'failed', error: result.error });
        }
      } catch (e) {
        if (controller.signal.aborted || isAbortError(e)) {
          return;
        }
        // The client settles every outcome but an abort into a result, so any other
        // rejection means no response was confirmed: show it as such, with Retry, rather
        // than leave the list loading forever.
        setState({ status: 'failed', error: networkError() });
      }
    })();
  }, [client]);

  useEffect(() => {
    load();
    return () => {
      loadRef.current?.abort();
    };
  }, [load]);

  // Retry removes its own button while the list reloads, which would drop focus to the
  // page body; the heading keeps it inside the list, one Tab before the outcome's controls.
  const headingRef = useRef<HTMLHeadingElement>(null);
  const retry = useCallback(() => {
    headingRef.current?.focus();
    load();
  }, [load]);

  const status = statusOf(state);

  return (
    <section className="forms-list" aria-labelledby={headingId}>
      <h2 id={headingId} ref={headingRef} tabIndex={-1}>
        Forms
      </h2>
      {/* Always rendered, so the loaded count and the empty result are announced as well as
          the loading text: a live region replaced, or inserted with its text, is not. */}
      <p role="status" className={status.shown ? undefined : 'visually-hidden'}>
        {status.text}
      </p>
      <FormsListBody state={state} onOpen={onOpen} onPreview={onPreview} onRetry={retry} />
    </section>
  );
}

interface FormsListBodyProps {
  state: FormsListState;
  onOpen: (form: FormSummary) => void;
  onPreview: ((form: FormSummary) => void) | undefined;
  onRetry: () => void;
}

function FormsListBody({ state, onOpen, onPreview, onRetry }: FormsListBodyProps) {
  switch (state.status) {
    case 'loading':
      // The status line says so.
      return null;

    case 'loaded':
      if (state.forms.length === 0) {
        // The status line says so.
        return null;
      }
      return (
        <ul>
          {state.forms.map((form) => (
            <li key={form.viewId}>
              <button
                type="button"
                onClick={() => onOpen(form)}
                onPointerEnter={() => onPreview?.(form)}
                onFocus={() => onPreview?.(form)}
                onPointerDown={() => onPreview?.(form)}
              >
                {formButtonText(form)}
              </button>
            </li>
          ))}
        </ul>
      );

    case 'failed':
      return <LoadFailure error={state.error} onRetry={onRetry} />;
  }
}

function LoadFailure({ error, onRetry }: { error: ApiError; onRetry: () => void }) {
  if (isConnectionFailure(error)) {
    return <ConnectionError error={error} method="GET" onRetry={onRetry} />;
  }

  const message = describeError(error);
  // describeError already ends a 500's text with the trace id, so it is not repeated.
  const traceId = error.traceId;
  const traceLine = traceId !== null && !message.includes(traceId) ? `Trace id: ${traceId}` : null;

  return (
    <div className="banner banner--error" role="alert">
      <p>{message}</p>
      {traceLine !== null && (
        <p>
          <small>{traceLine}</small>
        </p>
      )}
      <button type="button" onClick={() => onRetry()}>
        Retry
      </button>
    </div>
  );
}
