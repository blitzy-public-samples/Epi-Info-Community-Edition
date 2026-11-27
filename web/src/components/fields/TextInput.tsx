// Single-line text control for fieldType `text` (seeded Text, Phone Number and Comment
// Legal), the web counterpart of the text box legacy builds for a text field, whose
// MaxLength is the field's. Legacy masked entry for patterned fields was only an entry
// aid, so a metadata pattern is shown as a format hint and enforces nothing. Legacy
// Enter/Leave become onFocus/onBlur.
import type { FieldComponentProps } from './fieldComponents';
import {
  fieldAriaProps,
  fieldControlId,
  fieldErrorId,
  fieldLabelText,
  fieldTextValue,
  fieldWrapperClass,
  useLengthLimitNotice,
} from './fieldComponents';

/**
 * A native text `input` labelled by the field's prompt. It emits the raw text, `''`
 * included and untrimmed, and describes itself by a "Format:" hint when metadata sets a
 * pattern. A positive metadata `maxLength` is the input's native limit, as legacy's
 * MaxLength is; when typed or pasted text is cut to it, the limit's message is announced
 * beside the field and describes the input until the next edit or blur.
 */
export function TextInput(props: FieldComponentProps) {
  const { field, value, disabled, errors, onChange, onFocus, onBlur } = props;
  const id = fieldControlId(field.name);
  const configuredMaxLength = field.validation.maxLength;
  const maxLength =
    typeof configuredMaxLength === 'number' && configuredMaxLength > 0 ? configuredMaxLength : undefined;
  const configuredPattern = field.validation.pattern;
  const pattern = typeof configuredPattern === 'string' && configuredPattern !== '' ? configuredPattern : null;
  const hintId = pattern !== null ? `${id}-hint` : undefined;
  const lengthLimit = useLengthLimitNotice<HTMLInputElement>(maxLength, disabled);
  const limitId = lengthLimit.notice !== null ? `${id}-limit` : undefined;
  const describedBy = [hintId, limitId].filter((part) => part !== undefined).join(' ');

  return (
    <div className={fieldWrapperClass(props)} data-field-name={field.name}>
      <label htmlFor={id}>{fieldLabelText(field)}</label>
      <input
        ref={lengthLimit.ref}
        id={id}
        type="text"
        value={fieldTextValue(value)}
        maxLength={maxLength}
        disabled={disabled}
        {...fieldAriaProps(props, describedBy)}
        onChange={(e) => {
          onChange(e.target.value);
        }}
        onFocus={() => {
          onFocus?.();
        }}
        onBlur={() => {
          onBlur?.();
        }}
      />
      {pattern !== null && <small id={hintId}>Format: {pattern}</small>}
      {lengthLimit.notice !== null && (
        <small id={limitId} role="status">
          {lengthLimit.notice}
        </small>
      )}
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
