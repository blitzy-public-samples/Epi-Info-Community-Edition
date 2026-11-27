// Find record: the web counterpart of legacy Epi.Windows.Enter/Dialogs/FindRecordDialog.
// It keeps the legacy dialog's field list (every field that holds a searchable value,
// then the GlobalRecordId and RecStatus system fields), its six-field limit and its
// free-text criteria in the legacy syntax (`a or b`, `>=5`, `Sm*`, `>`). Each value is
// sent exactly as typed: trimming, the " or " split, comparison prefixes, wildcards,
// blank handling and every type coercion belong to the server's SearchCriteriaParser,
// which also enforces the field limit, so the dialog decides nothing about a value.
import {
  useEffect,
  useId,
  useMemo,
  useRef,
  useState,
  type FormEvent,
  type KeyboardEvent as ReactKeyboardEvent,
  type MouseEvent,
  type ReactNode,
} from 'react';
import { createPortal } from 'react-dom';
import { isExactDecimal, type ApiClient } from '../api/client';
import { describeError, isAbortError, networkError, type ApiError } from '../api/errors';
import type {
  FieldDefinition,
  FieldOption,
  FieldType,
  FieldValue,
  FormDefinition,
  SearchRequest,
  SearchResponse,
} from '../api/types';
import { ConnectionError } from './ConnectionError';

/** One entry of the field picker. */
export type SearchableField = {
  /** Canonical field name, sent as the criterion's `field`. */
  name: string;
  /** The field's prompt (its name when the prompt is blank), or a system field's description. */
  label: string;
  /** How to write a value for this field in the legacy Find syntax. */
  hint: string;
  /** The field's type, or `system` for `GlobalRecordId` and `RecStatus`. */
  kind: FieldType | 'system';
};

export interface FindRecordDialogProps {
  open: boolean;
  client: ApiClient;
  viewId: number;
  definition: FormDefinition;
  /** The form view's "Show deleted" toggle; it selects the set searched and the positions shown. */
  includeDeleted: boolean;
  /** The record on screen, left out of the results as legacy Find leaves it out; `null` for a new record. */
  openRecordId: string | null;
  /** Called with the chosen result; the form view closes the dialog and opens the record. */
  onOpen: (globalRecordId: string) => void;
  onClose: () => void;
  /**
   * Called when a search answers 404, which means the form itself no longer exists; the
   * form view's form-not-found handling closes the dialog and decides what follows.
   */
  onFormNotFound: () => void;
}

/** Legacy SEARCH_LIMIT_EXCEEDED: a search names at most six fields. */
const MAX_SEARCH_FIELDS = 6;
const LIMIT_NOTE = 'At most six fields can be searched.';
const NO_FIELD_NOTE = 'Choose up to six fields, then enter a value for each.';

// Text, multiline and legal values other than a Phone Number: the server strips a leading
// >=, <=, <>, > or < and ignores it, so these hints offer no comparison. What is left
// matches the whole value, a value holding a wildcard is a pattern match, and a prefix
// with nothing after it finds the records where the field is empty.
const PATTERN_SYNTAX = 'Use a or b for alternatives and * as a wildcard (e.g. Sm*); > alone finds empty values.';
const MULTILINE_HINT = `Matches the whole text; a longer text matches on its first 255 characters. ${PATTERN_SYNTAX}`;
// A legal-values field is never a Phone Number, so its wildcard needs no exception.
const LEGAL_VALUES_HINT = `Matches the whole value. ${PATTERN_SYNTAX}`;
// A Phone Number field arrives as text, with or without a mask, and the form definition
// carries no metadata type name to tell it from other text. For a Phone Number the server
// reads the prefix as the comparison and has no wildcard path, so * is matched as typed;
// alternatives and a prefix with nothing after it work as for other text. The text hints
// therefore claim only what holds for both: a masked field, most likely a Phone Number,
// gets no wildcard claim, and an unmasked one gets the wildcard with the Phone Number
// exception.
const MASKED_TEXT_HINT = 'Matches the whole value. Use a or b for alternatives; > alone finds empty values.';
const TEXT_HINT =
  `${MASKED_TEXT_HINT} * is a wildcard (e.g. Sm*), except in a phone number, where it is matched as typed.`;
const NUMBER_HINT = 'A number, e.g. >=5 or 1 or 2. Leave empty to find missing values.';
const DATE_HINT = 'A date as YYYY-MM-DD, e.g. >=2011-01-15. Leave empty to find missing values.';
const CHECKBOX_HINT = 'Yes, No, (+) or (-).';
const OPTION_HINT = 'The option number:';
const OPTION_HINT_UNLISTED = 'The option number.';
const ALLOWED_VALUES = 'Allowed values:';
const SYSTEM_HINT = 'GlobalRecordId text, or RecStatus 1 (active) / 0 (deleted).';
/** Legal-value lists can be long; the hint names only the first few. */
const LISTED_LEGAL_VALUES = 5;

// A search 404 means the form itself is gone, which describeError's record text misstates.
// The form view's form-not-found handling takes over, so the text names no next step.
const FORM_NOT_FOUND = 'This form no longer exists.';
const SEARCHING = 'Searching…';
const SEARCHING_OVER_RESULTS = 'Searching… The results below are from the previous search.';
const STALE_RESULTS = 'These results are for the previous criteria; Search updates them.';
const NO_MATCHES = 'No records match.';
// Name of the scrolling region around the results table, whose own name is its caption.
// It holds none of the toolbar's and navigator's names, which Playwright matches by substring.
const RESULTS_REGION_LABEL = 'Search results';

// The data-field types SearchCriteriaParser accepts. Labels, groups and unsupported
// fields (Yes/No, grids, relates, command buttons, …) hold no searchable value.
const SEARCHABLE_TYPES: ReadonlySet<FieldType> = new Set<FieldType>([
  'text',
  'multiline',
  'legalValues',
  'number',
  'option',
  'date',
  'checkbox',
]);

// The base-table columns legacy Find appends after the page fields.
const SYSTEM_FIELDS: readonly SearchableField[] = [
  { name: 'GlobalRecordId', label: 'Global record ID', hint: SYSTEM_HINT, kind: 'system' },
  { name: 'RecStatus', label: 'Record status', hint: SYSTEM_HINT, kind: 'system' },
];

// Controls that can take focus inside the dialog, for the Tab cycle.
const FOCUSABLE_SELECTOR = 'button, [href], input, select, textarea, summary, [tabindex]:not([tabindex="-1"])';
const MODAL_SELECTOR = '[aria-modal="true"]';

/** The field's prompt exactly as metadata holds it, or its name when the prompt is missing or blank. */
function promptOf(field: FieldDefinition): string {
  const prompt = field.prompt;
  return typeof prompt === 'string' && prompt.trim() !== '' ? prompt : field.name;
}

/** An option's stored value is its 0-based index; the list position stands in when the value is not a number. */
function optionNumber(option: FieldOption, index: number): number {
  return typeof option.value === 'number' ? option.value : index;
}

function optionHint(options: readonly FieldOption[]): string {
  if (options.length === 0) {
    return OPTION_HINT_UNLISTED;
  }
  const listed = options.map((option, index) => `${optionNumber(option, index)} = ${option.label}`);
  return `${OPTION_HINT} ${listed.join(', ')}.`;
}

function legalValuesHint(options: readonly FieldOption[]): string {
  if (options.length === 0) {
    return LEGAL_VALUES_HINT;
  }
  const listed = options.slice(0, LISTED_LEGAL_VALUES).map((option) => option.label);
  const more = options.length > LISTED_LEGAL_VALUES ? ', …' : '';
  return `${LEGAL_VALUES_HINT} ${ALLOWED_VALUES} ${listed.join(', ')}${more}.`;
}

/** Whether the field carries an input mask, as a Phone Number field usually does. */
function isMasked(field: FieldDefinition): boolean {
  const pattern = field.validation.pattern;
  return typeof pattern === 'string' && pattern.trim() !== '';
}

function hintFor(field: FieldDefinition): string {
  switch (field.fieldType) {
    case 'number':
      return NUMBER_HINT;
    case 'date':
      return DATE_HINT;
    case 'checkbox':
      return CHECKBOX_HINT;
    case 'option':
      return optionHint(field.options ?? []);
    case 'legalValues':
      return legalValuesHint(field.options ?? []);
    case 'multiline':
      return MULTILINE_HINT;
    case 'text':
      return isMasked(field) ? MASKED_TEXT_HINT : TEXT_HINT;
    default:
      return TEXT_HINT;
  }
}

/** Every field of the definition, pages by `position` and then `tabIndex`; the input is not modified. */
function fieldsInOrder(definition: FormDefinition): FieldDefinition[] {
  return [...definition.pages]
    .sort((a, b) => a.position - b.position)
    .flatMap((page) => [...page.fields].sort((a, b) => a.tabIndex - b.tabIndex));
}

/**
 * The fields Find can search, in picker order: every data field whose type holds a
 * searchable value, pages by `position` and then `tabIndex`, followed by the system
 * fields `GlobalRecordId` and `RecStatus`. Labels, groups and unsupported fields,
 * Yes/No included, are left out. Pure: the definition is not modified.
 */
export function searchableFields(definition: FormDefinition): SearchableField[] {
  const result: SearchableField[] = fieldsInOrder(definition)
    .filter((field) => SEARCHABLE_TYPES.has(field.fieldType))
    .map((field) => ({ name: field.name, label: promptOf(field), hint: hintFor(field), kind: field.fieldType }));

  // The server resolves a data field before a system field of the same name, so a
  // system entry is added only when no data field already claims that name.
  const taken = new Set(result.map((field) => field.name.toLowerCase()));
  for (const system of SYSTEM_FIELDS) {
    if (!taken.has(system.name.toLowerCase())) {
      result.push({ ...system });
    }
  }
  return result;
}

/** Column headers of the results: each field's prompt, or its name when the prompt is blank. */
function columnLabels(definition: FormDefinition): ReadonlyMap<string, string> {
  return new Map(fieldsInOrder(definition).map((field) => [field.name, promptOf(field)]));
}

/** Text of a result cell; an empty value is blank, never "null" or "undefined". */
function cellText(value: FieldValue | undefined): string {
  if (typeof value === 'string') {
    return value;
  }
  // A bigint is an exact integer beyond the safe range, shown digit for digit.
  if (typeof value === 'number' || typeof value === 'bigint') {
    return String(value);
  }
  if (typeof value === 'boolean') {
    return value ? 'Yes' : 'No';
  }
  // An exact decimal is a typed number, shown as its JSON literal; no response carries one.
  if (isExactDecimal(value)) {
    return value.literal;
  }
  return '';
}

function resultSummary(response: SearchResponse): string {
  const shown = response.items.length;
  if (shown === 0) {
    return NO_MATCHES;
  }
  if (response.truncated) {
    return `Showing ${shown} of ${response.total} matches.`;
  }
  return response.total === 1 ? '1 record matches.' : `${response.total} records match.`;
}

/** One criterion as the dialog sends it: the picked field and its text as entered. */
interface CriterionEntry {
  readonly field: string;
  readonly value: string;
}

/** The criteria a search was sent with, kept beside what that search returned. */
interface SubmittedCriteria {
  /** The search's identity, from {@link criteriaKey}. */
  readonly key: string;
  /** Each criterion's text as sent, by field name. */
  readonly texts: ReadonlyMap<string, string>;
}

/**
 * Identity of a search: the picked fields in picker order with their texts, the set
 * searched and the excluded record. Results and errors describe the criteria on screen
 * only while this equals the key they were sent with.
 */
function criteriaKey(entries: readonly CriterionEntry[], includeDeleted: boolean, openRecordId: string | null): string {
  return JSON.stringify([entries.map((entry) => [entry.field, entry.value]), includeDeleted, openRecordId]);
}

/**
 * The searchable field an `errors` key names. The server keys each message by the
 * field's canonical name, so the field of exactly that name wins; a key that differs
 * only in letter case names a field only when exactly one field name matches it that
 * way, as the server itself resolves names. `undefined` for any other key.
 */
function fieldNamed(key: string, fields: readonly SearchableField[]): string | undefined {
  if (fields.some((field) => field.name === key)) {
    return key;
  }
  const folded = key.toLowerCase();
  const names = new Set(fields.filter((field) => field.name.toLowerCase() === folded).map((field) => field.name));
  return names.size === 1 ? [...names][0] : undefined;
}

/**
 * Splits a 400's `errors` map. A message keyed by a searchable field ({@link fieldNamed})
 * that the search was sent with stays beside that field's criterion while the field is
 * picked and its text is still the text sent; once the criterion is edited or removed
 * the message no longer describes it and is dropped. Every other key (`criteria`,
 * `excludeGlobalRecordId`, a name two fields share ignoring case, a field the search did
 * not send, …) goes to the banner.
 */
function splitFieldErrors(
  error: ApiError | null,
  fields: readonly SearchableField[],
  submitted: SubmittedCriteria | null,
  texts: ReadonlyMap<string, string>,
  picked: ReadonlySet<string>,
): { byField: ReadonlyMap<string, readonly string[]>; other: readonly string[] } {
  const byField = new Map<string, string[]>();
  const other: string[] = [];
  if (error === null) {
    return { byField, other };
  }
  for (const [key, messages] of Object.entries(error.fieldErrors)) {
    const name = fieldNamed(key, fields);
    const sentText = name === undefined ? undefined : submitted?.texts.get(name);
    if (name === undefined || sentText === undefined) {
      other.push(...messages);
    } else if (picked.has(name) && (texts.get(name) ?? '') === sentText) {
      byField.set(name, [...(byField.get(name) ?? []), ...messages]);
    }
  }
  return { byField, other };
}

/** A 503, or no response at all: the connection banner with Retry, since a search is a read. */
function isConnectionFailure(error: ApiError): boolean {
  return error.kind === 'unavailable' || error.kind === 'network';
}

/** Banner lines of an error the connection banner does not show. */
function bannerLines(error: ApiError, otherFieldErrors: readonly string[], hasCriterionErrors: boolean): string[] {
  if (error.kind === 'validation') {
    return [...(hasCriterionErrors ? [describeError(error)] : []), ...otherFieldErrors];
  }
  const text = error.kind === 'notFound' ? FORM_NOT_FOUND : describeError(error);
  return [text, ...otherFieldErrors];
}

/** Whether the element is, or contains, a modal dialog. */
function holdsModal(element: Element): boolean {
  return element.matches(MODAL_SELECTOR) || element.querySelector(MODAL_SELECTOR) !== null;
}

/** Enabled, rendered controls of the dialog in document order. */
function focusableIn(container: HTMLElement): HTMLElement[] {
  return Array.from(container.querySelectorAll(FOCUSABLE_SELECTOR)).filter(
    (element): element is HTMLElement =>
      element instanceof HTMLElement &&
      !element.matches(':disabled') &&
      element.tabIndex >= 0 &&
      element.closest('[hidden], [inert]') === null,
  );
}

/** Focus on open and after a stray focus: the first enabled field checkbox, else the dialog itself. */
function focusInitial(dialog: HTMLElement): void {
  const checkbox = dialog.querySelector('input[type="checkbox"]:not(:disabled)');
  if (checkbox instanceof HTMLElement) {
    checkbox.focus();
  } else {
    dialog.focus();
  }
}

/** Keeps Tab and Shift+Tab inside the dialog, wrapping at its first and last controls. */
function trapTab(event: KeyboardEvent, dialog: HTMLElement): void {
  const controls = focusableIn(dialog);
  if (controls.length === 0) {
    event.preventDefault();
    dialog.focus();
    return;
  }
  const first = controls[0];
  const last = controls[controls.length - 1];
  const active = document.activeElement;
  const inside = active instanceof HTMLElement && active !== dialog && dialog.contains(active);
  if (event.shiftKey) {
    if (!inside || active === first) {
      event.preventDefault();
      last.focus();
    }
  } else if (!inside || active === last) {
    event.preventDefault();
    first.focus();
  }
}

/**
 * Legacy Find as a modal dialog: a field picker of at most six fields, one free-text
 * criterion per picked field, and the matches with their selected-set position. It
 * calls only `client.searchRecords`, opens nothing itself and writes nothing: choosing
 * a result reports its id through `onOpen`.
 *
 * Like ConfirmDialog it is a plain role="dialog" element (jsdom has no
 * <dialog>.showModal()) rendered into <body>, so that, while it is open, every other
 * child of <body> can be made inert. Focus moves to the first field checkbox on open
 * and returns to the element focused before it on close. Picked fields and their
 * values outlive a close; results and errors do not.
 */
export function FindRecordDialog({
  open,
  client,
  viewId,
  definition,
  includeDeleted,
  openRecordId,
  onOpen,
  onClose,
  onFormNotFound,
}: FindRecordDialogProps) {
  const baseId = useId();
  const headingId = `${baseId}-heading`;
  const formId = `${baseId}-form`;
  const limitNoteId = `${baseId}-limit`;
  const statusId = `${baseId}-status`;
  const backdropRef = useRef<HTMLDivElement>(null);
  const dialogRef = useRef<HTMLDivElement>(null);
  const checkboxRefs = useRef(new Map<string, HTMLInputElement>());
  const controllerRef = useRef<AbortController | null>(null);
  const searchButtonRef = useRef<HTMLButtonElement>(null);
  const refocusSearchRef = useRef(false);

  const fields = useMemo(() => searchableFields(definition), [definition]);
  const labels = useMemo(() => columnLabels(definition), [definition]);

  // Maps, not plain objects: a field may be named like an Object member ("constructor").
  const [selected, setSelected] = useState<ReadonlySet<string>>(() => new Set());
  const [texts, setTexts] = useState<ReadonlyMap<string, string>>(() => new Map());
  const [loading, setLoading] = useState(false);
  const [response, setResponse] = useState<SearchResponse | null>(null);
  const [error, setError] = useState<ApiError | null>(null);
  // The criteria `response` and `error` were returned for; set together with them.
  const [submitted, setSubmitted] = useState<SubmittedCriteria | null>(null);

  // Criteria belong to one form: another form starts with an empty picker. Adjusted
  // during render, so the other form's criteria are never shown, not even for a frame.
  const [criteriaViewId, setCriteriaViewId] = useState(viewId);
  if (criteriaViewId !== viewId) {
    setCriteriaViewId(viewId);
    setSelected(new Set());
    setTexts(new Map());
    setLoading(false);
    setResponse(null);
    setError(null);
    setSubmitted(null);
  }

  // A search still in flight for the previous form, or at unmount, is abandoned.
  useEffect(
    () => () => {
      controllerRef.current?.abort();
      controllerRef.current = null;
    },
    [viewId],
  );

  // The document listeners live for a whole open session and read the latest callback here.
  const onCloseRef = useRef(onClose);
  useEffect(() => {
    onCloseRef.current = onClose;
  });
  // A search's outcome arrives later and reports a missing form through the latest callback.
  const onFormNotFoundRef = useRef(onFormNotFound);
  useEffect(() => {
    onFormNotFoundRef.current = onFormNotFound;
  });

  // Modal isolation for one open session, as in ConfirmDialog and MessageDialog. The
  // element focused at open is recorded before focus moves into the dialog, and the
  // first checkbox takes focus before the page is made inert, so the page's blur names
  // it as the related target. Content added to <body> while open is made inert too;
  // focus that still lands outside is pulled back; Escape and Tab pressed outside the
  // dialog still act on it. Closing abandons a running search and drops its outcome.
  useEffect(() => {
    const backdrop = backdropRef.current;
    const dialog = dialogRef.current;
    if (!open || backdrop === null || dialog === null) {
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

    focusInitial(dialog);

    // Only the elements made inert here are released on close, so one that was already
    // inert stays inert. A newer modal dialog added to <body> isolates the page itself.
    const madeInert: Element[] = [];
    const makeInert = (element: Element) => {
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

    const isOutside = (target: EventTarget | null) => !(target instanceof Node && dialog.contains(target));

    // A newer modal dialog owns focus and keys while it is open: while it has made this
    // dialog inert, and for events inside it.
    const yieldsTo = (target: EventTarget | null) => {
      if (backdrop.closest('[inert]') !== null) {
        return true;
      }
      const modal = target instanceof Element ? target.closest(MODAL_SELECTOR) : null;
      return modal !== null && modal !== dialog;
    };

    const handleFocusIn = (event: FocusEvent) => {
      if (isOutside(event.target) && !yieldsTo(event.target)) {
        focusInitial(dialog);
      }
    };

    // Keys pressed inside the dialog are handled by its own onKeyDown.
    const handleDocumentKeyDown = (event: KeyboardEvent) => {
      if (!isOutside(event.target) || yieldsTo(event.target)) {
        return;
      }
      if (event.key === 'Escape') {
        event.preventDefault();
        onCloseRef.current();
      } else if (event.key === 'Tab') {
        trapTab(event, dialog);
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

      controllerRef.current?.abort();
      controllerRef.current = null;
      setLoading(false);
      setResponse(null);
      setError(null);
      setSubmitted(null);

      // Focus returns only when it has nowhere else to be: it was inside the dialog,
      // or on <body> once the dialog left the page.
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
  }, [open]);

  // Search disables itself while a search runs, which drops the focus it held, or the
  // Retry button's, which the outcome removes, to <body>. It takes that focus back once
  // the search settles; focus the user moved elsewhere meanwhile stays where it is.
  useEffect(() => {
    if (!open || loading || !refocusSearchRef.current) {
      return;
    }
    refocusSearchRef.current = false;
    const active = document.activeElement;
    if (active === null || active === document.body || active === document.documentElement) {
      searchButtonRef.current?.focus();
    }
  }, [open, loading]);

  if (!open) {
    return null;
  }

  const criteria = fields.filter((field) => selected.has(field.name));
  const atLimit = criteria.length >= MAX_SEARCH_FIELDS;
  const entries: CriterionEntry[] = criteria.map((field) => ({
    field: field.name,
    value: texts.get(field.name) ?? '',
  }));
  const searchKey = criteriaKey(entries, includeDeleted, openRecordId);

  // Pending policy. While a search runs, the picker, the value boxes and Remove stay
  // usable: an edit makes the running search's outcome out of date, shown as below
  // once it arrives. Search is disabled and the status line says "Searching…"; Close
  // is never disabled and abandons the search. Results and errors belong to the
  // criteria they were sent with (`submitted`). The results can be opened only when
  // no search is pending and the criteria on screen are the ones sent; otherwise they
  // stay on screen, marked out of date, and every row and Open button is
  // aria-disabled (focusable, inactive). A field's message shows while its criterion
  // holds the text sent, and the banner only while the whole criteria are unchanged.
  const current = submitted !== null && submitted.key === searchKey;
  const actionable = current && !loading;
  const { byField, other } = splitFieldErrors(error, fields, submitted, texts, selected);

  const setPicked = (name: string, picked: boolean) => {
    if (picked && atLimit) {
      return;
    }
    setSelected((current) => {
      const next = new Set(current);
      if (picked) {
        next.add(name);
      } else {
        next.delete(name);
      }
      return next;
    });
    if (!picked) {
      setTexts((current) => {
        if (!current.has(name)) {
          return current;
        }
        const next = new Map(current);
        next.delete(name);
        return next;
      });
    }
  };

  // The row and its Remove button disappear, so focus moves to the field's checkbox.
  const removeField = (name: string) => {
    setPicked(name, false);
    checkboxRefs.current.get(name)?.focus();
  };

  const setText = (name: string, text: string) => {
    setTexts((current) => new Map(current).set(name, text));
  };

  // Each picked field is sent with its text as entered, '' when nothing was typed; the
  // server trims, splits and decides what a blank means for the field's type. A newer
  // search replaces a running one, and only the latest search may settle the dialog.
  const runSearch = () => {
    if (criteria.length === 0) {
      return;
    }
    controllerRef.current?.abort();
    const controller = new AbortController();
    controllerRef.current = controller;

    const request: SearchRequest = {
      criteria: entries.map((entry) => ({ field: entry.field, value: entry.value })),
      includeDeleted,
      excludeGlobalRecordId: openRecordId ?? undefined,
    };
    const sent: SubmittedCriteria = {
      key: searchKey,
      texts: new Map(entries.map((entry) => [entry.field, entry.value])),
    };

    const settle = (nextResponse: SearchResponse | null, nextError: ApiError | null) => {
      if (controllerRef.current !== controller) {
        return;
      }
      controllerRef.current = null;
      setLoading(false);
      setResponse(nextResponse);
      setError(nextError);
      setSubmitted(sent);
    };

    refocusSearchRef.current = true;
    setLoading(true);
    setError(null);
    void client.searchRecords(viewId, request, controller.signal).then(
      (result) => {
        if (result.ok) {
          settle(result.data, null);
        } else {
          // A 404 of the latest search means the form itself is gone: the form view's
          // form-not-found handling takes over. A superseded search reports nothing.
          const formGone = result.error.kind === 'notFound' && controllerRef.current === controller;
          settle(null, result.error);
          if (formGone) {
            onFormNotFoundRef.current();
          }
        }
      },
      (reason: unknown) => {
        // The dialog's own abort: a newer search, closing, another form or unmounting.
        if (controller.signal.aborted) {
          return;
        }
        // The client rejects only for an abort. One the dialog did not cause, such as the
        // browser stopping the page's requests, failed nothing: the search settles idle with
        // no result and no error, so Search is enabled again.
        settle(null, isAbortError(reason) ? null : networkError());
      },
    );
  };

  const handleSubmit = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    runSearch();
  };

  const handleKeyDown = (event: ReactKeyboardEvent<HTMLDivElement>) => {
    if (event.key === 'Escape') {
      event.preventDefault();
      event.stopPropagation();
      onClose();
      return;
    }
    if (event.key === 'Tab' && dialogRef.current !== null) {
      trapTab(event.nativeEvent, dialogRef.current);
    }
  };

  // A press on the backdrop does not close the dialog; preventing its default also
  // keeps focus inside the dialog instead of moving it to the page.
  const handleBackdropMouseDown = (event: MouseEvent<HTMLDivElement>) => {
    if (event.target === event.currentTarget) {
      event.preventDefault();
    }
  };

  const items = response?.items ?? [];
  const columns = items.length > 0 ? Object.keys(items[0].values) : [];
  let statusText = '';
  if (loading) {
    statusText = items.length > 0 ? SEARCHING_OVER_RESULTS : SEARCHING;
  } else if (response !== null) {
    statusText = current ? resultSummary(response) : `${resultSummary(response)} ${STALE_RESULTS}`;
  }

  // The banner speaks of the search that failed, so it shows only while the criteria
  // on screen are the ones that search sent.
  let banner: ReactNode = null;
  if (error !== null && current) {
    if (isConnectionFailure(error)) {
      banner = <ConnectionError error={error} method="GET" onRetry={runSearch} />;
    } else {
      const lines = bannerLines(error, other, byField.size > 0);
      const traceId = error.kind === 'validation' ? null : error.traceId;
      const traceLine = traceId !== null && !lines.some((line) => line.includes(traceId)) ? traceId : null;
      // Empty once every field message belonged to a criterion the user has removed.
      banner = lines.length === 0 ? null : (
        <div className="banner banner--error" role="alert">
          {lines.map((line, index) => (
            <p key={`${index}-${line}`}>{line}</p>
          ))}
          {traceLine !== null && (
            <p>
              <small>{`Trace id: ${traceLine}`}</small>
            </p>
          )}
        </div>
      );
    }
  }

  return createPortal(
    <div ref={backdropRef} className="dialog-backdrop" onMouseDown={handleBackdropMouseDown}>
      <div
        ref={dialogRef}
        className="dialog find-dialog"
        role="dialog"
        aria-modal="true"
        aria-labelledby={headingId}
        tabIndex={-1}
        onKeyDown={handleKeyDown}
      >
        <h2 id={headingId}>Find record</h2>
        {banner}

        <form id={formId} noValidate onSubmit={handleSubmit}>
          <fieldset className="find-dialog__fields">
            <legend>Fields to search</legend>
            {fields.map((field, index) => {
              const picked = selected.has(field.name);
              const disabled = !picked && atLimit;
              const nameId = `${baseId}-field-${index}`;
              const descriptionId = `${nameId}-description`;
              // A prompt that only repeats the name would be read twice.
              const described = field.label !== field.name;
              const describedBy = [described ? descriptionId : null, disabled ? limitNoteId : null]
                .filter((id): id is string => id !== null)
                .join(' ');
              return (
                <label key={field.name}>
                  <input
                    ref={(element) => {
                      if (element === null) {
                        checkboxRefs.current.delete(field.name);
                      } else {
                        checkboxRefs.current.set(field.name, element);
                      }
                    }}
                    type="checkbox"
                    checked={picked}
                    disabled={disabled}
                    aria-labelledby={nameId}
                    aria-describedby={describedBy === '' ? undefined : describedBy}
                    onChange={(event) => {
                      setPicked(field.name, event.target.checked);
                    }}
                  />
                  <span id={nameId}>{field.name}</span>
                  {described && <small id={descriptionId}>{field.label}</small>}
                </label>
              );
            })}
          </fieldset>
          {/* Always rendered, so the note is announced when the sixth field is picked. */}
          <p id={limitNoteId} role="status" className={atLimit ? undefined : 'visually-hidden'}>
            {atLimit ? LIMIT_NOTE : ''}
          </p>

          {criteria.length === 0 ? (
            <p>
              <small>{NO_FIELD_NOTE}</small>
            </p>
          ) : (
            fields.map((field, index) => {
              if (!selected.has(field.name)) {
                return null;
              }
              const inputId = `${baseId}-value-${index}`;
              const hintId = `${inputId}-hint`;
              const errorId = `${inputId}-error`;
              const messages = byField.get(field.name) ?? [];
              const invalid = messages.length > 0;
              return (
                <div key={field.name} className="find-dialog__criterion">
                  <label htmlFor={inputId}>Value for {field.name}</label>
                  <input
                    id={inputId}
                    type="text"
                    value={texts.get(field.name) ?? ''}
                    autoComplete="off"
                    spellCheck={false}
                    aria-invalid={invalid ? true : undefined}
                    aria-describedby={invalid ? `${hintId} ${errorId}` : hintId}
                    onChange={(event) => {
                      setText(field.name, event.target.value);
                    }}
                  />
                  <button
                    type="button"
                    onClick={() => {
                      removeField(field.name);
                    }}
                  >
                    Remove<span className="visually-hidden"> {field.name}</span>
                  </button>
                  <small id={hintId}>{field.hint}</small>
                  {invalid && (
                    <div className="field-error" id={errorId}>
                      {messages.map((message, messageIndex) => (
                        <p key={`${messageIndex}-${message}`}>{message}</p>
                      ))}
                    </div>
                  )}
                </div>
              );
            })
          )}
        </form>

        {/* Outside the form, which lays its children out in one column; the form
            attribute keeps Search the form's submit button, so Enter in a value searches. */}
        <button ref={searchButtonRef} type="submit" form={formId} disabled={criteria.length === 0 || loading}>
          Search
        </button>
        <button
          type="button"
          onClick={() => {
            onClose();
          }}
        >
          Close
        </button>

        {/* Always rendered, so "Searching…" and the result count are announced. */}
        <p id={statusId} role="status" className={statusText === '' ? 'visually-hidden' : undefined}>
          {statusText}
        </p>
        {items.length > 0 && (
          // A named, focusable region, so a keyboard can scroll the results sideways.
          <div
            className={actionable ? 'find-dialog__results' : 'find-dialog__results find-dialog__results--stale'}
            role="region"
            aria-label={RESULTS_REGION_LABEL}
            tabIndex={0}
          >
            <table aria-busy={loading}>
              <caption className="visually-hidden">Matching records</caption>
              <thead>
                <tr>
                  <th scope="col">Position</th>
                  {columns.map((column) => (
                    <th key={column} scope="col">
                      {labels.get(column) ?? column}
                    </th>
                  ))}
                </tr>
              </thead>
              <tbody>
                {items.map((item) => {
                  const deleted = item.recStatus === 0;
                  return (
                    <tr
                      key={item.globalRecordId}
                      className={deleted ? 'record-grid__row--deleted' : undefined}
                      aria-disabled={actionable ? undefined : 'true'}
                      onClick={() => {
                        if (actionable) {
                          onOpen(item.globalRecordId);
                        }
                      }}
                    >
                      <td>
                        <button
                          type="button"
                          aria-label={`Open record at position ${item.position}`}
                          aria-disabled={actionable ? undefined : 'true'}
                          aria-describedby={actionable ? undefined : statusId}
                          onClick={(event) => {
                            // The row's own click would report the same record a second time.
                            event.stopPropagation();
                            if (actionable) {
                              onOpen(item.globalRecordId);
                            }
                          }}
                        >
                          {item.position}
                        </button>
                        {deleted && <span> (deleted)</span>}
                      </td>
                      {columns.map((column) => {
                        const text = cellText(Object.hasOwn(item.values, column) ? item.values[column] : undefined);
                        // The title holds the full value, which the column may cut short.
                        return (
                          <td key={column} title={text === '' ? undefined : text}>
                            {text}
                          </td>
                        );
                      })}
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </div>,
    document.body,
  );
}
