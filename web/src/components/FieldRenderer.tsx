// Lays out one page of a form and renders each field through the field component map.
// It replaces the legacy Canvas and ControlFactory.GetPageControls, which placed every
// control at its designer coordinates: here the page is a flow layout in `tabIndex`
// order, and a group is a section holding its members. Render-only: every value, error
// and state comes from props, and no check-code or validation rule is decided here.
import { useMemo, type ReactNode } from 'react';
import type { FieldDefinition, FieldValue, FormDefinition, PageDefinition, Values } from '../api/types';
import { isDataField, type FieldErrors } from '../validation/clientValidation';
import { fieldComponents } from './fields/fieldComponents';

export interface FieldRendererProps {
  /** The page to lay out; every field it lists renders exactly once. */
  page: PageDefinition;
  /** The form the page belongs to; group member names resolve against all of its fields. */
  definition: FormDefinition;
  /** The editor's current values, keyed by canonical field name. */
  values: Values;
  /** Messages keyed by canonical field name. */
  fieldErrors: FieldErrors;
  /** Read-only metadata, a deleted or read-only record, or an unsupported field. */
  isProtected: (field: FieldDefinition) => boolean;
  /** Disabled by check code (`DISABLE`); never unlocks a protected field. */
  isCheckCodeDisabled: (field: FieldDefinition) => boolean;
  onChange: (fieldName: string, value: FieldValue) => void;
  /** Raised when a field's control gains focus (`FieldBefore`). */
  onFocus?: (fieldName: string) => void;
  /** Raised when a field's control loses focus (`FieldAfter`). */
  onBlur?: (fieldName: string) => void;
  /** Raised after a checkbox or radio toggles (`FieldClick`). */
  onClick?: (fieldName: string) => void;
}

/** One top-level entry of a page: a field, and for a group its members in `tabIndex` order. */
export type LayoutItem = { field: FieldDefinition; members: FieldDefinition[] };

// Shared empty list, so a field without errors receives the same array on every render.
const NO_ERRORS: readonly string[] = [];

// `tabIndex` may be fractional; equal indexes fall back to an ordinal name comparison,
// so the order never depends on the locale.
function byTabIndex(a: FieldDefinition, b: FieldDefinition): number {
  return a.tabIndex - b.tabIndex || (a.name < b.name ? -1 : a.name > b.name ? 1 : 0);
}

function nameKey(name: string): string {
  return name.toLowerCase();
}

// Field names are user-defined metadata, so a name such as `constructor` must read only
// the record's own entry and never a member inherited from Object.prototype.
function ownEntry<T>(record: Readonly<Record<string, T>>, key: string): T | undefined {
  return Object.hasOwn(record, key) ? record[key] : undefined;
}

interface FieldIndex {
  exact: Map<string, FieldDefinition>;
  ignoringCase: Map<string, FieldDefinition>;
}

// Indexes every field of the form, this page's own field objects first, so a member that
// resolves to a field on this page resolves to the object the page renders.
function indexFormFields(page: PageDefinition, pageFields: readonly FieldDefinition[], definition: FormDefinition): FieldIndex {
  const index: FieldIndex = { exact: new Map(), ignoringCase: new Map() };
  const add = (field: FieldDefinition) => {
    if (!index.exact.has(field.name)) {
      index.exact.set(field.name, field);
    }
    const key = nameKey(field.name);
    if (!index.ignoringCase.has(key)) {
      index.ignoringCase.set(key, field);
    }
  };

  pageFields.forEach(add);
  for (const other of definition.pages) {
    if (other.pageId !== page.pageId) {
      other.fields.forEach(add);
    }
  }
  return index;
}

// A member name resolves against the whole form, the exact spelling first and then
// ignoring case, as the server resolves `metaFields.List`. Matching only this page's
// fields ignoring case would let a field on this page whose name differs only in case
// (a label "age" beside the field "Age" on another page) be pulled into the group in
// place of the field the server named.
function resolveMember(index: FieldIndex, name: string): FieldDefinition | undefined {
  return index.exact.get(name) ?? index.ignoringCase.get(nameKey(name));
}

/**
 * The page's top-level items in `tabIndex` order. Each group holds the members it names
 * that are on this page, sorted by `tabIndex`, and those members render only inside it.
 * Groups claim members in `tabIndex` order, so a field named by two groups belongs to the
 * first. A member on another page, an unknown name and the group itself are skipped; a
 * group that is itself a member of an earlier group is nested, claims nothing and renders
 * empty, so its own members keep their place on the page. Every field of the page appears
 * exactly once: as an item or as a member of one.
 */
export function layoutPageItems(page: PageDefinition, definition: FormDefinition): LayoutItem[] {
  const ordered = [...page.fields].sort(byTabIndex);
  const onPage = new Set(ordered);
  const index = indexFormFields(page, ordered, definition);
  const claimed = new Set<FieldDefinition>();
  const membersOf = new Map<FieldDefinition, FieldDefinition[]>();

  for (const group of ordered) {
    if (group.fieldType !== 'group' || claimed.has(group)) {
      continue;
    }

    const members: FieldDefinition[] = [];
    for (const name of group.members ?? []) {
      const member = resolveMember(index, name);
      // membersOf holds the groups already laid out at the top level, which no later group may claim.
      if (
        member === undefined ||
        member === group ||
        !onPage.has(member) ||
        claimed.has(member) ||
        membersOf.has(member)
      ) {
        continue;
      }
      claimed.add(member);
      members.push(member);
    }
    membersOf.set(group, members.sort(byTabIndex));
  }

  return ordered
    .filter((field) => !claimed.has(field))
    .map((field) => ({ field, members: membersOf.get(field) ?? [] }));
}

/**
 * Renders a page as `.page-fields`: each item through `fieldComponents[field.fieldType]`,
 * a group with its members as children. Elements are keyed by field name only, never by
 * record, so controls stay mounted when the editor replaces the record.
 */
export function FieldRenderer({
  page,
  definition,
  values,
  fieldErrors,
  isProtected,
  isCheckCodeDisabled,
  onChange,
  onFocus,
  onBlur,
  onClick,
}: FieldRendererProps) {
  const items = useMemo(() => layoutPageItems(page, definition), [page, definition]);

  const renderField = (field: FieldDefinition, children?: ReactNode) => {
    // The contract closes `fieldType`; a type the client does not know still renders,
    // as the unsupported placeholder, instead of failing the whole page.
    const Component = fieldComponents[field.fieldType] ?? fieldComponents.unsupported;
    const name = field.name;
    // An unsupported field is never written, so it is protected whatever the editor
    // reports; UnsupportedPlaceholder relies on this for its `field--protected` wrapper.
    const fieldProtected = field.unsupported || isProtected(field);

    return (
      <Component
        key={name}
        field={field}
        value={isDataField(field) ? (ownEntry(values, name) ?? null) : null}
        isProtected={fieldProtected}
        disabled={fieldProtected || isCheckCodeDisabled(field)}
        errors={ownEntry(fieldErrors, name) ?? NO_ERRORS}
        onChange={(value: FieldValue) => {
          onChange(name, value);
        }}
        onFocus={
          onFocus === undefined
            ? undefined
            : () => {
                onFocus(name);
              }
        }
        onBlur={
          onBlur === undefined
            ? undefined
            : () => {
                onBlur(name);
              }
        }
        onClick={
          onClick === undefined
            ? undefined
            : () => {
                onClick(name);
              }
        }
      >
        {children}
      </Component>
    );
  };

  return (
    <div className="page-fields">
      {items.map((item) =>
        renderField(
          item.field,
          item.members.length === 0 ? undefined : item.members.map((member) => renderField(member)),
        ),
      )}
    </div>
  );
}
