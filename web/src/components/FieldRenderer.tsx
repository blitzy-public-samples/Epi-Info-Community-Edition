// Lays out one page of a form and renders each field through the field component map.
// It replaces the legacy Canvas and ControlFactory.GetPageControls, which placed every
// control at its designer coordinates: here the page is a flow layout in `tabIndex`
// order, and a group is a section holding its members. Render-only: every value, error
// and state comes from props, and no check-code or validation rule is decided here.
import { memo, useMemo, type ComponentType } from 'react';
import type { FieldDefinition, FieldValue, FormDefinition, PageDefinition, Values } from '../api/types';
import { isDataField, type FieldErrors } from '../validation/clientValidation';
import { fieldComponents, type FieldComponentProps } from './fields/fieldComponents';

/**
 * Every control is memoized on its own state and on `onChange`, `onFocus`, `onBlur` and
 * `onClick`, so those four should keep their identity across renders, as the editor's
 * actions do. A new identity re-renders every control of the page; behaviour is unaffected.
 * `isProtected` and `isCheckCodeDisabled` are only called while rendering, so their
 * identity does not matter.
 */
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

/** What one control renders from, resolved by FieldRenderer on each render. */
interface FieldItemState {
  /** The React key of the control among its siblings. */
  readonly key: string;
  readonly field: FieldDefinition;
  readonly value: FieldValue;
  readonly isProtected: boolean;
  readonly disabled: boolean;
  readonly errors: readonly string[];
  /** 3 for an item at the page's top level, 4 for a member under its group's level-3 heading. */
  readonly headingLevel: 3 | 4;
}

// Shared empty list, so a field that is not a group receives the same members on every render.
const NO_MEMBERS: readonly FieldItemState[] = [];

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

// The contract closes `fieldType`, but a malformed response can carry any string, and a
// member every object inherits (`constructor`, `toString`, `__proto__`) is no component.
// Only the registry's own entries count.
function hasOwnComponent(field: FieldDefinition): boolean {
  return Object.hasOwn(fieldComponents, field.fieldType);
}

// A type without its own registry entry still renders, as the unsupported placeholder,
// instead of failing the whole page.
function componentFor(field: FieldDefinition): ComponentType<FieldComponentProps> {
  return hasOwnComponent(field) ? fieldComponents[field.fieldType] : fieldComponents.unsupported;
}

interface FieldIndex {
  exact: Map<string, FieldDefinition>;
  ignoringCase: Map<string, FieldDefinition>;
}

function byPosition(a: PageDefinition, b: PageDefinition): number {
  return a.position - b.position || a.pageId - b.pageId;
}

// The unsupported rows whose exact name a data field earlier in definition order already
// holds. Pages are walked by position and then pageId, and each page's fields in the order
// delivered, never re-sorted, since that order carries the server's FieldId tie-break.
function repeatRowsOf(definition: FormDefinition): Set<FieldDefinition> {
  const heldNames = new Set<string>();
  const repeats = new Set<FieldDefinition>();
  for (const page of [...definition.pages].sort(byPosition)) {
    for (const field of page.fields) {
      if (isDataField(field)) {
        heldNames.add(field.name);
      } else if (field.unsupported && heldNames.has(field.name)) {
        repeats.add(field);
      }
    }
  }
  return repeats;
}

// Indexes every field of the form, this page's own field objects first, so a member that
// resolves to a field on this page resolves to the object the page renders.
//
// Only a repeat row is left out of both indexes. The server gives each exact name to the
// first value-holding row in storage order (pages by Position then PageId, fields by
// TabIndex then FieldId), lists any later value-holding row of that name as an unsupported
// placeholder, and never resolves a `metaFields.List` entry to it, so the name resolves to
// the data field even when the repeat is on this page and the data field on another. Every
// other row, a genuinely unsupported one (a Grid or Yes/No field) included, stays a member
// candidate. The definition arrives in storage order, so a repeat always follows the data
// field holding its name, and an unsupported row listed before that data field is never a
// repeat. One listed after it is indistinguishable from a repeat on the wire, because the
// Field DTO carries no FieldId, and is treated as one. The server would resolve to it only
// when its FieldId is lower than the data field's despite a later TabIndex or page, which
// Epi Info's unique field names per view make possible only in corrupt metadata.
function indexFormFields(page: PageDefinition, pageFields: readonly FieldDefinition[], definition: FormDefinition): FieldIndex {
  const repeatRows = repeatRowsOf(definition);

  const index: FieldIndex = { exact: new Map(), ignoringCase: new Map() };
  const add = (field: FieldDefinition) => {
    if (repeatRows.has(field)) {
      return;
    }
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

interface FieldItemProps extends Pick<FieldRendererProps, 'onChange' | 'onFocus' | 'onBlur' | 'onClick'> {
  readonly item: FieldItemState;
  /** A group's members in `tabIndex` order; {@link NO_MEMBERS} for any other field. */
  readonly members: readonly FieldItemState[];
}

function sameMessages(a: readonly string[], b: readonly string[]): boolean {
  return a === b || (a.length === b.length && a.every((message, index) => message === b[index]));
}

function sameItemState(a: FieldItemState, b: FieldItemState): boolean {
  return (
    Object.is(a.key, b.key) &&
    Object.is(a.field, b.field) &&
    Object.is(a.value, b.value) &&
    Object.is(a.isProtected, b.isProtected) &&
    Object.is(a.disabled, b.disabled) &&
    Object.is(a.headingLevel, b.headingLevel) &&
    sameMessages(a.errors, b.errors)
  );
}

function sameMembers(a: readonly FieldItemState[], b: readonly FieldItemState[]): boolean {
  return a === b || (a.length === b.length && a.every((member, index) => sameItemState(member, b[index])));
}

// FieldRenderer rebuilds every state object on each render, so FieldItem compares their
// contents: a control renders again only when its own state changes, and a group also
// when one of its members' does. The callbacks are compared by identity.
function sameFieldItemProps(previous: FieldItemProps, next: FieldItemProps): boolean {
  return (
    sameItemState(previous.item, next.item) &&
    sameMembers(previous.members, next.members) &&
    previous.onChange === next.onChange &&
    previous.onFocus === next.onFocus &&
    previous.onBlur === next.onBlur &&
    previous.onClick === next.onClick
  );
}

// One field's component, with the callbacks bound to the field's name. A group renders
// its members as children, each its own FieldItem, so inside a re-rendered group only
// the member whose state changed renders again.
function FieldItemView({ item, members, onChange, onFocus, onBlur, onClick }: FieldItemProps) {
  const { field } = item;
  const name = field.name;
  const Component = componentFor(field);

  return (
    <Component
      field={field}
      value={item.value}
      isProtected={item.isProtected}
      disabled={item.disabled}
      errors={item.errors}
      headingLevel={item.headingLevel}
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
      {members.length === 0
        ? undefined
        : members.map((member) => (
            <FieldItem
              key={member.key}
              item={member}
              members={NO_MEMBERS}
              onChange={onChange}
              onFocus={onFocus}
              onBlur={onBlur}
              onClick={onClick}
            />
          ))}
    </Component>
  );
}

const FieldItem = memo(FieldItemView, sameFieldItemProps);

// A field's row on its page: its index in `page.fields`, which the server lists in TabIndex
// and then FieldId order, with its name. Names alone may repeat on a page.
function rowKeysOf(page: PageDefinition): Map<FieldDefinition, string> {
  return new Map(page.fields.map((field, index): [FieldDefinition, string] => [field, `${index}:${field.name}`]));
}

/**
 * Renders a page as `.page-fields`: each item through its `fieldComponents` entry, a
 * group with its members as children. Each control is memoized on its own state, so an
 * edit renders again only the edited control and the group holding it, although every
 * page stays mounted. Elements are keyed by the field's row on its page, never by record,
 * so controls stay mounted when the editor replaces the record.
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
  const rowKeys = useMemo(() => rowKeysOf(page), [page]);

  const stateOf = (field: FieldDefinition, headingLevel: 3 | 4): FieldItemState => {
    const name = field.name;
    const dataField = isDataField(field);
    // An unsupported field, and a field whose type has no registry entry and so renders as
    // the placeholder, is never written, so it is protected whatever the editor reports;
    // UnsupportedPlaceholder relies on this for its `field--protected` wrapper.
    const fieldProtected = field.unsupported || !hasOwnComponent(field) || isProtected(field);
    return {
      // The layout holds only this page's own field objects, so every field has a row key.
      key: rowKeys.get(field) ?? name,
      field,
      // Only a data field has a `values` entry or messages; any other field sharing its
      // name renders no value and no message of it.
      value: dataField ? (ownEntry(values, name) ?? null) : null,
      isProtected: fieldProtected,
      disabled: fieldProtected || isCheckCodeDisabled(field),
      errors: dataField ? (ownEntry(fieldErrors, name) ?? NO_ERRORS) : NO_ERRORS,
      headingLevel,
    };
  };

  // The form's name is an h2, so a top-level item's heading is an h3 and a member's, under
  // its group's h3, an h4.
  return (
    <div className="page-fields">
      {items.map((layoutItem) => {
        const item = stateOf(layoutItem.field, 3);
        return (
          <FieldItem
            key={item.key}
            item={item}
            members={
              layoutItem.members.length === 0 ? NO_MEMBERS : layoutItem.members.map((member) => stateOf(member, 4))
            }
            onChange={onChange}
            onFocus={onFocus}
            onBlur={onBlur}
            onClick={onClick}
          />
        );
      })}
    </div>
  );
}
