// The one place a field type picks its React component, and the props contract and
// helpers every component in this folder shares. FieldRenderer renders each field
// through `fieldComponents[field.fieldType]`; no other file switches on `fieldType`
// to choose a component. The server has already mapped every metadata type to a
// `fieldType` (Phone Number and Comment Legal to `text`, Yes/No to `unsupported`),
// so nothing here matches type names.
import type { ComponentType, ReactNode } from 'react';
import type { FieldDefinition, FieldType, FieldValue } from '../../api/types';
import { CheckboxInput } from './CheckboxInput';
import { DateInput } from './DateInput';
import { GroupSection } from './GroupSection';
import { LabelHeading } from './LabelHeading';
import { LegalValuesSelect } from './LegalValuesSelect';
import { MultilineInput } from './MultilineInput';
import { NumberInput } from './NumberInput';
import { OptionGroup } from './OptionGroup';
import { TextInput } from './TextInput';
import { UnsupportedPlaceholder } from './UnsupportedPlaceholder';

/** Props FieldRenderer passes to every field component. */
export interface FieldComponentProps {
  field: FieldDefinition;
  /** The field's current value; `null` is empty. */
  value: FieldValue;
  /** `isProtected` or disabled by check code; set as the native `disabled` attribute. */
  disabled: boolean;
  /** Read-only metadata, a deleted record, or an unsupported field; `ENABLE` never clears it. */
  isProtected: boolean;
  /** Client feedback and server `errors` for this field, already resolved by the editor. */
  errors: readonly string[];
  onChange: (value: FieldValue) => void;
  /** Raises `FieldBefore` when the control gains focus. */
  onFocus?: () => void;
  /** Raises `FieldAfter` when the control loses focus. */
  onBlur?: () => void;
  /** Raises `FieldClick` on a checkbox or radio, after the value toggles. */
  onClick?: () => void;
  /**
   * LabelHeading and GroupSection only: the level of the field's heading. 3 at the page's
   * top level, and 4 inside a group, whose own heading is level 3; defaults to 3.
   */
  headingLevel?: 3 | 4;
  /** GroupSection only: its members, already rendered by FieldRenderer. */
  children?: ReactNode;
}

/** ARIA attributes spread onto a native `input`, `textarea` or `select`, or an option group's radiogroup fieldset; absent keys are omitted, never `undefined`. */
export type FieldAriaProps = {
  'aria-invalid'?: true;
  'aria-describedby'?: string;
  'aria-required'?: true;
};

/**
 * DOM id of a field's control: `field-` followed by the encoded name. Deterministic, so
 * labels, validation jumps, radio group names and tests can address it.
 *
 * The encoding keeps ASCII letters and digits and writes every other UTF-16 code unit,
 * `_` and `-` included, as `_` plus its four lowercase hex digits. It is injective, so
 * distinct names never share an id, and its output holds only letters, digits and `_`,
 * so the id is safe in the DOM and in a CSS selector whatever the metadata name holds.
 *
 * Because the encoded name never contains `-`, an id derived from a control's id as
 * `${fieldControlId(name)}-<role>` (the error list, a hint, a radio) can never equal
 * another field's control id or a different role's id. Every id derived for a field
 * follows that form.
 */
export function fieldControlId(name: string): string {
  return 'field-' + name.replace(/[^A-Za-z0-9]/g, (unit) => '_' + unit.charCodeAt(0).toString(16).padStart(4, '0'));
}

/** DOM id of the element listing a field's error messages: the control's id with the role `-error`. */
export function fieldErrorId(name: string): string {
  return `${fieldControlId(name)}-error`;
}

/** The field's prompt exactly as metadata holds it, or its name when the prompt is missing or blank. */
export function fieldLabelText(field: FieldDefinition): string {
  const prompt = field.prompt;
  return typeof prompt === 'string' && prompt.trim() !== '' ? prompt : field.name;
}

/**
 * Class list of a field's wrapper: `field--protected` for a protected field, and
 * `field--disabled` for a field disabled by check code only.
 */
export function fieldWrapperClass(props: Pick<FieldComponentProps, 'disabled' | 'isProtected'>): string {
  if (props.isProtected) {
    return 'field field--protected';
  }
  return props.disabled ? 'field field--disabled' : 'field';
}

/**
 * ARIA state of a field's control. `aria-describedby` names `extraDescribedBy` (a
 * hint rendered by the component) and the error list only when errors are rendered,
 * so it never points at a missing element.
 */
export function fieldAriaProps(
  props: Pick<FieldComponentProps, 'field' | 'errors'>,
  extraDescribedBy?: string,
): FieldAriaProps {
  const aria: FieldAriaProps = {};
  const describedBy: string[] = [];
  if (extraDescribedBy !== undefined && extraDescribedBy.trim() !== '') {
    describedBy.push(extraDescribedBy);
  }
  if (props.errors.length > 0) {
    aria['aria-invalid'] = true;
    describedBy.push(fieldErrorId(props.field.name));
  }
  if (describedBy.length > 0) {
    aria['aria-describedby'] = describedBy.join(' ');
  }
  if (props.field.validation.required) {
    aria['aria-required'] = true;
  }
  return aria;
}

/** Text a controlled control displays for a value; an empty value is `''`. */
export function fieldTextValue(value: FieldValue): string {
  // `undefined` is outside FieldValue but can arrive from a missing `values` key at
  // run time; it is empty too, so "undefined" is never rendered.
  if (value === null || value === undefined) {
    return '';
  }
  return typeof value === 'string' ? value : String(value);
}

// Import cycle: the components import helpers from here, so every helper and component stays a hoisted `function` declaration and nothing reads this map at module evaluation.
// Each entry is a getter because Vitest's Vite 5 SSR transform defines a module's exports only after its imports have run: when a component module loads before this one, its export is still unbound while this module evaluates, and a plain `group: GroupSection` would store `undefined`.
export const fieldComponents: Readonly<Record<FieldType, ComponentType<FieldComponentProps>>> = {
  get label() {
    return LabelHeading;
  },
  get group() {
    return GroupSection;
  },
  get text() {
    return TextInput;
  },
  get multiline() {
    return MultilineInput;
  },
  get number() {
    return NumberInput;
  },
  get date() {
    return DateInput;
  },
  get checkbox() {
    return CheckboxInput;
  },
  get option() {
    return OptionGroup;
  },
  get legalValues() {
    return LegalValuesSelect;
  },
  get unsupported() {
    return UnsupportedPlaceholder;
  },
};
