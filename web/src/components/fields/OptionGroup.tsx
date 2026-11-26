// Option field: a native radio group whose fieldset is named by its legend, the
// prompt. The value is the 0-based index of the chosen item, as legacy stores it;
// an empty or out-of-range value checks no radio, and there is no "clear" radio.
import { Fragment } from 'react';
import type { FieldComponentProps } from './fieldComponents';
import { fieldControlId, fieldErrorId, fieldLabelText, fieldWrapperClass } from './fieldComponents';

/**
 * One radio per option, each followed by its own label, inside a fieldset whose
 * native `disabled` disables every radio; each radio also carries `disabled`, so
 * its own `disabled` property agrees with its `:disabled` state. A change sets
 * the chosen index, then raises `FieldClick`. Focus and blur are raised once as
 * focus enters and leaves the group, never when it moves between the group's own
 * radios, whether by keyboard, by a click on a radio or by a click on a label.
 */
export function OptionGroup(props: FieldComponentProps) {
  const { field, value, disabled, errors, onChange, onFocus, onBlur, onClick } = props;
  const groupId = fieldControlId(field.name);
  const options = field.options ?? [];
  const hasErrors = errors.length > 0;

  return (
    <div className={fieldWrapperClass(props)} data-field-name={field.name}>
      <fieldset
        id={groupId}
        disabled={disabled}
        aria-describedby={hasErrors ? fieldErrorId(field.name) : undefined}
        onFocus={(e) => {
          const from = e.relatedTarget;
          if (from instanceof Node && e.currentTarget.contains(from)) {
            return;
          }
          onFocus?.();
        }}
        onBlur={(e) => {
          const to = e.relatedTarget;
          if (to instanceof Node && e.currentTarget.contains(to)) {
            return;
          }
          onBlur?.();
        }}
        onMouseDown={(e) => {
          // A mousedown on a label moves focus to the body before the label's click
          // focuses its radio, so both focus events arrive with no related target and
          // the group would raise a spurious FieldAfter/FieldBefore pair. While focus
          // is already inside the group it stays there; the click still checks and
          // focuses the label's radio.
          if (e.target instanceof HTMLLabelElement && e.currentTarget.contains(document.activeElement)) {
            e.preventDefault();
          }
        }}
      >
        <legend>{fieldLabelText(field)}</legend>
        {options.map((option, i) => {
          const optionValue = typeof option.value === 'number' ? option.value : i;
          const radioId = `${groupId}-option-${i}`;
          // A keyed fragment, not a wrapper: styles.css lays out each radio and the
          // label that immediately follows it as direct children of the fieldset.
          return (
            <Fragment key={radioId}>
              <input
                type="radio"
                id={radioId}
                name={groupId}
                value={String(optionValue)}
                checked={value === optionValue}
                disabled={disabled}
                onChange={() => {
                  // Native `disabled` stops user input, but a scripted click event
                  // still checks a disabled radio and fires change; a protected or
                  // check-code-disabled field must emit neither a value nor FieldClick.
                  if (disabled) {
                    return;
                  }
                  // The new index first, then FieldClick, so check code sees the post-click value.
                  onChange(optionValue);
                  onClick?.();
                }}
              />
              <label htmlFor={radioId}>{option.label}</label>
            </Fragment>
          );
        })}
      </fieldset>
      {hasErrors ? (
        <div className="field-error" id={fieldErrorId(field.name)}>
          {errors.map((message, index) => (
            <p key={`${index}-${message}`}>{message}</p>
          ))}
        </div>
      ) : null}
    </div>
  );
}
