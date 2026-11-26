import type { FieldComponentProps } from './fieldComponents';
import { fieldControlId, fieldLabelText } from './fieldComponents';

/**
 * A `group` field: a section headed by the group's prompt, holding its members.
 * FieldRenderer resolves the members, sorts them by `tabIndex`, renders each once and
 * passes them as `children`; this component only frames them. The heading names the
 * section, so it is a region landmark. It is not a `fieldset`, because a disabled
 * fieldset would disable every member with it. The heading is `h3` at the page's top
 * level, and `h4` for a group nested as another group's member, which renders empty.
 */
export function GroupSection(props: FieldComponentProps) {
  const { field, headingLevel, children } = props;
  const headingId = `${fieldControlId(field.name)}-heading`;
  const Heading = headingLevel === 4 ? 'h4' : 'h3';
  return (
    <section className="group-section" data-field-name={field.name} aria-labelledby={headingId}>
      <Heading id={headingId}>{fieldLabelText(field)}</Heading>
      {children}
    </section>
  );
}
