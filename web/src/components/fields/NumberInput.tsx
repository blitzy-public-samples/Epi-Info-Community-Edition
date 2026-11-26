// Number control for fieldType `number`: a JSON number stored in a `tinyint`,
// `smallint`, `int`, `bigint` or `float` column (seeded: Age 0–120 with mask `###`,
// FeverTemp 80–110 with mask `###.#`, and the read-only OnsetWeek). It replaces the
// numeric MaskedTextBox/TextBox legacy ControlFactory builds; legacy Enter/Leave
// become onFocus/onBlur. Range, mask and required checks are not made here: they
// arrive as `errors` from client validation and the server.
import { useState } from 'react';
import { isExactDecimal, numberFromText, sameFieldValue } from '../../api/client';
import type { FieldValue } from '../../api/types';
import type { FieldComponentProps } from './fieldComponents';
import {
  fieldAriaProps,
  fieldControlId,
  fieldErrorId,
  fieldLabelText,
  fieldWrapperClass,
} from './fieldComponents';

/**
 * The value typed text stands for: blank is `null`, a finite number is that number,
 * and anything else is the raw text, so client validation can report that it is not
 * a number. `Infinity` and `NaN` are never emitted. An integer beyond
 * ±`Number.MAX_SAFE_INTEGER` that a `bigint` column can hold is its exact bigint, so
 * `9007199254740993` is saved as typed rather than as the double 9007199254740992, and a
 * fraction whose nearest double is whole is its exact decimal, so `9007199254740992.5`
 * is saved as typed rather than as the whole number 9007199254740992.
 */
function interpret(text: string): FieldValue {
  // Blank input is empty, never the raw text: Number('  ') is 0, and numberFromText
  // gives null for blank text as for any other text that is not a number.
  if (text.trim() === '') {
    return null;
  }
  return numberFromText(text) ?? text;
}

/** Text the control shows for a value; an empty value is `''`. */
function display(value: FieldValue): string {
  // `undefined` is outside FieldValue but can arrive from a missing `values` key at
  // run time; it is empty too, so "undefined" is never rendered.
  if (value === null || value === undefined) {
    return '';
  }
  if (typeof value === 'string') {
    return value;
  }
  // An exact decimal shows the number typed, as its JSON literal.
  if (isExactDecimal(value)) {
    return value.literal;
  }
  return String(value);
}

/**
 * A native text input labelled by the field's prompt, keeping the typed text as a
 * local draft so partial entries such as `37.` or `-` survive the round trip through
 * the editor. The draft is replaced only when `value` changes to something the draft
 * does not already stand for, such as a check-code `ASSIGN` or another record.
 */
export function NumberInput(props: FieldComponentProps) {
  const { field, value, disabled, errors, onChange, onFocus, onBlur } = props;
  const id = fieldControlId(field.name);
  const [draft, setDraft] = useState(() => display(value));

  // Adjust state when a prop changes, during render rather than in an effect, so the
  // replaced draft is never painted. sameFieldValue keeps NaN stable, tells -0 from 0,
  // compares bigints by value and exact decimals by literal, so a typed fraction such as
  // ` +9007199254740992.5` keeps its draft.
  const [lastValue, setLastValue] = useState<FieldValue>(value);
  if (!sameFieldValue(lastValue, value)) {
    setLastValue(value);
    if (!sameFieldValue(interpret(draft), value)) {
      setDraft(display(value));
    }
  }

  return (
    <div className={fieldWrapperClass(props)} data-field-name={field.name}>
      <label htmlFor={id}>{fieldLabelText(field)}</label>
      <input
        id={id}
        type="text"
        inputMode="decimal"
        value={draft}
        disabled={disabled}
        {...fieldAriaProps(props)}
        onChange={(e) => {
          const text = e.target.value;
          setDraft(text);
          onChange(interpret(text));
        }}
        onFocus={() => {
          onFocus?.();
        }}
        onBlur={() => {
          onBlur?.();
        }}
      />
      {errors.length > 0 && (
        <div className="field-error" id={fieldErrorId(field.name)}>
          {errors.map((message, index) => (
            <p key={`${index}-${message}`}>{message}</p>
          ))}
        </div>
      )}
    </div>
  );
}
