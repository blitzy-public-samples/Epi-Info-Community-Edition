// The data-entry workspace of one form: toolbar, record navigator, messages, page tabs,
// record grid and dialogs. It replaces legacy EnterMainForm, Canvas and ViewExplorer.
// The workflow lives in two hooks: useRecordNavigation (which record is open and every
// move between records, GET requests only) and useRecordEditor (the draft, saves, delete,
// Retry and field events). This component composes them and renders their state; it
// decides no business rule, no position and no request, and runs no check code.
//
// Loading the form or a record sends GETs only and raises no field event (DATA-01): no
// effect here calls a save or a field-event handler. Fields render only through
// FieldRenderer and its field component map.
import {
  useCallback,
  useEffect,
  useId,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
  type KeyboardEvent as ReactKeyboardEvent,
  type MouseEvent as ReactMouseEvent,
  type MutableRefObject,
  type ReactNode,
  type RefObject,
} from 'react';
import { createPortal } from 'react-dom';

import { apiClient, type ApiClient } from '../api/client';
import {
  describeError,
  errorMessages,
  isAbortError,
  isConnectionFailure,
  networkError,
  type ApiError,
} from '../api/errors';
import type { DialogMessage, FieldDefinition, FormDefinition, PageDefinition } from '../api/types';
import { useRecordEditor, type BannerAction, type EditorBanner } from '../state/useRecordEditor';
import { useRecordNavigation, type RecordNavigation } from '../state/useRecordNavigation';
import { hasFieldErrors, isDataField } from '../validation/clientValidation';
import type { ConfirmDialog } from './ConfirmDialog';
import { ConnectionError } from './ConnectionError';
import { FieldRenderer } from './FieldRenderer';
import type { FindRecordDialog } from './FindRecordDialog';
import type { MessageDialog } from './MessageDialog';
import { PageTabs, pageTabId, pageTabPanelId } from './PageTabs';
import { RecordGrid, primeListRead, type PrimedListRead } from './RecordGrid';
import { RecordNavigator } from './RecordNavigator';
import { ValidationSummary, type BlockedAction } from './ValidationSummary';
import { fieldControlId } from './fields/fieldComponents';

export interface FormViewProps {
  /** The form to open (`metaViews.ViewId`). */
  viewId: number;
  /** The API client; defaults to the page-origin client. Tests inject `createApiClient({ baseUrl, fetch })`. */
  client?: ApiClient;
  /** 1-based position opened on load (the `record` URL parameter); absent opens the first record. */
  initialPosition?: number;
  /**
   * The `record` URL parameter as written. When that record cannot be opened as asked, the
   * navigation notice names it and says what opened instead; absent, it names `initialPosition`.
   */
  initialPositionText?: string;
  /** Whether "Show deleted" is on as the form opens (the `deleted` URL parameter); absent opens it off. */
  initialIncludeDeleted?: boolean;
  /**
   * Called once "Back to forms" has left the record: a dirty draft was saved, or there was
   * nothing to save. `saved` is `true` when a save succeeded while that leave ran, so the
   * caller can confirm it once the form, and its own confirmation, are gone.
   */
  onExit: (saved: boolean) => void;
  /** Called once when the API reports that the form does not exist. */
  onFormNotFound: () => void;
  /**
   * Called whenever the navigator's position or its "Show deleted" toggle changes, with
   * both: `n` is `null` for a new record, and counts the set the toggle selects.
   */
  onPositionChange?: (n: number | null, includeDeleted: boolean) => void;
}

/**
 * The definition load. A loaded definition carries the grid's first page, requested beside
 * it, which the workspace's grid takes as it mounts.
 */
type DefinitionState =
  | { readonly status: 'loading' }
  | {
      readonly status: 'loaded';
      readonly viewId: number;
      readonly definition: FormDefinition;
      readonly primedList: PrimedListRead;
    }
  | { readonly status: 'failed'; readonly viewId: number; readonly error: ApiError };

const LOADING_STATE: DefinitionState = { status: 'loading' };

/** Navigation's beforeLeave while no workspace is mounted: nothing is open, so nothing needs saving. */
const leaveNothing = (): Promise<boolean> => Promise.resolve(true);

/** The workspace's modals, each a chunk of its own that a form open does not wait for. */
interface DialogComponents {
  readonly confirm: typeof ConfirmDialog;
  readonly message: typeof MessageDialog;
  readonly find: typeof FindRecordDialog;
}

type DialogKind = keyof DialogComponents;

/** One dialog's chunk, shared by every workspace. */
interface DialogChunkCache<C> {
  /** Fetches the chunk and takes the dialog from it. */
  readonly load: () => Promise<C>;
  /** The fetch under way or done; `null` before one starts or after one fails. */
  fetch: Promise<C> | null;
  /** The dialog once fetched, so a later workspace renders it in its first commit. */
  fetched: C | null;
}

const dialogChunks: { readonly [K in DialogKind]: DialogChunkCache<DialogComponents[K]> } = {
  confirm: { load: () => import('./ConfirmDialog').then((chunk) => chunk.ConfirmDialog), fetch: null, fetched: null },
  message: { load: () => import('./MessageDialog').then((chunk) => chunk.MessageDialog), fetch: null, fetched: null },
  find: { load: () => import('./FindRecordDialog').then((chunk) => chunk.FindRecordDialog), fetch: null, fetched: null },
};

/** One dialog's chunk, fetched once; a failed fetch is forgotten, so the next call fetches it again. */
function loadDialog<C>(cache: DialogChunkCache<C>): Promise<C> {
  cache.fetch ??= cache.load().then(
    (component) => {
      cache.fetched = component;
      return component;
    },
    (error: unknown) => {
      cache.fetch = null;
      throw error;
    },
  );
  return cache.fetch;
}

/** Starts fetching every dialog's chunk. A failure shows only once its dialog is asked for, which fetches it again. */
function prefetchDialogs(): void {
  loadDialog(dialogChunks.confirm).catch(() => undefined);
  loadDialog(dialogChunks.message).catch(() => undefined);
  loadDialog(dialogChunks.find).catch(() => undefined);
}

/** Where a dialog's chunk stands: fetched, being fetched, or failed in the latest attempt. */
type DialogChunkStatus = 'loaded' | 'pending' | 'failed';

/** One dialog's chunk as its fetch stands. */
interface DialogChunk<K extends DialogKind> {
  /** The dialog, `null` until its chunk is fetched. */
  readonly component: DialogComponents[K] | null;
  readonly status: DialogChunkStatus;
  /** Fetches the chunk again after a failure. */
  readonly retry: () => void;
}

/**
 * One dialog's chunk, fetched once the workspace has mounted unless FormView's fetch has
 * already brought it. Each dialog's chunk has its own attempts, so one that cannot be fetched
 * holds up no other. `wanted` says the dialog is asked for: one asked for after a failed
 * fetch fetches again at once.
 */
function useDialogChunk<K extends DialogKind>(kind: K, wanted: boolean): DialogChunk<K> {
  const cache: DialogChunkCache<DialogComponents[K]> = dialogChunks[kind];
  // Boxed: a component is a function, which a state setter would call as an updater.
  const [fetched, setFetched] = useState<{ readonly component: DialogComponents[K] } | null>(() => {
    const component = cache.fetched;
    return component === null ? null : { component };
  });
  // Bumped to fetch again; the fetch effect depends on it.
  const [attempt, setAttempt] = useState(0);
  const [failedAttempt, setFailedAttempt] = useState<number | null>(null);
  const failed = fetched === null && failedAttempt === attempt;
  // Adjusted during render, so a dialog asked for after a failure never shows that failure first.
  const [wantedBefore, setWantedBefore] = useState(wanted);
  if (wanted !== wantedBefore) {
    setWantedBefore(wanted);
    if (wanted && failed) {
      setAttempt(attempt + 1);
    }
  }

  useEffect(() => {
    if (fetched !== null) {
      return undefined;
    }
    // Unmounting, or a newer attempt, makes this fetch's answer moot here; the chunk stays shared.
    let current = true;
    loadDialog(cache).then(
      (component) => {
        if (current) {
          setFetched({ component });
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
  }, [cache, fetched, attempt]);

  const retry = useCallback(() => {
    setAttempt((previous) => previous + 1);
  }, []);
  let status: DialogChunkStatus = 'pending';
  if (fetched !== null) {
    status = 'loaded';
  } else if (failed) {
    status = 'failed';
  }
  return { component: fetched?.component ?? null, status, retry };
}

/** The dialogs the toolbar opens: the delete confirmation and Find. */
type ToolbarDialog = 'delete' | 'find';

/** Which of the workspace's modals show, and how each one's chunk stands. */
interface WorkspaceModals {
  readonly confirm: DialogChunk<'confirm'>;
  readonly message: DialogChunk<'message'>;
  readonly find: DialogChunk<'find'>;
  /**
   * The toolbar dialog shown: the dialog itself, or a placeholder while its chunk is being
   * fetched. `null` when none is asked for, or the one asked for could not be fetched.
   */
  readonly toolbarDialog: ToolbarDialog | null;
  /** The check-code messages show: as their dialog, or a placeholder while its chunk is being fetched. */
  readonly messagesShown: boolean;
  /** A modal is shown or on its way, so Delete and Find open nothing else meanwhile. */
  readonly occupied: boolean;
  /** A dialog asked for could not be opened: its chunk could not be fetched. */
  readonly failed: boolean;
  /** The toolbar dialog asked for whose chunk could not be fetched; `null` for none. */
  readonly failedToolbarDialog: ToolbarDialog | null;
  /** Fetches again each chunk asked for that could not be fetched; its dialog opens once it arrives. */
  readonly retry: () => void;
}

/**
 * The workspace's modals, one flow at a time. The delete confirmation and Find open from the
 * toolbar and never together: while a modal is shown or its chunk is on its way, Delete and
 * Find open nothing (`occupied`), as the page behind a modal is inert. Check-code messages
 * arrive on their own; they show over the toolbar dialog as a newer modal, but only in a later
 * commit than the one that showed the toolbar dialog itself: two modals mounted in one commit
 * would each make the other inert, and neither would answer a key. Over a placeholder they wait
 * for the dialog it stands for. A dialog whose chunk could not be fetched is not shown and
 * blocks nothing; `failed` reports it until it is fetched, or no longer asked for.
 */
function useWorkspaceModals(deleteWanted: boolean, findWanted: boolean, messagesWanted: boolean): WorkspaceModals {
  const confirm = useDialogChunk('confirm', deleteWanted);
  const message = useDialogChunk('message', messagesWanted);
  const find = useDialogChunk('find', findWanted);

  let toolbarDialog: ToolbarDialog | null = null;
  if (deleteWanted && confirm.status !== 'failed') {
    toolbarDialog = 'delete';
  } else if (findWanted && find.status !== 'failed') {
    toolbarDialog = 'find';
  }
  const toolbarChunk = toolbarDialog === 'delete' ? confirm : toolbarDialog === 'find' ? find : null;
  // The toolbar dialog shown as itself rather than as its placeholder.
  const toolbarDialogOpen = toolbarChunk?.status === 'loaded' ? toolbarDialog : null;
  // The toolbar dialog the last commit showed as itself.
  const [committedToolbarDialog, setCommittedToolbarDialog] = useState<ToolbarDialog | null>(null);
  useEffect(() => {
    setCommittedToolbarDialog(toolbarDialogOpen);
  }, [toolbarDialogOpen]);

  const messagesOpen = messagesWanted && message.status !== 'failed';
  const messagesShown =
    messagesOpen &&
    (toolbarDialog === null || (toolbarDialogOpen !== null && committedToolbarDialog === toolbarDialogOpen));

  const deleteFailed = deleteWanted && confirm.status === 'failed';
  const findFailed = findWanted && find.status === 'failed';
  const failedChunks = [
    ...(deleteFailed ? [confirm] : []),
    ...(messagesWanted && message.status === 'failed' ? [message] : []),
    ...(findFailed ? [find] : []),
  ];
  let failedToolbarDialog: ToolbarDialog | null = null;
  if (deleteFailed) {
    failedToolbarDialog = 'delete';
  } else if (findFailed) {
    failedToolbarDialog = 'find';
  }
  return {
    confirm,
    message,
    find,
    toolbarDialog,
    messagesShown,
    occupied: toolbarDialog !== null || messagesOpen,
    failed: failedChunks.length > 0,
    failedToolbarDialog,
    retry: () => {
      for (const chunk of failedChunks) {
        chunk.retry();
      }
    },
  };
}

// Toolbar names are matched by the browser smoke test. No other control of the form view
// may contain them, nor First, Previous, Next, Last or New, which the navigator owns:
// Playwright matches accessible names by substring. Banners ("Save again") and dialogs
// ("Delete record") are the only exceptions.
const BACK_TO_FORMS = 'Back to forms';
const SAVE = 'Save';
const DELETE = 'Delete';
const FIND = 'Find';

const LOADING_TEXT = 'Loading form…';
const LOAD_FAILED_TEXT = 'The form could not be loaded.';
const FORM_NOT_FOUND_TEXT = 'This form no longer exists.';
const DELETED_TEXT = 'This record is deleted and is read-only.';
// Statuses, not controls, so the toolbar-name rule above does not reach them.
const SAVING_TEXT = 'Saving…';
const RECORD_DELETED_TEXT = 'Record deleted.';
const SAVED_TEXT = 'Changes saved.';
// A dialog's chunk could not be fetched: the server, which serves it, did not answer.
const DIALOG_FAILED_TEXT = `The dialog could not be opened. ${errorMessages.networkWithoutDraft}`;
const RETRY = 'Retry';
const DISMISS = 'Dismiss';
const DISCARD_EDITS = 'Discard edits';
// The visible text matches ConnectionError's; the accessible name adds the risk of resending.
const SAVE_AGAIN = 'Save again';
const SAVE_AGAIN_CREATE_LABEL = 'Save again (this may create a duplicate record)';
const SAVE_AGAIN_UPDATE_LABEL = 'Save again (the earlier save may already have been applied)';
// Save again resends a write that may already have been applied, so it takes the warning
// tone, apart from Retry and Dismiss beside it.
const WARNING_BUTTON_CLASS = 'button--warning';
// A control refused while a write of the draft may already have been applied: it looks and
// is announced unavailable, yet a press still runs it, and its refusal shows the banner again.
const BLOCKED_BUTTON_CLASS = 'button--blocked';

/**
 * A key per editor banner object. A refused save shows its banner again as a new object, so
 * the alert remounts and is announced again, while every render of one banner keeps its element.
 */
const bannerKeys = new WeakMap<EditorBanner, number>();
let lastBannerKey = 0;

function bannerKeyOf(banner: EditorBanner): number {
  let key = bannerKeys.get(banner);
  if (key === undefined) {
    lastBannerKey += 1;
    key = lastBannerKey;
    bannerKeys.set(banner, key);
  }
  return key;
}

/** "Trace id: …" when the message does not already end with it, as describeError's 500 text does. */
function traceLineOf(message: string, traceId: string | null): string | null {
  return traceId !== null && !message.includes(traceId) ? `Trace id: ${traceId}` : null;
}

/**
 * Calls `callback` once for each new non-null `trigger` object, from an effect. A ref
 * remembers the trigger already reported, so StrictMode's repeated effects and later
 * renders with the same trigger do not report it again.
 */
function useReportOnce(trigger: object | null, callback: () => void): void {
  const callbackRef = useRef(callback);
  useLayoutEffect(() => {
    callbackRef.current = callback;
  });
  const reportedRef = useRef<object | null>(null);
  useEffect(() => {
    if (trigger !== null && reportedRef.current !== trigger) {
      reportedRef.current = trigger;
      callbackRef.current();
    }
  }, [trigger]);
}

/**
 * The form view: loads the form definition, then renders the workspace of that form.
 * Navigation needs no definition, so it starts with the definition load: the definition,
 * N, the first record and the grid's first page are requested together. The workspace is
 * keyed by form, so another `viewId` starts fresh editor state, as navigation does for
 * itself, and the workspace's hooks run only once a definition exists.
 */
export function FormView({
  viewId,
  client = apiClient,
  initialPosition,
  initialPositionText,
  initialIncludeDeleted,
  onExit,
  onFormNotFound,
  onPositionChange,
}: FormViewProps) {
  const [state, setState] = useState<DefinitionState>(LOADING_STATE);
  // Bumped by Retry; the load effect depends on it so that it runs again.
  const [attempt, setAttempt] = useState(0);

  // Navigation's beforeLeave is the workspace's, installed through this ref while the
  // workspace is mounted. No navigation action runs before that: nothing is shown to act on.
  const beforeLeaveRef = useRef<() => Promise<boolean>>(leaveNothing);
  const navigation = useRecordNavigation({
    client,
    viewId,
    initialPosition,
    initialPositionText,
    initialIncludeDeleted,
    beforeLeave: () => beforeLeaveRef.current(),
  });
  // Read by the load effect: the grid's first page asks for navigation's set and list version.
  const navigationRef = useRef(navigation);
  useLayoutEffect(() => {
    navigationRef.current = navigation;
  });

  useEffect(() => {
    // A new load, or unmounting, aborts this one, so a late answer never replaces a newer state.
    const controller = new AbortController();
    const { includeDeleted, listVersion } = navigationRef.current;
    const primedList = primeListRead(client, viewId, includeDeleted, listVersion);
    setState(LOADING_STATE);
    void (async () => {
      try {
        const result = await client.getForm(viewId, controller.signal);
        if (!controller.signal.aborted) {
          setState(
            result.ok
              ? { status: 'loaded', viewId, definition: result.data, primedList }
              : { status: 'failed', viewId, error: result.error },
          );
        }
      } catch (e) {
        if (controller.signal.aborted || isAbortError(e)) {
          return;
        }
        // The client settles every outcome but an abort into a result, so any other
        // rejection received no response.
        setState({ status: 'failed', viewId, error: networkError() });
      }
    })();
    return () => {
      controller.abort();
      // A grid that took the page owns it; otherwise no grid will take it.
      primedList.drop();
    };
  }, [client, viewId, attempt]);

  // The dialogs' chunks are fetched as the form opens, after the effects above have sent its
  // reads, so a dialog asked for once the workspace shows seldom waits for its chunk.
  useEffect(() => {
    prefetchDialogs();
  }, []);

  // Retry removes its own button and the toolbar while the definition reloads, which would
  // drop focus to the page body. The status line stays one element from failure to loading
  // and back, so it takes focus first and keeps it, one Tab before a repeated failure's Retry.
  const statusRef = useRef<HTMLParagraphElement>(null);
  // The workspace's heading, which takes focus once a retried load succeeds.
  const headingRef = useRef<HTMLHeadingElement>(null);
  // Set by Retry until its load settles. The first load moves no focus: the shell focuses
  // its own title when a form opens.
  const retriedRef = useRef(false);

  // N and the first record were requested beside the failed definition, so Retry requests
  // them again with it, as it requests the grid's first page; every one is a GET.
  const { retryLoad } = navigation;
  const reload = useCallback(() => {
    statusRef.current?.focus();
    retriedRef.current = true;
    setState(LOADING_STATE);
    setAttempt((previous) => previous + 1);
    void retryLoad();
  }, [retryLoad]);

  // A state left from another form, in the render before the load effect resets it, is
  // still loading for this one.
  const current: DefinitionState = state.status !== 'loading' && state.viewId === viewId ? state : LOADING_STATE;
  const formNotFound = current.status === 'failed' && current.error.kind === 'notFound' ? current.error : null;
  useReportOnce(formNotFound, onFormNotFound);

  // A layout effect, so the heading takes focus in the commit that removes the status line,
  // before the browser paints: focus never rests on the page body in between.
  useLayoutEffect(() => {
    if (current.status === 'loading' || !retriedRef.current) {
      return;
    }
    retriedRef.current = false;
    // A failure keeps focus on the status line. A success unmounted it, leaving focus on
    // the page body unless the user has moved it elsewhere, where it stays.
    const active = document.activeElement;
    if (current.status === 'loaded' && (active === null || active === document.body)) {
      headingRef.current?.focus();
    }
  }, [current.status]);

  switch (current.status) {
    case 'loading':
    case 'failed':
      return (
        <DefinitionLoadStatus
          error={current.status === 'failed' ? current.error : null}
          statusRef={statusRef}
          onRetry={reload}
          onExit={onExit}
        />
      );

    case 'loaded':
      return (
        <FormWorkspace
          key={current.viewId}
          client={client}
          viewId={current.viewId}
          definition={current.definition}
          navigation={navigation}
          beforeLeaveRef={beforeLeaveRef}
          primedList={current.primedList}
          initialIncludeDeleted={initialIncludeDeleted}
          headingRef={headingRef}
          onExit={onExit}
          onFormNotFound={onFormNotFound}
          onPositionChange={onPositionChange}
        />
      );
  }
}

interface DefinitionLoadStatusProps {
  /** Why the definition could not be loaded; `null` while it is loading. */
  readonly error: ApiError | null;
  /** The status line, which Retry focuses before the reload replaces the failure's controls. */
  readonly statusRef: RefObject<HTMLParagraphElement>;
  readonly onRetry: () => void;
  readonly onExit: (saved: boolean) => void;
}

/**
 * The form definition is loading, or could not be loaded; nothing is open, so going back
 * needs no leave. Each part keeps its place among the children, so the status line stays
 * the same element from loading to failure and back, and keeps the focus Retry gives it.
 */
function DefinitionLoadStatus({ error, statusRef, onRetry, onExit }: DefinitionLoadStatusProps) {
  // While loading there is no banner, only the status line.
  let banner: ReactNode = null;
  if (error !== null && isConnectionFailure(error)) {
    banner = <ConnectionError error={error} method="GET" onRetry={onRetry} />;
  } else if (error !== null) {
    // describeError's 404 text speaks of a record; here the form itself is unknown.
    const message = error.kind === 'notFound' ? FORM_NOT_FOUND_TEXT : describeError(error);
    const traceLine = traceLineOf(message, error.traceId);
    banner = (
      <div className="banner banner--error" role="alert">
        <p>{message}</p>
        {traceLine !== null && (
          <p>
            <small>{traceLine}</small>
          </p>
        )}
        {error.kind === 'server' && (
          <button type="button" onClick={() => onRetry()}>
            {RETRY}
          </button>
        )}
      </div>
    );
  }

  return (
    <div className="form-view">
      {error !== null && (
        <div className="form-toolbar" role="toolbar" aria-label="Form actions">
          {/* No record was open, so nothing was saved. */}
          <button type="button" onClick={() => onExit(false)}>
            {BACK_TO_FORMS}
          </button>
        </div>
      )}
      <p ref={statusRef} role="status" tabIndex={-1}>
        {error === null ? LOADING_TEXT : LOAD_FAILED_TEXT}
      </p>
      {banner}
    </div>
  );
}

interface FormWorkspaceProps {
  readonly client: ApiClient;
  readonly viewId: number;
  /** Kept as loaded while the form is open: the editor starts a new field-event queue for a new object. */
  readonly definition: FormDefinition;
  /** The form's navigation, started by FormView with the definition load. */
  readonly navigation: RecordNavigation;
  /** Navigation's beforeLeave, which the workspace sets to its own while it is mounted. */
  readonly beforeLeaveRef: MutableRefObject<() => Promise<boolean>>;
  /** The grid's first page, requested beside the definition. */
  readonly primedList: PrimedListRead;
  /** The deleted toggle the form opened with, which the shell's URL already shows. */
  readonly initialIncludeDeleted: boolean | undefined;
  /**
   * The form's heading, which FormView focuses once a retried definition load succeeds,
   * and which takes focus from a banner control that its own action removed.
   */
  readonly headingRef: RefObject<HTMLHeadingElement>;
  readonly onExit: (saved: boolean) => void;
  readonly onFormNotFound: () => void;
  readonly onPositionChange: ((n: number | null, includeDeleted: boolean) => void) | undefined;
}

/** A field-focus request from the validation summary; `sequence` repeats a request for the same field. */
interface FocusRequest {
  readonly fieldName: string;
  readonly sequence: number;
}

/** A page tab's selection, whose panel is shown from its start; `sequence` repeats a selection of the same page. */
interface RevealRequest {
  readonly pageId: number;
  readonly sequence: number;
}

function byPosition(a: PageDefinition, b: PageDefinition): number {
  return a.position - b.position || a.pageId - b.pageId;
}

function firstPageId(pages: readonly PageDefinition[]): number | null {
  return [...pages].sort(byPosition)[0]?.pageId ?? null;
}

/**
 * The page to show for `fieldName`: the page of the data field of exactly that name, the only
 * field that renders the control `focusFieldControl` looks for, even when a label, group or
 * placeholder of the same name sits on an earlier page. A name no data field holds falls back
 * to the first page by position that lists it.
 */
function pageOfField(pages: readonly PageDefinition[], fieldName: string): PageDefinition | null {
  const ordered = [...pages].sort(byPosition);
  return (
    ordered.find((page) => page.fields.some((field) => field.name === fieldName && isDataField(field))) ??
    ordered.find((page) => page.fields.some((field) => field.name === fieldName)) ??
    null
  );
}

/**
 * Each field name mapped to the field {@link pageOfField} shows for it: the data field of
 * exactly that name, first by page position, else the first field by page position that
 * lists the name. One walk of the pages answers every later lookup.
 */
function fieldsByName(pages: readonly PageDefinition[]): ReadonlyMap<string, FieldDefinition> {
  const fields = new Map<string, FieldDefinition>();
  for (const page of [...pages].sort(byPosition)) {
    for (const field of page.fields) {
      const known = fields.get(field.name);
      if (known === undefined || (!isDataField(known) && isDataField(field))) {
        fields.set(field.name, field);
      }
    }
  }
  return fields;
}

/**
 * Whether an enabled field renders a control {@link focusFieldControl} can focus: any data
 * field, except an option field with no option, whose fieldset holds no radio.
 */
function hasFocusableControl(field: FieldDefinition): boolean {
  if (!isDataField(field)) {
    return false;
  }
  return field.fieldType !== 'option' || (field.options ?? []).length > 0;
}

/**
 * Focuses a field's control. An option field's id names its fieldset, which cannot take
 * focus, so its checked radio, else its first enabled radio, takes it instead.
 */
function focusFieldControl(fieldName: string): void {
  const element = document.getElementById(fieldControlId(fieldName));
  if (element === null) {
    return;
  }
  const target =
    element instanceof HTMLFieldSetElement
      ? (element.querySelector<HTMLElement>('input:checked:not(:disabled)') ??
        element.querySelector<HTMLElement>('input:not(:disabled)'))
      : element;
  target?.focus();
}

/**
 * Scrolls a selected page's panel back to its start when that start lies above the bottom
 * edge the sticky page tab strip has while stuck under the workspace header, as it does on a
 * page scrolled past its start: otherwise the user would land deep inside the new page, or
 * past its end in the grid. The stuck edge is the strip's sticky offset plus its height, not
 * its current rect: a shorter new panel can end above the viewport and take the strip with
 * it. At the top of the page the panel starts below that edge, and where the strip is not
 * sticky the panel always starts right at the strip, so nothing moves.
 */
function revealPanelStart(pageId: number): void {
  const panel = document.getElementById(pageTabPanelId(pageId));
  const tablist = document.getElementById(pageTabId(pageId))?.closest('[role="tablist"]') ?? null;
  if (panel === null || tablist === null) {
    return;
  }
  const style = getComputedStyle(tablist);
  const stickyTop = Number.parseFloat(style.top);
  if (style.position !== 'sticky' || Number.isNaN(stickyTop)) {
    return;
  }
  const stuckBottom = stickyTop + tablist.getBoundingClientRect().height;
  const hidden = stuckBottom - panel.getBoundingClientRect().top;
  // A sliver under a pixel is layout rounding, not a hidden start.
  if (hidden >= 1) {
    window.scrollBy(0, -hidden);
  }
}

/** The page on show in the workspace, and the two ways to change it. */
interface PageSelection {
  /** The page whose panel is shown; `null` when the form has no page. */
  readonly selectedPageId: number | null;
  /** Shows a page from its start, as its tab does. */
  readonly selectPage: (pageId: number) => void;
  /** Shows the page that lists `fieldName` and focuses its control, as a validation summary jump does. */
  readonly selectField: (fieldName: string) => void;
}

/**
 * The workspace's selected page and the validation summary's field-focus requests. The
 * selected page starts at the form's first page and survives record changes; only the user,
 * through a tab, or the validation summary changes it. A jump shows the field's page and
 * focuses the field's control once the commit that shows that page has run, and a repeated
 * jump to the same field focuses it again. A tab's selection, repeated or not, shows its
 * page's panel from its start (revealPanelStart); a jump instead brings its focused control
 * into view.
 */
function usePageSelection(definition: FormDefinition): PageSelection {
  const [selectedPageId, setSelectedPageId] = useState<number | null>(() => firstPageId(definition.pages));
  const [focusRequest, setFocusRequest] = useState<FocusRequest | null>(null);
  const [revealRequest, setRevealRequest] = useState<RevealRequest | null>(null);
  useEffect(() => {
    // Runs after the commit that shows the field's page, so the control is no longer hidden.
    if (focusRequest !== null) {
      focusFieldControl(focusRequest.fieldName);
    }
  }, [focusRequest]);
  // A layout effect: it measures the panel the commit just showed, and the scroll lands
  // before the browser paints that panel at the old scroll position.
  useLayoutEffect(() => {
    if (revealRequest !== null) {
      revealPanelStart(revealRequest.pageId);
    }
  }, [revealRequest]);

  const selectPage = useCallback((pageId: number) => {
    setSelectedPageId(pageId);
    setRevealRequest((previous) => ({ pageId, sequence: (previous?.sequence ?? 0) + 1 }));
  }, []);

  const selectField = useCallback(
    (fieldName: string) => {
      const page = pageOfField(definition.pages, fieldName);
      if (page !== null) {
        setSelectedPageId(page.pageId);
      }
      setFocusRequest((previous) => ({ fieldName, sequence: (previous?.sequence ?? 0) + 1 }));
    },
    [definition],
  );

  return { selectedPageId, selectPage, selectField };
}

// Custom properties on the workspace root that styles.css reads for the sticky header, the
// sticky check-code notice and the page tab strip: each part's border-box block size, in px.
const HEADER_BLOCK_SIZE_PROPERTY = '--form-view-header-block-size';
const NOTICE_BLOCK_SIZE_PROPERTY = '--form-view-notice-block-size';
const TABS_BLOCK_SIZE_PROPERTY = '--form-view-tabs-block-size';

/**
 * Publishes the block sizes of the workspace header and of the page tab strip on the
 * workspace root, where styles.css reads them: the strip sticks just below the header and
 * any check-code notice stuck under it (useStickyNoticeBlockSize), and the scroll margins
 * of the controls below keep a control that focus scrolls into view clear of all of them.
 * Both sizes change as their controls wrap with the viewport width or the text size, so a
 * ResizeObserver republishes them; where there is none (jsdom), the sizes measured at mount
 * stay. A form with no pages has no tab strip, whose size is then 0.
 */
function useStickyBlockSizes(rootRef: RefObject<HTMLElement>, headerRef: RefObject<HTMLElement>): void {
  useLayoutEffect(() => {
    const root = rootRef.current;
    const header = headerRef.current;
    if (root === null || header === null) {
      return undefined;
    }
    const tablist = root.querySelector<HTMLElement>('.page-tabs > [role="tablist"]');
    const publish = (): void => {
      root.style.setProperty(HEADER_BLOCK_SIZE_PROPERTY, `${header.getBoundingClientRect().height}px`);
      root.style.setProperty(TABS_BLOCK_SIZE_PROPERTY, `${tablist?.getBoundingClientRect().height ?? 0}px`);
    };
    publish();
    if (typeof ResizeObserver === 'undefined') {
      return undefined;
    }
    const observer = new ResizeObserver(publish);
    observer.observe(header, { box: 'border-box' });
    if (tablist !== null) {
      observer.observe(tablist, { box: 'border-box' });
    }
    return () => {
      observer.disconnect();
    };
  }, [rootRef, headerRef]);
}

/**
 * Scrolls a focused control that starts inside its scroll margin to start just below it.
 * styles.css gives each control a scroll margin as deep as the parts stuck above it, so a
 * control starting inside it is hidden, wholly or in part, under them. A control taller than
 * the room below its margin, such as a tab panel that a click on its background focuses, is
 * left where it is, and so is one with no margin: the header's controls, or any control
 * where nothing sticks above it. With `inViewOnly`, so is a control starting above the
 * viewport, which the user has scrolled away from.
 */
function keepClearOfStickyParts(control: Element, inViewOnly: boolean): void {
  const margin = Number.parseFloat(getComputedStyle(control).scrollMarginBlockStart);
  // NaN where no style sheet sets it (jsdom); 0 where nothing sticks above the control.
  if (!(margin > 0)) {
    return;
  }
  const { top, height } = control.getBoundingClientRect();
  // A sliver under a pixel is layout rounding, not a hidden control.
  if (top <= margin - 1 && (!inViewOnly || top > -1) && height <= document.documentElement.clientHeight - margin) {
    window.scrollBy(0, top - margin);
  }
}

/**
 * Publishes the block size of the sticky check-code notice on the workspace root, where
 * styles.css reads it: where the header sticks, the notice sticks just below it and the tab
 * strip below the notice, and the scroll margins of the controls below count it too, so
 * neither the notice nor a control that focus scrolls into view is hidden under another
 * stuck part. The notice comes and goes with the failures it reports, so the returned
 * callback ref, set on the notice, follows it: its size is published while it is mounted
 * and 0px once it is gone. Its lines wrap with the viewport width and grow with each field
 * that fails, so a ResizeObserver republishes it; where there is none (jsdom), the size
 * measured at its mount stays.
 *
 * A notice that appears or grows moves the tab strip stuck below it down and deepens the
 * scroll margins, but the browser scrolls nothing for that, so the control that holds focus,
 * such as the field whose own event has just failed, would be left under the notice or the
 * strip. Each time the size grows, a focused control in view is brought back clear of them.
 * Where an observer reports the notice, that is done on its report, which comes after the
 * layout that places the notice and the control, scroll anchoring included, and before the
 * paint; the size itself is published at once all the same.
 */
function useStickyNoticeBlockSize(rootRef: RefObject<HTMLElement>): (notice: HTMLElement | null) => void {
  const [notice, setNotice] = useState<HTMLElement | null>(null);
  // The size focus was last kept clear of: a notice that shrinks or goes covers nothing new.
  const clearedSizeRef = useRef(0);
  useLayoutEffect(() => {
    const root = rootRef.current;
    if (root === null) {
      return undefined;
    }
    const publish = (keepFocusClear: boolean): void => {
      const size = notice?.getBoundingClientRect().height ?? 0;
      root.style.setProperty(NOTICE_BLOCK_SIZE_PROPERTY, `${size}px`);
      if (!keepFocusClear) {
        return;
      }
      const grew = size > clearedSizeRef.current;
      clearedSizeRef.current = size;
      const focused = document.activeElement;
      if (grew && focused !== null && root.contains(focused)) {
        keepClearOfStickyParts(focused, true);
      }
    };
    if (notice === null || typeof ResizeObserver === 'undefined') {
      publish(true);
      return undefined;
    }
    publish(false);
    const observer = new ResizeObserver(() => publish(true));
    observer.observe(notice, { box: 'border-box' });
    return () => {
      observer.disconnect();
    };
  }, [rootRef, notice]);
  return setNotice;
}

/**
 * Keeps a control that takes focus in the workspace clear of the sticky header, check-code
 * notice and tab strip, so focus is never hidden under them. The browser keeps a control
 * clear of its scroll margin when focus scrolls it into view, but not when focus lands on a
 * part inside a native control, such as the day, month or year of a date input: Shift+Tab
 * would leave that date under the header. So a focused control that still starts inside its
 * scroll margin is scrolled to start just below it (keepClearOfStickyParts).
 */
function useFocusClearOfStickyParts(rootRef: RefObject<HTMLElement>): void {
  useEffect(() => {
    const root = rootRef.current;
    if (root === null) {
      return undefined;
    }
    const handleFocusIn = (event: FocusEvent): void => {
      if (event.target instanceof Element) {
        keepClearOfStickyParts(event.target, false);
      }
    };
    root.addEventListener('focusin', handleFocusIn);
    return () => {
      root.removeEventListener('focusin', handleFocusIn);
    };
  }, [rootRef]);
}

/** The roles of the workspace's banners, each an alert or a status, whose buttons run the banner actions. */
const BANNER_SELECTOR = '[role="alert"], [role="status"]';

/**
 * Keeps focus in the workspace when a banner action removes the control that held it.
 * Retry, Save again, Discard edits and Dismiss each take their banner off the page, at
 * once or when the resent write starts, which would leave focus on the page body. Each
 * action runs through the returned function, which notes the banner control focused as
 * the action starts. After the commit in which that control has left the page with focus
 * on the body, `targetRef` takes focus, in the layout phase, before the browser paints.
 *
 * Focus the user moved elsewhere meanwhile stays there, and a banner that appears, such
 * as a repeated failure's, moves no focus. A press that left focus outside the banner, as
 * a pointer press does in browsers that do not focus buttons, has no focus to return.
 */
function useBannerFocusReturn(targetRef: RefObject<HTMLElement>): (action: () => void) => void {
  // The banner control that held focus as its action ran, until it leaves the page or loses focus.
  const originRef = useRef<Element | null>(null);
  // Runs after every commit: the banner may leave in a later commit than the action's own,
  // such as the one in which a retried write starts.
  useLayoutEffect(() => {
    const origin = originRef.current;
    if (origin === null) {
      return;
    }
    const active = document.activeElement;
    if (origin.isConnected) {
      // Still on the page: it keeps waiting while it holds focus, and focus moved on is not returned.
      if (active !== origin) {
        originRef.current = null;
      }
      return;
    }
    originRef.current = null;
    if (active === null || active === document.body) {
      targetRef.current?.focus();
    }
  });
  return useCallback((action: () => void) => {
    const active = document.activeElement;
    originRef.current = active !== null && active.closest(BANNER_SELECTOR) !== null ? active : null;
    action();
  }, []);
}

/**
 * Reports each change of the navigator's position or of its deleted toggle, with both, so
 * a toggle that leaves n as it was is reported too. Changes count from the pair the shell
 * shows as the form opens: no position yet, and `openedIncludeDeleted`, the toggle it opened
 * with. A record navigation opened before the workspace mounted is reported as it mounts.
 */
function usePositionReport(
  n: number | null,
  includeDeleted: boolean,
  report: ((n: number | null, includeDeleted: boolean) => void) | undefined,
  openedIncludeDeleted: boolean,
): void {
  const reportRef = useRef(report);
  useLayoutEffect(() => {
    reportRef.current = report;
  });
  const reportedRef = useRef<{ readonly n: number | null; readonly includeDeleted: boolean }>({
    n: null,
    includeDeleted: openedIncludeDeleted,
  });
  useEffect(() => {
    const reported = reportedRef.current;
    if (n !== reported.n || includeDeleted !== reported.includeDeleted) {
      reportedRef.current = { n, includeDeleted };
      reportRef.current?.(n, includeDeleted);
    }
  }, [n, includeDeleted]);
}

/**
 * A user action a failed save can stop: Save itself, or one that leaves the open record
 * (`leave` stands for a leave no user action of the workspace asked for, such as Retry's).
 */
type RecordAction =
  | { readonly kind: 'save' | 'first' | 'previous' | 'next' | 'last' | 'new' | 'open' | 'back' | 'leave' }
  | { readonly kind: 'goTo'; readonly position: number };

const CORRECT_THE_FIELDS = 'correct the fields below.';

/** The line that says what a blocked action did not do; plain text, never a control's name. */
function blockedText(action: RecordAction): string {
  switch (action.kind) {
    case 'save':
      return `The record was not saved: ${CORRECT_THE_FIELDS}`;
    case 'first':
    case 'previous':
    case 'next':
    case 'last':
      return `Could not move to the ${action.kind} record: ${CORRECT_THE_FIELDS}`;
    case 'goTo':
      return `Could not go to record ${action.position}: ${CORRECT_THE_FIELDS}`;
    case 'new':
      return `Could not start a new record: ${CORRECT_THE_FIELDS}`;
    case 'open':
      return `Could not open the record: ${CORRECT_THE_FIELDS}`;
    case 'back':
      return `Could not return to the forms list: ${CORRECT_THE_FIELDS}`;
    case 'leave':
      return `Could not leave this record: ${CORRECT_THE_FIELDS}`;
  }
}

/** Where the workspace moves focus to show the outcome of an action. */
type OutcomeTarget = 'summary' | 'heading' | 'formGone';

/** A user action that may be stopped by a failed save, as it was started. */
interface ActionAttempt {
  readonly action: RecordAction;
  /**
   * The element focused as the action started. Its outcome takes focus only from it or
   * from the page body; `null` lets the outcome take focus from anywhere but a field.
   */
  readonly origin: Element | null;
  /** Where focus goes when the action is stopped and no validation summary says why. */
  readonly fallback?: OutcomeTarget;
  /** Told whether the leave the action asked for was granted. */
  readonly onLeave?: (left: boolean) => void;
}

/** A request to focus an outcome; `sequence` makes a repeated request a new one. */
interface OutcomeFocusRequest {
  readonly target: OutcomeTarget;
  /** Taken when `target` is not on the page, such as the summary of a save that failed with a banner. */
  readonly fallback: OutcomeTarget | null;
  readonly origin: Element | null;
  readonly sequence: number;
}

/** The elements an outcome can take focus on, each with tabIndex -1. */
interface OutcomeTargets {
  readonly heading: RefObject<HTMLElement>;
  readonly summary: RefObject<HTMLElement>;
  readonly formGone: RefObject<HTMLElement>;
}

/**
 * Whether an outcome may take focus now. Never from a field, whose focus and blur raise
 * check-code events, nor from inside a dialog. From anywhere else only when focus rests on
 * the page body or still on `origin`, the element focused as the action started (`null`
 * stands for any): focus the user has moved on stays where it is.
 */
function mayTakeFocus(origin: Element | null): boolean {
  const active = document.activeElement;
  if (active === null || active === document.body || active === document.documentElement) {
    return true;
  }
  if (active.closest('[role="tabpanel"], [aria-modal="true"]') !== null) {
    return false;
  }
  return origin === null || active === origin;
}

/**
 * Brings an action's outcome to the user where the action started out of its sight: a save
 * or move stopped by field errors focuses the validation summary, an open from the grid
 * focuses the heading the opened record's banners sit under, and a list 404 focuses the
 * form-gone banner and its "Discard edits". Focusing an element scrolls it into view, but
 * focusing the element that already holds focus does nothing, so a target still focused by
 * an earlier outcome (a repeated stopped save, a second open from the grid, after a click
 * that focused no button) is scrolled into view instead. It is centred, where focusing puts
 * an element out of view, so it lands clear of the check-code notice pinned at the top.
 * A 409 that disables Save is not an outcome here: its banner's own effect focuses the heading.
 *
 * The focus moves in a passive effect of the commit that shows the outcome: a dialog closed
 * in that commit (Find, after a result was chosen) releases the page from `inert` and returns
 * its focus in its own effect cleanup, which runs first, so the outcome's target can take
 * focus. The returned function is stable.
 */
function useOutcomeFocus(
  targets: OutcomeTargets,
): (target: OutcomeTarget, origin: Element | null, fallback?: OutcomeTarget | null) => void {
  const [request, setRequest] = useState<OutcomeFocusRequest | null>(null);
  const { heading, summary, formGone } = targets;
  useEffect(() => {
    if (request === null) {
      return;
    }
    const elementOf = (target: OutcomeTarget | null): HTMLElement | null => {
      switch (target) {
        case 'heading':
          return heading.current;
        case 'summary':
          return summary.current;
        case 'formGone':
          return formGone.current;
        case null:
          return null;
      }
    };
    const element = elementOf(request.target) ?? elementOf(request.fallback);
    if (element !== null && mayTakeFocus(request.origin)) {
      if (element === document.activeElement) {
        element.scrollIntoView({ block: 'center' });
      } else {
        element.focus();
      }
    }
  }, [request, heading, summary, formGone]);
  return useCallback((target: OutcomeTarget, origin: Element | null, fallback: OutcomeTarget | null = null) => {
    setRequest((previous) => ({ target, fallback, origin, sequence: (previous?.sequence ?? 0) + 1 }));
  }, []);
}

/** The workspace of a loaded form: composes navigation and the editor and renders them. */
function FormWorkspace({
  client,
  viewId,
  definition,
  navigation,
  beforeLeaveRef,
  primedList,
  initialIncludeDeleted,
  headingRef,
  onExit,
  onFormNotFound,
  onPositionChange,
}: FormWorkspaceProps) {
  // The navigator awaits the editor's leave() before each move (beforeLeave, below), while
  // the editor needs the navigator's record and callbacks; the ref breaks that cycle. No
  // move runs before the editor's leave is installed: nothing is on screen to start one.
  const leaveRef = useRef<() => Promise<boolean>>(() => Promise.resolve(true));

  // Outcomes of the user's actions. The summary and the form-gone banner take focus (both
  // have tabIndex -1) when an action that started out of their sight is stopped or finds
  // the form gone.
  const summaryRef = useRef<HTMLDivElement>(null);
  const formGoneRef = useRef<HTMLDivElement>(null);
  const focusOutcome = useOutcomeFocus({ heading: headingRef, summary: summaryRef, formGone: formGoneRef });
  // The record generation on screen, for a refusal reported after the render that started it.
  const generationRef = useRef(navigation.recordGeneration);
  // The action a failed save stopped, while the field errors that stopped it are shown on
  // the record it was stopped on. `attempt` grows with every stopped action, so a repeated
  // one is announced again.
  const [blocked, setBlocked] = useState<(BlockedAction & { readonly generation: number }) | null>(null);
  const blockedAttemptsRef = useRef(0);
  const noteBlocked = useCallback(
    (attempt: ActionAttempt) => {
      blockedAttemptsRef.current += 1;
      setBlocked({
        message: blockedText(attempt.action),
        attempt: blockedAttemptsRef.current,
        generation: generationRef.current,
      });
      focusOutcome('summary', attempt.origin, attempt.fallback ?? null);
    },
    [focusOutcome],
  );
  // The user action whose beforeLeave is about to run. Every navigation action calls
  // beforeLeave before its first await, so the attempt noted as the action starts is the
  // one asking; a leave no action asked for, such as Retry's, is reported as a plain leave.
  const attemptRef = useRef<ActionAttempt | null>(null);
  const attempting = useCallback(<T,>(attempt: ActionAttempt, run: () => Promise<T>): Promise<T> => {
    attemptRef.current = attempt;
    try {
      return run();
    } finally {
      attemptRef.current = null;
    }
  }, []);

  // Successful saves of this workspace, counted so Back to forms can tell whether its leave saved.
  const savesRef = useRef(0);
  // Navigation's beforeLeave while the workspace is mounted: the editor's leave(), reported
  // as the action that asked for it.
  const beforeLeave = useCallback((): Promise<boolean> => {
    const attempt: ActionAttempt = attemptRef.current ?? {
      action: { kind: 'leave' },
      origin: document.activeElement,
    };
    attemptRef.current = null;
    return leaveRef.current().then((left) => {
      attempt.onLeave?.(left);
      if (!left) {
        noteBlocked(attempt);
      }
      return left;
    });
  }, [noteBlocked]);
  useLayoutEffect(() => {
    beforeLeaveRef.current = beforeLeave;
    return () => {
      beforeLeaveRef.current = leaveNothing;
    };
  }, [beforeLeaveRef, beforeLeave]);
  useLayoutEffect(() => {
    generationRef.current = navigation.recordGeneration;
  });
  // The control the delete confirmation returns focus to as it closes. A confirmed delete
  // points it at the heading before the dialog's close is committed, so focus stays on the
  // form, whose next record is opening, rather than falling to Back to forms; so does a
  // confirmation that discards a new draft. Cancel and a failed delete leave it unset, and
  // focus goes back to Delete.
  const deleteFocusRef = useRef<HTMLElement | null>(null);
  // The record generation a confirmed delete removed its record from. The toolbar status says
  // "Record deleted." until the user moves past the record navigation lands on, edits, saves
  // or asks to delete again.
  const [deletedIn, setDeletedIn] = useState<number | null>(null);
  const editor = useRecordEditor({
    client,
    viewId,
    definition,
    record: navigation.record,
    isNew: navigation.isNew,
    recordGeneration: navigation.recordGeneration,
    onSaved: (record) => {
      savesRef.current += 1;
      navigation.afterSave(record);
    },
    // The editor reads its options when the DELETE answers, so this render's closure sees
    // the generation of the record just deleted.
    onDeleted: () => {
      deleteFocusRef.current = headingRef.current;
      setDeletedIn(navigation.recordGeneration);
      navigation.afterDelete();
    },
    onConflict: navigation.afterConflict,
    onDiscardToNearest: navigation.openNearest,
    transitioning: navigation.transitioning,
  });
  // Adjusted during render, so the status never outlives what it reports for a frame.
  if (
    deletedIn !== null &&
    (navigation.recordGeneration > deletedIn + 1 || editor.isDirty || editor.savePending || editor.deleteDialogOpen)
  ) {
    setDeletedIn(null);
  }
  // Also adjusted during render: the line explains the errors on screen, so it goes with them,
  // and with the record they were stopped on.
  if (blocked !== null && (blocked.generation !== navigation.recordGeneration || !hasFieldErrors(editor.fieldErrors))) {
    setBlocked(null);
  }
  const { leave } = editor;
  useLayoutEffect(() => {
    leaveRef.current = leave;
  }, [leave]);
  // The browser's leave prompt for a dirty draft is registered by useRecordEditor itself,
  // so none is registered here: two listeners would prompt twice.

  usePositionReport(navigation.n, navigation.includeDeleted, onPositionChange, initialIncludeDeleted === true);

  const { selectedPageId, selectPage, selectField } = usePageSelection(definition);

  const rootRef = useRef<HTMLDivElement>(null);
  const headerRef = useRef<HTMLDivElement>(null);
  useStickyBlockSizes(rootRef, headerRef);
  const eventNoticeRef = useStickyNoticeBlockSize(rootRef);
  useFocusClearOfStickyParts(rootRef);

  const [findOpen, setFindOpen] = useState(false);
  // Find closes as a result is chosen, and keeps its results until the record on screen has
  // been left for it: an open refused because the draft could not be saved finds them again.
  const [keepFindResults, setKeepFindResults] = useState(false);
  const modals = useWorkspaceModals(editor.deleteDialogOpen, findOpen, editor.dialogs.length > 0);
  // The toolbar's Delete and Find, which open the toolbar dialogs.
  const deleteButtonRef = useRef<HTMLButtonElement>(null);
  const findButtonRef = useRef<HTMLButtonElement>(null);

  // One form-not-found path for every 404 that means the form itself is gone: the count's,
  // the grid's list and Find's search. It closes Find. A clean draft hands over to the
  // forms list only on a drain of the pending field events (applied or failed) that began
  // while it was clean and ended with it still clean: an effect in flight (an assigned Age)
  // may make it dirty, and each time a dirty draft turns clean again (a new record's one
  // entry cleared) a fresh drain decides, so no event queued meanwhile is dropped. The
  // drain saves nothing, as the form is gone. A dirty draft stays on screen until the user
  // chooses "Discard edits", which hands over at once, so it is never dropped without that
  // decision. A record that 404s on open is navigation's own recovery and does not come here.
  const [formGone, setFormGone] = useState(false);
  const [discardChosen, setDiscardChosen] = useState(false);
  const [eventsDrained, setEventsDrained] = useState(false);
  const draftClean = !editor.isDirty;
  const handleFormNotFound = useCallback(() => {
    setFindOpen(false);
    setKeepFindResults(false);
    setFormGone(true);
  }, []);
  // The grid sits far below the form-gone banner, whose "Discard edits" is the way on for a
  // dirty draft, so a list 404 brings the banner to the user. Find's search 404 closes Find,
  // whose focus returns to the toolbar beside the banner, so that path moves no focus.
  const handleGridFormNotFound = useCallback(() => {
    const origin = document.activeElement;
    handleFormNotFound();
    focusOutcome('formGone', origin);
  }, [handleFormNotFound, focusOutcome]);
  // Adjusted during render, so the count's 404 stays recorded after a later navigation
  // action clears navigation's load error.
  if (navigation.loadError?.kind === 'notFound' && !formGone) {
    setFindOpen(false);
    setKeepFindResults(false);
    setFormGone(true);
  }
  // Also adjusted during render: a drain that answered for a clean draft no longer speaks
  // for it once it is dirty, even when the answer and the dirtying effect land together.
  if (eventsDrained && !draftClean) {
    setEventsDrained(false);
  }
  const { drainFieldEvents } = editor;
  useEffect(() => {
    if (!formGone || discardChosen || !draftClean) {
      return undefined;
    }
    // Unmounting, choosing Discard edits or the draft turning dirty meanwhile makes the
    // drain's answer moot.
    let current = true;
    void drainFieldEvents().then(() => {
      if (current) {
        setEventsDrained(true);
      }
    });
    return () => {
      current = false;
    };
  }, [formGone, discardChosen, draftClean, drainFieldEvents]);
  // One object for the workspace's life: the loss of the form is reported once, however
  // many 404s arrive and whenever the drained draft is found clean or is discarded.
  const [formGoneReport] = useState(() => ({}));
  useReportOnce(
    formGone && (discardChosen || (eventsDrained && draftClean)) ? formGoneReport : null,
    onFormNotFound,
  );

  // The grid is memoised and every edit of the draft re-renders this workspace, so it gets
  // stable callbacks only: navigation's actions keep their identity, and so do these. The
  // grid sits far below the form, so its open ends on the heading, under which the opened
  // record's banners sit (deleted, no longer exists, a failed read). An open stopped by a
  // failed save focuses the summary of its field errors, or the heading when a banner says
  // why instead.
  const { openById } = navigation;
  const openFromGrid = useCallback(
    (id: string) => {
      const origin = document.activeElement;
      let refused = false;
      const attempt: ActionAttempt = {
        action: { kind: 'open' },
        origin,
        fallback: 'heading',
        onLeave: (left) => {
          refused = !left;
        },
      };
      void attempting(attempt, () => openById(id)).then(() => {
        if (!refused) {
          focusOutcome('heading', origin);
        }
      });
    },
    [openById, attempting, focusOutcome],
  );

  // The navigator's moves, each noted as the action that asks the editor to leave the record.
  const attemptOf = (action: RecordAction): ActionAttempt => ({ action, origin: document.activeElement });
  const navigatorActions: RecordNavigation = {
    ...navigation,
    first: () => attempting(attemptOf({ kind: 'first' }), navigation.first),
    previous: () => attempting(attemptOf({ kind: 'previous' }), navigation.previous),
    next: () => attempting(attemptOf({ kind: 'next' }), navigation.next),
    last: () => attempting(attemptOf({ kind: 'last' }), navigation.last),
    goTo: (position) => attempting(attemptOf({ kind: 'goTo', position }), () => navigation.goTo(position)),
    newRecord: () => attempting(attemptOf({ kind: 'new' }), navigation.newRecord),
  };

  // Back to forms is a leave like any move: a dirty draft is saved first, and a failed
  // save keeps the user on the record. A second press while the first is pending is ignored.
  const exitingRef = useRef(false);
  const backToForms = (): void => {
    if (exitingRef.current) {
      return;
    }
    exitingRef.current = true;
    const attempt = attemptOf({ kind: 'back' });
    const savesBefore = savesRef.current;
    void editor.leave().then(
      (left) => {
        exitingRef.current = false;
        if (left) {
          // The form, and the toolbar's confirmation with it, unmounts: the caller confirms the save.
          onExit(savesRef.current > savesBefore);
        } else {
          noteBlocked(attempt);
        }
      },
      () => {
        exitingRef.current = false;
      },
    );
  };

  const recordDeleted = navigation.record?.recStatus === 0 || editor.deletedReadOnly;
  const nothingOpen = navigation.record === null && !navigation.isNew;
  // A write is in flight, or a save is under way, a leave's included from the start of its
  // field-event drain: Save, Delete, the navigator and the grid wait for it.
  const busy = editor.saving || editor.savePending;
  // Permission flags are null until permission awareness is delivered; only `false` restricts.
  const { canInsert, canUpdate, canDelete } = definition;
  const saveDisabled =
    busy || recordDeleted || nothingOpen || (navigation.isNew ? canInsert === false : canUpdate === false);
  // A new draft with no entry has nothing to delete or discard, so Delete waits for one.
  const deleteDisabled =
    busy || recordDeleted || nothingOpen || canDelete === false || (navigation.isNew && !editor.isDirty);
  // While a write of the draft may already have been applied, Save refuses, and so does each
  // move that would first save the dirty draft (Back to forms and the navigator's moves): a
  // press shows that write's banner again. They stay operable for that, marked unavailable.
  const saveBlocked = editor.saveBlocked && !saveDisabled;
  const movesBlocked = editor.saveBlocked && editor.isDirty;

  const isProtected = (field: FieldDefinition): boolean => editor.isProtected(field.name);
  const isCheckCodeDisabled = (field: FieldDefinition): boolean => editor.checkCodeDisabled.has(field.name);
  // The summary asks once per message on every render, so the fields on pages are looked up
  // in a map built once per definition rather than by walking the pages each time.
  const fieldsOnPages = useMemo(() => fieldsByName(definition.pages), [definition]);
  // The summary's jump focuses a control, so it is offered only for a field rendered on a page
  // whose control is enabled, not protected and not disabled by check code, and can take focus.
  const canSelectField = (name: string): boolean => {
    if (editor.isDisabled(name)) {
      return false;
    }
    const field = fieldsOnPages.get(name);
    return field !== undefined && hasFocusableControl(field);
  };
  const renderPanel = (page: PageDefinition): ReactNode => (
    <FieldRenderer
      page={page}
      definition={definition}
      values={editor.values}
      fieldErrors={editor.fieldErrors}
      isProtected={isProtected}
      isCheckCodeDisabled={isCheckCodeDisabled}
      frozen={editor.frozen}
      onChange={editor.setValue}
      onFocus={editor.onFieldFocus}
      onBlur={editor.onFieldBlur}
      onClick={editor.onFieldClick}
    />
  );

  // A 409's banner says the record is deleted and read-only, and that the edits were not
  // saved, so while it is up it stands in for the deleted status, which returns once the
  // banner is dismissed.
  const conflictBanner = editor.banner?.kind === 'deleted' ? editor.banner : null;
  // Save stays disabled on a deleted record, so focus that a pressed Save lost while it
  // saved never returns to it: the heading takes it once a 409's banner is up. It does so a
  // task after the banner's commit, not in it. A navigator button whose leave the 409
  // cancelled is enabled again by that same commit, and the shell returns the focus it lost
  // in a microtask after the commit, which runs first and keeps it. The heading takes focus
  // only when it is still on the page body by then, so focus the user or the shell placed
  // elsewhere stays there. A newer banner, the banner's removal or unmounting cancels a
  // move still pending, so none lands on a record the banner no longer speaks for.
  useEffect(() => {
    if (conflictBanner === null) {
      return undefined;
    }
    const timer = setTimeout(() => {
      const active = document.activeElement;
      if (active === null || active === document.body) {
        headingRef.current?.focus();
      }
    }, 0);
    return () => {
      clearTimeout(timer);
    };
  }, [conflictBanner, headingRef]);
  // Every banner action removes the banner whose control ran it, so each goes through
  // runBannerAction, which hands that control's focus on to the heading.
  const runBannerAction = useBannerFocusReturn(headingRef);
  const bannerHandlers: EditorBannerHandlers = {
    onRetry: () =>
      runBannerAction(() => {
        void editor.retry();
      }),
    onSaveAgain: () =>
      runBannerAction(() => {
        void editor.saveAgain();
      }),
    onDiscardEdits: () => runBannerAction(() => editor.discardEdits()),
    onDismiss: () => runBannerAction(() => editor.dismissBanner()),
  };
  const openRecordId = navigation.record?.globalRecordId ?? null;

  // Where the delete dialog returns focus first (deleteFocusRef). Confirming on a new draft
  // discards it, which leaves Delete disabled on the empty draft, so the dialog would fall back
  // to the toolbar's first button, Back to forms, where a keyboard user's next Enter would leave
  // the form: the heading takes focus instead, as it does after a confirmed delete's 204.
  //
  // One modal flow at a time: while a modal is shown or its chunk is on its way, Delete and
  // Find open nothing, as the inert page behind it would refuse their press. A toolbar dialog
  // still asked for whose chunk could not be fetched blocks nothing and gives way to the other.
  const { requestDelete, confirmDelete, cancelDelete } = editor;
  const openDeleteDialog = (): void => {
    if (modals.occupied) {
      return;
    }
    if (findOpen) {
      // Closed as its Close button would close it.
      setFindOpen(false);
      setKeepFindResults(false);
    }
    deleteFocusRef.current = null;
    requestDelete();
  };
  const openFind = (): void => {
    if (modals.occupied) {
      return;
    }
    // Sends no request: cancelling the confirmation keeps the draft.
    cancelDelete();
    setFindOpen(true);
  };
  const confirmDeleteDialog = (): Promise<void> => {
    if (navigation.isNew) {
      deleteFocusRef.current = headingRef.current;
    }
    return confirmDelete();
  };

  return (
    <div ref={rootRef} className="form-view">
      <h2 ref={headingRef} tabIndex={-1}>
        {definition.name}
      </h2>

      {/* The workspace header, sticky where the viewport has room (styles.css), so Save and
          "[n] of N" stay in reach on a long page and a dialog returns focus to an opener in
          view. It holds the two toolbars only: the check-code notice sticks just below it, and
          the other notices and the summary scroll with the page. */}
      <div ref={headerRef} className="form-view__header">
        <WorkspaceToolbar
          saveDisabled={saveDisabled}
          saveBlocked={saveBlocked}
          backBlocked={movesBlocked}
          saving={editor.savePending}
          saved={editor.saved}
          deleteConfirmed={deletedIn !== null}
          deleteDisabled={deleteDisabled}
          onBackToForms={backToForms}
          onSave={() => {
            const attempt = attemptOf({ kind: 'save' });
            void editor.save().then((saved) => {
              if (!saved) {
                noteBlocked(attempt);
              }
            });
          }}
          onDelete={openDeleteDialog}
          onFind={openFind}
          deleteButtonRef={deleteButtonRef}
          findButtonRef={findButtonRef}
        />

        <RecordNavigator navigation={navigatorActions} busy={busy} blocked={movesBlocked} canInsert={canInsert} />
      </div>

      <WorkspaceNotices
        recordDeleted={recordDeleted && conflictBanner === null}
        navigationNotice={navigation.notice}
        onDismissNavigationNotice={() => runBannerAction(() => navigation.dismissNotice())}
        loadError={navigation.loadError}
        onRetryLoad={() =>
          runBannerAction(() => {
            void navigation.retryLoad();
          })
        }
        formGone={formGone}
        formGoneRef={formGoneRef}
        offerDiscard={editor.isDirty && !discardChosen}
        onDiscardFormGone={() => runBannerAction(() => setDiscardChosen(true))}
        editorBanner={editor.banner}
        bannerHandlers={bannerHandlers}
        dialogsFailed={modals.failed}
        onRetryDialogs={() => {
          // A toolbar dialog opened by Retry takes its own toolbar button as its opener, as a
          // press of that button would, so its close returns focus there. A button that refuses
          // focus leaves it on Retry, which hands it on to the heading as the banner goes.
          const opener =
            modals.failedToolbarDialog === 'delete'
              ? deleteButtonRef.current
              : modals.failedToolbarDialog === 'find'
                ? findButtonRef.current
                : null;
          opener?.focus();
          runBannerAction(() => modals.retry());
        }}
        eventNotices={editor.eventNotices}
        eventNoticeRef={eventNoticeRef}
      />

      {/* A request-level 400 is the editor banner's text, so the summary shows field messages only. */}
      <ValidationSummary
        definition={definition}
        fieldErrors={editor.fieldErrors}
        onSelectField={selectField}
        canSelectField={canSelectField}
        blocked={blocked}
        containerRef={summaryRef}
      />

      <PageTabs
        pages={definition.pages}
        selectedPageId={selectedPageId}
        onSelect={selectPage}
        renderPanel={renderPanel}
      />

      <RecordGrid
        client={client}
        viewId={viewId}
        definition={definition}
        includeDeleted={navigation.includeDeleted}
        onIncludeDeletedChange={navigation.setIncludeDeleted}
        listVersion={navigation.listVersion}
        openRecordId={openRecordId}
        onOpen={openFromGrid}
        onFormNotFound={handleGridFormNotFound}
        disabled={busy}
        primedFirstPage={primedList}
      />

      <WorkspaceDialogs
        modals={modals}
        client={client}
        viewId={viewId}
        definition={definition}
        busy={editor.saving}
        isNew={navigation.isNew}
        deleteDialogOpen={editor.deleteDialogOpen}
        deleteFocusRef={deleteFocusRef}
        onConfirmDelete={confirmDeleteDialog}
        onCancelDelete={editor.cancelDelete}
        dialogs={editor.dialogs}
        onDismissDialog={editor.dismissDialog}
        findOpen={findOpen}
        keepFindResults={keepFindResults}
        includeDeleted={navigation.includeDeleted}
        openRecordId={openRecordId}
        onFindResult={(id) => {
          setFindOpen(false);
          setKeepFindResults(true);
          // The dialog has closed and returned its focus to Find, so the outcome of a refused
          // open takes focus from wherever it rests outside a field.
          void attempting(
            {
              action: { kind: 'open' },
              origin: null,
              onLeave: (left) => {
                if (left) {
                  setKeepFindResults(false);
                }
              },
            },
            () => navigation.openById(id),
          );
        }}
        onFindClose={() => {
          setFindOpen(false);
          setKeepFindResults(false);
        }}
        onFindFormNotFound={handleFormNotFound}
      />
    </div>
  );
}

interface WorkspaceToolbarProps {
  readonly saveDisabled: boolean;
  /** Save is enabled, yet refuses while a write of the draft may already have been applied. */
  readonly saveBlocked: boolean;
  /** Back to forms would first save the dirty draft, which that write blocks, so it refuses. */
  readonly backBlocked: boolean;
  /** A save is under way, from the field-event drain to the write's answer. */
  readonly saving: boolean;
  /** The last save of the draft succeeded and nothing has changed since. */
  readonly saved: boolean;
  /**
   * A confirmed delete removed the record, and the user has not moved on since. Saving… takes
   * precedence, and this takes precedence over the confirmation of an earlier save.
   */
  readonly deleteConfirmed: boolean;
  readonly deleteDisabled: boolean;
  readonly onBackToForms: () => void;
  readonly onSave: () => void;
  readonly onDelete: () => void;
  readonly onFind: () => void;
  /** Set on Delete, which a delete confirmation opened by Retry takes as its opener. */
  readonly deleteButtonRef: RefObject<HTMLButtonElement>;
  /** Set on Find, which a Find opened by Retry takes as its opener. */
  readonly findButtonRef: RefObject<HTMLButtonElement>;
}

/** The form's actions, Back to forms, Save, Delete and Find, under the names the smoke test matches. */
function WorkspaceToolbar({
  saveDisabled,
  saveBlocked,
  backBlocked,
  saving,
  saved,
  deleteConfirmed,
  deleteDisabled,
  onBackToForms,
  onSave,
  onDelete,
  onFind,
  deleteButtonRef,
  findButtonRef,
}: WorkspaceToolbarProps) {
  let statusText = '';
  if (saving) {
    statusText = SAVING_TEXT;
  } else if (deleteConfirmed) {
    statusText = RECORD_DELETED_TEXT;
  } else if (saved) {
    statusText = SAVED_TEXT;
  }
  return (
    <div className="form-toolbar" role="toolbar" aria-label="Form actions">
      {/* A blocked control is marked, not disabled: its press shows the banner again. */}
      <button
        type="button"
        aria-disabled={backBlocked ? true : undefined}
        className={backBlocked ? BLOCKED_BUTTON_CLASS : undefined}
        onClick={() => onBackToForms()}
      >
        {BACK_TO_FORMS}
      </button>
      <button
        type="button"
        disabled={saveDisabled}
        aria-disabled={saveBlocked ? true : undefined}
        className={saveBlocked ? BLOCKED_BUTTON_CLASS : undefined}
        onClick={() => onSave()}
      >
        {SAVE}
      </button>
      <button ref={deleteButtonRef} type="button" disabled={deleteDisabled} onClick={() => onDelete()}>
        {DELETE}
      </button>
      <button ref={findButtonRef} type="button" onClick={() => onFind()}>
        {FIND}
      </button>
      {/* Always rendered, so the status text is announced when it appears; it tells a save
          under way apart from a Save disabled by permission or protection, and confirms a
          save that succeeded until the next edit, and a delete, whose record the navigator has
          already replaced. Visually hidden only while empty. */}
      <span role="status" className={statusText === '' ? 'visually-hidden' : undefined}>
        {statusText}
      </span>
    </div>
  );
}

interface WorkspaceNoticesProps {
  /** Shows the deleted, read-only status: the record is deleted and no 409 banner already says so. */
  readonly recordDeleted: boolean;
  /** Navigation's informational text, such as a record that no longer exists. */
  readonly navigationNotice: string | null;
  readonly onDismissNavigationNotice: () => void;
  /** The failed navigation load, shown with Retry; a count's 404 is the form-gone banner's instead. */
  readonly loadError: ApiError | null;
  readonly onRetryLoad: () => void;
  /** A 404 said the form itself no longer exists. */
  readonly formGone: boolean;
  /** The form-gone banner, which takes focus when the grid far below it found the form gone. */
  readonly formGoneRef: RefObject<HTMLDivElement>;
  /** The draft has unsaved edits the user has not yet chosen to discard. */
  readonly offerDiscard: boolean;
  /** "Discard edits" on the form-gone banner: the draft may be dropped and the forms list shown. */
  readonly onDiscardFormGone: () => void;
  /** The failed save or delete; `null` when there is none. */
  readonly editorBanner: EditorBanner | null;
  /** The editor banner's actions. */
  readonly bannerHandlers: EditorBannerHandlers;
  /** A dialog asked for could not be opened: its chunk could not be fetched. */
  readonly dialogsFailed: boolean;
  /** Fetches again each chunk asked for that could not be fetched; its dialog opens once it arrives. */
  readonly onRetryDialogs: () => void;
  /** The non-blocking notices of the fields whose field events could not be evaluated. */
  readonly eventNotices: readonly string[];
  /** Set on the field-event notice while it is shown, whose block size the sticky parts below it clear. */
  readonly eventNoticeRef: (notice: HTMLElement | null) => void;
}

/**
 * The workspace's banners, in order: deleted record, navigation notice, failed load, form
 * gone, failed write, dialog that could not be fetched, field event.
 */
function WorkspaceNotices({
  recordDeleted,
  navigationNotice,
  onDismissNavigationNotice,
  loadError,
  onRetryLoad,
  formGone,
  formGoneRef,
  offerDiscard,
  onDiscardFormGone,
  editorBanner,
  bannerHandlers,
  dialogsFailed,
  onRetryDialogs,
  eventNotices,
  eventNoticeRef,
}: WorkspaceNoticesProps) {
  const shownEventNotices = useHeldWhilePointerPressed(eventNotices);
  return (
    <>
      {recordDeleted && (
        <div className="banner banner--warning" role="status">
          {DELETED_TEXT}
        </div>
      )}

      {navigationNotice !== null && (
        <div className="banner banner--info" role="status">
          <p>{navigationNotice}</p>
          <button type="button" onClick={() => onDismissNavigationNotice()}>
            {DISMISS}
          </button>
        </div>
      )}

      {/* The form-gone banner, below, states a count's 404. */}
      {loadError !== null && loadError.kind !== 'notFound' && (
        <NavigationLoadFailure error={loadError} onRetry={onRetryLoad} />
      )}

      {formGone && (
        <div ref={formGoneRef} className="banner banner--error" role="alert" tabIndex={-1}>
          <p>{FORM_NOT_FOUND_TEXT}</p>
          {offerDiscard && (
            <button type="button" onClick={() => onDiscardFormGone()}>
              {DISCARD_EDITS}
            </button>
          )}
        </div>
      )}

      {/* Keyed by the banner object, so a banner shown again remounts and is announced again. */}
      {editorBanner !== null && (
        <EditorBannerView key={bannerKeyOf(editorBanner)} banner={editorBanner} handlers={bannerHandlers} />
      )}

      {dialogsFailed && (
        <ConnectionError error={networkError()} method="GET" message={DIALOG_FAILED_TEXT} onRetry={onRetryDialogs} />
      )}

      {/* Raised by work on a field, which may sit far below; it sticks to the top of the
          viewport, or just below the header where that sticks, while its place is scrolled
          past, and takes no focus, as it blocks nothing. */}
      {shownEventNotices.length > 0 && (
        <div ref={eventNoticeRef} className="banner banner--info banner--sticky" role="status">
          {/* One block, so each notice takes its own line instead of sharing one
              with another short notice as a flex item of the banner. */}
          <div className="banner__messages">
            {shownEventNotices.map((notice, index) => (
              // One notice per field, and a field's text can change in place, so the
              // position is the key.
              <p key={index}>{notice}</p>
            ))}
          </div>
        </div>
      )}
    </>
  );
}

/**
 * `latest`, except while a pointer button is pressed anywhere in the document: then the
 * value shown when it was pressed is kept, and `latest` is shown once it is released. A
 * press moves focus, which raises field events whose notices may appear, change or go; if
 * that moved the content between press and release, the release would land elsewhere and
 * the click would be lost. The release is applied a task later, so its click is dispatched
 * first. Losing the window's focus ends the hold too, for a release the window never sees.
 * Keyboard use holds nothing.
 */
function useHeldWhilePointerPressed<T>(latest: T): T {
  const latestRef = useRef(latest);
  // Boxed, so that holding a value never reads as no hold, whatever the value is.
  const [held, setHeld] = useState<{ readonly value: T } | null>(null);

  useLayoutEffect(() => {
    latestRef.current = latest;
  });

  useEffect(() => {
    let release: ReturnType<typeof setTimeout> | undefined;
    const cancelRelease = (): void => {
      if (release !== undefined) {
        clearTimeout(release);
        release = undefined;
      }
    };
    // Captured on the window, so a handler that stops the event cannot hide the press.
    const press = (): void => {
      cancelRelease();
      // What the last commit shows, taken now: without a hold that is the latest value.
      const shown = latestRef.current;
      // A press during a hold, or before its release ran, keeps what the hold froze.
      setHeld((current) => current ?? { value: shown });
    };
    const end = (): void => {
      cancelRelease();
      release = setTimeout(() => {
        release = undefined;
        setHeld(null);
      }, 0);
    };
    window.addEventListener('pointerdown', press, true);
    window.addEventListener('pointerup', end, true);
    window.addEventListener('pointercancel', end, true);
    window.addEventListener('blur', end);
    return () => {
      cancelRelease();
      window.removeEventListener('pointerdown', press, true);
      window.removeEventListener('pointerup', end, true);
      window.removeEventListener('pointercancel', end, true);
      window.removeEventListener('blur', end);
    };
  }, []);

  return held === null ? latest : held.value;
}

interface WorkspaceDialogsProps {
  /** Which modals show, and the dialogs whose chunks have been fetched. */
  readonly modals: WorkspaceModals;
  readonly client: ApiClient;
  readonly viewId: number;
  readonly definition: FormDefinition;
  /** A save or delete is in flight. */
  readonly busy: boolean;
  readonly isNew: boolean;
  readonly deleteDialogOpen: boolean;
  /** Where the delete confirmation returns focus first as it closes; unset, it returns to Delete. */
  readonly deleteFocusRef: RefObject<HTMLElement>;
  readonly onConfirmDelete: () => Promise<void>;
  readonly onCancelDelete: () => void;
  /** Check-code DIALOG messages in execution order. */
  readonly dialogs: readonly DialogMessage[];
  readonly onDismissDialog: () => void;
  readonly findOpen: boolean;
  /** Find keeps its results across its close while the result chosen last is being opened. */
  readonly keepFindResults: boolean;
  readonly includeDeleted: boolean;
  /** The record on screen; `null` while nothing is open. */
  readonly openRecordId: string | null;
  /** A Find result was chosen. */
  readonly onFindResult: (globalRecordId: string) => void;
  readonly onFindClose: () => void;
  /** Find's search answered 404: the form itself no longer exists. */
  readonly onFindFormNotFound: () => void;
}

/**
 * The workspace's modals: delete confirmation, check-code messages and Find. A dialog renders
 * once its chunk is fetched, and Find stays mounted while closed, keeping its picked fields. A
 * modal that shows while its chunk is still being fetched is a placeholder in its place.
 */
function WorkspaceDialogs({
  modals,
  client,
  viewId,
  definition,
  busy,
  isNew,
  deleteDialogOpen,
  deleteFocusRef,
  onConfirmDelete,
  onCancelDelete,
  dialogs,
  onDismissDialog,
  findOpen,
  keepFindResults,
  includeDeleted,
  openRecordId,
  onFindResult,
  onFindClose,
  onFindFormNotFound,
}: WorkspaceDialogsProps) {
  const { toolbarDialog, messagesShown } = modals;
  const Confirm = modals.confirm.component;
  const Message = modals.message.component;
  const Find = modals.find.component;
  const toolbarChunk = toolbarDialog === 'delete' ? modals.confirm : modals.find;
  return (
    <>
      {Confirm !== null && (
        <Confirm
          open={deleteDialogOpen && toolbarDialog === 'delete'}
          busy={busy}
          isNew={isNew}
          returnFocusRef={deleteFocusRef}
          onConfirm={onConfirmDelete}
          onCancel={onCancelDelete}
        />
      )}

      {Message !== null && <Message dialogs={messagesShown ? dialogs : NO_DIALOGS} onDismiss={onDismissDialog} />}

      {Find !== null && (
        <Find
          open={findOpen && toolbarDialog === 'find'}
          client={client}
          viewId={viewId}
          definition={definition}
          includeDeleted={includeDeleted}
          openRecordId={isNew ? null : openRecordId}
          onOpen={onFindResult}
          onClose={onFindClose}
          onFormNotFound={onFindFormNotFound}
          keepResults={keepFindResults}
        />
      )}

      {/* Keyed by the dialog it stands for, so each open records its own opener. */}
      {toolbarDialog !== null && toolbarChunk.status === 'pending' && (
        <PendingDialog key={toolbarDialog} onCancel={toolbarDialog === 'delete' ? onCancelDelete : onFindClose} />
      )}

      {/* Messages keep their queue until their dialog shows them, so nothing here dismisses one. */}
      {messagesShown && modals.message.status === 'pending' && <PendingDialog />}
    </>
  );
}

/** No check-code message: what the message dialog gets while the messages wait for another modal. */
const NO_DIALOGS: readonly DialogMessage[] = [];

const OPENING_TEXT = 'Opening…';
const CANCEL = 'Cancel';
const MODAL_SELECTOR = '[aria-modal="true"]';

/** Whether the element is, or contains, a modal dialog. */
function holdsModal(element: Element): boolean {
  return element.matches(MODAL_SELECTOR) || element.querySelector(MODAL_SELECTOR) !== null;
}

/**
 * The placeholder's keys: Escape cancels the open it stands for, when it can be cancelled,
 * and Tab and Shift+Tab keep focus inside. Returns whether the key was handled.
 */
function handlePendingDialogKey(event: KeyboardEvent, focusInside: () => void, cancel: (() => void) | undefined): boolean {
  if (event.key === 'Escape') {
    event.preventDefault();
    cancel?.();
    return true;
  }
  if (event.key === 'Tab') {
    event.preventDefault();
    focusInside();
    return true;
  }
  return false;
}

interface PendingDialogProps {
  /** Cancels the open the placeholder stands for; absent for check-code messages, which wait to be shown. */
  readonly onCancel?: () => void;
}

/**
 * A modal whose chunk is still being fetched: "Opening…" in the dialogs' own backdrop and
 * box, which holds the page exactly as the modal will. Like ConfirmDialog it is a plain
 * role="dialog" element rendered into <body>: the rest of <body>, content added while it is
 * open included, is made inert, focus that lands outside is pulled back to Cancel (or to the
 * placeholder itself when there is no Cancel), Tab stays inside, and Escape or Cancel cancels
 * the open. A newer modal dialog owns focus and keys while it is open.
 *
 * On unmount, whether cancelled or replaced by the dialog it stood for, it releases what it
 * made inert and returns focus to the element focused at open when focus would otherwise be
 * lost. Passive effect cleanups run before the next commit's effects, so the dialog mounted
 * in its place takes that element as its own opener and returns focus there when it closes.
 */
function PendingDialog({ onCancel }: PendingDialogProps) {
  const labelId = useId();
  const backdropRef = useRef<HTMLDivElement>(null);
  const dialogRef = useRef<HTMLDivElement>(null);
  const cancelRef = useRef<HTMLButtonElement>(null);

  // The document listeners live for the placeholder's life and read the latest callback here.
  const onCancelRef = useRef(onCancel);
  useLayoutEffect(() => {
    onCancelRef.current = onCancel;
  });

  useEffect(() => {
    const backdrop = backdropRef.current;
    const dialog = dialogRef.current;
    if (backdrop === null || dialog === null) {
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

    const focusInside = (): void => {
      (cancelRef.current ?? dialog).focus();
    };
    // Focus moves in before the page is made inert, so the page's blur names it as the related target.
    focusInside();

    // Only the elements made inert here are released on unmount, so one that was already
    // inert stays inert. A newer modal dialog added to <body> isolates the page itself.
    const madeInert: Element[] = [];
    const makeInert = (element: Element): void => {
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

    const isOutside = (target: EventTarget | null): boolean => !(target instanceof Node && dialog.contains(target));
    // A newer modal dialog owns focus and keys while it is open: while it has made this
    // placeholder inert, and for events inside it.
    const yieldsTo = (target: EventTarget | null): boolean => {
      if (backdrop.closest('[inert]') !== null) {
        return true;
      }
      const modal = target instanceof Element ? target.closest(MODAL_SELECTOR) : null;
      return modal !== null && modal !== dialog;
    };

    const handleFocusIn = (event: FocusEvent): void => {
      if (isOutside(event.target) && !yieldsTo(event.target)) {
        focusInside();
      }
    };
    // Keys pressed inside the placeholder are handled by its own onKeyDown.
    const handleDocumentKeyDown = (event: KeyboardEvent): void => {
      if (isOutside(event.target) && !yieldsTo(event.target)) {
        handlePendingDialogKey(event, focusInside, onCancelRef.current);
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
      // Focus returns only when it has nowhere else to be: it was inside the placeholder, or
      // on <body> once the placeholder left the page.
      const focused = document.activeElement;
      if (
        opener !== null &&
        opener.isConnected &&
        (focused === null ||
          focused === document.body ||
          focused === document.documentElement ||
          backdrop.contains(focused))
      ) {
        opener.focus();
      }
    };
  }, []);

  const handleKeyDown = (event: ReactKeyboardEvent<HTMLDivElement>): void => {
    const focusInside = (): void => {
      (cancelRef.current ?? dialogRef.current)?.focus();
    };
    if (handlePendingDialogKey(event.nativeEvent, focusInside, onCancel)) {
      event.stopPropagation();
    }
  };

  // A press on the backdrop cancels nothing; preventing its default also keeps focus inside
  // the placeholder instead of moving it to the page.
  const handleBackdropMouseDown = (event: ReactMouseEvent<HTMLDivElement>): void => {
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
        aria-busy="true"
        aria-labelledby={labelId}
        tabIndex={-1}
        onKeyDown={handleKeyDown}
      >
        <p id={labelId}>{OPENING_TEXT}</p>
        {onCancel !== undefined && (
          <button ref={cancelRef} type="button" onClick={() => onCancel()}>
            {CANCEL}
          </button>
        )}
      </div>
    </div>,
    document.body,
  );
}

interface NavigationLoadFailureProps {
  readonly error: ApiError;
  readonly onRetry: () => void;
}

/** A failed count, record or position read. Every navigation load is GETs only, so Retry is always safe. */
function NavigationLoadFailure({ error, onRetry }: NavigationLoadFailureProps) {
  if (isConnectionFailure(error)) {
    return <ConnectionError error={error} method="GET" onRetry={onRetry} />;
  }
  // A 404 here comes from the count, so the form is gone and the forms list takes over.
  const message = error.kind === 'notFound' ? FORM_NOT_FOUND_TEXT : describeError(error);
  const traceLine = traceLineOf(message, error.traceId);
  // A malformed request fails the same way again; a 404 hands over to the forms list.
  const canRetry = error.kind !== 'badRequest' && error.kind !== 'validation' && error.kind !== 'notFound';
  return (
    <div className="banner banner--error" role="alert">
      <p>{message}</p>
      {traceLine !== null && (
        <p>
          <small>{traceLine}</small>
        </p>
      )}
      {canRetry && (
        <button type="button" onClick={() => onRetry()}>
          {RETRY}
        </button>
      )}
    </div>
  );
}

/**
 * The editor's banner actions, the only part of the editor the banner needs. Which of them
 * a banner offers is decided by its `actions`.
 */
interface EditorBannerHandlers {
  /** Resends the failed write; offered only when resending it is safe. */
  readonly onRetry: () => void;
  /** Resends the draft at the user's request, although the earlier save may have been applied. */
  readonly onSaveAgain: () => void;
  /** Drops the draft and opens the nearest record; offered when the record no longer exists. */
  readonly onDiscardEdits: () => void;
  /** Closes the banner and keeps the draft. */
  readonly onDismiss: () => void;
}

interface EditorBannerViewProps {
  readonly banner: EditorBanner;
  readonly handlers: EditorBannerHandlers;
}

/**
 * A failed save or delete. The editor decided the text and the actions; this only shows
 * them. A POST, PUT or DELETE that got a 503 or no response goes through ConnectionError
 * with the editor's text. A save's text says that the draft is still on screen, or that
 * the save may already have been applied, which for a create risks a duplicate record. A
 * delete's confirmation already discarded the draft, so its text says only that the
 * delete could not be confirmed. ConnectionError offers Retry only when resending is safe,
 * and otherwise an explicit "Save again".
 */
function EditorBannerView({ banner, handlers }: EditorBannerViewProps) {
  if (isConnectionFailure(banner.error)) {
    return (
      <ConnectionError
        error={banner.error}
        method={banner.method}
        isCreate={banner.isCreate}
        message={banner.message}
        onRetry={banner.actions.includes('retry') ? handlers.onRetry : undefined}
        onSaveAgain={banner.actions.includes('saveAgain') ? handlers.onSaveAgain : undefined}
        onDismiss={handlers.onDismiss}
      />
    );
  }

  const traceLine = traceLineOf(banner.message, banner.traceId);
  const tone = banner.kind === 'deleted' ? 'banner--warning' : 'banner--error';
  return (
    <div className={`banner ${tone}`} role="alert">
      <p>{banner.message}</p>
      {traceLine !== null && (
        <p>
          <small>{traceLine}</small>
        </p>
      )}
      <div className="banner__actions">
        {banner.actions.map((action) => (
          <BannerActionButton key={action} action={action} isCreate={banner.isCreate} handlers={handlers} />
        ))}
        <button type="button" onClick={() => handlers.onDismiss()}>
          {DISMISS}
        </button>
      </div>
    </div>
  );
}

interface BannerActionButtonProps {
  readonly action: BannerAction;
  readonly isCreate: boolean;
  readonly handlers: EditorBannerHandlers;
}

function BannerActionButton({ action, isCreate, handlers }: BannerActionButtonProps) {
  switch (action) {
    case 'retry':
      return (
        <button type="button" onClick={() => handlers.onRetry()}>
          {RETRY}
        </button>
      );
    case 'saveAgain':
      return (
        <button
          type="button"
          className={WARNING_BUTTON_CLASS}
          aria-label={isCreate ? SAVE_AGAIN_CREATE_LABEL : SAVE_AGAIN_UPDATE_LABEL}
          onClick={() => handlers.onSaveAgain()}
        >
          {SAVE_AGAIN}
        </button>
      );
    case 'discardEdits':
      return (
        <button type="button" onClick={() => handlers.onDiscardEdits()}>
          {DISCARD_EDITS}
        </button>
      );
  }
}
