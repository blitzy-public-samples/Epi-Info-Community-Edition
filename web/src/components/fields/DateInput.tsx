// Date control for fieldType `date` (seeded `datetime` columns), the web counterpart
// of legacy's date text box. The metadata date mask (seeded `MM-DD-YYYY`) is the
// client's display and entry format: an enabled field whose pattern is a date mask is a
// text input that shows and reads the date through that mask, while the editor holds the
// wire value `YYYY-MM-DD`. A field with a date mask, enabled or disabled, states that
// mask in a visible "Format:" hint that describes its input, as a patterned text field
// does. An enabled field whose pattern is no date mask keeps the browser's date input,
// shown in the user's locale, and has no hint. Legacy Enter/Leave become
// onFocus/onBlur.
//
// A disabled field (protected, or disabled by check code) is rendered as a disabled,
// read-only text input instead of a disabled date input. Chromium exposes a date
// input as a `Date` node holding month, day and year spinbuttons; once disabled, that
// node is neither focusable nor a control role, so accessibility views that keep only
// such nodes drop it, and the spinbuttons are announced without the field's label. A
// text input is a textbox in every view and keeps its label, value and disabled state.
// Its text follows the field's date mask, falling back to the user's locale.
import { useState } from 'react';
import { isWireDate, sameFieldValue } from '../../api/client';
import type { FieldValue } from '../../api/types';
import { dateMaskOf, maskedDateText, wireDateFromMaskedText } from '../../validation/clientValidation';
import type { FieldComponentProps } from './fieldComponents';
import {
  fieldAriaProps,
  fieldControlId,
  fieldErrorId,
  fieldLabelText,
  fieldWrapperClass,
} from './fieldComponents';

// The user's locale, as the browser shows an enabled date input. UTC, because the
// date is built at UTC midnight, so no time-zone offset can move it to another day;
// Gregorian, the calendar the ISO value is written in, whatever the locale's default.
const localeDateFormat = new Intl.DateTimeFormat(undefined, {
  year: 'numeric',
  month: '2-digit',
  day: '2-digit',
  timeZone: 'UTC',
  calendar: 'gregory',
});

/**
 * A wire string as its UTC-midnight instant, or `null` when it is not a wire date
 * (isWireDate), so neither an impossible date such as 2011-02-30 nor year 0000, which
 * has no Gregorian rendering, nor `''`.
 */
function utcMidnight(iso: string): Date | null {
  if (!isWireDate(iso)) {
    return null;
  }
  // A wire date is exactly YYYY-MM-DD, so each part has its fixed place. setUTCFullYear
  // keeps a year below 100 as written; Date.UTC would map it to 19xx.
  const date = new Date(0);
  date.setUTCFullYear(Number(iso.slice(0, 4)), Number(iso.slice(5, 7)) - 1, Number(iso.slice(8, 10)));
  return date;
}

/**
 * Text the read-only rendering shows for a wire date: a valid ISO calendar date
 * through the field's date mask, or in the user's locale when the field has no date
 * mask, and any other string, `''` included, exactly as it is, so a value the browser
 * could not parse is never hidden or altered.
 */
function disabledDateText(iso: string, pattern: string | null | undefined): string {
  const mask = dateMaskOf(pattern);
  const masked = mask === null ? null : maskedDateText(iso, mask);
  if (masked !== null) {
    return masked;
  }
  const date = utcMidnight(iso);
  return date === null ? iso : localeDateFormat.format(date);
}

/**
 * The value text typed in a date mask stands for: blank is `null`, text that follows
 * the mask exactly and names a real day is its wire date, and any other text is the
 * raw text, so a partial entry keeps its text, a wire date typed as such is that date
 * (see DateInput), and client validation reports the rest.
 */
function interpret(text: string, mask: string): FieldValue {
  if (text.trim() === '') {
    return null;
  }
  return wireDateFromMaskedText(text, mask) ?? text;
}

/**
 * Text a masked control shows for a value: a wire date through the mask, any other
 * string as it is, and `''` for every other value, `null` and a missing `values` key
 * included, so "undefined" is never rendered.
 */
function display(value: FieldValue, mask: string): string {
  if (typeof value !== 'string') {
    return '';
  }
  return maskedDateText(value, mask) ?? value;
}

/**
 * Date field. Its value is the wire string `YYYY-MM-DD` (stored at 00:00:00), and
 * `null` when it is empty. Metadata `lower`/`upper` arrive as ISO strings; range and
 * required checks are the validator's, and their messages arrive in `errors`.
 *
 * While enabled with a date mask it is a text input that shows the value through the
 * mask (`MM-DD-YYYY` shows 2010-02-06 as `02-06-2010`) and has the mask as its
 * placeholder, as legacy showed the short-date pattern in an empty box. The typed text
 * is kept as a local draft, so a partial entry is never rewritten while the user types.
 * Blank text emits `null`; text that follows the mask exactly and names a real day
 * emits that wire date, never with a time; any other text emits the text as typed.
 * Text that is itself a wire date, typed as `YYYY-MM-DD`, is therefore that date, by
 * design: it is the field's own value, which none of Epi Info's date masks
 * (`MM-DD-YYYY`, `DD-MM-YYYY`, `YYYY-MM-DD`) can misread, because its four-digit year
 * comes first; and legacy's `control_Leave` read it too, since `DateTime.TryParse` reads
 * an ISO date in every Gregorian-calendar culture, then rewrote it as its display date
 * (Epi.Windows.Enter/Forms/Canvas.control.events.cs:564-583). Every other text stays
 * raw, for client validation to report in the mask's terms. When the control loses
 * focus, a draft that stands for the field's value, a typed wire date included, shows
 * it through the mask, and raw text stays as typed. The draft is replaced when `value`
 * changes to something it does not stand for, such as a check-code `ASSIGN` or another
 * record, and when the mask changes.
 *
 * While enabled with no date mask it is a native date input whose value is the wire
 * string, shown in the user's locale, with `min`/`max` from the ISO bounds guiding its
 * picker.
 *
 * While disabled it is a read-only text input with the same id, label and ARIA
 * state, showing the date through the date mask, or in the user's locale when the
 * field has no date mask. That text is display only, and the control never emits a
 * value.
 *
 * A field with a date mask, enabled or disabled, shows a "Format:" hint naming the mask
 * after its input, and the input's `aria-describedby` names that hint ahead of any
 * errors, so the format stays stated once text replaces the placeholder and is
 * announced with the field. A field with no date mask has no hint.
 */
export function DateInput(props: FieldComponentProps) {
  const { field, value, disabled, errors, onChange, onFocus, onBlur } = props;
  const id = fieldControlId(field.name);
  // Numeric bounds belong to number fields and are ignored here; only the native date
  // input has a picker for the bounds to guide.
  const { lower, upper, pattern } = field.validation;
  const mask = dateMaskOf(pattern);
  const hintId = mask !== null ? `${id}-hint` : undefined;
  // Any non-string value, null included, is shown empty; the native date input shows
  // the string as it is.
  const iso = typeof value === 'string' ? value : '';
  const [draft, setDraft] = useState(() => (mask === null ? '' : display(value, mask)));

  // Adjust state when a prop changes, during render rather than in an effect, so the
  // replaced draft is never painted. Raw text displays as itself, so a new mask keeps a
  // partial entry as typed and shows a date in its own format.
  const [synced, setSynced] = useState<{ readonly value: FieldValue; readonly mask: string | null }>({
    value,
    mask,
  });
  if (!sameFieldValue(synced.value, value) || synced.mask !== mask) {
    setSynced({ value, mask });
    if (mask !== null && (synced.mask !== mask || !sameFieldValue(interpret(draft, mask), value))) {
      setDraft(display(value, mask));
    }
  }

  const shown = disabled ? disabledDateText(iso, pattern) : mask === null ? iso : draft;

  return (
    <div className={fieldWrapperClass(props)} data-field-name={field.name}>
      <label htmlFor={id}>{fieldLabelText(field)}</label>
      <input
        id={id}
        type={disabled || mask !== null ? 'text' : 'date'}
        value={shown}
        placeholder={!disabled && mask !== null ? mask : undefined}
        min={!disabled && mask === null && typeof lower === 'string' ? lower : undefined}
        max={!disabled && mask === null && typeof upper === 'string' ? upper : undefined}
        readOnly={disabled}
        disabled={disabled}
        {...fieldAriaProps(props, hintId)}
        onChange={(e) => {
          // A disabled control takes no input, and its display text is never a wire value.
          if (disabled) {
            return;
          }
          const text = e.target.value;
          if (mask !== null) {
            setDraft(text);
            onChange(interpret(text, mask));
            return;
          }
          // Blank (a cleared or incomplete entry) emits null; any other value passes unchanged,
          // a year beyond four digits included, for the API's four-digit YYYY-MM-DD check
          // (mirrored for feedback by clientValidation.ts) to report.
          onChange(text === '' ? null : text);
        }}
        onFocus={() => {
          onFocus?.();
        }}
        onBlur={() => {
          // Only a draft that stands for the current value is redisplayed, so a value the
          // editor has not taken yet keeps its text. Raw text displays as itself.
          if (mask !== null && sameFieldValue(interpret(draft, mask), value)) {
            setDraft(display(value, mask));
          }
          onBlur?.();
        }}
      />
      {mask !== null && <small id={hintId}>Format: {mask}</small>}
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
