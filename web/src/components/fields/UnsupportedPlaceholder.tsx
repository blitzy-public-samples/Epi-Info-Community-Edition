// Placeholder for a field of type `unsupported`: grid, image, mirror, GUID, GPS or
// geocode, command button, relate, Date/Time, Time, any encrypted field, and any type
// whose column the app cannot convert exactly (seeded: Yes/No). The field is never
// written, so this renders no control, takes no focus and never emits a value; it
// ignores `value`, `disabled`, `errors` and `children`. FieldRenderer marks every
// unsupported field protected, so the wrapper carries `field--protected`.
import type { FieldComponentProps } from './fieldComponents';
import { fieldLabelText, fieldWrapperClass } from './fieldComponents';

/** A disabled placeholder that names the field's `unsupportedType`; not an input. */
export function UnsupportedPlaceholder(props: FieldComponentProps) {
  const { field } = props;
  return (
    <div className={fieldWrapperClass(props)} data-field-name={field.name}>
      <div className="unsupported-placeholder" aria-disabled="true">
        <span>{fieldLabelText(field)}</span>
        <span>{`Unsupported field type: ${field.unsupportedType ?? 'unknown'}`}</span>
      </div>
    </div>
  );
}
