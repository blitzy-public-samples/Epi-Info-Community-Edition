// The forms list: the first screen, where a form is chosen for data entry. It stands in for
// legacy Enter's view-selection dialog, except that the connected database is the project,
// so every form `GET /api/forms` returns is listed, in the order the API returns them.
// Render-only: the page count is the server's `pageCount`, and opening a form is the
// caller's decision through `onOpen`.
import { useCallback, useEffect, useId, useRef, useState } from 'react';

import { apiClient, type ApiClient } from '../api/client';
import { describeError, isAbortError, networkError, type ApiError } from '../api/errors';
import type { FormSummary } from '../api/types';
import { ConnectionError } from './ConnectionError';

export interface FormsListProps {
  /** The API client; defaults to the page-origin client. Tests inject `createApiClient({ baseUrl, fetch })`. */
  client?: ApiClient;
  /** Called with the chosen form when its button is pressed. */
  onOpen: (form: FormSummary) => void;
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

/** Lists the project's forms, one button per form, with loading, empty and error states. */
export function FormsList({ client = apiClient, onOpen }: FormsListProps) {
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

  return (
    <section className="forms-list" aria-labelledby={headingId}>
      <h2 id={headingId} ref={headingRef} tabIndex={-1}>
        Forms
      </h2>
      <FormsListBody state={state} onOpen={onOpen} onRetry={retry} />
    </section>
  );
}

interface FormsListBodyProps {
  state: FormsListState;
  onOpen: (form: FormSummary) => void;
  onRetry: () => void;
}

function FormsListBody({ state, onOpen, onRetry }: FormsListBodyProps) {
  switch (state.status) {
    case 'loading':
      return <p role="status">Loading forms…</p>;

    case 'loaded':
      if (state.forms.length === 0) {
        return <p>No forms found.</p>;
      }
      return (
        <ul>
          {state.forms.map((form) => (
            <li key={form.viewId}>
              <button type="button" onClick={() => onOpen(form)}>
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
  if (error.kind === 'unavailable' || error.kind === 'network') {
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
