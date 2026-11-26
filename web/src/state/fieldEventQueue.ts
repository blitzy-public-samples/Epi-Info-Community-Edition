/**
 * Serialises the record editor's check-code field events and resolves their races.
 *
 * - Events are sent one at a time, in the order they were raised. Each response is applied
 *   before the next event is sent, so every event sees the effects of earlier ones.
 * - Every event captures a sequence number s(E) and the editor's values U(E) when it is
 *   raised; a click is raised after its toggle, so U(E) holds the post-click value. When E
 *   is sent, a field the user edited after s(E) carries its U(E) value and every other field
 *   its current value: E sees each edit made before it and never a later one.
 * - A response is applied by applyFieldEvent, which owns every effect rule; the queue only
 *   tells it which fields the user edited after s(E), whose edits win.
 * - A record change calls reset(): pending events are dropped and a response that arrives
 *   for an older record generation is ignored. Sequence numbers and generations stay local
 *   and are never sent.
 * - A failed event shows a non-blocking notice and counts as processed, so it never blocks
 *   leaving or saving; drain() never rejects.
 *
 * The queue raises no event on its own, so loading or replacing a record raises none.
 */
import type { ApiClient, ApiResult } from '../api/client';
import { describeError, errorMessages, isAbortError, networkError, type ApiError } from '../api/errors';
import type {
  Diagnostic,
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

/** The record editor as the queue sees it. */
export interface FieldEventQueueHost {
  /** The editor's current state, read synchronously, including edits not yet rendered. */
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
}

export interface FieldEventQueue {
  /** Queues an event raised on `fieldName`; the request is sent when every earlier event is done. */
  raise(kind: FieldEventKind, fieldName: string): void;
  /** Records that the user, not check code, changed the field's value. */
  noteUserEdit(fieldName: string): void;
  /** Settles once no event is pending or in flight, or at the next reset(); never rejects. */
  drain(): Promise<void>;
  /** Starts a new record generation: drops pending events and ignores the in-flight one. */
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
  /** U(E): a shallow copy of the editor's values when the event was raised. */
  readonly captured: Values;
  readonly generation: number;
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
  const fieldNames = dataFieldNames(definition);
  const labels = fieldLabels(definition);

  let seq = 0;
  let generation = 0;
  const lastEditSeq = new Map<string, number>();
  const pending: QueuedEvent[] = [];
  let inFlight: QueuedEvent | null = null;
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

  const notifyFailure = (event: QueuedEvent, error: ApiError): void => {
    const label = labels.get(event.fieldName) ?? event.fieldName;
    host.onNotice(`Check code could not run for ${label}. ${failureReason(error)}`);
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

  const dispatch = async (event: QueuedEvent): Promise<void> => {
    const request: FieldEventRequest = { event: event.kind, fieldName: event.fieldName, values: payloadValues(event) };
    let result: ApiResult<FieldEventResponse>;
    try {
      result = await client.raiseFieldEvent(viewId, request);
    } catch (e) {
      // The client settles every failure except an abort into a result; anything else
      // thrown still means no response was received.
      if (!isAbortError(e) && isCurrent(event)) {
        notifyFailure(event, networkError());
      }
      return;
    }

    // A response for an earlier record generation belongs to a record no longer on screen.
    if (!isCurrent(event)) {
      return;
    }
    if (!result.ok) {
      notifyFailure(event, result.error);
      return;
    }
    apply(event, result.data);
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

    inFlight = event;
    // A host callback that throws still advances the queue, and its rejection surfaces
    // as an unhandled error instead of being swallowed.
    void dispatch(event).finally(() => {
      // After a reset the in-flight slot belongs to the new generation.
      if (inFlight === event) {
        inFlight = null;
        sendNext();
      }
    });
  };

  return {
    raise(kind, fieldName) {
      seq += 1;
      pending.push({ kind, fieldName, seq, captured: { ...host.getSnapshot().values }, generation });
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
      // Detached rather than awaited: a stale request that never answers must not hold
      // back the new record's events.
      inFlight = null;
      lastEditSeq.clear();
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
