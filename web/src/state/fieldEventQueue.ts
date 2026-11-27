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
 * - A response is applied by applyFieldEvent, which owns every effect rule; the queue only
 *   checks that the answer has the response's shape and tells applyFieldEvent which fields
 *   the user edited after s(E), whose edits win.
 * - A record change calls reset(): pending events are dropped, the in-flight request is
 *   cancelled, and a response that arrives for an older record generation is ignored.
 *   Sequence numbers and generations stay local and are never sent.
 * - A request that stays unanswered for the deadline is cancelled.
 * - A failed event (no answer in time, a network error, a 4xx or 5xx, an answer that cannot
 *   be read, or effects that cannot be applied) shows a non-blocking notice of fixed text
 *   and counts as processed, so it never blocks leaving or saving. An event of an older
 *   generation fails silently. drain() never rejects.
 *
 * The queue raises no event on its own, so loading or replacing a record raises none.
 */
import { isFieldValue, type ApiClient, type ApiResult } from '../api/client';
import { describeError, errorMessages, isAbortError, networkError, type ApiError } from '../api/errors';
import type {
  Diagnostic,
  DiagnosticSeverity,
  DialogMessage,
  FieldEventKind,
  FieldEventRequest,
  FieldEventResponse,
  FieldValue,
  FormDefinition,
  Values,
} from '../api/types';
import { applyFieldEvent, type EditorSnapshot } from '../checkcode/applyFieldEvent';
import { isDataField } from '../validation/clientValidation';

/**
 * How long a field-event request may stay unanswered before the queue cancels it. The
 * wait is bounded so that a request that never answers cannot hold drain(), and with it
 * Save and leaving the record. It equals the API's default `Database:CommandTimeoutSeconds`
 * (30 s), so the client does not give up before the server's own bound on a command.
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
  /** A non-blocking notice that an event could not be evaluated. */
  onNotice(message: string): void;
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
  /** Starts a new record generation: drops pending events and cancels the in-flight one. */
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

// The answer of an OK request
//
// The client types the body from the OpenAPI document but does not check it at run time,
// so it is read here as `unknown`. The document requires all six members and none of them
// is nullable, so a member that is missing, null or of another type makes the whole answer
// unreadable, and none of its effects is applied.

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function isString(value: unknown): value is string {
  return typeof value === 'string';
}

function isDialog(value: unknown): value is DialogMessage {
  return isObject(value) && isString(value.message) && (value.title === null || isString(value.title));
}

const SEVERITIES: readonly DiagnosticSeverity[] = ['info', 'warning', 'error'];

/** A diagnostic's line: 1-based in the form's program text. */
function isLine(value: unknown): value is number {
  return typeof value === 'number' && Number.isInteger(value) && value >= 1;
}

function isDiagnostic(value: unknown): value is Diagnostic {
  return (
    isObject(value) &&
    SEVERITIES.some((severity) => severity === value.severity) &&
    isString(value.form) &&
    isLine(value.line) &&
    isString(value.command) &&
    isString(value.message)
  );
}

/** A list member as a fresh array, or null when it is missing, null or not an array of `isEntry` entries. */
function readList<T>(value: unknown, isEntry: (entry: unknown) => entry is T): T[] | null {
  if (!Array.isArray(value)) {
    return null;
  }
  const list: readonly unknown[] = value;
  const entries: T[] = [];
  for (const entry of list) {
    if (!isEntry(entry)) {
      return null;
    }
    entries.push(entry);
  }
  return entries;
}

/**
 * `assigned` as own entries in the server's order, or null when it is missing, null or not a
 * map of field values. An entry is checked by the client's own field-value check, so a value
 * the client decoded exactly, such as the bigint of 9007199254740993, is an assignment like
 * any other.
 */
function readAssigned(value: unknown): Values | null {
  if (!isObject(value)) {
    return null;
  }
  const entries: [string, FieldValue][] = [];
  for (const [name, entry] of Object.entries(value)) {
    if (!isFieldValue(entry)) {
      return null;
    }
    entries.push([name, entry]);
  }
  // Object.fromEntries defines own properties, so no field name can reach the prototype.
  return Object.fromEntries(entries);
}

/** The answer as a response, or null when it lacks any of the six members or one does not have its shape. */
function readResponse(body: unknown): FieldEventResponse | null {
  if (!isObject(body)) {
    return null;
  }
  const enabled = readList(body.enabled, isString);
  const disabled = readList(body.disabled, isString);
  const cleared = readList(body.cleared, isString);
  const assigned = readAssigned(body.assigned);
  const dialogs = readList(body.dialogs, isDialog);
  const diagnostics = readList(body.diagnostics, isDiagnostic);
  if (
    enabled === null ||
    disabled === null ||
    cleared === null ||
    assigned === null ||
    dialogs === null ||
    diagnostics === null
  ) {
    return null;
  }
  return { enabled, disabled, cleared, assigned, dialogs, diagnostics };
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

/** Every data field of the form, once each, in page and tab order. */
function dataFieldNames(definition: FormDefinition): string[] {
  const names = new Set<string>();
  for (const page of definition.pages) {
    for (const field of page.fields) {
      if (isDataField(field)) {
        names.add(field.name);
      }
    }
  }
  return [...names];
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
  const fieldNames = dataFieldNames(definition);
  const labels = fieldLabels(definition);

  let seq = 0;
  let generation = 0;
  const lastEditSeq = new Map<string, number>();
  const pending: QueuedEvent[] = [];
  let inFlight: InFlight | null = null;
  let idleWaiters: (() => void)[] = [];

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
  // every earlier response. Object.fromEntries defines own properties, so no field name
  // can reach the prototype.
  const payloadValues = (event: QueuedEvent): Values => {
    const current = host.getSnapshot().values;
    const edited = editedSince(event);
    return Object.fromEntries(
      fieldNames.map((name): [string, FieldValue] => [
        name,
        valueOf(edited.has(name) ? event.captured : current, name),
      ]),
    );
  };

  // Silent for an event of an older generation, whose record is no longer on screen. A
  // notice the host cannot show is dropped: it is the queue's only way to report, and the
  // event must still count as processed so that drain() is released.
  const notify = (event: QueuedEvent, reason: string): void => {
    if (!isCurrent(event)) {
      return;
    }
    const label = labels.get(event.fieldName) ?? event.fieldName;
    try {
      host.onNotice(`Check code could not run for ${label}. ${reason}`);
    } catch {
      // Nothing is left to report the failure to.
    }
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
        notify(event, failureReason(result.error));
        return;
      }
      const response = readResponse(result.data);
      if (response === null) {
        notify(event, UNREADABLE_ANSWER);
        return;
      }
      apply(event, response);
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
