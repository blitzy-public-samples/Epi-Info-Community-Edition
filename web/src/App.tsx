// The application shell: the forms list, or the one form opened from it. It stands in for
// legacy Enter's main window, where a form (view) is opened, worked in and closed. Closing
// is FormView's "Back to forms", which first saves a dirty draft as legacy CloseViewHandler
// does, so the shell never unmounts an open form on its own. Render-only: every request
// goes through the ApiClient, and the open form is React state, not a route.
//
// Legacy command-line switches become URL parameters: `?view=<form name>&record=<1-based
// position>` opens that form at that record; a `record` that is not a record number, or
// lies past the end, opens the first or the last record with a notice naming it as
// written. `title` and `project` are ignored, because the page title is fixed and the
// API's configured connection string is the project. `deleted=1` opens the form with
// "Show deleted" on, so `record` counts every record, as the navigator does with the
// toggle on. Once read, the query string follows the shell: `?view=<name>&record=<n>`
// while a form is open, with `&deleted=1` while the toggle is on, without `record` for a
// new record or before the first one opens, and the bare path `/` on the forms list.
//
// The URL's request ends only when it is settled: the form opens, the forms list proves no
// form has that name, or the user chooses "Show forms list". A lookup that fails (a 503, a
// lost response, an OK answer that is not a forms list, or any other error) leaves the form
// unknown, so the request and its URL are kept and Retry repeats the open; `GET /api/forms`
// is safe to send again.
//
// The form screen is a chunk of its own, so the forms list loads without it. It is fetched
// ahead of an open, when a form's button is hovered, focused or pressed, and beside the
// lookup of a `?view=` URL, so an open does not wait for it.
import { useCallback, useEffect, useRef, useState, type FocusEvent, type ReactNode } from 'react';

import { apiClient, type ApiClient, type ApiResult } from './api/client';
import { describeError, isConnectionFailure, networkError, type ApiError } from './api/errors';
import type { FormSummary } from './api/types';
import { ConnectionError } from './components/ConnectionError';
import type { FormView, FormViewProps } from './components/FormView';
import { FormsList } from './components/FormsList';

export interface AppProps {
  /** The API client; defaults to the page-origin client. Tests inject `createApiClient({ baseUrl, fetch })`. */
  client?: ApiClient;
}

/**
 * The open form. `initialPosition` is the 1-based record it opens at; absent opens
 * FormView's default. `initialPositionText` is the URL's `record` as written, which
 * FormView's notice names when it cannot open that record as asked.
 * `initialIncludeDeleted` opens it with "Show deleted" on.
 */
interface SelectedForm {
  readonly viewId: number;
  readonly name: string;
  readonly initialPosition: number | undefined;
  readonly initialPositionText: string | undefined;
  readonly initialIncludeDeleted: boolean;
}

/**
 * The form, record and deleted toggle the page URL asked for when the shell loaded.
 * `recordText` is `record` as written; absent when the URL has none.
 */
interface UrlSwitches {
  readonly view: string;
  readonly record: number | undefined;
  readonly recordText: string | undefined;
  readonly deleted: boolean;
}

const TITLE = 'Epi Info Data Entry';
const DISMISS = 'Dismiss';
const FORM_NOT_FOUND_NOTICE = 'This form no longer exists.';
const LIST_PATH = '/';
const RETRY = 'Retry';
const SHOW_FORMS_LIST = 'Show forms list';
// FormView's own texts for its loading status, its failed load and its way back, which the
// form screen shows while its chunk is fetched or cannot be.
const LOADING_FORM_TEXT = 'Loading form…';
const FORM_LOAD_FAILED_TEXT = 'The form could not be loaded.';
const BACK_TO_FORMS = 'Back to forms';

/** The form screen's chunk: its one export the shell renders. */
interface FormViewChunk {
  readonly FormView: typeof FormView;
}

/** The fetch of the form screen's chunk, shared by every caller; `null` before one starts or after one fails. */
let formViewFetch: Promise<FormViewChunk> | null = null;
/** The form screen's chunk once fetched, so a later open renders the form in the same commit. */
let formViewChunk: FormViewChunk | null = null;

/** The form screen's chunk, fetched once; a failed fetch is forgotten, so the next call fetches again. */
function loadFormView(): Promise<FormViewChunk> {
  formViewFetch ??= import('./components/FormView').then(
    (chunk) => {
      formViewChunk = chunk;
      return chunk;
    },
    (error: unknown) => {
      formViewFetch = null;
      throw error;
    },
  );
  return formViewFetch;
}

/** Starts fetching the form screen ahead of an open. A failure shows only once a form opens, which fetches again. */
function preloadFormView(): void {
  loadFormView().catch(() => undefined);
}

interface FormScreenProps {
  readonly view: FormViewProps;
  /** Called as Retry is pressed, before the status and its Retry are replaced. */
  readonly onRetry: () => void;
}

/**
 * The open form: FormView, rendered at once when its chunk has been fetched, as it has after
 * a hover or focus of the form's button. Until it arrives, the form's loading status shows;
 * a failed fetch shows the failed load, as a definition that cannot be reached does, with
 * Retry and Back to forms. Nothing was open, so going back saves nothing.
 */
function FormScreen({ view, onRetry }: FormScreenProps) {
  const [chunk, setChunk] = useState<FormViewChunk | null>(() => formViewChunk);
  // Bumped by Retry; the fetch effect depends on it so that it runs again.
  const [attempt, setAttempt] = useState(0);
  const [failedAttempt, setFailedAttempt] = useState<number | null>(null);

  useEffect(() => {
    if (chunk !== null) {
      return undefined;
    }
    // Unmounting, or a Retry, makes this fetch's answer moot here; the chunk stays shared.
    let current = true;
    loadFormView().then(
      (fetched) => {
        if (current) {
          setChunk(fetched);
        }
      },
      () => {
        if (current) {
          setFailedAttempt(attempt);
        }
      },
    );
    return () => {
      current = false;
    };
  }, [chunk, attempt]);

  if (chunk !== null) {
    return <chunk.FormView {...view} />;
  }
  const failed = failedAttempt === attempt;
  return (
    <div className="form-view">
      {failed && (
        <div className="form-toolbar" role="toolbar" aria-label="Form actions">
          <button type="button" onClick={() => view.onExit(false)}>
            {BACK_TO_FORMS}
          </button>
        </div>
      )}
      <p role="status">{failed ? FORM_LOAD_FAILED_TEXT : LOADING_FORM_TEXT}</p>
      {failed && (
        <ConnectionError
          error={networkError()}
          method="GET"
          onRetry={() => {
            onRetry();
            setAttempt((previous) => previous + 1);
          }}
        />
      )}
    </div>
  );
}

/** The forms list's confirmation that Back to forms saved the draft of the form `name`. */
function savedNoticeOf(name: string): string {
  return `Changes to the ${name} record were saved.`;
}

const POSITION_PATTERN = /^[1-9]\d*$/;
// The API reads a position as a 32-bit integer and answers 400 above it. A larger `record`
// is still a record beyond the count, so it is capped here, and FormView's 404 recovery
// then opens the nearest record, as it does for any position past the end; its notice
// names `record` as written, not the cap.
const MAX_POSITION = 2_147_483_647;

const DELETED_PARAM = 'deleted';
/** The `deleted` values that switch "Show deleted" on, compared trimmed and ignoring case; any other is off. */
const DELETED_ON: ReadonlySet<string> = new Set(['1', 'true']);

/** `view`, `record` and `deleted` from a query string; null when no form is named. */
function readUrlSwitches(search: string): UrlSwitches | null {
  const params = new URLSearchParams(search);
  const view = params.get('view')?.trim() ?? '';
  if (view === '') {
    return null;
  }
  const record = params.get('record');
  const position =
    record !== null && POSITION_PATTERN.test(record) ? Math.min(Number(record), MAX_POSITION) : undefined;
  const deleted = DELETED_ON.has(params.get(DELETED_PARAM)?.trim().toLowerCase() ?? '');
  return { view, record: position, recordText: record ?? undefined, deleted };
}

/** The form whose name is `view`: the exact spelling first, then ignoring case. */
function findForm(forms: readonly FormSummary[], view: string): FormSummary | undefined {
  const lower = view.toLowerCase();
  return forms.find((form) => form.name === view) ?? forms.find((form) => form.name.toLowerCase() === lower);
}

/** The open form's URL: `position` counts the set the deleted toggle selects, so the toggle goes with it. */
function formUrl(name: string, position: number | null, includeDeleted: boolean): string {
  const params = new URLSearchParams({ view: name });
  if (position !== null) {
    params.set('record', String(position));
  }
  if (includeDeleted) {
    params.set(DELETED_PARAM, '1');
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
  if (isConnectionFailure(error)) {
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
  // Informational, unlike `notice`: the save Back to forms made, confirmed on the forms list
  // because the form's own confirmation unmounted with it. Cleared by the next screen change.
  const [savedNotice, setSavedNotice] = useState<string | null>(null);
  // Whether the URL named no form when the shell loaded, so the shell starts on the forms list.
  const startedOnListRef = useRef(switches === null);

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
    setSavedNotice(null);
    replaceUrl(form === null ? LIST_PATH : formUrl(form.name, null, form.initialIncludeDeleted));
  }, []);

  // The forms list's address is the bare path, so a URL that names no form, such as
  // `?record=3` or `?view=&record=2`, is rewritten to it once, as the shell starts. Every
  // later URL is the shell's own, and StrictMode's repeated effect finds it already there.
  useEffect(() => {
    if (startedOnListRef.current) {
      replaceUrl(LIST_PATH);
    }
  }, []);

  useEffect(() => {
    if (switches === null) {
      return;
    }
    const { view, record, recordText, deleted } = switches;
    // The form screen is fetched beside the lookup, so the open that follows waits for neither.
    preloadFormView();
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
        // The client settles an OK answer that is not a forms list as this failure too.
        setLookupFailure(result.error);
        return;
      }
      setSwitches(null);
      const form = findForm(result.data, view);
      if (form !== undefined) {
        show(
          {
            viewId: form.viewId,
            name: form.name,
            initialPosition: record,
            // User input from the URL; FormView's notice renders it as text, never as markup.
            initialPositionText: recordText,
            initialIncludeDeleted: deleted,
          },
          null,
        );
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
      show(
        {
          viewId: form.viewId,
          name: form.name,
          initialPosition: undefined,
          initialPositionText: undefined,
          initialIncludeDeleted: false,
        },
        null,
      );
    },
    [show],
  );

  const returnToList = useCallback(
    (saved: boolean) => {
      // Read before show() forgets the form that was open.
      const closed = openRef.current;
      focusTitleRef.current = true;
      show(null, null);
      if (saved && closed !== null) {
        setSavedNotice(savedNoticeOf(closed.name));
      }
    },
    [show],
  );

  const formNotFound = useCallback(() => {
    focusTitleRef.current = true;
    show(null, FORM_NOT_FOUND_NOTICE);
  }, [show]);

  const reportPosition = useCallback((form: SelectedForm, n: number | null, includeDeleted: boolean) => {
    // A form already closed must not write its URL back over the list's.
    if (openRef.current === form) {
      replaceUrl(formUrl(form.name, n, includeDeleted));
    }
  }, []);

  // Each removes the button just pressed, and the title stays on every screen, so focus goes
  // there first rather than falling to the page body.
  const focusTitle = useCallback(() => {
    headingRef.current?.focus();
  }, []);

  const dismissNotice = useCallback(() => {
    headingRef.current?.focus();
    setNotice(null);
  }, []);

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
    content = <FormsList client={client} onOpen={openFromList} onPreview={preloadFormView} />;
  } else {
    content = (
      <FormScreen
        key={selected.viewId}
        view={{
          viewId: selected.viewId,
          client,
          initialPosition: selected.initialPosition,
          initialPositionText: selected.initialPositionText,
          initialIncludeDeleted: selected.initialIncludeDeleted,
          onExit: returnToList,
          onFormNotFound: formNotFound,
          onPositionChange: (n, includeDeleted) => reportPosition(selected, n, includeDeleted),
        }}
        onRetry={focusTitle}
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
          <button type="button" onClick={() => dismissNotice()}>
            {DISMISS}
          </button>
        </div>
      )}
      {/* The shell's one status of its own; each screen keeps its own statuses. Always rendered,
          so the polite live region exists before its text arrives and the confirmation is
          announced, and unnamed, since a name on a live region can be read in place of its
          content. Visually hidden while empty. */}
      <p role="status" className={savedNotice === null ? 'visually-hidden' : 'banner banner--info'}>
        {savedNotice ?? ''}
      </p>
      {content}
    </main>
  );
}
