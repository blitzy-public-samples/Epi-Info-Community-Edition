import type { FieldComponentProps } from './fieldComponents';
import { fieldControlId, fieldLabelText } from './fieldComponents';

/**
 * A `group` field: a section headed by the group's prompt, holding its members.
 * FieldRenderer resolves the members, sorts them by `tabIndex`, renders each once and
 * passes them as `children`; this component only frames them. The heading names the
 * section, so it is a region landmark. It is not a `fieldset`, because a disabled
 * fieldset would disable every member with it.
 */
export function GroupSection(props: FieldComponentProps) {
  const { field, children } = props;
  const headingId = `${fieldControlId(field.name)}-heading`;
  return (
    <section className="group-section" data-field-name={field.name} aria-labelledby={headingId}>
      <h3 id={headingId}>{fieldLabelText(field)}</h3>
      {children}
    </section>
  );
}
