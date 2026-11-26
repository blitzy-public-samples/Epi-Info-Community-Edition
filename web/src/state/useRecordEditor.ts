// The record editor of one form: the draft of the open record, its dirty set, the save
// gate, delete, and the banners of failed writes. It replaces editing and saving in the
// legacy GuiMediator: a dirty record is saved before it is left and the move stops when
// the save fails (GoToRecordHandler), required fields are checked first
// (CheckViewRequiredFields), and a new record whose only entries are unchecked
// checkboxes is empty and is never saved (View.IsViewRecordEmpty, RunTimeView.SaveRecord).
//
// - The server validates every write; validateValues and checkRequired only give
//   feedback before the call.
// - Bodies carry writable data fields only. A POST sends every entered value and `false`
//   for each unchecked writable checkbox, so stored rows hold 0 as legacy writes; a PUT
//   sends the dirty fields only. Read-only fields are never sent, so a value check code
//   assigns to one is shown and never saved (C-3 in docs/parity-notes.md).
// - Delete asks first, then discards the draft and sends exactly one DELETE; unlike
//   legacy, no save precedes it.
// - A failed POST or PUT is resent automatically only when the server states that nothing
//   was written. Otherwise the save may have been applied, and only saveAgain, an explicit
//   action whose label names the risk, sends the draft again; save() and leave() refuse.
// - Check code runs in the API. Field events reach it only through the three onField*
//   handlers and the field-event queue; loading or replacing a record raises none.
import { useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';

import type { ApiClient, ApiResult } from '../api/client';
import {
  describeError,
  errorMessages,
  isSafeToResend,
  networkError,
  type ApiError,
  type ApiMethod,
  type ErrorAction,
} from '../api/errors';
import type {
  DialogMessage,
  FieldDefinition,
  FieldEventKind,
  FieldValue,
  FormDefinition,
  RecordDto,
  Values,
} from '../api/types';
import type { EditorSnapshot } from '../checkcode/applyFieldEvent';
import {
  checkRequired,
  hasFieldErrors,
  isDataField,
  isWritableDataField,
  mergeFieldErrors,
  normalizeValue,
  validateValues,
  type FieldErrors,
} from '../validation/clientValidation';
import { createFieldEventQueue, type FieldEventQueue, type FieldEventQueueHost } from './fieldEventQueue';

/** What a banner offers: Retry (a safe resend), Save again (an explicit resend that may duplicate) or Discard edits. */
export type BannerAction = 'retry' | 'saveAgain' | 'discardEdits';

export type EditorBannerKind =
  | 'badRequest'
  | 'forbidden'
  | 'tooLarge'
  | 'notFound'
  | 'deleted'
  | 'server'
  | 'unavailable'
  | 'uncertainWrite';

/**
 * A failed write as the form shows it. The text is fixed, or the server's own fixed
 * request-level `detail`; it never holds a field value.
 *
 * `error`, `method` and `isCreate` go beyond the banner text so that `unavailable` and
 * `uncertainWrite` can be rendered by ConnectionError, which takes the failed request.
 */
export interface EditorBanner {
  readonly kind: EditorBannerKind;
  readonly message: string;
  /** The server's trace id of the failed request; `null` when there was none or no response. */
  readonly traceId: string | null;
  readonly actions: readonly BannerAction[];
  readonly error: ApiError;
  readonly method: ApiMethod;
  /** The failed request was the first POST of a new record, so Save again may create a duplicate. */
  readonly isCreate: boolean;
}

/**
 * Callbacks are function-typed properties, as in useRecordNavigation, so navigation's
 * actions are passed unbound (`onSaved: navigation.afterSave`).
 */
export interface UseRecordEditorOptions {
  readonly client: ApiClient;
  readonly viewId: number;
  /** Keep its identity while the form is open: a new object starts a new field-event queue. */
  readonly definition: FormDefinition;
  /** The open record; `null` for New and while nothing is open. */
  readonly record: RecordDto | null;
  readonly isNew: boolean;
  /** Navigation's record generation: a change replaces the draft. */
  readonly recordGeneration: number;
  /** A save succeeded; the record is the server's DTO, which navigation installs. */
  readonly onSaved: (record: RecordDto) => void;
  /** The DELETE returned 204. */
  readonly onDeleted: () => void;
  /** The record is gone (404) or its edits were discarded: open the nearest record. */
  readonly onDiscardToNearest: (notice?: string) => void;
}

/**
 * Editor state and actions. Actions are function-typed properties, never methods, and
 * keep their identity for the life of the hook, so `leave` can be handed to
 * useRecordNavigation as `beforeLeave` and every action passed around unbound.
 */
export interface RecordEditor {
  readonly values: Values;
  /** Fields the next PUT sends. */
  readonly dirty: ReadonlySet<string>;
  /** Unsaved edits that leaving would save: a dirty record, or a new record that is not empty. */
  readonly isDirty: boolean;
  readonly fieldErrors: FieldErrors;
  readonly banner: EditorBanner | null;
  /** A save or delete is in flight. */
  readonly saving: boolean;
  /** The record was found deleted (409) or was just deleted; every field is protected. */
  readonly deletedReadOnly: boolean;
  /** Fields check code disabled; protection is kept apart, so ENABLE never unlocks a protected field. */
  readonly checkCodeDisabled: ReadonlySet<string>;
  /** Read-only metadata, an unsupported field, a deleted record, or no record open. */
  readonly isProtected: (name: string) => boolean;
  /** Protected, or disabled by check code. */
  readonly isDisabled: (name: string) => boolean;
  readonly setValue: (name: string, value: FieldValue) => void;
  /** Resolves `true` when nothing needed saving or the save succeeded. */
  readonly save: () => Promise<boolean>;
  /** Navigation's `beforeLeave`: resolves `true` only when the record may be left. */
  readonly leave: () => Promise<boolean>;
  readonly deleteDialogOpen: boolean;
  readonly requestDelete: () => void;
  readonly confirmDelete: () => Promise<void>;
  readonly cancelDelete: () => void;
  /** Resends the failed write when that is safe: a DELETE, or a POST/PUT that wrote nothing. */
  readonly retry: () => Promise<void>;
  /** Sends the current draft again at the user's explicit request, although the earlier save may have been applied. */
  readonly saveAgain: () => Promise<boolean>;
  readonly discardEdits: () => void;
  readonly dismissBanner: () => void;
  readonly onFieldFocus: (name: string) => void;
  readonly onFieldBlur: (name: string) => void;
  readonly onFieldClick: (name: string) => void;
  /** Check-code DIALOGs in execution order; the first is shown. */
  readonly dialogs: readonly DialogMessage[];
  readonly dismissDialog: () => void;
  /** A non-blocking notice that a field event could not be evaluated. */
  readonly eventNotice: string | null;
}

type EditorActions = Pick<
  RecordEditor,
  | 'setValue'
  | 'save'
  | 'leave'
  | 'requestDelete'
  | 'confirmDelete'
  | 'cancelDelete'
  | 'retry'
  | 'saveAgain'
  | 'discardEdits'
  | 'dismissBanner'
  | 'onFieldFocus'
  | 'onFieldBlur'
  | 'onFieldClick'
  | 'dismissDialog'
>;

interface EditorState {
  /** The record generation the draft belongs to. */
  readonly generation: number;
  /**
   * The record the draft edits; `null` for New and while nothing is open. A save installs
   * the server's DTO here at once, before navigation re-renders with it, so a second save
   * in between is a PUT and never a second POST.
   */
  readonly record: RecordDto | null;
  readonly isNew: boolean;
  readonly values: Values;
  readonly dirty: ReadonlySet<string>;
  readonly checkCodeDisabled: ReadonlySet<string>;
  readonly fieldErrors: FieldErrors;
  readonly banner: EditorBanner | null;
  readonly saving: boolean;
  readonly deletedReadOnly: boolean;
  readonly deleteDialogOpen: boolean;
  readonly dialogs: readonly DialogMessage[];
  readonly eventNotice: string | null;
}

interface FieldIndex {
  readonly byName: ReadonlyMap<string, FieldDefinition>;
  /** Data fields once each, in page then tab order. */
  readonly dataFields: readonly FieldDefinition[];
}

type RecordWrite =
  | { readonly method: 'POST'; readonly generation: number; readonly values: Values }
  | { readonly method: 'PUT'; readonly generation: number; readonly globalRecordId: string; readonly values: Values };

interface DeleteWrite {
  readonly method: 'DELETE';
  readonly generation: number;
  readonly globalRecordId: string;
}

/** A write that failed once it may have reached the server, kept for retry() and the save gate. */
interface FailedWrite {
  readonly write: RecordWrite | DeleteWrite;
  readonly error: ApiError;
}

const UNCERTAIN_WRITE_MESSAGE =
  'The save may already have been applied, and the record list cannot prove whether it was. ' +
  'Your changes are still on screen.';
const DUPLICATE_RISK_MESSAGE = ' Saving again may create a duplicate record.';
const DELETE_UNAVAILABLE_MESSAGE = 'The delete could not be confirmed: the database cannot be reached.';
const DELETE_NETWORK_MESSAGE = 'The delete could not be confirmed: the server could not be reached.';

function indexFields(definition: FormDefinition): FieldIndex {
  const byName = new Map<string, FieldDefinition>();
  const dataFields: FieldDefinition[] = [];
  for (const page of definition.pages) {
    for (const field of page.fields) {
      if (byName.has(field.name)) {
        continue;
      }
      byName.set(field.name, field);
      if (isDataField(field)) {
        dataFields.push(field);
      }
    }
  }
  return { byName, dataFields };
}

// Field names are metadata, so a name such as `constructor` must read only the record's
// own entry, never an inherited Object member.
function ownValue(values: Values, name: string): FieldValue {
  return Object.hasOwn(values, name) ? (values[name] ?? null) : null;
}

// A computed key defines an own property, so even a field named `__proto__` is a value.
function withValue(values: Values, name: string, value: FieldValue): Values {
  return { ...values, [name]: value };
}

function withoutErrorsFor(errors: FieldErrors, name: string): FieldErrors {
  if (!Object.hasOwn(errors, name)) {
    return errors;
  }
  return Object.fromEntries(Object.entries(errors).filter(([key]) => key !== name));
}

// The server's map has no prototype; the copy is a plain object of fresh arrays.
function copyFieldErrors(errors: Readonly<Record<string, string[]>>): FieldErrors {
  return Object.fromEntries(
    Object.entries(errors).map(([name, messages]): [string, string[]] => [name, [...messages]]),
  );
}

/** `null` for every data field, laid under the record's own values; New is all `null`. */
function baselineValues(index: FieldIndex, record: RecordDto | null): Values {
  const entries: [string, FieldValue][] = index.dataFields.map((field): [string, FieldValue] => [field.name, null]);
  if (record !== null) {
    for (const [name, value] of Object.entries(record.values)) {
      entries.push([name, value ?? null]);
    }
  }
  return Object.fromEntries(entries);
}

/**
 * Legacy emptiness: no writable data field holds a value, where an unchecked checkbox
 * (`false` or `null`) is no value and a checked one is. Read-only fields, which the user
 * cannot fill, never make a draft non-empty.
 */
function isEmptyNewDraft(index: FieldIndex, values: Values): boolean {
  return index.dataFields.every((field) => {
    if (!isWritableDataField(field)) {
      return true;
    }
    const value = normalizeValue(field, ownValue(values, field.name));
    return value === null || (field.fieldType === 'checkbox' && value !== true);
  });
}

/** The POST body: every entered writable value, and `false` for each unchecked writable checkbox. */
function createBody(index: FieldIndex, values: Values): Values {
  const entries: [string, FieldValue][] = [];
  for (const field of index.dataFields) {
    if (!isWritableDataField(field)) {
      continue;
    }
    const value = normalizeValue(field, ownValue(values, field.name));
    if (field.fieldType === 'checkbox') {
      entries.push([field.name, value === true]);
    } else if (value !== null) {
      entries.push([field.name, value]);
    }
  }
  return Object.fromEntries(entries);
}

/** The PUT body: the dirty writable fields, a cleared one as an explicit `null`. */
function updateBody(index: FieldIndex, values: Values, dirty: ReadonlySet<string>): Values {
  const entries: [string, FieldValue][] = [];
  for (const field of index.dataFields) {
    if (isWritableDataField(field) && dirty.has(field.name)) {
      entries.push([field.name, normalizeValue(field, ownValue(values, field.name))]);
    }
  }
  return Object.fromEntries(entries);
}

function hasWritableDirty(state: EditorState, index: FieldIndex): boolean {
  for (const name of state.dirty) {
    const field = index.byName.get(name);
    if (field !== undefined && isWritableDataField(field)) {
      return true;
    }
  }
  return false;
}

// An unchecked checkbox is the same whether it reads `null` or `false`.
function sameValue(field: FieldDefinition, a: FieldValue, b: FieldValue): boolean {
  return a === b || (field.fieldType === 'checkbox' && (a ?? false) === (b ?? false));
}

/** No field of the open record can be edited: it is deleted, or nothing is open. */
function recordProtected(state: EditorState): boolean {
  return state.deletedReadOnly || state.record?.recStatus === 0 || (state.record === null && !state.isNew);
}

function isProtectedIn(state: EditorState, index: FieldIndex, name: string): boolean {
  const field = index.byName.get(name);
  if (field === undefined) {
    return true;
  }
  return field.validation.readOnly || field.unsupported || field.fieldType === 'unsupported' || recordProtected(state);
}

function isDraftDirty(state: EditorState, index: FieldIndex): boolean {
  if (state.isNew) {
    return !isEmptyNewDraft(index, state.values);
  }
  return !recordProtected(state) && hasWritableDirty(state, index);
}

function freshDraft(index: FieldIndex, record: RecordDto | null, isNew: boolean, generation: number): EditorState {
  return {
    generation,
    record,
    isNew,
    values: baselineValues(index, record),
    dirty: new Set(),
    checkCodeDisabled: new Set(),
    fieldErrors: {},
    banner: null,
    saving: false,
    deletedReadOnly: false,
    deleteDialogOpen: false,
    dialogs: [],
    eventNotice: null,
  };
}

function makeBanner(
  kind: EditorBannerKind,
  message: string,
  error: ApiError,
  method: ApiMethod,
  actions: readonly BannerAction[],
): EditorBanner {
  return { kind, message, traceId: error.traceId, actions, error, method, isCreate: method === 'POST' };
}

/**
 * The banner of a failed POST or PUT (a field-level 400 shows its messages instead). A 500,
 * 503 or lost response offers Retry only when the server reported `notWritten`; every
 * other outcome may mean the save was applied, so it offers Save again only.
 */
function saveFailureBanner(error: ApiError, method: 'POST' | 'PUT'): EditorBanner {
  const action: ErrorAction = method === 'POST' ? 'create' : 'update';
  switch (error.kind) {
    case 'validation':
    case 'badRequest':
    case 'unsupportedMediaType':
      return makeBanner('badRequest', describeError(error), error, method, []);
    case 'forbidden':
      return makeBanner('forbidden', describeError(error, action), error, method, []);
    case 'notFound':
      return makeBanner('notFound', errorMessages.notFound, error, method, ['discardEdits']);
    case 'conflict':
      return makeBanner('deleted', errorMessages.conflict, error, method, []);
    case 'tooLarge':
      return makeBanner('tooLarge', errorMessages.tooLarge, error, method, []);
    case 'server':
    case 'unavailable':
    case 'network':
      if (isSafeToResend(error, method)) {
        return makeBanner(error.kind === 'server' ? 'server' : 'unavailable', describeError(error), error, method, [
          'retry',
        ]);
      }
      return makeBanner(
        'uncertainWrite',
        method === 'POST' ? UNCERTAIN_WRITE_MESSAGE + DUPLICATE_RISK_MESSAGE : UNCERTAIN_WRITE_MESSAGE,
        error,
        method,
        ['saveAgain'],
      );
  }
}

/** The banner of a failed DELETE; repeating a DELETE is always safe, so a 500, 503 or lost response offers Retry. */
function deleteFailureBanner(error: ApiError): EditorBanner {
  const retry: readonly BannerAction[] = isSafeToResend(error, 'DELETE') ? ['retry'] : [];
  switch (error.kind) {
    case 'forbidden':
      return makeBanner('forbidden', describeError(error, 'delete'), error, 'DELETE', []);
    case 'notFound':
      return makeBanner('notFound', errorMessages.notFound, error, 'DELETE', []);
    case 'conflict':
      return makeBanner('deleted', errorMessages.conflict, error, 'DELETE', []);
    case 'tooLarge':
      return makeBanner('tooLarge', errorMessages.tooLarge, error, 'DELETE', []);
    case 'server':
      return makeBanner('server', describeError(error), error, 'DELETE', retry);
    case 'unavailable':
      return makeBanner('unavailable', DELETE_UNAVAILABLE_MESSAGE, error, 'DELETE', retry);
    case 'network':
      return makeBanner('unavailable', DELETE_NETWORK_MESSAGE, error, 'DELETE', retry);
    case 'validation':
    case 'badRequest':
    case 'unsupportedMediaType':
      return makeBanner('badRequest', describeError(error), error, 'DELETE', []);
  }
}

// No call passes an AbortSignal, so a rejection can only mean that no response arrived.
async function settle<T>(call: () => Promise<ApiResult<T>>): Promise<ApiResult<T>> {
  try {
    return await call();
  } catch {
    return { ok: false, error: networkError() };
  }
}

interface ControllerHost {
  readonly options: () => UseRecordEditorOptions;
  readonly state: () => EditorState;
  readonly update: (patch: Partial<EditorState>) => void;
}

interface Controller {
  readonly actions: EditorActions;
  /** The field index of `definition`, cached for the latest definition. */
  readonly indexOf: (definition: FormDefinition) => FieldIndex;
  /** Called while rendering: a new record generation suppresses focus and blur until it is committed. */
  readonly noteRender: (generation: number) => void;
  /** Called a tick after `generation` was committed. */
  readonly endReplacing: (generation: number) => void;
  /** Called after every commit: installs a replaced record, or a same-generation record update. */
  readonly sync: () => void;
  readonly activate: () => void;
  readonly deactivate: () => void;
}

function createController(host: ControllerHost): Controller {
  let active = true;
  // The record installed at mount raises no event either.
  let replacing = true;
  const initial = host.options();
  let renderedGeneration = initial.recordGeneration;
  /** The record props the last sync saw: only a change of the props themselves acts. */
  let seenGeneration = initial.recordGeneration;
  let seenRecord = initial.record;
  let seenIsNew = initial.isNew;

  let indexCache: { readonly definition: FormDefinition; readonly index: FieldIndex } | null = null;
  let queueCache: {
    readonly client: ApiClient;
    readonly viewId: number;
    readonly definition: FormDefinition;
    readonly queue: FieldEventQueue;
  } | null = null;

  let failedWrite: FailedWrite | null = null;
  /** The save in flight; a second save() returns it. */
  let activeSave: Promise<boolean> | null = null;
  /** Writes run one after another; the tail never rejects. */
  let writeTail: Promise<unknown> = Promise.resolve();
  let deleting = false;
  /** Read-only fields whose shown value came from check code; kept on screen across saves, never sent. */
  const readOnlyAssigned = new Set<string>();
  /** The generation each field's FieldBefore was raised in. */
  const focusGenerations = new Map<string, number>();
  /**
   * The field a check-code dialog took focus from. Opening the dialog blurs the field and
   * closing it focuses the field again; neither is the user leaving or entering the field,
   * and a FieldBefore block holding a DIALOG would otherwise raise itself again.
   */
  let blurredForDialog: string | null = null;
  /**
   * Counts the field-event failure notices shown. A successful write clears the notice
   * only when none arrived while it was in flight, so a newer event's failure stays.
   */
  let noticeSerial = 0;

  const indexOf = (definition: FormDefinition): FieldIndex => {
    if (indexCache === null || indexCache.definition !== definition) {
      indexCache = { definition, index: indexFields(definition) };
    }
    return indexCache.index;
  };

  const index = (): FieldIndex => indexOf(host.options().definition);

  const queueHost: FieldEventQueueHost = {
    getSnapshot: (): EditorSnapshot => {
      const { values, checkCodeDisabled, dirty } = host.state();
      return { values, checkCodeDisabled, dirty };
    },
    setSnapshot: (next: EditorSnapshot) => {
      if (!active) {
        return;
      }
      const current = host.state();
      // A later event succeeded, so an earlier event's failure notice no longer holds.
      if (recordProtected(current)) {
        // A deleted record keeps its values: only the check-code flags change.
        host.update({ checkCodeDisabled: next.checkCodeDisabled, eventNotice: null });
        return;
      }
      for (const field of index().dataFields) {
        if (
          !isWritableDataField(field) &&
          !Object.is(ownValue(next.values, field.name), ownValue(current.values, field.name))
        ) {
          readOnlyAssigned.add(field.name);
        }
      }
      host.update({
        values: next.values,
        dirty: next.dirty,
        checkCodeDisabled: next.checkCodeDisabled,
        eventNotice: null,
      });
    },
    onDialogs: (dialogs) => {
      if (active) {
        host.update({ dialogs: [...host.state().dialogs, ...dialogs] });
      }
    },
    onNotice: (message) => {
      if (active) {
        noticeSerial += 1;
        host.update({ eventNotice: message });
      }
    },
  };

  /** The queue of the current client, form and definition; a change starts a new one. */
  const queue = (): FieldEventQueue => {
    const { client, viewId, definition } = host.options();
    if (
      queueCache === null ||
      queueCache.client !== client ||
      queueCache.viewId !== viewId ||
      queueCache.definition !== definition
    ) {
      queueCache?.queue.reset();
      queueCache = {
        client,
        viewId,
        definition,
        queue: createFieldEventQueue({ client, viewId, definition, host: queueHost }),
      };
    }
    return queueCache.queue;
  };

  /** Replaces the draft with `record` (or an empty New) and forgets everything about the previous one. */
  function install(record: RecordDto | null, isNew: boolean, generation: number): void {
    failedWrite = null;
    readOnlyAssigned.clear();
    focusGenerations.clear();
    blurredForDialog = null;
    queue().reset();
    host.update({ ...freshDraft(index(), record, isNew, generation), saving: host.state().saving });
  }

  /** Drops the draft's edits, banner and check-code state; the record stays open. */
  function discardDraft(): void {
    const { record, isNew, generation } = host.state();
    install(record, isNew, generation);
  }

  /**
   * Installs a record the server returned for the open draft: its values, except a value
   * that changed after `write` was sent, which stays on screen and dirty, and a read-only
   * value check code assigned, which stays on screen. Without `write` every edit is taken
   * as saved.
   */
  function adoptRecord(record: RecordDto, write: RecordWrite | null): void {
    const s = host.state();
    const idx = index();
    const entries: [string, FieldValue][] = [];
    const dirty = new Set<string>();
    for (const [name, serverValue] of Object.entries(baselineValues(idx, record))) {
      const field = idx.byName.get(name);
      const current = ownValue(s.values, name);
      if (field !== undefined && readOnlyAssigned.has(name)) {
        entries.push([name, current]);
      } else if (field !== undefined && write !== null && s.dirty.has(name) && changedSince(write, field, current)) {
        entries.push([name, current]);
        dirty.add(name);
      } else {
        entries.push([name, serverValue]);
      }
    }
    host.update({
      record,
      isNew: false,
      values: Object.fromEntries(entries),
      dirty,
      fieldErrors: {},
      banner: null,
      deletedReadOnly: false,
    });
  }

  // A POST stands for every writable field, an absent one as null; a PUT only for the fields it carries.
  function changedSince(write: RecordWrite, field: FieldDefinition, current: FieldValue): boolean {
    if (Object.hasOwn(write.values, field.name)) {
      return !sameValue(field, current, ownValue(write.values, field.name));
    }
    return write.method === 'PUT' || !sameValue(field, current, null);
  }

  /** Runs writes one after another, so a delete confirmed during a save deletes the saved record. */
  function serialize<T>(op: () => Promise<T>): Promise<T> {
    const run = writeTail.then(op);
    writeTail = run.then(
      () => undefined,
      () => undefined,
    );
    return run;
  }

  function exclusiveSave(op: () => Promise<boolean>): Promise<boolean> {
    if (activeSave !== null) {
      return activeSave;
    }
    const run = serialize(op);
    activeSave = run;
    const clear = (): void => {
      if (activeSave === run) {
        activeSave = null;
      }
    };
    void run.then(clear, clear);
    return run;
  }

  /** A POST or PUT whose outcome is unknown blocks every save but saveAgain. */
  function blockedByUncertainWrite(generation: number): boolean {
    const failed = failedWrite;
    if (failed === null || failed.write.generation !== generation || failed.write.method === 'DELETE') {
      return false;
    }
    if (isSafeToResend(failed.error, failed.write.method)) {
      return false;
    }
    host.update({ banner: saveFailureBanner(failed.error, failed.write.method) });
    return true;
  }

  async function sendWrite(write: RecordWrite): Promise<boolean> {
    const { client, viewId } = host.options();
    failedWrite = null;
    const noticesBefore = noticeSerial;
    host.update({ saving: true, banner: null });
    const result = await settle(() =>
      write.method === 'POST'
        ? client.createRecord(viewId, write.values)
        : client.updateRecord(viewId, write.globalRecordId, write.values),
    );
    host.update({ saving: false });
    // The draft was replaced meanwhile, or the editor is gone: the record now shown is not touched.
    if (!active || host.state().generation !== write.generation) {
      return result.ok;
    }
    if (result.ok) {
      adoptRecord(result.data, write);
      // The record was saved, so a field-event failure shown before the write, such as an
      // unreachable database, no longer holds; one that arrived during the write stays.
      if (noticeSerial === noticesBefore && host.state().eventNotice !== null) {
        host.update({ eventNotice: null });
      }
      host.options().onSaved(result.data);
      return true;
    }
    failWrite(write, result.error);
    return false;
  }

  function failWrite(write: RecordWrite, error: ApiError): void {
    if (error.kind === 'validation') {
      host.update({ fieldErrors: copyFieldErrors(error.fieldErrors), banner: null });
      return;
    }
    const banner = saveFailureBanner(error, write.method);
    if (error.kind === 'conflict') {
      // The edits cannot be saved, so they no longer hold the user on the record.
      host.update({ deletedReadOnly: true, dirty: new Set(), banner });
      return;
    }
    if (error.kind === 'server' || error.kind === 'unavailable' || error.kind === 'network') {
      failedWrite = { write, error };
    }
    host.update({ banner });
  }

  async function runSave(): Promise<boolean> {
    await queue().drain();
    const s = host.state();
    if (s.record === null && !s.isNew) {
      return true;
    }
    if (blockedByUncertainWrite(s.generation)) {
      return false;
    }

    const idx = index();
    let write: RecordWrite;
    if (s.isNew) {
      if (isEmptyNewDraft(idx, s.values)) {
        return true;
      }
      write = { method: 'POST', generation: s.generation, values: createBody(idx, s.values) };
    } else {
      if (s.record === null || recordProtected(s)) {
        return true;
      }
      const values = updateBody(idx, s.values, s.dirty);
      if (Object.keys(values).length === 0) {
        return true;
      }
      write = { method: 'PUT', generation: s.generation, globalRecordId: s.record.globalRecordId, values };
    }

    const { definition } = host.options();
    const errors = mergeFieldErrors(validateValues(definition, write.values), checkRequired(definition, s.values));
    if (hasFieldErrors(errors)) {
      host.update({ fieldErrors: errors, banner: null });
      return false;
    }
    return sendWrite(write);
  }

  function save(): Promise<boolean> {
    return exclusiveSave(runSave);
  }

  async function leave(): Promise<boolean> {
    // Edits made while an earlier save was in flight are still to be saved.
    if (activeSave !== null && !(await activeSave)) {
      return false;
    }
    await queue().drain();
    const s = host.state();
    const idx = index();
    if (s.isNew && isEmptyNewDraft(idx, s.values)) {
      discardDraft();
      return true;
    }
    return isDraftDirty(s, idx) ? save() : true;
  }


  async function sendDelete(write: DeleteWrite): Promise<void> {
    const { client, viewId } = host.options();
    failedWrite = null;
    host.update({ saving: true, banner: null });
    const result = await settle(() => client.deleteRecord(viewId, write.globalRecordId));
    host.update({ saving: false, deleteDialogOpen: false });
    if (!active || host.state().generation !== write.generation) {
      return;
    }
    if (result.ok) {
      // Protected until navigation replaces the record, so nothing can be saved to it.
      host.update({ deletedReadOnly: true });
      host.options().onDeleted();
      return;
    }
    if (result.error.kind === 'notFound') {
      host.options().onDiscardToNearest(errorMessages.notFound);
      return;
    }
    const banner = deleteFailureBanner(result.error);
    if (banner.actions.includes('retry')) {
      failedWrite = { write, error: result.error };
    }
    host.update({ banner });
  }

  function requestDelete(): void {
    const s = host.state();
    if (recordProtected(s) || s.deleteDialogOpen) {
      return;
    }
    host.update({ deleteDialogOpen: true });
  }

  function cancelDelete(): void {
    if (!deleting && host.state().deleteDialogOpen) {
      host.update({ deleteDialogOpen: false });
    }
  }

  async function confirmDelete(): Promise<void> {
    if (deleting || !host.state().deleteDialogOpen) {
      return;
    }
    deleting = true;
    try {
      // A save in flight finishes first, so a create it completes is the record deleted.
      await serialize(async () => {
        const s = host.state();
        if (!s.deleteDialogOpen) {
          return;
        }
        // The confirmation discards unsaved edits; no PUT precedes the DELETE.
        discardDraft();
        if (s.isNew || s.record === null) {
          return;
        }
        // The dialog stays open, pending, until the DELETE settles.
        host.update({ deleteDialogOpen: true });
        await sendDelete({ method: 'DELETE', generation: s.generation, globalRecordId: s.record.globalRecordId });
      });
    } finally {
      deleting = false;
    }
  }

  async function retry(): Promise<void> {
    const failed = failedWrite;
    if (failed === null || failed.write.generation !== host.state().generation) {
      return;
    }
    const { write, error } = failed;
    if (!isSafeToResend(error, write.method)) {
      return;
    }
    if (write.method === 'DELETE') {
      await serialize(() => sendDelete(write));
      return;
    }
    await exclusiveSave(() => sendWrite(write));
  }

  function saveAgain(): Promise<boolean> {
    if (activeSave !== null) {
      return activeSave;
    }
    // The user accepted that the earlier POST or PUT may have been applied.
    if (failedWrite !== null && failedWrite.write.method !== 'DELETE') {
      failedWrite = null;
    }
    host.update({ banner: null });
    return save();
  }

  function discardEdits(): void {
    discardDraft();
    host.options().onDiscardToNearest();
  }

  function setValue(name: string, raw: FieldValue): void {
    const s = host.state();
    const idx = index();
    const field = idx.byName.get(name);
    if (field === undefined || !isDataField(field) || isProtectedIn(s, idx, name)) {
      return;
    }
    const dirty = new Set(s.dirty);
    dirty.add(name);
    host.update({
      values: withValue(s.values, name, normalizeValue(field, raw)),
      dirty,
      fieldErrors: withoutErrorsFor(s.fieldErrors, name),
    });
    queue().noteUserEdit(name);
  }

  function canRaise(name: string): boolean {
    return active && !isProtectedIn(host.state(), index(), name);
  }

  function raise(kind: FieldEventKind, name: string): void {
    if (host.state().eventNotice !== null) {
      host.update({ eventNotice: null });
    }
    queue().raise(kind, name);
  }

  function onFieldFocus(name: string): void {
    if (replacing) {
      return;
    }
    if (blurredForDialog === name) {
      blurredForDialog = null;
      return;
    }
    blurredForDialog = null;
    if (!canRaise(name)) {
      return;
    }
    focusGenerations.set(name, host.state().generation);
    raise('FieldBefore', name);
  }

  function onFieldBlur(name: string): void {
    if (replacing) {
      return;
    }
    const focusedIn = focusGenerations.get(name);
    focusGenerations.delete(name);
    // The focus belonged to a record no longer on screen.
    if (focusedIn !== undefined && focusedIn !== host.state().generation) {
      return;
    }
    if (host.state().dialogs.length > 0) {
      blurredForDialog = name;
      return;
    }
    if (canRaise(name)) {
      raise('FieldAfter', name);
    }
  }

  // Raised by the control after its toggled value went through setValue, so the event
  // carries the post-click value.
  function onFieldClick(name: string): void {
    if (canRaise(name)) {
      raise('FieldClick', name);
    }
  }

  function dismissDialog(): void {
    const { dialogs } = host.state();
    if (dialogs.length === 0) {
      return;
    }
    host.update({ dialogs: dialogs.slice(1) });
    if (dialogs.length === 1 && blurredForDialog !== null) {
      // The last dialog returns focus to the field while its close is committed; a focus
      // that arrives later is the user's own.
      setTimeout(() => {
        blurredForDialog = null;
      }, 0);
    }
  }

  function sync(): void {
    const { record, isNew, recordGeneration } = host.options();
    // Started here as well as on first use, so a changed form drops the old form's events at once.
    queue();
    if (recordGeneration !== seenGeneration) {
      seenGeneration = recordGeneration;
      seenRecord = record;
      seenIsNew = isNew;
      install(record, isNew, recordGeneration);
      return;
    }
    if (record === seenRecord && isNew === seenIsNew) {
      return;
    }
    seenRecord = record;
    seenIsNew = isNew;
    // After a save the DTO is already installed; any other record is the server's newer copy.
    if (record !== null && record !== host.state().record) {
      adoptRecord(record, null);
    }
  }

  return {
    actions: {
      setValue,
      save,
      leave,
      requestDelete,
      confirmDelete,
      cancelDelete,
      retry,
      saveAgain,
      discardEdits,
      dismissBanner: () => {
        // An unknown outcome still blocks saving until Save again.
        host.update({ banner: null });
      },
      onFieldFocus,
      onFieldBlur,
      onFieldClick,
      dismissDialog,
    },
    indexOf,
    noteRender: (generation) => {
      if (generation !== renderedGeneration) {
        renderedGeneration = generation;
        replacing = true;
      }
    },
    endReplacing: (generation) => {
      if (generation === renderedGeneration) {
        replacing = false;
      }
    },
    sync,
    activate: () => {
      active = true;
    },
    deactivate: () => {
      active = false;
      queueCache?.queue.reset();
    },
  };
}

function present(state: EditorState, index: FieldIndex, actions: EditorActions): RecordEditor {
  const isProtected = (name: string): boolean => isProtectedIn(state, index, name);
  return {
    values: state.values,
    dirty: state.dirty,
    isDirty: isDraftDirty(state, index),
    fieldErrors: state.fieldErrors,
    banner: state.banner,
    saving: state.saving,
    deletedReadOnly: state.deletedReadOnly,
    checkCodeDisabled: state.checkCodeDisabled,
    isProtected,
    isDisabled: (name) => isProtected(name) || state.checkCodeDisabled.has(name),
    deleteDialogOpen: state.deleteDialogOpen,
    dialogs: state.dialogs,
    eventNotice: state.eventNotice,
    ...actions,
  };
}

/**
 * The editor of the record navigation has open. FormView passes navigation's record,
 * `isNew` and `recordGeneration` with its `afterSave`, `afterDelete` and `openNearest`
 * callbacks, and hands `leave` back to navigation as `beforeLeave` through a ref (see
 * useRecordNavigation).
 *
 * Options are read when an action runs, so new callback closures on every render never
 * change an action's identity.
 */
export function useRecordEditor(options: UseRecordEditorOptions): RecordEditor {
  const optionsRef = useRef(options);
  const [initialIndex] = useState(() => indexFields(options.definition));
  const [state, setState] = useState<EditorState>(() =>
    freshDraft(initialIndex, options.record, options.isNew, options.recordGeneration),
  );
  // Actions read the latest state between renders, and the field-event queue reads it
  // synchronously (a click is captured right after its toggle), so every update lands in
  // the ref first and is rendered from there.
  const stateRef = useRef(state);
  const [controller] = useState(() =>
    createController({
      options: () => optionsRef.current,
      state: () => stateRef.current,
      update: (patch) => {
        stateRef.current = { ...stateRef.current, ...patch };
        setState(stateRef.current);
      },
    }),
  );

  // Marked during render, not in an effect: a replaced record's commit can move focus
  // (a field becomes disabled), and those focus events arrive before any effect runs.
  // Only a flag outside React state is written, and repeating it is harmless.
  controller.noteRender(options.recordGeneration);

  useLayoutEffect(() => {
    optionsRef.current = options;
    controller.sync();
  });

  useEffect(() => {
    controller.activate();
    return () => {
      controller.deactivate();
    };
  }, [controller]);

  const { recordGeneration } = options;
  useEffect(() => {
    // Deferred a tick, past the focus and blur that replacing the rendered record causes.
    const timer = setTimeout(() => {
      controller.endReplacing(recordGeneration);
    }, 0);
    return () => {
      clearTimeout(timer);
    };
  }, [controller, recordGeneration]);

  const index = controller.indexOf(options.definition);
  const editor = useMemo(() => present(state, index, controller.actions), [state, index, controller]);

  const { isDirty } = editor;
  useEffect(() => {
    if (!isDirty) {
      return undefined;
    }
    const warn = (event: BeforeUnloadEvent): void => {
      event.preventDefault();
      // Browsers that predate preventDefault() for this event prompt only when returnValue is set.
      event.returnValue = '';
    };
    window.addEventListener('beforeunload', warn);
    return () => {
      window.removeEventListener('beforeunload', warn);
    };
  }, [isDirty]);

  return editor;
}

