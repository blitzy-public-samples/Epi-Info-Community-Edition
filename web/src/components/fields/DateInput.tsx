// Date control for fieldType `date` (seeded `datetime` columns), the web counterpart
// of legacy's DateTimePicker and masked date box. Entry uses the browser's date input,
// so the metadata date mask (seeded `MM-DD-YYYY`) is not an entry mask; it formats the
// disabled display. Legacy Enter/Leave become onFocus/onBlur.
//
// A disabled field (protected, or disabled by check code) is rendered as a disabled,
// read-only text input instead of a disabled date input. Chromium exposes a date
// input as a `Date` node holding month, day and year spinbuttons; once disabled, that
// node is neither focusable nor a control role, so accessibility views that keep only
// such nodes drop it, and the spinbuttons are announced without the field's label. A
// text input is a textbox in every view and keeps its label, value and disabled state.
// Its text follows the field's date mask, falling back to the user's locale.
import { isWireDate } from '../../api/client';
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

/** The date-mask tokens, as Epi Info writes them (`MM-DD-YYYY`, `DD-MM-YYYY`, `YYYY-MM-DD`), in any case. */
const DATE_MASK_TOKEN = /YYYY|MM|DD/gi;

/** A valid wire date: its digits as written, keyed by mask token, and its UTC-midnight instant. */
interface CalendarDate {
  readonly parts: Readonly<Record<string, string>>;
  readonly date: Date;
}

/**
 * A wire string as a calendar date, or `null` when it is not a wire date
 * (isWireDate), so neither an impossible date such as 2011-02-30 nor year 0000, which
 * has no Gregorian rendering, nor `''`.
 */
function calendarDate(iso: string): CalendarDate | null {
  if (!isWireDate(iso)) {
    return null;
  }
  // A wire date is exactly YYYY-MM-DD, so each part has its fixed place.
  const year = iso.slice(0, 4);
  const month = iso.slice(5, 7);
  const day = iso.slice(8, 10);
  // setUTCFullYear keeps a year below 100 as written; Date.UTC would map it to 19xx.
  const date = new Date(0);
  date.setUTCFullYear(Number(year), Number(month) - 1, Number(day));
  return { parts: { YYYY: year, MM: month, DD: day }, date };
}

/**
 * The date written through a metadata date mask: each `YYYY`, `MM` and `DD` token,
 * in any case, becomes the four-digit year, two-digit month and two-digit day, and
 * every other character stays as written. `null` unless the pattern holds each token
 * exactly once, so a partial mask such as `MM-DD` and a date/time mask, whose `MM`
 * also stands for minutes, are not date masks.
 */
function maskedDateText({ parts }: CalendarDate, pattern: string): string | null {
  const tokens = (pattern.match(DATE_MASK_TOKEN) ?? []).map((token) => token.toUpperCase()).sort();
  if (tokens.join(' ') !== 'DD MM YYYY') {
    return null;
  }
  // A replacer function's result is inserted as is, so no `$` in a mask is special.
  return pattern.replace(DATE_MASK_TOKEN, (token) => parts[token.toUpperCase()]);
}

/**
 * Text the read-only rendering shows for a wire date: a valid ISO calendar date
 * through the field's date mask, or in the user's locale when the field has no date
 * mask, and any other string, `''` included, exactly as it is, so a value the browser
 * could not parse is never hidden or altered.
 */
function disabledDateText(iso: string, pattern: string | null | undefined): string {
  const calendar = calendarDate(iso);
  if (calendar === null) {
    return iso;
  }
  const masked = typeof pattern === 'string' ? maskedDateText(calendar, pattern) : null;
  return masked ?? localeDateFormat.format(calendar.date);
}

/**
 * Date field: a native date input whose value is the wire string `YYYY-MM-DD`
 * (stored at 00:00:00). Metadata `lower`/`upper` arrive as ISO strings and only
 * guide the picker through `min`/`max`; range and required checks are the
 * validator's, and their messages arrive in `errors`. While enabled, the browser
 * renders the date in the user's locale.
 *
 * While disabled it is a read-only text input with the same id, label and ARIA
 * state, showing the date through the metadata date mask (`MM-DD-YYYY` shows
 * 2010-02-06 as `02-06-2010`), or in the user's locale when the field has no date
 * mask. That text is display only: the editor keeps the ISO string, and the
 * control never emits a value.
 */
export function DateInput(props: FieldComponentProps) {
  const { field, value, disabled, errors, onChange, onFocus, onBlur } = props;
  const id = fieldControlId(field.name);
  // Any non-string value, null included, is shown empty; an enabled control shows the
  // string as it is.
  const iso = typeof value === 'string' ? value : '';
  // Numeric bounds belong to number fields and are ignored here; a disabled control
  // takes no entry, so it has no bounds to guide.
  const { lower, upper } = field.validation;

  return (
    <div className={fieldWrapperClass(props)} data-field-name={field.name}>
      <label htmlFor={id}>{fieldLabelText(field)}</label>
      <input
        id={id}
        type={disabled ? 'text' : 'date'}
        value={disabled ? disabledDateText(iso, field.validation.pattern) : iso}
        min={!disabled && typeof lower === 'string' ? lower : undefined}
        max={!disabled && typeof upper === 'string' ? upper : undefined}
        readOnly={disabled}
        disabled={disabled}
        {...fieldAriaProps(props)}
        onChange={(e) => {
          // A disabled control takes no input, and its display text is never a wire value.
          if (disabled) {
            return;
          }
          // Blank (a cleared or incomplete entry) emits null; any other value passes unchanged,
          // a year beyond four digits included, for the API's four-digit YYYY-MM-DD check
          // (mirrored for feedback by clientValidation.ts) to report.
          onChange(e.target.value === '' ? null : e.target.value);
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
