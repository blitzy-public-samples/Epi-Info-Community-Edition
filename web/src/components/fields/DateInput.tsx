// Date control for fieldType `date` (seeded `datetime` columns), the web counterpart
// of legacy's DateTimePicker and masked date box; the legacy `MM-DD-YYYY` entry mask
// is not reproduced. Legacy Enter/Leave become onFocus/onBlur.
import type { FieldComponentProps } from './fieldComponents';
import {
  fieldAriaProps,
  fieldControlId,
  fieldErrorId,
  fieldLabelText,
  fieldWrapperClass,
} from './fieldComponents';

/**
 * Date field: a native date input whose value is the wire string `YYYY-MM-DD`
 * (stored at 00:00:00). Metadata `lower`/`upper` arrive as ISO strings and only
 * guide the picker through `min`/`max`; range and required checks are the
 * validator's, and their messages arrive in `errors`. The metadata display
 * pattern is not shown: the browser renders the date in the user's locale.
 */
export function DateInput(props: FieldComponentProps) {
  const { field, value, disabled, errors, onChange, onFocus, onBlur } = props;
  const id = fieldControlId(field.name);
  // Any non-string value, null included, is shown empty; the string is never reformatted.
  const shown = typeof value === 'string' ? value : '';
  // Numeric bounds belong to number fields and are ignored here.
  const { lower, upper } = field.validation;

  return (
    <div className={fieldWrapperClass(props)} data-field-name={field.name}>
      <label htmlFor={id}>{fieldLabelText(field)}</label>
      <input
        id={id}
        type="date"
        value={shown}
        min={typeof lower === 'string' ? lower : undefined}
        max={typeof upper === 'string' ? upper : undefined}
        disabled={disabled}
        {...fieldAriaProps(props)}
        onChange={(e) => {
          // The browser yields '' for a cleared or incomplete entry, else YYYY-MM-DD.
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
