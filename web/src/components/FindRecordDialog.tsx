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
}

/** Legacy SEARCH_LIMIT_EXCEEDED: a search names at most six fields. */
const MAX_SEARCH_FIELDS = 6;
const LIMIT_NOTE = 'At most six fields can be searched.';
const NO_FIELD_NOTE = 'Choose up to six fields, then enter a value for each.';

const TEXT_HINT = 'Use a or b for alternatives, >=, <=, <>, >, < to compare, * as a wildcard (e.g. Sm*).';
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
const FORM_NOT_FOUND = 'This form no longer exists. Close this dialog and choose a form from the forms list.';
const SEARCHING = 'Searching…';
const NO_MATCHES = 'No records match.';

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
    return TEXT_HINT;
  }
  const listed = options.slice(0, LISTED_LEGAL_VALUES).map((option) => option.label);
  const more = options.length > LISTED_LEGAL_VALUES ? ', …' : '';
  return `${TEXT_HINT} ${ALLOWED_VALUES} ${listed.join(', ')}${more}.`;
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

/**
 * Splits a 400's `errors` map. Messages keyed by a searchable field, matched
 * case-insensitively, go beside that field's criterion while it is picked, and are
 * dropped once its row is removed; every other key (`criteria`,
 * `excludeGlobalRecordId`, …) goes to the banner.
 */
function splitFieldErrors(
  error: ApiError | null,
  fields: readonly SearchableField[],
  picked: ReadonlySet<string>,
): { byField: ReadonlyMap<string, readonly string[]>; other: readonly string[] } {
  const byField = new Map<string, string[]>();
  const other: string[] = [];
  if (error === null) {
    return { byField, other };
  }
  const canonical = new Map(fields.map((field) => [field.name.toLowerCase(), field.name]));
  for (const [key, messages] of Object.entries(error.fieldErrors)) {
    const name = canonical.get(key.toLowerCase());
    if (name === undefined) {
      other.push(...messages);
    } else if (picked.has(name)) {
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
}: FindRecordDialogProps) {
  const baseId = useId();
  const headingId = `${baseId}-heading`;
  const formId = `${baseId}-form`;
  const limitNoteId = `${baseId}-limit`;
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
  const { byField, other } = splitFieldErrors(error, fields, selected);

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
      criteria: criteria.map((field) => ({ field: field.name, value: texts.get(field.name) ?? '' })),
      includeDeleted,
      excludeGlobalRecordId: openRecordId ?? undefined,
    };

    const settle = (nextResponse: SearchResponse | null, nextError: ApiError | null) => {
      if (controllerRef.current !== controller) {
        return;
      }
      controllerRef.current = null;
      setLoading(false);
      setResponse(nextResponse);
      setError(nextError);
    };

    refocusSearchRef.current = true;
    setLoading(true);
    setError(null);
    void client.searchRecords(viewId, request, controller.signal).then(
      (result) => {
        if (result.ok) {
          settle(result.data, null);
        } else {
          settle(null, result.error);
        }
      },
      (reason: unknown) => {
        // The client rejects only for an abort, which is this dialog's own decision.
        if (controller.signal.aborted || isAbortError(reason)) {
          return;
        }
        settle(null, networkError());
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

  const statusText = loading ? SEARCHING : response !== null ? resultSummary(response) : '';
  const items = response?.items ?? [];
  const columns = items.length > 0 ? Object.keys(items[0].values) : [];

  let banner: ReactNode = null;
  if (error !== null) {
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
        <p role="status" className={statusText === '' ? 'visually-hidden' : undefined}>
          {statusText}
        </p>
        {items.length > 0 && (
          <div className="find-dialog__results">
            <table>
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
                      onClick={() => {
                        onOpen(item.globalRecordId);
                      }}
                    >
                      <td>
                        <button
                          type="button"
                          aria-label={`Open record at position ${item.position}`}
                          onClick={(event) => {
                            // The row's own click would report the same record a second time.
                            event.stopPropagation();
                            onOpen(item.globalRecordId);
                          }}
                        >
                          {item.position}
                        </button>
                        {deleted && <span> (deleted)</span>}
                      </td>
                      {columns.map((column) => (
                        <td key={column}>
                          {cellText(Object.hasOwn(item.values, column) ? item.values[column] : undefined)}
                        </td>
                      ))}
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
