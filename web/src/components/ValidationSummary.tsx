// The error list of a rejected or blocked save: client feedback, the server's field-level `errors`
// and a request-level `detail`. It lists every message it is given, every missing required field
// included, where legacy's required check showed a message box for the first missing field only.
// Render-only: no state, no effects and no rule about what counts as an error.
import { useId, useMemo, type RefObject } from 'react';
import type { FieldDefinition, FormDefinition, PageDefinition } from '../api/types';
import { isDataField, type FieldErrors } from '../validation/clientValidation';

/** The action an error list stopped, as {@link ValidationSummaryProps.blocked} takes it. */
export interface BlockedAction {
  /** One line saying what did not happen, such as "Could not move to the next record: correct the fields below." */
  readonly message: string;
  /** Counts the blocked attempts; each new value is a new line, which the alert announces again. */
  readonly attempt: number;
}

export interface ValidationSummaryProps {
  /** The open form: maps a field name to its prompt and orders the messages by the field's place. */
  definition: FormDefinition;
  /**
   * Messages keyed by canonical field name, or by parameter or body member name (`values`,
   * `criteria`, `includeDeleted`, …) for an error that names no field. A read-only view of
   * {@link FieldErrors}, so the editor's map, a server `errors` map or a merge of both is accepted
   * as it is. A key with no messages shows nothing.
   */
  fieldErrors: Readonly<Record<keyof FieldErrors, readonly string[]>>;
  /** The request-level ProblemDetails `detail`, such as "the record has no values"; blank shows nothing. */
  detail?: string | null;
  /** Heading text. */
  title?: string;
  /**
   * When given, each field's label is a button that calls it with the canonical field name, so the
   * form can select the field's page and focus its control. Keys that name no field stay plain text.
   */
  onSelectField?: (fieldName: string) => void;
  /**
   * Whether the field's control can take focus now: it is rendered on a page and is not disabled.
   * With `onSelectField`, a field for which it returns `false` keeps its label button, marked
   * `aria-disabled` and described by a note that the field cannot be edited, and activating it
   * does nothing, instead of a jump that would focus nothing. The button stays the same element
   * as the answer changes, so focus on it is kept. Omitted, every field can.
   */
  canSelectField?: (fieldName: string) => boolean;
  /**
   * The save or move these errors stopped, shown as a line above the heading, so the user
   * learns why they are still on the record. The line is keyed by `attempt`, so a repeated
   * attempt replaces it with a new line, announced again although its text is the same.
   * Shown only while the summary lists a message or a detail; `null` or omitted shows none.
   */
  blocked?: BlockedAction | null;
  /**
   * The summary's root, which can take focus (tabIndex -1): the form focuses it when a blocked
   * action leaves the first error out of view, and focusing it scrolls it into view.
   */
  containerRef?: RefObject<HTMLDivElement>;
}

const DEFAULT_TITLE = 'Please correct the following';

/** Shown after the message of a field whose control cannot take focus; it also describes the label button. */
const NOT_EDITABLE_TEXT = '(This field cannot be edited right now.)';

/** One list item: a single message and the label it is shown under. */
interface SummaryItem {
  readonly key: string;
  readonly name: string;
  readonly label: string;
  readonly isField: boolean;
  readonly message: string;
  /**
   * Whether the message is the server's required-field message of a required field, which opens
   * with the field's prompt and names its page: the label, that prompt, is then shown as the
   * message's opening words, not ahead of it. `false` for every other message, the client's own
   * included, and for a key that names no field.
   */
  readonly leadsWithLabel: boolean;
}

function byPosition(a: PageDefinition, b: PageDefinition): number {
  return a.position - b.position || a.pageId - b.pageId;
}

/** A field and the name of the page it sits on. */
interface PlacedField {
  readonly field: FieldDefinition;
  readonly pageName: string;
}

/** A definition's fields in form order, each with its page's name, and the names its data fields hold. */
interface FormOrder {
  readonly fields: readonly PlacedField[];
  readonly dataFieldNames: ReadonlySet<string>;
}

/**
 * The form order of each definition a summary has listed errors for. A loaded definition is
 * never changed, so its order is worked out once, on its first error, whatever error maps
 * follow; a definition no longer referenced drops out with its entry.
 */
const formOrders = new WeakMap<FormDefinition, FormOrder>();

/**
 * Every field in form order, each with the name of its page: pages by `position`, then fields by
 * `tabIndex` (a stable sort, so ties keep metadata order).
 */
function formOrderOf(definition: FormDefinition): FormOrder {
  const cached = formOrders.get(definition);
  if (cached !== undefined) {
    return cached;
  }
  const fields: PlacedField[] = [];
  for (const page of [...definition.pages].sort(byPosition)) {
    for (const field of [...page.fields].sort((a, b) => a.tabIndex - b.tabIndex)) {
      fields.push({ field, pageName: page.name });
    }
  }
  const order: FormOrder = {
    fields,
    dataFieldNames: new Set(fields.filter(({ field }) => isDataField(field)).map(({ field }) => field.name)),
  };
  formOrders.set(definition, order);
  return order;
}

/** The text the field's control is labelled with: its prompt, or its name when the prompt is missing or blank. */
function fieldLabel(field: FieldDefinition): string {
  const prompt = field.prompt;
  return typeof prompt === 'string' && prompt.trim() !== '' ? prompt : field.name;
}

// Whitespace at the start of the text that follows a label.
const LEADING_WHITESPACE = /^\s/;

/**
 * Whether `message` opens with `label` as its own words: it starts with the non-empty label and
 * whitespace follows it, so a label that is only the start of a longer word ("Age" before
 * "Agency") does not count. The label of a field whose prompt is missing or blank is its name, so
 * a message opening with that blank prompt does not open with the label. One of the conditions of
 * {@link isServerRequiredMessage}.
 */
function opensWithLabel(message: string, label: string): boolean {
  return label !== '' && message.startsWith(label) && LEADING_WHITESPACE.test(message.slice(label.length));
}

/**
 * Whether `message` is the server's required-field message of the placed field, recognised by its
 * shape and never by its wording. It rests on one invariant: the server's required-field message
 * of a required field opens with its prompt and names its page. So the field is required, the
 * message opens with `label`, the field's label, as its own words ({@link opensWithLabel}), and the
 * text after the label contains the name of the field's page, which is not blank. The client's own
 * messages name no page, so none qualifies; a blank page name, which any text contains, qualifies
 * none.
 */
function isServerRequiredMessage({ field, pageName }: PlacedField, label: string, message: string): boolean {
  return (
    field.validation.required &&
    pageName.trim() !== '' &&
    opensWithLabel(message, label) &&
    message.slice(label.length).includes(pageName)
  );
}

const NO_ITEMS: readonly SummaryItem[] = [];

/**
 * Field keys in form order, then the keys that name no field in the map's own order. A key is
 * placed and labelled at the data field of exactly its name, which owns that key in `values` and
 * renders the control a jump focuses; a label, group or placeholder sharing the name is passed
 * over. A name no data field holds is placed at its first field in form order. A map with no
 * key, the state of almost every render, lists nothing without walking the metadata.
 */
function summaryItems(
  definition: FormDefinition,
  fieldErrors: ValidationSummaryProps['fieldErrors'],
): readonly SummaryItem[] {
  if (Object.keys(fieldErrors).length === 0) {
    return NO_ITEMS;
  }

  const items: SummaryItem[] = [];
  const listed = new Set<string>();

  // `placed` is the field that owns `name`, or `null` for a key that names no field.
  const add = (name: string, label: string, placed: PlacedField | null, messages: readonly string[]) => {
    listed.add(name);
    messages.forEach((message, index) => {
      items.push({
        key: `${placed !== null ? 'field' : 'other'}:${name}:${String(index)}`,
        name,
        label,
        isField: placed !== null,
        message,
        leadsWithLabel: placed !== null && isServerRequiredMessage(placed, label, message),
      });
    });
  };

  const { fields, dataFieldNames } = formOrderOf(definition);

  for (const placed of fields) {
    const { field } = placed;
    if (!isDataField(field) && dataFieldNames.has(field.name)) {
      continue;
    }
    if (!listed.has(field.name) && Object.hasOwn(fieldErrors, field.name)) {
      add(field.name, fieldLabel(field), placed, fieldErrors[field.name]);
    }
  }

  for (const [name, messages] of Object.entries(fieldErrors)) {
    if (!listed.has(name)) {
      add(name, name, null, messages);
    }
  }

  return items;
}

// Many metadata prompts already end with a colon ("Lab:", "Collection date:"), so the separator
// between label and message is not doubled after one.
const ENDS_WITH_COLON = /:\s*$/;

function separatorAfter(label: string): string {
  return ENDS_WITH_COLON.test(label) ? ' ' : ': ';
}

/**
 * Lists every message of `fieldErrors` under its field's prompt, in the order the fields appear in
 * the form, then messages under keys that name no field, after the request-level `detail`. The
 * server's required-field message of a required field opens with its prompt and names its page:
 * it is shown once, verbatim, with the prompt, its opening words, as its jump button. Every other
 * message, the client's own included, is shown verbatim after its label. The action the errors
 * stopped, when given, heads the list. Renders nothing when there is no message and no detail.
 */
export function ValidationSummary({
  definition,
  fieldErrors,
  detail = null,
  title = DEFAULT_TITLE,
  onSelectField,
  canSelectField,
  blocked = null,
  containerRef,
}: ValidationSummaryProps) {
  // The form re-renders on every keystroke while the definition and the error map keep their
  // identity until either changes, so the ordered items are computed once per change. Whether a
  // field can be selected changes without them and is asked at render time below.
  const items = useMemo(() => summaryItems(definition, fieldErrors), [definition, fieldErrors]);
  // Prefix of the ids that tie an unavailable label button to its note.
  const noteIdPrefix = useId();
  const detailText = typeof detail === 'string' && detail.trim() !== '' ? detail : null;

  if (items.length === 0 && detailText === null) {
    return null;
  }

  return (
    <div ref={containerRef} className="validation-summary" role="alert" tabIndex={-1}>
      {blocked !== null && (
        <p key={`blocked-${String(blocked.attempt)}`}>
          <strong>{blocked.message}</strong>
        </p>
      )}
      <h3>{title}</h3>
      {detailText !== null && <p>{detailText}</p>}
      {items.length > 0 && (
        <ul>
          {items.map((item, index) => {
            const offersJump = item.isField && onSelectField !== undefined;
            const selectable = offersJump && (canSelectField === undefined || canSelectField(item.name));
            const noteId = `${noteIdPrefix}-note-${String(index)}`;
            return (
              <li key={item.key}>
                {offersJump ? (
                  // One button in both states, so focus on it survives the field becoming
                  // unavailable. aria-disabled keeps it focusable; Enter and Space still click
                  // it, so the handler ignores activation while it is unavailable.
                  <button
                    type="button"
                    aria-disabled={selectable ? undefined : true}
                    aria-describedby={selectable ? undefined : noteId}
                    onClick={() => {
                      if (selectable) {
                        onSelectField(item.name);
                      }
                    }}
                  >
                    {item.label}
                  </button>
                ) : (
                  <strong>{item.label}</strong>
                )}
                {item.leadsWithLabel ? (
                  // The label stands as the message's opening words, so the prompt appears once.
                  item.message.slice(item.label.length)
                ) : (
                  <>
                    {separatorAfter(item.label)}
                    {item.message}
                  </>
                )}
                {offersJump && !selectable && (
                  <>
                    {' '}
                    <span id={noteId}>{NOT_EDITABLE_TEXT}</span>
                  </>
                )}
              </li>
            );
          })}
        </ul>
      )}
    </div>
  );
}
