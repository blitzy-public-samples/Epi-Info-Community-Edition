// Legal-values dropdown for fieldType `legalValues`: a JSON string stored in a
// character column (seeded example `Sex`). It replaces the legacy
// TableBasedDropDownField and LegalValuesComboBox; the native `select` gives the
// type-ahead the combo box offered. Options arrive as the code table's stored text,
// in the order the server sends them (sorted only when metadata asks), so nothing
// here sorts or filters them. The legal-value rule is enforced by the server and
// mirrored by clientValidation.ts, not here.
import type { FieldComponentProps } from './fieldComponents';
import {
  fieldAriaProps,
  fieldControlId,
  fieldErrorId,
  fieldLabelText,
  fieldTextValue,
  fieldWrapperClass,
} from './fieldComponents';

/**
 * A native `select` labelled by the field's prompt. Its first option is empty and
 * stands for `null`. A stored value missing from the list is shown as one extra
 * option after the empty one, so a read shows the stored value unchanged.
 */
export function LegalValuesSelect(props: FieldComponentProps) {
  const { field, value, disabled, errors, onChange, onFocus, onBlur } = props;
  const id = fieldControlId(field.name);
  const options = field.options ?? [];
  const current = typeof value === 'string' ? value : '';
  const currentIsListed = options.some((option) => fieldTextValue(option.value) === current);

  return (
    <div className={fieldWrapperClass(props)} data-field-name={field.name}>
      <label htmlFor={id}>{fieldLabelText(field)}</label>
      <select
        id={id}
        value={current}
        disabled={disabled}
        {...fieldAriaProps(props)}
        onChange={(e) => {
          onChange(e.target.value === '' ? null : e.target.value);
        }}
        onFocus={() => {
          onFocus?.();
        }}
        onBlur={() => {
          onBlur?.();
        }}
      >
        <option value=""></option>
        {current !== '' && !currentIsListed && <option value={current}>{current}</option>}
        {options.map((option, index) => {
          const optionValue = fieldTextValue(option.value);
          return (
            <option key={`${index}-${optionValue}`} value={optionValue}>
              {option.label}
            </option>
          );
        })}
      </select>
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
