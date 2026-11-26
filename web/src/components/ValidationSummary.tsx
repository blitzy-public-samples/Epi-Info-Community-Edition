// The error list of a rejected or blocked save: client feedback, the server's field-level `errors`
// and a request-level `detail`. It lists every message it is given, every missing required field
// included, where legacy's required check showed a message box for the first missing field only.
// Render-only: no state, no effects and no rule about what counts as an error.
import type { FieldDefinition, FormDefinition, PageDefinition } from '../api/types';
import type { FieldErrors } from '../validation/clientValidation';

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
}

const DEFAULT_TITLE = 'Please correct the following';

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

/** Every field in form order: pages by `position`, then fields by `tabIndex` (a stable sort, so ties keep metadata order). */
function fieldsInFormOrder(definition: FormDefinition): FieldDefinition[] {
  const fields: FieldDefinition[] = [];
  for (const page of [...definition.pages].sort(byPosition)) {
    fields.push(...[...page.fields].sort((a, b) => a.tabIndex - b.tabIndex));
  }
  return fields;
}

/** The text the field's control is labelled with: its prompt, or its name when the prompt is missing or blank. */
function fieldLabel(field: FieldDefinition): string {
  const prompt = field.prompt;
  return typeof prompt === 'string' && prompt.trim() !== '' ? prompt : field.name;
}

/** Field keys in form order, then the keys that name no field in the map's own order. */
function summaryItems(definition: FormDefinition, fieldErrors: ValidationSummaryProps['fieldErrors']): SummaryItem[] {
  const items: SummaryItem[] = [];
  const listed = new Set<string>();

  const add = (name: string, label: string, isField: boolean, messages: readonly string[]) => {
    listed.add(name);
    messages.forEach((message, index) => {
      items.push({ key: `${isField ? 'field' : 'other'}:${name}:${String(index)}`, name, label, isField, message });
    });
  };

  for (const field of fieldsInFormOrder(definition)) {
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
}: ValidationSummaryProps) {
  const items = summaryItems(definition, fieldErrors);
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
          {items.map((item) => (
            <li key={item.key}>
              {item.isField && onSelectField !== undefined ? (
                <button type="button" onClick={() => onSelectField(item.name)}>
                  {item.label}
                </button>
              ) : (
                <strong>{item.label}</strong>
              )}
              {separatorAfter(item.label)}
              {item.message}
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
