/**
 * Serialises the record editor's check-code field events and resolves their races.
 *
 * - Events are sent one at a time, in the order they were raised. Each response is applied
 *   before the next event is sent, so every event sees the effects of earlier ones.
 * - Every event captures a sequence number s(E) and the editor's values U(E) when it is
 *   raised; a click is raised after its toggle, so U(E) holds the post-click value. U(E) is
 *   the values object itself, never a copy: the editor replaces it on every change. When E
 *   is sent, a field the user edited after s(E) carries its U(E) value and every other field
 *   its current value: E sees each edit made before it and never a later one.
 * - Every data field is sent, read-only ones included, each value in its field type's wire
 *   shape: a checkbox is `true` when checked and `false` otherwise, an untouched (`null`) box
 *   included, as the record's POST sends it; any other value the server's field-event check
 *   would reject, such as text in a number field, is sent as `null`, so it cannot fail this
 *   event and every later one. Save still validates the value as the draft holds it.
 * - The client's raiseFieldEvent checks that an OK answer is the whole response before it
 *   reports one, and settles any other as a failure of its OK status, which the queue
 *   reports as an answer that cannot be read. The queue sequences events and reports
 *   failures; applyFieldEvent applies a response and owns every effect rule, told which
 *   fields the user edited after s(E), whose edits win.
 * - A record change calls reset(): pending events are dropped, the in-flight request is
 *   cancelled, and a response that arrives for an older record generation is ignored.
 *   Sequence numbers and generations stay local and are never sent.
 * - A request that stays unanswered for the deadline is cancelled.
 * - A failed event (no answer in time, a network error, a 4xx or 5xx, an answer that cannot
 *   be read, or effects that cannot be applied) counts as processed, so it never blocks
 *   leaving or saving, and is recorded against its field and event kind. Each failure lasts
 *   until an event of that same kind on that field is answered and applied; another field's
 *   event, another kind of event on the field, and a save resolve nothing. The host
 *   is given the whole list of non-blocking notices of fixed text whenever it changes: one
 *   per failed field, in the order of the field's first failure, with the latest reason. An
 *   event of an older generation fails silently, and reset() clears every failure. drain()
 *   never rejects.
 *
 * The queue raises no event on its own, so loading or replacing a record raises none.
 */
import { isExactDecimal, isWireDate, type ApiClient, type ApiResult } from '../api/client';
import { describeError, errorMessages, isAbortError, networkError, type ApiError } from '../api/errors';
import type {
  Diagnostic,
  DialogMessage,
  FieldDefinition,
  FieldEventKind,
  FieldEventRequest,
  FieldEventResponse,
  FieldType,
  FieldValue,
  FormDefinition,
  Values,
} from '../api/types';
import { applyFieldEvent, type EditorSnapshot } from '../checkcode/applyFieldEvent';
import { isDataField } from '../validation/clientValidation';

/**
 * How long a field-event request may stay unanswered before the queue cancels it. The
 * wait is bounded so that a request that never answers cannot hold drain(), and with it
 * Save and leaving the record; a cancelled event counts as failed and is never resent. It
 * equals the API's default `Database:CommandTimeoutSeconds` (30 s), but equal values do not
 * decide which ends first: that timeout bounds each database command, not the request,
 * which may run several, and this timer starts before the request is sent. An answer the
 * server sends after the deadline, its own failure included, is ignored.
 */
export const FIELD_EVENT_DEADLINE_MS = 30_000;

/** The record editor as the queue sees it. */
export interface FieldEventQueueHost {
  /**
   * The editor's current state, read synchronously, including edits not yet rendered.
   * The host replaces `values` with a new object on every change and never mutates one it
   * has returned, so the queue keeps the object itself as an event's U(E).
   */
  getSnapshot(): EditorSnapshot;
  /** Writes the state a response produced. */
  setSnapshot(next: EditorSnapshot): void;
  /** The `DIALOG`s of one response, in execution order; called only when there is one. */
  onDialogs(dialogs: readonly DialogMessage[]): void;
  /** The diagnostics of one response, in source order; called only when there is one. */
  onDiagnostics?(diagnostics: readonly Diagnostic[]): void;
  /**
   * The non-blocking notices of the fields whose events could not be evaluated, one per field
   * in the order of its first failure; empty once none is left. Called with a new array
   * whenever the list changes, and never with an unchanged one.
   */
  onNotices(messages: readonly string[]): void;
}

export interface FieldEventQueueOptions {
  client: ApiClient;
  viewId: number;
  definition: FormDefinition;
  host: FieldEventQueueHost;
  /**
   * How long a request may stay unanswered, in milliseconds; FIELD_EVENT_DEADLINE_MS
   * unless this is a positive finite number, and at most the longest delay a timer honours.
   */
  deadlineMs?: number;
}

export interface FieldEventQueue {
  /** Queues an event raised on `fieldName`; the request is sent when every earlier event is done. */
  raise(kind: FieldEventKind, fieldName: string): void;
  /** Records that the user, not check code, changed the field's value. */
  noteUserEdit(fieldName: string): void;
  /** Settles once no event is pending or in flight, or at the next reset(); never rejects. */
  drain(): Promise<void>;
  /**
   * Starts a new record generation: drops pending events, cancels the in-flight one and
   * clears every failure, reporting the empty list when there was any.
   */
  reset(): void;
  /** Events pending or in flight in the current generation. */
  readonly size: number;
  /** Local record generation; never sent to the API. */
  readonly generation: number;
}

interface QueuedEvent {
  readonly kind: FieldEventKind;
  readonly fieldName: string;
  /** s(E): the sequence number taken when the event was raised. */
  readonly seq: number;
  /**
   * U(E): the editor's values object when the event was raised. The host replaces that
   * object on every change and never mutates it, so it still holds those values.
   */
  readonly captured: Values;
  readonly generation: number;
}

/** The event whose request is being sent, and the controller that cancels the request. */
interface InFlight {
  readonly event: QueuedEvent;
  readonly controller: AbortController;
}

/** How a sent request ended, before its generation is checked. */
type Delivery =
  | { readonly kind: 'answered'; readonly result: ApiResult<FieldEventResponse> }
  /** No response was received, and the request was not cancelled. */
  | { readonly kind: 'noResponse' }
  /** The request was aborted: by the deadline, by a reset, or by the browser itself. */
  | { readonly kind: 'cancelled' };

// The largest delay a timer honours; setTimeout fires at once for a longer one.
const MAX_TIMER_DELAY_MS = 2_147_483_647;

function deadlineOf(deadlineMs: number | undefined): number {
  if (deadlineMs === undefined || !Number.isFinite(deadlineMs) || deadlineMs <= 0) {
    return FIELD_EVENT_DEADLINE_MS;
  }
  return Math.min(deadlineMs, MAX_TIMER_DELAY_MS);
}

// Notice reasons are fixed text: a notice names the field by its label and carries nothing
// from the server's answer.
const NO_TIMELY_ANSWER = 'The server did not answer in time.';
const UNREADABLE_ANSWER = "The server's answer could not be read.";
const EFFECTS_NOT_APPLIED = 'Its effects could not be applied.';

// An OK status on a failure: the client settles an OK answer whose body is not JSON, or is
// not the whole field-event response, as a failure of that status (raiseFieldEvent).
function isUnreadableAnswer(error: ApiError): boolean {
  return error.status !== null && error.status >= 200 && error.status <= 299;
}

// Settles when `signal` aborts, so the deadline and a reset end the wait even when the
// transport does not honour the abort.
function whenAborted(signal: AbortSignal): Promise<void> {
  return new Promise((resolve) => {
    if (signal.aborted) {
      resolve();
      return;
    }
    signal.addEventListener(
      'abort',
      () => {
        resolve();
      },
      { once: true },
    );
  });
}

// Own properties only, so a field named like an Object member (`constructor`) never reads
// an inherited value.
function valueOf(values: Values, name: string): FieldValue {
  return Object.hasOwn(values, name) ? (values[name] ?? null) : null;
}

/** Every data field of the form, once each by name, in page and tab order. */
function dataFields(definition: FormDefinition): FieldDefinition[] {
  const fields = new Map<string, FieldDefinition>();
  for (const page of definition.pages) {
    for (const field of page.fields) {
      if (isDataField(field) && !fields.has(field.name)) {
        fields.set(field.name, field);
      }
    }
  }
  return [...fields.values()];
}

// Event values
//
// The server checks that each event value has its field type's wire shape, and a single
// value without it fails the whole request with 400. The draft can hold such a value for as
// long as the user leaves it there, such as the text `abc` in a number field, so sending it
// would fail every later event of the record and stop all its check code. The rules below
// mirror the server's field-event shape check (ValuesReader.ToTyped and
// FieldValueCoercion.Coerce) and nothing more: it applies no storage range, option count,
// length, mask or legal-value rule to an event, so neither do they.

// The largest option index the server reads: int.MaxValue.
const MAX_OPTION_INDEX = 2_147_483_647;

// Whether a value that is not null has the wire shape the server's field-event check gives
// its field type: a string for text; a finite number for a number, which the client may hold
// as a bigint or an ExactDecimal; a wire date (isWireDate) for a date, with no storage range;
// a boolean for a checkbox; and a whole number from 0 to MAX_OPTION_INDEX for an option.
function hasEventShape(fieldType: FieldType, value: Exclude<FieldValue, null>): boolean {
  switch (fieldType) {
    case 'text':
    case 'multiline':
    case 'legalValues':
      return typeof value === 'string';
    case 'number':
      return (
        (typeof value === 'number' && Number.isFinite(value)) ||
        typeof value === 'bigint' ||
        (isExactDecimal(value) && Number.isFinite(Number(value.literal)))
      );
    case 'date':
      return isWireDate(value);
    case 'checkbox':
      return typeof value === 'boolean';
    case 'option':
      return (
        (typeof value === 'number' && Number.isInteger(value) && value >= 0 && value <= MAX_OPTION_INDEX) ||
        (typeof value === 'bigint' && value >= 0n && value <= BigInt(MAX_OPTION_INDEX))
      );
    default:
      // Labels, groups and unsupported fields hold no value.
      return false;
  }
}

/**
 * The value an event carries for a field. A checkbox is `true` only when checked: an
 * untouched box is `null` in the draft and shows unchecked, and legacy reads every unchecked
 * box as `false`, as the record's POST sends it, so check code such as `Fever = (-)` sees it
 * unchecked. Any other value without its type's wire shape is `null`, which the server
 * accepts for every field, so the event and every later one still run; validation at save
 * still reports the value.
 */
function eventValue(field: FieldDefinition, value: FieldValue): FieldValue {
  if (field.fieldType === 'checkbox') {
    return value === true;
  }
  return value === null || hasEventShape(field.fieldType, value) ? value : null;
}

/** The text a notice names a field by: its prompt, or its name when the prompt is blank. */
function fieldLabels(definition: FormDefinition): Map<string, string> {
  const labels = new Map<string, string>();
  for (const page of definition.pages) {
    for (const field of page.fields) {
      if (!labels.has(field.name)) {
        const prompt = field.prompt?.trim() ?? '';
        labels.set(field.name, prompt === '' ? field.name : prompt);
      }
    }
  }
  return labels;
}

/** The reason of a field's latest failure that still stands: the last entry of its reasons. */
function latestReason(reasons: ReadonlyMap<FieldEventKind, string>): string {
  let latest = '';
  for (const reason of reasons.values()) {
    latest = reason;
  }
  return latest;
}

// describeError's texts for these kinds speak of saving or opening records, which a field
// event never does; the endpoint's 404 means an unknown form.
function failureReason(error: ApiError): string {
  switch (error.kind) {
    case 'validation':
    case 'notFound':
    case 'conflict':
    case 'forbidden':
      return errorMessages.badRequest;
    default:
      return describeError(error);
  }
}

export function createFieldEventQueue(options: FieldEventQueueOptions): FieldEventQueue {
  const { client, viewId, definition, host } = options;
  const deadlineMs = deadlineOf(options.deadlineMs);
  const fields = dataFields(definition);
  const labels = fieldLabels(definition);

  let seq = 0;
  let generation = 0;
  const lastEditSeq = new Map<string, number>();
  const pending: QueuedEvent[] = [];
  let inFlight: InFlight | null = null;
  let idleWaiters: (() => void)[] = [];
  /**
   * The current generation's failures: each field, in the order of its first failure, maps
   * every event kind that failed and has not been applied since to the reason it failed,
   * the latest failure last.
   */
  const failures = new Map<string, Map<FieldEventKind, string>>();
  /** The notices the host was last given. */
  let reported: readonly string[] = [];

  const releaseWaiters = (): void => {
    const waiters = idleWaiters;
    idleWaiters = [];
    for (const resolve of waiters) {
      resolve();
    }
  };

  const isCurrent = (event: QueuedEvent): boolean => event.generation === generation;

  const editedSince = (event: QueuedEvent): Set<string> => {
    const names = new Set<string>();
    for (const [name, editSeq] of lastEditSeq) {
      if (editSeq > event.seq) {
        names.add(name);
      }
    }
    return names;
  };

  // Built when the event is sent, not when it was raised, so it carries the effects of
  // every earlier response. Each field's value is chosen by the capture rule and then sent
  // in its wire shape (eventValue). Object.fromEntries defines own properties, so no field
  // name can reach the prototype.
  const payloadValues = (event: QueuedEvent): Values => {
    const current = host.getSnapshot().values;
    const edited = editedSince(event);
    return Object.fromEntries(
      fields.map((field): [string, FieldValue] => [
        field.name,
        eventValue(field, valueOf(edited.has(field.name) ? event.captured : current, field.name)),
      ]),
    );
  };

  // Gives the host the notices of the current failures when they differ from the last list
  // it was given. A list the host cannot show is dropped: it is the queue's only way to
  // report, and the event must still count as processed so that drain() is released. The
  // next change sends the whole list again.
  const report = (): void => {
    const messages: string[] = [];
    for (const [fieldName, reasons] of failures) {
      const label = labels.get(fieldName) ?? fieldName;
      messages.push(`Check code could not run for ${label}. ${latestReason(reasons)}`);
    }
    if (messages.length === reported.length && messages.every((message, i) => message === reported[i])) {
      return;
    }
    reported = messages;
    try {
      host.onNotices(messages);
    } catch {
      // Nothing is left to report the failure to.
    }
  };

  // Silent for an event of an older generation, whose record is no longer on screen. A field
  // that failed before keeps its place in the list, and the reason given becomes its latest.
  const notify = (event: QueuedEvent, reason: string): void => {
    if (!isCurrent(event)) {
      return;
    }
    const reasons = failures.get(event.fieldName) ?? new Map<FieldEventKind, string>();
    // Removed and set again, so the latest failure is the field's last entry.
    reasons.delete(event.kind);
    reasons.set(event.kind, reason);
    failures.set(event.fieldName, reasons);
    report();
  };

  // An event that was answered and applied resolves the failure of its own kind on its own
  // field, and nothing else: the field's other kinds and every other field keep theirs.
  const clearFailure = (event: QueuedEvent): void => {
    const reasons = failures.get(event.fieldName);
    if (!isCurrent(event) || reasons === undefined || !reasons.delete(event.kind)) {
      return;
    }
    if (reasons.size === 0) {
      failures.delete(event.fieldName);
    }
    report();
  };

  const apply = (event: QueuedEvent, response: FieldEventResponse): void => {
    const result = applyFieldEvent({
      snapshot: host.getSnapshot(),
      response,
      definition,
      editedSince: editedSince(event),
    });
    host.setSnapshot(result.snapshot);
    if (result.dialogs.length > 0) {
      host.onDialogs(result.dialogs);
    }
    if (result.diagnostics.length > 0) {
      host.onDiagnostics?.(result.diagnostics);
    }
  };

  // Waits for the request to settle or for `signal` to abort, whichever comes first, so a
  // cancelled request holds nothing even when the transport keeps it open.
  const deliver = (request: FieldEventRequest, signal: AbortSignal): Promise<Delivery> => {
    // The executor turns a client that throws instead of rejecting into a rejection.
    const sent = new Promise<ApiResult<FieldEventResponse>>((resolve) => {
      resolve(client.raiseFieldEvent(viewId, request, signal));
    });
    const answered = sent.then(
      (result): Delivery => ({ kind: 'answered', result }),
      // The client settles every failure except an abort into a result; anything else
      // thrown still means no response was received.
      (e: unknown): Delivery => (signal.aborted || isAbortError(e) ? { kind: 'cancelled' } : { kind: 'noResponse' }),
    );
    const cancelled = whenAborted(signal).then((): Delivery => ({ kind: 'cancelled' }));
    return Promise.race([answered, cancelled]);
  };

  // Settles every outcome itself and never rejects: whatever throws, from building the
  // payload to the last host callback, becomes a notice of the current generation.
  const dispatch = async ({ event, controller }: InFlight): Promise<void> => {
    const deadline = { expired: false };
    const timer = setTimeout(() => {
      deadline.expired = true;
      controller.abort();
    }, deadlineMs);

    try {
      const request: FieldEventRequest = { event: event.kind, fieldName: event.fieldName, values: payloadValues(event) };
      const delivery = await deliver(request, controller.signal);

      // A response for an earlier record generation belongs to a record no longer on screen.
      if (!isCurrent(event)) {
        return;
      }
      if (delivery.kind === 'cancelled') {
        // Only the deadline's cancellation is a failure; any other abort is not reported.
        if (deadline.expired) {
          notify(event, NO_TIMELY_ANSWER);
        }
        return;
      }
      if (delivery.kind === 'noResponse') {
        notify(event, failureReason(networkError()));
        return;
      }
      const { result } = delivery;
      if (!result.ok) {
        notify(event, isUnreadableAnswer(result.error) ? UNREADABLE_ANSWER : failureReason(result.error));
        return;
      }
      apply(event, result.data);
      clearFailure(event);
    } catch {
      notify(event, EFFECTS_NOT_APPLIED);
    } finally {
      clearTimeout(timer);
    }
  };

  const sendNext = (): void => {
    if (inFlight !== null) {
      return;
    }
    const event = pending.shift();
    if (event === undefined) {
      releaseWaiters();
      return;
    }

    const sending: InFlight = { event, controller: new AbortController() };
    inFlight = sending;
    // dispatch never rejects; the slot is still released on either settlement, so nothing
    // can keep it taken and stop the queue.
    const release = (): void => {
      // After a reset the in-flight slot belongs to the new generation.
      if (inFlight === sending) {
        inFlight = null;
        sendNext();
      }
    };
    void dispatch(sending).then(release, release);
  };

  return {
    raise(kind, fieldName) {
      seq += 1;
      pending.push({ kind, fieldName, seq, captured: host.getSnapshot().values, generation });
      sendNext();
    },

    noteUserEdit(fieldName) {
      seq += 1;
      lastEditSeq.set(fieldName, seq);
    },

    drain() {
      if (pending.length === 0 && inFlight === null) {
        return Promise.resolve();
      }
      return new Promise<void>((resolve) => {
        idleWaiters.push(resolve);
      });
    },

    reset() {
      generation += 1;
      pending.length = 0;
      // Cancelled and detached rather than awaited: the new record's events do not wait
      // for the stale request, whose outcome belongs to an older generation and is silent.
      const stale = inFlight;
      inFlight = null;
      lastEditSeq.clear();
      // The failures belonged to the record being left; the host is told that none is left.
      failures.clear();
      report();
      stale?.controller.abort();
      releaseWaiters();
    },

    get size() {
      return pending.length + (inFlight === null ? 0 : 1);
    },

    get generation() {
      return generation;
    },
  };
}
