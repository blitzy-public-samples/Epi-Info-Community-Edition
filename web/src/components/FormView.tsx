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
// Statuses, not controls, so the toolbar-name rule above does not reach them.
const SAVING_TEXT = 'Saving…';
const SAVED_TEXT = 'Changes saved.';
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
  initialPositionText,
  initialIncludeDeleted,
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
          initialPositionText={initialPositionText}
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
  readonly initialPosition: number | undefined;
  readonly initialPositionText: string | undefined;
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
 * Reports each change of the navigator's position or of its deleted toggle, with both, so
 * a toggle that leaves n as it was is reported too. The pair the workspace mounts with
 * (`null` until the first record opens) is not a change and is not reported.
 */
function usePositionReport(
  n: number | null,
  includeDeleted: boolean,
  report: ((n: number | null, includeDeleted: boolean) => void) | undefined,
): void {
  const reportRef = useRef(report);
  useLayoutEffect(() => {
    reportRef.current = report;
  });
  const reportedRef = useRef({ n, includeDeleted });
  useEffect(() => {
    const reported = reportedRef.current;
    if (n !== reported.n || includeDeleted !== reported.includeDeleted) {
      reportedRef.current = { n, includeDeleted };
      reportRef.current?.(n, includeDeleted);
    }
  }, [n, includeDeleted]);
}

/** The workspace of a loaded form: composes navigation and the editor and renders them. */
function FormWorkspace({
  client,
  viewId,
  definition,
  initialPosition,
  initialPositionText,
  initialIncludeDeleted,
  headingRef,
  onExit,
  onFormNotFound,
  onPositionChange,
}: FormWorkspaceProps) {
  // The navigator awaits the editor's leave() before each move, while the editor needs the
  // navigator's record and callbacks; the ref breaks that cycle. Until the editor's leave
  // is installed nothing is open, so there is nothing to save.
  const leaveRef = useRef<() => Promise<boolean>>(() => Promise.resolve(true));
  // Successful saves of this workspace, counted so Back to forms can tell whether its leave saved.
  const savesRef = useRef(0);
  const navigation = useRecordNavigation({
    client,
    viewId,
    initialPosition,
    initialPositionText,
    initialIncludeDeleted,
    beforeLeave: () => leaveRef.current(),
  });
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
    onDeleted: navigation.afterDelete,
    onDiscardToNearest: navigation.openNearest,
    transitioning: navigation.transitioning,
  });
  const { leave } = editor;
  useLayoutEffect(() => {
    leaveRef.current = leave;
  }, [leave]);
  // The browser's leave prompt for a dirty draft is registered by useRecordEditor itself,
  // so none is registered here: two listeners would prompt twice.

  usePositionReport(navigation.n, navigation.includeDeleted, onPositionChange);

  const { selectedPageId, selectPage, selectField } = usePageSelection(definition);

  const [findOpen, setFindOpen] = useState(false);

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
    setFormGone(true);
  }, []);
  // Adjusted during render, so the count's 404 stays recorded after a later navigation
  // action clears navigation's load error.
  if (navigation.loadError?.kind === 'notFound' && !formGone) {
    setFindOpen(false);
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
  // stable callbacks only: navigation's actions keep their identity, and so does this one.
  const { openById } = navigation;
  const openFromGrid = useCallback(
    (id: string) => {
      void openById(id);
    },
    [openById],
  );

  // Back to forms is a leave like any move: a dirty draft is saved first, and a failed
  // save keeps the user on the record. A second press while the first is pending is ignored.
  const exitingRef = useRef(false);
  const backToForms = (): void => {
    if (exitingRef.current) {
      return;
    }
    exitingRef.current = true;
    const savesBefore = savesRef.current;
    void editor.leave().then(
      (left) => {
        exitingRef.current = false;
        if (left) {
          // The form, and the toolbar's confirmation with it, unmounts: the caller confirms the save.
          onExit(savesRef.current > savesBefore);
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

  // Where the delete dialog returns focus first. Confirming on a new draft discards it, which
  // leaves Delete disabled on the empty draft, so the dialog would fall back to the toolbar's
  // first button, Back to forms, where a keyboard user's next Enter would leave the form: the
  // heading takes focus instead. Every other close returns it to Delete, as the dialog does.
  const deleteReturnFocusRef = useRef<HTMLElement | null>(null);
  const { requestDelete, confirmDelete } = editor;
  const openDeleteDialog = (): void => {
    deleteReturnFocusRef.current = null;
    requestDelete();
  };
  const confirmDeleteDialog = (): Promise<void> => {
    if (navigation.isNew) {
      deleteReturnFocusRef.current = headingRef.current;
    }
    return confirmDelete();
  };

  return (
    <div className="form-view">
      <h2 ref={headingRef} tabIndex={-1}>
        {definition.name}
      </h2>

      <WorkspaceToolbar
        saveDisabled={saveDisabled}
        saveBlocked={saveBlocked}
        backBlocked={movesBlocked}
        saving={editor.savePending}
        saved={editor.saved}
        deleteDisabled={deleteDisabled}
        onBackToForms={backToForms}
        onSave={() => {
          void editor.save();
        }}
        onDelete={openDeleteDialog}
        onFind={() => setFindOpen(true)}
      />

      <RecordNavigator navigation={navigation} busy={busy} blocked={movesBlocked} canInsert={canInsert} />

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
        offerDiscard={editor.isDirty && !discardChosen}
        onDiscardFormGone={() => runBannerAction(() => setDiscardChosen(true))}
        editorBanner={editor.banner}
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
        onIncludeDeletedChange={navigation.setIncludeDeleted}
        listVersion={navigation.listVersion}
        openRecordId={openRecordId}
        onOpen={openFromGrid}
        onFormNotFound={handleFormNotFound}
        disabled={busy}
      />

      <WorkspaceDialogs
        client={client}
        viewId={viewId}
        definition={definition}
        busy={editor.saving}
        isNew={navigation.isNew}
        deleteDialogOpen={editor.deleteDialogOpen}
        deleteReturnFocusRef={deleteReturnFocusRef}
        onConfirmDelete={confirmDeleteDialog}
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
  readonly deleteDisabled: boolean;
  readonly onBackToForms: () => void;
  readonly onSave: () => void;
  readonly onDelete: () => void;
  readonly onFind: () => void;
}

/** The form's actions, Back to forms, Save, Delete and Find, under the names the smoke test matches. */
function WorkspaceToolbar({
  saveDisabled,
  saveBlocked,
  backBlocked,
  saving,
  saved,
  deleteDisabled,
  onBackToForms,
  onSave,
  onDelete,
  onFind,
}: WorkspaceToolbarProps) {
  const statusText = saving ? SAVING_TEXT : saved ? SAVED_TEXT : '';
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
      <button type="button" disabled={deleteDisabled} onClick={() => onDelete()}>
        {DELETE}
      </button>
      <button type="button" onClick={() => onFind()}>
        {FIND}
      </button>
      {/* Always rendered, so the status text is announced when it appears; it tells a save
          under way apart from a Save disabled by permission or protection, and confirms a
          save that succeeded until the next edit. Visually hidden only while empty. */}
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
  /** The draft has unsaved edits the user has not yet chosen to discard. */
  readonly offerDiscard: boolean;
  /** "Discard edits" on the form-gone banner: the draft may be dropped and the forms list shown. */
  readonly onDiscardFormGone: () => void;
  /** The failed save or delete; `null` when there is none. */
  readonly editorBanner: EditorBanner | null;
  /** The editor banner's actions. */
  readonly bannerHandlers: EditorBannerHandlers;
  /** The non-blocking notice that a field event could not be evaluated. */
  readonly eventNotice: string | null;
}

/**
 * The workspace's banners, in order: deleted record, navigation notice, failed load, form
 * gone, failed write, field event.
 */
function WorkspaceNotices({
  recordDeleted,
  navigationNotice,
  onDismissNavigationNotice,
  loadError,
  onRetryLoad,
  formGone,
  offerDiscard,
  onDiscardFormGone,
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

      {/* The form-gone banner, below, states a count's 404. */}
      {loadError !== null && loadError.kind !== 'notFound' && (
        <NavigationLoadFailure error={loadError} onRetry={onRetryLoad} />
      )}

      {formGone && (
        <div className="banner banner--error" role="alert">
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
  /** Focused first when the delete dialog closes; `null` leaves the dialog's own return. */
  readonly deleteReturnFocusRef: RefObject<HTMLElement>;
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
  /** Find's search answered 404: the form itself no longer exists. */
  readonly onFindFormNotFound: () => void;
}

/** The workspace's modals: delete confirmation, check-code messages and Find. */
function WorkspaceDialogs({
  client,
  viewId,
  definition,
  busy,
  isNew,
  deleteDialogOpen,
  deleteReturnFocusRef,
  onConfirmDelete,
  onCancelDelete,
  dialogs,
  onDismissDialog,
  findOpen,
  includeDeleted,
  openRecordId,
  onFindResult,
  onFindClose,
  onFindFormNotFound,
}: WorkspaceDialogsProps) {
  return (
    <>
      <ConfirmDialog
        open={deleteDialogOpen}
        busy={busy}
        isNew={isNew}
        returnFocusRef={deleteReturnFocusRef}
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
        onFormNotFound={onFindFormNotFound}
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
