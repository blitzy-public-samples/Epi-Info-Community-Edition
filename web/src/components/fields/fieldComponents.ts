// The one place a field type picks its React component, and the props contract and
// helpers every component in this folder shares. FieldRenderer renders each field
// through `fieldComponents[field.fieldType]`; no other file switches on `fieldType`
// to choose a component. The server has already mapped every metadata type to a
// `fieldType` (Phone Number and Comment Legal to `text`, Yes/No to `unsupported`),
// so nothing here matches type names.
import { useEffect, useRef, useState } from 'react';
import type { ComponentType, ReactNode, RefObject } from 'react';
import { isExactDecimal } from '../../api/client';
import type { FieldDefinition, FieldType, FieldValue } from '../../api/types';
import { clientMessages } from '../../validation/clientValidation';
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
  /** `isProtected`, disabled by check code, or a frozen draft; set as the native `disabled` attribute. */
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
 * `${fieldControlId(name)}-<role>` (the error list, a hint, a length-limit notice, a
 * radio) can never equal another field's control id or a different role's id. Every id
 * derived for a field follows that form.
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
 * `field--disabled` for a field disabled by check code only or by a frozen draft.
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
  if (typeof value === 'string') {
    return value;
  }
  // An exact decimal is the number typed, shown as its JSON literal.
  return isExactDecimal(value) ? value.literal : String(value);
}

/** What useLengthLimitNotice gives a text control: the ref to attach, and the notice to show beside it. */
export interface LengthLimitNotice<T extends HTMLInputElement | HTMLTextAreaElement> {
  /** Attached to the native control whose `maxLength` attribute is the limit. */
  readonly ref: RefObject<T>;
  /** The client's `maxLength` message while the control's last entry was cut to the limit; otherwise `null`. */
  readonly notice: string | null;
}

/**
 * Tells the user when a text control's native `maxLength` cut what they typed or pasted.
 * The limit stays the native attribute, as legacy sets the text box's MaxLength, and the
 * browser drops whatever passes it without a word. A native `beforeinput` listener (React's
 * onBeforeInput is a polyfill that carries no `inputType`) works out the length each
 * insertion would reach: the value's length, less the selection it replaces, plus the text
 * inserted as the browser stores it. Past `maxLength` the notice is the client's
 * `maxLength` message; an insertion that fits, any other edit, and blur clear it. It is
 * feedback only: no error, no `aria-invalid`, no change to the draft. Nothing is listened
 * to while `maxLength` is `undefined` or the control is disabled.
 */
export function useLengthLimitNotice<T extends HTMLInputElement | HTMLTextAreaElement>(
  maxLength: number | undefined,
  disabled: boolean,
): LengthLimitNotice<T> {
  const ref = useRef<T>(null);
  const [cut, setCut] = useState(false);

  useEffect(() => {
    const control = ref.current;
    if (control === null || maxLength === undefined || disabled) {
      return undefined;
    }
    const limit = maxLength;
    const onBeforeInput = (event: Event): void => {
      if (!(event instanceof InputEvent)) {
        return;
      }
      if (!event.inputType.startsWith('insert')) {
        // Any other edit, a deletion, undo or redo, supersedes the entry that was cut.
        setCut(false);
        return;
      }
      // While an IME composes, the selection spans the uncommitted text the update replaces.
      const selected = (control.selectionEnd ?? 0) - (control.selectionStart ?? 0);
      setCut(control.value.length - selected + insertedLength(control, event) > limit);
    };
    const onBlur = (): void => {
      setCut(false);
    };
    control.addEventListener('beforeinput', onBeforeInput);
    control.addEventListener('blur', onBlur);
    return () => {
      control.removeEventListener('beforeinput', onBeforeInput);
      control.removeEventListener('blur', onBlur);
      // A cut seen before the control was disabled or its limit changed no longer applies.
      setCut(false);
    };
  }, [maxLength, disabled]);

  return { ref, notice: cut && maxLength !== undefined && !disabled ? clientMessages.maxLength(maxLength) : null };
}

/**
 * Length of the text an `insert…` input event adds to `control`, as Chromium stores it. A
 * line break typed is one character. Typed, pasted or dropped text counts each CRLF, CR or
 * LF once, and a single-line input drops its trailing line breaks and turns each other one
 * into a space, so a cell copied from a spreadsheet with its line break still fits.
 */
function insertedLength(control: HTMLInputElement | HTMLTextAreaElement, event: InputEvent): number {
  if (event.inputType === 'insertLineBreak' || event.inputType === 'insertParagraph') {
    return 1;
  }
  // A plain-text control's inserted text, a paste's included, is `data`; `dataTransfer` carries it when `data` is null.
  const text = (event.data ?? event.dataTransfer?.getData('text/plain') ?? '').replace(/\r\n?/g, '\n');
  return (control instanceof HTMLInputElement ? text.replace(/\n+$/, '') : text).length;
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
