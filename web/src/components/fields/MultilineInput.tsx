// Multiline text control for fieldType `multiline` (seeded `ntext` columns), the web
// counterpart of the multiline text box legacy builds for a Multiline field. Legacy
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
 * A native `textarea` labelled by the field's prompt. It emits the raw text, `''`
 * and line breaks included. Its length is limited only when metadata sets a positive
 * `maxLength`, because `ntext` is unbounded; that limit is the native one, as legacy's
 * MaxLength is, and when typed or pasted text is cut to it, the limit's message is
 * announced beside the field and describes the textarea until the next edit or blur.
 */
export function MultilineInput(props: FieldComponentProps) {
  const { field, value, disabled, errors, onChange, onFocus, onBlur } = props;
  const id = fieldControlId(field.name);
  const configuredMaxLength = field.validation.maxLength;
  const maxLength =
    typeof configuredMaxLength === 'number' && configuredMaxLength > 0 ? configuredMaxLength : undefined;
  const lengthLimit = useLengthLimitNotice<HTMLTextAreaElement>(maxLength, disabled);
  const limitId = lengthLimit.notice !== null ? `${id}-limit` : undefined;

  return (
    <div className={fieldWrapperClass(props)} data-field-name={field.name}>
      <label htmlFor={id}>{fieldLabelText(field)}</label>
      <textarea
        ref={lengthLimit.ref}
        id={id}
        rows={4}
        value={fieldTextValue(value)}
        maxLength={maxLength}
        disabled={disabled}
        {...fieldAriaProps(props, limitId)}
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
