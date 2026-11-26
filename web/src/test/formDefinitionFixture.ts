// Shared typed fixture for the Vitest suites under src/: a synthetic form shaped like
// the GET /api/forms/{viewId} response, plus record builders. Names follow the seeded
// FoodHistory form, but nothing here reads testdata/, the network or the file system;
// the literals type-check against the generated contract, so a contract change breaks
// `tsc --noEmit` here first.
//
// Deliberate traps for renderer tests:
// - the group's `members` are out of `tabIndex` order and include a field on page 2;
// - `Black` has a fractional `tabIndex`;
// - `Sex` options are not alphabetical, so a test can prove server order is kept;
// - `IllnessSeverity` is an Option field, which no seeded form has.
import type {
  FieldDefinition,
  FieldOption,
  FieldType,
  FieldValidation,
  FormDefinition,
  FormSummary,
  RecordDto,
  RecordSummary,
  Values,
} from '../api/types';

// Explicit members rather than Pick<> of the generated types, so the builder keeps
// compiling whether the generator marks a property optional or required.
type FieldInit = {
  name: string;
  fieldType: FieldType;
  prompt: string;
  tabIndex: number;
  pageId: number;
  validation?: {
    required?: boolean;
    readOnly?: boolean;
    maxLength?: number | null;
    lower?: FieldValidation['lower'];
    upper?: FieldValidation['upper'];
    pattern?: string | null;
  };
  options?: FieldOption[] | null;
  members?: string[] | null;
  unsupportedType?: string | null;
};

const PAGE_1_ID = 2;
const PAGE_2_ID = 3;

const nonDataFieldTypes: ReadonlySet<FieldType> = new Set<FieldType>(['label', 'group', 'unsupported']);

/** Builds a complete Field DTO, failing fast when an edit to the fixture breaks a contract invariant. */
function field(init: FieldInit): FieldDefinition {
  const unsupported = init.fieldType === 'unsupported';
  const unsupportedType = init.unsupportedType ?? null;
  const options = init.options ?? null;
  const members = init.members ?? null;

  if (unsupported !== (unsupportedType !== null)) {
    throw new Error(`fixture field ${init.name}: unsupportedType must be set exactly when the field is unsupported`);
  }
  if (options !== null && init.fieldType !== 'option' && init.fieldType !== 'legalValues') {
    throw new Error(`fixture field ${init.name}: options belong only to option and legalValues fields`);
  }
  if (members !== null && init.fieldType !== 'group') {
    throw new Error(`fixture field ${init.name}: members belong only to group fields`);
  }

  const validation = init.validation ?? {};
  return {
    name: init.name,
    fieldType: init.fieldType,
    prompt: init.prompt,
    tabIndex: init.tabIndex,
    pageId: init.pageId,
    unsupported,
    unsupportedType,
    validation: {
      required: validation.required ?? false,
      readOnly: validation.readOnly ?? false,
      maxLength: validation.maxLength ?? null,
      lower: validation.lower ?? null,
      upper: validation.upper ?? null,
      pattern: validation.pattern ?? null,
    },
    options: options === null ? null : options.map((option) => ({ ...option })),
    members: members === null ? null : [...members],
  };
}

/**
 * A fresh `FoodHistory`-like form definition on every call: two pages in `position`
 * order, fields in `tabIndex` order. Use it when a test changes the definition.
 */
export function createFormDefinitionFixture(): FormDefinition {
  return {
    viewId: 2,
    name: 'FoodHistory',
    pages: [
      {
        pageId: PAGE_1_ID,
        name: 'Page 1',
        position: 1,
        fields: [
          field({ name: 'Title', fieldType: 'label', prompt: 'Food History Interview', tabIndex: 1, pageId: PAGE_1_ID }),
          field({ name: 'CaseID', fieldType: 'number', prompt: 'Case ID', tabIndex: 2, pageId: PAGE_1_ID }),
          field({
            name: 'DemographicInformation',
            fieldType: 'group',
            prompt: 'Demographic Information',
            tabIndex: 3,
            pageId: PAGE_1_ID,
            // Reverse tabIndex order, as the seeded metaFields.List is; Breastmilk is on page 2.
            members: ['Sex', 'LastName', 'FirstName', 'Breastmilk'],
          }),
          field({ name: 'FirstName', fieldType: 'text', prompt: 'First Name', tabIndex: 4, pageId: PAGE_1_ID }),
          field({ name: 'LastName', fieldType: 'text', prompt: 'Last Name', tabIndex: 5, pageId: PAGE_1_ID }),
          field({
            name: 'Sex',
            fieldType: 'legalValues',
            prompt: 'Sex',
            tabIndex: 6,
            pageId: PAGE_1_ID,
            options: [
              { value: 'M-Male', label: 'M-Male' },
              { value: 'F-Female', label: 'F-Female' },
              { value: 'U-Unknown', label: 'U-Unknown' },
            ],
          }),
          field({
            name: 'DOB',
            fieldType: 'date',
            prompt: 'DOB',
            tabIndex: 7,
            pageId: PAGE_1_ID,
            validation: { pattern: 'MM-DD-YYYY' },
          }),
          field({
            name: 'Age',
            fieldType: 'number',
            prompt: 'Age',
            tabIndex: 8,
            pageId: PAGE_1_ID,
            validation: { lower: 0, upper: 120, pattern: '###' },
          }),
          field({
            name: 'DateofInterview',
            fieldType: 'date',
            prompt: 'Date of Interview',
            tabIndex: 9,
            pageId: PAGE_1_ID,
            // Wire YYYY-MM-DD, as the API converts the stored 01-15-2011 and 12-31-2012.
            validation: { lower: '2011-01-15', upper: '2012-12-31', pattern: 'MM-DD-YYYY' },
          }),
          field({
            name: 'PhoneNumber',
            fieldType: 'text',
            prompt: 'Phone Number',
            tabIndex: 10,
            pageId: PAGE_1_ID,
            validation: { pattern: '###-###-####' },
          }),
          field({
            name: 'Zip',
            fieldType: 'text',
            prompt: 'Zip',
            tabIndex: 11,
            pageId: PAGE_1_ID,
            validation: { maxLength: 5 },
          }),
          field({ name: 'PatientAddress', fieldType: 'multiline', prompt: 'Address', tabIndex: 12, pageId: PAGE_1_ID }),
          field({ name: 'Asian', fieldType: 'checkbox', prompt: 'Asian', tabIndex: 13, pageId: PAGE_1_ID }),
          field({ name: 'Black', fieldType: 'checkbox', prompt: 'Black', tabIndex: 14.5, pageId: PAGE_1_ID }),
          field({
            name: 'Headache',
            fieldType: 'checkbox',
            prompt: 'Headache',
            tabIndex: 15,
            pageId: PAGE_1_ID,
            validation: { readOnly: true },
          }),
          field({
            name: 'IllnessSeverity',
            fieldType: 'option',
            prompt: 'Illness severity',
            tabIndex: 16,
            pageId: PAGE_1_ID,
            options: [
              { value: 0, label: 'Mild' },
              { value: 1, label: 'Moderate' },
              { value: 2, label: 'Severe' },
            ],
          }),
          field({
            name: 'ILL',
            fieldType: 'unsupported',
            prompt: 'Was the patient ill?',
            tabIndex: 17,
            pageId: PAGE_1_ID,
            unsupportedType: 'Yes/No',
          }),
          field({
            name: 'FeverTemp',
            fieldType: 'number',
            prompt: 'Highest fever temp',
            tabIndex: 18,
            pageId: PAGE_1_ID,
            validation: { lower: 80, upper: 110, pattern: '###.#' },
          }),
          field({
            name: 'OnsetDate',
            fieldType: 'date',
            prompt: 'Symptom Onset Date',
            tabIndex: 19,
            pageId: PAGE_1_ID,
            validation: { lower: '2011-01-15', upper: '2012-12-31', pattern: 'MM-DD-YYYY' },
          }),
          field({
            name: 'OnsetWeek',
            fieldType: 'number',
            prompt: 'Onset Week',
            tabIndex: 20,
            pageId: PAGE_1_ID,
            validation: { readOnly: true },
          }),
        ],
      },
      {
        pageId: PAGE_2_ID,
        name: 'Page 2',
        position: 2,
        fields: [
          field({
            name: 'FoodsLabel',
            fieldType: 'label',
            prompt: 'Foods eaten in the 10 days before illness',
            tabIndex: 1,
            pageId: PAGE_2_ID,
          }),
          field({
            name: 'BuyLastTenDays',
            fieldType: 'multiline',
            prompt: 'Where did you buy groceries?',
            tabIndex: 2,
            pageId: PAGE_2_ID,
          }),
          field({ name: 'Breastmilk', fieldType: 'checkbox', prompt: 'Breast milk', tabIndex: 3, pageId: PAGE_2_ID }),
          field({ name: 'Lab', fieldType: 'text', prompt: 'Lab:', tabIndex: 4, pageId: PAGE_2_ID }),
        ],
      },
    ],
    unsupportedCheckCode: [],
    canInsert: null,
    canUpdate: null,
    canDelete: null,
  };
}

// Freezes the shared instance so a test that mutates it fails loudly instead of
// leaking state into the tests that run after it.
function deepFreeze<T>(value: T): T {
  if (typeof value === 'object' && value !== null) {
    const children: unknown[] = Object.values(value);
    for (const child of children) {
      deepFreeze(child);
    }
    Object.freeze(value);
  }
  return value;
}

/**
 * The shared, deeply frozen form definition, for tests that only read it. Tests that
 * change the definition use {@link createFormDefinitionFixture} or {@link withField}.
 */
export const formDefinitionFixture: FormDefinition = deepFreeze(createFormDefinitionFixture());

/** The forms list (`GET /api/forms`); the `FoodHistory` entry agrees with {@link formDefinitionFixture}. */
export const formSummariesFixture: FormSummary[] = [
  { viewId: 1, name: 'CaseReport', pageCount: 1 },
  { viewId: 2, name: 'FoodHistory', pageCount: 2 },
];

/** Every field of a definition, pages by `position` and then `tabIndex`; the input is not modified. */
export function allFields(definition: FormDefinition = formDefinitionFixture): FieldDefinition[] {
  return [...definition.pages]
    .sort((a, b) => a.position - b.position)
    .flatMap((page) => [...page.fields].sort((a, b) => a.tabIndex - b.tabIndex));
}

/** The field with exactly this name; throws when the definition has none. */
export function fieldByName(name: string, definition: FormDefinition = formDefinitionFixture): FieldDefinition {
  for (const page of definition.pages) {
    const found = page.fields.find((candidate) => candidate.name === name);
    if (found !== undefined) {
      return found;
    }
  }
  throw new Error(`fixture has no field ${name}`);
}

/** Whether a field holds a value in the Record DTO: anything but a label, a group or an unsupported field. */
export function isDataField(definition: FieldDefinition): boolean {
  return !nonDataFieldTypes.has(definition.fieldType);
}

/** The fixture's data-field names in {@link allFields} order: the keys of every record's `values`. */
export const dataFieldNames: readonly string[] = Object.freeze(
  allFields()
    .filter(isDataField)
    .map((definition) => definition.name),
);

/** Data fields the client must never send (`Headache`, `OnsetWeek`). */
export const readOnlyFieldNames: readonly string[] = Object.freeze(
  allFields()
    .filter((definition) => isDataField(definition) && definition.validation.readOnly)
    .map((definition) => definition.name),
);

/** Writable checkboxes, which a new-record `POST` sends as `false` when unchecked (`Asian`, `Black`, `Breastmilk`). */
export const writableCheckboxNames: readonly string[] = Object.freeze(
  allFields()
    .filter((definition) => definition.fieldType === 'checkbox' && !definition.validation.readOnly)
    .map((definition) => definition.name),
);

/** A fresh `values` object with every data field `null`, as the API returns an empty record. */
export function emptyValues(): Values {
  const values: Values = {};
  for (const name of dataFieldNames) {
    values[name] = null;
  }
  return values;
}

/** Overrides accepted by {@link recordFixture}; `values` entries are laid over {@link emptyValues}. */
export type RecordFixtureOverrides = {
  globalRecordId?: string;
  uniqueKey?: number;
  recStatus?: number;
  position?: number;
  count?: number;
  values?: Values;
};

/**
 * A Record DTO of the fixture form: active record 1 of 3 with every value `null`
 * unless overridden. Throws when a `values` key is not a data field of the fixture.
 */
export function recordFixture(overrides: RecordFixtureOverrides = {}): RecordDto {
  const values = overrides.values ?? {};
  for (const name of Object.keys(values)) {
    if (!dataFieldNames.includes(name)) {
      throw new Error(`fixture has no data field ${name}`);
    }
  }
  return {
    globalRecordId: overrides.globalRecordId ?? '3f2b8c1e-5d4a-4c1b-9e7f-000000000001',
    uniqueKey: overrides.uniqueKey ?? 1,
    recStatus: overrides.recStatus ?? 1,
    position: overrides.position ?? 1,
    count: overrides.count ?? 3,
    values: { ...emptyValues(), ...values },
  };
}

const gridFieldTypes: ReadonlySet<FieldType> = new Set<FieldType>(['text', 'number', 'date', 'legalValues']);

const GRID_COLUMN_LIMIT = 6;

/**
 * A definition's grid columns by the contract's rule: the first six data fields whose
 * `fieldType` is `text`, `number`, `date` or `legalValues`, pages by `position` and then
 * `tabIndex`. Computed here, independently of the client, so a test can compare against
 * it. For {@link formDefinitionFixture}: `CaseID, FirstName, LastName, Sex, DOB, Age`.
 */
export function gridColumnNames(definition: FormDefinition = formDefinitionFixture): string[] {
  return allFields(definition)
    .filter((candidate) => isDataField(candidate) && gridFieldTypes.has(candidate.fieldType))
    .slice(0, GRID_COLUMN_LIMIT)
    .map((candidate) => candidate.name);
}

/**
 * The records-list row of `record`, as `GET /api/forms/{viewId}/records` returns it:
 * the record's ids and status, and `values` with the {@link gridColumnNames} of
 * `definition` only, `null` where the record holds none. Computed independently of the
 * client, so a fake API can serve it and a test can compare against it.
 */
export function recordSummaryFixture(
  record: RecordDto,
  definition: FormDefinition = formDefinitionFixture,
): RecordSummary {
  const values: Values = {};
  for (const name of gridColumnNames(definition)) {
    values[name] = record.values[name] ?? null;
  }
  return {
    globalRecordId: record.globalRecordId,
    uniqueKey: record.uniqueKey,
    recStatus: record.recStatus,
    values,
  };
}

/**
 * A fresh form definition with one field replaced by `update(field)` at the same
 * place, for example to make a field required. Throws when the name is missing.
 */
export function withField(name: string, update: (field: FieldDefinition) => FieldDefinition): FormDefinition {
  const definition = createFormDefinitionFixture();
  for (const page of definition.pages) {
    const index = page.fields.findIndex((candidate) => candidate.name === name);
    if (index >= 0) {
      const current = page.fields[index];
      page.fields[index] = update(current);
      return definition;
    }
  }
  throw new Error(`fixture has no field ${name}`);
}
