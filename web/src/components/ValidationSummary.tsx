// The error list of a rejected or blocked save: client feedback, the server's field-level `errors`
// and a request-level `detail`. It lists every message it is given, every missing required field
// included, where legacy's required check showed a message box for the first missing field only.
// Render-only: no state, no effects and no rule about what counts as an error.
import { useId, useMemo } from 'react';
import type { FieldDefinition, FormDefinition, PageDefinition } from '../api/types';
import { isDataField, type FieldErrors } from '../validation/clientValidation';

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
}

function byPosition(a: PageDefinition, b: PageDefinition): number {
  return a.position - b.position || a.pageId - b.pageId;
}

/** A definition's fields in form order, and the names its data fields hold. */
interface FormOrder {
  readonly fields: readonly FieldDefinition[];
  readonly dataFieldNames: ReadonlySet<string>;
}

/**
 * The form order of each definition a summary has listed errors for. A loaded definition is
 * never changed, so its order is worked out once, on its first error, whatever error maps
 * follow; a definition no longer referenced drops out with its entry.
 */
const formOrders = new WeakMap<FormDefinition, FormOrder>();

/** Every field in form order: pages by `position`, then fields by `tabIndex` (a stable sort, so ties keep metadata order). */
function formOrderOf(definition: FormDefinition): FormOrder {
  const cached = formOrders.get(definition);
  if (cached !== undefined) {
    return cached;
  }
  const fields: FieldDefinition[] = [];
  for (const page of [...definition.pages].sort(byPosition)) {
    fields.push(...[...page.fields].sort((a, b) => a.tabIndex - b.tabIndex));
  }
  const order: FormOrder = {
    fields,
    dataFieldNames: new Set(fields.filter(isDataField).map((field) => field.name)),
  };
  formOrders.set(definition, order);
  return order;
}

/** The text the field's control is labelled with: its prompt, or its name when the prompt is missing or blank. */
function fieldLabel(field: FieldDefinition): string {
  const prompt = field.prompt;
  return typeof prompt === 'string' && prompt.trim() !== '' ? prompt : field.name;
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

  const add = (name: string, label: string, isField: boolean, messages: readonly string[]) => {
    listed.add(name);
    messages.forEach((message, index) => {
      items.push({ key: `${isField ? 'field' : 'other'}:${name}:${String(index)}`, name, label, isField, message });
    });
  };

  const { fields, dataFieldNames } = formOrderOf(definition);

  for (const field of fields) {
    if (!isDataField(field) && dataFieldNames.has(field.name)) {
      continue;
    }
    if (!listed.has(field.name) && Object.hasOwn(fieldErrors, field.name)) {
      add(field.name, fieldLabel(field), true, fieldErrors[field.name]);
    }
  }

  for (const [name, messages] of Object.entries(fieldErrors)) {
    if (!listed.has(name)) {
      add(name, name, false, messages);
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
 * the form, then messages under keys that name no field, after the request-level `detail`. Each
 * message is shown verbatim: the required-field message already names the prompt and page. Renders
 * nothing when there is no message and no detail.
 */
export function ValidationSummary({
  definition,
  fieldErrors,
  detail = null,
  title = DEFAULT_TITLE,
  onSelectField,
  canSelectField,
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
    <div className="validation-summary" role="alert">
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
                {separatorAfter(item.label)}
                {item.message}
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
