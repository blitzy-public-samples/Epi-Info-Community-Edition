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
import { useCallback, useEffect, useLayoutEffect, useRef, useState, type ReactNode } from 'react';

import { apiClient, type ApiClient } from '../api/client';
import { describeError, isAbortError, networkError, type ApiError } from '../api/errors';
import type { FieldDefinition, FormDefinition, PageDefinition } from '../api/types';
import { useRecordEditor, type BannerAction, type EditorBanner, type RecordEditor } from '../state/useRecordEditor';
import { useRecordNavigation } from '../state/useRecordNavigation';
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
const FORM_NOT_FOUND_TEXT = 'This form no longer exists.';
const DELETED_TEXT = 'This record is deleted and is read-only.';
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

  const reload = useCallback(() => {
    setState(LOADING_STATE);
    setAttempt((previous) => previous + 1);
  }, []);

  // A state left from another form, in the render before the load effect resets it, is
  // still loading for this one.
  const current: DefinitionState = state.status !== 'loading' && state.viewId === viewId ? state : LOADING_STATE;
  const formNotFound = current.status === 'failed' && current.error.kind === 'notFound' ? current.error : null;
  useReportOnce(formNotFound, onFormNotFound);

  switch (current.status) {
    case 'loading':
      return (
        <div className="form-view">
          <p role="status">{LOADING_TEXT}</p>
        </div>
      );

    case 'failed':
      return <DefinitionLoadFailure error={current.error} onRetry={reload} onExit={onExit} />;

    case 'loaded':
      return (
        <FormWorkspace
          key={current.viewId}
          client={client}
          viewId={current.viewId}
          definition={current.definition}
          initialPosition={initialPosition}
          onExit={onExit}
          onFormNotFound={onFormNotFound}
          onPositionChange={onPositionChange}
        />
      );
  }
}

interface DefinitionLoadFailureProps {
  readonly error: ApiError;
  readonly onRetry: () => void;
  readonly onExit: () => void;
}

/** The form definition could not be loaded; nothing is open, so going back needs no leave. */
function DefinitionLoadFailure({ error, onRetry, onExit }: DefinitionLoadFailureProps) {
  let banner: ReactNode;
  if (isConnectionFailure(error)) {
    banner = <ConnectionError error={error} method="GET" onRetry={onRetry} />;
  } else {
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
      <div className="form-toolbar" role="toolbar" aria-label="Form actions">
        <button type="button" onClick={() => onExit()}>
          {BACK_TO_FORMS}
        </button>
      </div>
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

/** The page that lists `fieldName`, the first by position when several do. */
function pageOfField(pages: readonly PageDefinition[], fieldName: string): PageDefinition | null {
  return [...pages].sort(byPosition).find((page) => page.fields.some((field) => field.name === fieldName)) ?? null;
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

  // The selected page survives record changes; only the user, or the validation summary, changes it.
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
    recordDeleted ||
    nothingOpen ||
    (navigation.isNew ? canInsert === false : canUpdate === false);
  const deleteDisabled = editor.saving || recordDeleted || nothingOpen || canDelete === false;

  const isProtected = (field: FieldDefinition): boolean => editor.isProtected(field.name);
  const isCheckCodeDisabled = (field: FieldDefinition): boolean => editor.checkCodeDisabled.has(field.name);
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
  const openRecordId = navigation.record?.globalRecordId ?? null;

  return (
    <div className="form-view">
      <h2>{definition.name}</h2>

      <div className="form-toolbar" role="toolbar" aria-label="Form actions">
        <button type="button" onClick={backToForms}>
          {BACK_TO_FORMS}
        </button>
        <button
          type="button"
          disabled={saveDisabled}
          onClick={() => {
            void editor.save();
          }}
        >
          {SAVE}
        </button>
        <button type="button" disabled={deleteDisabled} onClick={() => editor.requestDelete()}>
          {DELETE}
        </button>
        <button type="button" onClick={() => setFindOpen(true)}>
          {FIND}
        </button>
      </div>

      <RecordNavigator navigation={navigation} busy={editor.saving} canInsert={canInsert} />

      {recordDeleted && (
        <div className="banner banner--warning" role="status">
          {DELETED_TEXT}
        </div>
      )}

      {navigation.notice !== null && (
        <div className="banner banner--info" role="status">
          <p>{navigation.notice}</p>
          <button type="button" onClick={() => navigation.dismissNotice()}>
            {DISMISS}
          </button>
        </div>
      )}

      {navigation.loadError !== null && (
        <NavigationLoadFailure
          error={navigation.loadError}
          onRetry={() => {
            void navigation.retryLoad();
          }}
        />
      )}

      {editorBanner !== null && <EditorBannerView banner={editorBanner} editor={editor} />}

      {editor.eventNotice !== null && (
        <div className="banner banner--info" role="status">
          <p>{editor.eventNotice}</p>
        </div>
      )}

      {/* A request-level 400 is the editor banner's text, so the summary shows field messages only. */}
      <ValidationSummary definition={definition} fieldErrors={editor.fieldErrors} onSelectField={selectField} />

      <PageTabs
        pages={definition.pages}
        selectedPageId={selectedPageId}
        onSelect={setSelectedPageId}
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

      <ConfirmDialog
        open={editor.deleteDialogOpen}
        busy={editor.saving}
        isNew={navigation.isNew}
        onConfirm={editor.confirmDelete}
        onCancel={editor.cancelDelete}
      />

      <MessageDialog dialogs={editor.dialogs} onDismiss={editor.dismissDialog} />

      <FindRecordDialog
        open={findOpen}
        client={client}
        viewId={viewId}
        definition={definition}
        includeDeleted={navigation.includeDeleted}
        openRecordId={navigation.isNew ? null : openRecordId}
        onOpen={(id) => {
          setFindOpen(false);
          void navigation.openById(id);
        }}
        onClose={() => setFindOpen(false)}
      />
    </div>
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

interface EditorBannerViewProps {
  readonly banner: EditorBanner;
  readonly editor: RecordEditor;
}

/**
 * A failed save or delete. The editor decided the text and the actions; this only shows
 * them, and the draft stays on screen in every case. A POST or PUT that got a 503 or no
 * response goes through ConnectionError, which offers Retry only when nothing was written
 * and otherwise an explicit "Save again". A DELETE keeps the editor's own text, because
 * its confirmation already discarded the draft that ConnectionError's text speaks of.
 */
function EditorBannerView({ banner, editor }: EditorBannerViewProps) {
  if (banner.method !== 'DELETE' && isConnectionFailure(banner.error)) {
    return (
      <ConnectionError
        error={banner.error}
        method={banner.method}
        isCreate={banner.isCreate}
        onRetry={
          banner.actions.includes('retry')
            ? () => {
                void editor.retry();
              }
            : undefined
        }
        onSaveAgain={
          banner.actions.includes('saveAgain')
            ? () => {
                void editor.saveAgain();
              }
            : undefined
        }
        onDismiss={() => editor.dismissBanner()}
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
          <BannerActionButton key={action} action={action} isCreate={banner.isCreate} editor={editor} />
        ))}
        <button type="button" onClick={() => editor.dismissBanner()}>
          {DISMISS}
        </button>
      </div>
    </div>
  );
}

interface BannerActionButtonProps {
  readonly action: BannerAction;
  readonly isCreate: boolean;
  readonly editor: RecordEditor;
}

function BannerActionButton({ action, isCreate, editor }: BannerActionButtonProps) {
  switch (action) {
    case 'retry':
      return (
        <button
          type="button"
          onClick={() => {
            void editor.retry();
          }}
        >
          {RETRY}
        </button>
      );
    case 'saveAgain':
      return (
        <button
          type="button"
          aria-label={isCreate ? SAVE_AGAIN_CREATE_LABEL : SAVE_AGAIN_UPDATE_LABEL}
          onClick={() => {
            void editor.saveAgain();
          }}
        >
          {SAVE_AGAIN}
        </button>
      );
    case 'discardEdits':
      return (
        <button type="button" onClick={() => editor.discardEdits()}>
          {DISCARD_EDITS}
        </button>
      );
  }
}

