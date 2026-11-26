// A `label` field (metadata Label/Title): a heading showing the field's prompt. It has
// no column and no value, is never a data field and is never written, so it renders
// no control, nothing focusable, and ignores the value, state and event props.
import type { FieldComponentProps } from './fieldComponents';
import { fieldLabelText } from './fieldComponents';

/**
 * Renders a label field as a heading below the form name's `h2`: `h3` at the page's top
 * level, and `h4` inside a group, whose own heading is `h3`.
 */
export function LabelHeading(props: FieldComponentProps) {
  const { field, headingLevel } = props;
  const Heading = headingLevel === 4 ? 'h4' : 'h3';
  return (
    <div className="field" data-field-name={field.name}>
      <Heading className="label-heading">{fieldLabelText(field)}</Heading>
    </div>
  );
}
