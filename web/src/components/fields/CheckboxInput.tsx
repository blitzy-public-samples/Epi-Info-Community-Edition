// Checkbox control for fieldType `checkbox`: a JSON boolean stored in a `bit` column.
// It replaces the legacy CheckBoxField control and its Click, Enter and Leave
// handlers. `null` and `false` both render unchecked, with no indeterminate state;
// whether an unchecked box leaves a record empty is decided by the editor, so a
// toggle always emits a boolean.
import type { FieldComponentProps } from './fieldComponents';
import {
  fieldAriaProps,
  fieldControlId,
  fieldErrorId,
  fieldLabelText,
  fieldWrapperClass,
} from './fieldComponents';

/** A native checkbox followed by its label; raises `onClick` after the value toggles. */
export function CheckboxInput(props: FieldComponentProps) {
  const { field, value, disabled, errors, onChange, onFocus, onBlur, onClick } = props;
  const id = fieldControlId(field.name);

  return (
    <div className={fieldWrapperClass(props)} data-field-name={field.name}>
      <input
        id={id}
        type="checkbox"
        checked={value === true}
        disabled={disabled}
        {...fieldAriaProps(props)}
        onChange={(e) => {
          // A user cannot toggle a disabled box, but a scripted click event still
          // toggles it and fires change; a protected or check-code-disabled field
          // must emit neither a value nor FieldClick.
          if (disabled) {
            return;
          }
          // The new value first, then FieldClick, so check code sees the post-click value.
          onChange(e.target.checked);
          onClick?.();
        }}
        onFocus={() => {
          onFocus?.();
        }}
        onBlur={() => {
          onBlur?.();
        }}
      />
      <label htmlFor={id}>{fieldLabelText(field)}</label>
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
