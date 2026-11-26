// A `label` field (metadata Label/Title): a heading showing the field's prompt. It has
// no column and no value, is never a data field and is never written, so it renders
// no control, nothing focusable, and ignores the value, state and event props.
import type { FieldComponentProps } from './fieldComponents';
import { fieldLabelText } from './fieldComponents';

/** Renders a label field as a heading; FormView uses the higher heading levels. */
export function LabelHeading(props: FieldComponentProps) {
  const { field } = props;
  return (
    <div className="field" data-field-name={field.name}>
      <h3 className="label-heading">{fieldLabelText(field)}</h3>
    </div>
  );
}
