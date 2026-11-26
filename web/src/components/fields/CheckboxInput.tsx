// Checkbox control for fieldType `checkbox`: a JSON boolean stored in a `bit` column.
// It replaces the legacy CheckBoxField control and its Click, Enter and Leave
// handlers. `null` and `false` both render unchecked, with no indeterminate state;
// whether an unchecked box leaves a record empty is decided by the editor, so a
// toggle always emits a boolean.
import { useRef } from 'react';
import type { FieldComponentProps } from './fieldComponents';
import {
  fieldAriaProps,
  fieldControlId,
  fieldErrorId,
  fieldLabelText,
  fieldWrapperClass,
} from './fieldComponents';

/**
 * A native checkbox followed by its label; raises `onClick` after the value toggles.
 * Focus and blur are raised only as focus enters and leaves the box: a click on the
 * label of the box that already has focus keeps focus on it, so that click raises
 * `onClick` alone.
 */
export function CheckboxInput(props: FieldComponentProps) {
  const { field, value, disabled, errors, onChange, onFocus, onBlur, onClick } = props;
  const id = fieldControlId(field.name);
  const inputRef = useRef<HTMLInputElement>(null);

  return (
    <div className={fieldWrapperClass(props)} data-field-name={field.name}>
      <input
        ref={inputRef}
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
      <label
        htmlFor={id}
        onMouseDown={(e) => {
          // A mousedown on a label moves focus to the body before the label's click
          // focuses the box again, so a click on the label of the focused box would
          // raise a spurious blur/focus pair around its toggle. While this box has
          // focus it keeps it; the click still toggles the box through the label,
          // firing change and then `onClick`. When another element has focus the
          // mousedown is left alone, so focus moves to this box as usual.
          if (inputRef.current !== null && inputRef.current === document.activeElement) {
            e.preventDefault();
          }
        }}
      >
        {fieldLabelText(field)}
      </label>
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
