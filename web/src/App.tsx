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
import { useCallback, useEffect, useRef, useState, type FocusEvent, type ReactNode } from 'react';

import { apiClient, type ApiClient } from './api/client';
import type { FormSummary } from './api/types';
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

/** The application shell: a title, a one-shot notice, then the forms list or the open form. */
export default function App({ client = apiClient }: AppProps) {
  // Read once, on the first render; every later URL is the shell's own.
  const [switches, setSwitches] = useState<UrlSwitches | null>(() => readUrlSwitches(window.location.search));
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
    const finish = (form: SelectedForm | null, nextNotice: string | null): void => {
      setSwitches(null);
      show(form, nextNotice);
    };
    // Unmounting, or StrictMode's repeated effect, aborts this lookup; only the last one applies.
    const controller = new AbortController();
    void (async () => {
      try {
        const result = await client.listForms(controller.signal);
        if (controller.signal.aborted) {
          return;
        }
        const form = result.ok ? findForm(result.data, view) : undefined;
        if (form !== undefined) {
          finish({ viewId: form.viewId, name: form.name, initialPosition: record }, null);
        } else {
          // `view` is user input from the URL; React renders it as text, never as markup.
          finish(null, result.ok ? `Form '${view}' was not found.` : `Form '${view}' could not be opened.`);
        }
      } catch {
        // The client rejects only when the call is aborted; anything else received no answer.
        if (!controller.signal.aborted) {
          finish(null, `Form '${view}' could not be opened.`);
        }
      }
    })();
    return () => {
      controller.abort();
    };
  }, [client, switches, show]);

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

  let content: ReactNode;
  if (switches !== null) {
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
