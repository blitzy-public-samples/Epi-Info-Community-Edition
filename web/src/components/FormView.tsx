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
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
  type ReactNode,
  type RefObject,
} from 'react';

import { apiClient, type ApiClient } from '../api/client';
import { describeError, isAbortError, networkError, type ApiError } from '../api/errors';
import type { DialogMessage, FieldDefinition, FormDefinition, PageDefinition } from '../api/types';
import { useRecordEditor, type BannerAction, type EditorBanner } from '../state/useRecordEditor';
import { useRecordNavigation } from '../state/useRecordNavigation';
import { isDataField } from '../validation/clientValidation';
import { ConfirmDialog } from './ConfirmDialog';
import { ConnectionError } from './ConnectionError';
import { FieldRenderer } from './FieldRenderer';
import { FindRecordDialog } from './FindRecordDialog';
import { MessageDialog } from './MessageDialog';
import { PageTabs } from './PageTabs';
import { RecordGrid } from './RecordGrid';
import { RecordNavigator } from './RecordNavigator';
import { ValidationSummary } from './ValidationSummary';
import { fieldControlId } from './fields/fieldComponents';

export interface FormViewProps {
  /** The form to open (`metaViews.ViewId`). */
  viewId: number;
  /** The API client; defaults to the page-origin client. Tests inject `createApiClient({ baseUrl, fetch })`. */
  client?: ApiClient;
  /** 1-based position opened on load (the `record` URL parameter); absent opens the first record. */
  initialPosition?: number;
  /** Called once "Back to forms" has left the record: a dirty draft was saved, or there was nothing to save. */
  onExit: () => void;
  /** Called once when the API reports that the form does not exist. */
  onFormNotFound: () => void;
  /** Called whenever the navigator's position changes; `null` for a new record. */
  onPositionChange?: (n: number | null) => void;
}

type DefinitionState =
  | { readonly status: 'loading' }
  | { readonly status: 'loaded'; readonly viewId: number; readonly definition: FormDefinition }
  | { readonly status: 'failed'; readonly viewId: number; readonly error: ApiError };

const LOADING_STATE: DefinitionState = { status: 'loading' };

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
// A status, not a control, so the toolbar-name rule above does not reach it.
const SAVING_TEXT = 'Saving…';
const RETRY = 'Retry';
const DISMISS = 'Dismiss';
const DISCARD_EDITS = 'Discard edits';
// The visible text matches ConnectionError's; the accessible name adds the risk of resending.
const SAVE_AGAIN = 'Save again';
const SAVE_AGAIN_CREATE_LABEL = 'Save again (this may create a duplicate record)';
const SAVE_AGAIN_UPDATE_LABEL = 'Save again (the earlier save may already have been applied)';

/** "Trace id: …" when the message does not already end with it, as describeError's 500 text does. */
function traceLineOf(message: string, traceId: string | null): string | null {
  return traceId !== null && !message.includes(traceId) ? `Trace id: ${traceId}` : null;
}

function isConnectionFailure(error: ApiError): boolean {
  return error.kind === 'unavailable' || error.kind === 'network';
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
 * The workspace is keyed by form, so another `viewId` starts fresh navigation and editor
 * state, and its hooks run only once a definition exists.
 */
export function FormView({
  viewId,
  client = apiClient,
  initialPosition,
  onExit,
  onFormNotFound,
  onPositionChange,
}: FormViewProps) {
  const [state, setState] = useState<DefinitionState>(LOADING_STATE);
  // Bumped by Retry; the load effect depends on it so that it runs again.
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    // A new load, or unmounting, aborts this one, so a late answer never replaces a newer state.
    const controller = new AbortController();
    setState(LOADING_STATE);
    void (async () => {
      try {
        const result = await client.getForm(viewId, controller.signal);
        if (!controller.signal.aborted) {
          setState(
            result.ok
              ? { status: 'loaded', viewId, definition: result.data }
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
    };
  }, [client, viewId, attempt]);

  // Retry removes its own button and the toolbar while the definition reloads, which would
  // drop focus to the page body. The status line stays one element from failure to loading
  // and back, so it takes focus first and keeps it, one Tab before a repeated failure's Retry.
  const statusRef = useRef<HTMLParagraphElement>(null);
  // The workspace's heading, which takes focus once a retried load succeeds.
  const headingRef = useRef<HTMLHeadingElement>(null);
  // Set by Retry until its load settles. The first load moves no focus: the shell focuses
  // its own title when a form opens.
  const retriedRef = useRef(false);

  const reload = useCallback(() => {
    statusRef.current?.focus();
    retriedRef.current = true;
    setState(LOADING_STATE);
    setAttempt((previous) => previous + 1);
  }, []);

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
          initialPosition={initialPosition}
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
  readonly onExit: () => void;
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
          <button type="button" onClick={() => onExit()}>
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
  readonly initialPosition: number | undefined;
  /**
   * The form's heading, which FormView focuses once a retried definition load succeeds,
   * and which takes focus from a banner control that its own action removed.
   */
  readonly headingRef: RefObject<HTMLHeadingElement>;
  readonly onExit: () => void;
  readonly onFormNotFound: () => void;
  readonly onPositionChange: ((n: number | null) => void) | undefined;
}

/** A field-focus request from the validation summary; `sequence` repeats a request for the same field. */
interface FocusRequest {
  readonly fieldName: string;
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

/** The page on show in the workspace, and the two ways to change it. */
interface PageSelection {
  /** The page whose panel is shown; `null` when the form has no page. */
  readonly selectedPageId: number | null;
  /** Shows a page, as its tab does. */
  readonly selectPage: (pageId: number) => void;
  /** Shows the page that lists `fieldName` and focuses its control, as a validation summary jump does. */
  readonly selectField: (fieldName: string) => void;
}

/**
 * The workspace's selected page and the validation summary's field-focus requests. The
 * selected page starts at the form's first page and survives record changes; only the user,
 * through a tab, or the validation summary changes it. A jump shows the field's page and
 * focuses the field's control once the commit that shows that page has run, and a repeated
 * jump to the same field focuses it again.
 */
function usePageSelection(definition: FormDefinition): PageSelection {
  const [selectedPageId, setSelectedPageId] = useState<number | null>(() => firstPageId(definition.pages));
  const [focusRequest, setFocusRequest] = useState<FocusRequest | null>(null);
  useEffect(() => {
    // Runs after the commit that shows the field's page, so the control is no longer hidden.
    if (focusRequest !== null) {
      focusFieldControl(focusRequest.fieldName);
    }
  }, [focusRequest]);

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

  return { selectedPageId, selectPage: setSelectedPageId, selectField };
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
 * Reports each change of the navigator's position. The position the workspace mounts
 * with (`null` until the first record opens) is not a change and is not reported.
 */
function usePositionReport(n: number | null, report: ((n: number | null) => void) | undefined): void {
  const reportRef = useRef(report);
  useLayoutEffect(() => {
    reportRef.current = report;
  });
  const reportedRef = useRef<number | null>(n);
  useEffect(() => {
    if (n !== reportedRef.current) {
      reportedRef.current = n;
      reportRef.current?.(n);
    }
  }, [n]);
}

/** The workspace of a loaded form: composes navigation and the editor and renders them. */
function FormWorkspace({
  client,
  viewId,
  definition,
  initialPosition,
  headingRef,
  onExit,
  onFormNotFound,
  onPositionChange,
}: FormWorkspaceProps) {
  // The navigator awaits the editor's leave() before each move, while the editor needs the
  // navigator's record and callbacks; the ref breaks that cycle. Until the editor's leave
  // is installed nothing is open, so there is nothing to save.
  const leaveRef = useRef<() => Promise<boolean>>(() => Promise.resolve(true));
  const navigation = useRecordNavigation({
    client,
    viewId,
    initialPosition,
    beforeLeave: () => leaveRef.current(),
  });
  const editor = useRecordEditor({
    client,
    viewId,
    definition,
    record: navigation.record,
    isNew: navigation.isNew,
    recordGeneration: navigation.recordGeneration,
    onSaved: navigation.afterSave,
    onDeleted: navigation.afterDelete,
    onDiscardToNearest: navigation.openNearest,
  });
  const { leave } = editor;
  useLayoutEffect(() => {
    leaveRef.current = leave;
  }, [leave]);
  // The browser's leave prompt for a dirty draft is registered by useRecordEditor itself,
  // so none is registered here: two listeners would prompt twice.

  usePositionReport(navigation.n, onPositionChange);

  // A count that answers 404 means the form itself is gone.
  const formGone = navigation.loadError?.kind === 'notFound' ? navigation.loadError : null;
  useReportOnce(formGone, onFormNotFound);

  const { selectedPageId, selectPage, selectField } = usePageSelection(definition);

  const [findOpen, setFindOpen] = useState(false);

  // Back to forms is a leave like any move: a dirty draft is saved first, and a failed
  // save keeps the user on the record. A second press while the first is pending is ignored.
  const exitingRef = useRef(false);
  const backToForms = (): void => {
    if (exitingRef.current) {
      return;
    }
    exitingRef.current = true;
    void editor.leave().then(
      (left) => {
        exitingRef.current = false;
        if (left) {
          onExit();
        }
      },
      () => {
        exitingRef.current = false;
      },
    );
  };

  const recordDeleted = navigation.record?.recStatus === 0 || editor.deletedReadOnly;
  const nothingOpen = navigation.record === null && !navigation.isNew;
  // Permission flags are null until permission awareness is delivered; only `false` restricts.
  const { canInsert, canUpdate, canDelete } = definition;
  const saveDisabled =
    editor.saving ||
    editor.savePending ||
    recordDeleted ||
    nothingOpen ||
    (navigation.isNew ? canInsert === false : canUpdate === false);
  const deleteDisabled = editor.saving || recordDeleted || nothingOpen || canDelete === false;

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
      onChange={editor.setValue}
      onFocus={editor.onFieldFocus}
      onBlur={editor.onFieldBlur}
      onClick={editor.onFieldClick}
    />
  );

  // A 409 banner says the same as the deleted banner, which is shown instead.
  const editorBanner =
    editor.banner !== null && !(recordDeleted && editor.banner.kind === 'deleted') ? editor.banner : null;
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

  return (
    <div className="form-view">
      <h2 ref={headingRef} tabIndex={-1}>
        {definition.name}
      </h2>

      <WorkspaceToolbar
        saveDisabled={saveDisabled}
        saving={editor.savePending}
        deleteDisabled={deleteDisabled}
        onBackToForms={backToForms}
        onSave={() => {
          void editor.save();
        }}
        onDelete={editor.requestDelete}
        onFind={() => setFindOpen(true)}
      />

      <RecordNavigator navigation={navigation} busy={editor.saving} canInsert={canInsert} />

      <WorkspaceNotices
        recordDeleted={recordDeleted}
        navigationNotice={navigation.notice}
        onDismissNavigationNotice={() => runBannerAction(() => navigation.dismissNotice())}
        loadError={navigation.loadError}
        onRetryLoad={() =>
          runBannerAction(() => {
            void navigation.retryLoad();
          })
        }
        editorBanner={editorBanner}
        bannerHandlers={bannerHandlers}
        eventNotice={editor.eventNotice}
      />

      {/* A request-level 400 is the editor banner's text, so the summary shows field messages only. */}
      <ValidationSummary
        definition={definition}
        fieldErrors={editor.fieldErrors}
        onSelectField={selectField}
        canSelectField={canSelectField}
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
        onIncludeDeletedChange={(value) => {
          void navigation.setIncludeDeleted(value);
        }}
        listVersion={navigation.listVersion}
        openRecordId={openRecordId}
        onOpen={(id) => {
          void navigation.openById(id);
        }}
        disabled={editor.saving}
      />

      <WorkspaceDialogs
        client={client}
        viewId={viewId}
        definition={definition}
        busy={editor.saving}
        isNew={navigation.isNew}
        deleteDialogOpen={editor.deleteDialogOpen}
        onConfirmDelete={editor.confirmDelete}
        onCancelDelete={editor.cancelDelete}
        dialogs={editor.dialogs}
        onDismissDialog={editor.dismissDialog}
        findOpen={findOpen}
        includeDeleted={navigation.includeDeleted}
        openRecordId={openRecordId}
        onFindResult={(id) => {
          setFindOpen(false);
          void navigation.openById(id);
        }}
        onFindClose={() => setFindOpen(false)}
      />
    </div>
  );
}

interface WorkspaceToolbarProps {
  readonly saveDisabled: boolean;
  /** A save is under way, from the field-event drain to the write's answer. */
  readonly saving: boolean;
  readonly deleteDisabled: boolean;
  readonly onBackToForms: () => void;
  readonly onSave: () => void;
  readonly onDelete: () => void;
  readonly onFind: () => void;
}

/** The form's actions, Back to forms, Save, Delete and Find, under the names the smoke test matches. */
function WorkspaceToolbar({
  saveDisabled,
  saving,
  deleteDisabled,
  onBackToForms,
  onSave,
  onDelete,
  onFind,
}: WorkspaceToolbarProps) {
  return (
    <div className="form-toolbar" role="toolbar" aria-label="Form actions">
      <button type="button" onClick={() => onBackToForms()}>
        {BACK_TO_FORMS}
      </button>
      <button type="button" disabled={saveDisabled} onClick={() => onSave()}>
        {SAVE}
      </button>
      <button type="button" disabled={deleteDisabled} onClick={() => onDelete()}>
        {DELETE}
      </button>
      <button type="button" onClick={() => onFind()}>
        {FIND}
      </button>
      {/* Always rendered, so the status text is announced when it appears; it tells a save
          under way apart from a Save disabled by permission or protection. */}
      <span role="status" className={saving ? undefined : 'visually-hidden'}>
        {saving ? SAVING_TEXT : ''}
      </span>
    </div>
  );
}

interface WorkspaceNoticesProps {
  readonly recordDeleted: boolean;
  /** Navigation's informational text, such as a record that no longer exists. */
  readonly navigationNotice: string | null;
  readonly onDismissNavigationNotice: () => void;
  /** The failed navigation load, shown with Retry. */
  readonly loadError: ApiError | null;
  readonly onRetryLoad: () => void;
  /** The failed save or delete; `null` when there is none or the deleted warning says the same. */
  readonly editorBanner: EditorBanner | null;
  /** The editor banner's actions. */
  readonly bannerHandlers: EditorBannerHandlers;
  /** The non-blocking notice that a field event could not be evaluated. */
  readonly eventNotice: string | null;
}

/** The workspace's banners, in order: deleted record, navigation notice, failed load, failed write, field event. */
function WorkspaceNotices({
  recordDeleted,
  navigationNotice,
  onDismissNavigationNotice,
  loadError,
  onRetryLoad,
  editorBanner,
  bannerHandlers,
  eventNotice,
}: WorkspaceNoticesProps) {
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

      {loadError !== null && <NavigationLoadFailure error={loadError} onRetry={onRetryLoad} />}

      {editorBanner !== null && <EditorBannerView banner={editorBanner} handlers={bannerHandlers} />}

      {eventNotice !== null && (
        <div className="banner banner--info" role="status">
          <p>{eventNotice}</p>
        </div>
      )}
    </>
  );
}

interface WorkspaceDialogsProps {
  readonly client: ApiClient;
  readonly viewId: number;
  readonly definition: FormDefinition;
  /** A save or delete is in flight. */
  readonly busy: boolean;
  readonly isNew: boolean;
  readonly deleteDialogOpen: boolean;
  readonly onConfirmDelete: () => Promise<void>;
  readonly onCancelDelete: () => void;
  /** Check-code DIALOG messages in execution order. */
  readonly dialogs: readonly DialogMessage[];
  readonly onDismissDialog: () => void;
  readonly findOpen: boolean;
  readonly includeDeleted: boolean;
  /** The record on screen; `null` while nothing is open. */
  readonly openRecordId: string | null;
  /** A Find result was chosen. */
  readonly onFindResult: (globalRecordId: string) => void;
  readonly onFindClose: () => void;
}

/** The workspace's modals: delete confirmation, check-code messages and Find. */
function WorkspaceDialogs({
  client,
  viewId,
  definition,
  busy,
  isNew,
  deleteDialogOpen,
  onConfirmDelete,
  onCancelDelete,
  dialogs,
  onDismissDialog,
  findOpen,
  includeDeleted,
  openRecordId,
  onFindResult,
  onFindClose,
}: WorkspaceDialogsProps) {
  return (
    <>
      <ConfirmDialog
        open={deleteDialogOpen}
        busy={busy}
        isNew={isNew}
        onConfirm={onConfirmDelete}
        onCancel={onCancelDelete}
      />

      <MessageDialog dialogs={dialogs} onDismiss={onDismissDialog} />

      <FindRecordDialog
        open={findOpen}
        client={client}
        viewId={viewId}
        definition={definition}
        includeDeleted={includeDeleted}
        openRecordId={isNew ? null : openRecordId}
        onOpen={onFindResult}
        onClose={onFindClose}
      />
    </>
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
