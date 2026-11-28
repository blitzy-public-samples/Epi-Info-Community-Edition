/**
 * Applies one check-code field-event response to the record editor's state.
 *
 * - Pure: no I/O, no timers, no globals, and the inputs are never mutated; the
 *   result holds fresh copies.
 * - Applies the server's effects only. Check code runs in the API, which has already
 *   ordered the effects, resolved their conflicts and expanded group names to their
 *   members, so names are applied exactly as the response lists them.
 * - `enabled`/`disabled` set only the check-code flag. Protected state (read-only
 *   metadata, a deleted record, an unsupported field) is not represented here, so an
 *   `ENABLE` can never unlock a protected control.
 * - `cleared`/`assigned` reach only data fields and lose to a user edit made after the
 *   event was captured. Applied to a writable field they mark it dirty, so the next save
 *   sends it; applied to a read-only field the value is shown but never sent, because a
 *   read-only field in a write body is a 400 (C-3 in docs/parity-notes.md). Which fields
 *   are data fields and which are writable is decided by `isDataField` and
 *   `isWritableDataField` of validation/clientValidation, the predicates the save uses.
 */
import type {
  Diagnostic,
  DialogMessage,
  FieldDefinition,
  FieldEventResponse,
  FieldValue,
  FormDefinition,
  Values,
} from '../api/types';
import { isDataField, isWritableDataField } from '../validation/clientValidation';

/** The editor state a field-event response acts on. */
export interface EditorSnapshot {
  /** Field values keyed by canonical field name. */
  values: Values;
  /** Fields that check code has disabled; protected state is tracked separately. */
  checkCodeDisabled: ReadonlySet<string>;
  /** Fields the next `PUT` or `POST` must send. */
  dirty: ReadonlySet<string>;
}

export interface ApplyFieldEventInput {
  snapshot: EditorSnapshot;
  response: FieldEventResponse;
  definition: FormDefinition;
  /** Fields the user edited after the event's capture point; their edits win. */
  editedSince: ReadonlySet<string>;
}

export interface ApplyFieldEventResult {
  snapshot: EditorSnapshot;
  /** The response's dialogs, in execution order. */
  dialogs: DialogMessage[];
  /** The response's diagnostics, in source order; they never block anything. */
  diagnostics: Diagnostic[];
}

function indexFields(definition: FormDefinition): Map<string, FieldDefinition> {
  const fields = new Map<string, FieldDefinition>();
  for (const page of definition.pages) {
    for (const field of page.fields) {
      if (!fields.has(field.name)) {
        fields.set(field.name, field);
      }
    }
  }
  return fields;
}

// The record editor's queue passes only an answer that the client's raiseFieldEvent has
// checked, with all six members present in their contract types. These defaults are a guard
// for a direct caller: a list or map it leaves out is read as empty instead of throwing.
function listOf<T>(list: readonly T[] | null | undefined): readonly T[] {
  return list ?? [];
}

function mapOf(map: Values | null | undefined): Values {
  return map ?? {};
}

export function applyFieldEvent(input: ApplyFieldEventInput): ApplyFieldEventResult {
  const { snapshot, response, definition, editedSince } = input;
  const fields = indexFields(definition);
  const disabled = new Set(snapshot.checkCodeDisabled);
  const dirty = new Set(snapshot.dirty);
  // Collected first and merged once, so every field becomes an own data property even
  // when its name is one an assignment would treat specially, such as `__proto__`.
  const updates = new Map<string, FieldValue>();

  // Fixed order, enabled then disabled and cleared then assigned, keeps a malformed
  // response deterministic: disabled beats enabled and assigned beats cleared.
  for (const name of listOf(response.enabled)) {
    if (fields.has(name)) {
      disabled.delete(name);
    }
  }
  for (const name of listOf(response.disabled)) {
    if (fields.has(name)) {
      disabled.add(name);
    }
  }

  const applyValue = (name: string, value: FieldValue): void => {
    const field = fields.get(name);
    if (field === undefined || !isDataField(field) || editedSince.has(name)) {
      return;
    }
    updates.set(name, value);
    if (isWritableDataField(field)) {
      dirty.add(name);
    } else {
      dirty.delete(name);
    }
  };

  for (const name of listOf(response.cleared)) {
    applyValue(name, null);
  }
  // Key order is the server's order of first occurrence.
  for (const [name, value] of Object.entries(mapOf(response.assigned))) {
    applyValue(name, value);
  }

  const values: Values = { ...snapshot.values, ...Object.fromEntries(updates) };

  return {
    snapshot: { values, checkCodeDisabled: disabled, dirty },
    dialogs: [...listOf(response.dialogs)],
    diagnostics: [...listOf(response.diagnostics)],
  };
}
