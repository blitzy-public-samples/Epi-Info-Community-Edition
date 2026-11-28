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
// - Retry resends a failed POST or PUT only when the server states that nothing was
//   written and no earlier write of the draft may have been applied. Otherwise the save may
//   have been applied: save(), leave() and retry() send nothing until a write of the draft
//   succeeds or the draft is replaced, and only saveAgain, an explicit action whose label
//   names the risk, sends it again. Its consent covers the one request it sends, so a Save
//   again stopped by validation or refused by the server, even with nothing written, leaves
//   the draft as blocked as before.
// - Check code runs in the API. Field events reach it only through the three onField*
//   handlers and the field-event queue; loading or replacing a record raises none.
import { useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';

import { sameFieldValue, type ApiClient, type ApiResult } from '../api/client';
import {
  describeError,
  describeUncertainWrite,
  errorMessages,
  isSafeToResend,
  networkError,
  parseProblem,
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
 * request-level `detail`; it never holds a field value. A 413's text may name a field by
 * its prompt, which is form metadata.
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
  /**
   * A PUT returned 409: the record was deleted elsewhere. It stays open read-only, and
   * navigation learns that the selected set has changed.
   */
  readonly onConflict: () => void;
  /** The record is gone (404) or its edits were discarded: open the nearest record. */
  readonly onDiscardToNearest: (notice?: string) => void;
  /**
   * useRecordNavigation's `transitioning`: the record is being left or replaced; the draft
   * is frozen until the next record is installed.
   */
  readonly transitioning?: boolean;
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
  /**
   * A write of the draft may already have been applied, so every save but Save again is
   * refused, and so is every move that must first save the draft, until a write of it
   * succeeds. A refused save shows that write's banner again.
   */
  readonly saveBlocked: boolean;
  /**
   * The last save of the draft succeeded and left nothing unsaved: Save, a save on leave,
   * Retry or Save again. It holds until the next edit, write, delete or discard, and is kept
   * on the record that a leave's save made way for, so a move that saved first confirms it.
   * There it confirms the save of the record left, not of the record now open, unless the
   * move opened the record it saved again; `savedPrevious` tells the two apart.
   */
  readonly saved: boolean;
  /**
   * `saved` confirms the save of the record a leave's save made way for, not of the record
   * now open: the move installed New or another record. Never `true` without `saved`.
   */
  readonly savedPrevious: boolean;
  /**
   * A save is under way: one started by save(), saveAgain() or a Retry of a POST or PUT,
   * from its start, through the field-event drain and client validation, until its write
   * settles; and a leave() of a draft with unsaved edits, from the start of its drain, since
   * it saves the draft once the drain ends, unless a write of unknown outcome refuses that
   * save. A delete is not a save.
   */
  readonly savePending: boolean;
  /**
   * The record was found deleted (409), or was deleted here and is still on screen because
   * navigation did not replace it (its landing failed); every field is protected. It stays
   * `false` while navigation replaces a record just deleted: that draft is only frozen, and
   * takes no edit, save, delete or field event, so the next record shows directly.
   */
  readonly deletedReadOnly: boolean;
  /** Fields check code disabled; protection is kept apart, so ENABLE never unlocks a protected field. */
  readonly checkCodeDisabled: ReadonlySet<string>;
  /**
   * The record is being left or replaced: leave() is running, or navigation's
   * `transitioning` holds until the next record is installed. No edit or field event
   * reaches the draft meanwhile, and every field is disabled; protection is unchanged.
   */
  readonly frozen: boolean;
  /**
   * Read-only metadata, an unsupported field, a deleted record, or no record open. A
   * frozen draft is not protected.
   */
  readonly isProtected: (name: string) => boolean;
  /** Protected, disabled by check code, or frozen. */
  readonly isDisabled: (name: string) => boolean;
  readonly setValue: (name: string, value: FieldValue) => void;
  /** Resolves `true` when nothing needed saving or the save succeeded. */
  readonly save: () => Promise<boolean>;
  /** Navigation's `beforeLeave`: resolves `true` only when the record may be left. */
  readonly leave: () => Promise<boolean>;
  /**
   * Resolves once every pending field event has been applied or has failed (a failure counts
   * as drained), or once the record changes; never rejects and never saves.
   */
  readonly drainFieldEvents: () => Promise<void>;
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
  /**
   * The non-blocking notices of the fields whose field events could not be evaluated, one
   * per field in the order of its first failure; empty when there is none. A field's notice
   * lasts until each kind of event that failed on that field has since succeeded there, or
   * the record changes.
   */
  readonly eventNotices: readonly string[];
}

type EditorActions = Pick<
  RecordEditor,
  | 'setValue'
  | 'save'
  | 'leave'
  | 'drainFieldEvents'
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
  readonly savePending: boolean;
  readonly deletedReadOnly: boolean;
  /**
   * The DELETE of this draft's record returned 204. Nothing may be saved to the record,
   * deleted again or raised on it until navigation installs the next one; it shows as
   * deleted and read-only only once navigation has settled without replacing it.
   */
  readonly deleted: boolean;
  readonly deleteDialogOpen: boolean;
  readonly dialogs: readonly DialogMessage[];
  readonly eventNotices: readonly string[];
  /** leave() is running: the draft is frozen until it resolves. */
  readonly leaving: boolean;
  /**
   * A POST or PUT of the draft may already have been applied. Kept in step with the
   * controller's uncertain write, which holds the write itself, so a render can show it.
   */
  readonly uncertain: boolean;
  /**
   * The last write of the draft succeeded, and no edit, write, delete or discard has
   * followed. Kept across the record change that a leave's save made way for.
   */
  readonly saved: boolean;
  /**
   * The `saved` kept across that record change belongs to the record left: the change
   * installed New or a record other than the one the save stored. Read only with `saved`.
   */
  readonly savedPrevious: boolean;
}

interface FieldIndex {
  readonly byName: ReadonlyMap<string, FieldDefinition>;
  /** Data fields once each, in page then tab order. */
  readonly dataFields: readonly FieldDefinition[];
}

/**
 * A POST or PUT of the draft. `sentSeq` is the draft's change number when the write was
 * built and sent; a Retry resends the same write, so no later change is ever in it.
 */
type RecordWrite =
  | { readonly method: 'POST'; readonly generation: number; readonly sentSeq: number; readonly values: Values }
  | {
      readonly method: 'PUT';
      readonly generation: number;
      readonly sentSeq: number;
      readonly globalRecordId: string;
      readonly values: Values;
    };

interface DeleteWrite {
  readonly method: 'DELETE';
  readonly generation: number;
  readonly globalRecordId: string;
}

/** A write that failed once it may have reached the server, kept for retry(). */
interface FailedWrite {
  readonly write: RecordWrite | DeleteWrite;
  readonly error: ApiError;
}

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

/**
 * The client's feedback copy of the server's required gate for one write. A POST is gated on
 * its submitted values, which hold every entered writable value of the draft. A PUT is gated on
 * the locked stored row with its values laid over it, so only the fields it carries can be judged
 * here: a required field it clears stays missing whatever the row holds, while an untouched one
 * may have been filled since the draft was loaded. Own entries are kept, `__proto__` included.
 */
function requiredErrors(definition: FormDefinition, write: RecordWrite): FieldErrors {
  const errors = checkRequired(definition, write.values);
  if (write.method === 'POST') {
    return errors;
  }
  return Object.fromEntries(Object.entries(errors).filter(([name]) => Object.hasOwn(write.values, name)));
}

/**
 * Whether `value`, a normalised entry of `field`, is the value of the record the draft
 * edits as the server last returned it: the loaded record, or the DTO a save installed;
 * `null` on New. A checkbox compares its checked state only, so `null` and `false` are
 * alike, as an unchecked box is no value in isEmptyNewDraft.
 */
function isRecordValue(state: EditorState, field: FieldDefinition, value: FieldValue): boolean {
  const stored = state.record === null ? null : normalizeValue(field, ownValue(state.record.values, field.name));
  if (field.fieldType === 'checkbox') {
    return (value === true) === (stored === true);
  }
  // sameFieldValue: a bigint compares by value and an exact decimal by its literal.
  return sameFieldValue(value, stored);
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

/** No field of the open record can be edited: it is deleted, or nothing is open. */
function recordProtected(state: EditorState): boolean {
  return state.deletedReadOnly || state.record?.recStatus === 0 || (state.record === null && !state.isNew);
}

/**
 * Nothing may be written to the draft or raised on it: its record is protected, or was just
 * deleted here and awaits the record navigation installs in its place.
 */
function recordClosed(state: EditorState): boolean {
  return state.deleted || recordProtected(state);
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
  return !recordClosed(state) && hasWritableDirty(state, index);
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
    savePending: false,
    deletedReadOnly: false,
    deleted: false,
    deleteDialogOpen: false,
    dialogs: [],
    eventNotices: [],
    leaving: false,
    uncertain: false,
    saved: false,
    savedPrevious: false,
  };
}

/**
 * Whether two states of one draft hold the same dirty set and values: a save that
 * succeeded and left this unchanged made no progress, and sending it again would too.
 */
function sameDraft(a: EditorState, b: EditorState): boolean {
  if (a.generation !== b.generation || a.isNew !== b.isNew || a.dirty.size !== b.dirty.size) {
    return false;
  }
  for (const name of a.dirty) {
    if (!b.dirty.has(name)) {
      return false;
    }
  }
  const names = new Set([...Object.keys(a.values), ...Object.keys(b.values)]);
  for (const name of names) {
    if (!Object.is(ownValue(a.values, name), ownValue(b.values, name))) {
      return false;
    }
  }
  return true;
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
 * The label a 413 banner names a field by: the prompt, or the name when the prompt is blank,
 * of the field whose text takes the most bytes in `values` as the JSON body sends it; `null`
 * when `values` holds no text. Only metadata reaches the banner, never a value.
 */
function largestTextLabel(index: FieldIndex, values: Values): string | null {
  const encoder = new TextEncoder();
  let largest: { readonly name: string; readonly size: number } | null = null;
  for (const [name, value] of Object.entries(values)) {
    if (typeof value !== 'string') {
      continue;
    }
    const size = encoder.encode(JSON.stringify(value)).length;
    if (largest === null || size > largest.size) {
      largest = { name, size };
    }
  }
  if (largest === null) {
    return null;
  }
  const prompt = index.byName.get(largest.name)?.prompt?.trim() ?? '';
  return prompt === '' ? largest.name : prompt;
}

/**
 * The banner of a failed POST or PUT (a field-level 400 shows its messages instead). A 500,
 * 503 or lost response offers Retry only when the server reported `notWritten`; every
 * other outcome may mean the save was applied, so it offers Save again only.
 * @param leaving The failed save was leaving the record, which a failed save cancels, so
 *   the text says so; a Retry or Save again that then succeeds does not resume the move.
 */
function saveFailureBanner(error: ApiError, write: RecordWrite, index: FieldIndex, leaving: boolean): EditorBanner {
  const banner = failedSaveBanner(error, write, index);
  return leaving ? { ...banner, message: banner.message + leaveCancelledSuffix(banner.kind) } : banner;
}

/**
 * What a save failure's text adds when the save was leaving the record: that leaving was
 * cancelled, and that the draft is kept where the text does not already say so. A 409's
 * edits were not saved and can no longer be, so its text adds only the first.
 */
function leaveCancelledSuffix(kind: EditorBannerKind): string {
  switch (kind) {
    case 'badRequest':
    case 'forbidden':
    case 'notFound':
      return ` ${errorMessages.leaveCancelled} ${errorMessages.draftKept}`;
    case 'deleted':
    case 'tooLarge':
    case 'server':
    case 'unavailable':
    case 'uncertainWrite':
      return ` ${errorMessages.leaveCancelled}`;
  }
}

/** saveFailureBanner's banner of a save that was not leaving the record. */
function failedSaveBanner(error: ApiError, write: RecordWrite, index: FieldIndex): EditorBanner {
  const { method } = write;
  const action: ErrorAction = method === 'POST' ? 'create' : 'update';
  switch (error.kind) {
    case 'validation':
    case 'badRequest':
    case 'unsupportedMediaType':
      return makeBanner('badRequest', describeError(error), error, method, []);
    case 'forbidden':
      // The draft stays, and Discard edits is the deliberate way out of a draft that cannot be saved.
      return makeBanner('forbidden', describeError(error, action), error, method, ['discardEdits']);
    case 'notFound':
      return makeBanner('notFound', errorMessages.notFound, error, method, ['discardEdits']);
    case 'conflict':
      return makeBanner('deleted', errorMessages.saveConflict, error, method, []);
    case 'tooLarge':
      return makeBanner(
        'tooLarge',
        errorMessages.saveTooLarge(largestTextLabel(index, write.values)),
        error,
        method,
        [],
      );
    case 'server':
    case 'unavailable':
    case 'network':
      if (isSafeToResend(error, method)) {
        // A 500's text says the draft is kept, as the connection texts do; its trace id
        // shows on a line of its own.
        return error.kind === 'server'
          ? makeBanner('server', errorMessages.serverWithDraft, error, method, ['retry'])
          : makeBanner('unavailable', describeError(error), error, method, ['retry']);
      }
      return makeBanner('uncertainWrite', describeUncertainWrite(error, method === 'POST'), error, method, [
        'saveAgain',
      ]);
  }
}

/**
 * The error of a write the form definition's permission flags already deny. It is refused
 * before any request, so, like a request that got no response, it has no status, trace id
 * or write outcome.
 */
function knownDenial(): ApiError {
  return { ...networkError(), kind: 'forbidden' };
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

/**
 * Whether `record`, the success body of `write`, is the complete record the contract
 * returns for it: an own `values` entry for every data field of the form (unsupported
 * fields are absent by contract), and for a PUT the record the write was sent to. Any
 * other body acknowledges nothing, so the write may or may not have been applied.
 */
function acknowledgesWrite(index: FieldIndex, write: RecordWrite, record: RecordDto): boolean {
  if (write.method === 'PUT' && record.globalRecordId !== write.globalRecordId) {
    return false;
  }
  return index.dataFields.every((field) => Object.hasOwn(record.values, field.name));
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
  /**
   * A POST or PUT of the open draft whose outcome is unknown: it may have been applied. It
   * blocks every save and Retry but saveAgain until a write of the draft succeeds, the
   * record proves deleted (409) or the draft is replaced; a later failure, of a Save again
   * included, never lifts it, and one reported `notWritten` offers Save again, not Retry.
   * `error` is the latest failure's, so the banner shows its trace id; its `writeOutcome`
   * stays the uncertain one.
   */
  let uncertainWrite: { readonly write: RecordWrite; readonly error: ApiError } | null = null;
  /** The POST or PUT whose request is in flight, from just before it is sent until it settles. */
  let inFlightWrite: RecordWrite | null = null;
  /** The save in flight; a second save() returns it. */
  let activeSave: Promise<boolean> | null = null;
  /** Writes run one after another; the tail never rejects. */
  let writeTail: Promise<unknown> = Promise.resolve();
  let deleting = false;
  /** Read-only fields whose shown value came from check code; kept on screen across saves, never sent. */
  const readOnlyAssigned = new Set<string>();
  /**
   * The draft's change number: each edit, and each field-event effect that changes a writable
   * field's value or makes it dirty, takes the next one. It only grows, so a write built at
   * `sentSeq` carries no change numbered above it. An effect that changes neither the value
   * nor the dirtiness of a field leaves the draft as it was and takes no number.
   */
  let changeSeq = 0;
  /** The number of each writable field's latest change in the current draft. */
  const changedAt = new Map<string, number>();
  /** The generation each field's FieldBefore was raised in. */
  const focusGenerations = new Map<string, number>();
  /**
   * The field a check-code dialog took focus from. Opening the dialog blurs the field and
   * closing it focuses the field again; neither is the user leaving or entering the field,
   * and a FieldBefore block holding a DIALOG would otherwise raise itself again.
   */
  let blurredForDialog: string | null = null;
  /** leave() calls running; any one freezes the draft. */
  let leaveLocks = 0;
  /**
   * The last leave() has ended, and the freeze holds until the next commit. Navigation sets
   * `transitioning` before it calls leave(), but the editor reads the new value only once
   * that commit has run, so without this an edit made in between would reach the draft
   * that the next record is about to replace.
   */
  let awaitingCommit = false;
  /**
   * A save made by leave() succeeded, so the record change that follows keeps `saved` for
   * the record it installs. Dropped by that install, and by a commit that shows no record
   * change will follow: a failed or cancelled move never carries it to a later one.
   */
  let carrySaved = false;
  /**
   * The `globalRecordId` of the record whose save `carrySaved` keeps, as the server returned
   * it, so the install tells the record left from the same record opened again.
   */
  let carriedRecordId: string | null = null;

  /** The draft is being left or replaced: no edit or field event may change it. */
  const frozen = (): boolean => leaveLocks > 0 || awaitingCommit || host.options().transitioning === true;

  const indexOf = (definition: FormDefinition): FieldIndex => {
    if (indexCache === null || indexCache.definition !== definition) {
      indexCache = { definition, index: indexFields(definition) };
    }
    return indexCache.index;
  };

  const index = (): FieldIndex => indexOf(host.options().definition);

  const noteChange = (name: string): void => {
    changeSeq += 1;
    changedAt.set(name, changeSeq);
  };

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
      if (recordClosed(current)) {
        // A deleted record keeps its values: only the check-code flags change.
        host.update({ checkCodeDisabled: next.checkCodeDisabled });
        return;
      }
      for (const field of index().dataFields) {
        // sameFieldValue: a bigint compares by value and an exact decimal by its literal.
        const valueChanged = !sameFieldValue(ownValue(next.values, field.name), ownValue(current.values, field.name));
        if (!isWritableDataField(field)) {
          if (valueChanged) {
            readOnlyAssigned.add(field.name);
          }
        } else if (valueChanged || (next.dirty.has(field.name) && !current.dirty.has(field.name))) {
          // A CLEAR of a checkbox that is still `null` changes no value, yet makes it dirty.
          noteChange(field.name);
        }
      }
      host.update({
        values: next.values,
        dirty: next.dirty,
        checkCodeDisabled: next.checkCodeDisabled,
      });
    },
    onDialogs: (dialogs) => {
      if (active) {
        host.update({ dialogs: [...host.state().dialogs, ...dialogs] });
      }
    },
    // The queue owns which failures stand: an event of another field, a save or a raise
    // resolves none, so nothing else in the editor changes the list.
    onNotices: (messages) => {
      if (active) {
        host.update({ eventNotices: messages });
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
    uncertainWrite = null;
    readOnlyAssigned.clear();
    changedAt.clear();
    focusGenerations.clear();
    blurredForDialog = null;
    queue().reset();
    const { saving, savePending, saved } = host.state();
    const carried = carrySaved && saved;
    host.update({
      ...freshDraft(index(), record, isNew, generation),
      saving,
      savePending,
      leaving: leaveLocks > 0,
      saved: carried,
      // The same record opened again, at First or Go to its own number or from the grid,
      // is the record saved; New, or any other record, was not.
      savedPrevious: carried && (isNew || record === null || record.globalRecordId !== carriedRecordId),
    });
    carrySaved = false;
    carriedRecordId = null;
  }

  /** Drops the draft's edits, banner and check-code state; the record stays open. */
  function discardDraft(): void {
    const { record, isNew, generation } = host.state();
    // Nothing on screen is the save it reported any more.
    carrySaved = false;
    install(record, isNew, generation);
  }

  /**
   * Installs a record the server returned for the open draft: its values, except a writable
   * value edited or changed by check code after `write` was sent, which stays on screen and
   * dirty for the next save (a checkbox cleared to `null` after a POST or PUT sent `false` is
   * such a change), and a read-only value check code assigned, which stays on screen. A
   * change made before `write` was sent is in it, so the server's value is taken: a POST
   * stores an unchecked checkbox, `null` included, as `false`. Without `write` every edit is
   * taken as saved.
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
      } else if (field !== undefined && write !== null && s.dirty.has(name) && changedSince(write, name)) {
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

  // Told by change numbers, not by values: a CLEAR after a POST sent `false` leaves `null`,
  // which a POST would also send as `false`, yet the next save must send it as `null`.
  function changedSince(write: RecordWrite, name: string): boolean {
    return (changedAt.get(name) ?? 0) > write.sentSeq;
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
    host.update({ savePending: true });
    // Registered before any caller's continuation, so a caller resumes with the save ended.
    const clear = (): void => {
      if (activeSave === run) {
        activeSave = null;
        host.update({ savePending: false });
      }
    };
    void run.then(clear, clear);
    return run;
  }

  /**
   * Whether the draft of `generation` has a write whose outcome is unknown, which blocks
   * every save but saveAgain; when it does, its banner is shown again.
   */
  function blockedByUncertainWrite(generation: number): boolean {
    const uncertain = uncertainWrite;
    if (uncertain === null || uncertain.write.generation !== generation) {
      return false;
    }
    host.update({ banner: saveFailureBanner(uncertain.error, uncertain.write, index(), leaveLocks > 0) });
    return true;
  }

  async function sendWrite(write: RecordWrite): Promise<boolean> {
    const { client, viewId } = host.options();
    failedWrite = null;
    inFlightWrite = write;
    host.update({ saving: true, banner: null, saved: false });
    const result = await settle(() =>
      write.method === 'POST'
        ? client.createRecord(viewId, write.values)
        : client.updateRecord(viewId, write.globalRecordId, write.values),
    );
    inFlightWrite = null;
    host.update({ saving: false });
    // The draft was replaced meanwhile, or the editor is gone: the record now shown is not touched.
    if (!active || host.state().generation !== write.generation) {
      return result.ok;
    }
    if (result.ok) {
      if (!acknowledgesWrite(index(), write, result.data)) {
        // The server may have applied the write before its body went wrong: a failure of
        // unknown outcome, which only Save again resends.
        failWrite(write, parseProblem(result.status, undefined));
        return false;
      }
      // The draft is now stored, so no earlier write of it is left in doubt.
      uncertainWrite = null;
      // A save leave() made precedes the record change it made way for, which keeps `saved`.
      carrySaved = leaveLocks > 0;
      carriedRecordId = carrySaved ? result.data.globalRecordId : null;
      // Until a record change, the record this save stored is the one open.
      host.update({ uncertain: false, saved: true, savedPrevious: false });
      adoptRecord(result.data, write);
      // A save runs no check code, so the field-event failures it drained stay on screen:
      // their effects, such as an assigned Age, are still missing from the record.
      host.options().onSaved(result.data);
      return true;
    }
    failWrite(write, result.error);
    return false;
  }

  /**
   * Whether a write of the draft of `generation` that the server may still receive or may
   * already hold carries `name`: the write in flight, a failed POST or PUT that Retry would
   * resend as it was sent, or a write whose outcome is unknown. The server's value of the
   * field may then be that write's, not the record's, so only a save can settle it.
   */
  function pendingWriteCarries(name: string, generation: number): boolean {
    const failed = failedWrite?.write;
    const pending: readonly (RecordWrite | null | undefined)[] = [
      inFlightWrite,
      failed?.method === 'DELETE' ? null : failed,
      uncertainWrite?.write,
    ];
    return pending.some(
      (write) => write !== null && write !== undefined && write.generation === generation && Object.hasOwn(write.values, name),
    );
  }

  function failWrite(write: RecordWrite, error: ApiError): void {
    // A failed write confirms no save, whatever an earlier one reported.
    host.update({ saved: false });
    if (error.kind === 'validation') {
      host.update({ fieldErrors: copyFieldErrors(error.fieldErrors), banner: null });
      return;
    }
    const banner = saveFailureBanner(error, write, index(), leaveLocks > 0);
    if (error.kind === 'conflict') {
      // The edits cannot be saved, so they no longer hold the user on the record; nor can
      // anything be saved to a deleted record, so no write is left to block.
      uncertainWrite = null;
      host.update({ deletedReadOnly: true, dirty: new Set(), banner, uncertain: false });
      host.options().onConflict();
      return;
    }
    if (error.kind === 'server' || error.kind === 'unavailable' || error.kind === 'network') {
      const uncertain = uncertainWrite;
      if (!isSafeToResend(error, write.method)) {
        uncertainWrite = { write, error };
        host.update({ uncertain: true });
      } else if (uncertain !== null && uncertain.write.generation === write.generation) {
        // This write was not applied, but an earlier one of the draft may have been, so a
        // Retry would resend the draft without consent: the uncertain banner, which offers
        // Save again only, is shown instead and nothing is kept for retry(). It reports this
        // failure, the latest, by its status, cause and trace id, while the outcome stays the
        // earlier write's, which is never `notWritten`, so the banner can never offer Retry.
        uncertainWrite = { write: uncertain.write, error: { ...error, writeOutcome: uncertain.error.writeOutcome } };
        blockedByUncertainWrite(write.generation);
        return;
      }
      failedWrite = { write, error };
    }
    host.update({ banner });
  }

  /**
   * Drains the field events, then validates and sends the draft.
   * @param replayAuthorized `true` only for Save again, which sends the draft although an
   *   earlier write of it may have been applied; any other save of such a draft is refused.
   */
  async function runSave(replayAuthorized: boolean): Promise<boolean> {
    await queue().drain();
    const s = host.state();
    if (s.record === null && !s.isNew) {
      return true;
    }
    if (!replayAuthorized && blockedByUncertainWrite(s.generation)) {
      return false;
    }

    const idx = index();
    let write: RecordWrite;
    if (s.isNew) {
      if (isEmptyNewDraft(idx, s.values)) {
        return true;
      }
      write = { method: 'POST', generation: s.generation, sentSeq: changeSeq, values: createBody(idx, s.values) };
    } else {
      // A record just deleted takes no write: nothing is left to save.
      if (s.record === null || recordClosed(s)) {
        return true;
      }
      const values = updateBody(idx, s.values, s.dirty);
      if (Object.keys(values).length === 0) {
        return true;
      }
      write = {
        method: 'PUT',
        generation: s.generation,
        sentSeq: changeSeq,
        globalRecordId: s.record.globalRecordId,
        values,
      };
    }

    const { definition } = host.options();
    // Permission flags are null until reported; only `false` denies. The same rule disables
    // the Save and New buttons, so a save started by leaving is refused like an explicit one.
    if (write.method === 'POST' ? definition.canInsert === false : definition.canUpdate === false) {
      host.update({ banner: saveFailureBanner(knownDenial(), write, idx, leaveLocks > 0) });
      return false;
    }
    // Both checks see only what the write submits: the draft may be stale, and the server's gate
    // decides a PUT's untouched fields on the stored row, its field-level 400 shown by failWrite.
    const errors = mergeFieldErrors(validateValues(definition, write.values), requiredErrors(definition, write));
    if (hasFieldErrors(errors)) {
      host.update({ fieldErrors: errors, banner: null });
      return false;
    }
    return sendWrite(write);
  }

  function save(): Promise<boolean> {
    // Refused at once while a write of the draft may already have been applied: its banner is
    // shown again, as a new banner, and no save starts, so nothing reports Saving… or
    // disables Save meanwhile. A write queued behind a running save is judged in runSave.
    if (activeSave === null && blockedByUncertainWrite(host.state().generation)) {
      return Promise.resolve(false);
    }
    return exclusiveSave(() => runSave(false));
  }

  /**
   * Resolves `true` only once the draft is clean: every save it awaits or starts is
   * followed by a fresh drain and check. The draft is frozen from the first moment, so
   * nothing the user does meanwhile can be dropped by the transition that follows.
   */
  async function leave(): Promise<boolean> {
    leaveLocks += 1;
    if (leaveLocks === 1) {
      host.update({ leaving: true });
    }
    try {
      /** The state before the last save this leave started. */
      let beforeSave: EditorState | null = null;
      for (;;) {
        // A save in flight settles first: edits made while it ran are still to be saved.
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
        if (!isDraftDirty(s, idx)) {
          return true;
        }
        // A save that reported success yet left the draft as it was would only be sent
        // again: the draft stays, and the record is not left.
        if (beforeSave !== null && sameDraft(beforeSave, s)) {
          return false;
        }
        beforeSave = s;
        if (!(await save())) {
          return false;
        }
      }
    } finally {
      leaveLocks -= 1;
      if (leaveLocks === 0) {
        awaitingCommit = true;
        host.update({ leaving: false });
      }
    }
  }

  /** The drain leave() and save() wait on, for a caller that must judge the draft without saving it. */
  function drainFieldEvents(): Promise<void> {
    return queue().drain();
  }

  async function sendDelete(write: DeleteWrite): Promise<void> {
    const { client, viewId } = host.options();
    failedWrite = null;
    host.update({ saving: true, banner: null, saved: false });
    const result = await settle(() => client.deleteRecord(viewId, write.globalRecordId));
    host.update({ saving: false, deleteDialogOpen: false });
    if (!active || host.state().generation !== write.generation) {
      return;
    }
    if (result.ok) {
      // Closed until navigation replaces the record, so nothing can be saved to it. It is not
      // marked deleted-read-only here: navigation's landing on the next record starts in this
      // same update, and the record shows deleted only if that landing leaves it on screen.
      host.update({ deleted: true });
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
    // An empty new draft holds nothing to discard, so it asks nothing; one with entries is
    // still asked about, and its confirmation discards it without a request.
    if (recordClosed(s) || s.deleteDialogOpen || (s.isNew && isEmptyNewDraft(index(), s.values))) {
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

  /**
   * Resends the last failed write when that is safe: a DELETE, or a POST or PUT the server
   * reported `notWritten`. A Retry carries no consent to replay, so a POST or PUT is
   * refused while an earlier write of the draft may have been applied, whose banner is
   * shown again.
   */
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
    // Decided when the write would be sent, after any write queued ahead of it.
    await exclusiveSave(async () => {
      if (blockedByUncertainWrite(write.generation)) {
        return false;
      }
      return sendWrite(write);
    });
  }

  function saveAgain(): Promise<boolean> {
    if (activeSave !== null) {
      return activeSave;
    }
    host.update({ banner: null });
    // The user accepted that the earlier POST or PUT may have been applied, for the one
    // request this save sends; the write stays uncertain until a write of the draft succeeds.
    return exclusiveSave(async () => {
      const saved = await runSave(true);
      if (!saved && host.state().banner === null) {
        // Stopped by validation, or refused with field messages: the statement that the
        // earlier save may have been applied stays on screen.
        blockedByUncertainWrite(host.state().generation);
      }
      return saved;
    });
  }

  function discardEdits(): void {
    // Read before discardDraft clears the banner.
    const deniedInPlace = host.state().banner?.kind === 'forbidden';
    discardDraft();
    // A record the user may not save still exists, so it stays open with its stored values;
    // a record that no longer exists gives way to the nearest one.
    if (!deniedInPlace) {
      host.options().onDiscardToNearest();
    }
  }

  function setValue(name: string, raw: FieldValue): void {
    const s = host.state();
    const idx = index();
    const field = idx.byName.get(name);
    if (field === undefined || !isDataField(field) || isProtectedIn(s, idx, name) || s.deleted || frozen()) {
      return;
    }
    const value = normalizeValue(field, raw);
    const dirty = new Set(s.dirty);
    // An edit back to the record's value leaves nothing to save, so the field leaves the
    // dirty set: no PUT rewrites the stored row, and no leave prompt holds the user. While a
    // pending write carries the field it stays, so the next save sends the value on screen.
    // Only the user's own edits come here; a check-code effect marks a field dirty as before.
    if (isRecordValue(s, field, value) && !pendingWriteCarries(name, s.generation)) {
      dirty.delete(name);
    } else {
      dirty.add(name);
    }
    host.update({
      values: withValue(s.values, name, value),
      dirty,
      fieldErrors: withoutErrorsFor(s.fieldErrors, name),
      saved: false,
    });
    noteChange(name);
    queue().noteUserEdit(name);
  }

  function canRaise(name: string): boolean {
    const s = host.state();
    return active && !s.deleted && !isProtectedIn(s, index(), name);
  }

  // Raising changes no notice: the layout stays as it is while focus moves, and a failure
  // stands until an event of its own kind on its own field succeeds.
  function raise(kind: FieldEventKind, name: string): void {
    queue().raise(kind, name);
  }

  // A frozen draft raises no event, and its focus and blur leave no trace for later ones.
  function onFieldFocus(name: string): void {
    if (replacing || frozen()) {
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
    if (replacing || frozen()) {
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
    if (!frozen() && canRaise(name)) {
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
    // The options now hold the committed `transitioning`, which takes over from leave().
    awaitingCommit = false;
    const { record, isNew, recordGeneration, transitioning } = host.options();
    // Started here as well as on first use, so a changed form drops the old form's events at once.
    queue();
    if (recordGeneration !== seenGeneration) {
      seenGeneration = recordGeneration;
      seenRecord = record;
      seenIsNew = isNew;
      install(record, isNew, recordGeneration);
      return;
    }
    if (transitioning !== true && leaveLocks === 0) {
      // No leave runs and no record change is pending: the move a leave's save made way for
      // failed or was cancelled, or none followed, so a later move must not carry `saved`.
      carrySaved = false;
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
      drainFieldEvents,
      requestDelete,
      confirmDelete,
      cancelDelete,
      retry,
      saveAgain,
      discardEdits,
      dismissBanner: () => {
        // A write whose outcome is unknown still blocks every save but Save again, and a
        // blocked save shows its banner again, until a write of the draft succeeds.
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

/**
 * @param frozen The draft is being left or replaced.
 * @param deletedShown The draft's record was deleted here and navigation has settled
 *   without replacing it, so it shows as deleted and read-only.
 */
function present(
  state: EditorState,
  index: FieldIndex,
  actions: EditorActions,
  frozen: boolean,
  deletedShown: boolean,
): RecordEditor {
  // A frozen draft disables every field, so no control accepts input meanwhile, but
  // protects none: protection stays read-only metadata, an unsupported field, a deleted
  // record or none open, and only it takes the protected styling.
  const isProtected = (name: string): boolean => deletedShown || isProtectedIn(state, index, name);
  const isDirty = isDraftDirty(state, index);
  const saved = state.saved && !isDirty;
  return {
    values: state.values,
    dirty: state.dirty,
    isDirty,
    fieldErrors: state.fieldErrors,
    banner: state.banner,
    saving: state.saving,
    saveBlocked: state.uncertain,
    saved,
    savedPrevious: saved && state.savedPrevious,
    // A leave of a dirty draft saves it once its drain ends, so the save is under way from
    // the drain's start; one a write of unknown outcome refuses never starts.
    savePending: state.savePending || (state.leaving && isDirty && !state.uncertain),
    deletedReadOnly: state.deletedReadOnly || deletedShown,
    checkCodeDisabled: state.checkCodeDisabled,
    frozen,
    isProtected,
    isDisabled: (name) => frozen || isProtected(name) || state.checkCodeDisabled.has(name),
    deleteDialogOpen: state.deleteDialogOpen,
    dialogs: state.dialogs,
    eventNotices: state.eventNotices,
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
  const frozen = state.leaving || options.transitioning === true;
  // Read from this render's options, never from the ref the layout effect updates: the
  // render that commits a delete's 204 already carries navigation's `transitioning`, and the
  // render whose generation moved on precedes the commit in which sync() installs the next
  // record. In neither does the record just deleted show as deleted.
  const deletedShown =
    state.deleted && state.generation === options.recordGeneration && options.transitioning !== true;
  const editor = useMemo(
    () => present(state, index, controller.actions, frozen, deletedShown),
    [state, index, controller, frozen, deletedShown],
  );

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
