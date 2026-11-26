// The application shell: the forms list, or the one form opened from it. It stands in for
// legacy Enter's main window, where a form (view) is opened, worked in and closed. Closing
// is FormView's "Back to forms", which first saves a dirty draft as legacy CloseViewHandler
// does, so the shell never unmounts an open form on its own. Render-only: every request
// goes through the ApiClient, and the open form is React state, not a route.
//
// Legacy command-line switches become URL parameters: `?view=<form name>&record=<1-based
// position>` opens that form at that record. `title` and `project` are ignored, because
// the page title is fixed and the API's configured connection string is the project. Once
// read, the query string follows the shell: `?view=<name>&record=<n>` while a form is
// open, without `record` for a new record or before the first one opens, and the bare
// path `/` on the forms list.
//
// The URL's request ends only when it is settled: the form opens, the forms list proves no
// form has that name, or the user chooses "Show forms list". A lookup that fails (a 503, a
// lost response, an OK answer that is not a forms list, or any other error) leaves the form
// unknown, so the request and its URL are kept and Retry repeats the open; `GET /api/forms`
// is safe to send again.
import { useCallback, useEffect, useRef, useState, type FocusEvent, type ReactNode } from 'react';

import { apiClient, type ApiClient, type ApiResult } from './api/client';
import { describeError, networkError, parseProblem, type ApiError } from './api/errors';
import type { FormSummary } from './api/types';
import { ConnectionError } from './components/ConnectionError';
import { FormView } from './components/FormView';
import { FormsList } from './components/FormsList';

export interface AppProps {
  /** The API client; defaults to the page-origin client. Tests inject `createApiClient({ baseUrl, fetch })`. */
  client?: ApiClient;
}

/** The open form. `initialPosition` is the 1-based record it opens at; absent opens FormView's default. */
interface SelectedForm {
  readonly viewId: number;
  readonly name: string;
  readonly initialPosition: number | undefined;
}

/** The form and record the page URL asked for when the shell loaded. */
interface UrlSwitches {
  readonly view: string;
  readonly record: number | undefined;
}

const TITLE = 'Epi Info Data Entry';
const FORM_NOT_FOUND_NOTICE = 'This form no longer exists.';
const LIST_PATH = '/';
const RETRY = 'Retry';
const SHOW_FORMS_LIST = 'Show forms list';

const POSITION_PATTERN = /^[1-9]\d*$/;
// The API reads a position as a 32-bit integer and answers 400 above it. A larger `record`
// is still a record beyond the count, so it is capped here, and FormView's 404 recovery
// then opens the nearest record, as it does for any position past the end.
const MAX_POSITION = 2_147_483_647;

/** `view` and `record` from a query string; null when no form is named. */
function readUrlSwitches(search: string): UrlSwitches | null {
  const params = new URLSearchParams(search);
  const view = params.get('view')?.trim() ?? '';
  if (view === '') {
    return null;
  }
  const record = params.get('record');
  const position =
    record !== null && POSITION_PATTERN.test(record) ? Math.min(Number(record), MAX_POSITION) : undefined;
  return { view, record: position };
}

/**
 * Whether an OK body holds what the lookup reads: a list whose every entry has an integer
 * `viewId` and a string `name`. The client accepts any JSON as a success, so the body is
 * checked as `unknown` before findForm reads it.
 */
function isFormList(value: unknown): value is readonly Pick<FormSummary, 'viewId' | 'name'>[] {
  if (!Array.isArray(value)) {
    return false;
  }
  const items: readonly unknown[] = value;
  return items.every(
    (item) =>
      typeof item === 'object' &&
      item !== null &&
      'viewId' in item &&
      Number.isInteger(item.viewId) &&
      'name' in item &&
      typeof item.name === 'string',
  );
}

/** The form whose name is `view`: the exact spelling first, then ignoring case. */
function findForm(forms: readonly FormSummary[], view: string): FormSummary | undefined {
  const lower = view.toLowerCase();
  return forms.find((form) => form.name === view) ?? forms.find((form) => form.name.toLowerCase() === lower);
}

function formUrl(name: string, position: number | null): string {
  const params = new URLSearchParams({ view: name });
  if (position !== null) {
    params.set('record', String(position));
  }
  return `${LIST_PATH}?${params.toString()}`;
}

/** Rewrites the address bar in place, so moving between records adds no history entries. */
function replaceUrl(url: string): void {
  const { pathname, search, hash } = window.location;
  if (`${pathname}${search}${hash}` !== url) {
    window.history.replaceState(null, '', url);
  }
}

/**
 * Keeps keyboard focus on a button that is disabled only while its own request runs, such
 * as Next, Save or Go. The browser moves focus to <body> when the focused control becomes
 * disabled, so a second key press would do nothing. Once that button is enabled again, and
 * focus has gone nowhere else meanwhile, focus returns to it; a button that stays disabled
 * is left alone. Buttons only, because focusing a field raises a check-code event. React's
 * focus events bubble through portals, so the dialogs are covered as well.
 */
function useFocusReturn(): { onBlur: (event: FocusEvent<HTMLElement>) => void; onFocus: () => void } {
  // Watches the one button that lost focus until it is enabled again.
  const watcherRef = useRef<MutationObserver | null>(null);

  const cancel = useCallback(() => {
    watcherRef.current?.disconnect();
    watcherRef.current = null;
  }, []);

  const onBlur = useCallback(
    (event: FocusEvent<HTMLElement>) => {
      const button = event.target;
      if (event.relatedTarget !== null || !(button instanceof HTMLButtonElement) || !button.disabled) {
        return;
      }
      cancel();
      const observer = new MutationObserver(() => {
        if (button.disabled) {
          return;
        }
        cancel();
        const active = document.activeElement;
        if (button.isConnected && (active === null || active === document.body)) {
          button.focus();
        }
      });
      observer.observe(button, { attributes: true, attributeFilter: ['disabled'] });
      watcherRef.current = observer;
    },
    [cancel],
  );

  useEffect(() => cancel, [cancel]);

  return { onBlur, onFocus: cancel };
}

interface LookupFailureProps {
  /** The form name the URL asked for. */
  readonly view: string;
  readonly error: ApiError;
  /** Repeats the lookup of `view`. */
  readonly onRetry: () => void;
  /** Gives up the URL's request and shows the forms list. */
  readonly onShowList: () => void;
}

/** The URL's form could not be looked up: the request is kept, so Retry repeats the open. */
function LookupFailure({ view, error, onRetry, onShowList }: LookupFailureProps) {
  let banner: ReactNode;
  if (error.kind === 'unavailable' || error.kind === 'network') {
    banner = <ConnectionError error={error} method="GET" onRetry={onRetry} />;
  } else {
    const message = describeError(error);
    // describeError already ends a 500's text with the trace id, so it is not repeated.
    const traceId = error.traceId;
    const traceLine = traceId !== null && !message.includes(traceId) ? `Trace id: ${traceId}` : null;
    banner = (
      <div className="banner banner--error" role="alert">
        <p>{message}</p>
        {traceLine !== null && (
          <p>
            <small>{traceLine}</small>
          </p>
        )}
        <button type="button" onClick={() => onRetry()}>
          {RETRY}
        </button>
      </div>
    );
  }

  return (
    <>
      {/* `view` is user input from the URL; React renders it as text, never as markup. */}
      <p>{`Form '${view}' could not be opened.`}</p>
      {banner}
      {/* Wrapped, so the shell's column does not stretch the button to its full width. */}
      <div>
        <button type="button" onClick={() => onShowList()}>
          {SHOW_FORMS_LIST}
        </button>
      </div>
    </>
  );
}

/** The application shell: a title, a one-shot notice, then the forms list or the open form. */
export default function App({ client = apiClient }: AppProps) {
  // Read once, on the first render; every later URL is the shell's own. Non-null until the
  // request is settled, which a failed lookup does not do.
  const [switches, setSwitches] = useState<UrlSwitches | null>(() => readUrlSwitches(window.location.search));
  // Why the last lookup of `switches` failed; null while one runs or before any has failed.
  const [lookupFailure, setLookupFailure] = useState<ApiError | null>(null);
  // Counts Retry presses, so the lookup effect runs again for the same request.
  const [lookupAttempt, setLookupAttempt] = useState(0);
  const [selected, setSelected] = useState<SelectedForm | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  // The open form as of the last change, read by position reports that may arrive late.
  const openRef = useRef<SelectedForm | null>(null);
  const headingRef = useRef<HTMLHeadingElement>(null);
  // Set by a user's move between the list and a form: the control they used unmounts with
  // the screen it was on, so focus goes to the title, which both screens keep.
  const focusTitleRef = useRef(false);
  const focusReturn = useFocusReturn();

  const show = useCallback((form: SelectedForm | null, nextNotice: string | null) => {
    openRef.current = form;
    setSelected(form);
    setNotice(nextNotice);
    replaceUrl(form === null ? LIST_PATH : formUrl(form.name, null));
  }, []);

  useEffect(() => {
    if (switches === null) {
      return;
    }
    const { view, record } = switches;
    // Unmounting, a Retry, or StrictMode's repeated effect aborts this lookup; only the last
    // one applies.
    const controller = new AbortController();
    void (async () => {
      let result: ApiResult<FormSummary[]>;
      try {
        result = await client.listForms(controller.signal);
      } catch {
        // The client rejects only when the call is aborted; any other rejection received no
        // answer, so it is shown as a lost response with Retry, never left loading.
        if (!controller.signal.aborted) {
          setLookupFailure(networkError());
        }
        return;
      }
      if (controller.signal.aborted) {
        return;
      }
      if (!result.ok) {
        // Whether the form exists is still unknown: the request and its URL stay for Retry.
        setLookupFailure(result.error);
        return;
      }
      if (!isFormList(result.data)) {
        // An OK answer that is not a forms list proves nothing either. The shared parser
        // makes it the unexpected failure it makes of an OK answer without a body, and the
        // request and its URL stay for Retry.
        setLookupFailure(parseProblem(result.status, undefined));
        return;
      }
      setSwitches(null);
      const form = findForm(result.data, view);
      if (form !== undefined) {
        show({ viewId: form.viewId, name: form.name, initialPosition: record }, null);
      } else {
        // `view` is user input from the URL; React renders it as text, never as markup.
        show(null, `Form '${view}' was not found.`);
      }
    })();
    return () => {
      controller.abort();
    };
    // `lookupAttempt` is not read: a change of it is the Retry that repeats this lookup.
  }, [client, switches, lookupAttempt, show]);

  useEffect(() => {
    if (focusTitleRef.current) {
      focusTitleRef.current = false;
      headingRef.current?.focus();
    }
  }, [selected]);

  const openFromList = useCallback(
    (form: FormSummary) => {
      focusTitleRef.current = true;
      show({ viewId: form.viewId, name: form.name, initialPosition: undefined }, null);
    },
    [show],
  );

  const returnToList = useCallback(() => {
    focusTitleRef.current = true;
    show(null, null);
  }, [show]);

  const formNotFound = useCallback(() => {
    focusTitleRef.current = true;
    show(null, FORM_NOT_FOUND_NOTICE);
  }, [show]);

  const reportPosition = useCallback((form: SelectedForm, n: number | null) => {
    // A form already closed must not write its URL back over the list's.
    if (openRef.current === form) {
      replaceUrl(formUrl(form.name, n));
    }
  }, []);

  // Both remove the button just pressed, and the title stays on every screen, so focus goes
  // there first rather than falling to the page body.
  const retryLookup = useCallback(() => {
    headingRef.current?.focus();
    setLookupFailure(null);
    setLookupAttempt((attempt) => attempt + 1);
  }, []);

  const abandonLookup = useCallback(() => {
    headingRef.current?.focus();
    setLookupFailure(null);
    setSwitches(null);
    show(null, null);
  }, [show]);

  let content: ReactNode;
  if (switches !== null && lookupFailure !== null) {
    content = (
      <LookupFailure view={switches.view} error={lookupFailure} onRetry={retryLookup} onShowList={abandonLookup} />
    );
  } else if (switches !== null) {
    content = <p role="status">{`Opening form ${switches.view}…`}</p>;
  } else if (selected === null) {
    content = <FormsList client={client} onOpen={openFromList} />;
  } else {
    content = (
      <FormView
        key={selected.viewId}
        viewId={selected.viewId}
        client={client}
        initialPosition={selected.initialPosition}
        onExit={returnToList}
        onFormNotFound={formNotFound}
        onPositionChange={(n) => reportPosition(selected, n)}
      />
    );
  }

  return (
    <main className="app" onBlur={focusReturn.onBlur} onFocus={focusReturn.onFocus}>
      <h1 ref={headingRef} tabIndex={-1}>
        {TITLE}
      </h1>
      {notice !== null && (
        <div className="banner banner--error" role="alert">
          <p>{notice}</p>
        </div>
      )}
      {content}
    </main>
  );
}
